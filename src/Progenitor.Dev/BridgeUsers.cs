using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Progenitor.Dev;

/// <summary>
/// The optional <c>who</c> object a caller sends next to <c>token</c>/<c>op</c>/<c>args</c>:
/// which AI and session is driving the bridge. Every field is optional; a request without
/// <c>who</c> (older scripts, the live rig) is recorded as <c>unknown</c>.
/// </summary>
internal sealed class BridgeWho
{
    internal const int MaxFieldLength = 120;
    internal const string UnknownAi = "unknown";

    public string Ai { get; init; } = UnknownAi;
    public string Name { get; init; } = "";
    public string Id { get; init; } = "";
    public string Client { get; init; } = "";
    public string Tool { get; init; } = "";
    public int? Pid { get; init; }
    public string Model { get; init; } = "";

    /// <summary>
    /// Session ID when known, else ai + name + pid for a long-lived MCP process. One-shot tools
    /// such as call.mjs start a new process per request, so their pid is left out of the key.
    /// Distinct keys are distinct users.
    /// </summary>
    public string Key => Id.Length > 0
        ? "id:" + Id
        : "who:" + Ai + "|" + Name + "|" + Tool + "|"
            + (Tool == "mcp" ? Pid?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "" : "");

    /// <summary>Reads <c>root.who</c>. Tolerates a missing, null or malformed object and wrong field types.</summary>
    internal static BridgeWho FromRequest(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("who", out var w)
            || w.ValueKind != JsonValueKind.Object)
            return new BridgeWho();
        string ai = Str(w, "ai");
        return new BridgeWho
        {
            Ai = ai.Length > 0 ? ai : UnknownAi,
            Name = Str(w, "name"),
            Id = Str(w, "id"),
            Client = Str(w, "client"),
            Tool = Str(w, "tool"),
            Pid = w.TryGetProperty("pid", out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out int pid) ? pid : null,
            Model = Str(w, "model"),
        };
    }

    private static string Str(JsonElement o, string name)
    {
        if (!o.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.String) return "";
        return Clip(v.GetString());
    }

    internal static string Clip(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var t = s.Trim();
        var sb = new StringBuilder(Math.Min(t.Length, MaxFieldLength));
        foreach (char c in t)
        {
            if (sb.Length >= MaxFieldLength) break;
            sb.Append(char.IsControl(c) ? ' ' : c);
        }
        return sb.ToString();
    }
}

/// <summary>One bridge user as recorded and persisted.</summary>
internal sealed class BridgeUserRecord
{
    public string Key { get; set; } = "";
    public string Ai { get; set; } = BridgeWho.UnknownAi;
    public string Name { get; set; } = "";
    public string Id { get; set; } = "";
    public string Client { get; set; } = "";
    public string Tool { get; set; } = "";
    public int? Pid { get; set; }
    public string Model { get; set; } = "";
    public DateTime FirstSeen { get; set; }
    public DateTime LastSeen { get; set; }
    public long Requests { get; set; }
    public string LastOp { get; set; } = "";

    internal BridgeUserRecord Clone() => (BridgeUserRecord)MemberwiseClone();

    internal static BridgeUserRecord From(BridgeWho who, DateTime now) => new()
    {
        Key = who.Key,
        Ai = who.Ai,
        Name = who.Name,
        Id = who.Id,
        Client = who.Client,
        Tool = who.Tool,
        Pid = who.Pid,
        Model = who.Model,
        FirstSeen = now,
        LastSeen = now,
    };

    /// <summary>Refresh the descriptive fields (a session title can change mid-session).</summary>
    internal void Update(BridgeWho who)
    {
        Ai = who.Ai;
        if (who.Name.Length > 0) Name = who.Name;
        if (who.Client.Length > 0) Client = who.Client;
        if (who.Tool.Length > 0) Tool = who.Tool;
        if (who.Pid != null) Pid = who.Pid;
        if (who.Model.Length > 0) Model = who.Model;
    }

    internal JsonObject ToJson(DateTime? now = null)
    {
        var o = new JsonObject
        {
            ["ai"] = Ai,
            ["name"] = Name,
            ["id"] = Id,
            ["client"] = Client,
            ["tool"] = Tool,
            ["pid"] = Pid,
            ["model"] = Model,
            ["firstSeen"] = Iso(FirstSeen),
            ["lastSeen"] = Iso(LastSeen),
            ["requests"] = Requests,
            ["lastOp"] = LastOp,
        };
        if (now != null)
            o["ageSeconds"] = (long)Math.Max(0, (now.Value - LastSeen).TotalSeconds);
        return o;
    }

    internal static BridgeUserRecord? FromJson(JsonNode? node)
    {
        if (node is not JsonObject o) return null;
        string S(string k) => o[k] is JsonValue v && v.TryGetValue<string>(out var s) ? BridgeWho.Clip(s) : "";
        var r = new BridgeUserRecord
        {
            Ai = S("ai") is { Length: > 0 } ai ? ai : BridgeWho.UnknownAi,
            Name = S("name"),
            Id = S("id"),
            Client = S("client"),
            Tool = S("tool"),
            Pid = o["pid"] is JsonValue pv && pv.TryGetValue<int>(out int pid) ? pid : null,
            Model = S("model"),
            FirstSeen = ParseTime(S("firstSeen")),
            LastSeen = ParseTime(S("lastSeen")),
            Requests = o["requests"] is JsonValue rv && rv.TryGetValue<long>(out long n) ? Math.Max(0, n) : 0,
            LastOp = S("lastOp"),
        };
        r.Key = new BridgeWho { Ai = r.Ai, Name = r.Name, Id = r.Id, Pid = r.Pid }.Key;
        return r;
    }

    internal static string Iso(DateTime t) =>
        DateTime.SpecifyKind(t, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture);

    private static DateTime ParseTime(string s) =>
        DateTime.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var t)
            ? DateTime.SpecifyKind(t, DateTimeKind.Utc)
            : DateTime.MinValue;
}

/// <summary>
/// Who has been driving this bridge: the current user plus the last <see cref="RecentCapacity"/>
/// distinct previous users. Game-free and thread-safe (requests arrive on several socket
/// threads). Persisted to a small JSON file beside the token so a Dev reload or a game restart
/// keeps the history. Saves happen on the calling socket thread, immediately on a user change
/// and otherwise at most every <see cref="SaveIntervalSeconds"/>; never on the game main thread
/// except the final <see cref="Flush"/> at unload.
/// </summary>
internal sealed class BridgeUserTracker
{
    internal const int RecentCapacity = 3;
    internal const int SaveIntervalSeconds = 5;
    internal const string FileName = "progenitor-dev.users.json";
    /// <summary>A previous user seen within this window is reported on every response as <c>bridgeUser</c>.</summary>
    internal static readonly TimeSpan ShareWindow = TimeSpan.FromMinutes(10);
    /// <summary>One "now used by" log line per user per window, so two agents alternating calls do not flood the log.</summary>
    internal static readonly TimeSpan ChangeLogWindow = TimeSpan.FromMinutes(5);

    private readonly string? _path;
    private readonly Func<DateTime> _utcNow;
    private readonly Action<string>? _log;
    private readonly object _lock = new();
    private readonly object _writeLock = new();
    private readonly Dictionary<string, DateTime> _lastChangeLog = new();
    private BridgeUserRecord? _current;
    private readonly List<BridgeUserRecord> _recent = new();
    private bool _dirty;
    private long _version;
    private long _writtenVersion;
    private DateTime _lastSave = DateTime.MinValue;
    private bool _saveFailing;

    internal BridgeUserTracker(string? path, Func<DateTime>? utcNow = null, Action<string>? log = null)
    {
        _path = string.IsNullOrEmpty(path) ? null : path;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _log = log;
    }

    internal static string PathBesideToken(string tokenPath)
    {
        var dir = Path.GetDirectoryName(tokenPath);
        return string.IsNullOrEmpty(dir) ? FileName : Path.Combine(dir, FileName);
    }

    /// <summary>Loads the persisted history. A missing or corrupt file starts empty.</summary>
    internal void Load()
    {
        if (_path == null) return;
        try
        {
            if (!File.Exists(_path)) return;
            var root = JsonNode.Parse(File.ReadAllText(_path, Encoding.UTF8)) as JsonObject;
            if (root == null) throw new InvalidDataException("not a JSON object");
            var current = BridgeUserRecord.FromJson(root["current"]);
            var recent = new List<BridgeUserRecord>();
            if (root["recent"] is JsonArray arr)
            {
                foreach (var n in arr)
                {
                    var r = BridgeUserRecord.FromJson(n);
                    if (r == null || (current != null && r.Key == current.Key) || recent.Exists(x => x.Key == r.Key)) continue;
                    recent.Add(r);
                    if (recent.Count == RecentCapacity) break;
                }
            }
            lock (_lock)
            {
                _current = current;
                _recent.Clear();
                _recent.AddRange(recent);
            }
        }
        catch (Exception e)
        {
            _log?.Invoke($"bridge: ignoring unreadable {FileName}: {e.Message}");
        }
    }

    /// <summary>
    /// Records one authenticated request. Returns the most recent other user when it was seen
    /// within <see cref="ShareWindow"/> (a copy), else null.
    /// </summary>
    internal BridgeUserRecord? Record(BridgeWho who, string op)
    {
        var now = _utcNow();
        string? changeLine = null;
        string? toWrite = null;
        long version = 0;
        BridgeUserRecord? previous;
        lock (_lock)
        {
            bool changed = _current == null || _current.Key != who.Key;
            if (changed)
            {
                var old = _current;
                int idx = _recent.FindIndex(r => r.Key == who.Key);
                BridgeUserRecord next;
                if (idx >= 0) { next = _recent[idx]; _recent.RemoveAt(idx); }
                else next = BridgeUserRecord.From(who, now);
                if (old != null)
                {
                    _recent.RemoveAll(r => r.Key == old.Key);
                    _recent.Insert(0, old);
                    while (_recent.Count > RecentCapacity) _recent.RemoveAt(_recent.Count - 1);
                }
                _current = next;
                if (!_lastChangeLog.TryGetValue(who.Key, out var logged) || now - logged >= ChangeLogWindow)
                {
                    PruneChangeLogLocked(now);
                    _lastChangeLog[who.Key] = now;
                    changeLine = ChangeLine(who, old, now);
                }
            }

            _current!.Update(who);
            _current.LastSeen = now;
            _current.Requests++;
            _current.LastOp = BridgeWho.Clip(op);
            _dirty = true;
            _version++;

            if (_path != null && (changed || now - _lastSave >= TimeSpan.FromSeconds(SaveIntervalSeconds)))
            {
                toWrite = SerializeLocked();
                version = _version;
                _lastSave = now;
                _dirty = false;
            }

            previous = _recent.Count > 0 && now - _recent[0].LastSeen <= ShareWindow ? _recent[0].Clone() : null;
        }

        if (changeLine != null) _log?.Invoke(changeLine);
        if (toWrite != null) Write(toWrite, version);
        return previous;
    }

    /// <summary>Writes any unsaved counts. Called from Bridge.Stop.</summary>
    internal void Flush()
    {
        string? toWrite = null;
        long version = 0;
        lock (_lock)
        {
            if (_path == null || !_dirty) return;
            toWrite = SerializeLocked();
            version = _version;
            _dirty = false;
            _lastSave = _utcNow();
        }
        Write(toWrite, version);
    }

    /// <summary><c>{current, recent}</c> with <c>ageSeconds</c> on each entry; the shape of <c>bridge_users</c>.</summary>
    internal JsonObject Snapshot()
    {
        var now = _utcNow();
        lock (_lock)
        {
            var recent = new JsonArray();
            foreach (var r in _recent) recent.Add(r.ToJson(now));
            return new JsonObject
            {
                ["current"] = _current?.ToJson(now),
                ["recent"] = recent,
            };
        }
    }

    internal static string Describe(string ai, string name, string id)
    {
        var sb = new StringBuilder(ai.Length > 0 ? ai : BridgeWho.UnknownAi);
        if (name.Length > 0) sb.Append(" \"").Append(name).Append('"');
        if (id.Length > 0) sb.Append(" (").Append(id).Append(')');
        return sb.ToString();
    }

    internal static string ChangeLine(BridgeWho now, BridgeUserRecord? previous, DateTime at)
    {
        var line = "bridge: now used by " + Describe(now.Ai, now.Name, now.Id);
        if (now.Id.Length == 0 && now.Tool.Length > 0)
            line += $" via {now.Tool}" + (now.Pid != null ? $" pid {now.Pid}" : "");
        if (previous != null)
        {
            long minutes = (long)Math.Max(0, (at - previous.LastSeen).TotalMinutes);
            line += "; previously " + Describe(previous.Ai, previous.Name, "") + $" {minutes} min ago";
        }
        return line;
    }

    private string SerializeLocked()
    {
        var recent = new JsonArray();
        foreach (var r in _recent) recent.Add(r.ToJson());
        var root = new JsonObject
        {
            ["version"] = 1,
            ["current"] = _current?.ToJson(),
            ["recent"] = recent,
        };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private void PruneChangeLogLocked(DateTime now)
    {
        if (_lastChangeLog.Count < 32) return;
        foreach (var key in _lastChangeLog.Where(kv => now - kv.Value >= ChangeLogWindow).Select(kv => kv.Key).ToList())
            _lastChangeLog.Remove(key);
    }

    private void Write(string json, long version)
    {
        if (_path == null) return;
        lock (_writeLock)
        {
            // Two socket threads can race here; never let an older snapshot overwrite a newer one.
            if (version <= _writtenVersion) return;
            try
            {
                // Not a secret, but never write through a link planted at the history path.
                if (IsLink(_path)) return;
                // Unique per write: an old generation's write can overlap a new one's after a hot reload.
                var temp = _path + "." + Environment.ProcessId + "." + Guid.NewGuid().ToString("N") + ".tmp";
                if (IsLink(temp)) return;
                File.WriteAllText(temp, json, new UTF8Encoding(false));
                try
                {
                    File.Move(temp, _path, overwrite: true);
                }
                catch
                {
                    try { File.Delete(temp); } catch { }
                    throw;
                }
                _writtenVersion = version;
                _saveFailing = false;
            }
            catch (Exception e)
            {
                // Log once until a save succeeds again: a file held open would otherwise warn on every write.
                if (!_saveFailing) _log?.Invoke($"bridge: could not save {FileName}: {e.Message}");
                _saveFailing = true;
            }
        }
    }

    private static bool IsLink(string path)
    {
        try
        {
            return File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch
        {
            return true;
        }
    }
}
