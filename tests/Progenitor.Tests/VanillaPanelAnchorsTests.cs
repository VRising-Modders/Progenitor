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

    // The character inventory beside a chest on 1.1.13.0, in the window parent's units, from the
    // live site line (inventory (5,3 620x925), bar (223,67 124x36)) and the shipped prefab: the bar
    // sits 30 units left of the window's center, its four 116x36 cells 8 apart are centered on it,
    // and the item area (ParentContainerInventory: top-pivoted 442 below the body's top, 275 high,
    // 12 in from each side of a body inset 8 per side) ends 38 units above the bar.
    private static readonly UiRect Inventory = new(5, 3, 620, 925);
    private static readonly UiRect DropAllCell = new(41, 67, 116, 36);
    private static readonly UiRect SortCell = new(165, 67, 116, 36);
    private static readonly UiRect SmartTakeCell = new(289, 67, 116, 36);
    private static readonly UiRect SmartMergeCell = new(413, 67, 116, 36);
    private static readonly UiRect ItemArea = new(25, 141, 580, 275);

    private static List<UiObstacle> BarObstacles() => new()
    {
        new("DropAllButton", DropAllCell), new("SortButtonNew", SortCell), new("SmartTakeButton", SmartTakeCell),
        new("SmartMergeButtonNew", SmartMergeCell), new("ParentContainerInventory", ItemArea),
    };

    [Fact]
    public void The_measured_bar_is_off_center_and_its_cells_are_where_the_grid_puts_them()
    {
        var bar = new UiRect(223, 67, 124, 36);
        float barCenter = bar.X + bar.Width / 2f;
        Assert.Equal(-30f, barCenter - (Inventory.X + Inventory.Width / 2f));
        Assert.Equal(barCenter - (4 * 116f + 3 * 8f) / 2f, DropAllCell.X);
        Assert.Equal(DropAllCell.XMax + 8f, SortCell.X);
        Assert.Equal(SmartMergeCell.XMax, barCenter + 244f);
    }

    [Fact]
    public void A_one_word_BesideSort_button_goes_right_of_SmartMerge_inside_the_window()
    {
        // 88 units are left between SmartMerge (ends at 529) plus the 8-unit gap and the window's
        // right edge (625): a 72-unit "Stash" fits; the old cell-wide 116 never could.
        var verdict = VanillaPanelPlacement.PlaceBeside(SmartMergeCell, SortCell, allowAbove: true, 8, 72, 36,
            Inventory, BarObstacles(), out var slot);
        Assert.Equal(VanillaFit.Fits, verdict.Fit);
        Assert.Equal(BesideSlot.After, slot);
        Assert.Equal(537f, verdict.Rect.X);
        Assert.Equal(67f, verdict.Rect.Y);
        Assert.True(verdict.Rect.XMax <= Inventory.XMax);

        // The widest caption that still fits the slot.
        verdict = VanillaPanelPlacement.PlaceBeside(SmartMergeCell, SortCell, allowAbove: true, 8, 88, 36,
            Inventory, BarObstacles(), out slot);
        Assert.Equal(BesideSlot.After, slot);
    }

    [Fact]
    public void A_wide_BesideSort_button_fits_neither_slot_on_this_build_and_is_hidden_with_both_reasons()
    {
        // Right: past the window edge. Above the sort button: the 38-unit gap under the item area
        // cannot take a 36-unit button and an 8-unit gap, so it would cover the items by 6 units.
        var verdict = VanillaPanelPlacement.PlaceBeside(SmartMergeCell, SortCell, allowAbove: true, 8, 116, 36,
            Inventory, BarObstacles(), out var slot);
        Assert.Equal(VanillaFit.Blocked, verdict.Fit);
        Assert.Equal(BesideSlot.None, slot);
        Assert.Equal("overflow:mod/overlap:ParentContainerInventory", verdict.Reason);
    }

    [Fact]
    public void Three_buttons_on_one_BesideSort_handle_are_one_row_that_fits_neither_slot_on_this_build()
    {
        // "Stash", "Inventory", "Find": 72 + 115 + 72 with 8 between = 275 units. Right of the bar
        // there are 88; above the sort button the row would cover the item area. Hidden, with both reasons.
        float row = 72 + 115 + 72 + 2 * 8;
        var verdict = VanillaPanelPlacement.PlaceBeside(SmartMergeCell, SortCell, allowAbove: true, 8, row, 36,
            Inventory, BarObstacles(), out var slot);
        Assert.Equal(VanillaFit.Blocked, verdict.Fit);
        Assert.Equal(BesideSlot.None, slot);
        Assert.Equal("overflow:mod/overlap:ParentContainerInventory", verdict.Reason);
    }

    [Fact]
    public void Above_the_sort_button_is_taken_when_the_right_slot_is_full_and_the_space_above_is_free()
    {
        // A build with more room above the bar: the button centers over the sort button.
        var obstacles = BarObstacles();
        obstacles[4] = new UiObstacle("ParentContainerInventory", new UiRect(25, 160, 580, 275));
        var verdict = VanillaPanelPlacement.PlaceBeside(SmartMergeCell, SortCell, allowAbove: true, 8, 116, 36,
            Inventory, obstacles, out var slot);
        Assert.Equal(VanillaFit.Fits, verdict.Fit);
        Assert.Equal(BesideSlot.Above, slot);
        Assert.Equal(new UiRect(165, 111, 116, 36).ToString(), verdict.Rect.ToString());
        foreach (var o in obstacles) Assert.False(verdict.Rect.Overlaps(o.Rect), o.Name);

        // The rename row never tries above: one slot only.
        VanillaPanelPlacement.PlaceBeside(SmartMergeCell, SortCell, allowAbove: false, 8, 116, 36, Inventory, obstacles, out slot);
        Assert.Equal(BesideSlot.None, slot);
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
}
