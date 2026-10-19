using System;
using System.Collections.Generic;
using System.Linq;
using Progenitor.Networking;
using Progenitor.Ui;
using Xunit;
using UiApi = Progenitor.Ui.Ui;

namespace Progenitor.Tests;

/// <summary>
/// DIALOGS1: the one dialog layout (<see cref="DialogLayout"/>), the dialog queue and the mod-facing
/// dialogs (<see cref="UiDialogs"/>, what <c>ctx.Dialogs</c> calls), the migrated framework callers,
/// the mod footer and the custom-page grid. Live defects these pin (stage 2 ConfirmButton dialog at
/// 1600×1024 and 3840×1080): the button pair 64 units right of center, a 72-unit empty band above
/// the title and a 200-unit floor under a two-row dialog.
/// </summary>
[Collection("PluginLogger")]
public sealed class DialogTests : IDisposable
{
    private readonly DialogFakeRuntime _runtime = new();
    private readonly object _owner = new();
    private readonly TestLogSource _log = new();
    private long _now = 10_000;

    public DialogTests()
    {
        Plugin.Logger = _log;
        UiApi.ResetForTests();
        UiApi.SetRuntimeForTests(_runtime);
        FrameworkVersions.CoreForTests = "2026.9.67";
        UiDialog.Clock = () => _now;
    }

    public void Dispose()
    {
        UiDialog.ForgetForTests();
        UiApi.ResetForTests();
        FrameworkVersions.ResetForTests();
        Plugin.Logger = null;
    }

    private const string Guid = "example.dialogs.test";
    private static readonly string Plain = "Progenitor 2026.9.67";

    private UiDialog Confirm(string title = "Delete the save?", Action? yes = null, Action? no = null, bool danger = false) =>
        UiDialogs.Confirm(_owner, Guid, Plain, title, "It cannot be undone.", "Delete", yes ?? (() => { }), "Keep", no, danger);

    private void Later() => _now += UiDialog.EnterGraceMs + 1;

    // ---- the layout ---------------------------------------------------------------------------

    /// <summary>
    /// The button group is centered in the frame for one, two and three buttons, at both live screen
    /// sizes. The frame is centered on the canvas and scaled uniformly, so a group centered in the
    /// row is centered on screen. The old in-pane block (right-aligned, 12 px inset, two 200 px
    /// buttons) is the defect: its pair sat 64 units right of center.
    /// </summary>
    [Theory]
    [InlineData(1600f, 1024f)]
    [InlineData(3840f, 1080f)]
    public void The_button_row_is_centred_in_the_dialog_for_one_to_three_buttons(float screenW, float screenH)
    {
        float scale = screenH / 1080f;
        float frameCentre = screenW / 2f;
        var sets = new[]
        {
            new[] { new DialogButton("main", "OK", DialogButtonRole.Main) },
            new[] { new DialogButton("cancel", "No", DialogButtonRole.Cancel), new DialogButton("main", "Log a line", DialogButtonRole.Main) },
            new[]
            {
                new DialogButton("cancel", "Cancel", DialogButtonRole.Cancel),
                new DialogButton("option-0", "Wolf", DialogButtonRole.Option),
                new DialogButton("option-1", "Bear", DialogButtonRole.Option),
            },
        };
        foreach (var set in sets)
        {
            var spec = DialogLayout.ButtonRowSpec("dialog-buttons", set);
            var placed = MenuPanelLayout.CellColumns(DialogLayout.RowWidth, spec).Where(p => p.Visible && p.Width > 0f && !p.Key.StartsWith("dialog-", StringComparison.Ordinal)).ToList();
            Assert.Equal(set.Length, placed.Count);
            float groupCentre = (placed.First().Left + placed.Last().Right) / 2f;
            Assert.Equal(frameCentre, frameCentre + groupCentre * scale, 1);
            Assert.True(placed.First().Left >= -DialogLayout.RowWidth / 2f && placed.Last().Right <= DialogLayout.RowWidth / 2f);
        }

        // The live defect, measured with the old block's own spec.
        var old = new CellRowSpec("old", new List<CellSpec>
        {
            new CellSpec(CellKind.Spacer, "gap"),
            new CellSpec(CellKind.Button, "no").With(text: "No", align: CellAlign.Right),
            new CellSpec(CellKind.Button, "yes").With(text: "Log a line", align: CellAlign.Right),
        }, rightInset: HeadBlockLayout.RightColumnInset);
        var oldPlaced = MenuPanelLayout.CellColumns(DialogLayout.RowWidth, old);
        float oldCentre = (oldPlaced.Single(p => p.Key == "no").Left + oldPlaced.Single(p => p.Key == "yes").Right) / 2f;
        Assert.True(oldCentre > 60f, "the pre-DIALOGS1 pair was right of center: " + oldCentre);
    }

    /// <summary>Every button of a row has one width, at least its widest caption's ask and the floor.</summary>
    [Fact]
    public void Buttons_share_one_width_at_least_as_wide_as_each_caption()
    {
        var set = new[]
        {
            new DialogButton("cancel", "No", DialogButtonRole.Cancel),
            new DialogButton("main", "Yes, uninstall", DialogButtonRole.Main),
        };
        var placed = MenuPanelLayout.CellColumns(DialogLayout.RowWidth, DialogLayout.ButtonRowSpec("r", set))
            .Where(p => p.Key == "cancel" || p.Key == "main").ToList();
        Assert.Equal(placed[0].Width, placed[1].Width, 2);
        foreach (var b in set)
        {
            Assert.True(placed[0].Width >= DialogLayout.CaptionWidth(b.Caption) - .01f, b.Caption);
            Assert.True(CellLayout.CaptionFits(new CellSpec(CellKind.Button, b.Key).With(text: b.Caption, fontSize: DialogLayout.ButtonFontSize), placed[0].Width));
        }

        Assert.True(placed[0].Width >= DialogLayout.MinButtonWidth);
        // OK alone still gets the floor, not a sliver.
        Assert.Equal(DialogLayout.MinButtonWidth, DialogLayout.ButtonWidth(new[] { "OK" }), 2);

        // Captions too long for the row share it equally instead of running past the dialog's edge.
        var huge = new[]
        {
            new DialogButton("cancel", "No, leave every setting as it is", DialogButtonRole.Cancel),
            new DialogButton("main", "Yes, reset every setting to its default", DialogButtonRole.Main),
        };
        var capped = MenuPanelLayout.CellColumns(DialogLayout.RowWidth, DialogLayout.ButtonRowSpec("r", huge))
            .Where(p => p.Key == "cancel" || p.Key == "main").ToList();
        Assert.Equal(capped[0].Width, capped[1].Width, 2);
        Assert.True(capped[0].Left >= -DialogLayout.RowWidth / 2f - .01f && capped[1].Right <= DialogLayout.RowWidth / 2f + .01f);
        Assert.False(DialogLayout.ButtonsFit(huge.Select(b => b.Caption).ToList()));
    }

    /// <summary>The main action is drawn rightmost and the dismissal leftmost, whatever order they came in.</summary>
    [Fact]
    public void The_main_action_is_on_the_right()
    {
        var ordered = DialogLayout.Order(new[]
        {
            new DialogButton("main", "Save", DialogButtonRole.Main),
            new DialogButton("option-0", "A", DialogButtonRole.Option),
            new DialogButton("cancel", "Cancel", DialogButtonRole.Cancel),
        });
        Assert.Equal(new[] { "cancel", "option-0", "main" }, ordered.Select(b => b.Key).ToArray());

        Confirm();
        var buttons = UiDialog.Current!.View!.Buttons!;
        var keys = buttons.Cells.Where(c => c.Kind == CellKind.Button).Select(c => c.Key).ToArray();
        Assert.Equal(new[] { UiDialogs.CellCancel, UiDialogs.CellMain }, keys);
    }

    /// <summary>The main button is the accent, or the danger tone for a destructive confirmation.</summary>
    [Fact]
    public void A_destructive_confirmation_draws_its_main_action_in_the_danger_tone()
    {
        Confirm(danger: false);
        Assert.Equal(CellTone.Gold, MainCell().Tone);
        UiDialog.ForgetForTests();
        UiDialog.Clock = () => _now;
        Confirm(danger: true);
        Assert.Equal(CellTone.Danger, MainCell().Tone);
        Assert.Equal(CellTone.Normal, UiDialog.Current!.View!.Buttons!.Cells.Single(c => c.Key == UiDialogs.CellCancel).Tone);
    }

    private static CellSpec MainCell() =>
        UiDialog.Current!.View!.Buttons!.Cells.Single(c => c.Key == UiDialogs.CellMain);

    /// <summary>
    /// The frame is its content, the top pad and the footer band — nothing more. A one-line
    /// confirmation with no body used to get the 72-unit title band on top and the 200-unit floor.
    /// </summary>
    [Fact]
    public void The_frame_height_fits_its_content_with_no_dead_band()
    {
        float title = DialogLayout.TextRowHeight(DialogLayout.TitleLines("Write a line to the Sample log?"), DialogLayout.TitleLineHeight);
        float body = DialogLayout.TextRowHeight(DialogLayout.BodyLines("A confirmed action: it asks first."), DialogLayout.BodyLineHeight);
        float band = PanelFooterLayout.BandHeight;
        foreach (var rows in new[] { new[] { title, DialogLayout.ButtonRowHeight }, new[] { title, body, DialogLayout.ButtonRowHeight } })
        {
            float content = DialogLayout.ContentHeight(rows);
            float frame = DialogLayout.FrameHeight(content, band, 1080f);
            float drawn = rows.Sum() + PanelLayout.RowGap * (rows.Length - 1);
            float air = frame - drawn - band;
            Assert.Equal(DialogLayout.FrameTopPad + PanelLayout.PadTop + PanelLayout.PadBottom, air, 2);
            Assert.True(air <= 48f, "air above and below the rows: " + air);
            // The old sizing: the title band plus the floor.
            float old = PanelFooterLayout.PanelHeightFor(content, 1080f, Plain);
            Assert.True(old - frame >= PanelLayout.HeaderHeight - DialogLayout.FrameTopPad - .01f);
        }

        // Past 80 % of the screen the content scrolls instead.
        Assert.Equal(1080f * PanelLayout.ScreenFraction, DialogLayout.FrameHeight(5000f, band, 1080f), 2);
        Assert.Equal(DialogLayout.FrameHeight(500f, band, 1080f) - DialogLayout.FrameTopPad - band, DialogLayout.ViewportHeight(DialogLayout.FrameHeight(500f, band, 1080f), band), 2);
    }

    /// <summary>The title and body are dialog text rows (not a heading with its full-width rule), in that order, above the buttons.</summary>
    [Fact]
    public void A_dialog_draws_title_and_body_as_dialog_text_and_no_heading()
    {
        Confirm("Delete the save?");
        Assert.Equal(new[] { ("Delete the save?", true), ("It cannot be undone.", false) }, _runtime.DialogTexts.ToArray());
        Assert.Equal(0, _runtime.Headings);
        Assert.Equal(1, _runtime.ChromeAsks);
        Assert.Equal(UiCaptureMode.WhileShown, UiDialog.Current!.Panel!.CaptureMode);
    }

    // ---- keys ---------------------------------------------------------------------------------

    /// <summary>Enter picks the main action and Escape the dismissal; Enter right after the dialog appears is ignored.</summary>
    [Fact]
    public void Enter_confirms_and_Escape_cancels()
    {
        int yes = 0, no = 0;
        Confirm(yes: () => yes++, no: () => no++);
        Assert.False(UiDialog.NotifyEnter(), "the Enter that opened it cannot answer it");
        Later();
        Assert.True(UiDialog.NotifyEnter());
        Assert.Equal((1, 0), (yes, no));
        Assert.Null(UiDialog.Current);

        Confirm(yes: () => yes++, no: () => no++);
        UiApi.PressKeyForTests(27, true);
        Assert.Equal((1, 1), (yes, no));
        Assert.Null(UiDialog.Current);

        // A notice: Enter and Escape both close it and run onClose.
        int closed = 0;
        UiDialogs.Notice(_owner, Guid, Plain, "Saved", "", "OK", () => closed++);
        UiApi.PressKeyForTests(27, true);
        UiDialogs.Notice(_owner, Guid, Plain, "Saved", "", "OK", () => closed++);
        Later();
        UiDialog.NotifyEnter();
        Assert.Equal(2, closed);

        // A Choose has no main action: Enter answers nothing.
        int picked = -1;
        UiDialogs.Choose(_owner, Guid, Plain, "Pick", "", new[] { "A", "B" }, i => picked = i, "Cancel", null);
        Later();
        Assert.False(UiDialog.NotifyEnter());
        Assert.NotNull(UiDialog.Current);
        Assert.Equal(-1, picked);
    }

    /// <summary>
    /// A framework confirmation opened with <c>danger: true</c> (the Mods page's Uninstall) is not
    /// answered by Enter, even after the grace, and its main button is drawn in the danger tone whatever
    /// tone the caller passed; a click still answers it. Without the flag, Enter answers as before.
    /// What this would catch: <c>UiDialog.Open</c> dropping the flag, so Enter uninstalls a mod.
    /// </summary>
    [Fact]
    public void A_framework_danger_confirmation_ignores_Enter_and_wears_the_danger_tone()
    {
        int yes = 0;
        var removal = UiDialog.Open(t => UiApi.Panel(t), "t", "uninstall", "", "Uninstall it?", "", "Yes, uninstall",
            LabelTone.Orange, "No", danger: true);
        removal.OnYes(() => yes++);
        Assert.True(removal.Content.Danger);
        Assert.Equal(CellTone.Danger, removal.Content.MainTone);
        Later();
        Assert.False(UiDialog.NotifyEnter(), "Enter must not answer a destructive confirmation");
        Assert.True(removal.IsOpen);
        Assert.Equal(0, yes);
        removal.Close();

        var keep = UiDialog.Open(t => UiApi.Panel(t), "t", "reactivate", "", "Keep it?", "", "Yes, keep it",
            LabelTone.Ok, "No");
        keep.OnYes(() => yes++);
        Assert.False(keep.Content.Danger);
        Assert.Equal(CellTone.Ok, keep.Content.MainTone);
        Later();
        Assert.True(UiDialog.NotifyEnter());
        Assert.Equal(1, yes);
        Assert.False(keep.IsOpen);
    }

    // ---- the queue ----------------------------------------------------------------------------

    /// <summary>One dialog at a time; later ones wait and are shown in the order they were opened.</summary>
    [Fact]
    public void Later_dialogs_queue_in_FIFO_order()
    {
        var answered = new List<string>();
        var a = UiDialogs.Notice(_owner, Guid, Plain, "A", "", "OK", () => answered.Add("A"));
        var b = UiDialogs.Notice(_owner, Guid, Plain, "B", "", "OK", () => answered.Add("B"));
        var c = UiDialogs.Notice(_owner, Guid, Plain, "C", "", "OK", () =>
        {
            answered.Add("C");
            // A dialog a handler opens goes behind the ones already waiting (none here).
            UiDialogs.Notice(_owner, Guid, Plain, "D", "", "OK", () => answered.Add("D"));
        });
        Assert.True(a.IsOpen);
        Assert.True(b.IsQueued && c.IsQueued);
        Assert.Equal(2, UiDialog.QueuedCount);
        Assert.Equal(1, _runtime.Shows);

        foreach (string expected in new[] { "A", "B", "C", "D" })
        {
            Assert.Equal(expected, UiDialog.Current!.Content.Title);
            Assert.True(_runtime.Click(UiDialogs.ButtonsRow, UiDialogs.CellMain));
        }

        Assert.Equal(new[] { "A", "B", "C", "D" }, answered);
        Assert.Null(UiDialog.Current);

        // Disposing a queued handle takes it out without a callback.
        UiDialogs.Notice(_owner, Guid, Plain, "E", "", "OK", () => answered.Add("E"));
        var f = UiDialogs.Notice(_owner, Guid, Plain, "F", "", "OK", () => answered.Add("F"));
        f.Dispose();
        Assert.True(_runtime.Click(UiDialogs.ButtonsRow, UiDialogs.CellMain));
        Assert.Null(UiDialog.Current);
        Assert.DoesNotContain("F", answered);
    }

    /// <summary>
    /// A framework dialog takes the screen at once and puts a mod's back at the head of the queue,
    /// unanswered; the mod's comes back (with its typed text) once the framework's is answered.
    /// </summary>
    [Fact]
    public void A_framework_dialog_steps_in_front_of_a_mods_dialog_without_answering_it()
    {
        int submitted = 0, cancelled = 0;
        var prompt = UiDialogs.Prompt(_owner, Guid, Plain, "Name", "", "", _ => submitted++, "", null, "OK", 0, "Cancel", () => cancelled++);
        prompt.SetText("Ada");
        var framework = UiApi.Dialog("Uninstall HotDemo?", "", "Yes, uninstall", LabelTone.Orange, "No", owner: new object(), pluginId: "progenitor");
        Assert.True(framework.IsOpen);
        Assert.True(prompt.IsQueued);
        Assert.Equal((0, 0), (submitted, cancelled));

        UiApi.PressKeyForTests(27, true); // answers the framework's No, then the queue resumes
        Assert.True(prompt.IsOpen);
        Assert.Equal("Ada", prompt.Text);
        Assert.Equal("Ada", prompt.View!.Input!.Cells.Single(c => c.Key == UiDialogs.CellField).Text);
    }

    /// <summary>
    /// MASKED1: a secret prompt builds a masked field (the runtime draws asterisks and blocks copy), reads back asterisks
    /// only, and still hands the typed text to its submit; an ordinary prompt stays unmasked.
    /// </summary>
    [Fact]
    public void A_secret_prompt_masks_its_field_and_every_readback_but_submits_the_text()
    {
        string? submitted = null;
        var secret = UiDialogs.Prompt(_owner, Guid, Plain, "Locked", "Enter the password.", "", t => submitted = t, "", null, "OK", 32, "Cancel", null, masked: true);
        Assert.True(secret.IsOpen);
        var field = _runtime.Row(UiDialogs.InputRow)!.Cells().Single(c => c.Key == UiDialogs.CellField);
        Assert.True(field.Masked);
        Assert.Equal(32, field.MaxLength);

        ModsMenu.TypeDialog("hunter2");
        var read = UiDialog.Describe();
        Assert.Equal("*******", read["typed"]);
        Assert.Equal("*******", read["field"]);
        Assert.Equal(true, read["masked"]);
        Assert.DoesNotContain("hunter2", string.Join("|", read.Values.Select(v => v?.ToString() ?? "")));

        Assert.True(_runtime.Click(UiDialogs.ButtonsRow, UiDialogs.CellMain));
        Assert.Equal("hunter2", submitted);
        Assert.Null(UiDialog.Current);

        Assert.Equal("", secret.Text);                                    // the typed secret left with the dialog

        UiDialogs.Prompt(_owner, Guid, Plain, "Name", "", "", _ => { }, "", null, "OK", 0, "Cancel", null);
        Assert.False(_runtime.Row(UiDialogs.InputRow)!.Cells().Single(c => c.Key == UiDialogs.CellField).Masked);
        ModsMenu.TypeDialog("Ada");
        Assert.Equal("Ada", UiDialog.Describe()["typed"]);
        Assert.Equal(false, UiDialog.Describe()["masked"]);
    }

    /// <summary>
    /// MASKED1: a masked form (the container open gate's prompt with its Remember password box) masks its field and
    /// readback, hands the text and the box to its save, and keeps no text after; a masked dialog that a framework
    /// dialog displaces comes back empty, not with the typed secret.
    /// </summary>
    [Fact]
    public void A_masked_form_hands_the_text_and_the_remember_box_to_its_save_and_keeps_nothing()
    {
        DialogFormResult? saved = null;
        var form = UiDialogs.Form(_owner, Guid, Plain, "Locked", new DialogFormOptions
        {
            Caption = "Password",
            Masked = true,
            Initial = "ignored?",
            MaxLength = 32,
            Checks = new[] { new DialogCheck { Label = "Remember password", Key = "remember" } },
            Save = "OK",
        }, r => saved = r, body: "Enter this container's password.");
        var field = _runtime.Row(UiDialogs.InputRow)!.Cells().Single(c => c.Key == UiDialogs.CellField);
        Assert.True(field.Masked);
        Assert.Contains(("Enter this container's password.", false), _runtime.DialogTexts);

        form.SetText("sesame");
        Assert.Equal("******", UiDialog.Describe()["typed"]);
        ClickRow(form.View!.CheckRows[0]);
        Assert.True(_runtime.Click(UiDialogs.ButtonsRow, UiDialogs.CellMain));
        Assert.NotNull(saved);
        Assert.Equal("sesame", saved!.Text);
        Assert.True(saved.IsOn("remember"));
        Assert.Equal("", form.Text);
    }

    [Fact]
    public void A_masked_prompt_displaced_by_a_framework_dialog_comes_back_empty()
    {
        var secret = UiDialogs.Prompt(_owner, Guid, Plain, "Locked", "", "", _ => { }, "", null, "OK", 32, "Cancel", null, masked: true);
        secret.SetText("hunter2");
        var framework = UiApi.Dialog("Uninstall HotDemo?", "", "Yes, uninstall", LabelTone.Orange, "No", owner: new object(), pluginId: "progenitor");
        Assert.True(secret.IsQueued);
        UiApi.PressKeyForTests(27, true);
        Assert.True(secret.IsOpen);
        Assert.Equal("", secret.Text);
        Assert.Equal("", secret.View!.Input!.Cells.Single(c => c.Key == UiDialogs.CellField).Text);
        _ = framework;
    }

    // ---- unload -------------------------------------------------------------------------------

    /// <summary>Every unload path drops the plugin's dialogs, shown and queued, and runs no callback.</summary>
    [Fact]
    public void Unload_drops_shown_and_queued_dialogs_without_callbacks()
    {
        int calls = 0;
        var other = new object();
        UiDialogs.Confirm(_owner, Guid, Plain, "One", "", "Yes", () => calls++, "No", () => calls++, false);
        UiDialogs.Notice(other, "example.other", Plain, "Other", "", "OK", () => calls += 100);
        UiDialogs.Prompt(_owner, Guid, Plain, "Two", "", "", _ => calls++, "", null, "OK", 0, "Cancel", () => calls++);

        // The tracked ctx.Dialogs registration's disposal.
        Assert.Equal(2, UiDialog.DropOwner(_owner));
        Assert.Equal(0, calls);
        Assert.Equal("Other", UiDialog.Current!.Content.Title);
        Assert.Equal(0, UiDialog.QueuedCount);

        // The owner sweep (Ui.RemoveAll) is the second line and reaches a dialog on screen too.
        UiDialogs.Notice(_owner, Guid, Plain, "Three", "", "OK", () => calls++);
        UiApi.RemoveAll(other);
        Assert.Equal("Three", UiDialog.Current!.Content.Title);
        UiApi.RemoveAll(_owner);
        Assert.Null(UiDialog.Current);
        Assert.Equal(0, calls);
    }

    /// <summary>A callback that throws is caught and logged; the queue carries on.</summary>
    [Fact]
    public void A_throwing_callback_is_logged_and_the_queue_continues()
    {
        UiDialogs.Notice(_owner, Guid, Plain, "Boom", "", "OK", () => throw new InvalidOperationException("boom"));
        UiDialogs.Notice(_owner, Guid, Plain, "Next", "", "OK", null);
        Assert.True(_runtime.Click(UiDialogs.ButtonsRow, UiDialogs.CellMain));
        Assert.Contains(_log.Warnings, l => l.Contains("answer threw", StringComparison.Ordinal) && l.Contains("boom", StringComparison.Ordinal));
        Assert.Equal("Next", UiDialog.Current!.Content.Title);
    }

    // ---- Prompt -------------------------------------------------------------------------------

    /// <summary>Validation blocks the submit and shows the error under the field; typing clears it; a valid value submits.</summary>
    [Fact]
    public void Prompt_validation_blocks_submit_and_shows_the_error()
    {
        string? got = null;
        var prompt = UiDialogs.Prompt(
            _owner, Guid, Plain, "Rename", "A name for the camp.", "", s => got = s, "Camp name",
            s => s.Trim().Length == 0 ? "Enter a name." : null, "Rename", 12, "Cancel", null);
        var error = prompt.View!.Error!;
        Assert.False(error.Visible);
        Assert.Equal("Camp name", prompt.View.Input!.Cells.Single(c => c.Key == UiDialogs.CellField).Placeholder);
        Assert.Equal(12, prompt.View.Input!.Cells.Single(c => c.Key == UiDialogs.CellField).MaxLength);

        Assert.True(_runtime.Click(UiDialogs.ButtonsRow, UiDialogs.CellMain));
        Assert.True(prompt.IsOpen);
        Assert.Null(got);
        Assert.True(error.Visible);
        Assert.Equal("Enter a name.", error.Cells.Single(c => c.Key == UiDialogs.CellError).Text);
        Assert.Equal("Enter a name.", UiDialog.Describe()["error"]);

        // Typing (the field's text slot, as the runtime raises it) clears the error.
        Assert.True(_runtime.Type(UiDialogs.InputRow, UiDialogs.CellField, "a much too long name"));
        Assert.False(error.Visible);
        Later();
        Assert.True(UiDialog.NotifyEnter());
        Assert.True(prompt.IsOpen, "longer than maxLength");
        Assert.Equal(UiDialogs.TooLong(12), prompt.Error);

        _runtime.Type(UiDialogs.InputRow, UiDialogs.CellField, "Riverside");
        UiDialog.NotifyEnter();
        Assert.Equal("Riverside", got);
        Assert.Null(UiDialog.Current);

        // A validator that throws blocks the submit and is logged.
        UiDialogs.Prompt(_owner, Guid, Plain, "X", "", "v", _ => got = "bad", "", _ => throw new Exception("nope"), "OK", 0, "Cancel", null);
        _runtime.Click(UiDialogs.ButtonsRow, UiDialogs.CellMain);
        Assert.Equal(UiDialogs.ValidationFailed, UiDialog.Current!.Error);
        Assert.Equal("Riverside", got);
    }

    // ---- Form (FORMDIALOG1) --------------------------------------------------------------------

    private static DialogCheck Check(string label, bool on, string key = "") => new() { Label = label, On = on, Key = key };

    private static CellSpec LabelCell(CellRowSpec row) => row.Cells.Single(c => c.Key == DialogForm.CellLabel);

    [Fact]
    public void Form_check_without_a_color_keeps_the_normal_label_color()
    {
        var check = Check("Blood", true);
        Assert.Equal("", check.Color);
        var label = LabelCell(DialogForm.CheckRowSpec(0, check));
        Assert.Null(label.TextColor);
        Assert.Equal(CellTone.Normal, label.Tone);
        Assert.Equal("", new DialogCheck { Label = "x", Color = "blue" }.Color);
        Assert.Equal("", new DialogCheck { Label = "x", Color = "#12345" }.Color);
    }

    [Fact]
    public void Form_check_color_reaches_its_label_and_survives_a_flip()
    {
        var check = new DialogCheck { Label = "Global", Color = " #7ab8f5 " };
        Assert.Equal("#7AB8F5", check.Color);
        Assert.Equal("#7AB8F5", check.With(true).Color);
        Assert.Equal("#7AB8F5", DialogForm.Flip(new[] { check }, 0)[0].Color);

        var color = LabelCell(DialogForm.CheckRowSpec(0, check)).TextColor;
        Assert.NotNull(color);
        Assert.Equal(PaletteColor.Hex(0x7AB8F5), color!.Value);
        // A disabled box draws the disabled tone, not its color.
        Assert.Null(LabelCell(DialogForm.CheckRowSpec(0, check, enabled: false)).TextColor);
    }

    [Fact]
    public void Form_check_color_too_dark_for_the_dialog_is_lifted()
    {
        var dark = DialogForm.ReadableLabel("#202040");
        Assert.NotNull(dark);
        Assert.True(dark!.Value.ContrastAgainst(MenuPalette.PaneFill) >= DialogForm.MinLabelContrast);
        Assert.True(dark.Value.B > dark.Value.R);
        Assert.Null(DialogForm.ReadableLabel(""));
    }

    [Fact]
    public void Form_passes_check_colors_to_its_rows()
    {
        var dialog = UiDialogs.Form(_owner, Guid, Plain, "Tab", new DialogFormOptions
        {
            Checks = new[] { new DialogCheck { Label = "Global", Color = "#7AB8F5" }, Check("Plain", false) },
        }, _ => { });
        var view = dialog.View!;
        Assert.Equal(PaletteColor.Hex(0x7AB8F5), view.CheckRows[0].Cells.Single(c => c.Key == DialogForm.CellLabel).TextColor);
        Assert.Null(view.CheckRows[1].Cells.Single(c => c.Key == DialogForm.CellLabel).TextColor);
        Assert.Equal("#7AB8F5", dialog.Content.Checks[0].Color);
    }

    private UiDialog Form(Action<DialogFormResult> onSave, bool blood = true, bool gems = false, Func<DialogFormResult, string?>? validate = null) =>
        UiDialogs.Form(_owner, Guid, Plain, "Edit stall", new DialogFormOptions
        {
            Caption = "Name",
            Initial = "Night market",
            Placeholder = "stall",
            MaxLength = 24,
            Heading = "Show stalls for",
            Checks = new[] { Check("Blood", blood), Check(" Weapons ", false, "arms"), Check("Gems", gems) },
            RequireOne = true,
            NoneHint = "Turn on at least one stall.",
            Validate = validate,
        }, onSave);

    private static void ClickRow(IUiCellRow row) =>
        UiSlotTable.BootstrapDispatch(((UiHandle)row).WidgetId, (byte)UiSlotKind.Click, 0f, false, null);

    /// <summary>
    /// The user's report on the two-step tab editor: "Edit this tab shouldn't require me to click next to change what
    /// channels it listens to", and the Done button looked like one more channel. A form is one dialog: the captioned
    /// field, the heading, one line per box (the check cell and its label, left on one edge), the hidden hint, and the
    /// centered Cancel / Save pair, Save the gold main action.
    /// </summary>
    [Fact]
    public void A_form_is_one_dialog_with_the_field_the_boxes_and_a_Save_set_apart()
    {
        var form = Form(_ => { });
        var view = form.View!;
        var field = view.Input!.Cells;
        Assert.Equal("Name", field.Single(c => c.Key == DialogForm.CellCaption).Text);
        Assert.Equal(CellAlign.Left, field.Single(c => c.Key == DialogForm.CellCaption).Align);
        Assert.Equal("Night market", field.Single(c => c.Key == UiDialogs.CellField).Text);
        Assert.Equal(24, field.Single(c => c.Key == UiDialogs.CellField).MaxLength);
        Assert.Equal("Show stalls for", view.ChecksHeadingRow!.Cells.Single(c => c.Key == DialogForm.CellHeading).Text);

        Assert.Equal(3, view.CheckRows.Count);
        Assert.Equal(new[] { "on", "off", "off" }, view.CheckRows.Select(r => r.Cells.Single(c => c.Key == DialogForm.CellCheck).Text).ToArray());
        Assert.All(view.CheckRows, r => Assert.Equal(CellKind.Check, r.Cells.Single(c => c.Key == DialogForm.CellCheck).Kind));
        Assert.Equal(new[] { "Blood", "Weapons", "Gems" }, view.CheckRows.Select(r => r.Cells.Single(c => c.Key == DialogForm.CellLabel).Text).ToArray());
        Assert.All(view.CheckRows, r => Assert.True(((UiCellRowHandle)r).RowClickReachable));
        Assert.False(view.ChecksHintRow!.Visible);

        // One left edge: the caption, the heading and every box start after the same inset.
        float Left(CellRowSpec spec, string key) => MenuPanelLayout.CellColumns(DialogLayout.RowWidth, spec).Single(p => p.Key == key).Left;
        float edge = Left(DialogForm.CaptionedFieldRowSpec("f", "Name", new CellSpec(CellKind.Input, "field")), DialogForm.CellCaption);
        Assert.Equal(edge, Left(DialogForm.HeadingRowSpec("Show stalls for"), DialogForm.CellHeading), 1);
        Assert.Equal(edge, Left(DialogForm.CheckRowSpec(0, Check("Blood", true)), DialogForm.CellCheck), 1);

        // Order on the panel: field, error, heading, boxes, hint, then the buttons.
        var keys = view.Rows.Select(r => r is IUiCellRow c ? c.Key : "").Where(k => k.Length > 0).ToList();
        Assert.True(keys.IndexOf(UiDialogs.InputRow) < keys.IndexOf(DialogForm.HeadingRow));
        Assert.True(keys.IndexOf(DialogForm.CheckRow(2)) < keys.IndexOf(DialogForm.HintRow));
        Assert.Equal(UiDialogs.ButtonsRow, keys.Last());
        var buttons = view.Buttons!.Cells.Where(c => c.Kind == CellKind.Button).ToList();
        Assert.Equal(new[] { "Cancel", "Save" }, buttons.Select(b => b.Text).ToArray());
        Assert.All(buttons, b => Assert.Equal(CellSkin.PopupButton, b.Skin));
        Assert.Equal(CellTone.Gold, buttons.Last().Tone);
        Assert.Equal("form", form.Kind);
    }

    /// <summary>
    /// A click anywhere on a box's line flips it. With every box off, Save is grayed, Enter saves nothing and the hint
    /// says why; checking one lights Save again and hides the hint.
    /// </summary>
    [Fact]
    public void A_form_flips_boxes_by_their_line_and_waits_for_one_checked()
    {
        int saves = 0;
        var form = Form(_ => saves++);
        var view = form.View!;
        CellTone SaveTone() => view.Buttons!.Cells.Single(c => c.Key == UiDialogs.CellMain).Tone;

        ClickRow(view.CheckRows[0]); // Blood off: nothing is checked
        Assert.Equal("off", view.CheckRows[0].Cells.Single(c => c.Key == DialogForm.CellCheck).Text);
        Assert.False(form.Content.ChecksOk);
        Assert.Equal(CellTone.Disabled, SaveTone());
        Assert.True(view.ChecksHintRow!.Visible);
        Assert.Equal(false, UiDialog.Describe()["mainEnabled"]);
        Assert.Equal("Turn on at least one stall.", UiDialog.Describe()["checksHint"]);

        Later();
        Assert.True(UiDialog.NotifyEnter());
        Assert.True(form.IsOpen);
        Assert.Equal(0, saves);

        ClickRow(view.CheckRows[2]); // Gems on
        Assert.True(form.Content.ChecksOk);
        Assert.Equal(CellTone.Gold, SaveTone());
        Assert.False(view.ChecksHintRow.Visible);
        Assert.Equal(new[] { false, false, true }, form.CheckStates.ToArray());
    }

    /// <summary>Save hands over the text and every box's state; the validator's error shows under the field and keeps it open.</summary>
    [Fact]
    public void A_form_saves_the_text_and_the_boxes_after_validation()
    {
        string? name = null;
        bool[]? states = null;
        bool armsByKey = false, gemsByLabel = true;
        var form = Form(r => { name = r.Text; states = r.States.ToArray(); armsByKey = r.IsOn("ARMS"); gemsByLabel = r.IsOn("gems"); },
            validate: r => r.Text.Trim().Length == 0 ? "Enter a name." : r.Count != 3 ? "three boxes" : null);
        _runtime.Type(UiDialogs.InputRow, UiDialogs.CellField, "  ");
        Assert.True(_runtime.Click(UiDialogs.ButtonsRow, UiDialogs.CellMain));
        Assert.True(form.IsOpen);
        Assert.Equal("Enter a name.", form.Error);
        Assert.True(form.View!.Error!.Visible);

        _runtime.Type(UiDialogs.InputRow, UiDialogs.CellField, "Trade");
        ClickRow(form.View.CheckRows[1]);
        var checks = (List<object?>)UiDialog.Describe()["checks"]!;
        Assert.Equal(true, ((Dictionary<string, object?>)checks[1]!)["on"]);
        Assert.Equal("Weapons", ((Dictionary<string, object?>)checks[1]!)["label"]);
        Assert.True(_runtime.Click(UiDialogs.ButtonsRow, UiDialogs.CellMain));
        Assert.Null(UiDialog.Current);
        Assert.Equal("Trade", name);
        Assert.Equal(new[] { true, true, false }, states);
        Assert.True(armsByKey);
        Assert.False(gemsByLabel);
    }

    /// <summary>
    /// Enter presses the stop the player moved the keyboard focus to: on Cancel it cancels, on a box it flips the box and
    /// the dialog stays. The focus the dialog starts with (Cancel) does not take Enter: typing a name and pressing Enter
    /// saves. A destructive confirmation's main answer is not pressed by Enter even when focused.
    /// </summary>
    [Fact]
    public void Enter_presses_the_focused_stop_and_otherwise_saves()
    {
        UiNav.Reset();
        try
        {
            int saves = 0, cancels = 0;
            var form = UiDialogs.Form(_owner, Guid, Plain, "Edit stall", new DialogFormOptions
            {
                Caption = "Name", Initial = "Night market", Checks = new[] { Check("Blood", true), Check("Gems", false) },
                OnCancel = () => cancels++,
            }, _ => saves++);
            Later();
            Assert.True(UiNav.FocusOn((UiHandle)form.View!.CheckRows[1], ""));
            Assert.True(UiDialog.NotifyEnter());
            Assert.True(form.IsOpen);
            Assert.True(form.Content.Checks[1].On);
            Assert.Equal(0, saves);

            Assert.True(UiNav.FocusOn((UiHandle)form.View.Buttons!, UiDialogs.CellCancel));
            Assert.True(UiDialog.NotifyEnter());
            Assert.Null(UiDialog.Current);
            Assert.Equal((0, 1), (saves, cancels));

            // A fresh dialog: its starting focus (Cancel) was not chosen by the player, so Enter saves.
            UiNav.Reset();
            UiDialogs.Form(_owner, Guid, Plain, "Edit stall", new DialogFormOptions
            {
                Caption = "Name", Initial = "Night market", Checks = new[] { Check("Blood", true) },
            }, _ => saves++);
            Later();
            UiNav.Execute(NavCommand.Down); // the first press only shows the marker where the dialog put it
            Assert.True(UiDialog.NotifyEnter());
            Assert.Equal(1, saves);

            // Danger: Enter on the focused main answer presses nothing.
            UiNav.Reset();
            int yes = 0;
            var danger = Confirm(yes: () => yes++, danger: true);
            Later();
            Assert.True(UiNav.FocusOn((UiHandle)danger.View!.Buttons!, UiDialogs.CellMain));
            UiDialog.NotifyEnter();
            Assert.True(danger.IsOpen);
            Assert.Equal(0, yes);
        }
        finally { UiNav.Reset(); }
    }

    /// <summary>
    /// The bridge flips a box by its label (or its row key) through the line's own click slot, as a pointer does; a
    /// framework dialog that steps in and back rebuilds the form with the boxes as the player left them.
    /// </summary>
    [Fact]
    public void A_form_box_answers_the_bridge_by_label_and_survives_a_framework_dialog()
    {
        var form = Form(_ => { });
        Assert.True(form.TryClickForBridge("weapons", out var detail));
        Assert.Equal(DialogForm.CheckRow(1), detail["label"]);
        Assert.True(form.Content.Checks[1].On);

        var framework = UiApi.Dialog("Uninstall HotDemo?", "", "Yes, uninstall", LabelTone.Orange, "No", owner: new object(), pluginId: "progenitor");
        Assert.True(form.IsQueued);
        framework.Close();
        Assert.True(form.IsOpen);
        Assert.Equal(new[] { "on", "on", "off" }, form.View!.CheckRows.Select(r => r.Cells.Single(c => c.Key == DialogForm.CellCheck).Text).ToArray());
    }

    /// <summary>The public call refuses an empty set of boxes, and more than ten.</summary>
    [Fact]
    public void A_form_needs_one_to_ten_boxes()
    {
        var eleven = Enumerable.Range(0, 11).Select(i => Check("Box " + i, false)).ToArray();
        Assert.Throws<ArgumentException>(() => UiDialogs.Form(_owner, Guid, Plain, "T", new DialogFormOptions { Checks = eleven }, _ => { }));
        Assert.Throws<ArgumentException>(() => UiDialogs.Form(_owner, Guid, Plain, "T", new DialogFormOptions(), _ => { }));
        Assert.Null(DialogForm.CountProblem(new[] { Check("a", false) }));
        Assert.Equal("[x] Blood", Check(" Blood ", true).ToString());
        Assert.False(DialogForm.Satisfied(new[] { Check("a", false) }, requireOne: true));
        Assert.True(DialogForm.Satisfied(new[] { Check("a", false) }, requireOne: false));
        Assert.True(DialogForm.Satisfied(Array.Empty<DialogCheck>(), requireOne: true));
    }

    // ---- Choose -------------------------------------------------------------------------------

    /// <summary>
    /// LISTROWS1: a Choose with option notes draws each option as a full-width two-line list row under its heading; the
    /// whole row is the click target (pointer, <c>click_at</c>), and <c>dialog_choose</c> with an index reaches it too.
    /// </summary>
    [Fact]
    public void Choose_with_notes_draws_two_line_rows_under_headings_and_the_row_is_the_target()
    {
        int picked = -1;
        DialogContent Content() => new()
        {
            Shape = UiDialogShape.Choose,
            Title = "Backups",
            Options = new[] { "Before restore · Wed, Oct 7, 2026 1:39 AM", "Wipe archive · Tue, Oct 6, 2026 11:37 PM" },
            OptionNotes = new[] { "World age: day 1 · 22 MB", "World age: day 6 · 21 MB" },
            OptionHeadings = new[] { new DialogOptionHeading(0, "Progenitor backups"), new DialogOptionHeading(1, "Wipe archives") },
            CancelCaption = "Close",
            FrameWidth = DialogLayout.WideFrameWidth,
        };

        var dialog = UiDialog.OpenContent(t => UiApi.Panel(t, _owner, Guid), Content(), "test", "list", "").OnPick(i => picked = i);
        var view = dialog.View!;
        // Two options would fit the button row; with notes they are rows all the same.
        Assert.Equal(2, view.OptionRows.Count);
        Assert.Equal(new[] { UiDialogs.CellCancel }, view.Buttons!.Cells.Where(c => c.Kind == CellKind.Button).Select(c => c.Key).ToArray());
        Assert.Equal(new[] { UiDialogs.OptionHeadingRow(0), UiDialogs.OptionHeadingRow(1) }, view.OptionHeadingRows.Select(r => r.Key).ToArray());
        var rows = view.Rows.Select(r => r is IUiCellRow c ? c.Key : "").Where(k => k.Length > 0).ToList();
        Assert.True(rows.IndexOf(UiDialogs.OptionHeadingRow(1)) < rows.IndexOf(UiDialogs.OptionRow(1)));
        var first = (UiCellRowHandle)view.OptionRows[0];
        var text = first.Cells.Single(c => c.Key == UiDialogs.OptionKey(0));
        Assert.Equal(CellWrap.TwoLine, text.Wrap);
        Assert.Equal("Before restore · Wed, Oct 7, 2026 1:39 AM\nWorld age: day 1 · 22 MB", text.Text);
        Assert.True(first.RowClickReachable);

        // The pointer: the row's own click slot.
        UiSlotTable.BootstrapDispatch(((UiHandle)view.OptionRows[1]).WidgetId, (byte)UiSlotKind.Click, 0f, false, null);
        Assert.Equal(1, picked);
        Assert.Null(UiDialog.Current);

        // The bridge: an option index presses the same slot.
        UiDialog.OpenContent(t => UiApi.Panel(t, _owner, Guid), Content(), "test", "list", "").OnPick(i => picked = i);
        var answer = UiDialog.ChooseForBridge("0");
        Assert.Equal(0, picked);
        Assert.Equal(true, answer["answered"]);
        Assert.Equal(UiDialogs.OptionRow(0), answer["label"]);
    }

    /// <summary>The Logs saved dialog: Not now left, the two links as buttons; a link button opens its link, Not now nothing.</summary>
    [Fact]
    public void The_Logs_saved_dialog_offers_the_links_as_buttons_and_Not_now_opens_nothing()
    {
        var opened = new List<string>();
        try
        {
            Progenitor.Support.SupportActions.OpenUrl = url => { opened.Add(url); return true; };
            var offer = Progenitor.Support.SupportActions.BuildOffer("a.zip", 2048, "your Desktop", false,
                "https://vrisingmods.com/discord", "https://discord.com/channels/1/2")!;
            SupportOfferDialog.Show(offer);
            var dialog = UiDialog.Current!;
            // The two link captions are too wide for one row with Not now at one width, so the kit draws them as its
            // vertical option list (one 360-unit button per row) above the Not now row.
            Assert.Equal(2, dialog.View!.OptionRows.Count);
            Assert.Equal(new[] { UiDialogs.CellCancel },
                dialog.View.Buttons!.Cells.Where(c => c.Kind == CellKind.Button).Select(c => c.Key).ToArray());
            Assert.Equal("Not now", dialog.View.Buttons.Cells.Single(c => c.Key == UiDialogs.CellCancel).Text);
            Assert.Equal("Logs saved", dialog.Content.Title);
            Assert.Equal(true, UiDialog.ChooseForBridge("Open #technical-support")["answered"]);
            Assert.Equal(new[] { "https://discord.com/channels/1/2" }, opened);

            // Not now (the bridge's no, and what Escape / B pick) closes it and opens nothing.
            SupportOfferDialog.Show(offer);
            Assert.Equal(true, UiDialog.ChooseForBridge("no")["answered"]);
            Assert.Null(UiDialog.Current);
            Assert.Single(opened);
            SupportOfferDialog.Show(offer);
            Assert.True(UiDialog.NotifyEscape());
            Assert.Null(UiDialog.Current);
            Assert.Single(opened);
        }
        finally { Progenitor.Support.SupportActions.ResetForTests(); }
    }

    /// <summary>Up to three short options are buttons (Cancel left); more, or long ones, are a vertical list above Cancel.</summary>
    [Fact]
    public void Choose_draws_short_lists_as_buttons_and_longer_ones_as_a_list()
    {
        int picked = -1, cancelled = 0;
        var few = UiDialogs.Choose(_owner, Guid, Plain, "Pet", "", new[] { "Wolf", "Bear" }, i => picked = i, "Cancel", () => cancelled++);
        Assert.Empty(few.View!.OptionRows);
        Assert.Equal(new[] { UiDialogs.CellCancel, "option-0", "option-1" },
            few.View.Buttons!.Cells.Where(c => c.Kind == CellKind.Button).Select(c => c.Key).ToArray());
        Assert.True(_runtime.Click(UiDialogs.ButtonsRow, "option-1"));
        Assert.Equal(1, picked);

        var many = UiDialogs.Choose(_owner, Guid, Plain, "Biome", "", new[] { "Farbane", "Dunley", "Silverlight", "Gloomrot", "Mortium" }, i => picked = i, "Cancel", () => cancelled++);
        Assert.Equal(5, many.View!.OptionRows.Count);
        Assert.Equal(new[] { UiDialogs.CellCancel }, many.View.Buttons!.Cells.Where(c => c.Kind == CellKind.Button).Select(c => c.Key).ToArray());
        Assert.True(_runtime.Click(UiDialogs.OptionRow(3), "option-3"));
        Assert.Equal(3, picked);

        Assert.False(DialogLayout.OptionsAsButtons(new[] { "A much longer option caption", "Another long option caption" }, "Cancel"));
        Assert.True(DialogLayout.OptionsAsButtons(new[] { "Yes", "No", "Maybe" }, "Cancel"));
        Assert.False(DialogLayout.OptionsAsButtons(new[] { "1", "2", "3", "4" }, "Cancel"));

        UiDialogs.Choose(_owner, Guid, Plain, "Pet", "", new[] { "Wolf" }, i => picked = i, "Cancel", () => cancelled++);
        UiApi.PressKeyForTests(27, true);
        Assert.Equal(1, cancelled);
    }

    // ---- footer -------------------------------------------------------------------------------

    /// <summary>A mod's dialog names the mod by default; the opt-out gives the plain line; a long name is cut, never a version.</summary>
    [Fact]
    public void The_mod_footer_is_on_by_default_can_be_turned_off_and_cuts_the_name_not_the_versions()
    {
        Assert.Equal("Progenitor Sample 2026.9.66 · Progenitor 2026.9.67", UiDialogs.Footer("Progenitor Sample", "2026.9.66", true));
        Assert.Equal("Progenitor 2026.9.67", UiDialogs.Footer("Progenitor Sample", "2026.9.66", false));

        UiDialogs.Notice(_owner, Guid, UiDialogs.Footer("Progenitor Sample", "2026.9.66", true), "Hi", "", "OK", null);
        Assert.Equal("Progenitor Sample 2026.9.66 · Progenitor 2026.9.67", UiDialog.Current!.Panel!.Footer);
        Assert.Equal("Progenitor Sample 2026.9.66 · Progenitor 2026.9.67", UiDialog.Describe()["footer"]);

        string longName = string.Concat(Enumerable.Repeat("Extraordinarily Long Mod Name ", 8)).Trim();
        string cut = DialogLayout.ModFooter(longName, "1.2.3", "Progenitor 2026.9.67");
        Assert.EndsWith("… 1.2.3 · Progenitor 2026.9.67", cut);
        Assert.StartsWith("Extraordinarily", cut);
        Assert.True(DialogLayout.FooterWidth(cut) <= PanelFooterLayout.TextWidth(DialogLayout.FrameWidth) + .01f);
        Assert.DoesNotContain('\n', cut);

        // A framework dialog keeps the plain line.
        UiDialog.ForgetForTests();
        UiApi.Dialog("Reset Sample to its defaults?", "", "Yes, reset", LabelTone.Orange, "No");
        Assert.Equal(Plain, UiDialog.Current!.Panel!.Footer);
    }

    // ---- migrated callers ---------------------------------------------------------------------

    /// <summary>
    /// The framework's confirmations keep their words, their tones and their handles on the new
    /// layout: title and body as dialog text, No left and Yes right under the caller's keys, and a
    /// ConfirmButton's question (with its new optional body).
    /// </summary>
    [Fact]
    public void Migrated_callers_keep_their_wording_tones_and_handles()
    {
        var dialog = UiApi.Dialog(
            "Install HotDemo 0.1.0?", "Client and server mod by Progenitor, version 0.1.0.", "Yes, install", LabelTone.Ok, "No",
            tag: "prompt", kind: "install", key: "Progenitor-HotDemo", rowKey: "prompt:Progenitor-HotDemo", yesKey: "promptYes", noKey: "promptNo");
        Assert.Equal("Install HotDemo 0.1.0?", dialog.Prompt.Heading!.Text);
        Assert.Equal("Client and server mod by Progenitor, version 0.1.0.", dialog.Prompt.Body!.Text);
        var cells = dialog.Prompt.Buttons!.Cells.Where(c => c.Kind == CellKind.Button).ToList();
        Assert.Equal(new[] { ("promptNo", "No"), ("promptYes", "Yes, install") }, cells.Select(c => (c.Key, c.Text)).ToArray());
        Assert.Equal(CellTone.Ok, cells[1].Tone);
        Assert.Equal(cells[0].Width.Value, cells[1].Width.Value, 2);

        UiDialog.ForgetForTests();
        UiDialog.Clock = () => _now;
        var panel = UiApi.Panel("Page", _owner, Guid);
        var button = panel.ConfirmButton("Log a line", "Write a line to the Sample log?", "It goes to the BepInEx log.");
        int ran = 0;
        button.OnClick(() => ran++);
        ((UiButtonHandle)button).Pressed();
        var confirm = UiDialog.Current!;
        Assert.Equal("Write a line to the Sample log?", confirm.Prompt.Heading!.Text);
        Assert.Equal("It goes to the BepInEx log.", confirm.Prompt.Body!.Text);
        Assert.Equal(new[] { "No", "Log a line" },
            confirm.Prompt.Buttons!.Cells.Where(c => c.Kind == CellKind.Button).Select(c => c.Text).ToArray());
        Later();
        UiDialog.NotifyEnter();
        Assert.Equal(1, ran);
    }

    /// <summary>
    /// PLAINDENY1 on the dialog layout: the refusal's summary lines, "Show details" on the left and
    /// Close (the main action) on the right; details on request rebuild the panel with the
    /// technical lines under the summary, and Close closes it.
    /// </summary>
    [Fact]
    public void The_join_refusal_panel_uses_the_dialog_layout_and_keeps_details_on_request()
    {
        ModManifestDeny.ResetForTests();
        try
        {
            var report = new ModDenyReport(
                "Cannot join — server mods", "127.0.0.1:9976", "Night Garden", "Ada", "fp", descriptorReceived: false,
                new[] { new ModDenyRow("progenitor.sample", "1.0.0", null, ModDenyOrigin.ServerRequires, "the server requires it") });
            ModManifestDenyPanel.Open(report);
            Assert.True(ModManifestDenyPanel.IsOpen);
            Assert.Contains(("Can't join Night Garden", true), _runtime.DialogTexts);
            var buttons = _runtime.Row(ModManifestDenyPanel.ButtonsRow)!;
            Assert.Equal(new[] { ("details", ModDenyText.ShowDetailsButton), ("close", ModDenyText.CloseButton) },
                buttons.Cells().Where(c => c.Kind == CellKind.Button).Select(c => (c.Key, c.Text)).ToArray());
            int labelsBefore = _runtime.Labels;

            Assert.True(_runtime.Click(ModManifestDenyPanel.ButtonsRow, ModManifestDenyPanel.CellDetails));
            Assert.True(ModManifestDenyPanel.IsOpen);
            Assert.True(_runtime.Labels - labelsBefore > ModDenyText.Summary(report).Count, "details add the technical lines");
            Assert.Equal(ModDenyText.HideDetailsButton,
                _runtime.Row(ModManifestDenyPanel.ButtonsRow)!.Cells().Single(c => c.Key == ModManifestDenyPanel.CellDetails).Text);

            Assert.True(_runtime.Click(ModManifestDenyPanel.ButtonsRow, ModManifestDenyPanel.CellClose));
            Assert.False(ModManifestDenyPanel.IsOpen);
        }
        finally
        {
            ModManifestDenyPanel.Close();
            ModManifestDeny.ResetForTests();
        }
    }

    // ---- custom pages -------------------------------------------------------------------------

    /// <summary>
    /// A custom page's controls are drawn on the settings grid; its labels, notes and plain buttons
    /// are not. A fact is a grid row too (GRIDROW1), its name and value on the columns the controls use.
    /// The Mods workspace turns it on for every custom page it builds.
    /// </summary>
    [Fact]
    public void Custom_page_controls_use_the_settings_grid_and_labels_do_not()
    {
        var panel = (UiPanelHandle)UiApi.Panel("Custom", _owner, Guid);
        panel.GridControls = true;
        panel.Label("Intro");
        panel.Toggle("Enabled", true);
        panel.Button("Run");
        panel.Slider("Volume", 0f, 1f, .5f);
        panel.Input("Name", "");
        ((IUiPanel)panel).Dropdown("Mode", new[] { "A", "B" }, "A");
        ((IUiPanel)panel).Number("Count", 1, 0, 10);
        ((IUiPanel)panel).Radio("Style", new[] { "Short", "Full" }, "Short");
        ((IUiPanel)panel).Info("Time", () => "now", 0);
        panel.Fact("Plugin", "progenitor.sample");
        Assert.Equal(
            new[] { "toggle", "slider", "input", "choice", "numeric", "options", "info", "fact" },
            _runtime.GridAdds.ToArray());
        Assert.False(_runtime.GridOn, "the grid is switched off after each control");

        string open = SourcePin.MethodBody(
            SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "ModsMenu.cs"),
            "public static IUiPanel OpenPage(");
        SourcePin.AssertStatementExactly(open, "if (panel is UiPanelHandle pageHost) pageHost.GridControls = true;");
    }

    // ---- review round 2 -----------------------------------------------------------------------

    /// <summary>
    /// Live finding: Choose had ~40 units more between its body and its buttons than Confirm. A
    /// dialog text row is its text plus <see cref="DialogLayout.TextPad"/>, not the 40-unit panel
    /// floor, so the space under a one-line body is the same as under a two-line one; and a Choose
    /// has exactly the rows a Confirm and a Notice have (title, body, buttons), nothing between.
    /// </summary>
    [Fact]
    public void Choose_has_the_same_body_to_buttons_gap_as_Confirm_and_Notice()
    {
        float oneLine = DialogLayout.TextRowHeight(DialogLayout.BodyLineHeight, title: false);
        float twoLines = DialogLayout.TextRowHeight(DialogLayout.BodyLineHeight * 2f, title: false);
        Assert.Equal(DialogLayout.BodyLineHeight + DialogLayout.TextPad, oneLine, 2);
        Assert.True(oneLine < UnityUiRuntimeMetrics.RowHeight, "a one-line body is not padded up to a 40-unit row");
        Assert.Equal(oneLine - DialogLayout.BodyLineHeight, twoLines - 2f * DialogLayout.BodyLineHeight, 2);

        // The panel arithmetic honors the text row's own height.
        var layout = new PanelLayout();
        layout.NextRow(oneLine, DialogLayout.MinTextRowHeight);
        float buttonsTop = layout.NextRow(DialogLayout.ButtonRowHeight, PanelLayout.MinRowHeight);
        Assert.Equal(PanelLayout.PadTop + oneLine + PanelLayout.RowGap, buttonsTop, 2);

        string Kinds(UiDialog d) => string.Join(",", d.View!.Rows.Select(r => r == d.View.Buttons ? "buttons" : r == d.View.Heading ? "title" : r == d.View.Body ? "body" : "other"));
        var confirm = Confirm();
        string confirmKinds = Kinds(confirm);
        confirm.Close();
        var notice = UiDialogs.Notice(_owner, Guid, Plain, "T", "B", "OK", null);
        Assert.Equal(confirmKinds, Kinds(notice));
        notice.Close();
        var choose = UiDialogs.Choose(_owner, Guid, Plain, "T", "B", new[] { "Wolf", "Bear", "Bat" }, _ => { }, "Cancel", null);
        Assert.Equal("title,body,buttons", Kinds(choose));
        Assert.Equal(confirmKinds, Kinds(choose));
    }

    /// <summary>Enter never answers a destructive confirmation, and never while another text field (the chat) has the keyboard.</summary>
    [Fact]
    public void Enter_skips_a_danger_dialog_and_a_field_outside_the_dialog()
    {
        int yes = 0;
        var danger = Confirm(yes: () => yes++, danger: true);
        Later();
        Assert.False(UiDialog.NotifyEnter());
        Assert.True(danger.IsOpen);
        Assert.True(_runtime.Click(UiDialogs.ButtonsRow, UiDialogs.CellMain), "the click still answers it");
        Assert.Equal(1, yes);

        Confirm(yes: () => yes++);
        Later();
        Assert.False(UiDialog.NotifyEnter(typingElsewhere: true));
        Assert.Equal(1, yes);
        Assert.True(UiDialog.NotifyEnter());
        Assert.Equal(2, yes);
    }

    /// <summary>
    /// A dialog whose panel something else hid or disposed is dropped (no callback) and the queue moves
    /// on; <c>IsOpen</c> is false once the panel is disposed.
    /// </summary>
    [Fact]
    public void A_dialog_whose_panel_left_the_screen_is_dropped_and_the_queue_advances()
    {
        int calls = 0;
        var first = UiDialogs.Notice(_owner, Guid, Plain, "First", "", "OK", () => calls++);
        var second = UiDialogs.Notice(_owner, Guid, Plain, "Second", "", "OK", () => calls++);
        first.Panel!.Hide();
        Assert.False(first.IsOpen);
        Assert.True(second.IsOpen);
        second.Panel!.Dispose();
        Assert.False(second.IsOpen);
        Assert.Null(UiDialog.Current);
        Assert.Equal(0, calls);
    }

    /// <summary>
    /// A framework dialog opened by a handler that Escape ran is shown after the Escape sweep, not
    /// shown and hidden by it; and a framework dialog that fails to show hands the screen back to
    /// the mod's dialog it displaced.
    /// </summary>
    [Fact]
    public void Framework_dialogs_survive_the_Escape_sweep_and_a_failed_show_gives_the_screen_back()
    {
        UiDialog? reopened = null;
        Confirm(no: () => reopened = UiApi.Dialog("Still there?", "", "Yes", LabelTone.Ok, "No"));
        UiApi.PressKeyForTests(27, true);
        Assert.NotNull(reopened);
        Assert.True(reopened!.IsOpen, "shown after the sweep, not hidden by it");
        Assert.Equal(UiHandleState.Shown, reopened.Panel!.State);
        reopened.Close();

        var mod = UiDialogs.Notice(_owner, Guid, Plain, "Mod", "", "OK", null);
        var failed = UiDialog.Open(_ => throw new InvalidOperationException("no panel"), "t", "k", "", "Boom", "", "Yes", LabelTone.Ok, "No");
        Assert.False(failed.IsOpen);
        Assert.True(mod.IsOpen, "the displaced mod dialog is back");
    }

    /// <summary>
    /// A long body never pushes the buttons off screen: the body is capped so the frame stays inside
    /// 80 % of the screen with the button row and footer in it; the full text stays in the readback.
    /// </summary>
    [Theory]
    [InlineData(1080f, 0)]
    [InlineData(1080f, 10)]
    [InlineData(1024f, 0)]
    public void A_long_body_is_capped_so_the_buttons_and_footer_stay_on_screen(float screen, int listOptions)
    {
        float band = PanelFooterLayout.BandHeight;
        float title = DialogLayout.TextRowHeight(DialogLayout.TitleLineHeight * 3f, title: true);
        var others = new List<float> { title };
        for (int i = 0; i < listOptions; i++) others.Add(DialogLayout.ListRowHeight);
        others.Add(DialogLayout.ButtonRowHeight);
        int rows = others.Count + 1;
        float cap = DialogLayout.BodyCap(screen, band, others.Sum(), rows);
        float body = DialogLayout.TextRowHeight(DialogLayout.BodyLineHeight * 200f, title: false, cap);
        Assert.True(body <= cap + .01f);
        var all = new List<float> { title, body };
        all.AddRange(others.Skip(1));
        float frame = DialogLayout.FrameHeight(DialogLayout.ContentHeight(all), band, screen);
        Assert.True(DialogLayout.FrameTopPad + DialogLayout.ContentHeight(all) + band <= screen * PanelLayout.ScreenFraction + .5f,
            "the content fits the frame cap, so the buttons and footer are not scrolled away");
        Assert.True(frame <= screen * PanelLayout.ScreenFraction + .01f);

        string longBody = string.Concat(Enumerable.Repeat("A very long paragraph. ", 400));
        var d = UiDialogs.Notice(_owner, Guid, Plain, "Long", longBody, "OK", null);
        Assert.Equal(longBody, d.Prompt.Body!.Text);
        d.Close();
        Assert.Throws<ArgumentException>(() => UiDialogs.Choose(_owner, Guid, Plain, "Too many", "",
            Enumerable.Range(0, DialogLayout.MaxListOptions + 1).Select(i => "Option " + i).ToList(), _ => { }, "Cancel", null));
    }

    /// <summary>A Prompt's initial text longer than maxLength is cut in the field and in the value alike; a blank validator error refuses.</summary>
    [Fact]
    public void Prompt_clamps_initial_to_maxLength_and_a_blank_error_is_not_accepted()
    {
        string? got = null;
        var prompt = UiDialogs.Prompt(_owner, Guid, Plain, "Name", "", "abcdefghij", s => got = s, "", _ => "  ", "OK", 4, "Cancel", null);
        Assert.Equal("abcd", prompt.Text);
        Assert.Equal("abcd", prompt.View!.Input!.Cells.Single(c => c.Key == UiDialogs.CellField).Text);
        _runtime.Click(UiDialogs.ButtonsRow, UiDialogs.CellMain);
        Assert.True(prompt.IsOpen);
        Assert.Null(got);
        Assert.Equal(UiDialogs.NotAccepted, prompt.Error);
    }

    /// <summary>The join-refusal panel closes on Enter (Close is its main action) and a build that throws after the chrome tears the panel down.</summary>
    [Fact]
    public void The_join_refusal_panel_closes_on_Enter_and_a_failed_build_leaves_no_backdrop()
    {
        ModManifestDeny.ResetForTests();
        var report = new ModDenyReport(
            "Cannot join — server mods", "127.0.0.1:9976", "Night Garden", "Ada", "fp", descriptorReceived: false,
            new[] { new ModDenyRow("progenitor.sample", "1.0.0", null, ModDenyOrigin.ServerRequires, "the server requires it") });
        var original = ModManifestDenyPanel.BuildView;
        try
        {
            ModManifestDenyPanel.Open(report);
            Assert.False(UiDialog.NotifyEnter(), "inside the grace");
            Later();
            Assert.True(UiDialog.NotifyEnter());
            Assert.False(ModManifestDenyPanel.IsOpen);
            Assert.Null(UiDialog.EnterFallback);

            ModManifestDenyPanel.BuildView = (p, c) =>
            {
                UiDialogView.Build(p, c);
                throw new InvalidOperationException("half built");
            };
            var panel = ModManifestDenyPanel.Open(report);
            Assert.True(panel.IsDisposed, "the chromed panel (and its backdrop) is torn down");
            Assert.False(ModManifestDenyPanel.IsOpen);
            Assert.False(ModManifestDenyPanel.HasHeldPanel);
        }
        finally
        {
            ModManifestDenyPanel.BuildView = original;
            ModManifestDenyPanel.Close();
            ModManifestDeny.ResetForTests();
        }
    }

    /// <summary>
    /// The Sample's Example page: its five controls are drawn on the settings grid and its labels and
    /// button are not; and the columns the runtime gives them (<see cref="RowColumnRules"/>, the
    /// decision <c>UnityPanel.RowColumnsFor</c> makes) put every control in one column at the same x,
    /// the settings page's, at both live detail-pane widths.
    /// </summary>
    [Theory]
    [InlineData(1248f)]
    [InlineData(1108f)]
    public void The_Sample_custom_page_controls_share_the_settings_grid_geometry(float paneRowWidth)
    {
        var panel = (UiPanelHandle)UiApi.Panel("Example", _owner, Guid);
        panel.GridControls = true;
        var logged = new List<string>();
        Progenitor.Sample.SampleMenu.BuildExamplePage(panel, hints: true, limit: 10, _ => { }, logged.Add);
        Assert.Equal(new[] { "toggle", "slider", "input", "choice", "numeric" }, _runtime.GridAdds.ToArray());

        // MENUKIT2: the settings grid is the game's Options row (ModSettingsRowGrid).
        var rowGrid = ModSettingsRowGrid.For(paneRowWidth);
        var grid = rowGrid.Columns(hasControl: true);
        foreach (string kind in new[] { "toggle", "slider", "input", "choice", "numeric" })
        {
            var c = RowColumnRules.For(kind, grid: true, hasControl: true, searchField: false, menuPane: true, paneRowWidth);
            Assert.Equal(grid.ControlX, c.ControlX, 2);
            Assert.Equal(grid.ControlWidth, c.ControlWidth, 2);
            // Every name starts at the same x.
            Assert.Equal(grid.LabelX - grid.LabelWidth * 0.5f, c.LabelX - c.LabelWidth * 0.5f, 2);
            Assert.True(RowColumnRules.CaptionLeft(kind, grid: true) == true, kind + ": the name is left-aligned");
            if (kind == "toggle")
            {
                // TOGGLEFIT1: a checkbox row's name runs up to one gap before the checkbox, not only to the column's start.
                float checkbox = rowGrid.FromCenter(rowGrid.ControlSlot(SettingsControlShape.Checkbox).Left);
                Assert.Equal(checkbox - ModSettingsRowGrid.Gap, c.LabelRight, 2);
                continue;
            }

            Assert.Equal(grid.LabelX, c.LabelX, 2);
            Assert.True(c.LabelRight <= c.ControlLeft, kind + ": the name ends before the control column");
        }

        var label = RowColumnRules.For("label", grid: false, hasControl: false, searchField: false, menuPane: true, paneRowWidth);
        Assert.Equal(MenuPanelLayout.Columns(paneRowWidth, false).LabelWidth, label.LabelWidth, 2);
        Assert.Null(RowColumnRules.CaptionLeft("label", grid: false));
    }

    /// <summary>
    /// The pair is centered against the width the runtime really lays a dialog's rows out at: the
    /// frame less the viewport insets the layout pass forces for a dialog, with the rows centered in
    /// that viewport — at both live screen sizes.
    /// </summary>
    [Theory]
    [InlineData(1600f, 1024f)]
    [InlineData(3840f, 1080f)]
    public void The_button_row_is_centred_against_the_runtime_content_width(float screenW, float screenH)
    {
        float scale = screenH / DialogLayout.CanvasHeight;
        float rowWidth = DialogLayout.ContentRowWidth(DialogLayout.FrameWidth);
        Assert.True(rowWidth <= DialogLayout.FrameWidth - DialogLayout.ViewportInsetLeft - DialogLayout.ViewportInsetRight + .01f);
        Confirm();
        var spec = new CellRowSpec(UiDialogs.ButtonsRow, UiDialog.Current!.View!.Buttons!.Cells.ToList());
        var placed = MenuPanelLayout.CellColumns(rowWidth, spec).Where(p => p.Key == UiDialogs.CellCancel || p.Key == UiDialogs.CellMain).ToList();
        float frameLeftOnScreen = screenW / 2f - DialogLayout.FrameWidth * scale / 2f;
        float viewportCentre = frameLeftOnScreen + (DialogLayout.ViewportInsetLeft + (DialogLayout.FrameWidth - DialogLayout.ViewportInsetLeft - DialogLayout.ViewportInsetRight) / 2f) * scale;
        float groupCentre = viewportCentre + (placed[0].Left + placed[1].Right) / 2f * scale;
        Assert.Equal(screenW / 2f, groupCentre, 1);
        Assert.True(placed[0].Left >= -rowWidth / 2f && placed[1].Right <= rowWidth / 2f);
    }

    /// <summary>A runtime that records dialog text, the chrome ask, grid use, and hosts clickable, typeable cell rows.</summary>
    // ---- DIALOGS2: the facts block ---------------------------------------------------------------

    private static readonly DialogFact[] SampleFacts =
    {
        new DialogFact("Mod", "RustyRepairs 1.2.2 by iTestTeam"),
        new DialogFact("Server", "Progenitor Forms Test"),
        new DialogFact("Takes effect", "At the next server restart"),
        new DialogFact("Requires", "ExampleCommandFramework 0.10.4 — installed on that server", DialogFactTone.Ok),
        new DialogFact("", "KinsmanCommands — will be installed alongside"),
    };

    /// <summary>
    /// The label column is as wide as the widest label (with the button caption slack), every label
    /// starts at the same left edge (the block's inset), and every value starts at the same left
    /// edge one gap after the label column — a left-aligned two-column block, whatever each label's
    /// own length, including a continuation row with no label.
    /// </summary>
    /// <summary>
    /// FIX-NONMODS (live 2026-10-03): the install confirmation's lone "Mod" label drew "M…" — the
    /// column was the glyph estimate alone, narrower than the text cell's padded room. A short label
    /// now always gets its padding and a floor that holds a short word, at the fact face.
    /// </summary>
    [Fact]
    public void A_short_fact_label_is_never_cut()
    {
        foreach (string label in new[] { "Mod", "Mods", "Server", "Size", "From" })
        {
            var facts = new[] { new DialogFact(label, "Almanacs 2.42.94") };
            float width = DialogFacts.LabelWidth(facts, DialogLayout.ContentRowWidth());
            Assert.True(width >= DialogLayout.FactLabelMinWidth, label + ": " + width);
            float glyphs = CellLayout.EstimateTextWidth(label, DialogLayout.FactFontSize);
            Assert.True(width >= glyphs + 2f * DialogLayout.TextPad, label + " has no room for its padding: " + width);
            var spec = DialogFacts.RowSpec(0, facts[0], width, DialogLayout.ContentRowWidth());
            var cell = spec.Cells.Single(c => c.Key == DialogFacts.CellLabel);
            Assert.True(CellLayout.CaptionFits(cell, width), label + " would be ellipsized at " + width);
        }
    }

    [Fact]
    public void Facts_are_two_left_aligned_columns_sized_to_the_widest_label()
    {
        float row = DialogLayout.ContentRowWidth();
        float labelWidth = DialogFacts.LabelWidth(SampleFacts, row);
        float widest = SampleFacts.Max(f => CellLayout.EstimateTextWidth(f.Label, DialogLayout.FactFontSize) * DialogLayout.CaptionSlack);
        // FIX-NONMODS: plus the label cell's own padding, never under the floor.
        Assert.Equal((float)Math.Ceiling(Math.Max(widest + DialogLayout.FactLabelPad, DialogLayout.FactLabelMinWidth)), labelWidth);
        Assert.True(labelWidth > CellLayout.EstimateTextWidth("Mod", DialogLayout.FactFontSize));

        float rowLeft = -row / 2f;
        var labelLefts = new List<float>();
        var valueLefts = new List<float>();
        for (int i = 0; i < SampleFacts.Length; i++)
        {
            var spec = DialogFacts.RowSpec(i, SampleFacts[i], labelWidth, row);
            Assert.True(spec.Plain);
            Assert.Equal(DialogFacts.RowKey(i), spec.Key);
            var label = spec.Cells.Single(c => c.Key == DialogFacts.CellLabel);
            var value = spec.Cells.Single(c => c.Key == DialogFacts.CellValue);
            Assert.Equal(CellAlign.Left, label.Align);
            Assert.Equal(CellAlign.Left, value.Align);
            Assert.Equal(CellTone.Muted, label.Tone);
            var placed = MenuPanelLayout.CellColumns(row, spec);
            var l = placed.Single(p => p.Key == DialogFacts.CellLabel);
            var v = placed.Single(p => p.Key == DialogFacts.CellValue);
            Assert.Equal(labelWidth, l.Width, 2);
            labelLefts.Add(l.Left);
            valueLefts.Add(v.Left);
            // The value fills to the right inset, mirroring the left one (inset plus one cell gap).
            Assert.Equal(row / 2f - DialogLayout.FactInset - CellLayout.Gap, v.Right, 1);
        }

        Assert.All(labelLefts, x => Assert.Equal(rowLeft + DialogLayout.FactInset + CellLayout.Gap, x, 1));
        Assert.All(valueLefts, x => Assert.Equal(labelLefts[0] + labelWidth + CellLayout.Gap, x, 1));
        // The continuation row keeps the column: an empty label, the same value edge.
        Assert.Equal("", DialogFacts.RowSpec(4, SampleFacts[4], labelWidth, row).Cells.Single(c => c.Key == DialogFacts.CellLabel).Text);
        Assert.Equal(CellTone.Ok, DialogFacts.RowSpec(3, SampleFacts[3], labelWidth, row).Cells.Single(c => c.Key == DialogFacts.CellValue).Tone);
    }

    [Fact]
    public void A_very_long_label_is_capped_and_a_long_value_wraps_into_a_taller_row()
    {
        float row = DialogLayout.ContentRowWidth();
        var facts = new[] { new DialogFact("A label much longer than any label should ever be in a dialog", "x") };
        Assert.Equal((float)Math.Ceiling(DialogFacts.BlockWidth(row) * DialogLayout.FactLabelMaxFraction), DialogFacts.LabelWidth(facts, row));
        Assert.Equal(0f, DialogFacts.LabelWidth(new[] { new DialogFact("", "only a value") }, row));

        float labelWidth = DialogFacts.LabelWidth(SampleFacts, row);
        var shortRow = DialogFacts.RowSpec(0, new DialogFact("Folder", @"C:\server"), labelWidth, row);
        var longRow = DialogFacts.RowSpec(1, new DialogFact("Note", string.Join(" ", Enumerable.Repeat("a long value that wraps", 12))), labelWidth, row);
        Assert.Equal(DialogFacts.RowHeight(1), shortRow.Height);
        Assert.Equal(DialogFacts.RowHeight(DialogLayout.MaxFactLines), longRow.Height);
        Assert.Equal(CellWrap.Wrap, longRow.Cells.Single(c => c.Key == DialogFacts.CellValue).Wrap);
    }

    /// <summary>
    /// FACTWRAP1 (live 2026-10-07: "every 304 days on Any → every 3 days o…", "…with a new start dat…"): a value the
    /// estimate puts on one line still wraps, and its row takes the measured text's height, up to four lines.
    /// </summary>
    [Fact]
    public void A_fact_value_always_wraps_and_its_row_fits_the_measured_text_up_to_four_lines()
    {
        float row = DialogLayout.ContentRowWidth();
        float labelWidth = DialogFacts.LabelWidth(SampleFacts, row);
        var spec = DialogFacts.RowSpec(0, new DialogFact("Change", "every 304 days on Any → every 3 days on Any"), labelWidth, row);
        var value = spec.Cells.Single(c => c.Key == DialogFacts.CellValue);
        Assert.Equal(CellWrap.Wrap, value.Wrap);
        Assert.True(spec.FitWrap);
        Assert.Equal(4, DialogLayout.MaxFactLines);
        // The runtime's measurement decides: two measured lines make a two-line row even when the estimate said one.
        float line = DialogLayout.FactLineHeight;
        Assert.Equal(DialogFacts.RowHeight(1), spec.FitWrapHeight(line * 0.9f, DialogLayout.FactFontSize));
        Assert.Equal(2f * line + CellRowSpec.FitWrapPad, spec.FitWrapHeight(2f * line, DialogLayout.FactFontSize), 2);
        // At most four lines (then the cell ellipsizes; the readback keeps the value whole).
        Assert.Equal(DialogFacts.RowHeight(4), spec.FitWrapHeight(9f * line, DialogLayout.FactFontSize));
        // An unreadable measurement keeps the estimate.
        Assert.Equal(spec.Height, spec.FitWrapHeight(0f, DialogLayout.FactFontSize));
        // Section copies keep the fit bounds.
        var copy = spec.InSection(CellSection.Red, CellSectionEdge.Middle);
        Assert.Equal(spec.FitWrapCeiling, copy.FitWrapCeiling);
        Assert.Equal(spec.FitWrapFloor, copy.FitWrapFloor);
    }

    [Fact]
    public void A_dialog_with_facts_draws_one_row_per_fact_then_the_note_and_reads_them_back()
    {
        var withEmpty = SampleFacts.Concat(new[] { new DialogFact("Folder", "  ") }).ToArray();
        var dialog = UiDialogs.Confirm(_owner, Guid, Plain, "Install RustyRepairs 1.2.2?", "", "Yes, install", () => { }, "No", null, false,
            withEmpty, "The server must be restarted for this to load.");
        Assert.True(dialog.IsOpen);
        Assert.Equal(SampleFacts.Length, dialog.View!.FactRows.Count); // the empty value is left out
        for (int i = 0; i < SampleFacts.Length; i++) Assert.NotNull(_runtime.Row(DialogFacts.RowKey(i)));
        Assert.Null(_runtime.Row(DialogFacts.RowKey(SampleFacts.Length)));
        // Title, then the note as dialog text; no body paragraph.
        Assert.Equal(new[] { ("Install RustyRepairs 1.2.2?", true), ("The server must be restarted for this to load.", false) }, _runtime.DialogTexts.ToArray());
        Assert.NotNull(dialog.View.NoteRow);
        // Row order: facts before the note before the buttons.
        var rows = dialog.View.Rows.ToList();
        Assert.True(rows.IndexOf(dialog.View.FactRows[0]) < rows.IndexOf(dialog.View.NoteRow!));
        Assert.True(rows.IndexOf(dialog.View.NoteRow!) < rows.IndexOf(dialog.View.Buttons!));

        var described = UiDialog.Describe();
        var facts = Assert.IsType<List<Dictionary<string, object?>>>(described["facts"]);
        Assert.Equal(SampleFacts.Select(f => (f.Label, f.Value)).ToArray(),
            facts.Select(f => ((string)f["label"]!, (string)f["value"]!)).ToArray());
        Assert.Equal("ok", facts[3]["tone"]);
        Assert.Equal("The server must be restarted for this to load.", described["note"]);

        dialog.Close();
        var closed = UiDialog.Describe();
        Assert.Empty(Assert.IsType<List<Dictionary<string, object?>>>(closed["facts"]));
        Assert.Equal("", closed["note"]);
    }

    [Fact]
    public void A_dialog_without_facts_is_unchanged()
    {
        var dialog = Confirm();
        Assert.Empty(dialog.View!.FactRows);
        Assert.Null(dialog.View.NoteRow);
        Assert.Empty(Assert.IsType<List<Dictionary<string, object?>>>(UiDialog.Describe()["facts"]));
    }

    // ---- DIALOGS3: Core unload, the bridge's answer, the facts pitch -------------------------------

    /// <summary>
    /// Live 2026-10-01: an install confirm opened before a Core swap was still on screen after it, under
    /// <c>progenitor-ui-unowned/overlay-18</c> with the old version footer, beside the new generation's.
    /// Core unload must close every dialog this generation holds (framework and mod, shown and queued),
    /// run none of their callbacks, show none of the queued ones on its way out (closing one dialog
    /// normally shows the next), and refuse any dialog opened after it.
    /// </summary>
    [Fact]
    public void Core_unload_closes_every_dialog_shown_or_queued_and_shows_none_on_the_way_out()
    {
        int callbacks = 0;
        var other = new object();
        var mod = Confirm("Mod one?", yes: () => callbacks++, no: () => callbacks++);
        var framework = UiApi.Dialog("Install RustyRepairs 1.2.2?", "", "Yes, install", LabelTone.Ok, "No")
            .OnYes(() => callbacks++).OnNo(() => callbacks++);
        var queued = UiDialogs.Confirm(other, "example.dialogs.other", Plain, "Mod two?", "", "Yes", () => callbacks++, "No", () => callbacks++, false);
        Assert.True(framework.IsOpen);
        Assert.True(mod.IsQueued, "the framework dialog put the mod's back in the queue");
        Assert.True(queued.IsQueued);
        var frameworkPanel = framework.Panel!;

        UiApi.Uninstall();

        Assert.Null(UiDialog.Current);
        Assert.Equal(0, UiDialog.QueuedCount);
        Assert.Equal(0, UiDialog.HeldCount);
        foreach (var d in new[] { framework, mod, queued })
        {
            Assert.False(d.IsOpen);
            Assert.False(d.IsQueued);
        }

        Assert.True(frameworkPanel.IsDisposed, "the dialog's panel (its overlay) is destroyed");
        Assert.Equal(0, callbacks);
        Assert.DoesNotContain("dialogs=", UiApi.DescribePins());
        Assert.Contains(_log.Infos, l => l.Contains("Ui: uninstalled") && l.Contains("dialogs=3") && l.Contains("held=0"));

        // Nothing comes back afterwards: a plugin's own sweep, a framework open, a mod's open.
        UiApi.RemoveAll(other);
        var late = UiApi.Dialog("Late?", "", "Yes", LabelTone.Ok, "No");
        var lateMod = Confirm("Late mod?");
        Assert.False(late.IsOpen);
        Assert.False(lateMod.IsOpen);
        Assert.False(lateMod.IsQueued);
        Assert.Null(UiDialog.Current);
        Assert.Null(late.Panel);
    }

    /// <summary>
    /// Live 2026-10-01: <c>mods_ui widget_click label=No</c> answered ok:true and left the install confirm
    /// standing, because the menu matched the first workspace widget whose text contained "No". With a
    /// dialog open, a bare caption names the dialog's button, fired through its own click slot — even when
    /// another visible row shows the same caption.
    /// </summary>
    [Fact]
    public void Widget_click_reaches_the_open_dialogs_button_first()
    {
        int decoy = 0;
        var panel = UiApi.Panel("Decoy", _owner, Guid);
        var row = panel.CellRow(new CellRowSpec("decoy-row", new List<CellSpec>
        {
            new CellSpec(CellKind.Button, "decoy").With(text: "No"),
        }));
        row.OnClick("decoy", () => decoy++);
        panel.Show();

        int yes = 0, no = 0;
        var dialog = UiApi.Dialog("Install RustyRepairs 1.2.2?", "", "Yes, install", LabelTone.Ok, "No")
            .OnYes(() => yes++).OnNo(() => no++);
        Assert.True(dialog.IsOpen);

        var clicked = System.Text.Json.JsonSerializer.SerializeToElement(ModsMenu.ClickVisibleWidget(null, "no", null, null));
        Assert.Equal("dialog", clicked.GetProperty("kind").GetString());
        Assert.Equal(1, clicked.GetProperty("invoked").GetInt32());
        Assert.Equal(1, no);
        Assert.Equal(0, yes);
        Assert.Equal(0, decoy);
        Assert.False(dialog.IsOpen);
        Assert.Null(UiDialog.Current);

        // A caption the dialog does not carry is not swallowed by it.
        var second = UiApi.Dialog("Again?", "", "Yes", LabelTone.Ok, "No").OnYes(() => yes++);
        Assert.False(second.TryClickForBridge("Install", out _));
        Assert.True(second.TryClickForBridge("Yes", out var detail));
        Assert.Equal(1, yes);
        Assert.Equal(1, detail["invoked"]);
        panel.Dispose();
    }

    /// <summary>
    /// <c>mods_ui dialog_choose {choice}</c>: yes / no / cancel / an option index press the open dialog's
    /// button through the same click path, and the readback says it left the screen.
    /// </summary>
    [Fact]
    public void Dialog_choose_presses_the_open_dialogs_buttons()
    {
        Assert.Throws<ArgumentException>(() => UiDialog.ChooseForBridge("yes"));

        int yes = 0, no = 0;
        UiApi.Dialog("Install?", "", "Yes, install", LabelTone.Ok, "No").OnYes(() => yes++).OnNo(() => no++);
        var answer = UiDialog.ChooseForBridge("no");
        Assert.Equal(1, no);
        Assert.Equal(true, answer["answered"]);
        Assert.Equal("no", answer["choice"]);
        Assert.Equal(false, ((Dictionary<string, object?>)answer["dialog"]!)["open"]);

        UiApi.Dialog("Install?", "", "Yes, install", LabelTone.Ok, "No").OnYes(() => yes++).OnNo(() => no++);
        UiDialog.ChooseForBridge("YES");
        Assert.Equal(1, yes);

        int picked = -1, cancelled = 0;
        UiDialogs.Choose(_owner, Guid, Plain, "Pet", "", new[] { "Wolf", "Bear" }, i => picked = i, "Cancel", () => cancelled++);
        Assert.Throws<ArgumentException>(() => UiDialog.ChooseForBridge("yes"));
        Assert.Throws<ArgumentException>(() => UiDialog.ChooseForBridge("2"));
        UiDialog.ChooseForBridge("1");
        Assert.Equal(1, picked);

        // A long list draws its options as rows, not buttons: an index still reaches them.
        UiDialogs.Choose(_owner, Guid, Plain, "Biome", "", new[] { "Farbane", "Dunley", "Silverlight", "Gloomrot", "Mortium" }, i => picked = i, "Cancel", () => cancelled++);
        UiDialog.ChooseForBridge("3");
        Assert.Equal(3, picked);

        UiDialogs.Choose(_owner, Guid, Plain, "Pet", "", new[] { "Wolf", "Bear" }, i => picked = i, "Cancel", () => cancelled++);
        UiDialog.ChooseForBridge("cancel");
        Assert.Equal(1, cancelled);

        // A notice has only OK: no and cancel both press it.
        int closed = 0;
        UiDialogs.Notice(_owner, Guid, Plain, "Done", "", "OK", () => closed++);
        UiDialog.ChooseForBridge("cancel");
        Assert.Equal(1, closed);
        Assert.Null(UiDialog.Current);

        Assert.Throws<ArgumentException>(() => UiDialog.ChooseForBridge("maybe"));
    }

    [Theory]
    [InlineData("{\"action\":\"dialog_choose\",\"choice\":\"No\"}", "no")]
    [InlineData("{\"action\":\"dialog_choose\",\"choice\":\" cancel \"}", "cancel")]
    [InlineData("{\"action\":\"dialog_choose\",\"choice\":2}", "2")]
    [InlineData("{\"action\":\"dialog_choose\",\"choice\":\"0\"}", "0")]
    public void Dialog_choose_parses_its_choice(string json, string choice)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var request = Progenitor.Dev.Ops.ModsUiArgs.Parse(doc.RootElement.Clone());
        Assert.Equal("dialog_choose", request.Action);
        Assert.Equal(choice, request.Choice);
        Assert.False(Progenitor.Dev.Ops.ModsUiArgs.RequiresOpenWorkspace(request.Action), "a dialog can be answered with the workspace closed");
        Assert.Contains("dialog_choose", Progenitor.Dev.Ops.ModsUiArgs.Actions);
    }

    /// <summary>
    /// Live 2026-10-07: <c>dialog_choose {"choice":"OK"}</c> did not close the "Restore requested" notice (its only
    /// button is OK). A button's caption, ignoring case, presses it, and so does index 0 of a one-button dialog; a
    /// choice that matches nothing names the dialog's choices.
    /// </summary>
    [Fact]
    public void Dialog_choose_presses_a_button_by_its_caption_or_screen_index()
    {
        int closed = 0;
        UiDialogs.Notice(_owner, Guid, Plain, "Restore requested", "The server stops soon.", "OK", () => closed++);
        var answer = UiDialog.ChooseForBridge("OK");
        Assert.Equal(1, closed);
        Assert.Equal(true, answer["answered"]);

        UiDialogs.Notice(_owner, Guid, Plain, "Restore requested", "", "OK", () => closed++);
        UiDialog.ChooseForBridge("ok");
        Assert.Equal(2, closed);

        UiDialogs.Notice(_owner, Guid, Plain, "Restore requested", "", "OK", () => closed++);
        var listed = (List<object?>)UiDialog.Describe()["choices"]!;
        var only = (Dictionary<string, object?>)Assert.Single(listed)!;
        Assert.Equal(0, only["index"]);
        Assert.Equal("OK", only["caption"]);
        var error = Assert.Throws<ArgumentException>(() => UiDialog.ChooseForBridge("Close"));
        Assert.Contains("'OK' (0, yes, no, cancel)", error.Message);
        Assert.Throws<ArgumentException>(() => UiDialog.ChooseForBridge("1"));
        Assert.Equal(2, closed);
        UiDialog.ChooseForBridge("0");
        Assert.Equal(3, closed);
        Assert.Null(UiDialog.Current);

        // The bridge's parse keeps a caption (lowercased) for the open dialog to match.
        UiDialogs.Notice(_owner, Guid, Plain, "Restore requested", "", "OK", () => closed++);
        using (var doc = System.Text.Json.JsonDocument.Parse("{\"action\":\"dialog_choose\",\"choice\":\"OK\"}"))
            UiDialog.ChooseForBridge(Progenitor.Dev.Ops.ModsUiArgs.Parse(doc.RootElement.Clone()).Choice);
        Assert.Equal(4, closed);

        // A Confirm's buttons in screen order: No (0), then the main action (1); captions press them too.
        int yes = 0, no = 0;
        UiApi.Dialog("Install?", "", "Yes, install", LabelTone.Ok, "No").OnYes(() => yes++).OnNo(() => no++);
        UiDialog.ChooseForBridge("yes, INSTALL");
        Assert.Equal((1, 0), (yes, no));
        UiApi.Dialog("Install?", "", "Yes, install", LabelTone.Ok, "No").OnYes(() => yes++).OnNo(() => no++);
        UiDialog.ChooseForBridge("1");
        Assert.Equal((2, 0), (yes, no));
        UiApi.Dialog("Install?", "", "Yes, install", LabelTone.Ok, "No").OnYes(() => yes++).OnNo(() => no++);
        UiDialog.ChooseForBridge("0");
        Assert.Equal((2, 1), (yes, no));

        // A Choose: an option by caption; an unknown choice lists the options with their indexes and the Cancel.
        int picked = -1;
        UiDialogs.Choose(_owner, Guid, Plain, "Pet", "", new[] { "Wolf", "Bear" }, i => picked = i, "Cancel", () => { });
        error = Assert.Throws<ArgumentException>(() => UiDialog.ChooseForBridge("Cat"));
        Assert.Contains("choices: 'Wolf' (0), 'Bear' (1), 'Cancel' (no, cancel)", error.Message);
        UiDialog.ChooseForBridge("bear");
        Assert.Equal(1, picked);
    }

    [Theory]
    [InlineData("{\"action\":\"dialog_choose\",\"choice\":\"OK\"}", "ok")]
    [InlineData("{\"action\":\"dialog_choose\",\"choice\":\" Restore now \"}", "restore now")]
    public void Dialog_choose_parses_a_caption(string json, string choice)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal(choice, Progenitor.Dev.Ops.ModsUiArgs.Parse(doc.RootElement.Clone()).Choice);
    }

    /// <summary>
    /// Live 2026-10-07: after <c>dialog_type {"text":"RESTORE"}</c> the readback said <c>mainEnabled: true</c> but the
    /// field still drew its placeholder "Type RESTORE": the bridge reported the change without writing the field. It
    /// now writes the drawn field as typing does (the readback's <c>field</c> is the drawn text), cut at the field's
    /// limit, and refuses a dialog without a field.
    /// </summary>
    [Fact]
    public void Dialog_type_draws_the_text_in_the_field_as_typing_does()
    {
        var content = new DialogContent
        {
            Shape = UiDialogShape.Prompt,
            Title = "Restore the backup?",
            InputPlaceholder = "Type RESTORE",
            MaxLength = 16,
            ConfirmWord = "RESTORE",
            ExtrasNeedWord = true,
            MainCaption = "Restore at next restart",
            MainTone = CellTone.Danger,
            Danger = true,
            CancelCaption = "Cancel",
        };
        content.ExtraButtons.Add(new DialogButton("restore-now", "Restore now", DialogButtonRole.Option, CellTone.Danger));
        var dialog = UiDialog.OpenContent(t => UiApi.Panel(t, _owner, Guid), content, "test", "prompt", "");
        var input = _runtime.Row(UiDialogs.InputRow)!;
        var field = input.Cells().Single(c => c.Key == UiDialogs.CellField);
        var main = _runtime.Row(UiDialogs.ButtonsRow)!.Cells().Single(c => c.Key == content.MainKey);
        Assert.Equal("", field.Text);
        Assert.Equal(CellTone.Disabled, main.Tone);

        var read = (Dictionary<string, object?>)ModsMenu.TypeDialog("RESTORE");
        Assert.Equal("RESTORE", field.Text);
        Assert.Contains(UiDialogs.CellField, input.Applied);
        Assert.Equal("RESTORE", read["typed"]);
        Assert.Equal("RESTORE", read["field"]);
        Assert.Equal(true, read["mainEnabled"]);
        Assert.Equal(CellTone.Danger, main.Tone);

        // A keystroke through the field's own text slot ends in the same state.
        Assert.True(_runtime.Type(UiDialogs.InputRow, UiDialogs.CellField, "restor"));
        Assert.Equal("restor", field.Text);
        Assert.Equal(UiDialog.Describe()["typed"], UiDialog.Describe()["field"]);
        Assert.Equal(CellTone.Disabled, main.Tone);

        // The field takes at most its limit, as the field itself does.
        ModsMenu.TypeDialog(new string('x', 40));
        Assert.Equal(new string('x', 16), field.Text);
        Assert.Equal(field.Text, dialog.Text);

        // Clearing it empties the field (its placeholder shows again) and locks the main action.
        read = (Dictionary<string, object?>)ModsMenu.TypeDialog("");
        Assert.Equal("", field.Text);
        Assert.Equal(false, read["mainEnabled"]);
        dialog.Dispose();

        UiDialogs.Notice(_owner, Guid, Plain, "Restore requested", "", "OK", () => { });
        Assert.Throws<ArgumentException>(() => ModsMenu.TypeDialog("RESTORE"));
    }

    [Theory]
    [InlineData("{\"action\":\"dialog_choose\"}")]
    [InlineData("{\"action\":\"dialog_choose\",\"choice\":\"  \"}")]
    [InlineData("{\"action\":\"dialog_choose\",\"choice\":-1}")]
    [InlineData("{\"action\":\"dialog_choose\",\"choice\":\"-2\"}")]
    public void Dialog_choose_refuses_a_missing_or_unknown_choice(string json)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Throws<ArgumentException>(() => Progenitor.Dev.Ops.ModsUiArgs.Parse(doc.RootElement.Clone()));
    }

    /// <summary>
    /// Live review 2026-10-01: the facts rows were loose and the note sat tight under the last fact. The
    /// row pitch is the game's description pitch (1.2 of the font plus a small pad) and the note row
    /// carries a top margin, with its text at the row's bottom so the margin is above it.
    /// </summary>
    [Fact]
    public void Facts_are_tighter_and_the_note_has_a_top_margin()
    {
        Assert.Equal(DialogLayout.FactFontSize * 1.2f + DialogLayout.FactRowPad, DialogFacts.RowHeight(1), 3);
        Assert.True(DialogFacts.RowHeight(1) < DialogLayout.FactFontSize * 1.3f + DialogLayout.TextPad, "tighter than DIALOGS2's pitch");
        Assert.True(DialogLayout.NoteTopMargin > 0f);
        Assert.True(DialogLayout.IsBodyKind(DialogLayout.KindNote));
        Assert.Equal(RowColumnRules.For(DialogLayout.KindBody, false, false, false, false, 560f).LabelWidth,
            RowColumnRules.For(DialogLayout.KindNote, false, false, false, false, 560f).LabelWidth);
        Assert.Equal(false, RowColumnRules.CaptionLeft(DialogLayout.KindNote, false));

        string runtime = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "UnityUiRuntime.cs");
        string fit = SourcePin.MethodBody(runtime, "private float FitDialogText(");
        SourcePin.AssertUncommented(fit, "height += DialogLayout.NoteTopMargin;");
        string add = SourcePin.MethodBody(runtime, "private IRuntimeWidget? AddDialogTextRow(");
        SourcePin.AssertUncommented(add, "tmp.verticalAlignment = VerticalAlignmentOptions.Bottom;");
        string dialog = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "UiDialog.cs");
        SourcePin.AssertUncommented(dialog, "view.NoteRow = host != null ? host.AddDialogNote(content.Note) : panel.Note(content.Note);");
    }

    /// <summary>Core unload closes the dialogs before anything else in Ui.Uninstall disposes a panel.</summary>
    [Fact]
    public void Ui_uninstall_closes_dialogs_first()
    {
        string ui = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "Ui.cs");
        string body = SourcePin.MethodBody(ui, "internal static void Uninstall()");
        int dialogs = body.IndexOf("dialogs = UiDialog.CloseAllForUnload();", StringComparison.Ordinal);
        int menu = body.IndexOf("ModsMenu.Uninstall();", StringComparison.Ordinal);
        Assert.True(dialogs > 0 && dialogs < menu, "CloseAllForUnload runs before ModsMenu.Uninstall");
        SourcePin.AssertUncommented(body, "int survivors = DisposeLateHandles();");
    }

    private sealed class DialogFakeRuntime : CellRowTests.PlainRuntime, IUiRuntime
    {
        private readonly Dictionary<string, CellRowTests.FakeCellRow> _rows = new(StringComparer.Ordinal);

        public readonly List<(string, bool)> DialogTexts = new();
        public readonly List<string> GridAdds = new();
        public int Headings;
        public int Labels;
        public int ChromeAsks;
        public int Shows;
        public bool GridOn;

        public override void DialogChrome(IRuntimePanel panel) => ChromeAsks++;

        public override void Show(IRuntimePanel panel) => Shows++;

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

        public IRuntimeWidget? AddHeading(IRuntimePanel panel, string text)
        {
            Headings++;
            return new CellRowTests.FakeWidget { Text = text };
        }

        IRuntimeWidget? IUiRuntime.AddLabel(IRuntimePanel panel, string text)
        {
            Labels++;
            return new CellRowTests.FakeWidget { Text = text };
        }

        public void SetSettingsGrid(IRuntimePanel panel, bool on) => GridOn = on;

        IRuntimeWidget? IUiRuntime.AddToggle(IRuntimePanel panel, string text, bool initial) => Note("toggle", text);
        IRuntimeWidget? IUiRuntime.AddSlider(IRuntimePanel panel, string text, float min, float max, float initial) => Note("slider", text);
        IRuntimeWidget? IUiRuntime.AddInput(IRuntimePanel panel, string text, string initial) => Note("input", text);
        IRuntimeWidget? IUiRuntime.AddButton(IRuntimePanel panel, string text) => Note("button", text);

        public IRuntimeWidget? AddChoice(IRuntimePanel panel, string text, IReadOnlyList<UiChoiceOption> options, string value) => Note("choice", text);

        public IRuntimeWidget? AddNumeric(IRuntimePanel panel, string text, double value, double min, double max, double step, int decimals, string units) => Note("numeric", text);

        public IRuntimeWidget? AddOptions(IRuntimePanel panel, string text, IReadOnlyList<UiChoiceOption> options, bool multi, UiOptionsLayout layout) => Note("options", text);

        public IRuntimeWidget? AddInfo(IRuntimePanel panel, string text, string value) => Note("info", text);

        IRuntimeWidget? IUiRuntime.AddFact(IRuntimePanel panel, string name, string value) => Note("fact", name);

        private IRuntimeWidget Note(string kind, string text)
        {
            if (GridOn) GridAdds.Add(kind);
            return new CellRowTests.FakeWidget { Text = text };
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

        /// <summary>Raise the text slot of an input cell, as the field's per-keystroke event does.</summary>
        public bool Type(string rowKey, string cellKey, string text)
        {
            if (!_rows.TryGetValue(rowKey, out var row)) return false;
            ushort id = row.WiredIdOf(cellKey);
            if (id == 0) return false;
            UiSlotTable.BootstrapDispatch(id, (byte)UiSlotKind.Text, 0f, false, text);
            return true;
        }
    }
}
