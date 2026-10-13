#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;
using Progenitor.Networking;

namespace Progenitor.Ui;

/// <summary>Bounded Mods/Server control for server-owned item quarantine; actions never access a client filesystem.</summary>
internal static class ItemManagementPane
{
    private static readonly object Owner = new();
    private static int _generation;
    private static string Summary(ItemManagementOwnerStatus row) => row.OwnerId + ": " + row.Pending + " pending, "
        + row.Unavailable + " unavailable, " + row.Restored + " restored";

    internal static void Open()
    {
        int generation = ++_generation;
        ItemManagementChannel.Request("", "", (status, note) =>
        {
            if (generation != _generation) return;
            if (status == null) { Notice(note); return; }
            Show(status, note, 0);
        });
    }

    internal static void Close() { ++_generation; UiDialog.DropOwner(Owner); }

    private static void Show(ItemManagementStatus status, string note, int start)
    {
        var options = new List<string>();
        int count = Math.Min(8, status.Owners.Count - start);
        for (int i = 0; i < count; i++)
        {
            var row = status.Owners[start + i];
            options.Add(Summary(row) + (row.RestoreOffered ? " · Restore previous items?" : ""));
        }
        if (start + count < status.Owners.Count) options.Add("Next page");
        if (start > 0) options.Add("Previous page");
        if (options.Count == 0) { Notice("No saved items. " + note + " " + status.Note); return; }
        UiDialogs.Choose(Owner, "progenitor", Ui.FrameworkFooter, "Saved mod items",
            "Items removed with their mod stay saved on this server. Restore is best effort; unavailable players, containers or definitions stay pending. "
            + status.Note + " " + note, options, index =>
            {
                if (index < count) ShowOwner(status.Owners[start + index]);
                else if (start + count < status.Owners.Count && index == count) Show(status, note, start + count);
                else Show(status, note, Math.Max(0, start - 8));
            }, "Close", null);
    }

    private static void ShowOwner(ItemManagementOwnerStatus row)
    {
        var options = new List<string>();
        var decisions = new List<string>();
        if (row.Available && row.Pending > 0)
        {
            options.Add(row.RestoreOffered ? "Restore previous items" : "Retry restoring pending items");
            decisions.Add(row.RestoreOffered ? "restore" : "retry");
        }
        options.Add("Keep saved items for later"); decisions.Add("keep");
        options.Add("Discard saved items permanently"); decisions.Add("discard");
        UiDialogs.Choose(Owner, "progenitor", Ui.FrameworkFooter, row.RestoreOffered ? "Restore previous items?" : "Saved items",
            Summary(row) + (row.Reasons.Length > 0 ? "\n" + row.Reasons : "") + (row.Available ? "\nThe mod is installed. Choose whether to restore its previous items."
                : "\nThis mod's definitions are unavailable. Saved items can be kept until it returns."), options,
            index =>
            {
                string decision = decisions[index];
                if (decision == "discard")
                    UiDialogs.Confirm(Owner, "progenitor", Ui.FrameworkFooter, "Discard saved items?",
                        "Permanently discard the server's saved items for " + row.OwnerId + ". This cannot be undone.",
                        "Discard permanently", () => Decide(row.OwnerId, decision), "Cancel", Open, true);
                else Decide(row.OwnerId, decision);
            }, "Back", Open);
    }

    private static void Decide(string owner, string decision)
    {
        int generation = _generation;
        ItemManagementChannel.Request(owner, decision, (status, note) =>
        {
            if (generation != _generation) return;
            if (status == null) Notice(note);
            else Show(status, note, 0);
        });
    }

    private static void Notice(string note) => UiDialogs.Notice(Owner, "progenitor", Ui.FrameworkFooter,
        "Saved mod items", note, "Close", null);
}
#endif
