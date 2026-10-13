using System;
using System.Collections.Generic;
using Progenitor.Support;

namespace Progenitor.Ui;

/// <summary>
/// The Logs saved dialog (requested 2026-10-10): after Collect logs saves a bundle, from the Mods tab's tile, Get help
/// or the title screen's tile, a framework <c>Choose</c> in the game's popup look (DIALOGS2) offers the community Discord:
/// <b>Not now</b> on the left (and Escape / B), then <b>Join the Discord</b> and, when its link is set,
/// <b>Open #technical-support</b>. A button opens its https link (<see cref="SupportActions.OpenOfferLink"/>); nothing
/// opens by itself. Controller navigation is the dialog kit's (NAV1): its buttons are one cell row the pad moves along.
/// The bridge answers it with <c>mods_ui dialog_choose</c> (a caption, or <c>no</c> for Not now). Game-free: the kit draws it.
/// </summary>
internal static class SupportOfferDialog
{
    /// <summary>The dialog's owner (its unload drops it with every other framework dialog).</summary>
    private static readonly object Owner = new();

    /// <summary>The dialog on screen from the last <see cref="Show"/>, for tests and the readback.</summary>
    internal static IDisposable? Current { get; private set; }

    internal static void Show(SupportActions.DiscordOffer offer)
    {
        if (offer == null || offer.Links.Count == 0) return;
        var links = new List<(string Caption, string Url)>(offer.Links);
        var captions = new List<string>(links.Count);
        foreach (var link in links) captions.Add(link.Caption);
        Current = UiDialogs.Choose(Owner, SettingsPanel.PluginId, Ui.FrameworkFooter, offer.Title, offer.Body, captions,
            pick =>
            {
                if (pick >= 0 && pick < links.Count) SupportActions.OpenOfferLink(links[pick].Url);
            },
            offer.CancelCaption, null);
    }
}
