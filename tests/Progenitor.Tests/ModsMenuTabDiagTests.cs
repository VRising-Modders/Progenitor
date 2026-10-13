using System.Collections.Generic;
using System.Text.Json;
using Progenitor.Dev.Ops;
using Progenitor.Ui;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Game-free shaping for <c>mods_ui hit</c> / <c>tabs</c> and the
/// <c>Ui: tabdiag</c> live-path log lines. Live Unity reads are not exercised.
/// </summary>
public class ModsMenuTabDiagTests
{
    [Fact]
    public void Hit_offline_shape_has_null_fields_with_why()
    {
        var described = ModsMenu.DescribePointerHit(1360f, 92f);
        string json = JsonSerializer.Serialize(described, ModsUiResult.JsonOptions);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal(1360, root.GetProperty("x").GetDouble());
        Assert.Equal(92, root.GetProperty("y").GetDouble());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("stripSlot").ValueKind);
        Assert.Equal(ModsMenuTabDiag.WhyOffline, root.GetProperty("stripSlotWhy").GetString());
        Assert.Equal(JsonValueKind.Array, root.GetProperty("stripRowHit").ValueKind);
        Assert.Equal(0, root.GetProperty("stripRowHit").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("clickWouldInvoke").ValueKind);
        Assert.Equal(ModsMenuTabDiag.WhyOffline, root.GetProperty("clickWouldInvokeWhy").GetString());
        var hover = root.GetProperty("hoverRow");
        Assert.Equal(ModsMenuTabDiag.HoverFunction, hover.GetProperty("function").GetString());
        Assert.Equal(JsonValueKind.Null, hover.GetProperty("name").ValueKind);
        Assert.Equal(ModsMenuTabDiag.WhyOffline, hover.GetProperty("why").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("focusField").ValueKind);
        Assert.Equal(ModsMenuTabDiag.WhyOffline, root.GetProperty("focusFieldWhy").GetString());
        Assert.Equal(JsonValueKind.Array, root.GetProperty("armedFields").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("uguiRaycast").ValueKind);
        Assert.Equal(ModsMenuTabDiag.WhyOffline, root.GetProperty("uguiRaycastWhy").GetString());
        Assert.DoesNotContain("SetActive", json, System.StringComparison.Ordinal);
        Assert.DoesNotContain("InvokeRowClick", json, System.StringComparison.Ordinal);
        Assert.DoesNotContain("ActivateInputField", json, System.StringComparison.Ordinal);
    }

    [Fact]
    public void Tabs_offline_shape_has_native_array_and_owned_null()
    {
        var described = ModsMenu.DescribeOuterTabs();
        string json = JsonSerializer.Serialize(described, ModsUiResult.JsonOptions);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal(ModsMenuTabDiag.WhyOffline, root.GetProperty("why").GetString());
        Assert.Equal(JsonValueKind.Array, root.GetProperty("native").ValueKind);
        Assert.Equal(0, root.GetProperty("native").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("owned").ValueKind);
        Assert.Equal(ModsMenuTabDiag.WhyOffline, root.GetProperty("ownedWhy").GetString());
        Assert.Equal(0, root.GetProperty("nativeTabsLit").GetInt32());
        Assert.Equal("", root.GetProperty("outerTabVisual").GetString());
        Assert.False(root.GetProperty("holdHeld").GetBoolean());
        Assert.False(root.GetProperty("holdCleared").GetBoolean());
    }

    [Fact]
    public void Hit_shape_keeps_overlap_rows_in_order()
    {
        var rows = new List<Dictionary<string, object?>>
        {
            ModsMenuTabDiag.StripRow(0, ModsTabStrip.ModsCaption, ModsMenuTabDiag.Rect(1088, 72, 1664, 40)),
            ModsMenuTabDiag.StripRow(1, ModsTabStrip.InstallModsCaption, ModsMenuTabDiag.Rect(1088, 72, 1664, 40)),
        };
        var shaped = ModsMenuTabDiag.HitShape(
            1360, 92,
            ModsMenuTabDiag.StripSlot(0, ModsTabStrip.ModsCaption, ModsMenuTabDiag.Rect(1088, 72, 1664, 40), rows),
            stripSlotWhy: null,
            rows,
            ModsMenuTabDiag.ClickWouldInvoke(0, ModsTabStrip.ModsCaption),
            clickWouldInvokeWhy: null,
            ModsMenuTabDiag.HoverRow(ModsTabStrip.InstallModsCaption, null),
            ModsMenuTabDiag.FocusField("search", ModsMenuTabDiag.Rect(1108, 208, 320, 40), true, false, true, "install"),
            focusFieldWhy: null,
            new[] { ModsMenuTabDiag.ArmedField("search", ModsMenuTabDiag.Rect(1108, 208, 320, 40), true, false, true, "install") },
            ModsMenuTabDiag.UguiRaycast("overlay-1/tab-strip/Install mods", 4999, true),
            uguiRaycastWhy: null,
            unityX: 1360, unityY: 988);
        string json = JsonSerializer.Serialize(shaped, ModsUiResult.JsonOptions);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal(0, root.GetProperty("stripSlot").GetProperty("index").GetInt32());
        Assert.Equal(ModsTabStrip.ModsCaption, root.GetProperty("stripSlot").GetProperty("name").GetString());
        Assert.Equal(2, root.GetProperty("stripRowHit").GetArrayLength());
        Assert.Equal(ModsTabStrip.InstallModsCaption, root.GetProperty("stripRowHit")[1].GetProperty("name").GetString());
        Assert.Equal(ModsTabStrip.ModsCaption, root.GetProperty("clickWouldInvoke").GetProperty("name").GetString());
        Assert.Equal(ModsMenuTabDiag.HoverFunction, root.GetProperty("hoverRow").GetProperty("function").GetString());
        Assert.Equal(ModsTabStrip.InstallModsCaption, root.GetProperty("hoverRow").GetProperty("name").GetString());
        Assert.Equal("search", root.GetProperty("focusField").GetProperty("id").GetString());
        Assert.True(root.GetProperty("uguiRaycast").GetProperty("ours").GetBoolean());
        Assert.Equal(1360, root.GetProperty("unityX").GetDouble());
        Assert.Equal(988, root.GetProperty("unityY").GetDouble());
    }

    [Fact]
    public void Client_point_flips_y_about_the_screen_height()
    {
        ModsMenuTabDiag.ClientPointToUnity(1360f, 92f, 1080, out float ux, out float uy);
        Assert.Equal(1360f, ux);
        Assert.Equal(988f, uy);
        var rect = ModsMenuTabDiag.UnityRectToClient(1088f, 968f, 1632f, 1008f, 1080);
        Assert.Equal(1088f, rect["x"]);
        Assert.Equal(72f, rect["y"]);
        Assert.Equal(544f, rect["width"]);
        Assert.Equal(40f, rect["height"]);
    }

    [Fact]
    public void Log_lines_use_the_tabdiag_prefix_and_named_fields()
    {
        string click = ModsMenuTabDiag.FormatClickStrip(1360f, 92f, 0, ModsTabStrip.ModsCaption);
        Assert.StartsWith(ModsMenuTabDiag.LogPrefix, click);
        Assert.Contains("click-strip", click);
        Assert.Contains("x=1360", click);
        Assert.Contains("y=92", click);
        Assert.Contains("slot=0", click);
        Assert.Contains("invoked='Mods'", click);

        string miss = ModsMenuTabDiag.FormatClickStrip(10f, 10f, -1, null);
        Assert.Contains("slot=-1", miss);
        Assert.Contains("invoked='none'", miss);

        string focus = ModsMenuTabDiag.FormatFocusField(1268f, 228f, "search", ModsMenuTabDiag.WhyArmedRect);
        Assert.StartsWith(ModsMenuTabDiag.LogPrefix, focus);
        Assert.Contains("focus-field", focus);
        Assert.Contains("x=1268", focus);
        Assert.Contains("y=228", focus);
        Assert.Contains("field='search'", focus);
        Assert.Contains("why=armed-rect", focus);

        string none = ModsMenuTabDiag.FormatFocusField(1268f, 228f, null, ModsMenuTabDiag.WhyNoHit);
        Assert.Contains("field='none'", none);
        Assert.Contains("why=no-hit", none);

        string held = ModsMenuTabDiag.FormatHeldTick(7, 0);
        Assert.StartsWith(ModsMenuTabDiag.LogPrefix, held);
        Assert.Contains("held-tick", held);
        Assert.Contains("cleared=7", held);
        Assert.Contains("nativeTabsLit=0", held);
    }

    [Fact]
    public void Held_tick_log_is_once_per_second()
    {
        Assert.True(ModsMenuTabDiag.ShouldLogHeldTick(0, long.MinValue));
        Assert.False(ModsMenuTabDiag.ShouldLogHeldTick(999, 0));
        Assert.True(ModsMenuTabDiag.ShouldLogHeldTick(1000, 0));
        Assert.Equal(1000, ModsMenuTabDiag.HeldTickMs);
    }

    [Fact]
    public void Live_path_logs_sit_in_the_fixed_functions()
    {
        string runtime = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "UnityUiRuntime.cs");
        string clickStrip = SourcePin.MethodBody(runtime, "private static bool ClickStripButtonAt");
        SourcePin.AssertUncommented(clickStrip, "ModsMenuTabDiag.FormatClickStrip(");
        string focus = SourcePin.MethodBody(runtime, "private static bool FocusFieldAt");
        SourcePin.AssertUncommented(focus, "ModsMenuTabDiag.FormatFocusField(");

        string entry = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "NativeModsEntry.cs");
        string hold = SourcePin.MethodBody(entry, "private static void HoldOuterTabVisual");
        SourcePin.AssertUncommented(hold, "LogHeldTickDrive(");
        string log = SourcePin.MethodBody(entry, "private static void LogHeldTickDrive");
        SourcePin.AssertUncommented(log, "ModsMenuTabDiag.FormatHeldTick(");
        SourcePin.AssertUncommented(log, "ModsMenuTabDiag.ShouldLogHeldTick(");
    }

    [Fact]
    public void Hit_and_tabs_facades_never_throw()
    {
        var hit = ModsMenu.DescribePointerHit(float.NaN, float.NegativeInfinity);
        Assert.NotNull(hit);
        var tabs = ModsMenu.DescribeOuterTabs();
        Assert.NotNull(tabs);
    }
}
