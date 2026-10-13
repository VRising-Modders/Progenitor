using System;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Progenitor.Models;

/// <summary>A detached authoring reference; contains no Unity objects or live callbacks.</summary>
public sealed class RigReferenceSnapshot
{
    internal RigReferenceSnapshot(string json, int meshCount, int vertexCount, int jointCount)
    { Json = json; MeshCount = meshCount; VertexCount = vertexCount; JointCount = jointCount; }

    public string Json { get; }
    public int MeshCount { get; }
    public int VertexCount { get; }
    public int JointCount { get; }

    /// <summary>Writes a new UTF-8 JSON file. Never replaces an existing capture.</summary>
    public void Save(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A capture path is required.", nameof(path));
        using var file = new FileStream(Path.GetFullPath(path), FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new StreamWriter(file, new UTF8Encoding(false));
        writer.Write(Json);
    }
}

internal static class RigReferenceValidation
{
    internal static void Mesh(float[][] vertices, int[] triangles, int boneCount, int[][] indices, float[][] weights)
    {
        if (vertices.Length == 0 || vertices.Length > 2000000) throw new InvalidDataException("Reference mesh must contain 1..2000000 vertices.");
        if (triangles.Length == 0 || triangles.Length % 3 != 0) throw new InvalidDataException("Reference requires complete triangle topology.");
        foreach (var point in vertices) Vector(point, 3);
        foreach (int index in triangles)
            if (index < 0 || index >= vertices.Length) throw new InvalidDataException("Triangle index is outside the vertex array.");
        if (boneCount == 0 || indices.Length != vertices.Length || weights.Length != vertices.Length)
            throw new InvalidDataException("A complete skin is required for every vertex.");
        for (int v = 0; v < vertices.Length; v++)
        {
            if (indices[v].Length != 4) throw new InvalidDataException("Expected four skin indices.");
            Vector(weights[v], 4);
            float total = 0;
            for (int j = 0; j < 4; j++)
            {
                float weight = weights[v][j];
                if (weight < 0 || (weight > 0 && (indices[v][j] < 0 || indices[v][j] >= boneCount)))
                    throw new InvalidDataException("Invalid skin influence.");
                total += weight;
            }
            if (MathF.Abs(total - 1f) > .0001f) throw new InvalidDataException("Skin weights must sum to one; export does not repair them silently.");
        }
    }

    internal static void Vector(float[] values, int length)
    {
        if (values.Length != length) throw new InvalidDataException("Invalid reference vector length.");
        foreach (float value in values)
            if (!float.IsFinite(value)) throw new InvalidDataException("Reference contains a non-finite value.");
    }

    internal static void Array(Utf8JsonWriter writer, float[] values)
    { writer.WriteStartArray(); foreach (float value in values) writer.WriteNumberValue(value); writer.WriteEndArray(); }
    internal static void Array(Utf8JsonWriter writer, int[] values)
    { writer.WriteStartArray(); foreach (int value in values) writer.WriteNumberValue(value); writer.WriteEndArray(); }
    internal static void Vectors(Utf8JsonWriter writer, float[][] values)
    { writer.WriteStartArray(); foreach (var value in values) Array(writer, value); writer.WriteEndArray(); }
}
