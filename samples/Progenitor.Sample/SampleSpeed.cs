using System;
using System.Globalization;

#if !PROGENITOR_TESTS
using Progenitor.Commands;
#endif

namespace Progenitor.Sample;

/// <summary>
/// <c>.speed [status|freeze|slow &lt;scale&gt;|release]</c> — <c>ctx.Time.Freeze</c> and
/// <c>ctx.Time.SlowMotion</c> (<c>docs/modules.md</c> "World speed"). Built for filming:
/// <c>.speed freeze</c> stops the whole world (server and every connected client) so the camera can
/// move around a held moment; <c>.speed slow 0.25</c> runs it at quarter speed; <c>.speed release</c>
/// gives normal speed back.
///
/// <para>One lease at a time per this command: a new <c>freeze</c>/<c>slow</c> is taken first and
/// the previous one released only once the new one is live, so the world never snaps back to full
/// speed in between, and a refused request leaves the previous lease in place. The handle is also
/// <c>ctx.Track</c>ed by <c>ctx.Time</c>, so unloading the Sample releases it.</para>
///
/// Parsing and every reply line are game-free so the test project compiles this file.
/// </summary>
internal static class SampleSpeed
{
    /// <summary>Log/grep area for this feature: <c>Sample speed: …</c>.</summary>
    internal const string Area = "speed";

    internal const string UsageReply = "Usage: .speed [status|freeze|slow <scale>|release]  (scale 0.001-1; 0.25 is quarter speed)";

    /// <summary>What the caller asked for.</summary>
    internal enum Kind
    {
        Status = 0,
        Freeze = 1,
        Slow = 2,
        Release = 3,
    }

    /// <summary>Parse <c>.speed</c> arguments. <paramref name="scale"/> is required for <c>slow</c> and must be in [0.001, 1].</summary>
    internal static bool TryParse(string? what, string? scaleText, out Kind kind, out float scale)
    {
        scale = 0f;
        kind = Kind.Status;
        switch ((what ?? "").Trim().ToLowerInvariant())
        {
            case "":
            case "status": kind = Kind.Status; return true;
            case "freeze": kind = Kind.Freeze; return true;
            case "release": kind = Kind.Release; return true;
            case "slow": kind = Kind.Slow; break;
            default: return false;
        }
        return float.TryParse((scaleText ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out scale)
            && !float.IsNaN(scale)
            && scale >= Progenitor.Clock.TimeScalePolicy.MinSlowMotion
            && scale <= Progenitor.Clock.TimeScalePolicy.MaxSlowMotion;
    }

    internal static string StatusReply(string reading) => "Speed: " + reading;

    internal static string TookReply(Kind kind, float scale) =>
        (kind == Kind.Freeze
            ? "World frozen (server and every connected client)."
            : "World at " + Progenitor.Clock.TimeScalePolicy.Format(scale) + " speed.")
        + " .speed release returns normal speed.";

    internal const string RefusedReply = "Refused; the previous .speed lease (if any) is unchanged.";
    internal const string ReleasedReply = "Released. Normal speed returns (unless another plugin holds a lease).";
    internal const string NothingToReleaseReply = "No .speed lease is live; nothing released.";
    internal const string AlreadyReleasedReply = "The .speed lease had already ended (plugin reload or world load); nothing released.";

#if !PROGENITOR_TESTS
    private static Progenitor.Clock.TimeScaleHandle? _lease;

    internal static void HandleCommand(ICommandContext ctx, Kind kind, float scale)
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
                ctx.Reply(StatusReply(time.GetTimeScale()?.ToString() ?? "not ready"));
                return;
            case Kind.Release:
                if (_lease == null) { ctx.Reply(NothingToReleaseReply); return; }
                bool wasLive = _lease.IsLive;
                _lease.Dispose();
                _lease = null;
                ctx.Reply(wasLive ? ReleasedReply : AlreadyReleasedReply);
                SampleLog.Info(Area, wasLive ? "released" : "release: nothing was live");
                return;
        }
        // Take the new lease before releasing the old one; a refused request leaves the old one.
        var next = kind == Kind.Freeze ? time.Freeze() : time.SlowMotion(scale);
        if (!next.IsLive)
        {
            ctx.Reply(RefusedReply + " " + next.RefusedReason);
            return;
        }
        _lease?.Dispose();
        _lease = next;
        ctx.Reply(TookReply(kind, scale));
        SampleLog.Info(Area, kind.ToString().ToLowerInvariant() + " " + scale.ToString(CultureInfo.InvariantCulture)
            + " | " + (time.GetTimeScale()?.ToString() ?? "not ready"));
    }
#endif
}
