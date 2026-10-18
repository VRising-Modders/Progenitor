using System;
using System.Collections.Generic;
using Progenitor.Plugins;

namespace Progenitor.Ui;

/// <summary>
/// <c>ctx.Dialogs</c> (DIALOGS1, docs/ui.md "Dialogs"): ready-made modal dialogs that look like the
/// rest of Progenitor without the mod styling anything — a confirmation, a notice, a pick from
/// options and a line of text. Every dialog uses the one dialog layout the framework's own
/// confirmations use: the title centered, an optional muted body, the content, one centered row of
/// equal-width buttons with the main action on the right, and a small version footer that names
/// this mod (<see cref="ShowModVersion"/>).
///
/// <para><b>Behavior.</b> One dialog is on screen at a time; later ones wait in order (FIFO). Enter
/// picks the main action (not in a Choose, which has none), Escape the dismissal. Each method
/// returns a handle whose <c>Dispose</c> closes the dialog, or takes it out of the queue, without
/// running a callback. A callback that throws is caught and logged. Client only: on the server a
/// call logs once and returns a handle that does nothing.</para>
///
/// <para><b>Hot reload.</b> The callbacks are held by Core only and bound to Core objects; nothing is
/// handed to Bootstrap or IL2CPP. Every unload reason drops this plugin's dialogs — on screen and
/// queued — without running a callback.</para>
/// </summary>
public sealed class PluginDialogs
{
    private readonly PluginContext _ctx;
    private bool _serverLogged;

    internal PluginDialogs(PluginContext ctx)
    {
        _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
        object owner = ctx.Owner;
        _ctx.Track(new ActionDisposable(() => UiDialog.DropOwner(owner)));
    }

    /// <summary>
    /// Whether this plugin's dialogs name it in their footer (<c>&lt;mod name&gt; &lt;version&gt; ·
    /// Progenitor &lt;version&gt;</c>, from the plugin's BepInEx metadata). On by default; off gives
    /// the plain <c>Progenitor &lt;version&gt;</c> line. A call's <c>showModVersion</c> argument wins.
    /// </summary>
    public bool ShowModVersion { get; set; } = true;

    /// <summary>
    /// Ask a yes/no question. <paramref name="onYes"/> runs on the main button (right) or Enter;
    /// <paramref name="onNo"/> on <paramref name="no"/> (left) or Escape. <paramref name="danger"/>
    /// draws the main button in the danger tone for a destructive action; otherwise it is the accent.
    /// </summary>
    public IDisposable Confirm(
        string title, string body, string yes, Action onYes, string no = "Cancel", Action? onNo = null,
        bool danger = false, bool? showModVersion = null)
    {
        if (onYes == null) throw new ArgumentNullException(nameof(onYes));
        if (ServerSide()) return Noop.Instance;
        return UiDialogs.Confirm(_ctx.Owner, _ctx.Guid, Footer(showModVersion), title, body, yes, onYes, no, onNo, danger);
    }

    /// <summary>
    /// DIALOGS2: ask a yes/no question about a set of facts — a short <paramref name="title"/>, then
    /// one label/value row per fact (labels muted and left-aligned in a column as wide as the widest
    /// label, values bright), then at most one short <paramref name="note"/>. A fact with an empty
    /// value is left out; one with an empty label continues the fact above it.
    /// </summary>
    public IDisposable Confirm(
        string title, IReadOnlyList<DialogFact> facts, string yes, Action onYes, string note = "", string no = "Cancel",
        Action? onNo = null, bool danger = false, bool? showModVersion = null)
    {
        if (onYes == null) throw new ArgumentNullException(nameof(onYes));
        if (ServerSide()) return Noop.Instance;
        return UiDialogs.Confirm(
            _ctx.Owner, _ctx.Guid, Footer(showModVersion), title, "", yes, onYes, no, onNo, danger, facts, note);
    }

    /// <summary>DIALOGS2: <see cref="Notice(string, string, string, Action?, bool?)"/> with a facts block in place of the body.</summary>
    public IDisposable Notice(
        string title, IReadOnlyList<DialogFact> facts, string note = "", string ok = "OK", Action? onClose = null,
        bool? showModVersion = null)
    {
        if (ServerSide()) return Noop.Instance;
        return UiDialogs.Notice(_ctx.Owner, _ctx.Guid, Footer(showModVersion), title, "", ok, onClose, facts, note);
    }

    /// <summary>Tell the player something. <paramref name="onClose"/> runs on the button, Enter or Escape.</summary>
    public IDisposable Notice(string title, string body, string ok = "OK", Action? onClose = null, bool? showModVersion = null)
    {
        if (ServerSide()) return Noop.Instance;
        return UiDialogs.Notice(_ctx.Owner, _ctx.Guid, Footer(showModVersion), title, body, ok, onClose);
    }

    /// <summary>
    /// Pick one of <paramref name="options"/>; <paramref name="onPick"/> gets its index. Up to three
    /// short options are buttons on the button row (Cancel on the left); more, or longer ones, are a
    /// vertical list above a Cancel row. Enter does nothing (there is no main action); Escape and
    /// <paramref name="cancel"/> run <paramref name="onCancel"/>.
    /// </summary>
    public IDisposable Choose(
        string title, string body, IReadOnlyList<string> options, Action<int> onPick, string cancel = "Cancel",
        Action? onCancel = null, bool? showModVersion = null)
    {
        if (onPick == null) throw new ArgumentNullException(nameof(onPick));
        if (options == null || options.Count == 0) throw new ArgumentException("At least one option is required.", nameof(options));
        if (ServerSide()) return Noop.Instance;
        return UiDialogs.Choose(_ctx.Owner, _ctx.Guid, Footer(showModVersion), title, body, options, onPick, cancel, onCancel);
    }

    /// <summary>
    /// Ask for a line of text. <paramref name="validate"/> returns an error to show under the field
    /// (and keep the dialog open) or null to accept; <paramref name="onSubmit"/> gets the accepted
    /// text. The main button and Enter submit; <paramref name="cancel"/> and Escape run
    /// <paramref name="onCancel"/>. <paramref name="maxLength"/> caps the field (0: no cap).
    /// </summary>
    public IDisposable Prompt(
        string title, string body, string initial, Action<string> onSubmit, string placeholder = "",
        Func<string, string?>? validate = null, string submit = "OK", int maxLength = 0,
        string cancel = "Cancel", Action? onCancel = null, bool? showModVersion = null)
    {
        if (onSubmit == null) throw new ArgumentNullException(nameof(onSubmit));
        if (ServerSide()) return Noop.Instance;
        return UiDialogs.Prompt(
            _ctx.Owner, _ctx.Guid, Footer(showModVersion), title, body, initial, onSubmit, placeholder, validate,
            submit, maxLength, cancel, onCancel);
    }

    /// <summary>
    /// MASKED1: ask for a secret (a password): a Prompt whose field draws one asterisk per character. Copy and cut do
    /// nothing in it, and the framework's readbacks show asterisks; <paramref name="onSubmit"/> gets the typed text. Never
    /// log it. The field starts empty. <paramref name="validate"/>, <paramref name="maxLength"/> (0: no cap),
    /// <paramref name="submit"/>, <paramref name="cancel"/> and <paramref name="onCancel"/> are as in
    /// <see cref="Prompt"/>.
    /// </summary>
    public IDisposable PromptSecret(
        string title, string body, Action<string> onSubmit, Func<string, string?>? validate = null, string submit = "OK",
        int maxLength = 64, string cancel = "Cancel", Action? onCancel = null, bool? showModVersion = null)
    {
        if (onSubmit == null) throw new ArgumentNullException(nameof(onSubmit));
        if (ServerSide()) return Noop.Instance;
        return UiDialogs.Prompt(
            _ctx.Owner, _ctx.Guid, Footer(showModVersion), title, body, "", onSubmit, "", validate,
            submit, maxLength, cancel, onCancel, masked: true);
    }

    /// <summary>
    /// FORMDIALOG1: a small form: a one-line text field with <see cref="DialogFormOptions.Caption"/> on its left, then
    /// <see cref="DialogFormOptions.Checks"/> as the game's checkboxes under <see cref="DialogFormOptions.Heading"/>, then
    /// Cancel (left) and Save (right). A click anywhere on a box's line flips it. Save validates
    /// (<see cref="DialogFormOptions.Validate"/>: an error under the field, or null), and Enter does too unless the
    /// keyboard focus is on a box or a button, which Enter then presses. With <see cref="DialogFormOptions.RequireOne"/>
    /// Save is grayed while no box is checked. <paramref name="onSave"/> gets the text and each box's state; Cancel and
    /// Escape run <see cref="DialogFormOptions.OnCancel"/>. One to ten boxes, or <see cref="ArgumentException"/>.
    /// </summary>
    public IDisposable Form(string title, DialogFormOptions options, Action<DialogFormResult> onSave)
    {
        if (onSave == null) throw new ArgumentNullException(nameof(onSave));
        if (options == null) throw new ArgumentNullException(nameof(options));
        string? countProblem = DialogForm.CountProblem(options.Checks);
        if (countProblem != null) throw new ArgumentException(countProblem, nameof(options));
        if (ServerSide()) return Noop.Instance;
        return UiDialogs.Form(_ctx.Owner, _ctx.Guid, Footer(options.ShowModVersion), title, options, onSave);
    }

    private string Footer(bool? showModVersion) =>
        UiDialogs.Footer(_ctx.Name, _ctx.Version, showModVersion ?? ShowModVersion);

    private bool ServerSide()
    {
        if (!_ctx.IsServer) return false;
        if (!_serverLogged)
        {
            _serverLogged = true;
            _ctx.Log.LogInfo("Ui: ctx.Dialogs is client only; dialogs on the server do nothing.");
        }

        return true;
    }
}
