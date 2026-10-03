namespace PoolSync.Configuration;

/// <summary>Products the dosing advice is worked out for.</summary>
public sealed class BalanceOptions
{
    public const string SectionName = "Balance";

    /// <summary>Liquid chlorine strength, as the trade percentage on the jug.</summary>
    public double ChlorinePercent { get; set; } = 10;

    /// <summary>Muriatic acid strength. 31.45% is full-strength (20° Baumé); 14.5% is the common half-strength.</summary>
    public double AcidPercent { get; set; } = 31.45;
}

/// <summary>Pool surface, which sets the calcium hardness range (and, for a spa, CYA too).</summary>
public enum PoolSurface
{
    Plaster,
    Fiberglass,
    Vinyl,

    /// <summary>An acrylic hot tub: lower calcium to avoid foaming, and moderate CYA.</summary>
    Spa,
}
