# Progenitor Shared Resources Test (test plugin)

A **test plugin** for the [material sources](../../docs/modules.md#material-sources) live checks
(`ctx.MaterialSource`). It is not a sample to ship and not a sharing policy: remove it from every
reload folder after a test. Server only, administrator only, hot-loadable; public API only
(`ctx.MaterialSource`, `ctx.Inventory`, `ctx.Castles`, `ctx.Commands`, `ctx.Events`, `ctx.WorldState`).

It does two things:

- **A fixed provider.** For one administrator on one plot (`scope`), every configured ingredient of a
  build, a craft, a craft from the Character window, a repair or a castle heart upgrade is paid from
  bound chests in a configured order with a configured pool reserve. For a Character window craft it
  also answers when the administrator stands on no plot, which Progenitor must never ask (case 27). The structure the operation runs at (`MaterialContext.Station`: the
  craft or repair station, the castle heart being upgraded) is dropped from the order. Unconfigured items are
  governed with no remote sources; `vanilla <item>` leaves an item to the game (`Sources` returns
  `null`); `fault …` makes the provider throw.
- **An inventory oracle.** `begin` records every bound inventory's count of every configured
  ingredient and predicts the debits; `verify` compares after one game operation. It never moves
  items, never stages stock and never pays for anything itself. A matching report alone is not an
  end-to-end pass: the placed piece or the queued craft is checked separately.

| File | What |
|---|---|
| [Plugin.cs](Plugin.cs) | The plugin, the fixture state and the `.srtest` commands |
| [MaterialProvider.cs](MaterialProvider.cs) | The fixed `IMaterialSourceProvider`, fault and vanilla switches |
| [Fixture.cs](Fixture.cs) | The game-free oracle; tested in `tests/Progenitor.Testing.Tests/Samples/Progenitor.SharedResourcesTest` |

## Build

From the repository root, with Core built (`dotnet build src/Progenitor.Core`):

```
dotnet build samples/Progenitor.SharedResourcesTest -p:BuildProjectReferences=false
dotnet test tests/Progenitor.Testing.Tests --filter "FullyQualifiedName~SharedResourcesTest"
```

Deploy it to the **server's** reload folder with the [hot reload workflow](../../docs/hot-reload.md); the
client does not run it. Never deploy it against a Core older than the one it was built with.

## Commands

All `.srtest …`, administrator only; the first administrator to use it owns the fixture until `clear`.
Entity indices are session values: never reuse them after a reload.

| Command | Does |
|---|---|
| `status` | Provider status line (`ctx.MaterialSource.Status`), enabled, fault, territory, epoch, sources, capture |
| `plots <item>` | Owned or clan plots and their ordinary-chest stock of one item |
| `containers <territory> <item>` | Containers on one plot with entity, station flag, count and stable id |
| `diagnose <territory>` | Every inventory on the plot with its rows |
| `scope <territory>` | The plot the provider governs (the administrator must stand on it) |
| `source <alias> <index> <version>` | Bind an ordinary chest as a material source |
| `watch <alias> <index> <version>` / `bag [alias]` | Watch a station or the bag without making it a source |
| `rank <item> <alias> <rank>` | Deposit rank for one ingredient: 0 = best home; the highest rank is drained first |
| `cost <item> <amount> [reserve]` | The whole operation's cost of one ingredient (as the game shows it) and its pool reserve |
| `vanilla <item>` | Toggle: the provider returns `null` for this item (the game's own rules) |
| `fault none\|candidates\|sources\|item <hash>` | Make the provider throw in `Candidates`, in every `Sources`, or for one item |
| `enable` / `disable` | Register or unregister the provider; `enable` refuses unless Build, Craft, Repair, Upgrade and CharacterCraft are wired |
| `baseline <build\|craft\|repair\|upgrade\|charcraft>` / `begin <…>` | Arm a capture expecting zero debit (provider off) or the predicted debits (provider on) |
| `verify` / `end` / `clear` | Compare the capture; end it; clear everything and unregister |

## Live re-verification procedure

For the hardened build (operation transaction and shortfall rule, scope pairing, client selection rules,
view version 2, per-item fallback). **Run on the isolated test rig on 2026-10-14:** every case passed
except case 4 (other screens), which was not run; case 7's shortage was produced with the pool reserve. Isolated test server and test client only, a copy of a save, never the live server.
Restore any stock you move, and remove the plugin afterward.

**Deploy.** Build the solution from one reviewed commit. Deploy Bootstrap, Core and Dev to both the
test server and the test client and this plugin to the server only. Set `[Materials] NativeSeams = true`
in `BepInEx/config/progenitor.cfg` on both, then **restart both processes** (Bootstrap is
process-lifetime; a Core swap does not arm the seams). Expect in each log:
`materials: native seams enabled count=armed pay=armed` (client: no `pay=`) and
`materials: Core seam handlers bound (server: availability + payment)` / `(client: availability)`.
`materials status` (bridge, each side) must show `hooksInstalled: true`.

**Set up.** Join with the test client, make the character an administrator for the test, and pick a
resource-poor plot P (a workbench on it) and a rich plot R with ordinary chests A and B holding the
ingredients of one workbench recipe (two ingredients X and Y) and of one build-menu piece. Empty X and Y
from the bag and the workbench (record what you moved). Then, standing on P:

1. `.srtest scope <P>`, `.srtest bag`, `.srtest watch bench <index> <version>`,
   `.srtest source A …`, `.srtest source B …`.
2. `.srtest rank X A 0`, `.srtest rank X B 10`, `.srtest rank Y A 10`, `.srtest rank Y B 0`
   (opposite orders), `.srtest cost X <n> 0`, `.srtest cost Y <m> 0` (the amounts the game shows).
3. `.srtest enable`, `.srtest status`: `wired=Build,Craft,Repair,Upgrade,CharacterCraft faults=0 shortfalls=0`.

**Checks.** After each, `.srtest verify` and `.srtest end`; read `materials status` on both sides.

**Open the station window first.** The server silently drops a craft event unless the client has
actually opened the station's window: no `materials: check` line, no queue row, no debit. An
interaction can report its target before the window is open, so confirm the window (a capture, or the
game UI) before reading a zero-debit refusal as evidence.

**Counters and logs.** `Status` counters reset on every provider registration (`enable`). The provider
fault log is capped at 20 lines per Core load, so after case 9's `Candidates` faults a per-item fault
may log nothing while `faults` keeps growing.

| # | Case | Do | Expect |
|---|---|---|---|
| 1 | Baseline | `disable`, `baseline craft`, try the recipe at the bench | The game refuses; zero debit; `enable` again |
| 2 | Craft | `begin craft`, open the bench, craft once | Client `views.Craft.station` is the bench's network id and its totals show X and Y from A and B; one `materials: check … tx=<t>` line (more than one per craft would show the game counts more than once per operation), then one `materials: paid Craft … tx=<t>` line per ingredient with the same `tx`, debits in the reversed per-item order; then one `Shared Resources Test: MaterialPaid tx=<t> …` line per ingredient; `verify` MATCH; the craft is queued |
| 3 | Build | `begin build`, open the build menu (`build_search open`, `select`), place the piece | Client `views.Build.heart` is P's heart; `paid Build` lines; `verify` MATCH; the piece stands |
| 4 | Other screens | With a Build view held, open a chest and the research desk | Their counts are the game's own (no pool totals) |
| 5 | Other station | Open a second workstation on P | Its costs are the game's own until its own Craft view arrives (its station id) |
| 6 | Per-item fallback | `vanilla Y`, put some Y in a shared chest on P, open the bench | X shows the pool, Y shows exactly the game's own shared amount; craft: Y is paid by the game (no `paid` line for Y, the shared chest on P debited), X from the pool; `vanilla Y` again to undo |
| 7 | Shortage | Leave one unit fewer of X than needed across A and B, `begin craft` | X shows short; the game refuses the craft; zero debit; `shortfalls` unchanged |
| 8 | Reserve boundary | `cost X <n> <r>` so that exactly n remain above r, then one more unit of reserve | First: paid; second: refused by the game's check, zero debit |
| 9 | Provider fault at the check | `fault candidates`, open the bench | Pool totals gone (0); the game refuses; zero debit; `faults` grows; `materials: provider.Candidates threw` in the server log. Repeat with `fault item <X>`: only X shows 0. `fault none` |
| 10 | Container changed between check and pay | Keep all of X in A (none in B) and bind a third chest C on R that is **not** a source (`.srtest watch C …`). Server bridge `materials` `{"action":"inject","from":"<A id>","to":"<C id>","item":<X>}` (ids from `containers`), then craft once | `fault injection moved …`, then Y is paid from the pool as usual and X's line takes nothing (A is empty, C is not a source): one `materials: shortfall at payment … item=<X> owed=<n> …` line, `shortfalls` grows, a `MaterialPaid` line for Y only; A, B, C and the bag together hold what they held before minus Y's cost. **The craft still starts** (the game does not re-check after its own check, and its own shared storage behaves the same; see the module page). Let it finish rather than cancel it (a cancel refunds the requested amount, which was not all paid), and restore the stock afterward |
| 11 | Disable | `disable` | Both client views become `null` within about 1 s; counts return to the game's own; the craft from case 1 is refused again |
| 12 | Resend | With the provider on, reload the client's Core (`core_reload`), wait up to 10 s | `views` returns without any change on the server |
| 13 | Unload | Hot-unload this plugin | `materials: provider unregistered owner=progenitor.sharedresourcestest`; views cleared |
| 14 | Server Core swap | Re-enable, then swap the server's Core (`core_reload` on the server) and do not re-enable | Within about 1 s of the new Core's first refresh, both client views are `null` (a cleared view per operation) |

### Repair and Upgrade cases

**Run on the isolated test rig on 2026-10-14:** cases 15, 16, 17, 18, 19, 20 and 22 (22 partially: the
chest half) passed; case 21 (claim) was not run, and the client's own path of an equipped-item repair
was not exercised (every repair was sent from the server bridge).
Run them with the same deployment (a Core with `wired=…,Repair,Upgrade`) and the same scope and sources.
Each operation is its own capture: `.srtest end`, then `cost` the new ingredients (disable first, then
`enable` again) before each `begin`.

Repair and Upgrade scopes honor a seam call only while their own system executes; a log line
`materials: a seam call inside the … scope came from another system` during these cases means the
executing-system check refused a call and must be reported.

**Sending the events focus-free.** Each repair and upgrade can be sent without the game window, from
the server bridge, as the event the player's client would send (`materials` `send`, see
[bridge-ops-client.md](../../docs/bridge-ops-client.md#materials)); `<name>` is the test character:

- worn item: `node tools/mcp/call.mjs server materials '{"action":"send","event":"repair_equipped","user":"<name>","equipment":"chest"}'`
- item at a workstation: open the bench's window on the client first (the server checks the
  interaction), then `{"action":"send","event":"repair_item","user":"<name>","station":"target","slot":<bag index>}`
  (`inventory` defaults to the bag; pass the bench's `index:version` as `inventory` for an item in the bench)
- heart upgrade: open the heart's window on the client first, then
  `{"action":"send","event":"heart_upgrade","user":"<name>"}` (`heart` defaults to the open window's heart)

The server bridge's `bot_repair` `repair` action sends the same worn-item event. A sent event is
silent when the game refuses it: read the result (durability, the heart level, the `materials:` lines).

**Bind a source for every ingredient of the cost.** The provider governs every item it is asked about,
and the pool replaces the plot's shared storage: an ingredient with no bound source counts as zero at the
game's check even when the plot's own chests hold it, and the game refuses the whole operation. Read the
item's full repair cost (the `ItemRepairBuffer` of its repair recipe) before `cost` and `rank`.
`inspect` at depth 1 truncates buffers: check `elementsTruncated` in the reply and read deeper when it is
true (on 2026-10-14 the first read of a sword's repair buffer showed two of its three ingredients).

**Repair setup.** Damage one piece of gear whose repair costs an ingredient X of the plot (wear it in a
fight, or pick a piece already below full durability), read its repair cost from the item tooltip, and
empty X from the bag and the workstation. `.srtest cost X <the tooltip's amount> 0`. The tooltip shows the
game's own count, not the pool (no Repair view is sent; see the module page), so it may read short.

**Upgrade setup.** On P, a castle heart one level below an upgrade whose cost you can stage in A and B:
read the costs in the heart window, empty them from the bag, and `cost` each one. The heart window's
costs show the pool once the Upgrade view arrives (up to about 1 s after opening the window).

| # | Case | Do | Expect |
|---|---|---|---|
| 15 | Repair baseline | `disable`, `baseline repair`, repair the piece from the character menu | The game charges nothing (it shows missing materials); zero debit; `enable` again |
| 16 | Repair equipped | `begin repair`, repair the worn piece from the character menu (or `send` `repair_equipped`) | One `materials: check Repair … tx=<t>`, then one `materials: paid Repair … tx=<t>` per ingredient in the per-item order; one `Shared Resources Test: MaterialPaid tx=<t> Repair …` per line; `verify` MATCH; the piece is at full durability. Also the unverified client side of this path: no `check Repair` line at all means the client refused the equipped repair on its own count (report it; it would need a client view or a hook). With a station's window still open, `check` names that station as `station=`: expected, because the game's own pass for a worn-item repair also reaches the open interaction inventory, so Progenitor names it and leaves it out of the pool (live run 2026-10-14) |
| 17 | Repair at a station | Damage another piece and keep it in the bag, bind a source for every ingredient of its repair cost, open the bench's window, `begin repair`, arm `materials` `{"action":"trace"}` on the server (optional), then `send` `repair_item` with `station` `target` and the piece's bag `slot` | One `check Repair … station=<bench>`, one `paid Repair` line per ingredient with one `tx`, one `MaterialPaid` per line, `verify` MATCH, the piece at full durability. **PASSED 2026-10-14** (Iron Bar, Plank and Gem Dust from three chests). An earlier attempt was refused at the game's check because one ingredient (Gem Dust) had no bound source |
| 18 | Repair shortfall | Leave one unit fewer of X across A and B, `begin repair`, repair | The game refuses at its check (missing materials); zero debit; `shortfalls` unchanged |
| 19 | Upgrade short | Leave one unit too few of one cost across A and B, `begin upgrade`, open the heart's window, `send` `heart_upgrade` (or press upgrade) | The view shows the short amount; the client's own check refuses (the button does nothing) or, for a `send`, the server's check does; zero debit; then restore the unit and `end` |
| 20 | Upgrade | `begin upgrade`, open P's heart window, read the costs, `send` `heart_upgrade` (or press upgrade) | Client bridge `materials`: `views.Upgrade.station` is P's heart network id while the window is open, and its costs show the pool; one `materials: check Upgrade … station=<heart id> tx=<t>`, one `paid Upgrade` line per cost line, `MaterialPaid` per line; `verify` MATCH; the heart's level rose by one |
| 21 | Claim, raid and destroy stay vanilla | Only on a throwaway copy of a save with a second test character's decaying heart: try a claim with its cost staged only in A | The game's own rules: A is not debited (no `materials: check Upgrade` line); the heart window's claim cost shows the game's own count; with that heart's window open the client's `views.Upgrade` stays `null` (Upgrade views only for hearts the player or the clan owns) |
| 22 | Other screens | With an Upgrade view held, open a chest and a workstation | Their counts are the game's own (the chest) or the Craft view (the workstation) |

### Character crafting cases

**Run on the isolated test rig on 2026-10-14** (build b6f997d61, by another session with its own provider
rather than this plugin): the equivalents of cases 24, 25 and 27 passed (pool totals in the crafting list
on a plot, one `check` and one `paid` line for a bridge-sent craft, nothing off a plot). Cases 23, 26 and
28 were not run. Run
these with the same deployment (a Core with `wired=…,CharacterCraft`), the same scope P and sources A and
B on R. Every step below is a bridge call (no mouse, no window focus); `<name>` is the test character.

**What the code relies on, to check here.** On the server, `StartCharacterCraftingSystem` counts once
and pays each ingredient at the castle heart of the plot the character stands on; Progenitor governs
only calls the game made with a heart (on a plot). On the client, the Character window's crafting menu
computes its recipe costs and a per-recipe "requirements met" flag in its own system update, and the
craft input sends the request only when the flag is set; Progenitor shows the CharacterCraft view there
only while the Character window is open.

**Setup.** Pick a recipe the Character window crafts for the test character (one of its own unlocked
recipes, two ingredients X and Y; read its PrefabGUID hash from the server user's
`WorkstationRecipesBuffer` with `inspect`, and the costs from the recipe's `RecipeRequirementBuffer`;
check `elementsTruncated`). Empty X and Y from the bag. `.srtest end`, `.srtest disable`, `cost X <n> 0`,
`cost Y <m> 0`, `rank` both as in step 2, `.srtest enable`. Stand on P (`teleport` to a point inside P).
Keep no other window open (an open chest's inventory is the game's own and is left out of the pool).

| # | Case | Do | Expect |
|---|---|---|---|
| 23 | Baseline | `disable`, `baseline charcraft`, server `materials` `{"action":"send","event":"character_craft","user":"<name>","recipe":<hash>}` | The game refuses (missing materials): no queued craft; zero debit; no `materials: check` line; `enable` again |
| 24 | View | Standing on P with the provider on, open the Character window with the game's own key through the client bridge (`key` `{"action":"tap","keys":"Tab","background":true}`, or the key the player bound), wait 2 s, read client `materials` `{"action":"status"}`, then capture the window (focus-free capture) on the crafting list | `views.CharacterCraft` is not `null`: `heart` is P's heart network id, `station` 0, `totals` show X and Y from A and B. The capture shows the recipe as craftable with those amounts. Close the window (`key` tap again) |
| 25 | Governed craft | `begin charcraft`, then the same `send` | One `materials: check CharacterCraft user=… territory=<P> tx=<t> quoted=2` (no `station=`), then one `materials: paid CharacterCraft … tx=<t>` line per ingredient in the per-item order, one `Shared Resources Test: MaterialPaid tx=<t> CharacterCraft …` line per ingredient; `verify` MATCH; the craft is queued and finishes into the bag. `materials status` (server): `payments` grew by 2, `shortfalls` unchanged |
| 26 | Short | Leave one unit fewer of X across A and B, open the Character window as in 24, read `views.CharacterCraft`, close it; `begin charcraft`, `send` | The view shows the short amount of X. The game refuses at its check: one `check CharacterCraft` line, no `paid` line, no queued craft, zero debit, `shortfalls` unchanged. Restore the unit and `end` |
| 27 | Off a plot stays vanilla | `teleport` the character to a point on no plot (stock in A and B unchanged, bag still empty of X and Y), open the Character window as in 24 and read `views.CharacterCraft`, close it; then `send` | `views.CharacterCraft` is `null` (the server sends a cleared view off a plot); no `materials: check CharacterCraft` line (the provider answers for no plot, so a check line would mean the plot rule failed); the game refuses (missing materials); A and B unchanged (`.srtest containers <R> <X>`). Put X and Y in the bag and `send` once more: the game pays from the bag alone, still no `check` line. Teleport back to P |

| # | Case | Do | Expect |
|---|---|---|---|
| 28 | The client's own craft click | The client's craft input sends the request only when the recipe's "requirements met" flag is set, and that flag comes from the count the CharacterCraft view replaces. Standing on P with the pool covering the cost and X and Y out of the bag: open the Character window (`key` tap as in 24) on its Inventory tab, `screenshot` (focus-free) to find the recipe's cell, `begin charcraft`, then client `mouse` `{"action":"move","x":…,"y":…,"background":true}` onto the cell and `{"action":"click","x":…,"y":…,"background":true}` (screen pixels, origin bottom left; the game's input reads `mouse`, which `ui_input` does not reach) | The recipe is not grayed in the screenshot; after the click one `check CharacterCraft … tx=<t>` and the `paid` lines as in 25, `verify` MATCH, the craft queued. With the provider disabled (`disable`, `baseline charcraft`), the same click shows the game's own missing-materials message and sends nothing (no `check` line). `mouse` reports `pressReached`; if it is false, the window needs `background:true` honored (see `mouse`), report it rather than taking focus |

**Clean up.** `.srtest clear`; remove this plugin from the server's reload folder and check its
unload line; set `NativeSeams = false` on both and restart them if the rig is handed back; restore the
moved stock; remove the test administrator flag; disconnect the client to the title screen.

## Verification limits

The oracle checks exact final deltas, the reserve kept once per pool, no debit from watched
inventories that are not sources, and zero debit when any ingredient is short. It cannot observe
transient states between snapshots, prove an output exists, read the client's displayed count, or
prove simultaneous requests are serialized; use the game's own UI, the `materials` bridge op and the
server log for those.
