# Progenitor Shared Resources Test (test plugin)

A **test plugin** for the [material sources](../../docs/modules.md#material-sources) live checks
(`ctx.MaterialSource`). It is not a sample to ship and not a sharing policy: remove it from every
reload folder after a test. Server only, administrator only, hot-loadable; public API only
(`ctx.MaterialSource`, `ctx.Inventory`, `ctx.Castles`, `ctx.Commands`, `ctx.Events`, `ctx.WorldState`).

It does two things:

- **A fixed provider.** For one administrator on one plot (`scope`), every configured ingredient is
  paid from bound chests in a configured order with a configured pool reserve. Unconfigured items are
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
| `enable` / `disable` | Register or unregister the provider; `enable` refuses unless Build and Craft are wired |
| `baseline <build\|craft>` / `begin <build\|craft>` | Arm a capture expecting zero debit (provider off) or the predicted debits (provider on) |
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
3. `.srtest enable`, `.srtest status`: `wired=Build,Craft faults=0 shortfalls=0`.

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

**Clean up.** `.srtest clear`; remove this plugin from the server's reload folder and check its
unload line; set `NativeSeams = false` on both and restart them if the rig is handed back; restore the
moved stock; remove the test administrator flag; disconnect the client to the title screen.

## Verification limits

The oracle checks exact final deltas, the reserve kept once per pool, no debit from watched
inventories that are not sources, and zero debit when any ingredient is short. It cannot observe
transient states between snapshots, prove an output exists, read the client's displayed count, or
prove simultaneous requests are serialized; use the game's own UI, the `materials` bridge op and the
server log for those.
