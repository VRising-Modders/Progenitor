using System;
using Progenitor.Plugins;

namespace Progenitor.Clock;

/// <summary>
/// <see cref="PluginContext.Time"/>: the world's time of day, server-side. Every member throws
/// <see cref="InvalidOperationException"/> on the client (a programming error, like
/// <c>ctx.Abilities</c>); ordinary refusals on the server are logged and return an inert handle
/// or false. Main thread (a command, <c>Plugin.Frame</c> or an event handler).
///
/// <para><b>Connected clients follow.</b> An override stops the game's own clock
/// (<c>SyncedServerDebugSettings.DayNightCycleDisabled</c>, a replicated setting) and writes the
/// server's <c>DayNightCycle.Time</c> and its replicated copy <c>DayNightCycle.ServerTime</c>;
/// with the setting on, each client copies that replicated value into its own clock, so its
/// sun, shadows and lighting follow (from the shared game code; not live-verified). Nothing else —
/// cooldowns, buffs, timers, the game clock <c>ServerTime</c> — is moved. Sun damage should follow
/// the new hour (inferred from the sun-damage window; not live-verified).</para>
///
/// <para><b>When to take one.</b> Overrides are refused until the current world has finished
/// loading, and they do not survive a world load (a new epoch drops them). Take them from a
/// <c>WorldLoaded</c> handler (<c>ctx.Events</c>) or later, e.g. a command.</para>
///
/// <para><b>An admin's own day/night toggle</b> (the game's <c>DayNightCycleDisabled</c> debug
/// setting) is overridden while an override lives — the flag is re-asserted every frame — and on
/// release it is put back to the value recorded before the first override, replacing whatever
/// the admin set in between.</para>
///
/// <para><b>Overrides stack</b> across plugins: the newest live one governs; releasing it hands
/// the clock back to the previous one; releasing the last restores the setting to what it was and
/// the game's clock resumes where the world would have been without any override. Handles are
/// <see cref="PluginContext.Track"/>ed, so unload releases them; a new world epoch (save reload)
/// and Core uninstall release them too.</para>
///
/// <para><b>Saves.</b> The game saves that setting with the world. The first override records the
/// prior value in a plain per-save marker (<see cref="TimeOfDayPolicy"/>), cleared after a
/// successful restore; after a world load a marker with no live override is resolved by writing
/// the prior value back. <see cref="ResumeWorldClock"/> is the manual fallback.</para>
///
/// <para><b>World speed.</b> <see cref="Freeze"/> and <see cref="SlowMotion"/> are a separate
/// stack of leases over the game's replicated <c>TimeScale</c> setting: everything simulated, on
/// the server and every connected client, stops or slows — including the game clock and every
/// timer, which the time-of-day overrides above deliberately leave alone. Same tracking, epoch,
/// uninstall and per-save marker rules (<see cref="TimeScalePolicy"/>). Not live-verified.</para>
/// </summary>
public sealed class PluginTime
{
    private readonly PluginContext _ctx;

    internal PluginTime(PluginContext ctx) =>
        _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));

    /// <summary>
    /// The world clock now: day number, hour (fraction = minutes), day or night, the configured
    /// day length and sunrise/sunset hours, and whether an override governs it. Null (logged) when
    /// the world or its <c>DayNightCycle</c> singleton is not ready.
    /// </summary>
    public TimeOfDayReading? GetTimeOfDay()
    {
        RequireServer("GetTimeOfDay");
        var reading = TimeOfDayRuntime.Read(_ctx.World, out string why);
        if (reading == null) _ctx.Log.LogDebug("Time.GetTimeOfDay: " + why);
        return reading;
    }

    /// <summary>
    /// Move the clock to <paramref name="hour"/> (0 ≤ hour &lt; 24; 23.5 is 23:30) on the current
    /// game day and let it keep running from there, until the returned handle is disposed or the
    /// plugin unloads; then the world's own time comes back. The day number is never changed, so
    /// an earlier hour moves the clock back within the day. Throws
    /// <see cref="ArgumentOutOfRangeException"/> for an hour outside that range; returns an inert
    /// handle (<see cref="TimeOfDayHandle.IsLive"/> false, reason logged and in
    /// <see cref="TimeOfDayHandle.RefusedReason"/>) when the world is not ready.
    /// </summary>
    public TimeOfDayHandle SetTimeOfDay(double hour) => Take(TimeOfDayOverrideKind.Shift, hour, "SetTimeOfDay");

    /// <summary>
    /// Freeze the clock at <paramref name="hour"/> on the current game day until the returned
    /// handle is disposed or the plugin unloads. The sun does not move and does not step back: the
    /// game stops advancing the clock and the value is written once. Same range and refusals as
    /// <see cref="SetTimeOfDay"/>.
    /// </summary>
    public TimeOfDayHandle HoldTimeOfDay(double hour) => HoldTimeOfDay(hour, false);

    /// <summary>
    /// <see cref="HoldTimeOfDay(double)"/> with control over the blood moon. The game's blood moon
    /// is the night from sunset of day <c>NextBloodMoonDay</c> to sunrise of the next day
    /// (<see cref="TimeOfDayMath.IsBloodMoonNight"/>).
    ///
    /// <para><paramref name="bloodMoon"/> false (and the 1-argument overload): never hold a blood
    /// moon night. When the hour on the current day falls inside one, the same hour one day earlier
    /// is held instead (the reading's <c>Day</c> is one lower during the hold). Never a day later: on
    /// day N+2 the server re-rolls the saved schedule.</para>
    ///
    /// <para><paramref name="bloodMoon"/> true: hold the blood moon night at that hour — day N for an
    /// evening hour, day N+1 before sunrise. Refused (handle not live, reason given) for a daytime
    /// hour, or while the schedule is uninitialized (<c>NextBloodMoonDay</c> 0). The schedule is not
    /// changed.</para>
    /// </summary>
    public TimeOfDayHandle HoldTimeOfDay(double hour, bool bloodMoon = false) =>
        Take(TimeOfDayOverrideKind.Hold, hour, bloodMoon ? "HoldTimeOfDay(bloodMoon)" : "HoldTimeOfDay", bloodMoon);

    /// <summary>True while this plugin has a live <see cref="SetTimeOfDay"/> or <see cref="HoldTimeOfDay"/>.</summary>
    public bool HasOverride
    {
        get
        {
            RequireServer("HasOverride");
            return TimeOfDayRuntime.HasOverride(_ctx.Owner);
        }
    }

    /// <summary>
    /// Restart a stopped world clock (clear <c>DayNightCycleDisabled</c>) when no override from any
    /// plugin is live. For a world saved during an override, or stopped by an admin. False (logged)
    /// while an override is live or when the world is not ready.
    /// </summary>
    public bool ResumeWorldClock()
    {
        RequireServer("ResumeWorldClock");
        bool ok = TimeOfDayRuntime.ResumeWorldClock(_ctx.World, out string why);
        if (!ok) _ctx.Log.LogWarning("Time.ResumeWorldClock: " + why);
        return ok;
    }

    // ---- world speed ---------------------------------------------------------------------------

    /// <summary>
    /// The world speed now: the game's <c>TimeScale</c> value (1 normal, 0 frozen), its pause flag,
    /// this process's <c>UnityEngine.Time.timeScale</c>, and which lease governs. Null (logged) when
    /// the world or the <c>TimeScale</c> singleton is not ready.
    /// </summary>
    public TimeScaleReading? GetTimeScale()
    {
        RequireServer("GetTimeScale");
        var reading = TimeScaleRuntime.Read(_ctx.World, out string why);
        if (reading == null) _ctx.Log.LogDebug("Time.GetTimeScale: " + why);
        return reading;
    }

    /// <summary>
    /// Stop the whole world — every unit, player, projectile, buff, cooldown and the game clock
    /// <c>ServerTime</c>, on the server and every connected client — until the returned handle is
    /// disposed or the plugin unloads; then the previous speed (normally 1) returns. Uses the game's
    /// own replicated world-speed setting (<c>TimeScale.Value</c> = 0), which each process copies into
    /// <c>UnityEngine.Time.timeScale</c>, so animation, VFX and particles on clients stop too. The
    /// game's free camera keeps flying on real time while the value is exactly 0; plugin frames,
    /// commands and <c>Plugin.Frame</c> keep running. Returns an inert handle
    /// (<see cref="TimeScaleHandle.IsLive"/> false, reason logged and in
    /// <see cref="TimeScaleHandle.RefusedReason"/>) when the world is not ready. Not live-verified.
    /// </summary>
    public TimeScaleHandle Freeze() => TakeScale(TimeScaleOverrideKind.Freeze, 0f, "Freeze");

    /// <summary>
    /// Run the whole world at <paramref name="scale"/> × normal speed (0.25 is quarter speed) until the
    /// returned handle is disposed or the plugin unloads. Same mechanism, reach and refusals as
    /// <see cref="Freeze"/>. Throws <see cref="ArgumentOutOfRangeException"/> outside
    /// [<see cref="TimeScalePolicy.MinSlowMotion"/>, <see cref="TimeScalePolicy.MaxSlowMotion"/>]
    /// (clients receive the value rounded to 3 decimals). Not live-verified.
    /// </summary>
    public TimeScaleHandle SlowMotion(float scale)
    {
        TimeScalePolicy.RequireSlowMotion(scale);
        return TakeScale(TimeScaleOverrideKind.SlowMotion, scale, "SlowMotion");
    }

    /// <summary>True while this plugin has a live <see cref="Freeze"/> or <see cref="SlowMotion"/>.</summary>
    public bool HasTimeScaleOverride
    {
        get
        {
            RequireServer("HasTimeScaleOverride");
            return TimeScaleRuntime.HasOverride(_ctx.Owner);
        }
    }

    private TimeScaleHandle TakeScale(TimeScaleOverrideKind kind, float scale, string op)
    {
        RequireServer(op);
        var lease = TimeScaleRuntime.Take(_ctx.World, kind, scale, _ctx.Owner, out string why);
        if (lease == null)
        {
            _ctx.Log.LogWarning("Time." + op + ": " + why);
            return new TimeScaleHandle(null, kind, scale, why);
        }
        var handle = new TimeScaleHandle(lease, kind, scale, null);
        _ctx.Track(handle);
        lease.AfterRelease = _ => _ctx.Untrack(handle);
        if (lease.Released) _ctx.Untrack(handle);
        return handle;
    }

    private TimeOfDayHandle Take(TimeOfDayOverrideKind kind, double hour, string op, bool bloodMoon = false)
    {
        RequireServer(op);
        var lease = TimeOfDayRuntime.Take(_ctx.World, kind, hour, _ctx.Owner, out string why, bloodMoon);
        if (lease == null)
        {
            _ctx.Log.LogWarning("Time." + op + ": " + why);
            return new TimeOfDayHandle(null, kind, hour, why);
        }
        var handle = new TimeOfDayHandle(lease, kind, hour, null);
        _ctx.Track(handle);
        // Released by any path (dispose, world load, uninstall): drop it from the tracked list so
        // a long session of set/release does not grow it.
        lease.AfterRelease = _ => _ctx.Untrack(handle);
        if (lease.Released) _ctx.Untrack(handle);
        return handle;
    }

    private void RequireServer(string op)
    {
        _ctx.EnsureAttached();
        if (!_ctx.IsServer)
            throw new InvalidOperationException("ctx.Time." + op + " is server-only.");
    }
}

/// <summary>
/// One <c>ctx.Time</c> override. Dispose it to release the override. Check <see cref="IsLive"/>
/// after taking one: a refused request returns a handle that was never live.
/// </summary>
public sealed class TimeOfDayHandle : IDisposable
{
    private readonly TimeOfDayRuntime.Lease? _lease;

    internal TimeOfDayHandle(TimeOfDayRuntime.Lease? lease, TimeOfDayOverrideKind kind, double hour, string? refusedReason)
    {
        _lease = lease;
        Kind = kind;
        Hour = hour;
        RefusedReason = refusedReason;
    }

    /// <summary><see cref="TimeOfDayOverrideKind.Shift"/> (<c>SetTimeOfDay</c>) or <see cref="TimeOfDayOverrideKind.Hold"/>.</summary>
    public TimeOfDayOverrideKind Kind { get; }

    /// <summary>The requested hour.</summary>
    public double Hour { get; }

    /// <summary>True until it is released (dispose, plugin unload, world load, Core uninstall). False for a refused request.</summary>
    public bool IsLive => _lease != null && !_lease.Released;

    /// <summary>Why the request was refused; null when it was taken.</summary>
    public string? RefusedReason { get; }

    /// <summary>Release this override. Idempotent.</summary>
    public void Dispose() => _lease?.Dispose();
}

/// <summary>
/// One <c>ctx.Time</c> world-speed lease (<c>Freeze</c> or <c>SlowMotion</c>). Dispose it to release
/// the lease. Check <see cref="IsLive"/> after taking one: a refused request returns a handle that
/// was never live.
/// </summary>
public sealed class TimeScaleHandle : IDisposable
{
    private readonly TimeScaleRuntime.Lease? _lease;

    internal TimeScaleHandle(TimeScaleRuntime.Lease? lease, TimeScaleOverrideKind kind, float scale, string? refusedReason)
    {
        _lease = lease;
        Kind = kind;
        Scale = scale;
        RefusedReason = refusedReason;
    }

    /// <summary><see cref="TimeScaleOverrideKind.Freeze"/> or <see cref="TimeScaleOverrideKind.SlowMotion"/>.</summary>
    public TimeScaleOverrideKind Kind { get; }

    /// <summary>The requested speed: 0 for a freeze.</summary>
    public float Scale { get; }

    /// <summary>True until it is released (dispose, plugin unload, world load, Core uninstall). False for a refused request.</summary>
    public bool IsLive => _lease != null && !_lease.Released;

    /// <summary>Why the request was refused; null when it was taken.</summary>
    public string? RefusedReason { get; }

    /// <summary>Release this lease. Idempotent.</summary>
    public void Dispose() => _lease?.Dispose();
}
