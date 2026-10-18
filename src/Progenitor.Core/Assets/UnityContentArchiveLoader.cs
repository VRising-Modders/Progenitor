#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Unity.Content;
using Unity.Entities.Content;
using Unity.IO.Archive;
using Unity.Jobs;
using Unity.Loading;
using UnityEngine;
using UnityObject = UnityEngine.Object;

namespace Progenitor.Effects;

/// <summary>
/// Live adapter for <see cref="IContentArchiveLoader"/>. Every
/// <c>ContentNamespace</c> / <c>ArchiveFileInterface</c> / <c>ContentLoadInterface</c>
/// call is gated on <see cref="AssetAcquisitionOptIn.Granted"/>. Signatures are
/// verified against the game's metadata; icalls are probed (no invoke) when the delegate field is null.
///
/// Pointer-form <c>LoadContentFileAsync(ns, filename, void*, n, fence, useUnsafe)</c>
/// (interop <c>NativeMethodInfoPtr_LoadContentFileAsync_Internal_Static_…</c>).
/// Do not construct <c>NativeArray&lt;ContentFile&gt;</c>.
/// </summary>
internal sealed unsafe class UnityContentArchiveLoader : IContentArchiveLoader
{
    internal static readonly UnityContentArchiveLoader Instance = new();

    // ContentNamespace { ulong Id }
    ContentNamespace _ns;
    bool _nsCreated;

    readonly Dictionary<string, ArchiveHandle> _archives = new(StringComparer.Ordinal);
    readonly Dictionary<string, ContentFile> _files = new(StringComparer.Ordinal);

    public bool IsClient => !Progenitor.Plugin.IsServerProcess;

    public ContentLoadStepResult Mount(string archiveId, string diskPath)
    {
        if (!EnsureOptIn(out var refuse)) return ContentLoadStepResult.Fail(refuse);
        if (!EnsureNamespace(out refuse)) return ContentLoadStepResult.Fail(refuse);
        if (_archives.ContainsKey(archiveId)) return ContentLoadStepResult.Done();

        string path = ResolveDiskPath(archiveId, diskPath);
        if (!File.Exists(path))
            return ContentLoadStepResult.Fail(AssetAcquisitionReasons.MissingDependency);

        // NativeMethodInfoPtr_MountAsync_Public_Static_ArchiveHandle_ContentNamespace_String_String_0
        ArchiveHandle arc = ArchiveFileInterface.MountAsync(_ns, path, CatalogAssetIdentity.MountPrefix);
        // Archive_IsValid
        if (!ArchiveFileInterface.Archive_IsValid(arc))
            return ContentLoadStepResult.Fail(AssetAcquisitionReasons.MountRefused);

        // JobHandle; IsCompleted. Do not JobHandle.Complete() (spins).
        JobHandle job = arc.JobHandle;
        if (!job.IsCompleted)
            JobHandle.ScheduleBatchedJobs();
        if (!arc.JobHandle.IsCompleted)
        {
            _archives[archiveId] = arc;
            return ContentLoadStepResult.Pending();
        }

        _archives[archiveId] = arc;
        return ContentLoadStepResult.Done();
    }

    public ContentLoadStepResult LoadFile(string archiveId, IReadOnlyList<string> dependencyArchiveIds)
    {
        if (!EnsureOptIn(out var refuse)) return ContentLoadStepResult.Fail(refuse);
        if (!EnsureNamespace(out refuse)) return ContentLoadStepResult.Fail(refuse);
        if (_files.ContainsKey(archiveId)) return ContentLoadStepResult.Done();

        string filename = CatalogAssetIdentity.MountedFileName(archiveId);
        int n = dependencyArchiveIds?.Count ?? 0;
        ContentFile file;
        if (n == 0)
        {
            // Pointer form, n=0, null deps (custom-sequences.md §0.4)
            file = ContentLoadInterface.LoadContentFileAsync(_ns, filename, null, 0, default, useUnsafe: false);
        }
        else
        {
            ContentFile* buf = stackalloc ContentFile[n];
            for (int i = 0; i < n; i++)
            {
                string dep = dependencyArchiveIds![i];
                if (!_files.TryGetValue(dep, out ContentFile depFile))
                    return ContentLoadStepResult.Fail(AssetAcquisitionReasons.MissingDependency);
                buf[i] = depFile;
            }

            file = ContentLoadInterface.LoadContentFileAsync(_ns, filename, buf, n, default, useUnsafe: false);
        }

        // IsValid, LoadingStatus, WaitForCompletion
        if (!file.IsValid)
            return ContentLoadStepResult.Fail(AssetAcquisitionReasons.LoadError);

        bool completed = file.WaitForCompletion(10000);
        if (!completed || file.LoadingStatus == LoadingStatus.Failed)
            return ContentLoadStepResult.Fail(completed ? AssetAcquisitionReasons.LoadError : AssetAcquisitionReasons.Timeout);
        if (file.LoadingStatus == LoadingStatus.InProgress)
        {
            _files[archiveId] = file;
            return ContentLoadStepResult.Pending();
        }

        _files[archiveId] = file;
        return ContentLoadStepResult.Done();
    }

    public ContentGetObjectResult GetObject(string archiveId, ulong pathId)
    {
        if (!EnsureOptIn(out var refuse)) return ContentGetObjectResult.Fail(refuse);
        if (!_files.TryGetValue(archiveId, out ContentFile file))
            return ContentGetObjectResult.Fail(AssetAcquisitionReasons.LoadError);

        int count = 0;
        // GetObjects -> Object[]
        Il2CppReferenceArray<UnityObject>? all = file.GetObjects();
        if (all != null) count = all.Length;

        // GetObject(ulong localIdentifierInFile) — not generic
        UnityObject? obj = file.GetObject(pathId);
        if (obj == null)
            return ContentGetObjectResult.Fail(AssetAcquisitionReasons.LoadError);

        string name;
        string typeName = "Object";
        try
        {
            name = obj.name ?? "";
            if (obj.TryCast<GameObject>() != null) typeName = "GameObject";
        }
        catch (Exception)
        {
            return ContentGetObjectResult.Fail(AssetAcquisitionReasons.LoadError);
        }

        if (CatalogAssetIdentity.IsLantern(archiveId, pathId)
            && !string.Equals(name, CatalogAssetIdentity.LanternName, StringComparison.Ordinal))
            return ContentGetObjectResult.Fail(AssetAcquisitionReasons.LoadError);

        return new ContentGetObjectResult(true, null, name, typeName, count);
    }

    public ContentUnloadStepResult UnloadFile(string archiveId)
    {
        if (!_files.TryGetValue(archiveId, out ContentFile file))
            return ContentUnloadStepResult.Done(waitVerified: true);
        _files.Remove(archiveId);

        if (!AssetAcquisitionOptIn.Granted)
            return ContentUnloadStepResult.Fail(AssetAcquisitionReasons.OptInRequired);

        // UnloadAsync -> ContentFileUnloadHandle
        file.UnloadAsync();

        // WaitForUnloadCompletion is an icall, absent from the game's metadata. Probe, do not guess ABI.
        if (!IcallResolved(typeof(ContentLoadInterface), "WaitForUnloadCompletion_InjectedDelegateField"))
            return ContentUnloadStepResult.Done(waitVerified: false);

        bool waited = ContentLoadInterface.WaitForUnloadCompletion(file, 10000);
        return waited
            ? ContentUnloadStepResult.Done(waitVerified: true)
            : ContentUnloadStepResult.Fail(AssetAcquisitionReasons.Timeout);
    }

    public ContentUnloadStepResult Unmount(string archiveId)
    {
        if (!_archives.TryGetValue(archiveId, out ArchiveHandle arc))
            return ContentUnloadStepResult.Done(waitVerified: true);
        _archives.Remove(archiveId);
        if (!AssetAcquisitionOptIn.Granted)
            return ContentUnloadStepResult.Fail(AssetAcquisitionReasons.OptInRequired);

        // Unmount -> JobHandle. Do not Delete/RemoveNamespace (failed unstrip / never ours).
        JobHandle job = arc.Unmount();
        if (!job.IsCompleted)
            JobHandle.ScheduleBatchedJobs();
        return ContentUnloadStepResult.Done(waitVerified: job.IsCompleted);
    }

    public ContentResidencyScan ScanResidency(string name, string? archiveId)
    {
        var scan = new ContentResidencyScan { ScanApi = "FindObjectsOfTypeAll" };
        var ids = new List<int>();

        // Resources.FindObjectsOfTypeAll(Type). Do NOT use the generic (it has no native address).
        try
        {
            var type = Il2CppType.Of<GameObject>();
            var arr = Resources.FindObjectsOfTypeAll(type);
            if (arr != null)
            {
                for (int i = 0; i < arr.Length; i++)
                {
                    GameObject? go = null;
                    try { go = arr[i] == null ? null : arr[i].TryCast<GameObject>(); }
                    catch { continue; }
                    if (go == null) continue;
                    string n;
                    try { n = go.name ?? ""; }
                    catch { continue; }
                    if (!string.Equals(n, name, StringComparison.Ordinal)) continue;
                    scan.NameCount++;
                    try { ids.Add(go.GetInstanceID()); } catch { /* */ }
                }
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Assets: FindObjectsOfTypeAll<GameObject>: " + e.Message);
        }

        scan.InstanceIds = ids;

        // GetLoadedArchivesStateDump. RTC namespace only.
        try { scan.RtcDump = RuntimeContentManager.GetLoadedArchivesStateDump() ?? ""; }
        catch (Exception e)
        {
            scan.RtcDump = "";
            Plugin.Logger?.LogWarning("Assets: GetLoadedArchivesStateDump: " + e.Message);
        }

        if (!string.IsNullOrEmpty(archiveId) && scan.RtcDump.IndexOf(archiveId, StringComparison.OrdinalIgnoreCase) >= 0)
            scan.RtcMentionsTarget = true;

        bool mountedIcall = IcallResolved(typeof(ArchiveFileInterface), "GetMountedArchives_InjectedDelegateField");
        bool filesIcall = IcallResolved(typeof(ContentLoadInterface), "GetContentFiles_InjectedDelegateField");
        scan.IcallsResolved = mountedIcall && filesIcall;

        // Do not GetOrCreateNamespace here: creating PGbrowser is an acquisition call.
        if (scan.IcallsResolved && _nsCreated && AssetAcquisitionOptIn.Granted)
        {
            try
            {
                var mounted = ArchiveFileInterface.GetMountedArchives(_ns);
                scan.PgMounted = mounted == null ? 0 : mounted.Length;
            }
            catch { scan.PgMounted = null; }

            try
            {
                var files = ContentLoadInterface.GetContentFiles(_ns);
                scan.PgFiles = files == null ? 0 : files.Length;
            }
            catch { scan.PgFiles = null; }
        }

        return scan;
    }

    public ContentInstantiateResult InstantiatePreview(string archiveId, ulong pathId, string pluginId, bool hostMenu)
    {
        if (!EnsureOptIn(out var refuse)) return ContentInstantiateResult.Fail(refuse);
        if (!_files.TryGetValue(archiveId, out ContentFile file))
            return ContentInstantiateResult.Fail(AssetAcquisitionReasons.LoadError);

        // Resolve this acquisition's identity now, never the most recent GetObject call.
        UnityObject? target = file.GetObject(pathId);
        if (target == null)
            return ContentInstantiateResult.Fail(AssetAcquisitionReasons.LoadError);
        GameObject? source = target.TryCast<GameObject>();
        if (source == null)
            return ContentInstantiateResult.Fail(AssetAcquisitionReasons.UnsupportedClass);
        if (CatalogAssetIdentity.IsLantern(archiveId, pathId)
            && !string.Equals(source.name, CatalogAssetIdentity.LanternName, StringComparison.Ordinal))
            return ContentInstantiateResult.Fail(AssetAcquisitionReasons.LoadError);

        if (hostMenu && source.GetComponent<ParticleSystem>() == null
            && source.GetComponentInChildren<ParticleSystem>() == null)
        {
            var model = UnityModelPreview.Create(source);
            return new ContentInstantiateResult(true, null, true, model.CloneName, null, null, model);
        }

        GameObject? clone;
        try
        {
            var obj = UnityObject.Instantiate(source);
            clone = obj == null ? null : obj.TryCast<GameObject>() ?? obj as GameObject;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Assets: Instantiate: " + e.Message);
            return ContentInstantiateResult.Fail(AssetAcquisitionReasons.LoadError);
        }

        if (clone == null) return ContentInstantiateResult.Fail(AssetAcquisitionReasons.LoadError);

        IDisposable ownership = new PreviewClone(clone);
        try
        {
            string rootName = EffectOwnership.RootName(pluginId);
            try
            {
                var root = GameObject.Find(rootName);
                if (root == null)
                {
                    root = new GameObject(rootName);
                    UnityObject.DontDestroyOnLoad(root);
                }

                clone.transform.SetParent(root.transform, false);
                clone.SetActive(true);
            }
            catch (Exception e)
            {
                Plugin.Logger?.LogWarning("Assets: parent clone: " + e.Message);
            }

            bool? playing = null;
            ParticleSystem? ps = null;
            try
            {
                ps = clone.GetComponent<ParticleSystem>() ?? clone.GetComponentInChildren<ParticleSystem>();
                if (ps != null)
                {
                    try
                    {
                        var main = ps.main;
                        main.simulationSpace = ParticleSystemSimulationSpace.Local;
                    }
                    catch { /* */ }
                    ps.Play(true);
                    playing = ps.isPlaying;
                }
            }
            catch { /* */ }

            bool hosted = false;
            string? unsupported = null;
            if (ps != null)
            {
                // ParticleHandle.Dispose releases its host first and then its owned clone.
                var wrapped = UnityBorrowedObject.ForParticle(clone.name, clone, ps);
                var handle = new ParticleHandle(clone.name, pluginId, rootName, owner: null, wrapped, enabled: true);
                ownership = handle;
                if (hostMenu)
                {
                    var host = Assets.Host(handle, HostKind.RenderTexture);
                    hosted = host != null && host.Enabled;
                    if (!hosted)
                        unsupported = "Assets.Host(RenderTexture) returned a disabled handle";
                }
            }
            else if (hostMenu)
            {
                unsupported = "preview Host needs a ParticleSystem on the clone";
            }

            return new ContentInstantiateResult(true, null, hosted, clone.name, playing, unsupported, ownership);
        }
        catch
        {
            ownership.Dispose();
            throw;
        }
    }

    private sealed class PreviewClone : IDisposable
    {
        private GameObject? _clone;

        internal PreviewClone(GameObject clone) => _clone = clone;

        public void Dispose()
        {
            var clone = _clone;
            _clone = null;
            // Only the instantiated hierarchy belongs to us; meshes/materials remain shared.
            if (clone != null) UnityObject.DestroyImmediate(clone);
        }
    }

    static bool EnsureOptIn(out string reason)
    {
        if (AssetAcquisitionOptIn.Granted)
        {
            reason = "";
            return true;
        }

        reason = AssetAcquisitionReasons.OptInRequired;
        return false;
    }

    bool EnsureNamespace(out string reason)
    {
        reason = "";
        if (_nsCreated) return true;
        if (!CatalogAssetIdentity.IsValidNamespaceName(CatalogAssetIdentity.NamespaceName))
        {
            reason = AssetAcquisitionReasons.NamespaceRejected;
            return false;
        }

        try
        {
            // NativeMethodInfoPtr_GetOrCreateNamespace_Public_Static_ContentNamespace_String_0
            // Never ContentNamespace.Delete (failed unstrip). Never RemoveNamespace.
            _ns = ContentNamespace.GetOrCreateNamespace(CatalogAssetIdentity.NamespaceName);
            _nsCreated = true;
            return true;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("Assets: GetOrCreateNamespace(PGbrowser): " + e.Message);
            reason = AssetAcquisitionReasons.NamespaceRejected;
            return false;
        }
    }

    static string ResolveDiskPath(string archiveId, string diskPath)
    {
        if (!string.IsNullOrEmpty(diskPath) && File.Exists(diskPath)) return diskPath;
        string data = Application.dataPath ?? "";
        return Path.Combine(data, "StreamingAssets", CatalogAssetIdentity.ContentArchiveDirectory, archiveId);
    }

    static bool IcallResolved(Type type, string fieldName)
    {
        try
        {
            var field = type.GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
            if (field == null) return false;
            object? value = field.GetValue(null);
            return value != null;
        }
        catch
        {
            return false;
        }
    }
}
#endif
