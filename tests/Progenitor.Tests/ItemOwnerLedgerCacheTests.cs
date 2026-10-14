using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Progenitor.Content;
using Xunit;

namespace Progenitor.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ItemOwnerLedgerCacheCollection { internal const string Name = "ItemOwnerLedgerCache"; }

/// <summary>
/// The ledger load cache and owner index: every Load still answers exactly what a fresh parse of
/// the file's current bytes answers, and is independent of every other Load.
/// </summary>
[Collection(ItemOwnerLedgerCacheCollection.Name)]
public sealed class ItemOwnerLedgerCacheTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "progenitor-owner-cache-" + Guid.NewGuid().ToString("N"));
    private string FileName => Path.Combine(_root, ItemOwnerLedger.RelativeFileName);

    public ItemOwnerLedgerCacheTests() => ItemOwnerLedger.ResetCacheForTests();

    public void Dispose()
    {
        ItemOwnerLedger.ResetCacheForTests();
        try { Directory.Delete(_root, true); } catch { }
    }

    private void Write(params (string Item, int Guid, string Owner)[] rows)
    {
        var ledger = new ItemOwnerLedger();
        foreach (var r in rows) Assert.True(ledger.Register(r.Item, r.Guid, r.Owner, "progenitor-reload/" + r.Owner + ".dll"));
        // Save refuses to overwrite a file it did not load: start from a clean slate.
        if (File.Exists(FileName)) File.Delete(FileName);
        Assert.True(ledger.Save(FileName));
    }

    private void Age(TimeSpan by) => File.SetLastWriteTimeUtc(FileName, DateTime.UtcNow - by);

    [Fact]
    public void Unchanged_old_file_is_parsed_once_and_then_not_even_read()
    {
        Write(("a", 1, "mod"));
        Age(TimeSpan.FromMinutes(1));
        Assert.Single(ItemOwnerLedger.Load(FileName).Rows);
        Assert.Single(ItemOwnerLedger.Load(FileName).Rows);
        Assert.Single(ItemOwnerLedger.Load(FileName).Rows);
        Assert.Equal(1, ItemOwnerLedger.ParseCount);
        Assert.Equal(1, ItemOwnerLedger.ReadCount);
    }

    [Fact]
    public void A_fresh_file_is_rehashed_every_load_so_a_same_size_rewrite_is_seen()
    {
        Write(("a", 1, "mod"));
        var first = ItemOwnerLedger.Load(FileName);
        Assert.Equal("mod", Assert.Single(first.Rows).PluginId);
        // Same length, same coarse timestamp: only the bytes differ.
        var stamp = File.GetLastWriteTimeUtc(FileName);
        var text = File.ReadAllText(FileName).Replace("\"mod\"", "\"moe\"").Replace("mod.dll", "moe.dll");
        File.WriteAllText(FileName, text);
        File.SetLastWriteTimeUtc(FileName, stamp);
        Assert.Equal("moe", Assert.Single(ItemOwnerLedger.Load(FileName).Rows).PluginId);
        Assert.Equal(2, ItemOwnerLedger.ParseCount);
    }

    [Fact]
    public void A_changed_size_or_write_time_rereads_a_trusted_file()
    {
        Write(("a", 1, "mod"));
        Age(TimeSpan.FromMinutes(1));
        Assert.Single(ItemOwnerLedger.Load(FileName).Rows);
        var ledger = ItemOwnerLedger.Load(FileName);
        Assert.True(ledger.Register("b", 2, "mod", "progenitor-reload/mod.dll"));
        Assert.True(ledger.Save(FileName));
        Assert.Equal(2, ItemOwnerLedger.Load(FileName).Rows.Count);

        // Write time alone (same bytes length) also invalidates.
        Age(TimeSpan.FromMinutes(2));
        int reads = ItemOwnerLedger.ReadCount;
        ItemOwnerLedger.Load(FileName);
        Assert.Equal(reads + 1, ItemOwnerLedger.ReadCount);
    }

    [Fact]
    public void Each_load_is_independent_of_changes_made_to_another()
    {
        Write(("a", 1, "mod"), ("b", 2, "other"));
        Age(TimeSpan.FromMinutes(1));
        var first = ItemOwnerLedger.Load(FileName);
        Assert.True(first.Register("c", 3, "mod", "progenitor-reload/mod.dll"));
        Assert.True(first.MarkRemoved("other"));
        var second = ItemOwnerLedger.Load(FileName);
        Assert.Equal(2, second.Rows.Count);
        Assert.Empty(second.Eligible("other", false, false, false, false));
        Assert.Single(first.Eligible("other", false, false, false, false));
    }

    [Fact]
    public void Save_after_a_cached_load_keeps_its_concurrency_check()
    {
        Write(("a", 1, "mod"));
        Age(TimeSpan.FromMinutes(1));
        var stale = ItemOwnerLedger.Load(FileName);
        var winner = ItemOwnerLedger.Load(FileName);
        Assert.True(winner.Register("b", 2, "mod", "progenitor-reload/mod.dll"));
        Assert.True(winner.Save(FileName));
        Assert.True(stale.MarkRemoved("mod"));
        Assert.False(stale.Save(FileName));
    }

    [Fact]
    public void Invalid_file_stays_invalid_from_the_cache()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FileName)!);
        File.WriteAllText(FileName, "{\"schema\":2,\"items\":[],\"removedOwners\":[]}");
        Age(TimeSpan.FromMinutes(1));
        var a = ItemOwnerLedger.Load(FileName);
        var b = ItemOwnerLedger.Load(FileName);
        Assert.False(a.IsValid); Assert.False(b.IsValid);
        Assert.Equal(a.Error, b.Error);
        Assert.False(b.Register("x", 1, "mod", "plugins/mod.dll"));
    }

    [Fact]
    public void Missing_file_is_an_empty_valid_ledger()
    {
        var ledger = ItemOwnerLedger.Load(FileName);
        Assert.True(ledger.IsValid); Assert.Empty(ledger.Rows);
    }

    [Fact]
    public void Owner_index_matches_the_row_scans_it_replaces()
    {
        var ledger = new ItemOwnerLedger();
        Assert.True(ledger.Register("a", 1, "m2", "plugins/m2.dll"));
        Assert.True(ledger.Register("b", 2, "m1", "plugins/m1.dll"));
        Assert.True(ledger.Register("c", 3, "m2", "plugins/m2.dll"));
        Assert.Equal(new[] { "m2", "m1" }, ledger.Owners);
        Assert.Equal(new[] { 1, 3 }, ledger.RowsOf("m2").Select(r => r.Guid));
        // A replaced row keeps its place and the index follows it.
        Assert.True(ledger.Register("a", 1, "m2", "plugins/m2-renamed.dll"));
        Assert.Equal("plugins/m2-renamed.dll", ledger.RowsOf("m2")[0].AssemblyRelativePath);
        Assert.True(ledger.Register("d", 4, "m3", "plugins/m3.dll"));
        Assert.Equal(ledger.Rows.Select(r => r.PluginId).Distinct(), ledger.Owners);
        foreach (var owner in ledger.Owners)
            Assert.Equal(ledger.Rows.Where(r => r.PluginId == owner), ledger.RowsOf(owner));
        Assert.Empty(ledger.RowsOf("absent"));
        Assert.False(ledger.MarkRemoved("absent"));
    }

    [Fact]
    public void Large_ledger_loads_once_per_change()
    {
        var big = new ItemOwnerLedger();
        for (int owner = 0; owner < 80; owner++)
            for (int item = 0; item < 128; item++)
                Assert.True(big.Register($"scale.p{owner:D3}.item{item}", owner * 1000 + item + 1, $"content.scale.p{owner:D3}",
                    $"progenitor-content/scale.p{owner:D3}/pack.json"));
        for (int owner = 0; owner < 80; owner++) Assert.True(big.MarkRemoved($"content.scale.p{owner:D3}"));
        Directory.CreateDirectory(Path.GetDirectoryName(FileName)!);
        Assert.True(big.Save(FileName));
        Age(TimeSpan.FromMinutes(1));
        for (int i = 0; i < 50; i++)
        {
            var ledger = ItemOwnerLedger.Load(FileName);
            Assert.Equal(10_240, ledger.Rows.Count);
            Assert.True(ItemOwnerLedger.RequestRemovalForFile(_root, Path.Combine(_root, "progenitor-content/scale.p007/pack.json")));
        }
        Assert.Equal(1, ItemOwnerLedger.ParseCount);
        Assert.Equal(1, ItemOwnerLedger.ReadCount);
    }
}

public sealed class ItemOwnerDiskPluginsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "progenitor-owner-disk-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private static ItemOwnerLedger Ledger(params (string Owner, string Path)[] rows)
    {
        var ledger = new ItemOwnerLedger();
        int guid = 1;
        foreach (var r in rows) Assert.True(ledger.Register(r.Owner + "/item" + guid, guid++, r.Owner, r.Path));
        return ledger;
    }

    private void Touch(string relative)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
    }

    [Fact]
    public void Provenance_files_and_staged_copies_count_as_present()
    {
        var ledger = Ledger(("a", "progenitor-reload/a.dll"), ("b", "progenitor-reload/b.dll"), ("c", "progenitor-content/c/pack.json"));
        Touch("progenitor-reload/b.dll.next");
        var reads = new Dictionary<string, IReadOnlyList<string>?>(StringComparer.OrdinalIgnoreCase)
        {
            [Path.Combine(_root, "progenitor-reload", "b.dll.next")] = Array.Empty<string>(),
        };
        var disk = new ItemOwnerDiskPlugins(_root, f => reads[f]);
        Assert.True(disk.Absent(ledger, "a"));
        Assert.False(disk.Absent(ledger, "b"));
        Assert.True(disk.Absent(ledger, "c"));
        Touch("progenitor-content/c/pack.json");
        Assert.False(new ItemOwnerDiskPlugins(_root, f => reads.TryGetValue(f, out var g) ? g : Array.Empty<string>()).Absent(ledger, "c"));
    }

    [Fact]
    public void A_dll_declaring_the_owner_counts_as_present_and_the_folders_are_walked_once()
    {
        var ledger = Ledger(("a", "plugins/old-a.dll"), ("b", "plugins/old-b.dll"), ("c", "plugins/old-c.dll"));
        Touch("plugins/sub/renamed.dll");
        Touch("progenitor-pending/other.dll");
        int reads = 0;
        var disk = new ItemOwnerDiskPlugins(_root, f =>
        {
            reads++;
            return f.EndsWith("renamed.dll", StringComparison.Ordinal) ? new[] { "b" } : new[] { "z" };
        });
        Assert.True(disk.Absent(ledger, "a"));
        Assert.False(disk.Absent(ledger, "b"));
        Assert.True(disk.Absent(ledger, "c"));
        Assert.Equal(1, disk.Walks);
        Assert.Equal(2, reads);
    }

    [Fact]
    public void An_unreadable_dll_blocks_every_owner()
    {
        var ledger = Ledger(("a", "plugins/a.dll"), ("b", "plugins/b.dll"));
        Touch("progenitor-reload/broken.dll");
        var disk = new ItemOwnerDiskPlugins(_root, _ => null);
        Assert.False(disk.Absent(ledger, "a"));
        Assert.False(disk.Absent(ledger, "b"));
    }

    [Fact]
    public void Too_many_files_block_every_owner()
    {
        var ledger = Ledger(("a", "plugins/a.dll"));
        Directory.CreateDirectory(Path.Combine(_root, "plugins"));
        for (int i = 0; i <= ItemOwnerDiskPlugins.MaxFiles; i++)
            File.WriteAllText(Path.Combine(_root, "plugins", "f" + i + ".dll"), "");
        Assert.False(new ItemOwnerDiskPlugins(_root, _ => Array.Empty<string>()).Absent(ledger, "a"));
    }
}
