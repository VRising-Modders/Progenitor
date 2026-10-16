# UI controls catalog

Every control, widget, window, text style and message call the Progenitor UI kit offers, grouped by category: its API, when to use it, where the samples gallery shows it, the older sample that also uses it, and the text style it draws in. Pick from this page first; the [UI style guide](ui-style.md) says where a mod's UI goes and how to word it, and [ui.md](ui.md) holds each control's contract.

The **gallery** is the controls sample's **Gallery** page (`samples/Progenitor.ControlsSample`, [GalleryDemo.cs](../samples/Progenitor.ControlsSample/GalleryDemo.cs)): open the Mods menu at the sample's entry (Ctrl+F7, in a world or at the title screen, opens its **All controls** page) and pick **Gallery**. It has one tab per category below, and on each tab every entry of that category with a live example (or the button that opens where it lives) and one sentence on when to use it. Its three standard windows are [GalleryWindows.cs](../samples/Progenitor.ControlsSample/GalleryWindows.cs). The table below and the gallery are one list ([GalleryCatalog.cs](../samples/Progenitor.ControlsSample/GalleryCatalog.cs)); `WindowPresetTests` fails when the gallery does not build an entry or when an entry has no API, use, location, sample or style.

## Text styles

Words on a page or a window take a named style, `panel.Text(text, UiTextStyle.X)` (or a control that fixes its own). The style fixes the game font, the size, the color and the alignment, so a mod never sets them. Raw colors, sizes and alignment are the exception: a color the player picks (`ColorField`) and a code area's highlighter (`CodeArea`) are the only places the kit takes a color from a mod.

`panel.Text(...)` is an extension method that adds a row; `handle.Text` (the property every handle has) reads or rewrites a row's words. They share the name on purpose: `var line = panel.Text("Idle", UiTextStyle.Muted); line.Text = "Done";`.

The table is one source (`UiTextStyles`): the kit's label rows are drawn at its body size, its cells take their size, tone and alignment from it, and `WindowPresetTests` pins the heading and dialog-title rows to it. Every style uses the kit's body font, the game's Nocturne Serif Regular.

| Style | Font | Size | Color | Alignment | Drawn as | Use when |
|---|---|---|---|---|---|---|
| `Title` | Body font, regular | 24 | The game's popup gray (0.9), with the game's rule under it | Centered | A dialog title row | The name of a block inside a window. A window's own title is drawn by the window, centered in its frame. |
| `Section` | Body font, bold | 24 | White, with the section rule | Left | `panel.Heading` | The label above a group of rows; a page has a few. |
| `Body` | Body font | 20 | Caption cream `#ECE4D8` | Left | A tone label (`Normal`) | Ordinary sentences and values. |
| `Muted` | Body font | 20 | Muted `#A3959A` | Left | A tone label (`Muted`) | A hint, a secondary detail, a value that does not apply now. |
| `Number` | Body font, tabular figures (the digits; a comma, a slash or a space keeps its own width) | 20 | Caption cream `#ECE4D8` | Right | A right-aligned figure cell | A count, an amount, a time or a version, so figures line up down a column. `panel.Text(name, value, UiTextStyle.Number)` puts the name (Body, 20, left) at the left and the figure at the right, sized to its text on one line; a long name is shortened with an ellipsis. |
| `Warning` | Body font | 20 | Amber `#C07A3A` | Left | A tone label (`Warn`) | Something the player should look at, not yet a failure. Also how a pending state reads. |
| `Error` | Body font | 20 | Red `#C8323C` | Left | A tone label (`Danger`) | A failure or a blocking problem; say what to do next. |
| `Success` | Body font | 20 | Green `#6FD08A` | Left | A tone label (`Ok`) | Done, running, enabled. |

Alignment rules the kit applies without being asked: text left; numbers right; captions centered on buttons, tabs and chips; titles centered in the frame (and in a dialog). Every standard window sets its rows left (`ContentAlignment = "left"`). A status that changes in place is a tone label in a style's tone: `panel.Label(text, UiTextStyles.ToneOf(UiTextStyle.Muted))`, then `line.SetText(text, UiTextStyles.ToneOf(UiTextStyle.Success))`.

Status: the style table and the rows it drives are game-free tested (`WindowPresetTests`); `panel.Text` is **not live-verified**.

## The catalog

Each row's **Text style** column is that control's checklist line: check it in the game before calling a page done.

### Layout and windows

Gallery tab: **Windows**.

| Control | API | When to use it | Gallery | Existing sample | Text style |
|---|---|---|---|---|---|
| Basic window | `ctx.Windows.Create(UiWindowPreset.Basic, title)` | Use it for a small tool the player opens during play, such as a dropdown and a few buttons. | Windows tab, Basic window | none (new) | Title centered in the frame; rows Body, left-aligned; stamp Muted, centered. |
| Header window | `ctx.Windows.Create(UiWindowPreset.Header, title, options)` | Use it for one thing the player works on, such as a chest or a named set, with a toolbar and tabs. | Windows tab, Header window | none (new) | Title centered between the ornaments; toolbar captions centered on their buttons; rows Body, left-aligned. |
| Tabbed window | `ctx.Windows.Create(UiWindowPreset.Tabbed, title, options)` | Use it for a large screen like the game's own menus, with tabs, an info column and the main content. | Windows tab, Tabbed window | none (new) | Title centered; tab captions centered; info column Section and Body, left-aligned; numbers right-aligned. |
| Floating panel | `Ui.Panel(title, ctx.Owner, ctx.Guid).GameWindow()` | Use it only when no preset fits and you lay the window out yourself. | Windows tab, Floating panel | ControlsSample settings page, Floating window (ControlsWindow) | Title centered; rows follow ContentAlignment (set left). |
| Mods page | `ctx.Menu.Register(name, description).Page(title, build)` | Use it when the mod needs a screen of its own in the Mods menu. | Windows tab, Open All controls | Controls sample, All controls page | Rows on the settings grid: names Body left, controls in a fixed column. |
| Settings page | `ctx.Settings.Page(page => ...)` | Use it for options the player sets once and then forgets. | Windows tab, Settings page | Controls sample, settings page | Names Body left; help in the description column; section labels Section. |
| Text styles | `panel.Text(text, UiTextStyle.Body \| Title \| Section \| Muted \| Number \| Warning \| Error \| Success)` | Give every line of text a named style instead of a color or a size. | Windows tab, Text styles | none (new) | Every named style (Title, Section, Body, Muted, Number, Warning, Error, Success) fixes font, size, color and alignment. |
| Name and value | `panel.Text(name, value, UiTextStyle.Number)` | Use it for a figure the player compares down a column, such as a count, an amount or a time. | Windows tab, Text styles | none (new) | Name Body left; value Number, right-aligned. |
| Heading | `panel.Heading(text)` | Start each topic with a heading; a page has only a few. | every tab, the headings | Controls sample, All controls page | Section, left-aligned, with the section rule. |
| Note | `panel.Note(text)` | Add one short note under a heading when the topic needs explaining. | every entry's line under its control | Controls sample, All controls page | Muted, left-aligned. |
| Status line | `panel.Label(text, UiTextStyles.ToneOf(UiTextStyle.X)), then SetText(text, tone)` | Use it for a status that changes in place, rewritten with SetText and a tone. | Windows tab, Status line | Controls sample, All controls page (status line) | Body, Muted, Success, Warning or Error by tone; left-aligned. |
| Fact | `panel.Fact(name, value)` | Use it for a value that does not change while the page is open. | Windows tab, Fact | Controls sample, All controls page (Plugin) | Name Body left; value Body in the control column. |
| Divider | `panel.Divider()` | Use it to separate a block of actions from the rows above it. | Windows tab, Divider | Controls sample, All controls page | No text. |
| Inline row | `panel.BeginInline() ... panel.EndInline()` | Put related buttons, or a field and its button, side by side. | Windows tab, Inline row | Controls sample, All controls page (Inline A / Inline B) | Captions centered on their buttons. |
| Tabs | `panel.Tabs(key, captions)` | Use tabs when the controls do not fit one page; keep to two to five short captions. | the gallery's own tab row | Controls sample, Widgets page (tab row) | Captions centered on the tabs; badges Number. |
| Game window tabs | `panel.Tabs(key, captions) on a game window, or panel.Tabs(key, captions, new UiTabsOptions { Look = UiTabsLook.Game })` | Use this when a window has tabs, so they look like the game's own window tabs. | Windows tab, Hub window | none (new) | Captions centered on the game's tabs, at the tab strip's size. |
| Stable tab height | `panel.Tabs(key, captions, new UiTabsOptions { KeepHeight = true, MinContentHeight = height })` | Use this when a window's tabs differ in height, so switching tabs never resizes the window. | Windows tab, Hub window (Chests tab) | none (new) | No text of its own. |
| Tab history | `new UiTabsOptions { History = true } (on by default); tabs.Back() / tabs.Forward()` | Use this when a window has tabs, so the mouse's back and forward buttons and Alt+Left and Alt+Right step through the tabs the player opened. | Windows tab, Hub window | none (new) | No text of its own. |
| Centered button | `panel.Button(caption).Centered()` | Use this when one action closes or leaves a window, such as Return, or for a centered row of buttons. | Windows tab, Centered button | none (new) | Caption centered on the button; the row centered in the window. |
| Version stamp | `panel.Footer / IUiWindow.VersionStamp` | Every window shows which mod and version drew it; the presets add the stamp for you. | every window's bottom line | Controls sample, All controls page (footer) | Muted, small, centered. |
| Scrolling | `automatic: a panel scrolls when its rows do not fit` | A page scrolls by itself when its rows do not fit; there is nothing to call. | every long gallery tab | Controls sample, All controls page | Rows keep their styles (Body, Muted, Number) while scrolled. |
| Input mode | `panel.CaptureMode = UiCaptureMode.WhenHovered \| WhileShown \| Never` | Set it on a floating window to decide whether the game keeps the input; the presets set it for you. | Windows tab, Basic and Header windows | ControlsSample ControlsWindow (WhileShown) | No text. |
| Game button look | `button.GameStyle()` | Call it on a button of a plain floating panel so it looks like the game's own; kit pages already do. | Windows tab, Game button | Controls sample, All controls page | Caption centered on the button. |

### Input

Gallery tab: **Input**.

| Control | API | When to use it | Gallery | Existing sample | Text style |
|---|---|---|---|---|---|
| Button | `panel.Button(caption).OnClick(...)` | Use a button for an action the player takes. | Input tab, Button | Controls sample, All controls page (Plain button) | Caption centered on the button. |
| Confirm button | `panel.ConfirmButton(caption, question)` | Use it for an action that cannot be undone. | Input tab, Confirm button | Controls sample, All controls page | Caption centered; the dialog's title Title, body Muted. |
| Toggle | `panel.Toggle(name, initial)` | Use it for a setting that is on or off. | Input tab, Toggle | Controls sample, All controls page | Name Body left; the game's checkbox at the column's right. |
| Slider | `panel.Slider(name, min, max, value)` | Use it for a number where only the rough amount matters. | Input tab, Slider | Controls sample, All controls page | Name Body left. |
| Slider field | `panel.SliderField(name, min, max, step, value)` | Use it for an exact number in a range the player can drag through. | Input tab, Slider field | Controls sample, All controls page | Name Body left; value Number, right-aligned in its box. |
| Number | `panel.Number(name, value, min, max, step, decimals, units)` | Use it for a small exact count that changes in steps. | Input tab, Number | Controls sample, All controls page | Name Body left; value Number, right-aligned. |
| Dropdown | `panel.Dropdown(name, values, value, labels, descriptions)` | Use it to pick one of many options, or options with long names. | Input tab, Dropdown | Controls sample, All controls page | Name Body left; value Body; descriptions Muted. |
| Radio | `panel.Radio(name, values, value, labels, descriptions)` | Use it to pick one of two to four short options the player should see together. | Input tab, Radio | Controls sample, All controls page | Options Body left; the chosen one bright, the others dimmed. |
| Multi-select | `panel.MultiSelect(name, values, selected)` | Use it to pick several of a few options. | Input tab, Multi-select | Controls sample, All controls page | Options Body left. |
| Text input | `panel.Input(name, initial)` | Use it for one line of free text. | Input tab, Text input | Controls sample, All controls page | Name Body left; typed text Body, left-aligned. |
| Compact dropdown | `panel.BeginInline(); panel.CompactDropdown(name, values, value, labels); ...; panel.EndInline()` | Use this when a few filters belong side by side on one row, such as Scope, Match and Show. | Input tab, Compact dropdowns | none (new) | Names Body left of each bar; values Body on the game's dropdown bar. |
| Field with a button | `panel.SearchInput(name, initial).Action(caption, onAction)` | Use this when a field has one action, such as Find at the end of a search box; Enter in the field runs it too. | Input tab, Search with Find | none (new) | Typed text Body, left-aligned; the button's caption centered. |
| Key hint | `button.KeyHint("Esc" \| bindId \| "game:Interact")` | Use this when a key also presses a button; name a registered keybind so the hint follows a remap. | Input tab, Key hint | none (new) | The key centered on the game's key cap at the button's right end; caption centered in the rest. |
| Text area | `panel.TextArea(name, initial, lines)` | Use it for several lines of text. | Input tab, Text area | Controls sample, All controls page | Typed text Body, left-aligned. |
| Color | `panel.ColorField(name, hex, alpha)` | Use it for a color the player picks. | Input tab, Color | Controls sample, All controls page | Name Body left; hex Number. |
| Date and time | `panel.DateTimeField(name, isoUtc, zone)` | Use it for a date and a time; the value is stored as ISO 8601 UTC. | Input tab, Date and time | Controls sample, All controls page | Name Body left; value Body. |
| Key field | `panel.KeyField(name, ctx.Guid, bindId)` | Use it to let the player rebind a key the mod registered with ctx.Keybinds. | Input tab, Key field | Controls sample, All controls page (Page key) | Name Body left; key cap centered. |
| Keybind | `ctx.Keybinds.Register / ctx.Keybinds.RegisterOpener` | Register every key the mod acts on; never read keys yourself. | Input tab, the gallery's own Ctrl+F7 | ControlsSample Plugin.cs (Ctrl+F7) | Shown in the Keybinds tab: name Body left, key centered. |
| Picker | `panel.Picker(key, entries)` | Use it to pick one of many named things, with search. | Input tab, Picker | Controls sample, Widgets page, Input tab | Entries Body left; notes Muted; disabled rows say why. |
| Multi picker | `panel.Picker(key, entries, new UiPickerOptions { Multi = true })` | Use it to pick several of many named things. | Input tab, Multi picker | Controls sample, Widgets page, Input tab | As the picker; the status line Muted. |
| Code area | `panel.CodeArea(name, initial, lines)` | Use it for a script or rules text, colored by the mod's highlighter. | Input tab, Code area | Controls sample, Widgets page, Input tab | Body in the game's font; line numbers right-aligned; errors Error. |
| Settings row (any) | `page.Setting(entry)` | Use it for a config entry; the kit picks the control from the entry's type. | Windows tab, Settings page | Controls sample, settings page | Name Body left; help Muted. |
| Settings toggle | `page.Toggle(entry)` | Use it for an option that is on or off. | Windows tab, Settings page | Controls sample, settings page | Name Body left. |
| Settings number | `page.Number(entry, min, max, step, units)` | Use it for an exact numeric option. | Windows tab, Settings page | Controls sample, settings page | Value Number, right-aligned. |
| Settings slider | `page.Slider(entry, min, max, step)` | Use it for a numeric option in a range. | Windows tab, Settings page | Controls sample, settings page | Value Number. |
| Settings dropdown | `page.Dropdown(entry).OptionDescriptions(...)` | Use it for an option with many choices. | Windows tab, Settings page | Controls sample, settings page | Descriptions Muted in the description column. |
| Settings radio | `page.Radio(entry)` | Use it for an option with a few choices, all shown at once. | Windows tab, Settings page | Controls sample, settings page | Options Body left. |
| Settings multi-select | `page.MultiSelect(entry, choices)` | Use it for an option that takes several of a few choices. | Windows tab, Settings page | Controls sample, settings page | Options Body left. |
| Settings text | `page.Text(entry).MaxLength(n) / page.Text(entry, multiline: true)` | Use it for an option the player types. | Windows tab, Settings page | Controls sample, settings page | Typed text Body, left-aligned. |
| Settings color | `page.Color(entry)` | Use it for a color option. | Windows tab, Settings page | Controls sample, settings page | Hex Number. |
| Settings date and time | `page.DateTime(entry)` | Use it for a date and time option. | Windows tab, Settings page | Controls sample, settings page | Value Body. |
| Settings keybind | `page.Keybind(bindId)` | Use it to show a key the mod registered among its options. | Windows tab, Settings page | Controls sample, settings page | Key centered. |
| Settings section and divider | `page.Section(name) / page.Divider()` | Use them when the options fall into topics. | Windows tab, Settings page | Controls sample, settings page | Section, left-aligned. |
| Settings info and button | `page.Info(name, value) / page.Button(caption, action, confirm:)` | Use them for a live value or an action beside the options. | Windows tab, Settings page | Controls sample, settings page | Info value Body; button caption centered. |
| Settings scope | `page.InScope(SettingScope) / page.HideWhenAlone()` | Use them when options apply per server or should hide when the player is alone. | Windows tab, Settings page | Controls sample, settings page | No text of its own. |

### Lists and data

Gallery tab: **Lists**.

| Control | API | When to use it | Gallery | Existing sample | Text style |
|---|---|---|---|---|---|
| Ordered list | `panel.OrderedList(key, items)` | Use it for an order the player arranges, such as a priority list. | Lists tab, Ordered list | Controls sample, Widgets page, Lists tab | Items Body left; notes Muted; badges Number. |
| Data table | `panel.DataTable(key, columns, options)` | Use it for many records the player sorts, filters, selects and edits. | Lists tab, Data table | Controls sample, Widgets page, Table tab | Text columns left; number columns Number, right-aligned; headers centered over their columns' alignment. |
| Table filling the window | `panel.DataTable(key, columns, new UiDataTableOptions { FillHeight = true })` | Use this when a table is the main thing in its window, so it takes all the height that is left and scrolls inside it. | Lists tab, Hub window (Find tab) | none (new) | As the data table. |
| Building icons | `UiTableRow.BuildingPrefab / ctx.Castles.BuildingIcon(prefabGuid)` | Use this when a row names a castle building, so it shows the game's build-menu icon for it. | Lists tab, Hub window (Find tab) | none (new) | Icon left of a Body caption; an empty, aligned place until the game has drawn the icon. |
| Master-detail | `panel.MasterDetail(key, options)` | Use it for a list of named sets, each edited on its own. | Lists tab, Master-detail | Controls sample, Widgets page, Layout tab | List Body left; detail headings Section; text Muted. |
| Chips | `panel.Chips(key, chips)` | Use it for a few short values the player adds and removes. | Lists tab, Chips | Controls sample, Widgets page, Lists tab | Captions centered on the chips. |
| Live readout | `panel.Info(name, () => value, refreshSeconds)` | Use it for a value that changes while the page is open. | Lists tab, Live readout | Controls sample, All controls page (Events so far) | Name Body left; value Body. |
| Item icons | `UiPickerEntry.ItemPrefab / UiChip / UiMasterEntry.ItemPrefab` | Show the game's own icon wherever a row names a game item. | Lists tab, Picker and Chips rows | Controls sample, Widgets page, Input and Lists tabs | Icon left of a Body caption. |
| Pending state | `LabelTone.Pending / a data table edit waiting for the server` | Say in words what is in progress; never show a bare spinner. | Lists tab, Status line | Controls sample, Widgets page, Table tab | Warning tone, left-aligned, with the words saying what waits. |

### Feedback and messages

Gallery tab: **Feedback**.

| Control | API | When to use it | Gallery | Existing sample | Text style |
|---|---|---|---|---|---|
| Notification | `ctx.Hud.Notify(text, detail, UiNoticeKind.Info \| Success \| Warning \| Error)` | Use it for a short message during play; it reads 'Mod Name: text'. | Feedback tab, the four kinds | Controls sample, Widgets page, HUD tab | The game's banner font, centered; the band's rules tinted by kind. |
| Notification without prefix | `ctx.Hud.Notify(text, prefix: false)` | Turn the prefix off when the text already names the mod. | Feedback tab, No prefix | none (new) | As the notification. |
| Confirm dialog | `ctx.Dialogs.Confirm(title, body, yes, onYes)` | Use it to ask a yes or no question. | Feedback tab, Confirm | Controls sample, All controls page (Dialogs) | Title, Muted body, centered; buttons centered captions. |
| Notice dialog | `ctx.Dialogs.Notice(title, body)` | Use it for something the player must read and acknowledge. | Feedback tab, Notice | Controls sample, All controls page (Dialogs) | Title, Muted body, centered. |
| Choose dialog | `ctx.Dialogs.Choose(title, body, options, onPick)` | Use it to let the player pick one of a few options in a popup. | Feedback tab, Choose | Controls sample, All controls page (Dialogs) | Options' captions centered. |
| Prompt dialog | `ctx.Dialogs.Prompt(title, body, initial, onSubmit)` | Use it to ask for a line of text in a popup, such as a name. | Feedback tab, Prompt | Controls sample, All controls page (Dialogs) | Typed text Body, left-aligned; error Error. |
| Tooltip | `handle.Tooltip(text)` | Use it for extra words about a control; the control's name must still stand alone. | every gallery control's hover | Controls sample, Widgets page (Reset list) | Body in the game's tooltip box, left-aligned. |
| Term | `UiTerms.Define(pluginId, term, explanation) and handle.Term(term)` | Use it for a word of the mod's own that the player may not know. | Feedback tab, Term | Controls sample, Widgets page, HUD tab | Body; the circle-i after the caption. |
| Term markers toggle | `panel.TermMarkersToggle()` | Offer it so the player can bring the help markers back. | Feedback tab, Term | Controls sample, Widgets page, HUD tab | Name Body left. |
| Tip card | `ctx.Hud.Tip(id, title, body, options)` | Use it once to explain a screen the first time the player opens it. | Feedback tab, Tip card | Controls sample, Widgets page, HUD tab | Title, Body, left-aligned. |
| Command reply | `ICommandContext.Reply(text) / Reply(text, prefix: false)` | Use it when a chat command answers the player; the reply reads 'Mod Name: text'. | Feedback tab, a note (server side) | Progenitor.Sample SampleCommands.cs | The game's chat style. |
| Chat message | `ctx.Chat.Broadcast(text) / SendTo(user, text, prefix: false)` | Use it when the server tells players something in chat; it reads 'Mod Name: text'. | Feedback tab, a note (server side) | Progenitor.Sample SampleChat.cs | The game's chat style. |

### HUD and world

Gallery tab: **HUD**.

| Control | API | When to use it | Gallery | Existing sample | Text style |
|---|---|---|---|---|---|
| Locate | `ctx.Hud.Locate(entity or position, label, seconds)` | Use it when the player must find something in the world. | HUD tab, Locate last pick | Controls sample, Widgets page, HUD tab | Label Body, centered on the marker. |
| Pick in world | `ctx.Hud.PickInWorld(onPicked, onCancelled)` | Use it to let the player choose a chest or station by interacting with it. | HUD tab, Pick in world | Controls sample, Widgets page, HUD tab | The game's own interact prompt. |
| Pick several | `ctx.Hud.PickInWorld(..., new UiWorldPickOptions { Repeat = true })` | Use it to let the player add several chests or stations in a row. | HUD tab, Pick several | Controls sample, Widgets page, Layout tab | As pick in world. |

### Game-panel attachments

Gallery tab: **Game panels**.

| Control | API | When to use it | Gallery | Existing sample | Text style |
|---|---|---|---|---|---|
| Container window | `Ui.AttachToVanilla(VanillaUiSite.ContainerPanel, anchor, ctx.Owner, ctx.Guid)` | Use it to put a button or a line on a chest's window. | Game panels tab, a note | Progenitor.PanelsExample (Deposit, Output, Locate) | The game's own button; caption centered. |
| Station window | `Ui.AttachToVanilla(VanillaUiSite.StationPanel, anchor, ...)` | Use it to put a button on a workstation or refinement window. | Game panels tab, a note | Progenitor.PanelsExample (Inputs, Outputs) | The game's own button. |
| Inventory bar | `Ui.AttachToVanilla(VanillaUiSite.InventoryPanel, VanillaPanelAnchor.BesideSort, ...)` | Use it for one short action beside the inventory's sort button. | Game panels tab, a note | Progenitor.PanelsExample (Stash) | An icon with its caption as tooltip (Body), centered on the button. |
| Castle heart window | `Ui.AttachToVanilla(VanillaUiSite.CastleHeartPanel, anchor, ...)` | Use it to put a button on the castle heart's window. | Game panels tab, a note | Progenitor.PanelsExample (Castles, Fuel) | The game's own button. |
| Item tooltip line | `Ui.AttachToVanilla(VanillaUiSite.InventoryTooltip, VanillaPanelAnchor.TooltipBody, ...)` | Use it to add a line of the mod's own to an item's tooltip. | Game panels tab, a note | Progenitor.PanelsExample (tooltip line) | Body, left-aligned, in the tooltip. |
| Map rows | `Ui.AttachToVanilla(VanillaUiSite.MapWarEventInfo, ...)` | Use it to add rows beside the map's war event info. | Game panels tab, a note | Progenitor.Sample SampleRifts.cs | Body, left-aligned. |

## Verification status

Source-checked against the kit on 2026-10-16; the gallery, the catalog and the standard windows are built and game-free tested (`WindowPresetTests`, `ModMessagePrefixTests`). **Not live-verified:** the Gallery page, the three standard windows and `panel.Text` have not been opened in the game. The older samples each row names keep their own verification labels in [ui.md](ui.md) and [api.md](api.md).

The hub controls (game window tabs, stable tab height, tab history, centered buttons, compact dropdowns, a field with a button, key hints, a table filling its window, building icons; [ui.md](ui.md#hub-controls-hub1)) are source-checked and game-free tested (`UiHubControlsTests`, `KeybindTabTests`) on 2026-10-16. **Not live-verified:** none has been drawn in the game, the hub window included.
