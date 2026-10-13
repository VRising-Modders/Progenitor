using System;
using Progenitor.Networking;
#if !PROGENITOR_TESTS
using ProjectM;
using ProjectM.Network;
using Unity.Entities;
#endif

namespace Progenitor.Install;

/// <summary>Why a <see cref="StopServerReply"/> says what it says. One byte on the wire.</summary>
internal enum StopReplyReason : byte
{
    /// <summary>No reason carried.</summary>
    None = 0,

    /// <summary>The sender is not an admin on this server. Refused before the save path is touched.</summary>
    NotAdmin = 1,

    /// <summary>Accepted: the world save was triggered and the process will quit once it lands.</summary>
    Saving = 2,

    /// <summary>The save was refused or threw. The server is still running.</summary>
    SaveFailed = 3,

    /// <summary>The save was accepted but no save file appeared within the confirmation window. Nothing quit.</summary>
    SaveTimeout = 4,

    /// <summary>This process has no world-save path at all, so it will not quit.</summary>
    NoSavePath = 5,

    /// <summary>The save landed but nothing could schedule the quit.</summary>
    NoQuitPath = 6,

    /// <summary>
    /// The request's schema is not one this server reads. Refused without touching the save path.
    /// A client that predates this value reads it as a failed stop, which is what it is.
    /// </summary>
    UnsupportedRequest = 7,

    /// <summary>Review (L1): this sender asked too often (more than 5 requests in 60 s). Older clients read it as a failed stop.</summary>
    RateLimited = 8,
}

/// <summary>
/// Client → server: "save the world and stop". Sent on
/// <see cref="CustomEventPolicy.StopServerEventId"/>. <see cref="Nonce"/> is this client's
/// correlation value and is echoed by every reply; a reply for a different nonce belongs to an
/// earlier attempt and is dropped.
/// </summary>
internal struct StopServerRequest
{
    public const int CurrentSchema = 1;

    public int Schema;
    public int Nonce;

    public static StopServerRequest For(int nonce) => new() { Schema = CurrentSchema, Nonce = nonce };
}

/// <summary>
/// Server → client, on the same id. Deliberately a different size from
/// <see cref="StopServerRequest"/>, and each side registers only its own direction, so the
/// size-checked <c>CustomEvents.TryRead&lt;T&gt;</c> can never read one as the other.
/// </summary>
internal struct StopServerReply
{
    public const int CurrentSchema = 1;

    public int Schema;
    public int Nonce;
    public byte Accepted;
    public byte ReasonCode;

    public static StopServerReply For(int nonce, bool accepted, StopReplyReason reason) => new()
    {
        Schema = CurrentSchema,
        Nonce = nonce,
        Accepted = (byte)(accepted ? 1 : 0),
        ReasonCode = (byte)reason,
    };

    public bool IsAccepted => Accepted != 0;
    public StopReplyReason Reason => (StopReplyReason)ReasonCode;
    public bool IsCurrentSchema => Schema == CurrentSchema;
}

/// <summary>
/// Job SERVERINST4. The transport under Progenitor's graceful <b>Stop server</b>, and the
/// production wiring of <see cref="ServerProcessControl"/>'s and
/// <see cref="ServerStopCommand"/>'s seams.
///
/// <para><b>Why not chat.</b> The stop used to be submitted as the text <c>.stop-server</c> through
/// whatever console route the client had, which meant the pane could not tell "the server refused
/// me" from "nothing was listening": there was no answer to correlate, so silence was the only
/// failure mode and every one of them fell to the force prompt — a kill, no save. This is
/// Progenitor's own reliable event channel instead: one reserved id, a nonce, and a reply that says
/// <em>which</em> check failed. <c>.stop-server</c> stays as a second entry to the same
/// <see cref="ServerStopCommand.Execute"/> path, for an admin who would rather type it.</para>
///
/// <para><b>Admin.</b> The server re-reads <c>User</c> off the sender entity that
/// <c>CustomEvents</c> resolved from the connection and authorizes it with
/// <see cref="ServerAdminList.AuthorizeShared"/>: on the admin list the game loads, <c>User.IsAdmin</c>
/// (adminauth), or the host of this private game — the rule the remote-install channel applies, so
/// an admin who installed a server mod can stop the server to load it. (F21, live 2026-10-07: this
/// used to test <c>User.IsAdmin</c> or the host only, so an admin-list admin who never ran
/// adminauth was refused, silently.) Nothing about identity or privilege travels inside the
/// request; a non-admin is answered <see cref="StopReplyReason.NotAdmin"/> before the save path is
/// touched, and every decision is logged on the server.</para>
/// </summary>
internal static class StopServerEvents
{
    public const int EventId = CustomEventPolicy.StopServerEventId;

    private static bool _installed;
    private static int _nextNonce;

    /// <summary>The newest reply this client received, or null. One attempt is in flight at a time.</summary>
    private static StopServerReply? _lastReply;

    /// <summary>A fresh correlation value. Never 0, so "no attempt" and "attempt 0" cannot be confused.</summary>
    public static int NextNonce()
    {
        int next = System.Threading.Interlocked.Increment(ref _nextNonce);
        return next == 0 ? System.Threading.Interlocked.Increment(ref _nextNonce) : next;
    }

    /// <summary>Client: forget any reply left from an earlier attempt before sending a new one.</summary>
    public static void ClearReply() => _lastReply = null;

    /// <summary>Client: record a decoded reply. Internal so the transport tests can drive it.</summary>
    public static void AcceptReply(StopServerReply reply)
    {
        if (!reply.IsCurrentSchema) return;
        _lastReply = reply;
    }

    /// <summary>Client: the reply for <paramref name="nonce"/>, or null. A reply for another nonce is ignored.</summary>
    public static StopServerReply? ReplyFor(int nonce)
    {
        var reply = _lastReply;
        if (reply is { } r && r.Nonce == nonce) return r;
        return null;
    }

    /// <summary>
    /// Server: decide and act on one decoded request, with the authorization as a bare bool (the
    /// shape the earlier tests drive): true is <see cref="RemoteAuthorizedBy.AdminAuth"/>.
    /// </summary>
    public static void Handle(int nonce, bool isAdmin, Action<StopServerReply> send) =>
        Handle(nonce, isAdmin ? RemoteAuthorizedBy.AdminAuth : RemoteAuthorizedBy.None, "", send, null);

    /// <summary>
    /// Server: decide and act on one decoded request. Split out from the packet handler so the
    /// admin refusal, the ordering (reply, then save, then quit) and every refusal reason are
    /// testable without a game. <paramref name="send"/> is how the answer reaches that one client;
    /// <paramref name="log"/> receives one line per decision and per step, so no request ends
    /// without a server log line (F21, live 2026-10-07).
    ///
    /// <para><b>Who may stop.</b> <paramref name="authorizedBy"/> is
    /// <see cref="ServerAdminList.AuthorizeShared"/>'s answer for the sender: on the admin list the
    /// game loads, <c>User.IsAdmin</c> (adminauth), or the host of this private game. That is the
    /// rule the remote-install channel applies (<c>RemoteInstallActor.Authorized</c>), so an admin who
    /// may install a server mod may also stop the server to load it.</para>
    ///
    /// <para><b>One stop at a time.</b> A request that arrives while an accepted stop is still
    /// saving is answered accepted/<see cref="StopReplyReason.Saving"/> and starts nothing: the
    /// first request's save and quit stand.</para>
    /// </summary>
    public static void Handle(int nonce, RemoteAuthorizedBy authorizedBy, string? who, Action<StopServerReply> send, Action<string>? log,
        ulong senderId = 0)
    {
        if (send == null) throw new ArgumentNullException(nameof(send));
        string sender = "request " + nonce + " from " + (string.IsNullOrEmpty(who) ? "a client" : "'" + who + "'")
            + " (authorized by " + ServerAdminList.Word(authorizedBy) + ")";
        if (authorizedBy == RemoteAuthorizedBy.None)
        {
            // Before anything else: a non-admin never reaches the save path.
            Log(log, "StopServer: refused " + sender + ": not-admin (not on the admin list, no adminauth, not the host).");
            send(StopServerReply.For(nonce, accepted: false, StopReplyReason.NotAdmin));
            return;
        }

        // Review (L1): at most 5 requests per sender in 60 s, like the remote-install channel; every refusal logs.
        if (!Admit(senderId))
        {
            Log(log, "StopServer: refused " + sender + ": rate-limited (more than " + MaxPerWindow + " requests in " + WindowSeconds + " s).");
            send(StopServerReply.For(nonce, accepted: false, StopReplyReason.RateLimited));
            return;
        }

        // Review (L2): an accepted stop whose process never exited does not hold every later request forever.
        if (_stopping && (Clock() - _stoppingSince).TotalSeconds > StoppingStaleSeconds)
        {
            Log(log, "StopServer: the stop accepted " + ((int)(Clock() - _stoppingSince).TotalSeconds) + " s ago never quit the process; a new stop may start.");
            _stopping = false;
        }

        if (_stopping)
        {
            Log(log, "StopServer: " + sender + ": a stop is already saving; answered saving, nothing new started.");
            send(StopServerReply.For(nonce, accepted: true, StopReplyReason.Saving));
            return;
        }

        // The accepted reply goes out first so the client's readback reaches "replied"/"saving"
        // while the save is still in flight; the stop is otherwise indistinguishable from silence
        // for as long as the serializer takes.
        Log(log, "StopServer: accepted " + sender + "; saving the world, then quitting.");
        _stopping = true;
        _stoppingSince = Clock();
        send(StopServerReply.For(nonce, accepted: true, StopReplyReason.Saving));
        bool started = ServerStopCommand.Execute(
            isAdmin: true,
            reply: line => Log(log, "StopServer: " + line),
            failed: reason =>
            {
                _stopping = false;
                Log(log, "StopServer: refused " + sender + " after accepting it: " + ReasonWord(reason) + "; the server keeps running.");
                send(StopServerReply.For(nonce, accepted: false, reason));
            });
        if (!started) _stopping = false;
    }

    /// <summary>The server is saving for an accepted stop. Cleared by a refusal that does not quit.</summary>
    private static bool _stopping;
    private static DateTime _stoppingSince;

    /// <summary>Review (L2): an accepted stop older than this that did not quit no longer blocks a new one.</summary>
    internal const double StoppingStaleSeconds = 120;

    internal const int MaxPerWindow = 5;
    internal const int WindowSeconds = 60;
    private static readonly System.Collections.Generic.Dictionary<ulong, System.Collections.Generic.Queue<DateTime>> Requests = new();

    /// <summary>Tests: the clock. Null: UTC now.</summary>
    internal static Func<DateTime>? ClockOverride { get; set; }

    private static DateTime Clock() => ClockOverride?.Invoke() ?? DateTime.UtcNow;

    private static bool Admit(ulong senderId)
    {
        lock (Requests)
        {
            var now = Clock();
            if (!Requests.TryGetValue(senderId, out var q)) Requests[senderId] = q = new System.Collections.Generic.Queue<DateTime>();
            while (q.Count > 0 && (now - q.Peek()).TotalSeconds >= WindowSeconds) q.Dequeue();
            if (q.Count >= MaxPerWindow) return false;
            q.Enqueue(now);
            return true;
        }
    }

    internal static bool StoppingForTests => _stopping;

    /// <summary>The wire word of a reason, as the logs and the readback write it.</summary>
    public static string ReasonWord(StopReplyReason reason) => reason switch
    {
        StopReplyReason.None => "none",
        StopReplyReason.NotAdmin => "not-admin",
        StopReplyReason.Saving => "saving",
        StopReplyReason.SaveFailed => "save-failed",
        StopReplyReason.SaveTimeout => "save-timeout",
        StopReplyReason.NoSavePath => "no-save-path",
        StopReplyReason.NoQuitPath => "no-quit-path",
        StopReplyReason.UnsupportedRequest => "unsupported-request",
        StopReplyReason.RateLimited => "rate-limited",
        _ => "reason-" + ((byte)reason).ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    /// <summary>
    /// What a client says after "&lt;server&gt; refused: " for one reason (the Install tab's
    /// footer). An unknown value from a newer server reads as a stop that did not happen.
    /// </summary>
    public static string RefusalText(StopReplyReason reason) => reason switch
    {
        StopReplyReason.NotAdmin => "only an admin can stop it (on its admin list, adminauth, or the host of a private game).",
        StopReplyReason.SaveFailed => "the world save failed, so it is still running.",
        StopReplyReason.SaveTimeout => "it triggered a save but no save file appeared within 15 seconds, so it is still running.",
        StopReplyReason.NoSavePath => "it has no world-save path, so it will not quit.",
        StopReplyReason.NoQuitPath => "it saved but could not schedule its quit, so it is still running.",
        StopReplyReason.UnsupportedRequest => "it does not read this client's stop request (a different Progenitor version).",
        StopReplyReason.RateLimited => "too many stop requests; wait a minute and try again.",
        _ => "it did not stop (" + ReasonWord(reason) + ").",
    };

    private static void Log(Action<string>? log, string line)
    {
        try { log?.Invoke(line); }
        catch { /* a log line never decides a stop */ }
    }

    internal static void ResetForTests()
    {
        _installed = false;
        _lastReply = null;
        _stopping = false;
        lock (Requests) Requests.Clear();
        ClockOverride = null;
    }

#if PROGENITOR_TESTS
    internal static bool Installed => _installed;

    internal static void Install() => _installed = true;

    internal static void Uninstall()
    {
        _installed = false;
        _lastReply = null;
        _stopping = false;
    }
#else
    /// <summary>
    /// Registered from <c>Plugin.Install</c>, next to <c>Networking.ServerSettings.Install()</c>,
    /// and released from <c>Plugin.Uninstall</c> beside it: the handler is context-owned and must
    /// not survive a Core generation.
    /// </summary>
    internal static unsafe void Install()
    {
        if (_installed) return;
        if (Plugin.IsServerProcess)
        {
            CustomEvents.OnReceiveFromClient(EventId, OnRequest);
            ServerStopCommand.SaveWorld = () => Progenitor.Persistence.WorldSaveTrigger.SaveCurrentWorld();
            ServerStopCommand.SaveWatch = () =>
            {
                // Sampled before TriggerSave: "newer than it was" only means something if "was"
                // predates the save being asked for.
                string? folder = Progenitor.Persistence.WorldSaveTrigger.SaveFolder();
                DateTime before = Progenitor.Persistence.WorldSaveTrigger.NewestSaveWriteUtc(folder);
                return () => Progenitor.Persistence.WorldSaveTrigger.SaveLanded(folder, before);
            };
            ServerStopCommand.Schedule = (seconds, action) => Progenitor.Dev.MainThread.PostDelayed(seconds, action);
            // The same call the bridge's quit op makes. Scheduled rather than called inline so the
            // reply packet leaves this frame before Unity tears the process down.
            ServerStopCommand.QuitNextFrame = () =>
                Progenitor.Dev.MainThread.PostDelayed(0, () => UnityEngine.Application.Quit());
        }
        else
        {
            CustomEvents.OnReceive(EventId, OnReply);
            ServerProcessControl.SubmitCommand = SendRequest;
            ServerProcessControl.PollReply = ReplyFor;
            // The identity half of GracefulAvailable: only a server whose Progenitor answered this
            // client's handshake is offered the graceful route.
            ServerProcessControl.ProgenitorHandshakeOk = () =>
                string.Equals(Handshake.LastClientResult, "ok", StringComparison.Ordinal);
            // SERVERINST5: the production value of the port half of GracefulAvailable. Bootstrap
            // records the address of every GameHelper.LaunchGame join because it is the only side
            // that outlives a Core generation; 0 when this Bootstrap predates the record (then
            // ConnectPortRecorded is false and the gate says so) or when nothing was joined yet.
            ServerProcessControl.ConnectPortRecorded = () => BootstrapLink.RecordsConnectPort;
            ServerProcessControl.ConnectedServerPort = () => BootstrapLink.LastConnectPort;
            ServerProcessControl.ConnectedServerDescription = () =>
                ServerProcessControl.FormatConnectedTo(
                    BootstrapLink.LastConnectProtocol,
                    BootstrapLink.LastConnectAddress,
                    BootstrapLink.LastConnectPort);
        }

        _installed = true;
    }

    internal static unsafe void Uninstall()
    {
        CustomEvents.RemoveClientHandler(EventId, OnRequest);
        CustomEvents.RemoveHandler(EventId, OnReply);
        ServerProcessControl.SubmitCommand = null;
        ServerProcessControl.PollReply = null;
        ServerProcessControl.ProgenitorHandshakeOk = null;
        // SERVERINST5: context-owned like the rest. Left set, a released generation's closure would
        // keep answering the gate through a BootstrapLink whose Plugin.Logger is gone.
        ServerProcessControl.ConnectedServerPort = null;
        ServerProcessControl.ConnectPortRecorded = null;
        ServerProcessControl.ConnectedServerDescription = null;
        ServerStopCommand.ResetForTests();
        _lastReply = null;
        _stopping = false;
        _installed = false;
    }

    private static bool SendRequest(int nonce)
    {
        ClearReply();
        return CustomEvents.SendToServer(EventId, StopServerRequest.For(nonce));
    }

    private static unsafe void OnReply(int _, byte* payload, int length)
    {
        if (payload == null || !CustomEvents.TryRead<StopServerReply>(payload, length, out var reply))
        {
            Plugin.Logger?.LogWarning("StopServer: dropped a reply of " + length + " byte(s) that is not a stop reply.");
            return;
        }

        Plugin.Logger?.LogInfo("StopServer: reply to request " + reply.Nonce + ": "
            + (reply.IsAccepted ? "accepted" : "refused") + " (" + ReasonWord(reply.Reason) + ")"
            + (reply.IsCurrentSchema ? "." : "; schema " + reply.Schema + " is not one this client reads, ignored."));
        AcceptReply(reply);
    }

    private static unsafe void OnRequest(int _, Entity userEntity, User user, byte* payload, int length)
    {
        // F21 (live 2026-10-07): every way out of this handler writes a line, and every one that
        // still has a connected sender answers it. A refusal used to leave nothing in the server
        // log, and the Install tab never read the answer, so the stop looked like nothing at all.
        var log = Plugin.Logger;
        if (payload == null || !CustomEvents.TryRead<StopServerRequest>(payload, length, out var request))
        {
            log?.LogWarning("StopServer: dropped a request of " + length + " byte(s) that is not a stop request; nothing to answer.");
            return;
        }

        try
        {
            var world = Worlds.Server;
            if (world == null || !world.IsCreated || !world.EntityManager.HasComponent<User>(userEntity))
            {
                log?.LogWarning("StopServer: dropped request " + request.Nonce + ": the sender has no user entity in the server world; nothing to answer.");
                return;
            }

            // Re-read rather than trust the resolved copy: the same rule ServerSettings follows.
            var current = world.EntityManager.GetComponentData<User>(userEntity);
            if (!current.IsConnected)
            {
                log?.LogWarning("StopServer: dropped request " + request.Nonce + ": the sender is no longer connected; nothing to answer.");
                return;
            }

            var bootstrap = world.GetExistingSystemManaged<ServerBootstrapSystem>();
            int index = current.Index;
            string who = current.CharacterName.ToString();
            void Send(StopServerReply reply)
            {
                bool sent = false;
                try { sent = bootstrap != null && CustomEvents.SendToUser(bootstrap, index, EventId, in reply); }
                catch (Exception e) { log?.LogWarning("StopServer: answering request " + reply.Nonce + " threw: " + e.Message); }
                if (!sent)
                    log?.LogWarning("StopServer: the answer to request " + reply.Nonce + " (" + ReasonWord(reply.Reason) + ") could not be sent.");
            }

            if (request.Schema != StopServerRequest.CurrentSchema)
            {
                log?.LogWarning("StopServer: refused request " + request.Nonce + " from '" + who + "': schema " + request.Schema
                    + " (this server reads " + StopServerRequest.CurrentSchema + ").");
                Send(StopServerReply.For(request.Nonce, accepted: false, StopReplyReason.UnsupportedRequest));
                return;
            }

            // F21: the remote-install rule (admin list, adminauth, or the private game's host), read
            // through the shared admin-list reader RemoteInstallEvents installs.
            var by = ServerAdminList.AuthorizeShared(current.PlatformId, current.IsAdmin);
            Handle(request.Nonce, by, who, Send, line =>
            {
                if (line.Contains(": refused ")) log?.LogWarning(line);
                else log?.LogInfo(line);
            }, current.PlatformId);
        }
        catch (Exception e)
        {
            log?.LogWarning("StopServer: request " + request.Nonce + " threw: " + e.Message);
        }
    }
#endif
}
