using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Progenitor.Content;
using Xunit;

namespace Progenitor.Tests;

public sealed class DataPackManifestTests : IDisposable
{
    private const string Id = "example.bone-sword";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "progenitor-pack-" + Guid.NewGuid().ToString("N"));
    private const string Item = "{\"id\":\"example.bone-sword.plus100\",\"cloneOf\":\"Item_Weapon_Sword_T01_Bone\",\"statBonuses\":{\"maxHealth\":100,\"physicalPower\":100,\"spellPower\":100}}";
    private static string Content(string item = Item) => "{\"schema\":1,\"items\":[" + item + "]}";

    public DataPackManifestTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, true);

    private void Write(string content, string? manifest = null)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        File.WriteAllBytes(Path.Combine(_directory, "items.json"), bytes);
        File.WriteAllText(Path.Combine(_directory, "pack.json"), manifest ??
            "{\"schema\":1,\"id\":\"" + Id + "\",\"version\":\"1.0.0\",\"content\":\"items.json\",\"sha256\":\"" + hash + "\"}", new UTF8Encoding(false));
    }

    private string Manifest() => File.ReadAllText(Path.Combine(_directory, "pack.json"));

    [Fact]
    public void Local_pack_validates_hash_namespace_and_item_stats()
    {
        Write(Content());
        var pack = DataPackManifest.Read(_directory);
        Assert.Equal(Id, pack.Id);
        Assert.Equal("1.0.0", pack.Version);
        Assert.Equal(_directory, pack.Directory);
        Assert.Equal(100f, Assert.Single(pack.Document.Items).StatBonuses!.PhysicalPower);
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("unsafe/name")]
    [InlineData("UPPERCASE")]
    [InlineData("a..b")]
    [InlineData(".hidden")]
    [InlineData("x-.")]
    public void Unsafe_pack_ids_are_refused(string id)
    {
        Write(Content());
        Write(Content(), Manifest().Replace(Id, id));
        Assert.Throws<InvalidDataException>(() => DataPackManifest.Read(_directory));
    }

    [Theory]
    [InlineData("scripts", "[]")]
    [InlineData("dependencies", "[]")]
    [InlineData("assets", "[]")]
    [InlineData("schema", "1")]
    public void Unknown_or_duplicate_manifest_fields_are_refused(string field, string value)
    {
        Write(Content());
        Write(Content(), Manifest().TrimEnd('}') + ",\"" + field + "\":" + value + "}");
        Assert.Throws<InvalidDataException>(() => DataPackManifest.Read(_directory));
    }

    [Theory]
    [InlineData("../items.json")]
    [InlineData("C:/elsewhere/items.json")]
    [InlineData("other.json")]
    public void Content_path_is_fixed(string path)
    {
        Write(Content());
        Write(Content(), Manifest().Replace("items.json", path));
        Assert.Throws<InvalidDataException>(() => DataPackManifest.Read(_directory));
    }

    [Fact]
    public void Content_hash_uses_exact_bytes()
    {
        Write(Content());
        File.AppendAllText(Path.Combine(_directory, "items.json"), " ");
        Assert.Throws<InvalidDataException>(() => DataPackManifest.Read(_directory));
    }

    [Fact]
    public void Items_must_use_pack_namespace()
    {
        Write(Content(Item.Replace(Id + ".plus100", "other.item")));
        Assert.Throws<InvalidDataException>(() => DataPackManifest.Read(_directory));
    }

    [Theory]
    [InlineData("payload.dll")]
    [InlineData("script.cs")]
    [InlineData("extra.json")]
    public void Extra_files_are_refused(string file)
    {
        Write(Content());
        File.WriteAllText(Path.Combine(_directory, file), "ignored?");
        Assert.Throws<InvalidDataException>(() => DataPackManifest.Read(_directory));
    }

    [Fact]
    public void Subdirectories_are_refused()
    {
        Write(Content());
        Directory.CreateDirectory(Path.Combine(_directory, "assets"));
        Assert.Throws<InvalidDataException>(() => DataPackManifest.Read(_directory));
    }

    [Fact]
    public void Oversized_file_is_refused()
    {
        Write(Content() + new string(' ', DataPackManifest.MaxFileBytes));
        Assert.Throws<InvalidDataException>(() => DataPackManifest.Read(_directory));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(10)]
    [InlineData(50)]
    [InlineData(100)]
    [InlineData(128)]
    public void Scale_sized_packs_validate_through_the_actual_loader(int count)
    {
        Write("{\"schema\":1,\"items\":[" + string.Join(",", Enumerable.Range(0, count)
            .Select(i => Item.Replace("plus100", "item" + i))) + "]}");
        var pack = DataPackManifest.Read(_directory);
        Assert.Equal(count, pack.Document.Items.Count);
        Assert.Equal(count, pack.Document.Items.Select(i => i.Id).Distinct().Count());
        Assert.All(pack.Document.Items, i => Assert.Equal(100f, i.StatBonuses!.MaxHealth));
    }

    [Fact]
    public void More_than_128_items_are_refused()
    {
        Write("{\"schema\":1,\"items\":[" + string.Join(",", Enumerable.Range(0, 129)
            .Select(i => Item.Replace("plus100", "item" + i))) + "]}");
        Assert.Throws<InvalidDataException>(() => DataPackManifest.Read(_directory));
    }

    [Fact]
    public void Duplicate_content_root_field_is_refused()
    {
        Write("{\"schema\":1,\"schema\":1,\"items\":[]}");
        Assert.Throws<InvalidDataException>(() => DataPackManifest.Read(_directory));
    }

    [Fact]
    public void Missing_content_file_is_refused()
    {
        Write(Content());
        File.Delete(Path.Combine(_directory, "items.json"));
        Assert.Throws<InvalidDataException>(() => DataPackManifest.Read(_directory));
    }

    [Fact]
    public void Numeric_builtin_clone_is_accepted()
    {
        Write(Content(Item.Replace("\"Item_Weapon_Sword_T01_Bone\"", "-257494203")));
        Assert.Equal(-257494203, Assert.Single(DataPackManifest.Read(_directory).Document.Items).CloneOfGuidHash);
    }

    [Fact]
    public void Invalid_utf8_is_refused_even_when_hash_matches()
    {
        var bytes = new byte[] { 0xff };
        Write(Content());
        var oldHash = DataPackManifest.Read(_directory).Sha256;
        File.WriteAllText(Path.Combine(_directory, "pack.json"), Manifest().Replace(oldHash,
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()), new UTF8Encoding(false));
        File.WriteAllBytes(Path.Combine(_directory, "items.json"), bytes);
        Assert.Throws<InvalidDataException>(() => DataPackManifest.Read(_directory));
    }

    [Fact]
    public void Checked_in_bone_sword_pack_validates()
    {
        var content = SourcePin.ReadFile("samples", "content-packs", "bone-sword", "items.json");
        var manifest = SourcePin.ReadFile("samples", "content-packs", "bone-sword", "pack.json");
        Write(content, manifest);
        Assert.Equal(Id, DataPackManifest.Read(_directory).Id);
    }
}
