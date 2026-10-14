using System;
using System.Collections.Generic;

namespace Progenitor.Ui;

public sealed partial class PluginHud
{
    /// <summary>
    /// Show a first-time tip card once: the game's popup look with <paramref name="title"/>, <paramref name="body"/>
    /// (one or two sentences), <b>Got it</b> and <b>Show all tips</b>. The tip is registered (it appears in the plugin's
    /// list of tips) and shown unless the player has dismissed it before; "seen" is the player's own, kept in Core's
    /// client <c>progenitor.ui.cfg</c> (<c>[Ui] SeenTips</c>) and keyed by this plugin and <paramref name="id"/>.
    ///
    /// <para><b>Never over a modal, one at a time.</b> While a framework dialog (<see cref="Dialogs"/>), a key capture or
    /// an open dropdown list is on screen the card waits, and a card already up steps back until it closes. Cards from
    /// every plugin share one queue, first asked first shown. With <see cref="UiTipOptions.Anchor"/> the card sits
    /// beside that panel (not over it) and is dropped unshown if the panel closes first; without one it goes to the
    /// bottom-right of the screen. The card is not modal and takes input only while hovered.</para>
    ///
    /// <para><b>Got it</b> marks it seen and closes it; <b>Show all tips</b> does the same and opens the plugin's list of
    /// tips (or <see cref="UiTipOptions.OnShowAll"/>); Escape closes it like any panel and counts as Got it. Disposing the
    /// returned handle, or unloading the plugin, takes the card away without marking it seen. Asking again for a tip that
    /// is waiting or showing returns that request. Client only: on the server it returns a dropped tip.</para>
    ///
    /// <para><b>Status: experimental</b> (built and unit-tested; not yet verified live).</para>
    /// </summary>
    public IUiTip Tip(string id, string title, string body, UiTipOptions? options = null)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("A tip id is required.", nameof(id));
        if (ServerSide("Tip"))
            return new UiTipRequest(_ctx.Owner, _ctx.Guid, id, title ?? "", body ?? "", null, 0) { State = UiTipState.Dropped, Reason = "server" };
        return UiTips.Request(_ctx.Owner, _ctx.Guid, id, title ?? "", body ?? "", options);
    }

    /// <summary>Register (or reword) a tip without showing it, so <see cref="ShowAllTips"/> lists it before it was ever shown.</summary>
    public void DefineTip(string id, string title, string body)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("A tip id is required.", nameof(id));
        if (ServerSide("DefineTip")) return;
        UiTips.Define(_ctx.Owner, _ctx.Guid, id, title ?? "", body ?? "");
    }

    /// <summary>This plugin's registered tips, in registration order, each with whether the player has seen it.</summary>
    public IReadOnlyList<UiTipInfo> Tips => _ctx.IsServer ? Array.Empty<UiTipInfo>() : UiTips.Infos(_ctx.Guid);

    /// <summary>Whether the player has dismissed tip <paramref name="id"/>.</summary>
    public bool IsTipSeen(string id) => !_ctx.IsServer && UiTips.IsSeen(_ctx.Guid, id);

    /// <summary>Open the list of this plugin's tips (the card's <b>Show all tips</b>): a game-styled window, not modal.</summary>
    public void ShowAllTips()
    {
        if (ServerSide("ShowAllTips")) return;
        UiTips.ShowList(_ctx.Owner, _ctx.Guid);
    }

    /// <summary>Every tip of this plugin shows once again (for a "show tips again" setting).</summary>
    public void ResetTips()
    {
        if (ServerSide("ResetTips")) return;
        UiTips.ResetAll(_ctx.Guid);
    }

    /// <summary>Tip <paramref name="id"/> shows once again.</summary>
    public void ResetTip(string id)
    {
        if (ServerSide("ResetTip")) return;
        UiTips.Reset(_ctx.Guid, id);
    }

    static partial void DropTips(object owner)
    {
        try { UiTips.DropOwner(owner); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: ctx.Hud tips of an unloading plugin were not dropped: " + e.Message); }
    }
}
