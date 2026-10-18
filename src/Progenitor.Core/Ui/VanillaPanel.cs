using System;
using System.Collections.Generic;
using System.Globalization;

namespace Progenitor.Ui;

/// <summary>
/// Where on a vanilla panel an attachment sits (spec P6). Which anchors a site accepts is part of
/// the site's catalog entry (<see cref="VanillaUiSites"/>); an anchor a site does not list gives a
/// disabled handle and one log line, the same answer an undefined site gets.
/// </summary>
public enum VanillaPanelAnchor
{
    /// <summary>The site's own default: the first anchor its catalog entry lists.</summary>
    Default = 0,

    /// <summary>
    /// A column of mod buttons and rows outside the panel's left frame edge, top-aligned with the
    /// panel's item grid. Placed by Progenitor, never laid out by the game, and hidden (with one log
    /// line) when it would leave the screen or overlap another vanilla panel.
    /// </summary>
    LeftEdge = 1,

    /// <summary>The same column outside the panel's right frame edge.</summary>
    RightEdge = 2,

    /// <summary>
    /// Mod buttons in the title row, right after the panel's rename button. On game build 1.1.13.0
    /// the rename button's group is a fixed 24-unit group beside the window title that does not size
    /// its children, so the buttons are placed after it on its row (title-row height, the bar
    /// button's art) rather than joined to it, which would push the rename button over the title.
    /// </summary>
    BesideRename = 3,

    /// <summary>
    /// Mod buttons in the inventory's bottom button bar, right after the sort button. On game build
    /// 1.1.13.0 the bar is a grid of 116x36 cells; the container is one more cell, sized and spaced by
    /// the grid like the game's own buttons (one button per cell reads best: several buttons share it).
    /// </summary>
    BesideSort = 4,

    /// <summary>
    /// The tooltip's own text column. On <see cref="VanillaUiSite.InventoryTooltip"/> the rows are the
    /// column's last lines; on <see cref="VanillaUiSite.RecipeTooltip"/> they sit right under the
    /// product's description, above the requirement list.
    /// </summary>
    TooltipBody = 5,

    /// <summary>
    /// <see cref="VanillaUiSite.RecipeTooltip"/> only: rows right under the recipe's requirement list
    /// (the "0/3 Glass" lines), in the tooltip's own text style.
    /// </summary>
    TooltipRequirements = 6,

    /// <summary>
    /// <see cref="VanillaUiSite.RecipeTooltip"/> only: rows right under one requirement line (the line
    /// <see cref="IVanillaAttachHandle.RequirementLine"/> names, an index into
    /// <see cref="VanillaPanelTarget.Requirements"/>), inside the requirement list, so the next line moves
    /// down to make room. Attach with <see cref="Ui.AttachToRequirementLine"/>; a plain
    /// <see cref="Ui.AttachToVanilla(VanillaUiSite, VanillaPanelAnchor, object?, string?)"/> with this anchor is
    /// line 0. Hidden while the recipe shown has no such line.
    /// </summary>
    TooltipRequirementLine = 7,
}

/// <summary>
/// One row of a vanilla attachment with its <see cref="LabelTone"/>: plain text in the site's own
/// font (the tooltip's description style on the tooltip sites). <see cref="LabelTone.Normal"/> keeps
/// the game's own text color; the other tones use the framework's label colors. Game-free.
/// </summary>
public readonly struct VanillaRow : IEquatable<VanillaRow>
{
    /// <summary>A row of <paramref name="text"/> in <paramref name="tone"/>.</summary>
    public VanillaRow(string text, LabelTone tone = LabelTone.Normal)
    {
        _text = text;
        Tone = tone;
    }

    private readonly string? _text;

    /// <summary>The row's text. Never null (a default row is empty).</summary>
    public string Text => _text ?? "";

    /// <summary>The row's color.</summary>
    public LabelTone Tone { get; }

    /// <summary>A plain row: the game's own text color.</summary>
    public static implicit operator VanillaRow(string text) => new(text);

    public bool Equals(VanillaRow other) => Tone == other.Tone && string.Equals(Text, other.Text, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is VanillaRow r && Equals(r);

    public override int GetHashCode() => HashCode.Combine(Text, Tone);

    public override string ToString() => Tone == LabelTone.Normal ? Text : Text + " [" + LabelTones.Name(Tone) + "]";
}

/// <summary>
/// One requirement line of a recipe as <see cref="VanillaUiSite.RecipeTooltip"/> reports it: the
/// item's <c>PrefabGUID</c> and the amount the recipe asks for. Game-free.
/// </summary>
public readonly struct VanillaRecipeRequirement : IEquatable<VanillaRecipeRequirement>
{
    /// <summary>A requirement of <paramref name="amount"/> x item <paramref name="itemGuid"/>.</summary>
    public VanillaRecipeRequirement(int itemGuid, int amount)
    {
        ItemGuid = itemGuid;
        Amount = amount;
    }

    /// <summary>The required item's <c>PrefabGUID</c>.</summary>
    public int ItemGuid { get; }

    /// <summary>
    /// The amount the recipe itself asks for (its <c>RecipeRequirementBuffer</c> row). A station's
    /// resource bonus can make the tooltip show less.
    /// </summary>
    public int Amount { get; }

    public bool Equals(VanillaRecipeRequirement other) => ItemGuid == other.ItemGuid && Amount == other.Amount;

    public override bool Equals(object? obj) => obj is VanillaRecipeRequirement r && Equals(r);

    public override int GetHashCode() => HashCode.Combine(ItemGuid, Amount);

    public override string ToString() =>
        ItemGuid.ToString(CultureInfo.InvariantCulture) + "x" + Amount.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// The face a P6 button shows when its caption does not fit its slot (spec P6; today only
/// <see cref="VanillaPanelAnchor.BesideSort"/> falls back): a square button in the game's bar art
/// with this icon, and the caption as its tooltip. Game-free.
/// </summary>
public readonly struct VanillaButtonIcon : IEquatable<VanillaButtonIcon>
{
    private VanillaButtonIcon(int itemGuid, string mark)
    {
        ItemGuid = itemGuid;
        Mark = mark ?? "";
    }

    /// <summary>An item's prefab id whose game icon is shown (the item's own inventory icon), or 0.</summary>
    public int ItemGuid { get; }

    /// <summary>
    /// The framework's drawn mark shown when there is no item icon: <c>chest</c> (the default),
    /// <c>check</c>, <c>up</c>, <c>down</c>, <c>remove</c>, <c>grip</c> or <c>locate</c>.
    /// </summary>
    public string Mark { get; }

    /// <summary>The drawn chest mark.</summary>
    public static VanillaButtonIcon Default => new(0, "chest");

    /// <summary>The game icon of item <paramref name="prefabGuid"/>; the chest mark when the item has none.</summary>
    public static VanillaButtonIcon Item(int prefabGuid) => new(prefabGuid, "chest");

    /// <summary>A drawn mark by name (see <see cref="Mark"/>); an unknown name gives the chest.</summary>
    public static VanillaButtonIcon Drawn(string mark) => new(0, string.IsNullOrWhiteSpace(mark) ? "chest" : mark.Trim());

    public bool Equals(VanillaButtonIcon other) => ItemGuid == other.ItemGuid && string.Equals(Mark, other.Mark, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is VanillaButtonIcon o && Equals(o);

    public override int GetHashCode() => ItemGuid * 31 + (Mark?.GetHashCode() ?? 0);

    public override string ToString() => ItemGuid != 0 ? "item:" + ItemGuid : "mark:" + (string.IsNullOrEmpty(Mark) ? "chest" : Mark);
}

/// <summary>Which vanilla window an attachment's panel belongs to.</summary>
public enum VanillaPanelKind
{
    /// <summary>No panel (the site is closed, or the site is not a panel).</summary>
    None = 0,

    /// <summary>A storage container (chest, stash, the castle's storage buildings).</summary>
    Container = 1,

    /// <summary>A crafting workstation (workbench, tailor, and the other recipe stations).</summary>
    Workstation = 2,

    /// <summary>A refinement station (furnace, sawmill, grinder, and the other converters).</summary>
    RefinementStation = 3,

    /// <summary>The castle heart window.</summary>
    CastleHeart = 4,

    /// <summary>The character menu's own inventory (no external target).</summary>
    Character = 5,

    /// <summary>A prison cell (<c>PrisonSubMenu</c>); the target is the cell's station entity.</summary>
    PrisonCell = 6,

    /// <summary>A salvage station, the devourer by prefab name (<c>SalvagestationSubMenu</c>).</summary>
    SalvageStation = 7,

    /// <summary>A unit station: tomb, vermin nest, Nether Gate, by prefab name (<c>UnitSpawnerstationSubMenu</c>).</summary>
    UnitStation = 8,
}

/// <summary>
/// What a vanilla panel is showing: the client entity it was opened on, that entity's
/// <c>NetworkId</c>, and for the tooltip sites the hovered item or recipe.
///
/// <para><b>Mapping to the server.</b> The client entity is only meaningful in the client world.
/// The <c>NetworkId</c> is what the game itself uses to name the same object on both sides
/// (<see cref="NetworkIdIndex"/> and <see cref="NetworkIdGeneration"/> are its
/// <c>Normal_Index</c> and <c>Normal_Generation</c>, and <see cref="NetworkId2"/> its type); send
/// them to the server and resolve them there.
/// It is a session-local allocation: never persist it, and never persist <see cref="EntityIndex"/>
/// either.</para>
/// </summary>
public readonly struct VanillaPanelTarget : IEquatable<VanillaPanelTarget>
{
    /// <summary>Create a target. Public so tests and samples can describe one.</summary>
    public VanillaPanelTarget(
        VanillaPanelKind kind,
        int entityIndex,
        int entityVersion,
        int networkId0 = 0,
        int networkId1 = 0,
        int networkId2 = 0,
        int itemGuid = 0,
        int itemAmount = 0)
    {
        Kind = kind;
        EntityIndex = entityIndex;
        EntityVersion = entityVersion;
        NetworkId0 = networkId0;
        NetworkId1 = networkId1;
        NetworkId2 = networkId2;
        ItemGuid = itemGuid;
        ItemAmount = itemAmount;
        RecipeGuid = 0;
        ProductGuid = 0;
        ProductAmount = 0;
        _requirements = null;
    }

    /// <summary>
    /// Create a <see cref="VanillaUiSite.RecipeTooltip"/> target: the window and its station (none
    /// for the character crafting tab), the hovered recipe, the item the tooltip describes
    /// (<paramref name="itemGuid"/> x <paramref name="itemAmount"/>), the recipe's product
    /// (<paramref name="productGuid"/> x <paramref name="productAmount"/>, its output row 0) and its
    /// requirements in the order the tooltip lists them. The list is copied.
    /// </summary>
    public VanillaPanelTarget(
        VanillaPanelKind kind,
        int entityIndex,
        int entityVersion,
        int networkId0,
        int networkId1,
        int networkId2,
        int recipeGuid,
        int itemGuid,
        int itemAmount,
        int productGuid,
        int productAmount,
        IReadOnlyList<VanillaRecipeRequirement>? requirements)
        : this(kind, entityIndex, entityVersion, networkId0, networkId1, networkId2, itemGuid, itemAmount)
    {
        RecipeGuid = recipeGuid;
        ProductGuid = productGuid;
        ProductAmount = productAmount;
        _requirements = requirements == null || requirements.Count == 0 ? null : Copy(requirements);
    }

    /// <summary>
    /// The resolver's form: <paramref name="owned"/> is taken as is, not copied (the resolver keeps one
    /// array per hovered recipe, so a frame that samples the same hover allocates nothing). It is never
    /// written after this call.
    /// </summary>
    internal static VanillaPanelTarget ForRecipe(
        VanillaPanelKind kind, int entityIndex, int entityVersion, int networkId0, int networkId1, int networkId2,
        int recipeGuid, int itemGuid, int itemAmount, int productGuid, int productAmount, VanillaRecipeRequirement[]? owned) =>
        new(kind, entityIndex, entityVersion, networkId0, networkId1, networkId2, recipeGuid, itemGuid, itemAmount,
            productGuid, productAmount, owned, owned: true);

    private VanillaPanelTarget(
        VanillaPanelKind kind, int entityIndex, int entityVersion, int networkId0, int networkId1, int networkId2,
        int recipeGuid, int itemGuid, int itemAmount, int productGuid, int productAmount,
        VanillaRecipeRequirement[]? requirements, bool owned)
        : this(kind, entityIndex, entityVersion, networkId0, networkId1, networkId2, itemGuid, itemAmount)
    {
        _ = owned;
        RecipeGuid = recipeGuid;
        ProductGuid = productGuid;
        ProductAmount = productAmount;
        _requirements = requirements == null || requirements.Length == 0 ? null : requirements;
    }

    private static VanillaRecipeRequirement[] Copy(IReadOnlyList<VanillaRecipeRequirement> list)
    {
        var copy = new VanillaRecipeRequirement[list.Count];
        for (int i = 0; i < copy.Length; i++) copy[i] = list[i];
        return copy;
    }

    private readonly VanillaRecipeRequirement[]? _requirements;

    /// <summary>The window the panel belongs to.</summary>
    public VanillaPanelKind Kind { get; }

    /// <summary>Client-world <c>Entity.Index</c> of the container or station, 0 when there is none.</summary>
    public int EntityIndex { get; }

    /// <summary>Client-world <c>Entity.Version</c>.</summary>
    public int EntityVersion { get; }

    /// <summary>
    /// The <c>NetworkId</c>'s first word: <c>Normal_Index</c> for a normal id (every placed
    /// container, station and heart). Only meaningful together with <see cref="NetworkId1"/> and
    /// <see cref="NetworkId2"/>.
    /// </summary>
    public int NetworkId0 { get; }

    /// <summary>
    /// The second word: <c>Normal_Generation</c> (0..255) for a normal id, with the padding bytes
    /// cleared; for a static-map id (<see cref="NetworkId2"/> = 2) the game's own second word.
    /// </summary>
    public int NetworkId1 { get; }

    /// <summary>The id's type (<c>NetworkIdType</c>: 0 none, 1 normal, 2 static map object), padding cleared.</summary>
    public int NetworkId2 { get; }

    /// <summary>
    /// For <see cref="VanillaUiSite.InventoryTooltip"/>: the hovered item's <c>PrefabGUID</c>. For
    /// <see cref="VanillaUiSite.RecipeTooltip"/>: the <c>PrefabGUID</c> the game's tooltip describes. At a
    /// workstation and in the crafting tab that is the product (<see cref="ProductGuid"/>); at a
    /// refinement station it is the recipe itself (<see cref="RecipeGuid"/>), which is what that window
    /// passes to its tooltip. Else 0.
    /// </summary>
    public int ItemGuid { get; }

    /// <summary>
    /// For <see cref="VanillaUiSite.InventoryTooltip"/>: the hovered stack's amount. For
    /// <see cref="VanillaUiSite.RecipeTooltip"/>: <see cref="ProductAmount"/> when <see cref="ItemGuid"/>
    /// is the product, 0 when it is the recipe (refinement station). Else 0.
    /// </summary>
    public int ItemAmount { get; }

    /// <summary>For <see cref="VanillaUiSite.RecipeTooltip"/>: the hovered recipe's <c>PrefabGUID</c>, else 0.</summary>
    public int RecipeGuid { get; }

    /// <summary>
    /// For <see cref="VanillaUiSite.RecipeTooltip"/>: the recipe's product, its <c>RecipeOutputBuffer</c>
    /// row 0 (0 when that row is missing). The same at every station. Else 0.
    /// </summary>
    public int ProductGuid { get; }

    /// <summary>For <see cref="VanillaUiSite.RecipeTooltip"/>: how many of <see cref="ProductGuid"/> one craft makes, else 0.</summary>
    public int ProductAmount { get; }

    /// <summary>
    /// For <see cref="VanillaUiSite.RecipeTooltip"/>: the recipe's requirements in the order the
    /// tooltip lists them (the recipe's own order). Empty for every other site. Never null.
    /// </summary>
    public IReadOnlyList<VanillaRecipeRequirement> Requirements =>
        (IReadOnlyList<VanillaRecipeRequirement>?)_requirements ?? Array.Empty<VanillaRecipeRequirement>();

    /// <summary>For <see cref="VanillaUiSite.RecipeTooltip"/>: the product as a requirement-shaped pair.</summary>
    public VanillaRecipeRequirement Product => new(ProductGuid, ProductAmount);

    /// <summary>
    /// While the container window shows a remote container preview (<c>ctx.Inventory.Preview</c>) of this client: the
    /// previewed container's stable id, which a mod can send to its server instead of (or with) the network id words;
    /// <see cref="Progenitor.Inventory.ContainerId.None"/> otherwise. Set only for <see cref="VanillaPanelKind.Container"/>.
    /// </summary>
    public Progenitor.Inventory.ContainerId PreviewContainer { get; init; }

    /// <summary>True when the panel shows a remote container preview (<see cref="PreviewContainer"/> is set).</summary>
    public bool IsPreview => !PreviewContainer.IsNone;

    /// <summary>The <c>NetworkId</c>'s <c>Normal_Index</c>.</summary>
    public int NetworkIdIndex => NetworkId0;

    /// <summary>The <c>NetworkId</c>'s <c>Normal_Generation</c>.</summary>
    public int NetworkIdGeneration => NetworkId1 & 0xFF;

    /// <summary>True when the panel is not bound to an entity (the character inventory, or nothing).</summary>
    public bool HasEntity => EntityIndex != 0 || EntityVersion != 0;

    /// <summary>True when a <c>NetworkId</c> was read for the target.</summary>
    public bool HasNetworkId => NetworkId0 != 0 || NetworkId1 != 0 || NetworkId2 != 0;

    /// <summary>The empty target: no panel.</summary>
    public static VanillaPanelTarget None => default;

    /// <inheritdoc />
    public bool Equals(VanillaPanelTarget other) =>
        Kind == other.Kind && EntityIndex == other.EntityIndex && EntityVersion == other.EntityVersion
        && NetworkId0 == other.NetworkId0 && NetworkId1 == other.NetworkId1 && NetworkId2 == other.NetworkId2
        && ItemGuid == other.ItemGuid && ItemAmount == other.ItemAmount
        && RecipeGuid == other.RecipeGuid && ProductGuid == other.ProductGuid && ProductAmount == other.ProductAmount
        && SameRequirements(_requirements, other._requirements) && PreviewContainer == other.PreviewContainer;

    private static bool SameRequirements(VanillaRecipeRequirement[]? a, VanillaRecipeRequirement[]? b)
    {
        if (ReferenceEquals(a, b)) return true;
        int na = a?.Length ?? 0;
        int nb = b?.Length ?? 0;
        if (na != nb) return false;
        for (int i = 0; i < na; i++)
            if (!a![i].Equals(b![i])) return false;
        return true;
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is VanillaPanelTarget t && Equals(t);

    /// <inheritdoc />
    public override int GetHashCode() =>
        HashCode.Combine(
            HashCode.Combine(Kind, EntityIndex, EntityVersion, NetworkId0, NetworkId1, NetworkId2, ItemGuid, ItemAmount),
            HashCode.Combine(RecipeGuid, ProductGuid, ProductAmount),
            _requirements?.Length ?? 0, PreviewContainer);

    /// <summary>Equality operator.</summary>
    public static bool operator ==(VanillaPanelTarget a, VanillaPanelTarget b) => a.Equals(b);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(VanillaPanelTarget a, VanillaPanelTarget b) => !a.Equals(b);

    /// <summary>
    /// One grep-friendly line: <c>kind=Container entity=812:3 net=40:1</c>, plus
    /// <c>item=&lt;guid&gt;x&lt;amount&gt;</c> for a tooltip, and for a recipe tooltip
    /// <c>recipe=&lt;guid&gt;</c> before the item, <c>makes=&lt;guid&gt;x&lt;amount&gt;</c> when the product is not the item
    /// (refinement station) and <c>needs=&lt;guid&gt;x&lt;amount&gt;,...</c> after it.
    /// </summary>
    public override string ToString()
    {
        var s = "kind=" + Kind + " entity=" + EntityIndex.ToString(CultureInfo.InvariantCulture) + ":"
            + EntityVersion.ToString(CultureInfo.InvariantCulture)
            + " net=" + (HasNetworkId
                ? NetworkIdIndex.ToString(CultureInfo.InvariantCulture) + ":"
                  + NetworkIdGeneration.ToString(CultureInfo.InvariantCulture)
                : "none");
        if (RecipeGuid != 0) s += " recipe=" + RecipeGuid.ToString(CultureInfo.InvariantCulture);
        if (ItemGuid != 0)
            s += " item=" + ItemGuid.ToString(CultureInfo.InvariantCulture) + "x"
                + ItemAmount.ToString(CultureInfo.InvariantCulture);
        if (ProductGuid != 0 && (ProductGuid != ItemGuid || ProductAmount != ItemAmount))
            s += " makes=" + ProductGuid.ToString(CultureInfo.InvariantCulture) + "x"
                + ProductAmount.ToString(CultureInfo.InvariantCulture);
        if (_requirements != null)
        {
            s += " needs=";
            for (int i = 0; i < _requirements.Length; i++) s += (i > 0 ? "," : "") + _requirements[i];
        }
        if (IsPreview) s += " preview=" + PreviewContainer;

        return s;
    }
}

/// <summary>What this client's container preview says about the open container window, for the panel sites. Game-free.</summary>
internal readonly struct VanillaPreviewWindow
{
    internal VanillaPreviewWindow(bool ownsWindow, int windowIndex, int windowVersion, int targetIndex, int targetVersion,
        int net0, int net1, int net2, Progenitor.Inventory.ContainerId container)
    {
        OwnsWindow = ownsWindow; WindowIndex = windowIndex; WindowVersion = windowVersion;
        TargetIndex = targetIndex; TargetVersion = targetVersion; Net0 = net0; Net1 = net1; Net2 = net2; Container = container;
    }

    /// <summary>The preview opened the container window and has not closed.</summary>
    internal bool OwnsWindow { get; }
    /// <summary>The client entity the window was opened on.</summary>
    internal int WindowIndex { get; }
    internal int WindowVersion { get; }
    /// <summary>The previewed chest's client entity now (0:0 while it is off the client).</summary>
    internal int TargetIndex { get; }
    internal int TargetVersion { get; }
    /// <summary>The chest's network id words the server sent.</summary>
    internal int Net0 { get; }
    internal int Net1 { get; }
    internal int Net2 { get; }
    internal Progenitor.Inventory.ContainerId Container { get; }
}

/// <summary>
/// The panel sites' target while a remote container preview owns the container window, game-free. The game's mapper
/// names the previewed chest as its target (there is no interaction behind the window); this makes the target say so
/// for sure: the previewed chest's client entity and network id (the server's words when the entity has none to read,
/// or is off the client for a moment), and <see cref="VanillaPanelTarget.PreviewContainer"/>. A container window that is
/// not the preview's (a chest the player opened at the chest) is left as it is.
/// </summary>
internal static class VanillaPanelPreview
{
    /// <param name="emptyIsWindow">
    /// True for a site whose empty target can only mean the window's own (the container panel and the inventory panel,
    /// while the mapper has no target for a moment). False for the inventory tooltip: there an empty target is a hovered
    /// item in the player's own bag half, which belongs to no container and must not name the previewed chest.
    /// </param>
    internal static VanillaPanelTarget Apply(in VanillaPanelTarget target, in VanillaPreviewWindow preview, bool emptyIsWindow)
    {
        if (target.Kind != VanillaPanelKind.Container || !preview.OwnsWindow || preview.Container.IsNone) return target;
        bool onWindow = (target.HasEntity && target.EntityIndex == preview.WindowIndex && target.EntityVersion == preview.WindowVersion)
            || (target.HasEntity && target.EntityIndex == preview.TargetIndex && target.EntityVersion == preview.TargetVersion)
            || (!target.HasEntity && emptyIsWindow);
        if (!onWindow) return target;
        int index = target.EntityIndex, version = target.EntityVersion;
        if (!target.HasEntity) { index = preview.TargetIndex != 0 || preview.TargetVersion != 0 ? preview.TargetIndex : preview.WindowIndex; version = preview.TargetIndex != 0 || preview.TargetVersion != 0 ? preview.TargetVersion : preview.WindowVersion; }
        int n0 = target.NetworkId0, n1 = target.NetworkId1, n2 = target.NetworkId2;
        if (!target.HasNetworkId) { n0 = preview.Net0; n1 = preview.Net1; n2 = preview.Net2; }
        return new VanillaPanelTarget(target.Kind, index, version, n0, n1, n2, target.ItemGuid, target.ItemAmount)
        {
            PreviewContainer = preview.Container,
        };
    }
}

/// <summary>What happened to a vanilla panel.</summary>
public enum VanillaPanelChange
{
    /// <summary>The panel opened. <see cref="VanillaPanelEvent.Target"/> is what it shows.</summary>
    Opened = 1,

    /// <summary>The panel is still open but shows something else (another container, another hovered item or recipe).</summary>
    TargetChanged = 2,

    /// <summary>The panel closed. <see cref="VanillaPanelEvent.Previous"/> is what it showed.</summary>
    Closed = 3,
}

/// <summary>One open, close or target change of a vanilla panel site.</summary>
public readonly struct VanillaPanelEvent
{
    /// <summary>Create an event. Public so tests can build one.</summary>
    public VanillaPanelEvent(VanillaUiSite site, VanillaPanelChange change, VanillaPanelTarget target, VanillaPanelTarget previous)
    {
        Site = site;
        Change = change;
        Target = target;
        Previous = previous;
    }

    /// <summary>The site that changed.</summary>
    public VanillaUiSite Site { get; }

    /// <summary>What changed.</summary>
    public VanillaPanelChange Change { get; }

    /// <summary>What the panel shows now; <see cref="VanillaPanelTarget.None"/> after a close.</summary>
    public VanillaPanelTarget Target { get; }

    /// <summary>What it showed before; <see cref="VanillaPanelTarget.None"/> on an open.</summary>
    public VanillaPanelTarget Previous { get; }

    /// <inheritdoc />
    public override string ToString() => Site + " " + Change + " " + (Change == VanillaPanelChange.Closed ? Previous : Target);
}

/// <summary>
/// The open/close/target edge logic of one attachment, with no Unity in it. Each frame the site's
/// sample says "open, showing T" or "closed"; this turns that level signal into the three edges a
/// plugin subscribes to. Game-free so the edges are tested, not assumed.
/// </summary>
internal sealed class VanillaPanelTracker
{
    internal bool IsOpen { get; private set; }

    internal VanillaPanelTarget Target { get; private set; }

    /// <summary>
    /// Feed one frame. Returns the edge it produced, or null when nothing changed (the common case:
    /// every frame the panel stays open on the same target, and every frame it stays closed).
    /// </summary>
    internal VanillaPanelChange? Observe(bool open, in VanillaPanelTarget target, out VanillaPanelTarget previous)
    {
        previous = Target;
        if (!open)
        {
            if (!IsOpen) return null;
            IsOpen = false;
            Target = VanillaPanelTarget.None;
            return VanillaPanelChange.Closed;
        }

        if (!IsOpen)
        {
            IsOpen = true;
            Target = target;
            previous = VanillaPanelTarget.None;
            return VanillaPanelChange.Opened;
        }

        if (Target == target) return null;
        Target = target;
        return VanillaPanelChange.TargetChanged;
    }

    /// <summary>Forget everything (dispose). A later frame starts from closed.</summary>
    internal void Reset()
    {
        IsOpen = false;
        Target = VanillaPanelTarget.None;
    }
}

/// <summary>A rectangle in one canvas's units, Y up (Unity's convention). Game-free.</summary>
internal readonly struct UiRect
{
    internal UiRect(float x, float y, float width, float height)
    {
        X = x;
        Y = y;
        Width = width < 0 ? 0 : width;
        Height = height < 0 ? 0 : height;
    }

    /// <summary>Left edge.</summary>
    internal float X { get; }

    /// <summary>Bottom edge.</summary>
    internal float Y { get; }

    internal float Width { get; }

    internal float Height { get; }

    internal float XMax => X + Width;

    internal float YMax => Y + Height;

    internal bool IsEmpty => Width <= 0 || Height <= 0;

    internal static UiRect FromMinMax(float xMin, float yMin, float xMax, float yMax) =>
        new(xMin, yMin, xMax - xMin, yMax - yMin);

    /// <summary>Interiors intersect by more than <paramref name="slack"/> on both axes. Touching edges do not overlap.</summary>
    internal bool Overlaps(in UiRect other, float slack = 0.5f) =>
        !IsEmpty && !other.IsEmpty
        && X < other.XMax - slack && other.X < XMax - slack
        && Y < other.YMax - slack && other.Y < YMax - slack;

    /// <summary><paramref name="inner"/> lies inside this rect, within <paramref name="slack"/>.</summary>
    internal bool Contains(in UiRect inner, float slack = 0.5f) =>
        inner.X >= X - slack && inner.XMax <= XMax + slack
        && inner.Y >= Y - slack && inner.YMax <= YMax + slack;

    public override string ToString() =>
        "(" + Fmt(X) + "," + Fmt(Y) + " " + Fmt(Width) + "x" + Fmt(Height) + ")";

    private static string Fmt(float v) => v.ToString("0", CultureInfo.InvariantCulture);
}

/// <summary>Where a placed inline container went (<see cref="VanillaPanelPlacement.PlaceBeside"/>).</summary>
internal enum BesideSlot
{
    /// <summary>No slot fits.</summary>
    None = 0,

    /// <summary>Right of the group's last visible member, on its row.</summary>
    After = 1,

    /// <summary>Centered above the reference control.</summary>
    Above = 2,
}

/// <summary>
/// Whether a window has come to rest: the frame and the group's last member measured the same
/// (within half a unit) <see cref="VanillaPanelPlacement.SettleFrames"/> times in a row. A window
/// slides in when it opens; a slot judged mid-slide is judged on the wrong rects (live 1.1.13.0: the
/// sliding frame read 642 wide and the slot 88, at rest they are 620 and 58). Game-free.
/// </summary>
internal sealed class PlacementSettle
{
    private UiRect _frame;
    private UiRect _last;
    private int _same = -1;

    /// <summary>Record one measurement; true once the last <see cref="VanillaPanelPlacement.SettleFrames"/> agreed.</summary>
    internal bool Observe(in UiRect frame, in UiRect last)
    {
        if (_same >= 0 && Same(frame, _frame) && Same(last, _last))
        {
            _same++;
        }
        else
        {
            _frame = frame;
            _last = last;
            _same = 0;
        }

        return _same >= VanillaPanelPlacement.SettleFrames - 1;
    }

    internal void Reset() => _same = -1;

    private static bool Same(in UiRect a, in UiRect b) =>
        Math.Abs(a.X - b.X) <= 0.5f && Math.Abs(a.Y - b.Y) <= 0.5f
        && Math.Abs(a.Width - b.Width) <= 0.5f && Math.Abs(a.Height - b.Height) <= 0.5f;
}

/// <summary>Whether an attachment's container may be shown where it is.</summary>
internal enum VanillaFit
{
    /// <summary>The runtime cannot say (a site that does no placement, or a fake). Treated as fits.</summary>
    Unknown = 0,

    /// <summary>Placed, on screen, overlapping nothing vanilla.</summary>
    Fits = 1,

    /// <summary>Would leave the screen or cover a vanilla element: the container is hidden.</summary>
    Blocked = 2,
}

/// <summary>A placement verdict and the one word the log line carries.</summary>
internal readonly struct VanillaFitResult
{
    internal VanillaFitResult(VanillaFit fit, string reason, UiRect rect = default, bool retry = false, bool settling = false, bool icons = false)
    {
        Fit = fit;
        Reason = reason ?? "";
        Rect = rect;
        Retry = retry;
        Settling = settling;
        Icons = icons;
    }

    /// <summary>
    /// The window is still moving (its open motion): nothing is judged, the container stays hidden
    /// and is measured again on the next frame. Never logged as a refusal.
    /// </summary>
    internal bool Settling { get; }

    /// <summary>The row fits only as icon buttons (<see cref="VanillaPanelPlacement.PlaceBesideRow"/>).</summary>
    internal bool Icons { get; }

    internal static VanillaFitResult Moving => new(VanillaFit.Unknown, "settling", default, retry: true, settling: true);

    /// <summary>
    /// A refusal that may change without the container being rebuilt, so it is asked again on the
    /// refit cadence. True for placed containers: hiding one moves nothing, and the window's content
    /// slides in under its open motion, so a slot measured on the first frames can be off by the
    /// motion's offset. A joined container's refusal stays final, because hiding it reflows the
    /// group and asking again would make it flicker.
    /// </summary>
    internal bool Retry { get; }

    internal VanillaFit Fit { get; }

    /// <summary><c>ok</c>, <c>offscreen</c>, <c>overlap:&lt;name&gt;</c>, <c>overflow:&lt;name&gt;</c>, <c>unlaid</c>.</summary>
    internal string Reason { get; }

    /// <summary>Where the container goes (edge columns) or is (inline), in the parent's units.</summary>
    internal UiRect Rect { get; }

    internal bool Shown => Fit != VanillaFit.Blocked;

    internal static VanillaFitResult Unknown => new(VanillaFit.Unknown, "unknown");
}

/// <summary>A vanilla rect the placement must not cover, with a name for the log line.</summary>
internal readonly struct UiObstacle
{
    internal UiObstacle(string name, UiRect rect)
    {
        Name = name ?? "?";
        Rect = rect;
    }

    internal string Name { get; }

    internal UiRect Rect { get; }
}

/// <summary>
/// The layout rules for P6 attachments, as arithmetic with no Unity in it. The Unity half reads
/// rects (world corners mapped into the container parent's local space) and hands them here; what
/// comes back is either a rect to place the container at or a refusal with a reason. Tested at the
/// canvas sizes 1920x1080, 2560x1440 and 3840x2160 produce under the game's height-matched scaler.
///
/// <para><b>The rules.</b></para>
/// <list type="number">
/// <item><description><b>Edge columns</b> sit outside the panel's frame, <see cref="EdgeGap"/>
/// canvas units from it, their top edge on the top edge of the panel's item grid (the frame's top
/// when the panel has no grid), one button per row at the donor button's own size, with the donor
/// group's spacing between them.</description></item>
/// <item><description>A column that would leave the canvas, or cover any other vanilla panel that is
/// open at the same time (the player inventory beside a chest), is <b>not drawn</b>. There is no
/// fallback position: a mod button floating somewhere else reads as bolted on.</description></item>
/// <item><description><b>Inline anchors</b> are laid out by the game's own layout group. After the
/// game has laid them out, every button in the group must still lie inside the panel's frame and
/// ours must cover no vanilla button; otherwise ours is hidden and the group reflows back.</description></item>
/// <item><description>The <b>tooltip line</b> goes into the tooltip's own text column only when that
/// column is governed by a vertical layout group, so the tooltip grows around it.</description></item>
/// </list>
/// </summary>
internal static class VanillaPanelPlacement
{
    /// <summary>Distance between a vanilla frame edge and an edge column, in canvas units.</summary>
    internal const float EdgeGap = 8f;

    /// <summary>Spacing between stacked column items when the donor group reports none.</summary>
    internal const float DefaultSpacing = 8f;

    /// <summary>Height of one text row in a column, when the runtime has no donor to measure.</summary>
    internal const float DefaultRowHeight = 24f;

    /// <summary>Size of an edge column: buttons first, then rows, one per line.</summary>
    internal static (float Width, float Height) ColumnSize(
        int buttons, int rows, float buttonWidth, float buttonHeight, float rowHeight, float spacing)
    {
        int b = buttons < 0 ? 0 : buttons;
        int r = rows < 0 ? 0 : rows;
        int items = b + r;
        if (items == 0) return (0f, 0f);
        float sp = spacing < 0 ? 0 : spacing;
        float h = b * buttonHeight + r * rowHeight + (items - 1) * sp;
        return (buttonWidth, h);
    }

    /// <summary>
    /// Place an edge column beside <paramref name="panel"/>. <paramref name="gridTop"/> is the top of
    /// the panel's item grid, or null to align with the frame's top.
    /// </summary>
    internal static VanillaFitResult PlaceEdge(
        VanillaPanelAnchor anchor,
        in UiRect panel,
        float? gridTop,
        float width,
        float height,
        in UiRect bounds,
        IReadOnlyList<UiObstacle>? obstacles)
    {
        if (anchor != VanillaPanelAnchor.LeftEdge && anchor != VanillaPanelAnchor.RightEdge)
            return new VanillaFitResult(VanillaFit.Blocked, "anchor");
        if (panel.IsEmpty) return new VanillaFitResult(VanillaFit.Blocked, "panel");

        // An empty column is nothing on screen; it fits trivially and is placed where it would go,
        // so a first button added later lands in the right spot.
        float top = gridTop.HasValue && gridTop.Value <= panel.YMax && gridTop.Value >= panel.Y
            ? gridTop.Value
            : panel.YMax;
        float x = anchor == VanillaPanelAnchor.LeftEdge ? panel.X - EdgeGap - width : panel.XMax + EdgeGap;
        var rect = new UiRect(x, top - height, width, height);
        if (rect.IsEmpty) return new VanillaFitResult(VanillaFit.Fits, "empty", rect);

        if (!bounds.IsEmpty && !bounds.Contains(rect))
            return new VanillaFitResult(VanillaFit.Blocked, "offscreen", rect);

        if (obstacles != null)
        {
            for (int i = 0; i < obstacles.Count; i++)
            {
                if (rect.Overlaps(obstacles[i].Rect))
                    return new VanillaFitResult(VanillaFit.Blocked, "overlap:" + obstacles[i].Name, rect);
            }
        }

        return new VanillaFitResult(VanillaFit.Fits, "ok", rect);
    }

    /// <summary>
    /// Verify an inline anchor after the game's layout group placed it: every member of the group
    /// (ours included) inside <paramref name="panel"/>, and ours covering no vanilla member.
    /// </summary>
    internal static VanillaFitResult CheckInline(
        in UiRect ours, in UiRect panel, IReadOnlyList<UiObstacle>? vanillaMembers)
    {
        if (panel.IsEmpty) return new VanillaFitResult(VanillaFit.Blocked, "panel");
        if (ours.IsEmpty) return new VanillaFitResult(VanillaFit.Fits, "empty", ours);
        if (!panel.Contains(ours)) return new VanillaFitResult(VanillaFit.Blocked, "overflow:mod", ours);
        if (vanillaMembers != null)
        {
            for (int i = 0; i < vanillaMembers.Count; i++)
            {
                var m = vanillaMembers[i];
                if (m.Rect.IsEmpty) continue;
                if (!panel.Contains(m.Rect)) return new VanillaFitResult(VanillaFit.Blocked, "overflow:" + m.Name, ours);
                if (ours.Overlaps(m.Rect)) return new VanillaFitResult(VanillaFit.Blocked, "overlap:" + m.Name, ours);
            }
        }

        return new VanillaFitResult(VanillaFit.Fits, "ok", ours);
    }

    /// <summary>
    /// A positioned inline container: right of <paramref name="reference"/> (the group's last
    /// visible member, or the group itself when it has none) by <paramref name="spacing"/>, centered
    /// on its row. All in the group's local units.
    /// </summary>
    internal static UiRect PlaceAfter(in UiRect reference, float spacing, float width, float height)
    {
        float sp = spacing < 0 ? 0 : spacing;
        float centerY = reference.Y + reference.Height * 0.5f;
        return new UiRect(reference.XMax + sp, centerY - height * 0.5f, width, height);
    }

    /// <summary>
    /// A placed inline container (one that never joins the vanilla group): the first of its
    /// candidate slots that lies inside <paramref name="frame"/> and covers no obstacle.
    /// <list type="number">
    /// <item><description><see cref="BesideSlot.After"/>: right of the group's last visible member
    /// (<paramref name="lastMember"/>), on its row (<see cref="PlaceAfter"/>).</description></item>
    /// <item><description><see cref="BesideSlot.Above"/> (only with <paramref name="allowAbove"/>):
    /// centered over <paramref name="reference"/> (the sort button), <paramref name="spacing"/> above it.</description></item>
    /// </list>
    /// All rects in one space (the container's parent). When no slot fits, the verdict is the first
    /// slot's refusal, with every slot's reason joined by <c>/</c>.
    /// </summary>
    internal static VanillaFitResult PlaceBeside(
        in UiRect lastMember, in UiRect reference, bool allowAbove, float spacing, float width, float height,
        in UiRect frame, IReadOnlyList<UiObstacle>? obstacles, out BesideSlot slot, float minWidth = 0f)
    {
        float sp = spacing < 0 ? 0 : spacing;
        var after = PlaceAfter(lastMember, sp, width, height);

        // The slot right of the group ends at the frame. A row a little wider than what is left is
        // narrowed to it, down to minWidth (the captions at their smallest readable size), rather
        // than refused: its captions shrink to fit, as the game's own do.
        if (!frame.IsEmpty && after.XMax > frame.XMax && minWidth > 0f)
        {
            float room = frame.XMax - after.X;
            if (room >= minWidth) after = new UiRect(after.X, after.Y, room, height);
        }

        var first = CheckPlaced(after, frame, obstacles);
        if (first.Fit == VanillaFit.Fits)
        {
            slot = BesideSlot.After;
            return Placed(first);
        }

        if (allowAbove && !reference.IsEmpty)
        {
            var above = new UiRect(reference.X + (reference.Width - width) * 0.5f, reference.YMax + sp, width, height);
            var second = CheckPlaced(above, frame, obstacles);
            if (second.Fit == VanillaFit.Fits)
            {
                slot = BesideSlot.Above;
                return Placed(second);
            }

            slot = BesideSlot.None;
            return new VanillaFitResult(VanillaFit.Blocked, first.Reason + "/" + second.Reason, after, retry: true);
        }

        slot = BesideSlot.None;
        return Placed(first);
    }

    private static VanillaFitResult Placed(in VanillaFitResult r) => new(r.Fit, r.Reason, r.Rect, retry: true);

    /// <summary>
    /// A placed row of <paramref name="buttons"/> captioned buttons, in the order the rule tries
    /// them: as text at full size (<paramref name="textWidth"/>) or squeezed to
    /// <paramref name="textMin"/> (<see cref="PlaceBeside"/>); then, with <paramref name="allowIcons"/>,
    /// as square icon buttons <paramref name="height"/> on a side, <paramref name="spacing"/> apart,
    /// with the captions as tooltips. The first that fits wins; the result says which
    /// (<see cref="VanillaFitResult.Icons"/>). When none fits, the text attempt's refusal is returned.
    /// </summary>
    internal static VanillaFitResult PlaceBesideRow(
        in UiRect lastMember, in UiRect reference, bool allowAbove, bool allowIcons, float spacing,
        float textWidth, float textMin, int buttons, float height,
        in UiRect frame, IReadOnlyList<UiObstacle>? obstacles, out BesideSlot slot)
    {
        var text = PlaceBeside(lastMember, reference, allowAbove, spacing, textWidth, height, frame, obstacles, out slot, textMin);
        if (text.Fit == VanillaFit.Fits || !allowIcons || buttons <= 0) return text;

        float sp = spacing < 0 ? 0 : spacing;
        float iconRow = buttons * height + (buttons - 1) * sp;
        var icons = PlaceBeside(lastMember, reference, allowAbove, spacing, iconRow, height, frame, obstacles, out var iconSlot);
        if (icons.Fit != VanillaFit.Fits)
            return new VanillaFitResult(VanillaFit.Blocked, text.Reason, text.Rect, retry: true);
        slot = iconSlot;
        return new VanillaFitResult(VanillaFit.Fits, "ok", icons.Rect, retry: true, icons: true);
    }

    private static VanillaFitResult CheckPlaced(in UiRect rect, in UiRect frame, IReadOnlyList<UiObstacle>? obstacles)
    {
        if (rect.IsEmpty) return new VanillaFitResult(VanillaFit.Fits, "empty", rect);
        if (!frame.IsEmpty && !frame.Contains(rect)) return new VanillaFitResult(VanillaFit.Blocked, "overflow:mod", rect);
        if (obstacles != null)
        {
            for (int i = 0; i < obstacles.Count; i++)
            {
                if (rect.Overlaps(obstacles[i].Rect))
                    return new VanillaFitResult(VanillaFit.Blocked, "overlap:" + obstacles[i].Name, rect);
            }
        }

        return new VanillaFitResult(VanillaFit.Fits, "ok", rect);
    }

    /// <summary>
    /// The frame an inline container must stay inside: the window's own rect, widened to the first
    /// ancestor of the vanilla group (nearest first) that is at least <see cref="FrameWidthShare"/>
    /// of the window's width. That ancestor is the strip the group lives on: the title strip of a
    /// container sits above its window's rect (so the rename group is outside the window rect and
    /// inside the strip), while the inventory's bottom bar lives on a body as wide as the window.
    /// Narrower ancestors (the title text, the 24-unit rename group) are not frames.
    /// </summary>
    internal static UiRect InlineFrame(in UiRect panel, IReadOnlyList<UiRect>? ancestors)
    {
        if (panel.IsEmpty || ancestors == null) return panel;
        for (int i = 0; i < ancestors.Count; i++)
        {
            var a = ancestors[i];
            if (a.IsEmpty || a.Width < panel.Width * FrameWidthShare) continue;
            return UiRect.FromMinMax(
                Math.Min(panel.X, a.X), Math.Min(panel.Y, a.Y),
                Math.Max(panel.XMax, a.XMax), Math.Max(panel.YMax, a.YMax));
        }

        return panel;
    }

    /// <summary>How many consecutive equal measurements count as a window at rest.</summary>
    internal const int SettleFrames = 3;

    /// <summary>How wide an ancestor must be, against the window, to count as the group's frame.</summary>
    internal const float FrameWidthShare = 0.9f;

    /// <summary>The tooltip line is only placed into a column the game lays out.</summary>
    internal static VanillaFitResult CheckTooltip(bool parentHasVerticalLayout) =>
        parentHasVerticalLayout
            ? new VanillaFitResult(VanillaFit.Fits, "ok")
            : new VanillaFitResult(VanillaFit.Blocked, "unlaid");

    /// <summary>
    /// The preferred size the inline container asks the vanilla group for: <paramref name="buttons"/>
    /// donor-sized buttons side by side with the group's spacing.
    /// </summary>
    internal static float InlineWidth(int buttons, float buttonWidth, float spacing)
    {
        if (buttons <= 0) return 0f;
        return buttons * buttonWidth + (buttons - 1) * (spacing < 0 ? 0 : spacing);
    }
}


/// <summary>
/// "A site that cannot be found after a game update hides and logs one line." Two different
/// things, kept apart (review of P6, finding 3):
///
/// <list type="bullet">
/// <item><description><b>Absent</b> (<see cref="MarkAbsent"/>): the resolver itself failed — a member
/// the game removed or renamed throws when it is touched. Nothing about the site can be read, so it
/// stays absent for the generation for every plugin, with one line.</description></item>
/// <item><description><b>Degraded</b> (<see cref="LogOnce"/>): the window resolves but one piece is
/// missing this time (a donor, an anchor's button group). Only the drawing that needs the piece is
/// withheld, for this frame and this handle; the site keeps resolving, other anchors and other
/// plugins are unaffected, and the line is said once per site and piece.</description></item>
/// </list>
/// Game-free; cleared on Core Uninstall.
/// </summary>
internal static class VanillaSiteHealth
{
    private static readonly object Gate = new();
    private static readonly Dictionary<VanillaUiSite, string> Absent = new();
    private static readonly HashSet<string> Said = new(StringComparer.Ordinal);

    /// <summary>
    /// The resolver for <paramref name="site"/> failed: mark it absent for this generation. True
    /// exactly once per site: the caller logs on true and stays silent after.
    /// </summary>
    internal static bool MarkAbsent(VanillaUiSite site, string what)
    {
        lock (Gate)
        {
            if (Absent.ContainsKey(site)) return false;
            Absent[site] = what ?? "?";
            Said.Add(Key(site, what));
            return true;
        }
    }

    /// <summary>Whether the site's resolver failed in this generation.</summary>
    internal static bool IsAbsent(VanillaUiSite site)
    {
        lock (Gate) return Absent.ContainsKey(site);
    }

    /// <summary>
    /// A piece of <paramref name="site"/> is missing. True the first time this site and piece are
    /// reported; the site is <b>not</b> marked absent.
    /// </summary>
    internal static bool LogOnce(VanillaUiSite site, string what)
    {
        lock (Gate) return Said.Add(Key(site, what));
    }

    /// <summary>The line a first report produces. One format, so docs can quote it.</summary>
    internal static string Line(VanillaUiSite site, string what) =>
        "Ui: site " + VanillaUiSites.Key(site) + " not found (" + (what ?? "?")
        + "); what needs it stays hidden until the game provides it.";

    /// <summary><c>missing=&lt;site&gt;:&lt;what&gt;,...</c> for the pin line (absent sites only), or empty.</summary>
    internal static string Describe()
    {
        lock (Gate)
        {
            if (Absent.Count == 0) return "";
            var parts = new List<string>();
            foreach (var kv in Absent) parts.Add(VanillaUiSites.Key(kv.Key) + ":" + kv.Value);
            parts.Sort(StringComparer.Ordinal);
            return "missing=" + string.Join(",", parts);
        }
    }

    /// <summary>Core Uninstall and tests: the next generation reports afresh.</summary>
    internal static void Clear()
    {
        lock (Gate)
        {
            Absent.Clear();
            Said.Clear();
        }
    }

    private static string Key(VanillaUiSite site, string what) => (int)site + "|" + (what ?? "?");
}
