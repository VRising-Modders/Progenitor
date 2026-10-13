using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Progenitor.Plugins;
#if !PROGENITOR_TESTS
using System.Diagnostics;
using System.Reflection;
using BepInEx;
using BepInEx.Unity.IL2CPP;
using ProjectM;
using ProjectM.Network;
using Progenitor.Ecs;
using Stunlock.Network;
using Unity.Entities;
#endif

namespace Progenitor.Networking;

/// <summary>
/// Who asked to pin this operator. The only legal value is <see cref="LocalUser"/>:
/// a receive handler cannot invent another origin, and <see cref="ModManifest.TrustOperator"/>
/// still refuses while a <see cref="CustomEvents"/> dispatch is on the stack: a pin the
/// player asks for originates on the client. (The automatic pin on first use is
/// <c>ModManifest.PinOnFirstUse</c>, which never re-pins a changed key.)
/// </summary>
public enum TrustOrigin
{
    LocalUser = 0,
}

/// <summary>
/// Signed required-mod catalog the server publishes after a handshake ok, plus the
/// phase-2 trust file (pinned on first use). Servers never send mod files (the
/// server-to-client transfer was removed 2026-10-05): the catalog names Thunderstore
/// packages, and the client installs from Thunderstore through the join screens.
///
/// Ordering after an accepted handshake answer: handshake result, then config
/// sync, then this catalog (one <see cref="OnHandshakeOk"/> from
/// <see cref="Handshake"/>). Receive slots stay with Bootstrap.
/// </summary>
#if !PROGENITOR_TESTS
public static unsafe class ModManifest
#else
public static class ModManifest
#endif
{
    /// <summary>Custom event id <c>"PrmM"</c>. Chunked JSON envelope, same 15 KiB budget as config sync.</summary>
    public const int EventId = 0x50726D4D;

    /// <summary>
    /// Custom event id <c>"PrmR"</c>: the retired client-to-server package request (removed 2026-10-05: servers no
    /// longer send mod files). The server keeps a handler only to drop a stray one with one log line.
    /// </summary>
    public const int RequestEventId = 0x50726D52;

    /// <summary>
    /// Custom event id <c>"PrmC"</c>: the retired server-to-client package chunk (removed 2026-10-05). The client keeps
    /// a handler only to drop one with one log line: an id with no handler would be handed to the game's own reader.
    /// </summary>
    public const int ChunkEventId = 0x50726D43;

    public const string ManifestFileName = "progenitor-manifest.json";
    public const string PrivateKeyFileName = "progenitor-operator.key";
    public const string PublicKeyFileName = "progenitor-operator.pub";
    public const string TrustedFileName = "progenitor-trusted-operators.json";
    public const string StagingFolderName = ModFolders.StagingFolderName;

    /// <summary>Exact client log when <see cref="TrustOperator"/> is invoked from a receive handler.</summary>
    public const string TrustFromReceiveLog =
        "ModManifest: TrustOperator refused: called from a receive handler";

    /// <summary>The one line a dropped <c>PrmR</c> or <c>PrmC</c> packet logs (once per id and Core generation).</summary>
    internal const string RetiredPacketLog =
        "ModManifest: dropped a {0} packet: servers no longer send mod files (mods come only from Thunderstore); further ones are dropped silently";

    /// <summary>The once-per-generation warning of the obsolete <see cref="Offer"/> and <c>Handshake.OfferPackage</c>.</summary>
    internal const string OfferObsoleteLog =
        "ModManifest: OfferPackage({0}) does nothing: servers no longer distribute mod files; publish the mod on Thunderstore "
        + "and name its package in progenitor-manifest.json so players can install it from the join screens";

    private static readonly object Gate = new();
    private static bool _installed;
    private static bool _loggedFingerprint;
    private static ModManifestKey? _key;
    private static ManifestEnvelope? _envelope;

    /// <summary>
    /// Mod compatibility C3: the extended form of <see cref="_envelope"/> (rows carry <c>peers</c> and <c>match</c>),
    /// signed with the same key and <c>issued</c> at every re-sign, for clients that declared
    /// <see cref="Handshake.Capability.VersionRanges"/>. Server only; null whenever <see cref="_envelope"/> is.
    /// </summary>
    private static ManifestEnvelope? _envelopeExtended;

    /// <summary>
    /// Mod compatibility C4: the Progenitor form (the extended form plus the server's own build while Exact is enforced for
    /// Progenitor), for clients that declared <see cref="Handshake.Capability.ProgenitorExact"/>. The same object as
    /// <see cref="_envelopeExtended"/> when there is no requirement.
    /// </summary>
    private static ManifestEnvelope? _envelopeProgenitor;

    private static ManifestDiff? _lastDiff;
    private static string _lastLogBlock = "";
    private static string _catalogStamp = "";

    // Why the catalog was re-signed since the handshake-ok clients last got it ("" when they have it). Live
    // 2026-10-04: an admin's accepted recommend-add re-signed the catalog directly, so the 1 s scan saw no stamp
    // change of its own and resent nothing; the open mods page never showed the row. Every re-sign now leaves this
    // set until the next scan resends the catalog to every handshake-ok client that does not have it yet.
    private static string _resendReason = "";

    /// <summary>Users that have the current envelope (cleared on every re-sign).</summary>
    private static readonly HashSet<int> SentUsers = new();
    private static readonly HashSet<int> HandshakeOkUsers = new();
    private static readonly ModManifestWire.Reassembly Incoming = new();
    private static TrustedOperatorsFile? _trustForJoin;
    private static PendingManifest? _pending;
    private static string _connectHost = "";

    /// <summary>
    /// Operator switch for the deny-time descriptor (job FMAN17). See
    /// <see cref="ModManifestDenyPolicy.DefaultEnabled"/> for why the default is on.
    /// </summary>
#if !PROGENITOR_TESTS
    private static bool _denyDescriptorEnabled = ModManifestDenyPolicy.DefaultEnabled;
#endif

    /// <summary>One "the switch is off" line per Core generation, not one per refused join.</summary>
    private static bool _loggedDenyDisabled;
    private static readonly Dictionary<string, CachedFileHash> FileHashCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Retired packet ids (and obsolete offer ids) already logged this Core generation.</summary>
    private static readonly HashSet<string> LoggedRetired = new(StringComparer.OrdinalIgnoreCase);
    private static bool _legacyCleaned;
    private static bool _loggedConnectHost;
#if !PROGENITOR_TESTS
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static double _nextScan;
    private static IDisposable? _disconnectedSub;
    private static IDisposable? _worldReadySub;
#endif

#if PROGENITOR_TESTS
    /// <summary>Override <see cref="ConfigDirectory"/> so tests never touch BepInEx/config.</summary>
    internal static string? TestConfigDirectory;

    /// <summary>When set, <see cref="EnumerateLoaded"/> returns this instead of walking plugins.</summary>
    internal static IReadOnlyList<LoadedModInfo>? TestLoadedMods;

    internal static bool TestIsServer;

    internal static string? TestConnectHost;

    internal static int TestLastHandshakeOkUser = -1;

    internal static readonly List<int> TestOkHookUsers = new();

    /// <summary>Job FMAN17: the narrowed envelope the last deny descriptor would have put on the wire.</summary>
    internal static ManifestEnvelope? TestDenyEnvelope;

    /// <summary>Job FMAN17: every user index a deny descriptor was sent to.</summary>
    internal static readonly List<int> TestDenyUsers = new();

    /// <summary>Job FMAN17: test override for the operator switch.</summary>
    internal static bool TestDenyDescriptorEnabled = ModManifestDenyPolicy.DefaultEnabled;

    internal static int TestReceiveDispatchDepth;

    internal static int TestHashComputes;

    /// <summary>Lines the retired-packet drop and the obsolete offer logged (tests read them instead of a logger).</summary>
    internal static readonly List<string> TestRetiredLog = new();

    internal static string TestCatalogStamp
    {
        get { lock (Gate) return _catalogStamp; }
    }

    internal static bool TestIsInstalled
    {
        get { lock (Gate) return _installed; }
    }

    /// <summary>C3: the users the tests say declared <see cref="Handshake.Capability.VersionRanges"/> (live: <c>Handshake.CapsOf</c>).</summary>
    internal static readonly HashSet<int> TestRangeUsers = new();

    /// <summary>C4: the users the tests say declared <see cref="Handshake.Capability.ProgenitorExact"/> too.</summary>
    internal static readonly HashSet<int> TestProgenitorUsers = new();

    /// <summary>C3: the envelopes the tests' catalog sends would have put on the wire, by user.</summary>
    internal static readonly List<(int User, ManifestEnvelope Envelope)> TestSent = new();
#endif

    /// <summary>
    /// Directory that holds the four catalog files. Live: BepInEx config. Tests: an
    /// injected temp folder. Never the reload folder — a private key there would be
    /// copied to clients.
    /// </summary>
    public static string ConfigDirectory
    {
        get
        {
#if PROGENITOR_TESTS
            return TestConfigDirectory ?? Path.Combine(Path.GetTempPath(), "progenitor-manifest-test");
#else
            try { return Paths.ConfigPath; }
            catch { return Path.Combine(Path.GetTempPath(), "progenitor-manifest"); }
#endif
        }
    }

    /// <summary>Last signed envelope this process built (server) or received (client).</summary>
    public static ManifestEnvelope? LastEnvelope
    {
        get { lock (Gate) return _envelope; }
    }

    /// <summary>Last client classification, or null on the server until a compare is requested.</summary>
    public static ManifestDiff? LastDiff
    {
        get { lock (Gate) return _lastDiff; }
    }

    /// <summary>
    /// Last catalog this client accepted this join, with the operator fingerprint,
    /// first-contact / key-change flags, and missing/mismatched lists. Null on the
    /// server and before the first valid envelope. A UI or <c>.mods</c> command
    /// reads this; it is never an auto-trust signal.
    /// </summary>
    public static PendingManifest? Pending
    {
        get { lock (Gate) return _pending; }
    }

    /// <summary>
    /// Readable dump for a later bridge op. Server: current catalog. Client: last
    /// received envelope plus present / missing / mismatch / restart-required.
    /// Safe to call before <see cref="Install"/>; it will load keys and build.
    /// </summary>
    public static string Describe()
    {
        try
        {
            EnsureInstalled();
            ManifestEnvelope? envelope;
            ManifestDiff? diff;
            bool server;
            lock (Gate)
            {
                envelope = _envelope;
                diff = _lastDiff;
            }
            server = IsServerSide();
            if (server)
            {
                RebuildCatalog();
                lock (Gate) envelope = _envelope;
            }
            if (!server && envelope != null && diff == null)
            {
                diff = CompareReceived(envelope);
                lock (Gate) _lastDiff = diff;
            }
            return ModManifestCompare.FormatDescribe(server, envelope, diff);
        }
        catch (Exception e)
        {
            LogWarning("ModManifest.Describe: " + e.Message);
            return "ModManifest: describe failed: " + e.Message;
        }
    }

    /// <summary>
    /// Attach receive + frame tick, generate the operator key on first run, and
    /// build the catalog. Idempotent. <see cref="Plugin.Uninstall"/> calls
    /// <see cref="Uninstall"/> so a same-ALC restore cannot keep a stale
    /// <c>_installed</c> flag or receive handler.
    /// </summary>
    internal static void Install()
    {
        if (HarmonySlotHandoff.ShouldSkipModule(HarmonySlotHandoff.NetworkingModule))
            return;
        bool first;
        lock (Gate)
        {
            first = !_installed;
            _installed = true;
        }
        Handshake.ServerContextCleared -= OnClientServerContextCleared;
        if (!IsServerSide()) Handshake.ServerContextCleared += OnClientServerContextCleared;
#if !PROGENITOR_TESTS
        Plugin.Frame -= OnFrame;
        Plugin.Frame += OnFrame;
        try
        {
            if (!Plugin.IsServerProcess)
            {
                CustomEvents.OnReceive(EventId, OnPacket);
                // 2026-10-05: the retired chunk id keeps a handler that drops it (one log line), so an older
                // server's chunk is never handed to the game's own reader.
                CustomEvents.OnReceive(ChunkEventId, OnRetiredChunkPacket);
                _worldReadySub?.Dispose();
                _worldReadySub = Events.EventBus.On<Events.WorldReady>(OnClientWorldReady, priority: 900);
            }
            else
            {
                // 2026-10-05: an older client's package request is dropped (one log line); nothing is ever sent.
                CustomEvents.OnReceiveFromClient(RequestEventId, OnRetiredRequestPacket);
                // The install window's Thunderstore fix: the server's read of the index for its catalog's packages.
                try { Handshake.ServerPackages ??= JoinFlowLive.CreateServerPackages(); }
                catch (Exception e) { LogWarning("ModManifest: the install window's Thunderstore lookup is not available: " + e.Message); }
                _disconnectedSub?.Dispose();
                _disconnectedSub = Events.EventBus.On<Events.PlayerDisconnected>(OnPlayerDisconnected, priority: 900);
            }
        }
        catch (Exception e)
        {
            LogError("ModManifest.Install receive: " + e.Message);
        }
        BindDenyDescriptorSwitch();
#endif
#if !PROGENITOR_TESTS
        // SERVERDESC1: the server-list description rows (bound before the page that places them) and, on the
        // server, the apply loop that publishes the description at start and after each edit.
        try { ServerListDescriptionSettings.Install(); }
        catch (Exception e) { LogWarning("ModManifest: server list description install threw: " + e.Message); }
        // CHAT4: the Server tab's Chat rows (bound before the page that places them), the client's ChatPolicy from the
        // synced values and, on the server, the word filter and the Discord relay.
        try { Progenitor.Chat.ServerChat.Install(); }
        catch (Exception e) { LogWarning("ModManifest: server chat settings install threw: " + e.Message); }
        // Server: clear combat music that no unit targeting a player carries any more (a boss left without the game's
        // "stopped targeting" event).
        try { Progenitor.Combat.CombatMusicRepairServer.Install(); }
        catch (Exception e) { LogWarning("ModManifest: combat music repair install threw: " + e.Message); }
#endif
        // JOINFLOW1: the server message rows (both sides: the server signs them into the catalog, the
        // client draws Progenitor's settings page and edits them as an admin). Before the first build.
        try { JoinServerSettings.Install(); }
        catch (Exception e) { LogWarning("ModManifest: server message settings install threw: " + e.Message); }
        // JOINADMIN1 (join flow stage 3): the admin's "Players joining" channel — server: the store and the
        // refused-join record; client: the panel's request channel.
#if !PROGENITOR_TESTS
        // Review fix 5: the lock list binds on its own, before any edit channel; until it is bound every edit is refused.
        try { ServerSettingsLock.Install(); }
        catch (Exception e) { LogWarning("ModManifest: settings lock list install threw: " + e.Message); }
#endif
        try { JoinAdminServer.Install(); }
        catch (Exception e) { LogWarning("ModManifest: join admin install threw: " + e.Message); }
        // Settings organization stages 6 and 7: the Server rules channel ('PrSv').
        try { ServerRulesServer.Install(); }
        catch (Exception e) { LogWarning("ModManifest: server rules install threw: " + e.Message); }
#if !PROGENITOR_TESTS
        try { ItemManagementChannel.Install(); } catch (Exception e) { LogWarning("ModManifest: item management install threw: " + e.Message); }
        try { JoinAdminClient.Install(); }
        catch (Exception e) { LogWarning("ModManifest: join admin client install threw: " + e.Message); }
        try { ServerRulesClient.Install(); }
        catch (Exception e) { LogWarning("ModManifest: server rules client install threw: " + e.Message); }
#endif

        try
        {
            EnsureKey(logFingerprint: first || !_loggedFingerprint);
            if (IsServerSide()) RebuildCatalog();
        }
        catch (Exception e)
        {
            LogError("ModManifest.Install: " + e);
        }

        // Client only. 2026-10-05: servers no longer send mod files. What an older Core installed because a server
        // sent it is removed once per Core generation (LegacyServerMods explains why a cleanup and not a Remove
        // list), before the join screens install the version store, which then puts the player's kept copy back
        // where a server's copy was active.
        if (!IsServerSide())
        {
            CleanUpLegacyServerModsOnce();
#if !PROGENITOR_TESTS
            // FMAN17: the disconnect-screen panel. JOINFLOW1: the join screens replace that read-only panel;
            // ModManifestDenyPanel stays as their fallback when they cannot draw.
            try { JoinFlowPanel.Install(); }
            catch (Exception e) { LogWarning("ModManifest: join screens install threw: " + e.Message); }
            // Per-server mod pause notice: non-modal, same lifecycle as the deny panel.
            try { ServerModPauseNotice.Install(); }
            catch (Exception e) { LogWarning("ModManifest: mod pause notice install threw: " + e.Message); }
            // JOINNOTICE: the server-change notice, same pattern.
            try { JoinChangeNoticePanel.Install(); }
            catch (Exception e) { LogWarning("ModManifest: server change notice install threw: " + e.Message); }
            // AUTOUPDATE3: the admin's server update notice, same pattern.
            try { UpdateNoticePanel.Install(); }
            catch (Exception e) { LogWarning("ModManifest: server update notice install threw: " + e.Message); }
#endif
        }
    }

    /// <summary>The one-time cleanup of server-sent installs (client, once per Core generation; never throws).</summary>
    private static void CleanUpLegacyServerModsOnce()
    {
        lock (Gate)
        {
            if (_legacyCleaned) return;
            _legacyCleaned = true;
        }

        try { LegacyServerMods.CleanUpAtStart(LogInfo, LogWarning); }
        catch (Exception e) { LogWarning("ModManifest: legacy server mods cleanup threw: " + e.Message); }
    }

    internal static void Uninstall()
    {
        Handshake.ServerContextCleared -= OnClientServerContextCleared;
        try { ModManifestDeny.OnJoinEnded(); }
        catch (Exception e) { LogWarning("ModManifest: deny teardown threw: " + e.Message); }
        // JOINFLOW1: the join screens' state and the server message rows' registrations go with Core.
        try { JoinFlowClient.Uninstall(); }
        catch (Exception e) { LogWarning("ModManifest: join screens teardown threw: " + e.Message); }
        try { JoinServerSettings.Uninstall(); }
        catch (Exception e) { LogWarning("ModManifest: server message settings teardown threw: " + e.Message); }
#if !PROGENITOR_TESTS
        try { ServerListDescriptionSettings.Uninstall(); }
        catch (Exception e) { LogWarning("ModManifest: server list description teardown threw: " + e.Message); }
        try { Progenitor.Chat.ServerChat.Uninstall(); }
        catch (Exception e) { LogWarning("ModManifest: server chat settings teardown threw: " + e.Message); }
        try { Progenitor.Combat.CombatMusicRepairServer.Uninstall(); }
        catch (Exception e) { LogWarning("ModManifest: combat music repair teardown threw: " + e.Message); }
#endif
        try { JoinAdminServer.Uninstall(); }
        catch (Exception e) { LogWarning("ModManifest: join admin teardown threw: " + e.Message); }
        try { ServerRulesServer.Uninstall(); }
        catch (Exception e) { LogWarning("ModManifest: server rules teardown threw: " + e.Message); }
#if !PROGENITOR_TESTS
        try { ServerSettingsLock.Uninstall(); }
        catch (Exception e) { LogWarning("ModManifest: settings lock list teardown threw: " + e.Message); }
#endif
#if !PROGENITOR_TESTS
        try { ItemManagementChannel.Uninstall(); } catch (Exception e) { LogWarning("ModManifest: item management uninstall threw: " + e.Message); }
        try { JoinAdminClient.Uninstall(); }
        catch (Exception e) { LogWarning("ModManifest: join admin client teardown threw: " + e.Message); }
        try { ServerRulesClient.Uninstall(); }
        catch (Exception e) { LogWarning("ModManifest: server rules client teardown threw: " + e.Message); }
        try { JoinFlowPanel.Uninstall(); }
        catch (Exception e) { LogWarning("ModManifest: join screens uninstall threw: " + e.Message); }
        try { ModManifestDenyPanel.Uninstall(); }
        catch (Exception e) { LogWarning("ModManifest: deny panel uninstall threw: " + e.Message); }
        try { ServerModPauseNotice.Uninstall(); }
        catch (Exception e) { LogWarning("ModManifest: mod pause notice uninstall threw: " + e.Message); }
        try { JoinChangeNoticePanel.Uninstall(); }
        catch (Exception e) { LogWarning("ModManifest: server change notice uninstall threw: " + e.Message); }
        try { UpdateNoticePanel.Uninstall(); }
        catch (Exception e) { LogWarning("ModManifest: server update notice uninstall threw: " + e.Message); }
        try { Plugin.Frame -= OnFrame; }
        catch { /* teardown */ }
        try { _disconnectedSub?.Dispose(); }
        catch { /* teardown */ }
        _disconnectedSub = null;
        try { _worldReadySub?.Dispose(); }
        catch { /* teardown */ }
        _worldReadySub = null;
        try { CustomEvents.RemoveHandler(EventId); }
        catch { /* teardown */ }
        try { CustomEvents.RemoveHandler(ChunkEventId); }
        catch { /* teardown */ }
        try { CustomEvents.RemoveClientHandler(RequestEventId); }
        catch { /* teardown */ }
        // The server's Thunderstore index reader owns an HTTP handler; release it with this generation.
        try { Handshake.ServerPackages?.Dispose(); }
        catch { /* teardown */ }
        Handshake.ServerPackages = null;
#endif
        lock (Gate)
        {
            _installed = false;
            _loggedFingerprint = false;
            _loggedDenyDisabled = false;
            _key?.Dispose();
            _key = null;
            _envelope = null;
            _envelopeExtended = null;
            _envelopeProgenitor = null;
            _lastDiff = null;
            _lastLogBlock = "";
            _catalogStamp = "";
            _resendReason = "";
            _pending = null;
            _trustForJoin = null;
            _connectHost = "";
            SentUsers.Clear();
            HandshakeOkUsers.Clear();
            Incoming.Reset();
            FileHashCache.Clear();
            LoggedRetired.Clear();
            _legacyCleaned = false;
            _loggedConnectHost = false;
            ModManifestCompare.ResetFpOnlyLog();
#if PROGENITOR_TESTS
            TestLastHandshakeOkUser = -1;
            TestOkHookUsers.Clear();
            TestDenyEnvelope = null;
            TestDenyUsers.Clear();
            TestDenyDescriptorEnabled = ModManifestDenyPolicy.DefaultEnabled;
            TestReceiveDispatchDepth = 0;
            TestHashComputes = 0;
            TestRetiredLog.Clear();
            TestRangeUsers.Clear();
            TestProgenitorUsers.Clear();
            TestSent.Clear();
#endif
        }
#if PROGENITOR_TESTS
        ModManifestDeny.ResetForTests();
        LegacyServerMods.TestReloadDirectory = null;
#endif
    }

    /// <summary>
    /// True while a <c>CustomEvents</c> receive handler is on this thread's stack. A decision (a trust pin, a
    /// Thunderstore confirmation) refused from there must originate on the client, not inside a packet the server sent.
    /// </summary>
    internal static bool InReceiveDispatch => IsReceiveDispatchInFlight();

    /// <summary>
    /// Locally derived folder / log key for the server of this join (operator fingerprint, else the connect address,
    /// else <c>unknown</c>).
    /// </summary>
    internal static string HostKeyForJoin()
    {
        PendingManifest? pending;
        lock (Gate) pending = _pending;
        string fp = pending?.Fingerprint ?? "";
        if (fp.Length > 0) return ModManifestIds.NormalizeHostKey(fp.Length > 16 ? fp.Substring(0, 16) : fp);
        return ModManifestIds.NormalizeHostKey(CurrentHost());
    }

    /// <summary>True on the dedicated server.</summary>
    internal static bool IsServerProcessSide() => IsServerSide();

#if PROGENITOR_TESTS
    internal static void ResetForTests() => Uninstall();

    /// <summary>
    /// Client world-change / disconnect seam used by tests. Same clear as live
    /// <c>WorldReady</c> on <c>Client_0</c>.
    /// </summary>
    internal static void NotifyClientWorldChanged() => ClearJoinState();

    internal static void AcceptCatalogPacketForTests(byte[] packet) => AcceptCatalogPacket(packet);

    /// <summary>Tests: what a retired <c>PrmR</c> / <c>PrmC</c> packet arriving now does (the live handlers call this).</summary>
    internal static void DropRetiredPacketForTests(int eventId, int length) => DropRetiredPacket(eventId, length);

    /// <summary>Tests: the one-time cleanup again in this generation.</summary>
    internal static void CleanUpLegacyForTests()
    {
        lock (Gate) _legacyCleaned = false;
        CleanUpLegacyServerModsOnce();
    }
#endif

    /// <summary>
    /// Handshake hook: the join passed. Called once from <see cref="Handshake"/>
    /// after <see cref="ConfigSync.OnHandshakeOk"/> so the wire order is handshake
    /// result, then config snapshot, then this catalog. Also the test fake path
    /// <c>Handshake.NotifyHandshakeOk</c>.
    /// </summary>
    internal static void OnHandshakeOk(int userIndex)
    {
        try
        {
            EnsureInstalled();
            if (!IsServerSide()) return;
            lock (Gate)
            {
                HandshakeOkUsers.Add(userIndex);
#if PROGENITOR_TESTS
                TestLastHandshakeOkUser = userIndex;
                TestOkHookUsers.Add(userIndex);
#endif
            }
            RebuildCatalog();
            SendToUserIndex(userIndex, force: true);
        }
        catch (Exception e)
        {
            LogError("ModManifest.OnHandshakeOk: " + e.Message);
        }
    }

    /// <summary>
    /// Handshake hook: the join was <b>refused</b> over a mod mismatch (job FMAN17). Called once
    /// from <see cref="Handshake"/>'s failure branch, synchronously, before the
    /// <c>KickFlushSeconds</c> hop that disconnects — so the descriptor rides the same live
    /// connection the <c>Result</c> packet just went out on, and lands after it because reliable
    /// events keep order.
    ///
    /// <para><b>This grants the catalog and nothing else.</b> The user index is deliberately
    /// <em>not</em> added to <c>HandshakeOkUsers</c>: that set is the only thing
    /// the module consults before it treats a peer as joined, and it stays
    /// exactly as it was. There is no
    /// second authorization set and no change to the kick's timing.</para>
    ///
    /// <para><b>The rows are narrowed to what this client actually failed</b>
    /// (<c>Compare.ServerEnforcedFailures</c>, computed by the caller from the same two tables the
    /// join check ran on). That is both the smaller disclosure — a stranger does not receive the
    /// operator's whole inventory — and the more useful list.</para>
    ///
    /// <para>Gated by the operator switch <see cref="ModManifestDenyPolicy.ConfigKey"/>; see that
    /// constant for why the default is on. Never throws: it decorates a disconnect that is
    /// happening anyway.</para>
    /// </summary>
    internal static void OnHandshakeDenied(int userIndex, IReadOnlyList<string>? failedIds) =>
        OnHandshakeDenied(userIndex, failedIds, joinFlowClient: false);

    /// <summary>
    /// Same, for a client that declared <c>Handshake.Capability.JoinFlow</c>: when no failing row has a
    /// catalog row but the catalog carries a join section (the server message), the client is sent that
    /// section alone in a zero-row descriptor (JOINFLOW1). A zero-row descriptor adds no row to anything:
    /// the deny report merges rows by id and has none to merge.
    /// </summary>
    internal static void OnHandshakeDenied(int userIndex, IReadOnlyList<string>? failedIds, bool joinFlowClient) =>
        OnHandshakeDenied(userIndex, failedIds, joinFlowClient, judgedProgenitor: null);

    /// <summary>
    /// Mod compatibility C4: the same, for a refusal over this server's Progenitor build. <paramref name="judgedProgenitor"/>
    /// is the requirement the join was judged with; a Progenitor-form client gets it in the descriptor even when the signed
    /// catalog has not caught up with it yet (Exact turned on, or the build became obtainable, within the status cache).
    /// </summary>
    internal static void OnHandshakeDenied(int userIndex, IReadOnlyList<string>? failedIds, bool joinFlowClient, ProgenitorRequirement? judgedProgenitor)
    {
        try
        {
            EnsureInstalled();
            if (!IsServerSide()) return;
            if (!DenyDescriptorEnabled)
            {
                WarnDenyDescriptorOffOnce();
                return;
            }

            RebuildCatalog();
            // C3: narrowed from the form this client reads (the extended one only for a VersionRanges client).
            var full = CatalogFor(userIndex);
            ModManifestKey? key;
            lock (Gate) key = _key;
            // C4 (review fix): the requirement the join was judged with, signed into this client's form when the catalog
            // does not carry it (yet).
            if (judgedProgenitor != null && UserReadsProgenitor(userIndex))
                full = WithJudgedProgenitor(full, judgedProgenitor, key);
            var narrowed = ModManifestDenyPolicy.Narrow(full, failedIds, key);
            if (narrowed == null && joinFlowClient)
                narrowed = ModManifestDenyPolicy.JoinOnly(full, key);
            if (narrowed == null)
            {
                LogInfo(ModDenyLog.NothingToSend(userIndex));
                return;
            }

            SendDenyDescriptor(userIndex, narrowed);
            LogInfo(ModDenyLog.Sent(userIndex, narrowed.Body.Mods.Count, narrowed.Body.Operator.Fp)
                + (narrowed.Join != null ? " with the join section" : ""));
        }
        catch (Exception e)
        {
            LogWarning("ModManifest.OnHandshakeDenied: " + e.Message);
        }
    }

    /// <summary>
    /// Handshake hook: the join failed over mods this client can fix during the join (from Thunderstore, a kept copy,
    /// Turn on, or a pause), so the kick was <b>deferred</b> into an install window (job FMAN18, retargeted 2026-10-05).
    /// Builds the same narrowed, signed descriptor the deny path sends — the failing rows with their Thunderstore
    /// packages, and the join section (the message, the client mod policy) — which the client's join screens are built
    /// from. When no failing id has a catalog row (a client policy problem), a client that draws the join screens gets
    /// the join section alone.
    ///
    /// <para>Returns <b>false</b> when nothing can be sent (not server-side, the operator switch is off, or there is
    /// nothing to describe). An install window with no descriptor behind it is a minute of waiting followed by the
    /// same disconnect, so <c>Handshake</c> asks this <em>before</em> it opens a window or sends a deferred
    /// <c>Result</c> and kicks straight away on a false (job FMAN20, R50 finding 8).</para>
    ///
    /// <para>The user index is not added to <see cref="HandshakeOkUsers"/>: the descriptor grants nothing. Nothing is
    /// ever transferred; the client installs from Thunderstore.</para>
    /// </summary>
    internal static bool TryBuildInstallDescriptor(
        int userIndex,
        IReadOnlyList<string>? failingIds,
        bool joinFlowClient,
        out ManifestEnvelope? descriptor)
    {
        descriptor = null;
        try
        {
            EnsureInstalled();
            if (!IsServerSide()) return false;
            if (!DenyDescriptorEnabled)
            {
                WarnDenyDescriptorOffOnce();
                return false;
            }

            RebuildCatalog();
            // C3: narrowed from the form this client reads.
            var full = CatalogFor(userIndex);
            ModManifestKey? key;
            lock (Gate) key = _key;
            var narrowed = ModManifestDenyPolicy.Narrow(full, failingIds, key);
            if (narrowed == null && joinFlowClient)
                narrowed = ModManifestDenyPolicy.JoinOnly(full, key);
            if (narrowed == null)
            {
                LogInfo(ModDenyLog.NothingToSend(userIndex));
                return false;
            }

            descriptor = narrowed;
            return true;
        }
        catch (Exception e)
        {
            LogWarning("ModManifest.TryBuildInstallDescriptor: " + e.Message);
            descriptor = null;
            return false;
        }
    }

    /// <summary>
    /// Send a descriptor <see cref="TryBuildInstallDescriptor"/> already produced. Separate from the build so the caller
    /// can decide sendability <em>before</em> it promises the client a window (job FMAN20, R50 finding 8).
    /// </summary>
    internal static void SendInstallDescriptor(int userIndex, ManifestEnvelope? descriptor)
    {
        if (descriptor == null) return;
        try
        {
            SendDenyDescriptor(userIndex, descriptor);
            LogInfo("ModManifest: install window descriptor to user " + userIndex.ToString(CultureInfo.InvariantCulture)
                + ": " + descriptor.Body.Mods.Count.ToString(CultureInfo.InvariantCulture)
                + " mod(s), operator " + descriptor.Body.Operator.Fp);
        }
        catch (Exception e)
        {
            LogWarning("ModManifest.SendInstallDescriptor: " + e.Message);
        }
    }

    /// <summary>
    /// Pack and push one envelope to a user index without touching <see cref="SentUsers"/>: the
    /// deny descriptor is not the join catalog and must not make the module think this user has
    /// been served one.
    /// </summary>
    private static void SendDenyDescriptor(int userIndex, ManifestEnvelope envelope)
    {
#if PROGENITOR_TESTS
        TestDenyEnvelope = envelope;
        TestDenyUsers.Add(userIndex);
#else
        var world = Worlds.Server;
        if (world == null || !world.IsCreated) return;
        var bootstrap = world.GetExistingSystemManaged<ServerBootstrapSystem>();
        if (bootstrap == null) return;
        string json = ModManifestJson.CanonicalEnvelope(envelope);
        List<byte[]> packets;
        try { packets = ModManifestWire.Pack(ModManifestCrypto.Utf8(json)); }
        catch (Exception e)
        {
            LogWarning("ModManifest: deny descriptor pack failed: " + e.Message);
            return;
        }
        for (int i = 0; i < packets.Count; i++)
        {
            try { CustomEvents.SendToUser(bootstrap, userIndex, EventId, packets[i]); }
            catch (Exception e) { LogWarning("ModManifest: deny descriptor send threw: " + e.Message); }
        }
#endif
    }

    /// <summary>
    /// Say once per Core generation that the switch is off. Once, because a busy server refuses
    /// the same client repeatedly and the operator needs the sentence, not the count.
    /// </summary>
    private static void WarnDenyDescriptorOffOnce()
    {
        lock (Gate)
        {
            if (_loggedDenyDisabled) return;
            _loggedDenyDisabled = true;
        }
        LogInfo(ModDenyLog.Disabled());
    }

    /// <summary>
    /// Remember this operator. First contact and a host key-change never write
    /// themselves; this method is the only writer of
    /// <c>progenitor-trusted-operators.json</c>. <paramref name="fingerprint"/> must
    /// match <see cref="Pending"/> when a catalog is in flight. Origin must be
    /// <see cref="TrustOrigin.LocalUser"/>; a receive-handler call is refused.
    /// <paramref name="always"/> false is session-only (in-memory pin, not written).
    /// </summary>
    public static bool TrustOperator(string fingerprint, bool always) =>
        TrustOperator(fingerprint, always, TrustOrigin.LocalUser);

    /// <inheritdoc cref="TrustOperator(string, bool)"/>
    public static bool TrustOperator(string fingerprint, bool always, TrustOrigin origin)
    {
        try
        {
            if (IsReceiveDispatchInFlight())
            {
                LogWarning(TrustFromReceiveLog);
                return false;
            }
            if (origin != TrustOrigin.LocalUser)
            {
                LogWarning("ModManifest: TrustOperator refused: origin is not LocalUser");
                return false;
            }

            EnsureInstalled();
            fingerprint = (fingerprint ?? "").Trim().ToLowerInvariant();
            if (fingerprint.Length != ModManifestCrypto.FingerprintHexLength)
            {
                LogWarning("ModManifest: TrustOperator refused (fingerprint must be "
                    + ModManifestCrypto.FingerprintHexLength.ToString(CultureInfo.InvariantCulture)
                    + " hex chars)");
                return false;
            }

            PendingManifest? pending;
            string host;
            lock (Gate)
            {
                pending = _pending;
                host = pending?.Host ?? CurrentHost();
            }

            string spki = "";
            string issued = "";
            if (pending != null)
            {
                if (!string.Equals(pending.Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase))
                {
                    LogWarning("ModManifest: TrustOperator fingerprint "
                        + fingerprint + " does not match pending " + pending.Fingerprint);
                    return false;
                }
                spki = pending.Spki ?? "";
                issued = pending.Envelope.Body.Issued ?? "";
                if (host.Length == 0) host = pending.Host;
            }

            if (string.IsNullOrEmpty(spki))
            {
                LogWarning("ModManifest: TrustOperator refused (no SPKI on the pending catalog)");
                return false;
            }

            string path = Path.Combine(ConfigDirectory, TrustedFileName);
            TrustedOperatorsFile memory;
            lock (Gate) memory = _trustForJoin ?? ModManifestTrust.ReadFile(path);

            // R27 finding 23: a session request must not downgrade a pin that is already on
            // disk for this host+key — the report would then claim "(trusted, session)" for a
            // pin that persists.
            var existing = ModManifestTrust.FindPin(memory, host, spki, fingerprint);
            if (!always && existing != null && existing.Always)
            {
                LogInfo("ModManifest: operator fp=" + fingerprint + " is already trusted always=true"
                    + (host.Length > 0 ? " host=" + host : " host-less") + "; session pin not written");
                RefreshPendingReport(host);
                return true;
            }

            var now = DateTime.UtcNow;
            if (always)
            {
                // Write the disk file from disk (never from memory: session pins must not leak
                // onto it), then upsert the same row into the in-memory file so session pins
                // for other hosts survive the write.
                var onDisk = ModManifestTrust.Upsert(
                    ModManifestTrust.ReadFile(path), host, fingerprint, spki, true, now, issued);
                ModManifestTrust.WriteAtomic(path, onDisk);
            }
            var updated = ModManifestTrust.Upsert(memory, host, fingerprint, spki, always, now, issued);
            lock (Gate) _trustForJoin = updated;
            RefreshPendingReport(host);
            LogInfo("ModManifest: trusted operator fp=" + fingerprint
                + (always ? " always=true" : " always=false (session)")
                + (host.Length > 0 ? " host=" + host : " host-less"));
            return true;
        }
        catch (Exception e)
        {
            LogError("ModManifest.TrustOperator: " + e.Message);
            return false;
        }
    }

    /// <summary>Recompute <see cref="Pending"/> and the log block after a trust change.</summary>
    private static void RefreshPendingReport(string host)
    {
        lock (Gate)
        {
            if (_envelope == null) return;
            var diff = CompareReceived(_envelope);
            _lastDiff = diff;
            _pending = new PendingManifest(_envelope, diff, host, diff.PreviousFingerprint);
            _lastLogBlock = _pending.FormatReport();
        }
    }

    /// <summary>
    /// Record the address this client used to join so trust pins are host-keyed.
    /// Called from the handshake-ok seam and from <see cref="CaptureConnectHostFromClient"/>.
    /// Logs once per join.
    /// </summary>
    public static void SetConnectHost(string host)
    {
        host = (host ?? "").Trim();
        bool log = false;
        lock (Gate)
        {
            if (string.Equals(_connectHost, host, StringComparison.Ordinal))
            {
                if (host.Length > 0 && !_loggedConnectHost)
                {
                    _loggedConnectHost = true;
                    log = true;
                }
            }
            else
            {
                _connectHost = host;
                if (host.Length > 0 && !_loggedConnectHost)
                {
                    _loggedConnectHost = true;
                    log = true;
                }
            }
        }
        if (log)
            LogInfo("ModManifest: connect host " + host);
    }

    /// <summary>
    /// Live writer for the host seam. Reads <c>ClientBootstrapSystem.ConnectionString</c> /
    /// <c>LastConnectionData.ConnectAddress</c> (client metadata: <c>ClientBootstrapSystem</c> fields
    /// <c>ConnectionString</c> at 0xD0 and <c>LastConnectionData</c>; <c>ConnectAddress.Address</c>
    /// + <c>Port</c> — never <c>ConnectAddress.ToString()</c>, the interop wrapper trap).
    /// Handshake <c>OnResult</c> calls this after an accepted join.
    /// </summary>
    internal static void CaptureConnectHostFromClient()
    {
        // Review 2026-10-07: a join's result: this PC's server facts refresh (in the background) for its route.
        LocalFolderFactsCache.RefreshAll();
        // LOCALDETECT1: and the auto-detected local server for the joined port.
        Progenitor.Install.LocalServerDetectCache.RefreshAll();
        try
        {
            if (IsServerSide()) return;
            string host = ReadLiveConnectHost();
            if (host.Length == 0) return;
            SetConnectHost(host);
        }
        catch (Exception e)
        {
            LogWarning("ModManifest: capturing connect host threw: " + e.Message);
        }
    }

    /// <summary>
    /// Removed 2026-10-05: servers no longer distribute mod files; mods come only from Thunderstore. Kept so a plugin
    /// built against an older Core still loads and runs: it registers nothing, logs one line per mod id and Core
    /// generation, and returns a handle whose <c>Dispose</c> does nothing. Publish the mod on Thunderstore and name its
    /// package in <c>progenitor-manifest.json</c> instead.
    /// </summary>
    [Obsolete("Servers no longer distribute mod files; publish the mod on Thunderstore. This call does nothing.")]
    public static IDisposable Offer(string modId, string path)
    {
        NoteObsoleteOffer(modId);
        return NoOfferHandle.Instance;
    }

    /// <summary>
    /// Removed 2026-10-05 with <see cref="Offer"/>: always false, with <paramref name="reason"/>
    /// <c>"removed"</c>, an empty hash and size 0.
    /// </summary>
    [Obsolete("Servers no longer distribute mod files; publish the mod on Thunderstore. This call always answers false.")]
    public static bool TryGetOfferTransferState(
        string modId, out string sha256, out long size, out string reason)
    {
        sha256 = "";
        size = 0;
        reason = "removed";
        return false;
    }

    /// <summary>The one line an obsolete offer logs, once per mod id and Core generation (never throws).</summary>
    internal static void NoteObsoleteOffer(string? modId)
    {
        try
        {
            string id = (modId ?? "").Trim();
            lock (Gate)
            {
                if (!LoggedRetired.Add("offer:" + id)) return;
            }

            string line = string.Format(CultureInfo.InvariantCulture, OfferObsoleteLog, id.Length > 0 ? id : "?");
#if PROGENITOR_TESTS
            lock (Gate) TestRetiredLog.Add(line);
#endif
            LogWarning(line);
        }
        catch
        {
            // A plugin's obsolete call must never fail its load.
        }
    }

    private sealed class NoOfferHandle : IDisposable
    {
        public static readonly NoOfferHandle Instance = new();

        public void Dispose()
        {
        }
    }

    /// <summary>
    /// A <c>PrmR</c> (server) or <c>PrmC</c> (client) packet arrived: dropped, with one log line per id and Core
    /// generation. Nothing in it is read.
    /// </summary>
    private static void DropRetiredPacket(int eventId, int length)
    {
        try
        {
            string name = eventId == RequestEventId ? "PrmR" : eventId == ChunkEventId ? "PrmC" : "0x" + eventId.ToString("x8", CultureInfo.InvariantCulture);
            lock (Gate)
            {
                if (!LoggedRetired.Add("packet:" + name)) return;
            }

            string line = string.Format(CultureInfo.InvariantCulture, RetiredPacketLog, name)
                + " (" + length.ToString(CultureInfo.InvariantCulture) + " bytes)";
#if PROGENITOR_TESTS
            lock (Gate) TestRetiredLog.Add(line);
#endif
            LogInfo(line);
        }
        catch
        {
            // A dropped packet logs at most; it never throws into the dispatcher.
        }
    }

    /// <summary>
    /// HOSTKEY1: after a catalog's signature and replay checks passed, move the trust rows this key has under relay strings (written before
    /// the host key was the dialed address) to <paramref name="host"/> (<see cref="ModManifestTrust.AdoptRelayRows"/>), on
    /// disk and in this join's copy. True when anything moved. An unreadable trust file is left alone.
    /// </summary>
    internal static bool AdoptRelayTrust(ManifestEnvelope envelope, string host)
    {
        try
        {
            if (envelope?.Body?.Operator == null) return false;
            host = (host ?? "").Trim();
            string spki = envelope.Body.Operator.Spki ?? "";
            string fp = envelope.Body.Operator.Fp ?? "";
            if (!ConnectHostKey.IsStable(host) || spki.Length == 0) return false;

            string path = Path.Combine(ConfigDirectory, TrustedFileName);
            TrustedOperatorsFile onDisk = new();
            if (File.Exists(path))
            {
                bool parsed;
                try { parsed = ModManifestJson.TryParseTrust(File.ReadAllText(path), out onDisk, out _); }
                catch { parsed = false; }
                if (!parsed) return false;
            }

            var movedDisk = ModManifestTrust.AdoptRelayRows(onDisk, host, spki, fp);
            TrustedOperatorsFile memory;
            lock (Gate) memory = _trustForJoin ?? onDisk;
            var movedMemory = ModManifestTrust.AdoptRelayRows(memory, host, spki, fp);
            if (ReferenceEquals(movedDisk, onDisk) && ReferenceEquals(movedMemory, memory)) return false;
            if (!ReferenceEquals(movedDisk, onDisk)) ModManifestTrust.WriteAtomic(path, movedDisk);
            lock (Gate) _trustForJoin = movedMemory;
            LogInfo("ModManifest: moved operator fp=" + fp + " trust rows from per-connection relay keys to host=" + host);
            return true;
        }
        catch (Exception e)
        {
            LogWarning("ModManifest: moving relay-keyed trust rows threw: " + e.Message);
            return false;
        }
    }

    /// <summary>
    /// Trust on first use (2026-10-05). With the consent panel gone, nothing else pins an operator, so the first
    /// verified catalog from an address this client has <b>no pin at all</b> for pins that address's key (always,
    /// with the catalog's <c>issued</c>). From then on the pin works as before: a replayed (older) catalog is refused,
    /// and a catalog signed by another key for this address is flagged as a key change and is <b>not</b> re-pinned
    /// here (the key-changed warning the join screens show stays). A trust file that exists but cannot be read is never
    /// rewritten. Returns true when a pin was written. Never throws.
    /// </summary>
    internal static bool PinOnFirstUse(ManifestEnvelope envelope, ManifestDiff diff, string host)
    {
        try
        {
            if (envelope?.Body?.Operator == null || diff == null) return false;
            if (!diff.SignatureValid || diff.Replayed || diff.OperatorTrusted) return false;
            if (!string.IsNullOrEmpty(diff.PreviousFingerprint)) return false;
            host = (host ?? "").Trim();
            string spki = envelope.Body.Operator.Spki ?? "";
            string fp = (envelope.Body.Operator.Fp ?? "").Trim().ToLowerInvariant();
            if (host.Length == 0 || spki.Length == 0 || fp.Length != ModManifestCrypto.FingerprintHexLength) return false;

            string path = Path.Combine(ConfigDirectory, TrustedFileName);
            TrustedOperatorsFile onDisk = new();
            if (File.Exists(path))
            {
                string? error = null;
                bool parsed;
                try { parsed = ModManifestJson.TryParseTrust(File.ReadAllText(path), out onDisk, out error); }
                catch (Exception e)
                {
                    parsed = false;
                    error = e.Message;
                }

                if (!parsed)
                {
                    LogWarning("ModManifest: operator fp=" + fp + " not pinned on first use: " + TrustedFileName + " could not be read (" + error + ")");
                    return false;
                }
            }

            // Any row for this address (another key, a session pin) means this is not a first use.
            if (ModManifestTrust.FindByHost(onDisk, host) != null) return false;
            TrustedOperatorsFile memory;
            lock (Gate) memory = _trustForJoin ?? onDisk;
            if (ModManifestTrust.FindByHost(memory, host) != null) return false;

            var now = DateTime.UtcNow;
            string issued = envelope.Body.Issued ?? "";
            ModManifestTrust.WriteAtomic(path, ModManifestTrust.Upsert(onDisk, host, fp, spki, true, now, issued));
            lock (Gate) _trustForJoin = ModManifestTrust.Upsert(memory, host, fp, spki, true, now, issued);
            LogInfo("ModManifest: pinned operator fp=" + fp + " host=" + host + " on first use (trust on first use; a later key change for this address is flagged)");
            return true;
        }
        catch (Exception e)
        {
            LogWarning("ModManifest: pinning on first use threw: " + e.Message);
            return false;
        }
    }

    /// <summary>Build + sign a catalog from an explicit plugin list. Used by tests and by the live enumerator.</summary>
    internal static ManifestEnvelope Build(
        IReadOnlyList<LoadedModInfo> loaded,
        ManifestOverrides? overrides,
        ModManifestKey key,
        DateTime issuedUtc,
        JoinSection? join = null)
    {
        if (key == null) throw new ArgumentNullException(nameof(key));
        var body = ModManifestJson.BuildBody(loaded, overrides, key.ToOperator(""), issuedUtc);
        string canonical = ModManifestJson.CanonicalBody(body);
        string signature = key.Sign(ModManifestCrypto.Utf8(canonical));
        // JOINFLOW1: the join section, bound to this body (its issued and operator fingerprint) and
        // signed separately; see JoinSection for why it is not a body field.
        if (join == null) return new ManifestEnvelope(body, signature);
        var bound = join.BoundTo(body);
        return new ManifestEnvelope(body, signature, bound, JoinSectionJson.Sign(bound, key));
    }

    /// <summary>
    /// Mod compatibility C3: build and sign both catalog forms from one body (<see cref="ModCatalogForms"/>). The
    /// current form is exactly what <see cref="Build"/> returns; the extended form carries each row's <c>peers</c>
    /// (from <paramref name="peersOf"/>) and <c>match</c> (from <paramref name="settings"/>). Same key, same
    /// <c>issued</c>, and the same signed join section, which is bound to <c>issued</c> and the fingerprint only.
    /// </summary>
    internal static (ManifestEnvelope Current, ManifestEnvelope Extended) BuildForms(
        IReadOnlyList<LoadedModInfo> loaded,
        ManifestOverrides? overrides,
        ModManifestKey key,
        DateTime issuedUtc,
        JoinSection? join,
        Func<ManifestMod, string?>? peersOf,
        VersionMatchSettings? settings)
    {
        if (key == null) throw new ArgumentNullException(nameof(key));
        var current = Build(loaded, overrides, key, issuedUtc, join);
        var extendedBody = ModCatalogForms.Extend(current.Body, peersOf, settings);
        string signature = key.Sign(ModManifestCrypto.Utf8(ModManifestJson.CanonicalBody(extendedBody)));
        return (current, current.WithBody(extendedBody, signature));
    }

    /// <summary>
    /// Mod compatibility C4: the three forms. The Progenitor form is the extended form plus the server's own build
    /// (<paramref name="progenitor"/>), signed separately; with no requirement it is the extended envelope itself.
    /// </summary>
    internal static (ManifestEnvelope Current, ManifestEnvelope Extended, ManifestEnvelope Progenitor) BuildForms(
        IReadOnlyList<LoadedModInfo> loaded,
        ManifestOverrides? overrides,
        ModManifestKey key,
        DateTime issuedUtc,
        JoinSection? join,
        Func<ManifestMod, string?>? peersOf,
        VersionMatchSettings? settings,
        ProgenitorRequirement? progenitor)
    {
        var (current, extended) = BuildForms(loaded, overrides, key, issuedUtc, join, peersOf, settings);
        var body = ModCatalogForms.WithProgenitor(extended.Body, progenitor);
        if (ReferenceEquals(body, extended.Body)) return (current, extended, extended);
        string signature = key.Sign(ModManifestCrypto.Utf8(ModManifestJson.CanonicalBody(body)));
        return (current, extended, current.WithBody(body, signature));
    }

    /// <summary>
    /// C3: the catalog form user <paramref name="userIndex"/> reads: the extended one when its answer declared
    /// <see cref="Handshake.Capability.VersionRanges"/>, else the current one. C4: the Progenitor form when it also declared
    /// <see cref="Handshake.Capability.ProgenitorExact"/>. Null before the first signing.
    /// </summary>
    internal static ManifestEnvelope? CatalogFor(int userIndex)
    {
        bool extended = UserReadsRanges(userIndex);
        bool progenitor = extended && UserReadsProgenitor(userIndex);
        lock (Gate)
        {
            if (progenitor && _envelopeProgenitor != null) return _envelopeProgenitor;
            return extended && _envelopeExtended != null ? _envelopeExtended : _envelope;
        }
    }

    /// <summary>
    /// C4: <paramref name="full"/> (a Progenitor-form or extended envelope) carrying <paramref name="requirement"/>, re-signed,
    /// or <paramref name="full"/> itself when it already carries it (or cannot be signed).
    /// </summary>
    internal static ManifestEnvelope? WithJudgedProgenitor(ManifestEnvelope? full, ProgenitorRequirement requirement, ModManifestKey? key)
    {
        if (full?.Body == null || key == null || requirement == null || !requirement.Valid) return full;
        if (string.Equals(full.Body.Progenitor, requirement.Version, StringComparison.Ordinal)
            && string.Equals(full.Body.ProgenitorSha, requirement.Sha, StringComparison.Ordinal)) return full;
        var plain = new ManifestBody(full.Body.Schema, full.Body.ServerName, full.Body.Issued, full.Body.Operator, full.Body.ProfileCode, full.Body.Mods)
        {
            Matching = full.Body.Matching,
        };
        var body = ModCatalogForms.WithProgenitor(plain, requirement);
        return full.WithBody(body, key.Sign(ModManifestCrypto.Utf8(ModManifestJson.CanonicalBody(body))));
    }

    /// <summary>C4: the Progenitor form of the current catalog (server), or null.</summary>
    internal static ManifestEnvelope? ProgenitorEnvelope
    {
        get { lock (Gate) return _envelopeProgenitor; }
    }

    /// <summary>C4: true when user <paramref name="userIndex"/>'s last answer declared <see cref="Handshake.Capability.ProgenitorExact"/> (and ranges).</summary>
    internal static bool UserReadsProgenitor(int userIndex)
    {
#if PROGENITOR_TESTS
        lock (Gate) return TestRangeUsers.Contains(userIndex) && TestProgenitorUsers.Contains(userIndex);
#else
        try { return ModCatalogForms.ReadsProgenitor(Handshake.CapsOf(userIndex)); }
        catch { return false; }
#endif
    }

    /// <summary>C4: the requirement this server signs now, or null (not Exact, not enforced, or no source).</summary>
    internal static ProgenitorRequirement? CurrentProgenitorRequirement()
    {
        try { return ProgenitorExact.ServerStatus()?.Requirement; }
        catch { return null; }
    }

    /// <summary>C3: the extended form of the current catalog (server), or null.</summary>
    internal static ManifestEnvelope? ExtendedEnvelope
    {
        get { lock (Gate) return _envelopeExtended; }
    }

    /// <summary>True when user <paramref name="userIndex"/>'s last answer declared <see cref="Handshake.Capability.VersionRanges"/>.</summary>
    internal static bool UserReadsRanges(int userIndex)
    {
#if PROGENITOR_TESTS
        lock (Gate) return TestRangeUsers.Contains(userIndex);
#else
        try { return ModCatalogForms.ReadsExtended(Handshake.CapsOf(userIndex)); }
        catch { return false; }
#endif
    }

    /// <summary>
    /// Mod compatibility C3: re-sign both catalog forms after the server's <b>Version matching</b> or a per-mod rule
    /// changed (the admin setting calls this). The extended form's <c>match</c> and <c>peers</c> are part of the
    /// catalog's change stamp, so this re-signs only when they actually changed, and the next frame's scan resends the
    /// catalog to every handshake-ok client. Returns the current form, or null when there is no key.
    /// </summary>
    internal static ManifestEnvelope? ResignForVersionMatching() => RebuildCatalog("version matching");

    /// <summary>
    /// C3: the server build's effective range for each catalog row: the registry's declaration of a loaded mod, else
    /// the row's handshake declaration (<see cref="VersionMatching.EffectiveRangeOf"/>), else the same minor version.
    /// </summary>
    internal static Func<ManifestMod, string?> CatalogPeers(IReadOnlyList<Handshake.ModRecord>? required)
    {
        var byGuid = new Dictionary<string, Handshake.ModRecord>(StringComparer.OrdinalIgnoreCase);
        if (required != null)
            foreach (var r in required)
                if (!string.IsNullOrEmpty(r.Guid)) byGuid[r.Guid] = r;
        return m =>
        {
            if (m == null || string.IsNullOrEmpty(m.Id)) return null;
            if (!byGuid.TryGetValue(m.Id, out var rec))
                rec = new Handshake.ModRecord(m.Id, m.Version, m.Level, m.VersionRule, declared: false);
            return VersionMatching.EffectiveRangeOf(rec);
        };
    }

    /// <summary>
    /// JOINFLOW2: record that the player confirmed "Install N mods from Thunderstore that &lt;server&gt; lists"
    /// for this join's server (host and key), with the <c>Owner-Name@version</c> packages it covered. It needs no
    /// pin. On disk and in this join's in-memory copy; refused from a receive handler and for a key that is not this
    /// join's.
    /// </summary>
    internal static bool AcknowledgeThunderstore(string fingerprint, string spki, string host, IReadOnlyList<string> packages)
    {
        try
        {
            if (IsReceiveDispatchInFlight())
            {
                LogWarning("ModManifest: Thunderstore confirmation refused: called from a receive handler");
                return false;
            }

            if (string.IsNullOrEmpty(spki))
            {
                LogWarning("ModManifest: Thunderstore confirmation not recorded: no operator key for this server");
                return false;
            }

            string path = Path.Combine(ConfigDirectory, TrustedFileName);
            var now = DateTime.UtcNow;
            // Never rewrite a trust file that exists but could not be read (locked, malformed): writing the
            // confirmation over an empty read would drop every pin.
            TrustedOperatorsFile onDisk = new();
            if (File.Exists(path))
            {
                string? error = null;
                bool parsed;
                try { parsed = ModManifestJson.TryParseTrust(File.ReadAllText(path), out onDisk, out error); }
                catch (Exception e)
                {
                    parsed = false;
                    error = e.Message;
                }

                if (!parsed)
                {
                    LogWarning("ModManifest: Thunderstore confirmation not recorded: " + TrustedFileName + " could not be read (" + error + ")");
                    return false;
                }
            }

            var updated = ModManifestTrust.AcknowledgeThunderstore(onDisk, host ?? "", spki, fingerprint, packages, now);
            ModManifestTrust.WriteAtomic(path, updated);
            lock (Gate)
            {
                if (_trustForJoin != null)
                    _trustForJoin = ModManifestTrust.AcknowledgeThunderstore(_trustForJoin, host ?? "", spki, fingerprint, packages, now);
            }

            LogInfo("ModManifest: Thunderstore batch confirmed for operator fp=" + fingerprint + " host=" + host + " (" + packages.Count + " package(s))");
            return true;
        }
        catch (Exception e)
        {
            LogWarning("ModManifest: Thunderstore confirmation threw: " + e.Message);
            return false;
        }
    }

    /// <summary>JOINFLOW2 test support (<c>join_ui forget_trust</c>): drop the Thunderstore confirmations of operator <paramref name="fingerprint"/>.</summary>
    internal static bool ForgetThunderstoreAcks(string fingerprint)
    {
        try
        {
            string fp = (fingerprint ?? "").Trim().ToLowerInvariant();
            if (fp.Length == 0) return false;
            string path = Path.Combine(ConfigDirectory, TrustedFileName);
            var onDisk = ReadTrust();
            var updated = ModManifestTrust.ClearThunderstoreAcks(onDisk, fp);
            bool changed = !ReferenceEquals(updated, onDisk);
            if (changed) ModManifestTrust.WriteAtomic(path, updated);
            lock (Gate)
            {
                if (_trustForJoin != null)
                {
                    var memory = ModManifestTrust.ClearThunderstoreAcks(_trustForJoin, fp);
                    changed |= !ReferenceEquals(memory, _trustForJoin);
                    _trustForJoin = memory;
                }
            }

            return changed;
        }
        catch (Exception e)
        {
            LogWarning("ModManifest: Thunderstore confirmation clear threw: " + e.Message);
            return false;
        }
    }

    /// <summary>JOINFLOW1: the trust store as this join reads it (in-memory, session pins included).</summary>
    internal static TrustedOperatorsFile TrustForJoin()
    {
        lock (Gate)
        {
            if (_trustForJoin == null) _trustForJoin = ReadTrust();
            return _trustForJoin;
        }
    }

    internal static void EnsureInstalled()
    {
        bool installed;
        lock (Gate) installed = _installed;
        if (!installed) Install();
    }

    /// <summary>Tests: stands in for the server's own name.</summary>
    internal static Func<string>? OwnServerNameProbe { get; set; }

    /// <summary>Tests: stands in for a loaded plugin's readable name.</summary>
    internal static Func<string, string?>? PluginNameProbe { get; set; }

#if !PROGENITOR_TESTS
    private static string? _ownServerName;
#endif

    /// <summary>
    /// The server's own name as the game resolves it at start (<see cref="OwnServerNameFrom"/>: command line,
    /// environment, then the <c>ServerHostSettings</c> layers). Read once per Core generation; empty when none is found.
    /// </summary>
    internal static string OwnServerName()
    {
        var probe = OwnServerNameProbe;
        if (probe != null)
        {
            try { return (probe() ?? "").Trim(); }
            catch { return ""; }
        }

#if PROGENITOR_TESTS
        return "";
#else
        if (_ownServerName != null) return _ownServerName;
        string name = "";
        try { name = OwnServerNameFrom(Environment.GetCommandLineArgs(), BepInEx.Paths.GameRootPath); }
        catch { name = ""; }
        _ownServerName = name;
        return name;
#endif
    }

    /// <summary>
    /// The name a dedicated server with these arguments and install folder runs under (game-free), through the
    /// game's own layering (<see cref="ServerHostSettingsFiles"/>, SERVERDESC1): <c>-serverName</c>, then
    /// <c>VR_NAME</c> / <c>VR_SERVERNAME</c> / <c>VR_SERVER_NAME</c>, then the save folder's
    /// <c>ServerHostSettings</c>, the persistent-data copy and the install's default. Line breaks and tabs are
    /// removed and the name is capped at 64 characters, as the game's <c>ValidateServerName</c> does. The shipped
    /// default's name (<see cref="StockServerName"/>) names no server: empty.
    /// </summary>
    internal static string OwnServerNameFrom(
        IReadOnlyList<string>? args, string? root, string? defaultDataPath = null, Func<string, string?>? env = null)
    {
        string name;
        try
        {
            var layers = ServerHostSettingsFiles.Resolve(new ServerHostSettingsInputs
            {
                Args = args ?? Array.Empty<string>(),
                InstallRoot = root,
                DefaultDataPath = defaultDataPath,
                Env = env,
            });
            name = layers.EffectiveString("Name") ?? "";
        }
        catch { name = ""; }

        name = System.Text.RegularExpressions.Regex.Replace(name.Trim(), "[\n\r\t\v\f]+", "");
        if (name.Length > 64) name = name.Substring(0, 64);
        return string.Equals(name, StockServerName, StringComparison.OrdinalIgnoreCase) ? "" : name;
    }

    /// <summary>The <c>Name</c> the dedicated server ships with in its default <c>ServerHostSettings.json</c>.</summary>
    internal const string StockServerName = ServerHostSettingsFiles.StockServerName;

    /// <summary>Readable names of the loaded mods the server requires (id → name), within the section's caps.</summary>
    internal static Dictionary<string, string> RequiredModNames(IReadOnlyList<LoadedModInfo> loaded)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        if (loaded == null) return names;
        foreach (var mod in loaded)
        {
            if (mod.Level != NetworkCompatibilityLevel.ServerMustHave || string.IsNullOrEmpty(mod.Id)) continue;
            if (mod.Id.Length > JoinSectionLimits.MaxIdChars || names.Count >= JoinSectionLimits.MaxNames) continue;
            string? name = PluginName(mod.Id);
            if (string.IsNullOrWhiteSpace(name)) continue;
            name = name!.Trim();
            if (name.Length > JoinSectionLimits.MaxNameChars || string.Equals(name, mod.Id, StringComparison.OrdinalIgnoreCase)) continue;
            names[mod.Id.ToLowerInvariant()] = name;
        }

        return names;
    }

    /// <summary>The versions the handshake requires for the loaded <c>ServerMustHave</c> mods (id → version), within the section's caps.</summary>
    internal static Dictionary<string, string> RequiredModVersions(IReadOnlyList<LoadedModInfo> loaded)
    {
        var versions = new Dictionary<string, string>(StringComparer.Ordinal);
        if (loaded == null) return versions;
        foreach (var mod in loaded)
        {
            if (mod.Level != NetworkCompatibilityLevel.ServerMustHave || string.IsNullOrEmpty(mod.Id) || string.IsNullOrEmpty(mod.Version)) continue;
            if (mod.Id.Length > JoinSectionLimits.MaxIdChars || mod.Version.Length > JoinSectionLimits.MaxVersionChars
                || versions.Count >= JoinSectionLimits.MaxNames) continue;
            versions[mod.Id.ToLowerInvariant()] = mod.Version.Trim();
        }

        return versions;
    }

    private static string? PluginName(string id)
    {
        var probe = PluginNameProbe;
        if (probe != null)
        {
            try { return probe(id); }
            catch { return null; }
        }

#if PROGENITOR_TESTS
        return null;
#else
        try
        {
            if (Progenitor.Plugins.ModRegistry.TryGet(id, out var entry) && !string.IsNullOrWhiteSpace(entry.Name)) return entry.Name;
            var plugins = IL2CPPChainloader.Instance?.Plugins;
            if (plugins != null && plugins.TryGetValue(id, out var info) && info?.Metadata != null) return info.Metadata.Name;
        }
        catch { /* unnamed */ }

        return null;
#endif
    }

    /// <summary>AUTOUPDATE3, server: (plugin id, package) of every install-ledger row Progenitor wrote from a Thunderstore package.</summary>
    private static List<(string, string)> LedgerPackages()
    {
        var list = new List<(string, string)>();
#if !PROGENITOR_TESTS
        try
        {
            string bep = BepInEx.Paths.BepInExRootPath;
            string game = System.IO.Path.GetDirectoryName(bep.TrimEnd('\\', '/')) ?? "";
            var ledger = Progenitor.Install.ServerInstallLedger.TryRead(game);
            if (ledger == null) return list;
            foreach (var row in ledger.Files)
            {
                if (row == null || row.UninstallPending || string.IsNullOrEmpty(row.PluginId)) continue;
                if (!string.Equals(row.Category, Progenitor.Install.ServerInstallLedger.CategoryMod, StringComparison.Ordinal)) continue;
                if (Progenitor.Install.InstalledState.TrySplitPackageSource(row.Source, out string package, out _) && package.Contains('-'))
                    list.Add((row.PluginId, package));
            }
        }
        catch { /* the overlay alone names packages */ }
#endif
        return list;
    }

    /// <summary>
    /// Server: the Thunderstore package (<c>Owner-Name</c>) the current signed catalog names for <paramref name="modId"/>,
    /// or null. The install window's Thunderstore fix reads it (2026-10-05).
    /// </summary>
    internal static string? CatalogPackageOf(string? modId)
    {
        if (string.IsNullOrEmpty(modId)) return null;
        ManifestEnvelope? envelope;
        lock (Gate) envelope = _envelope;
        var mods = envelope?.Body?.Mods;
        if (mods == null) return null;
        foreach (var m in mods)
            if (m != null && string.Equals(m.Id, modId, StringComparison.OrdinalIgnoreCase))
                return string.IsNullOrWhiteSpace(m.Package) ? null : m.Package!.Trim();
        return null;
    }

    /// <summary>
    /// Server: every Thunderstore package the current catalog and the admins' required client mods name, for the server's
    /// read of the Thunderstore index (<see cref="ServerPackageAvailability"/>).
    /// </summary>
    internal static IReadOnlyList<string> CatalogPackages()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ManifestEnvelope? envelope;
        lock (Gate) envelope = _envelope;
        var mods = envelope?.Body?.Mods;
        if (mods != null)
            foreach (var m in mods)
                if (m != null && !string.IsNullOrWhiteSpace(m.Package)) set.Add(m.Package!.Trim());
        try
        {
            var policy = JoinAdminServer.CurrentPolicy;
            if (policy != null)
                foreach (var r in policy.Required)
                    if (r.Package.Length > 0) set.Add(r.Package);
        }
        catch { /* no policy, no packages from it */ }

        return new List<string>(set);
    }

    internal static ManifestEnvelope? RebuildCatalog() => RebuildCatalog("scan");

    /// <summary>
    /// Re-sign the catalog when what it describes changed. <paramref name="reason"/> names the cause for the resend's
    /// log line (<c>ModManifest: catalog re-signed (&lt;reason&gt;); resent to N client(s)</c>); a change asks the next
    /// frame's scan to resend it to every handshake-ok client.
    /// </summary>
    internal static ManifestEnvelope? RebuildCatalog(string reason)
    {
        try
        {
            var key = EnsureKey(logFingerprint: false);
            if (key == null)
            {
                LogWarning("ModManifest: no operator key; catalog not signed.");
                return null;
            }
            var loaded = EnumerateLoaded();
            var overrides = ReadOverrides();
            // AUTOUPDATE3 (live 2026-10-09): a mod Progenitor installed here from Thunderstore is named by its package, so a
            // player can install it (or its update) from the join screens; the operator's overlay wins.
            overrides = ModManifestJson.WithLedgerPackages(overrides, loaded, LedgerPackages());
            // JOINFLOW1 live fix (2026-10-04): an overlay without serverName named every catalog "V Rising";
            // the server's own name (-serverName, else its ServerHostSettings.json) is the better default.
            if (string.IsNullOrWhiteSpace(overrides.ServerName))
            {
                string own = OwnServerName();
                if (own.Length > 0) overrides.ServerName = own;
            }

            // JOINFLOW1: the server message, reset notification and recommended mods. Their canonical
            // form is part of the stamp, so an admin's edit re-signs the catalog and resends it to every
            // handshake-ok client within a second, like a plugin load does.
            var join = JoinServerSettings.Content(overrides);
            // The readable names of the mods it requires (a client that lacks one can still name it), and the
            // versions its handshake requires now (an overlay version pin does not change them).
            // JOINADMIN1: plus the readable names the admin's recommended rows carry.
            if (join != null) join = JoinSectionJson.WithRequired(join, JoinAdminServer.WithRecommendedNames(RequiredModNames(loaded)), RequiredModVersions(loaded));
            // CLIENTPOLICY1: the admins' required client mods, the mode and the approved list, signed with the rest (and,
            // being part of the canonical form below, part of the stamp: a policy change re-signs and resends).
            // CLIENTPOLICY2: under Only approved, the GUIDs of the mods this server runs too, so a client judges "a mod this
            // server runs" as rule 2 does before it reports (the automatic pause).
            if (join != null) join = JoinSectionJson.WithClientPolicy(join, JoinAdminServer.CurrentPolicy, JoinAdminServer.PolicyRunIds());
            // Mod compatibility C3: what the extended form adds (each row's effective range and, off ranges, its match
            // rule) is part of the stamp, so a change of Version matching, a per-mod rule or a mod's declared range
            // re-signs both forms like any other change.
            var matching = VersionMatching.Current();
            Handshake.ModRecord[] required;
            try { required = Handshake.CopyRequired(); }
            catch { required = Array.Empty<Handshake.ModRecord>(); }
            var peersOf = CatalogPeers(required);
            // C4: the server's own build while Exact is enforced for Progenitor (part of the stamp, so turning Exact on or
            // off, or the server's build becoming obtainable or not, re-signs and resends like a mode change).
            var progenitor = CurrentProgenitorRequirement();
            var probe = ModCatalogForms.WithProgenitor(
                ModCatalogForms.Extend(ModManifestJson.BuildBody(loaded, overrides, key.ToOperator(""), DateTime.UtcNow), peersOf, matching), progenitor);
            string stamp = Stamp(loaded, overrides)
                + "|join=" + (join == null ? "" : JoinSectionJson.Canonical(join))
                + "|ext=" + ModCatalogForms.ExtensionStamp(probe);
            lock (Gate)
            {
                if (stamp == _catalogStamp && _envelope != null)
                    return _envelope;
            }
            var (envelope, extended, progenitorForm) = BuildForms(loaded, overrides, key, DateTime.UtcNow, join, peersOf, matching, progenitor);
            lock (Gate)
            {
                _envelope = envelope;
                _envelopeExtended = extended;
                _envelopeProgenitor = progenitorForm;
                _catalogStamp = stamp;
                SentUsers.Clear();
                if (_resendReason.Length == 0) _resendReason = string.IsNullOrWhiteSpace(reason) ? "scan" : reason.Trim();
            }

            RequestScanSoon();
            // 2026-10-05: the install window's Thunderstore fix needs the packages this catalog names read from the index.
            try { Handshake.ServerPackages?.Want(CatalogPackages()); }
            catch (Exception e) { LogWarning("ModManifest: package availability refresh threw: " + e.Message); }
            LogInfo("ModManifest: catalog " + envelope.Body.Mods.Count.ToString(CultureInfo.InvariantCulture)
                + " mod(s) signed fp=" + key.Fingerprint);
            return envelope;
        }
        catch (Exception e)
        {
            LogError("ModManifest.RebuildCatalog: " + e.Message);
            return null;
        }
    }

    internal static ModManifestKey? EnsureKey(bool logFingerprint)
    {
        lock (Gate)
        {
            if (_key != null)
            {
                if (logFingerprint && !_loggedFingerprint)
                {
                    _loggedFingerprint = true;
                    LogInfo("ModManifest: " + ModManifestCrypto.AlgorithmName + " operator key fp=" + _key.Fingerprint);
                }
                return _key;
            }
        }

        string dir = ConfigDirectory;
        try { Directory.CreateDirectory(dir); }
        catch (Exception e)
        {
            LogError("ModManifest: could not create config directory '" + dir + "': " + e.Message);
            return null;
        }

        string privPath = Path.Combine(dir, PrivateKeyFileName);
        string pubPath = Path.Combine(dir, PublicKeyFileName);
        if (File.Exists(privPath))
        {
            if (!TryLoadPrivateKey(privPath, out var loaded, out var error))
            {
                LogError("ModManifest: " + PrivateKeyFileName + " is unreadable (" + error
                    + "); not overwriting it. Catalog will not be signed.");
                return null;
            }
            TryWritePublic(pubPath, loaded);
            lock (Gate)
            {
                _key?.Dispose();
                _key = loaded;
                if (logFingerprint || !_loggedFingerprint)
                {
                    _loggedFingerprint = true;
                    LogInfo("ModManifest: " + ModManifestCrypto.AlgorithmName + " operator key fp=" + loaded.Fingerprint);
                }
            }
            return loaded;
        }

        var generated = ModManifestCrypto.Generate();
        try
        {
            WritePrivate(privPath, generated);
            TryWritePublic(pubPath, generated);
        }
        catch (Exception e)
        {
            generated.Dispose();
            LogError("ModManifest: failed to write operator key: " + e.Message);
            return null;
        }
        lock (Gate)
        {
            _key?.Dispose();
            _key = generated;
            _loggedFingerprint = true;
        }
        LogInfo("ModManifest: generated " + ModManifestCrypto.AlgorithmName + " operator key fp="
            + generated.Fingerprint + " (" + pubPath + ")");
        return generated;
    }

    internal static ManifestOverrides ReadOverrides()
    {
        string path = Path.Combine(ConfigDirectory, ManifestFileName);
        if (!File.Exists(path)) return new ManifestOverrides();
        try
        {
            string json = File.ReadAllText(path);
            if (!ModManifestJson.TryParseOverrides(json, out var o, out var error))
            {
                LogWarning("ModManifest: " + ManifestFileName + " parse failed: " + error);
                return new ManifestOverrides();
            }
            return o;
        }
        catch (Exception e)
        {
            LogWarning("ModManifest: reading " + ManifestFileName + " threw: " + e.Message);
            return new ManifestOverrides();
        }
    }

    internal static TrustedOperatorsFile ReadTrust()
    {
        string path = Path.Combine(ConfigDirectory, TrustedFileName);
        if (!File.Exists(path)) return new TrustedOperatorsFile();
        try
        {
            string json = File.ReadAllText(path);
            if (!ModManifestJson.TryParseTrust(json, out var file, out var error))
            {
                LogWarning("ModManifest: " + TrustedFileName + " parse failed: " + error);
                return new TrustedOperatorsFile();
            }
            return file;
        }
        catch (Exception e)
        {
            LogWarning("ModManifest: reading " + TrustedFileName + " threw: " + e.Message);
            return new TrustedOperatorsFile();
        }
    }

    internal static IReadOnlyList<LoadedModInfo> EnumerateLoaded()
    {
#if PROGENITOR_TESTS
        if (TestLoadedMods != null) return TestLoadedMods;
#endif
        var list = new List<LoadedModInfo>();
        var handshake = new Dictionary<string, Handshake.ModRecord>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var rows = IsServerSide() ? Handshake.CopyRequired() : Handshake.CopyProvided();
            for (int i = 0; i < rows.Length; i++)
            {
                if (string.IsNullOrEmpty(rows[i].Guid)) continue;
                handshake[rows[i].Guid] = rows[i];
            }
        }
        catch (Exception e)
        {
            LogWarning("ModManifest: handshake table read threw: " + e.Message);
        }

#if !PROGENITOR_TESTS
        try { AddChainloaderPlugins(list, handshake); }
        catch (Exception e) { LogWarning("ModManifest: chainloader walk threw: " + e.Message); }
        try { AddReloadPlugins(list, handshake); }
        catch (Exception e) { LogWarning("ModManifest: reload-folder walk threw: " + e.Message); }
#endif

        // Handshake rows that are not a live plugin still belong in the catalog:
        // a Require() without a loaded DLL is exactly what a client must fetch.
        foreach (var kv in handshake)
        {
            if (ContainsId(list, kv.Key)) continue;
            if (ModManifestJson.IsFrameworkGuid(kv.Key)) continue;
            if (Handshake.Compare.IsReservedGuid(kv.Key)) continue;
            var rec = kv.Value;
            list.Add(new LoadedModInfo(rec.Guid, rec.Version, rec.Level, rec.VersionRule, hotLoadable: false));
        }
        return list;
    }

    private static bool ContainsId(List<LoadedModInfo> list, string id)
    {
        for (int i = 0; i < list.Count; i++)
        {
            if (string.Equals(list[i].Id, id, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

#if !PROGENITOR_TESTS
    private static void AddChainloaderPlugins(List<LoadedModInfo> list, Dictionary<string, Handshake.ModRecord> handshake)
    {
        var loader = IL2CPPChainloader.Instance;
        if (loader?.Plugins == null) return;
        foreach (var kv in loader.Plugins)
        {
            var info = kv.Value;
            if (info == null) continue;
            var meta = info.Metadata;
            if (meta == null) continue;
            string id = meta.GUID ?? "";
            if (id.Length == 0 || ModManifestJson.IsFrameworkGuid(id)) continue;
            string version = meta.Version != null ? meta.Version.ToString() : "";
            var instance = info.Instance;
            bool inReload = IsReloadLocation(info.Location, instance);
            bool progenitor = IsProgenitorPlugin(instance);
            string? source = null;
            try
            {
                if (instance != null && Reload.PluginReloader.TryGetSourcePath(instance.GetType().Assembly, out var reloadPath))
                    source = reloadPath;
                else if (!string.IsNullOrEmpty(info.Location))
                    source = info.Location;
            }
            catch { /* hash is optional */ }
            AddLoaded(list, handshake, id, version, instance?.GetType(), inReload && progenitor, source);
        }
    }

    private static void AddReloadPlugins(List<LoadedModInfo> list, Dictionary<string, Handshake.ModRecord> handshake)
    {
        Assembly[] assemblies;
        try { assemblies = AppDomain.CurrentDomain.GetAssemblies(); }
        catch { return; }
        for (int i = 0; i < assemblies.Length; i++)
        {
            var asm = assemblies[i];
            if (asm == null) continue;
            if (!Reload.PluginReloader.TryGetSourcePath(asm, out _)) continue;
            Type?[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException e) { types = e.Types ?? Array.Empty<Type>(); }
            catch { continue; }
            for (int t = 0; t < types.Length; t++)
            {
                var type = types[t];
                if (type == null || type.IsAbstract) continue;
                if (!typeof(BasePlugin).IsAssignableFrom(type)) continue;
                var plugin = type.GetCustomAttribute<BepInPlugin>(inherit: true);
                if (plugin == null) continue;
                string id = plugin.GUID ?? "";
                if (id.Length == 0 || ModManifestJson.IsFrameworkGuid(id) || ContainsId(list, id)) continue;
                string version = plugin.Version != null ? plugin.Version.ToString() : "";
                bool progenitor = IsProgenitorPluginType(type);
                string? source = null;
                try
                {
                    if (Reload.PluginReloader.TryGetSourcePath(asm, out var reloadPath))
                        source = reloadPath;
                }
                catch { /* hash is optional */ }
                AddLoaded(list, handshake, id, version, type, progenitor, source);
            }
        }
    }

    private static void AddLoaded(
        List<LoadedModInfo> list,
        Dictionary<string, Handshake.ModRecord> handshake,
        string id,
        string version,
        Type? pluginType,
        bool hotLoadable,
        string? sourcePath = null)
    {
        if (ContainsId(list, id)) return;
        var level = NetworkCompatibilityLevel.NoNeedForSync;
        var rule = NetworkVersionRule.None;
        if (handshake.TryGetValue(id, out var rec))
        {
            level = rec.Level;
            rule = rec.VersionRule;
            if (!string.IsNullOrEmpty(rec.Version)) version = rec.Version;
        }
        else
        {
            var attr = pluginType?.GetCustomAttribute<NetworkCompatibilityAttribute>(inherit: true);
            if (attr != null)
            {
                level = attr.Level;
                rule = attr.VersionRule;
            }
        }
        list.Add(new LoadedModInfo(
            id, version, level, rule, hotLoadable, HashFile(sourcePath),
            serverOnly: ModManifestJson.IsServerOnlyType(pluginType)));
    }

    private static bool IsReloadLocation(string? location, object? instance)
    {
        if (instance != null)
        {
            try
            {
                if (Reload.PluginReloader.TryGetSourcePath(instance.GetType().Assembly, out _))
                    return true;
            }
            catch { /* ignore */ }
        }
        if (string.IsNullOrEmpty(location)) return false;
        return location.IndexOf("progenitor-reload", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool IsProgenitorPlugin(object? instance) =>
        instance != null && IsProgenitorPluginType(instance.GetType());

    private static bool IsProgenitorPluginType(Type type)
    {
        for (var t = type; t != null; t = t.BaseType)
        {
            if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(ProgenitorPlugin<>))
                return true;
        }
        return false;
    }

    private static void OnFrame()
    {
        try
        {
            if (IsServerSide())
            {
                double now = Clock.Elapsed.TotalSeconds;
                if (now >= _nextScan)
                {
                    _nextScan = now + 1.0;
                    if (CatalogScan(out string reason, out var users))
                    {
                        int sent = 0;
                        for (int i = 0; i < users.Length; i++)
                            if (SendToUserIndex(users[i], force: true)) sent++;
                        LogInfo("ModManifest: catalog re-signed (" + reason + "); resent to " + sent.ToString(CultureInfo.InvariantCulture) + " client(s)");
                    }
                }
            }
        }
        catch (Exception e)
        {
            LogError("ModManifest.OnFrame: " + e.Message);
        }
    }

    private static void OnPlayerDisconnected(Events.PlayerDisconnected ev)
    {
        try
        {
            int index = ev.User.Index;
            // R28 finding 28.3: ReceiveGate.Forget had no production caller, so a disconnecting
            // player's bucket lived on until the idle sweep. The bucket is keyed on the platform
            // id now, so this is the one place that can retire it.
            CustomEvents.ServerReceiveGate.ForgetPlatform(ev.User.PlatformId);
            lock (Gate)
            {
                HandshakeOkUsers.Remove(index);
                SentUsers.Remove(index);
            }
        }
        catch (Exception e)
        {
            LogWarning("ModManifest: disconnect cleanup threw: " + e.Message);
        }
    }

    private static void OnClientWorldReady(Events.WorldReady ev)
    {
        try
        {
            if (ev == null || ev.IsServer) return;
            ClearJoinState();
        }
        catch (Exception e)
        {
            LogError("ModManifest.OnClientWorldReady: " + e);
        }
    }

    private static unsafe void OnPacket(int eventId, byte* payload, int length)
    {
        try
        {
            if (eventId != EventId) return;
            if (length < 0) return;
            var copy = new byte[length];
            if (length > 0)
            {
                fixed (byte* dst = copy)
                    Buffer.MemoryCopy(payload, dst, length, length);
            }
            AcceptCatalogPacket(copy);
        }
        catch (Exception e)
        {
            LogError("ModManifest.OnPacket: " + e.Message);
        }
    }

    /// <summary>A retired <c>PrmC</c> chunk from an older server: dropped (one log line), never read.</summary>
    private static unsafe void OnRetiredChunkPacket(int eventId, byte* payload, int length)
    {
        DropRetiredPacket(eventId, length);
    }

    /// <summary>
    /// A retired <c>PrmR</c> request from an older client: dropped (one log line), never read. Nothing is ever sent
    /// back: this server has no packages to send.
    /// </summary>
    private static unsafe void OnRetiredRequestPacket(int eventId, Entity userEntity, User user, byte* payload, int length)
    {
        DropRetiredPacket(eventId, length);
    }
#endif

    /// <summary>
    /// The scan: re-sign when something changed, then hand back the handshake-ok clients that do not have the current
    /// catalog yet when it was re-signed since they last got it (by this scan or by any direct re-sign: an admin's
    /// change, a join, a describe). False when there is nothing to resend.
    /// </summary>
    internal static bool CatalogScan(out string reason, out int[] users)
    {
        RebuildCatalog("scan");
        return TakeCatalogResend(out reason, out users);
    }

    internal static bool TakeCatalogResend(out string reason, out int[] users)
    {
        lock (Gate)
        {
            reason = _resendReason;
            users = Array.Empty<int>();
            if (reason.Length == 0) return false;
            _resendReason = "";
            var list = new List<int>();
            foreach (int u in HandshakeOkUsers) if (!SentUsers.Contains(u)) list.Add(u);
            list.Sort();
            users = list.ToArray();
            return true;
        }
    }

    /// <summary>A re-sign outside the scan: the next frame's scan resends it (instead of within a second).</summary>
    private static void RequestScanSoon()
    {
#if !PROGENITOR_TESTS
        _nextScan = 0;
#endif
    }

    private static bool SendToUserIndex(int userIndex, bool force)
    {
#if !PROGENITOR_TESTS
        var world = Worlds.Server;
        if (world == null || !world.IsCreated) return false;
        var bootstrap = world.GetExistingSystemManaged<ServerBootstrapSystem>();
        if (bootstrap == null) return false;
        // C3: the form this user reads (the extended one only for a client that declared VersionRanges).
        var envelope = CatalogFor(userIndex);
        if (envelope == null) return false;
        return SendToUser(bootstrap, userIndex, "user " + userIndex.ToString(CultureInfo.InvariantCulture), envelope, force);
#else
        _ = force;
        var envelope = CatalogFor(userIndex);
        lock (Gate)
        {
            SentUsers.Add(userIndex);
            if (envelope != null) TestSent.Add((userIndex, envelope));
        }
        return true;
#endif
    }

#if !PROGENITOR_TESTS
    private static bool SendToUser(
        ServerBootstrapSystem bootstrap,
        int userIndex,
        string name,
        ManifestEnvelope envelope,
        bool force)
    {
        if (!force)
        {
            lock (Gate)
            {
                if (!SentUsers.Add(userIndex)) return false;
            }
        }
        else
        {
            lock (Gate) SentUsers.Add(userIndex);
        }

        string json = ModManifestJson.CanonicalEnvelope(envelope);
        List<byte[]> packets;
        try { packets = ModManifestWire.Pack(ModManifestCrypto.Utf8(json)); }
        catch (Exception e)
        {
            LogWarning("ModManifest: pack failed: " + e.Message);
            lock (Gate) SentUsers.Remove(userIndex);
            return false;
        }
        int sent = 0;
        for (int i = 0; i < packets.Count; i++)
        {
            try
            {
                if (CustomEvents.SendToUser(bootstrap, userIndex, EventId, packets[i]))
                    sent++;
            }
            catch (Exception e)
            {
                LogWarning("ModManifest: SendToUser threw: " + e.Message);
            }
        }
        LogInfo("ModManifest: sent " + sent.ToString(CultureInfo.InvariantCulture) + " chunk(s) to " + name);
        return sent > 0;
    }
#endif

    internal static void AcceptCatalogPacket(byte[] packet)
    {
        if (!ModManifestWire.TryRead(packet, out int seq, out int count, out bool more, out var slice, out var error))
        {
            LogWarning("ModManifest: dropped chunk: " + error);
            return;
        }
        if (more && seq + 1 >= count)
        {
            LogWarning("ModManifest: chunk flag More with seq " + seq.ToString(CultureInfo.InvariantCulture)
                + " of " + count.ToString(CultureInfo.InvariantCulture));
        }
        byte[]? envelopeBytes;
        lock (Gate)
        {
            if (!Incoming.TryAdd(seq, count, slice, out envelopeBytes, out error))
            {
                LogWarning("ModManifest: reassembly failed: " + error);
                return;
            }
        }
        if (envelopeBytes == null) return;
        AcceptEnvelope(envelopeBytes);
    }

    internal static void AcceptEnvelope(byte[] utf8)
    {
        string json;
        try { json = Encoding.UTF8.GetString(utf8); }
        catch (Exception e)
        {
            LogWarning("ModManifest: envelope is not UTF-8: " + e.Message);
            return;
        }
        if (!ModManifestJson.TryParseEnvelope(json, out var envelope, out var error))
        {
            LogWarning("ModManifest: envelope parse failed: " + error);
            return;
        }

        // Host before Classify so a MITM key-change can fire (pins are host-keyed).
        CaptureConnectHostFromClient();

        // Fingerprint / signature / replay gates run inside Classify. A body that fails
        // any of them is logged and is not LastEnvelope / Pending (and Classify does
        // not walk mods on a failed verify).
        var diff = CompareReceived(envelope);
        if (!diff.SignatureValid)
        {
            string line = diff.SignatureError ?? ModManifestCompare.FingerprintRejectedLog;
            if (line.StartsWith("Manifest:", StringComparison.Ordinal)
                || line.StartsWith("ModManifest:", StringComparison.Ordinal))
                LogWarning(line);
            else
                LogWarning("ModManifest: " + line);
            return;
        }
        if (diff.Replayed)
        {
            LogWarning(diff.ReplayError ?? ModManifestCompare.ReplayRejectedLog(envelope.Body.Issued, ""));
            return;
        }

        // HOSTKEY1: pins and Thunderstore confirmations written under per-connection relay strings move to this address
        // (after the replay check, so a replayed catalog moves nothing), before first use would pin it afresh.
        if (AdoptRelayTrust(envelope, CurrentHost())) diff = CompareReceived(envelope);

        string host = CurrentHost();
        if (host.Length == 0)
        {
            CaptureConnectHostFromClient();
            host = CurrentHost();
        }

        // 2026-10-05: trust on first use. The first verified catalog from an address with no pin pins its key, so
        // the replay check (issued) and key-change detection work from the next catalog on; a key change for a pinned
        // address is never re-pinned here.
        if (PinOnFirstUse(envelope, diff, host)) diff = CompareReceived(envelope);

        string block = ModManifestCompare.FormatLogBlock(envelope, diff, host);
        lock (Gate)
        {
            _envelope = envelope;
            _lastDiff = diff;
            _lastLogBlock = block;
            if (host.Length > 0) _connectHost = host;
            _pending = new PendingManifest(envelope, diff, host, diff.PreviousFingerprint);
        }
        if (diff.OperatorTrusted)
            PersistAcceptedIssued(envelope, host);
        LogInfo(block);

        // FMAN17: on a join this server refused, the verified catalog is routed to the read-only deny report (and the
        // join screens). Handshake.OnResult sets the flag before this packet can arrive (reliable events keep order,
        // and the Result is sent synchronously ahead of the descriptor).
        PendingManifest? accepted;
        lock (Gate) accepted = _pending;
        // Per-server mod pause: a server this client already has a profile for gains its display
        // name and operator fingerprint. Never creates a profile by itself.
        try
        {
            if (accepted != null && accepted.SignatureValid)
            {
                // HOSTKEY1: the profiles written under per-connection relay strings for this operator become this address's.
                if (ServerProfiles.AdoptRelayProfiles(accepted.Host, accepted.Fingerprint))
                    LogInfo("ModManifest: moved operator fp=" + accepted.Fingerprint + " server profiles from per-connection relay keys to host=" + accepted.Host);
                ServerProfiles.NoteIdentityIfKnown(accepted.Host, accepted.Envelope.Body.ServerName, accepted.Fingerprint);
            }
        }
        catch (Exception e) { LogWarning("ModManifest: server profile identity threw: " + e.Message); }

        // JOINFLOW1: the join section rides the same envelope and is verified only now, after the body
        // passed its fingerprint, signature and replay gates. A section that fails is dropped with its
        // reason; the catalog is still used. The join screens learn of it before the deny report
        // republishes, so either sees the message and the catalog together.
        JoinSection? join = null;
        try
        {
            join = JoinSectionJson.Verified(envelope, out var joinError);
            if (joinError != null) LogWarning("ModManifest: join section dropped: " + joinError);
            JoinFlowClient.OnCatalog(accepted, join, ModManifestDeny.JoinWasDenied);
        }
        catch (Exception e) { LogWarning("ModManifest: join section threw: " + e.Message); }

        if (ModManifestDeny.JoinWasDenied)
            ModManifestDeny.OnCatalogAccepted(accepted);
    }

    private static void OnClientServerContextCleared(string reason)
    {
        // This includes kick/timeout while Client_0 survives, but excludes a Core-only refresh.
        // Keep any refusal on the disconnect screen; Handshake dismisses it at the main menu.
        ClearJoinState(clearDeny: false);
    }

    /// <summary>
    /// Drop the previous join's catalog on connection end, handshake epoch change or the next WorldReady. Uninstall
    /// also retires the same session state.
    /// </summary>
    internal static void ClearJoinState(bool clearDeny = true)
    {
        lock (Gate)
        {
            _envelope = null;
            _lastDiff = null;
            _lastLogBlock = "";
            _pending = null;
            _trustForJoin = null;
            _connectHost = "";
            _loggedConnectHost = false;
            Incoming.Reset();
        }

        // FMAN17: and so does the previous join's refusal. A client that reaches WorldReady joined
        // something, so no deny report on this machine is still evidence about the connection it
        // is on.
        if (clearDeny)
            ModManifestDeny.OnJoinEnded();
    }

    private static ManifestDiff CompareReceived(ManifestEnvelope envelope)
    {
        var clientMods = EnumerateLoaded();
        TrustedOperatorsFile trust;
        lock (Gate)
        {
            if (_trustForJoin == null)
                _trustForJoin = ReadTrust();
            trust = _trustForJoin;
        }
        return ModManifestCompare.Classify(envelope, clientMods, trust, CurrentHost());
    }

    private static void PersistAcceptedIssued(ManifestEnvelope envelope, string host)
    {
        try
        {
            string spki = envelope.Body.Operator.Spki ?? "";
            string issued = envelope.Body.Issued ?? "";
            if (string.IsNullOrEmpty(spki) || string.IsNullOrEmpty(issued)) return;
            string path = Path.Combine(ConfigDirectory, TrustedFileName);
            // Disk only: session (always:false) pins live in _trustForJoin and must not land here.
            var onDisk = ReadTrust();
            var updatedDisk = ModManifestTrust.AdvanceIssued(onDisk, host, spki, issued);
            if (!ReferenceEquals(updatedDisk, onDisk))
                ModManifestTrust.WriteAtomic(path, updatedDisk);
            lock (Gate)
            {
                if (_trustForJoin != null)
                    _trustForJoin = ModManifestTrust.AdvanceIssued(_trustForJoin, host, spki, issued);
            }
        }
        catch (Exception e)
        {
            LogWarning("ModManifest: persisting issued stamp threw: " + e.Message);
        }
    }

    private static string Stamp(IReadOnlyList<LoadedModInfo> loaded, ManifestOverrides overrides)
    {
        var sb = new StringBuilder();
        string path = Path.Combine(ConfigDirectory, ManifestFileName);
        try
        {
            if (File.Exists(path))
                sb.Append(File.GetLastWriteTimeUtc(path).Ticks.ToString(CultureInfo.InvariantCulture));
        }
        catch { /* stamp without mtime */ }
        sb.Append('|');
        if (loaded != null)
        {
            for (int i = 0; i < loaded.Count; i++)
            {
                var m = loaded[i];
                sb.Append(m.Id).Append(',').Append(m.Version).Append(',')
                    .Append((int)m.Level).Append(',').Append((int)m.VersionRule).Append(',')
                    .Append(m.HotLoadable ? '1' : '0').Append(',')
                    .Append(m.Sha256 ?? "").Append(';');
            }
        }
        if (overrides?.Mods != null)
        {
            sb.Append("ov=");
            for (int i = 0; i < overrides.Mods.Count; i++)
            {
                var o = overrides.Mods[i];
                if (o == null) continue;
                sb.Append(o.Id ?? "").Append(',')
                    .Append(o.Sha256 ?? "").Append(',')
                    .Append((o.Size ?? 0).ToString(CultureInfo.InvariantCulture))
                    .Append(';');
            }
        }
        return sb.ToString();
    }

    private static bool TryLoadPrivateKey(string path, out ModManifestKey key, out string? error)
    {
        key = null!;
        error = null;
        string text;
        try { text = File.ReadAllText(path); }
        catch (Exception e)
        {
            error = e.Message;
            return false;
        }
        string b64 = StripKeyComments(text);
        byte[] pkcs8;
        try { pkcs8 = Convert.FromBase64String(b64); }
        catch (FormatException e)
        {
            error = e.Message;
            return false;
        }
        return ModManifestCrypto.TryImportPkcs8(pkcs8, out key, out error);
    }

    private static void WritePrivate(string path, ModManifestKey key)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# progenitor-operator.key — ECDSA P-256 PKCS#8 (base64). Never share this file.");
        sb.AppendLine("# Algorithm: " + ModManifestCrypto.AlgorithmName + ". Fingerprint: " + key.Fingerprint);
        sb.AppendLine(Convert.ToBase64String(key.Pkcs8));
        File.WriteAllText(path, sb.ToString());
    }

    private static void TryWritePublic(string path, ModManifestKey key)
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("# progenitor-operator.pub — ECDSA P-256 SPKI (base64). fp=" + key.Fingerprint);
            sb.AppendLine(key.SpkiBase64);
            File.WriteAllText(path, sb.ToString());
        }
        catch (Exception e)
        {
            LogWarning("ModManifest: writing " + PublicKeyFileName + " threw: " + e.Message);
        }
    }

    internal static string StripKeyComments(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var sb = new StringBuilder();
        using var reader = new StringReader(text);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            line = line.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            sb.Append(line);
        }
        return sb.ToString();
    }

    private static bool IsServerSide()
    {
#if PROGENITOR_TESTS
        return TestIsServer;
#else
        return Plugin.IsServerProcess;
#endif
    }

    /// <summary>
    /// The address this client dialed, or empty. Job FMAN17: the deny report is built before any
    /// catalog arrives, so it needs the host from the same seam the trust pins use.
    /// </summary>
    internal static string CurrentConnectHost() => CurrentHost();

    private static string CurrentHost()
    {
#if PROGENITOR_TESTS
        if (!string.IsNullOrEmpty(TestConnectHost)) return TestConnectHost;
#endif
        lock (Gate)
        {
            if (_connectHost.Length > 0) return _connectHost;
        }
        return "";
    }

    private static bool IsReceiveDispatchInFlight()
    {
#if PROGENITOR_TESTS
        return TestReceiveDispatchDepth > 0;
#else
        return CustomEvents.IsReceiveDispatchInFlight;
#endif
    }

    /// <summary>Live value of the deny-descriptor switch (test override first, then config).</summary>
    private static bool DenyDescriptorEnabled
    {
        get
        {
#if PROGENITOR_TESTS
            return TestDenyDescriptorEnabled;
#else
            return _denyDescriptorEnabled;
#endif
        }
    }

    internal static string ReadLiveConnectHost()
    {
#if PROGENITOR_TESTS
        return "";
#else
        try
        {
            var world = Worlds.Client;
            if (world == null || !world.IsCreated) return "";
            var bootstrap = world.GetExistingSystemManaged<ClientBootstrapSystem>();
            if (bootstrap == null) return "";
            // HOSTKEY1: the dialed address first. ConnectionString of a SteamIPv4 join is a relay string that changes on
            // every connection, so it is the last resort (ConnectHostKey).
            string gameAddress = "";
            int gamePort = 0;
            try
            {
                var addr = bootstrap.LastConnectionData.ConnectAddress;
                gamePort = addr.Port;
                gameAddress = addr.Address.ToString() ?? "";
            }
            catch
            {
                gameAddress = "";
                gamePort = 0;
            }
            string raw = "";
            try { raw = bootstrap.ConnectionString ?? ""; }
            catch { raw = ""; }
            // LOCALREJOIN1: a loopback rejoin of this PC's server keeps the original endpoint as its key.
            return ConnectHostAlias.Resolve(ConnectHostKey.Choose(BootstrapLink.LastConnectAddress, BootstrapLink.LastConnectPort, gameAddress, gamePort, raw));
        }
        catch (Exception e)
        {
            LogWarning("ModManifest: reading ClientBootstrapSystem connect address threw: " + e.Message);
            return "";
        }
#endif
    }

    /// <summary>
    /// JOINFLOW2: whether the client's connection used a password (<c>ClientBootstrapSystem.LastConnectionData.Password</c>
    /// is not empty). Only that it was set is read — never the value, which is not kept anywhere — so a rejoin
    /// knows to ask for it again. False with no client world.
    /// </summary>
    internal static bool ReadLiveConnectUsedPassword()
    {
#if PROGENITOR_TESTS
        return false;
#else
        try
        {
            var world = Worlds.Client;
            if (world == null || !world.IsCreated) return false;
            var bootstrap = world.GetExistingSystemManaged<ClientBootstrapSystem>();
            if (bootstrap == null) return false;
            var data = bootstrap.LastConnectionData;
            return !string.IsNullOrEmpty(data.Password);
        }
        catch
        {
            return false;
        }
#endif
    }

    internal static string NormalizeConnectHost(string raw)
    {
        raw = (raw ?? "").Trim();
        if (raw.Length == 0) return "";
        const string steam = "SteamIPv4://";
        const string lidgren = "Lidgren://";
        const string p2p = "SteamP2P://";
        const string eos = "EosP2P://";
        if (raw.StartsWith(steam, StringComparison.OrdinalIgnoreCase)) raw = raw.Substring(steam.Length);
        else if (raw.StartsWith(lidgren, StringComparison.OrdinalIgnoreCase)) raw = raw.Substring(lidgren.Length);
        else if (raw.StartsWith(p2p, StringComparison.OrdinalIgnoreCase)) raw = raw.Substring(p2p.Length);
        else if (raw.StartsWith(eos, StringComparison.OrdinalIgnoreCase)) raw = raw.Substring(eos.Length);
        return raw.Trim();
    }

    /// <summary>
    /// SHA-256 of a plugin DLL, cached by path + length + mtime. A 1 s catalog scan may
    /// stat the file; hashing runs once per Offer / file change, never every frame.
    /// </summary>
    private static string? HashFile(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        try
        {
            var info = new FileInfo(path);
            long length = info.Length;
            long mtime = info.LastWriteTimeUtc.Ticks;
            lock (Gate)
            {
                if (FileHashCache.TryGetValue(path, out var cached)
                    && cached.Length == length
                    && cached.MtimeTicks == mtime
                    && !string.IsNullOrEmpty(cached.Hash))
                    return cached.Hash;
            }
#if PROGENITOR_TESTS
            Interlocked.Increment(ref TestHashComputes);
#endif
            using var stream = File.OpenRead(path);
            using var sha = SHA256.Create();
            string hash = ModManifestCrypto.ToHex(sha.ComputeHash(stream));
            lock (Gate)
            {
                FileHashCache[path] = new CachedFileHash
                {
                    Length = length,
                    MtimeTicks = mtime,
                    Hash = hash,
                };
            }
            return hash;
        }
        catch (Exception e)
        {
            LogWarning("ModManifest: hashing '" + path + "' threw: " + e.Message);
            return null;
        }
    }

#if PROGENITOR_TESTS
    internal static string? HashFileForTests(string path) => HashFile(path);
#endif

#if !PROGENITOR_TESTS
    /// <summary>
    /// Bind the deny-descriptor switch now, or once the shared file resolves; the compiled-in default holds until then.
    /// (The <c>ModTransfer*</c> limits that used to share this bind went with server distribution, 2026-10-05; an old
    /// cfg's rows for them are simply no longer read.)
    /// </summary>
    private static void BindDenyDescriptorSwitch() =>
        Config.SharedConfig.BindWhenAvailable("[Networking] deny-time descriptor", BindDenyDescriptorSwitchFrom);

    private static void BindDenyDescriptorSwitchFrom(BepInEx.Configuration.ConfigFile file)
    {
        try
        {
            // Job FMAN17. BCL bool on the shared file, per the Core config policy.
            var deny = Config.ConfigHelper.Bind(
                file,
                ModManifestDenyPolicy.ConfigSection,
                ModManifestDenyPolicy.ConfigKey,
                ModManifestDenyPolicy.DefaultEnabled,
                ModManifestDenyPolicy.ConfigDescription);
            if (deny != null) _denyDescriptorEnabled = deny.Value;
        }
        catch (Exception e)
        {
            LogWarning("ModManifest: binding the deny-descriptor switch threw: " + e.Message);
        }
    }
#endif

    private static void LogInfo(string message) => Plugin.Logger?.LogInfo(message);
    private static void LogWarning(string message) => Plugin.Logger?.LogWarning(message);
    private static void LogError(string message) => Plugin.Logger?.LogError(message);

    private struct CachedFileHash
    {
        public long Length;
        public long MtimeTicks;
        public string Hash;
    }
}

#if !PROGENITOR_TESTS
/// <summary>
/// First-frame kick so the catalog installs without a <c>Plugin.cs</c> edit (that
/// file is owned by another job). Fresh-ALC loads run the initializer; same-ALC
/// restore is covered when <see cref="ModManifest.Describe"/> or the next join
/// tick calls <c>EnsureInstalled</c>.
/// </summary>
internal static class ModManifestBootstrap
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Usage",
        "CA2255:The 'ModuleInitializer' attribute is only intended to be used in application code",
        Justification = "Core is loaded by Bootstrap into a collectible ALC. Plugin.cs cannot be edited by this job to call Install.")]
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void Hook()
    {
        Plugin.Frame += Kick;
    }

    private static void Kick()
    {
        Plugin.Frame -= Kick;
        try { ModManifest.Install(); }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("ModManifest.Install: " + e);
        }
    }
}
#endif
