# UI style guide

How to give a mod's UI the game's look and the framework's behavior. Use this page when you decide where a mod's UI goes, which control fits a value, and how to word, order and color it. The API reference is [ui.md](ui.md) and the `IUiPanel` table in [api.md](api.md); the settings page builder is [mod-settings.md](mod-settings.md).

The reference is **Progenitor.ControlsSample** (`samples/Progenitor.ControlsSample`). Its **Widgets** page ([WidgetsDemo.cs](../samples/Progenitor.ControlsSample/WidgetsDemo.cs)) shows the composed widgets and the HUD calls on five tabs: Lists, Input, Table, Layout and HUD ([ui.md](ui.md#widgets-p7-p7b)). Its **All controls** page (Ctrl+F7 in a world, or the Mods menu: the mod's entry, then **All controls**) shows every control on the settings grid, and every control there is one line in [ControlsDemo.cs](../samples/Progenitor.ControlsSample/ControlsDemo.cs): find it by the caption the table in section 3 names. Its settings page is every `ctx.Settings.Page` row ([Plugin.cs](../samples/Progenitor.ControlsSample/Plugin.cs), `ControlsSettings`). **Progenitor.Sample**'s F9 window ([SampleUi.cs](../samples/Progenitor.Sample/SampleUi.cs), `OpenDemo`) is the reference for a floating window. Copy from those rather than from older panels; this page adds no code of its own.

**The standard: first class.** Every piece of Progenitor UI, and every UI a mod builds with it, must look like part of the game: the game's own art, fonts, colors and spacing (through the kit's controls and `UiLook`), vanilla hover, press and selected states, and aligned rows. Nothing may be cut off or overlap at 1920x1080, 2560x1440 or 3840x2160. A control that looks like debug UI or bolted on is not finished: mock it up first, compare it with the game's own panel it sits next to, and check it in the game before calling it done.

## 1. Pick where the UI lives

| The mod needs | Use | Why |
|---|---|---|
| Options the player sets once and forgets | `ctx.Settings.Page(...)` ([mod-settings.md](mod-settings.md)) | Search, reset, server scopes, sync and the description column come with it. No layout code. |
| A screen of its own: tools, lists, actions | `ctx.Menu.Register(name, description).Page(title, panel => ...)` | A page in the Mods menu, drawn on the settings grid in the game's look. `ctx.Menu.OpenPage(title)` opens it from a key or a button, in a world or at the title screen. |
| A window of its own: a small tool, one thing the player works on, or a large tabbed screen | `ctx.Windows.Create(UiWindowPreset.Basic \| Header \| Tabbed, title, options)` ([Standard windows](ui.md#standard-windows)) | The frame, title, version stamp, input mode, controller and Escape come from the preset, the same in every mod. Start here; the catalog of every control is [ui-controls.md](ui-controls.md). |
| A floating window laid out by hand, when no preset fits | `Ui.Panel(title, ctx.Owner, ctx.Guid)`, then `panel.GameWindow()`, then the controls, then `panel.Show()` | The game's popup frame and the same grid as a Mods page; placed and tiled with other panels, not modal. Inside `namespace` code that also uses `Progenitor.Ui`, write the factory as an alias, `using UiApi = Progenitor.Ui.Ui;`, as both samples do. |
| A question or a confirmation | `ctx.Dialogs` (`Confirm`, `Notice`, `Choose`, `Prompt`, `Form`) or `panel.ConfirmButton` | The game's popup, modal, controller-ready, starts on the safe choice. |
| HUD elements next to the game's own | Cloned vanilla widgets ([ui.md](ui.md#native-control-styling-and-lifecycle)) | Only when the element must sit inside the game's HUD. |
| A button or line on a chest, station, inventory, castle heart or item tooltip | `Ui.AttachToVanilla(site, anchor, ...)` ([ui.md](ui.md#vanilla-panel-sites-p6)) | The game's own button and text, placed by the framework's placement rules. Prefer an inline anchor for one short action (`BesideSort` is one button beside the inventory bar, shown as an icon with its caption as tooltip when the caption is longer than about 40 units; give it a `VanillaButtonIcon`; `BesideRename` is a title-row button after the rename button); use an edge column for more or longer captions. Captions are one or two words, never colored. |

Prefer the first row that fits. A settings value belongs on the settings page even when the mod also has a page of its own.

## 2. Lay out a page or a window

On a Mods page and a `GameWindow` the framework places every control: the name in the name column, the control in a fixed control column at the same x on every row, compact controls (toggle, number, color) at that column's right edge. A row whose control is a checkbox or a key cap lets its name run up to one gap before that control (or before a shown chip such as Restart), since the column is empty to its left; every other row's name stops at the control column and is cut with an ellipsis past it (TOGGLEFIT1, live 2026.10.136: a toggle's "Show the greeting on join" drew as "Show the greeti…"; source-checked and game-free tested, not live-verified). Do not position controls yourself and do not pad captions with spaces.

1. Start each topic with `Heading`. Keep a page to a few headings; a long page is better split into two pages.
2. Put one short `Note` under a heading when the topic needs explaining. Put an explanation of one option into that option (a dropdown or radio description), not into a note.
3. Add the controls in the order the player uses them. A control that shows, hides or enables another goes directly above it.
4. Put read-only values after the controls they describe: `Fact` for a value that does not change while the page is open, `Info` for a live readout (it refreshes itself).
5. Use `Divider` only to separate an action block from what precedes it; headings already separate topics.
6. Buttons are the game's compact buttons at the row's left edge, sized to their caption. Put related actions side by side between `BeginInline()` and `EndInline()`. Put the main action first.
7. A destructive or irreversible action uses `ConfirmButton(caption, question)` (or `ctx.Dialogs.Confirm` with `danger: true`), never a plain button.
8. Use the panel `Footer` for a version or a short running count, not for instructions.

## 3. Pick the control for the value

| Value | Control | In the controls sample | Notes |
|---|---|---|---|
| On or off | `Toggle` | Toggle | The game's checkbox. Name the thing that is on, not the action ("Show hints", not "Click to show hints"). |
| A number where the rough amount matters | `Slider` | Slider | No typed value. |
| A number where the exact value matters | `SliderField` or `Number` | Slider field, Number | `SliderField` for a range to drag through, `Number` for a small count with steps. `Number` takes `units`; for a `SliderField`, put the unit in its name. |
| One of 2 to 4 short options the player should see together | `Radio` | Radio, Radio (described) | Add descriptions when the options are not self-explanatory. |
| One of many options, or long option names | `Dropdown` | Dropdown, Dropdown (described) | Descriptions show beside the open list (or in the settings page's description column). |
| Several of a few options | `MultiSelect` | Multi-select | |
| One line of free text | `Input` | Text input | |
| Several lines of text | `TextArea` | Text area | |
| A color | `ColorField` | Color, Color with alpha | `alpha: true` only when transparency is meaningful. |
| A date and time | `DateTimeField` | Date and time | Stored as ISO 8601 UTC; shown in the zone you pass. |
| A key | `KeyField`, for a bind the mod registered with `ctx.Keybinds` | Page key | Never read keys yourself (see section 6). |
| A value the player cannot change | `Fact` (fixed) or `Info` (live) | Plugin, Events so far | Not a disabled input. |
| An action | `Button`; side by side in `BeginInline`/`EndInline` | Plain button, Inline A / Inline B | |
| An action that cannot be undone | `ConfirmButton` | Confirm button | |
| A question in a popup | `ctx.Dialogs` | Dialogs (ctx.Dialogs) | `Confirm`, `Notice`, `Choose`, `Prompt`; `Form` for a name with a few check boxes (the game's checkbox), never a list of buttons standing in for checks. |
| An order the player arranges (a priority list) | `OrderedList` | Widgets page, Lists tab | Drag by the grip, arrows, a drawer per row, remove. The list changes itself, then tells you. |
| One or several of many named things (items, chests, players) | `Picker` | Widgets page, Input tab | Search with autofill and sections. Gray what cannot be picked and say why in `DisabledReason`; never hide it silently. With `Multi`, the status line offers **Select all** and **Clear**. |
| Several of a long list (hundreds of chests, stations or belts) to act on together | `Checklist` | Gallery, Lists tab, Checklist window | The game's checkbox on every row, the count always shown, a filter beside the search, the mod's columns. **Select shown** acts only on what the filter and the search show. Use it instead of a multi `Picker` past a few dozen entries. |
| A script or rules text | `CodeArea` | Widgets page, Input tab | You supply the colors (a highlighter) and the line errors. |
| Groups of controls that do not fit one page | `Tabs` | Widgets page, the tab row | Two to five short captions; a count badge for a number the player should notice. |
| Extra words about a control | `Tooltip` | Widgets page, Reset list | Only for detail; the control's name must stand alone. |
| A short event message during play | `ctx.Hud.Notify` | Widgets page, HUD tab | The game's own announcement. Not for errors the player must act on (use a dialog). |
| Where something is in the world | `ctx.Hud.Locate` | Widgets page, HUD tab | A marker and an edge arrow for a few seconds. |
| Which chest or station | `ctx.Hud.PickInWorld` | Widgets page, HUD tab | The player interacts with it; its panel stays closed; Escape cancels. `Repeat` keeps picking until Escape (adding several; Layout tab, **Pick several (repeat)**). |
| Many records the player sorts, filters, selects and edits | `DataTable` | Widgets page, Table tab | Columns the player hides, Shift for a second sort key, numbers edited in place that wait for the server's answer. Do not use it for a handful of settings. |
| A list of named sets, each edited on its own (belts, categories, profiles) | `MasterDetail` | Widgets page, Layout tab | The list on the left with search; the selected entry on the right. Fill the right side on `OnSelected`. |
| A few short values the player adds and removes (members, tags, filters) | `Chips` | Widgets page, Lists tab | Item chips show the game's icon; `Toggle` for combinable filters; `+N more` past `MaxLines`. Not for a long list (use `OrderedList` or `Picker`). |
| A word of the mod's own the player may not know | `handle.Term(term)` with `UiTerms.Define` | Widgets page, HUD tab | One or two sentences, defined once. The circle-i goes away after three hovers; offer `TermMarkersToggle` to bring it back. Do not also put a `Tooltip` on the control. |
| A one-time explanation of a screen | `ctx.Hud.Tip` | Widgets page, HUD tab (**Show tip**) | One card per feature, the first time the player opens it; anchor it to the panel it explains. Not for errors (use a dialog). |

## 4. Word it

- US English, sentence case: "Show the note below", not "Show The Note Below" or all capitals.
- A control's name says what it controls, in two to four words. Put the explanation in the help or description.
- Button captions are verbs or verb phrases: "Collect logs", "Reset layout". A button that opens something says what opens ("Open settings").
- Messages say what happened and what to do next, in one sentence. Do not blame the player and do not show exception text.

## 5. Color and art

Use the framework's tones and the game's art; do not set colors, fonts or sprites on kit controls. Words take a named text style, `panel.Text(text, UiTextStyle.Body | Title | Section | Muted | Number | Warning | Error | Success)`, which fixes the font, size, color and alignment (text left, numbers right, button captions and titles centered): see [Text styles](ui-controls.md#text-styles).

| Tone (`LabelTone`) | Meaning |
|---|---|
| `Normal` | Ordinary text. |
| `Muted` | Secondary text, a hint, a value that does not apply now. |
| `Ok` | Done, running, enabled. |
| `Pending` | Waiting, in progress, needs a restart. |
| `Orange` | Failed or refused but recoverable. |
| `Warn` | Something the player should look at. |
| `Danger` | A destructive action or a blocking problem. |

A status line is `panel.Label(text, tone)`; change it in place with `SetText(text, tone)`. Never rely on color alone: the words must say the same thing.

On a Mods page and a `GameWindow`, buttons and controls already use the game's art; `.GameStyle()` there is harmless but unnecessary (the controls sample still calls it on a few buttons). On a plain floating panel (no `GameWindow`), call `.GameStyle()` on a button to draw the game's standard button.

## 6. Input, keys and controllers

- Every key a mod acts on is registered with `ctx.Keybinds.Register(...)`, so the player can see and remap it in the Keybinds tab. Never poll `UnityEngine.Input`, `Keyboard.current`, `Mouse.current` or a gamepad directly: a key read that way cannot be remapped, is invisible to the conflict check, and fires while the player types in a field.
- A key that opens or closes the mod's UI is registered with `ctx.Keybinds.RegisterOpener(...)`: it works at the title screen as well as in a world, and closes the window it opened. Use it only for that: a plain `Register` key does not fire at the title screen, which keeps keys that act in the world from firing there. A letter, digit or other printable key without Ctrl or Alt cannot be an opener. The controls sample's Ctrl+F7 and the Sample's F9, Ctrl+F9 and Shift+F9 are openers.
- The kit's controls are reachable with a controller and the keyboard without extra code ([ui.md](ui.md#controller-and-keyboard-navigation-nav1)). Keep it that way: use kit controls, and do not build clickable parts from raw images.
- On a floating window, set `panel.CaptureMode` (`UiCaptureMode`): `WhenHovered` (the default) lets the player keep playing while the pointer is elsewhere; `WhileShown` for a window the player works in; `Never` for a display that takes no input, which also means Escape reaches the game (it opens the game's menu as well as closing the window). A Mods page always captures while it is shown; you do not set it there. A standard window sets it for you: Header and Tabbed take the input while shown, and a Basic window opened while the player navigates with a controller or the keys is shown `WhileShown` so the controller moves into it ([Standard windows](ui.md#standard-windows)).
- Close what you open: a window has a Close button or a key that toggles it. Escape (B on a controller) already closes an open list, a dialog, the topmost standard window or input-taking floating panel (one per press) or, with none of those open, the shown panels; do not handle it yourself.

## 7. Before you ship

1. Open the page or window in the game at 1920x1080, 2560x1440 and 3840x2160; nothing is cut off or overlaps, and it reads as part of the game, not as debug UI.
2. Walk it with a controller or the keyboard only: every control is reached, and Escape or B leaves.
3. Hover every option of a dropdown or radio with descriptions; each reads well on its own.
4. Unload the mod (or hot-reload it): its windows close and nothing it drew stays on screen.
5. Compare it side by side with the controls sample's page; the rows line up the same way.

## Verification status

Source-checked against `IUiPanel`, `UiLook`, `ctx.Menu`, `ctx.Dialogs` and `LabelTone` on 2026-10-11. Verified live on 2026-10-11 on the Steam client: on the controls sample's page, compact page buttons and an inline pair, drawn radio circles, facts and date-time rows on the grid, the plain slider, and dropdown hover with its description box; on the Sample's F9 window, the `GameWindow` frame, headings, compact buttons, facts and pickers on the grid, and on 2026-10-12 wheel scrolling to its bottom rows. Not verified live: controller walk-through of these pages in this pass, and an inline field with a button on a Mods page. The checklist in section 7 is a procedure for mod authors, not a recorded run.

The standard window presets (`ctx.Windows.Create`, section 1), the named text styles (`panel.Text(..., UiTextStyle)`, section 5) and the standard windows' input rules (section 6) are source-checked and game-free tested (`WindowPresetTests`), **not live-verified**.
