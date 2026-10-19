using System;
using System.Collections.Generic;
using System.IO;
using Progenitor.Items;
using Progenitor.Sample;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// <c>ctx.Items.Catalog()</c> without the game: a fake prefab source stands in for the world.
/// Covers normalization (GUID 0, duplicates, order, blank names), the build-once cache and every
/// rebuild trigger (world object, epoch, content version, invalidate), the not-cached empty
/// result, the by-GUID lookup and the sample's reply.
/// </summary>
public class ItemCatalogTests
{
    private const int IronOre = 1332980397;
    private const int Copper = 1000;
    private const int Custom = 2000;

    private sealed class FakeSource : IItemCatalogSource
    {
        public object? World { get; set; } = new object();
        public string Side { get; set; } = "server";
        public int Epoch { get; set; } = 1;
        public int ContentVersion { get; set; }
        public int Frame { get; set; } = 1;
        public bool Ready { get; set; } = true;
        public int Collects { get; private set; }
        public bool Throw { get; set; }
        public List<ItemCatalogRow> Rows { get; } = new();
        public List<string> Warnings { get; } = new();

        public void Collect(List<ItemCatalogRow> into)
        {
            Collects++;
            if (Throw) throw new InvalidOperationException("prefab read failed");
            into.AddRange(Rows);
        }

        public void Warn(string message) => Warnings.Add(message);
    }

    private static FakeSource ServerWorld()
    {
        var s = new FakeSource();
        s.Rows.Add(new ItemCatalogRow(IronOre, "Item_Ingredient_Mineral_IronOre", null, 500, 0x10, 1, false));
        s.Rows.Add(new ItemCatalogRow(Copper, "Item_Ingredient_Mineral_CopperOre", null, 500, 0x10, 1, false));
        s.Rows.Add(new ItemCatalogRow(Custom, "progenitor_sample_ring", "Sample Ring", 1, 0x20, 2, true));
        return s;
    }

    [Fact]
    public void Builds_items_sorted_by_prefab_name_with_their_facts()
    {
        var cache = new ItemCatalogCache(ServerWorld());
        var list = cache.Entries();

        Assert.Equal(3, list.Count);
        Assert.Equal("Item_Ingredient_Mineral_CopperOre", list[0].PrefabName);
        Assert.Equal("Item_Ingredient_Mineral_IronOre", list[1].PrefabName);
        Assert.Equal("progenitor_sample_ring", list[2].PrefabName);
        var ore = list[1];
        Assert.Equal(IronOre, ore.PrefabGuid);
        Assert.Equal(500, ore.MaxAmount);
        Assert.Equal(0x10L, ore.ItemCategory);
        Assert.Equal(1, ore.ItemType);
        Assert.False(ore.IsCustom);
    }

    [Fact]
    public void Server_vanilla_items_have_null_display_names_and_custom_items_keep_theirs()
    {
        var cache = new ItemCatalogCache(ServerWorld());
        Assert.Null(cache.Find(IronOre)!.DisplayName);
        Assert.Equal("Sample Ring", cache.Find(Custom)!.DisplayName);
        Assert.True(cache.Find(Custom)!.IsCustom);

        var info = cache.Info;
        Assert.True(info.Built);
        Assert.Equal("server", info.Side);
        Assert.Equal(3, info.Count);
        Assert.Equal(1, info.WithDisplayName);
        Assert.Equal(1, info.Custom);
    }

    [Fact]
    public void Blank_display_names_become_null_and_names_are_trimmed()
    {
        var s = new FakeSource();
        s.Rows.Add(new ItemCatalogRow(1, "Item_A", "   ", 10, 0, 0, false));
        s.Rows.Add(new ItemCatalogRow(2, "Item_B", "", 10, 0, 0, false));
        s.Rows.Add(new ItemCatalogRow(3, null, "  Iron Ore ", 10, 0, 0, false));
        var cache = new ItemCatalogCache(s);

        Assert.Null(cache.Find(1)!.DisplayName);
        Assert.Null(cache.Find(2)!.DisplayName);
        Assert.Equal("Iron Ore", cache.Find(3)!.DisplayName);
        Assert.Equal("", cache.Find(3)!.PrefabName);
        Assert.Equal(1, cache.Info.WithDisplayName);
    }

    [Fact]
    public void Guid_zero_is_dropped_and_the_first_row_of_a_guid_wins()
    {
        var s = new FakeSource();
        s.Rows.Add(new ItemCatalogRow(0, "Item_Zero", null, 1, 0, 0, false));
        s.Rows.Add(new ItemCatalogRow(5, "Item_First", null, 7, 0, 0, false));
        s.Rows.Add(new ItemCatalogRow(5, "Item_Second", null, 9, 0, 0, false));
        var cache = new ItemCatalogCache(s);

        Assert.Single(cache.Entries());
        Assert.Equal("Item_First", cache.Find(5)!.PrefabName);
        Assert.Equal(7, cache.Find(5)!.MaxAmount);
        Assert.Null(cache.Find(0));
        Assert.Null(cache.Find(12345));
    }

    [Fact]
    public void Repeated_calls_reuse_one_build()
    {
        var s = ServerWorld();
        var cache = new ItemCatalogCache(s);
        var first = cache.Entries();
        for (int i = 0; i < 50; i++)
        {
            Assert.Same(first, cache.Entries());
            Assert.NotNull(cache.Find(IronOre));
        }
        Assert.Equal(1, s.Collects);
        Assert.Equal(1, cache.Info.Builds);
    }

    [Fact]
    public void A_new_epoch_rebuilds_and_drops_items_that_are_gone()
    {
        var s = ServerWorld();
        var cache = new ItemCatalogCache(s);
        var first = cache.Entries();
        Assert.NotNull(cache.Find(Custom));

        s.Epoch = 2;
        s.Rows.RemoveAt(2);
        var second = cache.Entries();

        Assert.NotSame(first, second);
        Assert.Equal(2, second.Count);
        Assert.Null(cache.Find(Custom));
        Assert.Equal(2, s.Collects);
        Assert.Equal(2, cache.Info.Epoch);
    }

    [Fact]
    public void A_new_world_object_rebuilds_even_at_the_same_epoch()
    {
        var s = ServerWorld();
        var cache = new ItemCatalogCache(s);
        cache.Entries();
        s.World = new object();
        cache.Entries();
        Assert.Equal(2, s.Collects);
    }

    [Fact]
    public void A_custom_item_change_rebuilds_and_shows_the_new_item()
    {
        var s = ServerWorld();
        var cache = new ItemCatalogCache(s);
        Assert.Equal(3, cache.Entries().Count);

        s.Rows.Add(new ItemCatalogRow(3000, "progenitor_sample_cape", "Sample Cape", 1, 0, 2, true));
        Assert.Equal(3, cache.Entries().Count); // unchanged version: still the cached list
        s.ContentVersion++;
        Assert.Equal(4, cache.Entries().Count);
        Assert.Equal("Sample Cape", cache.Find(3000)!.DisplayName);
    }

    [Fact]
    public void Invalidate_forces_one_rebuild()
    {
        var s = ServerWorld();
        var cache = new ItemCatalogCache(s);
        cache.Entries();
        cache.Invalidate();
        cache.Entries();
        cache.Entries();
        Assert.Equal(2, s.Collects);
    }

    [Fact]
    public void No_world_is_empty_and_never_collects()
    {
        var s = ServerWorld();
        s.World = null;
        var cache = new ItemCatalogCache(s);

        Assert.Empty(cache.Entries());
        Assert.Null(cache.Find(IronOre));
        Assert.Equal(0, s.Collects);
        Assert.False(cache.Info.Built);

        s.World = new object();
        Assert.Equal(3, cache.Entries().Count);
    }

    [Fact]
    public void Leaving_the_world_drops_the_catalog()
    {
        var s = ServerWorld();
        var cache = new ItemCatalogCache(s);
        Assert.Equal(3, cache.Entries().Count);
        s.World = null;
        Assert.Empty(cache.Entries());
        Assert.Null(cache.Find(IronOre));
    }

    [Fact]
    public void An_empty_build_is_not_cached_so_a_world_still_loading_is_read_again()
    {
        var s = new FakeSource();
        var cache = new ItemCatalogCache(s);
        Assert.Empty(cache.Entries());
        s.Frame++;
        Assert.Empty(cache.Entries());
        Assert.Equal(2, s.Collects);
        Assert.False(cache.Info.Built);

        s.Rows.Add(new ItemCatalogRow(IronOre, "Item_Ingredient_Mineral_IronOre", null, 500, 0, 1, false));
        s.Frame++;
        Assert.Single(cache.Entries());
        s.Frame++;
        Assert.Single(cache.Entries());
        Assert.Equal(3, s.Collects);
    }

    [Fact]
    public void An_empty_build_is_retried_at_most_once_per_frame()
    {
        var s = new FakeSource();
        var cache = new ItemCatalogCache(s);
        for (int i = 0; i < 20; i++)
        {
            Assert.Empty(cache.Entries());
            Assert.Null(cache.Find(IronOre));
        }
        Assert.Equal(1, s.Collects);

        s.Frame++;
        cache.Entries();
        Assert.Equal(2, s.Collects);

        // A new epoch or content version is a new key: it builds in the same frame.
        s.Epoch++;
        cache.Entries();
        Assert.Equal(3, s.Collects);

        // So does an explicit invalidate.
        cache.Invalidate();
        cache.Entries();
        Assert.Equal(4, s.Collects);
    }

    [Fact]
    public void A_throwing_source_yields_empty_warns_once_per_epoch_and_retries_next_frame()
    {
        var s = ServerWorld();
        s.Throw = true;
        var cache = new ItemCatalogCache(s);
        Assert.Empty(cache.Entries());
        Assert.Empty(cache.Entries());
        Assert.Equal(1, s.Collects);
        s.Frame++;
        Assert.Empty(cache.Entries());
        Assert.Equal(2, s.Collects);
        Assert.Single(s.Warnings);
        Assert.Contains("prefab read failed", s.Warnings[0]);

        s.Epoch++;
        Assert.Empty(cache.Entries());
        Assert.Equal(2, s.Warnings.Count);

        s.Throw = false;
        s.Frame++;
        Assert.Equal(3, cache.Entries().Count);
        Assert.Equal(2, s.Warnings.Count);
    }

    [Fact]
    public void A_build_before_the_side_is_ready_is_served_but_not_cached()
    {
        var s = ServerWorld();
        s.Side = "client";
        s.Ready = false;
        var cache = new ItemCatalogCache(s);

        Assert.Equal(3, cache.Entries().Count);
        Assert.Equal(3, cache.Entries().Count);
        Assert.Equal(1, s.Collects); // same frame: served from the last build

        s.Rows.Add(new ItemCatalogRow(3000, "Item_Late", null, 1, 0, 0, false));
        s.Frame++;
        Assert.Equal(4, cache.Entries().Count); // still loading: rebuilt on a later frame
        Assert.Equal(2, s.Collects);

        s.Ready = true;
        s.Frame++;
        cache.Entries();
        s.Frame++;
        cache.Entries();
        Assert.Equal(3, s.Collects); // cached once ready
    }

    [Fact]
    public void Info_is_cleared_when_the_world_is_gone()
    {
        var s = ServerWorld();
        var cache = new ItemCatalogCache(s);
        cache.Entries();
        Assert.True(cache.Info.Built);

        s.World = null;
        cache.Entries();
        var info = cache.Info;
        Assert.False(info.Built);
        Assert.Equal(0, info.Count);
        Assert.Equal(1, info.Builds);
    }

    [Fact]
    public void Entity_item_data_wins_over_a_stale_lookup_row()
    {
        var table = new ItemCatalogFacts(100, 0x10, 1);
        var entity = new ItemCatalogFacts(250, 0x30, 2);

        Assert.True(ItemCatalogFacts.TryPick(true, table, true, entity, false, out var f));
        Assert.Equal(250, f.MaxAmount);
        Assert.Equal(0x30L, f.ItemCategory);
        Assert.Equal(2, f.ItemType);

        // Entity read failed: the table row is the fallback.
        Assert.True(ItemCatalogFacts.TryPick(true, table, false, default, false, out f));
        Assert.Equal(100, f.MaxAmount);

        // Not in the table: a vanilla prefab is not listed, an applied custom item is.
        Assert.False(ItemCatalogFacts.TryPick(false, default, true, entity, false, out _));
        Assert.True(ItemCatalogFacts.TryPick(false, default, true, entity, true, out f));
        Assert.Equal(250, f.MaxAmount);
        Assert.False(ItemCatalogFacts.TryPick(false, default, false, default, true, out _));
    }

    // ---- instance entity (ItemData.Entity) ---------------------------------------------------------

    [Theory]
    [InlineData(0, 0, false)]  // Entity.Null: the game's add leaves the slot without an item entity
    [InlineData(812, 1, true)] // set: the add instantiates it for the slot
    [InlineData(0, 1, true)]   // any non-null half counts, as in the game's test
    [InlineData(5, 0, true)]
    public void Instance_entity_is_item_data_entity_not_null(int index, int version, bool expected) =>
        Assert.Equal(expected, ItemInstanceRule.FromItemDataEntity(index, version));

    [Fact]
    public void Instance_entity_comes_from_the_table_row_first_then_the_prefab_then_assumes_one()
    {
        // The table row is what the game's add reads: it wins over the prefab in both directions.
        Assert.False(ItemInstanceRule.Pick(inTable: true, tableEntitySet: false, prefabRead: true, prefabEntitySet: true));
        Assert.True(ItemInstanceRule.Pick(inTable: true, tableEntitySet: true, prefabRead: true, prefabEntitySet: false));
        Assert.True(ItemInstanceRule.Pick(inTable: true, tableEntitySet: true, prefabRead: false, prefabEntitySet: false));
        // No row (an applied custom item the client table missed): the prefab's own field.
        Assert.False(ItemInstanceRule.Pick(false, false, true, false));
        Assert.True(ItemInstanceRule.Pick(false, false, true, true));
        // Nothing read: the conservative answer.
        Assert.True(ItemInstanceRule.Pick(false, false, false, false));
    }

    [Fact]
    public void Catalog_entries_carry_the_instance_flag_independent_of_stack_size()
    {
        const int Book = 1383064284, Sword = 3000, Jewel = 3001, Ore = 3002;
        var s = new FakeSource();
        // A blueprint book: stack size 1, ItemType Tech (5), category 4608, no instance entity.
        s.Rows.Add(new ItemCatalogRow(Book, "Item_Ingredient_Book_Structure_Carpets_T01", null, 1, 4608, 5, false, hasInstanceEntity: false));
        s.Rows.Add(new ItemCatalogRow(Sword, "Item_Weapon_Sword_T01_Bone", null, 1, 1, 2, false, hasInstanceEntity: true));
        s.Rows.Add(new ItemCatalogRow(Jewel, "Item_Jewel_Test", null, 1, 0, 8, false, hasInstanceEntity: true));
        s.Rows.Add(new ItemCatalogRow(Ore, "Item_Ingredient_Mineral_Test", null, 500, 0x10, 0, false));
        var cache = new ItemCatalogCache(s);

        Assert.False(cache.Find(Book)!.HasInstanceEntity);
        Assert.Equal(1, cache.Find(Book)!.MaxAmount);
        Assert.True(cache.Find(Sword)!.HasInstanceEntity);
        Assert.True(cache.Find(Jewel)!.HasInstanceEntity);
        Assert.False(cache.Find(Ore)!.HasInstanceEntity);
        Assert.False(new ItemCatalogEntry(1, "Item_A", null, 1, 0, 0, false).HasInstanceEntity);
    }

    [Fact]
    public void Item_data_edits_bump_the_catalog_version()
    {
        int before = ItemCatalogVersion.Current;
        ItemCatalogVersion.Bump();
        Assert.NotEqual(before, ItemCatalogVersion.Current);
    }

    /// <summary>
    /// Every Core write to the item lookup table sits next to an <c>ItemCatalogVersion.Bump()</c>
    /// (within the 30 lines before or the 4 after), so a new writer cannot leave catalogs stale.
    /// </summary>
    [Fact]
    public void Every_item_lookup_table_write_in_core_bumps_the_catalog_version()
    {
        string root = Path.Combine(SourcePin.RepoRoot(), "src", "Progenitor.Core");
        var missing = new List<string>();
        int writes = 0;
        foreach (var file in Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                string code = lines[i].TrimStart();
                if (code.StartsWith("//", StringComparison.Ordinal)) continue;
                if (!code.Contains("ItemHashLookupMap.Remove(", StringComparison.Ordinal)
                    && !code.Contains("ItemHashLookupMap.TryAdd(", StringComparison.Ordinal))
                    continue;
                writes++;
                bool bumped = false;
                for (int j = Math.Max(0, i - 30); j <= Math.Min(lines.Length - 1, i + 4) && !bumped; j++)
                    bumped = lines[j].Contains("ItemCatalogVersion.Bump()", StringComparison.Ordinal);
                if (!bumped) missing.Add(Path.GetFileName(file) + ":" + (i + 1));
            }
        }

        Assert.True(writes > 0, "no item lookup table writes found; the scan is looking in the wrong place");
        Assert.True(missing.Count == 0, "item lookup table writes without a catalog version bump: " + string.Join(", ", missing));
    }

    [Fact]
    public void The_list_cannot_be_changed_through_a_cast()
    {
        var cache = new ItemCatalogCache(ServerWorld());
        var list = cache.Entries();
        Assert.False(list is List<ItemCatalogEntry>);
        Assert.Throws<NotSupportedException>(() => ((IList<ItemCatalogEntry>)list).Add(list[0]));
    }

    [Fact]
    public void Matches_by_guid_prefab_name_or_display_name()
    {
        var ring = new ItemCatalogEntry(Custom, "progenitor_sample_ring", "Sample Ring", 1, 0, 2, true);
        Assert.True(ring.Matches(null));
        Assert.True(ring.Matches("  "));
        Assert.True(ring.Matches("2000"));
        Assert.True(ring.Matches("SAMPLE_RING"));
        Assert.True(ring.Matches("ring"));
        Assert.True(ring.Matches("sample ring"));
        Assert.False(ring.Matches("ore"));

        var ore = new ItemCatalogEntry(IronOre, "Item_Ingredient_Mineral_IronOre", null, 500, 0, 1, false);
        Assert.True(ore.Matches(IronOre.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        Assert.False(ore.Matches("Iron Ore")); // no display name on the server
    }

    [Fact]
    public void Sample_reply_summarizes_then_lists_a_few_rows()
    {
        var cache = new ItemCatalogCache(ServerWorld());
        var lines = SampleItemCatalog.Lines(cache.Entries(), cache.Info, "ore");

        Assert.StartsWith("Item catalog (server): 3 items, 1 with display names, 1 custom, built in ", lines[0]);
        Assert.EndsWith("2 match \"ore\".", lines[0]);
        Assert.Equal(3, lines.Count);
        Assert.Equal("1000 Item_Ingredient_Mineral_CopperOre stack 500 type 1 cat 0x10", lines[1]);
        Assert.Equal(IronOre + " Item_Ingredient_Mineral_IronOre stack 500 type 1 cat 0x10", lines[2]);

        var ring = SampleItemCatalog.Lines(cache.Entries(), cache.Info, "Sample Ring");
        Assert.Equal("2000 progenitor_sample_ring \"Sample Ring\" stack 1 type 2 cat 0x20 custom", ring[1]);
    }

    [Fact]
    public void Sample_reply_caps_rows_and_explains_an_empty_catalog()
    {
        var s = new FakeSource();
        for (int i = 1; i <= 20; i++)
            s.Rows.Add(new ItemCatalogRow(i, "Item_" + i.ToString("00", System.Globalization.CultureInfo.InvariantCulture), null, 1, 0, 0, false));
        var cache = new ItemCatalogCache(s);
        var lines = SampleItemCatalog.Lines(cache.Entries(), cache.Info, "");
        Assert.Equal(1 + SampleItemCatalog.MaxRows + 1, lines.Count);
        Assert.Equal("... 12 more; narrow the filter.", lines[lines.Count - 1]);

        var empty = SampleItemCatalog.Lines(Array.Empty<ItemCatalogEntry>(), default, null);
        Assert.Single(empty);
        Assert.Contains("empty", empty[0]);
    }
}
