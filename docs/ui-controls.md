# UI controls catalog

Every control, widget, window, text style and message call the Progenitor UI kit offers, grouped by category: its API, when to use it, where the samples gallery shows it, the older sample that also uses it, and the text style it draws in. Pick from this page first; the [UI style guide](ui-style.md) says where a mod's UI goes and how to word it, and [ui.md](ui.md) holds each control's contract.

The **gallery** is the controls sample's **Gallery** page (`samples/Progenitor.ControlsSample`, [GalleryDemo.cs](../samples/Progenitor.ControlsSample/GalleryDemo.cs)): open the Mods menu at the sample's entry (Ctrl+F7, in a world or at the title screen, opens its **All controls** page) and pick **Gallery**. It has one tab per category below, and on each tab every entry of that category with a live example (or the button that opens where it lives) and its "Use this when" line. Its three standard windows are [GalleryWindows.cs](../samples/Progenitor.ControlsSample/GalleryWindows.cs). The table below and the gallery are one list ([GalleryCatalog.cs](../samples/Progenitor.ControlsSample/GalleryCatalog.cs)); `WindowPresetTests` fails when the gallery does not build an entry or when an entry has no API, use, location, sample or style.

## Text styles

Words on a page or a window take a named style, `panel.Text(text, UiTextStyle.X)` (or a control that fixes its own). The style fixes the game font, the size, the color and the alignment, so a mod never sets them. Raw colors, sizes and alignment are the exception: a color the player picks (`ColorField`) and a code area's highlighter (`CodeArea`) are the only places the kit takes a color from a mod.

| Style | Font | Size | Color | Alignment | Use when |
|---|---|---|---|---|---|
| `Title` | The game's title font (drop-shadow variant) | 24 | Caption cream `#ECE4D8`, with the game's rule under it | Centered | The name of a block inside a window. A window's own title is drawn by the window, centered in its frame. |
| `Section` | The game's title font | 24 | Caption cream, with the section rule | Left | The label above a group of rows; a page has a few. Same as `panel.Heading`. |
| `Body` | The game's body font (Nocturne Serif Regular) | 18 | Caption cream `#ECE4D8` | Left | Ordinary sentences and values. |
| `Muted` | Body font | 18 | Muted `#A3959A` | Left | A hint, a secondary detail, a value that does not apply now. Same color as `panel.Note`. |
| `Number` | Body font, tabular figures | 18 | Caption cream | Right | A count, an amount, a time or a version, so figures line up down a column. `panel.Text(name, value, UiTextStyle.Number)` puts the name left and the figure right. |
| `Warning` | Body font | 18 | Amber `#C07A3A` | Left | Something the player should look at, not yet a failure. Also how a pending state reads. |
| `Error` | Body font | 18 | Red `#C8323C` | Left | A failure or a blocking problem; say what to do next. |
| `Success` | Body font | 18 | Green `#6FD08A` | Left | Done, running, enabled. |

Alignment rules the kit applies without being asked: text left; numbers right; captions centered on buttons, tabs and chips; titles centered in the frame (and in a dialog). Every standard window sets its rows left (`ContentAlignment = "left"`). A status that changes in place is a tone label, `panel.Label(text, LabelTone)`: `Normal` is Body, `Muted` is Muted, `Ok` is Success, `Pending`, `Orange` and `Warn` read as Warning, `Danger` is Error.

Status: the style table is game-free tested (`WindowPresetTests`); the drawn fonts and colors are the kit's existing label, note, heading and dialog-title rows, verified live in their own rows of [ui.md](ui.md); `panel.Text` itself is **not live-verified**.

## The catalog

Each row's **Text style** column is that control's checklist line: check it in the game before calling a page done.

### Layout and windows

Gallery tab: **Windows**.

| Control | API | Use this when | Gallery | Existing sample | Text style |
|---|---|---|---|---|---|
| Basic window | `ctx.Windows.Create(UiWindowPreset.Basic, title)` | A small tool used during play: a dropdown and a few buttons. | Windows tab, Basic window | none (new) | Title centered in the frame; rows Body, left-aligned; stamp Muted, centered. |
| Header window | `ctx.Windows.Create(UiWindowPreset.Header, title, options)` | One thing the player works on (a container, a named set), with a toolbar and tabs. | Windows tab, Header window | none (new) | Title centered between the ornaments; toolbar captions centered on their buttons; rows Body, left-aligned. |
| Tabbed window | `ctx.Windows.Create(UiWindowPreset.Tabbed, title, options)` | A large screen like the game's own menus: tabs, an info column and the main content. | Windows tab, Tabbed window | none (new) | Title centered; tab captions centered; info column Section and Body, left-aligned; numbers right-aligned. |
| Floating panel | `Ui.Panel(title, ctx.Owner, ctx.Guid).GameWindow()` | None of the presets fits and the window is laid out by hand. | Windows tab, Floating panel | ControlsSample settings page, Floating window (ControlsWindow) | Title centered; rows follow ContentAlignment (set left). |
| Mods page | `ctx.Menu.Register(name, description).Page(title, build)` | The mod needs a screen of its own in the Mods menu. | Windows tab, Open All controls | Controls sample, All controls page | Rows on the settings grid: names Body left, controls in a fixed column. |
| Settings page | `ctx.Settings.Page(page => ...)` | Options the player sets once and forgets. | Windows tab, Settings page | Controls sample, settings page | Names Body left; help in the description column; section labels Section. |
| Text styles | `panel.Text(text, UiTextStyle.Body \| Title \| Section \| Muted \| Number \| Warning \| Error \| Success)` | Any words on a page or window: pick the style, never a color or a size. | Windows tab, Text styles | none (new) | Every named style (Title, Section, Body, Muted, Number, Warning, Error, Success) fixes font, size, color and alignment. |
| Name and value | `panel.Text(name, value, UiTextStyle.Number)` | A figure the player compares down a column (counts, amounts, times). | Windows tab, Text styles | none (new) | Name Body left; value Number, right-aligned. |
| Heading | `panel.Heading(text)` | A topic starts; a page has a few. | every tab, the headings | Controls sample, All controls page | Section, left-aligned, with the section rule. |
| Note | `panel.Note(text)` | A topic needs one short explanation under its heading. | every entry's line under its control | Controls sample, All controls page | Muted, left-aligned. |
| Status line | `panel.Label(text, LabelTone)` | A status that changes in place (SetText with a tone). | Windows tab, Status line | Controls sample, All controls page (status line) | Body, Muted, Success, Warning or Error by tone; left-aligned. |
| Fact | `panel.Fact(name, value)` | A value that does not change while the page is open. | Windows tab, Fact | Controls sample, All controls page (Plugin) | Name Body left; value Body in the control column. |
| Divider | `panel.Divider()` | An action block needs separating from what precedes it. | Windows tab, Divider | Controls sample, All controls page | No text. |
| Inline row | `panel.BeginInline() ... panel.EndInline()` | Related buttons (or a field and its button) belong side by side. | Windows tab, Inline row | Controls sample, All controls page (Inline A / Inline B) | Captions centered on their buttons. |
| Tabs | `panel.Tabs(key, captions)` | Groups of controls do not fit one page (two to five short captions). | the gallery's own tab row | Controls sample, Widgets page (tab row) | Captions centered on the tabs; badges Number. |
| Version stamp | `panel.Footer / IUiWindow.VersionStamp` | A window shows which mod and version drew it (on by default in the presets). | every window's bottom line | Controls sample, All controls page (footer) | Muted, small, centered. |
| Scrolling | `automatic: a panel scrolls when its rows do not fit` | A page has more rows than the screen; nothing to call. | every long gallery tab | Controls sample, All controls page | Rows keep their styles (Body, Muted, Number) while scrolled. |
| Input mode | `panel.CaptureMode = UiCaptureMode.WhenHovered \| WhileShown \| Never` | A floating window decides whether the game keeps the input (the presets set it). | Windows tab, Basic and Header windows | ControlsSample ControlsWindow (WhileShown) | No text. |
| Game button look | `button.GameStyle()` | A button on a plain floating panel must look like the game's (kit pages already do). | Windows tab, Game button | Controls sample, All controls page | Caption centered on the button. |

### Input

Gallery tab: **Input**.

| Control | API | Use this when | Gallery | Existing sample | Text style |
|---|---|---|---|---|---|
| Button | `panel.Button(caption).OnClick(...)` | An action. | Input tab, Button | Controls sample, All controls page (Plain button) | Caption centered on the button. |
| Confirm button | `panel.ConfirmButton(caption, question)` | An action that cannot be undone. | Input tab, Confirm button | Controls sample, All controls page | Caption centered; the dialog's title Title, body Muted. |
| Toggle | `panel.Toggle(name, initial)` | On or off. | Input tab, Toggle | Controls sample, All controls page | Name Body left; the game's checkbox at the column's right. |
| Slider | `panel.Slider(name, min, max, value)` | A number where the rough amount matters. | Input tab, Slider | Controls sample, All controls page | Name Body left. |
| Slider field | `panel.SliderField(name, min, max, step, value)` | A number where the exact value matters, in a range to drag through. | Input tab, Slider field | Controls sample, All controls page | Name Body left; value Number, right-aligned in its box. |
| Number | `panel.Number(name, value, min, max, step, decimals, units)` | A small exact count with steps. | Input tab, Number | Controls sample, All controls page | Name Body left; value Number, right-aligned. |
| Dropdown | `panel.Dropdown(name, values, value, labels, descriptions)` | One of many options, or long option names. | Input tab, Dropdown | Controls sample, All controls page | Name Body left; value Body; descriptions Muted. |
| Radio | `panel.Radio(name, values, value, labels, descriptions)` | One of two to four short options the player should see together. | Input tab, Radio | Controls sample, All controls page | Options Body left; the chosen one bright, the others dimmed. |
| Multi-select | `panel.MultiSelect(name, values, selected)` | Several of a few options. | Input tab, Multi-select | Controls sample, All controls page | Options Body left. |
| Text input | `panel.Input(name, initial)` | One line of free text. | Input tab, Text input | Controls sample, All controls page | Name Body left; typed text Body, left-aligned. |
| Text area | `panel.TextArea(name, initial, lines)` | Several lines of text. | Input tab, Text area | Controls sample, All controls page | Typed text Body, left-aligned. |
| Color | `panel.ColorField(name, hex, alpha)` | A color the player picks. | Input tab, Color | Controls sample, All controls page | Name Body left; hex Number. |
| Date and time | `panel.DateTimeField(name, isoUtc, zone)` | A date and time (stored as ISO 8601 UTC). | Input tab, Date and time | Controls sample, All controls page | Name Body left; value Body. |
| Key field | `panel.KeyField(name, ctx.Guid, bindId)` | The player rebinds a key the mod registered with ctx.Keybinds. | Input tab, Key field | Controls sample, All controls page (Page key) | Name Body left; key cap centered. |
| Keybind | `ctx.Keybinds.Register / ctx.Keybinds.RegisterOpener` | The mod acts on a key (never read keys yourself). | Input tab, the gallery's own Ctrl+F7 | ControlsSample Plugin.cs (Ctrl+F7) | Shown in the Keybinds tab: name Body left, key centered. |
| Picker | `panel.Picker(key, entries)` | One of many named things, with search. | Input tab, Picker | Controls sample, Widgets page, Input tab | Entries Body left; notes Muted; disabled rows say why. |
| Multi picker | `panel.Picker(key, entries, new UiPickerOptions { Multi = true })` | Several of many named things. | Input tab, Multi picker | Controls sample, Widgets page, Input tab | As the picker; the status line Muted. |
| Code area | `panel.CodeArea(name, initial, lines)` | A script or rules text, colored by the mod's highlighter. | Input tab, Code area | Controls sample, Widgets page, Input tab | Body in the game's font; line numbers right-aligned; errors Error. |
| Settings row (any) | `page.Setting(entry)` | A config entry whose control the kit picks from its type. | Windows tab, Settings page | Controls sample, settings page | Name Body left; help Muted. |
| Settings toggle | `page.Toggle(entry)` | A bool option. | Windows tab, Settings page | Controls sample, settings page | Name Body left. |
| Settings number | `page.Number(entry, min, max, step, units)` | An exact numeric option. | Windows tab, Settings page | Controls sample, settings page | Value Number, right-aligned. |
| Settings slider | `page.Slider(entry, min, max, step)` | A ranged numeric option. | Windows tab, Settings page | Controls sample, settings page | Value Number. |
| Settings dropdown | `page.Dropdown(entry).OptionDescriptions(...)` | A choice among many. | Windows tab, Settings page | Controls sample, settings page | Descriptions Muted in the description column. |
| Settings radio | `page.Radio(entry)` | A choice among a few, all visible. | Windows tab, Settings page | Controls sample, settings page | Options Body left. |
| Settings multi-select | `page.MultiSelect(entry, choices)` | Several of a few options. | Windows tab, Settings page | Controls sample, settings page | Options Body left. |
| Settings text | `page.Text(entry).MaxLength(n) / page.Text(entry, multiline: true)` | A free-text option. | Windows tab, Settings page | Controls sample, settings page | Typed text Body, left-aligned. |
| Settings color | `page.Color(entry)` | A color option. | Windows tab, Settings page | Controls sample, settings page | Hex Number. |
| Settings date and time | `page.DateTime(entry)` | A date-time option. | Windows tab, Settings page | Controls sample, settings page | Value Body. |
| Settings keybind | `page.Keybind(bindId)` | A key the mod registered, shown with its options. | Windows tab, Settings page | Controls sample, settings page | Key centered. |
| Settings section and divider | `page.Section(name) / page.Divider()` | The options fall into topics. | Windows tab, Settings page | Controls sample, settings page | Section, left-aligned. |
| Settings info and button | `page.Info(name, value) / page.Button(caption, action, confirm:)` | A live value or an action beside the options. | Windows tab, Settings page | Controls sample, settings page | Info value Body; button caption centered. |
| Settings scope | `page.InScope(SettingScope) / page.HideWhenAlone()` | Options apply per server or should hide when alone. | Windows tab, Settings page | Controls sample, settings page | No text of its own. |

### Lists and data

Gallery tab: **Lists**.

| Control | API | Use this when | Gallery | Existing sample | Text style |
|---|---|---|---|---|---|
| Ordered list | `panel.OrderedList(key, items)` | An order the player arranges (a priority list). | Lists tab, Ordered list | Controls sample, Widgets page, Lists tab | Items Body left; notes Muted; badges Number. |
| Data table | `panel.DataTable(key, columns, options)` | Many records the player sorts, filters, selects and edits. | Lists tab, Data table | Controls sample, Widgets page, Table tab | Text columns left; number columns Number, right-aligned; headers centered over their columns' alignment. |
| Master-detail | `panel.MasterDetail(key, options)` | A list of named sets, each edited on its own. | Lists tab, Master-detail | Controls sample, Widgets page, Layout tab | List Body left; detail headings Section; text Muted. |
| Chips | `panel.Chips(key, chips)` | A few short values the player adds and removes. | Lists tab, Chips | Controls sample, Widgets page, Lists tab | Captions centered on the chips. |
| Live readout | `panel.Info(name, () => value, refreshSeconds)` | A value that changes while the page is open. | Lists tab, Live readout | Controls sample, All controls page (Events so far) | Name Body left; value Body. |
| Item icons | `UiPickerEntry.ItemPrefab / UiChip / UiMasterEntry.ItemPrefab` | A row names a game item: show the game's own icon. | Lists tab, Picker and Chips rows | Controls sample, Widgets page, Input and Lists tabs | Icon left of a Body caption. |
| Pending state | `LabelTone.Pending / a data table edit waiting for the server` | Something is in progress; say so in words, never a bare spinner. | Lists tab, Status line | Controls sample, Widgets page, Table tab | Warning tone, left-aligned, with the words saying what waits. |

### Feedback and messages

Gallery tab: **Feedback**.

| Control | API | Use this when | Gallery | Existing sample | Text style |
|---|---|---|---|---|---|
| Notification | `ctx.Hud.Notify(text, detail, UiNoticeKind.Info \| Success \| Warning \| Error)` | A short event message during play; shown as 'Mod Name: text'. | Feedback tab, the four kinds | Controls sample, Widgets page, HUD tab | The game's banner font, centered; the band's rules tinted by kind. |
| Notification without prefix | `ctx.Hud.Notify(text, prefix: false)` | The text already names the mod another way. | Feedback tab, No prefix | none (new) | As the notification. |
| Confirm dialog | `ctx.Dialogs.Confirm(title, body, yes, onYes)` | A yes/no question. | Feedback tab, Confirm | Controls sample, All controls page (Dialogs) | Title, Muted body, centered; buttons centered captions. |
| Notice dialog | `ctx.Dialogs.Notice(title, body)` | Something the player must read and acknowledge. | Feedback tab, Notice | Controls sample, All controls page (Dialogs) | Title, Muted body, centered. |
| Choose dialog | `ctx.Dialogs.Choose(title, body, options, onPick)` | A pick from a few options in a popup. | Feedback tab, Choose | Controls sample, All controls page (Dialogs) | Options' captions centered. |
| Prompt dialog | `ctx.Dialogs.Prompt(title, body, initial, onSubmit)` | A line of text in a popup (a name). | Feedback tab, Prompt | Controls sample, All controls page (Dialogs) | Typed text Body, left-aligned; error Error. |
| Tooltip | `handle.Tooltip(text)` | Extra words about a control (its name must stand alone). | every gallery control's hover | Controls sample, Widgets page (Reset list) | Body in the game's tooltip box, left-aligned. |
| Term | `UiTerms.Define(pluginId, term, explanation) and handle.Term(term)` | A word of the mod's own the player may not know. | Feedback tab, Term | Controls sample, Widgets page, HUD tab | Body; the circle-i after the caption. |
| Term markers toggle | `panel.TermMarkersToggle()` | The player wants the help markers back. | Feedback tab, Term | Controls sample, Widgets page, HUD tab | Name Body left. |
| Tip card | `ctx.Hud.Tip(id, title, body, options)` | A one-time explanation of a screen. | Feedback tab, Tip card | Controls sample, Widgets page, HUD tab | Title, Body, left-aligned. |
| Command reply | `ICommandContext.Reply(text) / Reply(text, prefix: false)` | A chat command answers the player; shown as 'Mod Name: text'. | Feedback tab, a note (server side) | Progenitor.Sample SampleCommands.cs | The game's chat style. |
| Chat message | `ctx.Chat.Broadcast(text) / SendTo(user, text, prefix: false)` | The server tells players something in chat; shown as 'Mod Name: text'. | Feedback tab, a note (server side) | Progenitor.Sample SampleChat.cs | The game's chat style. |

### HUD and world

Gallery tab: **HUD**.

| Control | API | Use this when | Gallery | Existing sample | Text style |
|---|---|---|---|---|---|
| Locate | `ctx.Hud.Locate(entity or position, label, seconds)` | The player must find something in the world. | HUD tab, Locate last pick | Controls sample, Widgets page, HUD tab | Label Body, centered on the marker. |
| Pick in world | `ctx.Hud.PickInWorld(onPicked, onCancelled)` | The player chooses a chest or station by interacting with it. | HUD tab, Pick in world | Controls sample, Widgets page, HUD tab | The game's own interact prompt. |
| Pick several | `ctx.Hud.PickInWorld(..., new UiWorldPickOptions { Repeat = true })` | The player adds several chests or stations in a row. | HUD tab, Pick several | Controls sample, Widgets page, Layout tab | As pick in world. |

### Game-panel attachments

Gallery tab: **Game panels**.

| Control | API | Use this when | Gallery | Existing sample | Text style |
|---|---|---|---|---|---|
| Container window | `Ui.AttachToVanilla(VanillaUiSite.ContainerPanel, anchor, ctx.Owner, ctx.Guid)` | A button or a line belongs on a chest's window. | Game panels tab, a note | Progenitor.PanelsExample (Deposit, Output, Locate) | The game's own button; caption centered. |
| Station window | `Ui.AttachToVanilla(VanillaUiSite.StationPanel, anchor, ...)` | A button belongs on a workstation or refinement window. | Game panels tab, a note | Progenitor.PanelsExample (Inputs, Outputs) | The game's own button. |
| Inventory bar | `Ui.AttachToVanilla(VanillaUiSite.InventoryPanel, VanillaPanelAnchor.BesideSort, ...)` | One short action beside the inventory's sort. | Game panels tab, a note | Progenitor.PanelsExample (Stash) | An icon with its caption as tooltip (Body), centered on the button. |
| Castle heart window | `Ui.AttachToVanilla(VanillaUiSite.CastleHeartPanel, anchor, ...)` | A button belongs on the castle heart. | Game panels tab, a note | Progenitor.PanelsExample (Castles, Fuel) | The game's own button. |
| Item tooltip line | `Ui.AttachToVanilla(VanillaUiSite.InventoryTooltip, VanillaPanelAnchor.TooltipBody, ...)` | A line of the mod's own on an item's tooltip. | Game panels tab, a note | Progenitor.PanelsExample (tooltip line) | Body, left-aligned, in the tooltip. |
| Map rows | `Ui.AttachToVanilla(VanillaUiSite.MapWarEventInfo, ...)` | Rows beside the map's war event info. | Game panels tab, a note | Progenitor.Sample SampleRifts.cs | Body, left-aligned. |

## Verification status

Source-checked against the kit on 2026-10-16; the gallery, the catalog and the standard windows are built and game-free tested (`WindowPresetTests`, `ModMessagePrefixTests`). **Not live-verified:** the Gallery page, the three standard windows and `panel.Text` have not been opened in the game. The older samples each row names keep their own verification labels in [ui.md](ui.md) and [api.md](api.md).
