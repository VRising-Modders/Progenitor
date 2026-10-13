# Install, uninstall and rejoin test matrix

The live test set for installing, updating, uninstalling and rejoining across mod sides, load types, places and server types (requested 2026-10-07). Each case records the build it ran on and what was observed. "Not run" means not run; a case is never marked passed from source or unit tests alone.

## Factors

| Factor | Values |
|---|---|
| Mod side | client, both, server |
| Load type | hot, restart required |
| Where the action starts | title screen, in game, join screens |
| Server type | private game, local dedicated (this PC's configured server folder), remote dedicated |
| Restart | manual, automatic |
| Rejoin | manual, automatic |
| Operation | install, update, uninstall, turn off/on, cancel a pending install or uninstall |
| Player role | not an admin, on the admin list, adminauth |
| Server rule for the mod | required + warn/pause, required + disconnect, recommended, not listed; install window seconds 0 or more |
| Address used (local dedicated) | 127.0.0.1, public address |
| Source | Thunderstore, local test source (`[Install] LocalIndexPath`), another tool, dropped into `progenitor-reload` |

Combinations that do not exist are left out: the join screens install client mods only; a private game is always on this PC; restart and rejoin types apply to restart-required mods only; Progenitor stops a dedicated server but something else must start it again.

## Test packages

Built with `tools/make-dummy-package.ps1 -Out <local test folder> -Append` into the local test source (the only hot-loadable packages available):

| Package | Plugin id | Side | Load |
|---|---|---|---|
| Progenitor-HotDemo | `progenitor.hotdemo` | both | hot |
| Progenitor-ReloadExample | `progenitor.reloadexample` | server | hot |
| Progenitor-DyeAll | `progenitor.dyeall` | client | hot |
| Progenitor-NecroArmyExample (`-NotHotLoadable`) | `progenitor.necroarmy` | both | restart |
| Progenitor-TimedBuffExample (`-NotHotLoadable`) | `progenitor.timedbuffexample` | server | restart |

Dye all became part of Core on 2026-10-08 and its sample was removed, so `Progenitor-DyeAll` can no longer be built. New runs use `Progenitor-FilmExample` (`progenitor.film`, client, hot) as the client package. An old `Progenitor-DyeAll` package that is still installed makes Core's Dye all stand down; see [modules.md](modules.md#dye-all).

"Remote dedicated" uses the second test server joined by address, with no local-folder link. Remove every test package from the client and the servers after the run.

## Cases

Watch in every case for detours: a player sent to the join screen, a rejoin or a restart when the fix could happen where they are.

| # | Side | Load | Start | Server | What is checked | Result |
|---|---|---|---|---|---|---|
| A1 | both | hot | title | none | Install and uninstall from the Install tab, client only | Passed 2026-10-07 (snapshot 692b6a0ad): install and uninstall, client only, immediate; row refreshed |
| A2 | both | hot | in game | local, 127.0.0.1 | Admin installs and uninstalls both sides; the boxes name each side | Passed (local route, 127.0.0.1): server install box names server, folder and timing; client and server halves |
| A3 | both | hot | in game | local, public address | As A2; the route still recognizes this PC's server | Not run: the router does not loop back the public address on this network; LAN-address join used instead (see A7) |
| A4 | both | hot | in game | remote | Admin; the server installs from its own local test source | Passed (7c73dc281/54b740627): headless server installs and uninstalls from its own local index, hot; boxes name server, address, source, "Now (unloads)" |
| A5 | both | hot | in game | any | Not an admin: Simple shows the client half only, with the server note | Passed: not on the admin list, Simple scope client-only, server-only packages hidden, server-half note |
| A6 | both | hot | join | local | Required + warn, window > 0: install in the join window, the join finishes, no rejoin | Passed after fix (b33e9f866): window held for a local-test claim, install in the join window, report re-sent, join completes with no rejoin |
| A7 | both | hot | join | local | Required, window 0: refused; after the install the automatic rejoin uses 127.0.0.1 | Passed (LAN address): refused with window 0, install, Join now rejoins through 127.0.0.1 with the original address as fallback |
| A8 | both | hot | in game | local | Already in the world (warned): the fix is sent in place, no reconnect | Passed: in the world after a warning, install from Install Mods; the report is re-sent and the warning clears, no reconnect |
| A9 | server | hot | in game | local and remote | Admin removes from the Mods page and the Install tab; removed now | Passed (local route) after F10 fix; remote variant in A4 |
| A10 | client | hot | title and in game | none | Install, uninstall, turn off and on | Passed: Film Example (client-only) install, switch off and on, uninstall, all immediate |
| A11 | both | hot | in game | private | Private game: local-server wording and both halves | Passed (54b740627/9e9e9b167): route "your private game", install and uninstall hot into VRising_Server, header "Your private game", staged HotDemo reads installed |
| R1 | both | restart | title | none | Marked; manual restart; installed and loaded | Passed: title-screen restart applied a pending removal |
| R2 | both | restart | in game | local | Automatic client restart, then automatic rejoin | Passed after F19 fix: restart and automatic rejoin through 127.0.0.1, no password prompt |
| R3 | both | restart | in game | local | Manual restart, then manual rejoin | Passed (title manual restart); manual rejoin by Continue/Direct Connect works |
| R4 | both | restart | join | remote | Automatic restart and rejoin record; the join completes | Passed in substance on the remote headless server: Stop server to apply, Rejoin when it's back, pending server install promoted |
| R5 | server | restart | in game | local | Marked on the server; stop; start; loaded | Passed: Stop server to apply / Save, stop and start again (3824de9e9) on the isolated test server, same arguments, pending install and removal applied |
| R6 | server | restart | in game | private | The private game restarts with the client | Not run |
| R7 | client | restart | in game | none | Uninstall marked; Cancel uninstall reverts it | Passed: client uninstall marked; Cancel uninstall reverts; restart bar for pending removals (F28) |
| E1 | any | any | in game | any | Uninstalling a required mod: the box warns with the server's rule; the notice opens Install Mods | Passed: Required by line with the server's rule; notice opens Install Mods |
| E2 | any | any | any | any | A ledger row whose files are gone reads Not installed | Passed: a ledger row whose file is gone reads not installed |
| E3 | any | any | any | any | A mod installed by another tool: offered only when its files are certain | Passed (readback): mods installed by another tool offered for removal at the next start; not exercised on the user's own mods |
| E4 | any | any | any | server | A reload DLL that holds two plugins is refused, naming both | Not run live (unit tests) |
| E5 | any | any | any | any | A dependency of another mod: refused unless Uninstall anyway | Not run live (no test package with dependents; unit tests) |
| E6 | any | hot | any | any | Core swap after an install: the uninstall is still immediate | Passed: after Core swaps, an uninstall of a hot-loaded copy is immediate |
| E7 | any | any | in game | any | Server unreachable mid-request: a visible refusal, nothing half done | Not run live: the install finished before the server could be cut off (unit tests cover no-answer) |
| E8 | any | any | join | any | `[Install] LocalIndexPath` empty: no local-source offers anywhere | Passed (Install tab): no local rows with LocalIndexPath empty |
| E9 | any | any | in game | any | Turned off, then uninstalled: the notice says not installed | Not run live: the 15 s disconnect window is too short through the bridge (unit tests) |
| E10 | any | any | title | any | The main-menu notice after a disconnect opens Install Mods | Passed: after a kick, the notice button opens Mods on the mod's page |

## Findings

The run (2026-10-07 to 2026-10-08, client plus isolated, headless and private-game servers) found 37 problems, F1 to F37. F1 to F35 were fixed in PR #20. F36 (a false "Removes at the next restart" note when two local listings name the same plugin) and F37 (the rejoin page named "Stop server to apply" after Save, stop and start again, and asked before rejoining) were fixed afterwards; both are built and game-free tested, not live-verified (see [mods-menu.md](mods-menu.md) and [join-flow.md](join-flow.md)). Remaining open: A3 (public address), R6, E4, E5, E7, E9 live.

F41 (found 2026-10-08): with Disconnect them, a server notice drew over an open install confirmation and hid it, so the countdown ran out unseen. Fixed in two parts, both source-checked, built, game-free tested and **live-verified 2026-10-08** (the re-arm case 7a not run): server notices now wait while a confirmation is open and show when it closes; and while a countdown notice waits, the server also says the countdown as a private System chat line (at the start, then at 30 and 10 seconds left; never while the notice is on screen; it stops once the mod is fixed), with the fix where the player is, for example `Reinstall Hot Demo from Install Mods in the Mods menu within 60 s or you'll be disconnected.` See [client-mod-policy.md](client-mod-policy.md#the-countdown-in-chat-while-its-notice-waits-f41), which also has the live acceptance procedure.
