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

    /// <summary>Sets the calcium hardness range. Pool Math's own build type isn't documented, so it's set here.</summary>
    public PoolSurface Surface { get; set; } = PoolSurface.Plaster;

    /// <summary>
    /// Whether a salt cell supplies the chlorine, which changes the FC and CYA targets. Left unset, it
    /// follows whether a salt cell model is chosen in the pool's Pool Math settings.
    /// </summary>
    public bool? Swg { get; set; }

    /// <summary>The pool controller that reports this water body's sensors, if any.</summary>
    public ControllerOptions? Controller { get; set; }
}

/// <summary>Where a water body's controller shows up in Home Assistant.</summary>
public sealed class ControllerOptions
{
    /// <summary>Index into the HomeAssistant list: the instance this controller reports to.</summary>
    public int HomeAssistant { get; set; }

    /// <summary>
    /// The ESPHome device name as it appears in entity ids, e.g. "pool_antenna": every entity whose
    /// id contains it is considered, and sensors are picked out by their names.
    /// </summary>
    public string? Device { get; set; }
}
