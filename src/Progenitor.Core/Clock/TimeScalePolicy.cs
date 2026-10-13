using System;
using System.Collections.Generic;
using System.Globalization;

namespace Progenitor.Clock;

/// <summary>
/// Game-free rules behind <c>ctx.Time.Freeze</c> / <c>ctx.Time.SlowMotion</c>: which scales are
/// accepted, when a write is needed, and what to do with the per-save ownership marker. Kept free
/// of BepInEx and game types so the test project compiles it.
///
/// <para><b>The game's world speed</b> (the game's native code, read 2026-09-27). <c>ProjectM.TimeScale</c>
/// is a networked singleton (<c>Value</c> <c>[SyncRangedFloat(0, 100, 3, 1)]</c>, <c>IsPaused</c>
/// <c>[SyncBool]</c>). <c>TimeScaleSystem</c> runs in the server and client worlds and calls
/// <c>UnityEngine.Time.set_timeScale(Value)</c> every frame, so the server's
/// <c>World.Time.DeltaTime</c> (from <c>UpdateWorldTimeSystem</c>, which reads the scaled
/// <c>Time.deltaTime</c>), its <c>ServerTime</c> game clock and every connected client follow it.
/// The game's own debug time control steps it down to 0 and its replay HUD pauses with 0, so 0 is
/// a value the game itself uses.</para>
/// </summary>
public static class TimeScalePolicy
{
    /// <summary>The game's normal speed.</summary>
    public const float Normal = 1f;

    /// <summary>
    /// The smallest slow-motion scale. The value replicates with three decimals
    /// (<c>SyncRangedFloat(0, 100, 3, 1)</c>), so anything smaller would reach clients as 0.
    /// Use <c>Freeze</c> for 0.
    /// </summary>
    public const float MinSlowMotion = 0.001f;

    /// <summary>The largest slow-motion scale: 1 is normal speed (a lease that changes nothing visible).</summary>
    public const float MaxSlowMotion = 1f;

    /// <summary>A write is skipped when the live value is already this close to the target.</summary>
    public const float WriteTolerance = 1e-4f;

    /// <summary>The Core-owned <c>ModSave</c> store the marker lives in.</summary>
    public const string MarkerStore = "progenitor.core.timescale";

    /// <summary>Marker key. Value: the invariant-culture <c>TimeScale.Value</c> from before the first lease. Absent: no lease owns it.</summary>
    public const string MarkerKey = "timeScaleBefore";

    /// <summary>What to do with a marker found when no lease owns the world speed.</summary>
    public enum Recovery
    {
        /// <summary>No marker, or a lease is live (it still owns the speed): do nothing.</summary>
        None = 0,

        /// <summary>The speed already has the recorded prior value: just clear the marker.</summary>
        ClearMarker = 1,

        /// <summary>Write the recorded prior value, then clear the marker.</summary>
        RestoreAndClear = 2,
    }

    /// <summary>
    /// Throw <see cref="ArgumentOutOfRangeException"/> unless <paramref name="scale"/> is finite and in
    /// [<see cref="MinSlowMotion"/>, <see cref="MaxSlowMotion"/>].
    /// </summary>
    public static void RequireSlowMotion(float scale, string paramName = "scale")
    {
        if (float.IsNaN(scale) || float.IsInfinity(scale) || scale < MinSlowMotion || scale > MaxSlowMotion)
            throw new ArgumentOutOfRangeException(paramName, scale,
                "scale must be in [0.001, 1]: 0.25 is quarter speed, 1 is normal. Use Freeze() to stop time.");
    }

    /// <summary>
    /// The value a client receives for <paramref name="scale"/>: rounded to the three decimals the
    /// game replicates. Written on the server as is (the server's own speed uses the full float).
    /// </summary>
    public static float OnTheWire(float scale) =>
        (float)Math.Round(Math.Min(Math.Max(scale, 0f), 100f), 3, MidpointRounding.AwayFromZero);

    /// <summary>True when <paramref name="now"/> is not already <paramref name="target"/> (within <see cref="WriteTolerance"/>).</summary>
    public static bool NeedsWrite(float now, float target) =>
        float.IsNaN(now) || Math.Abs(now - target) > WriteTolerance;

    /// <summary>Encode a prior value for the marker.</summary>
    public static string Encode(float prior) => prior.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>Decode a marker. Null (no marker) for null, empty, unparsable, non-finite or out-of-range text.</summary>
    public static float? Decode(string? marker)
    {
        if (string.IsNullOrWhiteSpace(marker)) return null;
        if (!float.TryParse(marker.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float v)) return null;
        if (float.IsNaN(v) || float.IsInfinity(v) || v < 0f || v > 100f) return null;
        return v;
    }

    /// <summary>After a world load (or a Core install into a running world) with the marker read: restore, clear, or leave it.</summary>
    public static Recovery Decide(float? markerPrior, bool leaseLive, float now)
    {
        if (markerPrior == null || leaseLive) return Recovery.None;
        return NeedsWrite(now, markerPrior.Value) ? Recovery.RestoreAndClear : Recovery.ClearMarker;
    }

    /// <summary>
    /// The value the first lease records as "before": the marker's when one survived (the value now
    /// may be one an earlier lease set and a save kept), otherwise the value as it is.
    /// </summary>
    public static float PriorForFirstTake(float? markerPrior, float now) => markerPrior ?? now;

    /// <summary>"frozen", "0.25x" or "1x".</summary>
    public static string Format(float scale) =>
        scale <= 0f ? "frozen" : scale.ToString("0.###", CultureInfo.InvariantCulture) + "x";
}

/// <summary>What kind of <c>ctx.Time</c> world-speed lease is governing, if any.</summary>
public enum TimeScaleOverrideKind
{
    /// <summary>No lease: the game's own value (normally 1) applies.</summary>
    None = 0,

    /// <summary><c>Freeze</c>: world speed 0.</summary>
    Freeze = 1,

    /// <summary><c>SlowMotion</c>: world speed in (0, 1].</summary>
    SlowMotion = 2,
}

/// <summary>The world speed now, as <c>ctx.Time.GetTimeScale</c> reads it on the server.</summary>
public sealed class TimeScaleReading
{
    /// <summary>A reading with the given values.</summary>
    public TimeScaleReading(float scale, bool paused, float engineScale, TimeScaleOverrideKind overrideKind)
    {
        Scale = scale;
        Paused = paused;
        EngineScale = engineScale;
        Override = overrideKind;
    }

    /// <summary>The game's <c>TimeScale.Value</c> in the server world: 1 normal, 0 frozen.</summary>
    public float Scale { get; }

    /// <summary>
    /// The game's <c>TimeScale.IsPaused</c> (it toggles the <c>GameIsPaused</c> / <c>GameIsPlaying</c>
    /// tags). Never written by <c>ctx.Time</c>; reported so a caller can see a pause set elsewhere.
    /// </summary>
    public bool Paused { get; }

    /// <summary>
    /// <c>UnityEngine.Time.timeScale</c> in this process now. The game copies <see cref="Scale"/>
    /// into it once per frame (late update), so it lags a write by up to one frame.
    /// </summary>
    public float EngineScale { get; }

    /// <summary>Which <c>ctx.Time</c> lease governs (any plugin's), if any.</summary>
    public TimeScaleOverrideKind Override { get; }

    /// <summary><c>frozen (Freeze; engine 0, not paused)</c> or <c>0.25x (SlowMotion; engine 0.25, not paused)</c>.</summary>
    public override string ToString() =>
        TimeScalePolicy.Format(Scale)
        + " (" + (Override == TimeScaleOverrideKind.None ? "no lease" : Override.ToString())
        + "; engine " + EngineScale.ToString("0.###", CultureInfo.InvariantCulture)
        + (Paused ? ", paused" : ", not paused") + ")";
}

/// <summary>
/// An owner object that names itself in world-speed status: the Dev bridge and Core's photo-mode
/// pause hold leases under one of these instead of a plugin instance. A plugin's own leases are
/// labeled with its BepInEx GUID.
/// </summary>
public sealed class TimeScaleOwnerTag
{
    /// <summary>An owner labeled <paramref name="label"/>.</summary>
    public TimeScaleOwnerTag(string label) => Label = string.IsNullOrWhiteSpace(label) ? "unnamed" : label.Trim();

    /// <summary>What status prints as the owner.</summary>
    public string Label { get; }

    /// <summary>The label.</summary>
    public override string ToString() => Label;
}

/// <summary>One live world-speed lease, as the bridge's <c>time status</c> reports it.</summary>
public sealed class TimeScaleLeaseInfo
{
    /// <summary>A row with the given values.</summary>
    public TimeScaleLeaseInfo(string owner, TimeScaleOverrideKind kind, float scale, bool governs)
    {
        Owner = owner;
        Kind = kind;
        Scale = scale;
        Governs = governs;
    }

    /// <summary>The owning plugin's GUID, or an owner tag's label (<c>progenitor.dev bridge</c>, <c>photo mode: Name</c>).</summary>
    public string Owner { get; }

    /// <summary>Freeze or SlowMotion.</summary>
    public TimeScaleOverrideKind Kind { get; }

    /// <summary>The requested speed: 0 for a freeze.</summary>
    public float Scale { get; }

    /// <summary>True for the newest live lease: the one whose speed is applied.</summary>
    public bool Governs { get; }

    /// <summary>Rows from a stack snapshot, oldest first; the last one governs.</summary>
    public static List<TimeScaleLeaseInfo> FromStack<T>(IReadOnlyList<T> oldestFirst, Func<T, (string Owner, TimeScaleOverrideKind Kind, float Scale)> describe)
    {
        var rows = new List<TimeScaleLeaseInfo>(oldestFirst.Count);
        for (int i = 0; i < oldestFirst.Count; i++)
        {
            var (owner, kind, scale) = describe(oldestFirst[i]);
            rows.Add(new TimeScaleLeaseInfo(owner, kind, scale, i == oldestFirst.Count - 1));
        }
        return rows;
    }
}

/// <summary>
/// One holder's single world-speed lease, replaced in place: the Dev bridge's <c>time speed</c> /
/// <c>time freeze</c> keep exactly one lease, so repeated calls never pile up leases the bridge can
/// no longer reach. A replacement is taken <b>before</b> the old lease is released, so the world
/// never passes through normal speed between two bridge settings. Game-free so it is unit-tested.
/// </summary>
/// <typeparam name="T">The lease type.</typeparam>
public sealed class TimeScaleBridgeSlot<T> where T : class, IDisposable
{
    private readonly Func<T, bool> _isLive;
    private T? _held;

    /// <param name="isLive">False once the lease was released by another path (unload, world load).</param>
    public TimeScaleBridgeSlot(Func<T, bool> isLive) => _isLive = isLive ?? throw new ArgumentNullException(nameof(isLive));

    /// <summary>True while the slot's lease is live.</summary>
    public bool Holds => _held != null && _isLive(_held);

    /// <summary>Take a new lease with <paramref name="take"/>; on success release the old one. False (old kept) when refused.</summary>
    public bool Replace(Func<T?> take)
    {
        if (take == null) throw new ArgumentNullException(nameof(take));
        var next = take();
        if (next == null) return false;
        var old = _held;
        _held = next;
        if (old != null && !ReferenceEquals(old, next)) old.Dispose();
        return true;
    }

    /// <summary>Release the slot's lease, if any. True when one was live.</summary>
    public bool Release()
    {
        var old = _held;
        _held = null;
        if (old == null) return false;
        bool live = _isLive(old);
        old.Dispose();
        return live;
    }
}
