using System;
using System.Collections.Generic;
using System.Text.Json;
using Progenitor.Inventory;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Game-free coverage of the P5 container-id book: text forms, issue and reuse rules, restart
/// (save, load, prime), destruction and replacement, and the saved document.
/// </summary>
public class ContainerIdTests
{
    private static readonly ContainerAnchor ChestA = new(13, 279811010, 2785, 2985, 10, 0);
    private static readonly ContainerAnchor ChestB = new(13, 279811010, 2790, 2985, 10, 1);

    private readonly HashSet<(int, int)> _live = new();

    private bool IsLive(int index, int version) => _live.Contains((index, version));

    private ContainerSighting Seen(ContainerAnchor anchor, int index, int version = 1)
    {
        _live.Add((index, version));
        return new ContainerSighting(anchor, index, version);
    }

    [Fact]
    public void Text_forms_round_trip()
    {
        var id = ContainerId.New();
        Assert.StartsWith("c-", id.ToString());
        Assert.Equal(34, id.ToString().Length);
        Assert.True(ContainerId.TryParse(id.ToString(), out var back));
        Assert.Equal(id, back);
        Assert.True(ContainerId.TryParse(id.Value.ToString("N"), out back));
        Assert.False(ContainerId.TryParse("c-00000000000000000000000000000000", out _));
        Assert.False(ContainerId.TryParse("chest", out _));
        Assert.Equal("none", ContainerId.None.ToString());

        Assert.True(ContainerAnchor.TryParse(ChestA.Key, out var anchor));
        Assert.Equal(ChestA, anchor);
        Assert.True(ContainerAnchor.TryParse(new ContainerAnchor(-1, -5, -3, 4, 0, 3).Key, out anchor));
        Assert.Equal(-5, anchor.PrefabGuid);
        Assert.False(ContainerAnchor.TryParse("t1:p2:3:h4:r5", out _));
    }

    [Fact]
    public void Same_container_keeps_its_id_and_a_different_one_gets_another()
    {
        var book = new ContainerIdRegistry();
        var a = book.IdFor(Seen(ChestA, 100), 1, IsLive);
        var again = book.IdFor(Seen(ChestA, 100), 1, IsLive);
        var b = book.IdFor(Seen(ChestB, 101), 1, IsLive);
        Assert.Equal(a, again);
        Assert.NotEqual(a, b);
        Assert.Equal(ContainerIdStatus.Live, book.Status(a, 1));
        Assert.True(book.TryEntity(a, 1, out int index, out _, out _));
        Assert.Equal(100, index);
        Assert.True(book.TakeDirty());
    }

    [Fact]
    public void Ids_survive_a_restart_through_the_saved_document()
    {
        var book = new ContainerIdRegistry();
        var a = book.IdFor(Seen(ChestA, 100), 1, IsLive);
        var b = book.IdFor(Seen(ChestB, 101), 1, IsLive);
        string json = JsonSerializer.Serialize(book.Save());

        // Restart: new process, entity ids re-assigned.
        _live.Clear();
        var fresh = new ContainerIdRegistry();
        Assert.Equal(0, fresh.Load(JsonSerializer.Deserialize<ContainerIdDocument>(json)));
        Assert.Equal(ContainerIdStatus.Unbound, fresh.Status(a, 1));
        var (bound, retired) = fresh.Prime(1, new[] { Seen(ChestA, 555), Seen(ChestB, 556) });
        Assert.Equal(2, bound);
        Assert.Equal(0, retired);
        Assert.Equal(a, fresh.IdFor(Seen(ChestA, 555), 1, IsLive));
        Assert.Equal(b, fresh.IdFor(Seen(ChestB, 556), 1, IsLive));
        Assert.False(fresh.Dirty);
    }

    [Fact]
    public void A_destroyed_container_is_retired_and_its_tile_gets_a_new_id()
    {
        var book = new ContainerIdRegistry();
        var a = book.IdFor(Seen(ChestA, 100), 1, IsLive);
        _live.Remove((100, 1));
        Assert.Equal(1, book.Sweep(1, IsLive));
        Assert.Equal(ContainerIdStatus.Gone, book.Status(a, 1));
        var replacement = book.IdFor(Seen(ChestA, 200), 1, IsLive);
        Assert.NotEqual(a, replacement);
    }

    [Fact]
    public void A_replacement_seen_before_the_sweep_still_gets_a_new_id()
    {
        var book = new ContainerIdRegistry();
        var a = book.IdFor(Seen(ChestA, 100), 1, IsLive);
        _live.Remove((100, 1)); // destroyed, no sweep yet
        var replacement = book.IdFor(Seen(ChestA, 200), 1, IsLive);
        Assert.NotEqual(a, replacement);
        Assert.Equal(ContainerIdStatus.Gone, book.Status(a, 1));
    }

    [Fact]
    public void Two_live_models_on_one_anchor_never_share_an_id()
    {
        var book = new ContainerIdRegistry();
        var a = book.IdFor(Seen(ChestA, 100), 1, IsLive);
        Assert.Equal(ContainerId.None, book.IdFor(Seen(ChestA, 101), 1, IsLive));
        Assert.Equal(a, book.IdFor(Seen(ChestA, 100), 1, IsLive));
    }

    [Fact]
    public void Prime_retires_ids_whose_tile_holds_nothing_after_a_load()
    {
        var book = new ContainerIdRegistry();
        var a = book.IdFor(Seen(ChestA, 100), 1, IsLive);
        var b = book.IdFor(Seen(ChestB, 101), 1, IsLive);
        var doc = book.Save();
        var fresh = new ContainerIdRegistry();
        fresh.Load(doc);
        _live.Clear();
        var (bound, retired) = fresh.Prime(2, new[] { Seen(ChestA, 300) });
        Assert.Equal(1, bound);
        Assert.Equal(1, retired);
        Assert.Equal(ContainerIdStatus.Live, fresh.Status(a, 2));
        Assert.Equal(ContainerIdStatus.Gone, fresh.Status(b, 2));
        Assert.Equal(2, fresh.PrimedEpoch);
        Assert.Contains(b.ToString(), fresh.Save().Retired);
    }

    [Fact]
    public void A_moved_model_keeps_its_id_and_frees_its_old_tile()
    {
        var book = new ContainerIdRegistry();
        var a = book.IdFor(Seen(ChestA, 100), 1, IsLive);
        var moved = book.IdFor(Seen(ChestB, 100), 1, IsLive); // same entity, new tile
        Assert.Equal(a, moved); // a moved model keeps its id
        Assert.True(book.TryAnchor(a, out var anchor));
        Assert.Equal(ChestB, anchor);
        var newcomer = book.IdFor(Seen(ChestA, 101), 1, IsLive); // the old tile is free
        Assert.False(newcomer.IsNone);
        Assert.NotEqual(a, newcomer);
        Assert.Equal(2, book.Count);
    }

    [Fact]
    public void The_sweep_follows_a_moved_model_so_a_chest_placed_on_its_old_tile_never_inherits_its_id()
    {
        var book = new ContainerIdRegistry();
        var a = book.IdFor(Seen(ChestA, 100), 1, IsLive);
        // The model moves to ChestB's tile without anyone asking for its id.
        var where = new Dictionary<int, ContainerAnchor> { [100] = ChestB };
        book.Sweep(1, IsLive, (recorded, index, _) => where.TryGetValue(index, out var now) ? now : recorded);
        Assert.True(book.TryAnchor(a, out var anchor));
        Assert.Equal(ChestB, anchor);

        // Restart: a new chest stands on the old tile, the moved one on its new tile.
        var fresh = new ContainerIdRegistry();
        fresh.Load(book.Save());
        _live.Clear();
        fresh.Prime(2, new[] { Seen(ChestA, 300), Seen(ChestB, 301) });
        Assert.Equal(a, fresh.IdFor(Seen(ChestB, 301), 2, IsLive));
        Assert.NotEqual(a, fresh.IdFor(Seen(ChestA, 300), 2, IsLive));
    }

    [Fact]
    public void Validate_retires_one_dead_id()
    {
        var book = new ContainerIdRegistry();
        var a = book.IdFor(Seen(ChestA, 100), 1, IsLive);
        var b = book.IdFor(Seen(ChestB, 101), 1, IsLive);
        _live.Remove((100, 1));
        book.Validate(a, 1, IsLive);
        book.Validate(b, 1, IsLive);
        Assert.Equal(ContainerIdStatus.Gone, book.Status(a, 1));
        Assert.Equal(ContainerIdStatus.Live, book.Status(b, 1));
    }

    [Fact]
    public void A_prefab_change_on_the_same_tile_is_a_different_container()
    {
        var book = new ContainerIdRegistry();
        var a = book.IdFor(Seen(ChestA, 100), 1, IsLive);
        var other = new ContainerAnchor(ChestA.TerritoryIndex, 999, ChestA.TileX, ChestA.TileY, ChestA.HeightLevel, ChestA.Rotation);
        _live.Remove((100, 1)); // the old model was replaced by a different prefab
        Assert.NotEqual(a, book.IdFor(Seen(other, 101), 1, IsLive));
    }

    [Fact]
    public void Unknown_ids_and_bad_rows_are_handled()
    {
        var book = new ContainerIdRegistry();
        Assert.Equal(ContainerIdStatus.Unknown, book.Status(ContainerId.New(), 1));
        var doc = new ContainerIdDocument();
        doc.Live.Add(new ContainerIdRow { Id = "nope", Anchor = ChestA.Key });
        doc.Live.Add(new ContainerIdRow { Id = ContainerId.New().ToString(), Anchor = "bad" });
        doc.Retired.Add("bad");
        Assert.Equal(3, book.Load(doc));
        Assert.Equal(0, book.Count);
    }

    [Fact]
    public void The_retired_list_is_capped()
    {
        var book = new ContainerIdRegistry();
        for (int i = 0; i < ContainerIdRegistry.MaxRetired + 5; i++)
        {
            var anchor = new ContainerAnchor(1, 1, i, 0, 0, 0);
            book.IdFor(Seen(anchor, i + 1), 1, IsLive);
            _live.Remove((i + 1, 1));
        }

        book.Sweep(1, IsLive);
        Assert.Equal(ContainerIdRegistry.MaxRetired, book.RetiredCount);
        Assert.Equal(0, book.Count);
    }
}
