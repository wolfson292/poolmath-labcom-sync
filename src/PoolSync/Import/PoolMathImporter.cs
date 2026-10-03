using System.Text.Json;
using Microsoft.Extensions.Options;
using PoolSync.Configuration;
using PoolSync.PoolMath;
using PoolSync.State;
using PoolSync.Storage;

namespace PoolSync.Import;

/// <summary>What an import stored, for the endpoint's response and the log.</summary>
public sealed record ImportResult(
    int Tests,
    int Additions,
    int Maintenance,
    int AlreadyImported,
    int UnmappedPool,
    IReadOnlyList<string> SettingsSeeded);

/// <summary>
/// Copies a Pool Math account's whole history into the local database: every test, chemical
/// addition and maintenance log, plus each pool's settings the first time. Safe to run repeatedly:
/// entries are keyed on their Pool Math id, so a second run stores only what's new.
/// </summary>
public sealed class PoolMathImporter(
    IPoolMathClient poolMath,
    PoolDatabase database,
    ISyncStateStore stateStore,
    IOptions<List<WaterBodyOptions>> waterBodies,
    ILogger<PoolMathImporter> logger)
{
    /// <summary>
    /// Pool Math's unit codes, worked out from entries whose normalised amount pins the unit down
    /// (5 gal = 18,927 mL). Codes not listed here are kept as codes.
    /// </summary>
    private static readonly Dictionary<int, string> Units = new()
    {
        [4] = "fl oz",
        [5] = "gal",
        [7] = "oz",
    };

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public async Task<ImportResult> ImportAsync(CancellationToken ct)
    {
        var bodiesByPool = waterBodies.Value
            .Where(w => !string.IsNullOrWhiteSpace(w.PoolMathPoolId))
            .ToDictionary(w => w.PoolMathPoolId, StringComparer.OrdinalIgnoreCase);

        // Logs this service wrote to Pool Math itself came from LabCOM; label them as such.
        var state = await stateStore.LoadAsync(ct);
        var ours = state.WaterBodies.Values
            .SelectMany(b => b.RecentLogIds)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var timeline = await poolMath.GetTimelineAsync(ct);
        int tests = 0, additions = 0, maintenance = 0, already = 0, unmapped = 0;

        foreach (var entry in timeline)
        {
            if (entry.Deleted || entry.Id is null || entry.LogTimestamp is not { } at)
            {
                continue;
            }

            if (entry.PoolId is null || !bodiesByPool.TryGetValue(entry.PoolId, out var waterBody))
            {
                unmapped++;
                continue;
            }

            var externalId = "poolmath:" + entry.Id;
            var stored = entry.Type switch
            {
                PoolMathTimelineEntry.TestLog => await database.InsertTestAsync(
                    ToTest(entry, waterBody.Name, at, externalId, ours.Contains(entry.Id)), ct),
                PoolMathTimelineEntry.ChemLog => await database.InsertAdditionAsync(
                    ToAddition(entry, waterBody.Name, at, externalId), ct),
                PoolMathTimelineEntry.MaintLog => await database.InsertMaintenanceAsync(
                    ToMaintenance(entry, waterBody.Name, at, externalId), ct),
                _ => (bool?)null,
            };

            switch (stored, entry.Type)
            {
                case (null, _):
                    logger.LogInformation("Skipped Pool Math entry {Id} of unknown type {Type}.", entry.Id, entry.Type);
                    break;
                case (false, _):
                    already++;
                    break;
                case (true, PoolMathTimelineEntry.TestLog):
                    tests++;
                    break;
                case (true, PoolMathTimelineEntry.ChemLog):
                    additions++;
                    break;
                default:
                    maintenance++;
                    break;
            }
        }

        var seeded = await SeedSettingsAsync(bodiesByPool, ct);

        logger.LogInformation(
            "Imported from Pool Math: {Tests} test(s), {Additions} addition(s), {Maintenance} maintenance " +
            "entr(ies); {Already} already imported, {Unmapped} for pools not mapped to a water body.",
            tests, additions, maintenance, already, unmapped);

        return new ImportResult(tests, additions, maintenance, already, unmapped, seeded);
    }

    /// <summary>
    /// Copies each pool's settings out of Pool Math, but only where the service has none yet: once
    /// saved here, the settings are edited on the status page and an import must not overwrite them.
    /// </summary>
    private async Task<IReadOnlyList<string>> SeedSettingsAsync(
        IReadOnlyDictionary<string, WaterBodyOptions> bodiesByPool, CancellationToken ct)
    {
        var seeded = new List<string>();

        foreach (var pool in await poolMath.ListPoolsAsync(ct))
        {
            if (!bodiesByPool.TryGetValue(pool.Id, out var waterBody)
                || await database.SettingsAsync(waterBody.Name, ct) is not null)
            {
                continue;
            }

            await database.SaveSettingsAsync(waterBody.Name, PoolSettings.FromPoolMath(pool, waterBody), ct);
            seeded.Add(waterBody.Name);
        }

        return seeded;
    }

    public static TestRecord ToTest(
        PoolMathTimelineEntry entry, string waterBody, DateTimeOffset at, string externalId, bool fromLabCom) => new()
    {
        WaterBody = waterBody,
        TakenAt = at,
        Source = fromLabCom ? TestSource.LabCom : TestSource.PoolMath,
        Fc = entry.Fc,
        Cc = entry.Cc,
        Ph = entry.Ph,
        Ta = entry.Ta,
        Cya = entry.Cya,
        Ch = entry.Ch,
        Salt = entry.Salt,
        Bor = entry.Bor,
        Tds = entry.Tds,
        WaterTemp = entry.WaterTemp,
        // Pool Math sometimes keeps a unit with no temperature; drop it so it can't mislead.
        WaterTempUnits = entry.WaterTemp is null ? null : entry.WaterTempUnits,
        Notes = entry.Notes,
        Weather = entry.Weather?.GetRawText(),
        ExternalId = externalId,
    };

    public static AdditionRecord ToAddition(
        PoolMathTimelineEntry entry, string waterBody, DateTimeOffset at, string externalId) => new()
    {
        WaterBody = waterBody,
        At = at,
        Source = TestSource.PoolMath,
        ChemicalCode = entry.Chemical,
        Amount = entry.Amount,
        Unit = entry.Unit is { } u ? Units.GetValueOrDefault(u) : null,
        UnitCode = entry.Unit,
        Percent = entry.Percent,
        Normalized = entry.NormalizedAmount,
        Notes = entry.Notes,
        ExternalId = externalId,
    };

    public static MaintenanceRecord ToMaintenance(
        PoolMathTimelineEntry entry, string waterBody, DateTimeOffset at, string externalId)
    {
        // Only what was actually recorded; Pool Math sends every flag on every entry.
        var data = new
        {
            backwashed = entry.Backwashed == true ? true : (bool?)null,
            brushed = entry.Brushed == true ? true : (bool?)null,
            vacuumed = entry.Vacuumed == true ? true : (bool?)null,
            cleanedFilter = entry.CleanedFilter == true ? true : (bool?)null,
            opened = entry.Opened == true ? true : (bool?)null,
            closed = entry.Closed == true ? true : (bool?)null,
            pressure = entry.Pressure,
            flowRate = entry.FlowRate,
            pumpRuntime = entry.PumpRuntime,
            swgCellPercent = entry.SwgCellPercent,
            waterTemp = entry.WaterTemp,
        };

        return new MaintenanceRecord
        {
            WaterBody = waterBody,
            At = at,
            Source = TestSource.PoolMath,
            Data = JsonSerializer.Serialize(data, Json),
            Notes = entry.Notes,
            ExternalId = externalId,
        };
    }
}
