#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using UnityObject = UnityEngine.Object;

namespace Progenitor.Models;

/// <summary>
/// A posable copy of a game model: its bones by name, with the pose it was created in kept as the
/// rest pose. Make one with <see cref="ModelHandle.InstantiatePuppet"/> (or <see cref="Of"/> on any
/// inert copy). Bones are the rig's plain Transforms (<c>Root_JNT</c>, <c>Spine_JNT</c>,
/// <c>Left_Elbow_JNT</c>…); with the copy's <c>Animator</c> and IK off nothing else moves them, so a
/// rotation set here holds until you change it.
///
/// <para>Rotations can be set three ways: <see cref="SetLocalRotation"/> (the bone's own parent
/// space), <see cref="SetRotationFromRest"/> (a delta applied after the rest pose, in the bone's
/// local axes), and <see cref="SetWorldRotation"/>. <see cref="Dress"/> moves an equipment copy's
/// skinned meshes onto this rig, as the game does when a character equips armor.</para>
///
/// <para>The puppet does not own the GameObject: the <see cref="ModelHandle"/> that made it does, and
/// destroys it on dispose or unload. Every member throws <see cref="InvalidOperationException"/>
/// once the GameObject is gone.</para>
/// </summary>
public sealed class Puppet
{
    private readonly GameObject _root;
    private readonly Dictionary<string, Transform> _bones = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (Vector3 Position, Quaternion Rotation, Vector3 Scale)> _rest = new(StringComparer.Ordinal);

    private Puppet(GameObject root)
    {
        _root = root;
        foreach (var t in root.GetComponentsInChildren<Transform>(true))
        {
            if (t == null || t == root.transform) continue;
            string name = t.name;
            // First by name wins: the rig's joints come before any same-named child of an attachment.
            if (_bones.ContainsKey(name)) continue;
            _bones[name] = t;
            _rest[name] = (t.localPosition, t.localRotation, t.localScale);
        }
    }

    /// <summary>Wraps an existing copy. Its current pose becomes the rest pose.</summary>
    public static Puppet Of(GameObject copy)
    {
        if (copy == null) throw new ArgumentNullException(nameof(copy));
        return new Puppet(copy);
    }

    /// <summary>The copy's root. Move and turn it to place the puppet in the world.</summary>
    public GameObject Root => Alive();

    /// <summary>Every transform under the root, by name (joints, meshes, attachments).</summary>
    public IEnumerable<string> BoneNames => _bones.Keys;

    public bool Has(string bone) => _bones.ContainsKey(bone);

    public Transform Bone(string bone)
    {
        Alive();
        if (!_bones.TryGetValue(bone, out var t) || t == null)
            throw new KeyNotFoundException("puppet '" + _root.name + "' has no bone '" + bone + "'.");
        return t;
    }

    public bool TryGetBone(string bone, out Transform transform)
    {
        Alive();
        if (_bones.TryGetValue(bone, out var t) && t != null)
        {
            transform = t;
            return true;
        }
        transform = null!;
        return false;
    }

    /// <summary>The bone's local rotation at creation.</summary>
    public Quaternion RestLocalRotation(string bone)
    {
        Bone(bone);
        return _rest[bone].Rotation;
    }

    public void SetLocalRotation(string bone, Quaternion rotation) => Bone(bone).localRotation = rotation;

    /// <summary><c>localRotation = rest * delta</c>: <paramref name="delta"/> in the bone's own axes at rest.</summary>
    public void SetRotationFromRest(string bone, Quaternion delta)
    {
        var t = Bone(bone);
        t.localRotation = _rest[bone].Rotation * delta;
    }

    public void SetWorldRotation(string bone, Quaternion rotation) => Bone(bone).rotation = rotation;

    /// <summary>
    /// Turns <paramref name="bone"/> in world space, by the smallest rotation, so the line from it to
    /// <paramref name="child"/> (usually the next joint: hip to knee, elbow to wrist) points along
    /// <paramref name="worldDirection"/>. Twist about that line is left as it was. Useful for poses
    /// written in world terms ("thighs forward, shins down") without knowing each joint's local axes.
    /// </summary>
    public void AimAt(string bone, string child, Vector3 worldDirection)
    {
        var b = Bone(bone);
        var c = Bone(child);
        var current = c.position - b.position;
        if (current.sqrMagnitude < 1e-10f || worldDirection.sqrMagnitude < 1e-10f) return;
        b.rotation = Quaternion.FromToRotation(current, worldDirection) * b.rotation;
    }

    public void SetLocalPosition(string bone, Vector3 position) => Bone(bone).localPosition = position;

    /// <summary>
    /// Sets every material slot of the renderer named <paramref name="rendererName"/> (a mesh object
    /// such as <c>CHest_GEO</c> or <c>RazerHood_GEO</c>) to <paramref name="material"/>. Returns false
    /// when the puppet has no renderer by that name.
    /// </summary>
    public bool SetMaterial(string rendererName, Material material)
    {
        if (material == null) throw new ArgumentNullException(nameof(material));
        if (!TryGetBone(rendererName, out var t)) return false;
        var r = t.GetComponent<Renderer>();
        if (r == null) return false;
        int slots = Math.Max(1, r.sharedMaterials.Length);
        var mats = new Il2CppReferenceArray<Material>(slots);
        for (int i = 0; i < slots; i++) mats[i] = material;
        r.sharedMaterials = mats;
        return true;
    }

    /// <summary>
    /// Sets a color property (for example <c>_BaseColor</c>, or the customization-mask channels
    /// <c>_HairColor</c>, <c>_ExtraColor</c>, <c>_EyeColor</c>) on the renderer's property block, the
    /// way the game tints a character's skin and armor. The material asset is not modified.
    /// Returns false when the puppet has no renderer by that name.
    /// </summary>
    public bool SetColor(string rendererName, string property, Color color)
    {
        if (!TryGetBone(rendererName, out var t)) return false;
        var r = t.GetComponent<Renderer>();
        if (r == null) return false;
        var block = new MaterialPropertyBlock();
        r.GetPropertyBlock(block);
        block.SetColor(property, color);
        r.SetPropertyBlock(block);
        return true;
    }

    /// <summary>
    /// The bone's rotation at the bind pose, in the puppet root's space, from the skinned meshes'
    /// bind poses (joint = inverse of its bind matrix). Null for a bone no skinned mesh binds.
    /// </summary>
    public Quaternion? BindModelRotation(string bone)
    {
        EnsureBind();
        return _bind!.TryGetValue(bone, out var q) ? q : null;
    }

    private Dictionary<string, Quaternion>? _bind;
    private List<Transform>? _depthOrder;

    private void EnsureBind()
    {
        if (_bind != null) return;
        _bind = new Dictionary<string, Quaternion>(StringComparer.Ordinal);
        var root = Alive().transform;
        foreach (var smr in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            var mesh = smr.sharedMesh;
            if (mesh == null) continue;
            var bones = smr.bones;
            var binds = mesh.bindposes;
            // The renderer's bind space expressed in the root's space (identity for these rigs).
            var rendererToRoot = Quaternion.Inverse(root.rotation) * smr.transform.rotation;
            int n = Math.Min(bones.Length, binds.Length);
            for (int i = 0; i < n; i++)
            {
                var b = bones[i];
                if (b == null || _bind.ContainsKey(b.name)) continue;
                var joint = binds[i].inverse;
                _bind[b.name] = rendererToRoot * joint.rotation;
            }
        }
    }

    /// <summary>
    /// Poses the puppet from model-space rotation deltas against the bind pose (a
    /// <c>Progenitor.Film.TakeFile</c> frame): for each bone <paramref name="delta"/> returns a
    /// rotation for, the joint's world rotation becomes root × delta × bind. Bones are set parents
    /// first, so each child is placed after its parent moved. <paramref name="pelvisModel"/>, when
    /// given, places <c>Pelvis_JNT</c> at that point of the root's space (rig units). Bones without a
    /// delta, or that no skinned mesh binds, keep their current rotation.
    /// </summary>
    public void ApplyModelPose(Func<string, Quaternion?> delta, Vector3? pelvisModel = null)
    {
        EnsureBind();
        var root = Alive().transform;
        if (_depthOrder == null)
        {
            _depthOrder = new List<Transform>(_bones.Values);
            int Depth(Transform t) { int d = 0; for (var p = t.parent; p != null && p != root; p = p.parent) d++; return d; }
            var depth = new Dictionary<Transform, int>();
            foreach (var t in _depthOrder) depth[t] = Depth(t);
            _depthOrder.Sort((a, b) => depth[a].CompareTo(depth[b]));
        }
        var rootRot = root.rotation;
        foreach (var t in _depthOrder)
        {
            if (t == null || !_bind!.TryGetValue(t.name, out var bind)) continue;
            var d = delta(t.name);
            if (d == null) continue;
            t.rotation = rootRot * (d.Value * bind);
        }
        if (pelvisModel.HasValue && TryGetBone("Pelvis_JNT", out var pelvis))
            pelvis.position = root.TransformPoint(pelvisModel.Value);
    }

    /// <summary>Every bone back to its rest position, rotation and scale.</summary>
    public void ResetPose()
    {
        Alive();
        foreach (var pair in _rest)
        {
            if (!_bones.TryGetValue(pair.Key, out var t) || t == null) continue;
            t.localPosition = pair.Value.Position;
            t.localRotation = pair.Value.Rotation;
            t.localScale = pair.Value.Scale;
        }
    }

    /// <summary>
    /// Moves every skinned mesh of <paramref name="equipment"/> (an inert copy of an equipment
    /// prefab such as <c>VampireFemale_ArmorSet04_Dracula_Chest</c>) onto this puppet: each mesh's
    /// bones are replaced by this rig's bones of the same name, the mesh object is parented under
    /// the puppet root, and the rest of the equipment copy is destroyed. Static attachments
    /// (<c>MeshRenderer</c>s under an equipment bone) move to the puppet's bone of the same name
    /// with their local transform. Bones the puppet lacks keep the equipment's own transform and
    /// are listed in the result; those parts will not follow a pose. Call before posing: the
    /// equipment is bound to the rig as it stands. Equipment prefabs have empty material slots (the
    /// game fills them on equip); set them afterwards with <see cref="SetMaterial"/>.
    /// </summary>
    public DressResult Dress(GameObject equipment)
    {
        Alive();
        if (equipment == null) throw new ArgumentNullException(nameof(equipment));
        var missing = new List<string>();
        var moved = new List<Transform>();
        int skinned = 0, attached = 0;

        foreach (var smr in equipment.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            if (smr == null) continue;
            var source = smr.bones;
            var names = new string?[source.Length];
            for (int i = 0; i < source.Length; i++) names[i] = source[i] != null ? source[i].name : null;
            var (mapped, lacking) = PuppetBinding.Map(names, _bones.ContainsKey);
            foreach (var m in lacking) if (!missing.Contains(m)) missing.Add(m);

            var bound = new Il2CppReferenceArray<Transform>(source.Length);
            for (int i = 0; i < source.Length; i++)
                bound[i] = mapped[i] != null ? _bones[mapped[i]!] : source[i];
            smr.bones = bound;
            if (smr.rootBone != null && _bones.TryGetValue(smr.rootBone.name, out var root)) smr.rootBone = root;
            smr.updateWhenOffscreen = true;
            moved.Add(smr.transform);
            skinned++;
        }

        foreach (var mr in equipment.GetComponentsInChildren<MeshRenderer>(true))
        {
            if (mr == null) continue;
            var parent = mr.transform.parent;
            if (parent == null || !_bones.TryGetValue(parent.name, out var bone)) continue;
            var local = (mr.transform.localPosition, mr.transform.localRotation, mr.transform.localScale);
            mr.transform.SetParent(bone, false);
            mr.transform.localPosition = local.localPosition;
            mr.transform.localRotation = local.localRotation;
            mr.transform.localScale = local.localScale;
            attached++;
        }

        foreach (var t in moved)
        {
            t.SetParent(_root.transform, false);
            // Dressed meshes join the name map so SetMaterial and TryGetBone find them.
            if (!_bones.ContainsKey(t.name))
            {
                _bones[t.name] = t;
                _rest[t.name] = (t.localPosition, t.localRotation, t.localScale);
            }
        }
        // Immediately, not at end of frame: the equipment root's components clear the materials of
        // the renderers they managed when they are destroyed (live 2026-09-28: materials set after a
        // deferred Destroy in the same frame were gone the next frame). Destroyed now, whatever they
        // do happens before the caller assigns materials.
        if (equipment != _root) UnityObject.DestroyImmediate(equipment);
        return new DressResult(skinned, attached, missing);
    }

    private GameObject Alive()
    {
        if (_root == null) throw new InvalidOperationException("the puppet's GameObject was destroyed.");
        return _root;
    }
}

/// <summary>What <see cref="Puppet.Dress"/> moved.</summary>
public readonly struct DressResult
{
    internal DressResult(int skinnedMeshes, int attachments, IReadOnlyList<string> missingBones)
    {
        SkinnedMeshes = skinnedMeshes;
        Attachments = attachments;
        MissingBones = missingBones;
    }

    public int SkinnedMeshes { get; }
    public int Attachments { get; }

    /// <summary>Equipment bones the puppet does not have; those parts will not follow a pose.</summary>
    public IReadOnlyList<string> MissingBones { get; }
}
#endif
