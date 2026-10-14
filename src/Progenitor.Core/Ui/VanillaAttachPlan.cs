namespace Progenitor.Ui;

/// <summary>What one refresh tick must do to the mod container.</summary>
internal enum VanillaAttachAction
{
    /// <summary>The container is where it should be. The common case, once per frame.</summary>
    Keep = 0,

    /// <summary>No container yet, and the site is ready for one.</summary>
    Build = 1,

    /// <summary>A container exists but is stale (the site instance changed, or Unity destroyed it).</summary>
    Rebuild = 2,

    /// <summary>The site is not there this frame. Any container goes; the handle stays valid.</summary>
    Degrade = 3,
}

/// <summary>Where the container is parented.</summary>
internal enum VanillaAttachParent
{
    /// <summary>Nowhere: the decision is <see cref="VanillaAttachAction.Degrade"/> or <see cref="VanillaAttachAction.Keep"/>.</summary>
    None = 0,

    /// <summary>
    /// A sibling of the widget root, i.e. a child of the widget's own parent. The default, and
    /// the reason disposal never has to touch a vanilla object.
    /// </summary>
    WidgetSibling = 1,

    /// <summary>
    /// A child of the site's root canvas, positioned from the widget's world corners. Taken only
    /// when the sibling parent is governed by a layout group and the site forbids
    /// <c>ignoreLayout</c> — because a laid-out sibling would <i>move the vanilla card</i>, which
    /// is a visible regression the player did not ask for.
    /// </summary>
    CanvasRoot = 2,
}

/// <summary>
/// What the runtime saw at the site this frame. Game-free by construction: an instance id, three
/// booleans and a name — never a <c>GameObject</c> — which is what lets the whole decision be
/// tested without the game, exactly as <c>PanelLayout</c> and <c>UiSettleGate</c> are.
/// </summary>
internal readonly struct VanillaSiteSample
{
    internal VanillaSiteSample(
        bool resolved,
        int instanceId,
        bool donorPresent,
        bool parentLayoutGroup,
        string? parentName = null,
        bool parentSizeFitter = false,
        VanillaPanelTarget target = default)
    {
        Resolved = resolved;
        InstanceId = instanceId;
        DonorPresent = donorPresent;
        ParentLayoutGroup = parentLayoutGroup;
        ParentSizeFitter = parentSizeFitter;
        ParentName = parentName ?? "";
        Target = target;
    }

    /// <summary>
    /// P6: what the panel shows this frame (the container or station entity, its <c>NetworkId</c>,
    /// the hovered item for the tooltip). Meaningful only when <see cref="Resolved"/>; for a panel
    /// site <see cref="Resolved"/> is "the panel is open".
    /// </summary>
    internal VanillaPanelTarget Target { get; }

    /// <summary>The site instance was found this frame (for the map card: <c>GetMapMenu()</c> was not null).</summary>
    internal bool Resolved { get; }

    /// <summary>
    /// <c>GetInstanceID()</c> of the resolved widget. The whole close/reopen rule rests on this
    /// one number: a changed id means rebuild, an unchanged id means the container is still on
    /// the object it was parented to. It is correct whether the game destroys the map view on
    /// close or merely deactivates it, which is why that unknown (research §1.4 / N2) does not
    /// block the design.
    /// </summary>
    internal int InstanceId { get; }

    /// <summary>The site's donor widget (for the map card, <c>TimerText</c>) is alive.</summary>
    internal bool DonorPresent { get; }

    /// <summary>
    /// A <c>LayoutGroup</c> governs the sibling parent. This is the one that decides the
    /// placement: a group lays our container out and <i>moves the vanilla card to make room</i>,
    /// which is what <c>ignoreLayout</c> (or, where a site forbids it, the canvas parent) exists
    /// to prevent.
    /// </summary>
    internal bool ParentLayoutGroup { get; }

    /// <summary>
    /// A <c>ContentSizeFitter</c> is on the sibling parent. Reported, never acted on, and
    /// deliberately not merged with <see cref="ParentLayoutGroup"/> (R60 finding 2): a fitter
    /// lays no child out — it sizes the parent from <c>ILayoutElement</c>s on the parent itself —
    /// so it can never move the card, and <c>LayoutElement { ignoreLayout = true }</c> on our
    /// child means nothing to it. Merging the two made the one-shot hierarchy line, which exists
    /// to answer exactly this question for the first client spike, answer neither half of it.
    /// </summary>
    internal bool ParentSizeFitter { get; }

    /// <summary>Parent GameObject's name, for the log line only.</summary>
    internal string ParentName { get; }

    /// <summary>Nothing at the site this frame — the map is closed, or the view is gone.</summary>
    internal static VanillaSiteSample Absent => new(resolved: false, 0, false, false);
}

/// <summary>What the attachment currently holds, as the plan needs to see it.</summary>
internal readonly struct VanillaAttachState
{
    internal VanillaAttachState(bool hasContainer, int attachedInstanceId)
    {
        HasContainer = hasContainer;
        AttachedInstanceId = attachedInstanceId;
    }

    /// <summary>A container object exists <b>and is alive</b>. A destroyed one is not one.</summary>
    internal bool HasContainer { get; }

    /// <summary>Instance id of the widget the live container is parented beside.</summary>
    internal int AttachedInstanceId { get; }

    internal static VanillaAttachState Empty => new(false, 0);
}

/// <summary>One tick's verdict.</summary>
internal readonly struct VanillaAttachDecision
{
    internal VanillaAttachDecision(
        VanillaAttachAction action,
        VanillaAttachParent parent,
        bool ignoreLayout,
        bool destroyOld,
        string reason)
    {
        Action = action;
        Parent = parent;
        IgnoreLayout = ignoreLayout;
        DestroyOld = destroyOld;
        Reason = reason;
    }

    internal VanillaAttachAction Action { get; }

    internal VanillaAttachParent Parent { get; }

    /// <summary>Add a <c>LayoutElement { ignoreLayout = true }</c> so the vanilla layout does not see us.</summary>
    internal bool IgnoreLayout { get; }

    /// <summary>There is a live container to destroy before anything else happens.</summary>
    internal bool DestroyOld { get; }

    /// <summary>One word for the log line: <c>first</c>, <c>instance</c>, <c>gone</c>, <c>site</c>, <c>donor</c>, <c>undefined</c>.</summary>
    internal string Reason { get; }

    /// <summary>
    /// The <c>layout=</c> field of the attach log line, in the style <c>docs/ui.md</c> already
    /// uses for <c>ruler=</c> and <c>settle=</c>: it names the branch that was actually taken,
    /// because which branch a run took is the fact the offline research could not supply.
    /// </summary>
    internal string LayoutWord =>
        Parent == VanillaAttachParent.CanvasRoot ? "reparented"
        : IgnoreLayout ? "ignored"
        : "none";
}

/// <summary>
/// The whole decision behind <c>Ui.AttachToVanilla</c>'s per-frame refresh, with no Unity in it
/// (job FUI36). Everything the Unity half does is read the site, hand the reading to
/// <see cref="Decide"/>, and carry out the answer.
///
/// <para><b>Why it is a type of its own.</b> The same reason <see cref="UiSettleGate"/> is: the
/// test project does not compile <c>UnityUiRuntime.cs</c>, so anything written inline there is
/// covered by source pins over text and by nothing else. Close/reopen, "the site went away",
/// "the game destroyed our container" and the layout branch are the four things this capability
/// can get wrong, and all four are decidable from an instance id and three booleans.</para>
///
/// <para><b>The rules, and what each one costs if it is wrong.</b> A rebuild that forgets to
/// destroy the old container leaves a frozen duplicate parented to a vanilla object nobody owns
/// — visible, unownable, and not reachable by <c>DestroyRoot</c> because it does not live under
/// the plugin's DontDestroyOnLoad root (research §4.4). A <i>missing</i> rebuild leaves the rows
/// on a widget the game has replaced, which reads as "the mod stopped working after I closed the
/// map". And treating "no site this frame" as a failure rather than as a state would tear the
/// handle down every time the player closes the map.</para>
/// </summary>
internal static class VanillaAttachPlan
{
    /// <summary>
    /// Decide what this frame does. Never throws, never allocates on the <see cref="VanillaAttachAction.Keep"/>
    /// path — which is every frame the map is open and nothing has changed.
    /// </summary>
    internal static VanillaAttachDecision Decide(
        VanillaUiSite site,
        in VanillaSiteSample sample,
        in VanillaAttachState state)
    {
        // An undefined cast value is not a site anybody has read. It can never attach, and it
        // must not silently behave like the one site that does exist.
        if (!VanillaUiSites.IsDefined(site))
            return Detach(state, "undefined");

        // The map is closed, or the view is gone. Not a failure: the handle stays valid and the
        // next open re-attaches. The container goes now rather than being left parented to
        // something on its way out.
        if (!sample.Resolved)
            return Detach(state, "site");

        // A site whose donor is gone cannot produce a row in the game's own font, and a row in
        // some other font is worse than no row: it would look like the mod, not like the game.
        if (!sample.DonorPresent)
            return Detach(state, "donor");

        // Only the layout *group* decides this. A ContentSizeFitter on the parent is carried in
        // the sample and printed, and it changes nothing here: it sizes the parent from the
        // parent's own ILayoutElements and lays no child out, so it can neither move the vanilla
        // card nor be neutralized by an ignoreLayout on our child. Claiming layout=ignored for
        // one would be a log line asserting a neutralization that never happened (R60 finding 2).
        var info = VanillaUiSites.Info(site);
        var parent = sample.ParentLayoutGroup && !info.AllowIgnoreLayout
            ? VanillaAttachParent.CanvasRoot
            : VanillaAttachParent.WidgetSibling;
        bool ignoreLayout = sample.ParentLayoutGroup && parent == VanillaAttachParent.WidgetSibling;

        if (state.HasContainer)
        {
            if (state.AttachedInstanceId == sample.InstanceId)
                return new VanillaAttachDecision(
                    VanillaAttachAction.Keep, VanillaAttachParent.None, false, false, "same");

            // The widget the container is parented beside is not the widget that is there now.
            // Destroying first is not tidiness: the old container is still a child of whatever
            // the old parent was, and on the deactivate-and-reuse branch that parent is still
            // alive, so skipping the destroy is exactly how a duplicate appears on reopen.
            return new VanillaAttachDecision(
                VanillaAttachAction.Rebuild, parent, ignoreLayout, destroyOld: true, "instance");
        }

        // No live container. Either this is the first sight of the site, or Unity destroyed ours
        // out from under us (map view teardown), which is the same branch and deliberately so.
        return new VanillaAttachDecision(
            VanillaAttachAction.Build, parent, ignoreLayout, destroyOld: false, "first");
    }

    private static VanillaAttachDecision Detach(in VanillaAttachState state, string reason) =>
        new(VanillaAttachAction.Degrade, VanillaAttachParent.None, false, state.HasContainer, reason);
}
