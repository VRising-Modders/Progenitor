using System;
using System.Collections.Generic;
using System.Linq;
using Progenitor.Chat;
using Progenitor.Ui;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Chat stage 3 (mod links): the plain-text token (<see cref="ModLinkToken"/>), where a link goes
/// (<see cref="ModLinks.Decide"/>), the open and share flows against a fake host, and the share controls on the
/// Mods list, the mod page and the Install list. Game-free.
/// </summary>
public sealed class ModLinkTests : IDisposable
{
    private readonly FakeHost _host = new();
    private readonly IModLinkHost _previous;

    public ModLinkTests()
    {
        _previous = ModLinks.Host;
        ModLinks.Reset();
        ModLinks.Host = _host;
    }

    public void Dispose()
    {
        ModLinks.Reset();
        ModLinks.Host = _previous;
    }

    // ---- the token ---------------------------------------------------------------------------

    [Theory]
    [InlineData("dyno-ExampleCommandFramework", null, "[mod:dyno-ExampleCommandFramework]")]
    [InlineData("dyno-ExampleCommandFramework", "", "[mod:dyno-ExampleCommandFramework]")]
    [InlineData("dyno-ExampleCommandFramework", "0.10.4", "[mod:dyno-ExampleCommandFramework@0.10.4]")]
    [InlineData(" Some_Team-Mod_2 ", " 1.2.3.4 ", "[mod:Some_Team-Mod_2@1.2.3.4]")]
    public void Format_writes_the_token(string package, string? version, string expected) =>
        Assert.Equal(expected, ModLinkToken.Format(package, version));

    [Theory]
    [InlineData("", null)]
    [InlineData("NoDash", null)]
    [InlineData("a-b-c", null)]
    [InlineData("-Name", null)]
    [InlineData("Ns-", null)]
    [InlineData("Ns-Na me", null)]
    [InlineData("Ns-Name]", null)]
    [InlineData("Ns-Name", "1.2.3.4.5")]
    [InlineData("Ns-Name", "1..2")]
    [InlineData("Ns-Name", "v1.2")]
    [InlineData("Ns-Name", "1.2-beta")]
    public void Format_refuses_anything_invalid(string package, string? version) =>
        Assert.Equal("", ModLinkToken.Format(package, version));

    [Fact]
    public void Parts_are_limited_to_64_characters()
    {
        string max = new('a', 64);
        Assert.True(ModLinkToken.IsPackage(max + "-" + max));
        Assert.False(ModLinkToken.IsPackage(max + "a-b"));
        Assert.False(ModLinkToken.IsPackage("a-" + max + "b"));
        Assert.True(ModLinkToken.IsVersion("123456789.1"));
        Assert.False(ModLinkToken.IsVersion("1234567890"));
    }

    [Fact]
    public void Find_returns_positions_package_and_version()
    {
        string text = "try [mod:dyno-ExampleCommandFramework] and [mod:Ns-Name@1.2.3]!";
        var found = ModLinkToken.Find(text).ToList();
        Assert.Equal(2, found.Count);
        Assert.Equal(4, found[0].start);
        Assert.Equal("[mod:dyno-ExampleCommandFramework]", text.Substring(found[0].start, found[0].length));
        Assert.Equal("dyno-ExampleCommandFramework", found[0].package);
        Assert.Null(found[0].version);
        Assert.Equal("[mod:Ns-Name@1.2.3]", text.Substring(found[1].start, found[1].length));
        Assert.Equal("Ns-Name", found[1].package);
        Assert.Equal("1.2.3", found[1].version);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("no links here")]
    [InlineData("[mod:Ns-Name")]
    [InlineData("[mod:Ns Name]")]
    [InlineData("[mod: Ns-Name]")]
    [InlineData("[MOD:Ns-Name]")]
    [InlineData("[mod:Ns-Name@]")]
    [InlineData("[mod:Ns-Name@1.x]")]
    [InlineData("[mod:Ns-Name@1.2@3]")]
    [InlineData("[mod:../../etc-passwd]")]
    [InlineData("[mod:https://example.com]")]
    [InlineData("[mod:Ns-Name<color=red>]")]
    [InlineData("[mod:]")]
    public void Find_treats_malformed_tokens_as_plain_text(string? text) =>
        Assert.Empty(ModLinkToken.Find(text!));

    [Fact]
    public void A_malformed_token_does_not_hide_a_valid_one_after_it()
    {
        var found = ModLinkToken.Find("[mod:bad [mod:Ns-Name]").ToList();
        Assert.Single(found);
        Assert.Equal("Ns-Name", found[0].package);
        Assert.Equal(9, found[0].start);
    }

    [Fact]
    public void A_line_yields_at_most_eight_links()
    {
        string text = string.Concat(Enumerable.Range(0, 12).Select(i => "[mod:Ns-Mod" + i + "] "));
        var found = ModLinkToken.Find(text).ToList();
        Assert.Equal(ModLinkToken.MaxPerLine, found.Count);
        Assert.Equal("Ns-Mod7", found[7].package);
    }

    [Fact]
    public void TryParse_accepts_only_a_whole_token()
    {
        Assert.True(ModLinkToken.TryParse("[mod:Ns-Name@2.0]", out string package, out string? version));
        Assert.Equal("Ns-Name", package);
        Assert.Equal("2.0", version);
        Assert.False(ModLinkToken.TryParse("[mod:Ns-Name] ", out _, out _));
        Assert.False(ModLinkToken.TryParse("x[mod:Ns-Name]", out _, out _));
    }

    [Fact]
    public void Format_and_Find_round_trip()
    {
        string token = ModLinkToken.Format("Ns-Name", "1.0.0");
        var found = ModLinkToken.Find("see " + token).Single();
        Assert.Equal(("Ns-Name", "1.0.0"), (found.package, found.version));
    }

    // ---- the decision --------------------------------------------------------------------------

    private static ModLinkFacts Facts(
        IEnumerable<ModLinkInstalled>? installed = null, bool enabled = true, bool loaded = true, bool loading = false,
        Dictionary<string, (string, string)>? listings = null) => new()
    {
        Installed = (installed ?? Array.Empty<ModLinkInstalled>()).ToList(),
        CatalogEnabled = enabled,
        CatalogLoaded = loaded,
        CatalogLoading = loading,
        Listing = p => listings != null && listings.TryGetValue(p, out var hit) ? hit : null,
    };

    [Fact]
    public void An_installed_package_opens_its_mod_page_ignoring_case()
    {
        var d = ModLinks.Decide("ns-name", null, Facts(new[] { new ModLinkInstalled("ns.plugin", "Ns-Name", "1.0.0") }));
        Assert.Equal(ModLinkTarget.Installed, d.Target);
        Assert.Equal("ns.plugin", d.PluginId);
        Assert.Equal(ModLinks.StatusInstalled, d.Status);
        Assert.Equal("", d.Notice);
    }

    [Fact]
    public void An_installed_package_wins_over_its_catalog_listing()
    {
        var listings = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase) { ["Ns-Name"] = ("Ns-Name", "2.0.0") };
        var d = ModLinks.Decide("Ns-Name", null, Facts(new[] { new ModLinkInstalled("ns.plugin", "Ns-Name", "1.0.0") }, listings: listings));
        Assert.Equal(ModLinkTarget.Installed, d.Target);
    }

    [Fact]
    public void A_different_installed_version_still_opens_the_page_and_says_so()
    {
        var d = ModLinks.Decide("Ns-Name", "2.0.0", Facts(new[] { new ModLinkInstalled("ns.plugin", "Ns-Name", "1.0.0") }));
        Assert.Equal(ModLinkTarget.Installed, d.Target);
        Assert.Equal(ModLinks.InstalledVersionNotice("Ns-Name", "2.0.0", "1.0.0"), d.Notice);
    }

    [Fact]
    public void A_package_with_several_plugins_prefers_the_one_at_the_requested_version()
    {
        var d = ModLinks.Decide("Ns-Name", "2.0.0", Facts(new[]
        {
            new ModLinkInstalled("ns.a", "Ns-Name", "1.0.0"),
            new ModLinkInstalled("ns.b", "Ns-Name", "2.0.0"),
        }));
        Assert.Equal("ns.b", d.PluginId);
        Assert.Equal("", d.Notice);
    }

    [Fact]
    public void A_listed_package_opens_its_install_listing()
    {
        var listings = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase) { ["Ns-Name"] = ("Ns-Name", "1.4.0") };
        var d = ModLinks.Decide("ns-name", null, Facts(listings: listings));
        Assert.Equal(ModLinkTarget.Catalog, d.Target);
        Assert.Equal("Ns-Name", d.CatalogKey);
        Assert.Equal(ModLinks.StatusCatalog, d.Status);
        Assert.Equal("", d.Notice);

        var versioned = ModLinks.Decide("Ns-Name", "1.0.0", Facts(listings: listings));
        Assert.Equal(ModLinkTarget.Catalog, versioned.Target);
        Assert.Equal(ModLinks.CatalogVersionNotice("Ns-Name", "1.0.0", "1.4.0"), versioned.Notice);
    }

    [Fact]
    public void Unknown_off_and_offline_say_why()
    {
        var unknown = ModLinks.Decide("Ns-Name", null, Facts());
        Assert.Equal(ModLinkTarget.Unknown, unknown.Target);
        Assert.StartsWith(ModLinks.StatusNotFoundPrefix, unknown.Status);
        Assert.Equal(ModLinks.UnknownNotice("Ns-Name"), unknown.Notice);

        var off = ModLinks.Decide("Ns-Name", null, Facts(enabled: false));
        Assert.Equal(ModLinkTarget.SourceOff, off.Target);
        Assert.StartsWith(ModLinks.StatusNotFoundPrefix, off.Status);
        Assert.Equal(ModLinks.SourceOffNotice("Ns-Name"), off.Notice);

        var offline = ModLinks.Decide("Ns-Name", null, Facts(loaded: false));
        Assert.Equal(ModLinkTarget.Offline, offline.Target);
        Assert.Equal(ModLinks.OfflineNotice("Ns-Name"), offline.Notice);
    }

    [Theory]
    [InlineData("not a package", null)]
    [InlineData("Ns-Name", "1.x")]
    public void An_invalid_link_opens_nothing(string package, string? version)
    {
        var d = ModLinks.Decide(package, version, Facts());
        Assert.Equal(ModLinkTarget.Invalid, d.Target);
        Assert.False(d.Opens);
        Assert.Equal(ModLinks.StatusInvalid, d.Status);
    }

    // ---- open --------------------------------------------------------------------------------

    [Fact]
    public void Open_with_the_workspace_open_navigates_at_once()
    {
        _host.Open = true;
        _host.FactsValue = Facts(new[] { new ModLinkInstalled("ns.plugin", "Ns-Name", "1.0.0") });
        Assert.Equal(ModLinks.StatusInstalled, ModLinks.Open("Ns-Name", null));
        Assert.Equal(new[] { "installed:ns.plugin" }, _host.Calls);
        Assert.Empty(_host.Notices);
    }

    [Fact]
    public void Open_with_the_workspace_closed_opens_it_and_finishes_on_a_later_frame()
    {
        var listings = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase) { ["Ns-Name"] = ("Ns-Name", "1.0.0") };
        _host.FactsValue = Facts(listings: listings);
        Assert.Equal(ModLinks.StatusOpening, ModLinks.Open("Ns-Name", null));
        Assert.Equal(new[] { "open-workspace" }, _host.Calls);
        ModLinks.Tick();
        Assert.Equal(new[] { "open-workspace" }, _host.Calls);
        _host.Open = true;
        ModLinks.Tick();
        Assert.Equal(new[] { "open-workspace", "catalog:Ns-Name" }, _host.Calls);
        Assert.Equal(ModLinks.StatusCatalog, ModLinks.LastStatus);
        ModLinks.Tick();
        Assert.Equal(2, _host.Calls.Count);
    }

    [Fact]
    public void Open_waits_for_a_loading_catalog_before_it_says_offline()
    {
        _host.Open = true;
        _host.FactsValue = Facts(loaded: false, loading: true);
        Assert.Equal(ModLinks.StatusOpening, ModLinks.Open("Ns-Name", null));
        Assert.Empty(_host.Notices);
        var listings = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase) { ["Ns-Name"] = ("Ns-Name", "1.0.0") };
        _host.FactsValue = Facts(listings: listings);
        ModLinks.Tick();
        Assert.Equal(new[] { "catalog:Ns-Name" }, _host.Calls);
    }

    [Fact]
    public void Open_of_an_unknown_package_shows_a_notice_and_navigates_nowhere()
    {
        _host.Open = true;
        _host.FactsValue = Facts();
        Assert.StartsWith(ModLinks.StatusNotFoundPrefix, ModLinks.Open("Ns-Name", "1.0.0"));
        Assert.Empty(_host.Calls);
        Assert.Equal(new[] { ModLinks.UnknownNotice("Ns-Name") }, _host.Notices);
    }

    [Fact]
    public void Open_of_an_invalid_link_never_touches_the_workspace()
    {
        Assert.Equal(ModLinks.StatusInvalid, ModLinks.Open("../evil", null));
        Assert.Empty(_host.Calls);
        Assert.Equal(new[] { ModLinks.InvalidNotice }, _host.Notices);
    }

    [Fact]
    public void Open_gives_up_when_the_workspace_never_opens()
    {
        _host.FactsValue = Facts();
        ModLinks.Open("Ns-Name", null);
        for (int i = 0; i < ModLinks.OpenTimeoutFrames + 2; i++) ModLinks.Tick();
        Assert.StartsWith(ModLinks.StatusNotFoundPrefix, ModLinks.LastStatus);
        Assert.Equal(new[] { "open-workspace" }, _host.Calls);
    }

    // ---- share -------------------------------------------------------------------------------

    [Fact]
    public void Share_at_the_title_copies_and_says_so()
    {
        Assert.Equal(ModLinks.ShareCopied, ModLinks.Share("Ns-Name", "1.0.0"));
        Assert.Equal("[mod:Ns-Name@1.0.0]", _host.Clipboard);
        Assert.Equal(new[] { ModLinks.CopiedNotice("Ns-Name") }, _host.Notices);
    }

    [Fact]
    public void Share_without_a_clipboard_shows_the_token()
    {
        _host.CanCopy = false;
        Assert.Equal(ModLinks.ShareCopyFailed, ModLinks.Share("Ns-Name", null));
        Assert.Equal(new[] { ModLinks.CopyUnavailablePrefix + "[mod:Ns-Name]" }, _host.Notices);
    }

    [Fact]
    public void A_settings_link_waits_for_the_menu_then_runs_once()
    {
        int ran = 0;
        ModLinks.OpenWorkspaceThen(() => ran++);
        Assert.Equal(new[] { "open-workspace" }, _host.Calls);
        ModLinks.Tick();
        Assert.Equal(0, ran);
        Assert.Single(_host.Calls);
        _host.Open = true;
        ModLinks.Tick();
        ModLinks.Tick();
        Assert.Equal(1, ran);
    }

    [Fact]
    public void A_settings_link_runs_at_once_when_the_menu_is_open()
    {
        _host.Open = true;
        int ran = 0;
        ModLinks.OpenWorkspaceThen(() => ran++);
        Assert.Equal(1, ran);
        Assert.Empty(_host.Calls);
    }

    [Fact]
    public void Share_refuses_an_invalid_package()
    {
        Assert.Equal(ModLinks.ShareInvalid, ModLinks.Share("bad package", null));
        Assert.Empty(_host.Calls);
        Assert.Null(_host.Clipboard);
    }

    // ---- the controls ---------------------------------------------------------------------------

    [Fact]
    public void A_shareable_mods_list_entry_has_a_hidden_share_overlay_and_reports_hover()
    {
        var entry = new ModsListEntry { Id = "x", Name = "X" };
        var plain = ModsListLayout.EntryRow(entry, 320f);
        Assert.Null(plain.Find(ModsListLayout.CellShare));
        Assert.False(plain.Hover);

        var row = ModsListLayout.EntryRow(entry, 320f, shareable: true);
        var share = row.Find(ModsListLayout.CellShare)!;
        Assert.Equal(CellKind.Button, share.Kind);
        Assert.False(share.Visible);
        Assert.Equal(ModsListLayout.CellName, share.OverlayOf);
        Assert.Equal(ModLinks.ShareRowCaption, share.Text);
        Assert.True(row.Hover);
    }

    [Fact]
    public void The_mod_page_button_row_carries_share_in_chat_last()
    {
        // MODPAGE2: the Details grid's Store page row.
        var buttons = ModPagePane.StoreButtons("https://example.test/p", share: true);
        Assert.Equal(new[] { ModPagePane.CellStore, ModPagePane.CellShare }, buttons.Select(b => b.Key).ToArray());
        Assert.DoesNotContain(ModPagePane.StoreButtons("https://example.test/p", share: false), b => b.Key == ModPagePane.CellShare);
        Assert.Contains(ModPagePane.StoreButtons("", share: true), b => b.Key == ModPagePane.CellShare);
        Assert.Empty(ModPagePane.StoreButtons("", share: false));
    }

    [Fact]
    public void An_install_row_that_cannot_be_shared_keeps_a_hidden_plain_share_cell()
    {
        // A key that is not a Thunderstore package id (whatever the share handler is) is never offered.
        var listing = InstallListViewTests.Listing("NotAPackage", "Name");
        Assert.False(InstallModsPane.CanShare(listing));
        var cell = InstallModsPane.ShareRowCell(listing);
        Assert.Equal(InstallModsPane.CellShare, cell.Key);
        Assert.False(cell.Visible);
        Assert.NotEqual(CellKind.Button, cell.Kind);
    }

    // ---- the fake ------------------------------------------------------------------------------

    private sealed class FakeHost : IModLinkHost
    {
        internal bool Open;
        internal bool CanCopy = true;
        internal ModLinkFacts FactsValue = new();
        internal readonly List<string> Calls = new();
        internal readonly List<string> Notices = new();
        internal string? Clipboard;

        public bool WorkspaceOpen => Open;

        public void OpenWorkspace() => Calls.Add("open-workspace");

        public ModLinkFacts Facts() => FactsValue;

        public void OpenInstalled(string pluginId) => Calls.Add("installed:" + pluginId);

        public void OpenCatalog(string catalogKey) => Calls.Add("catalog:" + catalogKey);

        public bool CopyToClipboard(string text)
        {
            if (!CanCopy) return false;
            Clipboard = text;
            return true;
        }

        public void Notice(string title, string body) => Notices.Add(body);
    }
}
