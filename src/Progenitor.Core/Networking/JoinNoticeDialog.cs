using System;
using System.Collections.Generic;
using Progenitor.Ui;

namespace Progenitor.Networking;

/// <summary>
/// The join flow's notices (the per-server mod pause's "Mods paused for this server", the mid-session "server update")
/// drawn with the dialog kit, so they look like the game's own popups (DIALOGS2: the popup frame with its ornament
/// divider, the title, the body, the vanilla popup buttons) rather than a flat panel with a full-width bar (requested
/// 2026-10-05). They keep their own lifecycle (auto-close, closing on leave) instead of entering
/// <see cref="UiDialog"/>'s queue, as the join screens do (<see cref="UiDialogView.Build"/>). Every text a server or a
/// mod supplies is escaped (<see cref="UiRichText.Literal"/>). Game-free.
/// </summary>
internal static class JoinNoticeDialog
{
    /// <summary>The notice's content: an OK-only notice, or (with <paramref name="cancel"/>) a pair whose main button acts.</summary>
    internal static DialogContent Content(string title, string body, IReadOnlyList<string>? lines, string? note,
        string main, string? cancel, string rowKey)
    {
        var escaped = new List<string>();
        foreach (string line in lines ?? Array.Empty<string>()) escaped.Add(UiRichText.Literal(line));
        return new DialogContent
        {
            Shape = string.IsNullOrEmpty(cancel) ? UiDialogShape.Notice : UiDialogShape.Confirm,
            Title = UiRichText.Literal(title),
            Body = UiRichText.Literal(body),
            Lines = escaped,
            Note = UiRichText.Literal(note ?? ""),
            MainCaption = main ?? "OK",
            MainTone = CellTone.Gold,
            CancelCaption = cancel ?? "",
            RowKey = rowKey,
            Footer = Ui.Ui.FrameworkFooter,
        };
    }

    /// <summary>Draw <paramref name="content"/> on <paramref name="panel"/> with the kit (chrome, title, body, buttons).</summary>
    internal static UiDialogView Build(IUiPanel panel, DialogContent content) => UiDialogView.Build(panel, content);
}
