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
    /// (inside <c>ContainerSubMenu.OpenTagEditButtonParent</c>, after
    /// <c>OpenTagEditButton</c>).</description></item>
    /// <item><description><b>Donors:</b> buttons clone the inventory bottom bar's
    /// <c>SelfSortButton</c>; captions clone the label of the inventory's own tab buttons and rows
    /// clone the tooltip's body text (<c>FakeTooltip.Description</c>).</description></item>
    /// <item><description><b>Refresh:</b> <c>SystemHooks.After&lt;ContainerMenuMapper&gt;</c> with
    /// <c>OnlyWhenSystemRuns = false</c> (every panel site uses that option: the mappers only run
    /// while their queries match, and a close must be seen), reading every mapper from one shared
    /// per-frame scan.</description></item>
    /// </list>
    /// </summary>
    ContainerPanel = 2,

    /// <summary>
    /// The crafting workstation and refinement station windows: <c>WorkstationMenu</c>
    /// (<c>WorkstationMenuMapper._WorkstationSubMenu</c>, <c>._Target</c>) and
    /// <c>RefinementstationMenu</c> (<c>RefinementstationMenuMapper._RefinementStationSubMenu</c>,
    /// <c>._Target</c>). <see cref="VanillaPanelTarget.Kind"/> says which.
    /// Anchors: <see cref="VanillaPanelAnchor.LeftEdge"/> (default) and
    /// <see cref="VanillaPanelAnchor.RightEdge"/>. Refresh: <c>SystemHooks.After&lt;WorkstationMenuMapper&gt;</c>,
    /// which reads both mappers.
    /// </summary>
    StationPanel = 3,

    /// <summary>
    /// The player inventory, wherever the game shows it: the character menu's
    /// <c>InventoryMenu</c> and the inventory half of the container, station and castle heart
    /// windows (each mapper's <c>_InventorySubMenu</c>; the active one wins). Anchor:
    /// <see cref="VanillaPanelAnchor.BesideSort"/> (inside
    /// <c>InventorySubMenu.BottomBarButtonsParent</c>, right after <c>SelfSortButton</c>).
    /// <see cref="VanillaPanelTarget"/> names the window the inventory is shown in and, beside a
    /// container or station, that window's target. Refresh:
    /// <c>SystemHooks.After&lt;InventoryMenuMapper&gt;</c>.
    /// </summary>
    InventoryPanel = 4,

    /// <summary>
    /// The castle heart's own window (blood essence fuel, castle list, upgrades):
    /// <c>CastleHeartMenu</c> (<c>CastleHeartMenuMapper._CastleHeartSubMenu</c>, <c>._Target</c> =
    /// the heart entity). Anchors: <see cref="VanillaPanelAnchor.LeftEdge"/> (default) and
    /// <see cref="VanillaPanelAnchor.RightEdge"/>. Refresh:
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
    /// <see cref="VanillaPanelAnchor.TooltipBody"/>. Refresh: <c>SystemHooks.After&lt;InventoryMenuMapper&gt;</c>.
    /// </summary>
    InventoryTooltip = 6,
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
        donor: "SelfSortButton",
        system: "ContainerMenuMapper",
        allowIgnoreLayout: true,
        anchors: new[] { VanillaPanelAnchor.LeftEdge, VanillaPanelAnchor.RightEdge, VanillaPanelAnchor.BesideRename });

    private static readonly VanillaUiSiteInfo Station = new(
        VanillaUiSite.StationPanel,
        key: "StationPanel",
        donor: "SelfSortButton",
        system: "WorkstationMenuMapper",
        allowIgnoreLayout: true,
        anchors: new[] { VanillaPanelAnchor.LeftEdge, VanillaPanelAnchor.RightEdge });

    private static readonly VanillaUiSiteInfo Inventory = new(
        VanillaUiSite.InventoryPanel,
        key: "InventoryPanel",
        donor: "SelfSortButton",
        system: "InventoryMenuMapper",
        allowIgnoreLayout: true,
        anchors: new[] { VanillaPanelAnchor.BesideSort });

    private static readonly VanillaUiSiteInfo CastleHeart = new(
        VanillaUiSite.CastleHeartPanel,
        key: "CastleHeartPanel",
        donor: "SelfSortButton",
        system: "CastleHeartMenuMapper",
        allowIgnoreLayout: true,
        anchors: new[] { VanillaPanelAnchor.LeftEdge, VanillaPanelAnchor.RightEdge });

    private static readonly VanillaUiSiteInfo Tooltip = new(
        VanillaUiSite.InventoryTooltip,
        key: "InventoryTooltip",
        donor: "Description",
        system: "InventoryMenuMapper",
        allowIgnoreLayout: true,
        anchors: new[] { VanillaPanelAnchor.TooltipBody },
        buttons: false);

    /// <summary>
    /// A cast integer is a legal <c>enum</c> value in C#, so an undefined one would otherwise
    /// reach the resolver as a site nobody has read. Refuse at the door, the way
    /// <c>Ui.CaptureInput</c> refuses an undefined <c>UiCaptureMode</c>.
    /// </summary>
    internal static bool IsDefined(VanillaUiSite site) =>
        site >= VanillaUiSite.MapWarEventInfo && site <= VanillaUiSite.InventoryTooltip;

    /// <summary>True for the P6 panel sites (everything but the map card).</summary>
    internal static bool IsPanel(VanillaUiSite site) => IsDefined(site) && site != VanillaUiSite.MapWarEventInfo;

    internal static VanillaUiSiteInfo Info(VanillaUiSite site) => site switch
    {
        VanillaUiSite.ContainerPanel => Container,
        VanillaUiSite.StationPanel => Station,
        VanillaUiSite.InventoryPanel => Inventory,
        VanillaUiSite.CastleHeartPanel => CastleHeart,
        VanillaUiSite.InventoryTooltip => Tooltip,
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
    /// Every driving mapper (<c>MapMenuMapper</c> and the five window mappers) only updates while its
    /// own queries match, so with the default option a watch skips frames and can miss an open or
    /// never see a close. The watch has to run on exactly the frames the mapper does not. A new
    /// instance per call, because <c>HookOptions</c> is mutable.
    /// </summary>
    internal static HookOptions WatchOptions(VanillaUiSite site) => new() { OnlyWhenSystemRuns = false };

    /// <summary>Whether <paramref name="anchor"/> is one of the column anchors placed by Progenitor.</summary>
    internal static bool IsEdge(VanillaPanelAnchor anchor) =>
        anchor == VanillaPanelAnchor.LeftEdge || anchor == VanillaPanelAnchor.RightEdge;

    /// <summary>The short suffix the container name carries for an anchor (<c>left</c>, <c>sort</c>, ...).</summary>
    internal static string AnchorKey(VanillaPanelAnchor anchor) => anchor switch
    {
        VanillaPanelAnchor.LeftEdge => "left",
        VanillaPanelAnchor.RightEdge => "right",
        VanillaPanelAnchor.BesideRename => "rename",
        VanillaPanelAnchor.BesideSort => "sort",
        VanillaPanelAnchor.TooltipBody => "tooltip",
        _ => "",
    };
}
