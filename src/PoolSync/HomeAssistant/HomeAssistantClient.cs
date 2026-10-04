using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace PoolSync.HomeAssistant;

/// <summary>One Home Assistant instance. The pools are at different houses, each with its own HA.</summary>
public sealed class HomeAssistantInstance
{
    public string? Url { get; set; }

    public string? Token { get; set; }

    /// <summary>
    /// The notify service alerts go to, as "domain.service"; empty sends none. Only instance 0's is used.
    /// </summary>
    public string Notify { get; set; } = "notify.all_devices";

    /// <summary>Whether to publish readings and CSI as entities. Only instance 0's is used.</summary>
    public bool Publish { get; set; } = true;

    public bool Configured => !string.IsNullOrWhiteSpace(Url) && !string.IsNullOrWhiteSpace(Token);
}

/// <summary>An entity's current state, as GET /api/states returns it.</summary>
public sealed class HaState
{
    [JsonPropertyName("entity_id")]
    public string EntityId { get; set; } = string.Empty;

    [JsonPropertyName("state")]
    public string State { get; set; } = string.Empty;

    [JsonPropertyName("attributes")]
    public JsonElement Attributes { get; set; }

    [JsonPropertyName("last_changed")]
    public DateTimeOffset LastChanged { get; set; }

    public double? Number =>
        double.TryParse(State, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    public string? Attribute(string name) =>
        Attributes.ValueKind == JsonValueKind.Object && Attributes.TryGetProperty(name, out var value)
            ? value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText()
            : null;

    public double? NumberAttribute(string name) =>
        Attribute(name) is { } raw
        && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    public string? Unit => Attribute("unit_of_measurement");

    public string? FriendlyName => Attribute("friendly_name");
}

/// <summary>A point in an entity's history.</summary>
public sealed record HaHistoryPoint(DateTimeOffset At, string State)
{
    public double? Number =>
        double.TryParse(State, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
}

/// <summary>
/// Home Assistant's REST API, for each configured instance: entity states and history to read the
/// pool controllers, notify services for alerts, and state updates to publish readings as entities.
/// </summary>
public sealed class HomeAssistantClient(
    IHttpClientFactory httpFactory,
    IOptions<List<HomeAssistantInstance>> instances)
{
    public const string HttpClientName = "homeassistant";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public IReadOnlyList<HomeAssistantInstance> Instances => instances.Value;

    public bool IsConfigured(int index) => index >= 0 && index < Instances.Count && Instances[index].Configured;

    public async Task<IReadOnlyList<HaState>> StatesAsync(int instance, CancellationToken ct)
    {
        using var http = Client(instance);
        return await http.GetFromJsonAsync<List<HaState>>("api/states", Json, ct) ?? [];
    }

    /// <summary>
    /// An entity's history between two times, starting with its state at <paramref name="start"/>.
    /// HA keeps as much as its recorder is set to retain (30 days or so here).
    /// </summary>
    public async Task<IReadOnlyList<HaHistoryPoint>> HistoryAsync(
        int instance, string entityId, DateTimeOffset start, DateTimeOffset end, CancellationToken ct)
    {
        using var http = Client(instance);
        var url = $"api/history/period/{Iso(start)}?end_time={Uri.EscapeDataString(Iso(end))}" +
                  $"&filter_entity_id={Uri.EscapeDataString(entityId)}&minimal_response&no_attributes";

        var series = await http.GetFromJsonAsync<List<List<JsonElement>>>(url, Json, ct);
        var points = new List<HaHistoryPoint>();

        foreach (var item in series?.FirstOrDefault() ?? [])
        {
            if (item.TryGetProperty("state", out var state)
                && item.TryGetProperty("last_changed", out var changed)
                && changed.GetString() is { } at)
            {
                points.Add(new HaHistoryPoint(
                    DateTimeOffset.Parse(at, CultureInfo.InvariantCulture), state.GetString() ?? string.Empty));
            }
        }

        return points;
    }

    /// <summary>The instance's configured home location, for rainfall at the pool.</summary>
    public async Task<(double Latitude, double Longitude)?> LocationAsync(int instance, CancellationToken ct)
    {
        using var http = Client(instance);
        var config = await http.GetFromJsonAsync<JsonElement>("api/config", Json, ct);
        return config.TryGetProperty("latitude", out var lat) && config.TryGetProperty("longitude", out var lon)
               && lat.TryGetDouble(out var la) && lon.TryGetDouble(out var lo)
            ? (la, lo)
            : null;
    }

    /// <summary>Sends a notification through instance 0's configured notify service.</summary>
    public async Task NotifyAsync(string title, string message, CancellationToken ct)
    {
        if (!IsConfigured(0) || string.IsNullOrWhiteSpace(Instances[0].Notify))
        {
            return;
        }

        var (domain, service) = Split(Instances[0].Notify);
        using var http = Client(0);
        using var response = await http.PostAsJsonAsync(
            $"api/services/{domain}/{service}", new { title, message }, Json, ct);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Creates or updates an entity on instance 0. Entities set this way live until HA restarts and
    /// are refreshed on every sync, which is enough for dashboards and automations.
    /// </summary>
    public async Task SetStateAsync(string entityId, string state, object attributes, CancellationToken ct)
    {
        using var http = Client(0);
        using var response = await http.PostAsJsonAsync(
            $"api/states/{entityId}", new { state, attributes }, Json, ct);
        response.EnsureSuccessStatusCode();
    }

    private HttpClient Client(int index)
    {
        if (!IsConfigured(index))
        {
            throw new InvalidOperationException($"Home Assistant instance {index} is not configured.");
        }

        var instance = Instances[index];
        var http = httpFactory.CreateClient(HttpClientName);
        http.BaseAddress = new Uri(instance.Url!.EndsWith('/') ? instance.Url : instance.Url + "/");
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", instance.Token);
        return http;
    }

    private static (string Domain, string Service) Split(string service)
    {
        var dot = service.IndexOf('.');
        return dot > 0 ? (service[..dot], service[(dot + 1)..]) : ("notify", service);
    }

    private static string Iso(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
}
