using System;
using System.Collections.Generic;

namespace Progenitor.Ui;

/// <summary>How a vanilla parent lays out its children, as far as an inline anchor cares.</summary>
internal enum AnchorLayout
{
    /// <summary>No layout group: children sit where their own rects put them.</summary>
    None = 0,

    /// <summary>A <c>GridLayoutGroup</c>: every child is one cell of the group's fixed cell size.</summary>
    Grid = 1,

    /// <summary>A horizontal group that sets its children's widths (<c>childControlWidth</c>).</summary>
    HorizontalControlled = 2,

    /// <summary>A horizontal group that keeps each child's own width (<c>childControlWidth</c> off).</summary>
    HorizontalFree = 3,

    /// <summary>A vertical group.</summary>
    Vertical = 4,
}

/// <summary>How an inline anchor's container joins its vanilla group.</summary>
internal enum InlineMode
{
    /// <summary>No group was found: the anchor is withheld.</summary>
    None = 0,

    /// <summary>
    /// One more cell of a <c>GridLayoutGroup</c>, right after the reference control. The grid sizes
    /// the container to its cell, exactly like the game's own buttons in it.
    /// </summary>
    GridCell = 1,

    /// <summary>One more member of a horizontal group that sizes its children (asks for its width).</summary>
    Group = 2,

    /// <summary>
    /// Ignored by the group (<c>LayoutElement.ignoreLayout</c>) and placed by Progenitor right after
    /// the group's last visible member, on its row. Used where joining the group would move a vanilla
    /// control: a fixed-size group that does not size its children shifts its members sideways when
    /// a wider child arrives (the rename group on this build is 24 units wide and right-aligned, so
    /// a joined button would push the rename button over the window title).
    /// </summary>
    Positioned = 3,
}

/// <summary>
/// One node of a UI hierarchy, as the anchor resolver sees it. The Unity half wraps a
/// <c>Transform</c>; tests build a fake tree that mirrors the game's prefab.
/// </summary>
internal interface IAnchorNode
{
    string Name { get; }

    /// <summary><c>activeInHierarchy</c>.</summary>
    bool Active { get; }

    /// <summary>Carries the game's button component (<c>SimpleStunButton</c>).</summary>
    bool IsButton { get; }

    /// <summary>The rect's own size, in its parent's units (0 when unknown).</summary>
    float Width { get; }

    float Height { get; }

    AnchorLayout Layout { get; }

    IAnchorNode? Parent { get; }

    int ChildCount { get; }

    IAnchorNode? Child(int index);
}

/// <summary>A resolved control and the field (or rule) that produced it, for the log and <c>ui_read</c>.</summary>
internal readonly struct AnchorPick
{
    internal AnchorPick(IAnchorNode? node, string source)
    {
        Node = node;
        Source = source ?? "";
    }

    internal IAnchorNode? Node { get; }

    /// <summary><c>SelfSortButton</c>, <c>AllSortButton</c>, <c>bar:SortButtonNew</c>, <c>OpenTagEditButton.parent</c>, ...</summary>
    internal string Source { get; }

    internal bool Found => Node != null;

    internal static AnchorPick None => new(null, "");
}

/// <summary>
/// The game-free half of finding where a P6 anchor goes and what its buttons are cloned from, on
/// the hierarchy the game actually ships. Every rule here exists because a serialized reference the
/// first design relied on is empty on game build 1.1.13.0:
///
/// <list type="bullet">
/// <item><description><c>InventorySubMenu.SelfSortButton</c> is not set in the shipped inventory prefab.
/// The bottom bar (<c>BottomBarButtonsParent</c>, the <c>ButtonsLayoutNew</c> object) is a
/// <c>GridLayoutGroup</c> of 116x36 cells with 8 units of spacing holding four icon buttons in this
/// order: <c>DropAllButton</c>, <c>SortButtonNew</c> (the field <c>AllSortButton</c>),
/// <c>SmartTakeButton</c>, <c>SmartMergeButtonNew</c> (<c>SmartMergeButton</c>).</description></item>
/// <item><description><c>ContainerSubMenu.OpenTagEditButtonParent</c> is not set either. The rename button
/// (<c>OpenTagEditButton</c>, the <c>EditTagButton</c> object, 24x24) sits in <c>ButtonLayoutParent</c>,
/// a 24x24 right-aligned horizontal group that does not size its children, anchored to the right edge
/// of the window title.</description></item>
/// </list>
///
/// The Unity half (<c>VanillaPanelSiteResolver.cs</c>) reads the fields and wraps transforms; the
/// choices are made here so a fake hierarchy can test them.
/// </summary>
internal static class VanillaPanelAnchors
{
    /// <summary>
    /// The inventory's bottom-bar button fields, in the order a button donor is taken from them. The
    /// first is the original design's donor (empty on 1.1.13.0); the rest are its siblings on the bar,
    /// all the same art and size.
    /// </summary>
    internal static readonly string[] ButtonDonorFields =
    {
        "SelfSortButton", "AllSortButton", "SmartMergeButton", "SmartTakeButton", "DropAllButton",
    };

    /// <summary>
    /// The button every P6 button is cloned from: the first named field that is a live game button
    /// (an active one preferred), else the first button child of the bottom bar. The bar is what the
    /// buttons are meant to match, so a donor from anywhere else is not taken.
    /// </summary>
    /// <param name="named">The values of <see cref="ButtonDonorFields"/> in that order (null when unset).</param>
    /// <param name="bar">The bottom bar (<c>BottomBarButtonsParent</c>), or null.</param>
    internal static AnchorPick PickButtonDonor(IReadOnlyList<IAnchorNode?> named, IAnchorNode? bar)
    {
        AnchorPick inactive = AnchorPick.None;
        int n = Math.Min(named?.Count ?? 0, ButtonDonorFields.Length);
        for (int i = 0; i < n; i++)
        {
            var node = named![i];
            if (node == null || !node.IsButton) continue;
            if (node.Active) return new AnchorPick(node, ButtonDonorFields[i]);
            if (!inactive.Found) inactive = new AnchorPick(node, ButtonDonorFields[i]);
        }

        if (inactive.Found) return inactive;
        if (bar == null) return AnchorPick.None;
        for (int i = 0; i < bar.ChildCount; i++)
        {
            var child = bar.Child(i);
            if (child != null && child.IsButton) return new AnchorPick(child, "bar:" + child.Name);
        }

        return AnchorPick.None;
    }

    /// <summary>
    /// The sort control <see cref="VanillaPanelAnchor.BesideSort"/> goes beside: <c>SelfSortButton</c>
    /// when the game sets it, else <c>AllSortButton</c> (the bar's only sort button on 1.1.13.0), else a
    /// button on the bar whose name says sort.
    /// </summary>
    internal static AnchorPick SortControl(IAnchorNode? selfSort, IAnchorNode? allSort, IAnchorNode? bar)
    {
        if (selfSort != null && selfSort.IsButton) return new AnchorPick(selfSort, "SelfSortButton");
        if (allSort != null && allSort.IsButton) return new AnchorPick(allSort, "AllSortButton");
        if (bar == null) return AnchorPick.None;
        for (int i = 0; i < bar.ChildCount; i++)
        {
            var child = bar.Child(i);
            if (child != null && child.IsButton && child.Name.IndexOf("Sort", StringComparison.OrdinalIgnoreCase) >= 0)
                return new AnchorPick(child, "bar:" + child.Name);
        }

        return AnchorPick.None;
    }

    /// <summary>
    /// The group <see cref="VanillaPanelAnchor.BesideRename"/> joins: <c>OpenTagEditButtonParent</c>
    /// when the game sets it, else the rename button's own parent (the case on 1.1.13.0).
    /// </summary>
    internal static AnchorPick RenameGroup(IAnchorNode? parentField, IAnchorNode? renameButton)
    {
        if (parentField != null) return new AnchorPick(parentField, "OpenTagEditButtonParent");
        if (renameButton?.Parent != null) return new AnchorPick(renameButton.Parent, "OpenTagEditButton.parent");
        return AnchorPick.None;
    }

    /// <summary>How an inline container joins <paramref name="group"/>.</summary>
    internal static InlineMode PlanInline(IAnchorNode? group) => group == null
        ? InlineMode.None
        : group.Layout switch
        {
            AnchorLayout.Grid => InlineMode.GridCell,
            AnchorLayout.HorizontalControlled => InlineMode.Group,
            _ => InlineMode.Positioned,
        };

    /// <summary>The word the log line and <c>ui_read</c> use for a mode.</summary>
    internal static string ModeWord(InlineMode mode) => mode switch
    {
        InlineMode.GridCell => "grid",
        InlineMode.Group => "group",
        InlineMode.Positioned => "after",
        _ => "none",
    };

    /// <summary>
    /// What a positioned container is placed after: the last visible member of
    /// <paramref name="group"/> before sibling <paramref name="ourIndex"/>, or null (then the group's
    /// own rect). Only members before ours count, so two plugins' containers in one group chain one
    /// after the other instead of each placing itself after the other.
    /// </summary>
    internal static int LastVisibleBefore(IAnchorNode group, int ourIndex)
    {
        int from = Math.Min(ourIndex, group.ChildCount) - 1;
        for (int i = from; i >= 0; i--)
        {
            var child = group.Child(i);
            if (child != null && child.Active) return i;
        }

        return -1;
    }

    /// <summary>
    /// The height of a positioned row: the reference control's own height (the rename button's 24
    /// units: the title row's controls), never taller than the donor and never shorter than
    /// <see cref="MinPositionedHeight"/>, so a caption stays readable.
    /// </summary>
    internal static float PositionedHeight(float referenceHeight, float donorHeight)
    {
        float donor = donorHeight > 1f ? donorHeight : 34f;
        float h = referenceHeight > 1f ? Math.Min(referenceHeight, donor) : donor;
        return Math.Max(MinPositionedHeight, h);
    }

    /// <summary>Smallest positioned row, in canvas units.</summary>
    internal const float MinPositionedHeight = 20f;

    /// <summary>
    /// The minimum width of a button of <paramref name="height"/>: the donor's own proportions, so a
    /// shorter button in the title row keeps the bar button's shape before its caption widens it.
    /// </summary>
    internal static float ScaledWidth(float donorWidth, float donorHeight, float height)
    {
        if (donorWidth <= 1f || donorHeight <= 1f) return height * 3f;
        return donorWidth * height / donorHeight;
    }

    /// <summary>
    /// The gap a positioned container keeps from the group's last member: the group's own spacing
    /// when it has a visible one, else <see cref="VanillaPanelPlacement.DefaultSpacing"/> (the rename
    /// group's spacing is 0, and 8 is the gap the game leaves between the title and that group).
    /// </summary>
    internal static float PositionedSpacing(float groupSpacing) =>
        groupSpacing >= 4f ? groupSpacing : VanillaPanelPlacement.DefaultSpacing;
}
