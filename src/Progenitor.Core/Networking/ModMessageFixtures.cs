using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using Progenitor.Persistence;

namespace Progenitor.Networking;

// Mod compatibility C5 (docs/mod-compatibility.md, "Compat test helper"): the message fixture format, shared by the Dev
// bridge's messages_record op (live capture) and Progenitor.Testing (replay in a mod's own test project). Game-free:
// compiled into Core, Progenitor.Testing and the test suite.

/// <summary>
/// One recorded message: the payload a mod's <c>ctx.Messages</c> wrote (the bytes after the <c>'PrMs'</c> header) and
/// what the envelope said about it.
/// </summary>
internal sealed class ModMessageRecord
{
    internal string Mod { get; init; } = "";

    /// <summary>The version of the mod that <em>wrote</em> the payload (the sender's).</summary>
    internal string ModVersion { get; init; } = "";

    internal string Message { get; init; } = "";
    internal int Version { get; init; } = 1;
    internal int MinReaderVersion { get; init; } = 1;

    /// <summary><see cref="ModMessageFixtureFormat.ToServer"/>, <see cref="ModMessageFixtureFormat.ToClient"/> or <see cref="ModMessageFixtureFormat.AnyDirection"/>.</summary>
    internal string Direction { get; init; } = ModMessageFixtureFormat.AnyDirection;

    /// <summary><c>live</c> (the bridge) or <c>test</c> (written by a test).</summary>
    internal string Source { get; init; } = "live";

    internal byte[] Payload { get; init; } = Array.Empty<byte>();

    /// <summary>
    /// The writer's field schema (<see cref="MessageSchema.Shape"/>), when known: a typed send records it; a received
    /// message or a hand-written one has none.
    /// </summary>
    internal IReadOnlyList<FieldShape>? Schema { get; init; }
}

/// <summary>
/// The fixture layout. One fixture is two files under a mod's fixture directory:
/// <c>&lt;mod version&gt;/&lt;message&gt;@v&lt;n&gt;/&lt;sha12&gt;.bin</c> (the payload) and the <c>.json</c> sidecar beside it
/// (<c>format</c>, <c>mod</c>, <c>modVersion</c>, <c>message</c>, <c>version</c>, <c>minReader</c>, <c>direction</c>,
/// <c>source</c>, <c>recorded</c>, <c>sha256</c>, and <c>schema</c> when the writer's field schema is known: an array of
/// <c>{n, name, type, list, fields?}</c>). The file name is the first 12 hex digits of the payload's SHA-256, so
/// the same payload recorded twice is one fixture. The sidecar is authoritative; the folder names only sort the files.
/// </summary>
internal static class ModMessageFixtureFormat
{
    internal const int FormatVersion = 1;

    /// <summary>A client sent it to the server.</summary>
    internal const string ToServer = "to-server";

    /// <summary>The server sent it to a client.</summary>
    internal const string ToClient = "to-client";

    /// <summary>Written by a test, either way.</summary>
    internal const string AnyDirection = "any";

    internal const string PayloadExtension = ".bin";
    internal const string SidecarExtension = ".json";

    /// <summary>
    /// The largest payload a fixture may hold: the largest exchange body (a reply, snapshot or delta of
    /// <c>ctx.Requests</c> / <c>ctx.Subscriptions</c>); a <c>ctx.Messages</c> payload is at most one envelope's.
    /// </summary>
    internal const int MaxPayloadBytes = ExchangeLimits.MaxBodyBytes;

    /// <summary>The largest sidecar read back (a few hundred bytes, a few KiB with a schema).</summary>
    internal const int MaxSidecarBytes = 256 * 1024;

    internal static string Sha256Hex(byte[] payload)
    {
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(payload ?? Array.Empty<byte>());
        var sb = new StringBuilder(hash.Length * 2);
        foreach (byte b in hash) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    /// <summary>A name made safe for one path segment: <c>[A-Za-z0-9._+-]</c> kept, anything else <c>_</c>; never empty, <c>.</c> or <c>..</c>.</summary>
    internal static string Segment(string? text)
    {
        string t = (text ?? "").Trim();
        var sb = new StringBuilder(t.Length);
        foreach (char c in t)
            sb.Append((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '.' || c == '_' || c == '+' || c == '-' ? c : '_');
        string s = sb.ToString();
        if (s.Length == 0 || s.Trim('.').Length == 0) s = "_" + s;
        if (s.Length > 64) s = s.Substring(0, 64);
        // Windows drops a trailing dot from a folder name; keep the name as written instead.
        if (s.EndsWith(".", StringComparison.Ordinal)) s = s.Substring(0, s.Length - 1) + "_";
        return s;
    }

    /// <summary>The folder of one message version under a mod's fixture directory, relative: <c>1.2.0/hello@v2</c>.</summary>
    internal static string RelativeFolder(string modVersion, string message, int version) =>
        Path.Combine(Segment(modVersion), Segment(message) + "@v" + version.ToString(CultureInfo.InvariantCulture));

    /// <summary>The sidecar's JSON.</summary>
    internal static string SidecarJson(ModMessageRecord r, string sha256, DateTime recordedUtc)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteNumber("format", FormatVersion);
            w.WriteString("mod", r.Mod);
            w.WriteString("modVersion", r.ModVersion);
            w.WriteString("message", r.Message);
            w.WriteNumber("version", r.Version);
            w.WriteNumber("minReader", r.MinReaderVersion);
            w.WriteString("direction", r.Direction);
            w.WriteString("source", r.Source);
            w.WriteString("recorded", recordedUtc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));
            w.WriteString("sha256", sha256);
            if (r.Schema != null)
            {
                w.WritePropertyName("schema");
                WriteSchema(w, r.Schema);
            }

            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(ms.ToArray()).Replace("\r\n", "\n") + "\n";
    }

    private static void WriteSchema(Utf8JsonWriter w, IReadOnlyList<FieldShape> fields)
    {
        w.WriteStartArray();
        foreach (var f in fields)
        {
            w.WriteStartObject();
            w.WriteNumber("n", f.Number);
            w.WriteString("name", f.Name);
            w.WriteString("type", f.Type);
            if (f.List) w.WriteBoolean("list", true);
            if (f.Fields != null)
            {
                w.WritePropertyName("fields");
                WriteSchema(w, f.Fields);
            }

            w.WriteEndObject();
        }

        w.WriteEndArray();
    }

    private static List<FieldShape>? ReadSchema(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Array) return null;
        var list = new List<FieldShape>();
        foreach (var o in e.EnumerateArray())
        {
            if (o.ValueKind != JsonValueKind.Object) throw new FormatException("a schema entry is not an object");
            int n = Int(o, "n", 0);
            if (n < 1 || n > WireFormat.MaxFieldNumber) throw new FormatException("a schema entry has field number " + n);
            var f = new FieldShape(n, Str(o, "name"), Str(o, "type"), o.TryGetProperty("list", out var l) && l.ValueKind == JsonValueKind.True);
            if (o.TryGetProperty("fields", out var nested)) f.Fields = ReadSchema(nested) ?? new List<FieldShape>();
            list.Add(f);
        }

        return list;
    }

    /// <summary>
    /// Write <paramref name="r"/> under <paramref name="modDirectory"/>. Returns the payload file's path; <paramref name="created"/>
    /// is false when the same payload was already there (nothing is rewritten). Throws <see cref="ArgumentException"/> for a
    /// record that is not a valid fixture.
    /// </summary>
    internal static string Write(string modDirectory, ModMessageRecord r, DateTime recordedUtc, out bool created)
    {
        Validate(r);
        string folder = Path.Combine(modDirectory, RelativeFolder(r.ModVersion, r.Message, r.Version));
        string sha = Sha256Hex(r.Payload);
        string stem = Path.Combine(folder, sha.Substring(0, 12));
        string bin = stem + PayloadExtension;
        if (File.Exists(bin) && File.Exists(stem + SidecarExtension))
        {
            created = false;
            return bin;
        }

        Directory.CreateDirectory(folder);
        File.WriteAllBytes(bin, r.Payload);
        File.WriteAllText(stem + SidecarExtension, SidecarJson(r, sha, recordedUtc), new UTF8Encoding(false));
        created = true;
        return bin;
    }

    internal static void Validate(ModMessageRecord r)
    {
        if (r == null) throw new ArgumentNullException(nameof(r));
        if (string.IsNullOrWhiteSpace(r.Mod)) throw new ArgumentException("A fixture needs the mod's GUID.");
        if (string.IsNullOrWhiteSpace(r.ModVersion)) throw new ArgumentException("A fixture needs the sending mod's version.");
        if (!ModMessageEnvelope.IsValidName(r.Message)) throw new ArgumentException("'" + r.Message + "' is not a valid message name.");
        if (!ModMessageEnvelope.IsValidVersion(r.Version) || r.MinReaderVersion < 1 || r.MinReaderVersion > r.Version)
            throw new ArgumentException("Version " + r.Version + " / min reader " + r.MinReaderVersion + " is not valid (1 <= min reader <= version <= 65535).");
        if (r.Payload == null || r.Payload.Length > MaxPayloadBytes)
            throw new ArgumentException("A fixture payload is at most " + MaxPayloadBytes + " bytes.");
    }

    /// <summary>
    /// Read one fixture from its sidecar path. False with <paramref name="error"/> when the sidecar or its payload is missing,
    /// malformed, of another format, or the payload does not match the recorded SHA-256.
    /// </summary>
    internal static bool TryRead(string sidecarPath, out ModMessageRecord? record, out string? error)
    {
        record = null;
        error = null;
        try
        {
            var info = new FileInfo(sidecarPath);
            if (!info.Exists) { error = "no sidecar"; return false; }
            if (info.Length > MaxSidecarBytes) { error = "the sidecar is over " + MaxSidecarBytes + " bytes"; return false; }
            string bin = Path.ChangeExtension(sidecarPath, PayloadExtension);
            var binInfo = new FileInfo(bin);
            if (!binInfo.Exists) { error = "no payload file beside the sidecar"; return false; }
            if (binInfo.Length > MaxPayloadBytes) { error = "the payload is over " + MaxPayloadBytes + " bytes"; return false; }

            using var doc = JsonFiles.ParseFile(sidecarPath, new JsonDocumentOptions { MaxDepth = 2 * WireFormat.MaxDepth + 8 });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) { error = "the sidecar is not a JSON object"; return false; }
            int format = Int(root, "format", 0);
            if (format != FormatVersion) { error = "sidecar format " + format + " is not " + FormatVersion; return false; }
            byte[] payload = File.ReadAllBytes(bin);
            string sha = Str(root, "sha256");
            if (sha.Length > 0 && !string.Equals(sha, Sha256Hex(payload), StringComparison.OrdinalIgnoreCase))
            {
                error = "the payload does not match the sidecar's sha256";
                return false;
            }

            var r = new ModMessageRecord
            {
                Mod = Str(root, "mod"),
                ModVersion = Str(root, "modVersion"),
                Message = Str(root, "message"),
                Version = Int(root, "version", 0),
                MinReaderVersion = Int(root, "minReader", 1),
                Direction = Str(root, "direction") is { Length: > 0 } d ? d : AnyDirection,
                Source = Str(root, "source"),
                Payload = payload,
                Schema = root.TryGetProperty("schema", out var schema) ? ReadSchema(schema) : null,
            };
            Validate(r);
            record = r;
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or ArgumentException or FormatException)
        {
            error = e.Message;
            return false;
        }
    }

    private static string Str(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static int Int(JsonElement o, string name, int fallback) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i) ? i : fallback;
}

/// <summary>
/// One bounded recording session (the Dev bridge's <c>messages_record</c>): every message one mod (or every mod) sends or
/// receives is written as a fixture under <c>&lt;root&gt;/&lt;mod&gt;/</c> until the time, message or byte budget runs out,
/// whichever comes first; then the session stops itself. A payload already recorded is counted as a duplicate and not
/// written again. Game-free and thread-safe: the bridge feeds it from <see cref="ModMessageRecordQueue"/>'s writer
/// thread, so hashing and file writes never run on the game's main thread.
/// </summary>
internal sealed class ModMessageRecorder
{
    internal const int DefaultSeconds = 120;
    internal const int MaxSeconds = 1800;
    internal const int DefaultMaxMessages = 200;
    internal const int MaxMessagesCap = 5000;
    internal const int DefaultMaxBytes = 1024 * 1024;
    internal const int MaxBytesCap = 16 * 1024 * 1024;

    /// <summary>The most the fixture folder may hold across sessions; a session past it is refused.</summary>
    internal const long DiskCapBytes = 64L * 1024 * 1024;

    private readonly object _gate = new();

    private readonly Func<double> _clock;
    private readonly double _started;
    private readonly Dictionary<string, int> _perMessage = new(StringComparer.Ordinal);
    private readonly List<string> _errors = new();

    internal ModMessageRecorder(string root, string? mod, int seconds, int maxMessages, int maxBytes, Func<double> clock)
    {
        Root = root ?? throw new ArgumentNullException(nameof(root));
        Mod = (mod ?? "").Trim();
        Seconds = Math.Max(1, Math.Min(MaxSeconds, seconds <= 0 ? DefaultSeconds : seconds));
        MaxMessages = Math.Max(1, Math.Min(MaxMessagesCap, maxMessages <= 0 ? DefaultMaxMessages : maxMessages));
        MaxBytes = Math.Max(1024, Math.Min(MaxBytesCap, maxBytes <= 0 ? DefaultMaxBytes : maxBytes));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _started = _clock();
    }

    internal string Root { get; }

    /// <summary>The mod GUID recorded, or empty for every mod.</summary>
    internal string Mod { get; }

    internal int Seconds { get; }
    internal int MaxMessages { get; }
    internal int MaxBytes { get; }

    internal int Written { get; private set; }
    internal int Duplicates { get; private set; }
    internal int Seen { get; private set; }
    internal long BytesWritten { get; private set; }

    /// <summary>Null while recording; else why it stopped (<c>time</c>, <c>messages</c>, <c>bytes</c>, <c>stopped</c>).</summary>
    internal string? StoppedBecause { get; private set; }

    internal bool Active
    {
        get { lock (_gate) return StoppedBecause == null && !Expired(); }
    }

    /// <summary>Bytes in the files under <paramref name="root"/> (0 when it does not exist).</summary>
    internal static long FolderBytes(string root)
    {
        long total = 0;
        try
        {
            if (!Directory.Exists(root)) return 0;
            foreach (var f in new DirectoryInfo(root).EnumerateFiles("*", SearchOption.AllDirectories)) total += f.Length;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* what was counted */ }

        return total;
    }

    /// <summary>
    /// The byte budget a new session may have: the request (or the default), at most what is left of
    /// <see cref="DiskCapBytes"/> after <paramref name="existing"/>; 0 when the folder is full.
    /// </summary>
    internal static int BudgetFor(int requested, long existing)
    {
        long left = DiskCapBytes - Math.Max(0, existing);
        if (left < 1024) return 0;
        int wanted = Math.Max(1024, Math.Min(MaxBytesCap, requested <= 0 ? DefaultMaxBytes : requested));
        return (int)Math.Min(wanted, left);
    }

    private bool Expired()
    {
        if (StoppedBecause != null) return true;
        if (_clock() - _started < Seconds) return false;
        StoppedBecause = "time";
        return true;
    }

    internal void Stop()
    {
        lock (_gate) StoppedBecause ??= "stopped";
    }

    /// <summary>
    /// Offer one message. Returns what happened: <c>written</c>, <c>duplicate</c>, <c>other-mod</c>, <c>stopped</c>,
    /// <c>error</c>. Never throws.
    /// </summary>
    internal string Offer(ModMessageRecord r, DateTime nowUtc)
    {
        lock (_gate) return OfferLocked(r, nowUtc);
    }

    private string OfferLocked(ModMessageRecord r, DateTime nowUtc)
    {
        try
        {
            if (r == null) return "error";
            if (Mod.Length > 0 && !string.Equals(Mod, r.Mod, StringComparison.OrdinalIgnoreCase)) return "other-mod";
            if (Expired()) return "stopped";
            Seen++;
            long cost = r.Payload.Length + 512L;
            if (BytesWritten + cost > MaxBytes)
            {
                StoppedBecause = "bytes";
                return "stopped";
            }

            string dir = Path.Combine(Root, ModMessageFixtureFormat.Segment(r.Mod));
            ModMessageFixtureFormat.Write(dir, r, nowUtc, out bool created);
            if (!created)
            {
                Duplicates++;
                return "duplicate";
            }

            Written++;
            BytesWritten += cost;
            string key = r.Mod + "|" + r.ModVersion + "|" + r.Message + "@v" + r.Version.ToString(CultureInfo.InvariantCulture);
            _perMessage[key] = _perMessage.TryGetValue(key, out int n) ? n + 1 : 1;
            if (Written >= MaxMessages) StoppedBecause = "messages";
            return "written";
        }
        catch (Exception e)
        {
            if (_errors.Count < 8) _errors.Add(e.Message);
            return "error";
        }
    }

    /// <summary>The session as the bridge reports it.</summary>
    internal Dictionary<string, object?> Status()
    {
        lock (_gate) return StatusLocked();
    }

    private Dictionary<string, object?> StatusLocked()
    {
        bool active = StoppedBecause == null && !Expired();
        var per = new List<Dictionary<string, object?>>();
        foreach (var kv in _perMessage)
        {
            string[] parts = kv.Key.Split('|');
            per.Add(new Dictionary<string, object?>
            {
                ["mod"] = parts[0],
                ["modVersion"] = parts.Length > 1 ? parts[1] : "",
                ["message"] = parts.Length > 2 ? parts[2] : "",
                ["written"] = kv.Value,
            });
        }

        return new Dictionary<string, object?>
        {
            ["recording"] = active,
            ["stoppedBecause"] = StoppedBecause,
            ["root"] = Root,
            ["mod"] = Mod.Length > 0 ? Mod : "*",
            ["seconds"] = Seconds,
            ["elapsed"] = Math.Round(Math.Max(0, _clock() - _started), 1),
            ["maxMessages"] = MaxMessages,
            ["maxBytes"] = MaxBytes,
            ["seen"] = Seen,
            ["written"] = Written,
            ["duplicates"] = Duplicates,
            ["bytes"] = BytesWritten,
            ["messages"] = per,
            ["errors"] = new List<string>(_errors),
        };
    }
}

/// <summary>
/// The hand-off between the game's main thread and the recorder: the send and receive paths only enqueue (the payload
/// array is never written again by its owner), and one background writer thread hashes and writes. The queue is
/// bounded; a message that finds it full is dropped and counted, never waited for. Game-free.
/// </summary>
internal sealed class ModMessageRecordQueue
{
    internal const int Capacity = 256;

    private readonly ModMessageRecorder _recorder;
    private readonly System.Collections.Concurrent.ConcurrentQueue<ModMessageRecord> _queue = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly Func<DateTime> _now;
    private readonly Thread? _thread;
    private int _pending;
    private int _dropped;
    private volatile bool _completed;

    /// <summary>Start the writer thread (<paramref name="startThread"/> false: tests drain with <see cref="DrainOnce"/>).</summary>
    internal ModMessageRecordQueue(ModMessageRecorder recorder, Func<DateTime>? now = null, bool startThread = true)
    {
        _recorder = recorder ?? throw new ArgumentNullException(nameof(recorder));
        _now = now ?? (() => DateTime.UtcNow);
        if (!startThread) return;
        _thread = new Thread(Run) { IsBackground = true, Name = "Progenitor messages_record writer" };
        _thread.Start();
    }

    internal ModMessageRecorder Recorder => _recorder;

    internal int Dropped => Volatile.Read(ref _dropped);

    internal int Pending => Volatile.Read(ref _pending);

    /// <summary>Main thread: hand one message to the writer. False when it was dropped (queue full, or completed).</summary>
    internal bool Enqueue(ModMessageRecord r)
    {
        if (r == null) return false;
        if (_completed)
        {
            Interlocked.Increment(ref _dropped);
            return false;
        }

        if (Interlocked.Increment(ref _pending) > Capacity)
        {
            Interlocked.Decrement(ref _pending);
            Interlocked.Increment(ref _dropped);
            return false;
        }

        _queue.Enqueue(r);
        _signal.Release();
        return true;
    }

    /// <summary>Write everything queued now, on the calling thread. Returns how many were handled.</summary>
    internal int DrainOnce()
    {
        int n = 0;
        while (_queue.TryDequeue(out var r))
        {
            try { _recorder.Offer(r, _now()); }
            finally { Interlocked.Decrement(ref _pending); }
            n++;
        }

        return n;
    }

    private void Run()
    {
        try
        {
            while (true)
            {
                _signal.Wait(250);
                DrainOnce();
                if (_completed && _queue.IsEmpty) return;
            }
        }
        catch
        {
            // A writer that dies stops recording; the main thread is never affected.
            _recorder.Stop();
        }
    }

    /// <summary>Wait until the queue is empty or <paramref name="timeoutMs"/> passed. True when empty.</summary>
    internal bool Flush(int timeoutMs)
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (Pending > 0)
        {
            if (_thread == null) DrainOnce();
            else if (DateTime.UtcNow > until) return false;
            else Thread.Sleep(5);
        }

        return true;
    }

    /// <summary>Stop taking messages; the writer finishes what is queued and exits (waited for up to <paramref name="waitMs"/>).</summary>
    internal void Complete(int waitMs)
    {
        _completed = true;
        try { _signal.Release(); }
        catch { /* released enough */ }
        if (_thread == null) DrainOnce();
        else _thread.Join(Math.Max(0, waitMs));
    }
}
