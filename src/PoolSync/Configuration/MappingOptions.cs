namespace PoolSync.Configuration;

/// <summary>
/// Maps LabCOM measurements onto Pool Math test-log fields. A measurement is matched on its scenario
/// id first (stable across LabCOM's display-name changes), then on its parameter name.
/// </summary>
public sealed class MappingOptions
{
    public const string SectionName = "Mapping";

    /// <summary>LabCOM scenario id (e.g. "429-pH-PoolLab") to Pool Math field.</summary>
    public Dictionary<string, string> ByScenario { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["429-pH-PoolLab"] = PoolMathFields.Ph,
        ["428-Chlorine-Free"] = PoolMathFields.FreeChlorine,
        ["421-Chlorine-Total"] = PoolMathFields.TotalChlorine,
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
