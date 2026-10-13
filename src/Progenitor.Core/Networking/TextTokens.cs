using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Progenitor.Ui;

namespace Progenitor.Networking;

/// <summary>TOKENTIPS1: one placeholder a text setting understands, its Insert button's friendly name, and what it becomes.</summary>
internal sealed class TokenTip
{
    internal TokenTip(string token, string name, string meaning)
    {
        Token = token ?? "";
        Name = name ?? "";
        Meaning = meaning ?? "";
    }

    internal string Token { get; }

    /// <summary>TOKENINSERT1: the Insert button's name (<c>Countdown</c>, <c>Missing mods</c>).</summary>
    internal string Name { get; }

    internal string Meaning { get; }
}

/// <summary>TOKENINSERT1: which renderer fills a field's placeholders (and so its examples and its Players see preview).</summary>
internal enum TokenRenderer
{
    /// <summary>The server message (<see cref="ResetCountdown.Render(string?, ResetSchedule?, DateTime, TimeZoneInfo?, TimeZoneInfo?)"/>).</summary>
    ServerMessage = 0,

    /// <summary>The server list description (<see cref="ServerListDescription.Render"/>; the preview adds the signature).</summary>
    ServerList = 1,

    /// <summary>The chat line to a player without Progenitor (<see cref="NoAnswerNoticeText.Build"/>).</summary>
    NoProgenitor = 2,
}

/// <summary>TOKENTIPS1: a text setting whose value is rendered with placeholders or links, and its tip.</summary>
internal sealed class TokenField
{
    internal TokenField(string rowId, string label, IReadOnlyList<TokenTip> tokens, Func<IReadOnlyList<string>> rendererTokens, bool links,
        TokenRenderer renderer)
    {
        RowId = rowId ?? "";
        Label = label ?? "";
        Tokens = tokens ?? Array.Empty<TokenTip>();
        RendererTokens = rendererTokens;
        Links = links;
        Renderer = renderer;
    }

    /// <summary>The setting row (<c>progenitor/&lt;Section&gt;/&lt;Key&gt;</c>).</summary>
    internal string RowId { get; }

    /// <summary>The row's label, as drawn.</summary>
    internal string Label { get; }

    /// <summary>The Insert buttons, in order.</summary>
    internal IReadOnlyList<TokenTip> Tokens { get; }

    /// <summary>The tokens the field's renderer replaces (its own list, so a test can hold the tip to it).</summary>
    internal Func<IReadOnlyList<string>> RendererTokens { get; }

    /// <summary>True when players see the field's web links as clickable (<see cref="UiLinks"/>).</summary>
    internal bool Links { get; }

    internal TokenRenderer Renderer { get; }
}

/// <summary>
/// TOKENTIPS1 / TOKENINSERT1 (requested 2026-10-06): the one table of every Progenitor text setting rendered with
/// placeholders or links. Under each such field the settings page draws an <b>Insert</b> row (a button per token, named
/// by what it is and showing what it reads on this server now: <c>Countdown · 7d</c>), then a <b>Players see</b> line
/// (the text with the tokens filled, the links as players see them), then Apply. The renderers, examples and the preview
/// all go through the fields' own renderers with this side's current settings (<see cref="Context"/>);
/// <c>TextTokensTests</c> holds each field's tokens equal to its renderer's list. Game-free.
/// </summary>
internal static class TextTokens
{
    /// <summary>The Insert row's label.</summary>
    internal const string InsertLabel = "Insert";

    /// <summary>The preview row's label.</summary>
    internal const string PreviewLabel = "Players see";

    /// <summary>TOKENINSERT1: the preview's height in lines (past them it ends with an ellipsis).</summary>
    internal const int PreviewLines = 4;

    internal const string MessageRowId = "progenitor/ServerMessage/Message";
    internal const string NoProgenitorRowId = "progenitor/ServerMessage/NoProgenitorMessage";
    internal const string ServerListRowId = "progenitor/ServerList/Description";

    /// <summary>TOKENINSERT1: the one short link line (the help adds the confirmation; the full rules are in the docs).</summary>
    internal const string LinksLine = "Web links (https://…) become clickable.";

    /// <summary>The details pane's link sentence: what a click does.</summary>
    internal static readonly string LinksDetail =
        "Web links (https://…, www.…) become clickable: a click asks \"" + JoinLinkText.Title + "\" with the exact address, then "
        + "Copy link, Cancel or Open in browser.";

    /// <summary>The full link rules (docs/join-flow.md; tests hold them to <see cref="UiLinks"/>).</summary>
    internal static readonly string LinksRules =
        "A link is http:// or https:// followed by non-space characters, a www. address, or a bare host.tld/path "
        + "(a dot, a 2 to 24 letter TLD, then /). Trailing punctuation and an unbalanced ) or ] are left out. Only http and "
        + "https open; a link longer than " + UiLinks.MaxUrlLength.ToString("N0", CultureInfo.InvariantCulture)
        + " characters stays plain text.";

    internal static readonly IReadOnlyList<TokenField> Fields = new[]
    {
        new TokenField(MessageRowId, "Message", new[]
        {
            new TokenTip(ResetCountdown.TokenCountdown, "Countdown", "countdown to the next reset (3d, 17h, 42m 05s)"),
            new TokenTip(ResetCountdown.TokenDate, "Reset date", "the reset's date and time, in the player's time zone"),
            new TokenTip(ResetCountdown.TokenRelative, "Reset day", "in 3 days, in 5 hours or today"),
        }, () => ResetCountdown.Tokens, links: true, TokenRenderer.ServerMessage),
        new TokenField(ServerListRowId, "Server list description", new[]
        {
            new TokenTip(ResetCountdown.TokenRelative, "Reset day", "in 3 days, in 5 hours or today"),
            new TokenTip(ResetCountdown.TokenDate, "Reset date", "the reset's date and time, in the server's time zone"),
            new TokenTip(ResetCountdown.TokenCountdown, "Countdown", "the same as Reset day (the list does not tick)"),
        }, () => ServerListDescription.Tokens, links: false, TokenRenderer.ServerList),
        new TokenField(NoProgenitorRowId, "Message without Progenitor", new[]
        {
            new TokenTip(NoAnswerNoticeText.TokenMods, "Missing mods", "the mods they're missing"),
            new TokenTip(NoAnswerNoticeText.TokenSeconds, "Seconds left", "seconds until they're disconnected"),
        }, () => NoAnswerNoticeText.Tokens, links: false, TokenRenderer.NoProgenitor),
    };

    /// <summary>The field <paramref name="rowId"/>, or null when its value takes no placeholders.</summary>
    internal static TokenField? For(string? rowId)
    {
        foreach (var f in Fields)
            if (string.Equals(f.RowId, rowId, StringComparison.Ordinal)) return f;
        return null;
    }

    // ---- this side's state, for the renderers ------------------------------------------------------------------

    /// <summary>What the renderers read: the reset settings, the clock and the two zones (set by the settings module; tests set their own).</summary>
    internal sealed class RenderContext
    {
        public Func<ResetSchedule?> Reset { get; set; } = () => null;
        public Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;
        public Func<TimeZoneInfo> ServerZone { get; set; } = () => TimeZoneInfo.Local;
        public Func<TimeZoneInfo> PlayerZone { get; set; } = () => TimeZoneInfo.Local;

        /// <summary>The server list's signature is added under the description (its switch).</summary>
        public Func<bool> ServerListSignature { get; set; } = () => true;
    }

    internal static RenderContext Context { get; set; } = new();

    /// <summary>The seconds a without-Progenitor example and preview show for <c>{seconds}</c>.</summary>
    internal const int SampleSeconds = 20;

    /// <summary><paramref name="text"/> with the field's tokens filled in, as players get it now.</summary>
    internal static string Render(TokenField field, string? text)
    {
        var c = Context;
        string t = text ?? "";
        try
        {
            switch (field.Renderer)
            {
                case TokenRenderer.ServerMessage:
                    return ResetCountdown.Render(t, c.Reset(), c.UtcNow(), c.ServerZone(), c.PlayerZone());
                case TokenRenderer.ServerList:
                    return ServerListDescription.Render(t, c.Reset(), c.UtcNow(), c.ServerZone());
                default:
                    return NoAnswerNoticeText.Build(t, null, SampleSeconds);
            }
        }
        catch { return t; }
    }

    /// <summary>
    /// What the token reads on this server now, short enough for a button: <c>7d</c>, <c>Wed, Oct 14, 11:00 AM</c> (the
    /// offset left out), <c>in 7 days</c>, <c>Progenitor</c>, <c>20</c>.
    /// </summary>
    internal static string Example(TokenField field, TokenTip tip)
    {
        string v = Render(field, tip.Token);
        int paren = v.IndexOf(" (UTC", StringComparison.Ordinal);
        if (paren < 0) paren = v.IndexOf(" (", StringComparison.Ordinal);
        if (paren > 0 && v.EndsWith(")", StringComparison.Ordinal)) v = v.Substring(0, paren);
        return v.Trim();
    }

    /// <summary>The Insert button's caption: <c>Countdown · 7d</c>.</summary>
    internal static string Caption(TokenField field, TokenTip tip)
    {
        string example = Example(field, tip);
        return example.Length == 0 ? tip.Name : tip.Name + " · " + example;
    }

    /// <summary>
    /// The Players see line: the rendered text (the server list's with its signature line), line breaks kept. Empty text
    /// reads <c>(empty)</c>; a Message without Progenitor that is empty is its default message.
    /// </summary>
    internal static string Preview(TokenField field, string? text)
    {
        string rendered = Render(field, text);
        if (field.Renderer == TokenRenderer.ServerList)
            rendered = ServerListDescription.Compose(rendered, SafeSignature()).Text;
        return rendered.Trim().Length == 0 ? "(empty)" : rendered;
    }

    /// <summary>LISTING1: the counter line's label under a field that has one.</summary>
    internal const string CounterLabel = "Room left";

    /// <summary>
    /// LISTING1: the muted counter line drawn right under the field (before Insert), or null when the field has none:
    /// the server list description's room, counted on the text as it will read (<see cref="ServerListDescription.CounterLine"/>).
    /// </summary>
    internal static string? Counter(TokenField field, string? text) =>
        field.Renderer == TokenRenderer.ServerList
            ? ServerListDescription.CounterLine(ServerListDescription.Render(text ?? "", SafeReset(), SafeNow(), SafeZone()), SafeSignature())
            : null;

    internal static string CounterRowKey(string rowId) => rowId + "/counter";

    private static ResetSchedule? SafeReset()
    {
        try { return Context.Reset(); }
        catch { return null; }
    }

    private static DateTime SafeNow()
    {
        try { return Context.UtcNow(); }
        catch { return DateTime.UtcNow; }
    }

    private static TimeZoneInfo SafeZone()
    {
        try { return Context.ServerZone(); }
        catch { return TimeZoneInfo.Utc; }
    }

    private static bool SafeSignature()
    {
        try { return Context.ServerListSignature(); }
        catch { return true; }
    }

    // ---- the Insert row's layout -----------------------------------------------------------------------------

    /// <summary>The Insert buttons' caption size and padding (a small steel button).</summary>
    internal const float InsertFontSize = 14f;

    internal const float InsertButtonPad = 24f;

    internal const float InsertRowHeight = 34f;

    /// <summary>An Insert button's width for <paramref name="caption"/>, generous for a changing example.</summary>
    internal static float InsertButtonWidth(string caption) =>
        Math.Max(90f, CellLayout.EstimateTextWidth(caption, InsertFontSize) + InsertButtonPad);

    /// <summary>
    /// The Insert buttons in rows that fit <paramref name="available"/> (the value column's width): the indices of the
    /// buttons on each row, in order. A button wider than the column has a row of its own.
    /// </summary>
    internal static List<List<int>> PackInsertRows(IReadOnlyList<string> captions, float available)
    {
        var rows = new List<List<int>>();
        var current = new List<int>();
        float used = 0f;
        for (int i = 0; i < (captions?.Count ?? 0); i++)
        {
            float w = InsertButtonWidth(captions![i]);
            float need = current.Count == 0 ? w : used + CellLayout.Gap + w;
            if (current.Count > 0 && need > available)
            {
                rows.Add(current);
                current = new List<int>();
                need = w;
            }

            current.Add(i);
            used = need;
        }

        if (current.Count > 0) rows.Add(current);
        return rows;
    }

    /// <summary>The Insert button cell key of token <paramref name="index"/>.</summary>
    internal static string InsertCellKey(int index) => "insert-" + index.ToString(CultureInfo.InvariantCulture);

    /// <summary>The Insert rows' keys: <c>&lt;rowId&gt;/insert</c>, <c>…/insert-2</c>.</summary>
    internal static string InsertRowKey(string rowId, int row) =>
        rowId + "/insert" + (row == 0 ? "" : "-" + (row + 1).ToString(CultureInfo.InvariantCulture));

    internal static string PreviewRowKey(string rowId) => rowId + "/preview";

    // ---- the help ----------------------------------------------------------------------------------------------

    /// <summary>The tokens by friendly name: <c>Countdown, Reset date, Reset day</c>.</summary>
    internal static string TokenNames(TokenField field)
    {
        var names = new List<string>(field.Tokens.Count);
        foreach (var t in field.Tokens) names.Add(t.Name);
        return string.Join(", ", names);
    }

    /// <summary>
    /// The tip under the expanded editor: each token with what it is (<c>{reset} — countdown …</c>), then the link line.
    /// </summary>
    internal static string Line(TokenField field)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < field.Tokens.Count; i++)
        {
            if (i > 0) sb.Append('\n');
            sb.Append(field.Tokens[i].Token).Append(" — ").Append(field.Tokens[i].Meaning);
        }

        if (field.Links) sb.Append('\n').Append(LinksLine);
        return sb.ToString();
    }

    /// <summary>The editor tip for <paramref name="rowId"/>, or empty.</summary>
    internal static string Line(string rowId) => For(rowId) is { } f ? Line(f) : "";

    /// <summary>
    /// The field's help with a short summary appended (<c>Insert puts Countdown, Reset date or Reset day at the caret.</c>
    /// and, where links work, <see cref="LinksDetail"/>), so the details pane says it while the field is hovered or focused.
    /// </summary>
    internal static string Help(string rowId, string help)
    {
        var f = For(rowId);
        if (f == null) return help ?? "";
        string h = (help ?? "").TrimEnd();
        if (h.Length > 0 && !h.EndsWith(".", StringComparison.Ordinal)) h += ".";
        h += (h.Length > 0 ? " " : "") + InsertLabel + " puts " + OrList(f) + " at the caret; Players see shows the result.";
        if (f.Links) h += " " + LinksDetail;
        return h;
    }

    private static string OrList(TokenField f)
    {
        var names = new List<string>(f.Tokens.Count);
        foreach (var t in f.Tokens) names.Add(t.Name);
        if (names.Count <= 1) return string.Join("", names);
        return string.Join(", ", names.GetRange(0, names.Count - 1)) + " or " + names[names.Count - 1];
    }

    /// <summary>TOKENTIPS2: most characters <see cref="Help"/> adds to a field's own help (it stays a 3 to 4 line summary).</summary>
    internal const int MaxHelpAddition = 260;
}
