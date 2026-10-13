using System;
using System.Collections.Generic;
using System.Globalization;

#if !PROGENITOR_TESTS
using ProjectM.Network;
using Progenitor.Commands;
using Progenitor.Events;
using Progenitor.Networking;
using Progenitor.Plugins;
using Progenitor.Ui;
using Unity.Entities;
#endif

namespace Progenitor.Sample;

/// <summary>
/// Job FEX1 (RD6 J3). The client→server direction of <c>CustomEvents</c>, which had no consumer
/// anywhere in the repository: every other Sample packet (<c>.fx</c>, <c>.ui</c>, <c>.mods</c>)
/// travels server→client. One round trip, driven by a client keybind:
///
/// <code>
/// client  F8            → ctx.Net.SendToServer(PingEventId, Ping{ Magic, Counter })
/// server  OnReceiveFromClient → validate → ctx.Net.SendToUser(sender, PongEventId, Pong{ Reply })
///                                        → ctx.Net.SendToAll(PongEventId, Pong{ Notice })
/// client  OnReceive     → log the reply and the notice
/// </code>
///
/// <b>Packet conventions</b> (<c>src/Progenitor.Core/Networking/CustomEvents.cs</c>): one custom
/// event per packet, a fixed-size <c>unmanaged</c> struct, and a large distinctive event id —
/// the game's own ids are hashes, so a collision would be silent. Two rules, both enforced by
/// <c>CustomEvents.OnReceive</c> / <c>OnReceiveFromClient</c> since job FEX2 (R56 finding 7):
/// <b>the whole <c>0x5072____</c> ("Pr**") range belongs to Progenitor</b> and a plugin
/// registration inside it is refused, and an id already held by a <em>different</em> assembly is
/// refused rather than replaced. So a mod derives its ids from its own four-letter tag. The
/// Sample's are ASCII <c>Sm??</c> words, continuing <c>SmFx</c> / <c>SmUi</c> / <c>SmMd</c>:
/// <see cref="PingEventId"/> is <c>SmNp</c> and <see cref="PongEventId"/> is <c>SmNr</c>.
///
/// <b>Everything a client sends is hostile until proved otherwise.</b> Core's
/// <c>ReceiveGate</c> already token-buckets per connection and refuses any connection it cannot
/// resolve to an approved user row, so a handler never sees an unresolved sender. This file adds
/// the four checks that belong to the <em>mod</em>, in <see cref="Admit"/> so they are unit
/// tested rather than eyeballed: the payload is exactly one <see cref="Ping"/>
/// (<c>CustomEvents.TryRead&lt;T&gt;</c> refuses any other length), the magic word matches, the
/// counter is inside <see cref="MaxCounter"/>, the sender's handshake answer is <c>ok</c>
/// (<c>Handshake.UserAnswerStatus</c>), and that user has not exceeded
/// <see cref="MaxPacketsPerUser"/> for this session. Every refusal writes one line naming
/// the reason and does nothing else — no reply, so a spammer gets no echo to amplify.
///
/// <b>The budget is per session, and the session is what ends it</b> (job FEX2, R56 finding 6).
/// It is keyed on <c>User.Index</c>, which is a connection <em>slot</em> the game recycles, so
/// the table must be scrubbed when a user leaves or the next player to take that slot inherits
/// the previous one's spend and is refused <c>TooMany</c> on their first press, silently, with
/// no client-side feedback at all. <see cref="Attach"/> therefore subscribes to
/// <c>PlayerDisconnected</c> and <see cref="ForgetUser"/> drops the row — the same cleanup Core's
/// own handshake table does two files over (<c>HandshakeAnswerWatch.OnDisconnect</c>), which is
/// what makes the two halves of <see cref="Admit"/> agree about what a user is. The other policy
/// — a budget that survives a reconnect — would key on <c>User.PlatformId</c> instead; this file
/// deliberately wants the per-session one, because the cap exists to bound a stuck key rather
/// than to punish anybody.
///
/// <b>What the cap is bounding.</b> One accepted ping costs one <c>SendToUser</c> plus one
/// <c>SendToAll</c>, so with N users connected it produces N+1 outbound packets: the ceiling is
/// <see cref="MaxPacketsPerUser"/> × (N+1) per user per session. That fan-out is the point of the
/// demo — <c>SendToAll</c> is here to show the broadcast direction — and it is why the per-user
/// number is small. A real mod that answers a client packet to <em>everybody</em> should ask
/// whether it needs to.
///
/// <b>ServerMustHave.</b> <see cref="Plugin"/> carries
/// <c>[NetworkCompatibility(ServerMustHave, Exact)]</c> and calls <c>DeclareSelf()</c>, so a peer
/// without this plugin at this version is kicked at the handshake and never reaches this handler.
/// The handshake check below is still made rather than assumed: the level says what the framework
/// enforces, and a mod that changes its level later should not silently lose its gate.
///
/// The keybind is <b>not</b> <c>allowWhileCaptured</c>, unlike the Sample's panel toggles: a
/// panel is open, the player is clicking it, and firing a packet at the server is not what F8
/// meant. It is rebindable like every <c>ctx.Keybinds</c> bind — the cfg key is
/// <see cref="KeybindId"/> under <c>[Keybinds]</c> in the Sample's <c>GUID.cfg</c>, editable in
/// the settings panel (F10).
///
/// Framing constants, validation and every line are game-free so the test project compiles this
/// file; the sends, the receives and the keybind are behind <c>!PROGENITOR_TESTS</c>.
/// </summary>
internal static class SampleNet
{
    /// <summary>Log/grep area: <c>Sample net: …</c>.</summary>
    internal const string Area = "net";

    /// <summary>Client→server event id, ASCII <c>SmNp</c> ("sample net ping").</summary>
    internal const int PingEventId = 0x536D4E70;

    /// <summary>Server→client event id, ASCII <c>SmNr</c> ("sample net reply").</summary>
    internal const int PongEventId = 0x536D4E72;

    /// <summary>
    /// First field of every <see cref="Ping"/>, ASCII <c>SAMP</c>. A wrong-shaped packet that
    /// happens to be eight bytes long is refused here rather than being read as a counter.
    /// </summary>
    internal const int PingMagic = 0x53414D50;

    /// <summary>Largest counter the server will accept. A client that claims more is refused.</summary>
    internal const int MaxCounter = 1_000_000;

    /// <summary>
    /// Accepted packets per user per session, after which the sender is refused. The row is
    /// dropped on <c>PlayerDisconnected</c> (<see cref="ForgetUser"/>), so a reconnect starts
    /// over and the next player to inherit a recycled <c>User.Index</c> does not inherit a spend.
    /// </summary>
    internal const int MaxPacketsPerUser = 200;

    /// <summary>Presses one client may send per Core generation, so a stuck key cannot flood.</summary>
    internal const int MaxClientSends = 500;

    /// <summary>Cfg key of the keybind, under <c>[Keybinds]</c> in the Sample's <c>GUID.cfg</c>.</summary>
    internal const string KeybindId = "NetPing";

    /// <summary>The handshake answer status this plugin insists on before it answers a packet.</summary>
    internal const string RequiredAnswerStatus = "ok";

    /// <summary>Reply kind: sent to the one user whose packet was accepted.</summary>
    internal const int KindReply = 1;

    /// <summary>Notice kind: sent to every connected user so the broadcast direction is visible.</summary>
    internal const int KindNotice = 2;

    /// <summary>Why a packet was refused, or that it was accepted.</summary>
    internal enum Verdict
    {
        /// <summary>Accepted: reply and broadcast.</summary>
        Ok = 0,

        /// <summary>The payload was not exactly one <see cref="Ping"/>.</summary>
        BadSize = 1,

        /// <summary>The magic word did not match.</summary>
        BadMagic = 2,

        /// <summary>The counter was negative or above <see cref="MaxCounter"/>.</summary>
        BadCounter = 3,

        /// <summary>The sender's handshake answer is not <c>ok</c>.</summary>
        NotHandshaken = 4,

        /// <summary>This user is past <see cref="MaxPacketsPerUser"/> for this Core generation.</summary>
        TooMany = 5,
    }

    /// <summary>
    /// The whole mod-side validation, in one game-free function so it can be tested without a
    /// server. Order matters: cheapest and most specific first, so a refusal line names the real
    /// reason rather than the first gate a malformed packet happens to trip.
    /// </summary>
    /// <param name="readOk">What <c>CustomEvents.TryRead&lt;Ping&gt;</c> returned (a size check).</param>
    /// <param name="magic">The packet's magic field.</param>
    /// <param name="counter">The packet's counter field.</param>
    /// <param name="answerStatus">What <c>Handshake.UserAnswerStatus(user.Index)</c> returned.</param>
    /// <param name="acceptedSoFar">How many packets this user has already had accepted this session.</param>
    internal static Verdict Admit(bool readOk, int magic, int counter, string? answerStatus, int acceptedSoFar)
    {
        if (!readOk) return Verdict.BadSize;
        if (magic != PingMagic) return Verdict.BadMagic;
        if (counter < 0 || counter > MaxCounter) return Verdict.BadCounter;
        if (!string.Equals(answerStatus, RequiredAnswerStatus, StringComparison.Ordinal)) return Verdict.NotHandshaken;
        if (acceptedSoFar >= MaxPacketsPerUser) return Verdict.TooMany;
        return Verdict.Ok;
    }

    // ---- the per-session budget -----------------------------------------------------------------
    //
    // Game-free on purpose (job FEX2): the table, the arithmetic and the disconnect scrub are the
    // half of this file a mod author gets wrong, so they are unit tested rather than only run on a
    // server. Nothing in here names a game type; the caller passes the User.Index in.

    private static readonly object Gate = new();
    private static readonly Dictionary<int, int> AcceptedByUser = new();
    private static int _clientCounter;
    private static int _accepted;

    /// <summary>Accepted packets from one connection slot this session. 0 for a slot with no row.</summary>
    internal static int AcceptedFor(int userIndex)
    {
        lock (Gate) return AcceptedByUser.TryGetValue(userIndex, out int n) ? n : 0;
    }

    /// <summary>Record one accepted packet from a slot. Returns that slot's new total.</summary>
    internal static int NoteAccepted(int userIndex)
    {
        lock (Gate)
        {
            AcceptedByUser.TryGetValue(userIndex, out int current);
            int next = current + 1;
            AcceptedByUser[userIndex] = next;
            _accepted++;
            return next;
        }
    }

    /// <summary>
    /// Drop a slot's budget, on disconnect. Returns whether there was a row to drop, so the
    /// handler can stay quiet about the overwhelming majority of players who never pressed F8.
    /// </summary>
    internal static bool ForgetUser(int userIndex)
    {
        lock (Gate) return AcceptedByUser.Remove(userIndex);
    }

    /// <summary>How many connection slots currently hold a budget row. Diagnostics and tests.</summary>
    internal static int TrackedUsers
    {
        get { lock (Gate) return AcceptedByUser.Count; }
    }

    /// <summary>Accepted packets this Core generation, across all users. Diagnostics only.</summary>
    internal static int Accepted
    {
        get { lock (Gate) return _accepted; }
    }

    /// <summary>
    /// Take one client send from the per-generation ceiling. False when the ceiling is spent,
    /// which is the stuck-key case <see cref="MaxClientSends"/> exists for.
    /// </summary>
    internal static bool TryTakeClientSend(out int counter)
    {
        lock (Gate)
        {
            if (_clientCounter >= MaxClientSends)
            {
                counter = _clientCounter;
                return false;
            }

            counter = ++_clientCounter;
            return true;
        }
    }

    /// <summary>The disconnect line, so the scrub is visible in the log rather than inferred.</summary>
    internal static string ForgotUserLine(string? name, int userIndex, int accepted) =>
        "forgot the ping budget of " + Name(name) + " (user index " + N(userIndex)
        + ", accepted=" + N(accepted) + "): the slot is recycled, so the budget must not be.";

    /// <summary>Forget every budget and both counters. Attach and unload do this.</summary>
    internal static void Reset()
    {
        lock (Gate)
        {
            AcceptedByUser.Clear();
            _clientCounter = 0;
            _accepted = 0;
        }
    }

    /// <summary>The refusal line. One per refused packet, naming the sender and the reason.</summary>
    internal static string RefusedLine(string? sender, Verdict verdict) =>
        "refused ping from " + Name(sender) + " reason=" + verdict;

    /// <summary>The accepted line, with everything the round trip produced.</summary>
    internal static string AcceptedLine(string? sender, int counter, int accepted, bool replySent, int noticeRecipients) =>
        "ping from " + Name(sender) + " counter=" + N(counter) + " accepted=" + N(accepted)
        + " reply=" + (replySent ? "True" : "False") + " notice recipients=" + N(noticeRecipients);

    /// <summary>The client's line for one keypress.</summary>
    internal static string PressLine(int counter, bool sent) =>
        "F8 press counter=" + N(counter) + " sent=" + (sent ? "True" : "False");

    /// <summary>The client's line when it has spent its send budget.</summary>
    internal static string PressCappedLine(int max) =>
        "F8 press ignored: this Core generation has already sent " + N(max) + " ping(s).";

    /// <summary>The client's line for a packet coming back.</summary>
    internal static string ReceivedLine(int kind, int counter, int total) =>
        (kind == KindReply ? "reply" : kind == KindNotice ? "notice" : "packet kind=" + N(kind))
        + " counter=" + N(counter) + " total=" + N(total);

    /// <summary>The client's line for a reply it could not read or did not recognize.</summary>
    internal static string BadReplyLine(string why) => "ignored a server packet: " + why;

    /// <summary>Whether a kind field coming off the wire is one this client knows.</summary>
    internal static bool IsKnownKind(int kind) => kind == KindReply || kind == KindNotice;

    // ---- the reserved-id predicate (job J19) ------------------------------------------------
    //
    // `CustomEvents.IsReservedEventId` and `CustomEvents.ReservedEventIdPrefix` are the two public
    // members that tell a mod author which event ids are not theirs, and nothing in the repository
    // called either. `.net reserved <id>` runs the real predicate on an id typed in chat, and the
    // Attach line below runs it on the Sample's own two ids so the log says, every boot, that they
    // are outside the range rather than leaving it to be assumed.
    //
    // The predicate itself is a Core call and Core's `CustomEvents` is unsafe and game-bound, so
    // it cannot be compiled into the test suite. The answer is therefore a *parameter* here - the
    // same shape `Admit` uses - and a source pin asserts the live handler really passes
    // `CustomEvents.IsReservedEventId(id)` and not a local re-implementation of the mask. A second
    // copy of that rule in a sample is exactly how a sample goes stale.

    /// <summary>How an event id is written everywhere in this file: <c>0x536D4E70</c>.</summary>
    internal static string FormatEventId(int eventId) =>
        "0x" + eventId.ToString("X8", CultureInfo.InvariantCulture);

    /// <summary>Usage line for <c>.net</c>.</summary>
    internal const string UsageReply =
        "Usage: .net [status] | .net reserved <event id, e.g. 0x50726848 or 536D4E70>";

    /// <summary>Which branch of <c>.net</c> the caller asked for.</summary>
    internal enum CommandKind
    {
        /// <summary>No argument or <c>status</c>: the ids and the budgets.</summary>
        Status = 0,

        /// <summary><c>reserved &lt;id&gt;</c>: run Core's predicate on one id.</summary>
        Reserved = 1,
    }

    /// <summary>
    /// The whole <c>.net</c> grammar, game-free so the branch a chat line takes is unit tested
    /// rather than only exercised by typing it on a server.
    /// </summary>
    internal static bool TryParseCommand(string? what, string? arg, out CommandKind kind, out int eventId)
    {
        kind = CommandKind.Status;
        eventId = 0;

        string first = (what ?? "").Trim().ToLowerInvariant();
        string second = (arg ?? "").Trim();

        if (first.Length == 0 || first == "status")
            return second.Length == 0;

        if (first != "reserved") return false;

        kind = CommandKind.Reserved;
        return TryParseEventId(second, out eventId);
    }

    /// <summary>
    /// Parse the id argument of <c>.net reserved</c>: hex, with or without a <c>0x</c> prefix,
    /// one to eight digits. Decimal is deliberately not accepted — every id in the framework and
    /// in this file is written in hex, and guessing the base of <c>50726848</c> would make the
    /// answer depend on how the reader happened to type it.
    /// </summary>
    internal static bool TryParseEventId(string? token, out int eventId)
    {
        eventId = 0;
        if (string.IsNullOrWhiteSpace(token)) return false;

        string text = token!.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) text = text.Substring(2);
        if (text.Length == 0 || text.Length > 8) return false;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            bool hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
            if (!hex) return false;
        }

        eventId = unchecked((int)Convert.ToUInt32(text, 16));
        return true;
    }

    /// <summary>
    /// The <c>.net reserved</c> answer. <paramref name="reserved"/> is what
    /// <c>CustomEvents.IsReservedEventId</c> returned — this function never decides it, so the
    /// Sample cannot drift away from Core's rule.
    /// </summary>
    internal static string ReservedReply(int eventId, bool reserved) =>
        reserved
            ? FormatEventId(eventId) + " is RESERVED: CustomEvents.IsReservedEventId says yes. The "
                + "whole 0x" + ReservedPrefixText + "____ ('Pr**') range belongs to Progenitor's own "
                + "protocol. OnReceive/OnReceiveFromClient refuse a plugin handler on it and every "
                + "send entry point refuses to send on it, so a mod that picks one gets silence, not "
                + "an error at the far end."
            : FormatEventId(eventId) + " is free: CustomEvents.IsReservedEventId says no. Ids are "
                + "process-wide and one handler owns one id, so a second plugin that registers this "
                + "one is refused and the first registration is kept - derive yours from your own "
                + "four-letter tag, the way this file's " + FormatEventId(PingEventId) + " is ASCII "
                + "'SmNp'.";

    /// <summary>
    /// The Attach line that runs the predicate over this file's own ids. Written every boot
    /// because "our ids are outside the reserved range" is a claim, and a claim in a comment is
    /// not checked by anything.
    /// </summary>
    internal static string OwnIdsLine(bool pingReserved, bool pongReserved) =>
        "own event ids " + FormatEventId(PingEventId) + " ('SmNp') and " + FormatEventId(PongEventId)
        + " ('SmNr') reserved=" + (pingReserved ? "True" : "False") + "/"
        + (pongReserved ? "True" : "False")
        + " - both must be False; 0x" + ReservedPrefixText + "____ is Progenitor's range.";

    /// <summary>The reserved prefix as it is written in a reply. Kept as text so the reply is game-free.</summary>
    private const string ReservedPrefixText = "5072";

    /// <summary>The <c>.net status</c> reply: the ids, the budgets and what has been spent.</summary>
    internal static IReadOnlyList<string> StatusLines() => new[]
    {
        "net: ping=" + FormatEventId(PingEventId) + " pong=" + FormatEventId(PongEventId)
            + " (both outside Progenitor's reserved 0x" + ReservedPrefixText + "____ range)",
        "budgets: accepted=" + N(Accepted) + " trackedUsers=" + N(TrackedUsers)
            + " maxPerUser=" + N(MaxPacketsPerUser) + " maxClientSends=" + N(MaxClientSends),
    };

    private static string Name(string? sender) => string.IsNullOrEmpty(sender) ? "<unknown>" : sender!;

    private static string N(int value) => value.ToString(CultureInfo.InvariantCulture);

#if !PROGENITOR_TESTS
    /// <summary>
    /// Client→server payload. Fixed size, <c>unmanaged</c>, no strings: the transport copies
    /// <c>sizeof(Ping)</c> bytes and <c>TryRead</c> refuses anything else.
    /// </summary>
    internal struct Ping
    {
        public int Magic;
        public int Counter;
    }

    /// <summary>Server→client payload, used for both the personal reply and the broadcast notice.</summary>
    internal struct Pong
    {
        public int Kind;
        public int Counter;
        public int Total;
    }

    /// <summary>
    /// Registration for both sides. The server takes the client packet and the
    /// <c>PlayerDisconnected</c> scrub; the client takes the answer and owns the keybind. Every
    /// registration is tracked on the context, so unload drops the handler, the subscription and
    /// the bind.
    /// </summary>
    internal static unsafe void Attach(PluginContext ctx)
    {
        if (ctx == null) throw new ArgumentNullException(nameof(ctx));
        Reset();

        try
        {
            // Job J19. Run Core's own predicate over this file's ids on both sides, every boot,
            // rather than asserting in a comment that they are outside the reserved range.
            SampleLog.Info(Area, OwnIdsLine(
                CustomEvents.IsReservedEventId(PingEventId),
                CustomEvents.IsReservedEventId(PongEventId)));

            if (ctx.IsServer)
            {
                ctx.Net.OnReceiveFromClient(PingEventId, OnPingFromClient);
                // The budget is keyed on a recycled connection slot, so the scrub is not optional
                // bookkeeping: without it the next player in that slot is refused on arrival.
                ctx.Events.On<PlayerDisconnected>(OnPlayerDisconnected);
                // Job J19. `.net reserved <id>` runs the predicate on an id typed in chat.
                ctx.Commands.Register(typeof(SampleNetCommands));
                SampleLog.Info(Area, "server listening for " + Describe(PingEventId)
                    + "; replies on " + Describe(PongEventId) + ".");
                return;
            }

            ctx.Net.OnReceive(PongEventId, OnPongFromServer);
            ctx.Keybinds.Register(
                KeybindId,
                new KeyChord(UiKey.F8),
                "Send one sample ping packet to the server (client→server round trip demo).",
                SendPing);
            SampleLog.Info(Area, "client bound F8 to " + Describe(PingEventId)
                + "; listening for " + Describe(PongEventId) + ".");
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("Sample net attach threw: " + e);
        }
    }

    private static string Describe(int eventId) =>
        "event id 0x" + eventId.ToString("X8", CultureInfo.InvariantCulture);

    /// <summary>The keybind body. One packet per press, with a per-generation ceiling.</summary>
    internal static void SendPing()
    {
        try
        {
            var plugin = Plugin.Active;
            if (plugin == null) return;

            if (!TryTakeClientSend(out int counter))
            {
                SampleLog.Info(Area, PressCappedLine(MaxClientSends));
                return;
            }

            var ping = new Ping { Magic = PingMagic, Counter = counter };
            bool sent = plugin.Net.SendToServer(PingEventId, ping);
            SampleLog.Info(Area, PressLine(counter, sent));
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("Sample net keybind threw: " + e);
        }
    }

    /// <summary>
    /// Server receive. The sender arrives resolved (Core refuses any connection it cannot match
    /// to an approved user row), so this only has to decide whether the <em>packet</em> is
    /// acceptable and then answer it.
    /// </summary>
    private static unsafe void OnPingFromClient(int eventId, Entity userEntity, User user, byte* payload, int length)
    {
        try
        {
            if (eventId != PingEventId) return;

            var plugin = Plugin.Active;
            if (plugin == null) return;

            bool readOk = CustomEvents.TryRead<Ping>(payload, length, out var ping);
            string sender = user.CharacterName.ToString();
            string status = Handshake.UserAnswerStatus(user.Index);

            int soFar = AcceptedFor(user.Index);

            var verdict = Admit(readOk, readOk ? ping.Magic : 0, readOk ? ping.Counter : -1, status, soFar);
            if (verdict != Verdict.Ok)
            {
                SampleLog.Info(Area, RefusedLine(sender, verdict));
                return;
            }

            int accepted = NoteAccepted(user.Index);

            bool replySent = plugin.Net.SendToUser(
                userEntity, PongEventId, new Pong { Kind = KindReply, Counter = ping.Counter, Total = accepted });
            int recipients = plugin.Net.SendToAll(
                PongEventId, new Pong { Kind = KindNotice, Counter = ping.Counter, Total = accepted });

            SampleLog.Info(Area, AcceptedLine(sender, ping.Counter, accepted, replySent, recipients));
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("Sample net server receive threw: " + e);
        }
    }

    /// <summary>Client receive. Logs the two kinds and ignores anything it cannot read.</summary>
    private static unsafe void OnPongFromServer(int eventId, byte* payload, int length)
    {
        try
        {
            if (eventId != PongEventId) return;
            if (!CustomEvents.TryRead<Pong>(payload, length, out var pong))
            {
                SampleLog.Info(Area, BadReplyLine("payload was " + N(length) + " byte(s), not one Pong"));
                return;
            }

            if (!IsKnownKind(pong.Kind))
            {
                SampleLog.Info(Area, BadReplyLine("unknown kind " + N(pong.Kind)));
                return;
            }

            SampleLog.Info(Area, ReceivedLine(pong.Kind, pong.Counter, pong.Total));
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("Sample net client receive threw: " + e);
        }
    }

    /// <summary>
    /// Disconnect scrub (job FEX2, R56 finding 6). <c>User.Index</c> is a connection slot the
    /// game recycles, so a budget row that outlives its owner is charged to the next player who
    /// lands in that slot.
    /// </summary>
    private static void OnPlayerDisconnected(PlayerDisconnected ev)
    {
        try
        {
            if (ev == null) return;
            int index = ev.User.Index;
            int accepted = AcceptedFor(index);
            if (!ForgetUser(index)) return;      // never pressed F8: nothing to say
            SampleLog.Info(Area, ForgotUserLine(ev.User.CharacterName.ToString(), index, accepted));
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("Sample net disconnect handler threw: " + e);
        }
    }

    /// <summary>
    /// Server entry point for <c>.net reserved &lt;id&gt;</c> (job J19). The one live call in this
    /// feature is <c>CustomEvents.IsReservedEventId</c>, whose answer the game-free
    /// <see cref="ReservedReply"/> then formats. A source pin asserts this call is here and
    /// uncommented, because a Sample that re-implemented the mask locally would keep passing its
    /// own tests forever while Core's rule moved.
    /// </summary>
    internal static void HandleReserved(ICommandContext ctx, int eventId)
    {
        string line = ReservedReply(eventId, CustomEvents.IsReservedEventId(eventId));
        ctx.Reply(line);
        SampleLog.Info(Area, line);
    }
#endif

    /// <summary>
    /// Unload. Core drops the receive handlers, the disconnect subscription and the keybind (all
    /// tracked on the context) on every reason; the per-user counts are this generation's and
    /// must not outlive it — keeping them would mean a hot reload silently inherits somebody's
    /// spent budget.
    /// </summary>
    internal static void Detach() => Reset();
}

#if !PROGENITOR_TESTS
/// <summary>
/// <c>.net</c> (job J19). Registered from <see cref="SampleNet.Attach"/> with
/// <c>ctx.Commands.Register(Type)</c> on the server only.
/// </summary>
internal static class SampleNetCommands
{
    // Not adminOnly: both branches are reads of this plugin's own constants and of a pure
    // predicate over an integer the caller typed. Nothing is sent, nothing is written, and no
    // other mod's ids are visible - `.net reserved` cannot tell a player anything about the
    // server that the framework's documentation does not.
    [Command("net", usage: ".net [status] | .net reserved <event id>", description: "Report the sample packet ids and budgets, or ask CustomEvents.IsReservedEventId whether an event id belongs to Progenitor.")]
    public static void Net(ICommandContext ctx, string what = "", string arg = "")
    {
        try
        {
            if (!SampleNet.TryParseCommand(what, arg, out var kind, out int eventId))
            {
                ctx.Reply(SampleNet.UsageReply);
                return;
            }

            if (kind == SampleNet.CommandKind.Status)
            {
                foreach (string line in SampleNet.StatusLines())
                    ctx.Reply(line);
                return;
            }

            SampleNet.HandleReserved(ctx, eventId);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("Sample .net threw: " + e);
            ctx.Reply("Sample net failed. See the server log.");
        }
    }
}
#endif
