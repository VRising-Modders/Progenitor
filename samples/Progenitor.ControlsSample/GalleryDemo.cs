using System;
using System.Collections.Generic;
using System.Globalization;
using Progenitor.Ui;

namespace Progenitor.ControlsSample;

/// <summary>What the gallery asks of the plugin: the windows, the pages, the dialogs and the HUD calls.</summary>
internal interface IGalleryHost
{
    /// <summary>Open (or close, when open) the gallery's window of <paramref name="preset"/>; returns what happened.</summary>
    string ToggleWindow(UiWindowPreset preset);

    /// <summary>Open the plugin's floating panel (the All controls window).</summary>
    void OpenFloating();

    /// <summary>Open the plugin's Mods page <paramref name="page"/>.</summary>
    void OpenPage(string page);

    /// <summary>Open one <c>ctx.Dialogs</c> kind (<see cref="ControlsDemo.DialogKinds"/>).</summary>
    void OpenDialog(string kind);

    /// <summary><c>ctx.Hud.Notify</c>.</summary>
    void Notify(string text, string detail, UiNoticeKind kind, bool prefix);

    /// <summary>Arm a pick in the world; <paramref name="done"/> gets a one-line result.</summary>
    void Pick(Action<string> done);

    /// <summary>Ping the last pick; returns what happened.</summary>
    string LocatePicked();

    /// <summary>Show the gallery's tip card anchored to <paramref name="anchor"/>; returns its state.</summary>
    string Tip(IUiPanel anchor);
}

/// <summary>
/// The Gallery page: one tab per category of <see cref="GalleryCatalog"/>, and on it every entry of that category
/// with a live example (or the button that opens where it lives) and one muted sentence on when to use it. All words go through
/// the named text styles (<see cref="UiTextStyles.Text(IUiPanel, string, UiTextStyle)"/>): the page sets no color, size
/// or alignment of its own. Every event writes a <c>Controls sample: gallery …</c> line and updates the status line.
/// Game-free: the plugin's calls go through <see cref="IGalleryHost"/>.
/// </summary>
internal sealed class GalleryDemo
{
    internal const string PageId = "Gallery";
    internal const string TabsKey = "gallery-tabs";
    internal const string LogTag = "gallery ";

    private readonly Action<string> _log;
    private readonly IGalleryHost _host;
    private readonly WidgetsDemo _widgets;
    private IUiLabel? _status;
    private IUiTabs? _tabs;

    internal GalleryDemo(Action<string> log, IGalleryHost host, IWidgetsHud? widgetsHud = null)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _widgets = new WidgetsDemo(log, widgetsHud);
    }

    /// <summary>The last event, as the status line shows it.</summary>
    internal string LastStatus { get; private set; } = "";

    /// <summary>The entry ids the last build drew.</summary>
    internal List<string> Built { get; } = new();

    internal IUiTabs? Tabs => _tabs;

    internal void Report(string what)
    {
        LastStatus = what;
        _log(ControlsDemo.LogPrefix + LogTag + what);
        if (_status != null) _status.Text = "Last event: " + what;
    }

    internal void Build(IUiPanel panel, string pluginId, string bindId)
    {
        if (panel == null) throw new ArgumentNullException(nameof(panel));
        Built.Clear();
        panel.ContentAlignment = "left";
        panel.Text("Gallery", UiTextStyle.Title);
        panel.Text("Every control, widget, window and message the Progenitor UI kit offers, by category. Each entry says when to use it.", UiTextStyle.Muted);
        _status = panel.Text("Last event: (none yet)", UiTextStyle.Muted);
        _tabs = panel.Tabs(TabsKey, GalleryCatalog.Categories);
        _tabs.OnChanged(i => Report("tab = " + GalleryCatalog.Categories[i]));
        for (int t = 0; t < GalleryCatalog.Categories.Length; t++)
        {
            string category = GalleryCatalog.Categories[t];
            _tabs.SetTooltip(t, GalleryCatalog.LongName(category));
            _tabs.Add(t, panel.Text(GalleryCatalog.LongName(category), UiTextStyle.Section));
            foreach (var entry in GalleryCatalog.In(category))
            {
                BuildEntry(panel, _tabs, t, entry, pluginId, bindId);
                _tabs.Add(t, panel.Text(entry.UseWhen, UiTextStyle.Muted));
                Built.Add(entry.Id);
            }
        }
    }

    private T On<T>(int tab, T handle) where T : IUiHandle => _tabs!.Add(tab, handle);

    private IUiButton Action(IUiPanel panel, int tab, string caption, string tooltip, Action act)
    {
        var b = On(tab, panel.Button(caption));
        if (tooltip.Length > 0) b.Tooltip(tooltip);
        b.OnClick(act);
        return b;
    }

    private void BuildEntry(IUiPanel panel, IUiTabs tabs, int tab, GalleryCatalog.Entry e, string pluginId, string bindId)
    {
        string api = e.Api;
        switch (e.Id)
        {
            // ---- Layout and windows ----
            case "window-basic":
                Action(panel, tab, "Basic window", api, () => Report("window basic = " + _host.ToggleWindow(UiWindowPreset.Basic)));
                break;
            case "window-header":
                Action(panel, tab, "Header window", api, () => Report("window header = " + _host.ToggleWindow(UiWindowPreset.Header)));
                break;
            case "window-tabbed":
                Action(panel, tab, "Tabbed window", api, () => Report("window tabbed = " + _host.ToggleWindow(UiWindowPreset.Tabbed)));
                break;
            case "floating-panel":
                Action(panel, tab, "Floating panel", api, () => { _host.OpenFloating(); Report("floating panel opened"); });
                break;
            case "mods-page":
                Action(panel, tab, "Open All controls", api, () => { _host.OpenPage(ControlsDemo.PageId); Report("page " + ControlsDemo.PageId); });
                Action(panel, tab, "Open Widgets", api, () => { _host.OpenPage(WidgetsDemo.PageId); Report("page " + WidgetsDemo.PageId); });
                break;
            case "settings-page":
                On(tab, panel.Text("This mod's settings page opens from its entry in the Mods list.", UiTextStyle.Body));
                break;
            case "text-styles":
                On(tab, panel.Text("Title: a block's name", UiTextStyle.Title));
                On(tab, panel.Text("Section: a group of rows", UiTextStyle.Section));
                On(tab, panel.Text("Body: an ordinary sentence.", UiTextStyle.Body));
                On(tab, panel.Text("Muted: a hint or a secondary detail.", UiTextStyle.Muted));
                On(tab, panel.Text("1,280", UiTextStyle.Number));
                On(tab, panel.Text("Warning: the chest is almost full.", UiTextStyle.Warning));
                On(tab, panel.Text("Error: the furnace has no fuel.", UiTextStyle.Error));
                On(tab, panel.Text("Success: 3 chests sorted.", UiTextStyle.Success));
                break;
            case "name-value":
                On(tab, panel.Text("Iron Ore", "1,280", UiTextStyle.Number));
                On(tab, panel.Text("Copper Ore", "96", UiTextStyle.Number));
                On(tab, panel.Text("Fuel left", "4 min", UiTextStyle.Warning));
                break;
            case "heading":
                On(tab, panel.Heading("A heading"));
                break;
            case "note":
                On(tab, panel.Note("A note: one short explanation under a heading."));
                break;
            case "tone-label":
            {
                // A status line in place: the named styles' tones (UiTextStyles.ToneOf), never a raw tone or color.
                var cycle = new[]
                {
                    (UiTextStyle.Muted, "Status: idle"),
                    (UiTextStyle.Warning, "Status: sorting (waiting for the server)"),
                    (UiTextStyle.Success, "Status: sorted 3 chests"),
                    (UiTextStyle.Error, "Status: the furnace has no fuel"),
                };
                int at = 0;
                var line = On(tab, panel.Label(cycle[0].Item2, UiTextStyles.ToneOf(cycle[0].Item1)));
                Action(panel, tab, "Cycle status", "Rewrites the line above in place with SetText and a style's tone.", () =>
                {
                    at = (at + 1) % cycle.Length;
                    line.SetText(cycle[at].Item2, UiTextStyles.ToneOf(cycle[at].Item1));
                    Report("status = " + cycle[at].Item1);
                });
                break;
            }
            case "fact":
                On(tab, panel.Fact("Plugin", pluginId));
                break;
            case "divider":
                On(tab, panel.Divider());
                break;
            case "inline":
                panel.BeginInline();
                Action(panel, tab, "Inline A", "", () => Report("inline A"));
                Action(panel, tab, "Inline B", "", () => Report("inline B"));
                panel.EndInline();
                break;
            case "tabs":
                On(tab, panel.Text("This page's own tab row, above, is the tabs widget.", UiTextStyle.Body));
                break;
            case "footer":
                On(tab, panel.Text("Each gallery window carries its version stamp at the bottom.", UiTextStyle.Body));
                break;
            case "scroll":
                On(tab, panel.Text("This tab scrolls: its rows do not fit one screen.", UiTextStyle.Body));
                break;
            case "capture":
                On(tab, panel.Text("Basic windows leave the input to the game until hovered; Header and Tabbed take it while shown.", UiTextStyle.Body));
                break;
            case "game-style":
                Action(panel, tab, "Game button", api, () => Report("game button")).GameStyle();
                break;

            // ---- Input ----
            case "button":
            {
                int clicks = 0;
                IUiButton? b = null;
                b = Action(panel, tab, "Button", api, () => { clicks++; b!.Text = "Button (" + clicks + ")"; Report("button " + clicks); });
                break;
            }
            case "confirm-button":
            {
                var b = On(tab, panel.ConfirmButton("Confirm button", "Write a log line?"));
                b.OnClick(() => Report("confirm button = yes"));
                break;
            }
            case "toggle":
                On(tab, panel.Toggle("Toggle", true)).OnChanged(v => Report("toggle = " + (v ? "on" : "off")));
                break;
            case "slider":
                On(tab, panel.Slider("Slider", 0f, 10f, 5f)).OnChanged(v => Report("slider = " + v.ToString("0.##", CultureInfo.InvariantCulture)));
                break;
            case "slider-field":
                On(tab, panel.SliderField("Slider field", 0f, 100f, 5f, 50f)).OnChanged(v => Report("slider field = " + v.ToString("0.##", CultureInfo.InvariantCulture)));
                break;
            case "number":
                On(tab, panel.Number("Number", 3, 0, 99, 1, 0, "chests")).OnChanged(v => Report("number = " + v.ToString(CultureInfo.InvariantCulture)));
                break;
            case "dropdown":
                On(tab, panel.Dropdown("Dropdown", new[] { "ore", "wood", "stone" }, "ore", new[] { "Ore", "Wood", "Stone" },
                    new[] { "Iron, copper and silver ore.", "Lumber and planks.", "Stone and bricks." }))
                    .OnChanged(v => Report("dropdown = " + v));
                break;
            case "radio":
                On(tab, panel.Radio("Radio", new[] { "near", "all" }, "near", new[] { "Nearby chests", "Every chest" },
                    new[] { "Only chests in this room.", "Every chest in the castle." }))
                    .OnChanged(v => Report("radio = " + v));
                break;
            case "multiselect":
                On(tab, panel.MultiSelect("Multi-select", new[] { "ore", "wood", "stone" }, new[] { "ore" }, new[] { "Ore", "Wood", "Stone" }))
                    .OnChanged(v => Report("multi-select = " + string.Join(",", v)));
                break;
            case "input":
                On(tab, panel.Input("Text input", "Iron chest")).OnChanged(v => Report("input = " + v));
                break;
            case "textarea":
                On(tab, panel.TextArea("Text area", "", 3)).OnChanged(v => Report("text area = " + v.Length + " chars"));
                break;
            case "color":
                On(tab, panel.ColorField("Color", "#B03030")).OnChanged(v => Report("color = " + v));
                break;
            case "datetime":
                On(tab, panel.DateTimeField("Date and time", "")).OnChanged(v => Report("date-time = " + v));
                break;
            case "keyfield":
                On(tab, panel.KeyField("Key field", pluginId, bindId));
                break;
            case "keybind":
                On(tab, panel.Text("Ctrl+F7 (registered with ctx.Keybinds.RegisterOpener) opens and closes these pages.", UiTextStyle.Body));
                break;
            case "picker":
                On(tab, panel.Picker("gallery-picker", WidgetsDemo.SampleEntries())).OnPicked(p => Report("picker = " + p.Count));
                break;
            case "picker-multi":
                On(tab, panel.Picker("gallery-multi", WidgetsDemo.SampleEntries(), new UiPickerOptions { Multi = true, Rows = 5 }))
                    .OnPicked(p => Report("multi picker = " + p.Count));
                break;
            case "code":
            {
                var code = On(tab, panel.CodeArea("Code area", WidgetsDemo.SampleScript, 6));
                code.SetHighlighter(WidgetsDemo.Highlight);
                code.OnChanged(t => Report("code = " + t.Length + " chars"));
                break;
            }
            case "set-setting":
            case "set-toggle":
            case "set-number":
            case "set-slider":
            case "set-dropdown":
            case "set-radio":
            case "set-multi":
            case "set-text":
            case "set-color":
            case "set-datetime":
            case "set-keybind":
            case "set-section":
            case "set-info":
            case "set-scope":
                CallLines(panel, tab, e.Control, api);
                break;

            // ---- Lists and data ----
            case "ordered-list":
                On(tab, panel.OrderedList("gallery-list", WidgetsDemo.SampleTargets()))
                    .OnReorder(m => Report("list reorder " + m.Id + " " + m.From + "->" + m.To));
                break;
            case "data-table":
                // The gallery's tab badge is not the table's: the Widgets page's Table tab carries the row count.
                _widgets.BuildTable(panel, tabs, tab, badge: false);
                break;
            case "master-detail":
                _widgets.BuildMasterDetail(panel, tabs, tab);
                break;
            case "chips":
                _widgets.BuildChips(panel, tabs, tab);
                break;
            case "info":
            {
                int seconds = 0;
                On(tab, panel.Info("Seconds open", () => (seconds++).ToString(CultureInfo.InvariantCulture), 1));
                break;
            }
            case "item-icons":
                On(tab, panel.Text("The picker above (Input tab) shows Crystal and Coarse Thread with the game's own icons.", UiTextStyle.Body));
                break;
            case "pending":
                On(tab, panel.Text("Sorting: waiting for the server.", UiTextStyle.Warning));
                break;

            // ---- Feedback and messages ----
            case "notify":
                Action(panel, tab, "Info", api, () => Notify("A notification.", UiNoticeKind.Info, true));
                Action(panel, tab, "Success", api, () => Notify("3 chests sorted.", UiNoticeKind.Success, true));
                Action(panel, tab, "Warning", api, () => Notify("The chest is almost full.", UiNoticeKind.Warning, true));
                Action(panel, tab, "Error", api, () => Notify("The furnace has no fuel.", UiNoticeKind.Error, true));
                break;
            case "notify-plain":
                Action(panel, tab, "No prefix", api, () => Notify("[Gallery] names itself, so no prefix.", UiNoticeKind.Info, false));
                break;
            case "confirm":
                Action(panel, tab, "Confirm", api, () => { _host.OpenDialog("Confirm"); Report("dialog Confirm"); });
                break;
            case "notice":
                Action(panel, tab, "Notice", api, () => { _host.OpenDialog("Notice"); Report("dialog Notice"); });
                break;
            case "choose":
                Action(panel, tab, "Choose", api, () => { _host.OpenDialog("Choose"); Report("dialog Choose"); });
                break;
            case "prompt":
                Action(panel, tab, "Prompt", api, () => { _host.OpenDialog("Prompt"); Report("dialog Prompt"); });
                break;
            case "tooltip":
                On(tab, panel.Text("Hover any gallery button: its tooltip names the call.", UiTextStyle.Body));
                break;
            case "term":
                _widgets.BuildHelp(panel, tabs, tab);
                break;
            case "term-markers":
                On(tab, panel.Text("The help block above ends with its Show markers again toggle.", UiTextStyle.Body));
                break;
            case "tip":
                Action(panel, tab, "Tip card", api, () => Report("tip = " + _host.Tip(panel)));
                break;
            case "chat-reply":
            case "chat-send":
                CallLines(panel, tab, e.Control, api);
                break;

            // ---- HUD and world ----
            case "locate":
                Action(panel, tab, "Locate last pick", api, () => Report("locate = " + _host.LocatePicked()));
                break;
            case "pick":
                Action(panel, tab, "Pick in world", api, () => { Report("pick armed"); _host.Pick(r => Report("pick = " + r)); });
                break;
            case "pick-repeat":
                On(tab, panel.Text("Pick several (repeat) is on the Widgets page, Layout tab.", UiTextStyle.Body));
                break;

            // ---- Game panels ----
            case "attach-container":
            case "attach-station":
            case "attach-inventory":
            case "attach-heart":
            case "attach-tooltip":
            case "attach-map":
                CallLines(panel, tab, e.Control, e.Sample);
                break;

            default:
                throw new InvalidOperationException("The gallery has no example for '" + e.Id + "'.");
        }
    }

    /// <summary>
    /// A control that lives elsewhere: its name, then the call (or the sample) on its own wrapping line, so the whole call
    /// is readable. A name-and-value row cut the call at the row's edge.
    /// </summary>
    private void CallLines(IUiPanel panel, int tab, string name, string call)
    {
        On(tab, panel.Text(name, UiTextStyle.Body));
        On(tab, panel.Text(call, UiTextStyle.Muted));
    }

    private void Notify(string text, UiNoticeKind kind, bool prefix)
    {
        _host.Notify(text, "", kind, prefix);
        Report("notify " + kind + (prefix ? "" : " (no prefix)"));
    }
}
