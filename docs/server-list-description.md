# Server list description and Progenitor signature (spec)

**Status: source-checked, built (Debug and Release), game-free tested (`ServerListDescriptionTests`) and live-verified on 2026-10-05 ([live results](#live-results)).** Requested 2026-10-05; built on branch `feat/server-list-description` ([as built](#as-built)). The engine facts below come from read-only research of the game's metadata and native code (internal research notes); the live results confirm the ones listed there. The live recipe is in [bridge-ops-join.md](bridge-ops-join.md#server_desc). **The description is no longer posted to chat on join** ([below](#the-description-in-chat)): source-checked, built and game-free tested on 2026-10-06; not live-verified.

## Live results

On 2026-10-05, the isolated test server (Progenitor Core 2026.10.74.2) and the Steam client were used:

- **Unlisted server checks:**
  - composition with the signature on and off;
  - the cut with "…" at 128 characters;
  - the live `Description` field;
  - the published Steam `desc0` key;
  - the write to `ServerHostSettings.json`.
- **Query:** while briefly listed (ListOnSteam/ListOnEOS on, password set), the server answered A2S_RULES with `desc0` = the admin text and the signature on separate lines.
- **Browser:** the vanilla **Join Game** browser showed the server ("Progenitor Forms Test", search "Progenitor") with that text in the right-hand panel and the line break kept. The panel justifies text, so the signature line shows wide word spacing.
- **Live config edit:** editing `Description` in `progenitor.serverlist.cfg` while the server ran republished it at once ("Published … Steam updated"), with no restart.
- **Restart:** after a restart, the start publish sent the signature-only default (45 characters).
- **Filters:** finding the server needed the browser filters to match it. Game Mode PvE hid the PvP test server.

The listing was then removed (ListOnSteam/ListOnEOS off, password removed).

**The description also reached joining players in chat (observed 2026-10-05).** During the CLIENTPOLICY3 live run on the isolated test server, joining with the Steam client showed the server's `Description` as a System chat line: `[SYSTEM] Requires the Progenitor mod (thunderstore.io)`. The source is the game's own join handler; Progenitor now keeps it out ([the description in chat](#the-description-in-chat)).

## The description in chat

**Status: source-checked, built (Debug and Release) and game-free tested (`JoinDescriptionChatTests`) on 2026-10-06; not live-verified.** Decided 2026-10-06: the description line should not appear in chat for anyone. Players without Progenitor get the no-Progenitor notice instead, and players with it do not need the line. The description is still published to the **Join Game** list (Steam and EOS) unchanged.

**What the game does** (from the server metadata and native code). Near the end of `ServerBootstrapSystem.OnUserConnected`, when `ServerHostSettings.Description` is not empty, the game clamps it to fit a 512-byte chat message and sends it to the joining player with `ServerChatUtils.SendSystemMessageToClient`, which the client shows as a `[SYSTEM]` line. It is the method's only read of `Description`, and the game sends it to every joining player, with or without Progenitor.

**What Progenitor does.** For the length of that one call, the live `Description` field is empty, so the game's own check skips the send. The field is put back as the call returns.

| Part | Where | Notes |
|---|---|---|
| Prefix | `JoinDescriptionChatHook` (Core, server only) | A slot-routed Core prefix (`CorePatchBudget`, module `serverlist`) on `ServerBootstrapSystem.OnUserConnected`. It reads no argument, never skips the original, and only writes the managed string field `SettingsManager._ServerHostSettings.Description`, the accessor the apply loop already uses. No by-ref struct, no generic instantiation, no native call. With `DescriptionInChat` off and a non-empty description (any text, as the game's own check), it holds the text and empties the field. Log: `ServerList: a player is joining; the server's description is kept out of their chat (DescriptionInChat off).` |
| Restore | Core's existing `OnUserConnected` postfix (`EventHooks`, `PlayerConnected`), first thing | Puts the held text back. If the field is no longer empty (a new description was set meanwhile), the new one is kept. If the postfix does not run (the original threw, or the events module is skipped), the next frame restores it and logs a warning. The next join also restores a held text before holding it again. |
| Logic | `JoinDescriptionChatGuard` (game-free) | Tested over a fake of the game's join. |
| Setting | `progenitor.serverlist.cfg`, `[ServerList] DescriptionInChat` (bool, default `false`) | `true` restores the game's chat line. Cfg-only: not drawn, not a ConfigSync row, never sent to clients ([settings-organization.md](settings-organization.md#operator-diagnostic-and-developer-settings-cfg-only)). Read on every join, so an edit applies to the next join without a restart. |
| Readback | `server_desc read` → `joinChat {descriptionInChat, armed, pending, held, passed}` | [bridge-ops-join.md](bridge-ops-join.md#server_desc). |

**What is unaffected.** Every other system message (command replies, admin announcements, Progenitor's notices, including the no-Progenitor notice) never reads the description. The listing is published elsewhere: the Steam description from a separate call after Steam sign-in, and EOS from the game's one-second platform tick, which never runs inside `OnUserConnected`. The apply loop never sees the empty field, because it runs on the frame, between joins.

## Progenitor servers in Join Game

**Status: source-checked, built (Debug), game-free tested (`ServerBrowserFilterTests`) and live-verified on 2026-10-08 on the Steam client and the isolated test server ([live checks](#browser-filter-live-checks)), except the wrong-row window (step 7) and an EOS row.** Requested 2026-10-08 (BROWSERFILTER1): a way to show only servers running Progenitor in the game's **Join Game** browser. The live recipe is in [bridge-ops-join.md](bridge-ops-join.md#server_browser). The engine facts come from read-only research of the game's metadata and native code (internal research notes).

### What the browser receives

- **Each row carries the server's tags.** The client builds a `ServerListEntryData` for every server from the list query itself: a Steam row from the game tags of its `gameserveritem_t`, an EOS row from the session's `TAGS` attribute. Both are lowercased and split on commas into `ServerListEntryData.Tags`. The game reads its own columns (PvP, clan size, difficulty, Merciless, scheduled wipes, standard settings) from the same tags.
- **The description is not in a Steam row.** It arrives only when a player selects a server, over a rules query (`desc0`, `desc1`, …). An EOS row carries it, a Steam row does not, so the description cannot filter the list.
- **The server sets the tags.** `ServerBootstrapSystem.SetServerGameTag(tag)` adds a tag (a blank or duplicate one is ignored) and marks the list changed; the game's one-second `UpdatePlatformData` then sends the joined string to Steam (`SetGameTags`) and EOS (`TAGS`). Nothing in the game clears the list after startup.
- **The game's filter pass.** `PlayJoinMenuView.Update` calls `UpdateServerListView` every frame. It asks `ServerListSystem.GetFilteredServerListEntries` for the shown list, which returns its cached list object and rebuilds it from the full set (`_ServerListEntries`) with the game's filters, search and sort every two seconds, on a filter, search or sort change, and after a refresh. The view draws the rows and **Servers: N (M total)** from it: N is the shown list, M the full set. The filter pass is native code with no per-row callback.

### The marker: a server-list tag

A Progenitor server adds the tag **`progenitor`** to its game tags. The client keeps a row only when one of its tags is exactly `progenitor` (case and surrounding spaces ignored; `progenitor2` or `notprogenitor` do not count).

| Channel | Verdict |
|---|---|
| Server-list tag (chosen) | In every row, Steam and EOS, at no extra query. One fixed token the admin does not type. A vanilla client ignores tags it does not know. It uses 11 of Steam's 128 bytes of tags; the server warns in its log when the joined tags would pass that limit. The EOS `TAGS` attribute's own length limit was not checked. |
| Description signature | Not in Steam rows, and admins can edit, cut or imitate it. Not used. |
| A Steam rule (key-value) | Like the description, only fetched for the selected server. Not usable for a list filter. |

Any server can publish any tag, so the marker says the server **claims** to run Progenitor: a listing filter, not a trust check. Joining still runs the handshake.

**Server switch.** `[ServerList] BrowserTag` in `progenitor.serverlist.cfg` (bool, default `true`, cfg-only like `DescriptionInChat`). Off removes the tag at once. The tag goes after the game's own: the server waits until the game's startup tags exist (`pvp` or `pve`) and its first activity-level `dif_*` tag (added up to a minute after start), or 90 seconds after the startup tags, then checks once a minute and puts the tag back if something removed it. **After a Core hot unload the tag stays in the game's list**, so the server keeps listing as Progenitor until it restarts (or the next Core turns `BrowserTag` off). Log: `ServerList: the "progenitor" server-list tag is published (Join Game's Progenitor servers filter finds this server).` On the server, `server_desc read` reports `browserTag {tag, enabled, status, fitsSteam, tags}`.

### The client filter

| Part | Where | Notes |
|---|---|---|
| Checkbox | `ServerBrowserFilter` (Core, client) | **Show Only › Progenitor Servers**, in the game's Title Case like its neighbors. A clone of the **Scheduled Wipes** row, placed first in the Show Only list under the same parent (with hand-placed rows the game's rows move down one slot and get their positions back when the checkbox is removed), so the art, font and layout are the game's. Inherited wiring is cut first: every game script on the clone is switched off (Unity UI and TextMeshPro components stay, and so does any game subclass of `Toggle` or `Graphic`), the toggle's persistent and runtime listeners are switched off and removed, and it leaves any toggle group. The toggle is wired through Bootstrap's process-lifetime `UiSlots` trampoline. Built once per browser instance; a leftover from an earlier Core is destroyed by name (`progenitor-browser-filter`). After a fail-open it shows off, is not clickable and reads **Progenitor Servers (unavailable)**. It sits in the advanced filters like its neighbors; while the filter is on, the count line says so (below), so a player who closed the advanced panel still sees it. |
| Setting | `progenitor.ui.cfg`, `[ServerBrowser] ProgenitorOnly` (bool, default off) | Personal to this PC. Drawn in **Preferences › Join Game** as **Show only Progenitor servers**; the checkbox writes the same entry, and each follows the other within a frame. |
| Filter | `ServerBrowserFilter.LateTick`, from Core's LateFrame | Runs after every `Update` and the event system (FrameTicker `LateUpdate` on this HDRP client) and before the canvases rebuild. **The two-second rebuilds move here:** while the filter is on and the browser open, it holds the game's `_NextGetInternalResultsTime` far in the future, so the game's own Update does not rebuild on the clock; every two seconds, and when a refresh has just ended, it lets the game rebuild (`_NextGetInternalResultsTime = 0`, `UpdateServerListView`), removes the rows without the tag, and draws again. The rows, **Servers: N (M total)** and the selection are drawn from the trimmed list before the frame renders, and a row a player clicks in the next frame holds a server of the trimmed list. Between rebuilds the game returns the trimmed list itself. The hold is released whenever the filter is off, the browser closes, the filter fails or Core unloads. N counts the Progenitor servers that pass the game's other filters and search; M stays the full set. A selected server that is filtered out is deselected by the game's own check. While the filter is on, the count line reads `Servers: N (M total) · Progenitor only`. |
| Cost | per rebuild | One boxed row and one `Contains` on the game's own tag list per shown row (no tag is copied out), every two seconds and on each filter or search change, only while the browser is open and the filter on. `server_browser read` reports `perf {lastPassMs, maxPassMs, lastPassRows}`. |
| Logic | `ServerBrowserFilterState`, `ServerBrowserMarker` (game-free) | Tested over a fake list. |

**What it composes with.** The game's filters, search, sort and refresh run first, unchanged; this filter only removes rows from their result. Turning it off makes the game rebuild the list from the full set at once.

**Remaining wrong-row window.** Two rebuilds still happen inside the game's own Update: one caused by a filter, search or sort change in that frame, and one after a refresh that ends during that frame's Update. That frame draws the full list and the filter trims it before it renders, but a row click processed after the game's Update in that same frame could land on a server the filter then removes. The click that changes a filter is on the filter, so the realistic case is a row click in the frame a refresh ends. Measured in the live recipe (step 7), not yet run.

**Fail open.** A throw while reading the rows or redrawing turns the filter off for the rest of the session, logs once (`ServerBrowser: the Progenitor servers filter could not read the server list (…); it is off for this session and every server is shown.`, or `… failed (redrawing the server list threw: …) …`), releases the clock hold and makes the game rebuild the full list. A checkbox that cannot be built or wired is left out (logged once); the Preferences switch still filters.

**Hot reload.** No Harmony patch and no Bootstrap change: a Core swap is enough. On unload the clone is destroyed, its slot unwired and the clock hold released: with the browser open the full list is rebuilt and drawn at once; with it closed, the game rebuilds it the next time it opens.

**Interop.** The reads are fields and argument-free methods checked with the IL viewer: `PlayJoinMenuView._CachedServerListEntries`, `_ServerListSystem`, the Show Only toggles, `_PreviousIsRefreshing`, `ServersCountText`, `UpdateServerListView()`, `ServerListSystem._NextGetInternalResultsTime`, `IsRefreshing()`, `ServerListEntryData.Tags`. The game never reads its rebuild time for anything else (the `normalizedTimeUntilNextRefresh` it returns is a constant 1). The list's `get_Count`, `get_Item` and `RemoveAt` are in the game's own `List<ServerListEntryData>` instantiation (client metadata), so no new generic instantiation is made.

**Not built.** A mark on Progenitor rows in the list (an icon or tag), which was optional. Controller navigation into the new checkbox is whatever the cloned toggle's navigation gives; not checked.

<a id="browser-filter-live-checks"></a>**Live checks (2026-10-08).** Observed with the Steam client in **Join Game** and the isolated test server listed publicly for the test (random password; the listing and password were restored afterward):

- **Tag:** the server published `progenitor` after the game's own tags (`pvp, cs4, hc, ded, dif_no, progenitor`), and its row in the browser read `progenitor: true` (`server_browser read`). Only a Steam row was checked; an EOS row is not yet observed.
- **Filter:** with the checkbox on, 1 of 661 rows was shown and the count line read `Servers: 1 (661 total) · Progenitor only`. Turning it off showed all rows again. The slowest pass took 2.2 ms.
- **Checkbox:** it is the first row of **Show Only**, above **With Character** (requested 2026-10-08), with the game's spacing and caption. Both a real mouse click and the bridge click toggled it (log: `Show Only Progenitor Servers on (checkbox)` / `off (checkbox)`).
- **Cosmetic:** with the suffix the count line wraps to two lines.
- **Not run:** the wrong-row window (step 7).

## Why

The game's **Join Game** server browser shows a description for the selected server, from the `Description` field of the server's `ServerHostSettings.json`. It is the only place a player sees anything about a server before connecting. A server that needs Progenitor should say so there, in words a new player understands, before they spend time joining and creating a character only to be removed (see "What a player without Progenitor sees" in [modules.md](modules.md)).

## What the game does

From research. The live results above confirm the description path (field, Steam key, browser panel); the other details are not separately live-verified.

### Loading

`SettingsManager.VerifyServerSettings` layers the settings, later ones winning:

1. the install's `StreamingAssets\Settings`;
2. `<persistentDataPath>\Settings\ServerHostSettings.json`;
3. a `ServerHostSettings.json` in the save folder;
4. the `VR_DESCRIPTION` environment variable;
5. the `-description` command-line argument.

The file is read once at startup. A file named `ReloadSettings`, polled every 5 s, reloads everything.

### Publishing

`ServerBootstrapSystem.UpdatePlatformData` runs every second:

- **EOS:** re-reads `Description` every second and sends a session update only when something changed. A runtime change reaches the EOS listing within about a second.
- **Steam:** `SetServerDescription` (split into `desc0`, `desc1`, … of 120 characters) runs only on the first pass. A live change needs one explicit call.

### Limits

- The game's own setter cuts at **128 characters**. Steam carries up to 1,200, but 128 is the safe total for **admin text plus signature**.
- **Client:** the panel keeps line breaks and scrolls. Text passes the platform profanity filter and is not escaped. Rich text is probably on (unconfirmed), so the signature uses no markup. Links are not clickable.
- **Drawn once:** the panel draws the text when the player selects a server and does not refresh while it is shown.

## Admin settings

Two rows in Progenitor's **Server settings** (Progenitor's mod page, next to the server message). Admins only, server-wide, with the same authorization and audit as the other server settings ([settings-scopes.md](settings-scopes.md)).

| Row | Control | Default | Notes |
|---|---|---|---|
| **Server list description** | Multi-line text | The current `Description` | The admin's own text. Plain text, line breaks allowed. A live counter shows the room left: 128 minus the signature when it is on. |
| **Progenitor signature** | Switch | **On** | Adds one line after the admin's text. Turning it off removes the line and leaves the admin's text exactly as written. The signature is never stored in the admin's text. |

### The signature

A fixed line after the admin's text, on its own line:

> Requires the Progenitor mod (thunderstore.io)

At 46 characters, it leaves about 80 for the admin's text.

- **Wording:** it says Progenitor is a **mod** and where to get it, for a new player who knows neither. The browser does not make URLs clickable, so the site is spelled out.
- **No mod list:** once Progenitor is installed, its join screens offer the rest (the same reasoning as the in-game notice).
- **Over the limit:** when the admin's text and the signature together would pass 128 characters, the counter warns before saving and the admin's text is shortened with `…`. The signature is never cut.

## Applying the change: live

Save takes effect at once, without a restart, on the main thread:

1. **Set the runtime text:** `ServerBootstrapSystem.SetServerDescription(text)`, or the field directly. EOS follows within about a second through the game's own update loop.
2. **Steam:** call `PlatformSystemBase.GetSystem().SetServerDescription(text)`, then set any higher `desc{i}` keys left from a longer previous text to `""`. Never `ClearAllKeyValues`: it wipes keys the game writes only at startup.
3. **Persist:** write `Description` into the highest layer that exists: the save-folder `ServerHostSettings.json` if there is one, else `<persistentDataPath>\Settings\ServerHostSettings.json`. Never the install's `StreamingAssets` default. When `-description` or `VR_DESCRIPTION` is set, they override the file at the next start, so the row warns about it.

Both calls take plain strings: no by-ref structs and no new generic interop instantiations. The persisted text is the composed one (admin text, then signature) so that a restart without Progenitor still shows it. Progenitor keeps the admin's raw text and the switch in its own config, so turning the signature off removes it cleanly.

The existing settings-file resolvers (`ModManifest.OwnServerNameFrom`, `DevGate.ReadServerExposure`) do not follow the game's layering: they use the first file found, read a `Settings` folder the game does not read, and ignore the save-folder layer and the environment variable. This feature uses one shared resolver that matches the game, and both of them move to it.

## Reset countdown (coarse only)

Project rule: add tokens only if a live update is cheap and reliable. The research says it is cheap, two plain calls, so a **coarse** countdown is in scope:

- **`{reset:relative}`** renders "in 3 days", "in 5 hours" or "today", from the server message's reset settings ([join-flow.md](join-flow.md), server message settings).
  - **Updating:** the server re-checks every minute and re-publishes only when the rendered text changes, at most about hourly. Minute or second precision is not offered: the browser draws the text once per selection, so it would not tick anyway.
  - **Cost:** each change counts against the 128 characters.
- **`{reset:date}`** is shown with the server's time-zone abbreviation. The browser cannot convert it per player, so without the zone the date would mislead.
- **Fallback:** if live testing shows the listing does not follow, or the services throttle, the tokens are dropped and the description stays static.

## Out of scope

- **The server name**, also live-propagated per the research. On load the game strips newlines and tabs and caps the name at 64 characters; a runtime change skips that. No name editing is planned.
- **Clickable links**, which the vanilla browser cannot do.

## As built

Source-checked, built and game-free tested; nothing below has been seen in the game.

| Part | Where | Notes |
|---|---|---|
| Rows | `ServerListDescriptionSettings` (Core); `BepInEx/config/progenitor.serverlist.cfg`, `[ServerList]` `Description`, `Signature`, `Published` | Placed by `JoinServerSettings`' page builder, section **Server list** after the server message rows ([join-flow.md](join-flow.md#server-message-settings-admins)). `Description` is a server-wide text area with `MaxLength(128)`; its expanded editor counts against the room (`EditorCounterLimit`). **Room left** (every 0.25 s) counts the text being typed (`ModSettingsView.TypedText`: the field, or the open editor) after rendering the tokens. `Signature` is a switch: an administrator's bool row is now drawn as one (it was a field + Apply). `Published` is the server's status line, shown read-only as **Status**. On a server's first run (no cfg yet) `Description` starts as the game's live description, without a signature line an earlier run composed. |
| Composition | `ServerListDescription.Compose` | Text normalized (`\r\n` to `\n`, trimmed); room 128, or 82 with the signature (45 characters plus its line break); a longer text is cut to room − 1 and ends in `…` (a surrogate pair is never split); the signature is never cut. Empty text with the signature on is the signature alone. |
| Tokens | `ServerListDescription.Render` | `{reset:relative}` and `{reset}` read `in N days` / `in N hours` / `today` (a list row is drawn once, so `{reset}` is coarse here too); `{reset:date}` reads `Sat, Oct 10, 6:00 PM EDT` in the **server's** zone (always with the time since RESETWIPE1; seconds never). With the game's scheduled wipes on, the reset is the next wipe ([join-flow.md](join-flow.md#server-message-settings-admins)). The abbreviation comes from a table of common Windows and IANA zone ids (`EST`/`EDT`, `CET`/`CEST`, `GMT`/`BST`, `JST`, …); any other zone shows its offset (`UTC+05:30`): Windows has no abbreviations and initials of its names mislead. No reset: `not scheduled`; a passed one-time reset: its passed text. |
| Apply loop | `ServerListApplier` over `IServerListGame`; live half `ServerListGame` | Server only (`Plugin.IsServerProcess`, like the rest of the join flow), every 250 ms on the main thread. Waits until `SettingsManager._ServerHostSettings` exists and the server world is up, then applies at once (so the signature is advertised without anyone opening the page), after each edit (`SettingChanged`), and re-renders every minute, publishing only when the composed text differs from what was published **or** from the game's live value (a `ReloadSettings` re-read). |
| Live value | `ServerHostSettings.Description` field | Written directly: `ServerBootstrapSystem.SetServerDescription` would ignore an empty text and adds only the 128 cut the composition already makes. EOS follows through the game's own one-second `ModifySession`. |
| Steam | `PlatformSystemBase.GetSystem().SetServerDescription(text)`, `SteamGameServer.SetKeyValue("desc{i}", "")` | Only once `PlatformSystemBase._ServerInitSuccess`; until then the publish waits and retries each second. The keys the previous text used past the new chunk count are set to `""` (the first apply counts the game's own first publish of its loaded value); an empty text skips the setter and clears `desc0`. `ClearAllKeyValues` is never called. |
| File | `ServerListDescription.Persist` | Into the highest existing layer: the save folder's `ServerHostSettings` (bare name, then `.json`) when it exists, else `<persistentDataPath>\Settings\ServerHostSettings.json` (the game's `ServerHostSettings.LocalPath`), created with only `Description` when missing; never `StreamingAssets`. Only the `Description` value's bytes change (keys, order, spacing, comments and a byte-order mark are kept; the key is added after `{` when absent; the value is written ASCII-escaped). Unchanged text: no write. Temp file, then replace. |
| Override warning | `ServerHostSettingsResolution.OverrideOf("Description")` | `-description` or `VR_DESCRIPTION` (process, user, then machine) makes **Status** end with `The start command sets -description: at each start it replaces the saved description until Progenitor applies this one again. Remove it to keep the file in charge.` Progenitor still applies the row's text at each start. |
| Resolver | `ServerHostSettingsFiles` (Core; the same file compiled into Dev) | The game's layering (§Loading): default, persistent data, save folder (`<persistentDataPath>\Saves\v4\<SaveName>`, `SaveName` from `-saveName` / `-serverSaveName`, else the persistent-data or default file; on the server the game's own `GetCombinedFullSavePath`), environment, command line, with the override names from the attributes in the game's metadata. |

**Behavior changes from the shared resolver.**

- `ModManifest.OwnServerNameFrom` (the name a JOINFLOW1 server signs): now `-serverName`, then `VR_NAME` / `VR_SERVERNAME` / `VR_SERVER_NAME`, then the save folder's file, the persistent-data file and the install default, merged in that order, with line breaks and tabs removed and a 64-character cap like the game's `ValidateServerName`. It no longer reads `<install>\Settings\ServerHostSettings.json` (the game never does), and a per-save or environment name now wins as it does in the game.
- `DevGate.ReadServerExposure` (the dev bridge's server gate): the listing flags, address and port now come from the merged layers plus the environment and command-line overrides (`VR_LISTONSTEAM`, `-listOnSteam`, `-enableListOnSteam`, `VR_LIST_ON_EOS`, `-listOnEOS`, `-address`, `-gamePort`, …). A per-save file or an override that lists the server now refuses the bridge; `<root>\ServerHostSettings.json` and `<install>\Settings\` are no longer read. The private-instance requirement is unchanged ([bridge.md](bridge.md#the-opt-in-marker--the-bridge-refuses-to-start-without-it)).

**Bridge:** `server_desc` (`read`, and on the server `set` / `apply`); an admin's own edit from a client is the `setting` op's `request` on `progenitor/ServerList/Description` or `…/Signature` ([bridge-ops-join.md](bridge-ops-join.md#server_desc)).

## Acceptance (when built)

1. On the isolated test server, as admin: edit the description and save. Without a restart, the Steam client's **Join Game** panel shows the admin's text and then the signature.
   - This needs a listing the Steam client can see. Checking the EOS and Steam paths may need the server listed rather than LAN-only; the recipe will say how.
2. Turn the signature off: the panel shows only the admin's text.
3. Text at the limit with the signature on: the counter warns, the admin's text is shortened with `…`, and the signature stays whole.
4. Restart the server: the persisted text is the same.
5. `{reset:relative}` shows the coarse countdown, and it changes when the rendered text changes.

Also confirm live:
- whether rich text renders;
- whether an empty description clears the EOS value;
- where the `ReloadSettings` file is looked for.
