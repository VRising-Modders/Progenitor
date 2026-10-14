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
            AnchorLayout layout = AnchorLayout.None)
        {
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
    /// 116x36 cells, spacing 8, holding four icon buttons in this order.
    /// </summary>
    private static (Node Bar, Node DropAll, Node Sort, Node SmartTake, Node SmartMerge) Bar()
    {
        var bar = new Node("ButtonsLayoutNew", 124, 36, layout: AnchorLayout.Grid);
        var dropAll = bar.Add(new Node("DropAllButton", 116, 36, button: true));
        var sort = bar.Add(new Node("SortButtonNew", 116, 36, button: true));
        var take = bar.Add(new Node("SmartTakeButton", 116, 36, button: true));
        var merge = bar.Add(new Node("SmartMergeButtonNew", 116, 36, button: true));
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
    public void The_donor_on_this_build_is_the_bar_sort_button_because_SelfSortButton_is_empty()
    {
        var b = Bar();
        var pick = VanillaPanelAnchors.PickButtonDonor(FieldsOf(b), b.Bar);
        Assert.Same(b.Sort, pick.Node);
        Assert.Equal("AllSortButton", pick.Source);
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
    public void BesideSort_on_this_build_is_one_more_grid_cell_after_the_bar_sort_button()
    {
        var b = Bar();
        var sort = VanillaPanelAnchors.SortControl(null, b.Sort, b.Bar);
        Assert.Same(b.Sort, sort.Node);
        Assert.Equal("AllSortButton", sort.Source);
        Assert.Same(b.Bar, sort.Node!.Parent);
        Assert.Equal(InlineMode.GridCell, VanillaPanelAnchors.PlanInline(sort.Node.Parent));
        Assert.Equal("grid", VanillaPanelAnchors.ModeWord(InlineMode.GridCell));
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

    [Fact]
    public void Five_bar_cells_still_fit_the_inventory_frame_and_six_do_not()
    {
        // The grid centers its cells: 4 game buttons + ours = 5 cells of 116 with 8 between = 612,
        // inside the 620-unit inventory window. A second plugin's cell makes 736: overflow, hidden.
        var panel = new UiRect(0, 0, 620, 925);
        var five = Cells(5, 620);
        var verdict = VanillaPanelPlacement.CheckInline(five[4].Rect, panel, five.GetRange(0, 4));
        Assert.Equal(VanillaFit.Fits, verdict.Fit);

        var six = Cells(6, 620);
        verdict = VanillaPanelPlacement.CheckInline(six[4].Rect, panel, new List<UiObstacle> { six[0], six[1], six[2], six[3], six[5] });
        Assert.Equal(VanillaFit.Blocked, verdict.Fit);
    }

    private static List<UiObstacle> Cells(int n, float panelWidth)
    {
        float total = n * 116f + (n - 1) * 8f;
        float x = (panelWidth - total) / 2f;
        var list = new List<UiObstacle>();
        for (int i = 0; i < n; i++) list.Add(new UiObstacle("cell" + i, new UiRect(x + i * 124f, 20, 116, 36)));
        return list;
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
    public void Two_positioned_containers_chain_instead_of_leapfrogging()
    {
        var row = TitleRow();
        row.Group.Add(new Node("progenitor-attach-a"));
        row.Group.Add(new Node("progenitor-attach-b"));
        Assert.Equal(0, VanillaPanelAnchors.LastVisibleBefore(row.Group, 1));
        Assert.Equal(1, VanillaPanelAnchors.LastVisibleBefore(row.Group, 2));

        row.Rename.Active = false;
        Assert.Equal(-1, VanillaPanelAnchors.LastVisibleBefore(row.Group, 1));
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
