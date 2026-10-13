#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;
using Progenitor.Ecs;
using Unity.Collections;
using Unity.Entities;
using Unity.Rendering;
using Unity.Transforms;
using UnityEngine;

namespace Progenitor.Models;

/// <summary>
/// A client-side hide of game scenery or unit models, made by
/// <see cref="PluginModels.HideScenery"/> or <see cref="PluginModels.HideUnitModels"/>. Nothing
/// changes on the server: collision, AI and other players are untouched. The hide is re-applied
/// every <see cref="SceneryFrame.SweepFrames"/> frames, so a piece the game rebuilds (spawn chains
/// recreate some props as new entities) or a unit model that respawns is hidden again. Dispose
/// shows everything this handle hid.
/// </summary>
public sealed class SceneryHide : IDisposable
{
    private readonly Vector3 _min, _max;
    private readonly string[] _prefixes;
    private readonly bool _units;
    private readonly HashSet<Entity> _hidden = new();
    private readonly List<Renderer> _renderers = new();
    private bool _disposed;

    internal SceneryHide(Vector3 min, Vector3 max, string[] prefixes, bool units)
    {
        _min = min;
        _max = max;
        _prefixes = prefixes;
        _units = units;
    }

    /// <summary>Render entities or renderers hidden right now.</summary>
    public int Hidden => _units ? _renderers.Count : _hidden.Count;

    /// <summary>
    /// Matching pieces that are baked static scenery (<c>StaticSceneTag</c>): those ignore
    /// <c>DisableRendering</c> (live 2026-09-25, the throne-hall chandeliers) and stay drawn.
    /// </summary>
    public int SkippedStatic { get; private set; }

    /// <summary>Mesh (or model) names hidden so far, for logs.</summary>
    public HashSet<string> Names { get; } = new(StringComparer.Ordinal);

    internal bool Disposed => _disposed;

    internal void Sweep()
    {
        if (_disposed) return;
        if (_units) SweepUnits();
        else SweepEntities();
    }

    private bool Matches(string name)
    {
        foreach (var p in _prefixes) if (name.StartsWith(p, StringComparison.Ordinal)) return true;
        return false;
    }

    private unsafe void SweepEntities()
    {
        var world = Worlds.Client;
        if (world == null || !world.IsCreated) return;
        var em = world.EntityManager;
        var egs = world.GetExistingSystemManaged<EntitiesGraphicsSystem>();
        if (egs == null) return;
        var mmiType = ComponentIndex.Of<MaterialMeshInfo>(ComponentType.AccessMode.ReadOnly);
        var ltwType = ComponentIndex.Of<LocalToWorld>(ComponentType.AccessMode.ReadOnly);
        var disable = ComponentType.FromTypeIndex(ComponentIndex.IndexOf<DisableRendering>());
        TypeIndex staticTag = default;
        try { staticTag = ComponentIndex.IndexOf<Unity.Entities.StaticSceneTag>(); } catch { /* not in this build */ }

        var query = em.CreateEntityQuery(new EntityQueryDesc { All = new[] { mmiType, ltwType } });
        int skipped = 0;
        try
        {
            var all = query.ToEntityArray(Allocator.TempJob);
            try
            {
                foreach (var e in all)
                {
                    float* m = (float*)em.GetComponentDataRawRO(e, ltwType.TypeIndex);
                    if (m == null) continue;
                    // float4x4 column-major: translation in the fourth column.
                    float x = m[12], y = m[13], z = m[14];
                    if (x < _min.x || x > _max.x || y < _min.y || y > _max.y || z < _min.z || z > _max.z) continue;
                    var info = e.Read<MaterialMeshInfo>(em);
                    Mesh? mesh = null;
                    try { mesh = egs.GetMesh(info.MeshID); } catch { /* unregistered mesh id */ }
                    if (mesh == null) continue;
                    string name = mesh.name;
                    if (!Matches(name)) continue;
                    if (staticTag != default && em.HasComponent(e, ComponentType.FromTypeIndex(staticTag))) { skipped++; continue; }
                    if (!em.HasComponent(e, disable))
                    {
                        em.AddComponent(e, disable);
                        _hidden.Add(e);
                        Names.Add(name);
                    }
                }
            }
            finally { all.Dispose(); }
        }
        finally { query.Dispose(); }
        SkippedStatic = skipped;
    }

    private void SweepUnits()
    {
        var root = GameObject.Find("HybridModels");
        if (root == null) return;
        var t = root.transform;
        for (int i = 0; i < t.childCount; i++)
        {
            var model = t.GetChild(i);
            if (!Matches(model.name)) continue;
            foreach (var r in model.GetComponentsInChildren<Renderer>(true))
            {
                if (r == null || !r.enabled) continue;
                r.enabled = false;
                _renderers.Add(r);
                Names.Add(model.name);
            }
        }
        _renderers.RemoveAll(r => r == null);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var r in _renderers)
        {
            try { if (r != null) r.enabled = true; } catch { /* model gone */ }
        }
        _renderers.Clear();

        var world = Worlds.Client;
        if (world != null && world.IsCreated && _hidden.Count > 0)
        {
            var em = world.EntityManager;
            var disable = ComponentType.FromTypeIndex(ComponentIndex.IndexOf<DisableRendering>());
            foreach (var e in _hidden)
            {
                try { if (em.Exists(e) && em.HasComponent(e, disable)) em.RemoveComponent(e, disable); }
                catch (Exception ex) { Plugin.Logger?.LogWarning("Models: showing scenery again threw: " + ex.Message); }
            }
        }
        _hidden.Clear();
        SceneryFrame.Forget(this);
    }
}

/// <summary>The <see cref="Plugin.Frame"/> sweep behind every live <see cref="SceneryHide"/>.</summary>
internal static class SceneryFrame
{
    internal const int SweepFrames = 60;
    private static readonly List<SceneryHide> Live = new();
    private static bool _armed;
    private static int _frame;

    internal static void Add(SceneryHide hide)
    {
        Live.Add(hide);
        hide.Sweep();
        if (_armed) return;
        Plugin.Frame += OnFrame;
        _armed = true;
    }

    internal static void Forget(SceneryHide hide)
    {
        Live.Remove(hide);
        if (Live.Count == 0) Disarm();
    }

    private static void Disarm()
    {
        if (!_armed) return;
        try { Plugin.Frame -= OnFrame; } catch { /* Frame may already be null on uninstall */ }
        _armed = false;
    }

    private static void OnFrame()
    {
        if (++_frame % SweepFrames != 0) return;
        foreach (var h in Live.ToArray())
        {
            try { h.Sweep(); }
            catch (Exception e) { Plugin.Logger?.LogWarning("Models: scenery sweep threw: " + e.Message); }
        }
    }

    /// <summary>Core uninstall: everything shown again.</summary>
    internal static void Uninstall()
    {
        foreach (var h in Live.ToArray())
        {
            try { h.Dispose(); } catch { /* keep going */ }
        }
        Live.Clear();
        Disarm();
    }
}
#endif
