using System;
using Progenitor.Plugins;

namespace Progenitor.Ui;

/// <summary>
/// <c>ctx.Hud</c> (P7, docs/ui.md "HUD: notifications, locate and pick in world"): the client-side
/// pieces that act on the game's HUD and world rather than on a mod panel — a notification in the
/// game's own announcement style, a Locate ping on a world entity, and a pick-in-world mode that
/// captures the player's next interact on a container or station.
///
/// <para><b>Lifetime.</b> Everything a call starts belongs to this plugin and is released on every
/// unload reason: a notification still queued is dropped, a ping disappears, an armed pick is
/// cancelled without running its callback. Client only: on the server each call logs once and
/// returns a handle that does nothing.</para>
/// </summary>
public sealed partial class PluginHud
{
    private readonly PluginContext _ctx;
    private bool _serverLogged;

    internal PluginHud(PluginContext ctx)
    {
        _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
        object owner = ctx.Owner;
        _ctx.Track(new ActionDisposable(() => DropOwner(owner)));
    }

    /// <summary>Every per-owner backend's teardown; each partial adds its own line.</summary>
    private static void DropOwner(object owner)
    {
        DropNotices(owner);
        DropPings(owner);
        DropPicks(owner);
        DropTips(owner);
    }

    static partial void DropNotices(object owner);

    static partial void DropPings(object owner);

    static partial void DropPicks(object owner);

    static partial void DropTips(object owner);

    private bool _retiredLogged;

    /// <summary>
    /// True when <paramref name="member"/> must do nothing: on the server, and (PINS1) once this plugin's context is
    /// unloading. The owner sweep has run by then, so a notice, ping, pick or tip made now would put the plugin back
    /// into a Core table nothing clears and pin its load context.
    /// </summary>
    private bool ServerSide(string member)
    {
        if (_ctx.IsRetired)
        {
            if (!_retiredLogged)
            {
                _retiredLogged = true;
                Plugin.Logger?.LogInfo("Ui: ctx.Hud." + member + " was called while " + _ctx.Name + " unloads; ignored.");
            }
            return true;
        }
        if (!_ctx.IsServer) return false;
        if (!_serverLogged)
        {
            _serverLogged = true;
            _ctx.Log.LogInfo("Ui: ctx.Hud." + member + " is client only; on the server it does nothing.");
        }

        return true;
    }
}
