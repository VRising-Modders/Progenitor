using System;

namespace Progenitor.Units;

/// <summary>
/// Game-free policy for the instance mutation kit: lifetime bounds, VBlood-strip outcomes,
/// and the explicit contract that these writes never touch the shared prefab
/// (<c>Content.Edit</c> does that).
/// </summary>
public static class UnitInstanceMutation
{
    /// <summary>Lowest accepted <see cref="UnitSpawnOptions.LifetimeSeconds"/> / <c>TrySetLifetime</c>.</summary>
    public const float MinLifetimeSeconds = 0.05f;

    /// <summary>Highest accepted lifetime (same order as long-lived Effects beams).</summary>
    public const float MaxLifetimeSeconds = 3600f * 24f;

    /// <summary>
    /// Clamp a requested lifetime into the writable band, or null when the input is not a
    /// positive finite duration.
    /// </summary>
    public static float? ClampLifetime(float seconds)
    {
        if (!(seconds > 0f) || float.IsNaN(seconds) || float.IsInfinity(seconds))
            return null;
        if (seconds < MinLifetimeSeconds) return MinLifetimeSeconds;
        if (seconds > MaxLifetimeSeconds) return MaxLifetimeSeconds;
        return seconds;
    }

    /// <summary>
    /// One-line reminder written into API docs and log suffixes: instance kit vs Content.Edit.
    /// </summary>
    public const string InstanceVsContentEdit =
        "Units instance mutation writes the spawned entity only; Content.Edit / PrefabRegistry.Edit change the shared prefab for every future spawn and live refresh of that GUID.";

    /// <summary>
    /// Why a VBlood strip reports partial success. <paramref name="canBeConsumedCleared"/> is
    /// true when the feed flag was cleared or there was no <c>BloodConsumeSource</c>.
    /// Component removals are counted separately.
    /// </summary>
    public static string StripSummary(bool canBeConsumedCleared, int removedComponents) =>
        "stripVBlood canBeConsumedCleared=" + (canBeConsumedCleared ? "true" : "false")
        + " removed=" + removedComponents.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>Outcome of <see cref="IUnitHandle.TryStripVBlood"/>.</summary>
public readonly struct UnitStripVBloodResult
{
    public UnitStripVBloodResult(bool canBeConsumedCleared, int removedComponents, bool ok)
    {
        CanBeConsumedCleared = canBeConsumedCleared;
        RemovedComponents = removedComponents;
        Ok = ok;
    }

    /// <summary><c>BloodConsumeSource.CanBeConsumed</c> is false, or the component was absent.</summary>
    public bool CanBeConsumedCleared { get; }

    /// <summary>How many of <c>VBloodUnit</c> / <c>VBloodConsumeSource</c> were removed.</summary>
    public int RemovedComponents { get; }

    /// <summary>True when the strip completed without a hard refusal (disabled / disposed / gone).</summary>
    public bool Ok { get; }
}

/// <summary>Outcome of an instance Health / UnitStats / UnitLevel write.</summary>
public readonly struct UnitStatsWriteResult
{
    public UnitStatsWriteResult(bool written, bool pendingSpawnComputation, string? refusal)
    {
        Written = written;
        PendingSpawnComputation = pendingSpawnComputation;
        Refusal = refusal;
    }

    /// <summary>At least one field was written to the entity this call.</summary>
    public bool Written { get; }

    /// <summary>
    /// Max health still reads 0; the request is held and will be written once spawn computation
    /// fills health (Units poll).
    /// </summary>
    public bool PendingSpawnComputation { get; }

    /// <summary>Non-null when the call was refused (dead, missing component, validation).</summary>
    public string? Refusal { get; }

    public bool Ok => Written || PendingSpawnComputation;
}
