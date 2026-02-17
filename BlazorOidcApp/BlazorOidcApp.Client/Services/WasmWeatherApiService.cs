namespace BlazorOidcApp.Client.Services;

public class WasmWeatherApiService(ApiClient apiClient) : IWeatherApiService
{
    public Task<WeatherForecast[]?> GetWeatherAsync()
        => apiClient.GetAsync<WeatherForecast[]>("/api/weather");
}
