using System;
using System.Collections.Generic;
using System.Linq;
using Progenitor.Dev.Ops;
using Progenitor.Ui;
using Xunit;
using UiApi = Progenitor.Ui.Ui;

namespace Progenitor.Tests;

/// <summary>
/// NAV1: controller and keyboard navigation (<see cref="UiNav"/>): the repeat rule, the spatial pick, every handle type's
/// decision, and the model's behavior per control against a fake host. Game-free.
/// </summary>
[Collection("PluginLogger")]
public sealed class UiNavTests : IDisposable
{
    private readonly UiTests.FakeUiRuntime _runtime = new();
    private readonly FakeNavHost _host = new();
    private readonly INavHost _previous;
    private readonly object _owner = new();

    public UiNavTests()
    {
        UiLayoutBatch.ResetForTests();
        UiApi.ResetForTests();
        UiApi.SetRuntimeForTests(_runtime);
        _previous = UiNav.Host;
        UiNav.Host = _host;
        UiNav.Reset();
    }

    public void Dispose()
    {
        UiNav.Reset();
        UiNav.Host = _previous;
        UiLayoutBatch.ResetForTests();
        UiApi.ResetForTests();
    }

    private UiPanelHandle Panel() => (UiPanelHandle)UiApi.Panel("Nav", _owner, "example");

    private NavSurface Surface(params IReadOnlyList<UiHandle>[] panes) => _host.Surface = new NavSurface("s", panes);

    private static List<UiHandle> Children(UiPanelHandle panel) => panel.ChildrenSnapshot().ToList();

    private static ScreenRect Row(float top, float left = 0f, float width = 400f, float height = 30f) =>
        ScreenRect.FromCorners(left, top - height, left + width, top);

    // ---- the repeat rule ---------------------------------------------------------------------------------------

    [Fact]
    public void A_press_is_one_step_and_a_held_direction_repeats_after_the_delay()
    {
        var r = new NavRepeater();
        var output = new List<NavCommand>();
        var down = new NavSample { Down = true };
        r.Step(down, 0.0, output);
        Assert.Equal(new[] { NavCommand.Down }, output);
        output.Clear();
        r.Step(down, NavRepeater.InitialDelay - 0.01, output);
        Assert.Empty(output);
        r.Step(down, NavRepeater.InitialDelay + 0.001, output);
        Assert.Equal(new[] { NavCommand.Down }, output);
        output.Clear();
        r.Step(down, NavRepeater.InitialDelay + NavRepeater.Interval * 0.5, output);
        Assert.Empty(output);
        r.Step(default, 1.0, output);
        r.Step(down, 1.01, output);
        Assert.Equal(new[] { NavCommand.Down }, output);
    }

    [Fact]
    public void Buttons_step_once_per_press_and_never_repeat()
    {
        var r = new NavRepeater();
        var output = new List<NavCommand>();
        var a = new NavSample { Submit = true, Cancel = true, TabNext = true };
        for (int i = 0; i < 60; i++) r.Step(a, i * 0.1, output);
        Assert.Equal(new[] { NavCommand.Submit, NavCommand.Cancel, NavCommand.TabNext }, output);
    }

    // ---- the spatial pick -------------------------------------------------------------------------------------

    [Fact]
    public void Down_takes_the_nearest_row_below_in_line_and_right_stays_on_the_row()
    {
        var a = new NavTarget(null!, "a");
        var from = Row(500, 0, 300);
        var candidates = new List<(NavTarget, ScreenRect)>
        {
            (new NavTarget(null!, "far below"), Row(400, 0, 300)),
            (new NavTarget(null!, "below"), Row(460, 0, 300)),
            (new NavTarget(null!, "below, far right"), Row(460, 900, 40)),
            (new NavTarget(null!, "same row, right"), Row(500, 320, 40)),
            (new NavTarget(null!, "above"), Row(540, 0, 300)),
        };
        Assert.Equal(1, UiNavMath.Pick(from, candidates, NavCommand.Down));
        Assert.Equal(4, UiNavMath.Pick(from, candidates, NavCommand.Up));
        Assert.Equal(3, UiNavMath.Pick(from, candidates, NavCommand.Right));
        Assert.Equal(-1, UiNavMath.Pick(from, candidates, NavCommand.Left));
        Assert.Equal(-1, UiNavMath.Pick(ScreenRect.Unknown, candidates, NavCommand.Down));
        _ = a;
    }

    [Fact]
    public void A_slider_step_is_a_twentieth_of_its_range_and_clamps()
    {
        Assert.Equal(0.55f, UiNavMath.SliderStep(0.5f, 0f, 1f, 1), 3);
        Assert.Equal(1f, UiNavMath.SliderStep(0.99f, 0f, 1f, 1), 3);
        Assert.Equal(0f, UiNavMath.SliderStep(0.01f, 0f, 1f, -1), 3);
        Assert.Equal(5f, UiNavMath.SliderStep(5f, 3f, 3f, 1), 3);
    }

    // ---- every control has a decision ------------------------------------------------------------------------

    /// <summary>A control that ships without a navigation decision fails here: give it a role or a reason.</summary>
    [Fact]
    public void Every_concrete_handle_type_is_a_stop_or_says_why_not()
    {
        var types = typeof(UiHandle).Assembly.GetTypes()
            .Where(t => typeof(UiHandle).IsAssignableFrom(t) && !t.IsAbstract)
            .ToList();
        Assert.True(types.Count >= 15, "found only " + types.Count + " handle types");
        var undecided = types.Where(t => UiNav.RoleOf(t) == NavRole.None && !UiNav.NotStops.ContainsKey(t)).Select(t => t.Name).ToList();
        Assert.True(undecided.Count == 0, "no navigation decision for: " + string.Join(", ", undecided));
        var both = types.Where(t => UiNav.RoleOf(t) != NavRole.None && UiNav.NotStops.ContainsKey(t)).Select(t => t.Name).ToList();
        Assert.Empty(both);
    }

    // ---- the model ---------------------------------------------------------------------------------------------

    [Fact]
    public void The_first_press_shows_the_focus_and_the_next_moves_it_past_labels()
    {
        using var panel = Panel();
        panel.Label("Heading");
        var first = panel.Button("First");
        panel.Label("Text");
        var second = panel.Button("Second");
        Surface(Children(panel));

        Assert.True(UiNav.Execute(NavCommand.Down));
        Assert.Same(first, UiNav.Focus.Handle);
        Assert.True(UiNav.Marked);
        Assert.Contains("mark:First", _host.Calls);

        Assert.True(UiNav.Execute(NavCommand.Down));
        Assert.Same(second, UiNav.Focus.Handle);
        Assert.Contains("unmark:First", _host.Calls);
        Assert.False(UiNav.Execute(NavCommand.Down));
        Assert.Same(second, UiNav.Focus.Handle);
    }

    /// <summary>
    /// FORMDIALOG1: in a dialog, Tab walks the text field and every stop and wraps around; leaving the field takes its
    /// keyboard back, landing on it gives it the keyboard and puts the marker away; Shift+Tab walks back; Up from the
    /// first stop returns to the field. A screen that is not a dialog takes no Tab.
    /// </summary>
    [Fact]
    public void Tab_walks_a_dialogs_field_and_stops_and_Up_from_the_first_stop_returns_to_the_field()
    {
        using var panel = Panel();
        // A dialog's field is a plain cell row whose input cell is no stop of its own (the Prompt's dialog-input).
        var field = panel.CellRow(new CellRowSpec("dialog-input", new[] { new CellSpec(CellKind.Input, "field") }, plain: true));
        Assert.False(UiNav.IsStop((UiHandle)field));
        var first = panel.Button("Global");
        var save = panel.Button("Save");
        var surface = Surface(Children(panel));
        Assert.False(UiNav.TabStep(1, fromField: true));

        surface.TabCycle = true;
        surface.Field = (UiHandle)field;
        Assert.True(UiNav.TabStep(1, fromField: true));
        Assert.Contains("blur", _host.Calls);
        Assert.Same(first, UiNav.Focus.Handle);
        Assert.True(UiNav.Marked);

        Assert.True(UiNav.TabStep(1, fromField: false));
        Assert.Same(save, UiNav.Focus.Handle);

        _host.Calls.Clear();
        Assert.True(UiNav.TabStep(1, fromField: false));
        Assert.Contains("text:" + field.Text, _host.Calls);
        Assert.False(UiNav.Marked);

        Assert.True(UiNav.TabStep(-1, fromField: true));
        Assert.Same(save, UiNav.Focus.Handle);
        Assert.True(UiNav.TabStep(-1, fromField: false));
        Assert.Same(first, UiNav.Focus.Handle);

        _host.Calls.Clear();
        Assert.True(UiNav.Execute(NavCommand.Up));
        Assert.Contains("text:" + field.Text, _host.Calls);
        Assert.False(UiNav.Marked);
    }

    [Fact]
    public void A_windows_left_column_is_entered_from_the_tabs_and_crossed_to_the_nearest_row()
    {
        // WINDOWS1 live round 4: on the Tabbed window's Settings tab Down from the tabs went into the content and Left from
        // its first row stayed put, so the info column was out of reach.
        using var panel = Panel();
        var refresh = panel.Button("Refresh");
        var tab = panel.Button("Tabs");
        var sort = panel.Button("Sort on open");
        var reach = panel.Button("Reach");
        var locate = panel.Button("Locate all");
        var add = panel.Button("Add facts");
        _host.Rects[(UiHandle)refresh] = Row(700, 0, 150);
        _host.Rects[(UiHandle)tab] = Row(650, 0, 300);
        _host.Rects[(UiHandle)sort] = Row(600, 400, 500);
        _host.Rects[(UiHandle)reach] = Row(550, 400, 500);
        _host.Rects[(UiHandle)locate] = Row(450, 0, 150);
        _host.Rects[(UiHandle)add] = Row(400, 0, 150);
        var surface = Surface(Children(panel));
        surface.Column = new HashSet<UiHandle> { (UiHandle)locate, (UiHandle)add };
        surface.TopRows = new HashSet<UiHandle> { (UiHandle)refresh, (UiHandle)tab };

        UiNav.Execute(NavCommand.Down);
        Assert.Same(refresh, UiNav.Focus.Handle);
        UiNav.Execute(NavCommand.Down);
        Assert.Same(tab, UiNav.Focus.Handle);
        UiNav.Execute(NavCommand.Down);   // the column first, though the content's first row is nearer
        Assert.Same(locate, UiNav.Focus.Handle);
        UiNav.Execute(NavCommand.Right);  // nothing level: the nearest row of the content
        Assert.Same(reach, UiNav.Focus.Handle);
        UiNav.Execute(NavCommand.Up);
        Assert.Same(sort, UiNav.Focus.Handle);
        UiNav.Execute(NavCommand.Left);   // back to the column's nearest row
        Assert.Same(locate, UiNav.Focus.Handle);
        UiNav.Execute(NavCommand.Down);
        Assert.Same(add, UiNav.Focus.Handle);

        // From the toolbar, Down still goes to the tab row first (a pinned row below wins over the column).
        UiNav.FocusOn((UiHandle)refresh, "");
        UiNav.Execute(NavCommand.Down);
        Assert.Same(tab, UiNav.Focus.Handle);
    }

    [Fact]
    public void Directions_follow_the_controls_places_on_screen()
    {
        using var panel = Panel();
        var top = panel.Button("Top");
        var left = panel.Button("Left");
        var right = panel.Button("Right");
        _host.Rects[(UiHandle)top] = Row(500);
        _host.Rects[(UiHandle)left] = Row(450, 0, 180);
        _host.Rects[(UiHandle)right] = Row(450, 220, 180);
        Surface(Children(panel));

        UiNav.Execute(NavCommand.Down);
        Assert.Same(top, UiNav.Focus.Handle);
        UiNav.Execute(NavCommand.Down);
        Assert.Same(left, UiNav.Focus.Handle);
        UiNav.Execute(NavCommand.Right);
        Assert.Same(right, UiNav.Focus.Handle);
        UiNav.Execute(NavCommand.Up);
        Assert.Same(top, UiNav.Focus.Handle);
    }

    [Fact]
    public void A_presses_a_button_through_the_click_slot()
    {
        using var panel = Panel();
        panel.Button("Apply");
        Surface(Children(panel));
        UiNav.Execute(NavCommand.Submit); // shows the focus
        UiNav.Execute(NavCommand.Submit);
        Assert.Contains("click:Apply", _host.Calls);
    }

    [Fact]
    public void Enter_runs_a_controls_own_enter_hook_while_Space_and_A_still_press_it()
    {
        using var panel = Panel();
        var row = panel.Button("Row");
        int enters = 0;
        UiNav.EnterKey(row, () => { enters++; return true; });
        Surface(Children(panel));
        UiNav.Execute(NavCommand.Submit); // shows the focus
        UiNav.Execute(NavCommand.Submit); // Space or A: the press
        Assert.Contains("click:Row", _host.Calls);
        Assert.Equal(0, enters);
        _host.Calls.Clear();
        Assert.True(UiNav.ExecuteEnterForTests()); // Enter: the hook, not the press
        Assert.Equal(1, enters);
        Assert.DoesNotContain("click:Row", _host.Calls);
        UiNav.EnterKey(row, () => false); // a hook that declines: Enter presses as Space does
        UiNav.ExecuteEnterForTests();
        Assert.Contains("click:Row", _host.Calls);
        UiNav.EnterKey(row, null);
    }

    [Fact]
    public void A_toggle_flips_on_A_and_on_left_or_right()
    {
        using var panel = Panel();
        var toggle = (UiToggleHandle)panel.Toggle("Hints", false);
        Surface(Children(panel));
        UiNav.Execute(NavCommand.Down);
        UiNav.Execute(NavCommand.Submit);
        Assert.True(toggle.Value);
        UiNav.Execute(NavCommand.Left);
        Assert.False(toggle.Value);
        UiNav.Execute(NavCommand.Right);
        Assert.True(toggle.Value);
    }

    [Fact]
    public void Left_and_right_change_a_slider_a_number_and_a_slider_field()
    {
        using var panel = Panel();
        var slider = (UiSliderHandle)panel.Slider("Volume", 0f, 1f, 0.5f);
        var number = (UiNumericHandle)((IUiPanel)panel).Number("Count", 3, 0, 10);
        var field = (UiSliderEntryHandle)((IUiPanel)panel).SliderField("Speed", 0f, 10f, 1f, 5f);
        Surface(Children(panel));

        UiNav.Execute(NavCommand.Down);
        Assert.Same(slider, UiNav.Focus.Handle);
        UiNav.Execute(NavCommand.Right);
        Assert.Equal(0.55f, slider.Value, 3);

        UiNav.Execute(NavCommand.Down);
        UiNav.Execute(NavCommand.Right);
        Assert.Equal(4d, number.Value);
        UiNav.Execute(NavCommand.Left);
        UiNav.Execute(NavCommand.Left);
        Assert.Equal(2d, number.Value);

        UiNav.Execute(NavCommand.Down);
        UiNav.Execute(NavCommand.Right);
        Assert.Equal(6f, field.Value, 3);

        // A on a number gives its box the keyboard.
        UiNav.Execute(NavCommand.Submit);
        Assert.Contains("text:Speed", _host.Calls);
    }

    [Fact]
    public void A_dropdown_opens_on_A_moves_its_highlight_and_picks_and_B_closes_it()
    {
        using var panel = Panel();
        var dropdown = (UiChoiceHandle)((IUiPanel)panel).Dropdown("Style", new[] { "Short", "Full", "Verbose" }, "Short");
        Surface(Children(panel));
        UiNav.Execute(NavCommand.Down);

        UiNav.Execute(NavCommand.Right);
        Assert.Equal("Full", dropdown.Value);

        UiNav.Execute(NavCommand.Submit);
        Assert.True(dropdown.IsOpen);
        UiNav.Execute(NavCommand.Down);
        UiNav.Execute(NavCommand.Submit);
        Assert.False(dropdown.IsOpen);
        Assert.Equal("Verbose", dropdown.Value);

        UiNav.Execute(NavCommand.Submit);
        Assert.True(dropdown.IsOpen);
        UiNav.Execute(NavCommand.Cancel);
        Assert.False(dropdown.IsOpen);
        Assert.Equal("Verbose", dropdown.Value);
        Assert.DoesNotContain("escape", _host.Calls);
    }

    [Fact]
    public void A_radio_moves_with_left_and_right_and_a_multi_select_flips_the_option_under_its_cursor()
    {
        using var panel = Panel();
        var radio = (UiOptionsHandle)((IUiPanel)panel).Radio("Size", new[] { "S", "M", "L" }, "S");
        var multi = (UiOptionsHandle)((IUiPanel)panel).MultiSelect("Topics", new[] { "Combat", "Travel", "Build" }, new[] { "Combat" });
        Surface(Children(panel));

        UiNav.Execute(NavCommand.Down);
        UiNav.Execute(NavCommand.Right);
        Assert.Equal("M", radio.Value);
        UiNav.Execute(NavCommand.Right);
        UiNav.Execute(NavCommand.Right);
        Assert.Equal("L", radio.Value);
        UiNav.Execute(NavCommand.Left);
        Assert.Equal("M", radio.Value);

        UiNav.Execute(NavCommand.Down);
        Assert.Same(multi, UiNav.Focus.Handle);
        Assert.Equal("0", UiNav.Focus.Part);
        Assert.Equal(0, multi.HoveredOption);
        UiNav.Execute(NavCommand.Right);
        Assert.Equal("1", UiNav.Focus.Part);
        UiNav.Execute(NavCommand.Submit);
        Assert.True(multi.IsSelected("Travel"));
        Assert.True(multi.IsSelected("Combat"));
        UiNav.Execute(NavCommand.Left);
        UiNav.Execute(NavCommand.Submit);
        Assert.False(multi.IsSelected("Combat"));
    }

    [Fact]
    public void B_without_an_open_list_or_a_page_to_leave_is_the_Escape_path()
    {
        using var panel = Panel();
        panel.Button("Only");
        Surface(Children(panel));
        UiNav.Execute(NavCommand.Down);
        UiNav.Execute(NavCommand.Cancel);
        Assert.Contains("escape", _host.Calls);
    }

    [Fact]
    public void In_the_workspace_A_on_a_list_row_moves_on_to_the_page_once_it_is_built_and_B_comes_back()
    {
        using var list = Panel();
        var row = list.Button("Example mod");
        using var page = Panel();
        var surface = new NavSurface("workspace:mods", new IReadOnlyList<UiHandle>[] { Children(list), new List<UiHandle>() })
        {
            MasterDetail = true,
        };
        _host.Surface = surface;

        UiNav.Execute(NavCommand.Down);
        Assert.Same(row, UiNav.Focus.Handle);
        UiNav.Execute(NavCommand.Submit);
        Assert.Contains("click:Example mod", _host.Calls);

        // The page builds on a later frame.
        var toggle = page.Toggle("Hints", true);
        _host.Surface = new NavSurface("workspace:mods", new IReadOnlyList<UiHandle>[] { Children(list), Children(page) }) { MasterDetail = true };
        UiNav.Tick(default, 1.0);
        Assert.Same(toggle, UiNav.Focus.Handle);
        Assert.Equal(1, UiNav.Pane);

        UiNav.Execute(NavCommand.Cancel);
        Assert.Same(row, UiNav.Focus.Handle);
        Assert.Equal(0, UiNav.Pane);
        Assert.DoesNotContain("escape", _host.Calls);
    }

    [Fact]
    public void The_triggers_step_the_tabs()
    {
        using var panel = Panel();
        panel.Button("Only");
        var steps = new List<int>();
        _host.Surface = new NavSurface("workspace:mods", new IReadOnlyList<UiHandle>[] { Children(panel) })
        {
            Tab = d => { steps.Add(d); return true; },
        };
        UiNav.Execute(NavCommand.TabNext);
        UiNav.Execute(NavCommand.TabPrev);
        Assert.Equal(new[] { 1, -1 }, steps);
    }

    /// <summary>Review finding: a trigger at the last tab moved nothing and must not send the focus back to the list.</summary>
    [Fact]
    public void A_trigger_past_the_last_tab_keeps_the_focus()
    {
        using var panel = Panel();
        panel.Button("First");
        var second = panel.Button("Second");
        _host.Surface = new NavSurface("workspace:server", new IReadOnlyList<UiHandle>[] { Children(panel) }) { Tab = _ => false };
        UiNav.Execute(NavCommand.Down);
        UiNav.Execute(NavCommand.Down);
        Assert.Same(second, UiNav.Focus.Handle);
        Assert.False(UiNav.Execute(NavCommand.TabNext));
        UiNav.Tick(default, 1.0);
        Assert.Same(second, UiNav.Focus.Handle);
    }

    /// <summary>Review finding: A presses the focused row's own cell, never another live row with the same key.</summary>
    [Fact]
    public void A_on_a_cell_presses_the_focused_row_even_when_another_row_shares_its_key()
    {
        using var other = Panel();
        using var panel = Panel();
        var spec = new CellRowSpec("same", new[] { new CellSpec(CellKind.Button, "go") { Text = "Go" } });
        var first = (UiCellRowHandle)UiCellRows.CellRow(other, spec);
        var mine = (UiCellRowHandle)UiCellRows.CellRow(panel, new CellRowSpec("same", new[] { new CellSpec(CellKind.Button, "go") { Text = "Go" } }));
        var pressed = new List<string>();
        first.OnClick("go", () => pressed.Add("first"));
        mine.OnClick("go", () => pressed.Add("mine"));
        Surface(Children(panel));

        UiNav.Execute(NavCommand.Down);
        Assert.Same(mine, UiNav.Focus.Handle);
        // A row with its own click starts on the row; right reaches its button.
        if (UiNav.Focus.Part.Length == 0) UiNav.Execute(NavCommand.Right);
        Assert.Equal("go", UiNav.Focus.Part);
        UiNav.Execute(NavCommand.Submit);
        Assert.Equal(new[] { "mine" }, pressed);
    }

    /// <summary>Review finding: B while a text field has the keyboard is Escape's (it blurs the field), not a pane move.</summary>
    [Fact]
    public void B_while_a_field_has_the_keyboard_is_the_Escape_path_not_back_to_the_list()
    {
        using var list = Panel();
        var row = list.Button("Mod");
        using var page = Panel();
        page.Input("Name", "");
        _host.Surface = new NavSurface("workspace:mods", new IReadOnlyList<UiHandle>[] { Children(list), Children(page) }) { MasterDetail = true };
        UiNav.Execute(NavCommand.Down);
        UiNav.Execute(NavCommand.Submit);
        for (int i = 0; i <= UiNav.PendingFreshFrames; i++) UiNav.Tick(default, 1.0 + i * 0.016);
        Assert.Equal(1, UiNav.Pane);

        UiNav.Tick(new NavSample { Cancel = true, TextFocused = true }, 2.0);
        Assert.Contains("escape", _host.Calls);
        Assert.Equal(1, UiNav.Pane);
    }

    /// <summary>Review finding: the page still shows the previous mod's controls for a frame; the focus waits for new ones.</summary>
    [Fact]
    public void After_A_on_a_list_row_the_focus_waits_for_the_new_page_not_the_old_one()
    {
        using var list = Panel();
        list.Button("Mod");
        using var oldPage = Panel();
        oldPage.Button("Old control");
        _host.Surface = new NavSurface("workspace:mods", new IReadOnlyList<UiHandle>[] { Children(list), Children(oldPage) }) { MasterDetail = true };
        UiNav.Execute(NavCommand.Down);
        UiNav.Execute(NavCommand.Submit);

        UiNav.Tick(default, 1.0);
        Assert.Equal(0, UiNav.Pane);

        using var newPage = Panel();
        var fresh = newPage.Button("New control");
        _host.Surface = new NavSurface("workspace:mods", new IReadOnlyList<UiHandle>[] { Children(list), Children(newPage) }) { MasterDetail = true };
        UiNav.Tick(default, 1.1);
        Assert.Same(fresh, UiNav.Focus.Handle);
    }

    /// <summary>A on the row of the page already shown builds nothing new; the focus still moves to the page.</summary>
    [Fact]
    public void A_on_the_open_mods_row_still_moves_the_focus_to_its_page()
    {
        using var list = Panel();
        list.Button("Mod");
        using var page = Panel();
        var apply = page.Button("Apply");
        _host.Surface = new NavSurface("workspace:mods", new IReadOnlyList<UiHandle>[] { Children(list), Children(page) }) { MasterDetail = true };
        UiNav.Execute(NavCommand.Down);
        UiNav.Execute(NavCommand.Submit);
        UiNav.Tick(default, 1.0);
        Assert.Equal(0, UiNav.Pane);
        for (int i = 0; i < UiNav.PendingFreshFrames; i++) UiNav.Tick(default, 1.1 + i * 0.016);
        Assert.Same(apply, UiNav.Focus.Handle);
    }

    [Fact]
    public void The_game_close_is_held_only_while_B_means_back_to_the_list()
    {
        using var list = Panel();
        list.Button("Mod");
        using var page = Panel();
        page.Button("Apply");
        _host.Surface = new NavSurface("workspace:mods", new IReadOnlyList<UiHandle>[] { Children(list), Children(page) }) { MasterDetail = true };
        UiNav.Execute(NavCommand.Down);
        UiNav.Tick(default, 0.5);
        Assert.False(UiNav.HoldsGameClose);
        UiNav.Execute(NavCommand.Submit);
        for (int i = 0; i <= UiNav.PendingFreshFrames; i++) UiNav.Tick(default, 1.0 + i * 0.016);
        Assert.True(UiNav.HoldsGameClose);
        UiNav.Execute(NavCommand.Cancel);
        Assert.False(UiNav.HoldsGameClose);
    }

    /// <summary>Review finding: the Enter that answered a dialog (or left a field) must not act again behind it.</summary>
    [Fact]
    public void A_keyboard_key_that_began_owned_stays_out_until_released()
    {
        bool latched = false;
        Assert.False(UiNavMath.KeyCounts(true, gated: true, ref latched));
        Assert.False(UiNavMath.KeyCounts(true, gated: false, ref latched));
        Assert.False(UiNavMath.KeyCounts(false, gated: false, ref latched));
        Assert.True(UiNavMath.KeyCounts(true, gated: false, ref latched));
    }

    [Fact]
    public void The_pointer_puts_the_marker_away_and_the_next_press_only_brings_it_back()
    {
        using var panel = Panel();
        var first = panel.Button("First");
        panel.Button("Second");
        Surface(Children(panel));
        UiNav.Execute(NavCommand.Down);
        Assert.True(UiNav.Marked);

        UiNav.Tick(new NavSample { PointerActive = true }, 1.0);
        Assert.False(UiNav.Marked);
        Assert.Contains("unmark:First", _host.Calls);

        UiNav.Execute(NavCommand.Down);
        Assert.True(UiNav.Marked);
        Assert.Same(first, UiNav.Focus.Handle);
    }

    [Fact]
    public void A_text_field_with_the_keyboard_takes_every_key_but_B()
    {
        using var panel = Panel();
        var first = panel.Button("First");
        panel.Button("Second");
        Surface(Children(panel));
        UiNav.Execute(NavCommand.Down);

        UiNav.Tick(new NavSample { Down = true, TextFocused = true }, 1.0);
        Assert.Same(first, UiNav.Focus.Handle);
        UiNav.Tick(new NavSample { Cancel = true, TextFocused = true }, 2.0);
        Assert.Contains("escape", _host.Calls);
    }

    [Fact]
    public void A_rebuilt_control_hands_the_focus_to_the_stop_nearest_where_it_was()
    {
        using var panel = Panel();
        var a = panel.Button("A");
        var b = panel.Button("B");
        _host.Rects[(UiHandle)a] = Row(500);
        _host.Rects[(UiHandle)b] = Row(450);
        Surface(Children(panel));
        UiNav.Execute(NavCommand.Down);
        UiNav.Execute(NavCommand.Down);
        Assert.Same(b, UiNav.Focus.Handle);

        b.Dispose();
        var c = panel.Button("C");
        _host.Rects[(UiHandle)c] = Row(452);
        Surface(Children(panel));
        UiNav.Tick(default, 1.0);
        Assert.Same(c, UiNav.Focus.Handle);
    }

    [Fact]
    public void A_hooked_row_draws_its_own_focus_resets_on_X_and_a_skipped_control_is_passed_over()
    {
        using var panel = Panel();
        var toggle = panel.Toggle("Hints", true);
        var reset = panel.Button("«");
        var after = panel.Button("After");
        var focus = new List<bool>();
        int resets = 0;
        UiNav.Hook(toggle, new NavRowHook(focus.Add, () => { resets++; return true; }));
        UiNav.Skip(reset);
        Surface(Children(panel));

        UiNav.Execute(NavCommand.Down);
        Assert.Same(toggle, UiNav.Focus.Handle);
        Assert.Equal(new[] { true }, focus);
        Assert.DoesNotContain(_host.Calls, c => c.StartsWith("mark:Hints", StringComparison.Ordinal));

        Assert.True(UiNav.Execute(NavCommand.Reset));
        Assert.Equal(1, resets);

        UiNav.Execute(NavCommand.Down);
        Assert.Same(after, UiNav.Focus.Handle);
        Assert.Equal(new[] { true, false }, focus);
    }

    /// <summary>
    /// A handle holds its plugin's owner: with no panel shown the runtime's pump stops, and the focus must not keep the
    /// handle (and an unloaded plugin) alive.
    /// </summary>
    [Fact]
    public void With_no_panel_shown_the_focus_lets_go_of_its_handle()
    {
        using var panel = Panel();
        panel.Button("Only");
        Surface(Children(panel));
        UiNav.Execute(NavCommand.Down);
        Assert.False(UiNav.Focus.IsEmpty);

        UiNav.Release();
        Assert.True(UiNav.Focus.IsEmpty);
        Assert.True(UiNav.Marked, "the last input stays a navigation input, so the next screen draws its focus at once");
    }

    /// <summary>Live 2026-10-10: after B answered a dialog, the focus comes back to the control that opened it.</summary>
    [Fact]
    public void When_a_dialog_closes_the_focus_returns_to_the_control_that_opened_it()
    {
        using var page = Panel();
        page.Button("First");
        var asker = page.Button("Ask");
        var pageSurface = new NavSurface("workspace:mods", new IReadOnlyList<UiHandle>[] { new List<UiHandle>(), Children(page) }) { MasterDetail = true, StartPane = 0 };
        _host.Surface = pageSurface;
        UiNav.Execute(NavCommand.Down);
        UiNav.Execute(NavCommand.Down);
        Assert.Same(asker, UiNav.Focus.Handle);

        using var dialog = Panel();
        var no = dialog.Button("No");
        _host.Surface = new NavSurface("dialog:1", new IReadOnlyList<UiHandle>[] { Children(dialog) });
        UiNav.Tick(default, 1.0);
        Assert.Same(no, UiNav.Focus.Handle);

        _host.Surface = pageSurface;
        UiNav.Tick(default, 2.0);
        Assert.Same(asker, UiNav.Focus.Handle);
        Assert.Equal(1, UiNav.Pane);
    }

    /// <summary>Live 2026-10-10: the Mods page redraws when its prompt closes; the focus goes to the control nearest where it was.</summary>
    [Fact]
    public void When_the_page_was_rebuilt_under_a_dialog_the_focus_returns_to_the_nearest_control()
    {
        using var list = Panel();
        list.Button("Mod");
        using var page = Panel();
        var top = page.Button("Switch");
        var ask = page.Button("Uninstall");
        _host.Rects[(UiHandle)top] = Row(500);
        _host.Rects[(UiHandle)ask] = Row(450);
        var surface = new NavSurface("workspace:mods", new IReadOnlyList<UiHandle>[] { Children(list), Children(page) }) { MasterDetail = true };
        _host.Surface = surface;
        UiNav.Execute(NavCommand.Down);
        UiNav.Execute(NavCommand.Submit);
        for (int i = 0; i <= UiNav.PendingFreshFrames; i++) UiNav.Tick(default, 1.0 + i * 0.016);
        UiNav.Execute(NavCommand.Down);
        Assert.Same(ask, UiNav.Focus.Handle);

        using var dialog = Panel();
        dialog.Button("No");
        _host.Surface = new NavSurface("dialog:1", new IReadOnlyList<UiHandle>[] { Children(dialog) });
        UiNav.Tick(default, 2.0);

        top.Dispose();
        ask.Dispose();
        Assert.True(UiNav.HasReturnFocus);
        UiNav.Tick(default, 2.5);
        Assert.False(UiNav.HasReturnFocus); // PINS1: the disposed control (and its plugin owner) is let go
        page.Button("Switch again");
        var askAgain = page.Button("Uninstall again");
        var kids = Children(page);
        _host.Rects[kids[0]] = Row(500);
        _host.Rects[(UiHandle)askAgain] = Row(450);
        _host.Surface = new NavSurface("workspace:mods", new IReadOnlyList<UiHandle>[] { Children(list), kids }) { MasterDetail = true };
        UiNav.Tick(default, 3.0);
        Assert.Same(askAgain, UiNav.Focus.Handle);
        Assert.Equal(1, UiNav.Pane);
    }

    [Fact]
    public void A_new_screen_starts_at_its_preferred_stop()
    {
        using var panel = Panel();
        panel.Button("No");
        var yes = panel.Button("Yes");
        _host.Surface = new NavSurface("dialog:1", new IReadOnlyList<UiHandle>[] { Children(panel) }) { Start = new NavTarget((UiHandle)yes) };
        UiNav.Execute(NavCommand.Down);
        Assert.Same(yes, UiNav.Focus.Handle);
    }

    /// <summary>
    /// Live 2026-10-10: the focus started on the Uninstall prompt's Yes (orange, not flagged destructive) and one A
    /// uninstalled a mod. Every question with a dismissal starts on it; a notice with one answer starts on that.
    /// </summary>
    [Fact]
    public void A_dialog_with_a_dismissal_starts_on_it()
    {
        var plain = new DialogContent { MainCaption = "OK", CancelCaption = "Cancel" };
        Assert.Equal(plain.CancelKey, UiNavHost.StartKey(plain));
        Assert.Equal(plain.CancelKey, UiNavHost.StartKey(new DialogContent { MainCaption = "Uninstall", CancelCaption = "Keep", MainTone = CellTone.Orange }));
        Assert.Equal(plain.CancelKey, UiNavHost.StartKey(new DialogContent { MainCaption = "", CancelCaption = "Close" }));
        var notice = new DialogContent { MainCaption = "OK", CancelCaption = "" };
        Assert.Equal(notice.MainKey, UiNavHost.StartKey(notice));
    }

    // ---- the bridge -------------------------------------------------------------------------------------------

    [Fact]
    public void The_virtual_gamepad_state_follows_the_dump_layout()
    {
        Assert.Equal(28, NavOps.PadState("a").Length);
        Assert.Equal(1u << 6, BitConverter.ToUInt32(NavOps.PadState("a"), 0));
        Assert.Equal(1u << 5, BitConverter.ToUInt32(NavOps.PadState("b"), 0));
        Assert.Equal(1u << 1, BitConverter.ToUInt32(NavOps.PadState("down"), 0));
        Assert.Equal(1f, BitConverter.ToSingle(NavOps.PadState("lt"), 0x14));
        Assert.Equal(1f, BitConverter.ToSingle(NavOps.PadState("rt"), 0x18));
        Assert.All(NavOps.PadState(null), b => Assert.Equal(0, b));

        var ev = NavOps.PadStateEvent(7, 1.5, NavOps.PadState("x"));
        Assert.Equal(52, ev.Length);
        Assert.Equal(KeyEventBytes.StateType, BitConverter.ToInt32(ev, 0));
        Assert.Equal(52, BitConverter.ToUInt16(ev, 4));
        Assert.Equal(7, BitConverter.ToUInt16(ev, 6));
        Assert.Equal(1.5, BitConverter.ToDouble(ev, 8));
        Assert.Equal(0x47504144, BitConverter.ToInt32(ev, 0x14));
        Assert.Equal(1u << 7, BitConverter.ToUInt32(ev, 0x18));
    }

    [Theory]
    [InlineData("A", "a")]
    [InlineData(" rt ", "rt")]
    [InlineData("jump", null)]
    [InlineData(null, null)]
    public void Bridge_button_names_normalize(string? given, string? expected) => Assert.Equal(expected, NavOps.Normalize(given));

    [Fact]
    public void The_core_bridge_names_every_button()
    {
        foreach (string b in new[] { "up", "down", "left", "right", "a", "b", "x", "lt", "rt" })
            Assert.NotEqual(NavCommand.None, UiNavTestBridge.Command(b));
    }

    [Fact]
    public void Left_and_right_move_between_compact_dropdowns_and_never_step_their_values()
    {
        using var panel = Panel();
        panel.BeginInline();
        var scope = (UiChoiceHandle)panel.CompactDropdown("Scope", new[] { "castle", "all" }, "castle");
        var match = (UiChoiceHandle)panel.CompactDropdown("Match", new[] { "any", "all" }, "any");
        panel.EndInline();
        Assert.True(scope.Compact && match.Compact);
        // One row: Scope at the left, Match beside it.
        _host.Rects[scope] = Row(100f, 0f, 180f);
        _host.Rects[match] = Row(100f, 200f, 180f);
        Surface(Children(panel));
        UiNav.Execute(NavCommand.Down);
        Assert.Same(scope, UiNav.Focus.Handle);

        UiNav.Execute(NavCommand.Right);
        Assert.Same(match, UiNav.Focus.Handle);
        Assert.Equal("castle", scope.Value);
        Assert.Equal("any", match.Value);
        UiNav.Execute(NavCommand.Right); // the row's end: nothing beside it, the value stays
        Assert.Same(match, UiNav.Focus.Handle);
        Assert.Equal("any", match.Value);
        UiNav.Execute(NavCommand.Left);
        Assert.Same(scope, UiNav.Focus.Handle);

        // A opens the list; there Left and Right are taken by the open list and move nothing.
        UiNav.Execute(NavCommand.Submit);
        Assert.True(scope.IsOpen);
        UiNav.Execute(NavCommand.Right);
        Assert.Same(scope, UiNav.Focus.Handle);
        UiNav.Execute(NavCommand.Cancel);
        Assert.False(scope.IsOpen);
    }

    private sealed class FakeNavHost : INavHost
    {
        internal NavSurface? Surface;
        internal readonly Dictionary<UiHandle, ScreenRect> Rects = new();
        internal readonly List<string> Calls = new();

        public NavSurface? Active() => Surface;

        public ScreenRect Rect(NavTarget target) =>
            target.Handle != null && Rects.TryGetValue(target.Handle, out var r) ? r : ScreenRect.Unknown;

        public void Mark(NavTarget target, bool on) => Calls.Add((on ? "mark:" : "unmark:") + target.Handle.Text);

        public void Reveal(NavTarget target) => Calls.Add("reveal:" + target.Handle.Text);

        public bool FocusText(UiHandle handle)
        {
            Calls.Add("text:" + handle.Text);
            return true;
        }

        public void Click(UiHandle handle) => Calls.Add("click:" + handle.Text);

        public void Escape() => Calls.Add("escape");

        public void BlurText() => Calls.Add("blur");

        public void Scroll(NavTarget target, float amount) => Calls.Add("scroll");
    }
}
