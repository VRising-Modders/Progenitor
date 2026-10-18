#if !PROGENITOR_TESTS
using System;
using Progenitor.Plugins;
using Unity.Entities;

namespace Progenitor.Inventory;

public sealed partial class PluginInventory
{
    /// <summary>
    /// Server: decide every container window a player asks for, before it opens: a walk-up interaction with a placed
    /// container, station or castle heart, and the remote preview (<see cref="ContainerOpenRequest.Kind"/>).
    /// <paramref name="decide"/> answers <see cref="ContainerOpenDecision.Allow"/>, <see cref="ContainerOpenDecision.Deny"/>
    /// (the player reads the sentence) or <see cref="ContainerOpenDecision.Challenge"/> (the player's client shows a masked
    /// prompt; the typed text reaches <paramref name="answer"/> with the challenge's token). Every plugin's handler is
    /// asked; a deny wins, then every challenge is asked in turn. Enforced on the server: a refused interaction is ended
    /// before the window or the container's contents reach the client, whatever the client sends.
    ///
    /// <para>Keep <paramref name="decide"/> quick and free of entity writes: it runs inside the game's frame, once per
    /// open. A handler that throws refuses the open. A <see cref="ContainerOpenDecision.Challenge"/> without
    /// <paramref name="answer"/> refuses too. Tracked: dispose the handle, or unload, to remove it. Main thread, server only.
    /// <b>Experimental</b>: source-checked and game-free tested, not live-verified (docs/modules.md, "Container open gate").</para>
    /// </summary>
    public IDisposable OnOpenRequest(Func<ContainerOpenRequest, ContainerOpenDecision> decide,
        Func<ContainerOpenAnswer, ContainerAnswerVerdict>? answer = null)
    {
        if (decide == null) throw new ArgumentNullException(nameof(decide));
        RequireGate(nameof(OnOpenRequest));
        var registration = ContainerOpenGateServer.Register(_ctx.Owner, _ctx.Guid, _ctx.Name, decide, answer);
        _ctx.Track(registration);
        return registration;
    }

    /// <summary>
    /// Server: forget this plugin's session grants and pending passes for <paramref name="container"/> (its password
    /// changed, say), so the next open is asked again. Returns how many were dropped.
    /// </summary>
    public int RevokeOpenGrants(ContainerId container)
    {
        RequireGate(nameof(RevokeOpenGrants));
        return ContainerOpenGateServer.RevokeContainer(_ctx.Guid, container);
    }

    /// <summary>Server: forget this plugin's session grants and passes for one player (<paramref name="platformId"/>).</summary>
    public int RevokeOpenGrants(ulong platformId)
    {
        RequireGate(nameof(RevokeOpenGrants));
        return ContainerOpenGateServer.RevokePlayer(_ctx.Guid, platformId);
    }

    private void RequireGate(string op)
    {
        _ctx.EnsureAttached();
        if (!Plugin.IsServerProcess || !ContainerOpenGateServer.Installed)
            throw new InvalidOperationException("ctx.Inventory." + op + " runs on the server side only; the open gate is server-enforced.");
        if (!Dev.MainThread.IsMainThread)
            throw new InvalidOperationException("ctx.Inventory." + op + " must run on the game main thread (Plugin.Frame, a command, an event, or MainThread.Post).");
    }
}
#endif
