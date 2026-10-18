using System.Collections.Generic;
using Progenitor.Ui;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Spec P6 on game build 1.1.13.0: where each inline anchor goes and what P6 buttons are cloned
/// from, against a fake hierarchy that mirrors the shipped prefabs (read from the game's own
/// serialized UI data). Two serialized references the first design relied on are empty on this
/// build: <c>InventorySubMenu.SelfSortButton</c> (so no P6 button was ever built: every window
/// reported Opened and drew nothing) and <c>ContainerSubMenu.OpenTagEditButtonParent</c>.
/// </summary>
public class VanillaPanelAnchorsTests
{
    // ---------------------------------------------------------------- the fake hierarchy

    private sealed class Node : IAnchorNode
    {
        private readonly List<Node> _children = new();

        internal Node(string name, float w = 0, float h = 0, bool button = false, bool active = true,
            AnchorLayout layout = AnchorLayout.None, bool cloneSafe = true)
        {
            CloneSafe = cloneSafe;
            Name = name;
            Width = w;
            Height = h;
            IsButton = button;
            Active = active;
            Layout = layout;
        }

        public string Name { get; }

        public bool Active { get; set; }

        public bool IsButton { get; }

        public bool CloneSafe { get; }

        public float Width { get; }

        public float Height { get; }

        public AnchorLayout Layout { get; }

        public IAnchorNode? Parent { get; private set; }

        public int ChildCount => _children.Count;

        public IAnchorNode? Child(int index) => index >= 0 && index < _children.Count ? _children[index] : null;

        internal Node Add(Node child)
        {
            child.Parent = this;
            _children.Add(child);
            return child;
        }
    }

    /// <summary>
    /// <c>CharacterInventorySubMenu/MotionRoot/SharedForNowAtLeast/ButtonsLayoutNew</c>: a grid of
    /// 116x36 cells, spacing 8, holding four icon buttons in this order. With
    /// <paramref name="asShipped"/> the three after the first are not clone-safe, as on 1.1.13.0:
    /// their target graphic is <c>DropAllButton</c>'s image.
    /// </summary>
    private static (Node Bar, Node DropAll, Node Sort, Node SmartTake, Node SmartMerge) Bar(bool asShipped = false)
    {
        var bar = new Node("ButtonsLayoutNew", 124, 36, layout: AnchorLayout.Grid);
        var dropAll = bar.Add(new Node("DropAllButton", 116, 36, button: true));
        var sort = bar.Add(new Node("SortButtonNew", 116, 36, button: true, cloneSafe: !asShipped));
        var take = bar.Add(new Node("SmartTakeButton", 116, 36, button: true, cloneSafe: !asShipped));
        var merge = bar.Add(new Node("SmartMergeButtonNew", 116, 36, button: true, cloneSafe: !asShipped));
        return (bar, dropAll, sort, take, merge);
    }

    /// <summary>The bar fields in <see cref="VanillaPanelAnchors.ButtonDonorFields"/> order, as 1.1.13.0 sets them.</summary>
    private static IAnchorNode?[] FieldsOf((Node Bar, Node DropAll, Node Sort, Node SmartTake, Node SmartMerge) b, Node? selfSort = null) =>
        new IAnchorNode?[] { selfSort, b.Sort, b.SmartMerge, b.SmartTake, b.DropAll };

    /// <summary>
    /// <c>StashInventorySubMenu/Background/HUDPanelBackground/HUDPanelBackground/Name/ButtonLayoutParent/EditTagButton</c>:
    /// a 24x24 horizontal group that does not size its children, right-aligned, holding the 24x24 rename button.
    /// </summary>
    private static (Node Name, Node Group, Node Rename) TitleRow()
    {
        var name = new Node("Name", 140, 42);
        var group = name.Add(new Node("ButtonLayoutParent", 24, 24, layout: AnchorLayout.HorizontalFree));
        var rename = group.Add(new Node("EditTagButton", 24, 24, button: true));
        name.Add(new Node("HightlightFrame", 172, 50));
        return (name, group, rename);
    }

    // ---------------------------------------------------------------- the button donor

    [Fact]
    public void With_clone_safe_bar_buttons_the_first_set_field_after_SelfSortButton_wins()
    {
        var b = Bar();
        var pick = VanillaPanelAnchors.PickButtonDonor(FieldsOf(b), b.Bar);
        Assert.Same(b.Sort, pick.Node);
        Assert.Equal("AllSortButton", pick.Source);
    }

    [Fact]
    public void As_shipped_only_the_drop_all_button_can_be_cloned_so_it_is_the_donor()
    {
        // Live 1.1.13.0: with AllSortButton as the donor every clone was refused (its target graphic
        // is the drop-all button's image) and no button drew. Only DropAllButton drives its own graphics.
        var b = Bar(asShipped: true);
        var pick = VanillaPanelAnchors.PickButtonDonor(FieldsOf(b), b.Bar);
        Assert.Same(b.DropAll, pick.Node);
        Assert.Equal("DropAllButton", pick.Source);

        // The sort anchor is unaffected: it is stood beside, not cloned.
        Assert.Same(b.Sort, VanillaPanelAnchors.SortControl(null, b.Sort, b.Bar).Node);

        // No clone-safe button at all: no donor, rather than one the clone gate refuses.
        var none = new Node("ButtonsLayoutNew", layout: AnchorLayout.Grid);
        none.Add(new Node("SortButtonNew", 116, 36, button: true, cloneSafe: false));
        Assert.False(VanillaPanelAnchors.PickButtonDonor(new IAnchorNode?[5], none).Found);
    }

    [Fact]
    public void A_build_that_sets_SelfSortButton_keeps_it_as_the_donor()
    {
        var b = Bar();
        var self = new Node("SelfSortButton", 114, 34, button: true);
        var pick = VanillaPanelAnchors.PickButtonDonor(FieldsOf(b, self), b.Bar);
        Assert.Same(self, pick.Node);
        Assert.Equal("SelfSortButton", pick.Source);
    }

    [Fact]
    public void An_active_bar_button_is_preferred_and_an_inactive_one_is_still_better_than_none()
    {
        var b = Bar();
        b.Sort.Active = false;
        var pick = VanillaPanelAnchors.PickButtonDonor(FieldsOf(b), b.Bar);
        Assert.Same(b.SmartMerge, pick.Node);
        Assert.Equal("SmartMergeButton", pick.Source);

        foreach (var n in new[] { b.DropAll, b.SmartTake, b.SmartMerge }) n.Active = false;
        pick = VanillaPanelAnchors.PickButtonDonor(FieldsOf(b), b.Bar);
        Assert.Same(b.Sort, pick.Node);
    }

    [Fact]
    public void With_every_field_empty_the_donor_is_the_first_button_on_the_bar()
    {
        var b = Bar();
        var pick = VanillaPanelAnchors.PickButtonDonor(new IAnchorNode?[5], b.Bar);
        Assert.Same(b.DropAll, pick.Node);
        Assert.Equal("bar:DropAllButton", pick.Source);
    }

    [Fact]
    public void Nothing_that_is_not_a_game_button_is_ever_a_donor()
    {
        var bar = new Node("ButtonsLayoutNew", layout: AnchorLayout.Grid);
        bar.Add(new Node("Divider", 2, 36));
        var notButton = new Node("SortLabel", 116, 36);
        Assert.False(VanillaPanelAnchors.PickButtonDonor(new IAnchorNode?[] { null, notButton }, bar).Found);
        Assert.False(VanillaPanelAnchors.PickButtonDonor(new IAnchorNode?[5], null).Found);
    }

    // ---------------------------------------------------------------- BesideSort

    [Fact]
    public void BesideSort_stands_beside_the_bar_sort_button_and_never_joins_the_grid()
    {
        // One more cell would recenter the grid and move all four game buttons by 62 units.
        var b = Bar();
        var sort = VanillaPanelAnchors.SortControl(null, b.Sort, b.Bar);
        Assert.Same(b.Sort, sort.Node);
        Assert.Equal("AllSortButton", sort.Source);
        Assert.Same(b.Bar, sort.Node!.Parent);
        Assert.Equal(InlineMode.Positioned, VanillaPanelAnchors.PlanInline(sort.Node.Parent, besideSort: true));
        Assert.Equal(InlineMode.GridCell, VanillaPanelAnchors.PlanInline(sort.Node.Parent));
        Assert.Equal(72f, VanillaPanelAnchors.CompactWidth(36));
    }

    [Fact]
    public void The_sort_control_falls_back_to_a_bar_button_named_sort()
    {
        var b = Bar();
        var sort = VanillaPanelAnchors.SortControl(null, null, b.Bar);
        Assert.Same(b.Sort, sort.Node);
        Assert.Equal("bar:SortButtonNew", sort.Source);
        Assert.False(VanillaPanelAnchors.SortControl(null, null, null).Found);
    }

    // The character window's inventory on 1.1.13.0 AT REST, in the window's own units (origin at
    // its top-left corner, y down is negative), from the live placement line of 5001a8916:
    //   rest:    last=(438,-861 116x36) frame=(0,-925 620x925)
    //   sliding: last=(408,-861 116x36) frame=(-22,-925 642x925)
    // The bar is centered (its four 116x36 cells 8 apart, from 66 to 554); the item area
    // (ParentContainerInventory: the body inset 8 a side, then 12 a side, top-pivoted 442 below the
    // body's top, 275 high) ends 74 units above the bar's bottom; the body (SharedForNowAtLeast) ends at the bar's bottom.
    private static readonly UiRect Frame = new(0, -925, 620, 925);
    private static readonly UiRect SlidingFrame = new(-22, -925, 642, 925);
    private static readonly UiRect DropAllCell = new(66, -861, 116, 36);
    private static readonly UiRect SortCell = new(190, -861, 116, 36);
    private static readonly UiRect SmartTakeCell = new(314, -861, 116, 36);
    private static readonly UiRect SmartMergeCell = new(438, -861, 116, 36);
    private static readonly UiRect SlidingSmartMerge = new(408, -861, 116, 36);
    private static readonly UiRect ItemArea = new(20, -787, 580, 275);
    private static readonly UiRect Body = new(8, -861, 604, 791);

    private static List<UiObstacle> BarObstacles() => new()
    {
        new("DropAllButton", DropAllCell), new("SortButtonNew", SortCell), new("SmartTakeButton", SmartTakeCell),
        new("SmartMergeButtonNew", SmartMergeCell), new("ParentContainerInventory", ItemArea),
    };

    private static VanillaFitResult Row(float textWidth, float textMin, int buttons, out BesideSlot slot, bool icons = true) =>
        VanillaPanelPlacement.PlaceBesideRow(SmartMergeCell, SortCell, allowAbove: true, icons, 8, textWidth, textMin, buttons, 36,
            Frame, BarObstacles(), out slot);

    [Fact]
    public void At_rest_the_bar_is_centered_and_the_slot_right_of_it_is_58_units()
    {
        Assert.Equal(Frame.X + Frame.Width / 2f, (DropAllCell.X + SmartMergeCell.XMax) / 2f);
        Assert.Equal(DropAllCell.XMax + 8f, SortCell.X);
        Assert.Equal(554f, SmartMergeCell.XMax);
        Assert.Equal(58f, Frame.XMax - (SmartMergeCell.XMax + 8f));

        // The slide: 30 units left, frame widened to 642 by the body that slides out past the window.
        Assert.Equal(-30f, SlidingSmartMerge.X - SmartMergeCell.X);
        Assert.Equal(88f, SlidingFrame.XMax - (SlidingSmartMerge.XMax + 8f));
    }

    [Fact]
    public void A_short_caption_shows_as_text_full_size_up_to_30_units_and_squeezed_up_to_40()
    {
        // 58 = caption + 14 padding a side: 30 units at the game's 16-unit size; down to 0.75 of it, 40.
        Assert.Equal(58f, 30f + 2 * 14f);
        Assert.Equal(58f, VanillaPanelAnchors.MinButtonWidth(40f, 14f, 36f));

        // "Go" (about 22 units): a 72-unit button by its compact minimum, squeezed to 58.
        var verdict = Row(72, VanillaPanelAnchors.MinButtonWidth(22f, 14f, 36f), 1, out var slot);
        Assert.Equal(VanillaFit.Fits, verdict.Fit);
        Assert.False(verdict.Icons);
        Assert.Equal(BesideSlot.After, slot);
        Assert.Equal(new UiRect(562, -861, 58, 36).ToString(), verdict.Rect.ToString());

        // A 40-unit caption still fits as text; 41 does not.
        Assert.False(Row(68, VanillaPanelAnchors.MinButtonWidth(40f, 14f, 36f), 1, out _).Icons);
        Assert.True(Row(69, VanillaPanelAnchors.MinButtonWidth(41f, 14f, 36f), 1, out _).Icons);
    }

    [Fact]
    public void Storage_and_Stash_fall_back_to_a_36_unit_icon_button_right_of_the_bar()
    {
        // "Storage" (64-unit caption, 92 wide, at least 76) and "Stash" (72 wide) do not fit 58 as
        // text: each is a square icon button, 36 + the 8-unit gap = 44 of the 58, with its caption as tooltip.
        foreach (var (width, min) in new[] { (92f, VanillaPanelAnchors.MinButtonWidth(64f, 14f, 36f)), (72f, 72f) })
        {
            var verdict = Row(width, min, 1, out var slot);
            Assert.Equal(VanillaFit.Fits, verdict.Fit);
            Assert.True(verdict.Icons);
            Assert.True(verdict.Retry);
            Assert.Equal(BesideSlot.After, slot);
            Assert.Equal(new UiRect(562, -861, 36, 36).ToString(), verdict.Rect.ToString());
            Assert.True(Frame.Contains(verdict.Rect));
            foreach (var o in BarObstacles()) Assert.False(verdict.Rect.Overlaps(o.Rect), o.Name);
        }

        // Without an icon face (any other anchor) the same row is hidden with the reason.
        var noIcons = Row(92, 76, 1, out var none, icons: false);
        Assert.Equal(VanillaFit.Blocked, noIcons.Fit);
        Assert.Equal(BesideSlot.None, none);
        Assert.Equal("overflow:mod/overlap:ParentContainerInventory", noIcons.Reason);
    }

    [Fact]
    public void Three_icons_do_not_fit_the_58_unit_slot_so_a_three_button_handle_is_hidden()
    {
        // 3 x 36 + 2 x 8 = 124 > 58, and above the sort button the row meets the item area.
        var verdict = Row(275, 233, 3, out var slot);
        Assert.Equal(VanillaFit.Blocked, verdict.Fit);
        Assert.Equal(BesideSlot.None, slot);
        Assert.Equal("overflow:mod/overlap:ParentContainerInventory", verdict.Reason);
    }

    [Fact]
    public void Above_the_sort_button_and_below_the_bar_are_rejected_at_rest()
    {
        // Above Sort: the slot (8 above the bar) is 36 high and the item area starts 38 above the bar.
        var above = new UiRect(SortCell.X, SortCell.YMax + 8, 116, 36);
        Assert.True(above.Overlaps(ItemArea));
        Assert.Equal(6f, above.YMax - ItemArea.Y);

        // Below the bar: outside the window's content body, which ends at the bar's bottom; the 64
        // units to the window's edge are its bottom border, leaving 20 under a 36-unit row and its gap.
        var below = new UiRect(SortCell.X, SortCell.Y - 8 - 36, 116, 36);
        Assert.False(Body.Contains(below));
        Assert.Equal(64f, SortCell.Y - Frame.Y);
        Assert.Equal(20f, below.Y - Frame.Y);
    }

    [Fact]
    public void A_window_is_judged_only_after_three_equal_measurements()
    {
        // The live open: two sliding frames, then rest. The slot is judged on the third rest frame,
        // never on a sliding one, so a placement is never shown and then hidden.
        var settle = new PlacementSettle();
        Assert.False(settle.Observe(SlidingFrame, SlidingSmartMerge));
        Assert.False(settle.Observe(new UiRect(-10, -925, 630, 925), new UiRect(426, -861, 116, 36)));
        Assert.False(settle.Observe(Frame, SmartMergeCell));
        Assert.False(settle.Observe(Frame, SmartMergeCell));
        Assert.True(settle.Observe(Frame, SmartMergeCell));
        Assert.True(settle.Observe(Frame, SmartMergeCell));

        // Moving again (a resolution change) starts the count again.
        Assert.False(settle.Observe(SlidingFrame, SmartMergeCell));
        settle.Reset();
        Assert.False(settle.Observe(Frame, SmartMergeCell));
        Assert.True(VanillaFitResult.Moving.Settling);
        Assert.True(VanillaFitResult.Moving.Shown);
    }

    [Fact]
    public void A_window_sliding_as_one_piece_keeps_its_shape_and_is_placed_once()
    {
        // The devourer, live on Core 2026.10.107: the window held at its +30 open pose, then slid to rest, and the
        // title row was placed twice. Measured on the moving content, both poses are one shape.
        var settle = new PlacementSettle();
        var lastAtStart = new UiRect(424, 887, 24, 24);
        var frameAtStart = new UiRect(8, 0, 664, 938);
        var lastAtRest = new UiRect(394, 887, 24, 24);
        var frameAtRest = new UiRect(-22, 0, 664, 938);
        Assert.True(PlacementSettle.SameShape(frameAtRest, lastAtRest, frameAtStart, lastAtStart));
        Assert.False(settle.Observe(frameAtStart, lastAtStart));
        Assert.False(settle.Observe(frameAtStart, lastAtStart));
        Assert.True(settle.Observe(frameAtStart, lastAtStart));
        Assert.True(settle.Observe(new UiRect(-7, 0, 664, 938), new UiRect(409, 887, 24, 24)));
        Assert.True(settle.Observe(frameAtRest, lastAtRest));

        var placedAtStart = new UiRect(456, 887, 24, 24);
        var placedAtRest = new UiRect(426, 887, 24, 24);
        Assert.Equal(
            PlacementSettle.ShapeKey("after", "icon", frameAtStart, lastAtStart, placedAtStart),
            PlacementSettle.ShapeKey("after", "icon", frameAtRest, lastAtRest, placedAtRest));

        // The old static frame (the root's rect widened by the strip) changes shape against the content: two placements.
        Assert.False(PlacementSettle.SameShape(new UiRect(0, 0, 672, 938), lastAtStart, frameAtRest, lastAtRest));
        Assert.NotEqual(
            PlacementSettle.ShapeKey("after", "icon", frameAtStart, lastAtStart, placedAtStart),
            PlacementSettle.ShapeKey("after", "text", frameAtStart, lastAtStart, placedAtStart));
    }

    [Fact]
    public void The_window_rect_is_the_moving_content_when_it_fills_the_window()
    {
        var root = new UiRect(0, 0, 620, 925);
        Assert.Equal(new UiRect(30, 0, 620, 925), VanillaPanelPlacement.WindowRect(root, new UiRect(30, 0, 620, 925)));
        // The stash's Background is 620 x 0: not the window.
        Assert.Equal(root, VanillaPanelPlacement.WindowRect(root, new UiRect(0, 0, 620, 0)));
        Assert.Equal(root, VanillaPanelPlacement.WindowRect(root, new UiRect(0, 0, 300, 925)));
        Assert.Equal(root, VanillaPanelPlacement.WindowRect(root, default));
    }

    [Theory]
    [InlineData(1, 1, "icon")]
    [InlineData(2, 2, "icon")]
    [InlineData(2, 1, "mixed")]
    [InlineData(2, 0, "text")]
    [InlineData(0, 0, "text")]
    public void The_face_word_says_what_the_visible_buttons_show(int visible, int icons, string word) =>
        Assert.Equal(word, VanillaPanelPlacement.FaceWord(visible, icons));

    [Fact]
    public void A_title_row_container_goes_after_the_outermost_cover()
    {
        // Devourer on 1.1.13.0, from the group's parent out: Name (no cover), the strip, HUDPanelBackground,
        // MotionRoot (HUDPanelBackground at 0, Blocker at 1 covers the title row), the window root.
        var devourer = new List<CoverLevel>
        {
            new(0, null, "Name"),
            new(0, null, "HUDPanelBackground"),
            new(0, null, "HUDPanelBackground"),
            new(0, new[] { 1 }, "MotionRoot"),
            new(0, null, "SalvagestationSubMenu"),
        };
        Assert.Equal((3, 2), VanillaPanelAnchors.AboveCovers(devourer));

        // The stash: Blocker comes before HUDPanelBackground, so nothing later covers the row.
        var stash = new List<CoverLevel> { new(0, null, "Name"), new(1, null, "Background") };
        Assert.Equal((-1, -1), VanillaPanelAnchors.AboveCovers(stash));

        // A cover before the chain child does not count; covers at two levels pick the outer one, after its last cover.
        Assert.Equal((-1, -1), VanillaPanelAnchors.AboveCovers(new List<CoverLevel> { new(3, new[] { 1, 2 }) }));
        Assert.Equal((1, 6), VanillaPanelAnchors.AboveCovers(new List<CoverLevel> { new(0, new[] { 2 }), new(1, new[] { 5, 3 }) }));
        Assert.Equal((-1, -1), VanillaPanelAnchors.AboveCovers(null!));
    }

    [Fact]
    public void The_resolver_puts_the_title_row_above_covers_and_reads_back_what_takes_the_click()
    {
        string resolver = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "VanillaPanelSiteResolver.cs");
        Assert.Contains("var above = TitleRowHost(parentT, view.Panel!.transform, reference, group, out int aboveIndex, out coverNote);", resolver);
        Assert.Contains("if (!g.enabled || !g.raycastTarget || !g.gameObject.activeInHierarchy) continue;".Replace("if (!g", "if (g == null || !g"), resolver);
        Assert.Contains("ReparentAboveCovers(host, view, group)", resolver);
        Assert.Contains("windowLocal = VanillaPanelPlacement.WindowRect(panelLocal, moving);", resolver);
        Assert.Contains("b[\"raycast\"] = RaycastAtCenter(tipped.Rect, tipped.GameObject);", resolver);
        Assert.Contains("b[\"interactable\"] = InteractableOf(tipped.GameObject);", resolver);
        Assert.Contains("d[\"placements\"] = placed.Placements;", resolver);
        Assert.Contains("d[\"hitsSelf\"]", resolver);
    }

    // ---------------------------------------------------------------- BesideRename

    [Fact]
    public void BesideRename_on_this_build_uses_the_rename_buttons_own_group_and_is_positioned()
    {
        var row = TitleRow();
        var group = VanillaPanelAnchors.RenameGroup(null, row.Rename);
        Assert.Same(row.Group, group.Node);
        Assert.Equal("OpenTagEditButton.parent", group.Source);

        // Joining a 24-unit right-aligned group that keeps child widths would push the rename button
        // left over the title; the container is placed after it instead.
        Assert.Equal(InlineMode.Positioned, VanillaPanelAnchors.PlanInline(group.Node));
        Assert.Equal("after", VanillaPanelAnchors.ModeWord(InlineMode.Positioned));
    }

    [Fact]
    public void A_build_that_sets_OpenTagEditButtonParent_keeps_it()
    {
        var row = TitleRow();
        var field = new Node("RenameGroup", 200, 36, layout: AnchorLayout.HorizontalControlled);
        var group = VanillaPanelAnchors.RenameGroup(field, row.Rename);
        Assert.Same(field, group.Node);
        Assert.Equal("OpenTagEditButtonParent", group.Source);
        Assert.Equal(InlineMode.Group, VanillaPanelAnchors.PlanInline(field));
        Assert.False(VanillaPanelAnchors.RenameGroup(null, null).Found);
        Assert.Equal(InlineMode.None, VanillaPanelAnchors.PlanInline(null));
    }

    [Fact]
    public void A_positioned_row_is_title_row_high_at_the_bar_buttons_proportions_after_the_rename_button()
    {
        float h = VanillaPanelAnchors.PositionedHeight(24, 36);
        Assert.Equal(24f, h);
        Assert.Equal(116f * 24f / 36f, VanillaPanelAnchors.ScaledWidth(116, 36, h), 3);
        Assert.Equal(36f, VanillaPanelAnchors.PositionedHeight(0, 36));
        Assert.Equal(VanillaPanelAnchors.MinPositionedHeight, VanillaPanelAnchors.PositionedHeight(10, 36));
        Assert.Equal(36f, VanillaPanelAnchors.PositionedHeight(48, 36));

        // The rename group's own spacing is 0: the gap is the 8 units the game leaves before it.
        Assert.Equal(VanillaPanelPlacement.DefaultSpacing, VanillaPanelAnchors.PositionedSpacing(0));
        Assert.Equal(10f, VanillaPanelAnchors.PositionedSpacing(10));

        // In the group's units (pivot at its left middle): the rename button spans x 0..24, y -12..12.
        var rename = new UiRect(0, -12, 24, 24);
        var placed = VanillaPanelPlacement.PlaceAfter(rename, 8, 80, 24);
        Assert.Equal(32f, placed.X);
        Assert.Equal(-12f, placed.Y);
        Assert.Equal(80f, placed.Width);
        Assert.False(placed.Overlaps(rename));
    }

    [Fact]
    public void The_rename_row_is_placed_after_the_rename_button_inside_the_title_strip()
    {
        // In the window's units: window 620x240; title strip 664x80 centered on its top edge; the
        // title text ends at 390, the rename group follows 8 later, 24x24.
        var window = new UiRect(0, 0, 620, 240);
        var strip = new UiRect(-22, 213, 664, 80);
        var rename = new UiRect(398, 245, 24, 24);
        var frame = VanillaPanelPlacement.InlineFrame(window, new List<UiRect> { new(250, 236, 140, 42), strip });
        var verdict = VanillaPanelPlacement.PlaceBeside(rename, default, allowAbove: false, 8, 77, 24, frame,
            new List<UiObstacle> { new("EditTagButton", rename) }, out var slot);
        Assert.Equal(VanillaFit.Fits, verdict.Fit);
        Assert.Equal(BesideSlot.After, slot);
        Assert.Equal(new UiRect(430, 245, 77, 24).ToString(), verdict.Rect.ToString());
    }

    [Fact]
    public void The_title_strip_is_the_frame_for_the_rename_row_and_the_window_for_the_bar()
    {
        // The container window is 620x240; its title strip is a 664x80 bar centered on the top edge,
        // so the rename row is above the window rect. Against the window alone it would always be
        // hidden as an overflow; against the strip it fits.
        var window = new UiRect(0, 0, 620, 240);
        var title = new UiRect(250, 236, 140, 42);
        var strip = new UiRect(-22, 213, 664, 80);
        var renameRow = new UiRect(422, 245, 80, 24);
        var renameButton = new UiRect(390, 245, 24, 24);

        Assert.Equal(VanillaFit.Blocked, VanillaPanelPlacement.CheckInline(renameRow, window, null).Fit);

        var frame = VanillaPanelPlacement.InlineFrame(window, new List<UiRect> { new(390, 245, 24, 24), title, strip });
        Assert.Equal(-22f, frame.X);
        Assert.Equal(293f, frame.YMax);
        var verdict = VanillaPanelPlacement.CheckInline(renameRow, frame, new List<UiObstacle> { new("EditTagButton", renameButton) });
        Assert.Equal(VanillaFit.Fits, verdict.Fit);

        // The bar's ancestors are no wider than the window: the frame stays the window.
        var inventory = new UiRect(0, 0, 620, 925);
        var body = new UiRect(8, 67, 604, 791);
        var barFrame = VanillaPanelPlacement.InlineFrame(inventory, new List<UiRect> { body });
        Assert.Equal(inventory.X, barFrame.X);
        Assert.Equal(inventory.XMax, barFrame.XMax);
        Assert.Equal(inventory.YMax, barFrame.YMax);
    }

    // ---------------------------------------------------------------- BesideTitleButtons (the castle heart's icon row)

    /// <summary>
    /// <c>CastleHeartSubMenu/MotionRoot/HeartTab/AllyWindow/LevelPanel/ForfeitButtonsParent</c>: a horizontal group
    /// anchored top-right that sizes its children (42-unit preferred width), holding the two 42 x 42 icon buttons
    /// <c>AbandonButton</c> and <c>StandardImageButton</c> (the field <c>ExposeButton</c>).
    /// </summary>
    private static (Node Level, Node Row, Node Abandon, Node Expose) HeartRow()
    {
        var level = new Node("LevelPanel", 604, 337);
        level.Add(new Node("LevelHeader", 96, 86));
        var row = level.Add(new Node("ForfeitButtonsParent", 92, 42, layout: AnchorLayout.HorizontalControlled));
        var abandon = row.Add(new Node("AbandonButton", 42, 42, button: true));
        var expose = row.Add(new Node("StandardImageButton", 42, 42, button: true));
        level.Add(new Node("CapacityGroup", 540, 90));
        return (level, row, abandon, expose);
    }

    [Fact]
    public void The_heart_icon_row_is_the_buttons_own_group_and_is_placed_not_joined()
    {
        var h = HeartRow();
        var (group, reference) = VanillaPanelAnchors.TitleButtonsRow(new IAnchorNode?[] { h.Abandon, h.Expose });
        Assert.Same(h.Row, group.Node);
        Assert.Equal("AbandonButton.parent", group.Source);
        Assert.Same(h.Abandon, reference.Node);
        Assert.Equal("AbandonButton", reference.Source);

        // The row sizes its children, so a generic inline plan would join it and the right-anchored row would push the
        // game's buttons left. The heart's anchor is always placed.
        Assert.Equal(InlineMode.Group, VanillaPanelAnchors.PlanInline(group.Node));
        Assert.Equal(InlineMode.Positioned, VanillaPanelAnchors.PlanTitleButtons(group.Node));
        Assert.Equal(InlineMode.None, VanillaPanelAnchors.PlanTitleButtons(null));
        Assert.Equal("before", VanillaPanelAnchors.ModeWord(InlineMode.Positioned, before: true));
        Assert.Equal("after", VanillaPanelAnchors.ModeWord(InlineMode.Positioned, before: false));
    }

    /// <summary>
    /// A node as the Unity half hands them out: a new wrapper object on every <see cref="IAnchorNode.Parent"/> and
    /// <see cref="IAnchorNode.Child"/> call, equal (not the same object) when it wraps the same transform.
    /// </summary>
    private sealed class Wrapper : IAnchorNode
    {
        internal Wrapper(Node inner) => Inner = inner;

        internal Node Inner { get; }

        public string Name => Inner.Name;

        public bool Active => Inner.Active;

        public bool IsButton => Inner.IsButton;

        public bool CloneSafe => Inner.CloneSafe;

        public float Width => Inner.Width;

        public float Height => Inner.Height;

        public AnchorLayout Layout => Inner.Layout;

        public IAnchorNode? Parent => Inner.Parent is Node p ? new Wrapper(p) : null;

        public int ChildCount => Inner.ChildCount;

        public IAnchorNode? Child(int index) => Inner.Child(index) is Node c ? new Wrapper(c) : null;

        public override bool Equals(object? obj) => obj is Wrapper w && ReferenceEquals(w.Inner, Inner);

        public override int GetHashCode() => Inner.GetHashCode();
    }

    [Fact]
    public void The_heart_row_is_found_when_each_parent_read_is_a_new_wrapper()
    {
        // Both buttons' parents are fresh wrapper objects of one row: the second button still belongs to it.
        var h = HeartRow();
        h.Abandon.Active = false;
        var abandon = new Wrapper(h.Abandon);
        var expose = new Wrapper(h.Expose);
        Assert.NotSame(abandon.Parent, expose.Parent);
        var (group, reference) = VanillaPanelAnchors.TitleButtonsRow(new IAnchorNode?[] { abandon, expose });
        Assert.Equal(new Wrapper(h.Row), group.Node);
        Assert.Same(expose, reference.Node);
        Assert.Equal("ExposeButton", reference.Source);

        // A button in another group is not part of the row.
        var other = new Node("Elsewhere", 100, 42, layout: AnchorLayout.HorizontalControlled);
        var stray = other.Add(new Node("StrayButton", 42, 42, button: true));
        var pick = VanillaPanelAnchors.TitleButtonsRow(new IAnchorNode?[] { abandon, new Wrapper(stray) });
        Assert.Equal(new Wrapper(h.Row), pick.Group.Node);
        Assert.Same(abandon, pick.Reference.Node); // the only row member, inactive: still the reference
    }

    [Fact]
    public void A_hidden_first_button_makes_the_next_one_the_reference_and_no_buttons_is_no_row()
    {
        var h = HeartRow();
        h.Abandon.Active = false;
        var (group, reference) = VanillaPanelAnchors.TitleButtonsRow(new IAnchorNode?[] { h.Abandon, h.Expose });
        Assert.Same(h.Row, group.Node);
        Assert.Same(h.Expose, reference.Node);
        Assert.Equal("ExposeButton", reference.Source);

        // Both hidden (an enemy castle's window shows no ally row): still the row, the first button the reference.
        h.Expose.Active = false;
        Assert.Same(h.Abandon, VanillaPanelAnchors.TitleButtonsRow(new IAnchorNode?[] { h.Abandon, h.Expose }).Reference.Node);

        Assert.False(VanillaPanelAnchors.TitleButtonsRow(new IAnchorNode?[] { null, null }).Group.Found);
        Assert.False(VanillaPanelAnchors.TitleButtonsRow(null).Group.Found);
        Assert.False(VanillaPanelAnchors.TitleButtonsRow(new IAnchorNode?[] { new Node("NotAButton", 42, 42) }).Group.Found);
    }

    [Fact]
    public void The_heart_row_icon_buttons_go_left_of_the_games_and_a_second_plugins_go_left_of_the_first()
    {
        // In the heart window's units (620 x 925, origin bottom-left): the ally window insets 8 a side and starts 40 down,
        // the row sits 8 in from its right edge and 16 down: the game's buttons at x 512..554 and 562..604, y 827..869.
        var frame = new UiRect(0, 0, 620, 925);
        var abandon = new UiRect(512, 827, 42, 42);
        var expose = new UiRect(562, 827, 42, 42);
        var header = new UiRect(262, 806, 96, 86);
        var obstacles = new List<UiObstacle> { new("AbandonButton", abandon), new("StandardImageButton", expose), new("LevelHeader", header) };

        // One 42-unit icon button: 8 units left of the leftmost game button, on its row.
        var first = VanillaPanelPlacement.PlaceBeforeRow(abandon, 8, 42, 42, frame, obstacles, out var slot);
        Assert.Equal(VanillaFit.Fits, first.Fit);
        Assert.Equal(BesideSlot.Before, slot);
        Assert.Equal(new UiRect(462, 827, 42, 42).ToString(), first.Rect.ToString());
        Assert.True(first.Retry);

        // Another plugin's container, attached later, stacks left of the first one instead of drawing over it.
        var reference = VanillaPanelPlacement.StackReference(abandon, new List<UiRect> { first.Rect }, before: true);
        Assert.Equal(first.Rect.ToString(), reference.ToString());
        var second = VanillaPanelPlacement.PlaceBeforeRow(reference, 8, 92, 42, frame, obstacles, out slot);
        Assert.Equal(VanillaFit.Fits, second.Fit);
        Assert.Equal(new UiRect(362, 827, 92, 42).ToString(), second.Rect.ToString());
        Assert.False(second.Rect.Overlaps(first.Rect));

        // A row so wide it would reach the level header is hidden, never drawn over it.
        var wide = VanillaPanelPlacement.PlaceBeforeRow(reference, 8, 120, 42, frame, obstacles, out slot);
        Assert.Equal(VanillaFit.Blocked, wide.Fit);
        Assert.Equal("overlap:LevelHeader", wide.Reason);
        Assert.Equal(BesideSlot.None, slot);
    }

    [Fact]
    public void Stacking_skips_hidden_containers_and_goes_the_right_way()
    {
        var member = new UiRect(100, 0, 24, 24);
        var a = new UiRect(132, 0, 24, 24);
        var b = new UiRect(164, 0, 40, 24);
        Assert.Equal(b.ToString(), VanillaPanelPlacement.StackReference(member, new List<UiRect> { a, default, b }, before: false).ToString());
        Assert.Equal(member.ToString(), VanillaPanelPlacement.StackReference(member, new List<UiRect> { a, b }, before: true).ToString());
        Assert.Equal(member.ToString(), VanillaPanelPlacement.StackReference(member, null, before: false).ToString());

        // Edge columns: under the lowest earlier column, by the spacing; a hidden (empty) one leaves no gap.
        Assert.Equal(500f, VanillaPanelPlacement.StackedEdgeTop(500f, null, 8f));
        Assert.Equal(500f, VanillaPanelPlacement.StackedEdgeTop(500f, new List<UiRect> { default }, 8f));
        Assert.Equal(412f, VanillaPanelPlacement.StackedEdgeTop(500f, new List<UiRect> { new(628, 456, 116, 44), new(628, 420, 116, 80) }, 8f));
    }
}
