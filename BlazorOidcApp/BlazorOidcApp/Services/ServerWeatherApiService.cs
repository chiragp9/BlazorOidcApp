using BlazorOidcApp.Client.Services;
using System.Net.Http.Json;

namespace BlazorOidcApp.Services;

public class ServerWeatherApiService(IHttpClientFactory httpClientFactory) : IWeatherApiService
{
    public async Task<WeatherForecast[]?> GetWeatherAsync()
    {
        var client = httpClientFactory.CreateClient("Api");
        return await client.GetFromJsonAsync<WeatherForecast[]>("/api/weather");
    }
}
