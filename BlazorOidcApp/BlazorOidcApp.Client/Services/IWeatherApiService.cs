namespace BlazorOidcApp.Client.Services;

public interface IWeatherApiService
{
    Task<WeatherForecast[]?> GetWeatherAsync();
}

public record WeatherForecast(DateOnly Date, int TemperatureC, string? Summary)
{
    public int TemperatureF => 32 + (int)(TemperatureC / 0.5556);
}
