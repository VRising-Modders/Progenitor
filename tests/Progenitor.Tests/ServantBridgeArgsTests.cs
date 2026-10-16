using System;
using System.Collections.Generic;
using System.Text.Json;
using Progenitor.Castles;
using Progenitor.Dev.Ops;
using Progenitor.Servants;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// The Dev bridge's server <c>servants</c> op, game-free: argument parsing, the servant lookup,
/// the zone a send picks when none is named, and the start time a row finish writes. Every id,
/// GUID and name is a placeholder.
/// </summary>
public class ServantBridgeArgsTests
{
    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement;

    private static ServantInfo Servant(int index, string name, ServantState state = ServantState.Free) => new()
    {
        Servant = index == 0 ? CastleId.None : new CastleId(index, 1),
        Coffin = new CastleId(index + 1000, 1),
        Name = name,
        State = state,
    };

    private static readonly IReadOnlyList<ServantInfo> Castle = new[]
    {
        Servant(11, "Alda"),
        Servant(12, "Bren", ServantState.AwayOnHunt),
        Servant(0, "Cato", ServantState.Dead),
        Servant(14, "Dara"),
        Servant(15, "Eryn", ServantState.HasItems),
    };

    // ---- parsing ----------------------------------------------------------------------------------

    [Fact]
    public void List_is_the_default_and_takes_territory_or_heart()
    {
        var r = ServantArgs.Parse(Args("{\"territory\":7}"));
        Assert.Equal(ServantArgs.Action.List, r.Action);
        Assert.Equal(7, r.Territory);
        Assert.Null(r.Heart);

        r = ServantArgs.Parse(Args("{\"action\":\"list\",\"heart\":\"123:4\"}"));
        Assert.Equal(new CastleId(123, 4), r.Heart);
        Assert.Null(r.Territory);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"action\":\"zones\"}")]
    [InlineData("{\"territory\":1,\"heart\":\"1:1\"}")]
    [InlineData("{\"territory\":-1}")]
    [InlineData("{\"heart\":\"abc\"}")]
    [InlineData("{\"heart\":\"0:0\"}")]
    [InlineData("{\"territory\":\"5\"}")]
    [InlineData("{\"action\":\"hunt\",\"territory\":1}")]
    public void Castle_and_action_are_checked(string json) =>
        Assert.Throws<ArgumentException>(() => ServantArgs.Parse(Args(json)));

    [Fact]
    public void Send_reads_servants_mission_zone_and_slot()
    {
        var r = ServantArgs.Parse(Args("{\"action\":\"send\",\"territory\":3,\"servants\":[\"Alda\",\"#4\"],\"mission\":-123456,\"slot\":2}"));
        Assert.Equal(ServantArgs.Action.Send, r.Action);
        Assert.Equal(new[] { "Alda", "#4" }, r.Servants);
        Assert.Equal(-123456, r.Mission);
        Assert.Null(r.Zone);
        Assert.Equal(2, r.Slot);
        Assert.True(r.CheckDiscovered);

        r = ServantArgs.Parse(Args("{\"action\":\"send\",\"territory\":3,\"servants\":\"Alda, Dara\",\"zone\":\"-3,2#4\",\"checkDiscovered\":false}"));
        Assert.Equal(new[] { "Alda", "Dara" }, r.Servants);
        Assert.Equal(new MissionZone(-3, 2, 4), r.Zone);
        Assert.Equal(0, r.Mission);
        Assert.Equal(0, r.Slot);
        Assert.False(r.CheckDiscovered);

        r = ServantArgs.Parse(Args("{\"action\":\"send\",\"territory\":3,\"mission\":\"42\"}"));
        Assert.Null(r.Servants);
        Assert.Equal(42, r.Mission);
    }

    [Theory]
    [InlineData("{\"action\":\"send\",\"territory\":3,\"mission\":1,\"zone\":\"1,1#1\"}")]
    [InlineData("{\"action\":\"send\",\"territory\":3,\"mission\":0}")]
    [InlineData("{\"action\":\"send\",\"territory\":3,\"mission\":\"x\"}")]
    [InlineData("{\"action\":\"send\",\"territory\":3,\"zone\":\"1,1\"}")]
    [InlineData("{\"action\":\"send\",\"territory\":3,\"zone\":\"1,1#0\"}")]
    [InlineData("{\"action\":\"send\",\"territory\":3,\"zone\":\"200,1#1\"}")]
    [InlineData("{\"action\":\"send\",\"territory\":3,\"slot\":-1}")]
    [InlineData("{\"action\":\"send\",\"territory\":3,\"servants\":[]}")]
    [InlineData("{\"action\":\"send\",\"territory\":3,\"servants\":[\"a\",\"b\",\"c\",\"d\"]}")]
    [InlineData("{\"action\":\"send\",\"territory\":3,\"servants\":[1]}")]
    [InlineData("{\"action\":\"send\",\"territory\":3,\"user\":\"me\"}")]
    public void Send_arguments_are_checked(string json) =>
        Assert.Throws<ArgumentException>(() => ServantArgs.Parse(Args(json)));

    [Fact]
    public void Finish_defaults_to_the_timers_method()
    {
        var r = ServantArgs.Parse(Args("{\"action\":\"finish\",\"territory\":3}"));
        Assert.Equal(ServantArgs.Action.Finish, r.Action);
        Assert.True(r.ViaTimers);
        Assert.Equal(0, r.Mission);

        r = ServantArgs.Parse(Args("{\"action\":\"finish\",\"territory\":3,\"mission\":99,\"method\":\"row\"}"));
        Assert.False(r.ViaTimers);
        Assert.Equal(99, r.Mission);

        Assert.Throws<ArgumentException>(() => ServantArgs.Parse(Args("{\"action\":\"finish\",\"territory\":3,\"method\":\"now\"}")));
        Assert.Throws<ArgumentException>(() => ServantArgs.Parse(Args("{\"action\":\"finish\",\"territory\":3,\"zone\":\"1,1#1\"}")));
    }

    [Fact]
    public void Timers_needs_no_castle()
    {
        var r = ServantArgs.Parse(Args("{\"action\":\"timers\"}"));
        Assert.Equal(ServantArgs.Action.Timers, r.Action);
        Assert.Null(r.TimersOff);
        Assert.True(ServantArgs.Parse(Args("{\"action\":\"timers\",\"off\":true}")).TimersOff);
        Assert.False(ServantArgs.Parse(Args("{\"action\":\"timers\",\"off\":false}")).TimersOff);
        Assert.Throws<ArgumentException>(() => ServantArgs.Parse(Args("{\"action\":\"timers\",\"off\":\"yes\"}")));
    }

    [Fact]
    public void Zones_reads_all_and_user()
    {
        var r = ServantArgs.Parse(Args("{\"action\":\"zones\",\"territory\":3,\"all\":true,\"user\":\"9:2\"}"));
        Assert.True(r.All);
        Assert.Equal(new CastleId(9, 2), r.User);
        Assert.False(ServantArgs.Parse(Args("{\"action\":\"zones\",\"territory\":3}")).All);
    }

    [Fact]
    public void Zone_text_round_trips_with_MissionZone_ToString()
    {
        var z = new MissionZone(-12, 7, 3);
        Assert.Equal(z, ServantArgs.ParseZone(z.ToString()));
        Assert.Null(ServantArgs.ParseZone("#3"));
        Assert.Null(ServantArgs.ParseZone("a,b#c"));
    }

    // ---- servant lookup ---------------------------------------------------------------------------

    [Fact]
    public void Servants_resolve_by_name_row_or_id()
    {
        var ids = ServantArgs.Resolve(Castle, new[] { "alda", "#4", "77:3" }, out string error);
        Assert.Equal("", error);
        Assert.Equal(new[] { new CastleId(11, 1), new CastleId(14, 1), new CastleId(77, 3) }, ids);
    }

    [Theory]
    [InlineData("Zed", "no servant named 'Zed'")]
    [InlineData("#9", "#1..#5")]
    [InlineData("#0", "#1..#5")]
    [InlineData("#3", "dead")]
    [InlineData("Cato", "dead")]
    public void Unresolved_servants_name_the_token(string token, string expected)
    {
        Assert.Null(ServantArgs.Resolve(Castle, new[] { token }, out string error));
        Assert.Contains(expected, error);
    }

    [Fact]
    public void A_servant_named_twice_or_an_ambiguous_name_is_refused()
    {
        Assert.Null(ServantArgs.Resolve(Castle, new[] { "Alda", "#1" }, out string twice));
        Assert.Contains("named twice", twice);
        var dup = new[] { Servant(1, "Same"), Servant(2, "same") };
        Assert.Null(ServantArgs.Resolve(dup, new[] { "SAME" }, out string ambiguous));
        Assert.Contains("not unique", ambiguous);
    }

    [Fact]
    public void Free_servants_are_the_free_ones_in_list_order()
    {
        Assert.Equal(new[] { new CastleId(11, 1), new CastleId(14, 1) }, ServantArgs.FreeServants(Castle, 3));
        Assert.Equal(new[] { new CastleId(11, 1) }, ServantArgs.FreeServants(Castle, 1));
    }

    // ---- zone selection ---------------------------------------------------------------------------

    private static ServantZoneInfo Zone(byte id, int mission, bool? discovered, bool active, int slots) => new()
    {
        Zone = new MissionZone(1, 2, id),
        MissionGuid = mission,
        Discovered = discovered,
        Active = active,
        ServantSlots = slots,
    };

    [Fact]
    public void Pick_takes_the_first_discovered_free_zone_with_room()
    {
        var zones = new[]
        {
            Zone(1, 0, true, false, 3),      // no mission
            Zone(2, 102, false, false, 3),   // not discovered
            Zone(3, 103, null, false, 3),    // discovery unknown
            Zone(4, 104, true, true, 3),     // a hunt is out
            Zone(5, 105, true, false, 1),    // too few slots for two
            Zone(6, 106, true, false, -1),   // slots unreadable
            Zone(7, 107, true, false, 2),
            Zone(8, 108, true, false, 3),
        };
        Assert.Equal(107, ServantLogic.PickZone(zones, 2)!.MissionGuid);
        Assert.Equal(105, ServantLogic.PickZone(zones, 1)!.MissionGuid);
        Assert.Equal(105, ServantLogic.PickZone(zones, 0)!.MissionGuid);
        Assert.Equal(108, ServantLogic.PickZone(zones, 3)!.MissionGuid);
    }

    [Fact]
    public void No_pick_says_why()
    {
        Assert.Null(ServantLogic.PickZone(Array.Empty<ServantZoneInfo>(), 1));
        Assert.Contains("discovered no zone", ServantLogic.NoZoneReason(Array.Empty<ServantZoneInfo>(), 1));
        Assert.Contains("could not be read", ServantLogic.NoZoneReason(new[] { Zone(1, 101, null, false, 3) }, 1));
        Assert.Contains("already has a hunt", ServantLogic.NoZoneReason(new[] { Zone(1, 101, true, true, 3) }, 1));
        Assert.Contains("3 servant slot(s)", ServantLogic.NoZoneReason(new[] { Zone(1, 101, true, false, 2) }, 3));
    }

    // ---- row finish -------------------------------------------------------------------------------

    [Fact]
    public void Finish_now_start_makes_the_hunt_due_and_never_extends_it()
    {
        long now = 638_000_000_000_000_000L;
        long start = now - 60 * ServantLogic.TicksPerSecond;
        long moved = ServantLogic.FinishNowStartTicks(start, 7200f, now);
        Assert.True(ServantLogic.CompletionTicks(moved, 7200f) < now);
        var row = new MissionRow { StartTicks = moved, LengthSeconds = 7200f };
        Assert.True(ServantLogic.IsDue(row, now, 0, timersDisabled: false));

        // A hunt already overdue keeps its start.
        long old = now - 10_000 * ServantLogic.TicksPerSecond;
        Assert.Equal(old, ServantLogic.FinishNowStartTicks(old, 60f, now));
        // A bad length counts as zero.
        Assert.True(ServantLogic.FinishNowStartTicks(now, float.NaN, now) < now);
    }
}
