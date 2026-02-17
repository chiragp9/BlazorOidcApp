using Microsoft.AspNetCore.Authentication.JwtBearer;
using System.Security.Claims;

var builder = WebApplication.CreateBuilder(args);

// ── JWT Bearer Authentication ────────────────────────────────────────────────
// The API is a resource server — it only validates Bearer tokens issued by
// the OIDC provider. It never performs redirects or cookie handling.
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        // The OIDC provider's base URL — the middleware fetches the JWKS automatically
        options.Authority = builder.Configuration["Oidc:Authority"];

        // Must match the 'aud' claim in the access token
        options.Audience  = builder.Configuration["Oidc:ApiAudience"];

        options.TokenValidationParameters = new()
        {
            ValidateIssuer           = true,
            ValidateAudience         = true,
            ValidateLifetime         = true,
            ValidateIssuerSigningKey = true,
            NameClaimType            = "name",
            RoleClaimType            = "role",
        };
    });

builder.Services.AddAuthorization();

// ── CORS ─────────────────────────────────────────────────────────────────────
// Allows both the Blazor server and WASM origin to call the API.
// In production, tighten to exact origins.
builder.Services.AddCors(options =>
{
    options.AddPolicy("BlazorClient", policy =>
    {
        policy.WithOrigins(
                builder.Configuration["Cors:BlazorOrigin"] ?? "https://localhost:7169")
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials();
    });
});

var app = builder.Build();

app.UseHttpsRedirection();
app.UseCors("BlazorClient");
app.UseAuthentication();
app.UseAuthorization();

// ── Weather endpoint (protected) ─────────────────────────────────────────────
var summaries = new[]
{
    "Freezing", "Bracing", "Chilly", "Cool", "Mild", "Warm", "Balmy", "Hot", "Sweltering", "Scorching"
};

app.MapGet("/api/weather", (ClaimsPrincipal user) =>
{
    var forecasts = Enumerable.Range(1, 5).Select(index => new WeatherForecast(
        Date:         DateOnly.FromDateTime(DateTime.Now.AddDays(index)),
        TemperatureC: Random.Shared.Next(-20, 55),
        Summary:      summaries[Random.Shared.Next(summaries.Length)]
    )).ToArray();

    return Results.Ok(forecasts);
}).RequireAuthorization();

// ── User info endpoint (protected) ───────────────────────────────────────────
app.MapGet("/api/me", (ClaimsPrincipal user) =>
{
    return Results.Ok(new
    {
        name     = user.Identity?.Name,
        email    = user.FindFirst("email")?.Value,
        subject  = user.FindFirst("sub")?.Value,
        claims   = user.Claims.Select(c => new { c.Type, c.Value })
    });
}).RequireAuthorization();

app.Run();

record WeatherForecast(DateOnly Date, int TemperatureC, string? Summary)
{
    public int TemperatureF => 32 + (int)(TemperatureC / 0.5556);
}
