using System;
using System.IO;
using Progenitor.Content;
using Xunit;

namespace Progenitor.Tests;

public sealed class ItemOwnerLedgerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "progenitor-owner-tests-" + Guid.NewGuid().ToString("N"));
    private string FileName => Path.Combine(_root, ItemOwnerLedger.RelativeFileName);
    private static ItemOwnerLedger Owned()
    {
        var ledger = new ItemOwnerLedger();
        Assert.True(ledger.Register("outfit", 123, "mod", "progenitor-reload/mod.dll"));
        return ledger;
    }

    [Fact]
    public void Missing_registration_reload_and_failed_load_preserve_items()
    {
        var ledger = Owned();
        Assert.Empty(ledger.Eligible("mod", false, false, false, false));
        Assert.Empty(ledger.Eligible("mod", false, true, false, true));
        Assert.Empty(ledger.Eligible("mod", false, false, false, true, false));
        Assert.Single(ledger.Rows);
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void Active_present_and_staged_owners_protect_even_explicit_removal(bool active, bool present, bool staged)
    {
        var ledger = Owned(); ledger.MarkRemoved("mod");
        Assert.Empty(ledger.Eligible("mod", active, present, staged, true));
    }

    [Fact]
    public void Removal_and_history_roundtrip_and_reinstall_clears_intent()
    {
        var ledger = Owned();
        Assert.True(ledger.Register("new-outfit", 124, "mod", "progenitor-reload/mod.dll"));
        Assert.True(ledger.MarkRemoved("mod")); Assert.True(ledger.Save(FileName));
        ledger = ItemOwnerLedger.Load(FileName);
        Assert.True(ledger.IsValid); Assert.Equal(2, ledger.Eligible("mod", false, false, false, false).Count);
        Assert.True(ledger.Register("new-outfit", 124, "mod", "progenitor-reload/mod.dll"));
        Assert.Empty(ledger.Eligible("mod", false, false, false, false)); Assert.Equal(2, ledger.Rows.Count);
        Assert.True(ledger.Save(FileName)); Assert.Equal(2, ItemOwnerLedger.Load(FileName).Rows.Count);
    }

    [Fact]
    public void Guid_collision_cannot_transfer_ownership()
    {
        var ledger = Owned();
        Assert.False(ledger.Register("outfit", 123, "other", "plugins/other.dll"));
        Assert.False(ledger.Register("different-id", 123, "mod", "plugins/mod.dll"));
        Assert.Equal("mod", Assert.Single(ledger.Rows).PluginId);
    }

    [Fact]
    public void Missing_data_pack_at_startup_requires_explicit_removal()
    {
        var ledger = new ItemOwnerLedger();
        Assert.True(ledger.Register("example.pack.item", 123, "content.example.pack",
            "progenitor-content/example.pack/pack.json"));
        Assert.Empty(ledger.Eligible("content.example.pack", false, false, false, true));
        Assert.True(ledger.MarkRemoved("content.example.pack"));
        Assert.Single(ledger.Eligible("content.example.pack", false, false, false, true));
    }

    [Fact]
    public void Pack_provenance_is_authoritative_even_with_an_unexpected_owner_label()
    {
        var ledger = new ItemOwnerLedger();
        Assert.True(ledger.Register("pack.item", 123, "legacy-owner", "progenitor-content/pack/pack.json"));
        Assert.Empty(ledger.Eligible("legacy-owner", false, false, false, true));
        Assert.Single(Owned().Eligible("mod", false, false, false, true));
    }

    [Theory]
    [InlineData("../mod.dll")]
    [InlineData("C:/mod.dll")]
    [InlineData("/mod.dll")]
    [InlineData("plugins/../mod.dll")]
    [InlineData("plugins//mod.dll")]
    public void Unsafe_paths_refused(string path) => Assert.False(Owned().Register("new", 124, "mod", path));

    [Fact]
    public void Corrupt_ledger_never_authorizes_or_overwrites()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FileName)!);
        File.WriteAllText(FileName, "{bad");
        var ledger = ItemOwnerLedger.Load(FileName);
        Assert.False(ledger.IsValid); Assert.False(ledger.MarkRemoved("mod"));
        Assert.False(ledger.Register("x", 1, "mod", "plugins/mod.dll"));
        Assert.Empty(ledger.Eligible("mod", false, false, false, true));
        Assert.False(ledger.Save(FileName)); Assert.Equal("{bad", File.ReadAllText(FileName));
    }

    [Fact]
    public void Removal_helper_matches_only_owned_file_and_rejects_escape()
    {
        Assert.True(Owned().Save(FileName));
        Assert.True(ItemOwnerLedger.RequestRemovalForFile(_root, Path.Combine(_root, "plugins/other.dll")));
        Assert.Empty(ItemOwnerLedger.Load(FileName).Eligible("mod", false, false, false, false));
        Assert.True(ItemOwnerLedger.RequestRemovalForFile(_root, Path.Combine(_root, "progenitor-reload/mod.dll")));
        Assert.Single(ItemOwnerLedger.Load(FileName).Eligible("mod", false, false, false, false));
        Assert.False(ItemOwnerLedger.RequestRemovalForFile(_root, Path.Combine(_root, "../outside.dll")));
    }

    [Fact]
    public void Failed_write_does_not_replace_existing_ledger()
    {
        Assert.True(Owned().Save(FileName));
        var ledger = ItemOwnerLedger.Load(FileName); ledger.MarkRemoved("mod");
        using (var held = new FileStream(FileName, FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.False(ledger.Save(FileName));
        Assert.Empty(ItemOwnerLedger.Load(FileName).Eligible("mod", false, false, false, false));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(FileName)!, "*.tmp"));
    }

    [Fact]
    public void Stale_writer_cannot_erase_newer_removal_intent()
    {
        Assert.True(Owned().Save(FileName));
        var stale = ItemOwnerLedger.Load(FileName);
        var current = ItemOwnerLedger.Load(FileName);
        Assert.True(current.MarkRemoved("mod")); Assert.True(current.Save(FileName));
        Assert.True(stale.Register("next", 124, "mod", "progenitor-reload/mod.dll"));
        Assert.False(stale.Save(FileName));
        Assert.Single(ItemOwnerLedger.Load(FileName).Eligible("mod", false, false, false, false));
    }

    [Fact]
    public void Source_prefab_provenance_roundtrips_and_legacy_rows_default_to_unknown()
    {
        var ledger = new ItemOwnerLedger();
        Assert.True(ledger.Register("stack", 456, "mod", "plugins/mod.dll", -789));
        Assert.True(ledger.Save(FileName));
        Assert.Equal(-789, Assert.Single(ItemOwnerLedger.Load(FileName).Rows).SourceGuid);
        File.WriteAllText(FileName, "{\"schema\":1,\"items\":[{\"itemId\":\"legacy\",\"guid\":123,\"pluginId\":\"mod\",\"assemblyRelativePath\":\"plugins/mod.dll\"}],\"removedOwners\":[]}");
        var legacy = ItemOwnerLedger.Load(FileName);
        Assert.True(legacy.IsValid); Assert.Equal(0, Assert.Single(legacy.Rows).SourceGuid);
    }

    [Fact]
    public void Large_history_load_and_replacement_preserve_identity_and_cardinality()
    {
        var ledger = new ItemOwnerLedger();
        for (int i = 1; i <= 10000; i++)
            Assert.True(ledger.Register("item." + i, i, "content.scale", "progenitor-content/scale/pack.json", 77));
        Assert.True(ledger.Save(FileName));
        ledger = ItemOwnerLedger.Load(FileName);
        Assert.True(ledger.IsValid); Assert.Equal(10000, ledger.Rows.Count);
        Assert.True(ledger.Register("item.5000", 5000, "content.scale", "progenitor-content/scale/pack.json", 88));
        Assert.Equal(10000, ledger.Rows.Count);
        Assert.Equal(88, Assert.Single(ledger.Rows, r => r.Guid == 5000).SourceGuid);
        Assert.False(ledger.Register("item.5000", 5000, "other", "plugins/other.dll"));
    }

    [Fact]
    public void Registration_batch_coalesces_updates_and_preserves_other_owners()
    {
        Assert.True(Owned().Save(FileName));
        var queue = new ItemOwnerRegistrationQueue();
        Assert.True(queue.TryEnqueue(new("new", 124, "other", "plugins/other.dll", 1)));
        Assert.True(queue.TryEnqueue(new("new", 124, "other", "plugins/other.dll", 2)));
        Assert.False(queue.TryEnqueue(new("stolen", 124, "third", "plugins/third.dll")));
        Assert.True(queue.TryEnqueue(new("stolen", 123, "other", "plugins/other.dll")));
        Assert.Equal(2, queue.Count);
        Assert.True(queue.TryFlush(FileName, out var rejected, out _));
        Assert.Equal(new[] { "other" }, rejected); Assert.Equal(0, queue.Count);
        var ledger = ItemOwnerLedger.Load(FileName);
        Assert.Equal(2, ledger.Rows.Count);
        Assert.Equal("mod", Assert.Single(ledger.Rows, r => r.Guid == 123).PluginId);
        Assert.Equal(2, Assert.Single(ledger.Rows, r => r.Guid == 124).SourceGuid);
    }

    [Fact]
    public void Failed_batch_flush_retries_every_registration_without_erasing_removal()
    {
        var ledger = Owned(); ledger.MarkRemoved("mod"); Assert.True(ledger.Save(FileName));
        var queue = new ItemOwnerRegistrationQueue();
        Assert.True(queue.TryEnqueue(new("new", 124, "other", "plugins/other.dll")));
        Assert.True(queue.TryEnqueue(new("next", 125, "other", "plugins/other.dll")));
        using (var held = new FileStream(FileName + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.False(queue.TryFlush(FileName, out _, out _));
            Assert.Equal(2, queue.Count);
        }
        Assert.True(queue.TryFlush(FileName, out var rejected, out _)); Assert.Empty(rejected);
        Assert.Equal(0, queue.Count);
        ledger = ItemOwnerLedger.Load(FileName);
        Assert.Equal(3, ledger.Rows.Count);
        Assert.Single(ledger.Eligible("mod", false, false, false, false));
    }

    [Fact]
    public void Deferred_notification_for_unloaded_owner_preserves_new_removal_intent()
    {
        var ledger = Owned(); Assert.True(ledger.Save(FileName));
        var queue = new ItemOwnerRegistrationQueue();
        Assert.True(queue.TryEnqueue(new("outfit", 123, "mod", "progenitor-reload/mod.dll", 77)));
        Assert.True(ItemOwnerLedger.RequestRemovalForFile(_root, Path.Combine(_root, "progenitor-reload/mod.dll")));
        Assert.True(queue.TryFlush(FileName, out _, out _, _ => false));
        ledger = ItemOwnerLedger.Load(FileName);
        Assert.Single(ledger.Eligible("mod", false, false, false, false));
        Assert.Equal(77, Assert.Single(ledger.Rows).SourceGuid);
        Assert.True(queue.TryEnqueue(new("outfit", 123, "mod", "progenitor-reload/mod.dll")));
        Assert.True(queue.TryFlush(FileName, out _, out _, _ => true));
        Assert.Empty(ItemOwnerLedger.Load(FileName).Eligible("mod", false, false, false, false));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
