namespace PoolSync.Configuration;

/// <summary>Connection settings for the LabCOM Cloud GraphQL API.</summary>
public sealed class LabComOptions
{
    public const string SectionName = "LabCom";

    /// <summary>GraphQL endpoint. Only override this for testing.</summary>
    public string Endpoint { get; set; } = "https://backend.labcom.cloud/graphql";

    /// <summary>Token from https://labcom.cloud/pages/user-setting, sent as the Authorization header.</summary>
    public string ApiToken { get; set; } = string.Empty;
}
