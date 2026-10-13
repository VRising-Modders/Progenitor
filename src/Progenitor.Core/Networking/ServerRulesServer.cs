using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Progenitor.Install;
#if !PROGENITOR_TESTS
using ProjectM;
using ProjectM.Network;
using Unity.Entities;
#endif

namespace Progenitor.Networking;

/// <summary>
/// Settings organization stages 6 and 7, server: the Server rules channel (<c>'PrSv'</c>,
/// <see cref="CustomEventPolicy.ServerRulesEventId"/>). <c>get</c> answers the rules' state (any player who passed the
/// handshake may read it; the password is never in it). Every request spends the sender's read or edit budget (30 reads,
/// 10 edits a minute per SteamID), and every refusal is one audit line in <c>BepInEx/progenitor-settings.log</c>.
/// Game-free: the facts come from <see cref="FactsProvider"/> (live: <c>ServerRulesGame</c>).
/// </summary>
internal static partial class ServerRulesServer
{
    internal const int RateMaxEdits = ServerSettings.RateMaxEdits;
    internal const int RateMaxReads = 30;
    internal const double RateWindowSeconds = ServerSettings.RateWindowSeconds;

    internal const string NotAdminError = "Only server administrators can change the server rules.";
    internal const string HandshakeError = "The framework handshake is not ready.";
    internal const string UnknownOpError = "Unknown request.";

    private static RemoteInstallRateLimiter _edits = new(RateMaxEdits, RateWindowSeconds);
    private static RemoteInstallRateLimiter _reads = new(RateMaxReads, RateWindowSeconds);
    private static readonly Dictionary<ulong, double> RateAudited = new();

    /// <summary>What the server knows now (live: the running game and its files).</summary>
    internal static Func<ServerRulesFacts>? FactsProvider { get; set; }

    /// <summary>Where an audit line goes (live: the BepInEx log and <c>progenitor-settings.log</c>).</summary>
    internal static Action<string>? Audit { get; set; }

    internal static Func<DateTime> AuditClock { get; set; } = () => DateTime.UtcNow;

    internal static Func<double> Now { get; set; } = () => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;

    /// <summary>The rules' state now (the facts read once).</summary>
    internal static ServerRulesState BuildState()
    {
        ServerRulesFacts facts;
        try { facts = FactsProvider?.Invoke() ?? new ServerRulesFacts(); }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("ServerRules: reading the server's settings threw: " + e.Message);
            facts = new ServerRulesFacts();
        }

        facts.LockedBy ??= ServerSettingsLock.LockedBy;
        return ServerRulesStateBuilder.Build(facts);
    }

    /// <summary>
    /// Decide one request (the production path and the tests). Order: rate limit (reads and edits each spend their own
    /// budget), handshake, then the operation; a write also needs the sender on the admin list or adminauthed.
    /// </summary>
    internal static ServerRulesResponse Apply(ServerRulesRequest request, bool handshakeOk, in RemoteInstallActor actor)
    {
        request ??= new ServerRulesRequest();
        string op = (request.Op ?? "").Trim().ToLowerInvariant();
        var response = new ServerRulesResponse { Nonce = request.Nonce, Op = op, Key = request.Key ?? "" };
        // WORLDRESTORE1: reading the restore points spends the read budget like get.
        bool edit = op != ServerRulesRequest.OpGet && op != ServerRulesRequest.OpBackups;
        double now = Now();
        string row = ServerRules.Id(string.IsNullOrEmpty(request.Key) ? "?" : request.Key);
        if (!(edit ? _edits : _reads).TryTake(actor.SteamId, now))
        {
            response.Error = ServerSettings.RateLimitedError;
            if (!RateAudited.TryGetValue(actor.SteamId, out double last) || now - last >= RateWindowSeconds || now < last)
            {
                if (RateAudited.Count >= 1024) RateAudited.Clear();
                RateAudited[actor.SteamId] = now;
                WriteAudit(false, actor, row, "", AuditValue(request), response.Error);
            }

            return response;
        }

        if (!handshakeOk)
        {
            response.Error = HandshakeError;
            WriteAudit(false, actor, row, "", AuditValue(request), response.Error);
            return response;
        }

        switch (op)
        {
            case ServerRulesRequest.OpGet:
                response.Ok = true;
                response.State = BuildState();
                return response;
            case ServerRulesRequest.OpWrite:
                return Write(request, actor, response, row);
            case ServerRulesRequest.OpBackups:
            case ServerRulesRequest.OpRestore:
            case ServerRulesRequest.OpRestoreCancel:
            case ServerRulesRequest.OpBackupsKeep:
            case ServerRulesRequest.OpBackupNow:
            case ServerRulesRequest.OpBackupsSet:
                return Backups(request, op, actor, response, BackupsRow(op, request.Key));
            default:
                response.Error = UnknownOpError;
                WriteAudit(false, actor, row, "", AuditValue(request), response.Error);
                return response;
        }
    }

    /// <summary>The audit row of a Backups operation (BACKUPNOW1: Back up now and the four backup settings have their own).</summary>
    private static string BackupsRow(string op, string? key) => op switch
    {
        ServerRulesRequest.OpBackupsKeep => ServerRules.Id("KeepBackups"),
        ServerRulesRequest.OpBackupNow => ServerBackups.BackupNowSettingId,
        ServerRulesRequest.OpBackupsSet => ServerBackups.SettingIdOf(key) ?? ServerRules.Id("Backup?"),
        _ => ServerRules.Id("WorldRestore"),
    };

    /// <summary>The audit's value for a request: a password is never written.</summary>
    internal static string AuditValue(ServerRulesRequest r) =>
        string.Equals(r.Key, ServerRules.Password, StringComparison.OrdinalIgnoreCase) ? "<hidden>" : r.Value ?? "";

    private static void WriteAudit(bool accepted, in RemoteInstallActor actor, string row, string before, string after, string error)
    {
        var sink = Audit;
        if (sink == null) return;
        try
        {
            sink(SettingsAuditLog.Line(AuditClock(), accepted, actor.CharacterName, actor.SteamId,
                ServerAdminList.Word(actor.AuthorizedBy), row, before, after, error));
        }
        catch { /* the audit never decides a request */ }
    }

    /// <summary>Forget the rate windows (Core going down, tests).</summary>
    internal static void Reset()
    {
        _edits = new RemoteInstallRateLimiter(RateMaxEdits, RateWindowSeconds);
        _reads = new RemoteInstallRateLimiter(RateMaxReads, RateWindowSeconds);
        RateAudited.Clear();
        Asked.Clear();
        BackupsAsked.Clear();
    }

    /// <summary>The users who asked this session (they have the handler): a stored change is pushed to them.</summary>
    private static readonly HashSet<int> Asked = new();

    /// <summary>
    /// BACKUPSDLG1: the admins who were answered a Backups state this session: a change of the pending restore request is
    /// pushed to them (<see cref="ServerRulesWire.KindBackupsChanged"/>).
    /// </summary>
    private static readonly HashSet<int> BackupsAsked = new();

    /// <summary>BACKUPSDLG1: who a pending-request change is told to: every admin who read the Backups, except the one whose request caused it.</summary>
    internal static List<int> BackupsRecipients(IEnumerable<int> asked, int except)
    {
        var list = new List<int>();
        foreach (int index in asked)
            if (index != except) list.Add(index);
        list.Sort();
        return list;
    }

#if PROGENITOR_TESTS
    internal static void Install() { }

    internal static void Uninstall()
    {
        Reset();
        FactsProvider = null;
        Audit = null;
        ResetWriteSeams();
        ServerBackupsServer.Reset();
        AuditClock = () => DateTime.UtcNow;
        Now = () => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
    }

    /// <summary>Tests: the users a change is pushed to.</summary>
    internal static IReadOnlyCollection<int> AskedForTests => Asked;

    /// <summary>Tests: the admins a pending-request change is pushed to.</summary>
    internal static IReadOnlyCollection<int> BackupsAskedForTests => BackupsAsked;

    /// <summary>Tests: note an answer as the live request path does (a Backups state answered: the user is told of changes).</summary>
    internal static void NoteAnsweredForTests(int user, ServerRulesResponse response)
    {
        if (response.State != null) Asked.Add(user);
        if (response.Backups != null) BackupsAsked.Add(user);
    }

    internal static void NoteAskedForTests(int user) => Asked.Add(user);
#else
    private static bool _installed;
    private static IDisposable? _disconnected;

    /// <summary>BACKUPSDLG1: the user whose request is being decided (not told of the change it causes), or -1.</summary>
    private static int _answering = -1;

    /// <summary>Server: register the channel.</summary>
    internal static unsafe void Install()
    {
        if (_installed || !Plugin.IsServerProcess) return;
        _installed = true;
        FactsProvider = ServerRulesGame.ReadFacts;
        string root = "";
        try { root = BepInEx.Paths.BepInExRootPath ?? ""; } catch { /* the log line stays */ }
        Audit = line =>
        {
            Plugin.Logger?.LogInfo("ServerRules audit: " + line);
            SettingsAuditLog.Append(root, line);
        };
        InstallWriteSeams();
        try { ServerBackupsGame.Install(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("WorldRestore: the server seams could not be installed: " + e.Message); }
        try { CustomEvents.OnReceiveFromClient(CustomEventPolicy.ServerRulesEventId, OnRequest); }
        catch (Exception e) { Plugin.Logger?.LogWarning("ServerRules: the request channel could not be registered: " + e.Message); }
        try
        {
            _disconnected = Events.EventBus.On<Events.PlayerDisconnected>(ev =>
            {
                Asked.Remove(ev.User.Index);
                BackupsAsked.Remove(ev.User.Index);
            }, priority: 897);
        }
        catch { /* a push to a gone user is dropped by the transport */ }
        ServerBackupsServer.PendingChanged -= OnBackupsPendingChanged;
        ServerBackupsServer.PendingChanged += OnBackupsPendingChanged;
    }

    internal static unsafe void Uninstall()
    {
        if (_installed)
        {
            try { CustomEvents.RemoveClientHandler(CustomEventPolicy.ServerRulesEventId); } catch { /* teardown */ }
            _disconnected?.Dispose();
            _disconnected = null;
            ServerBackupsServer.PendingChanged -= OnBackupsPendingChanged;
        }

        _installed = false;
        Reset();
        FactsProvider = null;
        Audit = null;
        ResetWriteSeams();
        try { ServerBackupsGame.Uninstall(); } catch { /* teardown */ }
        ServerBackupsServer.Reset();
    }

    private static unsafe void OnRequest(int _, Entity userEntity, User user, byte* payload, int length)
    {
        if (payload == null || length <= 0) return;
        var span = new ReadOnlySpan<byte>(payload, length);
        if (!ServerRulesWire.TryDecodeRequest(span, out var request)) return;
        try
        {
            // Re-read the User the event resolved; no privilege or identity travels in the request.
            var world = Worlds.Server;
            if (world == null || !world.IsCreated || !world.EntityManager.HasComponent<User>(userEntity)) return;
            var current = world.EntityManager.GetComponentData<User>(userEntity);
            if (!current.IsConnected) return;
            var actor = new RemoteInstallActor(current.CharacterName.ToString(), current.PlatformId, current.IsAdmin,
                ServerAdminList.AuthorizeShared(current.PlatformId, current.IsAdmin));
            ServerRulesResponse response;
            _answering = current.Index;
            try { response = Apply(request, Handshake.UserAnswerStatus(current.Index) == "ok", actor); }
            finally { _answering = -1; }
            if (response.State != null) Asked.Add(current.Index);
            if (response.Backups != null) BackupsAsked.Add(current.Index);
            var bootstrap = world.GetExistingSystemManaged<ServerBootstrapSystem>();
            if (bootstrap != null)
                CustomEvents.SendToUser(bootstrap, current.Index, CustomEventPolicy.ServerRulesEventId, new ReadOnlySpan<byte>(ServerRulesWire.EncodeResponse(response)));
            // The answer's op is the request's, trimmed and lower-cased (a "Write" from a client is a write).
            if (response.Ok && response.Op == ServerRulesRequest.OpWrite && response.State != null) Push(world, bootstrap, response.State, current.Index);
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("ServerRules request: " + e.Message); }
    }

    /// <summary>
    /// BACKUPSDLG1: the pending restore request changed (an admin's request or cancel, the dev bridge, a countdown that ended):
    /// every other admin who read the Backups this session is told, and a Backups dialog open there asks again.
    /// </summary>
    private static unsafe void OnBackupsPendingChanged(BackupsPendingChange change)
    {
        var world = Worlds.Server;
        if (world == null || !world.IsCreated) return;
        var bootstrap = world.GetExistingSystemManaged<ServerBootstrapSystem>();
        if (bootstrap == null) return;
        byte[] bytes;
        try { bytes = ServerRulesWire.EncodeBackupsChanged(change.PendingId, change.Countdown, change.Why); }
        catch { return; }
        int told = 0;
        foreach (int index in BackupsRecipients(BackupsAsked, _answering))
        {
            try
            {
                CustomEvents.SendToUser(bootstrap, index, CustomEventPolicy.ServerRulesEventId, new ReadOnlySpan<byte>(bytes));
                told++;
            }
            catch { BackupsAsked.Remove(index); }
        }

        Plugin.Logger?.LogInfo("WorldRestore: the restore request changed (" + change.Why + "; pending " + (change.PendingId.Length > 0 ? change.PendingId : "none")
            + (change.Countdown ? ", countdown running" : "") + "): told " + told + " admin(s).");
    }

    /// <summary>A stored change: every other user who asked this session gets the new state (an open page refreshes).</summary>
    private static unsafe void Push(World world, ServerBootstrapSystem? bootstrap, ServerRulesState state, int except)
    {
        if (bootstrap == null) return;
        byte[] bytes;
        try { bytes = ServerRulesWire.EncodeChanged(state); }
        catch { return; }
        foreach (int index in new List<int>(Asked))
        {
            if (index == except) continue;
            try { CustomEvents.SendToUser(bootstrap, index, CustomEventPolicy.ServerRulesEventId, new ReadOnlySpan<byte>(bytes)); }
            catch { Asked.Remove(index); }
        }
    }
#endif
}
