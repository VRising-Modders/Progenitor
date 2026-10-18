using Progenitor.Hooks;

namespace Progenitor.Ui;

/// <summary>
/// The vanilla widgets a plugin may host rows inside, as a <b>closed</b> enum.
///
/// <para>
/// <b>Why an enum and not a string path.</b> A string-addressed "attach to any transform"
/// would make Progenitor answerable for whatever any mod does to the game's HUD, in every
/// future game build, with no way to review it. A closed enum means every site is a piece of
/// <i>Core</i> that has been read out of the game's metadata, spiked live and written down — its donor
/// widget, its parent rule, its layout verdict — exactly like an address-library entry.
/// Adding a site is a Core change with a research note; it is not a plugin's free choice.
/// </para>
/// </summary>
public enum VanillaUiSite
{
    /// <summary>
    /// The <b>world-map</b> "Rift Incursions" card: <c>ProjectM.UI.MapMenu.WarEventInfo</c>,
    /// a <c>MapWarEventInfoEntry</c> (field at 0x4E8 of <c>MapMenu</c>).
    ///
    /// <list type="bullet">
    /// <item><description><b>Address, never type.</b> Resolved through
    /// <c>MapMenuMapper.GetMapMenu()?.WarEventInfo</c>. There is a <i>second</i> instance of the
    /// same type — <c>AlertsParent.WarEventNode</c>, the transient HUD popup —
    /// and it is explicitly <b>not</b> a site. A <c>FindObjectsOfTypeAll</c> would return both
    /// plus the inactive prefab asset and picking one would be a coin flip.</description></item>
    /// <item><description><b>Donor:</b> the <c>TimerText</c> <c>LocalizedText</c>'s GameObject
    /// (offset 0x48). A row is that GameObject cloned with its <c>LocalizedText</c> removed;
    /// the TMP underneath carries the game's font, size, color and material.</description></item>
    /// <item><description><b>Parent:</b> a mod-owned container that is a <b>sibling</b> of the
    /// widget root, never a child of it and never a child of <c>TextGroup</c> — that
    /// <c>CanvasGroup</c>'s alpha is rewritten every frame from <c>SetData</c>'s
    /// <c>textAlpha</c>, and <c>Hide()</c> may deactivate the widget root when no event is
    /// scheduled, which is precisely the state a mod row is most useful in.</description></item>
    /// <item><description><b>Refresh:</b> <c>SystemHooks.After&lt;MapMenuMapper&gt;</c>, the
    /// managed <c>SystemBase.Update</c> seam. Never Harmony on the mapper, never
    /// <c>MapWarEventInfoEntry.SetData</c> (its <c>ref TimeLocalizationKeys</c> parameter cannot
    /// be marshaled — <c>MarshalDirectiveException</c> at map open, interop trap 1 in its
    /// calling form), and never any of the mapper's Burst <c>LambdaJob_*</c>
    /// types.</description></item>
    /// <item><description><b>Evidence:</b> the internal war-events research notes; the live
    /// spike checklist there is what turns the <i>inferred</i> half of the above into
    /// <i>live</i>.</description></item>
    /// </list>
    /// </summary>
    MapWarEventInfo = 1,

    /// <summary>
    /// The storage container window (chest, stash and the castle's other storage buildings):
    /// <c>ProjectM.UI.ContainerMenu</c>, driven by <c>ContainerMenuMapper</c>.
    ///
    /// <list type="bullet">
    /// <item><description><b>Address:</b> <c>ContainerMenuMapper._ContainerSubMenu</c> (the
    /// container half of the window) and <c>ContainerMenuMapper._Target</c>, the container entity
    /// the window was opened on. Open means the submenu's GameObject is active in the hierarchy and
    /// the target is not null.</description></item>
    /// <item><description><b>Anchors:</b> <see cref="VanillaPanelAnchor.LeftEdge"/> (default),
    /// <see cref="VanillaPanelAnchor.RightEdge"/>, <see cref="VanillaPanelAnchor.BesideRename"/>
    /// (in the title row after <c>OpenTagEditButton</c>; its group is <c>OpenTagEditButtonParent</c>, or the
    /// rename button's own parent when the game leaves that field empty, as 1.1.13.0 does).</description></item>
    /// <item><description><b>Donors:</b> buttons clone a button of the inventory bottom bar (the
    /// first live, clone-safe one of <c>SelfSortButton</c>, <c>AllSortButton</c>, <c>SmartMergeButton</c>, <c>SmartTakeButton</c>, <c>DropAllButton</c>, then any bar child: <c>DropAllButton</c> on 1.1.13.0); captions clone the label of the inventory's own tab buttons and rows
    /// clone the tooltip's body text (<c>FakeTooltip.Description</c>).</description></item>
    /// <item><description><b>Refresh:</b> <c>SystemHooks.After</c> on all eight window mappers (any one may be the only one updating) with
    /// <c>OnlyWhenSystemRuns = false</c> (every panel site uses that option: the mappers only run
    /// while their queries match, and a close must be seen), reading every mapper from one shared
    /// per-frame scan.</description></item>
    /// </list>
    /// </summary>
    ContainerPanel = 2,

    /// <summary>
    /// The station windows: the crafting workstation (<c>WorkstationMenuMapper._WorkstationSubMenu</c>),
    /// the refinement station (<c>RefinementstationMenuMapper._RefinementStationSubMenu</c>), the prison
    /// cell (<c>PrisonMenuMapper._PrisonSubMenu</c>), the salvage station
    /// (<c>SalvagestationMenuMapper._SalvageStationSubMenu</c>) and the unit stations: tomb, vermin
    /// nest, Nether Gate, by prefab name (<c>UnitSpawnerstationMenuMapper._UnitSpawnerstationSubMenu</c>). The target
    /// is always that mapper's <c>_Target</c>, the station entity; <see cref="VanillaPanelTarget.Kind"/>
    /// says which window. The list and the windows left out, with reasons, are
    /// <see cref="VanillaStationWindows"/>. Anchors: <see cref="VanillaPanelAnchor.LeftEdge"/> (default),
    /// <see cref="VanillaPanelAnchor.RightEdge"/> and <see cref="VanillaPanelAnchor.BesideRename"/> (after the
    /// window's <c>OpenTagEditButton</c>, as on the container: the workstation, refinement station, devourer and unit
    /// stations; the prison cell has no rename button, so a <c>BesideRename</c> container is not built on it,
    /// <see cref="VanillaStationWindows.RenameField"/>). <c>Ui.AttachPanelTabs</c> draws only on the windows
    /// with the game's station/redistribution tab row (all but the unit stations). Refresh: the shared
    /// mapper scan.
    /// </summary>
    StationPanel = 3,

    /// <summary>
    /// The player inventory, wherever the game shows it: the character menu's
    /// <c>InventoryMenu</c> and the inventory half of the container, station and castle heart
    /// windows (each mapper's <c>_InventorySubMenu</c>; the active one wins). Anchor:
    /// <see cref="VanillaPanelAnchor.BesideSort"/> (inside
    /// <c>InventorySubMenu.BottomBarButtonsParent</c>, right after the sort button: <c>SelfSortButton</c>, else <c>AllSortButton</c>, which is the only sort button on 1.1.13.0).
    /// <see cref="VanillaPanelTarget"/> names the window the inventory is shown in and, beside a
    /// container or station, that window's target. Refresh:
    /// <c>SystemHooks.After&lt;InventoryMenuMapper&gt;</c>.
    /// </summary>
    InventoryPanel = 4,

    /// <summary>
    /// The castle heart's own window (blood essence fuel, castle list, upgrades):
    /// <c>CastleHeartMenu</c> (<c>CastleHeartMenuMapper._CastleHeartSubMenu</c>, <c>._Target</c> =
    /// the heart entity). Anchors: <see cref="VanillaPanelAnchor.LeftEdge"/> (default) and
    /// <see cref="VanillaPanelAnchor.RightEdge"/>; not <see cref="VanillaPanelAnchor.BesideRename"/>: the heart window
    /// has no rename button on 1.1.13.0 (<c>CastleHeartSubMenu</c> has no <c>OpenTagEditButton</c>). Refresh:
    /// <c>SystemHooks.After&lt;CastleHeartMenuMapper&gt;</c>.
    /// </summary>
    CastleHeartPanel = 5,

    /// <summary>
    /// The same site as <see cref="CastleHeartPanel"/> under the name the requirements used. The
    /// throne has no window of its own on this game build (sitting on it is the <c>UseThrone</c>
    /// interaction, with no menu); the castle selection the "throne panel" was meant to host lives in
    /// the castle heart window's castle list, so both names address that one window.
    /// </summary>
    ThronePanel = CastleHeartPanel,

    /// <summary>
    /// The item tooltip of the player inventory and of a container: the submenu's
    /// <c>FakeTooltip</c> while it is shown over a hovered <c>ItemGridSelectionEntry</c>
    /// (<see cref="VanillaPanelTarget.ItemGuid"/> is that entry's <c>EntryId</c>). Rows only, appended
    /// to the tooltip's text column after <c>DescriptionFrame</c> and cloned from
    /// <c>Description</c>, so a line reads in the tooltip's own font, color and spacing. Anchor:
    /// <see cref="VanillaPanelAnchor.TooltipBody"/> (default), <see cref="VanillaPanelAnchor.TooltipHeader"/> (under the
    /// title) and <see cref="VanillaPanelAnchor.TooltipFooter"/> (the column's very last lines). Refresh: <c>SystemHooks.After&lt;InventoryMenuMapper&gt;</c>.
    /// </summary>
    InventoryTooltip = 6,

    /// <summary>
    /// The recipe tooltip: the window's <c>FakeTooltip</c> while it is shown over a hovered recipe
    /// entry of that window's <c>RecipesParent</c>. Three windows: the crafting workstation
    /// (<c>WorkstationSubMenu</c>, entries <c>WorkstationRecipeGridSelectionEntry</c>), the refinement
    /// station (<c>RefinementstationSubMenu</c>, rows <c>RefinementstationRecipeEntry</c>) and the
    /// character menu's crafting tab (<c>InventorySubMenu.RecipesParent</c>, no station). The target
    /// carries the recipe (<see cref="VanillaPanelTarget.RecipeGuid"/>, the entry's <c>EntryId</c>), the
    /// station and its <c>NetworkId</c>, the item the game's tooltip describes
    /// (<see cref="VanillaPanelTarget.ItemGuid"/>: the product at a workstation and in the crafting tab,
    /// the recipe itself at a refinement station), the product (<see cref="VanillaPanelTarget.ProductGuid"/> /
    /// <see cref="VanillaPanelTarget.ProductAmount"/>, the recipe's <c>RecipeOutputBuffer</c> row 0) and
    /// <see cref="VanillaPanelTarget.Requirements"/> (its <c>RecipeRequirementBuffer</c> rows, in order).
    /// Hover is the entry's <c>IsHovered</c>, which follows the mouse only: with a gamepad the site
    /// stays closed (the game's own tooltip follows its selection group's cursor, not read here).
    /// A new hovered recipe is a <see cref="VanillaPanelChange.TargetChanged"/>. Rows only, cloned from
    /// the tooltip's <c>Description</c>. Anchors: <see cref="VanillaPanelAnchor.TooltipBody"/> (under the
    /// product's description), <see cref="VanillaPanelAnchor.TooltipRequirements"/> (under the
    /// requirement list), <see cref="VanillaPanelAnchor.TooltipRequirementLine"/> (under one
    /// requirement line), <see cref="VanillaPanelAnchor.TooltipHeader"/> (under the title) and
    /// <see cref="VanillaPanelAnchor.TooltipFooter"/> (the very bottom). Refresh: the shared mapper scan.
    /// </summary>
    RecipeTooltip = 7,
}

/// <summary>
/// The per-site facts the runtime and the plan both need, in one place so neither has to know
/// the other's half. Game-free: nothing here is a Unity or a ProjectM type, which is what lets
/// <see cref="VanillaAttachPlan"/> be tested without the game.
/// </summary>
internal readonly struct VanillaUiSiteInfo
{
    internal VanillaUiSiteInfo(
        VanillaUiSite site,
        string key,
        string donor,
        string system,
        bool allowIgnoreLayout,
        VanillaPanelAnchor[]? anchors = null,
        bool buttons = true)
    {
        Site = site;
        Key = key;
        Donor = donor;
        System = system;
        AllowIgnoreLayout = allowIgnoreLayout;
        Anchors = anchors ?? NoAnchors;
        AcceptsButtons = buttons && Anchors.Length > 0;
    }

    private static readonly VanillaPanelAnchor[] NoAnchors = new VanillaPanelAnchor[0];

    internal VanillaUiSite Site { get; }

    /// <summary>Short stable name. Goes in the log lines and in the container's GameObject name.</summary>
    internal string Key { get; }

    /// <summary>Field on the site instance whose GameObject a row is cloned from.</summary>
    internal string Donor { get; }

    /// <summary>Managed system whose <c>Update</c> drives the refresh.</summary>
    internal string System { get; }

    /// <summary>
    /// Whether a <c>LayoutElement { ignoreLayout = true }</c> on our container is an acceptable
    /// answer when the chosen parent turns out to be governed by a layout group, or whether the
    /// container must instead be re-parented to the canvas and positioned from the widget's
    /// world corners. False forces the re-parent.
    /// </summary>
    internal bool AllowIgnoreLayout { get; }

    /// <summary>Anchors the site accepts, default first. Empty for the map card.</summary>
    internal VanillaPanelAnchor[] Anchors { get; }

    /// <summary>Whether <c>Button</c> works on this site (panels yes; the map card and the tooltip no).</summary>
    internal bool AcceptsButtons { get; }
}

/// <summary>Lookup over <see cref="VanillaUiSite"/>.</summary>
internal static class VanillaUiSites
{
    private static readonly VanillaUiSiteInfo MapWarEvent = new(
        VanillaUiSite.MapWarEventInfo,
        key: "MapWarEventInfo",
        donor: "TimerText",
        system: "MapMenuMapper",
        allowIgnoreLayout: true);

    private static readonly VanillaUiSiteInfo Container = new(
        VanillaUiSite.ContainerPanel,
        key: "ContainerPanel",
        donor: "BottomBarButtonsParent button",
        system: "ContainerMenuMapper",
        allowIgnoreLayout: true,
        anchors: new[] { VanillaPanelAnchor.LeftEdge, VanillaPanelAnchor.RightEdge, VanillaPanelAnchor.BesideRename });

    private static readonly VanillaUiSiteInfo Station = new(
        VanillaUiSite.StationPanel,
        key: "StationPanel",
        donor: "BottomBarButtonsParent button",
        system: "WorkstationMenuMapper",
        allowIgnoreLayout: true,
        anchors: new[] { VanillaPanelAnchor.LeftEdge, VanillaPanelAnchor.RightEdge, VanillaPanelAnchor.BesideRename });

    private static readonly VanillaUiSiteInfo Inventory = new(
        VanillaUiSite.InventoryPanel,
        key: "InventoryPanel",
        donor: "BottomBarButtonsParent button",
        system: "InventoryMenuMapper",
        allowIgnoreLayout: true,
        anchors: new[] { VanillaPanelAnchor.BesideSort });

    private static readonly VanillaUiSiteInfo CastleHeart = new(
        VanillaUiSite.CastleHeartPanel,
        key: "CastleHeartPanel",
        donor: "BottomBarButtonsParent button",
        system: "CastleHeartMenuMapper",
        allowIgnoreLayout: true,
        anchors: new[] { VanillaPanelAnchor.LeftEdge, VanillaPanelAnchor.RightEdge });

    private static readonly VanillaUiSiteInfo Tooltip = new(
        VanillaUiSite.InventoryTooltip,
        key: "InventoryTooltip",
        donor: "Description",
        system: "InventoryMenuMapper",
        allowIgnoreLayout: true,
        anchors: new[] { VanillaPanelAnchor.TooltipBody, VanillaPanelAnchor.TooltipHeader, VanillaPanelAnchor.TooltipFooter },
        buttons: false);

    private static readonly VanillaUiSiteInfo Recipe = new(
        VanillaUiSite.RecipeTooltip,
        key: "RecipeTooltip",
        donor: "Description",
        system: "WorkstationMenuMapper",
        allowIgnoreLayout: true,
        anchors: new[] { VanillaPanelAnchor.TooltipBody, VanillaPanelAnchor.TooltipRequirements, VanillaPanelAnchor.TooltipRequirementLine, VanillaPanelAnchor.TooltipHeader, VanillaPanelAnchor.TooltipFooter },
        buttons: false);

    /// <summary>
    /// A cast integer is a legal <c>enum</c> value in C#, so an undefined one would otherwise
    /// reach the resolver as a site nobody has read. Refuse at the door, the way
    /// <c>Ui.CaptureInput</c> refuses an undefined <c>UiCaptureMode</c>.
    /// </summary>
    internal static bool IsDefined(VanillaUiSite site) =>
        site >= VanillaUiSite.MapWarEventInfo && site <= VanillaUiSite.RecipeTooltip;

    /// <summary>True for the P6 panel sites (everything but the map card).</summary>
    internal static bool IsPanel(VanillaUiSite site) => IsDefined(site) && site != VanillaUiSite.MapWarEventInfo;

    internal static VanillaUiSiteInfo Info(VanillaUiSite site) => site switch
    {
        VanillaUiSite.ContainerPanel => Container,
        VanillaUiSite.StationPanel => Station,
        VanillaUiSite.InventoryPanel => Inventory,
        VanillaUiSite.CastleHeartPanel => CastleHeart,
        VanillaUiSite.InventoryTooltip => Tooltip,
        VanillaUiSite.RecipeTooltip => Recipe,
        _ => MapWarEvent,
    };

    /// <summary>The site's short name, or <c>site-&lt;n&gt;</c> for a cast integer.</summary>
    internal static string Key(VanillaUiSite site) =>
        IsDefined(site) ? Info(site).Key : "site-" + (int)site;

    /// <summary>
    /// Resolve <paramref name="anchor"/> for <paramref name="site"/>: <see cref="VanillaPanelAnchor.Default"/>
    /// becomes the site's first listed anchor; an anchor the site does not list returns false. The
    /// map card has no anchors and accepts only <see cref="VanillaPanelAnchor.Default"/>.
    /// </summary>
    internal static bool TryResolveAnchor(VanillaUiSite site, VanillaPanelAnchor anchor, out VanillaPanelAnchor resolved)
    {
        resolved = VanillaPanelAnchor.Default;
        if (!IsDefined(site)) return false;
        var list = Info(site).Anchors;
        if (list.Length == 0) return anchor == VanillaPanelAnchor.Default;
        if (anchor == VanillaPanelAnchor.Default)
        {
            resolved = list[0];
            return true;
        }

        for (int i = 0; i < list.Length; i++)
        {
            if (list[i] != anchor) continue;
            resolved = anchor;
            return true;
        }

        return false;
    }

    /// <summary>
    /// The options every site's frame watch is installed with: <c>OnlyWhenSystemRuns = false</c>.
    /// Every driving mapper (<c>MapMenuMapper</c> and the eight window mappers) only updates while its
    /// own queries match, so with the default option a watch skips frames and can miss an open or
    /// never see a close. The watch has to run on exactly the frames the mapper does not. A new
    /// instance per call, because <c>HookOptions</c> is mutable.
    /// </summary>
    internal static HookOptions WatchOptions(VanillaUiSite site) => new() { OnlyWhenSystemRuns = false };

    /// <summary>The tooltip sites: rebuilt on every hover, rows only, per-hover edges not logged.</summary>
    internal static bool IsTooltip(VanillaUiSite site) =>
        site == VanillaUiSite.InventoryTooltip || site == VanillaUiSite.RecipeTooltip;

    /// <summary>Whether <paramref name="anchor"/> puts the container into a tooltip's text column.</summary>
    internal static bool IsTooltipAnchor(VanillaPanelAnchor anchor) =>
        anchor == VanillaPanelAnchor.TooltipBody || anchor == VanillaPanelAnchor.TooltipRequirements
        || anchor == VanillaPanelAnchor.TooltipRequirementLine || anchor == VanillaPanelAnchor.TooltipHeader
        || anchor == VanillaPanelAnchor.TooltipFooter;

    /// <summary>
    /// How many requirement lines the game's recipe tooltip can show: its requirement list holds six
    /// pooled rows (<c>RecipeRequiredItem</c> .. <c>RecipeRequiredItem (5)</c> on 1.1.13.0).
    /// </summary>
    internal const int MaxRequirementLines = 6;

    /// <summary>Whether <paramref name="line"/> names a requirement line the tooltip can show.</summary>
    internal static bool IsRequirementLine(int line) => line >= 0 && line < MaxRequirementLines;

    /// <summary>The container name suffix of a per-line attachment (<c>line0</c>, <c>line1</c>, ...).</summary>
    internal static string LineKey(int line) => "line" + line.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Whether a per-line container is drawn: the plugin wants it shown and the game shows that line's
    /// own row (a recipe with fewer lines hides it with the game's unused rows). Game-free.
    /// </summary>
    internal static bool LineShown(bool wanted, bool gameLineShown) => wanted && gameLineShown;

    /// <summary>
    /// Whether the tooltip's shown requirement lines are the target's requirements one for one: the game leaves
    /// out a requirement it has no item data for, which shifts every later line, so a per-line container is shown
    /// only when the shown line count equals the requirement count. Game-free.
    /// </summary>
    internal static bool RequirementLinesMatch(int shownLines, int requirements) => shownLines > 0 && shownLines == requirements;

    /// <summary>Whether <paramref name="anchor"/> is one of the column anchors placed by Progenitor.</summary>
    internal static bool IsEdge(VanillaPanelAnchor anchor) =>
        anchor == VanillaPanelAnchor.LeftEdge || anchor == VanillaPanelAnchor.RightEdge;

    /// <summary>
    /// Whether <paramref name="anchor"/> is an inline anchor in a window's title row
    /// (<see cref="VanillaPanelAnchor.BesideRename"/>), whose row is the height of the game's small icon buttons.
    /// </summary>
    internal static bool IsTitleRow(VanillaPanelAnchor anchor) => anchor == VanillaPanelAnchor.BesideRename;

    /// <summary>
    /// A button with <paramref name="icon"/> on <paramref name="anchor"/> is always its square icon face, the caption
    /// its hover tooltip: a title-row anchor and an icon the caller chose. A caption-only button keeps its text.
    /// </summary>
    internal static bool IconAlways(VanillaPanelAnchor anchor, VanillaButtonIcon icon) => IsTitleRow(anchor) && icon.Chosen;

    /// <summary>
    /// A button on <paramref name="anchor"/> gets an icon face at all: always on <see cref="VanillaPanelAnchor.BesideSort"/>
    /// (the fallback when the captions do not fit), and where <see cref="IconAlways"/> holds.
    /// </summary>
    internal static bool HasIconFace(VanillaPanelAnchor anchor, VanillaButtonIcon icon) =>
        anchor == VanillaPanelAnchor.BesideSort || IconAlways(anchor, icon);

    /// <summary>The anchors <paramref name="site"/> accepts, default first, as their short keys (readback).</summary>
    internal static string[] AnchorKeys(VanillaUiSite site)
    {
        if (!IsDefined(site)) return new string[0];
        var list = Info(site).Anchors;
        var keys = new string[list.Length];
        for (int i = 0; i < list.Length; i++) keys[i] = AnchorKey(list[i]);
        return keys;
    }

    /// <summary>The short suffix the container name carries for an anchor (<c>left</c>, <c>sort</c>, ...).</summary>
    internal static string AnchorKey(VanillaPanelAnchor anchor) => anchor switch
    {
        VanillaPanelAnchor.LeftEdge => "left",
        VanillaPanelAnchor.RightEdge => "right",
        VanillaPanelAnchor.BesideRename => "rename",
        VanillaPanelAnchor.BesideSort => "sort",
        VanillaPanelAnchor.TooltipBody => "tooltip",
        VanillaPanelAnchor.TooltipRequirements => "requirements",
        VanillaPanelAnchor.TooltipRequirementLine => "line",
        VanillaPanelAnchor.TooltipHeader => "header",
        VanillaPanelAnchor.TooltipFooter => "footer",
        _ => "",
    };
}
