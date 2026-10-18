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
/// Set and Orphan flush immediately (no Tick/debounce); Flush is a lifecycle-compatible no-op.
/// Construction throws on an unreadable/invalid existing file; never fall back to an empty
/// store. Callers should refuse writes, log once, and construct again after repair. Mutations
/// propagate I/O errors and only publish memory after atomic replacement succeeds.
/// One instance must own each mod file; external changes require constructing a fresh store.
/// </summary>
internal sealed class PlayerSettingsStore
{
    private readonly string _path;
    private JsonObject _document;
    private byte[]? _disk;

    internal PlayerSettingsStore(string root, string modGuid)
    {
        if (string.IsNullOrWhiteSpace(root)) throw new ArgumentException("Root is required.", nameof(root));
        ValidateGuid(modGuid);
        _path = Path.Combine(Path.GetFullPath(root), modGuid + ".json");
        EnsureNoLinks(_path);
        _disk = ReadExisting(_path);
        if (_disk == null)
        {
            _document = new JsonObject
            {
                ["schema"] = 1, ["mod"] = modGuid,
                ["players"] = new JsonObject(), ["orphans"] = new JsonObject()
            };
            return;
        }

        try
        {
            using var parsed = JsonDocument.Parse(_disk);
            ValidateUniqueProperties(parsed.RootElement);
            _document = JsonNode.Parse(_disk) as JsonObject
                ?? throw new InvalidDataException("Player settings must be an object.");
            if (_document["schema"]?.GetValue<int>() != 1 ||
                _document["mod"]?.GetValue<string>() != modGuid)
                throw new InvalidDataException("Player settings schema or mod does not match.");
            ValidatePlayers(RequiredObject(_document, "players"), false);
            ValidatePlayers(RequiredObject(_document, "orphans"), true);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            throw new InvalidDataException("Invalid player settings; existing file was not changed.", ex);
        }
    }

    internal string Get(ulong player, string key, string defaultValue)
    {
        ValidateArguments(player, key);
        ArgumentNullException.ThrowIfNull(defaultValue);
        return _document["players"]![PlayerId(player)]?["values"]?[key]?.GetValue<string>()
            ?? defaultValue;
    }

    /// <summary>Persist a non-default override, or remove the override on reset. Empty strings
    /// are valid values. An empty playerName preserves the last known name. A no-op returns false.</summary>
    internal bool Set(ulong player, string key, string value, string defaultValue, string playerName = "")
    {
        ValidateArguments(player, key);
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(defaultValue);
        ArgumentNullException.ThrowIfNull(playerName);
        string id = PlayerId(player);
        var current = _document["players"]![id] as JsonObject;
        var previous = current?["values"]?[key];
        bool reset = string.Equals(value, defaultValue, StringComparison.Ordinal);
        if (reset && previous == null) return false;
        if (!reset && previous?.GetValue<string>() == value &&
            (playerName.Length == 0 || current!["name"]!.GetValue<string>() == playerName)) return false;

        var next = (JsonObject)JsonNode.Parse(_document.ToJsonString())!;
        var players = RequiredObject(next, "players");
        if (players[id] is not JsonObject record)
        {
            record = new JsonObject { ["name"] = playerName, ["values"] = new JsonObject() };
            players[id] = record;
        }
        var values = RequiredObject(record, "values");
        if (reset) values.Remove(key);
        else values[key] = value;
        if (playerName.Length != 0) record["name"] = playerName;
        record["updatedUtc"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        Commit(next);
        return true;
    }

    /// <summary>After the caller detects a type parse failure, move its raw value to orphans.
    /// Returns true once per moved value, so the caller can log once. A newer malformed value
    /// replaces the same orphan key; setting/resetting an active value leaves orphans intact.</summary>
    internal bool Orphan(ulong player, string key)
    {
        ValidateArguments(player, key);
        string id = PlayerId(player);
        if (_document["players"]![id]?["values"]?[key] is not JsonNode value) return false;
        var next = (JsonObject)JsonNode.Parse(_document.ToJsonString())!;
        var orphans = RequiredObject(next, "orphans");
        if (orphans[id] is not JsonObject values)
        {
            values = new JsonObject();
            orphans[id] = values;
        }
        values[key] = JsonValue.Create(value.GetValue<string>());
        var record = (JsonObject)next["players"]![id]!;
        RequiredObject(record, "values").Remove(key);
        record["updatedUtc"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        Commit(next);
        return true;
    }

    internal void Flush() { /* Every successful mutation is already persisted. */ }

    private void Commit(JsonObject next)
    {
        EnsureNoLinks(_path);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(next, new JsonSerializerOptions { WriteIndented = true });
        string temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            // Do not treat Exists=false as evidence of absence: access-denied and other read
            // failures must propagate. Detect operator edits and stale store instances as well.
            EnsureNoLinks(_path);
            var current = ReadExisting(_path);
            if ((_disk == null) != (current == null) ||
                (_disk != null && !current!.SequenceEqual(_disk)))
                throw new IOException("Player settings changed on disk; reopen the store before writing.");
            if (_disk == null) File.Move(temp, _path);
            else File.Replace(temp, _path, null);
            _disk = bytes;
            _document = next;
        }
        finally
        {
            try { File.Delete(temp); }
            catch (IOException) { /* Do not mask the original write failure. */ }
            catch (UnauthorizedAccessException) { }
        }
    }

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
