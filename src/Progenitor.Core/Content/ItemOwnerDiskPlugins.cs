using System;
using System.Collections.Generic;
using System.IO;

namespace Progenitor.Content;

/// <summary>
/// Whether an item owner's plugin is absent from disk, for automatic item removal. Reads assembly
/// metadata without loading it; an unreadable or staged DLL, a link or too many files conservatively
/// counts as present.
///
/// <para>One instance is one look at the disk: the plugin folders are walked at most once and each
/// provenance file is checked at most once, however many owners are asked. The removal tick makes
/// one per tick; asking per owner re-read every DLL and stat-ed every row (two file checks for each
/// of 128 rows sharing one <c>pack.json</c>) for each of 80+ owners.</para>
///
/// <para>A walk still lists the folders and stats every DLL each time, but a <see cref="GuidCache"/>
/// shared across instances keeps what each unchanged DLL declares (same length and write time), so
/// only a new or changed file is read again. The removal job rechecks the disk at every step (four a
/// second); reading every DLL's metadata at each of them cost tens of milliseconds a frame.</para>
/// </summary>
internal sealed class ItemOwnerDiskPlugins
{
    internal const int MaxFiles = 4096;
    internal static readonly string[] Folders = { "plugins", "progenitor-reload", "progenitor-pending" };

    private readonly string _root;
    private readonly Func<string, IReadOnlyList<string>?> _readGuids;
    private readonly GuidCache? _cache;
    private readonly Dictionary<string, bool> _fileExists = new(StringComparer.Ordinal);
    private HashSet<string>? _declared;
    private bool _walked;
    private bool _blocked;

    /// <summary>How many times the plugin folders were walked (tests).</summary>
    internal int Walks { get; private set; }

    /// <param name="readGuids">The GUIDs a DLL declares, or null when it cannot be read.</param>
    /// <param name="cache">What unchanged DLLs declared at an earlier walk; none when null (every DLL is read).</param>
    internal ItemOwnerDiskPlugins(string root, Func<string, IReadOnlyList<string>?>? readGuids = null, GuidCache? cache = null)
    {
        _root = root;
        _readGuids = readGuids ?? DefaultReadGuids;
        _cache = cache;
    }

    /// <summary>
    /// What each DLL declared, keyed by path and checked against its length and write time. A file written
    /// within <see cref="TrustAge"/> of the read is not kept (a write in the same timestamp tick could follow),
    /// nor is an unreadable one. Game-free; main thread only.
    /// </summary>
    internal sealed class GuidCache
    {
        internal static readonly TimeSpan TrustAge = TimeSpan.FromSeconds(2);

        private readonly Dictionary<string, (long Length, DateTime WriteUtc, IReadOnlyList<string> Ids)> _byPath = new(StringComparer.OrdinalIgnoreCase);
        private readonly Func<DateTime> _utcNow;

        internal GuidCache(Func<DateTime>? utcNow = null) => _utcNow = utcNow ?? (() => DateTime.UtcNow);

        /// <summary>DLL metadata reads that reached the reader (tests and diagnostics).</summary>
        internal int Reads { get; private set; }

        internal int Count => _byPath.Count;

        /// <summary>The GUIDs <paramref name="file"/> declares, from the cache when its length and write time match.</summary>
        internal IReadOnlyList<string>? Get(string file, long length, DateTime writeUtc, Func<string, IReadOnlyList<string>?> read)
        {
            if (_byPath.TryGetValue(file, out var hit) && hit.Length == length && hit.WriteUtc == writeUtc) return hit.Ids;
            Reads++;
            var ids = read(file);
            if (ids != null && _utcNow() - writeUtc > TrustAge) _byPath[file] = (length, writeUtc, ids);
            else _byPath.Remove(file);
            return ids;
        }

        internal void Clear() => _byPath.Clear();
    }

    private static IReadOnlyList<string>? DefaultReadGuids(string file) =>
        PluginActions.PluginFileActions.TryReadPluginGuids(file, out var ids) ? ids : null;

    /// <summary>
    /// True when no provenance file of <paramref name="owner"/> (or its staged <c>.next</c>) exists and
    /// no readable DLL under the plugin folders declares it.
    /// </summary>
    internal bool Absent(ItemOwnerLedger ledger, string owner)
    {
        try
        {
            foreach (var row in ledger.RowsOf(owner))
                if (Exists(row.AssemblyRelativePath)) return false;
            Walk();
            return !_blocked && !_declared!.Contains(owner);
        }
        catch { return false; }
    }

    private bool Exists(string relative)
    {
        if (_fileExists.TryGetValue(relative, out bool exists)) return exists;
        var path = Path.Combine(_root, relative);
        exists = File.Exists(path) || File.Exists(path + ".next");
        _fileExists[relative] = exists;
        return exists;
    }

    private void Walk()
    {
        if (_walked) return;
        _walked = true;
        Walks++;
        _declared = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            int count = 0;
            foreach (var folder in Folders)
            {
                var path = Path.Combine(_root, folder);
                if (!Directory.Exists(path)) continue;
                var pending = new Stack<string>(); pending.Push(path);
                while (pending.Count != 0)
                {
                    var current = pending.Pop();
                    if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) { _blocked = true; return; }
                    foreach (var child in Directory.GetDirectories(current)) pending.Push(child);
                    foreach (var file in Directory.GetFiles(current, "*.dll*"))
                    {
                        if (++count > MaxFiles) { _blocked = true; return; }
                        IReadOnlyList<string>? ids;
                        if (_cache == null) ids = _readGuids(file);
                        else
                        {
                            var info = new FileInfo(file);
                            ids = _cache.Get(file, info.Length, info.LastWriteTimeUtc, _readGuids);
                        }
                        if (ids == null) { _blocked = true; return; }
                        foreach (var id in ids) _declared.Add(id);
                    }
                }
            }
        }
        catch { _blocked = true; }
    }
}
