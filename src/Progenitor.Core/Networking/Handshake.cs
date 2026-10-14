using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Progenitor.Plugins;
#if !PROGENITOR_TESTS
using System.Runtime.InteropServices;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;
using Progenitor.Config;
using Progenitor.Ecs;
using Progenitor.Events;
using ProjectM;
using ProjectM.Network;
using Unity.Entities;
#endif

namespace Progenitor.Networking;

/// <summary>
/// Mod version handshake. The server always asks on connect (including when it has nothing
/// required) so a client-declared <see cref="NetworkCompatibilityLevel.ServerMustHave"/> can
/// still run in reverse. The client answers the asked rows plus extra declared mods that fit.
/// A mismatch is reported to both logs and to the client as a message naming the mod, expected
/// version, and what the peer sent, instead of a silent desync, and the offending client is
/// disconnected two seconds later so the message arrives first. A client that never answers
/// (Networking disabled, unmodded, or a dropped reply) is fail-closed the same way when at
/// least one server plugin is not <see cref="NetworkCompatibilityLevel.NoNeedForSync"/> /
/// <see cref="NetworkCompatibilityLevel.ClientOnly"/>: a per-user deadline (default
/// <see cref="DefaultAnswerTimeoutSeconds"/> s, config
/// <c>Networking.HandshakeAnswerTimeoutSeconds</c>, 0 disables) kicks with a readable reason.
/// Same-ALC Install after Uninstall re-asks connected users that have not answered. A failed
/// ask is retried once next frame, then the server waits
/// <see cref="HandshakeAnswerWatch.KickFlushSeconds"/> s (the same Ok back-off as a timeout
/// kick) before sending a failure Result. After that Result the server <c>Arm</c>s the
/// normal <see cref="DefaultAnswerTimeoutSeconds"/> s answer deadline so a real reply at
/// t=5 s is accepted; it does not disconnect at 4 s. A second
/// <see cref="HandshakeAnswerWatch.KickFlushSeconds"/> hop then disconnects only when
/// <see cref="Compare.DisconnectReasonAfterCouldNotAskResult"/> says a mismatch owns the
/// kick (otherwise the armed deadline or an Ok answer does). Timeout is already
/// on its own path and does not double-send. A retry never overwrites Ok.
///
/// Wire format is fixed-size and blittable: up to <see cref="MaxMods"/> entries of ASCII guid and
/// version. Short declared rows still pack level/rule into version-slot bytes 22–23 (high bit of
/// byte 22 set) so a v1 peer that only reads ASCII still sees a clean version string. Declared
/// rows whose version needs those bytes instead travel with a dedicated
/// <see cref="MetaRecordGuid"/> record so a legal 22–23 character version is not truncated.
/// Comparison of two plugin lists lives in <see cref="Compare.Evaluate"/> so the presence/version
/// rules can be unit-tested without game types. <see cref="Compare.FindProblems"/> is the original
/// required-list walk (undeclared extras ignored, exact version).
/// </summary>
#if !PROGENITOR_TESTS
public static unsafe partial class Handshake
#else
public static partial class Handshake
#endif
{
    public const int RequestEventId = 0x50726848;  // "PrhH"
    public const int ResponseEventId = 0x50726852; // "PrhR"
    public const int ResultEventId = 0x50726851;   // "PrhQ"

    public const int MaxMods = 16;

    /// <summary>Longest guid that fits the 64-byte wire slot with a NUL terminator.</summary>
    public const int MaxGuidChars = 63;

    /// <summary>Longest version that fits the 24-byte wire slot with a NUL terminator.</summary>
    public const int MaxVersionChars = 23;

    /// <summary>
    /// Layout generation for the extra compatibility bytes inside the version slot. The packet
    /// size and event ids are unchanged from v1; a peer that does not understand the extra bytes
    /// ignores them (ASCII read stops at NUL).
    /// </summary>
    public const int ProtocolVersion = 2;

    /// <summary>
    /// High bit of version-slot byte 22. Distinguishes a declared
    /// <see cref="NetworkCompatibilityLevel.ServerMustHave"/> (0x80) from an undeclared v1 row (0).
    /// </summary>
    public const byte MetaPresentFlag = 0x80;

    /// <summary>
    /// Version-slot offset of the declared-flag / level byte. In-slot metadata is written only
    /// when the ASCII version already NULs at or before this index (at most
    /// <see cref="InSlotVersionChars"/> characters) so a legal longer version is not truncated.
    /// </summary>
    public const int MetaFlagOffset = 22;

    /// <summary>Version-slot offset of the <see cref="NetworkVersionRule"/> byte.</summary>
    public const int MetaRuleOffset = 23;

    /// <summary>
    /// Longest version that can share the 24-byte slot with in-slot metadata (byte 21 is the
    /// NUL a v1 ASCII read stops at). Longer declared versions use <see cref="MetaRecordGuid"/>.
    /// </summary>
    public const int InSlotVersionChars = MetaFlagOffset - 1;

    /// <summary>
    /// Dedicated metadata row. Guid is reserved; the version is a format byte plus one ASCII
    /// pack-char per slot. A v1 peer treats it as an extra unknown mod and ignores it.
    /// </summary>
    public const string MetaRecordGuid = "progenitor.hs.meta";

    /// <summary>First character of a dedicated metadata version blob (<see cref="ProtocolVersion"/>).</summary>
    public const char MetaRecordFormat = '2';

    /// <summary>
    /// Marker the client puts on the wire when a declared
    /// <see cref="NetworkCompatibilityLevel.ServerMustHave"/> extra (or a ServerMustHave
    /// row that needed a dedicated metadata record) would not fit in <see cref="MaxMods"/>
    /// slots. The server fails closed instead of silently joining. Dropped
    /// <see cref="NetworkCompatibilityLevel.NoNeedForSync"/> extras do not use this marker.
    /// The server never puts this guid on its request: a 17th declared server plugin is
    /// refused at <see cref="Require(string,string,NetworkCompatibilityLevel,NetworkVersionRule)"/>
    /// and does not ban every joiner.
    /// </summary>
    public const string OverflowGuid = "progenitor.hs.overflow";

    /// <summary>ASCII characters that fit <c>Result.Message[480]</c> with a NUL.</summary>
    public const int ResultMessageMaxChars = 479;

    /// <summary>Appended when a kick reason is cut to <see cref="ResultMessageMaxChars"/>.</summary>
    public const string ReasonEllipsis = "...";

    /// <summary>
    /// Default fail-closed wait for a handshake answer, in seconds. Bound as
    /// <c>Networking.HandshakeAnswerTimeoutSeconds</c>; 0 disables the deadline.
    /// </summary>
    public const int DefaultAnswerTimeoutSeconds = HandshakeAnswerWatch.DefaultTimeoutSeconds;

    /// <summary>
    /// Default grace window for the deferred kick (job FMAN18), in seconds. Bound as
    /// <c>Networking.InstallGraceSeconds</c>; 0 disables the deferral entirely and every miss
    /// kicks as it did before. One window per join, never extended.
    /// </summary>
    public const int DefaultInstallGraceSeconds = HandshakeInstallGrace.DefaultGraceSeconds;

    /// <summary>
    /// <see cref="Result.Ok"/> value for a <b>deferred</b> refusal (job FMAN18): the join has not
    /// passed, has not been given up on, and the client has
    /// <c>Networking.InstallGraceSeconds</c> to install what the message names and re-answer.
    ///
    /// <para><b>Why a third value of an existing byte and not a new event id or a new field.</b>
    /// A new event id is the dangerous option on this transport: <c>CustomEvents</c>'s client
    /// dispatcher returns <c>false</c> for an id it has no handler for, and the caller then hands
    /// the packet to the game's own reliable-events reader — so a client running today's Core
    /// would feed a Progenitor packet to Stunlock's deserializer. A new <em>field</em> is
    /// dangerous for a different reason: <see cref="Result"/> is read with
    /// <c>CustomEvents.TryRead&lt;Result&gt;</c>, which is size-checked, so growing the struct
    /// would make today's client drop <em>every</em> Result including the ok one. The
    /// <see cref="Result.Ok"/> byte carries only 0 and 1 today and is read exactly once, as
    /// <c>r.Ok == 1</c>, so a 2 lands in the existing else branch: today's client logs the
    /// message, opens its deny report and is disconnected at the end of the window with the
    /// readable reason — strictly better than the immediate kick it gets now, and identical in
    /// shape to what it already does.</para>
    /// </summary>
    public const byte ResultDeferred = 2;

    /// <summary>
    /// What a peer can <em>do</em>, as opposed to what version it is (job FMAN20, R50 finding 2).
    /// A Core version (git-derived, docs/versioning.md) says when a build was made, not which
    /// features it has, so a version compare cannot answer "will this client act on a deferred
    /// Result"; a declared capability can, and it stays correct when a capability is backported
    /// or dropped.
    /// </summary>
    [Flags]
    public enum Capability : uint
    {
        /// <summary>Nothing declared. Every FMAN18-and-earlier client answers this way.</summary>
        None = 0,

        /// <summary>
        /// This client understands <see cref="ResultDeferred"/> and can spend an install window:
        /// it routes <c>Ok = 2</c> to the join screens rather than the read-only deny report, fixes
        /// the failing rows there (a Thunderstore install that hot-loads, a kept copy, Turn on,
        /// Pause for this server), and re-answers on the same connection (job FMAN19; servers no
        /// longer send mod files, 2026-10-05). Without it, deferring buys the player nothing but a
        /// minute of waiting before the same kick, so the server kicks immediately instead.
        /// </summary>
        InstallWindow = 1,

        /// <summary>
        /// REMOTEINST1: this client handles the admin remote install channel (<c>'PrRi'</c>). The
        /// server pushes its remote-install status after an ok handshake only to a client that
        /// declared this, so no older client is ever sent an id it has no handler for.
        /// </summary>
        RemoteInstall = 2,

        /// <summary>
        /// JOINFLOW1: this client draws the join screens (server message, mods page, trust prompt). A
        /// refused client that declared it is sent the signed join section even when no failing row has a
        /// catalog row (a zero-row descriptor), so it can show the server's message on the refusal; an
        /// older client is never sent a zero-row descriptor, which its read-only deny report would
        /// describe as a catalog with no sources.
        /// </summary>
        JoinFlow = 4,

        /// <summary>
        /// JOINADMIN1 (join flow stage 3): this client handles the admin "Players joining" channel
        /// (<c>'PrJa'</c>). The server sends its hello on that id after an ok handshake only to a client that
        /// declared this. (Bits 8 to 32 are left for other join-flow stages.)
        /// </summary>
        JoinAdmin = 64,

        /// <summary>
        /// CLIENTPOLICY1 (client mod policy, docs/client-mod-policy.md). Both directions:
        /// <list type="bullet">
        /// <item>in a <b>server's request</b> (its capability row, appended only when the admins' required client
        /// list is non-empty or the mode is Only approved): "send me your client mod inventory". A client sends
        /// its <c>'PrCm'</c> report only to a server that set it;</item>
        /// <item>in a <b>client's answer</b>: "I will send that report right after this answer". A server waits
        /// for the report (inside the answer deadline) only from a client that declared it; anything else is
        /// judged at once as a client whose mods can't be checked.</item>
        /// </list>
        /// </summary>
        ClientPolicy = 128,

        /// <summary>
        /// 2026-10-05: inside an install window this client fixes the rows on its own side (a Thunderstore install that
        /// hot-loads, a kept copy, Turn on, Pause for this server) and then <b>re-answers</b> the handshake. A Core from
        /// before servers stopped sending mod files declares <see cref="InstallWindow"/> but only re-answered after a
        /// server-sent download, so it would wait out the window and be kicked; a server opens the window only for a
        /// client that declares both (<see cref="Compare.CanSpendInstallWindow"/>).
        /// </summary>
        ClientFix = 256,

        /// <summary>
        /// CLIENTPOLICY3 (mid-session re-checks): this client reads the server's mid-session client mod policy notice
        /// (<c>'PrCm'</c> kind 1, <see cref="ClientPolicyNoticeWire"/>), pauses the pausable mods it names and shows it in
        /// the game's popup look; it also re-reports when a re-signed catalog brings a policy mid-session. A server sends
        /// that notice only to a client that declared this; anything else gets the same words as a chat line.
        /// </summary>
        PolicyRecheck = 512,

        /// <summary>
        /// Chat command hints: this client handles the <c>'PrCh'</c> channel (<c>ChatCommandHintsWire</c>). The server sends
        /// its hello on that id after an ok handshake, and when its command list changes, only to a client that declared
        /// this; the client asks for the catalog only after a hello.
        /// </summary>
        CommandHints = 1024,

        /// <summary>
        /// UNINSTREQ1 (server only, on its request row): the admins chose to <b>warn</b> players whose mods break the client
        /// mod policy mid-session (<c>midSession warn</c>). Exactly one of this and <see cref="PolicyDisconnects"/> is set
        /// whenever the server has a policy config; neither means an older server, whose rule is unknown. A client
        /// never declares it; an older client ignores the bit.
        /// </summary>
        PolicyWarns = 2048,

        /// <summary>UNINSTREQ1 (server only): the admins chose to <b>disconnect</b> such players (<c>midSession disconnect</c>).</summary>
        PolicyDisconnects = 4096,

        /// <summary>
        /// REQRULE2 (client only, in its answer): this client reads the server's rule message (<c>'PrCm'</c> kind 2,
        /// <see cref="ClientPolicyRuleWire"/>): the mid-session rule and the Disconnect them seconds, sent after an ok
        /// handshake and whenever the admins change them. A server sends it only to a client that declared this.
        /// </summary>
        PolicyRuleUpdates = 8192,

        /// <summary>
        /// F41 (server only, on its request row): this server reads the client's held-notice message (<c>'PrCm'</c> kind 3,
        /// <see cref="ClientPolicyHeldWire"/>) and, while the client says a disconnect countdown notice is held behind an
        /// open confirmation, sends the countdown as private System chat lines. A client sends that message only to a
        /// server that declared this (an older server would log it as a bad report packet); an older client ignores the bit.
        /// </summary>
        CountdownChat = 16384,

        /// <summary>
        /// Mod compatibility C1 (docs/mod-compatibility.md, section 1). Both directions: on a <b>server's request</b>,
        /// "send me your feature table after your answer"; in a <b>client's answer</b>, "I read the server's table".
        /// Each side sends its <c>'PrFt'</c> table (and later deltas) only to a peer that set it, so an older peer is
        /// never sent an id it has no handler for.
        /// </summary>
        ModFeatures = 32768,

        /// <summary>
        /// Mod compatibility C2 (docs/mod-compatibility.md, section 3): this peer reads the <c>'PrMs'</c> versioned mod
        /// message envelope. <c>ctx.Messages</c> sends only to a peer that declared it and whose feature table lists the
        /// mod; anything else is refused at send with <c>SendResult.PeerTooOld</c> or <c>PeerLacksMod</c>.
        /// </summary>
        ModMessages = 65536,
        /// <summary>Client accepts the versioned equipment-stat definition channel (PrfS).</summary>
        ItemStatSync = 131072,
        /// <summary>Client accepts bounded chunked item-key ledgers (PrfJ).</summary>
        ItemLedgerChunks = 262144,
        /// <summary>Client has enabled at least 1024 synchronized prefab identities.</summary>
        ItemCapacity1024 = 524288,
        /// <summary>Client has enabled the experimental 16384 synchronized prefab identity profile.</summary>
        ItemCapacity16384 = 1048576,
        /// <summary>Priority item demands and inventory loading notices.</summary>
        ItemDefinitionPriority = 2097152,

        /// <summary>
        /// SUPPORT1 (server only, on its request row): this server sends its own redacted log bundle to an admin over
        /// <c>'PrSL'</c> (<c>Support.ServerLogsWire</c>). A client asks only a server that declared it; anything else gets
        /// the client's bundle alone, with summary.txt saying the server is too old. A client never declares it.
        /// </summary>
        ServerLogs = 4194304,

        /// <summary>
        /// Mod compatibility C3 (docs/mod-compatibility.md, section 2). Both directions: on a <b>server's request</b>,
        /// "this server judges versions with ranges and signs the extended catalog"; in a <b>client's answer</b>, "my
        /// feature table carries each mod's effective range (<c>peers</c>), and I read the catalog form with ranges".
        /// The server waits briefly for the table of a client that declared it (<see cref="FeatureTableHold"/>) and judges
        /// it with the newer side's range; anything else is judged on the handshake rows as before.
        /// </summary>
        VersionRanges = 8388608,

        /// <summary>C3 (server only, on its request): Version matching is <b>Same minor for all</b>. An older client ignores the bit.</summary>
        MatchSameMinor = 16777216,

        /// <summary>C3 (server only, on its request): Version matching is <b>Exact version for all</b>. An older client ignores the bit.</summary>
        MatchExact = 33554432,

        /// <summary>
        /// Mod compatibility C4 (client only, in its answer): this client reads the catalog form that carries the server's own
        /// Progenitor build under Exact version for all (<c>progenitor</c>, <c>progenitorSha</c>) and draws the join screens'
        /// Progenitor row. A server sends that form only to a client that declared this and <see cref="VersionRanges"/>; a C3
        /// client keeps the extended form, whose signature it can check. The server judges every client's Progenitor the
        /// same way whatever it declared.
        /// </summary>
        ProgenitorExact = 67108864,

        /// <summary>
        /// Requests, large messages and subscriptions (docs/modules.md): this peer reads the <c>'PrXc'</c> exchange channel
        /// (<c>ctx.Requests</c>, <c>ctx.Subscriptions</c>). Both directions, like <see cref="ModMessages"/>: exchange traffic
        /// goes only to a peer that declared it and whose feature table lists the mod; anything else is refused at the
        /// sender with <c>SendResult.PeerTooOld</c> or <c>PeerLacksMod</c>.
        /// </summary>
        ModExchange = 134217728,
    }

    /// <summary>This Core's version for a refusal's text, or empty when it cannot be read.</summary>
    private static string OwnCoreForReasons()
    {
        string v = Progenitor.Ui.FrameworkVersions.Core;
        return v == Progenitor.Ui.FrameworkVersions.Unknown ? "" : v;
    }

    /// <summary>Everything <em>this</em> Core can do, as the client puts it on the wire.</summary>
    internal static Capability CapabilitiesForPrefabLimit(int limit) => SelfCapabilities
        | (limit >= 1024 ? Capability.ItemCapacity1024 : Capability.None)
        | (limit >= 16384 ? Capability.ItemCapacity16384 : Capability.None);

    /// <summary>Capacity promises required by the server's configured identity profile.</summary>
    internal static Capability RequiredPrefabCapacityCapability(int limit) => limit <= 256 ? Capability.None
        : limit <= 1024 ? Capability.ItemCapacity1024 : Capability.ItemCapacity16384;

    internal static bool SupportsPrefabLimit(Capability capabilities, int limit)
    {
        var required = RequiredPrefabCapacityCapability(limit);
        return (capabilities & required) == required;
    }

    public const Capability SelfCapabilities = Capability.InstallWindow | Capability.RemoteInstall | Capability.JoinFlow
        | Capability.JoinAdmin | Capability.ClientPolicy | Capability.ClientFix | Capability.PolicyRecheck
        | Capability.CommandHints | Capability.PolicyRuleUpdates | Capability.ModFeatures | Capability.ModMessages
        | Capability.ItemStatSync | Capability.ItemLedgerChunks | Capability.ItemDefinitionPriority
        | Capability.VersionRanges | Capability.ProgenitorExact | Capability.ModExchange;

    /// <summary>
    /// Reserved row carrying <see cref="Capability"/>. Reserved like
    /// <see cref="MetaRecordGuid"/> and <see cref="OverflowGuid"/>: never compared as a plugin,
    /// and unknown to a v1 peer, which ignores it as an extra mod. The client appends it
    /// <b>last</b>, after any metadata or overflow row, so a reader that drops it shifts no
    /// in-slot metadata index.
    /// </summary>
    public const string CapabilityGuid = "progenitor.hs.cap";

    /// <summary>
    /// First character of a capability blob. The rest is the flag word in lowercase hex, so a
    /// future capability costs no wire bytes and an unknown bit is simply not acted on.
    /// </summary>
    public const char CapabilityFormat = '1';

#if !PROGENITOR_TESTS
    private const int GuidBytes = MaxGuidChars + 1;
    private const int VersionBytes = MaxVersionChars + 1;
    private const int EntryBytes = GuidBytes + VersionBytes;
#endif

    /// <summary>
    /// One plugin's handshake row. <see cref="Declared"/> is true when the author set
    /// <see cref="NetworkCompatibilityAttribute"/> (or called Require/Provide with an explicit
    /// level): those rows are evaluated in both directions. Undeclared rows keep the original
    /// rule — the server requires an exact version, and extras the client reports are ignored.
    /// </summary>
    public readonly struct ModRecord
    {
        public ModRecord(
            string guid,
            string version,
            NetworkCompatibilityLevel level = NetworkCompatibilityLevel.ServerMustHave,
            NetworkVersionRule versionRule = NetworkVersionRule.Exact,
            bool declared = false)
        {
            Guid = guid ?? "";
            Version = version ?? "";
            Level = level;
            VersionRule = versionRule;
            Declared = declared;
        }

        public string Guid { get; }
        public string Version { get; }
        public NetworkCompatibilityLevel Level { get; }
        public NetworkVersionRule VersionRule { get; }
        public bool Declared { get; }

        /// <summary>Original handshake row: server requires this guid at this exact version.</summary>
        public static ModRecord Undeclared(string guid, string version) =>
            new(guid, version, NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact, declared: false);

        /// <summary>Row produced from <see cref="NetworkCompatibilityAttribute"/> (or an explicit Require/Provide overload).</summary>
        public static ModRecord CreateDeclared(string guid, string version, NetworkCompatibilityLevel level, NetworkVersionRule versionRule) =>
            new(guid, version, level, versionRule, declared: true);
    }

    /// <summary>
    /// Result of packing plugin rows into the 16-slot wire list. <see cref="Dropped"/> is every
    /// candidate that did not fit; <see cref="DeclaredDropped"/> is true only when a declared
    /// <see cref="NetworkCompatibilityLevel.ServerMustHave"/> row could not fit (fail closed),
    /// including a ServerMustHave row that needed a dedicated metadata record. Dropped
    /// <see cref="NetworkCompatibilityLevel.NoNeedForSync"/> extras are listed in
    /// <see cref="Dropped"/> but do not set this flag and must not overwrite an asked-row answer.
    /// </summary>
    public readonly struct PackedList
    {
        public PackedList(IReadOnlyList<ModRecord> rows, IReadOnlyList<ModRecord> dropped, bool declaredDropped)
        {
            Rows = rows ?? Array.Empty<ModRecord>();
            Dropped = dropped ?? Array.Empty<ModRecord>();
            DeclaredDropped = declaredDropped;
        }

        public IReadOnlyList<ModRecord> Rows { get; }
        public IReadOnlyList<ModRecord> Dropped { get; }
        public bool DeclaredDropped { get; }
    }

    /// <summary>
    /// Required-list vs answered-list comparison, two-list compatibility evaluation, wire Count
    /// clamp, and guid/version validation. No game types: the receive path and the unit tests run
    /// this same code. Walking <c>required</c> (not the answer) is the original handshake's job —
    /// a client that omits a required guid must fail, and extra answer rows are ignored. A Count
    /// outside [0, MaxMods] is malformed; callers clamp and ignore the out-of-range slots.
    /// </summary>
    public static class Compare
    {
        /// <summary>Clamp a wire <c>Count</c> into [0, <see cref="MaxMods"/>]. Negative becomes 0.</summary>
        public static int ClampCount(int count)
        {
            if (count < 0) return 0;
            if (count > MaxMods) return MaxMods;
            return count;
        }

        /// <summary>True when a wire <c>Count</c> is not in [0, <see cref="MaxMods"/>].</summary>
        public static bool IsMalformedCount(int count) => count < 0 || count > MaxMods;

        /// <summary>
        /// Guid 1–63 ASCII and version 1–23 ASCII, matching the fixed wire slots
        /// (64 and 24 bytes including a NUL). Rejects NUL and non-ASCII so <c>WriteAscii</c>
        /// cannot throw later from a hook. Declared rows may use the full 23 characters; metadata
        /// is not stored on top of a long version.
        /// </summary>
        public static bool TryValidateEntry(string guid, string version, out string? error)
        {
            if (!IsAsciiField(guid, MaxGuidChars, "guid", out error)) return false;
            return IsAsciiField(version, MaxVersionChars, "version", out error);
        }

        /// <summary>
        /// Compare required mods to the client's answer. <paramref name="claimedCount"/> is the
        /// wire Count; it is clamped to [0, MaxMods] and to the answered list length so a
        /// malicious Count cannot walk off the list. One string per required guid that fails:
        /// <c>{guid} missing</c> or <c>{guid} version {have}, need {want}</c>. Extra answer
        /// entries are ignored.
        /// </summary>
        public static List<string> FindProblems(
            IReadOnlyDictionary<string, string> required,
            IReadOnlyList<(string guid, string version)> answered,
            int claimedCount)
        {
            var problems = new List<string>();
            if (required == null || required.Count == 0) return problems;

            int n = ClampCount(claimedCount);
            if (answered != null && n > answered.Count) n = answered.Count;

            var have = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (answered != null)
            {
                for (int i = 0; i < n; i++)
                {
                    var (guid, version) = answered[i];
                    if (string.IsNullOrEmpty(guid)) continue;
                    have[guid] = version ?? "";
                }
            }

            foreach (var kv in required)
            {
                if (!have.TryGetValue(kv.Key, out var got) || got.Length == 0)
                    problems.Add($"{kv.Key} missing");
                else if (!string.Equals(got, kv.Value, StringComparison.OrdinalIgnoreCase))
                    problems.Add($"{kv.Key} version {got}, need {kv.Value}");
            }
            return problems;
        }

        /// <summary>
        /// Evaluate two plugin lists against each other. For each of <paramref name="server"/>'s
        /// rows, apply that row's level and version rule to the client's list; then the reverse
        /// for each <em>declared</em> client row (undeclared extras are ignored — the original
        /// handshake). One reason string per failing guid: <c>{guid}: expected {want}, got {got}</c>.
        /// Duplicate guids in a list: last one wins. Empty guids are skipped. Null lists are empty.
        /// Reserved handshake guids are stripped first so a dedicated metadata record is not
        /// compared as a plugin.
        /// </summary>
        public static List<string> Evaluate(
            IReadOnlyList<ModRecord>? server,
            IReadOnlyList<ModRecord>? client)
            => Evaluate(server, client, out _);

        /// <summary>
        /// Same walk, also reporting the guid behind each problem string in the same order
        /// (job FMAN12). The reason strings are formatted for a human and are the wrong thing to
        /// parse: <see cref="Reason"/> puts the guid before the first <c>": "</c> today and a
        /// later wording change would silently break any caller that relied on that. The set the
        /// walk already keeps to de-duplicate reports <em>is</em> the list of failing ids, so it
        /// is handed out rather than re-derived.
        /// </summary>
        public static List<string> Evaluate(
            IReadOnlyList<ModRecord>? server,
            IReadOnlyList<ModRecord>? client,
            out List<string> failedIds)
            => Evaluate(server, client, out failedIds, judge: null);

        /// <summary>
        /// Mod compatibility C3: the same walk, with every version comparison of a mod both sides run made by
        /// <see cref="VersionMatchJudge.Check"/> under <paramref name="judge"/> (version matching, per-mod rules, the
        /// newer side's range). Presence and levels are unchanged. A null <paramref name="judge"/> is the original walk.
        /// </summary>
        internal static List<string> Evaluate(
            IReadOnlyList<ModRecord>? server,
            IReadOnlyList<ModRecord>? client,
            out List<string> failedIds,
            VersionJudgeContext? judge)
        {
            var problems = new List<string>();
            var serverBy = Index(MaterializeWire(server));
            var clientBy = Index(MaterializeWire(client));
            var reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            failedIds = new List<string>();

            foreach (var rec in serverBy.Values)
            {
                var problem = Check(rec, clientBy, judge: judge);
                if (problem != null && reported.Add(rec.Guid))
                {
                    problems.Add(problem);
                    failedIds.Add(rec.Guid);
                }
            }

            foreach (var rec in clientBy.Values)
            {
                if (!rec.Declared) continue;
                // An absence sentinel is not a requirement. PackReply answers a mod the client
                // does not have with a row carrying that mod's guid, an EMPTY version and the
                // server's own Declared flag, so the forward pass can read it as "missing". Run
                // through this reverse pass unchanged it becomes the opposite claim - that the
                // CLIENT requires this mod at version "" - and an optional mod the client simply
                // does not have kicks the player with `expected  (minor), got 1.0.0`. Presence is
                // already defined as a non-empty version by TryGetPresent; this applies that same
                // definition to the subject of the check, not only to the peer being looked up.
                if (!IsPresentRecord(rec)) continue;
                var problem = Check(rec, serverBy, serverLacksIt: true, judge: judge);
                if (problem != null && reported.Add(rec.Guid))
                {
                    problems.Add(problem);
                    failedIds.Add(rec.Guid);
                }
            }

            return problems;
        }

        /// <summary>
        /// Why <c>Check</c> rejected this client's copy of one mod (job FMAN14, R45 findings 5 and 6).
        /// </summary>
        public enum KickNoteCause : byte
        {
            /// <summary>The server row is <see cref="NetworkCompatibilityLevel.ServerMustHave"/> and this client does not have the mod at all.</summary>
            Missing = 1,

            /// <summary>This client <em>has</em> the mod and the server row's version rule rejected the copy. Produced by <see cref="NetworkCompatibilityLevel.NoNeedForSync"/> rows as well as required ones.</summary>
            VersionMismatch = 2,
        }

        /// <summary>
        /// One id whose <em>own</em> row on this server rejected this client at the join check (job FMAN14, R45 finding
        /// 5). Produced by <see cref="ServerEnforcedFailures"/>, which is the only thing allowed to make one.
        /// </summary>
        public readonly struct EnforcedFailure
        {
            public EnforcedFailure(string id, NetworkCompatibilityLevel level, NetworkVersionRule versionRule, KickNoteCause cause)
            {
                Id = id ?? "";
                Level = level;
                VersionRule = versionRule;
                Cause = cause;
            }

            public string Id { get; }

            /// <summary>The level on <em>this server's</em> row, not the catalog's.</summary>
            public NetworkCompatibilityLevel Level { get; }

            public NetworkVersionRule VersionRule { get; }

            public KickNoteCause Cause { get; }
        }

        /// <summary>
        /// <see cref="NameIds(List{string},int)"/> at <see cref="MaxNoteIds"/>, for the install window's texts. One
        /// list formatter for every player-facing mod list in this file, so "a.mod, b.mod and 2 more" cannot be spelled
        /// two ways.
        /// </summary>
        public static string NameIdList(IReadOnlyList<string>? ids)
        {
            var list = new List<string>();
            if (ids != null)
                for (int i = 0; i < ids.Count; i++)
                    if (!string.IsNullOrEmpty(ids[i])) list.Add(ids[i]);
            if (list.Count == 0) return "";
            return NameIds(list, MaxNoteIds);
        }

        private static string NameIds(List<string> ids, int named)
        {
            int n = named < ids.Count ? named : ids.Count;
            if (n <= 0)
                return ids.Count.ToString(CultureInfo.InvariantCulture) + (ids.Count == 1 ? " mod" : " mods");
            string s = "";
            for (int i = 0; i < n; i++) s += (i > 0 ? ", " : "") + ids[i];
            if (ids.Count > n)
                s += " and " + (ids.Count - n).ToString(CultureInfo.InvariantCulture) + " more";
            return s;
        }

        /// <summary>How many guids a player-facing list names before it counts the rest.</summary>
        public const int MaxNoteIds = 3;

        /// <summary>The fixed head of every mismatch kick reason.</summary>
        public const string MismatchPrefix = "Mod mismatch: ";

        /// <summary>
        /// The closing sentence of every problem that refuses a client for a Progenitor Core capability it lacks. The
        /// client's join screens find Core refusals by it (<see cref="JoinCoreRefusal"/>), so keep it on each one.
        /// </summary>
        public const string CoreUpdateFix = "Update Progenitor Core on the client.";

        /// <summary>The item-budget problem's head on servers before 2026-10-13; the join screens still read it.</summary>
        public const string LegacyItemBudgetHead = "This server uses an extended item budget.";

        /// <summary>
        /// A refusal for a capability only a newer Progenitor has, naming this server's Progenitor so the player knows
        /// which version fixes it: <c>Needs Progenitor 2026.10.195 or newer (catalog-v2 equipment-stat synchronization).
        /// Update Progenitor Core on the client.</c> The client only learns the server's version from this text (the feature
        /// table follows an accepted join), and the join screens parse it (<see cref="JoinCoreRefusal"/>).
        /// </summary>
        public static string CoreCapabilityProblem(string capability, string? serverCore)
        {
            string v = (serverCore ?? "").Trim();
            return (v.Length == 0 ? "Needs a newer Progenitor" : "Needs Progenitor " + v + " or newer")
                + " (" + capability + "). " + CoreUpdateFix;
        }

        /// <summary>
        /// A refusal for a client that accepts fewer custom items per join than this server adds:
        /// <c>Needs Progenitor set to accept 1024 custom items (this server runs 2026.10.195). Set
        /// Networking.MaxSyncedPrefabsPerJoin to 1024 on a current Progenitor client.</c>
        /// </summary>
        public static string ItemBudgetProblem(int limit, string? serverCore)
        {
            string n = limit.ToString(CultureInfo.InvariantCulture);
            string v = (serverCore ?? "").Trim();
            return "Needs Progenitor set to accept " + n + " custom items" + (v.Length == 0 ? "" : " (this server runs " + v + ")")
                + ". Set Networking.MaxSyncedPrefabsPerJoin to " + n + " on a current Progenitor client.";
        }

        /// <summary>
        /// What this server's join check says about <paramref name="modId"/> for a client holding
        /// <paramref name="clientHas"/>, in the wording the disconnect box would use — or null
        /// when it says nothing (job FMAN13, R44 finding 9).
        ///
        /// <para>Null covers the two ways a claim can be unfounded, and they are not the same
        /// thing: <paramref name="serverAsked"/> has no row for the id (this server does not
        /// enforce it, whatever the catalog's <c>level</c> field says), or it has one and this
        /// client passes it. Only the third case — a row that this client's copy fails — is a
        /// consequence the code has actually established, and it is produced by <c>Check</c>, the
        /// same function the kick path runs, so the panel cannot drift from the disconnect box by
        /// rewording.</para>
        /// </summary>
        public static string? EnforcedKickReason(
            string? modId,
            IReadOnlyList<ModRecord>? serverAsked,
            IReadOnlyList<ModRecord>? clientHas)
            => EnforcedKickReason(modId, serverAsked, clientHas, judge: null);

        /// <summary>
        /// Mod compatibility C3: the same, with a mod both sides run judged by <see cref="VersionMatchJudge.Check"/> under
        /// <paramref name="judge"/> (on a client, <see cref="VersionMatching.ClientContextFor"/>: the server's Version
        /// matching, per-mod rules and ranges from its catalog). A null <paramref name="judge"/> is the original check.
        /// </summary>
        internal static string? EnforcedKickReason(
            string? modId,
            IReadOnlyList<ModRecord>? serverAsked,
            IReadOnlyList<ModRecord>? clientHas,
            VersionJudgeContext? judge)
        {
            if (string.IsNullOrEmpty(modId) || serverAsked == null) return null;
            var mine = Index(MaterializeWire(serverAsked));
            if (!mine.TryGetValue(modId!, out var row)) return null;
            return Check(row, Index(MaterializeWire(clientHas)), judge: judge);
        }

        /// <summary>
        /// The subset of <paramref name="ids"/> that <em>this server's own join check</em>
        /// rejected this client over, each tagged with the level, the rule and the
        /// <see cref="KickNoteCause"/> behind it (job FMAN14, R45 finding 5; supersedes FMAN13's
        /// <c>RequiredFailures</c>).
        ///
        /// <para><b>Provenance, not level.</b> <see cref="Evaluate"/> fills its failing-id list
        /// from two walks: server rows the client failed, and client-declared
        /// <c>ServerMustHave</c> rows <em>this server</em> lacks. R44 finding 8 asked for the note
        /// to be intersected with what the server requires, because telling a player "this server
        /// also requires it to join" about a requirement that exists only on their own machine is
        /// three false claims. FMAN13 intersected with <c>Level == ServerMustHave</c> instead,
        /// which drops the second half of the very check that kicks: a <c>NoNeedForSync</c> row
        /// rejects a client that <em>has</em> the mod at a version its rule refuses
        /// (<c>Check</c>'s <c>NoNeedForSync</c> branch), and that client was left with a bare
        /// "Mod mismatch" while the bytes it needed sat on the machine that disconnected it.</para>
        ///
        /// <para>So the filter is the consequence itself: keep an id when this server has a row for
        /// it <em>and</em> <c>Check</c> — the same function, on the same two tables,
        /// <see cref="Evaluate"/> just ran — rejects this client's copy. An id from the reverse
        /// walk has no server row, or has one that passed, and is dropped by construction rather
        /// than by a second rule that has to be kept in step with the first. This is also the only
        /// filter the operator warning applies, so the two halves cannot swap places again.</para>
        ///
        /// <para>A row whose <c>Check</c> failed for neither reason the note can explain — an
        /// unknown level or version-rule byte, which is a protocol error already named in the
        /// problems list — is dropped: there is no sentence about it that the code has
        /// established.</para>
        ///
        /// <para>Order and case follow <paramref name="ids"/>. Null inputs yield an empty list, and
        /// nothing here can throw — it only decorates a kick that is happening anyway.</para>
        /// </summary>
        public static List<EnforcedFailure> ServerEnforcedFailures(
            IReadOnlyList<string>? ids,
            IReadOnlyList<ModRecord>? serverRows,
            IReadOnlyList<ModRecord>? clientRows)
            => ServerEnforcedFailures(ids, serverRows, clientRows, judge: null);

        /// <summary>C3: the same, with versions judged under <paramref name="judge"/> (null: the original check).</summary>
        internal static List<EnforcedFailure> ServerEnforcedFailures(
            IReadOnlyList<string>? ids,
            IReadOnlyList<ModRecord>? serverRows,
            IReadOnlyList<ModRecord>? clientRows,
            VersionJudgeContext? judge)
        {
            var list = new List<EnforcedFailure>();
            if (ids == null || serverRows == null) return list;
            var serverBy = Index(MaterializeWire(serverRows));
            var clientBy = Index(MaterializeWire(clientRows));
            for (int i = 0; i < ids.Count; i++)
            {
                string id = ids[i];
                if (string.IsNullOrEmpty(id)) continue;
                if (!serverBy.TryGetValue(id, out var row)) continue;
                if (Check(row, clientBy, judge: judge) == null) continue;

                KickNoteCause cause;
                if (TryGetPresent(clientBy, row.Guid, out _)) cause = KickNoteCause.VersionMismatch;
                else if (row.Level == NetworkCompatibilityLevel.ServerMustHave) cause = KickNoteCause.Missing;
                else continue;

                list.Add(new EnforcedFailure(id, row.Level, row.VersionRule, cause));
            }

            return list;
        }

        /// <summary>
        /// The ids a deny-time descriptor may name (job FMAN17): the subset of
        /// <paramref name="ids"/> that <em>this server's own join check</em> rejected this client
        /// over, in the order the kick reason lists them.
        ///
        /// <para><b>Why it lives here.</b> R45 finding 13 — <c>OnResponse</c> needs an
        /// <c>Entity</c> and a <c>ProjectM.Network.User</c>, so no test in the suite can call it,
        /// and every composition left at that call site is a decision no test can reach.
        /// <c>ConsentReachabilityTests.The_kick_path_composes_nothing_of_its_own</c> pins that, and
        /// this method is how the deny hook obeys it: one call at the call site, the narrowing
        /// where <see cref="ServerEnforcedFailures"/> and its tests already are.</para>
        ///
        /// <para>The reverse walk's ids — rows the <em>client</em> declared
        /// <c>ServerMustHave</c> that this server does not have — are dropped by
        /// <see cref="ServerEnforcedFailures"/>, which is correct twice over: this server has no
        /// catalog row for a mod it does not run, and telling a player to download something
        /// from a machine that does not have it would be a claim the code has not established.</para>
        /// </summary>
        public static List<string> DenyDescriptorIds(
            IReadOnlyList<string>? ids,
            IReadOnlyList<ModRecord>? serverRows,
            IReadOnlyList<ModRecord>? clientRows)
            => ModManifestDenyPolicy.FailingIds(ServerEnforcedFailures(ids, serverRows, clientRows));

        /// <summary>
        /// The whole mismatch kick reason: <c>Mod mismatch: </c>, the note
        /// <paramref name="note"/> produces (empty for the normal case), then the problems list
        /// (job FMAN13, R44 findings 11 and 12).
        ///
        /// <para><b>The note goes first</b> because <see cref="TruncateReason"/> cuts the tail:
        /// three failing offered mods used to push the note past <c>ResultMessageMaxChars</c> and
        /// the first text lost was the only actionable sentence in the message. The problems list
        /// is the half a player can lose part of and still be told what happened.</para>
        ///
        /// <para><b>Nothing the decoration does can stop the kick.</b> <paramref name="note"/> is
        /// invoked inside a <c>try</c>: it reaches the offer table, the once-per-id operator
        /// warning and <c>Plugin.Logger</c>, and a throw from any of those used to escape
        /// <c>OnResponse</c>, be logged by the outer handler, and leave a client that failed a
        /// <c>ServerMustHave</c> check <b>joined</b> — worse than either outcome the code intends.
        /// A throw now costs the decoration, not the disconnect.</para>
        /// </summary>
        public static string MismatchReason(IReadOnlyList<string>? problems, Func<string>? note)
        {
            string list = problems == null ? "" : string.Join("; ", problems);
            string text = "";
            if (note != null)
            {
                try { text = note() ?? ""; }
                catch (Exception e)
                {
                    text = "";
                    // Job FLATCH2 (R45 finding 31). The handler cannot use the channel that may
                    // have just failed. R44 named the realistic trigger for the note throwing as
                    // Plugin.Logger during a Core teardown - the note reaches it through
                    // the operator warning (removed 2026-10-05) - and on that trigger this LogWarning throws for
                    // the same reason, MismatchReason throws, OnResponse's outer catch tries to
                    // log and throws again, and a client that failed a ServerMustHave check stays
                    // joined. That is the one trigger the fix above was written for, so the
                    // reporting half swallows too: the kick reason is already built and returning
                    // it is worth more than the line saying why it is plain.
                    try
                    {
                        Plugin.Logger?.LogWarning(
                            "Handshake: mismatch note threw; kicking with the plain reason: " + e.Message);
                    }
                    catch { /* logging is decoration; the disconnect is not */ }
                }
            }

            if (text.Length == 0) return MismatchPrefix + list;
            return MismatchPrefix + text + " | " + list;
        }

        /// <summary>
        /// The whole mismatch kick reason: <c>Mod mismatch: </c> and the problems list. The kick path composes nothing
        /// of its own (job FMAN14, R45 finding 13). The note that used to lead it named a copy the server had on offer;
        /// servers no longer send mod files (2026-10-05), so there is nothing to name.
        /// </summary>
        public static string MismatchKickReason(IReadOnlyList<string>? problems) => MismatchReason(problems, null);

        // ---- job FMAN18, retargeted 2026-10-05: defer the kick when the client can fix the miss itself ----

        /// <summary>
        /// Whether a failing handshake answer may open an install window instead of kicking, and the ids the window is
        /// for (job FMAN18).
        /// </summary>
        internal readonly struct MissVerdict
        {
            public MissVerdict(bool defer, IReadOnlyList<string> ids, string why, IReadOnlyList<MissFix>? fixes = null)
            {
                Defer = defer;
                Ids = ids ?? Array.Empty<string>();
                Why = why ?? "";
                Fixes = fixes ?? Array.Empty<MissFix>();
            }

            /// <summary>True: hold the kick, send the deferred Result, arm the window.</summary>
            public bool Defer { get; }

            /// <summary>The ids the window waits for (every failing row).</summary>
            public IReadOnlyList<string> Ids { get; }

            /// <summary>How each of <see cref="Ids"/> is fixed, in the same order.</summary>
            public IReadOnlyList<MissFix> Fixes { get; }

            /// <summary>Operator-facing reason the kick was <em>not</em> deferred. Empty when it was.</summary>
            public string Why { get; }
        }

        /// <summary>
        /// The FMAN18 decision, retargeted (2026-10-05): servers no longer send mod files, so the join is held open only
        /// when every failing row can be fixed <b>on the client</b> during the join (<paramref name="fixFor"/>: a
        /// Thunderstore package serving the needed version, or a kept or turned-off copy the client declared).
        ///
        /// <para>Every failing row must be fixable, or there is nothing to wait for. Deferring on a partial match would
        /// hold a client for a minute and then kick it for the row the window could never have fixed. Three conditions,
        /// all consequences the code has established rather than heuristics:</para>
        /// <list type="number">
        /// <item>every entry in <paramref name="problems"/> corresponds to a failing id — the overflow / cap-exceeded
        /// string is inserted into <c>problems</c> with no id behind it, and it is a protocol failure nothing fixes;</item>
        /// <item>every failing id is one <em>this server's own</em> <c>Check</c> rejected
        /// (<see cref="ServerEnforcedFailures"/>) — the reverse-walk ids are rows the <em>client</em> requires and this
        /// server does not have;</item>
        /// <item>every one of those has a fix (<see cref="MissFix"/> other than <c>None</c>).</item>
        /// </list>
        ///
        /// <para><b>And the client must be able to spend the window</b> (<paramref name="clientCanInstall"/>,
        /// <see cref="Capability.InstallWindow"/>, job FMAN20). Fail-closed in every direction: a null input, a throwing
        /// <paramref name="fixFor"/> or anything unexpected returns <c>Defer = false</c>, and the caller kicks. Nothing here
        /// can prevent a disconnect; it can only postpone one.</para>
        /// </summary>
        internal static MissVerdict DecideMiss(
            IReadOnlyList<string>? problems,
            IReadOnlyList<string>? failedIds,
            IReadOnlyList<ModRecord>? serverRows,
            IReadOnlyList<ModRecord>? clientRows,
            Func<EnforcedFailure, MissFix>? fixFor,
            bool graceAvailable,
            bool clientCanInstall = true)
        {
            try
            {
                if (!graceAvailable)
                    return new MissVerdict(false, Array.Empty<string>(), "no install window is available for this user");
                if (!clientCanInstall)
                    return new MissVerdict(false, Array.Empty<string>(), NoInstallWindowCapability);
                if (failedIds == null || failedIds.Count == 0)
                    return new MissVerdict(false, Array.Empty<string>(), "the failure names no mod id");
                if (problems != null && problems.Count != failedIds.Count)
                    return new MissVerdict(false, Array.Empty<string>(), "the answer also failed a check nothing on the client fixes");

                var enforced = ServerEnforcedFailures(failedIds, serverRows, clientRows);
                if (enforced.Count != failedIds.Count)
                    return new MissVerdict(false, Array.Empty<string>(), "a failing row is not one this server enforces");

                var ids = new List<string>(enforced.Count);
                var fixes = new List<MissFix>(enforced.Count);
                for (int i = 0; i < enforced.Count; i++)
                {
                    MissFix fix;
                    try { fix = fixFor != null ? fixFor(enforced[i]) : MissFix.None; }
                    catch { fix = MissFix.None; }
                    if (fix == MissFix.None)
                        return new MissVerdict(false, Array.Empty<string>(),
                            "'" + enforced[i].Id + "' cannot be fixed during the join (no Thunderstore package serving that version, and the client declared no kept or turned-off copy)");
                    ids.Add(enforced[i].Id);
                    fixes.Add(fix);
                }

                return new MissVerdict(true, ids, "", fixes);
            }
            catch
            {
                return new MissVerdict(false, Array.Empty<string>(), "the deferral check threw");
            }
        }

        /// <summary>
        /// The head of a deferred <see cref="Result"/>'s message (job FMAN18). Distinct from
        /// <see cref="MismatchPrefix"/> so a log or a client can tell the two apart by text alone.
        /// </summary>
        public const string DeferredPrefix = "Mods needed: ";

        /// <summary>
        /// <see cref="MissVerdict.Why"/> when the client did not declare
        /// <see cref="Capability.InstallWindow"/> (job FMAN20). Named so the log line an operator
        /// greps for is pinned by a test rather than retyped.
        /// </summary>
        public const string NoInstallWindowCapability =
            "the client did not declare the install-window and client-fix capabilities (it is running a Core that "
            + "cannot fix the join on its own side and re-answer within a deferred result)";

        /// <summary>
        /// Whether a client can spend an install window since 2026-10-05: it declared <see cref="Capability.InstallWindow"/>
        /// <b>and</b> <see cref="Capability.ClientFix"/>. An older Core declares only the first and never re-answers after
        /// a client-side fix.
        /// </summary>
        public static bool CanSpendInstallWindow(Capability caps) =>
            (caps & (Capability.InstallWindow | Capability.ClientFix)) == (Capability.InstallWindow | Capability.ClientFix);

        /// <summary>
        /// The message on the deferred <see cref="Result"/> (<see cref="ResultDeferred"/>): what is needed, where the
        /// player fixes it (the join screens: Thunderstore, a kept copy, Turn on, Pause), and how long the client has.
        /// Fitted by <see cref="Result.Create"/>'s own <see cref="TruncateReason"/>, and short by construction — it names
        /// at most <see cref="MaxNoteIds"/> ids and counts the rest.
        ///
        /// <para><b>ASCII only</b> (job FMAN20, R50 finding 4). This sentence is carried by
        /// <see cref="WireMessageBytes"/>, which is <c>Encoding.ASCII</c>; a test round-trips it.</para>
        /// </summary>
        public static string DeferredResultMessage(IReadOnlyList<string>? ids, int graceSeconds)
        {
            string names = NameIdList(ids);
            if (names.Length == 0) names = "the mods this server requires";
            return DeferredPrefix + names + ". Install from the join screens within "
                + graceSeconds.ToString(CultureInfo.InvariantCulture) + " s, or this join is refused.";
        }

        /// <summary>
        /// The kick reason when the window lapses with no passing re-answer (job FMAN18). It names the window, because
        /// "Mod mismatch: x missing" alone would leave a player who watched an install finish with no idea that a clock
        /// had run out on them.
        /// </summary>
        public static string NotInstalledReason(IReadOnlyList<string>? ids, int graceSeconds)
        {
            string names = NameIdList(ids);
            if (names.Length == 0) names = "the missing mods";
            bool many = ids != null && ids.Count > 1;
            // A6 (live 2026-10-07): the window lapses only when no passing answer came; the mod may well be installed by
            // now (an answer that never went out), so the sentence does not send a player who fixed it back to the screens.
            return MismatchPrefix + names + (many ? " were" : " was")
                + " not confirmed within "
                + graceSeconds.ToString(CultureInfo.InvariantCulture) + " s (this client did not answer again in time). "
                + "If " + (many ? "they are" : "it is") + " installed now, join again; if not, install " + (many ? "them" : "it") + " from the join screens first.";
        }

        /// <summary>
        /// Parse a dotted numeric version. A leading <c>v</c>/<c>V</c> is skipped. A proper
        /// <c>major.minor</c> is required; patch and further numeric components are optional
        /// (<c>1.2.3.4</c> is a version: major 1, minor 2, patch 3; the fourth part is ignored).
        /// After the numbers, only end of string, a <c>-</c> pre-release, or a <c>+</c> build
        /// suffix is accepted (<c>1.2.3foo</c> is not a version). Returns false when there is no
        /// leading integer or no minor component.
        /// </summary>
        public static bool TryParseVersion(string? value, out int major, out int minor, out int patch) =>
            TryParseVersion(value, out major, out minor, out patch, out _);

        /// <summary>
        /// Same as <see cref="TryParseVersion(string?, out int, out int, out int)"/> and also
        /// returns the <c>-</c> pre-release token (empty for a release). <c>+</c> build metadata
        /// is not part of the token.
        /// </summary>
        public static bool TryParseVersion(string? value, out int major, out int minor, out int patch, out string prerelease)
        {
            major = 0;
            minor = 0;
            patch = 0;
            prerelease = "";
            if (string.IsNullOrEmpty(value)) return false;

            int i = 0;
            if (value[0] == 'v' || value[0] == 'V') i = 1;
            if (!ReadComponent(value, ref i, out major)) return false;
            if (i >= value.Length || value[i] != '.') return false;
            i++;
            if (!ReadComponent(value, ref i, out minor)) return false;
            if (i < value.Length && value[i] == '.')
            {
                i++;
                if (!ReadComponent(value, ref i, out patch)) return false;
            }
            // Four-part (and further dotted numeric) versions: ignore everything after
            // major.minor for the Minor compare. `1.2.3.4` is a legal System.Version.
            while (i < value.Length && value[i] == '.')
            {
                i++;
                if (!ReadComponent(value, ref i, out _)) return false;
            }

            if (i >= value.Length) return true;
            if (value[i] == '+') return true;
            if (value[i] != '-') return false;
            i++;
            int start = i;
            while (i < value.Length && value[i] != '+') i++;
            if (start >= i) return false;
            prerelease = value.Substring(start, i - start);
            return true;
        }

        /// <summary>
        /// True when both strings parse as <c>major.minor</c>, share major and minor, and share
        /// the same pre-release token (case-insensitive). A release does not match a pre-release:
        /// <c>1.0-Beta</c> vs <c>1.0.5</c> is a mismatch. <c>+</c> build metadata is ignored.
        /// Identical strings (case-insensitive) always match so two plugins at <c>1.2.3.4</c>
        /// or at the same unparseable token never kick each other. Used by
        /// <see cref="NetworkVersionRule.Minor"/>.
        /// </summary>
        public static bool SameMinor(string a, string b)
        {
            a ??= "";
            b ??= "";
            if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;
            return TryParseVersion(a, out int aMaj, out int aMin, out _, out var aPre)
                && TryParseVersion(b, out int bMaj, out int bMin, out _, out var bPre)
                && aMaj == bMaj && aMin == bMin
                && string.Equals(aPre, bPre, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Apply <paramref name="rule"/> to two version strings. <see cref="NetworkVersionRule.None"/>
        /// is always true; Exact is case-insensitive string equality; Minor is <see cref="SameMinor"/>.
        /// Unknown rules are false.
        /// </summary>
        public static bool VersionsMatch(string expected, string got, NetworkVersionRule rule)
        {
            expected ??= "";
            got ??= "";
            switch (rule)
            {
                case NetworkVersionRule.None:
                    return true;
                case NetworkVersionRule.Exact:
                    return string.Equals(expected, got, StringComparison.OrdinalIgnoreCase);
                case NetworkVersionRule.Minor:
                    return SameMinor(expected, got);
                default:
                    return false;
            }
        }

        /// <summary>
        /// CLIENTPOLICY1: the <c>AtLeast</c> rule of the admins' client mod rows ("1.2.0 or newer"). True when
        /// <paramref name="got"/> is <paramref name="required"/> or later in semantic-version order
        /// (<see cref="CompareOrder"/>); when either side does not parse as a version it falls back as
        /// <see cref="SameMinor"/> does, so only the identical string (case-insensitive) passes. Not a
        /// <see cref="NetworkVersionRule"/>: the handshake's own wire rules are unchanged.
        /// </summary>
        public static bool AtLeast(string? required, string? got)
        {
            required ??= "";
            got ??= "";
            if (string.Equals(required, got, StringComparison.OrdinalIgnoreCase)) return true;
            int? order = CompareOrder(got, required);
            return order != null && order.Value >= 0;
        }

        /// <summary>
        /// CLIENTPOLICY1: semantic-version order of <paramref name="a"/> against <paramref name="b"/> (negative, zero,
        /// positive), or null when either does not parse. Every dotted number counts (<c>1.2.3.4</c> after
        /// <c>1.2.3.3</c>; a missing part is 0); a pre-release (<c>-beta.2</c>) comes before its release and
        /// pre-release identifiers compare as SemVer does (numbers numerically, before words); <c>+</c> build
        /// metadata is ignored. A leading <c>v</c> is skipped, and <c>major.minor</c> is required, as in
        /// <see cref="TryParseVersion(string?, out int, out int, out int)"/>.
        /// </summary>
        public static int? CompareOrder(string? a, string? b)
        {
            if (!TryOrderParts(a, out var x, out var xp) || !TryOrderParts(b, out var y, out var yp)) return null;
            for (int i = 0; i < Math.Max(x.Count, y.Count); i++)
            {
                long p = i < x.Count ? x[i] : 0, q = i < y.Count ? y[i] : 0;
                if (p != q) return p < q ? -1 : 1;
            }

            if (xp.Length == 0 && yp.Length == 0) return 0;
            if (xp.Length == 0) return 1;
            if (yp.Length == 0) return -1;
            string[] xs = xp.Split('.'), ys = yp.Split('.');
            for (int i = 0; i < Math.Min(xs.Length, ys.Length); i++)
            {
                bool xn = long.TryParse(xs[i], NumberStyles.None, CultureInfo.InvariantCulture, out long xv);
                bool yn = long.TryParse(ys[i], NumberStyles.None, CultureInfo.InvariantCulture, out long yv);
                int c = xn && yn ? xv.CompareTo(yv) : xn ? -1 : yn ? 1 : string.Compare(xs[i], ys[i], StringComparison.OrdinalIgnoreCase);
                if (c != 0) return c < 0 ? -1 : 1;
            }

            return xs.Length == ys.Length ? 0 : xs.Length < ys.Length ? -1 : 1;
        }

        private static bool TryOrderParts(string? value, out List<long> parts, out string prerelease)
        {
            parts = new List<long>();
            prerelease = "";
            string s = (value ?? "").Trim();
            if (s.Length > 0 && (s[0] == 'v' || s[0] == 'V')) s = s.Substring(1);
            int plus = s.IndexOf('+');
            if (plus >= 0) s = s.Substring(0, plus);
            int dash = s.IndexOf('-');
            if (dash >= 0)
            {
                prerelease = s.Substring(dash + 1);
                s = s.Substring(0, dash);
                if (prerelease.Length == 0) return false;
            }

            if (s.Length == 0) return false;
            foreach (string p in s.Split('.'))
            {
                if (p.Length == 0 || p.Length > 18 || !long.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out long n)) return false;
                parts.Add(n);
            }

            return parts.Count >= 2;
        }

        /// <summary>
        /// Pack declared level/rule into a 24-byte version slot. Byte 22 has <see cref="MetaPresentFlag"/>
        /// set when <paramref name="declared"/> is true so a v1 ASCII read (stop at NUL) still
        /// returns the version. Does not write when byte 21 is already a version character: that
        /// row needs <see cref="CreateMetaRecord"/> instead of truncating.
        /// </summary>
        public static void PackMeta(byte[] versionSlot, bool declared, NetworkCompatibilityLevel level, NetworkVersionRule rule)
        {
            if (versionSlot == null || versionSlot.Length < MetaRuleOffset + 1) return;
            if (!declared)
            {
                versionSlot[MetaFlagOffset] = 0;
                versionSlot[MetaRuleOffset] = 0;
                return;
            }
            if (versionSlot[InSlotVersionChars] != 0) return;
            versionSlot[MetaFlagOffset] = (byte)(MetaPresentFlag | (byte)level);
            versionSlot[MetaRuleOffset] = (byte)rule;
        }

        /// <summary>
        /// Inverse of <see cref="PackMeta"/>. A slot without the present-flag is undeclared
        /// <see cref="NetworkCompatibilityLevel.ServerMustHave"/> + <see cref="NetworkVersionRule.Exact"/>.
        /// Kept so a v1 / older-v2 peer that only packed bytes 22–23 still round-trips.
        /// </summary>
        public static void UnpackMeta(
            byte[] versionSlot,
            out bool declared,
            out NetworkCompatibilityLevel level,
            out NetworkVersionRule rule)
        {
            declared = false;
            level = NetworkCompatibilityLevel.ServerMustHave;
            rule = NetworkVersionRule.Exact;
            if (versionSlot == null || versionSlot.Length < MetaRuleOffset + 1) return;
            byte flag = versionSlot[MetaFlagOffset];
            if ((flag & MetaPresentFlag) == 0) return;
            declared = true;
            level = (NetworkCompatibilityLevel)(flag & unchecked((byte)~MetaPresentFlag));
            rule = (NetworkVersionRule)versionSlot[MetaRuleOffset];
        }

        /// <summary>True for handshake-owned guids that must not consume a plugin slot as a real mod.</summary>
        public static bool IsReservedGuid(string? guid) =>
            string.Equals(guid, MetaRecordGuid, StringComparison.OrdinalIgnoreCase)
            || string.Equals(guid, OverflowGuid, StringComparison.OrdinalIgnoreCase)
            || string.Equals(guid, CapabilityGuid, StringComparison.OrdinalIgnoreCase)
            || string.Equals(guid, InstallWindowFixes.FixGuid, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// The client's capability row (job FMAN20). <see cref="Capability.None"/> has no row:
        /// absence and "nothing declared" must mean the same thing, because every client older
        /// than FMAN19 says it by saying nothing.
        /// </summary>
        public static ModRecord CreateCapabilityRecord(Capability caps) =>
            ModRecord.CreateDeclared(
                CapabilityGuid,
                CapabilityFormat + ((uint)caps).ToString("x", CultureInfo.InvariantCulture),
                NetworkCompatibilityLevel.ClientOnly,
                NetworkVersionRule.None);

        /// <summary>
        /// Inverse of <see cref="CreateCapabilityRecord"/> for one row's version blob. Anything
        /// that is not a well-formed blob of this format reads as <see cref="Capability.None"/>:
        /// a garbled or future-format claim must not be believed, and disbelief costs the peer
        /// only the immediate kick it would have had before FMAN18.
        /// </summary>
        public static Capability ReadCapabilityBlob(string? version)
        {
            if (string.IsNullOrEmpty(version)) return Capability.None;
            if (version![0] != CapabilityFormat) return Capability.None;
            string hex = version.Substring(1);
            if (hex.Length == 0 || hex.Length > 8) return Capability.None;
            if (!uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint bits))
                return Capability.None;
            return (Capability)bits;
        }

        /// <summary>
        /// The capabilities a peer declared in <paramref name="rows"/>, or
        /// <see cref="Capability.None"/> when it declared none. Duplicate rows: the first wins,
        /// matching <see cref="ApplyMetaRecord"/>'s read of the metadata row.
        /// </summary>
        public static Capability ReadCapabilities(IReadOnlyList<ModRecord>? rows)
        {
            if (rows == null) return Capability.None;
            for (int i = 0; i < rows.Count; i++)
            {
                if (string.Equals(rows[i].Guid, CapabilityGuid, StringComparison.OrdinalIgnoreCase))
                    return ReadCapabilityBlob(rows[i].Version);
            }

            return Capability.None;
        }

        /// <summary>
        /// Append the capability row to a reply, last, if there is a free slot. False when there
        /// is nothing to declare or no room — and no room is <b>fail-closed</b>: a client with
        /// sixteen real rows to answer with does not get an install window, which is the
        /// behavior it had before FMAN18. Answering the server's question is worth more than
        /// declaring a capability, so no asked row is ever dropped to make space.
        /// </summary>
        public static bool AppendCapabilityRow(List<ModRecord>? rows, Capability caps)
        {
            if (rows == null || caps == Capability.None) return false;
            for (int i = rows.Count - 1; i >= 0; i--)
                if (string.Equals(rows[i].Guid, CapabilityGuid, StringComparison.OrdinalIgnoreCase))
                    rows.RemoveAt(i);
            if (rows.Count >= MaxMods) return false;
            rows.Add(CreateCapabilityRecord(caps));
            return true;
        }

        /// <summary>
        /// Mod compatibility C1/C2: OR <paramref name="extra"/> into the capability row of a request, or append the row
        /// last when there is none and a slot is free. False when there was no room: the peer then sees none of the new
        /// bits and behaves exactly as before them.
        /// </summary>
        public static bool MergeCapabilityBits(List<ModRecord>? rows, Capability extra)
        {
            if (rows == null || extra == Capability.None) return false;
            bool hadRow = false;
            for (int i = 0; i < rows.Count; i++)
                if (string.Equals(rows[i].Guid, CapabilityGuid, StringComparison.OrdinalIgnoreCase)) { hadRow = true; break; }
            var current = ReadCapabilities(rows);
            if (hadRow && (current & extra) == extra) return true;
            return AppendCapabilityRow(rows, current | extra);
        }

        /// <summary>
        /// Dedicated metadata row for <paramref name="rows"/> (one pack-char per slot, format
        /// <see cref="MetaRecordFormat"/>). Callers append this when a declared version is longer
        /// than <see cref="InSlotVersionChars"/>.
        /// </summary>
        public static ModRecord CreateMetaRecord(IReadOnlyList<ModRecord>? rows)
        {
            var chars = new char[1 + MaxMods];
            chars[0] = MetaRecordFormat;
            for (int i = 0; i < MaxMods; i++)
            {
                if (rows == null || i >= rows.Count || IsReservedGuid(rows[i].Guid))
                    chars[i + 1] = EncodeMeta(false, NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact);
                else
                    chars[i + 1] = EncodeMeta(rows[i].Declared, rows[i].Level, rows[i].VersionRule);
            }
            return ModRecord.CreateDeclared(
                MetaRecordGuid,
                new string(chars),
                NetworkCompatibilityLevel.ClientOnly,
                NetworkVersionRule.None);
        }

        /// <summary>
        /// Overlay dedicated-record metadata onto in-slot reads and drop the metadata row.
        /// In-slot flags remain if no dedicated record is present (v1 / older-v2).
        /// </summary>
        public static List<ModRecord> ApplyMetaRecord(IReadOnlyList<ModRecord>? rows)
        {
            var result = new List<ModRecord>();
            if (rows == null) return result;
            string? blob = null;
            for (int i = 0; i < rows.Count; i++)
            {
                if (string.Equals(rows[i].Guid, MetaRecordGuid, StringComparison.OrdinalIgnoreCase))
                {
                    blob = rows[i].Version;
                    break;
                }
            }

            bool haveBlob = blob != null && blob.Length >= 2 && blob[0] == MetaRecordFormat;
            for (int i = 0; i < rows.Count; i++)
            {
                var rec = rows[i];
                if (string.Equals(rec.Guid, MetaRecordGuid, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (haveBlob && i < MaxMods && i + 1 < blob!.Length
                    && DecodeMeta(blob[i + 1], out bool declared, out var level, out var rule)
                    && declared)
                {
                    rec = new ModRecord(rec.Guid, rec.Version, level, rule, declared: true);
                }
                result.Add(rec);
            }
            return result;
        }

        /// <summary>Apply dedicated metadata and drop reserved handshake guids before <see cref="Evaluate"/>.</summary>
        public static List<ModRecord> MaterializeWire(IReadOnlyList<ModRecord>? rows)
        {
            var applied = ApplyMetaRecord(rows);
            var list = new List<ModRecord>(applied.Count);
            for (int i = 0; i < applied.Count; i++)
            {
                if (IsReservedGuid(applied[i].Guid)) continue;
                if (string.IsNullOrEmpty(applied[i].Guid)) continue;
                list.Add(applied[i]);
            }
            return list;
        }

        /// <summary>
        /// Server request: skip <see cref="NetworkCompatibilityLevel.ClientOnly"/> (those rows
        /// must not consume a slot), keep at most <see cref="MaxMods"/>, prefer declared
        /// <see cref="NetworkCompatibilityLevel.ServerMustHave"/>. An empty required list still
        /// produces a (empty) request so the client can volunteer extras.
        /// </summary>
        public static PackedList PackRequest(IReadOnlyList<ModRecord>? required)
        {
            var candidates = new List<ModRecord>();
            if (required != null)
            {
                for (int i = 0; i < required.Count; i++)
                {
                    var rec = required[i];
                    if (string.IsNullOrEmpty(rec.Guid) || IsReservedGuid(rec.Guid)) continue;
                    if (rec.Level == NetworkCompatibilityLevel.ClientOnly) continue;
                    candidates.Add(rec);
                }
            }
            SortByWirePriority(candidates);

            var rows = new List<ModRecord>(MaxMods);
            var dropped = new List<ModRecord>();
            for (int i = 0; i < candidates.Count; i++)
            {
                if (rows.Count >= MaxMods) dropped.Add(candidates[i]);
                else rows.Add(candidates[i]);
            }

            bool declaredDropped = AnyDeclaredServerMustHave(dropped);
            AppendMetaIfNeeded(rows, dropped, askedGuids: null, emitOverflow: false, ref declaredDropped);
            return new PackedList(rows, dropped, declaredDropped);
        }

        /// <summary>
        /// Client reply: asked guids first, then extras with declared
        /// <see cref="NetworkCompatibilityLevel.ServerMustHave"/> before weaker rows. ClientOnly
        /// extras are omitted (they do not participate in reverse evaluation). A declared
        /// <see cref="NetworkCompatibilityLevel.ServerMustHave"/> extra that cannot fit is
        /// listed in <see cref="PackedList.Dropped"/> and signaled with
        /// <see cref="OverflowGuid"/> so the server fails closed. A dropped
        /// <see cref="NetworkCompatibilityLevel.NoNeedForSync"/> extra is omitted only: it
        /// does not fail closed and never overwrites an asked-row answer.
        /// </summary>
        public static PackedList PackReply(IReadOnlyList<ModRecord>? asked, IReadOnlyList<ModRecord>? provided)
        {
            var providedBy = Index(provided);
            var added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var askedGuids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var rows = new List<ModRecord>(MaxMods);
            var dropped = new List<ModRecord>();

            if (asked != null)
            {
                for (int i = 0; i < asked.Count; i++)
                {
                    var rec = asked[i];
                    if (string.IsNullOrEmpty(rec.Guid) || IsReservedGuid(rec.Guid)) continue;
                    if (rows.Count >= MaxMods) break;
                    if (providedBy.TryGetValue(rec.Guid, out var have))
                        rows.Add(have);
                    else
                        rows.Add(new ModRecord(rec.Guid, "", rec.Level, rec.VersionRule, rec.Declared));
                    added.Add(rec.Guid);
                    askedGuids.Add(rec.Guid);
                }
            }

            var extras = new List<ModRecord>();
            if (provided != null)
            {
                for (int i = 0; i < provided.Count; i++)
                {
                    var rec = provided[i];
                    if (string.IsNullOrEmpty(rec.Guid) || IsReservedGuid(rec.Guid)) continue;
                    if (added.Contains(rec.Guid)) continue;
                    if (rec.Level == NetworkCompatibilityLevel.ClientOnly) continue;
                    extras.Add(rec);
                }
            }
            SortByWirePriority(extras);
            for (int i = 0; i < extras.Count; i++)
            {
                if (rows.Count >= MaxMods) dropped.Add(extras[i]);
                else
                {
                    rows.Add(extras[i]);
                    added.Add(extras[i].Guid);
                }
            }

            bool declaredDropped = AnyDeclaredServerMustHave(dropped);
            if (declaredDropped) EnsureOverflowRow(rows, dropped, askedGuids);
            AppendMetaIfNeeded(rows, dropped, askedGuids, emitOverflow: true, ref declaredDropped);
            return new PackedList(rows, dropped, declaredDropped);
        }

        /// <summary>
        /// True when a server plugin at this level makes the handshake fail-closed if the
        /// client never answers. <see cref="NetworkCompatibilityLevel.NoNeedForSync"/> and
        /// <see cref="NetworkCompatibilityLevel.ClientOnly"/> do not; <see cref="NetworkCompatibilityLevel.ServerMustHave"/>
        /// and any unknown level do (unknown is fail-closed).
        /// </summary>
        public static bool LevelRequiresAnswer(NetworkCompatibilityLevel level) =>
            level != NetworkCompatibilityLevel.NoNeedForSync
            && level != NetworkCompatibilityLevel.ClientOnly;

        /// <summary>
        /// True when at least one non-reserved server row requires a handshake answer.
        /// An empty list, or a list of only optional / client-only plugins, does not
        /// kick a silent client — the server logs unverified and continues.
        /// </summary>
        public static bool ServerRequiresAnswer(IReadOnlyList<ModRecord>? required)
        {
            if (required == null) return false;
            for (int i = 0; i < required.Count; i++)
            {
                if (!IsEnforcedRow(required[i])) continue;
                return true;
            }
            return false;
        }

        /// <summary>
        /// Plugin list for the no-answer kick reason: every enforced row as
        /// <c>{guid} {version} {Level}/{Rule}</c>, comma-separated. Empty when nothing
        /// is at <see cref="NetworkCompatibilityLevel.ServerMustHave"/> (or unknown)
        /// with any version rule.
        /// </summary>
        public static string FormatRequiredSummary(IReadOnlyList<ModRecord>? required)
        {
            var parts = new List<string>();
            if (required != null)
            {
                for (int i = 0; i < required.Count; i++)
                {
                    var rec = required[i];
                    if (!IsEnforcedRow(rec)) continue;
                    parts.Add(rec.Guid + " " + rec.Version + " " + rec.Level + "/" + rec.VersionRule);
                }
            }
            return parts.Count == 0 ? "none" : string.Join(", ", parts);
        }

        /// <summary>
        /// Readable kick reason when the client never answered. Same path as a mismatch:
        /// log the full string, then Result + Disconnect get <see cref="TruncateReason"/>.
        /// </summary>
        public static string NoAnswerReason(int timeoutSeconds, string? requiredList)
        {
            if (timeoutSeconds < 0) timeoutSeconds = 0;
            return "Progenitor: no handshake answer within "
                + timeoutSeconds.ToString(CultureInfo.InvariantCulture)
                + " s (server requires: " + (string.IsNullOrEmpty(requiredList) ? "none" : requiredList) + ")";
        }

        /// <summary>
        /// Readable kick reason when the server could not deliver the handshake ask
        /// (SendToUser failed, one next-frame retry also failed). The client never
        /// saw the request, so a "no answer" timeout would be a false kick.
        /// </summary>
        public static string CouldNotAskReason() =>
            "Progenitor: could not ask for a handshake answer";

        /// <summary>
        /// CLIENTPOLICY2 (live 2026-10-05): a player's name for the server log. A brand-new player has no character name
        /// until character creation ends, and the log read "Handshake: asked  about 9 mod(s)" and "notice to  (…)":
        /// such a player is <c>a new player (&lt;platform id&gt;)</c>, or <c>a new player</c> without one.
        /// </summary>
        internal static string PlayerLabel(string? characterName, ulong platformId)
        {
            string name = (characterName ?? "").Trim();
            if (name.Length > 0) return name;
            return platformId == 0 ? "a new player" : "a new player (" + platformId.ToString(CultureInfo.InvariantCulture) + ")";
        }

        /// <summary>
        /// What to do after an ask <c>SendToUser</c>. A failed send must not arm a
        /// deadline the client cannot satisfy: retry once next frame, then kick.
        /// </summary>
        public enum AskSendFollowUp : byte
        {
            ArmDeadline = 0,
            RetryOnce = 1,
            KickCouldNotAsk = 2,
        }

        /// <summary>
        /// Fail-closed skip: only a positive ghost / hosted-child determination
        /// skips. A throw, a missing approved-user table, or a missing row for a
        /// non-bot arms the deadline (an extra-instance that raced ahead of the
        /// approved-user insert is not treated as a ghost).
        /// </summary>
        public static bool ShouldSkipAnswerDeadline(
            bool lookupThrew,
            bool approvedTablePresent,
            bool userRowFound,
            bool hasNetConnection,
            bool isBot)
        {
            if (lookupThrew) return false;
            if (hasNetConnection) return false;
            // Ghost: User.IsBot and no peer. Extra-instance hail IsBot is false, so a
            // missing row for a real client still arms.
            if (isBot) return true;
            // Hosted child: approved-user row exists but NetConnectionId is null.
            if (approvedTablePresent && userRowFound) return true;
            return false;
        }

        /// <summary>
        /// Next step after attempting to send the handshake ask. <paramref name="sent"/>
        /// is the <c>SendToUser</c> result; <paramref name="alreadyRetried"/> is true
        /// on the next-frame retry. The caller must still skip ask/retry/kick when
        /// <see cref="HandshakeAnswerWatch.HasAnswered"/> is already true.
        /// </summary>
        public static AskSendFollowUp NextAskSendFollowUp(bool sent, bool alreadyRetried)
        {
            if (sent) return AskSendFollowUp.ArmDeadline;
            if (!alreadyRetried) return AskSendFollowUp.RetryOnce;
            return AskSendFollowUp.KickCouldNotAsk;
        }

        /// <summary>
        /// True when a later ask, retry, or could-not-ask kick must not run: the
        /// client already matched or mismatched. A false-negative <c>SendToUser</c>
        /// can still deliver the packet; the answer wins.
        /// </summary>
        public static bool ShouldSkipAsk(bool hasAnswered) => hasAnswered;

        /// <summary>
        /// Whether the delayed disconnect should proceed. An <see cref="HandshakeAnswerWatch.Status.Ok"/>
        /// client is never kicked (timeout or could-not-ask racing a real answer). A timeout
        /// kick also requires the row to still be <see cref="HandshakeAnswerWatch.Status.Timeout"/>.
        /// Mismatch still disconnects (<paramref name="timeoutKick"/> is false). Could-not-ask
        /// uses <see cref="ShouldScheduleCouldNotAskKick"/> /
        /// <see cref="ShouldSendCouldNotAskResult"/> instead of this method so a Pending/None
        /// row does not get a failure Result until the 2 s back-off.
        /// </summary>
        internal static bool ShouldProceedWithKick(HandshakeAnswerWatch.Status status, bool timeoutKick)
        {
            if (status == HandshakeAnswerWatch.Status.Ok) return false;
            if (timeoutKick) return status == HandshakeAnswerWatch.Status.Timeout;
            return true;
        }

        /// <summary>
        /// True when a could-not-ask follow-up may wait the 2 s Ok back-off. Ok / Mismatch /
        /// Unverified already have an outcome (or need no answer) and must not be kicked.
        /// Timeout is already on the no-answer path. Pending and None are the false-negative
        /// SendToUser case. Any answer cancels this schedule.
        /// </summary>
        internal static bool ShouldScheduleCouldNotAskKick(HandshakeAnswerWatch.Status status) =>
            status == HandshakeAnswerWatch.Status.Pending
            || status == HandshakeAnswerWatch.Status.None;

        /// <summary>
        /// Could-not-ask must not send a failure Result while the watch is still
        /// <see cref="HandshakeAnswerWatch.Status.Pending"/> or
        /// <see cref="HandshakeAnswerWatch.Status.None"/>: the client may still answer
        /// in the 2 s back-off. After that delay, send Result only if they still have
        /// not matched or mismatched. <see cref="HandshakeAnswerWatch.Status.Timeout"/>
        /// is already on the timeout path and must not double-send. Disconnect is a
        /// later callback (<see cref="ShouldDisconnectAfterCouldNotAskResult"/>).
        /// </summary>
        internal static bool ShouldSendCouldNotAskResult(HandshakeAnswerWatch.Status status, bool delayed)
        {
            if (!delayed) return false;
            return status == HandshakeAnswerWatch.Status.Pending
                || status == HandshakeAnswerWatch.Status.None;
        }

        /// <summary>
        /// True when the row still has no handshake outcome. Used to decide whether
        /// the could-not-ask Result hop should run; the post-Result disconnect hop
        /// uses <see cref="ShouldDisconnectAfterCouldNotAskResult"/> so an armed
        /// 20 s deadline is not a 4 s kick.
        /// </summary>
        internal static bool ShouldDisconnectCouldNotAsk(HandshakeAnswerWatch.Status status) =>
            status == HandshakeAnswerWatch.Status.Pending
            || status == HandshakeAnswerWatch.Status.None;

        /// <summary>
        /// After the could-not-ask Result (and an attempted <c>Arm</c>), disconnect
        /// on the second hop only when a mismatch owns the kick, or when Arm did not
        /// take and the row is still unanswered (timeout 0). An armed Pending row
        /// waits for the 20 s deadline; Ok / Unverified / Timeout are other paths.
        /// </summary>
        internal static bool ShouldDisconnectAfterCouldNotAskResult(
            HandshakeAnswerWatch.Status status,
            bool deadlineArmed)
        {
            if (status == HandshakeAnswerWatch.Status.Mismatch) return true;
            if (status == HandshakeAnswerWatch.Status.Ok
                || status == HandshakeAnswerWatch.Status.Unverified
                || status == HandshakeAnswerWatch.Status.Timeout)
            {
                return false;
            }

            if (deadlineArmed) return false;
            return status == HandshakeAnswerWatch.Status.Pending
                || status == HandshakeAnswerWatch.Status.None;
        }

        /// <summary>
        /// Two <see cref="HandshakeAnswerWatch.KickFlushSeconds"/> hops after a failed
        /// ask. First hop: send the could-not-ask Result and run <paramref name="armDeadline"/>
        /// (the 20 s answer wait). Second hop: disconnect with
        /// <see cref="DisconnectReasonAfterCouldNotAskResult"/> only when
        /// <see cref="ShouldDisconnectAfterCouldNotAskResult"/> is true. Tests inject
        /// <paramref name="postDelayed"/>; live code passes <c>MainThread.PostDelayed</c>.
        /// </summary>
        internal static void ScheduleCouldNotAskKick(
            Action<double, Action> postDelayed,
            HandshakeAnswerWatch watch,
            int userIndex,
            string couldNotAskReason,
            Action armDeadline,
            Action<string> sendResult,
            Action<string> disconnect)
        {
            if (postDelayed == null) throw new ArgumentNullException(nameof(postDelayed));
            if (watch == null) throw new ArgumentNullException(nameof(watch));
            if (sendResult == null) throw new ArgumentNullException(nameof(sendResult));
            if (disconnect == null) throw new ArgumentNullException(nameof(disconnect));
            if (string.IsNullOrEmpty(couldNotAskReason))
                couldNotAskReason = CouldNotAskReason();

            if (!ShouldScheduleCouldNotAskKick(watch.GetStatus(userIndex)))
                return;

            postDelayed(HandshakeAnswerWatch.KickFlushSeconds, () =>
            {
                var later = watch.GetStatus(userIndex);
                if (!ShouldSendCouldNotAskResult(later, delayed: true))
                    return;
                sendResult(couldNotAskReason);
                bool armed = false;
                if (CanArmDeadline(watch.GetStatus(userIndex)))
                {
                    armDeadline?.Invoke();
                    armed = watch.GetStatus(userIndex) == HandshakeAnswerWatch.Status.Pending;
                }

                postDelayed(HandshakeAnswerWatch.KickFlushSeconds, () =>
                {
                    var after = watch.GetStatus(userIndex);
                    if (!ShouldDisconnectAfterCouldNotAskResult(after, armed))
                        return;
                    string reason = DisconnectReasonAfterCouldNotAskResult(
                        after, watch.MismatchReasonOf(userIndex));
                    if (string.IsNullOrEmpty(reason))
                        reason = couldNotAskReason;
                    disconnect(reason);
                });
            });
        }

        /// <summary>
        /// True when an Ok or Mismatch answer has canceled the could-not-ask path.
        /// Either answer is enough; the 2 s back-off must not kick after that.
        /// </summary>
        internal static bool AnswerCancelsCouldNotAsk(HandshakeAnswerWatch.Status status) =>
            status == HandshakeAnswerWatch.Status.Ok
            || status == HandshakeAnswerWatch.Status.Mismatch;

        /// <summary>
        /// Kick / Result text after the could-not-ask Result fire. A mismatch that
        /// landed before disconnect uses <paramref name="mismatchReason"/>; Ok has
        /// no kick; otherwise <see cref="CouldNotAskReason"/>.
        /// </summary>
        internal static string DisconnectReasonAfterCouldNotAskResult(
            HandshakeAnswerWatch.Status status,
            string mismatchReason)
        {
            if (status == HandshakeAnswerWatch.Status.Mismatch)
                return mismatchReason ?? "";
            if (status == HandshakeAnswerWatch.Status.Ok)
                return "";
            return CouldNotAskReason();
        }

        /// <summary>
        /// True when <c>Arm</c> may write a Pending row. A retry that reports
        /// <c>sent</c> after <see cref="HandshakeAnswerWatch.OnAnswer"/> must not
        /// replace Ok/Mismatch with a fresh 20 s clock.
        ///
        /// <para><b>Job FMAN20 (R50 finding 11).</b> <c>AwaitingInstall</c> joins them. FMAN18
        /// added that status and updated <see cref="HandshakeAnswerWatch.Arm"/>, which is the real
        /// gate, but left this predicate — whose name says it is decisive — answering <c>true</c>
        /// for the one status whose entire purpose is that no deadline may be armed over it. Two
        /// places encoding one rule, one of them updated, is the shape that lets a later caller
        /// reintroduce a 20 s kick inside a 60 s window.</para>
        /// </summary>
        internal static bool CanArmDeadline(HandshakeAnswerWatch.Status status) =>
            status != HandshakeAnswerWatch.Status.Ok
            && status != HandshakeAnswerWatch.Status.Mismatch
            && status != HandshakeAnswerWatch.Status.AwaitingInstall;

        private static bool IsEnforcedRow(in ModRecord rec) =>
            !string.IsNullOrEmpty(rec.Guid)
            && !IsReservedGuid(rec.Guid)
            && LevelRequiresAnswer(rec.Level);

        /// <summary>
        /// Cut <paramref name="message"/> to <paramref name="maxChars"/> with
        /// <see cref="ReasonEllipsis"/> so Result and Disconnect show the same string. The
        /// caller logs the full text first.
        /// </summary>
        public static string TruncateReason(string? message, int maxChars = ResultMessageMaxChars)
        {
            message ??= "";
            if (maxChars < 0) maxChars = 0;
            if (message.Length <= maxChars) return message;
            if (maxChars <= ReasonEllipsis.Length) return message.Substring(0, maxChars);
            return message.Substring(0, maxChars - ReasonEllipsis.Length) + ReasonEllipsis;
        }

        /// <summary>
        /// The bytes <c>Result.Message</c> actually carries for <paramref name="message"/>: cut to
        /// <see cref="ResultMessageMaxChars"/>, then <c>Encoding.ASCII</c>.
        ///
        /// <para><b>Why this is not inlined in <c>Result.Create</c> (job FMAN20, R50 finding 4).</b>
        /// <c>Result</c> is an <c>unsafe</c> fixed-buffer struct that does not exist in the test
        /// build, so every assertion about a handshake string was a length check on the managed
        /// string and the encoder was never reached — which is how an em dash shipped in the one
        /// sentence a deferred player reads and arrived as <c>?</c>. The encoder now lives here,
        /// where a test can round-trip any message through it, and <c>Result.Create</c> calls
        /// it.</para>
        /// </summary>
        public static byte[] WireMessageBytes(string? message)
        {
            var wire = TruncateReason(message, ResultMessageMaxChars);
            var bytes = Encoding.ASCII.GetBytes(wire);
            if (bytes.Length > ResultMessageMaxChars)
                Array.Resize(ref bytes, ResultMessageMaxChars);
            return bytes;
        }

        /// <summary>
        /// What <c>Result.Text</c> reads back from <see cref="WireMessageBytes"/>: ASCII up to the
        /// first NUL. A message that does not survive <c>WireMessageText(WireMessageBytes(m)) == m</c>
        /// is a message a player sees mangled.
        /// </summary>
        public static string WireMessageText(byte[]? bytes)
        {
            if (bytes == null) return "";
            int len = 0;
            while (len < bytes.Length && bytes[len] != 0) len++;
            return Encoding.ASCII.GetString(bytes, 0, len);
        }

        /// <summary>
        /// How many leading characters of an over-long reason <see cref="TruncateReason"/> actually
        /// keeps. Not the same number as <see cref="ResultMessageMaxChars"/>: the ellipsis is spent
        /// out of the wire budget, so a cut reason carries <c>maxChars - ReasonEllipsis.Length</c>
        /// characters of the original and three characters of "there was more".
        ///
        /// <para><b>Job FMAN15, R46 finding 4.</b> The kick note (removed 2026-10-05) was fitted against
        /// the wire size and not against this number — one budget written down in two places, three
        /// apart. A note landing in that window was composed as "this fits", handed to
        /// <see cref="TruncateReason"/>, and lost its last characters to exactly the truncation the
        /// note-first ordering in <see cref="MismatchReason"/> exists to survive. Re-derived from
        /// the real strings: ids of 18 characters, three per cause, is the shortest ordinary case
        /// that lands there, and 71 (id-length, count) combinations do across id lengths 1-64 with
        /// one to five ids per cause.</para>
        ///
        /// <para><b>It probes the function instead of restating its arithmetic</b>, so the two
        /// cannot drift apart again: change the limit, the ellipsis or the cutting rule and every
        /// caller that must fit inside a truncated reason follows without being edited. The probe
        /// character is one that <see cref="ReasonEllipsis"/> does not contain, or an ellipsis made
        /// of the probe character would be miscounted as surviving input.</para>
        /// </summary>
        public static int SurvivingReasonChars(int maxChars = ResultMessageMaxChars)
        {
            if (maxChars < 0) maxChars = 0;
            char probe = ReasonProbeChar();
            string cut = TruncateReason(new string(probe, maxChars + 1), maxChars);
            int kept = 0;
            while (kept < cut.Length && cut[kept] == probe) kept++;
            return kept;
        }

        private static char ReasonProbeChar()
        {
            for (char c = 'a'; c <= 'z'; c++)
                if (ReasonEllipsis.IndexOf(c) < 0) return c;
            return '\u0001';
        }

        /// <summary>Kick / result line when declared ServerMustHave rows could not fit on the wire.</summary>
        public static string CapExceededReason(int droppedDeclaredCount) =>
            "handshake cap: " + droppedDeclaredCount.ToString(CultureInfo.InvariantCulture)
            + " declared row(s) did not fit in " + MaxMods.ToString(CultureInfo.InvariantCulture) + " slots";

        /// <summary>
        /// Same as <see cref="CapExceededReason(int)"/> and names the dropped
        /// <see cref="NetworkCompatibilityLevel.ServerMustHave"/> guids so the kick is not a
        /// blanket "N rows" with no identity.
        /// </summary>
        public static string CapExceededReason(IReadOnlyList<ModRecord> dropped)
        {
            var names = new List<string>();
            if (dropped != null)
            {
                for (int i = 0; i < dropped.Count; i++)
                {
                    if (!IsFailClosedDrop(dropped[i])) continue;
                    names.Add(dropped[i].Guid);
                }
            }
            if (names.Count == 0) return CapExceededReason(0);
            return "handshake cap: " + string.Join(", ", names)
                + " did not fit in " + MaxMods.ToString(CultureInfo.InvariantCulture) + " slots";
        }

        public static int CountDeclared(IReadOnlyList<ModRecord> dropped)
        {
            int n = 0;
            if (dropped == null) return 0;
            for (int i = 0; i < dropped.Count; i++)
                if (dropped[i].Declared) n++;
            return n;
        }

        public static int CountDeclaredServerMustHave(IReadOnlyList<ModRecord> dropped)
        {
            int n = 0;
            if (dropped == null) return 0;
            for (int i = 0; i < dropped.Count; i++)
                if (IsFailClosedDrop(dropped[i])) n++;
            return n;
        }

        private static Dictionary<string, ModRecord> Index(IReadOnlyList<ModRecord>? list)
        {
            var d = new Dictionary<string, ModRecord>(StringComparer.OrdinalIgnoreCase);
            if (list == null) return d;
            for (int i = 0; i < list.Count; i++)
            {
                var rec = list[i];
                if (string.IsNullOrEmpty(rec.Guid)) continue;
                if (IsReservedGuid(rec.Guid)) continue;
                d[rec.Guid] = rec;
            }
            return d;
        }

        /// <param name="serverLacksIt">F25: the reverse pass (this client's must-have mod, looked up on the server).</param>
        /// <param name="judge">C3: when set, a mod both sides run is judged by <see cref="VersionMatchJudge.Check"/> (the
        /// server's row against the client's version, whichever direction this walk is going).</param>
        private static string? Check(ModRecord mine, Dictionary<string, ModRecord> peer, bool serverLacksIt = false, VersionJudgeContext? judge = null)
        {
            if (string.IsNullOrEmpty(mine.Guid)) return null;

            switch (mine.Level)
            {
                case NetworkCompatibilityLevel.ClientOnly:
                    return null;
                case NetworkCompatibilityLevel.NoNeedForSync:
                    if (!TryGetPresent(peer, mine.Guid, out var optional)) return null;
                    return VersionProblem(mine, optional, serverLacksIt, judge);
                case NetworkCompatibilityLevel.ServerMustHave:
                    if (!TryGetPresent(peer, mine.Guid, out var required))
                        return MissingReason(mine.Guid, DisplayExpected(mine), serverLacksIt);
                    return VersionProblem(mine, required, serverLacksIt, judge);
                default:
                    return Reason(mine.Guid, DisplayExpected(mine), $"unknown compatibility level {(byte)mine.Level}");
            }
        }

        /// <summary>
        /// Does this row describe a mod the peer actually has? A row with an empty version is an
        /// <em>absence sentinel</em> written by <see cref="PackReply"/>, never a real declaration:
        /// a loaded plugin always carries a version. Public so the suite can pin the rule that a
        /// missing optional mod cannot impose a version requirement on the other side.
        /// </summary>
        public static bool IsPresentRecord(ModRecord rec) => !string.IsNullOrEmpty(rec.Version);

        private static bool TryGetPresent(Dictionary<string, ModRecord> peer, string guid, out ModRecord rec)
        {
            if (peer.TryGetValue(guid, out rec) && rec.Version != null && rec.Version.Length > 0)
                return true;
            rec = default;
            return false;
        }

        private static string? VersionProblem(ModRecord mine, ModRecord theirs, bool reverse, VersionJudgeContext? judge)
        {
            if (judge != null)
            {
                // Review fix (the newer side decides): in the reverse pass mine is the client's declared row. A client older
                // than C3 sent no range, so when its build is the newer one (or the versions do not compare), its own row's
                // rule is its only declaration about the older server and decides, exactly as before C3. When the server is
                // newer, the server's declared range or rule decides (Check below). Admin rules and strict modes always
                // go through Check.
                var settingsNow = judge.Settings ?? VersionMatchSettings.Default;
                if (reverse && judge.ClientRanges == null && settingsNow.Mode == VersionMatchingMode.Range && !settingsNow.TryRule(mine.Guid, out _)
                    && !(CompatVersion.Compare(mine.Version, theirs.Version) is int order && order < 0))
                    return VersionProblem(mine, theirs);
                return reverse ? VersionMatchJudge.Check(theirs, mine.Version, judge) : VersionMatchJudge.Check(mine, theirs.Version, judge);
            }
            return VersionProblem(mine, theirs);
        }

        private static string? VersionProblem(ModRecord mine, ModRecord theirs)
        {
            switch (mine.VersionRule)
            {
                case NetworkVersionRule.None:
                    return null;
                case NetworkVersionRule.Exact:
                    if (string.Equals(mine.Version, theirs.Version, StringComparison.OrdinalIgnoreCase))
                        return null;
                    return Reason(mine.Guid, mine.Version, theirs.Version);
                case NetworkVersionRule.Minor:
                    if (SameMinor(mine.Version, theirs.Version)) return null;
                    return Reason(mine.Guid, mine.Version + " (minor)", theirs.Version);
                default:
                    return Reason(mine.Guid, DisplayExpected(mine), $"unknown version rule {(byte)mine.VersionRule}");
            }
        }

        private static string DisplayExpected(ModRecord mine)
        {
            if (mine.VersionRule == NetworkVersionRule.Minor) return mine.Version + " (minor)";
            if (mine.VersionRule == NetworkVersionRule.None) return mine.Version + " (any)";
            return mine.Version;
        }

        private static string Reason(string guid, string expected, string got) =>
            $"{guid}: expected {expected}, got {got}";

        /// <summary>
        /// F25 (live 2026-10-07: "progenitor.necroarmy: expected 0.1.0, got missing" when the SERVER lacked the client's
        /// must-have mod): a missing mod says which side lacks it and what fixes it. The id stays first, before ": ".
        /// ASCII (the kick reason travels as ASCII).
        /// </summary>
        internal static string MissingReason(string guid, string expected, bool serverLacksIt) => serverLacksIt
            ? guid + ": your game needs it on the server (" + expected + "), but the server doesn't have it. Turn it off, or ask the admins to install it"
            : guid + ": the server needs it (" + expected + "), but your game doesn't have it. Install it from the join screens";

        private static bool ReadComponent(string value, ref int i, out int n)
        {
            n = 0;
            if (i >= value.Length || value[i] < '0' || value[i] > '9') return false;
            while (i < value.Length && value[i] >= '0' && value[i] <= '9')
            {
                int digit = value[i] - '0';
                if (n > (int.MaxValue - digit) / 10) return false;
                n = n * 10 + digit;
                i++;
            }
            return true;
        }

        private static bool IsAsciiField(string value, int max, string name, out string? error)
        {
            if (string.IsNullOrEmpty(value))
            {
                error = $"{name} is required (1-{max} ASCII)";
                return false;
            }
            if (value.Length > max)
            {
                error = $"{name} length {value.Length} exceeds {max} ASCII";
                return false;
            }
            for (int i = 0; i < value.Length; i++)
            {
                if (value[i] == 0 || value[i] > 0x7F)
                {
                    error = $"{name} is not ASCII";
                    return false;
                }
            }
            error = null;
            return true;
        }

        private static char EncodeMeta(bool declared, NetworkCompatibilityLevel level, NetworkVersionRule rule)
        {
            int packed = 0;
            if (declared) packed |= 0x20;
            packed |= ((int)level & 3) << 3;
            packed |= (int)rule & 7;
            return (char)('0' + packed);
        }

        private static bool DecodeMeta(char c, out bool declared, out NetworkCompatibilityLevel level, out NetworkVersionRule rule)
        {
            int packed = c - '0';
            if (packed < 0 || packed > 63)
            {
                declared = false;
                level = NetworkCompatibilityLevel.ServerMustHave;
                rule = NetworkVersionRule.Exact;
                return false;
            }
            declared = (packed & 0x20) != 0;
            level = (NetworkCompatibilityLevel)((packed >> 3) & 3);
            rule = (NetworkVersionRule)(packed & 7);
            return true;
        }

        private static int WirePriority(ModRecord rec)
        {
            if (rec.Declared && rec.Level == NetworkCompatibilityLevel.ServerMustHave) return 0;
            if (rec.Declared) return 1;
            return 2;
        }

        private static void SortByWirePriority(List<ModRecord> list)
        {
            var keyed = new List<(int pri, int index, ModRecord rec)>(list.Count);
            for (int i = 0; i < list.Count; i++)
                keyed.Add((WirePriority(list[i]), i, list[i]));
            keyed.Sort((a, b) =>
            {
                int c = a.pri.CompareTo(b.pri);
                return c != 0 ? c : a.index.CompareTo(b.index);
            });
            for (int i = 0; i < keyed.Count; i++)
                list[i] = keyed[i].rec;
        }

        private static bool IsFailClosedDrop(in ModRecord rec) =>
            rec.Declared
            && rec.Level == NetworkCompatibilityLevel.ServerMustHave
            && !IsReservedGuid(rec.Guid)
            && !string.IsNullOrEmpty(rec.Guid);

        private static bool AnyDeclaredServerMustHave(List<ModRecord> dropped)
        {
            for (int i = 0; i < dropped.Count; i++)
                if (IsFailClosedDrop(dropped[i])) return true;
            return false;
        }

        private static bool NeedsDedicatedMeta(List<ModRecord> rows)
        {
            for (int i = 0; i < rows.Count; i++)
            {
                var rec = rows[i];
                if (!rec.Declared || IsReservedGuid(rec.Guid)) continue;
                if (rec.Version != null && rec.Version.Length > InSlotVersionChars) return true;
            }
            return false;
        }

        private static bool NeedsDedicatedMetaForServerMustHave(List<ModRecord> rows)
        {
            for (int i = 0; i < rows.Count; i++)
            {
                var rec = rows[i];
                if (!rec.Declared || IsReservedGuid(rec.Guid)) continue;
                if (rec.Level != NetworkCompatibilityLevel.ServerMustHave) continue;
                if (rec.Version != null && rec.Version.Length > InSlotVersionChars) return true;
            }
            return false;
        }

        private static bool AnyNonReservedDeclared(List<ModRecord> rows)
        {
            for (int i = 0; i < rows.Count; i++)
            {
                if (rows[i].Declared && !IsReservedGuid(rows[i].Guid)) return true;
            }
            return false;
        }

        private static bool IsAskedGuid(HashSet<string>? askedGuids, string guid) =>
            askedGuids != null && askedGuids.Contains(guid);

        private static void AppendMetaIfNeeded(
            List<ModRecord> rows,
            List<ModRecord> dropped,
            HashSet<string>? askedGuids,
            bool emitOverflow,
            ref bool declaredDropped)
        {
            if (!AnyNonReservedDeclared(rows) && !NeedsDedicatedMeta(rows)) return;
            bool needs = NeedsDedicatedMeta(rows);
            if (rows.Count < MaxMods)
            {
                rows.Add(CreateMetaRecord(rows));
                return;
            }
            if (!needs) return;
            for (int i = rows.Count - 1; i >= 0; i--)
            {
                if (IsReservedGuid(rows[i].Guid)) continue;
                if (IsAskedGuid(askedGuids, rows[i].Guid)) continue;
                if (!rows[i].Declared)
                {
                    rows.RemoveAt(i);
                    rows.Add(CreateMetaRecord(rows));
                    return;
                }
            }
            if (!NeedsDedicatedMetaForServerMustHave(rows)) return;
            declaredDropped = true;
            if (emitOverflow) EnsureOverflowRow(rows, dropped, askedGuids);
        }

        private static void EnsureOverflowRow(List<ModRecord> rows, List<ModRecord> dropped, HashSet<string>? askedGuids)
        {
            int n = CountDeclaredServerMustHave(dropped);
            if (n <= 0) n = 1;
            var overflow = ModRecord.CreateDeclared(
                OverflowGuid,
                n.ToString(CultureInfo.InvariantCulture),
                NetworkCompatibilityLevel.ServerMustHave,
                NetworkVersionRule.Exact);
            for (int i = 0; i < rows.Count; i++)
            {
                if (string.Equals(rows[i].Guid, OverflowGuid, StringComparison.OrdinalIgnoreCase))
                {
                    rows[i] = overflow;
                    return;
                }
            }
            if (rows.Count < MaxMods)
            {
                rows.Add(overflow);
                return;
            }
            // Prefer a non-asked extra so an asked-row answer stays on the wire. Only
            // overwrite an asked guid when every slot is an asked answer and the marker
            // must still travel (dropped ServerMustHave extra, or meta that could not fit).
            for (int i = rows.Count - 1; i >= 0; i--)
            {
                if (IsReservedGuid(rows[i].Guid)) continue;
                if (IsAskedGuid(askedGuids, rows[i].Guid)) continue;
                rows[i] = overflow;
                return;
            }
            for (int i = rows.Count - 1; i >= 0; i--)
            {
                if (IsReservedGuid(rows[i].Guid)) continue;
                rows[i] = overflow;
                return;
            }
        }
    }

    private static readonly Dictionary<string, ModRecord> Required = new(StringComparer.OrdinalIgnoreCase); // server
    private static readonly Dictionary<string, ModRecord> Provided = new(StringComparer.OrdinalIgnoreCase); // client
    private static readonly Dictionary<string, ModRecord> RequiredCapDropped = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, ModRecord> ProvidedCapDropped = new(StringComparer.OrdinalIgnoreCase);

    internal static readonly HandshakeAnswerWatch AnswerWatch = new(LiveClock);

    /// <summary>REASK1: who answered successfully this session, carried across Core generations (<see cref="HandshakeSessionMemory"/>).</summary>
    internal static HandshakeSessionMemory SessionMemory = new();

    /// <summary>REASK1: the users with a soft re-ask waiting for its answer (<see cref="HandshakeAskKind.SoftReask"/>).</summary>
    private static readonly HashSet<int> SoftReasks = new();

    /// <summary>REASK1: true while a re-ask after a Core generation change publishes its PlayerConnected events.</summary>
    private static bool _reaskScope;

    internal static bool SoftReaskPending(int userIndex)
    {
        lock (SoftReasks) return SoftReasks.Contains(userIndex);
    }

    /// <summary>
    /// RESEND1: what the request said to each user and the re-send answers each still owes, so a mid-session change of the
    /// required table is re-sent (<see cref="HandshakeRequirementWatch"/>).
    /// </summary>
    internal static readonly HandshakeRequirementWatch RequirementWatch = new();

    /// <summary>
    /// RESEND1: the required table changed (a server plugin's <c>Require</c> on load, its <c>Unrequire</c> on unload).
    /// Schedules one settle-then-compare; the live server re-sends its request to every handshake-ok client when the rows
    /// differ from the ones it last sent (<see cref="HandshakeRequirementWatch"/>). Never throws.
    /// </summary>
    internal static void NoteRequiredChanged()
    {
        try
        {
            if (!RequirementWatch.TrySchedule()) return;
#if !PROGENITOR_TESTS
            if (!_installed || !Plugin.IsServerProcess)
            {
                RequirementWatch.Unschedule();
                return;
            }

            Dev.MainThread.PostDelayed(HandshakeRequirementWatch.SettleSeconds, ResendRequestIfRequirementsChanged, DeadlineOwner);
#else
            TestRequirementCompareScheduled?.Invoke();
#endif
        }
        catch (Exception e)
        {
            RequirementWatch.Unschedule();
            Plugin.Logger?.LogWarning("Handshake: scheduling the requirement re-send threw: " + e.Message);
        }
    }

    private sealed class ReaskScopeHandle : IDisposable
    {
        private readonly bool _was;
        internal ReaskScopeHandle() { _was = _reaskScope; _reaskScope = true; }
        public void Dispose() => _reaskScope = _was;
    }

    /// <summary>
    /// REASK1: the PlayerConnected events published inside this scope are re-asks after a Core generation change, not
    /// joins (<see cref="Runtime.ReaskConnectedHandshake"/>): a user remembered as answered gets a soft re-ask.
    /// </summary>
    internal static IDisposable BeginReask() => new ReaskScopeHandle();

    /// <summary>
    /// Job FMAN18. The deferred-kick windows, one per user index, on the same live clock the
    /// answer deadline uses. Cleared by <c>Uninstall</c> and by every disconnect.
    /// </summary>
    internal static readonly HandshakeInstallGrace InstallGrace = new(LiveClock);

    /// <summary>
    /// Job FMAN21 (R52 finding 3). The floor between two re-answer side-effect runs for one user.
    /// </summary>
    internal static readonly HandshakeReAnswer ReAnswers = new(LiveClock);

#if PROGENITOR_TESTS
    /// <summary>
    /// Stands in for <c>PrefabRegistry.ResendEditLayersTo</c>, which lives in <c>Content</c> and is
    /// not compiled into the test assembly. One call per admitted re-answer.
    /// </summary>
    internal static Action<int>? TestResendEditLayers;
#endif

    /// <summary>
    /// <b>Every</b> side effect an accepted answer runs after the <c>Result</c> packet, in wire
    /// order, with one rate bound over the whole set for a user that was already <c>ok</c>
    /// (job FMAN22, R53 finding 3).
    ///
    /// <para>There are three of them and they are listed here because FMAN21 bounded one and the
    /// history claimed two:</para>
    /// <list type="number">
    /// <item><description>the <b>synced-config snapshot</b> — <c>ConfigSync.OnHandshakeOk</c>,
    /// which walks every Synced entry of every plugin this client declared and writes a packet
    /// per batch;</description></item>
    /// <item><description>the <b>signed catalog / offer</b> — <c>ModManifest.OnHandshakeOk</c>,
    /// which rebuilds the catalog and sends it to this user;</description></item>
    /// <item><description>the <b>prefab edit-layer resend</b> —
    /// <see cref="OnReAnswerFromOkUser"/>, which only exists for a re-answer and so runs only when
    /// <paramref name="wasAlreadyOk"/>.</description></item>
    /// </list>
    ///
    /// <para>The first two arrive together as <paramref name="sendPostOkPackets"/>: they need the
    /// world and the user entity, neither of which exists in the test build, and their wire order
    /// — config snapshot, then catalog — is a guarantee of its own that stays visible at the
    /// call site where <c>ModManifestPhase2Tests.Live_OnResponse_source_calls_config_sync_then_manifest</c>
    /// reads it.</para>
    ///
    /// <para><b>Why the bound is over the set and not over the cheapest member.</b> The resend is
    /// deduped per user per payload hash, so it was the one of the three that could absorb being
    /// driven at packet rate; the config snapshot is a full serialization of the server's synced
    /// config on the receive path — the game's own thread — and it was not bounded at all. A
    /// modified client looping <c>DeclareSelf</c> therefore bought an outbound amplifier and two
    /// log lines per packet for the cost of one small inbound packet. One admission per
    /// <see cref="HandshakeReAnswer.MinSecondsBetween"/> seconds per user index now covers all
    /// three.</para>
    ///
    /// <para><b>The first answer is untouched.</b> <paramref name="wasAlreadyOk"/> false skips the
    /// bound entirely and does not spend it: a join must never be made to wait for its config, and
    /// a first answer cannot be repeated without a status change. The bound is on a repeated
    /// <c>ok</c>, not on a change of state — a re-answer whose rows now fail never reaches this
    /// method at all, because <c>OnResponse</c> takes the mismatch branch and kicks, and a user
    /// whose status moved off <c>Ok</c> comes back through here as a first answer.</para>
    ///
    /// <para>Returns false only when the bound refused the whole set.</para>
    /// </summary>
    internal static bool RunPostOkEffects(int userIndex, bool wasAlreadyOk, Action? sendPostOkPackets)
    {
        if (wasAlreadyOk && !ReAnswers.TryAdmit(userIndex))
        {
            Plugin.Logger?.LogInfo(
                "Handshake: user " + userIndex.ToString(CultureInfo.InvariantCulture)
                + " re-answered again within " + HandshakeReAnswer.MinSecondsBetween.ToString(CultureInfo.InvariantCulture)
                + " s; the post-ok effects (config snapshot, catalog, edit-layer resend) were skipped");
            return false;
        }

        // Both callees already swallow their own exceptions; this guard is here so that a throw
        // from anywhere in the packet pair cannot cost the resend, which has one of its own for
        // the same reason.
        try
        {
            sendPostOkPackets?.Invoke();
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Handshake: a post-ok packet push threw for user "
                + userIndex.ToString(CultureInfo.InvariantCulture) + ": " + e.Message);
        }

        if (wasAlreadyOk) OnReAnswerFromOkUser(userIndex);
        return true;
    }

    /// <summary>
    /// The post-ok side effect that belongs to a <b>re-answer from a user that is already ok</b>
    /// and to nothing else (job FMAN21, R52 finding 3). The rate bound over it is
    /// <see cref="RunPostOkEffects"/>'s, which is this method's only caller (job FMAN22).
    ///
    /// <para>R51 finding 5 asked for a client whose Core swapped to get its prefab edit layers back.
    /// FMAN20's answer watched the server's own answer status for an edge into <c>ok</c>, which a
    /// client-side swap cannot produce: the server's row is already <c>ok</c> and
    /// <see cref="HandshakeAnswerWatch.Arm"/> will not move it. The observable that <em>does</em>
    /// happen is this one — the client sends a second <c>DeclareSelf</c> on the live connection —
    /// so the resend hangs off that instead of off a set membership that never changes.</para>
    ///
    /// <para>Everything it calls must be idempotent: it is allowed to run for a re-answer whose
    /// cause was something else entirely.</para>
    /// </summary>
    internal static void OnReAnswerFromOkUser(int userIndex)
    {
        try
        {
#if PROGENITOR_TESTS
            TestResendEditLayers?.Invoke(userIndex);
#else
            Progenitor.Content.PrefabRegistry.ResendEditLayersTo(userIndex);
#endif
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Handshake: the edit-layer resend threw for user "
                + userIndex.ToString(CultureInfo.InvariantCulture) + ": " + e.Message);
        }
#if !PROGENITOR_TESTS
        // Same cause, same recovery, its own failure: the client's announced inert-clone set is a
        // Core static too.
        try
        {
            Progenitor.Ai.InertClones.ResendTo(userIndex);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Handshake: the inert-clone resend threw for user "
                + userIndex.ToString(CultureInfo.InvariantCulture) + ": " + e.Message);
        }
#endif
    }

#if PROGENITOR_TESTS
    /// <summary>
    /// Job FMAN22. The live clock every per-user window on this type reads. Null is the old
    /// constant zero, which is what almost every test wants (two calls in one test are two calls
    /// at the same instant); a test that needs to cross a floor sets this and clears it in
    /// Dispose. <see cref="ResetTables"/> clears it too.
    /// </summary>
    internal static Func<double>? TestClock;
#endif

    private static double LiveClock()
    {
#if PROGENITOR_TESTS
        return TestClock?.Invoke() ?? 0;
#else
        return System.Diagnostics.Stopwatch.GetTimestamp()
            / (double)System.Diagnostics.Stopwatch.Frequency;
#endif
    }

    /// <summary>
    /// Per-user handshake answer status for the <c>users</c> bridge op:
    /// <c>none</c>, <c>asked</c>, <c>ok</c>, <c>mismatch</c>, <c>timeout</c>,
    /// or <c>unverified</c>.
    /// </summary>
    public static string UserAnswerStatus(int userIndex) => AnswerWatch.Token(userIndex);

    /// <summary>
    /// Set by Uninstall so the next Install re-asks connected users that have not
    /// answered. Fresh-ALC statics start false; that path re-asks after plugins
    /// re-Require. Same-ALC restore must consume this or pending deadlines stay dropped.
    /// </summary>
    internal static bool RearmDeadlinesOnNextInstall;

    /// <summary>
    /// True while Handshake holds a live <c>ConfigEntry</c> for the answer timeout.
    /// Uninstall nulls the entry so same-ALC Install rebinds (Hosted already does this).
    /// </summary>
    internal static bool AnswerTimeoutBound;

    /// <summary>
    /// Uninstall side-effects that tests can drive without game types: the next
    /// Install must rebind the timeout entry and re-arm unanswered deadlines.
    /// </summary>
    internal static void NotifyUninstalled()
    {
        RearmDeadlinesOnNextInstall = true;
        AnswerTimeoutBound = false;
    }

    /// <summary>True when this Install should re-ask connected users. Consumes the flag.</summary>
    internal static bool ConsumeRearmOnInstall()
    {
        bool rearm = RearmDeadlinesOnNextInstall;
        RearmDeadlinesOnNextInstall = false;
        return rearm;
    }

    /// <summary>
    /// Server: clients must have this mod at exactly this version (original undeclared rule).
    /// No-ops on invalid input or when the table is full of rows that already consume a slot.
    /// </summary>
    public static void Require(string guid, string version) =>
        TryRecord(Required, RequiredCapDropped, "Require", ModRecord.Undeclared(guid, version));

    /// <summary>
    /// Server: record this mod with an explicit compatibility level. Declared rows are also
    /// evaluated in the reverse direction when the client reports them.
    /// <see cref="NetworkCompatibilityLevel.ClientOnly"/> does not consume a request slot.
    /// </summary>
    public static void Require(string guid, string version, NetworkCompatibilityLevel level, NetworkVersionRule versionRule) =>
        TryRecord(Required, RequiredCapDropped, "Require", ModRecord.CreateDeclared(guid, version, level, versionRule));

    public static void Unrequire(string guid)
    {
        if (string.IsNullOrEmpty(guid)) return;
        lock (Required)
        {
            Required.Remove(guid);
            lock (RequiredCapDropped) RequiredCapDropped.Remove(guid);
            RetryDeclaredCapDropped(Required, RequiredCapDropped);
        }
        ReevaluatePendingDeadlines();
        NoteRequiredChanged();
    }

    /// <summary>Client: this mod is present at this version (undeclared). No-ops on invalid input or when the table is full.</summary>
    public static void Provide(string guid, string version) =>
        TryRecord(Provided, ProvidedCapDropped, "Provide", ModRecord.Undeclared(guid, version));

    /// <summary>
    /// Client: announce this mod with an explicit compatibility level so the server can evaluate
    /// the reverse direction.
    /// </summary>
    public static void Provide(string guid, string version, NetworkCompatibilityLevel level, NetworkVersionRule versionRule) =>
        TryRecord(Provided, ProvidedCapDropped, "Provide", ModRecord.CreateDeclared(guid, version, level, versionRule));

    public static void Unprovide(string guid)
    {
        if (string.IsNullOrEmpty(guid)) return;
        lock (Provided)
        {
            Provided.Remove(guid);
            ProvidedOwners.Remove(guid);
            lock (ProvidedCapDropped) ProvidedCapDropped.Remove(guid);
            RetryDeclaredCapDropped(Provided, ProvidedCapDropped);
        }
    }

    /// <summary>
    /// Client, per-server mod pause: which plugin (BepInPlugin GUID) recorded each
    /// <see cref="Provided"/> row. Written by <c>PluginHandshake.Provide</c>, which knows its
    /// context; a row provided through the static <see cref="Provide(string, string)"/> has no owner
    /// and so can never be paused. Guarded by the <see cref="Provided"/> lock.
    /// </summary>
    private static readonly Dictionary<string, string> ProvidedOwners = new(StringComparer.OrdinalIgnoreCase);

    /// <summary><see cref="Provide(string, string)"/> on behalf of plugin <paramref name="owner"/>.</summary>
    internal static void ProvideOwned(string owner, string guid, string version)
    {
        Provide(guid, version);
        NoteProvidedOwner(owner, guid);
    }

    /// <summary><see cref="Provide(string, string, NetworkCompatibilityLevel, NetworkVersionRule)"/> on behalf of plugin <paramref name="owner"/>.</summary>
    internal static void ProvideOwned(string owner, string guid, string version, NetworkCompatibilityLevel level, NetworkVersionRule versionRule)
    {
        Provide(guid, version, level, versionRule);
        NoteProvidedOwner(owner, guid);
    }

    private static void NoteProvidedOwner(string owner, string guid)
    {
        if (string.IsNullOrEmpty(owner) || string.IsNullOrEmpty(guid)) return;
        lock (Provided)
        {
            if (Provided.ContainsKey(guid)) ProvidedOwners[guid] = owner;
        }
    }

    /// <summary>Row guid → owning plugin guid, for every owned <see cref="Provided"/> row.</summary>
    internal static Dictionary<string, string> CopyProvidedOwners()
    {
        lock (Provided) return new Dictionary<string, string>(ProvidedOwners, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Validate guid/version, then insert. Updating an existing guid is allowed at capacity.
    /// <see cref="NetworkCompatibilityLevel.ClientOnly"/> does not count toward
    /// <see cref="MaxMods"/>. A new declared row at capacity evicts an undeclared slot-consuming
    /// row when one exists; otherwise the declared row is refused (logged, not recorded) and
    /// the first <see cref="MaxMods"/> stay. That refusal does not kick joiners: a 17th
    /// optional or required plugin is invisible to the handshake rather than a global ban.
    /// <see cref="Unrequire"/> / <see cref="Unprovide"/> retries a previously refused declared
    /// row when a slot frees.
    /// </summary>
    private static void TryRecord(
        Dictionary<string, ModRecord> table,
        Dictionary<string, ModRecord> pendingDropped,
        string op,
        ModRecord record)
    {
        if (Compare.IsReservedGuid(record.Guid))
        {
            Plugin.Logger?.LogError($"Handshake.{op}: '{record.Guid}' is reserved.");
            return;
        }
        if (!Compare.TryValidateEntry(record.Guid, record.Version, out var error))
        {
            Plugin.Logger?.LogError($"Handshake.{op}: {error}");
            return;
        }
        bool changed = false;
        lock (table)
        {
            bool exists = table.ContainsKey(record.Guid);
            bool consumes = record.Level != NetworkCompatibilityLevel.ClientOnly;
            if (!exists && consumes && SlotCount(table) >= MaxMods)
            {
                if (record.Declared && TryEvictUndeclared(table, out var evicted))
                {
                    Plugin.Logger?.LogWarning(
                        $"Handshake.{op}: already have {MaxMods} mods; dropping undeclared '{evicted.Guid}' to record declared '{record.Guid}'.");
                }
                else
                {
                    Plugin.Logger?.LogError(
                        $"Handshake.{op}: already have {MaxMods} mods; not recording '{record.Guid}'. " +
                        $"Keeping the first {MaxMods} declared slot-consuming plugin(s); this one is omitted from the handshake and does not ban joins.");
                    if (record.Declared)
                    {
                        lock (pendingDropped) pendingDropped[record.Guid] = record;
                    }
                    return;
                }
            }
            table[record.Guid] = record;
            changed = true;
            if (record.Declared)
            {
                lock (pendingDropped) pendingDropped.Remove(record.Guid);
            }
        }
        if (changed && ReferenceEquals(table, Required))
        {
            ReevaluatePendingDeadlines();
            NoteRequiredChanged();
        }
    }

    /// <summary>
    /// Wait-miss Unrequire/Require: refresh the kick reason on pending rows.
    /// Cancel-to-unverified happens at expire time so a plugin that Reloads before
    /// the due time still has a live deadline.
    /// </summary>
    internal static void ReevaluatePendingDeadlines()
    {
        var required = CopyRequired();
        AnswerWatch.ReevaluateRequired(
            RequiresAnswer(required),
            RequiredSummary(required));
    }

    /// <summary>
    /// The server refuses a client that never answers: a server plugin requires an answer
    /// (<see cref="Compare.ServerRequiresAnswer"/>), or CLIENTPOLICY1's client mod policy does (a required client mod,
    /// or Only approved with Refuse them).
    /// </summary>
    internal static bool RequiresAnswer(IReadOnlyList<ModRecord>? required) =>
#if !PROGENITOR_TESTS
        Progenitor.Content.DataPackRuntime.RequiresItemStats
        ||
#endif
        NoProgenitorKeepOut.Disconnects(KeepOut(), Compare.ServerRequiresAnswer(required), SafePolicy()?.RequiresAnswer ?? false);

    /// <summary>
    /// KEEPOUT1: the server's Players without Progenitor choice (<c>[Networking] RequireProgenitor</c>): true is Keep them
    /// out. The live binding sets <see cref="KeepOutProbe"/>; tests set their own. False when unbound.
    /// </summary>
    internal static Func<bool>? KeepOutProbe { get; set; }

    internal static bool KeepOut()
    {
        try { return KeepOutProbe?.Invoke() ?? NoProgenitorKeepOut.Default; }
        catch { return NoProgenitorKeepOut.Default; }
    }

    /// <summary>KEEPOUT1: the deadline armed for a silent player (<see cref="NoProgenitorKeepOut.Timeout"/>).</summary>
    internal static int AnswerTimeoutFor(int configuredSeconds) => NoProgenitorKeepOut.Timeout(KeepOut(), configuredSeconds);

    /// <summary>The no-answer kick's list: <see cref="Compare.FormatRequiredSummary"/> plus the client mod policy's part.</summary>
    internal static string RequiredSummary(IReadOnlyList<ModRecord>? required)
    {
        string summary = ClientModJudge.NoAnswerSummary(Compare.FormatRequiredSummary(required), SafePolicy());
        // KEEPOUT1: Keep them out with nothing else naming a mod: the kick says what is missing.
        bool nothing = summary.Trim().Length == 0 || string.Equals(summary.Trim(), "none", StringComparison.Ordinal);
        return nothing && KeepOut() ? NoProgenitorKeepOut.KeepOutSummary : summary;
    }

    /// <summary>
    /// CLIENTPOLICY1, client: the first steps of a handshake request, in the order that matters. The server's capability
    /// row is lifted out (nothing downstream may see it as a mod); a new join ends the previous one
    /// (<see cref="NotifyHandshakeEpochChanged"/>, which raises <see cref="ServerContextCleared"/> and so resets the
    /// report state); only THEN is the ask noted. Live 2026-10-05 the ask was noted before the epoch change, the reset
    /// erased it, and no report was ever sent (the server waited out 20 s and refused as no-report).
    /// </summary>
    internal static List<ModRecord> TakeClientPolicyAsk(List<ModRecord> askedRows, bool newJoin, out bool hadCapabilityRow)
    {
        var serverCaps = Compare.ReadCapabilities(askedRows);
#if !PROGENITOR_TESTS
        Progenitor.Content.ItemDefinitionDelivery.NoteServerCapabilities(serverCaps);
#endif
        hadCapabilityRow = askedRows.RemoveAll(r => string.Equals(r.Guid, CapabilityGuid, StringComparison.OrdinalIgnoreCase)) > 0;
        if (newJoin) NotifyHandshakeEpochChanged();
        ClientModReportClient.NoteServerRequest((serverCaps & Capability.ClientPolicy) != 0, (serverCaps & Capability.PolicyRecheck) != 0,
            ClientModReportClient.RuleOf(serverCaps), (serverCaps & Capability.CountdownChat) != 0);
        // Mod compatibility C1/C2: whether this server reads feature tables and versioned messages (after the epoch change).
        ModCompatCaps.NoteServerCaps(serverCaps);
        // SUPPORT1: whether Get help may ask this server for its logs.
        Progenitor.Support.ServerLogsCaps.NoteServerSendsLogs((serverCaps & Capability.ServerLogs) != 0);
        return askedRows;
    }

    /// <summary>The admins' client mod policy on a server (null on a client, or when it cannot be read).</summary>
    internal static ClientPolicyConfig? SafePolicy()
    {
        try
        {
            if (!ModManifest.IsServerProcessSide()) return null;
            return JoinAdminServer.CurrentPolicy;
        }
        catch { return null; }
    }

    private static int SlotCount(Dictionary<string, ModRecord> table)
    {
        int n = 0;
        foreach (var rec in table.Values)
        {
            if (rec.Level == NetworkCompatibilityLevel.ClientOnly) continue;
            n++;
        }
        return n;
    }

    private static bool TryEvictUndeclared(Dictionary<string, ModRecord> table, out ModRecord evicted)
    {
        string? key = null;
        evicted = default;
        foreach (var kv in table)
        {
            if (kv.Value.Declared) continue;
            if (kv.Value.Level == NetworkCompatibilityLevel.ClientOnly) continue;
            key = kv.Key;
            evicted = kv.Value;
            break;
        }
        if (key == null) return false;
        table.Remove(key);
        return true;
    }

    /// <summary>
    /// Caller holds <paramref name="table"/>. When a slot-consuming row was removed, record a
    /// previously refused declared plugin instead of leaving it dropped forever.
    /// </summary>
    private static void RetryDeclaredCapDropped(
        Dictionary<string, ModRecord> table,
        Dictionary<string, ModRecord> pendingDropped)
    {
        lock (pendingDropped)
        {
            if (pendingDropped.Count == 0) return;
            var pending = new List<ModRecord>(pendingDropped.Values);
            for (int i = 0; i < pending.Count; i++)
            {
                if (SlotCount(table) >= MaxMods) break;
                var rec = pending[i];
                if (string.IsNullOrEmpty(rec.Guid)) continue;
                if (rec.Level == NetworkCompatibilityLevel.ClientOnly)
                {
                    pendingDropped.Remove(rec.Guid);
                    continue;
                }
                if (table.ContainsKey(rec.Guid))
                {
                    pendingDropped.Remove(rec.Guid);
                    continue;
                }
                table[rec.Guid] = rec;
                pendingDropped.Remove(rec.Guid);
                Plugin.Logger?.LogWarning($"Handshake: recorded previously dropped '{rec.Guid}' after a slot freed.");
            }
        }
    }

    /// <summary>
    /// Mod compatibility C3: the required rows as this server's handshake request carries them, with Version matching and
    /// the admins' per-mod rules applied to the rule bytes (<see cref="VersionMatchJudge.RequestRow"/>).
    /// </summary>
    internal static List<ModRecord> RequestRowsNow()
    {
        var settings = VersionMatching.Current();
        lock (Required) return VersionMatchJudge.RequestRows(Required.Values, settings);
    }

    /// <summary>
    /// The request's rows packed for the wire, with their <paramref name="stamp"/>. Every request goes through here; the
    /// caller notes the stamp for its user once the send succeeded (RESEND1: <see cref="RequirementWatch"/> compares a
    /// later change of the required table against what each user was actually sent).
    /// </summary>
    internal static PackedList PackRequestNow(out string stamp)
    {
        var rows = RequestRowsNow();
        stamp = HandshakeRequirementWatch.Stamp(rows);
        return Compare.PackRequest(rows);
    }

    /// <summary>RESEND1: the stamp of the request as it would be sent now.</summary>
    internal static string RequestStampNow() => HandshakeRequirementWatch.Stamp(RequestRowsNow());

#if PROGENITOR_TESTS
    /// <summary>Tests: raised where the live server posts the settled compare.</summary>
    internal static Action? TestRequirementCompareScheduled;
#endif

    internal static ModRecord[] CopyRequired()
    {
        lock (Required)
        {
            var a = new ModRecord[Required.Count];
            Required.Values.CopyTo(a, 0);
            return a;
        }
    }

    internal static ModRecord[] CopyProvided()
    {
        lock (Provided)
        {
            var a = new ModRecord[Provided.Count];
            Provided.Values.CopyTo(a, 0);
            return a;
        }
    }

    /// <summary>
    /// The rows the server asked this client about on the connection it is on, exactly as
    /// received (job FMAN13, R44 finding 9).
    ///
    /// <para>This is the <em>only</em> client-side evidence of what a server actually enforces.
    /// The signed catalog's <c>level</c> is not: an offer-only row's level is a field
    /// <c>ModManifestJson.BuildBody</c> fills in when the operator left it out, a server plugin
    /// carrying <c>[NetworkCompatibility(ServerMustHave)]</c> without <c>DeclareSelf</c> is in the
    /// catalog and not in <c>Required</c>, and an operator override may say anything at all. The
    /// consent panel used to read the catalog and tell the player they would be disconnected; it
    /// now quotes this table or says nothing.</para>
    ///
    /// <para><b>Two things empty it, and both are load-bearing</b> (job FMAN14, R45 finding 36).
    /// <see cref="RecordServerRequest"/> <em>replaces</em> the table on every request, so the rows
    /// a client carries are always the ones the server it is talking to just sent; that is what
    /// actually prevents a cross-server claim, and it is the one that survives a client which
    /// never leaves a world cleanly. the join-end handlers then clear it when the
    /// join ends, so a client sitting at the main menu after a disconnect holds no evidence at
    /// all. FMAN13's comment credited only the second and nothing called it outside
    /// <c>PROGENITOR_TESTS</c> — a documented API with no caller, in the commit whose thesis is
    /// that a sentence may only state what the code does.</para>
    /// </summary>
    private static readonly List<ModRecord> ServerAsked = new(); // client

    /// <summary>
    /// Replace the recorded request wholesale. Called from <c>OnRequest</c> for every handshake
    /// request received, so the table never mixes two servers' rows even if a join ended without
    /// <see cref="ForgetServerRequest"/> running.
    /// </summary>
    /// <para>COUNTERPART-SAMPLES review: <paramref name="truncated"/> is whether the request filled
    /// every wire slot (<c>count &gt;= MaxMods</c> before the metadata row is stripped). A full
    /// request may have left a mod out, so it proves no absence (<see cref="ServerRequestTruncated"/>);
    /// the kept row count alone cannot say so, because a stripped metadata row makes a full request
    /// read 15.</para>
    internal static void RecordServerRequest(IReadOnlyList<ModRecord>? rows, bool truncated = false)
    {
        lock (ServerAsked)
        {
            ServerAsked.Clear();
            _serverAskedTruncated = truncated;
            _serverRequestSeen = true;
            if (rows != null)
                for (int i = 0; i < rows.Count; i++)
                {
                    if (string.IsNullOrEmpty(rows[i].Guid)) continue;
                    ServerAsked.Add(rows[i]);
                }
        }
        NotifyServerPartEvidence("request");
    }

    private static bool _serverAskedTruncated;

    // A6 (live 2026-10-07): a request was received on this connection, even one that asked about no mod (a server whose
    // only requirement is its client mod policy sends just its capability row). Cleared with the request.
    private static bool _serverRequestSeen;

    /// <summary>A6: this connection received the server's handshake request (its rows may be empty).</summary>
    internal static bool ServerRequestSeen
    {
        get { lock (ServerAsked) return _serverRequestSeen; }
    }

    /// <summary>A6: a re-answer has a server waiting for it: a request reached this connection, rows or none.</summary>
    internal static bool HasRequestToAnswer
    {
        get { lock (ServerAsked) return _serverRequestSeen || ServerAsked.Count > 0; }
    }

    /// <summary>The recorded request filled every wire slot and may have left mods out (see <see cref="RecordServerRequest"/>).</summary>
    internal static bool ServerRequestTruncated
    {
        get { lock (ServerAsked) return _serverAskedTruncated; }
    }

    /// <summary>The join ended, or Core is going down: this server's request is no longer evidence.</summary>
    internal static void ForgetServerRequest()
    {
        lock (ServerAsked)
        {
            ServerAsked.Clear();
            _serverAskedTruncated = false;
            _serverRequestSeen = false;
        }
        NotifyServerPartEvidence("forgotten");
    }

    /// <summary>
    /// COUNTERPART-SAMPLES: the evidence a mod page's server-part cell and needs-server-part rows read
    /// changed (a request, the join's result, or the end of the join); an open page rewrites both in
    /// place. Never throws.
    /// </summary>
    internal static void NotifyServerPartEvidence(string reason)
    {
        try { Progenitor.Ui.ModsMenu.NoteServerPartChanged(reason); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Handshake: server-part refresh threw: " + e.Message); }
    }

    internal static ModRecord[] CopyServerAsked()
    {
        lock (ServerAsked) return ServerAsked.ToArray();
    }

    /// <summary>
    /// INPLACE1 review (MEDIUM): run, on this client, the check the server runs on a re-answer (<c>OnResponse</c>: the
    /// reply this client would pack now, its metadata applied, judged by <see cref="Compare.Evaluate(IReadOnlyList{ModRecord}, IReadOnlyList{ModRecord}, out List{string})"/>
    /// against the rows the server asked). An already-ok player whose re-answer fails that check is kicked, so a fix
    /// finished in the world answers again only when this returns no problems. <paramref name="checkable"/> is false when
    /// the request is not known in full (none recorded, or it filled every slot): nothing can be promised then.
    /// </summary>
    internal static List<string> PreflightReAnswer(out bool checkable)
    {
        checkable = false;
        ModRecord[] asked;
        bool truncated, seen;
        lock (ServerAsked)
        {
            asked = ServerAsked.ToArray();
            truncated = _serverAskedTruncated;
            seen = _serverRequestSeen;
        }

        var server = new List<ModRecord>(asked.Length);
        foreach (var r in asked)
            if (!string.Equals(r.Guid, CapabilityGuid, StringComparison.OrdinalIgnoreCase)) server.Add(r);
        if (!seen) return new List<string> { "this client has no record of the server's request" };
        if (truncated) return new List<string> { "the server's request filled every slot, so it may require more than this client saw" };

        PackedList packed;
        lock (Provided)
        {
            var provided = new List<ModRecord>(Provided.Count);
            foreach (var rec in Provided.Values) provided.Add(rec);
            packed = Compare.PackReply(server, provided);
        }

        bool overflow = false;
        var answered = new List<ModRecord>(packed.Rows.Count);
        foreach (var rec in packed.Rows)
        {
            if (string.Equals(rec.Guid, OverflowGuid, StringComparison.OrdinalIgnoreCase)) { overflow = true; continue; }
            if (string.Equals(rec.Guid, CapabilityGuid, StringComparison.OrdinalIgnoreCase)) continue;
            answered.Add(rec);
        }

        // Mod compatibility C3: the server judges a re-answer with its Version matching and ranges; so does this check.
        var serverRows = Compare.ApplyMetaRecord(server);
        var answerRows = Compare.ApplyMetaRecord(answered);
        var problems = Compare.Evaluate(serverRows, answerRows, out _, ClientJudge(serverRows, answerRows));
        if (overflow || packed.DeclaredDropped) problems.Insert(0, "this client's answer would not fit the reply");
        checkable = true;
        return problems;
    }

    /// <summary>
    /// The reason this server's own join check would give for <paramref name="modId"/>, or null
    /// when that check does not reject this client's copy — which includes the common case of a
    /// server that never asked about the id at all (job FMAN13, R44 finding 9). Never throws: a
    /// panel that cannot establish a consequence must say nothing, not fail to open.
    /// </summary>
    internal static string? EnforcedKickReasonFor(string? modId)
    {
        try
        {
            var asked = CopyServerAsked();
            var provided = CopyProvided();
            return Compare.EnforcedKickReason(modId, asked, provided, ClientJudge(asked, provided));
        }
        catch { return null; }
    }

    /// <summary>
    /// Mod compatibility C3 (client): the server's judgment rebuilt from the joined server's last verified catalog
    /// (<see cref="VersionMatching.ClientContextFor"/>), or null when it carries no Version matching.
    /// </summary>
    internal static VersionJudgeContext? ClientJudge(IReadOnlyList<ModRecord> asked, IReadOnlyList<ModRecord> provided)
    {
        try
        {
            return VersionMatching.ClientContextFor(ModManifest.LastEnvelope?.Body, asked, VersionMatching.OwnRangeOf);
        }
        catch { return null; }
    }

    internal static string[] CopyDeclaredCapDropped()
    {
        lock (RequiredCapDropped)
        {
            var a = new string[RequiredCapDropped.Count];
            RequiredCapDropped.Keys.CopyTo(a, 0);
            return a;
        }
    }

    /// <summary>
    /// How a failing row of this server's own is fixed during the join (2026-10-05): the client's declared local fix
    /// first, else a Thunderstore package the signed catalog names for it that serves the version this server's row
    /// needs (<see cref="ServerPackages"/>, the server's read of the Thunderstore index; not read yet is no fix).
    /// </summary>
    internal static Func<Compare.EnforcedFailure, MissFix> ServerFixFor(
        IReadOnlyList<ModRecord> server, HashSet<string> localFixes) => f =>
    {
        string version = "";
        foreach (var row in server)
            if (string.Equals(row.Guid, f.Id, StringComparison.OrdinalIgnoreCase)) { version = row.Version ?? ""; break; }
        bool local = localFixes.Contains(f.Id);
        string? package = ModManifest.CatalogPackageOf(f.Id);
        var packages = ServerPackages;
        return InstallWindowFixes.ForHandshakeRow(f.Id, local, package, version, f.VersionRule,
            packages == null ? null : packages.Availability);
    };

    /// <summary>
    /// The server's read of the Thunderstore index for the packages its catalog and its client mod policy name (set by
    /// <c>ServerPackageAvailability.InstallLive</c> on the server; tests set their own). Null: no package is known to be
    /// served, so only the client's local fixes defer a kick.
    /// </summary>
    internal static ServerPackageAvailability? ServerPackages { get; set; }

    /// <summary>
    /// Client: whether this client can fix asked row <c>guid</c> by itself during the join (a kept copy that loads now, a
    /// turned-off copy that hot-loads). Set by the join screens' live half; null declares nothing.
    /// </summary>
    internal static Func<string, bool>? LocalFixProbe { get; set; }

#if PROGENITOR_TESTS
    internal static void ResetTables()
    {
        lock (Required) Required.Clear();
        lock (Provided) { Provided.Clear(); ProvidedOwners.Clear(); }
        lock (RequiredCapDropped) RequiredCapDropped.Clear();
        lock (ProvidedCapDropped) ProvidedCapDropped.Clear();
        ForgetServerRequest();
        ClientEpoch = 0;
        ClearedEpoch = 0;
        AcceptedEpoch = 0;
        LastClientResult = null;
        AnswerWatch.Clear();
        InstallGrace.Clear();
        ReAnswers.Clear();
        RefreshRequests.Clear();
        lock (SoftReasks) SoftReasks.Clear();
        RequirementWatch.Reset();
        TestRequirementCompareScheduled = null;
        SessionMemory.Clear();
        _reaskScope = false;
        TestClock = null;
        TestResendEditLayers = null;
        ClientInstallWindow.ResetForTests();
        RearmDeadlinesOnNextInstall = false;
        AnswerTimeoutBound = false;
        TestOkHookOrder.Clear();
    }

    /// <summary>
    /// Fake handshake-ok path used by tests. Same order as live <c>OnResponse</c> after
    /// an accepted answer: handshake, then config sync, then the signed catalog.
    /// </summary>
    internal static readonly List<string> TestOkHookOrder = new();

    /// <summary>
    /// Job FMAN22: this drives the real composition (<see cref="RunPostOkEffects"/>) rather than
    /// its own copy of the order, so the ordering test cannot pass while the live path reorders.
    /// A first answer, so the rate bound is not consulted and not spent.
    /// </summary>
    internal static void NotifyHandshakeOk(int userIndex)
    {
        LastClientResult = "ok";
        TestOkHookOrder.Clear();
        TestOkHookOrder.Add("handshake");
        RunPostOkEffects(userIndex, wasAlreadyOk: false, () =>
        {
            ConfigSync.NotifyHandshakeOkForTests();
            TestOkHookOrder.Add("config");
            ModManifest.OnHandshakeOk(userIndex);
        });
        TestOkHookOrder.Add("manifest");
    }
#endif

#if !PROGENITOR_TESTS
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public unsafe struct ModList
    {
        public int Count;
        public fixed byte Data[MaxMods * EntryBytes];

        public void Add(string guid, string version) => Add(ModRecord.Undeclared(guid, version));

        public void Add(in ModRecord record)
        {
            if ((uint)Count >= (uint)MaxMods) return;
            fixed (byte* d = Data)
                WriteEntry(d + Count * EntryBytes, record);
            Count++;
        }

        /// <summary>
        /// Read slot <paramref name="i"/>. Out-of-range indices return empty strings so a
        /// malicious Count cannot walk off <see cref="Data"/>.
        /// </summary>
        public (string guid, string version) Get(int i)
        {
            if ((uint)i >= (uint)MaxMods) return ("", "");
            fixed (byte* d = Data)
                return (ReadAscii(d + i * EntryBytes, GuidBytes), ReadAscii(d + i * EntryBytes + GuidBytes, VersionBytes));
        }

        /// <summary>
        /// Read slot <paramref name="i"/> including packed compatibility metadata. Out-of-range
        /// indices return a default (empty guid) record.
        /// </summary>
        public ModRecord GetRecord(int i)
        {
            if ((uint)i >= (uint)MaxMods) return default;
            fixed (byte* d = Data)
            {
                byte* entry = d + i * EntryBytes;
                string guid = ReadAscii(entry, GuidBytes);
                string version = ReadAscii(entry + GuidBytes, VersionBytes);
                UnpackMeta(entry + GuidBytes, out bool declared, out var level, out var rule);
                return new ModRecord(guid, version, level, rule, declared);
            }
        }

        private static void WriteEntry(byte* dst, in ModRecord record)
        {
            WriteAscii(dst, GuidBytes, record.Guid);
            WriteAscii(dst + GuidBytes, VersionBytes, record.Version);
            if (!record.Declared) return;
            if (record.Version != null && record.Version.Length > InSlotVersionChars) return;
            byte* versionSlot = dst + GuidBytes;
            if (versionSlot[InSlotVersionChars] != 0) return;
            versionSlot[MetaFlagOffset] = (byte)(MetaPresentFlag | (byte)record.Level);
            versionSlot[MetaRuleOffset] = (byte)record.VersionRule;
        }

        private static void UnpackMeta(byte* versionSlot, out bool declared, out NetworkCompatibilityLevel level, out NetworkVersionRule rule)
        {
            byte flag = versionSlot[MetaFlagOffset];
            if ((flag & MetaPresentFlag) == 0)
            {
                declared = false;
                level = NetworkCompatibilityLevel.ServerMustHave;
                rule = NetworkVersionRule.Exact;
                return;
            }
            declared = true;
            level = (NetworkCompatibilityLevel)(flag & unchecked((byte)~MetaPresentFlag));
            rule = (NetworkVersionRule)versionSlot[MetaRuleOffset];
        }

        private static void WriteAscii(byte* dst, int cap, string s)
        {
            s ??= "";
            int n = s.Length;
            if (n > cap - 1) n = cap - 1;
            for (int i = 0; i < cap; i++)
            {
                if (i < n)
                {
                    char c = s[i];
                    dst[i] = c <= 0x7F ? (byte)c : (byte)'?';
                }
                else dst[i] = 0;
            }
        }

        private static string ReadAscii(byte* src, int cap)
        {
            int len = 0;
            while (len < cap && src[len] != 0) len++;
            return Encoding.ASCII.GetString(src, len);
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public unsafe struct Result
    {
        public byte Ok;
        public fixed byte Message[480];

        public string Text
        {
            get { fixed (byte* m = Message) { int len = 0; while (len < 480 && m[len] != 0) len++; return Encoding.ASCII.GetString(m, len); } }
        }

        /// <summary>
        /// A <b>deferred</b> refusal (job FMAN18). Same packet, same size, same event id; only
        /// <see cref="Ok"/> differs, and it differs into a value today's client already handles
        /// as "not ok" — see <see cref="ResultDeferred"/> for why that is the safe extension on
        /// this transport.
        /// </summary>
        public static Result CreateDeferred(string message) => Create(ResultDeferred, message);

        public static Result Create(bool ok, string message) => Create(ok ? (byte)1 : (byte)0, message);

        private static Result Create(byte ok, string message)
        {
            var r = new Result { Ok = ok };
            // Job FMAN20: one encoder, shared with the tests. See Compare.WireMessageBytes.
            var bytes = Compare.WireMessageBytes(message);
            for (int i = 0; i < bytes.Length; i++) r.Message[i] = bytes[i];
            return r;
        }
    }

    private static IDisposable? _connectedSub;
    private static IDisposable? _disconnectedSub;
    private static bool _installed;
    private static readonly HashSet<int> MalformedSenders = new();

    private static readonly object DeadlineOwner = new();
    private static ConfigEntry<int>? _answerTimeoutSeconds;

    /// <summary>KEEPOUT1: <c>[Networking] RequireProgenitor</c> (<see cref="NoProgenitorKeepOut"/>).</summary>
    private static ConfigEntry<bool>? _requireProgenitor;
    private static ConfigEntry<int>? _installGraceSeconds;
    private static ConfigEntry<int>? _noProgenitorGraceSeconds;

    /// <summary>
    /// Client: <c>[Networking] NoProgenitorGraceSeconds</c>, how long after the join reaches
    /// <c>ClientState.Running</c> without a Progenitor handshake request before the server is treated
    /// as one without Progenitor and this client's ServerMustHave mods are paused.
    /// </summary>
    private static void BindNoProgenitorGrace()
    {
        _noProgenitorGraceSeconds = null;
        ServerModPause.GraceReader = null;
        SharedConfig.BindWhenAvailable("[Networking] NoProgenitorGraceSeconds", file =>
        {
            try
            {
                _noProgenitorGraceSeconds = ConfigHelper.Bind(
                    file,
                    "Networking",
                    "NoProgenitorGraceSeconds",
                    ServerModPause.DefaultNoProgenitorGraceSeconds,
                    "Client. Seconds after a join reaches the game world with no Progenitor handshake request before the server is treated as one without Progenitor (BepInEx-only or vanilla); this client's hot-loaded mods that declared ServerMustHave are then paused until you leave. 0 disables the detection. Servers on this machine (a private game) are never treated this way.");
                var entry = _noProgenitorGraceSeconds;
                if (entry != null) ServerModPause.GraceReader = () => entry.Value;
            }
            catch (Exception e)
            {
                Plugin.Logger?.LogError($"Handshake: binding Networking.NoProgenitorGraceSeconds threw: {e}");
            }
        });
    }

    internal static unsafe void Install()
    {
        if (_installed) return;
        if (HarmonySlotHandoff.ShouldSkipModule(HarmonySlotHandoff.NetworkingModule))
        {
            Plugin.Logger?.LogInfo(HarmonySlotHandoff.SkipModuleLog(HarmonySlotHandoff.NetworkingModule));
            return;
        }
        ReceiveLimits.Bind();
        if (Plugin.IsServerProcess)
        {
            BindAnswerTimeout();
            _connectedSub = EventBus.On<PlayerConnected>(OnPlayerConnected, priority: 1000);
            _disconnectedSub = EventBus.On<PlayerDisconnected>(OnPlayerDisconnected, priority: 1000);
            CustomEvents.OnReceiveFromClient(ResponseEventId, OnResponse);
            CustomEvents.OnReceiveFromClient(RefreshEventId, OnRefreshRequest);
            // CLIENTPOLICY1: the client mod report.
            CustomEvents.OnReceiveFromClient(ClientModReportWire.EventId, OnClientModReport);
        }
        else
        {
            CustomEvents.OnReceive(RequestEventId, OnRequest);
            CustomEvents.OnReceive(ResultEventId, OnResult);
            BindNoProgenitorGrace();
            try { ClientModReportClient.Install(); }
            catch (Exception e) { Plugin.Logger?.LogWarning($"Handshake: the client mod report could not be set up: {e.Message}"); }
        }
        _installed = true;
        try { ModManifest.Install(); }
        catch (Exception e) { Plugin.Logger?.LogError($"Handshake: ModManifest.Install: {e}"); }
        if (Plugin.IsServerProcess) MarkCarriedOverInstallGrace();
        if (Plugin.IsServerProcess && ConsumeRearmOnInstall())
            RearmConnectedUnanswered();
        if (!Plugin.IsServerProcess) ReDeclareAfterCoreInstall();
    }

    /// <summary>
    /// Job FMAN21 (R52 finding 3), client side. The server cannot see a client-side Core swap: its
    /// answer row for this user stays <c>ok</c>, so nothing on its side is an edge. What the client
    /// can do is say so — one extra <c>DeclareSelf</c> on the connection it is already on, which the
    /// server treats as a re-answer and answers with a resend of the state this generation lost
    /// (<see cref="OnReAnswerFromOkUser"/>).
    ///
    /// <para><b>Silent when nothing was ever asked.</b> Unlike <see cref="ReDeclareSelf"/>, which is
    /// called by the install window and whose empty case is a real surprise, this runs at every
    /// client <c>Install</c> — main menu included — and the ordinary case there is that no server
    /// has asked this client anything.</para>
    ///
    /// <para>A collectible Core swap starts with an empty <see cref="ServerAsked"/> list.
    /// Runtime requests a fresh server handshake through <see cref="RefreshEventId"/> after
    /// consumer plugins have reloaded; that path restores the actual server request as well as
    /// synchronized state. This method covers only a same-generation reinstall.</para>
    /// </summary>
    private static void ReDeclareAfterCoreInstall()
    {
        try
        {
            var asked = CopyServerAsked();
            if (asked.Length == 0) return;
            bool sent = SendReply(new List<ModRecord>(asked), "re-answering after a Core install");
            Plugin.Logger?.LogInfo(
                "Handshake: re-answered after a Core install with " + asked.Length.ToString(CultureInfo.InvariantCulture)
                + " asked row(s); sent=" + (sent ? "true" : "false"));
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Handshake: the post-install re-answer threw: " + e.Message);
        }
    }

    /// <summary>
    /// Job FMAN20 (R50 finding 11). Spend the install-window budget of every user that was
    /// <em>already connected</em> when this Core generation installed. Their join began under a
    /// generation whose <see cref="InstallGrace"/> table is gone, so without this a Core swap
    /// would hand each of them a second sixty-second window — and "one grace per join" would be
    /// false exactly where this project reloads Core with players connected.
    ///
    /// <para>Runs before <see cref="RearmConnectedUnanswered"/>, so the re-ask it triggers already
    /// sees the mark. On a first Install at server boot there are no connected users and this does
    /// nothing.</para>
    /// </summary>
    private static void MarkCarriedOverInstallGrace()
    {
        try
        {
            var world = Worlds.Server;
            if (world == null || !world.IsCreated) return;
            var em = world.EntityManager;
            var entities = Query.Entities(
                em,
                includeDisabled: true,
                includeSystems: false,
                ComponentIndex.Of<User>(ComponentType.AccessMode.ReadOnly));
            int spent = 0;
            for (int i = 0; i < entities.Count; i++)
            {
                try
                {
                    var user = em.GetComponentData<User>(entities[i]);
                    if (!user.IsConnected) continue;
                    if (InstallGrace.NoteCarriedOverJoin(user.Index)) spent++;
                }
                catch (Exception e)
                {
                    Plugin.Logger?.LogWarning($"Handshake: carried-over install grace: {e.Message}");
                }
            }

            if (spent > 0)
            {
                Plugin.Logger?.LogInfo(
                    $"Handshake: {spent} connected user(s) carried a join into this Core generation; "
                    + "their install window counts as already spent.");
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning($"Handshake.MarkCarriedOverInstallGrace: {e.Message}");
        }
    }

    /// <summary>
    /// What Handshake and CustomEvents registered that can outlive a collectible Core
    /// generation (EventBus, receive-slot delegates, native-detour fallback).
    /// </summary>
    internal static string DescribePins()
    {
        var holds = new List<string>();
        try
        {
            if (_installed) holds.Add("installed");
            if (_connectedSub != null) holds.Add("EventBus.PlayerConnected");
            if (_disconnectedSub != null) holds.Add("EventBus.PlayerDisconnected");
            int pending = AnswerWatch.PendingCount;
            if (pending > 0) holds.Add("answerDeadlines=" + pending.ToString(CultureInfo.InvariantCulture));
            CustomEvents.CollectPinHolds(holds);
        }
        catch (Exception e)
        {
            holds.Add("threw:" + e.Message);
        }
        return HarmonySlotHandoff.FormatModulePins(HarmonySlotHandoff.NetworkingModule, holds);
    }

    internal static void Uninstall()
    {
        if (!_installed) return;
        _connectedSub?.Dispose();
        _connectedSub = null;
        _disconnectedSub?.Dispose();
        _disconnectedSub = null;
        try { Dev.MainThread.DropOwnedBy(DeadlineOwner, typeof(Handshake).Assembly); }
        catch (Exception e) { Plugin.Logger?.LogWarning($"Handshake.Uninstall DropOwnedBy: {e.Message}"); }
        AnswerWatch.Clear();
        InstallGrace.Clear();
        // RESEND1: a compare posted under DeadlineOwner was just dropped; the next generation starts from its own asks.
        RequirementWatch.Reset();
        CustomEvents.RemoveClientHandler(ResponseEventId);
        CustomEvents.RemoveClientHandler(ClientModReportWire.EventId);
        ClientPolicyUninstall();
        NoAnswerUninstall();
        try { ClientModReportClient.Uninstall(); }
        catch (Exception e) { Plugin.Logger?.LogWarning($"Handshake: client mod report teardown: {e.Message}"); }
        UninstallRefresh();
        CustomEvents.RemoveHandler(RequestEventId);
        CustomEvents.RemoveHandler(ResultEventId);
        lock (MalformedSenders) MalformedSenders.Clear();
        // Job FMAN19: the client window is per join, so Core going down ends it like any other.
        try { ClientInstallWindow.Reset(); }
        catch (Exception e) { Plugin.Logger?.LogWarning($"Handshake: install window reset: {e.Message}"); }
        _answerTimeoutSeconds = null;
        _installGraceSeconds = null;
        try { ServerModPause.Uninstall(); }
        catch (Exception e) { Plugin.Logger?.LogWarning($"Handshake: ServerModPause.Uninstall: {e.Message}"); }
        ServerModPause.GraceReader = null;
        _noProgenitorGraceSeconds = null;
        try { ModManifest.Uninstall(); }
        catch (Exception e) { Plugin.Logger?.LogWarning($"Handshake: ModManifest.Uninstall: {e.Message}"); }
        NotifyUninstalled();
        _installed = false;
    }

    /// <summary>
    /// Same-ALC Install after Uninstall: re-ask every currently connected user
    /// (including disabled) the same way a join does. One log line per re-armed
    /// user. Fresh-ALC statics never set the flag; Runtime.ReaskConnectedHandshake
    /// covers that path after plugins re-Require.
    /// </summary>
    private static void RearmConnectedUnanswered()
    {
        try
        {
            var world = Worlds.Server;
            if (world == null || !world.IsCreated)
            {
                Plugin.Logger?.LogInfo("Handshake: no Server world; pending deadlines were not re-armed after Install.");
                return;
            }
            var em = world.EntityManager;
            var entities = Query.Entities(
                em,
                includeDisabled: true,
                includeSystems: false,
                ComponentIndex.Of<User>(ComponentType.AccessMode.ReadOnly));
            for (int i = 0; i < entities.Count; i++)
            {
                try
                {
                    var entity = entities[i];
                    var user = em.GetComponentData<User>(entity);
                    if (!user.IsConnected) continue;
                    if (AnswerWatch.HasAnswered(user.Index)) continue;
                    using (BeginReask())
                        AskUser(new PlayerConnected(world, entity, user), alreadyRetried: false, rearmLog: true);
                }
                catch (Exception e)
                {
                    Plugin.Logger?.LogWarning($"Handshake: re-arm of a connected user threw: {e.Message}");
                }
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"Handshake.RearmConnectedUnanswered: {e}");
        }
    }

    /// <summary>CLIENTPOLICY2: the player's name for the log, or <c>a new player (&lt;steamid&gt;)</c> before character creation.</summary>
    private static string LabelOf(PlayerConnected ev)
    {
        try { return Compare.PlayerLabel(ev.CharacterName, ev.User.PlatformId); }
        catch { return "a player"; }
    }

    private static void OnPlayerConnected(PlayerConnected ev)
    {
        // REASK1: a join is always asked in full; what an earlier connection on this user index answered is forgotten.
        if (!_reaskScope)
        {
            try { SessionMemory.Forget(ev.UserIndex); }
            catch { /* the slot is best effort */ }
        }

        try { AskUser(ev, alreadyRetried: false, rearmLog: false); }
        catch (Exception e) { Plugin.Logger?.LogError($"Handshake.OnPlayerConnected: {e}"); }
    }

    private static void AskUser(PlayerConnected ev, bool alreadyRetried, bool rearmLog)
    {
        if (Compare.ShouldSkipAsk(AnswerWatch.HasAnswered(ev.UserIndex)))
        {
            Plugin.Logger?.LogInfo($"Handshake: {LabelOf(ev)} already answered; skipping ask");
            return;
        }

        // REASK1: a re-ask of a user who answered this session never disconnects them (a soft re-ask).
        bool remembered;
        try { remembered = SessionMemory.Contains(ev.UserIndex, ev.User.PlatformId); }
        catch { remembered = false; }
        if (HandshakeReask.KindFor(_reaskScope, remembered) == HandshakeAskKind.SoftReask)
        {
            SoftReaskUser(ev, 0);
            return;
        }

        PackedList packed = PackRequestNow(out string requestStamp);
        // CLIENTPOLICY1: with a client mod policy the request carries the capability row (last, after any metadata
        // row) asking for the client's mod report. No free slot: no ask, and the join is judged as unchecked.
        var requestRows = new List<ModRecord>(packed.Rows);
        bool policyAsk = PolicyAsksForReport(requestRows, ev.UserIndex, LabelOf(ev));
        var list = new ModList();
        for (int i = 0; i < requestRows.Count; i++)
            list.Add(requestRows[i]);
        for (int i = 0; i < packed.Dropped.Count; i++)
        {
            Plugin.Logger?.LogWarning(
                $"Handshake.OnPlayerConnected: already have {MaxMods} rows; not sending '{packed.Dropped[i].Guid}'.");
        }

        if (ShouldSkipAnswerDeadline(ev))
        {
            ArmAnswerDeadline(ev);
            return;
        }

        bool sent = CustomEvents.SendToUser(ev.World, ev.UserEntity, RequestEventId, list);
        if (sent) RequirementWatch.NoteSent(ev.UserIndex, requestStamp);
        Plugin.Logger?.LogInfo($"Handshake: asked {LabelOf(ev)} about {Compare.ClampCount(list.Count)} mod(s): sent={sent}"
            + (policyAsk ? " (and for its client mod report)" : ""));
        if (rearmLog)
            Plugin.Logger?.LogInfo($"Handshake: re-armed {LabelOf(ev)} after Install");

        var follow = Compare.NextAskSendFollowUp(sent, alreadyRetried);
        if (follow == Compare.AskSendFollowUp.ArmDeadline)
        {
            ArmAnswerDeadline(ev);
            return;
        }
        if (follow == Compare.AskSendFollowUp.RetryOnce)
        {
            Plugin.Logger?.LogWarning($"Handshake: ask to {LabelOf(ev)} failed; retrying next frame");
            var captured = ev;
            Dev.MainThread.PostDelayed(HandshakeAnswerWatch.AskRetryDelaySeconds, () =>
            {
                try
                {
                    if (Compare.ShouldSkipAsk(AnswerWatch.HasAnswered(captured.UserIndex)))
                    {
                        Plugin.Logger?.LogInfo(
                            $"Handshake: {LabelOf(captured)} answered before ask retry; skipping retry");
                        return;
                    }
                    AskUser(captured, alreadyRetried: true, rearmLog: false);
                }
                catch (Exception e) { Plugin.Logger?.LogError($"Handshake.AskRetry: {e}"); }
            }, DeadlineOwner);
            return;
        }

        Plugin.Logger?.LogWarning($"Handshake: ask to {LabelOf(ev)} failed after retry; kicking");
        SendResultAndKick(
            ev.World,
            ev.UserEntity,
            ev.UserIndex,
            LabelOf(ev),
            Compare.CouldNotAskReason(),
            timeoutKick: false,
            couldNotAsk: true);
    }

    /// <summary>
    /// REASK1: one send of a soft re-ask (<see cref="HandshakeAskKind.SoftReask"/>): the same request a join gets, then a
    /// wait of <see cref="HandshakeReask.RetryDelays"/>[<paramref name="attempt"/>]; no answer resends, and after the last
    /// wait the user stays connected and the admins get a log line. No deadline, no notice, no kick: the join-time rules
    /// (Keep them out, the client mod policy's wait for a report) are not applied; an actual answer is checked as always.
    /// </summary>
    private static void SoftReaskUser(PlayerConnected ev, int attempt)
    {
        string name = LabelOf(ev);
        PackedList packed = PackRequestNow(out string requestStamp);

        var requestRows = new List<ModRecord>(packed.Rows);
        PolicyAsksForReport(requestRows, ev.UserIndex, name);
        var list = new ModList();
        for (int i = 0; i < requestRows.Count; i++) list.Add(requestRows[i]);
        lock (SoftReasks) SoftReasks.Add(ev.UserIndex);
        bool sent = false;
        try { sent = CustomEvents.SendToUser(ev.World, ev.UserEntity, RequestEventId, list); }
        catch (Exception e) { Plugin.Logger?.LogWarning($"Handshake: re-ask of {name} threw: {e.Message}"); }
        if (sent) RequirementWatch.NoteSent(ev.UserIndex, requestStamp);
        Plugin.Logger?.LogInfo($"Handshake: re-asked {name} after a Core generation change (send {attempt + 1} of {HandshakeReask.RetryDelays.Count}; "
            + $"they answered when they joined, so no answer never disconnects them): sent={sent}");

        var captured = ev;
        ulong platform = ev.User.PlatformId;
        Dev.MainThread.PostDelayed(HandshakeReask.RetryDelays[Math.Min(attempt, HandshakeReask.RetryDelays.Count - 1)], () =>
        {
            try
            {
                bool connected = false;
                var world = captured.World;
                if (world != null && world.IsCreated && world.EntityManager.HasComponent<User>(captured.UserEntity))
                {
                    var now = world.EntityManager.GetComponentData<User>(captured.UserEntity);
                    connected = now.IsConnected && now.Index == captured.UserIndex && now.PlatformId == platform;
                }

                switch (HandshakeReask.After(attempt, AnswerWatch.HasAnswered(captured.UserIndex), connected))
                {
                    case HandshakeReaskStep.Resend:
                        SoftReaskUser(captured, attempt + 1);
                        return;
                    case HandshakeReaskStep.KeepConnected:
                        Plugin.Logger?.LogWarning(HandshakeReask.KeptConnectedLine(name, attempt + 1));
                        break;
                }

                lock (SoftReasks) SoftReasks.Remove(captured.UserIndex);
            }
            catch (Exception e) { Plugin.Logger?.LogError($"Handshake.SoftReask: {e}"); }
        }, DeadlineOwner);
    }

    /// <summary>
    /// RESEND1 (live 2026-10-14): the settled compare <see cref="NoteRequiredChanged"/> scheduled. When the request's rows
    /// differ from the ones last sent (a server plugin loaded or unloaded mid-session), send the new request to every
    /// connected user whose handshake is ok. Their client records it (the evidence the mod page's server-part cell and
    /// needs-server-part rows read, refreshed in place), resumes or pauses its per-server mods, and answers; that answer is
    /// judged as a re-answer, except that a mismatch keeps them connected (<see cref="HandshakeReask.KeepConnectedOnMismatch"/>).
    /// A user mid-join is left alone: their answer is judged against the current table anyway.
    /// </summary>
    private static void ResendRequestIfRequirementsChanged()
    {
        try
        {
            string now = RequestStampNow();
            var world = Worlds.Server;
            if (world == null || !world.IsCreated) return;
            var em = world.EntityManager;
            var entities = Query.Entities(
                em,
                includeDisabled: true,
                includeSystems: false,
                ComponentIndex.Of<User>(ComponentType.AccessMode.ReadOnly));
            int stale = 0, sent = 0;
            for (int i = 0; i < entities.Count; i++)
            {
                try
                {
                    var entity = entities[i];
                    var user = em.GetComponentData<User>(entity);
                    if (!user.IsConnected) continue;
                    // A user mid-join is compared again when its answer is accepted (AcceptAnswer).
                    if (AnswerWatch.GetStatus(user.Index) != HandshakeAnswerWatch.Status.Ok) continue;
                    if (!RequirementWatch.IsStale(user.Index, now)) continue;
                    stale++;
                    if (SendRequirementResend(world, entity, user)) sent++;
                }
                catch (Exception e)
                {
                    Plugin.Logger?.LogWarning("Handshake: the requirement re-send to a connected user threw: " + e.Message);
                }
            }

            if (stale > 0)
                Plugin.Logger?.LogInfo("Handshake: this server's mods changed; re-sent the request to " + sent.ToString(CultureInfo.InvariantCulture)
                    + " of " + stale.ToString(CultureInfo.InvariantCulture) + " connected user(s) whose last request differs");
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("Handshake.ResendRequestIfRequirementsChanged: " + e);
        }
        finally
        {
            // Review L3: whatever happened, the next change schedules another compare.
            RequirementWatch.Unschedule();
        }
    }

    /// <summary>
    /// RESEND1: send <paramref name="user"/> the request as it is now, with the policy's capability row. Only a send that
    /// went out records the stamp and one owed answer (review L2). True when it was sent.
    /// </summary>
    private static bool SendRequirementResend(World world, Entity entity, User user)
    {
        var ev = new PlayerConnected(world, entity, user);
        PackedList packed = PackRequestNow(out string stamp);
        var requestRows = new List<ModRecord>(packed.Rows);
        PolicyAsksForReport(requestRows, user.Index, LabelOf(ev));
        var list = new ModList();
        for (int r = 0; r < requestRows.Count; r++) list.Add(requestRows[r]);
        if (!CustomEvents.SendToUser(world, entity, RequestEventId, list)) return false;
        RequirementWatch.NoteSent(user.Index, stamp);
        RequirementWatch.NoteResendSent(user.Index);
        return true;
    }

    private static unsafe void OnRequest(int _, byte* payload, int length)
    {
        try
        {
            if (!CustomEvents.TryRead<ModList>(payload, length, out var asked)) return;
            int claimed = asked.Count;
            int count = Compare.ClampCount(claimed);
            if (Compare.IsMalformedCount(claimed))
                WarnMalformedOnce(int.MinValue, "server", claimed);

            var askedRows = new List<ModRecord>(count);
            for (int i = 0; i < count; i++)
            {
                var rec = asked.GetRecord(i);
                if (rec.Guid.Length == 0) continue;
                askedRows.Add(rec);
            }
            askedRows = Compare.ApplyMetaRecord(askedRows);
            // CLIENTPOLICY1: the server's capability row (it asks for this client's mod report). Lifted out before the
            // rows are recorded, so nothing downstream mistakes it for a mod; a request carrying it left no mod out.
            // A request on a dead or never-ok connection is a new join (handshake epoch), not a
            // Core-only refresh. Refresh while LastClientResult is still ok keeps the epoch.
            // (NotifyHandshakeEpochChanged runs inside TakeClientPolicyAsk, before the ask is noted.)
            askedRows = TakeClientPolicyAsk(askedRows, newJoin: !string.Equals(LastClientResult, "ok", StringComparison.Ordinal), out bool hadCapabilityRow);
            // A request that carried the row used a slot on it, not on a mod: it is not a full (truncated) request.
            if (hadCapabilityRow && count > 0) count--;
            // Job FMAN13 (R44 finding 9). Keep what this server actually asked for: it is the only
            // thing on this machine that says what the server enforces, and the consent panel is
            // not allowed to predict a disconnect from anything else.
            RecordServerRequest(askedRows, truncated: count >= MaxMods);
            // AUTOUPDATE1: the pre-join prompt caps an update at what this server asks for; remembered per dialed address.
            Progenitor.Install.ClientUpdates.NoteServerAsked(askedRows);
            // Job FMAN17. A request is the start of a join attempt and always arrives before that
            // attempt's result, so this is where the previous attempt's refusal stops being this
            // client's state. Without it a client that was refused, went to the menu and connected
            // again would route the new server's catalog into the old deny report.
            ModManifestDeny.OnJoinAttempt();
            // Job FMAN19: same reason, one job later. An install window belongs to the join it was
            // opened in, and a request is that join's first packet.
            ClientInstallWindow.Reset();

            // Per-server mod pause: resume what this server asks about and pause this client's own
            // hot-loaded mods that would fail only because the server does not run them, then
            // reply. Unchanged (an immediate reply) when there is nothing to do. A full request may
            // have been truncated, so it never auto-pauses.
            var rowsForReply = askedRows;
            string why = $"server asked about {count} mod(s)";
            bool truncatedRequest = count >= MaxMods;
            // JOINFLOW4: a kept version this server needs is swapped in first (when the reloader can do it now),
            // and the reply waits until it is declared. Immediate when nothing is kept for the asked mods.
            ModVersionJoin.BeforeReply(rowsForReply,
                () => ServerModPause.OnRequest(rowsForReply, truncated: truncatedRequest, () => SendReply(rowsForReply, why)));
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"Handshake.OnRequest: {e}");
        }
    }

    /// <summary>
    /// Job FMAN19. Answer the handshake <b>again</b>, on the same connection, from the rows this
    /// server already asked about (<see cref="CopyServerAsked"/>) — the client half of FMAN18's
    /// install window, once the package it fetched has loaded.
    ///
    /// <para>It is deliberately <see cref="SendReply"/> and nothing else. The reply is rebuilt from
    /// the current <c>Provided</c> table, which is the whole point (a plugin loaded inside the
    /// window is in it now), and the per-join resets <see cref="OnRequest"/> does — the deny
    /// report, the install window itself — must <em>not</em> run: this is the same join attempt,
    /// not a new one. The server closes the window on this packet and re-evaluates it down the
    /// ordinary path.</para>
    ///
    /// <para>False when there is nothing to answer: no request reached this connection, so there is
    /// no server waiting for one. A request that asked about no mod (A6: a server whose only
    /// requirement is its client mod policy) is still answered.</para>
    /// </summary>
    internal static bool ReDeclareSelf()
    {
        try
        {
            var askedRows = new List<ModRecord>(CopyServerAsked());
            // A6 (live 2026-10-07): a server whose only requirement is its client mod policy asks about no mod, and its
            // install window still waits for this answer (and the report after it). Only a connection that never got a
            // request has nothing to answer.
            if (!HasRequestToAnswer)
            {
                Plugin.Logger?.LogWarning(
                    "Handshake.ReDeclareSelf: no server request on this connection; nothing sent");
                return false;
            }

            return SendReply(askedRows, "re-answering the handshake");
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"Handshake.ReDeclareSelf: {e}");
            return false;
        }
    }

    /// <summary>
    /// Pack and send this client's answer to <paramref name="askedRows"/>. One body for the first
    /// answer and for FMAN19's re-answer, so the two cannot pack differently.
    /// </summary>
    private static bool SendReply(List<ModRecord> askedRows, string why)
    {
        PackedList packed;
        lock (Provided)
        {
            var provided = new List<ModRecord>(Provided.Count);
            foreach (var rec in Provided.Values) provided.Add(rec);
            packed = Compare.PackReply(askedRows, provided);
        }

        // Job FMAN20 (R50 finding 2). The capability row goes on LAST, after any metadata or
        // overflow row PackReply appended, so a reader that drops it shifts no in-slot metadata
        // index. If there is no free slot it is simply not sent and this client gets the
        // pre-FMAN18 immediate kick instead of an install window: answering the server's question
        // is worth more than declaring a capability, so no asked row is dropped for it.
        var rows = new List<ModRecord>(packed.Rows);
        if (!Compare.AppendCapabilityRow(rows, CapabilitiesForPrefabLimit(Progenitor.Content.PrefabSyncBudget.Cap)) && packed.Rows.Count >= MaxMods)
        {
            Plugin.Logger?.LogWarning(
                $"Handshake: all {MaxMods} reply slots are taken, so this client cannot declare "
                + "the install-window capability; a server will kick at once rather than hold the "
                + "join open while a missing mod is installed.");
        }

        // 2026-10-05: the asked rows this client can fix by itself during the join (a kept copy that loads now, a
        // turned-off copy that hot-loads), so a server can hold the join open for them. After the capability row,
        // only with a free slot; an older server ignores the row.
        try
        {
            var askedGuids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var a in askedRows) if (!string.IsNullOrEmpty(a.Guid)) askedGuids.Add(a.Guid);
            uint mask = InstallWindowFixes.LocalFixMask(packed.Rows, askedGuids, LocalFixProbe);
            if (mask != 0 && InstallWindowFixes.AppendFixRow(rows, mask))
                Plugin.Logger?.LogInfo("Handshake: declared local fixes for answer rows 0x" + mask.ToString("x", CultureInfo.InvariantCulture));
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("Handshake: declaring local fixes threw: " + e.Message); }

        var reply = new ModList();
        for (int i = 0; i < rows.Count; i++)
            reply.Add(rows[i]);
        for (int i = 0; i < packed.Dropped.Count; i++)
        {
            Plugin.Logger?.LogWarning(
                $"Handshake.OnRequest: already have {MaxMods} rows; not sending '{packed.Dropped[i].Guid}'.");
        }
        if (packed.DeclaredDropped)
            Plugin.Logger?.LogError($"Handshake.OnRequest: {Compare.CapExceededReason(packed.Dropped)}");

        bool sent = CustomEvents.SendToServer(ResponseEventId, reply);
        Plugin.Logger?.LogInfo($"Handshake: {why}; replied sent={sent} with {Compare.ClampCount(reply.Count)} row(s)");
        // CLIENTPOLICY1: the mod report follows every answer to a server that asked for it (same reliable channel, so
        // it arrives after the answer the server is holding the join on).
        if (sent)
        {
            try { ClientModReportClient.AfterAnswer(why); }
            catch (Exception e) { Plugin.Logger?.LogWarning("Handshake: the client mod report threw: " + e.Message); }
            // Mod compatibility C1: the feature table follows the answer to a server whose request declared ModFeatures.
            try { ModCompatNet.AfterClientAnswer(); }
            catch (Exception e) { Plugin.Logger?.LogWarning("Handshake: the feature table threw: " + e.Message); }
        }

        return sent;
    }

    private static unsafe void OnResponse(int _, Entity userEntity, ProjectM.Network.User user, byte* payload, int length)
    {
        try
        {
            // Threat model surface 1. The dispatcher already refuses an unresolved connection;
            // this is the handler's own refusal so nothing here can ever run as default(User)
            // (User.Index 0 is a real index) if a future seam reaches it another way.
            if (ReceiveGate.HandlerMustRefuse(userEntity == Entity.Null, user.Index))
            {
                Plugin.Logger?.LogWarning(ReceiveGate.HandlerRefusedLog("Handshake", ReceiveGate.Fourcc(ResponseEventId)));
                return;
            }
            if (!CustomEvents.TryRead<ModList>(payload, length, out var answer)) return;
            var name = Compare.PlayerLabel(user.CharacterName.ToString(), user.PlatformId);
            int claimed = answer.Count;
            int count = Compare.ClampCount(claimed);
            if (Compare.IsMalformedCount(claimed))
                WarnMalformedOnce(user.Index, name, claimed);

            var answered = new List<ModRecord>(count);
            int overflowDeclared = 0;
            bool overflow = false;
            // Job FMAN20 (R50 finding 2). What this client can DO, not what version it is. Read
            // here, beside the overflow row and for the same reason: a reserved row is lifted out
            // before `answered` exists, so nothing downstream can mistake it for a plugin.
            var clientCaps = Capability.None;
            // 2026-10-05: the answer rows the client says it can fix by itself during the join (bit i = row i of the
            // answer as sent, reserved rows included: the positions the client counted).
            uint fixMask = 0;
            var sent = new List<ModRecord>(count);
            for (int i = 0; i < count; i++)
            {
                var rec = answer.GetRecord(i);
                sent.Add(rec);
                if (rec.Guid.Length == 0) continue;
                if (string.Equals(rec.Guid, OverflowGuid, StringComparison.OrdinalIgnoreCase))
                {
                    overflow = true;
                    int.TryParse(rec.Version, NumberStyles.Integer, CultureInfo.InvariantCulture, out overflowDeclared);
                    continue;
                }
                if (string.Equals(rec.Guid, CapabilityGuid, StringComparison.OrdinalIgnoreCase))
                {
                    clientCaps |= Compare.ReadCapabilityBlob(rec.Version);
                    continue;
                }
                if (string.Equals(rec.Guid, InstallWindowFixes.FixGuid, StringComparison.OrdinalIgnoreCase))
                {
                    fixMask |= InstallWindowFixes.ReadFixBlob(rec.Version);
                    continue;
                }
                answered.Add(rec);
            }
            answered = Compare.ApplyMetaRecord(answered);
            var localFixes = InstallWindowFixes.DeclaredIds(sent, fixMask);
            // CLIENTPOLICY3: what this client can do mid-session (the notice, a re-ask after a policy change).
            NoteClientCaps(user.Index, clientCaps);

            // Job FMAN18. A second DeclareSelf inside an install window closes the window before
            // anything is evaluated, so the re-answer runs down exactly the same path as the
            // first and every existing rule applies to it unchanged. The grace stays SPENT
            // (HandshakeInstallGrace._used), so a re-answer that fails cannot open a second one.
            bool wasAwaitingInstall = InstallGrace.Settle(user.Index);
            if (wasAwaitingInstall)
                Plugin.Logger?.LogInfo($"Handshake: {name} re-answered inside the install window");

            // Mod compatibility C3 (the join hold C1 deferred): a client that declared VersionRanges sends its feature
            // table, with its ranges, right after this answer. The judgment waits for it, at most
            // FeatureTableHold.WaitSeconds, then proceeds with whatever it has; a client cannot hold a join slot longer.
            var answeredUser = user;
            // Mod compatibility C4 (review fix): Exact for Progenitor is read once per answer, so the hold and the judgment
            // (and the refusal's descriptor) agree even when it changes while the join waits for the table.
            // Live 2026-10-10: in the moments after a Core load the server's own image may not be read yet (Pending); the
            // join then waits for its table as under Exact, and its judgment waits until the status is known (bounded by
            // ProgenitorExactSource.PendingSeconds) instead of passing unchecked.
            var progenitorStatus = ProgenitorExact.ServerStatus();
            int answeredIndex = user.Index;
            // Review fix: a wait belongs to this answer only; another answer or a disconnect ends it unjudged.
            int answerGeneration = PendingAnswers.Next(answeredIndex);
            void Judge(string trigger)
            {
                // A judgment that waited is checked for a user who left meanwhile, as a held one is (JudgeAnswer).
                bool waited = false;
                ProgenitorExact.JudgeWhenKnown(progenitorStatus, ProgenitorExact.ServerStatus,
                    status => JudgeAnswer(userEntity, answeredUser, name, answered, overflow, overflowDeclared, clientCaps, localFixes,
                        waited && trigger == "rows" ? "pending" : trigger, status),
                    retry =>
                    {
                        if (!waited)
                        {
                            waited = true;
                            AnswerWatch.OnAnswerHeld(answeredIndex);
                        }

                        Dev.MainThread.PostDelayed(ProgenitorExact.PendingRetrySeconds, () =>
                        {
                            try { retry(); }
                            catch (Exception e) { Plugin.Logger?.LogError("Handshake: the Progenitor status wait: " + e); }
                        }, DeadlineOwner);
                    },
                    () => PendingAnswers.IsCurrent(answeredIndex, answerGeneration));
            }

            ForgetReportDuringHold(user.Index);
            if (HoldForFeatureTable(user.Index, name, clientCaps, progenitorStatus.HoldsJoins, Judge)) return;
            Judge("rows");
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"Handshake.OnResponse: {e}");
        }
    }

    /// <summary>
    /// Mod compatibility C3: the user's last version judgment (its settings and ranges), for the post-ok packets of this
    /// answer and the bridge. Forgotten on disconnect.
    /// </summary>
    private static readonly Dictionary<int, VersionJudgeContext> LastVersionJudge = new();

    /// <summary>Compat C3: the join hold for each answering user's feature table.</summary>
    internal static readonly FeatureTableHold TableHold = new();

    /// <summary>C4: each user's current answer, so a judgment waiting for the server's own status ends with it.</summary>
    internal static readonly AnswerGenerations PendingAnswers = new();

    /// <summary>
    /// Compat C3: hold the judgment of a client that declared <see cref="Capability.VersionRanges"/> until its table
    /// arrives (<see cref="OnClientFeatureTable"/>) or <see cref="FeatureTableHold.WaitSeconds"/> pass. False (judge now)
    /// for any other client.
    /// </summary>
    private static bool HoldForFeatureTable(int userIndex, string name, Capability clientCaps, bool progenitorExact, Action<string> judge)
    {
        if (!ProgenitorExact.HoldsForTable(clientCaps, progenitorExact)) return false;
        int seen;
        try { seen = ModCompatNet.TablesSeen(userIndex); }
        catch { return false; }
        int generation = TableHold.Begin(userIndex, seen, judge);
        // Review fix: the client answered; the answer deadline must not time it out while its judgment waits for the table.
        AnswerWatch.OnAnswerHeld(userIndex);
        Dev.MainThread.PostDelayed(FeatureTableHold.WaitSeconds, () =>
        {
            try
            {
                var due = TableHold.TryExpire(userIndex, generation);
                if (due == null) return;
                Plugin.Logger?.LogWarning("Handshake: " + name + "'s feature table did not arrive within "
                    + FeatureTableHold.WaitSeconds.ToString(CultureInfo.InvariantCulture) + " s; judging the answer on its handshake rows");
                due("no-table");
            }
            catch (Exception e) { Plugin.Logger?.LogError("Handshake: the feature table hold: " + e); }
        }, DeadlineOwner);
        return true;
    }

    /// <summary>Compat C3: a full feature table from <paramref name="userIndex"/> arrived (held or applied); a held join is judged now.</summary>
    internal static void OnClientFeatureTable(int userIndex, int tablesNow)
    {
        var due = TableHold.OnTable(userIndex, tablesNow);
        due?.Invoke("table");
    }

    /// <summary>Compat C3: the bridge's view of a user's last version judgment, or null.</summary>
    internal static VersionJudgeContext? VersionJudgeOf(int userIndex) =>
        LastVersionJudge.TryGetValue(userIndex, out var v) ? v : null;

    /// <summary>
    /// The judgment of one handshake answer (everything after the answer was read): the rows against this server's
    /// required list, then ok, an install window, or the refusal. Runs at once, or after the feature table hold.
    /// </summary>
    private static void JudgeAnswer(Entity userEntity, ProjectM.Network.User user, string name, List<ModRecord> answered, bool overflow,
        int overflowDeclared, Capability clientCaps, HashSet<string> localFixes, string trigger, ProgenitorExactStatus? progenitorStatus = null)
    {
        try
        {
            if (trigger != "rows" && !StillConnected(userEntity, user))
            {
                Plugin.Logger?.LogInfo("Handshake: " + name + " left before its held answer was judged");
                return;
            }

            int requiredCount;
            List<string> problems;
            List<string> failedIds;
            List<ModRecord> server;
            // Mod compatibility C3: the rows as the request carried them (Version matching and the admins' per-mod
            // rules applied), judged with ranges: the newer side's range when this client sent its table.
            var matching = VersionMatching.Current();
            lock (Required)
            {
                requiredCount = Required.Count;
                server = VersionMatchJudge.RequestRows(Required.Values, matching);
            }

            var clientRanges = (clientCaps & Capability.VersionRanges) != 0 ? ModCompatNet.ClientRangesOf(user.Index) : null;
            var versions = VersionMatching.ContextFor(server, clientRanges, matching);
            problems = Compare.Evaluate(server, answered, out failedIds, versions);
            foreach (var line in versions.Accepted) Plugin.Logger?.LogInfo("Handshake: " + line);
            // Mod compatibility C4: under Exact version for all, the client's Progenitor must be this server's own build
            // (Core version and sha256, from its feature table), while that build is obtainable (ProgenitorExact.StatusOf).
            bool progenitorRefused = false;
            try
            {
                var status = progenitorStatus ?? ProgenitorExact.ServerStatus();
                progenitorStatus = status;
                if (status.Enforced)
                {
                    var core = ModCompatNet.ClientCoreOf(user.Index);
                    string? coreRefusal = ProgenitorExact.Check(status, core.Table, core.Core, core.Sha, (clientCaps & Capability.ModFeatures) != 0);
                    if (!string.IsNullOrEmpty(coreRefusal))
                    {
                        problems.Insert(0, coreRefusal!);
                        failedIds.Add(ProgenitorExact.RowId);
                        progenitorRefused = true;
                    }
                    else Plugin.Logger?.LogInfo("Handshake: " + name + " runs this server's Progenitor " + status.Version + " (Exact version for all)");
                }
            }
            catch (Exception e) { Plugin.Logger?.LogWarning("Handshake: the Progenitor version check threw: " + e.Message); }
            LastVersionJudge[user.Index] = versions;

            // Overflow on the *client* reply is fail-closed for a dropped ServerMustHave extra
            // (or a ServerMustHave meta record that could not fit). A 17th declared *server*
            // plugin was already refused at Require and must not kick every answering client.
            if (overflow)
                problems.Insert(0, Compare.CapExceededReason(overflowDeclared));

            if (!SupportsPrefabLimit(clientCaps, Progenitor.Content.PrefabSyncBudget.Cap))
            {
                int requiredLimit = Progenitor.Content.PrefabSyncBudget.Cap > 1024 ? 16384 : 1024;
                problems.Insert(0, Compare.ItemBudgetProblem(requiredLimit, OwnCoreForReasons()));
                failedIds.Add("progenitor");
            }

            if (Progenitor.Content.PrefabRegistry.RequiresExtendedSync && (clientCaps & Capability.ItemLedgerChunks) == 0)
            {
                problems.Insert(0, Compare.CoreCapabilityProblem("extended item synchronization", OwnCoreForReasons()));
                failedIds.Add("progenitor");
            }

            if (Progenitor.Content.DataPackRuntime.RequiresItemStats && (clientCaps & Capability.ItemStatSync) == 0)
            {
                problems.Insert(0, Compare.CoreCapabilityProblem("equipment-stat synchronization", OwnCoreForReasons()));
                failedIds.Add("progenitor");
            }

            var world = Worlds.Server;
            // RESEND1: this answer settles one owed re-send answer, if any (review L1: a count, not a flag).
            bool requirementResend = RequirementWatch.TakeResendAnswer(user.Index);
            if (problems.Count == 0)
            {
                // Job FMAN21 (R52 finding 3). Read BEFORE OnAnswer, which is what erases the
                // difference: a user already at Ok that answers again did so because its own state
                // changed — a package finished loading inside an install window, or a client-side
                // Core swap emptied the tables holding what this server sent it. That is the only
                // signal this server ever gets for the second case.
                bool wasAlreadyOk = AnswerWatch.GetStatus(user.Index) == HandshakeAnswerWatch.Status.Ok;
                // REASK1: a user who answered this session (before a Core swap emptied the tables) is not re-judged by the
                // join-time client mod policy; mid-session judgment runs on the reports that follow, as for wasAlreadyOk.
                bool rememberedOk;
                try { rememberedOk = SessionMemory.Contains(user.Index, user.PlatformId); }
                catch { rememberedOk = false; }
                void AcceptAnswer()
                {
                AnswerWatch.OnAnswer(user.Index);
                try { SessionMemory.Remember(user.Index, user.PlatformId); }
                catch { /* best effort */ }
                lock (SoftReasks) SoftReasks.Remove(user.Index);
                // JOINADMIN1: an accepted answer (an install window that worked) is not a refused join.
                RefusedJoins.Drop(user.Index);
                Plugin.Logger?.LogInfo($"Handshake: {name} ok ({requiredCount} mod(s) match)");
                if (world != null) CustomEvents.SendToUser(world, userEntity, ResultEventId, Result.Create(true, "ok"));
                // Job FMAN22 (R53 finding 3). All three post-ok effects, behind one bound when
                // this user was already ok. FMAN21 put the floor on the resend alone — the
                // cheapest of the three, and the only one that was already deduped — leaving the
                // config snapshot and the catalog to run per inbound packet on this thread.
                // The pair stays composed here, in wire order, because that order is the thing
                // ModManifestPhase2Tests.Live_OnResponse_source_calls_config_sync_then_manifest
                // pins: result, then config snapshot, then catalog.
                void SendPostOkPackets()
                {
                    ConfigSync.OnHandshakeOk(world, userEntity, answered, versions);
                    ModManifest.OnHandshakeOk(user.Index);
                    // REMOTEINST1: after the catalog, so the pinned wire order above is unchanged.
                    Progenitor.Install.RemoteInstallEvents.OnHandshakeOk(world, userEntity, clientCaps);
                    // JOINADMIN1: the admin channel's hello, last, to a client that declared it.
                    JoinAdminServer.OnHandshakeOk(world, userEntity, clientCaps);
                    // Chat command hints: the channel's hello, after the admin hello, to a client that declared it.
                    Progenitor.Chat.ChatCommandHintsServer.OnHandshakeOk(world, userEntity, clientCaps);
                    // REQRULE2: the mid-session rule and its grace, last, to a client that declared it.
                    SendPolicyRuleTo(world, userEntity, clientCaps);
                    // Mod compatibility C1: this server's feature table, to a client that declared ModFeatures.
                    ModCompatNet.OnHandshakeOk(world, userEntity, clientCaps);
                    // Initial prefab definitions precede the answer; capabilities are known now.
                    if (world != null)
                        Progenitor.Content.PrefabRegistry.SendItemStatsToUser(world, userEntity);
                }

                RunPostOkEffects(user.Index, wasAlreadyOk, SendPostOkPackets);

                // RESEND1 (review M2): the required table changed after this user was asked (mid-join, or between a
                // re-send and its answer). The settled compare skipped them while they were not ok; send them the new rows now.
                try
                {
                    if (world != null && RequirementWatch.IsStale(user.Index, RequestStampNow()))
                    {
                        bool resent = SendRequirementResend(world, userEntity, user);
                        Plugin.Logger?.LogInfo($"Handshake: this server's mods changed while {name} was being asked; re-sent the request: sent={resent}");
                    }
                }
                catch (Exception e) { Plugin.Logger?.LogWarning("Handshake: the requirement re-send after an accepted answer threw: " + e.Message); }
                }

                // CLIENTPOLICY1: a first acceptance under a client mod policy waits for the client's mod report (or
                // is judged at once when none can come); a re-answer from a user already ok is not re-judged here:
                // mid-session judgment (CLIENTPOLICY3) runs on the reports that follow it (Handshake.ClientPolicy.cs).
                if (!wasAlreadyOk && !rememberedOk && GateOnClientPolicy(world, userEntity, user.Index, name, user.PlatformId, clientCaps, AcceptAnswer))
                    return;
                AcceptAnswer();
                return;
            }
            // Job FMAN18, retargeted 2026-10-05 (servers no longer send mod files). Before the kick: can the CLIENT fix the
            // miss during the join (a Thunderstore package serving the needed version, or a kept or turned-off copy it
            // declared)? The decision is Compare.DecideMiss's alone; this method only supplies the facts it has. On a
            // yes the client gets one window per join to fix the rows from its join screens and re-answer.
            // JOINADMIN1: the failing rows (mod, required, had) are held for the admins' refused-join record:
            // recorded when this join is refused (now, or when its install window expires), forgotten when it
            // is accepted. Only rows this server requires are kept, once per connection; the SteamID feeds an
            // in-memory throttle and is never written.
            // RESEND1: an answer to a mid-session requirement re-send from a user already ok is not a join: a mismatch keeps
            // them connected (the admins read why in the log) instead of kicking them over a server mod that just loaded.
            if (HandshakeReask.KeepConnectedOnMismatch(requirementResend, AnswerWatch.GetStatus(user.Index) == HandshakeAnswerWatch.Status.Ok))
            {
                Plugin.Logger?.LogWarning(HandshakeReask.KeptAfterRequirementChangeLine(name, problems));
                return;
            }

            RefusedJoins.Note(user.Index, failedIds, server, answered, user.PlatformId);
            if (TryDeferKick(world, userEntity, user.Index, name, problems, failedIds, server, answered, clientCaps, localFixes))
                return;

            RefusedJoins.Commit(user.Index);
            // REASK1: an actual failing answer ends what this session remembered.
            try { SessionMemory.Forget(user.Index); }
            catch { /* best effort */ }
            lock (SoftReasks) SoftReasks.Remove(user.Index);
            var full = Compare.MismatchKickReason(problems);
            AnswerWatch.OnMismatch(user.Index, full);
            if (world != null) SendResultAndKick(world, userEntity, user.Index, name, full, timeoutKick: false);
            else Plugin.Logger?.LogWarning($"Handshake: {name} failed: {full}");

            // Job FMAN17, corrected by evidence 185 (R48 finding 3). The descriptor goes out
            // after the Result, and what carries it is frame and reliable-channel ordering, NOT
            // the two-second kick window: this send is synchronous and already handed to the
            // transport before SendResultAndKick's PostDelayed has scheduled the disconnect at
            // all, so the Result and then the descriptor are both with the transport, in order,
            // before ServerNetworkLayer.Disconnect writes ConnectState = 6 and sends start being
            // refused (docs/mod-manifest.md, evidence 185).
            //
            // The rows are ServerEnforcedFailures — the ids THIS server's own Check rejected this
            // client over — not `failedIds` (which also carries rows the client requires and this
            // server does not have, which no catalog of ours can name) and not the whole
            // inventory. This call grants a catalog and nothing else: it never adds the user to
            // ModManifest's HandshakeOkUsers.
            // JOINFLOW1: a client that draws the join screens is also sent the signed message alone when
            // no failing row has a catalog row (Capability.JoinFlow; see ModManifest.OnHandshakeDenied).
            // C4: a refusal over this server's Progenitor build keeps the signed requirement in the descriptor.
            var denyIds = Compare.DenyDescriptorIds(failedIds, server, answered);
            if (progenitorRefused) denyIds.Add(ProgenitorExact.RowId);
            var judgedProgenitor = progenitorRefused ? progenitorStatus?.Requirement : null;
            ModManifest.OnHandshakeDenied(user.Index, denyIds, (clientCaps & Capability.JoinFlow) != 0, judgedProgenitor);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"Handshake.OnResponse: {e}");
        }
    }

    /// <summary>True while <paramref name="userEntity"/> is still the connected user that answered.</summary>
    private static bool StillConnected(Entity userEntity, ProjectM.Network.User answered)
    {
        try
        {
            var world = Worlds.Server;
            if (world == null || !world.IsCreated || !world.EntityManager.HasComponent<ProjectM.Network.User>(userEntity)) return false;
            var now = world.EntityManager.GetComponentData<ProjectM.Network.User>(userEntity);
            return now.IsConnected && now.Index == answered.Index && now.PlatformId == answered.PlatformId;
        }
        catch { return false; }
    }

    /// <summary>
    /// Job FMAN18, retargeted 2026-10-05. Open an install window instead of kicking, when <see cref="Compare.DecideMiss"/>
    /// says every failing row is one this server enforces <em>and</em> the client can fix during the join. True when the
    /// window is open and the caller must return; false when the caller must kick.
    ///
    /// <para><b>Sendability is decided before anything is sent, and then exactly one <see cref="Result"/> goes out</b>
    /// (job FMAN20, R50 finding 8): the descriptor is built first; if nothing can be sent this returns false having sent
    /// nothing, opened no window and spent no grace.</para>
    ///
    /// <para><b>Order on the wire is the deferred <see cref="Result"/>, then the descriptor</b> — the order the client
    /// needs: the Result is what tells it a window exists at all, and reliable events keep order.</para>
    ///
    /// <para><b>Nothing here can keep a failing client joined.</b> Every branch that is not a successfully opened window
    /// falls through to the caller's kick, and the whole body runs inside <c>OnResponse</c>'s try.</para>
    /// </summary>
    private static bool TryDeferKick(
        World? world,
        Entity userEntity,
        int userIndex,
        string name,
        List<string> problems,
        List<string> failedIds,
        List<ModRecord> server,
        List<ModRecord> answered,
        Capability clientCaps,
        HashSet<string> localFixes)
    {
        int seconds = ReadInstallGraceSeconds();
        try { ServerPackages?.Want(ModManifest.CatalogPackages()); }
        catch { /* a refresh that cannot start leaves the last read */ }
        var verdict = Compare.DecideMiss(
            problems,
            failedIds,
            server,
            answered,
            ServerFixFor(server, localFixes),
            graceAvailable: seconds > 0 && !InstallGrace.HasSpentGrace(userIndex),
            clientCanInstall: Compare.CanSpendInstallWindow(clientCaps));
        if (!verdict.Defer)
        {
            if (seconds > 0)
                Plugin.Logger?.LogInfo($"Handshake: {name} not deferred: {verdict.Why}");
            return false;
        }

        return OpenInstallWindow(world, userEntity, userIndex, name, verdict.Ids, FixWords(verdict.Fixes),
            string.Join("; ", problems), seconds, (clientCaps & Capability.JoinFlow) != 0);
    }

    /// <summary>The fixes as the log names them (<c>thunderstore, local</c>).</summary>
    private static string FixWords(IReadOnlyList<MissFix> fixes)
    {
        var words = new List<string>(fixes.Count);
        foreach (var f in fixes) words.Add(f.ToString().ToLowerInvariant());
        return string.Join(", ", words);
    }

    /// <summary>
    /// Open the window (shared by the handshake rows and the client mod policy): build the descriptor, grant the one
    /// window, send the deferred Result, then the descriptor, and arm the expiry. False (nothing sent) when the descriptor
    /// cannot be built or the window is not granted.
    /// </summary>
    private static bool OpenInstallWindow(
        World? world, Entity userEntity, int userIndex, string name, IReadOnlyList<string> ids, string fixes, string problems,
        int seconds, bool joinFlowClient)
    {
        // Decide first, send second. Nothing above this line has left the machine.
        if (!ModManifest.TryBuildInstallDescriptor(userIndex, ids, joinFlowClient, out var descriptor) || descriptor == null)
        {
            Plugin.Logger?.LogWarning(
                $"Handshake: {name} not deferred: no descriptor could be sent for "
                + string.Join(", ", ids) + "; kicking");
            return false;
        }

        var grant = InstallGrace.Begin(userIndex, name, ids, seconds);
        if (!grant.Opened)
        {
            Plugin.Logger?.LogInfo($"Handshake: {name} not deferred: install window {grant.Verdict}");
            return false;
        }

        AnswerWatch.OnAwaitingInstall(userIndex);
        string message = Compare.DeferredResultMessage(ids, grant.GraceSeconds);
        if (world != null)
            CustomEvents.SendToUser(world, userEntity, ResultEventId, Result.CreateDeferred(message));

        ModManifest.SendInstallDescriptor(userIndex, descriptor);

        Plugin.Logger?.LogWarning(
            $"Handshake: {name} deferred for {grant.GraceSeconds} s to fix "
            + string.Join(", ", ids) + " on the client (" + fixes + ")"
            + (problems.Length > 0 ? " (" + problems + ")" : ""));
        ArmInstallGrace(world, userEntity, userIndex, name, grant);
        return true;
    }

    /// <summary>
    /// The window's own delayed post (job FMAN18). Guarded by the grant generation, so a settled
    /// window, a disconnect and a stale post are all no-ops. On expiry the kick takes the ordinary
    /// readable-reason path — Result, then <c>Disconnect</c> after the flush hop — with a reason
    /// that names the window rather than only the mods.
    /// </summary>
    private static void ArmInstallGrace(
        World? world,
        Entity userEntity,
        int userIndex,
        string name,
        HandshakeInstallGrace.GrantResult grant)
    {
        int generation = grant.Generation;
        Dev.MainThread.PostDelayed(grant.GraceSeconds, () =>
        {
            try
            {
                if (!InstallGrace.TryExpire(userIndex, generation, out var due)) return;
                RefusedJoins.Commit(userIndex);
                AnswerWatch.OnMismatch(userIndex, due.Reason);
                if (world != null)
                    SendResultAndKick(world, userEntity, userIndex, due.Name, due.Reason, timeoutKick: false);
                else
                    Plugin.Logger?.LogWarning($"Handshake: {name} failed: {due.Reason}");
            }
            catch (Exception e)
            {
                Plugin.Logger?.LogError($"Handshake.InstallGraceDeadline: {e}");
            }
        }, DeadlineOwner);
    }

    private static unsafe void OnResult(int _, byte* payload, int length)
    {
        try
        {
            if (!CustomEvents.TryRead<Result>(payload, length, out var r)) return;
            LastClientResult = r.Text;
            // Job FMAN19. The byte has three values and this is the only place that reads it.
            // Deferred is not denied: the join has not passed and has not been given up on, so the
            // deny report must NOT open (its whole claim is "nothing you do on this connection
            // changes this") and the descriptor that follows belongs to the consent panel.
            var kind = ClientInstallWindow.Classify(r.Ok);
            if (kind == HandshakeResultKind.Ok)
            {
                Plugin.Logger?.LogInfo("Handshake: ok");
                // COUNTERPART-SAMPLES: the Mods page reads the server's evidence only once the join
                // is accepted, so an open page re-reads its server part now.
                NotifyServerPartEvidence("handshake-ok");
                // Same join seam ConfigSync uses (handshake result, then catalog). Capture the
                // connect address here so trust pins are host-keyed before the first PrmM.
                ModManifest.CaptureConnectHostFromClient();
                // A deny panel belongs to the join that produced it. An accepted result
                // replaces that refusal: dispose, never stack over the live world.
                ModManifestDeny.OnJoinEnded();
                // Mod compatibility C4: a Progenitor build placed for a server has now been used for a join; it is
                // restored once the player is back at the title screen.
                try { Progenitor.Install.SelfUpdates.NoteJoinAccepted(); }
                catch (Exception e) { Plugin.Logger?.LogWarning("Handshake: noting the join for a placed Progenitor threw: " + e.Message); }
            }
            else if (kind == HandshakeResultKind.Deferred)
            {
                Plugin.Logger?.LogWarning($"Handshake: {r.Text}");
                // Host first, for the same reason the deny path captures it first: the consent
                // prompt this opens is host-keyed.
                ModManifest.CaptureConnectHostFromClient();
                ModManifestDeny.OnJoinEnded();
                ClientInstallWindow.OnResult(r.Ok, r.Text);
            }
            else
            {
                Plugin.Logger?.LogWarning($"Handshake: {r.Text}");
                // Job FMAN17. The refusal is the client's state for the rest of this connection
                // attempt: the deny report opens now, from local state alone (ServerAsked plus this
                // client's own Provided rows), and the flag routes the descriptor that follows to
                // the read-only report rather than the consent panel. Host first, so a report built
                // here names the address this client dialed.
                ModManifest.CaptureConnectHostFromClient();
                ModManifestDeny.OnJoinDenied(r.Text, ModManifest.CurrentConnectHost());
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"Handshake.OnResult: {e}");
        }
    }

    /// <summary>
    /// A Count outside [0, MaxMods] is a malformed packet. Log once per sender so a flood
    /// cannot fill the log, then ignore the out-of-range slots (the caller already clamped).
    /// </summary>
    private static void WarnMalformedOnce(int senderId, string senderLabel, int claimed)
    {
        lock (MalformedSenders)
        {
            if (!MalformedSenders.Add(senderId)) return;
        }
        Plugin.Logger?.LogWarning(
            $"Handshake: malformed ModList.Count={claimed} from {senderLabel}; ignoring slots outside 0..{MaxMods}.");
    }

    private static void OnPlayerDisconnected(PlayerDisconnected ev)
    {
        RefreshRequests.Forget(ev.User.Index);
        // REASK1: the session's answer and any soft re-ask end with the connection.
        try { SessionMemory.Forget(ev.User.Index); }
        catch { /* best effort */ }
        lock (SoftReasks) SoftReasks.Remove(ev.User.Index);
        RequirementWatch.Forget(ev.User.Index);
        try { AnswerWatch.OnDisconnect(ev.User.Index); }
        catch (Exception e) { Plugin.Logger?.LogError($"Handshake.OnPlayerDisconnected: {e}"); }
        // Job FMAN18: the window and the spent-grace mark both die with the connection, so a
        // delayed expiry cannot kick a new occupant of this User.Index and a genuine reconnect
        // gets its own window.
        try { InstallGrace.OnDisconnect(ev.User.Index); }
        catch (Exception e) { Plugin.Logger?.LogError($"Handshake.OnPlayerDisconnected (install grace): {e}"); }
        // Job FMAN21: and so does the re-answer floor, so a reconnecting player is not made to
        // wait out the previous occupant's five seconds.
        try { ReAnswers.OnDisconnect(ev.User.Index); }
        catch (Exception e) { Plugin.Logger?.LogError($"Handshake.OnPlayerDisconnected (re-answer): {e}"); }
        // CLIENTPOLICY1: a waiting join, a half-received report and the ask die with the connection.
        try { ClientPolicyOnDisconnect(ev.User.Index); }
        catch (Exception e) { Plugin.Logger?.LogError($"Handshake.OnPlayerDisconnected (client policy): {e}"); }
        try { NoAnswerOnDisconnect(ev.User.Index); }
        catch (Exception e) { Plugin.Logger?.LogError($"Handshake.OnPlayerDisconnected (no-answer notice): {e}"); }
        // Compat C3: a held answer and its judgment die with the connection.
        TableHold.Forget(ev.User.Index);
        PendingAnswers.Forget(ev.User.Index);
        ForgetReportDuringHold(ev.User.Index);
        LastVersionJudge.Remove(ev.User.Index);
    }

    private static void ArmAnswerDeadline(PlayerConnected ev) =>
        ArmAnswerDeadline(ev.World, ev.UserEntity, ev.UserIndex, LabelOf(ev), ShouldSkipAnswerDeadline(ev));

    /// <summary>
    /// Could-not-ask Result hop: skip is always false (we already tried SendToUser).
    /// Shared with the join / re-ask path so the 20 s expire callback is the same.
    /// </summary>
    private static void ArmAnswerDeadline(World world, Entity userEntity, int userIndex, string name, bool skip)
    {
        if (!Compare.CanArmDeadline(AnswerWatch.GetStatus(userIndex)))
            return;

        var required = CopyRequired();
        bool requires = RequiresAnswer(required);
        string summary = RequiredSummary(required);
        int timeout = AnswerTimeoutFor(ReadAnswerTimeoutSeconds());
        var arm = AnswerWatch.Arm(userIndex, name, timeout, skip, requires, summary);
        if (arm.Skipped)
        {
            Plugin.Logger?.LogInfo(
                $"Handshake: {name} has no network peer (ghost or hosted child); not waiting for a handshake answer");
            return;
        }
        if (arm.Unverified)
        {
            Plugin.Logger?.LogInfo(
                $"Handshake: {name} unverified (no plugin requires a handshake answer); continuing");
            return;
        }
        if (!arm.Scheduled) return;

        // NOPROGNOTICE: the deadline runs as a countdown that waits while the player has no character (character
        // creation has no chat or HUD) and sends the player-without-Progenitor notice before the same kick.
        RunNoAnswerCountdown(world, userEntity, userIndex, name, arm.Generation, timeout);
    }

    /// <summary>
    /// Fail-closed skip. Only a positive ghost (User.IsBot, no peer) or hosted child
    /// (approved-user row with a null NetConnectionId) is skipped. A throw, a missing
    /// approved-user table, or a missing row for a non-bot arms the deadline.
    /// Extra-instance clients that made the approved table still have to answer, even
    /// if hail <c>IsBot</c> was set (they have a NetConnectionId).
    /// </summary>
    private static bool ShouldSkipAnswerDeadline(PlayerConnected ev)
    {
        try
        {
            var peer = ReadPeer(ev.World, ev.UserEntity);
            return Compare.ShouldSkipAnswerDeadline(
                lookupThrew: false,
                approvedTablePresent: peer.TablePresent,
                userRowFound: peer.RowFound,
                hasNetConnection: peer.HasConnection,
                isBot: ev.User.IsBot);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning(
                $"Handshake: peer lookup for {LabelOf(ev)} threw: {e.Message}; arming the deadline");
            return Compare.ShouldSkipAnswerDeadline(
                lookupThrew: true,
                approvedTablePresent: false,
                userRowFound: false,
                hasNetConnection: false,
                isBot: false);
        }
    }

    private readonly struct PeerLookup
    {
        public PeerLookup(bool tablePresent, bool rowFound, bool hasConnection)
        {
            TablePresent = tablePresent;
            RowFound = rowFound;
            HasConnection = hasConnection;
        }

        public bool TablePresent { get; }
        public bool RowFound { get; }
        public bool HasConnection { get; }
    }

    private static PeerLookup ReadPeer(World world, Entity userEntity)
    {
        var bootstrap = world.GetExistingSystemManaged<ServerBootstrapSystem>();
        if (bootstrap == null) return new PeerLookup(false, false, false);
        var clients = bootstrap._ApprovedUsersLookup;
        if (clients == null) return new PeerLookup(false, false, false);
        for (int i = 0; i < clients.Length; i++)
        {
            var client = clients[i];
            if (client == null || !client.Exists || client.UserEntity != userEntity) continue;
            return new PeerLookup(true, true, client.NetConnectionId != null);
        }
        return new PeerLookup(true, false, false);
    }

    /// <summary>
    /// Job FMAN18. Seconds of install window, config <c>Networking.InstallGraceSeconds</c>.
    /// 0 (or a negative value, clamped) disables the deferral entirely and every miss kicks the
    /// way it did before FMAN18. Read once per deferral decision, never re-read to extend a
    /// window that is already open.
    /// </summary>
    private static int ReadInstallGraceSeconds()
    {
        try
        {
            int n = _installGraceSeconds?.Value ?? DefaultInstallGraceSeconds;
            if (n < 0) return 0;
            return n;
        }
        catch
        {
            return DefaultInstallGraceSeconds;
        }
    }

    /// <summary>JOINADMIN1: <c>Networking.InstallGraceSeconds</c> as the handshake reads it (the admin panel's window).</summary>
    internal static int InstallGraceSecondsSetting() => ReadInstallGraceSeconds();

    /// <summary>
    /// JOINADMIN1: store <paramref name="seconds"/> (already clamped to 0-300 by the admin policy) in
    /// <c>Networking.InstallGraceSeconds</c> and make sure it reaches the cfg on disk. The next deferral decision
    /// reads it; a window already open keeps its length. False when the entry is not bound yet.
    /// </summary>
    internal static bool TrySetInstallGraceSeconds(int seconds)
    {
        var entry = _installGraceSeconds;
        if (entry == null) return false;
        bool changed = entry.Value != seconds;
        entry.Value = seconds;
        try { Progenitor.Ui.SettingPersistence.AfterWrite("progenitor/Networking/InstallGraceSeconds", entry, changed); }
        catch { /* the value is set; the cfg save is best effort */ }
        return entry.Value == seconds;
    }

    private static int ReadAnswerTimeoutSeconds()
    {
        try
        {
            int n = _answerTimeoutSeconds?.Value ?? DefaultAnswerTimeoutSeconds;
            if (n < 0) return 0;
            return n;
        }
        catch
        {
            return DefaultAnswerTimeoutSeconds;
        }
    }

    /// <summary>
    /// Same readable-reason path as a mismatch: log the full text, send Result, then
    /// disconnect 2 s later with the truncated string. A timeout kick backs off if the
    /// client answered in that window; a mismatch kick always proceeds. Could-not-ask
    /// does not send Result while status is Pending/None; it waits the same 2 s, sends
    /// Result, Arms the 20 s answer deadline, then a second 2 s hop disconnects only
    /// when <see cref="Compare.DisconnectReasonAfterCouldNotAskResult"/> says a mismatch
    /// owns the kick (an armed Pending row is not kicked at 4 s). Timeout does not
    /// double-send.
    /// </summary>
    private static void SendResultAndKick(
        World world,
        Entity userEntity,
        int userIndex,
        string name,
        string fullReason,
        bool timeoutKick,
        bool couldNotAsk = false)
    {
        var status = AnswerWatch.GetStatus(userIndex);
        if (couldNotAsk)
        {
            Compare.ScheduleCouldNotAskKick(
                (seconds, action) => Dev.MainThread.PostDelayed(
                    seconds,
                    () =>
                    {
                        try { action(); }
                        catch (Exception e)
                        {
                            Plugin.Logger?.LogError($"Handshake.Disconnect {name}: {e}");
                        }
                    },
                    DeadlineOwner),
                AnswerWatch,
                userIndex,
                fullReason,
                armDeadline: () => ArmAnswerDeadline(world, userEntity, userIndex, name, skip: false),
                sendResult: reason =>
                {
                    Plugin.Logger?.LogWarning($"Handshake: {name} failed: {fullReason}");
                    var delayedWire = Compare.TruncateReason(reason, ResultMessageMaxChars);
                    CustomEvents.SendToUser(world, userEntity, ResultEventId, Result.Create(false, delayedWire));
                },
                disconnect: reason =>
                {
                    var delayedWire = Compare.TruncateReason(reason, ResultMessageMaxChars);
                    bool kicked = Players.PlayerControl.Disconnect(world, userEntity, delayedWire);
                    Plugin.Logger?.LogWarning($"Handshake: disconnected {name}: {kicked}");
                });
            return;
        }

        if (!Compare.ShouldProceedWithKick(status, timeoutKick))
        {
            Plugin.Logger?.LogInfo($"Handshake: {name} already matched; not kicking");
            return;
        }

        Plugin.Logger?.LogWarning($"Handshake: {name} failed: {fullReason}");
        var wire = Compare.TruncateReason(fullReason, ResultMessageMaxChars);
        CustomEvents.SendToUser(world, userEntity, ResultEventId, Result.Create(false, wire));

        Dev.MainThread.PostDelayed(HandshakeAnswerWatch.KickFlushSeconds, () =>
        {
            try
            {
                var later = AnswerWatch.GetStatus(userIndex);
                if (!Compare.ShouldProceedWithKick(later, timeoutKick))
                {
                    Plugin.Logger?.LogInfo($"Handshake: {name} already matched; not kicking");
                    return;
                }
                bool kicked = Players.PlayerControl.Disconnect(world, userEntity, wire);
                Plugin.Logger?.LogWarning($"Handshake: disconnected {name}: {kicked}");
            }
            catch (Exception e)
            {
                Plugin.Logger?.LogError($"Handshake.Disconnect {name}: {e}");
            }
        }, DeadlineOwner);
    }

    private static void BindAnswerTimeout()
    {
        _answerTimeoutSeconds = null;
        _installGraceSeconds = null;
        AnswerTimeoutBound = false;
        // Defaults (DefaultAnswerTimeoutSeconds, DefaultInstallGraceSeconds) hold until it binds.
        SharedConfig.BindWhenAvailable("[Networking] HandshakeAnswerTimeoutSeconds", BindAnswerTimeoutFrom);
    }

    private static void BindAnswerTimeoutFrom(ConfigFile file)
    {
        try
        {
            _answerTimeoutSeconds = ConfigHelper.Bind(
                file,
                "Networking",
                "HandshakeAnswerTimeoutSeconds",
                DefaultAnswerTimeoutSeconds,
                "Seconds the server waits for a handshake answer after asking at a join (a re-ask after a Core generation change of a player who already answered this session never disconnects them). 0: no deadline, so a player whose game never answers (no Progenitor) is never disconnected for it. Only enforced when at least one server plugin is not NoNeedForSync/ClientOnly. Ghosts and hosted-child users with no network peer are never kicked for silence.");
            AnswerTimeoutBound = _answerTimeoutSeconds != null;
            _installGraceSeconds = ConfigHelper.Bind(
                file,
                "Networking",
                "InstallGraceSeconds",
                DefaultInstallGraceSeconds,
                "Job FMAN18. Seconds a client gets to install and hot-load the mods this server offers, when a handshake miss names ONLY mods this server both requires and has on offer. During the window the client may fetch those packages and nothing else, and it must re-send its handshake answer before the window closes. One window per join, never extended. 0: no extra time, every miss kicks immediately, as before.");
            var keepOutEntry = _requireProgenitor = ConfigHelper.Bind(file, "Networking", NoProgenitorKeepOut.ConfigKey, NoProgenitorKeepOut.Default,
                "KEEPOUT1. true (Keep them out): a player whose game never answers the handshake (no Progenitor) gets the Message "
                + "without Progenitor and is disconnected after HandshakeAnswerTimeoutSeconds (a 0 there counts as "
                + NoProgenitorKeepOut.KeepOutMinSeconds + " here), whatever this server's mods need. false (Let them play, the default): "
                + "they are disconnected only when a server mod needs Progenitor on clients or the client mod policy needs an answer.");
            KeepOutProbe = () => keepOutEntry?.Value ?? NoProgenitorKeepOut.Default;
            BindNoAnswerNotice(file);
            BindDisconnectGrace(file);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"Handshake: binding Networking.HandshakeAnswerTimeoutSeconds threw: {e}");
        }
    }
#endif
}
