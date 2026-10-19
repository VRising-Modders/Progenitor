using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using Progenitor.Persistence;

namespace Progenitor.Content;

internal enum ItemQuarantineState { Captured, RemovalPending, Quarantined, RestorePending, Restoring, Restored, Unavailable, Discarded }
internal sealed record ItemQuarantineDestination(string Kind, string PlayerId, string CastleId, string ContainerId, string OwnerId, int Slot);
internal sealed record ItemQuarantineRecord(string WorldId, string InstanceId, string PluginId, string ItemId, int Guid,
    string SnapshotJson, ItemQuarantineDestination Destination, ItemQuarantineState State = ItemQuarantineState.Captured, string Reason = "", bool RestoreDecisionPending = true);

/// <summary>Write-ahead journal. Pending engine operations require reconciliation after a crash;
/// neither removal nor restoration is inferred from a missing entity. Snapshot payloads remain opaque.</summary>
internal sealed class ItemQuarantineJournal
{
    internal const string RelativeDirectory = "config/progenitor-item-quarantine";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly List<ItemQuarantineRecord> _rows = new();
    private string? _loadedHash;
    private string? _loadedPath;
    public string WorldId { get; }
    public bool IsValid { get; private set; } = true;
    public string Error { get; private set; } = "";
    public IReadOnlyList<ItemQuarantineRecord> Rows => _rows.AsReadOnly();
    private ItemQuarantineJournal(string worldId) { WorldId = worldId; }

    public static string FileName(string root, string worldId) => Path.Combine(root, RelativeDirectory,
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(worldId))) + ".json");

    public static ItemQuarantineJournal Load(string path, string worldId)
    {
        var result = new ItemQuarantineJournal(worldId);
        try
        {
            if (string.IsNullOrWhiteSpace(worldId)) throw new FormatException("World identity is required.");
            result._loadedPath = Path.GetFullPath(path);
            if (!File.Exists(path)) return result;
            var bytes = File.ReadAllBytes(path);
            result._loadedHash = Hash(bytes);
            using var document = JsonFiles.Parse(bytes);
            var root = document.RootElement;
            if (root.GetProperty("schema").GetInt32() != 1 || root.GetProperty("worldId").GetString() != worldId)
                throw new FormatException("Unsupported quarantine schema or mismatched world.");
            var identities = new HashSet<string>(StringComparer.Ordinal);
            foreach (var element in root.GetProperty("items").EnumerateArray())
            {
                var row = element.Deserialize<ItemQuarantineRecord>()!;
                if (!result.Valid(row) || !identities.Add(row.InstanceId)) throw new FormatException("Invalid or duplicate quarantine record.");
                result._rows.Add(row);
            }
        }
        catch (Exception e) { result.IsValid = false; result.Error = e.Message; result._rows.Clear(); }
        return result;
    }

    /// <summary>Idempotent only for the exact same capture. A changed payload cannot overwrite retained history.</summary>
    public bool Capture(string path, ItemQuarantineRecord row)
    {
        if (!IsValid || !Valid(row) || row.State != ItemQuarantineState.Captured) return false;
        var prior = _rows.FirstOrDefault(r => r.InstanceId == row.InstanceId);
        if (prior != null) return prior.WorldId == row.WorldId && prior.PluginId == row.PluginId && prior.ItemId == row.ItemId
            && prior.Guid == row.Guid && prior.SnapshotJson == row.SnapshotJson && prior.Destination == row.Destination;
        _rows.Add(row);
        if (Save(path)) return true;
        _rows.Remove(row); return false;
    }

    /// <summary>Persist the claim before touching the engine. Final states require observed engine completion.</summary>
    public bool Transition(string path, string instanceId, ItemQuarantineState expected, ItemQuarantineState next, string reason = "")
    {
        if (!IsValid || reason == null || !Allowed(expected, next)) return false;
        var index = _rows.FindIndex(r => r.InstanceId == instanceId && r.State == expected);
        if (index < 0) return false;
        var old = _rows[index]; _rows[index] = old with { State = next, Reason = reason };
        if (Save(path)) return true;
        _rows[index] = old; return false;
    }

    public bool RequestRestore(string path, string pluginId)
    {
        if (!IsValid) return false;
        var old = _rows.ToArray();
        for (var i = 0; i < _rows.Count; i++)
            if (_rows[i].PluginId == pluginId && (_rows[i].State == ItemQuarantineState.Quarantined || _rows[i].State == ItemQuarantineState.Unavailable))
                _rows[i] = _rows[i] with { State = ItemQuarantineState.RestorePending, Reason = "", RestoreDecisionPending = false };
        if (_rows.SequenceEqual(old)) return true;
        if (Save(path)) return true;
        _rows.Clear(); _rows.AddRange(old); return false;
    }

    /// <summary>Retain a preflight explanation without changing the operation claim.</summary>
    public bool Annotate(string path, string instanceId, string reason)
    {
        if (!IsValid || reason == null) return false;
        var index = _rows.FindIndex(r => r.InstanceId == instanceId);
        if (index < 0) return false;
        var old = _rows[index];
        if (old.Reason == reason) return true;
        _rows[index] = old with { Reason = reason };
        if (Save(path)) return true;
        _rows[index] = old; return false;
    }

    /// <summary>Only call after proving the adapter made no engine mutation. An attempted or uncertain
    /// add must retain its Restoring claim; this method must never be used for crash recovery.</summary>
    public bool RestoreNotAttempted(string path, string instanceId, bool unavailable, string reason)
    {
        if (!IsValid || reason == null) return false;
        var index = _rows.FindIndex(r => r.InstanceId == instanceId && r.State == ItemQuarantineState.Restoring);
        if (index < 0) return false;
        var old = _rows[index];
        _rows[index] = old with { State = unavailable ? ItemQuarantineState.Unavailable : ItemQuarantineState.RestorePending, Reason = reason };
        if (Save(path)) return true;
        _rows[index] = old; return false;
    }

    /// <summary>Remember an explicit keep decision without making snapshots eligible for restoration.</summary>
    public bool Keep(string path, string pluginId)
    {
        if (!IsValid) return false;
        var old = _rows.ToArray();
        for (var i = 0; i < _rows.Count; i++)
            if (_rows[i].PluginId == pluginId && _rows[i].State is ItemQuarantineState.Quarantined or ItemQuarantineState.RestorePending or ItemQuarantineState.Unavailable)
                _rows[i] = _rows[i] with
                {
                    State = _rows[i].State == ItemQuarantineState.RestorePending ? ItemQuarantineState.Quarantined : _rows[i].State,
                    RestoreDecisionPending = false, Reason = "Explicit keep decision."
                };
        if (_rows.SequenceEqual(old)) return true;
        if (Save(path)) return true;
        _rows.Clear(); _rows.AddRange(old); return false;
    }

    /// <summary>Explicit irreversible decision; ambiguous engine claims cannot be discarded by this helper.</summary>
    public bool Discard(string path, string pluginId)
    {
        if (!IsValid) return false;
        var old = _rows.ToArray();
        for (var i = 0; i < _rows.Count; i++)
            if (_rows[i].PluginId == pluginId && Allowed(_rows[i].State, ItemQuarantineState.Discarded))
                _rows[i] = _rows[i] with { State = ItemQuarantineState.Discarded, Reason = "Explicit discard decision.", RestoreDecisionPending = false };
        if (_rows.SequenceEqual(old)) return true;
        if (Save(path)) return true;
        _rows.Clear(); _rows.AddRange(old); return false;
    }

    public IReadOnlyList<ItemQuarantineRecord> RestoreCandidates(string pluginId) => IsValid
        ? _rows.Where(r => r.PluginId == pluginId && r.State == ItemQuarantineState.RestorePending).ToArray()
        : Array.Empty<ItemQuarantineRecord>();

    // Full destinations remain RestorePending; missing identities or changed castle owners become Unavailable.
    // Final states describe running-world readback only. Runtime must restrict restoration to the same
    // in-memory world/incarnation capability; persisted snapshots alone never authorize replay after load.
    private static bool Allowed(ItemQuarantineState from, ItemQuarantineState to) => (from, to) switch
    {
        (ItemQuarantineState.Captured, ItemQuarantineState.RemovalPending) => true,
        (ItemQuarantineState.RemovalPending, ItemQuarantineState.Quarantined) => true,
        (ItemQuarantineState.RestorePending, ItemQuarantineState.Restoring) => true,
        (ItemQuarantineState.RestorePending, ItemQuarantineState.Unavailable) => true,
        (ItemQuarantineState.Restoring, ItemQuarantineState.Restored) => true,
        // An ambiguous restoring claim stays Restoring, excluding it from even explicit retry decisions.
        (ItemQuarantineState.Quarantined, ItemQuarantineState.Discarded) => true,
        (ItemQuarantineState.RestorePending, ItemQuarantineState.Discarded) => true,
        (ItemQuarantineState.Unavailable, ItemQuarantineState.Discarded) => true,
        _ => false
    };

    private bool Valid(ItemQuarantineRecord? row)
    {
        if (row == null || row.WorldId != WorldId || string.IsNullOrWhiteSpace(row.InstanceId)
            || string.IsNullOrWhiteSpace(row.PluginId) || string.IsNullOrWhiteSpace(row.ItemId) || row.Guid == 0
            || row.Destination == null || string.IsNullOrWhiteSpace(row.Destination.Kind) || row.Destination.Slot < -1
            || row.Destination.PlayerId == null || row.Destination.CastleId == null || row.Destination.ContainerId == null
            || row.Destination.OwnerId == null || row.Reason == null || !Enum.IsDefined(typeof(ItemQuarantineState), row.State)) return false;
        try { using var parsed = JsonDocument.Parse(row.SnapshotJson); return parsed.RootElement.ValueKind == JsonValueKind.Object; }
        catch { return false; }
    }

    private bool Save(string path)
    {
        string? temp = null;
        try
        {
            if (!IsValid || Path.GetFullPath(path) != _loadedPath) throw new IOException("Reload the journal at the requested path before writing.");
            var full = _loadedPath!;
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            using var held = new FileStream(full + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var current = File.Exists(full) ? Hash(File.ReadAllBytes(full)) : null;
            if (current != _loadedHash) throw new IOException("Quarantine changed since load; reload before writing.");
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new { schema = 1, worldId = WorldId, items = _rows.OrderBy(r => r.InstanceId, StringComparer.Ordinal) }, JsonOptions);
            temp = full + "." + System.Guid.NewGuid().ToString("N") + ".tmp";
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(bytes); stream.Flush(true); }
            if (File.Exists(full)) File.Replace(temp, full, null); else File.Move(temp, full);
            _loadedHash = Hash(bytes); Error = ""; return true;
        }
        catch (Exception e) { Error = e.Message; return false; }
        finally { if (temp != null) try { if (File.Exists(temp)) File.Delete(temp); } catch { } }
    }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
}
