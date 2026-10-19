using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Progenitor.Install;
using Progenitor.Ui;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Game-free Thunderstore README/changelog adapter: versioned URLs, JSON, markdown
/// plain-text rules, in-memory cache, and fetch failures. No network.
/// </summary>
public sealed class ThunderstoreMarkdownTests
{
    [Fact]
    public void Readme_url_is_versioned_and_uses_owner_as_namespace()
    {
        Assert.True(ThunderstoreAdapter.TryBuildReadmeUrl(
            "dyno", "ExampleCommandFramework", "0.11.0", out var url, out var error), error);
        Assert.Equal(
            "https://thunderstore.io/api/experimental/package/dyno/ExampleCommandFramework/0.11.0/readme/",
            url);
        Assert.False(ThunderstoreAdapter.TryBuildReadmeUrl("dyno", "ExampleCommandFramework", "", out _, out error));
        Assert.Equal(InstallReasons.DocumentIdentityMissing, error);
        Assert.DoesNotContain("/ExampleCommandFramework/readme/", url, StringComparison.Ordinal);
        // Revert: dropping version (the unversioned form is HTTP 400) or using full_name as namespace fails this.
    }

    [Fact]
    public void Markdown_json_empty_string_is_success_and_a_missing_key_is_unavailable()
    {
        Assert.True(ThunderstoreAdapter.TryReadMarkdown("{\"markdown\":\"\"}", out var empty, out var error), error);
        Assert.Equal("", empty);
        Assert.True(ThunderstoreAdapter.TryReadMarkdown("{\"markdown\":null}", out var nil, out error), error);
        Assert.Equal("", nil);
        Assert.True(ThunderstoreAdapter.TryReadMarkdown("{\"markdown\":\"# Hi\"}", out var text, out error), error);
        Assert.Equal("# Hi", text);
        Assert.False(ThunderstoreAdapter.TryReadMarkdown("{\"body\":\"# Hi\"}", out _, out error));
        Assert.Equal(InstallReasons.DocumentMissingMarkdown, error);
        Assert.False(ThunderstoreAdapter.TryReadMarkdown("not-json", out _, out error));
        Assert.StartsWith(InstallReasons.DocumentMalformedJson, error, StringComparison.Ordinal);
        Assert.False(ThunderstoreAdapter.TryReadMarkdown("[\"invalid version number 'readme'\"]", out _, out error));
        Assert.Equal(InstallReasons.DocumentNotObject, error);
        // Revert: treating a missing markdown key as empty, or refusing an empty string, fails this.
    }

    [Fact]
    public void Markdown_heading_becomes_its_own_line()
    {
        Assert.Equal("Title", MarkdownPlain.Render("# Title"));
        // Revert: leaving the '#' in the listing text fails this.
    }

    [Fact]
    public void Markdown_paragraphs_are_kept()
    {
        Assert.Equal("first\n\nsecond", MarkdownPlain.Render("first\n\nsecond"));
        // Revert: collapsing paragraphs into one line fails this.
    }

    [Fact]
    public void Markdown_list_items_become_bullets()
    {
        Assert.Equal("• a\n• b", MarkdownPlain.Render("- a\n- b"));
        Assert.Equal("1. one", MarkdownPlain.Render("1. one"));
        // Revert: dropping the list marker without a bullet, or a numbered item's number, fails this.
    }

    [Fact]
    public void Markdown_links_keep_text_and_drop_urls()
    {
        Assert.Equal("V Roles", MarkdownPlain.Render("[V Roles](https://thunderstore.io/c/v-rising/p/orvik/VRanks/)"));
        // Revert: emitting the URL on the listing page fails this.
    }

    [Fact]
    public void Markdown_images_are_dropped()
    {
        string rendered = MarkdownPlain.Render("intro\n\n![](https://example/logo.png)\n\nafter");
        Assert.DoesNotContain("example/logo", rendered, StringComparison.Ordinal);
        Assert.Contains("intro", rendered, StringComparison.Ordinal);
        Assert.Contains("after", rendered, StringComparison.Ordinal);
        // Revert: keeping the image URL as visible text fails this.
    }

    [Fact]
    public void Markdown_html_and_badges_are_dropped()
    {
        string rendered = MarkdownPlain.Render(
            "<details><summary>Hidden</summary>body</details>\n\n![build](https://img.shields.io/badge/x-y)");
        Assert.DoesNotContain("<details>", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("shields.io", rendered, StringComparison.Ordinal);
        Assert.Contains("Hidden", rendered, StringComparison.Ordinal);
        Assert.Contains("body", rendered, StringComparison.Ordinal);
        // Revert: leaving HTML tags or badge URLs in the listing text fails this.
    }

    [Fact]
    public void Markdown_code_fences_keep_the_code_lines()
    {
        Assert.Equal("int x;", MarkdownPlain.Render("```csharp\nint x;\n```"));
        // Revert: dropping fenced code or keeping the fence ticks fails this.
    }

    [Fact]
    public void Markdown_table_rows_are_labeled_by_the_header_row()
    {
        Assert.Equal("1 — b: 2", MarkdownPlain.Render("| a | b |\n| --- | --- |\n| 1 | 2 |"));
        // ABOUTMD1: a pipe row without a separator is not a table, and still shows no raw pipes.
        Assert.Equal("a · b", MarkdownPlain.Render("| a | b |"));
        // Revert: dropping tables, keeping the separator row, or drawing raw pipes fails this.
    }

    [Fact]
    public void Markdown_blocks_split_heading_paragraph_list_code_and_table()
    {
        var blocks = MarkdownPlain.RenderBlocks(
            "# Title\n\nfirst paragraph\n\n- alpha\n\n```\nint x;\n```\n\n| a | b |\n| --- | --- |\n| 1 | 2 |\n");
        Assert.Equal(MarkdownBlockKind.Heading, blocks[0].Kind);
        Assert.Equal("Title", blocks[0].Text);
        Assert.Equal(MarkdownBlockKind.Paragraph, blocks[1].Kind);
        Assert.Equal("first paragraph", blocks[1].Text);
        Assert.Equal(MarkdownBlockKind.ListItem, blocks[2].Kind);
        Assert.Equal("• alpha", blocks[2].Text);
        Assert.Equal(MarkdownBlockKind.Code, blocks[3].Kind);
        Assert.Equal("int x;", blocks[3].Text);
        Assert.Equal(MarkdownBlockKind.Table, blocks[4].Kind);
        Assert.Equal("1 — b: 2", blocks[4].Text);
        Assert.Equal(new[] { "1", "2" }, blocks[4].Cells);
        Assert.Equal(new[] { "a", "b" }, blocks[4].Header);
        Assert.Equal(5, blocks.Count);
        Assert.All(blocks, b => Assert.DoesNotContain("\n\n", b.Text, StringComparison.Ordinal));
        Assert.DoesNotContain(blocks, b => b.Text.IndexOf("Title", StringComparison.Ordinal) >= 0
            && b.Text.IndexOf("first paragraph", StringComparison.Ordinal) >= 0);
        // Revert: joining the document into one block, or dropping a kind, fails this.
    }

    [Fact]
    public void Markdown_block_cap_cuts_the_stream_and_no_block_exceeds_the_paragraph()
    {
        string para = new string('y', 100);
        var md = new System.Text.StringBuilder();
        for (int i = 0; i < 80; i++)
        {
            if (i > 0) md.Append("\n\n");
            md.Append(para);
        }

        var full = MarkdownPlain.RenderBlocks(md.ToString(), int.MaxValue);
        var capped = MarkdownPlain.RenderBlocks(md.ToString(), MarkdownPlain.DefaultMaxChars);
        Assert.True(full.Count > capped.Count);
        Assert.True(MarkdownPlain.ContentLength(capped) <= MarkdownPlain.DefaultMaxChars + MarkdownPlain.Ellipsis.Length);
        Assert.All(capped, b =>
        {
            Assert.Equal(MarkdownBlockKind.Paragraph, b.Kind);
            Assert.True(b.Text.Length <= 100 + MarkdownPlain.Ellipsis.Length);
            Assert.DoesNotContain("\n\n", b.Text, StringComparison.Ordinal);
        });
        Assert.True(capped[capped.Count - 1].Text.Length <= para.Length + MarkdownPlain.Ellipsis.Length);
        // Revert: leaving the uncapped stream, or merging paragraphs into one block, fails this.
    }

    [Fact]
    public void Markdown_is_truncated_at_the_plain_cap()
    {
        string longMd = new string('x', MarkdownPlain.DefaultMaxChars + 50);
        string rendered = MarkdownPlain.Render(longMd);
        Assert.Equal(MarkdownPlain.DefaultMaxChars + MarkdownPlain.Ellipsis.Length, rendered.Length);
        Assert.EndsWith(MarkdownPlain.Ellipsis, rendered);
        string noted = InstallModsPane.FormatReadme(longMd, "https://thunderstore.io/c/v-rising/p/dyno/ExampleCommandFramework/");
        Assert.Contains(InstallModsCopy.ReadMoreOnThunderstore, noted, StringComparison.Ordinal);
        // Revert: emitting the raw 6 000+ character body without an ellipsis fails this.
    }

    [Fact]
    public async Task Cache_hit_does_not_touch_transport_again()
    {
        int calls = 0;
        var transport = new FakeTransport(req =>
        {
            calls++;
            string path = req.Uri.AbsolutePath;
            if (path.IndexOf("/readme/", StringComparison.OrdinalIgnoreCase) >= 0)
                return new CatalogTransportResponse(200, Bytes("{\"markdown\":\"# Hi\\n\\nHello\"}"));
            return new CatalogTransportResponse(200, Bytes("{\"markdown\":\"\"}"));
        });
        var fetcher = new CatalogFetcher(
            transport,
            new CatalogFetchOptions { MaxBytes = ThunderstoreListingDocuments.MaxBodyBytes, Timeout = TimeSpan.FromSeconds(5) },
            new ImmediateClock());
        var docs = new ThunderstoreListingDocuments();
        var first = await docs.LoadAsync("dyno", "ExampleCommandFramework", "0.11.0", fetcher, CancellationToken.None);
        Assert.True(first.ReadmeOk);
        Assert.Equal("# Hi\n\nHello", first.ReadmeMarkdown);
        Assert.Equal(1, calls); // TSTRAFFIC1: the README only, no changelog request
        var second = await docs.LoadAsync("dyno", "ExampleCommandFramework", "0.11.0", fetcher, CancellationToken.None);
        Assert.Same(first, second);
        Assert.Equal(1, calls);
        Assert.True(docs.TryGet(ThunderstoreListingDocuments.CacheKey("dyno", "ExampleCommandFramework", "0.11.0"), out _));
        // Revert: skipping the in-memory cache (a second GET) fails this.
    }

    [Fact]
    public async Task Non_200_and_non_json_are_unavailable_with_a_reason()
    {
        var notFound = new FakeTransport(_ => new CatalogTransportResponse(404, Bytes("nope")));
        var fetcher = new CatalogFetcher(
            notFound,
            new CatalogFetchOptions { MaxBytes = ThunderstoreListingDocuments.MaxBodyBytes, Timeout = TimeSpan.FromSeconds(5) },
            new ImmediateClock());
        var docs = new ThunderstoreListingDocuments();
        var missing = await docs.LoadAsync("dyno", "NoSuch", "1.0.0", fetcher, CancellationToken.None);
        Assert.False(missing.ReadmeOk);
        Assert.Equal(InstallReasons.HttpStatus(404), missing.ReadmeError);

        var badJson = new FakeTransport(_ => new CatalogTransportResponse(200, Bytes("not-json")));
        var badFetcher = new CatalogFetcher(
            badJson,
            new CatalogFetchOptions { MaxBytes = ThunderstoreListingDocuments.MaxBodyBytes, Timeout = TimeSpan.FromSeconds(5) },
            new ImmediateClock());
        var badDocs = new ThunderstoreListingDocuments();
        var malformed = await badDocs.LoadAsync("dyno", "Pack", "1.0.0", badFetcher, CancellationToken.None);
        Assert.False(malformed.ReadmeOk);
        Assert.StartsWith(InstallReasons.DocumentMalformedJson, malformed.ReadmeError, StringComparison.Ordinal);

        var huge = new FakeTransport(_ => new CatalogTransportResponse(200, Bytes("{\"markdown\":\"" + new string('x', 80) + "\"}")));
        var capped = new CatalogFetcher(
            huge,
            new CatalogFetchOptions { MaxBytes = 16, Timeout = TimeSpan.FromSeconds(5) },
            new ImmediateClock());
        var ceiling = await capped.FetchTextAsync(
            "https://thunderstore.io/api/experimental/package/dyno/Pack/1.0.0/readme/",
            16,
            CancellationToken.None);
        Assert.False(ceiling.Ok);
        Assert.Equal(InstallReasons.FetchSizeCeiling, ceiling.Error);
        // Revert: treating HTTP 404 or a non-JSON body as empty markdown fails this.
    }

    [Fact]
    public void Changelog_split_keeps_the_latest_entry()
    {
        MarkdownPlain.SplitLatest("`1.0.0`\n- first\n\n`0.9.0`\n- old", out var latest, out var rest);
        Assert.Contains("first", latest, StringComparison.Ordinal);
        Assert.DoesNotContain("old", latest, StringComparison.Ordinal);
        Assert.Contains("old", rest, StringComparison.Ordinal);
        Assert.Contains("0.9.0", rest, StringComparison.Ordinal);
        // Revert: showing the whole changelog as the latest entry fails this.
    }

    private sealed class FakeTransport : ICatalogTransport
    {
        private readonly Func<CatalogTransportRequest, CatalogTransportResponse> _handler;

        public FakeTransport(Func<CatalogTransportRequest, CatalogTransportResponse> handler) => _handler = handler;

        public Task<CatalogTransportResponse> SendAsync(CatalogTransportRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(_handler(request));
    }

    private sealed class ImmediateClock : ICatalogClock
    {
        public DateTime UtcNow => new(2026, 9, 12, 12, 0, 0, DateTimeKind.Utc);
        public Task Delay(TimeSpan duration, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private static MemoryStream Bytes(string text) => new(Encoding.UTF8.GetBytes(text));
}
