using System.Text.RegularExpressions;
using PoolSync.Chemistry;
using PoolSync.Configuration;
using PoolSync.Controllers;
using PoolSync.Storage;

namespace PoolSync.HomeAssistant;

/// <summary>
/// Sends alerts through Home Assistant's notify service, and publishes each water body's readings
/// and CSI as entities there for dashboards and automations.
/// </summary>
public sealed partial class HomeAssistantPublisher(
    HomeAssistantClient homeAssistant,
    PoolDatabase database,
    ILogger<HomeAssistantPublisher> logger)
{
    /// <summary>A still-active alert is repeated this often, so it isn't forgotten but doesn't nag.</summary>
    private static readonly TimeSpan Repeat = TimeSpan.FromHours(24);

    /// <summary>
    /// Sends each warning not sent in the last day, and forgets alerts that are no longer active so
    /// they are sent again if they come back.
    /// </summary>
    public async Task AlertAsync(IReadOnlyList<HealthIssue> active, DateTimeOffset now, CancellationToken ct)
    {
        if (!homeAssistant.IsConfigured(0))
        {
            return;
        }

        var sent = await database.AlertsAsync(ct);

        foreach (var issue in active.Where(i => i.Severity == HealthIssue.Warning).DistinctBy(i => i.Key))
        {
            if (sent.TryGetValue(issue.Key, out var previous) && now - previous.LastSent < Repeat)
            {
                continue;
            }

            try
            {
                await homeAssistant.NotifyAsync("Pool", issue.Message, ct);
                await database.SaveAlertAsync(
                    new AlertRecord(issue.Key, issue.Message, previous?.FirstSeen ?? now, now), ct);
                logger.LogInformation("Sent alert {Key}: {Message}", issue.Key, issue.Message);
            }
            catch (HttpRequestException ex)
            {
                logger.LogWarning("Could not send alert {Key}: {Message}", issue.Key, ex.Message);
            }
        }

        await database.ClearAlertsExceptAsync(active.Select(i => i.Key).ToHashSet(StringComparer.Ordinal), ct);
    }

    /// <summary>
    /// Publishes sensor.poolsync_&lt;water body&gt;_csi (with the recommendations as attributes) and one
    /// entity per current reading.
    /// </summary>
    public async Task PublishAsync(string waterBody, WaterBalance balance, CancellationToken ct)
    {
        if (!homeAssistant.IsConfigured(0) || !homeAssistant.Instances[0].Publish)
        {
            return;
        }

        var slug = Slug(waterBody);
        var fahrenheit = balance.TempUnits == 0;

        try
        {
            await homeAssistant.SetStateAsync(
                $"sensor.poolsync_{slug}_csi",
                balance.Csi?.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) ?? "unknown",
                new
                {
                    friendly_name = $"{waterBody} CSI",
                    icon = "mdi:scale-balance",
                    state_class = "measurement",
                    csi_after_changes = balance.CsiAfter,
                    recommendations = balance.Recommendations
                        .Select(r => $"{r.Label} {r.Current:0.##} → {r.Target:0.##}: {r.Amount ?? r.Action}")
                        .ToArray(),
                    notes = balance.Notes,
                },
                ct);

            foreach (var (key, reading) in balance.Water)
            {
                var (label, unit, value) = key == WaterReadings.WaterTempC
                    ? ("Water temperature", fahrenheit ? "°F" : "°C", fahrenheit ? reading.Value * 9 / 5 + 32 : reading.Value)
                    : (Label(key), key == PoolMathFields.Ph ? null : "ppm", reading.Value);

                await homeAssistant.SetStateAsync(
                    $"sensor.poolsync_{slug}_{(key == WaterReadings.WaterTempC ? "water_temp" : key)}",
                    Math.Round(value, 2).ToString(System.Globalization.CultureInfo.InvariantCulture),
                    new
                    {
                        friendly_name = $"{waterBody} {label}",
                        unit_of_measurement = unit,
                        state_class = "measurement",
                        source = reading.Source,
                        tested_at = reading.At,
                    },
                    ct);
            }
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning("Could not publish {WaterBody} to Home Assistant: {Message}", waterBody, ex.Message);
        }
    }

    private static string Label(string key) => key switch
    {
        PoolMathFields.FreeChlorine => "FC",
        PoolMathFields.CombinedChlorine => "CC",
        PoolMathFields.Ph => "pH",
        PoolMathFields.TotalAlkalinity => "TA",
        PoolMathFields.CyanuricAcid => "CYA",
        PoolMathFields.CalciumHardness => "CH",
        PoolMathFields.Salt => "Salt",
        PoolMathFields.Borate => "Borate",
        PoolMathFields.Tds => "TDS",
        _ => key,
    };

    public static string Slug(string name) => NonWord().Replace(name.ToLowerInvariant(), "_").Trim('_');

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonWord();
}
