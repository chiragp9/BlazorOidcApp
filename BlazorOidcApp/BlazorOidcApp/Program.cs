using BlazorOidcApp;
using BlazorOidcApp.Components;
using BlazorOidcApp.Client.Services;
using BlazorOidcApp.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using System.Net.Http.Headers;
using System.Security.Claims;

var builder = WebApplication.CreateBuilder(args);

// ── Razor / Blazor ──────────────────────────────────────────────────────────
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents()
    .AddInteractiveWebAssemblyComponents();

builder.Services.AddCascadingAuthenticationState();

// ── HttpContextAccessor (needed by BearerTokenHandler) ─────────────────────
builder.Services.AddHttpContextAccessor();

// ── Named HttpClient: server → API (token auto-attached) ───────────────────
builder.Services.AddTransient<BearerTokenHandler>();
builder.Services.AddHttpClient("Api", client =>
    client.BaseAddress = new Uri(builder.Configuration["Api:BaseUrl"]!))
    .AddHttpMessageHandler<BearerTokenHandler>();

// ── OIDC Authentication ─────────────────────────────────────────────────────
builder.Services.AddAuthentication(options =>
{
    options.DefaultScheme          = CookieAuthenticationDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
})
.AddCookie(options =>
{
    options.Cookie.HttpOnly     = true;
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.Cookie.SameSite     = SameSiteMode.Lax;
    options.ExpireTimeSpan      = TimeSpan.FromHours(1);
})
.AddOpenIdConnect(options =>
{
    options.Authority    = builder.Configuration["Oidc:Authority"];
    options.ClientId     = builder.Configuration["Oidc:ClientId"];
    options.ClientSecret = builder.Configuration["Oidc:ClientSecret"];

    options.ResponseType = OpenIdConnectResponseType.Code;
    options.SaveTokens   = true;   // persists tokens inside the encrypted cookie
    options.GetClaimsFromUserInfoEndpoint = true;

    options.Scope.Clear();
    options.Scope.Add("openid");
    options.Scope.Add("profile");
    options.Scope.Add("email");
    options.Scope.Add("offline_access"); // refresh token
    options.Scope.Add("api");            // API audience scope

    // Map standard OIDC claims
    options.TokenValidationParameters.NameClaimType = "name";
    options.TokenValidationParameters.RoleClaimType = "role";
});

builder.Services.AddAuthorization();

// ── Auto render mode: server-side implementation of shared API service ───────
builder.Services.AddScoped<IWeatherApiService, ServerWeatherApiService>();

var app = builder.Build();

// ── Middleware pipeline ─────────────────────────────────────────────────────
if (app.Environment.IsDevelopment())
{
    app.UseWebAssemblyDebugging();
}
else
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

// ── Auth endpoints ──────────────────────────────────────────────────────────
app.MapGet("/auth/login", (string? returnUrl) =>
    Results.Challenge(
        new Microsoft.AspNetCore.Authentication.AuthenticationProperties
        {
            RedirectUri = returnUrl ?? "/"
        },
        [OpenIdConnectDefaults.AuthenticationScheme]));

app.MapPost("/auth/logout", async (HttpContext ctx) =>
{
    await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    await ctx.SignOutAsync(OpenIdConnectDefaults.AuthenticationScheme,
        new Microsoft.AspNetCore.Authentication.AuthenticationProperties { RedirectUri = "/" });
}).RequireAuthorization();

// ── BFF Token Endpoint ──────────────────────────────────────────────────────
// WASM pages call this (with the session cookie) to get an access token.
// The token stays in WASM heap memory — never written to localStorage.
app.MapGet("/bff/token", async (HttpContext ctx) =>
{
    var token = await ctx.GetTokenAsync("access_token");
    if (token is null) return Results.Unauthorized();

    var expiresAt = await ctx.GetTokenAsync("expires_at");
    if (DateTimeOffset.TryParse(expiresAt, out var expiry) && expiry < DateTimeOffset.UtcNow)
        return Results.Unauthorized();

    return Results.Ok(new { access_token = token, expires_at = expiresAt });
}).RequireAuthorization();

// ── BFF User Info Endpoint ──────────────────────────────────────────────────
app.MapGet("/bff/user", (ClaimsPrincipal user) =>
{
    if (user.Identity?.IsAuthenticated != true)
        return Results.Unauthorized();

    return Results.Ok(user.Claims.Select(c => new { c.Type, c.Value }));
}).RequireAuthorization();

// ── Static assets + Razor components ───────────────────────────────────────
app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddInteractiveWebAssemblyRenderMode()
    .AddAdditionalAssemblies(typeof(BlazorOidcApp.Client._Imports).Assembly);

app.Run();
