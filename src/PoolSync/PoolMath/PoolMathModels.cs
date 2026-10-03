using System.Text.Json.Serialization;

namespace PoolSync.PoolMath;

/// <summary>
/// A Pool Math test-log document, matching what the first-party clients POST to /testlogs.
///
/// Every field is sent, nulls included: the API treats an omitted field the same as null, and
/// mirroring the observed payload keeps this from drifting. The server fills in id, userId, _ts and
/// the weather block on the way back.
/// </summary>
public sealed class PoolMathTestLog
{
    /// <summary>DateTime.MinValue as Unix seconds; the server replaces it with the real timestamp.</summary>
    public const long UnsetTimestamp = -62135596800L;

    [JsonPropertyName("type")]
    public string Type { get; set; } = "testlog";

    [JsonPropertyName("fc")]
    public double? Fc { get; set; }

    [JsonPropertyName("cc")]
    public double? Cc { get; set; }

    [JsonPropertyName("cya")]
    public double? Cya { get; set; }

    [JsonPropertyName("ch")]
    public double? Ch { get; set; }

    [JsonPropertyName("ph")]
    public double? Ph { get; set; }

    [JsonPropertyName("ta")]
    public double? Ta { get; set; }

    [JsonPropertyName("salt")]
    public double? Salt { get; set; }

    [JsonPropertyName("bor")]
    public double? Bor { get; set; }

    [JsonPropertyName("tds")]
    public double? Tds { get; set; }

    /// <summary>Left null: Pool Math derives the saturation index from the pool's own configuration.</summary>
    [JsonPropertyName("csi")]
    public double? Csi { get; set; }

    [JsonPropertyName("waterTemp")]
    public double? WaterTemp { get; set; }

    /// <summary>0 = Fahrenheit, 1 = Celsius. Null when no temperature was recorded.</summary>
    [JsonPropertyName("waterTempUnits")]
    public int? WaterTempUnits { get; set; }

    [JsonPropertyName("poolId")]
    public string PoolId { get; set; } = string.Empty;

    /// <summary>ISO 8601 UTC with milliseconds, e.g. "2026-08-29T20:12:37.165Z".</summary>
    [JsonPropertyName("logTimestamp")]
    public string LogTimestamp { get; set; } = string.Empty;

    /// <summary>Server-populated from the pool's weather location.</summary>
    [JsonPropertyName("weather")]
    public object? Weather { get; set; }

    [JsonPropertyName("weatherLogId")]
    public string? WeatherLogId { get; set; }

    [JsonPropertyName("userId")]
    public string? UserId { get; set; }

    [JsonPropertyName("origin")]
    public string? Origin { get; set; }

    /// <summary>Null on create; the server assigns the document id.</summary>
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("_ts")]
    public long Ts { get; set; } = UnsetTimestamp;

    [JsonPropertyName("deleted")]
    public bool Deleted { get; set; }

    /// <summary>
    /// Not part of the observed payload. Only serialised when a note is configured, so the default
    /// request stays byte-comparable with what the apps send.
    /// </summary>
    [JsonPropertyName("notes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Notes { get; set; }
}

/// <summary>Response from POST /testlogs: the stored log plus the pool's refreshed overview.</summary>
public sealed class TestLogResponse
{
    [JsonPropertyName("log")]
    public PoolMathTestLog? Log { get; set; }
}

/// <summary>Credentials for the Basic auth header: base64("{UserId}:{AuthToken}").</summary>
public sealed record PoolMathCredentials(string UserId, string AuthToken);

public sealed class PoolMathPool
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("volume")]
    public double? Volume { get; set; }

    [JsonPropertyName("deleted")]
    public bool Deleted { get; set; }

    /// <summary>Per-pool share code, set when sharing by link is enabled for this pool.</summary>
    [JsonPropertyName("shareCode")]
    public string? ShareCode { get; set; }

    [JsonPropertyName("shareWithCode")]
    public bool ShareWithCode { get; set; }

    /// <summary>The older "share with Trouble Free Pool" setting, keyed on the account id.</summary>
    [JsonPropertyName("shareWithTfp")]
    public bool ShareWithTfp { get; set; }

    [JsonPropertyName("userId")]
    public string? UserId { get; set; }

    /// <summary>0 = US gallons, otherwise litres.</summary>
    [JsonPropertyName("poolVolumeUnit")]
    public int? PoolVolumeUnit { get; set; }

    /// <summary>Set when a salt cell model is chosen in the pool's settings.</summary>
    [JsonPropertyName("swgModelId")]
    public string? SwgModelId { get; set; }

    [JsonPropertyName("trackSalt")]
    public bool TrackSalt { get; set; }

    [JsonPropertyName("saltMin")]
    public double? SaltMin { get; set; }

    [JsonPropertyName("saltMax")]
    public double? SaltMax { get; set; }

    [JsonPropertyName("saltTarget")]
    public double? SaltTarget { get; set; }

    [JsonPropertyName("trackBor")]
    public bool TrackBor { get; set; }

    [JsonPropertyName("borMin")]
    public double? BorMin { get; set; }

    [JsonPropertyName("borMax")]
    public double? BorMax { get; set; }

    [JsonPropertyName("borTarget")]
    public double? BorTarget { get; set; }

    [JsonPropertyName("overrideFCTarget")]
    public double? OverrideFcTarget { get; set; }

    /// <summary>0 = Fahrenheit, 1 = Celsius.</summary>
    [JsonPropertyName("waterTempUnitDefault")]
    public int? WaterTempUnitDefault { get; set; }

    /// <summary>Pool Math's running summary: the newest value of each parameter and when it was logged.</summary>
    [JsonPropertyName("overview")]
    public PoolMathOverview? Overview { get; set; }

    /// <summary>
    /// The code that addresses this pool's public share page, or null when sharing is off. Pool
    /// Math offers two mechanisms; the per-pool code wins because it points at this pool alone.
    /// </summary>
    public string? ShareCodeOrNull =>
        ShareWithCode && !string.IsNullOrWhiteSpace(ShareCode) ? ShareCode
        : ShareWithTfp && !string.IsNullOrWhiteSpace(UserId) ? UserId
        : null;
}

/// <summary>
/// The newest value of each parameter across all of a pool's logs, as Pool Math maintains it. Each
/// value has its own timestamp because a test log rarely carries every parameter.
/// </summary>
public sealed class PoolMathOverview
{
    [JsonPropertyName("fc")]
    public double? Fc { get; set; }

    [JsonPropertyName("fcTs")]
    public DateTimeOffset? FcTs { get; set; }

    [JsonPropertyName("cc")]
    public double? Cc { get; set; }

    [JsonPropertyName("ccTs")]
    public DateTimeOffset? CcTs { get; set; }

    [JsonPropertyName("ph")]
    public double? Ph { get; set; }

    [JsonPropertyName("phTs")]
    public DateTimeOffset? PhTs { get; set; }

    [JsonPropertyName("ta")]
    public double? Ta { get; set; }

    [JsonPropertyName("taTs")]
    public DateTimeOffset? TaTs { get; set; }

    [JsonPropertyName("cya")]
    public double? Cya { get; set; }

    [JsonPropertyName("cyaTs")]
    public DateTimeOffset? CyaTs { get; set; }

    [JsonPropertyName("ch")]
    public double? Ch { get; set; }

    [JsonPropertyName("chTs")]
    public DateTimeOffset? ChTs { get; set; }

    [JsonPropertyName("salt")]
    public double? Salt { get; set; }

    [JsonPropertyName("saltTs")]
    public DateTimeOffset? SaltTs { get; set; }

    [JsonPropertyName("bor")]
    public double? Bor { get; set; }

    [JsonPropertyName("borTs")]
    public DateTimeOffset? BorTs { get; set; }

    [JsonPropertyName("waterTemp")]
    public double? WaterTemp { get; set; }

    [JsonPropertyName("waterTempUnits")]
    public int? WaterTempUnits { get; set; }

    [JsonPropertyName("waterTempTs")]
    public DateTimeOffset? WaterTempTs { get; set; }

    /// <summary>Pool Math's own CSI, kept for comparison with the one calculated here.</summary>
    [JsonPropertyName("csi")]
    public double? Csi { get; set; }
}

/// <summary>The public share page's JSON: the shared pool documents, overview included.</summary>
public sealed class SharedPools
{
    [JsonPropertyName("pools")]
    public List<SharedPool>? Pools { get; set; }
}

public sealed class SharedPool
{
    [JsonPropertyName("pool")]
    public PoolMathPool? Pool { get; set; }
}

/// <summary>
/// One entry from /timeline/list: a test log, a chemical addition or a maintenance log, told apart by
/// <see cref="Type"/>. One class with every field nullable, since the three share the envelope.
/// </summary>
public sealed class PoolMathTimelineEntry
{
    public const string TestLog = "testlog";
    public const string ChemLog = "chemlog";
    public const string MaintLog = "maintlog";

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("poolId")]
    public string? PoolId { get; set; }

    [JsonPropertyName("logTimestamp")]
    public DateTimeOffset? LogTimestamp { get; set; }

    [JsonPropertyName("deleted")]
    public bool Deleted { get; set; }

    [JsonPropertyName("notes")]
    public string? Notes { get; set; }

    [JsonPropertyName("weather")]
    public System.Text.Json.JsonElement? Weather { get; set; }

    // Test log.
    [JsonPropertyName("fc")]
    public double? Fc { get; set; }

    [JsonPropertyName("cc")]
    public double? Cc { get; set; }

    [JsonPropertyName("ph")]
    public double? Ph { get; set; }

    [JsonPropertyName("ta")]
    public double? Ta { get; set; }

    [JsonPropertyName("cya")]
    public double? Cya { get; set; }

    [JsonPropertyName("ch")]
    public double? Ch { get; set; }

    [JsonPropertyName("salt")]
    public double? Salt { get; set; }

    [JsonPropertyName("bor")]
    public double? Bor { get; set; }

    [JsonPropertyName("tds")]
    public double? Tds { get; set; }

    [JsonPropertyName("waterTemp")]
    public double? WaterTemp { get; set; }

    [JsonPropertyName("waterTempUnits")]
    public int? WaterTempUnits { get; set; }

    // Chemical addition. Chemical and unit are Pool Math's own numeric codes.
    [JsonPropertyName("chemical")]
    public int? Chemical { get; set; }

    [JsonPropertyName("amount")]
    public double? Amount { get; set; }

    [JsonPropertyName("unit")]
    public int? Unit { get; set; }

    [JsonPropertyName("percent")]
    public double? Percent { get; set; }

    /// <summary>The amount in mL or g, as Pool Math normalises it.</summary>
    [JsonPropertyName("normalizedAmount")]
    public double? NormalizedAmount { get; set; }

    // Maintenance log.
    [JsonPropertyName("backwashed")]
    public bool? Backwashed { get; set; }

    [JsonPropertyName("brushed")]
    public bool? Brushed { get; set; }

    [JsonPropertyName("vacuumed")]
    public bool? Vacuumed { get; set; }

    [JsonPropertyName("cleanedFilter")]
    public bool? CleanedFilter { get; set; }

    [JsonPropertyName("opened")]
    public bool? Opened { get; set; }

    [JsonPropertyName("closed")]
    public bool? Closed { get; set; }

    [JsonPropertyName("pressure")]
    public double? Pressure { get; set; }

    [JsonPropertyName("flowRate")]
    public double? FlowRate { get; set; }

    [JsonPropertyName("pumpRuntime")]
    public double? PumpRuntime { get; set; }

    [JsonPropertyName("swgCellPercent")]
    public double? SwgCellPercent { get; set; }
}

/// <summary>Paged list envelope used by /pools/list and /timeline/list.</summary>
public sealed class PagedResults<T>
{
    [JsonPropertyName("results")]
    public List<T>? Results { get; set; }

    [JsonPropertyName("continuationToken")]
    public string? ContinuationToken { get; set; }
}

/// <summary>The user document returned by POST /auth.</summary>
public sealed class PoolMathUser
{
    [JsonPropertyName("userId")]
    public string? UserId { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("defPoolId")]
    public string? DefaultPoolId { get; set; }

    [JsonPropertyName("authorizations")]
    public List<PoolMathAuthorization>? Authorizations { get; set; }
}

public sealed class PoolMathAuthorization
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("token")]
    public string? Token { get; set; }
}

public sealed class PoolMathException(string message) : Exception(message);
