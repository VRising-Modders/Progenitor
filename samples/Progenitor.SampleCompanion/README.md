# Progenitor.SampleCompanion

A second small plugin beside [`Progenitor.Sample`](../Progenitor.Sample). It exists to show, on the Mods page, what a counterpart declaration and a mod problem look like; the source is [`Plugin.cs`](Plugin.cs). It has one config entry, one settings page and one keybind, `CompanionPing`, whose default is **F8**. The Sample's `NetPing` also defaults to F8, so the two conflict on purpose.

## Key conflict dialog

**Status: source-checked and game-free tested, not live-verified** (KEYS4, `tests/Progenitor.Tests/KeybindConflictTests.cs`, which uses the same two plugin ids and binds). The steps below have not been run.

The companion is the fixture for the [key conflict dialog](../../docs/mods-menu.md#key-conflict-dialog). A dedicated sample was not added: this pair already ships, conflicts on a real key, and is hot-reloadable, which is everything the dialog needs.

Which of the two loses F8 is the winner rule's call: the mod installed first keeps it, and when the install times are the same or unknown, `progenitor.sample` comes before `progenitor.samplecompanion` by id. The dialog is about whichever mod lost, so it appears either way.

To reproduce it on the Steam client (Core and both samples deployed from the same tree; bridge calls through `progenitor_command` / the Dev bridge, port 27341):

1. Put CompanionPing back on F8. A reset from the Keybinds tab is refused while the Sample holds F8 (`Its default, F8, is now used by …`), because a remap may never create a conflict. Instead, with the companion loaded, edit `BepInEx\config\progenitor.samplecompanion.cfg` and set `CompanionPing = F8` under `[Keybinds]` (or delete the line to fall back to its default). The running plugin does not reread its cfg until it reloads.
2. Hot-reload the companion (`progenitor_reload` with the companion's project, client side; see [hot-reload.md](../../docs/hot-reload.md)). It registers `CompanionPing` on F8 again.
3. On the next frame the dialog appears, over the HUD in a world, over the title screen, or over the Mods workspace. Read it: `mods_ui {"action":"keys","op":"conflict"}` and `mods_ui {"action":"status"}` (`dialog.kind: key-conflict`, `dialog.keyConflict.plugin`).
4. Answer it, through the dialog or `mods_ui {"action":"keys","op":"conflict-choose","target":"give|leave|edit|later"}`.
5. To see it again after **Give** or **Leave**, repeat from step 1 (and put the other mod's key back on F8 the same way after a Give). After **Later**, a hot reload does not ask again; restarting the game does.
