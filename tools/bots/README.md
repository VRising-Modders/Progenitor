# Headless player bots

> **Experimental, early development — not a supported Progenitor feature.** These scripts and the Dev bridge `bot*`
> ops are an experiment in AI-driven players. They drive headless test clients (game clients run in
> batch mode on the developer's own PC) through the [development bridge](../../docs/bridge.md#bridge-wire-protocol),
> together with server-side bridge ops on the isolated test server. They are development-only tools:
> nothing here is part of the framework API, and nothing bot-related ships in the Release package
> (`tools/package.ps1` builds only Bootstrap, the Preloader and Core, and fails the package if a
> `Progenitor.Dev` file is staged; the `tools/bots` scripts are never copied). The bots, their ops and
> this page may change or be removed at any time. Never run them against a server you do not own.

Scripts that play V Rising through headless game clients connected to the isolated test server,
using the Dev bridge ops. A bot has the same input and knowledge as a human player's client. Server
ops only read state the player could see, or send the event a key press would send.

## Layers

| Layer | What | Where |
|---|---|---|
| 1. Hands | Client op `bot`: move, walk, aim, hold/tap buttons, hover, stop, status | `Bot.drive`, `moveTo`, `aim`, `hover` in `bot.mjs` |
| 2. Eyes | Client op `bot_look`: the bot itself plus nearby units, resources, breakables, structures and players, nearest first; hostiles are remembered as tracks (90 s, V Blood 10 min, death spots 30 min) and weighed by one threat measure, `threatAt`, for prey, guards, training targets, food roams and flight | `Bot.look`, `Bot.self`; `threat.mjs` |
| 3. Legs | Server op `bot_path` (the game's pathfinder) plus `goTo`/`explore`, which jump down ledges the navigation graph does not link; `reachable()` path-checks a target before a skill commits to it, and `Progress` ends approaches and chases that stop gaining | `Bot.path`, `goTo`, `explore`, `reachable` in `bot.mjs`; `reach.mjs` (Progress, blacklist, typed failures) |
| 4. Skills | Async functions over one bot returning `{ok, ...}` or `{ok:false, reason}` | `skills.mjs` (crypt, recover, feed, fight, gather), `progression.mjs` (pickup, craft, equip, heart, journal) |
| 5. Brain | Goal-priority loop: observe, `decide(snapshot)`, run one skill, record the outcome | `brain.mjs` |
| Goals | Personas (builder, warrior, balanced) ordering long-term goals; pure planners for home, gear, castle and upkeep | `goals.mjs`, `layout.mjs` |

`bridge.mjs` is the wire client. It re-reads the token on each call, retries "already running" refusals
and refuses token files under the live dedicated server. Client bridge for bot `i`: port `27341 + 10*i`,
token `%TEMP%\progenitor-client-i\progenitor-dev.token`. Server bridge: `PROGENITOR_BOT_SERVER_PORT`
(default 27362) and `PROGENITOR_BOT_SERVER_TOKEN_FILE`.

## Running

Only run these against headless clients and the isolated test server, and only when a live test is
authorized (see `CLAUDE.md`).

```
node tools/bots/supervise.mjs --bots 1:HeadlessOne:builder,2:HeadlessTwo:warrior,3:HeadlessThree:balanced --hours 8
node tools/bots/brain.mjs --bots 1:HeadlessOne:builder,2:HeadlessTwo:warrior --minutes 20
node tools/bots/run.mjs <break|look|goto|feed|leave|recover|gather|fight> --bots 1:HeadlessOne [...]
node --test "tools/bots/test/*.test.mjs"     # pure unit tests, no bridge
```

`supervise.mjs` keeps the bots playing: it runs the brain in rounds (`--round`, default 20 minutes; code
changes are picked up at the next round), restarts it after a crash, refuses to start while another
supervisor holds `%TEMP%\progenitor-bots\supervisor.pid`, and stops when `STOP` appears in that folder.
Logs: `%TEMP%\progenitor-bots\brain-<date-hour>.log`. Only one driver may run per bot: two brains on the
same bots corrupted a live run. When stopping by hand, kill `node` processes whose command line matches
`*bots*brain*` or `*supervise*`.

## Personas and goals (`goals.mjs`)

A persona orders the long-term goals; the survival rules in `decide` always come first.

| Persona | Goal order | Stock work | Blood it keeps |
|---|---|---|---|
| `builder` | home, heart, castle (full plan), journal, gear, boss | trees, rocks | Worker, Creature |
| `warrior` | home, gear, boss, journal, heart, castle (starter plan) | hunting, rocks | Warrior, Brute, Rogue |
| `balanced` | home, then journal/heart/gear/castle/boss taking turns | rocks, trees, hunting | Brute, Warrior, Rogue, Creature |
| `trainer` | none: fights fair targets all day, rotating weapons every 6 fights | training | Warrior, Brute, Rogue |

- **home**: walk back to the castle when more than 120 m away, or leave the starting graveyard for the
  nearest free plot. A trip starts only above 70% health (rest first). On the way the bot fights what it
  can take (level up to gear + 2) and runs past only stronger units, dashing along a short routed path.
- **gear**: craft and wear the best *learned* recipe per slot (AlwaysUnlocked cosmetics are ignored),
  in hand or at a castle station (opened first), gathering or hunting missing ingredients. Once the
  Vermin Salve recipe is learned, keep two salves.
- **castle**: upkeep first (fuel a decaying heart; top it up with 20+ carried blood essence every 30
  minutes; feed the mist brazier with 40+ bones and switch it on), then the plan in `CASTLE_PLANS`:
  5 floors, the coffin as soon as it unlocks (so deaths respawn at home), 3 walls, more floors, stash,
  brazier, workbench, at spots from `layout.mjs`. Each footprint is cleared first (trees cut, rocks
  broken). A refused spot is skipped for 10 minutes. Floors grow from the heart's center block: each
  floor is one 5 m block and must share a face with a built floor at the heart's height, so the first
  one goes at heart +-5 m (placed live on HeadlessOne's plot; rule read from the server's placement log,
  internal research notes).
- **boss** (`boss.mjs`): the first boss in unlock order (Alpha Wolf, Keely, Errol, Rufus, Grayson,
  Goreswine) whose level is at most gear + 3, at full health, blood 80+ and with enough night left. The
  fight uses the normal fight skill: adds near the boss first, then the boss inside its leash, retreat
  at 40% health; a downed boss is fed on. Source-checked and unit-tested; no boss fight has run live.
- **Day and night**: trips start only if they end before sunrise; before dawn the bot heads for cover;
  by day it works within 25 m of a known shaded spot and waits there until sun exposure resets.

## Combat (`skills.mjs` `fight`)

- **Weapons**: combo timing, reach and knockback come from the weapons' prefab data
  (`data/weapon-combos.json`); with `chooseWeapon` the bot picks the carried weapon with a bonus
  against the target (reaper vs undead, spear vs beasts). The crossbow keeps 7-11 m, leads its shots
  through the 1 s rooted draw and dashes or strafes away when a chaser closes in.
- **Reactions**: `bot_threats` reports each nearby unit's cast; a melee wind-up aimed at the bot gets
  a sideways Veil of Shadow dash, or Blood Rite while the dash cools down. Spells are cast at 3-18 m.
- **Healing**: below 40% health a carried healing salve is drunk (at most every 20 s), before fleeing.
- **Feeding as a finisher**: a wounded unit (30% health, bears 25%) that the server marks `feedable`
  (it carries `BloodConsumeSource`; never a V Blood) is grabbed with the feed. The bot hangs on for the
  blood when its pool is below 90, when it carries no blood type yet, or when the victim's blood is a
  type its persona wants at clearly better quality, and nothing else is attacking it; otherwise it
  breaks the feed with an attack, which kills the unit at once without blood.
- Every fight is appended to `%TEMP%\progenitor-bots\combat.jsonl` (weapon, time, health lost,
  dodges, shots and hits, feed or finisher, potions).

Crossbow leading, feeding on living units and salves are source-checked and unit-tested only; the
feedable flag was read live on graveyard undead (all `false`) but not yet on a living unit.

## Brain priorities (`decide`)

The first match wins. Thresholds are constants at the top of `brain.mjs`.

| # | Condition | Action |
|---|---|---|
| 1 | dead, or health 0 (downed) | `recover` (remember where it fell, respawn, leave the crypt) |
| 2 | inside the starting crypt | `leaveCrypt` |
| 3 | burning, or in sun with charge > 0.5 while sun damage is active | `seekShade` |
| 4 | blood < 30 and food carried (rats, hearts; the smallest that covers the deficit) | `feed` (eat) |
| 5 | a V Blood the gear cannot take, or a unit above gear + 5, nearby | `flee` |
| 6 | a hostile within 3.5 m it can take, health above 15% | `hunt` it (fight back) |
| 7 | the bag it dropped at death is within 400 m and 15 min, health above 70% | `retrieve` (take-all from the death container) |
| 8 | a trip home is due and health above 70% | `goal` home |
| 9 | any other hostile within 3.5 m | `hunt` it (fight back) |
| 10 | health < 35% and a hostile within 20 m | `flee` |
| 11 | health < 60% (until 90%), blood >= 30, nothing within 15 m | `rest` |
| 12 | a weak, isolated, fair-level hostile close by, health > 70% | `hunt` it (clear the guard) |
| 13 | blood < 10 | `feed` first (in the graveyard only with a critter close by) |
| 14 | blood < 30 | `feed`: a wounded living unit first (to ~100 blood), else a safe critter |
| 15 | a safe critter within 15 m and blood < 60 or fewer than 2 rats carried | `stock` (catch it, eat if topping up) |
| 16 | a loot drop nearby | `pickup` |
| 17 | the persona's goals (home, journal, heart, castle, gear, boss) | `goal` |
| 18 | daylight or dawn close | shelter by shade |
| 19 | otherwise | the persona's stock rotation (`gather`, `hunt`, `train`) |

`buildHeart` returns early without counting an attempt when the blueprint is still locked (it is
unlocked by claiming `Journal_Gathering`), when the bag cannot pay 30 blood essence + 240 stone, or
when the trip yields to the brain (a hostile within 3.5 m, sun, blood < 5). Six such interrupts
count as one attempt. The heart spot is a pivot in the nearest free plot whose 3 x 3-cell footprint
has no colliding structure (`TileCollisionTag`, not vegetation), fewest trees first; the bot tries
up to three such pivots there. The scan is cached for the session.

After a failure, that action enters a cooldown, so the next tick falls through to a lower priority. The
bot ignores an entity after 2 failures and skips a journal subtask after 3; failure counts are
forgotten after 60 s and skips after 10 minutes. A missing resource name gets its own 60 s cooldown.

**Shade heuristic.** No safe query answers whether a point is shaded
(internal research notes). The bot therefore relies on the server's `inSun`
reading for its own position:

1. Return to the last spot that read shaded during the damage window.
2. Try the four sides of the nearest trees or structures.
3. Otherwise, wait by cover.

## Ops

| Op | Side | Used for | Status |
|---|---|---|---|
| `bot` (move/walk/aim/hold/tap/stop/status) | client | hands | verified live 2026-09-24 on the earlier driver: walked at about 4.5 m/s by server position; aim reached the server. Since 2026-09-26 the op drives through a Core `ctx.Input` lease ([docs/input.md](../../docs/input.md)); that driver is compile-verified only, and `stop` now also ends the lease |
| `bot` hover/unhover | client | feed, pickup target | verified live 2026-09-24 on the earlier driver: hover + Interact picked up a ground drop (+10 stone). The lease-based driver is compile-verified only; the entity must be a client-world id with a `NetworkId` |
| `bot_look` | client | eyes | verified live 2026-09-24: urn found and broken |
| `bot_cast` cast/status | server | attack, feed (castStartedCounter) | verified live 2026-09-24: two basic attacks broke a crypt urn |
| `bot_cast action=vitals` (blood, sun) | server | brain snapshot | verified live 2026-09-24: blood, health, downed, `inSun`/charge/intensity read |
| `bot_inventory` | server | gains, craft/equip checks | verified live 2026-09-24: +10 `Item_Ingredient_Stone` after a pickup |
| `bot_path` | server | legs | verified live 2026-09-24, including the finding that levels joined only by drops are not linked |
| `goTo`/`explore` (script) | client+server | legs | verified live 2026-09-24 on the crypt-gate plateau |
| `leaveCrypt` route (script) | client+server | crypt exit | verified live 2026-09-24 for all three bots: every player has a crypt copy, so the route is relative to its `TM_ExitGate_01`; each gate lands on its own plateau |
| `bot_daytime` | server | sun window | verified live 2026-09-24: cycle 1080 s, sunrise 405 s, day 360 s |
| `respawn`, `downed` | server | recover | verified live 2026-09-24: give up, respawn (125 HP, ~10 blood), leave the crypt again |
| `query` `{all:["ItemPickup"], near}` | client | drop finder | verified live 2026-09-24: broken urns drop `Resource_Drop_*` ItemPickup entities; walking over them does nothing, hover + Interact picks up |
| `bot_event` claim/craft | client | journal, craft | verified live 2026-09-24: claiming "Collecting the Remains" unlocked the Bone Sword recipe; the craft event then made one |
| `bot_event` equip | client | (unused) | the client-sent equip was ignored live; `equipBest` uses the server `equip` op, which equipped the sword (slot 0 group became the sword attack) |
| `bot_event` `{kind:"drop",slot:N}` | client | drop a whole main-bag stack through the normal inventory event | verified live 2026-10-10: headless donor bag emptied, matching `ItemPickup` appeared, full client picked it up through hover + Interact and retained authored bonuses. `N` is the main-bag index from server `bot_inventory`; `amount` and alternate inventories are refused. Pickup uses the recipient's client-world entity. |
| `bot_event` build | client | castle heart | verified live 2026-09-24: placed a heart on a free plot's pivot (cost taken from the bag). Refused silently without the blueprint (unlocked by claiming `Journal_Gathering`), outside a territory, and on a pivot among ruins; trees and plants inside the footprint did not block two placements |
| `bot_use` | server | eat carried food | verified live 2026-09-24: eating `Item_Consumable_Eat_Rat` took blood 7.6 -> 27.4 and consumed it |
| `bot_cost`, `bot_territories` | server | heart affordability, plot choice | verified live 2026-09-24: heart = 30 blood essence + 240 stone; territory 0 (region `None`) is skipped |
| `bot_quests` | server | journal | verified live 2026-09-24: 35 achievements; current: Gathering (300 wood/stone, 50 blood essence), StoneBreaker, ArmorOfBones, Shelter |
| `bot_threats` | server | combat reactions, feed choice | verified live 2026-09-24/25: wind-ups (withered skeleton 0.7 s, rotting ghoul 1.5 s), aggro on the bot, levels; `feedable`/`vblood` and blood type/quality read live on graveyard undead (not feedable, no blood) |
| `bot_cast action=vitals` bloodType/bloodQuality | server | feed choice | verified live 2026-09-25: rat-fed bots read `BloodType_None`, quality 0 |
| `bot_event` moveall (item filter optional) | client | heart fuel, brazier bones, death-container take-all | fuel and bones verified live 2026-09-24; take-all without a filter source-checked only |
| `bot_event` spell | client | put learned spells on the bar | verified live 2026-09-24: Blood Rite placed on slot 6 |
| `bot_recipes`, `bot_blueprints`, `bot_canplace`, `bot_prefabs` | server | gear, castle plans | verified live 2026-09-24: learned recipes, unlocked blueprints and costs |

`brain.mjs` has run live against three headless bots (2026-09-24). In `progression.mjs`, claim, craft, equip and `buildHeart` have run live. `buildHeart` chose a plot-17 pivot (no structures, five colliding plants and trees in the footprint), traveled about 75 m with two stuck-leg sidesteps and placed the heart (2026-09-24). The bot was teleported near the plots and given the materials first (test shortcuts); the walk from the graveyard and full-brain placement have not completed live: bots die to skeletons or daylight on the way, and death drops the materials. Only `decide`, the journal and
subtask planners, heart-pivot snapping and the inventory diffs have unit tests. The inferred parts
are marked in the code:

- loot needs hover plus Interact;
- the heart's placement range and consume type;
- `bot_quests` state strings;
- item-to-source guesses.
