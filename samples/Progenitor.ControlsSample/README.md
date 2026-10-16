# Progenitor Controls Sample

A client-only test mod that uses every UI control the Progenitor API offers. Every interactive
control writes a `Controls sample: <control> = <value>` line to the BepInEx log and updates the
status line at the top of the panel (and the `Events: N` footer), so a tester can click through.

Plugin GUID `progenitor.controlssample`; version comes from git (docs/versioning.md).

## How to open it

- **Ctrl+F7** (keybind `OpenControls`, remappable in the Keybinds tab) opens the Mods menu on
  **Progenitor Controls Sample → All controls** with `ctx.Menu.OpenPage`, which opens the menu the way its
  own key does (in a world through the game's menu on its MODS tab) and then shows the page. Pressing it
  again closes the menu. The page is a custom mod page (`ctx.Menu.Register(...).Page`) with every
  `IUiPanel` control plus one button per `ctx.Dialogs` kind, drawn on the settings grid with the game's
  Options-page art (docs/ui.md). The key is registered with `ctx.Keybinds.RegisterOpener`, so it also
  works at the title screen.
- **Settings → Floating window → Open window** shows the same controls in a floating `Ui.Panel` drawn
  with `UiLook.GameWindow()`: the game's popup frame around the same grid and buttons as the page. It
  captures input while shown, so a controller can drive it.
- **Mods workspace → Progenitor Controls Sample → Settings** shows every `ctx.Settings.Page` row kind;
  each change logs `Controls sample: setting <key> = <value>`.

- **Mods workspace → Progenitor Controls Sample → Widgets** shows the composed widgets
  ([WidgetsDemo.cs](WidgetsDemo.cs) and its `WidgetsDemo.*.cs` parts, docs/ui.md "Widgets") under five tabs
  with count badges: **Lists** (an ordered list of four sample targets: drag by the grip, arrows, a drawer,
  remove, **Reset list** with a tooltip; then chips: item chips with game icons, a container's belts with an
  add chip, combinable category filters with a `+N more` chip, **Reset chips**), **Input** (a single and a
  multiple picker with sections, two game item icons, grayed rows and the **Select all** / **Clear** links;
  a code area with a toy highlighter and checker, line 4 starting with an error), **Table** (a 600-row data
  table: sorting, hidden columns, drawers, multi-select, grouping with subtotals, Reserve and Cap edited in
  place with answers that resolve, wait for **Confirm pending**, or are refused), **Layout** (a Belts /
  Categories master-detail screen with search, members, links and **Pick several (repeat)**) and **HUD**
  (`ctx.Hud.Notify` in four kinds, **Pick in world**, **Locate the picked target**; explained words with
  circle-i markers on labels, a button, inner tabs and a list, the markers toggle, and tip cards: **Show tip**,
  **All tips**, **Reset tips**). The glossary terms are placeholders. Each event logs
  `Controls sample: widget <event>`.

- **Mods workspace → Progenitor Controls Sample → Gallery** ([GalleryDemo.cs](GalleryDemo.cs)) is the samples
  gallery: one tab per category (**Windows**, **Input**, **Lists**, **Feedback**, **HUD**, **Game panels**) with every
  control, widget, window and message call the kit offers, each a live example (or the button that opens where it lives)
  and a muted "Use this when" line. The table it is built from is [GalleryCatalog.cs](GalleryCatalog.cs), the same list
  as [docs/ui-controls.md](../../docs/ui-controls.md). Its words all use the named text styles (`panel.Text(…, UiTextStyle.X)`).
  The Windows tab opens the three standard windows ([GalleryWindows.cs](GalleryWindows.cs), `ctx.Windows.Create`):
  **Basic window** ("Other Gear · here": a dropdown, two inline buttons, a status line), **Header window** ("Iron
  Chest": Sort, Locate and Rename in the toolbar, Contents / Rules / Log tabs, right-aligned counts) and **Tabbed window**
  ("Storage network": Refresh, Overview / Belts / Settings tabs, an info column with facts and **Locate all**). Each
  button toggles its window; each event logs `Controls sample: gallery …` or `Controls sample: window …`.

There is no chat command: chat commands run on the server, and this mod is client-only; opening
a client panel from chat would need a server half and a custom event (see `Progenitor.Sample`'s `.ui`).

## The game look and the controller

- `UiLook.GameStyle()` on every button but one (**Plain button** keeps the kit's look for comparison), and
  a tone label (`panel.Label(text, LabelTone.Muted)`, `SetText(…, LabelTone.Ok)`) for the status line.
- Every control can be reached and used with a controller or the arrow keys (docs/ui.md, "Controller and
  keyboard navigation"). With the bridge: `nav pad down` walks the page, `nav state` names the focus.

## What it covers

| Surface | Members used |
|---|---|
| `IUiPanel` (`Ui.Panel`, mod pages) | `Label`, `Heading`, `Note`, `Fact`, `Info`, `Divider`, `Button`, `ConfirmButton` (both overloads), `BeginInline`/`EndInline`, `Toggle`, `Slider`, `SliderField`, `Number`, `Input`, `TextArea`, `Dropdown` (both overloads), `Radio` (both overloads), `MultiSelect`, `ColorField` (with and without alpha), `DateTimeField` (minute and second precision), `KeyField`, `Show`, `Hide`, `ContentAlignment`, `CaptureMode`, `Footer`; handle `Text`, `Visible`, `Interactable` |
| `ctx.Dialogs` | `Confirm` (body and facts), `Notice` (body and facts), `Choose`, `Prompt` (with validation) |
| `ctx.Settings.Page` | `Section`, `Setting`, `Toggle`, `Number`, `Slider`, `Dropdown` + `OptionDescriptions`, `Radio`, `MultiSelect`, `Text` + `MaxLength`, `Text(multiline)`, `Color`, `DateTime`, `Keybind`, `Divider`, `Info`, `Button` (plain and confirmed), `HideWhenAlone` |
| `ctx.Keybinds` / `ctx.Menu` | `Register` (the Ctrl+F7 bind), `Register(...).Page`, `OpenPage` |
| `UiLook` | `GameStyle`, `Label(text, LabelTone)`; `IUiToneLabel.SetText` |
| `UiWidgets` (Widgets page) | `OrderedList`, `Picker` (single and `Multi`), `Tabs` (`Add`, `SetBadge` both overloads, `SetTooltip`, `SetTerm`, `OnChanged`), `CodeArea` (`SetHighlighter`, `SetErrors`, `OnChanged`), `Tooltip`, `DataTable`, `MasterDetail`, `Chips`, `Term`, `TermMarkersToggle` |
| `UiTerms` (Widgets page, HUD tab) | `Define`, `ShowMarkers`; `UiListItem.Term`, `UiTableColumn.Term`, `UiChip.Term`, `UiMasterEntry.Term`, `UiDetailLine.Heading(…, term)` |
| `ctx.Hud` (Widgets page, HUD and Layout tabs) | `Notify` (`Info`, `Success`, `Warning`, `Error`), `PickInWorld` (one-shot and `Repeat`), `Locate(Entity, …)`, `Tip`, `ShowAllTips`, `ResetTips` |

Not demonstrated: `SettingsPageBuilder.InScope` (marks a button or info line server-wide; meaningless
for a client-only mod). Lower-level surfaces `Ui.AttachToVanilla`, `Ui.WatchPointer` and `WorldMap` are
not controls and are left to `Progenitor.Sample`.

## Gallery

Status: built and game-free tested (`WindowPresetTests`: every catalog entry is built on its tab, the windows open with
their contents and stamps, and the gallery sources set no raw color, size or alignment). **Not run in the game.**

Live check procedure (bridge only, no OS input; acceptance procedure, not run). Needs a client that nobody is playing,
held in the bridge queue, at the title screen, with this sample and a Core built from this branch deployed to
`BepInEx/progenitor-reload` (a Core swap; Bootstrap is unchanged).

1. `display set` 1920x1080 windowed (`allowInWorld` only if testing in a world), then `display status`.
2. `keybind_press` `progenitor.controlssample` / `OpenControls` (opens **All controls**), then `mods_ui select` the
   sample's **Gallery** page. Expect `ui_read widgets` to list `gallery-tabs` and `gallery-picker`.
3. For each tab 0 to 5: `mods_ui widget_click` `gallery-tabs:tabs/tab<i>`, then `screenshot` (`name`
   `gallery-<tab>-1080.png`, `panel` the workspace pane) and `mods_ui scroll` to the bottom and a second shot. Expect:
   every entry's control and its muted "Use this when" line; text left, numbers right, button captions centered; nothing
   cut off or overlapping.
4. On the Windows tab, `widget_click` **Basic window**, **Header window** and **Tabbed window** in turn. After each:
   `ui_read panels` (expect the window's title, its `capture`: Basic `WhenHovered`, Header and Tabbed `WhileShown`, and
   its `rect`: Basic and Header 640 wide and tiled beside the workspace, Tabbed 960 wide and centered), `screenshot`
   with `panel` = the window title (`window-basic-1080.png`, …). Expect the version stamp
   `Progenitor Controls Sample <version> · Progenitor <version>` centered at the bottom; Header and Tabbed: the dark
   header band, the title centered over the divider, the side ornaments (or none, if the game's
   `Window_TitleSideOrnament` was not found: `mods_ui status` `settingsArt.parts.windowTitleOrnament` says which), the
   toolbar then the tabs pinned above the content; Tabbed: the info column on the left on a darker ground, the content
   on the right. Click **Rename** on the Header window: the game's prompt shows **Rename** with the title; `dialog_type`
   a name and `dialog_choose yes`: the title changes and the log shows `Controls sample: window header renamed = …`.
5. Escape: `ui_input` `{"action":"key","key":"Escape"}` with the prompt closed: the windows hide (`ui_read panels`).
6. Controller: open the Header window, `nav pad down` a few times, `nav state`: the focus walks the toolbar, the tabs and
   the rows. Open the Basic window alone: `nav state` does not move into it (it leaves the controller to the game).
7. Repeat steps 3 and 4 at 2560x1440 (`-1440.png`), then `display restore`.
8. Feedback tab: **Info**, **Success**, **Warning**, **Error** each show `Progenitor Controls Sample: …` on the banner
   (or the band at the title screen) in its kind's look; **No prefix** shows the text alone.
9. Cleanup: close the windows and the workspace, remove the sample from the client's reload folder, confirm its clean
   unload in the log (`Controls sample: unloading`), and leave the bridge queue.

`tests/Progenitor.Tests/ControlsSampleTests.cs` fails when a new `IUiPanel` member, settings-row
kind, `ctx.Dialogs` method or `UiLook` helper has no demo here, and when a controller walking the page
does not reach every interactive control.

Verification: built and game-free tested. The Widgets page: live-verified on the test client 2026-10-14 through the bridge at 1920x1080 and 2560x1440, plus a 2x supersample capture (layout only), its Locate button excepted; not verified: native 4K pixel density, the feel of a real-mouse drag, and controller hardware (navigation was driven through the bridge's virtual pad, not a physical controller). Remove it from `BepInEx/progenitor-reload`
after a test batch (CLAUDE.md sample cleanup).
