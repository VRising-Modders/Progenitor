using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Progenitor.Models;

/// <summary>
/// A binary glTF 2.0 file (<c>.glb</c>) read into plain arrays, already converted to Unity's
/// left-handed space: X is negated on positions, normals and translations, rotations are mirrored
/// to (x, -y, -z, w), triangle winding is reversed, and texture v is flipped (Unity's v origin is
/// the bottom). No Unity types, so the parser is unit-tested; <c>GltfBuilder</c> turns a scene into
/// GameObjects. Parsed with <see cref="JsonDocument"/> only (no serializer type caches).
///
/// <para>Supported: nodes (TRS or matrix) with children; meshes with triangle primitives
/// (POSITION, NORMAL, TEXCOORD_0, optional indices; float vertex data; u8/u16/u32 indices);
/// four joint influences per vertex, skin inverse binds, LINEAR/STEP translation/rotation/scale animations;
/// metallic-roughness materials (base color factor and texture, metallic, roughness, emissive
/// factor and texture, alpha mode, double-sided, <c>KHR_materials_unlit</c>,
/// <c>KHR_materials_emissive_strength</c>); textures from images embedded in the binary chunk. Not
/// supported: morph targets, cubic spline animation, sparse accessors, external .bin or image files,
/// normalized integer vertex data other than skin weights; the parser throws <see cref="FormatException"/> on the first
/// feature it cannot read rather than importing it wrong.</para>
/// </summary>
public sealed class GltfScene
{
    public List<GltfNode> Nodes { get; } = new();
    public List<int> RootNodes { get; } = new();
    public List<GltfMesh> Meshes { get; } = new();
    public List<GltfMaterial> Materials { get; } = new();
    public List<GltfImage> Images { get; } = new();
    public List<GltfSkin> Skins { get; } = new();
    public List<GltfAnimation> Animations { get; } = new();

    /// <summary>Texture index → image index (samplers are not carried; textures repeat, bilinear).</summary>
    public List<int> TextureImages { get; } = new();

    private const uint Magic = 0x46546C67; // "glTF"
    private const uint ChunkJson = 0x4E4F534A; // "JSON"
    private const uint ChunkBin = 0x004E4942; // "BIN\0"

    public static GltfScene LoadGlb(string path) => ParseGlb(File.ReadAllBytes(path));

    public static GltfScene ParseGlb(byte[] glb)
    {
        if (glb == null || glb.Length < 20) throw new FormatException("not a GLB: too short.");
        if (BitConverter.ToUInt32(glb, 0) != Magic) throw new FormatException("not a GLB: bad magic.");
        uint version = BitConverter.ToUInt32(glb, 4);
        if (version != 2) throw new FormatException("GLB version " + version + " is not supported (2 is).");
        uint declaredTotal = BitConverter.ToUInt32(glb, 8);
        if (declaredTotal != (uint)glb.Length) throw new FormatException("GLB length field does not match the file.");
        int total = glb.Length;

        string? json = null;
        ArraySegment<byte> bin = default;
        int at = 12;
        while (at < total)
        {
            if (total - at < 8) throw new FormatException("GLB has an incomplete chunk header.");
            uint declaredLength = BitConverter.ToUInt32(glb, at);
            uint type = BitConverter.ToUInt32(glb, at + 4);
            if (declaredLength > (uint)(total - at - 8)) throw new FormatException("GLB chunk runs past the end of the file.");
            if (declaredLength % 4 != 0) throw new FormatException("GLB chunk length is not aligned to four bytes.");
            int len = (int)declaredLength;
            if (at == 12 && type != ChunkJson) throw new FormatException("GLB first chunk must be JSON.");
            if (type == ChunkJson)
            {
                if (json != null) throw new FormatException("GLB has duplicate JSON chunks.");
                json = Encoding.UTF8.GetString(glb, at + 8, len);
            }
            else if (type == ChunkBin)
            {
                if (bin.Array != null) throw new FormatException("GLB has duplicate binary chunks.");
                bin = new ArraySegment<byte>(glb, at + 8, len);
            }
            at += 8 + len;
        }
        if (json == null) throw new FormatException("GLB has no JSON chunk.");
        return Parse(json, bin);
    }

    internal static GltfScene Parse(string json, ArraySegment<byte> bin)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        foreach (var extension in Array(root, "extensionsRequired"))
        {
            string? name = extension.ValueKind == JsonValueKind.String ? extension.GetString() : null;
            if (name != "KHR_materials_unlit" && name != "KHR_materials_emissive_strength")
                throw new FormatException("required extension '" + name + "' is not supported.");
        }
        var reader = new Reader(root, bin);
        var scene = new GltfScene();

        foreach (var img in Array(root, "images"))
        {
            if (img.TryGetProperty("uri", out _))
                throw new FormatException("external image files are not supported; embed images in the GLB.");
            int view = Int(img, "bufferView", -1);
            if (view < 0) throw new FormatException("image without a bufferView.");
            scene.Images.Add(new GltfImage(Str(img, "name") ?? "", Str(img, "mimeType") ?? "", reader.View(view)));
        }

        foreach (var tex in Array(root, "textures"))
            scene.TextureImages.Add(Int(tex, "source", -1));

        foreach (var m in Array(root, "materials"))
            scene.Materials.Add(ReadMaterial(m));

        foreach (var mesh in Array(root, "meshes"))
        {
            if (mesh.TryGetProperty("weights", out _)) throw new FormatException("morph weights are not supported.");
            var gm = new GltfMesh(Str(mesh, "name") ?? "mesh" + scene.Meshes.Count);
            foreach (var prim in Array(mesh, "primitives"))
                gm.Primitives.Add(reader.Primitive(prim));
            scene.Meshes.Add(gm);
        }

        foreach (var n in Array(root, "nodes"))
            scene.Nodes.Add(ReadNode(n, scene.Nodes.Count));
        ValidateNodes(scene);

        int sceneIndex = Int(root, "scene", 0);
        var scenes = Array(root, "scenes");
        if (scenes.Count > 0)
        {
            if (sceneIndex < 0 || sceneIndex >= scenes.Count) throw new FormatException("default scene " + sceneIndex + " does not exist.");
            foreach (var r in Array(scenes[sceneIndex], "nodes")) scene.RootNodes.Add(r.GetInt32());
        }
        else
        {
            // No scene list: every node that is nobody's child is a root.
            var children = new HashSet<int>();
            foreach (var node in scene.Nodes) foreach (var c in node.Children) children.Add(c);
            for (int i = 0; i < scene.Nodes.Count; i++) if (!children.Contains(i)) scene.RootNodes.Add(i);
        }

        foreach (int r in scene.RootNodes)
            if (r < 0 || r >= scene.Nodes.Count) throw new FormatException("root node " + r + " does not exist.");
        ValidateReferences(scene);
        reader.ReadSkinsAndAnimations(scene);
        ValidateAnimatedReferences(scene);
        return scene;
    }

    private static GltfNode ReadNode(JsonElement n, int index)
    {
        var node = new GltfNode(Str(n, "name") ?? "node" + index) { Mesh = Int(n, "mesh", -1), Skin = Int(n, "skin", -1) };
        if (n.TryGetProperty("weights", out _)) throw new FormatException("morph weights are not supported.");
        foreach (var c in Array(n, "children")) node.Children.Add(c.GetInt32());

        float[] t = { 0, 0, 0 }, r = { 0, 0, 0, 1 }, s = { 1, 1, 1 };
        if (n.TryGetProperty("matrix", out var mat))
        {
            node.HasMatrix = true;
            var m = Floats(mat, 16);
            Decompose(m, t, r, s);
        }
        else
        {
            if (n.TryGetProperty("translation", out var tr)) t = Floats(tr, 3);
            if (n.TryGetProperty("rotation", out var ro)) r = Floats(ro, 4);
            if (n.TryGetProperty("scale", out var sc)) s = Floats(sc, 3);
        }

        // Right-handed to left-handed by mirroring X.
        foreach (float value in t) RequireFinite(value);
        foreach (float value in r) RequireFinite(value);
        foreach (float value in s) RequireFinite(value);
        node.Translation = new[] { -t[0], t[1], t[2] };
        node.Rotation = new[] { r[0], -r[1], -r[2], r[3] };
        node.Scale = s;
        return node;
    }

    private static void ValidateNodes(GltfScene scene)
    {
        var parents = new int[scene.Nodes.Count];
        foreach (var node in scene.Nodes)
        {
            if (node.Mesh < -1 || node.Mesh >= scene.Meshes.Count) throw new FormatException("node mesh " + node.Mesh + " does not exist.");
            foreach (int child in node.Children)
            {
                if (child < 0 || child >= scene.Nodes.Count) throw new FormatException("child node " + child + " does not exist.");
                if (++parents[child] > 1) throw new FormatException("node " + child + " has multiple parents or duplicate child references.");
            }
        }
        // Remove roots iteratively, so even an adversarially deep hierarchy needs no recursion.
        var pending = new Queue<int>();
        var depths = new int[parents.Length];
        for (int i = 0; i < parents.Length; i++) if (parents[i] == 0) pending.Enqueue(i);
        int visited = 0;
        while (pending.Count > 0)
        {
            int index = pending.Dequeue();
            visited++;
            foreach (int child in scene.Nodes[index].Children)
            {
                depths[child] = depths[index] + 1;
                if (depths[child] > 64) throw new FormatException("node hierarchy deeper than 64 is not supported.");
                if (--parents[child] == 0) pending.Enqueue(child);
            }
        }
        if (visited != scene.Nodes.Count) throw new FormatException("node hierarchy contains a cycle.");
    }

    private static void ValidateReferences(GltfScene scene)
    {
        foreach (int image in scene.TextureImages)
            if (image < 0 || image >= scene.Images.Count) throw new FormatException("texture image " + image + " does not exist.");
        foreach (var material in scene.Materials)
        {
            ValidateOptionalReference(material.BaseColorTexture, scene.TextureImages.Count, "base color texture");
            ValidateOptionalReference(material.EmissiveTexture, scene.TextureImages.Count, "emissive texture");
        }
        foreach (var mesh in scene.Meshes)
            foreach (var primitive in mesh.Primitives)
                ValidateOptionalReference(primitive.Material, scene.Materials.Count, "primitive material");
        var roots = new HashSet<int>();
        var children = new HashSet<int>();
        foreach (var node in scene.Nodes) foreach (int child in node.Children) children.Add(child);
        foreach (int root in scene.RootNodes)
            if (!roots.Add(root) || children.Contains(root)) throw new FormatException("scene roots must be unique unparented nodes.");
    }

    private static void ValidateAnimatedReferences(GltfScene scene)
    {
        if (scene.Skins.Count > 0 || scene.Animations.Count > 0)
        {
            foreach (var node in scene.Nodes)
            {
                var rotation = node.Rotation;
                double length = Math.Sqrt((double)rotation[0] * rotation[0] + (double)rotation[1] * rotation[1] +
                    (double)rotation[2] * rotation[2] + (double)rotation[3] * rotation[3]);
                if (length == 0) throw new FormatException("animated/skinned node rest rotation quaternion must be nonzero.");
                for (int k = 0; k < 4; k++) rotation[k] = (float)(rotation[k] / length);
            }
        }
        var active = new HashSet<int>();
        var queue = new Queue<int>(scene.RootNodes);
        while (queue.Count > 0)
        {
            int node = queue.Dequeue();
            if (!active.Add(node)) continue;
            foreach (int child in scene.Nodes[node].Children) queue.Enqueue(child);
        }
        var parents = new int[scene.Nodes.Count];
        for (int i = 0; i < parents.Length; i++) parents[i] = -1;
        for (int i = 0; i < scene.Nodes.Count; i++) foreach (int child in scene.Nodes[i].Children) parents[child] = i;
        foreach (var skin in scene.Skins)
        {
            var unique = new HashSet<int>();
            foreach (int joint in skin.Joints)
                if (joint < 0 || joint >= scene.Nodes.Count || !unique.Add(joint)) throw new FormatException("skins contain invalid or duplicate joint nodes.");
            int commonTree = -1;
            foreach (int joint in skin.Joints)
            {
                int tree = joint;
                while (parents[tree] >= 0) tree = parents[tree];
                if (commonTree >= 0 && tree != commonTree) throw new FormatException("skin joints must have a common root.");
                commonTree = tree;
            }
            ValidateOptionalReference(skin.Skeleton, scene.Nodes.Count, "skin skeleton");
            if (skin.Skeleton >= 0)
            {
                var descendants = new HashSet<int>();
                var pending = new Queue<int>(); pending.Enqueue(skin.Skeleton);
                while (pending.Count > 0) { int n = pending.Dequeue(); descendants.Add(n); foreach (int c in scene.Nodes[n].Children) pending.Enqueue(c); }
                foreach (int joint in skin.Joints) if (!descendants.Contains(joint)) throw new FormatException("skin skeleton must be an ancestor of every joint.");
            }
        }
        for (int i = 0; i < scene.Nodes.Count; i++)
        {
            var node = scene.Nodes[i];
            ValidateOptionalReference(node.Skin, scene.Skins.Count, "node skin");
            if (node.Skin < 0) continue;
            if (node.Mesh < 0) throw new FormatException("skinned node must reference a mesh.");
            var skin = scene.Skins[node.Skin];
            foreach (var primitive in scene.Meshes[node.Mesh].Primitives)
            {
                if (primitive.Joints == null || primitive.Weights == null) throw new FormatException("skinned mesh requires JOINTS_0 and WEIGHTS_0.");
                foreach (int joint in primitive.Joints) if (joint >= skin.Joints.Length) throw new FormatException("vertex joint index exceeds skin joints.");
            }
            if (active.Contains(i))
            {
                foreach (int joint in skin.Joints) if (!active.Contains(joint)) throw new FormatException("skin joint is outside the selected scene.");
                if (skin.Skeleton >= 0 && !active.Contains(skin.Skeleton)) throw new FormatException("skin skeleton is outside the selected scene.");
            }
        }
        foreach (var animation in scene.Animations)
            foreach (var channel in animation.Channels)
            {
                if (channel.Node < 0 || channel.Node >= scene.Nodes.Count) throw new FormatException("animation target node does not exist.");
                if (!active.Contains(channel.Node)) throw new FormatException("animation target is outside the selected scene.");
                if (scene.Nodes[channel.Node].HasMatrix) throw new FormatException("animation cannot target a matrix node; use TRS.");
            }
    }

    private static void ValidateOptionalReference(int index, int count, string kind)
    {
        if (index < -1 || index >= count) throw new FormatException(kind + " " + index + " does not exist.");
    }

    private static void RequireFinite(float value)
    {
        if (float.IsNaN(value) || float.IsInfinity(value)) throw new FormatException("non-finite model data is not supported.");
    }

    /// <summary>Column-major TRS matrix (no shear) into translation, rotation quaternion and scale.</summary>
    internal static void Decompose(float[] m, float[] t, float[] r, float[] s)
    {
        t[0] = m[12]; t[1] = m[13]; t[2] = m[14];
        float sx = Len(m[0], m[1], m[2]), sy = Len(m[4], m[5], m[6]), sz = Len(m[8], m[9], m[10]);
        if (sx == 0 || sy == 0 || sz == 0)
            throw new FormatException("zero-scale node matrices are not supported; use TRS or apply transforms before export.");
        s[0] = sx; s[1] = sy; s[2] = sz;
        float r00 = m[0] / sx, r10 = m[1] / sx, r20 = m[2] / sx;
        float r01 = m[4] / sy, r11 = m[5] / sy, r21 = m[6] / sy;
        float r02 = m[8] / sz, r12 = m[9] / sz, r22 = m[10] / sz;
        float determinant = r00 * (r11 * r22 - r12 * r21) - r01 * (r10 * r22 - r12 * r20) + r02 * (r10 * r21 - r11 * r20);
        if (determinant < 0)
            throw new FormatException("reflected node matrices are not supported; use TRS or apply transforms before export.");
        const float tolerance = 0.0001f;
        if (Math.Abs(r00 * r01 + r10 * r11 + r20 * r21) > tolerance ||
            Math.Abs(r00 * r02 + r10 * r12 + r20 * r22) > tolerance ||
            Math.Abs(r01 * r02 + r11 * r12 + r21 * r22) > tolerance ||
            Math.Abs(m[3]) > tolerance || Math.Abs(m[7]) > tolerance || Math.Abs(m[11]) > tolerance || Math.Abs(m[15] - 1) > tolerance)
            throw new FormatException("sheared or non-affine node matrices are not supported; use TRS or apply transforms before export.");
        float trace = r00 + r11 + r22;
        if (trace > 0)
        {
            float k = (float)Math.Sqrt(trace + 1) * 2;
            r[3] = 0.25f * k; r[0] = (r21 - r12) / k; r[1] = (r02 - r20) / k; r[2] = (r10 - r01) / k;
        }
        else if (r00 > r11 && r00 > r22)
        {
            float k = (float)Math.Sqrt(1 + r00 - r11 - r22) * 2;
            r[3] = (r21 - r12) / k; r[0] = 0.25f * k; r[1] = (r01 + r10) / k; r[2] = (r02 + r20) / k;
        }
        else if (r11 > r22)
        {
            float k = (float)Math.Sqrt(1 + r11 - r00 - r22) * 2;
            r[3] = (r02 - r20) / k; r[0] = (r01 + r10) / k; r[1] = 0.25f * k; r[2] = (r12 + r21) / k;
        }
        else
        {
            float k = (float)Math.Sqrt(1 + r22 - r00 - r11) * 2;
            r[3] = (r10 - r01) / k; r[0] = (r02 + r20) / k; r[1] = (r12 + r21) / k; r[2] = 0.25f * k;
        }
    }

    private static float Len(float x, float y, float z) => (float)Math.Sqrt(x * x + y * y + z * z);

    private static GltfMaterial ReadMaterial(JsonElement m)
    {
        var mat = new GltfMaterial(Str(m, "name") ?? "material");
        if (m.TryGetProperty("pbrMetallicRoughness", out var pbr))
        {
            if (pbr.TryGetProperty("baseColorFactor", out var bc)) mat.BaseColor = Floats(bc, 4);
            if (pbr.TryGetProperty("baseColorTexture", out var bt)) mat.BaseColorTexture = TextureIndex(bt);
            mat.Metallic = Float(pbr, "metallicFactor", 1f);
            mat.Roughness = Float(pbr, "roughnessFactor", 1f);
        }
        if (m.TryGetProperty("emissiveFactor", out var ef)) mat.Emissive = Floats(ef, 3);
        if (m.TryGetProperty("emissiveTexture", out var et)) mat.EmissiveTexture = TextureIndex(et);
        mat.AlphaMode = Str(m, "alphaMode") ?? "OPAQUE";
        mat.AlphaCutoff = Float(m, "alphaCutoff", 0.5f);
        mat.DoubleSided = m.TryGetProperty("doubleSided", out var ds) && ds.ValueKind == JsonValueKind.True;
        if (m.TryGetProperty("extensions", out var ext))
        {
            if (ext.TryGetProperty("KHR_materials_unlit", out _)) mat.Unlit = true;
            if (ext.TryGetProperty("KHR_materials_emissive_strength", out var es)) mat.EmissiveStrength = Float(es, "emissiveStrength", 1f);
        }
        return mat;
    }

    private static int TextureIndex(JsonElement texture)
    {
        if (Int(texture, "texCoord", 0) != 0)
            throw new FormatException("texture coordinates other than TEXCOORD_0 are not supported.");
        if (texture.TryGetProperty("extensions", out var extensions) && extensions.TryGetProperty("KHR_texture_transform", out _))
            throw new FormatException("KHR_texture_transform is not supported; apply texture transforms before export.");
        int index = Int(texture, "index", -1);
        if (index < 0) throw new FormatException("texture index is missing or invalid.");
        return index;
    }

    private sealed class Reader
    {
        private readonly JsonElement _root;
        private readonly ArraySegment<byte> _bin;
        private readonly List<JsonElement> _views;
        private readonly List<JsonElement> _accessors;

        public Reader(JsonElement root, ArraySegment<byte> bin)
        {
            _root = root;
            _bin = bin;
            _views = Array(root, "bufferViews");
            _accessors = Array(root, "accessors");
            var buffers = Array(root, "buffers");
            if (buffers.Count > 1) throw new FormatException("only one buffer (the GLB binary chunk) is supported.");
            if (buffers.Count == 1 && buffers[0].TryGetProperty("uri", out _))
                throw new FormatException("external .bin buffers are not supported; export a single .glb.");
        }

        public byte[] View(int index)
        {
            var (offset, length, _) = ViewRange(index);
            var bytes = new byte[length];
            System.Array.Copy(_bin.Array!, _bin.Offset + offset, bytes, 0, length);
            return bytes;
        }

        private (int Offset, int Length, int Stride) ViewRange(int index)
        {
            if (index < 0 || index >= _views.Count) throw new FormatException("bufferView " + index + " does not exist.");
            if (_bin.Array == null) throw new FormatException("the GLB has no binary chunk.");
            var v = _views[index];
            int offset = Int(v, "byteOffset", 0), length = Int(v, "byteLength", -1), stride = Int(v, "byteStride", 0);
            if (Int(v, "buffer", 0) != 0) throw new FormatException("bufferView must reference the GLB buffer.");
            if (length < 0 || offset < 0 || (long)offset + length > _bin.Count) throw new FormatException("bufferView " + index + " is outside the binary chunk.");
            return (offset, length, stride);
        }

        public GltfPrimitive Primitive(JsonElement prim)
        {
            int mode = Int(prim, "mode", 4);
            if (mode != 4) throw new FormatException("primitive mode " + mode + " is not supported (triangles only).");
            if (prim.TryGetProperty("targets", out _)) throw new FormatException("morph targets are not supported.");
            if (!prim.TryGetProperty("attributes", out var attrs) || !attrs.TryGetProperty("POSITION", out var posEl))
                throw new FormatException("primitive without POSITION.");

            var p = new GltfPrimitive { Material = Int(prim, "material", -1) };
            p.Positions = Floats3(posEl.GetInt32(), mirrorX: true);
            int count = p.Positions.Length / 3;
            if (attrs.TryGetProperty("NORMAL", out var nEl))
            {
                p.Normals = Floats3(nEl.GetInt32(), mirrorX: true);
                if (p.Normals.Length != p.Positions.Length) throw new FormatException("NORMAL count differs from POSITION count.");
            }
            if (attrs.TryGetProperty("TEXCOORD_0", out var uvEl))
            {
                var uv = ReadFloats(uvEl.GetInt32(), "VEC2", 2);
                for (int i = 1; i < uv.Length; i += 2) uv[i] = 1f - uv[i];
                if (uv.Length / 2 != count) throw new FormatException("TEXCOORD_0 count differs from POSITION count.");
                p.Uvs = uv;
            }

            foreach (var attribute in attrs.EnumerateObject())
                if ((attribute.Name.StartsWith("JOINTS_", StringComparison.Ordinal) && attribute.Name != "JOINTS_0") ||
                    (attribute.Name.StartsWith("WEIGHTS_", StringComparison.Ordinal) && attribute.Name != "WEIGHTS_0"))
                    throw new FormatException("more than four joint influences (JOINTS_1/WEIGHTS_1) are not supported.");
            bool joints = attrs.TryGetProperty("JOINTS_0", out var jointsEl), weights = attrs.TryGetProperty("WEIGHTS_0", out var weightsEl);
            if (joints != weights) throw new FormatException("JOINTS_0 and WEIGHTS_0 must occur together.");
            if (joints)
            {
                var jointValues = ReadInfluences(jointsEl.GetInt32(), false);
                p.Joints = new int[jointValues.Length];
                for (int i = 0; i < jointValues.Length; i++) p.Joints[i] = (int)jointValues[i];
                p.Weights = ReadInfluences(weightsEl.GetInt32(), true);
                if (p.Joints.Length != count * 4 || p.Weights.Length != count * 4) throw new FormatException("joint/weight count differs from POSITION count.");
                for (int i = 0; i < p.Weights.Length; i += 4)
                {
                    double sum = 0;
                    for (int k = 0; k < 4; k++) { if (p.Weights[i + k] < 0) throw new FormatException("skin weights must be nonnegative."); sum += p.Weights[i + k]; }
                    for (int k = 0; k < 4; k++)
                        for (int previous = 0; previous < k; previous++)
                            if (p.Weights[i + k] > 0 && p.Weights[i + previous] > 0 && p.Joints[i + k] == p.Joints[i + previous])
                                throw new FormatException("duplicate joint influences with nonzero weights are not supported.");
                    if (sum <= 0) throw new FormatException("skin weights have zero sum.");
                    for (int k = 0; k < 4; k++) p.Weights[i + k] = (float)(p.Weights[i + k] / sum);
                }
            }
            int[] idx;
            if (prim.TryGetProperty("indices", out var iEl)) idx = ReadIndices(iEl.GetInt32());
            else
            {
                idx = new int[count];
                for (int i = 0; i < count; i++) idx[i] = i;
            }
            if (idx.Length % 3 != 0) throw new FormatException("index count is not a multiple of 3.");
            foreach (int i in idx) if (i < 0 || i >= count) throw new FormatException("index " + i + " is out of range.");
            // Mirroring X flips handedness; reverse each triangle so faces keep facing out.
            for (int i = 0; i < idx.Length; i += 3) (idx[i + 1], idx[i + 2]) = (idx[i + 2], idx[i + 1]);
            p.Indices = idx;
            return p;
        }

        public void ReadSkinsAndAnimations(GltfScene scene)
        {
            foreach (var source in Array(_root, "skins"))
            {
                var joints = Array(source, "joints");
                if (joints.Count == 0) throw new FormatException("skins require a nonempty joints array.");
                var skin = new GltfSkin { Name = Str(source, "name") ?? "skin" + scene.Skins.Count, Skeleton = Int(source, "skeleton", -1), Joints = new int[joints.Count] };
                for (int i = 0; i < joints.Count; i++) skin.Joints[i] = joints[i].GetInt32();
                if (source.TryGetProperty("inverseBindMatrices", out var bind))
                {
                    skin.InverseBindMatrices = ReadFloats(bind.GetInt32(), "MAT4", 16);
                    if (skin.InverseBindMatrices.Length != joints.Count * 16) throw new FormatException("inverse bind matrix count differs from skin joints.");
                    for (int i = 0; i < skin.InverseBindMatrices.Length; i += 16)
                        if (skin.InverseBindMatrices[i + 3] != 0 || skin.InverseBindMatrices[i + 7] != 0 || skin.InverseBindMatrices[i + 11] != 0 || skin.InverseBindMatrices[i + 15] != 1)
                            throw new FormatException("inverse bind matrices must be affine (last row 0,0,0,1).");
                    for (int i = 0; i < skin.InverseBindMatrices.Length; i++)
                        if ((i % 16 / 4 == 0) != (i % 4 == 0)) skin.InverseBindMatrices[i] = -skin.InverseBindMatrices[i];
                }
                else
                {
                    skin.InverseBindMatrices = new float[joints.Count * 16];
                    for (int i = 0; i < joints.Count; i++) for (int k = 0; k < 16; k += 5) skin.InverseBindMatrices[i * 16 + k] = 1;
                }
                scene.Skins.Add(skin);
            }
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var source in Array(_root, "animations"))
            {
                string name = Str(source, "name") ?? "animation-" + scene.Animations.Count;
                if (!names.Add(name)) throw new FormatException("animation names must be unique.");
                var animation = new GltfAnimation { Name = name };
                var samplers = Array(source, "samplers");
                var targets = new HashSet<string>();
                foreach (var sourceChannel in Array(source, "channels"))
                {
                    int samplerIndex = Int(sourceChannel, "sampler", -1);
                    if (samplerIndex < 0 || samplerIndex >= samplers.Count) throw new FormatException("animation sampler does not exist.");
                    if (!sourceChannel.TryGetProperty("target", out var target)) throw new FormatException("animation channel requires target.");
                    string path = Str(target, "path") ?? "";
                    if (path != "translation" && path != "rotation" && path != "scale") throw new FormatException("animation path '" + path + "' is not supported (morph weights are unsupported).");
                    int node = Int(target, "node", -1);
                    if (!targets.Add(node + ":" + path)) throw new FormatException("duplicate animation channel node/path.");
                    var sampler = samplers[samplerIndex];
                    string interpolation = Str(sampler, "interpolation") ?? "LINEAR";
                    if (interpolation != "LINEAR" && interpolation != "STEP") throw new FormatException("animation interpolation '" + interpolation + "' is not supported; use LINEAR or STEP (CUBICSPLINE is unsupported).");
                    var times = ReadFloats(Int(sampler, "input", -1), "SCALAR", 1);
                    if (times.Length == 0) throw new FormatException("animation times cannot be empty.");
                    for (int i = 0; i < times.Length; i++) if (times[i] < 0 || (i > 0 && times[i] <= times[i - 1])) throw new FormatException("animation times must be nonnegative and strictly increasing.");
                    int width = path == "rotation" ? 4 : 3;
                    var values = ReadFloats(Int(sampler, "output", -1), width == 4 ? "VEC4" : "VEC3", width);
                    if (values.Length != times.Length * width) throw new FormatException("animation output count differs from input count.");
                    for (int i = 0; i < values.Length; i += width)
                    {
                        if (path == "translation") values[i] = -values[i];
                        if (path == "rotation")
                        {
                            double length = Math.Sqrt((double)values[i] * values[i] + (double)values[i + 1] * values[i + 1] + (double)values[i + 2] * values[i + 2] + (double)values[i + 3] * values[i + 3]);
                            if (length == 0) throw new FormatException("animation rotation quaternion must be nonzero.");
                            for (int k = 0; k < 4; k++) values[i + k] = (float)(values[i + k] / length);
                            values[i + 1] = -values[i + 1]; values[i + 2] = -values[i + 2];
                        }
                    }
                    animation.Duration = Math.Max(animation.Duration, times[times.Length - 1]);
                    animation.Channels.Add(new GltfAnimationChannel { Node = node, Path = path, Interpolation = interpolation, Times = times, Values = values });
                }
                if (animation.Channels.Count == 0) throw new FormatException("animations require nonempty channels.");
                // Reject unsupported interpolation even in an unused sampler.
                foreach (var sampler in samplers)
                {
                    string interpolation = Str(sampler, "interpolation") ?? "LINEAR";
                    if (interpolation != "LINEAR" && interpolation != "STEP") throw new FormatException("animation interpolation '" + interpolation + "' is unsupported.");
                }
                scene.Animations.Add(animation);
            }
        }

        private float[] ReadInfluences(int accessor, bool weights)
        {
            var a = Accessor(accessor);
            if (Str(a, "type") != "VEC4") throw new FormatException("joint/weight accessor must be VEC4.");
            int comp = Int(a, "componentType", 0);
            bool normalized = a.TryGetProperty("normalized", out var norm) && norm.ValueKind == JsonValueKind.True;
            if (weights && comp == 5126) return ReadFloats(accessor, "VEC4", 4);
            if (comp != 5121 && comp != 5123) throw new FormatException("joint/weight accessor must use unsigned byte or unsigned short (or float weights).");
            if (weights != normalized) throw new FormatException(weights ? "integer weights must be normalized." : "joint indices must not be normalized.");
            if (a.TryGetProperty("sparse", out _)) throw new FormatException("sparse accessors are not supported.");
            int count = Int(a, "count", 0), size = comp == 5121 ? 1 : 2;
            var (offset, length, stride) = ViewRange(Int(a, "bufferView", -1));
            if (stride == 0) stride = size * 4;
            offset = AccessorOffset(a, count, offset, length, stride, size * 4);
            if ((long)count * 4 > int.MaxValue) throw new FormatException("accessor is too large.");
            var result = new float[count * 4];
            for (int i = 0; i < count; i++) for (int k = 0; k < 4; k++)
            {
                int at = _bin.Offset + offset + i * stride + k * size;
                int value = size == 1 ? _bin.Array![at] : BitConverter.ToUInt16(_bin.Array!, at);
                result[i * 4 + k] = weights ? (float)value / (size == 1 ? 255 : 65535) : value;
            }
            return result;
        }

        private float[] Floats3(int accessor, bool mirrorX)
        {
            var f = ReadFloats(accessor, "VEC3", 3);
            if (mirrorX) for (int i = 0; i < f.Length; i += 3) f[i] = -f[i];
            return f;
        }

        public float[] ReadFloats(int accessor, string type, int width)
        {
            var a = Accessor(accessor);
            if (Str(a, "type") != type) throw new FormatException("accessor " + accessor + " is not " + type + ".");
            if (Int(a, "componentType", 0) != 5126) throw new FormatException("accessor " + accessor + ": only float vertex data is supported.");
            if (a.TryGetProperty("sparse", out _)) throw new FormatException("sparse accessors are not supported.");
            if (a.TryGetProperty("normalized", out var normalized) && normalized.ValueKind == JsonValueKind.True) throw new FormatException("float accessors cannot be normalized.");
            int count = Int(a, "count", 0);
            var (offset, length, stride) = ViewRange(Int(a, "bufferView", -1));
            int elem = width * 4;
            if (stride == 0) stride = elem;
            offset = AccessorOffset(a, count, offset, length, stride, elem);
            if ((long)count * width > int.MaxValue) throw new FormatException("accessor is too large.");
            var result = new float[count * width];
            for (int i = 0; i < count; i++)
                for (int k = 0; k < width; k++)
                {
                    float value = BitConverter.ToSingle(_bin.Array!, _bin.Offset + offset + i * stride + k * 4);
                    RequireFinite(value);
                    result[i * width + k] = value;
                }
            return result;
        }

        private int[] ReadIndices(int accessor)
        {
            var a = Accessor(accessor);
            if (Str(a, "type") != "SCALAR") throw new FormatException("index accessor is not SCALAR.");
            int comp = Int(a, "componentType", 0);
            int size = comp switch { 5121 => 1, 5123 => 2, 5125 => 4, _ => throw new FormatException("index component type " + comp + " is not supported.") };
            int count = Int(a, "count", 0);
            if (a.TryGetProperty("sparse", out _)) throw new FormatException("sparse accessors are not supported.");
            var (offset, length, stride) = ViewRange(Int(a, "bufferView", -1));
            if (stride == 0) stride = size;
            offset = AccessorOffset(a, count, offset, length, stride, size);
            var result = new int[count];
            var arr = _bin.Array!;
            int b = _bin.Offset + offset;
            for (int i = 0; i < count; i++)
            {
                int at = b + i * stride;
                result[i] = size == 1 ? arr[at] : size == 2 ? BitConverter.ToUInt16(arr, at) : (int)BitConverter.ToUInt32(arr, at);
            }
            return result;
        }

        private JsonElement Accessor(int index)
        {
            if (index < 0 || index >= _accessors.Count) throw new FormatException("accessor " + index + " does not exist.");
            return _accessors[index];
        }

        private static int AccessorOffset(JsonElement accessor, int count, int viewOffset, int length, int stride, int elementSize)
        {
            int relative = Int(accessor, "byteOffset", 0);
            long end = count == 0 ? relative : (long)relative + (long)(count - 1) * stride + elementSize;
            if (count < 0 || relative < 0 || stride < elementSize || end > length)
                throw new FormatException("accessor runs past its bufferView or has an invalid count, offset or stride.");
            return viewOffset + relative;
        }
    }

    private static List<JsonElement> Array(JsonElement e, string name)
    {
        var list = new List<JsonElement>();
        if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array)
            foreach (var x in a.EnumerateArray()) list.Add(x);
        return list;
    }

    private static int Int(JsonElement e, string name, int fallback) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : fallback;

    private static float Float(JsonElement e, string name, float fallback)
    {
        float value = e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetSingle() : fallback;
        RequireFinite(value);
        return value;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static float[] Floats(JsonElement a, int n)
    {
        if (a.ValueKind != JsonValueKind.Array || a.GetArrayLength() != n) throw new FormatException("expected an array of " + n + " numbers.");
        var f = new float[n];
        int i = 0;
        foreach (var x in a.EnumerateArray())
        {
            float value = x.GetSingle();
            RequireFinite(value);
            f[i++] = value;
        }
        return f;
    }
}

public sealed class GltfNode
{
    internal GltfNode(string name) => Name = name;
    public string Name { get; }
    public int Mesh { get; internal set; } = -1;
    public int Skin { get; internal set; } = -1;
    internal bool HasMatrix { get; set; }
    public List<int> Children { get; } = new();
    /// <summary>Unity space.</summary>
    public float[] Translation { get; internal set; } = { 0, 0, 0 };
    /// <summary>Unity space, (x, y, z, w).</summary>
    public float[] Rotation { get; internal set; } = { 0, 0, 0, 1 };
    public float[] Scale { get; internal set; } = { 1, 1, 1 };
}

public sealed class GltfMesh
{
    internal GltfMesh(string name) => Name = name;
    public string Name { get; }
    public List<GltfPrimitive> Primitives { get; } = new();
}

public sealed class GltfPrimitive
{
    /// <summary>x, y, z per vertex, Unity space.</summary>
    public float[] Positions { get; internal set; } = System.Array.Empty<float>();
    public int[]? Joints { get; internal set; }
    public float[]? Weights { get; internal set; }
    public float[]? Normals { get; internal set; }
    /// <summary>u, v per vertex, v already flipped for Unity.</summary>
    public float[]? Uvs { get; internal set; }
    /// <summary>Triangles, wound for Unity.</summary>
    public int[] Indices { get; internal set; } = System.Array.Empty<int>();
    public int Material { get; internal set; } = -1;
}

public sealed class GltfMaterial
{
    internal GltfMaterial(string name) => Name = name;
    public string Name { get; }
    /// <summary>Linear RGBA.</summary>
    public float[] BaseColor { get; internal set; } = { 1, 1, 1, 1 };
    public int BaseColorTexture { get; internal set; } = -1;
    public float Metallic { get; internal set; } = 1f;
    public float Roughness { get; internal set; } = 1f;
    /// <summary>Linear RGB.</summary>
    public float[] Emissive { get; internal set; } = { 0, 0, 0 };
    public int EmissiveTexture { get; internal set; } = -1;
    public float EmissiveStrength { get; internal set; } = 1f;
    /// <summary>OPAQUE, MASK or BLEND.</summary>
    public string AlphaMode { get; internal set; } = "OPAQUE";
    public float AlphaCutoff { get; internal set; } = 0.5f;
    public bool DoubleSided { get; internal set; }
    /// <summary><c>KHR_materials_unlit</c>: draw the base color as is, without lighting (screens, signs).</summary>
    public bool Unlit { get; internal set; }
}

public sealed class GltfImage
{
    internal GltfImage(string name, string mimeType, byte[] bytes)
    {
        Name = name;
        MimeType = mimeType;
        Bytes = bytes;
    }

    public string Name { get; }
    public string MimeType { get; }
    /// <summary>The encoded PNG or JPEG.</summary>
    public byte[] Bytes { get; }
}

public sealed class GltfSkin
{
    public string Name { get; internal set; } = "";
    public int[] Joints { get; internal set; } = System.Array.Empty<int>();
    /// <summary>Sixteen column-major Unity-space entries per joint.</summary>
    public float[] InverseBindMatrices { get; internal set; } = System.Array.Empty<float>();
    public int Skeleton { get; internal set; } = -1;
}

public sealed class GltfAnimation
{
    public string Name { get; internal set; } = "";
    public List<GltfAnimationChannel> Channels { get; } = new();
    public float Duration { get; internal set; }
}

public sealed class GltfAnimationChannel
{
    public int Node { get; internal set; }
    public string Path { get; internal set; } = "";
    public string Interpolation { get; internal set; } = "LINEAR";
    public float[] Times { get; internal set; } = System.Array.Empty<float>();
    public float[] Values { get; internal set; } = System.Array.Empty<float>();
}
