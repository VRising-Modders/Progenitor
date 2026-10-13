using System;

namespace Progenitor.Ui;

/// <summary>Where a press-and-hold is (<see cref="HoldMachine"/>).</summary>
public enum HoldPhase
{
    /// <summary>Nothing pressed (or the last hold ended and its button came up).</summary>
    Idle,

    /// <summary>A target is held and the ring is filling.</summary>
    Charging,

    /// <summary>The ring filled and the hold fired; waiting for the button to come up.</summary>
    Completed,
}

/// <summary>Why a hold ended without firing.</summary>
public enum HoldCancel
{
    /// <summary>It did not cancel.</summary>
    None,

    /// <summary>The button came up before the ring filled.</summary>
    Released,

    /// <summary>The pointer left the target (a swatch, the map).</summary>
    Left,

    /// <summary>The surface the hold was on closed, was hidden or was rebuilt.</summary>
    Closed,

    /// <summary>The feature was switched off mid-hold.</summary>
    Disabled,

    /// <summary>The plugin is unloading.</summary>
    Unloaded,
}

/// <summary>What one <see cref="HoldMachine.Tick"/> asks the view to do.</summary>
public readonly struct HoldStep
{
    /// <summary>Build a step (the machine does; tests may).</summary>
    public HoldStep(HoldPhase phase, float progress, bool fire, HoldCancel canceled)
    {
        Phase = phase;
        Progress = progress;
        Fire = fire;
        Cancelled = canceled;
    }

    /// <summary>The phase after this tick.</summary>
    public HoldPhase Phase { get; }

    /// <summary>Ring fill, 0 to 1. 1 on the completing tick and while completed.</summary>
    public float Progress { get; }

    /// <summary>True on exactly one tick per hold: the one the ring filled on. Act then.</summary>
    public bool Fire { get; }

    /// <summary>Set on the tick a charging hold was canceled; the ring goes away.</summary>
    public HoldCancel Cancelled { get; }
}

/// <summary>
/// The press-and-hold state machine behind a charge ring (Core's Dye all swatches, the debug tools' map
/// teleport). Game-free: the caller feeds it presses, releases, leaves and a clock, and draws what
/// <see cref="Tick"/> returns (<see cref="HoldRing"/> on the client).
///
/// <list type="bullet">
/// <item>An accepted press on a target starts a charge (the caller decides what is acceptable).</item>
/// <item>The ring fills over <see cref="HoldSeconds"/>; the tick it fills on returns
/// <see cref="HoldStep.Fire"/> once.</item>
/// <item>Releasing, leaving the target, the surface closing, the feature turning off or unloading
/// before that cancels with nothing done. After the fire, a release just returns to idle.</item>
/// <item>One hold at a time: a second press while one is live is ignored.</item>
/// </list>
/// Not thread-safe; drive it from the main thread.
/// </summary>
public class HoldMachine
{
    /// <summary>Shortest accepted hold, seconds.</summary>
    public const double MinHoldSeconds = 0.3;

    /// <summary>Longest accepted hold, seconds.</summary>
    public const double MaxHoldSeconds = 5.0;

    /// <summary>The default hold.</summary>
    public const double DefaultHoldSeconds = 1.0;

    private double _start;
    private double _hold = DefaultHoldSeconds;
    private HoldCancel _pendingCancel;

    /// <summary>The current phase.</summary>
    public HoldPhase Phase { get; private set; } = HoldPhase.Idle;

    /// <summary>The target the live hold is on, or -1.</summary>
    public int Target { get; private set; } = -1;

    /// <summary>The hold length the live (or next) charge uses.</summary>
    public double HoldSeconds => _hold;

    /// <summary>Whether the live hold has already fired.</summary>
    public bool Fired { get; private set; }

    /// <summary>Clamp a configured hold time into the accepted range; NaN and infinities give the default.</summary>
    public static double ClampHold(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds)) return DefaultHoldSeconds;
        return Math.Min(MaxHoldSeconds, Math.Max(MinHoldSeconds, seconds));
    }

    /// <summary>
    /// A press went down on <paramref name="target"/> (any id ≥ 0 the caller uses to tell targets
    /// apart; a surface with one target uses 0). Starts a charge when <paramref name="accept"/> is
    /// true and no hold is live. Returns whether a charge started.
    /// </summary>
    public bool Press(int target, bool accept, double holdSeconds, double now)
    {
        if (!accept || target < 0) return false;
        if (Phase != HoldPhase.Idle) return false;
        _hold = ClampHold(holdSeconds);
        _start = now;
        Fired = false;
        _pendingCancel = HoldCancel.None;
        Target = target;
        Phase = HoldPhase.Charging;
        return true;
    }

    /// <summary>The button came up. Cancels a charge on that target; ends a completed hold.</summary>
    public void Release(int target)
    {
        if (target != Target) return;
        End(HoldCancel.Released);
    }

    /// <summary>The pointer left a target. Cancels a charge on it; a completed hold waits for release.</summary>
    public void Leave(int target)
    {
        if (target != Target || Phase != HoldPhase.Charging) return;
        End(HoldCancel.Left);
    }

    /// <summary>Cancel whatever is live for <paramref name="reason"/> (closed, disabled, unloading).</summary>
    public void Cancel(HoldCancel reason)
    {
        if (Phase == HoldPhase.Idle) return;
        End(reason == HoldCancel.None ? HoldCancel.Closed : reason);
    }

    /// <summary>
    /// Advance to <paramref name="now"/>. <paramref name="targetVisible"/> false (surface closed,
    /// target hidden or rebuilt) and <paramref name="enabled"/> false cancel a charge. Returns the ring
    /// fill, the one-shot fire and, on the tick a charge ended, why.
    /// </summary>
    public HoldStep Tick(double now, bool targetVisible, bool enabled)
    {
        if (Phase == HoldPhase.Charging)
        {
            if (!enabled) End(HoldCancel.Disabled);
            else if (!targetVisible) End(HoldCancel.Closed);
        }
        else if (Phase == HoldPhase.Completed && !targetVisible)
        {
            // The ring belongs to the target; with it gone there is nothing left to wait for.
            End(HoldCancel.None);
        }

        var canceled = _pendingCancel;
        _pendingCancel = HoldCancel.None;

        switch (Phase)
        {
            case HoldPhase.Charging:
            {
                double elapsed = Math.Max(0, now - _start);
                if (elapsed + 1e-9 >= _hold)
                {
                    Phase = HoldPhase.Completed;
                    Fired = true;
                    return new HoldStep(Phase, 1f, true, HoldCancel.None);
                }

                return new HoldStep(Phase, (float)(elapsed / _hold), false, HoldCancel.None);
            }
            case HoldPhase.Completed:
                return new HoldStep(Phase, 1f, false, HoldCancel.None);
            default:
                return new HoldStep(HoldPhase.Idle, 0f, false, canceled);
        }
    }

    private void End(HoldCancel reason)
    {
        if (Phase == HoldPhase.Charging && reason != HoldCancel.None) _pendingCancel = reason;
        Phase = HoldPhase.Idle;
        Target = -1;
        Fired = false;
    }
}
