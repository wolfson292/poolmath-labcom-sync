namespace PoolSync.Configuration;

/// <summary>Pairs one LabCOM account with one Pool Math pool.</summary>
public sealed class WaterBodyOptions
{
    /// <summary>Label used in logs and on the status endpoint.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The LabCOM account id these readings come from.</summary>
    public string LabComAccountId { get; set; } = string.Empty;

    /// <summary>The Pool Math pool id (a GUID) these readings are written to.</summary>
    public string PoolMathPoolId { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;
}
