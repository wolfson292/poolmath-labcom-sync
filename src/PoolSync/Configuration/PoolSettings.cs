using PoolSync.PoolMath;

namespace PoolSync.Configuration;

/// <summary>
/// A pool's own settings, held by this service so it no longer depends on Pool Math for them. Seeded
/// once from the pool's Pool Math settings, then edited on the status page.
/// </summary>
public sealed record PoolSettings
{
    public double? Volume { get; init; }

    /// <summary>0 = US gallons, 1 = litres.</summary>
    public int VolumeUnit { get; init; }

    public PoolSurface Surface { get; init; } = PoolSurface.Plaster;

    /// <summary>A salt cell supplies the chlorine, which changes the FC and CYA targets.</summary>
    public bool Swg { get; init; }

    public double? SaltMin { get; init; }

    public double? SaltMax { get; init; }

    public double? SaltTarget { get; init; }

    public double? BorMin { get; init; }

    public double? BorMax { get; init; }

    public double? BorTarget { get; init; }

    /// <summary>Overrides the FC target that would otherwise follow from CYA.</summary>
    public double? FcTarget { get; init; }

    /// <summary>0 = Fahrenheit, 1 = Celsius: how temperatures are shown and entered by default.</summary>
    public int TempUnits { get; init; }

    /// <summary>Where the pool is, for rainfall. Seeded from its controller's Home Assistant location.</summary>
    public double? Latitude { get; init; }

    public double? Longitude { get; init; }

    /// <summary>
    /// Water surface, in ft² (or m² when the volume is in litres), for how much rain the pool takes on.
    /// Empty: estimated from the volume, or for a spa, assumed covered.
    /// </summary>
    public double? SurfaceArea { get; init; }

    /// <summary>
    /// How often each maintenance task is due, in days; null turns that reminder off. Keys are the
    /// task names in <see cref="MaintenanceTasks"/>.
    /// </summary>
    public Dictionary<string, int?> ReminderDays { get; init; } = new(StringComparer.Ordinal)
    {
        [MaintenanceTasks.Brushed] = 7,
        [MaintenanceTasks.Vacuumed] = 7,
        [MaintenanceTasks.CleanedFilter] = 90,
    };

    /// <summary>Warn when the controller's acid tank falls below this, in fl oz.</summary>
    public double AcidTankLowOz { get; init; } = 128;

    /// <summary>Warn when filter pressure is this many psi above clean, at the same pump speed.</summary>
    public double FilterPsiRise { get; init; } = 8;

    /// <summary>Warn when the controller's pH probe differs from a test by more than this.</summary>
    public double PhDriftLimit { get; init; } = 0.2;

    /// <summary>Warn when the salt cell's salt reading differs from a test by more than this, in ppm.</summary>
    public double SaltDriftLimit { get; init; } = 400;

    /// <summary>Warn when the controller's water temperature differs from a test by more than this, in °C.</summary>
    public double TempDriftLimitC { get; init; } = 1.5;

    /// <summary>The settings a water body starts with when Pool Math isn't there to seed them.</summary>
    public static PoolSettings Defaults(WaterBodyOptions waterBody) => new PoolSettings
    {
        Surface = waterBody.Surface,
        Swg = waterBody.Swg ?? false,
    }.WithCellReminder();

    /// <summary>A salt cell needs cleaning every few months; only remind where there is one.</summary>
    public PoolSettings WithCellReminder() =>
        Swg && !ReminderDays.ContainsKey(MaintenanceTasks.CleanedCell)
            ? this with { ReminderDays = new(ReminderDays, StringComparer.Ordinal) { [MaintenanceTasks.CleanedCell] = 90 } }
            : this;

    /// <summary>Copies a pool's settings out of Pool Math, for the one-time seed.</summary>
    public static PoolSettings FromPoolMath(PoolMathPool pool, WaterBodyOptions waterBody) => new PoolSettings
    {
        Volume = pool.Volume,
        VolumeUnit = (pool.PoolVolumeUnit ?? 0) == 0 ? 0 : 1,
        Surface = waterBody.Surface,
        Swg = waterBody.Swg ?? !string.IsNullOrWhiteSpace(pool.SwgModelId),
        SaltMin = pool.SaltMin,
        SaltMax = pool.SaltMax,
        SaltTarget = pool.SaltTarget,
        BorMin = pool.BorMin,
        BorMax = pool.BorMax,
        BorTarget = pool.BorTarget,
        FcTarget = pool.OverrideFcTarget,
        TempUnits = pool.WaterTempUnitDefault ?? 0,
    }.WithCellReminder();
}

/// <summary>Maintenance tasks, as the keys of a maintenance entry's data and of reminder settings.</summary>
public static class MaintenanceTasks
{
    public const string Backwashed = "backwashed";
    public const string Brushed = "brushed";
    public const string Vacuumed = "vacuumed";
    public const string CleanedFilter = "cleanedFilter";
    public const string CleanedCell = "cleanedCell";

    public static readonly IReadOnlyDictionary<string, string> Labels = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [Backwashed] = "Backwash",
        [Brushed] = "Brush",
        [Vacuumed] = "Vacuum",
        [CleanedFilter] = "Clean filter",
        [CleanedCell] = "Clean salt cell",
    };
}
