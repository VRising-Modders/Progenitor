using System.Collections.Generic;
using Progenitor.Triggers;
using Progenitor.Units;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Game-free coverage of <c>ctx.Triggers</c>: who counts as inside, when a crossing fires,
/// and when the first sample only records a side. The server tick supplies positions.
/// </summary>
public class AreaTriggerTests
{
    private readonly AreaTriggerBook _book = new();
    private readonly List<AreaTriggerBook.Sighting> _seen = new();
    private readonly List<AreaTriggerBook.Edge> _edges = new();

    [Fact]
    public void Refusal_requires_an_action_and_a_finite_volume()
    {
        Assert.Equal("Set OnEnter, OnLeave, or both.", new AreaTriggerOptions().Refusal());

        var negative = new AreaTriggerOptions { Radius = -1f, Height = 4f, OnEnter = _ => { } };
        Assert.Equal("Radius must be zero or greater, and finite.", negative.Refusal());

        var infinite = new AreaTriggerOptions { Radius = float.PositiveInfinity, Height = 4f, OnEnter = _ => { } };
        Assert.Equal("Radius must be zero or greater, and finite.", infinite.Refusal());

        var flat = new AreaTriggerOptions { Radius = 1f, OnEnter = _ => { } };
        Assert.Equal("Height must be greater than zero, and finite.", flat.Refusal());

        var center = new AreaTriggerOptions
        {
            Radius = 1f,
            Height = 4f,
            Center = new UnitVec3(float.NaN, 0f, 0f),
            OnEnter = _ => { },
        };
        Assert.Equal("Center must be a finite position.", center.Refusal());

        var shape = new AreaTriggerOptions
        {
            Shape = (AreaTriggerShape)9,
            Radius = 1f,
            Height = 4f,
            OnEnter = _ => { },
        };
        Assert.Equal("Shape must be Circle or Rectangle.", shape.Refusal());

        var ok = new AreaTriggerOptions { Radius = 0f, Height = 4f, OnLeave = _ => { } };
        Assert.Null(ok.Refusal());

        var room = new AreaTriggerOptions
        {
            Shape = AreaTriggerShape.Rectangle,
            Min = new UnitVec3(2f, 4f, 6f),
            Max = new UnitVec3(0f, 0f, 0f),
            OnLeave = _ => { },
        };
        Assert.Null(room.Refusal());

        var slab = new AreaTriggerOptions
        {
            Shape = AreaTriggerShape.Rectangle,
            Min = new UnitVec3(0f, 1f, 0f),
            Max = new UnitVec3(2f, 1f, 2f),
            OnEnter = _ => { },
        };
        Assert.Equal("The rectangle must have a positive size on each axis.", slab.Refusal());

        var missing = new AreaTriggerOptions
        {
            Shape = AreaTriggerShape.Rectangle,
            Min = new UnitVec3(float.NaN, 0f, 0f),
            Max = new UnitVec3(1f, 1f, 1f),
            OnEnter = _ => { },
        };
        Assert.Equal("Min and Max must be finite positions.", missing.Refusal());
    }

    [Fact]
    public void Dispose_stops_once_and_a_client_handle_is_inactive()
    {
        int stops = 0;
        var trigger = new AreaTrigger(() => stops++);
        Assert.True(trigger.IsActive);
        trigger.Dispose();
        trigger.Dispose();
        Assert.False(trigger.IsActive);
        Assert.Equal(1, stops);
        Assert.False(AreaTrigger.Inactive().IsActive);
    }

    [Fact]
    public void The_first_sight_records_the_side_and_fires_nothing()
    {
        Watch();
        At(1, P(7, 0f, 0f), P(8, 0f, 20f));
        Assert.Empty(_edges);
    }

    [Fact]
    public void Walking_in_fires_enter_once_and_the_skin_does_not_chatter()
    {
        Watch();
        At(1, P(7, 0f, 20f));
        At(1, P(7, 0f, 0f));
        Edge(enter: true, account: 7, z: 0f);

        At(1, P(7, 0f, 5.4f));
        Assert.Empty(_edges);

        At(1, P(7, 0f, 0f));
        Assert.Empty(_edges);

        At(1, P(7, 0f, 5.5f));
        Assert.Empty(_edges);

        At(1, P(7, 0f, 6f));
        Edge(enter: false, account: 7, z: 6f);

        At(1, P(7, 3f, 4f));
        Edge(enter: true, account: 7, x: 3f, z: 4f);
    }

    [Fact]
    public void A_player_already_inside_does_not_enter_until_they_leave_and_come_back()
    {
        Watch();
        At(1, P(7, 0f, 0f));
        At(1, P(7, 0f, 0f));
        Assert.Empty(_edges);
        At(1, P(7, 0f, 20f));
        Edge(enter: false, account: 7, z: 20f);
        At(1, P(7, 0f, 0f));
        Edge(enter: true, account: 7);
    }

    [Fact]
    public void Disconnect_while_inside_fires_leave_and_a_reconnect_only_records()
    {
        Watch();
        At(1, P(7, 1f, 1f, name: "TestPlayer"));
        At(1);
        Edge(enter: false, account: 7, x: 1f, z: 1f, name: "TestPlayer");
        Assert.Equal(0, _book.OccupancyCount);

        At(1, P(7, 1f, 1f));
        Assert.Empty(_edges);
    }

    [Fact]
    public void Disconnect_while_outside_fires_nothing()
    {
        Watch();
        At(1, P(7, 0f, 20f));
        At(1);
        Assert.Empty(_edges);
    }

    [Fact]
    public void A_missing_position_does_not_count_as_leaving()
    {
        Watch();
        At(1, P(7, 0f, 0f));
        At(1, P(7, 0f, 0f, has: false));
        Assert.Empty(_edges);
        At(1, P(7, 0f, 0f, y: float.NaN));
        Assert.Empty(_edges);
        At(1, P(7, 0f, 20f));
        Edge(enter: false, account: 7, z: 20f);
    }

    [Fact]
    public void A_world_epoch_change_records_again_and_does_not_fire()
    {
        Watch();
        At(1, P(7, 0f, 0f));
        At(2, P(7, 0f, 20f));
        Assert.Empty(_edges);
        At(2, P(7, 0f, 0f));
        Edge(enter: true, account: 7);
    }

    [Fact]
    public void The_floor_above_and_below_a_circle_do_not_count()
    {
        Watch();
        At(1, P(7, 0f, 20f));
        At(1, P(7, 0f, 0f, y: 16f));
        Assert.Empty(_edges);
        At(1, P(7, 0f, 0f, y: 4f));
        Assert.Empty(_edges);

        At(1, P(7, 0f, 0f, y: 12f));
        Edge(enter: true, y: 12f);
        At(1, P(7, 0f, 0f, y: 8f));
        Assert.Empty(_edges);

        At(1, P(7, 0f, 0f, y: 12.5f));
        Assert.Empty(_edges);
        At(1, P(7, 0f, 0f, y: 12.6f));
        Edge(enter: false, y: 12.6f);
    }

    [Fact]
    public void A_circle_excludes_the_corner_of_its_bounding_square()
    {
        Watch();
        At(1, P(7, 20f, 20f));
        At(1, P(7, 5f, 5f));
        Assert.Empty(_edges);
        At(1, P(7, 0f, 5f));
        Edge(enter: true, z: 5f);
    }

    [Fact]
    public void A_rectangle_includes_its_corners_and_ignores_the_floor_above()
    {
        WatchBox();
        At(1, P(7, 20f, 20f));
        At(1, P(7, 4f, 4f));
        Edge(enter: true, x: 4f, z: 4f);

        At(1, P(7, 4f, 4f, y: 16f));
        Edge(enter: false, x: 4f, z: 4f, y: 16f);
    }

    [Fact]
    public void Swapped_rectangle_corners_still_contain_the_room()
    {
        WatchBox(minX: 4f, maxX: -4f, minY: 12f, maxY: 8f, minZ: 4f, maxZ: -4f);
        At(1, P(7, 20f, 20f));
        At(1, P(7, 0f, 0f));
        Edge(enter: true);
    }

    [Fact]
    public void A_rectangle_skin_holds_just_outside_a_face()
    {
        WatchBox();
        At(1, P(7, 20f, 0f));
        At(1, P(7, 4f, 0f));
        Edge(enter: true, x: 4f);
        At(1, P(7, 4.5f, 0f));
        Assert.Empty(_edges);
        At(1, P(7, 4.6f, 0f));
        Edge(enter: false, x: 4.6f);
    }

    [Fact]
    public void Enter_only_still_arms_the_next_enter_after_a_quiet_leave()
    {
        Watch(leave: false);
        At(1, P(7, 0f, 20f));
        At(1, P(7, 0f, 0f));
        Edge(enter: true, account: 7);
        At(1, P(7, 0f, 20f));
        Assert.Empty(_edges);
        At(1, P(7, 0f, 0f));
        Edge(enter: true, account: 7);
    }

    [Fact]
    public void Two_players_and_a_removed_watch_stay_independent()
    {
        int first = Watch();
        int second = Watch(radius: 2f);
        At(1, P(7, 0f, 20f), P(8, 0f, 20f));
        At(1, P(7, 0f, 4f), P(8, 0f, 20f));
        Assert.Single(_edges);
        Assert.Equal(first, _edges[0].Id);
        Assert.Equal(7ul, _edges[0].AccountId);

        Assert.True(_book.Remove(first));
        Assert.Equal(2, _book.OccupancyCount);
        At(1, P(7, 0f, 20f), P(8, 0f, 0f));
        Assert.Single(_edges);
        Assert.Equal(second, _edges[0].Id);
        Assert.Equal(8ul, _edges[0].AccountId);
        Assert.False(_book.Remove(first));
    }

    [Fact]
    public void The_first_row_wins_unless_only_a_later_one_has_a_position()
    {
        Watch();
        At(1, P(7, 0f, 0f, name: "First"), P(7, 0f, 20f, name: "Second"));
        At(1);
        Edge(enter: false, name: "First", z: 0f);

        At(1, P(7, 0f, 0f, has: false, name: "Blank"), P(7, 0f, 3f, name: "Later"));
        Assert.Empty(_edges);
        At(1, P(7, 0f, 20f, name: "Later"));
        Edge(enter: false, name: "Later", z: 20f);
    }

    [Fact]
    public void Account_zero_is_ignored()
    {
        Watch();
        At(1, P(0, 0f, 0f));
        At(1, P(0, 0f, 20f));
        Assert.Empty(_edges);
        Assert.Equal(0, _book.OccupancyCount);
    }

    private int Watch(float radius = 5f, float halfHeight = 2f, bool enter = true, bool leave = true) =>
        _book.Add(new AreaTriggerBook.Watch
        {
            Shape = AreaTriggerShape.Circle,
            X = 0f,
            Y = 10f,
            Z = 0f,
            Radius = radius,
            HalfHeight = halfHeight,
            ReportEnter = enter,
            ReportLeave = leave,
        });

    private int WatchBox(
        float minX = -4f,
        float maxX = 4f,
        float minY = 8f,
        float maxY = 12f,
        float minZ = -4f,
        float maxZ = 4f,
        bool enter = true,
        bool leave = true) =>
        _book.Add(new AreaTriggerBook.Watch
        {
            Shape = AreaTriggerShape.Rectangle,
            X = (minX + maxX) * 0.5f,
            Y = (minY + maxY) * 0.5f,
            Z = (minZ + maxZ) * 0.5f,
            MinX = minX,
            MinY = minY,
            MinZ = minZ,
            MaxX = maxX,
            MaxY = maxY,
            MaxZ = maxZ,
            ReportEnter = enter,
            ReportLeave = leave,
        });

    private void At(int epoch, params AreaTriggerBook.Sighting[] rows)
    {
        _seen.Clear();
        if (rows != null)
        {
            for (int i = 0; i < rows.Length; i++)
                _seen.Add(rows[i]);
        }

        _book.Step(epoch, _seen, _edges);
    }

    private static AreaTriggerBook.Sighting P(
        ulong id, float x, float z, float y = 10f, bool has = true, string name = "TestPlayer") =>
        new(id, name, x, y, z, has);

    private void Edge(bool enter, ulong account = 7, float x = 0f, float z = 0f, string name = "TestPlayer", float? y = null)
    {
        Assert.Single(_edges);
        AreaTriggerBook.Edge edge = _edges[0];
        Assert.Equal(enter, edge.Enter);
        Assert.Equal(account, edge.AccountId);
        Assert.Equal(name, edge.Name);
        Assert.Equal(x, edge.X);
        Assert.Equal(z, edge.Z);
        if (y is float expected)
            Assert.Equal(expected, edge.Y);
    }
}
