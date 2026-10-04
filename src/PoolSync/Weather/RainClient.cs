using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace PoolSync.Weather;

/// <summary>A day's rain and reference evaporation (FAO ET₀), in mm.</summary>
public sealed record WaterDay(double RainMm, double EvaporationMm);

/// <summary>
/// Daily rainfall and evaporation at a location from Open-Meteo: its historical archive for anything older than a
/// few days (it goes back decades), and its forecast API's recent past for the last week, which the
/// archive hasn't caught up with yet. Free and keyless. Cached for a few hours per location.
/// </summary>
public sealed class RainClient(IHttpClientFactory httpFactory, ILogger<RainClient> logger)
{
    public const string HttpClientName = "open-meteo";

    private static readonly TimeSpan CacheFor = TimeSpan.FromHours(3);

    private readonly ConcurrentDictionary<string, (DateTimeOffset Fetched, IReadOnlyDictionary<DateOnly, WaterDay> Days)> _cache = new();

    /// <summary>Rain and evaporation per day from <paramref name="since"/> to today, or null if Open-Meteo can't be reached.</summary>
    public async Task<IReadOnlyDictionary<DateOnly, WaterDay>?> DailyAsync(
        double latitude, double longitude, DateOnly since, CancellationToken ct)
    {
        var key = string.Create(CultureInfo.InvariantCulture, $"{latitude:0.###},{longitude:0.###},{since:yyyy-MM-dd}");
        if (_cache.TryGetValue(key, out var hit) && DateTimeOffset.UtcNow - hit.Fetched < CacheFor)
        {
            return hit.Days;
        }

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var days = new Dictionary<DateOnly, WaterDay>();

        try
        {
            using var http = httpFactory.CreateClient(HttpClientName);
            var lat = latitude.ToString(CultureInfo.InvariantCulture);
            var lon = longitude.ToString(CultureInfo.InvariantCulture);

            var archiveEnd = today.AddDays(-6);
            if (since <= archiveEnd)
            {
                var archive = await http.GetFromJsonAsync<Response>(
                    $"https://archive-api.open-meteo.com/v1/archive?latitude={lat}&longitude={lon}" +
                    $"&start_date={since:yyyy-MM-dd}&end_date={archiveEnd:yyyy-MM-dd}&daily=precipitation_sum,et0_fao_evapotranspiration&timezone=auto", ct);
                Merge(archive, days);
            }

            var recent = await http.GetFromJsonAsync<Response>(
                $"https://api.open-meteo.com/v1/forecast?latitude={lat}&longitude={lon}" +
                "&daily=precipitation_sum,et0_fao_evapotranspiration&past_days=10&forecast_days=1&timezone=auto", ct);
            Merge(recent, days);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            logger.LogWarning("Could not read rainfall from Open-Meteo: {Message}", ex.Message);
            return null;
        }

        var result = days.Where(d => d.Key >= since).ToDictionary(d => d.Key, d => d.Value);
        _cache[key] = (DateTimeOffset.UtcNow, result);
        return result;
    }

    private static void Merge(Response? response, Dictionary<DateOnly, WaterDay> days)
    {
        var dates = response?.Daily?.Time ?? [];
        var rain = response?.Daily?.PrecipitationSum ?? [];
        var evaporation = response?.Daily?.Evaporation ?? [];
        for (var i = 0; i < Math.Min(dates.Count, rain.Count); i++)
        {
            if (rain[i] is { } mm && DateOnly.TryParse(dates[i], CultureInfo.InvariantCulture, out var date))
            {
                days[date] = new WaterDay(mm, i < evaporation.Count ? evaporation[i] ?? 0 : 0);
            }
        }
    }

    private sealed class Response
    {
        [JsonPropertyName("daily")]
        public Daily? Daily { get; set; }
    }

    private sealed class Daily
    {
        [JsonPropertyName("time")]
        public List<string>? Time { get; set; }

        [JsonPropertyName("precipitation_sum")]
        public List<double?>? PrecipitationSum { get; set; }

        [JsonPropertyName("et0_fao_evapotranspiration")]
        public List<double?>? Evaporation { get; set; }
    }
}
