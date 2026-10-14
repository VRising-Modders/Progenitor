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

`tests/Progenitor.Tests/ControlsSampleTests.cs` fails when a new `IUiPanel` member, settings-row
kind, `ctx.Dialogs` method or `UiLook` helper has no demo here, and when a controller walking the page
does not reach every interactive control.

Verification: built and game-free tested. The Widgets page: live-verified on the test client 2026-10-14 through the bridge at 1920x1080 and 2560x1440, plus a 2x supersample capture (layout only), its Locate button excepted; not verified: native 4K pixel density, the feel of a real-mouse drag, and controller hardware (navigation was driven through the bridge's virtual pad, not a physical controller). Remove it from `BepInEx/progenitor-reload`
after a test batch (CLAUDE.md sample cleanup).
