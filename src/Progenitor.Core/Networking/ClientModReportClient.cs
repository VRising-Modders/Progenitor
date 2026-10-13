using System;
using System.Collections.Generic;
using System.Globalization;
#if !PROGENITOR_TESTS
using System.Reflection;
using BepInEx;
using BepInEx.Unity.IL2CPP;
using Progenitor.Ui;
#endif

namespace Progenitor.Networking;

/// <summary>UNINSTREQ1: the joined server's mid-session rule for mods that break its client mod policy.</summary>
internal enum ServerMidSessionRule
{
    /// <summary>Not said (an older server, or no re-check).</summary>
    Unknown = 0,

    /// <summary>A warning in game; the mods it can pause are paused.</summary>
    Warn = 1,

    /// <summary>A disconnect after the grace seconds unless it is fixed.</summary>
    Disconnect = 2,
}

/// <summary>
/// CLIENTPOLICY1, client: the inventory report (docs/client-mod-policy.md, "How the server learns a client's mods").
/// Sent only to a server whose handshake request set <see cref="Handshake.Capability.ClientPolicy"/>: right after
/// each handshake answer, and again when a hot load or unload changed what this process runs during the session.
/// Every plugin this process loaded: chainloader plugins, Progenitor plugins (hot-loaded included) and preloader
/// patchers, each with its package when the install ledger knows it. No file hashes: a lying client can fake them.
/// </summary>
internal static class ClientModReportClient
{
    /// <summary>A change to the loaded set is re-checked this often while the server asked (and on every inventory change).</summary>
    internal const double RecheckSeconds = 10;

    /// <summary>Debounce after an inventory change before the resend is built.</summary>
    internal const double SettleSeconds = 1.5;

    private static bool _serverAsks;
    private static string _lastSentSignature = "";

    // SELFFIX1 review: the mods-only part of the last report, which the mid-session check compares (the claims are for the
    // join; a change in them alone never makes a mid-session report).
    private static string _lastSentModsSignature = "";
    private static int _sent;
    private static int _nextReportId = 1;
    private static string _lastReason = "";
    private static int _lastEntries;
    private static bool _lastTruncated;
    private static double _dirtySince = -1;
    private static double _nextRecheck = double.PositiveInfinity;

    // CLIENTPOLICY3: the next resend goes out even when unchanged (a re-signed policy; the server re-judges it), the ask
    // came from that signed section rather than the request, and what the automatic pause before the last resend took.
    private static bool _forceNext;
    private static bool _askedBySection;
    private static bool _serverRechecks;

    /// <summary>CLIENTPOLICY3: the joined server re-checks mid-session (its request declared PolicyRecheck).</summary>
    internal static bool ServerRechecks => _serverRechecks;

    private static ServerMidSessionRule _midSessionRule;

    private static bool _serverCountdownChat;
    private static int _heldMessagesSent;

    /// <summary>
    /// F41: the joined server reads the held-notice message (its request declared
    /// <see cref="Handshake.Capability.CountdownChat"/>), so it says a held countdown in chat.
    /// </summary>
    internal static bool ServerCountdownChat => _serverCountdownChat;

    /// <summary>F41: held-notice messages sent to the joined server (readback).</summary>
    internal static int HeldMessagesSent => _heldMessagesSent;

    /// <summary>
    /// F41 (decided 2026-10-08): tell the joined server whether its disconnect countdown notice is held behind an open
    /// confirmation (<paramref name="held"/>), with what this client has of the mods it names, so the server says the
    /// countdown in chat while the player can't see it. Only to a server that declared
    /// <see cref="Handshake.Capability.CountdownChat"/>, and only on an accepted join. True when it was sent.
    /// </summary>
    internal static bool SendNoticeHeld(bool held, IReadOnlyList<RecheckItem>? items)
    {
        if (!_serverCountdownChat || !JoinOk()) return false;
        bool sent;
        try { sent = SendPacket(ClientPolicyHeldWire.Encode(held, items)); }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("ClientPolicy: the held-notice message could not be sent: " + e.Message);
            return false;
        }

        if (sent) _heldMessagesSent++;
        Plugin.Logger?.LogInfo("ClientPolicy: told the server its countdown notice is "
            + (held ? "held behind an open confirmation" : "no longer held") + "; sent=" + sent);
        return sent;
    }

    /// <summary>
    /// UNINSTREQ1: what the joined server does mid-session about mods that break its client mod policy: from its request's
    /// capability row (<see cref="Handshake.Capability.PolicyWarns"/> / <see cref="Handshake.Capability.PolicyDisconnects"/>),
    /// then from each notice it sends (the admins may change the rule mid-session; a notice carries the rule in force).
    /// </summary>
    internal static ServerMidSessionRule MidSessionRule => _midSessionRule;

    private static int _serverGraceSeconds;

    /// <summary>
    /// REQRULE5: the joined server's Disconnect them seconds, as its rule message said them (<see cref="ClientPolicyRuleWire"/>);
    /// 0 until one arrives (a server from before the setting never sends one: its grace is the old fixed 15).
    /// </summary>
    internal static int ServerGraceSeconds => _serverGraceSeconds;

    private static int _ruleUpdates;

    /// <summary>
    /// REQRULE2: the server sent its rule as it stands now (after the join, and whenever its admins change the policy or the
    /// grace), so the uninstall boxes' Required by line follows a mid-session change.
    /// </summary>
    internal static void NoteRuleUpdate(ServerMidSessionRule rule, int graceSeconds)
    {
        if (rule != ServerMidSessionRule.Unknown) _midSessionRule = rule;
        if (graceSeconds > 0) _serverGraceSeconds = ClientPolicyRecheckLimits.Clamp(graceSeconds);
        _ruleUpdates++;
    }

    /// <summary>UNINSTREQ1 (server): the admins' mid-session rule as its capability bit on the request row.</summary>
    internal static Handshake.Capability MidSessionBit(MidSessionPolicy rule) =>
        rule == MidSessionPolicy.Disconnect ? Handshake.Capability.PolicyDisconnects : Handshake.Capability.PolicyWarns;

    /// <summary>UNINSTREQ1: the rule a server's capability row declares (unknown from an older server).</summary>
    internal static ServerMidSessionRule RuleOf(Handshake.Capability caps) =>
        (caps & Handshake.Capability.PolicyDisconnects) != 0 ? ServerMidSessionRule.Disconnect
        : (caps & Handshake.Capability.PolicyWarns) != 0 ? ServerMidSessionRule.Warn
        : ServerMidSessionRule.Unknown;

    /// <summary>
    /// UNINSTREQ1: a notice arrived: it shows the rule in force now. A disconnect countdown proves Disconnect; a warning
    /// proves Warn only when it names a mod that would count toward a disconnect (under Disconnect a requirement the admins
    /// added mid-session is told as a warning too).
    /// </summary>
    internal static void NoteNoticeRule(RecheckNoticeKind kind, IReadOnlyList<RecheckItem>? items = null)
    {
        bool kickWorthy = false;
        foreach (var i in items ?? Array.Empty<RecheckItem>()) if (i.KickWorthy) { kickWorthy = true; break; }
        if (kind == RecheckNoticeKind.Warn && kickWorthy) _midSessionRule = ServerMidSessionRule.Warn;
        else if (kind is RecheckNoticeKind.Disconnect or RecheckNoticeKind.Disconnected) _midSessionRule = ServerMidSessionRule.Disconnect;
    }
    private static int _midSessionPauses;
    private static int _lastMidSessionPaused;

    /// <summary>
    /// CLIENTPOLICY3: the automatic pause before a mid-session report (live: <c>ServerModPause.PausePolicyModsMidSession</c>):
    /// pause what the remembered policy does not allow and show the paused-mods notice; returns how many were paused.
    /// </summary>
    internal static Func<int>? PauseBeforeReport { get; set; }

    /// <summary>
    /// CLIENTPOLICY3: on a mid-session policy update, resume what the policy paused that it now allows (live:
    /// <c>ServerModPause.ResumePolicyPausedNowAllowed</c>); returns how many were released.
    /// </summary>
    internal static Func<int>? ResumeOnPolicyUpdate { get; set; }

    private static int _lastPolicyResumed;

    /// <summary>Tests: the clock (seconds). Null: the stopwatch.</summary>
    internal static Func<double>? TestClock { get; set; }

    /// <summary>Tests: whether this client's join is accepted (live: the last handshake result is <c>ok</c>).</summary>
    internal static Func<bool>? TestJoinOk { get; set; }

    private static bool JoinOk() => TestJoinOk?.Invoke() ?? string.Equals(Handshake.LastClientResult, "ok", StringComparison.Ordinal);

    /// <summary>The joined server asked for the report (its request's capability bit).</summary>
    internal static bool ServerAsks => _serverAsks;

    internal static int Sent => _sent;

    /// <summary>
    /// The handshake request arrived: remember whether this server asked, and (CLIENTPOLICY3) whether it re-checks
    /// mid-session (<see cref="Handshake.Capability.PolicyRecheck"/> in its capability row). A new server starts over.
    /// </summary>
    internal static void NoteServerRequest(bool asks, bool rechecks = false, ServerMidSessionRule rule = ServerMidSessionRule.Unknown,
        bool countdownChat = false)
    {
        bool changed = asks != _serverAsks;
        _serverAsks = asks;
        _serverRechecks = rechecks;
        _serverCountdownChat = rechecks && countdownChat;
        _midSessionRule = rechecks ? rule : ServerMidSessionRule.Unknown;
        // Review L2: the grace is the request's server's too; its rule message (after the ok answer) says it again.
        _serverGraceSeconds = 0;
        _askedBySection = false;
        _forceNext = false;
        _lastSentSignature = "";
        _lastSentModsSignature = "";
        _dirtySince = -1;
        _nextRecheck = asks ? Now() + RecheckSeconds : double.PositiveInfinity;
        if (changed || asks)
            Plugin.Logger?.LogInfo(asks
                ? "ClientPolicy: this server checks your client mods; your mod list is sent after the handshake answer."
                : "ClientPolicy: this server does not check client mods; nothing is sent.");
    }

    /// <summary>The connection ended: nothing is sent until a server asks again.</summary>
    internal static void Reset()
    {
        if (_serverAsks) Plugin.Logger?.LogInfo("ClientPolicy: the server context ended; no mod report is sent until a server asks again.");
        _serverAsks = false;
        _serverRechecks = false;
        _serverCountdownChat = false;
        _midSessionRule = ServerMidSessionRule.Unknown;
        _serverGraceSeconds = 0;
        _askedBySection = false;
        _forceNext = false;
        _lastSentSignature = "";
        _lastSentModsSignature = "";
        _dirtySince = -1;
        _nextRecheck = double.PositiveInfinity;
    }

    /// <summary>A handshake answer went out: send the report when this server asked.</summary>
    internal static void AfterAnswer(string why)
    {
        if (!_serverAsks)
        {
            Plugin.Logger?.LogInfo("ClientPolicy: no mod report after the handshake answer (" + why + "): this server did not ask for one.");
            return;
        }

        Send("after the handshake answer (" + why + ")", force: true);
    }

    /// <summary>The inventory changed (a hot load or unload, a switch): re-check soon.</summary>
    internal static void NoteInventoryChanged()
    {
        if (_serverAsks && _dirtySince < 0) _dirtySince = Now();
    }

    /// <summary>
    /// CLIENTPOLICY3: a re-signed catalog arrived mid-session. When it carries a client mod policy, this server checks
    /// client mods: re-check now (the automatic pause, then a report the server re-judges against the new policy), even
    /// when this join was never asked (the policy was off when it joined; the server asked through the signed catalog).
    /// </summary>
    internal static void OnSignedPolicyMidSession(bool hasPolicy)
    {
        if (!JoinOk()) return;
        // Live 2026-10-05: a mod paused because the policy did not allow it comes back when the update allows it (the
        // resume changes the loaded set, so the report below carries it).
        var resume = ResumeOnPolicyUpdate;
        if (resume != null)
        {
            try { _lastPolicyResumed = resume(); }
            catch (Exception e) { Plugin.Logger?.LogWarning("ClientPolicy: resuming mods the policy allows now threw: " + e.Message); }
        }

        if (!hasPolicy)
        {
            // The admins turned the policy off: nothing is sent until a server asks again.
            if (_serverAsks) Plugin.Logger?.LogInfo("ClientPolicy: this server no longer checks client mods (its re-signed catalog carries no policy); your mod list is no longer sent.");
            _serverAsks = false;
            _askedBySection = false;
            _forceNext = false;
            _dirtySince = -1;
            return;
        }

        // Only a server that said it re-checks mid-session is sent mid-session reports.
        if (!_serverRechecks) return;
        if (!_serverAsks)
        {
            _serverAsks = true;
            _askedBySection = true;
            Plugin.Logger?.LogInfo("ClientPolicy: this server now checks client mods (its re-signed catalog carries a policy); your mod list is sent to it.");
        }

        _forceNext = true;
        if (_dirtySince < 0) _dirtySince = Now();
    }

    /// <summary>
    /// Frame: a debounced resend when the loaded set changed during the session (or a re-signed policy asks for one). Before
    /// a resend whose mods changed, the automatic pause takes what the remembered policy does not allow (CLIENTPOLICY3), so
    /// the server judges what is left.
    /// </summary>
    internal static void Tick()
    {
        // F41: a server notice held for an open confirmation is shown when that box closes (also at the main menu).
        try { ClientPolicyNoticeClient.ShowHeldWhenFree(); }
        catch { /* the next frame tries again */ }
        if (!_serverAsks) return;
        double now = Now();
        bool due = (_dirtySince >= 0 && now - _dirtySince >= SettleSeconds) || now >= _nextRecheck;
        if (!due) return;
        _dirtySince = -1;
        _nextRecheck = now + RecheckSeconds;
        if (!JoinOk()) return;
        // CLIENTPOLICY3: mid-session reports go only to a server that re-checks them (the report after each answer is
        // sent regardless, AfterAnswer).
        if (!_serverRechecks) return;
        bool force = _forceNext;
        _forceNext = false;
        if (!force && !ChangedSinceLastSend()) return;
        var pause = PauseBeforeReport;
        if (pause != null)
        {
            int paused = 0;
            try { paused = pause(); }
            catch (Exception e) { Plugin.Logger?.LogWarning("ClientPolicy: the automatic pause before the report threw: " + e.Message); }
            _lastMidSessionPaused = paused;
            if (paused > 0) _midSessionPauses++;
        }

        Send(force ? "the server's policy changed" : "the loaded mods changed", force);
    }

    private static bool ChangedSinceLastSend()
    {
        try { return !string.Equals(ClientModInventory.Signature(BuildEntries()), _lastSentModsSignature, StringComparison.Ordinal); }
        catch { return true; }
    }

    /// <summary>
    /// Build and send the report. <paramref name="force"/>: send even when it equals the last one (an answer always
    /// carries one). Returns the number of packets sent (0: nothing sent).
    /// </summary>
    internal static int Send(string reason, bool force)
    {
        List<ClientModEntry> entries;
        try { entries = BuildEntries(); }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("ClientPolicy: the mod list could not be built: " + e.Message);
            return 0;
        }

        // SELFFIX1: the required mods this client can fix by itself (a hint the server only uses to open the window).
        IReadOnlyList<ClientModFix> fixes;
        try { fixes = FixesProbe?.Invoke() ?? Array.Empty<ClientModFix>(); }
        catch { fixes = Array.Empty<ClientModFix>(); }
        string modsSignature = ClientModInventory.Signature(entries);
        string signature = modsSignature + FixSignature(fixes);
        if (!force && string.Equals(signature, _lastSentSignature, StringComparison.Ordinal)) return 0;
        int id = _nextReportId++;
        if (_nextReportId == int.MaxValue) _nextReportId = 1;
        var packets = ClientModReportWire.Encode(entries, truncated: false, id, fixes);
        ClientModReportWire.Body(entries, false, fixes, out int kept);
        int sent = 0, bytes = 0;
        string failure = "";
        foreach (var p in packets)
        {
            bool ok;
            try { ok = SendPacket(p); }
            catch (Exception e)
            {
                ok = false;
                failure = e.Message;
            }

            if (!ok)
            {
                if (failure.Length == 0) failure = "the send was refused (CustomEvents.SendToServer returned false)";
                break;
            }

            sent++;
            bytes += p.Length;
        }

        _lastReason = reason;
        _lastEntries = kept;
        _lastTruncated = kept < entries.Count;
        if (sent < packets.Count)
        {
            _lastSentSignature = "";
        _lastSentModsSignature = "";
            Plugin.Logger?.LogWarning("ClientPolicy: mod report NOT sent (" + sent.ToString(CultureInfo.InvariantCulture) + "/"
                + packets.Count.ToString(CultureInfo.InvariantCulture) + " chunk(s) went out): " + failure + "; " + reason);
            return sent;
        }

        _lastSentSignature = signature;
        _lastSentModsSignature = modsSignature;
        _sent++;
        Plugin.Logger?.LogInfo("ClientPolicy: sent mod report (" + kept.ToString(CultureInfo.InvariantCulture) + " mods, "
            + sent.ToString(CultureInfo.InvariantCulture) + " chunk(s), " + bytes.ToString(CultureInfo.InvariantCulture) + " bytes"
            + (_lastTruncated ? ", cut to fit 64 KiB" : "")
            + (fixes.Count > 0 ? ", " + fixes.Count.ToString(CultureInfo.InvariantCulture) + " fix claim(s)" : "") + ") " + reason);
        _lastFixes = fixes.Count;
        return sent;
    }

    /// <summary>SELFFIX1: what this client claims it can fix by itself (live: <c>JoinFlowClient.SelfFixClaims</c>). Null: no claims.</summary>
    internal static Func<IReadOnlyList<ClientModFix>>? FixesProbe { get; set; }

    private static int _lastFixes;

    private static string FixSignature(IReadOnlyList<ClientModFix> fixes)
    {
        if (fixes.Count == 0) return "";
        var sb = new System.Text.StringBuilder("|fixes");
        foreach (var f in fixes) sb.Append('|').Append(f.Kind).Append(':').Append(f.Key).Append('@').Append(f.Version);
        return sb.ToString();
    }

    /// <summary>The readback: what was last sent and why.</summary>
    internal static Dictionary<string, object?> Describe(bool withEntries)
    {
        var d = new Dictionary<string, object?>
        {
            ["serverAsks"] = _serverAsks, ["sent"] = _sent, ["lastReason"] = _lastReason, ["lastEntries"] = _lastEntries,
            ["lastTruncated"] = _lastTruncated,
            ["lastFixClaims"] = _lastFixes,
            // CLIENTPOLICY3.
            ["serverRechecks"] = _serverRechecks, ["midSessionRule"] = _midSessionRule.ToString().ToLowerInvariant(), ["serverGraceSeconds"] = _serverGraceSeconds, ["ruleUpdates"] = _ruleUpdates, ["countdownChat"] = _serverCountdownChat, ["heldMessagesSent"] = _heldMessagesSent, ["askedBySignedCatalog"] = _askedBySection, ["lastPolicyResumed"] = _lastPolicyResumed, ["midSessionPauses"] = _midSessionPauses, ["lastMidSessionPaused"] = _lastMidSessionPaused,
            ["recheckPending"] = _dirtySince >= 0 || _forceNext,
            ["serverNotice"] = ClientPolicyNoticeClient.Describe(),
        };
        if (withEntries)
        {
            try
            {
                var report = new ClientModReport();
                report.Entries.AddRange(BuildEntries());
                d["wouldSend"] = ClientModPolicyRecords.Describe(report);
            }
            catch (Exception e) { d["wouldSendError"] = e.Message; }
        }

        return d;
    }

    /// <summary>
    /// CLIENTPOLICY2: the mods this process runs now, as the report would list them (the join screens' policy rows and the
    /// automatic pause read the same list the server judges). Empty when it cannot be built.
    /// </summary>
    internal static IReadOnlyList<ClientModEntry> CurrentEntries()
    {
        try { return BuildEntries(); }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("ClientPolicy: the mod list could not be built: " + e.Message);
            return Array.Empty<ClientModEntry>();
        }
    }

    private static double Now() => TestClock?.Invoke() ?? System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;

    /// <summary>One <c>'PrCm'</c> packet from the server: a rule message (REQRULE2) or a mid-session notice. True when it was read.</summary>
    internal static bool HandleServerPacket(ReadOnlySpan<byte> span)
    {
        int length = span.Length;
        if (ClientPolicyRuleWire.IsRule(span))
        {
            if (!ClientPolicyRuleWire.TryDecode(span, out var rule, out int grace))
            {
                Plugin.Logger?.LogWarning("ClientPolicy: dropped a server rule packet this build can't read (" + length.ToString(CultureInfo.InvariantCulture) + " bytes)");
                return false;
            }

            NoteRuleUpdate(rule, grace);
            Plugin.Logger?.LogInfo("ClientPolicy: the server's mid-session rule is " + (rule == ServerMidSessionRule.Disconnect ? "disconnect" : "warn")
                + " (" + grace.ToString(CultureInfo.InvariantCulture) + " s to fix a mod under disconnect).");
            return true;
        }

        if (!ClientPolicyNoticeWire.TryDecode(span, out var notice))
        {
            Plugin.Logger?.LogWarning("ClientPolicy: dropped a server notice packet that is not one (" + length.ToString(CultureInfo.InvariantCulture) + " bytes)");
            return false;
        }

        ClientPolicyNoticeClient.Handle(notice);
        return true;
    }

#if PROGENITOR_TESTS
    /// <summary>Tests: what this process "loaded".</summary>
    internal static List<ClientModObservation> TestObserved = new();

    internal static List<byte[]> TestPackets = new();

    private static List<ClientModEntry> BuildEntries() => ClientModInventory.Build(TestObserved, null);

    private static bool SendPacket(byte[] packet)
    {
        TestPackets.Add(packet);
        return true;
    }

    internal static void ResetForTests()
    {
        Uninstall();
        _sent = 0;
        _ruleUpdates = 0;
        _heldMessagesSent = 0;
        _midSessionPauses = 0;
        _lastMidSessionPaused = 0;
        PauseBeforeReport = null;
        FixesProbe = null;
        _lastFixes = 0;
        ResumeOnPolicyUpdate = null;
        _lastPolicyResumed = 0;
        TestClock = null;
        TestJoinOk = null;
        TestObserved = new List<ClientModObservation>();
        TestPackets = new List<byte[]>();
        ClientPolicyNoticeClient.ResetProbes();
    }

    private static bool _testInstalled;

    // The same context subscription as the live Install, so a test drives the real trigger order.
    internal static void Install()
    {
        if (_testInstalled) return;
        _testInstalled = true;
        Handshake.ServerContextCleared += OnContextClearedForTests;
    }

    internal static void Uninstall()
    {
        if (_testInstalled) Handshake.ServerContextCleared -= OnContextClearedForTests;
        _testInstalled = false;
        Reset();
    }

    private static void OnContextClearedForTests(string reason) => Reset();
#else
    private static bool _installed;

    private static bool SendPacket(byte[] packet) => CustomEvents.SendToServer(ClientModReportWire.EventId, packet);

    private static readonly object NoticeOwner = new();

    internal static unsafe void Install()
    {
        if (_installed || Plugin.IsServerProcess) return;
        _installed = true;
        Handshake.ServerContextCleared += OnContextCleared;
        ModMenuInventory.GenerationChanged += NoteInventoryChanged;
        try { Plugin.Frame += Tick; }
        catch { /* resends then happen only after an answer */ }

        // CLIENTPOLICY3: the automatic pause before a mid-session report, and the server's mid-session notice ('PrCm'
        // kind 1; the report's chunks go the other way on the same id).
        PauseBeforeReport = ServerModPause.PausePolicyModsMidSession;
        ResumeOnPolicyUpdate = ServerModPause.ResumePolicyPausedNowAllowed;
        ClientPolicyNoticeClient.PauseProbe = ServerModPause.PauseForPolicy;
        ClientPolicyNoticeClient.PausableProbe = () =>
        {
            var backend = ServerModPause.PauseBackend();
            return backend.Supported ? backend.Suspendable() : Array.Empty<string>();
        };
        ClientPolicyNoticeClient.PausedProbe = ServerModPause.IsPaused;
        // SRVUNINST1: the advice for a missing required mod follows what this client has (turned off or not installed).
        ClientPolicyNoticeClient.LocalStateProbe = Ui.ModsMenu.RecheckLocalStateOf;
        // LOCALJOIN1 (debug): the configured local test index provides it (never while [Install] LocalIndexPath is empty).
        ClientPolicyNoticeClient.LocalTestProbe = JoinFlowClient.LocalTestProvides;
        // NOTICEOPEN1: whether Install Mods lists it, and the notice's Open Install Mods / Open Mods.
        ClientPolicyNoticeClient.InstallListedProbe = Ui.ModsMenu.InstallListedOf;
        ServerModPauseNotice.OpenRequested = Ui.ModsMenu.OpenForPolicyNotice;
        // SELFFIX1: the report claims the required mods this client can fix by itself, so the server can open the window.
        FixesProbe = JoinFlowClient.SelfFixClaims;
        JoinFlowClient.RememberedRequiredSource = ServerModPause.RememberedRequiredRows;
        ClientPolicyNoticeClient.AutoPauseProbe = () => ServerProfiles.AutoPause(ServerModPause.CurrentHost());
        ClientPolicyNoticeClient.ServerNameProbe = ServerModPause.CurrentServerName;
        ClientPolicyNoticeClient.Show = ServerModPauseNotice.Open;
        // F41: a server notice waits while a confirmation box is on screen.
        ClientPolicyNoticeClient.ConfirmationOpenProbe = () => Progenitor.Ui.UiDialog.Current != null;
        // At the menu the disconnect notice stays until the player closes it.
        ClientPolicyNoticeClient.ShowAtMenu = content => ServerModPauseNotice.Open(content, closeAfterSeconds: 0);
        try { CustomEvents.OnReceive(ClientModReportWire.EventId, OnServerNotice); }
        catch (Exception e) { Plugin.Logger?.LogWarning("ClientPolicy: the server notice handler could not be registered: " + e.Message); }
        Handshake.ClientReturnedToMenu += OnReturnedToMenu;
    }

    internal static void Uninstall()
    {
        if (!_installed) return;
        _installed = false;
        Handshake.ServerContextCleared -= OnContextCleared;
        ModMenuInventory.GenerationChanged -= NoteInventoryChanged;
        Handshake.ClientReturnedToMenu -= OnReturnedToMenu;
        try { Plugin.Frame -= Tick; } catch { /* teardown */ }
        try { CustomEvents.RemoveHandler(ClientModReportWire.EventId); } catch { /* teardown */ }
        try { Dev.MainThread.DropOwnedBy(NoticeOwner, typeof(ClientModReportClient).Assembly); } catch { /* teardown */ }
        PauseBeforeReport = null;
        ResumeOnPolicyUpdate = null;
        FixesProbe = null;
        JoinFlowClient.RememberedRequiredSource = null;
        ServerModPauseNotice.OpenRequested = null;
        ClientPolicyNoticeClient.ResetProbes();
        PerAssembly.Clear();
        Reset();
    }

    private static void OnContextCleared(string reason) => Reset();

    private static unsafe void OnServerNotice(int _, byte* payload, int length)
    {
        try
        {
            if (payload == null || length <= 0) return;
            HandleServerPacket(new ReadOnlySpan<byte>(payload, length));
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("ClientPolicy: the server notice threw: " + e.Message); }
    }

    // Live 2026-10-05: shown again when the connection ended, the notice was still in the world (the game's kick
    // banner, "Leave Game") and was hidden with it. Now it waits for the main menu: about 1.5 s after the client is
    // back there with no client world left, checked again each second for up to a minute while the world tears down.
    private static void OnReturnedToMenu()
    {
        try { Dev.MainThread.PostDelayed(1.5, () => ReshowWhenAtMenu(40), NoticeOwner); }
        catch { /* the chat line said it too */ }
    }

    private static void ReshowWhenAtMenu(int triesLeft)
    {
        bool worldUp;
        try
        {
            var world = Worlds.Client;
            worldUp = world != null && world.IsCreated;
        }
        catch { worldUp = false; }

        if (worldUp && triesLeft > 0)
        {
            try { Dev.MainThread.PostDelayed(1.0, () => ReshowWhenAtMenu(triesLeft - 1), NoticeOwner); }
            catch { /* the chat line said it too */ }
            return;
        }

        ClientPolicyNoticeClient.ReshowAfterLeave();
    }

    // Plugin rows per assembly, read once (GetTypes is not free). Keyed by a string, not the Assembly: a
    // ConditionalWeakTable keyed on a patcher assembly (process lifetime) kept its Core-typed value, and through
    // it this Core generation, alive forever (live 2026-10-05: every client Core swap after a join was refused).
    // A plain Assembly key would instead pin a hot-loaded plugin's ALC while this generation runs.
    private static readonly Dictionary<string, ClientModObservation[]> PerAssembly = new(StringComparer.Ordinal);

    private static ClientModObservation[] RowsOf(Assembly assembly, Func<Assembly, List<ClientModObservation>> read)
    {
        string key = assembly.ManifestModule.ModuleVersionId.ToString("N") + "|" + assembly.FullName;
        if (!PerAssembly.TryGetValue(key, out var rows)) PerAssembly[key] = rows = read(assembly).ToArray();
        return rows;
    }

    private static List<ClientModEntry> BuildEntries()
    {
        var observed = new List<ClientModObservation>();
        try
        {
            var plugins = IL2CPPChainloader.Instance?.Plugins;
            if (plugins != null)
                foreach (var pair in plugins)
                {
                    var meta = pair.Value?.Metadata;
                    if (meta == null || pair.Value!.Instance == null) continue;
                    observed.Add(new ClientModObservation(meta.GUID, meta.Name, meta.Version?.ToString() ?? "", ClientModSources.Chainloader));
                }
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("ClientPolicy: chainloader plugins unreadable: " + e.Message); }

        int visited = 0;
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (++visited > 4096) break;
            try
            {
                if (Reload.PluginReloader.TryGetSourcePath(assembly, out _))
                {
                    observed.AddRange(RowsOf(assembly, ReadReloaded));
                    continue;
                }

                string location = "";
                try { location = assembly.IsDynamic ? "" : assembly.Location ?? ""; }
                catch { location = ""; }
                if (location.IndexOf("patchers", StringComparison.OrdinalIgnoreCase) >= 0
                    && location.IndexOf("BepInEx", StringComparison.OrdinalIgnoreCase) >= 0)
                    observed.AddRange(RowsOf(assembly, ReadPatchers));
            }
            catch { /* one assembly never stops the list */ }
        }

        return ClientModInventory.Build(observed, PackageOf);
    }

    private static List<ClientModObservation> ReadReloaded(Assembly assembly)
    {
        var list = new List<ClientModObservation>();
        Type?[] types;
        try { types = assembly.GetTypes(); }
        catch (ReflectionTypeLoadException e) { types = e.Types; }
        foreach (var type in types)
        {
            if (type == null || type.IsAbstract || !typeof(BasePlugin).IsAssignableFrom(type)) continue;
            var meta = type.GetCustomAttribute<BepInPlugin>(inherit: false);
            if (meta != null) list.Add(new ClientModObservation(meta.GUID, meta.Name, meta.Version?.ToString() ?? "", ClientModSources.Progenitor));
        }

        return list;
    }

    // BepInEx 6's [PatcherPluginInfo(string GUID, string Name, string Version)] (BepInEx.Preloader.Core.Patching),
    // read as attribute data so nothing is instantiated and Core needs no preloader reference.
    private static List<ClientModObservation> ReadPatchers(Assembly assembly)
    {
        var list = new List<ClientModObservation>();
        Type?[] types;
        try { types = assembly.GetTypes(); }
        catch (ReflectionTypeLoadException e) { types = e.Types; }
        foreach (var type in types)
        {
            if (type == null) continue;
            IList<CustomAttributeData> data;
            try { data = type.GetCustomAttributesData(); }
            catch { continue; }
            foreach (var a in data)
            {
                if (!string.Equals(a.AttributeType.Name, "PatcherPluginInfoAttribute", StringComparison.Ordinal)) continue;
                var args = a.ConstructorArguments;
                if (args.Count < 3) continue;
                list.Add(new ClientModObservation(args[0].Value as string ?? "", args[1].Value as string ?? "", args[2].Value as string ?? "", ClientModSources.Patcher));
            }
        }

        return list;
    }

    private static IReadOnlyList<Install.InstalledLedgerRow>? _ledger;
    private static double _ledgerAt = double.NegativeInfinity;

    /// <summary>The package the install ledger installed a plugin from (its listing key), or empty.</summary>
    private static string PackageOf(string guid)
    {
        double now = Now();
        if (_ledger == null || now - _ledgerAt > 30)
        {
            try { _ledger = ModsMenu.JoinAdminLedger(); }
            catch { _ledger = Array.Empty<Install.InstalledLedgerRow>(); }
            _ledgerAt = now;
        }

        foreach (var row in ModMenuInventory.LedgerRowsForPlugin(_ledger, guid))
        {
            string package = ClientModInventory.PackageFromLedger(row.Source, row.Id);
            if (package.Length > 0) return package;
        }

        return "";
    }
#endif
}
