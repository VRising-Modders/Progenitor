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
    /// for about a second Progenitor hovers the object and presses Interact through a framework input lease, as the
    /// player would. The server sees an ordinary interaction, with its range and permission checks and every server-side
    /// open handler (the container open gate) in front of it. Does nothing (and says why) when there is no remembered
    /// window, the object left this client, the character moved more than a meter from where it stood, another game
    /// window stays open, or another input lease holds the hover or buttons. A window still open is left alone
    /// (<see cref="ContainerReopenResult.AlreadyOpen"/>), so it is safe to call after any flow that may or may not have
    /// closed it. Returns <see cref="ContainerReopenResult.Started"/> or the reason it did not start;
    /// <paramref name="done"/> runs once on the main thread with the outcome. A newer reopen replaces a running one.
    /// Main thread. <b>Experimental</b>: source-checked and game-free tested, not live-verified.
    /// </summary>
    public ContainerReopenResult ReopenLast(Action<ContainerReopenResult>? done = null)
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
        return ContainerReturnClient.ReopenLast(done, "ReopenLast", owner);
    }
}
#endif
