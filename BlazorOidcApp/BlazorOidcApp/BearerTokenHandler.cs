using Microsoft.AspNetCore.Authentication;
using System.Net.Http.Headers;

namespace BlazorOidcApp;

/// <summary>
/// DelegatingHandler that reads the access token from the current HTTP session
/// and attaches it as a Bearer header on outgoing requests to the API.
/// Used only by the server-side named HttpClient "Api".
/// </summary>
public class BearerTokenHandler(IHttpContextAccessor httpContextAccessor) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var httpContext = httpContextAccessor.HttpContext;
        if (httpContext is not null)
        {
            var token = await httpContext.GetTokenAsync("access_token");
            if (token is not null)
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return await base.SendAsync(request, cancellationToken);
    }
}
