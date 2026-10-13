#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using ProjectM;
using ProjectM.Contest.Arena;
using ProjectM.Presentation.Trails;
using ProjectM.Sequencer;
using ProjectM.Shared;
using Progenitor.Ecs;
using Stunlock.Core;
using Stunlock.Sequencer;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine.UI;
using UnityEngine.VFX;

namespace Progenitor.Effects;

/// <summary>
/// Client Unity backend for <see cref="Assets"/>. Each family follows a passing spike:
/// UI image (evidence 78), particle clone (79), HDRP volume (79), Cardinal sequence (80).
/// Restructured; not a paste of the snippet files.
/// </summary>
internal sealed class UnityEffectRuntime : IEffectRuntime
{
    private static readonly string[] MaterialColorProps = { "_UnlitColor", "_BaseColor", "_Color", "_EmissionColor" };
    private static readonly string[] VfxColorNames =
    {
        "Color", "_Color", "StartColor", "Tint", "BeamColor", "HDRColor", "ColorHDR", "MainColor",
    };

    private static Shader? ResolvedUnlitShader;
    private static string ResolvedUnlitName = "";
    private static bool UnlitResolved;
    private static bool PoseTypesReady;
    private static ComponentType TranslationRo;
    private static ComponentType LocalToWorldRo;

    private readonly Dictionary<string, int> _layersByRoot = new(StringComparer.OrdinalIgnoreCase);

    public bool IsClient => !Plugin.IsServerProcess;

    public EffectVec2 ScreenSize =>
        new(Math.Max(16, Screen.width), Math.Max(16, Screen.height));

    public int MaxDriverPollFrames => SequenceEffectHandle.DefaultDriverPollFrames;

    public string? LastMissReason { get; private set; }

    public string? ResolveBeamPreset(BeamStyle style, out string source) =>
        ResolveBeamPresetLive(style, out source);

    public string? ResolveBeamLinePrefab(BeamStyle style, out string source)
    {
        source = "";
        var prefab = FindGameLinePrefab(style, out string chosen, out var beam);
        if (prefab == null || string.IsNullOrEmpty(chosen)) return null;
        source = FormatGameLineSource(chosen, beam);
        return chosen;
    }

    internal static string FormatGameLineSource(string chosen, BeamCatalog.GameLineBeam beam) =>
        "loaded beam prefab '" + chosen + "' " + beam.Shader
        + " width " + beam.Width.ToString("0.###")
        + " (evidence 130; LineRenderer body, HybridLineRendererComponent)";

    public IReadOnlyList<string> Find(AssetKind kind, string? nameContains)
    {
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            switch (kind)
            {
                case AssetKind.Image:
                    ForEach<Image>(img =>
                    {
                        if (EffectOwnership.IsOwned(SafeName(img.gameObject))) return;
                        TryAddName(names, seen, SafeSpriteName(img), nameContains);
                        TryAddName(names, seen, SafeName(img.gameObject), nameContains);
                    });
                    ForEachObject<Sprite>(sp =>
                    {
                        TryAddName(names, seen, sp.name, nameContains);
                    });
                    break;
                case AssetKind.Particle:
                    ForEach<ParticleSystem>(ps =>
                    {
                        if (EffectOwnership.IsOwned(SafeName(ps.gameObject))) return;
                        TryAddName(names, seen, SafeName(ps.gameObject), nameContains);
                    });
                    break;
                case AssetKind.VisualEffect:
                    ForEach<VisualEffect>(vfx =>
                    {
                        if (EffectOwnership.IsOwned(SafeName(vfx.gameObject))) return;
                        TryAddName(names, seen, SafeName(vfx.gameObject), nameContains);
                    });
                    break;
                case AssetKind.Volume:
                    ForEach<Volume>(vol =>
                    {
                        if (EffectOwnership.IsOwned(SafeName(vol.gameObject))) return;
                        TryAddName(names, seen, SafeName(vol.gameObject), nameContains);
                    });
                    break;
                case AssetKind.Sequence:
                    // Sequence names live on buff prefabs (ECS), not scene GOs. The fake
                    // covers Find(Sequence); a live catalog walk is a later cut.
                    break;
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Effects: Find(" + kind + ") scan threw: " + e.Message);
        }

        names.Sort(StringComparer.OrdinalIgnoreCase);
        return names;
    }

    public IRuntimeObject? Borrow(AssetKind kind, string name, string rootName)
    {
        LastMissReason = null;
        try
        {
            var root = EnsureRoot(rootName);
            if (root == null) return null;
            return kind switch
            {
                AssetKind.Image => BorrowImage(name, root),
                AssetKind.Particle => BorrowParticle(name, root),
                AssetKind.Volume => BorrowVolume(name, root),
                AssetKind.Sequence => BorrowSequence(name, root),
                _ => null,
            };
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("Effects: Unity Borrow(" + kind + ", '" + name + "') threw: " + e);
            return null;
        }
    }

    public IRuntimeHost? Host(IRuntimeObject borrowed, HostKind kind, string rootName)
    {
        if (borrowed is not UnityBorrowedObject obj)
        {
            Plugin.Logger?.LogInfo("Effects: Host(" + kind + ") ignored a non-Unity runtime object; returning a disabled handle.");
            return null;
        }

        try
        {
            // Sequence play waits until both Start and End are set on the handle
            // (evidence 96). Host of an unposed sequence still succeeds: the RT camera
            // looks along the stored beam and the poll attaches the driver after play.
            if (!obj.Alive && obj.Kind != AssetKind.Sequence)
            {
                Plugin.Logger?.LogInfo("Effects: Host(" + kind + ") skipped; borrowed '" + obj.Name + "' is not alive.");
                return null;
            }

            var root = EnsureRoot(rootName);
            if (root == null)
            {
                Plugin.Logger?.LogInfo("Effects: Host(" + kind + ") failed; could not create root '" + rootName + "'.");
                return null;
            }

            if (obj.Kind == AssetKind.Volume)
            {
                Plugin.Logger?.LogInfo("Effects: volume is camera-wide; Host does not create a canvas");
                return new UnityHostSurface(kind, canvas: null, camera: null, rt: null);
            }

            IRuntimeHost? host = kind switch
            {
                HostKind.Overlay => HostOverlay(obj, root),
                HostKind.RenderTexture => HostRenderTexture(obj, root),
                _ => null,
            };
            if (host == null)
                Plugin.Logger?.LogInfo("Effects: Host(" + kind + ") for '" + obj.Name + "' produced no surface.");
            return host;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("Effects: Unity Host(" + kind + ") threw: " + e);
            return null;
        }
    }

    public int DestroyRoot(string rootName)
    {
        if (string.IsNullOrEmpty(rootName)) return 0;
        GameObject? found = null;
        ForEach<Transform>(t =>
        {
            if (found != null) return;
            try
            {
                var go = t.gameObject;
                if (go == null) return;
                if ((go.name ?? "").Equals(rootName, StringComparison.OrdinalIgnoreCase)
                    && t.parent == null)
                    found = go;
            }
            catch { /* */ }
        });
        if (found == null) return 0;
        return TryDestroyImmediate(found) ? 1 : 0;
    }

    public int ReserveLayer(string rootName)
    {
        if (!string.IsNullOrEmpty(rootName)
            && _layersByRoot.TryGetValue(rootName, out int cached)
            && cached >= 8)
            return cached;

        if (!TryPickUnusedLayer(out int layer))
            return -1;

        if (!string.IsNullOrEmpty(rootName))
            _layersByRoot[rootName] = layer;
        Plugin.Logger?.LogInfo(
            "Effects: RT host reserved layer " + layer + " (unnamed, no renderer)"
            + (string.IsNullOrEmpty(rootName) ? "." : " for " + rootName + "."));
        return layer;
    }

    public int DestroyByPrefix(string prefix)
    {
        if (string.IsNullOrEmpty(prefix)) return 0;
        int n = 0;
        try
        {
            ForEach<ParticleSystem>(ps =>
            {
                if (!NameStarts(ps.gameObject, prefix)) return;
                try { ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear); }
                catch { /* */ }
            });
            ForEach<BeamEmitter>(b =>
            {
                if (!NameStarts(b.gameObject, prefix)) return;
                try { b.Stop(true); }
                catch { /* */ }
            });
            ForEach<Camera>(cam =>
            {
                if (!NameStarts(cam.gameObject, prefix) && !ParentStarts(cam.gameObject, prefix)) return;
                try { cam.enabled = false; } catch { /* */ }
                RenderTexture? rt = null;
                try { rt = cam.targetTexture; } catch { /* */ }
                try { cam.targetTexture = null; } catch { /* */ }
                SafeRelease(rt);
            });
            ForEach<Image>(img =>
            {
                if (!NameStarts(img.gameObject, prefix) && !ParentStarts(img.gameObject, prefix)) return;
                try { img.enabled = false; } catch { /* */ }
            });
            ForEach<Volume>(vol =>
            {
                if (!NameStarts(vol.gameObject, prefix)) return;
                try { vol.enabled = false; } catch { /* */ }
                try { vol.weight = 0f; } catch { /* */ }
                try { vol.sharedProfile = null; } catch { /* */ }
            });

            var doomed = new List<GameObject>();
            ForEach<Transform>(t =>
            {
                try
                {
                    var go = t.gameObject;
                    if (go == null) return;
                    if (!NameStarts(go, prefix) && !ParentStarts(go, prefix)) return;
                    if (go.transform.parent != null && NameStarts(go.transform.parent.gameObject, prefix))
                        return;
                    doomed.Add(go);
                }
                catch { /* */ }
            });

            for (int i = 0; i < doomed.Count; i++)
            {
                if (TryDestroyImmediate(doomed[i]))
                    n++;
            }

            ForEachObject<VolumeProfile>(p =>
            {
                try
                {
                    if ((p.name ?? "").IndexOf(prefix, StringComparison.OrdinalIgnoreCase) < 0) return;
                    UnityEngine.Object.DestroyImmediate(p);
                }
                catch { /* */ }
            });
            ForEachObject<RenderTexture>(rt =>
            {
                try
                {
                    if ((rt.name ?? "").IndexOf(prefix, StringComparison.OrdinalIgnoreCase) < 0) return;
                    SafeRelease(rt);
                }
                catch { /* */ }
            });
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Effects: DestroyByPrefix threw: " + e.Message);
        }

        return n;
    }

    // ---- borrow families ---------------------------------------------------------------------

    private static UnityBorrowedObject? BorrowImage(string name, GameObject root)
    {
        var sprite = FindSprite(name);
        var go = new GameObject("image-" + SanitizeChild(name));
        go.transform.SetParent(root.transform, false);
        try { go.AddComponent<CanvasRenderer>(); } catch { /* */ }
        var img = TryAdd<Image>(go);
        if (img == null)
        {
            TryDestroyImmediate(go);
            return null;
        }

        try { img.raycastTarget = false; } catch { /* */ }
        if (sprite != null)
        {
            try { img.sprite = sprite; } catch (Exception e) { Plugin.Logger?.LogWarning("Effects: sprite assign: " + e.Message); }
        }

        PlaceRect(go, Vector2.zero, 128f);
        return UnityBorrowedObject.ForImage(name, go, img);
    }

    private static UnityBorrowedObject? BorrowParticle(string name, GameObject root)
    {
        if (!TryFindParticle(name, out var source) || source == null)
            return null;
        GameObject? clone;
        try
        {
            var obj = UnityEngine.Object.Instantiate(source.gameObject);
            clone = obj == null ? null : obj.TryCast<GameObject>() ?? obj as GameObject;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Effects: ParticleSystem Instantiate: " + e.Message);
            return null;
        }

        if (!IsAlive(clone) || clone == null) return null;
        try { clone.name = "particle-" + SanitizeChild(name); } catch { /* */ }
        try { clone.transform.SetParent(root.transform, false); } catch { /* */ }
        try { clone.transform.localPosition = Vector3.zero; } catch { /* */ }
        try
        {
            var settings = clone.GetComponent<ParticleSettings>() ?? clone.GetComponentInChildren<ParticleSettings>();
            if (settings != null) settings.enabled = false;
        }
        catch { /* */ }

        var ps = clone.GetComponent<ParticleSystem>() ?? clone.GetComponentInChildren<ParticleSystem>();
        if (ps == null)
        {
            TryDestroyImmediate(clone);
            return null;
        }

        try
        {
            var main = ps.main;
            main.simulationSpace = ParticleSystemSimulationSpace.Local;
        }
        catch { /* */ }

        // A pooled source may be inactive. Activate only our clone; Play does not
        // activate its GameObject (live Charm particle probe, 2026-09-06).
        clone.SetActive(true);
        try { ps.Play(true); } catch { /* */ }
        return UnityBorrowedObject.ForParticle(name, clone, ps);
    }

    private static UnityBorrowedObject? BorrowVolume(string name, GameObject root)
    {
        var go = new GameObject("volume-" + SanitizeChild(name));
        go.transform.SetParent(root.transform, false);
        var volume = TryAdd<Volume>(go);
        if (volume == null)
        {
            TryDestroyImmediate(go);
            return null;
        }

        try { volume.isGlobal = true; } catch { /* */ }
        try { volume.priority = 10000f; } catch { /* */ }
        try { volume.weight = 1f; } catch { /* */ }
        try { volume.enabled = true; } catch { /* */ }

        VolumeProfile? profile = null;
        try
        {
            profile = ScriptableObject.CreateInstance<VolumeProfile>();
            if (profile != null) profile.name = root.name + "-profile";
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Effects: VolumeProfile create: " + e.Message);
        }

        if (profile == null)
        {
            TryDestroyImmediate(go);
            return null;
        }

        if (!TryAddVignette(profile) && !TryAddColorAdjustments(profile))
            Plugin.Logger?.LogWarning("Effects: Volume profile has neither Vignette nor ColorAdjustments");

        try { volume.sharedProfile = profile; } catch (Exception e) { Plugin.Logger?.LogWarning("Effects: sharedProfile: " + e.Message); }
        Plugin.Logger?.LogInfo("Effects: volume is camera-wide (not a per-panel overlay)");
        return UnityBorrowedObject.ForVolume(name, go, volume, profile);
    }

    private UnityBorrowedObject? BorrowSequence(string name, GameObject root)
    {
        var world = Worlds.Client;
        if (world == null || !world.IsCreated)
        {
            LastMissReason = "Effects: Sequence borrow needs Client_0";
            return null;
        }

        var em = world.EntityManager;
        if (!TryResolveBuffPrefab(world, name, out var buffPrefab) || buffPrefab == Entity.Null)
            return null;

        if (!TryResolveSequencePrefab(world, em, buffPrefab, out var seqPrefab) || seqPrefab == Entity.Null)
        {
            LastMissReason =
                "Effects: Sequence '" + name + "' has no BuffSequence buffer; returning a disabled handle.";
            return null;
        }

        // PlaySequence is pose-only at Start (evidence 91). Marker / SequenceInstance bind
        // is behind Assets.BindSequencerMarkers (default off: evidence 101). Late-frame
        // SetPosition is the drawing path. Never PlaySequence the buff itself (R22 #1).
        return UnityBorrowedObject.ForSequence(name, world, seqPrefab, root);
    }

    /// <summary>
    /// Spike step 2 (evidence 80, 91): <c>PlaySequence(em, prefab, Start, identity)</c> only.
    /// That call spawns <c>Beam_Cardinal_Orb_Guardian_01(Clone)</c> and plays the beam sound.
    /// A two-point <c>PlaySequence(target, prefab, …, secondary)</c> lost both. Markers are
    /// created here but <c>SequenceInstance.Target</c> / <c>SecondaryTarget</c> are <em>not</em>
    /// written on this call: evidence 96 showed that poke (on a Translation-only entity)
    /// stopped the VFX clone from instantiating. Bind runs on a later tick once the driver
    /// GO exists, after <c>ParticleLifetimeSystem.CheckIfShouldSpawn</c> has seen the
    /// sequence entity's own <c>LocalToWorld</c>.
    /// </summary>
    internal static SequencePlayResult PlaySequenceAtPose(
        World world,
        Entity playPrefab,
        EffectVec3 start,
        EffectVec3 end,
        TransformWriteState writes)
    {
        var result = new SequencePlayResult();
        var em = world.EntityManager;
        var startPos = new float3(start.X, start.Y, start.Z);
        var endPos = new float3(end.X, end.Y, end.Z);
        var before = SnapshotDriverIds();

        if (!TryPlayPose(em, playPrefab, startPos, out var state) || state.Id == Entity.Null)
        {
            Plugin.Logger?.LogInfo("Effects: PlaySequence returned a null sequence entity");
            return result;
        }

        result.Sequence = state.Id;
        result.SeenDrivers = before;
        Plugin.Logger?.LogInfo(
            "Effects: sequence play pose start=" + start
            + " end=" + end
            + " seq=" + result.Sequence.Index + ":" + result.Sequence.Version);

        if (Assets.BindSequencerMarkers)
        {
            if (!TryCreateMarker(em, startPos, writes, out result.StartMarker))
                Plugin.Logger?.LogInfo("Effects: sequence start marker failed.");
            if (!TryCreateMarker(em, endPos, writes, out result.EndMarker))
                Plugin.Logger?.LogInfo("Effects: sequence end marker failed.");
        }

        result.Driver = SequenceDriverLookup.FromSequenceEntity(world, result.Sequence)
            ?? SequenceDriverLookup.FindFallback(before);
        if (result.Driver == null)
        {
            Plugin.Logger?.LogInfo(
                "Effects: sequence driver GO not found this frame (sequence entity has no HybridLineRenderers yet; Host will poll again).");
        }
        else
            Plugin.Logger?.LogInfo("Effects: sequence driver '" + SafeName(result.Driver) + "'");
        return result;
    }

    internal struct SequencePlayResult
    {
        public Entity Sequence;
        public Entity StartMarker;
        public Entity EndMarker;
        public GameObject? Driver;
        public HashSet<int>? SeenDrivers;
        public bool BoundDataSource;
    }

    internal static void LogBeamPath(in SequencePlayResult result, EffectVec3 start, EffectVec3 end)
    {
        if (result.BoundDataSource)
        {
            Plugin.Logger?.LogInfo(
                "Effects: sequence beam SequenceInstance.Target/SecondaryTarget start=" + start
                + " end=" + end
                + " seq=" + result.Sequence.Index + ":" + result.Sequence.Version
                + " marker=" + result.EndMarker.Index + ":" + result.EndMarker.Version);
            return;
        }

        Plugin.Logger?.LogInfo(
            "Effects: sequence beam hybrid SetPosition fallback (no SequenceInstance on seq="
            + result.Sequence.Index + ":" + result.Sequence.Version + ")");
    }

    private static bool TryPlayPose(EntityManager em, Entity seqPrefab, float3 pos, out SequenceState state)
    {
        state = default;
        try
        {
            state = SequenceUtility.PlaySequence(em, seqPrefab, pos, quaternion.identity);
            return state.Id != Entity.Null;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Effects: PlaySequence pose-only threw: " + e.Message);
            return false;
        }
    }

    /// <summary>
    /// Client-local transform entity. The sequencer reads this through
    /// <c>SequenceInstance.Target</c> / <c>SecondaryTarget</c> and
    /// <c>GetTranslationSource.SpellTarget</c>. <c>ParticleLifetimeSystem</c>
    /// spawn and <c>Pull_Entity</c> position lookups sample <c>LocalToWorld</c>
    /// plus <c>Translation</c> / <c>Rotation</c>, so a Translation-only
    /// marker is not enough (evidence 96). Destroyed with the play entity on dispose.
    /// </summary>
    private static bool TryCreateMarker(EntityManager em, float3 pos, TransformWriteState writes, out Entity marker)
    {
        marker = Entity.Null;
        try
        {
            marker = em.CreateEntity();
            if (marker == Entity.Null) return false;
            TryWriteTransform(em, marker, pos, writes);
            return true;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Effects: sequence marker CreateEntity threw: " + e.Message);
            marker = Entity.Null;
            return false;
        }
    }

    internal static void TryMoveMarker(World? world, Entity marker, EffectVec3 pos, TransformWriteState writes)
    {
        if (world == null || !world.IsCreated || marker == Entity.Null) return;
        try
        {
            var em = world.EntityManager;
            if (!em.Exists(marker)) return;
            TryWriteTransform(em, marker, new float3(pos.X, pos.Y, pos.Z), writes);
        }
        catch (Exception e)
        {
            if (writes.ShouldAttempt(TransformWriteKind.Translation))
            {
                writes.MarkFailed(TransformWriteKind.Translation);
                Plugin.Logger?.LogWarning("Effects: sequence marker transform write threw: " + e.Message);
            }
        }
    }

    /// <summary>
    /// Move the pose-only play entity to <paramref name="pos"/> so a later Start write
    /// walks the near end without another <c>PlaySequence</c>.
    /// </summary>
    internal static void TryReposeSequence(World? world, Entity sequence, EffectVec3 pos, TransformWriteState writes)
    {
        if (world == null || !world.IsCreated || sequence == Entity.Null) return;
        try
        {
            var em = world.EntityManager;
            if (!em.Exists(sequence)) return;
            TryWriteTransform(em, sequence, new float3(pos.X, pos.Y, pos.Z), writes);
        }
        catch (Exception e)
        {
            if (writes.ShouldAttempt(TransformWriteKind.Translation))
            {
                writes.MarkFailed(TransformWriteKind.Translation);
                Plugin.Logger?.LogWarning("Effects: sequence pose Translation write threw: " + e.Message);
            }
        }
    }

    /// <summary>
    /// Write Translation, Rotation and LocalToWorld through generic
    /// <c>SetComponentData&lt;T&gt;</c> (evidence 99: <c>Entity.Write&lt;LocalToWorld&gt;</c>
    /// is refused because TypeSize reads 4). Structural add is non-generic
    /// <c>AddComponent</c>. Each component's failure is logged once on
    /// <paramref name="writes"/> and not retried.
    /// </summary>
    private static void TryWriteTransform(EntityManager em, Entity entity, float3 pos, TransformWriteState writes)
    {
        var t = new Translation { Value = pos };
        TryGenericTransformWrite(em, entity, in t, writes, TransformWriteKind.Translation);

        var r = new Rotation { Value = quaternion.identity };
        TryGenericTransformWrite(em, entity, in r, writes, TransformWriteKind.Rotation);

        var ltw = new LocalToWorld
        {
            Value = float4x4.TRS(pos, quaternion.identity, new float3(1f, 1f, 1f)),
        };
        TryGenericTransformWrite(em, entity, in ltw, writes, TransformWriteKind.LocalToWorld);
    }

    private static void TryGenericTransformWrite<T>(
        EntityManager em,
        Entity entity,
        in T value,
        TransformWriteState writes,
        TransformWriteKind kind)
        where T : struct
    {
        if (!writes.ShouldAttempt(kind)) return;
        try
        {
            var type = ComponentIndex.Of<T>(ComponentType.AccessMode.ReadWrite);
            if (!em.HasComponent(entity, type))
                em.AddComponent(entity, type);
            em.SetComponentData(entity, value);
            writes.MarkSucceeded();
        }
        catch (Exception e)
        {
            writes.MarkFailed(kind);
            Plugin.Logger?.LogWarning("Effects: sequence " + kind + " write threw: " + e.Message);
        }
    }

    /// <summary>
    /// Feed the slots <c>ParticleLifetimeSystem.UpdateLineRendererPositionElement_Job</c> ultimately
    /// samples: <c>SequenceInstance.Target</c> / <c>SecondaryTarget</c> (Pull_Entity),
    /// plus the game's <c>SpellTarget</c> on the start marker. Returns true when SequenceInstance
    /// was written so the hybrid SetPosition path can stay off.
    /// </summary>
    internal static bool TryBindSequenceDataSource(
        EntityManager em,
        Entity sequence,
        Entity startMarker,
        Entity endMarker)
    {
        if (sequence == Entity.Null) return false;
        try
        {
            if (!em.Exists(sequence)) return false;
        }
        catch
        {
            return false;
        }

        bool bound = TryPokeSequenceInstanceTargets(em, sequence, startMarker, endMarker);
        // The far end is SpellTarget on the sequence *target* (the start marker), not a
        // float3. Add it on the marker we own. On the play entity, only poke if already present.
        TryWriteSpellTarget(em, startMarker, endMarker, addIfMissing: true);
        TryWriteSpellTarget(em, sequence, endMarker, addIfMissing: false);
        TryWriteEntityOwner(em, sequence, startMarker);
        return bound;
    }

    private static unsafe bool TryPokeSequenceInstanceTargets(
        EntityManager em,
        Entity sequence,
        Entity startMarker,
        Entity endMarker)
    {
        try
        {
            if (!sequence.Has<SequenceInstance>(em)) return false;
            var typeIndex = ComponentIndex.IndexOf<SequenceInstance>();
            void* raw = em.GetComponentDataRawRW(sequence, typeIndex);
            if (raw == null) return false;
            // Game metadata / Sequencer.dll FieldOffset: Target @8, SecondaryTarget @16.
            // SequenceInstance has bools so it is not C# unmanaged; field poke, not Write<T>.
            *(Entity*)((byte*)raw + 8) = startMarker;
            *(Entity*)((byte*)raw + 16) = endMarker;
            return true;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Effects: SequenceInstance Target poke threw: " + e.Message);
            return false;
        }
    }

    private static unsafe bool TryWriteSpellTarget(EntityManager em, Entity entity, Entity target, bool addIfMissing)
    {
        if (entity == Entity.Null || target == Entity.Null) return false;
        try
        {
            if (!em.Exists(entity)) return false;
            var writeType = ComponentIndex.Of<SpellTarget>(ComponentType.AccessMode.ReadWrite);
            if (!em.HasComponent(entity, writeType))
            {
                if (!addIfMissing) return false;
                // Start marker we own: the far end is SpellTarget on the sequence target
                // (game metadata). Adding it on a client-local marker is lookup-only.
                try { em.AddComponent(entity, writeType); }
                catch (Exception e)
                {
                    Plugin.Logger?.LogWarning("Effects: SpellTarget add threw: " + e.Message);
                    return false;
                }
            }

            var typeIndex = ComponentIndex.IndexOf<SpellTarget>();
            void* raw = em.GetComponentDataRawRW(entity, typeIndex);
            if (raw == null) return false;
            // NetworkedEntity._Entity @0, _WaitingForSync @8.
            *(Entity*)raw = target;
            *((byte*)raw + 8) = 0;
            return true;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Effects: SpellTarget write threw: " + e.Message);
            return false;
        }
    }

    private static bool TryWriteEntityOwner(EntityManager em, Entity sequence, Entity owner)
    {
        if (sequence == Entity.Null || owner == Entity.Null) return false;
        try
        {
            var value = new EntityOwner { Owner = owner };
            if (!sequence.Has<EntityOwner>(em)) return false;
            sequence.Write(em, in value);
            return true;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Effects: EntityOwner write threw: " + e.Message);
            return false;
        }
    }

    /// <summary>
    /// Per-handle late-write scratch. Reused every frame so the slot never news a
    /// state object (R25 #12). Strings are assigned only when <see cref="LogInspect"/>.
    /// </summary>
    internal struct HybridDriveState
    {
        public bool LogInspect;
        public bool SequencerMatched;
        public bool Wrote;
        public bool SlotsEnsured;
        public string? Before;
        public string? After;
        public LineRenderer? Line;
        public Vector3 Pos0;
        public Vector3 Pos1;

        public void BeginFrame(bool logInspect)
        {
            LogInspect = logInspect;
            SequencerMatched = false;
            Wrote = false;
        }
    }

    /// <summary>
    /// R27 finding 17: <c>GetBufferLength</c> is read here as an <b>element</b> count and
    /// multiplied by a hard-coded 20-byte stride, so a game update that shrank
    /// <c>LineRendererPositionElement</c> would put the last elements past the end of the chunk
    /// allocation. The interop's own answer is checked once and a mismatch disables the path.
    /// </summary>
    private static LayoutOnceGate _lineElementGate;

    /// <summary>
    /// One-shot flag for the "this sequence's line buffer is too small" line (R29 finding 4).
    /// Deliberately <b>not</b> the gate: a 0- or 1-element buffer is an ordinary entity, so the
    /// raw path stays on for every other sequence and only the note is throttled.
    /// </summary>
    private static bool _lineTooSmallLogged;

    /// <summary>One-shot flag for the <c>TypeManager</c> cross-check disagreement (EV17).</summary>
    private static bool _lineCrossCheckLogged;

    /// <summary>One-shot flag for the <c>sizeof</c> reading note (R30 finding 5).</summary>
    private static bool _lineSizeReadingLogged;

    /// <summary>
    /// One-shot flag for the "that number cannot be a buffer length" warning.
    ///
    /// <para><b>R30 finding 9.</b> This condition used to call
    /// <c>_lineElementGate.Fail(...)</c>, latching the <em>element-size</em> gate — a fact about
    /// the build - on one entity's <c>GetBufferLength</c>, which is a fact about that entity.
    /// One misread turned the raw line write off for every beam for the rest of the process and
    /// said "raw buffer writes disabled for this build". It refuses this call now, exactly as
    /// the blackboard path already did, and the warning stays once per generation.</para>
    /// </summary>
    private static bool _lineLengthRefusedLogged;

    /// <summary>
    /// Write start/end into the sequence entity's <c>LineRendererPositionElement</c>
    /// buffer (stride 0x14). The Presentation job
    /// <c>UpdateLineRendererPositionElement_Job</c> copies those
    /// into <c>HybridLineRendererComponent.Positions</c>. LateFrame runs after that
    /// job, so this write is for the next frame plus inspect; the owned line is
    /// still the pixels unless the clone LineRenderer is the visible body.
    /// </summary>
    internal static unsafe bool TryWriteLineRendererPositions(
        World? world,
        Entity sequence,
        in EffectVec3 start,
        in EffectVec3 end)
    {
        if (world == null || !world.IsCreated || sequence == Entity.Null) return false;
        // R28 finding 2: the gate is closed until CheckLineElementSizeOnce has run and passed,
        // so it cannot be consulted before the probe (the old code checked it here first, which
        // with a default-closed gate would refuse everything forever).
        if (_lineElementGate.Failed) return false;
        try
        {
            var em = world.EntityManager;
            if (!em.Exists(sequence)) return false;
            var type = ComponentIndex.Of<LineRendererPositionElement>(ComponentType.AccessMode.ReadWrite);
            if (!em.HasComponent(sequence, type)) return false;
            var index = ComponentIndex.IndexOf<LineRendererPositionElement>();
            CheckLineElementSizeOnce(index);
            if (_lineElementGate.Disabled) return false;
            void* raw = em.GetBufferRawRW(sequence, index);
            if (raw == null) return false;
            int length = em.GetBufferLength(sequence, index);
            const int stride = BufferElementGuard.LineRendererPositionElementSize;
            // R28 finding 1: `length < 2` used to be the only bound, and every offset below was
            // computed in int. A garbage length — the same interop misread the element-size
            // guard above exists for — wrote past the chunk, and i * stride wrapped negative
            // at i ~ 107M. Bounds and offsets are now long arithmetic in a game-free type.
            //
            // R29 finding 4: only an *impossible* length latches. A buffer with 0 or 1 element
            // is an ordinary sequence (nothing authored yet, or a one-point renderer); EV16
            // latched the gate on it and turned the raw path off for the whole process.
            var verdict = LineRendererBufferLayout.Classify(length, stride);
            if (verdict == LineRendererBufferLayout.LengthVerdict.TooSmall)
            {
                if (!_lineTooSmallLogged)
                {
                    _lineTooSmallLogged = true;
                    Plugin.Logger?.LogInfo(LineRendererBufferLayout.LengthSkippedLog(length));
                }

                return false;
            }

            if (verdict != LineRendererBufferLayout.LengthVerdict.Ok)
            {
                // R30 finding 9: refuse THIS sequence, exactly as TooSmall does. The gate is
                // about the build's layout; a length is about one entity.
                if (!_lineLengthRefusedLogged)
                {
                    _lineLengthRefusedLogged = true;
                    Plugin.Logger?.LogWarning(LineRendererBufferLayout.LengthRefusedLog(length));
                }

                return false;
            }

            long total = LineRendererBufferLayout.TotalBytes(length, stride);

            // R29 residual 21.9: elements 0 and 1 used to be written as start and end
            // unconditionally, with PositionIndex stamped over, while the loop from i = 2
            // consulted PositionIndex — so element 0 carrying PositionIndex 1 put the start
            // point in the end slot. The first two elements' own indices now decide whether the
            // buffer already carries a usable mapping or has to be seeded positionally, and the
            // single loop below treats every element the same way.
            int index0 = ReadLineRendererPositionIndex(raw, 0, length, total, stride);
            int index1 = ReadLineRendererPositionIndex(raw, 1, length, total, stride);
            bool seed = LineRendererBufferLayout.SeedPositionally(index0, index1);

            for (int i = 0; i < length; i++)
            {
                long at = LineRendererBufferLayout.ElementOffset(i, length, stride);
                if (at < 0L) break;
                if (!LineRendererBufferLayout.FieldFits(
                        at,
                        LineRendererBufferLayout.PositionIndexOffset,
                        LineRendererBufferLayout.IndexFieldBytes,
                        total))
                {
                    break;
                }

                int endpoint;
                if (seed && i < LineRendererBufferLayout.MinElements)
                {
                    endpoint = i;
                    *(int*)((byte*)raw + at + LineRendererBufferLayout.PositionIndexOffset) = i;
                }
                else
                {
                    endpoint = LineRendererBufferLayout.EndpointFor(
                        *(int*)((byte*)raw + at + LineRendererBufferLayout.PositionIndexOffset));
                }

                if (endpoint == 0) WriteLineRendererPosition(raw, at, total, in start);
                else if (endpoint == 1) WriteLineRendererPosition(raw, at, total, in end);
            }

            return true;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Effects: LineRendererPositionElement write threw: " + e.Message);
            return false;
        }
    }

    /// <summary>
    /// R27 finding 17. The stride this walk multiplies by is
    /// <c>BufferElementGuard.LineRendererPositionElementSize</c>, read from the dump
    /// (<c>int@0</c>, <c>int@4</c>, <c>float3@8</c> = 20), so the
    /// number is stated, checked and logged rather than asserted in a comment.
    ///
    /// <para><b>R30 findings 5 and 21.</b> This probe used to latch the gate on
    /// <c>sizeof</c> — the same shape that took the blackboard path down live twice - and cited
    /// CLAUDE.md's raw-write policy for doing so. That policy is about a <em>component</em>
    /// write and makes <c>sizeof</c> the authority; the rule for a native buffer or blob stride
    /// is the one in <c>docs/effects.md</c>, "For a native array stride the dump is the
    /// authority; every runtime reading is a cross-check". So the gate is keyed on the dump
    /// constant being self-consistent and both readings only ever produce a line.</para>
    /// </summary>
    private static unsafe void CheckLineElementSizeOnce(TypeIndex index)
    {
        if (_lineElementGate.Checked) return;
        const int expected = BufferElementGuard.LineRendererPositionElementSize;
        // R31 finding 20: the field extent, summed from the dump's own offsets, so the gate can
        // refuse a mistyped size even though this element has no tail padding and the two
        // numbers coincide.
        const int packed = BufferElementGuard.LineRendererPositionElementPackedBytes;
        const int alignment = BufferElementGuard.LineRendererPositionElementAlignment;

        string? gateLog = _lineElementGate.Check(
            BufferElementGuard.WritesAllowed(expected, packed, alignment),
            SequenceBlackboardLayout.ConstantsInvariantLog(
                "LineRendererPositionElement", expected, packed, alignment));
        if (gateLog != null) Plugin.Logger?.LogWarning(gateLog);

        int reported = 0;
        try { reported = TypeManager.GetTypeInfo(index).ElementSize; }
        catch { /* TypeManager can read garbage through the interop; 0 = unknown. */ }
        int sizeOf = 0;
        try { sizeOf = sizeof(LineRendererPositionElement); }
        catch { sizeOf = 0; }

        if (!_lineSizeReadingLogged)
        {
            string? note = BufferElementGuard.SizeReadingLog(
                "LineRendererPositionElement", sizeOf, expected, packed, alignment);
            if (note != null)
            {
                _lineSizeReadingLogged = true;
                if (BufferElementGuard.SizeReadingIsWarning(sizeOf, expected, packed, alignment))
                    Plugin.Logger?.LogWarning(note);
                else
                    Plugin.Logger?.LogInfo(note);
            }
        }

        if (BufferElementGuard.TypeManagerDisagrees(reported, expected) && !_lineCrossCheckLogged)
        {
            _lineCrossCheckLogged = true;
            Plugin.Logger?.LogWarning(
                BufferElementGuard.TypeManagerNote(
                    "LineRendererPositionElement", reported, expected));
        }
    }

    /// <summary>
    /// <c>PositionIndex</c> of element <paramref name="i"/>, or <c>-1</c> when the element or
    /// that field does not fit (R29 residual 21.9). Never writes.
    /// </summary>
    private static unsafe int ReadLineRendererPositionIndex(
        void* raw, int i, int length, long totalBytes, int stride)
    {
        long at = LineRendererBufferLayout.ElementOffset(i, length, stride);
        if (at < 0L) return -1;
        if (!LineRendererBufferLayout.FieldFits(
                at,
                LineRendererBufferLayout.PositionIndexOffset,
                LineRendererBufferLayout.IndexFieldBytes,
                totalBytes))
        {
            return -1;
        }

        return *(int*)((byte*)raw + at + LineRendererBufferLayout.PositionIndexOffset);
    }

    /// <summary>
    /// Write one element's three floats at an offset the caller already derived and
    /// bounds-checked. R28 finding 1: the field is checked against the buffer's total byte
    /// count before the pointer is formed. R29 residual 21.9: this no longer touches
    /// <c>PositionIndex</c> — the caller owns that decision for every element uniformly.
    /// </summary>
    private static unsafe bool WriteLineRendererPosition(
        void* raw, long at, long totalBytes, in EffectVec3 pos)
    {
        if (at < 0L) return false;
        if (!LineRendererBufferLayout.FieldFits(
                at,
                LineRendererBufferLayout.PositionValueOffset,
                LineRendererBufferLayout.PositionFieldBytes,
                totalBytes))
        {
            return false;
        }

        float* v = (float*)((byte*)raw + at + LineRendererBufferLayout.PositionValueOffset);
        v[0] = pos.X;
        v[1] = pos.Y;
        v[2] = pos.Z;
        return true;
    }

    internal static void DriveDriverEnds(
        GameObject go,
        in EffectVec3 start,
        in EffectVec3 end,
        ref HybridDriveState state)
    {
        HybridLineRendererComponent? hybrid = null;
        try { hybrid = go.GetComponent<HybridLineRendererComponent>(); }
        catch { /* */ }
        if (hybrid == null)
        {
            try { hybrid = go.GetComponentInChildren<HybridLineRendererComponent>(true); }
            catch { /* */ }
        }

        if (hybrid != null)
        {
            DriveHybrid(hybrid, start, end, ref state);
            return;
        }

        BeamEmitter? beam = null;
        try { beam = go.GetComponent<BeamEmitter>(); }
        catch { /* */ }
        if (beam == null)
        {
            try { beam = go.GetComponentInChildren<BeamEmitter>(true); }
            catch { /* */ }
        }

        if (beam != null)
        {
            state.Pos0.x = start.X;
            state.Pos0.y = start.Y;
            state.Pos0.z = start.Z;
            state.Pos1.x = end.X;
            state.Pos1.y = end.Y;
            state.Pos1.z = end.Z;
            try { beam.StartPos = state.Pos0; } catch { /* */ }
            try { beam.EndPos = state.Pos1; } catch { /* */ }
            state.Wrote = true;
            if (state.LogInspect)
                Plugin.Logger?.LogInfo(
                    "Effects: sequence driver '" + SafeName(go)
                    + "' has no HybridLineRendererComponent; BeamEmitter fallback StartPos/EndPos");
            return;
        }

        if (!state.LogInspect) return;

        VisualEffect? vfx = null;
        try { vfx = go.GetComponent<VisualEffect>(); }
        catch { /* */ }
        if (vfx != null)
        {
            state.Pos0.x = start.X;
            state.Pos0.y = start.Y;
            state.Pos0.z = start.Z;
            state.Pos1.x = end.X;
            state.Pos1.y = end.Y;
            state.Pos1.z = end.Z;
            TryVfxVec3(vfx, "StartBeamOffset", state.Pos0);
            TryVfxVec3(vfx, "EndBeamOffset", state.Pos1);
        }

        Plugin.Logger?.LogInfo(
            "Effects: sequence driver '" + SafeName(go)
            + "' has no HybridLineRendererComponent or BeamEmitter to drive");
    }

    private static void DriveHybrid(
        HybridLineRendererComponent h,
        in EffectVec3 startV,
        in EffectVec3 endV,
        ref HybridDriveState state)
    {
        // before= is the game's write this frame (Presentation / HybridLineRenderers).
        // Comparing against our previous write was the evidence-101 false positive.
        if (state.Line == null)
        {
            try { state.Line = h.GetComponent<LineRenderer>(); }
            catch { /* */ }
        }

        if (state.LogInspect)
        {
            // Positions list wrapper is only taken on the once-per-handle inspect.
            bool haveBefore = TryReadHybridListEnds(h, out Vector3 before0, out Vector3 before1);
            state.SequencerMatched = haveBefore && Near(before0, startV) && Near(before1, endV);
            state.Before = FmtTwoPoints(before0, before1, haveBefore);
        }
        else
        {
            state.SequencerMatched = LineEndsMatch(state.Line, startV, endV);
        }

        if (!state.SlotsEnsured)
        {
            EnsureHybridSlots(h);
            state.SlotsEnsured = true;
        }

        state.Pos0.x = startV.X;
        state.Pos0.y = startV.Y;
        state.Pos0.z = startV.Z;
        state.Pos1.x = endV.X;
        state.Pos1.y = endV.Y;
        state.Pos1.z = endV.Z;
        var start3 = new float3(startV.X, startV.Y, startV.Z);
        var end3 = new float3(endV.X, endV.Y, endV.Z);
        try
        {
            h.SetPosition(0, start3);
            h.SetPosition(1, end3);
            h.Refresh();
            state.Wrote = true;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Effects: HybridLineRenderer SetPosition/Refresh threw: " + e.Message);
        }

        if (!state.LogInspect) return;

        bool haveAfter = TryReadHybridListEnds(h, out Vector3 after0, out Vector3 after1);
        bool stuck = haveAfter && Near(after0, startV) && Near(after1, endV);
        state.After = FmtTwoPoints(after0, after1, haveAfter);
        Plugin.Logger?.LogInfo(
            SequenceLateDriver.FormatHybridPositionsLog(
                state.Before ?? "",
                state.After ?? "",
                stuck,
                state.SequencerMatched));
    }

    private static void EnsureHybridSlots(HybridLineRendererComponent h)
    {
        try
        {
            var list = h.Positions;
            if (list == null) return;
            while (list.Count < 2)
                list.Add(Vector3.zero);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Effects: HybridLineRenderer Positions pad threw: " + e.Message);
        }
    }

    private static bool TryReadHybridListEnds(HybridLineRendererComponent h, out Vector3 p0, out Vector3 p1)
    {
        p0 = default;
        p1 = default;
        try
        {
            var list = h.Positions;
            if (list == null || list.Count < 2) return false;
            p0 = list[0];
            p1 = list[1];
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string FmtTwoPoints(Vector3 p0, Vector3 p1, bool haveTwo)
    {
        if (!haveTwo) return "[]";
        return "[(" + p0.x.ToString("0.##") + "," + p0.y.ToString("0.##") + "," + p0.z.ToString("0.##")
            + "),(" + p1.x.ToString("0.##") + "," + p1.y.ToString("0.##") + "," + p1.z.ToString("0.##") + ")]";
    }

    private static bool LineEndsMatch(LineRenderer? lr, in EffectVec3 start, in EffectVec3 end)
    {
        if (lr == null) return false;
        try
        {
            if (lr.positionCount < 2) return false;
            return Near(lr.GetPosition(0), start) && Near(lr.GetPosition(1), end);
        }
        catch
        {
            return false;
        }
    }

    private static bool Near(Vector3 a, in EffectVec3 b)
    {
        float dx = a.x - b.X;
        float dy = a.y - b.Y;
        float dz = a.z - b.Z;
        return (dx * dx + dy * dy + dz * dz) < 1e-4f;
    }

    internal static void LogDriverComponentsOnce(GameObject go)
    {
        try
        {
            var all = go.GetComponentsInChildren<Component>(true);
            if (all == null)
            {
                Plugin.Logger?.LogInfo("Effects: sequence driver '" + SafeName(go) + "' components=");
                return;
            }

            var names = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i] == null) continue;
                string n;
                try { n = all[i].GetType().Name; }
                catch { continue; }
                if (string.IsNullOrEmpty(n) || !seen.Add(n)) continue;
                names.Add(n);
            }

            Plugin.Logger?.LogInfo(
                "Effects: sequence driver '" + SafeName(go) + "' components=" + string.Join(",", names));
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Effects: sequence driver component list threw: " + e.Message);
        }
    }

    private static void TryVfxVec3(VisualEffect vfx, string prop, Vector3 value)
    {
        try { vfx.SetVector3(prop, value); }
        catch { /* */ }
    }

    internal static void TryDestroyEntity(World? world, Entity entity)
    {
        if (world == null || !world.IsCreated || entity == Entity.Null) return;
        try
        {
            var em = world.EntityManager;
            if (!em.Exists(entity)) return;
            try
            {
                DestroyUtility.Destroy(em, entity, DestroyDebugReason.None, callerFilePath: null, callerLineNumber: 0);
            }
            catch (Exception e)
            {
                Plugin.Logger?.LogWarning("Effects: DestroyUtility.Destroy threw: " + e.Message);
                try { em.DestroyEntity(entity); }
                catch (Exception e2) { Plugin.Logger?.LogWarning("Effects: DestroyEntity threw: " + e2.Message); }
            }

            try
            {
                if (em.Exists(entity) && !entity.Has<DestroyTag>(em))
                    entity.Add<DestroyTag>(em);
            }
            catch (Exception e)
            {
                Plugin.Logger?.LogWarning("Effects: DestroyTag add threw: " + e.Message);
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Effects: TryDestroyEntity threw: " + e.Message);
        }
    }

    // ---- host --------------------------------------------------------------------------------

    private static UnityHostSurface? HostOverlay(UnityBorrowedObject obj, GameObject root)
    {
        var canvasGo = new GameObject("overlay");
        canvasGo.transform.SetParent(root.transform, false);
        var canvas = TryAdd<Canvas>(canvasGo);
        if (canvas == null)
        {
            TryDestroyImmediate(canvasGo);
            return null;
        }

        try { canvas.renderMode = RenderMode.ScreenSpaceOverlay; } catch { /* */ }
        try { canvas.sortingOrder = 5000; } catch { /* */ }
        try { canvas.pixelPerfect = true; } catch { /* */ }
        var scaler = TryAdd<CanvasScaler>(canvasGo);
        if (scaler != null)
        {
            try { scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize; } catch { /* */ }
            try { scaler.scaleFactor = 1f; } catch { /* */ }
        }

        TryAdd<GraphicRaycaster>(canvasGo);
        Stretch(canvasGo);
        if (obj.Kind == AssetKind.Image && IsAlive(obj.GameObject) && obj.GameObject != null)
        {
            try { obj.GameObject.transform.SetParent(canvasGo.transform, false); } catch { /* */ }
            PlaceRect(obj.GameObject, Vector2.zero, 192f);
        }

        obj.OverlayCanvas = canvas;
        return new UnityHostSurface(
            HostKind.Overlay, canvasGo, camera: null, rt: null, borrowed: obj.GameObject, pluginRoot: root);
    }

    private UnityHostSurface? HostRenderTexture(UnityBorrowedObject obj, GameObject root)
    {
        if (obj.Kind == AssetKind.Sequence)
            obj.TryBindSequenceDriver();

        bool sequence = obj.Kind == AssetKind.Sequence;
        int layer = ReserveLayer(root.name);
        bool haveLayer = layer >= 8;
        if (!haveLayer)
        {
            Plugin.Logger?.LogInfo(
                sequence
                    ? "Effects: no unused layer 8-31; RT host looks along the beam without moving layers (never a gameplay layer)."
                    : "Effects: no unused layer 8-31; RenderTexture host skipped (never a gameplay layer).");
            if (!sequence)
                return null;
        }

        List<(GameObject go, int layer)>? restored = null;
        if (haveLayer && IsAlive(obj.GameObject) && obj.GameObject != null)
        {
            restored = SnapshotAndSetLayer(obj.GameObject, layer);
            Plugin.Logger?.LogInfo(
                "Effects: RT host moved driver '" + SafeName(obj.GameObject) + "' to layer " + layer
                + " (restore on dispose).");
        }
        else if (sequence)
        {
            Plugin.Logger?.LogInfo(
                "Effects: RT host has no sequence driver GO yet; camera looks along the beam (clone is not parented under our root).");
        }

        var screen = ScreenSize;
        int sw = Math.Max(16, (int)screen.X);
        int sh = Math.Max(16, (int)screen.Y);
        RenderTexture? rt;
        try
        {
            rt = new RenderTexture(sw, sh, 0, RenderTextureFormat.ARGB32) { name = root.name + "-rt" };
            if (!rt.Create())
                Plugin.Logger?.LogWarning("Effects: RenderTexture.Create returned false");
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Effects: new RenderTexture: " + e.Message);
            RestoreLayers(restored);
            return null;
        }

        var camGo = new GameObject("rt-cam");
        camGo.transform.SetParent(root.transform, false);
        if (sequence)
            PlaceCameraAlongBeam(camGo, obj.BeamStart, obj.BeamEnd);
        else
            camGo.transform.localPosition = new Vector3(0f, 0f, -8f);
        if (haveLayer)
            camGo.layer = layer;
        var cam = TryAdd<Camera>(camGo);
        if (cam == null)
        {
            Plugin.Logger?.LogInfo("Effects: Host(RenderTexture) failed; AddComponent<Camera> returned null.");
            SafeRelease(rt);
            TryDestroyImmediate(camGo);
            RestoreLayers(restored);
            return null;
        }

        try { cam.orthographic = true; } catch { /* */ }
        try { cam.orthographicSize = sequence ? OrthoSizeForBeam(obj.BeamStart, obj.BeamEnd) : 4f; } catch { /* */ }
        try { cam.nearClipPlane = 0.1f; } catch { /* */ }
        try { cam.farClipPlane = 200f; } catch { /* */ }
        try { cam.cullingMask = haveLayer ? (1 << layer) : ~0; } catch { /* */ }
        try { cam.clearFlags = CameraClearFlags.SolidColor; } catch { /* */ }
        try { cam.backgroundColor = new Color(0f, 0f, 0f, 0f); } catch { /* */ }
        try { cam.targetTexture = rt; } catch { /* */ }
        try { cam.depth = -100f; } catch { /* */ }
        try { cam.allowHDR = false; } catch { /* */ }
        try { cam.allowMSAA = false; } catch { /* */ }

        var canvasGo = new GameObject("rt-canvas");
        canvasGo.transform.SetParent(root.transform, false);
        var canvas = TryAdd<Canvas>(canvasGo);
        if (canvas != null)
        {
            try { canvas.renderMode = RenderMode.ScreenSpaceOverlay; } catch { /* */ }
            try { canvas.sortingOrder = 5000; } catch { /* */ }
        }

        var scaler = TryAdd<CanvasScaler>(canvasGo);
        if (scaler != null)
        {
            try { scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize; } catch { /* */ }
        }

        TryAdd<GraphicRaycaster>(canvasGo);
        Stretch(canvasGo);
        var edge = new GameObject("raw");
        edge.transform.SetParent(canvasGo.transform, false);
        Stretch(edge);
        var raw = TryAdd<RawImage>(edge);
        if (raw == null)
        {
            Plugin.Logger?.LogInfo("Effects: Host(RenderTexture) failed; AddComponent<RawImage> returned null.");
            SafeRelease(rt);
            TryDestroyImmediate(canvasGo);
            TryDestroyImmediate(camGo);
            RestoreLayers(restored);
            return null;
        }

        try { raw.texture = rt; } catch { /* */ }
        try { raw.color = Color.white; } catch { /* */ }
        try { raw.raycastTarget = false; } catch { /* */ }
        return new UnityHostSurface(HostKind.RenderTexture, canvasGo, cam, rt, camGo, restored, haveLayer ? layer : -1, this, raw, root.name);
    }

    private static void PlaceCameraAlongBeam(GameObject camGo, EffectVec3 start, EffectVec3 end)
    {
        var a = new Vector3(start.X, start.Y, start.Z);
        var b = new Vector3(end.X, end.Y, end.Z);
        var along = b - a;
        if (along.sqrMagnitude < 1e-6f)
            along = Vector3.right;
        along.Normalize();
        var side = Vector3.Cross(along, Vector3.up);
        if (side.sqrMagnitude < 1e-6f)
            side = Vector3.Cross(along, Vector3.right);
        side.Normalize();
        var mid = (a + b) * 0.5f;
        float len = Vector3.Distance(a, b);
        try { camGo.transform.position = mid + side * Math.Max(8f, len); } catch { /* */ }
        try { camGo.transform.LookAt(mid); } catch { /* */ }
    }

    private static float OrthoSizeForBeam(EffectVec3 start, EffectVec3 end)
    {
        float dx = end.X - start.X;
        float dy = end.Y - start.Y;
        float dz = end.Z - start.Z;
        float len = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
        return Math.Max(4f, len * 0.5f + 2f);
    }

    internal static List<(GameObject go, int layer)> SnapshotAndSetLayer(GameObject go, int layer)
    {
        var saved = new List<(GameObject go, int layer)>();
        SnapshotAndSetLayerRecurse(go, layer, saved);
        return saved;
    }

    private static void SnapshotAndSetLayerRecurse(GameObject go, int layer, List<(GameObject go, int layer)> saved)
    {
        try
        {
            saved.Add((go, go.layer));
            go.layer = layer;
            var t = go.transform;
            for (int i = 0; i < t.childCount; i++)
            {
                var ch = t.GetChild(i);
                if (ch != null && ch.gameObject != null)
                    SnapshotAndSetLayerRecurse(ch.gameObject, layer, saved);
            }
        }
        catch { /* */ }
    }

    internal static void RestoreLayers(List<(GameObject go, int layer)>? saved)
    {
        if (saved == null) return;
        for (int i = 0; i < saved.Count; i++)
        {
            var go = saved[i].go;
            if (!IsAlive(go) || go == null) continue;
            try { go.layer = saved[i].layer; }
            catch { /* */ }
        }
    }

    // ---- sequence resolve --------------------------------------------------------------------

    private static readonly Dictionary<BeamStyle, string> GameLinePrefabCache = new();
    private static readonly HashSet<BeamStyle> GameLinePrefabMisses = new();

    internal static void ResetGameLinePrefabCache()
    {
        GameLinePrefabCache.Clear();
        GameLinePrefabMisses.Clear();
    }

    /// <summary>
    /// Newest loaded GameObject whose name is one of <see cref="BeamCatalog.GameLineBeamsFor"/>
    /// and that carries a <c>LineRenderer</c> — a vanilla two-point beam whose visible body is
    /// the line, not a trail sprite or a particle graph (evidence 130). The caller
    /// <c>Instantiate</c>s it, so the copy is ours and may be written; the original is only
    /// read. Candidate order is the catalog's, not scene order, so GameRed prefers the
    /// already-red Dracula blood-soul channel.
    /// </summary>
    internal static GameObject? FindGameLinePrefab(
        BeamStyle style,
        out string chosen,
        out BeamCatalog.GameLineBeam beam)
    {
        chosen = "";
        beam = default;
        var wanted = BeamCatalog.GameLineBeamsFor(style);
        if (wanted.Length == 0) return null;
        if (GameLinePrefabMisses.Contains(style)) return null;

        GameObject? found = null;
        int bestRank = int.MaxValue;
        try
        {
            var type = Il2CppType.Of<LineRenderer>();
            if (type == null) return null;
            var arr = Resources.FindObjectsOfTypeAll(type);
            if (arr == null) return null;
            for (int i = 0; i < arr.Length; i++)
            {
                LineRenderer? lr = null;
                try { lr = arr[i] == null ? null : arr[i].TryCast<LineRenderer>(); }
                catch { continue; }
                if (lr == null) continue;

                GameObject? go = null;
                string name;
                try
                {
                    go = lr.gameObject;
                    name = go != null ? (go.name ?? "") : "";
                }
                catch { continue; }
                if (go == null || name.Length == 0) continue;
                if (EffectOwnership.IsOwned(name)) continue;

                for (int r = 0; r < wanted.Length && r < bestRank; r++)
                {
                    if (!string.Equals(wanted[r].PrefabName, name, StringComparison.Ordinal)) continue;
                    bestRank = r;
                    found = go;
                    break;
                }

                if (bestRank == 0) break;
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Effects: game line prefab scan threw: " + e.Message);
            return null;
        }

        if (found == null || bestRank >= wanted.Length)
        {
            GameLinePrefabMisses.Add(style);
            return null;
        }

        beam = wanted[bestRank];
        chosen = beam.PrefabName;
        GameLinePrefabCache[style] = chosen;
        return found;
    }

    /// <summary>
    /// GameRed walks coffin / invalid-placement / arena-flag sequence GUIDs
    /// (<c>ServantCoffinEffects</c>, <c>BlueprintData.InvalidPlaceSequence</c>,
    /// <c>CastleArenaFlagBuff_Client.BeamSequenceGUID</c>). Thin and GameBlue are the Cardinal buff. Returns the
    /// decimal hash string so <see cref="Borrow"/> can resolve it.
    /// </summary>
    internal static string? ResolveBeamPresetLive(BeamStyle style, out string source)
    {
        source = "";
        if (style == BeamStyle.Thin || style == BeamStyle.GameBlue)
        {
            source = "Cardinal AB_Cardinal_SummonOrb_Beam_Buff dump/wiki guid -2014639169 sequence 586432528 (evidence 80)";
            return BeamCatalog.CardinalBuffHashName;
        }

        if (style != BeamStyle.GameRed) return null;

        var world = Worlds.Client;
        if (world == null || !world.IsCreated) return null;
        var em = world.EntityManager;

        if (TryFirstSequenceGuid(
                world, em,
                ComponentIndex.Of<ServantCoffinEffects>(ComponentType.AccessMode.ReadOnly),
                ReadCoffinGuids,
                "ServantCoffinEffects.ActiveServantGuid",
                out var name))
        {
            source = "ServantCoffinEffects.ActiveServantGuid (ServantCoffinstationSequenceSystem)";
            return name;
        }

        if (TryFirstSequenceGuid(
                world, em,
                ComponentIndex.Of<BlueprintData>(ComponentType.AccessMode.ReadOnly),
                ReadInvalidPlaceGuid,
                "BlueprintData.InvalidPlaceSequence",
                out name))
        {
            source = "BlueprintData.InvalidPlaceSequence (PreviewPlacementBuffSequenceSystem)";
            return name;
        }

        if (TryFirstSequenceGuid(
                world, em,
                ComponentIndex.Of<CastleArenaFlagBuff_Client>(ComponentType.AccessMode.ReadOnly),
                ReadArenaBeamGuid,
                "CastleArenaFlagBuff_Client.BeamSequenceGUID",
                out name))
        {
            source = "CastleArenaFlagBuff_Client.BeamSequenceGUID";
            return name;
        }

        return null;
    }

    private static bool TryFirstSequenceGuid(
        World world,
        EntityManager em,
        ComponentType type,
        TryReadSequenceGuid read,
        string _,
        out string name)
    {
        name = "";
        try
        {
            var list = Query.Entities(em, includeDisabled: true, includeSystems: false, type);
            for (int i = 0; i < list.Count; i++)
            {
                if (!read(em, list[i], out int hash) || hash == 0) continue;
                if (!PrefabLookup.TryGetEntity(world, new PrefabGUID(hash), out var ent) || ent == Entity.Null)
                    continue;
                name = hash.ToString();
                return true;
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Effects: Beam(GameRed) walk threw: " + e.Message);
        }

        return false;
    }

    private delegate bool TryReadSequenceGuid(EntityManager em, Entity entity, out int hash);

    private static bool ReadCoffinGuids(EntityManager em, Entity entity, out int hash)
    {
        // GetComponentData<ServantCoffinEffects> is not a known-good
        // generic (interop trap 1); raw ints at the SequenceGUID offsets.
        hash = 0;
        try
        {
            var index = ComponentIndex.IndexOf<ServantCoffinEffects>();
            unsafe
            {
                void* raw = em.GetComponentDataRawRO(entity, index);
                if (raw == null) return false;
                int servant = *(int*)((byte*)raw + 0x20);
                int wake = *(int*)((byte*)raw + 0x2C);
                int convert = *(int*)((byte*)raw + 0x14);
                if (servant != 0) { hash = servant; return true; }
                if (wake != 0) { hash = wake; return true; }
                if (convert != 0) { hash = convert; return true; }
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Effects: ServantCoffinEffects read threw: " + e.Message);
        }

        return false;
    }

    private static bool ReadInvalidPlaceGuid(EntityManager em, Entity entity, out int hash)
    {
        // BlueprintData has bools so it is not C# unmanaged.
        hash = 0;
        try
        {
            var index = ComponentIndex.IndexOf<BlueprintData>();
            unsafe
            {
                void* raw = em.GetComponentDataRawRO(entity, index);
                if (raw == null) return false;
                int invalid = *(int*)((byte*)raw + 0x10);
                int place = *(int*)((byte*)raw + 0xC);
                if (invalid != 0) { hash = invalid; return true; }
                if (place != 0) { hash = place; return true; }
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Effects: BlueprintData sequence read threw: " + e.Message);
        }

        return false;
    }

    private static bool ReadArenaBeamGuid(EntityManager em, Entity entity, out int hash)
    {
        // SequenceGUID BeamSequenceGUID @ 0x8.
        hash = 0;
        try
        {
            var index = ComponentIndex.IndexOf<CastleArenaFlagBuff_Client>();
            unsafe
            {
                void* raw = em.GetComponentDataRawRO(entity, index);
                if (raw == null) return false;
                int guid = *(int*)((byte*)raw + 8);
                if (guid != 0) { hash = guid; return true; }
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Effects: CastleArenaFlagBuff_Client read threw: " + e.Message);
        }

        return false;
    }

    private static bool TryResolveBuffPrefab(World world, string name, out Entity prefab)
    {
        prefab = Entity.Null;
        if (int.TryParse(name, out int hash))
        {
            if (PrefabLookup.TryGetEntity(world, new PrefabGUID(hash), out prefab) && prefab != Entity.Null)
                return true;
        }

        if (PrefabLookup.TryGetGuidByName(world, name, out var guid)
            && PrefabLookup.TryGetEntity(world, guid, out prefab)
            && prefab != Entity.Null)
            return true;

        return false;
    }

    private static bool TryResolveSequencePrefab(World world, EntityManager em, Entity buffPrefab, out Entity seqPrefab)
    {
        seqPrefab = Entity.Null;
        try
        {
            if (buffPrefab.Has<SequenceGUID>(em))
            {
                var g = em.GetComponentData<SequenceGUID>(buffPrefab);
                if (g.GuidHash != 0 && PrefabLookup.TryGetEntity(world, g.ToPrefabGUID(), out seqPrefab) && seqPrefab != Entity.Null)
                    return true;
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Effects: SequenceGUID resolve: " + e.Message);
        }

        // BuffSequence is a buffer. GetBuffer<T> hard-crashes when the IL2CPP instantiation
        // is missing (interop trap 1). HasComponent via ComponentIndex is the safe presence
        // check; a prefab without the buffer returns false (disabled handle, one log line).
        // When the buffer is present, walk it through GetBufferRawRO — never GetBuffer<T>.
        try
        {
            if (em.HasComponent(buffPrefab, ComponentIndex.Of<BuffSequence>(ComponentType.AccessMode.ReadOnly)))
            {
                if (TryReadBuffSequenceGuids(em, buffPrefab, world, out seqPrefab) && seqPrefab != Entity.Null)
                    return true;
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Effects: BuffSequence resolve: " + e.Message);
        }

        // Direct sequence prefab (InvalidPlaceSequence / coffin GUIDs): the game's
        // DefaultBlackboardValueElement is the sequencer bake; a Buff prefab is refused
        // (R22 #1: never PlaySequence the buff itself).
        try
        {
            var buffType = ComponentIndex.Of<Buff>(ComponentType.AccessMode.ReadOnly);
            if (em.HasComponent(buffPrefab, buffType))
                return false;
            var bbType = ComponentIndex.Of<DefaultBlackboardValueElement>(ComponentType.AccessMode.ReadOnly);
            if (em.HasComponent(buffPrefab, bbType))
            {
                seqPrefab = buffPrefab;
                return true;
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Effects: sequence-prefab resolve: " + e.Message);
        }

        return false;
    }

    /// <summary>
    /// <b>R30 finding 2.</b> This walk used to stride <c>24</c> over a <b>16</b>-byte element,
    /// behind a comment ("Condition BlobAssetReference at 8 (16)") that cited no dump line and
    /// got the field size wrong: <c>BlobAssetReference&lt;T&gt;</c> is one
    /// <c>BlobAssetReferenceData</c>, which is a union of <c>byte* m_Ptr</c> and
    /// <c>long m_Align8Union</c> both at <c>0x0</c> — eight bytes. At <c>i = 1</c> the old loop
    /// read the low dword of element 1's <c>Condition</c> <em>pointer</em> as a prefab guid hash
    /// and fed a live heap address to <c>PrefabLookup</c>; from <c>i = 2</c> it read past the
    /// buffer entirely. A buff prefab with several <c>BuffSequence</c> entries resolved the
    /// wrong sequence or none — the ".beam link drew nothing" class of bug.
    ///
    /// <para>The stride, the offsets and the bounds now come from
    /// <see cref="BuffSequenceLayout"/>, which the test project compiles, and the length is
    /// checked against the buffer extent rather than trusted. The refusal is per call: a buff
    /// prefab with a garbage length is skipped, not a switch for the module (R30 finding 9).</para>
    /// </summary>
    private static unsafe bool TryReadBuffSequenceGuids(
        EntityManager em,
        Entity buffPrefab,
        World world,
        out Entity seqPrefab)
    {
        seqPrefab = Entity.Null;
        if (!BuffSequenceLayout.WalkAllowed()) return false;
        var typeIndex = ComponentIndex.IndexOf<BuffSequence>();
        void* raw = em.GetBufferRawRO(buffPrefab, typeIndex);
        if (raw == null) return false;
        int length = em.GetBufferLength(buffPrefab, typeIndex);
        var verdict = BuffSequenceLayout.Classify(length);
        if (verdict == LineRendererBufferLayout.LengthVerdict.TooSmall) return false;
        if (verdict != LineRendererBufferLayout.LengthVerdict.Ok)
        {
            if (!_buffSequenceLengthRefusedLogged)
            {
                _buffSequenceLengthRefusedLogged = true;
                Plugin.Logger?.LogWarning(BuffSequenceLayout.LengthRefusedLog(length));
            }

            return false;
        }

        long total = BuffSequenceLayout.TotalBytes(length);
        if (total <= 0L || total > int.MaxValue) return false;
        var buffer = new ReadOnlySpan<byte>(raw, (int)total);

        for (int i = 0; i < length; i++)
        {
            if (!BuffSequenceLayout.TryReadGuidHash(buffer, i, length, out int hash)) break;
            if (hash == 0) continue;
            var g = new SequenceGUID(hash);
            if (PrefabLookup.TryGetEntity(world, g.ToPrefabGUID(), out seqPrefab) && seqPrefab != Entity.Null)
                return true;
        }

        return false;
    }

    /// <summary>One-shot flag for the <see cref="BuffSequenceLayout.LengthRefusedLog"/> line.</summary>
    private static bool _buffSequenceLengthRefusedLogged;

    private static HashSet<int> SnapshotDriverIds()
    {
        var ids = new HashSet<int>();
        ForEach<BeamEmitter>(b => TryAddId(ids, b.gameObject));
        ForEach<HybridLineRendererComponent>(h => TryAddId(ids, h.gameObject));
        ForEach<VisualEffect>(v => TryAddId(ids, v.gameObject));
        return ids;
    }

    internal static GameObject? FindNewDriver(HashSet<int> before) =>
        SequenceDriverLookup.FindFallback(before);

    internal static void RememberBoundDriver(GameObject? go, UnityBorrowedObject owner)
    {
        if (go != null)
            SequenceDriverLookup.Claim(go, owner);
    }

    internal static void ForgetBoundDriver(GameObject? go, UnityBorrowedObject owner) =>
        SequenceDriverLookup.Release(go, owner);

    private static void TryAddId(HashSet<int> ids, GameObject? go)
    {
        if (SequenceDriverLookup.TryInstanceId(go, out int id))
            ids.Add(id);
    }

    // ---- catalog helpers -------------------------------------------------------------------

    private static Sprite? FindSprite(string name)
    {
        Sprite? exact = null;
        Sprite? contains = null;
        ForEach<Image>(img =>
        {
            if (exact != null) return;
            if (EffectOwnership.IsOwned(SafeName(img.gameObject))) return;
            try
            {
                var sp = img.sprite;
                if (sp == null) return;
                string n = sp.name ?? "";
                if (n.Equals(name, StringComparison.OrdinalIgnoreCase)) exact = sp;
                else if (contains == null && n.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0) contains = sp;
            }
            catch { /* */ }
        });
        if (exact != null) return exact;
        ForEachObject<Sprite>(sp =>
        {
            if (exact != null) return;
            string n = sp.name ?? "";
            if (n.Equals(name, StringComparison.OrdinalIgnoreCase)) exact = sp;
            else if (contains == null && n.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0) contains = sp;
        });
        return exact ?? contains;
    }

    private static bool TryFindParticle(string name, out ParticleSystem? ps)
    {
        ps = null;
        ParticleSystem? exact = null;
        ParticleSystem? contains = null;
        ForEach<ParticleSystem>(p =>
        {
            if (exact != null) return;
            if (EffectOwnership.IsOwned(SafeName(p.gameObject))) return;
            string n = SafeName(p.gameObject);
            if (n.Equals(name, StringComparison.OrdinalIgnoreCase)) exact = p;
            else if (contains == null && n.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0) contains = p;
        });
        ps = exact ?? contains;
        return ps != null;
    }

    private static bool TryAddVignette(VolumeProfile profile)
    {
        try
        {
            var added = profile.Add(Il2CppType.Of<Vignette>(), true);
            var v = added == null ? null : added.TryCast<Vignette>();
            if (v == null) return false;
            try { v.SetAllOverridesTo(true); } catch { /* */ }
            try { v.active = true; } catch { /* */ }
            return true;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Effects: Add Vignette: " + e.Message);
            return false;
        }
    }

    private static bool TryAddColorAdjustments(VolumeProfile profile)
    {
        try
        {
            var added = profile.Add(Il2CppType.Of<ColorAdjustments>(), true);
            var a = added == null ? null : added.TryCast<ColorAdjustments>();
            if (a == null) return false;
            try { a.SetAllOverridesTo(true); } catch { /* */ }
            try { a.active = true; } catch { /* */ }
            return true;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Effects: Add ColorAdjustments: " + e.Message);
            return false;
        }
    }

    // ---- GO helpers --------------------------------------------------------------------------

    private static GameObject? EnsureRoot(string rootName)
    {
        GameObject? found = null;
        ForEach<Transform>(t =>
        {
            if (found != null) return;
            try
            {
                if ((t.gameObject.name ?? "").Equals(rootName, StringComparison.OrdinalIgnoreCase)
                    && t.parent == null)
                    found = t.gameObject;
            }
            catch { /* */ }
        });
        if (IsAlive(found)) return found;
        try
        {
            var go = new GameObject(rootName);
            UnityEngine.Object.DontDestroyOnLoad(go);
            return go;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("Effects: EnsureRoot '" + rootName + "': " + e);
            return null;
        }
    }

    private static bool TryPickUnusedLayer(out int layer)
    {
        layer = -1;
        var occupied = OccupiedRendererLayers();
        for (int i = 31; i >= 8; i--)
        {
            string n;
            try { n = LayerMask.LayerToName(i) ?? ""; }
            catch { n = "x"; }
            if (!string.IsNullOrEmpty(n)) continue;
            if (occupied.Contains(i)) continue;
            layer = i;
            return true;
        }

        return false;
    }

    private static HashSet<int> OccupiedRendererLayers()
    {
        var set = new HashSet<int>();
        ForEach<Renderer>(r =>
        {
            try
            {
                if (r != null && r.gameObject != null)
                    set.Add(r.gameObject.layer);
            }
            catch { /* */ }
        });
        return set;
    }

    private static void PlaceRect(GameObject go, Vector2 anchored, float size)
    {
        try
        {
            var rt = go.GetComponent<RectTransform>() ?? go.AddComponent<RectTransform>();
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = anchored;
            rt.sizeDelta = new Vector2(size, size);
        }
        catch { /* */ }
    }

    private static void Stretch(GameObject go)
    {
        try
        {
            var rt = go.GetComponent<RectTransform>() ?? go.AddComponent<RectTransform>();
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }
        catch { /* */ }
    }

    private static T? TryAdd<T>(GameObject go) where T : Component
    {
        try { return go.AddComponent<T>(); }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Effects: AddComponent<" + typeof(T).Name + ">: " + e.Message);
            return null;
        }
    }

    private static bool TryDestroyImmediate(GameObject? go)
    {
        if (!IsAlive(go) || go == null) return false;
        try
        {
            UnityEngine.Object.DestroyImmediate(go);
            return true;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Effects: DestroyImmediate: " + e.Message);
            try { UnityEngine.Object.Destroy(go); } catch { /* */ }
            return false;
        }
    }

    private static void SafeRelease(RenderTexture? rt)
    {
        if (rt == null) return;
        try
        {
            rt.Release();
            UnityEngine.Object.Destroy(rt);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Effects: RenderTexture.Release: " + e.Message);
        }
    }

    internal static bool IsAlive(UnityEngine.Object? obj)
    {
        try { return obj != null && obj.Pointer != IntPtr.Zero; }
        catch { return false; }
    }

    /// <summary>
    /// Evidence 99: the clone landed <c>activeSelf=False inHierarchy=False</c> under our
    /// root, so nothing rendered. Activate once after parenting. If the game later
    /// deactivates because the sequence failed validation, fix that cause (marker
    /// LocalToWorld) rather than fighting SetActive every frame.
    /// </summary>
    internal static void EnsureDriverActiveOnce(GameObject go, ref bool activated)
    {
        if (activated) return;
        bool self = false;
        bool hierarchy = false;
        try { self = go.activeSelf; } catch { /* */ }
        try { hierarchy = go.activeInHierarchy; } catch { /* */ }
        if (self && hierarchy)
        {
            activated = true;
            return;
        }

        try { go.SetActive(true); }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Effects: sequence driver SetActive threw: " + e.Message);
            return;
        }

        activated = true;
        Plugin.Logger?.LogInfo(
            "Effects: sequence driver '" + SafeName(go)
            + "' SetActive(true) (was activeSelf=" + self + " inHierarchy=" + hierarchy + ")");
    }

    internal static string SafeName(GameObject? go)
    {
        try { return go != null ? (go.name ?? "") : ""; }
        catch { return ""; }
    }

    private static string SafeSpriteName(Image img)
    {
        try
        {
            var sp = img.sprite;
            return sp == null ? "" : (sp.name ?? "");
        }
        catch { return ""; }
    }

    private static bool NameStarts(GameObject? go, string prefix) =>
        SafeName(go).StartsWith(prefix, StringComparison.OrdinalIgnoreCase);

    private static bool ParentStarts(GameObject? go, string prefix)
    {
        try
        {
            var t = go != null ? go.transform : null;
            while (t != null)
            {
                if ((t.name ?? "").StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
                t = t.parent;
            }
        }
        catch { /* */ }
        return false;
    }

    private static string SanitizeChild(string name)
    {
        var s = EffectOwnership.Sanitize(name);
        return s.Length <= 48 ? s : s.Substring(0, 48);
    }

    private static void TryAddName(List<string> names, HashSet<string> seen, string? value, string? filter)
    {
        if (string.IsNullOrEmpty(value)) return;
        if (!string.IsNullOrEmpty(filter) && value.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
            return;
        if (!seen.Add(value)) return;
        names.Add(value);
    }

    private static void ForEach<T>(Action<T> body) where T : Component
    {
        try
        {
            var type = Il2CppType.Of<T>();
            if (type == null) return;
            var arr = UnityEngine.Object.FindObjectsOfType(type, true);
            if (arr == null) return;
            for (int i = 0; i < arr.Length; i++)
            {
                T? c = null;
                try { c = arr[i] == null ? null : arr[i].TryCast<T>(); }
                catch { continue; }
                if (c == null) continue;
                try { body(c); }
                catch (Exception e) { Plugin.Logger?.LogWarning("Effects: ForEach " + typeof(T).Name + ": " + e.Message); }
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Effects: FindObjectsOfType<" + typeof(T).Name + ">: " + e.Message);
        }
    }

    private static void ForEachObject<T>(Action<T> body) where T : UnityEngine.Object
    {
        try
        {
            var type = Il2CppType.Of<T>();
            if (type == null) return;
            var arr = Resources.FindObjectsOfTypeAll(type);
            if (arr == null) return;
            for (int i = 0; i < arr.Length; i++)
            {
                T? c = null;
                try { c = arr[i] == null ? null : arr[i].TryCast<T>(); }
                catch { continue; }
                if (c == null) continue;
                try { body(c); }
                catch { /* */ }
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Effects: FindObjectsOfTypeAll<" + typeof(T).Name + ">: " + e.Message);
        }
    }

    /// <summary>
    /// Tint every color surface under <paramref name="go"/>. Only ever called with a
    /// GameObject this process instantiated: <paramref name="owned"/> false refuses the whole
    /// pass and logs once (R26 finding 3). <c>Renderer.material</c>, <c>ParticleSystem.main.startColor</c>
    /// and <c>BeamEmitter.StartColor</c> all mutate state that <c>ParticlePool.ReturnInstance</c>
    /// hands to the next user of that clone — vanilla or ours.
    /// </summary>
    internal static bool TintGameObject(
        GameObject go,
        EffectColor color,
        List<Material>? instantiated,
        bool owned,
        AssetKind kind = AssetKind.Particle)
    {
        if (!CloneWritePolicy.AllowsMaterialWrites(kind, owned))
        {
            LogCloneTintSkippedOnce(go);
            return false;
        }

        TintOwnedGameObject(go, color, instantiated);
        return true;
    }

    private static readonly HashSet<string> LoggedCloneTintSkips = new(StringComparer.Ordinal);

    internal static void ResetCloneTintLog() => LoggedCloneTintSkips.Clear();

    private static void LogCloneTintSkippedOnce(GameObject? go)
    {
        string name;
        try { name = go != null ? (go.name ?? "?") : "?"; }
        catch { name = "?"; }
        if (!LoggedCloneTintSkips.Add(name)) return;
        Plugin.Logger?.LogInfo(CloneWritePolicy.FormatSkippedTintLog(name));
    }

    private static void TintOwnedGameObject(GameObject go, EffectColor color, List<Material>? instantiated)
    {
        var unity = (Color)color;
        ForEachOn(go, (Image img) => { try { img.color = unity; } catch { /* */ } });
        ForEachOn(go, (ParticleSystem ps) =>
        {
            try { ps.main.startColor = new ParticleSystem.MinMaxGradient(unity); }
            catch { /* */ }
            TintRenderers(go, unity, instantiated);
        });
        ForEachOn(go, (BeamEmitter beam) => TintBeam(beam, unity));
        ForEachOn(go, (HybridLineRendererComponent h) => TintLine(h, unity, instantiated));
        ForEachOn(go, (VisualEffect vfx) =>
        {
            for (int i = 0; i < VfxColorNames.Length; i++)
            {
                if (TryVfxColor(vfx, VfxColorNames[i], unity)) break;
            }
        });
        ForEachOn(go, (Volume vol) => TintVolume(vol, unity));
    }

    /// <summary>
    /// Remove <c>HybridLineRendererComponent</c> from a copy we own. On a real sequence entity
    /// that component is the sink for
    /// <c>ParticleLifetimeSystem.UpdateLineRendererPositionElement_Job</c>
    /// and its own <c>CalculateThrowArc</c> rewrites
    /// <c>Positions</c>; on our copy it can only fight our
    /// <c>SetPosition</c> writes.
    /// </summary>
    internal static int StripHybridLineDrivers(GameObject go)
    {
        int removed = 0;
        ForEachOn(go, (HybridLineRendererComponent h) =>
        {
            try
            {
                UnityEngine.Object.DestroyImmediate(h);
                removed++;
            }
            catch
            {
                try
                {
                    UnityEngine.Object.Destroy(h);
                    removed++;
                }
                catch { /* */ }
            }
        });

        return removed;
    }

    private static void ForEachOn<T>(GameObject go, Action<T> body) where T : Component
    {
        try
        {
            var all = go.GetComponentsInChildren<T>(true);
            if (all == null) return;
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i] == null) continue;
                try { body(all[i]); } catch { /* */ }
            }
        }
        catch { /* */ }
    }

    private static void TintBeam(BeamEmitter beam, Color color)
    {
        bool playing = false;
        try { playing = beam.IsPlaying(); } catch { /* */ }
        if (playing)
        {
            try
            {
                beam._IsPlaying = false;
                beam._ParticleSystemID = TrailSystemID.None;
            }
            catch { /* */ }
        }

        try { beam.StartColor = color; } catch { /* */ }
        if (playing)
        {
            try { beam.Play(); } catch { /* */ }
        }
    }

    private static void TintLine(HybridLineRendererComponent h, Color color, List<Material>? instantiated)
    {
        LineRenderer? lr = null;
        try { lr = h.GetComponent<LineRenderer>(); } catch { /* */ }
        if (lr == null) return;
        try { lr.startColor = color; lr.endColor = color; } catch { /* */ }
        TintMaterial(lr, color, instantiated);
    }

    private static void TintRenderers(GameObject go, Color color, List<Material>? instantiated)
    {
        try
        {
            var renderers = go.GetComponentsInChildren<Renderer>(true);
            if (renderers == null) return;
            for (int i = 0; i < renderers.Length; i++)
            {
                if (renderers[i] == null) continue;
                TintMaterial(renderers[i], color, instantiated);
            }
        }
        catch { /* */ }
    }

    /// <summary>
    /// Instances <c>Renderer.material</c> and tints it. Callers that own the GO
    /// (particle clones) may Destroy those instances on Release. Sequence pooled
    /// clones must never enter here (evidence 125 magenta wedge).
    /// </summary>
    private static void TintMaterial(Renderer r, Color color, List<Material>? instantiated)
    {
        Material? mat = null;
        try { mat = r.material; } catch { /* */ }
        if (mat == null) return;
        if (instantiated != null)
        {
            // R27 finding 23: this dedup used ReferenceEquals — exactly the wrapper-identity
            // assumption EV12 removed from PoolParentPolicy. Il2CppInterop can front one native
            // Material with two managed wrappers, so both entered the list and Release
            // destroyed the same material twice. Identity is GetInstanceID().
            int matId = PoolParentPolicy.NoInstanceId;
            try { matId = mat.GetInstanceID(); }
            catch { /* */ }

            bool seen = false;
            for (int i = 0; i < instantiated.Count; i++)
            {
                var known = instantiated[i];
                if (known == null) continue;
                int knownId = PoolParentPolicy.NoInstanceId;
                try { knownId = known.GetInstanceID(); }
                catch { continue; }
                if (PoolParentPolicy.SameObject(knownId, matId))
                {
                    seen = true;
                    break;
                }
            }

            if (!seen)
                instantiated.Add(mat);
        }

        try { mat.color = color; } catch { /* */ }
        for (int i = 0; i < MaterialColorProps.Length; i++)
        {
            string p = MaterialColorProps[i];
            try
            {
                if (!mat.HasProperty(p)) continue;
                mat.SetColor(p, color);
                return;
            }
            catch { /* */ }
        }
    }

    private static bool TryVfxColor(VisualEffect vfx, string prop, Color color)
    {
        try
        {
            vfx.SetVector4(prop, new Vector4(color.r, color.g, color.b, color.a));
            return true;
        }
        catch { return false; }
    }

    private static void TintVolume(Volume volume, Color color)
    {
        VolumeProfile? profile = null;
        try { profile = volume.sharedProfile; } catch { /* */ }
        if (profile == null) return;
        try
        {
            var list = profile.components;
            if (list == null) return;
            for (int i = 0; i < list.Count; i++)
            {
                var c = list[i];
                if (c == null) continue;
                try
                {
                    var v = c.TryCast<Vignette>();
                    if (v != null)
                    {
                        try { v.color.overrideState = true; v.color.value = color; } catch { /* */ }
                        continue;
                    }
                }
                catch { /* */ }
                try
                {
                    var a = c.TryCast<ColorAdjustments>();
                    if (a != null)
                    {
                        try { a.colorFilter.overrideState = true; a.colorFilter.value = color; } catch { /* */ }
                    }
                }
                catch { /* */ }
            }
        }
        catch { /* */ }
    }

    internal static void SetVignetteFloats(Volume volume, float intensity, float smoothness)
    {
        VolumeProfile? profile = null;
        try { profile = volume.sharedProfile; } catch { /* */ }
        if (profile == null) return;
        try
        {
            var list = profile.components;
            if (list == null) return;
            for (int i = 0; i < list.Count; i++)
            {
                var c = list[i];
                if (c == null) continue;
                Vignette? v = null;
                try { v = c.TryCast<Vignette>(); } catch { /* */ }
                if (v == null) continue;
                TrySetFloat(v.intensity, intensity);
                TrySetFloat(v.smoothness, smoothness);
            }
        }
        catch { /* */ }
    }

    private static void TrySetFloat(ClampedFloatParameter? p, float value)
    {
        if (p == null) return;
        try { p.overrideState = true; } catch { /* */ }
        try { p.value = value; } catch { /* */ }
    }

    internal static void TryReadVignetteFloats(Volume? volume, out float intensity, out float smoothness)
    {
        intensity = 0f;
        smoothness = 0f;
        if (volume == null) return;
        VolumeProfile? profile = null;
        try { profile = volume.sharedProfile; } catch { /* */ }
        if (profile == null) return;
        try
        {
            var list = profile.components;
            if (list == null) return;
            for (int i = 0; i < list.Count; i++)
            {
                var c = list[i];
                if (c == null) continue;
                Vignette? v = null;
                try { v = c.TryCast<Vignette>(); } catch { /* */ }
                if (v == null) continue;
                try { intensity = v.intensity != null ? v.intensity.value : 0f; } catch { /* */ }
                try { smoothness = v.smoothness != null ? v.smoothness.value : 0f; } catch { /* */ }
                return;
            }
        }
        catch { /* */ }
    }

    /// <summary>
    /// Resolve the default owned-line shader once per process. <c>Shader.Find("HDRP/Unlit")</c>
    /// first (evidence 35, 488: it exists and exposes <c>_UnlitColor</c> / <c>_Color</c> /
    /// <c>_EmissionColor</c>). Each miss is logged; the first hit is cached. Null means
    /// every candidate failed — callers fall back to the prefab material.
    /// </summary>
    internal static Shader? ResolveOwnedLineShader(out string name)
    {
        if (UnlitResolved)
        {
            name = ResolvedUnlitName;
            return ResolvedUnlitShader;
        }

        UnlitResolved = true;
        var candidates = SequenceLateDriver.OwnedLineShaderCandidates;
        for (int i = 0; i < candidates.Length; i++)
        {
            string candidate = candidates[i];
            Shader? shader = null;
            try { shader = Shader.Find(candidate); }
            catch (Exception e)
            {
                Plugin.Logger?.LogWarning("Effects: Shader.Find('" + candidate + "') threw: " + e.Message);
                continue;
            }

            if (shader != null)
            {
                ResolvedUnlitShader = shader;
                ResolvedUnlitName = candidate;
                name = candidate;
                if (i > 0)
                    Plugin.Logger?.LogInfo("Effects: Shader.Find('" + candidates[0] + "') is null; using '" + candidate + "'");
                return shader;
            }

            Plugin.Logger?.LogInfo("Effects: Shader.Find('" + candidate + "') is null");
        }

        ResolvedUnlitShader = null;
        ResolvedUnlitName = "";
        name = "";
        Plugin.Logger?.LogInfo("Effects: sequence owned line no unlit shader; falling back to prefab material");
        return null;
    }

    /// <summary>
    /// HDRP Unlit.shader (com.unity.render-pipelines.high-definition): <c>_SurfaceType</c>
    /// 0=Opaque 1=Transparent; <c>_BlendMode</c> 0=Alpha 1=Additive 2=Premultiply;
    /// <c>_SrcBlend</c>/<c>_DstBlend</c> are the actual blend factors; <c>_ZWrite</c> 0
    /// so the beam is not a depth wall. Keywords <c>_SURFACE_TYPE_TRANSPARENT</c>,
    /// <c>_BLENDMODE_ADD</c>, <c>_ENABLE_FOG_ON_TRANSPARENT</c>. <c>renderQueue</c> 3000
    /// (Transparent) so the line is not hidden by the ground. Properties a fallback
    /// shader lacks are skipped via <c>HasProperty</c>.
    /// <c>HDMaterial.ValidateMaterial</c> is on the IL2CPP interop
    /// (<c>UnityEngine.Rendering.HighDefinition.HDMaterial</c>) but Unity's HDRP C#
    /// source keeps the useful body under <c>UNITY_EDITOR</c> — do not call it at
    /// runtime. This property/keyword set is the runtime-safe equivalent.
    /// </summary>
    internal static void ConfigureUnlitTransparentAdditive(Material mat)
    {
        if (mat == null) return;
        TrySetMatFloat(mat, "_SurfaceType", 1f);
        TrySetMatFloat(mat, "_BlendMode", 1f);
        TrySetMatFloat(mat, "_SrcBlend", 1f);
        TrySetMatFloat(mat, "_DstBlend", 1f);
        TrySetMatFloat(mat, "_AlphaSrcBlend", 1f);
        TrySetMatFloat(mat, "_AlphaDstBlend", 1f);
        TrySetMatFloat(mat, "_ZWrite", 0f);
        TrySetMatFloat(mat, "_TransparentZWrite", 0f);
        try { mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT"); } catch { /* */ }
        try { mat.EnableKeyword("_BLENDMODE_ADD"); } catch { /* */ }
        try { mat.EnableKeyword("_ENABLE_FOG_ON_TRANSPARENT"); } catch { /* */ }
        try { mat.DisableKeyword("_BLENDMODE_ALPHA"); } catch { /* */ }
        try { mat.DisableKeyword("_BLENDMODE_PRE_MULTIPLY"); } catch { /* */ }
        try { mat.SetOverrideTag("RenderType", "Transparent"); } catch { /* */ }
        try { mat.renderQueue = 3000; } catch { /* */ }
    }

    internal static bool TryShaderIsSupported(Shader? shader)
    {
        if (shader == null) return false;
        try { return shader.isSupported; }
        catch { return false; }
    }

    private static void TrySetMatFloat(Material mat, string prop, float value)
    {
        try
        {
            if (!mat.HasProperty(prop)) return;
            mat.SetFloat(prop, value);
        }
        catch { /* */ }
    }

    internal static void EnsurePoseTypes()
    {
        if (PoseTypesReady) return;
        TranslationRo = ComponentIndex.Of<Translation>(ComponentType.AccessMode.ReadOnly);
        LocalToWorldRo = ComponentIndex.Of<LocalToWorld>(ComponentType.AccessMode.ReadOnly);
        PoseTypesReady = true;
    }

    /// <summary>
    /// Read world pose through cached <see cref="ComponentType"/>s. No allocation.
    /// Callers must not wrap this in a per-frame try/catch: <c>Exists</c> false is
    /// the lost-entity signal. Prefers <c>LocalToWorld.Position</c>, then
    /// <c>Translation.Value</c>. Both <c>GetComponentData&lt;T&gt;</c> instantiations
    /// are known-good on this client.
    /// </summary>
    internal static bool TryReadEntityPose(World? world, int index, int version, out EffectVec3 pos)
    {
        pos = default;
        if (world == null || !world.IsCreated) return false;
        if (index == 0 && version == 0) return false;
        EnsurePoseTypes();
        var em = world.EntityManager;
        var entity = new Entity { Index = index, Version = version };
        if (!em.Exists(entity)) return false;
        if (em.HasComponent(entity, LocalToWorldRo))
        {
            var ltw = em.GetComponentData<LocalToWorld>(entity);
            var p = ltw.Position;
            pos = new EffectVec3(p.x, p.y, p.z);
            return true;
        }

        if (em.HasComponent(entity, TranslationRo))
        {
            var t = em.GetComponentData<Translation>(entity);
            var p = t.Value;
            pos = new EffectVec3(p.x, p.y, p.z);
            return true;
        }

        return false;
    }

    internal static EffectColor ReadColor(GameObject go)
    {
        try
        {
            var img = go.GetComponent<Image>();
            if (img != null) return img.color;
        }
        catch { /* */ }
        try
        {
            var ps = go.GetComponent<ParticleSystem>() ?? go.GetComponentInChildren<ParticleSystem>();
            if (ps != null)
            {
                var g = ps.main.startColor;
                try { return g.color; } catch { return g.colorMax; }
            }
        }
        catch { /* */ }
        try
        {
            var beam = go.GetComponent<BeamEmitter>();
            if (beam != null) return beam.StartColor;
        }
        catch { /* */ }

        return default;
    }
}

internal sealed class UnityBorrowedObject : IRuntimeObject
{
    private bool _released;
    private bool _sequencePlayed;
    private bool _loggedBeamPath;
    private World? _sequenceWorld;
    private Entity _sequencePrefab;
    private Entity _sequenceEntity;
    private Entity _startMarker;
    private Entity _endMarker;
    private bool _boundSequenceDataSource;
    private HashSet<int>? _seenDrivers;
    private GameObject? _root;
    private EffectVec3 _beamStart;
    private EffectVec3 _beamEnd;
    private EffectColor _pendingColor;
    private bool _hasPendingColor;
    private bool _tintAppliedToDriver;
    private bool _loggedDriverInspect;
    private bool _loggedLateInspect;
    private bool _loggedVerdict;
    private bool _loggedSummary;
    private bool _loggedLineReadback;
    private bool _loggedRootScale;
    private bool _driverActivated;
    /// <summary>
    /// Job FEFF2. True once a bind has read the clone's parent back and seen our own root, so
    /// the late-write ownership check can require it instead of inferring the recycle from a
    /// transient <c>!activeInHierarchy</c>. Cleared with every drop of the driver reference.
    /// </summary>
    private bool _boundUnderRoot;
    private bool _loggedOwnershipUnprovable;
    private Transform? _poolParent;
    private GameObject? _ownedLineGo;
    private LineRenderer? _ownedLine;
    private Material? _ownedLineMaterial;
    private bool _ownedLineLogged;
    private bool _ownedLineStyleCopied;
    private bool _ownedLineStyleLogged;
    private bool _ownedCountSet;
    private bool _ownedLineWidthSet;
    private bool _usePrefabLineMaterial;
    private bool _copyGameLineIfSuitable;
    private bool _driverMaterialHasGlow;
    private bool _loggedGlow;
    private bool _loggedDrawPath;
    private bool _loggedTrailSkip;
    private BeamStyle _beamStyle;
    private GameObject? _glowLineGo;
    private LineRenderer? _glowLine;
    private Material? _glowLineMaterial;
    private GameObject? _gameLineGo;
    private LineRenderer? _gameLine;
    private Material? _gameLineMaterial;
    private string _gameLinePrefabName = "";
    private bool _gameLineTried;
    private bool _loggedGameLine;
    private bool _loggedPoolParentIsRoot;
    private bool _explicitTint;
    private DriverRecycleState _recycle;
    private float _glowWidth;
    private float _glowAlpha = BeamCatalog.DefaultGlowAlpha;
    private string _drawPath = "";
    private EffectColor _copiedMaterialColor;
    private bool _hasCopiedMaterialColor;
    private EffectColor _lastTintedColor;
    private int _lineRendererBufferWrites;
    private float _ownedLineWidth = SequenceLateDriver.OwnedLineDefaultWidth;
    private string _ownedLineMaterialName = "";
    private Vector3 _ownedPos0;
    private Vector3 _ownedPos1;
    private bool _trackStart;
    private bool _trackEnd;
    private bool _trackEndFixed;
    private bool _startFrozen;
    private bool _endFrozen;
    private bool _loggedStartLost;
    private bool _loggedEndLost;
    private int _trackStartIndex;
    private int _trackStartVersion;
    private int _trackEndIndex;
    private int _trackEndVersion;
    private EffectVec3 _trackStartOffset;
    private EffectVec3 _trackEndOffset;
    private EffectVec3 _trackEndWorld;
    private UnityEffectRuntime.HybridDriveState _hybridDrive;
    private int _lateWriteCount;
    private int _lastLateWriteFrame;
    private int _sequencerOverwriteCount;
    private string _lateWriteVerdict = "";
    private readonly List<Material> _tintedMaterials = new();
    private readonly TransformWriteState _transformWrites = new();
    private float _intensity;
    private float _smoothness;
    private float _uniformScale = 1f;
    private float _baseStartSize = 1f;
    private bool _capturedBaseStartSize;
    private bool _loggedScalePath;

    private UnityBorrowedObject(AssetKind kind, string name, GameObject? go, bool owns)
    {
        Kind = kind;
        Name = name;
        GameObject = go;
        OwnsGameObject = owns;
    }

    internal static UnityBorrowedObject ForImage(string name, GameObject go, Image img) =>
        new(AssetKind.Image, name, go, owns: true) { Image = img };

    internal static UnityBorrowedObject ForParticle(string name, GameObject go, ParticleSystem ps) =>
        new(AssetKind.Particle, name, go, owns: true) { Particle = ps };

    internal static UnityBorrowedObject ForVolume(string name, GameObject go, Volume volume, VolumeProfile profile) =>
        new(AssetKind.Volume, name, go, owns: true) { Volume = volume, Profile = profile };

    internal static UnityBorrowedObject ForSequence(string name, World world, Entity playPrefab, GameObject root) =>
        new(AssetKind.Sequence, name, go: null, owns: false)
        {
            _sequenceWorld = world,
            _sequencePrefab = playPrefab,
            _root = root,
        };

    public AssetKind Kind { get; }

    public string Name { get; }

    internal GameObject? GameObject { get; private set; }

    internal bool OwnsGameObject { get; }

    internal Image? Image { get; init; }

    internal ParticleSystem? Particle { get; init; }

    internal Volume? Volume { get; init; }

    internal VolumeProfile? Profile { get; init; }

    internal Canvas? OverlayCanvas { get; set; }

    public bool Alive
    {
        get
        {
            if (_released) return false;
            if (Kind == AssetKind.Sequence)
                return _sequenceWorld != null && _sequenceWorld.IsCreated;
            try { return GameObject != null && GameObject.Pointer != IntPtr.Zero; }
            catch { return false; }
        }
    }

    public bool HasDriver
    {
        get
        {
            if (Kind != AssetKind.Sequence)
                return Alive;
            try { return GameObject != null && GameObject.Pointer != IntPtr.Zero; }
            catch { return false; }
        }
    }

    public bool HasOwnedLine =>
        Kind == AssetKind.Sequence && _ownedLineGo != null && UnityEffectRuntime.IsAlive(_ownedLineGo);

    public float LineWidth
    {
        get => _ownedLineWidth;
        set
        {
            _ownedLineWidth = value;
            _ownedLineWidthSet = true;
            ApplyOwnedLineWidth();
        }
    }

    public bool UsePrefabLineMaterial
    {
        get => _usePrefabLineMaterial;
        set
        {
            _usePrefabLineMaterial = value;
            if (value)
                CopyOwnedLineStyleFromDriver();
        }
    }

    public string OwnedLineMaterialName => _ownedLineMaterialName ?? "";

    public BeamStyle BeamStyle => _beamStyle;

    public bool HasGlowLine =>
        Kind == AssetKind.Sequence && _glowLineGo != null && UnityEffectRuntime.IsAlive(_glowLineGo);

    public float GlowWidth
    {
        get => _glowWidth;
        set
        {
            _glowWidth = value;
            if (value <= 0f)
                DestroyGlowLine();
            else
                EnsureGlowLine();
        }
    }

    public float GlowAlpha
    {
        get => _glowAlpha;
        set
        {
            _glowAlpha = value;
            ApplyGlowColor();
        }
    }

    public string DrawPath => _drawPath ?? "";

    public string GameLinePrefabName => _gameLinePrefabName ?? "";

    internal bool HasGameLine => _gameLine != null && UnityEffectRuntime.IsAlive(_gameLineGo);

    public bool Recycled => _recycle.Recycled;

    public bool LineVisible
    {
        get
        {
            // R27 finding 11: a recycle no longer hides the owned line, only the clone-derived
            // borrowed one, so the beam stays visible until Dispose.
            if (_released) return false;
            return HasOwnedLine || (HasGameLine && !_recycle.GameLineHidden);
        }
    }

    public EffectColor CopiedMaterialColor => _copiedMaterialColor;

    public EffectColor LastTintedColor => _lastTintedColor;

    public int LineRendererBufferWrites => _lineRendererBufferWrites;

    public void ApplyBeamPreset(BeamStyle style)
    {
        _beamStyle = style;
        if (style == BeamStyle.Thin)
        {
            _copyGameLineIfSuitable = false;
            _usePrefabLineMaterial = false;
            if (!_hasPendingColor)
            {
                _pendingColor = SequenceLateDriver.OwnedLineDefaultTint;
                _hasPendingColor = true;
            }

            ApplyOwnedLineColor();
            return;
        }

        _copyGameLineIfSuitable = true;
        if (!_hasPendingColor)
        {
            _pendingColor = BeamCatalog.DefaultTint(style);
            _hasPendingColor = true;
        }

        // EV12: prefer a copy of a vanilla beam prefab whose body really is a LineRenderer
        // (evidence 130). It draws with the game's own material and it is ours to write.
        EnsureGameLine(style);
        if (HasGameLine)
        {
            ApplyOwnedLineColor();
            ApplyGameLineColor();
            return;
        }

        if (!_driverMaterialHasGlow && _glowWidth <= 0f)
        {
            _glowWidth = BeamCatalog.DefaultGlowWidth;
            _glowAlpha = BeamCatalog.DefaultGlowAlpha;
        }

        CopyOwnedLineStyleFromDriver();
        ApplyOwnedLineColor();
        EnsureGlowLine();
    }

    public void TrackStartEntity(int index, int version, EffectVec3 offset)
    {
        _trackStart = index != 0 || version != 0;
        _trackStartIndex = index;
        _trackStartVersion = version;
        _trackStartOffset = offset;
        _startFrozen = false;
        _loggedStartLost = false;
        if (_trackStart)
            SeedTrackedStart();
    }

    public void TrackEndEntity(int index, int version, EffectVec3 offset)
    {
        _trackEnd = index != 0 || version != 0;
        _trackEndFixed = false;
        _trackEndIndex = index;
        _trackEndVersion = version;
        _trackEndOffset = offset;
        _endFrozen = false;
        _loggedEndLost = false;
        if (_trackEnd)
            SeedTrackedEnd();
    }

    public void TrackEndWorld(EffectVec3 world)
    {
        _trackEnd = false;
        _trackEndFixed = true;
        _trackEndWorld = world;
        _endFrozen = false;
        _loggedEndLost = false;
        _beamEnd = world;
    }

    public void ClearEndpointTracking()
    {
        _trackStart = false;
        _trackEnd = false;
        _trackEndFixed = false;
        _startFrozen = false;
        _endFrozen = false;
    }

    public string DriverName
    {
        get
        {
            try { return GameObject != null ? (GameObject.name ?? "") : ""; }
            catch { return ""; }
        }
    }

    public int SequenceEntityId => _sequenceEntity != Entity.Null ? _sequenceEntity.Index : 0;

    public int EndMarkerId => _endMarker != Entity.Null ? _endMarker.Index : 0;

    public bool TintAppliedToDriver => _tintAppliedToDriver;

    public bool DriverActive { get; private set; }

    public string LastTransformWritePath =>
        _transformWrites.AnySucceeded || _sequencePlayed
            ? TransformWritePath.GenericSetComponentData
            : "";

    public int TransformWriteWarnings => _transformWrites.WarningCount;

    public bool HybridLastWrite { get; private set; }

    public int LateWriteCount => _lateWriteCount;

    public int LastLateWriteFrame => _lastLateWriteFrame;

    public int SequencerOverwriteCount => _sequencerOverwriteCount;

    public string LateWriteVerdict => _lateWriteVerdict ?? "";

    public bool TryBindDriver() => TryBindSequenceDriver();

    /// <summary>
    /// Play the buff sequence at <see cref="BeamStart"/> with identity rotation (spike step 2).
    /// Markers are created now; <c>SequenceInstance</c> is bound only once the driver clone
    /// exists (evidence 96). Later Start/End writes re-pose the play entity and move those
    /// markers. Hybrid SetPosition is the last write of the frame even after that bind.
    /// </summary>
    internal bool EnsureSequencePlayed()
    {
        if (_released) return false;
        if (_sequencePlayed)
        {
            TryBindSequenceDriver();
            return Alive;
        }

        if (_sequenceWorld == null || !_sequenceWorld.IsCreated) return false;
        var played = UnityEffectRuntime.PlaySequenceAtPose(
            _sequenceWorld, _sequencePrefab, _beamStart, _beamEnd, _transformWrites);
        _sequencePlayed = true;
        _sequenceEntity = played.Sequence;
        _startMarker = played.StartMarker;
        _endMarker = played.EndMarker;
        _boundSequenceDataSource = false;
        _seenDrivers = played.SeenDrivers;
        EnsureOwnedLine("play");
        if (played.Driver != null)
            BindDriverGo(played.Driver);
        SequenceLateDriver.Watch(this);
        ApplyPending();
        return Alive;
    }

    private void BindDriverGo(GameObject driver)
    {
        RememberPoolParent(driver);
        GameObject = driver;
        _boundUnderRoot = false;
        UnityEffectRuntime.RememberBoundDriver(driver, this);
        if (_root != null)
        {
            try
            {
                if (!_root.activeSelf)
                    _root.SetActive(true);
            }
            catch { /* */ }
            // Reparent while the handle lives so Host/inspect can find it; RestorePoolParent
            // on dispose (ReturnInstance parents under _ParticleParent —
            // never DestroyImmediate a pooled clone).
            try
            {
                driver.transform.SetParent(_root.transform, worldPositionStays: true);
                // Verify rather than assume (the UiInputCapture rule): read the parent back,
                // so the late-write ownership check has a positive proof the game itself
                // maintains — the sequencer's take/return reparents the clone away from here.
                var bound = driver.transform.parent;
                _boundUnderRoot = bound != null
                    && PoolParentPolicy.SameObject(InstanceIdOf(bound.gameObject), RootInstanceId);
            }
            catch { _boundUnderRoot = false; }
        }

        UnityEffectRuntime.EnsureDriverActiveOnce(driver, ref _driverActivated);
        DriverActive = _driverActivated;
        if (_usePrefabLineMaterial || _copyGameLineIfSuitable)
            CopyOwnedLineStyleFromDriver();
    }

    /// <summary>
    /// Another handle claimed this pooled GO. Drop our reference without destroying the
    /// clone or logging a dispose summary (the new owner will write it).
    /// </summary>
    internal void ReleaseDriverForReuse()
    {
        var go = GameObject;
        GameObject = null;
        DriverActive = false;
        _driverActivated = false;
        _boundUnderRoot = false;
        UnityEffectRuntime.ForgetBoundDriver(go, this);
    }

    internal bool TryBindSequenceDriver()
    {
        if (_released || Kind != AssetKind.Sequence) return false;
        if (GameObject != null)
        {
            try
            {
                if (GameObject.Pointer != IntPtr.Zero)
                {
                    // Job FBUFF17 (R45 finding 30). This used to ask IsDriverRecycled(), a bool
                    // shim that folded Unprovable into "not recycled" — the exact fail-open
                    // FEFF2 was written to remove, left at one of the answer's two call sites.
                    // On an Unprovable frame the two sites disagreed: DriveLateWrite refused the
                    // clone write while this one reported the driver live and ran ApplyPending.
                    // Switch on the three-state answer the way DriveLateWrite does.
                    switch (ReadDriverOwnership())
                    {
                        case DriverOwnership.Recycled:
                            DropRecycledDriver();
                            break;

                        case DriverOwnership.Unprovable:
                            // Do not write, do not drop the bind, and do not claim a bind the
                            // caller would stop polling on. The next frame reads again.
                            return false;

                        default:
                            ApplyPending();
                            return true;
                    }
                }
            }
            catch { GameObject = null; }
        }

        // The authoritative bind is always allowed: the sequence entity's own managed hybrid is
        // ours by construction. The snapshot fallback is not, once the sequencer has recycled
        // this handle's clone (R28 finding 13).
        bool mayFallback = DriverClaimPolicy.MayUseFallback(_recycle.Recycled, _seenDrivers != null);
        var driver = SequenceDriverLookup.FromSequenceEntity(_sequenceWorld, _sequenceEntity)
            ?? (mayFallback ? SequenceDriverLookup.FindFallback(_seenDrivers!) : null);
        if (driver == null) return false;
        BindDriverGo(driver);
        ApplyPending();
        Plugin.Logger?.LogInfo("Effects: sequence driver bound after poll");
        return true;
    }

    public void DriveEndpoints() => ApplyPending();

    public void DriveLateWrite()
    {
        if (_released) return;
        ResolveTrackedEndpoints();
        WriteOwnedLine();
        if (UnityEffectRuntime.TryWriteLineRendererPositions(_sequenceWorld, _sequenceEntity, in _beamStart, in _beamEnd))
        {
            _lineRendererBufferWrites++;
            MaybePromoteGameLinePath();
        }

        if (GameObject == null)
            return;

        try
        {
            if (GameObject.Pointer == IntPtr.Zero)
            {
                DropRecycledDriver();
                return;
            }
        }
        catch
        {
            DropRecycledDriver();
            return;
        }

        var ownership = ReadDriverOwnership();
        if (ownership == DriverOwnership.Recycled)
        {
            DropRecycledDriver();
            return;
        }

        // Job FEFF2. An unprovable identity means "do not write": a foreign LineRenderer is a
        // visible beam across somebody else's effect. Keep the bind — the recycle transition is
        // terminal for the borrowed game line (R27 finding 11) and a transient read failure must
        // not take it. The owned line above is our own GameObject and has already been written.
        if (ownership == DriverOwnership.Unprovable)
        {
            if (!_loggedOwnershipUnprovable)
            {
                _loggedOwnershipUnprovable = true;
                Plugin.Logger?.LogWarning(PoolParentPolicy.OwnershipUnprovable);
            }

            return;
        }

        _hybridDrive.BeginFrame(!_loggedLateInspect);
        UnityEffectRuntime.DriveDriverEnds(GameObject, in _beamStart, in _beamEnd, ref _hybridDrive);
        if (!_hybridDrive.Wrote)
        {
            LogOwnedLineReadbackOnce();
            return;
        }

        if (!_hybridDrive.SequencerMatched)
            _sequencerOverwriteCount++;
        _lateWriteCount++;
        try { _lastLateWriteFrame = Time.frameCount; }
        catch { _lastLateWriteFrame = _lateWriteCount; }
        HybridLastWrite = true;
        if (_hybridDrive.LogInspect)
        {
            _loggedLateInspect = true;
            string game = string.IsNullOrEmpty(_hybridDrive.Before) ? "[(0,0,0),driver]" : _hybridDrive.Before;
            Plugin.Logger?.LogInfo(
                SequenceLateDriver.FormatFirstWriteLog(game, in _beamStart, in _beamEnd, _lastLateWriteFrame));
            LogOwnedLineReadbackOnce();
        }

        // Cardinal pixels come from the owned LineRenderer (default). Keep the hybrid
        // inspect verdict available when a graph ever honors the marker.
        if (!_loggedVerdict && _lateWriteCount >= SequenceLateDriver.VerdictAfterFrames
            && !HasOwnedLine)
            LogLateVerdict();
    }

    private void LogLateVerdict()
    {
        if (_loggedVerdict) return;
        _loggedVerdict = true;
        if (string.IsNullOrEmpty(_lateWriteVerdict))
        {
            _lateWriteVerdict = _sequencerOverwriteCount > 0
                ? SequenceLateDriver.VerdictLateFrame
                : SequenceLateDriver.VerdictSequencer;
        }

        Plugin.Logger?.LogInfo("Effects: sequence beam " + _lateWriteVerdict);
    }

    private void LogLateSummary()
    {
        if (_loggedSummary) return;
        if (!_sequencePlayed && _lateWriteCount <= 0 && !HasOwnedLine) return;
        _loggedSummary = true;
        if (!HasOwnedLine)
            LogLateVerdict();
        Plugin.Logger?.LogInfo(
            "Effects: sequence late writes=" + _lateWriteCount
            + ", sequencer overwrote before us " + _sequencerOverwriteCount + " frames"
            + " lastFrame=" + _lastLateWriteFrame);
    }

    /// <summary>
    /// Instance id of <paramref name="obj"/>, or <see cref="PoolParentPolicy.NoInstanceId"/>.
    /// R26 finding 9: Il2CppInterop can front one native object with two managed wrappers, so
    /// <c>ReferenceEquals</c> is not identity here — <c>GetInstanceID()</c> is.
    /// </summary>
    private static int InstanceIdOf(UnityEngine.Object? obj)
    {
        if (obj == null) return PoolParentPolicy.NoInstanceId;
        try { return obj.GetInstanceID(); }
        catch { return PoolParentPolicy.NoInstanceId; }
    }

    private int RootInstanceId => InstanceIdOf(_root);

    private void RememberPoolParent(GameObject driver)
    {
        if (_poolParent != null) return;
        Transform? parent = null;
        try { parent = driver.transform.parent; }
        catch { /* */ }

        if (parent != null)
        {
            int parentId = PoolParentPolicy.NoInstanceId;
            try { parentId = InstanceIdOf(parent.gameObject); }
            catch { parentId = PoolParentPolicy.NoInstanceId; }

            if (PoolParentPolicy.CanRecordPoolParent(parentId, RootInstanceId))
                _poolParent = parent;
            else if (!_loggedPoolParentIsRoot)
            {
                // A second handle claimed a clone the first one had already reparented under
                // our root. Recording that as the pool parent would hand the game's pooled
                // instance to DestroyRoot (R26 finding 9).
                _loggedPoolParentIsRoot = true;
                Plugin.Logger?.LogInfo(PoolParentPolicy.ParentIsOurRoot);
            }
        }

        if (_poolParent == null)
            _poolParent = SequenceDriverLookup.TryGetParticleParent(_sequenceWorld);

        // Belt and braces: never leave our own root recorded as the pool parent.
        if (_poolParent != null)
        {
            int recorded = PoolParentPolicy.NoInstanceId;
            try { recorded = InstanceIdOf(_poolParent.gameObject); }
            catch { recorded = PoolParentPolicy.NoInstanceId; }
            if (!PoolParentPolicy.CanRecordPoolParent(recorded, RootInstanceId))
                _poolParent = null;
        }
    }

    private void RestorePoolParent(bool deactivate)
    {
        if (GameObject == null || !UnityEffectRuntime.IsAlive(GameObject)) return;
        try
        {
            var parent = _poolParent ?? SequenceDriverLookup.TryGetParticleParent(_sequenceWorld);
            if (parent != null)
                GameObject.transform.SetParent(parent, worldPositionStays: true);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Effects: sequence restore pool parent threw: " + e.Message);
        }

        if (!deactivate) return;
        try { GameObject.SetActive(false); }
        catch { /* */ }
    }

    /// <summary>
    /// Job FEFF2. Read the clone's identity evidence and hand it to
    /// <see cref="PoolParentPolicy.DecideOwnership"/>. The parent read is the load-bearing
    /// one: it used to be swallowed, which left an unreadable parent answering "not recycled"
    /// and writing endpoints into a clone we could not prove was ours.
    /// </summary>
    private DriverOwnership ReadDriverOwnership()
    {
        if (GameObject == null || !UnityEffectRuntime.IsAlive(GameObject)) return DriverOwnership.Recycled;
        bool self;
        bool hierarchy;
        try { self = GameObject.activeSelf; }
        catch { return DriverOwnership.Recycled; }
        try { hierarchy = GameObject.activeInHierarchy; }
        catch { return DriverOwnership.Recycled; }

        bool parentRead = true;
        int parentId = PoolParentPolicy.NoInstanceId;
        try
        {
            var parent = GameObject.transform.parent;
            if (parent != null)
                parentId = InstanceIdOf(parent.gameObject);
        }
        catch { parentRead = false; }

        int poolId = PoolParentPolicy.NoInstanceId;
        if (_poolParent != null)
        {
            try { poolId = InstanceIdOf(_poolParent.gameObject); }
            catch { poolId = PoolParentPolicy.NoInstanceId; }
        }

        return PoolParentPolicy.DecideOwnership(
            parentRead, parentId, poolId, RootInstanceId, _boundUnderRoot, self, hierarchy);
    }


    /// <summary>
    /// The sequencer took the pooled clone back. R27 finding 11: the owned LineRenderer is our
    /// own GameObject under our own root, so the recycle stops only the <b>clone-related</b>
    /// writes — the borrowed prefab copy is disabled, the driver reference is dropped, and the
    /// handle stays watched so <see cref="DriveLateWrite"/> keeps re-pointing the owned line
    /// from the handle's endpoints until the handle's own lifetime ends. The plugin's Dispose
    /// still runs the normal cleanup.
    /// </summary>
    private void DropRecycledDriver()
    {
        var action = _recycle.OnRecycled();
        var held = GameObject;
        GameObject = null;
        DriverActive = false;
        _driverActivated = false;
        _boundUnderRoot = false;
        UnityEffectRuntime.ForgetBoundDriver(held, this);
        // R28 finding 13: the play-frame snapshot is now stale — the clone it names is back in
        // the pool and no longer bound to us, so FindFallback would rate it a fresh NewClone and
        // re-claim a pool-owned object. Drop the snapshot with the driver; DriverClaimPolicy
        // .MayUseFallback refuses the fallback for a recycled handle in any case.
        _seenDrivers = null;

        if (action.HideOwnedLine)
        {
            SetLineEnabled(_ownedLine, false);
            SetLineEnabled(_glowLine, false);
        }

        if (action.HideGameLine)
            HideGameLineForRecycle();
        if (action.Log != null)
            Plugin.Logger?.LogInfo(action.Log);
        if (!action.KeepWatching)
            SequenceLateDriver.Unwatch(this);
    }

    private void HideGameLineForRecycle()
    {
        SetLineEnabled(_gameLine, false);
        try
        {
            if (UnityEffectRuntime.IsAlive(_gameLineGo) && _gameLineGo != null)
                _gameLineGo.SetActive(false);
        }
        catch { /* */ }
    }

    private static void SetLineEnabled(LineRenderer? lr, bool enabled)
    {
        if (lr == null) return;
        try { lr.enabled = enabled; }
        catch { /* */ }
    }

    private void EnsureOwnedLine(string reason)
    {
        if (_released || Kind != AssetKind.Sequence) return;
        if (UnityEffectRuntime.IsAlive(_ownedLineGo) && _ownedLine != null)
            return;

        if (_root == null) return;
        try
        {
            var go = new GameObject("beam-line");
            go.transform.SetParent(_root.transform, false);
            try { go.transform.localScale = Vector3.one; } catch { /* */ }
            LogRootLossyScaleOnce();
            var lr = go.AddComponent<LineRenderer>();
            if (lr == null)
            {
                try { UnityEngine.Object.DestroyImmediate(go); }
                catch { /* */ }
                return;
            }

            lr.useWorldSpace = true;
            lr.positionCount = 2;
            try { lr.alignment = LineAlignment.View; } catch { /* */ }
            try { lr.textureMode = LineTextureMode.Stretch; } catch { /* */ }
            try { lr.numCornerVertices = 0; } catch { /* */ }
            try { lr.numCapVertices = 4; } catch { /* */ }
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            lr.receiveShadows = false;
            _ownedLineGo = go;
            _ownedLine = lr;
            _ownedCountSet = true;
            _ownedLineStyleCopied = false;
            if ((_usePrefabLineMaterial || _copyGameLineIfSuitable) && GameObject != null)
                CopyOwnedLineStyleFromDriver();
            else if (!AssignDefaultOwnedLineMaterial())
            {
                // Last resort: copy the prefab material onto the owned line only.
                _usePrefabLineMaterial = true;
                CopyOwnedLineStyleFromDriver();
            }

            ApplyOwnedLineWidth();
            ApplyOwnedLineColor();
            LogOwnedLineOnce(reason);
            LogOwnedLineStyleOnce();
            LogOwnedLineReadbackOnce();
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Effects: owned LineRenderer create threw: " + e.Message);
        }
    }

    private void LogRootLossyScaleOnce()
    {
        if (_loggedRootScale || _root == null) return;
        Vector3 s;
        try { s = _root.transform.lossyScale; }
        catch { return; }
        if (NearOne(s.x) && NearOne(s.y) && NearOne(s.z)) return;
        _loggedRootScale = true;
        Plugin.Logger?.LogInfo(
            "Effects: sequence owned line root lossyScale=("
            + s.x.ToString("0.##") + "," + s.y.ToString("0.##") + "," + s.z.ToString("0.##")
            + ") (expected 1)");
    }

    private static bool NearOne(float v) => v > 0.99f && v < 1.01f;

    /// <summary>
    /// Create a material that belongs only to the owned line. Never assign it to the
    /// pooled clone (EV7 magenta: <c>Renderer.material</c> instanced the clone, then
    /// <c>Release</c> destroyed those instances while the pool still drew them).
    /// </summary>
    private bool AssignDefaultOwnedLineMaterial()
    {
        if (_ownedLine == null) return false;
        if (!TryCreateOwnedLineMaterial(out var mat, out string shaderName) || mat == null)
            return false;
        BindOwnedLineMaterial(mat, shaderName);
        return true;
    }

    private static bool TryCreateOwnedLineMaterial(out Material? mat, out string shaderName)
    {
        mat = null;
        shaderName = "";
        var shader = UnityEffectRuntime.ResolveOwnedLineShader(out shaderName);
        if (shader != null)
        {
            if (TryBuildOwnedMaterial(shader, shaderName, configureHdrp: IsHdrpUnlitName(shaderName), out mat))
            {
                if (UnityEffectRuntime.TryShaderIsSupported(shader))
                    return true;
                Plugin.Logger?.LogInfo(
                    "Effects: sequence owned line shader '" + shaderName + "' isSupported=False; trying fallback");
                DestroyOwnedMaterial(mat);
                mat = null;
            }
        }

        var fallbacks = SequenceLateDriver.OwnedLineFallbackShaders;
        for (int i = 0; i < fallbacks.Length; i++)
        {
            string candidate = fallbacks[i];
            Shader? fb = null;
            try { fb = Shader.Find(candidate); }
            catch (Exception e)
            {
                Plugin.Logger?.LogWarning("Effects: Shader.Find('" + candidate + "') threw: " + e.Message);
                continue;
            }

            if (fb == null)
            {
                Plugin.Logger?.LogInfo("Effects: Shader.Find('" + candidate + "') is null");
                continue;
            }

            if (!TryBuildOwnedMaterial(fb, candidate, configureHdrp: false, out mat))
                continue;
            if (!UnityEffectRuntime.TryShaderIsSupported(fb))
            {
                Plugin.Logger?.LogInfo(
                    "Effects: sequence owned line shader '" + candidate + "' isSupported=False");
                DestroyOwnedMaterial(mat);
                mat = null;
                continue;
            }

            shaderName = candidate;
            Plugin.Logger?.LogInfo("Effects: sequence owned line using fallback shader '" + candidate + "'");
            return true;
        }

        Plugin.Logger?.LogInfo("Effects: sequence owned line no unlit shader; falling back to prefab material");
        return false;
    }

    private static bool IsHdrpUnlitName(string name) =>
        !string.IsNullOrEmpty(name)
        && name.IndexOf("HDRP", StringComparison.OrdinalIgnoreCase) >= 0;

    private static bool TryBuildOwnedMaterial(Shader shader, string shaderName, bool configureHdrp, out Material? mat)
    {
        mat = null;
        try
        {
            mat = new Material(shader) { name = "progenitor-owned-beam" };
            if (configureHdrp)
                UnityEffectRuntime.ConfigureUnlitTransparentAdditive(mat);
            return mat != null;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning(
                "Effects: owned LineRenderer material '" + shaderName + "' threw: " + e.Message);
            DestroyOwnedMaterial(mat);
            mat = null;
            return false;
        }
    }

    private static void DestroyOwnedMaterial(Material? mat)
    {
        if (mat == null) return;
        try { UnityEngine.Object.Destroy(mat); }
        catch { /* */ }
    }

    private void BindOwnedLineMaterial(Material mat, string shaderName)
    {
        var previous = _ownedLineMaterial;
        _ownedLineMaterial = mat;
        _ownedLineMaterialName = shaderName;
        try { _ownedLine!.sharedMaterial = mat; }
        catch { /* */ }
        if (previous != null && !ReferenceEquals(previous, mat))
            DestroyOwnedMaterial(previous);
    }

    /// <summary>
    /// Opt-in: copy <c>clone.sharedMaterial</c> onto the owned line only
    /// (<c>new Material(shared)</c>). Never write <c>clone.material</c> — that getter
    /// instances onto the pooled renderer (evidence 125 magenta wedge).
    /// </summary>
    private void CopyOwnedLineStyleFromDriver()
    {
        if (_ownedLineStyleCopied) return;
        if (!_usePrefabLineMaterial && !_copyGameLineIfSuitable) return;
        if (_ownedLine == null || GameObject == null) return;
        LineRenderer? src = null;
        try { src = GameObject.GetComponent<LineRenderer>(); }
        catch { /* */ }
        if (src == null)
        {
            try { src = GameObject.GetComponentInChildren<LineRenderer>(true); }
            catch { /* */ }
        }

        if (src == null) return;

        string shaderName = "";
        Material? shared = null;
        try { shared = src.sharedMaterial; }
        catch { /* */ }
        try
        {
            if (shared != null && shared.shader != null)
                shaderName = shared.shader.name ?? "";
        }
        catch { /* */ }

        if (_copyGameLineIfSuitable && !_usePrefabLineMaterial && IsTrailSpriteShader(shaderName))
        {
            if (!_loggedTrailSkip)
            {
                _loggedTrailSkip = true;
                Plugin.Logger?.LogInfo(SequenceLateDriver.FormatTrailSpriteSkipLog(
                    string.IsNullOrEmpty(shaderName) ? "trail" : shaderName));
            }

            return;
        }

        try
        {
            // sharedMaterial is a read; .material would instance onto the clone.
            if (shared != null)
            {
                Material? instanced = null;
                try { instanced = new Material(shared) { name = "progenitor-owned-beam" }; }
                catch { instanced = null; }
                if (instanced != null)
                {
                    if (string.IsNullOrEmpty(shaderName)) shaderName = "prefab";
                    BindOwnedLineMaterial(instanced, shaderName);
                    CaptureCopiedMaterialColor(instanced, src);
                    _driverMaterialHasGlow = MaterialCarriesGlow(instanced, src);
                }
            }
        }
        catch { /* */ }
        if (!_ownedLineWidthSet)
        {
            try { _ownedLineWidth = src.startWidth; }
            catch { /* */ }
        }

        try { _ownedLine.colorGradient = src.colorGradient; }
        catch { /* */ }
        try
        {
            _ownedLine.startColor = src.startColor;
            _ownedLine.endColor = src.endColor;
        }
        catch { /* */ }
        try { _ownedLine.textureMode = src.textureMode; }
        catch { /* */ }

        _ownedLineStyleCopied = true;
        if (_ownedLineStyleCopied && !string.IsNullOrEmpty(_ownedLineMaterialName))
            SetDrawPath(SequenceLateDriver.VerdictOwnedGameMaterial);
        ApplyOwnedLineWidth();
        if (_hasPendingColor || _tintAppliedToDriver)
            ApplyOwnedLineColor();
        if (_driverMaterialHasGlow)
        {
            DestroyGlowLine();
            if (!_loggedGlow)
            {
                _loggedGlow = true;
                Plugin.Logger?.LogInfo(SequenceLateDriver.GlowFromMaterialLog);
            }
        }
        else
            EnsureGlowLine();
        LogOwnedLineStyleOnce();
        LogOwnedLineReadbackOnce();
        LogDrawPathOnce();
    }

    private static bool IsTrailSpriteShader(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        return name.IndexOf("Trail", StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf("SPRITE", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private void CaptureCopiedMaterialColor(Material mat, LineRenderer src)
    {
        Color c = default;
        bool have = false;
        try
        {
            if (mat.HasProperty("_UnlitColor")) { c = mat.GetColor("_UnlitColor"); have = true; }
            else if (mat.HasProperty("_EmissionColor")) { c = mat.GetColor("_EmissionColor"); have = true; }
            else if (mat.HasProperty("_EmissiveColor")) { c = mat.GetColor("_EmissiveColor"); have = true; }
            else if (mat.HasProperty("_Color")) { c = mat.GetColor("_Color"); have = true; }
            else if (mat.HasProperty("_BaseColor")) { c = mat.GetColor("_BaseColor"); have = true; }
        }
        catch { /* */ }

        if (!have)
        {
            try { c = src.startColor; have = true; }
            catch { /* */ }
        }

        if (!have) return;
        _copiedMaterialColor = new EffectColor(c.r, c.g, c.b, c.a);
        _hasCopiedMaterialColor = true;
    }

    private static bool MaterialCarriesGlow(Material mat, LineRenderer src)
    {
        try
        {
            if (mat.HasProperty("_EmissionColor"))
            {
                var e = mat.GetColor("_EmissionColor");
                if (EffectColor.Max3(e.r, e.g, e.b) > 1.01f) return true;
            }

            if (mat.HasProperty("_EmissiveColor"))
            {
                var e = mat.GetColor("_EmissiveColor");
                if (EffectColor.Max3(e.r, e.g, e.b) > 1.01f) return true;
            }
        }
        catch { /* */ }

        try
        {
            var c = src.startColor;
            if (EffectColor.Max3(c.r, c.g, c.b) > 1.01f) return true;
        }
        catch { /* */ }

        return false;
    }

    /// <summary>
    /// World width in meters: <c>widthMultiplier = 1</c>, constant curve of 1 (no taper),
    /// then <c>startWidth = endWidth = width</c>. Setting the curve <em>after</em>
    /// startWidth (EV8) overwrote the 0.12 m keys with 1, so the line drew ~1 m wide
    /// (evidence 125). Do not also put the meters value in <c>widthMultiplier</c>.
    /// </summary>
    private void ApplyOwnedLineWidth()
    {
        if (_ownedLine == null) return;
        try { _ownedLine.widthMultiplier = 1f; }
        catch { /* */ }
        try { _ownedLine.SetWidthCurve(AnimationCurve.Constant(0f, 1f, 1f)); }
        catch { /* */ }
        try
        {
            _ownedLine.startWidth = _ownedLineWidth;
            _ownedLine.endWidth = _ownedLineWidth;
        }
        catch { /* */ }
    }

    private void ApplyOwnedLineColor()
    {
        if (_ownedLine == null) return;
        var color = _hasPendingColor ? _pendingColor : SequenceLateDriver.OwnedLineDefaultTint;
        if (_hasCopiedMaterialColor && _hasPendingColor)
            color = EffectColor.TintPreserveIntensity(_copiedMaterialColor, _pendingColor);
        else if (_hasCopiedMaterialColor && !_hasPendingColor)
            color = _copiedMaterialColor;
        _lastTintedColor = color;
        var unity = (Color)color;
        try
        {
            _ownedLine.startColor = unity;
            _ownedLine.endColor = unity;
        }
        catch { /* */ }

        Material? mat = _ownedLineMaterial;
        if (mat == null)
        {
            try { mat = _ownedLine.sharedMaterial; }
            catch { /* */ }
        }

        if (mat == null && !_usePrefabLineMaterial)
            AssignDefaultOwnedLineMaterial();
        mat = _ownedLineMaterial;
        if (mat == null) return;
        try { mat.color = unity; }
        catch { /* */ }
        try
        {
            if (mat.HasProperty("_UnlitColor")) mat.SetColor("_UnlitColor", unity);
            else if (mat.HasProperty("_Color")) mat.SetColor("_Color", unity);
            else if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", unity);
            else if (mat.HasProperty("_EmissionColor")) mat.SetColor("_EmissionColor", unity);
            if (mat.HasProperty("_EmissiveColor")) mat.SetColor("_EmissiveColor", unity);
        }
        catch { /* */ }
        if (Kind == AssetKind.Sequence)
            _tintAppliedToDriver = true;
        ApplyGlowColor();
    }

    private void LogOwnedLineStyleOnce()
    {
        if (_ownedLineStyleLogged) return;
        if (_ownedLine == null) return;
        if (string.IsNullOrEmpty(_ownedLineMaterialName)) return;
        _ownedLineStyleLogged = true;
        Plugin.Logger?.LogInfo(
            SequenceLateDriver.FormatOwnedLineStyleLog(_ownedLineMaterialName, _ownedLineWidth));
    }

    private void ResolveTrackedEndpoints()
    {
        if (_trackStart && !_startFrozen)
        {
            if (UnityEffectRuntime.TryReadEntityPose(_sequenceWorld, _trackStartIndex, _trackStartVersion, out var pos))
                _beamStart = AddOffset(pos, _trackStartOffset);
            else
            {
                _startFrozen = true;
                if (!_loggedStartLost)
                {
                    _loggedStartLost = true;
                    Plugin.Logger?.LogInfo("Effects: sequence endpoint start lost; frozen");
                }
            }
        }

        if (_trackEndFixed)
        {
            _beamEnd = _trackEndWorld;
            return;
        }

        if (_trackEnd && !_endFrozen)
        {
            if (UnityEffectRuntime.TryReadEntityPose(_sequenceWorld, _trackEndIndex, _trackEndVersion, out var pos))
                _beamEnd = AddOffset(pos, _trackEndOffset);
            else
            {
                _endFrozen = true;
                if (!_loggedEndLost)
                {
                    _loggedEndLost = true;
                    Plugin.Logger?.LogInfo("Effects: sequence endpoint end lost; frozen");
                }
            }
        }
    }

    private void SeedTrackedStart()
    {
        if (UnityEffectRuntime.TryReadEntityPose(_sequenceWorld, _trackStartIndex, _trackStartVersion, out var pos))
            _beamStart = AddOffset(pos, _trackStartOffset);
    }

    private void SeedTrackedEnd()
    {
        if (UnityEffectRuntime.TryReadEntityPose(_sequenceWorld, _trackEndIndex, _trackEndVersion, out var pos))
            _beamEnd = AddOffset(pos, _trackEndOffset);
    }

    private static EffectVec3 AddOffset(in EffectVec3 pos, in EffectVec3 offset) =>
        new(pos.X + offset.X, pos.Y + offset.Y, pos.Z + offset.Z);

    private void WriteOwnedLine()
    {
        _ownedPos0.x = _beamStart.X;
        _ownedPos0.y = _beamStart.Y;
        _ownedPos0.z = _beamStart.Z;
        _ownedPos1.x = _beamEnd.X;
        _ownedPos1.y = _beamEnd.Y;
        _ownedPos1.z = _beamEnd.Z;

        // The borrowed game line is the body when one was adopted; the owned line still
        // exists (disabled) as the fallback, so both take the same two points. After a recycle
        // the borrowed copy is gone (R27 finding 11) and only the owned line is written.
        if (!_recycle.GameLineHidden)
            WriteGameLine();

        if (_ownedLine == null && (_recycle.GameLineHidden || !HasGameLine))
            EnsureOwnedLine("late-write");
        if (_ownedLine == null) return;
        try
        {
            if (!_ownedCountSet)
            {
                if (_ownedLine.positionCount < 2)
                    _ownedLine.positionCount = 2;
                _ownedCountSet = true;
            }

            _ownedLine.SetPosition(0, _ownedPos0);
            _ownedLine.SetPosition(1, _ownedPos1);
            WriteGlowLine();
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Effects: owned LineRenderer SetPosition threw: " + e.Message);
        }
    }

    private void LogOwnedLineOnce(string _)
    {
        if (_ownedLineLogged) return;
        _ownedLineLogged = true;
        _loggedVerdict = true;
        if (string.IsNullOrEmpty(_drawPath))
            _drawPath = SequenceLateDriver.VerdictOwnedLine;
        _lateWriteVerdict = _drawPath;
        if (_drawPath == SequenceLateDriver.VerdictOwnedLine)
        {
            Plugin.Logger?.LogInfo(
                "Effects: sequence beam drawn by owned LineRenderer ("
                + SequenceLateDriver.OwnedLineNotDrawable + ")");
        }
        else
            LogDrawPathOnce();
    }

    private void LogOwnedLineReadbackOnce()
    {
        if (_loggedLineReadback) return;
        if (_ownedLine == null) return;
        _loggedLineReadback = true;
        float startWidth = _ownedLineWidth;
        float multiplier = 1f;
        float lx = 1f;
        float ly = 1f;
        float lz = 1f;
        string shader = _ownedLineMaterialName ?? "";
        bool supported = true;
        int queue = 3000;
        try { startWidth = _ownedLine.startWidth; }
        catch { /* */ }
        try { multiplier = _ownedLine.widthMultiplier; }
        catch { /* */ }
        try
        {
            if (_ownedLineGo != null)
            {
                var s = _ownedLineGo.transform.lossyScale;
                lx = s.x;
                ly = s.y;
                lz = s.z;
            }
        }
        catch { /* */ }

        Material? mat = _ownedLineMaterial;
        if (mat == null)
        {
            try { mat = _ownedLine.sharedMaterial; }
            catch { /* */ }
        }

        if (mat != null)
        {
            try { queue = mat.renderQueue; }
            catch { /* */ }
            try
            {
                var sh = mat.shader;
                if (sh != null)
                {
                    try { if (!string.IsNullOrEmpty(sh.name)) shader = sh.name; }
                    catch { /* */ }
                    supported = UnityEffectRuntime.TryShaderIsSupported(sh);
                }
            }
            catch { /* */ }
        }

        Plugin.Logger?.LogInfo(
            SequenceLateDriver.FormatOwnedLineReadbackLog(
                startWidth, multiplier, lx, ly, lz, shader, supported, queue));
    }

    private void DestroyOwnedLine()
    {
        if (_ownedLineGo != null)
        {
            try { UnityEngine.Object.DestroyImmediate(_ownedLineGo); }
            catch
            {
                try { UnityEngine.Object.Destroy(_ownedLineGo); }
                catch { /* */ }
            }

            _ownedLineGo = null;
            _ownedLine = null;
        }

        if (_ownedLineMaterial != null)
        {
            try { UnityEngine.Object.Destroy(_ownedLineMaterial); }
            catch { /* */ }
            _ownedLineMaterial = null;
        }

        _ownedLineStyleCopied = false;
        _ownedLineStyleLogged = false;
        _ownedCountSet = false;
        _ownedLineLogged = false;
        _loggedLineReadback = false;
        _ownedLineMaterialName = "";
        DestroyGlowLine();
    }

    private void EnsureGlowLine()
    {
        if (_released || Kind != AssetKind.Sequence) return;
        // A borrowed game line is the body; a halo around it would double-draw the segment.
        if (HasGameLine)
        {
            DestroyGlowLine();
            return;
        }

        if (_glowWidth <= 0f || _driverMaterialHasGlow)
        {
            DestroyGlowLine();
            return;
        }

        if (_ownedLine == null)
            EnsureOwnedLine("glow");
        if (_ownedLine == null || _root == null) return;
        if (UnityEffectRuntime.IsAlive(_glowLineGo) && _glowLine != null)
        {
            ApplyGlowWidth();
            ApplyGlowColor();
            return;
        }

        try
        {
            var go = new GameObject("beam-glow");
            go.transform.SetParent(_root.transform, false);
            try { go.transform.localScale = Vector3.one; } catch { /* */ }
            var lr = go.AddComponent<LineRenderer>();
            if (lr == null)
            {
                try { UnityEngine.Object.DestroyImmediate(go); }
                catch { /* */ }
                return;
            }

            lr.useWorldSpace = true;
            lr.positionCount = 2;
            try { lr.alignment = LineAlignment.View; } catch { /* */ }
            try { lr.textureMode = LineTextureMode.Stretch; } catch { /* */ }
            try { lr.numCornerVertices = 0; } catch { /* */ }
            try { lr.numCapVertices = 4; } catch { /* */ }
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            lr.receiveShadows = false;
            _glowLineGo = go;
            _glowLine = lr;
            if (TryCreateOwnedLineMaterial(out var mat, out _) && mat != null)
            {
                _glowLineMaterial = mat;
                try { lr.sharedMaterial = mat; }
                catch { /* */ }
            }

            ApplyGlowWidth();
            ApplyGlowColor();
            if (!_loggedGlow)
            {
                _loggedGlow = true;
                Plugin.Logger?.LogInfo(SequenceLateDriver.FormatGlowLineLog(_glowWidth, _glowAlpha));
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Effects: glow LineRenderer create threw: " + e.Message);
        }
    }

    private void ApplyGlowWidth()
    {
        if (_glowLine == null) return;
        try { _glowLine.widthMultiplier = 1f; } catch { /* */ }
        try { _glowLine.SetWidthCurve(AnimationCurve.Constant(0f, 1f, 1f)); } catch { /* */ }
        try
        {
            _glowLine.startWidth = _glowWidth;
            _glowLine.endWidth = _glowWidth;
        }
        catch { /* */ }
    }

    private void ApplyGlowColor()
    {
        if (_glowLine == null) return;
        var core = _lastTintedColor;
        if (core.A <= 0f && core.R <= 0f && core.G <= 0f && core.B <= 0f)
            core = _hasPendingColor ? _pendingColor : SequenceLateDriver.OwnedLineDefaultTint;
        var glow = new EffectColor(core.R, core.G, core.B, core.A * _glowAlpha);
        var unity = (Color)glow;
        try
        {
            _glowLine.startColor = unity;
            _glowLine.endColor = unity;
        }
        catch { /* */ }

        var mat = _glowLineMaterial;
        if (mat == null) return;
        try { mat.color = unity; } catch { /* */ }
        try
        {
            if (mat.HasProperty("_UnlitColor")) mat.SetColor("_UnlitColor", unity);
            else if (mat.HasProperty("_Color")) mat.SetColor("_Color", unity);
            else if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", unity);
            if (mat.HasProperty("_EmissionColor")) mat.SetColor("_EmissionColor", unity);
        }
        catch { /* */ }
    }

    private void WriteGlowLine()
    {
        if (_glowWidth > 0f && !_driverMaterialHasGlow)
            EnsureGlowLine();
        if (_glowLine == null) return;
        try
        {
            _glowLine.SetPosition(0, _ownedPos0);
            _glowLine.SetPosition(1, _ownedPos1);
        }
        catch { /* */ }
    }

    private void DestroyGlowLine()
    {
        if (_glowLineGo != null)
        {
            try { UnityEngine.Object.DestroyImmediate(_glowLineGo); }
            catch
            {
                try { UnityEngine.Object.Destroy(_glowLineGo); }
                catch { /* */ }
            }

            _glowLineGo = null;
            _glowLine = null;
        }

        if (_glowLineMaterial != null)
        {
            try { UnityEngine.Object.Destroy(_glowLineMaterial); }
            catch { /* */ }
            _glowLineMaterial = null;
        }
    }

    // ---- borrowed game line (EV12) ----------------------------------------------------------

    /// <summary>
    /// Copy a vanilla two-point beam prefab and make <em>that</em> LineRenderer the body, so
    /// the game's own <c>Shader Graphs/Effect</c> (or <c>Liquid</c>) material draws the beam
    /// (evidence 130). The copy is ours: <c>Instantiate</c>d here, parented under our root,
    /// written every late frame and destroyed on Release. The pooled sequencer clone is
    /// still never touched (R26 finding 3). Falls back to the owned unlit line — the existing
    /// EV8/EV10 path — when nothing in the catalog is loaded.
    /// </summary>
    private void EnsureGameLine(BeamStyle style)
    {
        if (_released || Kind != AssetKind.Sequence) return;
        if (HasGameLine || _gameLineTried) return;
        if (!BeamCatalog.WantsGameLine(style)) return;
        if (_root == null) return;
        _gameLineTried = true;

        var prefab = UnityEffectRuntime.FindGameLinePrefab(style, out string chosen, out var beam);
        if (prefab == null)
        {
            if (!_loggedGameLine)
            {
                _loggedGameLine = true;
                Plugin.Logger?.LogInfo(
                    "Effects: Beam(" + style + ") found no loaded game line prefab; keeping the owned line");
            }

            return;
        }

        GameObject? copy = null;
        try
        {
            copy = UnityEngine.Object.Instantiate(prefab);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Effects: game line Instantiate('" + chosen + "') threw: " + e.Message);
            return;
        }

        if (copy == null) return;
        try
        {
            // Prefixed so a later FindGameLinePrefab scan skips our own copies.
            copy.name = EffectOwnership.Prefix + "beam-gameline-" + chosen;
            copy.transform.SetParent(_root.transform, worldPositionStays: false);
            try { copy.transform.localScale = Vector3.one; } catch { /* */ }
            // ParticleLifetimeSystem.UpdateLineRendererPositionElement_Job
            // pushes LineRendererPositionElement into HybridLineRendererComponent.
            // Our copy is not on a sequence entity, but its Awake caches the
            // LineRenderer and CalculateThrowArc rewrites Positions; drop the driver so only we
            // write SetPosition.
            UnityEffectRuntime.StripHybridLineDrivers(copy);

            LineRenderer? lr = null;
            try { lr = copy.GetComponent<LineRenderer>(); }
            catch { /* */ }
            if (lr == null)
            {
                try { lr = copy.GetComponentInChildren<LineRenderer>(true); }
                catch { /* */ }
            }

            if (lr == null)
            {
                DestroyGameLine();
                Plugin.Logger?.LogInfo(
                    "Effects: game line copy of '" + chosen + "' has no LineRenderer; keeping the owned line");
                return;
            }

            lr.useWorldSpace = true;
            lr.positionCount = 2;
            try { lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; } catch { /* */ }
            try { lr.receiveShadows = false; } catch { /* */ }
            try { lr.enabled = true; } catch { /* */ }

            _gameLineGo = copy;
            _gameLine = lr;
            _gameLinePrefabName = chosen;
            if (!_ownedLineWidthSet && beam.Width > 0f)
                _ownedLineWidth = beam.Width;
            ApplyGameLineWidth();
            ApplyGameLineColor();
            try { copy.SetActive(true); }
            catch { /* */ }

            // The borrowed line is the body now: the owned unlit core and its halo would
            // double-draw the same segment.
            SetLineEnabled(_ownedLine, false);
            DestroyGlowLine();
            SetDrawPath(SequenceLateDriver.VerdictBorrowedGameLine);
            if (!_loggedGameLine)
            {
                _loggedGameLine = true;
                Plugin.Logger?.LogInfo(
                    "Effects: sequence beam body = " + UnityEffectRuntime.FormatGameLineSource(chosen, beam));
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Effects: game line adopt('" + chosen + "') threw: " + e.Message);
            DestroyGameLine();
        }
    }

    private void ApplyGameLineWidth()
    {
        if (_gameLine == null) return;
        if (!_ownedLineWidthSet) return;
        try { _gameLine.widthMultiplier = 1f; } catch { /* */ }
        try { _gameLine.SetWidthCurve(AnimationCurve.Constant(0f, 1f, 1f)); } catch { /* */ }
        try
        {
            _gameLine.startWidth = _ownedLineWidth;
            _gameLine.endWidth = _ownedLineWidth;
        }
        catch { /* */ }
    }

    /// <summary>
    /// Vertex colors always (harmless when the graph ignores them); material properties only
    /// when the plugin set <c>Color</c> itself. Borrowing exists to keep the vanilla look, so
    /// a preset default never repaints the game's material.
    /// </summary>
    private void ApplyGameLineColor()
    {
        if (_gameLine == null) return;
        var color = _hasPendingColor ? _pendingColor : _lastTintedColor;
        if (color.A <= 0f && color.R <= 0f && color.G <= 0f && color.B <= 0f) return;
        var unity = (Color)color;
        try
        {
            _gameLine.startColor = unity;
            _gameLine.endColor = unity;
        }
        catch { /* */ }

        if (!_explicitTint) return;
        if (_gameLineMaterial == null)
        {
            Material? shared = null;
            try { shared = _gameLine.sharedMaterial; }
            catch { /* */ }
            if (shared == null) return;
            try { _gameLineMaterial = new Material(shared) { name = "progenitor-borrowed-beam" }; }
            catch { _gameLineMaterial = null; }
            if (_gameLineMaterial == null) return;
            try { _gameLine.sharedMaterial = _gameLineMaterial; }
            catch { /* */ }
        }

        var mat = _gameLineMaterial;
        try { mat.color = unity; } catch { /* */ }
        try
        {
            if (mat.HasProperty("_UnlitColor")) mat.SetColor("_UnlitColor", unity);
            else if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", unity);
            else if (mat.HasProperty("_Color")) mat.SetColor("_Color", unity);
            if (mat.HasProperty("_EmissionColor")) mat.SetColor("_EmissionColor", unity);
            if (mat.HasProperty("_EmissiveColor")) mat.SetColor("_EmissiveColor", unity);
        }
        catch { /* */ }
    }

    private void WriteGameLine()
    {
        if (_gameLine == null) return;
        try
        {
            _gameLine.SetPosition(0, _ownedPos0);
            _gameLine.SetPosition(1, _ownedPos1);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Effects: borrowed game line SetPosition threw: " + e.Message);
            _gameLine = null;
        }
    }

    private void DestroyGameLine()
    {
        if (_gameLineGo != null)
        {
            try { UnityEngine.Object.DestroyImmediate(_gameLineGo); }
            catch
            {
                try { UnityEngine.Object.Destroy(_gameLineGo); }
                catch { /* */ }
            }

            _gameLineGo = null;
            _gameLine = null;
        }

        if (_gameLineMaterial != null)
        {
            try { UnityEngine.Object.Destroy(_gameLineMaterial); }
            catch { /* */ }
            _gameLineMaterial = null;
        }
    }

    private void SetDrawPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        _drawPath = path;
        _lateWriteVerdict = path;
        LogDrawPathOnce();
    }

    private void LogDrawPathOnce()
    {
        if (_loggedDrawPath) return;
        if (string.IsNullOrEmpty(_drawPath)) return;
        if (_drawPath == SequenceLateDriver.VerdictOwnedLine) return;
        _loggedDrawPath = true;
        Plugin.Logger?.LogInfo(SequenceLateDriver.FormatDrawPathLog(_drawPath));
    }

    private void MaybePromoteGameLinePath()
    {
        if (_drawPath == SequenceLateDriver.VerdictGameLineBuffer) return;
        if (!_ownedLineStyleCopied) return;
        if (IsTrailSpriteShader(_ownedLineMaterialName)) return;
        SetDrawPath(SequenceLateDriver.VerdictGameLineBuffer);
    }

    private void ApplyPending()
    {
        ResolveTrackedEndpoints();
        UnityEffectRuntime.TryMoveMarker(_sequenceWorld, _startMarker, _beamStart, _transformWrites);
        UnityEffectRuntime.TryMoveMarker(_sequenceWorld, _endMarker, _beamEnd, _transformWrites);
        UnityEffectRuntime.TryReposeSequence(_sequenceWorld, _sequenceEntity, _beamStart, _transformWrites);

        TryBindSequenceDataSourceAfterSpawn();

        if (GameObject == null) return;

        if (!_loggedDriverInspect)
        {
            UnityEffectRuntime.LogDriverComponentsOnce(GameObject);
            _loggedDriverInspect = true;
        }

        if (_hasPendingColor)
        {
            // Sequence clones come out of ParticlePool and go back in: never instance,
            // color or destroy anything on them (EV7 magenta wedge, R26 finding 3).
            // The tint lives on the owned line and on the borrowed prefab copy.
            if (UnityEffectRuntime.TintGameObject(
                    GameObject, _pendingColor, _tintedMaterials, OwnsGameObject, Kind))
                _tintAppliedToDriver = true;

            _hasPendingColor = false;
            ApplyOwnedLineColor();
        }
    }

    /// <summary>
    /// Bind <c>SequenceInstance.Target</c> only after the play frame and only once the
    /// hybrid driver GO exists. CheckIfShouldSpawn requires LocalToWorld on the spawn
    /// candidate; poking Target at play time to a foreign entity aborted
    /// the VFX instantiate (evidence 96). The clone landing is the proof that first
    /// graph update already ran with Target left default.
    /// </summary>
    private void TryBindSequenceDataSourceAfterSpawn()
    {
        if (!Assets.BindSequencerMarkers) return;
        if (_boundSequenceDataSource || _sequenceEntity == Entity.Null) return;
        // The clone landing is the proof CheckIfShouldSpawn already ran with Target left
        // default (evidence 95 vs 96). Do not poke SequenceInstance before that GO exists.
        if (GameObject == null) return;
        if (_sequenceWorld == null || !_sequenceWorld.IsCreated) return;
        try
        {
            var em = _sequenceWorld.EntityManager;
            _boundSequenceDataSource = UnityEffectRuntime.TryBindSequenceDataSource(
                em, _sequenceEntity, _startMarker, _endMarker);
            if (!_loggedBeamPath)
            {
                _loggedBeamPath = true;
                UnityEffectRuntime.LogBeamPath(
                    new UnityEffectRuntime.SequencePlayResult
                    {
                        Sequence = _sequenceEntity,
                        StartMarker = _startMarker,
                        EndMarker = _endMarker,
                        BoundDataSource = _boundSequenceDataSource,
                    },
                    _beamStart,
                    _beamEnd);
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Effects: delayed SequenceInstance bind threw: " + e.Message);
        }
    }

    public EffectColor Color
    {
        get
        {
            if (GameObject != null) return UnityEffectRuntime.ReadColor(GameObject);
            return _hasPendingColor ? _pendingColor : default;
        }
        set
        {
            _pendingColor = value;
            _hasPendingColor = true;
            // A plugin asked for this color, so the borrowed game material may be repainted;
            // a preset default never is (borrowing exists to keep the vanilla look).
            _explicitTint = true;
            ApplyOwnedLineColor();
            if (Kind == AssetKind.Sequence)
            {
                // Owned line (or the borrowed prefab copy) is the drawable; the pooled
                // sequencer clone is never tinted (R26 finding 3).
                ApplyGameLineColor();
                _tintAppliedToDriver = HasOwnedLine || HasGameLine;
                return;
            }

            if (GameObject != null
                && UnityEffectRuntime.TintGameObject(
                    GameObject, value, _tintedMaterials, OwnsGameObject, Kind))
            {
                _tintAppliedToDriver = true;
                _hasPendingColor = false;
            }
        }
    }

    public EffectVec2 Size
    {
        get
        {
            try
            {
                if (GameObject == null) return default;
                var rt = GameObject.GetComponent<RectTransform>();
                if (rt == null) return default;
                var s = rt.sizeDelta;
                return new EffectVec2(s.x, s.y);
            }
            catch { return default; }
        }
        set
        {
            try
            {
                if (GameObject == null) return;
                var rt = GameObject.GetComponent<RectTransform>() ?? GameObject.AddComponent<RectTransform>();
                rt.sizeDelta = new Vector2(value.X, value.Y);
            }
            catch { /* */ }
        }
    }

    public EffectVec2 ScreenPosition
    {
        get
        {
            try
            {
                if (GameObject == null) return default;
                var rt = GameObject.GetComponent<RectTransform>();
                if (rt == null) return default;
                var p = rt.anchoredPosition;
                return new EffectVec2(p.x, p.y);
            }
            catch { return default; }
        }
        set
        {
            try
            {
                if (GameObject == null) return;
                var rt = GameObject.GetComponent<RectTransform>() ?? GameObject.AddComponent<RectTransform>();
                rt.anchoredPosition = new Vector2(value.X, value.Y);
            }
            catch { /* */ }
        }
    }

    public EffectVec3 WorldPosition
    {
        get => Kind == AssetKind.Sequence ? _beamStart : ReadWorldPosition();
        set
        {
            if (Kind == AssetKind.Sequence)
            {
                BeamStart = value;
                return;
            }

            try
            {
                if (GameObject == null) return;
                GameObject.transform.position = new Vector3(value.X, value.Y, value.Z);
            }
            catch { /* */ }
        }
    }

    /// <summary>
    /// Uniform scale. Root <c>localScale</c> always; <c>main.startSizeMultiplier</c> when
    /// <c>main.scalingMode</c> is not Hierarchy (interop: Hierarchy, Local, Shape). Logged once.
    /// </summary>
    public float UniformScale
    {
        get => _uniformScale;
        set => ApplyParticleScale(value);
    }

    public EffectVec3 BeamStart
    {
        get => _beamStart;
        set
        {
            _beamStart = value;
            if (_sequencePlayed)
                ApplyPending();
        }
    }

    public EffectVec3 BeamEnd
    {
        get => _beamEnd;
        set
        {
            _beamEnd = value;
            if (_sequencePlayed)
                ApplyPending();
        }
    }

    public float Intensity
    {
        get
        {
            if (Volume != null)
            {
                UnityEffectRuntime.TryReadVignetteFloats(Volume, out float i, out _);
                return i;
            }

            return _intensity;
        }
        set
        {
            _intensity = value;
            if (Volume != null)
                UnityEffectRuntime.SetVignetteFloats(Volume, _intensity, _smoothness);
        }
    }

    public float Smoothness
    {
        get
        {
            if (Volume != null)
            {
                UnityEffectRuntime.TryReadVignetteFloats(Volume, out _, out float s);
                return s;
            }

            return _smoothness;
        }
        set
        {
            _smoothness = value;
            if (Volume != null)
                UnityEffectRuntime.SetVignetteFloats(Volume, _intensity, _smoothness);
        }
    }

    private EffectVec3 ReadWorldPosition()
    {
        try
        {
            if (GameObject == null) return default;
            var p = GameObject.transform.position;
            return new EffectVec3(p.x, p.y, p.z);
        }
        catch { return default; }
    }

    private void ApplyParticleScale(float scale)
    {
        _uniformScale = scale;
        if (Kind != AssetKind.Particle) return;
        if (GameObject == null) return;

        try
        {
            GameObject.transform.localScale = new Vector3(scale, scale, scale);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Effects: particle localScale threw: " + e.Message);
        }

        bool wroteStartSize = false;
        string modeName = "Hierarchy";
        try
        {
            if (Particle != null)
            {
                // MainModule.scalingMode / startSizeMultiplier are native; this interop
                // only recovered the ParticleSystem convenience wrappers (they read/write
                // main.startSizeMultiplier and main.scalingMode). Hierarchy uses transform.
                var mode = Particle.scalingMode;
                modeName = mode.ToString();
                if (mode != ParticleSystemScalingMode.Hierarchy)
                {
                    if (!_capturedBaseStartSize)
                    {
                        try { _baseStartSize = Particle.startSize; }
                        catch { _baseStartSize = 1f; }
                        if (_baseStartSize == 0f) _baseStartSize = 1f;
                        _capturedBaseStartSize = true;
                    }

                    Particle.startSize = _baseStartSize * scale;
                    wroteStartSize = true;
                }
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Effects: particle startSizeMultiplier threw: " + e.Message);
        }

        if (_loggedScalePath) return;
        _loggedScalePath = true;
        string path = wroteStartSize
            ? "transform + startSize (" + modeName + ")"
            : "transform (" + modeName + ")";
        Plugin.Logger?.LogInfo(
            "Effects: particle '" + Name + "' scale " + scale.ToString("0.##") + " via " + path);
    }

    public int SortingOrder
    {
        get
        {
            try { return OverlayCanvas != null ? OverlayCanvas.sortingOrder : 0; }
            catch { return 0; }
        }
        set
        {
            try
            {
                if (OverlayCanvas != null) OverlayCanvas.sortingOrder = value;
            }
            catch { /* */ }
        }
    }

    public void Play()
    {
        if (Kind == AssetKind.Sequence)
            EnsureSequencePlayed();
        try { Particle?.Play(true); } catch { /* */ }
        try
        {
            var beam = GameObject != null ? GameObject.GetComponent<BeamEmitter>() : null;
            beam?.Play();
        }
        catch { /* */ }
    }

    public void Stop()
    {
        try { Particle?.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear); } catch { /* */ }
        try
        {
            var beam = GameObject != null ? GameObject.GetComponent<BeamEmitter>() : null;
            beam?.Stop(true);
        }
        catch { /* */ }
    }

    public void Release()
    {
        if (_released) return;
        _released = true;
        Stop();
        try
        {
            if (Image != null) Image.enabled = false;
        }
        catch { /* */ }
        try
        {
            if (Volume != null)
            {
                Volume.enabled = false;
                Volume.weight = 0f;
                Volume.sharedProfile = null;
            }
        }
        catch { /* */ }

        SequenceLateDriver.Unwatch(this);
        if (Kind == AssetKind.Sequence)
        {
            UnityEffectRuntime.ForgetBoundDriver(GameObject, this);
            RestorePoolParent(deactivate: true);
            GameObject = null;
            DestroyOwnedLine();
            // The borrowed body is our own Instantiate, so it (and the one material we
            // copied for it) is ours to destroy. The source prefab is never touched.
            DestroyGameLine();
            // Never destroy clone-instanced materials: sequences do not tint the
            // pooled renderer (EV7 assigned Renderer.material then Destroy'd it).
        }
        else
        {
            if (OwnsGameObject && GameObject != null)
            {
                try { UnityEngine.Object.DestroyImmediate(GameObject); }
                catch
                {
                    try { UnityEngine.Object.Destroy(GameObject); } catch { /* */ }
                }

                GameObject = null;
            }
        }

        // Sequence clones are pooled and keep their original sharedMaterial. Destroying
        // instanced materials that were assigned to them is the EV7 magenta wedge
        // (evidence 125). Only owned-line materials are destroyed, in DestroyOwnedLine.
        if (Kind != AssetKind.Sequence)
        {
            for (int i = 0; i < _tintedMaterials.Count; i++)
            {
                try
                {
                    if (_tintedMaterials[i] != null)
                        UnityEngine.Object.Destroy(_tintedMaterials[i]);
                }
                catch { /* */ }
            }
        }

        _tintedMaterials.Clear();

        if (Profile != null)
        {
            try { UnityEngine.Object.DestroyImmediate(Profile); } catch { /* */ }
        }

        LogLateSummary();
        UnityEffectRuntime.TryDestroyEntity(_sequenceWorld, _sequenceEntity);
        UnityEffectRuntime.TryDestroyEntity(_sequenceWorld, _startMarker);
        UnityEffectRuntime.TryDestroyEntity(_sequenceWorld, _endMarker);
        _sequenceEntity = Entity.Null;
        _startMarker = Entity.Null;
        _endMarker = Entity.Null;
        _sequenceWorld = null;
        DriverActive = false;
        HybridLastWrite = false;
    }
}

internal sealed class UnityHostSurface : IRuntimeHost
{
    private bool _released;
    private readonly GameObject? _canvas;
    private readonly GameObject? _cameraGo;
    private readonly Camera? _camera;
    private RenderTexture? _rt;
    private List<(GameObject go, int layer)>? _layers;
    private readonly UnityEffectRuntime? _runtime;
    private readonly RawImage? _raw;
    private readonly string _rtName;
    private readonly GameObject? _borrowed;
    private readonly GameObject? _pluginRoot;

    internal UnityHostSurface(
        HostKind kind,
        GameObject? canvas,
        Camera? camera,
        RenderTexture? rt,
        GameObject? cameraGo = null,
        List<(GameObject go, int layer)>? layers = null,
        int hostedLayer = -1,
        UnityEffectRuntime? runtime = null,
        RawImage? raw = null,
        string? rtName = null,
        GameObject? borrowed = null,
        GameObject? pluginRoot = null)
    {
        Kind = kind;
        _canvas = canvas;
        _camera = camera;
        _rt = rt;
        _cameraGo = cameraGo;
        _layers = layers;
        HostedLayer = hostedLayer;
        _runtime = runtime;
        _raw = raw;
        _rtName = rtName ?? (rt != null ? rt.name : "");
        RtWidth = rt != null ? rt.width : 0;
        RtHeight = rt != null ? rt.height : 0;
        _borrowed = borrowed;
        _pluginRoot = pluginRoot;
    }

    public HostKind Kind { get; }

    public bool Alive => !_released;

    public int HostedLayer { get; }

    public int RtWidth { get; private set; }

    public int RtHeight { get; private set; }

    public void SyncScreenSize()
    {
        if (_released || Kind != HostKind.RenderTexture || _rt == null) return;
        var size = _runtime != null ? _runtime.ScreenSize : new EffectVec2(Screen.width, Screen.height);
        int w = Math.Max(16, (int)size.X);
        int h = Math.Max(16, (int)size.Y);
        if (w == RtWidth && h == RtHeight) return;
        RenderTexture? next;
        try
        {
            next = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32) { name = _rtName };
            if (!next.Create())
                Plugin.Logger?.LogWarning("Effects: RenderTexture resize Create returned false");
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Effects: RenderTexture resize: " + e.Message);
            return;
        }

        try
        {
            if (_camera != null) _camera.targetTexture = next;
        }
        catch { /* */ }
        try
        {
            if (_raw != null) _raw.texture = next;
        }
        catch { /* */ }

        var old = _rt;
        _rt = next;
        RtWidth = w;
        RtHeight = h;
        try
        {
            old.Release();
            UnityEngine.Object.Destroy(old);
        }
        catch { /* */ }

        Plugin.Logger?.LogInfo("Effects: RT host resized to " + w + "x" + h);
    }

    public void AttachDriver(IRuntimeObject obj)
    {
        if (_released || HostedLayer < 8) return;
        if (obj is not UnityBorrowedObject u) return;
        if (!UnityEffectRuntime.IsAlive(u.GameObject) || u.GameObject == null) return;
        if (_layers != null) return;
        _layers = UnityEffectRuntime.SnapshotAndSetLayer(u.GameObject, HostedLayer);
        Plugin.Logger?.LogInfo(
            "Effects: RT host moved driver '" + UnityEffectRuntime.SafeName(u.GameObject)
            + "' to layer " + HostedLayer + " (restore on dispose).");
    }

    public void Release()
    {
        if (_released) return;
        _released = true;
        UnityEffectRuntime.RestoreLayers(_layers);
        try
        {
            if (_camera != null)
            {
                _camera.enabled = false;
                _camera.targetTexture = null;
            }
        }
        catch { /* */ }

        if (_rt != null)
        {
            try
            {
                _rt.Release();
                UnityEngine.Object.Destroy(_rt);
            }
            catch { /* */ }
        }

        try { if (_cameraGo != null) UnityEngine.Object.DestroyImmediate(_cameraGo); } catch { /* */ }
        if (Kind == HostKind.Overlay && _borrowed != null && _pluginRoot != null)
        {
            try
            {
                if (UnityEffectRuntime.IsAlive(_borrowed) && UnityEffectRuntime.IsAlive(_pluginRoot))
                    _borrowed.transform.SetParent(_pluginRoot.transform, worldPositionStays: true);
            }
            catch { /* */ }
        }

        try { if (_canvas != null) UnityEngine.Object.DestroyImmediate(_canvas); } catch { /* */ }
    }
}

#endif
