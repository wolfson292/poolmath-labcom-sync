namespace PoolSync.Configuration;

/// <summary>
/// Maps LabCOM measurements onto Pool Math test-log fields. A measurement is matched on its scenario
/// id first (stable across LabCOM's display-name changes), then on its parameter name.
/// </summary>
public sealed class MappingOptions
{
    public const string SectionName = "Mapping";

    /// <summary>
    /// LabCOM scenario id to Pool Math field, tried before the parameter name.
    ///
    /// Only unambiguous scenarios belong here. A PoolLab reports free, total and combined chlorine
    /// under a single "8-CL" scenario, so chlorine has to be resolved by parameter name instead;
    /// mapping that scenario would collapse all three readings onto one field. The catch-all
    /// "manually added" scenario is excluded for the same reason.
    /// </summary>
    public Dictionary<string, string> ByScenario { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["19-PH"] = PoolMathFields.Ph,
        ["2-TA"] = PoolMathFields.TotalAlkalinity,
        ["12-CYA"] = PoolMathFields.CyanuricAcid,
        ["429-pH-PoolLab"] = PoolMathFields.Ph,
        ["430-Total-Alkalinity"] = PoolMathFields.TotalAlkalinity,
        ["431-Cyanuric-Acid"] = PoolMathFields.CyanuricAcid,
    };

    /// <summary>LabCOM parameter name (e.g. "PL pH") to Pool Math field. Matched case-insensitively.</summary>
    public Dictionary<string, string> ByParameter { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["PL pH"] = PoolMathFields.Ph,
        ["pH"] = PoolMathFields.Ph,
        ["PL Chlorine Free"] = PoolMathFields.FreeChlorine,
        ["Chlorine free"] = PoolMathFields.FreeChlorine,
        ["PL Chlorine Total"] = PoolMathFields.TotalChlorine,
        ["Chlorine total"] = PoolMathFields.TotalChlorine,
        ["PL Chlorine Combined"] = PoolMathFields.CombinedChlorine,
        ["PL Alkalinity"] = PoolMathFields.TotalAlkalinity,
        ["PL T-Alka"] = PoolMathFields.TotalAlkalinity,
        ["Alkalinity-M"] = PoolMathFields.TotalAlkalinity,
        ["PL Cyanuric Acid"] = PoolMathFields.CyanuricAcid,
        ["Cyanuric Acid"] = PoolMathFields.CyanuricAcid,
        ["PL Ca-Hardness"] = PoolMathFields.CalciumHardness,
        ["Calcium Hardness"] = PoolMathFields.CalciumHardness,
        ["PL Salt"] = PoolMathFields.Salt,
        ["Salt"] = PoolMathFields.Salt,
        ["PL Borate"] = PoolMathFields.Borate,
        ["Borate"] = PoolMathFields.Borate,
        ["PL TDS"] = PoolMathFields.Tds,
        ["Water Temperature"] = PoolMathFields.WaterTemp,
        ["Temperature"] = PoolMathFields.WaterTemp,
    };

    /// <summary>
    /// Readings outside these bounds are dropped rather than written. A PoolLab reports an
    /// over-range result as an out-of-scale number (an FC of 1,000,000, a pH of 9.0) instead of
    /// flagging it, and Pool Math would otherwise take that as the pool's current value. The
    /// defaults are the PoolLab 1.0 measuring ranges; widen one if another photometer reads further.
    /// Keyed by Pool Math field, inclusive at both ends. A field with no entry is not checked.
    /// </summary>
    public Dictionary<string, ValueRange> ValidRanges { get; set; } = new(StringComparer.Ordinal)
    {
        [PoolMathFields.Ph] = new(6.5, 8.4),
        [PoolMathFields.FreeChlorine] = new(0, 8),
        [PoolMathFields.TotalChlorine] = new(0, 8),
        [PoolMathFields.CombinedChlorine] = new(0, 8),
        [PoolMathFields.TotalAlkalinity] = new(0, 200),
        [PoolMathFields.CyanuricAcid] = new(0, 160),
        [PoolMathFields.CalciumHardness] = new(0, 500),
        [PoolMathFields.Salt] = new(0, 10000),
        [PoolMathFields.Borate] = new(0, 100),
        [PoolMathFields.Tds] = new(0, 10000),
        // Wide enough for either unit, since WaterTempUnits decides which one this is.
        [PoolMathFields.WaterTemp] = new(-5, 120),
    };

    /// <summary>
    /// Pool Math records combined chlorine, LabCOM records total. When both free and total chlorine
    /// are present in a session, derive CC = total - free.
    /// </summary>
    public bool DeriveCombinedChlorine { get; set; } = true;

    /// <summary>Water temperature unit written alongside waterTemp. 0 = Fahrenheit, 1 = Celsius.</summary>
    public int WaterTempUnits { get; set; } = 0;

    /// <summary>
    /// Optional note attached to each imported log, e.g. "Imported from PoolLab {device}", where
    /// "{device}" is replaced with the PoolLab serial. Empty by default: the official clients send
    /// no notes field on a test log, so this adds one the Pool Math UI may not surface.
    /// </summary>
    public string NoteTemplate { get; set; } = "";
}

/// <summary>An inclusive bound on a reading. Settable as Mapping__ValidRanges__ph__Max and so on.</summary>
public sealed class ValueRange
{
    public ValueRange()
    {
    }

    public ValueRange(double min, double max)
    {
        Min = min;
        Max = max;
    }

    public double Min { get; set; } = double.MinValue;

    public double Max { get; set; } = double.MaxValue;

    public bool Contains(double value) => value >= Min && value <= Max;
}

/// <summary>Pool Math test-log field names, as they appear in the log document JSON.</summary>
public static class PoolMathFields
{
    public const string Ph = "ph";
    public const string FreeChlorine = "fc";
    public const string CombinedChlorine = "cc";
    public const string TotalAlkalinity = "ta";
    public const string CyanuricAcid = "cya";
    public const string CalciumHardness = "ch";
    public const string Salt = "salt";
    public const string Borate = "bor";
    public const string Tds = "tds";
    public const string WaterTemp = "waterTemp";

    /// <summary>Not a Pool Math field: captured only so combined chlorine can be derived.</summary>
    public const string TotalChlorine = "totalChlorine";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Ph, FreeChlorine, CombinedChlorine, TotalAlkalinity, CyanuricAcid,
        CalciumHardness, Salt, Borate, Tds, WaterTemp, TotalChlorine,
    };
}
