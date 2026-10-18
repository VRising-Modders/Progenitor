using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using Progenitor.WorldState;

namespace Progenitor.Effects;

/// <summary>
/// Owned mount / load / preview / release of one initially-unloaded catalog asset in
/// Progenitor's <c>PGbrowser</c> namespace. Native calls run only when
/// <see cref="AssetAcquisitionOptIn.Granted"/>; otherwise the method returns a refused
/// handle and the loader is never asked to mount. Dispose unloads and unmounts only what
/// this process mounted. Never <c>RTC</c>, never <c>ReleaseObjectAsync</c> of a game id.
/// </summary>
public static class AssetAcquisition
{
    private static readonly object Gate = new();
    private static readonly List<AcquisitionHandle> Live = new();
    private static readonly AssetAcquisitionLedger SharedLedger = new();
    private static IContentArchiveLoader? _loader;
    private static int _nextId = 1;

    /// <summary>Shared PGbrowser refcounts. Tests inspect this; plugins do not.</summary>
    internal static AssetAcquisitionLedger Ledger => SharedLedger;

    /// <summary>Archives currently refcounted in PGbrowser (ours only).</summary>
    public static int LiveArchiveCount => SharedLedger.LiveArchives;

    /// <summary>Content-files currently refcounted in PGbrowser (ours only).</summary>
    public static int LiveFileCount => SharedLedger.LiveFiles;

    /// <summary>Test / Dev install of the archive loader. Production Dev ops install the Unity adapter.</summary>
    internal static void UseLoader(IContentArchiveLoader? loader)
    {
        lock (Gate) _loader = loader;
    }

#if !PROGENITOR_TESTS
    /// <summary>Wire the metadata-verified Unity adapter. Still refuses every native call until opt-in.</summary>
    public static void InstallLiveLoader()
    {
        UseLoader(UnityContentArchiveLoader.Instance);
    }
#endif

    internal static IContentArchiveLoader? CurrentLoader
    {
        get { lock (Gate) return _loader; }
    }

    /// <summary>
    /// Acquire <paramref name="request"/>. Refuses without native calls when the opt-in
    /// is unset, the budget is exceeded, or the identity is not the lantern (this cut).
    /// </summary>
    public static IAssetAcquisitionHandle Acquire(
        AssetAcquisitionRequest request,
        object? owner = null,
        string? pluginId = null)
    {
        EnsureMainThread(nameof(Acquire));

        string? refuse = AssetAcquisitionRequest.RefuseBeforeMount(request, out long knownBytes);
        if (refuse != null)
            return Track(AcquisitionHandle.Refused(request, refuse, knownBytes, owner, pluginId));

        if (!AssetAcquisitionOptIn.Granted)
            return Track(AcquisitionHandle.Refused(request, AssetAcquisitionReasons.OptInRequired, knownBytes, owner, pluginId));

        var loader = CurrentLoader;
        if (loader == null)
            return Track(AcquisitionHandle.Refused(request, AssetAcquisitionReasons.LoadError, knownBytes, owner, pluginId));
        if (!loader.IsClient)
            return Track(AcquisitionHandle.Refused(request, AssetAcquisitionReasons.ClientOnly, knownBytes, owner, pluginId));

        var handle = new AcquisitionHandle(request, knownBytes, owner, pluginId, WorldEpoch.Current);
        Track(handle);
        handle.RunAcquire(loader);
        return handle;
    }

    /// <summary>Read-only residency scan. Does not mount. Does not require the acquire opt-in.</summary>
    public static AssetResidencyReport ScanResidency(string name, string? archiveId = null)
    {
        EnsureMainThread(nameof(ScanResidency));
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Name is required.", nameof(name));

        var loader = CurrentLoader;
        var scan = loader?.ScanResidency(name.Trim(), archiveId) ?? new ContentResidencyScan();
        return new AssetResidencyReport(
            name.Trim(),
            archiveId,
            scan.ScanApi,
            scan.NameCount,
            scan.InstanceIds,
            scan.RtcDump ?? "",
            scan.RtcMentionsTarget,
            scan.PgMounted,
            scan.PgFiles,
            scan.IcallsResolved,
            CatalogAssetIdentity.CatalogSha256);
    }

    public static IAssetAcquisitionHandle? Find(string handleId)
    {
        if (string.IsNullOrWhiteSpace(handleId)) return null;
        lock (Gate)
        {
            for (int i = 0; i < Live.Count; i++)
            {
                if (string.Equals(Live[i].HandleId, handleId, StringComparison.Ordinal))
                    return Live[i];
            }
        }

        return null;
    }

    /// <summary>Dispose every handle recorded for <paramref name="owner"/>.</summary>
    public static int RemoveAll(object owner)
    {
        if (owner == null) throw new ArgumentNullException(nameof(owner));
        EnsureMainThread(nameof(RemoveAll));
        AcquisitionHandle[] snapshot;
        lock (Gate)
        {
            var list = new List<AcquisitionHandle>();
            for (int i = 0; i < Live.Count; i++)
            {
                if (ReferenceEquals(Live[i].Owner, owner))
                    list.Add(Live[i]);
            }

            snapshot = list.ToArray();
        }

        int n = 0;
        for (int i = 0; i < snapshot.Length; i++)
        {
            try
            {
                snapshot[i].Dispose();
                n++;
            }
            catch { /* release continues */ }
        }

        return n;
    }

    public static AssetPreviewResult Preview(IAssetAcquisitionHandle handle, bool hostMenu)
    {
        if (handle == null) throw new ArgumentNullException(nameof(handle));
        EnsureMainThread(nameof(Preview));
        if (handle is not AcquisitionHandle owned)
            return AssetPreviewResult.Fail(AssetAcquisitionReasons.StaleHandle);
        return owned.Preview(CurrentLoader, hostMenu);
    }

    internal static void ResetForTests()
    {
        AcquisitionHandle[] snapshot;
        lock (Gate)
        {
            snapshot = Live.ToArray();
            Live.Clear();
            _loader = null;
            _nextId = 1;
        }

        for (int i = 0; i < snapshot.Length; i++)
        {
            try { snapshot[i].Dispose(); } catch { /* */ }
        }

        AssetAcquisitionOptIn.ResetForTests();
    }

    static AcquisitionHandle Track(AcquisitionHandle handle)
    {
        lock (Gate) Live.Add(handle);
        return handle;
    }

    static void Forget(AcquisitionHandle handle)
    {
        lock (Gate) Live.Remove(handle);
    }

    static string NextHandleId()
    {
        int n = Interlocked.Increment(ref _nextId);
        return "acq-" + n.ToString(CultureInfo.InvariantCulture);
    }

    static void EnsureMainThread(string op)
    {
#if PROGENITOR_TESTS
        _ = op;
#else
        if (Progenitor.Dev.MainThread.IsMainThread) return;
        throw new InvalidOperationException(
            "AssetAcquisition." + op + " must run on the game main thread (Plugin.Frame, a command, or EventBus).");
#endif
    }

    internal sealed class AcquisitionHandle : IAssetAcquisitionHandle
    {
        private int _disposed;
        private readonly List<string> _ownedArchives = new();
        private readonly List<string> _ownedFiles = new();
        private readonly object _gate = new();
        private IDisposable? _preview;

        internal AcquisitionHandle(
            AssetAcquisitionRequest request,
            long knownBytes,
            object? owner,
            string? pluginId,
            int epoch)
        {
            HandleId = NextHandleId();
            ArchiveId = request.ArchiveId;
            PathId = request.PathId;
            Name = CatalogAssetIdentity.IsLantern(request.ArchiveId, request.PathId)
                ? CatalogAssetIdentity.LanternName
                : "";
            UnityType = CatalogAssetIdentity.LanternUnityType;
            Status = AssetAcquisitionStatus.Queued;
            BytesCompressed = knownBytes;
            TimeoutMs = request.TimeoutMs;
            Owner = owner;
            PluginId = string.IsNullOrWhiteSpace(pluginId) ? "unowned" : pluginId.Trim();
            Epoch = epoch;
        }

        internal static AcquisitionHandle Refused(
            AssetAcquisitionRequest request,
            string reason,
            long knownBytes,
            object? owner,
            string? pluginId)
        {
            var h = new AcquisitionHandle(request, knownBytes, owner, pluginId, WorldEpoch.Current)
            {
                Status = AssetAcquisitionStatus.Refused,
                Reason = reason,
            };
            return h;
        }

        public string HandleId { get; }
        public string ArchiveId { get; }
        public ulong PathId { get; }
        public string Name { get; private set; }
        public string UnityType { get; private set; }
        public AssetAcquisitionStatus Status { get; private set; }
        public string? Reason { get; private set; }
        public bool Visible { get; private set; }
        public bool IsDisposed => Volatile.Read(ref _disposed) != 0;
        public int OwnedFileCount { get { lock (_gate) return _ownedFiles.Count; } }
        public int OwnedArchiveCount { get { lock (_gate) return _ownedArchives.Count; } }
        public int ObjectCount { get; private set; }
        public int Epoch { get; }
        public bool IsStale => Epoch != WorldEpoch.Current;
        public long BytesCompressed { get; }
        public int TimeoutMs { get; }
        internal object? Owner { get; }
        internal string PluginId { get; }

        internal void RunAcquire(IContentArchiveLoader loader)
        {
            Status = AssetAcquisitionStatus.Loading;
            string[] order = CatalogAssetIdentity.IsLantern(ArchiveId, PathId)
                ? CatalogAssetIdentity.LanternClosureLoadOrder
                : new[] { ArchiveId };

            try
            {
                for (int i = 0; i < order.Length; i++)
                {
                    string id = order[i];
                    var mount = loader.Mount(id, diskPath: id);
                    if (!mount.Ok)
                    {
                        Fail(mount.Reason ?? AssetAcquisitionReasons.MountRefused, loader);
                        return;
                    }

                    NoteArchive(id);
                    if (!mount.Completed)
                    {
                        Status = AssetAcquisitionStatus.Loading;
                        return;
                    }
                }

                for (int i = 0; i < order.Length; i++)
                {
                    string id = order[i];
                    IReadOnlyList<string> deps = id == ArchiveId
                        ? CatalogAssetIdentity.LanternDirectDependencies
                        : Array.Empty<string>();
                    var load = loader.LoadFile(id, deps);
                    if (!load.Ok)
                    {
                        Fail(load.Reason ?? AssetAcquisitionReasons.MissingDependency, loader);
                        return;
                    }

                    NoteFile(id);
                    if (!load.Completed)
                    {
                        Status = AssetAcquisitionStatus.Loading;
                        return;
                    }
                }

                var got = loader.GetObject(ArchiveId, PathId);
                if (!got.Ok)
                {
                    Fail(got.Reason ?? AssetAcquisitionReasons.LoadError, loader);
                    return;
                }

                if (!string.IsNullOrEmpty(got.Name)) Name = got.Name!;
                if (!string.IsNullOrEmpty(got.UnityType)) UnityType = got.UnityType!;
                ObjectCount = got.ObjectCount;
                Status = AssetAcquisitionStatus.Ready;
            }
            catch (Exception)
            {
                Fail(AssetAcquisitionReasons.LoadError, loader);
            }
        }

        internal AssetPreviewResult Preview(IContentArchiveLoader? loader, bool hostMenu)
        {
            if (IsDisposed || IsStale)
                return AssetPreviewResult.Fail(AssetAcquisitionReasons.StaleHandle);
            if (Status != AssetAcquisitionStatus.Ready)
                return AssetPreviewResult.Fail(Reason ?? AssetAcquisitionReasons.LoadError);
            if (loader == null)
                return AssetPreviewResult.Fail(AssetAcquisitionReasons.LoadError);

            DisposePreview();
            ContentInstantiateResult result;
            try { result = loader.InstantiatePreview(ArchiveId, PathId, PluginId, hostMenu); }
            catch { return AssetPreviewResult.Fail(AssetAcquisitionReasons.LoadError); }
            _preview = result.Ownership;
            if (!result.Ok)
            {
                DisposePreview();
                return AssetPreviewResult.Fail(result.Reason ?? AssetAcquisitionReasons.LoadError);
            }
            if (_preview == null)
                return AssetPreviewResult.Fail(AssetAcquisitionReasons.LoadError);

            Visible = true;
            return new AssetPreviewResult(true, result.Hosted, result.CloneName, result.ParticlePlaying, result.Unsupported);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            var loader = CurrentLoader;
            Unwind(loader);
            Forget(this);
            Status = Status == AssetAcquisitionStatus.Refused ? Status : AssetAcquisitionStatus.Ready;
            Visible = false;
        }

        void Fail(string reason, IContentArchiveLoader loader)
        {
            Reason = reason;
            Status = AssetAcquisitionStatus.Error;
            Unwind(loader);
        }

        void NoteArchive(string id)
        {
            lock (_gate) _ownedArchives.Add(id);
            SharedLedger.AddArchive(id);
        }

        void NoteFile(string id)
        {
            lock (_gate) _ownedFiles.Add(id);
            SharedLedger.AddFile(id);
        }

        void DisposePreview()
        {
            var preview = _preview;
            _preview = null;
            Visible = false;
            try { preview?.Dispose(); } catch { /* continue releasing owned resources */ }
        }

        void Unwind(IContentArchiveLoader? loader)
        {
            string[] files;
            string[] archives;
            lock (_gate)
            {
                files = _ownedFiles.ToArray();
                archives = _ownedArchives.ToArray();
            }

            DisposePreview();

            for (int i = files.Length - 1; i >= 0; i--)
            {
                string id = files[i];
                if (SharedLedger.ReleaseFile(id) && loader != null)
                {
                    try { loader.UnloadFile(id); } catch { /* continue */ }
                }
            }

            for (int i = archives.Length - 1; i >= 0; i--)
            {
                string id = archives[i];
                if (SharedLedger.ReleaseArchive(id) && loader != null)
                {
                    try { loader.Unmount(id); } catch { /* continue */ }
                }
            }

            lock (_gate)
            {
                _ownedFiles.Clear();
                _ownedArchives.Clear();
            }
        }
    }
}

/// <summary>Read-only residency snapshot. Abort the lantern identity when <see cref="NameCount"/> &gt; 0 or <see cref="RtcMentionsTarget"/>.</summary>
public sealed class AssetResidencyReport
{
    public AssetResidencyReport(
        string name,
        string? archiveId,
        string scanApi,
        int nameCount,
        IReadOnlyList<int> instanceIds,
        string rtcDump,
        bool rtcMentionsTarget,
        int? pgMounted,
        int? pgFiles,
        bool icallsResolved,
        string catalogSha256)
    {
        Name = name;
        ArchiveId = archiveId;
        ScanApi = scanApi;
        NameCount = nameCount;
        InstanceIds = instanceIds;
        RtcDump = rtcDump;
        RtcMentionsTarget = rtcMentionsTarget;
        PgMounted = pgMounted;
        PgFiles = pgFiles;
        IcallsResolved = icallsResolved;
        CatalogSha256 = catalogSha256;
    }

    public string Name { get; }
    public string? ArchiveId { get; }
    public string ScanApi { get; }
    public int NameCount { get; }
    public IReadOnlyList<int> InstanceIds { get; }
    public string RtcDump { get; }
    public bool RtcMentionsTarget { get; }
    public int? PgMounted { get; }
    public int? PgFiles { get; }
    public bool IcallsResolved { get; }
    public string CatalogSha256 { get; }

    /// <summary>True when a baseline scan already sees this name or RTC holds the archive.</summary>
    public bool AbortIdentity => NameCount > 0 || RtcMentionsTarget;
}

/// <summary>Preview outcome. <see cref="Unsupported"/> names a missing Effects seam when Host was refused.</summary>
public readonly struct AssetPreviewResult
{
    public AssetPreviewResult(bool ok, bool hosted, string? cloneName, bool? particlePlaying, string? unsupported)
    {
        Ok = ok;
        Hosted = hosted;
        CloneName = cloneName;
        ParticlePlaying = particlePlaying;
        Unsupported = unsupported;
        Reason = ok ? null : unsupported;
    }

    public bool Ok { get; }
    public bool Hosted { get; }
    public string? CloneName { get; }
    public bool? ParticlePlaying { get; }
    public string? Unsupported { get; }
    public string? Reason { get; }

    public static AssetPreviewResult Fail(string reason) =>
        new(false, false, null, null, reason);
}
