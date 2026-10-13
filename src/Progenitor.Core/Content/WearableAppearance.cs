#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using ProjectM;
using ProjectM.Hybrid;
using Progenitor.Ecs;
using Stunlock.Core;
using Unity.Entities;
using UnityEngine;
using UnityEngine.Rendering;

namespace Progenitor.Content;

/// <summary>
/// Client pass that puts a wearable's sketch on the wearer. While an
/// <see cref="Equipment"/> slot holds a cloned item that registered
/// <see cref="WearableGraphics"/>, this builds that sketch as a
/// <c>SkinnedMeshRenderer</c> on the character's bones — the same joint names
/// <c>HybridEquipmentUser</c> binds — and switches off the donor renderers the
/// sketch names. Unequip, unload and a new model instance put those renderers
/// back and destroy the mesh.
///
/// <para>The mesh is measured from compatible source mesh bind poses, so equipping
/// during an animation does not make that animation the garment's rest pose.
/// Body clearance and animation fit still require live verification.</para>
/// </summary>
internal static partial class WearableAppearance
{
    private const int Interval = 8;
    private const int PendingHideFrameLimit = 120;
    private const string RootPrefix = "progenitor-wear-";

    private static bool _hooked;
    private static int _frame;
    private static int _lastLateFrame = -1000;
    private static bool _shaderMissing;
    private static Material? _cloth;
    private static Material? _gold;
    private static Material? _void;
    private static readonly Dictionary<long, Worn> Active = new();
    private static readonly Dictionary<long, PendingHide> Pending = new();
    private static readonly Dictionary<int, HideHold> Hidden = new();
    private static readonly HashSet<int> Logged = new();
    // Native Body_GEO renderers per wearer model, refreshed on reference-check passes.
    private static readonly Dictionary<int, SkinnedMeshRenderer[]> BodyRenderers = new();
    // Last game visibility applied per wearer model, for one diagnostic line per change.
    private static readonly Dictionary<int, bool> ModelShown = new();

    internal static void Install()
    {
        if (_hooked) return;
        Plugin.Frame += Tick;
        _hooked = true;
    }

    internal static void Uninstall()
    {
        if (_hooked)
        {
            try { Plugin.Frame -= Tick; }
            catch { /* Frame may already be null on uninstall */ }
            _hooked = false;
        }
        ReleaseAll();
        DestroyMaterial(_cloth);
        DestroyMaterial(_gold);
        DestroyMaterial(_void);
        _cloth = _gold = _void = null;
        _shaderMissing = false;
        Logged.Clear();
        _frame = 0;
        _lastLateFrame = -1000;
    }

    private static void Tick()
    {
        if (Plugin.IsServerProcess) return;
        // The normal client pass runs after native presentation and before rendering.
        // Retain the old eight-frame pump only if that late callback is unavailable.
        // The frame Core's dispatch already read (Time.frameCount boxes its int on the IL2CPP heap).
        int frame = Plugin.TryDispatchFrame(out int dispatched) ? dispatched : Time.frameCount;
        if (frame - _lastLateFrame <= 2) return;
        if (++_frame % Interval != 0) return;
        try { Pass(checkReferences: true); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Wearables: pass threw: " + e.Message); }
    }

    internal static void LateTick()
    {
        if (!_hooked || Plugin.IsServerProcess) return;
        _lastLateFrame = Time.frameCount;
        try { Pass(checkReferences: ++_frame % Interval == 0); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Wearables: late pass threw: " + e.Message); }
    }

    private static void Pass(bool checkReferences)
    {
        var wanted = new Dictionary<long, Want>();
        var graphics = Collect();
        if (graphics.Count == 0)
        {
            LogOnce(1, "Wearables: Collect empty (no Wearable definitions with parts).");
            ReleaseAll();
            return;
        }
        LogOnce(2, "Wearables: Collect count=" + graphics.Count);

        var world = Worlds.Client;
        if (world == null || !world.IsCreated)
        {
            ReleaseAll();
            return;
        }

        var em = world.EntityManager;
        var query = Query.Build(
            em,
            includeDisabled: true,
            includeSystems: false,
            ComponentIndex.Of<Equipment>(ComponentType.AccessMode.ReadOnly),
            ComponentIndex.Of<HybridModelUser>(ComponentType.AccessMode.ReadOnly));
        try
        {
            var entities = query.ToEntityArray(Unity.Collections.Allocator.Temp);
            try
            {
                foreach (var entity in entities)
                {
                    if (!em.Exists(entity)) continue;
                    Equipment equipment;
                    try { equipment = em.GetComponentData<Equipment>(entity); }
                    catch { continue; }
                    var model = ModelOf(em, entity);
                    if (model == null) continue;
                    int modelId = model.GetInstanceID();
                    foreach (var pair in graphics)
                    {
                        // Cosmetic equipment owns the visible slot when present, including
                        // vanilla cosmetics that must suppress a custom stats-item overlay.
                        var stats = SlotOf(equipment, pair.Value.Slot);
                        var visibleSlot = stats;
                        var statsGuid = ResolveWornGuid(em, stats, graphics);
                        int cosmeticGuid = 0;
                        bool cosmeticOverride = false;
                        bool cosmeticHidden = false;
                        if (TryCosmeticSlotOf(equipment, pair.Value.Slot, out var cosmetic))
                        {
                            // The native hybrid equipment pass selects a cosmetic slot when
                            // its SlotId is set, then applies that slot's hide state.
                            cosmeticOverride = cosmetic.SlotId.GuidHash != 0;
                            if (cosmeticOverride)
                            {
                                visibleSlot = cosmetic;
                                cosmeticGuid = ResolveWornGuid(em, cosmetic, graphics).GuidHash;
                                cosmeticHidden = cosmetic.HideEquipment || cosmetic.IsBroken;
                            }
                        }
                        int visibleGuid = WearableSlotSelection.Select(statsGuid.GuidHash,
                            stats.HideEquipment || stats.IsBroken,
                            cosmeticGuid, cosmeticOverride, cosmeticHidden);
                        if (visibleGuid != pair.Key) continue;
                        long key = Key(modelId, pair.Key);
                        Dictionary<string, Color>? palette = null;
                        if (pair.Value.PaletteMode == WearablePaletteMode.Native)
                        {
                            var paletteSlot = pair.Value.PaletteSource == pair.Value.Slot
                                ? visibleSlot : EffectiveSlotOf(equipment, pair.Value.PaletteSource);
                            palette = ReadPalette(world, paletteSlot, pair.Value);
                        }
                        wanted[key] = new Want(model, pair.Value, pair.Key,
                            palette);
                    }
                }
            }
            finally { entities.Dispose(); }
        }
        finally { query.Dispose(); }

        var stale = new List<long>();
        foreach (var pair in Active)
            if (!wanted.ContainsKey(pair.Key)) stale.Add(pair.Key);
        for (int i = 0; i < stale.Count; i++) Release(stale[i]);
        stale.Clear();
        foreach (var pair in Pending)
            if (!wanted.TryGetValue(pair.Key, out var pendingWant)
                || !ReferenceEquals(pair.Value.Graphics, pendingWant.Graphics)) stale.Add(pair.Key);
        for (int i = 0; i < stale.Count; i++) ReleasePending(stale[i]);

        var referenceSets = new Dictionary<int, ulong>();
        var modelVisibility = new Dictionary<int, bool>();
        if (checkReferences) RefreshBodyCache(wanted);
        foreach (var pair in wanted)
        {
            try
            {
                if (Active.TryGetValue(pair.Key, out var previous)
                    && (previous.Root == null || !ReferenceEquals(previous.Graphics, pair.Value.Graphics)))
                {
                    // A plugin can replace a recipe for the same item ID without changing
                    // native meshes. Release the old recipe's hide leases before attaching
                    // the new geometry and its independently declared donor hides.
                    Release(pair.Key);
                    ClearLogged(pair.Value.Guid);
                    Plugin.Logger?.LogInfo("Wearables: graphics recipe changed; rebuilding " + pair.Value.Guid);
                }
                bool attached = Active.TryGetValue(pair.Key, out var current);
                PendingHide? retryPending = null;
                if (!attached)
                {
                    EnsurePending(pair.Key, pair.Value);
                    retryPending = Pending[pair.Key];
                }
                // A failed bind may stay incompatible for many frames (for example,
                // a body with no matching authored variant). Keep pending-hide timeout
                // running, but inspect native references only on the regular tick or
                // when the bounded retry is due.
                if (retryPending != null && !retryPending.Retry.ShouldInspect(checkReferences, Time.frameCount))
                    continue;
                ulong references = attached && !checkReferences ? current!.References : 0UL;
                if (!attached || checkReferences)
                {
                    int modelId = pair.Value.Model.GetInstanceID();
                    if (!referenceSets.TryGetValue(modelId, out references))
                        referenceSets[modelId] = references = ReferenceFingerprint(pair.Value.Model);
                }
                if (Active.TryGetValue(pair.Key, out var worn))
                {
                    ApplyModelVisibility(worn, pair.Value, modelVisibility);
                    UpdatePalette(worn.PaletteMaterials, pair.Value.Palette);
                    // Native appearance updates can replace or re-enable hair without changing
                    // the skinned reference mesh set. Maintain each garment's existing lease.
                    worn.Hidden = HideDonors(pair.Value.Model, pair.Value.Graphics.HideNames,
                        pair.Value.Graphics.Slot, worn.Hidden);
                    if (worn.NativeSource != null)
                    {
                        worn.NativeSource.enabled = false;
                        worn.Root.transform.localPosition = worn.NativeSource.transform.localPosition;
                        worn.Root.transform.localRotation = worn.NativeSource.transform.localRotation;
                        worn.Root.transform.localScale = worn.NativeSource.transform.localScale;
                    }
                    if (SyncEffect(worn, pair.Value.Model)) continue;
                    if (!checkReferences) continue;
                    if (worn.References == references)
                    {
                        worn.PendingReferences = references;
                        worn.ReferencePasses = 0;
                        continue;
                    }
                    if (worn.PendingReferences != references)
                    {
                        worn.PendingReferences = references;
                        worn.ReferencePasses = 1;
                        continue;
                    }
                    // Two consecutive passes debounce staged equipment construction.
                    if (++worn.ReferencePasses < 2) continue;
                    // Keep the working appearance until its replacement validates.
                    // Abilities can temporarily substitute an incompatible material.
                }
                if (retryPending != null && !retryPending.Retry.ShouldAttempt(references, Time.frameCount))
                    continue;
                Worn? replacement;
                try { replacement = Attach(pair.Value, references); }
                catch
                {
                    retryPending?.Retry.Failed(references, Time.frameCount);
                    throw;
                }
                if (replacement != null)
                {
                    Active[pair.Key] = replacement;
                    ApplyModelVisibility(replacement, pair.Value, modelVisibility);
                    replacement.Root.SetActive(true);
                    if (replacement.OverlayRoot != null) replacement.OverlayRoot.SetActive(true);
                    if (worn != null) ReleaseWorn(worn);
                    ReleasePending(pair.Key);
                }
                else retryPending?.Retry.Failed(references, Time.frameCount);
            }
            catch (Exception e) { Plugin.Logger?.LogWarning("Wearables: attach threw: " + e.Message); }
        }
    }

    private static ulong ReferenceFingerprint(Transform model)
    {
        var renderers = new List<SkinnedMeshRenderer>();
        foreach (var renderer in model.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            if (renderer != null && !Ours(renderer.transform)) renderers.Add(renderer);
        renderers.Sort((a, b) => a.GetInstanceID().CompareTo(b.GetInstanceID()));
        ulong hash = 14695981039346656037UL;
        void Add(int value) { unchecked { hash = (hash ^ (uint)value) * 1099511628211UL; } }
        Add(renderers.Count);
        foreach (var renderer in renderers)
        {
            Add(renderer.GetInstanceID());
            Add(renderer.transform.parent == null ? 0 : renderer.transform.parent.GetInstanceID());
            var mesh = renderer.sharedMesh;
            Add(mesh == null ? 0 : mesh.GetInstanceID());
            if (mesh != null)
            {
                Add(mesh.vertexCount);
                Add(mesh.bindposes.Length);
                Add(mesh.isReadable ? 1 : 0);
                Add(mesh.HasBoneWeights() ? 1 : 0);
            }
            var materials = renderer.sharedMaterials;
            Add(materials.Length);
            foreach (var material in materials) Add(material == null ? 0 : material.GetInstanceID());
            var bones = renderer.bones;
            Add(bones.Length);
            foreach (var bone in bones)
            {
                Add(bone == null ? 0 : bone.GetInstanceID());
                Add(bone == null || bone.parent == null ? 0 : bone.parent.GetInstanceID());
            }
        }
        // Deliberately exclude enabled/active flags, generated objects and all animated
        // transforms: hiding donors or changing pose cannot trigger a rebuild loop.
        return hash;
    }

    private static Dictionary<int, WearableGraphics> Collect()
    {
        var map = new Dictionary<int, WearableGraphics>();
        var defs = PrefabRegistry.Definitions;
        for (int i = 0; i < defs.Count; i++)
        {
            var wearable = defs[i].Wearable;
            if (wearable == null || wearable.PartCount == 0) continue;
            map[defs[i].Guid.GuidHash] = wearable;
        }
        return map;
    }

    private static Worn? Attach(Want want, ulong referenceFingerprint)
    {
        var model = want.Model;
        if (model == null) return null;
        if (want.Graphics.NativeRenderer != null)
        {
            return AttachNative(want, referenceFingerprint);
        }
        if (!TryBind(model, want.Graphics, out var bindings, out var mesh))
        {
            LogOnce(want.Guid, "Wearables: guid " + want.Guid + " has no complete compatible bind pose yet"
                + "; will retry.");
            return null;
        }
        var clearance = MeasureTorso(model, bindings);
        var references = SkinReferences(model, bindings, want.Graphics.Slot);
        if (mesh.Authored == null) mesh = BuildBound(want.Graphics, bindings, clearance, references.Body);
        if (!EnsureMaterials())
        {
            LogOnce(0, "Wearables: HDRP/Lit or HDRP/Unlit was not found; worn meshes were not attached.");
            return null;
        }

        var root = new GameObject(RootPrefix + want.Guid);
        root.SetActive(false);
        root.transform.SetParent(model, false);
        root.transform.localPosition = Vector3.zero;
        root.transform.localRotation = Quaternion.identity;
        root.transform.localScale = Vector3.one;
        var smr = root.AddComponent<SkinnedMeshRenderer>();
        var unityMesh = new Mesh { name = root.name };
        var ownedMaterials = new List<Material>();
        var paletteMaterials = new List<PaletteMaterial>();
        int[] hidden = Array.Empty<int>();
        try
        {
            int transferred = Fill(unityMesh, smr, model, mesh, bindings, references, out int bodyTransferred);
            smr.sharedMesh = unityMesh;
            smr.updateWhenOffscreen = true;
            smr.shadowCastingMode = ShadowCastingMode.On;
            smr.sharedMaterials = MaterialsFor(mesh, ownedMaterials, paletteMaterials);
            UpdatePalette(paletteMaterials, want.Palette);

            hidden = HideDonors(model, want.Graphics.HideNames, want.Graphics.Slot);
            ClearLogged(want.Guid);
            LogOnce(want.Guid, "Wearables: attached " + want.Guid
                + " verts=" + mesh.Vertices.Length
                + " cloth=" + (mesh.Cloth.Length / 3)
                + " gold=" + (mesh.Gold.Length / 3)
                + " void=" + (mesh.Void.Length / 3)
                + " hidden=" + hidden.Length
                + " fitPoints=" + clearance.Count + " transferred=" + transferred
                + " bodyTransferred=" + bodyTransferred + " " + references.Diagnostics
                + (mesh.Missing.Length > 0 ? " missing=" + string.Join(",", mesh.Missing) : ""));
            return new Worn(root, unityMesh, true, hidden, referenceFingerprint, want.Graphics, ownedMaterials.ToArray(), paletteMaterials);
        }
        catch
        {
            ReleaseHidden(hidden);
            foreach (var material in ownedMaterials) DestroyMaterial(material);
            UnityEngine.Object.Destroy(unityMesh);
            UnityEngine.Object.Destroy(root);
            throw;
        }
    }

    private sealed class BoundJoint
    {
        internal BoundJoint(Transform bone, Matrix4x4 joint)
        {
            Bone = bone;
            Joint = joint;
            Bind = joint.inverse;
        }

        internal Transform Bone { get; }
        internal Matrix4x4 Joint { get; }
        internal Matrix4x4 Bind { get; }
    }

    private static bool TryBind(Transform model, WearableGraphics graphics,
        out Dictionary<string, BoundJoint> selected, out WearableMesh built)
    {
        selected = new Dictionary<string, BoundJoint>(StringComparer.Ordinal);
        built = graphics.Build(new Dictionary<string, WearableVec3>(StringComparer.Ordinal));
        var renderers = model.GetComponentsInChildren<SkinnedMeshRenderer>(true);
        var rigBones = new HashSet<int>();
        foreach (var renderer in renderers)
        {
            if (renderer == null || Ours(renderer.transform)) continue;
            foreach (var bone in renderer.bones)
                if (bone != null) rigBones.Add(bone.GetInstanceID());
        }

        var candidates = new List<Dictionary<string, BoundJoint>>();
        foreach (var renderer in renderers)
        {
            if (renderer == null || Ours(renderer.transform) || renderer.sharedMesh == null) continue;
            // A renderer carried by an animated joint does not provide a stable model frame.
            // Non-rig renderer ancestors must also remain static relative to the model.
            bool safeFrame = true;
            for (var p = renderer.transform; p != null && p != model; p = p.parent)
                if (rigBones.Contains(p.GetInstanceID())) { safeFrame = false; break; }
            if (!safeFrame) continue;
            var bones = renderer.bones;
            var binds = renderer.sharedMesh.bindposes;
            if (bones.Length == 0 || bones.Length != binds.Length) continue;
            var toModel = model.worldToLocalMatrix * renderer.transform.localToWorldMatrix;
            if (!ValidMatrix(toModel)) continue;
            var candidate = new Dictionary<string, BoundJoint>(StringComparer.Ordinal);
            bool valid = true;
            for (int i = 0; i < bones.Length; i++)
            {
                var bone = bones[i];
                if (bone == null || !Under(bone, model) || !ValidMatrix(binds[i])) { valid = false; break; }
                var joint = toModel * binds[i].inverse;
                if (!ValidMatrix(joint)) { valid = false; break; }
                var entry = new BoundJoint(bone, joint);
                if (candidate.TryGetValue(bone.name, out var previous) && !SameJoint(previous, entry))
                { valid = false; break; }
                candidate[bone.name] = entry;
            }
            if (valid && candidate.Count > 0) candidates.Add(candidate);
        }
        candidates.Sort((a, b) => b.Count.CompareTo(a.Count));
        // Prefer one coherent donor; only supplement when overlapping joint identities and
        // full rest matrices agree. Never mix unrelated rigs using bone names alone.
        foreach (var candidate in candidates)
        {
            if (TryBuildCompatible(graphics, candidate, model, out var mesh))
            { selected = candidate; built = mesh; return true; }
        }
        foreach (var candidate in candidates)
        {
            var merged = new Dictionary<string, BoundJoint>(candidate, StringComparer.Ordinal);
            foreach (var other in candidates)
            {
                bool overlap = false;
                bool compatible = true;
                foreach (var pair in other)
                {
                    if (!merged.TryGetValue(pair.Key, out var existing)) continue;
                    overlap = true;
                    if (!SameJoint(existing, pair.Value)) { compatible = false; break; }
                }
                if (!overlap || !compatible) continue;
                foreach (var pair in other) merged.TryAdd(pair.Key, pair.Value);
            }
            if (TryBuildCompatible(graphics, merged, model, out var mesh))
            { selected = merged; built = mesh; return true; }
        }
        return false;
    }

    private static bool TryBuildCompatible(WearableGraphics graphics, Dictionary<string, BoundJoint> bindings,
        Transform model, out WearableMesh mesh)
    {
        var variants = graphics.AuthoredVariants;
        if (variants != null || graphics.Authored != null)
        {
            var paths = new Dictionary<string, string>(StringComparer.Ordinal);
            var rests = new Dictionary<string, float[]>(StringComparer.Ordinal);
            foreach (var pair in bindings)
            {
                var components = new List<string>();
                for (var t = pair.Value.Bone; t != null && t != model; t = t.parent)
                    components.Insert(0, t.name + "[" + t.GetSiblingIndex() + "]");
                paths[pair.Key] = model.name + "[" + model.GetSiblingIndex() + "]"
                    + (components.Count == 0 ? "" : "/" + string.Join("/", components));
                var matrix = new float[16];
                for (int r = 0; r < 4; r++) for (int c = 0; c < 4; c++)
                    matrix[r * 4 + c] = pair.Value.Joint[r, c];
                rests[pair.Key] = matrix;
            }
            var selected = graphics.SelectCompatibleAuthored(paths, rests);
            mesh = selected?.Mesh ?? graphics.Authored!.Mesh;
            return selected != null && Complete(mesh);
        }
        mesh = BuildBound(graphics, bindings);
        return Complete(mesh);
    }

    private static bool Under(Transform bone, Transform model)
    {
        for (var p = bone; p != null; p = p.parent)
            if (p == model) return true;
        return false;
    }

    private static bool ValidMatrix(Matrix4x4 matrix)
    {
        for (int row = 0; row < 4; row++)
            for (int col = 0; col < 4; col++)
                if (!float.IsFinite(matrix[row, col])) return false;
        return float.IsFinite(matrix.determinant) && Math.Abs(matrix.determinant) > 0.00000001f;
    }

    private static bool SameJoint(BoundJoint a, BoundJoint b)
    {
        if (a.Bone.GetInstanceID() != b.Bone.GetInstanceID()) return false;
        for (int row = 0; row < 4; row++)
            for (int col = 0; col < 4; col++)
            {
                float x = a.Joint[row, col], y = b.Joint[row, col];
                if (Math.Abs(x - y) > 0.0001f * Math.Max(1f, Math.Max(Math.Abs(x), Math.Abs(y)))) return false;
            }
        return true;
    }

    private static List<WearableVec3> MeasureTorso(Transform model, Dictionary<string, BoundJoint> bindings)
    {
        var points = new List<WearableVec3>();
        foreach (var renderer in model.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            if (renderer == null || renderer.name != "Body_GEO" || Ours(renderer.transform)) continue;
            var source = renderer.sharedMesh;
            if (source == null || !source.isReadable) continue;
            var bones = renderer.bones;
            var binds = source.bindposes;
            if (bones.Length != binds.Length) continue;
            var toModel = model.worldToLocalMatrix * renderer.transform.localToWorldMatrix;
            bool compatible = ValidMatrix(toModel);
            for (int i = 0; compatible && i < bones.Length; i++)
            {
                var b = bones[i];
                compatible = b != null && bindings.TryGetValue(b.name, out var expected)
                    && ValidMatrix(binds[i]) && SameJoint(expected, new BoundJoint(b, toModel * binds[i].inverse));
            }
            if (!compatible) continue;
            var vertices = source.vertices;
            var weights = source.boneWeights;
            if (vertices.Length != weights.Length) continue;
            for (int i = 0; i < vertices.Length; i++)
            {
                var w = weights[i];
                float torso = TorsoWeight(w.boneIndex0, w.weight0) + TorsoWeight(w.boneIndex1, w.weight1)
                    + TorsoWeight(w.boneIndex2, w.weight2) + TorsoWeight(w.boneIndex3, w.weight3);
                if (torso < .5f) continue;
                var p = toModel.MultiplyPoint3x4(vertices[i]);
                if (float.IsFinite(p.x) && float.IsFinite(p.y) && float.IsFinite(p.z))
                    points.Add(new WearableVec3(p.x, p.y, p.z));
            }
            float TorsoWeight(int index, float weight)
            {
                if (weight <= 0f || index < 0 || index >= bones.Length || bones[index] == null) return 0f;
                string name = bones[index].name;
                return name.StartsWith("Pelvis", StringComparison.Ordinal) || name.StartsWith("Spine", StringComparison.Ordinal)
                    || name.StartsWith("Chest", StringComparison.Ordinal) || name.StartsWith("Neck", StringComparison.Ordinal)
                    || name.StartsWith("Ribcage", StringComparison.Ordinal) || name.StartsWith("Gut", StringComparison.Ordinal)
                    || name.Contains("_Hip", StringComparison.Ordinal) || name.Contains("_UpLeg", StringComparison.Ordinal)
                    || name.Contains("_Thigh", StringComparison.Ordinal)
                    || name.Contains("Collar", StringComparison.Ordinal) ? weight : 0f;
            }
            if (points.Count > 0) break;
        }
        return points;
    }

    private static WearableMesh BuildBound(WearableGraphics graphics, Dictionary<string, BoundJoint> bindings,
        IReadOnlyList<WearableVec3>? torso = null, IReadOnlyList<WearableSkinSample>? bodySamples = null)
    {
        var pose = new Dictionary<string, WearableVec3>(StringComparer.Ordinal);
        foreach (var pair in bindings)
        {
            var p = pair.Value.Joint.MultiplyPoint3x4(Vector3.zero);
            pose[pair.Key] = new WearableVec3(p.x, p.y, p.z);
        }
        return graphics.Build(pose, torso, bodySamples);
    }

    private static bool Complete(WearableMesh mesh) => mesh.Vertices.Length > 0 && mesh.Missing.Length == 0;

    private static int Fill(Mesh mesh, SkinnedMeshRenderer smr, Transform model, WearableMesh built,
        Dictionary<string, BoundJoint> bindings, SkinReferenceSet references, out int bodyTransferred)
    {
        var boneNames = new List<string>(built.Bones);
        int transferred = 0;
        bodyTransferred = 0;
        int n = built.Vertices.Length;
        var verts = new Il2CppStructArray<Vector3>(n);
        var weights = new Il2CppStructArray<BoneWeight>(n);
        for (int i = 0; i < n; i++)
        {
            var v = built.Vertices[i];
            verts[i] = new Vector3(v.X, v.Y, v.Z);
            int b = built.BoneIndex[i];
            float secondary = built.BoneWeight2[i];
            weights[i] = new BoneWeight { boneIndex0 = b, weight0 = 1f - secondary,
                boneIndex1 = built.BoneIndex2[i], weight1 = secondary };
            if (built.Authored != null)
            {
                var a = built.Authored; var indices=a.Indices[i]; var values=a.Weights[i];
                weights[i] = new BoneWeight { boneIndex0=indices[0],boneIndex1=indices[1],boneIndex2=indices[2],boneIndex3=indices[3],
                    weight0=values[0],weight1=values[1],weight2=values[2],weight3=values[3] };
                continue;
            }
            bool missingLegArmor = references.Chest && !references.HasLegArmor && v.Y < references.PelvisY;
            if (TryReferenceSkin(v, built.Bones[b], references, missingLegArmor, out var fitted, out bool bodyFit))
            {
                weights[i] = UnityWeights(fitted, boneNames);
                transferred++;
                if (bodyFit) bodyTransferred++;
            }
        }
        mesh.vertices = verts;
        mesh.boneWeights = weights;

        int sub = 0;
        if (built.Cloth.Length > 0) sub++;
        if (built.Gold.Length > 0) sub++;
        if (built.Void.Length > 0) sub++;
        mesh.subMeshCount = Math.Max(1, sub);
        int s = 0;
        if (built.Cloth.Length > 0) mesh.SetTriangles(Ints(built.Cloth), s++, false);
        if (built.Gold.Length > 0) mesh.SetTriangles(Ints(built.Gold), s++, false);
        if (built.Void.Length > 0) mesh.SetTriangles(Ints(built.Void), s++, false);
        if (built.Authored == null) mesh.RecalculateNormals();
        else
        {
            var normals = new Il2CppStructArray<Vector3>(n);
            var uv = new Il2CppStructArray<Vector2>(n);
            for (int i=0;i<n;i++)
            {
                var normal=built.Authored.Normals[i]; normals[i]=new Vector3(normal.X,normal.Y,normal.Z);
                var coord=built.Authored.Uv[i]; uv[i]=new Vector2(coord[0],coord[1]);
            }
            mesh.normals=normals; mesh.uv=uv;
        }
        mesh.RecalculateBounds();

        var boneArr = new Il2CppReferenceArray<Transform>(boneNames.Count);
        var binds = new Il2CppStructArray<Matrix4x4>(boneNames.Count);
        for (int i = 0; i < boneNames.Count; i++)
        {
            var binding = bindings[boneNames[i]];
            boneArr[i] = binding.Bone;
            binds[i] = binding.Bind;
        }
        mesh.bindposes = binds;
        smr.bones = boneArr;
        smr.rootBone = model;
        return transferred;
    }

    private static Il2CppStructArray<int> Ints(int[] src)
    {
        var dst = new Il2CppStructArray<int>(src.Length);
        for (int i = 0; i < src.Length; i++) dst[i] = src[i];
        return dst;
    }

    private static Il2CppReferenceArray<Material> MaterialsFor(WearableMesh mesh, List<Material> owned, List<PaletteMaterial> palettes)
    {
        int n = 0;
        if (mesh.Cloth.Length > 0) n++;
        if (mesh.Gold.Length > 0) n++;
        if (mesh.Void.Length > 0) n++;
        var arr = new Il2CppReferenceArray<Material>(Math.Max(1, n));
        int i = 0;
        if (mesh.Cloth.Length > 0) arr[i++] = Surface(_cloth!, "cloth");
        if (mesh.Gold.Length > 0) arr[i++] = Surface(_gold!, "gold");
        if (mesh.Void.Length > 0) arr[i++] = Surface(_void!, "void");
        if (i == 0) arr[0] = _cloth!;
        return arr;

        Material Surface(Material original, string surface)
        {
            var authored = mesh.Authored;
            if (authored == null) return original;
            bool fixedColor = authored.TrySurfaceColor(surface, out var rgba);
            bool mapped = authored.TrySurfacePalette(surface, out var channel);
            if (!fixedColor && !mapped) return original;
            var material = new Material(original);
            owned.Add(material); // Track before property setters: failure must release this clone.
            material.name = original.name;
            var color = fixedColor ? new Color(rgba.R, rgba.G, rgba.B, rgba.A)
                : original.GetColor(surface == "void" ? "_UnlitColor" : "_BaseColor");
            SetSurfaceColor(material, color);
            if (mapped) palettes.Add(new PaletteMaterial(material, channel, color));
            return material;
        }
    }

    private static Worn? AttachNative(Want want, ulong referenceFingerprint)
    {
        var model = want.Model;
        var native = want.Graphics.NativeRenderer!;
        SkinnedMeshRenderer? source = null;
        foreach (var candidate in model.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            if (candidate == null || Ours(candidate.transform)
                || !string.Equals(candidate.name, native.RendererName, StringComparison.Ordinal)) continue;
            if (source != null)
            {
                LogOnce(want.Guid, "Wearables: ambiguous native renderer " + native.RendererName + "; will retry.");
                return null;
            }
            source = candidate;
        }
        if (source == null || source.sharedMesh == null || source.transform.parent == null)
        {
            LogOnce(want.Guid, "Wearables: native renderer " + native.RendererName + " is not ready; will retry.");
            return null;
        }
        if (!WearableHide.ShouldHide(source.name, new[] { source.name }, want.Graphics.Slot))
        {
            LogOnce(want.Guid, "Wearables: native renderer " + native.RendererName + " is a protected body or face renderer.");
            return null;
        }
        var borrowedMesh = source.sharedMesh;
        var bones = source.bones;
        var sourceMaterials = source.sharedMaterials;
        if (borrowedMesh.vertexCount == 0 || borrowedMesh.subMeshCount == 0
            || bones.Length == 0
            // Native effects such as Blood Moon append overlay materials that draw
            // another pass on the last submesh. Preserve those passes when borrowing;
            // they do not make a complete mesh/material binding incompatible.
            || sourceMaterials.Length < borrowedMesh.subMeshCount)
        {
            LogOnce(want.Guid, "Wearables: native renderer " + native.RendererName + " has incompatible mesh/material bindings; will retry.");
            return null;
        }
        foreach (var bone in bones)
            if (bone == null || !Under(bone, model)) return null;
        foreach (var rule in native.Colors)
        {
            if (rule.MaterialIndex >= sourceMaterials.Length || sourceMaterials[rule.MaterialIndex] == null
                || !IsColorProperty(sourceMaterials[rule.MaterialIndex], rule.Property))
            {
                LogOnce(want.Guid, "Wearables: native renderer " + native.RendererName
                    + " lacks requested material color property " + rule.Property + "; will retry.");
                return null;
            }
        }
        foreach (var rule in native.FixedColors)
        {
            if (rule.MaterialIndex >= sourceMaterials.Length || sourceMaterials[rule.MaterialIndex] == null
                || !IsColorProperty(sourceMaterials[rule.MaterialIndex], rule.Property))
            {
                LogOnce(want.Guid, "Wearables: native renderer " + native.RendererName
                    + " lacks requested fixed color property " + rule.Property + "; will retry.");
                return null;
            }
        }
        foreach (var rule in native.WhiteTextures)
        {
            if (rule.MaterialIndex >= sourceMaterials.Length || sourceMaterials[rule.MaterialIndex] == null
                || !IsShaderProperty(sourceMaterials[rule.MaterialIndex], rule.Property, ShaderPropertyType.Texture))
            {
                LogOnce(want.Guid, "Wearables: native renderer " + native.RendererName
                    + " lacks requested texture property " + rule.Property + "; will retry.");
                return null;
            }
        }
        Dictionary<string, BoundJoint>? overlayBindings = null;
        WearableMesh? overlayBuilt = null;
        if (want.Graphics.AuthoredOverlay != null)
        {
            var overlayGraphics = new WearableGraphics(want.Graphics.Slot, null, null,
                Array.Empty<string>(), new List<WearablePart>())
                { Authored = want.Graphics.AuthoredOverlay, AuthoredVariants = want.Graphics.AuthoredOverlayVariants };
            if (!TryBind(model, overlayGraphics, out var bound, out var built) || built.Void.Length == 0)
            {
                LogOnce(want.Guid, "Wearables: native renderer " + native.RendererName
                    + " has no complete compatible bind pose for its authored overlay; will retry.");
                return null;
            }
            if (!EnsureMaterials())
            {
                LogOnce(want.Guid, "Wearables: authored overlay materials are unavailable; will retry.");
                return null;
            }
            overlayBindings = bound;
            overlayBuilt = built;
        }

        var root = new GameObject(RootPrefix + want.Guid);
        var ownedMaterials = new List<Material>();
        var paletteMaterials = new List<PaletteMaterial>();
        GameObject? overlayRoot = null;
        Mesh? overlayMesh = null;
        int[] hidden = Array.Empty<int>();
        try
        {
            root.SetActive(false);
            root.transform.SetParent(source.transform.parent, false);
            root.transform.localPosition = source.transform.localPosition;
            root.transform.localRotation = source.transform.localRotation;
            root.transform.localScale = source.transform.localScale;
            var copy = root.AddComponent<SkinnedMeshRenderer>();
            copy.sharedMesh = borrowedMesh;
            copy.bones = bones;
            copy.rootBone = source.rootBone;
            copy.localBounds = source.localBounds;
            copy.updateWhenOffscreen = source.updateWhenOffscreen;
            copy.shadowCastingMode = source.shadowCastingMode;
            var materials = new Il2CppReferenceArray<Material>(sourceMaterials.Length);
            for (int i = 0; i < sourceMaterials.Length; i++)
            {
                if (sourceMaterials[i] == null) throw new InvalidOperationException("Native wearable source has a null material.");
                var material = new Material(sourceMaterials[i]);
                ownedMaterials.Add(material);
                material.name = "progenitor-wear-native-" + i;
                materials[i] = material;
            }
            foreach (var rule in native.Colors)
            {
                var material = ownedMaterials[rule.MaterialIndex];
                var fallback = rule.Fallback.HasValue
                    ? new Color(rule.Fallback.Value.R, rule.Fallback.Value.G,
                        rule.Fallback.Value.B, rule.Fallback.Value.A)
                    : material.GetColor(rule.Property);
                material.SetColor(rule.Property, fallback);
                paletteMaterials.Add(new PaletteMaterial(material, rule.Channel,
                    fallback, rule.Property));
            }
            foreach (var rule in native.FixedColors)
            {
                var color = rule.Color;
                ownedMaterials[rule.MaterialIndex].SetColor(rule.Property,
                    new Color(color.R, color.G, color.B, color.A));
            }
            foreach (var rule in native.WhiteTextures)
                ownedMaterials[rule.MaterialIndex].SetTexture(rule.Property, Texture2D.whiteTexture);
            copy.sharedMaterials = materials;
            UpdatePalette(paletteMaterials, want.Palette);
            if (overlayBindings != null && overlayBuilt != null)
            {
                overlayRoot = new GameObject(RootPrefix + want.Guid + "-overlay");
                overlayRoot.SetActive(false);
                overlayRoot.transform.SetParent(model, false);
                overlayRoot.transform.localPosition = Vector3.zero;
                overlayRoot.transform.localRotation = Quaternion.identity;
                overlayRoot.transform.localScale = Vector3.one;
                var overlayRenderer = overlayRoot.AddComponent<SkinnedMeshRenderer>();
                overlayMesh = new Mesh { name = overlayRoot.name };
                Fill(overlayMesh, overlayRenderer, model, overlayBuilt, overlayBindings,
                    new SkinReferenceSet(), out _);
                overlayRenderer.sharedMesh = overlayMesh;
                overlayRenderer.sharedMaterials = MaterialsFor(overlayBuilt, ownedMaterials, paletteMaterials);
                overlayRenderer.updateWhenOffscreen = true;
                overlayRenderer.shadowCastingMode = ShadowCastingMode.On;
            }
            hidden = HideDonors(model, want.Graphics.HideNames, want.Graphics.Slot);
            hidden = HideNativeSource(source, hidden);
            ClearLogged(want.Guid);
            LogOnce(want.Guid, "Wearables: borrowed native renderer " + native.RendererName
                + " verts=" + borrowedMesh.vertexCount + " submeshes=" + borrowedMesh.subMeshCount);
            return new Worn(root, borrowedMesh, false, hidden, referenceFingerprint,
                want.Graphics, ownedMaterials.ToArray(), paletteMaterials, source, overlayRoot, overlayMesh);
        }
        catch
        {
            ReleaseHidden(hidden);
            foreach (var material in ownedMaterials) DestroyMaterial(material);
            if (overlayMesh != null) UnityEngine.Object.Destroy(overlayMesh);
            if (overlayRoot != null) UnityEngine.Object.Destroy(overlayRoot);
            UnityEngine.Object.Destroy(root);
            throw;
        }
    }

    private static bool SyncEffect(Worn worn, Transform model)
    {
        var source = worn.NativeSource;
        if (source == null && worn.Graphics.NativeRenderer == null)
            foreach (var candidate in model.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                if (candidate != null && candidate.name == "Body_GEO" && !Ours(candidate.transform))
                {
                    source = candidate;
                    break;
                }
        if (source == null || worn.Root == null) return false;
        var materials = source.sharedMaterials;
        // Observed Veil of Shadow replaces every native material with Sh_Overlay.
        // Borrow its live presentation while retaining our owned resting materials;
        // do not recolor, clone or destroy these game-owned effect materials.
        bool effect = materials.Length > 0;
        foreach (var material in materials)
            effect &= material != null && material.shader != null
                && material.shader.name == "Shader Graphs/Sh_Overlay";
        var renderer = worn.Root.GetComponent<SkinnedMeshRenderer>();
        if (renderer == null) return false;
        if (effect)
        {
            if (worn.Graphics.NativeRenderer != null) renderer.sharedMaterials = materials;
            else
            {
                // Authored cloth and trim must enter the same concealment phase as
                // the body and native hood. Repeat the body's effect passes for
                // each authored submesh, preserving any additional overlay passes.
                var effects = new Il2CppReferenceArray<Material>(worn.Mesh.subMeshCount + materials.Length - 1);
                for (int i = 0; i < worn.Mesh.subMeshCount; i++) effects[i] = materials[0];
                for (int i = 1; i < materials.Length; i++) effects[worn.Mesh.subMeshCount + i - 1] = materials[i];
                renderer.sharedMaterials = effects;
            }
            if (worn.OverlayRoot != null) worn.OverlayRoot.SetActive(false);
            worn.NativeEffectActive = true;
            return true;
        }
        if (worn.NativeEffectActive)
        {
            renderer.sharedMaterials = worn.RestMaterials;
            if (worn.OverlayRoot != null) worn.OverlayRoot.SetActive(true);
            worn.NativeEffectActive = false;
        }
        return false;
    }

    // Owned renderers are not under the game's control. When the game hides the
    // character (a dead player awaiting respawn disables Body_GEO and its other native
    // renderers), conceal the overlays too instead of leaving clothing with no body.
    private static void ApplyModelVisibility(Worn worn, Want want, Dictionary<int, bool> cache)
    {
        if (worn.Root == null || want.Model == null) return;
        int modelId = want.Model.GetInstanceID();
        if (!cache.TryGetValue(modelId, out bool visible))
        {
            cache[modelId] = visible = ModelVisible(want.Model, modelId);
            bool changed = ModelShown.TryGetValue(modelId, out bool previous) ? previous != visible : !visible;
            ModelShown[modelId] = visible;
            if (changed)
                Plugin.Logger?.LogDebug("Wearables: " + (visible ? "showing" : "concealing")
                    + " overlays on model " + modelId + " because the game "
                    + (visible ? "shows" : "hides") + " the wearer's body.");
        }
        if (worn.ModelVisible == visible) return;
        var renderer = worn.Root.GetComponent<SkinnedMeshRenderer>();
        if (renderer != null) renderer.enabled = visible;
        if (worn.OverlayRoot != null)
        {
            var overlay = worn.OverlayRoot.GetComponent<SkinnedMeshRenderer>();
            if (overlay != null) overlay.enabled = visible;
        }
        worn.ModelVisible = visible;
    }

    private static bool ModelVisible(Transform model, int modelId)
    {
        if (!BodyRenderers.TryGetValue(modelId, out var bodies))
        {
            var found = new List<SkinnedMeshRenderer>();
            foreach (var renderer in model.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                if (renderer != null && WearableModelVisibility.IsNativeBody(renderer.name, false)
                    && !Ours(renderer.transform)) found.Add(renderer);
            BodyRenderers[modelId] = bodies = found.ToArray();
        }
        int count = 0, shown = 0;
        foreach (var body in bodies)
        {
            if (body == null) continue; // Destroyed since the last refresh; rediscovered on the next one.
            count++;
            if (body.enabled && body.gameObject.activeInHierarchy) shown++;
        }
        return WearableModelVisibility.Visible(count, shown);
    }

    // Body renderers change only with model rebuilds, which the reference pass already
    // samples every eight frames; rediscover them there instead of walking every frame.
    private static void RefreshBodyCache(Dictionary<long, Want> wanted)
    {
        BodyRenderers.Clear();
        if (ModelShown.Count == 0) return;
        var live = new HashSet<int>();
        foreach (var want in wanted.Values)
            if (want.Model != null) live.Add(want.Model.GetInstanceID());
        var gone = new List<int>();
        foreach (var id in ModelShown.Keys) if (!live.Contains(id)) gone.Add(id);
        foreach (var id in gone) ModelShown.Remove(id);
    }

    private static int[] HideNativeSource(SkinnedMeshRenderer source, int[] existing)
    {
        int id = source.GetInstanceID();
        foreach (int held in existing) if (held == id) return existing;
        bool wasEnabled = source.enabled;
        source.enabled = false;
        if (!Hidden.TryGetValue(id, out var hold))
            Hidden[id] = hold = new HideHold(source, wasEnabled);
        hold.Count++;
        var result = new int[existing.Length + 1];
        Array.Copy(existing, result, existing.Length);
        result[existing.Length] = id;
        return result;
    }

    private static void EnsurePending(long key, Want want)
    {
        if (!Pending.TryGetValue(key, out var pending))
            Pending[key] = pending = new PendingHide(want.Graphics, Time.frameCount);
        if (pending.TimedOut) return;
        if (Time.frameCount - pending.FirstFrame > PendingHideFrameLimit)
        {
            ReleaseHidden(pending.Hidden);
            pending.Hidden = Array.Empty<int>();
            pending.TimedOut = true;
            Plugin.Logger?.LogWarning("Wearables: pending hide timed out for " + want.Guid
                + "; restoring donor while compatible graphics remain unavailable.");
            return;
        }
        pending.Hidden = HideDonors(want.Model, want.Graphics.HideNames, want.Graphics.Slot, pending.Hidden);
        var native = want.Graphics.NativeRenderer;
        if (native == null) return;
        SkinnedMeshRenderer? exact = null;
        foreach (var renderer in want.Model.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            if (renderer == null || Ours(renderer.transform)
                || !string.Equals(renderer.name, native.RendererName, StringComparison.Ordinal)) continue;
            if (exact != null) return; // Ambiguous native source must not be concealed speculatively.
            exact = renderer;
        }
        if (exact != null && WearableHide.ShouldHide(exact.name, new[] { exact.name }, want.Graphics.Slot))
            pending.Hidden = HideNativeSource(exact, pending.Hidden);
    }

    private static void ReleasePending(long key)
    {
        if (!Pending.TryGetValue(key, out var pending)) return;
        Pending.Remove(key);
        ReleaseHidden(pending.Hidden);
    }

    private static bool IsColorProperty(Material material, string property)
        => IsShaderProperty(material, property, ShaderPropertyType.Color);

    private static bool IsShaderProperty(Material material, string property, ShaderPropertyType type)
    {
        var shader = material.shader;
        if (shader == null || !material.HasProperty(property)) return false;
        for (int i = 0; i < shader.GetPropertyCount(); i++)
            if (string.Equals(shader.GetPropertyName(i), property, StringComparison.Ordinal))
                return shader.GetPropertyType(i) == type;
        return false;
    }

    private static Dictionary<string, Color>? ReadPalette(World world, EquipmentSlot slot, WearableGraphics graphics)
    {
        if ((!graphics.HasMappedSurfacePalette && graphics.NativeRenderer?.Colors.Length is not > 0)
            || slot.TransmogIndex == byte.MaxValue
            || slot.TransmogKey.AssetGuid.GuidHash == 0) return null;
        try
        {
            var gameData = world.GetExistingSystemManaged<GameDataSystem>();
            var registry = gameData?.ManagedDataRegistry;
            // The native utility uses this existing reference-type registry instantiation.
            // Read it directly, avoiding that utility's by-ref managed-registry wrapper.
            if (registry == null || !registry.TryGet<ProjectM.Transmog.TransmogPaletteData>(
                    slot.TransmogKey.AssetGuid, out var data) || data == null) return null;
            var choices = data.TransmogColorData;
            int index = (int)slot.TransmogIndex - slot.TransmogKey.StartIndex;
            if (choices == null || index < 0 || index >= choices.Length) return null;
            var colors = choices[index];
            if (colors == null) return null;
            return new Dictionary<string, Color>(StringComparer.Ordinal)
            {
                ["primary"] = colors.PrimaryColor, ["accent"] = colors.AccentColor,
                ["neutral"] = colors.NeutralColor, ["metal"] = colors.MetalColor,
            };
        }
        catch { return null; } // Unavailable data preserves this garment's authored fallback.
    }

    private static void SetSurfaceColor(Material material, Color color)
    {
        material.SetColor("_BaseColor", color);
        material.SetColor("_Color", color);
        material.SetColor("_UnlitColor", color);
    }

    private static void UpdatePalette(List<PaletteMaterial> materials, Dictionary<string, Color>? palette)
    {
        foreach (var binding in materials)
        {
            var color = palette != null && palette.TryGetValue(binding.Channel, out var selected)
                ? selected : binding.Fallback;
            if (color == binding.Current) continue;
            if (binding.Property == null) SetSurfaceColor(binding.Material, color);
            else binding.Material.SetColor(binding.Property, color);
            binding.Current = color;
        }
    }

    private sealed class PaletteMaterial
    {
        internal readonly Material Material;
        internal readonly string Channel;
        internal readonly Color Fallback;
        internal readonly string? Property;
        internal Color Current;
        internal PaletteMaterial(Material material, string channel, Color fallback, string? property = null)
        { Material = material; Channel = channel; Fallback = Current = fallback; Property = property; }
    }

    private static int[] HideDonors(Transform model, IReadOnlyList<string> prefixes, WearableSlot slot, int[]? existing = null)
    {
        var ids = existing == null ? new List<int>() : new List<int>(existing);
        var owned = new HashSet<int>(ids);
        foreach (var r in model.GetComponentsInChildren<Renderer>(true))
        {
            if (r == null || Ours(r.transform)) continue;
            if (!WearableHide.ShouldHide(r.name, prefixes, slot)) continue;
            int id = r.GetInstanceID();
            if (!Hidden.TryGetValue(id, out var hold))
            {
                hold = new HideHold(r, r.enabled);
                Hidden[id] = hold;
            }
            r.enabled = false;
            if (owned.Add(id))
            {
                hold.Count++;
                ids.Add(id);
            }
        }
        return ids.ToArray();
    }

    private static bool Ours(Transform t)
    {
        for (var p = t; p != null; p = p.parent)
            if (p.name.StartsWith(RootPrefix, StringComparison.Ordinal)) return true;
        return false;
    }

    private static bool EnsureMaterials()
    {
        if (_cloth != null && _gold != null && _void != null) return true;
        if (_shaderMissing) return false;
        var lit = Shader.Find("HDRP/Lit");
        var unlit = Shader.Find("HDRP/Unlit");
        if (lit == null || unlit == null)
        {
            _shaderMissing = true;
            return false;
        }
        _cloth = new Material(lit) { name = "progenitor-wear-cloth" };
        // Ankh Blue #0054B6, using the existing material color-input convention.
        var clothColor = new Color(0f, 84f / 255f, 182f / 255f, 1f);
        _cloth.SetColor("_BaseColor", clothColor);
        _cloth.SetColor("_Color", clothColor);
        _cloth.SetColor("_UnlitColor", clothColor);
        _cloth.SetFloat("_Metallic", 0f);
        _cloth.SetFloat("_Smoothness", 0.12f);
        DoubleSided(_cloth);
        _gold = new Material(lit) { name = "progenitor-wear-gold" };
        _gold.SetColor("_BaseColor", new Color(0.86f, 0.62f, 0.18f, 1f));
        _gold.SetFloat("_Metallic", 0.92f);
        _gold.SetFloat("_Smoothness", 0.72f);
        _gold.SetColor("_EmissiveColor", new Color(0.25f, 0.14f, 0.02f, 1f));
        DoubleSided(_gold);
        _void = new Material(unlit) { name = "progenitor-wear-void" };
        _void.SetColor("_UnlitColor", Color.black);
        DoubleSided(_void);
        return true;
    }

    private static void DoubleSided(Material m)
    {
        m.SetFloat("_DoubleSidedEnable", 1f);
        m.SetFloat("_CullMode", 0f);
        m.SetFloat("_CullModeForward", 0f);
        m.EnableKeyword("_DOUBLESIDED_ON");
    }

    private static unsafe Transform? ModelOf(EntityManager em, Entity unit)
    {
        var type = ComponentIndex.Of<HybridModelUser>(ComponentType.AccessMode.ReadOnly);
        if (!em.HasComponent(unit, type)) return null;
        byte* user = (byte*)em.GetComponentDataRawRO(unit, type.TypeIndex);
        if (user == null) return null;
        var hybrid = *(Entity*)(user + Layout.HybridModelUserHybridEntityOffset);
        if (hybrid == Entity.Null || !em.Exists(hybrid)) return null;
        try
        {
            var transform = em.GetComponentObject<Transform>(hybrid);
            return transform == null ? null : transform;
        }
        catch
        {
            return null;
        }
    }

    private static EquipmentSlot SlotOf(Equipment equipment, WearableSlot slot) => slot switch
    {
        WearableSlot.Headgear => equipment.ArmorHeadgearSlot,
        WearableSlot.Chest => equipment.ArmorChestSlot,
        WearableSlot.Gloves => equipment.ArmorGlovesSlot,
        WearableSlot.Legs => equipment.ArmorLegsSlot,
        WearableSlot.Footgear => equipment.ArmorFootgearSlot,
        WearableSlot.Cloak => equipment.CloakSlot,
        WearableSlot.Weapon => equipment.WeaponSlot,
        _ => equipment.GrimoireSlot,
    };

    private static EquipmentSlot EffectiveSlotOf(Equipment equipment, WearableSlot slot) =>
        TryCosmeticSlotOf(equipment, slot, out var cosmetic) && cosmetic.SlotId.GuidHash != 0
            ? cosmetic : SlotOf(equipment, slot);

    private static bool TryCosmeticSlotOf(Equipment equipment, WearableSlot slot, out EquipmentSlot cosmetic)
    {
        switch (slot)
        {
            case WearableSlot.Chest: cosmetic = equipment.ChestCosmeticSlot; return true;
            case WearableSlot.Gloves: cosmetic = equipment.GlovesCosmeticSlot; return true;
            case WearableSlot.Legs: cosmetic = equipment.LegsCosmeticSlot; return true;
            case WearableSlot.Footgear: cosmetic = equipment.FootgearCosmeticSlot; return true;
            case WearableSlot.Cloak: cosmetic = equipment.CloakCosmeticSlot; return true;
            default: cosmetic = default; return false;
        }
    }

    /// <summary>
    /// Prefers a registered slot ID, then a registered item ID. Either side can
    /// retain the donor identity on a networked clone; a custom slot ID must not
    /// be replaced by its client item entity's vanilla donor.
    /// </summary>
    private static PrefabGUID ResolveWornGuid(EntityManager em, EquipmentSlot slot,
        Dictionary<int, WearableGraphics> graphics)
    {
        try
        {
            var item = slot.SlotEntity._Entity;
            if (item != Entity.Null && em.Exists(item)
                && em.HasComponent(item, ComponentType.ReadOnly<PrefabGUID>()))
            {
                var onItem = em.GetComponentData<PrefabGUID>(item);
                return new PrefabGUID(WearableSlotSelection.ResolveGuid(slot.SlotId.GuidHash,
                    onItem.GuidHash, graphics.ContainsKey(slot.SlotId.GuidHash),
                    graphics.ContainsKey(onItem.GuidHash)));
            }
        }
        catch
        {
            // Fall through to SlotId.
        }
        return slot.SlotId;
    }

    private static long Key(int modelId, int guid) => ((long)modelId << 32) ^ (uint)guid;

    private static void Release(long key)
    {
        if (!Active.TryGetValue(key, out var worn)) return;
        Active.Remove(key);
        ReleaseWorn(worn);
    }

    private static void ReleaseWorn(Worn worn)
    {
        ReleaseHidden(worn.Hidden);
        try { if (worn.Root != null) { worn.Root.SetActive(false); UnityEngine.Object.Destroy(worn.Root); } }
        catch { /* already gone */ }
        try { if (worn.OwnsMesh && worn.Mesh != null) UnityEngine.Object.Destroy(worn.Mesh); }
        catch { /* already gone */ }
        try { if (worn.OverlayMesh != null) UnityEngine.Object.Destroy(worn.OverlayMesh); }
        catch { /* already gone */ }
        try { if (worn.OverlayRoot != null) { worn.OverlayRoot.SetActive(false); UnityEngine.Object.Destroy(worn.OverlayRoot); } }
        catch { /* already gone */ }
        foreach (var material in worn.Materials) DestroyMaterial(material);
    }

    private static void ReleaseHidden(int[]? ids)
    {
        if (ids != null)
        {
            for (int i = 0; i < ids.Length; i++)
            {
                if (!Hidden.TryGetValue(ids[i], out var hold)) continue;
                hold.Count--;
                if (hold.Count > 0) continue;
                try { if (hold.Renderer != null) hold.Renderer.enabled = hold.WasEnabled; }
                catch { /* renderer already destroyed */ }
                Hidden.Remove(ids[i]);
            }
        }
    }

    private static void ReleaseAll()
    {
        BodyRenderers.Clear();
        ModelShown.Clear();
        var keys = new List<long>(Active.Keys);
        for (int i = 0; i < keys.Count; i++) Release(keys[i]);
        keys = new List<long>(Pending.Keys);
        for (int i = 0; i < keys.Count; i++) ReleasePending(keys[i]);
    }

    private static void DestroyMaterial(Material? m)
    {
        try { if (m != null) UnityEngine.Object.Destroy(m); }
        catch { /* already gone */ }
    }

    private static void LogOnce(int guid, string line)
    {
        if (!Logged.Add(guid)) return;
        Plugin.Logger?.LogInfo(line);
    }

    /// <summary>Allow a later success line after a one-shot retry message for the same guid.</summary>
    private static void ClearLogged(int guid) => Logged.Remove(guid);

    private readonly struct Want
    {
        internal Want(Transform model, WearableGraphics graphics, int guid, Dictionary<string, Color>? palette)
        {
            Model = model;
            Graphics = graphics;
            Guid = guid;
            Palette = palette;
        }

        internal Transform Model { get; }
        internal WearableGraphics Graphics { get; }
        internal int Guid { get; }
        internal Dictionary<string, Color>? Palette { get; }
    }

    private sealed class Worn
    {
        internal Worn(GameObject root, Mesh mesh, bool ownsMesh, int[] hidden, ulong references, WearableGraphics graphics, Material[] materials, List<PaletteMaterial> paletteMaterials, SkinnedMeshRenderer? nativeSource = null, GameObject? overlayRoot = null, Mesh? overlayMesh = null)
        {
            Root = root;
            Mesh = mesh;
            OwnsMesh = ownsMesh;
            Hidden = hidden;
            References = PendingReferences = references;
            Graphics = graphics;
            Materials = materials;
            PaletteMaterials = paletteMaterials;
            NativeSource = nativeSource;
            OverlayRoot = overlayRoot;
            OverlayMesh = overlayMesh;
            // Actual slot assignments include shared core materials as well as owned
            // palette clones. Keep this separate from the destruction ownership list.
            RestMaterials = root.GetComponent<SkinnedMeshRenderer>().sharedMaterials;
        }

        internal GameObject Root { get; }
        internal Mesh Mesh { get; }
        internal bool OwnsMesh { get; }
        internal int[] Hidden { get; set; }
        internal WearableGraphics Graphics { get; }
        internal Material[] Materials { get; }
        internal List<PaletteMaterial> PaletteMaterials { get; }
        internal SkinnedMeshRenderer? NativeSource { get; }
        internal GameObject? OverlayRoot { get; }
        internal Mesh? OverlayMesh { get; }
        internal Il2CppReferenceArray<Material> RestMaterials { get; }
        internal bool NativeEffectActive { get; set; }
        // Owned renderers start enabled; tracks the last applied game visibility.
        internal bool ModelVisible { get; set; } = true;
        internal ulong References { get; }
        internal ulong PendingReferences { get; set; }
        internal int ReferencePasses { get; set; }
    }

    private sealed class PendingHide
    {
        internal PendingHide(WearableGraphics graphics, int firstFrame)
        { Graphics = graphics; FirstFrame = firstFrame; }
        internal WearableGraphics Graphics { get; }
        internal int FirstFrame { get; }
        internal int[] Hidden = Array.Empty<int>();
        internal bool TimedOut;
        internal readonly WearableAttachRetry Retry = new();
    }

    private sealed class HideHold
    {
        internal HideHold(Renderer renderer, bool wasEnabled)
        {
            Renderer = renderer;
            WasEnabled = wasEnabled;
        }

        internal Renderer Renderer { get; }
        internal bool WasEnabled { get; }
        internal int Count { get; set; }
    }
}
#endif
