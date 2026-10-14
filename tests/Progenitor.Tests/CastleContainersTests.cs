using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;
using Progenitor.Castles;
using Progenitor.Clans;
using Progenitor.Inventory;
using Progenitor.Sample;
using Xunit;
using CastlesApi = Progenitor.Castles.Castles;

namespace Progenitor.Tests;

/// <summary>
/// Game-free coverage of milestone P-a's castle and clan surface: container and station rows
/// with stable ids, the heart id and level table, nameplate encoding, clan membership and the
/// relation test, the territory and ready trackers, and the sample's parsing and replies.
/// Entity ids and names are placeholders.
/// </summary>
[Collection("PluginLogger")]
public class CastleContainersTests : IDisposable
{
    private static readonly CastleId Heart = new(500, 1);
    private static readonly CastleId Owner = new(600, 1);
    private static readonly CastleId Clan = new(700, 1);
    private readonly CastlesTests.FakeCastleRuntime _server = new() { IsServer = true };

    public CastleContainersTests()
    {
        Plugin.Logger = new TestLogSource();
        Progenitor.WorldState.WorldEpoch.ResetForTests();
        CastlesApi.ResetForTests();
        _server.Castles.Add(new CastleSnapshot
        {
            Heart = Heart,
            Territory = new CastleTerritoryInfo { Index = 4, Entity = new CastleId(501, 1), MinX = 0, MinY = 0, MaxX = 10, MaxY = 10 },
            Owner = new CastleOwnerInfo { User = Owner, Clan = Clan, Name = "Owner" },
            Level = 3,
        });
        CastlesApi.SetRuntimeForTests(_server);
    }

    public void Dispose()
    {
        CastleContainerRows.IdResolver = null;
        CastleContainerRows.HeartIdResolver = null;
        CastlesApi.ResetForTests();
        Progenitor.WorldState.WorldEpoch.ResetForTests();
        Plugin.Logger = null;
    }

    private static CastleBuildingInfo Model(int index, int prefab, int x) =>
        new() { Entity = new CastleId(index, 1), PrefabGuid = prefab, PrefabName = "TM_Placeholder_" + prefab, TileX = x, TileY = 2, HeightLevel = 1 };

    [Fact]
    public void Containers_and_stations_split_and_carry_ids_nameplates_and_inventory_refs()
    {
        _server.InventoryRows[Heart] = new List<CastleInventoryRow>
        {
            new() { Building = Model(10, 1, 1), SlotCount = 40, Nameable = true, Nameplate = "Ore" },
            new() { Building = Model(11, 2, 3), IsStation = true, IsRefinement = true, SlotCount = 8 },
            new() { Building = Model(12, 3, 5), IsStation = true, SlotCount = 4 },
        };
        var book = new ContainerIdRegistry();
        CastleContainerRows.IdResolver = (b, territory) =>
            book.IdFor(new ContainerSighting(new ContainerAnchor(territory, b.PrefabGuid, b.TileX, b.TileY, b.HeightLevel, b.Rotation),
                b.Entity.Index, b.Entity.Version), Progenitor.WorldState.WorldEpoch.Current, (_, _) => true);

        var castle = CastlesApi.ForTerritory(4);
        var chests = castle.Containers();
        var stations = castle.Stations();
        Assert.Single(chests);
        Assert.Equal(2, stations.Count);
        Assert.Equal("Ore", chests[0].Nameplate);
        Assert.False(chests[0].Id.IsNone);
        Assert.Equal(new InventoryRef(10, 1, 0, Progenitor.WorldState.WorldEpoch.Current), chests[0].Main);
        Assert.True(chests[0].Output.IsNone);
        Assert.Equal(1, stations[0].Output.Instance);
        Assert.True(stations[1].Output.IsNone);
        // Stable across reads.
        Assert.Equal(chests[0].Id, castle.Containers()[0].Id);
    }

    [Fact]
    public void A_handle_without_the_inventory_module_has_no_ids_and_a_disabled_one_has_no_rows()
    {
        _server.InventoryRows[Heart] = new List<CastleInventoryRow> { new() { Building = Model(10, 1, 1) } };
        var castle = CastlesApi.ForTerritory(4);
        Assert.True(castle.Containers()[0].Id.IsNone);
        Assert.True(castle.HeartId.IsNone);
        var none = CastlesApi.ForTerritory(99);
        Assert.Empty(none.Containers());
        Assert.False(none.HeartFuel().Ok);
    }

    [Fact]
    public void HeartId_and_fuel_and_name_come_from_the_runtime()
    {
        var id = ContainerId.New();
        CastleContainerRows.HeartIdResolver = heart => heart == Heart ? id : ContainerId.None;
        _server.HeartNames[Heart] = "Keep";
        _server.Fuel[Heart] = new CastleHeartFuel { Level = 3, UnlockedSlots = 3, LevelRows = 5, FuelQuantity = 120 };
        var castle = CastlesApi.ForTerritory(4);
        Assert.Equal(id, castle.HeartId);
        Assert.Equal("Keep", castle.Name);
        Assert.Equal(3, castle.HeartFuel().UnlockedSlots);
    }

    [Fact]
    public void Heart_level_table_decode_reads_item_slots_for_the_level()
    {
        int stride = CastleHeartLevels.RowStride;
        Assert.Equal(40, stride);
        int rows = 5;
        var blob = new byte[16 + rows * stride];
        // Levels BlobArray at 0: offset relative to the field, then length.
        BinaryPrimitives.WriteInt32LittleEndian(blob.AsSpan(0, 4), 16);
        BinaryPrimitives.WriteInt32LittleEndian(blob.AsSpan(4, 4), rows);
        for (int i = 0; i < rows; i++)
        {
            int at = 16 + i * stride;
            BinaryPrimitives.WriteInt32LittleEndian(blob.AsSpan(at + Layout.CastleHeartLevelBlobLevelOffset, 4), i + 1);
            BinaryPrimitives.WriteInt32LittleEndian(blob.AsSpan(at + Layout.CastleHeartLevelBlobItemSlotsOffset, 4), (i + 1) * 2);
        }

        Assert.True(CastleHeartLevels.TryItemSlots(blob, 3, out int slots, out int count));
        Assert.Equal(6, slots);
        Assert.Equal(5, count);
        Assert.False(CastleHeartLevels.TryItemSlots(blob, 9, out _, out _));
        Assert.False(CastleHeartLevels.TryItemSlots(blob.AsSpan(0, 20), 1, out _, out _));
    }

    [Fact]
    public void Nameplate_codec_round_trips_and_refuses_bad_names()
    {
        var buf = new byte[64];
        Assert.True(NameplateCodec.TryEncode("Iron chest", buf, out _));
        Assert.Equal(10, buf[0]);
        Assert.Equal("Iron chest", NameplateCodec.Decode(buf));
        Assert.True(NameplateCodec.TryEncode("Fer à épée", buf, out _));
        Assert.Equal("Fer à épée", NameplateCodec.Decode(buf));
        Assert.True(NameplateCodec.TryEncode("", buf, out _));
        Assert.Equal("", NameplateCodec.Decode(buf));
        Assert.False(NameplateCodec.TryEncode(new string('x', 62), buf, out string why));
        Assert.Contains("61", why);
        Assert.False(NameplateCodec.TryEncode("a\nb", buf, out _));
        Assert.NotNull(NameplateCodec.Validate(null));
        Assert.Equal(61, Layout.FixedString64MaxUtf8Bytes);
        Assert.Equal(NameplateCodec.MaxBytes, Encoding.UTF8.GetByteCount(new string('x', 61)));
    }

    // ---- clans ----------------------------------------------------------------------------------

    [Fact]
    public void Clan_members_are_the_users_whose_clan_entity_matches()
    {
        var users = new[]
        {
            new ClanUserRow(new CastleId(1, 1), Clan, 11, "Bravo", true),
            new ClanUserRow(new CastleId(2, 1), Clan, 12, "alpha", false),
            new ClanUserRow(new CastleId(3, 1), new CastleId(9, 1), 13, "Other", true),
            new ClanUserRow(new CastleId(4, 1), Clan, 0, "Ghost", false),
            new ClanUserRow(new CastleId(5, 1), CastleId.None, 15, "Solo", true),
        };
        var members = ClanLogic.Members(users, Clan);
        Assert.Equal(2, members.Count);
        Assert.Equal("alpha", members[0].Name);
        Assert.Equal("Bravo", members[1].Name);
        Assert.Empty(ClanLogic.Members(users, CastleId.None));
    }

    [Fact]
    public void Relation_is_owner_then_clan_then_team()
    {
        var owner = new CastleOwnerInfo { User = Owner, Clan = Clan };
        Assert.Equal(CastleRelation.Owner, ClanLogic.Relation(Owner, Clan, owner, false));
        Assert.Equal(CastleRelation.ClanMember, ClanLogic.Relation(new CastleId(2, 1), Clan, owner, false));
        Assert.Equal(CastleRelation.Allied, ClanLogic.Relation(new CastleId(3, 1), CastleId.None, owner, true));
        Assert.Equal(CastleRelation.None, ClanLogic.Relation(new CastleId(3, 1), CastleId.None, owner, null));
        // A clanless owner does not make every clanless user a member.
        var solo = new CastleOwnerInfo { User = Owner };
        Assert.Equal(CastleRelation.None, ClanLogic.Relation(new CastleId(3, 1), CastleId.None, solo, false));
        Assert.False(ClanLogic.IsAllied(CastleRelation.None));
    }

    // ---- presence -------------------------------------------------------------------------------

    [Fact]
    public void Territory_changes_are_debounced_and_initial_only_on_a_plot()
    {
        var t = new PlayerTerritoryTracker();
        Assert.False(t.Observe(1, -1, out _, out _, out _)); // first seen in the open: no event
        Assert.True(t.Observe(2, 4, out int from, out int to, out bool initial)); // logged in on a plot
        Assert.True(initial);
        Assert.Equal(-1, from);
        Assert.Equal(4, to);

        Assert.False(t.Observe(1, 4, out _, out _, out _)); // one reading is not enough
        Assert.False(t.Observe(1, -1, out _, out _, out _)); // flapped back
        Assert.False(t.Observe(1, 4, out _, out _, out _));
        Assert.True(t.Observe(1, 4, out from, out to, out initial));
        Assert.False(initial);
        Assert.Equal(-1, from);
        Assert.Equal(4, to);

        Assert.False(t.Observe(1, 7, out _, out _, out _));
        Assert.True(t.Observe(1, 7, out from, out to, out _));
        Assert.Equal(4, from);
        Assert.Equal(7, to);

        Assert.Equal(1, t.Retain(new HashSet<long> { 1 }));
        Assert.Equal(1, t.Count);
    }

    [Fact]
    public void Ready_fires_once_after_the_character_is_live_and_acks_advance()
    {
        var r = new PlayerReadyTracker();
        r.Connected(5);
        Assert.False(r.Observe(5, false, 100));
        Assert.False(r.Observe(5, true, 0));
        Assert.False(r.Observe(5, true, 100)); // first ack recorded
        Assert.False(r.Observe(5, true, 100)); // not advancing yet
        Assert.True(r.Observe(5, true, 101));
        Assert.False(r.Observe(5, true, 102)); // once per session
        Assert.Equal(0, r.Pending);
        r.Disconnected(5);
        r.Connected(5); // a new session waits again
        Assert.True(r.IsPending(5));
        r.AlreadyReady(6);
        Assert.False(r.Observe(6, true, 500));
        var keys = new List<long>();
        r.CopyPending(keys);
        Assert.Equal(new long[] { 5 }, keys);
    }

    // ---- sample ---------------------------------------------------------------------------------

    [Fact]
    public void Sample_container_tokens_parse()
    {
        Assert.True(SampleInventory.TryParseContainer("#3", out var t));
        Assert.Equal(SampleInventory.TokenKind.Row, t.Kind);
        Assert.Equal(3, t.Row);
        Assert.True(SampleInventory.TryParseContainer("#2/out", out t));
        Assert.True(t.Output);
        Assert.True(SampleInventory.TryParseContainer("1234:2", out t));
        Assert.Equal(SampleInventory.TokenKind.Entity, t.Kind);
        Assert.Equal(1234, t.Index);
        var id = ContainerId.New();
        Assert.True(SampleInventory.TryParseContainer(id.ToString(), out t));
        Assert.Equal(id, t.Id);
        Assert.True(SampleInventory.TryParseContainer("bag", out t));
        Assert.True(SampleInventory.TryParseContainer("HEART", out t));
        Assert.False(SampleInventory.TryParseContainer("bag/out", out _));
        Assert.False(SampleInventory.TryParseContainer("#0", out _));
        Assert.False(SampleInventory.TryParseContainer("chest", out _));
        Assert.True(SampleInventory.TryParseAmount("50", out int n));
        Assert.Equal(50, n);
        Assert.False(SampleInventory.TryParseAmount("-1", out _));
        Assert.True(SampleInventory.Handles("MOVE"));
        Assert.False(SampleInventory.Handles("grant"));
    }

    [Fact]
    public void Sample_move_reply_and_log_name_both_containers_before_and_after()
    {
        var r = new InventoryResult
        {
            Outcome = InventoryOutcome.Complete, Item = -1, Requested = 50, Done = 50,
            SourceBefore = 120, SourceAfter = 70, TargetBefore = 5, TargetAfter = 55, Steps = 1,
        };
        string reply = SampleInventory.MoveReply("#1", "#2", "Item_Ingredient_Mineral_IronOre", r);
        Assert.Equal("Moved 50/50 Item_Ingredient_Mineral_IronOre. #1: 120 -> 70, #2: 5 -> 55 (Complete)", reply);
        string log = SampleInventory.MoveLog("#1", "#2", -1, r);
        Assert.Contains("moved=50 outcome=Complete from_before=120 from_after=70 to_before=5 to_after=55 total_before=125 total_after=125", log);

        var slots = new[] { new InventorySlot(0, 7, 3, 10, 0, false), new InventorySlot(1, 0, 0, 0, 0, false) };
        Assert.Equal("#1: 2 slots | #0 seven x3 | free 1", SampleInventory.SlotsReply("#1", slots, _ => "seven"));
    }
}
