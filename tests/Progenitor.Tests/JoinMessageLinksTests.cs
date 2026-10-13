using System;
using System.Collections.Generic;
using System.Linq;
using Progenitor.Networking;
using Progenitor.Ui;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// JOINLINKS1: links in the server message — what is a link, how it is normalized and trimmed, what is
/// refused (bidi, control characters, length), that nothing else in the message becomes markup, the
/// confirmation's model, and the mods page's section bands staying readable.
/// </summary>
public sealed class JoinMessageLinksTests
{
    private static List<(string Text, string Url)> Links(string text) =>
        UiLinks.Find(text).Select(l => (l.Text, l.Url)).ToList();

    [Theory]
    [InlineData("Join https://discord.gg/abc now", "https://discord.gg/abc", "https://discord.gg/abc")]
    [InlineData("Rules: http://example.com/rules?x=1&y=2#top", "http://example.com/rules?x=1&y=2#top", "http://example.com/rules?x=1&y=2#top")]
    [InlineData("HTTPS://Example.com/Path", "HTTPS://Example.com/Path", "https://Example.com/Path")]
    [InlineData("See www.nightgarden.net for the map", "www.nightgarden.net", "https://www.nightgarden.net")]
    [InlineData("Discord: discord.gg/nightgarden", "discord.gg/nightgarden", "https://discord.gg/nightgarden")]
    [InlineData("wiki.example.org/page/Main_Page", "wiki.example.org/page/Main_Page", "https://wiki.example.org/page/Main_Page")]
    [InlineData("host.example.com:8080/status", "host.example.com:8080/status", "https://host.example.com:8080/status")]
    [InlineData("(see https://a.com/b).", "https://a.com/b", "https://a.com/b")]
    [InlineData("Visit https://x.com/a, then rest.", "https://x.com/a", "https://x.com/a")]
    [InlineData("Go to https://x.com/a!?", "https://x.com/a", "https://x.com/a")]
    [InlineData("[https://x.com/y]", "https://x.com/y", "https://x.com/y")]
    [InlineData("https://en.wikipedia.org/wiki/Foo_(bar)", "https://en.wikipedia.org/wiki/Foo_(bar)", "https://en.wikipedia.org/wiki/Foo_(bar)")]
    [InlineData("see:(https://x.com/q)", "https://x.com/q", "https://x.com/q")]
    [InlineData("<https://x.com/a>", "https://x.com/a", "https://x.com/a")]
    [InlineData("Link:https://x.com/a", "https://x.com/a", "https://x.com/a")]
    public void A_link_is_found_trimmed_and_normalized(string text, string written, string url)
    {
        var links = Links(text);
        Assert.Single(links);
        Assert.Equal(written, links[0].Text);
        Assert.Equal(url, links[0].Url);
        Assert.True(UiLinks.IsOpenable(links[0].Url));
    }

    [Theory]
    [InlineData("Version 1.2.3 is out")]
    [InlineData("v1.2.3")]
    [InlineData("Wipe at 12:30, 18:45:00")]
    [InlineData("Install Progenitor.dll and Mod.Name.dll")]
    [InlineData("example.com")]
    [InlineData("e.g. this, i.e. that")]
    [InlineData("3.14/2 and 10.0.0.1/24")]
    [InlineData("ftp://files.example.com/a")]
    [InlineData("javascript:alert(1)")]
    [InlineData("mailto:admin@example.com")]
    [InlineData("bob@site.com/x")]
    [InlineData("https:// nothing")]
    [InlineData("file:///C:/Windows/system32")]
    [InlineData("a.b/c")]
    [InlineData("site.c0m/path")]
    [InlineData("Price is 5.99/month")]
    public void Not_a_link(string text) => Assert.Empty(UiLinks.Find(text));

    [Fact]
    public void Several_links_are_indexed_in_order()
    {
        var links = UiLinks.Find("A https://a.com/1 then www.b.org and c.net/x.");
        Assert.Equal(new[] { 0, 1, 2 }, links.Select(l => l.Index));
        Assert.Equal(new[] { "https://a.com/1", "https://www.b.org", "https://c.net/x" }, links.Select(l => l.Url));
        Assert.Equal("c.net/x", links[2].Text);
    }

    [Fact]
    public void A_link_with_bidi_or_control_characters_or_too_long_is_not_a_link()
    {
        foreach (char c in new[] { '\u200E', '\u200F', '\u202A', '\u202B', '\u202C', '\u202D', '\u202E', '\u2066', '\u2067', '\u2068', '\u2069' })
            Assert.Empty(UiLinks.Find("https://x.com/" + c + "gpj.exe"));
        Assert.Empty(UiLinks.Find("https://x.com/a\u0007b"));
        Assert.Empty(UiLinks.Find("discord.gg/a\u202Eb"));

        string path = new string('a', UiLinks.MaxUrlLength - "https://x.com/".Length);
        Assert.Single(UiLinks.Find("https://x.com/" + path));
        Assert.Empty(UiLinks.Find("https://x.com/" + path + "b"));

        Assert.False(UiLinks.IsOpenable("javascript:alert(1)"));
        Assert.False(UiLinks.IsOpenable("file:///C:/x"));
        Assert.False(UiLinks.IsOpenable("https://"));
        Assert.False(UiLinks.IsOpenable("https://x.com/\u202Eexe"));
        Assert.False(UiLinks.IsOpenable("https://x.com/a b"));
        Assert.True(UiLinks.IsOpenable("http://x.com"));
    }

    [Fact]
    public void Normalize_adds_https_to_a_scheme_less_link_only()
    {
        Assert.Equal("https://www.example.com", UiLinks.Normalize("www.example.com"));
        Assert.Equal("https://discord.gg/x", UiLinks.Normalize("discord.gg/x"));
        Assert.Equal("http://example.com/a", UiLinks.Normalize("http://example.com/a"));
        Assert.Equal("https://example.com/a", UiLinks.Normalize("https://example.com/a."));
        Assert.Null(UiLinks.Normalize("1.2.3"));
        Assert.Null(UiLinks.Normalize("not a link"));
    }

    [Fact]
    public void Markup_escapes_everything_but_the_kits_own_link_tags()
    {
        string hostile = "<b>bold</b> <color=red>red</color> <sprite=1> <link=\"9\">fake</link> <size=200>BIG "
            + "</noparse><size=99> https://x.com/a<script> www.y.org <u>u</u>";
        string markup = UiLinks.Markup(hostile);
        string rest = markup
            .Replace("<noparse><</noparse>", "")
            .Replace("<link=\"0\">", "").Replace("<link=\"1\">", "").Replace("</link>", "")
            .Replace("<color=" + UiLinks.ColorHex + ">", "").Replace("</color>", "")
            .Replace("<u>", "").Replace("</u>", "");
        // Only the kit's own tags (two links) and noparse runs carry a '<': nothing the admin typed is live.
        Assert.DoesNotContain("<", rest);
        Assert.Equal(2, CountOf(markup, "<link=\""));
        Assert.Contains("<link=\"0\"><color=#9fc7e8><u>https://x.com/a</u></color></link>", markup, StringComparison.Ordinal);
        Assert.Contains("<link=\"1\"><color=#9fc7e8><u>www.y.org</u></color></link>", markup, StringComparison.Ordinal);
        // The admin's own tags are still drawn literally (each '<' in its own noparse run).
        Assert.Contains("<noparse><</noparse>link=\"9\">fake", markup, StringComparison.Ordinal);
        Assert.Contains("<noparse><</noparse>sprite=1>", markup, StringComparison.Ordinal);
        Assert.Contains("<noparse><</noparse>color=red>", markup, StringComparison.Ordinal);
        // Text without links is exactly today's escaping.
        Assert.Equal(UiRichText.Literal("<b>no links</b> 1.2.3"), UiLinks.Markup("<b>no links</b> 1.2.3"));
        Assert.Equal("", UiLinks.Markup(""));
    }

    [Fact]
    public void A_linked_cell_finds_its_links_on_every_write_and_a_scroll_block_counts_across_lines()
    {
        var cell = new CellSpec(CellKind.Text, "text").With(text: "a https://x.com/1", wrap: CellWrap.Wrap);
        Assert.Empty(cell.LinksNow());
        cell.Linkify = true;
        Assert.True(UiCellRowHandle.IsLinked(cell));
        Assert.Single(cell.LinksNow());
        cell.Text = "no links now";
        Assert.Empty(cell.LinksNow());

        var block = new CellSpec(CellKind.ScrollText, "text")
        {
            Linkify = true,
            Lines = new[]
            {
                new CellLine(CellLineKind.Body, "first https://a.com/1"),
                new CellLine(CellLineKind.Body, "plain"),
                new CellLine(CellLineKind.Body, "www.b.org and c.net/2"),
            },
        };
        var links = block.LinksNow();
        Assert.Equal(new[] { 0, 1, 2 }, links.Select(l => l.Index));
        Assert.Equal("https://c.net/2", links[2].Url);
        // A two-line cell never links (its note is clipped).
        var two = new CellSpec(CellKind.Text, "name").With(text: "x\nhttps://a.com/1", wrap: CellWrap.TwoLine);
        two.Linkify = true;
        Assert.False(UiCellRowHandle.IsLinked(two));
    }

    [Fact]
    public void The_dialog_shows_the_exact_url_copies_it_and_opens_only_http_or_https()
    {
        JoinLinkOpener.ResetForTests();
        try
        {
            var link = UiLinks.Find("Our Discord: discord.gg/nightgarden.")[0];
            var dialog = new JoinLinkDialog(link, "Night Garden");
            Assert.Equal("https://discord.gg/nightgarden", dialog.Url);
            Assert.Equal("discord.gg/nightgarden", dialog.Text);
            Assert.Equal("Night Garden's admins wrote this link. It opens in your web browser.", dialog.Body);
            Assert.Equal("This server's admins wrote this link. It opens in your web browser.", JoinLinkText.Body(""));
            Assert.Equal("Open this link?", JoinLinkText.Title);
            Assert.Equal(new[] { "Cancel", "Copy link", "Open in browser" }, JoinLinkDialog.Buttons().Select(b => b.Caption));

            string? copied = null;
            Assert.Equal("copied", dialog.Copy(s => { copied = s; return true; }));
            Assert.Equal("https://discord.gg/nightgarden", copied);
            Assert.Equal("Copied.", dialog.Status);
            Assert.Equal("copy failed", dialog.Copy(_ => false));
            Assert.Equal(JoinLinkText.CopyFailed, dialog.Status);

            // A dry run records the URL and launches nothing.
            JoinLinkOpener.DryRun = true;
            string? opened = null;
            Assert.Equal("dry-run: https://discord.gg/nightgarden", dialog.Open(u => opened = u));
            Assert.Null(opened);
            Assert.Equal("https://discord.gg/nightgarden", JoinLinkOpener.LastUrl);

            JoinLinkOpener.DryRun = false;
            Assert.Equal("opened", dialog.Open(u => opened = u));
            Assert.Equal("https://discord.gg/nightgarden", opened);
            Assert.Equal(2, JoinLinkOpener.Count);

            // Anything that is not http(s) never reaches the opener.
            var bad = new JoinLinkDialog(new UiTextLink(0, 0, 4, "evil", "javascript:alert(1)"), "x");
            opened = null;
            Assert.StartsWith("refused", bad.Open(u => opened = u), StringComparison.Ordinal);
            Assert.Null(opened);
        }
        finally { JoinLinkOpener.ResetForTests(); }
    }

    [Fact]
    public void The_admin_message_help_names_links()
    {
        Assert.Equal("Links (https://…) become clickable for players.", JoinLinkText.AdminHint);
    }

    [Fact]
    public void The_message_box_and_reader_are_linked_cells()
    {
        var box = JoinFlowLayout.MessageBox("Hi https://a.com/x", 600f);
        Assert.True(box.Cells.Single(c => c.Key == JoinFlowLayout.CellText).Linkify);
        var reader = JoinFlowLayout.MessageReader("Hi\nhttps://a.com/x", 600f);
        var block = reader.Cells.Single(c => c.Key == JoinFlowLayout.CellText);
        Assert.True(block.Linkify);
        Assert.Single(block.LinksNow());
        // The full URL row is as tall as the URL needs, whatever its length.
        string url = "https://example.com/" + new string('a', 1900);
        var row = JoinFlowLayout.LinkUrl(url, 600f);
        Assert.Equal(url, row.Cells.Single(c => c.Key == JoinFlowLayout.CellText).Text);
        Assert.True(row.Height > JoinFlowLayout.LinkUrl("https://a.com/x", 600f).Height * 10);
    }

    // ---- section bands ----------------------------------------------------------------------------

    [Fact]
    public void Section_bands_are_red_and_green_and_every_word_on_them_reads_at_4_5_to_1()
    {
        Assert.Equal(PaletteColor.Hex(0x2a0f12), MenuPalette.SectionRedFill);
        Assert.Equal(PaletteColor.Hex(0x6b2a30), MenuPalette.SectionRedRim);
        Assert.Equal(PaletteColor.Hex(0x0f2416), MenuPalette.SectionGreenFill);
        Assert.Equal(PaletteColor.Hex(0x2f6a40), MenuPalette.SectionGreenRim);
        Assert.Equal(PaletteColor.Hex(0x9fc7e8), MenuPalette.LinkText);
        var red = MenuPalette.SectionRedFill;
        var green = MenuPalette.SectionGreenFill;
        Assert.True(red.R > red.G + 0.05f && red.R > red.B + 0.05f, red.ToString());
        Assert.True(green.G > green.R + 0.05f && green.G > green.B + 0.03f, green.ToString());

        // Every word color a mods-page row draws: normal, muted, amber, green ready (OkBright), pale blue
        // (Info), orange (refused / failed), and the gold of a New / Changed tag.
        var words = new[]
        {
            MenuPalette.TextColor(CellTone.Normal), MenuPalette.TextColor(CellTone.Muted), MenuPalette.TextColor(CellTone.Amber),
            MenuPalette.TextColor(CellTone.OkBright), MenuPalette.TextColor(CellTone.Info), MenuPalette.TextColor(CellTone.Orange),
            MenuPalette.TextColor(CellTone.Gold),
        };
        foreach (var band in new[] { red, green })
            foreach (var word in words)
                Assert.True(Wcag(word, band) >= 4.5f, word + " on " + band + ": " + Wcag(word, band));
        Assert.True(Wcag(MenuPalette.TextColor(CellTone.SectionRedHead), red) >= 4.5f);
        Assert.True(Wcag(MenuPalette.TextColor(CellTone.SectionRedNote), red) >= 4.5f);
        Assert.True(Wcag(MenuPalette.TextColor(CellTone.SectionGreenHead), green) >= 4.5f);
        Assert.True(Wcag(MenuPalette.TextColor(CellTone.SectionGreenNote), green) >= 4.5f);
        Assert.Equal(PaletteColor.Hex(0xffd8d8), MenuPalette.TextColor(CellTone.SectionRedHead));
        Assert.Equal(PaletteColor.Hex(0xf09595), MenuPalette.TextColor(CellTone.SectionRedNote));
        Assert.Equal(PaletteColor.Hex(0xd4f0d8), MenuPalette.TextColor(CellTone.SectionGreenHead));
        Assert.Equal(PaletteColor.Hex(0x97c459), MenuPalette.TextColor(CellTone.SectionGreenNote));
    }

    [Fact]
    public void A_section_row_keeps_its_cells_behind_a_pad_and_names_its_band()
    {
        var head = JoinFlowLayout.SectionHeading(JoinFlowLayout.RequiredHeadRow, "Required to join", "1 of 2 ready", CellSection.Red);
        Assert.Equal(CellSection.Red, head.Section);
        Assert.Equal(CellSectionEdge.First, head.SectionEdge);
        Assert.Equal("REQUIRED TO JOIN", head.Cells.Single(c => c.Key == JoinFlowLayout.CellText).Text);
        Assert.Equal(CellTone.SectionRedNote, head.Cells.Single(c => c.Key == JoinFlowLayout.CellCount).Tone);

        var line = JoinFlowLayout.Line("x", "This server requires no mods.", CellTone.Muted, 600f);
        var inside = JoinFlowLayout.InSection(line, CellSection.Green, last: true);
        Assert.Equal(CellSectionEdge.Last, inside.SectionEdge);
        Assert.Equal(CellRowSpec.SectionPad, inside.RightInset);
        Assert.Equal(line.Height, inside.Height);
        Assert.Equal(CellKind.Spacer, inside.Cells[0].Kind);
        Assert.Same(line.Cells[0], inside.Cells[1]);
        Assert.Equal("green", UiCellRowHandle.SectionName(CellSection.Green));
        Assert.Equal("single", UiCellRowHandle.SectionEdgeName(CellSectionEdge.First | CellSectionEdge.Last));
        // The pad and inset fit inside the row: the text cell keeps a readable width.
        var placed = MenuPanelLayout.CellColumns(600f, inside);
        Assert.True(placed.Single(p => p.Key == JoinFlowLayout.CellText).Width >= 600f - 2f * CellRowSpec.SectionPad - 20f);
    }

    private static int CountOf(string s, string part)
    {
        int n = 0, at = 0;
        while ((at = s.IndexOf(part, at, StringComparison.Ordinal)) >= 0)
        {
            n++;
            at += part.Length;
        }

        return n;
    }

    private static float Wcag(PaletteColor a, PaletteColor b)
    {
        static double Lin(float c) => c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        static double L(PaletteColor c) => 0.2126 * Lin(c.R) + 0.7152 * Lin(c.G) + 0.0722 * Lin(c.B);
        double la = L(a), lb = L(b);
        return (float)((Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05));
    }
}
