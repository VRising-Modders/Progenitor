using System;
using System.Globalization;
using System.Threading;

#if !PROGENITOR_TESTS
using ProjectM;
using Progenitor.Commands;
using Progenitor.Hooks;
using Progenitor.Plugins;
using Progenitor.Systems;
#endif

namespace Progenitor.Sample;

/// <summary>
/// Job FEX1 (RD6 J1). The first <c>ctx.Hooks</c> consumer in the repository: a Before/After pair
/// on two real vanilla systems, a heartbeat that proves they fire, and a bounded
/// <see cref="Progenitor.Hooks.SystemHookContext.Skip"/> demonstration.
///
/// <b>Why two systems.</b> <c>SystemHooks</c> covers two different engine seams with one API:
/// managed systems go through <c>SystemBase.Update</c> and unmanaged Burst systems through
/// <c>WorldUnmanagedImpl.UnmanagedUpdate</c>. <c>ProjectM.ServerBootstrapSystem</c> is the
/// managed one and <c>ProjectM.MoveEntitySystem</c> is the Burst one — the same pair CLAUDE.md
/// records as live-verified on 2026-09-02 (61 and 59 calls in two seconds). Hooking both means
/// the server log alone shows that neither path silently stopped working, with no player needed:
/// both systems tick on an empty server.
///
/// <b>The Skip target, and what a skipped frame actually costs.</b> <c>.hooks skip [frames]</c>
/// arms a Before hook that sets <c>ctx.Skip = true</c> for at most <see cref="MaxSkipFrames"/>
/// frames on <c>MoveEntitySystem</c>. Job FEX2 re-read the metadata for this paragraph after R56
/// finding 8 caught it claiming more than it had checked; what is established and what is not:
///
/// <list type="bullet">
/// <item><description><b>The system's shape</b> (server metadata): a persistent
/// <c>NativeList&lt;ColliderCastHit&gt; _MoveStopHits</c>, a <c>SingletonAccessor&lt;ServerTime&gt;</c>
/// and <b>three</b> queries — <c>__query_1244541736_0</c>, <c>_1</c> and <c>_2</c>.</description></item>
/// <item><description><b>Query <c>_0</c>, the only one whose iteration the metadata spells out</b>:
/// <c>Translation</c>, <c>Velocity</c>, <c>MoveVelocity</c> and
/// <c>Rotation</c> read-write, <c>Movement</c>, <c>EntityInput</c> and
/// <c>NetworkInterpolated_Shared</c> read-only, alongside read-only lookups of <c>Dead</c>,
/// <c>MoveStopTrigger</c>, <c>EntityCategory</c>, <c>BuffableFlagState</c>, <c>DynamicCollision</c>,
/// <c>Team</c> and <c>PrefabGUID</c>. Skipping <em>this</em> query is a
/// dropped frame of integration: it consumes nothing, and the next frame integrates from the same
/// components with a larger delta.</description></item>
/// <item><description><b>Queries <c>_1</c> and <c>_2</c> were not traced.</b> Neither has a
/// generated <c>IFE_</c> enumerable, a <c>ResolvedChunk</c> or a type handle anywhere in the metadata,
/// so what they match is not established here — only that they are used through the plain
/// <c>EntityQuery</c> API (a <c>SingletonAccessor</c> generates no <c>__query_</c> field of its own:
/// compare <c>TraderSpawnSystem</c> and <c>TraderSyncSystem</c>, one query each).
/// <c>_MoveStopHits</c> is likewise a buffer the update owns
/// across frames. <b>That is why the skip is bounded to <see cref="MaxSkipFrames"/></b> rather than
/// being a switch: the bound is doing the work the missing reading would have done.</description></item>
/// </list>
///
/// <b>And it is not "one frame of stutter".</b> That <c>_0</c> query carries <c>EntityInput</c> and
/// <c>NetworkInterpolated_Shared</c> — client-input-driven, network-interpolated entities, player
/// characters among them. The client has already predicted the frame the <em>server</em> did not
/// integrate, so the divergence is resolved the way every authoritative-position mismatch is: a
/// correction. Five frames of it is a visible rubber-band for every moving player on the server.
/// It still heals itself whether or not the plugin is loaded, which is the bar for a demo, but an
/// operator arming it should expect to be seen doing so. Nothing here skips
/// <c>ServerBootstrapSystem</c>: suppressing that one would stop connection processing.
///
/// A skipped frame also shows the dispatch rule that is easy to get wrong: an After hook with the
/// default <c>OnlyWhenSystemRuns = true</c> does <b>not</b> run on a frame a Before hook skipped
/// (<c>SystemHooks.DispatchAfter</c>), so during a skip the before counter climbs and the after
/// counter does not. The heartbeat line makes that visible instead of looking like a bug.
///
/// Reload-safe: every registration comes from <c>ctx.Hooks</c> / <c>ctx.Systems</c>, which track
/// on the context, so unload removes them; the counters and the skip budget are cleared in
/// <see cref="Detach"/>. Counting is an <see cref="Interlocked.Increment(ref long)"/> per call —
/// these hooks run every frame, so they must cost nothing.
///
/// Parsing, the counter arithmetic and every log/reply line are game-free so the test project
/// compiles this file.
/// </summary>
internal static class SampleHooks
{
    /// <summary>Log/grep area for the reply-shaped lines: <c>Sample hooks: …</c>.</summary>
    internal const string Area = "hooks";

    /// <summary>Managed hook target. Server only, and it ticks with nobody connected.</summary>
    internal const string ManagedTarget = "ServerBootstrapSystem";

    /// <summary>Unmanaged (Burst) hook target, and the only system this demo will skip.</summary>
    internal const string BurstTarget = "MoveEntitySystem";

    /// <summary>Virtual system that writes the heartbeat, and its cadence in frames.</summary>
    internal const string HeartbeatSystemName = "sample.hooks";

    /// <summary>How many frames between heartbeat lines. Matches the brief's 600.</summary>
    internal const int HeartbeatFrames = 600;

    /// <summary>Most frames one <c>.hooks skip</c> may suppress. A bounded demo, not a switch.</summary>
    internal const int MaxSkipFrames = 5;

    /// <summary>Frames skipped when <c>.hooks skip</c> is given no number.</summary>
    internal const int DefaultSkipFrames = 1;

    private static long _managedBefore;
    private static long _managedAfter;
    private static long _burstBefore;
    private static long _burstAfter;
    private static int _skipBudget;

    /// <summary>What the caller asked for.</summary>
    internal enum Kind
    {
        /// <summary>No argument: report the counters and the skip budget.</summary>
        Status = 0,

        /// <summary>Arm the bounded Skip demonstration.</summary>
        Skip = 1,
    }

    /// <summary>Before-count on the managed target this Core generation.</summary>
    internal static long ManagedBefore => Interlocked.Read(ref _managedBefore);

    /// <summary>After-count on the managed target this Core generation.</summary>
    internal static long ManagedAfter => Interlocked.Read(ref _managedAfter);

    /// <summary>Before-count on the Burst target this Core generation.</summary>
    internal static long BurstBefore => Interlocked.Read(ref _burstBefore);

    /// <summary>After-count on the Burst target. Lags Before by the number of skipped frames.</summary>
    internal static long BurstAfter => Interlocked.Read(ref _burstAfter);

    /// <summary>Frames the Skip demo still owes.</summary>
    internal static int SkipBudget => Volatile.Read(ref _skipBudget);

    /// <summary>
    /// Parse <c>.hooks</c> / <c>.hooks skip [frames]</c>. An unparsable or out-of-range frame
    /// count is refused rather than clamped, so a typo replies usage instead of skipping five.
    /// </summary>
    internal static bool TryParse(string? token, string? frames, out Kind kind, out int frameCount)
    {
        kind = Kind.Status;
        frameCount = 0;

        if (string.IsNullOrWhiteSpace(token)) return true;
        string head = token!.Trim().ToLowerInvariant();
        if (head == "status") return true;
        if (head != "skip") return false;

        kind = Kind.Skip;
        if (string.IsNullOrWhiteSpace(frames))
        {
            frameCount = DefaultSkipFrames;
            return true;
        }

        if (!int.TryParse(frames!.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n))
            return false;
        if (n < 1 || n > MaxSkipFrames) return false;
        frameCount = n;
        return true;
    }

    /// <summary>Usage line, replied on anything <see cref="TryParse"/> refuses.</summary>
    internal static string UsageReply =>
        "Usage: .hooks [status] | .hooks skip [1-" + N(MaxSkipFrames) + "]";

    /// <summary>
    /// The heartbeat line, exactly as <c>docs/bridge-ops.md</c> tells an operator to grep for.
    /// Deliberately not in <c>SampleLog</c>'s <c>Sample &lt;area&gt;:</c> shape: this one is
    /// named after the virtual system that writes it, so a reader can match log line to
    /// registration.
    /// </summary>
    internal static string HeartbeatLine(long managedBefore, long managedAfter, long burstBefore, long burstAfter) =>
        HeartbeatSystemName + ": " + ManagedTarget + " before=" + N(managedBefore) + " after=" + N(managedAfter)
        + "; " + BurstTarget + " before=" + N(burstBefore) + " after=" + N(burstAfter);

    /// <summary>Reply for the bare <c>.hooks</c>.</summary>
    internal static string StatusReply(long managedBefore, long managedAfter, long burstBefore, long burstAfter, int skipBudget) =>
        ManagedTarget + " (managed): before=" + N(managedBefore) + " after=" + N(managedAfter)
        + "; " + BurstTarget + " (Burst): before=" + N(burstBefore) + " after=" + N(burstAfter)
        + "; skip budget=" + N(skipBudget) + " frame(s). The heartbeat writes '"
        + HeartbeatSystemName + ":' every " + N(HeartbeatFrames) + " frames.";

    /// <summary>
    /// Reply for <c>.hooks skip n</c>. It says what the admin typing it will hear about (R56
    /// finding 8): the server misses that many frames of movement integration for every moving
    /// player, and a client that predicted them is corrected, which looks like rubber-banding.
    /// </summary>
    internal static string SkipArmedReply(int frames) =>
        "Armed SystemHookContext.Skip for " + N(frames) + " frame(s) on " + BurstTarget
        + ". The server misses that many frames of movement integration for every moving player, so "
        + "a client that already predicted them is corrected: expect visible rubber-banding, not a "
        + "silent frame. The After hook does not run on a skipped frame, so before will lead after by "
        + N(frames) + ".";

    /// <summary>Reply when a skip is already running.</summary>
    internal static string SkipBusyReply(int remaining) =>
        "A skip is already armed (" + N(remaining) + " frame(s) left). Let it finish, then ask again.";

    /// <summary>The per-frame line the Skip hook writes, so the log alone proves it fired.</summary>
    internal static string SkippedFrameLine(int remaining) =>
        "skipped " + BurstTarget + " update this frame (remaining=" + N(remaining) + ")";

    /// <summary>Count one managed Before. Returns the new total.</summary>
    internal static long CountManagedBefore() => Interlocked.Increment(ref _managedBefore);

    /// <summary>Count one managed After. Returns the new total.</summary>
    internal static long CountManagedAfter() => Interlocked.Increment(ref _managedAfter);

    /// <summary>Count one Burst Before. Returns the new total.</summary>
    internal static long CountBurstBefore() => Interlocked.Increment(ref _burstBefore);

    /// <summary>Count one Burst After. Returns the new total.</summary>
    internal static long CountBurstAfter() => Interlocked.Increment(ref _burstAfter);

    /// <summary>
    /// Arm the Skip demo. Refuses while one is running rather than extending it, so the budget
    /// cannot be grown without bound by repeating the command.
    /// </summary>
    internal static bool TryArmSkip(int frames, out int remaining)
    {
        if (frames < 1 || frames > MaxSkipFrames)
        {
            remaining = Volatile.Read(ref _skipBudget);
            return false;
        }

        remaining = Interlocked.CompareExchange(ref _skipBudget, frames, 0);
        if (remaining != 0) return false;
        remaining = frames;
        return true;
    }

    /// <summary>
    /// Consume one frame of the skip budget. Returns true when this frame should be skipped;
    /// <paramref name="remaining"/> is what is left after this frame.
    /// </summary>
    internal static bool TryConsumeSkip(out int remaining)
    {
        while (true)
        {
            int current = Volatile.Read(ref _skipBudget);
            if (current <= 0)
            {
                remaining = 0;
                return false;
            }

            if (Interlocked.CompareExchange(ref _skipBudget, current - 1, current) == current)
            {
                remaining = current - 1;
                return true;
            }
        }
    }

    /// <summary>Whether this frame is a heartbeat frame. Frame 0 is not one.</summary>
    internal static bool IsHeartbeatFrame(long frame) => frame > 0 && frame % HeartbeatFrames == 0;

    /// <summary>Forget the counters and any armed skip. Unload does this; Core drops the hooks.</summary>
    internal static void Reset()
    {
        Interlocked.Exchange(ref _managedBefore, 0);
        Interlocked.Exchange(ref _managedAfter, 0);
        Interlocked.Exchange(ref _burstBefore, 0);
        Interlocked.Exchange(ref _burstAfter, 0);
        Volatile.Write(ref _skipBudget, 0);
    }

#if !PROGENITOR_TESTS
    /// <summary>
    /// Server-side registration. Four hooks and one virtual system, all tracked on the context:
    /// unload removes every one of them, which is why nothing here keeps an <c>IDisposable</c>.
    /// </summary>
    internal static void Attach(PluginContext ctx)
    {
        if (ctx == null) throw new ArgumentNullException(nameof(ctx));
        Reset();

        try
        {
            // Default HookOptions: fire only when the system would actually run, every eligible
            // frame. That is what makes the counters mean "the system ticked", not "a frame passed".
            ctx.Hooks.Before<ServerBootstrapSystem>(_ => CountManagedBefore());
            ctx.Hooks.After<ServerBootstrapSystem>(_ => CountManagedAfter());

            ctx.Hooks.Before<MoveEntitySystem>(OnBurstBefore);
            ctx.Hooks.After<MoveEntitySystem>(_ => CountBurstAfter());

            ctx.Systems.Add(HeartbeatSystemName, OnHeartbeat);

            SampleLog.Info(Area, "attached Before/After on " + ManagedTarget + " (managed) and "
                + BurstTarget + " (Burst); heartbeat every " + N(HeartbeatFrames) + " frames.");
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("Sample hooks attach threw: " + e);
        }
    }

    /// <summary>
    /// The one hook that can change what the game does. It counts on every eligible frame and
    /// sets <c>Skip</c> only while <c>.hooks skip</c> has budget left.
    /// </summary>
    private static void OnBurstBefore(SystemHookContext hook)
    {
        CountBurstBefore();
        if (!TryConsumeSkip(out int remaining)) return;
        hook.Skip = true;
        SampleLog.Info(Area, SkippedFrameLine(remaining));
    }

    private static void OnHeartbeat(VirtualSystemContext vs)
    {
        if (!IsHeartbeatFrame(vs.Frame)) return;
        Write(HeartbeatLine(ManagedBefore, ManagedAfter, BurstBefore, BurstAfter));
    }

    /// <summary>Server entry point for <c>.hooks</c>.</summary>
    internal static void HandleCommand(ICommandContext ctx, Kind kind, int frames)
    {
        if (kind == Kind.Skip)
        {
            if (!TryArmSkip(frames, out int remaining))
            {
                ctx.Reply(SkipBusyReply(remaining));
                return;
            }

            ctx.Reply(SkipArmedReply(frames));
            SampleLog.Info(Area, "skip armed frames=" + N(frames) + " target=" + BurstTarget);
            return;
        }

        string reply = StatusReply(ManagedBefore, ManagedAfter, BurstBefore, BurstAfter, SkipBudget);
        ctx.Reply(reply);
        SampleLog.Info(Area, "status " + reply);
    }

    private static void Write(string line)
    {
        SampleLog.Record(line);
        try { Plugin.Logger?.LogInfo(line); }
        catch { /* the logger is torn down during unload */ }
    }
#endif

    /// <summary>
    /// Unload. Core removes the hooks and the virtual system on every reason
    /// (<c>ctx.Hooks</c> / <c>ctx.Systems</c> track them), so this only forgets the numbers.
    /// </summary>
    internal static void Detach() => Reset();

    private static string N(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string N(long value) => value.ToString(CultureInfo.InvariantCulture);
}
