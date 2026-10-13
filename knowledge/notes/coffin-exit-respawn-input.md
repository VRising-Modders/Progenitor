---
title: Leaving a coffin is the Respawn input, not Interact or movement
game: V Rising
game_build: Steam 23521359
progenitor: 09ed4732 or earlier (bot driver as of 2026-09-27; the deployed revision was not recorded)
side: both
area: bots
tags: coffin, sleep, input, entityinput, respawn, buff, headless
route: bot drive action "hold Respawn" (synced button 1024) read by server SleepInsideSystem
status: verified-live
date: 2026-09-27
evidence: internal research notes (bot coffin exit)
---

## Goal

Get a headless bot out of its wooden coffin (`TM_SpecialStation_WoodenCoffin`) after it slept, the way a vanilla player does.

## Route

Press the synced `Respawn` button through the existing bot drive action: `bot {action: "hold", buttons: "Respawn", seconds: 0.3}`. No new event kind. Rejected, all tried live and all left the sleep buff in place: hover plus hold Interact 1 s, tap Interact, tap Interrupt, tap TravelSkill, taps and holds of Primary, WeaponSkill1 and OffensiveSpell, and a `move` command.

## What the engine did

- Inside the coffin the character carries buff `AB_Interact_GetInside_Owner_Buff_Base` (381160212) with `InsideBuff {ExitBuff: -997204628}`, `SpawnSleepingBuff`, `SpellTarget` and a 999999 s `LifeTime`. It has no `DestroyBuffOnMove`, `DestroyOnManualInterrupt` or `DestroyOnAbilityCast` (live component read).
- Server-only `ProjectM.SleepInsideSystem` pins the sleeper to the coffin each frame and spawns `InsideBuff.ExitBuff` (a travel buff out of the coffin) only when the character's `EntityInput.State.InputsDown` has the `Respawn` bit (1024). Source-checked from the server native code; see the evidence.
- The same 1024 bit wakes a new character from `AB_Interact_CoffinSleeping`.
- Other ends of the buff are not player exits: the coffin dying or being dismantled, or becoming non-interactable.

## Verification

**Verified live** 2026-09-27 on the isolated test server with HeadlessOne: hold Respawn 0.3 s removed buff 381160212 and walked the character out, confirmed by component read-back. The default keyboard binding for Respawn was not checked. The game build was not recorded at the time; the stamp is the installed client build, unchanged since 2026-08-25.

## Gotchas

### Interact and move do nothing in a coffin

- **Symptom:** the bot hovers the coffin and holds Interact, or sends `move`; the sleep buff stays and the character stays pinned.
- **Cause:** the exit is read from the `Respawn` down edge only; the buff has no move or interrupt destroy components, and the bot's `move` does not write the analog values a move-destroy check would read anyway.
- **Fix:** `hold Respawn` for about 0.3 s. A hold beats a tap because it guarantees one down edge; a longer hold is harmless since the system checks for the exit buff before spawning another.
