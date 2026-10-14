using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Progenitor.Dev;

/// <summary>
/// Transport and protocol. One JSON object per line in each direction.
/// Request:  {"id": 1, "token": "...", "op": "logs", "args": {...}, "who": {...}}
/// Response: {"id": 1, "ok": true, "result": {...}, "bridgeUser": {...}} or {"id": 1, "ok": false, "error": "..."}
/// <c>who</c> (optional) names the AI session making the call; <c>bridgeUser</c> (only when set)
/// is the previous distinct user seen within ten minutes. See <see cref="BridgeUserTracker"/>.
/// Game-touching operations are marshaled to the main thread; the socket thread only parses and writes.
/// </summary>
internal sealed class Bridge : IMainThreadDropTarget
{
    internal const int MaxConnections = 4;

    private readonly int _port;
    private readonly string _token;
    private readonly string _instance;
    private readonly object? _owner;
    private readonly object _listenLock = new();
    private readonly HashSet<TcpClient> _clients = new();
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Thread? _acceptThread;
    private readonly Handlers _handlers;
    private readonly MainThreadOpGate _gate = new();
    private int _liveConnections;
    private readonly BridgeUserTracker _users;

    private static JsonSerializerOptions JsonOptions => BridgeJson.Options;

    public Bridge(int port, string token, string instance, object? owner = null, string? usersPath = null)
    {
        _users = new BridgeUserTracker(usersPath, log: m => Plugin.Logger?.LogInfo(m));
        _port = port;
        _token = token;
        _instance = instance ?? "";
        _owner = owner ?? this;
        _handlers = new Handlers(_gate);
    }

    public void Start()
    {
        var cts = new CancellationTokenSource();
        _cts = cts;
        // First bind attempt is synchronous and does not sleep. Address-in-use (the previous
        // generation's AcceptLoop still unwinding) retries on this background thread so Load
        // / a Core-swap ReloadPlugins does not hitch the game main thread (R13 finding 5).
        var thread = new Thread(() => BindAndAccept(cts.Token))
        {
            IsBackground = true,
            Name = "progenitor-bridge",
        };
        _acceptThread = thread;
        thread.Start();
    }

    public void Stop()
    {
        // A Core swap unloads this Dev instance mid-lifetime. Abort the in-flight
        // slot so it cannot leak onto a later generation (the live failure was
        // "a main-thread op is already running" after the dropped Run never cleared
        // the flag, and the old listener kept answering after the new bind raced).
        AbortPendingOps();
        try { _handlers.StopWatchdog(); } catch { }
        try { _users.Flush(); } catch { }
        try { _cts?.Cancel(); } catch { }
        CloseAcceptedClients();

        TcpListener? listener;
        lock (_listenLock) listener = _listener;
        if (listener != null)
        {
            try
            {
                listener.Stop();
                lock (_listenLock)
                {
                    if (ReferenceEquals(_listener, listener))
                        _listener = null;
                }
            }
            catch (Exception e)
            {
                // Keep _listener so a later generation cannot bind the same endpoint while
                // this AcceptLoop is still in AcceptTcpClient (ExclusiveAddressUse stays on).
                Plugin.Logger?.LogWarning($"bridge listener Stop: {e.Message}");
            }
        }

        // Do not Join the accept thread on the Unload / game main thread (R14 finding 5).
        // Cancel + listener.Stop unblocks AcceptTcpClient; the thread is background.
        // Leave _cts undisposed so a still-running AcceptLoop can read IsCancellationRequested.
        _acceptThread = null;
    }

    /// <summary>
    /// Fail the current deferred waiter and free the one-op slot. Called from
    /// <see cref="Plugin.Unload"/> so a Core swap that unloads Dev while an op was
    /// running cannot leave the next Dev generation stuck.
    /// </summary>
    internal void AbortPendingOps(string? reason = null) =>
        _gate.Abort(reason ?? MainThreadOpGate.UnloadMessage);

    /// <summary>
    /// <see cref="MainThread.DropOwnedBy"/> of a Run/Post owned by this Bridge. Completing the
    /// gate here is what stops the dropped-then-already-running loop (evidence 50): the Run
    /// TCS faults, but the one-op flag used to stay set because the lambda never ran.
    /// </summary>
    public void OnMainThreadDropped(Exception exception) =>
        AbortPendingOps(exception?.Message);

    private void BindAndAccept(CancellationToken ct)
    {
        // Off the main thread: the history file is small, but Load must never hitch the game.
        _users.Load();
        TcpListener? listener = null;
        SocketException? last = null;
        int delay = BridgeListenPolicy.BindRetryDelayMs;
        int attempt = 0;
        while (BridgeListenPolicy.BindUntilCancelled && !ct.IsCancellationRequested)
        {
            listener = TryBindOnce(out last);
            if (listener != null) break;
            attempt++;
            if (attempt == BridgeListenPolicy.BindRetryAttempts || attempt % BridgeListenPolicy.BindRetryAttempts == 0)
            {
                Plugin.Logger?.LogWarning(
                    $"bridge still waiting to bind 127.0.0.1:{_port} after {attempt} attempts: {last?.Message}");
            }
            if (ct.WaitHandle.WaitOne(delay))
                return;
            delay = Math.Min(delay * 2, BridgeListenPolicy.BindRetryDelayMaxMs);
        }

        if (listener == null)
            return;

        lock (_listenLock)
        {
            if (ct.IsCancellationRequested)
            {
                try { listener.Stop(); } catch { }
                return;
            }
            _listener = listener;
        }

        Plugin.Logger?.LogWarning($"Progenitor Dev Bridge listening on 127.0.0.1:{_port}");
        AcceptLoop(listener, ct);
    }

    /// <summary>
    /// One bind attempt. ExclusiveAddressUse stays on and ReuseAddress stays off so two
    /// generations cannot listen on 127.0.0.1:27341/27342 at once (R13 finding 5).
    /// </summary>
    private TcpListener? TryBindOnce(out SocketException? error)
    {
        error = null;
        TcpListener? listener = null;
        try
        {
            listener = new TcpListener(IPAddress.Loopback, _port);
            listener.Server.ExclusiveAddressUse = BridgeListenPolicy.ExclusiveAddressUse;
            // ReuseAddress stays off (BridgeListenPolicy.ReuseAddress is false). Setting it
            // would allow two generations to listen on 127.0.0.1:27341/27342 at once.
            listener.Start();
            return listener;
        }
        catch (SocketException e)
        {
            error = e;
            try { listener?.Stop(); } catch { }
            return null;
        }
    }

    private void AcceptLoop(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = listener.AcceptTcpClient(); }
            catch { break; }

            if (ct.IsCancellationRequested)
            {
                try { client.Close(); } catch { }
                break;
            }

            int live = Interlocked.Increment(ref _liveConnections);
            if (live > MaxConnections)
            {
                Interlocked.Decrement(ref _liveConnections);
                try { client.Close(); } catch { }
                continue;
            }

            TrackClient(client);
            var t = new Thread(() => ServeGuarded(client, ct)) { IsBackground = true, Name = "progenitor-bridge-conn" };
            t.Start();
        }
    }

    private void TrackClient(TcpClient client)
    {
        lock (_clients) _clients.Add(client);
    }

    private void UntrackClient(TcpClient client)
    {
        lock (_clients) _clients.Remove(client);
    }

    private void CloseAcceptedClients()
    {
        TcpClient[] snapshot;
        lock (_clients)
        {
            snapshot = new TcpClient[_clients.Count];
            _clients.CopyTo(snapshot);
            _clients.Clear();
        }
        for (int i = 0; i < snapshot.Length; i++)
        {
            try { snapshot[i].Close(); } catch { }
        }
    }

    private void ServeGuarded(TcpClient client, CancellationToken ct)
    {
        try { Serve(client, ct); }
        finally
        {
            UntrackClient(client);
            Interlocked.Decrement(ref _liveConnections);
        }
    }

    private void Serve(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            try
            {
                var remote = (IPEndPoint?)client.Client.RemoteEndPoint;
                if (remote == null || !IPAddress.IsLoopback(remote.Address)) return;

                using var stream = client.GetStream();
                var reader = new BoundedLineReader(stream, BoundedLineReader.DefaultMaxBytes);
                using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
                while (!ct.IsCancellationRequested)
                {
                    string? line;
                    try
                    {
                        line = reader.ReadLine();
                    }
                    catch (BoundedLineReader.LineTooLongException)
                    {
                        Plugin.Logger?.LogWarning("bridge: request line exceeded 1 MB; closing connection");
                        return;
                    }
                    if (line == null) break;
                    if (line.Trim().Length == 0) continue;
                    var response = Handle(line);
                    writer.WriteLine(response);
                }
            }
            catch (BoundedLineReader.LineTooLongException)
            {
                Plugin.Logger?.LogWarning("bridge: request line exceeded 1 MB; closing connection");
            }
            catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException)
            {
                // client went away (including Stop() closing the accepted socket)
            }
            catch (Exception e)
            {
                Plugin.Logger?.LogWarning($"bridge connection error: {e.Message}");
            }
        }
    }

    private string Handle(string line)
    {
        long id = 0;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.Number) id = idEl.GetInt64();
            var token = root.TryGetProperty("token", out var tokEl) ? tokEl.GetString() : null;
            if (token != _token) return Error(id, "bad token");
            var op = root.TryGetProperty("op", out var opEl) ? opEl.GetString() ?? "" : "";
            var args = root.TryGetProperty("args", out var argsEl) ? argsEl.Clone() : default;
            var previousUser = _users.Record(BridgeWho.FromRequest(root), op);

            object? result;
            if (string.Equals(op, "bridge_users", StringComparison.OrdinalIgnoreCase))
            {
                // Socket thread; never takes the one-op gate.
                result = _users.Snapshot();
            }
            else if (_handlers.IsMainThreadOp(op))
            {
                result = InvokeOnMainThread(id, op, args, out var error);
                if (error != null) return error;
            }
            else
            {
                result = _handlers.Invoke(op, args);
            }
            if (IsInstanceOp(op))
                result = WithInstance(result);
            if (previousUser != null)
            {
                var bridgeUser = previousUser.ToJson(DateTime.UtcNow);
                return JsonSerializer.Serialize(new { id, ok = true, result, bridgeUser }, JsonOptions);
            }
            return JsonSerializer.Serialize(new { id, ok = true, result }, JsonOptions);
        }
        catch (AggregateException ae) when (ae.InnerException != null)
        {
            return Error(id, ae.InnerException.ToString());
        }
        catch (Exception e)
        {
            return Error(id, e.ToString());
        }
    }

    /// <summary>
    /// One in-flight main-thread op for the whole bridge, held on <see cref="MainThreadOpGate"/>
    /// (instance state, never a process-wide static). The socket thread waits up to 30 s then
    /// fails the waiter and marks the ticket abandoned: the original <see cref="MainThread.Run{T}"/>
    /// is not canceled by steal, and a retry cannot enter until that lambda completes, skips, or
    /// is dropped. A dropped Run (owner unloading) also frees the slot. Deferred ops
    /// (<see cref="DeferredResult"/>, used by <c>chat_observe</c>) return immediately after
    /// <see cref="MainThread.PostDelayed"/>; the socket thread waits on that TCS so the game
    /// frame is not blocked. A deferred timeout drops that delayed post so it cannot inject a
    /// second chat line after the waiter has already failed.
    /// </summary>
    private object? InvokeOnMainThread(long id, string op, JsonElement args, out string? error)
    {
        error = null;
        if (!_gate.TryEnter(op, id, out int ticket, out var reject))
        {
            error = Error(id, reject ?? MainThreadOpGate.AlreadyRunningMessage);
            return null;
        }

        try
        {
            Task<object?> task;
            try
            {
                // Owner is this Bridge (or the Plugin instance passed at construction) so
                // DropOwnedBy of Dev completes the Run TCS and IMainThreadDropTarget aborts
                // the gate instead of leaving the socket waiter and the slot hanging.
                // That owner is this generation's token: a reloaded Plugin is a new instance
                // and is not retired because the previous one unloaded (evidence 53).
                // Ticket is stamped so Drain skips invoke after a 30 s abandon.
                task = MainThreadOpInvoke.Run(
                    _gate,
                    ticket,
                    () => _handlers.Invoke(op, args),
                    _owner);
            }
            catch
            {
                _gate.Abort(ticket, MainThreadOpGate.UnloadMessage);
                throw;
            }

            int waitMs = _gate.RemainingWaitMs();
            if (waitMs <= 0 || !task.Wait(waitMs))
            {
                _gate.Abandon(ticket, MainThreadOpGate.TimeoutMessage);
                error = Error(id, MainThreadOpGate.TimeoutMessage);
                return null;
            }

            object? result;
            try
            {
                result = task.GetAwaiter().GetResult();
            }
            catch (Exception e)
            {
                _gate.Abort(ticket, e.Message);
                error = Error(id, e.ToString());
                return null;
            }

            if (result is DeferredResult deferred)
            {
                _gate.AttachDeferred(deferred);
                int deferredWait = Math.Min(
                    Math.Clamp(deferred.WaitMs + 5_000, 1, MainThreadOpGate.DefaultTimeoutMs),
                    _gate.RemainingWaitMs());
                string? deferredError = null;
                if (deferredWait <= 0 || !WaitDeferred(deferred, deferredWait, out deferredError))
                {
                    // Abandon holds the slot. DropOwnedBy of this deferred runs on the
                    // main thread so it cannot race Drain; Finish is idempotent (R14 finding 4).
                    DeferredTimeout.OnTimeout(_gate, ticket, deferred, _owner);
                    error = Error(id, deferredError ?? MainThreadOpGate.TimeoutMessage);
                    return null;
                }
                return deferred.Completion.Task.GetAwaiter().GetResult();
            }

            return result;
        }
        finally
        {
            if (!_gate.IsAbandoned(ticket))
                _gate.Exit(ticket);
        }
    }

    private static bool WaitDeferred(DeferredResult deferred, int waitMs, out string? error)
    {
        error = null;
        try
        {
            if (!deferred.Completion.Task.Wait(waitMs))
            {
                deferred.SetException(new TimeoutException(MainThreadOpGate.TimeoutMessage));
                error = MainThreadOpGate.TimeoutMessage;
                return false;
            }
            return true;
        }
        catch (AggregateException ae)
        {
            error = (ae.InnerException ?? ae).ToString();
            return false;
        }
        catch (Exception e)
        {
            error = e.ToString();
            return false;
        }
    }

    private static string Error(long id, string message) =>
        JsonSerializer.Serialize(new { id, ok = false, error = message }, JsonOptions);

    private static bool IsInstanceOp(string op) =>
        string.Equals(op, "ping", StringComparison.OrdinalIgnoreCase)
        || string.Equals(op, "status", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Attach the process tag and the bridge users to ping/status without changing those ops' handler types.
    /// Extra clients set <c>PROGENITOR_INSTANCE</c>; the primary Steam client leaves it empty.
    /// </summary>
    private object WithInstance(object? result)
    {
        try
        {
            var node = result == null
                ? new JsonObject()
                : JsonSerializer.SerializeToNode(result, JsonOptions) ?? new JsonObject();
            if (node is JsonObject obj)
            {
                obj["instance"] = _instance;
                obj["bridgeUsers"] = _users.Snapshot();
            }
            return node;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning($"bridge: could not attach instance tag: {e.Message}");
            return result ?? new { instance = _instance };
        }
    }
}
