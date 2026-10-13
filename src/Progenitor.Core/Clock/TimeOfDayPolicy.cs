using System;

namespace Progenitor.Clock;

/// <summary>
/// Game-free decisions behind <c>ctx.Time</c>'s ownership of the game's
/// <c>DayNightCycleDisabled</c> flag: when an override may be taken, which prior value to restore,
/// and what to do with the persisted ownership marker after a world load. Kept free of BepInEx and
/// game types so the test project compiles it.
///
/// <para><b>Why a marker.</b> The game saves the flag with the world. An autosave during a hold,
/// then a restart, loads a world whose flag is already set; without a record, the next override
/// would take "set" as the value to restore and a later release would leave the cycle stopped
/// for good. The marker is a plain per-save value (<c>ModSave</c> store
/// <see cref="MarkerStore"/>, key <see cref="MarkerKey"/>) holding the flag's value from before
/// the first override. It is written in memory at that first override and reaches disk with the
/// next world save, so it is on disk exactly when a stopped flag can be.</para>
/// </summary>
public static class TimeOfDayPolicy
{
    /// <summary>The Core-owned <c>ModSave</c> store the marker lives in (<c>progenitor/&lt;this&gt;.json</c> beside the save).</summary>
    public const string MarkerStore = "progenitor.core.time";

    /// <summary>Marker key. Value <c>"true"</c>/<c>"false"</c>: the flag before the first override. Absent: ctx.Time does not own the flag.</summary>
    public const string MarkerKey = "dayNightCycleDisabledBefore";

    /// <summary>What to do with a marker found when no override owns the flag.</summary>
    public enum Recovery
    {
        /// <summary>No marker, or an override is live (it still owns the flag): do nothing.</summary>
        None = 0,

        /// <summary>The flag already has the recorded prior value: just clear the marker.</summary>
        ClearMarker = 1,

        /// <summary>Write the recorded prior value to the flag, then clear the marker.</summary>
        RestoreAndClear = 2,
    }

    /// <summary>Encode a prior flag value for the marker.</summary>
    public static string Encode(bool prior) => prior ? "true" : "false";

    /// <summary>Decode a marker value. Null (no marker) for null, empty or anything unrecognised.</summary>
    public static bool? Decode(string? marker) =>
        string.Equals(marker, "true", StringComparison.OrdinalIgnoreCase) ? true
        : string.Equals(marker, "false", StringComparison.OrdinalIgnoreCase) ? false
        : null;

    /// <summary>
    /// After a world load (or a Core install into a running world) with the marker read:
    /// restore, clear, or leave it.
    /// </summary>
    public static Recovery Decide(bool? markerPrior, bool overrideLive, bool flagNow)
    {
        if (markerPrior == null || overrideLive) return Recovery.None;
        return flagNow == markerPrior.Value ? Recovery.ClearMarker : Recovery.RestoreAndClear;
    }

    /// <summary>
    /// The value the first override records as "before": the marker's when one survived (the flag
    /// now may be one an earlier override set and a save kept), otherwise the flag as it is.
    /// </summary>
    public static bool PriorForFirstTake(bool? markerPrior, bool flagNow) => markerPrior ?? flagNow;

    /// <summary>
    /// Overrides are refused until this Core generation has seen the current world finish loading:
    /// a completed load (<paramref name="loadsThisGeneration"/> &gt; 0), or a Core install into a
    /// world that was already running. An override taken mid-load would be overwritten by the
    /// save's own flag.
    /// </summary>
    public static bool WorldLoadComplete(int loadsThisGeneration, bool installedIntoRunningWorld) =>
        loadsThisGeneration > 0 || installedIntoRunningWorld;
}
