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

    /// <summary>Container active flag. Rows keep their text while hidden.</summary>
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
}
