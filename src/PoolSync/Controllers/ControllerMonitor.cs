using Microsoft.Extensions.Options;
using PoolSync.Chemistry;
using PoolSync.Configuration;
using PoolSync.HomeAssistant;
using PoolSync.Storage;

namespace PoolSync.Controllers;

/// <summary>A controller sensor beside a test taken at the same moment.</summary>
public sealed record ComparisonView(
    string Role, DateTimeOffset At, double TestValue, double SensorValue, double Delta, string TestSource, double? Limit, bool Drifted);

/// <summary>What a water body's controller is reporting, for the status page.</summary>
public sealed record ControllerStatus(
    string Device,
    int HomeAssistant,
    bool Connected,
    string? Error,
    IReadOnlyList<SensorReading> Readings,
    IReadOnlyList<ControllerFault> Faults,
    IReadOnlyList<ComparisonView> Comparisons,
    IReadOnlyList<HealthIssue> Issues,
    double? FilterCleanPsi,
    FcPrediction? Fc = null);

/// <summary>
/// Reads each pool's controller through Home Assistant on every sync. Samples are stored on their
/// own, apart from tests; doses the controller makes are logged as additions; each test is paired
/// with what the controller's sensors read at the time; and the equipment rules in
/// <see cref="EquipmentHealth"/> are run over the samples.
/// </summary>
public sealed class ControllerMonitor(
    HomeAssistantClient homeAssistant,
    PoolDatabase database,
    IOptions<BalanceOptions> balanceOptions,
    ILogger<ControllerMonitor> logger)
{
    /// <summary>How long samples are kept; health checks look back 30 days.</summary>
    private static readonly TimeSpan SampleRetention = TimeSpan.FromDays(60);

    /// <summary>How far back tests are paired with sensor history: about what HA's recorder keeps.</summary>
    private static readonly TimeSpan ComparisonLookback = TimeSpan.FromDays(30);

    /// <summary>Caps HA history lookups per run, so a first run over a month of tests stays gentle.</summary>
    private const int ComparisonsPerRun = 20;

    /// <summary>Entity states fetched once per instance per run and shared between water bodies.</summary>
    public sealed class StatesCache(HomeAssistantClient client)
    {
        private readonly Dictionary<int, Task<IReadOnlyList<HaState>>> _states = [];

        public Task<IReadOnlyList<HaState>> GetAsync(int instance, CancellationToken ct)
        {
            if (!_states.TryGetValue(instance, out var task))
            {
                task = client.StatesAsync(instance, ct);
                _states[instance] = task;
            }

            return task;
        }
    }

    public StatesCache NewRun() => new(homeAssistant);

    public async Task PruneAsync(DateTimeOffset now, CancellationToken ct) =>
        await database.PruneSamplesAsync(now - SampleRetention, ct);

    /// <summary>
    /// Fills in a pool's location (for rainfall) from its controller's Home Assistant, and its
    /// surface area from the controller if it reports one. Only fields still empty are touched.
    /// </summary>
    public async Task<PoolSettings> SeedSettingsAsync(
        WaterBodyOptions waterBody, PoolSettings settings, StatesCache cache, CancellationToken ct)
    {
        if (waterBody.Controller is not { Device: { Length: > 0 } device } controller
            || !homeAssistant.IsConfigured(controller.HomeAssistant)
            || (settings.Latitude is not null && settings.SurfaceArea is not null))
        {
            return settings;
        }

        var seeded = settings;
        try
        {
            if (settings.Latitude is null && await homeAssistant.LocationAsync(controller.HomeAssistant, ct) is { } location)
            {
                seeded = seeded with { Latitude = Math.Round(location.Latitude, 4), Longitude = Math.Round(location.Longitude, 4) };
            }

            if (settings.SurfaceArea is null && settings.VolumeUnit == 0)
            {
                var states = await cache.GetAsync(controller.HomeAssistant, ct);
                var area = states.FirstOrDefault(s => s.EntityId.StartsWith("number.", StringComparison.Ordinal)
                    && s.EntityId.Contains(device, StringComparison.OrdinalIgnoreCase)
                    && s.EntityId.EndsWith("_surface_area", StringComparison.Ordinal))?.Number;
                if (area is > 0)
                {
                    seeded = seeded with { SurfaceArea = area };
                }
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            logger.LogDebug("Could not seed {Name}'s location: {Message}", waterBody.Name, ex.Message);
            return settings;
        }

        if (seeded != settings)
        {
            await database.SaveSettingsAsync(waterBody.Name, seeded, ct);
            logger.LogInformation("{Name}: filled in location/surface area from Home Assistant.", waterBody.Name);
        }

        return seeded;
    }

    public async Task<ControllerStatus?> CheckAsync(
        WaterBodyOptions waterBody,
        PoolSettings settings,
        IReadOnlyList<TestRecord> tests,
        IReadOnlyList<MaintenanceRecord> maintenance,
        StatesCache cache,
        DateTimeOffset now,
        CancellationToken ct,
        double? cya = null,
        double? fcMinimum = null)
    {
        if (waterBody.Controller is not { Device: { Length: > 0 } device } controller)
        {
            return null;
        }

        var instance = controller.HomeAssistant;
        if (!homeAssistant.IsConfigured(instance))
        {
            return new ControllerStatus(device, instance, false, $"Home Assistant {instance} is not configured.", [], [], [], [], null);
        }

        IReadOnlyList<HaState> states;
        try
        {
            states = await cache.GetAsync(instance, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            logger.LogWarning("{Name}: could not reach Home Assistant {Instance}: {Message}", waterBody.Name, instance, ex.Message);
            return new ControllerStatus(device, instance, false, ex.Message, [], [], [], [], null);
        }

        var readings = ControllerSensors.Resolve(states, device);
        var faults = ControllerSensors.Faults(states, device);
        var byRole = readings.ToDictionary(r => r.Role, StringComparer.Ordinal);
        var issues = new List<HealthIssue>();

        if (readings.Count == 0)
        {
            return new ControllerStatus(
                device, instance, false, $"No sensors found for \"{device}\" in Home Assistant {instance}.", [], faults, [], [], null);
        }

        await LogAcidAsync(waterBody, byRole, now, issues, settings, ct);

        await database.InsertSamplesAsync(
            readings.Select(r => new SensorSample(waterBody.Name, r.Role, r.Entity, now, r.Value)), ct);

        await CompareAsync(waterBody, instance, tests, byRole, states, now, ct);
        var comparisons = await LatestComparisonsAsync(waterBody, settings, ct);
        issues.AddRange(comparisons.Where(c => c.Drifted).Select(c => new HealthIssue(
            $"{waterBody.Name}:drift-{c.Role}",
            HealthIssue.Warning,
            $"{waterBody.Name}: the controller's {RoleLabel(c.Role)} reads {Signed(c.Delta, c.Role)} vs the " +
            $"{c.At.ToLocalTime():MMM d} test. Check or recalibrate the probe.")));

        foreach (var fault in faults)
        {
            issues.Add(new HealthIssue($"{waterBody.Name}:fault:{fault.Entity}", HealthIssue.Warning, $"{waterBody.Name}: {fault.Name}."));
        }

        double? cleanPsi = null;
        if (byRole.TryGetValue(SensorRole.FilterPsi, out var psi) && byRole.TryGetValue(SensorRole.PumpRpm, out var rpm))
        {
            var lastCleaned = maintenance
                .Where(m => EquipmentHealth.Tasks(m).Any(t => t is MaintenanceTasks.Backwashed or MaintenanceTasks.CleanedFilter))
                .Select(m => (DateTimeOffset?)m.At)
                .Max();
            var since = lastCleaned is { } cleaned && cleaned > now.AddDays(-30) ? cleaned : now.AddDays(-30);

            var pairs = await PairedAsync(waterBody.Name, SensorRole.FilterPsi, SensorRole.PumpRpm, since, ct);
            var (baseline, issue) = EquipmentHealth.FilterPressure(
                pairs.Select(p => (p.A, p.B)).ToList(), psi.Value, rpm.Value, settings.FilterPsiRise, waterBody.Name);
            cleanPsi = baseline;
            if (issue is not null)
            {
                issues.Add(issue);
            }
        }

        if (byRole.ContainsKey(SensorRole.PumpWatts) && byRole.ContainsKey(SensorRole.PumpRpm))
        {
            var pairs = await PairedAsync(waterBody.Name, SensorRole.PumpWatts, SensorRole.PumpRpm, now.AddDays(-30), ct);
            if (EquipmentHealth.PumpEfficiency(pairs.Select(p => (p.At, p.A, p.B)).ToList(), now, waterBody.Name) is { } issue)
            {
                issues.Add(issue);
            }
        }

        FcPrediction? fc = null;
        if (byRole.ContainsKey(SensorRole.Orp) && fcMinimum is { } minimum)
        {
            fc = await PredictFcAsync(waterBody, cya, minimum, now, ct);
            if (fc.Estimate is { } estimate && estimate < minimum)
            {
                issues.Add(new HealthIssue($"{waterBody.Name}:fc-low", HealthIssue.Warning,
                    $"{waterBody.Name}: FC is about {estimate:0.#} going by ORP, below the {minimum:0.#} minimum. Add chlorine."));
            }
            else if (fc.HoursToMinimum is < 6 and var hours)
            {
                issues.Add(new HealthIssue($"{waterBody.Name}:fc-falling", HealthIssue.Warning,
                    $"{waterBody.Name}: FC is about {fc.Estimate:0.#} going by ORP and falling; it reaches the " +
                    $"{minimum:0.#} minimum in about {hours:0} h."));
            }
        }

        return new ControllerStatus(device, instance, true, null, readings, faults, comparisons, issues, cleanPsi, fc);
    }

    /// <summary>Calibrates ORP against the last 90 days of FC tests, then reads the last 6 hours of ORP through it.</summary>
    private async Task<FcPrediction> PredictFcAsync(
        WaterBodyOptions waterBody, double? cya, double minimum, DateTimeOffset now, CancellationToken ct)
    {
        var points = (await database.ComparisonsAsync(waterBody.Name, 500, ct))
            .Where(c => c.Role == SensorRole.Orp && c.At >= now.AddDays(-90))
            .Select(c => (Ratio: c.TestValue, Orp: c.SensorValue))
            .ToList();
        var calibration = FcFromOrp.Fit(points);
        var recent = (await database.SamplesAsync(waterBody.Name, SensorRole.Orp, now.AddHours(-6), ct))
            .Select(s => (s.At, s.Value))
            .ToList();
        return FcFromOrp.Predict(calibration, points.Count, cya, minimum, recent);
    }

    /// <summary>
    /// Logs acid the controller dosed since the last run as an addition, and a refill as maintenance.
    /// Read from the controller's counters each run, because HA doesn't record their history.
    /// </summary>
    private async Task LogAcidAsync(
        WaterBodyOptions waterBody, IReadOnlyDictionary<string, SensorReading> byRole, DateTimeOffset now,
        List<HealthIssue> issues, PoolSettings settings, CancellationToken ct)
    {
        var stamp = now.ToUnixTimeSeconds();

        if (byRole.TryGetValue(SensorRole.AcidDosedToday, out var dosedToday))
        {
            var previous = (await database.SamplesAsync(waterBody.Name, SensorRole.AcidDosedToday, now.AddDays(-2), ct))
                .LastOrDefault();
            if (EquipmentHealth.AcidDosed(previous?.Value, dosedToday.Value) is { } dosed)
            {
                var percent = balanceOptions.Value.AcidPercent;
                await database.InsertAdditionAsync(new AdditionRecord
                {
                    WaterBody = waterBody.Name,
                    At = now,
                    Source = AdditionSource.Controller,
                    Chemical = Chemicals.MuriaticAcid,
                    Amount = Math.Round(dosed, 2),
                    Unit = "fl oz",
                    Percent = percent,
                    Normalized = dosed * 29.5735,
                    Notes = "Dosed by the controller",
                    ExternalId = $"controller:{waterBody.Name}:acid:{stamp}",
                }, ct);
                logger.LogInformation("{Name}: controller dosed {Oz} fl oz of acid.", waterBody.Name, dosed);
            }
        }

        if (byRole.TryGetValue(SensorRole.AcidTank, out var tank))
        {
            var previous = (await database.SamplesAsync(waterBody.Name, SensorRole.AcidTank, now.AddDays(-2), ct))
                .LastOrDefault();
            if (EquipmentHealth.AcidRefilled(previous?.Value, tank.Value) is { } refilled)
            {
                await database.InsertMaintenanceAsync(new MaintenanceRecord
                {
                    WaterBody = waterBody.Name,
                    At = now,
                    Source = AdditionSource.Controller,
                    Data = $$"""{"acidRefilledOz":{{Math.Round(refilled, 1).ToString(System.Globalization.CultureInfo.InvariantCulture)}}}""",
                    Notes = "Acid tank refilled",
                    ExternalId = $"controller:{waterBody.Name}:acid-refill:{stamp}",
                }, ct);
            }

            if (tank.Value < settings.AcidTankLowOz)
            {
                issues.Add(new HealthIssue(
                    $"{waterBody.Name}:acid-low", HealthIssue.Warning,
                    $"{waterBody.Name}: the acid tank is down to {tank.Value:0} fl oz ({tank.Value / 128:0.#} gal). Refill it."));
            }
        }
    }

    /// <summary>Pairs recent tests with what the controller's sensors read when each was taken.</summary>
    private async Task CompareAsync(
        WaterBodyOptions waterBody, int instance, IReadOnlyList<TestRecord> tests,
        IReadOnlyDictionary<string, SensorReading> byRole, IReadOnlyList<HaState> states, DateTimeOffset now, CancellationToken ct)
    {
        var done = await database.ComparedAsync(waterBody.Name, ct);
        var lookups = 0;

        foreach (var test in tests.Where(t => t.TakenAt >= now - ComparisonLookback && t.TakenAt <= now.AddMinutes(-10)))
        {
            foreach (var role in new[] { SensorRole.Ph, SensorRole.Salt, SensorRole.WaterTemp, SensorRole.Orp })
            {
                var testValue = role == SensorRole.Orp ? FcOverCya(test, tests) : TestValue(test, role);
                if (!byRole.TryGetValue(role, out var sensor) || testValue is null
                    || done.Contains(test.Id + "|" + role))
                {
                    continue;
                }

                if (lookups++ >= ComparisonsPerRun)
                {
                    return;
                }

                var sensorValue = await SensorValueAtAsync(waterBody.Name, instance, sensor, states, test.TakenAt, ct);
                if (sensorValue is null)
                {
                    continue;
                }

                await database.InsertComparisonAsync(new SensorComparison(
                    test.Id, waterBody.Name, role, test.TakenAt, testValue.Value, sensorValue.Value, test.Source), ct);
            }
        }
    }

    /// <summary>
    /// The sensor's average over the ten minutes either side of a test, from HA's history, or from
    /// this service's own samples when HA has none (or the value lives in an attribute).
    /// </summary>
    private async Task<double?> SensorValueAtAsync(
        string waterBody, int instance, SensorReading sensor, IReadOnlyList<HaState> states, DateTimeOffset at, CancellationToken ct)
    {
        if (!sensor.Entity.Contains('#'))
        {
            try
            {
                var points = await homeAssistant.HistoryAsync(instance, sensor.Entity, at.AddMinutes(-10), at.AddMinutes(10), ct);
                var values = points.Select(p => p.Number).OfType<double>().ToList();
                if (values.Count > 0)
                {
                    return ControllerSensors.Normalise(sensor.Role, values.Average(), sensor.Unit);
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
            {
                logger.LogDebug("History lookup for {Entity} failed: {Message}", sensor.Entity, ex.Message);
            }
        }

        var samples = (await database.SamplesAsync(waterBody, sensor.Role, at.AddMinutes(-20), ct))
            .Where(s => s.At <= at.AddMinutes(20))
            .Select(s => s.Value)
            .ToList();
        return samples.Count > 0 ? samples.Average() : null;
    }

    private async Task<IReadOnlyList<ComparisonView>> LatestComparisonsAsync(
        WaterBodyOptions waterBody, PoolSettings settings, CancellationToken ct)
    {
        var all = await database.ComparisonsAsync(waterBody.Name, 200, ct);
        // ORP pairs calibrate the FC estimate; there's no test value to show a difference against.
        return all
            .Where(c => c.Role != SensorRole.Orp)
            .GroupBy(c => c.Role)
            .Select(g => g.First())
            .Select(c =>
            {
                var limit = EquipmentHealth.DriftLimit(c.Role, settings);
                return new ComparisonView(
                    c.Role, c.At, c.TestValue, c.SensorValue, c.Delta, c.TestSource, limit, limit is { } l && Math.Abs(c.Delta) > l);
            })
            .ToList();
    }

    /// <summary>Two roles' samples taken in the same run, matched by time.</summary>
    private async Task<IReadOnlyList<(DateTimeOffset At, double A, double B)>> PairedAsync(
        string waterBody, string roleA, string roleB, DateTimeOffset since, CancellationToken ct)
    {
        var b = (await database.SamplesAsync(waterBody, roleB, since, ct)).ToDictionary(s => s.At, s => s.Value);
        return (await database.SamplesAsync(waterBody, roleA, since, ct))
            .Where(s => b.ContainsKey(s.At))
            .Select(s => (s.At, s.Value, b[s.At]))
            .ToList();
    }

    /// <summary>A test's FC over the CYA in force at the time (the newest CYA test up to then).</summary>
    public static double? FcOverCya(TestRecord test, IReadOnlyList<TestRecord> tests)
    {
        if (test.Fc is not { } fc)
        {
            return null;
        }

        var cya = test.Cya ?? tests
            .Where(t => t.Cya is > 0 and <= 300 && t.TakenAt <= test.TakenAt)
            .OrderByDescending(t => t.TakenAt)
            .FirstOrDefault()?.Cya;
        return cya is > 0 && fc is >= 0 and <= 60 ? fc / cya : null;
    }

    public static double? TestValue(TestRecord test, string role) => role switch
    {
        SensorRole.Ph => test.Ph,
        SensorRole.Salt => test.Salt,
        SensorRole.WaterTemp => WaterReadings.Temperature(test.WaterTemp, test.WaterTempUnits).Celsius,
        _ => null,
    };

    private static string RoleLabel(string role) => role switch
    {
        SensorRole.Ph => "pH probe",
        SensorRole.Salt => "salt reading",
        SensorRole.WaterTemp => "water temperature",
        _ => role,
    };

    private static string Signed(double delta, string role) => role switch
    {
        SensorRole.Ph => $"{delta:+0.00;-0.00}",
        SensorRole.Salt => $"{delta:+0;-0} ppm",
        SensorRole.WaterTemp => $"{delta * 9 / 5:+0.0;-0.0} °F",
        _ => $"{delta:+0.##;-0.##}",
    };
}
