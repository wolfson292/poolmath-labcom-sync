namespace PoolSync.Configuration;

/// <summary>
/// Connection settings for the Pool Math API. Pool Math publishes no API key: the apps sign in with
/// a Trouble Free Pool account at POST /auth and then present the issued token as Basic auth.
/// </summary>
public sealed class PoolMathOptions
{
    public const string SectionName = "PoolMath";

    public string ApiServer { get; set; } = "https://api.poolmathapp.com";

    /// <summary>Sent as x-clientversion on every request.</summary>
    public string ClientVersion { get; set; } = "512 (512192)";

    /// <summary>Trouble Free Pool forum username (not the email address).</summary>
    public string? Username { get; set; }

    public string? Password { get; set; }

    /// <summary>
    /// A token already issued to this service. Set these two to skip the sign-in entirely, so the
    /// account password never has to live on the NUC.
    /// </summary>
    public string? UserId { get; set; }

    public string? AuthToken { get; set; }

    /// <summary>Names the authorization in the Pool Math account so it can be revoked on its own.</summary>
    public string DeviceName { get; set; } = "Mobile App (LabCOM Sync)";

    /// <summary>
    /// Written to each log's origin field. Left unset by default so imported logs match what the
    /// first-party clients send.
    /// </summary>
    public string? Origin { get; set; }

    public string AuthRoute { get; set; } = "auth";

    public string TestLogRoute { get; set; } = "testlogs";

    public string PoolsListRoute { get; set; } = "pools/list";

    /// <summary>
    /// Public share page for a pool; "{code}" is replaced with the pool's share code. Settable so a
    /// change to Pool Math's share URLs doesn't need a rebuild.
    /// </summary>
    public string ShareUrlTemplate { get; set; } = "https://api.poolmathapp.com/share/{code}";
}
