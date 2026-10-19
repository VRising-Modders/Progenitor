using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using Progenitor.Persistence;

namespace Progenitor.Content;

internal sealed record ItemOwnerRecord(string ItemId, int Guid, string PluginId, string AssemblyRelativePath, int SourceGuid = 0);

/// <summary>Durable historical item ownership. Missing registrations never imply removal.</summary>
internal sealed class ItemOwnerLedger
{
    internal const string RelativeFileName = "config/progenitor-item-owners.json";
    private List<ItemOwnerRecord> _rows;
    private Dictionary<int, int> _rowIndices;
    private HashSet<string> _removed;
    // Rows per owner, so MarkRemoved checks ownership without scanning every row (a ledger of
    // 10,000 rows with 80 removed owners cost 800,000 comparisons per load).
    private Dictionary<string, int> _ownerRowCounts;
    // True while the collections above belong to the cached parse: the first change copies them.
    private bool _shared;
    // Owners and rows per owner; built on first use, dropped on any row change.
    private OwnerIndex? _index;
    private string? _loadedHash;
    public bool IsValid { get; private set; } = true;
    public string Error { get; private set; } = "";
    public IReadOnlyList<ItemOwnerRecord> Rows => _rows.AsReadOnly();

    private sealed class OwnerIndex
    {
        public readonly List<string> Owners = new();
        public readonly Dictionary<string, List<ItemOwnerRecord>> ByOwner = new(StringComparer.Ordinal);
    }

    public ItemOwnerLedger()
    {
        _rows = new List<ItemOwnerRecord>();
        _rowIndices = new Dictionary<int, int>();
        _removed = new HashSet<string>(StringComparer.Ordinal);
        _ownerRowCounts = new Dictionary<string, int>(StringComparer.Ordinal);
    }

    /// <summary>A ledger reading <paramref name="source"/>'s collections until its first change.</summary>
    private ItemOwnerLedger(ItemOwnerLedger source)
    {
        _rows = source._rows;
        _rowIndices = source._rowIndices;
        _removed = source._removed;
        _ownerRowCounts = source._ownerRowCounts;
        _index = source._index;
        _shared = true;
        _loadedHash = source._loadedHash;
        IsValid = source.IsValid;
        Error = source.Error;
    }

    private void BeforeChange(bool rows)
    {
        if (rows) _index = null;
        if (!_shared) return;
        _rows = new List<ItemOwnerRecord>(_rows);
        _rowIndices = new Dictionary<int, int>(_rowIndices);
        _removed = new HashSet<string>(_removed, StringComparer.Ordinal);
        _ownerRowCounts = new Dictionary<string, int>(_ownerRowCounts, StringComparer.Ordinal);
        _shared = false;
    }

    /// <summary>Each owner once, in the order of its first row (<c>Rows.Select(r => r.PluginId).Distinct()</c>).</summary>
    internal IReadOnlyList<string> Owners => Index().Owners;

    /// <summary>The owner's rows in ledger order (<c>Rows.Where(r => r.PluginId == pluginId)</c>).</summary>
    internal IReadOnlyList<ItemOwnerRecord> RowsOf(string pluginId) =>
        Index().ByOwner.TryGetValue(pluginId, out var rows) ? rows : Array.Empty<ItemOwnerRecord>();

    private OwnerIndex Index()
    {
        var index = _index;
        if (index != null) return index;
        index = new OwnerIndex();
        foreach (var row in _rows)
        {
            if (!index.ByOwner.TryGetValue(row.PluginId, out var list))
            {
                index.ByOwner.Add(row.PluginId, list = new List<ItemOwnerRecord>());
                index.Owners.Add(row.PluginId);
            }
            list.Add(row);
        }
        return _index = index;
    }

    // ---- load cache ----------------------------------------------------------------------------
    // The content-pack scan and the removal tick read the ledger several times a second, and the
    // parse is the expensive part (10,000 rows: 30 to 60 ms and several MB of garbage, a 2.6 MB
    // byte array on the large-object heap among it). Each Load still returns its own ledger: it
    // reads the last parse of the same bytes and copies it on its first change. Bytes are
    // identified by SHA-256, the identity Save's concurrency check already uses. Reading and
    // hashing are skipped only while the file's size and write time are unchanged AND that write
    // time was more than StatTrustAge older than the read of the cached bytes: any later write
    // then carries a later write time, even with the file system's coarse timestamp clock.
    internal static readonly TimeSpan StatTrustAge = TimeSpan.FromSeconds(2);
    private static readonly object CacheGate = new();
    private static CacheEntry? _cache;

    /// <summary>Tests: the clock the stat-trust rule reads.</summary>
    internal static Func<DateTime> UtcNow = () => DateTime.UtcNow;

    /// <summary>Tests: how many times Load parsed JSON since the last reset.</summary>
    internal static int ParseCount { get; private set; }

    /// <summary>Tests: how many times Load read the file's bytes since the last reset.</summary>
    internal static int ReadCount { get; private set; }

    internal static void ResetCacheForTests()
    {
        lock (CacheGate) { _cache = null; ParseCount = 0; ReadCount = 0; }
        UtcNow = () => DateTime.UtcNow;
    }

    private sealed class CacheEntry
    {
        public string Path = "";
        public string Hash = "";
        public long Length;
        public DateTime WriteUtc;
        public bool StatTrusted;
        public ItemOwnerLedger Parsed = null!;
    }

    public static ItemOwnerLedger Load(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var before = new FileInfo(full);
            if (!before.Exists) return new ItemOwnerLedger();
            long length = before.Length;
            DateTime write = before.LastWriteTimeUtc;
            lock (CacheGate)
            {
                var hit = _cache;
                if (hit != null && hit.StatTrusted && hit.Length == length && hit.WriteUtc == write
                    && string.Equals(hit.Path, full, StringComparison.OrdinalIgnoreCase))
                    return new ItemOwnerLedger(hit.Parsed);
            }
            DateTime readAt = UtcNow();
            var bytes = File.ReadAllBytes(full);
            var hash = Convert.ToHexString(SHA256.HashData(bytes));
            var after = new FileInfo(full);
            // The stat key stands for these bytes only when two identical stats of an old file bracket the read.
            bool trusted = after.Exists && after.Length == length && after.LastWriteTimeUtc == write
                && bytes.LongLength == length && readAt - write > StatTrustAge;
            lock (CacheGate)
            {
                ReadCount++;
                var hit = _cache;
                if (hit != null && hit.Hash == hash && string.Equals(hit.Path, full, StringComparison.OrdinalIgnoreCase))
                {
                    hit.Length = length; hit.WriteUtc = write; hit.StatTrusted = trusted;
                    return new ItemOwnerLedger(hit.Parsed);
                }
            }
            var parsed = Parse(bytes, hash);
            parsed.Index(); // shared, read-only from here on
            lock (CacheGate)
            {
                ParseCount++;
                _cache = new CacheEntry { Path = full, Hash = hash, Length = length, WriteUtc = write, StatTrusted = trusted, Parsed = parsed };
            }
            return new ItemOwnerLedger(parsed);
        }
        catch (Exception e) { return new ItemOwnerLedger { IsValid = false, Error = e.Message }; }
    }

    private static ItemOwnerLedger Parse(byte[] bytes, string hash)
    {
        var ledger = new ItemOwnerLedger();
        try
        {
            ledger._loadedHash = hash;
            using var doc = JsonFiles.Parse(bytes);
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
        catch (Exception e)
        {
            ledger.IsValid = false; ledger.Error = e.Message;
            ledger._rows.Clear(); ledger._rowIndices.Clear(); ledger._removed.Clear(); ledger._ownerRowCounts.Clear();
            ledger._index = null;
        }
        return ledger;
    }

    public bool Register(string itemId, int guid, string pluginId, string assemblyRelativePath, int sourceGuid = 0, bool clearRemoval = true)
    {
        if (!IsValid || string.IsNullOrWhiteSpace(itemId) || string.IsNullOrWhiteSpace(pluginId) || guid == 0
            || !TryCanonicalPath(assemblyRelativePath, out var path)) return false;
        var previous = _rowIndices.TryGetValue(guid, out int index) ? _rows[index] : null;
        if (previous != null && (previous.PluginId != pluginId || previous.ItemId != itemId)) return false;
        var record = new ItemOwnerRecord(itemId, guid, pluginId, path, sourceGuid);
        BeforeChange(rows: true);
        if (previous != null) _rows[index] = record;
        else
        {
            _rowIndices.Add(guid, _rows.Count); _rows.Add(record);
            _ownerRowCounts[pluginId] = _ownerRowCounts.TryGetValue(pluginId, out int n) ? n + 1 : 1;
        }
        if (clearRemoval) _removed.Remove(pluginId);
        return true;
    }

    public bool MarkRemoved(string pluginId)
    {
        if (!IsValid || !_ownerRowCounts.ContainsKey(pluginId)) return false;
        if (_removed.Contains(pluginId)) return true;
        BeforeChange(rows: false);
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
        var owned = RowsOf(pluginId);
        if (owned.Any(r => r.AssemblyRelativePath.StartsWith("progenitor-content/", StringComparison.OrdinalIgnoreCase)
            && r.AssemblyRelativePath.EndsWith("/pack.json", StringComparison.OrdinalIgnoreCase)))
            confirmedStartupAbsence = false;
        if (!IsValid || !inventoryResolved || activeOwner || diskPresent || stagedReplacement
            || (!_removed.Contains(pluginId) && !confirmedStartupAbsence)) return Array.Empty<ItemOwnerRecord>();
        return owned.ToArray();
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
