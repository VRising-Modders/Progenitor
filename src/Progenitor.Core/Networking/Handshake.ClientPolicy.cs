#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;
using System.Globalization;
using Progenitor.Ecs;
using ProjectM.Network;
using Unity.Entities;

namespace Progenitor.Networking;

/// <summary>
/// CLIENTPOLICY1 (docs/client-mod-policy.md), the server's live half: the request's capability row, the wait for the
/// client's <c>'PrCm'</c> report inside the answer deadline, the judgment (<see cref="ClientModJudge"/>), the refusal
/// and its record, and the bridge's TEST ONLY report injection. The decisions themselves are game-free
/// (<see cref="ClientModJudge"/>, <see cref="ClientPolicyGate"/>, <see cref="ClientModReportAssembler"/>).
/// </summary>
public static unsafe partial class Handshake
{
    internal static readonly ClientPolicyGate PolicyGate = new();
    internal static readonly ClientModReportAssembler ReportAssembler = new();

    // The users whose current request carried the ClientPolicy bit (only they may send a report).
    private static readonly HashSet<int> PolicyAsked = new();
    private static readonly HashSet<int> UnaskedReportWarned = new();
    private static int _uncheckedLetIn;

    /// <summary>CLIENTPOLICY3: the mid-session re-check per connected user (game-free decisions).</summary>
    internal static readonly ClientPolicyRecheckTracker Recheck = new();

    // CLIENTPOLICY3: what each user's last answer declared (the notice and the re-ask need PolicyRecheck and ClientPolicy).
    private static readonly Dictionary<int, Capability> ClientCaps = new();
    private static int _noticeSequence;

    private static void NoteClientCaps(int userIndex, Capability caps)
    {
        lock (ClientCaps)
        {
            if (ClientCaps.Count >= 512 && !ClientCaps.ContainsKey(userIndex)) ClientCaps.Clear();
            ClientCaps[userIndex] = caps;
        }
    }

    internal static Capability CapsOf(int userIndex)
    {
        lock (ClientCaps) return ClientCaps.TryGetValue(userIndex, out var c) ? c : Capability.None;
    }

    internal static bool SupportsItemStats(int userIndex) => (CapsOf(userIndex) & Capability.ItemStatSync) != 0;

    /// <summary>Fail closed before enabling stat content for already connected users.</summary>
    internal static bool ItemStatClientsCompatible() => ClientsHaveCapabilities(Capability.ItemStatSync);

    internal static bool ItemLedgerClientsCompatible() => ClientsHaveCapabilities(Capability.ItemLedgerChunks
        | RequiredPrefabCapacityCapability(Content.PrefabSyncBudget.Cap));

    private static bool ClientsHaveCapabilities(Capability required)
    {
        var world = Worlds.Server;
        if (world == null || !world.IsCreated) return true;
        try
        {
            var em = world.EntityManager;
            var query = Query.Build(em, includeDisabled: true, includeSystems: false,
                ComponentIndex.Of<User>(ComponentType.AccessMode.ReadOnly));
            try
            {
                var entities = query.ToEntityArray(Unity.Collections.Allocator.Temp);
                try
                {
                    for (int i = 0; i < entities.Length; i++)
                    {
                        var user = em.GetComponentData<User>(entities[i]);
                        if (user.IsConnected && (CapsOf(user.Index) & required) != required) return false;
                    }
                    return true;
                }
                finally { entities.Dispose(); }
            }
            finally { query.Dispose(); }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Handshake: could not check equipment-stat client compatibility: " + e.Message);
            return false;
        }
    }

    /// <summary>How many joins were let in without a checkable report this Core generation (Let them join; readback).</summary>
    internal static int UncheckedLetIn => _uncheckedLetIn;

    /// <summary>
    /// TEST ONLY (the bridge's <c>join_admin inject_report</c>): a report that replaces the next real report of a user
    /// (by lowercase character name or SteamID), or <c>null</c> to drop the next real report (a client whose report
    /// never arrives). One use each.
    /// </summary>
    private static readonly Dictionary<string, (ClientModReport? Report, DateTime At)> InjectedNext = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Append the capability row when the policy is active; true when this user was asked for a report.</summary>
    private static bool PolicyAsksForReport(List<ModRecord> rows, int userIndex, string name)
    {
        bool ask = false;
        var policy = SafePolicy();
        if (policy != null && policy.Active)
        {
            // CLIENTPOLICY3: PolicyRecheck says this server re-checks mid-session, so the client keeps reporting changes.
            ask = Compare.AppendCapabilityRow(rows, Capability.ClientPolicy | Capability.PolicyRecheck | Capability.CountdownChat
                | ClientModReportClient.MidSessionBit(policy.MidSession));
            if (!ask)
                Plugin.Logger?.LogWarning("ClientPolicy: the handshake request to " + name + " has no free slot for the client mod report ask; "
                    + "the join is judged as one whose mods can't be checked.");
        }
        else if (policy != null)
        {
            // No policy now: only "this server can re-check", so a client reports if the admins turn one on mid-session
            // (asked through the re-signed catalog). Without a free slot that client is simply not re-asked.
            Compare.AppendCapabilityRow(rows, Capability.PolicyRecheck | Capability.CountdownChat | ClientModReportClient.MidSessionBit(policy.MidSession));
        }

        // Mod compatibility C1/C2: every request says this server reads feature tables and versioned messages.
        Compare.MergeCapabilityBits(rows, ModCompatCaps.RequestBitsNow());
        // SUPPORT1: this server sends its logs to an admin over 'PrSL' (Get help > Include this server's logs).
        Compare.MergeCapabilityBits(rows, Capability.ItemDefinitionPriority | Capability.ServerLogs);

        lock (PolicyAsked)
        {
            if (ask) PolicyAsked.Add(userIndex);
            else PolicyAsked.Remove(userIndex);
        }

        return ask;
    }

    /// <summary>
    /// A first accepted answer under an active policy: wait for the report (true; <paramref name="accept"/> or a refusal
    /// follows), or false when there is no policy and the caller accepts at once.
    /// </summary>
    private static bool GateOnClientPolicy(World? world, Entity userEntity, int userIndex, string name, ulong steamId,
        Capability clientCaps, Action accept)
    {
        var policy = SafePolicy();
        if (policy == null || !policy.Active) return false;
        bool asked;
        lock (PolicyAsked) asked = PolicyAsked.Contains(userIndex);
        void Decide(ClientModReport? report, string trigger) =>
            DecideClientPolicy(world, userEntity, userIndex, name, steamId, clientCaps, report, trigger, accept);

        if (!asked || (clientCaps & Capability.ClientPolicy) == 0)
        {
            Decide(null, asked ? "no-capability" : "not-asked");
            return true;
        }

        // Compat C3: the report already came while this answer's judgment waited for the feature table.
        if (TakeReportDuringHold(userIndex, out var early, out string earlyHow))
        {
            Plugin.Logger?.LogInfo("ClientPolicy: " + name + " answered ok; judging the join on the report it sent during the feature table hold");
            Decide(early, earlyHow == "injected" ? "injected" : "report");
            return true;
        }

        int timeout = ReadAnswerTimeoutSeconds();
        if (timeout <= 0) timeout = DefaultAnswerTimeoutSeconds;
        AnswerWatch.OnAwaitingReport(userIndex);
        int generation = PolicyGate.Begin(userIndex, name, LiveClock() + timeout, Decide);
        Plugin.Logger?.LogInfo("ClientPolicy: " + name + " answered ok; waiting up to " + timeout.ToString(CultureInfo.InvariantCulture)
            + " s for the client mod report");
        Dev.MainThread.PostDelayed(timeout, () =>
        {
            try
            {
                var waiting = PolicyGate.TryExpire(userIndex, generation, double.PositiveInfinity);
                waiting?.Decide?.Invoke(null, "no-report");
            }
            catch (Exception e) { Plugin.Logger?.LogError("ClientPolicy: report deadline: " + e); }
        }, DeadlineOwner);
        return true;
    }

    private static void DecideClientPolicy(World? world, Entity userEntity, int userIndex, string name, ulong steamId, Capability clientCaps,
        ClientModReport? report, string trigger, Action accept)
    {
        bool joinFlow = (clientCaps & Capability.JoinFlow) != 0;
        ClientModVerdict verdict;
        try { verdict = ClientModJudge.Judge(SafePolicy(), report, JoinAdminServer.PolicyFacts()); }
        catch (Exception e)
        {
            // A judgment that cannot run must not keep a join hanging: it is let in and the cause logged.
            Plugin.Logger?.LogError("ClientPolicy: judging " + name + " threw; letting the join through: " + e);
            accept();
            return;
        }

        ClientModPolicyRecords.NoteJudgment(userIndex, name, verdict, trigger);
        if (verdict.Outcome != ClientJudgmentOutcome.Refuse)
        {
            if (verdict.Outcome == ClientJudgmentOutcome.PassUnchecked)
            {
                _uncheckedLetIn++;
                Plugin.Logger?.LogInfo("ClientPolicy: " + name + "'s mods can't be checked (" + trigger + "); let in (Players whose mods can't be checked: Let them join)");
            }
            else Plugin.Logger?.LogInfo("ClientPolicy: " + name + " passed the client mod policy (" + trigger + ")");
            // CLIENTPOLICY3: the session's re-check starts from this report (the required rows it meets count as met).
            try { Recheck.OnJoinAccepted(userIndex, SafePolicy(), report); }
            catch (Exception e) { Plugin.Logger?.LogWarning("ClientPolicy: starting the re-check record threw: " + e.Message); }
            accept();
            return;
        }

        // Rule 4 (2026-10-05): when every problem can be fixed on the client during the join (a required client mod
        // Thunderstore serves, or SELFFIX1 one the client's report claims it can fix itself; a not-allowed mod the client can
        // pause), hold the join open in the install window instead.
        // The client re-answers once fixed; that answer is judged again from its fresh report.
        RefusedJoins.NoteRows(userIndex, ClientModJudge.RecordRows(verdict), steamId, ClientModJudge.RecordNames(verdict));
        if (TryDeferPolicy(world, userEntity, userIndex, name, clientCaps, verdict, report))
            return;

        Plugin.Logger?.LogWarning("ClientPolicy: " + name + " refused (" + trigger + "): " + verdict.Reason);
        RefusedJoins.Commit(userIndex);
        AnswerWatch.OnMismatch(userIndex, verdict.Reason);
        if (world != null && world.IsCreated) SendResultAndKick(world, userEntity, userIndex, name, verdict.Reason, timeoutKick: false);
        // The signed join section (the message, the policy rows) for a client that draws the join screens; no catalog rows.
        try { ModManifest.OnHandshakeDenied(userIndex, new List<string>(), joinFlow); }
        catch (Exception e) { Plugin.Logger?.LogWarning("ClientPolicy: the refusal's join section threw: " + e.Message); }
    }

    /// <summary>
    /// Rule 4 of docs/client-mod-policy.md (2026-10-05): open the install window for a policy refusal whose every problem
    /// the client can fix during the join (<see cref="InstallWindowFixes.PolicyFixable"/>). Same window, budget and
    /// capability rule as the handshake rows' deferral; false (nothing sent) otherwise.
    /// </summary>
    private static bool TryDeferPolicy(World? world, Entity userEntity, int userIndex, string name, Capability clientCaps,
        ClientModVerdict verdict, ClientModReport? report)
    {
        try
        {
            int seconds = ReadInstallGraceSeconds();
            if (seconds <= 0) return false;
            if (!Compare.CanSpendInstallWindow(clientCaps))
            {
                Plugin.Logger?.LogInfo("ClientPolicy: " + name + " not deferred: " + Compare.NoInstallWindowCapability);
                return false;
            }

            if (InstallGrace.HasSpentGrace(userIndex))
            {
                Plugin.Logger?.LogInfo("ClientPolicy: " + name + " not deferred: no install window is available for this user");
                return false;
            }

            try { ServerPackages?.Want(ModManifest.CatalogPackages()); }
            catch { /* a refresh that cannot start leaves the last read */ }
            var packages = ServerPackages;
            if (!InstallWindowFixes.PolicyFixable(verdict, SafePolicy(), report, packages == null ? null : packages.Availability, out var keys, out var kinds, out var why))
            {
                Plugin.Logger?.LogInfo("ClientPolicy: " + name + " not deferred: " + why);
                return false;
            }

            // SELFFIX1: which fix opened the window, per key (a claimed one is the client's word only).
            var opened = new List<string>(keys.Count);
            for (int i = 0; i < keys.Count; i++)
                opened.Add(keys[i] + " (" + InstallWindowFixes.FixName(i < kinds.Count ? kinds[i] : MissFix.None) + ")");
            Plugin.Logger?.LogInfo("ClientPolicy: " + name + " install window for: " + string.Join(", ", opened));

            return OpenInstallWindow(world, userEntity, userIndex, name, keys, "client mod policy", verdict.Reason, seconds,
                (clientCaps & Capability.JoinFlow) != 0);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("ClientPolicy: the install window check threw; refusing as before: " + e.Message);
            return false;
        }
    }

    private static void OnClientModReport(int _, Entity userEntity, User user, byte* payload, int length)
    {
        try
        {
            if (ReceiveGate.HandlerMustRefuse(userEntity == Entity.Null, user.Index))
            {
                Plugin.Logger?.LogWarning(ReceiveGate.HandlerRefusedLog("ClientPolicy", ReceiveGate.Fourcc(ClientModReportWire.EventId)));
                return;
            }

            if (payload == null || length <= 0) return;
            int index = user.Index;
            // F41 chat: the client's held-notice message ('PrCm' kind 3) shares the id with the report's chunks.
            if (ClientPolicyHeldWire.IsHeld(new ReadOnlySpan<byte>(payload, length)))
            {
                OnNoticeHeld(index, Compare.PlayerLabel(user.CharacterName.ToString(), user.PlatformId), new ReadOnlySpan<byte>(payload, length));
                return;
            }

            bool asked;
            lock (PolicyAsked) asked = PolicyAsked.Contains(index);
            if (!asked)
            {
                // A report nobody asked for is dropped (it would only be stored for no purpose).
                lock (UnaskedReportWarned)
                    if (UnaskedReportWarned.Add(index))
                        Plugin.Logger?.LogWarning("ClientPolicy: rejected a report from " + Compare.PlayerLabel(user.CharacterName.ToString(), user.PlatformId) + " (user " + index.ToString(CultureInfo.InvariantCulture) + "): this server did not ask it for one");
                return;
            }

            if (!ReportAssembler.Accept(index, new ReadOnlySpan<byte>(payload, length), out var report, out var error))
            {
                if (error != null) Plugin.Logger?.LogWarning("ClientPolicy: rejected a report packet from " + Compare.PlayerLabel(user.CharacterName.ToString(), user.PlatformId) + " (" + length.ToString(CultureInfo.InvariantCulture) + " bytes): " + error);
                else Plugin.Logger?.LogInfo("ClientPolicy: report chunk from " + Compare.PlayerLabel(user.CharacterName.ToString(), user.PlatformId) + " (" + length.ToString(CultureInfo.InvariantCulture) + " bytes); waiting for the rest");
                return;
            }

            string how = "report";
            if (TakeInjection(user.CharacterName.ToString(), user.PlatformId, out var injected))
            {
                Plugin.Logger?.LogWarning("ClientPolicy: TEST ONLY: " + Compare.PlayerLabel(user.CharacterName.ToString(), user.PlatformId) + "'s real report was "
                    + (injected == null ? "dropped" : "replaced by an injected one") + " (bridge)");
                if (injected == null) return;
                report = injected;
                how = "injected";
            }

            OnReportComplete(index, report!, how, Compare.PlayerLabel(user.CharacterName.ToString(), user.PlatformId));
        }
        catch (Exception e) { Plugin.Logger?.LogError("ClientPolicy: report handler: " + e); }
    }

    // Mod compatibility C3 (review fix): the client sends its answer, then its mod report, then its feature table, so the
    // report arrives while the answer's judgment is held for the table (TableHold) and nothing waits for it yet. It is
    // kept here, one per user, and the policy gate takes it when the held judgment runs. Cleared at each answer and on
    // disconnect.
    private static readonly Dictionary<int, (ClientModReport Report, string How)> ReportsDuringHold = new();

    /// <summary>Compat C3: forget a report kept during a feature table hold (a new answer, a disconnect).</summary>
    internal static void ForgetReportDuringHold(int userIndex)
    {
        lock (ReportsDuringHold) ReportsDuringHold.Remove(userIndex);
    }

    private static bool TakeReportDuringHold(int userIndex, out ClientModReport report, out string how)
    {
        lock (ReportsDuringHold)
        {
            if (ReportsDuringHold.TryGetValue(userIndex, out var kept))
            {
                ReportsDuringHold.Remove(userIndex);
                report = kept.Report;
                how = kept.How;
                return true;
            }
        }

        report = null!;
        how = "";
        return false;
    }

    /// <summary>A whole report: it decides a waiting join, or (mid-session, CLIENTPOLICY3) is re-checked against the policy.</summary>
    private static void OnReportComplete(int userIndex, ClientModReport report, string how, string name)
    {
        var waiting = PolicyGate.TakeOnReport(userIndex);
        bool midSession = waiting == null && UserAnswerStatus(userIndex) == "ok";
        if (waiting == null && !midSession && TableHold.IsWaiting(userIndex))
        {
            ClientModPolicyRecords.NoteReport(userIndex, report, how == "injected" ? "injected" : "join");
            lock (ReportsDuringHold) ReportsDuringHold[userIndex] = (report, how);
            Plugin.Logger?.LogInfo("ClientPolicy: received report from " + name + " (" + report.Entries.Count.ToString(CultureInfo.InvariantCulture) + " mods"
                + (report.Truncated ? ", cut" : "") + (how == "injected" ? ", injected" : "") + "); kept until its answer's judgment, which waits for the feature table");
            return;
        }
        ClientModPolicyRecords.NoteReport(userIndex, report, waiting != null ? (how == "injected" ? "injected" : "join") : (how == "injected" ? "injected" : "resend"));
        Plugin.Logger?.LogInfo("ClientPolicy: received report from " + name + " (" + report.Entries.Count.ToString(CultureInfo.InvariantCulture) + " mods"
            + (report.Truncated ? ", cut" : "") + (how == "injected" ? ", injected" : "") + ")"
            + (waiting != null ? "; judging the join" : midSession ? "; re-checking it mid-session" : "; recorded (the join is not decided yet)"));
        if (waiting != null)
        {
            waiting.Decide?.Invoke(report, how == "injected" ? "injected" : "report");
            return;
        }

        if (midSession) RunRecheck(userIndex, name, how == "injected" ? "injected" : "report", bypassFloor: false);
    }

    // ---- CLIENTPOLICY3: mid-session re-checks ------------------------------------------------------------------------

    /// <summary>
    /// Re-judge <paramref name="userIndex"/>'s latest report against the current policy and act on the admins' choice
    /// (<see cref="ClientPolicyRecheckTracker"/>). Reports are judged at most every
    /// <see cref="ClientPolicyRecheckLimits.MinJudgeSeconds"/>; one inside that floor is judged (the latest) when it passes.
    /// </summary>
    private static void RunRecheck(int userIndex, string name, string trigger, bool bypassFloor)
    {
        try
        {
            double now = LiveClock();
            if (!bypassFloor && !Recheck.Admit(userIndex, now, out double wait))
            {
                if (wait < 0) return; // one held re-check is already scheduled; it reads the latest report
                Plugin.Logger?.LogInfo("ClientPolicy: " + name + "'s report is re-checked in " + wait.ToString("0.#", CultureInfo.InvariantCulture)
                    + " s (at most one re-check every " + ClientPolicyRecheckLimits.MinJudgeSeconds.ToString(CultureInfo.InvariantCulture) + " s)");
                Dev.MainThread.PostDelayed(wait, () =>
                {
                    Recheck.ReleaseHeld(userIndex);
                    RunRecheck(userIndex, name, trigger, bypassFloor: true);
                }, DeadlineOwner);
                return;
            }

            var policy = SafePolicy();
            if (policy == null) return;
            var record = ClientModPolicyRecords.ReportOf(userIndex);
            var facts = JoinAdminServer.PolicyFacts();
            var decision = Recheck.Judge(userIndex, policy, record?.Report, facts, trigger, now);
            if (record != null && policy.Active)
                ClientModPolicyRecords.NoteJudgment(userIndex, name, ClientModJudge.Judge(policy, record.Report, facts), "recheck-" + trigger);
            ApplyRecheck(userIndex, name, decision);
        }
        catch (Exception e) { Plugin.Logger?.LogError("ClientPolicy: the mid-session re-check of " + name + " threw: " + e); }
    }

    private static void ApplyRecheck(int userIndex, string name, RecheckDecision d)
    {
        if (d.Items.Count > 0 || d.CancelKick || d.KickNow)
        {
            string line = "ClientPolicy: re-check of " + name + ": " + d.Describe();
            if (d.KickNow || d.ArmKick) Plugin.Logger?.LogWarning(line);
            else Plugin.Logger?.LogInfo(line);
        }

        if (d.KickNow)
        {
            KickForPolicy(userIndex, name, d);
            return;
        }

        if (d.Notice != RecheckNoticeKind.None) SendRecheckNotice(userIndex, name, d);
        if (d.ArmKick)
        {
            int generation = d.KickGeneration;
            Dev.MainThread.PostDelayed(d.Seconds, () =>
            {
                try
                {
                    if (!Recheck.IsDue(userIndex, generation, LiveClock())) return;
                    RunRecheck(userIndex, name, "deadline", bypassFloor: true);
                }
                catch (Exception e) { Plugin.Logger?.LogError("ClientPolicy: the re-check deadline threw: " + e); }
            }, DeadlineOwner);

            // F41 chat: a countdown notice held on the client keeps its chat lines across a re-arm.
            try { RearmHeldChat(userIndex, name, generation); }
            catch (Exception e) { Plugin.Logger?.LogWarning("ClientPolicy: moving the countdown chat to the re-armed disconnect threw: " + e.Message); }
        }
    }

    /// <summary>
    /// The notice: the game-styled popup on a client that declared <see cref="Capability.PolicyRecheck"/> (it pauses the
    /// pausable mods it names), else the same words as a System chat line. A disconnect is also said in chat, because the
    /// popup goes with the world.
    /// </summary>
    private static void SendRecheckNotice(int userIndex, string name, RecheckDecision d)
    {
        if (!TryConnectedUserAt(userIndex, out var world, out var entity, out var user)) return;
        bool popup = (CapsOf(userIndex) & Capability.PolicyRecheck) != 0;
        bool sent = false;
        if (popup)
        {
            try
            {
                var bootstrap = world.GetExistingSystemManaged<ProjectM.ServerBootstrapSystem>();
                byte[] packet = ClientPolicyNoticeWire.Encode(d.Notice, d.Seconds, d.NoticeItems, System.Threading.Interlocked.Increment(ref _noticeSequence));
                sent = bootstrap != null && CustomEvents.SendToUser(bootstrap, user.Index, ClientModReportWire.EventId, new ReadOnlySpan<byte>(packet));
            }
            catch (Exception e) { Plugin.Logger?.LogWarning("ClientPolicy: the notice to " + name + " threw: " + e.Message); }
        }

        bool chat = !sent || d.Notice == RecheckNoticeKind.Disconnected;
        if (chat)
        {
            try { Progenitor.Chat.Chat.SendTo(world, entity, ClientPolicyNoticeText.Chat(d.Notice, d.NoticeItems, d.Seconds), ChatMessageType.System, Entity.Null); }
            catch (Exception e) { Plugin.Logger?.LogWarning("ClientPolicy: the chat notice to " + name + " threw: " + e.Message); }
        }

        Plugin.Logger?.LogInfo("ClientPolicy: notice to " + name + " (" + ClientPolicyNoticeWire.ActionWord(d.Notice)
            + (sent ? ", popup" : "") + (chat ? ", chat" : "") + ")");
    }

    /// <summary>F41 chat: when the countdown is said in chat while a player's countdown notice is held (game-free).</summary>
    internal static readonly CountdownChatSchedule HeldChat = new();

    /// <summary>
    /// F41 chat (decided 2026-10-08): the client says its disconnect countdown notice is held behind an open
    /// confirmation (or no longer is). While it is and the disconnect is armed, the countdown goes to that player as a
    /// private System chat line now and at <see cref="CountdownChatSchedule.Marks"/> seconds left; never while the notice
    /// is on screen. A re-armed disconnect carries the lines over (<see cref="RearmHeldChat"/>); a called-off one ends
    /// them. Messages are rate limited per player and acted on only while a disconnect is armed (a "no longer held" is
    /// always applied, quietly when throttled).
    /// </summary>
    private static void OnNoticeHeld(int userIndex, string name, ReadOnlySpan<byte> packet)
    {
        double now = LiveClock();
        bool throttled = HeldChat.Throttled(userIndex, now);
        if (!ClientPolicyHeldWire.TryDecode(packet, out bool held, out var local))
        {
            if (!throttled) Plugin.Logger?.LogWarning("ClientPolicy: dropped a malformed held-notice message from " + name);
            return;
        }

        if (!held)
        {
            bool was = HeldChat.IsHeld(userIndex);
            HeldChat.OnHeld(userIndex, false, null, null, 0, now, out _);
            if (was && !throttled) Plugin.Logger?.LogInfo("ClientPolicy: " + name + "'s countdown notice is no longer held");
            return;
        }

        int? generation = Recheck.ArmedGeneration(userIndex);
        if (throttled || generation == null) return; // nothing armed: nothing to say, nothing to log
        double left = Recheck.SecondsLeft(userIndex, now) ?? 0;
        bool sendNow = HeldChat.OnHeld(userIndex, true, local, generation, left, now, out var marks);
        Plugin.Logger?.LogInfo("ClientPolicy: " + name + "'s countdown notice is held behind an open confirmation");
        StartHeldChat(userIndex, name, generation.Value, left, sendNow, marks, "held");
    }

    /// <summary>
    /// F41 chat (review 2026-10-08): the disconnect was re-armed (a fix of one mod leaves another with a later deadline, with
    /// no new notice for the client to report). While the notice is held, the lines move to the new countdown.
    /// </summary>
    private static void RearmHeldChat(int userIndex, string name, int generation)
    {
        double now = LiveClock();
        double left = Recheck.SecondsLeft(userIndex, now) ?? 0;
        if (!HeldChat.IsHeld(userIndex)) return;
        bool sendNow = HeldChat.OnRearmed(userIndex, generation, left, now, out var marks);
        StartHeldChat(userIndex, name, generation, left, sendNow, marks, "re-armed");
    }

    private static void StartHeldChat(int userIndex, string name, int gen, double left, bool sendNow, List<int> marks, string why)
    {
        if (sendNow) SendCountdownChat(userIndex, name, gen, why, null);
        foreach (int mark in marks)
        {
            int m = mark;
            Dev.MainThread.PostDelayed(Math.Max(0.1, left - m), () =>
            {
                try
                {
                    if (Recheck.ArmedGeneration(userIndex) != gen || !HeldChat.MarkDue(userIndex, gen, m, LiveClock())) return;
                    SendCountdownChat(userIndex, name, gen, m.ToString(CultureInfo.InvariantCulture) + " s mark", m);
                }
                catch (Exception e) { Plugin.Logger?.LogError("ClientPolicy: the countdown chat line threw: " + e); }
            }, DeadlineOwner);
        }
    }

    private static void SendCountdownChat(int userIndex, string name, int generation, string why, int? mark)
    {
        double now = LiveClock();
        if (Recheck.ArmedGeneration(userIndex) != generation) return;
        double? left = Recheck.SecondsLeft(userIndex, now);
        if (left == null) return;
        // A mark says its own number (a timer a little early must not read 31 s at the 30 s mark).
        int seconds = CountdownChatSchedule.SecondsToSay(left.Value, mark);
        string line = ClientPolicyNoticeText.CountdownChat(Recheck.ArmedItems(userIndex), seconds, HeldChat.Local(userIndex));
        if (line.Length == 0 || !TryConnectedUserAt(userIndex, out var world, out var entity, out _)) return;
        bool sent = false;
        try { sent = Progenitor.Chat.Chat.SendTo(world, entity, line, ChatMessageType.System, Entity.Null); }
        catch (Exception e) { Plugin.Logger?.LogWarning("ClientPolicy: the countdown chat line to " + name + " threw: " + e.Message); }
        if (sent) HeldChat.NoteSent(userIndex, line, now);
        Plugin.Logger?.LogInfo("ClientPolicy: countdown chat to " + name + " (" + why + ", " + seconds.ToString(CultureInfo.InvariantCulture)
            + " s left, notice held): sent=" + sent + ": " + line);
    }

    /// <summary>The disconnect: the notice (popup and chat), then the game's own Kicked disconnect after the flush hop.</summary>
    private static void KickForPolicy(int userIndex, string name, RecheckDecision d)
    {
        if (!TryConnectedUserAt(userIndex, out _, out _, out var user)) return;
        ulong steamId = user.PlatformId;
        SendRecheckNotice(userIndex, name, d);
        Dev.MainThread.PostDelayed(HandshakeAnswerWatch.KickFlushSeconds, () =>
        {
            try
            {
                if (!TryConnectedUserAt(userIndex, out var world, out var entity, out var now) || now.PlatformId != steamId) return;
                // The last check: a clean report in the flush hop (the player paused or turned it off just in time)
                // calls the disconnect off; otherwise the reason names what is still there.
                var policy = SafePolicy();
                var latest = ClientModPolicyRecords.ReportOf(userIndex)?.Report;
                var still = Recheck.KickWorthyNow(userIndex, policy, latest, JoinAdminServer.PolicyFacts());
                if (still.Count == 0)
                {
                    Plugin.Logger?.LogInfo("ClientPolicy: the disconnect of " + name + " is called off: the latest report is clean");
                    var resolved = new RecheckDecision { Choice = policy?.MidSession ?? MidSessionPolicy.Warn, Trigger = "last-check", Notice = RecheckNoticeKind.Resolved };
                    SendRecheckNotice(userIndex, name, resolved);
                    return;
                }

                string reason = ClientPolicyNoticeText.KickReason(still);
                bool kicked = Players.PlayerControl.Disconnect(world, entity, reason);
                Plugin.Logger?.LogWarning("ClientPolicy: disconnected " + name + " (" + reason + "): " + kicked);
            }
            catch (Exception e) { Plugin.Logger?.LogError("ClientPolicy: the disconnect of " + name + " threw: " + e); }
        }, DeadlineOwner);
    }

    /// <summary>
    /// <see cref="JoinAdminServer.PolicyChanged"/>: the admins changed the policy. Every player in the world is re-judged
    /// from their last report, everything they have loaded counting as loaded before the change. A player never asked for a
    /// report (the policy was off when they joined) is asked now, through the re-signed catalog the scan resends: a client
    /// that declared <see cref="Capability.PolicyRecheck"/> reports when that catalog brings a policy.
    /// </summary>
    internal static void OnPolicyChangedMidSession(string op)
    {
        var world = Worlds.Server;
        if (world == null || !world.IsCreated) return;
        var policy = SafePolicy();
        if (policy == null) return;
        int judged = 0, asked = 0, unchecked_ = 0;
        foreach (var (index, label) in ConnectedOkUsers(world))
        {
            var record = ClientModPolicyRecords.ReportOf(index);
            if (record != null)
            {
                Recheck.OnPolicyChange(index, record.Report, policy);
                RunRecheck(index, label, "policy-change", bypassFloor: true);
                judged++;
                continue;
            }

            if (!policy.Active) continue;
            var caps = CapsOf(index);
            if ((caps & Capability.ClientPolicy) != 0 && (caps & Capability.PolicyRecheck) != 0)
            {
                lock (PolicyAsked) PolicyAsked.Add(index);
                Recheck.OnPolicyChange(index, null, policy);
                asked++;
            }
            else unchecked_++;
        }

        // REQRULE2: the rule as it stands now, so each client's own wording (the uninstall boxes' Required by) follows it.
        BroadcastPolicyRule(op);
        Plugin.Logger?.LogInfo("ClientPolicy: policy changed (" + op + "); re-checked " + judged.ToString(CultureInfo.InvariantCulture)
            + " player(s) from their last report, asked " + asked.ToString(CultureInfo.InvariantCulture) + " for one through the re-signed catalog"
            + (unchecked_ > 0 ? ", " + unchecked_.ToString(CultureInfo.InvariantCulture) + " can't be checked mid-session (an older Progenitor, or none)" : ""));
    }

    /// <summary>
    /// REQRULE2 / REQRULE5: send one connected client the mid-session rule and the Disconnect them grace
    /// (<see cref="ClientPolicyRuleWire"/>), when it declared <see cref="Capability.PolicyRuleUpdates"/> and this server has a
    /// policy config. True when it was sent.
    /// </summary>
    internal static bool SendPolicyRuleTo(World? world, int userIndex, Capability clientCaps)
    {
        if (world == null || (clientCaps & Capability.PolicyRuleUpdates) == 0 || SafePolicy() == null) return false;
        try { return TryConnectedUserAt(userIndex, out var w, out _, out var user) && SendPolicyRule(w, user.Index); }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("ClientPolicy: the rule message threw: " + e.Message);
            return false;
        }
    }

    /// <summary>The post-ok send: the user's entity is in hand, so no query (review L5).</summary>
    internal static bool SendPolicyRuleTo(World? world, Entity userEntity, Capability clientCaps)
    {
        if (world == null || !world.IsCreated || (clientCaps & Capability.PolicyRuleUpdates) == 0 || SafePolicy() == null) return false;
        try
        {
            var em = world.EntityManager;
            if (!em.Exists(userEntity) || !em.HasComponent<User>(userEntity)) return false;
            var user = em.GetComponentData<User>(userEntity);
            return user.IsConnected && SendPolicyRule(world, user.Index);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("ClientPolicy: the rule message threw: " + e.Message);
            return false;
        }
    }

    private static bool SendPolicyRule(World world, int userIndex)
    {
        var policy = SafePolicy();
        if (policy == null) return false;
        var bootstrap = world.GetExistingSystemManaged<ProjectM.ServerBootstrapSystem>();
        byte[] packet = ClientPolicyRuleWire.Encode(policy.MidSession, ClientPolicyRecheckLimits.DisconnectGraceSeconds);
        return bootstrap != null && CustomEvents.SendToUser(bootstrap, userIndex, ClientModReportWire.EventId, new ReadOnlySpan<byte>(packet));
    }

    private static string _lastRuleBroadcast = "";
    private static double _lastRuleBroadcastAt = double.NegativeInfinity;

    /// <summary>
    /// REQRULE2 / REQRULE5: the rule (or the grace) changed: tell every connected client that reads the rule message. One
    /// query for all of them (review L5). The same rule told again within two seconds (the admin channel's store raises the
    /// cfg entry's change too) is not sent twice.
    /// </summary>
    internal static void BroadcastPolicyRule(string why)
    {
        var world = Worlds.Server;
        if (world == null || !world.IsCreated) return;
        var policy = SafePolicy();
        if (policy == null) return;
        string words = ClientModRules.MidSessionWord(policy.MidSession) + ", " + ClientPolicyRecheckLimits.DisconnectGraceSeconds.ToString(CultureInfo.InvariantCulture) + " s";
        double now = LiveClock();
        if (string.Equals(words, _lastRuleBroadcast, StringComparison.Ordinal) && now - _lastRuleBroadcastAt < 2) return;
        _lastRuleBroadcast = words;
        _lastRuleBroadcastAt = now;
        int told = 0;
        var em = world.EntityManager;
        var entities = Query.Entities(em, includeDisabled: true, includeSystems: false, ComponentIndex.Of<User>(ComponentType.AccessMode.ReadOnly));
        for (int i = 0; i < entities.Count; i++)
        {
            try
            {
                var user = em.GetComponentData<User>(entities[i]);
                if (!user.IsConnected || UserAnswerStatus(user.Index) != "ok" || (CapsOf(user.Index) & Capability.PolicyRuleUpdates) == 0) continue;
                if (SendPolicyRule(world, user.Index)) told++;
            }
            catch { /* one user never stops the rest */ }
        }

        Plugin.Logger?.LogInfo("ClientPolicy: told " + told.ToString(CultureInfo.InvariantCulture) + " player(s) the rule after " + why + ": "
            + words + " to fix a mod under disconnect (older clients keep what they learned at join).");
    }

    private static BepInEx.Configuration.ConfigEntry<int>? _disconnectGraceSeconds;

    /// <summary>REQRULE5: bind <c>[ClientPolicy] DisconnectGraceSeconds</c> (server-wide; read at each judgment).</summary>
    private static void BindDisconnectGrace(BepInEx.Configuration.ConfigFile file)
    {
        var entry = _disconnectGraceSeconds = Progenitor.Config.ConfigHelper.Bind(file, ServerOptions.ClientPolicySection, ServerOptions.DisconnectGraceKey,
            ClientPolicyRecheckLimits.DefaultDisconnectGraceSeconds,
            "Seconds a player gets to fix a mod that breaks this server's client mod rules while playing (a mod you don't allow, or a "
            + "required mod removed or turned off) before they are disconnected, when the mid-session rule is Disconnect them. "
            + ClientPolicyRecheckLimits.MinDisconnectGraceSeconds + " to " + ClientPolicyRecheckLimits.MaxDisconnectGraceSeconds
            + " (clamped). A mod you don't allow that was loaded before the rules changed gets at least "
            + ClientPolicyRecheckLimits.MinPolicyChangeGraceSeconds + ".");
        ClientPolicyRecheckLimits.Configured = () => entry?.Value ?? ClientPolicyRecheckLimits.DefaultDisconnectGraceSeconds;
        // Review L6: a cfg reload (or an edit) of the grace tells connected players too. Through ConfigHelper, so the handler
        // goes with this Core generation (the file is process-lifetime).
        Progenitor.Config.ConfigHelper.AddSettingChanged(file, (_, e) =>
        {
            if (!ReferenceEquals(e.ChangedSetting, entry) || !Plugin.IsServerProcess) return;
            try { Dev.MainThread.Post(() => BroadcastPolicyRule("a change of [ClientPolicy] DisconnectGraceSeconds")); }
            catch { /* tearing down */ }
        });
    }

    /// <summary>The bridge's <c>join_admin recheck</c>: re-judge every player in the world from their last report now.</summary>
    internal static int RecheckAllNow()
    {
        var world = Worlds.Server;
        if (world == null || !world.IsCreated) return 0;
        int n = 0;
        foreach (var (index, label) in ConnectedOkUsers(world))
        {
            if (ClientModPolicyRecords.ReportOf(index) == null) continue;
            RunRecheck(index, label, "bridge", bypassFloor: true);
            n++;
        }

        return n;
    }

    /// <summary>The bridge's readback of one user's re-check.</summary>
    internal static Dictionary<string, object?>? DescribeRecheck(int userIndex)
    {
        var d = Recheck.Describe(userIndex, LiveClock());
        if (d == null) return null;
        var caps = CapsOf(userIndex);
        d["popupNotice"] = (caps & Capability.PolicyRecheck) != 0;
        d["countdownChat"] = HeldChat.Describe(userIndex, LiveClock());
        bool asked;
        lock (PolicyAsked) asked = PolicyAsked.Contains(userIndex);
        d["asked"] = asked;
        return d;
    }

    private static List<(int Index, string Label)> ConnectedOkUsers(World world)
    {
        var list = new List<(int, string)>();
        var em = world.EntityManager;
        var entities = Query.Entities(em, includeDisabled: true, includeSystems: false, ComponentIndex.Of<User>(ComponentType.AccessMode.ReadOnly));
        for (int i = 0; i < entities.Count; i++)
        {
            try
            {
                var user = em.GetComponentData<User>(entities[i]);
                if (!user.IsConnected || UserAnswerStatus(user.Index) != "ok") continue;
                list.Add((user.Index, Compare.PlayerLabel(user.CharacterName.ToString(), user.PlatformId)));
            }
            catch { /* one user never stops the rest */ }
        }

        return list;
    }

    private static bool TryConnectedUserAt(int userIndex, out World world, out Entity entity, out User user)
    {
        world = null!;
        entity = Entity.Null;
        user = default;
        var w = Worlds.Server;
        if (w == null || !w.IsCreated) return false;
        var em = w.EntityManager;
        var entities = Query.Entities(em, includeDisabled: true, includeSystems: false, ComponentIndex.Of<User>(ComponentType.AccessMode.ReadOnly));
        for (int i = 0; i < entities.Count; i++)
        {
            try
            {
                var u = em.GetComponentData<User>(entities[i]);
                if (!u.IsConnected || u.Index != userIndex) continue;
                world = w;
                entity = entities[i];
                user = u;
                return true;
            }
            catch { /* next */ }
        }

        return false;
    }

    private static bool TakeInjection(string name, ulong steamId, out ClientModReport? report)
    {
        report = null;
        lock (InjectedNext)
        {
            foreach (string key in new[] { steamId.ToString(CultureInfo.InvariantCulture), (name ?? "").Trim() })
            {
                if (key.Length == 0 || !InjectedNext.TryGetValue(key, out var hit)) continue;
                InjectedNext.Remove(key);
                if ((DateTime.UtcNow - hit.At).TotalMinutes > 30) continue; // stale
                report = hit.Report;
                return true;
            }
        }

        return false;
    }

    /// <summary>TEST ONLY: replace (or, with null, drop) the next real report of the user named <paramref name="who"/> (character name or SteamID).</summary>
    internal static void InjectNextReport(string who, ClientModReport? report)
    {
        lock (InjectedNext)
        {
            if (InjectedNext.Count >= 32) InjectedNext.Clear();
            InjectedNext[(who ?? "").Trim()] = (report, DateTime.UtcNow);
        }
    }

    internal static List<string> PendingInjections()
    {
        lock (InjectedNext)
        {
            var list = new List<string>();
            foreach (var pair in InjectedNext) list.Add(pair.Key + (pair.Value.Report == null ? " (drop)" : " (" + pair.Value.Report.Entries.Count + " mods)"));
            return list;
        }
    }

    /// <summary>
    /// TEST ONLY: deliver an injected report to a connected user now, as if it had just arrived. With nothing waiting it is
    /// a mid-session report (CLIENTPOLICY3): it is re-checked for real, with the notice and, under Disconnect the player,
    /// the disconnect.
    /// </summary>
    internal static string InjectReportNow(int userIndex, string name, ClientModReport report)
    {
        bool waiting = PolicyGate.IsWaiting(userIndex);
        bool ok = UserAnswerStatus(userIndex) == "ok";
        OnReportComplete(userIndex, report, "injected", name);
        if (waiting) return "decided the waiting join";
        if (ok)
        {
            var d = Recheck.Describe(userIndex, LiveClock());
            return "re-checked mid-session (" + (d != null && d.TryGetValue("last", out var w) ? w : "?") + ")";
        }

        // Nothing waits and the join is not ok yet: judge it for the readback only.
        var verdict = ClientModJudge.Judge(SafePolicy(), report, JoinAdminServer.PolicyFacts());
        ClientModPolicyRecords.NoteJudgment(userIndex, name, verdict, "injected");
        return "judged for the readback only (" + verdict.Word + "); the join is not decided yet, nobody is kicked";
    }

    private static void ClientPolicyOnDisconnect(int userIndex)
    {
        PolicyGate.OnDisconnect(userIndex);
        ReportAssembler.Drop(userIndex);
        ClientModPolicyRecords.OnDisconnect(userIndex);
        Recheck.Forget(userIndex);
        HeldChat.Forget(userIndex);
        lock (ClientCaps) ClientCaps.Remove(userIndex);
        lock (PolicyAsked) PolicyAsked.Remove(userIndex);
        lock (UnaskedReportWarned) UnaskedReportWarned.Remove(userIndex);
    }

    private static void ClientPolicyUninstall()
    {
        PolicyGate.Clear();
        ReportAssembler.Clear();
        Recheck.Clear();
        HeldChat.Clear();
        lock (ClientCaps) ClientCaps.Clear();
        lock (PolicyAsked) PolicyAsked.Clear();
        lock (UnaskedReportWarned) UnaskedReportWarned.Clear();
        lock (InjectedNext) InjectedNext.Clear();
    }
}
#endif
