# UI toolkit — clone vanilla HUD onto a mod overlay

For mod authors, start with the [UI style guide](ui-style.md): where a mod's UI goes, which control fits a value, and how to lay out, word and color it, with the controls sample as the reference. Then the [Mods workspace](mods-menu.md): `ctx.Menu` registration, the main-menu and top-navigation entries, and the two-pane host as coded.

## Native control styling and lifecycle

Visual suppression must preserve native input routing: disabling a tab's `raycastTarget` also blocks pointer clicks. Test navigation through the normal input path; directly invoking a click handler does not verify that the player can reach it. Modal dialogs may deliberately block underlying input.

Use `ctx.Menu` and tracked panel handles for mod pages and controls. Core owns native menu integration; consumers should not set native tab force flags or add invented game enum values. The following behavior is source-checked against the current client, not a guarantee for future game builds:

- Native outer-tab selection is painted through `ForceHighlight`; `ForceSelect` is a stronger override and must not be used as a persistent page-selected flag. Release must remove framework overrides and return subsequent updates to the native mapper.
- `FullscreenMenu_OuterTabEntry.Refresh` takes separate selection, requirement and highlight inputs. Its `HighlightImage` is controlled by the last input, not by page selection. `InitInstantHighlight()` requests the highlighted state; it is not a neutral refresh. If `ForceHighlight` already matches the wanted selected bit, a typed `OnPointerExit(null)` / `OnDeselect(null)` on that locked entry precedes the final `Refresh` so idle and selected both repaint.
- Native color transitions use `Graphic.CrossFadeColor`, including alpha. `Image.color` and the renderer's tint/alpha are distinct state. Do not set every root Graphic white: transparent hit targets can become visible boxes. Confine styling to owned graphics or explicitly leased properties, and restore them on every release path.
- Choose a font for its role. The Nocturne body SDF and MainMenu display variant have different glyph advances even when `characterSpacing` is zero. Reuse the selected font asset's material; do not modify shared font/material assets.
- Let the layout group place its children. Size a new entry through layout properties and sibling order; copying a donor's anchored position can overlap that donor. Clone decoration transforms relative to their intended parent and qualify geometry after layout settles.

Any reusable native-control helper must own callbacks and restoration, distinguish suppression from release, and verify subsequent native navigation after disposal. A correct screenshot while a panel is open does not qualify its cleanup. These rules do not introduce a new public native-cloning API.

Client-only. The server returns a disabled handle and one log line so shared plugin code does not need a side check. The widget research, the vanilla catalog and the input-context list are in the internal research notes. Spike (evidence 88): cloned `PassiveFrame_Normal` + `SimpleStunButton` + Nocturne Serif TMP onto a sorting-5000 overlay; click needed a process-lifetime slot; `IInputContext` needed a Bootstrap-owned injected type.

Mechanisms: a factory that styles Unity UI with vanilla sprites/fonts, a mod-owned overlay (not parented under `UICanvasBase`, where scale and visibility go wrong), and an explicit capture/release contract. The settings panel is a Register/Unregister list of typed rows per mod. General panel buttons are composed from supported parts and game fonts. Named native entry sites qualify a donor explicitly; arbitrary scene buttons may contain unrelated HUD artwork or external targets.

A `UnityAction` whose method lives in collectible Core pins the ALC (Il2CppInterop ConvertDelegate cache, same class as Harmony trampolines). Bootstrap owns the listeners; Core fills a ushort-keyed slot.

This document is the UI module's own contract. Lifetime rules that apply to every module (collectible ALC, skip tokens, Uninstall ordering) are [`modules.md`](modules.md); the plugin-facing `ctx.*` surface is [`api.md`](api.md). How a shown panel actually receives keys, wheel and clicks in this game is [Authoring: input, scroll and chrome](#authoring-input-scroll-and-chrome) — several of those rules contradict what Unity's uGUI would do on its own.

## API

```csharp
using Progenitor.Ui;

// From a namespace that is not Progenitor.* this is Ui.Panel. From Progenitor.Sample
// (or any Progenitor.* type) write Progenitor.Ui.Ui.Panel — unqualified Ui is the namespace.
IUiPanel panel = Ui.Panel("Sample", owner: this, pluginId: MyPluginInfo.PLUGIN_GUID);
var log = panel.Button("Log click");
log.OnClick(() => ctx.Log.LogInfo("clicked"));
var close = panel.Button("Close");
close.OnClick(() => { Ui.Hide(panel); panel.Dispose(); });
var toggle = panel.Toggle("Demo toggle", initial: true);
toggle.OnChanged(v => { /* bind a ConfigEntry */ });
panel.Label("Ember stack: 99");
Ui.Show(panel);   // cursor unlock + Escape closes; gameplay input blocked while hovered
// later:
Ui.Hide(panel);
Ui.RemoveAll(this);
```

| Member | What it does |
|---|---|
| `Ui.Panel(title, owner, pluginId)` | Overlay canvas sorting 5000, cloned `PassiveFrame_Normal` chrome, title TMP. Disabled on the server. The overlay's `CanvasScaler` is copied from the game's own (see [Measuring](#measuring-the-games-design-space-not-device-pixels)). |
| `panel.Button` / `Toggle` / `Label` / `Slider` / `Input` | Clone vanilla `SimpleStunButton`, Toggle, TMP, Slider, `TMP_InputField` (built from the game's font + sprites if no live vanilla donor is both widget-sized and operable). |
| `Text` / `Visible` / `Value` | Handle state. TMP bypasses `LocalizedText`. |
| `OnClick` / `OnChanged` | Core handler; the UnityEvent listener is a Bootstrap trampoline keyed by ushort widget id. |
| `Ui.Show` / `Ui.Hide` | Cursor unlock (Menu_Normal equivalent; `SetCursorSystem` has no setter) through Bootstrap `UiInputContext`. Escape closes the panel. `Show` takes the cursor only on that panel's 0→1; `Hide` releases on 1→0. Nested different panels are refcounted. Cursor lock/visible is snapshotted on each capture 0→1 and restored on 1→0 (not the first capture of the process). Both also re-flow every shown panel (see [Placement](#placement-panels-tile-they-do-not-cascade)). |
| `Ui.CaptureInput(bool)` | Module-level on/off for the input block (`UiInputCapture.SetEnabled`). Off means no context is ever registered. |
| `Ui.CaptureInput(UiCaptureMode, owner, pluginId)` | Per-plugin override of the capture mode. See [Input](#input-what-a-panel-takes-and-for-how-long). |
| `Ui.CaptureMode(pluginId)` / `Ui.IsCapturingInput` | The mode in force for that plugin; whether a context is registered right now. |
| `RemoveAll(owner)` | Dispose that plugin's handles and destroy the exact DontDestroyOnLoad root `progenitor-ui-<pluginId>`. A longer GUID (`example.sample.extra`) is a different root. |
| `panel.Dropdown` / `Number` / `SliderField` / `ColorField` / `KeyField` | The settings kit (SETTINGS1). See [Settings kit controls](#settings-kit-controls-settings1-source-checked-and-game-free-tested-not-live-verified). |
| `panel.TextArea` / `Radio` / `MultiSelect` / `Info` / `Divider` / `ConfirmButton`, `IUiHandle.Interactable` | SETTINGS1 stage 2. See [Stage 2 controls](#stage-2-controls-settings1-source-checked-and-game-free-tested-not-live-verified). |
| `panel.OrderedList` / `Picker` / `Tabs` / `CodeArea`, `handle.Tooltip` (`UiWidgets`); `ctx.Hud.Notify` / `Locate` / `PickInWorld` | P7, experimental. See [Widgets](#widgets-p7-p7b) and [HUD](#hud-notifications-locate-and-pick-in-world). |
| `ctx.Windows.Create` / `Ui.Window` (`UiWindowPreset`), `panel.Text(…, UiTextStyle)` | WINDOWS1. See [Standard windows](#standard-windows) and the [controls catalog](ui-controls.md). |
| `panel.DataTable` / `MasterDetail` / `Chips` (`UiWidgets`); `handle.Term` / `UiTerms`; `ctx.Hud.Tip` | P7b, experimental. See [Data table](#data-table), [Master-detail](#master-detail), [Chips](#chips) and [Help](#help-explained-words-and-first-time-tips-p7b). |

Keybinds are registered per plugin through `ctx.Keybinds.Register(id, defaultKey, description, onPressed, allowWhileCaptured: false)`, which binds the chord to that plugin's own cfg file. See [Keybinds](#keybinds-edges-come-from-held-state).

Skip token: `HarmonySlotHandoff.UiModule` (`"Ui"`).

## Version footer

Every prompt Progenitor shows carries `Progenitor <version>` in small, muted text centered at the bottom of the panel, below its last button. `<version>` is `Ui.FrameworkVersion`: the running Core's version per [versioning.md](versioning.md) (`2026.9.58` for a release, `2026.9.58.3` for an unmerged build), read from the Core assembly's informational version with the `+<commit>` metadata removed.

```csharp
var panel = Ui.Panel("My mod", owner: this, pluginId: MyPluginInfo.PLUGIN_GUID);
panel.Footer = Ui.FrameworkFooter;                        // "Progenitor 2026.9.58"
panel.Footer = "My mod " + MyPluginInfo.PLUGIN_VERSION;   // or your own version, the same way
panel.Footer = "";                                        // none (the default for a plugin panel)
```

| Member | What it does |
|---|---|
| `IUiPanel.Footer` | The footer line, get/set, trimmed. Empty draws nothing. Settable at any time; the panel re-lays out. |
| `Ui.FrameworkVersion` / `Ui.FrameworkFooter` | The Core version string, and `Progenitor <version>`. |

Who gets it by default:

| Panel | Footer |
|---|---|
| Any panel created with Core's plugin id (`progenitor`): the join-refusal panel (`ModManifestDenyPanel`), the per-server pause notice (`ServerModPauseNotice`), the standalone settings panel | `Progenitor <version>`, set in `Ui.Panel` |
| Every `Ui.Dialog` / `UiDialog` (the Install tab's and the mod page's confirmations), whatever plugin id its factory used | `Progenitor <version>`, set in `UiDialog.Open` |
| Mods workspace main pane (Mods detail, Install, Configuration, settings and custom pages) | The component line (see [mods-menu.md](mods-menu.md#workspace-presentation)), set in `Ui.ConfigureMenuPanel` |
| Mods workspace sidebar and tab strip | None |
| A plugin's panel | None until it sets `Footer` |

Layout: the line is a direct child of the panel frame (never a scrolling row), `PanelFooterLayout.FontSize` 13 against a note's 16 and a body label's 18, in the kit's muted note color, anchored bottom-center in a `PanelFooterLayout.BandHeight` (24) band. For a runtime-sized panel or dialog the band is added to the height the frame is sized for (`PanelHeightFor(content + band)`) and the viewport is inset above it, so a panel whose rows fit without a footer still fits and the last button stays above the line; bottom-pinned rows are raised by the band. A self-placed panel reserves the band itself (the consent prompt did, until it was removed 2026-10-05). On a workspace detail pane the line is shifted to the workspace frame's center, clamped to stay inside the pane (`PanelFooterLayout.CentredOffset`). The sidebar and strip never reserve a band. Readback: `mods_ui status.footer` and `status.dialog.footer`.

**Source-checked and game-free tested** (`PanelFooterTests`: version formatting, which panels carry a footer, the Mods footer (Core and Bootstrap, never Dev), the readback keys; the consent band's tests went with the consent prompt on 2026-10-05), **not live-verified**: the drawn position, size and color have not been seen in game.

## Standard windows

A mod's own window starts from one of three presets, so every mod's windows share the game's frame, title, spacing, version stamp, input rules and Escape behavior. Use `ctx.Windows.Create` inside a plugin; it names the plugin in the stamp and closes the window on every unload reason. `Ui.Window(preset, title, owner, pluginId, options)` is the same outside a context (the stamp is read from the owner's `[BepInPlugin]`). Every control and widget, and which one fits a value, is in the [controls catalog](ui-controls.md); the named text styles are its [Text styles](ui-controls.md#text-styles) section.

```csharp
var window = ctx.Windows.Create(UiWindowPreset.Header, "Iron Chest", new UiWindowOptions
{
    Renamable = true,
    Tools = new[] { new UiWindowTool("Sort", "Sort this chest by item type.") },
    Tabs = new[] { "Contents", "Rules" },
});
window.Tools[0].OnClick(SortChest);
window.OnRename(name => SaveName(name));
window.Add(0, window.Panel.Text("Iron Ore", "1,280", UiTextStyle.Number));
window.Add(1, window.Panel.Toggle("Accept overflow", true));
window.Show();               // window.Hide() / Toggle(); Dispose() closes it for good
```

| Preset | Use when | Looks like | Input and controller | Placement |
|---|---|---|---|---|
| `UiWindowPreset.Basic` | A small tool used during play: a dropdown and a few buttons ("Other Gear · here"). | The game's popup frame, the title centered in its band, rows on the settings grid. | `WhenHovered`: the game keeps the input while the pointer is elsewhere. Opened while the player navigates with a controller or the keys (A on the button that opened it), it is shown `WhileShown` instead, so the controller moves into it and B closes it. | In the window row (below). |
| `UiWindowPreset.Header` | One thing the player works on: a container, a station, a named set. | Basic plus the ornamental header band (a darker ground behind the title, the game's title ornaments either side of it, the game's divider under it), an optional toolbar with an optional **Rename**, an optional tab row. | `WhileShown`: takes the input while shown; a controller navigates it. | In the window row (below). |
| `UiWindowPreset.Tabbed` | A large screen like the game's own menus: several tabs of content and a column that describes the selection. | The header band, a toolbar (optional), a tab row across the top, an info column on the left (`window.Info`) and the main content on the right. | `WhileShown`; a controller navigates it. | In the window row (below). |

Every preset: rows are left-aligned (`ContentAlignment = "left"`), the version stamp `<mod name> <version> · Progenitor <version>` is on by default at the bottom (`UiWindowOptions.VersionStamp = false` turns it off, `VersionText` replaces it, `window.VersionStamp` changes it later), and Escape or a controller's B first closes an open list, dialog or key capture; with none open it clears a pending Mods search (the workspace's own Escape step), else closes the **topmost** standard window or floating panel that takes the input (`WhileShown`, or `WhenHovered` with the pointer over it; the one shown last, drawn on top), one per press; only when none is open does the press go on to the kit's [Escape sweep](#input-what-a-panel-takes-and-for-how-long), which hides the Mods workspace and the other panels. `window.IsOpen` reads the result; there is no close event. **Behavior change:** before the standard windows one Escape hid every shown panel; an input-taking floating panel now closes on its own press ahead of the workspace, and a passive one (`Never`, or `WhenHovered` away from the pointer) still goes with the sweep. `UiWindowOptions.CaptureMode` overrides the input mode. Up to six tabs and six toolbar buttons (one or two words, at most 16 characters) are accepted, and the toolbar's buttons, measured as the game's compact buttons (at least 140 wide, 8 apart, Rename included), must fit the row they are drawn in (560 in Basic and Header, 960 in Tabbed); anything more throws `ArgumentException` rather than running past the frame. **Rename** opens the game's prompt with the current title; the answer becomes the title and `OnRename` handlers get it. The caption is the word Rename: the game's font has no pencil glyph.

| Member | What it does |
|---|---|
| `ctx.Windows.Create(preset, title, options = null)` → `IUiWindow`; `ctx.Windows.Open`, `ctx.Windows.VersionStamp` | A tracked window (not shown yet); the plugin's windows still open; its default stamp line. |
| `Ui.Window(preset, title, owner = null, pluginId = null, options = null)` → `IUiWindow` | The same without a context; the caller disposes it. |
| `IUiWindow` `Preset`, `Title` (get/set), `Panel`, `Tabs`, `Info`, `Tools`, `VersionStamp`, `IsOpen`, `IsDisposed`, `Add(tab, handle)`, `OnRename(handler)`, `Show()`, `Hide()`, `Toggle()`, `Describe()` | Rows go on `Panel` with the ordinary kit calls; `Add` puts a row on a tab (returned unchanged without a tab row). `Describe()` is the readback (`preset`, `title`, `open`, `stamp`, `frameWidth`, `headerBand`, `infoColumn`, `capture`, `tabs`, `selectedTab`, `tools`, `rename`). |
| `IUiWindowColumn` (`window.Info`, Tabbed only) `Heading`, `Note`, `Label(text, tone)`, `Fact(name, value)`, `Button(text)` | The info column's rows, on a darker ground, apart from the content's scrolling; a column longer than its clip scrolls on its own (the wheel over it, the right stick on a control in it). `Fact` is one line: the name at the left (shortened with an ellipsis when it runs out of room), the value sized to its text and right-aligned (`Number` when it holds a figure, else `Body`); set the value later with `.Text`. A value counts as a figure when it is digits with only separators, signs or a percent sign (`3,212`, `14 / 40`, `12:04`); `Level 3` is text. |
| `UiWindowOptions` `VersionStamp`, `VersionText`, `Renamable`, `Tools`, `Tabs`, `SelectedTab`, `CaptureMode`; `UiWindowTool(caption, tooltip)` | What differs from the preset. |
| `panel.Text(text, UiTextStyle)` / `panel.Text(name, value, UiTextStyle)` → `IUiLabel`; `UiTextStyle`, `UiTextAlign` | Words in a named style ([Text styles](ui-controls.md#text-styles)). |

### Layout spec (panel units, 1920x1080 design space)

The numbers are `UiWindowLayout` (game-free, `WindowPresetTests`); the runtime draws from them.

**Basic** (640 wide; height = rows, at least 200, at most 80% of the screen, then the content scrolls):

```
+------------------------------------------------------------+  y 0
|                    Other Gear · here                       |  title band 0-72, title 18-58, centered
|------------------------------------------------------------|
| Gear set                              [ Weapons       v ]  |  rows from y 80: 560 wide, 40 tall, 8 apart
| Nothing moved yet.                                         |  Muted
| [Stash all] [Take all]                                     |  inline buttons, captions centered
|           Example Mod 1.2.3 · Progenitor 2026.10.16        |  stamp: 13 pt Muted, centered, 10 above the frame's
+------------------------------------------------------------+  bottom edge (band 24 + 10)
```

**Header** (640 wide):

```
+------------------------------------------------------------+  y 0
|######  <ornament>     Iron Chest      <ornament>     ######|  header band 0-72 (dark wash), title centered at y 38;
|######  ------------------ divider ------------------  ######|  ornaments 171x17 max, 12 from the title; divider y 64 (560x12)
| [Sort] [Locate] [Rename]                                   |  toolbar, pinned at y 80 (40 tall)
| [Contents] [Rules] [Log]                                   |  tab row, pinned at y 128 (40 tall)
| Iron Ore                                          1,280    |  content from y 176, scrolls under the pinned rows;
| Copper Ore                                           96    |  numbers right-aligned
|           Example Mod 1.2.3 · Progenitor 2026.10.16        |  stamp band 24 + 10
+------------------------------------------------------------+
```

**Tabbed** (1040 wide, at most 90% of the screen):

```
+------------------------------------------------------------------------------------------+
|#######################  <ornament>   Storage network   <ornament>  ######################|  header band 0-72, divider y 64 (960x12)
| [Refresh]                                                                                |  toolbar y 80
| [Overview] [Belts] [Settings]                                                            |  tab row y 128
| +- info column (340) ---------+   Throughput                                             |  info column x 20-360 from y 176,
| | Network                     |   Items moved                                  3,212     |  on a darker wash; content 600 wide
| | Chests                  24  |   Per minute                                      48     |  right of it, scrolls on its own;
| | [Locate all]                |   Two chests are full.                                   |  headings and rows share the left
| +-----------------------------+                                                          |  edge of their column
|                    Example Mod 1.2.3 · Progenitor 2026.10.16                             |  stamp band 24 + 10
+------------------------------------------------------------------------------------------+
```

On a canvas narrower than 1156 units the Tabbed frame shrinks to 90% of it and the info column shrinks with it so the content keeps its 560 row; under 160 units of room the column is switched off (never drawn over the content) and back on at the first layout with room. The frame height is the taller of the content and the info column, at most 80% of the screen; a longer column scrolls inside its clip (the wheel over the column, or the right stick while a control in it has the focus; a controller enters the column with Down from the tabs on every tab, however it is scrolled, and Left and Right cross between the column and the content to the nearest row drawn on that side, and the focus scrolls them into view), which ends at the last whole row above the version stamp's band, never over it or past the frame. Rows added to a shown window (an info fact, a content row) lay it out again on the next frame, so it grows up to its cap. While it stays open a window never gets shorter (switching to a shorter tab keeps its height); it opens next time at its content's height. Toggles, value boxes and bars end at the row's right edge (a window has no reset arrows, so the Options page's gutter for them is not kept).

**Placement.** Standard windows open in one row, in the order they were opened (the oldest at the left), centered on the screen with 24 units between frames, so they never cover each other. Every window's top edge is on one line and the window grows downward, so its title does not move: while the Mods workspace is shown the line is 16 units under the workspace's tab strip (a window never covers the tabs), otherwise it is where the tallest frame the screen allows (80% of its height) would be centered. When the next window does not fit the row (16 units from each screen edge), it cascades over a window of the row, from the right: one title band (72) right and down per layer, clamped inside the screen, so the title of the window under it stays visible; on a screen too short for whole bands the step shrinks. Every window's height is capped so its bottom stays on screen. At 1920x1080 a Tabbed and a Header window share the row and a third window cascades over the Header; on 3840x1080 all three share the row. While a standard window is shown, an ordinary floating panel (`Ui.Panel`, with or without `GameWindow()`) joins the same row as a 640-wide frame, so a panel and a window never land on each other; with no standard window shown, floating panels keep the panel flow. Dialogs, Mods workspace panes (parked ones too), panels mounted in a chest or station tab, panels placed by their owner, top-pinned panels, wide centered game windows (`GameWindow(width)`) and [docked panels](#docked-panels-dock1) never join the row (the row is laid out on the canvas the docked panels leave), and a window's place is computed from the row's membership, the canvas size and the workspace's tab strip alone, so a re-flow that changes none of them leaves it where it is (a canvas or strip change can move it). When the row changes a window's height cap (a window in front of it closed), it is laid out again and hung from its top line at its new height.

**Draw order and the workspace dim.** Bottom to top: the Mods workspace, ordinary panels, the workspace dim, the standard windows (and the floating panels in their row) with the one shown last on top, the same one Escape closes first (a floating panel in the row too: Escape closes the row topmost first, as the game closes its topmost popup), then every dialog (`ctx.Dialogs`, and the Rename prompt a window opens, with its own dim over the window). While the Mods workspace is shown and a window that takes the input while shown is open (Header, Tabbed, or a Basic window opened with a controller), the workspace behind the windows is dimmed with the game's popup blocker color at 60%, so the page does not read as part of the window. The dim takes no clicks: the workspace stays usable beside a window. A Basic window that leaves the input to the pointer does not dim it. The wheel goes to the window or dialog drawn on top under the pointer, never to the page behind it. Every dialog (`ctx.Dialogs`, the Rename prompt) lifts its version stamp 10 units off its frame's bottom edge, as the windows do.

**Art.** The frame is the game's popup frame (`Window_Box_Background`, the art `ctx.Dialogs` uses). The divider is the game's `Divider_Big_02` (the dialogs' title rule; a thin kit rule until it is loaded). The title ornaments are the game's `Window_TitleSideOrnament` (227x17), borrowed from the game's generic popup prefab (`GenericPopup`, resident in a world), which draws one 171x17 either side of its title text over its divider. The kit draws them the same way: each fills the room the title leaves in the 560-wide title row, at most 171 wide and 17 tall, 12 from the words, the right one mirrored, and none when that room is under 48 (a title wider than 440 units). Until the sprite is found (the window paints again on each layout) the title stands alone over the divider, the way the game's settings pages title their sections; nothing is drawn in the ornaments' place. Which parts drew is in the settings art readback (`settingsArt.parts.windowTitleOrnament`).

**Status: game-free tested, not live-verified.** The specs, the stamp, the composition (pins, tabs, toolbar, rename, input modes) and the gallery are tested in `WindowPresetTests`. The runtime drawing (the wide frame, the window row and cascade, the header band, the ornaments, the info column, the draw order and the workspace dim) is source-checked and built only; whether `Window_TitleSideOrnament` loads, and how it looks, has not been seen in game. The live check is the procedure in the [controls sample README](../samples/Progenitor.ControlsSample/README.md#gallery).

## Input: what a panel takes, and for how long

Two effects used to travel under one word. **Cursor unlock** has to last the whole time a panel is shown, or the player cannot move the mouse to the panel. **Blocking gameplay input** is the only half that belongs to the pointer. They are now separate mechanisms with separate lifetimes (evidence 177).

| Mechanism | Lives in | Lasts | Does |
|---|---|---|---|
| Cursor unlock + Escape *forwarding* | Bootstrap `Progenitor.Ui.UiInputContext` | while any panel is shown | Unlocks/shows the cursor, forwards Escape / `Back` to a Core slot. Consumes nothing. |
| Gameplay input *block* + Escape *consumption* | Core `UiInputCapture` + `GameInputBlocker` | while the pointer is over a panel (default) | Pushes a game-owned `ProjectM.RotateCameraBlocker` into `InputActionSystem._InputContexts` at order **599** and postfixes its `GetConsumedInputs` to apply the mask. |

`UiCaptureMode` decides only the second row. The framework default is **`WhenHovered`**: over a panel the wheel scrolls the list and a click hits a row; off the panel the same wheel and click go to the game. `WhileShown` is the old behavior, kept for a genuinely modal panel; `Never` is what `Ui.CaptureInput(false)` always meant. It is overridable per plugin and framework-wide by the client-only config entry `Ui.CaptureMode` in `progenitor.ui.cfg`.

The hover test transforms the panel's rect corners out to **screen pixels** and compares against `Mouse.current.position` — necessary because the overlay copies the game's scaler, so a canvas unit is not a device pixel. The wanted state is `enabled && count > 0 && pointerBlock`, ANDed and never ORed, so a hover gate stuck true cannot leave a context registered after the last panel closes. Only edge crossings reach the refcount.

**Escape is consumed.** One press closes the mod panel and does *not* open the vanilla system menu (evidence 178). Three things had to be true at once:

- The mask **names the actions** rather than relying on `BlockAllKeys(false)`, whose `includeUINavigation` parameter deliberately exempts `UINavigationActions` — and whether `Back` is in that array is decided by a `.cctor` the game's metadata does not contain. `Back` (17) and `Menu_Close` (1004) are masked by name, correct whichever way that unreadable `.cctor` goes.
- Registration follows the **panel**, and the pointer selects a *scope* (`UiMaskScope`) instead of adding/removing the context: `Full` over a panel, `CloseKeyOnly` off it, with the close key taken in **every** scope. Hover scoping had otherwise removed the context entirely, and a mask alone can subtract nothing when nothing is registered.
- Order is **599**, not `RotateCameraBlocker.Register`'s hard-coded 600 — which *is* `InputContextOrder.MenuInput`, where the system that opens the menu sits. Which of two equal-order entries subtracts first is a `Comparison<InputContextState>` that cannot be read offline, so the tie was removed rather than predicted. Nothing is named between `HUDMenu` (502) and `MenuInput` (600), so every documented ordering relationship survives (still below chat at 99, still above Camera 1000 / Gameplay 1001).

The cost of order 599 is that `RotateCameraBlocker.Register` is unusable: the context is pushed with `AddInputContext(IInputContext, World, int)` directly, and `Deregister` must **never** be called — its `_Registered` guard is never set and it would silently leak the entry. Both halves verify against `IsContextRegistered`.

**A `Never` panel does not consume Escape.** The cursor unlock and Escape forwarding come from Bootstrap's refcount, which every shown panel drives whatever its mode is, but consumption needs a registered context and a `Never` panel deliberately registers none. So with `Never`, one Escape closes the panel *and* opens the system menu. That is the documented meaning of the mode, not a defect. **`Ui.CaptureInput(false)` is the same case**: the master switch removes Core's context and does not touch Bootstrap's refcount, so Escape still closes the panel and the menu still opens. `api.md` said the opposite until job FDOC5 — that Escape stopped closing the panel too — which is not what `UiHandles` does: `SetCapture(true)` is unconditional on the 0→1 shown transition.

**The mask can be absent.** Consumption depends on the `GetConsumedInputs` detour resolving from the address library. If it does not, the context still registers with vanilla `_BlockRotateCamera` behavior — camera rotation blocked, nothing else — and Escape is **not** consumed. The register log line reports which of the two actually happened rather than what was intended (`mask=live` / `mask=absent`); the remedy is to add `ProjectM.RotateCameraBlocker$$GetConsumedInputs` to `tools/addresslib/wanted-client.txt` and rebuild.

`ZoomCamera` is the one place the mask departs from chat's recipe (`ChatFocusedInputContext`), which leaves the wheel alive so a player typing can still zoom. A panel that scrolls with that same wheel cannot afford it (evidence 167).

Never add or remove a context from inside an input callback: `InputActionSystem` sets `_DenyModificationsOfInputContexts` around every `GetConsumedInputs` / `HandleInput`, and both mutators then log an error and do nothing. `UiInputCapture` defers to the next frame tick whenever that flag is set.

**Capture mode is not how a panel receives its own input.** `WhenHovered` / `WhileShown` / `Never` decide only the gameplay *block*. Typed keys, wheel notches and click-to-focus are delivered by a separate pump; see [Shown-input pump](#shown-input-pump-not-the-hover-pump).

## Panels size, clip and scroll

A panel's row count is supplied by *other people's plugins*, so it has no fixed size (evidence 163).

- Rows live in a top-anchored `content` rect inside a `viewport` clipped by **`RectMask2D`**, with a `ScrollRect` over it. The viewport also carries a **raycastable `Image`** (alpha a hair above zero). Without both, rows scrolled outside the viewport keep drawing and stay hit-testable — they slide over the chrome above the list and steal its clicks. `RectMask2D` rather than `Mask` because the clip is rectangular and every panel already has its own overlay canvas, where a stencil mask per canvas is a real cost. Scrolling is `Clamped` with `inertia = false`. The wheel that moves this list is the [shown-input pump](#the-wheel-the-framework-is-the-mover-not-ugui), not uGUI `OnScroll`.
- **Every** row measures its own caption, not just standalone labels. All four row types (`Label`, `Toggle`, `Slider`, `Input`) go through one `FitLabel`, which reads the TMP `preferredHeight` after the rect width is set and `ForceMeshUpdate` has run, grows the label's own rect, and returns the height the row reserves. Which control a row carries has nothing to do with how long somebody else's description is (evidence 164). Each row is therefore placed provisionally and placed again once its height is known.
- Labels are `TextOverflowModes.Ellipsis`. A clamp that only shrinks a *reservation* is not a boundary — TMP's default `Overflow` painted a ten-line description straight over the three rows beneath it (evidence 167). `MaxRowHeight` is 400 px and now only stops one pathological entry filling the viewport.
- On `Show` — the first moment every row is known — the content rect is sized to its rows and the frame to that content, capped at 80% of the available height, past which the list scrolls. The title is anchored to the top edge; anything positioned from the panel's center would drift as the frame resizes.
- The frame sprite is used as `Image.Type.Sliced` **only when `sprite.border` is non-zero on some edge**. Slicing a border-less sprite is not an error and logs nothing — it just stretches, which is what turned a self-sizing panel into a 640x819 oval (evidence 164). Otherwise the panel takes a flat dark ground.
- **The scrollbar is hidden when the content fits.** `ScrollbarVisibility.AutoHide`, not `Permanent`: `Permanent` names who *owns* the state, not what it will be, and `ScrollRect` force-activates the bar on every layout rebuild, undoing a manual `SetActive` a frame later. `AutoHide` hides the bar when the content fits and, unlike `AutoHideAndExpandViewport`, never re-lays out the viewport — which was the only reason `Permanent` was chosen. On an ordinary overlay the viewport's bottom inset is `0`, because the content rect already ends with `PadBottom` and counting it twice made `PanelLayout` and the real `RectTransform` disagree about the same panel (evidence 175). A pane with **pinned footer chrome** is the other case: that inset must be the footer band **plus a gap**, or the last row collides with the band. See [Chrome insets](#chrome-insets-are-arithmetic).

The arithmetic is game-free in `Ui/PanelLayout.cs` and `Ui/PanelFlow.cs`, with tests. A panel may opt out of that flow entirely with `Ui.SelfPlacePanel` (`IUiRuntime.SelfPlacePanel`, additive, default no-op): for a self-placed panel neither the layout pass nor `Reposition` writes the frame's `sizeDelta` or `anchoredPosition`, the layout line ends `self-placed`, and a skipped re-flow logs `re-flow skipped rank=… at=(self-placed)`. Only the consent prompt asked (removed 2026-10-05; the seam stays) — it was a centered modal and not one of a ranked set, and the re-flow that ran when the Mods workspace's panels joined the shown set moved it to `at=(-332,0)` one frame after it was revealed centered (CONSENT2; see [mod-manifest.md](mod-manifest.md)). A runtime that cannot build a viewport logs and parents rows onto the frame, degrading to the unclipped behavior rather than showing nothing. `ScrollRect` and `RectMask2D` are on the `DisableForeignBehaviours` allowlist so a cloned vanilla subtree cannot arrive with them switched off.

### Measuring: the game's design space, not device pixels

Every vanilla screen is laid out in the game's design space and follows the player's resolution and UI-scale setting. Mod overlays were `ConstantPixelSize` at `scaleFactor = 1`, so they were measured in raw device pixels — right at the reference resolution and wrong everywhere else (evidence 165). The overlay now copies a vanilla `CanvasScaler`'s `uiScaleMode`, `referenceResolution`, `screenMatchMode`, `matchWidthOrHeight` and `referencePixelsPerUnit` — the same read-the-game's-own-asset approach `FindFont` already uses — and logs once which canvas it copied and what it took. With no donor (the main menu before the HUD exists) it keeps the constant-pixel behavior and says so once. `FindFont` must not walk the whole scene per widget: a per-row `FindObjectsOfType` is a main-thread stall. FindFont picks the loaded body face by name (`Nocturne Serif Regular SDF`, prefix-agnostic) from one `TMP_FontAsset` enumeration per cache lifetime, demotes MainMenu/DropShadow/Hover display variants and LiberationSans, falls back to the options-menu label then a scene sweep, and `mods_ui status.fontDonor` carries `{font, path, source}` (`asset-list` / `options-label` / `scene-sweep`) with `status.fontAssets` only when `args.fonts=true`. The font asset is cached until the world epoch changes, the asset is destroyed, or Core unloads. The same rule governs every per-frame UI path: do not `FindObjectsOfType*`, `GetComponentsInChildren`, `Resources.FindObjectsOfTypeAll` or walk a full hierarchy on a tick. Cache the result and invalidate on a named state change (host/menu pointer, our attach/drop, world epoch, native `childCount`); tolerate a dead Unity object the way holds already do. Yesterday's freeze was uncached per-row `FindFont`; a later ~50 ms tick in world with the workspace closed was `TryCacheLivePlayBackdrop` scanning `PlaySelectMenuView` every frame. Enable `uiTickTiming` and read `Ui: tick-breakdown` to name the step.

**All panel measurements in this document are canvas units, not device pixels.** The height cap is measured from the overlay canvas's own rect. The hover test is the deliberate exception: it converts out to screen pixels because the scale factor now sits on the canvas `RectTransform.localScale`.

**Which ruler answered is on the layout line** (`ruler=canvas` / `screen` / `constant`, job FDOC5, R43 finding 5). Until it was, the sentence above was a rule the code did not follow: the canvas rect can read zero — most plausibly at the one moment this code runs, since `SettingsPanel.Open` builds a fresh panel and lays it out in the same frame, before Unity has sized the canvas — and the fallback was `Screen.height`, three lines under a doc comment saying `Screen.height` is a number from a different coordinate system. On a 4K display with the UI scaled up that capped the panel at `0.8 × 2160 = 1728` units inside a 1080-unit canvas and printed `(fits)`, which is evidence 163's symptom produced by the fix for it.

Two things changed. `ruler=` names the rung, so a panel measured against the wrong unit is diagnosable instead of invisible; and while the overlay is in the game's design space the `screen` rung is **skipped entirely** — an unreadable canvas goes straight to the constant. `ruler=screen` therefore appears only on a build or scene where no vanilla `CanvasScaler` could be copied and one canvas unit really is one pixel. `ruler=constant` means nothing could be read and `avail` is 1080 whatever your display is: it is wrong in a way you can see, which is the point of preferring it. The numbers carry `u`, not `px`, for the same reason.

**And *when* it was measured is on the same line** (`canvas=sized|zero|norect|threw`, `settle=final|armed|settled|gaveup`, job FUI32). A panel's canvas is created and laid out inside one `Plugin.Frame` tick, and a root `ScreenSpaceOverlay` canvas is not sized until Unity's canvas update at the end of that frame — so the first pass measures a zero rect, prints `settle=armed`, and is measured again a frame later (`settled`), or says `gaveup` after eight frames rather than leaving a guess looking like a measurement. **A line that says `armed` is provisional; believe the `settled` one.**

The wait itself is `Ui/UiSettleGate.cs` (job FUI33, R48 finding 2), and it is game-free so the four transitions are tested: armed, settled, re-armed by its own re-layout, given up on. The third is the one that was wrong — a re-layout that still could not read the canvas armed the panel again from inside the tick whose answer the pump was about to act on, and the pump dropped it anyway, so the panel kept the fallback size for the session with `settle=armed` as the last word about it. The gate now refuses to drop a panel that was re-armed during the pass, and the budget still bounds the wait because a re-arm does not reset the frame count. `Ui.Uninstall` resets the gate and `pin-diag` reports `settleOwed=N pump=True/False` (R48 finding 6): the pump-armed latch is process state, and a latch left standing over a `Plugin.Frame` that `Plugin.Uninstall` nulled would mean no later panel ever subscribes while every one of them prints `settle=armed`.

### Is a click going to reach the panel: the interaction probe

The probe logs one `Ui: interaction probe` line per show, with a `verdict=` naming the first missing link in the chain a click travels (no `EventSystem`, none active, no `EventSystem.current`, no input module, no `GraphicRaycaster` of our own, a `CanvasGroup` over us with `blocksRaycasts` off, a canvas above us that overlaps) and one sentence saying what to do. Everything but `Live` is a warning; `CanvasAboveOverlaps` is the one partial claim — part of the panel is covered, the rest is reachable.

Orders actually in use (do not raise 5000 to "win"):

| Canvas | Order | Role |
|---|---|---|
| Native FullscreenMenu / outer tabs | **1** (live `rects`) | Game HUD menu. Below every overlay. |
| Mods curtain `progenitor-ui-core-mods-background` | **4999** | Click-eating backdrop. One below overlays so dialogs and workspace win. |
| Mods workspace frame `progenitor-ui-core-mods-workspace` | **5000** | Art + scrim behind both panes. Created before pane overlays; no GraphicRaycaster. |
| `Ui.Panel` overlays | **5000** (`UiOwnership.OverlaySorting`) | Mod panels. (The consent prompt used this band until it was removed 2026-10-05.) |
| `CanvasUnscaledMainMenu` (main menu, `ConnectingView`) | **9000** | Game's own main-menu canvas. Sorts **above** every mod panel. |

`canvasAbove=<name>@<order> overlapsPanel=yes` means a graphic on that higher canvas covers part of yours — a click there goes to the game, not to you. Raising our order to beat 9000 would also outrank loading screens, modal popups and the disconnect UI, and this overlay is `DontDestroyOnLoad`. Leave 5000; treat the overlap as the probe's job. `overlapsPanel=no` on a canvas above us is fine: it takes nothing. `verdict=Live` is the all-clear. A panel that draws and does not receive clicks: grep `Ui: interaction probe` and read `verdict=` first.

`canvasAbove=<name>@<order> overlapsPanel=yes|no|?` is the last of those, and it applies the filters uGUI applies before a graphic can take a pointer (job FUI33, R48 finding 1): enabled, `raycastTarget`, registered to *that* canvas rather than a nested one, rects that overlap, not culled, `depth >= 0`, and no `CanvasGroup` with `blocksRaycasts` off over it. It used to test only the first three and the rects, so a view left visible-and-inert on the main-menu canvas — the ordinary way a game parks a screen — made *every* mod panel shown there warn that part of it was unclickable. On the main menu the named canvas is typically `CanvasUnscaledMainMenu@9000`.

**Alpha is read and never judged** (job FUI34, R49 finding 1). FUI33 also filtered on `Graphic.color.a`, and nothing in uGUI does: `GraphicRaycaster` skips on `depth == -1`, `!raycastTarget` and `canvasRenderer.cull`, then the rect, then `Graphic.Raycast` applies `CanvasGroup.blocksRaycasts` and any `ICanvasRaycastFilter` — the color is never consulted. A full-screen transparent `Image` with `raycastTarget` on is not a stray spacer (a spacer has `raycastTarget` off), it is the canonical uGUI modal blocker: the one graphic that eats a mod panel's clicks and leaves nothing in a screenshot. Filtering it out turned the probe from crying wolf into staying silent about exactly that, which is the worse failure for an instrument whose job is to say a panel is dead. The value is still on the reading as a diagnostic.

The `CanvasGroup` walk models one rule `Graphic.Raycast` applies to the parent chain: the first group with `ignoreParentGroups` is the last one consulted, and that flag is read off the component itself, so a disabled group carrying it still ends the walk. That was missing until FUI34 and over-counted — harmless for our own panel (a false `RaycastsBlocked`, an over-warning) and an under-warning once the same walk was reused for a foreign graphic.

The walk does **not** skip a disabled group, and the engine fact behind that is cited rather than assumed (job FUI35, R50 finding 1). FUI34 also added an `enabled` test on the claim that `CanvasGroup.IsRaycastLocationValid` is `!enabled || blocksRaycasts`; no Unity version has ever had that `enabled` term. It is `return blocksRaycasts;` in `UnityCsReference` (`Modules/UI/ScriptBindings/CanvasGroup.bindings.cs`), and the `enabled` skip lives only in Unity 6's rewritten `Graphic.Raycast` (`Unity-Technologies/uGUI`, branch `6000.0`, `Runtime/UI/Core/Graphic.cs`). *Inferred, not verified* (job FCFG5, R52 finding 8): that this build's `Graphic.Raycast` collects its filters with `GetComponents` — disabled components included — and calls `IsRaycastLocationValid` on each. That is the shape of the uGUI source for this version and it is why the group is reached at all, but the native `Graphic.Raycast` has not been examined and the rule below does not rest on it. *Metadata-verified*, settled against this machine's client metadata: `UnityEngine.CanvasGroup.IsRaycastLocationValid` and `get_blocksRaycasts` share one native address (the method *is* the getter), and `UnityEngine.UI.Graphic` declares only `Raycast(Vector2, Camera)` with no Unity 6 `Raycast(Vector2, Camera, bool)` overload, alongside `private Vector4 m_RaycastPadding; // 0x3C`, the `com.unity.ugui` 1.0.0 layout. So a disabled group with `blocksRaycasts` off still blocks on this build, and the walk counts it. **An engine claim in a review is not evidence; cite the source file and the metadata entry, the way this project cites the game's metadata for a stride.**

**Which way the read errs, per filter.** Towards `yes` (over-warning, the safe direction): `Image.alphaHitTestMinimumThreshold` and `ignoreReversedGraphics` are further refusals uGUI can make that this read does not model. Towards `no` (under-warning, so it is named rather than left implied): the `CanvasGroup` walk does not stop where `Graphic.Raycast` stops for a parent `Canvas` with `overrideSorting`, so a non-blocking group above such a canvas is counted here and ignored by uGUI. Unreachable on the screens this probe runs against today, and documented rather than modeled for that reason.

The reading is built in two steps — geometry first, then the corroborators — so only a graphic actually over the panel pays for the 64-level parent walk (job FUI34, R49 finding 5). Up to `MaxProbedGraphicsPerCanvas` graphics are examined per canvas.

### Placement: panels tile, they do not cascade

Panels are **not draggable** — there is no title-bar drag, no move affordance and no z-order control. A cascade idiom carries "the user can drag the top window off the one beneath it" as a hidden premise, and a 40-unit step between two 640-wide frames leaves 94% of each under the other (evidence 173).

The frame's width is a constant (`PanelWidth = 640`), only its height is content-derived, so how many frames fit side by side is a question about the canvas alone. Panels are placed from a `PanelSpot(rank, count)` — index in the shown set, and the size of that set — with a 24-unit column gap and a 16-unit edge margin. `Ui.ReflowPanels` is the only layer that can see every live handle, so it counts the set and re-flows all of them on Show and on Hide. The slot survives as the *ordering* (lowest-free, so the longest-open panel keeps the leftmost column). Overflow past the last column steps down by a whole 72-unit header band.

A panel therefore **may** move when another panel opens or closes. The narrower promise that replaces "a panel never moves while it is shown" is the one that protects the player: **a re-layout caused by *using* a panel never moves anything.** `Relayout` (the pass that runs when a row's text is rewritten under the player's own click) recomputes the same rank out of the same set and lands on the same position by construction; only Show and Hide change the set.

### Docked panels (DOCK1)

Status: source-checked, built and game-free tested (`UiDockTests`); **not live-verified**. Gallery: Windows tab, **Docked panel** (`samples/Progenitor.ControlsSample/GalleryDocked.cs`).

`panel.Dock(UiDock.Left, margin)` (or `UiDock.Right`) puts a floating panel or window against a screen edge, so the middle of the screen stays clear: a preview's controls beside the previewed object, a Find panel on the left while the game's chest window is open on the right. `panel.Dock(side, new UiDockOptions { ... })` takes the options. `UiDock.None` undocks it.

| Option | Default | Effect |
|---|---|---|
| `Margin` | 16 | Panel units between the screen edge and the frame (0 to 480). |
| `Align` | `Center` | `Center`: vertically centered on the screen. `Top`: hung from the top margin, growing down. |
| `Width` | 0 | A game window's frame width in panel units; 0 keeps its own (`GameWindow(width)`, else 640). |
| `WidthFraction` | 0 | A game window's frame width as a share of the screen width (0.3 is 30%), at most 0.9; it wins over `Width` and follows a resolution change. |

The rules:

- **Width.** A docked frame is never narrower than the kit's 640 frame (the rows are laid out for it) and never wider than 90% of the screen, so 30% is 640 at 1920 wide and 768 at 2560. `Width` and `WidthFraction` apply to a game window (`UiLook.GameWindow`); an ordinary kit panel keeps 640 and a standard window keeps its preset's width.
- **It stays put.** Its place is computed from its dock, the other docked panels on its side and the canvas alone, so other panels opening and closing never move it. The runtime checks the canvas size every 15 frames while a docked panel is on screen and lays it out again when it changed. Its height is capped to the screen between the margins (under the Mods workspace's tab strip while the workspace is shown).
- **The others keep clear.** A docked panel never joins the window row or the panel flow. Both are laid out on the canvas the docked panels leave, with a 24-unit gap from the docked frame, and centered on it. When what is left could not hold one 640 frame (a 640 dock on a 1280-wide screen), the docks are ignored for the others: a panel over a docked one is better than a panel off the screen.
- **Several on one side** line up from the edge inward in slot order (the longest-open at the edge), 24 units apart, clamped inside the screen.
- **Precedence.** Dock replaces `Ui.TopPanel` and a wide game window's centering while the panel is docked. Dialogs, Mods workspace panes and self-placed panels are never docked.

Pair a left-docked panel with a camera preview's framing offset (`CameraPreviewOptions.ScreenOffset`, [modules.md](modules.md#camera-preview)) so the previewed object sits in the open part of the screen:

```csharp
var controls = Ui.Panel("Object preview", ctx.Owner, ctx.Guid).GameWindow(640f);
controls.Dock(UiDock.Left, new UiDockOptions { Margin = 24f, Align = UiDockAlign.Center });
// ... add the controls, then:
controls.Show();
_preview = ctx.Camera.Preview(point, yawDegrees: 30, new CameraPreviewOptions { Hold = true, ScreenOffset = 0.15f });
```

## Keybinds: edges come from held state

`ButtonControl.wasPressedThisFrame` does not mean "this key went down since you last looked". It is `pressedStepCount == updateStepCount` — true only while the Input System is in the *update step* that processed the press. Progenitor samples once per rendered frame, so when the game advances the input step more than once between two samples, the step that saw the press is one we never observed and the press is simply gone. That is why every mod keybind needed two presses (evidence 161).

Every input edge the framework exposes is therefore derived from a **held state we own**, with the engine's flag as a bonus rather than the source of truth (`Ui/KeyEdgeTracker.cs`, and Bootstrap's own `KeyPressEdge` for Escape, which must not lose its sampling history to a `.corereload`):

- the Input System's flag is trusted whenever it *is* set;
- otherwise a key seen **up on the immediately preceding sample** and down now counts as a press.

Contiguity is load-bearing: the pump skips frames where every bind is suppressed by a captured panel, and after such a gap a key that is merely still held must not read as a fresh press. A gap costs at most the one press inside it and re-seeds, so it never latches off. One verdict is cached per key per frame, because `F9` and `Ctrl+F9` share a main key and the pump asks about both in the same frame. A keyboard `deviceId` change clears the held state.

An unreadable `Time.frameCount` is **not** frame zero. A fabricated sentinel fed to a cache keyed on it makes the cache permanent — whatever the first failed read decided, returned forever, for every key, ignoring even the Input System's flag. `NoFrame` falls back to the raw flag alone and leaves the tracker untouched (evidence 167). Never observed live; it needed one transient read failure.

Both `UnityKeyInputSource` and Bootstrap's Escape path log once, with `InputSystem.settings.updateMode`, when a press had to be recovered from held state. The line's presence is the standing evidence that the flag and our frame disagree on that build; its absence means they agree. `pin-diag UiInputContext` carries `escapeEdgeOnly=<n>` for the same reason.

Chat focus suppresses a bind, and so does a focused text field, except a bind on a mouse button (`Keybinds.TypingSafeChord`: a mouse button never types a character; 2026-10-10, Mouse Back was dead while the settings search had focus). A click outside a framework field leaves it: the field is deactivated and, when it is the event system's selection, deselected (`BlurArmedFieldOnClickElsewhere`), so the focus probe is false again on the next frame. Readback: `mods_ui nav_state` `textFocus` (`textFieldFocused`, `pumpSawTextField`, `ownedField`) and `mods_ui status` `focus.lastBlur`. `allowWhileCaptured` defaults to false: a panel is open, the player is clicking it, and a stray hotkey is almost never what they meant.

**One key, one winner (KEYS1, source-checked and game-free tested, not live-verified).** When binds share an exact chord, only one fires. The game's own bound keys always come first. Between mods, the one installed first wins; a tie or an unknown time falls back to plugin id order. The pump asks `KeybindArbiter` on the press edge only, so a quiet frame costs nothing. A bind that lost does not run, and the log says `Ui: keybind '<plugin>/<id>' was pressed but not fired: <reason>` once per press. Before KEYS1 both handlers ran. An ordinary mod's config-file key is only reported: Progenitor cannot stop another mod from reading its keys. The rule, the words and the remap flow are in [mods-menu.md](mods-menu.md#keybinds-tab).

**Hold and release.** `ctx.Keybinds.RegisterHold(id, key, description, onPressed, whileHeld, onReleased)` adds per-frame and release handlers to the same bind. The hold follows the main key's held state (`IKeyInputSource.IsHeld`), and modifiers are not re-checked after the press. A hold ends with `onReleased` when the key comes up, and also whenever the bind stops being allowed to fire: a panel takes input, chat or a text field gets focus, or a key capture starts. A held action therefore never sticks. A bind that is not active starts no hold. There is no "only when no game menu is open" context. Binds are suppressed only by a mod panel's capture, chat or a text field ([api.md](api.md)).

**Map keys.** `ctx.Keybinds.RegisterInMap(id, key, description, onPressed)` and `RegisterHoldInMap(id, key, description, onPressed, whileHeld, onReleased)` register a bind that fires only while the game's world map is open (the full-screen menu on its Map tab). It competes only with what is live there (other map and menu keys, the game's map and menu keys, and Push To Talk, which the game reads on every screen), so a key that means something in play (a letter, a mouse button) can be used in the map. Closing the map under a held key releases it. Plugin example: the Sample's map ruler (`samples/Progenitor.Sample/SampleMap.cs`): Ctrl+C over the map logs the world point under the cursor (`RegisterInMap`), and holding Ctrl+R measures a distance (`RegisterHoldInMap`: the press marks the start, each held frame follows the cursor, the release logs the distance and a release with the map closed cancels). Core's debug buff map teleport (`src/Progenitor.Core/Debugging/DebugMap.cs`, [Debug tools](modules.md#debug-tools-debug)) registers the same map-scope hold through `Keybinds.RegisterScoped`.

**Key capture.** The Keybinds tab reads a chord with `Keybinds.BeginCapture`, which applies the rebind flow's rules (Escape cancels, AltGr refused, gated by chat and text fields) without storing anything, so it can preview a clash before **Save**. A capture and a rebind cancel each other.

## Controller and keyboard navigation (NAV1)

Status: source-checked, built and game-free tested (`UiNavTests`). **Verified live 2026-10-10 at the title screen** on the Steam client through the bridge's virtual gamepad (the real device read, focus-free): the Mods list (search, filters, entries), A into a mod page and the focus following, B back to the list, the triggers to Install Mods and back, a settings page's rows (the game's band and the description column), a custom page, a confirmation dialog (start on No, left and right, B answers it, the focus back on the control that asked), the Uninstall prompt (starts on No; B keeps the mod), the mouse putting the marker away and the next press bringing it back, B in the list closing the window. **Not verified:** in a world (the game's own menu under the workspace: the held Close on B, whether its minor-tab actions also take the triggers), the Install, Keybinds, Preferences and Server panes beyond their first stop, floating windows, the date-time picker, and a physical controller. With the window up at the title screen the game's own menu shows its controller prompts while a pad is in use; no press reached it in the run.

Every Progenitor screen can be used with a controller or the keyboard alone. Mods get this for free: the controls they add through `IUiPanel` are reached and changed the same way as the framework's own. The model is `UiNav` (game-free, `src/Progenitor.Core/Ui/UiNav.cs`); the runtime reads the devices once a frame while a panel is shown (`SampleNav` in `UnityUiRuntime.cs`, part of the [framework's input layer](modules.md#keybinds-fui5)).

**Which screen takes it.** The topmost screen that takes input: an open dialog, else the newest shown window whose [capture mode](#input-what-a-panel-takes-and-for-how-long) is `WhileShown`, else the Mods workspace. A window that captures only under the pointer (`WhenHovered`, the default) is never navigated, because a controller there is playing the game.

| Controller | Keyboard | Does |
|---|---|---|
| D-pad or left stick | Arrow keys | Move the focus to the nearest control in that direction on screen. A focused slider, number, toggle, dropdown or radio list takes left and right itself (below). |
| A | Space; Enter | Press, flip, open or pick the focused control. A keyboard Space or Enter counts only when it went down while nothing else owned it: Enter that answers a dialog or a screen's own Enter, or that leaves a text field, and a key that ends a key capture, never act again through navigation (they stay out until released). |
| B | Escape (unchanged) | Close an open dropdown list; on a Mods workspace page, go back to the list; else what Escape does: answer the dialog, a screen's own Back, then hide the panels. While a text field has the keyboard, a key capture waits, or another dropdown list is open, B is Escape first (it leaves the field, cancels the capture, closes the list). |
| X | | Reset the focused setting to its default (what its reset arrow does). |
| Left and right triggers | | The previous and next Mods workspace tab (Mods, Install Mods, Keybinds, Preferences, Server). The bumpers stay the game's own (its menu's outer tabs). |
| Right stick | | Scroll the list the focus is in. |

A direction held for 0.40 s repeats every 0.09 s; buttons act once per press. While a text field has the keyboard, or a key capture is waiting, only B navigates.

| Control | Controller |
|---|---|
| Button, icon link, sidebar entry | A presses it. |
| Toggle | A, left or right flips it. |
| Slider | Left and right move it by a twentieth of its range. |
| Slider field (`SliderField`) | Left and right step it; A gives its value box the keyboard. |
| Number | Left and right step it; A gives its box the keyboard. |
| Dropdown | Left and right step through the options (wrapping); A opens the list, up and down move the highlight, A picks, B closes it. |
| Radio list | Left and right choose the previous or next option; A the next (wrapping). |
| Multi-select | Left and right move a cursor over the options (the details pane describes it, as the pointer's hover does); A flips that option. |
| Text field, text area, color | A gives the field the keyboard. A controller cannot type: there is no on-screen keyboard. |
| Key field (Keys row) | A starts the capture, which reads a keyboard key or mouse button. |
| Cell row (Mods list entry, Install row, dialog buttons) | A is the row's click; left and right move between its buttons, A presses one. Always this row's own slots, never another row with the same key. |
| Date-time field | Its cell row's buttons: A on the date opens the picker, which is a screen of its own. |
| Label, readout, divider | Not a stop. |

**Focus marker.** The game's own highlight: a settings row is its row selection (the game's row band and bright name, with the description column describing it), a grid row lights its band, a list row (a Mods list entry, an Install row) gets the game's lit row band with its bright top edge, and a control or a button (a switch, a cell, a dialog's answer) a thin frame in that edge color, because its own face would cover a band. Live 2026-10-10: simulating the pointer's enter on a list entry changed nothing (its hover is the framework's own), so the marker is drawn. Moving the mouse (a delta or a new position) or clicking puts the marker away; the next navigation press only brings it back where it was, without acting. When a screen opens after a navigation press (a dialog over the workspace) its focus is drawn at once.

**Where the focus starts.** A dialog with a dismissal (No, Cancel, Close) starts on the dismissal, so A alone never confirms: right, then A. A notice with one answer starts on it, and a long Choose list on its first option. (Live 2026-10-10: a focus that started on the main answer uninstalled a mod with one A. The rule applies to every dialog rather than only to destructive ones; since DANGERENTER1 the framework's destructive prompts also carry the danger flag, see [Behavior](#behavior).) The Mods workspace starts in its left list. A on a list entry opens its page and the focus follows to the page's first control once the new page is built (controls that were not there before A; after 6 frames with none, the page as it is, which covers A on the entry already open; at most 30 frames). B on the page comes back to the list, and while it means that, the game's own Close (its Back and Menu_Close actions) is held so the game's menu does not close under the workspace (`UiNav.HoldsGameClose`, the KEYS3 close hold). Other screens start at their top-left control. When the focused control is rebuilt (a page that redraws after a change), the focus goes to the control nearest where it was.

**Settings pages.** A settings view hooks each row it draws (`UiNav.Hook`): the focus is the row's selection, X is the row's reset, and the reset arrow is passed over (`UiNav.Skip`).

**Known limits.** No text entry with a controller (text fields, a color's hex box and typed numbers need a keyboard). A key field captures keyboard keys and mouse buttons only, and a gamepad button cannot be a keybind. The buttons are fixed (A, B, X, triggers); the game's controller remapping is not followed. A list that draws only its visible rows is navigated over the rows drawn; scroll it with the right stick for more. In a world, whether the game's own menu also reacts to the triggers (its minor-tab actions) is not checked yet.

**Adding a control type.** `UiNavTests.Every_concrete_handle_type_is_a_stop_or_says_why_not` fails until a new handle type has a role in `UiNav.RoleOf` or a reason in `UiNav.NotStops`.

**Bridge.** The dev bridge's [`nav`](bridge-ops-client.md#nav) op reads the focus (`state`), takes one step (`press`), or presses a button on a virtual gamepad through the real device read (`pad`). BRIDGEUI1 adds `hold` (buttons held through `SampleNav` itself, repeat included, no device), `stick` (the right stick) and `focus` (the focus straight onto a cell row or cell, the way into a content pane); live-verified on the test client 2026-10-14.

## Authoring: input, scroll and chrome

Use `Ui.Panel` / the panel `ctx.Menu.Page` supplies. Do not parent extra uGUI under a vanilla canvas and expect Unity's `ScrollRect` / `TMP_InputField` to behave as they do in the editor. The rules below are what this game actually does. Raw measurements are in the internal research notes (2026-09-10). Workspace live status stays on [mods-menu.md](mods-menu.md).

### Shown-input pump (not the hover pump)

Two `Plugin.Frame` pumps exist, and they arm independently.

| Pump | Armed when | Delivers |
|---|---|---|
| Hover (`UiHoverGate`) | at least one **`WhenHovered`** panel is shown | gameplay-block on/off as the pointer crosses the panel |
| Shown-input (`PumpShownInput`) | **any** panel is shown (`shownCount > 0`) | wheel notches onto the hovered list; typed keys into the armed field; click-to-focus |

A set of `WhileShown` panes (the Mods workspace) has a constant block answer, so the **hover pump stays off**. Anything that piggy-backs on `ReadPointer` — typing, custom pointer handling, a wheel apply that lived in that method — then silently does nothing: the panel is on screen, the cursor is free, and no keystroke arrives. **Source-checked.** The live workspace had exactly that failure: search stayed empty after a click and type.

`Ui.Show` is what arms shown-input. Authors do not subscribe to it. Stay on `panel.Input` / `panel.Button` / the supplied page panel so the field is armed and the list is the framework `ScrollRect`. A `Never` panel still arms the pump (it is shown); it does not register the gameplay block, so the camera can still zoom under the list.

### The wheel: the framework is the mover, not uGUI

Unity's `ScrollRect.OnScroll` is **starved** in this game. Blocking `ZoomCamera` stops the camera; it does **not** zero `Mouse.current.scroll` and it does **not** deliver the notch to uGUI. A real-mouse follow-up recorded on the [workspace page](mods-menu.md) saw `deltaY=120` on 13 of 13 samples with uGUI applying about 1 of 13. Why EventSystem starves `OnScroll` is not in this tree (script order is serialized scene data). The fix does not depend on knowing.

Settled design, **source-checked**: the shown-input pump samples `Mouse.current.scroll`, writes one [row-height step](#scroll-step-is-row-height-not-content-height) per new notch onto `content.anchoredPosition`, and sets `ScrollRect.scrollSensitivity = 0` on every pump-driven panel so uGUI cannot also write. Scrollbar **dragging** still goes through uGUI and is reliable — **verified live** (track click moved `listScroll.position` when the wheel, injected, did not). Exactly one writer should move a given list.

Two traps:

- A **held** non-zero `Mouse.current.scroll` reading is not a new notch. Some builds keep the last `WHEEL_DELTA` (120) until the next event. Applying every non-zero sample runs the list away. Edges come from held state, same rule as [Keybinds](#keybinds-edges-come-from-held-state): a 0 → non-zero crossing is a notch; the same 120 held from the previous sample is not.
- **OS-injected (`SendInput`) wheel events do not reach this path.** They also do not reach uGUI `OnScroll`. The injection itself works — with the panel closed it zoomed the game camera (**verified live**). Never judge wheel behavior with OS-injected input; use a real mouse, the `Ui: shown-input ` line, or the bridge's [`ui_input wheel`](bridge-ops-ui.md#ui-input-without-focus-bridgeui1), which hands its notches to this pump's own wheel sample (one notch a sample with a zero between) and so takes the same claims-first path as a real notch (BRIDGEUI1, live-verified on the test client 2026-10-14: `notches -3` gave three pump readings and three steps).

Read the diagnostic, not a call-site. Signatures:

| Line | Meaning |
|---|---|
| `notch=1 applied=pump path=pump` | pass: the pump took this notch |
| `notch=1 applied=none` | dropped notch (hovered list missing, write failed, or content fits) |
| `notch=1 applied=both` | two writers fought (pump and uGUI or a drag in the same sample) |
| `notch=1 applied=ugui` | uGUI or a scrollbar drag moved the list; pump did not write |
| `deltaY` non-zero, `notch=0` | held last reading, not a drop |
| `applied=ugui` with `notch=0` | scrollbar drag (expected) |

Heartbeat once a second while panes are shown; a notch, a typed key or a click that armed a field logs immediately. **Acceptance procedure — not run** on the pump-driven `scrollSensitivity = 0` build: every real-mouse notch should read `applied=pump`; drags `applied=ugui` with `notch=0`; `applied=both` never. The starved-uGUI 13/13 sample predates that mover.

### Scroll step is row height, not content height

A step expressed as a fraction of the whole list moves more rows the more rows you add. **Verified live** on a 51-row list: each real-mouse notch moved ~0.095 of content, about four rows; the same constant would move ~46 rows on a 500-row list. That is the "chunky" wheel.

Settled step, **source-checked**: **2 rows per notch**, from `RowHeight + RowGap` (40 + 8 = 48 units × 2 = 96). `MenuPanelLayout.NextScrollPosition` divides a Windows `±120` by 120, so a double notch (`deltaY=240`) is two steps. Ends clamp. Offline tests pin two rows from 5 to 500; the 96-unit quantum has not been re-measured on a live client after the pump took over.

### Chrome insets are arithmetic

The scrolling viewport, the top chrome and the bottom chrome must sum to the pane. On the Mods sidebar the search field is pinned above the list and Overview / Close sit in a footer band below it. The viewport's bottom inset is `FooterGap` (12) **plus** the painted band (`FooterPadTop` + actions + `FooterPadBottom`), not the band alone — otherwise the last catalog row clips against the footer. Ordinary overlay panels keep bottom inset 0 because `PadBottom` already lives on the content rect; raising both shortens the real viewport relative to `PanelLayout`.

### Restoring scroll after a layout pass

Writing `ScrollRect.verticalNormalizedPosition` right after `Show` uses `content.rect.height` from *before* the canvas update. Unity's setter against empty bounds lands at 0 (the bottom). The symptom looks like "selection resets the list". **Verified live** as a jump to the top, then after a first restore that still used the setter, a jump to the bottom.

Correct approach, **source-checked** (offline): capture the 0–1 value, then write `content.anchoredPosition.y` from `ContentOffsetY` against **this pass's** `contentHeight` / viewport height. Skip the write when content does not yet overflow (`CanApplyNormalized`). The workspace logs `Mods workspace: scroll-preserve pane=… capture=… restore=… applied=offset`. A live pass should read `capture` ≈ `restore` near the value before the rebuild (mid-list ≈ 0.77), not `restore=0`. **Acceptance procedure — not run.**

### Viewport clip is draw *and* hit-test

`RectMask2D` without a raycastable graphic on the same viewport clips what you see and not what you click. **Verified live**: at `listScroll.position = 0.72`, a click on the search field selected sidebar row "Fixture 09" — that row's rect still covered the search coordinates. The viewport `Image` is `raycastTarget = true` with alpha 0.02 (fully transparent is skipped by some raycast paths). `mods_ui rects` must mark rows outside the list viewport `visible: false`.

### Chrome belongs outside the scrolling content

Pin search **above** the list and actions **below** it (`MenuRowPin.Top` / `Content` / `Bottom`). Chrome that lives in the scrolling content moves with the rows, leaves the viewport, and then either vanishes or overlays the real chrome.

Chrome must not be styled like a catalog row. Overview / Refresh and Close Mods that reused row chrome read as extra mods — **user-observed**. Footer actions use a darker inset fill, a rim, and a smaller caption; they never take the selected gold.

Custom pages built through `ctx.Menu.Page` receive the scrolling content only. `PinMenuRows` / `StyleMenuRow` are not plugin API. A search field you add as an ordinary row will scroll away.

### Selection is color, not a glyph

A small prefix glyph (`>` / `›`) is too subtle at real resolutions — **user-observed**. Selected is a gold-brown fill from the panel palette; hover is a lighter gray; chrome is the darker inset. Those three states must stay mutually distinguishable, including selected-hover vs chrome-hover. Selected and unselected catalog captions are the same string so the row does not jump horizontally when the fill is applied. **Source-checked**; color-on-screen is **acceptance procedure — not run**.

### Text fields on this build

Interop (**the IL viewer**, this machine's client): `TMP_InputField` has `ActivateInputField`, `isFocused` and inherited `Selectable.Select`. It has **no** `shouldActivateOnSelect`. `Keyboard` exposes `add_onTextInput(Il2CppSystem.Action<char>)`, not a C# event a collectible Core can subscribe; Bootstrap subscribes it instead ([typed characters](#typed-characters-textinput1)).

A click must therefore: set `EventSystem.current`'s selected object, `Select()`, and `ActivateInputField()`. The shown-input pump does that for `panel.Input` and then samples `Keyboard.current` through `KeyEdgeTracker`. Frame-scoped flags such as `wasPressedThisFrame` can already be false by `Plugin.Frame`; derive edges from held state, as [Keybinds](#keybinds-edges-come-from-held-state) already requires. BlockAllKeys subtracts ProjectM actions, not the Input System device.

**Live-text fields bind `onValueChanged` at creation.** Bootstrap's text-slot `UnityAction` is created once and `AddListener`'d onto whichever TMP event `TryWire` first sees. A later `TryWire` onto a different event recycles that same action and does **not** move the native listener, so a field created on `onEndEdit` and "rewired" to live never fires per keystroke.

| Kind | Event | Why |
|---|---|---|
| Mods sidebar Search (`UsesLiveText`, `ConfigureMenuPanel(..., sidebar: true)`) | `onValueChanged` | filter as you type |
| Settings editors, content panes, custom `panel.Input` | `onEndEdit` | per-keystroke parse made `1.5` / `-3` untypeable (the panel wrote the parsed value back and erased the dot / minus) |

There is no plugin-facing `UsesLiveText` switch. A custom page that needs a live filter is a Core change, not an `OnChanged` rebinding. Live sidebar search toggles existing rows and runs one layout pass for the filter, not one per row.

### Cell rows: positioned cells from a column model (pending LIST2 live acceptance)

`panel.CellRow(CellRowSpec)` / `panel.CellHeader(cells)` (internal, `UiCellRows` extension methods; not in `docs/api.md`) build one full-width band — `RowHeight`, or 53 px for the two-line name + one-liner variant — whose cells (`Image`, `Text`, `Muted`, `Mono`, `Badge`, `Button`, `Chevron`, `Spacer`) are positioned from an explicit column model, `MenuPanelLayout.CellColumns(rowWidth, specs)`: px / fraction / auto widths resolved left to right with `InlineGap` between, right-aligned cells packed at the right edge, a spacer absorbing the remainder, minimum widths enforced, and cells marked `optional` dropped from the right (the mock's Author / Released columns) before any caption is squeezed under its readable floor. This is the generic inline-group behavior two earlier chip rows lacked: gen 40 / gen 42 composed existing controls and failed on geometry, the pinned listing header (UIHDR2) positioned its cells from `HeaderColumns` and held — the cell row generalizes that pattern, and every panel layout pass re-resolves the columns against the live row width. Side badges take the vanilla palette from one place, `MenuPalette` (Server = the menu's crimson, Client = the selection gold, Both = the two halves, Unknown = muted), which the header and LIST2's drawer read too. Button cells are real kit buttons with their own click slot (`OnClick(cellKey, …)`); a click anywhere else on the row raises `OnRowClick`; `SetExpanded` rotates the chevron and `SetSelected` applies the selected fill plus the 3 px gold stripe; `SetCell(key, text, tone, visible)` and `SetImage(key, sprite)` rewrite one cell in place and re-place only that row's cells — never the panel, because the row's height is fixed at creation. `Mono` cells use the body face under a TMP `mspace` run (no game font is monospaced). Listing icons come from `Install/IconCache` (one fetch in flight, 100 ms apart, 512 KB cap, LRU 256, placeholder while loading, textures destroyed on eviction and on Core Uninstall) through `CatalogFetcher.FetchBytesAsync`; the decoder is `UnityIconDecoder` over `ImageConversion.LoadImage`, the same interop call the workspace backdrop uses. `mods_ui widget_read` describes a cell row as `kind: cellrow` / `cellheader` with `key`, `cells [{key, kind, text, tone, visible, rect}]`, `expanded`, `selected` (rects in screen pixels); `widget_click` takes the row key, `rowKey/cellKey`, or a button caption. Four additions from the Install list's column rework (INSTALLCOLS1, source-checked and game-free tested, not live-verified; see [Install list columns](mods-menu.md#install-list-columns)): a `CellWidth.Fill(minPx)` cell takes the row's remainder (instead of the spacers) and never less than its floor, so a row has no dead gap; `CellSpec.DropOrder` decides which optional cell drops first (lowest positive number first; cells left at 0 keep the old rightmost-first rule, after the numbered ones); a two-line cell's `NoteLead` is drawn at the start of its second line in `NoteLeadTone` before the muted note (readback `noteLead`); and `IUiCellHeader.SetSorted(key, ascending)` marks the sorted column ▲ (U+25B2, `CellLayout.SortIndicatorAscending`) or ▼ (readback `sortAscending`). **Source-checked and unit-tested** (`CellRowTests`, `IconCacheTests`); this is promoted to a kit capability only once LIST2's live Install list passes acceptance — **pending LIST2 live acceptance**.

### Detail-page blocks: a two-column head, tones, an icon link, a marked row, a prompt (KIT1, pending live acceptance)

Five composed blocks sit on top of the cell row, as internal `UiKitBlocks` extension methods (not in `docs/api.md`); each one degrades to state-only on a runtime that cannot draw it, the way every kit handle does.

- **`panel.HeadBlock(HeadSpec, rowWidth = 0)`** is the mock's `.head` grid: three cell rows, plus a fourth when there is a warning. Left column, top to bottom — the title at `HeadBlockLayout.TitleFontSize` (34 px, an explicit `CellSpec.FontSizeOverride` over the kind's 20), the meta line as separate `Mono` / `Text` cells plus an inline toggle-button cell, and a `CellWrap.Wrap` one-liner. Right column, on the same three rows and right-aligned so all three end on one edge — the colored status word behind a muted `Status:`, the action button, and the side badges. `HeadBlockLayout.Columns` sizes the right column to the wider of its own ask and `RightColumnMinWidth` (250 px, the mock's), capped at the same 45 % `HeaderColumns` puts on a pinned header's action column, so the title keeps the larger half at every fixture. A wrapping cell needs a row tall enough for its lines — a cell row's height never changes after creation — so `CellRowSpec` takes an explicit height and `HeadBlockLayout.WrappedRowHeight(WrappedLines(...))` supplies it; `CellVAlign.Top` keeps the description and the badges beside it at the top of that grown row instead of floating in its middle. `SetStatus` / `SetAction` / `SetToggle` rewrite one cell in place, so a state change does not rebuild the page and lose the scroll. The meta line's `·` separators live in the text of the value each one introduces (`HeadBlockLayout.MetaCellText`), never in a cell of their own: `CellLayout.Resolve` drops optional cells from the *right*, so a separator cell would outlive the value beside it for exactly one drop and leave a dangling dot on a narrow pane. The runtime splits a leading separator off so it is drawn muted and outside a mono cell's `mspace` run (`TrySplitMetaSeparator`) — the value is tabular, the dot between values is not. The inline toggle is sized to its caption plus a button's padding (`HeadBlockLayout.ToggleWidth`, floor `MinToggleWidth`), not to a cell row's 200 px action-button column: live gen 1 drew `Like · 0` in a 170 px box. The right column stops one **scrollbar band** short of the row's own right edge (`HeadBlockLayout.RightColumnInset` = `PanelLayout.ScrollbarWidth + ScrollbarInset`, carried by every head row as `CellRowSpec.RightInset` and applied by `CellLayout.Resolve`): a pane row is exactly as wide as its clipped viewport, so a cell packed flush against the row edge is drawn on the boundary of the clip with the bar immediately beyond it — live gen 1 read `status-word` and `badge-0` ending at x 2732 on a 1484..2732 row and the player saw the wider **Uninstall** button cropped and the `Server` badge half under the bar (HEAD2). `HeadBlockLayout.ContentWidth` / `ContentRight` are that edge, every column measurement here is taken from them, and the inset is reserved by the model rather than taken out of a squeezed column, so the action button keeps the mock's 200 px. `MenuPanelLayout.CellColumns(rowWidth, CellRowSpec)` is the overload that honors it — resolving a row's *cells* alone places its right column flush again, which is why the runtime's placement pass, the readback's rects and the geometry tests all pass the row.
- **`panel.Label(text, LabelTone)`** is a colored caption: `Normal | Muted | Ok | Pending | Orange | Warn | Danger`, resolved by `MenuPalette.LabelColor` and mapped onto the cell tones an Install row uses (`LabelTones.ToCellTone`) so a badge and a page label cannot drift. The three state tones are the mock's **word** colors (`--oktx` / `--pendtx` / `--orangetx`), not the badge rims: a rim is authored to be read against its own fill and comes out near 4.1:1 on the pane, under the 4.5 a caption needs. `MenuPalette.ReadingTones` is the set that clears 4.5 and is asserted to; `Warn` and `Danger` are deliberately outside it — `Danger` is the mock's red, about 2.9 on the pane, used for exactly one thing, the bold `(!)` in front of a warning line whose sentence is drawn in `Orange` and carries the meaning. Nothing here is said in color alone: every tone is named in the readback.
- **`panel.IconLink(glyph, tooltip, onClick, url)`** is a square icon-only button, `IconLinkSize` (30 px) wide. It asks to be placed in the **previous** row's right-hand column (`UnityPanel.AttachRightControl`, which sets the row's control and then calls `Relayout` — re-placing one row in place would advance the layout cursor twice and put the next row on top of it) and falls back to its own right-aligned row when there is nothing to attach to. `MenuPanelLayout.IconLinkGlyph` is `»` (U+00BB, Latin-1, so the vanilla TMP font has it) until a link sprite exists; `SetSprite` is that slot. Readback: `{kind: link, glyph, tooltip, url}`.
- **`panel.Button(caption, marker, dot)`** is a sidebar row whose marker (red, left) and state dot (`RowDotSize` 9 px, the tone's color, right) are drawn parts rather than characters in the caption — `MenuRowStyle.MarkedCaption` clips the name to what they leave. Before this the caption was `(!) HuntsAndGates ●`, so a search match, a bridge click and a reader all saw the glyphs as part of the name. Readback: `{marker, dot}` on the row, and `entries[].marker` / `entries[].dot` in `mods_ui status`. The **Mods sidebar no longer passes a dot** (HEAD2, requested 2026-09-13: "lets not use the lights (yellow in this case) on the listing in the menu on the left") — `ModsMenu.SidebarDotOf` answers null for every row, so `dot` reads back empty and the caption gets the width a dot used to cost; the parameter stays for other callers and the `(!)` marker stays, because it is a warning and not a state light. Two-line sidebar rows remain the MODS1 gap.
- **`panel.Prompt(tag, heading, body, yesCaption, yesTone, noCaption)`** is the SERVERINST2 y/n shape as one block: a heading row, a muted body row, and one cell row with No and Yes right-aligned (Yes outermost, the mock's `justify-content:flex-end`). The caller keys its readback rows off `tag`, so the three panes that hand-built this keep their own key. Its buttons row carries `HeadBlockLayout.RightColumnInset` as its `CellRowSpec.RightInset` (CONSENT2 item 5, INSTALLUX1 flagged it): without it the pair is packed flush against the row's right edge, which is the pane clip, and `Yes` — the wider caption — is drawn under the scrollbar band with its right side cut off, exactly as the head block's **Uninstall** was. **Source-checked and unit-tested** (`PromptBlockInsetTests` at both head-block fixtures), not yet live-verified.

- **`Ui.Dialog(heading, body, yes, yesTone, no, tag, kind, key, owner, pluginId, rowKey, yesKey, noKey)`** (INSTALLUX2 item 1) is that prompt as a **modal dialog** instead of a block inside the caller's pane: its own `Ui.Panel`, no pane title, the runtime's dialog chrome. Since DIALOGS1 it is drawn with the shared [dialog layout](#dialogs) (the pair centered, No left, Yes right, no right inset) and hands back a `UiPromptBlock` with the same heading, body, buttons and keys. It was requested on the INSTALLUX1 build — "I click install on and it expands like the old behavior asking me if I want to stall like this instead of popping up a dialogue" — so every confirmation the Mods workspace asks for (the Install tab's install / uninstall / reactivate / cancel-install / refusal boxes and the mod page's three y/n prompts) is one of these, and nothing about a confirmation is drawn in a row any more.
  - **Chrome** is `IUiRuntime.DialogChrome(panel)` (`UnityPanel.MakeDialog`): the frame image repainted to the game's popup frame when its owned copy is ready, else `DialogFrameColors.Frame` — the Mods palette's pane ground at alpha 1, so nothing reads through the box (moved from the removed consent panel's chrome, 2026-10-05) — with a full-canvas, raycast-blocking backdrop child (`dialog-backdrop`, the game's popup blocker color) as the first sibling of the panel's own overlay canvas, so it is drawn behind the frame and hit-tested after it.
  - **Placement** is centered, not flowed: the layout pass and the re-flow both take `PanelOffset.Centre` for a dialog instead of `PanelFlow.Offset`, because a dialog is never one of a ranked set — that ranking is what moved the consent prompt 332 units left the moment the workspace's panes joined the shown set (CONSENT2 item 1). Sizing stays the runtime's, so the box is content-sized (`PanelLayout.PanelHeightFor`, up to `ScreenFraction`) and the bar only appears when the content does not fit.
  - **Reveal after the layout pass**: `MakeDialog` puts the overlay's `CanvasGroup` at alpha 0 (never `SetActive`, so the panel captures input and is Escape-able throughout) and `RunLayout` raises it to 1 as the last thing it does, so the first frame the player can see is the placed, sized one.
  - **One at a time, and Escape = No.** `UiDialog.Current` is the open dialog; a second `Open` closes the first without answering it. `Ui.OnBootstrapKey` calls `UiDialog.NotifyEscape()` first, so Escape is a decision the owner is told about rather than a box that silently leaves the screen (the FUI29 failure). When it answered a dialog, that press hides nothing else (KEYS3 polish, 2026-09-30): the panels behind the dialog stay, and the next Escape is the ordinary sweep. While a key capture waits (`Keybinds.BeginCapture`, `BeginRebind`), Escape cancels the capture and the sweep does not run (`Keybinds.EscapeBelongsToCapture`). In a world, a vanilla menu that hosts the panel (the Mods workspace in the FullscreenMenu) is kept open too: while a capture or a dialog owns Escape, the menu-hotkey guard context at order 498 also takes `Back` and `Menu_Close` ahead of `FullscreenMenuMapper` (502), where the order-599 block is too late (`MenuHotkeyGuard.CloseHeld`). The order (capture, dialog, search, close) and the hold are one object for every host, `WorkspaceInputPolicy` (HOSTS1; [mods-menu.md](mods-menu.md#one-layout-for-both-hosts)).
  - **Handles.** `UiPromptBlock.Build` takes the caller's `rowKey`, `yesKey` and `noKey`, so a pane that has already published `prompt:<key>/promptYes` / `promptNo` (and `promptClose` for a refusal, which has one button on the No seam) keeps those `widget_click` handles now that the buttons live on another panel. Readback: `UiDialog.Describe()` is `dialog {open, key, kind}`, carried by `ModsMenu.DescribeWorkspace` into `mods_ui status`. **Source-checked and unit-tested** (`UiDialogTests`), not yet live-verified.

**`Ui.HidePaneTitle(panel)`** drops a pane's own centered title band and gives it to the content, for a page that draws its own name — the same three writes `ConfigureMenu` makes for a `MenuPaneMode.FullWidth` pane (LIST3 item 8) without changing the geometry, so the mod page stays a `Split` detail pane beside the sidebar. A pane asks once while it is built; the panes that want a title (the overview, a custom page) do not ask. Live gen 1 is why: the mod page drew a mod's name twice, because `IsRedundantPaneTitle` drops a body *label* equal to the pane title and the head block's title is a cell row.

`CellTone` gained `Danger` (the red marker) and `Orange` (the mock's warning-line `--orangetx`, lighter than `Warn` so a whole sentence stays readable); `CellWrap` gained `Wrap`; `CellSpec` gained `FontSizeOverride` and `VAlign`; `CellRowSpec` gained `RightInset` (HEAD2, zero on every row but a head block's; INSTALLUX1 item 4 adopted it on the Install tab too — the column header, every listing row, the confirmation's `No` / `Yes` row and the pending bar's expanded rows all pass `HeadBlockLayout.RightColumnInset`, because the same clip boundary was cropping the Install-column button and the y/n buttons live at 3840×1080: "buttons on the right are cropped". `UiCellRows.CellHeader` gained a `rightInset` parameter for that, and `IUiCellRow` exposes `RightInset` so a geometry test measures the placed row rather than a spec it rebuilt). **Source-checked and unit-tested** (`HeadBlockTests` at the 1248 px in-game detail pane and the 1600×1024 fallback, `ModPageTests`). The head block itself is **verified live** at 3840×1080 (client gen 1, capture of 2026-09-13): the 34 px title, the meta line with the inline Like, the one-liner, and on the right `Status: Installed` in green with the `Server` badge. The three fixes that capture asked for — the dropped pane title, the left-aligned body and the caption-sized toggle — are **source-checked and unit-tested, not yet live-verified**; acceptance is the next client generation. HEAD2's two fixes from the same client — the right column pulled one scrollbar band inside the row edge and the sidebar's state dots dropped — are **source-checked and unit-tested** (`HeadBlockTests.The_right_column_stops_one_scrollbar_band_inside_the_row_edge` at both fixtures, `ModPageTests.No_sidebar_row_draws_a_state_dot_and_the_warning_marker_still_does`), **not yet live-verified**. The Install tab's adoption of the same inset is **source-checked and unit-tested** (`InstallListViewTests.Every_install_row_kind_ends_one_scrollbar_band_inside_the_row_edge` at 1248 px and 1108 px), not yet live-verified.

**Linked text and section bands (JOINLINKS1; source-checked, built (Debug and Release) and game-free tested, not live-verified).** `CellSpec.Linkify` on a `Text` cell (not `TwoLine`) or a `ScrollText` cell (its `Body` lines) draws the web links in its untrusted text pale blue (`MenuPalette.LinkText`, `#9fc7e8`) and underlined. The kit builds the markup itself (`UiLinks.Markup`): every character outside a link still goes through `UiRichText.Literal`, and only the kit's own `<link="n"><color=…><u>` around each escaped link is live, so nothing the text contains becomes markup. Detection, normalization (scheme-less links open as `https://`), trimming and the bidi/control/length refusals are `UiLinks.Find` ([join-flow.md](join-flow.md#links-in-the-message) lists the rules); links are found again on every write. A linked cell takes clicks: bind `OnClick(cellKey, …)` and call `IUiCellRow.LinkAt(cellKey)` in the handler for the link under the pointer (the runtime asks TMP, `TMP_TextUtilities.FindIntersectingLink(text, pointer, null)` on the overlay canvas; a scroll block adds the links of the lines above), or -1. Opening anything is the caller's job (the join screens confirm first). Readback: a linked cell's `links [{index, text, url}]`. `CellRowSpec.Section` (`CellSection.Red` / `Green`) with `SectionEdge` (`First`, `Last`) draws consecutive rows as one tinted block: the row fill is the section's, the fill runs up through the row gap above a non-first row, and a 1 px rim goes round the block (`MenuPalette.SectionBand`); build such rows with `CellRowSpec.InSection(section, edge)`, which pads the cells `SectionPad` (12 px) from both rims. New tones `SectionRedHead`/`SectionRedNote`/`SectionGreenHead`/`SectionGreenNote` color a section's heading. Readback: `section`, `sectionEdge` on the row.

### Bordered tiles (TILES1)

`CellTiles.Pair(headKey, noteKey, left, right, firstTile, firstLine)` builds one line of two bordered tiles as two cell rows: the head row (each tile's link, `CellSkin.Link`, or key-cap button, `TileSpec.Button`) and the note row (each tile's muted note, one line, ellipsized at the tile). Every cell of a tile carries its number (`CellSpec.Tile`), and the row says which edges it draws (`CellRowSpec.TileEdge`: `First` on the head row, `Last` on the note row, which also has `GapAbove = 0`). The runtime draws, behind each tile's cells, a fill and a 1 px rim from the left of its first cell to the right of its last (`CellTiles.Spans`), the top rim on a first row, the bottom one on a last row, and a non-first row's box covers the gap above it, so head and note read as one box. The colors are existing workspace tones (`MenuPalette.TileBand`: the row hover fill and the red section's rim); there is no new color. The cells are laid out so both tiles of a line are exactly `(row width − gap) / 2` wide whatever their captions, and the two rows' boxes match: head `[pad][link W][fill]` twice with W the wider caption's ask, note `[pad][note fill][pad]` twice. A pad is an empty `Muted` cell `CellTiles.Pad` wide, because a kit `Spacer` takes no width of its own (its `Px` ask is ignored; it only shares a row's remainder). Readback: `tile` on a tile cell, `tileEdge` on the row. **Source-checked and unit-tested** (`SupportLandingAndTileTests`), not live-verified; the Mods tab's landing page is the first user.

**Known gap:** `mods_ui rects` finds a row by matching a TMP caption against the readback `label`, and a cell row's label is its *key*, so a head block reads back as `MissingRect` there. Its geometry is in `widget_read` instead, which carries a `rect` per cell and for the row. Teaching `rects` about cell rows belongs to that op's owner.

### `visibleRows` is the rendered subset

`mods_ui status.visibleRows` is how many catalog rows currently intersect the list viewport, not how many match the search. Reporting every match makes the number useless for the overflow diagnosis it exists for — **verified live** as `visibleRows = 51` with roughly 14 rows on screen. Chrome (search / Overview / Close) is pinned outside the viewport and is not counted. `totalRows` is the catalog size.

## Settings panel

`F10` (rebindable) opens the [Mods workspace](mods-menu.md). Selecting a mod exposes its registered pages, settings and keybinds. `SettingsPanel.Open()` remains available for the full settings list. Rebinding is a press-a-key flow; Escape cancels and every keybind is suppressed until it lands. Other mods' entries are read-only unless `EditOtherModsSettings` is on.

The settings kit aligns rows **center** unless a panel sets `ContentAlignment` to `left`. Center is the default for the Mods overview and deny panels. Left (Configuration and the Install listing) puts the label on the left with a consistent inset, the control (toggle, field, button) on the right of the same row, secondary text under the label in a dimmer tone, and section headings larger/bolder with a separator and extra top spacing. That write is the TMP horizontal anchor only (`HorizontalAlignmentOptions.Left` / `Center`); character spacing, word spacing, and Flush/Justified are left as the donor (or TMP default) set them. `mods_ui rects` reports `align` per row so a live read can confirm it. **Source-checked.** On-screen left layout is **acceptance procedure — not run**.

Settings editors stay on `onEndEdit`; only the Mods sidebar Search is live-text. See [Text fields](#text-fields-on-this-build).

### Settings kit controls (SETTINGS1, source-checked and game-free tested, not live-verified)

`IUiPanel` publishes five more controls. Each is a row with the caption in the label column and the control in the control column, like `Toggle` and `Input`. Before SETTINGS1 the runtime drew these as shells whose clicks and edits reached nothing; each now wires its raw events to a Bootstrap slot and its handle's state machine, and a control with several clickable parts (arrows, steppers) holds one extra slot per part (`IRuntimePartsWidget`, released with the handle). Each part is wired to the click of its own object (the arrow or stepper button), never to the row.

| Factory → handle | Drawn as | Commits |
|---|---|---|
| `Dropdown(text, values, value, labels = null)` → `IUiDropdown` | `<` value `>` in one framed box (ASCII arrows so every game font draws them) | Each arrow, or a click on the value (next), picks and commits. Wraps at the ends. A value outside the list is kept and shown `value (not in list)`. |
| `Number(text, value, min, max, step = 1, decimals = 0, units = "")` → `IUiNumber` | `-` field `+` | Typed text on Enter/focus loss, clamped to `min..max` and snapped to `step` from `min`; a stepper click commits one step. |
| `SliderField(text, min, max, step, value)` → `IUiSliderField` | slider (62 % of the control column) and an exact field | Slider samples preview; one commit once the slider has rested 0.35 s (`UiSliderEntryHandle.SettleSeconds`) — Unity raises no release event, so "rested" is the release, and a pause longer than that mid-drag commits the intermediate value. A commit still pending when the handle is disposed is flushed first. Typed value on Enter/focus loss. |
| `ColorField(text, hex, alpha = false)` → `IUiColorField` | swatch and hex field | Typed `#RRGGBB` (or `#RRGGBBAA`) on Enter/focus loss. The swatch repaints on the next build. |
| `KeyField(text, pluginId, bindId)` → `IUiKeyField` | the chord in a framed button | A click starts the capture (the same flow as the settings pane's rebind button); Escape cancels. |

`OnChanged` on each fires for a commit only — never for a raw drag sample, a keystroke, or a programmatic `Value` set — and the plugin's handler is held by the handle rather than a slot, so the raw event and the commit never share a slot kind. The mod page's settings sections are built from these ([mod-settings.md](mod-settings.md)).

Each composite control is **one container** the width of the control column, and that container is the row's control: a menu pane's relayout stretches a row's control across the column, which would otherwise stretch the number field over its own steppers or the slider over its field. The parts sit inside the container by anchors (steppers and arrows on its edges, the field or value text between them, the slider on the left 62 %). Not yet seen live: arrow and stepper hit targets at 1600×1024 and 3840×1080, and whether a cloned slider donor keeps its handle travel inside the narrower anchor.

**The editor controls are real and operable.** They were not: `AddToggle` / `AddSlider` / `AddInput` each added a bare component and a caption, and a Unity control is not the component — it is the component **plus the objects it draws through** (evidence 176). A `Toggle` with no `targetGraphic` has no click target and no checkmark; a `Slider` with no `fillRect` / `handleRect` holds a value and draws nothing to grab; a `TMP_InputField` with no `textComponent` silently accepts nothing and, with no background, cannot even become the EventSystem's selected object. Every instrument the framework had reported success, because every one of them was a check on the component.

What the runtime now requires, asserted at build time and re-asserted on anything cloned:

| Control | Minimum for it to work |
|---|---|
| `Toggle` | `targetGraphic` (a raycastable `Image` — the click target) and `graphic` (the checkmark whose alpha `PlayEffect` drives) |
| `Slider` | `fillRect` and `handleRect`, each in a parent inset by half a handle so travel stops where the track does, plus a raycastable track and an explicit `direction` |
| `TMP_InputField` | `textComponent`, `textViewport`, and a `targetGraphic` background so a click can select it |

Two build-order facts matter as much as the references. **`OnEnable` has to run after the references are set** — `Toggle` applies its checkmark alpha there and `TMP_InputField` creates its caret there, and a component added to an already-active GameObject runs `OnEnable` immediately, with everything still null; controls are built inactive and activated at the end. **`Instantiate` remaps only what is inside the copied hierarchy** — a clone keeps the game's *persistent*, inspector-wired `UnityEvent` calls, which survive `RemoveAllListeners` and still point at the game's own objects, so a cloned checkbox would drive the vanilla setting it came from; they are switched off one at a time with `SetPersistentListenerState(i, Off)` on every clone, the `SimpleStunButton` included. A cloned toggle can also arrive still enrolled in a vanilla `ToggleGroup`.

Whether a live vanilla donor exists while the player is in the world is answered by a log line per control type, not by a guess. A control that fails its operability check is **destroyed** and the row reverts to a full-width label — which is what a read-only row has always been — so "is this row indented?" and "does this row have a control?" cannot answer differently. A label and its control come out of one call (`PanelLayout.Columns`).

#### Typed characters (TEXTINPUT1)

**Status: source-checked and game-free tested (`CaretLayoutAndTextInputTests`), not live-verified. The Bootstrap half needs a client restart to exist.**

| Source | Characters | When |
|---|---|---|
| Bootstrap's `TextInputQueue` (`Keyboard.onTextInput`, subscribed once per keyboard device by `UiInputContext.Update`) | What the OS typed: the player's layout, Shift and AltGr, dead keys and accents, every symbol | Bootstrap reports `Capability` 1 and the queue has delivered text once in this Core generation |
| The physical-key map (`UiTypedKeys`) | US layout only: letters, digits, the Shift row over the digits, the punctuation block with its shifted forms, Space, the numpad (Num Lock assumed on) | An older Bootstrap, or before the queue's first delivery |

Editing keys always come from the physical keys: Backspace, Delete, arrows, Home/End, Enter and the Ctrl shortcuts (A, C, X, V, and Z and Y for [undo and redo](#undo-and-redo-textundo1)). The queue's control characters (Enter, Tab, Backspace, Ctrl+letter) are dropped. In a frame where the queue delivered text, the physical map's inserts are dropped, so no keystroke types twice; from then on the queue alone types. Core drains the queue every frame and discards anything older than the previous frame, so text typed while no field had focus (walking, chat) never lands in a field. A converted delegate over a Core method would pin the collectible generation, so only Bootstrap's own method is handed to IL2CPP. Not supported: IME composition (`onIMECompositionChange`), Tab as a character or focus move, Num Lock state. Enter starts a line in a text area and commits a single-line field; the game has no multi-line field to copy.

A field's own validation still applies after the mapping: the Prompt dialog's validator, a settings row's bounds and `MaxLength`. TEXTFILTER1 adds a per-field typing filter on every path (OS text, the US map, paste, the bridge's injected input): number boxes, slider value boxes and numbers shown in plain fields take digits, one leading sign and (with decimals) one point; color boxes take one leading `#` and hex digits, at most 7 (9 with alpha). A filtered character is dropped before the edit. The characters `<` and `>` are now typeable everywhere; every place a typed value is drawn is plain text ([text limits](mod-settings.md#text-settings-limits-and-plain-text)).

Readback: `mods_ui status` → `focus.textInput` `{capability, state (subscribed|no-keyboard|not-installed|failed:<type>|absent), received, proven, source (os-text|physical-us-map)}`.

#### Undo and redo (TEXTUNDO1)

**Status: source-checked and game-free tested (`TextUndoTests`), not live-verified.** Core only; no Bootstrap change.

Every framework text field has its own history: single-line fields, text areas, the expanded editor, number and color boxes, the settings search, and dialog prompts. **Ctrl+Z** undoes; **Ctrl+Y** and **Ctrl+Shift+Z** redo. A held chord repeats like a held Backspace. The chords are decided by `UiTypedKeys.Decide` (Ctrl without Alt or Meta plus a letter) from the physical keys on both input paths; the OS text queue's matching control characters (0x1A, 0x19) are dropped at the drain, so nothing is typed. While a framework field has owned focus, no `ctx.Keybinds` bind fires on a key the field takes itself (`UiTypedKeys.FieldTakes`): a printable key with or without Shift or Ctrl (Ctrl+Z, Ctrl+Y and every other Ctrl+letter included), Ctrl+Alt (AltGr), Backspace, Delete, the arrows, Home and End. Binds on other keys still fire: F10, function keys, Alt+letter and the mouse's Back and Forward. `keybind_press` reports `suppressed: a framework text field has focus and takes this key`. The workspace's gameplay block keeps the keys from the game.

| Rule | Behavior |
|---|---|
| Typing | Consecutive characters merge into one step. A whitespace or punctuation character joins the word before it; the next word character starts a new step. A pause of 1 s or more, a caret move, a selection, or an undo or redo also ends the step. Enter's line break in a text area counts as whitespace. |
| Backspace, Delete | A run of either merges into one step under the same pause and caret rules. Deleting a selection is its own step. |
| Paste, cut | Always their own step. |
| Restore | Undo puts back the text and the caret and selection from before the step. Redo restores the text and caret after it. A new edit after an undo drops the redo steps. |
| Bounds | At most 100 steps and 65,536 characters (removed plus inserted text, undo and redo together) per field. The oldest steps go first. A single edit over the character bound empties the history. |
| Lifetime | The history clears when the field loses focus (committed or not), and when its text changes from outside: a commit's trim, a sync, a reset or a server echo. The expanded editor's history ends with the editor, so Cancel discards it. |
| Filters and limits | Every step is recorded after the field's character filter and limit have applied, so every text undo can restore is one the field held: a typed or pasted state, or the value it started with. The limit is checked again on restore, because it can shrink while the field has focus. A restored text over the current limit is refused, and the step is kept. The filter is not checked again, so a starting value the filter would not accept as typed (a stored `1e3` in a number box) is still restored. |
| Read-only | Read-only fields ignore undo and redo. |

The history is `UiTextUndoHistory` (game-free). The pump records each owned edit in `ApplyFieldCommand`.

Readback: every bridge `field` readback has `undo {depth, redoDepth, totalChars, lastKind (typing|backspace|delete|delete-selection|paste|cut|other|none), maxEntries, maxChars, evicted, refused, lastClear (blur|external|mismatch|""), readOnly, undoApplied, redoApplied, ignoredReadOnly}` ([dev loop](bridge-ops-ui.md#mods_ui-text-field-tests-texttest1)).

**Read-only fields draw no caret** (TEXTUNDO1 polish). The caret is still placed, so the arrow keys still move the view, and Ctrl+A and Ctrl+C still copy. The framework draws no selection highlight in any field. `focus.caret` reports `caretHidden` and `caretAlpha`.

#### Caret geometry (TEXTAREA1, TEXTAREA2)

**Status: source-checked and game-free tested (`CaretLayoutAndTextInputTests`), not live-verified.** The owned caret is placed by `UiCaretLayout`: the text is split into visual lines (each line break, and TMP-style greedy word wrap at the text rect's width less the component's `margin`), the caret's line is found, x is that line's prefix width placed by the horizontal alignment (left, centered number and color boxes, right), and y is the line index times the line advance (`GetPreferredValues("A\nA").y − GetPreferredValues("A").y`) placed by the vertical alignment (a text area is top-aligned, a single-line field middle-aligned). Widths are differences against a visible end mark, so trailing spaces count and margins cancel. A caret on a soft-wrap boundary is drawn at the start of the next line; the bar is never taller than one line.

Before this the caret sat at the text rect's vertical center and at the whole prefix's width from the rect's left edge: in a text area "adsf" on line 1 drew the bar on line 2, Enter left it at the end of the old line, and a centered number box or a field with a text inset put it beside the text.

**TEXTAREA2 (source-checked and game-free tested, not live-verified).** A left-aligned field (every text field and text area) takes the caret from TMP's own last layout first: `textInfo.lineInfo` gives each drawn line's first character and its ascender and descender, so the line and its height are TMP's (wrapping included) and the text area's scroll is already in the text rect's position. x is the left inset plus the width of the caret's line up to the caret. The lines are used only when `textInfo.characterCount` matches the text (plus the zero-width space `TMP_InputField` may append); on the frame of an edit, before TMP has laid the new text out, the previous placement stays and nothing is cached. Widths are measured on a hidden `caret-measure` label that copies the field's font and spacing, never on the field's own text component (measuring there re-parses its text). A centered number or color box keeps the measured layout above. The layout is cached until text, caret or rect change, and an idle focused field writes nothing to the bar's transform (`caretWrites` in `mods_ui perf`). When TMP has not scrolled a text area to the caret, the framework moves the text by the overflow (`scrollWrites`). The 2026-10-04 live readback that motivated this (2,895 characters, `lineCount` 6 against `tmpLineCount` 94, the bar at the field's top) came from the measured layout stopping at the visible lines while TMP had scrolled the text to its end.

Readback: `mods_ui status` → `focus.caret` `{focused, caption, multiline, textLength, caret, characterLimit, line, column, lineCount, beyondView, wrap, hAlign, vAlign, richText, tmpLineCount, caretRect, fieldRect, textBounds, checks {insideField, endOnLastLine, endAtRightEdge}}`; rects are y-down client pixels. With the caret at the end of the text, `endOnLastLine` compares the bar with TMP's rendered bounds, and `endAtRightEdge` is meaningful when the last line is also the widest. `tmpLineCount` is TMP's own count; a disagreement with `lineCount` means TMP wrapped differently (a trailing line break may also count differently).

### Stage 2 controls (SETTINGS1, source-checked and game-free tested, not live-verified)

| Factory → handle | Drawn as | Commits |
|---|---|---|
| `TextArea(text, initial = "", lines = 3)` → `IUiInput` | a framed field `lines` tall (2–12), TMP `MultiLineNewline`, word-wrapped from the top | Enter inserts a line (the framework's typed-key pump, `UiTextEdit.Newline`, since armed fields are `readOnly` to TMP); the text commits once, on focus loss (`onEndEdit`). The row grows to the field's height. |
| `Radio(text, values, value, labels = null)` → `IUiRadio` | two to four options of at most 14 characters: one segmented row, the chosen option in the accent (`MenuPalette.Gold`) with bright text, the others muted, labels vertically centered; otherwise one option per line with the kit's check box in front. On a mod's settings page the game look applies instead ([control gallery](mod-settings.md#control-gallery)) | A click on an option picks and commits it; the same option again does nothing. |
| `MultiSelect(text, values, selected, labels = null)` → `IUiMultiSelect` | one option per line with the kit's check box (framed box, filled mark) in front; an unchecked option's label is muted | Each click flips one option and commits the whole selection. |
| `Info(text, Func<string> value, refreshSeconds = 1)` → `IUiInfo` | the caption and the value in the control column | Read-only. Re-read every `refreshSeconds` (clamped to 0.25–60; 0 reads once) through `MainThread.PostDelayed` with the panel's owner, only while visible; a throwing function is logged once and the last value stays; disposing the handle (or its panel) stops the clock. |
| `Divider()` → `IUiLabel` | a 1 px rule in a 14 px row | — |
| `ConfirmButton(text, question)` / `ConfirmButton(text, question, body)` → `IUiButton` | an ordinary button | The click opens the kit [dialog](#dialogs) (`question` as its title, the optional muted `body`, **No** left and **text** right; Escape is No, Enter is Yes); Yes runs the `OnClick` handler. The dialog closes when the button is disposed, and a Yes that outlives the button runs nothing. Needs a Progenitor panel. |

Each option of a radio or multi-select is a part (`IRuntimePartsWidget`, key `option-<i>`) wired to its own slot, like the selector's arrows. **`IUiHandle.Interactable`** (default `true`) grays a row in place: the runtime puts a `CanvasGroup` on the row object (alpha 0.45, `interactable` off, so its selectables take no input), and the button and options handles also ignore a click while it is false. Nothing is rebuilt and the layout does not move. Every new `IUiRuntime` member is defaulted (a text area falls back to an input, an info line to a fact row, a divider to an empty label, options to a disabled handle), so an existing runtime or fake compiles unchanged.

**Wide dialogs (WIDEDIALOG1, 2026-10-06; source-checked and game-free tested, not live-verified).** `DialogContent.FrameWidth` asks for a wider frame than the ordinary 640 px; the runtime gives it at most 90% of the canvas and never less than 640 (`DialogLayout.FrameWidthFor`), and the content is the frame less 80 px (`DialogLayout.ContentRowWidth(frameWidth)`; 640 → 560 as before). The Players joining panel uses `DialogLayout.WideFrameWidth` (880 → 800 px content).

**Radio lists share one set of metrics and take real clicks (RADIOCLICK1, 2026-10-06; source-checked and game-free tested, not live-verified).** `UiRadioList` (the cell-row radio list of dialogs such as Players joining) and the settings grid's radio lines use the same numbers: a 24 px circle column (at least the glyph's 20 px, `UiRadioList.MarkGlyphNeed`; the circle is a `CellWrap.Glyph` cell, drawn whole rather than ellipsized, RADIOCLICK2; the settings grid's circle has the same 24 px box, centered and drawn whole, with the font's drawable form from the start, RADIOCLICK3), the label and its explanation both starting 32 px in (`UiRadioList.LabelInset`), a 22 px label line, and the explanation 2 px under it, then 10 px to the next option. The explanation row is `CellRowSpec.FitWrap`: the runtime sizes it to the lines TMP draws (the game-free estimate, 17 px a line, at most 4, over-counted a one-line explanation as two). The cell-row list's rows are `CellRowSpec.Compact` (the panel reserves their own height instead of its 40 px row floor) with `CellRowSpec.GapAbove` (their own gap instead of the 8 px `PanelLayout.RowGap`). `IUiCellRow.OnRowClick` on a plain or band row (built without a click target, `CellRowSpec.RowClickAtBuild`) now gives it one, without a tint (`IRuntimeCellRow.EnsureRowClick`); before, the handler sat on a slot no pointer could reach. Test a click the way a player makes it with `mods_ui click_at {row}` ([api.md](api.md)).

**The mod page's settings grid** is a kit seam, not published: rows added while `SetSettingsGrid(panel, true)` is on are placed on `ModSettingsGrid`'s columns (caption column, a fixed-width control column at the same x on every row, a reset gutter), a row's help line is drawn under its caption inside the caption column (`SetRowHelp`; the row grows to hold it), the reset link sits in the row's gutter (`AddGutterLink`), a toggle is drawn as a switch (track and knob) at the right of the control column, a read-only or info value is right-aligned, every single-line control is 34 px tall with its caption centered, and a button is a named row with a normal-size button in the control column (`AddGridButton`). Row rhythm, slots and heights: `ModSettingsGrid`. Geometry and rules: [mod-settings.md](mod-settings.md#page-design-stage-2).

**Cell rows** gained a switch cell (`CellKind.Switch`: a track and a knob, not clickable), a band row (`CellRowSpec.Band`: an opaque fill in the existing `MenuPalette.RowHover`, no row click) and bottom alignment (`CellVAlign.Bottom`), for the mod page's status bar and section headings.

**Stage 3 (SETTINGS1 / MODLIST1, source-checked and game-free tested, not live-verified)** added four kit pieces, none published to mods:

- `CellSpec.Clickable` on a `CellKind.Switch` cell: the same track and knob, plus a click (a `Button` on the track; `IsClickable`, `ForEachClickableCell` and `widget_click <row>/switch` see it). The mod page's working switch.
- `CellKind.Diamond`: a square turned 45 degrees (`CellLayout.DiamondSide` 11, cell `DiamondWidth` 22), filled (`Text = "filled"`) or a hollow outline of four 2-unit edges (`"hollow"`), in the cell's tone through `ModsListLayout.DiamondColor` (the existing green, amber, red, muted). Images, not a glyph: the game font has no U+25C6. The Mods list's status mark.
- `CellRowSpec.Soft`: a list row whose hover is a subtle light tint (`MenuPalette.Text` at 6 %) and whose selected fill is a soft accent tint (`MenuPalette.Gold` at 14 %, 20 % hovered) beside the slim left bar, instead of the near-black `RowHover` and the solid selected fill. Derived from existing entries; no palette entry was added.
- `IUiRuntime.TryCopyToClipboard` / `CanCopyToClipboard` (defaulted false; Unity: `GUIUtility.systemCopyBuffer`, the clipboard the text fields paste from, read back to confirm) and `UiDialog.OpenContent` (a framework dialog of any shape, for the problems notice with its lines).

### Borrowed game art across Core swaps (BUTTONART1, source-checked and game-free tested, not live-verified)

The settings kit (`SettingsArtDonors`), the Keybinds tab (`KeybindArtDonors`) and every game button (`GameButton()`, `CellSkin.KeyCap`, the settings grid's `button` and `keybind` rows) draw sprites borrowed from the game's menus. Each part takes the first rung that has it:

| Rung | Readback | What it is |
|---|---|---|
| Live | `live` | A donor menu in a loaded scene. |
| Owned copy | `owned-copy` | This generation's GPU copy of the part (copy note `owned`, or `kept` when it was adopted). |
| Held | `cached` | The original sprite, held on a hidden image while its texture stays loaded. |
| Kept | `kept` | A copy an earlier Core generation of this process handed to Bootstrap's process-lifetime sprite table (`IconCache`). |
| Resident | `resident` | A menu prefab in memory, or a loaded sprite of the part's primary name. |
| Kit | `kit` | No game art. |

- **Kept copies.** Every owned copy is also stored in Bootstrap's `IconCache` (owner `progenitor.core.game-art`, through `BootstrapLink.TryGetOrCreateSprite`), and the copy then draws from that table's texture. Core owns only a bordered sprite over it, so a Core unload destroys the sprite and never the texture. The part, the donor's sprite name, the 9-slice border, pixels per unit, the image type and the donor image's color are in the row's path (`game-art/{tag}{part}~{sprite}~b{L}_{B}_{R}_{T}~p{ppu}~{s|n}~c{RRGGBBAA}.png`, numbers in hundredths, `GameArtKept`). The next generation reads them back from the table sprite's name (`progenitor-icon-…`) during the resident scan it already makes. No Bootstrap change is needed: the table has existed since 2026-09-03. Without Bootstrap the copy stays Core-owned, as before.
- **Names are not identities.** A bare loaded sprite is matched by the part's primary name only (`SettingsArtLadder.PrimaryPartOf`). The alternate `Button_Normal` is accepted only where a donor menu names it, because the game ships three sprites of that name: the Options Back button, a 40×40 main-menu frame and the HUD's flat red 35×35 square, the only one loaded in a world. A button part (the menu button and the popup button family) must also have a game button's shape: a horizontal-only 9-slice on a rect at least twice as wide as tall (`SettingsArtLadder.ShapeFits`).
- **In a world with nothing kept** (the client started straight into a world, or every copy failed), the menu button is not available. A game button then takes the popup button (`SmallerButton_Normal`, live in every world through `CommonPopup`). The Keybinds ornament (`Divider_Big_04`) and category rule (`Divider02_LeftToRight`) are looked for by name, because their only assets are in the HUD's bundle. A loaded rule with no donor image is tinted like the game's section rule (`GameSettingsLook.HeaderRule`).
- **Before the art is ready.** A game button with no art yet draws a flat steel face and frame (`GameSettingsLook.NeutralButtonFill` / `NeutralButtonRim`, the game buttons' center color), never the kit's dark red. `SetGameButton` retries on idle frames and repaints pending game buttons at every panel show, so a list built before a swap's idle preparation (the parked sidebar's Overview / refresh and Close Mods) takes the art when it arrives.
- **Readback and logs.** `mods_ui` `settingsArt.parts.<part>` gives `{source, donor, sprite, copy}`; a kept part reads `donor` `kept`. A fall-through is logged once, for example `Settings kit: art menuButton live=… cached=none kept -> kept`. The settings page's `buttonArt` gives the button donor (`key-cap`, `menu-button`, `popup-button` or `kit`) and its sprite.

- **Inline buttons (INLINEAPPLY1; source-checked, built and game-free tested in `ServerSettingsStage5Tests`, not live-verified).** A button attached beside a field on the settings grid (`BeginInline` … `Button` … `EndInline`: the inline **Apply** of a one-line server setting or server rule, the Local server **Apply** / **Clear** off the workspace, a mod author's own inline button) is drawn through `SetGameButton`, the same path as **Show** or **Close Mods**, so it takes the borrowed steel art, waits for it and survives Core swaps. It keeps its compact slot and the field's bar height (`ModSettingsRowGrid.InlineSlots`, `BarHeight`). The review of 2026-10-06 showed a flat gray block with lighter text: the cloned donor button's own look. Readback: a settings row lists `inlineButtons [{caption, art}]`, `art` `game-button` (the steel art drawn), `game-button-pending` (the flat steel face, art not ready) or `kit`; `IUiHandle.ButtonArt()` returns the same word, the runtime's answer or, game-free, the requested style.

Evidence and the symptom this fixes: [knowledge note](../knowledge/notes/game-art-name-match-red-button.md).

## Dialogs

**Status: source-checked and game-free tested, not live-verified** (`tests/Progenitor.Tests/DialogTests.cs`, `UiDialogTests.cs`; the migrated callers' own suites). The [acceptance procedure](#dialog-acceptance-procedure-not-run) has not been run.

Every modal dialog Progenitor shows uses one layout, `DialogLayout` (`src/Progenitor.Core/Ui/DialogLayout.cs`): the framework's own confirmations and a mod's `ctx.Dialogs` alike. A mod gets a finished-looking dialog without styling anything.

### The game's popup look (DIALOGS2)

**Status: source-checked and game-free tested (`DialogsVanillaTests`), not live-verified.** Every dialog is drawn like the game's own popup, `CommonPopup`, the "Warning / Yes / No" confirm. Probe D1 read it on 2026-10-01; the evidence is research `menu-kit-assets.md` section 7.

| Part | The game's | Ours |
|---|---|---|
| Overlay | The blocker: no sprite, black at 0.8, full screen | The dialog backdrop at the same color |
| Frame | `Window_Box_Background`, 9-sliced (border 32), white: the dark panel with the steel corner trim | The frame image, the owned copy |
| Title | DropShadow 24, 0.9 gray, centered, then `Divider_Big_02` (592×12, the thin steel rule with its small center ornament) | 24 regular, 0.9 gray, centered, with the rule under it in a reserved 24-unit band at the game's share of the row |
| Body | DropShadow 22, 0.9 gray, centered | The body, 0.9 gray, centered |
| Buttons | Two equal 256×42 `SmallerButton_Normal` (border 40,0,70,0), sprite-swapped to `SmallerButton_Normal_Hover` / `_Pressed` / `_Disabled`; light captions | `CellSkin.PopupButton`: the same sprites and swap. Every button is the same width, 256 when the row holds them. Our order stays (the dismissal left) and so does our wording. A destructive main action's caption is red. |
| Facts | (no game equivalent) | Unchanged: a muted label column and a bright value, no separators |

- **Art source.** The sprites are parts of the settings kit's ladder (`SettingsArtLadder`, `SettingsArtDonors`), copied into Progenitor-owned textures on idle frames once a scene settles. The donors are every `CommonPopup` and its prefab; D1 found them resident and live at the title and in a world. A part that is not ready draws the kit's flat look.
- **Readback.** `dialogs.art` gives each part's source: `frame`, `titleRule`, `button`, `buttonHover`, `buttonPressed`, `buttonDisabled`, and a form's check lines' `check` (the checkbox frame) and `checkMark` as `{source, donor, sprite, copy}`, and `overlay`. A check line draws the kit's box while either reads `kit` or `unresolved`, and the game's checkbox otherwise.
- **Text input.** A Prompt's text field keeps the kit input box.

### Layout rules

Sizes are canvas units (the game's 1920×1080 design space, scaled uniformly to the screen).

| Part | Rule |
|---|---|
| Frame | The ordinary panel width (640). Centered on the screen, over the existing dim, raycast-blocking backdrop. Height = a 16-unit top pad + the content + the 24-unit footer band. No pane title band and no 200-unit floor (the minimum is 120). The frame never passes 80 % of the screen: the body is capped so the title, the content, the button row and the footer always fit (below). Rows are laid out at `DialogLayout.ContentRowWidth` (560, the frame less the 20-unit viewport insets on each side), centered in the viewport. Capture mode `WhileShown`. |
| Title | Centered, 24 px, 0.9 gray (DIALOGS2: regular weight, the game's header rule under it), wrapping at the row width less 20 on each side (520), at most three lines (then an ellipsis). |
| Body | Optional. Centered, 0.9 gray (DIALOGS2; muted before), 17 px, wrapping at 460. A title or body row is its measured text's height plus 6 units — not the ordinary 40-unit row floor, which left a one-line body with a dead band under it — so every dialog has the same space between its text and its buttons. A body too long for 80 % of the screen is cut with an ellipsis at `DialogLayout.BodyCap`; the full text stays in the readback (`widget_read`, `Prompt.Body.Text`). |
| Facts | DIALOGS2. Optional, under the body: one plain cell row per fact (`dialog-fact-<i>`, cells `label` / `value`). A 24-unit inset cell and one cell gap on each side; the label column is the widest label's estimate × 1.3, at most 40 % of the block, muted, left-aligned, 17 px; the value fills the rest, bright (or its tone), left-aligned, top-aligned, wrapping to at most three lines (the row is born that tall: lines × 20.4 + 3; DIALOGS3 tightened it from lines × 22.1 + 6 after live review). |
| Note | DIALOGS2. Optional, under the facts: one short sentence drawn like the body (centered, muted, 17 px). DIALOGS3: its row (`dialog-note`) is 8 units taller than its text, with the text at the row's bottom, so there is space above it and it does not crowd the last fact. |
| Content | A Prompt's text field (420 wide) with an error line under it (16 px, the readable warning tone `Orange`, hidden until there is an error); a long Choose's option buttons (360 wide, one per 40-unit row; at most 10 options). |
| Button row | 52 units tall, the buttons at the bottom of it. Centered as a group between two equal spacers. Every button has one width: the widest caption's estimate × 1.3 + 40, at least 120; when the row cannot hold that many at that width they share it equally (a caption then ellipsizes; the row never overflows). Order: the dismissal leftmost, then options or extra buttons, the main action rightmost. |
| Tones | A mod's main action is the accent (`Gold`), or `Danger` when `danger: true`. Framework confirmations keep their own tones (`Ok` for install, reactivate and updates, `Orange` for reversible changes such as a key swap or a switch-off); a destructive framework confirmation (`UiDialog.Open(..., danger: true)`, below) draws its main button in `Danger` whatever tone it passed. Other buttons are the plain button tone. No palette entry was added. |
| Footer | One small muted line (13 px) — see [the footer rule](#dialog-footer). |

The runtime draws the title and body through `IUiRuntime.AddDialogText` (a label row with its own columns, measured by TMP) and everything else as plain cell rows (`CellRowSpec.Plain`: no row click, no hover tint, no fill). The layout pass sizes a dialog with `DialogLayout.FrameHeight` instead of the ordinary panel's title band and floor.

### API

`ctx.Dialogs` (`PluginDialogs`). Client only: on the server a call logs once and returns a handle that does nothing.

| Member | Behavior |
|---|---|
| `Confirm(title, body, yes, onYes, no = "Cancel", onNo = null, danger = false, showModVersion = null)` → `IDisposable` | Yes/no. The main button and Enter run `onYes`; `no` and Escape run `onNo`. |
| `Notice(title, body, ok = "OK", onClose = null, showModVersion = null)` → `IDisposable` | One button. The button, Enter and Escape run `onClose`. |
| `Choose(title, body, options, onPick, cancel = "Cancel", onCancel = null, showModVersion = null)` → `IDisposable` | `onPick` gets the option's index. At most three options whose captions fit the row at one width (with Cancel's) are buttons on the button row, Cancel on the left; otherwise a vertical list above a Cancel row. Enter does nothing (there is no main action); `cancel` and Escape run `onCancel`. One to ten options, or `ArgumentException` (a longer list belongs on a page). |
| `Prompt(title, body, initial, onSubmit, placeholder = "", validate = null, submit = "OK", maxLength = 0, cancel = "Cancel", onCancel = null, showModVersion = null)` → `IDisposable` | A text field. The main button and Enter validate: `validate` returns null to accept, and `onSubmit` gets the text; anything else refuses and is shown under the field (the dialog stays open) — an empty or blank string shows `Not accepted.`. A text longer than `maxLength` is refused the same way (`At most N characters.`); the field also stops typing at it, and an `initial` longer than `maxLength` is cut to it (the field and the value agree). The field has the keyboard when the dialog appears (the same focus path a click takes). Typing clears the error. A validator that throws blocks the submit (`This value could not be checked.`) and is logged. |
| `Confirm(title, facts, yes, onYes, note = "", no = "Cancel", onNo = null, danger = false, showModVersion = null)` → `IDisposable` | <a id="facts"></a>DIALOGS2: the same yes/no, with label/value `DialogFact` rows in place of the body and at most one short `note` under them. A fact with an empty value is left out; an empty label continues the fact above (a second `Requires` line). `DialogFactTone` `Ok` / `Warn` / `Danger` colors a value. |
| `Notice(title, facts, note = "", ok = "OK", onClose = null, showModVersion = null)` → `IDisposable` | DIALOGS2: a notice with facts. |
| `Form(title, options, onSave)` → `IDisposable` | FORMDIALOG1: a name and a few on/off choices in one dialog: a captioned one-line field, one to ten `DialogCheck` boxes under a heading, Cancel and Save. `DialogFormOptions` holds the field, the boxes and the rules; `onSave` gets a `DialogFormResult` (`Text`, `IsOn(index)`, `IsOn(key)`). See [Form dialog](#form-dialog-formdialog1). |
| `ShowModVersion` (get/set, default `true`) | The per-plugin default of the footer's mod name and version. A call's `showModVersion` wins. |
| `IUiPanel.ConfirmButton(text, question, body)` → `IUiButton` | The stage 2 confirmed button with a muted body under the question. Same dialog. |

```csharp
ctx.Dialogs.Prompt("Name your camp", "Up to 24 characters. Letters, digits and spaces.", "",
    name => ctx.Log.LogInfo("Sample dialogs: prompt -> '" + name + "'."),
    placeholder: "Riverside", validate: Validate, submit: "Save", maxLength: 24);
```

This is `samples/Progenitor.Sample/SampleDialogs.cs` (compile-checked with the Sample's Release build; compile against that file rather than copying this block). The Sample's settings page has a **Dialogs** section with one button per kind (Mods workspace → Progenitor Sample).

```csharp
ctx.Dialogs.Confirm("Plant the herb garden?",
    new[]
    {
        new DialogFact("Plot", "Castle courtyard, north wall"),
        new DialogFact("Requires", "Gardening table — built", DialogFactTone.Ok),
        new DialogFact("", "Water barrel — missing", DialogFactTone.Danger),
    },
    "Plant", () => ctx.Log.LogInfo("Sample dialogs: facts -> yes."),
    note: "Just a demonstration; nothing is planted.");
```

Shortened from `SampleDialogs.Facts` (**Dialogs → Facts dialog**). The readback `mods_ui status` → `dialog.facts` lists `{label, value, tone}` and `dialog.note` the sentence.

### Form dialog (FORMDIALOG1)

**Status: source-checked, built and game-free tested (`DialogTests` `A_form_*` and `Form_*`, `ChatTabFormTests`, `UiNavTests.Tab_walks_a_dialogs_field_and_stops_and_Up_from_the_first_stop_returns_to_the_field`), not live-verified.** Decided 2026-10-17 from the user's review of the chat tab editor: changing what a tab shows must not need a Next step, the channel choices are check boxes like the game's Options menu, and Save is set apart from them.

```
                    Edit tab
       ─────────────────◆─────────────────
   Name     [ Trade                              ]
   Show messages from
   [x] Global
   [ ] Local
   [ ] Clan
   [x] Whispers
   [ ] System and mods
   [ ] Mentions of me
            [   Cancel   ]   [    Save    ]
                Progenitor 2026.10.x
```

```csharp
ctx.Dialogs.Form("Form dialog", new DialogFormOptions
{
    Caption = "Name",
    Initial = "Night market",
    MaxLength = 24,
    Heading = "Show stalls for",
    Checks = new[]
    {
        new DialogCheck { Label = "Blood", On = true, Key = "blood" },
        new DialogCheck { Label = "Weapons", Key = "weapons" },
        new DialogCheck { Label = "Gems", On = true, Key = "gems" },
        new DialogCheck { Label = "Rare finds", Key = "rare" },
    },
    RequireOne = true,
    NoneHint = "Turn on at least one stall.",
    Validate = r => r.Text.Trim().Length == 0 ? "Enter a name." : null,
    OnCancel = () => Say("cancel"),
}, r => Say("\"" + r.Text + "\" blood=" + r.IsOn("blood") + " weapons=" + r.IsOn("weapons") + " gems=" + r.IsOn(2)
    + " rare=" + r.IsOn("rare")));
```

From `samples/Progenitor.ControlsSample/Plugin.cs` (`ControlsDialogs`, kind `Form`; compile-checked with the sample's build): the Controls sample's **All controls** page (Dialogs) and the Gallery's Feedback tab, **Form**.

| Part | Rule (`DialogForm`) |
|---|---|
| Edge | Every part starts on one left edge, the facts block's 24-unit inset inside the 560-unit content row: the field's caption, the heading, the boxes. The buttons stay the dialog's centered popup pair, Cancel left, Save right in the accent. |
| Field | `dialog-input`: the caption (17 px, muted) in a column as wide as its estimate × 1.3 plus the facts' label pad (at least 56), then the field (the Prompt's `field` cell) filling the row. The error line `dialog-error/error` starts under the field, left-aligned, in the error tone. The field has the keyboard when the dialog opens. |
| Heading | `dialog-checks-heading/heading`: 17 px, muted, 6 units under the field. |
| Boxes | `dialog-check-<i>`: one 30-unit line per box, 2 apart: the `check` cell (`CellKind.Check`, 24 units: the game's `Checkbox_Normal` frame and checkmark from the settings art, the kit's framed box until that art is ready, tried again on later frames until the art arrives or its source is final) and the `label` cell (17 px, bright, left; in the box's `Color` when it has one). The whole line is the click target. |
| Label color | `DialogCheck.Color`: `#RRGGBB` (trimmed, uppercased; anything else is ignored), empty by default (the bright label). A color under 4.5:1 against the dialog's ground (`MenuPalette.PaneFill`, the captions' rule `MinCaptionContrast`) is mixed toward white in 10% steps until it reaches it (`DialogForm.ReadableLabel`). A disabled box draws the disabled tone. The chat tab editor colors each channel's label in that channel's chat color and **Mentions of me** in the mention gold `#E8B33F`. |
| Hint | `dialog-checks-hint/hint`: with `requireOne`, shown in the error tone while no box is checked, when Save is grayed. |

- **Keys.** Enter saves (validated, as a Prompt's submit), unless the player moved the keyboard focus onto a box or a button: then Enter presses that one (on a box it flips it, on Cancel it cancels, on Save it saves). The focus a dialog opens with does not count, so typing a name and pressing Enter saves; a `danger` confirmation's main answer is never pressed by Enter. Escape cancels. Tab and Shift+Tab walk the field, each box and the buttons, in reading order, and wrap around; Down leaves the one-line field for the first box, and Up from the first box goes back into the field. Tab and Down stay with an input method while it is composing text in the field. Space on a focused box (A on a controller) flips it; arrows move between boxes and buttons. These keys are read by the framework's own input layer (the typed-key pump and `UiNav`, sampled every frame), like every dialog's Enter, and apply to every dialog with a one-line field (a Prompt too).
- **State.** Nothing is written until Save. A framework dialog that steps in front puts the form back in the queue with its text and boxes as the player left them.
- **Bridge.** `widget_click` with a box's label or row key flips it through the line's own click slot. `mods_ui status` → `dialog.checks [{index, row, label, on, color}]`, `checksHeading`, `checksOk`, `checksHint` (the hint while shown) and `mainEnabled`.

### Behavior

- **One at a time.** The dialog on screen is `UiDialog.Current`. A mod's dialog opened while another is up waits in a queue and is shown when the screen is free, first in first out. A dialog a callback opens goes behind the ones already waiting.
- **Framework dialogs step in front.** A framework confirmation replaces a framework confirmation without answering it (unchanged), and puts a mod's dialog back at the head of the queue unanswered; the mod's dialog comes back, with the text the player had typed, when the framework's is answered.
- **Keys.** Enter picks the main action (Yes, OK, a Prompt's submit, a single-button box's only button); not in a Choose, not in a `danger: true` confirmation (its main button needs a click, or a controller's right then A), not while a multi-line field has focus, and not while a text field outside the dialog has the keyboard (the game's chat, another panel's field). Enter within 250 ms of the dialog appearing is ignored, so the Enter that opened it cannot answer it. Escape picks the dismissal (a Notice's is its OK). The Escape sweep holds the queue so the next dialog is not shown and hidden by the same press. Bootstrap forwards only Escape, so Enter is read in Core's typed-key pump from held state.
- **Destructive framework confirmations (DANGERENTER1, 2026-10-10).** `UiDialog.Open` takes `danger: true` for a framework confirmation whose Yes uninstalls, deletes or wipes something; it sets `DialogContent.Danger`, so Enter does not answer it and its main button is drawn in `Danger`, as a mod's `danger: true` confirmation is. Marked destructive: the mod page's **Uninstall** and **Cancel install** prompts (`ModPagePane.PromptIsDestructive`; Cancel install deletes the downloaded file), the Install tab's Uninstall and Cancel install boxes (`InstallModsPane.PromptIsDestructive`, not when the box is refused and offers only Close), the Uninstall of a plain BepInEx mod and of a `progenitor-reload` file, **Reset all** on a mod's settings and **Reset all mod keys** (both wipe the player's changes with no undo), and an admin setting confirmation whose `AdminSettingConfirm.Danger` is set. Not destructive, so Enter still answers them: Reactivate, Install, updates, a key swap, a switch-off that also turns off dependents, the restart offers, the join questions and a mod's `ConfirmButton`. The server Stop / Restart box keeps its `Danger` tone without the flag. Game-free tests: `DialogTests.A_framework_danger_confirmation_ignores_Enter_and_wears_the_danger_tone`, `ModPageTests.Enter_does_not_answer_the_Uninstall_prompt`, `ModPageTests.Enter_still_answers_the_Reactivate_prompt`. Not live-verified.
- **Handles.** Each method returns the dialog; `Dispose` closes it, or takes it out of the queue, without running a callback.
- **Liveness.** A dialog whose panel something else hides or disposes (another owner's sweep) is dropped without a callback (`Ui: dialog '<title>' left the screen without an answer; dropped.`) and the queue moves on. A framework dialog opened by a handler that Escape ran is shown after the Escape sweep, not hidden by it; a framework dialog that fails to build hands the screen back to the mod's dialog it displaced, and a dialog whose build throws is torn down with its backdrop.
- **Callbacks.** A dialog closes before its callback runs. A callback that throws is caught and logged (`Ui: dialog '<title>' answer threw: …`) and the queue continues.
- **Unload.** `ctx.Dialogs` tracks a registration: every unload reason drops the plugin's dialogs, on screen and queued, without running a callback (`Ui: dropped N dialog(s) of an unloading plugin.`). `Ui.RemoveAll(owner)` does the same as a second line.
- **Hot reload.** The plugin's callbacks are held by Core's `UiDialog` only; the click and text slots are bound to that object's own methods. Nothing collectible is handed to Bootstrap or IL2CPP. A mod's dialog panel is created under the plugin's own UI root.
- **Core unload (DIALOGS3).** The first step of `Ui.Uninstall`, for every unload reason, is `UiDialog.CloseAllForUnload`: every dialog the generation holds (framework and mod, on screen and queued) is closed and its panel destroyed, no callback runs, and the generation shows no other dialog. Without the latch, closing one dialog showed the next queued one on a fresh overlay that no later sweep reached; live 2026-10-01 an install confirm survived a Core swap under `progenitor-ui-unowned` beside the new generation's dialog. A panel that a close handler builds during the teardown is disposed too. The log line `Ui: uninstalled ... dialogs=N late=N held=0` reports the result, and a dialog still held after the teardown is listed as `dialogs=N` in the Ui pins.
- **Bridge (DIALOGS3).** With a dialog open, `mods_ui widget_click label=<caption or cell key>` (without `pane` or `index`) clicks that dialog's button first, using the button's own click slot. Before this change `label=No` matched the first workspace widget whose text contained "No", and the dialog stayed open. `mods_ui dialog_choose {choice}` presses a button of the open dialog through the same path: `yes` presses the main button, `no` / `cancel` press No / Cancel / Close (and a Notice's OK), a button's caption or cell key presses that button (ignoring case: `OK`, `Restore`), and an index picks a Choose option (`0`, `"2"`), whether it is drawn as a button or as a list row; on any other dialog the index counts its buttons in screen order, so a one-button dialog's button is `0`. It works with the workspace closed and returns `{label, choice, title, answered, dialog}`; the dialog readback lists `choices [{index, key, caption, aliases}]`. With no dialog open, or no such button, it returns `ok: false`, and the error names the dialog's choices. `mods_ui dialog_type {text}` types into a Prompt's field as a player would: the field draws the text (the placeholder hides), the main button and gated answers light when the confirmation word matches, and the readback's `field` is the drawn text. It refuses a dialog without a field or a read-only one.

### Dialog footer

A dialog a mod opens through `ctx.Dialogs` names the mod: `<mod name> <mod version> · Progenitor <version>`, for example `Progenitor Sample 2026.9.66 · Progenitor 2026.9.67`, from the plugin's BepInEx metadata (`ctx.Name`, `ctx.Version`). On by default; `showModVersion: false` on a call, or `ctx.Dialogs.ShowModVersion = false` for the plugin, gives the plain `Progenitor <version>` line. The line never wraps: when it is too wide for the footer (560 units at 13 px, by the glyph estimate) the mod's name is cut with an ellipsis; the versions are never cut.

**Framework dialogs keep the plain `Progenitor <version>` line**, including the ones about one mod (uninstall, reactivate, cancel install, Reset all, a `ConfirmButton` on a mod's page): the question is the framework's, and the mod is already named in the title. Only text a mod wrote through `ctx.Dialogs` carries the mod's name.

### Framework callers

Every former `Ui.Dialog` / `UiDialog.Open` caller draws with the layout above and keeps its wording, tones, row and cell keys (`prompt:<key>/promptYes`, `modPrompt-<kind>-buttons/yes`, `modSettingsResetAll-buttons/yes`, `uiConfirm-buttons/yes`) and lifetime rules: the Install tab's install / uninstall / reactivate / cancel-install / refusal boxes, the mod page's three prompts, the settings page's **Reset all** (closed on a rebuild, a mod switch, the workspace closing and the mod's unload; Yes refused if the page is no longer on show), and `ConfirmButton`. The button pair is now centered with No left and Yes right, instead of right-aligned against a scrollbar inset.

The **join-refusal panel** (PLAINDENY1) keeps its own lifecycle (one per handshake result, disposed per join) but is drawn with the same layout: the title, the plain summary lines, and one button row with **Show details** / **Hide details** on the left and **Close** on the right (`deny-buttons/details`, `deny-buttons/close`). Details on request still rebuild the panel in place with the technical lines under the summary. Enter closes it (Close is the main action; the same 250 ms grace). It now has the dialog backdrop, so the disconnect screen behind it takes no clicks until it is closed; a build that throws after the chrome is applied disposes the panel, backdrop and all.

Not migrated: the **per-server pause notice** (deliberately non-modal and closed by a timer; a modal dialog would change that). (The optional-mod consent prompt, also not migrated, was removed 2026-10-05.)

### Custom pages use the settings grid

The controls a mod adds to its own custom page (`ctx.Menu.Register(...).Page(title, panel => …)`) — `Toggle`, `Slider`, `Input`, `Dropdown`, `Number`, `SliderField`, `ColorField`, `KeyField`, `TextArea`, `Radio`, `MultiSelect`, `Info`, `Fact` — are drawn on the stage 2 settings grid: the name on the left, the control in the fixed control column at the same x on every row, a toggle as the track-and-knob switch (`UiPanelHandle.GridControls`, set by the Mods workspace while it builds a custom page). Labels, headings, notes and dividers keep the full row. A button (`Button`, `ConfirmButton`, the page's own Back) is the game's compact button at the row's left edge, sized to its caption as the game's Back is (`PageButtonLayout`: the caption's width plus 48, at least 140, the grid's control height), drawn with the game's steel art; an inline block (`BeginInline`/`EndInline`) puts its buttons side by side from the left, 8 apart (CSAMPLE1, decided 2026-10-10, "left, like Back"). A plain `Slider` sits in the control column with the slider field's track width and the game's track and handle (GRIDROW1; game-free tested in `SettingsControlsVanillaTests`, live-verified 2026-10-11 on the controls sample's page), and a date-time field's row is a grid row. A `Fact` reads as an `Info` line: its name on the name column, its value right-aligned in the control column. A date-time field's caption starts on the grid's name inset, in the names' gray, and its value box on the grid's bar column at every row width (GRIDROW1, `CellWidth.GridName`). Live 2026-10-11 on the Sample's window, before: the caption started at the row's edge and a fact was the kit's centered pair. After (Core 704f130fb, same day): the fact's name sits on the name column and its value right-aligned with the `Info` value under it, and the date-time caption and value box sit on the grid's columns; the caption's gray was live-verified later that day on the controls sample's page (game-free tests: `DateTimePickerTests`, `DialogTests`). The date-time value box is drawn shorter than the text fields' bars (not changed). Live 2026-10-10 on the controls sample before this: buttons were full-row kit bars, an inline block of buttons stacked one per row, and a plain slider was drawn over its own caption. Not changed: a panel a mod opens itself with `Ui.Panel` (floating panels such as the Sample's `.ui demo`) keeps the ordinary label/control columns.

### Opening a custom page, and the game's look on a mod's controls

A mod opens its page with `ctx.Menu.OpenPage(title)`, from a keybind or a button. In a world the Mods menu is a tab of the game's full-screen menu, so the call opens that menu on its MODS tab first (as the Open Mods key does) and shows the page on a later frame; at the title screen, or with the menu already open, the page shows at once. `ModsMenu.OpenPage(pluginId, pageId)` is the workspace's own navigation: it draws the page without opening the game's menu, and an in-world player is left on the game's spellbook page (2026-10-09). `ctx.Menu.OpenPage` returns false and opens nothing for an unknown title, and on the server.

Plain buttons and labels keep the kit's look on a floating window unless it asks for the game's. `UiLook.GameWindow(panel)`, called before adding controls, gives a floating window the game's popup frame and the Mods page's grid, compact buttons and controls (GAMEWINDOW1, decided 2026-10-10: "game popup + grid"; verified live 2026-10-11 on the Sample's F9 window: the frame, headings, compact buttons and grid rows; on 2026-10-12 the mouse wheel scrolled it to its bottom rows, the key field and the toggle); it stays a placed, non-modal window with its own title. `UiLook` also asks for single pieces: `panel.Button(caption).GameStyle()` draws the game's standard button (the same art as the workspace's own Close and Apply buttons), and `panel.Label(text, LabelTone.Ok)` is a label in a framework status color that `SetTone` / `SetText(text, tone)` change in place. Both work on a floating window as well as a Mods page. `GameStyle` and the tone label: source-checked and game-free tested (`ModPageOpenTests`, `UiLookTests`), and seen live on 2026-10-12 on the Progenitor Sample's Example page, the example for both, opened with its Shift+F9 key. A plain `ctx.Keybinds.Register` bind does not fire at the title screen (no chat window can be read there). A key registered with `ctx.Keybinds.RegisterOpener(id, key, description, onPressed)` does (KEYOPEN1): it is for a key that only opens or closes the mod's own UI, it also fires while a panel has input so it closes what it opened, and a key that calls `OpenPage` that way works at the title screen as well as in a world (verified live 2026-10-12). A button can call `OpenPage` anywhere.

### Readback and bridge handles

`mods_ui status` → `dialog {open, key, kind, footer, title, error, queued}`. A mod's dialog: buttons row `dialog-buttons` with cells `cancel`, `main`, `option-<i>`; a list option `dialog-option-<i>/option-<i>`; the Prompt field `dialog-input/field` and the error line `dialog-error/error`; a form's boxes `dialog-check-<i>` (cells `check`, `label`), its heading `dialog-checks-heading` and hint `dialog-checks-hint`. `widget_click dialog-buttons/main` answers the dialog as a click does.

### Dialog acceptance procedure (not run)

On the isolated test client (not a client someone is playing), with a candidate that contains this change:

1. Open the Mods workspace, select **Progenitor Sample**, scroll to **Dialogs**.
2. **Confirm dialog** → the dialog is centered; the title has no empty band above it and no rule under it; **Keep them** (left) and **Clear notes** (right, danger tone) are the same width and centered as a pair; the footer reads `Progenitor Sample <version> · Progenitor <version>`. Capture at 3840×1080 and at 1600×1024 and measure: the pair's midpoint within 2 px of the frame's.
3. Enter does nothing (a danger dialog). Escape → `confirm -> no`. Open it again and click **Clear notes** → `confirm -> yes; Notes cleared.` and the Notes setting is empty. Choose, Confirm and Notice have the same space between the body and the buttons.
4. **Notice**, **Choose** (three buttons, Cancel left), **Prompt**: Save with the field empty shows `Enter a name.` under the field and keeps the dialog; type `River side` and Enter → `prompt -> 'River side'`.
5. Queue: press Confirm and then, while it is open, run `mods_ui widget_click` on a second Sample button through the bridge; answer the first and see the second appear.
6. Hot-swap the Sample with a dialog open and one queued: both leave the screen, no `Sample dialogs:` line is logged, and nothing is left on screen.
7. Framework dialogs: **Reset all** on the Sample page and the Install tab's install confirmation draw the centered pair with No on the left.
8. Custom page: **Example** shows **Show hints** (switch), **Demo volume**, **Demo greeting**, **Demo style** and **Demo count** with their names on the left and every control in one column at the same x as the settings page's; the labels and **Try an action** keep the full row.
9. Restore: close the workspace; leave the client at the title screen.

Guessed sizes (set from the glyph estimate, not measured live): the 16-unit top pad, 24/17/16 px text, the 520/460/420/360 widths, the 52-unit button row, the ×1.3 + 40 button width with the 120 floor, the 6-unit text pad, the 10-option Choose cap and the 250 ms Enter grace.

## Widgets (P7, P7b)

Composed widgets for a mod's panel or Mods page: an ordered list, an entity and item picker, tabs with count badges, tooltips, a code text area (P7), and a data table, a master-detail screen and chips (P7b), and a checklist for choosing several of a long list ([CHECKLIST1](#checklist-checklist1), not live-verified). Each one is built from the kit's own cell rows and buttons, so it draws in the game's look, sits on the settings grid with the rows around it, and is reached by a controller like every other kit control. A widget is disposed with its panel (a close, `Ui.RemoveAll`, an unload, the Core teardown), which ends a drag and hides its tooltip. On a runtime that cannot draw (the server, a skipped module) a widget keeps its state and draws nothing, like every other handle. The HUD pieces (notifications, Locate, pick in world) are on `ctx.Hud`, [below](#hud-notifications-locate-and-pick-in-world); explained words and tip cards are under [Help](#help-explained-words-and-first-time-tips-p7b).

**Status: experimental.** Source-checked, built and game-free tested (P7: `UiWidgetsTests`, `UiCodeAreaTests`; P7b: `UiDataTableTests`, `UiMasterDetailTests`, `UiChipsTests`, `UiTermsTests`, `UiTipsTests`, `UiPickerMultiTests`, `UiWorldPickRepeatTests`); **live-verified on the test client 2026-10-14 through the bridge at 1920x1080 and 2560x1440, plus a 2x supersample capture (layout only)**, the P7 widgets as regressions in the same passes; not verified: native 4K pixel density, the feel of a real-mouse drag, and controller hardware (navigation was driven through the bridge's virtual pad, not a physical controller). The live procedure is [Widgets acceptance procedure](#widgets-acceptance-procedure-run-2026-10-14). Reference page: the Controls sample's **Widgets** page ([`WidgetsDemo.cs`](../samples/Progenitor.ControlsSample/WidgetsDemo.cs) and its `WidgetsDemo.*.cs` parts), five tabs: **Lists** (ordered list, chips), **Input** (pickers, code area), **Table** (data table), **Layout** (master-detail), **HUD** (notifications, pick in world, Locate, explained words, tip cards).

**Interface additions are additive for callers.** P7b adds members to existing interfaces (`IUiPicker.SelectAllMatches`, `IUiTabs.SetTerm`, `IUiWorldPick.PickCount`; later `IUiDataTable.OnRowAction` and `PressRowAction`) and properties to existing types (`UiListItem.Term`, `UiWorldPickOptions.Repeat`; later `UiTableRow.Actions`, `UiTableColumn.ActionSlots`, `UiDataTableOptions.MinRows`, `UiWorldPickOptions.CloseGameWindows`). Code that calls them needs nothing; these interfaces are implemented by the framework only, so a plugin never implements them and no plugin breaks. The framework-internal runtime seams gained default-bodied members, so their existing implementations are unchanged.

```csharp
using Progenitor.Ui;

ctx.Menu.Register("My mod", "…").Page("Targets", panel =>
{
    var tabs = panel.Tabs("tabs", new[] { "Targets", "Rules" });
    var list = tabs.Add(0, panel.OrderedList("targets", new[]
    {
        new UiListItem("chest-a", "Storage chest") { Note = "plot A", Badge = "12", Detail = "Accepts ore and stone." },
        new UiListItem("furnace-a", "Furnace input") { Note = "plot A" },
    }));
    list.OnReorder(m => ctx.Log.LogInfo(m.Id + " moved to " + m.To));
    list.OnRemove(item => ctx.Log.LogInfo("removed " + item.Id));
    tabs.SetBadge(0, list.Items.Count);

    var picker = tabs.Add(1, panel.Picker("item", new[]
    {
        new UiPickerEntry("crystal", "Crystal") { Section = "Resources", ItemPrefab = -257494203 },
        new UiPickerEntry("dark", "Dark Silver Ingot") { Section = "Resources", DisabledReason = "Unlocked by research" },
    }));
    picker.OnPicked(sel => ctx.Log.LogInfo("picked " + string.Join(",", sel.Select(e => e.Id))));

    var rules = tabs.Add(1, panel.CodeArea("Rules", "send 50 to \"Furnace\"", lines: 8));
    rules.SetHighlighter(MyHighlighter);            // Func<string, IReadOnlyList<UiTextSpan>>
    rules.OnChanged(text => rules.SetErrors(MyChecker(text)));
});
```

| Widget | Call | Vanilla counterpart and borrowed art |
|---|---|---|
| Ordered list | `panel.OrderedList(key, items, options)` → `IUiOrderedList` | The game's settings list: one settings-grid row per item with the list's separator rule, the held row in the one held-row style every kit list shares (the Mods list's entry band at its hover strength with the gold stripe, the caption gold, the grip lit; `UiTileState.Held`), the game's dropdown arrow (`DropDownButton_Arrow`) as the expander, box-less icon buttons in the muted gold for grip, up, down and remove (drawn marks, not font glyphs: brighter under the pointer, faint where the button does nothing on that row), the kit's count badge (the client gold) and the game item icon in the kit's icon well. The drawer is the Install list's drawer (`JoinAbove`). |
| Picker | `panel.Picker(key, entries, options)` → `IUiPicker` | The search field is the game's settings value box (`SimpleBox_Normal`) with the kit's clear button; results are settings-grid rows with the entry band for the selection, an icon well only around an icon that resolved (any other entry keeps a blank pad of the same width, so the captions line up), and the right-hand note and links 18 units clear of the row edge; section headings are gold captions; grayed rows use the kit's faint tone; the autofill suggestion is the kit's link (`#9fc7e8`, underlined). |
| Tabs | `panel.Tabs(key, captions, selected)` → `IUiTabs` | The Install tab's filter chips: the game's popup button (`SmallerButton_*`) with the gold plate on the selected one; a count badge after each caption. |
| Tooltip | `handle.Tooltip(text, title)` (any kit control), `UiListItem.Tooltip`, `UiPickerEntry.DisabledReason` / `Tooltip`, `IUiTabs.SetTooltip` | The dropdown description box (DROPHOVER1): the game's frame sprite (`PassiveFrame_Normal`) or its dark fill, a gold title line, a light body, on its own canvas above every mod panel. |
| Code area | `panel.CodeArea(text, initial, lines)` → `IUiCodeArea` | The settings value box's multi-line editor (TEXTEXPAND1) with the game's scroll bar (`ScrollBar_Bar` / `ScrollBar_Handle`), a line-number gutter, caller colors drawn over the field's own (transparent) glyphs, and a red marker and band on each line with an error. |
| Data table (P7b) | `panel.DataTable(key, columns, options)` → `IUiDataTable` | The Install tab's list: the header is the Install list's `CellHeader` (sortable captions in the game's small caps at 15 px; the sort mark is a drawn gold arrow with a small order digit when there are several keys); body rows are settings-grid cell rows with the list's separator rule and the Mods list's entry-row states (a light wash under the pointer, the gold entry band with its 3-unit gold stripe when selected, NAV1's frame for the controller focus); the game's dropdown arrow (`DropDownButton_Arrow`) as the row expander; the game item icon in the kit's icon well; editable numbers in the settings value box (`SimpleBox_Normal`); the waiting spinner as a drawn three-quarter ring in the pending yellow; the Columns button as the Options dropdown bar; the column chooser as the Install filter chips (`SmallerButton_*`); the scroll bar in the game's `ScrollBar_Bar` / `ScrollBar_Handle`. |
| Master-detail (P7b) | `panel.MasterDetail(key, options)` → `IUiMasterDetail` | The game's options menu pairing its category list with the selected category: settings-grid rows with the separator rule, the Mods list entry's band and 3-unit gold stripe behind the selected entry's half, its hover wash, NAV1's focus marker, a 1-unit divider rule in the separator color, the tab row as the Install filter chips, detail buttons as the game's popup button, links in the kit's link blue, members as the ordered list's rows. Part by part: [Master-detail](#master-detail). |
| Chips (P7b) | `panel.Chips(key, chips, options)` → `IUiChips` | The Install tab's filter chips (`InstallModsPane.Chip`): each chip is one game popup button (`SmallerButton_*`, with the game's hover and pressed sprites) with the gold plate when selected. An item chip's icon (the kit's icon well, 24, with the game item's own inventory icon) sits inside the chip art at its left; a removable chip's box-less remove glyph (muted gold, light cream on the gold plate) inside it at its right. The controller focus is the NAV1 frame 3 units outside the chip. |

### Ordered list

The list changes itself and then raises the event, so a plugin never has to redraw it. `SetItems` replaces the items (no event), which is also how a plugin undoes a change it refuses.

| Member | Behavior |
|---|---|
| `UiListItem(id, title)` | `Id` is the caller's stable key; duplicate ids are refused (`ArgumentException`, nothing changes). `Note` (muted, after the caption, the first cell a narrow row drops), `Badge` (count text), `Detail` (the drawer's text; empty means no drawer), `Icon` (a `Sprite`) or `ItemPrefab` (a game item's prefab id; the client shows that item's own inventory icon, `ManagedItemData.Icon`), `Removable`, `Tooltip`, `Term` (P7b: the caption explains a glossary term in place of `Tooltip`, see [Term tooltips](#term-tooltips)). |
| `UiOrderedListOptions` | `Capacity` (rows made; 0 = the first item count, at least 8, at most 64), `Reorder` (grip and arrows, on), `Remove` (on), `DetailLines` (1–6, 2), `EmptyText`. |
| `Items`, `Capacity`, `Overflow` | The order now; the rows the list can draw; items past the capacity (kept, not drawn; the footer says how many). |
| `Move(id, index)`, `Remove(id)`, `SetExpanded(id, open)`, `IsExpanded(id)` | Programmatic changes; each raises its event. |
| `OnReorder(UiListReorder)` | After every move: `Id`, `From`, `To` and the whole new `Order`. |
| `OnRemove(UiListItem)`, `OnExpand(item, open)` | After a removal; after a drawer opened or closed. |

**Drag.** Press the grip (three short bars) with the left button and move: the held row takes the held-row style (the Mods list's gold entry band at its hover strength with the stripe, the caption gold, the grip lit, the same in the master-detail member list), and the rows close up around it live, showing the order the drop will leave. Let go to drop; one `OnReorder` follows. The drop slot is the one whose center is nearest the pointer, so it changes on the line halfway between two rows; it is decided from the slots' fixed centers, not from the moving content, so it cannot flicker. A release the grip did not see (the pointer let go outside the window) drops on the next frame, from the framework's held-button sample. A right or middle press, a hidden list and a disposed list start no drag. Closing the panel, `Ui.RemoveAll` or an unload mid-drag ends the drag without an event. The pointer events come from an owned `EventTrigger` on the grip through the Bootstrap trampoline (`Ui.WatchPointer`'s path); the pointer position from the framework's input layer. No key is read.

**Buttons.** Up and down move one step; the top row's up and the bottom row's down are drawn faint and do nothing. Remove is faint on a row whose item is not `Removable`. The expander is faint on a row with no drawer. The icon and expander columns follow the items: hidden on every row while no item has an icon (or a drawer), shown on every row once one does, so the columns always line up. A click elsewhere on a row opens or closes its drawer.

### Picker

| Member | Behavior |
|---|---|
| `UiPickerEntry(id, label)` | `Section` (heading), `Note` (muted, right), `Icon` / `ItemPrefab`, `DisabledReason` (grays the row, refuses selection, shown in the note and the tooltip), `Tooltip`, `Keywords` (more words the search matches). |
| `UiPickerOptions` | `Multi` (each click flips; off: one), `Rows` (result lines incl. headings, 4–30, 8), `Placeholder`, `Autofill` (on), `Selected` (initial ids). |
| `Query` | The search text; typing in the field sets it. |
| `Matches`, `Suggestion`, `AcceptSuggestion()` | The matching entries; the autofill completion; complete the query to it (and select it in single mode). |
| `Selected`, `Select(id, on)`, `ClearSelection()`, `OnPicked(entries)` | The selection, in selection order; `OnPicked` after every change. |
| `SetEntries(entries)` | Replace the entries; selected ids that remain stay selected. |
| `SelectAllMatches()` | P7b. Multi mode: select every pickable entry matching the query (also those past the drawn lines), as the status line's **Select all** link does. One `OnPicked`; returns how many were added (0 in single mode or when all were selected). |

**Matching.** Case- and accent-insensitive, on the label and the keywords. Best first: the label equals the query; starts with it; a word of the label starts with it; every query word starts some label word; the label contains it; a keyword contains it. No match, not listed. Sections keep the order of their first entry in the caller's list; within a section, rank, then pickable before grayed, then the caller's order. **Autofill:** the best pickable match whose label starts with the query is offered as a link under the field (`Use "Iron Ore"`); a click completes the query (and picks it in single mode). There is no completion key, so the field never fights the keybinds. **Lines:** headings and entries share the `Rows` slots; a heading never ends the window; what did not fit is counted in the status line ("12 matches. Keep typing to narrow them down.").

**Multiple selection** (`UiPickerOptions.Multi`; P7b, `UiPickerMultiTests`). Each entry has a check column (a drawn check mark in the muted gold, brighter under the pointer; never a font glyph, never underlined); a click on the row or on the check flips it, and so does the controller's A on the row. The status line adds the count (`12 matches. 3 selected.`) and two links after the autofill suggestion: **Select all** (shown while some pickable match is not selected) and **Clear** (shown while something is selected). Grayed entries are never selected by either. `OnPicked` runs once per change with every selected entry. The multi status row is checked at 560–1248 units (`UiPickerMultiTests.The_multi_status_row_fits_at_every_checked_width`); the two links are never dropped, only the suggestion link is optional.

### Tabs and badges

`Tabs(key, captions, selected)`; `Add(tab, handle)` makes a control part of a tab (shown only while it is selected; add the controls after the tab row so they sit under it); `Selected` (setting it raises `OnChanged`); `SetBadge(tab, count)` (0 hides; over 99 reads `99+`, `UiBadge.Text`); `SetBadge(tab, text, tone)` (`Danger` / `Warn` / `Orange` draw the server red, `Ok` green, otherwise the client gold); `SetTooltip(tab, text)`; `SetTerm(tab, term)` (P7b: the caption explains a glossary term, see [Term tooltips](#term-tooltips)).

Each tab is one Install filter chip as wide as its caption needs, measured per character (`UiChipsText`: capitals and wide letters get their own width, so `HUD` is never cut), plus one glyph of slack and the button pad. The count badge sits **inside** the chip after the caption, 6 units from its right edge, 20 tall (an overlay cell that takes no pointer: a press on it presses the tab); the chip grows by the badge (its text plus 10, at least 22) and a 4-unit gap, and the caption's right margin keeps it clear. A tab whose caption explains a term grows by the marker's room as well. A tab switch shows and hides its content inside one layout batch (`UiLayoutBatch`), so the page is laid out once per switch, not once per row (`A_tab_switch_lays_the_panel_out_once`); the `OnChanged` handlers run inside the same batch, so a handler that rewrites a label or shows a control does not lay the page out a second time.

### Hub controls (HUB1)

The controls a mod's hub window needs, together in the controls sample's hub window (**Gallery** → Windows tab → **Hub window**, [GalleryHub.cs](../samples/Progenitor.ControlsSample/GalleryHub.cs)). Status: **source-checked and game-free tested, not live-verified** (`UiHubControlsTests`); none of it has been seen in the game.

**Game window tabs.** `panel.Tabs(key, captions, new UiTabsOptions())` on a panel made a game window with `GameWindow()` draws the game's own window tabs: a clone of the full-screen menu's inner tab, the art the Character menu draws its tabs with, through the same donor ladder as the Mods workspace's tab strip (the in-game menu's tab, a hidden template of it taken earlier this session, the resident prefab; `WorkspaceTabLadder`). The clone keeps the game's selected and hover states (hover lighter than selected), its caption in the donor's font at 22, centered. Each tab is as wide as its caption plus 48 (at least 140), with the badge and term marker inside it as on the chips. The row draws the strip's baseline, as the game's window tabs stand on theirs (the workspace strip on the pane's top trim, the inventory's on its tab divider): a 2-unit line across the whole row at its bottom edge (`MenuPalette.TabBaseline`, the tab frame's steel), drawn before the tabs. Every tab's bottom edge is the row's bottom edge (`UiTabsLayout.GameTabTop`, never centered), so the tabs cover the line's last rows and the selected tab's fill runs into it, at every window width. It is the one line there: the first shown row under the strip does not draw its own top line (a settings row's top gradient, a list row's separator; `UiTabsLayout.TopLinesHidden`). Without that, a settings row first under the tabs (a `SearchInput`) drew a second line one row gap under the baseline. The row under the strip is worked out again on every layout, so it follows the selected tab. Standard windows (Header, Tabbed) always draw chip tabs, which have no baseline, so their rows keep their lines. Readback: the row's `skin` is `game-tab-strip` with `baseline` (its height), and a settings row's drawn readback has `topLine` false while it is that row. With no donor reachable a game tab is drawn as the chip. Pick explicitly with `UiTabsOptions.Look` (`Auto`, `Game`, `Chips`). Unchanged for existing callers: the overload without options (`panel.Tabs(key, captions, selected)`) draws the popup-button chips everywhere, as it always has, and the standard windows (Header, Tabbed) keep their live-verified chip tabs; the game tab is a different art (the full-screen menu's tab, not the popup button).

**Stable height.** A window with tabs keeps its tallest height while it stays open (`UiTabsOptions.KeepHeight`, on by default, and on for the overload without options), the standard windows' rule: switching to a shorter tab never shrinks it; it opens next time at the selected tab's height. `MinContentHeight` (kit units, rows only, capped at 2000 and to the screen) opens it at least that tall on every tab: give the tallest tab's height. Both apply to floating windows; a Mods page is laid out by the workspace and a dialog by its content.

**Tab history.** A browser-style history of the tabs the player selected: a selected tab (a click, a controller press, `Selected`) is pushed and drops the forward part; `tabs.Back()` and `tabs.Forward()` walk it and raise `OnChanged` as a click does. It lasts one opening of its window: a window closed (Escape, `Hide`, the window's own close) and shown again starts a new history at the tab it shows. **Which tab rows have one:** the standard windows' tab rows (Header, Tabbed) and every row made with `panel.Tabs(key, captions, options)` (`UiTabsOptions.History`, on by default); not the overload without options, so existing pages and windows behave as before. **Keys:** Navigate back / forward (the mouse's back and forward buttons) and their keyboard rows, Navigate back / forward (keys) (Alt+Left, Alt+Right), step the target window's tabs. They are four Core keybinds in the Window scope (HUB2), remappable in the Keybinds tab. In a menu (the Mods workspace, the game's full-screen menu) they step a window under the pointer first, then the workspace. Outside menus they act only while the window holds the input: the native input mask applied is full (a `WhileShown` window, or the pointer over a hovered one), so the game's actions are blocked (`GameInputBlocker` applies `BlockAllKeys`). They therefore never clash with a game action such as Auto Run or Interrupt Ability Cast on the same button, and no key-conflict dialog is raised for them. A mod's key on the same key, in any scope, is not a clash either: the mod's key keeps it, and the window key stands aside on any press that mod key acts on; the Keybinds tab row says so in a quiet note. One press fires one of them, and never steps both a window and the workspace. **Precedence**, decided once a frame: the window drawn on top (its canvas sort order) under the pointer that has a tab history; else, only while the pointer is over no panel at all (not the Mods workspace, not another window), the window drawn on top that holds the input while shown (`WhileShown`); else the Mods workspace's history. A window at the start (or end) of its history does not take Back (or Forward): the press goes on to the workspace. A window that takes no input (`CaptureMode.Never`) is never a target, and a Mods page's own tabs are the workspace's. A controller steps a window's tabs with the triggers (LT, RT), the workspace's tab keys in the existing navigation model; the shoulders (LB, RB) are left to the game.

**Compact dropdowns.** `panel.CompactDropdown(name, values, value, labels)` → `IUiDropdown`: the caption (the kit row label, 18, shortened past 220) and 10 units on, the game's dropdown bar (`DropdownButton_Normal`, the arrow at its right) as wide as its widest option at the bar's size plus its chrome and one glyph of slack (72 to 320; a longer option is ellipsized on the bar, whole in the list). Between `BeginInline()` and `EndInline()` every compact dropdown joins one row, left to right, 24 units apart, wrapping to a further line (6 units under) only when the next would cross the row's right edge (`UiCompactChoiceLayout`). Outside an inline block each takes a row of its own at the left. A click opens the list under the bar, as the full dropdown does; Left and Right (a controller, the arrow keys while navigating) move to the neighboring dropdown instead of stepping the value; A, Enter or Space opens the list.

**Field with a button.** `input.ActionButton(caption, onAction)` (on `panel.Input` or `panel.SearchInput`) → `IUiButton`: the game's compact button at the end of the field's row, the field shortened for it (the inline row's geometry; on the settings grid the field and button share the control column). A click, or Enter in the field, runs `onAction(text)`; a grayed field or button runs nothing. When the runtime cannot join the row the button takes a row of its own under the field and Enter still runs the action.

**Centered buttons.** `button.Centered()` centers the row of compact buttons holding it (a single Return, or the inline block's buttons as a group; a group wider than the row starts at its left). It works where buttons are the game's compact buttons: a game window and a Mods page. On a plain panel a button already spans the row, caption centered.

**Key hints.** `button.KeyHint(key)` draws the key on the kit's key cap (the game's key-cap art, `SimpleMenuButton_Normal`, as the world-pick prompt draws its keys) at the button's right end, 10 units in, 26 tall, as wide as its text at 16 plus 16 (at least 26); the button keeps the cap's room (a compact button grows by it) and its caption stays centered in the rest. `key` should name a keybind this plugin registered with `ctx.Keybinds` that does what the button does (the cap shows its current key, `KeyChord.DisplayText`, and follows a remap; unbound hides it), or a game action as `game:Interact` (the game's own text for its primary key, the text the Keybinds tab shows; re-read once a second). A key's name as written (`Esc`) is drawn as is, for a key the framework itself handles. The hint only labels the button: the key must be registered and do the same thing. Empty removes it; disposing the button removes it at once. Controller glyphs are not drawn: the kit's key text comes from the keyboard bindings, and a mod keybind has no controller binding.

**A table that fills its window.** `new UiDataTableOptions { FillHeight = true }`: the table builds 30 slot rows and uses as many as fit (4 to 30): the panel's tallest viewport (at its height cap: the screen share, a standard window's place, a Mods pane's fixed height) less every other row, one slot row being 48 units with its gap (`UiTableViewport.FillRows`). The window grows to its cap and stops, and the table scrolls inside it; the panel never scrolls for it. The count is read again every 10 frames while the table is shown, so a tab with more rows above the table or a resized screen changes it. `VisibleRows` is the count until the first reading.

**Building icons.** `UiTableRow.BuildingPrefab` (a castle building's prefab id) shows that building's icon from the game's build menu when `Icon` is null and `ItemPrefab` is 0; `ctx.Castles.BuildingIcon(prefabGuid)` returns the sprite (client, main thread; null on the server). The icon is the building's own blueprint icon: the blueprint names it by asset id (`ManagedBlueprintData.Icon`), and the kit looks that id up in the game's loaded asset sections (`ManagedAssetReferences.AssetLookup`, where the build menu itself finds it), so the build menu need not be open. **Limitation:** the kit does not load game assets itself, and the game streams a blueprint's icon in for its build menu, so a building whose icon the game has not loaded since the world was joined has none yet, and its row keeps an empty, aligned icon place. Build-menu cells (`BuildMenu_StructureEntry`) are not read: a cell whose sprite is still streaming keeps the sprite it showed before (another building's) while it already names the new building. A blueprint that names no icon asset has no icon. `ctx.Castles.BuildingIconSource(prefabGuid)` and each table slot's `iconSource` read back where an icon came from: `blueprint` or `none`. A drawn row waiting for an icon asks again each frame; a scan runs only for such a waiting row or a `BuildingIcon` call that misses, at most once per 30 frames, and every full pass that finds nothing new doubles the wait (to 1800 frames, about 30 s at 60 frames a second), reset when one learns an icon. A building nobody has asked for in 7200 frames (about 2 minutes) leaves the waiting list, and one scan looks for at most 16 buildings with at most 512 game lookups, resuming where the last stopped. When an icon is learned the waiting rows redraw at once (no `SetRows` or scroll needed). A sprite the game unloaded is dropped: drawn rows (tables and checklists) check their building icons every 60 frames and draw again, and a later scan may find the sprite again. The cache is per client world.

### Tooltips

One tooltip box exists at a time, on its own overlay canvas (`progenitor-ui-core-tooltip`, sorting order 5060: above panels and an open dropdown list), with no raycaster so it never takes the pointer it describes. It is at most 340 units wide, its body wrapped, its title in the game's gold (`MenuPalette.ChipOnBorder`, the gold caption of the game's chips and sorted headers). It stays inside the panel the hovered control is on (P7b: the smallest shown panel holding the control, so on the Mods workspace a tooltip from the page never flips over the Mods list; `UiTooltipLayout.AreaFor`), on each axis where it fits there, else the canvas; always 8 units inside. A wide control (a list or table row, at least half that panel's width) gets the box under it, else over it, never beside it, so it does not cover the row it describes; a narrow one gets it 8 units to its right, top edges aligned, else to its left, else under or over it (`UiTooltipLayout.PlaceIn`, tested at 1920×1080, 2560×1440, 3840×2160 and 3440×1440, and with the workspace's geometry at 1920×1080 and 2560×1440). `handle.Tooltip(...)` puts owned PointerEnter/PointerExit rows on the control's object through the Bootstrap trampoline (`UiHoverWatch`); the watch is owner-tagged and released by `Ui.RemoveAll` and the Core teardown like any pointer watch. While the box is up it asks once a frame whether its control is still on screen and hides itself when not, so a panel closed by Escape, a rebuilt page or an unloaded plugin never leaves it behind. P7b: a term's tooltip also checks the panel its control is on (a cell row's cells keep their rects while hidden), and the box goes whenever no panel is shown at all (Escape hid every panel).

### Code area

`CodeArea(text, initial, lines)` (2–40 lines) → `IUiCodeArea`: `Value`, `OnChanged` (per edit; a programmatic `Value` write raises nothing), `SetHighlighter(Func<string, IReadOnlyList<UiTextSpan>>)` (run again on every change; a highlighter that throws is logged once and the last colors stay), `SetSpans`, `SetErrors(IReadOnlyList<UiLineError>)`, `Errors`, `LineCount`, `Describe()`.

- `UiTextSpan(start, length, color)`: character offsets into `Value`, the color as `#RRGGBB` or `#RRGGBBAA`. Overlapping spans: the later one wins and splits the earlier. Spans are clipped to the text; empty ones and ones past the end are dropped. The text is always escaped, so a `<` the player types never becomes markup.
- `UiLineError(line, message)`: 1-based lines; sorted, one per line (messages on one line joined), repeats dropped, lines past the end moved to the last line. The gutter shows a red dot (`MenuPalette.Red`) and a faint band across the line; hovering the dot shows the message in the tooltip; an orange summary row under the area reads `(!) Line N: message (+k more)` and keeps its height when empty so the page never jumps.
- Line endings `\r\n` and `\r` become `\n` on every write, the player's typing and pasting included (a field that held a `\r` gets the clean text written back, with one `OnChanged`), so the field, the colors and the gutter count the same lines. Word wrap is off: a long line is clipped at the field's right edge (no sideways scroll).

### Data table

A sortable, virtualized table of rows the caller supplies: an inventory, a list of stations, a log. The player sorts by one or several columns, hides columns, opens a row's drawer, selects rows (several with Ctrl or Shift, or with each row's check cell), and edits numbers in place; each edit waits for the caller's answer. Rows can be grouped with subtotals. However many rows there are, the table builds `VisibleRows` body rows once and rewrites their cells as it scrolls.

**Status: experimental (P7b).** Source-checked, built and game-free tested (`UiDataTableTests`); **live-verified on the test client 2026-10-14 through the bridge at 1920x1080 and 2560x1440, plus a 2x supersample capture (layout only)**; not verified: native 4K pixel density, the feel of a real-mouse drag, and controller hardware (navigation was driven through the bridge's virtual pad, not a physical controller). Reference: the Widgets page's **Table** tab ([`WidgetsDemo.Table.cs`](../samples/Progenitor.ControlsSample/WidgetsDemo.Table.cs)).

```csharp
using Progenitor.Ui;

var table = panel.DataTable("inventory", new[]
{
    new UiTableColumn("item", "Item") { Hideable = false },
    new UiTableColumn("total", "Total", UiTableColumnKind.Number) { Width = 84, Hideable = false, Subtotal = UiTableSubtotal.Sum },
    new UiTableColumn("reserve", "Reserve", UiTableColumnKind.Number) { Width = 100, Editable = true, Term = "Reserve" },
    new UiTableColumn("cap", "Cap", UiTableColumnKind.Number) { Width = 100, Editable = true },   // empty: unlimited, sorts last
    new UiTableColumn("status", "Status") { Width = 160 },
}, new UiDataTableOptions { MultiSelect = true, VisibleRows = 12 });

table.SetRows(items.Select(i => new UiTableRow(i.Key) { ItemPrefab = i.Prefab, Groups = i.Categories, Detail = i.PerPlot }
    .Text("item", i.Name)
    .Number("total", i.Total)
    .Number("reserve", i.Reserve, inherited: !i.HasOwnReserve, source: "category " + i.Category)
    .Number("cap", i.Cap)
    .Set("status", new UiTableCell(i.StatusWord) { Value = i.Severity, Tone = i.StatusTone })));

table.OnEdit(edit => server.SetRule(edit.RowId, edit.Column, edit.Clear ? null : edit.NewValue,
    ok => { if (ok) edit.Resolve(); else edit.Reject("Only clan members can change this."); }));
table.OnSortChanged(sort => prefs.Save("sort", sort));
table.OnColumnsChanged(hidden => prefs.Save("hidden", hidden));
```

| Member | Behavior |
|---|---|
| `UiTableColumn(key, caption, kind)` | `Kind` `Text`, `Number` or `Actions` (the row action buttons, see **Row actions**; `ActionSlots` buttons, 1–6, 4). `Width` (units; 0: the fill column, which takes the row's remainder, never under 120; with none the first text column fills), `Sortable` (on), `Hideable` (on; off: always shown and never dropped), `Hidden` (start hidden), `Editable` (number columns: a value box per cell), `Tooltip` (hover text on the caption), `Term` (a [`UiTerms`](#term-tooltips) term the caption explains: the circle-i marker after the caption, and hovering a sortable caption shows the term's explanation, which wins over `Tooltip`), `NullsLast` (on: empty cells sort after every value in both directions, "unlimited last"; off: empty is the smallest), `Format` (.NET number format, invariant, `#,0.##`), `NullText` (`—`), `Subtotal` (`None`, `Sum`, `Min`, `Max`, `Count`). A column is widened to fit its caption, its term marker and the drawn sort mark with an order digit, in that order after the caption. Number columns are packed from the right, so every figure, subtotals and totals included, lines up on its last digit in the game's lining figures with unspaced thousands separators (`266,400`; proportional body text, right-justified: a monospace run would space the comma). |
| `UiTableRow(id)` | `Id` is the caller's stable key; duplicate ids are refused (`ArgumentException`, nothing changes). Cells by column key: `row[column]`, `Set(column, cell)`, `Text(column, text, tone)`, `Number(column, value, text, inherited, source)`. `Icon` (a `Sprite`) or `ItemPrefab` (the item's own inventory icon), `Groups` (a row may sit in several), `Detail` (the drawer: one body line per `\n` line), `Tooltip`, `Locked` (every editable cell of the row is plain text), `Actions` / `Action(id, icon, tooltip, enabled)` (its icon buttons, see **Row actions**). |
| `UiTableCell` | `Text` (shown; empty on a number cell: the formatted `Value`), `Value` (sorts and totals; on a text column it makes the column sort by it, e.g. a status by severity), `SortText` (sort by this text instead), `Tone` (`LabelTone`), `Inherited` (drawn muted; in an editable cell the box is empty and the value is its gray hint; the row's tooltip says it in words), `Source` (where an inherited value comes from: the tooltip reads `Reserve 200: inherited from category Ore`), `Locked`. `UiTableCell.Of(text)`, `UiTableCell.Number(value, text)`. |
| `UiDataTableOptions` | `VisibleRows` (body rows built, 4–30, 12), `Selectable` (on), `MultiSelect` (off), `Grouped` (start grouped), `OtherGroup` (`Other`: the group of a row with no groups, listed last), `TotalRow` (on: a grand total line when a column has a subtotal), `DetailLines` (most drawer lines, 1–8, 4; more read `N more lines`), `EditTimeout` (15 s; zero or less: none), `ColumnChooser` (on), `Sort` (initial keys), `EmptyText`, `OverflowTooltips` (off: see below), `MinRows` (0: the fewest body rows drawn, see **Filler rows**). |
| `Columns`, `SetRows(rows)`, `Rows` | Replace every row (a refresh, e.g. a server delta); no event. Selection, drawers, waiting edits and refusal reasons follow the ids; ids that disappear leave the selection silently; the scroll position is kept (clamped); a draft whose row stays in its slot keeps its typed text. |
| `Sort`, `SortBy(column, add)`, `OnSortChanged` | The keys (`UiTableSortKey {Column, Ascending}`), first first. A header click sorts by that column alone (ascending; again: flipped); **Shift**-click adds it as the next key (again: flips that key in place). The sort is stable: ties keep the caller's order. The mark is drawn after the caption in the header's sorted gold, never a font glyph: one key, the arrow alone (up ascending, down descending); two or more, the arrow and the key's order as a small digit. |
| `HiddenColumns`, `SetColumnVisible`, `OnColumnsChanged`, `ChooserOpen` | The hidden keys (always-shown columns refuse). **Columns** at the footer's right (`Columns · 2 hidden` while some are hidden; the button is as wide as its longest caption) opens the chooser: one chip per hideable column, lit while it shows; a click flips it. Persist `Sort` and `HiddenColumns` per player from the two events. |
| `Selected`, `SetSelected(ids)`, `OnSelectionChanged` | By id. Click: that row alone. Ctrl-click (multi): add or remove it. Shift-click (multi): the rows from the last plain or Ctrl click to this one, in the order on screen, including rows scrolled out of view (Ctrl+Shift adds them). A row in two groups is lit in both. With `MultiSelect`, each row has a check cell that toggles it like Ctrl-click (the controller's way): an empty box while the row is not selected, the box with its check when it is (drawn marks `check-empty` / `check-box`). With a controller the focused row carries NAV1's frame (2 units, 3 outside the row, the top-lit color), distinct from the hover wash and the selection band. |
| `SetExpanded`, `IsExpanded`, `OnExpand` | A row with `Detail` gets the expander; its drawer lines follow it in the body (muted, one per detail line). |
| `Grouped`, `Groups`, `SetGroupCollapsed`, `IsGroupCollapsed` | Rows fold under gold group header lines (`Ores (12)`; a click or the arrow folds it), each followed by its subtotal line; groups sorted by name, `OtherGroup` last, and a group stays together under its header whatever the sort (the sort applies within each group). Subtotal, total and header lines take no selection and do not sort. A folded group keeps its header and subtotal. The grand total counts each row once, however many groups it is in. |
| `TopIndex`, `LineCount`, `VisibleRows`, `ScrollTo(id)` | The scroll window over the body lines (rows, drawer lines, group headers, subtotals, the total). |
| `OnEdit(UiTableEdit)`, `Edit(rowId, column, text)`, `PendingEdits` | See **Inline edits**. `Edit` commits as if typed (a bulk "Set reserve…" over `Selected`, the bridge). |
| `OnRowAction((rowId, actionId))`, `PressRowAction(rowId, actionId)` | See **Row actions**. `PressRowAction` presses as if clicked (a keybind, the bridge); false for an unknown or disabled action. |
| `StatusText`, `StatusReason`, `Describe()` | The footer line (`Showing 13–24 of 600 · 3 selected · 1 waiting`; the selected count only with `MultiSelect`: a single-select table reads `Showing 1–4 of 8`, its one row shown by the selection band); the last refusal; the readback. |

**Inline edits.** An editable cell is the game's value box, its number right-justified like the column's figures. A box that takes the keyboard (a click, A on a controller) selects its whole value, so typing replaces it; the first click on a box selects (no caret placed), a second click places the caret. Typing keeps a draft; leaving the field commits it: Enter (the box gives up the keyboard at once and the draft commits on the next frame), a click elsewhere, the field's row leaving its slot, the table hiding. While the keys typed into a box that just gave up the keyboard are still held, they stay the box's: they open no game menu, fire no keybind and type nowhere else. When a box that has the keyboard loses its row to a scroll, a sort, a refresh, grouping or folding, the box first gives up the keyboard (its draft commits), so the next key can never reach the row that moves into the slot; click the box again to keep typing. **Escape** (B on a controller) in the box drops the typing, shows the value again and leaves the box without committing; the press goes no further (the panel stays; the next Escape is the panel's). This is the text field's own Escape (on by default), which the game's settings value boxes are built on; it is not verified live against them. The kit's other value boxes (a settings text row, a Prompt's field) have no Escape of their own. Empty text means *clear to the inherited value* (`UiTableEdit.Clear`); otherwise the text must be a number (`1,250.5`, or in the player's culture), else the refusal line says `"x" is not a number.` and the cell returns to its value. An unchanged value commits nothing. A commit raises `OnEdit(UiTableEdit {RowId, Column, OldValue, NewValue, Clear, Text, State, Reason})`; the cell then shows a turning spinner before the new value in the pending tone (a waiting clear reads `Inherited`, gray), takes no typing, and the footer counts it (`· 1 waiting`). Answer once, on the main thread: `edit.Resolve(display?)` writes the new value into the row (the caller's `UiTableRow`, so a later sort sees it; `display` replaces the formatted text, which after a clear is the inherited value to show), `edit.Reject(reason)` keeps the old value and shows `Item, Column: reason` in orange under the table and in the row's tooltip. `UiTableEditState`: `Pending`, `Resolved`, `Rejected`. With no answer within `EditTimeout` the table rejects it with *No answer from the server.*; a late answer is ignored. With `EditTimeout` zero or less nothing expires. A `SetRows` while an edit waits keeps the spinner, and the answer lands on the refreshed row; a `SetRows` without that row drops the edit (`Rejected`, *The row left the table.*, no refusal line), and a late answer to it does nothing. A second edit of a waiting cell is refused (*Waiting for the server to answer the last change.*); a locked cell or row is plain text.

**Scrolling.** The body is `VisibleRows` slot rows made once; a scroll rewrites only the cells whose text, tone or visibility changed, so nothing is laid out again and nothing flickers. The step is a whole line: the wheel over the body moves two lines per notch through the framework's wheel claim, and at either end hands the notch to the panel. The scroll bar, built exactly as the page's own bar (plain images: a faint light track and a light gray handle, 8 units wide), sits in the rows' right inset (18 units no cell enters), spanning the body's slots; dragging it or clicking its track moves the window to the nearest whole line. With a controller, Up on the first slot and Down on the last scroll by one line instead of leaving the table; the right stick over a slot scrolls about 15 lines a second. The focus stays on the slot (the content moves under it); selection, drawers and waiting edits are by id and survive any scroll. A row's tooltip closes whenever the body moves (wheel, stick, controller step, scroll bar, `ScrollTo`, a sort, a refresh). Group members and subtotals are computed once per change of rows, sort, grouping, folding or a resolved value, not per scrolled frame.

**Row actions** (source-checked, built and game-free tested, `UiTableRowActionsTests`; not verified live). A column of kind `UiTableColumnKind.Actions` (one per table; it never sorts and is always shown: `Hideable` is ignored on it, so the column chooser never offers it and a narrow row never drops it; it is exactly `ActionSlots` buttons wide: 28 units a button, 8 between, its caption cut if longer) draws each row's `UiTableRow.Actions` left to right as the kit's box-less icon buttons, the same buttons as the ordered list's up, down and remove: a drawn mark in the glyph gold (`UiRowActionIcon` `Locate`, `Up`, `Down`, `Remove`, `Chest`, `Check`, `View` (an eye)), brighter under the pointer, faint when `UiRowAction.Enabled` is false. A click, or A on a controller (each button is a navigation stop like the expander), raises `OnRowAction(rowId, actionId)` and does not select the row; a faint button does nothing. Hovering a button shows its `Tooltip` through the kit's tooltip, a disabled one's too; moving from the button back onto the row shows the row's own tooltip again. The table changes nothing itself: the caller moves or removes the row and calls `SetRows`, which also redraws which arrows are enabled (the first row's up and the last row's down are the caller's to disable). Give every row the same actions in the same order, so each icon keeps its own column; a row with fewer leaves blanks as wide. Group headers, subtotals, the total, drawer lines and filler rows draw no buttons. The readback lists each slot's actions (`{id, icon, enabled, tooltip, cell}`); `mods_ui widget_click` reaches a button by its cell, `a:<column>:<n>`. Drag-to-reorder is not offered on a table: use the arrows (the ordered list has a drag grip).

```csharp
var table = panel.DataTable("inputs", new[]
{
    new UiTableColumn("name", "Name") { Hideable = false },
    new UiTableColumn("type", "Type") { Width = 110 },
    new UiTableColumn("plot", "Plot") { Width = 90 },
    new UiTableColumn("status", "Status") { Width = 120 },
    new UiTableColumn("actions", "", UiTableColumnKind.Actions),
}, new UiDataTableOptions { VisibleRows = 8, MinRows = 8 });

table.SetRows(inputs.Select((s, i) => new UiTableRow(s.Id) { BuildingPrefab = s.Prefab }
    .Text("name", s.Name).Text("type", s.Type).Text("plot", s.Plot).Text("status", s.Status)
    .Action("locate", UiRowActionIcon.Locate, "Find in the world")
    .Action("up", UiRowActionIcon.Up, "Move up", enabled: i > 0)
    .Action("down", UiRowActionIcon.Down, "Move down", enabled: i < inputs.Count - 1)
    .Action("remove", UiRowActionIcon.Remove, "Remove")));
table.OnRowAction((id, action) => { Apply(id, action); table.SetRows(BuildRows()); });
```

**Filler rows** (source-checked, built and game-free tested, `UiTableRowActionsTests`; not verified live). `UiDataTableOptions.MinRows` (0 to 30, at most the rows in use, `VisibleRows` or a `FillHeight` table's count) keeps the body at least that many rows tall: with fewer lines, blank rows right under them (the separator rule, every column's place kept, no text, no icon, no buttons) fill the rest, so a tab with two rows is as tall as one with ten. A filler row takes no click, selection or tooltip, draws no hover wash and no check cell (a `MultiSelect` table draws a blank as wide), and the controller and keyboard navigation pass over it: Down from the last row does not land on a filler. A slot that becomes a row again (more rows) takes all of that back. With no rows at all the body is fillers alone and the footer says `EmptyText`. Scrolling is unchanged: with more lines than `VisibleRows` there are no fillers. The readback says `minRows` and `fillers`; `slots` lists lines only.

**Inherited values.** Never by tone alone: an editable inherited cell is an empty value box whose gray hint is the inherited value (an own value is typed text in the box), and the row's tooltip names every inherited value in words (from `UiTableCell.Source`). A locked inherited cell is muted text and has the tooltip's words. In `mods_ui widget_read` a value box reads `text` (its value, empty when inherited), `placeholder` (its gray hint) and `shown` (what the screen shows: the value, else the hint, e.g. `"10"`).

**Drawn marks.** The sort arrows with their digit and the spinner are textures drawn once per Core generation, tinted, never a raycast target, children of the cell's text so they clip, hide and go with it, and released at the Core unload. A mark after right-justified text keeps its own room in the text's margin, so the caption moves left instead of under the mark.

**Known limits.** Header tooltips and term explanations need a sortable column (only those captions take the pointer); a non-sortable caption with a term still shows the marker. Multi-key sorting needs Shift (a controller sorts by one key; `Sort` sets several). The Columns button is on the footer, not the header row. Drawer lines carry the separator rule like rows, and subtotal lines are 40 units like every body line.

#### Data table layout rules

1. Every body line is one settings-grid row (40 units, the separator rule). The header, the footer with the Columns button, the refusal line and the chooser are rows of their own around the body: only the body's slots scroll, and the scroll bar spans the body alone; the header is cell for cell as wide as the body.
2. Columns run in the caller's order: up to and including the fill column they pack from the left, after it from the right, so numbers line up on their last digit and every column sits under its caption (a fill column after a number column is not honored, an earlier text column fills instead). Text is left-justified and numbers right-justified wherever their column is packed; a left-justified text column after a right-justified one keeps a 12-unit lead before its text and caption (part of its width), so captions never butt. Text never wraps (every body line is one fixed-height row): give a text column the width of its longest expected value. With `UiDataTableOptions.OverflowTooltips` a value its column still cuts is also shown whole in the row's tooltip. It is off by default: the kit's tooltips are asked for (`UiTableRow.Tooltip`, `handle.Tooltip`), and an automatic box repeating the row under the pointer is noise. A row's own `Tooltip`, its inherited values and its refused edits always show.
3. A row too narrow for every column drops hideable columns from the right; always-shown columns never drop.
4. Fixed parts: the check cell, the expander and each row action button are box-less 28-unit cells, the icon well 30; an actions column is its buttons and the 8-unit gaps between them, and its header caption spans exactly that. A column that has no icon (or no drawer) on any row is hidden on every row, and a line that does not use one draws an equal blank, so the columns line up on every line. A row whose item has no icon (none given, or the game has none for its `ItemPrefab`) draws no well, only that blank.
5. A sorted column's caption is drawn in the header's sorted gold, like its sort mark. A text column with a `Value` but empty text draws nothing (the value only sorts).
6. Checked at 560, 760, 1108 and 1248 units (`UiDataTableTests`).

### Checklist (CHECKLIST1)

Choosing several things out of a long list to act on together: the chests, stations and belts to add to a group, out of a few hundred. It replaces `Picker` with `Multi` for long lists: every row has the game's checkbox, the count is always in view, a filter dropdown sits beside the search, rows carry an icon and the mod's own columns, and **Select shown** never touches a row the filter or the search hides.

**Status: experimental.** Source-checked, built and game-free tested (`UiChecklistTests`, `UiNavTests`); **not live-verified**: nothing of it has been seen in the game. Reference: the Controls sample's **Gallery** → **Lists** tab → **Checklist window** ([`GalleryChecklist.cs`](../samples/Progenitor.ControlsSample/GalleryChecklist.cs)): 300 synthetic rows in three sections, four filters, a default sort, grayed rows and a preview stub. The icon preview button and the stable height (`PreviewIcon`, `KeepHeight`) are also game-free tested only.

```csharp
using Progenitor.Ui;

var list = panel.Checklist("add-chests", chests.Select(c => new UiChecklistEntry(c.Id, c.Name)
    {
        Section = c.Kind,                       // "Storage", "Stations", "Belts": a folding heading each
        Filters = c.Tags,                       // which filter options list it ("chests", "near", …)
        Note = c.Note,
        Keywords = new[] { c.PrefabName },      // searched, never shown
        BuildingPrefab = c.PrefabGuid,          // the build-menu icon
        DisabledReason = c.InGroup ? "Already in this group" : "",
    }
    .Text("plot", c.Plot)
    .Number("distance", c.Distance, c.Distance + " m")),
    new UiChecklistOptions
    {
        Columns = new[]
        {
            new UiTableColumn(UiChecklistEntry.LabelColumn, "Name") { Hideable = false },
            new UiTableColumn("plot", "Plot") { Width = 90 },
            new UiTableColumn("distance", "Distance", UiTableColumnKind.Number) { Width = 110 },
            new UiTableColumn(UiChecklistEntry.NoteColumn, "Note") { Width = 170 },
        },
        Sort = new[] { new UiTableSortKey("distance") },
        Filters = new[] { new UiChecklistFilter("chests", "Chests"), new UiChecklistFilter("near", "Within 30 m") },
        ConfirmFormat = "Add {0}",
        Preview = true,
        FillHeight = true,
    });

list.OnConfirmed(ids => { group.Add(ids); window.Hide(); });
list.OnCancelled(() => window.Hide());
list.OnPreview(id => { list.Suspend(); window.Hide(); camera.ShowChest(id, onDone: () => { window.Show(); list.Resume(); }); });
```

| Member | Behavior |
|---|---|
| `UiChecklistEntry(id, label)` | `Id` is the caller's stable key (duplicates refused, `ArgumentException`). `Section` (the group heading it is listed under, as on a picker entry; entries without one sit under `OtherGroup` while others have one), `Note`, `Keywords`, `Filters` (filter ids), `Icon` / `ItemPrefab` / `BuildingPrefab` (as on a data table row), `DisabledReason` (grays the row, never checked, shown as its tooltip), `Tooltip`. Cells by column key: `Text(column, text, tone)`, `Number(column, value, text)`, `Set(column, cell)`, `entry[column]`. The `label` column (`LabelColumn`) shows `Label` and the `note` column (`NoteColumn`) shows `Note` unless the entry sets its own cell. **A snapshot:** an entry is read when `SetEntries` (or the factory) takes it; change an entry, then call `SetEntries` again. |
| `UiChecklistOptions` | `Columns` (`UiTableColumn`s, the data table's: kind, width, sortable; no `label` column: a `LabelCaption` column, `Name`, comes first), `Sort` (the mod's default sort), `Filters` (`UiChecklistFilter(id, label)`), `DefaultFilter` (empty: All), `AllFilterLabel` (`All`; empty: no All option, the first filter applies), `FilterCaption` (`Show`), `Placeholder`, `Selected` (initial ids), `ConfirmFormat` (`Confirm ({0})`), `CancelCaption`, `Buttons` (on: Cancel and Confirm under the list), `Preview`, `PreviewIcon` (`UiRowActionIcon.View`, the eye; null: a text button), `PreviewCaption` (`View`: the icon's tooltip, or the text button's caption), `CollapsibleGroups` (on), `OtherGroup` (`Other`, the data table's rule), `VisibleRows` (4–30, 12), `FillHeight`, `KeepHeight` (on: [the height stays](#checklist-height)), `EmptyText`. |
| `Query`, `Filters`, `Filter`, `FilterOpen`, `OnFilterChanged` | The search and the filter. Setting `Query` rewrites the field; a new query or filter scrolls back to the top. |
| `Shown` | The entries the filter and the search show, in display order, without those under a folded heading: exactly what **Select shown** acts on. |
| `Sort`, `SortBy(column, add)`, `OnSortChanged` | The data table's header: a click sorts by the column (again: flipped), Shift-click adds a key; the drawn arrow and the gold caption. The sort applies within each section. |
| `Selected`, `IsSelected(id)`, `Select(id, selected)` (the picker's), `SetSelected(ids)`, `OnSelectionChanged` | The checked ids in check order, shown or not. Disabled and unknown ids are refused. |
| `SelectShown()`, `ClearSelection()` | The status line's **Select shown** (only `Shown`, enabled rows; shown while it has work) and **Clear** (everything; shown while something is checked). One event each; `SelectShown` returns how many it added, `ClearSelection` returns nothing (as the picker's). |
| `Groups`, `SetGroupCollapsed`, `IsGroupCollapsed` (the data table's names) | The entries' `Section` headings, in the order of their first entry in the caller's list; a click on a heading (or its arrow) folds it. A folded heading keeps its count. |
| `SetEntries(entries)` | A refresh: checked ids that remain and can still be checked stay checked, others drop out silently (no event). The search, the filter, the sort and the folds stay; the scroll keeps the entry that was at the top by its id (the line number, clamped, when that entry left). |
| `Confirm()`, `OnConfirmed(ids)`, `Cancel()`, `OnCancelled`, `ConfirmText`, `CanConfirm` | Confirm reads `ConfirmFormat` with the count (`Add 3`), gray and inert at 0. |
| `Preview(id)`, `OnPreview(id)`, `OnRowHovered(id)` | With `Preview`, each row ends with a preview button, and a right-click on the row raises it too. The button is the data table's box-less icon button (`PreviewIcon`, the eye by default; any `UiRowActionIcon`), a fixed 28 units wide, brighter under the pointer, with `PreviewCaption` as its tooltip (moving back onto the row shows the row's own tooltip), and a controller stop on the row as the table's actions are. `PreviewIcon = null` draws the earlier box-less text button with `PreviewCaption` instead (sized from an estimate of the caption, so a long caption can be cut). A double-click is two clicks: two flips. A disabled row can be previewed. `OnRowHovered` gets the id under the pointer, empty when it leaves. |
| `Suspend()`, `Resume()`, `Suspended` | Hide the checklist keeping everything (search, filter, checks, sort, folds, scroll) while the mod shows its own preview; it takes no clicks, Escape or Enter meanwhile. Hiding the widget or its window keeps the state as well. Setting `Visible` while suspended (a tab switch) shows nothing; `Resume` applies the last request. When the controller focus was on one of its rows, `Resume` puts it back on the previewed row, scrolled into view. |
| `TopIndex`, `LineCount`, `VisibleRows`, `ScrollTo(id)`, `StatusText`, `Describe()` | The scroll window, the status line (`120 of 300 shown · 3 selected (1 not shown)`), the readback (`ui_read widget <key>`). The readback also has `previewIcon` (the mark's name, `eye`; empty for the text button), `keepHeight`, `bodyRows` (slot rows drawn, fillers included), `fillers`, `bodyTarget` (the height in rows the fillers make up to), `fullLines` (the lines unfiltered and unfolded), `chipRows` (the open filter's rows), and `room` (`{viewport, content}`: the panel's last layout in kit units, null before one). |

**Search.** Case- and accent-insensitive; every word of the query must appear in the entry's label, note, section, keywords or one of its text cells (number cells are not searched). The order is the sort's, not a match rank.

**Keys and controller.** Every row is a NAV1 stop: Up and Down move (and scroll the body at its first and last slot), the right stick scrolls, **Space** or **A** flips the focused row (Shift held: the range), **Enter** on a row confirms (it never flips one), **Escape** or **B** closes the open filter first, then cancels while `OnCancelled` has a handler (with none, the press goes on and closes the window). Both happen only while the checklist's panel is the topmost Escape target (`Ui.IsEscapeTarget`: the window the press would close, or no window shown at all, and no dropdown list open), as a world pick does; with a window stacked over it, that window closes first and the checklist keeps its open filter. Subscribe `OnCancelled` only where cancelling closes the checklist's screen (its own window): on a Mods page a handler would keep Escape from closing the menu. No key is read directly: Enter is NAV1's own Submit, told apart from Space (`UiNav.EnterKey`), and Escape is the framework's Escape path.

<a id="checklist-height"></a>**Height.** With `KeepHeight` (on by default) the body draws all its rows: `VisibleRows`, or with `FillHeight` as many as fit the window (read every 10 frames), but never more than the lines its entries make with no filter, no search and nothing folded, so a short or empty list stays short. Rows its lines do not fill up to that height (a folded section, a filter or a search that shows fewer) draw blank, the data table's `MinRows` filler rows: no check, no click, no hover wash or tooltip, and the controller passes over them. Opening the filter's chips takes their rows out of the body (the slot rows in use down to their fewest, 4) and closing it gives them back; a new fill count while the chips are open is taken as the closed count less the chips, so closing never restores a stale count. So a fold, an unfold, a filter or a search never resizes the window; the list scrolls inside it. **Why the height followed the content:** a floating window (`GameWindow`) is laid out as tall as its rows, and the body hid every slot row its lines did not fill, so the content and the window shrank on a fold; `FillHeight` only chose how many rows to build and use, and a hidden row took no room. `KeepHeight = false` gives the earlier behavior (the body as tall as its lines). Only `SetEntries` (more or fewer entries) changes the height; `FillHeight` follows the window's room as before. A list of 4 lines or fewer can still grow by the chips' rows when the filter opens (the body never uses fewer than 4 slot rows).

**Range select.** Shift-click (or Shift with Space) sets every shown, enabled row from the last plain-clicked row to this one to this row's new state; headings, disabled rows and rows the filter, the search or a fold hides are never touched. When the anchor row is no longer shown, the click is a plain flip.

#### Checklist layout rules

```
┌ Add to group ───────────────────────────────────────────────────────────────┐
│ [ Search chests, stations and belts          × ]    Show [ All          ▾ ] │  search row
│   ( All ) ( Chests ) ( Stations ) ( Belts ) ( Within 30 m )                 │  only while the filter is open
│          NAME ▲                    PLOT    DISTANCE   NOTE                  │  header, small caps
│ » Storage (100) · 3 selected                                                │  section heading, gold
│ [✓] [ic] Wooden chest 4            North        6 m   near the gate    ◉   │  checked: gold band
│ [ ] [ic] Wooden chest 9            East        12 m                    ◉   │  ◉: the eye icon button
│ [ ] [ic] Wooden chest 2            South       19 m                    ◉   │  grayed, reason on hover
│ » Crafting (100)                                                            │
│ [ ] [ic] Smithy 3                  West        22 m   upstairs         ◉   │
│ …                                                                       ▐   │  scroll bar in the right inset
│ 120 of 300 shown · 3 selected (1 not shown)        Select shown   Clear     │  status line, muted
│                        [ Cancel ]   [ Add 3 ]                               │  the dialogs' buttons
└─────────────────────────────────────────────────────────────────────────────┘
```

1. The search row is the game's value box filling the row, then the filter's caption and the game's dropdown bar at its right end (the compact dropdown's sizes: the caption up to 220, the bar as wide as the longest option, 72 to 320). The bar opens the options as chips under the row, the selected one lit; a chip picks and closes them.
2. The header and the body are the data table's: the same column widths and packing (text up to and including the fill column from the left, the rest from the right, so numbers right-justify on their last digit and text stays left), small-caps captions with the drawn sort mark, every body line one 40-unit settings-grid row with the separator, the 18-unit right inset for the scroll bar.
3. Every entry line starts with the game's checkbox (24 units, `CellKind.Check`; the kit's framed box until the art is ready), then the icon well or its equal blank, so the check column and the captions line up on every line; the header keeps blanks of the same widths. A checked row also carries the Mods list's gold entry band. A disabled row draws its box half-transparent and its text faint.
4. A section line hides the entry cells and shows the fold arrow and the heading in gold (`Storage (100) · 3 selected`).
5. With `Preview`, each entry line ends with the preview button: the data table's 28-unit box-less icon button (the eye, the glyph gold), or with `PreviewIcon = null` the box-less **View** text button; the header keeps a blank as wide.
6. With `KeepHeight`, filler rows under the lines keep every column's place and draw nothing; they are as tall as any line, so the body's height never changes on a fold, a filter or a search. The body is never taller than the unfiltered, unfolded list.
7. The status line is muted text with the two links at its right; Cancel and Confirm are the dialogs' button row (the game's popup buttons, Cancel left, centered), Confirm sized for a four-digit count.
8. Checked at 560, 760, 1108 and 1248 units, with the text and the icon preview button (`UiChecklistTests`).

**Live check (not run).** On the test client, with the Controls sample deployed: `keybind_press` the sample's Ctrl+F7 opener (or `mods_ui menu open`) to the Gallery page, `widget_click {"label":"gallery-tabs:tabs/tab2"}` (Lists), `widget_click {"label":"Checklist window"}`. Then `ui_read {"what":"widget","key":"gallery-checklist"}`: `entries 300`, `groups` Storage, Crafting, Logistics, `sort` distance ascending, `status` `300 entries · none selected` (**screenshot** at 1920x1080 and 2560x1440: the checkbox column, the captions over their columns, numbers right-aligned, nothing under the scroll bar). `widget_click gallery-checklist:search/filter`, then `widget_click gallery-checklist:filters0/f:4` (Within 30 m): `filter near`, fewer `shown` (**screenshot**). `ui_input {"action":"focus","row":"gallery-checklist:search","cell":"search"}`, `ui_input {"action":"type","text":"chest"}`: only chests. `widget_click gallery-checklist:status/select-shown`: `selected` lists them; clear the search and the filter: the status says how many of them are checked (`… selected`), and nothing else was checked. `ui_input click` on `gallery-checklist:row3`, then on `row8` with `"modifiers":["shift"]`: the range. `ui_input hover` on a grayed row and `ui_read tooltip`: its reason. `ui_read widget gallery-checklist`: `previewIcon eye`; **screenshot**: each row ends with the eye, nothing cut. `ui_input hover` on `gallery-checklist:row2` cell `view` and `ui_read tooltip`: `View`. `nav {"action":"focus","row":"gallery-checklist:row2","cell":"view"}`: the frame is on the eye. `widget_click gallery-checklist:row2/view` and `ui_input {"action":"click","row":"gallery-checklist:row4","button":"right"}`: the log has `checklist preview …`, the list hides and **Back to the list** shows; press it: the list as it was (`top`, `selected`, `filter`, `query`). `nav {"action":"focus","row":"gallery-checklist:row1"}`, `nav press a` (flips), then `ui_input {"action":"key","key":"Enter"}`: `checklist confirmed N`, the window closes. Height: reopen the window, read `bodyRows` and `room.content`, then `widget_click gallery-checklist:row0/fold` (fold Storage), the same for Crafting and Logistics: `bodyRows` and `room.content` unchanged, `fillers` > 0 once the lines no longer fill the body; unfold: `fillers 0`; **screenshot** folded and unfolded (the window's frame in the same place). Type `smithy 3` in the search, then open the filter: `room.content` unchanged, `bodyRows` plus `chipRows` as before. Escape over a stacked window: open the filter, then the Hub window (Ctrl+F8) over the checklist window and press Escape (`ui_input key Escape`): the hub closes, the checklist stays with its filter open; a second Escape closes the filter, a third cancels. Clean up: close the window; remove the Controls sample from both reload folders after the batch.

### Master-detail

`panel.MasterDetail(key, options)` → `IUiMasterDetail`. A list of entries on the left and the selected entry on the right, the way the game's options menu pairs its category list with the selected category (a Belts / Categories screen, a profile editor). An optional tab row above it switches between independent lists. The caller fills the right side when an entry is selected.

**Status: experimental (P7b).** Source-checked, built and game-free tested (`UiMasterDetailTests`); **live-verified on the test client 2026-10-14 through the bridge at 1920x1080 and 2560x1440, plus a 2x supersample capture (layout only)**; not verified: native 4K pixel density, the feel of a real-mouse drag, and controller hardware (navigation was driven through the bridge's virtual pad, not a physical controller). Reference: the Widgets page's **Layout** tab ([`WidgetsDemo.MasterDetail.cs`](../samples/Progenitor.ControlsSample/WidgetsDemo.MasterDetail.cs)).

```csharp
var md = panel.MasterDetail("belts", new UiMasterDetailOptions
{
    Tabs = new[] { "Belts", "Categories" },
    NewCaption = "+ New belt",
    EmptyText = "No belts yet.",
    DetailEmptyText = "A belt is an ordered set of containers.\nSelect a belt, or make one with + New belt.",
});
md.SetTexts(1, "+ New category", "No categories yet.", "A category is a set of items.");
md.SetEntries(0, belts.Select(b => new UiMasterEntry(b.Id, b.Name) { Note = b.Members.Count + " chests", Term = "Belt" }));
md.OnSelected(id => md.SetDetail(id.Length == 0 ? Array.Empty<UiDetailLine>() : new[]
{
    UiDetailLine.Title(name, new UiDetailButton("rename", "Rename")),
    UiDetailLine.Line("3 chests · 2 plots"),
    UiDetailLine.MemberList(members),                       // drag, arrows, expand, remove
    UiDetailLine.Actions(new UiDetailButton("add", "+ Add…"), new UiDetailButton("pick", "Pick")),
    UiDetailLine.Heading("Used by", 4, term: "Used by"),
    UiDetailLine.Link("use-1", "Input of Furnace 1", "list entry"),
    UiDetailLine.Actions(new UiDetailButton("locate", "Locate all"), new UiDetailButton("delete", "Delete")),
}));
md.OnAction(a => { /* a.ActionId: "rename", "add", "pick", "open" (a link, a.LineId names it), "new" */ });
md.OnReorder(m => SaveOrder(md.SelectedId, m.Order));
md.OnRemove(item => RemoveMember(md.SelectedId, item.Id));
```

| Part | Vanilla counterpart and borrowed art |
|---|---|
| Tab row | The `Tabs` widget: the Install tab's filter chips (the game's popup button, `SmallerButton_*`, the gold plate on the selected one). |
| Rows | Settings-grid rows 40 units tall with the settings list's separator rule; no whole-row fill, so the panel's ground shows through both halves. |
| Search field | The game's settings value box (`SimpleBox_Normal`) with the kit's clear button, as the picker's. |
| Selected entry | The Mods list entry's band (the gold at 0.14) with its 3-unit gold stripe at the left, behind the master half only; the caption keeps its bright text color. |
| Hovered entry | The Mods list entry's hover wash on the master half while the pointer is left of the divider; a hovered selected entry gets the brighter selected band and keeps the stripe. The new-entry line takes the hover wash too. |
| Controller focus | NAV1's own focus marker on the row, separate from the band, so focus, hover and selection never look alike and can combine. |
| New-entry line | The caption in the pale link blue, as an action rather than an entry. |
| Divider | A 1-unit rule in the settings list's separator color down the center of an 8-unit column, continuous from row to row. A drawn image, not a font glyph. |
| Counts | A master entry's `Badge` right-aligned in tabular figures in the client gold, so the digits line up; a member's badge is the kit's count badge, packed at the row's right edge before the glyph buttons. |
| Detail buttons | The game's popup button (`SmallerButton_*`), sized to the caption. |
| Links | The kit's link (`#9fc7e8`, underlined), as the picker's suggestion. |
| Member rows | The ordered list's row: box-less icon buttons drawn as marks in the muted gold (grip, up, down, remove; brighter under the pointer, faint where they do nothing), the game's dropdown arrow as the expander, the game item icon in the kit's icon well, a count in the client gold. Every row keeps the scroll bar's 18-unit band free at its right end, so the remove button never sits under the bar. |

| Member | Behavior |
|---|---|
| `UiMasterDetailOptions` | `Tabs` (captions; null: no tab row), `Rows` (6–30, 14), `MasterFraction` (0.2–0.45, 0.32), `Search` (on), `SearchPlaceholder` (`Search`), `NewCaption` (empty: no new-entry line), `EmptyText` (the master list's one line while empty), `DetailEmptyText` (the detail while nothing is shown; `\n` starts a line). |
| `UiMasterEntry(id, title)` | `Title` (settable), `Note` (muted, after the caption; searched), `Badge` (a short count at the entry's right), `Icon` / `ItemPrefab`, `Tooltip`, `Keywords` (searched, never shown), `Term` (a [`UiTerms`](#term-tooltips) term: the caption gets the marker and hovering the entry's half shows the term's explanation, anchored on the caption, instead of `Tooltip`; an undefined term leaves the plain tooltip). Duplicate ids are refused. |
| `Tabs`, `Tab` | The tab row (`IUiTabs`, or null) and the tab shown. Each tab keeps its own entries, selection, query, detail, member list and both scroll positions; switching restores them. |
| `SetEntries(entries)`, `SetEntries(tab, entries)`, `Entries`, `Matches` | Replace a tab's entries (a selected id that left is unselected, no event); the entries matching the query, in the caller's order. |
| `SelectedId`, `Select(id)`, `OnSelected(id)` | The selection (null: none). A click on an entry, the controller's A on its row, or `Select` raises `OnSelected` (empty string for none). Selecting scrolls the entry into view. |
| `Query`, `OnSearch(text)` | The search text (typing or `Query` raise `OnSearch`). |
| `SetDetail(lines)`, `SetDetail(tab, lines)`, `Detail` | Replace the detail (no event). The detail scrolls to its top when it now describes another entry than the last call did, and keeps its place on a refresh of the same entry. |
| `SetTexts(tab, newCaption, emptyText, detailEmptyText)` | Per-tab texts (null keeps one). |
| `Members`, `MoveMember(id, index)`, `RemoveMember(id)`, `SetMemberExpanded(id, open)` | The member list in its current order (it changes itself before `OnReorder` / `OnRemove`; the `UiDetailLine` the caller passed keeps the old order). |
| `Rows`, `MasterTop`, `DetailTop`, `ScrollMaster(n)`, `ScrollDetail(n)` | The rows made and each half's first drawn line; scroll a half by lines (clamped; false when it did not move). |
| `OnAction(UiDetailAction)` | A detail button (`ActionId` = the button's id, `LineId` = `title` or `actions`), a link (`ActionId` = `open`, `UiDetailAction.Open`; `LineId` = the link's id), or the new-entry line (`LineId` = `master`, `UiDetailAction.MasterLine`; `ActionId` = `new`, `UiDetailAction.New`). `Tab` and `EntryId` (the selection then) come with it. A disabled button raises nothing. |
| `OnReorder(UiListReorder)`, `OnRemove(UiListItem)` | The member list's events, as the ordered list's. |
| `Describe()` | `{kind: masterdetail, key, tab, selected, query, rows, masterTop, detailTop, master [{kind, id, text, selected}], detail [{kind, id, text}], members, drag, rowKeys}`. |

**Detail lines** (`UiDetailLine` factories; `Kind` is a `UiDetailLineKind`): `Title(text, buttons…)` (the name with up to three buttons at the right), `Line(text, tone = Muted)` (one line; a longer text ends in an ellipsis), `Heading(text, count, term)` (gold; `Heading("Used by", 4)` reads `Used by (4)`; `term` marks the heading with a glossary term that explains it when the detail half of its row is hovered), `Link(id, text, note)`, `Actions(buttons…)` (up to three, from the detail's left edge), `MemberList(items, reorder, remove, emptyText)` (one per detail; a second, or duplicate member ids, throw and change nothing), `Blank()`. `UiDetailButton(id, caption) { Enabled }`.

**Members.** One row per member, rewritten in place. The order, the drawers and the drag are the ordered list's own: drag a member by its grip (the slot whose center is nearest the pointer is the target; the rows show the order the drop will leave, the held member's caption in the gold; a release the grip did not see drops on the next frame), or use the arrows; the top member's up and the bottom one's down are faint. The expander opens a drawer of up to three muted lines (the item's `Detail`, one line per `\n`) under the member, indented to its caption. Remove is faint on a member that is not `Removable`. A drag reaches only the members drawn now; the detail does not scroll while one runs (use the arrows for a longer move).

**Search.** The picker's matching (case- and accent-insensitive) on the title, with the note and the keywords as keywords. The list keeps the caller's order while filtering, so entries never jump as the player types. The search row's right shows the entry count, `2 of 4` while filtering, or the drawn range (`4–16 of 20`) while the list scrolls. No match reads `No match for "text".`; the new-entry line stays at the end.

**Empty states.** With no entries, the master list shows `EmptyText` (keep it short: it is one line of the list) and the new-entry line; the detail shows `DetailEmptyText`, one line per `\n` (the place for the longer explanation). An empty member list shows its `emptyText`.

**Terms.** Rows are reused slots, so each row keeps one term binding for its master caption and one for its detail heading, re-synced on every render (a filtered or scrolled list moves the marker with its entry; an empty slot, the search row and a non-heading line are unmarked). The row's hover goes to the binding of the half the pointer is on. A term wins over an entry's `Tooltip`.

**States.** Hover, selection and controller focus are three different looks that can combine: the hover wash on the master half, the selected band with its gold stripe, the brighter selected band when both, and NAV1's focus marker on the row. Nothing is drawn as a glyph: the band, the stripe and the divider are images.

**Input.** A master entry is its row's own click: the pointer anywhere on the entry's half, or the controller's A on the row. A click on the detail half of a row is not a master click, so clicking a detail line's text does nothing. Every detail action is a button cell, so Left and Right on a row move from the entry to the detail's buttons and back; A presses. Up on the first entry row or Down on the last row scrolls the half the focus is in before the focus leaves; the right stick scrolls the focused half, and at that half's end hands the stick to the panel. The mouse wheel over either half scrolls that half by two lines; at its end the notch goes to the panel. Any scroll (wheel, stick, Up or Down, the scroll bar) hides the row's tooltip; the pointer's row is looked at afresh on the next frame. A scroll or any change rewrites only the cells whose text, tone or visibility changed. The detail gets the widget scroll bar in the rows' 18-unit right band, which no cell enters. No key is read.

**Rows and capacity.** The widget makes `Rows` rows once (one more for the tab row) and rewrites them in place; a longer list or detail scrolls inside its half. Nothing is added to or removed from the panel after it is built, so a change never lays the page out again or loses its scroll. Hiding the widget (its tab) ends a drag; disposing it (with its panel) releases its rows, the drag tick, the wheel claims and the scroll bar.

#### Master-detail layout rules

These are the numbers in `UiMasterDetailLayout`; `UiMasterDetailTests.Every_row_fits_and_the_divider_lines_up_at_every_checked_width` resolves every row the widget draws at 560, 760, 1108 and 1248 units, with and without master icons and counts, and fails when a cell leaves its row, two overlap, one is under its floor, the master caption drops under 120, a detail cell crosses the divider, or the divider is not at the same x on every row.

1. A kit panel has no columns, so each row carries both halves: the master cells, an 8-unit divider, then the detail cells, with 18 units at the row's right end left for the scroll bar. Every row has the same master cells at the same widths (a master column is shown or hidden for all rows at once), so the divider is at one x on every row.
2. The master half: a lead (12, room for the band's 3-unit stripe), the icon (30, only while some entry has one; a row whose entry has no icon, and the new-entry line, show an empty pad of the same width instead of an empty well), the caption (0.7 of `MasterFraction` of the row, clamped to 120–300), the note (0.3 of it, clamped to 40–140), the count (three tabular characters right-aligned at the monospace pitch plus the text's insets, 36 units, so `99+` is never cut; only while some entry has one). With the default 0.32 the caption is 125 units at 560 and 280 at 1248.
3. The detail takes the rest. Its cells are packed from the divider; a member's count badge and glyph buttons are packed from the row's right edge in the ordered list's fixed order (badge, up, down, expand, remove); the one text or link cell takes the space between, which also puts a title line's buttons at the right edge.
4. The selection band spans from the row's left edge to the divider, the row's height, nothing in the gap between rows (one entry lit, never a block). The search row has no band.
5. A detail row that does not fit drops, in order: the member note and count, the grip, the member icon, then the expander. On the 560-unit window with master icons and counts a member row keeps its caption, arrows and remove.
6. The split is fixed, not draggable: a draggable divider would re-place every row's cells while dragging and has no controller equivalent. Choose `MasterFraction` per screen.
7. Detail buttons are sized to their caption (at least 96 units). A line holds up to three; on the 560-unit window keep it to two short captions (`Locate all`, `Delete`) and give a third its own line.
8. Rows are 40 units with the separator rule, the master entries and the detail lines on the same grid.
9. Master counts are at most three characters (`99+`), right-aligned.

### Chips

A set of chips that wrap onto lines: **item chips** (a game item's icon, a caption and a remove glyph inside one chip: a category's members) and **plain chips** (a caption, optionally removable: a container's belts, combinable category filters), with an optional square **add chip** after the last one. Each line is a kit cell row; every chip and every remove glyph is a button cell, so the controller reaches them like any other cell (NAV1).

**Status: experimental (P7b).** Source-checked, built and game-free tested (`UiChipsTests`); **live-verified on the test client 2026-10-14 through the bridge at 1920x1080 and 2560x1440, plus a 2x supersample capture (layout only)**; not verified: native 4K pixel density, the feel of a real-mouse drag, and controller hardware (navigation was driven through the bridge's virtual pad, not a physical controller). Reference: the Widgets page's **Lists** tab ([`WidgetsDemo.Chips.cs`](../samples/Progenitor.ControlsSample/WidgetsDemo.Chips.cs)).

```csharp
using Progenitor.Ui;

var belts = panel.Chips("belts", new[]
{
    new UiChip("ores", "Ores belt") { Tooltip = "Iron Ore, Copper Ore, Sulfur." },
    new UiChip("wood", "Wood belt"),
}, new UiChipsOptions { Add = true, AddTooltip = "Add this container to a belt.", EmptyText = "On no belt." });
belts.OnClick(chip => OpenBelt(chip.Id));          // a press opens the belt
belts.OnRemove(chip => LeaveBelt(chip.Id));        // the chip is already gone
belts.OnAdd(() => PickBeltToJoin());               // then belts.SetChips(...) with the new list

var members = panel.Chips("members", new[]
{
    new UiChip("crystal", "Crystal") { ItemPrefab = -257494203, Note = "240" },   // "Crystal · 240" with its icon
});

var filters = panel.Chips("cats", categories, new UiChipsOptions { Toggle = true, Remove = false, MaxLines = 2 });
filters.OnClick(_ => ApplyFilter(filters.SelectedIds));   // combinable: each click flips one
filters.OnMore(hidden => ShowAllCategories(hidden));       // the "+N more" chip
```

| Member | Behavior |
|---|---|
| `UiChip(id, label)` | `Id` is the caller's stable key; duplicate ids are refused (`ArgumentException`, nothing changes). `Label` (settable), `Note` (drawn after the caption as `Label · Note`: a stock count), `Icon` (a `Sprite`) or `ItemPrefab` (a game item's prefab id; the client shows that item's own inventory icon) — either makes it an item chip (`IsItem`); `Removable` (on); `Selected` (the initial state; the set keeps it from then on); `Tooltip`; `Term` (a [`UiTerms`](#term-tooltips) term: the caption gets the circle-i marker and hovering the chip shows the term's explanation in place of `Tooltip`; set one or the other). |
| `UiChipsOptions` | `MaxLines` (1–8, 3), `MaxPerLine` (2–16, 8), `Toggle` (a click flips the chip's selection before `OnClick`; off), `Remove` (draw the remove glyph on removable chips; on), `Add` (the add chip; off), `AddText` (`+`), `AddTooltip`, `EmptyText` (`None yet.`), `RowWidth` (0 = measure the row; see below). |
| `Chips`, `Shown`, `Overflow`, `Capacity` | Every chip in order; how many are drawn; how many are counted on the `+N more` chip; `MaxLines × MaxPerLine`. |
| `SetChips(chips)` | Replace every chip, no event; selection from each chip's `Selected`. The way to add a chip, and to undo a removal the plugin refuses. |
| `Remove(id)` | Remove a chip and raise `OnRemove`; false when it is not in the set. |
| `SetSelected(id, on)`, `IsSelected(id)`, `SelectedIds` | Selection without an event (an active belt, a filter restored from settings); the ids in chip order. |
| `OnClick(UiChip)` | A chip's caption was pressed (in `Toggle` mode after its selection flipped, so the handler reads the new state). |
| `OnRemove(UiChip)` | After a chip was removed by its glyph or `Remove`: the set has already changed (as the ordered list). |
| `OnAdd()` | The add chip was pressed. The set does not add anything itself. |
| `OnMore(IReadOnlyList<UiChip>)` | The `+N more` chip was pressed: the chips it counts, in order. Its tooltip lists the first eight. |
| `Describe()` | `{kind: chips, key, chips [{id, label, shown, selected, removable, item, term, line, slot}], shown, overflow, capacity, rowWidth, lines, toggle, add {line, slot}, more {line, slot, text}, rowKeys, cellKeys}`; a chip not drawn has line and slot -1. |

**Wrapping.** Chips are packed in order onto lines, each chip on the line after the previous one if it fits beside it and the line has fewer than `MaxPerLine` chips, else at the start of the next line. When the chips need more than `MaxLines` lines, as many as fit are drawn, in order, followed by a `+N more` chip (raises `OnMore`); the add chip is always last. A single chip wider than the whole row is shortened to the row and its caption ends in an ellipsis; hovering it shows the full caption as the tooltip's title. Each line row is made once (`MaxLines` rows of `MaxPerLine` slots) and rewritten in place: a removal, `SetChips` or a resize never rebuilds the page or loses its scroll; unused lines are hidden.

**Row width.** A widget is not told its row's width, so the set measures it from its first line row on screen. Until a caption has been drawn the set packs for 560 units (the narrowest checked row), packs again as soon as the row reads a different width, and checks again every 20 frames while its panel is shown (a resized window). A reading under 240 units (a row read before its first layout) is ignored. `UiChipsOptions.RowWidth` fixes the width instead and turns the measuring off.

**One chip.** A chip is one button cell on the chip art, as wide as the whole chip. The icon well and the remove glyph are overlay cells inside it, at the chip's left and right ends with a 6 and a 4 unit inset, and the caption is kept between them. A chip with no icon and no glyph keeps the button's symmetric pad, so its caption is centered on the whole chip art. A press on the icon is a press on the chip; a press on the glyph removes. The remove button is a box-less 20-unit button with no caption whose face is the drawn remove mark (`UiIconMarks`, the cross the ordered list's remove button uses, centered in the cell): no font glyph that could be missing, cut or underlined. It is muted gold, brighter under the pointer, and light cream (`MenuPalette.ChipOnText`) on a selected chip's gold plate. The button stays the click target and the NAV1 stop. A chip whose item has no prefab id and no icon is a plain chip: no empty well.

**States.** Selected, hover and controller focus are separate and combine: selected is the gold plate under the caption, filling the chip art inside its rim (3 units from every edge, `UiChipsLayout.PlateInset`; the tabs and the Install filter chips share it), and the remove mark turns light cream; hover is the art's own hover sprite, on the plate or not; the controller focus is the NAV1 frame, a 2-unit line 3 units outside the focused chip or glyph, drawn over everything.

**Controller and keyboard.** Left and Right walk a line in reading order (a chip, its remove glyph, the next chip), Up and Down move between lines, A (Space or Enter) presses. X is not used (a chip has no default to reset to); remove a chip with its glyph. The add and `+N more` chips are ordinary stops.

**Terms.** A chip with a `Term` is marked like any explained control: the marker after its caption and the term's line in the tooltip box while the chip is hovered (it counts as a hover of the term). The marker follows its chip when the set re-packs; each chip slot keeps one marker binding and rewrites it on every render. A chip with a term is wider by the marker's room (16 units), which a caption shortened to the row keeps too. The add and `+N more` chips never carry a term. Every marker is released with the set.

**Item icons.** An item chip's icon is the game's own inventory sprite for `ItemPrefab`, resolved on the client on every render (cached per world); on the title screen or the server the well stays empty. `Icon` wins over `ItemPrefab`.

#### Chips layout rules

These are the numbers in `UiChipsLayout`. `UiChipsTests.Every_line_fits_its_cells_at_every_checked_width` packs a mixed set (item chips, notes, a caption longer than any row, non-removable chips) at 560, 760, 1108 and 1248 units with 2, 5, 8 and 16 chips a line, with and without an add chip, and fails when a cell leaves the row, two chips overlap, a cell is squeezed under its floor, a chip lands anywhere but where the packer put it, an icon or a glyph is not inside its own chip at its inset, or a caption does not fit between its margins. `Chip_height_follows_the_art_slices_and_the_icon` pins the heights; `An_overlay_sits_at_either_end_of_its_target_with_its_inset` pins the overlay rule.

1. A chip is one button cell: its width is the caption measured per character at 16 units (`UiChipsText`: the book serif's advances, times 1.15 for the game face, so no short word is cut: `No_short_word_is_cut_at_any_checked_width`) plus one glyph of slack, plus the margins: 8 each side for a plain chip; 36 on the left with an icon (6 inset, the 24 well, 6 gap); 26 on the right with a remove glyph (4 inset, the 20 glyph target, 2 gap). A chip with a term adds 16 to the right margin. Whole units, never narrower than the chip is tall (the add chip is square). Chips on a line are 8 apart. The column model never squeezes a chip: the packer decides the wrap, and a caption too long for the row is shortened with an ellipsis by the packer, never by the runtime.
2. The chip art's top and bottom slices take 9 units each. Chips are 36 tall in a set with an item chip (the icon well is 24), else 31 (the Install filter chip's height); the add and `+N more` chips match the set.
3. Lines are plain kit cell rows 40 tall with a 4-unit gap above every line after the first (no separator rule, no row hover: only the chips light); the empty text is a muted fill cell after the last slot. A render shows and hides its lines inside one layout batch.

### Layout rules

These are the numbers in `UiWidgetLayout`, `UiPickerLayout`, `UiTabsLayout` and `UiTooltipLayout`; `UiWidgetsTests.Every_widget_row_fits_its_cells_at_every_checked_width` resolves every widget row at 560, 760, 1108 and 1248 units (the floating window's row and the Mods pages') and fails when a cell leaves the row, two cells overlap, or a cell is squeezed under its floor.

1. Every widget row is a kit cell row 40 units tall on the panel's grid with the settings list's separator rule; a widget lines up with the settings rows above and below it.
2. Small actions are box-less glyph buttons 28 units wide (the grip 24), packed at the row's right edge in a fixed order — badge, up, down, expand, remove — so their columns line up from row to row whether a row has a badge or not (`Glyph_buttons_line_up_from_row_to_row`).
3. Icons are 30 units in the kit's icon well. The caption takes the remainder (never under 120); the muted note is the first cell a narrow row drops.
4. A drawer row continues the row above it, its text indented to the caption's start, 21 units a line plus 12.
5. The grip, up, down, remove and check buttons have no caption: their face is a drawn mark (`UiIconMarks`, a coverage texture 13 units tall centered in the 28-unit column) in the muted gold `#b89a5a`, brighter (`#e6c98a`) under the pointer and faint (`#5a4e3c`) where the button does nothing on that row. The game's font lacks the triangles, and a text glyph on a box-less button is underlined; a mark can be neither. The cell stays a button: its click and its NAV1 stop are unchanged.
6. Rows are made once, in page order, and rewritten in place; a slot past the item count is hidden. A change never rebuilds the page or loses its scroll.
7. Text comes in through the kit's own row text paths and is escaped; nothing a caller passes becomes markup.

## HUD: notifications, locate and pick in world

`ctx.Hud` (`PluginHud`) acts on the game's HUD and world rather than on a panel. Everything a call starts belongs to the plugin and ends on every unload reason without running a callback (a queued notice is dropped, a ping disappears, an armed pick is cancelled). Client only: on the server each call logs once and returns a handle that does nothing. **Status: experimental** — source-checked, built and game-free tested (`UiNoticeTests`, `UiLocateTests`, `UiWorldPickTests`). Notify (the game's banner, the framework band it falls back to, the band's notice kinds) and pick in world (one-shot, repeat, Escape precedence): **live-verified on the test client 2026-10-14 through the bridge at 1920x1080 and 2560x1440, plus a 2x supersample capture (layout only)**; not verified: native 4K pixel density, the feel of a real-mouse drag, and controller hardware (navigation was driven through the bridge's virtual pad, not a physical controller). **Locate is not verified live.**

```csharp
ctx.Hud.Notify("Deposit list saved", "3 containers", UiNoticeKind.Success);

ctx.Hud.PickInWorld(
    picked =>
    {
        if (!picked.IsCurrent) return;              // validate the world epoch before using the entity
        ctx.Hud.Locate(picked.Entity, "Target", seconds: 10);
    },
    onCancelled: () => ctx.Log.LogInfo("pick cancelled"));
```

### Notifications

`Notify(text, detail = "", kind = Info, seconds = 0, prefix = true)` → `IUiNotice` (`State`: `Queued`, `Showing`, `Done`, `Dropped`; `Dispose` takes a queued notice out). `UiNoticeKind`: `Info`, `Success`, `Warning`, `Error`. The text is shown as `<Mod Name>: text` (the [message prefix](modules.md#message-prefix); not twice, and not with `prefix: false`). The four-argument form plugins were built against before the prefix still loads and prefixes.

- **Vanilla counterpart:** the game's own center announcement banner (the one achievements use), through `AnnouncementHUDParent.ShowBasicAnnouncement(header, subHeader)`, an ordinary instance method taking two `LocalizationKey`s by value. The texts are registered as mod strings; an empty detail hides the banner's second line. The banner shows every kind the same.
- **The game's banner has no queue** (a second call restarts its animation and overwrites the text), so the framework queues: one notice at a time across all plugins, first posted first shown; the same text within 4 s is dropped; a plugin may post 6 per 10 s; at most 8 wait (the oldest is dropped). A notice never overwrites a banner the game is playing: it waits while the banner is busy or the player has hidden the HUD (nothing is drawn while the HUD is hidden, not even the band), and one still unshown 30 s after it was posted is dropped. **While the game's full-screen menu is open, or the Mods workspace is open in a world**, the banner is out of sight behind it; a notice posted then (a mod's page button in the workspace) follows the game: it waits, and the game's banner is not asked to play until both have closed and a 0.75 s settle has passed (`UiNoticeRules.MenuHolds`, `MenuSettleSeconds`), so it plays where the player sees it. The menu is read from the game's menu state, not from the HUD canvas (live 2026-10-14: the HUD canvas stays enabled under the menu, and a banner played unseen). The time the menu stays open does not count toward the 30 s (the notice is still dropped 10 minutes after it was posted; `UiNoticeRules.MaxHeldSeconds`). Game-free tests: `UiNoticeTests.A_notice_posted_while_the_games_full_screen_menu_hides_the_HUD_shows_when_the_menu_closes`, `The_menu_or_the_workspace_in_a_world_holds_the_banner_and_a_short_settle_follows`. At the title screen there is no banner and no game menu: the framework band shows the notice over the workspace at once. A queued notice from an earlier world is dropped when the world changes; one already on screen finishes. Each drop logs `Ui: ctx.Hud.Notify dropped a notice unshown (<reason>): "<text>".` `seconds` 0 is the banner's own animation length; a longer value holds the slot longer. A vanilla achievement that fires during a notice still replaces it on the banner.
- **Fallback band:** with no banner at all (title screen, the HUD not built yet, a refused call) the notice is drawn on a framework band in the same style: a dark top-center band between thin rules in the game's body font, the detail line muted. `kind` marks the band (P7b, live 2026-10-14: a Warning looked like an Info): Info keeps the game's gold on quiet 1-unit rules; Success (the palette's green, `OkText`), Warning (the framework's warning orange-amber, `OrangeText`) and Error (the palette's red) draw 2-unit rules in their accent at 0.9 and a small round mark in that accent before the header (`UiNoticeBand.Accent`, `RuleThicknessFor`, `MarkShown`). The game's own banner shows every kind alike. One log line names the path in use.
- **A banner the game switches off** (P7b, live 2026-10-14): after the workspace closed, the banner was asked to play (logged) but never seen; its object was inactive 1.5 s later. The game's own HUD system switches the announcement objects off on frames where it shows no day or night announcement (read from its update in the game's metadata; why its own achievements survive that is not settled). The framework writes nothing to the game's objects: 0.3 s after asking the banner to play it reads whether the banner object is still active (`UiNoticeRules.CheckBanner`), and when it is not, the same notice is drawn on the framework band for the rest of its slot and later notices in that world go to the band directly (`Ui: ctx.Hud.Notify draws notices on the framework band (the game's banner was switched off 0.3 s after it was asked to play; this world uses the band).`). A new world tries the banner again.

### Locate

`Locate(Entity target, label = "", seconds = 10)` / `Locate(float3 position, label = "", seconds = 10)` / `Locate(float3 position, string label, double seconds, float arrivalDistance, bool showOnMaps)` → `IUiLocatePing` (`Active`, `Dispose`).

| Parameter | Meaning |
|---|---|
| `target` | A client-world entity. Its `Translation` (or `LocalToWorld`) is read every frame, so the marker follows it. |
| `position` | A fixed point in the game's world space, meters, as `Translation` holds it: `x` east, `y` up (height), `z` north. Pass the real floor height of the thing you mean: the marker floats 1.6 m above `y`, and a `y` a floor too high draws the marker in the air above it. |
| `label` | The text before the distance, up to 60 characters. Empty: the distance alone. |
| `seconds` | Lifetime; 0 or less: until disposed. Clamped to 1..3600. |
| `arrivalDistance` | Meters. A positive value ends the ping (`arrived`) once the player's character is within that straight-line (3D) distance of `position`; 0 never ends it that way. It is never shown. |
| `showOnMaps` | Also draws a diamond on the world map and the minimap for the ping's lifetime (they do not touch the player's own map marker). |

- The game has no world ping, so this is framework UI drawn each frame over the game camera (`Camera.main`): a gold diamond marker 1.6 m above the target and, while the target is off screen or behind the camera, an arrow on the screen edge pointing the way to turn. It pulses for its first second and fades in and out.
- **The label** is `<label> · 22 m`: the straight-line (3D) distance from the player's character to the target point (`position` itself, not the marker 1.6 m above it), rounded to whole meters (`<1 m` under a meter, `1.2 km` from 999.5 m). It is the real distance, recomputed every frame, never `arrivalDistance`. The character is found by its `LocalCharacter` tag and re-found in every new world (the next world reuses entity ids, so a character kept from the last one would be some other entity there).
- **Placement.** The target point plus 1.6 m goes through the camera's viewport projection and is scaled to the window the overlay canvas covers, so the marker sits on its target whatever size the game camera renders at.
- The marker and arrow are drawn into textures (as the kit's radio circles are), not font glyphs, so no glyph can be missing. `UiLocate` places them: the edge clamp keeps a margin at every resolution and aspect (tested at 1920×1080, 2560×1440, 3840×2160, 3440×1440), and a target behind the camera flips its arrow.
- The Dev bridge's [`locate`](bridge-ops-ui.md#locate) op places a ping at given coordinates and reads back every ping's last drawn frame (screen position, label, distance, the character position it was measured from, the camera).
- A ping ends when its time is up (0 or less: until disposed), the entity no longer exists, the client world changes or goes away, the player's character has been missing for 3 s (a kick or a disconnect; a respawn survives), or the plugin unloads. At most 8 at once; a ninth ends the oldest. The `Entity` overload takes a client-world entity.
- **A target the server names:** `ctx.Hud.TryFindByNetworkId(index, generation, type, out Entity entity, out string reason)` turns the network id words the server sent (`ctx.Inventory.TryGetNetworkId`, [below](#resolving-a-target-on-the-server)) into this client's entity, for `Locate`. One pass over the client's networked entities (`NetworkId`, read with the typed accessor): call it once per reply, not every frame. False with a reason on malformed words, on the server, or when no entity of the client world carries the id (destroyed, or not streamed to this client: a building far away may not be). **Experimental**, built; not verified live.

### Pick in world

`PickInWorld(onPicked, onCancelled = null, options = null)` → `IUiWorldPick` (`Active`, `State`: `Idle` / `Armed` / `Picked` / `Cancelled` / `TimedOut` / `Disposed`, `Cancel()`).

- **What the player does:** a band above the HUD shows the prompt (`UiWorldPickOptions.Prompt`, default `Pick a container or station`) with the game's key caps for the Interact key (the player's actual binding, read from the game) and Escape. The next interact on a matching target is captured: its panel does not open, and `onPicked` receives a `UiWorldPickResult` (`Entity`, `Kind`, `Menu`, `MenuType`, `WorldEpoch`, `IsCurrent`, `Suppressed`, and the picked entity's network id: `NetworkId0..2`, `NetworkIdIndex`, `NetworkIdGeneration`, `HasNetworkId`, the same words and 12-byte size guard as `VanillaPanelTarget`; send them to the server and resolve them with `ctx.Inventory.TryResolveNetworkId`, [as for a panel target](#resolving-a-target-on-the-server)). Escape cancels; an interact on another kind (a door, a waygate) behaves normally and the pick stays armed.
- **Options:** `Kinds` (`UiWorldPickKinds`: `Containers`, `Stations`, `Other` — castle heart, prison, servant, trader and the like; default containers and stations), `TimeoutSeconds` (0: none), `SuppressPanel` (true; false lets the panel open and still reports the pick), `CloseGameWindows` (false; see below).
- **Close the open window first** (`UiWorldPickOptions.CloseGameWindows = true`; source-checked, built and game-free tested, `UiWorldPickCloseWindowsTests`; not verified live): when the pick arms, the game window the player has open is closed as Escape would close it: a container's or a station's window (with the inventory beside it), another interact window a pick could capture (the castle heart, a prison cell and the like), or the character inventory on its own. Any other menu (the map, the full-screen menu) stays open, and a pick that cannot arm (no world) closes nothing. It is closed with `HUDMenuManager.CloseMenu(World)`, the close the framework already makes for the map and the build menu and the one the game's own interact HUD makes when an interaction ends, so the window's own close tells the server the interaction ended. It runs once, after the readiness check and before the pick arms, for a one-shot or a repeating pick alike; the log says `Ui: world pick #<n> closed the open game window (menu <type>) before arming.` **Nothing is reopened** when the pick ends: the player opens the window again if they want it. A remote [container preview](modules.md#container-preview) window counts as an open container window: it closes too, and its owner then gets `Closed` with `ContainerPreviewCloseReason.WindowClosed`. Use it when the pick starts from a button on a container or station window (or from a mod panel shown over the inventory), so the player's next interact reaches the world and Escape belongs to the pick.
- **Placement** (P7b): the band's bottom edge sits 200 units above the screen's bottom edge on the design canvas (at least 196): the HUD's health readout and the buff row above the action bar reach about 180 units up. One-shot and repeating picks share the placement.
- **Repeat** (`UiWorldPickOptions.Repeat`, P7b, experimental, `UiWorldPickRepeatTests`): keep picking. Each matching interact runs `onPicked` and the pick stays armed (`State` stays `Armed`, the same handle) until Escape, `Cancel()`, the timeout, leaving the world, a newer pick, or dispose. `IUiWorldPick.PickCount` counts the targets delivered (0 or 1 for a one-shot pick). The band reads `<prompt> · 3 picked` once it has a count (the separator muted, the count in the gold), and the words after the key caps are `Add` and `Finish` instead of `Pick` and `Cancel`; a repeating pick that kept the default prompt shows `UiWorldPickOptions.DefaultRepeatPrompt` (`Pick containers or stations`). **Escape and `Cancel()` mean "finished"**: the pick ends as `Cancelled` and `onCancelled` runs; read `PickCount` there (a repeating pick never ends as `Picked`). The timeout counts from the last pick (an idle timeout). The same target interacted with twice in a row within ten seconds is reported once; its panel still stays closed. The Escape precedence is unchanged: a repeating pick takes Escape only when it is topmost.
- **One pick at a time, process-wide.** A second `PickInWorld` cancels the first (its `onCancelled` runs) before arming. `onCancelled` runs for Escape, `Cancel()`, replacement, a timeout (`TimedOut`), leaving the world (the client world or the local character it was armed with is gone: a disconnect, a kick, a world change; checked every frame), or a call with no world. `Dispose`, plugin unload and Core unload end it with no callback.
- **Seam:** a Core-owned `Before<OpenHUDMenuSystem>` system hook, installed only while a pick is armed (and 3 s after, so a repeated open of the same interaction stays closed). It reads each `OpenMenuEvent` and its init data (`ContainerMenu.InitData.TargetContainer`, the station menus' `TargetWorkstation`) and first sends `StopInteractingWithObjectEvent` for the interaction's buff, the way the framework's other client events are sent. Only when that send went out are the two client-only menu entities destroyed, so the panel never opens, and further opens of the same interaction swallowed until the client no longer sees the interact buff (10 s at most). With no buff, or a send that failed, nothing is swallowed: the panel opens and `onPicked` gets the target with `Suppressed = false`, so a panel never pops open later unprompted. It never skips the system, adds no Harmony patch and never uses `ctx.UnsafeHarmony`. Escape comes through the framework's consumed-Escape path (the band is a kit panel), not a key read.
- **Escape precedence** (first match wins): an open dialog or dropdown list; a key capture; the game's focused chat box; any other shown Progenitor panel, the Mods workspace included; an open game menu or panel; then the pick. Escape cancels the pick only when nothing above it is open; otherwise the press closes that thing as usual and the pick stays armed (from the open Mods workspace: the first Escape closes the workspace, the second cancels the pick). The band takes the close key only while the pick is topmost, and it never blocks gameplay input or clicks (none of its graphics is a raycast target), so the player can attack, move, aim and zoom while it shows. How: the band is a `UiCaptureMode.Never` panel, so the pointer never routes it and it can never ask for the full mask (not even with no readable pointer, a controller player's case); while the pick is topmost it holds a close-key-only context (`UiInputCapture.AcquireCloseKey`), which takes the game's Back and Menu_Close and nothing else, and it lets go of it as soon as anything else owns Escape. The `Ui: input context registered … mask=live (BlockAllKeys + Move/Aim + Zoom …)` line names what the context can apply; the line after it, `Ui: input context order=599 applies scope=…`, names what it applies now: `CloseKeyOnly` while only the pick holds it. One physical Escape is one action: it arrives as the Escape key and then its paired Back, and a press whose Escape hid panels (the Mods workspace) also takes its Back, so the pick, which stood back while the workspace was over it, does not take the same press once the workspace is gone; the next Escape is the pick's. The band never scrolls (`Ui.NoScrollPanel`: its ScrollRect and bar are off from the first layout pass, and the scroll-bar read-back skips it). The caller's prompt is drawn as plain text, at most 900 units wide with an ellipsis. **Look** (one-shot and repeating picks alike, `UiWorldPickBandLook`): the framework notice band, centered and at least 980 units wide (wider only for a long prompt): a black ground at 0.84 between 1-unit gold rules along its top and bottom edges, the ground and both rules fading to clear over the outer sixth at each end (one drawn fade texture).
- **Limits:** the interact animation still plays; picking needs the Interact key released first, like closing a panel; the waygate and blood altar menus carry no target and cannot be picked.

### Widgets acceptance procedure (run 2026-10-14)

Run on the test client 2026-10-14 through the bridge at 1920x1080 and 2560x1440, plus a 2x supersample capture for layout. Not covered: native 4K pixel density, the feel of a real-mouse drag (step 3), controller hardware (the controller steps used the bridge's virtual pad), and Locate (step 9).

Every step runs through the dev bridge without OS input and without the game window's focus ([UI input without focus](bridge-ops-ui.md#ui-input-without-focus-bridgeui1)): `mods_ui` opens and reads the workspace, `ui_input` hovers, clicks (with Shift or Ctrl), drags, scrolls, focuses fields, types and sends Escape and Enter at the UI kit's own input layer, `ui_read` reads widget state (`widget`, `tooltip`, `focus`, `tips`, `terms`, `panels`, `state`), `nav` drives the controller path, `display` and `screenshot` cover resolutions and captures. The MCP tools are `progenitor_ui_input`, `progenitor_ui_read`, `progenitor_nav`, `progenitor_display` and `progenitor_screenshot`. Two things stay outside the bridge and are marked **in world**: walking to a chest for the world pick, and whether the game's own pause menu opens on Escape (the UI-layer Escape does not reach the game; check it with `key {"action":"tap","keys":"Escape","background":true}`). The bridge ops are live-verified on the test client 2026-10-14 ([what was verified](bridge-ops-ui.md#ui-input-without-focus-bridgeui1)); a step that fails because an op misbehaves is an op defect to report, not a widget failure.

Deploy Core and `Progenitor.ControlsSample`. The Widgets page has five tabs: `tab0` **Lists** (ordered list, chips), `tab1` **Input** (pickers, code area), `tab2` **Table**, `tab3` **Layout** (master-detail), `tab4` **HUD** (notifications, pick in world, Locate, explained words, tip cards). Resolutions: `display {"action":"set","width":1920,"height":1080,"mode":"windowed"}`, then 2560x1440; for 3840x2160 on a monitor that cannot hold it, `screenshot {"superSize":2}` on the 1920x1080 window (the layout is the same at 16:9; see [display](bridge-ops-ui.md#display)). `display {"action":"restore"}` at the end (Dev unload restores a set left pending). Change resolutions at the title screen; in a world `display set` needs `"allowInWorld": true`, for an authorized in-world capture only. Where a step says **screenshot**, take `screenshot` (add `"panel"` or `"row"` for a region) at each of those. Run `ui_input {"action":"release"}` after any step that left the pointer over the page. The last step removes the sample.

1. Title screen: `mods_ui {"action":"open","site":"standalone"}`, `{"action":"select","plugin":"progenitor.controlssample","page":"custom","group":"Widgets"}`, `{"action":"widget_read","pane":"content"}`. Expect the cell rows `demo-tabs:tabs` (chips `tab0` to `tab4` captioned `Lists`, `Input`, `Table`, `Layout`, `HUD`; `badge0` `4`; `badge1` `1` in the server red; `badge2` `99+`), `demo-list:row0` to `row3` (titles in the sample's order, the notes, badges `12` and `3`), `demo-list:drawer0` to `3` hidden, `demo-list:footer` hidden. **Screenshot** at each resolution: no clipped or overlapping text; the rows line up with the settings grid.
2. `widget_click {"label":"demo-list:row0/down"}`: the log has `Controls sample: widget list reorder chest-a 0->1 = furnace-a,chest-a,chest-b,sawmill-b`; `widget_read` shows `row0` titled `Furnace input`. `widget_click demo-list:row1/expand`: `drawer1` visible with the detail text. `widget_click demo-list:row3/remove` (`Sawmill output`, not removable): nothing happens. `widget_click demo-list:row2/remove`: `widget list remove chest-b`, `badge0` reads `3`.
3. Drag `row0`'s grip below the last row: `ui_input {"action":"drag","row":"demo-list:row0","cell":"grip","to":{"row":"demo-list:row3","fy":0.1},"steps":20,"keepDown":true}`, then **screenshot**: the held row lit, the rows closed up live; `ui_read state` shows `pressed` on the grip. `ui_input {"action":"up"}`: one `list reorder` line on release, and `ui_read {"what":"widget","key":"demo-list"}` shows the new order.
4. `widget_click demo-tabs:tabs/tab1`: the picker rows show and the list rows hide. `ui_input {"action":"focus","row":"demo-picker:search","cell":"<field key from widget_read>"}` (or `ui_input click` on that cell), then `ui_input {"action":"type","text":"cr"}`: `demo-picker:status` shows the match count and the `use` link `Use “Crystal”`; `widget_click demo-picker:status/use`: `widget picker = crystal`. In a world, `demo-picker:line1` shows the Crystal inventory icon (**screenshot**).
5. Hover a grayed row (the second `Furnace`, *Not your clan's castle*): `ui_input {"action":"hover","row":"demo-picker:line<n>"}`, then `ui_read {"what":"tooltip"}`: `shown true`, `title` gold-titled reason, `rect` beside the row (**screenshot**). At a window edge (`ui_input hover` with `"x"` near the right edge of the screen over a row there, or a 1280x720 `display set`) the tooltip `rect` stays inside `ui_read panels`' `screenWidth` × `screenHeight`.
6. Still on `tab1`, under the pickers: the code area shows line numbers 1 to 10, colored keywords, strings and comments, a red dot and band on line 4, and the summary `(!) Line 4: Expected "to" before the target.` (**screenshot**). `ui_input focus` on the code area's row, move the caret to line 4's gap with `ui_input key` (`Down`, `End`, `Left`), `ui_input {"action":"type","text":" to"}`, then `ui_read {"what":"widget","key":"demo-code"}`: the colors follow (**screenshot**), and fixing line 4 clears the dot and `badge1`.
7. In a world with a castle chest: `widget_click demo-tabs:tabs/tab4`, then **Notify info** four times quickly (`widget_click {"label":"Notify info"}` ×4): one `Ui: ctx.Hud.Notify shows notices on the game's announcement banner …` line, and the notices show one after another on the game's banner (**screenshot**). At the title screen the same press logs `Ui: ctx.Hud.Notify draws notices on the framework band (…)`.
8. **In world:** **Pick in world**, walk to the chest and press Interact (the character's movement and Interact are game input: `bot` or `key` with `background:true`): the chest's panel does not open; the log has `Ui: world pick #N picked …`, `Ui: world pick sent StopInteractingWithObjectEvent for buff …` and `Controls sample: widget pick = Container ContainerMenu (…)`. Interact again: the chest opens normally. Arm again and `ui_input {"action":"key","key":"Escape"}`: `Ui: world pick #N cancelled by Escape.` The game's pause menu not opening is checked separately with `key tap Escape` (**in world**).
9. **Locate the picked target**: one `Ui: ctx.Hud.Locate draws pings as framework UI over the game camera …` line; the marker over the chest with its distance (**screenshot**). Turning the camera away is game input (**in world**: `key hold` on the camera keys, or `bot_look`): the edge arrow points back; after 10 s it fades.

#### Data table acceptance

1. `widget_click {"label":"demo-tabs:tabs/tab2"}`, then `widget_read {"pane":"content"}`: `demo-table:header` (cells `c:item` … `c:status`, captions in their own case), `demo-table:row0` … `row11` visible, nothing past `row11`, `demo-table:footer` `status` = `Showing 1–12 of 600`, `columns` = `Columns`, `demo-table:reason` hidden. Rows `row0`/`row1` are Crystal and Coarse Thread, with the game's own item icons in a world (empty icon wells at the title screen) (**screenshot**). **Screenshot** at each resolution: no clipped caption or value, the numbers right-aligned under their captions, the header lined up with the rows, nothing under the scroll bar.
2. `widget_click {"label":"demo-table:header/c:total"}`: log `Controls sample: widget table sort = total+`; `row0`'s `c:total` is the smallest; a drawn gold up arrow follows the `Total` caption (**screenshot**; `widget_read` keeps the caption `Total`). Again: `table sort = total-`, the arrow points down. Shift-click Item: `ui_input {"action":"click","row":"demo-table:header","cell":"c:item","modifiers":["shift"]}` (or `widget_click {"label":"demo-table:header/c:item","modifiers":["shift"]}`): `table sort = total-,item+`; `ui_read {"what":"widget","key":"demo-table"}` `sort` lists `total` descending then `item` ascending and `sortMarks` gives `Total` the down arrow with `1`, `Item` the up arrow with `2` (**screenshot**: both in the same gold).
3. `widget_click demo-table:footer/columns`: `demo-table:columns0` (and `columns1` at narrow widths) visible with chips `col:category` … `col:status` (no Item, no Total). `widget_click demo-table:columns0/col:here`: `table hidden = here`, the `c:here` cells hidden on the header and every row, `columns` reads `Columns · 1 hidden`. Click it again: shown.
4. `widget_click demo-table:row0/check`, then `row2/check`: `table selected = 2 (…)`, both rows lit with the Mods list's gold entry band and its left stripe and the drawn check mark in the check cell, the footer `· 2 selected`. Hover a third row: `ui_input {"action":"hover","row":"demo-table:row4"}`: a light wash, distinct from the band (**screenshot**); `nav {"action":"focus","row":"demo-table:row5"}`: NAV1's frame, distinct from both (**screenshot**). Range: `ui_input click` on `demo-table:row1`, `ui_input {"action":"wheel","row":"demo-table:row6","notches":-2}`, then `ui_input {"action":"click","row":"demo-table:row9","modifiers":["shift"]}`: the whole range lights (`ui_read widget demo-table`); `ui_input {"action":"click","row":"demo-table:row5","modifiers":["ctrl"]}`: that one goes off.
5. `widget_click demo-table:row0/expand`: `table expand item-… = open`; `row1`–`row3` show the drawer lines (`Plot A: …`, `Plot B: …`, `Most in: …`) in the muted tone.
6. Typing, all through `ui_input`: `{"action":"focus","row":"demo-table:row0","cell":"e:reserve"}` (`ui_read focus` shows the field), `{"action":"type","text":"40"}`, `{"action":"key","key":"Enter"}`: `table edit item-… reserve 40`, `table resolved … = 40`, the cell shows 40. Focus, type `41`, Enter: `table waiting …`, the cell shows the drawn turning ring before `41` in the pending yellow (**screenshot**), `ui_read widget demo-table` `pending` has the edit, the footer `· 1 waiting`; press **Confirm pending** (`widget_click`): `table confirmed 1`, the value box is back with 41. Focus Cap, type `12000`, Enter: `table rejected …: Above the cap`, the orange line `…, Cap: Above the cap` under the table, the old cap back; `ui_input hover` on the row and `ui_read tooltip`: the reason. Type `43` + Enter and wait 15 s without Confirm: the line reads `…: No answer from the server.` Clear a Reserve box (`key Backspace` until empty) + Enter: `table resolved … = inherited`, the box empty with the gray hint `10`; hover the row: `ui_read tooltip` reads `Reserve 10: inherited from category …`. Hover the `Reserve` and `Cap` captions (`ui_input hover {"row":"demo-table:header","cell":"c:reserve"}`): a circle-i after each caption (before the sort arrow when sorted) and `ui_read tooltip` `Reserve` / `How many of an item stations leave untouched.`. Focus Reserve, type `55`, `ui_input key Escape`: the box shows its value again, no `table edit` line, `mods_ui status` still open; a second `key Escape` closes the panel as usual.
7. The wheel: `ui_input {"action":"wheel","row":"demo-table:row6","notches":-1}`: the body moves two lines, the footer counting (`Showing 3–14 of 600`), the scroll bar handle following (`ui_read widget demo-table` `top`; the bar spans the body rows only, **screenshot**); at the bottom (`wheel` with enough notches) the next notch scrolls the page (the panel's scroll moves, `top` does not). Drag the handle: `ui_input {"action":"drag","x":…,"y":…,"to":{…},"steps":20}` with the handle's point from **screenshot** or `ui_read target` on the bar's row edge: the body follows in whole lines. Selection, the open drawer and a waiting spinner are where they were when scrolled back.
8. Controller: `nav {"action":"focus","row":"demo-table:row11"}` (into the content pane), then `{"action":"press","button":"down"}`: `taken`, the focus still on `row11`, the footer `Showing 2–13 of 600`. `{"action":"press","button":"up"}` to `row0` and once more: back to `Showing 1–12`. `nav {"action":"hold","button":"down","ms":1500}` repeats down the body. `nav {"action":"stick","y":-1,"ms":400}` scrolls the body. `nav {"action":"focus","row":"demo-table:row0","cell":"check"}`, `{"action":"press","button":"a"}` toggles it.
9. Toggle **Group by category**: `table grouped = on`; `row0` reads `Alchemy (…)` in gold with the arrow down, its rows, then a `Subtotal` line with the Total and Here sums; an item in two categories appears under both; the last line is `Total` (each item once). `widget_click demo-table:row0`: the group folds to its header and subtotal.
10. **Reset table**: `table reset`.

#### Master-detail, multi picker and repeat pick acceptance

1. `widget_click {"label":"demo-tabs:tabs/tab3"}`, then `{"action":"widget_read","pane":"content"}`. Expect `demo-md:tabs:tabs` (chips `tab0` Belts, `tab1` Categories), `demo-md:row0` to `demo-md:row13`: `row0` with the search field `m-search` and `m-note` `16`; every row's `tileStates` has `"2": "rule"`; `row1` to `row13` with `m-title` `Ore in`, `Line 1`, `Jewel cabinets`, `Belt 4` … `Belt 13`; every row's `divider` visible; detail cells hidden except `d-text` on rows 0 and 1 (`A belt is an ordered set of containers.`, `Select a belt, or make one with + New belt.`). **Screenshot** at each resolution: the divider is one straight thin line at one x, no clipped or overlapping text, the rows line up with the settings rows around them.
2. `widget_click {"label":"demo-md:row1"}`: the log has `Controls sample: widget md select belt-ore`; `widget_read` shows `demo-md:row1` with `tileStates {"1": "selected", "2": "rule"}` (**screenshot**: the gold band and stripe behind the left half only, the rule unbroken from row to row). Hover: `ui_input {"action":"hover","row":"demo-md:row2","fx":0.25}` lights the hover wash on the left half (`tileStates`, **screenshot**); `"fx":0.75` on the same row: the detail half does not light; `ui_input hover {"row":"demo-md:row1","fx":0.25}`: the selected entry under the pointer is brighter. The detail shows `Ore in` with `Rename` (`row0`), `6 chests · filled in list order` (`row1`), the members on rows 2–7, `+ Add…` / `Pick` on row 8, `Used by (3)` on row 10 and the three links on rows 11–13. The detail has 15 lines in 14 rows: `ui_read widget demo-md` shows `detailTop` 0; `ui_input {"action":"wheel","row":"demo-md:row7","fx":0.75,"notches":-1}` or `nav focus` on `row13`'s link then `press down` brings the `Locate all` / `Delete` line up.
3. `widget_click demo-md:row2/d-down`: `widget md reorder ch-1 0->1 = ch-2,ch-1,ch-3,ch-4,ch-5,ch-6`; `row2` now `Ore chest`. `widget_click demo-md:row3/d-expand` (Iron bin): rows 4 and 5 read `Holds Iron Ore and Copper Ore.` and `31 of 40 slots used.`, indented. `widget_click demo-md:row7/d-remove` on `Storage chest 6` (not removable): nothing happens. `widget_click demo-md:row0/d-btn0`: `widget md action tab 0 belt-ore title/rename`, the entry reads `Ore in (renamed)`.
4. `nav {"action":"focus","row":"demo-md:row13"}`, then `press down`: `ui_read widget demo-md` shows `masterTop` 1 and `row13` reading `Belt 14`; `nav focus` on a member row, `press right` moves the focus to `d-grip` / `d-up` …; `press a` on `d-down` reorders (a `md reorder` line).
5. The wheel and the halves: `ui_input wheel` with `"fx":0.25` scrolls only the list (`masterTop` changes, `detailTop` does not); with `"fx":0.75` only the detail; at the detail's end the next notch scrolls the panel. Drag a member by its grip below the last member: `ui_input {"action":"drag","row":"demo-md:row2","cell":"d-grip","to":{"row":"demo-md:row7","fy":0.1},"steps":20,"keepDown":true}`, then **screenshot**: the held row takes the same held-row style as the ordered list (gold band, gold caption, lit grip) and the rows close up live; `ui_input up`: one `md reorder` line on release.
6. Typing: `ui_input {"action":"focus","row":"demo-md:row0","cell":"m-search"}`, `{"action":"type","text":"ore"}`: `widget md search "ore" = 1 matches` per keystroke, the list shows `Ore in` only, `m-note` reads `1 of 16`. `key Backspace` ×3, `type "zzz"`: `No match for "zzz".` and the `+ New belt` line under it.
7. `widget_click demo-md:tabs:tabs/tab1`: `widget md tab = Categories`; the list reads `Flowers`, `Gems`, `Wood`, `+ New category`; back to `tab0`, the selection, query and detail are as they were. Scroll the list to its end (`ui_input wheel` with `"fx":0.25` or `nav stick`) and click `+ New belt`: `widget md action tab 0 … master/new`, then `widget md select belt-new-N`.
8. Terms: `widget_read` shows the marker on every Belts entry's caption and on the `Used by (3)` heading (**screenshot**: the muted gold circle-i after each caption, inside the master half). `ui_input {"action":"hover","row":"demo-md:row1","fx":0.25}` then `ui_read tooltip`: titled `Belt` with its explanation (not the entry's own tooltip); hover the `Used by` heading's row with `"fx":0.75`: the `Used by` explanation.
9. **In world, with three castle chests:** select `Ore in`, press `Pick` (`demo-md:row8/d-btn1`): `widget md action tab 0 belt-ore actions/pick`, `widget pick several armed for belt-ore`; the band reads `Pick containers or stations`, the Interact cap then `Add`, the Esc cap then `Finish`, its bottom edge clear of the health readout and the buff row (**screenshot** at 1920x1080 and with `superSize 2`). Interact with chest A (game input, as step 8 of the widgets list): no panel, `Ui: world pick #N picked … (1 so far; repeating).`, `widget pick several 1 = …`, the band reads `… · 1 picked` with `1 picked` in the gold, the member list grows by one. Interact with chest A again within ten seconds: `ignored a repeat of the same target`, no new member. Interact with chests B and C: counts 2 and 3. `ui_input key Escape` with no other panel open: `Ui: world pick #N finished by Escape after 3 picks.`, `widget pick several finished = 3`. Repeat with the Mods workspace open: the first `key Escape` closes the workspace, the pick stays armed; the second finishes it. (**Pick several (repeat)** under the master-detail arms the same pick.)
10. Multi picker, `tab1` (`demo-multi`): `widget_click demo-multi:line1` (Crystal): `widget multi = crystal`, `demo-multi:status` reads `7 entries. 1 selected.` with `all` and `clear` visible. `widget_click demo-multi:status/all`: `widget multi = crystal,thread,st-furnace,ch-iron,ch-wood` (the grayed entries are not selected), `all` hidden. `widget_click demo-multi:status/clear`: `widget multi = ` and `clear` hidden. `nav focus demo-multi:line2` and `press a`: it flips.

#### Chips acceptance

1. `widget_click {"label":"demo-tabs:tabs/tab0"}`, then `{"action":"widget_read","pane":"content"}`. Under the ordered list expect the rows `demo-chips-items:line0` (cells `chip0` `Crystal · 240`, `chip1` `Coarse Thread · 75`, `chip2` `Planks · 1,320`; `icon0` to `icon2` visible, each rect inside its chip's rect at the left; `x0`, `x1` visible inside their chips at the right, `x2` hidden; every chip rect 36 tall at 1:1), `demo-chips-belts:line0` (and on a 560-unit row `line1`: the long belt starts its own line ending in `…`; the add chip `+` after it), `demo-chips-filters:line0` and `line1` (`chip0` `Ore` in tone `chipOn`, the rest `chipOff`; the last drawn chip on `line1` reads `+2 more`). `demo-chips-*:line2` hidden. **Screenshot** at each resolution (and the 640-unit floating window if the page is opened there): no chip, glyph or caption clipped or overlapping; each item chip reads as one chip (icon, caption and × inside one chip art); captions centered between the icon and the ×, and on plain chips centered on the whole chip; the selected filter on the gold plate.
2. `ui_input {"action":"click","row":"demo-chips-belts:line0","cell":"x0"}` (the pointer path; `widget_click demo-chips-belts:line0/x0` is the slot path): log `Controls sample: widget chips belt remove belt-ores`; `widget_read`: `line0/chip0` now `Wood belt`. Click the add chip (`demo-chips-belts:line<L>/chip<S>`, from `ui_read widget demo-chips-belts` `add`): `widget chips belt add belt-new-1`, and a `New belt 1` chip before the `+`.
3. `widget_click demo-chips-belts:line0/chip0`: `widget chips belt open belt-wood`. `widget_click demo-chips-items:line0/x2` (Planks, not removable): the glyph is hidden, nothing happens.
4. `widget_click demo-chips-filters:line0/chip1`: `widget chips filter cat-ingot = on (cat-ore,cat-ingot)` and `chip1` turns `chipOn`; again: `= off`. `widget_click` the `+2 more` chip: `widget chips filter more 2 = cat-blood,cat-misc`.
5. Controller: `nav {"action":"focus","row":"demo-chips-items:line0","cell":"chip0"}` (`nav state`), then `press right`: the focus is on `x0`; `right`: `chip1`; on the filters, `down` moves from `line0` to `line1`. `press a` on a filter chip flips it (one `widget chips filter …` line). `press x` on a chip does nothing. **Screenshot** with the focus on the selected `Ore` filter: the gold plate and the focus frame 3 units outside the chip both show.
6. Terms: **Reset chips** first; `widget_read`: the belt chips `Ores belt` and `Wood belt` and the filter `Ore` show the circle-i after their caption (**screenshot**: the marker inside the chip art, clear of the ×). `ui_input hover {"row":"demo-chips-belts:line0","cell":"chip0"}`, `ui_read tooltip`: the gold title `Belt` and the glossary line; hover `Ore` (`demo-chips-filters:line0/chip0`): the `Category` line. Three hovers of `Belt` more than a second apart (`hover` the chip, `hover` off it with `"panel"` and `"fx":0.02`, wait 1.1 s, again): the markers on both belt chips go (`Ui: term 'Belt' of progenitor.controlssample hovered 3 times; …`; `ui_read terms {"plugin":"progenitor.controlssample"}` `marker false`, `hovers 3`) and the hover still explains. Bring them back (**Show markers again** on `tab4`), then remove `Ores belt` with its ×: the marker stays with `Wood belt`, now in the first slot.
7. Hover states: `ui_input hover` on a chip: the art's hover sprite (also on the selected chip, over its plate) (**screenshot**); hover its × only (`cell x0`): the glyph brightens (**screenshot**). Hover the long belt chip: `ui_read tooltip` title is the full caption, its body the chip's tooltip; hover `+2 more`: the hidden labels. In a world, the item chips show the Crystal and Coarse Thread inventory icons (**screenshot**).
8. Resize with `tab0` open: `display {"action":"set","width":1280,"height":720,"mode":"windowed"}`, then `widget_read` and **screenshot**: within a moment the belts and filters re-wrap for the new row width; nothing leaves the row. `display restore`.
9. **Reset chips**: `widget chips reset`; the three sets are back.

#### Help acceptance

The explained words and the tip buttons are on `tab4` under the HUD buttons.

1. `widget_click {"label":"demo-tabs:tabs/tab4"}`, `widget_read {"pane":"content"}`: the heading `Belt`, the labels `Ore in: three chests beside the furnaces.`, `Reserve 20 Iron Ingot`, `Cap 500 Planks`, the button `Category: Ore`, the inner tab row `help-tabs:tabs` (`Belts`, `Categories`), the list `help-list:row0` (`Ore in`) and `row1` (`Iron bin`), the toggle `Show help markers` (on). **Screenshot** at each resolution: a small muted gold circle-i after each of those five captions and after `Belts`, `Categories` and `Ore in`, the same gold as the list's glyph buttons, vertically centered on the text, not clipped, not overlapping the control column.
2. Hover `Belt` (`ui_input hover` on its row, from `widget_read`): `ui_read tooltip` gold title `Belt` and the line `A named list of chests …`. Off and on twice more more than a second apart: after the third hover every Belt marker on the page disappears (`ui_read terms`); the log has `Ui: term 'Belt' of progenitor.controlssample hovered 3 times; its marker is hidden (the explanation still shows).`; hovering again still shows the tooltip. The toggle `Show help markers` now reads off.
3. `widget_click {"label":"Show markers again"}`: the Belt markers are back, the toggle reads on, the log has `Controls sample: widget help markers shown`. `[Ui] HiddenTermMarkers` in `BepInEx/config/progenitor.ui.cfg` is empty again.
4. Turn the toggle off (`widget_click` on it): every marker on the page disappears; the cfg's `HiddenTermMarkers` lists the sample's `belt`, `cap`, `category`, `reserve` and `used by` keys. Hot-reload Core: the markers stay hidden. Turn it on again.
5. `widget_click {"label":"help-list:row0/down"}`: the marker moves with `Ore in` to the second row. Hover the `Belts` tab (`ui_input hover {"row":"help-tabs:tabs","cell":"tab0"}`) and the `Ore in` row: `ui_read tooltip` the Belt line; hover `Iron bin`: its own tooltip `A single chest.`
6. `widget_click {"label":"Show tip"}`: the log has `Ui: tip 'help-demo' of progenitor.controlssample shown.` and `Controls sample: widget help tip = Showing`. `ui_read {"what":"tips"}`: `showing.title` `Explained words`, `cardRect`, `anchorRect`, `relative` (beside the Mods workspace, or the bottom-right corner), `cardOnScreen true` at each resolution. **Screenshot** (`"panel":"Explained words"`): the game's popup frame over the title rule, the centered body, **Show all tips** (left) and **Got it** (right, gold caption) as popup buttons with their captions centered on the whole button art, not over a dialog.
7. `widget_click {"label":"tip-buttons/got-it"}`: `Ui: tip 'help-demo' of progenitor.controlssample marked seen (got it).`; the card is gone (`ui_read tips` `showing null`). **Show tip** again: `Controls sample: widget help tip = Seen` and no card. `[Ui] SeenTips` lists `progenitor.controlssample:help-demo`.
8. Modal: **Reset tips** (`widget help tips reset`), open a framework dialog (the Sample's **Confirm dialog**, or `ctx.Dialogs` from any page), then **Show tip** through the bridge: `widget help tip = Queued` and `Ui: 1 tip card(s) wait for a dialog, key capture or list to close.`; answer the dialog (`mods_ui dialog_choose {"choice":"no"}`): the card appears. With the card up, open the dialog again: `… steps back while a dialog, key capture or list is open.`, the card is gone while the dialog is up and returns after it.
9. **Show all tips** on the card (`tip-buttons/show-all`): the card closes (`marked seen (show all tips)`), the `Tips` window lists `Explained words` with its body, **Show tips again** and **Close** work.
10. Escape: **Reset tips**, **Show tip**, then `ui_input {"action":"key","key":"Escape"}` (the framework's Escape path; `nav press b` is the controller's): the card closes with `marked seen (escape)`.
11. With the card up and the Mods workspace closed: `nav {"action":"hold","button":"a","ms":200}` and `nav hold down`: nothing on the card reacts (it is not a controller screen; `nav state` has no surface on it); `nav hold b`: it closes (`marked seen (escape)`). Gameplay clicks stop only while the pointer is over the card: `ui_input hover {"panel":"Explained words"}`, then `mods_ui status` / the log's capture line shows the block registered; `ui_input hover` off the card: released.
12. Unload: with a card waiting behind a dialog, hot-swap the sample: `Ui: dropped 1 tip card(s) of an unloading plugin.` and `… dropped unanswered (its plugin unloaded).`; nothing left on screen; the cfg's `SeenTips` unchanged.

#### Cleanup

`ui_input {"action":"release"}`, `nav {"action":"pad_remove"}` if a pad was added, `display {"action":"restore"}`. Remove the sample from `BepInEx/progenitor-reload` on the client and confirm the clean unload in the log: no `Ui: ctx.Hud …`, `Ui: master-detail …` or term warning; the tooltip, the pick band, the pings, every marker, tip card and tip list and the table's scroll bar are gone with the page.

## Help: explained words and first-time tips (P7b)

Two pieces that explain a mod's own words where the player meets them: **term tooltips** (a glossary, a hover explanation on any kit control, and a small circle-i marker that goes away after a few hovers) and **tip cards** (a one-time card in the game's popup look, with **Got it** and **Show all tips**).

**Status: experimental.** Source-checked, built and game-free tested (`UiTermsTests`, `UiTipsTests`); **live-verified on the test client 2026-10-14 through the bridge at 1920x1080 and 2560x1440, plus a 2x supersample capture (layout only)**; not verified: native 4K pixel density, the feel of a real-mouse drag, and controller hardware (navigation was driven through the bridge's virtual pad, not a physical controller). The live procedure is [Help acceptance](#help-acceptance). Reference: the Widgets page's **HUD** tab ([`WidgetsDemo.Help.cs`](../samples/Progenitor.ControlsSample/WidgetsDemo.Help.cs)).

```csharp
using Progenitor.Ui;

// Once, when the plugin loads: the wording lives here and nowhere else.
UiTerms.Define(ctx.Guid, new Dictionary<string, string>
{
    ["Belt"] = "A named list of chests that work together.",
    ["Cap"] = "Stations stop making an item once you hold this many.",
});

ctx.Menu.Register("My mod", "…").Page("Belts", panel =>
{
    panel.Heading("Belts").Term("Belt");                 // hover: the Belt line; a circle-i after "Belts"
    panel.Label("Cap 500 Planks").Term("Cap");
    panel.TermMarkersToggle("Show help markers");         // the "show markers again" setting
    ctx.Hud.Tip("belts-intro", "Belts",
        "A belt groups chests you use together. Make one, then add it to any Input or Output.",
        new UiTipOptions { Anchor = panel });
});
```

| Piece | Call | Vanilla counterpart and borrowed art |
|---|---|---|
| Glossary | `UiTerms.Define(pluginId, term, explanation)` / `Define(pluginId, glossary)` | None (the game explains its words in item tooltips); one place per plugin, read on every hover. |
| Hover explanation | `handle.Term(term)` on any kit control | The kit tooltip (the dropdown description box's look, DROPHOVER1): the game's frame sprite, a title in the game's gold (the term as the glossary writes it), a light body. |
| Marker | drawn by `handle.Term(term)` | No game glyph fits: a circle-i drawn once per Core generation into a texture (as the Locate marker is), tinted the muted gold of the widgets' glyph buttons, never a raycast target. |
| "Show markers again" | `panel.TermMarkersToggle(text)`, `UiTerms.ShowMarkers` / `SetMarkers` / `MarkersOn` / `OnMarkersChanged` | The settings grid's toggle. |
| Tip card | `ctx.Hud.Tip(id, title, body, options)` | The game's popup (`CommonPopup`, DIALOGS2): the `Window_Box_Background` frame (no backdrop: not modal) holding a framework dialog's own rows: the centered title over the game's title rule (`Divider_Big_02`), the centered body, and the dialog button row of the game's popup buttons (`SmallerButton_*`, one width, captions centered on the whole button art). |
| Tip list | `ctx.Hud.ShowAllTips()` (and the card's **Show all tips**) | A game-window panel: each tip's title as a heading and its body as a note, **Show tips again** and **Close**. |

### Term tooltips

| Member | Behavior |
|---|---|
| `UiTerms.Define(pluginId, term, explanation)` | Defines (or rewords) a term for a plugin. Terms compare without case and with whitespace runs as one space (`"Mass Deposit"` = `" mass  deposit "`). An empty explanation removes the term. Process-wide per plugin id; holds only text. |
| `UiTerms.Define(pluginId, IEnumerable<KeyValuePair<string,string>>)` | Every pair of a glossary. |
| `UiTerms.Explain(pluginId, term)` → `string` | The explanation, or empty. |
| `UiTerms.IsDefined(pluginId, term)` → `bool` | |
| `UiTerms.Glossary(pluginId)` → `IReadOnlyList<KeyValuePair<string,string>>` | Definition order, the term as written: for a plugin's Help page. |
| `UiWidgets.Term(this IUiHandle, term)` → `IDisposable` | Marks the term on a kit control (label, heading, note, button, toggle…). The plugin is the control's (`IUiHandle.PluginId`). Hover shows the explanation (title = the term as written in the glossary); the marker sits after the control's text. A term defined later still works (the glossary is read on every hover; the marker appears when it is defined). The lease removes the hover watch and the marker; both also go with the control. On a runtime that cannot draw, an inert lease that keeps state. |
| `UiTerms.AutoHideAfter` (get/set, `DefaultAutoHideAfter` 3; 0 never; at most 20) | Hovers after which a marker hides itself. Persisted for the player (`[Ui] TermMarkerAutoHide`). |
| `UiTerms.MarkerShown(pluginId, term)` → `bool` | Defined and not hidden. |
| `UiTerms.Hovers(pluginId, term)` → `int` | Hovers counted so far toward the auto-hide. |
| `UiTerms.HideMarker(pluginId, term)` | Hides one marker for this player; the explanation still shows. |
| `UiTerms.ShowMarkers(pluginId)` | Every marker of that plugin back, its hover counts restarted. Other plugins untouched. |
| `UiTerms.MarkersOn(pluginId)` / `SetMarkers(pluginId, on)` | For a plugin's own toggle: on when none of its defined terms is hidden; writing on is `ShowMarkers`, off hides every defined term's marker. |
| `UiTerms.OnMarkersChanged(pluginId, handler)` → `IDisposable` | After a definition, a hide, an auto-hide or a show for that plugin. Track the lease (`ctx.Track`) so it ends with the plugin. |
| `UiWidgets.TermMarkersToggle(this IUiPanel, text = "Show help markers")` → `IUiToggle` | A toggle bound both ways to `MarkersOn` / `SetMarkers`; follows changes made elsewhere. Its change handler lives as long as the toggle on every panel kind (a Mods page, a mod header, a floating window): it is dropped as soon as the toggle is disposed (a page redraw, a header rebuilt; checked at the next registration or change), with the panel, and with the plugin's unload. |
| `UiTerms.Describe(pluginId)` | Readback: `{plugin, autoHideAfter, markersOn, terms [{term, explanation, marker, hovers}], marked}`. |

**Terms on composed widgets.** The composed widgets mark their own captions: the tabs (`IUiTabs.SetTerm(tab, term)`, empty removes it), the ordered list (`UiListItem.Term`), the data table (`UiTableColumn.Term`), the chips (`UiChip.Term`) and the master-detail (`UiMasterEntry.Term`, `UiDetailLine.Heading(text, count, term)`). Each widget forwards its own hover to the term, so hovering the caption shows the glossary line in place of that item's plain tooltip, and the marker follows its item when rows or chips are reordered, filtered or re-packed (reused slots re-sync their marker on every render). A tab, row or chip whose term is not defined keeps its tooltip. Every binding is released with the widget.

- **Counting.** One pointer visit counts once, while the term's marker shows. A visit starts when the pointer enters a control marked with the term and ends when it leaves; more enter events during it (a row and its cell, a repeated event, a second control with the same term) are the same visit, and coming back within 1 s of the leave continues it (a pointer jittering on an edge). An enter whose leave never comes (its panel closed under the pointer) stops holding the visit after 10 s. At the limit the marker is hidden (`Ui: term '<term>' of <plugin> hovered 3 times; its marker is hidden (the explanation still shows).`) and the count is dropped. After a marker is hidden nothing is counted any more.
- **Whose glossary.** A control reads the glossary of the plugin it belongs to (`UiTerms.PluginOf(handle)`): on the Mods workspace's page (a mod's overview or custom page) that is the mod whose page it is, although the page pane itself is the framework's and its handles report Progenitor's plugin id; anywhere else the control's own plugin id. So `UiTerms.Define(ctx.Guid, …)` explains the words on the mod's own page, the composed widgets (tabs, list, table, chips, master-detail) included, and the log names the mod.
- **Keying.** Every stored key is `plugin:term`, percent-escaped (`%`, `,`, `:`, `=`, `;`, control characters), so two plugins' "Cap" never share a count or a hidden mark. The plugin id keeps its case (the cfg line names the mod as its GUID is written); the term is normalized (lower case, whitespace runs as one space), since terms compare without case.
- **Persistence.** Core's client `progenitor.ui.cfg`: `[Ui] HiddenTermMarkers` (sorted `plugin:term` keys, commas), `[Ui] TermMarkerHovers` (`plugin:term=n`), `[Ui] TermMarkerAutoHide` (0–20, default 3). Read on every use (never cached), so a Core reload or a restart finds them. Unbound (server, a failed bind): session memory, one warning. A write that would not change the stored text is not made. None of them is drawn on a settings page; a plugin offers its own toggle.
- **One hover handler per control.** Do not put `Tooltip(...)` and `Term(...)` on the same control: both show the one tooltip box and the later hover wins.
- **Unload.** The marker is a child of the control's text object, so it goes with the control and its panel. A plugin's unload (`Ui.RemoveAll(owner)`, which every plugin unload runs) releases its marked controls and the change handlers registered for its controls (`TermMarkersToggle`); a handler a plugin registers itself with `OnMarkersChanged` must be tracked. The glossary text stays until the next generation defines it again. The Core teardown drops the glossaries, every marked control, every handler, the marker sprite and its texture.

#### Marker layout rules

1. The marker's side is the text's font size × 0.72, rounded, clamped to 10–22 units (13 at the kit's 18-unit body text).
2. It sits 4 units after the text's last character, vertically centered on the text, and **never overlaps the text**. While it shows it keeps its room (its side + 4) in the text's right margin, so a caption that would fill its rect is shortened with its ellipsis instead of being covered, and the marker gives the width back when it hides. Where it still does not fit inside the text's rect it is hidden, never pulled back onto the caption (`UiTermMarkerArt.CenterAfter`).
3. Widgets that size their captions keep the same room: a tab chip grows by it while the tab explains a term (`IUiTabs.SetTerm`); a data-table column's width includes it (`UiDataTableLayout.TermRoom`), and a sorted column's drawn sort mark, which owns that text's margin, places itself after the marker; a chip's caption margin includes it (`UiChipsLayout.TermReserve`), so the chips' marker does not add a second reserve. The ordered list's and the master list's captions fill their row, so their margin reserve shortens a long caption with its ellipsis.
4. It is a child of the text object: it scales with the canvas (the game's design space), is clipped by the panel's viewport and hidden with the control.
5. One shared frame tick re-places every live marker (reads only cached objects; no scene search) and stops when none is left; a caption rewritten in place keeps its marker at its end.
6. Header cells have no hover state in the kit, so a hovered table header shows the tooltip but no wash or underline.
7. On a selected chip's gold plate (a selected tab, a selected filter chip) the muted gold would vanish into the plate, so the marker is drawn dark there (`UiTermMarkerArt.Plate`, #2a1c0a) and switches back when the selection moves (it reads the host cell's tone once a frame).
8. Art: a 48×48 coverage texture (ring, dot, stem; one-pixel smoothed edges), bilinear, aspect preserved. No font glyph: U+24D8 is not known to be in the game's body font, and the fallbacks a font surely has (`(i)`, `i`, `?`) read as debug text after a caption.

### Tip cards

| Member (`ctx.Hud`) | Behavior |
|---|---|
| `Tip(id, title, body, UiTipOptions? options = null)` → `IUiTip` | Registers the tip and shows it once: at once when the screen is free, else when it is. A tip the player already dismissed returns `State = Seen` and shows nothing. Asking again for a tip that is waiting or showing returns that request. Server: a `Dropped` tip, one log line. |
| `DefineTip(id, title, body)` | Registers (or rewords) a tip without showing it, so the list has it. |
| `Tips` → `IReadOnlyList<UiTipInfo>` | Registration order: `Id`, `Title`, `Body`, `Seen`. |
| `IsTipSeen(id)` → `bool` | |
| `ShowAllTips()` | Opens the plugin's tip list (one at a time; a second call replaces it). |
| `ResetTips()` / `ResetTip(id)` | Every tip (or one) shows once again. Other plugins untouched. |
| `UiTipOptions.Anchor` (`IUiPanel?`) | The panel the tip explains: the card is placed beside it; a card still waiting when it closes is dropped unshown (not marked seen); a shown card whose anchor closes is closed the same way. Null: bottom-right of the screen. |
| `UiTipOptions.OnShowAll` (`Action?`) | What **Show all tips** runs instead of the framework's list (a plugin with its own Help page opens it). Guarded; a throw is logged. |
| `UiTipOptions.AnchorControl` (`IUiHandle?`) | The control the tip explains (a button, a label, a widget on one tab). While it is not on screen (its tab is not selected, it is hidden, its rect is outside its panel's) the card steps out of sight and comes back when the control shows again; nothing is answered or marked seen meanwhile, and a hidden card takes no Escape. A control that is disposed (its page was rebuilt) drops the tip unshown. Null: the card follows only `Anchor`. The controls sample passes its **Show tip** button, so the card is on the Help tab only. |
| `IUiTip` | `Id`, `State` (`UiTipState`: `Seen`, `Queued`, `Showing`, `Done`, `Dropped`), `Dispose()` (takes a waiting card out or closes a shown one, without marking it seen). |

- **Once.** "Seen" is per player in `[Ui] SeenTips` of Core's client `progenitor.ui.cfg` (sorted `plugin:tip` keys), read on every use.
- **One at a time.** One queue across every plugin, first asked first shown.
- **Never over a modal.** While a framework dialog (`ctx.Dialogs` and the framework's confirmations), a key capture or an open dropdown list is on screen, nothing new shows (`Ui: N tip card(s) wait for a dialog, key capture or list to close.`, once per wait); a card already up closes and goes back to the head of the queue (`… steps back while a dialog, key capture or list is open.`) and returns when the screen is free. The pump runs on the widget frame tick only while something waits or shows.
- **Buttons.** The card's rows are a dialog's (DIALOGS2): **Show all tips** sits on the dismissal's side (left) and **Got it** is the main answer (right, its caption in the game's gold), both the game's popup buttons at one width, centered as a pair. Bridge: `mods_ui widget_click {"label":"tip-buttons/got-it"}` and `tip-buttons/show-all`.
- **Answers.** **Got it** marks it seen and closes it. **Show all tips** marks it seen, closes it and opens the list (or `OnShowAll`). **Escape** (B on a controller) counts as **Got it**: the player saw the card and sent it away. The card is the topmost layer while it shows: it takes the framework's consumed Escape (`Ui.PushEscape`, after an open dialog is answered and before the sweep that hides every shown panel), so one press closes the card and nothing else; the Mods workspace behind it stays open, and the next press closes the workspace as usual.
- **Controller focus (NAV1).** The card takes input only while hovered, so it is not a NAV1 screen and a stray A press never reaches it; the game keeps the controller. Should it become a navigated screen, the dialog rule applies: the focus starts on **Show all tips**, which only opens a list.
- **Placement** (`UiTipLayout`, after each show and once a frame while it is up, because a re-flow of other panels may move it): right of the anchor panel, top edges aligned; else left of it; else under it (left edges aligned); else over it; the first that fits 24 units inside the canvas wins, 16 units from the panel. With no room around the panel (the Mods workspace fills a 16:9 screen at every resolution: its page leaves about 125 units at the right, the card needs 680), the card goes **inside the panel, against its right edge**: 24 units from the panel's right edge, below its 72-unit title band and above its bottom 40 units (the footer band), searched downward in 8-unit steps, keeping 8 units from every control and line of text. It takes the highest spot that covers **neither a control nor a line of the panel's text** (each label's, heading's, note's and read-out's own text extent, `IUiHelpRuntime.TextRect`), so a long description stays readable; where none exists, the highest spot that covers no control. Controls are what takes input (buttons, toggles, fields, sliders, dropdowns, key fields; a cell row's button, field, switch and expander cells; the whole row when the row itself takes a click, as a list row does), read from the panel's own rows' rects (`UiTipObstacles`); labels, headings, notes and read-outs are not controls, so in that second search the card may sit over the end of a line of text. Where every spot covers a control, the spot that covers the least. The controls are read when the card shows and again only when the panel's rect changes, so the card does not move while the player uses the page. It is never over another panel beside it (the Mods list). A panel narrower than the card plus those insets, or no anchor: the screen's bottom-right corner, 24 units from the edges. Always clamped onto the canvas. Tested at 1920×1080, 2560×1440, 3840×2160 and 3440×1440 (a centered window, the leftmost and rightmost columns, a full-screen anchor) and with the workspace's page, the Help tab's controls and the HUD tab's controls and lines of text from the live captures at 1920×1080, 2560×1440, 3840×2160 (inside the page, over none of its controls or text) and 3840×1080 (beside the workspace). The card is a 640-unit frame (the dialog frame width) and an ordinary kit panel, so it counts in the panel flow and is then moved to its own spot.
- **Anchored to a control.** A tip card with `AnchorControl` only shows while that control is on screen: when the control is hidden (another tab, scrolled out of its panel, its panel hidden) the card steps out of sight and comes back with it.
- **Capture.** `WhenHovered`: gameplay input is blocked only while the pointer is over the card. The card never takes the wheel: the input pump skips it when it picks the hovered panel, so a notch over the card scrolls the page beneath it; its buttons still click.
- **Unload.** Every unload reason drops the plugin's waiting cards, closes its shown card and its list, and removes its tips from the registry (their callbacks with them), without marking anything seen (`Ui: dropped N tip card(s) of an unloading plugin.`): through `ctx.Hud`'s tracked registration and through `Ui.RemoveAll(owner)`, which every plugin unload runs. The Core teardown closes everything and stops the tick.
- **The list.** One list at a time. Its **Close** button, the Escape sweep and its plugin's unload close it, and the framework forgets it (the readback's `listOpen` turns false); a second **Show all tips** opens a fresh one.

#### Card layout rules

1. The dialog layout (DIALOGS2): the pane title hidden, and its band with it, and the frame sized like a dialog's (its 16-unit top pad, its content and its footer; no 72-unit title band, no 200-unit floor; the viewport starts under the top pad), so the title sits at the top of the frame as in the game's popup; the title row centered, 24, in the game's gold (`MenuPalette.ChipOnBorder`, as the tooltip's title; the framework's dialogs keep their gray), the game's title rule under it; the body centered, 17, wrapping at 460; the 52-unit button row, both buttons one width (the widest caption × 1.3 + 40, at least 120, 256 when the row holds them), centered between spacers, dismissal left, main right.
2. Never over its anchor panel when any side has room; never off the canvas; never shown while a modal is up.
3. The list: one heading and one note per tip, then **Show tips again** and **Close**; placed by the ordinary panel flow.

## Ownership and teardown

Every GameObject lives under a DontDestroyOnLoad root named `progenitor-ui-<pluginId>`. Plugin unload / `RemoveAll` **Unwire every widget first**, then destroy that plugin's root by **exact name** (`DestroyRoot`). Core Uninstall is the only prefix sweep: after Unwire it `DestroyByPrefix("progenitor-ui-")` so leftover roots from any plugin go away. `DestroyImmediate` is used only when the Core-reload cascade honors a one-frame gap before `ALC.Unload` (`CoreReloadPolicy.MayDestroyImmediateOnUninstall`); otherwise deferred `Object.Destroy`. Each panel owns its overlay canvas (`Canvas` + `GraphicRaycaster`); Hide deactivates that panel's GameObject and its canvas so EventSystem cannot fire `EventTrigger` / Button UnityEvents on a hidden panel across a swap, and a sibling panel on the same plugin root stays visible. Close / `Dispose` of a panel **destroys that panel's overlay** (Unity destroys the panel child with it); a second panel on the same plugin root keeps its canvas. Overlays do not leak until `RemoveAll` / Uninstall. `UiSlots.ClearAll` nulls the Core dispatcher and returns listeners to the pool; the pooled Bootstrap `ClickSlot` / `PointerSlot` UnityActions stay process-lifetime (`Invoke` is a no-op when Core is down). Double-dispose is a no-op. `IsOwnedBy` does not treat `.` as a child separator (`example.sample` does not own `example.sample.extra`).

Nothing may survive teardown: panel dispose, plugin unload and Core Uninstall all drive the capture count to zero and remove the input context, because a Core swap that left one registered would leave the player's movement blocked with no owner. The blocked state is a per-frame mask, so losing it costs one frame rather than a stuck character — with one exception teardown must handle: `RotateCameraBlocker` carries a **persistent** `_BlockRotateCamera` field, so a removal the game refuses has to leave that field `false` or a leaked list entry blocks the camera for the rest of the process.

## Hosting rows inside a vanilla widget

`Ui.Panel` puts a mod panel on a mod overlay. `Ui.AttachToVanilla` does the opposite: it hangs mod rows **beside a live vanilla widget the game owns and rewrites every frame**, so the information reads as part of the game's own screen. The war-events research is in the internal research notes. Job FUI36.

```csharp
using Progenitor.Ui;

IVanillaAttachHandle rifts = Ui.AttachToVanilla(
        VanillaUiSite.MapWarEventInfo, owner: this, pluginId: MyPluginInfo.PLUGIN_GUID);

var t1 = rifts.Row("T1 (57+) next in 1h 9m");
var t2 = rifts.Row("T2 (80+) remaining 19m 49s");
t1.Text = "...";                                   // or, for the whole display at once:
rifts.SetRows(new[] { "T1 ...", "T2 ..." });
rifts.Visible = haveData;
rifts.Dispose();                                    // or Ui.RemoveAll(this), or Core Uninstall
```

| Member | What it does |
|---|---|
| `Ui.AttachToVanilla(site, owner, pluginId)` → `IVanillaAttachHandle` | Never null. Disabled but valid on the server, with the module skipped, or for an undefined site. Installs the site's frame watch lazily on the first attachment and drops it with the last, so a client with no attaching plugin pays nothing per frame. |
| `handle.Row(text)` → `IUiLabel` | Clones the site's donor widget, strips `LocalizedText`, sets `raycastTarget = false`. The returned handle is addressed **by position**, so it survives the container being rebuilt (which happens on every map close/reopen). |
| `handle.SetRows(IReadOnlyList<string>)` | The whole display in one call, growing or shrinking the row list. What a per-second refresh uses: a caller cannot leave a stale row behind by forgetting one. |
| `handle.Visible` / `IsAttached` / `RowCount` / `Enabled` / `IsDisposed` | `IsAttached` is false while the map is closed — a state, not a failure. Row texts are held by the handle, not by Unity, so they come back on the next open. |
| `handle.Dispose()` | Destroys our container. Idempotent. Touches no vanilla object. |

### Why the site is a closed enum

`VanillaUiSite` lists the map card (`MapWarEventInfo`) and the P6 panel sites ([below](#vanilla-panel-sites-p6)); adding one is a Core change with a research note. A string-addressed "attach to any transform" would make Progenitor answerable for whatever any mod does to the game's HUD, in every future build, with no review. Each site carries its donor widget, its parent rule, its layout verdict and its evidence — the same discipline as an address-library entry.

The map card is addressed by **address, never by type**: `MapMenuMapper.GetMapMenu()?.WarEventInfo`. A second instance of the same type exists — `AlertsParent.WarEventNode`, the transient HUD popup — and is explicitly **not** a site, so a `FindObjectsOfTypeAll` sweep would be a coin flip that changes between sessions. `GetMapMenu()` is an entity query behind a method call and returning null is a normal state; every frame null-checks it.

### Sibling container, never under `TextGroup`

The container is a **sibling** of the widget root — a child of the widget's parent — for three reasons, in the order of what they cost when ignored:

1. `TextGroup` is the `CanvasGroup` whose alpha the vanilla per-frame `SetData` rewrites from its `textAlpha` argument. Rows under it fade with the vanilla text and can vanish at some zoom levels.
2. `Hide()` may deactivate the widget root when nothing is scheduled — which is precisely the state in which a mod row ("T1 next in 1h 9m") is the only useful thing on screen.
3. Adding a child *to* a vanilla transform is already a mutation of the vanilla hierarchy: it changes `childCount`, and anything the game does with sibling indices or `GetChild(i)` then sees us.

The payoff is teardown: disposal destroys our container and that is all. No sibling order to undo, no vanilla component to re-enable, no field to restore.

Three things are never done here, each learned from a crash rather than from a preference. **Never call or patch `MapWarEventInfoEntry.SetData`** — its `ref TimeLocalizationKeys` parameter (160 bytes of `AssetGuid`) cannot be marshaled and throws `MarshalDirectiveException` at map open; that is [interop trap 1](../CLAUDE.md) in its *calling* form. **Never patch the mapper's Burst `LambdaJob_*` types** — the seam is `SystemHooks.After<MapMenuMapper>` on the managed `SystemBase.Update`, which is not a Harmony patch of ours at all, so the rule is unbreakable by construction. **Never clone `MapWarEventInfoEntry` itself** — one TMP leaf is a small, inspectable graft; the card is a live `UIEntry` MonoBehaviour with inspector-wired `UnityEvent` calls that survive `RemoveAllListeners`.

### Positions are set; a reflow is not assumed

Whether a `LayoutGroup` or `ContentSizeFitter` governs the chosen parent is **prefab serialized data**: nothing in the game's metadata can answer it, and both answers are bad to assume. With a layout group, our container becomes a laid-out sibling and *the vanilla card moves to make room* — a visible regression nobody asked for. Without one, nothing positions the container and it lands at the parent's pivot. So the runtime **detects** and **logs which branch it took**, in the style `ruler=` and `settle=` already use:

The two components are **read and reported separately, and only the group decides anything** (R60 finding 2). A `ContentSizeFitter` lays no child out — it sizes the parent from `ILayoutElement`s on the parent itself — so it can never move the card, and `LayoutElement { ignoreLayout = true }` on our child means nothing to it. A fitter-only parent is therefore `layout=none`, and the one-shot hierarchy line prints `parentLayoutGroup=` and `parentSizeFitter=` (and the same pair for the widget root and for `TextGroup`) rather than one merged boolean that answers neither question the spike is sent to settle.

- `layout=none` — no layout group on the parent; the container is anchored from the widget's own rect. A fitter alone lands here.
- `layout=ignored` — a layout **group** was found and the container carries `LayoutElement { ignoreLayout = true }`.
- `layout=reparented` — the site forbids `ignoreLayout`, so the container went to the canvas and is positioned from the widget's world corners. No site takes this branch today.

Disabling the vanilla layout component is the last resort and is **not** taken: a vanilla group left disabled after a mod unloads is a broken vanilla screen, and taking it would require a restore-on-close pass.

Inside our container the rows are ours end to end, so a `VerticalLayoutGroup` + `ContentSizeFitter` of our own governs them.

### Map close and reopen, and the teardown hole

The refresh runs from the site's own managed system update, installed with `HookOptions { OnlyWhenSystemRuns = false }` (`VanillaUiSites.WatchOptions`, shared with the panel sites). `MapMenuMapper` only updates while its own queries match, so with the default option the watch skipped exactly the frames on which the map closed, and the close (and the detach) could go unseen; with the option off it runs every frame the client updates its UI and sees the map as absent when it is closed. Each tick resolves the widget and compares its `GetInstanceID()` with last frame's: unchanged is a no-op, changed or absent-then-present rebuilds, and a container Unity destroyed under us takes the same branch. That is correct whether the game destroys the map view on close or merely deactivates and reuses it, so that unknown is not a blocker. A rebuild destroys the old container **first**; skipping that is exactly how a frozen duplicate appears beside the card on the deactivate-and-reuse branch.

**A destroy that did not take is not a destroy.** Because our reference is the only one in the process, the destroy of an attached container is the one in the module that must not fail quietly, and it does not (R60 finding 1): it goes through the module's shared destroy helper — warn, then fall back to a deferred `Destroy` — and it reports **which of three things** happened. `DestroyImmediate` is the one Unity destroy that throws rather than deferring, and this one is called from inside the game's own `MapMenuMapper.Update`, so the throw is the expected case and the fallback is the branch most likely to work. `UnityUiRuntime.DestroyWithVerdict` therefore answers `Destroyed` (gone now, drop the reference), `Deferred` (Unity has it and it dies at the end of this frame) or `Failed` (still here, and nothing but another try will move it); `UnityUiRuntime.TryDestroy` is the bool face of it for callers that only want to know whether the object is going, and counts `Deferred` as going. Reporting the fallback as a failure was R61 finding 7: it made the common recovery path warn "destroy did not take" and re-throw into the same call once a frame for thirty frames.

So the widget keeps its GameObject reference until the destroy is confirmed, and the handle moves anything still alive to a doomed list that it works from the site's own frame watch: a `Failed` object is **retried**, a `Deferred` one is only **verified** — no second destroy, no warning — with the verdict **re-asked of the widget on every sweep** rather than latched when the object was filed, because the commonest transition is a first destroy that failed and a retry that Unity then defers (R62 finding 5). The handle has **two** `Release()` call sites and both make that test: the sweep, and `ReleaseLocked`, which is where an object is first destroyed and filed. Guarding only the sweep left the claim "the rule lives in the handle" true of one of its two methods, so a later edit that re-released a widget already on the doomed list would have called `Destroy` on an object Unity owned, from inside `MapMenuMapper.Update`, with nothing anywhere to stop it (R63 finding 6). `Dispose` leaves the handle **in the registry** while either is pending, because the watch is what drives the follow-up. The budget is 30 frames either way; past that the object is written off with a line naming the container, because a disposed handle must not hold a per-frame Core-owned delegate for the life of the generation over something Unity will never destroy. Core Uninstall, which gets no later frame, says how many refused destroys it had to leave in the vanilla hierarchy.

**Disposal and the handoff are one step (R61 finding 3).** The retry has exactly one driver, so a disposal must never be visible before the thing it hands over. `Dispose` destroys, files what survived and publishes its disposed flag **last**, all under the handle's own gate, and `Apply` reads the flag under that same gate; the registry then decides retention in `VanillaAttachRegistry.Settle`, which reads *both* facts — disposed, and still owes a destroy — under its own lock rather than trusting a snapshot the caller took. Publishing the flag first let a frame land between the two: it saw a disposed handle with an empty doomed list, dropped it from the table and disposed the site watch, and the container the disposal was one statement away from filing then had no retry, no give-up and not even the leak line.

Rows are written into the widget **only when the string differs** from what it already shows (R60 finding 6). A plugin that pushes on a timer rather than on a change — the Sample's rift rows push four times a second against a string that moves once a second — otherwise assigns an identical string into TMP three frames in four, and whether TMP's own setter early-outs on an equal string cannot be read from the interop stubs. The guard is in `VanillaAttachHandle`, so every caller gets it rather than each sink having to remember.

The container is parented into the vanilla tree, so it is **not** under `progenitor-ui-<pluginId>` and neither `DestroyRoot` (exact name) nor `DestroyByPrefix` (a DontDestroyOnLoad scan) can reach it. This is the one real hole the capability opens, and `VanillaAttachRegistry` is the whole of the fix: `Ui.RemoveAll(owner)` disposes that owner's attachments and **counts them in its return value** alongside panels, and Core Uninstall drains the table and every watch. The `SystemHooks` subscription is a Core-owned delegate and pins the collectible generation exactly the way a Harmony trampoline did, so it goes with the last attachment on its site — and whether the site still needs one is decided against the live table under the registry's own lock, so a watch built for a handle that was disposed while it was being installed is given straight back rather than filed for a site with nothing on it (R60 finding 7). A site that still owes a destroy and has **lost** its watch gets it rebuilt from the remembered factory by `VanillaAttachRegistry.RepairLostWatches`, which is driven from a `Plugin.Frame` heartbeat rather than from a site watch — the repair cannot be driven by the thing it repairs, which is what R62 finding 2 was about — and which gives up after five attempts with a line naming the site. **Five rebuilds, not five consecutive failures** (R63 finding 2): the count runs over the whole generation and is given back only where the site stopped needing a watch at all, so a site whose watch is rebuilt successfully and lost again every frame reaches the same give-up line, with one warning each way, instead of repairing and warning forever. The heartbeat itself is a pin of the same shape as the watch, so it lasts exactly as long as there is something it could repair: started by the first attachment that could need one, stopped the moment the last *enabled* attachment and the last watch are gone, restarted by the next one, and dropped at Uninstall. **Enabled, not merely tracked** (R65 finding 2): the disabled handle `Ui.AttachToVanilla` hands back for a site Core does not know, for a skipped module or off the client stays in the table until its owner disposes it, and it can never install a watch, lose one or be repaired for — so one of them used to keep the per-frame Core-owned delegate alive for the whole generation over a list that could not produce a single repair. The predicate all three decisions read is now "no watch that could drop and no enabled handle", and the pin line then says `attach=1 ... attachTick=False`, which is the true thing: something is held, nothing ticks. **Including the one interleave that used to escape it** (R64 finding 2): the tick is built outside the registry's gate, so the last handle can go while the factory is running and the stop — which only `Settle`'s remove arm can perform — then finds nothing to stop. That needs no lock held across a call into the Unity half to prevent, because the factory has already returned by the time the tick is filed: the filing lock re-asks the live table and disposes the tick instead of storing it, which is the same second-lock guard `InstallWatch` makes for a watch. All of it shows up in `pin-diag Ui` as `attach=<n> attachSites=<n> attachHooks=<n> attachTick=<bool>`, and the tick is still reported **even with nothing attached** — the way `UiSettleGate`'s pump is — because a tick with nothing to drive is the state that means an earlier teardown left one standing (R63 finding 1), and no path produces it any more, which is what makes it worth seeing.

## Vanilla panel sites (P6)

The same `Ui.AttachToVanilla`, pointed at the game's interaction windows: the storage container, the crafting and refinement stations, the player inventory, the castle heart, the item tooltip and the recipe tooltip. Each site reports **open, close and which entity the window shows**, and hosts buttons and rows that are clones of the game's own, so a mod's controls read as part of the window. Experimental. Open, close and the target were seen live on game build 1.1.13.0 (a chest, 2026-10-14). The buttons and rows were not: on that run no button was drawn, because the donor the first design cloned (`InventorySubMenu.SelfSortButton`) is empty in the shipped prefab. The anchors and donors below are rebuilt from the prefabs this build ships: **built, game-free tested, not live-verified** (see the live acceptance procedure below).

```csharp
using Progenitor.Ui;
using UiApi = Progenitor.Ui.Ui;

var chest = UiApi.AttachToVanilla(VanillaUiSite.ContainerPanel, VanillaPanelAnchor.LeftEdge, ctx.Owner, ctx.Guid);
ctx.Track(chest);
var deposit = chest.Button("Deposit");
ctx.Track(deposit.OnClick(() => SendDeposit(chest.Target)));          // your own request to the server
ctx.Track(chest.OnPanel(e =>
{
    if (e.Change == VanillaPanelChange.Opened) ctx.Log.LogInfo("chest " + e.Target);   // kind=Container entity=812:3 net=40:1
}));

var tip = UiApi.AttachToVanilla(VanillaUiSite.InventoryTooltip, VanillaPanelAnchor.TooltipBody, ctx.Owner, ctx.Guid);
ctx.Track(tip);
ctx.Track(tip.OnPanel(e => tip.SetRows(new[] { "In storage: " + CountInStorage(e.Target.ItemGuid) })));
```

### Sites and anchors

| Site | Window (how it is found) | Anchors (default first) | Target |
|---|---|---|---|
| `ContainerPanel` | `ContainerMenuMapper._ContainerSubMenu`, `._Target` | `LeftEdge`, `RightEdge`, `BesideRename` | the container |
| `StationPanel` | the open station window of the mappers in [Station windows](#station-windows), each `._XSubMenu` and `._Target` | `LeftEdge`, `RightEdge` | the station entity (the mapper's `_Target`); `Kind` is `Workstation`, `RefinementStation`, `PrisonCell`, `SalvageStation` or `UnitStation` |
| `InventoryPanel` | the active `_InventorySubMenu` of the container, station, castle heart or character window; the station windows include the prison cell, devourer and unit stations, so `InventoryPanel` and `InventoryTooltip` also resolve beside those (source-checked; live check pending) | `BesideSort` | the window's target beside a container or station; none (`Kind = Character`) in the character menu |
| `CastleHeartPanel` (alias `ThronePanel`) | `CastleHeartMenuMapper._CastleHeartSubMenu`, `._Target` | `LeftEdge`, `RightEdge` | the castle heart |
| `InventoryTooltip` | the inventory's (or the container half's) `FakeTooltip` while it is shown over a hovered `ItemGridSelectionEntry` | `TooltipBody` | `ItemGuid` and `ItemAmount` of the hovered stack; the container when the slot is in the container half |
| `RecipeTooltip` | the window's own `FakeTooltip` while it is shown over a hovered recipe entry of that window's `RecipesParent`: workstation, refinement station or the character menu's crafting tab ([Recipe tooltip](#recipe-tooltip)) | `TooltipBody`, `TooltipRequirements`, `TooltipRequirementLine` | `RecipeGuid`, `ItemGuid` (what the tooltip describes), the product as `ProductGuid` / `ProductAmount`, `Requirements`; the station and its `NetworkId` (none in the crafting tab) |

`ThronePanel` and `CastleHeartPanel` are the same value. The throne has no window of its own on this build (sitting on it is the `UseThrone` interaction); the castle list and the blood essence fuel live in the castle heart window, so both names address it.

### Recipe tooltip

`VanillaUiSite.RecipeTooltip` adds rows to the tooltip the game shows over a recipe: in a crafting workstation (`WorkstationSubMenu`), a refinement station (`RefinementstationSubMenu`: the furnace and the other converters) and the character menu's crafting tab (`InventorySubMenu.RecipesParent`). Status: **source-checked** against the client metadata, the client interop, the tooltip prefabs this build ships, and the native analysis of the code that fills each of the three tooltips; **built and game-free tested** (`VanillaPanelTests`, `VanillaRecipeTooltipTests`); **not live-verified**.

```csharp
var needs = UiApi.AttachToVanilla(VanillaUiSite.RecipeTooltip, VanillaPanelAnchor.TooltipRequirements, ctx.Owner, ctx.Guid);
ctx.Track(needs);
ctx.Track(needs.OnPanel(e =>
{
    if (e.Change == VanillaPanelChange.Closed) return;
    var rows = new List<VanillaRow>();
    foreach (var r in e.Target.Requirements)
        rows.Add(new VanillaRow("In storage: " + CountInStorage(r.ItemGuid), Enough(r) ? LabelTone.Ok : LabelTone.Warn));
    needs.SetRows(rows);
}));
```

- **Open** means the window's tooltip is shown and one of its recipe entries is hovered (`GridSelectionEntry.IsHovered`). The site asks the workstation, then the refinement station, then the shown inventory's crafting tab; the game shows one at a time. Moving to another recipe is a `TargetChanged`, so a plugin can fetch data once per hover; leaving the recipe closes it. Per-hover edges are not logged by the framework (as on the item tooltip); the attach line is said once per handle.
- **Target.** `RecipeGuid` is the entry's `EntryId`. `ProductGuid` x `ProductAmount` is the recipe's `RecipeOutputBuffer` row 0 (what one craft makes; 0 when the recipe has no row 0), and `Requirements` are its `RecipeRequirementBuffer` rows in order, read once per hovered recipe from the recipe's prefab. That is the order every one of the three tooltips lists them; the tooltip leaves out an item the game has no item data for. `Amount` is the recipe's own amount: the station's resource bonus can make the tooltip show less. `Kind` is `Workstation`, `RefinementStation` or `Character`; the station entity and its `NetworkId` are the window's target, and the crafting tab has none.
- **What the tooltip describes** (`ItemGuid`, `ItemAmount`) depends on the window:

  | Window | The game's tooltip is given | `ItemGuid` | `ItemAmount` |
  |---|---|---|---|
  | Workstation | output row 0 and the entry's scaled cost list | the product | the product amount |
  | Crafting tab | output row 0 and the entry's cost list | the product | the product amount |
  | Refinement station | the recipe's own `PrefabGUID` (`EntryId`) and the recipe's requirements scaled by the station's bonus | the recipe (`RecipeGuid`) | 0 |

  At a refinement station, use `ProductGuid` for the item the recipe makes.
- **Anchors.** `TooltipBody` (default) puts the rows right under the product's description; `TooltipRequirements` right under the requirement list (the "0/3 Glass" lines). Both are in the tooltip's own entry column, a vertical group, so the tooltip grows to fit. Rows from several plugins stack in the order they were built. `TooltipRequirementLine` puts them right under one requirement line (below).
- **Under one requirement line.** `UiApi.AttachToRequirementLine(line, ctx.Owner, ctx.Guid)` gives a handle whose rows sit directly under requirement line `line` (0 = the first; an index into `Target.Requirements`, so `Target.Requirements[line].ItemGuid` is that line's item). One handle per line; lines 0 to 5 (the tooltip's six pooled line rows); another line number gives a disabled handle and one log line. `AttachToVanilla(RecipeTooltip, TooltipRequirementLine, ...)` is line 0. The container is a child of the requirement list (`Tooltip_RecipeRequiredItems`, a vertical group), right after that line's row and any Progenitor container already there, so the following lines move down and the tooltip grows. The game's rewrite of the lines on every tooltip update only writes each line's texts, colors and shown state, never the list's children, so it neither moves nor removes the rows. The container is shown exactly while the game shows its line: a recipe with fewer lines hides it with the game's unused rows, re-checked after every window mapper's update so a new recipe never shows a stale line container for a frame. `ui_read panels` lists `requirementLine` and `gameLineShown` for such a handle. Status: game-free tested (`VanillaPanelTests`), built, **not live-verified**.

  ```csharp
  for (int i = 0; i < 6; i++)
  {
      int line = i;
      var under = UiApi.AttachToRequirementLine(line, ctx.Owner, ctx.Guid);
      ctx.Track(under);
      ctx.Track(under.OnPanel(e =>
      {
          if (e.Change == VanillaPanelChange.Closed) return;
          var needs = e.Target.Requirements;
          if (line >= needs.Count) { under.ClearRows(); return; }
          under.SetRows(new VanillaRow[] { new("In the network: " + CountInStorage(needs[line].ItemGuid), LabelTone.Ok) });
      }));
  }
  ```

- **Requirement counts at a refinement station.** The game counts only the station's own slots there. A material provider that names `MaterialOperation.RefineView` makes each governed line show the station's count plus the provider's pool total, in the game's format and colors ([modules.md, Refinement recipe tooltip counts](modules.md#refinement-recipe-tooltip-counts)); the per-line rows above are independent of it.
- **Rows only,** in the tooltip's description style; `SetRows(IReadOnlyList<VanillaRow>)` colors each row by `LabelTone`. Buttons are disabled.
- **Not read:** the entry's own requirement list (the amounts the tooltip shows) and the window's selection group. Both are interop generics over game value types, which the framework does not instantiate.
- **Refinement rows.** A refinement recipe is a row with input and output icons. Hovering an icon does not change the game's tooltip (the row's icon lookup always answers "none"), so the site reports the row's recipe whichever part of the row is hovered.
- **Mouse only (known limit).** Hover is the entry's `GridSelectionEntry.IsHovered`, which only the pointer sets. The game's tooltip follows its selection group's cursor (`HoveredEntryIndex`), which is also what a gamepad moves. With a gamepad the site therefore stays closed, or reports the entry the parked pointer last entered. The cursor lives on a generic selection group over a game value type, which the framework does not instantiate, so it is not read.
- **Cost.** The site scans a window's recipe entries only while that window's tooltip is shown and no recipe is known hovered (a station bonus or an output slot is showing). The scan is one field read per entry; a hover can change on any pointer move, so a "nothing hovered" answer is not cached across frames.

### Station windows

`StationPanel` covers every station window whose mapper keeps the window in `_XSubMenu` and the station in `_Target` and whose layout is a crafting or refining station's (bonuses, a recipe or input grid, outputs). The target is always the mapper's `_Target`: the station entity the player opened. `AttachPanelTabs` draws its strip in place of the game's station/redistribution tab row, so it draws only on windows that have that row. Status: **source-checked; live check pending** for the prison cell, salvage and unit station windows (member names checked against the game's metadata and the client interop; built and game-free tested in `VanillaStationWindowsTests`). The workstation and refinement rows are the ones already observed live. The prison, salvage and unit station mappers are each read and hooked on their own: if a game update renames one of their members, that window alone is dropped for the generation with one warning (`Ui: StationPanel window <Kind> unavailable ...`), and every other window and site keeps working.

| Window (mapper, submenu) | Prefab examples | `Kind` | `AttachToVanilla` edges | `AttachPanelTabs` |
|---|---|---|---|---|
| `WorkstationMenuMapper`, `WorkstationSubMenu` | workbench, tailor, alchemy table, anvil | `Workstation` | yes | yes |
| `RefinementstationMenuMapper`, `RefinementstationSubMenu` | furnace, sawmill, grinder, blood press, paper press | `RefinementStation` | yes | yes |
| `PrisonMenuMapper`, `PrisonSubMenu` | `TM_SpecialStation_PrisonCell` and its variants | `PrisonCell` | yes | yes; a custom tab hides the prisoner block, the recipe grid, the cell's slots and the status line |
| `SalvagestationMenuMapper`, `SalvagestationSubMenu` | devourer (by prefab name) | `SalvageStation` | yes (aligned with the input grid; there is no recipe grid) | yes; a custom tab hides the inputs, outputs, progress, status, the on/off toggle (`ToggleButton_Active`/`_Inactive`, `ToggleButtonText`) and the rename button |
| `UnitSpawnerstationMenuMapper`, `UnitSpawnerstationSubMenu` | `TM_UnitStation_Tomb`, `TM_UnitStation_VerminNest`, `TM_UnitStation_NetherGate` (by prefab name) | `UnitStation` | yes | no: the window has no tab row to replace, so the tabs treat it as closed (no `PanelChanged`) |

Windows read and left out of `StationPanel`:

| Window | Reason |
|---|---|
| braziers | a container window with a burn toggle (`ContainerSubMenu.BurnContainerUI`): use `ContainerPanel` |
| servant coffin (`ServantCoffinstationSubMenu`) | servant layout (portrait, perks, action box), no grid or tab row |
| research tables (`ResearchstationSubMenu`), spell school passive station | tree layouts, no grid or tab row |
| forge, fusion forge, jewelcrafting table, blood mixer, lightning rod, stable pen | own layouts without the station/redistribution row; not read in enough detail to place a column or strip |
| arena station, inventory routing station, music player, respec station | no inventory half; the target is a differently named field |
| traders | not a castle station |

### Anchors on game build 1.1.13.0

Read from the UI prefabs the game ships, not from a live run. Two serialized fields the first design used are empty on this build, so each anchor has a fallback (`VanillaPanelAnchors`, game-free tested against a fake hierarchy of these prefabs in `VanillaPanelAnchorsTests`). Status of every row: **built, game-free tested, not live-verified**.

| Site, anchor | Host (where the container goes) | How it joins | Button size |
|---|---|---|---|
| any panel, button donor | the inventory bottom bar (`BottomBarButtonsParent`, object `ButtonsLayoutNew`): the first live, **clone-safe** one of `SelfSortButton`, `AllSortButton`, `SmartMergeButton`, `SmartTakeButton`, `DropAllButton`, then any clone-safe button on the bar. Clone-safe means every graphic its state transitions drive is in its own subtree. On this build that is **`DropAllButton`** only: `SelfSortButton` is empty, and `SortButtonNew`, `SmartTakeButton` and `SmartMergeButtonNew` all have `DropAllButton`'s image as their target graphic | cloned, icon hidden, captioned | the bar button: 116x36 |
| `ContainerPanel` `LeftEdge` / `RightEdge`, `StationPanel` and `CastleHeartPanel` edges | the window's parent (`MenuParent`, a horizontal group that does not size its children) | `column`: ignored by the group, placed 8 units outside the frame | 116x36, widened to the caption |
| `ContainerPanel` `BesideRename` | `OpenTagEditButtonParent` when set; on this build it is empty, so the rename button's own parent (`ButtonLayoutParent`: a 24x24 right-aligned group beside the window title that keeps its children's widths) | `after`: a child of the group's parent (`Name`), placed right of the rename button on the title row, 8 units from it. Joining that group would push the rename button over the title. | the rename button's height (24, its authored size) at the bar button's proportions (77x24), widened to the caption |
| `InventoryPanel` `BesideSort` | beside the bar, never in it: a child of the bar's parent (`SharedForNowAtLeast`). The sort control is `SelfSortButton` when set, else `AllSortButton` (the only sort button on this build), else a bar button named like sort | `after`: right of the bar's last button (`SmartMergeButtonNew`) on its row, 8 units from it (else centered above the sort button, which does not fit on this build). Joining the grid would recenter it and move all four game buttons by 62 units | 36 high (the bar); text when the caption fits the 58-unit slot, else a 36x36 icon button with the caption as tooltip (the rule below) |
| `InventoryTooltip` `TooltipBody` | `FakeTooltip.DescriptionFrame`'s parent (a vertical group) | `tooltip`: the last line of the text column | rows only |
| `RecipeTooltip` `TooltipBody` | the same column (`InformationEntries`) | `tooltip`: right after `TooltipDescFrame`, above the requirement list | rows only |
| `RecipeTooltip` `TooltipRequirements` | the same column | `tooltip`: right after `Tooltip_RecipeRequiredItems` (`FakeTooltip.RecipeRequiredItems`) | rows only |
| `RecipeTooltip` `TooltipRequirementLine` | `Tooltip_RecipeRequiredItems` (a vertical group of six `RecipeRequiredItem` rows, `TooltipRecipeRequiredItems.RequiredItemsList`) | `tooltip`: right after that line's `RecipeRequiredItem` row; shown only while the game shows the row | rows only |

**`BesideSort` geometry on this build, at rest** (the window's own units, origin at its top-left corner; the game's scaler matches height to 1080, so 1920x1080 and 2560x1440 give the same numbers). The window is 620 x 925. The bar is centered: its four 116x36 cells run from 66 to 554, with the row at y -861 to -825. The slot right of `SmartMergeButtonNew` starts 8 units after it and ends at the window edge, so it is **58 units** wide (562-620). The item area (`ParentContainerInventory`, x 20-600, y -787 to -512) starts 38 units above the bar and never reaches the bar row.

**The rule for a `BesideSort` row,** applied when the window is at rest:

1. **Text at full size,** when the button (caption + 14 padding a side) fits the slot: a caption up to **30 units** at the game's 16-unit size.
2. **Text squeezed,** when it fits with the caption at no less than 0.75 of full size (12 units): a caption up to **40 units**, in a button narrowed to the slot.
3. **A square icon button,** 36 x 36 in the bar's art (36 + the 8-unit gap = 44 of the 58). The icon is the button's `VanillaButtonIcon`: an item's own game icon (`VanillaButtonIcon.Item(prefabGuid)`) or a drawn mark (`VanillaButtonIcon.Drawn("chest")`; the default is the drawn chest). The caption is its tooltip, shown on hover above every game and mod canvas, next to the icon, for as long as the pointer stays on the button. It does not need a mod panel to be open. A tooltip that cannot be shown is one warning per site and reason (`Ui: attach InventoryPanel icon tooltip not shown: <why>.`); the first one shown is one info line (`... icon tooltip shown on hover ('Storage').`). `Storage` and `Stash` both show this way. Several buttons on one handle need one icon each, 8 apart, so only one fits this slot.
4. **Hidden,** with `hidden reason=...`, when none of these fits. Every slot must lie inside the window and cover no game button and no part of the item area.

Slots that are rejected on this build:

- **Above the sort button:** the slot would start 8 above the bar and be 36 high, but the item area starts 38 above the bar, so it would cover the item area by 6 units.
- **Below the bar:** outside the window's content body, which ends at the bar's bottom. The 64 units down to the window edge are the window's bottom border, which leaves 20 units under a 36-unit row and its gap. It is not tried.

**At rest only.** The window slides in when it opens, 30 units off with its content area reaching past the window (live: the sliding frame read 642 wide and the slot 88; at rest they are 620 and 58). A placed container is judged only once the window frame and the bar have measured the same on three frames in a row. Until then it stays hidden, nothing is logged as a refusal, and it is measured again every frame. So a placement is never shown and then hidden, and the geometry line is the at-rest one: `Ui: attach InventoryPanel anchor=sort slot=<after|above|none> face=<text|icon|none> last=<rect> frame=<rect> row=<w>/min<w> placed=<rect> reason=<word>`. A placed container's refusal is asked again every 30 frames; a joined container's refusal stays final, because hiding it reflows its group. Only one placed container fits right of the bar; a second plugin's would overlap the first and is hidden. Game-free tested on these at-rest rects (`VanillaPanelAnchorsTests`), not live-verified.

**Controller.** The cloned buttons are not reachable with a gamepad, and harmlessly so. Each clone keeps the donor's navigation mode, which is None, so Unity's automatic navigation never selects it. The window's `ButtonSelectionGroup`s keep their own button lists, built from the game's buttons (constructor and `UpdateButtons`, from the metadata). A placed container is a child of the group's parent, not of the group, so a group built over that layout's children never sees it. This conclusion comes from the metadata, not from a live check.

An anchor a site does not list gives a disabled handle and one line: `Ui: attach InventoryPanel anchor LeftEdge is not an anchor this site accepts; returning a disabled handle.`

### API

| Member | What it does |
|---|---|
| `Ui.AttachToVanilla(site, anchor, owner, pluginId)` → `IVanillaAttachHandle` | Never null. `VanillaPanelAnchor.Default` picks the site's first anchor. Several attachments on one site (several plugins or anchors) share one frame watch. The old three-argument overload is `anchor = Default`. |
| `handle.OnPanel(Action<VanillaPanelEvent>)` → `IDisposable` | `Opened`, `TargetChanged`, `Closed`, with `Target` and `Previous`. Runs on the main thread inside the site's frame watch, **before** the frame's rows and buttons are synchronized, so a `SetRows` from the handler shows on the same frame. Subscribing while the window is open delivers `Opened` at once (a hot reload with a chest open). A handler that throws is logged and the frame continues. |
| `handle.IsOpen`, `handle.Target`, `handle.Anchor` | The current state, readable any time. |
| `handle.Visible` | The plugin's wish, kept across close and reopen. The container is drawn only while the window is open, attached and the placement fits, and then only if this is true. |
| `handle.Button(caption, icon)` → `IUiButton` | The same button, with the icon its face falls back to when the caption does not fit its slot: `VanillaButtonIcon.Item(prefabGuid)` (the item's own game icon), `VanillaButtonIcon.Drawn(name)` (a drawn mark: `chest`, `check`, `up`, `down`, `remove`, `grip`, `locate`) or `VanillaButtonIcon.Default` (the drawn chest). Only `BesideSort` falls back today; elsewhere the icon is unused. |
| `handle.Button(caption)` → `IUiButton` | A clone of the game's own inventory bar button (the donor row of the [anchor table](#anchors-on-game-build-11130); its art and its hover and press transitions, the icon and any donor label switched off), captioned with a clone of the label of the inventory's own tab buttons (font, material, color, size) and **sized to its caption** with the game's padding (never narrower than the bar button). Addressed by position like a row and **kept across close and reopen**: the caption, `Visible` and the Bootstrap click slot live on the handle, and each rebuilt clone is rewired to the same slot. One click handler per button, as every `IUiButton`: a later `OnClick` replaces the earlier one. A clone that cannot be wired is destroyed and logged rather than shown dead. Disabled on the map card and on the tooltip sites. |
| `handle.Row(text)` / `SetRows(...)` | Rows in an edge column (under the buttons) and the tooltip line, cloned from the game's body text (the tooltip's `Description`) unchanged. Inline anchors take buttons only: a text row inside a button bar would read as a broken button. |
| `handle.SetRows(IReadOnlyList<VanillaRow>)` / `handle.ClearRows()` | The same, with a color per row: `new VanillaRow(text, LabelTone.Warn)`; a `string` converts to a `LabelTone.Normal` row, which keeps the game's own text color. The other tones use the framework's label colors. A row's color is written only when its tone changes. Pass a typed array or list (`new VanillaRow[] { ... }`); an untyped `null` or `[]` matches both `SetRows` overloads and does not compile. `ClearRows()` removes every row. |
| `VanillaPanelTarget` | `Kind`, `EntityIndex` / `EntityVersion` (client world), the target's `NetworkId` as `NetworkId0` (`Normal_Index`), `NetworkId1` (`Normal_Generation`, padding cleared) and `NetworkId2` (the id type: 1 for every placed container, station and heart), with `NetworkIdIndex` / `NetworkIdGeneration` as shorthands; `ItemGuid` / `ItemAmount` (item tooltip: the hovered stack; recipe tooltip: the product); `RecipeGuid`, `ProductGuid` / `ProductAmount` / `Product`, `Requirements` (`VanillaRecipeRequirement`: `ItemGuid`, `Amount`) (recipe tooltip); `HasEntity`, `HasNetworkId`, `ToString()` → `kind=Container entity=812:3 net=40:1` (a recipe adds `recipe=<guid>`, `item=<guid>x<n>`, `makes=<guid>x<n>` when the product is not the item, and `needs=<guid>x<n>,...`). The id is read only when the interop's `NetworkId` is the 12 bytes the read assumes. |

**Mapping to the server entity.** The client entity means nothing on the server. The `NetworkId` is the identity the game itself uses for the same object on both sides: send `NetworkIdIndex`, `NetworkIdGeneration` and the type (`NetworkId2`) in your request and resolve them on the server with `ctx.Inventory.TryResolveNetworkId` ([below](#resolving-a-target-on-the-server)). It is session-local: never persist it, and never persist the client entity either; persist the `ContainerId` the resolver returns (P5).

**The seam.** Each site's watch is `SystemHooks.After<XMenuMapper>` with `HookOptions { OnlyWhenSystemRuns = false }`. Every one of these mappers is marked to update only while its queries match, so with the default option a watch would skip frames: a site that reads a second mapper (the station site reads the refinement mapper too; the inventory and tooltip sites read all eight) would miss opens and never see a close, leaving an edge column on screen. With the option off the watch runs whenever its mapper updates. All eight mappers are read once per frame into one shared scan, however many sites and plugins ask.

The mappers do not all update every frame. On 1.1.13.0, a watch on `InventoryMenuMapper` alone never sampled the bag beside an open chest; it first resolved when the character menu opened. A window can also close in the same frame as the only mapper that would have seen it closed. So every panel site hooks **all eight** window mappers and samples on the first of them to run each frame. While a site last read open, a frame watchdog (`Plugin.Frame`) samples it on the last world after two frames with no mapper update. A window that went inactive or was destroyed therefore reads closed on the next frame, and the tracker raises exactly one `Closed` per `Opened`, also when the chest and its bag close together (`Closing_a_chest_and_its_bag_in_one_frame_raises_exactly_one_Closed_per_open`).

### Resolving a target on the server

A panel target (or a `UiWorldPickResult`) reaches the server as three integers in your own request; the server turns them into the entity and its stable `ContainerId`, and can turn a `ContainerId` back into words the client finds. **Experimental**: source-checked, built and game-free tested (`NetworkIdResolverTests`, `UiWorldPickTests`); **not live-verified**.

```csharp
// Shared message types ([ModMessage] classes, see modules.md "Requests, large messages and subscriptions").
[ModMessage("mymod.target")] sealed class TargetRequest { [Field(1)] public int Index; [Field(2)] public int Generation; [Field(3)] public int Type; }
[ModMessage("mymod.target.ok")] sealed class TargetReply { [Field(1)] public bool Ok; [Field(2)] public string Id = ""; [Field(3)] public string Reason = "";
                                                          [Field(4)] public int Index; [Field(5)] public int Generation; [Field(6)] public int Type; }

// Server: the words are untrusted; the resolver validates them and never throws on bad input.
ctx.Requests.Handle<TargetRequest, TargetReply>((user, r, info) =>
{
    if (!ctx.Inventory.TryResolveNetworkId(r.Index, r.Generation, r.Type, out var id, out var chest, out var why))
        return new TargetReply { Reason = why };
    // Check the user may use this container (ctx.Castles.RelationTo) before acting on it.
    ctx.Inventory.TryGetNetworkId(id, out int i, out int g, out int t);   // words for the client, e.g. to Locate it
    return new TargetReply { Ok = true, Id = id.ToString(), Index = i, Generation = g, Type = t };
}, new RequestOptions { MaxRequestBytes = 64, Burst = 4, PerSecond = 1 });

// Client: send the window's target, then find the server's answer in this world and ping it.
var t = chest.Target;
ctx.Requests.Send<TargetRequest, TargetReply>(
    new TargetRequest { Index = t.NetworkIdIndex, Generation = t.NetworkIdGeneration, Type = t.NetworkId2 },
    res =>
    {
        if (res.Ok && res.Reply!.Ok && ctx.Hud.TryFindByNetworkId(res.Reply.Index, res.Reply.Generation, res.Reply.Type, out var e, out _))
            ctx.Hud.Locate(e, "Chest", 10);
    });
```

| Member | Side | What |
|---|---|---|
| `ctx.Inventory.TryResolveNetworkId(int index, int generation, int type, out ContainerId id, out Entity owner, out string reason)` | server | The words to the server entity and the stable id of the placed container, station or castle heart it names (issuing one as `IdOf` does). False with a reason for: type 0 (empty) or 2 (a static map object) or any other type, an index not above 0, a generation outside 0..255, an id no candidate carries (destroyed, from an earlier session or world load, or naming something else), a stale generation (the index now carries another), and an entity that has no container id (a player, a servant: `IdOf`'s reason). |
| `ctx.Inventory.TryGetNetworkId(ContainerId id, out int index, out int generation, out int type[, out string reason])` | server | The live container's network id words, to send to a client. False when the id is not live (`TryFind`'s reason) or the entity has no normal network id. |
| `ctx.Hud.TryFindByNetworkId(int index, int generation, int type, out Entity entity, out string reason)` | client | The client world's entity with that network id ([Locate](#locate)). |

How the server resolves: the game keeps its own network id map (`NetworkIdSystem.Singleton`, a native hash map from `NetworkId` to `Entity`). Reaching it from a plugin needs a new generic interop instantiation over a game value type, which this framework does not create, so the resolver builds its own map from the **same data**: the `NetworkId` component of every castle heart and every entity with `InventoryOwner`, read with the typed accessor the castle reader already uses. A hit is confirmed by reading the entity's id back (a destroyed or reused entity is never trusted from the map, and a request with a forged generation gets the stale reason without evicting the valid row); a miss or a stale row rebuilds the map, **at most once every 250 ms** however many requests miss, so a client sending made-up ids cannot make the server scan every frame (combine it with the request's own rate limit). A request that misses inside that window gets a reason saying when the lookup was refreshed; a container placed since is found on a later request. The map is dropped when the world epoch changes and at Core Uninstall. Authorization is yours: the resolver says *which* container the words name, not whether this user may touch it.

### A container window for a chest somewhere else

`ctx.Inventory.Preview.Open(containerId, options)` (client) opens **the game's own container window** for a chest
or brazier the player is not standing at, read-only, for the castle's owner and clan. It is the window this page's
container sites attach to, so a vanilla panel site on the container window is expected to see it like any other
container window, with `VanillaPanelTarget` naming the previewed chest (inferred from the source, not checked).
What changes on screen while a preview owns the window:

- The take-all, sort, rename and burn-toggle buttons are hidden with a CanvasGroup (alpha 0, no raycasts, not
  interactable) so the window's layout keeps its spacing. They only work at the chest.
- With `ContainerPreviewOptions.HidePlayerInventory` (the default) the player's bag panel is hidden the same
  way. That is the space a mod window (a Find list, for example) docks into, on the left of the container
  window, with `panel.Dock(UiDock.Left, …)` ([Docked panels](#docked-panels-dock1)). The pairing of the two is not live-checked.
- Escape closes the preview and the window after framework dialogs, key captures and the focused chat box
  (the same precedence as the world pick).

Everything is restored on close. The server rule, the read-only guarantee, the close reasons and the open live
plan: [modules.md, Container preview](modules.md#container-preview). **Not live-verified.**

### Layout and placement rules

These are the rules the code applies (`VanillaPanelPlacement`, tested at 1920x1080, 2560x1440 and 3840x2160). The game's HUD scaler matches height to a 1920x1080 reference, so the three resolutions lay out the same canvas and the rules are written in canvas units.

1. **Inline anchors go where the group's own controls are, the way the group allows.** The host group decides (`VanillaPanelAnchors.PlanInline`): a **grid** (the inventory bar on this build) takes our container as one more cell right after the reference control, so the grid sizes and spaces it exactly like its own buttons; a horizontal group that **sizes its children** takes it as one more member asking for its caption-fitted width (the donor's flexible width copied); any other group (the rename group on this build, fixed-size and keeping its children's widths), and the inventory bar for `BesideSort` whatever its layout, gets a container **placed beside it**. That container is a child of the group's parent (ignored by any layout there), in the first slot that is inside the frame and covers nothing: after the group's last visible member on its row, by the group's spacing (8 units when it has none), and for `BesideSort` also centered above the sort button. In the first two cases the group reflows when we come and when we go; for a placed container nothing vanilla moves. Destroying our container is the whole undo.
2. **The check runs after layout.** A joined group is rebuilt (`LayoutRebuilder.ForceRebuildLayoutImmediate`) before anything is measured; a rect that still cannot be measured is "not yet", which never hides anything and is asked again the next frame. Then every member of the group must lie inside the frame and ours must cover no vanilla member. The frame is the window's rect widened to the strip the group lives on: the first ancestor of the group at least 90% as wide as the window (the container's title strip sits above its window rect; the inventory bar's body is the window). A bar that does not shrink its children can push buttons past the frame; then ours is hidden (the group reflows back) with one line, `hidden reason=overflow:<name>`, and that verdict stands until the window is closed and reopened.
3. **Edge columns sit outside the frame.** `EdgeGap` (8 canvas units) from the window's left or right frame edge, top-aligned with the window's item grid (`EquipmentItemsParent` for a container, `RecipesParent` for a station; the frame top for the castle heart), one button per line, the column as wide as the widest caption-fitted button, with the inventory bar's spacing; rows follow the buttons. The column is ignored by any vanilla layout group (`LayoutElement.ignoreLayout`), so no vanilla element moves.
4. **A column that does not fit is not drawn.** If it would leave the canvas, or cover another window that is open at the same time (the player inventory beside a chest is checked as an obstacle), it is hidden with `hidden reason=offscreen` or `reason=overlap:<name>`, and shown again with `shown again` when it fits. There is no fallback position: a mod button floating somewhere else reads as bolted on. Use the other edge or `BesideRename` when one side is taken.
5. **When placement is checked.** On every build, on a change of row or button count, of the target or of `Visible`, and otherwise once every 30 frames (which follows a resolution change and an edge column's obstacle going away). Nothing is measured every frame.
6. **The tooltip line goes into the tooltip's own text column**, as its last line, cloned from `Description` (its font, size, color and spacing) with its `LocalizedText` removed, wrapping inside the description's width so the tooltip does not get wider. It is placed only when that column has a `VerticalLayoutGroup`, so the tooltip grows around the line; otherwise it is hidden with `reason=unlaid`. The game placed the tooltip before the line existed, so after it grows the tooltip is moved back inside its own clamp rectangle (`FakeTooltip.ClampWithinRect`); the game positions it again on its next show.
7. **Words.** Button captions are short verbs (`Deposit`, `Stash`), one or two words, as the game's own buttons. A button is as wide as its caption at the game's size plus padding; when a group squeezes it the caption shrinks to 10 units and then ends in an ellipsis, never past the frame. Never color a caption: the clone carries the game's own color.

The mockups these rules were drawn from (rendered over a 1080p capture of the character inventory) are in the external research notes. Captures of the container, station and castle heart windows were not available offline; the one-shot `Ui: site <Site> panel=...` line logs the rects the rules need, so the first live run checks them.

### Missing after a game update

Two cases, kept apart so one plugin's missing piece never takes a site away from another:

- **The resolver fails** (a member the game removed or renamed throws when it is touched): the site is **absent** for the Core generation, for every plugin, with one line. `pin-diag Ui` lists it as `missing=<Site>:<what>`.
- **A piece is missing** (a donor, an anchor's button group): one warning per site and piece naming every field that was tried, and only what needs the piece is withheld — a handle whose anchor group is gone gets no container (and says so once), while the site keeps resolving, still reports `Opened` and `Closed`, and every other anchor and plugin keeps working. A missing button donor withholds the buttons and rows for that frame, not for the session. `ui_read panels` shows the same thing per attachment (`withheld`, `anchorStatus`, [below](#reading-attachments-over-the-bridge)).

```
Ui: site ContainerPanel not found (anchor rename: ContainerSubMenu.OpenTagEditButtonParent and OpenTagEditButton); what needs it stays hidden until the game provides it.
Ui: site InventoryPanel not found (anchor sort: InventorySubMenu.SelfSortButton, AllSortButton and a sort button on BottomBarButtonsParent); what needs it stays hidden until the game provides it.
Ui: site ContainerPanel not found (button donor: InventorySubMenu.SelfSortButton/AllSortButton/SmartMergeButton/SmartTakeButton/DropAllButton and the BottomBarButtonsParent buttons are all missing); what needs it stays hidden until the game provides it.
```

When an anchor is found, one info line per site and anchor names where it went (again only when that changes):

```
Ui: site ContainerPanel anchor=rename ok host=Name group=ButtonLayoutParent mode=after via=OpenTagEditButton.parent donor=DropAllButton button=77x24
Ui: site InventoryPanel anchor=sort ok host=SharedForNowAtLeast group=ButtonsLayoutNew mode=after sort=AllSortButton donor=DropAllButton button=72x36
Ui: site ContainerPanel anchor=left ok host=MenuParent mode=column donor=DropAllButton button=116x36
```

The button size is printed but a change in it alone does not repeat the line. A button that cannot be built is one warning per site and reason, and `ui_read` shows it as `buttonFailure`:

```
Ui: attach ContainerPanel button not built: donor SortButtonNew refused by the clone gate (a state target outside its own subtree, or not a plain SimpleStunButton).
```

The `Ui: attached <Site> ... layout=` line says `column`, `group` (every inline mode) or `tooltip`; the `anchor=... ok` line above names the inline mode (`grid`, `group`, `after`; `BesideSort` and `BesideRename` are `after` on this build).

### Reading attachments over the bridge

`ui_read {"what":"panels"}` (MCP `progenitor_ui_read`) returns, beside the shown panels, `attached`: one entry per `Ui.AttachToVanilla` handle, in every state, so a bridge test can check an attachment without a screenshot.

| Field | Meaning |
|---|---|
| `site`, `anchor`, `plugin`, `container` | Which attachment (`container` is its GameObject name). |
| `enabled`, `open`, `target` | Disabled handles are listed too. `open` and `target` are what `IsOpen` and `Target` say. |
| `visible` | The plugin's `Visible` wish. |
| `attached`, `shown` | A container exists under the vanilla window; it is active (fits and visible). |
| `withheld` | Empty when attached. Else `site` (the window is closed), `donor` (no button donor), `container` (the anchor's group is missing: see `anchorStatus`) or `disabled`. |
| `fit`, `fitReason` | `ok`, `blocked` or `unknown`, and the last `hidden reason=` word. |
| `anchorStatus` | Panel sites: the last `ok host=... mode=... donor=... button=WxH` or `missing: <fields>`; `not resolved yet` before the window was open with this anchor attached; `absent` when the site's resolver failed. |
| `donor` | The field the button donor came from while the window is open (`DropAllButton` on 1.1.13.0). |
| `buttonFailure` | Why the site's last button clone failed (empty after a success). A declared button that failed is asked for again every 30 frames while the window stays open. |
| `slot` | Placed containers only: `after`, `above` or `none` (no slot fits; see `fitReason`). |
| `face` | Placed containers only: `text` or `icon` (the row fell back to icon buttons). |
| `buttons[].face`, `buttons[].tooltip` | Each built button's face (`text` or `icon`) and, for an icon, its tooltip: `text` (the caption), `watch` (the pointer watch is attached), `hovered`, `hoverEvents` (pointer-enter events received), `shown` (the box is up for this button) and `boxTitle` (the title of whatever tooltip box is up). |
| `buttons[]` | `index`, `caption`, `wanted` (the button's `Visible`), `built`, `shown`, `rect`. |
| `rows[]`, `rect` | The row texts; the container's rect while it is shown. Rects are screen pixels, origin bottom-left, like the panels' rects. |

### Release

Everything goes on dispose, `Ui.RemoveAll(owner)` and Core Uninstall, through the same registry as the map card: containers (with their cloned buttons and rows) are destroyed with the confirmed-destroy rule, every button's Bootstrap click slot is unwired and released, the panel handlers are dropped, and the site's frame watch goes with its last attachment. The resolver's references to vanilla windows, mappers and slot caches are cleared at Core Uninstall. No key is read anywhere in this capability; a mod that wants a key for one of these actions registers it through `ctx.Keybinds`.

### Live acceptance procedure (P6)

Run by the coordinator; not run for this change. Uses `samples/Progenitor.PanelsExample` (both sides: the client attaches the panels, the server only answers the `Locate` request; nothing moves an item).

1. Build and deploy Core and the example to the test client and the isolated test server (`progenitor_build`, then `progenitor_deploy` with side both and `Progenitor.PanelsExample`), join the isolated test server, and confirm `Panels example: loaded (tooltip enabled=True)` in the client log and `Panels example: loaded (server: resolve handler)` in the server log.
2. In a castle, open a chest, a workstation, a furnace, the castle heart and the character inventory in turn, closing each before the next. Without focus: stand within about 1.7 m (`teleport`) and use `bot {"action":"interact","entity":"<i>:<v>"}` on the client entity ([bot interaction](bridge-ops-client.md#bot-interaction)); otherwise walk up and interact (an authorized foreground step).
3. For each window, read `ui_read {"what":"panels"}` once: every example handle of that site has `open: true`, `attached: true`, `shown: true`, `withheld: ""`, `fit: "ok"`, its one button `built: true, shown: true` with a `rect`, `buttonFailure: ""`, `donor: "DropAllButton"`, `slot: "after"` on the two placed anchors, and an `anchorStatus` starting `ok` that matches the [anchor table](#anchors-on-game-build-11130). Then read the client log once (`progenitor_logs`, filter `Ui: site|Ui: attached|Ui: panel|Panels example|hidden reason|not found`) and expect, in order:
   - `Ui: site <Site> panel=<name>(x,y wxh) parent=<name> ... grid=... sort=...{AllSortButton} donor=DropAllButton(...){DropAllButton} bar=ButtonsLayoutNew(...)[group]{Grid} renameGroup=ButtonLayoutParent(...)[group]{OpenTagEditButton.parent} ...` once per site (`sort` and `renameGroup` only where the window has them);
   - `Ui: panel <Site> opened kind=<Kind> entity=<i>:<v> net=<n>:<g> plugin=progenitor.panelsexample` and `Panels example: <Site> <Anchor> Opened kind=...` — the net index is non-zero for a placed building;
   - `Ui: site <Site> anchor=<left|right|rename|sort> ok host=... mode=...` once per anchor, then `Ui: attached <Site> anchor=<...> fit=ok buttons=1 ... layout=<column|group>`; a `hidden reason=...` line instead names the rule that refused it; no `not found` line;
   - on close, `Ui: panel <Site> closed ...` and `Panels example: <Site> <Anchor> Closed ...`.
4. Capture each open window with the focus-free capture tool (`progenitor_capture`) at 1920x1080, then at 2560x1440 and 3840x2160 if the client can be switched: the buttons must sit on the bar or beside the frame as described above, in the game's art, not overlapping or clipped. Compare with the mockup.
5. Click each example button once (foreground step): one `Panels example: click <Site> <Anchor> '<caption>' kind=...` line each.
6. Hover two different items in the inventory and one in the chest: the tooltip shows `Example: item <guid> x<amount>` as its last line, in the tooltip's own style, changing with the item.
   **Recipe tooltip:** hover two recipes at a workstation, one recipe row at a furnace (its output icon and an input icon), and one recipe in the character menu's crafting tab. Expect one `Panels example: recipe kind=<Workstation|RefinementStation|Character> ... recipe=<guid> item=<guid>x<n> needs=...` line per hovered recipe (at the furnace `item=<recipe>x0 makes=<product>x<n>`, and no new line when moving between the row's icons) (the station's `net=` is non-zero, the crafting tab's is `none`), a muted `Example: recipe ...` line right under the description and one `Example: needs <n> x <guid>` line per requirement right under the requirement list (the first green), in the tooltip's own font, and the tooltip grown to fit (**screenshot** each). Read `ui_read {"what":"panels"}` once while a recipe is hovered: both `RecipeTooltip` handles `open: true`, `attached: true`, `rowTones` as shown. Then move through the workstation's recipes with a gamepad: expect no recipe line (the known limit above), and record what the game's own tooltip does.
7. Close and reopen the chest: exactly one set of example buttons, still clickable (the click line appears again).
   **Network id round trip** (the resolver, [above](#resolving-a-target-on-the-server)): with the chest open, click `Locate` beside the rename button (foreground step). Expect, in order: client `Panels example: click ContainerPanel BesideRename 'Locate' kind=Container entity=<ci>:<cv> net=<n>:<g>`; server `Panels example: resolve net=<n>:<g> -> entity=<si>:<sv> id=c-<32 hex> back=<n>:<g> type=1` (the same `n:g` back; the server entity is a different world's and need not match the client's); client `Panels example: locate c-<id> server=<si>:<sv> client=<ci>:<cv> same=True` and the Locate marker over the chest (**screenshot**). Cross-checks in chat as an admin, with `Progenitor.Sample` on the server: `.items net #k` for that chest (row from `.items chests`) prints the same `net=<n>:<g>` and id; `.items resolve <n> <g>` prints the same id; `.items resolve <n> <g+1>` is refused as `stale` or not found; `.items resolve <n> <g> 2` is refused as a static map object; `.items resolve 0 0` is refused as out of range. Destroy a spare chest whose words you noted, wait a second, and `.items resolve` them: refused (no container). Same words from a pick: with the Controls sample loaded, **Pick in world** on that chest logs the framework line `Ui: world pick #N picked Container <ci>:<cv> net=<n>:<g> via ContainerMenu …`, the same `n:g` as the panel; `.items resolve <n> <g>` gives the same id.
8. With a controller connected, open the chest and the inventory and move focus through the window. Expect the example buttons to be skipped by the controller (see **Controller** above), while the game's own buttons stay reachable as before. Record what happens.
9. Remove `Progenitor.PanelsExample` from the client reload folder; expect `Panels example: unloading`, `Ui: attach <Site> watch removed` for each site, no `leaked` or `retrying` line, and the next open of each window shows no example button. Disconnect and leave the client at the title screen.

## Bootstrap (restart required)

| Type | Role |
|---|---|
| `Progenitor.Bootstrap.UiSlots` | Process-lifetime `UnityAction` / `UnityAction<T>` pool. `TrySetDispatch` is Core's ushort-keyed handler. `TryWire` attaches to `SimpleStunButton.onClick`, Toggle/Slider/TMP events, and `EventTrigger` pointer enter/exit. Cleared on Core Uninstall. |
| `Progenitor.Ui.UiInputContext` | Injected once (`ClassInjector`, FullName frozen like `FrameTicker`). **Does not implement `IInputContext`:** the interop wraps it as a class with `GetConsumedInputs(ref BlockInputState)` (by-ref struct + NativeList), so a mod cannot author a context type from managed code. It does **cursor unlock only**: `Cursor.lockState = None` + `visible = true`, and forwards Escape / `Back` from `Keyboard.current` (press edge derived from held state, evidence 161). It consumes nothing — gameplay input blocking and Escape *consumption* are Core's `GameInputBlocker`, which pushes a game-owned `RotateCameraBlocker` at order 599 instead. Nested capture is refcounted; lock/visible is snapshotted on each 0→1 and restored on 1→0. |

## Tests must assert outcomes

Offline tests that pin *that a method was called*, or *that a source line exists*, will go green while the live client is unchanged. Four UI fixes in this area had that shape. One is worth naming: capture/restore of scroll position was called, the test asserted it was called, and the restored value was wrong — Unity had written 0 against empty bounds.

Assert values and relationships: the normalized position after restore, the rows one notch moves, whether `onValueChanged` is the event `TryWire` first sees, whether a row outside the viewport is a raycast target. Where an outcome cannot be observed offline, emit a diagnostic line and say in this page which line the live pass should read. Do not treat a source pin of `RestoreScroll()` as proof the list stayed put.

Diagnostic lines this module now emits, and what each tells you:

| Grep | Pass | Fail |
|---|---|---|
| `Ui: shown-input ` | `notch=1 applied=pump path=pump` with `pos=a->b` a 2-row step (ends clamp, so `a≈b` there); heartbeat once a second while shown | `notch=1 applied=none` (dropped); `applied=both` (two writers); `deltaY` non-zero with `notch=0` is a held reading, not a drop |
| `Mods workspace: scroll-preserve ` | `capture=` ≈ `restore=` (mid-list near 0.77), `applied=offset` | `restore=0` after a mid-list select — empty-bounds snap |
| `Ui: interaction probe` | `verdict=Live` | any other `verdict=`; `canvasAbove=… overlapsPanel=yes` for a click that misses |

`hoverPump=0` on a `WhileShown` workspace is expected. `hoverPump=1` means a `WhenHovered` panel is also shown.

## Expected log lines

`UiInputContext`'s one-time limitation line still says "Gameplay keys are not consumed (camera still moves)". That sentence is true **of that Bootstrap component**, which is a cursor-unlock fallback and nothing more. It is not a statement about the module: gameplay input is blocked by the Core-side context described above. Read it as the caption on the fallback, not on the UI.

```
UiSlots: ready (process-lifetime UnityEvent trampolines).
UiInputContext: IInputContext is an Il2CppObjectBase class with GetConsumedInputs(ref BlockInputState); ClassInjector cannot implement it from managed code. Falling back to Cursor.unlock + Keyboard.current Escape. Gameplay keys are not consumed (camera still moves). BlockInputState.BlockAllKeys is only valid inside GetConsumedInputs.
UiInputContext: injected (cursor-unlock fallback; IInputContext not implementable).
Ui: installed (client)
Ui: installed (server no-op)
Ui: Bootstrap click slot filled.
Ui: Panel('Sample') is client-only; returning a disabled handle.
Ui: panel 'Sample' under progenitor-ui-progenitor.sample
Ui: overlay scaling copied from the game's own canvas (HUDWheelCanvas): mode=ScaleWithScreenSize reference=1920x1080 match=1. Panel sizes are in the game's design space, not device pixels.
Ui: panel 'Sample' laid out rows=4 content=211u panel=283u avail=1080u ruler=canvas rank=0/1 at=(0,0) (fits)
Ui: panel 'Sample log' laid out rows=5 content=256u panel=328u avail=1080u ruler=canvas rank=1/2 at=(332,0) (fits)
Ui: show 'Sample'
Ui: pointer entered a mod panel; gameplay input is blocked (panels=1 window=Inside).
Ui: input context registered order=599 mask=live (BlockAllKeys + Move/Aim + Zoom; RotateCamera kept on keyboard+mouse; Back/Menu_Close taken in every scope so Escape closes the panel without also opening the game menu)
Ui: pointer left a mod panel; gameplay input is released (panels=1 window=Inside).
Ui: input context removed order=599
Ui: hide 'Sample'
Ui: uninstalled leftover=0 attachments=0
Diagnostics.SkipCoreModules: skipped module Ui
pin-diag UiSlots dispatch=True wired=0 pin-diag UiInputContext capture=False keys=True escapeEdgeOnly=0
```

Shown-input (once a second while any panel is shown; immediately on a notch, a typed key, or a click that armed a field). `hoverPump=0` is expected on an all-`WhileShown` workspace:

```
Ui: shown-input pump=1 shown=2 hoverPump=0 deltaY=120 notch=1 hit='Mods' pos=0.241->0.291 applied=pump path=pump content=2464 view=568 keys=0 focused=0 selected=none click=0
Ui: shown-input pump=1 shown=2 hoverPump=0 deltaY=0 notch=0 hit='Mods' pos=0.241 applied=none path=pump content=2464 view=568 keys=1 char='f' focused=1 selected=Search click=0
Mods workspace: scroll-preserve pane=list capture=0.77 restore=0.77 content=2464 view=568 applied=offset
```

Vanilla attachment (job FUI36). One line per outcome; the steady state is silent, because the refresh runs every frame the map is open:

```
Ui: attach MapWarEventInfo requested by progenitor.sample as progenitor-ui-progenitor.sample-MapWarEventInfo
Ui: attach MapWarEventInfo watch installed on MapMenuMapper
Ui: site MapWarEventInfo root=<name> instance=<id> parent=<name> children=<n> rootLayoutGroup=False rootSizeFitter=False parentLayoutGroup=False parentSizeFitter=False textGroupLayoutGroup=False textGroupSizeFitter=False donor=<name>
Ui: attached MapWarEventInfo parent=<name> layout=none rows=2 source=first instance=<id> plugin=progenitor.sample
Ui: attach MapWarEventInfo detached source=site plugin=progenitor.sample
Ui: attached MapWarEventInfo parent=<name> layout=none rows=2 source=instance instance=<newid> plugin=progenitor.sample
Ui: attach MapWarEventInfo watch removed
Ui: uninstalled leftover=0 attachments=1
pin-diag Ui handles=1 installed attach=1 attachSites=1 attachHooks=1 attachTick=True
```

The `site ... root= ... parentLayoutGroup= parentSizeFitter=` line is printed **once per process** and is the answer to the one question the offline research could not settle: what the hierarchy under the widget actually is, and *which* layout component — a group, a fitter, both or neither — governs the root, the parent and `TextGroup`. The group and the fitter are separate fields because they are separate answers and only the group changes what the code does. `source=` names why this frame rebuilt: `first`, `instance` (the map was closed and reopened), `site` (the map is closed), `donor`, `undefined`.

Lines that mean something is wrong here:

```
Ui: attach MapWarEventInfo has no frame watch; rows will not appear (site=MapMenuMapper).
Ui: attach MapWarEventInfo could not build its container; no rows will appear (plugin=<id>).
Ui: attach MapWarEventInfo donor clone came out without a TextMeshProUGUI; the row is dropped.
Ui: attach MapWarEventInfo read threw: <e>
Ui: DestroyImmediate: <e>
Ui: attach MapWarEventInfo container destroy did not take; retrying (container=<name> plugin=<id>).
Ui: attach MapWarEventInfo leaked 1 object(s) after 30 destroy attempts; container=<name> plugin=<id> (it is parented into the vanilla hierarchy, so no name sweep can reach it).
Ui: attach Uninstall left 1 object(s) whose destroy was refused; they stay in the vanilla hierarchy.
```

The last three are the destroy rule saying it failed. `Ui: DestroyImmediate: <e>` on its own is **not** one of them in the common case: the fallback `Destroy` that follows it usually takes, and a destroy Unity deferred is reported as deferred, followed silently and never warned about again (R61 finding 7). The `retrying` line means the destroy genuinely failed, and it is still recoverable — the next frame the map is open tries again. The `leaked` / `Uninstall left` lines are not: a frozen container with dead rows is sitting in the game's hierarchy under that name until the client restarts.

Settings panel and keybinds:

```
Ui: keybind 'progenitor.sample/ToggleDemoPanel' bound to F9 (Open or close the Sample UI demo panel.)
Ui: keybind 'progenitor.sample/ToggleLogPanel' bound to F9 + Ctrl (Open or close the Sample log panel (last Sample log lines in this process).)
Ui: settings panel opened rows=24 keybinds=3 sources=8
Ui: settings toggle source=built (no live vanilla toggle was both widget-sized and operable, so one was built)
Ui: settings waiting for a key for progenitor.sample/ToggleDemoPanel (Escape cancels; every keybind is suppressed until it lands).
Ui: keybind 'Sample demo panel' rebound to F8.
Ui: settings panel closed.
```

Lines that mean something is wrong, and what to do:

```
Ui: input mask not installed; the context blocks camera rotation only. Add 'ProjectM.RotateCameraBlocker$$GetConsumedInputs' to tools/addresslib/wanted-client.txt and rebuild (node tools/addresslib/build.mjs --side client).
Ui: input context registered order=599 mask=absent (... Escape is NOT consumed — one press closes the panel AND opens the game menu ...)
Ui: no vanilla CanvasScaler to copy; mod overlays stay at constant pixel size, so panels do not follow the game's UI scale. Logged once.
Ui: a settings toggle built from parts came out without the references that make it operable; it is destroyed and the row falls back to a plain label.
Ui: panel viewport threw: <e>; rows will not clip or scroll.
UiInputContext: an escape press arrived with wasPressedThisFrame already false and was recovered from the key's held state (Input System updateMode=...).
Ui: shown-input … notch=1 applied=none …          (a wheel notch was seen and nobody moved the list)
Ui: shown-input … notch=1 applied=both …          (pump and uGUI both wrote; scrollSensitivity should be 0)
Mods workspace: scroll-preserve … restore=0 …     (restore ran against empty bounds)
```

Widgets, pick in world and help (P7b; expectations from source, not captured output):

```
Ui: world pick #N picked <result> (K so far; repeating).
Ui: world pick #N ignored a repeat of the same target I:V.
Ui: world pick #N finished by Escape after K picks.
Ui: term '<term>' of <plugin> hovered <n> times; its marker is hidden (the explanation still shows).
Ui: tip '<id>' of <plugin> shown.
Ui: tip '<id>' of <plugin> marked seen (got it|show all tips|escape).
Ui: tip '<id>' of <plugin> dropped unanswered (<reason>).
Ui: tip '<id>' of <plugin> steps back while a dialog, key capture or list is open.
Ui: N tip card(s) wait for a dialog, key capture or list to close.
Ui: dropped N tip card(s) of an unloading plugin.
Ui: tips of <plugin> reset; each shows once again.
Ui: ctx.Hud.Tip is client only; on the server it does nothing.
```

A repeating pick logs `picked` per target and `finished by Escape` at the end; a repeat of the same target within ten seconds is `ignored`. The tip drop reasons are `disposed`, `closed`, `its panel closed`, `its panel closed before it showed`, `could not be drawn` and `its plugin unloaded`. The server line is logged once per plugin, naming the first `ctx.Hud` member called (`Tip` above). Warnings that mean something is wrong: `Ui: term marker could not be drawn: …`, `Ui: term '<term>' marker on … failed: …`, `Ui: term '<term>' hover on … failed: …`, and `Ui: [Ui] term marker and tip values could not be bound (…); hidden markers and seen tips are remembered for this session only.`

### Sample `.ui demo` (client)

Chat is server-side (`.ui` is registered like `.ember` / `.fx`); the server sends custom event `0x536D5569` (`SmUi`) to that user's client.

```
Sample: ui server Demo sent=True
Sample: ui demo open
Ui: panel 'Sample' under progenitor-ui-progenitor.sample
Sample: ui ember-stack 99
Ui: show 'Sample'
Sample: ui demo shown
Sample: ui click log
Sample: ui click close
Ui: hide 'Sample'
Sample: ui demo closed
Sample: ui toggle value=False
```

What you should see: a native-looking panel with a title, two buttons, a toggle bound to `Sample.UiDemoToggle`, and a label of the synced ember stack. The mouse is free. Moving the pointer onto the panel blocks gameplay input; moving it off releases it. Escape closes the panel and does not open the vanilla system menu. `.ui close` disposes the panel.

## Verification status

Several things here were confirmed on a live client today and several were not, and the distinction matters when a claim above fails for you.

| Behavior | Status |
|---|---|
| Overlay copies the game's `CanvasScaler` (evidence 165) | **Verified live** — the copy line appears in the client log with `HUDWheelCanvas`, `reference=1920x1080 match=1` |
| Rows clip, scroll and the panel sizes to content (evidence 163) | **Verified live** — a live `laid out rows=47 content=5466px panel=819px (scrolls)`. That line predates the `u` / `ruler=` fields; nothing since has been seen live, so it is quoted as it was logged |
| `ruler=` names the rung the height cap came from (R43 finding 5) | Landed, live check owed — no panel has been laid out in a running client since. The check is one F10: the line must read `ruler=canvas` on a build with a vanilla scaler, and `avail` must be the canvas height rather than the display's pixel height |
| Pointer-scoped input capture, `WhenHovered` default (evidence 177) | **Verified live** — both edge crossings driven with the real pointer, one `Register` / one `Remove` |
| Escape consumed, no vanilla menu, order 599 (evidence 178) | **Verified live** — `key F10`, `key Escape`, screenshot: panel closed, no system menu |
| Keybind edges derived from held state (evidence 161) | Landed, live check owed — hot-swapped into the running client; player confirmation that one press is enough is still owed |
| Every row type measures its caption (evidence 164) | Landed, live check owed — the top rows were confirmed correctly spaced in the player's next F10, the rest were not |
| `Ellipsis` clamp and the wheel no longer zooming (evidence 167) | Landed, live check owed |
| Panels tile into columns (evidence 173) | Landed, live check owed — the predicted offsets do appear in a live log (`rank=1/2 at=(332,0)`), but the screenshot check that two panels are readable side by side had not run |
| Scrollbar hidden when content fits; viewport inset fixed (evidence 175) | Landed, live check owed — the *defect* was found live, in a cropped screenshot; the `AutoHide` fix has not been re-checked |
| Settings editor controls are operable (evidence 176) | Landed, live check owed — designed offline against the source and the widget catalog; which controls clone a vanilla donor and which are built is answered by the next live run's log line |
| Register line reports `mask=live` / `mask=absent` as observed | Landed, live check owed |
| Shown-input pump arms whenever any panel is shown; hover pump does not run on an all-`WhileShown` set | **Source-checked.** Live workspace search was empty after click+type while the hover pump was off. Pump-driven typing is **not live-retested** |
| Real mouse wheel reaches `Mouse.current.scroll`; OS-injected wheel does not | **Verified live** (2026-09-10): injected wheel left `listScroll.position` unchanged and zoomed the camera with the panel closed; a real mouse moved the list |
| uGUI `OnScroll` starved; pump is the list mover (`scrollSensitivity = 0`) | Starvation recorded on a later real-mouse pass (workspace page: 13/13 `deltaY=120`, ~1/13 applied by uGUI). Pump-as-mover with inert uGUI is **source-checked**, **acceptance procedure — not run**: grep `Ui: shown-input `, expect `notch=1 applied=pump`, never `applied=both` |
| Scrollbar drag still goes through uGUI | **Verified live** — track click moved `listScroll.position` 1 → 0.72 |
| Wheel step is 2 rows from row height, not a fraction of content | Chunky fraction-of-content step **verified live** (~0.095 / ~4 rows on 51). 2-row pump step **source-checked**, offline tests pin 5–500 rows; live quantum **not re-measured** |
| Viewport `Image` + `RectMask2D` clip draw and hit-test | Defect **verified live** (search click selected a scrolled row). Fix **source-checked**, **not live-retested** |
| Scroll restore writes content offset against this pass's height | Snap **verified live** (select jumped the list). Offset restore **source-checked**; grep `Mods workspace: scroll-preserve `. **Not live-retested** |
| Selection is gold fill, not a `>` glyph; chrome is a footer band | Glyph-too-subtle **user-observed**. Fill + footer **source-checked**, **not live-retested** |
| `visibleRows` is the rendered subset | Defect **verified live** (`51`/`51` with ~14 on screen). Count is now viewport intersection, **source-checked** |
| `TMP_InputField` has `ActivateInputField` / `isFocused` / `Select`, no `shouldActivateOnSelect`; `Keyboard.add_onTextInput(Il2CppSystem.Action<char>)` | **Source-checked** against this machine's client interop (the IL viewer) |
| `Ui.AttachToVanilla` — the plan, the registry, the sweep and the log lines (FUI36) | Landed, **nothing live**. The game was not launched for this job. The pure half (close/reopen, rebuild, degrade, layout branch, `RemoveAll` / Uninstall sweep, pin line) is covered by tests |
| P6 panel sites (`ContainerPanel`, `StationPanel`, `InventoryPanel`, `CastleHeartPanel` / `ThronePanel`, `InventoryTooltip`, `RecipeTooltip`): resolution, anchors, buttons, open/close/target events, placement, missing-site line | **Experimental**, built and game-free tested (`VanillaPanelTests`: catalog, tracker, placement at three resolutions, buttons across reopen, click slot, blocked fit, missing donor, `RemoveAll`, recipe targets and toned rows; `VanillaRecipeTooltipTests`). Member names source-checked against the game's metadata and the client interop. **Nothing live**: the hierarchy under each window, its rects and whether the bars shrink their children are logged by the one-shot `Ui: site` line on the first live run |
| Widgets and `ctx.Hud` (P7: ordered list, picker, tabs, tooltips, code area, notifications, Locate, pick in world) | **Experimental**, source-checked, built and game-free tested (`UiWidgetsTests`, `UiCodeAreaTests`, `UiNoticeTests`, `UiLocateTests`, `UiWorldPickTests`); **live-verified on the test client 2026-10-14 through the bridge at 1920x1080 and 2560x1440, plus a 2x supersample capture (layout only)**, as regressions in the P7b passes: the widget pages on the Widgets page, notify (the game's banner and the band it falls back to), pick in world one-shot and repeat, Escape precedence. **Locate not verified live.** not verified: native 4K pixel density, the feel of a real-mouse drag, and controller hardware (navigation was driven through the bridge's virtual pad, not a physical controller). [Widgets acceptance procedure](#widgets-acceptance-procedure-run-2026-10-14) run 2026-10-14 |
| Checklist (CHECKLIST1: search and filter, sortable columns, the game's checkbox, sections, Select shown, Confirm, preview, suspend) | **Experimental**, source-checked, built and game-free tested (`UiChecklistTests`, `UiNavTests`); **not live-verified**. The [live check](#checklist-checklist1) has not been run |
| P7b widgets and help (data table, master-detail, chips, picker multi-select links, repeating pick in world, term tooltips, tip cards) | **Experimental**, source-checked, built and game-free tested (`UiDataTableTests`, `UiMasterDetailTests`, `UiChipsTests`, `UiPickerMultiTests`, `UiWorldPickRepeatTests`, `UiTermsTests`, `UiTipsTests`); **live-verified on the test client 2026-10-14 through the bridge at 1920x1080 and 2560x1440, plus a 2x supersample capture (layout only)**: the data table, master-detail, chips, term tooltips and markers, tip cards (`AnchorControl` included), the picker's Select all / Clear, repeat pick in world and the band's notice kinds. not verified: native 4K pixel density, the feel of a real-mouse drag, and controller hardware (navigation was driven through the bridge's virtual pad, not a physical controller). Acceptance procedures run 2026-10-14 |
| The confirmed destroy, its bounded retry, the split layout fields, the write-on-change guard and the watch race (FUI37, R60 findings 1, 2, 6, 7, 8) | Landed, **nothing live**. All five are covered by tests against the fake runtime; the `retrying` / `leaked` lines have never been seen in a real client, which is the point — they only appear when Unity refuses a destroy |
| `MapMenuMapper.GetMapMenu()?.WarEventInfo` resolves the **map** card and not the HUD popup | Inferred from the game's metadata plus a third-party mod's live docking. **Spike P2 / N4** |
| The hierarchy under the widget, and whether a `LayoutGroup` governs its parent | **Unknowable offline** — prefab serialized data. Both branches are implemented and the one taken is named in `layout=`; the one-shot `site ...` line reports the group and the fitter as separate fields for the root, the parent and `TextGroup`, so one run answers both halves. **Spike P3** |
| A cloned `TimerText` with `LocalizedText` removed shows a mod string and survives the vanilla per-frame `SetData` | Inferred: the row is not a vanilla child, so nothing rewrites it. **Spike P4 / P5** |
| Map close/reopen leaves exactly one container | Covered by tests against the instance-id rule; the *live* question is whether the view is destroyed or deactivated. **Spike N1 / N2** |
| Rows stay visible when `Hide()` runs (no event scheduled), and do not fade when the map is zoomed | The reasons the container is a sibling and not a child of `TextGroup`. **Spike N3 / N7** |
| `Ui.RemoveAll` and `.corereload` leave no orphan container and the generation collects | The registry and the watch teardown exist for this. **Spike N6** |

The [Mods workspace](mods-menu.md) is the player-facing directory: a main-menu button, a top-navigation Mods button beside the native tabs, a shortcut that opens through that native menu in a world, a two-pane host, and `ctx.Menu` registration. Its tab strip clones the native inner tab wherever a donor is reachable ([tab look per host](mods-menu.md#tab-look-per-host)). Live verification and remaining defects are on that page. Map layers, draggable/reorderable panels and vanilla Options input-action extension remain outside the implemented scope.

## Container and station tabs

`Ui.AttachPanelTabs(site, captions, owner)` supports `ContainerPanel` and `StationPanel` (the station windows with the game's station/redistribution tab row: workstations, refinement stations, prison cells and the devourer; see [Station windows](#station-windows)). The first caption represents the native inventory or production view; the remaining captions select custom content. Track the returned `IVanillaPanelTabs` with the plugin context. Subscribe to `PanelChanged` to resolve the current target, and to `SelectionChanged` to populate a panel, call `Mount(panel)`, then `Ui.Show(panel)`. Create controls after `panel.GameWindow()` to use the native settings layout.

`Selected = 0` restores the native view. Closing, changing target, disposing the owner, and Core reload restore native content and detach callbacks. Reject stale asynchronous replies when the target or selected tab changes. Mounted content scrolls inside the native window; it does not replace the inventory entity or its mapper.

On stations, `ShowRedistribution` optionally adds the game's redistribution view; the default is false. This controls visibility only, not native transfer ownership. Server integrations must inspect `ctx.Stations.RedistributionOwnership` before running competing automation. Changing visibility returns to the native production view.

For a separate manager window, `panel.GameWindow(width)` requests a centered native frame capped to the canvas width. Call before creating controls. The parameterless overload retains ordinary panel placement. Wide manager windows reserve 15% of the canvas at the bottom; mounted tab panels cap their height above the same HUD margin and scroll excess content.

On a station the strip sits 20 canvas units under the window's bonus row, is drawn after every other sibling in the window, and never rises above a parent mask's top edge. Its height is the character-window donor tab's height when that is between 24 and 96 units. Until then it uses 49 units, or more if the caption needs it, and re-reads the donor every frame; after that, every 30 frames. A tab read before the game laid it out is zero tall. The clipped prison cell captions fit a zero-height strip (inferred from screenshots; confirm with `tabHeightSource`). A self-check runs every 30 frames, and at most every 5 frames while the strip moves. It only reports: the strip is never re-sorted. It logs once per strip, after two checks agree (`Ui: panel tabs on <Kind> degenerate|clipped|covered (...)`), when the strip is too short, cut by a parent `RectMask2D`/`Mask`, or overlapped by a later sibling or an override-sorted canvas (named with its order). `ui_read panels` lists each strip under `tabs`: `site`, `kind`, `shown`, `selected`, `layout` (`placed` or `flow`), screen `rect`, `tabHeight` and `tabHeightSource` (`donor` or `fallback`), `top`, `panelTop` and `clamped` (root-local units), `masks`, `clip` (`ok`, `degenerate`, `clipped`, `covered`), `clipBy` and `visible` (share inside every mask).

Each tab's caption is the donor tab's own label, made a direct child of its tab and stretched over it 6 units in from each side, pivoted at its center, center-aligned both ways, without margins or wrapping. The donor's frame art comes with the donor's own anchors, position and size too, so it is refitted:

- Frame art is any of the button's state graphics that spans at least 60% of the largest drawing image's width or height: the frame, its state overlays, underlines, top rules and end caps. A button that names no state graphics uses 60% both ways instead.
- Each frame image is anchored where it sat in the donor tab, as shares of the donor tab's size, so its insets, any spill past the edges and its height scale with the new tab. It fills the tab when the donor tab had no usable size, or when the measured box reaches more than one tab past an edge.
- Objects between a frame image and the tab, or the frame image it sits in, fill their parent.
- A frame image with an enabled animator, layout group, content size fitter or aspect ratio fitter on its chain is left as the donor drew it. That is logged once per strip (`Ui: panel tab art left as the donor drew it, driven by ...`) and listed in `artDrivers`.
- Smaller images, such as icons and badges, keep their place.

Anything that moves a caption or a frame image later is put back on the next frame (counted in `captionFixes` and `artFixes`). Every caption is centered on its tab's art whatever its length.

`ui_read panels` → `tabs` also reports `captionsCentered`. It is true when, for every tab, the caption rect's center is within 1.5 pixels of the center of the tab's visible art box both ways, and the drawn text is within 1.5 pixels of it horizontally. The art box is the largest frame image in the tab that draws. Without one, it is the largest other image that draws. The tab's own root image counts only when nothing else draws, since it follows the tab's rect. Each image's box excludes its sprite's packing padding: scaled with the sprite for a simple or filled image, and over pixels per unit times the image's multiplier for a sliced one. A tiled image counts its whole rect. A tab with no visible art is never centered. Per tab, `tabCaptions` lists:

- the screen rects `tab`, `artBox`, `caption` and `glyphs` (the drawn text);
- `artDx`/`artDy` (the art box against the tab);
- `captionDx`/`captionDy` and `textDx`/`textDy` (against the art box) and `tabTextDx` (the text against the tab), all in pixels (x right, y up);
- `artDrivers`: every animator or layout driver on a frame image's chain, marked `(off)` when disabled;
- `art`: every image in the tab with its path, `visible`, `frame`, `fitted`, screen `rect` and `drawn` rect, `dx`/`dy`, its anchors, pivot, position and size, and its sprite's name, `type`, `border`, `padding`, size and pivot;
- `parentIsTab`, `font` and `fontSize`.

`textDy` is not judged: a line is centered on the font's line metrics, as on the game's tabs, so a descender lowers the glyphs. `captionDonor` lists what the donor's label brought before it was adopted: parent, anchors, pivot, position, size, margins, alignment, wrapping and the tab's state transitions. `artDonor` lists the first tab's images before they were refitted, and `stripArt` lists the strip's own baseline images with their screen rects.

Status: built and game-free lifecycle tests pass. Container and refinement tab selection, content mounting, native restoration and reload were observed on the isolated client at 1920x1080 and 3840x1080. Anvil Production/Input mounting and restricted-container content at 2560x1440 were also observed. The native font, tab height and silver divider are retained from the character-tab donor. Prison cell tabs were observed live with clipped captions; the screenshots fit a zero-height strip (inferred, not measured). The fix (donor height re-read with a fallback, mask clamp, self-check) is built and game-free tested (`VanillaTabStripLayoutTests`), live check pending. Off-center captions were observed live on a Fabricator (Production left of center; Input and Output right of center and low). Adopting and stretching the caption (Core 2026.10.84) centered the text on the tab's rect, but on an Iron Furnace the text still sat off the tab art, and the readback passed falsely because it measured the tab's rect. Refitting the frame art and judging against the art box is built and game-free tested; live check pending (`captionsCentered`, `artBox`, `art`, `artDonor`). Devourer tabs are source-checked; live check pending. Other station layouts, native 4K and physical controller input require their own qualification.

### Search input

Use `panel.SearchInput(label, initial)` for live suggestions. Its `OnChanged` fires while typing; `OnSubmitted` fires separately on Enter. Ordinary `Input` retains its end-edit behavior. `Focus(selectAll: true)` activates a visible field. Debounce network searches and discard stale responses in the consumer. Typing, Enter submission and focus were verified live in Find.

`Ui.TopPanel(panel)` places a panel near the top of the available area; it is intended for brief preview controls. Verified live with room-preview controls above the scene. To keep preview controls beside the scene instead, dock them ([Docked panels](#docked-panels-dock1)) and offset the preview's framing (`CameraPreviewOptions.ScreenOffset`).

Locate pins can opt into map and minimap display with the five-argument `ctx.Hud.Locate` overload ([parameters](#locate)). A positive arrival distance ends the pin based on real-character distance, including elevation. Map markers share the ping lifetime and disappear on dispose, timeout, disconnect or owner unload; they do not overwrite the player's native map marker.
