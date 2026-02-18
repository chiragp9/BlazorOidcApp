# BlazorOidcApp — Complete Code Walkthrough

This document walks through every important file in the project with plain-English explanations so anyone can understand what each line does and why.

---

## Table of Contents

1. [BlazorOidcApp — Server Project](#1-blazoroidcapp--server-project)
   - [Program.cs](#programcs)
   - [BearerTokenHandler.cs](#bearertokenhandlercs)
   - [ServerPage.razor](#serverpagerazor)
   - [InteractiveServerPage.razor](#interactiveserverpagerazor)
   - [Weather.razor](#weatherrazor)
   - [ServerWeatherApiService.cs](#serverweatherapiservicecs)
2. [BlazorOidcApp.Client — WASM Project](#2-blazoroidcappclient--wasm-project)
   - [Program.cs](#programcs-1)
   - [BffAuthenticationStateProvider.cs](#bffauthenticationstateprovidercs)
   - [TokenService.cs](#tokenservicecs)
   - [ApiClient.cs](#apiclientcs)
   - [IWeatherApiService.cs](#iweatherapiservicecs)
   - [WasmWeatherApiService.cs](#wasmweatherapiservicecs)
   - [WasmPage.razor](#wasmpagerazor)
   - [AutoPage.razor](#autopagerazor)
3. [BlazorOidcApp.Api — Resource API Project](#3-blazoroidcappapi--resource-api-project)
   - [Program.cs](#programcs-2)

---

## 1. BlazorOidcApp — Server Project

This is the main web server. It handles login, stores tokens securely, serves web pages, and acts as the "middle man" (BFF) between the browser and the API.

---

### Program.cs

This is the startup file — where the whole application is configured before it starts running.

```csharp
// ── Step 1: Add Blazor page support ─────────────────────────────────────────
// This tells the app to support Razor pages (the .razor files).
// - AddInteractiveServerComponents: enables pages that run C# on the server via WebSocket
// - AddInteractiveWebAssemblyComponents: enables pages that run C# in the browser
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents()
    .AddInteractiveWebAssemblyComponents();

// This makes authentication state (logged-in user info) automatically available
// to all pages and components without having to pass it manually.
builder.Services.AddCascadingAuthenticationState();

// ── Step 2: Allow access to the current HTTP request ────────────────────────
// IHttpContextAccessor lets us read the current user's session/cookie from
// anywhere in the app (e.g., inside BearerTokenHandler).
builder.Services.AddHttpContextAccessor();

// ── Step 3: Set up an HTTP client that calls the API ────────────────────────
// This creates a named HTTP client called "Api".
// Every time we use this client to call the API, it will automatically
// attach the user's access token as a "Bearer" header.
// BearerTokenHandler (explained below) does the actual token attachment.
builder.Services.AddTransient<BearerTokenHandler>();
builder.Services.AddHttpClient("Api", client =>
    client.BaseAddress = new Uri(builder.Configuration["Api:BaseUrl"]!))
    .AddHttpMessageHandler<BearerTokenHandler>();

// ── Step 4: Set up login/logout (OIDC Authentication) ──────────────────────
// We use two types of authentication working together:
// 1. Cookies — to remember the user is logged in (stored in browser)
// 2. OpenID Connect (OIDC) — to actually verify identity via an external provider

builder.Services.AddAuthentication(options =>
{
    // By default, use cookies to check if user is logged in
    options.DefaultScheme          = CookieAuthenticationDefaults.AuthenticationScheme;
    // When login is required, redirect to the OIDC provider (e.g., Duende)
    options.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
})
.AddCookie(options =>
{
    // HttpOnly: JavaScript in the browser CANNOT read this cookie.
    // This prevents hackers from stealing the token via JavaScript attacks (XSS).
    options.Cookie.HttpOnly     = true;

    // Secure: Cookie only sent over HTTPS, never plain HTTP.
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;

    // SameSite=Lax: Cookie won't be sent when another website links to yours.
    // This prevents Cross-Site Request Forgery (CSRF) attacks.
    options.Cookie.SameSite     = SameSiteMode.Lax;

    // The login session expires after 1 hour of inactivity.
    options.ExpireTimeSpan      = TimeSpan.FromHours(1);
})
.AddOpenIdConnect(options =>
{
    // The URL of the identity provider (e.g., Duende demo server)
    options.Authority    = builder.Configuration["Oidc:Authority"];
    // Who we are (our app's identifier registered with the provider)
    options.ClientId     = builder.Configuration["Oidc:ClientId"];
    // Our app's secret password (kept server-side, never sent to browser)
    options.ClientSecret = builder.Configuration["Oidc:ClientSecret"];

    // "Code" flow: we get a short-lived "code" from the provider,
    // then exchange it server-side for real tokens. Safer than getting
    // tokens directly in the browser URL.
    options.ResponseType = OpenIdConnectResponseType.Code;

    // SaveTokens: store the access_token and refresh_token inside the encrypted cookie.
    // This means tokens never touch the browser directly.
    options.SaveTokens   = true;

    // After login, fetch the user's profile info (name, email, etc.)
    options.GetClaimsFromUserInfoEndpoint = true;

    // Request these pieces of information from the identity provider:
    options.Scope.Clear();
    options.Scope.Add("openid");        // Required — proves who the user is
    options.Scope.Add("profile");       // User's name, picture, etc.
    options.Scope.Add("email");         // User's email address
    options.Scope.Add("offline_access");// Allows getting a refresh token (to stay logged in)
    options.Scope.Add("api");           // Permission to call our API
});

// ── Step 5: Add authorization rules ─────────────────────────────────────────
// Allows pages to use [Authorize] to require the user to be logged in.
builder.Services.AddAuthorization();
```

```csharp
// ── Step 6: Configure the request pipeline ───────────────────────────────────
// Middleware runs in order — each request passes through all of these.

app.UseHttpsRedirection();  // Redirect any HTTP requests to HTTPS
app.UseAuthentication();    // Check if the user has a valid login cookie
app.UseAuthorization();     // Check if the user has permission for this page
app.UseAntiforgery();       // Protect forms against Cross-Site Request Forgery attacks
```

```csharp
// ── Step 7: Login and logout endpoints ──────────────────────────────────────

// When user clicks "Login", they come here.
// This tells the browser to go to the OIDC provider's login page.
app.MapGet("/auth/login", (string? returnUrl) =>
    Results.Challenge(
        new AuthenticationProperties { RedirectUri = returnUrl ?? "/" },
        [OpenIdConnectDefaults.AuthenticationScheme]));

// When user clicks "Logout", they POST here.
// We clear both the local cookie AND log them out at the OIDC provider.
app.MapPost("/auth/logout", async (HttpContext ctx) =>
{
    await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    await ctx.SignOutAsync(OpenIdConnectDefaults.AuthenticationScheme,
        new AuthenticationProperties { RedirectUri = "/" });
}).RequireAuthorization(); // Only logged-in users can call this
```

```csharp
// ── Step 8: BFF Endpoints — the bridge between WASM and tokens ──────────────

// The WASM app (running in the browser) needs the access token to call the API.
// But the token is stored in the HttpOnly cookie — JavaScript can't read it!
// So we provide this endpoint: the browser (with its cookie) calls this,
// and we return the token in the response body.
//
// This is safe because:
// - RequireAuthorization() ensures only logged-in users can call it
// - SameSite=Lax prevents other websites from triggering this call
// - The token is kept in WASM memory only, never written to localStorage
app.MapGet("/bff/token", async (HttpContext ctx) =>
{
    // Read the access token from the encrypted session cookie
    var token = await ctx.GetTokenAsync("access_token");
    if (token is null) return Results.Unauthorized(); // Not logged in

    // Check if the token has expired
    var expiresAt = await ctx.GetTokenAsync("expires_at");
    if (DateTimeOffset.TryParse(expiresAt, out var expiry) && expiry < DateTimeOffset.UtcNow)
        return Results.Unauthorized(); // Token expired

    // Return the token as JSON
    return Results.Ok(new { access_token = token, expires_at = expiresAt });
}).RequireAuthorization();

// Returns the logged-in user's identity information (claims).
// Used by the WASM app to know who is logged in.
app.MapGet("/bff/user", (ClaimsPrincipal user) =>
{
    if (user.Identity?.IsAuthenticated != true)
        return Results.Unauthorized();

    // Return all claims as a list of {type, value} pairs
    return Results.Ok(user.Claims.Select(c => new { c.Type, c.Value }));
}).RequireAuthorization();
```

---

### BearerTokenHandler.cs

This is a "pipeline interceptor" for the server-side HTTP client that calls the API. Think of it like a toll booth — every API request passes through it, and it automatically attaches the user's token.

```csharp
// This class intercepts every outgoing HTTP request made by the "Api" HttpClient.
// Its job: read the access token from the current user's session cookie
// and attach it to the request as an Authorization header.
//
// Why do we need this?
// When a server-side page calls the API, it needs to prove who the user is.
// The access token is locked in the encrypted cookie.
// This handler reads it and adds it to every API call automatically.
// Pages don't need to handle tokens themselves.
public class BearerTokenHandler(IHttpContextAccessor httpContextAccessor) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var httpContext = httpContextAccessor.HttpContext;
        if (httpContext is not null)
        {
            // Read the access token from the encrypted session cookie
            var token = await httpContext.GetTokenAsync("access_token");
            if (token is not null)
                // Add it to the outgoing request:
                // Authorization: Bearer eyJhbGci...
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        // Continue sending the request (with the token now attached)
        return await base.SendAsync(request, cancellationToken);
    }
}
```

---

### ServerPage.razor

A static SSR page. It renders once on the server and sends finished HTML to the browser. No real-time interactivity.

```razor
@page "/server-page"
@* No @rendermode means Static SSR — renders on server once, no WebSocket *@
@attribute [Authorize]  @* User must be logged in to see this page *@

@inject IHttpContextAccessor HttpContextAccessor  @* Access the current HTTP request/session *@
@inject IHttpClientFactory   HttpClientFactory    @* Create the named "Api" HTTP client *@
```

```csharp
protected override async Task OnInitializedAsync()
{
    // Get the current HTTP request context
    var ctx = HttpContextAccessor.HttpContext!;

    // Read user info directly from the login cookie claims
    // (In SSR, we have full access to HttpContext)
    username     = ctx.User.Identity?.Name;
    email        = ctx.User.FindFirst("email")?.Value;

    // Read the access token directly from the encrypted session cookie
    var token    = await ctx.GetTokenAsync("access_token");

    // Show only the first 40 characters for display (tokens are very long)
    tokenPreview = token is not null ? token[..Math.Min(40, token.Length)] + "..." : "none";
    expiresAt    = await ctx.GetTokenAsync("expires_at");

    // Call the API using the named "Api" HttpClient.
    // BearerTokenHandler automatically attaches the token — we don't do it manually.
    var client = HttpClientFactory.CreateClient("Api");
    var data   = await client.GetFromJsonAsync<WeatherForecast[]>("/api/weather");
    apiResult  = $"API returned {data?.Length ?? 0} weather forecast(s).";
}
```

---

### InteractiveServerPage.razor

An interactive page that runs C# on the server via a WebSocket (SignalR). Button clicks happen instantly without full page reloads.

```razor
@page "/interactive-server"
@rendermode InteractiveServer  @* Keep a live WebSocket connection to the server *@
@attribute [Authorize]

@inject IHttpClientFactory HttpClientFactory
```

```razor
@* This button calls the CallApi() C# method when clicked.
   The click event travels from the browser over WebSocket to the server,
   C# runs, and the updated HTML is sent back over WebSocket.
   The page does NOT reload. *@
<button class="btn btn-primary" @onclick="CallApi" disabled="@isLoading">
    @(isLoading ? "Loading..." : "Call API")
</button>
```

```csharp
private async Task CallApi()
{
    isLoading = true;   // Disable button while loading (updates UI instantly via WebSocket)
    forecasts = null;

    // Create the "Api" HTTP client — BearerTokenHandler automatically adds the token.
    // Note: Unlike SSR, we CANNOT use HttpContext here because the connection is WebSocket.
    // BearerTokenHandler handles getting the token from the session for us.
    var client = HttpClientFactory.CreateClient("Api");
    forecasts  = await client.GetFromJsonAsync<WeatherForecast[]>("/api/weather");

    isLoading = false;  // Re-enable button — UI updates instantly via WebSocket
}
```

---

### Weather.razor

A static SSR page with **streaming** — the page is sent to the browser in chunks. The browser shows "Loading..." immediately, then the table appears once the API responds.

```razor
@page "/weather"
@attribute [StreamRendering]  @* Send the page to the browser in chunks (streaming) *@
@attribute [Authorize]

@inject IHttpClientFactory HttpClientFactory
```

```razor
@* While forecasts is null, the API call is still in progress.
   StreamRendering sends this "Loading..." to the browser immediately,
   then sends the table once the data arrives. *@
@if (forecasts == null)
{
    <p><em>Loading from API...</em></p>
}
else
{
    @* Show the weather table once data is loaded *@
}
```

```csharp
protected override async Task OnInitializedAsync()
{
    // Call the API. While we await this, the browser already shows "Loading..."
    // because StreamRendering flushed the initial HTML immediately.
    var client = HttpClientFactory.CreateClient("Api");
    forecasts  = await client.GetFromJsonAsync<WeatherForecast[]>("/api/weather");
    // Once this line completes, Blazor sends the updated HTML (the table) to the browser.
}
```

---

### ServerWeatherApiService.cs

The server-side implementation of `IWeatherApiService`, used by `AutoPage.razor` when it runs as Interactive Server. It delegates to the same named `"Api"` HttpClient (with `BearerTokenHandler`) that other server pages use.

```csharp
// Implements IWeatherApiService so AutoPage can inject it on the server side.
// IHttpClientFactory is available in server DI — we use the "Api" named client
// which already has BearerTokenHandler configured. Token handling is automatic.
public class ServerWeatherApiService(IHttpClientFactory httpClientFactory) : IWeatherApiService
{
    public async Task<WeatherForecast[]?> GetWeatherAsync()
    {
        var client = httpClientFactory.CreateClient("Api");
        // BearerTokenHandler intercepts this call and adds Authorization: Bearer <token>
        return await client.GetFromJsonAsync<WeatherForecast[]>("/api/weather");
    }
}
```

**Registered in** `BlazorOidcApp/Program.cs`:
```csharp
builder.Services.AddScoped<IWeatherApiService, ServerWeatherApiService>();
```

---

## 2. BlazorOidcApp.Client — WASM Project

This project runs entirely inside the user's browser as WebAssembly (.NET compiled for the browser). It cannot access the server's session directly — it uses the BFF endpoints to bridge the gap.

---

### Program.cs

The startup file for the WASM app running in the browser.

```csharp
// Enable authorization checks in WASM (e.g., [Authorize] on pages)
builder.Services.AddAuthorizationCore();

// Make the auth state (who is logged in) available to all WASM components
builder.Services.AddCascadingAuthenticationState();

// Register our custom auth state provider (explained below).
// This tells WASM how to find out if the user is logged in.
builder.Services.AddScoped<AuthenticationStateProvider, BffAuthenticationStateProvider>();

// Register a general-purpose HTTP client for calling BFF endpoints (/bff/token, /bff/user).
// BaseAddress is the Blazor server's URL (same origin — so the session cookie is sent automatically).
builder.Services.AddScoped(sp =>
    new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });

// Register a separate HTTP client specifically for ApiClient.
// This one points to the API server (different port/origin).
builder.Services.AddHttpClient<ApiClient>(client =>
    client.BaseAddress = new Uri(builder.Configuration["Api:BaseUrl"]!));

// Register TokenService so it can be injected into ApiClient
builder.Services.AddScoped<TokenService>();

// Register the WASM-side implementation of the shared Auto render mode service.
// When AutoPage runs in WASM, this implementation is injected.
// (ServerWeatherApiService is registered in the server's Program.cs for the server phase.)
builder.Services.AddScoped<IWeatherApiService, WasmWeatherApiService>();
```

---

### BffAuthenticationStateProvider.cs

This class tells the WASM app "who is currently logged in?" by asking the BFF server. Without this, the WASM runtime would not know whether the user is authenticated.

```csharp
// AuthenticationStateProvider is an abstract class from Blazor.
// We extend it to teach WASM how to get the user's identity.
public class BffAuthenticationStateProvider(HttpClient httpClient) : AuthenticationStateProvider
{
    // A reusable "anonymous user" object (used when nobody is logged in)
    private static readonly AuthenticationState Anonymous =
        new(new ClaimsPrincipal(new ClaimsIdentity()));

    // Blazor calls this method automatically to find out who is logged in.
    public override async Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        try
        {
            // Call GET /bff/user on the server.
            // The browser automatically sends the session cookie (same-origin request).
            // The server reads the cookie, checks if user is logged in,
            // and returns their claims (name, email, roles, etc.) as JSON.
            var claims = await httpClient.GetFromJsonAsync<ClaimRecord[]>("/bff/user");

            // If the server returns nothing, the user is not logged in
            if (claims is null || claims.Length == 0)
                return Anonymous;

            // Build a user identity from the claims returned by the server
            var identity = new ClaimsIdentity(
                claims.Select(c => new Claim(c.Type, c.Value)),
                authenticationType: "bff");  // "bff" marks this as a BFF-authenticated user

            // Return the authenticated user
            return new AuthenticationState(new ClaimsPrincipal(identity));
        }
        catch
        {
            // If the call fails (network error, server down), treat as anonymous
            return Anonymous;
        }
    }

    // A simple record to hold each claim from the /bff/user response
    private record ClaimRecord(string Type, string Value);
}
```

---

### TokenService.cs

This service fetches the access token from the BFF and caches it in memory. The WASM app needs this token to call the API directly from the browser.

```csharp
public class TokenService(HttpClient httpClient)
{
    // Store the token in memory (not in localStorage — safer against XSS attacks)
    private string? _cachedToken;
    private DateTimeOffset _tokenExpiry = DateTimeOffset.MinValue;

    public async Task<string?> GetTokenAsync()
    {
        // If we already have a token that's valid for more than 2 more minutes, reuse it.
        // The 2-minute buffer prevents using a token that might expire mid-request.
        if (_cachedToken is not null && _tokenExpiry > DateTimeOffset.UtcNow.AddMinutes(2))
            return _cachedToken;

        try
        {
            // Call GET /bff/token on the server.
            // The browser sends the session cookie automatically.
            // The server reads the access token from the encrypted cookie and returns it.
            var response = await httpClient.GetFromJsonAsync<TokenResponse>("/bff/token");

            if (response?.AccessToken is null) return null; // Server said no token available

            // Cache the token in memory for future calls
            _cachedToken = response.AccessToken;
            _tokenExpiry = DateTimeOffset.TryParse(response.ExpiresAt, out var exp)
                ? exp
                : DateTimeOffset.UtcNow.AddMinutes(55); // Default 55-min expiry if not provided

            return _cachedToken;
        }
        catch
        {
            // If the call fails, return null (caller will handle the error)
            return null;
        }
    }

    // Call this when the API returns a 401 Unauthorized response.
    // Clears the cache so the next call fetches a fresh token.
    public void InvalidateToken()
    {
        _cachedToken = null;
        _tokenExpiry = DateTimeOffset.MinValue;
    }

    // Maps the JSON response from /bff/token to C# properties.
    // [JsonPropertyName] tells the deserializer that the JSON uses snake_case
    // (e.g., "access_token") while C# uses PascalCase (AccessToken).
    private record TokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("expires_at")]  string? ExpiresAt);
}
```

---

### ApiClient.cs

A helper that makes it easy to call the API from WASM. It gets the token from `TokenService` and attaches it to every request automatically.

```csharp
// HttpClient here points to the API server (e.g., https://localhost:7124)
// TokenService provides the access token fetched from /bff/token
public class ApiClient(HttpClient httpClient, TokenService tokenService)
{
    public async Task<T?> GetAsync<T>(string path)
    {
        // Step 1: Get the access token (from cache or from /bff/token)
        var token = await tokenService.GetTokenAsync()
            ?? throw new UnauthorizedAccessException("No access token available. Please log in.");

        // Step 2: Build the HTTP request
        using var request = new HttpRequestMessage(HttpMethod.Get, path);

        // Step 3: Attach the token as a Bearer header
        // This tells the API "I am this authenticated user"
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // Step 4: Send the request directly from browser to API server
        var response = await httpClient.SendAsync(request);

        // Step 5: Handle expired/invalid token
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            // Clear the cached token so the next call will fetch a fresh one
            tokenService.InvalidateToken();
            throw new UnauthorizedAccessException("API returned 401 Unauthorized. Please log in again.");
        }

        // Step 6: Throw if any other error occurred (500, 404, etc.)
        response.EnsureSuccessStatusCode();

        // Step 7: Deserialize the JSON response into the requested type T
        return await response.Content.ReadFromJsonAsync<T>();
    }
}
```

---

### IWeatherApiService.cs

A shared interface (defined in the WASM project, so both the server and WASM can use it) that abstracts away _how_ weather data is fetched. This is the key to making `InteractiveAuto` work: the page doesn't care whether it's running on the server or in the browser — it just calls the interface method.

```csharp
// The contract: one method that returns weather data.
// Both the server-side and WASM-side implementations must fulfil this.
public interface IWeatherApiService
{
    Task<WeatherForecast[]?> GetWeatherAsync();
}

// Shared model used by both implementations and AutoPage.razor.
// Defined here so both the server and WASM projects share the same type.
public record WeatherForecast(DateOnly Date, int TemperatureC, string? Summary)
{
    // Computed property — no API change needed, just a formula
    public int TemperatureF => 32 + (int)(TemperatureC / 0.5556);
}
```

**Why define it in the WASM project?**
The server project already references the WASM project (to serve its pages). Defining the interface here means both sides see exactly the same type — no duplication, no mismatch.

---

### WasmWeatherApiService.cs

The WASM-side implementation of `IWeatherApiService`. Delegates to `ApiClient`, which handles token fetching and Bearer header attachment.

```csharp
// Wraps ApiClient in the shared interface.
// When AutoPage runs in WASM (after bundle is cached), this is the implementation injected.
public class WasmWeatherApiService(ApiClient apiClient) : IWeatherApiService
{
    public Task<WeatherForecast[]?> GetWeatherAsync()
        // ApiClient internally: calls /bff/token, caches token, attaches Bearer header,
        // then calls the API directly from the browser.
        => apiClient.GetAsync<WeatherForecast[]>("/api/weather");
}
```

**Registered in** `BlazorOidcApp.Client/Program.cs`:
```csharp
builder.Services.AddScoped<IWeatherApiService, WasmWeatherApiService>();
```

---

### WasmPage.razor

A page that runs as WebAssembly in the browser. It fetches weather data by calling the API directly from the browser (not via the server).

```razor
@page "/wasm-page"

@* prerender: false — disable server-side prerendering.
   Without this, the server would try to render this page during the initial request,
   but ApiClient is only registered in the WASM DI container (not the server's),
   causing a "service not found" error. With prerender disabled, the page only
   renders in the browser after WASM has fully loaded. *@
@rendermode @(new InteractiveWebAssemblyRenderMode(prerender: false))

@attribute [Authorize]  @* Must be logged in (checked by BffAuthenticationStateProvider) *@

@inject ApiClient ApiClient         @* Our helper for calling the API with a Bearer token *@
@inject NavigationManager Navigation @* Used to redirect if needed *@
```

```csharp
private async Task LoadWeather()
{
    isLoading = true;

    // ApiClient internally:
    // 1. Calls /bff/token to get the access token (using session cookie)
    // 2. Attaches it as Authorization: Bearer <token>
    // 3. Calls GET https://localhost:7124/api/weather directly from the browser
    // 4. Returns the deserialized result
    forecasts = await ApiClient.GetAsync<WeatherForecast[]>("/api/weather");

    isLoading = false;
}
```

---

### AutoPage.razor

A page that demonstrates `InteractiveAuto` render mode — the most sophisticated Blazor rendering mode. On a **first visit** (WASM bundle not yet cached), it runs as Interactive Server (instant startup via SignalR). On **subsequent visits** (bundle cached), it runs as Interactive WebAssembly (no server connection needed).

```razor
@page "/auto-page"

@* InteractiveAuto: Blazor decides at runtime which interactive mode to use.
   - Server-side (first visit): runs in the server's DI context via SignalR.
     ServerWeatherApiService is injected — reads token via BearerTokenHandler.
   - WASM (subsequent visits): runs in the browser's DI context.
     WasmWeatherApiService is injected — calls /bff/token via ApiClient. *@
@rendermode InteractiveAuto

@attribute [Authorize]

@* IWeatherApiService is the shared abstraction.
   The correct implementation is injected automatically by DI
   depending on whether we're on the server or in the browser. *@
@inject IWeatherApiService WeatherApiService
```

```razor
@* OperatingSystem.IsBrowser() is a .NET runtime API.
   Returns true when the code is running inside WebAssembly (browser),
   false when running on the server (Interactive Server phase).
   We use it to update the badge colour and label in real time. *@
<span class="badge @(OperatingSystem.IsBrowser() ? "bg-success" : "bg-primary")">
    @(OperatingSystem.IsBrowser() ? "WebAssembly" : "Server") Interactive
</span>
```

```csharp
private async Task LoadWeather()
{
    isLoading = true;

    // This single line works in BOTH runtimes:
    // - On server: ServerWeatherApiService.GetWeatherAsync()
    //     → IHttpClientFactory → named "Api" HttpClient → BearerTokenHandler adds token
    // - In WASM: WasmWeatherApiService.GetWeatherAsync()
    //     → ApiClient → TokenService → /bff/token → Bearer header → direct API call
    forecasts = await WeatherApiService.GetWeatherAsync();

    isLoading = false;
}
```

**How to observe the Auto behaviour:**
1. Open `/auto-page` for the first time — badge shows **"Server Interactive"**
2. The browser downloads the WASM bundle silently in the background
3. Navigate to another page and come back — badge now shows **"WebAssembly Interactive"**
4. Hard-refresh the browser (Ctrl+F5) — clears WASM cache, badge goes back to **"Server Interactive"** until bundle re-downloads

---

## 3. BlazorOidcApp.Api — Resource API Project

This is the protected API server. It does not handle login at all — it only accepts requests that carry a valid JWT token and returns data.

---

### Program.cs

```csharp
// ── Step 1: Set up JWT Bearer token validation ───────────────────────────────
// The API validates incoming Bearer tokens without storing any user sessions.
// It trusts tokens issued by the configured OIDC provider.
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        // The OIDC provider's URL. The middleware automatically downloads the
        // public keys (JWKS) from this URL to verify token signatures.
        options.Authority = builder.Configuration["Oidc:Authority"];

        // The "audience" is who the token is intended for.
        // Tokens must have "api" in their audience claim, otherwise they are rejected.
        // This prevents tokens issued for other applications from being used here.
        options.Audience  = builder.Configuration["Oidc:ApiAudience"];

        options.TokenValidationParameters = new()
        {
            ValidateIssuer           = true,  // Token must come from our trusted provider
            ValidateAudience         = true,  // Token must be intended for this API
            ValidateLifetime         = true,  // Token must not be expired
            ValidateIssuerSigningKey = true,  // Token signature must be valid
        };
    });
```

```csharp
// ── Step 2: Set up CORS ──────────────────────────────────────────────────────
// CORS (Cross-Origin Resource Sharing) controls which websites can call this API.
// The WASM app runs at localhost:7169 and calls this API at localhost:7124.
// Browsers block cross-origin requests by default — CORS allows exceptions.
builder.Services.AddCors(options =>
{
    options.AddPolicy("BlazorClient", policy =>
    {
        policy
            .WithOrigins("https://localhost:7169") // Only allow requests from the Blazor app
            .AllowAnyHeader()                      // Allow any HTTP headers
            .AllowAnyMethod()                      // Allow GET, POST, etc.
            .AllowCredentials();                   // Allow the session cookie to be sent
    });
});
```

```csharp
// ── Step 3: API endpoints ─────────────────────────────────────────────────────

// GET /api/weather — returns 5 random weather forecasts
// RequireAuthorization() means the request must have a valid Bearer token
app.MapGet("/api/weather", (ClaimsPrincipal user) =>
{
    // Generate 5 random weather entries
    var forecasts = Enumerable.Range(1, 5).Select(index => new WeatherForecast(
        Date:         DateOnly.FromDateTime(DateTime.Now.AddDays(index)),
        TemperatureC: Random.Shared.Next(-20, 55),
        Summary:      summaries[Random.Shared.Next(summaries.Length)]
    )).ToArray();

    // Return as JSON array: [{date, temperatureC, temperatureF, summary}, ...]
    return Results.Ok(forecasts);
}).RequireAuthorization();

// GET /api/me — returns information about the currently authenticated user
app.MapGet("/api/me", (ClaimsPrincipal user) =>
{
    // ClaimsPrincipal is populated automatically by the JWT middleware
    // from the validated token's claims
    return Results.Ok(new
    {
        name    = user.Identity?.Name,
        email   = user.FindFirst("email")?.Value,
        subject = user.FindFirst("sub")?.Value,   // Unique user ID from the token
        claims  = user.Claims.Select(c => new { c.Type, c.Value })
    });
}).RequireAuthorization();
```

---

## How It All Connects

```
┌─────────────────────────────────────────────────────────────┐
│  BROWSER                                                     │
│                                                              │
│  1. User visits site → BffAuthenticationStateProvider        │
│     calls /bff/user → server returns user claims            │
│     → WASM knows user is logged in                          │
│                                                              │
│  2. User clicks "Fetch Weather" on WASM page:               │
│     TokenService calls /bff/token → server returns token    │
│     ApiClient attaches token → calls API directly           │
│                                                              │
│  3. On SSR / Interactive Server pages:                      │
│     BearerTokenHandler reads token from cookie server-side  │
│     → attaches to HttpClient → calls API                    │
└─────────────────────────────────────────────────────────────┘
```

| What you see | What runs | How token is obtained |
|---|---|---|
| Server Page (SSR) | C# on server, once | Read from `HttpContext` cookie directly |
| Weather (SSR) | C# on server, streaming | Read from `HttpContext` via `BearerTokenHandler` |
| Interactive Server | C# on server, WebSocket | Read from session via `BearerTokenHandler` |
| Auto Page (first visit) | C# on server, WebSocket | `ServerWeatherApiService` → `BearerTokenHandler` |
| Auto Page (after WASM cached) | C# in browser (WASM) | `WasmWeatherApiService` → `ApiClient` → `/bff/token` |
| WASM Page | C# in browser | Fetched from `/bff/token`, cached in memory |
