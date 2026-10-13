using System;
using System.Collections.Generic;
using System.Globalization;
using Progenitor.WorldState;

#if !PROGENITOR_TESTS
using Progenitor.Commands;
#endif

namespace Progenitor.Sample;

/// <summary>
/// Job FSAMP, feature 1. <c>.epoch</c> — the world-load epoch (<c>docs/modules.md</c> "World",
/// <c>ctx.WorldState</c>) demonstrated on a handle this plugin actually kept.
///
/// <b>The rule being shown.</b> A V Rising save reload rebuilds every entity from index 0 at
/// version 1, so a retained <c>index:version</c> pair can pass <c>EntityManager.Exists</c> while
/// naming somebody else's entity. The fix is one integer: stamp <c>ctx.WorldState.LoadEpoch</c>
/// beside anything you keep past the frame, and drop it when
/// <c>ctx.WorldState.IsStale(stamped)</c> answers true. The Sample stamps the castle heart from
/// the <b>first</b> <c>.castle</c> of the session (see <see cref="SampleCastle"/>) and never
/// touches that entity again — <c>.epoch</c> only reports whether it would still be safe to.
///
/// <b>Three lines, two routes to one counter.</b> Line 1 is <c>ctx.WorldState.Describe()</c>;
/// line 2 is the verdict from <c>ctx.WorldState.IsStale(stamped)</c>; line 3
/// (<see cref="StaticLine"/>) reads the static <c>WorldEpoch.Current</c> and formats the pair
/// with <c>WorldEpoch.FormatStale</c> — the same wording <c>CastleHandle.DisabledReason</c> uses.
/// <c>ctx.WorldState</c> is a facade over those statics, and a helper with no
/// <c>PluginContext</c> in reach uses the statics directly.
///
/// <b>There is no <c>.epoch bump</c>, on purpose.</b> <c>WorldEpoch.Advance</c> is Core's report
/// that the game finished a world load, not a counter a plugin may nudge: it publishes
/// <c>WorldLoaded</c>, makes every <c>ICastleHandle</c> in the process stale and drops every
/// god-mode guard from the old epoch. Its only callers are <c>WorldLoadHook</c> and
/// <c>ClientWorldEpoch</c> (and <c>ResetForTests</c> in the suite). <see cref="StaticLine"/> says
/// so in the reply, which is the example this API needs — showing how to fake a world load would
/// teach the one thing the module exists to make trustworthy.
///
/// Parsing and every reply line are game-free so the test project compiles this file; only the
/// command entry point is behind <c>!PROGENITOR_TESTS</c>.
/// </summary>
internal static class SampleEpoch
{
    /// <summary>Log/grep area for this feature: <c>Sample epoch: …</c>.</summary>
    internal const string Area = "epoch";

    private static readonly object Gate = new();
    private static string _label = "";
    private static int _stampedEpoch;

    /// <summary>Reply when nothing has been stamped yet this session.</summary>
    internal const string NoStampReply =
        "No handle stamped yet - run .castle once (it stamps your castle heart), then .epoch.";

    /// <summary>
    /// Record a handle and the epoch it was read in. Called once per session by the first
    /// <c>.castle</c>; a later <c>.castle</c> does not overwrite it, because the point of the
    /// demo is to still be holding the <em>old</em> stamp after a save reload.
    /// </summary>
    internal static bool Stamp(string label, int epoch)
    {
        if (string.IsNullOrWhiteSpace(label) || epoch <= 0) return false;
        lock (Gate)
        {
            if (_stampedEpoch > 0) return false;
            _label = label;
            _stampedEpoch = epoch;
        }

        SampleLog.Info(Area, "stamped " + label + " in world load epoch " + N(epoch));
        return true;
    }

    /// <summary>True when a handle has been stamped this session.</summary>
    internal static bool HasStamp
    {
        get { lock (Gate) return _stampedEpoch > 0; }
    }

    /// <summary>The stamped label, or the empty string.</summary>
    internal static string Label
    {
        get { lock (Gate) return _label; }
    }

    /// <summary>The stamped epoch, or 0.</summary>
    internal static int StampedEpoch
    {
        get { lock (Gate) return _stampedEpoch; }
    }

    /// <summary>Forget the stamp. Unload does this; a new generation stamps again.</summary>
    internal static void Reset()
    {
        lock (Gate)
        {
            _label = "";
            _stampedEpoch = 0;
        }
    }

    /// <summary>
    /// The two <c>.epoch</c> reply lines: <c>ctx.WorldState.Describe()</c> verbatim, then the
    /// verdict on the stamped handle. <paramref name="isStale"/> is
    /// <c>ctx.WorldState.IsStale(stamped)</c> — the Sample never re-derives that comparison
    /// itself, because "stale" also covers a stamp from an earlier Core generation, which no
    /// epoch arithmetic in a plugin could see.
    /// </summary>
    internal static IReadOnlyList<string> Lines(string describe, string label, int stamped, bool isStale)
    {
        string first = string.IsNullOrEmpty(describe) ? "world: (unavailable)" : describe;
        if (stamped <= 0 || string.IsNullOrWhiteSpace(label))
            return new[] { first, NoStampReply };

        string verdict = isStale
            ? "Handle " + label + " was stamped in epoch " + N(stamped)
              + " and IsStale=True: the world was rebuilt (or Core swapped) since, so that entity id "
              + "names something else now. Drop it."
            : "Handle " + label + " was stamped in epoch " + N(stamped)
              + " and IsStale=False: still the same world load, so the entity id is still yours.";

        return new[] { first, verdict, StaticLine(stamped) };
    }

    /// <summary>
    /// The third line: the same two numbers straight off the static
    /// <c>Progenitor.WorldState.WorldEpoch</c>, in the shape Core itself prints them
    /// (<c>WorldEpoch.FormatStale</c>, the wording <c>CastleHandle.DisabledReason</c> and the
    /// god-mode drop line both use). It exists so a mod author sees the two routes are one
    /// counter: <c>ctx.WorldState</c> is a thin facade over these statics, and a module that has
    /// no <c>PluginContext</c> to hand — a static helper, a Harmony patch body — reads them
    /// directly instead of plumbing a context through.
    ///
    /// <para><b>Nothing in this Sample calls <c>WorldEpoch.Advance</c>, and no plugin should.</b>
    /// It is not a "bump the counter" utility: it is Core's report that the game finished a world
    /// load, and calling it publishes <c>WorldLoaded</c> to every subscriber, moves every
    /// <c>ICastleHandle</c> in the process to stale, and drops every god-mode guard stamped in the
    /// old epoch. A plugin calling it makes Core lie about an event that did not happen. The two
    /// callers are <c>WorldLoadHook</c>'s postfix on the server's load path and
    /// <c>ClientWorldEpoch</c> on the client (<c>World/WorldEpoch.cs:281</c>); tests are the
    /// third, through <c>ResetForTests</c>. There is deliberately no <c>.epoch bump</c> here —
    /// an example that showed how to fake a world load would teach the one thing this module
    /// exists to make trustworthy.</para>
    /// </summary>
    internal static string StaticLine(int stamped)
    {
        int current = WorldEpoch.Current;
        return "WorldEpoch.Current=" + N(current) + "; " + WorldEpoch.FormatStale(stamped, current)
            + ". Both are opaque generation-stamped tokens - compare them, never subtract them. "
            + "Only Core advances the counter (WorldLoadHook on the server, ClientWorldEpoch on "
            + "the client); a plugin that called Advance would be reporting a world load that "
            + "did not happen.";
    }

#if !PROGENITOR_TESTS
    /// <summary>Server entry point for <c>.epoch</c>.</summary>
    internal static void HandleCommand(ICommandContext ctx)
    {
        var plugin = Plugin.Active;
        if (plugin == null)
        {
            ctx.Reply("Sample plugin context is not available.");
            return;
        }

        var world = plugin.WorldState;
        string label = Label;
        int stamped = StampedEpoch;
        bool stale = stamped > 0 && world.IsStale(stamped);

        var lines = Lines(world.Describe(), label, stamped, stale);
        for (int i = 0; i < lines.Count; i++)
            ctx.Reply(lines[i]);

        SampleLog.Info(
            Area,
            world.Describe() + " stamped=" + (stamped > 0 ? N(stamped) : "none")
            + " handle=" + (label.Length > 0 ? label : "none")
            + " stale=" + (stamped > 0 ? stale.ToString() : "n/a"));
    }
#endif

    private static string N(int value) => value.ToString(CultureInfo.InvariantCulture);
}
