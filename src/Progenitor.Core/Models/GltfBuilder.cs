#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using UnityEngine.Rendering;
using UnityObject = UnityEngine.Object;

namespace Progenitor.Models;

/// <summary>
/// Builds GameObjects from a <see cref="GltfScene"/>: one per node, a <c>Mesh</c> per glTF mesh
/// (its primitives become submeshes), a <c>Texture2D</c> per used image, and an HDRP material per
/// glTF material. Lit materials use <c>HDRP/Lit</c> (base color and map, metallic, smoothness =
/// 1 - roughness, emissive color and map, alpha test, double-sided); <c>KHR_materials_unlit</c>
/// materials use <c>HDRP/Unlit</c>. Both shaders are present and supported in the game's build
/// (checked live 2026-09-28). Everything created is recorded so <see cref="ImportedModel"/> can
/// destroy it.
/// </summary>
internal static class GltfBuilder
{
    internal static GameObject Build(GltfScene scene, string name, List<UnityObject> created, out Transform[] nodes)
    {
        var textures = new Texture2D?[scene.Images.Count];
        Texture2D? Texture(int textureIndex, bool linear)
        {
            if (textureIndex < 0 || textureIndex >= scene.TextureImages.Count) return null;
            int image = scene.TextureImages[textureIndex];
            if (image < 0 || image >= scene.Images.Count) return null;
            if (textures[image] != null) return textures[image];
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, true, linear) { name = scene.Images[image].Name };
            created.Add(tex);
            if (!ImageConversion.LoadImage(tex, scene.Images[image].Bytes, false))
                throw new FormatException("image " + image + " (" + scene.Images[image].MimeType + ") did not decode.");
            tex.wrapMode = TextureWrapMode.Repeat;
            tex.filterMode = FilterMode.Trilinear;
            tex.anisoLevel = 4;
            textures[image] = tex;
            return tex;
        }

        var materials = new Material[scene.Materials.Count];
        for (int i = 0; i < materials.Length; i++)
        {
            materials[i] = MakeMaterial(scene.Materials[i], Texture, created);
        }
        Material? fallback = null;

        var meshes = new Mesh[scene.Meshes.Count];
        var meshMaterials = new Material[scene.Meshes.Count][];
        for (int i = 0; i < meshes.Length; i++)
        {
            var gm = scene.Meshes[i];
            meshes[i] = MakeMesh(gm, created);
            meshMaterials[i] = new Material[gm.Primitives.Count];
            for (int p = 0; p < gm.Primitives.Count; p++)
            {
                int m = gm.Primitives[p].Material;
                if (m >= 0 && m < materials.Length) meshMaterials[i][p] = materials[m];
                else
                {
                    if (fallback == null)
                    {
                        fallback = MakeMaterial(new GltfMaterial("default") { Metallic = 0f }, Texture, created);
                    }
                    meshMaterials[i][p] = fallback;
                }
            }
        }

        var root = new GameObject(name);
        nodes = new Transform[scene.Nodes.Count];
        try
        {
            foreach (int r in scene.RootNodes) BuildNode(scene, r, root.transform, nodes, 0);
            for (int index = 0; index < nodes.Length; index++)
            {
                if (nodes[index] == null) continue;
                var n = scene.Nodes[index];
                if (n.Mesh < 0) continue;
                Renderer renderer;
                if (n.Skin >= 0)
                {
                    var skin = scene.Skins[n.Skin];
                    // Each skin owns its bind palette, even when multiple nodes share one mesh.
                    var mesh = UnityObject.Instantiate(meshes[n.Mesh]);
                    created.Add(mesh);
                    var binds = new Il2CppStructArray<Matrix4x4>(skin.Joints.Length);
                    var bones = new Il2CppReferenceArray<Transform>(skin.Joints.Length);
                    for (int joint = 0; joint < skin.Joints.Length; joint++)
                    {
                        var matrix = new Matrix4x4();
                        for (int cell = 0; cell < 16; cell++) matrix[cell] = skin.InverseBindMatrices[joint * 16 + cell];
                        binds[joint] = matrix;
                        bones[joint] = nodes[skin.Joints[joint]];
                    }
                    mesh.bindposes = binds;
                    var skinned = nodes[index].gameObject.AddComponent<SkinnedMeshRenderer>();
                    skinned.sharedMesh = mesh;
                    skinned.bones = bones;
                    skinned.rootBone = skin.Skeleton >= 0 ? nodes[skin.Skeleton] : root.transform;
                    skinned.updateWhenOffscreen = true;
                    renderer = skinned;
                }
                else
                {
                    nodes[index].gameObject.AddComponent<MeshFilter>().sharedMesh = meshes[n.Mesh];
                    renderer = nodes[index].gameObject.AddComponent<MeshRenderer>();
                }
                var materialsForNode = new Il2CppReferenceArray<Material>(meshMaterials[n.Mesh].Length);
                for (int i = 0; i < materialsForNode.Length; i++) materialsForNode[i] = meshMaterials[n.Mesh][i];
                renderer.sharedMaterials = materialsForNode;
                renderer.shadowCastingMode = ShadowCastingMode.On;
            }
        }
        catch
        {
            UnityObject.Destroy(root);
            throw;
        }
        return root;
    }

    private static void BuildNode(GltfScene scene, int index, Transform parent, Transform[] nodes, int depth)
    {
        if (depth > 64) throw new FormatException("node hierarchy deeper than 64 (a cycle?).");
        var n = scene.Nodes[index];
        var go = new GameObject(n.Name);
        var t = go.transform;
        nodes[index] = t;
        t.SetParent(parent, false);
        t.localPosition = new Vector3(n.Translation[0], n.Translation[1], n.Translation[2]);
        t.localRotation = new Quaternion(n.Rotation[0], n.Rotation[1], n.Rotation[2], n.Rotation[3]);
        t.localScale = new Vector3(n.Scale[0], n.Scale[1], n.Scale[2]);
        foreach (int c in n.Children) BuildNode(scene, c, t, nodes, depth + 1);
    }

    private static Mesh MakeMesh(GltfMesh gm, List<UnityObject> created)
    {
        int total = 0;
        foreach (var p in gm.Primitives) total += p.Positions.Length / 3;
        var verts = new Il2CppStructArray<Vector3>(total);
        var normals = new Il2CppStructArray<Vector3>(total);
        var uvs = new Il2CppStructArray<Vector2>(total);
        var weights = new Il2CppStructArray<BoneWeight>(total);
        bool anyWeights = false;
        bool anyNormals = false;
        int at = 0;
        foreach (var p in gm.Primitives)
        {
            int count = p.Positions.Length / 3;
            for (int v = 0; v < count; v++)
            {
                verts[at + v] = new Vector3(p.Positions[v * 3], p.Positions[v * 3 + 1], p.Positions[v * 3 + 2]);
                if (p.Normals != null) { normals[at + v] = new Vector3(p.Normals[v * 3], p.Normals[v * 3 + 1], p.Normals[v * 3 + 2]); anyNormals = true; }
                if (p.Uvs != null) uvs[at + v] = new Vector2(p.Uvs[v * 2], p.Uvs[v * 2 + 1]);
                if (p.Joints != null && p.Weights != null)
                {
                    int j = v * 4;
                    weights[at + v] = new BoneWeight { boneIndex0 = p.Joints[j], boneIndex1 = p.Joints[j + 1],
                        boneIndex2 = p.Joints[j + 2], boneIndex3 = p.Joints[j + 3],
                        weight0 = p.Weights[j], weight1 = p.Weights[j + 1], weight2 = p.Weights[j + 2], weight3 = p.Weights[j + 3] };
                    anyWeights = true;
                }
            }
            at += count;
        }

        var mesh = new Mesh { name = gm.Name };
        created.Add(mesh);
        if (total > 65535) mesh.indexFormat = IndexFormat.UInt32;
        mesh.vertices = verts;
        if (anyWeights) mesh.boneWeights = weights;
        mesh.uv = uvs;
        mesh.subMeshCount = gm.Primitives.Count;
        int baseVertex = 0;
        for (int s = 0; s < gm.Primitives.Count; s++)
        {
            var p = gm.Primitives[s];
            var tri = new Il2CppStructArray<int>(p.Indices.Length);
            for (int i = 0; i < p.Indices.Length; i++) tri[i] = p.Indices[i] + baseVertex;
            mesh.SetTriangles(tri, s, false);
            baseVertex += p.Positions.Length / 3;
        }
        if (anyNormals) mesh.normals = normals;
        else mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        mesh.RecalculateTangents();
        return mesh;
    }

    private static Material MakeMaterial(GltfMaterial g, Func<int, bool, Texture2D?> texture, List<UnityObject> created)
    {
        var baseColor = new Color(g.BaseColor[0], g.BaseColor[1], g.BaseColor[2], g.BaseColor[3]);
        var baseMap = texture(g.BaseColorTexture, false);
        Material m;
        if (g.Unlit)
        {
            m = new Material(Shader.Find("HDRP/Unlit")) { name = g.Name };
            created.Add(m);
            m.SetColor("_UnlitColor", baseColor);
            if (baseMap != null) m.SetTexture("_UnlitColorMap", baseMap);
        }
        else
        {
            m = new Material(Shader.Find("HDRP/Lit")) { name = g.Name };
            created.Add(m);
            m.SetColor("_BaseColor", baseColor);
            if (baseMap != null) m.SetTexture("_BaseColorMap", baseMap);
            m.SetFloat("_Metallic", g.Metallic);
            m.SetFloat("_Smoothness", 1f - g.Roughness);

            float k = g.EmissiveStrength;
            var emissive = new Color(g.Emissive[0] * k, g.Emissive[1] * k, g.Emissive[2] * k, 1f);
            var emissiveMap = texture(g.EmissiveTexture, false);
            if (emissive.maxColorComponent > 0f || emissiveMap != null)
            {
                m.SetColor("_EmissiveColor", emissive);
                m.SetColor("_EmissiveColorLDR", emissive);
                if (emissiveMap != null)
                {
                    m.SetTexture("_EmissiveColorMap", emissiveMap);
                    m.EnableKeyword("_EMISSIVE_COLOR_MAP");
                }
            }
        }

        if (g.AlphaMode == "MASK" || g.AlphaMode == "BLEND")
        {
            // BLEND is drawn as alpha test: a transparent HDRP material needs render-state setup the
            // editor normally does, and props rarely need true transparency.
            m.SetFloat("_AlphaCutoffEnable", 1f);
            m.SetFloat("_AlphaCutoff", g.AlphaMode == "MASK" ? g.AlphaCutoff : 0.5f);
            m.EnableKeyword("_ALPHATEST_ON");
        }
        if (g.DoubleSided)
        {
            m.SetFloat("_DoubleSidedEnable", 1f);
            m.SetFloat("_CullMode", 0f);
            m.SetFloat("_CullModeForward", 0f);
            m.EnableKeyword("_DOUBLESIDED_ON");
        }
        return m;
    }
}

/// <summary>
/// A GLB imported with <see cref="PluginModels.ImportGlb"/>: the root GameObject (under this
/// plugin's <c>progenitor-models-&lt;plugin&gt;</c> root, at the origin) and everything made for it.
/// Dispose destroys the GameObjects and the meshes, materials and textures, which Unity does not
/// free on its own.
/// </summary>
public sealed class ImportedModel : IDisposable
{
    private readonly List<UnityObject> _created;

    internal ImportedModel(GameObject root, List<UnityObject> created, GltfScene scene, Transform[] nodes)
    {
        Root = root;
        _created = created;
        Animations = new ImportedAnimations(root, scene, nodes);
    }

    public GameObject Root { get; }
    public ImportedAnimations Animations { get; }

    /// <summary>The first descendant GameObject named <paramref name="name"/> (a glTF node name), or null.</summary>
    public GameObject? Find(string name)
    {
        if (Root == null) return null;
        foreach (var t in Root.GetComponentsInChildren<Transform>(true))
            if (t.name == name) return t.gameObject;
        return null;
    }

    /// <summary>The material this import made for the glTF material named <paramref name="name"/>, or null.</summary>
    public Material? Material(string name)
    {
        foreach (var o in _created)
        {
            var m = o?.TryCast<Material>();
            if (m != null && m.name == name) return m;
        }
        return null;
    }

    public void Dispose()
    {
        Animations.Dispose();
        try { if (Root != null) UnityObject.Destroy(Root); } catch (Exception e) { Plugin.Logger?.LogWarning("Models: destroy import threw: " + e.Message); }
        foreach (var o in _created)
        {
            try { if (o != null) UnityObject.Destroy(o); } catch { /* already gone */ }
        }
        _created.Clear();
    }
}
#endif
