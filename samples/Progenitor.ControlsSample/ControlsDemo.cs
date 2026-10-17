using System;
using System.Collections.Generic;
using System.Globalization;
using Progenitor.Ui;

namespace Progenitor.ControlsSample;

/// <summary>
/// Builds one panel that uses every control <see cref="IUiPanel"/> offers. Every interactive control
/// writes a log line and updates the status label at the top of the panel, so a tester can click
/// through and see each one answer. Game-free: it only talks to <see cref="IUiPanel"/> and the handle
/// interfaces, so the test project compiles it and drives it with a recording panel.
/// </summary>
internal sealed class ControlsDemo
{
    internal const string Title = "Controls Sample";
    internal const string OpenBindId = "OpenControls";
    internal const string PageId = "All controls";
    internal const string LogPrefix = "Controls sample: ";
    internal const string StatusIdle = "Last event: (none yet, click a control)";

    /// <summary>The <c>ctx.Dialogs</c> kinds the panel offers a button for.</summary>
    internal static readonly string[] DialogKinds =
        { "Confirm", "Confirm (facts)", "Notice", "Notice (facts)", "Choose", "Prompt", "Form" };

    private static readonly string[] Fruits = { "apple", "pear", "plum" };
    private static readonly string[] FruitLabels = { "Apple", "Pear", "Plum" };
    private static readonly string[] FruitNotes = { "Crisp and red.", "Soft and green.", "Small and purple." };
    private static readonly string[] CaptureValues = { "WhenHovered", "WhileShown", "Never" };

    private readonly Action<string> _log;
    private readonly Action<string>? _openDialog;
    private readonly Action? _close;
    private IUiToneLabel? _status;
    private IUiPanel? _panel;
    private int _events;

    internal ControlsDemo(Action<string> log, Action<string>? openDialog = null, Action? close = null)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _openDialog = openDialog;
        _close = close;
    }

    /// <summary>Text of the last event, as the status label shows it.</summary>
    internal string LastStatus { get; private set; } = StatusIdle;

    /// <summary>Events reported so far.</summary>
    internal int EventCount => _events;

    /// <summary>Report one control event: one log line and the status label.</summary>
    internal void Report(string control, string value)
    {
        _events++;
        string line = control + " = " + value;
        LastStatus = "Last event: " + line;
        _log(LogPrefix + line);
        // UiLook: the status line is a tone label, muted until the first event and green after it.
        if (_status != null) _status.SetText(LastStatus, LabelTone.Ok);
        if (_panel != null) _panel.Footer = "Events: " + _events.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Add every control to <paramref name="panel"/>. <paramref name="pluginId"/> owns <paramref name="bindId"/>.</summary>
    internal void Build(IUiPanel panel, string pluginId, string bindId)
    {
        if (panel == null) throw new ArgumentNullException(nameof(panel));
        _panel = panel;
        panel.ContentAlignment = "left";
        panel.Footer = "Events: 0";

        // Read-only rows.
        panel.Heading("Every Progenitor UI control");
        panel.Note("Each control below writes a log line and updates the status line under this note.");
        _status = panel.Label(StatusIdle, LabelTone.Muted);
        panel.Label("A plain label: the kit's ordinary caption color.");
        panel.Fact("Plugin", pluginId);
        panel.Info("Events so far", () => _events.ToString(CultureInfo.InvariantCulture), 1);
        panel.Divider();

        // Buttons. On a Mods page or a game window every button is the game's compact button; UiLook.GameStyle matters on a
        // plain floating panel, where a button otherwise keeps the kit's look.
        panel.Heading("Buttons");
        var target = panel.Button("Plain button");
        var styled = panel.Button("Game-styled button").GameStyle();
        styled.OnClick(() => Report("GameStyle button", "clicked"));
        int clicks = 0;
        target.OnClick(() => { clicks++; target.Text = "Plain button (" + clicks + ")"; Report("Button", "clicked " + clicks); });
        panel.ConfirmButton("Confirm button", "Run the confirm button?")
            .OnClick(() => Report("ConfirmButton", "yes"));
        panel.ConfirmButton("Confirm button (with body)", "Run the second confirm button?", "It only writes a log line.")
            .OnClick(() => Report("ConfirmButton+body", "yes"));
        panel.BeginInline();
        panel.Button("Inline A").GameStyle().OnClick(() => Report("Inline button", "A"));
        panel.Button("Inline B").GameStyle().OnClick(() => Report("Inline button", "B"));
        panel.EndInline();

        // Two-state and range controls. The toggles also drive handle properties.
        panel.Heading("Toggles and ranges");
        panel.Toggle("Toggle", true).OnChanged(on => Report("Toggle", on ? "on" : "off"));
        panel.Toggle("Plain button clickable", true).OnChanged(on =>
        {
            target.Interactable = on;
            Report("Interactable", on ? "plain button enabled" : "plain button grayed");
        });
        // The toggle comes first and the note it shows or hides right under it (live 2026-10-10: the note sat above its toggle
        // and named the wrong one).
        IUiLabel? hideMe = null;
        panel.Toggle("Show the note below", true).OnChanged(on =>
        {
            if (hideMe != null) hideMe.Visible = on;
            Report("Visible", on ? "note shown" : "note hidden");
        });
        hideMe = panel.Note("The toggle above shows or hides this note (IUiHandle.Visible).");
        panel.Slider("Slider", 0f, 10f, 5f).OnChanged(v => Report("Slider", v.ToString("0.##", CultureInfo.InvariantCulture)));
        panel.SliderField("Slider field", 0f, 100f, 5f, 50f)
            .OnChanged(v => Report("SliderField", v.ToString("0.##", CultureInfo.InvariantCulture)));
        panel.Number("Number", 3, 0, 99, 1, 0, "units")
            .OnChanged(v => Report("Number", v.ToString(CultureInfo.InvariantCulture)));

        // Text.
        panel.Heading("Text");
        panel.Input("Text input", "hello").OnChanged(s => Report("Input", Quote(s)));
        var search = panel.SearchInput("Search", "");
        search.OnChanged(s => Report("Search", Quote(s)));
        search.OnSubmitted(s => Report("Search submitted", Quote(s)));
        panel.TextArea("Text area", "line one\nline two", 3).OnChanged(s => Report("TextArea", Quote(s)));

        // Choices.
        panel.Heading("Choices");
        panel.Dropdown("Dropdown", Fruits, "apple", FruitLabels).OnChanged(s => Report("Dropdown", s));
        panel.Dropdown("Dropdown (described)", Fruits, "pear", FruitLabels, FruitNotes).OnChanged(s => Report("Dropdown+descriptions", s));
        panel.Radio("Radio", Fruits, "plum", FruitLabels).OnChanged(s => Report("Radio", s));
        panel.Radio("Radio (described)", Fruits, "apple", FruitLabels, FruitNotes).OnChanged(s => Report("Radio+descriptions", s));
        panel.MultiSelect("Multi-select", Fruits, new[] { "apple" }, FruitLabels)
            .OnChanged(list => Report("MultiSelect", list.Count == 0 ? "(none)" : string.Join(",", list)));
        panel.Dropdown("Panel capture mode", CaptureValues, panel.CaptureMode.ToString()).OnChanged(s =>
        {
            if (Enum.TryParse<UiCaptureMode>(s, out var mode)) panel.CaptureMode = mode;
            Report("CaptureMode", s);
        });

        // Pickers.
        panel.Heading("Pickers");
        panel.ColorField("Color", "#B03030").OnChanged(s => Report("ColorField", s));
        panel.ColorField("Color with alpha", "#3060B0CC", alpha: true).OnChanged(s => Report("ColorField+alpha", s));
        panel.DateTimeField("Date and time", "", null, DateTimePickerPrecision.Minute).OnChanged(s => Report("DateTimeField", Quote(s)));
        panel.DateTimeField("Date and time (seconds)", "2026-10-10T18:00:00Z", TimeZoneInfo.Utc, DateTimePickerPrecision.Second)
            .OnChanged(s => Report("DateTimeField+seconds", s));
        panel.KeyField("Page key", pluginId, bindId).OnChanged(chord => Report("KeyField", chord.ToString()));

        // ctx.Dialogs (the plugin wires these; the panel only offers the buttons).
        if (_openDialog != null)
        {
            panel.Heading("Dialogs (ctx.Dialogs)");
            panel.BeginInline();
            foreach (var kind in DialogKinds)
            {
                string k = kind;
                panel.Button(k).GameStyle().OnClick(() => _openDialog(k));
            }
            panel.EndInline();
        }

        if (_close != null)
        {
            panel.Divider();
            panel.Button("Close").GameStyle().OnClick(() => { Report("Close", "window closed"); _close(); });
        }
    }

    private static string Quote(string? s) => "\"" + (s ?? "").Replace("\n", "\\n") + "\"";
}

/// <summary>The standalone window: <see cref="Ui.Panel"/> + <see cref="ControlsDemo"/> + Show/Hide. Game-free.</summary>
internal sealed class ControlsWindow : IDisposable
{
    private readonly Func<IUiPanel> _create;
    private readonly Action<string> _log;
    private readonly Action<string>? _openDialog;
    private readonly string _pluginId;
    private IUiPanel? _panel;

    internal ControlsWindow(string pluginId, Func<IUiPanel> create, Action<string> log, Action<string>? openDialog = null)
    {
        _pluginId = pluginId;
        _create = create ?? throw new ArgumentNullException(nameof(create));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _openDialog = openDialog;
    }

    internal bool IsOpen => _panel != null && !_panel.IsDisposed && _panel.State == UiHandleState.Shown;

    internal ControlsDemo? Demo { get; private set; }

    internal void Toggle()
    {
        if (IsOpen) Close();
        else Open();
    }

    internal void Open()
    {
        Close();
        var panel = _create();
        // UiLook.GameWindow: the game's popup frame and the Mods page's grid and buttons (call before adding controls).
        panel.GameWindow();
        // Takes input while shown, so a controller can drive the window too (docs/ui.md, NAV1).
        panel.CaptureMode = UiCaptureMode.WhileShown;
        Demo = new ControlsDemo(_log, _openDialog, Close);
        Demo.Build(panel, _pluginId, ControlsDemo.OpenBindId);
        _panel = panel;
        panel.Show();
        _log(ControlsDemo.LogPrefix + "window opened");
    }

    internal void Close()
    {
        var panel = _panel;
        _panel = null;
        if (panel == null) return;
        try { panel.Hide(); } finally { panel.Dispose(); }
    }

    public void Dispose() => Close();
}
