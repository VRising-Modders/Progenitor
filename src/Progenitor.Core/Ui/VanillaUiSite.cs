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
        bool allowIgnoreLayout)
    {
        Site = site;
        Key = key;
        Donor = donor;
        System = system;
        AllowIgnoreLayout = allowIgnoreLayout;
    }

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
}

/// <summary>Lookup over <see cref="VanillaUiSite"/>. One entry today.</summary>
internal static class VanillaUiSites
{
    private static readonly VanillaUiSiteInfo MapWarEvent = new(
        VanillaUiSite.MapWarEventInfo,
        key: "MapWarEventInfo",
        donor: "TimerText",
        system: "MapMenuMapper",
        allowIgnoreLayout: true);

    /// <summary>
    /// A cast integer is a legal <c>enum</c> value in C#, so an undefined one would otherwise
    /// reach the resolver as a site nobody has read. Refuse at the door, the way
    /// <c>Ui.CaptureInput</c> refuses an undefined <c>UiCaptureMode</c>.
    /// </summary>
    internal static bool IsDefined(VanillaUiSite site) => site == VanillaUiSite.MapWarEventInfo;

    internal static VanillaUiSiteInfo Info(VanillaUiSite site) => MapWarEvent;

    /// <summary>The site's short name, or <c>site-&lt;n&gt;</c> for a cast integer.</summary>
    internal static string Key(VanillaUiSite site) =>
        IsDefined(site) ? Info(site).Key : "site-" + (int)site;
}
