#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using UnityEngine;

namespace Progenitor.Models;

/// <summary>Read-only, client/main-thread export for local clothing authoring.</summary>
public static partial class RigReference
{
    /// <summary>
    /// Captures explicit skinned renderers under one model root: complete authored triangle
    /// topology, normals, UVs, skin, bind matrices, joint hierarchy and this frame's pose/bake.
    /// No reference meshes or materials are changed. Unreadable meshes, blend shapes, scaled
    /// renderers and inconsistent binds fail explicitly rather than exporting a guessed rig.
    /// </summary>
    public static RigReferenceSnapshot Capture(Transform modelRoot, params SkinnedMeshRenderer[] renderers)
        => Capture(modelRoot, renderers, Array.Empty<SkinnedMeshRenderer>());

    /// <summary>
    /// Capture readable geometry and supplement joint rests from explicit bind-reference
    /// renderers. Bind-reference meshes may be unreadable; their geometry is never exported.
    /// Every supplied bind must agree on actual bone identity and model-root rest transform.
    /// </summary>
    public static RigReferenceSnapshot Capture(Transform modelRoot,
        SkinnedMeshRenderer[] renderers, SkinnedMeshRenderer[] bindReferenceRenderers)
    {
        if (Plugin.IsServerProcess) throw new InvalidOperationException("RigReference.Capture is client-only.");
        if (!Dev.MainThread.IsMainThread) throw new InvalidOperationException("Capture the rig on the Unity main thread.");
        if (modelRoot == null) throw new ArgumentNullException(nameof(modelRoot));
        if (renderers == null || renderers.Length == 0) throw new ArgumentException("Select at least one skinned renderer.", nameof(renderers));
        if (bindReferenceRenderers == null) throw new ArgumentNullException(nameof(bindReferenceRenderers));
        var nodes = new List<Transform>();
        var nodeIndices = new Dictionary<int, int>();
        var rests = new Dictionary<int, Matrix4x4>();
        var parents = new List<int>();
        var paths = new List<string>();
        var meshIds = new HashSet<int>();
        var boneNames = new Dictionary<string, int>(StringComparer.Ordinal);
        var rootInverse = modelRoot.worldToLocalMatrix;
        Matrix(rootInverse);
        int AddNode(Transform node)
        {
            if (node == null) throw new InvalidDataException("A renderer contains a missing bone.");
            int id = node.GetInstanceID();
            if (nodeIndices.TryGetValue(id, out int found)) return found;
            int parent;
            if (node == modelRoot) parent = -1;
            else
            {
                if (node.parent == null) throw new InvalidDataException("Every selected renderer and bone must be under the explicit model root.");
                parent = AddNode(node.parent);
            }
            int index = nodes.Count;
            nodes.Add(node); parents.Add(parent); nodeIndices.Add(id, index);
            paths.Add((parent < 0 ? "" : paths[parent] + "/") + node.name + "[" + node.GetSiblingIndex() + "]");
            return index;
        }
        AddNode(modelRoot);
        var rigBones = new HashSet<int>();
        foreach (var renderer in modelRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            if (renderer != null)
                foreach (var bone in renderer.bones)
                    if (bone != null) rigBones.Add(bone.GetInstanceID());
        void RecordRest(SkinnedMeshRenderer renderer, Matrix4x4 toRoot)
        {
            var sourceBones = renderer.bones;
            var binds = renderer.sharedMesh.bindposes;
            if (sourceBones.Length == 0 || sourceBones.Length != binds.Length)
                throw new InvalidDataException(renderer.name + ": incomplete bind matrices.");
            Matrix(toRoot);
            for (int i = 0; i < sourceBones.Length; i++)
            {
                var bone = sourceBones[i];
                int node = AddNode(bone);
                int id = bone.GetInstanceID();
                if (boneNames.TryGetValue(bone.name, out int previousId) && previousId != id)
                    throw new InvalidDataException("Different bones share the name " + bone.name + ". Capture one coherent rig.");
                boneNames[bone.name] = id;
                Matrix(binds[i]);
                var rest = toRoot * binds[i].inverse;
                Matrix(rest);
                if (rests.TryGetValue(node, out var previous))
                    for (int row = 0; row < 4; row++) for (int col = 0; col < 4; col++)
                        if (MathF.Abs(previous[row, col] - rest[row, col]) > .0001f)
                            throw new InvalidDataException("Selected meshes disagree on the authored bind for " + bone.name + ". Capture incompatible rigs separately.");
                rests[node] = rest;
            }
        }
        foreach (var renderer in bindReferenceRenderers)
        {
            if (renderer == null || !meshIds.Add(renderer.GetInstanceID()))
                throw new ArgumentException("Bind-reference selection contains a null or duplicate renderer.", nameof(bindReferenceRenderers));
            // The renderer frame must be stable relative to the model, not carried by
            // an animated joint whose current pose would contaminate its bind rests.
            bool foundRoot = false;
            for (var p = renderer.transform; p != null; p = p.parent)
            {
                if (p == modelRoot) { foundRoot = true; break; }
                if (rigBones.Contains(p.GetInstanceID()))
                    throw new InvalidDataException(renderer.name + ": bind-reference renderer is under an animated joint.");
            }
            if (!foundRoot) throw new InvalidDataException(renderer.name + ": bind-reference renderer is outside the model root.");
            var mesh = renderer.sharedMesh;
            if (mesh == null) throw new InvalidDataException(renderer.name + ": bind-reference mesh is missing.");
            RecordRest(renderer, rootInverse * renderer.transform.localToWorldMatrix);
        }
        var exports = new List<MeshData>();
        int vertexCount = 0;
        foreach (var renderer in renderers)
        {
            if (renderer == null || !meshIds.Add(renderer.GetInstanceID())) throw new ArgumentException("Renderer selection contains a null or duplicate, including bind-reference renderers.", nameof(renderers));
            AddNode(renderer.transform);
            var mesh = renderer.sharedMesh;
            if (mesh == null || !mesh.isReadable) throw new InvalidDataException(renderer.name + ": a complete readable authored mesh is required.");
            if (mesh.blendShapeCount != 0) throw new InvalidDataException(renderer.name + ": blend shapes are not supported by reference schema 1.");
            var scale = renderer.transform.lossyScale;
            if (MathF.Abs(scale.x - 1) > .0001f || MathF.Abs(scale.y - 1) > .0001f || MathF.Abs(scale.z - 1) > .0001f)
                throw new InvalidDataException(renderer.name + ": non-unit renderer scale has not been qualified for BakeMesh capture.");
            var toRoot = rootInverse * renderer.transform.localToWorldMatrix;
            var sourceBones = renderer.bones;
            var binds = mesh.bindposes;
            RecordRest(renderer, toRoot);
            var data = new MeshData { Name = renderer.name, RendererToRoot = Matrix(toRoot), Bones = new int[sourceBones.Length], Binds = new float[sourceBones.Length][] };
            for (int i = 0; i < sourceBones.Length; i++)
            {
                int node = AddNode(sourceBones[i]);
                data.Bones[i] = node; data.Binds[i] = Matrix(binds[i]);
            }
            var vertices = mesh.vertices;
            var normals = mesh.normals;
            var uv = mesh.uv;
            var skin = mesh.boneWeights;
            data.Vertices = new float[vertices.Length][]; data.Normals = new float[normals.Length][]; data.Uv = new float[uv.Length][];
            data.Indices = new int[skin.Length][]; data.Weights = new float[skin.Length][];
            for (int i = 0; i < vertices.Length; i++) data.Vertices[i] = Vector(vertices[i]);
            for (int i = 0; i < normals.Length; i++) data.Normals[i] = Vector(normals[i]);
            for (int i = 0; i < uv.Length; i++) { data.Uv[i] = new[] { uv[i].x, uv[i].y }; RigReferenceValidation.Vector(data.Uv[i], 2); }
            if (normals.Length != vertices.Length || (uv.Length != 0 && uv.Length != vertices.Length)) throw new InvalidDataException(renderer.name + ": incomplete normals or UVs.");
            for (int i = 0; i < skin.Length; i++)
            {
                var w = skin[i]; data.Indices[i] = new[] { w.boneIndex0, w.boneIndex1, w.boneIndex2, w.boneIndex3 };
                data.Weights[i] = new[] { w.weight0, w.weight1, w.weight2, w.weight3 };
            }
            var triangles = mesh.triangles; data.Triangles = new int[triangles.Length];
            for (int i = 0; i < triangles.Length; i++) data.Triangles[i] = triangles[i];
            var materials = renderer.sharedMaterials;
            long totalIndices = 0;
            for (int i = 0; i < mesh.subMeshCount; i++)
            {
                uint count = mesh.GetIndexCount(i);
                if (count > int.MaxValue || count % 3 != 0)
                    throw new InvalidDataException(renderer.name + ": submesh must contain triangle indices.");
                totalIndices += count;
                var material = i < materials.Length ? materials[i] : null;
                string materialName = material != null ? material.name : "";
                float[]? color = null;
                if (material != null && (materialName == "progenitor-wear-cloth" || materialName == "progenitor-wear-gold" || materialName == "progenitor-wear-void"))
                {
                    string property = materialName == "progenitor-wear-void" ? "_UnlitColor" : "_BaseColor";
                    if (material.HasProperty(property))
                    {
                        var rgba = material.GetColor(property);
                        color = new[] { rgba.r, rgba.g, rgba.b, rgba.a };
                        foreach (float channel in color)
                            if (!float.IsFinite(channel) || channel < 0f || channel > 1f)
                                throw new InvalidDataException(materialName + ": expected finite color channels in [0,1].");
                    }
                }
                data.Submeshes.Add(((int)count, materialName, color));
            }
            if (totalIndices != data.Triangles.Length)
                throw new InvalidDataException(renderer.name + ": submesh counts disagree with flattened triangles.");
            RigReferenceValidation.Mesh(data.Vertices, data.Triangles, data.Bones.Length, data.Indices, data.Weights);
            var baked = new Mesh();
            try
            {
                renderer.BakeMesh(baked); var posed = baked.vertices;
                if (posed.Length != vertices.Length) throw new InvalidDataException("BakeMesh changed vertex count.");
                data.Baked = new float[posed.Length][];
                for (int i = 0; i < posed.Length; i++) data.Baked[i] = Vector(posed[i]);
            }
            finally { UnityEngine.Object.Destroy(baked); }
            vertexCount += vertices.Length; exports.Add(data);
        }
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartObject(); writer.WriteNumber("schemaVersion", 1);
            writer.WriteString("coordinateSystem", "unity-left-handed-y-up"); writer.WriteString("matrixLayout", "row-major");
            writer.WriteString("rootName", modelRoot.name); writer.WriteString("capturedUtc", DateTime.UtcNow);
            writer.WriteString("coreVersion", typeof(RigReference).Assembly.GetName().Version?.ToString());
            writer.WriteStartArray("nodes");
            for (int i = 0; i < nodes.Count; i++)
            {
                writer.WriteStartObject(); writer.WriteString("name", nodes[i].name); writer.WriteString("path", paths[i]); writer.WriteNumber("parent", parents[i]);
                writer.WritePropertyName("restMatrix"); if (rests.TryGetValue(i, out var rest)) RigReferenceValidation.Array(writer, Matrix(rest)); else writer.WriteNullValue();
                writer.WritePropertyName("poseMatrix"); RigReferenceValidation.Array(writer, Matrix(rootInverse * nodes[i].localToWorldMatrix)); writer.WriteEndObject();
            }
            writer.WriteEndArray(); writer.WriteStartArray("meshes");
            foreach (var m in exports)
            {
                writer.WriteStartObject(); writer.WriteString("name", m.Name);
                writer.WritePropertyName("rendererToRoot"); RigReferenceValidation.Array(writer, m.RendererToRoot);
                WriteVectors("vertices", m.Vertices); WriteVectors("normals", m.Normals); WriteVectors("uv", m.Uv); WriteVectors("bindPoses", m.Binds); WriteVectors("bakedVertices", m.Baked);
                writer.WritePropertyName("triangles"); RigReferenceValidation.Array(writer, m.Triangles);
                writer.WriteStartArray("submeshes");
                foreach (var submesh in m.Submeshes)
                {
                    writer.WriteStartObject(); writer.WriteNumber("indexCount", submesh.IndexCount);
                    writer.WriteString("materialName", submesh.MaterialName);
                    if (submesh.Color != null) { writer.WritePropertyName("color"); RigReferenceValidation.Array(writer, submesh.Color); }
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WritePropertyName("bones"); RigReferenceValidation.Array(writer, m.Bones);
                writer.WriteStartArray("weights");
                for (int i = 0; i < m.Weights.Length; i++)
                {
                    writer.WriteStartObject(); writer.WritePropertyName("indices"); RigReferenceValidation.Array(writer, m.Indices[i]);
                    writer.WritePropertyName("values"); RigReferenceValidation.Array(writer, m.Weights[i]); writer.WriteEndObject();
                }
                writer.WriteEndArray(); writer.WriteEndObject();
            }
            writer.WriteEndArray(); writer.WriteEndObject();
            void WriteVectors(string name, float[][] values) { writer.WritePropertyName(name); RigReferenceValidation.Vectors(writer, values); }
        }
        return new RigReferenceSnapshot(Encoding.UTF8.GetString(output.ToArray()), exports.Count, vertexCount, nodes.Count);
    }

    private static float[] Vector(Vector3 v)
    { var values = new[] { v.x, v.y, v.z }; RigReferenceValidation.Vector(values, 3); return values; }

    private static float[] Matrix(Matrix4x4 matrix)
    {
        var values = new float[16];
        for (int row = 0; row < 4; row++) for (int col = 0; col < 4; col++) values[row * 4 + col] = matrix[row, col];
        RigReferenceValidation.Vector(values, 16);
        if (!float.IsFinite(matrix.determinant) || MathF.Abs(matrix.determinant) < .0000001f) throw new InvalidDataException("Reference contains a singular transform.");
        return values;
    }

    private sealed class MeshData
    {
        internal string Name = "";
        internal readonly List<(int IndexCount, string MaterialName, float[]? Color)> Submeshes = new();
        internal float[] RendererToRoot = Array.Empty<float>();
        internal float[][] Vertices = Array.Empty<float[]>(), Normals = Array.Empty<float[]>(), Uv = Array.Empty<float[]>(), Binds = Array.Empty<float[]>(), Weights = Array.Empty<float[]>(), Baked = Array.Empty<float[]>();
        internal int[] Bones = Array.Empty<int>(), Triangles = Array.Empty<int>();
        internal int[][] Indices = Array.Empty<int[]>();
    }
}
#endif
