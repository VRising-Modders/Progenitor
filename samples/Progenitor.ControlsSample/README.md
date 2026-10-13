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

Not demonstrated: `SettingsPageBuilder.InScope` (marks a button or info line server-wide; meaningless
for a client-only mod). Lower-level surfaces `Ui.AttachToVanilla`, `Ui.WatchPointer` and `WorldMap` are
not controls and are left to `Progenitor.Sample`.

`tests/Progenitor.Tests/ControlsSampleTests.cs` fails when a new `IUiPanel` member, settings-row
kind, `ctx.Dialogs` method or `UiLook` helper has no demo here, and when a controller walking the page
does not reach every interactive control.

Verification: built and game-free tested; not live-verified. Remove it from `BepInEx/progenitor-reload`
after a test batch (CLAUDE.md sample cleanup).
