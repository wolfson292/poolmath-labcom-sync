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

    /// <summary>
    /// The code that addresses this pool's public share page, or null when sharing is off. Pool
    /// Math offers two mechanisms; the per-pool code wins because it points at this pool alone.
    /// </summary>
    public string? ShareCodeOrNull =>
        ShareWithCode && !string.IsNullOrWhiteSpace(ShareCode) ? ShareCode
        : ShareWithTfp && !string.IsNullOrWhiteSpace(UserId) ? UserId
        : null;
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
