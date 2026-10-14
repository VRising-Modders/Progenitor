using System;
using System.Collections.Generic;
using System.Globalization;
using BepInEx;
using BepInEx.Configuration;
using Progenitor.Plugins;
using Progenitor.Ui;
using UiApi = Progenitor.Ui.Ui;

namespace Progenitor.ControlsSample;

/// <summary>
/// Test mod that uses every UI control the Progenitor API offers (README beside this file).
/// Ctrl+F7 opens the Mods workspace on this mod's custom page, where every <see cref="IUiPanel"/> control
/// draws with the game's art; the settings page has every <c>ctx.Settings</c> row kind, and a button there
/// opens the same controls in a floating <c>Ui.Panel</c> drawn with <c>UiLook.GameWindow</c> (the game's popup frame).
/// </summary>
[BepInPlugin(Guid, Name, MyPluginInfo.PLUGIN_VERSION)]
[BepInDependency("progenitor")]
[ProgenitorSide(Side.Client)]
public sealed class Plugin : ProgenitorPlugin<ControlsContext>
{
    internal const string Guid = "progenitor.controlssample";
    internal const string Name = "Progenitor Controls Sample";

    /// <inheritdoc />
    protected override void OnLoad(ControlsContext ctx)
    {
        Action<string> log = line => ctx.Log.LogInfo(line);
        Action<string> dialog = kind => ControlsDialogs.Open(ctx, kind, log);

        var window = new ControlsWindow(ctx.Guid, () => UiApi.Panel(ControlsDemo.Title, ctx.Owner, ctx.Guid), log, dialog);
        ctx.Track(window);

        // RegisterOpener: the key only opens or closes the page, so it also works at the title screen.
        ctx.Keybinds.RegisterOpener(
            ControlsDemo.OpenBindId,
            new KeyChord(UiKey.F7, KeyModifiers.Ctrl),
            "Open or close the Controls Sample page in the Mods menu (every Progenitor UI control, game look).",
            () => TogglePage(ctx));

        var hud = new ControlsHud(ctx);
        ctx.Menu.Register(Name, "Every Progenitor UI control, each wired to a log line and a status label.")
            .Page(ControlsDemo.PageId, panel => new ControlsDemo(log, dialog).Build(panel, ctx.Guid, ControlsDemo.OpenBindId))
            .Page(WidgetsDemo.PageId, panel => new WidgetsDemo(log, hud).Build(panel));

        ControlsSettings.Register(ctx, log, window);
        log(ControlsDemo.LogPrefix + "loaded; Ctrl+F7 opens the All controls page");
    }

    /// <summary>
    /// Ctrl+F7: <c>ctx.Menu.OpenPage</c> opens the Mods menu the way its own key does (in a world through the game's
    /// menu on its MODS tab) and then shows this mod's page; pressed again with the menu open, it closes the menu.
    /// </summary>
    private static void TogglePage(PluginContext ctx)
    {
        if (ModsMenu.IsOpen)
        {
            ModsMenu.Toggle();
            return;
        }

        ctx.Menu.OpenPage(ControlsDemo.PageId);
    }

    /// <inheritdoc />
    protected override void OnUnload(ControlsContext ctx, PluginUnloadReason reason) =>
        ctx.Log.LogInfo(ControlsDemo.LogPrefix + "unloading, reason=" + reason);
}

/// <summary>Empty context (repository convention).</summary>
public sealed class ControlsContext : PluginContext { }

/// <summary>
/// The Widgets page's HUD calls on <c>ctx.Hud</c>: a notification, a pick in world, and a Locate ping on the last pick.
/// The pick and the ping are this plugin's and go with it on every unload.
/// </summary>
internal sealed class ControlsHud : IWidgetsHud
{
    private readonly PluginContext _ctx;
    private UiWorldPickResult? _picked;
    private IUiLocatePing? _ping;

    internal ControlsHud(PluginContext ctx) => _ctx = ctx;

    public void Notify(string text, string detail, UiNoticeKind kind) => _ctx.Hud.Notify(text, detail, kind);

    public void Pick(Action<string> done) =>
        _ctx.Hud.PickInWorld(
            result =>
            {
                _picked = result;
                done(result.Kind + " " + result.Menu + " (" + result.EntityIndex + ":" + result.EntityVersion + ")");
            },
            () => done("Pick cancelled."));

    public string LocatePicked()
    {
        if (_picked is not { } picked) return "nothing picked yet";
        if (!picked.IsCurrent) return "the picked target belongs to an earlier world";
        _ping?.Dispose();
        _ping = _ctx.Hud.Locate(picked.Entity, picked.Menu, seconds: 10);
        return "pinged " + picked.EntityIndex + ":" + picked.EntityVersion;
    }
}

/// <summary>One <c>ctx.Dialogs</c> call per kind in <see cref="ControlsDemo.DialogKinds"/>.</summary>
internal static class ControlsDialogs
{
    internal static void Open(PluginContext ctx, string kind, Action<string> log)
    {
        void Say(string what) => log(ControlsDemo.LogPrefix + "Dialog " + kind + " = " + what);
        var facts = new List<DialogFact> { new("Plugin", ctx.Guid), new("Version", ctx.Version) };
        switch (kind)
        {
            case "Confirm":
                ctx.Dialogs.Confirm("Confirm dialog", "Write a log line?", "Yes", () => Say("yes"), onNo: () => Say("no"));
                break;
            case "Confirm (facts)":
                ctx.Dialogs.Confirm("Confirm with facts", facts, "Yes", () => Say("yes"), note: "Facts block demo.",
                    onNo: () => Say("no"), danger: true);
                break;
            case "Notice":
                ctx.Dialogs.Notice("Notice dialog", "A message with one OK button.", onClose: () => Say("closed"));
                break;
            case "Notice (facts)":
                ctx.Dialogs.Notice("Notice with facts", facts, note: "Facts block demo.", onClose: () => Say("closed"));
                break;
            case "Choose":
                ctx.Dialogs.Choose("Choose dialog", "Pick one.", new[] { "Red", "Green", "Blue" },
                    i => Say("picked " + i.ToString(CultureInfo.InvariantCulture)), onCancel: () => Say("cancel"));
                break;
            case "Prompt":
                ctx.Dialogs.Prompt("Prompt dialog", "Type at least three characters.", "", s => Say("\"" + s + "\""),
                    placeholder: "text", validate: s => (s ?? "").Trim().Length < 3 ? "At least three characters." : null,
                    maxLength: 40, onCancel: () => Say("cancel"));
                break;
            default:
                Say("unknown kind");
                break;
        }
    }
}

/// <summary>Every <see cref="SettingsPageBuilder"/> row kind (ctx.Settings.Page), each logging its change.</summary>
internal static class ControlsSettings
{
    internal static void Register(PluginContext ctx, Action<string> log, ControlsWindow window)
    {
        var toggle = Watch(ctx, log, ctx.Bind("Controls", "Toggle", true, "A toggle row."));
        var number = Watch(ctx, log, ctx.Bind("Controls", "Number", 3, "A number row with steppers."));
        var slider = Watch(ctx, log, ctx.Bind("Controls", "Slider", 50, "A slider row.",
            acceptableValues: new AcceptableValueRange<int>(0, 100)));
        var dropdown = Watch(ctx, log, ctx.Bind("Controls", "Dropdown", "Apple", "A dropdown row.",
            acceptableValues: new AcceptableValueList<string>("Apple", "Pear", "Plum")));
        var radio = Watch(ctx, log, ctx.Bind("Controls", "Radio", "Pear", "A radio row.",
            acceptableValues: new AcceptableValueList<string>("Apple", "Pear", "Plum")));
        var multi = Watch(ctx, log, ctx.Bind("Controls", "MultiSelect", "Apple", "A multi-select row (comma-separated)."));
        var text = Watch(ctx, log, ctx.Bind("Controls", "Text", "hello", "A one-line text row."));
        var area = Watch(ctx, log, ctx.Bind("Controls", "TextArea", "", "A multi-line text row."));
        var color = Watch(ctx, log, ctx.Bind("Controls", "Color", "#B03030", "A color row (#RRGGBB)."));
        var when = Watch(ctx, log, ctx.Bind("Controls", "DateTime", "", "A date-time row (ISO 8601 UTC)."));
        var plain = Watch(ctx, log, ctx.Bind("Controls", "Plain", 1.5f, "A row whose control the kit picks (Setting)."));

        ctx.Settings.Page(page => page
            .Section("Rows")
                .Setting(plain)
                .Toggle(toggle, help: "Toggle row.")
                .Number(number, min: 0, max: 99, step: 1, units: "units")
                .Slider(slider, min: 0, max: 100, step: 5)
                .Dropdown(dropdown).OptionDescriptions("Crisp and red.", "Soft and green.", "Small and purple.")
                .Radio(radio)
                .MultiSelect(multi, choices: new[] { "Apple", "Pear", "Plum" })
                .Text(text).MaxLength(40)
                .Text(area, multiline: true, lines: 4)
                .Color(color)
                .DateTime(when, label: "Date and time")
                .Keybind(ControlsDemo.OpenBindId, label: "Open the window")
                .Divider()
            .Section("Actions")
                .Info("Local time", () => DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture))
                .Button("Open window", () => window.Open(), help: "The same controls in a floating window in the game's look (UiLook.GameWindow).", label: "Floating window")
                .Button("Log a line", () => log(ControlsDemo.LogPrefix + "settings button confirmed"),
                    confirm: "Write a line to the log?", label: "Confirmed button")
                .HideWhenAlone());
    }

    private static ConfigEntry<T> Watch<T>(PluginContext ctx, Action<string> log, ConfigEntry<T> entry)
    {
        EventHandler handler = (_, _) => log(ControlsDemo.LogPrefix + "setting " + entry.Definition.Key + " = " + entry.BoxedValue);
        entry.SettingChanged += handler;
        ctx.Track(new Unsubscribe(() => entry.SettingChanged -= handler));
        return entry;
    }

    private sealed class Unsubscribe : IDisposable
    {
        private Action? _undo;
        internal Unsubscribe(Action undo) => _undo = undo;
        public void Dispose() { _undo?.Invoke(); _undo = null; }
    }
}
