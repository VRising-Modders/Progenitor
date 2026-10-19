using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Progenitor.Networking;

/// <summary>
/// Who holds an event id. Two shapes, and the difference is the whole point (job FEX4, R57
/// finding 10).
///
/// <para><b>Attributed:</b> the simple name of the assembly that declares the handler. Compared by
/// name rather than by <see cref="System.Reflection.Assembly"/> identity so a plugin hot reload —
/// which comes back from a fresh collectible assembly with the same name — is still the same owner
/// and may re-register its own ids.</para>
///
/// <para><b>Core is not one of the names</b> (job FEX6, R58 finding 12). "Is this the same plugin"
/// and "is this Core" are different questions and used to share one answer: both compared a simple
/// name against <c>"Progenitor"</c>, so a plugin whose <c>&lt;AssemblyName&gt;</c> happened to be
/// <c>Progenitor</c> — a fork built from this tree with the project file unchanged, a mod shipping a
/// private copy of <c>Progenitor.dll</c> — <em>became</em> Core: allowed inside the reserved range
/// on registration and on send alike, on <c>PrhH</c> / <c>PrfD</c> / <c>PrcS</c> and the rest. A
/// name collision between two plugins costs them an id; a name collision with Core takes the
/// protection away. So <see cref="IsCore"/> is a flag only <see cref="ForCore"/> can set, and the
/// only caller of <see cref="ForCore"/> compares the declaring assembly by reference against
/// <c>typeof(CustomEvents).Assembly</c>. Rule 2's by-name comparison is untouched — a plugin named
/// <c>Progenitor</c> is just another plugin named <c>Progenitor</c>.</para>
///
/// <para><b>Un-attributable:</b> a delegate whose <c>Target</c> is null and whose <c>Method</c> has
/// no declaring type (a dynamic method, a delegate over a global function in a merged assembly), or
/// any throw out of <c>Assembly.GetName()</c>. These used to collapse into the single name
/// <c>""</c>, so the one-owner rule compared <c>""</c> against <c>""</c>, found them equal, and let
/// any un-attributable registrant replace any other — the precise case rule 2 exists to stop, off
/// for exactly the delegates nothing can name. An un-attributable owner is now a <b>distinct
/// identity per registration</b>: the registering delegate instance, compared by reference. Two of
/// them can never match, so neither can take an id the other holds; re-registering the very same
/// delegate instance still counts as the same owner and replaces its own handler.</para>
/// </summary>
internal readonly struct EventIdOwner : IEquatable<EventIdOwner>
{
    private readonly string? _name;
    private readonly object? _identity;
    private readonly bool _isCore;

    private EventIdOwner(string? name, object? identity, bool isCore)
    {
        _name = name;
        _identity = identity;
        _isCore = isCore;
    }

    /// <summary>An owner that could be named: the assembly simple name. Never Core.</summary>
    public static EventIdOwner Named(string name) =>
        string.IsNullOrEmpty(name) ? throw new ArgumentException("an attributed owner needs a name", nameof(name))
                                   : new EventIdOwner(name, null, isCore: false);

    /// <summary>
    /// Progenitor Core itself. The <em>only</em> way to get <see cref="IsCore"/>, and its one caller
    /// establishes it by <c>ReferenceEquals</c> against Core's own assembly — never from
    /// <paramref name="name"/>, which is carried for the log lines alone (job FEX6, R58 finding 12).
    /// </summary>
    public static EventIdOwner ForCore(string name) =>
        new EventIdOwner(string.IsNullOrEmpty(name) ? "Progenitor" : name, null, isCore: true);

    /// <summary>
    /// An owner nothing could name. <paramref name="registration"/> is the registering delegate
    /// instance and is the identity: reference equality, so no two un-attributable registrations
    /// are ever the same owner.
    /// </summary>
    public static EventIdOwner Anonymous(object registration) =>
        new EventIdOwner(null, registration ?? new object(), isCore: false);

    /// <summary>False when the owner could not be named; see the type docstring.</summary>
    public bool IsAttributed => _name != null;

    /// <summary>
    /// True only for an owner built by <see cref="ForCore"/>, i.e. one whose declaring assembly
    /// <em>is</em> Core's assembly. Not derivable from <see cref="Name"/>: see the type docstring.
    /// </summary>
    public bool IsCore => _isCore;

    /// <summary>The assembly simple name, or <c>""</c> for an un-attributable owner.</summary>
    public string Name => _name ?? "";

    /// <summary>How the owner reads in a log line.</summary>
    public string Describe() => _name ?? "<unknown assembly>";

    // Equality is deliberately blind to IsCore: it answers rule 2's question ("is this the same
    // registrant") and that one is by simple name, hot reload included. Rule 1's question ("is this
    // Core") is IsCore and nothing else.
    public bool Equals(EventIdOwner other)
    {
        if (_name == null || other._name == null)
            return _name == null && other._name == null && ReferenceEquals(_identity, other._identity);
        return string.Equals(_name, other._name, StringComparison.Ordinal);
    }

    public override bool Equals(object? obj) => obj is EventIdOwner other && Equals(other);

    public override int GetHashCode() =>
        _name != null ? StringComparer.Ordinal.GetHashCode(_name) : RuntimeHelpers.GetHashCode(_identity!);

    public override string ToString() => Describe();
}

/// <summary>What a receive path does with one packet (<see cref="CustomEventPolicy.RouteReceive"/>).</summary>
internal enum ReceiveRoute
{
    /// <summary>A handler holds the id: call it, then consume the packet.</summary>
    Dispatch,

    /// <summary>No handler, reserved id: consume the packet without calling anything, logged once per id.</summary>
    DropReserved,

    /// <summary>No handler, not reserved: hand the packet to the game.</summary>
    PassToGame,
}

/// <summary>
/// Logs a dropped reserved id with no handler once per id, so a peer resending it every few seconds writes one
/// line, not one per packet. Bounded: the reserved range has 65536 ids and the sender is a remote peer, so past
/// <see cref="MaxIds"/> distinct ids nothing more is logged (the packets are still dropped). Thread-safe.
/// </summary>
internal sealed class ReservedDropGate
{
    /// <summary>How many distinct ids the gate remembers.</summary>
    public const int MaxIds = 64;

    private readonly HashSet<int> _logged = new();

    /// <summary>True the first time <paramref name="eventId"/> is seen, while fewer than <see cref="MaxIds"/> are remembered.</summary>
    public bool Note(int eventId)
    {
        lock (_logged)
        {
            if (_logged.Contains(eventId) || _logged.Count >= MaxIds) return false;
            return _logged.Add(eventId);
        }
    }

    /// <summary>Forget every logged id (tests; a Core swap replaces the instance). Not named Clear: the handler table's pin forbids a bare Clear in this file.</summary>
    public void ForgetLogged()
    {
        lock (_logged) _logged.Clear();
    }
}

/// <summary>What happened to one registration.</summary>
internal enum EventRegistrationResult
{
    /// <summary>Stored; this id had no handler before.</summary>
    Stored,

    /// <summary>Stored; it replaced a handler the same owner had registered on this id.</summary>
    ReplacedOwn,

    /// <summary>Nothing stored and nothing owned. <c>log</c> carries the sentence to write.</summary>
    Refused,
}

/// <summary>
/// The event-id rules, with no game types in them (job FEX4, R57 findings 3, 4 and 10).
///
/// <para>The rules used to live inside <c>CustomEvents</c>, which is <c>unsafe</c> and binds
/// ProjectM, Stunlock.Network and Unity, so it cannot be compiled into the test suite — and the
/// only thing standing between the repository and a refusal that refuses nothing was a source pin
/// that did not pin the <c>return</c>. R57 finding 4 measured that: deleting both <c>return;</c>
/// statements, so every refusal logged an error and then stored the handler anyway, passed all
/// twenty-five tests. The decision and the store now live here together, so a test drives the real
/// thing: <see cref="EventHandlerTable{THandler}.Register"/> is the only path that stores, and a
/// refusal cannot reach it.</para>
/// </summary>
internal static class CustomEventPolicy
{
    /// <summary>
    /// Top two bytes of every id Progenitor reserves for itself: ASCII <c>"Pr"</c>. Every one of
    /// Core's twelve protocol ids is in this range and every future one will be, so the range —
    /// not the twelve — is what a plugin must stay out of.
    /// </summary>
    public const int ReservedEventIdPrefix = 0x5072;

    /// <summary>
    /// <c>'PrsQ'</c> — job SERVERINST4's graceful <b>Stop server</b> channel, and the one reserved
    /// id declared here rather than beside its protocol (the stop wire lives under
    /// <c>Install/</c>, which has no other event ids, and a reader looking for "which ids does
    /// Core own" should find it with the range rule).
    ///
    /// <para>One id carries both directions, the way <c>'PrSe'</c> does for
    /// <see cref="ServerSettings"/>: the client sends <c>StopServerRequest</c> on it through
    /// <c>SendToServer</c> and the server answers <c>StopServerReply</c> on it through
    /// <c>SendToUser</c>. The two payloads are different sizes and each side has only the one
    /// handler direction registered, so a reply can never be read as a request. The neighboring
    /// ids are <c>'Prh*'</c> (handshake), <c>'Prf*'</c> (prefabs), <c>'Prc*'</c> (config sync),
    /// <c>'Prm*'</c> (mod manifest) and <c>'PrSe'</c> (server settings); <c>'Prs*'</c> was
    /// free.</para>
    ///
    /// <para>It is inside <see cref="ReservedEventIdPrefix"/>, so only Core may register or send on
    /// it — which is the point: a plugin that could send this id could ask an admin's server to
    /// save and quit.</para>
    /// </summary>
    public const int StopServerEventId = 0x50727351;

    /// <summary>
    /// <c>'PrRi'</c> — job REMOTEINST1's admin remote install channel, declared here for the same
    /// reason as <see cref="StopServerEventId"/> (its protocol lives under <c>Install/</c>). One id
    /// carries both directions; the payload's own direction byte (<c>RemoteInstallWire</c>) keeps
    /// a reply from ever decoding as a request. Inside the reserved range, so only Core may register
    /// or send on it: a plugin that could send this id could ask an admin's server to install code.
    /// </summary>
    public const int RemoteInstallEventId = 0x50725269;

    /// <summary>
    /// <c>'PrJa'</c> — JOINADMIN1's admin "Players joining" channel (join flow stage 3): the recommended and shared
    /// mods and the install window. One id carries both directions; the payload's kind byte
    /// (<c>JoinAdminWire</c>) keeps an answer from ever decoding as a request. Inside the reserved range, so only
    /// Core may register or send on it: a plugin that could send this id could ask a server to share a mod.
    /// </summary>
    public const int JoinAdminEventId = 0x50724A61;

    /// <summary>
    /// <c>'PrSv'</c> — settings organization stages 6 and 7: the Server rules channel (the vanilla settings behind the Join
    /// Game browser). One id carries both directions; the payload's kind byte (<c>ServerRulesWire</c>) keeps an answer from
    /// ever decoding as a request. Inside the reserved range, so only Core may register or send on it: a plugin that could
    /// send this id could ask a server to change its password or schedule a wipe.
    /// </summary>
    public const int ServerRulesEventId = 0x50725376;

    /// <summary>
    /// <c>'PrCh'</c> — the chat command hints channel (<c>Chat/ChatCommandHintsWire.cs</c>): the server's hello and
    /// catalog pages, the client's catalog request. One id carries both directions; the payload's kind byte keeps an
    /// answer from ever decoding as a request. The server sends only to a client that declared
    /// <c>Handshake.Capability.CommandHints</c>, and the client sends only after the server's hello. Inside the reserved
    /// range, so only Core may register or send on it.
    /// </summary>
    public const int ChatCommandHintsEventId = 0x50724368;

    /// <summary>True when <paramref name="eventId"/> falls in the reserved <c>0x5072____</c> range.</summary>
    public static bool IsReserved(int eventId) => ((eventId >> 16) & 0xFFFF) == ReservedEventIdPrefix;

    /// <summary>
    /// What a receive path does with one packet's event id, given whether a handler holds it.
    /// <list type="bullet">
    /// <item><see cref="ReceiveRoute.Dispatch"/>: a handler holds the id; call it and consume the packet.</item>
    /// <item><see cref="ReceiveRoute.DropReserved"/>: no handler, but the id is in Progenitor's reserved range.
    /// Consume it anyway. A reserved id is never a game event, and passing it on makes the game's event reader
    /// reject it as malformed and log a line per packet. This happens whenever this side's feature that would
    /// read the id is off (a client whose Bootstrap lacks the seams that feature needs, say) while the peer
    /// still sends it.</item>
    /// <item><see cref="ReceiveRoute.PassToGame"/>: no handler and not reserved; the game's own traffic.</item>
    /// </list>
    /// </summary>
    public static ReceiveRoute RouteReceive(bool hasHandler, int eventId)
    {
        if (hasHandler) return ReceiveRoute.Dispatch;
        return IsReserved(eventId) ? ReceiveRoute.DropReserved : ReceiveRoute.PassToGame;
    }

    /// <summary>The id as its four characters (<c>'PrMv'</c>), or its hex form when they are not printable.</summary>
    public static string FourCc(int eventId)
    {
        var chars = new char[4];
        for (int i = 0; i < 4; i++)
        {
            int b = (eventId >> (24 - 8 * i)) & 0xFF;
            if (b < 0x20 || b > 0x7E) return Format(eventId);
            chars[i] = (char)b;
        }
        return "'" + new string(chars) + "'";
    }

    /// <summary>The line written the first time a reserved id with no handler is dropped on <paramref name="side"/>.</summary>
    public static string UnhandledReservedLog(int eventId, string side) =>
        $"CustomEvents: no {side} handler for reserved event {FourCc(eventId)} ({Format(eventId)}); dropped. "
        + "The feature that reads it is not running on this side; further drops of this id are not logged.";

    /// <summary>How an id reads in a log line.</summary>
    public static string Format(int eventId) => "0x" + eventId.ToString("X8");

    private static string ReservedTail() =>
        $"0x{ReservedEventIdPrefix:X4}____ ('Pr**') is Progenitor's own range and is reserved for the "
        + "framework's protocol. Pick an id derived from your own four-letter tag instead.";

    /// <summary>
    /// May <paramref name="owner"/> take <paramref name="eventId"/>, given whoever holds it now?
    /// Returns null to allow, or the sentence to log on a refusal.
    /// </summary>
    public static string? RefuseRegistration(
        int eventId, in EventIdOwner owner, EventIdOwner? currentOwner, string side)
    {
        if (IsReserved(eventId) && !owner.IsCore)
            return $"CustomEvents: refusing a {side} handler for event id {Format(eventId)} from "
                + $"'{owner.Describe()}': " + ReservedTail();

        if (currentOwner is { } held && !held.Equals(owner))
            return $"CustomEvents: refusing a {side} handler for event id {Format(eventId)} from "
                + $"'{owner.Describe()}': '{held.Describe()}' already owns it. Event ids are process-wide "
                + "and one handler owns one id; the earlier registration is kept.";

        return null;
    }

    /// <summary>
    /// The same range rule on the way out (R57 finding 3). Six send entry points used to implement
    /// none of it, so an author who picked <c>0x50726D4D</c> because it looked distinctive got one
    /// red line saying their <c>OnReceive</c> was refused — which reads as "this id does nothing
    /// for me" — while their <c>SendToAll</c> on it still fed every connected client's
    /// <c>ModManifest</c> handler a foreign payload, and a client's <c>SendToServer(0x50726852)</c>
    /// still reached Core's handshake. Ownership cannot be attributed from a send the way it can
    /// from a delegate, so the range check alone is the honest half — and it is the half that
    /// protects Core's protocol. Returns null to allow.
    ///
    /// <para><paramref name="senderIsCore"/> is decided by the caller from a <c>ReferenceEquals</c>
    /// against Core's own assembly, never from <paramref name="owner"/> — which is the sender's name
    /// for the log line and nothing more (job FEX6, R58 finding 12). The old form compared
    /// <paramref name="owner"/> against the string <c>"Progenitor"</c>, so a plugin assembly with
    /// that simple name could send on any reserved id.</para>
    /// </summary>
    public static string? RefuseSend(int eventId, string owner, bool senderIsCore)
    {
        if (!IsReserved(eventId)) return null;
        if (senderIsCore) return null;
        string who = string.IsNullOrEmpty(owner) ? "<unknown assembly>" : owner;
        return $"CustomEvents: refusing to send event id {Format(eventId)} from '{who}': " + ReservedTail();
    }

    /// <summary>
    /// The third direction into the table (job FEX6, R58 finding 1). Registration and send were
    /// gated and removal was not, so <c>CustomEvents.RemoveHandler(id)</c> was a public,
    /// argument-free way to do both things the other two rules forbid: drop plugin B's handler and
    /// leave the id free for the next <c>OnReceive</c> (rule 2 bypassed in two lines), and silence
    /// Core's own <c>PrfD</c> / <c>PrhR</c> handler for the rest of the generation — purely
    /// destructive, since the remover can neither register nor send on a reserved id, and silent,
    /// because <c>ReceiveSlotHandoff</c> still routes the fourcc and the miss drops the packet.
    ///
    /// <para>The rule is the registration rule read backwards: <b>only the owner, or Core, may
    /// remove.</b> An id nobody holds is not refused (there is nothing to protect and nothing to
    /// remove). Ownership is the same <see cref="EventIdOwner"/> comparison, so a hot reload can
    /// still drop its own handlers. Returns null to allow.</para>
    ///
    /// <para><b><paramref name="remover"/> is the calling assembly, on every removal path</b> (job
    /// FEX8, R59 findings 2 and 7). It used to be derived from the presented handler on the
    /// handler-matched overloads, which made this comparison a tautology: the recorded owner was
    /// computed at registration as <c>OwnerOf(thatSameDelegate)</c> and the remover was
    /// <c>OwnerOf</c> of it again, so the two sides were equal by construction and <b>no argument
    /// pair reachable through the public API could be refused</b> — a plugin holding somebody
    /// else's <c>CustomEventHandler</c> (from a shared registry, a published field, an event
    /// payload) removed it, silently, while the docstring said it could not. One attribution now
    /// serves both overloads, which also removes the asymmetry R59 finding 7 named: the id-only
    /// form always attributed the caller, so a plugin whose handler is declared in a library it
    /// ships was refused on one form and allowed on the other, for the same removal.</para>
    ///
    /// <para>The cost is stated rather than hidden. A handler declared in a helper library must be
    /// removed <em>from</em> that library — or through <c>PluginNet</c>, whose unload disposables
    /// call from Core — and an <b>un-attributable holder</b> (its own identity per registration)
    /// can be removed only by Core: no calling assembly can equal an identity that is a delegate
    /// instance. That is the honest end of "nothing can name it", and the tracked path
    /// (<c>PluginNet.OnReceive</c>'s handle) still works, because Core is doing the removing.</para>
    /// </summary>
    public static string? RefuseRemoval(
        int eventId, EventIdOwner? currentOwner, in EventIdOwner remover, string side)
    {
        if (currentOwner is not { } held) return null;
        if (remover.IsCore || held.Equals(remover)) return null;

        return $"CustomEvents: refusing to remove the {side} handler for event id {Format(eventId)} on "
            + $"behalf of '{remover.Describe()}': '{held.Describe()}' owns it. Removing somebody else's "
            + "handler would silence it for the rest of this Core generation — the fourcc is still "
            + "routed and the packet is dropped on the miss — and would free the id for a registration "
            + "the one-owner rule refuses head-on.";
    }

    /// <summary>
    /// May <paramref name="caller"/> erase the <em>whole</em> table? Only Core may (job FEX8, R59
    /// finding 8): a Core uninstall genuinely drops every row without asking, and nothing else has
    /// a reason to. Returns null to allow, or the sentence to log.
    /// </summary>
    public static string? RefuseClear(in EventIdOwner caller, string side)
    {
        if (caller.IsCore) return null;
        return $"CustomEvents: refusing to clear the whole {side} handler table on behalf of "
            + $"'{caller.Describe()}': only Progenitor Core may do that, and only on uninstall. "
            + "Remove your own ids one at a time.";
    }

    /// <summary>The line written once when a registration's owner could not be named.</summary>
    public static string UnattributedOwnerLog(int eventId, string side) =>
        $"CustomEvents: could not attribute the {side} handler for event id {Format(eventId)} to an "
        + "assembly; it is treated as its own owner, so it can neither take nor lose an id to another "
        + "un-attributable handler.";

    // ---- log gates: one line per id per Core generation ---------------------------------------
    //
    // These are statics on a Core type, so a Core swap resets them by construction: "per
    // generation" needs no clock and no eviction. A refused send is a per-call event on a hot path
    // and the author only needs to be told once.

    private static readonly HashSet<int> LoggedSendRefusals = new();
    private static readonly HashSet<int> LoggedRemovalRefusals = new();

    /// <summary>
    /// The once-per-id gate for dropped reserved ids with no handler, shared by both sides' tables this
    /// generation. A test gives its table its own <see cref="ReservedDropGate"/> instead.
    /// </summary>
    public static readonly ReservedDropGate UnhandledReserved = new();

    private static bool _notedUnattributedOwner;

    /// <summary>True the first time <paramref name="eventId"/> is refused on a send this generation.</summary>
    public static bool NoteSendRefusal(int eventId)
    {
        lock (LoggedSendRefusals) return LoggedSendRefusals.Add(eventId);
    }

    /// <summary>
    /// True the first time <paramref name="eventId"/> is refused on a removal this generation. A
    /// removal loop in a plugin's teardown would otherwise write the same line per id per unload.
    /// </summary>
    public static bool NoteRemovalRefusal(int eventId)
    {
        lock (LoggedSendRefusals) return LoggedRemovalRefusals.Add(eventId);
    }

    /// <summary>True the first time an un-attributable owner is seen this generation.</summary>
    public static bool NoteUnattributedOwner()
    {
        lock (LoggedSendRefusals)
        {
            if (_notedUnattributedOwner) return false;
            _notedUnattributedOwner = true;
            return true;
        }
    }

    /// <summary>
    /// Forget what has been logged. Production never calls this — a Core swap replaces the statics,
    /// which is what "per generation" means — but a test needs to run the gate more than once.
    /// </summary>
    public static void ResetLogGates()
    {
        lock (LoggedSendRefusals)
        {
            LoggedSendRefusals.Clear();
            LoggedRemovalRefusals.Clear();
            UnhandledReserved.ForgetLogged();
            _notedUnattributedOwner = false;
        }
    }
}

/// <summary>
/// One side's event-id table: the handlers, their owners, and the only path that stores or erases
/// either.
/// Game-free so the refusals are driven by behavior rather than pinned in source (R57 finding 4).
/// Not thread-safe on its own; the dispatcher locks the table instance, exactly as it locked the
/// handler dictionary before.
/// </summary>
internal sealed class EventHandlerTable<THandler> where THandler : class
{
    private readonly Dictionary<int, THandler> _handlers = new();
    private readonly Dictionary<int, EventIdOwner> _owners = new();
    private readonly string _side;
    private readonly ReservedDropGate _dropGate;

    /// <param name="side">"client" or "server"; appears in the refusal lines.</param>
    /// <param name="dropGate">The once-per-id log gate for <see cref="Route"/>; the shared generation gate when null.</param>
    public EventHandlerTable(string side, ReservedDropGate? dropGate = null)
    {
        _side = side ?? "";
        _dropGate = dropGate ?? CustomEventPolicy.UnhandledReserved;
    }

    public int Count => _handlers.Count;

    public bool TryGetHandler(int eventId, out THandler? handler) => _handlers.TryGetValue(eventId, out handler);

    /// <summary>
    /// The receive decision for one packet (<see cref="CustomEventPolicy.RouteReceive"/>), with the handler when
    /// it is <see cref="ReceiveRoute.Dispatch"/>. <paramref name="log"/> carries the once-per-id line the first
    /// time a reserved id with no handler is dropped, and is null otherwise. Both dispatchers call this, so the
    /// tests drive the same decision the receive hooks make.
    /// </summary>
    public ReceiveRoute Route(int eventId, out THandler? handler, out string? log)
    {
        log = null;
        bool has = _handlers.TryGetValue(eventId, out handler) && handler != null;
        var route = CustomEventPolicy.RouteReceive(has, eventId);
        if (route == ReceiveRoute.DropReserved && _dropGate.Note(eventId))
            log = CustomEventPolicy.UnhandledReservedLog(eventId, _side);
        return route;
    }

    public bool TryGetOwner(int eventId, out EventIdOwner owner) => _owners.TryGetValue(eventId, out owner);

    /// <summary>True when some handler holds <paramref name="eventId"/>.</summary>
    public bool Owns(int eventId) => _handlers.ContainsKey(eventId);

    /// <summary>
    /// Store <paramref name="handler"/> under <paramref name="eventId"/> — unless one of the two
    /// rules refuses it, in which case <b>nothing is stored and nothing is owned</b>. There is no
    /// store outside this method, so a refusal cannot be followed by one.
    /// </summary>
    public EventRegistrationResult Register(int eventId, THandler handler, in EventIdOwner owner, out string? log)
    {
        if (handler == null) throw new ArgumentNullException(nameof(handler));

        EventIdOwner? currentOwner = _owners.TryGetValue(eventId, out var held) ? held : null;
        log = CustomEventPolicy.RefuseRegistration(eventId, owner, currentOwner, _side);
        if (log != null) return EventRegistrationResult.Refused;

        bool replaced = _handlers.TryGetValue(eventId, out var existing) && !ReferenceEquals(existing, handler);
        _handlers[eventId] = handler;
        _owners[eventId] = owner;
        if (!replaced) return EventRegistrationResult.Stored;

        log = $"CustomEvents: event id {CustomEventPolicy.Format(eventId)} already has a {_side} handler "
            + "from the same owner; replacing it.";
        return EventRegistrationResult.ReplacedOwn;
    }

    /// <summary>
    /// Drop whatever handler currently owns <paramref name="eventId"/> — if
    /// <paramref name="remover"/> is that owner, or is Core. Otherwise <b>nothing is removed</b> and
    /// <paramref name="log"/> carries the refusal (job FEX6, R58 finding 1). As with
    /// <see cref="Register"/>, there is no <b>plugin-reachable</b> removal path that skips the
    /// decision: this method and its handler-matched sibling are the only things that erase a
    /// single row, and <see cref="ClearForUninstall"/> — which drops the whole table — asks the
    /// same identity question and refuses anyone but Core (job FEX8, R59 finding 8; the sentence
    /// used to say "the only things that erase a row" with an unchecked <c>Clear()</c> eleven
    /// lines below it).
    /// </summary>
    public bool Remove(int eventId, in EventIdOwner remover, out string? log)
    {
        EventIdOwner? currentOwner = _owners.TryGetValue(eventId, out var held) ? held : null;
        log = CustomEventPolicy.RefuseRemoval(eventId, currentOwner, remover, _side);
        if (log != null) return false;

        _owners.Remove(eventId);
        return _handlers.Remove(eventId);
    }

    /// <summary>
    /// Drop <paramref name="eventId"/> only if <paramref name="handler"/> is still the registered
    /// one, so unloading plugin A cannot silence plugin B — and only if <paramref name="remover"/>
    /// owns it or is Core, so presenting somebody else's delegate is not a second way in. The
    /// handler mismatch is the quiet case (a later registrant took the id) and writes no log; the
    /// owner refusal is the loud one.
    ///
    /// <para><paramref name="remover"/> is the <b>calling</b> assembly, exactly as on the sibling
    /// overload (job FEX8, R59 finding 2). Deriving it from <paramref name="handler"/> — which is
    /// what this overload used to be handed — made the check a tautology, because the recorded
    /// owner was derived from the same delegate at registration. Holding the delegate is a
    /// necessary proof here and not a sufficient one.</para>
    /// </summary>
    public bool Remove(int eventId, THandler handler, in EventIdOwner remover, out string? log)
    {
        if (handler == null) throw new ArgumentNullException(nameof(handler));
        log = null;
        if (!_handlers.TryGetValue(eventId, out var current) || !ReferenceEquals(current, handler)) return false;

        EventIdOwner? currentOwner = _owners.TryGetValue(eventId, out var held) ? held : null;
        log = CustomEventPolicy.RefuseRemoval(eventId, currentOwner, remover, _side);
        if (log != null) return false;

        _handlers.Remove(eventId);
        _owners.Remove(eventId);
        return true;
    }

    /// <summary>
    /// Drop every row, for Core's own uninstall — the one erasure that is allowed to skip the
    /// per-id decision, because Core is tearing its own table down (job FEX8, R59 finding 8).
    /// Anyone else is refused and nothing is erased: this used to be a bare <c>Clear()</c> with no
    /// decision at all, sitting under a docstring that said the two <see cref="Remove(int, in EventIdOwner, out string?)"/>
    /// overloads were the only things that erase a row. Returns how many rows went.
    /// </summary>
    public int ClearForUninstall(in EventIdOwner caller, out string? log)
    {
        log = CustomEventPolicy.RefuseClear(caller, _side);
        if (log != null) return 0;

        int count = _handlers.Count;
        _handlers.Clear();
        _owners.Clear();
        return count;
    }
}
