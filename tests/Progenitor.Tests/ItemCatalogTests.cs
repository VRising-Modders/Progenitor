using System;
using System.Collections.Generic;
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
        public int Collects { get; private set; }
        public bool Throw { get; set; }
        public List<ItemCatalogRow> Rows { get; } = new();

        public void Collect(List<ItemCatalogRow> into)
        {
            Collects++;
            if (Throw) throw new InvalidOperationException("prefab read failed");
            into.AddRange(Rows);
        }
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
        Assert.Empty(cache.Entries());
        Assert.Equal(2, s.Collects);
        Assert.False(cache.Info.Built);

        s.Rows.Add(new ItemCatalogRow(IronOre, "Item_Ingredient_Mineral_IronOre", null, 500, 0, 1, false));
        Assert.Single(cache.Entries());
        Assert.Single(cache.Entries());
        Assert.Equal(3, s.Collects);
    }

    [Fact]
    public void A_throwing_source_yields_empty_and_retries()
    {
        var s = ServerWorld();
        s.Throw = true;
        var cache = new ItemCatalogCache(s);
        Assert.Empty(cache.Entries());
        s.Throw = false;
        Assert.Equal(3, cache.Entries().Count);
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
