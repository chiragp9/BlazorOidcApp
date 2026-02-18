# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Commands

```bash
# Restore dependencies
dotnet restore

# Build entire solution
dotnet build

# Run Blazor Server (HTTPS: 7169, HTTP: 5022) — https is the default profile
dotnet run --project "BlazorOidcApp/BlazorOidcApp/BlazorOidcApp.csproj"

# Run API Server (HTTPS: 7124, HTTP: 5129) — https is the default profile
dotnet run --project "BlazorOidcApp.Api/BlazorOidcApp.Api.csproj"
```

Both servers must run simultaneously for full functionality. There are no automated tests in this project.

## Architecture

This is a **Backend-for-Frontend (BFF) pattern** implementation demonstrating secure OIDC authentication with hybrid Blazor rendering modes.

### Three Projects

**BlazorOidcApp (Server)** — The BFF layer. Handles the OIDC Authorization Code flow, stores tokens in encrypted HttpOnly server-side session cookies, and exposes two BFF proxy endpoints:
- `GET /bff/token` — Returns the access token to WASM clients (token stays in JS memory, never localStorage)
- `GET /bff/user` — Returns authenticated user claims

**BlazorOidcApp.Client (WASM)** — Client-side Blazor WebAssembly. `TokenService` fetches tokens from `/bff/token` and caches them in memory. `ApiClient` attaches Bearer tokens to API calls. These are registered in the WASM `Program.cs` and used by WASM-rendered pages.

**BlazorOidcApp.Api (Resource Server)** — Protected REST API at port 7124. Validates JWT Bearer tokens against the OIDC provider's JWKS. Exposes `/api/weather` and `/api/me`. CORS is configured to allow only the Blazor origin.

### Rendering Modes

The server project uses four Blazor render modes, each demonstrated by a page:
- **Static SSR** (`ServerPage.razor`, `Weather.razor`, `Dashboard.razor`, `GdpPage.razor`) — Renders on server, no interactivity. Accesses `HttpContext` directly for tokens and user info. `Dashboard.razor` shows user info (name/email/subject/token expiry) and links to all render mode pages. `GdpPage.razor` shows a hardcoded top-10 countries by GDP table (no API call).
- **Interactive Server** (`InteractiveServerPage.razor`) — Server-side C# over SignalR. Uses `BearerTokenHandler` (a `DelegatingHandler`) to inject Bearer tokens into server-side `HttpClient` calls to the API.
- **Interactive WebAssembly** (`WasmPage.razor`, `Counter.razor`) — Runs in browser. Fetches token from BFF, calls API directly from the browser.
- **Interactive Auto** (`AutoPage.razor`) — First visit uses Interactive Server (instant startup); subsequent visits switch to WebAssembly once the WASM bundle is cached. Uses `IWeatherApiService` abstraction with two DI-registered implementations: `ServerWeatherApiService` (server DI) and `WasmWeatherApiService` (WASM DI).

### Authentication Flow

1. User initiates login → OIDC Authorization Code flow with external provider
2. Tokens stored in encrypted server-side cookie (HttpOnly, Secure, SameSite=Lax, 1-hour expiry)
3. Token auto-refresh with 2-minute buffer before expiry; 401 responses invalidate WASM token cache
4. WASM pages call `/bff/token` to get token transiently in JS heap for API requests

### Configuration

Three `appsettings.json` files require OIDC provider details:

| File | Key Settings |
|------|-------------|
| `BlazorOidcApp/appsettings.json` | `Oidc.Authority`, `Oidc.ClientId`, `Oidc.ClientSecret`, `Api.BaseUrl` |
| `BlazorOidcApp.Api/appsettings.json` | `Oidc.Authority`, `Oidc.ApiAudience`, `Cors.BlazorOrigin` |
| `BlazorOidcApp.Client/wwwroot/appsettings.json` | `Api.BaseUrl` |

### Tech Stack

- .NET 10.0, ASP.NET Core, Blazor (Server + WebAssembly)
- `Microsoft.AspNetCore.Authentication.OpenIdConnect` (OIDC)
- `Microsoft.AspNetCore.Authentication.Cookies` (session)
- `Microsoft.AspNetCore.Authentication.JwtBearer` (API token validation)
- Bootstrap 5 (static files in `wwwroot/lib/`)
