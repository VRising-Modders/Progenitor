using System;
using Progenitor.Ui;
using Xunit;
using UiApi = Progenitor.Ui.Ui;

namespace Progenitor.Tests;

/// <summary>
/// Jobs FUI20 and FUI21, the half <see cref="PanelFlowTests"/> cannot cover: the placement driven
/// through the real <see cref="Ui"/> handles rather than through the arithmetic directly, so the
/// lifetime — claimed at Show, held while shown, given back at Hide, at Dispose and at Uninstall —
/// and the re-flow of the whole set are covered and not only the numbers. The Unity side is behind
/// <see cref="IUiRuntime"/> and faked.
/// </summary>
[Collection("PluginLogger")]
public class PanelPlacementTests : IDisposable
{
    private const string PluginId = "example.placement.test";

    private readonly TestLogSource _log = new();
    private readonly UiTests.FakeUiRuntime _client = new() { IsClient = true };
    private readonly object _owner = new();

    public PanelPlacementTests()
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

    private IUiPanel Panel(string title) => UiApi.Panel(title, _owner, PluginId);

    /// <summary>
    /// The defect. Two panels shown at once used to be handed the same placement, because there
    /// was no placement — every panel went to <c>Vector2.zero</c>. They are now the two members of
    /// a set of two, and the runtime is told so for both of them.
    /// </summary>
    [Fact]
    public void Two_panels_shown_together_are_placed_as_a_set_of_two()
    {
        var demo = Panel("Demo");
        var log = Panel("Log");

        UiApi.Show(demo);
        UiApi.Show(log);

        Assert.Equal(0, _client.SpotFor("Demo").Rank);
        Assert.Equal(2, _client.SpotFor("Demo").Count);
        Assert.Equal(1, _client.SpotFor("Log").Rank);
        Assert.Equal(2, _client.SpotFor("Log").Count);
        Assert.Equal(2, UiPanelSlots.Count);

        // And they land in different columns, which is the thing the player sees.
        Assert.True(_client.OffsetFor("Demo").X < _client.OffsetFor("Log").X);
    }

    /// <summary>
    /// Job FUI21's re-flow: the panel that was already open was placed for a set of one and has to
    /// be told there are two now, because where the first panel goes is a fact about how many are
    /// open. Without this the new panel would move into its column and the old one would sit in
    /// the center, straddling it.
    /// </summary>
    [Fact]
    public void Opening_a_second_panel_moves_the_first_out_of_the_centre()
    {
        var first = Panel("First");
        UiApi.Show(first);
        Assert.True(_client.OffsetFor("First").IsCentre);

        UiApi.Show(Panel("Second"));

        Assert.False(_client.OffsetFor("First").IsCentre);
        Assert.Equal(2, _client.SpotFor("First").Count);
    }

    /// <summary>A single panel is centered, which is what every screenshot so far shows.</summary>
    [Fact]
    public void One_panel_holds_the_centre()
    {
        var panel = Panel("Only");
        UiApi.Show(panel);

        Assert.Equal(0, _client.SpotFor("Only").Rank);
        Assert.Equal(1, _client.SpotFor("Only").Count);
        Assert.True(_client.OffsetFor("Only").IsCentre);
    }

    /// <summary>
    /// Centered means "the only panel on screen", not "the first panel ever opened". Closing
    /// everything and opening something else must put that back in the middle — a monotonic
    /// counter would leave a lone panel sitting off-center with nothing to be off-center from.
    /// </summary>
    [Fact]
    public void The_next_panel_opened_alone_is_centred_again()
    {
        var first = Panel("First");
        var second = Panel("Second");

        UiApi.Show(first);
        UiApi.Show(second);
        Assert.Equal(1, _client.SpotFor("Second").Rank);

        first.Hide();
        second.Hide();
        Assert.Equal(0, UiPanelSlots.Count);

        var third = Panel("Third");
        UiApi.Show(third);
        Assert.True(_client.OffsetFor("Third").IsCentre);
    }

    /// <summary>
    /// The deliberate reversal of FUI20's "closing the panel above does not move the one below".
    /// With panels tiled, a survivor left in its old column would sit off-center beside an empty
    /// column — the same "nothing to be off-center from" FUI20 rejected for monotonic slots. The
    /// layout is a function of what is on screen, so what is left is laid out for how many are
    /// left. It is safe here and not under the pointer because only opening and closing re-flows:
    /// a re-layout the player causes by <i>using</i> a panel recomputes the same set.
    /// </summary>
    [Fact]
    public void Closing_one_of_two_panels_re_centres_the_survivor()
    {
        var a = Panel("A");
        var b = Panel("B");
        UiApi.Show(a);
        UiApi.Show(b);
        Assert.False(_client.OffsetFor("B").IsCentre);

        a.Hide();

        Assert.Equal(0, _client.SpotFor("B").Rank);
        Assert.Equal(1, _client.SpotFor("B").Count);
        Assert.True(_client.OffsetFor("B").IsCentre);
        Assert.Equal(1, UiPanelSlots.Count);
    }

    /// <summary>
    /// The longest-open panel keeps the lowest slot and therefore the leftmost column, so a panel
    /// opening and closing beside it never makes it swap sides. That is the whole reason the slot
    /// allocator survived job FUI21: the position comes from the set, the order comes from here.
    /// </summary>
    [Fact]
    public void A_panel_that_stays_open_does_not_swap_columns()
    {
        var kept = Panel("Kept");
        UiApi.Show(kept);

        for (int i = 0; i < 3; i++)
        {
            var transient = Panel("Transient" + i);
            UiApi.Show(transient);

            Assert.Equal(0, _client.SpotFor("Kept").Rank);
            Assert.True(_client.OffsetFor("Kept").X < _client.OffsetFor("Transient" + i).X);

            transient.Hide();
            Assert.True(_client.OffsetFor("Kept").IsCentre);
        }
    }

    /// <summary>Hiding gives the slot back, and the runtime is told the panel is out of the set.</summary>
    [Fact]
    public void Hiding_returns_the_slot()
    {
        var panel = Panel("A");
        UiApi.Show(panel);
        Assert.Equal(1, UiPanelSlots.Count);

        panel.Hide();

        Assert.Equal(0, UiPanelSlots.Count);
        Assert.False(_client.SpotFor("A").IsPlaced);
    }

    /// <summary>
    /// Disposed while shown goes through <c>HideCore</c>, which is the path that exists because
    /// <c>Dispose</c> flips <c>IsDisposed</c> before it hides (FUI2). A slot leaked there would
    /// count a panel that is not on screen into every later layout for the rest of the session.
    /// </summary>
    [Fact]
    public void Disposing_a_shown_panel_returns_its_slot()
    {
        var panel = Panel("A");
        UiApi.Show(panel);
        Assert.Equal(1, UiPanelSlots.Count);

        panel.Dispose();

        Assert.Equal(0, UiPanelSlots.Count);
    }

    /// <summary>
    /// A disposed panel is out of the set at once: the panel still open must be re-flowed for the
    /// set it is now in rather than left in a column beside a frame that no longer exists.
    /// </summary>
    [Fact]
    public void Disposing_one_of_two_panels_re_flows_the_other()
    {
        var a = Panel("A");
        var b = Panel("B");
        UiApi.Show(a);
        UiApi.Show(b);
        Assert.False(_client.OffsetFor("B").IsCentre);

        a.Dispose();

        Assert.True(_client.OffsetFor("B").IsCentre);
    }

    /// <summary>Showing the same panel twice takes one slot, matching the shown refcount.</summary>
    [Fact]
    public void Showing_twice_takes_one_slot()
    {
        var panel = Panel("A");

        UiApi.Show(panel);
        UiApi.Show(panel);

        Assert.Equal(1, UiPanelSlots.Count);
        Assert.Equal(0, _client.SpotFor("A").Rank);
        Assert.Equal(1, _client.SpotFor("A").Count);

        panel.Hide();
        Assert.Equal(0, UiPanelSlots.Count);
    }

    /// <summary>
    /// A panel that never becomes shown never holds a slot: a disabled handle (the server path, a
    /// runtime that could not build the frame) must not silently consume a column.
    /// </summary>
    [Fact]
    public void A_disabled_handle_takes_no_slot()
    {
        UiApi.SetRuntimeForTests(new UiTests.FakeUiRuntime { IsClient = false });
        var panel = UiApi.Panel("Server", _owner, PluginId);

        UiApi.Show(panel);

        Assert.Equal(0, UiPanelSlots.Count);
    }

    /// <summary>
    /// A Core swap must not carry slots into the next generation: the handles that held them are
    /// gone, so the next generation's first panel would be laid out as one of a crowd that is not
    /// on screen.
    /// </summary>
    [Fact]
    public void Uninstall_forgets_every_slot()
    {
        UiApi.Show(Panel("A"));
        UiApi.Show(Panel("B"));
        Assert.Equal(2, UiPanelSlots.Count);

        UiApi.Uninstall();

        Assert.Equal(0, UiPanelSlots.Count);
    }

    /// <summary>
    /// Slots survive a full open/close churn without drifting. The symptom of a leak here is
    /// gradual and easy to miss live — a lone panel drifting further off-center over a session —
    /// so it is pinned rather than left to a screenshot.
    /// </summary>
    [Fact]
    public void Opening_and_closing_the_same_panel_does_not_drift()
    {
        var panel = Panel("A");

        for (int i = 0; i < 100; i++)
        {
            UiApi.Show(panel);
            Assert.True(_client.OffsetFor("A").IsCentre);
            panel.Hide();
        }

        Assert.Equal(0, UiPanelSlots.Count);
    }

    /// <summary>The pin diagnostic reports held slots, so a leak is greppable in a live log.</summary>
    [Fact]
    public void Held_slots_show_up_in_the_pin_line()
    {
        var panel = Panel("A");
        UiApi.Show(panel);

        Assert.Contains("panelSlots=1", UiPanelSlots.Describe(), StringComparison.Ordinal);

        panel.Hide();
        Assert.Contains("panelSlots=0", UiPanelSlots.Describe(), StringComparison.Ordinal);
    }
}
