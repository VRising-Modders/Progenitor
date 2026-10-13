#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using UnityEngine;

namespace Progenitor.Models;

/// <summary>A posed, local surface measurement. It contains no bind geometry or skin weights.</summary>
public sealed class PoseSurfaceSnapshot
{
    internal PoseSurfaceSnapshot(string json, int meshCount, int vertexCount, int jointCount)
    { Json = json; MeshCount = meshCount; VertexCount = vertexCount; JointCount = jointCount; }

    public string Json { get; }
    public int MeshCount { get; }
    public int VertexCount { get; }
    public int JointCount { get; }

    /// <summary>Writes a new UTF-8 JSON file; an existing capture is never replaced.</summary>
    public void Save(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A capture path is required.", nameof(path));
        using var file = new FileStream(Path.GetFullPath(path), FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new StreamWriter(file, new UTF8Encoding(false));
        writer.Write(Json);
    }
}

public static partial class RigReference
{
    /// <summary>
    /// Captures one frame of explicitly selected skinned surfaces for local clearance study.
    /// Posed vertices are in model-root space. This snapshot is not an authored bind reference,
    /// animation clip, or coverage approval. The source renderers and meshes are not modified.
    /// </summary>
    public static PoseSurfaceSnapshot CapturePoseSurface(Transform modelRoot, params SkinnedMeshRenderer[] renderers)
    {
        if (Plugin.IsServerProcess) throw new InvalidOperationException("Pose surface capture is client-only.");
        if (!Dev.MainThread.IsMainThread) throw new InvalidOperationException("Capture the pose on the Unity main thread.");
        if (modelRoot == null) throw new ArgumentNullException(nameof(modelRoot));
        if (renderers == null || renderers.Length < 1 || renderers.Length > 16)
            throw new ArgumentException("Select 1..16 skinned renderers.", nameof(renderers));

        var rootInverse = modelRoot.worldToLocalMatrix;
        Matrix(rootInverse);
        var nodes = new List<Transform>();
        var parents = new List<int>();
        var paths = new List<string>();
        var indices = new Dictionary<int, int>();
        int AddNode(Transform node)
        {
            if (node == null) throw new InvalidDataException("A selected renderer has a missing bone.");
            int id = node.GetInstanceID();
            if (indices.TryGetValue(id, out int found)) return found;
            int parent;
            if (node == modelRoot) parent = -1;
            else
            {
                if (node.parent == null)
                    throw new InvalidDataException("Every selected renderer and bone must be under the model root.");
                parent = AddNode(node.parent);
            }
            int index = nodes.Count;
            nodes.Add(node); parents.Add(parent); indices.Add(id, index);
            paths.Add((parent < 0 ? "" : paths[parent] + "/") + node.name + "[" + node.GetSiblingIndex() + "]");
            return index;
        }
        AddNode(modelRoot);

        var seen = new HashSet<int>();
        var meshes = new List<(string Name, int RendererNode, int RootBone, int[] Bones, float[][] Vertices, int[] Triangles)>();
        int totalVertices = 0;
        int totalIndices = 0;
        foreach (var renderer in renderers)
        {
            if (renderer == null || !seen.Add(renderer.GetInstanceID()))
                throw new ArgumentException("Renderer selection contains a null or duplicate.", nameof(renderers));
            int rendererNode = AddNode(renderer.transform);
            if (renderer.sharedMesh == null) throw new InvalidDataException(renderer.name + ": missing source mesh.");
            var scale = renderer.transform.lossyScale;
            if (MathF.Abs(scale.x - 1) > .0001f || MathF.Abs(scale.y - 1) > .0001f || MathF.Abs(scale.z - 1) > .0001f)
                throw new InvalidDataException(renderer.name + ": non-unit renderer scale is not qualified for BakeMesh capture.");
            var bones = renderer.bones;
            var boneNodes = new int[bones.Length];
            for (int i = 0; i < bones.Length; i++) boneNodes[i] = AddNode(bones[i]);
            int rootBone = renderer.rootBone == null ? -1 : AddNode(renderer.rootBone);
            var toRoot = rootInverse * renderer.transform.localToWorldMatrix;
            Matrix(toRoot);

            var baked = new Mesh();
            try
            {
                renderer.BakeMesh(baked);
                var points = baked.vertices;
                var triangles = baked.triangles;
                if (points.Length != renderer.sharedMesh.vertexCount)
                    throw new InvalidDataException(renderer.name + ": BakeMesh changed vertex count.");
                if (points.Length == 0 || points.Length > 2000000 || totalVertices > 2000000 - points.Length)
                    throw new InvalidDataException("Pose surface capture requires 1..2000000 total vertices.");
                if (triangles.Length == 0 || triangles.Length % 3 != 0 || triangles.Length > 6000000
                    || totalIndices > 6000000 - triangles.Length)
                    throw new InvalidDataException(renderer.name + ": baked mesh has no complete triangle topology.");
                var rootPoints = new float[points.Length][];
                for (int i = 0; i < points.Length; i++) rootPoints[i] = Vector(toRoot.MultiplyPoint3x4(points[i]));
                for (int i = 0; i < triangles.Length; i++)
                    if (triangles[i] < 0 || triangles[i] >= points.Length)
                        throw new InvalidDataException(renderer.name + ": baked triangle index is outside the vertex array.");
                meshes.Add((renderer.name, rendererNode, rootBone, boneNodes, rootPoints, triangles));
                totalVertices += points.Length;
                totalIndices += triangles.Length;
            }
            finally { UnityEngine.Object.Destroy(baked); }
        }

        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", 1);
            writer.WriteString("kind", "posed-surface");
            writer.WriteBoolean("referenceOnly", true);
            writer.WriteString("coordinateSystem", "unity-left-handed-y-up");
            writer.WriteString("matrixLayout", "row-major");
            writer.WriteString("rootName", modelRoot.name);
            writer.WriteString("capturedUtc", DateTime.UtcNow);
            writer.WriteString("coreVersion", typeof(RigReference).Assembly.GetName().Version?.ToString());
            writer.WriteStartArray("nodes");
            for (int i = 0; i < nodes.Count; i++)
            {
                writer.WriteStartObject(); writer.WriteString("name", nodes[i].name);
                writer.WriteString("path", paths[i]); writer.WriteNumber("parent", parents[i]);
                writer.WritePropertyName("poseMatrix"); RigReferenceValidation.Array(writer, Matrix(rootInverse * nodes[i].localToWorldMatrix));
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteStartArray("meshes");
            foreach (var mesh in meshes)
            {
                writer.WriteStartObject(); writer.WriteString("name", mesh.Name);
                writer.WriteNumber("rendererNode", mesh.RendererNode);
                writer.WriteNumber("rootBone", mesh.RootBone);
                writer.WritePropertyName("bones"); RigReferenceValidation.Array(writer, mesh.Bones);
                writer.WritePropertyName("posedVerticesRoot"); RigReferenceValidation.Vectors(writer, mesh.Vertices);
                writer.WritePropertyName("triangles"); RigReferenceValidation.Array(writer, mesh.Triangles);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return new PoseSurfaceSnapshot(Encoding.UTF8.GetString(output.ToArray()), meshes.Count, totalVertices, nodes.Count);
    }
}
#endif
