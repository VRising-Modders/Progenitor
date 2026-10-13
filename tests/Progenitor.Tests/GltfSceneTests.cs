using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Progenitor.Models;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// The GLB reader behind <c>ctx.Models.ImportGlb</c>: container parsing, the right- to left-handed
/// conversion (X mirrored, winding reversed, v flipped, rotation mirrored), materials and embedded
/// images, and loud refusal of what it cannot import. GLBs are written here byte by byte.
/// </summary>
public class GltfSceneTests
{
    /// <summary>A GLB with one triangle node, u16 indices, UVs, a textured material and an embedded image.</summary>
    private static byte[] TriangleGlb(string? extraJson = null, string nodeJson = "\"translation\":[1,2,3],\"rotation\":[0.1,0.2,0.3,0.927]", int mode = 4, string materialJson = null!)
    {
        var bin = new MemoryStream();
        var w = new BinaryWriter(bin);
        float[] pos = { 0, 0, 0, 1, 0, 0, 0, 1, 0 };
        float[] nrm = { 0, 0, 1, 0, 0, 1, 0, 0, 1 };
        float[] uv = { 0, 0, 1, 0, 0, 0.25f };
        foreach (var f in pos) w.Write(f);   // 0..36
        foreach (var f in nrm) w.Write(f);   // 36..72
        foreach (var f in uv) w.Write(f);    // 72..96
        foreach (ushort i in new ushort[] { 0, 1, 2 }) w.Write(i); // 96..102
        w.Write((ushort)0);                  // pad to 104
        byte[] png = { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3, 4 };
        w.Write(png);                        // 104..112
        var binBytes = bin.ToArray();

        materialJson ??= "{\"name\":\"Screen\",\"pbrMetallicRoughness\":{\"baseColorFactor\":[0.5,0.25,1,1],\"baseColorTexture\":{\"index\":0},\"metallicFactor\":0.2,\"roughnessFactor\":0.7},\"emissiveFactor\":[1,0.5,0],\"doubleSided\":true,\"alphaMode\":\"MASK\",\"alphaCutoff\":0.3,\"extensions\":{\"KHR_materials_unlit\":{},\"KHR_materials_emissive_strength\":{\"emissiveStrength\":4}}}";
        string json = "{\"asset\":{\"version\":\"2.0\"},\"scene\":0,\"scenes\":[{\"nodes\":[0]}]," +
            "\"nodes\":[{\"name\":\"Desk\",\"mesh\":0," + nodeJson + ",\"children\":[1]},{\"name\":\"Leg\"}]," +
            "\"meshes\":[{\"name\":\"DeskMesh\",\"primitives\":[{\"attributes\":{\"POSITION\":0,\"NORMAL\":1,\"TEXCOORD_0\":2},\"indices\":3,\"material\":0,\"mode\":" + mode + "}]}]," +
            "\"materials\":[" + materialJson + "],\"textures\":[{\"source\":0}],\"images\":[{\"name\":\"screen\",\"mimeType\":\"image/png\",\"bufferView\":4}]," +
            "\"buffers\":[{\"byteLength\":" + binBytes.Length + "}]," +
            "\"bufferViews\":[{\"buffer\":0,\"byteOffset\":0,\"byteLength\":36},{\"buffer\":0,\"byteOffset\":36,\"byteLength\":36},{\"buffer\":0,\"byteOffset\":72,\"byteLength\":24},{\"buffer\":0,\"byteOffset\":96,\"byteLength\":6},{\"buffer\":0,\"byteOffset\":104,\"byteLength\":8}]," +
            "\"accessors\":[{\"bufferView\":0,\"componentType\":5126,\"count\":3,\"type\":\"VEC3\"},{\"bufferView\":1,\"componentType\":5126,\"count\":3,\"type\":\"VEC3\"},{\"bufferView\":2,\"componentType\":5126,\"count\":3,\"type\":\"VEC2\"},{\"bufferView\":3,\"componentType\":5123,\"count\":3,\"type\":\"SCALAR\"}]" +
            (extraJson ?? "") + "}";
        return Glb(json, binBytes);
    }

    private static byte[] Glb(string json, byte[]? bin)
    {
        var jsonBytes = Encoding.UTF8.GetBytes(json);
        int jsonPad = (4 - jsonBytes.Length % 4) % 4;
        int binPad = bin == null ? 0 : (4 - bin.Length % 4) % 4;
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        int total = 12 + 8 + jsonBytes.Length + jsonPad + (bin == null ? 0 : 8 + bin.Length + binPad);
        w.Write(0x46546C67u); w.Write(2u); w.Write((uint)total);
        w.Write((uint)(jsonBytes.Length + jsonPad)); w.Write(0x4E4F534Au); w.Write(jsonBytes);
        for (int i = 0; i < jsonPad; i++) w.Write((byte)' ');
        if (bin != null)
        {
            w.Write((uint)(bin.Length + binPad)); w.Write(0x004E4942u); w.Write(bin);
            for (int i = 0; i < binPad; i++) w.Write((byte)0);
        }
        return ms.ToArray();
    }

    [Fact]
    public void Positions_and_normals_are_mirrored_in_X_and_triangles_rewound()
    {
        var s = GltfScene.ParseGlb(TriangleGlb());
        var p = Assert.Single(Assert.Single(s.Meshes).Primitives);
        Assert.Equal(new float[] { -0f, 0, 0, -1, 0, 0, -0f, 1, 0 }, p.Positions);
        Assert.Equal(new float[] { -0f, 0, 1, -0f, 0, 1, -0f, 0, 1 }, p.Normals);
        Assert.Equal(new[] { 0, 2, 1 }, p.Indices);
    }

    [Fact]
    public void Texture_v_is_flipped_for_Unity()
    {
        var p = GltfScene.ParseGlb(TriangleGlb()).Meshes[0].Primitives[0];
        Assert.Equal(new float[] { 0, 1, 1, 1, 0, 0.75f }, p.Uvs);
    }

    [Fact]
    public void Node_transform_is_mirrored_and_hierarchy_kept()
    {
        var s = GltfScene.ParseGlb(TriangleGlb());
        Assert.Equal(new[] { 0 }, s.RootNodes);
        var desk = s.Nodes[0];
        Assert.Equal("Desk", desk.Name);
        Assert.Equal(0, desk.Mesh);
        Assert.Equal(new float[] { -1, 2, 3 }, desk.Translation);
        Assert.Equal(new[] { 0.1f, -0.2f, -0.3f, 0.927f }, desk.Rotation);
        Assert.Equal(new[] { 1 }, desk.Children);
        Assert.Equal(-1, s.Nodes[1].Mesh);
    }

    [Fact]
    public void A_matrix_node_decomposes_to_translation_rotation_and_scale()
    {
        // 90 degrees about Y, scale 2, translation (4, 5, 6); column-major.
        var s = GltfScene.ParseGlb(TriangleGlb(nodeJson: "\"matrix\":[0,0,-2,0, 0,2,0,0, 2,0,0,0, 4,5,6,1]"));
        var n = s.Nodes[0];
        Assert.Equal(new float[] { -4, 5, 6 }, n.Translation);
        Assert.Equal(new float[] { 2, 2, 2 }, n.Scale);
        float h = (float)Math.Sqrt(0.5);
        // glTF (0, h, 0, h) mirrored to (0, -h, 0, h).
        Assert.Equal(0f, n.Rotation[0], 4);
        Assert.Equal(-h, n.Rotation[1], 4);
        Assert.Equal(0f, n.Rotation[2], 4);
        Assert.Equal(h, n.Rotation[3], 4);
    }

    [Fact]
    public void Materials_and_embedded_images_are_read()
    {
        var s = GltfScene.ParseGlb(TriangleGlb());
        var m = Assert.Single(s.Materials);
        Assert.Equal("Screen", m.Name);
        Assert.Equal(new[] { 0.5f, 0.25f, 1f, 1f }, m.BaseColor);
        Assert.Equal(0, m.BaseColorTexture);
        Assert.Equal(0.2f, m.Metallic);
        Assert.Equal(0.7f, m.Roughness);
        Assert.Equal(new[] { 1f, 0.5f, 0f }, m.Emissive);
        Assert.Equal(4f, m.EmissiveStrength);
        Assert.True(m.DoubleSided);
        Assert.True(m.Unlit);
        Assert.Equal("MASK", m.AlphaMode);
        Assert.Equal(0.3f, m.AlphaCutoff);
        Assert.Equal(new[] { 0 }, s.TextureImages);
        var img = Assert.Single(s.Images);
        Assert.Equal("image/png", img.MimeType);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3, 4 }, img.Bytes);
    }

    [Fact]
    public void Material_defaults_follow_the_spec()
    {
        var m = GltfScene.ParseGlb(TriangleGlb(materialJson: "{\"name\":\"Plain\"}")).Materials[0];
        Assert.Equal(new[] { 1f, 1f, 1f, 1f }, m.BaseColor);
        Assert.Equal(1f, m.Metallic);
        Assert.Equal(1f, m.Roughness);
        Assert.Equal("OPAQUE", m.AlphaMode);
        Assert.False(m.Unlit);
        Assert.False(m.DoubleSided);
    }

    [Theory]
    [InlineData(",\"skins\":[{}]", "\"skin\":0", 4, "skins")]
    [InlineData(null, "\"translation\":[0,0,0]", 1, "triangles only")]
    public void Unsupported_features_are_refused_by_name(string? extra, string node, int mode, string expected)
    {
        var e = Assert.Throws<FormatException>(() => GltfScene.ParseGlb(TriangleGlb(extra, node, mode)));
        Assert.Contains(expected, e.Message);
    }

    [Fact]
    public void External_buffers_and_images_are_refused()
    {
        string json = "{\"asset\":{\"version\":\"2.0\"},\"buffers\":[{\"byteLength\":4,\"uri\":\"x.bin\"}]}";
        Assert.Contains("external .bin", Assert.Throws<FormatException>(() => GltfScene.ParseGlb(Glb(json, null))).Message);
        json = "{\"asset\":{\"version\":\"2.0\"},\"images\":[{\"uri\":\"a.png\"}]}";
        Assert.Contains("external image", Assert.Throws<FormatException>(() => GltfScene.ParseGlb(Glb(json, null))).Message);
    }

    [Fact]
    public void Bad_containers_are_refused()
    {
        Assert.Throws<FormatException>(() => GltfScene.ParseGlb(new byte[4]));
        var glb = TriangleGlb();
        glb[0] = 0;
        Assert.Contains("magic", Assert.Throws<FormatException>(() => GltfScene.ParseGlb(glb)).Message);
        glb = TriangleGlb();
        glb[4] = 1;
        Assert.Contains("version", Assert.Throws<FormatException>(() => GltfScene.ParseGlb(glb)).Message);
    }

    [Fact]
    public void Without_a_scene_list_every_unparented_node_is_a_root()
    {
        string json = "{\"asset\":{\"version\":\"2.0\"},\"nodes\":[{\"name\":\"A\",\"children\":[2]},{\"name\":\"B\"},{\"name\":\"C\"}]}";
        Assert.Equal(new List<int> { 0, 1 }, GltfScene.ParseGlb(Glb(json, null)).RootNodes);
    }

    [Theory]
    [InlineData("\"animations\":[{}]", "animations")]
    [InlineData("\"skins\":[{}]", "skins")]
    [InlineData("\"nodes\":[{\"weights\":[1]}]", "morph")]
    [InlineData("\"meshes\":[{\"weights\":[1]}]", "morph")]
    [InlineData("\"meshes\":[{\"primitives\":[{\"targets\":[{}]}]}]", "morph")]
    [InlineData("\"extensionsRequired\":[\"KHR_draco_mesh_compression\"]", "required extension")]
    public void Static_import_refuses_unsupported_features_even_when_unused(string body, string expected)
    {
        Assert.Contains(expected, Assert.Throws<FormatException>(() => GltfScene.ParseGlb(Glb("{" + body + "}", null))).Message);
    }

    [Fact]
    public void Supported_required_material_extensions_are_accepted()
    {
        GltfScene.ParseGlb(Glb("{\"extensionsRequired\":[\"KHR_materials_unlit\",\"KHR_materials_emissive_strength\"]}", null));
    }

    [Theory]
    [InlineData("{\"nodes\":[{\"children\":[0]}]}", "cycle")]
    [InlineData("{\"nodes\":[{\"children\":[1]},{\"children\":[0]}]}", "cycle")]
    [InlineData("{\"nodes\":[{\"children\":[1]}]}", "child node")]
    [InlineData("{\"nodes\":[{\"children\":[-1]}]}", "child node")]
    [InlineData("{\"nodes\":[{\"children\":[1,1]},{}]}", "duplicate")]
    [InlineData("{\"nodes\":[{\"mesh\":0}]}", "node mesh")]
    [InlineData("{\"scene\":-1,\"scenes\":[{}]}", "default scene")]
    public void Invalid_node_graphs_are_refused(string json, string expected)
    {
        Assert.Contains(expected, Assert.Throws<FormatException>(() => GltfScene.ParseGlb(Glb(json, null))).Message);
    }

    [Theory]
    [InlineData(8, uint.MaxValue, "length field")]
    [InlineData(8, 12u, "length field")]
    [InlineData(12, uint.MaxValue, "chunk runs")]
    [InlineData(12, 1u, "aligned")]
    public void Container_lengths_cannot_wrap_or_ignore_file_bytes(int offset, uint value, string expected)
    {
        byte[] glb = Glb("{}", null);
        System.Array.Copy(BitConverter.GetBytes(value), 0, glb, offset, 4);
        Assert.Contains(expected, Assert.Throws<FormatException>(() => GltfScene.ParseGlb(glb)).Message);
    }

    [Theory]
    [InlineData("\"byteLength\":4", "\"count\":3", "bufferView")]
    [InlineData("\"byteLength\":36", "\"count\":-1", "invalid count")]
    [InlineData("\"byteLength\":36,\"byteOffset\":2147483647", "\"count\":3", "outside")]
    [InlineData("\"byteLength\":36,\"byteStride\":4", "\"count\":3", "stride")]
    public void Accessors_must_fit_their_own_views(string view, string count, string expected)
    {
        string json = "{\"meshes\":[{\"primitives\":[{\"attributes\":{\"POSITION\":0}}]}],\"bufferViews\":[{" + view + "}],\"accessors\":[{\"bufferView\":0,\"componentType\":5126,\"type\":\"VEC3\"," + count + "}]}";
        Assert.Contains(expected, Assert.Throws<FormatException>(() => GltfScene.ParseGlb(Glb(json, new byte[36]))).Message);
    }

    [Theory]
    [InlineData("{\"textures\":[{}]}", "texture image")]
    [InlineData("{\"textures\":[{\"source\":0}]}", "texture image")]
    [InlineData("{\"materials\":[{\"emissiveTexture\":{}}]}", "texture index")]
    [InlineData("{\"materials\":[{\"pbrMetallicRoughness\":{\"baseColorTexture\":{\"index\":0}}}]}", "base color texture")]
    [InlineData("{\"materials\":[{\"emissiveTexture\":{\"index\":0}}]}", "emissive texture")]
    [InlineData("{\"nodes\":[{},{}],\"scenes\":[{\"nodes\":[0,0]}]}", "roots")]
    [InlineData("{\"nodes\":[{\"children\":[1]},{}],\"scenes\":[{\"nodes\":[1]}]}", "roots")]
    public void Missing_render_references_and_invalid_roots_are_refused(string json, string expected)
    {
        Assert.Contains(expected, Assert.Throws<FormatException>(() => GltfScene.ParseGlb(Glb(json, null))).Message);
    }

    [Fact]
    public void Missing_primitive_material_is_refused()
    {
        Assert.Contains("primitive material", Assert.Throws<FormatException>(() => GltfScene.ParseGlb(
            TriangleGlb(materialJson: "{}", extraJson: ",\"materials\":[]"))).Message);
    }

    [Fact]
    public void Hierarchy_depth_matches_the_runtime_limit()
    {
        string Chain(int depth)
        {
            var nodes = new List<string>();
            for (int i = 0; i < depth; i++) nodes.Add("{\"children\":[" + (i + 1) + "]}");
            nodes.Add("{}");
            return "{\"nodes\":[" + string.Join(",", nodes) + "]}";
        }
        Assert.Equal(65, GltfScene.ParseGlb(Glb(Chain(64), null)).Nodes.Count);
        Assert.Contains("deeper than 64", Assert.Throws<FormatException>(() => GltfScene.ParseGlb(Glb(Chain(65), null))).Message);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void Non_finite_binary_vertices_are_refused(float value)
    {
        byte[] glb = TriangleGlb();
        int binaryStart = 20 + (int)BitConverter.ToUInt32(glb, 12) + 8;
        System.Array.Copy(BitConverter.GetBytes(value), 0, glb, binaryStart, 4);
        Assert.Contains("non-finite", Assert.Throws<FormatException>(() => GltfScene.ParseGlb(glb)).Message);
    }

    [Theory]
    [InlineData("\"texCoord\":1", "TEXCOORD_0")]
    [InlineData("\"extensions\":{\"KHR_texture_transform\":{}}", "KHR_texture_transform")]
    public void Unsupported_texture_coordinate_mapping_is_refused(string property, string expected)
    {
        string material = "{\"pbrMetallicRoughness\":{\"baseColorTexture\":{\"index\":0," + property + "}}}";
        Assert.Contains(expected, Assert.Throws<FormatException>(() => GltfScene.ParseGlb(TriangleGlb(materialJson: material))).Message);
    }

    [Theory]
    [InlineData("[-1,0,0,0,0,1,0,0,0,0,1,0,0,0,0,1]", "reflected")]
    [InlineData("[1,0,0,0,0.5,1,0,0,0,0,1,0,0,0,0,1]", "sheared")]
    [InlineData("[1,0,0,0.5,0,1,0,0,0,0,1,0,0,0,0,1]", "non-affine")]
    public void Matrices_that_cannot_be_preserved_as_TRS_are_refused(string matrix, string expected)
    {
        var error = Assert.Throws<FormatException>(() => GltfScene.ParseGlb(TriangleGlb(nodeJson: "\"matrix\":" + matrix)));
        Assert.Contains(expected, error.Message);
        Assert.Contains("use TRS or apply transforms", error.Message);
    }

    [Fact]
    public void Reflected_scale_is_supported_when_authored_as_TRS()
    {
        Assert.Equal(new float[] { -1, 1, 1 }, GltfScene.ParseGlb(TriangleGlb(nodeJson: "\"scale\":[-1,1,1]")).Nodes[0].Scale);
    }
}
