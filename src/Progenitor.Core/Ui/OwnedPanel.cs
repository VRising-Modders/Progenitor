using System;

namespace Progenitor.Ui;

/// <summary>
/// The field a module keeps its own panel in, when "is my panel open?" has to stay true
/// (job FUI29).
///
/// <para><b>The defect this exists for.</b> A panel is hidden by paths its owner never runs.
/// <c>Ui.OnBootstrapKey</c> hides <i>every</i> shown panel on Escape and tells nobody;
/// <c>Ui.Uninstall</c> disposes every live handle; a plugin sweep does the same for one owner. A
/// module that kept <c>private static IUiPanel? _panel</c> and answered <c>_panel != null</c> then
/// said "open" about a panel that had left the screen, for as long as the process lived. Measured
/// twice on the live client: the deny panel vanished on Escape with no <c>deny panel closed.</c>
/// ever following, and the consent panel did the same in the morning of the same day. The settings
/// panel had it too and it was diagnosed as a settings bug (R34 panel note 1) rather than as this.
/// </para>
///
/// <para><b>Why derivation and not a callback.</b> The house rule is that where a value has two
/// representations, one derives from the other. A <c>Hidden</c> event on <see cref="IUiPanel"/>
/// would be a second representation of a fact the handle already holds — and a second
/// representation is a thing that can be missed, which is precisely the failure being fixed. It
/// would also have to be raised from inside the Escape sweep, so one owner's handler throwing, or
/// re-opening its panel, would run in the middle of another owner's teardown. There is nothing to
/// miss here: <see cref="IUiHandle.State"/> is the panel's own state, written by the same
/// <c>Hide</c> that took it off the screen, and this type is a read of it.
/// </para>
///
/// <para><b>No side effects in a getter.</b> <see cref="IsOpen"/> and <see cref="Live"/> only read.
/// They deliberately do not dispose the handle they find stale: a getter that disposes could be
/// reached from a diagnostic — or from another thread during <see cref="Adopt"/> — and destroy a
/// panel that is mid-build. The stale handle is released by <see cref="Take"/>, which every
/// owner's <c>Close</c> already runs and which every owner's <c>Open</c> already calls first. The
/// residual is therefore one hidden, undisposed panel per owner if the player escapes it and never
/// touches that module again, until the next Open, <c>Ui.RemoveAll</c> or Core Uninstall — which is
/// what the code did before this type existed, minus the lie.
/// </para>
/// </summary>
public sealed class OwnedPanel
{
    private readonly object _gate = new();
    private IUiPanel? _panel;

    /// <summary>
    /// True while the held panel is still standing between its owner and a fresh
    /// <c>Open</c> — that is, while opening another one would achieve nothing. Derived from
    /// <see cref="IUiHandle.State"/> on every read; never cached.
    /// </summary>
    public bool IsOpen => Live != null;

    /// <summary>
    /// The held panel while <see cref="IsOpen"/>, otherwise null. Use it to touch widgets that
    /// belong to a panel that must still be on screen — a row refresh, a caption rewrite — so the
    /// write cannot land on a panel the player has already dismissed.
    /// </summary>
    public IUiPanel? Live
    {
        get
        {
            IUiPanel? panel;
            lock (_gate) panel = _panel;
            return IsStanding(panel) ? panel : null;
        }
    }

    /// <summary>
    /// Whatever handle is held, on screen or not. For diagnostics only: an owner asking "am I
    /// open" must ask <see cref="IsOpen"/>, and an owner closing must use <see cref="Take"/>.
    /// </summary>
    public IUiPanel? Held
    {
        get { lock (_gate) return _panel; }
    }

    /// <summary>
    /// Record the panel this owner just built. Any handle already held is returned so the caller
    /// can dispose it; in practice every owner calls its own <c>Close</c> first and this is null.
    /// </summary>
    public IUiPanel? Adopt(IUiPanel panel)
    {
        if (panel == null) throw new ArgumentNullException(nameof(panel));
        lock (_gate)
        {
            var previous = _panel;
            _panel = panel;
            return ReferenceEquals(previous, panel) ? null : previous;
        }
    }

    /// <summary>
    /// Clear the field and hand the handle back for disposal, or null when there was none. This
    /// is the release path, and it does <b>not</b> consult <see cref="IsOpen"/>: a panel that was
    /// hidden by Escape is exactly the one that still has to be disposed, and a panel adopted but
    /// not yet shown must not be leaked either.
    /// </summary>
    public IUiPanel? Take()
    {
        lock (_gate)
        {
            var panel = _panel;
            _panel = null;
            return panel;
        }
    }

    /// <summary>
    /// Whether a handle is still standing. <see cref="UiHandleState.Shown"/> is the live panel.
    /// <see cref="UiHandleState.Disabled"/> counts too — that is the server, the skipped module or
    /// the failed overlay, where there is nothing to show and re-opening on every key press would
    /// only churn (the rule <c>SettingsPanel</c> arrived at first, R34 panel note 1).
    /// <see cref="UiHandleState.Built"/> means it was shown and something hid it, and
    /// <see cref="UiHandleState.Disposed"/> means it is gone: both are closed.
    ///
    /// <para>An unreadable handle is treated as still standing. Failing the other way would let a
    /// module stack a second panel on top of one that is still on screen, and "I could not read
    /// it" is not evidence that it left.</para>
    /// </summary>
    private static bool IsStanding(IUiPanel? panel)
    {
        if (panel == null) return false;
        try
        {
            if (panel.IsDisposed) return false;
            return panel.State == UiHandleState.Shown || panel.State == UiHandleState.Disabled;
        }
        catch
        {
            return true;
        }
    }
}
