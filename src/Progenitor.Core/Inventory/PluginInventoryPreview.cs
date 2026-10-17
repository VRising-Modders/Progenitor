#if !PROGENITOR_TESTS
using System;
using Progenitor.Plugins;

namespace Progenitor.Inventory;

public sealed partial class PluginInventory
{
    private PluginInventoryPreview? _preview;

    /// <summary>
    /// Client only: show the game's own container window for a container somewhere else, read-only
    /// (<see cref="PluginInventoryPreview.Open"/>). Unlike the rest of <c>ctx.Inventory</c> this works on the client.
    /// </summary>
    public PluginInventoryPreview Preview
    {
        get
        {
            _ctx.EnsureAttached();
            return _preview ??= new PluginInventoryPreview(_ctx);
        }
    }
}

/// <summary>
/// <c>ctx.Inventory.Preview</c> (client): the game's real container window for a chest or brazier anywhere in the world,
/// read-only, for the castle's owner and clan. The server decides who may preview what (Core-owned, owner or clan of a
/// plain container's castle; allies and strangers are refused) and streams the container to this client; the game's
/// own server check refuses every item event the window sends, so nothing can be moved, split, sorted, renamed or
/// toggled. One preview at a time per client. <b>Experimental</b>: source-checked and game-free tested, not
/// live-verified. See <c>docs/modules.md</c>, "Container preview".
/// </summary>
public sealed class PluginInventoryPreview
{
    private readonly PluginContext _ctx;
    private IContainerPreview? _last;

    internal PluginInventoryPreview(PluginContext ctx)
    {
        _ctx = ctx;
        ctx.Track(new ActionDisposable(() =>
        {
            if (_last is ContainerPreviewHandle h) h.Close(ContainerPreviewCloseReason.Unloaded, "", notify: false);
            _last = null;
        }));
    }

    /// <summary>
    /// Ask the server to preview <paramref name="container"/>, wait for it to reach this client, then open the game's
    /// container window for it. Returns at once; watch <see cref="IContainerPreview.State"/> or
    /// <see cref="IContainerPreview.Closed"/>. A running preview (this plugin's or another's) closes with
    /// <see cref="ContainerPreviewCloseReason.Replaced"/>. Main thread, client only.
    /// </summary>
    public IContainerPreview Open(ContainerId container, ContainerPreviewOptions? options = null)
    {
        _ctx.EnsureAttached();
        if (!_ctx.IsClient) throw new InvalidOperationException("ctx.Inventory.Preview runs on the client.");
        if (!Dev.MainThread.IsMainThread) throw new InvalidOperationException("ctx.Inventory.Preview.Open must run on the game main thread.");
        options ??= new ContainerPreviewOptions();
        var problem = options.Problem();
        if (problem != null) throw new ArgumentException(problem, nameof(options));
        var handle = ContainerPreviewClient.Open(container, options, _ctx.Owner);
        _last = handle;
        return handle;
    }

    /// <summary>The running preview (any plugin's), or null.</summary>
    public IContainerPreview? Current => ContainerPreviewClient.Current;
}
#endif
