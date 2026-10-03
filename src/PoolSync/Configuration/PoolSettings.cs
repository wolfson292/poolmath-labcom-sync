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

    /// <summary>The settings a water body starts with when Pool Math isn't there to seed them.</summary>
    public static PoolSettings Defaults(WaterBodyOptions waterBody) => new()
    {
        Surface = waterBody.Surface,
        Swg = waterBody.Swg ?? false,
    };

    /// <summary>Copies a pool's settings out of Pool Math, for the one-time seed.</summary>
    public static PoolSettings FromPoolMath(PoolMathPool pool, WaterBodyOptions waterBody) => new()
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
    };
}
