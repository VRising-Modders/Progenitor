#if !PROGENITOR_TESTS
using System;
using Progenitor.Plugins;

namespace Progenitor.Inventory;

public sealed partial class PluginInventory
{
    private bool _returnTracked;

    /// <summary>
    /// Client only: the chest, station or castle heart window the player most recently opened at the object, or null.
    /// A remote container preview's window never counts. This is the window <see cref="ReopenLast"/> opens.
    /// </summary>
    public ContainerWindowInfo? LastWindow
    {
        get
        {
            _ctx.EnsureAttached();
            if (!_ctx.IsClient) throw new InvalidOperationException("ctx.Inventory.LastWindow runs on the client.");
            return ContainerReturnClient.LastWindow();
        }
    }

    /// <summary>
    /// Client only: open the chest, station or castle heart window the player last had open again, the game's own way:
    /// Progenitor hovers the object and presses Interact for 0.4 s, as the player would, then hands the input back and
    /// watches for the window (the buttons yield to the player; only the hover overrides the mouse, for that half
    /// second). The server sees an ordinary interaction, with its range and permission checks and every server-side open
    /// handler (the container open gate) in front of it.
    /// <para>Pass <paramref name="openWhenStarted"/>, the <see cref="LastWindow"/> snapshot taken when your flow started
    /// (the hub opened, the Locate click): the window is reopened only if it was open then and it is still that same
    /// opening (<see cref="ContainerWindowInfo.OpenCount"/>). Without it, the last window is reopened whenever it was
    /// opened. Either way a window the player closed themselves (it closed to no other window and no Progenitor flow
    /// closed it) is never reopened (<see cref="ContainerReopenResult.ClosedByPlayer"/>).</para>
    /// <para>Also does nothing (and says why) when the object left this client, the character moved more than a meter
    /// from where it stood, another game window stays open, or another input lease holds the hover or buttons. A window
    /// still open is left alone (<see cref="ContainerReopenResult.AlreadyOpen"/>), so it is safe to call after any flow
    /// that may or may not have closed it. Returns <see cref="ContainerReopenResult.Started"/> or the reason it did not
    /// start; <paramref name="done"/> runs once on the main thread with the outcome. A newer reopen replaces a running
    /// one. Main thread. <b>Experimental</b>: source-checked and game-free tested, not live-verified.</para>
    /// </summary>
    public ContainerReopenResult ReopenLast(ContainerWindowInfo? openWhenStarted = null, Action<ContainerReopenResult>? done = null) =>
        ReopenLast(openWhenStarted, done, null);

    /// <summary>
    /// <see cref="ReopenLast(ContainerWindowInfo?, Action{ContainerReopenResult}?)"/> with options: with
    /// <see cref="ContainerReopenOptions.RemoteIfAway"/>, when the window cannot be reopened where the player stands
    /// (moved away, out of reach), the same chest opens remotely through the container preview instead
    /// (<see cref="ContainerReopenResult.RemoteOpened"/>, or <see cref="ContainerReopenResult.RemoteFailed"/> with the
    /// server's sentence in <see cref="ContainerReopenFinished.Reason"/>). <see cref="ContainerReopenOptions.Done"/> gets
    /// the outcome, its reason and the remote preview. Opt-in. <b>Experimental</b>: not live-verified.
    /// <para>When the remote fallback starts from the call itself (the player already moved away), <c>Done</c> can run
    /// before this returns <see cref="ContainerReopenResult.Started"/>: at once with <see cref="ContainerReopenResult.RemoteFailed"/>
    /// for a station's window, or with <see cref="ContainerReopenResult.RemoteOpened"/> / <see cref="ContainerReopenResult.RemoteFailed"/>
    /// when the preview settles on its first look. Treat <c>Done</c> as the outcome, never the return value.
    /// The plugin's unload closes a remote preview the fallback opened for it, also one kept open.</para>
    /// </summary>
    public ContainerReopenResult ReopenLast(ContainerWindowInfo? openWhenStarted, ContainerReopenOptions options) =>
        ReopenLast(openWhenStarted, null, options ?? throw new ArgumentNullException(nameof(options)));

    private ContainerReopenResult ReopenLast(ContainerWindowInfo? openWhenStarted, Action<ContainerReopenResult>? done, ContainerReopenOptions? options)
    {
        _ctx.EnsureAttached();
        if (!_ctx.IsClient) throw new InvalidOperationException("ctx.Inventory.ReopenLast runs on the client.");
        if (!Dev.MainThread.IsMainThread) throw new InvalidOperationException("ctx.Inventory.ReopenLast must run on the game main thread.");
        var owner = _ctx.Owner;
        if (!_returnTracked)
        {
            _returnTracked = true;
            _ctx.Track(new ActionDisposable(() => ContainerReturnClient.CancelFor(owner)));
        }
        if (options?.RemotePreview?.Problem() is { } problem) throw new ArgumentException(problem, nameof(options));
        return ContainerReturnClient.ReopenLast(done, "ReopenLast", owner, openWhenStarted: openWhenStarted, options: options);
    }
}
#endif
