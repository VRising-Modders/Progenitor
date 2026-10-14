using System;
using System.Collections.Generic;

namespace Progenitor.Ui;

/// <summary>
/// Mod rows hosted beside a vanilla widget (job FUI36). Returned by
/// <see cref="Ui.AttachToVanilla(VanillaUiSite, object?, string?)"/>, which is <b>never null</b>:
/// on the server, with the module skipped, or with an undefined site it hands back a disabled but
/// valid handle, the same shape <c>Ui.Panel</c> has always had.
///
/// <para>
/// <b>What it does not do, and why.</b> It never writes a vanilla object. The rows are our own
/// GameObjects in a container that is a <i>sibling</i> of the vanilla widget, so disposal is one
/// <c>Destroy</c> with nothing to restore: no sibling order to undo, no vanilla component to
/// re-enable, no field to put back. It never calls the widget's own <c>SetData</c> — that method
/// takes a 160-byte struct by reference and the interop marshaler throws before the native call
/// (research <c>war-events.md</c> §1.5) — and it never patches the driving system with Harmony,
/// because the seam is <c>SystemHooks</c> on the managed <c>SystemBase.Update</c> and therefore
/// cannot reach a Burst job even by mistake.
/// </para>
///
/// <para>
/// <b>Teardown, and the one hole this capability opens.</b> The container is parented into the
/// vanilla tree, so it is <b>not</b> under the plugin's <c>progenitor-ui-&lt;pluginId&gt;</c>
/// DontDestroyOnLoad root and neither <c>DestroyRoot</c> nor the Uninstall prefix sweep can reach
/// it. Every attachment is therefore tracked in a per-owner registry that
/// <see cref="Ui.RemoveAll"/> and Core Uninstall both drain, and the count appears in
/// <c>pin-diag Ui</c>. An untracked attachment would survive a Core swap as a frozen GameObject
/// with a dead text writer. <b>Dispose is not always immediate</b> (FUI38): when Unity defers or
/// refuses the container's destroy, the handle stays in the registry with the object on its doomed
/// list and the site watch alive until the destroy is confirmed - up to thirty frames, then a
/// named leak line - so <see cref="IsDisposed"/> can be true while the container is still on screen
/// for one more frame.
/// </para>
/// </summary>
public interface IVanillaAttachHandle : IDisposable
{
    /// <summary>Which vanilla widget this handle hosts rows beside.</summary>
    VanillaUiSite Site { get; }

    /// <summary>BepInEx plugin GUID that owns the container.</summary>
    string PluginId { get; }

    /// <summary>Name of the container GameObject: <c>progenitor-ui-&lt;pluginId&gt;-&lt;site&gt;</c>.</summary>
    string ContainerName { get; }

    /// <summary>
    /// False on the server, when the module is skipped, or for an undefined site. Every setter
    /// becomes a no-op and <see cref="Row"/> hands back a disabled widget. The caller still
    /// disposes it.
    /// </summary>
    bool Enabled { get; }

    /// <summary>
    /// Whether the container is parented to a live site instance <b>right now</b>. False while
    /// the map is closed — which is a state, not a failure: the next refresh re-attaches, the
    /// row texts are kept, and nothing the plugin has to do changes.
    /// </summary>
    bool IsAttached { get; }

    /// <summary>True after <see cref="IDisposable.Dispose"/>, including a second call.</summary>
    bool IsDisposed { get; }

    /// <summary>
    /// The plugin's wish to show the attachment. Kept across close and reopen: the container is
    /// drawn only while the site is open, attached, and (panel sites) the placement fits, and then
    /// only if this is true. Rows and buttons keep their text while hidden.
    /// </summary>
    bool Visible { get; set; }

    /// <summary>How many rows this attachment holds.</summary>
    int RowCount { get; }

    /// <summary>
    /// Add a row. Clones the site's donor widget so the row carries the game's own font, size,
    /// color and material — nothing is shipped — then strips its <c>LocalizedText</c> (which
    /// would otherwise blank the row on the next locale change or localization-version bump) and
    /// sets <c>raycastTarget = false</c> so a mod row can never eat a click meant for the map.
    ///
    /// <para>
    /// The returned handle survives a rebuild: when the map is closed and reopened the row's
    /// GameObject is cloned again and the handle keeps writing to whichever one is current. A
    /// row is addressed by its position, so text written while detached is applied on the next
    /// attach.
    /// </para>
    /// </summary>
    IUiLabel Row(string text);

    /// <summary>
    /// Set every row at once, growing or shrinking the row list to match. The call a per-frame
    /// (or per-second) refresh makes: it is the whole display, so a caller cannot leave a stale
    /// row behind by forgetting one. Null entries render as empty.
    /// </summary>
    void SetRows(IReadOnlyList<string> rows);

    /// <summary>
    /// P6: where on the panel this attachment sits, after <see cref="VanillaPanelAnchor.Default"/>
    /// was resolved to the site's default. <see cref="VanillaPanelAnchor.Default"/> for the map card.
    /// </summary>
    VanillaPanelAnchor Anchor => VanillaPanelAnchor.Default;

    /// <summary>
    /// P6: the panel is open right now (for the tooltip site: a tooltip is shown over an item).
    /// Always false for the map card, which reports no open state.
    /// </summary>
    bool IsOpen => false;

    /// <summary>P6: what the open panel shows; <see cref="VanillaPanelTarget.None"/> while closed.</summary>
    VanillaPanelTarget Target => VanillaPanelTarget.None;

    /// <summary>
    /// P6: be told when the panel opens, closes or starts showing another target. Runs on the main
    /// thread inside the site's frame watch, <b>before</b> this frame's rows and buttons are
    /// synchronized, so a <see cref="SetRows"/> from the handler shows on the same frame. Subscribing
    /// while the panel is open delivers one <see cref="VanillaPanelChange.Opened"/> straight away.
    /// Dispose the returned lease (or the handle) to unsubscribe. A disabled handle never calls it.
    /// </summary>
    IDisposable OnPanel(Action<VanillaPanelEvent> handler) => NoLease.Instance;

    /// <summary>
    /// P6: add a button. On the panel sites it is a clone of the game's own inventory bar button
    /// (its art, hover and press states), captioned in the window's own font, and sized and spaced by
    /// the vanilla group (inline anchors) or by the column (edge anchors). Like a row it is
    /// addressed by position and survives the panel being closed and reopened: the caption and the
    /// click handler live on the handle, and the clone is rewired to the same Bootstrap click slot
    /// each time it is rebuilt. A button has <b>one</b> click handler, as every <c>IUiButton</c>
    /// does: a later <c>OnClick</c> replaces the earlier one, and disposing the earlier lease then
    /// does nothing. On the map card and the tooltip site the button is disabled.
    /// </summary>
    IUiButton Button(string caption) => DisabledAttachButton.Instance;

    /// <summary>How many buttons this attachment holds.</summary>
    int ButtonCount => 0;
}

/// <summary>An inert lease for defaulted members.</summary>
internal sealed class NoLease : IDisposable
{
    internal static readonly NoLease Instance = new();

    public void Dispose() { }
}

/// <summary>The button a site without buttons hands back: valid, disabled, inert.</summary>
internal sealed class DisabledAttachButton : IUiButton
{
    internal static readonly DisabledAttachButton Instance = new();

    public string Text { get => ""; set { } }

    public bool Visible { get => false; set { } }

    public bool Enabled => false;

    public bool IsDisposed => false;

    public UiHandleState State => UiHandleState.Disabled;

    public string PluginId => UiOwnership.UnownedId;

    public string RootName => "";

    public IDisposable OnClick(Action handler) => NoLease.Instance;

    public void Dispose() { }
}
