using System;
using System.Collections.Generic;
using System.IO;
using BepInEx.Configuration;
using Progenitor.Ui;
using Xunit;
using UiApi = Progenitor.Ui.Ui;

namespace Progenitor.Tests;

/// <summary>
/// Job FUI19. The routing decision: which of the shown panels wants gameplay input blocked, given
/// where they are and where the pointer is, and when that answer changes.
///
/// <para>
/// The behavior these exist for: an open mod panel used to take the whole screen hostage. Showing
/// a 640-unit frame in one corner blocked the wheel, the click and the movement keys everywhere,
/// because "capture" meant both "unlock the cursor" and "block gameplay input" and both lasted for
/// as long as the panel was shown. Only the cursor half has to. So the arithmetic that decides the
/// other half is game-free and lives here, the way <see cref="KeyEdgeTracker"/> and
/// <see cref="PanelLayout"/> do — the game is only ever asked "where is this panel" and "where is
/// the mouse".
/// </para>
/// </summary>
public class HoverRouterTests
{
    private static readonly ScreenRect Left = ScreenRect.FromCorners(0f, 0f, 200f, 200f);
    private static readonly ScreenRect Right = ScreenRect.FromCorners(100f, 100f, 300f, 300f);

    private static HoverPanel Hover(ScreenRect rect) => new(UiCaptureMode.WhenHovered, rect);

    private static HoverPanel Modal() => new(UiCaptureMode.WhileShown, ScreenRect.Unknown);

    private static HoverPanel Never(ScreenRect rect) => new(UiCaptureMode.Never, rect);

    private static List<HoverPanel> Set(params HoverPanel[] panels) => new(panels);

    // ---- the rectangle -----------------------------------------------------------------------

    /// <summary>
    /// The hit test is exact for a rectangle and includes both edges, so a pointer parked on the
    /// frame's border gives the same answer every frame instead of oscillating across it.
    /// </summary>
    [Theory]
    [InlineData(100f, 100f, true)]
    [InlineData(0f, 0f, true)]
    [InlineData(200f, 200f, true)]
    [InlineData(0f, 200f, true)]
    [InlineData(-0.01f, 100f, false)]
    [InlineData(200.01f, 100f, false)]
    [InlineData(100f, -0.01f, false)]
    [InlineData(100f, 200.01f, false)]
    public void The_rect_test_is_exact_and_includes_its_edges(float x, float y, bool inside) =>
        Assert.Equal(inside, Left.Contains(x, y));

    /// <summary>Corners in either order describe the same rectangle.</summary>
    [Fact]
    public void Corners_are_normalised_whichever_way_round_they_come()
    {
        var forward = ScreenRect.FromCorners(10f, 20f, 110f, 220f);
        var reversed = ScreenRect.FromCorners(110f, 220f, 10f, 20f);

        Assert.Equal(forward.MinX, reversed.MinX);
        Assert.Equal(forward.MinY, reversed.MinY);
        Assert.Equal(forward.MaxX, reversed.MaxX);
        Assert.Equal(forward.MaxY, reversed.MaxY);
        Assert.True(reversed.Contains(50f, 100f));
    }

    /// <summary>
    /// A transform read mid-teardown yields NaN before it yields an exception. That must become the
    /// explicit "unreadable" rect, not a rectangle that quietly contains everything or nothing.
    /// </summary>
    [Theory]
    [InlineData(float.NaN, 0f, 10f, 10f)]
    [InlineData(0f, float.NaN, 10f, 10f)]
    [InlineData(0f, 0f, float.PositiveInfinity, 10f)]
    [InlineData(0f, 0f, 10f, float.NegativeInfinity)]
    public void A_corner_that_is_not_a_number_makes_the_rect_unknown(float ax, float ay, float bx, float by)
    {
        var rect = ScreenRect.FromCorners(ax, ay, bx, by);

        Assert.False(rect.IsKnown);
        Assert.False(rect.Contains(0f, 0f));
    }

    /// <summary>The unknown rect contains nothing, including its own zeroed corner.</summary>
    [Fact]
    public void The_unknown_rect_contains_nothing()
    {
        Assert.False(ScreenRect.Unknown.IsKnown);
        Assert.False(ScreenRect.Unknown.Contains(0f, 0f));
    }

    /// <summary>A pointer read that produced a NaN is not a position; it is an unreadable pointer.</summary>
    [Theory]
    [InlineData(float.NaN, 0f)]
    [InlineData(0f, float.NaN)]
    [InlineData(float.PositiveInfinity, 0f)]
    public void A_pointer_that_is_not_a_number_is_unreadable(float x, float y) =>
        Assert.Equal(PointerKind.Unreadable, PointerSample.At(x, y).Kind);

    // ---- the decision ------------------------------------------------------------------------

    /// <summary>Nothing shown, nothing blocked — however the pointer reads.</summary>
    [Fact]
    public void No_panel_never_blocks()
    {
        Assert.False(HoverRouter.ShouldBlock(Set(), PointerSample.At(1f, 1f)));
        Assert.False(HoverRouter.ShouldBlock(Set(), PointerSample.Unreadable));
        Assert.False(HoverRouter.ShouldBlock(null, PointerSample.Unreadable));
    }

    /// <summary>The whole point: over the panel the block is on, off it the game gets the input.</summary>
    [Fact]
    public void A_hovered_panel_blocks_and_the_same_panel_unhovered_does_not()
    {
        Assert.True(HoverRouter.ShouldBlock(Set(Hover(Left)), PointerSample.At(100f, 100f)));
        Assert.False(HoverRouter.ShouldBlock(Set(Hover(Left)), PointerSample.At(600f, 400f)));
    }

    /// <summary>
    /// A modal panel is the old behavior, kept: it blocks wherever the pointer is, and without
    /// needing a rect or a readable pointer at all.
    /// </summary>
    [Fact]
    public void A_WhileShown_panel_blocks_wherever_the_pointer_is()
    {
        Assert.True(HoverRouter.ShouldBlock(Set(Modal()), PointerSample.At(600f, 400f)));
        Assert.True(HoverRouter.ShouldBlock(Set(Modal()), PointerSample.Outside));
        Assert.True(HoverRouter.ShouldBlock(Set(Modal()), PointerSample.Unreadable));
    }

    /// <summary>
    /// The pointer left the game window — a second monitor, an alt-tab, dragged off the edge. It is
    /// definitely not over a panel, so hover panels stop blocking and the player gets their game
    /// back without having to close anything.
    /// </summary>
    [Fact]
    public void A_pointer_outside_the_window_is_over_no_panel()
    {
        Assert.False(HoverRouter.ShouldBlock(Set(Hover(Left)), PointerSample.Outside));
        Assert.False(HoverRouter.ShouldBlock(Set(Hover(Left), Hover(Right)), PointerSample.Outside));
    }

    /// <summary>
    /// Fail closed, direction one. No pointer at all is not the same fact as a pointer that is
    /// elsewhere: it means we cannot route, so the panel keeps blocking exactly as it did before
    /// this feature existed. A panel the player can see and cannot scroll is the worse outcome.
    /// </summary>
    [Fact]
    public void An_unreadable_pointer_makes_a_hover_panel_block()
    {
        Assert.True(HoverRouter.ShouldBlock(Set(Hover(Left)), PointerSample.Unreadable));
    }

    /// <summary>Fail closed, direction two: a panel whose rect could not be read blocks.</summary>
    [Fact]
    public void An_unreadable_rect_makes_a_hover_panel_block()
    {
        Assert.True(HoverRouter.ShouldBlock(Set(Hover(ScreenRect.Unknown)), PointerSample.At(600f, 400f)));
    }

    /// <summary>
    /// ...but "cannot route" only matters to a panel that asked to be routed. A set of HUD overlays
    /// with no pointer must stay out of the way, not fail closed into blocking the game.
    /// </summary>
    [Fact]
    public void Never_panels_with_no_readable_pointer_still_do_not_block()
    {
        Assert.False(HoverRouter.ShouldBlock(Set(Never(Left), Never(Right)), PointerSample.Unreadable));
        Assert.False(HoverRouter.ShouldBlock(Set(Never(ScreenRect.Unknown)), PointerSample.Unreadable));
    }

    /// <summary>A Never panel under the pointer is a HUD overlay: it is read, not interacted with.</summary>
    [Fact]
    public void A_Never_panel_under_the_pointer_does_not_block()
    {
        Assert.False(HoverRouter.ShouldBlock(Set(Never(Left)), PointerSample.At(100f, 100f)));
    }

    /// <summary>
    /// Overlap is an OR, and Never shields nothing. A HUD overlay stretched over the screen must
    /// not make every other panel unclickable — "Never" means "this panel never asks for the
    /// block", not "this panel suppresses everyone else's". Each panel has its own overlay canvas
    /// (R24 finding 3), so there is no cheap honest z-order to consult instead.
    /// </summary>
    [Fact]
    public void A_Never_panel_does_not_shield_a_hovered_panel_under_it()
    {
        var stacked = Set(Never(ScreenRect.FromCorners(0f, 0f, 1920f, 1080f)), Hover(Left));

        Assert.True(HoverRouter.ShouldBlock(stacked, PointerSample.At(100f, 100f)));
        Assert.False(HoverRouter.ShouldBlock(stacked, PointerSample.At(600f, 400f)));
    }

    /// <summary>Two overlapping panels: hovering either blocks, hovering neither does not.</summary>
    [Theory]
    [InlineData(50f, 50f, true)]     // only Left
    [InlineData(150f, 150f, true)]   // the overlap
    [InlineData(250f, 250f, true)]   // only Right
    [InlineData(400f, 400f, false)]  // neither
    public void Overlapping_panels_block_if_the_pointer_is_over_any_of_them(float x, float y, bool blocked) =>
        Assert.Equal(blocked, HoverRouter.ShouldBlock(Set(Hover(Left), Hover(Right)), PointerSample.At(x, y)));

    /// <summary>One modal panel in the set decides for all of them, wherever the pointer is.</summary>
    [Fact]
    public void One_modal_panel_among_hover_panels_blocks_everywhere()
    {
        Assert.True(HoverRouter.ShouldBlock(Set(Hover(Left), Modal()), PointerSample.At(999f, 999f)));
    }

    // ---- the transition ----------------------------------------------------------------------

    /// <summary>
    /// The router starts blocked, so a process that never gets a readable pointer — a server, a
    /// test, the frames before the mouse device exists — behaves exactly as it did before FUI19.
    /// </summary>
    [Fact]
    public void The_router_starts_blocked_so_a_process_with_no_pointer_keeps_the_old_behaviour()
    {
        Assert.True(new HoverRouter().Blocked);
    }

    /// <summary>
    /// The debounce. Below the router sits a refcount that drives a real
    /// <c>AddInputContext</c> / <c>RemoveInputContext</c> against the game's own ordered list, with
    /// a deferral rule behind it, so only an edge crossing may reach it — never a frame. Sixty
    /// identical frames either side of one crossing must produce exactly one change.
    /// </summary>
    [Fact]
    public void Only_crossing_the_edge_is_a_change_never_a_frame()
    {
        var router = new HoverRouter();
        var panels = Set(Hover(Left));
        int changes = 0;

        for (int i = 0; i < 60; i++)
        {
            if (router.Update(panels, PointerSample.At(600f, 400f), out _)) changes++;
        }

        Assert.Equal(1, changes); // the initial fail-closed true → false
        Assert.False(router.Blocked);

        for (int i = 0; i < 60; i++)
        {
            if (router.Update(panels, PointerSample.At(100f, 100f), out _)) changes++;
        }

        Assert.Equal(2, changes);
        Assert.True(router.Blocked);

        for (int i = 0; i < 60; i++)
        {
            if (router.Update(panels, PointerSample.At(600f, 400f), out _)) changes++;
        }

        Assert.Equal(3, changes);
        Assert.Equal(3, router.Transitions);
    }

    /// <summary>
    /// A pointer sitting exactly on the frame's border is the case a half-open rule would make
    /// oscillate. It reads the same every frame here, so it costs one transition and never more.
    /// </summary>
    [Fact]
    public void A_pointer_parked_on_the_border_does_not_oscillate()
    {
        var router = new HoverRouter();
        var panels = Set(Hover(Left));

        for (int i = 0; i < 100; i++)
            router.Update(panels, PointerSample.At(200f, 200f), out _);

        Assert.True(router.Blocked);
        Assert.Equal(0, router.Transitions); // it starts blocked and stays blocked
    }

    /// <summary>The out value is always the current answer, change or no change.</summary>
    [Fact]
    public void The_out_value_is_the_current_answer_even_when_nothing_changed()
    {
        var router = new HoverRouter();
        var panels = Set(Hover(Left));

        Assert.True(router.Update(panels, PointerSample.At(600f, 400f), out bool first));
        Assert.False(first);
        Assert.False(router.Update(panels, PointerSample.At(601f, 401f), out bool second));
        Assert.False(second);
    }

    /// <summary>
    /// The panel closing while the pointer is over it is a change like any other: the shown set is
    /// what the router reads, so an empty set releases the block on the very next pass.
    /// </summary>
    [Fact]
    public void A_panel_that_stops_being_shown_while_hovered_releases_the_block()
    {
        var router = new HoverRouter();

        router.Update(Set(Hover(Left)), PointerSample.At(100f, 100f), out bool held);
        Assert.True(held);

        Assert.True(router.Update(Set(), PointerSample.At(100f, 100f), out bool released));
        Assert.False(released);
    }

    /// <summary>Reset puts it back to the fail-closed default, for a teardown or a Core swap.</summary>
    [Fact]
    public void Reset_returns_to_the_fail_closed_default()
    {
        var router = new HoverRouter();
        router.Update(Set(Hover(Left)), PointerSample.At(600f, 400f), out _);
        Assert.False(router.Blocked);

        router.Reset();

        Assert.True(router.Blocked);
        Assert.Equal(0, router.Transitions);
    }

    // ---- the config entry --------------------------------------------------------------------

    /// <summary>The framework default is the pointer-routed one; that is the whole request.</summary>
    [Fact]
    public void The_framework_default_is_WhenHovered()
    {
        Assert.Equal(UiCaptureMode.WhenHovered, UiCaptureModes.Default);
        Assert.Equal("WhenHovered", UiCaptureModes.DefaultName);
    }

    /// <summary>
    /// The entry is client-owned and its section says so. A synced entry would make a server the
    /// owner of how a player's own mouse behaves, which is not a thing a server may decide.
    /// </summary>
    [Fact]
    public void The_config_entry_is_a_client_Ui_entry_and_names_its_three_values()
    {
        Assert.Equal("Ui", UiCaptureModes.ConfigSection);
        Assert.Equal("CaptureMode", UiCaptureModes.ConfigKey);
        Assert.Contains("client setting", UiCaptureModes.ConfigDescription, StringComparison.Ordinal);
        Assert.Contains("WhenHovered", UiCaptureModes.ConfigDescription, StringComparison.Ordinal);
        Assert.Contains("WhileShown", UiCaptureModes.ConfigDescription, StringComparison.Ordinal);
        Assert.Contains("Never", UiCaptureModes.ConfigDescription, StringComparison.Ordinal);
    }

    /// <summary>Every mode's own name round-trips, whatever case or padding the player typed.</summary>
    [Theory]
    [InlineData("WhenHovered", UiCaptureMode.WhenHovered)]
    [InlineData("whenhovered", UiCaptureMode.WhenHovered)]
    [InlineData("  WhileShown  ", UiCaptureMode.WhileShown)]
    [InlineData("WHILESHOWN", UiCaptureMode.WhileShown)]
    [InlineData("never", UiCaptureMode.Never)]
    public void Every_mode_name_parses(string text, UiCaptureMode expected)
    {
        Assert.True(UiCaptureModes.TryParse(text, out var mode));
        Assert.Equal(expected, mode);
    }

    /// <summary>
    /// A value the enum does not name keeps the default and says so, rather than falling back
    /// silently — the caller logs which value it did not understand.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("true")]
    [InlineData("Hovered")]
    [InlineData("0")]
    public void An_unknown_value_keeps_the_default_and_reports_it(string? text)
    {
        Assert.False(UiCaptureModes.TryParse(text, out var mode));
        Assert.Equal(UiCaptureModes.Default, mode);
        Assert.Contains("WhenHovered", UiCaptureModes.BadValueLog(text), StringComparison.Ordinal);
    }

    /// <summary>Every declared mode's own <c>ToString</c> parses back to itself.</summary>
    [Fact]
    public void Every_declared_mode_round_trips_through_its_own_name()
    {
        foreach (UiCaptureMode declared in Enum.GetValues(typeof(UiCaptureMode)))
        {
            Assert.True(UiCaptureModes.TryParse(declared.ToString(), out var parsed));
            Assert.Equal(declared, parsed);
        }
    }
}

/// <summary>
/// Job FUI19, the other half: the same decision driven through the real <see cref="Ui"/> handles,
/// the shown-panel registry and the input-capture refcount, so the plumbing between them is
/// covered and not only the arithmetic. The game's context list is behind
/// <see cref="IUiInputBlocker"/> and faked here; the panel rects and the pointer are behind
/// <see cref="IUiRuntime"/> and faked by <see cref="UiTests.FakeUiRuntime"/>.
/// </summary>
[Collection("PluginLogger")]
public class HoverGateTests : IDisposable
{
    private const string PluginId = "example.hover.test";

    private readonly TestLogSource _log = new();
    private readonly UiTests.FakeUiRuntime _client = new() { IsClient = true };
    private readonly CountingBlocker _blocker = new();
    private readonly object _owner = new();

    public HoverGateTests()
    {
        Plugin.Logger = _log;
        UiApi.ResetForTests();
        UiApi.SetRuntimeForTests(_client);
        UiApi.SetInputBlockerForTests(_blocker);
    }

    public void Dispose()
    {
        UiApi.ResetForTests();
        Plugin.Logger = null;
    }

    private IUiPanel Open(string title, float minX, float minY, float maxX, float maxY)
    {
        var panel = UiApi.Panel(title, _owner, PluginId);
        _client.SetRect(title, minX, minY, maxX, maxY);
        return panel;
    }

    private void PointAt(float x, float y)
    {
        _client.Pointer = PointerSample.At(x, y);
        UiHoverGate.Pump();
    }

    /// <summary>
    /// Gameplay input belongs to the game right now (job FUI20). Not "no context is registered":
    /// since defect 1 the context stays in the game's list for as long as the panel is shown, so
    /// that Escape can close the panel from anywhere, and the pointer decides how much it masks
    /// rather than whether it is there. A released panel takes two actions - Back and Menu_Close -
    /// and nothing else.
    /// </summary>
    private void AssertGameplayReleased()
    {
        Assert.True(UiApi.IsCapturingInput);
        Assert.Equal(UiMaskScope.CloseKeyOnly, UiInputCapture.MaskScope);
        Assert.Equal(UiMaskScope.CloseKeyOnly, _blocker.Scope);
        Assert.False(InputMaskPlan.For(_blocker.Scope).BlockAllKeys);
    }

    /// <summary>Chat's whole recipe is applying: the pointer is over a panel.</summary>
    private void AssertGameplayBlocked()
    {
        Assert.True(UiApi.IsCapturingInput);
        Assert.Equal(UiMaskScope.Full, UiInputCapture.MaskScope);
        Assert.Equal(UiMaskScope.Full, _blocker.Scope);
        Assert.True(InputMaskPlan.For(_blocker.Scope).BlockAllKeys);
    }

    /// <summary>
    /// The FUI19 bug, end to end, as job FUI20 left it. A panel opened while the pointer is
    /// elsewhere takes no gameplay input — the wheel still zooms the camera and the click still
    /// swings — and the panel is nonetheless shown, with the cursor unlocked so the player can
    /// reach it. What changed is the mechanism: the context is now registered for as long as the
    /// panel is shown (that is what lets one Escape close the panel from anywhere without also
    /// opening the vanilla system menu) and its mask is scoped to the close key alone.
    /// </summary>
    [Fact]
    public void A_panel_opened_with_the_pointer_elsewhere_does_not_take_the_screen()
    {
        var panel = Open("A", 0f, 0f, 200f, 200f);
        _client.Pointer = PointerSample.At(600f, 400f);

        UiApi.Show(panel);

        Assert.Equal(UiHandleState.Shown, panel.State);
        AssertGameplayReleased();
        Assert.Equal(1, _blocker.Registers);
        // The cursor half is unconditional: it is what lets the player reach the panel at all.
        Assert.Equal(1, _client.Capture);
        Assert.False(_client.CursorLocked);
    }

    /// <summary>Crossing onto the panel takes the input; crossing off gives it straight back.</summary>
    [Fact]
    public void Crossing_the_panel_edge_takes_and_returns_the_input()
    {
        var panel = Open("A", 0f, 0f, 200f, 200f);
        _client.Pointer = PointerSample.At(600f, 400f);
        UiApi.Show(panel);

        PointAt(100f, 100f);

        AssertGameplayBlocked();
        Assert.Equal(1, _blocker.Registers);

        PointAt(600f, 400f);

        AssertGameplayReleased();
        // Job FUI20: crossing the edge is now a scope change and not a list mutation, so the
        // context is added once when the panel opens and removed once when it closes. Three scope
        // changes, not two: opening the panel with the pointer elsewhere is itself a release.
        Assert.Equal(1, _blocker.Registers);
        Assert.Equal(0, _blocker.Removes);
        Assert.Equal(3, _blocker.ScopeChanges);
    }

    /// <summary>
    /// The debounce, where it actually matters. Each pass here is a real decide/act/fold against
    /// the game's context list with a deferral rule behind it, so drifting the mouse around inside
    /// and then around outside the panel must cost exactly one register and one remove.
    /// </summary>
    [Fact]
    public void Moving_within_the_panel_does_not_touch_the_game_context_list_again()
    {
        var panel = Open("A", 0f, 0f, 200f, 200f);
        _client.Pointer = PointerSample.At(600f, 400f);
        UiApi.Show(panel);

        // One scope change already: the panel opened with the pointer elsewhere.
        Assert.Equal(1, _blocker.ScopeChanges);

        for (int i = 0; i < 50; i++) PointAt(10f + i, 10f + i);
        Assert.Equal(1, _blocker.Registers);
        Assert.Equal(0, _blocker.Removes);
        Assert.Equal(2, _blocker.ScopeChanges);

        for (int i = 0; i < 50; i++) PointAt(500f + i, 500f + i);
        Assert.Equal(1, _blocker.Registers);
        Assert.Equal(0, _blocker.Removes);
        Assert.Equal(3, _blocker.ScopeChanges);
    }

    /// <summary>A modal panel is the old behavior and needs no per-frame work at all.</summary>
    [Fact]
    public void A_WhileShown_panel_blocks_from_the_moment_it_is_shown_and_arms_no_pump()
    {
        var panel = Open("A", 0f, 0f, 200f, 200f);
        panel.CaptureMode = UiCaptureMode.WhileShown;
        _client.Pointer = PointerSample.At(600f, 400f);

        UiApi.Show(panel);

        Assert.True(UiApi.IsCapturingInput);
        Assert.Equal(1, _blocker.Registers);
        Assert.False(UiHoverGate.PumpArmedForTests);
        // Hover-gate pump stays off. Wheel and typing use a separate arm: any shown panel.
        Assert.True(UiShownInput.ShouldArm(2));
        Assert.False(UiShownInput.ShouldArm(0));
        string line = UiShownInput.Format(
            shown: 2, hoverPump: false, deltaY: -120f, notch: true, hit: "Mods", pos: 1f, nextPos: 0.973f,
            content: 2400f, view: 568f, applied: UiShownInput.AppliedPump, path: MenuPanelLayout.WheelMover,
            keys: 0, typed: '\0', focused: false, selected: null,
            click: false, activated: false);
        Assert.StartsWith("Ui: shown-input ", line);
        Assert.Contains("hoverPump=0", line);
        Assert.Contains("deltaY=-120", line);
        Assert.Contains("notch=1", line);
        Assert.Contains("hit='Mods'", line);
        Assert.Contains("applied=pump", line);
        Assert.Contains("path=pump", line);
    }

    /// <summary>The per-frame pass is armed only while a pointer-routed panel is shown.</summary>
    [Fact]
    public void The_pump_is_armed_only_while_a_hover_panel_is_shown()
    {
        var panel = Open("A", 0f, 0f, 200f, 200f);
        Assert.False(UiHoverGate.PumpArmedForTests);

        _client.Pointer = PointerSample.At(600f, 400f);
        UiApi.Show(panel);
        Assert.True(UiHoverGate.PumpArmedForTests);

        panel.Hide();
        Assert.False(UiHoverGate.PumpArmedForTests);
        Assert.Equal(0, UiHoverGate.Count);
    }

    /// <summary>A HUD overlay never registers the context and never joins the refcount.</summary>
    [Fact]
    public void A_Never_panel_never_takes_input_even_under_the_pointer()
    {
        var panel = Open("A", 0f, 0f, 200f, 200f);
        panel.CaptureMode = UiCaptureMode.Never;
        _client.Pointer = PointerSample.At(100f, 100f);

        UiApi.Show(panel);
        PointAt(100f, 100f);

        Assert.False(UiApi.IsCapturingInput);
        Assert.Equal(0, _blocker.Registers);
        Assert.Equal(0, UiInputCapture.Count);
        Assert.Equal(0, UiHoverGate.Count);
        // ...and the cursor is still unlocked, because that half was never conditional.
        Assert.Equal(1, _client.Capture);
    }

    /// <summary>
    /// Hidden with the pointer still over it. The refcount is what makes the block wanted at all,
    /// so the context goes even though nothing moved the mouse.
    /// </summary>
    [Fact]
    public void A_panel_hidden_while_the_pointer_is_over_it_releases_the_context()
    {
        var panel = Open("A", 0f, 0f, 200f, 200f);
        _client.Pointer = PointerSample.At(100f, 100f);
        UiApi.Show(panel);
        Assert.True(UiApi.IsCapturingInput);

        panel.Hide();

        Assert.False(UiApi.IsCapturingInput);
        Assert.Equal(1, _blocker.Removes);
        Assert.Equal(0, UiInputCapture.Count);
        Assert.Equal(0, UiHoverGate.Count);
    }

    /// <summary>Disposed while shown and hovered: the same, through <c>HideCore</c> (FUI2).</summary>
    [Fact]
    public void A_panel_disposed_while_hovered_releases_the_context()
    {
        var panel = Open("A", 0f, 0f, 200f, 200f);
        _client.Pointer = PointerSample.At(100f, 100f);
        UiApi.Show(panel);
        Assert.True(UiApi.IsCapturingInput);

        panel.Dispose();

        Assert.False(UiApi.IsCapturingInput);
        Assert.Equal(0, UiInputCapture.Count);
        Assert.Equal(0, UiHoverGate.Count);
        Assert.True(UiInputCapture.PointerBlock); // back to the fail-closed default
    }

    /// <summary>
    /// Two overlapping panels: closing the one under the pointer leaves the other still hovered,
    /// so the context stays exactly where it is rather than being dropped and retaken.
    /// </summary>
    [Fact]
    public void Closing_one_of_two_overlapping_panels_leaves_the_other_routing()
    {
        var a = Open("A", 0f, 0f, 200f, 200f);
        var b = Open("B", 100f, 100f, 300f, 300f);
        _client.Pointer = PointerSample.At(150f, 150f);
        UiApi.Show(a);
        UiApi.Show(b);

        Assert.True(UiApi.IsCapturingInput);
        Assert.Equal(1, _blocker.Registers);

        a.Hide();

        Assert.True(UiApi.IsCapturingInput);
        Assert.Equal(0, _blocker.Removes);
        Assert.Equal(1, UiHoverGate.Count);

        b.Hide();

        Assert.False(UiApi.IsCapturingInput);
        Assert.Equal(1, _blocker.Removes);
    }

    /// <summary>
    /// The master switch still wins. <c>Ui.CaptureInput(false)</c> is documented as removing the
    /// context now and keeping it off while panels open, and a hovered panel must not undo that.
    /// </summary>
    [Fact]
    public void CaptureInput_false_still_wins_over_a_hovered_panel()
    {
        var panel = Open("A", 0f, 0f, 200f, 200f);
        _client.Pointer = PointerSample.At(100f, 100f);
        UiApi.Show(panel);
        Assert.True(UiApi.IsCapturingInput);

        UiApi.CaptureInput(false);
        Assert.False(UiApi.IsCapturingInput);

        PointAt(600f, 400f);
        PointAt(100f, 100f);
        Assert.False(UiApi.IsCapturingInput);

        UiApi.CaptureInput(true);
        Assert.True(UiApi.IsCapturingInput);
    }

    /// <summary>A plugin's own default applies to its panels and leaves the framework's alone.</summary>
    [Fact]
    public void A_plugin_can_move_the_default_for_its_own_panels_only()
    {
        UiApi.CaptureInput(UiCaptureMode.WhileShown, pluginId: PluginId);

        Assert.Equal(UiCaptureMode.WhileShown, UiApi.CaptureMode(PluginId));
        Assert.Equal(UiCaptureMode.WhenHovered, UiApi.CaptureMode("some.other.plugin"));

        var panel = Open("A", 0f, 0f, 200f, 200f);
        _client.Pointer = PointerSample.At(600f, 400f);
        UiApi.Show(panel);

        Assert.True(UiApi.IsCapturingInput);
    }

    /// <summary>The framework-wide default — what the client config entry moves — applies to all.</summary>
    [Fact]
    public void The_framework_default_can_be_moved_for_every_plugin()
    {
        UiApi.CaptureInput(UiCaptureMode.WhileShown);

        Assert.Equal(UiCaptureMode.WhileShown, UiApi.CaptureMode(PluginId));
        Assert.Equal(UiCaptureMode.WhileShown, UiApi.CaptureMode("some.other.plugin"));
    }

    /// <summary>A panel's own choice beats both defaults.</summary>
    [Fact]
    public void A_panel_overrides_both_defaults()
    {
        UiApi.CaptureInput(UiCaptureMode.WhileShown);
        var panel = Open("A", 0f, 0f, 200f, 200f);
        panel.CaptureMode = UiCaptureMode.WhenHovered;
        _client.Pointer = PointerSample.At(600f, 400f);

        UiApi.Show(panel);

        AssertGameplayReleased();
    }

    /// <summary>
    /// Changing the mode of an already-open panel takes effect at once: a panel switched to
    /// <c>WhileShown</c> has to start blocking without waiting for the player to move the mouse.
    /// </summary>
    [Fact]
    public void Changing_the_mode_of_an_open_panel_applies_immediately()
    {
        var panel = Open("A", 0f, 0f, 200f, 200f);
        _client.Pointer = PointerSample.At(600f, 400f);
        UiApi.Show(panel);
        AssertGameplayReleased();

        panel.CaptureMode = UiCaptureMode.WhileShown;
        AssertGameplayBlocked();

        // Never is still the one mode that leaves the game's list alone entirely.
        panel.CaptureMode = UiCaptureMode.Never;
        Assert.False(UiApi.IsCapturingInput);
        Assert.Equal(0, UiInputCapture.Count);

        panel.CaptureMode = UiCaptureMode.WhileShown;
        AssertGameplayBlocked();
        Assert.Equal(1, UiInputCapture.Count);
    }

    /// <summary>
    /// A rect read that throws is the same fact as one that cannot be read: the panel blocks, the
    /// exception does not escape into the frame handler, and the reason is logged once.
    /// </summary>
    [Fact]
    public void A_rect_read_that_throws_fails_closed_and_says_so_once()
    {
        var panel = Open("A", 0f, 0f, 200f, 200f);
        _client.Pointer = PointerSample.At(600f, 400f);
        UiApi.Show(panel);
        AssertGameplayReleased();

        _client.ThrowOnRect = true;
        PointAt(600f, 400f);

        AssertGameplayBlocked();
        Assert.Single(_log.Warnings, w => w.Contains("screen rect could not be read", StringComparison.Ordinal));

        PointAt(601f, 401f);
        Assert.Single(_log.Warnings, w => w.Contains("screen rect could not be read", StringComparison.Ordinal));
    }

    /// <summary>A pointer read that throws is an unreadable pointer, so the panel blocks.</summary>
    [Fact]
    public void A_pointer_read_that_throws_fails_closed()
    {
        var panel = Open("A", 0f, 0f, 200f, 200f);
        _client.Pointer = PointerSample.At(600f, 400f);
        UiApi.Show(panel);
        AssertGameplayReleased();

        _client.ThrowOnPointer = true;
        UiHoverGate.Pump();

        AssertGameplayBlocked();
    }

    /// <summary>
    /// The pointer leaving the game window releases the block without the player closing anything
    /// — alt-tab, a second monitor, the mouse dragged off the edge.
    /// </summary>
    [Fact]
    public void The_pointer_leaving_the_window_releases_the_block()
    {
        var panel = Open("A", 0f, 0f, 200f, 200f);
        _client.Pointer = PointerSample.At(100f, 100f);
        UiApi.Show(panel);
        AssertGameplayBlocked();

        _client.Pointer = PointerSample.Outside;
        UiHoverGate.Pump();

        AssertGameplayReleased();
    }

    /// <summary>
    /// The invariant the whole design rests on: the gate is ANDed with the refcount, so a gate
    /// left stuck at "blocked" — a pump that stopped, a throw at the wrong moment — still cannot
    /// leave a context registered with nobody owning it once the last panel closes.
    /// </summary>
    [Fact]
    public void A_gate_stuck_at_blocked_cannot_leave_a_context_with_no_owner()
    {
        var panel = Open("A", 0f, 0f, 200f, 200f);
        _client.Pointer = PointerSample.At(100f, 100f);
        UiApi.Show(panel);
        Assert.True(UiApi.IsCapturingInput);

        UiInputCapture.SetPointerBlock(true);
        panel.Hide();

        Assert.True(UiInputCapture.PointerBlock);
        Assert.False(UiApi.IsCapturingInput);
        Assert.Equal(0, UiInputCapture.Count);
    }

    /// <summary>Uninstall forgets every routed panel and unsubscribes the per-frame pass.</summary>
    [Fact]
    public void Uninstall_forgets_every_routed_panel()
    {
        var panel = Open("A", 0f, 0f, 200f, 200f);
        _client.Pointer = PointerSample.At(100f, 100f);
        UiApi.Show(panel);
        Assert.Equal(1, UiHoverGate.Count);

        UiApi.Uninstall();

        Assert.Equal(0, UiHoverGate.Count);
        Assert.False(UiHoverGate.PumpArmedForTests);
        Assert.True(UiHoverGate.BlockedForTests);
        Assert.True(UiInputCapture.PointerBlock);
    }

    /// <summary>
    /// R44 finding 1, end to end through the real handles, in the sequence a player produces
    /// inside the shipped settings panel: <b>block on, panel closed with the pointer away, panel
    /// reopened with the pointer over it.</b>
    ///
    /// <para>
    /// Step by step. F10 with the mouse off center: the router transitions to "not blocked" and the
    /// blocker takes <see cref="UiMaskScope.CloseKeyOnly"/> — correct, the wheel should zoom.
    /// Escape or F10 with the mouse still off it: <c>HideCore</c> calls <c>Release</c> first, which
    /// resets the module's pointer flag; if it does not also tell the blocker, the blocker stays at
    /// CloseKeyOnly. <c>Detach</c> then resets the router to "blocked", so <c>SetPointerBlock</c>
    /// is called with the value the module already holds and the old equality guard returned early.
    /// F10 again with the mouse over the panel: the router is already "blocked", reports no change,
    /// and never pushes. Nothing repairs it until the pointer crosses an edge.
    /// </para>
    ///
    /// <para>
    /// The player is then hovering a panel with the full mask believed on and none of it applied:
    /// every wheel notch scrolls the list <i>and</i> zooms the camera underneath it (evidence 167's
    /// defect 2, restored), WASD walks, and a click on a row also swings.
    /// <see cref="AssertGameplayBlocked"/> asserts the blocker's own scope, which is the half that
    /// fails.
    /// </para>
    /// </summary>
    [Fact]
    public void A_panel_closed_with_the_pointer_away_and_reopened_under_it_masks_everything()
    {
        var panel = Open("A", 0f, 0f, 200f, 200f);

        // 1. Opened with the pointer elsewhere: released, correctly.
        _client.Pointer = PointerSample.At(600f, 400f);
        UiApi.Show(panel);
        AssertGameplayReleased();

        // 2. Closed with the pointer still elsewhere. The module's pointer flag goes back to its
        //    fail-closed default — that is what step 3 rests on — and the blocker is told, which is
        //    finding 1's property. What it is told is CloseKeyOnly, not Full: with no panel open
        //    there is nothing to mask, and the removal this Hide starts may not land on this pass
        //    (job FUI27, R45 finding 8). Note that BOTH of the calls Hide makes reach this — the
        //    Release and the Detach whose empty-set Refresh pushes the gate's default — which is
        //    exactly why the fix is in the derivation and not in either caller.
        panel.Hide();
        Assert.False(UiApi.IsCapturingInput);
        Assert.True(UiInputCapture.PointerBlock);
        Assert.Equal(UiMaskScope.CloseKeyOnly, _blocker.Scope);

        // 3. Reopened with the pointer over where it appears. The router's answer did not change,
        //    so this is the state that has to be right already rather than repaired.
        _client.Pointer = PointerSample.At(100f, 100f);
        UiApi.Show(panel);

        AssertGameplayBlocked();
        Assert.True(InputMaskPlan.For(_blocker.Scope).BlockZoom);

        panel.Dispose();
    }

    /// <summary>
    /// The same pair of states, with the pointer never moving at all — the case that makes the
    /// desync permanent rather than merely long: after the reopen, no edge crossing is available to
    /// repair anything, because the mouse is exactly where it was.
    /// </summary>
    [Fact]
    public void The_scope_after_a_close_and_reopen_does_not_depend_on_the_pointer_having_moved()
    {
        var a = Open("A", 0f, 0f, 200f, 200f);
        _client.Pointer = PointerSample.At(600f, 400f);
        UiApi.Show(a);
        AssertGameplayReleased();

        a.Hide();
        UiApi.Show(a);

        // Still off the panel, so still released — and released because the pointer says so, not
        // because a stale scope survived the close.
        AssertGameplayReleased();
        Assert.Equal(UiInputCapture.WantedMaskScope, UiInputCapture.MaskScope);

        a.Dispose();
    }

    /// <summary>A mode the enum does not name is refused at the door, not routed as "Never".</summary>
    [Fact]
    public void An_undeclared_mode_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => UiApi.CaptureInput((UiCaptureMode)77));
    }

    /// <summary>Counts what the game's context list was actually asked to do.</summary>
    private sealed class CountingBlocker : IUiInputBlocker
    {
        public bool IsRegistered { get; private set; }

        public bool BlocksCameraRotation => false;

        public bool DenyModifications => false;

        public int Registers { get; private set; }

        public int Removes { get; private set; }

        public bool Register()
        {
            Registers++;
            IsRegistered = true;
            return true;
        }

        public bool Remove()
        {
            Removes++;
            IsRegistered = false;
            return true;
        }

        // Job FUI20: the scope no longer changes the registration, so this fake records it
        // rather than counting it as a register/remove.
        public UiMaskScope Scope { get; private set; } = UiMaskScope.Full;

        public int ScopeChanges { get; private set; }

        /// <summary>
        /// Job FUI23 (R44 finding 4). The blocker's own copy is what the game reads and what
        /// <see cref="UiInputCapture.MaskScope"/> now reports; this fake had it all along, as
        /// <see cref="Scope"/>, which is why the tests could tell the two apart and the production
        /// diagnostic could not.
        /// </summary>
        public UiMaskScope AppliedScope => Scope;

        public bool MaskLive => true;

        public void SetMaskScope(UiMaskScope scope)
        {
            if (Scope != scope) ScopeChanges++;
            Scope = scope;
        }

        public void Dispose() { }
    }
}

/// <summary>
/// Job FUI23, R44 finding 2: the keyboard half of the pointer routing, which FUI19 delivered,
/// FUI20 silently removed, and a rename was mistaken for.
///
/// <para>
/// FUI19's request was that a panel open in one corner stops taking the whole screen hostage, and
/// the keyboard is part of the screen: with the pointer nowhere near the panel, a mod keybind
/// registered <c>allowWhileCaptured: false</c> should still fire. FUI19 implemented that by adding
/// the pointer term to <c>UiInputCapture.IsBlocking</c> and pointing <c>Keybinds.Pump</c> at it.
/// FUI20 moved the term back out — correctly, so a panel the pointer is not over still consumes
/// Escape — and left <c>Keybinds</c> reading a property that no longer carried it. The line was
/// then "fixed" to <c>IsBlocking</c>, which is character-for-character the expression it replaced,
/// under a comment describing behavior that had stopped existing.
/// </para>
///
/// <para>
/// Nothing in the suite drove <c>Keybinds.Pump</c> with a panel open and the pointer off it, which
/// is why a rename could pass for a fix. These are that test, in both directions.
/// </para>
/// </summary>
[Collection("PluginLogger")]
public class HoverKeybindTests : IDisposable
{
    private const string PluginId = "example.hover.keybind.test";

    private readonly TestLogSource _log = new();
    private readonly UiTests.FakeUiRuntime _client = new() { IsClient = true };
    private readonly UiKeybindTests.FakeKeyInput _keys = new();
    private readonly object _owner = new();
    private readonly string _dir;
    private readonly ConfigFile _file;

    public HoverKeybindTests()
    {
        Plugin.Logger = _log;
        _dir = Path.Combine(Path.GetTempPath(), "progenitor-hover-keys-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _file = new ConfigFile(Path.Combine(_dir, "example.mod.cfg"), saveOnInit: true);

        UiApi.ResetForTests();
        UiApi.SetRuntimeForTests(_client);
        UiApi.SetKeyInputSourceForTests(_keys);
    }

    public void Dispose()
    {
        UiApi.ResetForTests();
        Plugin.Logger = null;
        try { Directory.Delete(_dir, recursive: true); }
        catch { /* best effort */ }
    }

    private IUiPanel Open(string title, float minX, float minY, float maxX, float maxY)
    {
        var panel = UiApi.Panel(title, _owner, PluginId);
        _client.SetRect(title, minX, minY, maxX, maxY);
        return panel;
    }

    /// <summary>
    /// The FUI19 promise applied to the keyboard: a panel open in one corner while the player is
    /// fighting in the world does not suppress their binds, and moving the pointer onto it does.
    /// </summary>
    [Fact]
    public void A_panel_the_pointer_is_not_over_does_not_suppress_a_keybind()
    {
        int fired = 0;
        Keybinds.Register(_owner, PluginId, "Demo", _file, new KeyChord(UiKey.F9), "", () => fired++);

        var panel = Open("A", 0f, 0f, 200f, 200f);
        _client.Pointer = PointerSample.At(600f, 400f);
        UiApi.Show(panel);

        // The panel is shown and the block is wanted — that is what keeps a context registered so
        // Escape closes it from anywhere — but the pointer is elsewhere, so the mask is scoped to
        // the close key and the bind is the player's. (No game blocker is installed here, so
        // Ui.IsCapturingInput would report the null one; the wanted state is the fact under test.)
        Assert.True(UiInputCapture.IsBlocking);
        Assert.Equal(UiMaskScope.CloseKeyOnly, UiInputCapture.MaskScope);
        Assert.False(UiInputCapture.IsFullyBlocking);

        _keys.Press(new KeyChord(UiKey.F9));
        Keybinds.Pump();
        Assert.Equal(1, fired);

        // Onto the panel: now it is suppressed, which is the half that must not be lost.
        _client.Pointer = PointerSample.At(100f, 100f);
        UiHoverGate.Pump();
        Assert.True(UiInputCapture.IsFullyBlocking);

        _keys.Press(new KeyChord(UiKey.F9));
        Keybinds.Pump();
        Assert.Equal(1, fired);

        panel.Dispose();
    }

    /// <summary>
    /// A genuinely modal panel suppresses binds wherever the pointer is, which is what
    /// <see cref="UiCaptureMode.WhileShown"/> means and the reason the pointer term is read through
    /// the gate rather than from a rect test of its own.
    /// </summary>
    [Fact]
    public void A_WhileShown_panel_suppresses_a_keybind_with_the_pointer_anywhere()
    {
        int fired = 0;
        Keybinds.Register(_owner, PluginId, "Demo", _file, new KeyChord(UiKey.F9), "", () => fired++);

        var panel = Open("A", 0f, 0f, 200f, 200f);
        panel.CaptureMode = UiCaptureMode.WhileShown;
        _client.Pointer = PointerSample.At(600f, 400f);
        UiApi.Show(panel);

        Assert.True(UiInputCapture.IsFullyBlocking);

        _keys.Press(new KeyChord(UiKey.F9));
        Keybinds.Pump();
        Assert.Equal(0, fired);

        panel.Dispose();
    }

    /// <summary>
    /// The two questions are different questions, and the names now say which is which. A panel
    /// shown with the pointer off it wants a context registered (so Escape closes it from
    /// anywhere) and does <b>not</b> want the player's binds suppressed. One property answering
    /// both is what R44 finding 2 is.
    /// </summary>
    [Fact]
    public void Registration_and_suppression_are_not_the_same_question()
    {
        var panel = Open("A", 0f, 0f, 200f, 200f);
        _client.Pointer = PointerSample.At(600f, 400f);
        UiApi.Show(panel);

        Assert.True(UiInputCapture.IsBlocking);
        Assert.False(UiInputCapture.IsFullyBlocking);

        _client.Pointer = PointerSample.At(100f, 100f);
        UiHoverGate.Pump();

        Assert.True(UiInputCapture.IsBlocking);
        Assert.True(UiInputCapture.IsFullyBlocking);

        panel.Dispose();
    }
}
