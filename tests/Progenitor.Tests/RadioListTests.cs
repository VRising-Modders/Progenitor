using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Progenitor.Networking;
using Progenitor.Ui;
using Xunit;
using UiApi = Progenitor.Ui.Ui;

namespace Progenitor.Tests;

/// <summary>
/// RADIO1 (decided 2026-10-06): pick-one choices are radio lists. One option per line with a radio circle, the
/// chosen line bright and the others dimmed, an optional explanation under each option; a click on a line chooses it
/// once and never toggles. One widget kind in both kits (settings grid and cell rows), read back as <c>radio</c>.
/// </summary>
public sealed class RadioListTests : IDisposable
{
    private readonly UiTests.FakeUiRuntime _runtime = new();
    private readonly object _owner = new();

    public RadioListTests()
    {
        UiLayoutBatch.ResetForTests();
        UiApi.ResetForTests();
        UiApi.SetRuntimeForTests(_runtime);
    }

    public void Dispose()
    {
        UiLayoutBatch.ResetForTests();
        UiApi.ResetForTests();
    }

    private static IReadOnlyList<UiChoiceOption> Two() => new[]
    {
        new UiChoiceOption("warn", "Warn and pause it", "Nobody is disconnected."),
        new UiChoiceOption("disconnect", "Disconnect the player", "A notice, then a disconnect."),
    };

    // ---- the settings grid's radio (IUiRadio) ----------------------------------------------------------------------

    [Fact]
    public void Every_pick_one_is_a_list_never_a_segmented_picker()
    {
        var shortOptions = UiSettingsControls.Options(new[] { "None", "Days", "Weeks" }, null);
        Assert.Equal(UiOptionsLayout.List, UiOptionsRules.LayoutFor(shortOptions, multi: false));
        Assert.Equal(UiOptionsLayout.List, UiOptionsRules.LayoutFor(shortOptions, multi: true));

        var panel = UiApi.Panel("Controls", _owner, "example.radio.test");
        var radio = (UiOptionsHandle)panel.Radio("Repeat", new[] { "None", "Days", "Weeks" }, "None");
        Assert.Equal(UiOptionsLayout.List, radio.Layout);
        Assert.NotNull(_runtime.OptionStatesOf("radio", "Repeat"));
        panel.Dispose();
    }

    [Fact]
    public void A_line_click_chooses_once_and_a_click_on_the_chosen_line_writes_nothing()
    {
        var panel = UiApi.Panel("Controls", _owner, "example.radio.test");
        var radio = panel.Radio("Mid", new[] { "warn", "disconnect" }, "warn", new[] { "Warn", "Disconnect" });
        var writes = new List<string>();
        radio.OnChanged(v => writes.Add(v));

        Assert.True(_runtime.FireOption("radio", "Mid", 1));
        Assert.True(_runtime.FireOption("radio", "Mid", 1)); // the chosen line again: no toggle, no second write
        Assert.Equal(new[] { "disconnect" }, writes.ToArray());
        Assert.Equal(new[] { false, true }, _runtime.OptionStatesOf("radio", "Mid")!.ToArray());

        // A click on the row beside the lines does nothing (it used to move to the next option).
        _runtime.FireControlClick("radio", "Mid");
        Assert.Equal("disconnect", radio.Value);
        Assert.Single(writes);
        panel.Dispose();
    }

    [Fact]
    public void Descriptions_reach_the_runtime_and_the_readback()
    {
        var panel = UiApi.Panel("Controls", _owner, "example.radio.test");
        var radio = (UiOptionsHandle)panel.Radio("Mid", new[] { "warn", "disconnect" }, "disconnect",
            new[] { "Warn and pause it", "Disconnect the player" }, new[] { "Nobody is disconnected.", "" });
        Assert.Equal("Nobody is disconnected.", radio.Options[0].Description);
        Assert.Equal("", radio.Options[1].Description);

        var d = radio.Describe();
        Assert.Equal("disconnect", d["current"]);
        Assert.Equal("Disconnect the player", d["currentLabel"]);
        Assert.Equal("list", d["layout"]);
        var options = Assert.IsAssignableFrom<IList<object?>>(d["options"]);
        var first = Assert.IsType<Dictionary<string, object?>>(options[0]);
        Assert.Equal("warn", first["value"]);
        Assert.Equal("Nobody is disconnected.", first["description"]);
        Assert.Equal(false, first["selected"]);
        Assert.Equal(UiOptionsRules.RadioMarkOff, first["mark"]);
        Assert.Equal(true, ((Dictionary<string, object?>)options[1]!)["selected"]);

        // The bridge finds an option by value or by label.
        Assert.Equal(0, radio.Find("WARN"));
        Assert.Equal(0, radio.Find("Warn and pause it"));
        Assert.Equal(-1, radio.Find("maybe"));
        panel.Dispose();
    }

    [Fact]
    public void A_described_list_has_taller_lines()
    {
        Assert.Equal(UiOptionsRules.RadioLineWithDescriptionHeight, UiOptionsRules.LineHeight(Two(), multi: false));
        Assert.Equal(UiOptionsRules.RadioLineHeight, UiOptionsRules.LineHeight(UiSettingsControls.Options(new[] { "a", "b" }, null), multi: false));
        Assert.Equal(UiOptionsRules.RadioLineHeight, UiOptionsRules.LineHeight(Two(), multi: true));
        Assert.NotEqual(UiOptionsRules.RadioMark(true), UiOptionsRules.RadioMark(false));
    }

    [Fact]
    public void The_chosen_line_is_bright_and_the_others_dimmed()
    {
        Assert.True(Progenitor.Ui.GameArt.SettingsControlLook.RadioCaptionGrey(true) > Progenitor.Ui.GameArt.SettingsControlLook.RadioCaptionGrey(false) + 0.3f);
        Assert.True(Progenitor.Ui.GameArt.SettingsControlLook.RadioDescriptionGrey(true) > Progenitor.Ui.GameArt.SettingsControlLook.RadioDescriptionGrey(false));
    }

    // ---- the cell-row radio list (dialogs, the Players joining panel) ------------------------------------------------

    [Fact]
    public void Cell_rows_draw_a_line_per_option_and_an_explanation_row_under_it()
    {
        var list = new UiRadioList("ja-mid", Two(), "warn");
        var rows = list.Rows(600f);
        Assert.Equal(new[] { "ja-mid-0", "ja-mid-0-about", "ja-mid-1", "ja-mid-1-about" }, rows.Select(r => r.Key).ToArray());

        var chosen = rows[0].Cells;
        Assert.Equal(UiOptionsRules.RadioMarkOn, chosen[0].Text);
        Assert.Equal(CellTone.Gold, chosen[0].Tone);
        Assert.Equal(CellTone.Normal, chosen[1].Tone);
        var other = rows[2].Cells;
        Assert.Equal(UiOptionsRules.RadioMarkOff, other[0].Text);
        Assert.Equal(CellTone.Muted, other[1].Tone);
        Assert.Equal("A notice, then a disconnect.", rows[3].Cells[1].Text);

        // No explanation, no second row.
        var plain = new UiRadioList("k", UiSettingsControls.Options(new[] { "a", "b" }, null), "a").Rows(600f);
        Assert.Equal(2, plain.Count);
    }

    [Fact]
    public void Cell_row_choose_writes_once_never_toggles_and_respects_the_lock()
    {
        var list = new UiRadioList("ja-mid", Two(), "warn");
        Assert.False(list.Choose(0));
        Assert.True(list.Choose(1));
        Assert.Equal("disconnect", list.Current);
        Assert.False(list.Choose(1));
        Assert.False(list.Choose(5));

        var locked = new UiRadioList("ja-mid", Two(), "warn", locked: true);
        Assert.False(locked.Choose(1));
        Assert.Equal("warn", locked.Current);
        Assert.All(locked.Rows(600f).Where(r => !r.Key.EndsWith("-about", StringComparison.Ordinal)),
            r => Assert.Equal(CellTone.Disabled, r.Cells[1].Tone));
    }

    [Fact]
    public void Cell_row_readback_matches_the_settings_grids_shape()
    {
        var d = new UiRadioList("ja-mode", ClientPolicyText.ModeOptions, "approved").Describe();
        Assert.Equal("radio", d["kind"]);
        Assert.Equal("ja-mode", d["key"]);
        Assert.Equal("approved", d["current"]);
        Assert.Equal(ClientPolicyText.ModeApproved, d["currentLabel"]);
        var options = Assert.IsAssignableFrom<IList<object?>>(d["options"]);
        Assert.Equal(2, options.Count);
        var any = (Dictionary<string, object?>)options[0]!;
        Assert.Equal("any", any["value"]);
        Assert.Equal(ClientPolicyText.ModeAnyShort, any["description"]);
        Assert.Equal("ja-mode-0", any["row"]);
        Assert.Equal(JsonValueKind.Object, JsonSerializer.SerializeToElement(d).ValueKind);
    }

    [Fact]
    public void The_client_policy_choices_carry_their_explanations()
    {
        foreach (var options in new[] { ClientPolicyText.ModeOptions, ClientPolicyText.UnverifiedOptions, ClientPolicyText.MidSessionOptions })
        {
            Assert.Equal(2, options.Count);
            Assert.All(options, o => Assert.False(string.IsNullOrWhiteSpace(o.Description)));
        }

        Assert.Equal(ClientModRules.WordWarn, ClientPolicyText.MidSessionOptions[0].Value);
        Assert.Equal("refuse", ClientPolicyText.UnverifiedOptions[1].Value);
    }

    [Fact]
    public void The_server_tab_this_or_that_rows_are_radio_lists_with_explanations()
    {
        foreach (var spec in new[] { JoinAdminRows.MidSession(), JoinAdminRows.Unverified() })
        {
            Assert.True(spec.ChoiceRadio, spec.Label);
            Assert.Equal(spec.Choices.Count, spec.ChoiceDescriptions.Count);
            Assert.All(spec.ChoiceDescriptions, t => Assert.False(string.IsNullOrWhiteSpace(t)));
        }
    }

    // ---- RADIOCLICK1: real clicks reach the lines, in every host ----------------------------------------------------

    [Fact]
    public void A_plain_row_has_no_click_target_until_a_row_click_is_bound()
    {
        var panel = UiApi.Panel("Dialog", _owner, "example.radio.test");
        var cells = new List<CellSpec> { new CellSpec(CellKind.Text, "t").With(text: "line") };
        var row = panel.CellRow(new CellRowSpec("plain-row", cells, plain: true));
        var fake = _runtime.CellRow("plain-row")!;

        // Built as the Unity row is: a raycast fill and no Button, so a pointer click finds no handler.
        Assert.Null(fake.ClickEvent);
        Assert.False(fake.PointerClick());
        Assert.False(UiCellRowHandle.TryInvoke("plain-row", out var refused));
        Assert.Equal(false, refused["reachable"]);
        Assert.Equal(false, ((UiCellRowHandle)row).Describe()["rowClick"]);

        int clicks = 0;
        row.OnRowClick(() => clicks++);
        Assert.True(fake.EnsuredClick);
        Assert.True(fake.PointerClick());
        Assert.Equal(1, clicks);
        Assert.Equal(true, ((UiCellRowHandle)row).Describe()["rowClick"]);

        // A list row is built with its target and wired when it is added, as before.
        panel.CellRow(new CellRowSpec("list-row", cells));
        Assert.NotNull(_runtime.CellRow("list-row")!.ClickEvent);
        Assert.False(_runtime.CellRow("list-row")!.EnsuredClick);
        panel.Dispose();
    }

    [Fact]
    public void A_pointer_click_on_a_radio_line_or_its_explanation_chooses_that_option_once()
    {
        var panel = UiApi.Panel("Dialog", _owner, "example.radio.test");
        var writes = new List<string>();
        var list = UiRadioList.Draw(panel, "ja-mid", Two(), "warn", 600f, locked: false, v => writes.Add(v));

        Assert.True(_runtime.CellRow("ja-mid-1")!.PointerClick());
        Assert.Equal(new[] { "disconnect" }, writes.ToArray());
        Assert.True(_runtime.CellRow("ja-mid-1-about")!.PointerClick()); // already chosen: no second write
        Assert.True(_runtime.CellRow("ja-mid-0-about")!.PointerClick());
        Assert.Equal(new[] { "disconnect", "warn" }, writes.ToArray());

        var options = (IList<object?>)list.Describe()["options"]!;
        Assert.All(options, o => Assert.Equal(true, ((Dictionary<string, object?>)o!)["clickable"]));
        panel.Dispose();
    }

    [Fact]
    public void A_locked_radio_list_looks_disabled_says_why_and_takes_no_click()
    {
        var panel = UiApi.Panel("Dialog", _owner, "example.radio.test");
        var writes = new List<string>();
        var list = UiRadioList.Draw(panel, "ja-mid", Two(), "warn", 600f, locked: true, v => writes.Add(v), lockedWhy: "Locked by the operator.");

        Assert.False(_runtime.CellRow("ja-mid-1")!.PointerClick());
        Assert.Empty(writes);
        var d = list.Describe();
        Assert.Equal(true, d["locked"]);
        Assert.Equal("Locked by the operator.", d["lockedWhy"]);
        Assert.All((IList<object?>)d["options"]!, o => Assert.Equal(false, ((Dictionary<string, object?>)o!)["clickable"]));
        Assert.All(list.Rows(600f).Where(r => !r.Key.EndsWith("-about", StringComparison.Ordinal)),
            r => Assert.Equal(CellTone.Disabled, r.Cells[0].Tone));
        Assert.Equal("", new UiRadioList("k", Two(), "warn").LockedWhy);
        panel.Dispose();
    }

    // ---- RADIOCLICK1: the metrics every host shares -----------------------------------------------------------------

    [Fact]
    public void Option_lines_and_explanations_are_compact_with_tight_and_moderate_gaps()
    {
        var rows = new UiRadioList("ja-mid", Two(), "warn").Rows(600f);
        Assert.All(rows, r => Assert.True(r.Compact, r.Key));
        Assert.Equal(UiRadioList.LineHeight, rows[0].Height);
        Assert.Equal(-1f, rows[0].GapAbove);                           // the heading's ordinary gap above the list
        Assert.Equal(UiRadioList.DescriptionGap, rows[1].GapAbove);
        Assert.Equal(UiRadioList.AboutHeight(1), rows[1].Height);
        Assert.Equal(UiRadioList.OptionGap, rows[2].GapAbove);
        Assert.InRange(UiRadioList.DescriptionGap, 0f, 6f);
        Assert.InRange(UiRadioList.OptionGap, 8f, 14f);

        // Stacked by the panel: label line 22, 2 px, explanation 19, 10 px, the next line. Before: 40 + 8 + 40 + 8.
        var tops = PanelLayout.StackCellRows(rows);
        Assert.Equal(UiRadioList.LineHeight + UiRadioList.DescriptionGap, tops[1] - tops[0]);
        Assert.Equal(UiRadioList.AboutHeight(1) + UiRadioList.OptionGap, tops[2] - tops[1]);
        Assert.Equal(53f, tops[2] - tops[0]);
    }

    [Fact]
    public void The_explanation_starts_under_the_label_not_at_the_row_edge()
    {
        const float width = 600f;
        var rows = new UiRadioList("ja-mid", Two(), "warn").Rows(width);
        float LeftOf(CellRowSpec row, string key) =>
            MenuPanelLayout.CellColumns(width, row).Single(p => p.Key == key).Left + width * .5f;

        float label = LeftOf(rows[0], UiRadioList.CellLabel);
        float about = LeftOf(rows[1], UiRadioList.CellAbout);
        Assert.Equal(UiRadioList.LabelInset, label, 2);
        Assert.Equal(label, about, 2);
        Assert.Equal(UiRadioList.MarkWidth, MenuPanelLayout.CellColumns(width, rows[0]).Single(p => p.Key == UiRadioList.CellMark).Width, 2);
    }

    /// <summary>
    /// RADIOCLICK2 (live 2026-10-06): the circle was not drawn in the Players joining panel. In its 20 px column the
    /// glyph did not fit and the ellipsis clamp replaced it with nothing. The column now holds the glyph and the cell
    /// is a glyph cell (drawn whole).
    /// </summary>
    [Fact]
    public void The_circle_cell_is_visible_wide_enough_for_its_glyph_and_never_truncated()
    {
        foreach (float width in new[] { 300f, DialogLayout.ContentRowWidth(), 900f })
        {
            var rows = new UiRadioList("ja-mid", Two(), "warn").Rows(width);
            foreach (var line in rows.Where(r => !r.Key.EndsWith("-about", StringComparison.Ordinal)))
            {
                var mark = line.Find(UiRadioList.CellMark)!;
                Assert.True(mark.Visible);
                Assert.Equal(CellWrap.Glyph, mark.Wrap);
                Assert.False(string.IsNullOrEmpty(mark.Text));
                var placed = MenuPanelLayout.CellColumns(width, line).Single(p => p.Key == UiRadioList.CellMark);
                Assert.True(placed.Visible);
                Assert.True(placed.Width >= UiRadioList.MarkGlyphNeed, width + ": " + placed.Width);
                Assert.True(placed.Width >= mark.FontSize, "the column is at least an em wide");
            }
        }

        // The settings grid's circle sits at RadioMarkInset with the label after it: the glyph fits in between.
        Assert.True(4f + UiRadioList.MarkGlyphNeed <= UiRadioList.LabelInset);
        string source = System.IO.File.ReadAllText(System.IO.Path.Combine(RepoRoot(), "src", "Progenitor.Core", "Ui", "UnityUiRuntime.cs"));
        Assert.Contains("else if (spec.Wrap == CellWrap.Glyph)", source);
        Assert.Contains("try { tmp.overflowMode = TextOverflowModes.Overflow; } catch { /* */ }", source);
    }

    /// <summary>
    /// RADIOCLICK2 (live 2026-10-06): a one-line explanation reserved two lines (the game-free estimate over-counts), so
    /// the gap after it was larger than after the others. The runtime fits the row to the text TMP measures.
    /// </summary>
    [Fact]
    public void The_explanation_row_fits_the_lines_it_draws()
    {
        var about = new UiRadioList("ja-mid", ClientPolicyText.MidSessionOptions, "warn").Rows(DialogLayout.ContentRowWidth())
            .Where(r => r.Key.EndsWith("-about", StringComparison.Ordinal)).ToList();
        Assert.All(about, r => Assert.True(r.FitWrap, r.Key));
        var row = about[0];
        float oneLine = 15.6f; // 13 px text, one line as TMP measures it
        Assert.Equal(oneLine + CellRowSpec.FitWrapPad, row.FitWrapHeight(oneLine, UiRadioList.AboutFontSize), 2);
        Assert.True(row.FitWrapHeight(oneLine, UiRadioList.AboutFontSize) <= UiRadioList.AboutHeight(1));
        // Unreadable measurement: the estimate stands; a runaway one is capped.
        Assert.Equal(row.Height, row.FitWrapHeight(0f, UiRadioList.AboutFontSize));
        Assert.Equal(row.Height, row.FitWrapHeight(float.NaN, UiRadioList.AboutFontSize));
        Assert.Equal(row.Height * 2f, row.FitWrapHeight(10_000f, UiRadioList.AboutFontSize));
        // A row that does not ask keeps its height whatever is measured.
        var fixedRow = new CellRowSpec("k", row.Cells, height: 40f);
        Assert.Equal(40f, fixedRow.FitWrapHeight(15f, 13f));
        // The runtime row reads its height through the fit, and places its cells at that height.
        string source = System.IO.File.ReadAllText(System.IO.Path.Combine(RepoRoot(), "src", "Progenitor.Core", "Ui", "UnityUiRuntime.cs"));
        Assert.Contains("public float Height => _spec.FitWrap ? FittedHeight() : _spec.Height;", source);
        Assert.Contains("            float rowHeight = Height;", source);
    }

    [Fact]
    public void Long_explanations_wrap_inside_the_panel_and_the_rows_grow_to_hold_them()
    {
        string longText = string.Join(" ", Enumerable.Repeat("a fairly long explanation", 12));
        var rows = new UiRadioList("k", new[] { new UiChoiceOption("a", "A", longText) }, "a").Rows(500f);
        var about = rows[1];
        Assert.Equal(CellWrap.Wrap, about.Cells[1].Wrap);
        int lines = UiRadioList.AboutLines(longText, 500f);
        Assert.InRange(lines, 2, UiRadioList.MaxAboutLines);
        Assert.Equal(UiRadioList.AboutHeight(lines), about.Height);
    }

    [Fact]
    public void The_mid_session_choices_use_short_sentences_that_name_required_mods()
    {
        foreach (var o in ClientPolicyText.MidSessionOptions)
        {
            // REQRULE1: they name required mods too: at most three short sentences on three lines.
            Assert.InRange(o.Description.Split(". ", StringSplitOptions.RemoveEmptyEntries).Length, 1, 3);
            Assert.InRange(UiRadioList.AboutLines(o.Description, DialogLayout.ContentRowWidth()), 1, 3);
            Assert.Contains("required mod", o.Description, StringComparison.Ordinal);
        }

        Assert.Equal(ClientPolicyText.MidSessionDisconnectShort, ClientPolicyText.MidSessionOptions[1].Description);
        Assert.Contains("60 seconds", ClientPolicyText.MidSessionDisconnectShort);
    }


    [Fact]
    public void The_settings_grid_radio_lines_use_the_same_metrics()
    {
        Assert.Equal(UiRadioList.LineHeight + UiRadioList.DescriptionGap + UiRadioList.AboutLineHeight + UiRadioList.OptionGap,
            UiOptionsRules.RadioLineWithDescriptionHeight);
        Assert.Equal(30f, UiOptionsRules.RadioLineHeight);
        string source = System.IO.File.ReadAllText(System.IO.Path.Combine(RepoRoot(), "src", "Progenitor.Core", "Ui", "UnityUiRuntime.cs"));
        Assert.Contains("private const float RadioCaptionInset = UiRadioList.LabelInset;", source);
        // RADIOCLICK3 (live: no circle on the Server tab's radios): the grid's circle is fitted like the cell-row one:
        // its own MarkWidth box ending before the label, centered, drawn whole, the drawable glyph from the start, in front.
        Assert.Contains("            FitRadioMark(mark);", source);
        int fit = source.IndexOf("private static void FitRadioMark(TextMeshProUGUI? mark)", StringComparison.Ordinal);
        Assert.True(fit > 0);
        string body = source.Substring(fit, 1400);
        Assert.Contains("rt.offsetMax = new Vector2(RadioMarkLeft + UiRadioList.MarkWidth, rt.offsetMax.y);", body);
        Assert.Contains("try { mark.overflowMode = TextOverflowModes.Overflow; } catch { /* */ }", body);
        Assert.Contains("try { mark.text = DrawableGlyph(mark, UiOptionsRules.RadioMark(false)); } catch { /* */ }", body);
        Assert.Contains("try { mark.transform.SetAsLastSibling(); } catch { /* */ }", body);
        Assert.True(0f + UiRadioList.MarkWidth <= UiRadioList.LabelInset, "the circle box ends before the label");
        Assert.True(UiRadioList.MarkWidth >= UiRadioList.MarkGlyphNeed);
        Assert.Contains("mark.text = DrawableGlyph(mark, UiOptionsRules.RadioMark(on));", source);
        // Every circle has a drawable fallback ending in ASCII.
        foreach (bool on in new[] { true, false })
        {
            var forms = UiOptionsRules.GlyphCandidates(UiOptionsRules.RadioMark(on));
            Assert.Equal(UiOptionsRules.RadioMark(on), forms[0]);
            Assert.True(forms[^1].Length == 1 && forms[^1][0] < 0x80);
        }
        Assert.Contains("UiRadioList.LineHeight + UiRadioList.DescriptionGap,", source);
    }

    private static string RepoRoot()
    {
        var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "Progenitor.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
    }

    [Fact]
    public void Click_at_parses_a_row_or_a_point()
    {
        using var row = JsonDocument.Parse("{\"action\":\"click_at\",\"row\":\"ja-mid-1\"}");
        var byRow = Progenitor.Dev.Ops.ModsUiArgs.Parse(row.RootElement.Clone());
        Assert.Equal("click_at", byRow.Action);
        Assert.Equal("ja-mid-1", byRow.Label);
        Assert.False(Progenitor.Dev.Ops.ModsUiArgs.RequiresOpenWorkspace(byRow.Action));

        using var point = JsonDocument.Parse("{\"action\":\"click_at\",\"x\":120,\"y\":340.5}");
        var byPoint = Progenitor.Dev.Ops.ModsUiArgs.Parse(point.RootElement.Clone());
        Assert.Equal(120f, byPoint.X);
        Assert.Equal(340.5f, byPoint.Y);

        using var missing = JsonDocument.Parse("{\"action\":\"click_at\",\"x\":1}");
        Assert.Throws<ArgumentException>(() => Progenitor.Dev.Ops.ModsUiArgs.Parse(missing.RootElement.Clone()));
        Assert.Contains("click_at", Progenitor.Dev.Ops.ModsUiArgs.Actions);
    }

    // ---- the bridge -------------------------------------------------------------------------------------------------

    [Fact]
    public void Radio_choose_parses_and_needs_an_open_workspace()
    {
        using var doc = JsonDocument.Parse("{\"action\":\"radio_choose\",\"label\":\"Repeat\",\"value\":\"Days\"}");
        var request = Progenitor.Dev.Ops.ModsUiArgs.Parse(doc.RootElement.Clone());
        Assert.Equal("radio_choose", request.Action);
        Assert.Equal("Repeat", request.Label);
        Assert.Equal("Days", request.Value);
        Assert.True(Progenitor.Dev.Ops.ModsUiArgs.RequiresOpenWorkspace(request.Action));
        Assert.Contains("radio_choose", Progenitor.Dev.Ops.ModsUiArgs.Actions);

        using var missing = JsonDocument.Parse("{\"action\":\"radio_choose\",\"label\":\"Repeat\"}");
        Assert.Throws<ArgumentException>(() => Progenitor.Dev.Ops.ModsUiArgs.Parse(missing.RootElement.Clone()));
    }
}
