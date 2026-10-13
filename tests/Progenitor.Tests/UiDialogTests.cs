using System;
using System.Collections.Generic;
using Progenitor.Ui;
using Xunit;
using UiApi = Progenitor.Ui.Ui;

namespace Progenitor.Tests;

/// <summary>
/// INSTALLUX2 item 1: the kit's modal confirmation dialog (<see cref="UiDialog"/>). The user, on
/// the INSTALLUX1 build: "it expands like the old behavior asking me if I want to stall like this
/// instead of popping up a dialogue". Every confirmation the Mods workspace asks for is now this
/// block — its own panel, the runtime's dialog chrome, the two right-aligned buttons — so the
/// behavior it promises (one dialog at a time, the caller's own button handles, Escape = No) is
/// pinned here once rather than in each of the two panes that open one.
/// </summary>
public sealed class UiDialogTests : IDisposable
{
    private readonly DialogRuntime _runtime = new();
    private readonly object _owner = new();

    public UiDialogTests()
    {
        UiApi.ResetForTests();
        UiApi.SetRuntimeForTests(_runtime);
    }

    public void Dispose()
    {
        UiDialog.Current?.Close();
        UiApi.ResetForTests();
    }

    private UiDialog Open(
        string heading = "Install HotDemo 0.1.0?",
        string body = "Client and server mod by Progenitor, version 0.1.0.",
        string yes = "Yes, install",
        string no = "No",
        string kind = "install",
        string key = "Progenitor-HotDemo") =>
        UiApi.Dialog(
            heading, body, yes, LabelTone.Ok, no,
            tag: "prompt", kind: kind, key: key, owner: _owner, pluginId: "example.installux2",
            rowKey: "prompt:" + key, yesKey: "promptYes", noKey: "promptNo");

    /// <summary>
    /// Opening a dialog builds a panel of its own — not rows inside the caller's pane — asks the
    /// runtime for the modal chrome, shows it, and becomes the one open dialog. The buttons carry
    /// the caller's row key and cell keys, which is what keeps a bridge client's
    /// <c>prompt:&lt;key&gt;/promptYes</c> handle working now that the box is not in the row.
    ///
    /// <para>What removal would break: drawing the confirmation in the caller's own panel again
    /// (no new panel, no chrome ask), or letting <see cref="UiPromptBlock"/> hard-code its
    /// <c>yes</c>/<c>no</c> cell keys, which silently moves every published click handle.</para>
    /// </summary>
    [Fact]
    public void Dialog_is_its_own_chromed_panel_with_the_callers_button_handles()
    {
        var dialog = Open();

        Assert.True(dialog.IsOpen);
        Assert.Same(dialog, UiDialog.Current);
        Assert.Equal(1, _runtime.PanelsCreated);
        Assert.Equal(1, _runtime.ChromeAsks);
        Assert.Equal(1, _runtime.Shows);
        Assert.Equal("Install HotDemo 0.1.0?", dialog.Prompt.Heading?.Text);
        Assert.Equal("Client and server mod by Progenitor, version 0.1.0.", dialog.Prompt.Body?.Text);
        Assert.Equal("prompt:Progenitor-HotDemo", dialog.Prompt.Buttons?.Key);
        Assert.Equal("promptYes", dialog.Prompt.YesKey);
        Assert.Equal("promptNo", dialog.Prompt.NoKey);
        Assert.Equal(
            new[] { "promptNo", "promptYes" },
            _runtime.CellKeysOf("prompt:Progenitor-HotDemo", CellKind.Button));
    }

    /// <summary>
    /// Yes and No each answer once, close the dialog before the handler runs (so a handler that
    /// opens another dialog cannot be replaced by its own closing), and leave nothing open.
    ///
    /// <para>What removal would break: a decision that leaves the overlay on screen, or a second
    /// click on an already-answered dialog running the handler twice.</para>
    /// </summary>
    [Fact]
    public void Yes_and_No_answer_once_and_close_the_dialog()
    {
        int yes = 0, no = 0;
        var dialog = Open().OnYes(() => yes++).OnNo(() => no++);
        Assert.True(_runtime.Click("prompt:Progenitor-HotDemo", "promptYes"));
        Assert.Equal(1, yes);
        Assert.Equal(0, no);
        Assert.False(dialog.IsOpen);
        Assert.Null(UiDialog.Current);

        var second = Open().OnYes(() => yes++).OnNo(() => no++);
        Assert.True(_runtime.Click("prompt:Progenitor-HotDemo", "promptNo"));
        Assert.Equal(1, yes);
        Assert.Equal(1, no);
        Assert.False(second.IsOpen);
    }

    /// <summary>
    /// AUTOUPDATE1 review: a framework dialog replaced by another framework dialog, or closed by its owner, runs its drop
    /// handler once (and no answer); an answered dialog never runs it. The pre-join prompt releases a held join on it.
    /// </summary>
    [Fact]
    public void A_replaced_or_closed_framework_dialog_runs_its_drop_handler_and_an_answered_one_does_not()
    {
        int dropped = 0, yes = 0;
        UiDialog Framework(string key) => UiDialog.Open(
            t => UiApi.Panel(t, _owner, "progenitor"), "prejoin", "prejoin-updates", key,
            "Mod updates are available", "Chat 1.0.0 to 1.1.0.", "Update first", LabelTone.Ok, "Cancel", rowKey: "prejoin:" + key);

        var first = Framework("a").OnYes(() => yes++).OnDropped(() => dropped++);
        Assert.True(first.IsOpen);
        var second = Framework("b").OnDropped(() => dropped++);
        Assert.False(first.IsOpen);
        Assert.Equal(1, dropped);
        second.Close();
        Assert.Equal(2, dropped);

        var answered = Framework("c").OnYes(() => yes++).OnDropped(() => dropped++);
        Assert.True(_runtime.Click("prejoin:c", UiPromptBlock.CellYes));
        Assert.Equal(1, yes);
        Assert.Equal(2, dropped);
        Assert.False(answered.IsOpen);
    }

    /// <summary>
    /// Escape = No. The key hook answers the open dialog before the sweep that hides every shown
    /// panel, so the owner is told rather than left believing a box the player dismissed is still
    /// up — the FUI29 failure, which is why this is a hook and not "the panel went away".
    ///
    /// <para>What removal would break: dropping <c>UiDialog.NotifyEscape</c> from
    /// <c>Ui.OnBootstrapKey</c> — the dialog leaves the screen with no decision and no owner
    /// callback.</para>
    /// </summary>
    [Fact]
    public void Escape_answers_the_open_dialog_with_No()
    {
        int yes = 0, no = 0;
        var dialog = Open().OnYes(() => yes++).OnNo(() => no++);
        UiApi.PressKeyForTests(27, true);
        Assert.Equal(0, yes);
        Assert.Equal(1, no);
        Assert.False(dialog.IsOpen);
        Assert.Null(UiDialog.Current);

        // A second Escape with nothing open answers nobody.
        UiApi.PressKeyForTests(27, true);
        Assert.Equal(1, no);
    }

    /// <summary>
    /// One dialog at a time: a second Open closes the first (its panel is disposed) and tells
    /// nobody, so a refusal replacing a confirmation cannot stack two overlays or fire the first
    /// box's No as a side effect.
    ///
    /// <para>What removal would break: two modal panels over each other, each with a backdrop.</para>
    /// </summary>
    [Fact]
    public void A_second_dialog_replaces_the_first_without_answering_it()
    {
        int no = 0;
        var first = Open().OnNo(() => no++);
        var second = Open(heading: "Uninstall HotDemo 0.1.0?", yes: "Yes, uninstall", kind: "uninstall");
        Assert.False(first.IsOpen);
        Assert.True(second.IsOpen);
        Assert.Equal(0, no);
        Assert.Equal(UiHandleState.Disposed, ((IUiHandle)first.Panel).State);
    }

    /// <summary>
    /// A refusal has nothing to say yes to: one button, and it is the caller's <c>promptClose</c>
    /// handle on the No seam, so Close and Escape are the same answer.
    ///
    /// <para>What removal would break: a refusal box that still offers Yes, or one whose single
    /// button is published under a handle no readback names.</para>
    /// </summary>
    [Fact]
    public void A_refusal_draws_one_Close_button_on_the_No_seam()
    {
        int closed = 0;
        var dialog = UiApi.Dialog(
            "Install HotDemo 0.1.0?", "Cannot install — the package is not signed.",
            yesCaption: "", yesTone: LabelTone.Ok, noCaption: "Close",
            tag: "prompt", kind: "install", key: "Progenitor-HotDemo",
            owner: _owner, pluginId: "example.installux2",
            rowKey: "prompt:Progenitor-HotDemo", yesKey: "promptYes", noKey: "promptClose")
            .OnNo(() => closed++);

        Assert.Equal(new[] { "promptClose" }, _runtime.CellKeysOf("prompt:Progenitor-HotDemo", CellKind.Button));
        Assert.True(_runtime.Click("prompt:Progenitor-HotDemo", "promptClose"));
        Assert.Equal(1, closed);
        Assert.False(dialog.IsOpen);
    }

    /// <summary>
    /// The readback <c>dialog {open, key, kind}</c> the workspace status carries, closed and open.
    ///
    /// <para>What removal would break: <c>mods_ui status</c> could no longer say which
    /// confirmation is on screen, which is the only way a bridge client knows what it is about to
    /// click Yes on.</para>
    /// </summary>
    [Fact]
    public void Describe_reports_the_open_dialog_for_the_status_readback()
    {
        var closed = UiDialog.Describe();
        Assert.Equal(false, closed["open"]);
        Assert.Equal("", closed["key"]);
        Assert.Equal("none", closed["kind"]);

        Open(kind: "uninstall", key: "Progenitor-HotDemo");
        var open = UiDialog.Describe();
        Assert.Equal(true, open["open"]);
        Assert.Equal("Progenitor-HotDemo", open["key"]);
        Assert.Equal("uninstall", open["kind"]);
    }

    /// <summary>A runtime that records the chrome ask and hosts clickable cell rows.</summary>
    private sealed class DialogRuntime : CellRowTests.PlainRuntime
    {
        private readonly Dictionary<string, CellRowTests.FakeCellRow> _rows = new(StringComparer.Ordinal);
        private readonly Dictionary<string, CellRowSpec> _specs = new(StringComparer.Ordinal);

        public int PanelsCreated;
        public int ChromeAsks;
        public int Shows;

        public override IRuntimePanel? CreatePanel(string title, string rootName)
        {
            PanelsCreated++;
            return base.CreatePanel(title, rootName);
        }

        public override void DialogChrome(IRuntimePanel panel) => ChromeAsks++;

        public override void Show(IRuntimePanel panel) => Shows++;

        public override IRuntimeCellRow? AddCellRow(IRuntimePanel panel, CellRowSpec spec, bool header)
        {
            var row = new CellRowTests.FakeCellRow(null, spec, header);
            _rows[spec.Key] = row;
            _specs[spec.Key] = spec;
            return row;
        }

        /// <summary>The button cells drawn on one row, in order.</summary>
        public string[] CellKeysOf(string rowKey, CellKind kind)
        {
            if (!_specs.TryGetValue(rowKey, out var spec)) return Array.Empty<string>();
            var keys = new List<string>();
            foreach (var cell in spec.Cells)
                if (cell.Kind == kind) keys.Add(cell.Key);
            return keys.ToArray();
        }

        /// <summary>Raise the click slot wired to one cell, the way Bootstrap does.</summary>
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
