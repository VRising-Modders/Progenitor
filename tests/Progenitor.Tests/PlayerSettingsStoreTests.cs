using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Progenitor.Config;
using Xunit;

namespace Progenitor.Tests;

public sealed class PlayerSettingsStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "player-settings-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _warnings = new();
    private double _now = 100;
    private string FilePath => Path.Combine(_root, "author.mod.json");
    private string BadPath => FilePath + ".bad";
    private PlayerSettingsStore Open() => new(_root, "author.mod", _warnings.Add, () => _now);
    private JsonObject Read() => (JsonObject)JsonNode.Parse(File.ReadAllText(FilePath))!;
    private void Plant(string json)
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(FilePath, json);
    }
    private static string Doc(string values, string player = "1") =>
        "{\"schema\":1,\"mod\":\"author.mod\",\"players\":{\"" + player + "\":{\"name\":\"Op\",\"updatedUtc\":\"x\",\"values\":{" +
        values + "}}},\"orphans\":{}}";

    [Fact]
    public void MissingAndDefaultOnlyDoNotCreateAFile()
    {
        var store = Open();
        Assert.Equal("true", store.Get(1, "Announce/Receive", "true"));
        Assert.False(store.Set(1, "Announce/Receive", "true", "true"));
        Assert.False(store.Orphan(1, "Missing"));
        store.Flush();
        _now += 60; store.Tick();
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public void OverridesRoundTripWithFullUlongIdentityAndMetadata()
    {
        var store = Open();
        Assert.True(store.Set(ulong.MaxValue, "Text/Value", "\"hello\"\n世界", "", "Test Player"));
        Assert.True(store.Set(1, "Text/Value", "", "default"));
        store.Flush();
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
        store.Flush();
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
        store.Flush();
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
    [InlineData("{\"schema\":1,\"mod\":\"author.mod\",\"players\":{\"0\":{\"name\":\"\",\"updatedUtc\":\"old\",\"values\":{}}},\"orphans\":{}}")]
    [InlineData("{\"schema\":1,\"mod\":\"author.mod\",\"players\":{\"01\":{\"name\":\"\",\"updatedUtc\":\"old\",\"values\":{}}},\"orphans\":{}}")]
    [InlineData("{\"schema\":1,\"mod\":\"author.mod\",\"players\":{\"1\":{\"name\":\"\",\"updatedUtc\":\"old\",\"values\":{\"K\":false}}},\"orphans\":{}}")]
    [InlineData("{\"schema\":1,\"mod\":\"author.mod\",\"players\":{\"1\":{\"name\":\"\",\"updatedUtc\":\"old\",\"values\":{\"K\":null}}},\"orphans\":{}}")]
    public void CorruptFileStartsEmptyKeepsABackupAndWarns(string json)
    {
        Plant(json);
        byte[] original = File.ReadAllBytes(FilePath);
        var store = Open();
        Assert.True(store.Loaded);
        Assert.Equal("default", store.Get(1, "K", "default"));
        Assert.Equal(original, File.ReadAllBytes(BadPath));
        Assert.Equal(original, File.ReadAllBytes(FilePath));
        Assert.Contains(_warnings, w => w.Contains("author.mod.json.bad", StringComparison.Ordinal));

        // The next save replaces the corrupt file; the backup stays.
        Assert.True(store.Set(1, "K", "new", "default"));
        store.Flush();
        Assert.Equal("new", Open().Get(1, "K", "default"));
        Assert.Equal(original, File.ReadAllBytes(BadPath));
    }

    [Fact]
    public void SecondCorruptionKeepsTheFirstBackup()
    {
        Plant("first broken");
        Open();
        Plant("second broken file");
        Open();
        var backups = Directory.GetFiles(_root, "*.bad").Select(File.ReadAllText).OrderBy(t => t).ToArray();
        Assert.Equal(new[] { "first broken", "second broken file" }, backups);
    }

    [Fact]
    public void LockedExistingFileIsRetriedAndNeverOverwrittenUnread()
    {
        var first = Open();
        first.Set(1, "A", "saved", "default");
        first.Flush();
        byte[] original = File.ReadAllBytes(FilePath);
        PlayerSettingsStore store;
        using (var locked = new FileStream(FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            store = Open();
            Assert.False(store.Loaded);
            Assert.Contains(_warnings, w => w.Contains("could not be read", StringComparison.Ordinal));
            Assert.True(store.Set(2, "A", "mine", "default"));
            Assert.ThrowsAny<IOException>(() => store.Flush());
            _now += 60; store.Tick();
            Assert.False(store.Loaded);
        }
        Assert.Equal(original, File.ReadAllBytes(FilePath));
        _now += 60; store.Tick();
        Assert.True(store.Loaded);
        Assert.Equal("saved", store.Get(1, "A", "default"));
        Assert.Equal("mine", store.Get(2, "A", "default"));
        store.Flush();
        var reload = Open();
        Assert.Equal("saved", reload.Get(1, "A", "default"));
        Assert.Equal("mine", reload.Get(2, "A", "default"));
    }

    [Fact]
    public void FailedWriteKeepsMemoryAndDiskAndIsRetried()
    {
        var store = Open();
        store.Set(1, "A", "saved", "default");
        store.Flush();
        byte[] original = File.ReadAllBytes(FilePath);
        using (var locked = new FileStream(FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.True(store.Set(1, "A", "changed", "default"));
            Assert.ThrowsAny<IOException>(() => store.Flush());
            _now += 60;
            store.Tick(); // warns once, never throws
            store.Tick();
        }
        Assert.Single(_warnings, w => w.Contains("could not be saved", StringComparison.Ordinal));
        Assert.Equal("changed", store.Get(1, "A", "default"));
        Assert.Equal(original, File.ReadAllBytes(FilePath));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
        _now += PlayerSettingsStore.RetrySeconds + 1;
        store.Tick();
        Assert.False(store.HasUnsavedChanges);
        Assert.Equal("changed", Open().Get(1, "A", "default"));
    }

    [Fact]
    public void WritesAreCoalescedUntilTheDelayPasses()
    {
        var store = Open();
        for (int i = 0; i < 20; i++) store.Set(1, "A", "value" + i, "default");
        store.Tick();
        Assert.False(File.Exists(FilePath));
        Assert.True(store.HasUnsavedChanges);
        _now += PlayerSettingsStore.FlushDelaySeconds / 2;
        store.Set(1, "B", "later", "default");
        store.Tick();
        Assert.False(File.Exists(FilePath));
        _now += PlayerSettingsStore.FlushDelaySeconds;
        store.Tick();
        Assert.False(store.HasUnsavedChanges);
        var reload = Open();
        Assert.Equal("value19", reload.Get(1, "A", "default"));
        Assert.Equal("later", reload.Get(1, "B", "default"));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    [Fact]
    public void ExternalEditWinsForKeysItChangedAndOwnChangesToOtherKeysAreReapplied()
    {
        var store = Open();
        store.Set(1, "A", "saved", "default");
        store.Set(1, "B", "saved", "default");
        store.Flush();
        // Unsaved changes: A (also edited externally) and C (not).
        store.Set(1, "A", "mine", "default");
        store.Set(1, "C", "mine", "default");
        Plant(Doc("\"A\":\"operator value\",\"B\":\"saved\",\"D\":\"operator\""));
        store.Flush();
        Assert.Equal("operator value", store.Get(1, "A", "default"));
        Assert.Equal("saved", store.Get(1, "B", "default"));
        Assert.Equal("mine", store.Get(1, "C", "default"));
        Assert.Equal("operator", store.Get(1, "D", "default"));
        var reload = Open();
        Assert.Equal("operator value", reload.Get(1, "A", "default"));
        Assert.Equal("mine", reload.Get(1, "C", "default"));
        Assert.Equal("operator", reload.Get(1, "D", "default"));
    }

    [Fact]
    public void RepeatedExternalEditsNeverBreakTheStore()
    {
        var store = Open();
        for (int i = 0; i < 4; i++)
        {
            Plant(Doc("\"Edited\":\"" + new string('x', i + 1) + "\""));
            _now += PlayerSettingsStore.CheckIntervalSeconds + 1;
            store.Tick();
            Assert.Equal(new string('x', i + 1), store.Get(1, "Edited", "default"));
            Assert.True(store.Set(2, "Mine", "v" + i, "default"));
            store.Flush();
            Assert.Equal("v" + i, Open().Get(2, "Mine", "default"));
            Assert.Equal(new string('x', i + 1), Open().Get(1, "Edited", "default"));
        }
        Assert.Empty(_warnings);
    }

    [Fact]
    public void ExternalCorruptionIsBackedUpAndTheLastGoodStateRewritten()
    {
        var first = Open();
        var other = Open();
        first.Set(1, "A", "saved", "default");
        first.Flush();
        // A second owner of the same file merges instead of failing.
        other.Set(2, "A", "other", "default");
        other.Flush();
        Assert.Equal("saved", Open().Get(1, "A", "default"));
        Assert.Equal("other", Open().Get(2, "A", "default"));

        Plant("broken by operator");
        _now += PlayerSettingsStore.CheckIntervalSeconds + 1;
        other.Tick();   // detects the corrupt file, keeps a backup and its last good values
        Assert.Equal("broken by operator", File.ReadAllText(BadPath));
        Assert.Equal("other", other.Get(2, "A", "default"));
        Assert.Equal("saved", other.Get(1, "A", "default"));
        _now += PlayerSettingsStore.FlushDelaySeconds + 1;
        other.Tick();   // rewrites the file
        Assert.Equal("saved", Open().Get(1, "A", "default"));
        Assert.Equal("other", Open().Get(2, "A", "default"));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    [Fact]
    public void StoresKeepAStoreOnlyWhenRegistrationSucceeds()
    {
        int opened = 0;
        var stores = new PlayerSettingsStores(id => { opened++; return new PlayerSettingsStore(_root, id, _warnings.Add, () => _now); }, _warnings.Add);
        Assert.Throws<ArgumentException>(() => stores.Register<int>("author.mod", _ => throw new ArgumentException("bad row")));
        Assert.Equal(0, stores.Count);
        PlayerSettingsStore? kept = null;
        Assert.Equal(1, stores.Register("author.mod", s => { kept = s; return 1; }));
        Assert.True(stores.Contains("author.mod"));
        Assert.Same(kept, stores.Register("author.mod", s => s));
        Assert.Equal(2, opened);
        Assert.Throws<ArgumentException>(() => stores.Register<int>("author.mod", _ => throw new ArgumentException("bad row")));
        Assert.True(stores.Contains("author.mod"));

        kept!.Set(1, "A", "value", "default");
        stores.Release("author.mod");
        Assert.Equal(0, stores.Count);
        Assert.Equal("value", Open().Get(1, "A", "default"));
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
        one.Flush(); two.Flush();
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
