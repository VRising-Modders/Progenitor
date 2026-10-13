using System;
using System.Collections.Generic;

namespace Progenitor.Effects;

/// <summary>
/// Ownership ledger for mounts and content-files acquired in <c>PGbrowser</c>.
/// Refcounts stay inside this namespace: two handles sharing a dep do not unmount
/// each other's archive. Release is newest-first and idempotent. The game's <c>RTC</c>
/// ids are never recorded here.
/// </summary>
internal sealed class AssetAcquisitionLedger
{
    private readonly Dictionary<string, int> _archiveRefs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _fileRefs = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    /// <summary>Archives currently refcounted above zero.</summary>
    public int LiveArchives
    {
        get
        {
            lock (_gate) return CountPositive(_archiveRefs);
        }
    }

    /// <summary>Content-files currently refcounted above zero.</summary>
    public int LiveFiles
    {
        get
        {
            lock (_gate) return CountPositive(_fileRefs);
        }
    }

    /// <summary>Record that this handle now owns <paramref name="archiveId"/> (our mount only).</summary>
    public void AddArchive(string archiveId)
    {
        if (!CatalogAssetIdentity.IsArchiveId(archiveId))
            throw new ArgumentException("archiveId must be a 32-char hex ContentArchive id.", nameof(archiveId));
        if (string.Equals(archiveId, "RTC", StringComparison.Ordinal))
            throw new ArgumentException("The game RTC namespace is never ours.", nameof(archiveId));
        lock (_gate)
        {
            _archiveRefs.TryGetValue(archiveId, out int n);
            _archiveRefs[archiveId] = n + 1;
        }
    }

    /// <summary>Record that this handle now owns a loaded content-file named <paramref name="archiveId"/>.</summary>
    public void AddFile(string archiveId)
    {
        if (!CatalogAssetIdentity.IsArchiveId(archiveId))
            throw new ArgumentException("archiveId must be a 32-char hex ContentArchive id.", nameof(archiveId));
        lock (_gate)
        {
            _fileRefs.TryGetValue(archiveId, out int n);
            _fileRefs[archiveId] = n + 1;
        }
    }

    /// <summary>
    /// Drop this handle's claim on <paramref name="archiveId"/>. Returns true when the
    /// process-wide count hit zero and the caller must Unmount that archive.
    /// </summary>
    public bool ReleaseArchive(string archiveId)
    {
        lock (_gate) return Decrement(_archiveRefs, archiveId);
    }

    /// <summary>
    /// Drop this handle's claim on a loaded content-file. Returns true when the caller
    /// must <c>UnloadAsync</c> that file.
    /// </summary>
    public bool ReleaseFile(string archiveId)
    {
        lock (_gate) return Decrement(_fileRefs, archiveId);
    }

    public int ArchiveRefCount(string archiveId)
    {
        lock (_gate)
        {
            _archiveRefs.TryGetValue(archiveId, out int n);
            return n;
        }
    }

    public int FileRefCount(string archiveId)
    {
        lock (_gate)
        {
            _fileRefs.TryGetValue(archiveId, out int n);
            return n;
        }
    }

    /// <summary>
    /// Newest-first release plan for one handle's owned ids. Files before archives.
    /// Ids the process still needs (refcount would stay &gt; 0) are omitted from the
    /// physical-release lists.
    /// </summary>
    public ReleasePlan PlanRelease(IReadOnlyList<string> ownedFilesNewestFirst, IReadOnlyList<string> ownedArchivesNewestFirst)
    {
        if (ownedFilesNewestFirst == null) throw new ArgumentNullException(nameof(ownedFilesNewestFirst));
        if (ownedArchivesNewestFirst == null) throw new ArgumentNullException(nameof(ownedArchivesNewestFirst));

        var files = new List<string>();
        var archives = new List<string>();
        lock (_gate)
        {
            for (int i = 0; i < ownedFilesNewestFirst.Count; i++)
            {
                string id = ownedFilesNewestFirst[i];
                if (WouldHitZero(_fileRefs, id)) files.Add(id);
            }

            for (int i = 0; i < ownedArchivesNewestFirst.Count; i++)
            {
                string id = ownedArchivesNewestFirst[i];
                if (WouldHitZero(_archiveRefs, id)) archives.Add(id);
            }
        }

        return new ReleasePlan(files, archives);
    }

    public readonly struct ReleasePlan
    {
        public ReleasePlan(IReadOnlyList<string> filesNewestFirst, IReadOnlyList<string> archivesNewestFirst)
        {
            FilesNewestFirst = filesNewestFirst;
            ArchivesNewestFirst = archivesNewestFirst;
        }

        public IReadOnlyList<string> FilesNewestFirst { get; }
        public IReadOnlyList<string> ArchivesNewestFirst { get; }
    }

    static int CountPositive(Dictionary<string, int> map)
    {
        int n = 0;
        foreach (var kv in map)
            if (kv.Value > 0) n++;
        return n;
    }

    static bool WouldHitZero(Dictionary<string, int> map, string id)
    {
        if (string.IsNullOrEmpty(id)) return false;
        map.TryGetValue(id, out int n);
        return n == 1;
    }

    static bool Decrement(Dictionary<string, int> map, string id)
    {
        if (string.IsNullOrEmpty(id)) return false;
        if (!map.TryGetValue(id, out int n) || n <= 0) return false;
        if (n == 1)
        {
            map.Remove(id);
            return true;
        }

        map[id] = n - 1;
        return false;
    }
}
