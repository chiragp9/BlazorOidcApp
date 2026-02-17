using Microsoft.AspNetCore.Components.Authorization;
using System.Net.Http.Json;
using System.Security.Claims;

namespace BlazorOidcApp.Client.Services;

/// <summary>
/// Supplies authentication state to the WASM runtime by fetching the current user
/// from the BFF /bff/user endpoint using the session cookie.
/// </summary>
public class BffAuthenticationStateProvider(HttpClient httpClient) : AuthenticationStateProvider
{
    private static readonly AuthenticationState Anonymous =
        new(new ClaimsPrincipal(new ClaimsIdentity()));

    public override async Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        try
        {
            var claims = await httpClient.GetFromJsonAsync<ClaimRecord[]>("/bff/user");
            if (claims is null || claims.Length == 0)
                return Anonymous;

            var identity = new ClaimsIdentity(
                claims.Select(c => new Claim(c.Type, c.Value)),
                authenticationType: "bff");

            return new AuthenticationState(new ClaimsPrincipal(identity));
        }
        catch
        {
            return Anonymous;
        }
    }

    private record ClaimRecord(string Type, string Value);
}
