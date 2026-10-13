using System.Collections.Generic;
using Progenitor.Sample;
using Progenitor.Triggers;
using Progenitor.Units;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// The <c>.triggers</c> sample: parse, the two volumes around a pair of feet, and what those
/// volumes do to a player who is already standing in them.
/// </summary>
public class SampleTriggerTests
{
    public SampleTriggerTests() => SampleTriggers.Off();

    [Theory]
    [InlineData("", SampleTriggers.Kind.Status)]
    [InlineData("status", SampleTriggers.Kind.Status)]
    [InlineData(" ON ", SampleTriggers.Kind.On)]
    [InlineData("off", SampleTriggers.Kind.Off)]
    internal void Known_words_parse(string what, SampleTriggers.Kind kind)
    {
        Assert.True(SampleTriggers.TryParse(what, out var parsed));
        Assert.Equal(kind, parsed);
    }

    [Fact]
    public void An_unknown_word_is_refused()
    {
        Assert.False(SampleTriggers.TryParse("circle", out _));
    }

    [Fact]
    public void The_plan_is_one_floor_and_both_shapes_can_be_registered()
    {
        var feet = new UnitVec3(100f, 20f, -50f);
        SampleTriggers.Plan plan = SampleTriggers.At(feet);

        Assert.Equal(AreaTriggerShape.Circle, plan.Circle.Shape);
        Assert.Equal(feet, plan.Circle.Center);
        Assert.Equal(SampleTriggers.CircleRadius, plan.Circle.Radius);
        Assert.Equal(SampleTriggers.FloorHeight, plan.Circle.Height);
        Assert.Null(plan.Circle.Refusal());

        Assert.Equal(AreaTriggerShape.Rectangle, plan.Rectangle.Shape);
        Assert.Null(plan.Rectangle.Refusal());
        plan.Rectangle.ReadRectangle(out UnitVec3 min, out UnitVec3 max);
        Assert.Equal(feet.X - SampleTriggers.RectHalf, min.X);
        Assert.Equal(feet.X + SampleTriggers.RectHalf, max.X);
        Assert.Equal(feet.Y - SampleTriggers.FloorHeight * 0.5f, min.Y);
        Assert.Equal(feet.Y + SampleTriggers.FloorHeight * 0.5f, max.Y);
        Assert.True(SampleTriggers.RectHalf > SampleTriggers.CircleRadius);
    }

    [Fact]
    public void The_armed_reply_says_to_step_out_and_names_the_rat()
    {
        var feet = new UnitVec3(1f, 2f, 3f);
        string reply = SampleTriggers.ArmedReply(feet, server: true);
        Assert.Contains(SampleTriggers.PrefabName, reply);
        Assert.Contains("step out and back in", reply);
        Assert.Contains("first sight does not fire", reply);
        Assert.DoesNotContain("client", reply);

        Assert.Contains("On a client the watches do nothing.", SampleTriggers.ArmedReply(feet, server: false));
        Assert.Equal(SampleTriggers.NoneReply, SampleTriggers.StatusReply());
    }

    [Fact]
    public void Off_disposes_both_watches_and_a_second_off_finds_nothing()
    {
        int disposed = 0;
        var feet = new UnitVec3(4f, 5f, 6f);
        string armed = SampleTriggers.HoldForTests(
            feet,
            new AreaTrigger(() => disposed++),
            new AreaTrigger(() => disposed++));

        Assert.Contains("Triggers armed at", armed);
        Assert.Contains("Triggers armed at", SampleTriggers.StatusReply());
        Assert.Equal(SampleTriggers.OffReply, SampleTriggers.Off());
        Assert.Equal(2, disposed);
        Assert.Equal(SampleTriggers.NoneReply, SampleTriggers.Off());
        Assert.Equal(SampleTriggers.NoneReply, SampleTriggers.StatusReply());
    }

    [Fact]
    public void Standing_inside_does_not_fire_until_they_leave_and_come_back()
    {
        var feet = new UnitVec3(100f, 20f, -50f);
        SampleTriggers.Plan plan = SampleTriggers.At(feet);
        var book = new AreaTriggerBook();
        int circle = book.Add(Circle(plan.Circle));
        int rectangle = book.Add(Box(plan.Rectangle));
        var seen = new List<AreaTriggerBook.Sighting>();
        var edges = new List<AreaTriggerBook.Edge>();

        At(book, seen, edges, P(feet.X, feet.Y, feet.Z + 40f));
        Assert.Empty(edges);

        At(book, seen, edges, P(feet.X, feet.Y, feet.Z));
        Assert.Equal(2, edges.Count);
        Assert.Equal(circle, edges[0].Id);
        Assert.True(edges[0].Enter);
        Assert.Equal(rectangle, edges[1].Id);
        Assert.True(edges[1].Enter);

        At(book, seen, edges, P(feet.X, feet.Y, feet.Z));
        Assert.Empty(edges);

        At(book, seen, edges, P(feet.X, feet.Y, feet.Z + 7f));
        Assert.Single(edges);
        Assert.Equal(circle, edges[0].Id);
        Assert.False(edges[0].Enter);

        At(book, seen, edges, P(feet.X, feet.Y, feet.Z));
        Assert.Single(edges);
        Assert.Equal(circle, edges[0].Id);
        Assert.True(edges[0].Enter);
    }

    [Fact]
    public void The_rectangle_corner_and_the_floor_above_stay_out_of_the_circle()
    {
        var feet = new UnitVec3(100f, 20f, -50f);
        SampleTriggers.Plan plan = SampleTriggers.At(feet);
        var book = new AreaTriggerBook();
        int circle = book.Add(Circle(plan.Circle));
        int rectangle = book.Add(Box(plan.Rectangle));
        var seen = new List<AreaTriggerBook.Sighting>();
        var edges = new List<AreaTriggerBook.Edge>();

        At(book, seen, edges, P(feet.X, feet.Y, feet.Z + 40f));
        At(book, seen, edges, P(feet.X + SampleTriggers.RectHalf, feet.Y, feet.Z + SampleTriggers.RectHalf));
        Assert.Single(edges);
        Assert.Equal(rectangle, edges[0].Id);
        Assert.True(edges[0].Enter);
        Assert.NotEqual(circle, edges[0].Id);

        At(book, seen, edges, P(feet.X, feet.Y + SampleTriggers.FloorHeight, feet.Z));
        Assert.Single(edges);
        Assert.Equal(rectangle, edges[0].Id);
        Assert.False(edges[0].Enter);
    }

    private static AreaTriggerBook.Watch Circle(AreaTriggerOptions options) => new()
    {
        Shape = AreaTriggerShape.Circle,
        X = options.Center.X,
        Y = options.Center.Y,
        Z = options.Center.Z,
        Radius = options.Radius,
        HalfHeight = options.Height * 0.5f,
        ReportEnter = true,
        ReportLeave = true,
    };

    private static AreaTriggerBook.Watch Box(AreaTriggerOptions options)
    {
        options.ReadRectangle(out UnitVec3 min, out UnitVec3 max);
        return new AreaTriggerBook.Watch
        {
            Shape = AreaTriggerShape.Rectangle,
            X = (min.X + max.X) * 0.5f,
            Y = (min.Y + max.Y) * 0.5f,
            Z = (min.Z + max.Z) * 0.5f,
            MinX = min.X,
            MinY = min.Y,
            MinZ = min.Z,
            MaxX = max.X,
            MaxY = max.Y,
            MaxZ = max.Z,
            ReportEnter = true,
            ReportLeave = true,
        };
    }

    private static AreaTriggerBook.Sighting P(float x, float y, float z) =>
        new(7, "TestPlayer", x, y, z, true);

    private static void At(
        AreaTriggerBook book,
        List<AreaTriggerBook.Sighting> seen,
        List<AreaTriggerBook.Edge> edges,
        AreaTriggerBook.Sighting row)
    {
        seen.Clear();
        seen.Add(row);
        book.Step(1, seen, edges);
    }
}
