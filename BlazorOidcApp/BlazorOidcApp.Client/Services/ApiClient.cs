using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace BlazorOidcApp.Client.Services;

/// <summary>
/// HTTP client for calling the API project directly from WASM.
/// Uses TokenService to obtain a Bearer token before each request.
/// </summary>
public class ApiClient(HttpClient httpClient, TokenService tokenService)
{
    public async Task<T?> GetAsync<T>(string path)
    {
        var token = await tokenService.GetTokenAsync()
            ?? throw new UnauthorizedAccessException("No access token available. Please log in.");

        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await httpClient.SendAsync(request);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            // Token may have expired mid-session; clear cache so next call re-fetches
            tokenService.InvalidateToken();
            throw new UnauthorizedAccessException("API returned 401 Unauthorized. Please log in again.");
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>();
    }
}
