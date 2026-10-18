using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Progenitor.Config;
using Xunit;

namespace Progenitor.Tests;

public sealed class PlayerSettingsStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "player-settings-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_root, "author.mod.json");
    private PlayerSettingsStore Open() => new(_root, "author.mod");
    private JsonObject Read() => (JsonObject)JsonNode.Parse(File.ReadAllText(FilePath))!;
    private void Plant(string json)
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(FilePath, json);
    }

    [Fact]
    public void MissingAndDefaultOnlyDoNotCreateAFile()
    {
        var store = Open();
        Assert.Equal("true", store.Get(1, "Announce/Receive", "true"));
        Assert.False(store.Set(1, "Announce/Receive", "true", "true"));
        Assert.False(store.Orphan(1, "Missing"));
        store.Flush();
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public void OverridesRoundTripWithFullUlongIdentityAndMetadata()
    {
        var store = Open();
        Assert.True(store.Set(ulong.MaxValue, "Text/Value", "\"hello\"\n世界", "", "Test Player"));
        Assert.True(store.Set(1, "Text/Value", "", "default"));
        var reload = Open();
        Assert.Equal("\"hello\"\n世界", reload.Get(ulong.MaxValue, "Text/Value", ""));
        Assert.Equal("", reload.Get(1, "Text/Value", "default"));
        Assert.Equal("fallback", reload.Get(2, "Text/Value", "fallback"));
        var json = Read();
        Assert.Equal(1, json["schema"]!.GetValue<int>());
        Assert.Equal("author.mod", json["mod"]!.GetValue<string>());
        var player = json["players"]!["18446744073709551615"]!;
        Assert.Equal("Test Player", player["name"]!.GetValue<string>());
        Assert.EndsWith("Z", player["updatedUtc"]!.GetValue<string>());
        Assert.Single(Directory.GetFiles(_root));
    }

    [Fact]
    public void ResetRemovesKeySoANewDefaultApplies()
    {
        var store = Open();
        store.Set(1, "A", "false", "true", "Name");
        store.Set(1, "Unregistered", "keep", "default");
        Assert.True(store.Set(1, "A", "true", "true"));
        Assert.Null(Read()["players"]!["1"]!["values"]!["A"]);
        Assert.Equal("new-default", Open().Get(1, "A", "new-default"));
        Assert.Equal("keep", Open().Get(1, "Unregistered", "default"));
        Assert.Equal("Name", Read()["players"]!["1"]!["name"]!.GetValue<string>());
    }

    [Fact]
    public void UnknownSettingsMetadataAndExistingOrphansSurvive()
    {
        Plant("""
            {"schema":1,"mod":"author.mod","future":{"enabled":true},
             "players":{"1":{"name":"Old","updatedUtc":"old","future":[1,2],
               "values":{"Unknown/Key":"raw","Known":"bad-number"}}},
             "orphans":{"2":{"Older":"retained"}}}
            """);
        var store = Open();
        Assert.True(store.Orphan(1, "Known"));
        Assert.False(store.Orphan(1, "Known"));
        Assert.Equal("42", store.Get(1, "Known", "42"));
        store.Set(1, "New", "changed", "default", "New Name");
        var json = Read();
        Assert.True(json["future"]!["enabled"]!.GetValue<bool>());
        Assert.Equal(2, ((JsonArray)json["players"]!["1"]!["future"]!).Count);
        Assert.Equal("raw", Open().Get(1, "Unknown/Key", ""));
        Assert.Equal("bad-number", json["orphans"]!["1"]!["Known"]!.GetValue<string>());
        Assert.Equal("retained", json["orphans"]!["2"]!["Older"]!.GetValue<string>());
        Assert.Equal("New Name", json["players"]!["1"]!["name"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("{\"schema\":2,\"mod\":\"author.mod\",\"players\":{},\"orphans\":{}}")]
    [InlineData("{\"schema\":1,\"mod\":\"other.mod\",\"players\":{},\"orphans\":{}}")]
    [InlineData("{\"schema\":1,\"schema\":1,\"mod\":\"author.mod\",\"players\":{},\"orphans\":{}}")]
    [InlineData("{\"schema\":1,\"mod\":\"author.mod\",\"players\":null,\"orphans\":{}}")]
    public void MalformedFilesFailClosedAndStayByteIdentical(string json)
    {
        Plant(json);
        byte[] original = File.ReadAllBytes(FilePath);
        Assert.Throws<InvalidDataException>(() => Open());
        Assert.Equal(original, File.ReadAllBytes(FilePath));
    }

    [Theory]
    [InlineData("0", "\"value\"")]
    [InlineData("01", "\"value\"")]
    [InlineData("18446744073709551616", "\"value\"")]
    [InlineData("1", "false")]
    [InlineData("1", "null")]
    [InlineData("1", "{}")]
    public void InvalidIdentityOrNonStringValuesFailClosed(string id, string value)
    {
        string json = "{\"schema\":1,\"mod\":\"author.mod\",\"players\":{\"" + id +
            "\":{\"name\":\"\",\"updatedUtc\":\"old\",\"values\":{\"K\":" + value + "}}},\"orphans\":{}}";
        Plant(json);
        Assert.Throws<InvalidDataException>(() => Open());
        Assert.Equal(json, File.ReadAllText(FilePath));
    }

    [Fact]
    public void LockedExistingFileCannotBecomeAnEmptyStore()
    {
        Open().Set(1, "A", "saved", "default");
        byte[] original = File.ReadAllBytes(FilePath);
        using (var locked = new FileStream(FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.ThrowsAny<IOException>(() => Open());
        Assert.Equal(original, File.ReadAllBytes(FilePath));
    }

    [Fact]
    public void FailedWriteLeavesMemoryAndDiskUnchangedAndCanBeRetried()
    {
        var store = Open();
        store.Set(1, "A", "saved", "default");
        byte[] original = File.ReadAllBytes(FilePath);
        using (var locked = new FileStream(FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.ThrowsAny<IOException>(() => store.Set(1, "A", "changed", "default"));
        Assert.Equal("saved", store.Get(1, "A", "default"));
        Assert.Equal(original, File.ReadAllBytes(FilePath));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
        Assert.True(store.Set(1, "A", "changed", "default"));
        Assert.Equal("changed", Open().Get(1, "A", "default"));
    }

    [Fact]
    public void ExternalCorruptionOrAStaleOwnerIsNeverOverwritten()
    {
        var first = Open();
        var stale = Open();
        first.Set(1, "A", "saved", "default");
        Assert.Throws<IOException>(() => stale.Set(2, "A", "stale", "default"));
        Assert.Equal("saved", Open().Get(1, "A", "default"));
        Plant("broken by operator");
        Assert.Throws<IOException>(() => first.Orphan(1, "A"));
        Assert.Equal("saved", first.Get(1, "A", "default"));
        Assert.Equal("broken by operator", File.ReadAllText(FilePath));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("..\\escape")]
    [InlineData("C:\\escape")]
    [InlineData("stream:other")]
    [InlineData("author.mod.")]
    [InlineData("author mod")]
    [InlineData("CON")]
    [InlineData("nul.anything")]
    [InlineData("LPT1")]
    [InlineData("")]
    public void UnsafeModGuidsAreRejected(string guid)
    {
        Assert.ThrowsAny<ArgumentException>(() => new PlayerSettingsStore(_root, guid));
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public void ModFilesAreIsolatedAndArgumentsValidated()
    {
        var one = Open();
        var two = new PlayerSettingsStore(_root, "author.other-mod_2");
        one.Set(1, "A", "one", "default");
        two.Set(1, "A", "two", "default");
        Assert.Equal("one", Open().Get(1, "A", "default"));
        Assert.Equal("two", two.Get(1, "A", "default"));
        Assert.Throws<ArgumentOutOfRangeException>(() => one.Get(0, "A", ""));
        Assert.Throws<ArgumentOutOfRangeException>(() => one.Set(0, "A", "", ""));
        Assert.Throws<ArgumentOutOfRangeException>(() => one.Orphan(0, "A"));
        Assert.ThrowsAny<ArgumentException>(() => one.Set(1, " ", "", ""));
        Assert.Throws<ArgumentNullException>(() => one.Set(1, "A", null!, ""));
        Assert.False(one.Set(1, "A", "one", "default"));
        Assert.Equal(2, Directory.GetFiles(_root).Length);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
