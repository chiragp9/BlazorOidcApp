# BlazorOidcApp — Architecture & API Call Flow

## Overview

This application uses the **Backend-for-Frontend (BFF) pattern** to securely authenticate users via OpenID Connect (OIDC) and call a protected API from both server-rendered and WebAssembly (WASM) Blazor components.

There are three projects:

| Project | Port | Role |
|---------|------|------|
| `BlazorOidcApp` (Server) | 7169 | BFF — handles OIDC login, stores tokens in session cookie, serves Blazor pages |
| `BlazorOidcApp.Client` (WASM) | — | Runs in browser, fetches token from BFF, calls API directly |
| `BlazorOidcApp.Api` | 7124 | Protected resource server, validates JWT Bearer tokens |

---

## Architecture Diagram

```
┌─────────────────────────────────────────────────────────────────────┐
│                         BROWSER                                      │
│                                                                      │
│  ┌──────────────────────────────────────────────────────────────┐   │
│  │              BlazorOidcApp (Server :7169)                     │   │
│  │                                                              │   │
│  │  ┌─────────────────┐    ┌──────────────────────────────┐    │   │
│  │  │  Static SSR /   │    │  Interactive Server           │    │   │
│  │  │  Server Pages   │    │  (SignalR / WebSocket)        │    │   │
│  │  │                 │    │                              │    │   │
│  │  │  HttpContext ──►│    │  BearerTokenHandler ─────►  │    │   │
│  │  │  (reads token   │    │  (reads token from cookie,  │    │   │
│  │  │   from cookie)  │    │   attaches to HttpClient)   │    │   │
│  │  └────────┬────────┘    └──────────────┬───────────────┘    │   │
│  │           │                            │ Named "Api"        │   │
│  │           │  Server-to-server          │ HttpClient         │   │
│  │           └──────────────┬─────────────┘                    │   │
│  │                          │  Bearer token in HTTP header      │   │
│  │  ┌───────────────────────┼──────────────────────────────┐   │   │
│  │  │  BFF Endpoints        │                              │   │   │
│  │  │  GET /bff/token  ◄────┼── (WASM fetches this)       │   │   │
│  │  │  GET /bff/user        │                              │   │   │
│  │  │  GET /auth/login      │                              │   │   │
│  │  │  POST /auth/logout    │                              │   │   │
│  │  └───────────────────────┼──────────────────────────────┘   │   │
│  │                          │                                    │   │
│  │  Encrypted HttpOnly Cookie (stores access_token)             │   │
│  └──────────────────────────┼───────────────────────────────────┘   │
│                             │                                        │
│  ┌──────────────────────────┼───────────────────────────────────┐   │
│  │  BlazorOidcApp.Client (WASM in Browser)                       │   │
│  │                          │                                    │   │
│  │  TokenService ───────────┘ fetches /bff/token (with cookie)  │   │
│  │       │ caches token in JS heap memory (NOT localStorage)     │   │
│  │       ▼                                                       │   │
│  │  ApiClient ──────────────────────────────────────────────►   │   │
│  │       attaches Bearer token to direct API calls               │   │
│  └───────────────────────────────────────────────────────────┬───┘   │
│                                                              │        │
└──────────────────────────────────────────────────────────────┼────────┘
                                                               │
                                          Bearer token in HTTP header
                                                               │
                                                               ▼
                             ┌─────────────────────────────────────┐
                             │   BlazorOidcApp.Api  (:7124)         │
                             │                                      │
                             │   Validates JWT via OIDC JWKS        │
                             │   GET /api/weather  (requires auth)  │
                             │   GET /api/me       (requires auth)  │
                             └──────────────────┬───────────────────┘
                                                │
                                                ▼
                                        OIDC Provider
                                    (validates signatures,
                                     issues tokens)
```

---

## Authentication Flow (Login)

1. User clicks **Login** → browser navigates to `GET /auth/login`
2. Server initiates **OIDC Authorization Code flow**, redirecting to the OIDC provider
3. User authenticates at the provider
4. Provider redirects back with an authorization code
5. Server exchanges the code for **access token + refresh token**
6. Tokens are stored in an **encrypted, HttpOnly, Secure session cookie** (`SameSite=Lax`, 1-hour expiry)
7. User is now authenticated — all subsequent server requests carry the cookie automatically

---

## How the Server Calls the API

**Key files:** `BearerTokenHandler.cs`, `Program.cs`

Server-side pages (Static SSR and Interactive Server) use a named `HttpClient` called `"Api"` that has `BearerTokenHandler` registered as a pipeline middleware.

### What BearerTokenHandler does

```csharp
// On every outgoing request:
var token = await httpContext.GetTokenAsync("access_token");
request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
```

It reads the access token directly from the current `HttpContext` session (decrypted from the cookie server-side) and attaches it as a `Bearer` header — automatically, without any page code needing to handle tokens.

### Call flow

```
SSR / Interactive Server Page
   → IHttpClientFactory.CreateClient("Api")
   → Page calls httpClient.GetAsync("/api/weather")
   → BearerTokenHandler intercepts the request
       → Reads access_token from HttpContext (server-side cookie)
       → Attaches: Authorization: Bearer <token>
   → Request arrives at BlazorOidcApp.Api
   → API validates JWT and returns data
```

Pages do **zero** token management — the handler is invisible to them.

---

## How the WASM Client Calls the API

**Key files:** `TokenService.cs`, `ApiClient.cs`

WASM runs entirely in the browser. It has **no access to the server session cookie** because it is marked `HttpOnly` — JavaScript (and .NET WASM) cannot read it. A different mechanism is needed.

### Step 1 — TokenService fetches token from BFF

```csharp
// TokenService.GetTokenAsync()
var response = await httpClient.GetFromJsonAsync<TokenResponse>("/bff/token");
_cachedToken = response.AccessToken;
```

The browser sends `GET /bff/token` to the same origin (`:7169`). The browser **automatically** includes the session cookie on this same-origin request. The server validates the session, extracts the token, and returns it in the JSON body.

The token is cached in a C# field (JS heap memory). It is **never written to `localStorage` or `sessionStorage`**.

Cache invalidation rules:
- Token is considered expired 2 minutes before its actual expiry (early refresh buffer)
- On any 401 response from the API, `ApiClient` calls `tokenService.InvalidateToken()` to force a re-fetch

### Step 2 — ApiClient calls the API directly

```csharp
// ApiClient.GetAsync<T>()
var token = await tokenService.GetTokenAsync();
request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
var response = await httpClient.SendAsync(request);  // direct to :7124
```

The WASM client calls `BlazorOidcApp.Api` **directly** (not proxied through the server). The API validates the JWT signature against the OIDC provider's JWKS endpoint.

### Call flow

```
WASM Page
   → ApiClient.GetAsync<WeatherForecast[]>("/api/weather")
   → TokenService.GetTokenAsync()
       → Cache hit? Return cached token
       → Cache miss / expired?
           → fetch GET /bff/token  (cookie auto-sent by browser)
           → Server returns { access_token, expires_at }
           → Cached in C# field (JS heap)
   → ApiClient attaches: Authorization: Bearer <token>
   → Direct HTTP call to BlazorOidcApp.Api:7124
   → API validates JWT → returns data
   → If 401: tokenService.InvalidateToken() → exception thrown
```

---

## Is `/bff/token` Secure?

Yes. This is the standard BFF pattern for WASM/SPA applications. Each threat is addressed:

| Threat | How it's mitigated |
|--------|-------------------|
| Unauthenticated user calls `/bff/token` | Endpoint has `.RequireAuthorization()` — returns 401 with no valid session |
| A different website triggers the call (CSRF) | Cookie is `SameSite=Lax` — browsers block cross-site requests from attaching the cookie |
| XSS script reads the session cookie | Cookie is `HttpOnly` — JavaScript cannot read it |
| Token persisted in browser storage (XSS theft) | Token is held only in C# WASM heap memory — gone when the tab closes |
| Network interception | Cookie and response are transmitted over HTTPS only (`Secure` policy) |
| Expired token returned | Endpoint checks `expires_at` before returning; returns 401 if expired |

### The key insight

The access token briefly appears in the `/bff/token` response body. An attacker would need to:
1. Get malicious JavaScript running on your **same origin** (a full XSS compromise), AND
2. Make the call before the token expires

If an attacker has achieved full XSS on your origin, you have much larger problems — and even then, the token is not persisted anywhere after the tab closes. This is the **best achievable security model** for browser-based applications that must call APIs directly.

This pattern is documented in the [IETF OAuth 2.0 for Browser-Based Apps](https://datatracker.ietf.org/doc/html/draft-ietf-oauth-browser-based-apps) specification.

---

## Rendering Mode Summary

| Page | Render Mode | How it calls the API |
|------|-------------|----------------------|
| `Dashboard.razor` | Static SSR | Reads user claims from `HttpContext`; no API call |
| `GdpPage.razor` | Static SSR | Hardcoded data; no API call, no token needed |
| `ServerPage.razor` | Static SSR | Reads token from `HttpContext` directly |
| `Weather.razor` | Static SSR (streaming) | Named `HttpClient` + `BearerTokenHandler` |
| `InteractiveServerPage.razor` | Interactive Server (SignalR) | Named `HttpClient` + `BearerTokenHandler` |
| `AutoPage.razor` | Interactive Auto | `IWeatherApiService` — `ServerWeatherApiService` on server, `WasmWeatherApiService` in browser |
| `WasmPage.razor` | Interactive WebAssembly | `ApiClient` + `TokenService` → `/bff/token` |
| `Counter.razor` | Interactive WebAssembly | No API call (stateful counter demo) |

---

## Server and API Hosting — Independent or Together?

They are **independent** and can be hosted anywhere separately.

### What makes them independent

- Separate processes, separate ports, separate deployments
- The API has no reference to the server project — it only validates JWT signatures against the OIDC provider's public keys (JWKS endpoint)
- The server has no compile-time reference to the API project either — it just calls it over HTTP

### What connects them (configuration only)

```
BlazorOidcApp (Server)
  appsettings.json → Api:BaseUrl = "https://localhost:7124"        ← server-side HttpClient target

BlazorOidcApp.Client (WASM)
  wwwroot/appsettings.json → Api:BaseUrl = "https://localhost:7124" ← browser direct call target

BlazorOidcApp.Api
  appsettings.json → Cors:BlazorOrigin = "https://localhost:7169"  ← who is allowed to call it
                   → Oidc:Authority = "https://your-provider.com"  ← who issued the tokens
```

### Real-world hosting example

```
Server  → https://myapp.com          (Azure App Service / any host)
API     → https://api.myapp.com      (separate App Service / container / different cloud)
```

You would just update the three config values above — no code changes needed.

### One constraint to be aware of

Since the WASM client calls the API **directly from the browser**, the API must have **CORS configured** to allow the server's origin. If they were on the same domain (e.g., same App Service with path routing), CORS wouldn't even be needed — but that's an optimization, not a requirement.

---

## Static SSR vs Interactive Server

### Static SSR

```
Browser requests /server-page
        │
        ▼
Server runs C# code → builds HTML → sends it → DONE
        │
        ▼
Browser displays HTML

No ongoing connection. Page is "dead" HTML.
Clicking a button = full page reload (new HTTP request).
```

- C# runs **once** on the server per request
- No JavaScript interactivity (unless you add it manually)
- Fastest to load, lowest server resource usage
- Cannot update UI without a full page reload
- **Can access `HttpContext`** directly (has the real request/session)

**In this project:** `ServerPage.razor`, `Weather.razor`, `Dashboard.razor`, `GdpPage.razor`

### Interactive Server

```
Browser requests /interactive-server-page
        │
        ▼
Server prerenders initial HTML → sends it
        │
        ▼
Browser loads blazor.web.js
  → Opens WebSocket (SignalR) back to server
        │
        ▼
Page is now "live" — a copy of the component lives on the server

User clicks button
  → Event sent over WebSocket to server
  → Server runs C# → computes UI diff
  → Diff sent back over WebSocket
  → Browser updates DOM
```

- C# **stays running** on the server for the lifetime of the page
- Full Blazor interactivity — `@onclick`, `@bind`, timers, state all work
- Every user interaction travels over the WebSocket
- Higher server resource usage (one SignalR connection per user)
- **Cannot access `HttpContext`** directly (connection is WebSocket, not HTTP)

**In this project:** `InteractiveServerPage.razor`

### Interactive Auto

```
First visit (WASM bundle not yet cached):
Browser requests /auto-page
        │
        ▼
Server prerenders initial HTML → sends it
        │
        ▼
Browser loads blazor.web.js
  → Opens WebSocket (SignalR) back to server
  → Page runs as Interactive Server
  → Meanwhile: browser downloads WASM bundle in background

Subsequent visits (WASM bundle cached):
Browser requests /auto-page
        │
        ▼
Server sends lightweight HTML shell
        │
        ▼
Browser loads WASM bundle from cache (fast — already downloaded)
  → Page runs entirely in WebAssembly
  → No SignalR connection needed
```

- First visit: instant interactivity via SignalR (no WASM download wait)
- Subsequent visits: fully client-side in WebAssembly (no server connection)
- Best of both worlds: fast startup + scalable runtime
- **The challenge**: the component must work in both server DI and WASM DI contexts

#### The IWeatherApiService Pattern

Because the component runs server-side first and WASM later, it cannot simply inject `ApiClient` (WASM-only) or use `HttpContext` (server-only). Instead, a shared interface is used:

```
IWeatherApiService  (defined in WASM project, shared)
    │
    ├── ServerWeatherApiService  (registered in server DI)
    │       uses: IHttpClientFactory → named "Api" client → BearerTokenHandler
    │
    └── WasmWeatherApiService    (registered in WASM DI)
            uses: ApiClient → TokenService → /bff/token
```

The page injects `IWeatherApiService` and calls `GetWeatherAsync()` — identical code regardless of which runtime is active. `OperatingSystem.IsBrowser()` is used only for the UI badge that shows the current runtime.

**In this project:** `AutoPage.razor`, `IWeatherApiService.cs`, `ServerWeatherApiService.cs`, `WasmWeatherApiService.cs`

### Side-by-Side Comparison

| | Static SSR | Interactive Server | Interactive Auto | Interactive WebAssembly |
|--|--|--|--|--|
| Rendering | Server, once | Server, via SignalR | Server first → WASM after | Browser (WASM) |
| Connection | HTTP only | Persistent WebSocket | WebSocket → none | None (WASM) |
| Button clicks | Full page reload | Instant (WebSocket) | Instant (both modes) | Instant (WASM) |
| `HttpContext` access | Yes | No | No | No |
| Token source | `HttpContext` directly | `BearerTokenHandler` | `IWeatherApiService` abstraction | `TokenService` → `/bff/token` |
| Server memory per user | None | Yes (SignalR circuit) | Yes → None after WASM loads | None |
| Best for | Read-only pages | Dashboards, real-time UI | Pages needing fast startup + scalable runtime | Fully client-side apps |

### Navigation Menu Mapping

| Nav Label | Route | Render Mode |
|-----------|-------|-------------|
| Home | `/` | Static SSR |
| Dashboard | `/dashboard` | Static SSR |
| Server Page (SSR) | `/server-page` | Static SSR |
| Interactive Server | `/interactive-server` | Interactive Server (SignalR) |
| Auto Page | `/auto-page` | Interactive Auto |
| WASM Page | `/wasm-page` | Interactive WebAssembly |
| Counter (WASM) | `/counter` | Interactive WebAssembly |
| GDP Top 10 | `/gdp` | Static SSR |
| Weather (SSR) | `/weather` | Static SSR (with streaming) |

---

## OIDC Configuration — Where to Put Credentials

If you use your own OIDC provider instead of the Duende demo server, update these two files:

### Server Project — `BlazorOidcApp/appsettings.json`

```json
{
  "Oidc": {
    "Authority":    "https://YOUR-OIDC-PROVIDER.com",
    "ClientId":     "YOUR-CLIENT-ID",
    "ClientSecret": "YOUR-CLIENT-SECRET"
  },
  "Api": {
    "BaseUrl": "https://localhost:7124"
  }
}
```

### API Project — `BlazorOidcApp.Api/appsettings.json`

```json
{
  "Oidc": {
    "Authority":   "https://YOUR-OIDC-PROVIDER.com",
    "ApiAudience": "api"
  },
  "Cors": {
    "BlazorOrigin": "https://localhost:7169"
  }
}
```

### What "Authority" means

The `Authority` URL is the base URL of your OIDC provider. The middleware automatically appends `/.well-known/openid-configuration` to discover everything else (login URL, token URL, public keys).

| Provider | Authority URL |
|----------|--------------|
| Duende IdentityServer (self-hosted) | `https://your-server.com` |
| Azure AD | `https://login.microsoftonline.com/{tenant-id}/v2.0` |
| Okta | `https://your-domain.okta.com/oauth2/default` |
| Auth0 | `https://your-domain.auth0.com` |
| Keycloak | `https://your-server.com/realms/your-realm` |

### Do NOT put secrets in `appsettings.json` for production

`appsettings.json` is checked into source control. Keep `ClientSecret` out of it in real deployments.

**For local development — use .NET User Secrets:**
```bash
dotnet user-secrets set "Oidc:ClientSecret" "your-real-secret" --project BlazorOidcApp/BlazorOidcApp
```
This stores the secret in your Windows user profile, not in the project files.

**For production — use environment variables:**
```
OIDC__ClientSecret=your-real-secret
OIDC__Authority=https://your-provider.com
```
ASP.NET Core reads environment variables automatically and they override `appsettings.json`. Double underscore `__` maps to nested config keys (`Oidc:ClientSecret`).
