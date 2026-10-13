using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using BepInEx.Configuration;
using Progenitor.Ui;
using Xunit;
using UiApi = Progenitor.Ui.Ui;

namespace Progenitor.Tests;

/// <summary>
/// Settings-kit primitives that do not wait on the contract owner's descriptor names.
/// Asserts values, commit counts and drawability (meshCount), not that a factory method
/// exists in source.
/// </summary>
[Collection("PluginLogger")]
public class UiControlPrimitiveTests : IDisposable
{
    private readonly TestLogSource _log = new();
    private readonly UiTests.FakeUiRuntime _client = new() { IsClient = true };
    private readonly object _owner = new();

    public UiControlPrimitiveTests()
    {
        Plugin.Logger = _log;
        UiApi.ResetForTests();
        UiApi.SetRuntimeForTests(_client);
    }

    public void Dispose()
    {
        UiApi.ResetForTests();
        Plugin.Logger = null;
    }

    [Fact]
    public void Choice_commits_immediately_keeps_an_unknown_value_and_navigates_by_keyboard()
    {
        var options = new[]
        {
            new UiChoiceOption("slow", "Slow"),
            new UiChoiceOption("fast", "Fast"),
            new UiChoiceOption("off", "Off"),
        };
        var state = new UiChoiceState(options, "fast");
        Assert.Equal(UiCommitPolicy.Immediate, UiChoiceState.Commit);
        Assert.Equal("fast", state.Value);
        Assert.Equal("Fast", state.DisplayLabel);
        Assert.True(state.ValueInList);

        Assert.True(state.Select("nope"));
        Assert.Equal("nope", state.Value);
        Assert.False(state.ValueInList);
        Assert.Equal("nope (not in list)", state.DisplayLabel);
        Assert.False(state.IsOpen);

        state.Open();
        Assert.True(state.IsOpen);
        Assert.True(state.MoveHighlight(1));
        Assert.True(state.ConfirmHighlight());
        Assert.False(state.IsOpen);
        Assert.True(state.ValueInList);
        Assert.NotEqual("nope", state.Value);

        var panel = UiApi.Panel("Choice", _owner, "example.ui.test");
        var choice = UiPrimitives.Choice(panel, "Mode", options, "slow");
        Assert.True(choice.Enabled);
        Assert.Equal(UiCommitPolicy.Immediate, choice.Commit);
        string? seen = null;
        int commits = 0;
        choice.OnChanged(v =>
        {
            seen = v;
            commits++;
        });
        choice.Open();
        choice.MoveHighlight(1);
        Assert.True(choice.ConfirmHighlight());
        Assert.Equal(1, commits);
        Assert.Equal(choice.Value, seen);
        Assert.Equal("Fast", choice.DisplayLabel);

        choice.Value = "ghost";
        Assert.Equal("ghost", choice.Value);
        Assert.False(choice.ValueInList);
        Assert.Equal(1, commits);
        panel.Dispose();
    }

    /// <summary>User, 2026-10-10: an open list's highlight follows the pointer, and stays on the last option it was over.</summary>
    [Fact]
    public void The_open_lists_highlight_follows_the_hover()
    {
        var options = new[] { new UiChoiceOption("apple", "Apple"), new UiChoiceOption("pear", "Pear"), new UiChoiceOption("plum", "Plum") };
        var panel = UiApi.Panel("Choice", _owner, "example.ui.test");
        var choice = (UiChoiceHandle)UiPrimitives.Choice(panel, "Fruit", options, "apple");
        choice.Open();
        Assert.Equal(0, choice.HighlightIndex);
        choice.HoverOption(1);
        Assert.Equal(1, choice.HighlightIndex);
        choice.HoverOption(-1);
        Assert.Equal(1, choice.HighlightIndex);
        Assert.True(choice.ConfirmHighlight());
        Assert.Equal("pear", choice.Value);
        panel.Dispose();
    }

    /// <summary>
    /// DROPHOVER1 (user, 2026-10-11: hovering an option neither highlighted it nor showed its description). The highlighted
    /// option is lit brighter than the current one; the description box is at least one option tall and grows with its text;
    /// the drawn list paints the highlight it is sent (the float value) instead of a button tint that multiplied a
    /// transparent fill; and it shows the highlighted option's description beside the list unless a details column does.
    /// </summary>
    [Fact]
    public void The_drawn_list_paints_the_highlight_and_shows_its_description()
    {
        Assert.True(UiChoiceState.OptionFillAlpha(current: false, highlighted: true) > UiChoiceState.OptionFillAlpha(current: true, highlighted: false));
        Assert.True(UiChoiceState.OptionFillAlpha(current: true, highlighted: false) > UiChoiceState.OptionFillAlpha(current: false, highlighted: false));
        Assert.Equal(0f, UiChoiceState.OptionFillAlpha(current: false, highlighted: false));
        Assert.Equal(34f, UiChoiceState.TipHeight(5f, 34f));
        Assert.Equal(60f + 2f * UiChoiceState.TipPad, UiChoiceState.TipHeight(60f, 34f));
        Assert.Equal(34f, UiChoiceState.TipHeight(float.NaN, 34f));

        string runtime = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "UnityUiRuntime.cs");
        SourcePin.AssertUncommented(runtime, "AfterFloatWrite(value);");
        SourcePin.AssertUncommented(SourcePin.MethodBody(runtime, "public void BuildList(GameObject control, IReadOnlyList<UiChoiceOption> options)"),
            "button.transition = Selectable.Transition.None;");
        string mark = SourcePin.MethodBody(runtime, "private void Mark(string current)");
        SourcePin.AssertUncommented(mark, "UiChoiceState.OptionFillAlpha(on, lit)");
        SourcePin.AssertUncommented(mark, "ShowTip(");
        string tip = SourcePin.MethodBody(runtime, "private void ShowTip(string text)");
        SourcePin.AssertUncommented(tip, "Owner?.ColumnShown == true");

        string handles = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "UiControlHandles.cs");
        SourcePin.AssertUncommented(SourcePin.MethodBody(handles, "private void WriteWidget()"), "r.FloatValue = _state.HighlightIndex;");
    }

    [Fact]
    public void Choice_label_is_not_drawable_with_a_zero_mesh()
    {
        Assert.True(UiControlDraw.ChoiceLabelIsDrawable(
            "Fast", assigned: true, active: true, graphicEnabled: true,
            alpha: 1f, rectW: 120f, rectH: 20f, meshCount: 4));
        Assert.False(UiControlDraw.ChoiceLabelIsDrawable(
            "Fast", assigned: true, active: true, graphicEnabled: true,
            alpha: 1f, rectW: 120f, rectH: 20f, meshCount: 0));
        Assert.Equal("v", UiControlDraw.PickGlyph("v", ">", s => s == "v"));
        Assert.Equal("", UiControlDraw.PickGlyph("v", ">", _ => false));
    }

    [Fact]
    public void Numeric_parse_is_culture_safe_and_stepper_commits_immediately()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            var commaCulture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
            commaCulture.NumberFormat.NumberDecimalSeparator = ",";
            commaCulture.NumberFormat.NumberGroupSeparator = " ";
            CultureInfo.CurrentCulture = commaCulture;
            Assert.True(UiNumericParse.TryParse("1,5", out double comma, out _));
            Assert.Equal(1.5, comma, 5);
            Assert.True(UiNumericParse.TryParse("1.5", out double period, out _));
            Assert.Equal(1.5, period, 5);
            Assert.True(UiNumericParse.TryParse("1.234,56", out double eu, out _));
            Assert.Equal(1234.56, eu, 5);
            Assert.True(UiNumericParse.TryParse("1,234.56", out double us, out _));
            Assert.Equal(1234.56, us, 5);
            string formatted = UiNumericParse.Format(1.5, 1);
            Assert.Equal("1.5", formatted);
            Assert.DoesNotContain(",", formatted);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }

        var state = new UiNumericState(4, 0, 10, 1, 0, "s");
        Assert.Equal(UiCommitPolicy.OnSubmit, UiNumericState.TypedCommit);
        Assert.Equal(UiCommitPolicy.Immediate, UiNumericState.StepperCommit);
        Assert.True(state.StepBy(1));
        Assert.Equal(5, state.Committed);
        state.SetDraft("1,5");
        Assert.True(state.TryCommitDraft(out _));
        Assert.Equal(2, state.Committed);

        var panel = UiApi.Panel("Numeric", _owner, "example.ui.test");
        var numeric = UiPrimitives.Numeric(panel, "Count", 2, 0, 8, 2, decimals: 0, units: "x");
        int commits = 0;
        numeric.OnChanged(_ => commits++);
        Assert.True(numeric.StepBy(1));
        Assert.Equal(4, numeric.Value);
        Assert.Equal(1, commits);
        numeric.SetDraft("9");
        Assert.True(numeric.TryCommitDraft());
        Assert.Equal(8, numeric.Value);
        Assert.Equal(2, commits);
        Assert.True(UiControlDraw.NumericTextIsDrawable(
            numeric.Draft, assigned: true, active: true, graphicEnabled: true,
            alpha: 1f, rectW: 80f, rectH: 20f, meshCount: numeric.Draft.Length));
        Assert.False(UiControlDraw.NumericTextIsDrawable(
            numeric.Draft, assigned: true, active: true, graphicEnabled: true,
            alpha: 1f, rectW: 80f, rectH: 20f, meshCount: 0));
        panel.Dispose();
    }

    [Fact]
    public void Slider_drag_previews_locally_and_commits_once_on_release()
    {
        var state = new UiSliderEntryState(0.2f, 0f, 1f, 0.1f);
        Assert.Equal(UiCommitPolicy.OnRelease, UiSliderEntryState.DragCommit);
        Assert.Equal(UiCommitPolicy.Immediate, UiSliderEntryState.KeyboardCommit);
        Assert.Equal(UiCommitPolicy.OnSubmit, UiSliderEntryState.ExactCommit);

        state.BeginDrag();
        state.DragTo(0.35f);
        state.DragTo(0.47f);
        state.DragTo(0.51f);
        Assert.True(state.Dragging);
        Assert.NotEqual(state.Preview, state.Committed);
        Assert.True(state.EndDrag());
        Assert.False(state.Dragging);
        Assert.Equal(state.Preview, state.Committed);
        Assert.False(state.EndDrag());

        var panel = UiApi.Panel("Slider", _owner, "example.ui.test");
        var slider = UiPrimitives.SliderEntry(panel, "Intensity", 0f, 1f, 0.1f, 0.2f);
        int commits = 0;
        int previews = 0;
        float lastPreview = 0f;
        slider.OnChanged(_ => commits++);
        slider.OnPreview(v =>
        {
            previews++;
            lastPreview = v;
        });
        slider.BeginDrag();
        slider.DragTo(0.3f);
        slider.DragTo(0.6f);
        slider.DragTo(0.8f);
        Assert.Equal(0, commits);
        Assert.Equal(3, previews);
        Assert.Equal(0.8f, lastPreview, 3);
        Assert.True(slider.EndDrag());
        Assert.Equal(1, commits);
        Assert.Equal(0.8f, slider.Value, 3);
        Assert.True(slider.Nudge(-1));
        Assert.Equal(2, commits);
        Assert.True(slider.TryCommitExact("0.4"));
        Assert.Equal(3, commits);
        Assert.Equal(0.4f, slider.Value, 3);

        Assert.True(UiControlDraw.SliderPartsAreDrawable(
            fillAssigned: true, handleAssigned: true,
            fillW: 80f, fillH: 6f, handleW: 14f, handleH: 14f,
            fillMesh: 1, handleMesh: 1));
        Assert.False(UiControlDraw.SliderPartsAreDrawable(
            fillAssigned: true, handleAssigned: true,
            fillW: 80f, fillH: 6f, handleW: 14f, handleH: 14f,
            fillMesh: 0, handleMesh: 1));
        panel.Dispose();
    }

    [Fact]
    public void Color_hex_round_trips_deterministically_and_the_swatch_needs_a_mesh()
    {
        var color = new UiColor(1f, 0.5f, 0f, 1f);
        Assert.Equal("#FF8000", color.ToHex(alpha: false));
        Assert.Equal("#FF8000FF", color.ToHex(alpha: true));
        Assert.True(UiColor.TryParseHex("ff8000", alpha: false, out var parsed));
        Assert.Equal(color.ToHex(false), parsed.ToHex(false));
        Assert.True(UiColor.TryParseHex("#FF800080", alpha: true, out var withAlpha));
        Assert.Equal("#FF800080", withAlpha.ToHex(true));

        var state = new UiColorState(color, alpha: false);
        Assert.Equal(UiCommitPolicy.OnSubmit, UiColorState.TypedCommit);
        state.SetHexDraft("00ff00");
        Assert.True(state.TryCommitHex(out _));
        Assert.Equal("#00FF00", state.HexCommitted);

        var panel = UiApi.Panel("Color", _owner, "example.ui.test");
        var picker = UiPrimitives.ColorPicker(panel, "Tint", color, alpha: false);
        int commits = 0;
        picker.OnChanged(_ => commits++);
        picker.SetHexDraft("#0000ff");
        Assert.True(picker.TryCommitHex());
        Assert.Equal(1, commits);
        Assert.Equal("#0000FF", picker.Value.ToHex(false));
        Assert.True(picker.SetComponent(UiColorChannel.R, 1f));
        Assert.Equal(2, commits);

        Assert.True(UiControlDraw.SwatchIsDrawable(
            assigned: true, active: true, graphicEnabled: true,
            graphicAlpha: 1f, rectW: 24f, rectH: 24f, meshCount: 1));
        Assert.False(UiControlDraw.SwatchIsDrawable(
            assigned: true, active: true, graphicEnabled: true,
            graphicAlpha: 1f, rectW: 24f, rectH: 24f, meshCount: 0));
        panel.Dispose();
    }

    [Fact]
    public void Keybind_picker_reuses_capture_cancel_clear_reset_and_names_a_conflict()
    {
        string dir = Path.Combine(Path.GetTempPath(), "progenitor-keybind-picker-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var file = new ConfigFile(Path.Combine(dir, "a.mod.cfg"), saveOnInit: true);
            var other = new ConfigFile(Path.Combine(dir, "b.mod.cfg"), saveOnInit: true);
            using var bind = Keybinds.Register(
                _owner, "a.mod", "Demo", file, new KeyChord(UiKey.F9), "Demo.", () => { });
            using var clash = Keybinds.Register(
                _owner, "b.mod", "Other", other, new KeyChord(UiKey.F8), "Other.", () => { });

            var panel = UiApi.Panel("Keys", _owner, "example.ui.test");
            var picker = UiPrimitives.KeybindPicker(panel, "Demo key", "a.mod", "Demo");
            Assert.Equal(UiCommitPolicy.Immediate, picker.Commit);
            Assert.Equal("F9", picker.ChordText);
            Assert.False(picker.Capturing);
            Assert.Null(picker.ConflictNotice);

            Assert.True(picker.BeginCapture());
            Assert.True(picker.Capturing);
            Assert.Equal(UiKeybindPickerState.CapturingLabel, picker.ChordText);
            picker.CancelCapture();
            Assert.False(picker.Capturing);
            Assert.Equal("F9", picker.ChordText);

            int commits = 0;
            picker.OnChanged(_ => commits++);
            Assert.True(picker.Clear());
            Assert.Equal(1, commits);
            Assert.False(picker.Chord.IsBound);
            Assert.True(picker.ResetToDefault());
            Assert.Equal(2, commits);
            Assert.Equal(UiKey.F9, picker.Chord.Key);

            // KEYS2: a remap onto b.mod's F8 is refused with the holder named; the conflict is built unchecked.
            Assert.False(Keybinds.Rebind("a.mod", "Demo", new KeyChord(UiKey.F8)));
            Assert.True(Keybinds.Rebind("a.mod", "Demo", new KeyChord(UiKey.F8), checkConflicts: false));
            var after = UiPrimitives.KeybindPicker(panel, "Demo key", "a.mod", "Demo");
            Assert.NotNull(after.ConflictNotice);
            // KEYS1: the winner rule's sentence. a.mod and b.mod have no install time, so the first by
            // id keeps F8 and the notice on a.mod's picker names the one that lost it.
            Assert.StartsWith("Also bound by b.mod", after.ConflictNotice, StringComparison.Ordinal);
            Assert.Contains("not active there", after.ConflictNotice, StringComparison.Ordinal);

            Assert.True(UiControlDraw.KeybindChordIsDrawable(
                "F9", assigned: true, active: true, graphicEnabled: true,
                alpha: 1f, rectW: 80f, rectH: 20f, meshCount: 2));
            Assert.False(UiControlDraw.KeybindChordIsDrawable(
                "F9", assigned: true, active: true, graphicEnabled: true,
                alpha: 1f, rectW: 80f, rectH: 20f, meshCount: 0));
            panel.Dispose();
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); }
            catch { /* */ }
        }
    }

    [Fact]
    public void Search_placeholder_drawability_still_requires_a_six_glyph_mesh()
    {
        Assert.True(MenuRowStyle.HintGraphicIsDrawable(
            assigned: true, active: true, graphicEnabled: true,
            alpha: MenuRowStyle.SearchPlaceholderA, rectW: 282f, rectH: 20f,
            hintSiblingIndex: 1, fillSiblingIndex: -1, meshCount: 6));
        Assert.False(MenuRowStyle.HintGraphicIsDrawable(
            assigned: true, active: true, graphicEnabled: true,
            alpha: MenuRowStyle.SearchPlaceholderA, rectW: 282f, rectH: 20f,
            hintSiblingIndex: 1, fillSiblingIndex: -1, meshCount: 0));
    }
}
