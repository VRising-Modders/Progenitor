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
