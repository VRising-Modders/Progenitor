using System;
using System.IO;
using System.Text;
using Progenitor.Models;
using Xunit;

namespace Progenitor.Tests;

public class GltfAnimationTests
{
    private static GltfScene Animation(float[] times, float[] values, string path = "translation", string interpolation = "LINEAR", string nodes = "[{}]", string channels = "[{\"sampler\":0,\"target\":{\"node\":0,\"path\":\"PATH\"}}]")
    {
        using var binary = new MemoryStream();
        using var writer = new BinaryWriter(binary);
        foreach (float value in times) writer.Write(value);
        foreach (float value in values) writer.Write(value);
        string json = "{\"nodes\":" + nodes + ",\"animations\":[{\"samplers\":[{\"input\":0,\"output\":1,\"interpolation\":\"" + interpolation + "\"}],\"channels\":" + channels.Replace("PATH", path) + "}],\"bufferViews\":[{\"byteLength\":" + times.Length * 4 + "},{\"byteOffset\":" + times.Length * 4 + ",\"byteLength\":" + values.Length * 4 + "}],\"accessors\":[{\"bufferView\":0,\"componentType\":5126,\"type\":\"SCALAR\",\"count\":" + times.Length + "},{\"bufferView\":1,\"componentType\":5126,\"type\":\"" + (path == "rotation" ? "VEC4" : "VEC3") + "\",\"count\":" + values.Length / (path == "rotation" ? 4 : 3) + "}]}";
        return GltfScene.ParseGlb(Glb(json, binary.ToArray()));
    }

    private static byte[] Glb(string json, byte[]? bin = null)
    {
        byte[] text = Encoding.UTF8.GetBytes(json.PadRight((Encoding.UTF8.GetByteCount(json) + 3) / 4 * 4));
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(0x46546C67u); writer.Write(2u); writer.Write((uint)(20 + text.Length + (bin == null ? 0 : 8 + bin.Length)));
        writer.Write((uint)text.Length); writer.Write(0x4E4F534Au); writer.Write(text);
        if (bin != null) { writer.Write((uint)bin.Length); writer.Write(0x004E4942u); writer.Write(bin); }
        return stream.ToArray();
    }

    [Fact]
    public void Translation_keys_are_mirrored_and_duration_is_last_time()
    {
        var clip = Assert.Single(Animation(new[] { 0f, 2f }, new[] { 1f, 2f, 3f, 4f, 5f, 6f }).Animations);
        Assert.Equal("animation-0", clip.Name);
        Assert.Equal(2f, clip.Duration);
        Assert.Equal(new[] { -1f, 2f, 3f, -4f, 5f, 6f }, Assert.Single(clip.Channels).Values);
    }

    [Fact]
    public void Rotation_keys_are_normalized_and_mirrored()
    {
        var channel = Assert.Single(Assert.Single(Animation(new[] { 0f }, new[] { 0f, 2f, 0f, 2f }, "rotation", "STEP").Animations).Channels);
        Assert.Equal("STEP", channel.Interpolation);
        Assert.Equal(-(float)Math.Sqrt(0.5), channel.Values[1], 5);
        Assert.Equal((float)Math.Sqrt(0.5), channel.Values[3], 5);
    }

    [Theory]
    [InlineData(-1f, 2f)]
    [InlineData(1f, 1f)]
    [InlineData(2f, 1f)]
    [InlineData(float.NaN, 2f)]
    public void Invalid_key_times_fail(float a, float b) => Assert.Throws<FormatException>(() => Animation(new[] { a, b }, new float[6]));

    [Theory]
    [InlineData("CUBICSPLINE")]
    [InlineData("BAD")]
    public void Unsupported_interpolation_fails(string interpolation) => Assert.Contains(interpolation, Assert.Throws<FormatException>(() => Animation(new[] { 0f }, new float[3], interpolation: interpolation)).Message);

    [Fact]
    public void Zero_quaternion_and_mismatched_output_fail()
    {
        Assert.Contains("nonzero", Assert.Throws<FormatException>(() => Animation(new[] { 0f }, new float[4], "rotation")).Message);
        Assert.Contains("count", Assert.Throws<FormatException>(() => Animation(new[] { 0f, 1f }, new float[3])).Message);
    }

    [Fact]
    public void Duplicate_target_and_matrix_target_fail()
    {
        const string channel = "{\"sampler\":0,\"target\":{\"node\":0,\"path\":\"PATH\"}}";
        Assert.Contains("duplicate", Assert.Throws<FormatException>(() => Animation(new[] { 0f }, new float[3], channels: "[" + channel + "," + channel + "]")).Message);
        Assert.Contains("matrix", Assert.Throws<FormatException>(() => Animation(new[] { 0f }, new float[3], nodes: "[{\"matrix\":[1,0,0,0,0,1,0,0,0,0,1,0,0,0,0,1]}]")).Message);
    }

    [Fact]
    public void Missing_inverse_binds_default_to_identity()
    {
        var skin = Assert.Single(GltfScene.ParseGlb(Glb("{\"nodes\":[{}],\"skins\":[{\"joints\":[0]}]}")).Skins);
        Assert.Equal(new float[] { 1,0,0,0,0,1,0,0,0,0,1,0,0,0,0,1 }, skin.InverseBindMatrices);
    }

    [Theory]
    [InlineData("{\"nodes\":[{}],\"skins\":[{\"joints\":[0,0]}]}", "duplicate")]
    [InlineData("{\"nodes\":[{}],\"skins\":[{\"joints\":[1]}]}", "joint")]
    [InlineData("{\"nodes\":[{},{}],\"skins\":[{\"joints\":[1],\"skeleton\":0}]}", "ancestor")]
    [InlineData("{\"nodes\":[{\"skin\":0}],\"skins\":[{\"joints\":[0]}]}", "mesh")]
    public void Invalid_skins_fail(string json, string expected) => Assert.Contains(expected, Assert.Throws<FormatException>(() => GltfScene.ParseGlb(Glb(json))).Message);
    private static GltfScene SkinMesh(int component, bool zero = false, int joint = 0, bool normalized = true, bool duplicateJoint = false)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        for (int i = 0; i < 9; i++) writer.Write((float)i);
        for (int i = 0; i < 12; i++) writer.Write((byte)(i % 4 == 0 ? joint : i % 4 == 1 && !duplicateJoint ? 1 : 0));
        int bytes = component == 5121 ? 1 : component == 5123 ? 2 : 4;
        for (int i = 0; i < 12; i++)
        {
            int weight = zero ? 0 : (i % 4 == 0 ? 2 : i % 4 == 1 ? 1 : 0);
            if (bytes == 1) writer.Write((byte)weight); else if (bytes == 2) writer.Write((ushort)weight); else writer.Write((float)weight);
        }
        string json = "{\"nodes\":[{\"mesh\":0,\"skin\":0,\"children\":[1]},{\"children\":[2]},{}],\"skins\":[{\"joints\":[1,2],\"skeleton\":1}],\"meshes\":[{\"primitives\":[{\"attributes\":{\"POSITION\":0,\"JOINTS_0\":1,\"WEIGHTS_0\":2}}]}],\"bufferViews\":[{\"byteLength\":36},{\"byteOffset\":36,\"byteLength\":12},{\"byteOffset\":48,\"byteLength\":" + bytes * 12 + "}],\"accessors\":[{\"bufferView\":0,\"componentType\":5126,\"type\":\"VEC3\",\"count\":3},{\"bufferView\":1,\"componentType\":5121,\"type\":\"VEC4\",\"count\":3},{\"bufferView\":2,\"componentType\":" + component + ",\"type\":\"VEC4\",\"count\":3" + (component != 5126 && normalized ? ",\"normalized\":true" : "") + "}]}";
        return GltfScene.ParseGlb(Glb(json, stream.ToArray()));
    }

    [Theory]
    [InlineData(5121)]
    [InlineData(5123)]
    [InlineData(5126)]
    public void Four_influences_accept_supported_weights_and_normalize(int component)
    {
        var primitive = SkinMesh(component).Meshes[0].Primitives[0];
        Assert.Equal(12, primitive.Joints!.Length);
        Assert.Equal(2f / 3, primitive.Weights![0], 5);
        Assert.Equal(1f / 3, primitive.Weights[1], 5);
    }

    [Fact]
    public void Zero_weights_bad_joint_and_unnormalized_integer_weights_fail()
    {
        Assert.Contains("zero sum", Assert.Throws<FormatException>(() => SkinMesh(5121, zero: true)).Message);
        Assert.Contains("joint index", Assert.Throws<FormatException>(() => SkinMesh(5121, joint: 2)).Message);
        Assert.Contains("normalized", Assert.Throws<FormatException>(() => SkinMesh(5121, normalized: false)).Message);
    }

    [Fact]
    public void Inverse_bind_matrices_mirror_X_on_both_sides()
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        foreach (float value in new float[] { 1,0,0,0,0,1,0,0,0,0,1,0,4,5,6,1 }) writer.Write(value);
        var scene = GltfScene.ParseGlb(Glb("{\"nodes\":[{}],\"skins\":[{\"joints\":[0],\"inverseBindMatrices\":0}],\"bufferViews\":[{\"byteLength\":64}],\"accessors\":[{\"bufferView\":0,\"componentType\":5126,\"count\":1,\"type\":\"MAT4\"}]}", stream.ToArray()));
        Assert.Equal(-4f, scene.Skins[0].InverseBindMatrices[12]);
        Assert.Equal(5f, scene.Skins[0].InverseBindMatrices[13]);
        Assert.Equal(6f, scene.Skins[0].InverseBindMatrices[14]);
    }

    [Fact]
    public void Animation_target_must_belong_to_selected_scene()
    {
        // Additional top-level members are permitted after the node array in this fixture.
        Assert.Contains("selected scene", Assert.Throws<FormatException>(() => Animation(new[] { 0f }, new float[3], nodes: "[{},{}],\"scenes\":[{\"nodes\":[1]}]")).Message);
    }

    [Fact]
    public void Duplicate_positive_joint_influences_fail()
    {
        Assert.Contains("duplicate joint", Assert.Throws<FormatException>(() => SkinMesh(5121, duplicateJoint: true)).Message);
    }

    [Fact]
    public void Animated_rest_rotations_are_normalized_and_zero_is_refused()
    {
        var scene = Animation(new[] { 0f }, new float[3], nodes: "[{\"rotation\":[0,2,0,2]}]");
        Assert.Equal(-(float)Math.Sqrt(0.5), scene.Nodes[0].Rotation[1], 5);
        Assert.Equal((float)Math.Sqrt(0.5), scene.Nodes[0].Rotation[3], 5);
        Assert.Contains("rest rotation", Assert.Throws<FormatException>(() => Animation(new[] { 0f }, new float[3], nodes: "[{\"rotation\":[0,0,0,0]}]")).Message);
        // Preserve the static parser's existing transform contract.
        Assert.Equal(new float[4], GltfScene.ParseGlb(Glb("{\"nodes\":[{\"rotation\":[0,0,0,0]}]}")).Nodes[0].Rotation);
        Assert.Contains("rest rotation", Assert.Throws<FormatException>(() => GltfScene.ParseGlb(Glb("{\"nodes\":[{\"rotation\":[0,0,0,0]}],\"skins\":[{\"joints\":[0]}]}"))).Message);
    }

}
