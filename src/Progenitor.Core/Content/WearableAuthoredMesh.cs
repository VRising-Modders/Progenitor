using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Progenitor.Content;

/// <summary>Validated, original, bind-space garment data. Runtime binding refuses a different skeleton rest shape.</summary>
internal sealed class WearableAuthoredMesh
{
    internal WearableMesh Mesh = null!;
    internal string[] Paths = Array.Empty<string>();
    internal float[][] Rest = Array.Empty<float[]>();
    internal int[][] Indices = Array.Empty<int[]>();
    internal float[][] Weights = Array.Empty<float[]>();
    internal WearableVec3[] Normals = Array.Empty<WearableVec3>();
    internal float[][] Uv = Array.Empty<float[]>();
    private readonly Dictionary<string, (float R, float G, float B, float A)> _surfaceColors = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _surfacePalette = new(StringComparer.Ordinal);
    internal WearableSlot? PaletteSource { get; private set; }
    internal WearablePaletteMode PaletteMode { get; private set; }

    // Detached value tuples prevent callers from mutating validated channels. These
    // are direct shader RGBA inputs, without implicit gamma/linear conversion.
    internal bool TrySurfaceColor(string surface, out (float R, float G, float B, float A) color) =>
        _surfaceColors.TryGetValue(surface, out color);

    internal bool HasSurfacePalette => _surfacePalette.Count != 0;

    internal bool TrySurfacePalette(string surface, out string channel) =>
        _surfacePalette.TryGetValue(surface, out channel!);

    internal bool Matches(IReadOnlyDictionary<string, float[]> rest)
    {
        for (int i = 0; i < Mesh.Bones.Length; i++)
        {
            if (!rest.TryGetValue(Mesh.Bones[i], out var actual) || actual.Length != 16) return false;
            for (int j = 0; j < 16; j++)
                if (!float.IsFinite(actual[j]) || Math.Abs(actual[j]-Rest[i][j]) > .0001f) return false;
        }
        return true;
    }

    internal static WearableAuthoredMesh Parse(string json)
    {
        if (json == null) throw new ArgumentNullException(nameof(json));
        if (json.Length > 64 * 1024 * 1024) throw new InvalidDataException("Garment exceeds 64 MiB limit.");
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
        var root = doc.RootElement;
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (!keys.Add(property.Name)) throw new InvalidDataException("Duplicate garment property: " + property.Name);
            switch (property.Name)
            {
                case "schemaVersion": case "coordinateSystem": case "matrixLayout":
                case "vertices": case "normals": case "uv": case "bones": case "weights":
                case "cloth": case "gold": case "void": case "surfaceColors":
                case "surfacePalette": case "paletteSource": case "paletteMode":
                    break;
                default:
                    throw new InvalidDataException("Unknown garment property: " + property.Name);
            }
        }
        if (root.GetProperty("schemaVersion").GetInt32() != 1 || root.GetProperty("coordinateSystem").GetString() != "unity-left-handed-y-up"
            || root.GetProperty("matrixLayout").GetString() != "row-major") throw new InvalidDataException("Unsupported garment schema.");
        float[] Vector(JsonElement element, int count)
        {
            var value = element.EnumerateArray().Select(x => x.GetSingle()).ToArray();
            if (value.Length != count || value.Any(x => !float.IsFinite(x))) throw new InvalidDataException("Invalid finite vector.");
            return value;
        }
        var points = root.GetProperty("vertices").EnumerateArray().Select(v => Vector(v,3)).ToArray();
        if (points.Length == 0 || points.Length > 60000) throw new InvalidDataException("Expected 1..60000 garment vertices.");
        var result = new WearableAuthoredMesh();
        if (root.TryGetProperty("paletteSource", out var source))
        {
            if (source.ValueKind != JsonValueKind.String
                || !Enum.TryParse<WearableSlot>(source.GetString(), out var parsed)
                || !Enum.IsDefined(typeof(WearableSlot), parsed)
                || !string.Equals(source.GetString(), parsed.ToString(), StringComparison.Ordinal))
                throw new InvalidDataException("paletteSource must name a WearableSlot.");
            result.PaletteSource = parsed;
        }
        if (root.TryGetProperty("paletteMode", out var mode))
        {
            if (mode.ValueKind != JsonValueKind.String) throw new InvalidDataException("paletteMode must be native or authored.");
            result.PaletteMode = mode.GetString() switch
            {
                "native" => WearablePaletteMode.Native,
                "authored" => WearablePaletteMode.Authored,
                _ => throw new InvalidDataException("paletteMode must be native or authored."),
            };
        }
        if (root.TryGetProperty("surfaceColors", out var colors))
        {
            if (colors.ValueKind != JsonValueKind.Object) throw new InvalidDataException("surfaceColors must be an object.");
            foreach (var property in colors.EnumerateObject())
            {
                if (property.Name != "cloth" && property.Name != "gold" && property.Name != "void")
                    throw new InvalidDataException("Unknown wearable surface color: " + property.Name);
                var channels = property.Value;
                if (channels.ValueKind != JsonValueKind.Array || channels.GetArrayLength() != 4)
                    throw new InvalidDataException("Surface colors require four RGBA channels.");
                var rgba = new float[4];
                int channel = 0;
                foreach (var value in channels.EnumerateArray())
                {
                    if (value.ValueKind != JsonValueKind.Number || !value.TryGetSingle(out float number)
                        || !float.IsFinite(number) || number < 0 || number > 1)
                        throw new InvalidDataException("Surface color channels must be finite numbers in [0,1].");
                    rgba[channel++] = number;
                }
                if (!result._surfaceColors.TryAdd(property.Name, (rgba[0],rgba[1],rgba[2],rgba[3])))
                    throw new InvalidDataException("Duplicate wearable surface color: " + property.Name);
            }
        }
        if (root.EnumerateObject().Count(p => p.Name == "surfacePalette") > 1)
            throw new InvalidDataException("Duplicate surfacePalette property.");
        if (root.TryGetProperty("surfacePalette", out var palette))
        {
            if (palette.ValueKind != JsonValueKind.Object) throw new InvalidDataException("surfacePalette must be an object.");
            foreach (var property in palette.EnumerateObject())
            {
                if (property.Name != "cloth" && property.Name != "gold" && property.Name != "void")
                    throw new InvalidDataException("Unknown wearable palette surface: " + property.Name);
                if (property.Value.ValueKind != JsonValueKind.String)
                    throw new InvalidDataException("Wearable palette channel must be a string.");
                var channel = property.Value.GetString()!;
                if (channel != "primary" && channel != "accent" && channel != "neutral" && channel != "metal")
                    throw new InvalidDataException("Unknown wearable palette channel: " + channel);
                if (!result._surfacePalette.TryAdd(property.Name, channel))
                    throw new InvalidDataException("Duplicate wearable palette surface: " + property.Name);
            }
        }
        var bones = root.GetProperty("bones").EnumerateArray().ToArray();
        if (bones.Length == 0 || bones.Length > 512) throw new InvalidDataException("Expected 1..512 bones.");
        var names = bones.Select(b => b.GetProperty("name").GetString()!).ToArray();
        result.Paths = bones.Select(b => b.GetProperty("path").GetString()!).ToArray();
        if (names.Any(string.IsNullOrWhiteSpace) || result.Paths.Any(string.IsNullOrWhiteSpace)
            || names.Distinct(StringComparer.Ordinal).Count() != names.Length || result.Paths.Distinct(StringComparer.Ordinal).Count() != names.Length)
            throw new InvalidDataException("Bones require unique names and source paths.");
        result.Rest = bones.Select(b => Vector(b.GetProperty("restMatrix"),16)).ToArray();
        foreach (var m in result.Rest)
        {
            var matrix = new System.Numerics.Matrix4x4(m[0],m[1],m[2],m[3],m[4],m[5],m[6],m[7],m[8],m[9],m[10],m[11],m[12],m[13],m[14],m[15]);
            if (!float.IsFinite(matrix.GetDeterminant()) || Math.Abs(matrix.GetDeterminant()) < 1e-8f
                || Math.Abs(m[12])+Math.Abs(m[13])+Math.Abs(m[14])+Math.Abs(m[15]-1) > 1e-5f)
                throw new InvalidDataException("Expected nonsingular affine rest matrices.");
        }
        var skin = root.GetProperty("weights").EnumerateArray().ToArray();
        result.Indices = skin.Select(w => w.GetProperty("indices").EnumerateArray().Select(i => i.GetInt32()).ToArray()).ToArray();
        result.Weights = skin.Select(w => Vector(w.GetProperty("values"),4)).ToArray();
        if (skin.Length != points.Length) throw new InvalidDataException("Missing vertex weights.");
        for (int i=0;i<skin.Length;i++)
        {
            if (result.Indices[i].Length != 4 || result.Weights[i].Any(w => w < 0) || Math.Abs(result.Weights[i].Sum()-1) > .0001f)
                throw new InvalidDataException("Expected four normalized nonnegative weights.");
            for (int k=0;k<4;k++) if (result.Indices[i][k] < 0 || result.Indices[i][k] >= bones.Length) throw new InvalidDataException("Invalid bone index.");
        }
        int[] Triangles(string key)
        {
            var a=root.GetProperty(key).EnumerateArray().Select(t=>t.GetInt32()).ToArray();
            if (a.Length % 3 != 0 || a.Any(i=>i<0 || i>=points.Length)) throw new InvalidDataException("Invalid triangles.");
            return a;
        }
        var cloth=Triangles("cloth"); var gold=Triangles("gold"); var dark=Triangles("void");
        if (cloth.Length+gold.Length+dark.Length == 0) throw new InvalidDataException("Garment has no triangles.");
        result.Normals = root.GetProperty("normals").EnumerateArray().Select(v=>Vector(v,3)).Select(v=>new WearableVec3(v[0],v[1],v[2])).ToArray();
        result.Uv = root.GetProperty("uv").EnumerateArray().Select(v=>Vector(v,2)).ToArray();
        if (result.Normals.Length != points.Length || result.Uv.Length != points.Length || result.Normals.Any(n=>Math.Abs(n.Length()-1)>.001f))
            throw new InvalidDataException("Expected unit normals and UVs per vertex.");
        result.Mesh = new WearableMesh(points.Select(v=>new WearableVec3(v[0],v[1],v[2])).ToArray(), result.Indices.Select(i=>i[0]).ToArray(),
            new int[points.Length],new float[points.Length],names,cloth,gold,dark,Array.Empty<string>()) { Authored = result };
        return result;
    }
}
