using System;
using System.Collections.Generic;
using System.Linq;
using Progenitor.Install;
using Progenitor.Ui;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// ABOUTMD1 (live 2026.10.136, a fresh template mod): the Mods page's About showed its README's blockquote as
/// "&gt; Requires Progenitor. …" and its settings table as pipe text ("Setting | Default | Who decides | What it does").
/// The README renderer (<see cref="MarkdownPlain"/>) and the About rows (<see cref="ModAboutLayout"/>) now draw every
/// common README construct cleanly or drop it. Game-free.
/// </summary>
public sealed class AboutMarkdownTests
{
    /// <summary>The shape of the template's README: a title, a blockquote, paragraphs, two tables, a list.</summary>
    private const string TemplateReadme =
        "# My Mod\n\n"
        + "> **Requires Progenitor.** This mod is built on [Progenitor](https://thunderstore.io/c/v-rising/p/VRising_Modders/Progenitor/), "
        + "a modding framework for V Rising. Install Progenitor first; its Install Mods tab can then install this mod.\n\n"
        + "Asks the server for its greeting and shows it in a window and a notice.\n\n"
        + "## Settings\n\n"
        + "| Setting | Default | Who decides | What it does |\n"
        + "|---|---|---|---|\n"
        + "| Display / Show notices | On | Each player | Show this mod's notices at the top of the screen. |\n"
        + "| Server / Greeting | Welcome to the server! | The server (admins edit it in game) | What the server answers when a player asks. |\n\n"
        + "## Keys\n\n"
        + "| Action | Default key | Remap in |\n"
        + "|---|---|---|\n"
        + "| Open or close the window | Ctrl+F8 | Mods → Keybinds |\n\n"
        + "## Notes\n\n"
        + "- Works on the client\n"
        + "  and the server.\n"
        + "  - Nested point\n"
        + "1. First step\n"
        + "2) Second step\n"
        + "- [x] Done item\n";

    [Fact]
    public void A_readme_with_a_quote_tables_lists_and_headings_has_no_markdown_left()
    {
        var blocks = MarkdownPlain.RenderBlocks(TemplateReadme, int.MaxValue);
        foreach (var b in blocks)
        {
            Assert.DoesNotContain("|", b.Text, StringComparison.Ordinal);
            Assert.DoesNotContain("---", b.Text, StringComparison.Ordinal);
            Assert.DoesNotContain("**", b.Text, StringComparison.Ordinal);
            Assert.DoesNotContain("](", b.Text, StringComparison.Ordinal);
            Assert.False(b.Text.StartsWith(">", StringComparison.Ordinal), b.Text);
            Assert.False(b.Text.StartsWith("#", StringComparison.Ordinal), b.Text);
        }

        Assert.Equal(MarkdownBlockKind.Heading, blocks[0].Kind);
        Assert.Equal(1, blocks[0].Level);
        var quote = blocks.Single(b => b.Kind == MarkdownBlockKind.Quote);
        Assert.StartsWith("Requires Progenitor. This mod is built on Progenitor, a modding framework for V Rising.", quote.Text, StringComparison.Ordinal);
        Assert.Equal(2, blocks.Count(b => b.Kind == MarkdownBlockKind.Heading && b.Level == 2 && b.Text is "Settings" or "Keys"));

        var rows = blocks.Where(b => b.Kind == MarkdownBlockKind.Table).ToList();
        Assert.Equal(3, rows.Count);
        Assert.Equal("Display / Show notices — Default: On · Who decides: Each player · What it does: Show this mod's notices at the top of the screen.",
            rows[0].Text);
        Assert.Equal(new[] { "Setting", "Default", "Who decides", "What it does" }, rows[0].Header);
        Assert.Same(rows[0].Header, rows[1].Header);
        Assert.Equal(new[] { "Server / Greeting", "Welcome to the server!", "The server (admins edit it in game)", "What the server answers when a player asks." }, rows[1].Cells);
        Assert.NotSame(rows[0].Header, rows[2].Header);
        Assert.Equal("Open or close the window — Default key: Ctrl+F8 · Remap in: Mods → Keybinds", rows[2].Text);
        // The header row is the labels, not a row of its own.
        Assert.DoesNotContain(blocks, b => b.Kind == MarkdownBlockKind.Table && b.Text.StartsWith("Setting", StringComparison.Ordinal));

        var items = blocks.Where(b => b.Kind == MarkdownBlockKind.ListItem).ToList();
        Assert.Equal("• Works on the client and the server.", items[0].Text);
        Assert.Equal(0, items[0].Level);
        Assert.Equal("• Nested point", items[1].Text);
        Assert.Equal(1, items[1].Level);
        Assert.Equal("1. First step", items[2].Text);
        Assert.Equal("2. Second step", items[3].Text);
        Assert.Equal("• Done item", items[4].Text);
        // Revert: the quote marker, the pipes, the separator row, list continuation lines or the number kept raw fails this.
    }

    [Fact]
    public void Inline_markdown_html_comments_and_entities_render_as_plain_text()
    {
        Assert.Equal("bold, italic, code and a link", MarkdownPlain.Render("**bold**, _italic_, `code` and [a link](https://x.y/z)"));
        Assert.Equal("Badge row", MarkdownPlain.Render("[![build](https://img.shields.io/b)](https://ci) Badge row"));
        Assert.Equal("snake_case_name stays", MarkdownPlain.Render("snake_case_name stays"));
        Assert.Equal("2 * 3 * 4", MarkdownPlain.Render("2 * 3 * 4"));
        Assert.Equal("gone and *escaped*", MarkdownPlain.Render("~~gone~~ and \\*escaped\\*"));
        Assert.Equal("a < b & c", MarkdownPlain.Render("a &lt; b &amp; c"));
        Assert.Equal("Site: https://example.org", MarkdownPlain.Render("Site: <https://example.org>"));
        Assert.Equal("before\n\nafter", MarkdownPlain.Render("before\n\n<!-- a comment\nover lines -->\nafter"));
        Assert.DoesNotContain("hidden", MarkdownPlain.Render("keep <!-- hidden --> this"), StringComparison.Ordinal);
        // A setext heading and a rule.
        var blocks = MarkdownPlain.RenderBlocks("Title\n=====\n\ntext\n\n***\n\nSub\n---", int.MaxValue);
        Assert.Equal(new[] { "Title", "text", "Sub" }, blocks.Select(b => b.Text));
        Assert.Equal(MarkdownBlockKind.Heading, blocks[0].Kind);
        Assert.Equal(1, blocks[0].Level);
        Assert.Equal(MarkdownBlockKind.Heading, blocks[2].Kind);
        Assert.Equal(2, blocks[2].Level);
        // A GitHub alert's marker is dropped; its text is the quote.
        var alert = MarkdownPlain.RenderBlocks("> [!NOTE]\n> Back up your save.", int.MaxValue);
        Assert.Equal(MarkdownBlockKind.Quote, Assert.Single(alert).Kind);
        Assert.Equal("Back up your save.", alert[0].Text);
        // Revert: emphasis inside words, a lost autolink, comment text, or a raw "=====" fails this.
    }

    [Fact]
    public void The_about_rows_draw_a_quote_as_an_indented_note_and_a_table_as_aligned_columns()
    {
        var blocks = MarkdownPlain.RenderBlocks(TemplateReadme, int.MaxValue);
        var rows = ModAboutLayout.Rows(blocks, ModPageSpacing.ReadableWidth, "My Mod");

        // The page's own title already names the mod: the README's "# My Mod" is not repeated.
        Assert.DoesNotContain(rows, r => r.Cells.Any(c => c.Text == "My Mod"));
        Assert.Contains(rows, r => r.Cells.Any(c => c.Text == "Settings"));
        Assert.True(ModAboutLayout.IsTitleHeading(blocks[0], "my mod"));
        Assert.False(ModAboutLayout.IsTitleHeading(blocks[0], "Another Mod"));

        var quote = rows.Single(r => r.Cells.Any(c => c.Text.StartsWith("Requires Progenitor.", StringComparison.Ordinal)));
        Assert.Equal(ModAboutLayout.IndentKey, quote.Cells[0].Key);
        var quoteText = quote.Cells.Single(c => c.Key == ModAboutLayout.TextKey);
        Assert.Equal(CellTone.Muted, quoteText.Tone);
        Assert.Equal(CellKind.Text, quoteText.Kind);

        var heading = rows.Single(r => r.Cells.Any(c => c.Text == "Settings"));
        Assert.Equal(ModAboutLayout.HeadingFontSize, heading.Cells.Single().FontSize);
        Assert.Equal(ModAboutLayout.HeadingGapAbove, heading.GapAbove);

        // Two aligned tables: a muted header row, then a row per data row, the columns the same width on every row.
        var heads = rows.Where(r => r.Key.EndsWith(ModAboutLayout.TableHeadSuffix, StringComparison.Ordinal)).ToList();
        Assert.Equal(2, heads.Count);
        Assert.Equal(new[] { "Setting", "Default", "Who decides", "What it does" }, heads[0].Cells.Select(c => c.Text));
        Assert.All(heads[0].Cells, c => Assert.Equal(CellTone.Muted, c.Tone));
        int at = rows.IndexOf(heads[0]);
        var first = rows[at + 1];
        Assert.Equal(new[] { "Display / Show notices", "On", "Each player", "Show this mod's notices at the top of the screen." }, first.Cells.Select(c => c.Text));
        for (int c = 0; c < 4; c++)
            Assert.Equal(heads[0].Cells[c].Width.Value, first.Cells[c].Width.Value, 1);
        float total = first.Cells.Sum(c => c.Width.Value) + 3 * CellLayout.Gap;
        Assert.True(total <= ModPageSpacing.ReadableWidth + 0.5f, "the table fits the text width: " + total);
        Assert.All(first.Cells, c => Assert.True(c.Width.Value >= ModAboutLayout.MinColumnWidth - 0.5f || c.Text.Length <= 8, c.Text));
        // A wrapping cell makes its row taller than one line.
        var greeting = rows[at + 2];
        Assert.True(greeting.Height > ModAboutLayout.LineHeight(1, CellLayout.TextFontSize));

        // Nested list items are indented one step; every row's text is plain.
        var nested = rows.Single(r => r.Cells.Any(c => c.Text == "• Nested point"));
        Assert.Equal(ModAboutLayout.IndentKey, nested.Cells[0].Key);
        Assert.All(rows, r => Assert.All(r.Cells, c => Assert.DoesNotContain("|", c.Text, StringComparison.Ordinal)));
        // Revert: the quote drawn as body text, the README's title repeated, or a table as pipe text fails this.
    }

    [Fact]
    public void A_table_too_wide_for_the_text_width_is_drawn_as_labeled_lines()
    {
        var blocks = MarkdownPlain.RenderBlocks(TemplateReadme, int.MaxValue);
        var rows = ModAboutLayout.Rows(blocks, 300f, "My Mod");
        Assert.DoesNotContain(rows, r => r.Key.EndsWith(ModAboutLayout.TableHeadSuffix, StringComparison.Ordinal));
        Assert.Contains(rows, r => r.Cells.Any(c => c.Text.StartsWith("Display / Show notices — Default: On", StringComparison.Ordinal)));
        // Six columns are always lines.
        var six = new[] { "a", "b", "c", "d", "e", "f" };
        Assert.Null(ModAboutLayout.TableColumns(six, new[] { (IReadOnlyList<string>)six }, 2000f));
        // A cut row (the page's character cap) has no cells and is drawn as its line.
        var cut = MarkdownPlain.CapBlocks(blocks, 400);
        Assert.Null(cut[cut.Count - 1].Cells);
        // Revert: a squeezed table with unreadable columns, or a cut row drawn with stale cells, fails this.
    }

    [Fact]
    public void Tags_in_a_quote_or_a_table_never_reach_a_label_unescaped()
    {
        var blocks = MarkdownPlain.RenderBlocks(
            "> <color=red>red</color> &lt;size=99&gt;big\n\n| a | b |\n|---|---|\n| `<sprite=0>` | <b>x</b> |", int.MaxValue);
        var rows = ModAboutLayout.Rows(blocks, ModPageSpacing.ReadableWidth, "Mod");
        var texts = rows.SelectMany(r => r.Cells).Select(c => c.Text).Where(t => t.Length > 0).ToList();
        Assert.Contains(texts, t => t.Contains("<size=99>", StringComparison.Ordinal));
        Assert.Contains(texts, t => t.Contains("<sprite=0>", StringComparison.Ordinal));
        Assert.DoesNotContain(texts, t => t.Contains("<color", StringComparison.Ordinal));
        foreach (var text in texts)
        {
            string drawn = UiRichText.Literal(text);
            Assert.DoesNotContain("<size", drawn.Replace("<noparse><</noparse>", ""), StringComparison.Ordinal);
            Assert.DoesNotContain("<sprite", drawn.Replace("<noparse><</noparse>", ""), StringComparison.Ordinal);
        }
        // Revert: an About cell kind whose write skips the escape fails the page-level test; this pins the text side.
    }
}
