using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Progenitor.Networking;
using Progenitor.Ui;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// JOINDRAFT1 (requested 2026-10-07, "This should have an apply button at the bottom instead of changes being instant.
/// Show the changes."): the Players joining panel stages every edit in a draft, lists the changes in plain words, and
/// Apply sends them as the existing requests in order, keeping what the server refused.
/// </summary>
public sealed class JoinAdminDraftTests
{
    private static JoinAdminState Server()
    {
        var s = new JoinAdminState { WindowSeconds = 60, PolicySupported = true, PolicyMode = "any", Unverified = "allow", MidSession = "warn", ApprovedTotal = 1 };
        s.Recommended.Add(new JoinAdminRecRow { Id = "author.mapmarkers", Version = "2.0.0", Reason = "Nice map", Name = "Map Markers" });
        s.RequiredClient.Add(new JoinAdminClientRow { Id = "author.basinhovertips", Version = "1.0.19", Rule = "at-least", Reason = "Shows blood", Name = "BasinHoverTips" });
        s.Approved.Add(new JoinAdminClientRow { Id = "author.minimap", Rule = "any", Name = "Minimap" });
        return s;
    }

    private static JoinAdminDraft Draft(JoinAdminState? s = null)
    {
        var d = new JoinAdminDraft();
        d.Reset(s ?? Server());
        return d;
    }

    [Fact]
    public void Edits_are_staged_with_markers_and_returning_to_the_server_value_removes_the_change()
    {
        var d = Draft();
        Assert.False(d.HasChanges);
        Assert.Null(d.Require("progenitor.hotdemo", "", "0.1.0", "at-least", "We <3 hot demo", "Progenitor Hot Demo"));
        Assert.Null(d.SetRequiredRule("author.basinhovertips", "exact", ""));
        Assert.Null(d.SetPolicyMode("approved"));
        Assert.Null(d.Unrecommend("author.mapmarkers"));
        Assert.Equal(4, d.Count);

        var req = d.RequiredRows();
        Assert.Equal(JoinAdminRowMark.Changed, req[0].Mark);
        Assert.Equal("exact", req[0].Row.Rule);
        Assert.Equal(JoinAdminRowMark.Added, req[1].Mark);
        Assert.Equal(JoinAdminRowMark.Removed, d.RecommendedRows().Single().Mark);
        Assert.Equal("approved", d.Word(JoinAdminChangeKind.PolicyMode));
        // The server's state is untouched until Apply.
        Assert.Equal("any", d.Base!.PolicyMode);
        Assert.Equal("at-least", d.Base.RequiredClient[0].Rule);

        // Back to the server's value: the change goes. A typed reason equal to the stored one is no change.
        Assert.Null(d.SetRequiredRule("author.basinhovertips", "at-least", ""));
        Assert.Null(d.SetPolicyMode("any"));
        Assert.Null(d.SetRequiredReason("author.basinhovertips", "Shows blood "));
        Assert.Equal(2, d.Count);
        Assert.Null(d.SetRequiredReason("author.basinhovertips", "Shows blood pools"));
        Assert.Equal(JoinAdminRowMark.Changed, d.RequiredRows()[0].Mark);
        Assert.Null(d.SetRequiredReason("author.basinhovertips", "Shows blood"));
        Assert.Equal(JoinAdminRowMark.None, d.RequiredRows()[0].Mark);

        // A staged add's reason is part of the add; removing a staged add drops it; recommending a removed row undoes it.
        Assert.Null(d.SetRequiredReason("progenitor.hotdemo", "Hot demo!"));
        Assert.Equal("Hot demo!", d.Changes.Single(c => c.Kind == JoinAdminChangeKind.RequiredAdd).Reason);
        Assert.Null(d.Unrequire("progenitor.hotdemo"));
        Assert.Null(d.Recommend("author.mapmarkers", "", "2.0.0", "", "Map Markers"));
        Assert.False(d.HasChanges);
    }

    [Fact]
    public void Staging_refuses_what_the_server_would_and_honors_the_lock_list()
    {
        var d = Draft();
        Assert.Equal("That mod is already recommended.", d.Recommend("author.mapmarkers", "", "", "", ""));
        Assert.Equal("This server requires that mod, so it is not a recommendation.", d.Recommend("author.basinhovertips", "", "", "", ""));
        Assert.Equal("That mod is already required.", d.Require("author.basinhovertips", "", "", "", "", ""));
        Assert.Equal("That mod is already approved.", d.Approve("author.minimap", "", "", "", ""));
        Assert.Null(d.Approve("author.zoom", "", "", "", "Zoom"));
        Assert.Equal(JoinAdminDraft.AlreadyStaged, d.Approve("author.zoom", "", "", "", ""));
        Assert.NotNull(d.SetRecommendedReason("author.mapmarkers", new string('x', JoinAdminLimits.MaxReasonChars + 1)));
        Assert.Equal("Choose any or approved.", d.SetPolicyMode("most"));

        // A removed row's reason can't be edited until its removal is undone.
        Assert.Null(d.Unrecommend("author.mapmarkers"));
        Assert.Equal(JoinAdminDraft.UndoRemovalFirst, d.SetRecommendedReason("author.mapmarkers", "new"));
        Assert.True(d.UndoRemoval(JoinAdminChangeKind.RecommendRemove, "author.mapmarkers"));

        // The operator's lock list: a locked list stages nothing.
        var locked = Draft();
        locked.LockedBy = op => op == JoinAdminOps.PolicyMode ? "Locked by the server operator (policy-mode)." : null;
        Assert.Equal("Locked by the server operator (policy-mode).", locked.SetPolicyMode("approved"));
        Assert.Null(locked.SetMidSession("disconnect"));
        Assert.Single(locked.Changes);

        // No state (not an admin, or not answered yet): nothing can be staged.
        Assert.NotNull(new JoinAdminDraft().SetPolicyMode("approved"));
    }

    [Fact]
    public void The_changes_list_says_each_change_in_plain_words()
    {
        var d = Draft();
        d.Require("progenitor.hotdemo", "", "0.1.0", "", "We <3 hot demo", "Progenitor Hot Demo");
        d.SetRequiredRule("author.basinhovertips", "exact", "");
        d.SetPolicyMode("approved");
        d.Unrequire("progenitor.hotdemo");
        d.Require("progenitor.hotdemo", "", "0.1.0", "", "We <3 hot demo", "Progenitor Hot Demo");
        d.Unrecommend("author.mapmarkers");
        d.Approve("author.zoom", "", "", "", "Zoom");
        d.Unapprove("author.minimap");
        d.SetUnverified("refuse");
        d.SetMidSession("disconnect");
        d.SetWindow(120);
        var lines = d.Lines();
        Assert.Equal(new[]
        {
            "BasinHoverTips: 1.0.19 or newer → exact 1.0.19",
            "Mod rules: Any mods → Only approved mods",
            "Require Progenitor Hot Demo (0.1.0 or newer), reason 'We <3 hot demo'",
            "Stop recommending Map Markers",
            "Approve Zoom",
            "Remove Minimap from the approved list",
            "Mods can't be checked: Let them join → Refuse them",
            "Mods break the rules while playing: Pause the mod and warn them → Disconnect them",
            "Install window: 60 → 120 seconds",
        }, lines);

        var r = Draft();
        r.SetRecommendedReason("author.mapmarkers", "Shows markers");
        r.Unrequire("author.basinhovertips");
        r.Recommend("author.zoom", "", "1.2.0", "", "Zoom");
        Assert.Equal(new[] { "Map Markers: reason 'Nice map' → 'Shows markers'", "Unrequire BasinHoverTips", "Recommend Zoom (1.2.0)" }, r.Lines());

        // A required add of a recommended mod moves it (the server's rule), and says so.
        var move = Draft();
        Assert.Null(move.Require("author.mapmarkers", "", "2.0.0", "", "", "Map Markers"));
        Assert.Equal("Require Map Markers (2.0.0 or newer); moves it from Recommended", move.Lines().Single());
        Assert.Equal(JoinAdminRowMark.Removed, move.RecommendedRows().Single().Mark);

        // A refusal and a conflict follow the line.
        var c = new JoinAdminChange { Kind = JoinAdminChangeKind.ApproveAdd, Id = "a.b", Error = "At most 128 client mods can be approved." };
        Assert.Equal("Approve a.b (not applied: At most 128 client mods can be approved)", JoinAdminDraft.LineOf(c));
        Assert.Equal(CellTone.Orange, JoinAdminRowLayout.ChangeTone(c));
        Assert.Equal(JoinAdminDraft.NoChanges, "No changes");
        Assert.True(d.Undo(0));
        Assert.Equal("Mod rules: Any mods → Only approved mods", d.Lines()[0]);
        Assert.False(d.Undo(99));
    }

    [Fact]
    public void Apply_sends_removals_adds_edits_approvals_choices_then_the_window_and_bulks_approvals()
    {
        var d = Draft();
        d.SetWindow(90);
        d.SetPolicyMode("approved");
        d.Approve("author.zoom", "", "", "", "Zoom");
        d.Approve("author.fly", "", "", "", "Fly");
        d.SetRequiredReason("author.basinhovertips", "New reason");
        d.Recommend("author.zoom2", "", "1.0.0", "Try it", "Zoom 2");
        d.Unrecommend("author.mapmarkers");
        var ops = d.Steps().Select(s => s.Request.Op).ToArray();
        Assert.Equal(new[]
        {
            JoinAdminOps.RecommendRemove, JoinAdminOps.RecommendAdd, JoinAdminOps.RequiredReason, JoinAdminOps.ApproveBulk,
            JoinAdminOps.PolicyMode, JoinAdminOps.Window,
        }, ops);
        var bulk = d.Steps().Single(s => s.Bulk);
        Assert.Equal(2, bulk.Changes.Count);
        Assert.Equal(new[] { "author.zoom", "author.fly" }, bulk.Request.Items.Select(i => i.Id).ToArray());
        Assert.Equal(90, d.Steps().Last().Request.Seconds);
        Assert.Equal("Try it", d.Steps()[1].Request.Reason);

        // One plain approval is an ordinary approve-add; one with a rule is never bulked.
        var one = Draft();
        one.Approve("author.zoom", "", "", "", "Zoom");
        one.Approve("author.fly", "", "1.0.0", "exact", "Fly");
        Assert.Equal(new[] { JoinAdminOps.ApproveAdd, JoinAdminOps.ApproveAdd }, one.Steps().Select(s => s.Request.Op).ToArray());
    }

    /// <summary>A fake channel: records every request and answers it from <paramref name="decide"/> (null: no answer).</summary>
    private sealed class FakeServer
    {
        public readonly List<JoinAdminRequest> Sent = new();
        public readonly Queue<(JoinAdminRequest Request, Action<JoinAdminResponse?> Answer)> Waiting = new();
        public Func<JoinAdminRequest, JoinAdminResponse?> Decide = r => new JoinAdminResponse { Op = r.Op, Ok = true };
        public bool Immediate = true;

        public string? Send(JoinAdminRequest request, Action<JoinAdminResponse?> answer)
        {
            Sent.Add(request);
            if (Immediate) answer(Decide(request));
            else Waiting.Enqueue((request, answer));
            return null;
        }

        public void AnswerNext()
        {
            var (request, answer) = Waiting.Dequeue();
            answer(Decide(request));
        }
    }

    [Fact]
    public void Apply_keeps_refused_changes_with_the_reason_and_refreshes_afterwards()
    {
        var d = Draft();
        d.Unrecommend("author.mapmarkers");
        d.Require("progenitor.hotdemo", "", "0.1.0", "", "We <3 hot demo", "Progenitor Hot Demo");
        d.SetPolicyMode("approved");
        var server = new FakeServer
        {
            Decide = r => r.Op == JoinAdminOps.RequiredAdd
                ? new JoinAdminResponse { Op = r.Op, Ok = false, Error = "At most 16 client mods can be required." }
                : new JoinAdminResponse { Op = r.Op, Ok = true },
            Immediate = false,
        };
        JoinAdminApplyRun? done = null;
        var run = new JoinAdminApplyRun(d, server.Send, r => done = r);
        run.Start();
        // One request at a time: the next goes only after the previous answer.
        Assert.Single(server.Sent);
        Assert.True(run.Running);
        server.AnswerNext();
        Assert.Equal(2, server.Sent.Count);
        server.AnswerNext();
        server.AnswerNext();
        Assert.Equal(JoinAdminOps.Get, server.Sent.Last().Op);
        Assert.Same(run, done);
        Assert.False(run.Running);
        Assert.Equal(2, run.Applied);
        Assert.Equal(1, run.Failed);
        var kept = d.Changes.Single();
        Assert.Equal(JoinAdminChangeKind.RequiredAdd, kept.Kind);
        Assert.Equal("At most 16 client mods can be required.", kept.Error);
        Assert.Contains("(not applied: At most 16 client mods can be required)", d.Lines().Single(), StringComparison.Ordinal);
        Assert.Equal("Applied 2 of 3; 1 change was not applied and stays below.", run.Summary());
        Assert.Contains("refused: Require Progenitor Hot Demo (0.1.0 or newer), reason 'We <3 hot demo': At most 16 client mods can be required.", run.Results);

        // All accepted: the draft is empty.
        var all = Draft();
        all.SetWindow(30);
        var ok = new JoinAdminApplyRun(all, new FakeServer().Send);
        ok.Start();
        Assert.False(all.HasChanges);
        Assert.Equal("Applied 1 change. Players see it on their next join.", ok.Summary());
    }

    [Fact]
    public void Apply_stops_when_the_server_stops_answering_and_keeps_the_rest()
    {
        var d = Draft();
        d.Unrecommend("author.mapmarkers");
        d.SetPolicyMode("approved");
        d.SetWindow(10);
        var server = new FakeServer { Decide = _ => null };
        var run = new JoinAdminApplyRun(d, server.Send);
        run.Start();
        Assert.Equal(new[] { JoinAdminOps.RecommendRemove, JoinAdminOps.Get }, server.Sent.Select(r => r.Op).ToArray());
        Assert.Equal(3, d.Count);
        Assert.Equal(JoinAdminApplyRun.NoAnswer, d.Changes[0].Error);
        Assert.StartsWith("not sent: ", d.Changes[1].Error, StringComparison.Ordinal);

        // A send the channel refuses (the client's own limit) fails that change and goes on.
        var e = Draft();
        e.SetPolicyMode("approved");
        e.SetWindow(10);
        int calls = 0;
        var refusing = new JoinAdminApplyRun(e, (r, answer) =>
        {
            calls++;
            if (r.Op == JoinAdminOps.PolicyMode) return "Too many requests are waiting for the server.";
            answer(new JoinAdminResponse { Op = r.Op, Ok = true });
            return null;
        });
        refusing.Start();
        Assert.Equal(3, calls);
        Assert.Equal("Too many requests are waiting for the server.", e.Changes.Single().Error);
    }

    [Fact]
    public void A_bulk_approval_keeps_the_rows_the_server_did_not_add()
    {
        var d = Draft();
        d.Approve("author.zoom", "", "", "", "Zoom");
        d.Approve("author.fly", "", "", "", "Fly");
        var server = new FakeServer
        {
            Decide = r =>
            {
                var s = Server();
                s.Approved.Add(new JoinAdminClientRow { Id = "author.zoom", Rule = "any" });
                s.ApprovedTotal = 2;
                return new JoinAdminResponse { Op = r.Op, Ok = true, Note = "Approved 1 mod. 1 not added: at most 128 can be approved.", State = s };
            },
        };
        var run = new JoinAdminApplyRun(d, server.Send);
        run.Start();
        Assert.Equal("author.fly", d.Changes.Single().Id);
        Assert.Contains("1 not added", d.Changes.Single().Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Rebase_drops_what_the_server_has_and_flags_another_admins_changes()
    {
        var d = Draft();
        d.SetPolicyMode("approved");
        d.SetWindow(120);
        d.SetRecommendedReason("author.mapmarkers", "Shows markers");
        d.Approve("author.zoom", "", "", "", "Zoom");
        d.Recommend("author.fly", "", "1.0.0", "Fly around", "Fly");
        d.Unrequire("author.basinhovertips");

        var moved = Server();
        moved.PolicyMode = "approved";                      // another admin did the same: dropped
        moved.WindowSeconds = 30;                           // another admin changed it: conflict, ours kept
        moved.Recommended[0].Reason = "Great map";          // conflict on the reason
        moved.Approved.Add(new JoinAdminClientRow { Id = "author.zoom", Rule = "any" }); // approved already: dropped
        moved.ApprovedTotal = 2;
        moved.Recommended.Add(new JoinAdminRecRow { Id = "author.fly", Version = "1.0.0", Reason = "Other reason" }); // added too
        moved.RequiredClient.Clear();                       // removed already: dropped
        d.Rebase(moved);

        var kinds = d.Changes.Select(c => c.Kind).ToArray();
        Assert.Equal(new[] { JoinAdminChangeKind.Window, JoinAdminChangeKind.RecommendReason, JoinAdminChangeKind.RecommendReason }, kinds);
        Assert.Equal("Install window: 30 → 120 seconds (another admin changed it to 30 seconds)", d.Lines()[0]);
        Assert.Equal("Map Markers: reason 'Great map' → 'Shows markers' (another admin changed the reason to 'Great map')", d.Lines()[1]);
        Assert.Contains("another admin recommended it too", d.Lines()[2], StringComparison.Ordinal);
        Assert.Equal(CellTone.Amber, JoinAdminRowLayout.ChangeTone(d.Changes[0]));

        // A row another admin removed under an edit: kept, flagged.
        var gone = Draft();
        gone.SetRequiredReason("author.basinhovertips", "x");
        var noReq = Server();
        noReq.RequiredClient.Clear();
        gone.Rebase(noReq);
        Assert.Equal(JoinAdminDraft.ConflictRemoved, gone.Changes.Single().Conflict);

        // No state (no longer an admin): no draft.
        gone.Rebase(null);
        Assert.False(gone.HasChanges);
        Assert.Null(gone.Base);
    }

    [Fact]
    public void Close_asks_with_unapplied_changes_and_the_buttons_are_apply_discard_close()
    {
        Assert.Equal(JoinAdminCloseAction.Close, JoinAdminCloseRule.Decide(hasChanges: false, applying: false, force: false));
        Assert.Equal(JoinAdminCloseAction.Ask, JoinAdminCloseRule.Decide(hasChanges: true, applying: false, force: false));
        Assert.Equal(JoinAdminCloseAction.Wait, JoinAdminCloseRule.Decide(hasChanges: true, applying: true, force: false));
        Assert.Equal(JoinAdminCloseAction.Close, JoinAdminCloseRule.Decide(hasChanges: true, applying: true, force: true));
        Assert.Equal("Apply your 3 changes?", JoinAdminText.CloseBody(3));
        Assert.Equal("Apply your 1 change?", JoinAdminText.CloseBody(1));

        var prompt = DialogLayout.Order(JoinAdminRowLayout.CloseButtons());
        Assert.Equal(new[] { "Cancel", "Discard", "Apply" }, prompt.Select(b => b.Caption).ToArray());
        Assert.Equal(DialogButtonRole.Cancel, prompt[0].Role);
        var main = DialogLayout.Order(JoinAdminRowLayout.MainButtons(canApply: false));
        Assert.Equal(new[] { "Close", "Discard", "Apply" }, main.Select(b => b.Caption).ToArray());
        Assert.Equal(CellTone.Disabled, main[2].Tone);
        Assert.Equal(CellTone.Gold, JoinAdminRowLayout.MainButtons(canApply: true)[2].Tone);

        // Escape is Close on the main page; on the prompt it is Cancel (back to the panel).
        string panel = Source("src/Progenitor.Core/Networking/JoinAdminPanel.cs");
        Assert.Contains("switch (JoinAdminCloseRule.Decide(Draft.HasChanges, Applying, force || !_wanted))", panel);
        Assert.Contains("_page = Page.ConfirmClose;", panel);
        Assert.Contains("row.OnClick(ButtonCancel, () => ShowPage(Page.Main));", panel);
        Assert.Contains("row.OnClick(ButtonApply, () => ApplyAndClose());", panel);
        Assert.Contains("return Close();", panel);
    }

    [Fact]
    public void Rows_show_markers_and_a_removed_row_is_struck_with_undo()
    {
        var removed = JoinAdminRowLayout.Head("ja-req-a", "BasinHoverTips", "1.0.19 or newer", "Or newer", removable: true, locked: false, JoinAdminRowMark.Removed);
        Assert.True(removed.Find(JoinAdminRowLayout.CellName)!.Strike);
        Assert.NotNull(removed.Find(JoinAdminRowLayout.CellUndo));
        Assert.Null(removed.Find(JoinAdminRowLayout.CellRemove));
        Assert.Equal(CellTone.Disabled, removed.Find(JoinAdminRowLayout.CellRule)!.Tone);
        Assert.Equal("Removed", removed.Find(JoinAdminRowLayout.CellMark)!.Text);
        Assert.Equal(CellTone.Danger, removed.Find(JoinAdminRowLayout.CellMark)!.Tone);

        var added = JoinAdminRowLayout.Head("ja-rec-b", "Zoom", "1.0.0", null, removable: true, locked: false, JoinAdminRowMark.Added);
        Assert.Equal("Added", added.Find(JoinAdminRowLayout.CellMark)!.Text);
        Assert.True(added.Find(JoinAdminRowLayout.CellMark)!.Visible);
        Assert.False(JoinAdminRowLayout.Head("ja-rec-c", "Zoom", "", null, true, false).Find(JoinAdminRowLayout.CellMark)!.Visible);
        Assert.Equal("Changed", JoinAdminRowLayout.MarkText(JoinAdminRowMark.Changed));

        // The reason line has no Save: the reason is part of the draft.
        Assert.Null(JoinAdminRowLayout.Reason("ja-rec-b", "x").Find("save"));
        Assert.True(JoinAdminRowLayout.RemovedReason("ja-rec-b", "x").Find(JoinAdminRowLayout.CellReason)!.Strike);

        // Every cell of the marked rows and a Changes line stays inside the narrowest content width.
        float width = DialogLayout.ContentRowWidth(DialogLayout.FrameWidth);
        foreach (var row in new[] { removed, added, JoinAdminRowLayout.ChangeLine(0, "Require Progenitor Hot Demo (0.1.0 or newer)", CellTone.Normal) })
            foreach (var p in MenuPanelLayout.CellColumns(width, row).Where(p => p.Visible))
                Assert.True(p.Right + width * .5f <= width + 0.01f, row.Key + "/" + p.Key);
    }

    /// <summary>
    /// CHANGESFIT1 (live 2026-10-07: with two changes the footer took ~170 px and the scrolling body ~150 px, cutting
    /// "Required client mods" after its heading): the footer is as tall as what it shows, and the body keeps most of the panel.
    /// </summary>
    [Theory]
    [InlineData(1080f)]
    [InlineData(1440f)]
    [InlineData(720f)]
    public void With_two_changes_the_scrolling_body_keeps_most_of_the_panel(float screen)
    {
        float footerBand = PanelFooterLayout.BandFor("Progenitor 2026.10.98");
        // A long panel: the frame is capped at the screen fraction and the body scrolls.
        float frame = DialogLayout.FrameHeight(5000f, footerBand, screen);
        float body = frame - DialogLayout.FrameTopPad - footerBand;
        float pinned = DialogLayout.PinnedBottomHeight(JoinAdminRowLayout.FooterHeights(2));
        float viewport = DialogLayout.ViewportHeight(frame, footerBand + pinned);
        Assert.True(viewport >= 0.6f * body, "viewport " + viewport + " of body " + body + " (footer " + pinned + ")");
        // Two change lines are about 22 px each, not a fixed block.
        Assert.True(pinned - DialogLayout.PinnedBottomHeight(JoinAdminRowLayout.FooterHeights(0)) <= 3 * (JoinAdminRowLayout.ChangeLineHeight + PanelLayout.RowGap) + 0.01f);
        // Clean: one muted "No changes" line, no heading.
        Assert.Equal(3, JoinAdminRowLayout.FooterHeights(0).Count);
        // Capped: five lines and "+N more", however many changes.
        Assert.Equal(JoinAdminRowLayout.FooterHeights(40).Count, JoinAdminRowLayout.FooterHeights(6).Count);
        Assert.Equal(1 + JoinAdminRowLayout.ChangeSlots + 1 + 2, JoinAdminRowLayout.FooterHeights(9).Count);
        Assert.Equal(JoinAdminRowLayout.ChangeLineHeight, JoinAdminRowLayout.ChangeLine(0, "x", CellTone.Normal).Height);
        Assert.Equal(JoinAdminRowLayout.ChangeLineHeight, JoinAdminRowLayout.ChangesHeading("h", 2).Height);

        // The runtime gives hidden pinned rows (unused slots) no room, and the panel hides the heading when clean.
        string runtime = Source("src/Progenitor.Core/Ui/UnityUiRuntime.cs");
        Assert.Contains("if (!RowActive(_rows[i])) continue;", runtime);
        Assert.Contains("if (_dialog && !RowActive(row)) continue;", runtime);
        Assert.Contains("SetRowVisible(_changesHead, n > 0);", Source("src/Progenitor.Core/Networking/JoinAdminPanel.cs"));
    }

    [Fact]
    public void The_bridge_has_apply_discard_and_undo_change()
    {
        string dev = Source("src/Progenitor.Dev/Ops/JoinAdminOps.cs");
        Assert.Contains("\"apply\" => Call(\"Apply\")", dev);
        Assert.Contains("\"discard\" => Call(\"Discard\")", dev);
        Assert.Contains("\"undo_change\" => Call(\"UndoChange\"", dev);
        Assert.Contains("if (action == \"apply\") return WaitForApply(reply);", dev);
        string bridge = Source("src/Progenitor.Core/Networking/JoinAdminTestBridge.cs");
        Assert.Contains("public static Dictionary<string, object?> Apply() => After(JoinAdminPanel.Apply());", bridge);
        Assert.Contains("public static Dictionary<string, object?> UndoChange(int index) => After(JoinAdminPanel.UndoChange(index));", bridge);
        string panel = Source("src/Progenitor.Core/Networking/JoinAdminPanel.cs");
        Assert.Contains("[\"changes\"] = Draft.Describe(),", panel);
        Assert.Contains("d[\"draft\"] = DescribeDraft();", panel);
    }

    private static string Source(string relative) =>
        File.ReadAllText(Path.Combine(CastlesTests.RepoRootForDumpSources(), relative.Replace('/', Path.DirectorySeparatorChar)));
}
