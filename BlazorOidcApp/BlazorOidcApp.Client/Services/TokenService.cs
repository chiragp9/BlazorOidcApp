using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace BlazorOidcApp.Client.Services;

/// <summary>
/// Fetches an access token from the server BFF endpoint (/bff/token) using the
/// session cookie, then caches it in memory for the lifetime of the WASM session.
/// The token is never written to localStorage — it lives only in the JS heap.
/// </summary>
public class TokenService(HttpClient httpClient)
{
    private string? _cachedToken;
    private DateTimeOffset _tokenExpiry = DateTimeOffset.MinValue;

    /// <summary>Returns a valid access token, fetching from the BFF if expired or not yet loaded.</summary>
    public async Task<string?> GetTokenAsync()
    {
        // Return cached token if it has more than 2 minutes left
        if (_cachedToken is not null && _tokenExpiry > DateTimeOffset.UtcNow.AddMinutes(2))
            return _cachedToken;

        try
        {
            var response = await httpClient.GetFromJsonAsync<TokenResponse>("/bff/token");
            if (response?.AccessToken is null) return null;

            _cachedToken = response.AccessToken;
            _tokenExpiry = DateTimeOffset.TryParse(response.ExpiresAt, out var exp)
                ? exp
                : DateTimeOffset.UtcNow.AddMinutes(55);

            return _cachedToken;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Clears the cached token — call this on 401 responses.</summary>
    public void InvalidateToken()
    {
        _cachedToken = null;
        _tokenExpiry = DateTimeOffset.MinValue;
    }

    private record TokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("expires_at")]  string? ExpiresAt);
}
