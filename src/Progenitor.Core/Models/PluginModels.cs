#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;
using Progenitor.Plugins;
using UnityEngine;
using UnityObject = UnityEngine.Object;

namespace Progenitor.Models;

/// <summary>
/// <see cref="PluginContext.Models"/>: loads the game's presentation prefabs (hybrid character
/// models such as <c>HYB_NPCDracula_Evolved</c>) that are not loaded right now, keeps them loaded
/// while held, and instantiates owned copies. <b>Client only</b>; on the server every load fails
/// at once with <see cref="ModelLoadReasons.ServerSide"/>.
///
/// <code>
/// ctx.Models.Catalog = ModelCatalog.Load(path);          // from tools/catalog/model-sections.mjs
/// var dracula = ctx.Models.Load("HYB_NPCDracula_Evolved");
/// // later, once dracula.IsReady:
/// GameObject copy = dracula.Instantiate("FilmDracula");
/// </code>
///
/// Every handle is tracked: unload (every reason) destroys this plugin's copies and releases its
/// holds. See <c>docs/modules.md</c>, "Models".
/// </summary>
public sealed class PluginModels
{
    private readonly PluginContext _ctx;

    internal PluginModels(PluginContext ctx)
    {
        _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
    }

    /// <summary>Name table for <see cref="Load(string)"/>. Empty until the plugin sets it.</summary>
    public ModelCatalog Catalog { get; set; } = ModelCatalog.Empty;

    /// <summary>Captures complete selected rig meshes for offline authoring. Client/main thread only; no game state is changed.</summary>
    public RigReferenceSnapshot CaptureRigReference(Transform modelRoot, params SkinnedMeshRenderer[] renderers)
    {
        _ctx.EnsureAttached();
        return RigReference.Capture(modelRoot, renderers);
    }

    /// <summary>Loads <paramref name="prefabName"/> from the section <see cref="Catalog"/> names.</summary>
    public ModelHandle Load(string prefabName)
    {
        _ctx.EnsureAttached();
        if (!Catalog.TryFind(prefabName, out var section))
            throw new KeyNotFoundException("'" + prefabName + "' is not in ctx.Models.Catalog (" + Catalog.Count + " models). Regenerate the table with tools/catalog/model-sections.mjs, or pass the section.");
        return Load(section, prefabName);
    }

    /// <summary>
    /// Hides, on this client only, the game's rendered scenery inside the world box
    /// <paramref name="min"/>..<paramref name="max"/> whose mesh name starts with one of
    /// <paramref name="meshPrefixes"/> (for example <c>Church03_Podium</c>, <c>Church_Candleholder</c>).
    /// Adds <c>DisableRendering</c>; baked static-scene pieces ignore that and are counted in
    /// <see cref="SceneryHide.SkippedStatic"/>. Re-applied every second; dispose (or unload) shows
    /// them again.
    /// </summary>
    public SceneryHide HideScenery(Vector3 min, Vector3 max, params string[] meshPrefixes)
    {
        _ctx.EnsureAttached();
        if (!_ctx.IsClient) throw new InvalidOperationException("ctx.Models.HideScenery is client-only.");
        if (meshPrefixes == null || meshPrefixes.Length == 0) throw new ArgumentException("at least one mesh name prefix is required.", nameof(meshPrefixes));
        var hide = new SceneryHide(Vector3.Min(min, max), Vector3.Max(min, max), meshPrefixes, units: false);
        SceneryFrame.Add(hide);
        _ctx.Track(hide);
        return hide;
    }

    /// <summary>
    /// Hides, on this client only, every unit model under <c>HybridModels</c> whose name starts with
    /// <paramref name="modelPrefix"/> (for example <c>HYB_NPCNecromancer_MilitiaBishop</c> for
    /// Raziel), by switching its renderers off; a respawned model is caught by the next sweep. The
    /// unit still exists on the server. Dispose (or unload) shows it again.
    /// </summary>
    public SceneryHide HideUnitModels(string modelPrefix)
    {
        _ctx.EnsureAttached();
        if (!_ctx.IsClient) throw new InvalidOperationException("ctx.Models.HideUnitModels is client-only.");
        if (string.IsNullOrEmpty(modelPrefix)) throw new ArgumentException("a model name prefix is required.", nameof(modelPrefix));
        var hide = new SceneryHide(Vector3.zero, Vector3.zero, new[] { modelPrefix }, units: true);
        SceneryFrame.Add(hide);
        _ctx.Track(hide);
        return hide;
    }

    /// <summary>
    /// Under development and incomplete: experimental client-visual import, not a complete gameplay asset pipeline.
    /// Imports a binary glTF (<c>.glb</c>) file as new GameObjects under this plugin's
    /// <c>progenitor-models-&lt;plugin&gt;</c> root, at the origin, active. See <see cref="GltfScene"/>
    /// for what is supported. Client only; throws on the server and on a file it cannot read. The
    /// import is tracked: unload destroys it with its meshes, materials and textures.
    /// </summary>
    public ImportedModel ImportGlb(string path, string? name = null)
    {
        _ctx.EnsureAttached();
        if (!_ctx.IsClient) throw new InvalidOperationException("ctx.Models.ImportGlb is client-only.");
        var scene = GltfScene.LoadGlb(path);
        return BuildImported(scene, name ?? System.IO.Path.GetFileNameWithoutExtension(path));
    }

    /// <summary>Under development and incomplete: validates a static or animated model package and imports its client visual.
    /// Experimental; does not create gameplay entities or provide a complete third-party asset pipeline.</summary>
    public ImportedModel ImportPackage(string directory, string? name = null)
    {
        _ctx.EnsureAttached();
        if (!_ctx.IsClient) throw new InvalidOperationException("ctx.Models.ImportPackage is client-only.");
        return BuildImported(ModelPackage.Load(directory), name ?? System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(directory)));
    }

    private ImportedModel BuildImported(GltfScene scene, string name)
    {
        var created = new List<UnityEngine.Object>();
        GameObject root;
        Transform[] nodes;
        ImportedModel model;
        try
        {
            root = GltfBuilder.Build(scene, name, created, out nodes);
            created.Add(root);
            root.transform.SetParent(ModelInstances.Root(_ctx.Name).transform, false);
            model = new ImportedModel(root, created, scene, nodes);
            created.Remove(root); // Constructor succeeded; ImportedModel now owns root separately.
        }
        catch
        {
            foreach (var o in created) try { UnityEngine.Object.Destroy(o); } catch { }
            throw;
        }
        _ctx.Track(model);
        return model;
    }

    /// <summary>
    /// A loaded material asset by exact name, or null. Equipment prefabs ship with empty material
    /// slots (the game fills them when a character equips the item); the materials themselves
    /// arrive with the equipment's section, so after a Load they can be found here, for example
    /// <c>VampireMale_ArmorSet04_Dracula</c> for the Dracula armor set.
    /// </summary>
    public Material? FindLoadedMaterial(string name)
    {
        _ctx.EnsureAttached();
        if (!_ctx.IsClient) return null;
        var all = Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<Material>());
        for (int i = 0; i < all.Length; i++)
        {
            var m = all[i]?.TryCast<Material>();
            if (m != null && string.Equals(m.name, name, StringComparison.Ordinal)) return m;
        }
        return null;
    }

    /// <summary>
    /// A prefab the game keeps loaded (the player models <c>HYB_VampireFemale_Prefab</c> and
    /// <c>HYB_VampireMale_Prefab</c>), found by name with no section to hold. Fails with
    /// <see cref="ModelLoadReasons.PrefabNotLoaded"/> if no loaded prefab has that name.
    /// </summary>
    public ModelHandle LoadResident(string prefabName) => Load(ModelSection.None, prefabName);

    /// <summary>
    /// Starts streaming <paramref name="section"/> in and finding <paramref name="prefabName"/> in
    /// it. Returns at once; the handle becomes Ready on a later frame (typically a few frames; a
    /// cold section can take a second or more) or Failed with a <see cref="ModelLoadReasons"/> code.
    /// </summary>
    public ModelHandle Load(ModelSection section, string prefabName)
    {
        _ctx.EnsureAttached();
        var handle = new ModelHandle(section, prefabName, _ctx.Name);
        if (_ctx.IsClient) handle.Start();
        else handle.FailServerSide();
        _ctx.Track(handle);
        return handle;
    }
}

/// <summary>
/// One plugin's hold on a presentation prefab plus the copies made from it. Dispose destroys the
/// copies and ends the hold; the section may then unload (see <see cref="ModelReleaseResult"/>).
/// </summary>
public sealed class ModelHandle : IDisposable
{
    private readonly string _owner;
    private readonly List<GameObject> _instances = new();
    private readonly List<ModelPreview> _previews = new();
    private ModelLoad? _load;
    private string? _serverReason;

    internal ModelHandle(ModelSection section, string prefabName, string owner)
    {
        Section = section;
        PrefabName = prefabName;
        _owner = owner;
    }

    public ModelSection Section { get; }
    public string PrefabName { get; }

    public ModelLoadState State => _serverReason != null ? ModelLoadState.Failed : _load?.State ?? ModelLoadState.Released;

    /// <summary>Why it failed (<see cref="ModelLoadReasons"/>), or <see cref="ModelLoadReasons.Reloading"/> while it recovers.</summary>
    public string? Reason => _serverReason ?? _load?.Reason;

    public bool IsReady => State == ModelLoadState.Ready;

    /// <summary>The loaded prefab asset while Ready. Do not modify it: the game instantiates it too.</summary>
    public GameObject? Prefab => IsReady ? _load!.Prefab as GameObject : null;

    /// <summary>Copies made by <see cref="Instantiate"/> that still exist.</summary>
    public int InstanceCount
    {
        get
        {
            _instances.RemoveAll(go => go == null);
            return _instances.Count;
        }
    }

    internal void Start() => _load = ModelFrame.Acquire(Section, PrefabName);

    internal void FailServerSide() => _serverReason = ModelLoadReasons.ServerSide;

    /// <summary>
    /// An owned, inert copy of the prefab under this plugin's <c>progenitor-models-&lt;plugin&gt;</c>
    /// root, active, at the origin. Inert means the game's own drivers are switched off on the copy:
    /// <c>Animator</c>, the FinalIK solvers, every <c>Hybrid*</c> component, Magica cloth, footstep
    /// and secondary-animation receivers. Bones then hold the prefab's own pose until you move them,
    /// and nothing tries to bind the copy to a gameplay entity. Skinned renderers are set to update
    /// off screen so a posed copy is never culled by its bind-pose bounds.
    /// Throws unless <see cref="IsReady"/>.
    /// </summary>
    public GameObject Instantiate(string? name = null)
    {
        var prefab = Prefab ?? throw new InvalidOperationException("model '" + PrefabName + "' is " + State + (Reason != null ? " (" + Reason + ")" : "") + ", not Ready.");
        var copy = UnityObject.Instantiate(prefab);
        copy.name = string.IsNullOrEmpty(name) ? PrefabName : name;
        ModelInstances.MakeInert(copy);
        copy.transform.SetParent(ModelInstances.Root(_owner).transform, false);
        copy.SetActive(true);
        _instances.Add(copy);
        return copy;
    }

    /// <summary>
    /// <see cref="Instantiate"/> wrapped as a <see cref="Puppet"/>: bones by name, the prefab's pose
    /// as the rest pose. This handle still owns the GameObject.
    /// </summary>
    public Puppet InstantiatePuppet(string? name = null) => Puppet.Of(Instantiate(name));

    /// <summary>Shows a static, camera-framed copy with its original materials. Client/main thread only, Ready required.
    /// Dispose the returned preview to close it. Disposing this model also closes all its previews.</summary>
    public ModelPreview Preview()
    {
        var prefab = Prefab ?? throw new InvalidOperationException("model must be Ready before previewing");
        var preview = new ModelPreview(Progenitor.Effects.UnityModelPreview.Create(prefab));
        _previews.RemoveAll(p => !p.IsOpen);
        _previews.Add(preview);
        return preview;
    }

    /// <summary>Destroys the copies and ends the hold. Idempotent.</summary>
    public void Dispose()
    {
        foreach (var preview in _previews) preview.Dispose();
        _previews.Clear();
        foreach (var go in _instances)
        {
            try { if (go != null) UnityObject.Destroy(go); }
            catch (Exception e) { Plugin.Logger?.LogWarning("Models: destroy copy threw: " + e.Message); }
        }
        _instances.Clear();
        if (_load != null)
        {
            var result = ModelFrame.Release(_load);
            if (result == ModelReleaseResult.KeptForGame)
                Plugin.Logger?.LogInfo("Models: " + PrefabName + " released; a game unit uses it, so its section stays requested.");
            _load = null;
        }
    }
}

/// <summary>Owned copies: roots, inerting, and the Core-unload sweep.</summary>
internal static class ModelInstances
{
    internal const string Prefix = "progenitor-models-";

    internal static GameObject Root(string owner)
    {
        string name = Prefix + Sanitize(owner);
        var root = GameObject.Find(name);
        if (root != null) return root;
        root = new GameObject(name);
        UnityObject.DontDestroyOnLoad(root);
        return root;
    }

    internal static void MakeInert(GameObject copy)
    {
        foreach (var behaviour in copy.GetComponentsInChildren<Behaviour>(true))
        {
            if (behaviour == null) continue;
            string typeName;
            try { typeName = behaviour.GetIl2CppType().Name; } catch { continue; }
            if (ModelInertPolicy.IsInert(typeName)) behaviour.enabled = false;
        }
        foreach (var skinned in copy.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            skinned.updateWhenOffscreen = true;
    }

    /// <summary>Core unload: every plugin's copies go.</summary>
    internal static void DestroyAllRoots()
    {
        var doomed = new List<GameObject>();
        var all = Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<GameObject>());
        for (int i = 0; i < all.Length; i++)
        {
            var go = all[i]?.TryCast<GameObject>();
            if (go != null && go.transform.parent == null && go.scene.IsValid() && go.name.StartsWith(Prefix, StringComparison.Ordinal))
                doomed.Add(go);
        }
        foreach (var go in doomed) UnityObject.DestroyImmediate(go);
    }

    private static string Sanitize(string? owner)
    {
        if (string.IsNullOrWhiteSpace(owner)) return "unowned";
        var chars = owner!.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
            if (chars[i] is '/' or '\\') chars[i] = '_';
        return new string(chars);
    }
}

/// <summary>
/// The one <see cref="ModelLoader"/> in this Core generation and its <see cref="Plugin.Frame"/>
/// subscription, armed while anything is held.
/// </summary>
internal static class ModelFrame
{
    private static readonly ModelLoader Loader = new(UnityModelStreamingRuntime.Instance);
    private static bool _armed;

    internal static ModelLoad Acquire(ModelSection section, string prefabName)
    {
        var load = Loader.Acquire(section, prefabName);
        Arm();
        return load;
    }

    internal static ModelReleaseResult Release(ModelLoad load) => Loader.Release(load);

    private static void Arm()
    {
        if (_armed) return;
        Plugin.Frame += OnFrame;
        _armed = true;
    }

    private static void Disarm()
    {
        if (!_armed) return;
        try { Plugin.Frame -= OnFrame; } catch { /* Frame may already be null on uninstall */ }
        _armed = false;
    }

    private static void OnFrame()
    {
        bool keep;
        try { keep = Loader.Tick(); }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Models: frame tick threw: " + e.Message);
            keep = Loader.LiveLoads > 0;
        }
        if (!keep) Disarm();
    }

    /// <summary>Core uninstall: copies destroyed, holds released, requests this generation added removed.</summary>
    internal static void Uninstall()
    {
        ModelPreviewBridge.Close();
        Progenitor.Effects.UnityModelPreview.DisposeAll();
        try { SceneryFrame.Uninstall(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Models: showing scenery on uninstall threw: " + e.Message); }
        try { ModelInstances.DestroyAllRoots(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Models: destroying copies on uninstall threw: " + e.Message); }
        try { Loader.ReleaseAll(ModelLoadReasons.CoreUnloaded); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Models: release on uninstall threw: " + e.Message); }
        Disarm();
    }
}
#endif
