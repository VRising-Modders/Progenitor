using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Progenitor.Config;

/// <summary>
/// Main-thread, single-owner store of plain serialized setting strings. Root is the
/// progenitor-players directory, not its parent. No game objects or type parsing live here.
/// Schema 1: {schema:1, mod:guid, players:{platformId:{name,updatedUtc,values:{key:string}}},
/// orphans:{platformId:{key:string}}}. Unknown settings and JSON fields survive writes.
/// <para>Set and Orphan change memory at once and never touch the disk. <see cref="Tick"/> writes
/// them coalesced, <see cref="FlushDelaySeconds"/> after the first unsaved change, with an atomic,
/// flushed replacement; <see cref="Flush"/> writes now (unload). A crash loses at most the
/// changes of that window and never leaves a partial file.</para>
/// <para>External edits are detected (on Tick and before every write) and merged: a key the
/// edit changed takes the file's value; this store's unsaved changes to other keys are re-applied.
/// A corrupt file, at construction or later, is copied to <c>&lt;guid&gt;.json.bad</c> with a
/// warning and replaced by this store's state on the next write; a file that cannot be read yet
/// (locked) is retried and never overwritten unread. Construction only throws for invalid
/// arguments or a linked path.</para>
/// </summary>
internal sealed class PlayerSettingsStore
{
    internal const double FlushDelaySeconds = 1.5;
    internal const double CheckIntervalSeconds = 2;
    internal const double RetrySeconds = 5;

    private readonly string _path;
    private readonly string _modGuid;
    private readonly Action<string>? _warn;
    private readonly Func<double> _clock;
    private readonly List<Op> _pending = new();
    private JsonObject _document;
    private JsonObject _base;           // the document as of _disk (what this store last read or wrote)
    private byte[]? _disk;              // bytes last read or written; null = no file
    private bool _loaded;               // false while an existing file could not be read
    private bool _dirty;                // a write is needed (unsaved changes or a corrupt file to replace)
    private bool _failing;
    private double _flushAt, _nextCheck;
    private (long Length, DateTime Time) _stamp = (-1, default);

    private readonly record struct Op(bool Orphan, string Player, string Key, string? Value, string Name, string Stamp);

    internal PlayerSettingsStore(string root, string modGuid, Action<string>? warn = null, Func<double>? clock = null)
    {
        if (string.IsNullOrWhiteSpace(root)) throw new ArgumentException("Root is required.", nameof(root));
        ValidateGuid(modGuid);
        _modGuid = modGuid;
        _warn = warn;
        _clock = clock ?? (() => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency);
        _path = Path.Combine(Path.GetFullPath(root), modGuid + ".json");
        EnsureNoLinks(_path);
        _document = Empty();
        _base = Empty();
        TryLoad();
    }

    /// <summary>Whether unsaved changes wait for the next write.</summary>
    internal bool HasUnsavedChanges => _dirty;

    /// <summary>Whether the existing file has been read (false while it is locked or unreadable).</summary>
    internal bool Loaded => _loaded;

    internal string Get(ulong player, string key, string defaultValue)
    {
        ValidateArguments(player, key);
        ArgumentNullException.ThrowIfNull(defaultValue);
        return Lookup(_document, PlayerId(player), key) ?? defaultValue;
    }

    /// <summary>Record a non-default override, or remove the override on reset. Empty strings
    /// are valid values. An empty playerName preserves the last known name. A no-op returns false.
    /// Memory changes now; the disk follows on <see cref="Tick"/> or <see cref="Flush"/>.</summary>
    internal bool Set(ulong player, string key, string value, string defaultValue, string playerName = "")
    {
        ValidateArguments(player, key);
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(defaultValue);
        ArgumentNullException.ThrowIfNull(playerName);
        string id = PlayerId(player);
        var current = _document["players"]![id] as JsonObject;
        string? previous = Lookup(_document, id, key);
        bool reset = string.Equals(value, defaultValue, StringComparison.Ordinal);
        if (reset && previous == null) return false;
        if (!reset && previous == value &&
            (playerName.Length == 0 || current?["name"]?.GetValue<string>() == playerName)) return false;
        Record(new Op(false, id, key, reset ? null : value, playerName, UtcStamp()));
        return true;
    }

    /// <summary>After the caller detects a type parse failure, move its raw value to orphans.
    /// Returns true once per moved value, so the caller can log once. A newer malformed value
    /// replaces the same orphan key; setting/resetting an active value leaves orphans intact.</summary>
    internal bool Orphan(ulong player, string key)
    {
        ValidateArguments(player, key);
        string id = PlayerId(player);
        if (Lookup(_document, id, key) is not string value) return false;
        Record(new Op(true, id, key, value, "", UtcStamp()));
        return true;
    }

    /// <summary>Game-thread pump: retries an unreadable file, writes due changes and picks up
    /// external edits. Never throws for I/O; a failure warns once and retries.</summary>
    internal void Tick()
    {
        double now = _clock();
        try
        {
            if (!_loaded)
            {
                if (now < _nextCheck) return;
                _nextCheck = now + CheckIntervalSeconds;
                if (!TryLoad()) return;
            }
            if (_dirty && now >= _flushAt) Flush();
            else if (now >= _nextCheck)
            {
                _nextCheck = now + CheckIntervalSeconds;
                CheckExternal();
            }
            if (_failing) { _failing = false; _warn?.Invoke("Player settings for " + _modGuid + " saved again."); }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            if (!_failing) _warn?.Invoke("Player settings for " + _modGuid + " could not be saved; retrying: " + e.Message);
            _failing = true;
            _flushAt = now + RetrySeconds;
        }
    }

    /// <summary>Write unsaved changes now (merging any external edit first). Throws on I/O
    /// failure; the changes stay in memory and are retried.</summary>
    internal void Flush()
    {
        if (!_dirty) return;
        if (!_loaded && !TryLoad())
            throw new IOException("Player settings file could not be read; it is not overwritten unread.");
        for (int attempt = 0; ; attempt++)
        {
            EnsureNoLinks(_path);
            var current = ReadExisting(_path);
            if (!Same(current, _disk)) Merge(current);
            if (!_dirty) return;
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(_document, new JsonSerializerOptions { WriteIndented = true });
            string temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(bytes);
                    stream.Flush(flushToDisk: true);
                }
                // An edit that landed while the temp file was written is merged and written again.
                EnsureNoLinks(_path);
                if (!Same(ReadExisting(_path), _disk))
                {
                    if (attempt >= 2) throw new IOException("Player settings keep changing on disk; retrying later.");
                    continue;
                }
                if (_disk == null) File.Move(temp, _path);
                else File.Replace(temp, _path, null);
                _disk = bytes;
                _base = Parse(bytes);
                _pending.Clear();
                _dirty = false;
                _stamp = Stat();
                return;
            }
            finally
            {
                try { File.Delete(temp); }
                catch (IOException) { /* Do not mask the original write failure. */ }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private void Record(Op op)
    {
        Apply(_document, op);
        _pending.Add(op);
        MarkDirty();
    }

    private void MarkDirty()
    {
        if (!_dirty) _flushAt = _clock() + FlushDelaySeconds;
        _dirty = true;
    }

    private bool TryLoad()
    {
        byte[]? bytes;
        try { bytes = ReadExisting(_path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            if (_loaded || !_failing) _warn?.Invoke("Player settings for " + _modGuid + " could not be read yet; retrying: " + e.Message);
            _failing = true;
            return false;
        }
        if (!_loaded)
        {
            // Nothing was known about the file: every own change made meanwhile is replayed.
            JsonObject? parsed = bytes == null ? Empty() : TryParse(bytes);
            if (parsed == null)
            {
                // Start empty. The corrupt file stays in place (and is replaced by the next
                // write) only once its backup exists; without one it is retried, never overwritten.
                try { Quarantine(bytes!, atLoad: true); }
                catch (IOException e)
                {
                    if (!_failing) _warn?.Invoke(e.Message);
                    _failing = true;
                    return false;
                }
                parsed = Empty();
            }
            _failing = false;
            _loaded = true;
            _disk = bytes;
            var unread = new List<Op>(_pending);
            _base = parsed;
            _document = Clone(parsed);
            foreach (var op in unread) Apply(_document, op);
            _stamp = Stat();
            return true;
        }
        Merge(bytes);
        return true;
    }

    private void CheckExternal()
    {
        var stamp = Stat();
        if (stamp == _stamp) return;
        EnsureNoLinks(_path);
        var current = ReadExisting(_path);
        _stamp = stamp;
        if (!Same(current, _disk)) Merge(current);
    }

    /// <summary>Adopt the file's current content: keys it changed take its value; this store's
    /// unsaved changes to other keys are re-applied. A corrupt file keeps this store's state and
    /// is backed up before the next write replaces it.</summary>
    private void Merge(byte[]? current)
    {
        JsonObject? next = current == null ? Empty() : TryParse(current);
        if (next == null)
        {
            Quarantine(current!, atLoad: false);
            _disk = current;
            MarkDirty();
            return;
        }
        var kept = new List<Op>(_pending.Count);
        var document = Clone(next);
        foreach (var op in _pending)
        {
            if (Lookup(_base, op.Player, op.Key) != Lookup(next, op.Player, op.Key)) continue;
            Apply(document, op);
            kept.Add(op);
        }
        _pending.Clear();
        _pending.AddRange(kept);
        _document = document;
        _base = next;
        _disk = current;
        _stamp = Stat();
        if (_pending.Count == 0) _dirty = false;
    }

    private void Quarantine(byte[] bytes, bool atLoad)
    {
        string backup = _path + ".bad";
        try
        {
            if (File.Exists(backup))
            {
                if (File.ReadAllBytes(backup).SequenceEqual(bytes)) { Warn(backup, atLoad); return; }
                backup = _path + "." + DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmssfff", CultureInfo.InvariantCulture) + ".bad";
            }
            using (var stream = new FileStream(backup, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            Warn(backup, atLoad);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Without a backup the corrupt file must not be replaced; the next write retries it.
            throw new IOException("Player settings file is invalid and could not be backed up: " + e.Message, e);
        }
    }

    private void Warn(string backup, bool atLoad) =>
        _warn?.Invoke("Player settings file for " + _modGuid + " is invalid; it was kept as " +
            Path.GetFileName(backup) + (atLoad ? ". Starting with default values." : ". Keeping the last values read; the next save replaces the file."));

    private static void Apply(JsonObject document, Op op)
    {
        var players = document["players"] as JsonObject ?? (JsonObject)(document["players"] = new JsonObject());
        if (op.Orphan)
        {
            if (players[op.Player]?["values"] is not JsonObject active || active[op.Key] == null) return;
            var orphans = document["orphans"] as JsonObject ?? (JsonObject)(document["orphans"] = new JsonObject());
            if (orphans[op.Player] is not JsonObject moved) orphans[op.Player] = moved = new JsonObject();
            moved[op.Key] = op.Value;
            active.Remove(op.Key);
            players[op.Player]!["updatedUtc"] = op.Stamp;
            return;
        }
        if (players[op.Player] is not JsonObject record)
        {
            if (op.Value == null) return;
            players[op.Player] = record = new JsonObject { ["name"] = op.Name, ["values"] = new JsonObject() };
        }
        if (record["values"] is not JsonObject values) record["values"] = values = new JsonObject();
        if (op.Value == null) values.Remove(op.Key);
        else values[op.Key] = op.Value;
        if (op.Name.Length != 0 || record["name"] == null) record["name"] = op.Name;
        record["updatedUtc"] = op.Stamp;
    }

    private static string? Lookup(JsonObject document, string player, string key) =>
        document["players"]?[player]?["values"]?[key]?.GetValue<string>();

    private JsonObject Empty() => new()
    {
        ["schema"] = 1, ["mod"] = _modGuid,
        ["players"] = new JsonObject(), ["orphans"] = new JsonObject()
    };

    private static JsonObject Clone(JsonObject document) => (JsonObject)JsonNode.Parse(document.ToJsonString())!;

    private JsonObject? TryParse(byte[] bytes)
    {
        try { return Parse(bytes); }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or InvalidDataException)
        { return null; }
    }

    private JsonObject Parse(byte[] bytes)
    {
        using var parsed = JsonDocument.Parse(bytes);
        ValidateUniqueProperties(parsed.RootElement);
        var document = JsonNode.Parse(bytes) as JsonObject
            ?? throw new InvalidDataException("Player settings must be an object.");
        if (document["schema"]?.GetValue<int>() != 1 ||
            document["mod"]?.GetValue<string>() != _modGuid)
            throw new InvalidDataException("Player settings schema or mod does not match.");
        ValidatePlayers(RequiredObject(document, "players"), false);
        ValidatePlayers(RequiredObject(document, "orphans"), true);
        return document;
    }

    private (long, DateTime) Stat()
    {
        try
        {
            var info = new FileInfo(_path);
            return info.Exists ? (info.Length, info.LastWriteTimeUtc) : (-1, default);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return (-2, default); }
    }

    private static bool Same(byte[]? a, byte[]? b) =>
        a == null ? b == null : b != null && a.AsSpan().SequenceEqual(b);

    private static string UtcStamp() => DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);

    private static byte[]? ReadExisting(string path)
    {
        try { return File.ReadAllBytes(path); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    private static void EnsureNoLinks(string path)
    {
        for (string? part = path; part != null; part = Path.GetDirectoryName(part))
        {
            try
            {
                if ((File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Player settings paths must not contain symbolic links or junctions.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    private static void ValidateGuid(string guid)
    {
        if (string.IsNullOrWhiteSpace(guid)) throw new ArgumentException("Mod GUID is required.", nameof(guid));
        if (guid.Length > 128 || guid[0] == '.' || guid[^1] == '.' || guid.Contains("..") ||
            guid.Any(c => !(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9') && c != '.' && c != '_' && c != '-'))
            throw new ArgumentException("Mod GUID must be a safe ASCII filename stem of at most 128 characters.", nameof(guid));
        string stem = guid.Split('.')[0].ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" ||
            (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) ||
             stem.StartsWith("LPT", StringComparison.Ordinal)) && stem[3] is >= '0' and <= '9'))
            throw new ArgumentException("Mod GUID must not use a reserved device name.", nameof(guid));
    }

    private static string PlayerId(ulong player) => player.ToString(CultureInfo.InvariantCulture);

    private static void ValidateArguments(ulong player, string key)
    {
        if (player == 0) throw new ArgumentOutOfRangeException(nameof(player), "A stable nonzero platform ID is required.");
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("Setting key is required.", nameof(key));
    }

    private static JsonObject RequiredObject(JsonObject parent, string key) =>
        parent[key] as JsonObject ?? throw new InvalidDataException("Expected object: " + key);

    private static void ValidatePlayers(JsonObject players, bool orphans)
    {
        foreach (var player in players)
        {
            if (!ulong.TryParse(player.Key, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ||
                id == 0 || PlayerId(id) != player.Key || player.Value is not JsonObject record)
                throw new InvalidDataException("Invalid platform ID or player record.");
            var values = orphans ? record : RequiredObject(record, "values");
            if (!orphans)
            {
                if (record["name"] == null || record["updatedUtc"] == null)
                    throw new InvalidDataException("Missing player metadata.");
                _ = record["name"]!.GetValue<string>();
                _ = record["updatedUtc"]!.GetValue<string>();
            }
            foreach (var value in values)
            {
                if (string.IsNullOrWhiteSpace(value.Key) || value.Value == null)
                    throw new InvalidDataException("Invalid setting key or null value.");
                _ = value.Value.GetValue<string>();
            }
        }
    }

    private static void ValidateUniqueProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("Duplicate JSON property.");
                ValidateUniqueProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) ValidateUniqueProperties(item);
    }
}
