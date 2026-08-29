using Microsoft.Extensions.Options;
using PoolSync.Configuration;
using PoolSync.LabCom;
using PoolSync.PoolMath;

namespace PoolSync.Sync;

/// <summary>Turns LabCOM measurements into Pool Math test logs.</summary>
public sealed class ReadingMapper(
    IOptions<MappingOptions> mappingOptions,
    IOptions<SyncOptions> syncOptions,
    ILogger<ReadingMapper> logger)
{
    private readonly MappingOptions _mapping = mappingOptions.Value;
    private readonly SyncOptions _sync = syncOptions.Value;

    // LabCOM is inconsistent about dashes in scenario ids: "19-PH" uses a hyphen while "12-CYA"
    // uses an en dash. Normalising both sides means config can be written with plain hyphens,
    // which also keeps these settable from environment variables.
    private readonly Lazy<Dictionary<string, string>> _scenarioLookup = new(() =>
    {
        var lookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (scenario, field) in mappingOptions.Value.ByScenario)
        {
            lookup[NormaliseDashes(scenario)] = field;
        }

        return lookup;
    });

    /// <summary>
    /// Clusters measurements into test runs. A PoolLab records one measurement per parameter over
    /// several minutes, so readings chain together while consecutive gaps stay inside the window.
    /// </summary>
    public IReadOnlyList<TestSession> GroupIntoSessions(IEnumerable<LabComMeasurement> measurements)
    {
        var ordered = measurements.OrderBy(m => m.Timestamp).ThenBy(m => m.Id).ToList();
        if (ordered.Count == 0)
        {
            return [];
        }

        var sessions = new List<TestSession>();
        var current = new List<LabComMeasurement> { ordered[0] };

        foreach (var measurement in ordered.Skip(1))
        {
            if (measurement.Timestamp - current[^1].Timestamp <= _sync.SessionWindow)
            {
                current.Add(measurement);
            }
            else
            {
                sessions.Add(new TestSession(current));
                current = [measurement];
            }
        }

        sessions.Add(new TestSession(current));
        return sessions;
    }

    /// <summary>
    /// Builds a Pool Math test log, or null when nothing in the session maps to a Pool Math field.
    /// </summary>
    public PoolMathTestLog? ToTestLog(TestSession session, WaterBodyOptions waterBody)
    {
        var values = new Dictionary<string, double>(StringComparer.Ordinal);

        // Later readings win, so a re-test within the same session replaces the earlier value.
        foreach (var measurement in session.Measurements.OrderBy(m => m.Timestamp).ThenBy(m => m.Id))
        {
            var field = ResolveField(measurement);
            if (field is null)
            {
                logger.LogDebug(
                    "No mapping for LabCOM parameter {Parameter} (scenario {Scenario}); skipping.",
                    measurement.Parameter,
                    measurement.Scenario);
                continue;
            }

            if (measurement.NumericValue is not { } value)
            {
                logger.LogDebug(
                    "LabCOM measurement {Id} for {Parameter} has a non-numeric value {Value}; skipping.",
                    measurement.Id,
                    measurement.Parameter,
                    measurement.Value);
                continue;
            }

            values[field] = value;
        }

        if (_mapping.DeriveCombinedChlorine
            && !values.ContainsKey(PoolMathFields.CombinedChlorine)
            && values.TryGetValue(PoolMathFields.TotalChlorine, out var total)
            && values.TryGetValue(PoolMathFields.FreeChlorine, out var free))
        {
            values[PoolMathFields.CombinedChlorine] = Math.Round(Math.Max(0, total - free), 2);
        }

        values.Remove(PoolMathFields.TotalChlorine);

        if (values.Count == 0)
        {
            return null;
        }

        return new PoolMathTestLog
        {
            PoolId = waterBody.PoolMathPoolId,
            LogTimestamp = session.Timestamp.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fff'Z'"),
            Fc = Value(values, PoolMathFields.FreeChlorine),
            Cc = Value(values, PoolMathFields.CombinedChlorine),
            Ph = Value(values, PoolMathFields.Ph),
            Ta = Value(values, PoolMathFields.TotalAlkalinity),
            Cya = Value(values, PoolMathFields.CyanuricAcid),
            Ch = Value(values, PoolMathFields.CalciumHardness),
            Salt = Value(values, PoolMathFields.Salt),
            Bor = Value(values, PoolMathFields.Borate),
            Tds = Value(values, PoolMathFields.Tds),
            WaterTemp = Value(values, PoolMathFields.WaterTemp),
            WaterTempUnits = values.ContainsKey(PoolMathFields.WaterTemp) ? _mapping.WaterTempUnits : null,
            Notes = BuildNote(session),
        };
    }

    private string? ResolveField(LabComMeasurement measurement)
    {
        if (!string.IsNullOrWhiteSpace(measurement.Scenario)
            && _scenarioLookup.Value.TryGetValue(NormaliseDashes(measurement.Scenario!), out var byScenario))
        {
            return Validate(byScenario);
        }

        if (!string.IsNullOrWhiteSpace(measurement.Parameter)
            && _mapping.ByParameter.TryGetValue(measurement.Parameter, out var byParameter))
        {
            return Validate(byParameter);
        }

        return null;
    }

    private string? Validate(string field)
    {
        if (PoolMathFields.All.Contains(field))
        {
            return field;
        }

        logger.LogWarning("Mapping targets unknown Pool Math field {Field}; ignoring it.", field);
        return null;
    }

    private string? BuildNote(TestSession session)
    {
        if (string.IsNullOrWhiteSpace(_mapping.NoteTemplate))
        {
            return null;
        }

        return _mapping.NoteTemplate.Replace(
            "{device}", session.DeviceSerial ?? "PoolLab", StringComparison.Ordinal);
    }

    private static string NormaliseDashes(string value) =>
        value.Replace('\u2013', '-').Replace('\u2014', '-');

    private static double? Value(Dictionary<string, double> values, string field) =>
        values.TryGetValue(field, out var value) ? value : null;
}
