using System;
using System.Collections.Generic;

namespace Progenitor.ControlsSample;

/// <summary>
/// The gallery's inventory: every control, widget, window and message call the Progenitor UI kit offers, grouped by
/// category, each with its API, one sentence on when to use it, where the gallery shows it, the older sample that also uses
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

        /// <summary>When to use it: one short, complete sentence (the gallery shows it under the control as written).</summary>
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
            "Use it for a small tool the player opens during play, such as a dropdown and a few buttons.", "Windows tab, Basic window", "none (new)",
            "Title centered in the frame; rows Body, left-aligned; stamp Muted, centered."),
        E("window-header", Windows, "Header window", "ctx.Windows.Create(UiWindowPreset.Header, title, options)",
            "Use it for one thing the player works on, such as a chest or a named set, with a toolbar and tabs.", "Windows tab, Header window", "none (new)",
            "Title centered between the ornaments; toolbar captions centered on their buttons; rows Body, left-aligned."),
        E("window-tabbed", Windows, "Tabbed window", "ctx.Windows.Create(UiWindowPreset.Tabbed, title, options)",
            "Use it for a large screen like the game's own menus, with tabs, an info column and the main content.", "Windows tab, Tabbed window", "none (new)",
            "Title centered; tab captions centered; info column Section and Body, left-aligned; numbers right-aligned."),
        E("floating-panel", Windows, "Floating panel", "Ui.Panel(title, ctx.Owner, ctx.Guid).GameWindow()",
            "Use it only when no preset fits and you lay the window out yourself.", "Windows tab, Floating panel", "ControlsSample settings page, Floating window (ControlsWindow)",
            "Title centered; rows follow ContentAlignment (set left)."),
        E("docked-panel", Windows, "Docked panel", "panel.Dock(UiDock.Left, margin) / panel.Dock(side, new UiDockOptions { Align, Offset, Width, WidthFraction }); sides Left, Right, Top, Bottom",
            "Use it for controls that sit beside the scene, such as a preview's buttons or a Find panel, so the middle of the screen stays clear, or for a strip above or below the game's windows.", "Windows tab, Docked panel", "none (new)",
            "Title centered; rows follow ContentAlignment (set left); buttons captions centered."),
        E("mods-page", Windows, "Mods page", "ctx.Menu.Register(name, description).Page(title, build)",
            "Use it when the mod needs a screen of its own in the Mods menu.", "Windows tab, Open All controls", ControlsPage,
            "Rows on the settings grid: names Body left, controls in a fixed column."),
        E("settings-page", Windows, "Settings page", "ctx.Settings.Page(page => ...)",
            "Use it for options the player sets once and then forgets.", "Windows tab, Settings page", SettingsPage,
            "Names Body left; help in the description column; section labels Section."),
        E("text-styles", Windows, "Text styles", "panel.Text(text, UiTextStyle.Body | Title | Section | Muted | Number | Warning | Error | Success)",
            "Give every line of text a named style instead of a color or a size.", "Windows tab, Text styles", "none (new)",
            "Every named style (Title, Section, Body, Muted, Number, Warning, Error, Success) fixes font, size, color and alignment."),
        E("name-value", Windows, "Name and value", "panel.Text(name, value, UiTextStyle.Number)",
            "Use it for a figure the player compares down a column, such as a count, an amount or a time.", "Windows tab, Text styles", "none (new)",
            "Name Body left; value Number, right-aligned."),
        E("heading", Windows, "Heading", "panel.Heading(text)",
            "Start each topic with a heading; a page has only a few.", "every tab, the headings", ControlsPage,
            "Section, left-aligned, with the section rule."),
        E("note", Windows, "Note", "panel.Note(text)",
            "Add one short note under a heading when the topic needs explaining.", "every entry's line under its control", ControlsPage,
            "Muted, left-aligned."),
        E("tone-label", Windows, "Status line", "panel.Label(text, UiTextStyles.ToneOf(UiTextStyle.X)), then SetText(text, tone)",
            "Use it for a status that changes in place, rewritten with SetText and a tone.", "Windows tab, Status line", ControlsPage + " (status line)",
            "Body, Muted, Success, Warning or Error by tone; left-aligned."),
        E("fact", Windows, "Fact", "panel.Fact(name, value)",
            "Use it for a value that does not change while the page is open.", "Windows tab, Fact", ControlsPage + " (Plugin)",
            "Name Body left; value Body in the control column."),
        E("divider", Windows, "Divider", "panel.Divider()",
            "Use it to separate a block of actions from the rows above it.", "Windows tab, Divider", ControlsPage,
            "No text."),
        E("inline", Windows, "Inline row", "panel.BeginInline() ... panel.EndInline()",
            "Put related buttons, or a field and its button, side by side.", "Windows tab, Inline row", ControlsPage + " (Inline A / Inline B)",
            "Captions centered on their buttons."),
        E("tabs", Windows, "Tabs", "panel.Tabs(key, captions)",
            "Use tabs when the controls do not fit one page; keep to two to five short captions.", "the gallery's own tab row", WidgetsPage + " (tab row)",
            "Captions centered on the tabs; badges Number."),
        E("game-tabs", Windows, "Game window tabs", "panel.Tabs(key, captions, new UiTabsOptions()) on a game window, or new UiTabsOptions { Look = UiTabsLook.Game }",
            "Use this when a window has tabs, so they look like the game's own window tabs.", "Windows tab, Hub window", "none (new)",
            "Captions centered on the game's tabs, at the tab strip's size."),
        E("tab-height", Windows, "Stable tab height", "panel.Tabs(key, captions, new UiTabsOptions { KeepHeight = true, MinContentHeight = height })",
            "Use this when a window's tabs differ in height, so switching tabs never resizes the window.", "Windows tab, Hub window (Chests tab)", "none (new)",
            "No text of its own."),
        E("tab-history", Windows, "Tab history", "new UiTabsOptions { History = true } (on by default); tabs.Back() / tabs.Forward()",
            "Use this when a window has tabs, so the mouse's back and forward buttons and Alt+Left and Alt+Right step through the tabs the player opened.", "Windows tab, Hub window", "none (new)",
            "No text of its own."),
        E("centered-button", Windows, "Centered button", "panel.Button(caption).Centered()",
            "Use this when one action closes or leaves a window, such as Return, or for a centered row of buttons.", "Windows tab, Centered button", "none (new)",
            "Caption centered on the button; the row centered in the window."),
        E("footer", Windows, "Version stamp", "panel.Footer / IUiWindow.VersionStamp",
            "Every window shows which mod and version drew it; the presets add the stamp for you.", "every window's bottom line", ControlsPage + " (footer)",
            "Muted, small, centered."),
        E("scroll", Windows, "Scrolling", "automatic: a panel scrolls when its rows do not fit",
            "A page scrolls by itself when its rows do not fit; there is nothing to call.", "every long gallery tab", ControlsPage,
            "Rows keep their styles (Body, Muted, Number) while scrolled."),
        E("capture", Windows, "Input mode", "panel.CaptureMode = UiCaptureMode.WhenHovered | WhileShown | Never",
            "Set it on a floating window to decide whether the game keeps the input; the presets set it for you.", "Windows tab, Basic and Header windows", "ControlsSample ControlsWindow (WhileShown)",
            "No text."),
        E("game-style", Windows, "Game button look", "button.GameStyle()",
            "Call it on a button of a plain floating panel so it looks like the game's own; kit pages already do.", "Windows tab, Game button", ControlsPage,
            "Caption centered on the button."),

        // ---- Input -------------------------------------------------------------------------------------------------
        E("button", Input, "Button", "panel.Button(caption).OnClick(...)",
            "Use a button for an action the player takes.", "Input tab, Button", ControlsPage + " (Plain button)", "Caption centered on the button."),
        E("confirm-button", Input, "Confirm button", "panel.ConfirmButton(caption, question)",
            "Use it for an action that cannot be undone.", "Input tab, Confirm button", ControlsPage, "Caption centered; the dialog's title Title, body Muted."),
        E("toggle", Input, "Toggle", "panel.Toggle(name, initial)",
            "Use it for a setting that is on or off.", "Input tab, Toggle", ControlsPage, "Name Body left; the game's checkbox at the column's right."),
        E("slider", Input, "Slider", "panel.Slider(name, min, max, value)",
            "Use it for a number where only the rough amount matters.", "Input tab, Slider", ControlsPage, "Name Body left."),
        E("slider-field", Input, "Slider field", "panel.SliderField(name, min, max, step, value)",
            "Use it for an exact number in a range the player can drag through.", "Input tab, Slider field", ControlsPage, "Name Body left; value Number, right-aligned in its box."),
        E("number", Input, "Number", "panel.Number(name, value, min, max, step, decimals, units)",
            "Use it for a small exact count that changes in steps.", "Input tab, Number", ControlsPage, "Name Body left; value Number, right-aligned."),
        E("dropdown", Input, "Dropdown", "panel.Dropdown(name, values, value, labels, descriptions)",
            "Use it to pick one of many options, or options with long names.", "Input tab, Dropdown", ControlsPage, "Name Body left; value Body; descriptions Muted."),
        E("radio", Input, "Radio", "panel.Radio(name, values, value, labels, descriptions)",
            "Use it to pick one of two to four short options the player should see together.", "Input tab, Radio", ControlsPage, "Options Body left; the chosen one bright, the others dimmed."),
        E("multiselect", Input, "Multi-select", "panel.MultiSelect(name, values, selected)",
            "Use it to pick several of a few options.", "Input tab, Multi-select", ControlsPage, "Options Body left."),
        E("input", Input, "Text input", "panel.Input(name, initial)",
            "Use it for one line of free text.", "Input tab, Text input", ControlsPage, "Name Body left; typed text Body, left-aligned."),
        E("compact-dropdown", Input, "Compact dropdown", "panel.BeginInline(); panel.CompactDropdown(name, values, value, labels); ...; panel.EndInline()",
            "Use this when a few filters belong side by side on one row, such as Scope, Match and Show.", "Input tab, Compact dropdowns", "none (new)",
            "Names Body left of each bar; values Body on the game's dropdown bar."),
        E("input-action", Input, "Field with a button", "panel.SearchInput(name, initial).ActionButton(caption, onAction)",
            "Use this when a field has one action, such as Find at the end of a search box; Enter in the field runs it too.", "Input tab, Search with Find", "none (new)",
            "Typed text Body, left-aligned; the button's caption centered."),
        E("key-hint", Input, "Key hint", "button.KeyHint(bindId | \"game:Interact\")",
            "Use this when a key also presses a button; name a registered keybind so the hint follows a remap.", "Input tab, Key hint", "none (new)",
            "The key centered on the game's key cap at the button's right end; caption centered in the rest."),
        E("textarea", Input, "Text area", "panel.TextArea(name, initial, lines)",
            "Use it for several lines of text.", "Input tab, Text area", ControlsPage, "Typed text Body, left-aligned."),
        E("color", Input, "Color", "panel.ColorField(name, hex, alpha)",
            "Use it for a color the player picks.", "Input tab, Color", ControlsPage, "Name Body left; hex Number."),
        E("datetime", Input, "Date and time", "panel.DateTimeField(name, isoUtc, zone)",
            "Use it for a date and a time; the value is stored as ISO 8601 UTC.", "Input tab, Date and time", ControlsPage, "Name Body left; value Body."),
        E("keyfield", Input, "Key field", "panel.KeyField(name, ctx.Guid, bindId)",
            "Use it to let the player rebind a key the mod registered with ctx.Keybinds.", "Input tab, Key field", ControlsPage + " (Page key)", "Name Body left; key cap centered."),
        E("keybind", Input, "Keybind", "ctx.Keybinds.Register / ctx.Keybinds.RegisterOpener",
            "Register every key the mod acts on; never read keys yourself.", "Input tab, the gallery's own Ctrl+F7", "ControlsSample Plugin.cs (Ctrl+F7)", "Shown in the Keybinds tab: name Body left, key centered."),
        E("picker", Input, "Picker", "panel.Picker(key, entries)",
            "Use it to pick one of many named things, with search.", "Input tab, Picker", WidgetsPage + ", Input tab", "Entries Body left; notes Muted; disabled rows say why."),
        E("picker-multi", Input, "Multi picker", "panel.Picker(key, entries, new UiPickerOptions { Multi = true })",
            "Use it to pick several of many named things.", "Input tab, Multi picker", WidgetsPage + ", Input tab", "As the picker; the status line Muted."),
        E("code", Input, "Code area", "panel.CodeArea(name, initial, lines)",
            "Use it for a script or rules text, colored by the mod's highlighter.", "Input tab, Code area", WidgetsPage + ", Input tab", "Body in the game's font; line numbers right-aligned; errors Error."),
        E("set-setting", Input, "Settings row (any)", "page.Setting(entry)",
            "Use it for a config entry; the kit picks the control from the entry's type.", "Windows tab, Settings page", SettingsPage, "Name Body left; help Muted."),
        E("set-toggle", Input, "Settings toggle", "page.Toggle(entry)", "Use it for an option that is on or off.", "Windows tab, Settings page", SettingsPage, "Name Body left."),
        E("set-number", Input, "Settings number", "page.Number(entry, min, max, step, units)", "Use it for an exact numeric option.", "Windows tab, Settings page", SettingsPage, "Value Number, right-aligned."),
        E("set-slider", Input, "Settings slider", "page.Slider(entry, min, max, step)", "Use it for a numeric option in a range.", "Windows tab, Settings page", SettingsPage, "Value Number."),
        E("set-dropdown", Input, "Settings dropdown", "page.Dropdown(entry).OptionDescriptions(...)", "Use it for an option with many choices.", "Windows tab, Settings page", SettingsPage, "Descriptions Muted in the description column."),
        E("set-radio", Input, "Settings radio", "page.Radio(entry)", "Use it for an option with a few choices, all shown at once.", "Windows tab, Settings page", SettingsPage, "Options Body left."),
        E("set-multi", Input, "Settings multi-select", "page.MultiSelect(entry, choices)", "Use it for an option that takes several of a few choices.", "Windows tab, Settings page", SettingsPage, "Options Body left."),
        E("set-text", Input, "Settings text", "page.Text(entry).MaxLength(n) / page.Text(entry, multiline: true)", "Use it for an option the player types.", "Windows tab, Settings page", SettingsPage, "Typed text Body, left-aligned."),
        E("set-color", Input, "Settings color", "page.Color(entry)", "Use it for a color option.", "Windows tab, Settings page", SettingsPage, "Hex Number."),
        E("set-datetime", Input, "Settings date and time", "page.DateTime(entry)", "Use it for a date and time option.", "Windows tab, Settings page", SettingsPage, "Value Body."),
        E("set-keybind", Input, "Settings keybind", "page.Keybind(bindId)", "Use it to show a key the mod registered among its options.", "Windows tab, Settings page", SettingsPage, "Key centered."),
        E("set-section", Input, "Settings section and divider", "page.Section(name) / page.Divider()", "Use them when the options fall into topics.", "Windows tab, Settings page", SettingsPage, "Section, left-aligned."),
        E("set-info", Input, "Settings info and button", "page.Info(name, value) / page.Button(caption, action, confirm:)", "Use them for a live value or an action beside the options.", "Windows tab, Settings page", SettingsPage, "Info value Body; button caption centered."),
        E("set-scope", Input, "Settings scope", "page.InScope(SettingScope) / page.HideWhenAlone()", "Use them when options apply per server or should hide when the player is alone.", "Windows tab, Settings page", SettingsPage, "No text of its own."),

        // ---- Lists and data -----------------------------------------------------------------------------------------
        E("ordered-list", Lists, "Ordered list", "panel.OrderedList(key, items)",
            "Use it for an order the player arranges, such as a priority list.", "Lists tab, Ordered list", WidgetsPage + ", Lists tab", "Items Body left; notes Muted; badges Number."),
        E("data-table", Lists, "Data table", "panel.DataTable(key, columns, options)",
            "Use it for many records the player sorts, filters, selects and edits.", "Lists tab, Data table", WidgetsPage + ", Table tab", "Text columns left; number columns Number, right-aligned; headers centered over their columns' alignment."),
        E("fill-height", Lists, "Table filling the window", "panel.DataTable(key, columns, new UiDataTableOptions { FillHeight = true })",
            "Use this when a table is the main thing in its window, so it takes all the height that is left and scrolls inside it.", "Lists tab, Hub window (Find tab)", "none (new)",
            "As the data table."),
        E("building-icons", Lists, "Building icons", "UiTableRow.BuildingPrefab / ctx.Castles.BuildingIcon(prefabGuid)",
            "Use this when a row names a castle building, so it shows the game's build-menu icon for it.", "Lists tab, Hub window (Find tab)", "none (new)",
            "Icon left of a Body caption; an empty, aligned place until the game has drawn the icon."),
        E("row-actions", Lists, "Table row actions", "UiTableColumnKind.Actions / UiTableRow.Action(id, icon, tooltip, enabled) / table.OnRowAction",
            "Use them when each row needs its own small commands, such as find, move up, move down and remove.", "Lists tab, Hub window (Find tab)", "none (new)",
            "No text of its own: box-less icon buttons in the glyph gold, faint when disabled; a tooltip names each."),
        E("min-rows", Lists, "Table minimum height", "new UiDataTableOptions { MinRows = n }",
            "Use this when a table with few rows should keep its height, so tabs with short and long lists line up.", "Lists tab, Hub window (Find tab)", "none (new)",
            "No text of its own: blank rows with the body's separator rule."),
        E("checklist", Lists, "Checklist", "panel.Checklist(key, entries, options)",
            "Use it to choose several of a long list (hundreds of chests or stations) to act on together.", "Lists tab, Checklist window", "none (new)",
            "As the data table; the game's checkbox first on every row; section headings in the group gold; the status line Muted; Cancel and Confirm centered."),
        E("master-detail", Lists, "Master-detail", "panel.MasterDetail(key, options)",
            "Use it for a list of named sets, each edited on its own.", "Lists tab, Master-detail", WidgetsPage + ", Layout tab", "List Body left; detail headings Section; text Muted."),
        E("chips", Lists, "Chips", "panel.Chips(key, chips)",
            "Use it for a few short values the player adds and removes.", "Lists tab, Chips", WidgetsPage + ", Lists tab", "Captions centered on the chips."),
        E("info", Lists, "Live readout", "panel.Info(name, () => value, refreshSeconds)",
            "Use it for a value that changes while the page is open.", "Lists tab, Live readout", ControlsPage + " (Events so far)", "Name Body left; value Body."),
        E("item-icons", Lists, "Item icons", "UiPickerEntry.ItemPrefab / UiChip / UiMasterEntry.ItemPrefab",
            "Show the game's own icon wherever a row names a game item.", "Lists tab, Picker and Chips rows", WidgetsPage + ", Input and Lists tabs", "Icon left of a Body caption."),
        E("pending", Lists, "Pending state", "LabelTone.Pending / a data table edit waiting for the server",
            "Say in words what is in progress; never show a bare spinner.", "Lists tab, Status line", WidgetsPage + ", Table tab", "Warning tone, left-aligned, with the words saying what waits."),

        // ---- Feedback and messages ----------------------------------------------------------------------------------
        E("notify", Feedback, "Notification", "ctx.Hud.Notify(text, detail, UiNoticeKind.Info | Success | Warning | Error)",
            "Use it for a short message during play; it reads 'Mod Name: text'.", "Feedback tab, the four kinds", WidgetsPage + ", HUD tab", "The game's banner font, centered; the band's rules tinted by kind."),
        E("notify-plain", Feedback, "Notification without prefix", "ctx.Hud.Notify(text, prefix: false)",
            "Turn the prefix off when the text already names the mod.", "Feedback tab, No prefix", "none (new)", "As the notification."),
        E("confirm", Feedback, "Confirm dialog", "ctx.Dialogs.Confirm(title, body, yes, onYes)",
            "Use it to ask a yes or no question.", "Feedback tab, Confirm", ControlsPage + " (Dialogs)", "Title, Muted body, centered; buttons centered captions."),
        E("notice", Feedback, "Notice dialog", "ctx.Dialogs.Notice(title, body)",
            "Use it for something the player must read and acknowledge.", "Feedback tab, Notice", ControlsPage + " (Dialogs)", "Title, Muted body, centered."),
        E("choose", Feedback, "Choose dialog", "ctx.Dialogs.Choose(title, body, options, onPick)",
            "Use it to let the player pick one of a few options in a popup.", "Feedback tab, Choose", ControlsPage + " (Dialogs)", "Options' captions centered."),
        E("prompt", Feedback, "Prompt dialog", "ctx.Dialogs.Prompt(title, body, initial, onSubmit)",
            "Use it to ask for a line of text in a popup, such as a name.", "Feedback tab, Prompt", ControlsPage + " (Dialogs)", "Typed text Body, left-aligned; error Error."),
        E("form", Feedback, "Form dialog", "ctx.Dialogs.Form(title, options, onSave)",
            "Use it to edit a name and a few on/off choices together in a popup.", "Feedback tab, Form", ControlsPage + " (Dialogs)",
            "Caption, heading and box labels left on one edge; boxes the game's checkbox; Save and Cancel centered."),
        E("tooltip", Feedback, "Tooltip", "handle.Tooltip(text)",
            "Use it for extra words about a control; the control's name must still stand alone.", "every gallery control's hover", WidgetsPage + " (Reset list)", "Body in the game's tooltip box, left-aligned."),
        E("term", Feedback, "Term", "UiTerms.Define(pluginId, term, explanation) and handle.Term(term)",
            "Use it for a word of the mod's own that the player may not know.", "Feedback tab, Term", WidgetsPage + ", HUD tab", "Body; the circle-i after the caption."),
        E("term-markers", Feedback, "Term markers toggle", "panel.TermMarkersToggle()",
            "Offer it so the player can bring the help markers back.", "Feedback tab, Term", WidgetsPage + ", HUD tab", "Name Body left."),
        E("tip", Feedback, "Tip card", "ctx.Hud.Tip(id, title, body, options)",
            "Use it once to explain a screen the first time the player opens it.", "Feedback tab, Tip card", WidgetsPage + ", HUD tab", "Title, Body, left-aligned."),
        E("chat-reply", Feedback, "Command reply", "ICommandContext.Reply(text) / Reply(text, prefix: false)",
            "Use it when a chat command answers the player; the reply reads 'Mod Name: text'.", "Feedback tab, a note (server side)", "Progenitor.Sample SampleCommands.cs", "The game's chat style."),
        E("chat-send", Feedback, "Chat message", "ctx.Chat.Broadcast(text) / SendTo(user, text, prefix: false)",
            "Use it when the server tells players something in chat; it reads 'Mod Name: text'.", "Feedback tab, a note (server side)", "Progenitor.Sample SampleChat.cs", "The game's chat style."),

        // ---- HUD and world ------------------------------------------------------------------------------------------
        E("locate", Hud, "Locate", "ctx.Hud.Locate(entity or position, label, seconds)",
            "Use it when the player must find something in the world.", "HUD tab, Locate last pick", WidgetsPage + ", HUD tab", "Label Body, centered on the marker."),
        E("pick", Hud, "Pick in world", "ctx.Hud.PickInWorld(onPicked, onCancelled)",
            "Use it to let the player choose a chest or station by interacting with it.", "HUD tab, Pick in world", WidgetsPage + ", HUD tab", "The game's own interact prompt."),
        E("pick-repeat", Hud, "Pick several", "ctx.Hud.PickInWorld(..., new UiWorldPickOptions { Repeat = true })",
            "Use it to let the player add several chests or stations in a row.", "HUD tab, Pick several", WidgetsPage + ", Layout tab", "As pick in world."),

        // ---- Game-panel attachments ---------------------------------------------------------------------------------
        E("attach-container", Panels, "Container window", "Ui.AttachToVanilla(VanillaUiSite.ContainerPanel, anchor, ctx.Owner, ctx.Guid)",
            "Use it to put a button or a line on a chest's window.", "Game panels tab, a note", "Progenitor.PanelsExample (Deposit, Output, Locate)", "The game's own button; caption centered."),
        E("attach-station", Panels, "Station window", "Ui.AttachToVanilla(VanillaUiSite.StationPanel, anchor, ...)",
            "Use it to put a button on a workstation or refinement window.", "Game panels tab, a note", "Progenitor.PanelsExample (Inputs, Outputs)", "The game's own button."),
        E("attach-inventory", Panels, "Inventory bar", "Ui.AttachToVanilla(VanillaUiSite.InventoryPanel, VanillaPanelAnchor.BesideSort, ...)",
            "Use it for one short action beside the inventory's sort button.", "Game panels tab, a note", "Progenitor.PanelsExample (Stash)", "An icon with its caption as tooltip (Body), centered on the button."),
        E("attach-heart", Panels, "Castle heart window", "Ui.AttachToVanilla(VanillaUiSite.CastleHeartPanel, anchor, ...)",
            "Use it to put a button on the castle heart's window.", "Game panels tab, a note", "Progenitor.PanelsExample (Castles, Fuel)", "The game's own button."),
        E("attach-tooltip", Panels, "Item tooltip line", "Ui.AttachToVanilla(VanillaUiSite.InventoryTooltip, VanillaPanelAnchor.TooltipBody, ...)",
            "Use it to add a line of the mod's own to an item's tooltip.", "Game panels tab, a note", "Progenitor.PanelsExample (tooltip line)", "Body, left-aligned, in the tooltip."),
        E("attach-recipe", Panels, "Recipe tooltip rows", "Ui.AttachToVanilla(VanillaUiSite.RecipeTooltip, VanillaPanelAnchor.TooltipRequirements, ...)",
            "Use it to add lines of the mod's own to a recipe's tooltip in a station or the crafting tab.", "Game panels tab, a note", "Progenitor.PanelsExample (recipe rows)", "Body, in the tooltip; a LabelTone color per row."),
        E("attach-map", Panels, "Map rows", "Ui.AttachToVanilla(VanillaUiSite.MapWarEventInfo, ...)",
            "Use it to add rows beside the map's war event info.", "Game panels tab, a note", "Progenitor.Sample SampleRifts.cs", "Body, left-aligned."),
    };

    /// <summary>The entries of one category, in order.</summary>
    internal static List<Entry> In(string category)
    {
        var list = new List<Entry>();
        foreach (var e in All) if (string.Equals(e.Category, category, StringComparison.Ordinal)) list.Add(e);
        return list;
    }
}
