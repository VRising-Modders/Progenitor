using System;

namespace Progenitor.Systems;

/// <summary>
/// Decides when the default After/Before Simulation virtual-system anchors may fall back to
/// bootstrap systems. Counts Plugin.Frame only after the side's world exists, resets the
/// countdown on WorldReady, and yields back to <c>SimulationSystemGroup</c> if that group later
/// fires — even after fallback already engaged. No Unity, BepInEx, or game types; the same class
/// is compiled into the test project so the state machine can be proven without the interop.
/// </summary>
internal sealed class DefaultAnchorWatch
{
    /// <summary>
    /// Plugin.Frame ticks to wait after the world exists before assuming
    /// <c>SimulationSystemGroup</c> does not go through <c>SystemBase.Update</c> on this build.
    /// </summary>
    public const int FallbackTicks = 600;

    /// <summary>Which default-simulation seam is live. <see cref="VirtualSystems.Describe"/> reports this.</summary>
    public enum LiveAnchor
    {
        /// <summary>World may still be missing, or the group has not fired and the countdown has not elapsed.</summary>
        Pending,
        /// <summary><c>SimulationSystemGroup</c> has ticked; default callbacks run After/Before that group.</summary>
        Group,
        /// <summary>Countdown elapsed with no group tick; default callbacks run After/Before the bootstrap systems.</summary>
        Bootstrap,
    }

    private readonly object _gate = new();
    private readonly int _fallbackTicks;
    private int _framesAfterWorld;
    private bool _worldExists;
    private bool _receivedWorldReady;
    private bool _groupFired;
    private bool _useFallback;

    /// <summary>Uses <see cref="FallbackTicks"/> (the production countdown).</summary>
    public DefaultAnchorWatch() : this(FallbackTicks) { }

    /// <summary>
    /// Production code passes the default; tests pass a smaller tick count so the engage path
    /// does not need 600 iterations to prove the transition.
    /// </summary>
    public DefaultAnchorWatch(int fallbackTicks)
    {
        if (fallbackTicks < 1)
            throw new ArgumentOutOfRangeException(nameof(fallbackTicks), "Fallback tick count must be at least 1.");
        _fallbackTicks = fallbackTicks;
    }

    /// <summary>Live default-simulation anchor. Describe() must report this, not a sticky fallback flag.</summary>
    public LiveAnchor Live
    {
        get
        {
            lock (_gate)
            {
                if (_useFallback) return LiveAnchor.Bootstrap;
                if (_groupFired) return LiveAnchor.Group;
                return LiveAnchor.Pending;
            }
        }
    }

    /// <summary>Status-op label: <c>pending</c>, <c>group</c>, or <c>bootstrap</c>.</summary>
    public string DescribeLabel()
    {
        return Live switch
        {
            LiveAnchor.Bootstrap => "bootstrap",
            LiveAnchor.Group => "group",
            _ => "pending",
        };
    }

    /// <summary>Frames counted toward fallback since the last WorldReady (or first sighting of the world).</summary>
    public int FramesAfterWorld
    {
        get { lock (_gate) return _framesAfterWorld; }
    }

    /// <summary>True when the last <see cref="NotifyFrame"/> saw a world, or <see cref="NotifyWorldReady"/> has run.</summary>
    public bool WorldExists
    {
        get { lock (_gate) return _worldExists; }
    }

    /// <summary>True after <see cref="NotifyWorldReady"/>; independent of a later world teardown.</summary>
    public bool ReceivedWorldReady
    {
        get { lock (_gate) return _receivedWorldReady; }
    }

    /// <summary>True once <c>SimulationSystemGroup</c> has updated at least once this process.</summary>
    public bool GroupFired
    {
        get { lock (_gate) return _groupFired; }
    }

    /// <summary>True while default callbacks should run from the bootstrap relays, not the group.</summary>
    public bool UseFallback
    {
        get { lock (_gate) return _useFallback; }
    }

    /// <summary>
    /// The side's Server / Client world exists. Starts (or restarts) the fallback countdown from
    /// 0 so boot frames before WorldReady cannot burn the budget. Does not un-engage fallback or
    /// clear <see cref="GroupFired"/> — a rejoin still prefers the group as soon as it ticks.
    /// </summary>
    public void NotifyWorldReady()
    {
        lock (_gate)
        {
            _worldExists = true;
            _receivedWorldReady = true;
            _framesAfterWorld = 0;
        }
    }

    /// <summary>
    /// <c>SimulationSystemGroup</c> updated. Prefer it over bootstrap even if fallback already
    /// engaged.
    /// </summary>
    /// <returns>
    /// True when this call left fallback (caller must release bootstrap relays and log once).
    /// False when the group was already the live anchor, or fallback was never engaged.
    /// </returns>
    public bool NotifyGroupFired()
    {
        lock (_gate)
        {
            _groupFired = true;
            if (!_useFallback) return false;
            _useFallback = false;
            return true;
        }
    }

    /// <summary>
    /// One Plugin.Frame. <paramref name="worldExists"/> is whether <c>Worlds.Server</c> or
    /// <c>Worlds.Client</c> is created on this process. Frames before the world exists do not
    /// count; a missing world pauses the countdown rather than advancing it.
    /// </summary>
    /// <returns>
    /// True when this call engaged fallback (caller must install bootstrap relays and log once).
    /// Subsequent frames return false.
    /// </returns>
    public bool NotifyFrame(bool worldExists)
    {
        lock (_gate)
        {
            _worldExists = worldExists;
            if (_useFallback || _groupFired) return false;
            if (!worldExists) return false;
            if (++_framesAfterWorld < _fallbackTicks) return false;
            _useFallback = true;
            return true;
        }
    }
}
