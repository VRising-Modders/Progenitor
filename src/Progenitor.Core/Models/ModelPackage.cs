using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace Progenitor.Models;

/// <summary>A versioned, integrity-checked directory containing one static GLB model.</summary>
public static class ModelPackage
{
    private const int MaxManifestBytes = 64 * 1024;
    private const int MaxModelBytes = 256 * 1024 * 1024;

    /// <summary>Validates and publishes a package into a fresh directory. Existing outputs are never replaced.</summary>
    public static void Build(string glbPath, string outputDirectory, string assetId) => Build(glbPath, outputDirectory, assetId, false);

    /// <summary>Publishes a schema-2 animated model with its own authored node hierarchy and clips.</summary>
    public static void BuildAnimated(string glbPath, string outputDirectory, string assetId) => Build(glbPath, outputDirectory, assetId, true);

    private static void Build(string glbPath, string outputDirectory, string assetId, bool animated)
    {
        ValidateId(assetId);
        string output = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(output) || File.Exists(output))
            throw new IOException("Model package output already exists: " + output);
        byte[] bytes = ReadBounded(glbPath, MaxModelBytes, "model GLB");
        ParseModel(bytes, animated);
        string parent = Path.GetDirectoryName(output) ?? throw new ArgumentException("Package output must have a parent directory.", nameof(outputDirectory));
        Directory.CreateDirectory(parent);
        RejectLinks(parent);
        string staging = Path.Combine(parent, ".model-package-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            File.WriteAllBytes(Path.Combine(staging, "model.glb"), bytes);
            using (var stream = File.Create(Path.Combine(staging, "manifest.json")))
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                writer.WriteNumber("schemaVersion", animated ? 2 : 1);
                writer.WriteString("assetId", assetId);
                writer.WriteString("kind", animated ? "animated-model" : "static-model");
                writer.WriteString("model", "model.glb");
                writer.WriteString("sha256", Hash(bytes));
                writer.WriteEndObject();
            }
            Directory.Move(staging, output);
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
        }
    }

    /// <summary>Validates the manifest and hash before parsing the exact model bytes checked.</summary>
    public static GltfScene Load(string packageDirectory)
    {
        string directory = Path.GetFullPath(packageDirectory);
        RejectLinks(directory);
        byte[] manifest = ReadBounded(Path.Combine(directory, "manifest.json"), MaxManifestBytes, "package manifest");
        try
        {
            using var doc = JsonDocument.Parse(manifest);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new FormatException("Model package manifest must be an object.");
            var names = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
                if (!names.Add(property.Name)) throw new FormatException("Model package manifest repeats field '" + property.Name + "'.");
            if (!root.TryGetProperty("schemaVersion", out var version) || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out int number) || (number != 1 && number != 2))
                throw new FormatException("Model package schemaVersion must be 1 (static) or 2 (animated).");
            bool animated = number == 2;
            if (StringField(root, "kind") != (animated ? "animated-model" : "static-model"))
                throw new FormatException("Model package kind does not match schemaVersion: 1 requires static-model, 2 requires animated-model.");
            ValidateId(StringField(root, "assetId"));
            if (StringField(root, "model") != "model.glb") throw new FormatException("Model package model must be exactly 'model.glb'.");
            string expected = StringField(root, "sha256");
            if (expected.Length != 64 || !IsHex(expected)) throw new FormatException("Model package sha256 must contain exactly 64 hexadecimal characters.");
            byte[] bytes = ReadBounded(Path.Combine(directory, "model.glb"), MaxModelBytes, "package model");
            if (!string.Equals(expected, Hash(bytes), StringComparison.OrdinalIgnoreCase))
                throw new FormatException("Model package model.glb SHA256 mismatch; rebuild the package from its original source.");
            return ParseModel(bytes, animated);
        }
        catch (JsonException ex) { throw new FormatException("Model package manifest is invalid JSON.", ex); }
        catch (ArgumentException ex) { throw new FormatException("Model package assetId is invalid.", ex); }
    }

    private static string StringField(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
            throw new FormatException("Model package manifest requires string field '" + name + "'.");
        return value.GetString()!;
    }

    private static void ValidateId(string id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > 128 || id == "." || id == "..")
            throw new ArgumentException("Asset ID must contain 1–128 ASCII letters, digits, dots, underscores or hyphens.", nameof(id));
        foreach (char c in id)
            if (!(c >= 'a' && c <= 'z' || c >= 'A' && c <= 'Z' || c >= '0' && c <= '9' || c == '.' || c == '_' || c == '-'))
                throw new ArgumentException("Asset ID must contain only ASCII letters, digits, dots, underscores or hyphens.", nameof(id));
    }

    private static bool IsHex(string value)
    {
        foreach (char c in value)
            if (!(c >= '0' && c <= '9' || c >= 'a' && c <= 'f' || c >= 'A' && c <= 'F')) return false;
        return true;
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static GltfScene ParseModel(byte[] bytes, bool animated)
    {
        GltfScene scene = GltfScene.ParseGlb(bytes);
        if (!animated && (scene.Animations.Count > 0 || scene.Skins.Count > 0))
            throw new FormatException("Static model package cannot contain skins or animations; use BuildAnimated.");
        if (animated && scene.Animations.Count == 0)
            throw new FormatException("Animated model package requires at least one animation clip.");
        var pending = new System.Collections.Generic.Stack<int>(scene.RootNodes);
        var visited = new System.Collections.Generic.HashSet<int>();
        while (pending.Count > 0)
        {
            int index = pending.Pop();
            if (!visited.Add(index)) continue;
            var node = scene.Nodes[index];
            if (node.Mesh >= 0)
                foreach (var primitive in scene.Meshes[node.Mesh].Primitives)
                    if (primitive.Positions.Length >= 9 && primitive.Indices.Length >= 3) return scene;
            foreach (int child in node.Children) pending.Push(child);
        }
        throw new FormatException("Static model package requires at least one triangle mesh reachable from the selected scene roots.");
    }

    private static byte[] ReadBounded(string path, int maxBytes, string description)
    {
        RejectLinks(Path.GetFullPath(path));
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > maxBytes) throw new FormatException("The " + description + " exceeds the " + maxBytes + " byte limit.");
        byte[] bytes = new byte[checked((int)stream.Length)];
        int offset = 0;
        while (offset < bytes.Length)
        {
            int read = stream.Read(bytes, offset, bytes.Length - offset);
            if (read == 0) throw new EndOfStreamException("The " + description + " was truncated while reading.");
            offset += read;
        }
        return bytes;
    }

    private static void RejectLinks(string path)
    {
        for (string? at = path; at != null; at = Path.GetDirectoryName(at))
            if ((File.GetAttributes(at) & FileAttributes.ReparsePoint) != 0)
                throw new FormatException("Model packages cannot read symbolic links or junctions: " + at);
    }
}
