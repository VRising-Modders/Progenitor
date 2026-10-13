using System.Collections.Generic;
using System.Text.Json;
using Progenitor.Dev.Ops;
using Progenitor.Ui;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Game-free shaping for <c>mods_ui menu</c>: Graphic rows, zone tags, and the
/// FullscreenMenu walk bound. Live Unity reads are not exercised.
/// </summary>
public class ModsFullscreenMenuProbeTests
{
    [Fact]
    public void Graphic_types_are_image_rawimage_and_tmp()
    {
        Assert.True(ModsFullscreenMenuProbe.IsGraphicType("Image"));
        Assert.True(ModsFullscreenMenuProbe.IsGraphicType("RawImage"));
        Assert.True(ModsFullscreenMenuProbe.IsGraphicType("TMP"));
        Assert.False(ModsFullscreenMenuProbe.IsGraphicType("Transform"));
        Assert.False(ModsFullscreenMenuProbe.IsGraphicType("Canvas"));
        Assert.False(ModsFullscreenMenuProbe.IsGraphicType(""));
        Assert.False(ModsFullscreenMenuProbe.IsGraphicType(null));
    }

    [Fact]
    public void Walk_emits_only_graphics_with_path_rect_zone_and_stops_at_cap()
    {
        var root = new ModsFullscreenMenuProbe.Node
        {
            Name = "FullscreenMenu",
            Children = new[]
            {
                new ModsFullscreenMenuProbe.Node
                {
                    Name = "ViewParent",
                    GraphicType = ModsFullscreenMenuProbe.TypeImage,
                    Zone = NativeFrameExtent.ZoneViewParent,
                    X = 1088f, Y = 72f, W = 1664f, H = 870f,
                    Sprite = "BackgroundImage",
                    SortingOrder = 10,
                },
                new ModsFullscreenMenuProbe.Node
                {
                    Name = "OuterTabGroup",
                    Zone = NativeFrameExtent.ZoneOuterTab,
                    Children = new[]
                    {
                        new ModsFullscreenMenuProbe.Node
                        {
                            Name = "SpellBook",
                            GraphicType = ModsFullscreenMenuProbe.TypeTmp,
                            Zone = NativeFrameExtent.ZoneOuterTab,
                            X = 1100f, Y = 20f, W = 120f, H = 32f,
                            R = 1f, G = 1f, B = 1f, A = 1f,
                        },
                    },
                },
                new ModsFullscreenMenuProbe.Node
                {
                    Name = "EscapeMenuView",
                    Zone = NativeFrameExtent.ZoneHosted,
                    Children = new[]
                    {
                        new ModsFullscreenMenuProbe.Node
                        {
                            Name = "ConnectionInfoRect",
                            GraphicType = ModsFullscreenMenuProbe.TypeImage,
                            Zone = NativeFrameExtent.ZoneHosted,
                            Enabled = true,
                            ActiveInHierarchy = false,
                            X = 1088f, Y = 942f, W = 1664f, H = 58f,
                            Sprite = "MenuFog",
                        },
                    },
                },
            },
        };

        var rows = ModsFullscreenMenuProbe.Walk(
            root, ModsFullscreenMenuProbe.MaxDepth, ModsFullscreenMenuProbe.WalkCap,
            out int walked, out bool truncated);
        Assert.False(truncated);
        Assert.Equal(6, walked);
        Assert.Equal(3, rows.Count);
        Assert.Equal("FullscreenMenu/ViewParent", rows[0]["path"]);
        Assert.Equal(ModsFullscreenMenuProbe.TypeImage, rows[0]["type"]);
        Assert.Equal(NativeFrameExtent.ZoneViewParent, rows[0]["zone"]);
        var vpRect = Assert.IsType<Dictionary<string, object?>>(rows[0]["rect"]);
        Assert.Equal(1088f, vpRect["x"]);
        Assert.Equal(870f, vpRect["h"]);
        Assert.Equal("FullscreenMenu/OuterTabGroup/SpellBook", rows[1]["path"]);
        Assert.Equal(ModsFullscreenMenuProbe.TypeTmp, rows[1]["type"]);
        Assert.Equal(NativeFrameExtent.ZoneOuterTab, rows[1]["zone"]);
        Assert.Equal("FullscreenMenu/EscapeMenuView/ConnectionInfoRect", rows[2]["path"]);
        Assert.Equal(NativeFrameExtent.ZoneHosted, rows[2]["zone"]);
        Assert.Equal(false, rows[2]["activeInHierarchy"]);
        Assert.Equal("MenuFog", rows[2]["sprite"]);
        var rgba = Assert.IsType<float[]>(rows[2]["rgba"]);
        Assert.Equal(4, rgba.Length);

        const int cap = 4;
        var truncatedRows = ModsFullscreenMenuProbe.Walk(root, ModsFullscreenMenuProbe.MaxDepth, cap, out int walkedCap, out bool wasTruncated);
        Assert.True(wasTruncated);
        Assert.Equal(cap, walkedCap);
        Assert.True(truncatedRows.Count <= cap);
        // Revert: emitting every Transform, or dropping zone/rect, hides the footer probe.
    }

    [Fact]
    public void Walk_stops_at_max_depth()
    {
        var leaf = new ModsFullscreenMenuProbe.Node
        {
            Name = "d7",
            GraphicType = ModsFullscreenMenuProbe.TypeImage,
        };
        for (int depth = 6; depth >= 1; depth--)
        {
            leaf = new ModsFullscreenMenuProbe.Node
            {
                Name = "d" + depth,
                Children = new[] { leaf },
            };
        }

        var root = new ModsFullscreenMenuProbe.Node
        {
            Name = "d0",
            Children = new[] { leaf },
        };

        var rows = ModsFullscreenMenuProbe.Walk(root, maxDepth: 6, cap: 64, out int walked, out bool truncated);
        Assert.False(truncated);
        Assert.Equal(7, walked);
        Assert.Empty(rows);
        for (int i = 0; i < rows.Count; i++)
            Assert.DoesNotContain("d7", (string)rows[i]["path"]!);
    }

    [Fact]
    public void Shape_json_has_menu_and_hosted_trees()
    {
        var graphics = new List<Dictionary<string, object?>>
        {
            ModsFullscreenMenuProbe.GraphicRow(
                "FullscreenMenu/ViewParent",
                ModsFullscreenMenuProbe.TypeImage,
                enabled: true,
                activeInHierarchy: true,
                ModsFullscreenMenuProbe.Rect(1088f, 72f, 1664f, 870f),
                "BackgroundImage", 1f, 1f, 1f, 1f, 10,
                NativeFrameExtent.ZoneViewParent),
        };
        var shaped = ModsFullscreenMenuProbe.Shape(
            ModsFullscreenMenuProbe.Tree(true, "FullscreenMenu", 4, false, graphics),
            ModsFullscreenMenuProbe.Tree(true, "EscapeMenuView", 2, false, graphics),
            ModsFullscreenMenuProbe.Stamp(42, 3840, 1080));

        string json = JsonSerializer.Serialize(shaped, ModsUiResult.JsonOptions);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.True(root.GetProperty("menu").GetProperty("present").GetBoolean());
        Assert.Equal("FullscreenMenu", root.GetProperty("menu").GetProperty("path").GetString());
        Assert.Equal(4, root.GetProperty("menu").GetProperty("walked").GetInt32());
        Assert.False(root.GetProperty("menu").GetProperty("truncated").GetBoolean());
        Assert.Equal(ModsFullscreenMenuProbe.WalkCap, root.GetProperty("menu").GetProperty("cap").GetInt32());
        Assert.Equal(ModsFullscreenMenuProbe.MaxDepth, root.GetProperty("menu").GetProperty("maxDepth").GetInt32());
        Assert.Equal(
            "FullscreenMenu/ViewParent",
            root.GetProperty("menu").GetProperty("graphics")[0].GetProperty("path").GetString());
        Assert.Equal(
            NativeFrameExtent.ZoneViewParent,
            root.GetProperty("menu").GetProperty("graphics")[0].GetProperty("zone").GetString());
        Assert.True(root.GetProperty("hosted").GetProperty("present").GetBoolean());
        Assert.Equal("EscapeMenuView", root.GetProperty("hosted").GetProperty("path").GetString());
        Assert.Equal(42, root.GetProperty("stamp").GetProperty("frame").GetInt32());
        Assert.Equal(3840, root.GetProperty("stamp").GetProperty("width").GetInt32());
    }

    [Fact]
    public void DescribeFullscreenMenu_is_read_only_and_never_throws()
    {
        var described = ModsMenu.DescribeFullscreenMenu();
        string json = JsonSerializer.Serialize(described, ModsUiResult.JsonOptions);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.False(root.GetProperty("menu").GetProperty("present").GetBoolean());
        Assert.False(root.GetProperty("hosted").GetProperty("present").GetBoolean());
        Assert.Equal(ModsFullscreenMenuProbe.WalkCap, root.GetProperty("menu").GetProperty("cap").GetInt32());
        Assert.Equal(6, root.GetProperty("menu").GetProperty("maxDepth").GetInt32());
        Assert.Equal(JsonValueKind.Array, root.GetProperty("menu").GetProperty("graphics").ValueKind);
        Assert.Equal(0, root.GetProperty("stamp").GetProperty("frame").GetInt32());
        Assert.DoesNotContain("SetActive", json, System.StringComparison.Ordinal);
        Assert.DoesNotContain("GoToView", json, System.StringComparison.Ordinal);
        Assert.InRange(ModsFullscreenMenuProbe.WalkCap, 256, 1024);
        Assert.Equal(6, ModsFullscreenMenuProbe.MaxDepth);
        // Revert: a 2048 cap or missing hosted tree breaks the on-demand bound.
    }
}
