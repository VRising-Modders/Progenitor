using System;
using Progenitor.Ui;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// SEARCHCLEAR1 (requested 2026-10-09): the workspace search boxes (the Mods search, the Install
/// search) draw a clear button while they have text; Escape clears, then leaves the box, then closes;
/// closing the workspace empties them. The Unity half is pinned to its source here; the Escape order and
/// the reset on close are driven through the menu in <c>ModsMenuTests</c> and <c>InstallListPolishTests</c>.
/// </summary>
public sealed class SearchClearTests
{
    [Fact]
    public void The_clear_button_shows_only_while_the_box_has_text_opposite_the_search_mark()
    {
        Assert.False(MenuRowStyle.SearchClearVisible(null));
        Assert.False(MenuRowStyle.SearchClearVisible(""));
        Assert.True(MenuRowStyle.SearchClearVisible("u"));
        foreach (string? text in new[] { null, "", "update" })
            Assert.NotEqual(MenuRowStyle.SearchHintVisible(text), MenuRowStyle.SearchClearVisible(text));
    }

    [Fact]
    public void The_clear_button_uses_the_kits_glyph_and_never_draws_a_missing_glyph_box()
    {
        Assert.Equal("×", MenuRowStyle.SearchClearGlyph);
        // The Keybinds tab's Unbind mark is the same kit glyph.
        Assert.Equal(MenuRowStyle.SearchClearGlyph, KeybindsTabLayout.ClearGlyph);
        Assert.Equal(MenuRowStyle.SearchClearGlyph, MenuRowStyle.PickSearchClearGlyph(_ => true));
        Assert.Equal(MenuRowStyle.SearchClearFallbackGlyph, MenuRowStyle.PickSearchClearGlyph(_ => false));
        Assert.Equal(MenuRowStyle.SearchClearGlyph, MenuRowStyle.PickSearchClearGlyph(null));
    }

    [Fact]
    public void The_clear_button_is_a_square_on_the_box_and_the_typed_text_stops_short_of_it()
    {
        // The field is 28 units tall; the target is the whole height and as wide.
        Assert.Equal(28f, MenuRowStyle.SearchClearWidth);
        Assert.True(MenuRowStyle.SearchClearWidth >= MenuRowStyle.SearchGlyphWidth);
        Assert.Equal(MenuRowStyle.SearchClearWidth, MenuRowStyle.SearchTextRightInset(hasClear: true, padX: 8f));
        Assert.Equal(8f, MenuRowStyle.SearchTextRightInset(hasClear: false, padX: 8f));
    }

    [Fact]
    public void The_clear_glyph_brightens_on_hover_and_darkens_when_pressed()
    {
        var rest = MenuRowStyle.SearchClearColor(hovered: false, pressed: false);
        var hover = MenuRowStyle.SearchClearColor(hovered: true, pressed: false);
        var pressed = MenuRowStyle.SearchClearColor(hovered: true, pressed: true);
        // At rest it is the search mark's color.
        Assert.Equal((MenuRowStyle.SearchPlaceholderR, MenuRowStyle.SearchPlaceholderG, MenuRowStyle.SearchPlaceholderB, MenuRowStyle.SearchPlaceholderA), rest);
        float L((float R, float G, float B, float A) c) => MenuRowStyle.Luminance(c.R, c.G, c.B);
        Assert.True(L(hover) > L(rest));
        Assert.True(L(pressed) < L(rest));
        Assert.InRange(hover.R, 0f, 1f);
        Assert.InRange(hover.G, 0f, 1f);
        Assert.InRange(hover.B, 0f, 1f);
        // Readable on the search fill at rest.
        Assert.True(MenuRowStyle.ContrastRatio(rest.R, rest.G, rest.B, MenuRowStyle.SearchR, MenuRowStyle.SearchG, MenuRowStyle.SearchB)
            >= MenuRowStyle.SearchHintMinContrastVsFill);
    }

    [Theory]
    [InlineData(true, "update", (int)EscapeStage.ClearSearch)]
    [InlineData(true, "", (int)EscapeStage.LeaveSearch)]
    [InlineData(false, "update", (int)EscapeStage.Close)]
    [InlineData(false, "", (int)EscapeStage.Close)]
    public void Escape_clears_then_leaves_then_closes(bool focused, string text, int expected)
    {
        foreach (var host in new[] { WorkspaceHost.InGame, WorkspaceHost.Title, WorkspaceHost.Standalone })
        {
            var outcome = WorkspaceInputPolicy.Escape(host, captureOwnsEscape: false, dialogOpen: false, focused, text);
            Assert.Equal((EscapeStage)expected, outcome.Stage);
            Assert.False(outcome.SwallowPairedBack);
        }
    }

    [Fact]
    public void Leaving_the_box_takes_the_press_so_its_paired_back_does_not_close()
    {
        var sweep = new UiEscapeSweep();
        Assert.True(sweep.TryConsume(UiSearchEscape.Decision.Leave));
        Assert.True(sweep.Cleared);
        Assert.True(sweep.TryConsume(UiSearchEscape.Decision.Close));
        sweep.Release();
        Assert.False(sweep.TryConsume(UiSearchEscape.Decision.Close));
    }

    [Fact]
    public void The_runtime_wires_the_clear_button_into_both_boxes_and_the_pumps_click_path()
    {
        string runtime = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "UnityUiRuntime.cs");
        // The Mods search row and an input cell marked Clearable (the Install search) get the button.
        SourcePin.AssertUncommented(SourcePin.MethodBody(runtime, "public IRuntimeWidget? AddInput("), "AttachSearchClear(control, field);");
        SourcePin.AssertUncommented(runtime, "if (spec.Clearable && spec.EditorLines <= 0) AttachSearchClear(control, field);");
        // The hint sync (every edit, every value written from code) also shows or hides the button.
        SourcePin.AssertUncommented(SourcePin.MethodBody(runtime, "private static void SyncSearchHint("), "SyncSearchClear(field, field.text);");
        // The pump's click path tests the button before the field it sits in.
        string focus = SourcePin.MethodBody(runtime, "private static bool FocusFieldAt(");
        SourcePin.AssertUncommented(focus, "if (TryPressSearchClearAt(x, y, dialogRoot)) return true;");
        Assert.True(focus.IndexOf("TryPressSearchClearAt", StringComparison.Ordinal) < focus.IndexOf("Consider(_armedFields[i].Field);", StringComparison.Ordinal));
        SourcePin.AssertUncommented(SourcePin.MethodBody(runtime, "private static void PumpTextField("), "SyncSearchClearTints(pointer, held);");
        // Escape's focused search is the focused workspace search box, the Install search included.
        SourcePin.AssertUncommented(SourcePin.MethodBody(runtime, "public bool TryReadSearchField("), "IsWorkspaceSearch(_ownedFocusField)");
    }

    [Fact]
    public void The_clear_button_reads_no_keys_and_is_not_a_selectable()
    {
        string clear = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "UnityUiRuntime.SearchClear.cs");
        // A Button would take the event system's selection on a press and deselect the field.
        Assert.DoesNotContain("<Button>", clear, StringComparison.Ordinal);
        Assert.DoesNotContain("Keyboard.current", clear, StringComparison.Ordinal);
        Assert.DoesNotContain("Mouse.current", clear, StringComparison.Ordinal);
        Assert.DoesNotContain("UnityEngine.Input", clear, StringComparison.Ordinal);
        // The click empties the box through the owned edit path, so the pane's value-changed handler re-filters.
        SourcePin.AssertUncommented(SourcePin.MethodBody(clear, "private static void ClearSearchFromButton("), "WriteFieldState(field, before, new UiTextEditState(\"\", 0, 0));");
        SourcePin.AssertUncommented(SourcePin.MethodBody(clear, "private static void ClearSearchFromButton("), "FocusOwnedField(field);");
    }

    [Fact]
    public void A_status_read_never_copies_the_mods_search_into_the_install_search()
    {
        // Live 2026-10-09: the status read synced the parked Mods search row into the Install search,
        // so the Install box showed the Mods text and typing there was undone.
        string mods = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "ModsMenu.cs");
        string body = SourcePin.MethodBody(mods, "private static void SyncSearchFromLiveField(");
        SourcePin.AssertUncommented(body, "if (_tabState != null && _tabState.Selected != ModsTab.Mods) return;");
        Assert.DoesNotContain("_installPane.Search = live", body, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_workspace_close_empties_the_searches()
    {
        string mods = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "ModsMenu.cs");
        SourcePin.AssertUncommented(SourcePin.MethodBody(mods, "public static void Close("), "ResetSearchesForClose();");
        SourcePin.AssertUncommented(SourcePin.MethodBody(mods, "internal static void NotifyHiddenByEscape("), "ResetSearchesForClose();");
        // A tab switch does not.
        Assert.DoesNotContain("ResetSearchesForClose", SourcePin.MethodBody(mods, "internal static void SelectWorkspaceTab("), StringComparison.Ordinal);
    }
}
