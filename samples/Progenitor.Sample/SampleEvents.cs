using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;

#if !PROGENITOR_TESTS
using Progenitor.Castles;
using Progenitor.Commands;
using Progenitor.Events;
using Progenitor.Plugins;
using Progenitor.WorldState;
#endif

namespace Progenitor.Sample;

/// <summary>
/// Job FEX5 (RD6 J5). The event types the framework publishes and nothing in the repository
/// listened to: <c>PlayerDisconnected</c>, <c>WorldLoaded</c>, <c>CastleHeartPlaced</c> /
/// <c>CastleHeartDestroyed</c>, and the <see cref="Progenitor.Events.ICancellable"/> half of
/// <c>ChatMessage</c> — a handler that actually <b>cancels</b> a line, which
/// <see cref="SampleChat"/>'s observer then reports as <c>handled=True</c>.
///
/// <para>Each subscriber writes exactly one <c>Sample events: …</c> line, so the server log alone
/// proves the event fired, and the last <see cref="Capacity"/> lines are kept in a ring that
/// <c>.events</c> prints for someone who is in chat rather than tailing a file.</para>
///
/// <b>The three things a reader should take away.</b>
/// <list type="bullet">
/// <item><description><b>Canceling does not stop the bus.</b>
/// <c>EventBus.Publish</c> runs <em>every</em> handler in order regardless
/// (<c>EventBus.cs:9-13</c>: "Later handlers still see it, with <c>IsCancelled</c> set"). So the
/// canceler below sits at <see cref="CancelPriority"/> and <see cref="SampleChat"/>'s observer
/// at <c>-100</c>, and the observer's line is the proof: it prints <c>handled=True</c> for a
/// canceled line while still having run. A mod that wants to <em>make</em> the decision goes
/// above the commands module's 500; a mod that wants to <em>see</em> it goes below.</description></item>
/// <item><description><b>The castle events are a change detector, not a game callback</b>
/// (<c>Castles/CastleEvents.cs:8-11</c>, <c>CastleWatch.cs:9-18</c>). They are published from a
/// once-a-second poll that runs <b>only while something is subscribed</b>
/// (<c>Castles.cs:295-296</c> checks <c>EventBus.SubscriberCount</c> for both types), and the
/// first poll of a world reports every existing heart with <c>InitialScan=true</c>.
/// <c>.events castle on|off</c> exists to make that visible: the reply prints the live
/// subscriber counts, so an operator can see the poll switch itself off when the last subscriber
/// goes.</description></item>
/// <item><description><b>A disconnect is published <em>before</em> the game processes it</b>
/// (<c>Events/GameEvents.cs:25</c>), so the user entity is still readable in the handler. This is
/// the seam a mod uses to persist per-player state on the way out — do the read here, not on a
/// later frame.</description></item>
/// </list>
///
/// <b>Why the canceler is off by default.</b> A <c>ChatMessage</c> handler that cancels swallows
/// other players' chat, and the Sample is loaded by operators who did not write it. So the
/// subscriber is always registered (that is what makes the ordering visible) but it only cancels
/// while an admin has switched it on with <c>.events cancel on</c>, and only for a line that
/// starts with <see cref="CancelMarker"/>. Off, it still logs what it <em>would</em> have done.
///
/// <b>Live-check split: server only.</b> All five events are published server-side. The triggers
/// are a player leaving (<c>PlayerDisconnected</c>), a save reload (<c>WorldLoaded</c>), a heart
/// placed or destroyed on the test save (<c>CastleHeartPlaced</c> / <c>Destroyed</c>, plus the
/// initial scan at the first poll after subscribing), and any chat line starting
/// <c>!!</c> (<c>ChatMessage</c> + <c>ICancellable</c>).
///
/// Reload-safe: every subscription is an <c>IDisposable</c> this file owns and disposes in
/// <see cref="Detach"/>, and each is registered under the plugin's owner so Core's sweep would
/// drop it anyway. The counters and the ring are cleared with them. Everything outside the
/// <c>!PROGENITOR_TESTS</c> fence is game-free, so the test project compiles and drives it.
/// </summary>
internal static class SampleEvents
{
    /// <summary>Log/grep area: <c>Sample events: …</c>.</summary>
    internal const string Area = "events";

    /// <summary>How many recent event lines the ring keeps and <c>.events</c> prints.</summary>
    internal const int Capacity = 10;

    /// <summary>
    /// Priority of the canceling <c>ChatMessage</c> handler. Below the commands module's 500 so
    /// a real command is still handled by the dispatcher first, and well above
    /// <see cref="SampleChat.ObserverPriority"/> so the observer sees the verdict.
    /// </summary>
    internal const int CancelPriority = 400;

    /// <summary>A chat line starting with this is what <c>.events cancel on</c> swallows.</summary>
    internal const string CancelMarker = "!!";

    // ---- the before/after overload of ctx.Events.On (job J19) ---------------------------------
    //
    // `ctx.Events.On<T>(handler, priority, before, after)` had no example: Plugin.cs demonstrates
    // ordering through the *static* EventBus.On, which is the route a plugin takes when it wants to
    // choose the plugin id itself. The context overload cannot - it always passes this plugin's
    // BepInEx guid as the id - and that is exactly the thing worth showing, because it decides what
    // a *different* mod has to name to order itself around you, and because two handlers registered
    // through it share one id and therefore cannot be ordered against each other at all.
    //
    // So the pair below is one of each. The notice handler goes in through the static route under
    // its own id; the audit handler goes in through `ctx.Events.On` with `after: [NoticeId]`, at a
    // priority that would put it FIRST if the number were all that mattered. `.events order` prints
    // the resolved order, and the audit line is second.

    /// <summary>
    /// Plugin id of the always-on <c>PlayerDisconnected</c> notice handler. Registered through the
    /// static <c>EventBus.On</c> so it can carry an id of its own for the audit handler to name.
    /// </summary>
    internal const string NoticeId = "progenitor.sample.events.notice";

    /// <summary>
    /// Priority of the audit handler. Higher than <see cref="NoticePriority"/>, so a numeric sort
    /// alone would run it first; <c>after: [<see cref="NoticeId"/>]</c> is what puts it second.
    /// </summary>
    internal const int AuditPriority = 200;

    /// <summary>Priority of the notice handler. The lower number, and it still runs first.</summary>
    internal const int NoticePriority = 0;

    /// <summary>How much of a canceled chat line reaches the log. The rest is elided.</summary>
    internal const int LogTextChars = 60;

    private static readonly object Gate = new();
    private static readonly List<string> Lines = new(Capacity);
    private static int _disconnects;
    private static int _worldLoads;
    private static int _castlesPlaced;
    private static int _castlesDestroyed;
    private static int _chatSeen;
    private static int _chatCancelled;
    private static int _cancelArmed;
    private static int _audits;

    /// <summary>True while <c>.events cancel on</c> has armed the canceler.</summary>
    internal static bool CancelArmed => Volatile.Read(ref _cancelArmed) != 0;

    /// <summary><c>PlayerDisconnected</c> count since attach.</summary>
    internal static int DisconnectCount => Volatile.Read(ref _disconnects);

    /// <summary>
    /// How many times the ordered audit handler ran. Equal to <see cref="DisconnectCount"/>
    /// unless something dropped one of the two subscriptions.
    /// </summary>
    internal static int AuditCount => Volatile.Read(ref _audits);

    /// <summary><c>WorldLoaded</c> count since attach.</summary>
    internal static int WorldLoadCount => Volatile.Read(ref _worldLoads);

    /// <summary><c>CastleHeartPlaced</c> count since attach, initial scan included.</summary>
    internal static int CastlePlacedCount => Volatile.Read(ref _castlesPlaced);

    /// <summary><c>CastleHeartDestroyed</c> count since attach.</summary>
    internal static int CastleDestroyedCount => Volatile.Read(ref _castlesDestroyed);

    /// <summary>Chat lines the marker matched, armed or not.</summary>
    internal static int ChatMatchedCount => Volatile.Read(ref _chatSeen);

    /// <summary>Chat lines this plugin actually canceled.</summary>
    internal static int ChatCancelledCount => Volatile.Read(ref _chatCancelled);

    /// <summary>The last <see cref="Capacity"/> event lines, oldest first.</summary>
    internal static IReadOnlyList<string> RecentLines
    {
        get { lock (Gate) return Lines.ToArray(); }
    }

    /// <summary>What the caller asked <c>.events</c> for.</summary>
    internal enum Kind
    {
        /// <summary>No argument: counters, subscriber counts and the ring.</summary>
        Status = 0,

        /// <summary>Arm the <c>ChatMessage</c> canceler.</summary>
        CancelOn = 1,

        /// <summary>Disarm it. The subscriber stays; it stops canceling.</summary>
        CancelOff = 2,

        /// <summary>Subscribe the two castle events, which starts the once-a-second poll.</summary>
        CastleOn = 3,

        /// <summary>Drop them, which stops the poll.</summary>
        CastleOff = 4,

        /// <summary>Forget the counters and the ring. Subscriptions are untouched.</summary>
        Clear = 5,

        /// <summary>
        /// Print <c>EventBus.DescribeOrder&lt;PlayerDisconnected&gt;()</c> — the live resolved
        /// handler order, which is what the <c>before</c>/<c>after</c> overload actually bought
        /// (job J19).
        /// </summary>
        Order = 6,
    }

    /// <summary>Usage line, replied on anything <see cref="TryParse"/> refuses.</summary>
    internal const string UsageReply =
        "Usage: .events [status] | .events order | .events cancel on|off | .events castle on|off | .events clear";

    /// <summary>
    /// Parse <c>.events</c>'s two chat tokens. <c>cancel</c> and <c>castle</c> require an
    /// explicit <c>on</c> or <c>off</c>: a bare <c>.events cancel</c> is refused rather than
    /// guessed at, because guessing "toggle" makes the reply the only record of which way it went.
    /// </summary>
    internal static bool TryParse(string? first, string? second, out Kind kind)
    {
        kind = Kind.Status;
        string a = (first ?? "").Trim().ToLowerInvariant();
        string b = (second ?? "").Trim().ToLowerInvariant();

        if (a.Length == 0) return b.Length == 0;

        switch (a)
        {
            case "status":
                return b.Length == 0;
            case "clear":
                kind = Kind.Clear;
                return b.Length == 0;
            case "order":
                kind = Kind.Order;
                return b.Length == 0;
            case "cancel":
                if (b == "on") { kind = Kind.CancelOn; return true; }
                if (b == "off") { kind = Kind.CancelOff; return true; }
                return false;
            case "castle":
                if (b == "on") { kind = Kind.CastleOn; return true; }
                if (b == "off") { kind = Kind.CastleOff; return true; }
                return false;
            default:
                return false;
        }
    }

    /// <summary>
    /// Whether a chat line is one this example would swallow. Marker match only — the text after
    /// it is never interpreted, because it is player input.
    /// </summary>
    internal static bool Matches(string? text) =>
        !string.IsNullOrEmpty(text) && text!.TrimStart().StartsWith(CancelMarker, StringComparison.Ordinal);

    /// <summary>The line written when the subscriptions go in.</summary>
    internal static string SubscribedLine(bool castle) =>
        "subscribed to PlayerDisconnected, WorldLoaded and ChatMessage"
        + (castle ? ", plus CastleHeartPlaced/Destroyed (this starts the once-a-second castle poll)" : "")
        + "; one line each, last " + N(Capacity) + " shown by .events";

    /// <summary>The <c>PlayerDisconnected</c> line.</summary>
    internal static string DisconnectLine(string? name, string? reason) =>
        "PlayerDisconnected name=" + Who(name) + " reason=" + (string.IsNullOrEmpty(reason) ? "<none>" : reason!)
        + " (published before the game processes it, so the user entity is still readable here)";

    /// <summary>
    /// The audit handler's line (job J19). It names the ordinal so the log alone shows that the
    /// <c>after</c> constraint beat the priority number: the notice line is written first even
    /// though the audit handler asked for the higher priority.
    /// </summary>
    internal static string AuditLine(string? name, int audits) =>
        "PlayerDisconnected (audit #" + N(audits) + ") name=" + Who(name)
        + " - registered through ctx.Events.On(handler, priority=" + N(AuditPriority)
        + ", before: null, after: [" + NoticeId + "]), so it runs AFTER the notice handler at "
        + "priority " + N(NoticePriority) + ". Priority alone would have run it first; .events order "
        + "prints the resolved list.";

    /// <summary>The <c>WorldLoaded</c> line.</summary>
    internal static string WorldLoadedLine(int epoch, int previousEpoch, string? source) =>
        "WorldLoaded epoch=" + N(previousEpoch) + "->" + N(epoch)
        + " source=" + (string.IsNullOrEmpty(source) ? "<none>" : source!)
        + " (every raw entity id recorded before this now names something else)";

    /// <summary>The <c>CastleHeartPlaced</c> line.</summary>
    internal static string CastlePlacedLine(int territoryIndex, string? owner, int level, bool initialScan) =>
        "CastleHeartPlaced territory=" + N(territoryIndex) + " owner=" + Who(owner) + " level=" + N(level)
        + (initialScan ? " (initial scan: a heart that already existed when the poll started)" : "");

    /// <summary>The <c>CastleHeartDestroyed</c> line. The handle is gone; this is the last snapshot.</summary>
    internal static string CastleDestroyedLine(int territoryIndex, string? owner) =>
        "CastleHeartDestroyed territory=" + N(territoryIndex) + " owner=" + Who(owner)
        + " (last snapshot; the heart entity no longer exists)";

    /// <summary>
    /// The <c>ChatMessage</c> line for a marker match. <paramref name="cancelled"/> is what this
    /// handler did, which is not the same as whether the line was already canceled by something
    /// with a higher priority.
    /// </summary>
    internal static string ChatCancelLine(string? sender, bool cancelled, bool alreadyCancelled, string? text) =>
        "ChatMessage from=" + Who(sender)
        + (cancelled ? " CANCELED" : " matched but not armed (.events cancel on to swallow it)")
        + " alreadyCancelled=" + Yn(alreadyCancelled)
        + " text=\"" + SampleChat.Elide(text, LogTextChars) + "\"";

    /// <summary>Reply for <c>.events cancel on|off</c>.</summary>
    internal static string CancelReply(bool armed) =>
        armed
            ? "Armed: a chat line starting '" + CancelMarker + "' is now canceled at priority "
                + N(CancelPriority) + ". Type one and grep the log for 'Sample chat: saw' — the "
                + "observer at " + N(SampleChat.ObserverPriority) + " still runs and reports handled=True."
            : "Disarmed: the ChatMessage subscriber stays registered and keeps logging, but it "
                + "cancels nothing.";

    /// <summary>Reply for <c>.events castle on|off</c>, carrying the live subscriber counts.</summary>
    internal static string CastleReply(bool subscribed, int placedSubscribers, int destroyedSubscribers) =>
        (subscribed
            ? "Subscribed to CastleHeartPlaced/Destroyed. The once-a-second poll is running; the "
                + "first poll reports every existing heart with InitialScan=true."
            : "Unsubscribed. The poll stops as soon as the last subscriber goes.")
        + " Subscribers now: placed=" + N(placedSubscribers) + " destroyed=" + N(destroyedSubscribers) + ".";

    /// <summary>Reply for <c>.events clear</c>.</summary>
    internal const string ClearReply = "Counters and the recent-event ring are cleared. Subscriptions are untouched.";

    /// <summary>
    /// The <c>.events</c> status reply: the counters, the two castle subscriber counts (which is
    /// how a reader tells whether the poll is running), then the ring.
    /// </summary>
    internal static IReadOnlyList<string> StatusLines(int placedSubscribers, int destroyedSubscribers)
    {
        var lines = new List<string>(Capacity + 3)
        {
            "events: disconnects=" + N(DisconnectCount) + " audits=" + N(AuditCount)
                + " worldLoads=" + N(WorldLoadCount)
                + " castlesPlaced=" + N(CastlePlacedCount) + " castlesDestroyed=" + N(CastleDestroyedCount)
                + " chatMatched=" + N(ChatMatchedCount) + " chatCancelled=" + N(ChatCancelledCount),
            "cancel armed=" + Yn(CancelArmed) + " marker='" + CancelMarker + "' priority=" + N(CancelPriority)
                + "; castle subscribers placed=" + N(placedSubscribers) + " destroyed=" + N(destroyedSubscribers)
                + " (0/0 means the once-a-second poll is not running)",
        };

        var recent = RecentLines;
        if (recent.Count == 0)
        {
            lines.Add("Nothing yet. Triggers: a player leaving, a save reload, a castle heart "
                + "placed or destroyed (.events castle on first), or a chat line starting '"
                + CancelMarker + "'.");
            return lines;
        }

        for (int i = 0; i < recent.Count; i++)
            lines.Add("  " + recent[i]);
        return lines;
    }

    /// <summary>Record a <c>PlayerDisconnected</c>. Returns the line it wrote.</summary>
    internal static string RecordDisconnect(string? name, string? reason)
    {
        Interlocked.Increment(ref _disconnects);
        return Record(DisconnectLine(name, reason));
    }

    /// <summary>Record one run of the ordered audit handler. Returns the line it wrote.</summary>
    internal static string RecordAudit(string? name)
    {
        int audits = Interlocked.Increment(ref _audits);
        return Record(AuditLine(name, audits));
    }

    /// <summary>
    /// Reply for <c>.events order</c>. <paramref name="dump"/> is
    /// <c>EventBus.DescribeOrder&lt;T&gt;()</c> verbatim — a multi-line string, re-resolved on
    /// every subscribe, so this is the order <c>Publish</c> will actually use and not a record of
    /// what was asked for. Split rather than reformatted: a chat reply is one line each, and
    /// rewriting Core's dump would make the example lie about what the API returns.
    /// </summary>
    internal static IReadOnlyList<string> OrderLines(string? dump)
    {
        var lines = new List<string>(6)
        {
            "Resolved handler order. The audit handler asked for priority " + N(AuditPriority)
                + " and the notice handler for " + N(NoticePriority) + "; after=[" + NoticeId
                + "] is why the lower number still runs first.",
        };

        if (string.IsNullOrWhiteSpace(dump))
        {
            lines.Add("  (EventBus.DescribeOrder returned nothing: no handlers are registered.)");
            return lines;
        }

        foreach (string line in dump!.Replace("\r", "").Split('\n'))
        {
            if (line.Trim().Length == 0) continue;
            lines.Add("  " + line.Trim());
        }

        return lines;
    }

    /// <summary>Record a <c>WorldLoaded</c>. Returns the line it wrote.</summary>
    internal static string RecordWorldLoaded(int epoch, int previousEpoch, string? source)
    {
        Interlocked.Increment(ref _worldLoads);
        return Record(WorldLoadedLine(epoch, previousEpoch, source));
    }

    /// <summary>Record a <c>CastleHeartPlaced</c>. Returns the line it wrote.</summary>
    internal static string RecordCastlePlaced(int territoryIndex, string? owner, int level, bool initialScan)
    {
        Interlocked.Increment(ref _castlesPlaced);
        return Record(CastlePlacedLine(territoryIndex, owner, level, initialScan));
    }

    /// <summary>Record a <c>CastleHeartDestroyed</c>. Returns the line it wrote.</summary>
    internal static string RecordCastleDestroyed(int territoryIndex, string? owner)
    {
        Interlocked.Increment(ref _castlesDestroyed);
        return Record(CastleDestroyedLine(territoryIndex, owner));
    }

    /// <summary>
    /// The whole cancel decision, game-free: given the armed flag and the line, say whether this
    /// handler cancels, bump the counters and write the log line. The live handler does exactly
    /// this and then calls <c>ev.Cancel()</c> when it returns true.
    /// </summary>
    internal static bool RecordChatMatch(string? sender, string? text, bool alreadyCancelled)
    {
        Interlocked.Increment(ref _chatSeen);
        bool cancel = CancelArmed;
        if (cancel) Interlocked.Increment(ref _chatCancelled);
        Record(ChatCancelLine(sender, cancel, alreadyCancelled, text));
        return cancel;
    }

    /// <summary>Arm or disarm the canceler. Returns the new state.</summary>
    internal static bool SetCancelArmed(bool armed)
    {
        Volatile.Write(ref _cancelArmed, armed ? 1 : 0);
        return armed;
    }

    /// <summary>Forget the counters and the ring. Subscriptions are not touched.</summary>
    internal static void ClearCounters()
    {
        lock (Gate) Lines.Clear();
        Volatile.Write(ref _disconnects, 0);
        Volatile.Write(ref _worldLoads, 0);
        Volatile.Write(ref _castlesPlaced, 0);
        Volatile.Write(ref _castlesDestroyed, 0);
        Volatile.Write(ref _chatSeen, 0);
        Volatile.Write(ref _chatCancelled, 0);
        Volatile.Write(ref _audits, 0);
    }

#if !PROGENITOR_TESTS
    private static readonly List<IDisposable> Subs = new(3);
    private static readonly List<IDisposable> CastleSubs = new(2);
    private static object? _owner;
    private static string? _pluginId;

    /// <summary>
    /// Server-side registration. The three always-on subscriptions go in here; the two castle
    /// ones are deliberately left to <c>.events castle on</c> so the poll they start is a choice
    /// an operator makes and can see in the reply.
    /// </summary>
    internal static void Attach(PluginContext ctx)
    {
        if (ctx == null) throw new ArgumentNullException(nameof(ctx));
        Detach();

        _owner = ctx.Owner;
        _pluginId = ctx.Guid;

        var subs = new List<IDisposable>(4)
        {
            // The notice handler, through the static route so it can carry NoticeId. That id is
            // what the audit handler names, and what another mod would name to order itself
            // around this one.
            EventBus.On<PlayerDisconnected>(
                OnPlayerDisconnected, priority: NoticePriority, owner: ctx.Owner, pluginId: NoticeId),

            // Job J19. The context overload with before/after. Priority AuditPriority would put
            // this first; after: [NoticeId] puts it second, and `.events order` shows the result.
            // `before` is passed explicitly as null rather than omitted so the example names both
            // halves of the overload it exists to demonstrate.
            ctx.Events.On<PlayerDisconnected>(
                OnPlayerDisconnectedAudit, AuditPriority, before: null, after: new[] { NoticeId }),

            EventBus.On<WorldLoaded>(OnWorldLoaded, owner: ctx.Owner, pluginId: ctx.Guid),
            EventBus.On<ChatMessage>(OnChat, priority: CancelPriority, owner: ctx.Owner, pluginId: ctx.Guid),
        };

        lock (Gate) Subs.AddRange(subs);
        SampleLog.Info(Area, SubscribedLine(castle: false));
        // Job J19. The resolved order, once, at attach: the proof that the after constraint took
        // effect does not have to wait for somebody to disconnect.
        SampleLog.Info(Area, EventBus.DescribeOrder<PlayerDisconnected>().Replace("\n", " | "));
    }

    /// <summary>
    /// Subscribe or drop the two castle events. Returns true when they are subscribed after the
    /// call. This is the switch behind the module's documented "polls only while subscribed".
    /// </summary>
    internal static bool SetCastleSubscribed(bool subscribe)
    {
        List<IDisposable> drop;
        lock (Gate)
        {
            drop = new List<IDisposable>(CastleSubs);
            CastleSubs.Clear();
        }

        for (int i = 0; i < drop.Count; i++)
        {
            try { drop[i].Dispose(); }
            catch (Exception e) { Plugin.Logger?.LogError("Sample events castle dispose threw: " + e); }
        }

        if (!subscribe) return false;

        var owner = _owner;
        if (owner == null) return false;

        var subs = new List<IDisposable>(2)
        {
            EventBus.On<CastleHeartPlaced>(OnCastlePlaced, owner: owner, pluginId: _pluginId),
            EventBus.On<CastleHeartDestroyed>(OnCastleDestroyed, owner: owner, pluginId: _pluginId),
        };

        lock (Gate) CastleSubs.AddRange(subs);
        return true;
    }

    /// <summary>Server entry point for <c>.events</c>.</summary>
    internal static void HandleCommand(ICommandContext ctx, Kind kind)
    {
        switch (kind)
        {
            case Kind.CancelOn:
            case Kind.CancelOff:
            {
                bool armed = SetCancelArmed(kind == Kind.CancelOn);
                string line = CancelReply(armed);
                ctx.Reply(line);
                SampleLog.Info(Area, "cancel armed=" + Yn(armed));
                return;
            }

            case Kind.CastleOn:
            case Kind.CastleOff:
            {
                bool on = SetCastleSubscribed(kind == Kind.CastleOn);
                string line = CastleReply(
                    on,
                    EventBus.SubscriberCount<CastleHeartPlaced>(),
                    EventBus.SubscriberCount<CastleHeartDestroyed>());
                ctx.Reply(line);
                SampleLog.Info(Area, line);
                return;
            }

            case Kind.Order:
            {
                var order = OrderLines(EventBus.DescribeOrder<PlayerDisconnected>());
                for (int i = 0; i < order.Count; i++)
                    ctx.Reply(order[i]);
                return;
            }

            case Kind.Clear:
                ClearCounters();
                ctx.Reply(ClearReply);
                SampleLog.Info(Area, "counters cleared");
                return;

            default:
            {
                var lines = StatusLines(
                    EventBus.SubscriberCount<CastleHeartPlaced>(),
                    EventBus.SubscriberCount<CastleHeartDestroyed>());
                for (int i = 0; i < lines.Count; i++)
                    ctx.Reply(lines[i]);
                return;
            }
        }
    }

    private static void OnPlayerDisconnected(PlayerDisconnected ev)
    {
        try
        {
            SampleLog.Info(Area, RecordDisconnect(ev.User.CharacterName.ToString(), ev.Reason));
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("Sample PlayerDisconnected handler threw: " + e);
        }
    }

    /// <summary>
    /// The ordered handler (job J19). It does nothing a real audit would not: one line naming the
    /// player, written after the notice handler because of the <c>after</c> constraint.
    /// </summary>
    private static void OnPlayerDisconnectedAudit(PlayerDisconnected ev)
    {
        try
        {
            SampleLog.Info(Area, RecordAudit(ev.User.CharacterName.ToString()));
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("Sample PlayerDisconnected audit handler threw: " + e);
        }
    }

    private static void OnWorldLoaded(WorldLoaded ev)
    {
        try
        {
            SampleLog.Info(Area, RecordWorldLoaded(ev.Epoch, ev.PreviousEpoch, ev.Source));
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("Sample WorldLoaded handler threw: " + e);
        }
    }

    private static void OnCastlePlaced(CastleHeartPlaced ev)
    {
        try
        {
            var castle = ev.Castle;
            SampleLog.Info(Area, RecordCastlePlaced(
                castle.Territory.Index, castle.Owner.Name, castle.Level, ev.InitialScan));
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("Sample CastleHeartPlaced handler threw: " + e);
        }
    }

    private static void OnCastleDestroyed(CastleHeartDestroyed ev)
    {
        try
        {
            SampleLog.Info(Area, RecordCastleDestroyed(ev.TerritoryIndex, ev.Owner.Name));
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("Sample CastleHeartDestroyed handler threw: " + e);
        }
    }

    /// <summary>
    /// The <see cref="Progenitor.Events.ICancellable"/> demonstration. Every non-matching line is
    /// ignored in silence — <see cref="SampleChat"/> already logs one line per chat line and a
    /// second copy would only make the log harder to read.
    /// </summary>
    private static void OnChat(ChatMessage ev)
    {
        try
        {
            if (!Matches(ev.Text)) return;
            bool cancel = RecordChatMatch(ev.User.CharacterName.ToString(), ev.Text, ev.IsCancelled);
            if (cancel) ev.Cancel();
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("Sample ChatMessage canceler threw: " + e);
        }
    }
#endif

    /// <summary>
    /// Unload. Every subscription is dropped here as well as by Core's owner sweep, the armed
    /// flag goes back to off, and the counters and ring are forgotten — so a hot swap starts a
    /// fresh generation with nothing carried over.
    /// </summary>
    internal static void Detach()
    {
#if !PROGENITOR_TESTS
        List<IDisposable> subs;
        lock (Gate)
        {
            subs = new List<IDisposable>(Subs);
            subs.AddRange(CastleSubs);
            Subs.Clear();
            CastleSubs.Clear();
        }

        for (int i = 0; i < subs.Count; i++)
        {
            try { subs[i].Dispose(); }
            catch (Exception e) { Plugin.Logger?.LogError("Sample events dispose threw: " + e); }
        }

        _owner = null;
        _pluginId = null;
#endif
        SetCancelArmed(false);
        ClearCounters();
    }

    private static string Record(string line)
    {
        lock (Gate)
        {
            Lines.Add(line);
            while (Lines.Count > Capacity) Lines.RemoveAt(0);
        }

        return line;
    }

    private static string Who(string? name) => string.IsNullOrEmpty(name) ? "<unknown>" : name!;

    private static string Yn(bool value) => value ? "True" : "False";

    private static string N(int value) => value.ToString(CultureInfo.InvariantCulture);
}
