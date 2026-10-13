# Boss variants example

Six Frostmaws (`CHAR_Wendigo_VBlood`) near the caller, all on `Faction_Bandits` so they do not down
each other:

| Variant | Count | Behavior | How |
|---|---|---|---|
| vanilla | 2 | Frostmaw as shipped: melee, icicle, frost nova; Snow Storm below 60 %, Ice Beam below 50 % | `ctx.Units.Spawn(..., new UnitSpawnOptions { Faction = ... })` only |
| Frenzied | 2 | Snow Storm and Ice Beam from full health, frost nova on an 8 s row cooldown with weight 150, no melee, downed at 25 % | per-instance cast options (**experimental**), health gates at 1.0, melee row at weight 0, phase threshold 0.25 |
| Sentinel | 2 | Chases, strafes and relocates, never casts | private tree copy with `DisableCombat()` (**experimental**) |

**Ice Beam in a group.** In vanilla, Ice Beam’s hit has no team check, so a beam aimed at the
player also damaged the other Frostmaws in its line (live A/B, 2026-09-24). Core’s ally-safe default
guards it, so the Frenzied variant casts it from full health without hurting the group. See
[docs/ai.md](../../docs/ai.md), "Ally-safe damaging hits".

Read [Plugin.cs](Plugin.cs). It uses only `ctx.Ai`, `ctx.Units`, `ctx.Commands` and `ctx.Log`. The
API contract, safety rules and defaults are in [docs/ai.md](../../docs/ai.md); `ctx.Units.ClearArea`
is described in [docs/modules.md](../../docs/modules.md#clear-an-area).

**`clear` verification.** The `ClearArea` version of `.bossvariants clear` is source-checked,
compile-verified and its policy unit-tested (2026-09-24, offline); it is not yet verified live.

**Verification.** Source-checked against the current API and compile-verified (Release build on
2026-09-24, offline). Verified live on an earlier build (Core b366e2da, sample e2621273, isolated
server): the six bosses spawned on `Faction_Bandits` (`Team` 2/0, no friendly damage) and the
Sentinels cast 0 times while vanilla cast 64. The Frenzied variant was refused on that build (cast-option
clone) and the marker did not stay on the units; both paths have changed since and are not re-run.
**Revert or remove the bosses before a save** (`.bossvariants clear`): a saved Sentinel reloads with a
null tree, which aborted the server once a player came near (INC-20260924-02); Core now repairs it
to vanilla on load, but that repair is not yet live-verified. The mechanisms it uses were
proven live by hand on Frostmaw (tree copy with the cast node disabled, health gate copy, row edits,
per-instance phase threshold, faction index 0 for 100 bosses); the example's own code, the
per-instance cast-option clone and the variant marker's persistence are not verified. Server only;
it is not in the default deployment set.

## Build offline

From the repository root, with the game interop references configured:

```powershell
dotnet build samples/Progenitor.BossVariantsExample/Progenitor.BossVariantsExample.csproj -c Release
```

Expected: the build succeeds and writes `Progenitor.BossVariantsExample.dll` under
`samples/Progenitor.BossVariantsExample/bin/Release/`.

## Commands (admin)

| Command | Does |
|---|---|
| `.bossvariants spawn` | Spawns the six bosses in a 5 m ring around you; variants are applied 1.5 s later (after the tree binding resolves). |
| `.bossvariants status` | One line per boss: variant, behavior state, health, target, current cast, team/faction, tree variant, recent casts. |
| `.bossvariants engage` | Points every example boss at you and requests combat. |
| `.bossvariants clear` | Reverts the variants, then `ctx.Units.ClearArea` within 50 m of you in `Owned` scope with `IncludeUnattributedDrops`: the bosses this plugin spawned, their summons (including ones summoned while they are being removed) and the loot drops in that radius. Loose items inside the radius that nobody can be attributed to go too; player-owned items never do. Bosses of this plugin outside the radius are despawned through their handles. Bosses restored from a save have no Units handle: they are reverted to vanilla and left in the world. |

## Verify in an authorized test session (acceptance procedure — not run)

Prerequisites: the isolated test server with a compatible Progenitor Core, one connected admin test
character in open ground away from other NPCs. Never the live server; never a client the user is
playing on.

1. Build as above and deploy only this project to the server:

   ```powershell
   powershell -NoProfile -File tools/deploy.ps1 -Projects Progenitor.BossVariantsExample -Side server -NoBuild
   ```

2. Server log: expect `Boss variants example: loaded` and two `Ai: variant '…' defined` lines.
3. `.bossvariants spawn`, wait 3 s, `.bossvariants status`. Expect six lines, all `team=2/0`, two
   `Frenzied`, two `Sentinel` with `tree=variant`, two `vanilla`. The log shows one
   `Ai: tree variant …` line, one `Ai: cast-option clone …` line and six variant/faction writes.
4. Fight at full health for two minutes (keep the character protected with the Dev `protect` op).
   Expect from `status`: Frenzied `recent=` lists `SnowStorm` / `IceBeam_First` and never
   `MeleeAttack`; vanilla lists only melee, icicle and frost nova; Sentinels list nothing. No boss
   loses health except to you (Core’s ally-safe default guards Ice Beam).
5. Damage one Frenzied boss to about 25 %: expect it downed. Damage a vanilla one to 25 %: expect
   nothing.
6. `.bossvariants clear` while at least one boss is summoning and some loot is on the ground. Expect
   the reply to count the bosses, summons and drops removed by pass 1, then the server log lines
   `Units: ClearArea pass N/4 …` and `Boss variants example: clear finished: … remaining=0 complete`.
   On the client, expect the six bosses, every summon and the loot gone within a few seconds, with
   no stale copies.
7. Persistence (optional, needs a server restart): spawn, trigger a world save, restart the server,
   wait 30 s, `.bossvariants status`. Record whether the Frenzied and Sentinel bosses were re-applied
   (`(restored from save)` lines). Then `.bossvariants clear` and remove the reverted bosses with the
   Dev bridge.

Record the deployed hash, server log range, status replies and cleanup externally.
