using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx.Configuration;
using Progenitor.Networking;
using Progenitor.Ui;
using Progenitor.Ui.GameArt;
using Progenitor.WorldState;
using Xunit;
using UiApi = Progenitor.Ui.Ui;

namespace Progenitor.Tests;

/// <summary>
/// MENUKIT2: the game-look settings kit's game-free parts — the Options page values probe M1 read
/// (<see cref="GameSettingsLook"/>), the settings art ladder, the one-line row grid, the row marks and
/// the description column's model. Nothing here draws or touches the game; the runtime drawing is
/// switched over separately.
/// </summary>
[Collection("PluginLogger")]
public sealed class MenuKitSettingsTests : IDisposable
{
    private readonly UiTests.FakeUiRuntime _runtime = new();
    private readonly TestLogSource _log = new();
    private readonly string _dir;
    private readonly object _owner = new();
    private readonly List<(ModSettingsView View, IUiPanel Panel)> _built = new();

    public MenuKitSettingsTests()
    {
        UiLayoutBatch.ResetForTests();
        UiApi.ResetForTests();
        UiApi.SetRuntimeForTests(_runtime);
        Plugin.Logger = _log;
        WorldEpoch.ResetForTests();
        SettingsCatalog.ResetForTests();
        SettingsCatalog.UseSyncedProbe(_ => false);
        ModSettingsPages.ResetForTests();
        ConfigSync.ResetForTests();
        SettingsArtLadder.PrepareHook = null;
        SettingsArtLadder.Reset();
        _dir = Path.Combine(Path.GetTempPath(), "progenitor-menukit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        // A test that fails part-way still releases what it built, so later classes start clean.
        foreach (var (view, panel) in _built)
        {
            try { view.Release(); } catch { }
            try { panel.Dispose(); } catch { }
        }

        UiLayoutBatch.ResetForTests();
        ModSettingsPages.ResetForTests();
        WorldEpoch.ResetForTests();
        SettingsCatalog.ResetForTests();
        ConfigSync.ResetForTests();
        SettingsArtLadder.PrepareHook = null;
        SettingsArtLadder.Reset();
        Plugin.Logger = null;
        UiApi.ResetForTests();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private ConfigFile File(string name) => new(Path.Combine(_dir, name + ".cfg"), saveOnInit: true);

    private ModSettingsView Build(string id, out IUiPanel panel)
    {
        Assert.True(ModSettingsPages.TryGet(id, out var registration));
        var view = new ModSettingsView(id, ModSettingsLayout.Build(registration!.Sections(), null, null), "", () => ServerPartState.Running);
        panel = UiApi.Panel("MenuKit", _owner, "example.ui.test");
        view.Build(panel, (_, _, _) => { });
        _built.Add((view, panel));
        return view;
    }

    // ---- M1 values ----------------------------------------------------------------------------------

    /// <summary>
    /// The M1 values convert to what the reference capture measured: a 58-unit row is 70 px, the name's
    /// resting gray is the measured 161 and its lit gray 231. What this would catch: a scale or tint typo
    /// that makes the page drift from the game's Options page.
    /// </summary>
    [Fact]
    public void The_M1_values_match_the_reference_capture()
    {
        Assert.Equal(70f, GameSettingsLook.Kit(GameSettingsLook.RowHeight));
        Assert.Equal(70f, ModSettingsRowGrid.RowPitch);
        Assert.InRange(GameSettingsLook.NameGrey(lit: false) * 255f, 160f, 163f);
        Assert.InRange(GameSettingsLook.NameGrey(lit: true) * 255f, 230f, 232f);
        Assert.Equal(0f, GameSettingsLook.Kit(float.NaN));
        // The band's composition as the game does it: image alpha times the lit tint alpha, in the tint's hue.
        var lit = GameSettingsLook.RowBandLit();
        Assert.Equal(0.753f * 0.102f, lit.A, 4);
        Assert.Equal(GameSettingsLook.RowBandHue, (lit.R, lit.G, lit.B));
        // No loud green or gold anywhere in the borrowed tones: every channel of the band hue is a blue.
        Assert.True(lit.B > lit.G && lit.G > lit.R);
    }

    // ---- the settings art ladder ------------------------------------------------------------------

    /// <summary>
    /// Every part is one M1 sprite name, no two parts share one, and the name lookup is exact. What this
    /// would catch: a part that could never be found (a typo in its name) or two parts drawing one sprite.
    /// </summary>
    [Fact]
    public void Every_settings_part_is_a_distinct_M1_sprite()
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (SettingsArtPart part in Enum.GetValues(typeof(SettingsArtPart)))
        {
            string name = SettingsArtLadder.SpriteName(part);
            Assert.False(string.IsNullOrEmpty(name), part.ToString());
            Assert.True(names.Add(name), "shared sprite " + name);
            Assert.Equal(part, SettingsArtLadder.PartOf(name));
        }

        Assert.Equal(SettingsArtLadder.PartCount, names.Count);
        Assert.Equal(SettingsArtLadder.PartCount, Enum.GetValues(typeof(SettingsArtPart)).Length);
        Assert.Null(SettingsArtLadder.PartOf("checkbox_normal"));
        Assert.Null(SettingsArtLadder.PartOf(null));
        Assert.Null(SettingsArtLadder.PartOf("progenitor-ui-settings-art-Checkbox_Normal"));

        // Sliced exactly where the game slices (M1 borders), simple elsewhere.
        Assert.True(SettingsArtLadder.Sliced(SettingsArtPart.DropdownBar));
        Assert.True(SettingsArtLadder.Sliced(SettingsArtPart.ValueBox));
        Assert.True(SettingsArtLadder.Sliced(SettingsArtPart.SliderTrack));
        Assert.False(SettingsArtLadder.Sliced(SettingsArtPart.CheckboxFrame));
        Assert.False(SettingsArtLadder.Sliced(SettingsArtPart.SliderHandle));
        Assert.False(SettingsArtLadder.Sliced(SettingsArtPart.ResetIcon));
        Assert.Equal(3f, SettingsArtLadder.PpuMultiplier(SettingsArtPart.ScrollbarHandle));
        Assert.Equal(1f, SettingsArtLadder.PpuMultiplier(SettingsArtPart.CheckboxMark));
        Assert.Equal("checkboxFrame", SettingsArtLadder.PartName(SettingsArtPart.CheckboxFrame));
    }

    /// <summary>
    /// Game-free there is no donor: every part is the kit's, logged once, and the readback lists every
    /// part. A throwing hook falls back to the kit. What this would catch: a page that cannot draw
    /// without the game, or a log line per build.
    /// </summary>
    [Fact]
    public void Without_a_donor_every_part_is_the_kit_and_the_readback_lists_them()
    {
        Assert.Same(SettingsArt.Unresolved, SettingsArtLadder.Current);
        SettingsArtLadder.Prepare();
        Assert.Same(SettingsArt.AllKit, SettingsArtLadder.Current);
        var read = SettingsArtLadder.Describe();
        var parts = (Dictionary<string, object?>)read["parts"]!;
        Assert.Equal(SettingsArtLadder.PartCount, parts.Count);
        Assert.Equal("kit", ((Dictionary<string, object?>)parts["checkboxFrame"]!)["source"]);
        Assert.Equal(0, read["borrowed"]);
        Assert.Single(_log.Infos, l => l.StartsWith(SettingsArtLadder.LogPrefix, StringComparison.Ordinal));
        SettingsArtLadder.Prepare();
        Assert.Single(_log.Infos, l => l.StartsWith(SettingsArtLadder.LogPrefix, StringComparison.Ordinal));

        SettingsArtLadder.PrepareHook = () => throw new InvalidOperationException("boom");
        SettingsArtLadder.Prepare();
        Assert.Same(SettingsArt.AllKit, SettingsArtLadder.Current);
        Assert.Contains(_log.Warnings, l => l.Contains("settings art donors threw: boom", StringComparison.Ordinal));

        // A mixed result: the summary counts rungs and names the kit parts.
        var mixed = new GameArtPart[SettingsArtLadder.PartCount];
        for (int i = 0; i < mixed.Length; i++)
            mixed[i] = new GameArtPart(GameArtSource.OwnedCopy, SettingsArtLadder.DonorOptionsPanel, SettingsArtLadder.SpriteName((SettingsArtPart)i), null, "owned");
        mixed[(int)SettingsArtPart.ScrollbarTrack] = GameArtPart.Kit;
        var art = new SettingsArt(mixed, new[] { "Checkbox_Normal" });
        Assert.Equal("kit=1 owned-copy=24 kit:scrollbarTrack", art.Summary());
        Assert.Equal(24, art.BorrowedCount);
        Assert.Equal("Settings kit: art kit=1 owned-copy=24 kit:scrollbarTrack", SettingsArtLadder.Use(art));
        Assert.Null(SettingsArtLadder.Use(art));
        Assert.Equal(new List<string> { "Checkbox_Normal" }, SettingsArtLadder.Describe()["donorSprites"]);
    }

    /// <summary>
    /// The Unity side keeps the shared ladder's rules: its own holder and asset tag, owned copies through
    /// the shared store, non-generic scans, and a hook installed and removed with the keybind host.
    /// </summary>
    [Fact]
    public void The_settings_donors_use_the_shared_store_and_install_with_the_host()
    {
        string donors = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "GameArt", "SettingsArtDonors.cs");
        SourcePin.AssertUncommented(donors, "internal const string HolderName = UiOwnership.Prefix + \"core-settings-art\"");
        SourcePin.AssertUncommented(donors, "private static readonly OwnedSprites Art = new(HolderName, AssetTag, SettingsArtLadder.LogPrefix, PartNames)");
        string prepare = SourcePin.MethodBody(donors, "private static void PrepareCore(bool copyNow)");
        SourcePin.AssertUncommented(prepare, "GameArtLadder.Next(liveFound, owned, cached, kept, residentHit != null)");
        SourcePin.AssertUncommented(prepare, "SettingsArtLadder.ShouldScanLive(allHeld, _liveScanEpoch, epoch, _liveScanFrame, frame)");
        SourcePin.AssertUncommented(SourcePin.MethodBody(donors, "internal static void Release()"), "Art.Release()");
        SourcePin.AssertUncommented(SourcePin.MethodBody(donors, "private static void ReadLoadedSprites("), "Resources.FindObjectsOfTypeAll(type)");
        Assert.DoesNotContain("FindObjectsOfType<", donors, StringComparison.Ordinal);
        Assert.True(UiOwnership.IsOwned(UiOwnership.Prefix + "core-settings-art"));

        string host = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "UiKeybindHost.cs");
        SourcePin.AssertUncommented(host, "GameArt.SettingsArtLadder.PrepareHook = GameArt.SettingsArtDonors.Prepare");
        SourcePin.AssertUncommented(host, "GameArt.SettingsArtLadder.PrepareHook = null");
        SourcePin.AssertUncommented(host, "GameArt.SettingsArtDonors.Release()");
    }

    // ---- the one-line row grid ---------------------------------------------------------------------

    /// <summary>
    /// At every pane width the columns keep their order and never overlap: name, control column (bars to
    /// the bar edge, compact controls to the compact edge), then the reset arrow, then the scrollbar band.
    /// What this would catch: a slider track under its value box, a checkbox over the reset arrow, or a
    /// column that goes negative on a narrow pane.
    /// </summary>
    [Theory]
    [InlineData(400f)]
    [InlineData(700f)]
    [InlineData(926f)]
    [InlineData(1288f)]
    [InlineData(2400f)]
    public void The_row_grid_keeps_its_columns_apart_at_every_width(float rowWidth)
    {
        var g = ModSettingsRowGrid.For(rowWidth);
        Assert.True(g.NameWidth > 0f);
        Assert.True(ModSettingsRowGrid.NameInset + g.NameWidth <= g.ControlLeft);
        Assert.True(g.ControlLeft < g.CompactRight && g.CompactRight < g.BarRight);
        float resetLeft = g.ResetCenterX - ModSettingsRowGrid.ResetSize * 0.5f;
        Assert.True(g.BarRight <= resetLeft, "the reset arrow is clear of every control");
        Assert.True(g.ResetCenterX + ModSettingsRowGrid.ResetSize * 0.5f <= g.Right);
        Assert.True(g.Right <= rowWidth);

        var slider = g.ControlSlot(SettingsControlShape.SliderWithValue);
        var box = g.SliderValueBoxSlot;
        Assert.Equal(g.CompactRight, box.Left + box.Width);
        Assert.Equal(g.CompactRight, g.ValueBoxSlot.Left + g.ValueBoxSlot.Width);
        if (g.SliderHasValueBox)
        {
            Assert.True(slider.Left + slider.Width <= box.Left, "the track ends before its value box");
            Assert.True(slider.Width >= ModSettingsRowGrid.MinSliderTrack);
        }
        else
        {
            Assert.Equal(g.CompactRight, slider.Left + slider.Width);
        }

        if (rowWidth >= 900f) Assert.True(g.SliderHasValueBox, "a normal pane shows the value box");
        var check = g.ControlSlot(SettingsControlShape.Checkbox);
        Assert.Equal(g.CompactRight, check.Left + check.Width);
        Assert.Equal(ModSettingsRowGrid.CheckboxSize, check.Width);
        var bar = g.ControlSlot(SettingsControlShape.Bar);
        Assert.Equal((g.ControlLeft, g.BarRight - g.ControlLeft), (bar.Left, bar.Width));
        var button = g.ControlSlot(SettingsControlShape.Button);
        Assert.Equal(g.BarRight, button.Left + button.Width);
        Assert.True(button.Left >= g.ControlLeft);

        if (rowWidth >= 1000f)
        {
            // Wide: the game's proportions — the control column starts at 45% of the usable row.
            Assert.Equal(g.Right * GameSettingsLook.ControlStartShare, g.ControlLeft, 1);
            Assert.True(g.ControlWidth >= ModSettingsRowGrid.MinControlWidth);
        }
    }

    /// <summary>
    /// Every row is one pitch high whatever its help says; only an options list or a text area is taller.
    /// A bool is the checkbox, a finite choice the bar, a bounded number the slider with its value box.
    /// What this would catch: help text making a row taller again (the wall of text), or the switch coming back.
    /// </summary>
    [Fact]
    public void Rows_are_one_pitch_and_each_control_takes_the_games_shape()
    {
        foreach (SettingsControlShape shape in Enum.GetValues(typeof(SettingsControlShape)))
            Assert.Equal(ModSettingsRowGrid.RowPitch, ModSettingsRowGrid.RowHeight(shape));
        Assert.Equal(ModSettingsRowGrid.RowPitch, ModSettingsRowGrid.RowHeight(SettingsControlShape.Tall, 20f));
        Assert.Equal(200f + ModSettingsRowGrid.RowPitch - ModSettingsRowGrid.BarHeight, ModSettingsRowGrid.RowHeight(SettingsControlShape.Tall, 200f));
        Assert.Equal(PanelLayout.MaxRowHeight, ModSettingsRowGrid.RowHeight(SettingsControlShape.Tall, 5000f));

        Assert.Equal(SettingsControlShape.Checkbox, ModSettingsRowGrid.ShapeOf(SettingControl.Toggle));
        Assert.Equal(SettingsControlShape.Bar, ModSettingsRowGrid.ShapeOf(SettingControl.Dropdown));
        Assert.Equal(SettingsControlShape.Bar, ModSettingsRowGrid.ShapeOf(SettingControl.Segmented));
        Assert.Equal(SettingsControlShape.Bar, ModSettingsRowGrid.ShapeOf(SettingControl.Text));
        Assert.Equal(SettingsControlShape.KeyCap, ModSettingsRowGrid.ShapeOf(SettingControl.Keybind));
        Assert.Equal(SettingsControlShape.Bar, ModSettingsRowGrid.ShapeOf(SettingControl.ReadOnly));
        Assert.Equal(SettingsControlShape.SliderWithValue, ModSettingsRowGrid.ShapeOf(SettingControl.Slider));
        Assert.Equal(SettingsControlShape.ValueBox, ModSettingsRowGrid.ShapeOf(SettingControl.Number));
        Assert.Equal(SettingsControlShape.ValueBox, ModSettingsRowGrid.ShapeOf(SettingControl.Color));
        Assert.Equal(SettingsControlShape.Button, ModSettingsRowGrid.ShapeOf(SettingControl.Action));
        Assert.Equal(SettingsControlShape.Tall, ModSettingsRowGrid.ShapeOf(SettingControl.Radio));

        // The game's control sizes, in our units. Round C (live): the checkbox and the text at the game's
        // on-screen sizes (checkbox 24 with its frame about 18-20 px, names about 20 px), bars scaled.
        Assert.Equal(24f, ModSettingsRowGrid.CheckboxSize);
        Assert.Equal(20f, ModSettingsRowGrid.CheckmarkSize);
        Assert.Equal(51f, ModSettingsRowGrid.BarHeight);
        Assert.Equal((135f, 39f), (ModSettingsRowGrid.ValueBoxWidth, ModSettingsRowGrid.ValueBoxHeight));
        Assert.Equal(18f, ModSettingsRowGrid.NameFontSize);

        // The section header: about 30 px on screen, the rule over the left half of the row.
        var g = ModSettingsRowGrid.For(1288f);
        Assert.Equal(28f, ModSettingsRowGrid.HeaderFontSize);
        Assert.Equal(48f, ModSettingsRowGrid.HeaderHeight);
        Assert.Equal(g.Right * 0.5f, g.HeaderRuleWidth);
        var spec = ModSettingsView.GameSectionRow(2, "Display", openKeybinds: false);
        Assert.Equal(ModSettingsView.SectionKeyPrefix + "2", spec.Key);
        Assert.Equal(ModSettingsRowGrid.HeaderHeight, spec.Height);
        var title = spec.Cells.Single();
        Assert.Equal(CellKind.Text, title.Kind);
        Assert.Equal(ModSettingsRowGrid.HeaderFontSize, title.FontSize);
        Assert.NotEqual(CellTone.Muted, title.Tone);
    }

    // ---- marks and the description column ----------------------------------------------------------

    /// <summary>
    /// A row's state shows without words: the reset arrow is dim at the default and bright when changed
    /// (none on a row that cannot be reset), a pending restart gets its chip, a grayed row its alpha.
    /// What this would catch: the reset arrow hidden at the default (the game always draws it) or shown on
    /// a server-owned row.
    /// </summary>
    [Fact]
    public void Row_marks_show_the_state_without_text()
    {
        var file = File("marks");
        const string id = "example.menukit.marks";
        var clock = file.Bind("M", "ShowClock", true, "Shows the clock.");
        var restart = file.Bind("M", "Experimental", false, "");
        ModSettingsPages.Register(_owner, id, page =>
        {
            page.Toggle(clock);
            page.Toggle(restart, needsRestart: true);
        });
        var view = Build(id, out var panel);

        var marks = view.MarksOf(id + "/M/ShowClock")!.Value;
        Assert.Equal(SettingsResetState.Default, marks.Reset);
        Assert.Equal(GameSettingsLook.ResetDisabled, marks.ResetColor(hovered: true));
        Assert.Equal(SettingsNameTone.Rest, marks.NameTone);
        Assert.Equal(1f, marks.RowAlpha);

        Assert.True(_runtime.FireToggle("Show clock", false));
        view.Hover(id + "/M/ShowClock");
        marks = view.MarksOf(id + "/M/ShowClock")!.Value;
        Assert.Equal(SettingsResetState.Changed, marks.Reset);
        Assert.Equal(GameSettingsLook.ResetRest, marks.ResetColor(hovered: false));
        Assert.Equal(GameSettingsLook.ResetHover, marks.ResetColor(hovered: true));
        Assert.Equal(SettingsNameTone.Lit, marks.NameTone);
        Assert.Equal("changed", marks.Readback()["reset"]);

        Assert.True(_runtime.FireToggle("Experimental", true));
        Assert.True(view.MarksOf(id + "/M/Experimental")!.Value.PendingRestart);
        Assert.Null(view.MarksOf("nope"));

        Assert.Equal(ModSettingsRowMarks.GreyedAlpha, new ModSettingsRowMarks(SettingsResetState.None, false, true, false, false).RowAlpha);
        Assert.Equal(SettingsNameTone.Warn, new ModSettingsRowMarks(SettingsResetState.None, false, false, true, true).NameTone);
        view.Release();
        panel.Dispose();
    }

    /// <summary>
    /// The column describes the hovered row, else the selected one, else the first; it carries the same
    /// sentences as the row's help line, split into the help and its notes, and follows a commit. What
    /// this would catch: a column stuck on a row the pointer left, a note lost in the move out of the
    /// row, or help that says something different from the readback's <c>helpLine</c>.
    /// </summary>
    [Fact]
    public void The_description_follows_hover_then_selection_then_the_first_row()
    {
        var file = File("describe");
        const string id = "example.menukit.describe";
        var clock = file.Bind("D", "ShowClock", true, "");
        var seconds = file.Bind("D", "ShowSeconds", false, "");
        ModSettingsPages.Register(_owner, id, page =>
        {
            page.Toggle(clock, help: "Shows the clock.");
            page.Toggle(seconds, help: "Adds seconds", needsRestart: true);
        });
        var view = Build(id, out var panel);
        var seen = new List<ModSettingsDescription>();
        view.DescriptionChanged = d => seen.Add(d);

        var first = view.Description();
        Assert.Equal((id + "/D/ShowClock", ModSettingsDescription.SourceFirst), (first.RowId, first.Source));
        Assert.Equal("Show clock", first.Heading);
        Assert.Equal("Shows the clock.", first.Body);
        Assert.Empty(first.Notes);
        Assert.Equal("", first.Tone);

        view.Select(id + "/D/ShowSeconds");
        Assert.Equal(ModSettingsDescription.SourceSelected, view.Description().Source);
        view.Hover(id + "/D/ShowClock");
        Assert.Equal((id + "/D/ShowClock", ModSettingsDescription.SourceHover), (view.Description().RowId, view.Description().Source));
        view.Hover(null);
        var selected = view.Description();
        Assert.Equal((id + "/D/ShowSeconds", ModSettingsDescription.SourceSelected), (selected.RowId, selected.Source));
        Assert.Equal("Adds seconds.", selected.Body);
        Assert.Equal(3, seen.Count);

        // A commit on the described row: its notes follow (Changed from, Pending restart, Saved), in the
        // help line's order, and the column is told.
        Assert.True(_runtime.FireToggle("Show seconds", true));
        var after = view.Description();
        Assert.Equal(new[] { ModSettingsLayout.ChangedFromPrefix + "false.", ModSettingsLayout.PendingRestartMarker + ".", ModSettingsView.SavedNote }, after.Notes.ToArray());
        Assert.Equal(ModSettingsDescription.ToneMuted, after.Tone);
        Assert.Equal(_runtime.HelpOf("toggle", "Show seconds"), after.Body + " " + after.NotesText);
        Assert.True(seen.Count > 3);
        Assert.Equal(id + "/D/ShowSeconds", seen[seen.Count - 1].RowId);

        // An unknown id is ignored; releasing the page forgets the focus and the callback.
        view.Hover("not-a-row");
        Assert.Null(view.HoveredId);
        view.Release();
        Assert.Null(view.SelectedId);
        Assert.Null(view.DescriptionChanged);
        panel.Dispose();
    }

    /// <summary>
    /// The split of the old help line is lossless: help plus notes joined is exactly the help line. And the
    /// column decision: none on a narrow content area, the game's width at most on a wide one.
    /// </summary>
    [Fact]
    public void Help_parts_rejoin_to_the_help_line_and_the_column_fits_the_pane()
    {
        var file = File("parts");
        var entry = file.Bind("P", "Greeting", "hi", "Says hello");
        var row = ModSettingsLayout.RowOf("example.menukit.parts", entry, null);
        foreach (var (why, status) in new[] { ("", ""), ("Not available right now.", ""), ("", "Saved."), ("Needs the server part", "bad value") })
        {
            var (help, notes) = ModSettingsLayout.HelpParts(row, null, SettingControl.Text, why, status);
            string joined = string.Join(" ", new[] { help }.Where(h => h.Length > 0).Concat(notes));
            Assert.Equal(ModSettingsLayout.HelpText(row, null, SettingControl.Text, why, status), joined);
        }

        var refused = ModSettingsDescription.For(row, null, SettingControl.Text, "", "bad value", ModSettingsDescription.SourceHover);
        Assert.Equal(ModSettingsDescription.ToneWarn, refused.Tone);
        var greyed = ModSettingsDescription.For(row, null, SettingControl.Text, "Not available right now.", "", ModSettingsDescription.SourceHover);
        Assert.Equal(ModSettingsDescription.ToneWarn, greyed.Tone);
        var extra = ModSettingsDescription.ForExtra("x/button/Run", "Run", "Runs it", "", "Done.", ModSettingsDescription.SourceHover);
        Assert.Equal(("Run", "Runs it.", ModSettingsDescription.ToneMuted), (extra.Heading, extra.Body, extra.Tone));
        Assert.Equal(("", ModSettingsDescription.SourceNone), ModSettingsDescription.Pick(null, "", null));
        Assert.Equal("page-settings-description", ModSettingsDescription.ReadbackKey);

        Assert.False(ModSettingsDescriptionLayout.HasColumn(900f));
        Assert.Equal(0f, ModSettingsDescriptionLayout.ColumnWidth(900f));
        Assert.Equal(900f, ModSettingsDescriptionLayout.RowsWidth(900f));
        Assert.Equal(308f, ModSettingsDescriptionLayout.ColumnWidth(1100f));
        Assert.True(ModSettingsDescriptionLayout.ColumnWidth(1100f) >= ModSettingsDescriptionLayout.MinColumnWidth);
        Assert.Equal(361f, ModSettingsDescriptionLayout.ColumnWidth(1288f));
        Assert.Equal(1288f - 361f - ModSettingsDescriptionLayout.ColumnGap, ModSettingsDescriptionLayout.RowsWidth(1288f));
        Assert.Equal(ModSettingsDescriptionLayout.MaxColumnWidth, ModSettingsDescriptionLayout.ColumnWidth(4000f));
        Assert.Equal(GameSettingsLook.Kit(440f), ModSettingsDescriptionLayout.MaxColumnWidth);
    }

    // ---- phase B: what the runtime is told to draw ------------------------------------------------

    /// <summary>
    /// The runtime's columns for a grid row are the game row's: the name from its inset to the control
    /// column, the control column from 45% to the bar edge; a row without a control gives the name the
    /// row up to the bar edge. Row tops land exactly one pitch apart (drawn height plus the layout gap).
    /// What this would catch: the runtime drawing a control off the grid the unit tests pin.
    /// </summary>
    [Theory]
    [InlineData(926f)]
    [InlineData(1288f)]
    public void The_runtime_columns_are_the_game_rows(float rowWidth)
    {
        var g = ModSettingsRowGrid.For(rowWidth);
        var c = g.Columns(hasControl: true);
        Assert.Equal(g.FromCenter(g.ControlLeft), c.ControlLeft, 2);
        Assert.Equal(g.ControlWidth, c.ControlWidth, 2);
        Assert.Equal(g.FromCenter(ModSettingsRowGrid.NameInset), c.LabelX - c.LabelWidth * 0.5f, 2);
        Assert.True(c.LabelRight <= c.ControlLeft);
        var nameOnly = g.Columns(hasControl: false);
        Assert.Equal(g.FromCenter(g.BarRight), nameOnly.LabelX + nameOnly.LabelWidth * 0.5f, 2);
        Assert.Equal(0f, nameOnly.ControlWidth);
        Assert.Equal(RowColumnRules.For("toggle", true, true, false, true, rowWidth).ControlX, c.ControlX, 2);

        Assert.Equal(ModSettingsRowGrid.RowPitch, ModSettingsRowGrid.DrawnRowHeight(SettingsControlShape.Checkbox) + PanelLayout.RowGap);
        Assert.Equal(PanelLayout.RowGap, ModSettingsRowGrid.BandOverhang);
        Assert.Equal(SettingsControlShape.Checkbox, ModSettingsRowGrid.ShapeOfKind("toggle"));
        Assert.Equal(SettingsControlShape.SliderWithValue, ModSettingsRowGrid.ShapeOfKind("slider-entry"));
        Assert.Equal(SettingsControlShape.Bar, ModSettingsRowGrid.ShapeOfKind("choice"));
        Assert.Equal(SettingsControlShape.ValueBox, ModSettingsRowGrid.ShapeOfKind("numeric"));
        Assert.Equal(SettingsControlShape.Button, ModSettingsRowGrid.ShapeOfKind("button"));
        Assert.Equal(SettingsControlShape.Tall, ModSettingsRowGrid.ShapeOfKind("multiselect"));
        Assert.Equal(SettingsControlShape.Bar, ModSettingsRowGrid.ShapeOfKind("info"));
    }

    /// <summary>
    /// Built on a panel, the page turns the description column on, draws the red rule under each section
    /// heading, writes the first row's description, lights the hovered and the selected row, marks every
    /// reset arrow (dim at the default, bright when changed, never hidden) and the restart chip. What this
    /// would catch: a page that draws the old inline help, a reset arrow that vanishes at the default, or a
    /// column that never follows the pointer.
    /// </summary>
    [Fact]
    public void The_page_drives_the_column_lit_rows_reset_arrows_and_chips()
    {
        var file = File("drive");
        const string id = "example.menukit.drive";
        var clock = file.Bind("V", "ShowClock", true, "");
        var restart = file.Bind("V", "Experimental", false, "");
        ModSettingsPages.Register(_owner, id, page => page
            .Section("Display")
                .Toggle(clock, help: "Shows the clock.")
                .Toggle(restart, help: "Try the new code.", needsRestart: true));
        var view = Build(id, out var panel);

        Assert.True(_runtime.DescriptionMode);
        Assert.Equal(1, _runtime.SectionRules);
        Assert.Equal(("Show clock", "Shows the clock.", "", ""), _runtime.Descriptions.Last());
        Assert.Equal(SettingsResetState.Default, _runtime.ResetMarks["Show clock"]);
        Assert.Equal(true, _runtime.VisibleOf("button", ModSettingsView.ResetGlyph));

        view.Hover(id + "/V/Experimental");
        Assert.True(_runtime.LitRows["Experimental"]);
        Assert.Equal("Experimental", _runtime.Descriptions.Last().Heading);
        view.Hover(null);
        Assert.False(_runtime.LitRows["Experimental"]);
        Assert.Equal("Show clock", _runtime.Descriptions.Last().Heading);

        // A commit selects its row (lit, and the column's subject once the pointer leaves), brightens its
        // reset arrow and shows the restart chip.
        Assert.True(_runtime.FireToggle("Experimental", true));
        Assert.True(_runtime.LitRows["Experimental"]);
        Assert.Equal(SettingsResetState.Changed, _runtime.ResetMarks["Experimental"]);
        Assert.Equal(ModSettingsRowMarks.RestartChip, _runtime.Chips["Experimental"]);
        var last = _runtime.Descriptions.Last();
        Assert.Equal("Experimental", last.Heading);
        Assert.Contains(ModSettingsLayout.PendingRestartMarker, last.Notes, StringComparison.Ordinal);

        // Back to the default: the arrow dims again but stays.
        Assert.True(_runtime.FireToggle("Experimental", false));
        Assert.Equal(SettingsResetState.Default, _runtime.ResetMarks["Experimental"]);
        Assert.Equal("", _runtime.Chips["Experimental"]);
        view.Release();
        panel.Dispose();
    }

    /// <summary>
    /// The runtime draws what the plan says: a grid bool is the game's checkbox (not the switch, which
    /// only the mod's own on/off keeps), every grid row gets the game's chrome and skin, each control uses
    /// its M1 sprite part with a kit fallback, and the art is taken at the first workspace open.
    /// </summary>
    [Fact]
    public void The_runtime_draws_the_games_controls_on_the_grid()
    {
        string runtime = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "UnityUiRuntime.cs");
        SourcePin.AssertUncommented(SourcePin.MethodBody(runtime, "public IRuntimeWidget? AddToggle("),
            "if (host.Grid && !host.Inline) return AddGridCheckbox(host, text, initial);");
        Assert.DoesNotContain("AddGridSwitch", runtime, StringComparison.Ordinal);
        SourcePin.AssertUncommented(SourcePin.MethodBody(runtime, "public void AddRow("), "if (_grid) PrepareGridRow(go, label, kind, control);");
        SourcePin.AssertUncommented(runtime, "BeginGridPass();");
        SourcePin.AssertUncommented(runtime, "PlaceSettingsColumn();");

        string look = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "UnityUiRuntime.SettingsLook.cs");
        string checkbox = SourcePin.MethodBody(look, "private IRuntimeWidget? AddGridCheckbox(");
        SourcePin.AssertUncommented(checkbox, "SettingsArtPart.CheckboxFrame");
        SourcePin.AssertUncommented(checkbox, "SettingsArtPart.CheckboxMark");
        SourcePin.AssertUncommented(checkbox, "ApplySpriteSwap(toggle, SettingsArtPart.CheckboxFrameHover, SettingsArtPart.CheckboxFrameDisabled)");
        string slider = SourcePin.MethodBody(look, "private static bool SkinSlider(");
        SourcePin.AssertUncommented(slider, "SettingsArtPart.SliderTrack");
        SourcePin.AssertUncommented(slider, "SettingsArtPart.SliderHandle");
        SourcePin.AssertUncommented(SourcePin.MethodBody(look, "private static bool SkinField("), "SettingsArtPart.ValueBox");
        string choice = SourcePin.MethodBody(look, "private static bool SkinChoice(");
        SourcePin.AssertUncommented(choice, "SettingsArtPart.DropdownBar");
        SourcePin.AssertUncommented(choice, "PartSprite(SettingsArtPart.DropdownArrow)");
        SourcePin.AssertUncommented(SourcePin.MethodBody(look, "private static void SkinResetArrow("), "PartSprite(SettingsArtPart.ResetIcon)");
        SourcePin.AssertUncommented(SourcePin.MethodBody(look, "public void AddSectionRule("), "SettingsArtPart.SectionRule");
        SourcePin.AssertUncommented(SourcePin.MethodBody(look, "private void EnsureColumn("), "SettingsArtPart.DetailsOrnament");
        // A part the donors did not give draws the kit's flat look: the page always draws.
        string paint = SourcePin.MethodBody(look, "private static bool PaintPart(");
        SourcePin.AssertUncommented(paint, "image.sprite = null;");
        SourcePin.AssertUncommented(paint, "image.color = kit;");
        // Names and the narrow help line are one line cut with an ellipsis (the whole help is the readback's helpLine).
        Assert.DoesNotContain("TextOverflowModes.Overflow;", SourcePin.MethodBody(look, "private void PlaceGridRowParts("), StringComparison.Ordinal);

        // Round C: nothing scans or copies on the open or page-build path; the idle tick prepares the art.
        string menu = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "ModsMenu.cs");
        Assert.DoesNotContain("SettingsArtLadder.Prepare", SourcePin.MethodBody(menu, "public static IUiPanel Open("), StringComparison.Ordinal);
        string view = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "ModSettingsView.cs");
        Assert.DoesNotContain("SettingsArtLadder.Prepare", SourcePin.MethodBody(view, "internal void Build("), StringComparison.Ordinal);
        string donors = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "GameArt", "SettingsArtDonors.cs");
        string idle = SourcePin.MethodBody(donors, "private static void IdleTick()");
        SourcePin.AssertUncommented(idle, "if (++_idleFrames < SettleFrames) return;");
        SourcePin.AssertUncommented(idle, "if (Art.CopyPending(i)) copies++;");
        SourcePin.AssertUncommented(idle, "PrepareCore(copyNow: false);");
        SourcePin.AssertUncommented(donors, "internal static void Prepare() => PrepareCore(copyNow: false);");
        string host = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "UiKeybindHost.cs");
        SourcePin.AssertUncommented(host, "GameArt.SettingsArtDonors.StartIdle();");
        SourcePin.AssertUncommented(host, "GameArt.SettingsArtDonors.StopIdle();");
        Assert.Equal("Settings kit: prepared in 12 ms (owned-copy=20) over 340 ms of idle frames", SettingsArtLadder.PreparedLine(12.4, 339.6, 20));
    }

    // ---- round C (live review 2026-09-30) ---------------------------------------------------------------

    /// <summary>
    /// The live review's sizes: names about 20 px on screen (18 units), section titles and the description
    /// title about 30 px (28), the description body about 20 px (18); the page's settings header is a small
    /// action row inside the grid (so Reset all sits over the rows, not the column) and section titles are
    /// middle-aligned (a bottom-aligned cell cut a big title away). What this would catch: the text scaled
    /// with the geometry again, or the missing section title coming back.
    /// </summary>
    [Fact]
    public void Round_C_text_sizes_and_the_action_row()
    {
        Assert.Equal(18f, GameSettingsLook.NameText);
        Assert.Equal(28f, ModSettingsDescriptionLayout.HeadingFontSize);
        Assert.Equal(18f, ModSettingsDescriptionLayout.BodyFontSize);
        Assert.Equal(28f, ModSettingsRowGrid.HeaderFontSize);
        Assert.Equal(CellVAlign.Middle, ModSettingsView.GameSectionRow(0, "Clock", openKeybinds: true).Cells[0].VAlign);
        var keys = ModSettingsView.GameSectionRow(0, "Keys", openKeybinds: true);
        Assert.Equal(new[] { ModSettingsView.CellTitle, ModSettingsView.CellOpenKeybinds }, keys.Cells.Select(c => c.Key).ToArray());

        var file = File("action");
        const string id = "example.menukit.action";
        var clock = file.Bind("A", "ShowClock", true, "");
        ModSettingsPages.Register(_owner, id, page => page.Section("Clock").Toggle(clock));
        var view = Build(id, out var panel);
        var head = _runtime.CellRow(ModSettingsView.HeaderKey)!;
        var title = head.Cells().Single(c => c.Key == ModSettingsView.CellTitle);
        Assert.Equal(GameSettingsLook.ActionRowText, title.FontSize);
        Assert.Equal(CellKind.Muted, title.Kind);
        var rect = head.RowRect();
        Assert.Equal(ModSettingsRowGrid.ActionRowHeight, rect.MaxY - rect.MinY, 2);
        string source = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "ModSettingsView.cs");
        string rows = SourcePin.MethodBody(source, "private void BuildRows(");
        Assert.True(rows.IndexOf("panel.SettingsGrid(true);", StringComparison.Ordinal) < rows.IndexOf("BuildHeader(panel, add);", StringComparison.Ordinal),
            "the action row is built in the grid, at the rows' width");
        view.Release();
        panel.Dispose();
    }

    /// <summary>
    /// The raw "Settings and keybinds" page in the game look: one section per config section in entry
    /// order, names humanized from the key, the description plus what the entry accepts (no more
    /// "[Clock] Format [one of: …]" captions), and the automatic control. Built in the workspace it is the
    /// same view as the mod page. What this would catch: the centered raw list coming back.
    /// </summary>
    [Fact]
    public void The_raw_settings_page_maps_to_the_game_rows()
    {
        var file = File("raw");
        const string id = "example.menukit.raw";
        var format = file.Bind("Clock", "Format", RawFormat.TwentyFour, "Your own choice overrides the server default");
        var seconds = file.Bind("Clock", "ShowSeconds", false, "Show seconds in your local time.");
        var limit = file.Bind("Server", "Limit", 5, "");
        var rows = new List<SettingsRow>
        {
            SettingsRow.ForEntry(id, format, false),
            SettingsRow.ForEntry(id, seconds, false),
            SettingsRow.ForEntry(id, limit, false),
        };

        var sections = ModSettingsLayout.RawSections(rows);
        Assert.Equal(new[] { "Clock", "Server" }, sections.Select(s => s.Title).ToArray());
        Assert.Equal(2, sections[0].Items.Count);
        Assert.Equal("Show seconds", ModSettingsLayout.DisplayNameOf(sections[0].Items[1].Row!, sections[0].Items[1].Options));
        // The test ConfigFile shim drops descriptions and bounds, so the help is empty here; RawHelp and
        // AcceptsSentence are pinned below.
        string formatHelp = sections[0].Items[0].Options.Help!;
        Assert.Equal(ModSettingsLayout.AcceptsSentence(rows[0].Bounds), formatHelp);
        Assert.Equal(SettingControl.Dropdown, ModSettingsLayout.ControlFor(rows[0]));
        Assert.Equal(SettingControl.Toggle, ModSettingsLayout.ControlFor(rows[1]));
        Assert.Equal("", ModSettingsLayout.AcceptsSentence(""));
        Assert.Equal("One of: a, b.", ModSettingsLayout.AcceptsSentence("one of: a, b"));
        Assert.Equal("From -1 to 1.", ModSettingsLayout.AcceptsSentence("-1 to 1"));
        Assert.Equal(new[] { ModSettingsLayout.GeneralTitle },
            ModSettingsLayout.RawSections(new[] { SettingsRow.ForEntry(id, file.Bind("", "Loose", 1, ""), false) }).Select(s => s.Title).ToArray());

        // In the workspace the page is the game-look view: game rows and names, no bracketed captions.
        SettingsPanel.OpenRows("Settings and keybinds", id, rows, menuHosted: true);
        try
        {
            Assert.NotNull(SettingsPanel.GameLookView);
            Assert.True(_runtime.DescriptionMode);
            Assert.Contains("Show seconds", _runtime.WidgetTexts("toggle"));
            Assert.DoesNotContain(_runtime.WidgetTexts("label"), t => t.Contains("[Clock]", StringComparison.Ordinal));
            Assert.Equal(rows.Select(r => r.RowId).ToArray(), SettingsPanel.BoundRowIds().ToArray());
        }
        finally { SettingsPanel.Close(); }
        Assert.Null(SettingsPanel.GameLookView);
    }

    /// <summary>
    /// The row chip: Restart while a needs-restart change waits, else on a server-synced value Server (in
    /// force) or Awaiting server (not received) — in place of the old suffix text. And the other round C
    /// details: an empty header button row is hidden, a keybind is a key cap at the bar edge, the scrollbar
    /// moves between the rows and the column, and the art is never prepared on the open path.
    /// </summary>
    [Fact]
    public void Round_C_chips_key_caps_header_and_scrollbar()
    {
        Assert.Equal("Restart", ModSettingsRowMarks.ChipText(pendingRestart: true, synced: true, locked: true));
        Assert.Equal("Server", ModSettingsRowMarks.ChipText(false, synced: true, locked: true));
        Assert.Equal("Awaiting server", ModSettingsRowMarks.ChipText(false, synced: true, locked: false));
        Assert.Equal("", ModSettingsRowMarks.ChipText(false, false, false));

        // MODPAGE2: no page and no store url, no buttons (the Details rows are not drawn empty).
        Assert.Empty(ModPagePane.PageButtons(null));
        Assert.Empty(ModPagePane.StoreButtons(null, share: false));
        Assert.Single(ModPagePane.StoreButtons("https://x", share: false));

        var g = ModSettingsRowGrid.For(926f);
        var cap = g.ControlSlot(SettingsControlShape.KeyCap);
        Assert.Equal((g.BarRight, ModSettingsRowGrid.KeyCapWidth, ModSettingsRowGrid.KeyCapHeight), (cap.Left + cap.Width, cap.Width, cap.Height));
        Assert.Equal(SettingsControlShape.KeyCap, ModSettingsRowGrid.ShapeOfKind("keybind"));

        string look = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "UnityUiRuntime.SettingsLook.cs");
        SourcePin.AssertUncommented(SourcePin.MethodBody(look, "private static bool SkinKeyCap("), "PaintGameButtonArt(background, control);");
        SourcePin.AssertUncommented(SourcePin.MethodBody(look, "private static bool SkinGridControl("), "SkinKeyCap(control);");
        string runtime = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "UnityUiRuntime.cs");
        SourcePin.AssertUncommented(runtime, "bar.offsetMax = new Vector2(-(PanelLayout.ScrollbarInset + ScrollbarShiftX), -top);");
        string config = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "ConfigurationPane.cs");
        SourcePin.AssertUncommented(SourcePin.MethodBody(config, "private static IUiLabel GameHeading("), "heading.SectionRule();");
    }

    private enum RawFormat
    {
        TwentyFour,
        Twelve,
    }

    // ---- round D (live review 2026-09-30) ---------------------------------------------------------------

    /// <summary>
    /// Round D's page details: the description column's 20-unit inner padding and the gap its thin scrollbar
    /// sits in; chips sized for their caption; the game's standard button found under the Options Back
    /// button's name too; the raw page's title and humanized section names; the Configuration nav in the
    /// Mods list's row look. What this would catch: body text against the scrollbar again, a chip as loose
    /// text, or the flat full-width nav bars coming back.
    /// </summary>
    [Fact]
    public void Round_D_column_padding_chips_buttons_raw_title_and_nav()
    {
        Assert.Equal((20f, 20f), (ModSettingsDescriptionLayout.Padding.L, ModSettingsDescriptionLayout.Padding.R));
        Assert.True(ModSettingsDescriptionLayout.ColumnGap >= ModSettingsDescriptionLayout.ScrollbarWidth + 16f,
            "the scrollbar fits in the gap with air either side");
        Assert.InRange(ModSettingsDescriptionLayout.ScrollbarWidth, 4f, 8f);
        Assert.Equal(56f, ModSettingsRowGrid.ChipWidth(10f));
        Assert.Equal(120f, ModSettingsRowGrid.ChipWidth(100f));
        Assert.Equal(56f, ModSettingsRowGrid.ChipWidth(float.NaN));

        Assert.Equal(SettingsArtPart.MenuButton, SettingsArtLadder.PartOf("SimpleMenuButton_Normal"));
        Assert.Equal(SettingsArtPart.MenuButton, SettingsArtLadder.PartOf("Button_Normal"));
        Assert.Equal("", SettingsArtLadder.AlternateSpriteName(SettingsArtPart.CheckboxFrame));

        Assert.Equal("Client-led sample · Settings", SettingsPanel.RawTitle("Client-led sample"));
        Assert.Equal("Mod · Settings", SettingsPanel.RawTitle(" "));
        var file = File("rawd");
        const string id = "example.menukit.rawd";
        var rows = new List<SettingsRow>
        {
            SettingsRow.ForEntry(id, file.Bind("ForServer", "Delivery", 1, ""), false),
            SettingsRow.ForEntry(id, file.Bind("Client", "JoinCheck", true, ""), false),
        };
        Assert.Equal(new[] { "For server", "Client" }, ModSettingsLayout.RawSections(rows).Select(s => s.Title).ToArray());

        SettingsPanel.OpenRows("Settings and keybinds", id, rows, menuHosted: true);
        try
        {
            var title = _runtime.CellRow(SettingsPanel.RawTitleKey)!;
            Assert.Equal(SettingsPanel.RawTitle(ModsMenu.ModDisplayName(id)), title.Cells().Single().Text);
            var head = _runtime.CellRow(ModSettingsView.HeaderKey)!;
            var keys = head.Cells().Select(c => c.Key).ToArray();
            Assert.True(Array.IndexOf(keys, ModSettingsView.CellBack) >= 0 && Array.IndexOf(keys, ModSettingsView.CellBack) < Array.IndexOf(keys, ModSettingsView.CellResetAll),
                "Back sits on the action row, before Reset all");
            Assert.All(head.Cells().Where(c => c.Kind == CellKind.Button), c => Assert.Equal(CellSkin.KeyCap, c.Skin));
            Assert.DoesNotContain(_runtime.WidgetTexts("button"), t => t == SettingsPanel.BackToModCaption);
        }
        finally { SettingsPanel.Close(); }

        var nav = ModsListLayout.NavRow("Local server", 320f);
        Assert.StartsWith(ModsListLayout.NavRowKeyPrefix, nav.Key, StringComparison.Ordinal);
        Assert.Equal(CellAlign.Left, nav.Cells[0].Align);
        Assert.Equal("Local server", nav.Cells[0].Text);
        Assert.Equal(ModsListLayout.NavRowHeight, nav.Height);
    }

    /// <summary>
    /// The runtime and the Configuration tab keep round D's rules: the settings scrollbar is thin, dark and
    /// starts at the first settings row; plain rows below the first settings row share the rows' width; the
    /// chip is placed left of the control; the tab's buttons are labeled rows whose explanations go to the
    /// description column; the idle preparation also readies the key-cap button art.
    /// </summary>
    [Fact]
    public void Round_D_runtime_and_configuration_rules()
    {
        string look = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "UnityUiRuntime.SettingsLook.cs");
        string shift = SourcePin.MethodBody(look, "private void ApplyScrollbarShift(");
        SourcePin.AssertUncommented(shift, "float top = ScrollbarBaseTop + (settings ? Math.Max(0f, _gridTop) : 0f);");
        SourcePin.AssertUncommented(shift, "PaintScrollbar(settings);");
        SourcePin.AssertUncommented(look, "!row.Grid && row.Pin == MenuRowPin.Content && ColumnShown");
        // The chip sits left of the value (ModSettingsRowGrid.ChipCenterX, tested for no overlap in ServerRulesTests), and a
        // value written after the layout places it again (live 2026-10-06).
        SourcePin.AssertUncommented(look, "rt.anchoredPosition = new Vector2(ModSettingsRowGrid.ChipCenterX(controlLeft, w), 0f);");
        SourcePin.AssertUncommented(SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "UnityUiRuntime.cs"), "Owner?.NoteRowValueChanged(GameObject);");
        SourcePin.AssertUncommented(SourcePin.MethodBody(look, "private static bool SkinGridControl("), "case \"button\":");
        string runtime = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "UnityUiRuntime.cs");
        SourcePin.AssertUncommented(runtime, "if (beside || row.Grid) _narrowWidth = GridWidth;");

        string donors = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "GameArt", "SettingsArtDonors.cs");
        SourcePin.AssertUncommented(SourcePin.MethodBody(donors, "private static void IdleTick()"), "KeybindArtLadder.Prepare();");

        string config = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "ConfigurationPane.cs");
        string build = SourcePin.MethodBody(config, "public void Build(");
        SourcePin.AssertUncommented(build, "panel.SettingsDescriptionMode(true);");
        // Three homes: the purpose line and the row kit replace the Progenitor settings button.
        SourcePin.AssertUncommented(build, "BuildPurpose(panel);");
        SourcePin.AssertUncommented(build, "BuildSettings(panel);");
        SourcePin.AssertUncommented(build, "InColumn(panel, panel.Note(PrivateGameNote));");
        string process = SourcePin.MethodBody(config, "private void BuildProcessRows(");
        SourcePin.AssertUncommented(process, "GridButton(panel, LocalServerRowName, StopServerButton)");
        SourcePin.AssertUncommented(process, "force.Destructive();");
        // Settings organization stage 4: the uninstall row is on Progenitor's page, still in the danger tone.
        SourcePin.AssertUncommented(SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "ProgenitorPage.cs"), ".Destructive()");
        SourcePin.AssertUncommented(SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "ModSettingsView.cs"),
            "if (item.Options.Destructive) button.Destructive();");
        string menu = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "ModsMenu.cs");
        SourcePin.AssertUncommented(SourcePin.MethodBody(menu, "private static void BuildConfigurationPanes("), "AddSectionNavRows(sidebar, ModsTab.Preferences, ConfigurationPane.ConfigGroups)");
    }

    // ---- round E (live polish 2026-09-30) ---------------------------------------------------------------

    /// <summary>
    /// Round E: the raw page is titled with the mod's name, never its id, when a menu is registered; the
    /// destructive caption and the sidebar's bottom buttons take the game's button look; a heading's kit
    /// rule gives way to the game's (and sits at the rows' width); the settings scrollbar is the game's
    /// dark tones; the Configuration tab drops its centered pane title. What this would catch: any of the
    /// five live defects coming back.
    /// </summary>
    [Fact]
    public void Round_E_titles_buttons_rules_and_scrollbar_tones()
    {
        using (var mod = ModsMenu.Register(_owner, "example.menukit.named", "Server-led sample"))
            Assert.Equal("Server-led sample", ModsMenu.ModDisplayName("example.menukit.named"));
        Assert.Equal("", ModsMenu.ModDisplayName(""));

        string look = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "UnityUiRuntime.SettingsLook.cs");
        string danger = SourcePin.MethodBody(look, "public void SetDestructive(");
        SourcePin.AssertUncommented(danger, "tmp.color = DangerCaption;");
        Assert.DoesNotContain("gameObject.name != \"label\"", danger, StringComparison.Ordinal);
        string game = SourcePin.MethodBody(look, "public void SetGameButton(");
        SourcePin.AssertUncommented(game, "if (image != null && !PaintGameButtonArt(image, go))");
        SourcePin.AssertUncommented(game, "tmps[i].verticalAlignment = VerticalAlignmentOptions.Middle;");
        SourcePin.AssertUncommented(SourcePin.MethodBody(look, "public void AddSectionRule("), "if (kitRule != null) kitRule.gameObject.SetActive(false);");
        SourcePin.AssertUncommented(look, "&& (_gridTop >= 0f || string.Equals(row.Kind, \"heading\", StringComparison.Ordinal));");
        SourcePin.AssertUncommented(look, "internal static Color ScrollHandle => new(0.16f, 0.22f, 0.27f, 1f);");

        string menu = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "ModsMenu.cs");
        SourcePin.AssertUncommented(menu, "overview.GameButton();");
        SourcePin.AssertUncommented(menu, "closeMods.GameButton();");
        SourcePin.AssertUncommented(menu, "closeConfig.GameButton();");
        string config = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "ConfigurationPane.cs");
        SourcePin.AssertUncommented(SourcePin.MethodBody(config, "public void Build("), "Ui.HidePaneTitle(panel);");
    }
}
