using BlazorOidcApp.Client.Services;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

builder.Services.AddAuthorizationCore();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<AuthenticationStateProvider, BffAuthenticationStateProvider>();

// HttpClient for BFF calls (same origin as the Blazor server host)
// Used by TokenService to call /bff/token
builder.Services.AddScoped(sp =>
    new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });

// Named HttpClient for direct API calls (different origin)
builder.Services.AddHttpClient<ApiClient>(client =>
    client.BaseAddress = new Uri(builder.Configuration["Api:BaseUrl"]!));

builder.Services.AddScoped<TokenService>();

// Auto render mode: WASM-side implementation of shared API service
builder.Services.AddScoped<IWeatherApiService, WasmWeatherApiService>();

await builder.Build().RunAsync();
