using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;

namespace Progenitor.Content;

internal sealed record ItemOwnerRecord(string ItemId, int Guid, string PluginId, string AssemblyRelativePath, int SourceGuid = 0);

/// <summary>Durable historical item ownership. Missing registrations never imply removal.</summary>
internal sealed class ItemOwnerLedger
{
    internal const string RelativeFileName = "config/progenitor-item-owners.json";
    private readonly List<ItemOwnerRecord> _rows = new();
    private readonly Dictionary<int, int> _rowIndices = new();
    private readonly HashSet<string> _removed = new(StringComparer.Ordinal);
    private string? _loadedHash;
    public bool IsValid { get; private set; } = true;
    public string Error { get; private set; } = "";
    public IReadOnlyList<ItemOwnerRecord> Rows => _rows.AsReadOnly();

    public static ItemOwnerLedger Load(string path)
    {
        var ledger = new ItemOwnerLedger();
        try
        {
            if (!File.Exists(path)) return ledger;
            var bytes = File.ReadAllBytes(path);
            ledger._loadedHash = Convert.ToHexString(SHA256.HashData(bytes));
            using var doc = JsonDocument.Parse(bytes);
            var root = doc.RootElement;
            if (root.GetProperty("schema").GetInt32() != 1) throw new FormatException("Unsupported item ownership schema.");
            var seen = new HashSet<int>();
            foreach (var row in root.GetProperty("items").EnumerateArray())
            {
                if (!seen.Add(row.GetProperty("guid").GetInt32())) throw new FormatException("Duplicate item ownership.");
                if (!ledger.Register(row.GetProperty("itemId").GetString()!, row.GetProperty("guid").GetInt32(),
                    row.GetProperty("pluginId").GetString()!, row.GetProperty("assemblyRelativePath").GetString()!,
                    row.TryGetProperty("sourceGuid", out var source) ? source.GetInt32() : 0))
                    throw new FormatException("Invalid or conflicting item ownership.");
            }
            foreach (var owner in root.GetProperty("removedOwners").EnumerateArray())
                if (!ledger.MarkRemoved(owner.GetString()!)) throw new FormatException("Unknown removed owner.");
        }
        catch (Exception e) { ledger.IsValid = false; ledger.Error = e.Message; ledger._rows.Clear(); ledger._rowIndices.Clear(); ledger._removed.Clear(); }
        return ledger;
    }

    public bool Register(string itemId, int guid, string pluginId, string assemblyRelativePath, int sourceGuid = 0, bool clearRemoval = true)
    {
        if (!IsValid || string.IsNullOrWhiteSpace(itemId) || string.IsNullOrWhiteSpace(pluginId) || guid == 0
            || !TryCanonicalPath(assemblyRelativePath, out var path)) return false;
        var previous = _rowIndices.TryGetValue(guid, out int index) ? _rows[index] : null;
        if (previous != null && (previous.PluginId != pluginId || previous.ItemId != itemId)) return false;
        var record = new ItemOwnerRecord(itemId, guid, pluginId, path, sourceGuid);
        if (previous != null) _rows[index] = record;
        else { _rowIndices.Add(guid, _rows.Count); _rows.Add(record); }
        if (clearRemoval) _removed.Remove(pluginId);
        return true;
    }

    public bool MarkRemoved(string pluginId)
    {
        if (!IsValid || !_rows.Any(r => r.PluginId == pluginId)) return false;
        _removed.Add(pluginId);
        return true;
    }

    internal static bool RequestRemovalForFile(string bepInExRoot, string absoluteDllPath)
    {
        try
        {
            var root = Path.GetFullPath(bepInExRoot);
            var absolute = Path.GetFullPath(absoluteDllPath);
            var relative = Path.GetRelativePath(root, absolute);
            if (!TryCanonicalPath(relative, out var canonical)) return false;
            var file = Path.Combine(root, RelativeFileName);
            var ledger = Load(file);
            if (!ledger.IsValid) return false;
            var owners = ledger._rows.Where(r => string.Equals(r.AssemblyRelativePath, canonical, StringComparison.OrdinalIgnoreCase))
                .Select(r => r.PluginId).Distinct(StringComparer.Ordinal).ToArray();
            if (owners.Length == 0) return true;
            if (owners.All(owner => ledger._removed.Contains(owner))) return true;
            foreach (var owner in owners) ledger.MarkRemoved(owner);
            return ledger.Save(file);
        }
        catch { return false; }
    }

    public IReadOnlyList<ItemOwnerRecord> Eligible(string pluginId, bool activeOwner, bool diskPresent,
        bool stagedReplacement, bool confirmedStartupAbsence, bool inventoryResolved = true)
    {
        // Missing data-pack files may be a mount/deployment interruption. Only explicit
        // removal intent can authorize their inventory cleanup, even at process startup.
        if (_rows.Any(r => r.PluginId == pluginId
            && r.AssemblyRelativePath.StartsWith("progenitor-content/", StringComparison.OrdinalIgnoreCase)
            && r.AssemblyRelativePath.EndsWith("/pack.json", StringComparison.OrdinalIgnoreCase)))
            confirmedStartupAbsence = false;
        if (!IsValid || !inventoryResolved || activeOwner || diskPresent || stagedReplacement
            || (!_removed.Contains(pluginId) && !confirmedStartupAbsence)) return Array.Empty<ItemOwnerRecord>();
        return _rows.Where(r => r.PluginId == pluginId).ToArray();
    }

    public bool Save(string path)
    {
        if (!IsValid) return false;
        string? temp = null;
        try
        {
            var full = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            using var held = new FileStream(full + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var currentHash = File.Exists(full) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(full))) : null;
            if (currentHash != _loadedHash) throw new IOException("Item ownership changed since load; reload before saving.");
            temp = full + "." + System.Guid.NewGuid().ToString("N") + ".tmp";
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
                {
                    writer.WriteStartObject(); writer.WriteNumber("schema", 1); writer.WriteStartArray("items");
                    foreach (var r in _rows.OrderBy(r => r.Guid))
                    {
                        writer.WriteStartObject(); writer.WriteString("itemId", r.ItemId); writer.WriteNumber("guid", r.Guid);
                        writer.WriteString("pluginId", r.PluginId); writer.WriteString("assemblyRelativePath", r.AssemblyRelativePath);
                        writer.WriteNumber("sourceGuid", r.SourceGuid); writer.WriteEndObject();
                    }
                    writer.WriteEndArray(); writer.WriteStartArray("removedOwners");
                    foreach (var owner in _removed.OrderBy(o => o, StringComparer.Ordinal)) writer.WriteStringValue(owner);
                    writer.WriteEndArray(); writer.WriteEndObject(); writer.Flush();
                }
                stream.Flush(true);
            }
            if (File.Exists(full)) File.Replace(temp, full, null); else File.Move(temp, full);
            _loadedHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(full)));
            return true;
        }
        catch (Exception e) { Error = e.Message; return false; }
        finally { if (temp != null) try { if (File.Exists(temp)) File.Delete(temp); } catch { } }
    }

    internal static bool TryCanonicalPath(string? relative, out string canonical)
    {
        canonical = "";
        if (string.IsNullOrWhiteSpace(relative)) return false;
        var value = relative.Replace('\\', '/');
        if (value.StartsWith("/", StringComparison.Ordinal) || value.Contains(':')) return false;
        var parts = value.Split('/');
        if (parts.Any(p => p.Length == 0 || p == "." || p == ".." || p.Trim() != p
            || p.EndsWith(".", StringComparison.Ordinal) || p.Any(char.IsControl)
            || p.IndexOfAny(new[] { '<', '>', '"', '|', '?', '*', '\0' }) >= 0)) return false;
        canonical = value;
        return true;
    }
}

/// <summary>Coalesces main-thread ownership notifications into one durable transaction.
/// Failed I/O leaves the complete batch queued for a fresh load and retry.</summary>
internal sealed class ItemOwnerRegistrationQueue
{
    internal const int MaxPending = 16384;
    private readonly Dictionary<int, ItemOwnerRecord> _pending = new();
    internal int Count => _pending.Count;

    internal bool TryEnqueue(ItemOwnerRecord record)
    {
        if (_pending.TryGetValue(record.Guid, out var previous))
        {
            if (previous.ItemId != record.ItemId || previous.PluginId != record.PluginId) return false;
        }
        else if (_pending.Count >= MaxPending) return false;
        _pending[record.Guid] = record;
        return true;
    }

    internal bool TryFlush(string path, out string[] rejectedOwners, out string error, Func<string, bool>? ownerActive = null)
    {
        rejectedOwners = Array.Empty<string>(); error = "";
        if (_pending.Count == 0) return true;
        var ledger = ItemOwnerLedger.Load(path);
        if (!ledger.IsValid) { error = ledger.Error; return false; }
        var rejected = new HashSet<string>(StringComparer.Ordinal);
        bool changed = false;
        foreach (var row in _pending.Values)
        {
            if (ledger.Register(row.ItemId, row.Guid, row.PluginId, row.AssemblyRelativePath, row.SourceGuid,
                clearRemoval: ownerActive?.Invoke(row.PluginId) ?? true)) changed = true;
            else rejected.Add(row.PluginId);
        }
        if (changed && !ledger.Save(path)) { error = ledger.Error; return false; }
        rejectedOwners = rejected.ToArray();
        _pending.Clear();
        return true;
    }
}
