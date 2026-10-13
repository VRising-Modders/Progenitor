using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Progenitor.Networking;
using Progenitor.Ui;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// TOKENTIPS1 / TOKENINSERT1: every text setting rendered with placeholders or links is in one table
/// (<see cref="TextTokens"/>); each gets Insert buttons (friendly name and this server's example), a Players see line
/// and a short help, and each field's tokens are exactly its renderer's.
/// </summary>
public sealed class TextTokensTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 7, 15, 0, 0, DateTimeKind.Utc);
    private static readonly TimeZoneInfo Eastern = TimeZoneInfo.CreateCustomTimeZone("Test -4", TimeSpan.FromHours(-4), "Test -4", "Test -4");
    private static readonly ResetSchedule Reset = new("2026-10-14T15:00:00Z", ResetRepeat.None, 1, ResetPrecision.Minute, "");

    public TextTokensTests()
    {
        TextTokens.Context = new TextTokens.RenderContext
        {
            Reset = () => Reset,
            UtcNow = () => Now,
            ServerZone = () => Eastern,
            PlayerZone = () => Eastern,
            ServerListSignature = () => true,
        };
    }

    public void Dispose() => TextTokens.Context = new TextTokens.RenderContext();

    [Fact]
    public void Each_field_lists_exactly_its_renderers_tokens_with_a_friendly_name()
    {
        foreach (var field in TextTokens.Fields)
        {
            var tip = field.Tokens.Select(t => t.Token).OrderBy(t => t, StringComparer.Ordinal).ToArray();
            var renderer = field.RendererTokens().OrderBy(t => t, StringComparer.Ordinal).ToArray();
            Assert.Equal(renderer, tip);
            Assert.All(field.Tokens, t => Assert.False(string.IsNullOrWhiteSpace(t.Name), t.Token));
            Assert.All(field.Tokens, t => Assert.DoesNotContain("{", t.Name, StringComparison.Ordinal));
        }

        var message = TextTokens.For(TextTokens.MessageRowId)!;
        Assert.Equal(new[] { ("Countdown", "{reset}"), ("Reset date", "{reset:date}"), ("Reset day", "{reset:relative}") },
            message.Tokens.Select(t => (t.Name, t.Token)).ToArray());
        var noProg = TextTokens.For(TextTokens.NoProgenitorRowId)!;
        Assert.Equal(new[] { ("Missing mods", "{mods}"), ("Seconds left", "{seconds}") }, noProg.Tokens.Select(t => (t.Name, t.Token)).ToArray());
    }

    [Fact]
    public void The_renderer_lists_match_what_the_renderers_replace()
    {
        foreach (string token in ResetCountdown.Tokens)
            Assert.DoesNotContain(token, ResetCountdown.Render(token, Reset, Now, TimeZoneInfo.Utc, TimeZoneInfo.Utc), StringComparison.Ordinal);
        foreach (string token in ServerListDescription.Tokens)
            Assert.DoesNotContain(token, ServerListDescription.Render(token, Reset, Now, TimeZoneInfo.Utc), StringComparison.Ordinal);
        foreach (string token in NoAnswerNoticeText.Tokens)
            Assert.DoesNotContain(token, NoAnswerNoticeText.Build(token, new[] { "a.mod" }, 20), StringComparison.Ordinal);
        Assert.Equal("{nope}", ResetCountdown.Render("{nope}", Reset, Now, TimeZoneInfo.Utc, TimeZoneInfo.Utc));
    }

    /// <summary>The buttons show what each token reads on this server now, through the field's own renderer.</summary>
    [Fact]
    public void Insert_buttons_show_this_servers_examples()
    {
        var message = TextTokens.For(TextTokens.MessageRowId)!;
        Assert.Equal("Countdown · 7d", TextTokens.Caption(message, message.Tokens[0]));
        Assert.Equal("Reset date · Wed, Oct 14, 11:00 AM", TextTokens.Caption(message, message.Tokens[1]));
        Assert.Equal("Reset day · in 7 days", TextTokens.Caption(message, message.Tokens[2]));

        var noProg = TextTokens.For(TextTokens.NoProgenitorRowId)!;
        Assert.Equal("Missing mods · Progenitor", TextTokens.Caption(noProg, noProg.Tokens[0]));
        Assert.Equal("Seconds left · 20", TextTokens.Caption(noProg, noProg.Tokens[1]));

        // No reset set: the tokens' own "not scheduled".
        TextTokens.Context.Reset = () => null;
        Assert.Equal("Countdown · not scheduled", TextTokens.Caption(message, message.Tokens[0]));
    }

    [Fact]
    public void Players_see_renders_the_text_as_players_get_it()
    {
        var message = TextTokens.For(TextTokens.MessageRowId)!;
        Assert.Equal("Wipe in 7d, see https://example.com", TextTokens.Preview(message, "Wipe in {reset}, see https://example.com"));
        Assert.Equal("(empty)", TextTokens.Preview(message, "  "));
        var noProg = TextTokens.For(TextTokens.NoProgenitorRowId)!;
        Assert.Contains("disconnected in 20 seconds", TextTokens.Preview(noProg, ""), StringComparison.Ordinal); // the default
        Assert.Equal("Need Progenitor, 20 s.", TextTokens.Preview(noProg, "Need {mods}, {seconds} s."));
        var list = TextTokens.For(TextTokens.ServerListRowId)!;
        Assert.EndsWith(ServerListDescription.Signature, TextTokens.Preview(list, "Wipe {reset:relative}"), StringComparison.Ordinal);
        TextTokens.Context.ServerListSignature = () => false;
        Assert.Equal("Wipe in 7 days", TextTokens.Preview(list, "Wipe {reset:relative}"));

        // The row: fixed four lines, wrapping, links drawn as players see them only where they are clickable.
        var row = ConfigurationPane.PreviewRowSpec("p", "x", links: true);
        var cell = row.Find(ConfigurationPane.CellStatusValue)!;
        Assert.True(cell.Linkify);
        Assert.Equal(CellWrap.Wrap, cell.Wrap);
        Assert.Equal(TextTokens.PreviewLines * ConfigurationPane.StatusWrapLineHeight + 12f, row.Height);
        Assert.False(ConfigurationPane.PreviewRowSpec("p", "x", links: false).Find(ConfigurationPane.CellStatusValue)!.Linkify);
    }

    /// <summary>A token goes in at the caret, whole or not at all; a field never focused gets it at the end.</summary>
    [Fact]
    public void A_token_is_inserted_at_the_caret_whole_or_not_at_all()
    {
        Assert.Equal("Hello {reset}", UiFieldInsert.At("Hello ", null, "{reset}").Text);
        var mid = new UiTextEditState("Reset  soon", 6, 6);
        var after = UiFieldInsert.At("Reset  soon", mid, "{reset}");
        Assert.Equal("Reset {reset} soon", after.Text);
        Assert.Equal(13, after.Caret);
        // A selection is replaced.
        Assert.Equal("Reset {reset:date}", UiFieldInsert.At("Reset now", new UiTextEditState("Reset now", 9, 6), "{reset:date}").Text);
        // A stale caret (the text changed since) falls back to the end.
        Assert.Equal("abc{mods}", UiFieldInsert.At("abc", new UiTextEditState("ab", 1, 1), "{mods}").Text);
        // Too little room: nothing, never "{res".
        Assert.Equal("abcd", UiFieldInsert.At("abcd", null, "{reset}", maxLength: 8).Text);
        Assert.Equal("abcd{mods}", UiFieldInsert.At("abcd", null, "{mods}", maxLength: 10).Text);
    }

    [Fact]
    public void Insert_buttons_wrap_to_the_value_column()
    {
        var captions = new[] { "Countdown · 7d", "Reset date · Wed, Oct 14, 11:00 AM", "Reset day · in 7 days" };
        var wide = TextTokens.PackInsertRows(captions, 1000f);
        Assert.Single(wide);
        var narrow = TextTokens.PackInsertRows(captions, 300f);
        Assert.True(narrow.Count >= 2);
        Assert.Equal(new[] { 0, 1, 2 }, narrow.SelectMany(r => r).ToArray());
        foreach (var r in narrow.Where(r => r.Count > 1))
            Assert.True(r.Sum(i => TextTokens.InsertButtonWidth(captions[i])) + (r.Count - 1) * CellLayout.Gap <= 300f);

        var spec = ConfigurationPane.InsertRowSpec("k", first: true, new[] { 0, 2 }, captions);
        Assert.Equal(TextTokens.InsertLabel, spec.Find(ConfigurationPane.CellStatusLabel)!.Text);
        Assert.Equal(CellSkin.KeyCap, spec.Find(TextTokens.InsertCellKey(0))!.Skin);
        Assert.Equal(captions[2], spec.Find(TextTokens.InsertCellKey(2))!.Text);
        Assert.Null(spec.Find(TextTokens.InsertCellKey(1)));
        Assert.Equal("", ConfigurationPane.InsertRowSpec("k2", first: false, new[] { 1 }, captions).Find(ConfigurationPane.CellStatusLabel)!.Text);
    }

    [Fact]
    public void Links_get_one_short_line_and_the_help_the_confirmation_detail()
    {
        Assert.Equal("Web links (https://…) become clickable.", TextTokens.LinksLine);
        Assert.DoesNotContain("[text](url)", TextTokens.LinksLine + TextTokens.LinksDetail + TextTokens.LinksRules, StringComparison.Ordinal);
        string own = "The first thing a joining player sees";
        string help = TextTokens.Help(TextTokens.MessageRowId, own);
        Assert.Contains("Insert puts Countdown, Reset date or Reset day at the caret", help, StringComparison.Ordinal);
        Assert.Contains(TextTokens.LinksDetail, help, StringComparison.Ordinal);
        Assert.Contains(JoinLinkText.Title, TextTokens.LinksDetail, StringComparison.Ordinal);
        Assert.DoesNotContain(TextTokens.LinksDetail, TextTokens.Help(TextTokens.NoProgenitorRowId, own), StringComparison.Ordinal);
        foreach (var f in TextTokens.Fields)
            Assert.True(TextTokens.Help(f.RowId, own).Length - own.Length <= TextTokens.MaxHelpAddition, f.RowId);
        Assert.Equal("Plain.", TextTokens.Help("progenitor/Other/Key", "Plain."));

        // What the rules say is what UiLinks does.
        Assert.Equal("https://example.com/a", UiLinks.Normalize("https://example.com/a"));
        Assert.NotNull(UiLinks.Normalize("www.example.com"));
        Assert.Null(UiLinks.Normalize("https://example.com/" + new string('a', UiLinks.MaxUrlLength + 10)));
        Assert.Contains("2,048", TextTokens.LinksRules, StringComparison.Ordinal);
    }

    /// <summary>Row order: field, Insert, Players see, Apply; the old Placeholders and separate preview rows are gone.</summary>
    [Fact]
    public void The_rows_come_in_order_and_the_old_tip_rows_are_gone()
    {
        string view = Source("Ui", "ModSettingsView.cs");
        int field = view.IndexOf("input = multiline ? panel.TextArea(caption, displayed, options.Lines) : panel.Input(caption, displayed);", StringComparison.Ordinal);
        int insert = view.IndexOf("if (multiline) AddTokenRows(panel, binding, row, input, add);", StringComparison.Ordinal);
        int apply = view.IndexOf("apply = panel.Button(\"Apply\");", insert, StringComparison.Ordinal);
        Assert.True(field > 0 && insert > field && apply > insert);
        int insertRows = view.IndexOf("var cellRow = panel.CellRow(spec);", StringComparison.Ordinal);
        int previewRow = view.IndexOf("var preview = panel.CellRow(ConfigurationPane.PreviewRowSpec(", StringComparison.Ordinal);
        Assert.True(insertRows > 0 && previewRow > insertRows);
        Assert.Contains("cellRow.OnClick(Progenitor.Networking.TextTokens.InsertCellKey(i), () => InsertToken(binding, row, input, tip.Token));", view);
        Assert.Contains("string value = input.InsertAtCaret(token);", view);
        Assert.Contains("add(cellRow, RowSettingInsert,", view);
        Assert.Contains("add(preview, RowSettingPreview,", view);

        string join = Source("Networking", "JoinServerSettings.cs");
        string list = Source("Networking", "ServerListDescriptionSettings.cs");
        Assert.DoesNotContain("AddPlaceholdersRow", join + list);
        Assert.DoesNotContain("NoProgenitorMessageRow.PreviewLabel", join);
        Assert.DoesNotContain("page.Info(\"Listed as\"", list);
        // Preview stays: it opens the whole message page with its clickable links.
        Assert.Contains("page.Button(\"Preview\", JoinFlowPanel.OpenPreview", join);
        Assert.Contains("help: TextTokens.Help(TextTokens.MessageRowId,", join);
        Assert.Contains("help: TextTokens.Help(TextTokens.ServerListRowId,", list);

        // The runtime inserts through the field's own edit path and keeps the keyboard there.
        string runtime = Source("Ui", "UnityUiRuntime.cs");
        Assert.Contains("var next = UiFieldInsert.Token(s, token, FieldMaxLength(field));", runtime);
        Assert.Contains("FocusOwnedField(field);\n            return true;", runtime.Replace("\r\n", "\n"));
    }

    /// <summary>No other text row names a {token} without being in the table.</summary>
    [Fact]
    public void No_other_text_row_claims_tokens()
    {
        var known = TextTokens.Fields.SelectMany(f => f.Tokens.Select(t => t.Token)).ToHashSet(StringComparer.Ordinal);
        foreach (string file in Directory.EnumerateFiles(Path.Combine(Root(), "src"), "*.cs", SearchOption.AllDirectories))
        {
            string src = File.ReadAllText(file);
            foreach (Match m in Regex.Matches(src, @"page\.Text\((?<body>[^;]*);", RegexOptions.Singleline))
            {
                string body = m.Groups["body"].Value;
                foreach (Match t in Regex.Matches(body, @"\{[a-z]+(:[a-z]+)?\}"))
                    Assert.True(known.Contains(t.Value) && body.Contains("TextTokens.Help(", StringComparison.Ordinal)
                                || body.Contains("NoProgenitorMessageRow.Help", StringComparison.Ordinal),
                        Path.GetFileName(file) + ": a text row names " + t.Value + " without a TextTokens entry");
            }
        }
    }

    [Fact]
    public void The_description_column_is_measured_to_its_whole_text()
    {
        Assert.Contains("float h = Math.Max(1f, MeasureHelp(go, width, PanelLayout.MaxFixedRowHeight));", Source("Ui", "UnityUiRuntime.SettingsLook.cs"));
    }

    private static string Source(string dir, string file) => File.ReadAllText(Path.Combine(Root(), "src", "Progenitor.Core", dir, file));

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Progenitor.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
    }
}
