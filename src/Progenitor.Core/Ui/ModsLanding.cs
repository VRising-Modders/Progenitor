using System;
using System.Collections.Generic;

namespace Progenitor.Ui;

/// <summary>
/// The Mods tab's landing page (user-approved mockup, 2026-10-10): the title, one subtitle line with the local and server
/// summaries, what Progenitor is (a centered block of points), a 2×2 grid of bordered tiles (Your settings, Server settings,
/// Preferences, Collect logs with the Last bundle line as its note; CellTiles), a third line with the Help guide tile while
/// Progenitor's guide is installed, and the footer. Every text and row key is here so the page and its tests read the same
/// words. Game-free; <see cref="ModsMenu.Open"/> draws it.
/// </summary>
internal static class ModsLanding
{
    internal const string Title = "Progenitor — Mods";
    internal const string Tagline = "Your mods, in one place.";

    internal const string Intro = "Progenitor is a mod manager and mod framework for V Rising.";

    /// <summary>What it does, in four short lines (checked against docs/api.md and the roadmap).</summary>
    internal static readonly IReadOnlyList<string> Points = new[]
    {
        "Installs, updates and turns mods on or off, on your game and on servers you run.",
        "Comes with its own client and server features: chat, photo mode, keybinds, server rules.",
        "Coordinates the mods built on it: they plug in, and Progenitor sorts out key and command conflicts and shares the game's resources between them.",
        "Gives mod authors a framework with hot reload and tools.",
    };

    /// <summary>
    /// The bullet in front of each point, in a column of its own: U+2022 (WGL4), drawn as a glyph cell so the runtime
    /// falls back to the middle dot when the font lacks it (live 2026-10-10: the middle dot read as a barely visible speck).
    /// </summary>
    internal const string Bullet = "\u2022";

    /// <summary>Space between blocks: the points and the first tiles, the last tiles and the footer (1080p units).</summary>
    internal const float BlockGap = 14f;

    /// <summary>The points block: this share of the pane wide, centered as a block, the lines left-aligned in it.</summary>
    internal const float PointsFraction = 0.6f;

    /// <summary>The points' size: the muted body tone, a little smaller than the intro line's 20.</summary>
    internal const float PointsFontSize = 17f;

    internal const float BulletWidth = 16f;

    /// <summary>Row key of point <paramref name="n"/> (1-based).</summary>
    internal static string PointRowKey(int n) => "landing-point-" + n;

    /// <summary>
    /// One point: <c>[spacer][·][text 60%][spacer]</c>; the two spacers share what is left, so the block is centered and
    /// its bullets line up. The text wraps under itself (a hanging indent); the row is fitted to the wrapped text.
    /// </summary>
    internal static CellRowSpec PointRow(int n, string text)
    {
        var cells = new List<CellSpec>
        {
            new CellSpec(CellKind.Spacer, "lead"),
            new CellSpec(CellKind.Muted, "bullet").With(text: Bullet, width: CellWidth.Px(BulletWidth), minWidth: BulletWidth,
                fontSize: PointsFontSize, vAlign: CellVAlign.Top, wrap: CellWrap.Glyph),
            new CellSpec(CellKind.Muted, "text").With(text: text, width: CellWidth.Fraction(PointsFraction), wrap: CellWrap.Wrap,
                fontSize: PointsFontSize, vAlign: CellVAlign.Top),
            new CellSpec(CellKind.Spacer, "trail"),
        };
        int lines = Math.Max(1, MenuPanelLayout.WrappedLines(text, 560f, PointsFontSize, 3));
        return new CellRowSpec(PointRowKey(n), cells, height: lines * 22f + 4f, plain: true, compact: true,
            gapAbove: n == 1 ? -1f : 2f, fitWrap: true);
    }

    internal const string Footer =
        "Choose a mod on the left for its status, settings and pages. Install and update mods on the Install mods tab.";

    // ---- the tiles: two rows of two ----------------------------------------------------------------------------------

    /// <summary>Readback keys: <c>mods_ui widget_read</c> lists the rows by these, and a tile is clicked by its caption.</summary>
    internal const string RowTop = "landing-tiles-top";
    internal const string RowBottom = "landing-tiles-bottom";
    internal const string RowTopNotes = "landing-tiles-top-notes";
    internal const string RowBottomNotes = "landing-tiles-bottom-notes";
    internal const string RowIntro = "landing-intro";
    internal const string RowSubtitle = "landing-subtitle";
    internal const string RowFooter = "landing-footer";
    internal const string RowLastBundle = "landing-last-bundle";

    internal const string CellYourSettings = "your-settings";
    internal const string CellYourSettingsNote = "your-settings-note";
    internal const string CellServerSettings = "server-settings";
    internal const string CellServerSettingsNote = "server-settings-note";
    internal const string CellPreferences = "preferences";
    internal const string CellPreferencesNote = "preferences-note";
    internal const string CellCollect = "collect-logs";
    internal const string CellCollectNote = "collect-logs-note";

    // The four tiles are the same native-styled kit button (the Collect logs key cap), not underlined links (user, 2026-10-10).
    internal const string YourSettingsCaption = "Your settings";
    internal const string YourSettingsNote = "Progenitor's options on this game";
    internal const string ServerSettingsCaption = "Server settings";
    internal const string ServerSettingsAdminNote = "Progenitor on this server (admins)";
    internal const string ServerSettingsJoinNote = "Join a server as its admin to change these";
    internal const string PreferencesCaption = "Preferences";
    internal const string PreferencesNote = "How the Mods menu, installs and updates behave";
    internal const string CollectCaption = Support.SupportActions.CollectCaption;

    /// <summary>The Collect logs tile's second line when nothing was collected just now.</summary>
    internal const string CollectDescription = "Collect the logs for a help post to assist with troubleshooting a mod.";

    /// <summary>How long the Collect logs tile shows a collect's outcome before its description returns.</summary>
    internal const long CollectResultMs = 10_000;

    /// <summary>
    /// The Collect logs tile's second line: while collecting the collect's progress, for <see cref="CollectResultMs"/> after
    /// it its outcome's first line, else <see cref="CollectDescription"/>. (The newest bundle stays on Progenitor's Get help
    /// page.)
    /// </summary>
    internal static string CollectNote(long nowMs, bool running, string? runningLine, string? result, long shownUntilMs)
    {
        if (running) return string.IsNullOrWhiteSpace(runningLine) ? Support.SupportActions.Collecting : runningLine!;
        if (nowMs < shownUntilMs && !string.IsNullOrWhiteSpace(result))
        {
            string r = result!.Trim();
            int nl = r.IndexOf('\n');
            return nl >= 0 ? r.Substring(0, nl).TrimEnd() : r;
        }
        return CollectDescription;
    }
    internal const string LastBundleLabel = Support.SupportActions.LastLabel;

    /// <summary>
    /// The subtitle: <c>Your mods, in one place. 7 mods found on this client (1 more reported, loading unconfirmed) · server
    /// mod list unavailable (known requirements shown)</c> — each summary condensed (<see cref="Condense"/>): its first
    /// sentence, then its qualifiers shortened in parentheses, never dropped (review 2026-10-10: "The list is partial."
    /// and "loading is unconfirmed" are what keep the count honest).
    /// </summary>
    internal static string Subtitle(string? localSummary, string? serverSummary)
    {
        var parts = new List<string>();
        string local = Condense(localSummary);
        if (local.Length > 0) parts.Add(local);
        string server = Condense(serverSummary);
        if (server.Length > 0) parts.Add(LowerFirst(server));
        return parts.Count == 0 ? Tagline : Tagline + " " + string.Join(" · ", parts);
    }

    /// <summary>
    /// A summary as one clause: the first sentence without its period, and every later sentence as a short qualifier in
    /// parentheses (<see cref="ShortQualifier"/>); a first sentence that already ends in a parenthesis takes them inside it.
    /// </summary>
    internal static string Condense(string? text)
    {
        var sentences = Sentences(text);
        if (sentences.Count == 0) return "";
        string head = sentences[0];
        var tail = new List<string>();
        for (int i = 1; i < sentences.Count; i++)
        {
            string q = ShortQualifier(sentences[i]);
            if (q.Length > 0) tail.Add(q);
        }
        if (tail.Count == 0) return head;
        string joined = string.Join("; ", tail);
        return head.EndsWith(")", StringComparison.Ordinal)
            ? head.Substring(0, head.Length - 1) + "; " + joined + ")"
            : head + " (" + joined + ")";
    }

    /// <summary>The known summary qualifiers, shortened; any other sentence lower-cased as it is.</summary>
    internal static string ShortQualifier(string sentence)
    {
        string s = sentence.Trim();
        const string unconfirmed = " more reported; loading is unconfirmed";
        if (s.EndsWith(unconfirmed, StringComparison.Ordinal)) return s.Substring(0, s.Length - unconfirmed.Length) + " more reported, loading unconfirmed";
        if (s == "The list is partial") return "list partial";
        if (s == "Server activity is not reported") return "activity not reported";
        if (s == "Known requirements are shown where available") return "known requirements shown";
        return LowerFirst(s);
    }

    private static List<string> Sentences(string? text)
    {
        var list = new List<string>();
        string t = (text ?? "").Trim();
        foreach (string part in t.Split(new[] { ". " }, StringSplitOptions.RemoveEmptyEntries))
        {
            string s = part.Trim().TrimEnd('.', ' ');
            if (s.Length > 0) list.Add(s);
        }
        return list;
    }

    private static string LowerFirst(string s)
    {
        if (s.Length < 2 || !char.IsUpper(s[0]) || char.IsUpper(s[1])) return s; // keep "MOD" and the like
        return char.ToLowerInvariant(s[0]) + s.Substring(1);
    }

    /// <summary>The Server settings tile's note: admins edit Progenitor's own server settings; anyone else is told how.</summary>
    internal static string ServerNote(bool adminHere) => adminHere ? ServerSettingsAdminNote : ServerSettingsJoinNote;

    /// <summary>
    /// The first line of tiles: Your settings and Server settings, each a bordered tile (<see cref="CellTiles"/>) with
    /// its link on the head row and its muted note on the note row.
    /// </summary>
    internal static (CellRowSpec Head, CellRowSpec Note) TopRow(bool adminHere) => CellTiles.Pair(RowTop, RowTopNotes,
        new TileSpec(CellYourSettings, YourSettingsCaption, CellYourSettingsNote, YourSettingsNote, Button: true),
        new TileSpec(CellServerSettings, ServerSettingsCaption, CellServerSettingsNote, ServerNote(adminHere), Button: true),
        firstTile: 1, firstLine: true, gapAbove: BlockGap);

    /// <summary>
    /// The second line: Preferences (a link) and Collect logs (a key-cap button, like the page's other buttons) whose
    /// note is the live Last bundle line (<paramref name="lastBundle"/>; the page re-reads it every second).
    /// </summary>
    internal static (CellRowSpec Head, CellRowSpec Note) BottomRow(string note = "") => CellTiles.Pair(RowBottom, RowBottomNotes,
        new TileSpec(CellPreferences, PreferencesCaption, CellPreferencesNote, PreferencesNote, Button: true),
        new TileSpec(CellCollect, CollectCaption, CellCollectNote, string.IsNullOrEmpty(note) ? CollectDescription : note, Button: true),
        firstTile: 3, firstLine: false);

    internal const string RowHelp = "landing-tiles-help";
    internal const string RowHelpNotes = "landing-tiles-help-notes";
    internal const string CellHelpGuide = "help-guide";
    internal const string CellHelpGuideNote = "help-guide-note";
    internal const string HelpGuideCaption = "Help guide";
    internal const string HelpGuideNote = "Guides for players, server admins and mod developers";

    /// <summary>The Help guide tile's target in the readback (it opens the guide in the browser).</summary>
    internal const string TargetHelpGuide = "help-guide";

    /// <summary>
    /// The third line, drawn only when Progenitor's guide is installed: Help guide in the left slot, the right slot empty,
    /// so the tile lines up with and is as wide as Preferences above it (a 2×3 grid with one empty slot).
    /// </summary>
    internal static (CellRowSpec Head, CellRowSpec Note) HelpRow() => CellTiles.Single(RowHelp, RowHelpNotes,
        new TileSpec(CellHelpGuide, HelpGuideCaption, CellHelpGuideNote, HelpGuideNote, Button: true),
        new TileSpec(CellPreferences, PreferencesCaption, CellPreferencesNote, PreferencesNote, Button: true),
        tileNumber: 5);

    /// <summary>
    /// The footer as a centered one-line row with <see cref="BlockGap"/> above it (a label row cannot ask for its gap;
    /// live the footer touched the last tiles).
    /// </summary>
    internal static CellRowSpec FooterRow() => new(RowFooter, new List<CellSpec>
        {
            new CellSpec(CellKind.Text, "footer").With(text: Footer, width: CellWidth.Fill(0f), align: CellAlign.Center, wrap: CellWrap.Wrap),
        }, height: 30f, plain: true, compact: true, gapAbove: BlockGap, fitWrap: true);

    /// <summary>The readback field naming a landing row (<c>landing: landing-tiles-top</c>).</summary>
    internal const string ReadbackKey = "landing";

    /// <summary>The Collect logs tile's target in the readback (it runs the collect; it opens nothing).</summary>
    internal const string TargetCollect = "collect-logs";

    /// <summary>One tile in the readback: its cell, its caption (what <c>widget_click label=</c> takes) and where it goes.</summary>
    internal static Dictionary<string, object?> TileReadback(string cell, string caption, string target) => new()
    {
        ["cell"] = cell,
        ["label"] = caption,
        ["target"] = target,
    };

    /// <summary>The Last bundle line: while collecting the collect's progress, then its outcome, else the newest bundle.</summary>
    internal static string LastBundleLine() => ProgenitorPage.LastBundleLine();
}
