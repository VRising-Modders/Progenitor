using System;
using System.Linq;
using Progenitor.Ui;
using Xunit;
using UiApi = Progenitor.Ui.Ui;

namespace Progenitor.Tests;

/// <summary>
/// PICKFRONT1: the canvas order of the shown panels follows the order they were shown, so a picker, popup or dialog
/// opened from a window draws over it, whichever band either is in, and focus or a repeated show never re-raises the
/// window over it. Game-free: the order is computed by <see cref="UiWindowLayout.DrawOrders"/> and handed to the runtime
/// in each panel's spot.
/// </summary>
[Collection("PluginLogger")]
public sealed class PanelDrawOrderTests : IDisposable
{
    private readonly UiTests.FakeUiRuntime _runtime = new();
    private readonly object _owner = new();

    public PanelDrawOrderTests()
    {
        UiApi.ResetForTests();
        UiApi.SetRuntimeForTests(_runtime);
    }

    public void Dispose()
    {
        UiDialog.ForgetForTests();
        UiApi.ResetForTests();
    }

    [Fact]
    public void A_panel_shown_later_draws_over_every_panel_shown_before_it()
    {
        // The live case: the list window and its picker are both wide game windows, out of the window row; both kept the
        // overlay's default order and the picker (shown second) drew under the list.
        var orders = UiWindowLayout.DrawOrders(
            new[] { UiDrawBand.Floating, UiDrawBand.Floating },
            new long[] { 34, 35 });
        Assert.Equal(UiOwnership.OverlaySorting, orders[0]);
        Assert.True(orders[1] > orders[0]);

        // A window first, then an ordinary panel it opened: the panel goes over the window.
        orders = UiWindowLayout.DrawOrders(new[] { UiDrawBand.Window, UiDrawBand.Floating }, new long[] { 1, 2 });
        Assert.Equal(UiWindowLayout.WindowSorting, orders[0]);
        Assert.True(orders[1] > orders[0]);

        // Created in one order, shown in the other: the show order decides.
        orders = UiWindowLayout.DrawOrders(new[] { UiDrawBand.Floating, UiDrawBand.Window }, new long[] { 9, 3 });
        Assert.True(orders[0] > orders[1]);

        // Workspace panes keep their own order; a dialog stacks over a dialog and over every window.
        orders = UiWindowLayout.DrawOrders(
            new[] { UiDrawBand.None, UiDrawBand.Window, UiDrawBand.Dialog, UiDrawBand.Dialog },
            new long[] { 1, 2, 3, 4 });
        Assert.Equal(0, orders[0]);
        Assert.Equal(UiWindowLayout.DialogSorting, orders[2]);
        Assert.True(orders[3] > orders[2]);
        Assert.True(orders[2] > orders[1]);
    }

    [Fact]
    public void Orders_stay_in_their_bands_and_off_the_dim_however_many_panels_are_open()
    {
        var bands = Enumerable.Range(0, 40).Select(i => i % 5 == 4 ? UiDrawBand.Dialog : UiDrawBand.Floating).ToArray();
        var orders = UiWindowLayout.DrawOrders(bands, Enumerable.Range(0, 40).Select(i => (long)i).ToArray());
        int lastFloating = int.MinValue, lastDialog = int.MinValue;
        for (int i = 0; i < bands.Length; i++)
        {
            int o = orders[i];
            Assert.NotEqual(UiWindowLayout.ShadeSorting, o);
            if (bands[i] == UiDrawBand.Dialog)
            {
                Assert.InRange(o, UiWindowLayout.DialogSorting, UiWindowLayout.DialogSortingMax);
                Assert.True(o >= lastDialog);
                lastDialog = o;
            }
            else
            {
                Assert.InRange(o, UiOwnership.OverlaySorting, UiWindowLayout.WindowSortingMax);
                Assert.True(o >= lastFloating);
                lastFloating = o;
            }
        }
        // Dialogs stay under the notice band; windows under every dialog.
        Assert.True(UiWindowLayout.DialogSortingMax < UiOwnership.OverlaySorting + 20);
        Assert.True(UiWindowLayout.WindowSortingMax < UiWindowLayout.DialogSorting);
    }

    [Fact]
    public void A_picker_opened_from_a_game_window_draws_over_it_and_stays_there()
    {
        var list = UiApi.Panel("Pickup filter", _owner, "auto").GameWindow(720);
        var picker = UiApi.Panel("Add to Pickup filter", _owner, "auto").GameWindow(900);
        list.Show();
        picker.Show();
        int listOrder = _runtime.SpotFor("Pickup filter").Sorting;
        int pickerOrder = _runtime.SpotFor("Add to Pickup filter").Sorting;
        Assert.True(listOrder > 0);
        Assert.True(pickerOrder > listOrder);

        // Showing the list again (its Edit button pressed again, a rebuild of the page) is not a new show: the picker
        // stays in front while it is open.
        list.Show();
        UiApi.ReflowPanels();
        Assert.True(_runtime.SpotFor("Add to Pickup filter").Sorting > _runtime.SpotFor("Pickup filter").Sorting);

        // Closed and opened again: in front again.
        picker.Hide();
        picker.Show();
        Assert.True(_runtime.SpotFor("Add to Pickup filter").Sorting > _runtime.SpotFor("Pickup filter").Sorting);
    }

    [Fact]
    public void A_panel_opened_from_a_standard_window_draws_over_it()
    {
        var window = UiApi.Window(UiWindowPreset.Header, "Iron Chest", _owner, "example.mod", "Example Mod", "1.2.3", null);
        window.Show();
        var popup = UiApi.Panel("Choose a color", _owner, "example.mod").GameWindow(900);
        popup.Show();
        Assert.True(_runtime.SpotFor("Choose a color").Sorting > _runtime.SpotFor("Iron Chest").Sorting);
        Assert.True(_runtime.SpotFor("Iron Chest").Sorting >= UiWindowLayout.WindowSorting);
    }

    [Fact]
    public void A_docked_standard_window_stays_above_the_dim_it_turns_on()
    {
        // Review: a docked window (WindowWidth > 0) counted as an ordinary panel; shown first it got 5000, under the dim.
        var dock = new UiDockSpec(UiDock.Left, UiDockAlign.Center, 16f, 640f, 0f);
        var spots = UiWindowLayout.Spots(new[] { "docked" }, _ => 640f, _ => false, _ => false, _ => 1L, _ => dock);
        Assert.Equal(UiWindowLayout.WindowSorting, spots.Single().Spot.Sorting);
        Assert.True(spots.Single().Spot.Sorting > UiWindowLayout.ShadeSorting);

        // The handle's band: a standard or game window is a window, docked or not.
        var window = UiApi.Window(UiWindowPreset.Header, "Lone Chest", _owner, "example.mod", "Example Mod", "1.2.3", null);
        window.Show();
        Assert.Equal(UiWindowLayout.WindowSorting, _runtime.SpotFor("Lone Chest").Sorting);
        var game = (UiPanelHandle)UiApi.Panel("Game", _owner, "example.mod").GameWindow();
        Assert.Equal(UiDrawBand.Window, UiApi.DrawBandOf(game));
    }

    [Fact]
    public void A_passive_hud_panel_is_not_raised_over_a_window_however_late_it_is_shown()
    {
        var window = UiApi.Window(UiWindowPreset.Header, "Iron Chest", _owner, "example.mod", "Example Mod", "1.2.3", null);
        window.Show();
        var hud = UiApi.Panel("Tracker", _owner, "example.mod");
        hud.CaptureMode = UiCaptureMode.Never;
        hud.Show();
        Assert.Equal(UiOwnership.OverlaySorting, _runtime.SpotFor("Tracker").Sorting);
        Assert.True(_runtime.SpotFor("Iron Chest").Sorting > _runtime.SpotFor("Tracker").Sorting);
        // Hidden and shown again (a HUD that comes back): still under the window and the dim, never taking its clicks.
        hud.Hide();
        hud.Show();
        Assert.Equal(UiOwnership.OverlaySorting, _runtime.SpotFor("Tracker").Sorting);
        Assert.True(UiOwnership.OverlaySorting < UiWindowLayout.ShadeSorting);

        // An input-taking panel shown after the window is raised over it.
        var popup = UiApi.Panel("Popup", _owner, "example.mod");
        popup.CaptureMode = UiCaptureMode.WhileShown;
        popup.Show();
        Assert.True(_runtime.SpotFor("Popup").Sorting > _runtime.SpotFor("Iron Chest").Sorting);

        // Pure: passive panels take no rank, so they never push the windows shown after them up the band.
        var orders = UiWindowLayout.DrawOrders(
            new[] { UiDrawBand.Window, UiDrawBand.Passive, UiDrawBand.Passive, UiDrawBand.Window },
            new long[] { 1, 2, 3, 4 });
        Assert.Equal(new[] { UiWindowLayout.WindowSorting, UiOwnership.OverlaySorting, UiOwnership.OverlaySorting, UiWindowLayout.WindowSorting + 1 }, orders);
    }

    [Fact]
    public void Past_the_band_the_newest_panel_keeps_the_top_order_and_the_older_ones_are_pressed_under_it()
    {
        int n = 20;
        var orders = UiWindowLayout.DrawOrders(Enumerable.Repeat(UiDrawBand.Window, n).ToArray(),
            Enumerable.Range(0, n).Select(i => (long)i).ToArray());
        Assert.Equal(UiWindowLayout.WindowSortingMax, orders[n - 1]);
        // The newest 14 each have their own order, strictly increasing to the top; the oldest share the floor.
        int slots = UiWindowLayout.WindowSortingMax - UiWindowLayout.WindowSorting + 1;
        for (int k = n - slots + 1; k < n; k++) Assert.True(orders[k] > orders[k - 1]);
        for (int k = 0; k <= n - slots; k++) Assert.Equal(UiWindowLayout.WindowSorting, orders[k]);

        // Shown in another order than created: the one shown last is on top.
        var seq = Enumerable.Range(0, n).Select(i => (long)(i == 3 ? 100 : i)).ToArray();
        orders = UiWindowLayout.DrawOrders(Enumerable.Repeat(UiDrawBand.Window, n).ToArray(), seq);
        Assert.Equal(UiWindowLayout.WindowSortingMax, orders[3]);
        Assert.Single(orders, o => o == UiWindowLayout.WindowSortingMax);

        // Dialogs: 4 orders; the newest of 6 is on top alone.
        orders = UiWindowLayout.DrawOrders(Enumerable.Repeat(UiDrawBand.Dialog, 6).ToArray(), new long[] { 1, 2, 3, 4, 5, 6 });
        Assert.Equal(UiWindowLayout.DialogSortingMax, orders[5]);
        Assert.Equal(UiWindowLayout.DialogSortingMax - 1, orders[4]);
        Assert.Equal(UiWindowLayout.DialogSorting, orders[0]);

        // An ordinary input panel pressed down past the windows' floor drops to the overlay order, never onto the dim.
        orders = UiWindowLayout.DrawOrders(
            new[] { UiDrawBand.Floating }.Concat(Enumerable.Repeat(UiDrawBand.Window, 14)).ToArray(),
            Enumerable.Range(0, 15).Select(i => (long)i).ToArray());
        Assert.Equal(UiOwnership.OverlaySorting, orders[0]);
        Assert.DoesNotContain(UiWindowLayout.ShadeSorting, orders);
    }

    [Fact]
    public void The_runtime_takes_the_order_from_the_spot_and_nothing_else_raises_a_panel()
    {
        string windows = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "UnityUiRuntime.Windows.cs");
        Assert.Contains("SetOverlaySorting(Spot.Sorting);", windows);
        string ui = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "Ui.cs");
        Assert.Contains("p => p.EffectiveDock, DrawBandOf))", ui);
        // The show sequence (the order's only input) moves only on a hidden-to-shown change.
        string handles = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "UiHandles.cs");
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(handles, @"ShownSequence = Interlocked\.Increment"));
    }
}
