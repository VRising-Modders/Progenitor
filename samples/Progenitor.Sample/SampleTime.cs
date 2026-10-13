using System;
using System.Globalization;

#if !PROGENITOR_TESTS
using Progenitor.Commands;
#endif

namespace Progenitor.Sample;

/// <summary>
/// <c>.time [status|set &lt;hour&gt;|hold &lt;hour&gt; [bloodmoon]|release|resume]</c> — <c>ctx.Time</c> on its own
/// (<c>docs/modules.md</c> "Time of day"). Built for filming: <c>.time hold 0</c> freezes the
/// world at midnight so clips taken minutes apart light the same way (never on a blood moon night;
/// <c>.time hold 1 bloodmoon</c> asks for one); <c>.time release</c> gives
/// the clock back to the game.
///
/// <para>One override at a time per this command: a new <c>set</c>/<c>hold</c> is taken first and
/// the previous one released only once the new one is live, so the clock never falls back to the
/// world's own time in between, and a refused request leaves the previous override in place. The handle is also <c>ctx.Track</c>ed by <c>ctx.Time</c>, so unloading
/// the Sample releases it even if nobody types <c>.time release</c>.</para>
///
/// Parsing and every reply line are game-free so the test project compiles this file.
/// </summary>
internal static class SampleTime
{
    /// <summary>Log/grep area for this feature: <c>Sample time: …</c>.</summary>
    internal const string Area = "time";

    internal const string UsageReply = "Usage: .time [status|set <hour>|hold <hour> [bloodmoon]|release|resume]  (hour 0-23.99; 23.5 is 23:30)";

    /// <summary>What the caller asked for.</summary>
    internal enum Kind
    {
        Status = 0,
        Set = 1,
        Hold = 2,
        Release = 3,
        Resume = 4,
    }

    /// <summary>
    /// Parse <c>.time</c> arguments. <paramref name="hour"/> is required for set/hold and must be in
    /// [0, 24); it is ignored otherwise.
    /// </summary>
    internal static bool TryParse(string? what, string? hourText, out Kind kind, out double hour) =>
        TryParse(what, hourText, "", out kind, out hour, out _);

    /// <summary>
    /// <see cref="TryParse(string?, string?, out Kind, out double)"/> plus the hold option:
    /// <c>bloodmoon</c> after a hold's hour asks for the blood moon night. Any other option, or an
    /// option on anything but <c>hold</c>, is refused.
    /// </summary>
    internal static bool TryParse(string? what, string? hourText, string? optionText, out Kind kind, out double hour, out bool bloodMoon)
    {
        string option = (optionText ?? "").Trim().ToLowerInvariant();
        bloodMoon = option == "bloodmoon";
        if (!TryParseCore(what, hourText, out kind, out hour)) return false;
        if (option.Length == 0) return true;
        return bloodMoon && kind == Kind.Hold;
    }

    private static bool TryParseCore(string? what, string? hourText, out Kind kind, out double hour)
    {
        hour = 0;
        kind = Kind.Status;
        switch ((what ?? "").Trim().ToLowerInvariant())
        {
            case "":
            case "status": kind = Kind.Status; return true;
            case "release": kind = Kind.Release; return true;
            case "resume": kind = Kind.Resume; return true;
            case "set": kind = Kind.Set; break;
            case "hold": kind = Kind.Hold; break;
            default: return false;
        }
        return double.TryParse((hourText ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out hour)
            && !double.IsNaN(hour) && hour >= 0 && hour < 24;
    }

    internal static string StatusReply(string reading) => "Time: " + reading;

    internal static string TookReply(Kind kind, double hour, string reading) => TookReply(kind, hour, false, reading);

    internal static string TookReply(Kind kind, double hour, bool bloodMoon, string reading) =>
        (kind == Kind.Hold ? (bloodMoon ? "Holding the blood moon night at " : "Holding ") : "Moved the clock to ")
        + Progenitor.Clock.TimeOfDayMath.FormatHour(hour)
        + (kind == Kind.Hold ? " until .time release." : "; it keeps running. .time release returns the world's own time.")
        + " Now: " + reading;

    internal const string RefusedReply = "Refused; the previous .time override (if any) is unchanged.";
    internal const string ReleasedReply = "Released. The game runs the clock again (unless another plugin holds it).";
    internal const string NothingToReleaseReply = "No .time override is live; nothing released.";
    internal const string AlreadyReleasedReply = "The .time override had already ended (plugin reload or world load); nothing released.";

#if !PROGENITOR_TESTS
    private static Progenitor.Clock.TimeOfDayHandle? _lease;

    internal static void HandleCommand(ICommandContext ctx, Kind kind, double hour, bool bloodMoon = false)
    {
        var plugin = Plugin.Active;
        if (plugin == null)
        {
            ctx.Reply("Sample plugin context is not available.");
            return;
        }
        var time = plugin.Time;
        switch (kind)
        {
            case Kind.Status:
                ctx.Reply(StatusReply(time.GetTimeOfDay()?.ToString() ?? "not ready"));
                return;
            case Kind.Release:
                if (_lease == null) { ctx.Reply(NothingToReleaseReply); return; }
                bool wasLive = _lease.IsLive;
                _lease.Dispose();
                _lease = null;
                ctx.Reply(wasLive ? ReleasedReply : AlreadyReleasedReply);
                SampleLog.Info(Area, wasLive ? "released" : "release: nothing was live");
                return;
            case Kind.Resume:
                ctx.Reply(time.ResumeWorldClock() ? "World clock running." : "Resume refused; see the server log.");
                return;
        }
        // Take the new override before releasing the old one. A refused request returns a handle
        // that was never live; the previous override (if any) stays in place.
        var next = kind == Kind.Hold ? time.HoldTimeOfDay(hour, bloodMoon) : time.SetTimeOfDay(hour);
        if (!next.IsLive)
        {
            ctx.Reply(RefusedReply + " " + next.RefusedReason);
            return;
        }
        _lease?.Dispose();
        _lease = next;
        string reading = time.GetTimeOfDay()?.ToString() ?? "not ready";
        ctx.Reply(TookReply(kind, hour, bloodMoon, reading));
        SampleLog.Info(Area, kind.ToString().ToLowerInvariant() + " " + hour.ToString(CultureInfo.InvariantCulture) + " | " + reading);
    }
#endif
}
