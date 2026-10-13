#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Progenitor.Content;

internal static partial class WearableAppearance
{
    // Only reference positions and skin influences cross this boundary. The garment's
    // topology, contours, materials and UVs are not taken from the reference armor.
    private sealed class SkinReferenceSet
    {
        internal readonly List<WearableSkinSample> Native = new();
        internal readonly List<WearableSkinSample> Body = new();
        internal bool Chest;
        internal bool Gloves;
        internal readonly Dictionary<string, (List<WearableSkinSample> Body, List<WearableSkinSample> Native)> HandGroups = new(StringComparer.Ordinal);
        internal bool HasLegArmor;
        internal float PelvisY = float.NegativeInfinity;
        internal float ChestY = float.PositiveInfinity;
        internal string Diagnostics = "skinSources=none";
    }

    private static SkinReferenceSet SkinReferences(Transform model,
        Dictionary<string, BoundJoint> bindings, WearableSlot slot)
    {
        var result = new SkinReferenceSet { Chest = slot == WearableSlot.Chest, Gloves = slot == WearableSlot.Gloves };
        if (slot == WearableSlot.Headgear || slot == WearableSlot.Weapon) return result;
        if (bindings.TryGetValue("Pelvis_JNT", out var pelvis)) result.PelvisY = pelvis.Joint.MultiplyPoint3x4(Vector3.zero).y;
        if (bindings.TryGetValue("Chest_JNT", out var chest)) result.ChestY = chest.Joint.MultiplyPoint3x4(Vector3.zero).y;
        var sources = new List<string>();
        var renderers = model.GetComponentsInChildren<SkinnedMeshRenderer>(true);
        foreach (var renderer in renderers)
        {
            if (renderer == null || Ours(renderer.transform)) continue;
            string name = renderer.name;
            bool match = slot switch
            {
                WearableSlot.Chest => name.StartsWith("CHest_GEO", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("Legs_GEO", StringComparison.OrdinalIgnoreCase),
                WearableSlot.Gloves => name.StartsWith("Gloves_GEO", StringComparison.OrdinalIgnoreCase),
                WearableSlot.Footgear => name.StartsWith("Boots_GEO", StringComparison.OrdinalIgnoreCase),
                _ => false
            };
            if (match)
            {
                int before = result.Native.Count;
                ReadSkinReference(renderer, model, bindings, result.Native);
                int added = result.Native.Count - before;
                if (added > 0 && name.StartsWith("Legs_GEO", StringComparison.OrdinalIgnoreCase)) result.HasLegArmor = true;
                sources.Add(name + "#" + renderer.GetInstanceID() + ":" + added);
            }
        }
        // Keep the fallback separate: partial armor availability must not prevent
        // uncovered body regions from receiving compatible skinning references.
        foreach (var renderer in renderers)
            if (renderer != null && !Ours(renderer.transform) && renderer.name == "Body_GEO")
            {
                int before = result.Body.Count;
                ReadSkinReference(renderer, model, bindings, result.Body);
                sources.Add("body#" + renderer.GetInstanceID() + ":" + (result.Body.Count - before));
            }
        result.Diagnostics = "skinSources=" + string.Join(",", sources)
            + " nativePoints=" + result.Native.Count + " bodyPoints=" + result.Body.Count
            + " legArmor=" + result.HasLegArmor;
        return result;
    }

    private static bool TryReferenceSkin(WearableVec3 position, string authoredBone,
        SkinReferenceSet references, bool missingLegArmor, out WearableSkinInfluence[] fitted, out bool bodyFit)
    {
        fitted = Array.Empty<WearableSkinInfluence>();
        bodyFit = false;
        if (references.Gloves)
        {
            if (!references.HandGroups.TryGetValue(authoredBone, out var group))
            {
                group = (WearableHandSkin.Filter(references.Body, authoredBone),
                    WearableHandSkin.Filter(references.Native, authoredBone));
                references.HandGroups[authoredBone] = group;
            }
            float distance = WearableHandSkin.IsDigit(authoredBone) ? .06f : .12f;
            // Geometry is fitted to the compatible body hand. Preserve that deformation
            // first; native glove samples may supplement only the same anatomical group.
            bodyFit = WearableSkinTransfer.TryTransfer(position, group.Body, distance, out fitted);
            if (bodyFit) return true;
            return WearableSkinTransfer.TryTransfer(position, group.Native, distance, out fitted);
        }
        // The close-fitting shoulder and underarm follow the same body surface on
        // both sides of the sleeve/bodice overlap. Armor can carry a raised pauldron
        // on the arm alone, which pulls this overlap apart in overhead attacks.
        if (references.Chest && position.Y >= references.ChestY)
        {
            bodyFit = WearableSkinTransfer.TryTransfer(position, references.Body, .12f, out fitted);
            if (bodyFit) return true;
        }
        bool nativeFit = !missingLegArmor && WearableSkinTransfer.TryTransfer(position, references.Native, .45f, out fitted);
        if (nativeFit) return true;
        bodyFit = WearableSkinTransfer.TryTransfer(position, references.Body, .45f, out fitted);
        return bodyFit;
    }

    private static void ReadSkinReference(SkinnedMeshRenderer renderer, Transform model,
        Dictionary<string, BoundJoint> bindings, List<WearableSkinSample> samples)
    {
        int start = samples.Count;
        try { ReadSkinReferenceCore(renderer, model, bindings, samples); }
        catch (Exception e)
        {
            if (samples.Count > start) samples.RemoveRange(start, samples.Count - start);
            Plugin.Logger?.LogWarning("Wearables: optional skin reference unavailable: " + e.Message);
        }
    }

    private static void ReadSkinReferenceCore(SkinnedMeshRenderer renderer, Transform model,
        Dictionary<string, BoundJoint> bindings, List<WearableSkinSample> samples)
    {
        var source = renderer.sharedMesh;
        if (source == null) return;
        var bones = renderer.bones;
        var binds = source.bindposes;
        if (bones.Length != binds.Length) return;
        var toModel = model.worldToLocalMatrix * renderer.transform.localToWorldMatrix;
        if (!ValidMatrix(toModel)) return;
        var skin = new Matrix4x4[bones.Length];
        for (int i = 0; i < bones.Length; i++)
        {
            var bone = bones[i];
            if (bone == null || !bindings.TryGetValue(bone.name, out var expected)
                || !ValidMatrix(binds[i]) || !SameJoint(expected, new BoundJoint(bone, toModel * binds[i].inverse))) return;
            skin[i] = renderer.transform.worldToLocalMatrix * bone.localToWorldMatrix * binds[i];
        }
        var weights = source.boneWeights;
        Mesh? baked = null;
        try
        {
            // Readable reference meshes provide authored positions directly. For an
            // unreadable mesh, undo the same weighted skin transform on a CPU snapshot;
            // assigning posed BakeMesh vertices as rest vertices would double-deform it.
            bool posed = !source.isReadable;
            if (posed && source.blendShapeCount != 0) return;
            // BakeMesh(false) excludes renderer scale. Until a scaled reference's
            // bake convention is qualified, use procedural/body fallback weights.
            var scale = renderer.transform.lossyScale;
            if (posed && (MathF.Abs(scale.x - 1f) > .0001f
                || MathF.Abs(scale.y - 1f) > .0001f || MathF.Abs(scale.z - 1f) > .0001f)) return;
            if (posed) { baked = new Mesh(); renderer.BakeMesh(baked); }
            var vertices = (baked ?? source).vertices;
            if (vertices.Length != weights.Length) return;
            for (int v = 0; v < vertices.Length; v++)
            {
                var w = weights[v];
                int[] indices = { w.boneIndex0, w.boneIndex1, w.boneIndex2, w.boneIndex3 };
                float[] values = { w.weight0, w.weight1, w.weight2, w.weight3 };
                var influences = new List<WearableSkinInfluence>(4);
                var blend = new Matrix4x4();
                bool valid = true;
                float total = 0f;
                for (int j = 0; j < 4; j++)
                {
                    float weight = values[j];
                    if (weight == 0f) continue;
                    int index = indices[j];
                    if (!float.IsFinite(weight) || weight < 0f || index < 0 || index >= bones.Length) { valid = false; break; }
                    total += weight;
                    influences.Add(new WearableSkinInfluence(bones[index].name, weight));
                    if (posed)
                        for (int row = 0; row < 4; row++)
                            for (int col = 0; col < 4; col++) blend[row, col] += skin[index][row, col] * weight;
                }
                if (!valid || total < .99f || total > 1.01f) continue;
                var p = vertices[v];
                if (posed)
                {
                    if (!ValidMatrix(blend)) continue;
                    p = blend.inverse.MultiplyPoint3x4(p);
                }
                p = toModel.MultiplyPoint3x4(p);
                if (!float.IsFinite(p.x) || !float.IsFinite(p.y) || !float.IsFinite(p.z)) continue;
                samples.Add(new WearableSkinSample(new WearableVec3(p.x, p.y, p.z), influences.ToArray()));
            }
        }
        finally { if (baked != null) UnityEngine.Object.Destroy(baked); }
    }

    private static BoneWeight UnityWeights(WearableSkinInfluence[] influences, List<string> bones)
    {
        var result = new BoneWeight();
        for (int i = 0; i < influences.Length; i++)
        {
            var influence = influences[i];
            int index = bones.IndexOf(influence.Bone);
            if (index < 0) { index = bones.Count; bones.Add(influence.Bone); }
            switch (i)
            {
                case 0: result.boneIndex0 = index; result.weight0 = influence.Weight; break;
                case 1: result.boneIndex1 = index; result.weight1 = influence.Weight; break;
                case 2: result.boneIndex2 = index; result.weight2 = influence.Weight; break;
                case 3: result.boneIndex3 = index; result.weight3 = influence.Weight; break;
            }
        }
        return result;
    }
}
#endif
