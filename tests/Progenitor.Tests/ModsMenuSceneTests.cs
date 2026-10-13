using System.Collections.Generic;
using System.Text.Json;
using Progenitor.Dev.Ops;
using Progenitor.Ui;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Game-free shaping for <c>mods_ui scene</c>: JSON keys, interesting-type
/// filter, and the MenuParent walk bound. Live Unity reads are not exercised.
/// </summary>
public class ModsMenuSceneTests
{
    [Fact]
    public void TypeMatters_matches_named_renderers_and_fog_tokens()
    {
        Assert.True(ModsMenuScene.TypeMatters("Image"));
        Assert.True(ModsMenuScene.TypeMatters("UnityEngine.UI.RawImage"));
        Assert.True(ModsMenuScene.TypeMatters("Camera"));
        Assert.True(ModsMenuScene.TypeMatters("ParticleSystem"));
        Assert.True(ModsMenuScene.TypeMatters("VideoPlayer"));
        Assert.True(ModsMenuScene.TypeMatters("Canvas"));
        Assert.True(ModsMenuScene.TypeMatters("CanvasGroup"));
        Assert.True(ModsMenuScene.TypeMatters("SpriteRenderer"));
        Assert.True(ModsMenuScene.TypeMatters("MeshRenderer"));
        Assert.True(ModsMenuScene.TypeMatters("Volume"));
        Assert.True(ModsMenuScene.TypeMatters("CustomVignette"));
        Assert.True(ModsMenuScene.TypeMatters("StunVideoPlayer"));
        Assert.True(ModsMenuScene.TypeMatters("AmbienceVolume"));
        Assert.True(ModsMenuScene.TypeMatters("SmokeOverlay"));
        Assert.True(ModsMenuScene.TypeMatters("FogEmitter"));
        Assert.True(ModsMenuScene.TypeMatters("BackgroundImage"));
        Assert.True(ModsMenuScene.TypeMatters("VFXGraph"));
        Assert.False(ModsMenuScene.TypeMatters("Transform"));
        Assert.False(ModsMenuScene.TypeMatters("RectTransform"));
        Assert.False(ModsMenuScene.TypeMatters(""));
        Assert.False(ModsMenuScene.TypeMatters(null));
        Assert.True(ModsMenuScene.ReportsGraphicAssets("Image"));
        Assert.True(ModsMenuScene.ReportsGraphicAssets("UnityEngine.UI.RawImage"));
        Assert.False(ModsMenuScene.ReportsGraphicAssets("Canvas"));
        Assert.False(ModsMenuScene.ReportsGraphicAssets("ParticleSystem"));
        Assert.False(ModsMenuScene.ReportsGraphicAssets((string?)null));
    }

    [Fact]
    public void Walk_serializes_path_active_flags_and_interesting_components()
    {
        var root = new ModsMenuScene.Node
        {
            Name = "MenuParent",
            ActiveSelf = true,
            ActiveInHierarchy = true,
            TypeNames = new[] { "RectTransform", "Canvas" },
            Children = new[]
            {
                new ModsMenuScene.Node
                {
                    Name = "PlaySelectModeMenuView",
                    ActiveSelf = true,
                    ActiveInHierarchy = true,
                    TypeNames = new[] { "Transform", "Image", "CanvasGroup" },
                    CanvasGroupAlpha = 0.58f,
                    Sprite = "MenuBackgroundImage",
                    Material = "MenuFog",
                    Shader = "UI/MenuFog",
                    Children = new[]
                    {
                        new ModsMenuScene.Node
                        {
                            Name = "Smoke",
                            ActiveSelf = true,
                            ActiveInHierarchy = true,
                            TypeNames = new[] { "ParticleSystem", "MeshRenderer" },
                        },
                    },
                },
            },
        };

        var rows = ModsMenuScene.Walk(root, ModsMenuScene.MaxDepth, ModsMenuScene.WalkCap, out int walked, out bool truncated);
        Assert.False(truncated);
        Assert.Equal(3, walked);
        Assert.Equal(3, rows.Count);

        Assert.Equal("MenuParent", rows[0]["path"]);
        Assert.Equal(true, rows[0]["activeSelf"]);
        Assert.Equal(true, rows[0]["activeInHierarchy"]);
        var rootComps = Assert.IsType<List<string>>(rows[0]["components"]);
        Assert.Contains("Canvas", rootComps);
        Assert.DoesNotContain("RectTransform", rootComps);
        Assert.False(rows[0].ContainsKey("canvasGroupAlpha"));

        Assert.Equal("MenuParent/PlaySelectModeMenuView", rows[1]["path"]);
        var playComps = Assert.IsType<List<string>>(rows[1]["components"]);
        Assert.Contains("Image", playComps);
        Assert.Contains("CanvasGroup", playComps);
        Assert.Equal(0.58f, rows[1]["canvasGroupAlpha"]);
        Assert.Equal("MenuBackgroundImage", rows[1]["sprite"]);
        Assert.Equal("MenuFog", rows[1]["material"]);
        Assert.Equal("UI/MenuFog", rows[1]["shader"]);
        Assert.False(rows[0].ContainsKey("sprite"));
        Assert.False(rows[2].ContainsKey("shader"));

        Assert.Equal("MenuParent/PlaySelectModeMenuView/Smoke", rows[2]["path"]);
        var smokeComps = Assert.IsType<List<string>>(rows[2]["components"]);
        Assert.Contains("ParticleSystem", smokeComps);
        Assert.Contains("MeshRenderer", smokeComps);
    }

    [Fact]
    public void Walk_truncates_a_tree_over_the_cap_and_flags_it()
    {
        const int cap = 10;
        var extras = new ModsMenuScene.Node[cap + 5];
        for (int i = 0; i < extras.Length; i++)
        {
            extras[i] = new ModsMenuScene.Node
            {
                Name = "n" + i,
                ActiveSelf = i % 2 == 0,
                ActiveInHierarchy = true,
                TypeNames = new[] { "Image" },
            };
        }

        var root = new ModsMenuScene.Node
        {
            Name = "MenuParent",
            ActiveSelf = true,
            ActiveInHierarchy = true,
            Children = extras,
        };

        var rows = ModsMenuScene.Walk(root, ModsMenuScene.MaxDepth, cap, out int walked, out bool truncated);
        Assert.True(truncated);
        Assert.Equal(cap, walked);
        Assert.Equal(cap, rows.Count);
        Assert.Equal("MenuParent", rows[0]["path"]);
        Assert.Equal("MenuParent/n0", rows[1]["path"]);
        Assert.Equal("MenuParent/n8", rows[cap - 1]["path"]);
        Assert.True(extras.Length + 1 > cap);
    }

    [Fact]
    public void Walk_stops_at_max_depth()
    {
        var leaf = new ModsMenuScene.Node { Name = "d7", ActiveSelf = true, ActiveInHierarchy = true };
        for (int depth = 6; depth >= 1; depth--)
        {
            leaf = new ModsMenuScene.Node
            {
                Name = "d" + depth,
                ActiveSelf = true,
                ActiveInHierarchy = true,
                Children = new[] { leaf },
            };
        }

        var root = new ModsMenuScene.Node
        {
            Name = "d0",
            ActiveSelf = true,
            ActiveInHierarchy = true,
            Children = new[] { leaf },
        };

        var rows = ModsMenuScene.Walk(root, maxDepth: 6, cap: 64, out int walked, out bool truncated);
        Assert.False(truncated);
        Assert.Equal(7, walked);
        Assert.Equal("d0", rows[0]["path"]);
        Assert.Equal("d0/d1/d2/d3/d4/d5/d6", rows[6]["path"]);
        for (int i = 0; i < rows.Count; i++)
            Assert.DoesNotContain("d7", (string)rows[i]["path"]!);
    }

    [Fact]
    public void Shape_json_has_the_documented_fields()
    {
        var children = new List<Dictionary<string, object?>>
        {
            ModsMenuScene.NodeRow("MenuParent/Smoke", true, true, new[] { "ParticleSystem" }, null),
        };
        var shaped = ModsMenuScene.Shape(
            view: ModsMenuScene.View("PlaySelect_Mode", 20),
            viewWhy: null,
            ambience: ModsMenuScene.GameObjectState(true, true, true),
            music: ModsMenuScene.GameObjectState(true, true, true),
            titleView: ModsMenuScene.TitleView(
                present: false,
                activeSelf: false,
                videoImageEnabled: null,
                video: new[] { ModsMenuScene.Video("StunVideoPlayer", false, false, false) }),
            menuParentPresent: true,
            menuParentPath: "MainMenuCanvas/MenuParent",
            menuParentChildren: children,
            walked: 1,
            truncated: false,
            cameras: new[] { ModsMenuScene.Camera("Main", true, "Skybox", ~0, -1f, false) },
            roots: new[] { ModsMenuScene.Root("MainMenu", true) },
            stamp: ModsMenuScene.Stamp(42, 3840, 1080));

        string json = JsonSerializer.Serialize(shaped, ModsUiResult.JsonOptions);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("PlaySelect_Mode", root.GetProperty("view").GetProperty("name").GetString());
        Assert.Equal(20, root.GetProperty("view").GetProperty("value").GetInt32());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("viewWhy").ValueKind);
        Assert.True(root.GetProperty("ambience").GetProperty("present").GetBoolean());
        Assert.True(root.GetProperty("ambience").GetProperty("activeSelf").GetBoolean());
        Assert.True(root.GetProperty("ambience").GetProperty("activeInHierarchy").GetBoolean());
        Assert.True(root.GetProperty("music").GetProperty("present").GetBoolean());
        Assert.False(root.GetProperty("titleView").GetProperty("present").GetBoolean());
        Assert.Equal("StunVideoPlayer", root.GetProperty("titleView").GetProperty("video")[0].GetProperty("type").GetString());
        var menu = root.GetProperty("menuParent");
        Assert.True(menu.GetProperty("present").GetBoolean());
        Assert.Equal("MainMenuCanvas/MenuParent", menu.GetProperty("path").GetString());
        Assert.Equal(1, menu.GetProperty("walked").GetInt32());
        Assert.False(menu.GetProperty("truncated").GetBoolean());
        Assert.Equal(ModsMenuScene.WalkCap, menu.GetProperty("cap").GetInt32());
        Assert.Equal(ModsMenuScene.MaxDepth, menu.GetProperty("maxDepth").GetInt32());
        Assert.Equal("MenuParent/Smoke", menu.GetProperty("children")[0].GetProperty("path").GetString());
        Assert.Equal("ParticleSystem", menu.GetProperty("children")[0].GetProperty("components")[0].GetString());
        Assert.Equal("Main", root.GetProperty("cameras")[0].GetProperty("name").GetString());
        Assert.False(root.GetProperty("cameras")[0].GetProperty("hasTargetTexture").GetBoolean());
        Assert.Equal("MainMenu", root.GetProperty("roots")[0].GetProperty("name").GetString());
        Assert.Equal(42, root.GetProperty("stamp").GetProperty("frame").GetInt32());
        Assert.Equal(3840, root.GetProperty("stamp").GetProperty("width").GetInt32());
        Assert.Equal(1080, root.GetProperty("stamp").GetProperty("height").GetInt32());
        Assert.True(root.TryGetProperty("playBackdrop", out var playEl));
        Assert.False(playEl.GetProperty("present").GetBoolean());
        Assert.Equal(NativeModsEntry.PlayBackdropHold.SourceNone, playEl.GetProperty("source").GetString());
        Assert.True(playEl.TryGetProperty("sprite", out _));
        Assert.True(playEl.TryGetProperty("material", out _));
        Assert.True(playEl.TryGetProperty("shader", out _));
    }

    [Fact]
    public void DescribeMenuScene_is_read_only_and_never_throws()
    {
        var described = ModsMenu.DescribeMenuScene();
        string json = JsonSerializer.Serialize(described, ModsUiResult.JsonOptions);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal(JsonValueKind.Null, root.GetProperty("view").ValueKind);
        Assert.Equal(ModsMenuScene.WhyOffline, root.GetProperty("viewWhy").GetString());
        Assert.False(root.GetProperty("ambience").GetProperty("present").GetBoolean());
        Assert.False(root.GetProperty("music").GetProperty("present").GetBoolean());
        Assert.False(root.GetProperty("titleView").GetProperty("present").GetBoolean());
        Assert.False(root.GetProperty("menuParent").GetProperty("present").GetBoolean());
        Assert.False(root.GetProperty("menuParent").GetProperty("truncated").GetBoolean());
        Assert.Equal(ModsMenuScene.WalkCap, root.GetProperty("menuParent").GetProperty("cap").GetInt32());
        Assert.Equal(ModsMenuScene.MaxDepth, root.GetProperty("menuParent").GetProperty("maxDepth").GetInt32());
        Assert.Equal(JsonValueKind.Array, root.GetProperty("cameras").ValueKind);
        Assert.Equal(JsonValueKind.Array, root.GetProperty("roots").ValueKind);
        Assert.Equal(0, root.GetProperty("stamp").GetProperty("frame").GetInt32());
        Assert.False(root.GetProperty("playBackdrop").GetProperty("present").GetBoolean());
        Assert.Equal(
            NativeModsEntry.PlayBackdropHold.SourceNone,
            root.GetProperty("playBackdrop").GetProperty("source").GetString());
        Assert.DoesNotContain("SetActive", json, System.StringComparison.Ordinal);
        Assert.DoesNotContain("GoToView", json, System.StringComparison.Ordinal);
    }

    [Fact]
    public void Walk_cap_constant_is_a_few_thousand()
    {
        Assert.InRange(ModsMenuScene.WalkCap, 256, 4096);
        Assert.Equal(6, ModsMenuScene.MaxDepth);
    }
}
