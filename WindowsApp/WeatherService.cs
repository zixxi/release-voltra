using System.Net.Http.Json;
using System.Text.Json;

namespace Voltra;

internal sealed record WeatherReading(string City, double Temperature, string Condition);

internal sealed class WeatherService : IDisposable
{
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(12) };
    private string? query;
    private (string Name, double Latitude, double Longitude) location;

    internal async Task<WeatherReading> ReadAsync(string city, CancellationToken cancellation)
    {
        if (!string.Equals(query, city, StringComparison.OrdinalIgnoreCase))
        {
            string search = $"https://geocoding-api.open-meteo.com/v1/search?name={Uri.EscapeDataString(city)}&count=1&language=en";
            var data = await http.GetFromJsonAsync<JsonElement>(search, cancellation);
            if (!data.TryGetProperty("results", out var results) || results.GetArrayLength() == 0)
                throw new InvalidOperationException("City not found.");
            var first = results[0];
            location = (first.GetProperty("name").GetString() ?? city,
                first.GetProperty("latitude").GetDouble(), first.GetProperty("longitude").GetDouble());
            query = city;
        }

        string url = FormattableString.Invariant(
            $"https://api.open-meteo.com/v1/forecast?latitude={location.Latitude}&longitude={location.Longitude}&current=temperature_2m,weather_code&temperature_unit=celsius");
        var response = await http.GetFromJsonAsync<JsonElement>(url, cancellation);
        var current = response.GetProperty("current");
        return new(location.Name, current.GetProperty("temperature_2m").GetDouble(),
            Describe(current.GetProperty("weather_code").GetInt32()));
    }

    private static string Describe(int code) => code switch
    {
        0 => "Clear", 1 => "Mostly clear", 2 => "Partly cloudy", 3 => "Overcast",
        45 or 48 => "Fog", 51 or 53 or 55 => "Drizzle", 56 or 57 => "Freezing drizzle",
        61 or 63 or 65 => "Rain", 66 or 67 => "Freezing rain",
        71 or 73 or 75 or 77 => "Snow", 80 or 81 or 82 => "Rain showers",
        85 or 86 => "Snow showers", 95 or 96 or 99 => "Thunderstorm", _ => "Current weather"
    };

    public void Dispose() => http.Dispose();
}
