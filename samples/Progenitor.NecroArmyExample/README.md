# Necro army example

A filmed scene in Dracula's throne hall. A necromancer (the admin running the commands) raises a 42-unit skeleton army out of the ground, points at Dracula, and the army charges while she keeps her distance and casts unholy spells at him. When Dracula falls she stops and bows. The scene is split into commands so any shot can be retaken without restaging.

It exists to exercise, together and live, the Core APIs a scripted scene needs. Read [Plugin.cs](Plugin.cs) (server: scene logic and commands), [NecroCamera.cs](NecroCamera.cs) (client: camera director, HUD, scenery), [AimLock.cs](AimLock.cs) (client: aim, strafing and dash through the player's input) and [Formation.cs](Formation.cs) (army slots). Research notes and recorded takes are in the external research repository under `reviews/2026-09-25-creature-customization/necro/`.

## Commands (admin only)

| Command | What it does |
|---|---|
| `.arena setup [distance] [bossHealth] [heading]` | Dress her (outfit, 1.5x size, green glow), 100 % Draculin blood, spells on R/C/T, no cooldowns, clock held at 23:00, breakable props cleared, Dracula held 11 m away. |
| `.arena reset` | Despawn the army and Dracula, give back her bar, release the clock and cooldowns, camera to the game's own. |
| `.army summon` | Raise the army around her, back rank first. |
| `.army attack [delay] [direct]` | Cut to a side shot, wait until she faces Dracula, point, and after `delay` s send the army. |
| `.army status` | Army count, stances and Dracula's health. |
| `.cast <spell>` / `.barrage [seconds] [spells]` | One spell, or spells in turn until Dracula falls; then she bows. |
| `.camera <shot>` / `.look x y z lx ly lz` | Fixed shots, the fight director (`director`), or the game camera (`top`). |
| `.aim lock|move|free` | Face and aim at Dracula every frame; `move` also keeps spell range, strafes and dashes away; `free` hands the player back. |
| `.hud on|off`, `.lighting film|default` | Clean footage: HUD down to the ability bar, no cursor; optional film grade. |

A take: `.arena setup`, `.hud off`, `.aim lock`, `.camera summon`, `.army summon`, `.army attack`, `.barrage 2.5`, `.aim move`.

## Core APIs it uses

`ctx.Units` (spawn options `Permanent`, `DropLoot`, `Entrance`; `SetPet`, `HoldAt`, `Engage`, `TeamOf`, `SetLook`, `HideHealthBar`, `GetHealth`, `SetBlood`, `ClearBreakables`), `ctx.Abilities` (`ForceCast`, `NoCooldowns`, `SetSlot`), `ctx.Time.HoldTimeOfDay`, `ctx.Camera`, `ctx.Content.Edit` (Corrupted Skull reach 12 m to 20 m, restored on unload; see [modules.md](../../docs/modules.md)), `ctx.Net` (camera cues and the dash request), `ctx.Hooks` (the aim writes around `GameplayInputSystem` and `CreateInputCommandsSystem`).

The aim lock is sample code, not an API: it writes the local player's outgoing input (`EntityInput` on the input controller entity) every frame while locked, and never moves the operating-system cursor.

## Verification

Built offline and deployed to the isolated test server and the test client on 2026-09-25/26; the full test suite passed. Observed live on that server with one connected client:

- Complete takes 11, 12, 13 and 14: summon, point, charge, spells cast at Dracula, his fall and the bow, recorded to video. The side-on point, broadcast director and the R/C/T bar were filmed in take 16 (68 s, stopped by another window). The 20 m skull reach has not been filmed yet.
- The aim through the controller entity: the outgoing input command carried Dracula's position and Dracula as the hovered target, read back from the client's command buffer.
- Spells on R/C/T set and given back (`SetSlot`), the skull's lifetime edit on both sides, the side-on point shot and the broadcast-style director.
- The blood change and the prop clearing were run live as snippets with the same recipes. The Core wrappers `SetBlood` and `ClearBreakables`, written afterwards while the test server was down, have unit tests but have not yet run live through `.arena setup`.

Needs Dracula's throne hall (the hall bounds and chandelier positions are hard-coded) and a player with admin rights. The breakables and the pillar respawn behavior are specific to that hall.
