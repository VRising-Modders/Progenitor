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

    /// <summary>
    /// A button that can be cloned on its own: every graphic its state transitions drive (the
    /// target graphic and each transition target) lies inside its own subtree. A clone of a button
    /// whose transitions drive a sibling's graphic would keep driving that sibling.
    /// </summary>
    bool CloneSafe { get; }

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
    /// The button every P6 button is cloned from: the first named field that is a live,
    /// <see cref="IAnchorNode.CloneSafe"/> game button (an active one preferred), else the first such
    /// button on the bottom bar. The bar is what the buttons are meant to match, so a donor from
    /// anywhere else is not taken.
    ///
    /// <para>On 1.1.13.0 three of the four bar buttons (<c>SortButtonNew</c>, <c>SmartTakeButton</c>,
    /// <c>SmartMergeButtonNew</c>) have the <i>drop-all</i> button's image as their target graphic, so
    /// only <c>DropAllButton</c> is clone-safe and it is the donor. Taking <c>AllSortButton</c> left
    /// every clone refused and no button drawn.</para>
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
            if (node == null || !node.IsButton || !node.CloneSafe) continue;
            if (node.Active) return new AnchorPick(node, ButtonDonorFields[i]);
            if (!inactive.Found) inactive = new AnchorPick(node, ButtonDonorFields[i]);
        }

        if (inactive.Found) return inactive;
        if (bar == null) return AnchorPick.None;
        for (int i = 0; i < bar.ChildCount; i++)
        {
            var child = bar.Child(i);
            if (child != null && child.IsButton && child.CloneSafe) return new AnchorPick(child, "bar:" + child.Name);
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

    /// <summary>
    /// How an inline container joins <paramref name="group"/>. <paramref name="besideSort"/> is the
    /// inventory bar's anchor, which is always placed and never joined: on 1.1.13.0 the bar is a
    /// centered grid, and one more cell recenters the row, moving all four game buttons by half a
    /// cell (62 units). Game buttons never move for a mod's control.
    /// </summary>
    internal static InlineMode PlanInline(IAnchorNode? group, bool besideSort = false) => group == null
        ? InlineMode.None
        : besideSort
            ? InlineMode.Positioned
            : group.Layout switch
            {
                AnchorLayout.Grid => InlineMode.GridCell,
                AnchorLayout.HorizontalControlled => InlineMode.Group,
                _ => InlineMode.Positioned,
            };

    /// <summary>
    /// The fields of the castle heart window's top-right icon row, in the order the row is read from:
    /// <c>CastleHeartLevelPanel.AbandonButton</c> and <c>.ExposeButton</c> (<c>CastleHeartSubMenu.AllyWindow.LevelPanel</c>).
    /// On 1.1.13.0 both sit in <c>ForfeitButtonsParent</c>: a horizontal group anchored to the level panel's top-right
    /// corner (pivot at its right, aligned upper right, spacing 8) that sizes its children to their 42-unit preferred
    /// width and is sized to them, holding the two 42 x 42 icon buttons (<c>Icon_Castle</c>, <c>Icon_ExposeCastle</c>, 24 x 24).
    /// </summary>
    internal static readonly string[] TitleButtonFields = { "AbandonButton", "ExposeButton" };

    /// <summary>
    /// The row <see cref="VanillaPanelAnchor.BesideTitleButtons"/> goes beside, and the button that is its reference: the
    /// parent of the first of <paramref name="buttons"/> (<see cref="TitleButtonFields"/> order) that is a game button,
    /// and of those in that row the first active one (the leftmost the game shows), else the first found. The reference
    /// is also the button donor, when it is clone-safe: the mod's buttons then are the row's own art and size.
    /// </summary>
    internal static (AnchorPick Group, AnchorPick Reference) TitleButtonsRow(IReadOnlyList<IAnchorNode?>? buttons)
    {
        int n = Math.Min(buttons?.Count ?? 0, TitleButtonFields.Length);
        IAnchorNode? group = null;
        AnchorPick reference = AnchorPick.None, inactive = AnchorPick.None;
        for (int i = 0; i < n; i++)
        {
            var b = buttons![i];
            if (b == null || !b.IsButton || b.Parent == null) continue;
            if (group == null) group = b.Parent;
            else if (!Equals(b.Parent, group)) continue;
            if (b.Active && !reference.Found) reference = new AnchorPick(b, TitleButtonFields[i]);
            if (!inactive.Found) inactive = new AnchorPick(b, TitleButtonFields[i]);
        }

        if (group == null) return (AnchorPick.None, AnchorPick.None);
        var refPick = reference.Found ? reference : inactive;
        return (new AnchorPick(group, refPick.Source + ".parent"), refPick);
    }

    /// <summary>
    /// How a <see cref="VanillaPanelAnchor.BesideTitleButtons"/> container goes beside its row: always placed, never
    /// joined. The heart's row sizes its children, so <see cref="PlanInline"/> would join it; joined, the row (pivot at
    /// its right) would grow and move the game's buttons left. Placed, nothing of the game's moves.
    /// </summary>
    internal static InlineMode PlanTitleButtons(IAnchorNode? group) => group == null ? InlineMode.None : InlineMode.Positioned;

    /// <summary>The <c>mode=</c> word for a placed container that goes left of its reference: <c>before</c>.</summary>
    internal static string ModeWord(InlineMode mode, bool before) =>
        before && mode == InlineMode.Positioned ? "before" : ModeWord(mode);

    /// <summary>The word the log line and <c>ui_read</c> use for a mode.</summary>
    internal static string ModeWord(InlineMode mode) => mode switch
    {
        InlineMode.GridCell => "grid",
        InlineMode.Group => "group",
        InlineMode.Positioned => "after",
        _ => "none",
    };

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
    /// The minimum width of a <see cref="VanillaPanelAnchor.BesideSort"/> button: twice its height
    /// (72 units on the 36-unit bar), widened only as far as its caption needs. Not the bar cell's
    /// 116: the slot right of the bar is 88 units wide on 1.1.13.0, so a cell-wide button never fits
    /// there, while a one-word caption does.
    /// </summary>
    internal static float CompactWidth(float height) => Math.Max(MinPositionedHeight, height) * 2f;

    /// <summary>
    /// How far a placed row's captions may shrink to fit its slot: 0.75 of the game's size, which
    /// is 12 units for the 16-unit label the buttons clone. Below that a caption stops reading as the
    /// game's own, so the row is hidden instead.
    /// </summary>
    internal const float MinCaptionScale = 0.75f;

    /// <summary>A button's narrowest width: its caption at <see cref="MinCaptionScale"/> plus the padding, at least 36.</summary>
    internal static float MinButtonWidth(float captionWidth, float padding, float height) =>
        Math.Max(36f, captionWidth * MinCaptionScale + 2f * padding);

    /// <summary>
    /// The gap a positioned container keeps from the group's last member: the group's own spacing
    /// when it has a visible one, else <see cref="VanillaPanelPlacement.DefaultSpacing"/> (the rename
    /// group's spacing is 0, and 8 is the gap the game leaves between the title and that group).
    /// </summary>
    internal static float PositionedSpacing(float groupSpacing) =>
        groupSpacing >= 4f ? groupSpacing : VanillaPanelPlacement.DefaultSpacing;

    /// <summary>
    /// Where a placed title-row container goes so that nothing the game draws later covers it. uGUI hands a click to
    /// the last-drawn raycast target under the pointer, and a later sibling draws over a whole earlier subtree: on
    /// 1.1.13.0 the devourer's and the workstation's <c>MotionRoot/Blocker</c> (a transparent, raycast-target image
    /// that covers the window and its title strip) comes after <c>HUDPanelBackground</c>, which holds the title row,
    /// so a container inside the title row never gets a pointer event. <paramref name="levels"/> runs from the group's
    /// parent (index 0) out to the window root's child level; each says which child is on the chain to the group and
    /// which later siblings are active and cover the rename button with a raycast target. The answer is the outermost
    /// level with such a cover and the index right after its last cover, or level -1 when nothing covers the row (the
    /// container stays the last child of the group's parent, as before).
    /// </summary>
    internal static (int Level, int SiblingIndex) AboveCovers(IReadOnlyList<CoverLevel> levels)
    {
        if (levels == null) return (-1, -1);
        for (int i = levels.Count - 1; i >= 0; i--)
        {
            var l = levels[i];
            int last = -1;
            if (l.Covers != null)
                foreach (int c in l.Covers)
                    if (c > l.ChainIndex && c > last) last = c;
            if (last >= 0) return (i, last + 1);
        }

        return (-1, -1);
    }
}

/// <summary>One level of <see cref="VanillaPanelAnchors.AboveCovers"/>: the chain child's index and the covering later siblings.</summary>
internal readonly struct CoverLevel
{
    internal CoverLevel(int chainIndex, IReadOnlyList<int>? covers, string name = "")
    {
        ChainIndex = chainIndex;
        Covers = covers;
        Name = name ?? "";
    }

    /// <summary>The index, at this level, of the child that holds the rename group.</summary>
    internal int ChainIndex { get; }

    /// <summary>Indices of later siblings that are active and cover the rename button with a raycast target.</summary>
    internal IReadOnlyList<int>? Covers { get; }

    /// <summary>The level's object name, for the log line.</summary>
    internal string Name { get; }
}
