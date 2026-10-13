using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Progenitor.Networking;
using Progenitor.Plugins;
using Progenitor.Ui;
using Xunit;
using UiApi = Progenitor.Ui.Ui;

namespace Progenitor.Tests;

/// <summary>
/// JOINFLOW1: the join screens drawn through a fake runtime — a refused join end to end (the refusal opens
/// the screens; the signed descriptor that follows, with its join section, is accepted by the real
/// <c>ModManifest.AcceptEnvelope</c> and moves them to the message), the buttons, Escape, the trust page's
/// rows, and the bridge readback. Same collection as the other <c>ModManifest</c> suites, which also swap
/// the UI runtime.
/// </summary>
[Collection("ModManifest")]
public sealed class JoinFlowPanelTests : IDisposable
{
    private const string Sample = "progenitor.sample";
    private readonly string _dir;
    private readonly Runtime _runtime = new();

    public JoinFlowPanelTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "progenitor-joinpanel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        Plugin.Logger = new TestLogSource();
        ModManifest.ResetForTests();
        ModManifest.TestConfigDirectory = _dir;
        ModManifest.TestLoadedMods = Array.Empty<LoadedModInfo>();
        ModManifest.TestIsServer = false;
        ModManifest.TestConnectHost = "127.0.0.1:9976";
        UiApi.ResetForTests();
        UiApi.SetRuntimeForTests(_runtime);
        JoinFlowPanel.Install();
    }

    public void Dispose()
    {
        JoinFlowPanel.Uninstall();
        ModManifest.ResetForTests();
        ModManifest.TestConfigDirectory = null;
        ModManifest.TestLoadedMods = null;
        ModManifest.TestIsServer = false;
        ModManifest.TestConnectHost = null;
        UiApi.ResetForTests();
        Plugin.Logger = null;
        try { Directory.Delete(_dir, recursive: true); }
        catch { /* temp */ }
    }

    private static Handshake.ModRecord Must(string id, string version) =>
        Handshake.ModRecord.CreateDeclared(id, version, NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact);

    private static JoinSection Section(string message) =>
        new(message, new ResetSchedule(ResetCountdown.FormatUtc(DateTime.UtcNow.AddDays(3).AddHours(1)), ResetRepeat.None, 1, ResetPrecision.Day, "now"),
            new[] { new RecommendedMod("maps.mod", "Owner-Maps", "1.0", "Better maps") }, new JoinServerZone("UTC", 0));

    /// <summary>The signed descriptor a refusing server sends: the failing row, sourced, with the join section.</summary>
    private static byte[] Descriptor(string message, string serverName = "Night Garden")
    {
        using var key = ModManifestCrypto.Generate();
        var overrides = new ManifestOverrides
        {
            ServerName = serverName,
            Mods = new[] { new ManifestModOverride { Id = Sample, Version = "0.1.0", Package = "ExampleTeam-Progenitor_Sample" } },
        };
        var loaded = new[] { new LoadedModInfo(Sample, "0.1.0", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact, true) };
        var envelope = ModManifest.Build(loaded, overrides, key, DateTime.UtcNow, Section(message));
        return ModManifestCrypto.Utf8(ModManifestJson.CanonicalEnvelope(envelope));
    }

    /// <summary>A successful join's signed catalog from <paramref name="key"/>, issued at <paramref name="issued"/>.</summary>
    private static byte[] Catalog(ModManifestKey key, string message, DateTime issued)
    {
        var loaded = new[] { new LoadedModInfo(Sample, "0.1.0", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact, true) };
        var envelope = ModManifest.Build(loaded, new ManifestOverrides { ServerName = "Night Garden" }, key, issued, Section(message));
        return ModManifestCrypto.Utf8(ModManifestJson.CanonicalEnvelope(envelope));
    }

    [Fact]
    public void Only_the_first_catalog_of_a_join_opens_the_in_world_screens_and_a_resend_never_does()
    {
        var clock = JoinFlowClient.Clock;
        double now = 100;
        JoinFlowClient.Clock = () => now;
        JoinFlowClient.InWorldReady = () => true;
        try
        {
            ModManifestDeny.TestServerAsked = new[] { Must(Sample, "0.1.0") };
            ModManifestDeny.TestProvided = new[] { Must(Sample, "0.1.0") };
            using var key = ModManifestCrypto.Generate();
            var issued = DateTime.UtcNow;

            // A Core loaded mid-session saw no join attempt: its first catalog opens nothing.
            ModManifest.AcceptEnvelope(Catalog(key, "Hello", issued));
            Assert.False(JoinFlowClient.InWorldPending);

            // A join attempt, then its first catalog: the screens open 2 s later.
            ModManifestDeny.OnJoinAttempt();
            ModManifest.AcceptEnvelope(Catalog(key, "Hello", issued.AddSeconds(1)));
            Assert.True(JoinFlowClient.InWorldPending);
            now += 3;
            JoinFlowClient.Tick();
            Assert.True(JoinFlowPanel.IsOpen);
            Assert.Equal(JoinPage.Message, JoinFlowClient.Session!.Page);
            Assert.Equal(JoinShowPolicy.ReasonFirstJoin, JoinFlowClient.Session!.MessageDecision.Reason);
            JoinFlowClient.Session!.Close();
            Assert.False(JoinFlowPanel.IsOpen);

            // An admin edits the message: the server re-signs and resends. Nothing opens mid-session.
            ModManifest.AcceptEnvelope(Catalog(key, "Changed", issued.AddSeconds(2)));
            Assert.False(JoinFlowClient.InWorldPending);
            now += 3;
            JoinFlowClient.Tick();
            Assert.False(JoinFlowPanel.IsOpen);

            // The next join decides again: the changed message shows.
            ModManifestDeny.OnJoinAttempt();
            ModManifest.AcceptEnvelope(Catalog(key, "Changed", issued.AddSeconds(3)));
            Assert.True(JoinFlowClient.InWorldPending);
            now += 3;
            JoinFlowClient.Tick();
            Assert.Equal(JoinShowPolicy.ReasonMessageChanged, JoinFlowClient.Session!.MessageDecision.Reason);
        }
        finally
        {
            JoinFlowClient.Clock = clock;
            JoinFlowClient.InWorldReady = () => false;
        }
    }

    [Fact]
    public void A_refused_join_opens_the_screens_and_the_signed_message_moves_them_to_the_message_page()
    {
        ModManifestDeny.TestServerAsked = new[] { Must(Sample, "0.1.0") };
        ModManifestDeny.TestProvided = Array.Empty<Handshake.ModRecord>();
        ModManifestDeny.OnJoinDenied("Mod mismatch: progenitor.sample: expected 0.1.0, got missing", "127.0.0.1:9976");

        // The refusal alone: the mods page, the read-only deny panel not opened.
        Assert.True(JoinFlowPanel.IsOpen);
        Assert.False(ModManifestDenyPanel.IsOpen);
        var s = JoinFlowClient.Session!;
        Assert.Equal(JoinPage.Mods, s.Page);
        Assert.Equal(JoinMode.Refused, s.Mode);
        var row = _runtime.Row(JoinFlowLayout.RequiredRow(Sample))!;
        Assert.Equal(JoinText.ActionCaption(JoinRowAction.AskAdmin), row.Cells().Single(c => c.Key == JoinFlowLayout.CellAction).Text);

        // The descriptor arrives with the section: the message page, the message box, the summary.
        ModManifest.AcceptEnvelope(Descriptor("Welcome!\nWipe {reset:relative} ({reset})."));
        Assert.Equal(JoinPage.Message, JoinFlowClient.Session!.Page);
        // A refusal shows the message whatever was seen before.
        Assert.Equal(JoinShowPolicy.ReasonRefused, JoinFlowClient.Session!.MessageDecision.Reason);
        Assert.Contains(("Welcome to Night Garden", true), _runtime.DialogTexts);
        string text = _runtime.Row(JoinFlowLayout.MessageRow)!.Cells().Single(c => c.Key == JoinFlowLayout.CellText).Text;
        Assert.Equal("Welcome!\nWipe in 3 days (3d).", text);
        Assert.Equal("1 required mod, 1 recommended. 1 needs attention.",
            _runtime.Row(JoinFlowLayout.SummaryRow)!.Cells().Single().Text);
        Assert.Equal(new[] { JoinText.LeaveButton, JoinText.ContinueButton }, Buttons());

        // Continue → mods: the row now has its Thunderstore source and installs (a later update answers).
        Assert.True(_runtime.Click(JoinFlowLayout.ButtonsRow, JoinFlowLayout.ButtonContinue));
        Assert.Equal(JoinPage.Mods, JoinFlowClient.Session!.Page);
        Assert.Contains(("Mods for Night Garden", true), _runtime.DialogTexts);
        var sourced = _runtime.Row(JoinFlowLayout.RequiredRow(Sample))!;
        // The status, version and source share the row's second line, fitted to the popup (live 2026-10-04).
        Assert.Contains("needs 0.1.0 · Thunderstore", sourced.Cells().Single(c => c.Key == JoinFlowLayout.CellName).Text, StringComparison.Ordinal);
        Assert.Equal("Install", sourced.Cells().Single(c => c.Key == JoinFlowLayout.CellAction).Text);
        Assert.Equal(new[] { JoinText.BackToMessageButton, JoinText.CloseButton, JoinText.InstallButton(1) }, Buttons());
        // JOINFLOW2: Install asks "Install from Thunderstore?" first; Back returns with nothing installed.
        Assert.True(_runtime.Click(JoinFlowLayout.RequiredRow(Sample), JoinFlowLayout.CellAction));
        Assert.Equal(JoinPage.ThunderstoreTrust, JoinFlowClient.Session!.Page);
        Assert.Contains((JoinText.TsTrustTitle, true), _runtime.DialogTexts);
        Assert.True(_runtime.Click(JoinFlowLayout.ButtonsRow, JoinFlowLayout.ButtonBack));
        Assert.Equal(JoinPage.Mods, JoinFlowClient.Session!.Page);
        Assert.Equal(JoinText.NothingInstalled, _runtime.Row(JoinFlowLayout.NoticeRow)!.Cells().Single().Text);

        // The recommended checkbox counts toward Install.
        Assert.True(_runtime.Click(JoinFlowLayout.RecommendedRow("maps.mod"), JoinFlowLayout.CellCheck));
        Assert.Equal(JoinText.InstallButton(2), Buttons().Last());

        // Escape is Back on the mods page, Close on the message page.
        UiApi.PressKeyForTests(27, true);
        UiApi.PressKeyForTests(17, true);
        Assert.Equal(JoinPage.Message, JoinFlowClient.Session!.Page);
        Assert.True(JoinFlowPanel.IsOpen);
        UiApi.PressKeyForTests(27, true);
        UiApi.PressKeyForTests(17, true);
        Assert.Null(JoinFlowClient.Session);
        Assert.False(JoinFlowPanel.IsOpen);
    }

    [Fact]
    public void The_bridge_reads_the_screens_and_presses_through_the_buttons_handlers()
    {
        ModManifestDeny.TestServerAsked = new[] { Must(Sample, "0.1.0") };
        ModManifestDeny.TestProvided = Array.Empty<Handshake.ModRecord>();
        ModManifestDeny.OnJoinDenied("Mod mismatch", "127.0.0.1:9976");
        ModManifest.AcceptEnvelope(Descriptor("Hello"));

        var read = JoinFlowTestBridge.Read();
        Assert.Equal("message", read["page"]);
        Assert.Equal("refused", read["mode"]);
        Assert.Equal("Hello", ((Dictionary<string, object?>)read["message"]!)["rendered"]);
        var required = (List<object?>)read["required"]!;
        Assert.Equal("install", ((Dictionary<string, object?>)required[0]!)["action"]);

        var pressed = JoinFlowTestBridge.Press("continue");
        Assert.Equal("mods", pressed["result"]);
        Assert.Equal("mods", pressed["page"]);
        Assert.Equal("toggled", JoinFlowTestBridge.Recommend("maps.mod")["result"]);
        Assert.Equal("message", JoinFlowTestBridge.Escape()["result"]);
        Assert.Equal("checked", JoinFlowTestBridge.DontShow(true)["result"]);
        Assert.Equal("left", JoinFlowTestBridge.Press("leave")["result"]);
        Assert.False((bool)JoinFlowTestBridge.Read()["open"]!);
        // Leave with the box ticked remembered the message for this server, and the readback says so.
        var row = JoinSeenStore.Get(JoinFlowClient.SeenPath, JoinFlowClient.ServerKey)!;
        Assert.Equal(JoinShowPolicy.MessageHash("Hello"), row.Dismissed);
        Assert.Equal(JoinShowPolicy.MessageHash("Hello"), row.MessageHash);

        // RESETWIPE1: the precision is automatic (days, then hours, then minutes and seconds in the last hour).
        var render = JoinFlowTestBridge.Render("{reset}", "2026-10-10T18:00:00Z", "None", 1, 0, "Any", "", "2026-10-07T13:42:55Z");
        Assert.Equal("3d", render["rendered"]);
        Assert.Equal("Day", render["precision"]);
        Assert.Equal(false, render["fromWipeSchedule"]);
        Assert.Equal("17m 05s", JoinFlowTestBridge.Render("{reset}", "2026-10-10T18:00:00Z", "None", 1, 0, "Any", "", "2026-10-10T17:42:55Z")["rendered"]);
        // A game schedule (every 30 days from Sep 1, any day: Oct 1) wins over the stored date.
        var wipe = JoinFlowTestBridge.Render("{reset}", "2026-10-10T18:00:00Z", "None", 1, 30, "Any", "2026-09-01", "2026-09-30T12:00:00Z");
        Assert.Equal(true, wipe["fromWipeSchedule"]);
        Assert.NotEqual("", wipe["effectiveNextUtc"]);
        Assert.NotEqual(ResetCountdown.FormatUtc(new DateTime(2026, 10, 10, 18, 0, 0, DateTimeKind.Utc)), wipe["effectiveNextUtc"]);
    }

    [Fact]
    public void A_server_name_cannot_carry_markup_into_the_dialog_title()
    {
        ModManifestDeny.TestServerAsked = new[] { Must(Sample, "0.1.0") };
        ModManifestDeny.TestProvided = Array.Empty<Handshake.ModRecord>();
        ModManifestDeny.OnJoinDenied("Mod mismatch: progenitor.sample: expected 0.1.0, got missing", "127.0.0.1:9976");
        string hostile = "<size=200>Night</size> Garden";
        ModManifest.AcceptEnvelope(Descriptor("Welcome!", hostile));
        Assert.Equal(JoinPage.Message, JoinFlowClient.Session!.Page);
        // Dialog titles are rich text; the operator-chosen name goes through UiRichText.Literal.
        Assert.Contains(("Welcome to " + UiRichText.Literal(hostile), true), _runtime.DialogTexts);
        Assert.DoesNotContain(_runtime.DialogTexts, t => t.Item1.Contains("<size=200>", StringComparison.Ordinal));
    }

    [Fact]
    public void A_message_cannot_carry_markup_into_the_box_and_a_long_one_scrolls()
    {
        string hostile = "<size=200>BIG</size> <color=red>red</color>";
        Assert.Equal(hostile, JoinFlowLayout.MessageBox(hostile, 600f).Cells.Single(c => c.Key == JoinFlowLayout.CellText).Text);
        // The text goes into a cell, whose runtime escapes every '<' (UiRichText.Literal); pinned here.
        Assert.Equal("<noparse><</noparse>size=200>", UiRichText.Literal("<size=200>"));
        string longText = string.Join("\n", Enumerable.Repeat("A line of the server message that wraps.", 40));
        Assert.False(JoinFlowLayout.FitsBox(longText, 600f));
        Assert.True(JoinFlowLayout.FitsBox("Short", 600f));
    }

    [Fact]
    public void A_link_in_the_message_opens_the_confirmation_which_copies_cancels_and_opens_through_its_buttons()
    {
        var copy = JoinFlowPanel.CopyToClipboard;
        var open = JoinFlowPanel.OpenUrl;
        string? copied = null, opened = null;
        JoinFlowPanel.CopyToClipboard = s => { copied = s; return true; };
        JoinFlowPanel.OpenUrl = u => opened = u;
        JoinLinkOpener.ResetForTests();
        try
        {
            ModManifestDeny.TestServerAsked = new[] { Must(Sample, "0.1.0") };
            ModManifestDeny.TestProvided = Array.Empty<Handshake.ModRecord>();
            ModManifestDeny.OnJoinDenied("Mod mismatch", "127.0.0.1:9976");
            ModManifest.AcceptEnvelope(Descriptor("Rules: discord.gg/nightgarden. <color=red>Map</color> https://example.com/map"));
            Assert.Equal(JoinPage.Message, JoinFlowClient.Session!.Page);

            // The box is a linked cell that takes clicks; the readback lists its links.
            var box = _runtime.Row(JoinFlowLayout.MessageRow)!;
            Assert.True(box.Cells().Single(c => c.Key == JoinFlowLayout.CellText).Linkify);
            Assert.NotEqual(0, box.WiredIdOf(JoinFlowLayout.CellText));
            // A click the fake cannot hit-test (no link under the pointer) opens nothing.
            Assert.True(_runtime.Click(JoinFlowLayout.MessageRow, JoinFlowLayout.CellText));
            Assert.Equal(false, ((Dictionary<string, object?>)JoinFlowPanel.DescribeDrawn()["linkDialog"]!)["open"]);
            var drawn = (Dictionary<string, object?>)JoinFlowTestBridge.Read()["drawn"]!;
            var links = (List<object?>)drawn["links"]!;
            Assert.Equal(2, links.Count);
            Assert.Equal("https://discord.gg/nightgarden", ((Dictionary<string, object?>)links[0]!)["url"]);
            Assert.Equal("discord.gg/nightgarden", ((Dictionary<string, object?>)links[0]!)["text"]);

            // link_click 0: the confirmation, with the exact URL on its own line.
            var clicked = JoinFlowTestBridge.LinkClick(0);
            Assert.Equal("link dialog", clicked["result"]);
            Assert.Contains((JoinLinkText.Title, true), _runtime.DialogTexts);
            Assert.Contains((UiRichText.Literal(JoinLinkText.Body("Night Garden")), false), _runtime.DialogTexts);
            Assert.Equal("https://discord.gg/nightgarden",
                _runtime.Row(JoinLinkDialog.UrlRow)!.Cells().Single(c => c.Key == JoinFlowLayout.CellText).Text);
            Assert.Equal(new[] { "Cancel", "Copy link", "Open in browser" },
                _runtime.Row(JoinLinkDialog.ButtonsRow)!.Cells().Where(c => c.Kind == CellKind.Button).Select(c => c.Text)
                    .OrderBy(t => t, StringComparer.Ordinal));

            // Copy link: the clipboard gets the normalized URL; the dialog stays with "Copied.".
            Assert.True(_runtime.Click(JoinLinkDialog.ButtonsRow, JoinLinkDialog.ButtonCopy));
            Assert.Equal("https://discord.gg/nightgarden", copied);
            Assert.Equal(JoinLinkText.Copied, _runtime.Row(JoinLinkDialog.StatusRow)!.Cells().Single().Text);

            // Escape cancels: back on the message page, nothing opened.
            UiApi.PressKeyForTests(27, true);
            UiApi.PressKeyForTests(17, true);
            Assert.Equal(false, ((Dictionary<string, object?>)JoinFlowPanel.DescribeDrawn()["linkDialog"]!)["open"]);
            Assert.Equal(JoinPage.Message, JoinFlowClient.Session!.Page);
            Assert.Null(opened);

            // Open in browser hands the normalized https URL to the opener; a dry run records it instead.
            JoinFlowTestBridge.LinkDryRun(true);
            JoinFlowTestBridge.LinkClick(1);
            Assert.Equal("dry-run: https://example.com/map", JoinFlowTestBridge.LinkButton("open")["result"]);
            Assert.Null(opened);
            Assert.Equal("https://example.com/map", JoinLinkOpener.LastUrl);
            JoinFlowTestBridge.LinkDryRun(false);
            JoinFlowTestBridge.LinkClick(0);
            Assert.True(_runtime.Click(JoinLinkDialog.ButtonsRow, JoinLinkDialog.ButtonOpen));
            Assert.Equal("https://discord.gg/nightgarden", opened);
            Assert.Equal(JoinPage.Message, JoinFlowClient.Session!.Page);
            Assert.StartsWith("no link 5", JoinFlowPanel.OpenLink(5), StringComparison.Ordinal);
            Assert.Equal("no link dialog is open", JoinFlowPanel.LinkButton("copy"));
        }
        finally
        {
            JoinFlowPanel.CopyToClipboard = copy;
            JoinFlowPanel.OpenUrl = open;
            JoinLinkOpener.ResetForTests();
        }
    }

    [Fact]
    public void The_mods_page_draws_required_on_a_red_band_and_recommended_on_a_green_one()
    {
        ModManifestDeny.TestServerAsked = new[] { Must(Sample, "0.1.0") };
        ModManifestDeny.TestProvided = Array.Empty<Handshake.ModRecord>();
        ModManifestDeny.OnJoinDenied("Mod mismatch", "127.0.0.1:9976");
        ModManifest.AcceptEnvelope(Descriptor("Hello"));
        Assert.True(_runtime.Click(JoinFlowLayout.ButtonsRow, JoinFlowLayout.ButtonContinue));
        Assert.Equal(JoinPage.Mods, JoinFlowClient.Session!.Page);

        var head = _runtime.Row(JoinFlowLayout.RequiredHeadRow)!;
        Assert.Equal("REQUIRED TO JOIN", head.Cells().Single(c => c.Key == JoinFlowLayout.CellText).Text);
        var required = (CellRowSpec)typeof(CellRowTests.FakeCellRow).GetField("_spec",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(_runtime.Row(JoinFlowLayout.RequiredRow(Sample)))!;
        Assert.Equal(CellSection.Red, required.Section);
        Assert.Equal(CellSectionEdge.Last, required.SectionEdge);
        var rec = (CellRowSpec)typeof(CellRowTests.FakeCellRow).GetField("_spec",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(_runtime.Row(JoinFlowLayout.RecommendedRow("maps.mod")))!;
        Assert.Equal(CellSection.Green, rec.Section);

        var sections = (Dictionary<string, object?>)JoinFlowPanel.DescribeDrawn()["sections"]!;
        Assert.Equal("red", sections["required"]);
        Assert.Equal("green", sections["recommended"]);
        // The row keeps its words and its button.
        Assert.Equal("Install", _runtime.Row(JoinFlowLayout.RequiredRow(Sample))!.Cells().Single(c => c.Key == JoinFlowLayout.CellAction).Text);
    }

    private void OpenThunderstoreConfirmation()
    {
        ModManifestDeny.TestServerAsked = new[] { Must(Sample, "0.1.0") };
        ModManifestDeny.TestProvided = Array.Empty<Handshake.ModRecord>();
        ModManifestDeny.OnJoinDenied("Mod mismatch: progenitor.sample: expected 0.1.0, got missing", "127.0.0.1:9976");
        ModManifest.AcceptEnvelope(Descriptor("Welcome!"));
        if (JoinFlowClient.Session!.Page == JoinPage.Message)
            Assert.True(_runtime.Click(JoinFlowLayout.ButtonsRow, JoinFlowLayout.ButtonContinue));
        Assert.True(_runtime.Click(JoinFlowLayout.RequiredRow(Sample), JoinFlowLayout.CellAction));
        Assert.Equal(JoinPage.ThunderstoreTrust, JoinFlowClient.Session!.Page);
    }

    /// <summary>
    /// Requested 2026-10-05: the red risk band is gone from "Install from Thunderstore?" (the title already says where
    /// the mods come from); with the pinned key there is no other line between the facts and the buttons.
    /// </summary>
    [Fact]
    public void The_thunderstore_confirmation_has_no_risk_band_and_no_key_line_for_the_pinned_key()
    {
        OpenThunderstoreConfirmation();

        Assert.Null(_runtime.Row("join-ts-risk"));
        Assert.Null(_runtime.Row(JoinFlowLayout.TsKeyChangedRow));
        Assert.Null(_runtime.Row(JoinFlowLayout.TsWarningRow));
        Assert.Equal(new[] { JoinText.BackButton, JoinText.TsInstallButton }, Buttons());
        var ts = (Dictionary<string, object?>)JoinFlowTestBridge.Read()["tsTrust"]!;
        Assert.False(ts.ContainsKey("risk"));
        Assert.Equal("", ts["keyChanged"]);
    }

    /// <summary>A catalog signed by another key than the one pinned for this address names the old key, as a plain line.</summary>
    [Fact]
    public void A_changed_key_is_named_on_the_thunderstore_confirmation()
    {
        ModManifestTrust.WriteAtomic(Path.Combine(_dir, ModManifest.TrustedFileName), new TrustedOperatorsFile
        {
            Operators = new[]
            {
                new TrustedOperator { Host = "127.0.0.1:9976", Fp = "deadbeefdeadbeef", Spki = "old-key", Always = true, Seen = "2026-10-04T12:00:00Z" },
            },
        });

        OpenThunderstoreConfirmation();

        var line = _runtime.Row(JoinFlowLayout.TsKeyChangedRow)!.Cells().Single();
        Assert.Equal("This server's key changed since Oct 4, 2026; it was deadbeefdeadbeef.", line.Text);
        Assert.Equal(CellTone.Normal, line.Tone);
        Assert.Null(_runtime.Row("join-ts-risk"));
        var ts = (Dictionary<string, object?>)JoinFlowTestBridge.Read()["tsTrust"]!;
        Assert.Equal(line.Text, ts["keyChanged"]);
        Assert.Equal("", JoinText.KeyChangedLine("", "Oct 4, 2026"));
        Assert.Equal("This server's key changed; it was ab12.", JoinText.KeyChangedLine("ab12", ""));
    }

    private IReadOnlyList<string> Buttons() =>
        _runtime.Row(JoinFlowLayout.ButtonsRow)!.Cells().Where(c => c.Kind == CellKind.Button).Select(c => c.Text).ToList();

    private sealed class Runtime : CellRowTests.PlainRuntime, IUiRuntime
    {
        private readonly Dictionary<string, CellRowTests.FakeCellRow> _rows = new(StringComparer.Ordinal);
        public readonly List<(string, bool)> DialogTexts = new();

        public override IRuntimeCellRow? AddCellRow(IRuntimePanel panel, CellRowSpec spec, bool header)
        {
            var row = new CellRowTests.FakeCellRow(null, spec, header);
            _rows[spec.Key] = row;
            return row;
        }

        public CellRowTests.FakeCellRow? Row(string key) => _rows.TryGetValue(key, out var r) ? r : null;

        public IRuntimeWidget? AddDialogText(IRuntimePanel panel, string text, bool title)
        {
            DialogTexts.Add((text, title));
            return new CellRowTests.FakeWidget { Text = text };
        }

        public bool Click(string rowKey, string cellKey)
        {
            if (!_rows.TryGetValue(rowKey, out var row)) return false;
            ushort id = row.WiredIdOf(cellKey);
            if (id == 0) return false;
            UiSlotTable.BootstrapDispatch(id, (byte)UiSlotKind.Click, 0f, false, null);
            return true;
        }
    }
}
