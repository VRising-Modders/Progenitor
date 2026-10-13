using System;
using System.Collections.Generic;
using Progenitor.Ui;

namespace Progenitor.Networking;

/// <summary>JOINLINKS1: the words of the server message's link confirmation and the admin's hint.</summary>
internal static class JoinLinkText
{
    public const string Title = "Open this link?";
    public const string CopyButton = "Copy link";
    public const string CancelButton = "Cancel";
    public const string OpenButton = "Open in browser";
    public const string Copied = "Copied.";
    public const string CopyFailed = "Could not copy the link.";

    /// <summary>The admin's message editor hint (one sentence after the existing help).</summary>
    public const string AdminHint = "Links (https://…) become clickable for players.";

    /// <summary><c>Night Garden's admins wrote this link. It opens in your web browser.</c></summary>
    public static string Body(string? serverName)
    {
        string name = (serverName ?? "").Trim();
        return (name.Length > 0 ? name + "'s" : "This server's") + " admins wrote this link. It opens in your web browser.";
    }
}

/// <summary>
/// JOINLINKS1: the confirmation a link in the server message opens, game-free. It holds the one normalized
/// http(s) URL that will open — the dialog draws that exact URL, never the link's own words — and does what
/// its three buttons do: Copy link (status <see cref="JoinLinkText.Copied"/>; the dialog stays), Cancel, and
/// Open in browser, which re-checks the URL (<see cref="UiLinks.IsOpenable"/>) and hands it to the opener.
/// </summary>
internal sealed class JoinLinkDialog
{
    internal const string ButtonCopy = "link-copy";
    internal const string ButtonCancel = "link-cancel";
    internal const string ButtonOpen = "link-open";
    internal const string UrlRow = "join-link-url";
    internal const string StatusRow = "join-link-status";
    internal const string ButtonsRow = "join-link-buttons";

    internal JoinLinkDialog(UiTextLink link, string? serverName)
    {
        Index = link.Index;
        Text = link.Text;
        Url = link.Url;
        ServerName = serverName ?? "";
    }

    /// <summary>The link's position in the message (0 for the first).</summary>
    internal int Index { get; }

    /// <summary>The link as the admins wrote it.</summary>
    internal string Text { get; }

    /// <summary>The normalized URL shown in full and opened.</summary>
    internal string Url { get; }

    internal string ServerName { get; }

    /// <summary>The line under the URL (<see cref="JoinLinkText.Copied"/>), or empty.</summary>
    internal string Status { get; private set; } = "";

    internal string Body => JoinLinkText.Body(ServerName);

    /// <summary>The dialog's buttons: Cancel, Copy link, Open in browser (the main action).</summary>
    internal static List<DialogButton> Buttons() => new()
    {
        new DialogButton(ButtonCancel, JoinLinkText.CancelButton, DialogButtonRole.Cancel),
        new DialogButton(ButtonCopy, JoinLinkText.CopyButton, DialogButtonRole.Option, CellTone.Normal),
        new DialogButton(ButtonOpen, JoinLinkText.OpenButton, DialogButtonRole.Main, CellTone.Gold),
    };

    /// <summary>Copy link: <paramref name="copy"/> gets the normalized URL; the status says whether it worked.</summary>
    internal string Copy(Func<string, bool> copy)
    {
        bool ok;
        try { ok = copy(Url); }
        catch { ok = false; }
        Status = ok ? JoinLinkText.Copied : JoinLinkText.CopyFailed;
        return ok ? "copied" : "copy failed";
    }

    /// <summary>
    /// Open in browser: only an http(s) URL ever reaches <paramref name="open"/>. With <see cref="JoinLinkOpener.DryRun"/>
    /// the URL is recorded and nothing launches. Returns what happened.
    /// </summary>
    internal string Open(Action<string> open)
    {
        if (!UiLinks.IsOpenable(Url)) return "refused: not an http(s) link";
        JoinLinkOpener.Record(Url);
        if (JoinLinkOpener.DryRun) return "dry-run: " + Url;
        try { open(Url); }
        catch (Exception e) { return "open failed: " + e.Message; }
        return "opened";
    }

    internal Dictionary<string, object?> Describe() => new()
    {
        ["open"] = true,
        ["index"] = Index,
        ["text"] = Text,
        ["url"] = Url,
        ["title"] = JoinLinkText.Title,
        ["body"] = Body,
        ["status"] = Status,
        ["buttons"] = new List<object?> { JoinLinkText.CancelButton, JoinLinkText.CopyButton, JoinLinkText.OpenButton },
    };
}

/// <summary>
/// JOINLINKS1: the test switch and record of Open in browser. <see cref="DryRun"/> (off by default; the dev
/// bridge's <c>join_ui link_dry_run</c> sets it) records the URL instead of launching a browser.
/// </summary>
internal static class JoinLinkOpener
{
    internal static bool DryRun { get; set; }

    /// <summary>The last URL Open in browser accepted (opened, or recorded in a dry run), or empty.</summary>
    internal static string LastUrl { get; private set; } = "";

    /// <summary>How many times Open in browser accepted a URL this session.</summary>
    internal static int Count { get; private set; }

    internal static void Record(string url)
    {
        LastUrl = url ?? "";
        Count++;
    }

    internal static Dictionary<string, object?> Describe() => new()
    {
        ["dryRun"] = DryRun,
        ["lastUrl"] = LastUrl,
        ["count"] = Count,
    };

    internal static void ResetForTests()
    {
        DryRun = false;
        LastUrl = "";
        Count = 0;
    }
}
