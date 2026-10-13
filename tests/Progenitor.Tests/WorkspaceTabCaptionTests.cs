using System;
using System.Collections.Generic;
using Progenitor.Ui;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// TABCAPS1 (user report 2026-10-03: once, the workspace strip's four tabs drew with no text). The
/// verify / repair rules every strip show applies, the repair log line, the <c>mods_ui tabs</c>
/// readback shape, and source pins for the runtime hook, the label choice and the resident-donor
/// preference that the incident's log points at.
/// </summary>
public sealed class WorkspaceTabCaptionTests : IDisposable
{
    public WorkspaceTabCaptionTests() => WorkspaceTabCaptions.Reset();

    public void Dispose() => WorkspaceTabCaptions.Reset();

    private static WorkspaceTabCaptions.Observation Good(string intended = "Install Mods") =>
        new("InstallMods", intended, labelFound: true, active: true, enabled: true, text: intended,
            fontAlive: true, glyphsPresent: true, alpha: 1f, font: "Body", labels: 1);

    private static WorkspaceTabCaptions.Observation With(
        bool labelFound = true, bool active = true, bool enabled = true, string text = "Install Mods",
        bool fontAlive = true, bool glyphs = true, float alpha = 1f) =>
        new("InstallMods", "Install Mods", labelFound, active, enabled, text, fontAlive, glyphs, alpha, "Body", 2);

    [Fact]
    public void A_caption_that_shows_its_words_has_no_faults()
    {
        Assert.Empty(WorkspaceTabCaptions.Faults(Good()));
        // A row that is not one of the known strip's tabs is never held to a caption.
        Assert.Empty(WorkspaceTabCaptions.Faults(new WorkspaceTabCaptions.Observation("button0", "", false, false, false, "", false, false, 0f)));
    }

    [Fact]
    public void Each_way_a_caption_can_draw_blank_is_a_fault()
    {
        Assert.Equal(new[] { WorkspaceTabCaptions.FaultNoLabel }, WorkspaceTabCaptions.Faults(With(labelFound: false)));
        Assert.Equal(new[] { WorkspaceTabCaptions.FaultInactive }, WorkspaceTabCaptions.Faults(With(active: false)));
        Assert.Equal(new[] { WorkspaceTabCaptions.FaultDisabled }, WorkspaceTabCaptions.Faults(With(enabled: false)));
        Assert.Equal(new[] { WorkspaceTabCaptions.FaultText }, WorkspaceTabCaptions.Faults(With(text: "")));
        Assert.Equal(new[] { WorkspaceTabCaptions.FaultText }, WorkspaceTabCaptions.Faults(With(text: "Spellbook")));
        Assert.Equal(new[] { WorkspaceTabCaptions.FaultFont }, WorkspaceTabCaptions.Faults(With(fontAlive: false, glyphs: false)));
        Assert.Equal(new[] { WorkspaceTabCaptions.FaultGlyphs }, WorkspaceTabCaptions.Faults(With(glyphs: false)));
        Assert.Equal(new[] { WorkspaceTabCaptions.FaultTransparent }, WorkspaceTabCaptions.Faults(With(alpha: 0f)));
        Assert.Equal(new[] { WorkspaceTabCaptions.FaultTransparent }, WorkspaceTabCaptions.Faults(With(alpha: float.NaN)));
        Assert.Empty(WorkspaceTabCaptions.Faults(With(alpha: WorkspaceTabCaptions.MinAlpha)));
        // Several at once are all reported, in a stable order.
        Assert.Equal(
            new[] { WorkspaceTabCaptions.FaultInactive, WorkspaceTabCaptions.FaultText, WorkspaceTabCaptions.FaultTransparent },
            WorkspaceTabCaptions.Faults(With(active: false, text: "", alpha: 0f)));
    }

    [Fact]
    public void The_repair_line_names_the_tab_the_old_text_and_the_faults()
    {
        var seen = With(text: "", alpha: 0f);
        string line = WorkspaceTabCaptions.RepairLine(seen, WorkspaceTabCaptions.Faults(seen));
        Assert.StartsWith("Ui: tab caption repaired InstallMods (was '')", line, StringComparison.Ordinal);
        Assert.Contains("faults=text,transparent", line, StringComparison.Ordinal);
        Assert.Contains("font=Body", line, StringComparison.Ordinal);
        Assert.Contains("source=", line, StringComparison.Ordinal);

        WorkspaceTabCaptions.NoteRepair(line);
        WorkspaceTabCaptions.NoteRepair(line);
        Assert.Equal(2, WorkspaceTabCaptions.Repairs);
        Assert.Equal(line, WorkspaceTabCaptions.LastRepair);
        WorkspaceTabCaptions.Reset();
        Assert.Equal(0, WorkspaceTabCaptions.Repairs);
        Assert.Equal("", WorkspaceTabCaptions.LastRepair);
    }

    [Fact]
    public void The_readback_carries_intended_and_rendered_text_and_the_verdict()
    {
        var ok = WorkspaceTabCaptions.Readback(Good());
        Assert.Equal("InstallMods", ok["tab"]);
        Assert.Equal("Install Mods", ok["intended"]);
        Assert.Equal("Install Mods", ok["text"]);
        Assert.Equal(true, ok["ok"]);
        Assert.Empty((List<string>)ok["faults"]!);
        foreach (string key in new[] { "labelFound", "labels", "active", "enabled", "font", "fontAlive", "glyphs", "alpha" })
            Assert.True(ok.ContainsKey(key), key);

        var blank = WorkspaceTabCaptions.Readback(With(text: ""));
        Assert.Equal("", blank["text"]);
        Assert.Equal(false, blank["ok"]);
        Assert.Equal(new List<string> { WorkspaceTabCaptions.FaultText }, blank["faults"]);
    }

    [Fact]
    public void The_intended_captions_are_the_strip_order()
    {
        // Three homes (decided 2026-10-06).
        Assert.Equal(new[] { "Mods", "Install Mods", "Keybinds", "Preferences", "Server" }, ModsTabStrip.CaptionsFor(ModsTabStrip.Order.Length));
        // Another strip's rows get no intended caption, so they are never "repaired" into tab names.
        Assert.All(ModsTabStrip.CaptionsFor(3), c => Assert.Equal("", c));
    }

    // ---- runtime (source pins) --------------------------------------------------------------------

    [Fact]
    public void Every_strip_show_checks_and_repairs_and_rechecks_next_frame()
    {
        string runtime = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "UnityUiRuntime.cs");
        string show = SourcePin.MethodBody(runtime, "public void Show(IRuntimePanel panel)");
        SourcePin.AssertUncommented(show, "if (host.IsStrip) VerifyStripOnShow(host);");
        string verify = SourcePin.MethodBody(runtime, "private static void VerifyStripOnShow(UnityPanel host)");
        SourcePin.AssertUncommented(verify, "CheckStripCaptions(host, repair: true);");
        SourcePin.AssertUncommented(verify, "Plugin.Frame += StripRecheckPump;");
        string pump = SourcePin.MethodBody(runtime, "private static void StripRecheckPump()");
        SourcePin.AssertUncommented(pump, "Plugin.Frame -= StripRecheckPump;");
        SourcePin.AssertUncommented(pump, "CheckStripCaptions(panel, repair: true);");
        string check = SourcePin.MethodBody(runtime, "private static List<WorkspaceTabCaptions.Observation> CheckStripCaptions(");
        SourcePin.AssertUncommented(check, "var intended = ModsTabStrip.CaptionsFor(buttons.Count);");
        SourcePin.AssertUncommented(check, "Plugin.Logger?.LogWarning(line);");
        // Nothing outlives the runtime: the strip reference and the frame subscription go on release.
        SourcePin.AssertUncommented(runtime, "WorkspaceTabCaptions.Reset();");
        SourcePin.AssertUncommented(runtime, "_lastStrip = null;");
    }

    [Fact]
    public void A_cloned_button_writes_its_caption_into_the_label_it_shows()
    {
        string runtime = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "UnityUiRuntime.cs");
        string set = SourcePin.MethodBody(runtime, "private static GameObject? SetButtonText(GameObject go, string text)");
        SourcePin.AssertUncommented(set, "var tmp = PrimaryLabel(go, out _);");
        Assert.DoesNotContain("GetComponentInChildren<TextMeshProUGUI>(true)", set, StringComparison.Ordinal);
        string label = SourcePin.MethodBody(runtime, "private static TextMeshProUGUI? PrimaryLabel(GameObject root, out int count)");
        SourcePin.AssertUncommented(label, "if (enabled && SubtreeActive(tmp.gameObject, root)) return tmp;");
    }

    [Fact]
    public void The_resident_rung_prefers_the_fullscreen_menus_own_tab_prefab()
    {
        string donors = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "WorkspaceTabDonors.cs");
        string scan = SourcePin.MethodBody(donors, "internal static GameObject? ResidentDonor()");
        int menuScan = scan.IndexOf("Il2CppType.Of<FullscreenMenu>()", StringComparison.Ordinal);
        int entryScan = scan.IndexOf("Il2CppType.Of<FullscreenMenu_InnerTabEntry>()", StringComparison.Ordinal);
        Assert.True(menuScan >= 0 && entryScan > menuScan, "the menu's prefab is tried before any inner-tab entry");
        SourcePin.AssertUncommented(scan, "_residentKind = ResidentMenuPrefab;");
        SourcePin.AssertUncommented(scan, "_residentKind = ResidentAnyEntry;");
        string menu = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "ModsMenu.cs");
        Assert.Contains("described[\"stripCaptions\"] = UnityUiRuntime.DescribeStripCaptions();", menu, StringComparison.Ordinal);
    }
}
