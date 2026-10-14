using System;
using System.Collections.Generic;
using System.Globalization;
using Progenitor.Networking;

#if !PROGENITOR_TESTS
using Progenitor.Commands;
using Progenitor.Plugins;
#endif

namespace Progenitor.Sample;

/// <summary>
/// <c>.request [kib]</c> and <c>.subscribe [start|stop|post &lt;text&gt;|status] [key]</c>: requests larger than one
/// packet and subscriptions (<c>ctx.Requests</c>, <c>ctx.Subscriptions</c>; docs/modules.md, "Requests, large messages
/// and subscriptions"). Chat commands run on the server, so each command tells the caller's client what to do with a
/// versioned message (<see cref="ExchangeStart"/>); the client sends the request or subscribes, logs the result as
/// <c>Sample exchange: …</c> and reports it back (<see cref="ExchangeReport"/>), and the server whispers that line to the
/// player.
///
/// <para><c>.request 64</c>: the client asks for 64 KiB; the server's handler answers with that many deterministic bytes
/// and their checksum (five packets); the client checks the checksum. <c>.subscribe start lobby</c>: the client subscribes
/// to the <c>board</c> topic's key <c>lobby</c> and gets a snapshot (padded to two packets), then <c>.subscribe post
/// hello</c> from any player appends a line and publishes it to every subscriber of that key. Source-checked, built and
/// game-free tested; not verified live.</para>
/// </summary>
internal static class SampleExchange
{
    internal const string Area = "exchange";
    internal const string Topic = "board";
    internal const string DefaultKey = "lobby";
    internal const int DefaultKiB = 64;

    internal const string RequestUsage = "Usage: .request [kib]  (1-1000, default 64)";
    internal const string SubscribeUsage = "Usage: .subscribe [start|stop|post <text>|status] [key]  (key default lobby)";

    /// <summary>Parse <c>.request</c>'s size. Empty is the default; anything outside 1 to 1000 is refused.</summary>
    internal static bool TryParseKiB(string? text, out int kib)
    {
        kib = DefaultKiB;
        string t = (text ?? "").Trim();
        if (t.Length == 0) return true;
        return int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out kib) && kib >= 1 && kib <= SampleExchangeData.MaxKiB;
    }

    /// <summary>A board key: 1 to 32 characters of <c>[a-z0-9-]</c>; empty is <see cref="DefaultKey"/>.</summary>
    internal static bool TryParseKey(string? text, out string key)
    {
        key = (text ?? "").Trim().ToLowerInvariant();
        if (key.Length == 0) key = DefaultKey;
        if (key.Length > 32) return false;
        foreach (char c in key)
            if (!((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-')) return false;
        return true;
    }

    /// <summary>The client's line for a finished request.</summary>
    internal static string ResultLine(int kib, RequestStatus status, string reason, BlobReply? reply, double milliseconds)
    {
        string ms = milliseconds.ToString("0", CultureInfo.InvariantCulture);
        if (status != RequestStatus.Ok || reply == null)
            return "request " + kib + " KiB: " + status + (string.IsNullOrEmpty(reason) ? "" : " (" + reason + ")") + " after " + ms + " ms.";
        bool ok = reply.Data.Length == kib * 1024 && SampleExchangeData.Checksum(reply.Data) == reply.Checksum
                  && reply.Checksum == SampleExchangeData.Checksum(SampleExchangeData.Blob(kib));
        int packets = (reply.Data.Length + 64 + ChunkData - 1) / ChunkData;
        return "request " + kib + " KiB: Ok in " + ms + " ms, " + reply.Data.Length + " bytes in about " + packets + " packet(s), checksum "
               + (ok ? "ok" : "MISMATCH") + ".";
    }

    internal static string SnapshotLine(string key, BoardSnapshot s) =>
        "board " + key + ": snapshot rev " + s.Revision + ", " + s.Lines.Count + " line(s), " + (s.Padding.Length / 1024) + " KiB padding.";

    internal static string DeltaLine(string key, BoardLine d) => "board " + key + ": rev " + d.Revision + " '" + d.Line + "'.";

    internal static string EndedLine(string key, SubscriptionEndReason reason, string text) =>
        "board " + key + ": subscription ended, " + reason + (string.IsNullOrEmpty(text) ? "" : " (" + text + ")") + ".";

    /// <summary>The packet arithmetic of the result line: one packet carries 15 KiB minus a 14-byte frame header.</summary>
    private const int ChunkData = 15 * 1024 - 14;

#if !PROGENITOR_TESTS
    private static readonly SampleExchangeData.BoardBook Boards = new();
    private static readonly Dictionary<string, Subscription<BoardSnapshot, BoardLine>> Subscriptions = new(StringComparer.Ordinal);
    private static SubscriptionTopic<BoardSnapshot, BoardLine>? _topic;

    internal static void Attach(PluginContext ctx)
    {
        if (ctx == null) throw new ArgumentNullException(nameof(ctx));
        Boards.Clear();
        Subscriptions.Clear();
        // Unload: Core ends the topic and every subscription; this only drops the Sample's own references.
        ctx.Track(new ActionDisposableShim(() =>
        {
            Boards.Clear();
            Subscriptions.Clear();
            _topic = null;
        }));
        if (ctx.IsServer) AttachServer(ctx);
        else AttachClient(ctx);
    }

    private static void AttachServer(PluginContext ctx)
    {
        ctx.Requests.Handle<BlobRequest, BlobReply>((user, request, info) =>
        {
            var reply = SampleExchangeData.Answer(request);
            SampleLog.Info(Area, "answered a " + request.KiB + " KiB request from user " + info.UserIndex + " (request v" + info.SenderVersion + ").");
            return reply;
        }, new RequestOptions { MaxRequestBytes = 64, Burst = 3, PerSecond = 0.5 });

        _topic = ctx.Subscriptions.Topic<BoardSnapshot, BoardLine>(Topic, (user, key) =>
        {
            if (!TryParseKey(key, out var k) || k != key) throw new RequestRefusedException("a board key is 1 to 32 characters of a-z, 0-9 and -");
            // A key is untrusted input: an unknown board is an empty snapshot, not a new board (only a post creates one).
            return Boards.Snapshot(key);
        }, new TopicOptions { MaxKeyBytes = 32, MaxPerUser = 4 });
        _topic.Opened += (user, key) => SampleLog.Info(Area, "board " + key + ": a player subscribed (" + (_topic?.Subscribers(key).Count ?? 0) + " now).");
        _topic.Closed += (user, key) => SampleLog.Info(Area, "board " + key + ": a player unsubscribed (" + (_topic?.Subscribers(key).Count ?? 0) + " now).");

        ctx.Messages.On<ExchangeReport>((from, report, info) =>
        {
            string line = report.Line.Length > 300 ? report.Line.Substring(0, 300) : report.Line;
            ctx.Chat.SendTo(from, "Sample exchange (your client): " + line);
        });
        ctx.Commands.Register(typeof(SampleExchangeCommands));
    }



    private static void AttachClient(PluginContext ctx)
    {
        ctx.Messages.On<ExchangeStart>((from, start, info) =>
        {
            try { Run(ctx, start); }
            catch (Exception e) { Plugin.Logger?.LogError("Sample exchange threw: " + e); }
        });
    }

    private static void Report(PluginContext ctx, string message)
    {
        SampleLog.Info(Area, message);
        ctx.Messages.SendToServer(new ExchangeReport { Line = message });
    }

    private static void Run(PluginContext ctx, ExchangeStart start)
    {
        switch (start.What)
        {
            case "request":
            {
                int kib = start.KiB;
                var clock = System.Diagnostics.Stopwatch.StartNew();
                ctx.Requests.Send<BlobRequest, BlobReply>(new BlobRequest { KiB = kib }, r =>
                    Report(ctx, ResultLine(kib, r.Status, r.Reason, r.Reply, clock.Elapsed.TotalMilliseconds)));
                return;
            }
            case "subscribe":
            {
                string key = start.Key;
                if (Subscriptions.TryGetValue(key, out var held) && held.State != SubscriptionState.Ended)
                {
                    Report(ctx, "board " + key + ": already subscribed (" + held.State + ").");
                    return;
                }

                bool first = true;
                Subscriptions[key] = ctx.Subscriptions.Subscribe<BoardSnapshot, BoardLine>(Topic, key,
                    (snapshot, info) =>
                    {
                        if (first) Report(ctx, SnapshotLine(key, snapshot));
                        else SampleLog.Info(Area, SnapshotLine(key, snapshot));
                        first = false;
                    },
                    (delta, info) => SampleLog.Info(Area, DeltaLine(key, delta)),
                    (reason, text) => Report(ctx, EndedLine(key, reason, text)));
                return;
            }
            case "unsubscribe":
            {
                string key = start.Key;
                if (!Subscriptions.TryGetValue(key, out var held))
                {
                    Report(ctx, "board " + key + ": not subscribed.");
                    return;
                }

                long deltas = held.Deltas;
                held.Dispose();
                Subscriptions.Remove(key);
                Report(ctx, "board " + key + ": unsubscribed after " + deltas + " delta(s).");
                return;
            }
        }
    }

    internal static string Post(string key, string text)
    {
        var board = Boards.GetOrCreate(key);
        if (board == null) return "board " + key + ": not created; this server keeps at most " + SampleExchangeData.MaxBoards + " boards.";
        var delta = board.Post(text);
        int n = _topic?.Publish(key, delta) ?? 0;
        return SampleLog.Info(Area, "board " + key + ": rev " + delta.Revision + " posted to " + n + " subscriber(s).");
    }

    internal static string Status(string key)
    {
        var board = Boards.Find(key);
        int subs = _topic?.Subscribers(key).Count ?? 0;
        return "board " + key + ": rev " + (board?.Revision ?? 0) + ", " + (board?.Lines.Count ?? 0) + " line(s), " + subs + " subscriber(s); keys with subscribers: "
               + (_topic == null || _topic.Keys.Count == 0 ? "none" : string.Join(", ", _topic.Keys)) + ".";
    }

    private sealed class ActionDisposableShim : IDisposable
    {
        private Action? _action;
        internal ActionDisposableShim(Action action) => _action = action;

        public void Dispose()
        {
            var a = _action;
            _action = null;
            a?.Invoke();
        }
    }
#endif
}

#if !PROGENITOR_TESTS
internal static class SampleExchangeCommands
{
    // Not adminOnly: each asks the caller's own client, and the server's handler and topic carry their own limits.
    [Command("request", usage: SampleExchange.RequestUsage,
        description: "ctx.Requests: your client asks the server for that many KiB (default 64) and checks the reply; the result comes back as a whisper.")]
    public static void Request(ICommandContext ctx, string kib = "")
    {
        var plugin = Plugin.Active;
        if (plugin == null)
        {
            ctx.Reply("Sample plugin context is not available.");
            return;
        }

        if (!SampleExchange.TryParseKiB(kib, out int size))
        {
            ctx.Reply(SampleExchange.RequestUsage);
            return;
        }

        var sent = plugin.Messages.SendTo(ctx.UserEntity, new ExchangeStart { What = "request", KiB = size });
        ctx.Reply(sent == SendResult.Sent ? "Asked your client to request " + size + " KiB." : "Could not ask your client (" + sent + ").");
    }

    [Command("subscribe", usage: SampleExchange.SubscribeUsage,
        description: "ctx.Subscriptions: start or stop your client's subscription to a board, post a line to every subscriber, or show the board.")]
    public static void Subscribe(ICommandContext ctx, string what = "status", [Remainder] string rest = "")
    {
        var plugin = Plugin.Active;
        if (plugin == null)
        {
            ctx.Reply("Sample plugin context is not available.");
            return;
        }

        string verb = (what ?? "").Trim().ToLowerInvariant();
        string arg = (rest ?? "").Trim();
        switch (verb)
        {
            case "start":
            case "stop":
            {
                if (!SampleExchange.TryParseKey(arg, out var key))
                {
                    ctx.Reply(SampleExchange.SubscribeUsage);
                    return;
                }

                var sent = plugin.Messages.SendTo(ctx.UserEntity, new ExchangeStart { What = verb == "start" ? "subscribe" : "unsubscribe", Key = key });
                ctx.Reply(sent == SendResult.Sent ? "Asked your client to " + (verb == "start" ? "subscribe to" : "unsubscribe from") + " board " + key + "."
                    : "Could not ask your client (" + sent + ").");
                return;
            }
            case "post":
            {
                // `.subscribe post <text>` posts to lobby; `.subscribe post @key <text>` to another board.
                string key = SampleExchange.DefaultKey;
                if (arg.StartsWith("@", StringComparison.Ordinal))
                {
                    int space = arg.IndexOf(' ');
                    string k = space < 0 ? arg.Substring(1) : arg.Substring(1, space - 1);
                    if (!SampleExchange.TryParseKey(k, out key))
                    {
                        ctx.Reply(SampleExchange.SubscribeUsage);
                        return;
                    }

                    arg = space < 0 ? "" : arg.Substring(space + 1).Trim();
                }

                if (arg.Length == 0)
                {
                    ctx.Reply("Usage: .subscribe post [@key] <text>");
                    return;
                }

                ctx.Reply(SampleExchange.Post(key, arg));
                return;
            }
            case "":
            case "status":
            {
                if (!SampleExchange.TryParseKey(arg, out var key))
                {
                    ctx.Reply(SampleExchange.SubscribeUsage);
                    return;
                }

                ctx.Reply(SampleExchange.Status(key));
                return;
            }
            default:
                ctx.Reply(SampleExchange.SubscribeUsage);
                return;
        }
    }
}
#endif
