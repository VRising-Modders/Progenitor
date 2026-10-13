using System.Collections.Generic;

namespace Progenitor.Clock;

/// <summary>
/// The Dev bridge's <c>time</c> op over the same world-speed leases <c>ctx.Time.Freeze</c> /
/// <c>SlowMotion</c> take. Public only because Core grants <c>Progenitor.Dev</c> no
/// <c>InternalsVisibleTo</c>; not a plugin API (plugins use <c>ctx.Time</c>). Server only; main thread.
///
/// <para>The bridge holds <b>one</b> lease, owned by <see cref="BridgeOwner"/>
/// (<c>progenitor.dev bridge</c> in status), exactly like a plugin would: it stacks with plugin
/// leases under the same newest-governs rule, <see cref="Release"/> hands the world back to the
/// previous lease or to the speed from before the first lease (normally 1), and a Core unload,
/// world load or Core uninstall releases it like any other lease. A new <see cref="SetSpeed"/> or
/// <see cref="Freeze"/> replaces the bridge's lease after the new one is taken
/// (<see cref="TimeScaleBridgeSlot{T}"/>), so the bridge never holds more than one.</para>
/// </summary>
public static class TimeScaleBridge
{
    /// <summary>The owner the bridge's lease is held under.</summary>
    public static readonly TimeScaleOwnerTag BridgeOwner = new("progenitor.dev bridge");

    private static readonly TimeScaleBridgeSlot<TimeScaleRuntime.Lease> Slot = new(l => !l.Released);

    /// <summary>True while the bridge's own lease is live.</summary>
    public static bool Holds => Slot.Holds;

    /// <summary>The world speed now (null with <paramref name="why"/> when the server world or singleton is not ready).</summary>
    public static TimeScaleReading? Read(out string why)
    {
        if (!Plugin.IsServerProcess)
        {
            why = "world speed is server-side: send this op to the server bridge (27342).";
            return null;
        }
        return TimeScaleRuntime.Read(Worlds.Server, out why);
    }

    /// <summary>Every live lease (any owner), oldest first; the last one governs.</summary>
    public static IReadOnlyList<TimeScaleLeaseInfo> Leases() =>
        Plugin.IsServerProcess ? TimeScaleRuntime.Leases() : new List<TimeScaleLeaseInfo>();

    /// <summary>
    /// Hold the world at <paramref name="scale"/> (validated like <c>SlowMotion</c>: [0.001, 1]).
    /// False with <paramref name="why"/> when refused; the bridge's previous lease is then kept.
    /// </summary>
    public static bool SetSpeed(float scale, out string why)
    {
        TimeScalePolicy.RequireSlowMotion(scale, "value");
        return Take(TimeScaleOverrideKind.SlowMotion, scale, out why);
    }

    /// <summary>Hold the world frozen (speed 0). False with <paramref name="why"/> when refused.</summary>
    public static bool Freeze(out string why) => Take(TimeScaleOverrideKind.Freeze, 0f, out why);

    /// <summary>Drop the bridge's own lease. True when one was live. Leases other owners hold are untouched.</summary>
    public static bool Release() => Slot.Release();

    private static bool Take(TimeScaleOverrideKind kind, float scale, out string why)
    {
        if (!Plugin.IsServerProcess)
        {
            why = "world speed is server-side: send this op to the server bridge (27342).";
            return false;
        }
        string refused = "";
        bool ok = Slot.Replace(() => TimeScaleRuntime.Take(Worlds.Server, kind, scale, BridgeOwner, out refused));
        why = refused;
        return ok;
    }
}
