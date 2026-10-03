using PoolSync.HomeAssistant;

namespace PoolSync.Controllers;

/// <summary>What a controller sensor measures, independent of which entity reports it.</summary>
public static class SensorRole
{
    public const string Ph = "ph";
    public const string Orp = "orp";
    public const string WaterTemp = "waterTemp";
    public const string Salt = "salt";
    public const string FilterPsi = "filterPsi";
    public const string PumpWatts = "pumpWatts";
    public const string PumpRpm = "pumpRpm";
    public const string SwgPercent = "swgPercent";
    public const string AcidDosedToday = "acidDosedToday";
    public const string AcidTank = "acidTank";

    public static readonly IReadOnlyList<string> All =
        [Ph, Orp, WaterTemp, Salt, FilterPsi, PumpWatts, PumpRpm, SwgPercent, AcidDosedToday, AcidTank];
}

/// <summary>A sensor role resolved to the entity that reports it, with its current value.</summary>
/// <param name="Value">In the sensor's own unit, except water temperature, which is always °C.</param>
public sealed record SensorReading(string Role, string Entity, string? Name, double Value, string? Unit);

/// <summary>A fault flag the controller is raising right now.</summary>
public sealed record ControllerFault(string Entity, string Name);

/// <summary>
/// Finds a controller's sensors among Home Assistant's entities by the device's name, so a new
/// controller works without listing entity ids. Each role has entity-name endings in order of
/// preference: the pool-antenna's temperature-compensated "pH Adjusted" beats its raw pH, for one.
/// </summary>
public static class ControllerSensors
{
    private static readonly (string Role, string Domain, string[] Endings)[] Patterns =
    [
        (SensorRole.Ph, "sensor", ["_ph_adjusted", "_ph_temp_comp", "_ph"]),
        (SensorRole.Orp, "sensor", ["_orpmv", "_orp"]),
        (SensorRole.WaterTemp, "sensor", ["_pool_temperature", "_water_temperature", "_swg_water_temp", "_water_temp"]),
        (SensorRole.Salt, "sensor", ["_swg_salt_ppm", "_salt_ppm"]),
        (SensorRole.FilterPsi, "sensor", ["_filter_psi", "_pump_psi", "_psi"]),
        (SensorRole.PumpWatts, "sensor", ["_pump_input_power", "_pump_watts"]),
        (SensorRole.PumpRpm, "sensor", ["_pump_speed", "_pump_rpm"]),
        (SensorRole.SwgPercent, "sensor", ["_swg_output", "_swg_set_percent"]),
        (SensorRole.AcidDosedToday, "number", ["_acid_dosed_today"]),
        (SensorRole.AcidTank, "number", ["_acid_tank"]),
    ];

    /// <summary>Fault flags worth raising: the salt cell's alarms, dosing faults, sensor faults.</summary>
    private static readonly string[] FaultEndings =
    [
        "_swg_no_flow", "_swg_low_salt", "_swg_very_low_salt", "_swg_clean", "_swg_high_current",
        "_swg_low_volts", "_swg_check_pcb", "_acid_dose_fault", "_sensor_fault", "_pump_fault",
    ];

    public static IReadOnlyList<SensorReading> Resolve(IEnumerable<HaState> states, string device)
    {
        var mine = states.Where(s => Matches(s.EntityId, device)).ToList();
        var readings = new List<SensorReading>();

        foreach (var (role, domain, endings) in Patterns)
        {
            var reading = endings
                .Select(ending => mine.FirstOrDefault(
                    s => s.EntityId.StartsWith(domain + ".", StringComparison.Ordinal)
                         && s.EntityId.EndsWith(ending, StringComparison.Ordinal)
                         && s.Number is not null))
                .FirstOrDefault(s => s is not null);

            if (reading?.Number is { } value)
            {
                readings.Add(new SensorReading(
                    role, reading.EntityId, reading.FriendlyName, Normalise(role, value, reading.Unit), reading.Unit));
            }
        }

        // A spa controller reports its water temperature on the heater, not as a sensor.
        if (readings.All(r => r.Role != SensorRole.WaterTemp)
            && mine.FirstOrDefault(s => s.EntityId.StartsWith("climate.", StringComparison.Ordinal)
                                        && s.NumberAttribute("current_temperature") is not null) is { } climate)
        {
            var fahrenheit = climate.NumberAttribute("current_temperature")!.Value;
            readings.Add(new SensorReading(
                SensorRole.WaterTemp, climate.EntityId + "#current_temperature", climate.FriendlyName,
                (fahrenheit - 32) * 5 / 9, "°F"));
        }

        return readings;
    }

    public static IReadOnlyList<ControllerFault> Faults(IEnumerable<HaState> states, string device) =>
        states
            .Where(s => s.EntityId.StartsWith("binary_sensor.", StringComparison.Ordinal)
                        && Matches(s.EntityId, device)
                        && s.State == "on"
                        && FaultEndings.Any(e => s.EntityId.EndsWith(e, StringComparison.Ordinal)))
            .Select(s => new ControllerFault(s.EntityId, s.FriendlyName ?? s.EntityId))
            .ToList();

    /// <summary>Temperatures are compared in °C whatever unit the sensor reports.</summary>
    public static double Normalise(string role, double value, string? unit) =>
        role == SensorRole.WaterTemp && unit is "°F" or "F" ? (value - 32) * 5 / 9 : value;

    private static bool Matches(string entityId, string device)
    {
        // Raw and rate-of-change helpers would otherwise match the same endings.
        if (entityId.Contains("_raw", StringComparison.Ordinal) || entityId.Contains("_delta", StringComparison.Ordinal))
        {
            return false;
        }

        var objectId = entityId[(entityId.IndexOf('.') + 1)..];
        return objectId.Contains(device, StringComparison.OrdinalIgnoreCase);
    }
}
