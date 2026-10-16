using System;
using System.Collections.Generic;

namespace Progenitor.ControlsSample;

/// <summary>
/// The gallery's inventory: every control, widget, window and message call the Progenitor UI kit offers, grouped by
/// category, each with its API, a one-line "use this when", where the gallery shows it, the older sample that also uses
/// it, and its text-style checklist line. docs/ui-controls.md is the same table in prose; ControlsSampleTests holds the
/// two together, so a control added to one and not the other fails the suite. Game-free.
/// </summary>
internal static class GalleryCatalog
{
    internal const string Windows = "Windows";
    internal const string Input = "Input";
    internal const string Lists = "Lists";
    internal const string Feedback = "Feedback";
    internal const string Hud = "HUD";
    internal const string Panels = "Game panels";

    /// <summary>The gallery's tabs, in order (one per category).</summary>
    internal static readonly string[] Categories = { Windows, Input, Lists, Feedback, Hud, Panels };

    /// <summary>The long name of each category, for headings and the docs.</summary>
    internal static string LongName(string category) => category switch
    {
        Windows => "Layout and windows",
        Input => "Input",
        Lists => "Lists and data",
        Feedback => "Feedback and messages",
        Hud => "HUD and world",
        Panels => "Game-panel attachments",
        _ => category,
    };

    internal sealed class Entry
    {
        internal Entry(string id, string category, string control, string api, string useWhen, string gallery, string sample, string style)
        {
            Id = id;
            Category = category;
            Control = control;
            Api = api;
            UseWhen = useWhen;
            Gallery = gallery;
            Sample = sample;
            Style = style;
        }

        /// <summary>Stable key: the gallery builds each entry by it.</summary>
        internal string Id { get; }

        internal string Category { get; }

        /// <summary>The control's name as the docs and the gallery caption it.</summary>
        internal string Control { get; }

        /// <summary>The call a mod writes.</summary>
        internal string Api { get; }

        /// <summary>One line: use this when ...</summary>
        internal string UseWhen { get; }

        /// <summary>Where the gallery shows it.</summary>
        internal string Gallery { get; }

        /// <summary>The older sample that also uses it (file and how to reach it), or "none".</summary>
        internal string Sample { get; }

        /// <summary>The text-style checklist line: which named style the control draws its words in, and the alignment.</summary>
        internal string Style { get; }
    }

    private const string ControlsPage = "Controls sample, All controls page";
    private const string WidgetsPage = "Controls sample, Widgets page";
    private const string SettingsPage = "Controls sample, settings page";

    private static Entry E(string id, string category, string control, string api, string useWhen, string gallery, string sample, string style) =>
        new(id, category, control, api, useWhen, gallery, sample, style);

    /// <summary>Every entry, in gallery order.</summary>
    internal static readonly IReadOnlyList<Entry> All = new[]
    {
        // ---- Layout and windows ----------------------------------------------------------------------------------
        E("window-basic", Windows, "Basic window", "ctx.Windows.Create(UiWindowPreset.Basic, title)",
            "a small tool used during play: a dropdown and a few buttons.", "Windows tab, Basic window", "none (new)",
            "Title centered in the frame; rows Body, left-aligned; stamp Muted, centered."),
        E("window-header", Windows, "Header window", "ctx.Windows.Create(UiWindowPreset.Header, title, options)",
            "one thing the player works on (a container, a named set), with a toolbar and tabs.", "Windows tab, Header window", "none (new)",
            "Title centered between the ornaments; toolbar captions centered on their buttons; rows Body, left-aligned."),
        E("window-tabbed", Windows, "Tabbed window", "ctx.Windows.Create(UiWindowPreset.Tabbed, title, options)",
            "a large screen like the game's own menus: tabs, an info column and the main content.", "Windows tab, Tabbed window", "none (new)",
            "Title centered; tab captions centered; info column Section and Body, left-aligned; numbers right-aligned."),
        E("floating-panel", Windows, "Floating panel", "Ui.Panel(title, ctx.Owner, ctx.Guid).GameWindow()",
            "none of the presets fits and the window is laid out by hand.", "Windows tab, Floating panel", "ControlsSample settings page, Floating window (ControlsWindow)",
            "Title centered; rows follow ContentAlignment (set left)."),
        E("mods-page", Windows, "Mods page", "ctx.Menu.Register(name, description).Page(title, build)",
            "the mod needs a screen of its own in the Mods menu.", "Windows tab, Open All controls", ControlsPage,
            "Rows on the settings grid: names Body left, controls in a fixed column."),
        E("settings-page", Windows, "Settings page", "ctx.Settings.Page(page => ...)",
            "options the player sets once and forgets.", "Windows tab, Settings page", SettingsPage,
            "Names Body left; help in the description column; section labels Section."),
        E("text-styles", Windows, "Text styles", "panel.Text(text, UiTextStyle.Body | Title | Section | Muted | Number | Warning | Error | Success)",
            "any words on a page or window: pick the style, never a color or a size.", "Windows tab, Text styles", "none (new)",
            "Every named style (Title, Section, Body, Muted, Number, Warning, Error, Success) fixes font, size, color and alignment."),
        E("name-value", Windows, "Name and value", "panel.Text(name, value, UiTextStyle.Number)",
            "a figure the player compares down a column (counts, amounts, times).", "Windows tab, Text styles", "none (new)",
            "Name Body left; value Number, right-aligned."),
        E("heading", Windows, "Heading", "panel.Heading(text)",
            "a topic starts; a page has a few.", "every tab, the headings", ControlsPage,
            "Section, left-aligned, with the section rule."),
        E("note", Windows, "Note", "panel.Note(text)",
            "a topic needs one short explanation under its heading.", "every entry's line under its control", ControlsPage,
            "Muted, left-aligned."),
        E("tone-label", Windows, "Status line", "panel.Label(text, UiTextStyles.ToneOf(UiTextStyle.X)), then SetText(text, tone)",
            "a status that changes in place (SetText with a tone).", "Windows tab, Status line", ControlsPage + " (status line)",
            "Body, Muted, Success, Warning or Error by tone; left-aligned."),
        E("fact", Windows, "Fact", "panel.Fact(name, value)",
            "a value that does not change while the page is open.", "Windows tab, Fact", ControlsPage + " (Plugin)",
            "Name Body left; value Body in the control column."),
        E("divider", Windows, "Divider", "panel.Divider()",
            "an action block needs separating from what precedes it.", "Windows tab, Divider", ControlsPage,
            "No text."),
        E("inline", Windows, "Inline row", "panel.BeginInline() ... panel.EndInline()",
            "related buttons (or a field and its button) belong side by side.", "Windows tab, Inline row", ControlsPage + " (Inline A / Inline B)",
            "Captions centered on their buttons."),
        E("tabs", Windows, "Tabs", "panel.Tabs(key, captions)",
            "groups of controls do not fit one page (two to five short captions).", "the gallery's own tab row", WidgetsPage + " (tab row)",
            "Captions centered on the tabs; badges Number."),
        E("footer", Windows, "Version stamp", "panel.Footer / IUiWindow.VersionStamp",
            "a window shows which mod and version drew it (on by default in the presets).", "every window's bottom line", ControlsPage + " (footer)",
            "Muted, small, centered."),
        E("scroll", Windows, "Scrolling", "automatic: a panel scrolls when its rows do not fit",
            "a page has more rows than the screen; nothing to call.", "every long gallery tab", ControlsPage,
            "Rows keep their styles (Body, Muted, Number) while scrolled."),
        E("capture", Windows, "Input mode", "panel.CaptureMode = UiCaptureMode.WhenHovered | WhileShown | Never",
            "a floating window decides whether the game keeps the input (the presets set it).", "Windows tab, Basic and Header windows", "ControlsSample ControlsWindow (WhileShown)",
            "No text."),
        E("game-style", Windows, "Game button look", "button.GameStyle()",
            "a button on a plain floating panel must look like the game's (kit pages already do).", "Windows tab, Game button", ControlsPage,
            "Caption centered on the button."),

        // ---- Input -------------------------------------------------------------------------------------------------
        E("button", Input, "Button", "panel.Button(caption).OnClick(...)",
            "an action.", "Input tab, Button", ControlsPage + " (Plain button)", "Caption centered on the button."),
        E("confirm-button", Input, "Confirm button", "panel.ConfirmButton(caption, question)",
            "an action that cannot be undone.", "Input tab, Confirm button", ControlsPage, "Caption centered; the dialog's title Title, body Muted."),
        E("toggle", Input, "Toggle", "panel.Toggle(name, initial)",
            "on or off.", "Input tab, Toggle", ControlsPage, "Name Body left; the game's checkbox at the column's right."),
        E("slider", Input, "Slider", "panel.Slider(name, min, max, value)",
            "a number where the rough amount matters.", "Input tab, Slider", ControlsPage, "Name Body left."),
        E("slider-field", Input, "Slider field", "panel.SliderField(name, min, max, step, value)",
            "a number where the exact value matters, in a range to drag through.", "Input tab, Slider field", ControlsPage, "Name Body left; value Number, right-aligned in its box."),
        E("number", Input, "Number", "panel.Number(name, value, min, max, step, decimals, units)",
            "a small exact count with steps.", "Input tab, Number", ControlsPage, "Name Body left; value Number, right-aligned."),
        E("dropdown", Input, "Dropdown", "panel.Dropdown(name, values, value, labels, descriptions)",
            "one of many options, or long option names.", "Input tab, Dropdown", ControlsPage, "Name Body left; value Body; descriptions Muted."),
        E("radio", Input, "Radio", "panel.Radio(name, values, value, labels, descriptions)",
            "one of two to four short options the player should see together.", "Input tab, Radio", ControlsPage, "Options Body left; the chosen one bright, the others dimmed."),
        E("multiselect", Input, "Multi-select", "panel.MultiSelect(name, values, selected)",
            "several of a few options.", "Input tab, Multi-select", ControlsPage, "Options Body left."),
        E("input", Input, "Text input", "panel.Input(name, initial)",
            "one line of free text.", "Input tab, Text input", ControlsPage, "Name Body left; typed text Body, left-aligned."),
        E("textarea", Input, "Text area", "panel.TextArea(name, initial, lines)",
            "several lines of text.", "Input tab, Text area", ControlsPage, "Typed text Body, left-aligned."),
        E("color", Input, "Color", "panel.ColorField(name, hex, alpha)",
            "a color the player picks.", "Input tab, Color", ControlsPage, "Name Body left; hex Number."),
        E("datetime", Input, "Date and time", "panel.DateTimeField(name, isoUtc, zone)",
            "a date and time (stored as ISO 8601 UTC).", "Input tab, Date and time", ControlsPage, "Name Body left; value Body."),
        E("keyfield", Input, "Key field", "panel.KeyField(name, ctx.Guid, bindId)",
            "the player rebinds a key the mod registered with ctx.Keybinds.", "Input tab, Key field", ControlsPage + " (Page key)", "Name Body left; key cap centered."),
        E("keybind", Input, "Keybind", "ctx.Keybinds.Register / ctx.Keybinds.RegisterOpener",
            "the mod acts on a key (never read keys yourself).", "Input tab, the gallery's own Ctrl+F7", "ControlsSample Plugin.cs (Ctrl+F7)", "Shown in the Keybinds tab: name Body left, key centered."),
        E("picker", Input, "Picker", "panel.Picker(key, entries)",
            "one of many named things, with search.", "Input tab, Picker", WidgetsPage + ", Input tab", "Entries Body left; notes Muted; disabled rows say why."),
        E("picker-multi", Input, "Multi picker", "panel.Picker(key, entries, new UiPickerOptions { Multi = true })",
            "several of many named things.", "Input tab, Multi picker", WidgetsPage + ", Input tab", "As the picker; the status line Muted."),
        E("code", Input, "Code area", "panel.CodeArea(name, initial, lines)",
            "a script or rules text, colored by the mod's highlighter.", "Input tab, Code area", WidgetsPage + ", Input tab", "Body in the game's font; line numbers right-aligned; errors Error."),
        E("set-setting", Input, "Settings row (any)", "page.Setting(entry)",
            "a config entry whose control the kit picks from its type.", "Windows tab, Settings page", SettingsPage, "Name Body left; help Muted."),
        E("set-toggle", Input, "Settings toggle", "page.Toggle(entry)", "a bool option.", "Windows tab, Settings page", SettingsPage, "Name Body left."),
        E("set-number", Input, "Settings number", "page.Number(entry, min, max, step, units)", "an exact numeric option.", "Windows tab, Settings page", SettingsPage, "Value Number, right-aligned."),
        E("set-slider", Input, "Settings slider", "page.Slider(entry, min, max, step)", "a ranged numeric option.", "Windows tab, Settings page", SettingsPage, "Value Number."),
        E("set-dropdown", Input, "Settings dropdown", "page.Dropdown(entry).OptionDescriptions(...)", "a choice among many.", "Windows tab, Settings page", SettingsPage, "Descriptions Muted in the description column."),
        E("set-radio", Input, "Settings radio", "page.Radio(entry)", "a choice among a few, all visible.", "Windows tab, Settings page", SettingsPage, "Options Body left."),
        E("set-multi", Input, "Settings multi-select", "page.MultiSelect(entry, choices)", "several of a few options.", "Windows tab, Settings page", SettingsPage, "Options Body left."),
        E("set-text", Input, "Settings text", "page.Text(entry).MaxLength(n) / page.Text(entry, multiline: true)", "a free-text option.", "Windows tab, Settings page", SettingsPage, "Typed text Body, left-aligned."),
        E("set-color", Input, "Settings color", "page.Color(entry)", "a color option.", "Windows tab, Settings page", SettingsPage, "Hex Number."),
        E("set-datetime", Input, "Settings date and time", "page.DateTime(entry)", "a date-time option.", "Windows tab, Settings page", SettingsPage, "Value Body."),
        E("set-keybind", Input, "Settings keybind", "page.Keybind(bindId)", "a key the mod registered, shown with its options.", "Windows tab, Settings page", SettingsPage, "Key centered."),
        E("set-section", Input, "Settings section and divider", "page.Section(name) / page.Divider()", "the options fall into topics.", "Windows tab, Settings page", SettingsPage, "Section, left-aligned."),
        E("set-info", Input, "Settings info and button", "page.Info(name, value) / page.Button(caption, action, confirm:)", "a live value or an action beside the options.", "Windows tab, Settings page", SettingsPage, "Info value Body; button caption centered."),
        E("set-scope", Input, "Settings scope", "page.InScope(SettingScope) / page.HideWhenAlone()", "options apply per server or should hide when alone.", "Windows tab, Settings page", SettingsPage, "No text of its own."),

        // ---- Lists and data -----------------------------------------------------------------------------------------
        E("ordered-list", Lists, "Ordered list", "panel.OrderedList(key, items)",
            "an order the player arranges (a priority list).", "Lists tab, Ordered list", WidgetsPage + ", Lists tab", "Items Body left; notes Muted; badges Number."),
        E("data-table", Lists, "Data table", "panel.DataTable(key, columns, options)",
            "many records the player sorts, filters, selects and edits.", "Lists tab, Data table", WidgetsPage + ", Table tab", "Text columns left; number columns Number, right-aligned; headers centered over their columns' alignment."),
        E("master-detail", Lists, "Master-detail", "panel.MasterDetail(key, options)",
            "a list of named sets, each edited on its own.", "Lists tab, Master-detail", WidgetsPage + ", Layout tab", "List Body left; detail headings Section; text Muted."),
        E("chips", Lists, "Chips", "panel.Chips(key, chips)",
            "a few short values the player adds and removes.", "Lists tab, Chips", WidgetsPage + ", Lists tab", "Captions centered on the chips."),
        E("info", Lists, "Live readout", "panel.Info(name, () => value, refreshSeconds)",
            "a value that changes while the page is open.", "Lists tab, Live readout", ControlsPage + " (Events so far)", "Name Body left; value Body."),
        E("item-icons", Lists, "Item icons", "UiPickerEntry.ItemPrefab / UiChip / UiMasterEntry.ItemPrefab",
            "a row names a game item: show the game's own icon.", "Lists tab, Picker and Chips rows", WidgetsPage + ", Input and Lists tabs", "Icon left of a Body caption."),
        E("pending", Lists, "Pending state", "LabelTone.Pending / a data table edit waiting for the server",
            "something is in progress; say so in words, never a bare spinner.", "Lists tab, Status line", WidgetsPage + ", Table tab", "Warning tone, left-aligned, with the words saying what waits."),

        // ---- Feedback and messages ----------------------------------------------------------------------------------
        E("notify", Feedback, "Notification", "ctx.Hud.Notify(text, detail, UiNoticeKind.Info | Success | Warning | Error)",
            "a short event message during play; shown as 'Mod Name: text'.", "Feedback tab, the four kinds", WidgetsPage + ", HUD tab", "The game's banner font, centered; the band's rules tinted by kind."),
        E("notify-plain", Feedback, "Notification without prefix", "ctx.Hud.Notify(text, prefix: false)",
            "the text already names the mod another way.", "Feedback tab, No prefix", "none (new)", "As the notification."),
        E("confirm", Feedback, "Confirm dialog", "ctx.Dialogs.Confirm(title, body, yes, onYes)",
            "a yes/no question.", "Feedback tab, Confirm", ControlsPage + " (Dialogs)", "Title, Muted body, centered; buttons centered captions."),
        E("notice", Feedback, "Notice dialog", "ctx.Dialogs.Notice(title, body)",
            "something the player must read and acknowledge.", "Feedback tab, Notice", ControlsPage + " (Dialogs)", "Title, Muted body, centered."),
        E("choose", Feedback, "Choose dialog", "ctx.Dialogs.Choose(title, body, options, onPick)",
            "a pick from a few options in a popup.", "Feedback tab, Choose", ControlsPage + " (Dialogs)", "Options' captions centered."),
        E("prompt", Feedback, "Prompt dialog", "ctx.Dialogs.Prompt(title, body, initial, onSubmit)",
            "a line of text in a popup (a name).", "Feedback tab, Prompt", ControlsPage + " (Dialogs)", "Typed text Body, left-aligned; error Error."),
        E("tooltip", Feedback, "Tooltip", "handle.Tooltip(text)",
            "extra words about a control (its name must stand alone).", "every gallery control's hover", WidgetsPage + " (Reset list)", "Body in the game's tooltip box, left-aligned."),
        E("term", Feedback, "Term", "UiTerms.Define(pluginId, term, explanation) and handle.Term(term)",
            "a word of the mod's own the player may not know.", "Feedback tab, Term", WidgetsPage + ", HUD tab", "Body; the circle-i after the caption."),
        E("term-markers", Feedback, "Term markers toggle", "panel.TermMarkersToggle()",
            "the player wants the help markers back.", "Feedback tab, Term", WidgetsPage + ", HUD tab", "Name Body left."),
        E("tip", Feedback, "Tip card", "ctx.Hud.Tip(id, title, body, options)",
            "a one-time explanation of a screen.", "Feedback tab, Tip card", WidgetsPage + ", HUD tab", "Title, Body, left-aligned."),
        E("chat-reply", Feedback, "Command reply", "ICommandContext.Reply(text) / Reply(text, prefix: false)",
            "a chat command answers the player; shown as 'Mod Name: text'.", "Feedback tab, a note (server side)", "Progenitor.Sample SampleCommands.cs", "The game's chat style."),
        E("chat-send", Feedback, "Chat message", "ctx.Chat.Broadcast(text) / SendTo(user, text, prefix: false)",
            "the server tells players something in chat; shown as 'Mod Name: text'.", "Feedback tab, a note (server side)", "Progenitor.Sample SampleChat.cs", "The game's chat style."),

        // ---- HUD and world ------------------------------------------------------------------------------------------
        E("locate", Hud, "Locate", "ctx.Hud.Locate(entity or position, label, seconds)",
            "the player must find something in the world.", "HUD tab, Locate last pick", WidgetsPage + ", HUD tab", "Label Body, centered on the marker."),
        E("pick", Hud, "Pick in world", "ctx.Hud.PickInWorld(onPicked, onCancelled)",
            "the player chooses a chest or station by interacting with it.", "HUD tab, Pick in world", WidgetsPage + ", HUD tab", "The game's own interact prompt."),
        E("pick-repeat", Hud, "Pick several", "ctx.Hud.PickInWorld(..., new UiWorldPickOptions { Repeat = true })",
            "the player adds several chests or stations in a row.", "HUD tab, Pick several", WidgetsPage + ", Layout tab", "As pick in world."),

        // ---- Game-panel attachments ---------------------------------------------------------------------------------
        E("attach-container", Panels, "Container window", "Ui.AttachToVanilla(VanillaUiSite.ContainerPanel, anchor, ctx.Owner, ctx.Guid)",
            "a button or a line belongs on a chest's window.", "Game panels tab, a note", "Progenitor.PanelsExample (Deposit, Output, Locate)", "The game's own button; caption centered."),
        E("attach-station", Panels, "Station window", "Ui.AttachToVanilla(VanillaUiSite.StationPanel, anchor, ...)",
            "a button belongs on a workstation or refinement window.", "Game panels tab, a note", "Progenitor.PanelsExample (Inputs, Outputs)", "The game's own button."),
        E("attach-inventory", Panels, "Inventory bar", "Ui.AttachToVanilla(VanillaUiSite.InventoryPanel, VanillaPanelAnchor.BesideSort, ...)",
            "one short action beside the inventory's sort.", "Game panels tab, a note", "Progenitor.PanelsExample (Stash)", "An icon with its caption as tooltip (Body), centered on the button."),
        E("attach-heart", Panels, "Castle heart window", "Ui.AttachToVanilla(VanillaUiSite.CastleHeartPanel, anchor, ...)",
            "a button belongs on the castle heart.", "Game panels tab, a note", "Progenitor.PanelsExample (Castles, Fuel)", "The game's own button."),
        E("attach-tooltip", Panels, "Item tooltip line", "Ui.AttachToVanilla(VanillaUiSite.InventoryTooltip, VanillaPanelAnchor.TooltipBody, ...)",
            "a line of the mod's own on an item's tooltip.", "Game panels tab, a note", "Progenitor.PanelsExample (tooltip line)", "Body, left-aligned, in the tooltip."),
        E("attach-map", Panels, "Map rows", "Ui.AttachToVanilla(VanillaUiSite.MapWarEventInfo, ...)",
            "rows beside the map's war event info.", "Game panels tab, a note", "Progenitor.Sample SampleRifts.cs", "Body, left-aligned."),
    };

    /// <summary>The entries of one category, in order.</summary>
    internal static List<Entry> In(string category)
    {
        var list = new List<Entry>();
        foreach (var e in All) if (string.Equals(e.Category, category, StringComparison.Ordinal)) list.Add(e);
        return list;
    }
}
