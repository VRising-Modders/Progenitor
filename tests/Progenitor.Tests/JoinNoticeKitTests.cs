using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Progenitor.Networking;
using Progenitor.Ui;
using Xunit;
using UiApi = Progenitor.Ui.Ui;

namespace Progenitor.Tests;

/// <summary>
/// Requested 2026-10-05: the join flow's notices look like the game's own popups. The per-server pause notice and the
/// mid-session change notice are drawn with the dialog kit (the chrome, a title, the body, the paused mods one per line,
/// vanilla popup buttons), the pause notice lists each mod with its readable name and why, with correct plurals and a cap,
/// and a guard keeps every join-flow notice on the kit. Game-free: nothing here has been observed in the game.
/// </summary>
[Collection("ModManifest")]
public sealed class JoinNoticeKitTests : IDisposable
{
    private readonly Runtime _runtime = new();

    public JoinNoticeKitTests()
    {
        Plugin.Logger = new TestLogSource();
        UiApi.ResetForTests();
        UiApi.SetRuntimeForTests(_runtime);
    }

    public void Dispose()
    {
        ServerModPauseNotice.Close();
        UiApi.ResetForTests();
        Plugin.Logger = null;
    }

    private sealed class Runtime : CellRowTests.PlainRuntime, IUiRuntime
    {
        public int Chromed;
        public readonly List<(string Text, bool Title)> DialogTexts = new();
        public readonly List<CellRowSpec> Rows = new();

        public override void DialogChrome(IRuntimePanel panel) => Chromed++;

        public override IRuntimeCellRow? AddCellRow(IRuntimePanel panel, CellRowSpec spec, bool header)
        {
            Rows.Add(spec);
            return new CellRowTests.FakeCellRow(null, spec, header);
        }

        public IRuntimeWidget? AddDialogText(IRuntimePanel panel, string text, bool title)
        {
            DialogTexts.Add((text, title));
            return new CellRowTests.FakeWidget { Text = text };
        }
    }

    [Fact]
    public void The_pause_notice_lists_each_mod_with_its_reason_and_counts_with_plurals()
    {
        var one = ServerModPauseText.Paused(new[] { ("RotorCamera", ServerModPauseText.ReasonNotAllowed) }, "Night Garden");
        Assert.Equal("Mods paused for this server", one.Title);
        Assert.Equal("Paused 1 mod for this server. It's off while you're on Night Garden and comes back when you leave:", one.Body);
        Assert.Equal(new[] { "RotorCamera · Not allowed on this server" }, one.Lines);

        var three = ServerModPauseText.Paused(new[]
        {
            ("RotorCamera", ServerModPauseText.ReasonNotAllowed),
            ("Hot Demo", ServerModPauseText.ReasonServerLacks),
            ("Sample", ServerModPauseText.ReasonServerLacks),
        }, "");
        Assert.Equal("Paused 3 mods for this server. They're off while you're on this server and come back when you leave:", three.Body);
        Assert.Equal(new[]
        {
            "RotorCamera · Not allowed on this server", "Hot Demo · This server doesn't run it", "Sample · This server doesn't run it",
        }, three.Lines);
        Assert.Equal("Paused 3 mods for this server: RotorCamera (Not allowed on this server), Hot Demo (This server doesn't run it), "
            + "Sample (This server doesn't run it). They come back when you leave.", three.Text);

        var many = ServerModPauseText.Paused(Enumerable.Range(1, 11).Select(i => ("Mod " + i, ServerModPauseText.ReasonNotAllowed)).ToList(), "X");
        Assert.Equal(ServerModPauseText.MaxListed + 1, many.Lines.Count);
        Assert.Equal("and 3 more", many.Lines[^1]);
        Assert.StartsWith("Paused 11 mods", many.Body);

        var withNote = ServerModPauseText.Paused(new[] { ("A", ServerModPauseText.ReasonNoProgenitor) }, "", new[] { "plain.mod" });
        Assert.Equal("Still running because they were loaded at game start: plain.mod; restart V Rising without them to turn them off.", withNote.Note);
    }

    [Fact]
    public void The_pause_notice_is_drawn_with_the_dialog_kit()
    {
        var content = ServerModPauseText.Paused(new[] { ("Retro<b>Camera</b>", ServerModPauseText.ReasonNotAllowed) }, "Night Garden");
        ServerModPauseNotice.Open(content);
        Assert.True(ServerModPauseNotice.IsOpen);
        Assert.Equal(1, _runtime.Chromed);
        Assert.Contains(("Mods paused for this server", true), _runtime.DialogTexts);
        Assert.Contains((content.Body, false), _runtime.DialogTexts);
        // The vanilla popup button row, OK as its main cell; no plain full-width button.
        var buttons = _runtime.Rows.Single(r => r.Key == ServerModPauseNotice.ButtonsRow);
        var ok = buttons.Cells.Single(c => c.Key == UiDialogs.CellMain);
        Assert.Equal("OK", ok.Text);
        Assert.Equal(CellSkin.PopupButton, ok.Skin);
        Assert.Same(content, ServerModPauseNotice.Shown);
        ServerModPauseNotice.Close();
        Assert.False(ServerModPauseNotice.IsOpen);
    }

    /// <summary>NOTICEOPEN1: an open button is the main cell with OK beside it; its press closes the notice, then opens.</summary>
    [Fact]
    public void The_notice_s_open_button_closes_it_and_opens_the_tab()
    {
        var item = new RecheckItem { Key = "progenitor.hotdemo", Name = "Hot Demo", Kind = ClientModProblem.KindMissing, Local = RecheckLocalState.NotInstalled };
        var content = ClientPolicyNoticeText.Compose(RecheckNoticeKind.Warn, new[] { item }, 0, "Night Garden");
        var calls = new List<string>();
        try
        {
            Assert.Equal("no notice", ServerModPauseNotice.PressOpen());
            // No handler (an older host): plain OK only.
            ServerModPauseNotice.Open(content);
            Assert.Equal("OK", _runtime.Rows.Last(r => r.Key == ServerModPauseNotice.ButtonsRow).Cells.Single(c => c.Key == UiDialogs.CellMain).Text);
            ServerModPauseNotice.OpenRequested = (kind, key, name) => calls.Add(kind + ":" + key + ":" + name);
            ServerModPauseNotice.Open(content);
            var buttons = _runtime.Rows.Last(r => r.Key == ServerModPauseNotice.ButtonsRow);
            Assert.Equal("Open Install Mods", buttons.Cells.Single(c => c.Key == UiDialogs.CellMain).Text);
            Assert.Equal("OK", buttons.Cells.Single(c => c.Key == UiDialogs.CellCancel).Text);
            Assert.Equal("opened install", ServerModPauseNotice.PressOpen());
            Assert.Equal(new[] { "install:progenitor.hotdemo:Hot Demo" }, calls);
            Assert.False(ServerModPauseNotice.IsOpen);

            // E1 (live 2026-10-07): the notice had already closed by itself; its fix is still there.
            ClientPolicyNoticeClient.LocalStateProbe = _ => RecheckLocalState.NotInstalled;
            var notice = new ClientPolicyNoticeWire.Notice { Kind = RecheckNoticeKind.Warn };
            notice.Items.Add(new RecheckItem { Key = "progenitor.hotdemo", Name = "Hot Demo", Kind = ClientModProblem.KindMissing });
            ClientPolicyNoticeClient.Handle(notice);
            ServerModPauseNotice.Close();
            Assert.Equal("opened install (the notice had closed)", ServerModPauseNotice.PressOpen());
            Assert.Equal(2, calls.Count);
        }
        finally
        {
            ServerModPauseNotice.OpenRequested = null;
            ClientPolicyNoticeClient.ResetProbes();
        }
    }

    [Fact]
    public void Mod_names_on_the_notice_are_escaped()
    {
        var content = JoinNoticeDialog.Content("T", "B", new[] { "Evil<size=99>Mod · Not allowed on this server" }, "", "OK", null, "k");
        Assert.DoesNotContain("<size", content.Lines[0].Replace("<noparse><</noparse>", ""));
    }

    [Fact]
    public void The_change_notice_is_drawn_with_the_dialog_kit()
    {
        var state = new JoinChangeNoticeState { Kind = "mods", Text = "This server's mods changed.", ViewCaption = "View mods" };
        JoinChangeNoticePanel.Open(state);
        Assert.True(JoinChangeNoticePanel.IsOpen);
        Assert.Equal(1, _runtime.Chromed);
        Assert.Contains(("This server's mods changed.", false), _runtime.DialogTexts);
        var buttons = _runtime.Rows.Single(r => r.Key == JoinChangeNoticePanel.ButtonsRow);
        Assert.Equal("View mods", buttons.Cells.Single(c => c.Key == UiDialogs.CellMain).Text);
        Assert.Equal(JoinChangeNoticePolicy.CloseButton, buttons.Cells.Single(c => c.Key == UiDialogs.CellCancel).Text);
        JoinChangeNoticePanel.Uninstall();
        Assert.False(JoinChangeNoticePanel.IsOpen);
    }

    /// <summary>
    /// The guard: every join-flow and per-server pause notice goes through the kit (<see cref="JoinNoticeDialog"/> or
    /// <see cref="UiDialogView"/>), never the old flat panel calls (<c>panel.Label</c> / <c>panel.Button</c>).
    /// </summary>
    [Fact]
    public void Every_join_flow_notice_goes_through_the_dialog_kit()
    {
        string dir = Path.Combine(CastlesTests.RepoRootForDumpSources(), "src", "Progenitor.Core", "Networking");
        var notices = Directory.GetFiles(dir, "*Notice*.cs")
            .Where(f => Path.GetFileName(f).EndsWith("Panel.cs", StringComparison.Ordinal) || Path.GetFileName(f) == "ServerModPauseNotice.cs")
            .ToList();
        Assert.Contains(notices, f => Path.GetFileName(f) == "ServerModPauseNotice.cs");
        Assert.Contains(notices, f => Path.GetFileName(f) == "JoinChangeNoticePanel.cs");
        foreach (string file in notices)
        {
            string src = File.ReadAllText(file);
            Assert.True(src.Contains("JoinNoticeDialog.Build", StringComparison.Ordinal) || src.Contains("UiDialogView.Build", StringComparison.Ordinal),
                Path.GetFileName(file) + " does not draw with the dialog kit");
            Assert.DoesNotContain("panel.Label(", src);
            Assert.DoesNotContain("panel.Button(", src);
        }
    }
}
