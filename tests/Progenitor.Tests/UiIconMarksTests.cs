using System;
using System.Collections.Generic;
using System.Linq;
using Progenitor.Ui;
using Xunit;
using UiApi = Progenitor.Ui.Ui;

namespace Progenitor.Tests;

/// <summary>
/// P7b: icon buttons drawn as marks (grip, up, down, remove, check) instead of text glyphs: the shapes' art, their names,
/// their centered placement, the colors of their states, and the ordered list using them. Outcomes only.
/// </summary>
[Collection("UiStatic")]
public sealed class UiIconMarksTests : IDisposable
{
    private readonly WidgetRuntimeFake _runtime = new();
    private readonly List<IUiPanel> _panels = new();

    public UiIconMarksTests()
    {
        UiApi.ResetForTests();
        UiApi.SetRuntimeForTests(_runtime);
    }

    public void Dispose()
    {
        foreach (var p in _panels) p.Dispose();
        UiApi.ResetForTests();
    }

    public static IEnumerable<object[]> Icons() => new[]
    {
        new object[] { "up" }, new object[] { "down" }, new object[] { "remove" }, new object[] { "check" }, new object[] { "grip" },
    };

    [Theory]
    [MemberData(nameof(Icons))]
    public void Each_icon_round_trips_its_name_and_has_drawn_coverage(string name)
    {
        Assert.True(UiDrawnMark.TryParse(name, out var mark));
        Assert.True(mark.IsIcon);
        Assert.Equal(name, mark.Name);
        Assert.False(UiDrawnMark.TryParse(name + ":2", out _));
        var (w, h) = UiDrawnMarkArt.TextureSize(mark);
        Assert.True(w > 0 && h > 0);
        int covered = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (UiDrawnMarkArt.Alpha(mark, x, y) > 0.5f) covered++;
        Assert.InRange(covered, w * h / 25, w * h * 2 / 3); // a visible shape, not a filled box
        Assert.Equal(0f, UiDrawnMarkArt.Alpha(mark, 0, 0)); // nothing in the corner
        Assert.Equal(13f, UiDrawnMarkArt.Height(mark, 18f)); // sized for the 28-unit glyph column
    }

    [Fact]
    public void The_shapes_are_what_they_say()
    {
        int n = UiDrawnMarkArt.TextureHeight;
        float At(UiDrawnMark m, float fx, float fy) => UiDrawnMarkArt.Alpha(m, (int)(fx * UiDrawnMarkArt.TextureSize(m).Width), (int)(fy * n));
        // Up: wide near the bottom, narrow at the top; down the mirror.
        Assert.True(At(UiDrawnMark.UpMark, 0.2f, 0.25f) > 0.5f && At(UiDrawnMark.UpMark, 0.2f, 0.75f) < 0.5f);
        Assert.True(At(UiDrawnMark.DownMark, 0.2f, 0.75f) > 0.5f && At(UiDrawnMark.DownMark, 0.2f, 0.25f) < 0.5f);
        // Remove: the center and the diagonals, not the middle of an edge.
        Assert.True(At(UiDrawnMark.RemoveMark, 0.5f, 0.5f) > 0.5f && At(UiDrawnMark.RemoveMark, 0.25f, 0.25f) > 0.5f);
        Assert.True(At(UiDrawnMark.RemoveMark, 0.5f, 0.2f) < 0.5f);
        // Grip: three bars with gaps between them.
        Assert.True(At(UiDrawnMark.GripMark, 0.5f, 0.28f) > 0.5f && At(UiDrawnMark.GripMark, 0.5f, 0.5f) > 0.5f && At(UiDrawnMark.GripMark, 0.5f, 0.72f) > 0.5f);
        Assert.True(At(UiDrawnMark.GripMark, 0.5f, 0.39f) < 0.5f);
        // Check: the knee low, the long stroke rising right.
        Assert.True(At(UiDrawnMark.CheckMark, 0.40f, 0.24f) > 0.5f && At(UiDrawnMark.CheckMark, 0.80f, 0.70f) > 0.5f);
        Assert.True(At(UiDrawnMark.CheckMark, 0.2f, 0.85f) < 0.5f);
    }

    [Fact]
    public void A_centered_mark_sits_in_the_middle_of_its_cell_whatever_the_text()
    {
        Assert.Equal(14f, UiDrawnMarkArt.CenterX(UiMarkSide.Center, float.NaN, float.NaN, 0f, 28f, 13f));
        Assert.Equal(14f, UiDrawnMarkArt.CenterX(UiMarkSide.Center, 2f, 30f, 0f, 28f, 13f));
    }

    [Fact]
    public void The_states_are_three_different_colors()
    {
        var normal = UiIconMarks.ColorFor(true, false);
        var hover = UiIconMarks.ColorFor(true, true);
        var faint = UiIconMarks.ColorFor(false, true);
        Assert.Equal(new[] { normal, hover, faint }.Distinct().Count(), 3);
        Assert.Equal(faint, UiIconMarks.ColorFor(false, false)); // a disabled button does not light under the pointer
        Assert.Equal(PaletteColor.Hex(0xb89a5a), normal); // the game's box-less glyph gold
    }

    [Fact]
    public void The_ordered_list_draws_its_buttons_as_marks_and_keeps_their_clicks()
    {
        var panel = UiApi.Panel("Marks", new object(), "example.marks");
        _panels.Add(panel);
        var list = (UiOrderedListHandle)panel.OrderedList("l", new[]
        {
            new UiListItem("a", "A"),
            new UiListItem("b", "B") { Removable = false },
        });
        var row0 = _runtime.Rows.Single(r => r.Text == "l:row0");
        foreach (var key in new[] { UiWidgetLayout.CellGrip, UiWidgetLayout.CellUp, UiWidgetLayout.CellDown, UiWidgetLayout.CellRemove })
            Assert.Equal("", row0.Cells().Single(c => c.Key == key).Text);
        Assert.Equal((UiDrawnMark.GripMark, true), list.MarkAt(0, UiWidgetLayout.CellGrip));
        Assert.Equal((UiDrawnMark.UpMark, false), list.MarkAt(0, UiWidgetLayout.CellUp)); // the top row
        Assert.Equal((UiDrawnMark.DownMark, true), list.MarkAt(0, UiWidgetLayout.CellDown));
        Assert.Equal((UiDrawnMark.RemoveMark, false), list.MarkAt(1, UiWidgetLayout.CellRemove)); // not removable

        UiSlotTable.BootstrapDispatch(row0.WiredIdOf(UiWidgetLayout.CellDown), (byte)UiSlotKind.Click, 0f, false, null);
        Assert.Equal(new[] { "b", "a" }, list.Items.Select(i => i.Id)); // still a button
        Assert.Equal((UiDrawnMark.UpMark, true), list.MarkAt(1, UiWidgetLayout.CellUp));
        Assert.Equal((UiDrawnMark.RemoveMark, false), list.MarkAt(0, UiWidgetLayout.CellRemove)); // b moved to the top
    }
}
