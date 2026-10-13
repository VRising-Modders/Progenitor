using System;
using System.IO;
using System.Text;
using Progenitor.Models;
using Xunit;

namespace Progenitor.Tests;

public sealed class ModelPackageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "progenitor-model-package-tests-" + Guid.NewGuid().ToString("N"));
    private string Output => Path.Combine(_root, "package");
    private string Manifest => Path.Combine(Output, "manifest.json");

    public ModelPackageTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);

    private string Source(string? overrideJson = null)
    {
        byte[] json = Encoding.UTF8.GetBytes(overrideJson ?? "{\"asset\":{\"version\":\"2.0\"},\"nodes\":[{\"name\":\"Original\",\"mesh\":0}],\"meshes\":[{\"primitives\":[{\"attributes\":{\"POSITION\":0}}]}],\"buffers\":[{\"byteLength\":36}],\"bufferViews\":[{\"buffer\":0,\"byteLength\":36}],\"accessors\":[{\"bufferView\":0,\"componentType\":5126,\"count\":3,\"type\":\"VEC3\"}]}");
        int size = (json.Length + 3) & ~3;
        string source = Path.Combine(_root, "source.glb");
        using var stream = File.Create(source);
        using var writer = new BinaryWriter(stream);
        writer.Write(0x46546C67u); writer.Write(2u); writer.Write((uint)(20 + size + 44));
        writer.Write((uint)size); writer.Write(0x4E4F534Au); writer.Write(json);
        for (int i = json.Length; i < size; i++) writer.Write((byte)' ');
        writer.Write(36u); writer.Write(0x004E4942u);
        foreach (float value in new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 }) writer.Write(value);
        return source;
    }

    private void Build() => ModelPackage.Build(Source(), Output, "original.prop-1");
    private string AnimatedSource() => Source("""
        {"asset":{"version":"2.0"},"nodes":[{"mesh":0}],
         "meshes":[{"primitives":[{"attributes":{"POSITION":0}}]}],
         "buffers":[{"byteLength":36}],"bufferViews":[{"buffer":0,"byteLength":36}],
         "accessors":[{"bufferView":0,"componentType":5126,"count":3,"type":"VEC3"},
                      {"bufferView":0,"componentType":5126,"count":1,"type":"SCALAR"},
                      {"bufferView":0,"componentType":5126,"count":1,"type":"VEC3"}],
         "animations":[{"name":"Pose","samplers":[{"input":1,"output":2}],
                         "channels":[{"sampler":0,"target":{"node":0,"path":"translation"}}]}]}
        """);

    [Fact]
    public void Animated_package_roundtrips_and_static_kind_cannot_smuggle_clips()
    {
        var source = AnimatedSource();
        Assert.Throws<FormatException>(() => ModelPackage.Build(source, Output, "banner"));
        Assert.False(Directory.Exists(Output));
        ModelPackage.BuildAnimated(source, Output, "banner");
        Assert.Equal("Pose", Assert.Single(ModelPackage.Load(Output).Animations).Name);
        Assert.Contains("\"schemaVersion\": 2", File.ReadAllText(Manifest));
        Change("\"schemaVersion\": 2", "\"schemaVersion\": 1");
        Change("animated-model", "static-model");
        Assert.Contains("Static model", Assert.Throws<FormatException>(() => ModelPackage.Load(Output)).Message);
    }

    [Fact]
    public void Animated_package_requires_animation_and_never_overwrites()
    {
        Assert.Throws<FormatException>(() => ModelPackage.BuildAnimated(Source(), Output, "banner"));
        Assert.False(Directory.Exists(Output));
        ModelPackage.BuildAnimated(AnimatedSource(), Output, "banner");
        var before = File.ReadAllBytes(Manifest);
        Assert.Throws<IOException>(() => ModelPackage.BuildAnimated(AnimatedSource(), Output, "another"));
        Assert.Equal(before, File.ReadAllBytes(Manifest));
    }
    private void Change(string before, string after) => File.WriteAllText(Manifest, File.ReadAllText(Manifest).Replace(before, after));

    [Fact]
    public void Roundtrip_preserves_source_bytes_and_scene()
    {
        string source = Source();
        ModelPackage.Build(source, Output, "original.prop-1");
        Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(Path.Combine(Output, "model.glb")));
        Assert.Equal("Original", Assert.Single(ModelPackage.Load(Output).Nodes).Name);
    }

    [Fact]
    public void Existing_output_is_never_overwritten()
    {
        Build();
        string original = File.ReadAllText(Manifest);
        Assert.Throws<IOException>(() => ModelPackage.Build(Source(), Output, "another"));
        Assert.Equal(original, File.ReadAllText(Manifest));
        Assert.Empty(Directory.GetDirectories(_root, ".model-package-*"));
    }

    [Fact]
    public void Invalid_glb_never_publishes_output()
    {
        string source = Source();
        File.WriteAllText(source, "invalid");
        Assert.Throws<FormatException>(() => ModelPackage.Build(source, Output, "model"));
        Assert.False(Directory.Exists(Output));
        Assert.Empty(Directory.GetDirectories(_root, ".model-package-*"));
    }

    [Fact]
    public void Changed_model_is_rejected_before_parsing()
    {
        Build();
        File.WriteAllText(Path.Combine(Output, "model.glb"), "corrupted");
        Assert.Contains("SHA256 mismatch", Assert.Throws<FormatException>(() => ModelPackage.Load(Output)).Message);
    }

    [Theory]
    [InlineData("\"schemaVersion\": 1", "\"schemaVersion\": 2", "schemaVersion")]
    [InlineData("\"schemaVersion\": 1", "\"schemaVersion\": \"1\"", "schemaVersion")]
    [InlineData("static-model", "animated-model", "kind")]
    [InlineData("model.glb", "../source.glb", "model")]
    [InlineData("original.prop-1", "../escape", "assetId")]
    public void Unsupported_or_unsafe_manifest_is_rejected(string before, string after, string field)
    {
        Build(); Change(before, after);
        Assert.Contains(field, Assert.Throws<FormatException>(() => ModelPackage.Load(Output)).Message);
    }

    [Fact]
    public void Malformed_hash_is_rejected()
    {
        Build();
        string text = File.ReadAllText(Manifest);
        int start = text.IndexOf("\"sha256\": \"", StringComparison.Ordinal) + "\"sha256\": \"".Length;
        File.WriteAllText(Manifest, text.Remove(start, 64).Insert(start, new string('z', 64)));
        Assert.Contains("sha256", Assert.Throws<FormatException>(() => ModelPackage.Load(Output)).Message);
    }

    [Fact]
    public void Corrupt_manifest_has_actionable_error()
    {
        Build(); File.WriteAllText(Manifest, "{");
        Assert.Contains("invalid JSON", Assert.Throws<FormatException>(() => ModelPackage.Load(Output)).Message);
    }

    [Fact]
    public void Duplicate_manifest_fields_are_rejected()
    {
        Build(); Change("\"schemaVersion\": 1", "\"schemaVersion\": 1, \"schemaVersion\": 1");
        Assert.Contains("repeats field", Assert.Throws<FormatException>(() => ModelPackage.Load(Output)).Message);
    }

    [Fact]
    public void Oversized_manifest_is_rejected_before_JSON_parsing()
    {
        Build(); File.WriteAllText(Manifest, new string(' ', 65537));
        Assert.Contains("byte limit", Assert.Throws<FormatException>(() => ModelPackage.Load(Output)).Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("..")]
    [InlineData("folder/model")]
    public void Invalid_asset_ids_never_publish(string id)
    {
        Assert.Throws<ArgumentException>(() => ModelPackage.Build(Source(), Output, id));
        Assert.False(Directory.Exists(Output));
    }

    [Fact]
    public void Empty_scene_is_rejected_by_build_and_load_even_with_matching_hash()
    {
        Build();
        string empty = Source("{\"asset\":{\"version\":\"2.0\"},\"nodes\":[]}");
        Assert.Contains("reachable", Assert.Throws<FormatException>(() => ModelPackage.Build(empty, Path.Combine(_root, "empty"), "empty")).Message);
        byte[] bytes = File.ReadAllBytes(empty);
        File.WriteAllBytes(Path.Combine(Output, "model.glb"), bytes);
        string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
        File.WriteAllText(Manifest, "{\"schemaVersion\":1,\"assetId\":\"empty\",\"kind\":\"static-model\",\"model\":\"model.glb\",\"sha256\":\"" + hash + "\"}");
        Assert.Contains("reachable", Assert.Throws<FormatException>(() => ModelPackage.Load(Output)).Message);
    }

    [Fact]
    public void Mesh_outside_selected_scene_is_rejected()
    {
        string source = Source();
        byte[] bytes = File.ReadAllBytes(source);
        string json = Encoding.UTF8.GetString(bytes, 20, (int)BitConverter.ToUInt32(bytes, 12)).TrimEnd();
        source = Source(json.Substring(0, json.Length - 1) + ",\"scenes\":[{\"nodes\":[]}],\"scene\":0}");
        Assert.Contains("reachable", Assert.Throws<FormatException>(() => ModelPackage.Build(source, Output, "empty")).Message);
        Assert.False(Directory.Exists(Output));
    }
}
