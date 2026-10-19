# Restoring an earlier world (server admins)

**Status: phase 0 and phase 1 built, game-free tested and verified live on the isolated test server (2026-10-07): phase 0 timing, restoring a wipe archive and Progenitor backups, the verified backup first, a tampered-archive refusal, the undo through the before-restore backup, the `.outgoing` cleanup, retention, a crash between the two renames (recovery, fail closed while the world is locked, recovery without a journal), the in-game dialogs including the three wipe-clock answers, **Restore and restart now** (the 60, 30 and 10 second warnings, the save and the stop), and mod data: `ctx.Save` goes back with the world, `ctx.ServerSave` does not.** Not tested live: the low-disk refusal (game-free tests only) and the live-server fence (see the [recipe](#live-acceptance-recipe-acceptance-procedure--run-2026-10-07)). The [pre-update backup kind](#pre-update-backups) (AUTOUPDATE3, 2026-10-09) is built and game-free tested; one was made, labeled and skipped as recent live, its retention beyond the keep count was not observed live. Research and the full design are in the internal research notes.

An admin of a dedicated server can put an earlier world back: a **wipe archive** the game made, or a **Progenitor backup** (made before an earlier restore, by **Back up now**, or on the [backup schedule](#backups-back-up-now-and-the-schedule)). The restore happens at the next server start, before the game picks its save, and it always backs up the current world first.

**Status of the backups (BACKUPNOW1): verified live on the isolated test server (2026-10-07): Back up now (while the server runs, and on a world in its first session after a wipe), a backup at server start, the every-N-hours schedule, retention by count and by age, and the autosave comparison in the list.** The low-disk refusal is game-free tested only. Their [live recipe](#live-acceptance-recipe-backups-acceptance-procedure--run-2026-10-07) records the run.

| Restore point | Restorable | What a restore brings back |
|---|---|---|
| Wipe archive, `<save>_<ticks>` beside the save | Yes (phase 1) | The whole archived folder: its saves, `StartDate.json`, `SessionId.json`, `progenitor\` mod data. The archive is **copied**: it stays and can be restored again. |
| Progenitor backup, `progenitor-backups\v<N>\<save>\<stamp>-before-restore` | Yes (phase 1) | The same, from the backup, after its files are checked against its own SHA-256 record. Listed as "Before restore of the wipe archive from <date time>, <date time>" (or "… of the backup from …", "… of the manual backup from …": labels never nest). |
| Manual or scheduled backup, `progenitor-backups\v<N>\<save>\<stamp>-manual` / `<stamp>-scheduled` | Yes | The same, checked against its own SHA-256 record. Listed as "Manual backup from <date time>" or "Scheduled backup from <date time>". |
| Pre-update backup (AUTOUPDATE3), `progenitor-backups\v<N>\<save>\<stamp>-pre-update` | Yes | The same, checked against its own SHA-256 record. Listed under **Backups made before an update** as "Before update · <the mods it came before> · <date time>" ([below](#pre-update-backups)). |
| Autosave or manual save in the save folder | No (phase 2) | Needs per-save mod-data history first; `progenitor\` is rewritten in place on every save. |
| The game's `.BACKUP\<version>` copies | No (phase 4) | — |
| A hosted private game's world (`CloudSaves`, the client's own saves) | No (phase 3) | Listed, but a restore is refused: "World restores are not available in a hosted private game yet". |

The current per-save settings files (`ServerHostSettings.json`, `ServerGameSettings.json` in the save folder) are the operator's, not the world's: a restore keeps the current ones, as the game's own wipe does.

**A restore touches the world folder only.** Mod data a plugin keeps through `ctx.Save` (`progenitor\` in the world folder) goes back with the world. Everything else stays as it is now: mod config files, Progenitor's server-level store `<persistentDataPath>\progenitor-server` (`ctx.ServerSave`, kept on purpose: season stats, bans), and any file another mod writes outside the world folder, which a restore cannot bring back. Mod authors: [Where should my data go?](modules.md#where-should-my-data-go)

## Where it is

**Mods → Server → Server rules**, after **Next scheduled wipe**. Only an admin of the joined server sees these rows:

| Row | What it shows or does |
|---|---|
| **Backups** | `3 restore points, 246 MB`, or `No restore points yet`. |
| **Restore an earlier world** · **Show** | Opens the list (below). |
| **Restore pending** | `Restore of <name> at the next restart (<wipe choice>).` while a request waits; with **Cancel restore**. |
| **Last restore** | The last result: `Restored <name>. Backup of the previous world: <backup name>.` or `Not restored: <reason>. Nothing changed.` |
| **Keep Progenitor backups** | The backups made before restores: 0 (keep every one) or 2 to 100 (default 5; 1 is raised to 2). Also `[WorldRestore] KeepBackups` in the server's `progenitor.cfg`. Manual and scheduled backups have their own rule (below). |
| **Back up the world now** · **Back up now** | The [manual backup](#backups-back-up-now-and-the-schedule), after a short confirmation. |
| **Last backup** | The backup running now (`Manual backup by <admin>: copying 12 of 40 files (35 %)…`) or the last manual or scheduled backup's result, amber when it failed. While a backup runs, the page asks the server for news every 5 seconds; when it finishes the server tells the admins who read the Backups, so this row and **Backups** show the result within moments (BACKUPROWS1, 2026-10-19; game-free tested in `BackupRowsRefreshTests`, not live-verified). |
| **Back up at every server start** | On or off (default off). `[WorldRestore] BackupAtStart`. |
| **Back up every** | Hours, 0 (off, the default) to 168. `[WorldRestore] BackupEveryHours`. |
| **Keep backups per kind** | The newest this many of each kind, 0 (no count limit) to 100 (default 10). `[WorldRestore] BackupKeepCount`. |
| **Delete backups older than** | Days, 0 (no age limit, the default) to 3650. `[WorldRestore] BackupKeepDays`. |

Every row is admin-only and lockable through `[ServerSettings] LockedInGame`: `progenitor/ServerRules/BackupNow`, `…/BackupAtStart`, `…/BackupEveryHours`, `…/BackupKeepCount`, `…/BackupKeepDays` (the `server-rules` group locks them all, with the restore rows).

## The dialogs

1. **Backups**: the header is left-aligned label/value lines: **Disk use** (`Restore points 186 MB · current world 22 MB · free 360 GB`), **By kind** (`manual 40 MB · scheduled 120 MB · before restore 82 MB · wipe archives 100 MB`, when there are manual or scheduled backups), **Kept** (`newest 2 backups made before a restore; wipe archives are never deleted`), **Backups kept** (the manual and scheduled rule), **Scheduled** (`at every server start and every 6 hours; next …`), **Schedule failed** (amber: the last scheduled backup failed and none succeeded since), **Last backup**, **Game autosaves** and **Autosaved** ([below](#the-games-autosaves-and-what-backups-add)), **Next restore deletes** and **Next backup deletes** (in amber, only when retention would delete a backup), and on a server where restores cannot run, **Restores** with the reason (the live-server fence, an unknown save folder, a lock). Two lines, **Autosaves are** and **Backups add**, compare the game's autosaves with Progenitor backups (once, wrapped, never cut at the narrowest dialog width). Below it, up to 10 restore points (the newest, but every kind keeps its newest), in four groups under small headings: manual backups, scheduled backups, backups made before a restore, wipe archives, each newest first. Each entry is a full-width two-line row: the kind and date (`Before restore · Wed, Oct 7, 2026 1:39 AM`) over a muted world age and size (`World age: day 34 · 82.4 MB`, the age at its last save); a click anywhere on the row opens it, and nothing is clipped at the narrowest dialog width (LISTROWS1, game-free tested in `BackupsListTests`, not live-verified). With more than 10, **Listed** says how many are shown.
2. **The entry**: kind, made, world started, world age, size. **Restore this world**, or why it cannot be restored.
3. **When the restored world's wipe date has passed** under the next start's `ResetDaysInterval` and `DayOfReset` (the game would archive it again at that very start), the admin must pick one answer or go back:
   - **Restart the wipe clock from today**: the restored `StartDate.json` is rewritten to the day of the restore; the original is kept beside it as `StartDate.before-restore.json`. The world's shown age starts again at 0.
   - **Turn wipes off**: `ResetDaysInterval` 0 in the next-start host settings file.
   - **Pick a new wipe date**: the date picker inside the dialog (00:00 server time). `ResetDaysInterval` becomes the days from the restored start date to that day (1 to 365, after today), and `DayOfReset` becomes `Any` when the weekday would move the date.
   The two interval answers are offered only when the interval can be written: not when a command-line argument or environment variable sets it, the lock list locks it, or no settings file is known (the dialog says which).
4. **The typed confirmation**: the backup step, the chosen state (date, world age, size), the world's start date after the restore, what happens (later characters, items, castles and clan changes are not in it; the players online now), the next wipe date after the restore, the backups retention would delete after it, and the restart-now warning. Type `RESTORE` (case-insensitive, as the dialog kit's typed confirmations): **Restore at next restart** and **Restore and restart now** stay disabled until then.

**Which server and world (BACKUPSDLG1).** The list, the typed confirmation and the Back up now confirmation start with a **Server** line (`<server name>, world <save name>`: the name the Server tab shows and the running save). Their answers are notices named the same way: **Restore requested** reads `On <server name>: world <save> goes back to the manual backup from Wed, Oct 7, 2026 10:48 AM at the next restart, after a verified backup of the current world. Next wipe after the restore: …`; **Restore cancelled**, **Not restored**, **Not cancelled**, **Backing up** and **Not backed up** start with `On <server name>:` too. The entry and the typed confirmation show a request already waiting (**Restore pending**, **Replaces**) before another replaces it.

**Bound to the server (BACKUPSDLG1).** Every Backups dialog (the list, the entry, the wipe answers, the date, the typed confirmation, Back up now and the notices) belongs to the server session it was opened in. When the client leaves that server (a disconnect, a kick, the world ending, a new join's handshake) they all close without an answer, and an answer still on its way is dropped: nothing opens on the title screen. The Server rules' own wipe and password confirmations belong to the Server tab's page and close with it, and the Players joining window closes when the joined server's channel goes away (both source-checked, unchanged here).

**A request changed elsewhere (BACKUPSDLG1).** When the pending request or its countdown changes (another admin's request or cancel, the dev bridge, a countdown that ended without a stop), the server sends the admins who read the Backups this session a small notice on the Server rules channel (no state, no polling). A client with a Backups dialog open asks once for the state and redraws a view whose request no longer matches: the list again, or the entry again (a typed confirmation falls back to it and its typed word is dropped), with the note `Changed on the server: restore of … at the next restart.` or `Changed on the server: no restore is pending now.` A notice about a request becomes the list with that note. The wipe answers and the date stay (the confirmation after them is drawn from the new state) unless their restore point is gone. With no Backups dialog open, the rows ask on their next draw.

These three are built, game-free tested (`BackupsDialogSessionTests`, `WorldRestoreServerTests`) and verified live on the isolated test server (2026-10-07): the "Restore requested" notice named the server and the world; a cancel through the dev bridge turned the open notice into the list with "Changed on the server: no restore is pending now."; leaving the server closed the open notice, and the title screen showed no dialog. The server name comes from the Server tab's state: the Backups opened before that state arrived (the bridge's `world_backups open` straight after a join) show only `world <save name>`.

**Restore and restart now** writes the request, then broadcasts `The server restarts in 60 seconds to restore an earlier world.` (and at 30 and 10 seconds) and runs the graceful stop (save, wait for the save file, quit). **Progenitor never starts the server again**: the host's own restart (a Windows service, a panel, a script) must bring it back, or it stays off. Cancel restore during the countdown stops it. A server that cannot save and stop itself keeps the request for the next restart and says so.

## What happens at the next start

Bootstrap (`Plugin.Load`, server process, before any patch and before Core loads) runs, in order:

1. **The live-server fence**, before any file is touched: the BepInEx folder, the process path and every command-line argument, compared as final paths (forward slashes, `\\?\`, this machine's `C$` shares, `..`, and, on Windows, 8.3 names, junctions and symbolic links) against `C:\VRisingServer`. A match: nothing is read or written.
2. **The recovery pass** over `BepInEx\progenitor-restore.journal.json` (below).
3. **A pending request** (`BepInEx\progenitor-restore.json`), unless it already ran: then its recorded result is reported again and it is never run twice.

A request runs as follows. The journal is written (flushed) before every step that changes the disk.

1. **Validate**: the fence over every path in the request; not a hosted game; this server's save folder (`-saveName` and `-persistentDataPath`, when given, must agree); a wipe archive beside it or a Progenitor backup of it, unchanged since the admin chose it (the file list, sizes and write times); a host settings file under the server's folder or the persistent-data root; room for two copies plus 10 % and 256 MB.
2. **Back up** the whole current world (everything but `.TEMP\`, write times kept, every file flushed with `Flush(true)`) to `<stamp>-before-restore.partial`.
3. **Verify** it by file count, size and every file's SHA-256. A newest save that does not decompress is only a warning here: the backup is an exact copy, so a damaged live world can still be restored over. Then the sidecar (with the file list and its SHA-256 values), the folder flushes and the rename to its final name.
4. **Stage** a copy of the source with the current per-save settings files. A Progenitor backup must match its sidecar's SHA-256 list (per-save settings aside). The copy is verified strictly, gzip check included: the newest save must decompress with a matching CRC-32 and length.
5. **The wipe choice**: the staged `StartDate.json`, or the host settings file (in the staged copy when it is the per-save file; otherwise its bytes and write time go into the journal first). Then the **wipe date is recomputed** the way the game will: the staged world's `StartDate.json` and the next start's `ResetDaysInterval` / `DayOfReset` (the command line and `VR_RESET_DAYS_INTERVAL` / `VR_DAY_OF_RESET` first, else the highest settings file that sets them). A wipe still due, or a picked date that is not after today any more, stops the restore and puts the settings file back.
6. **Swap** with two renames on one volume, each retried with backoff (0.1 to 2 s, five times: antivirus and indexer locks). When the second fails the first is undone. When the undo fails too, the server **stops** (fail closed, exit code 75) instead of letting the game start an empty world; the result says which folder to rename.
7. After the swap the restore stands. The replaced world stays in `.outgoing-<id>` until the restored world has saved once (the save hook marks the journal, or a save file newer than the swap is found); the next start after that removes it. If it is not the verified backup's exact copy it becomes a backup of its own (`<stamp>-replaced`). A new restore before the restored world ever saved keeps it as such a backup too. Then **retention** (below).

The result goes to `BepInEx\progenitor-restore.done.json` (every step, its time, the backup path, warnings), and the request is renamed to `progenitor-restore.applied-<id>.json` or `.refused-<id>.json`. The page's **Last restore** line reads the result file.

**Recovery.** At every start, before any request, the journal of an interrupted restore is settled:

| Journal phase at the crash | What the next start does |
|---|---|
| `prepare`, `backed-up`, `staged` | The world was never touched: the settings file is put back, the copies are removed, the request is refused ("interrupted before the swap … Nothing changed"). |
| `swapping`, save folder there, no `.outgoing` | The first rename never happened: undone as above. |
| `swapping`, both there | The second rename happened: finished as a success. |
| `swapping`, save folder missing | `.outgoing` is renamed back (retried), and the request is refused ("the current world was put back"). When that fails, the verified staged copy is rolled forward instead. When neither works the server stops before the game loads. |
| `swapped`, no result written | Finished and reported as the success it was. |
| `swapped`, result written | Waits for the restored world's first save, then removes `.outgoing`. |

**The last check before the game loads** does not rely on the journal. For every save folder this start knows (the command line's `-persistentDataPath` and `-saveName`, the journal's, the request's): when it is missing while restore debris lies beside its backups (`.outgoing-*`, `.incoming-*`, `*.partial`) or the journal cannot be read, the world in the one `.outgoing-*` folder that holds a save is renamed back; with none, several, or a rename that keeps failing, the server stops (exit code 75). An unreadable journal with no save folder on the command line stops the server too; next to a save folder that is in place it is set aside as `progenitor-restore.journal.json.unreadable`, and a waiting request runs at the start after that. A missing save folder with no debris is an ordinary first start.

Leftover `*.partial`, `.incoming-*` and `.deleting-*` folders without a journal are removed at the next restore; an `.outgoing-*` folder is never removed without its journal (it may be a stranded world).

**Retention** keeps the newest `KeepBackups` backups made before a restore (manual and scheduled backups are never counted or deleted by it; they have [their own rule](#backups-back-up-now-and-the-schedule)), and always: the backup the restore just made, the source of the restore, and the oldest backup of the restore chain (every restore of a save continues one chain, so this is the world as it was before the first restore). A backup is deleted by renaming it to `.deleting-<name>` first, so a half-deleted folder is never listed. The game's wipe archives and `.BACKUP` copies are never deleted.

**When the server stopped itself** (the log has `WorldRestore: FAIL CLOSED:`): the save folder is missing. The result file and the log name the folders: rename the `.outgoing-<id>` folder (or the verified `<stamp>-before-restore` backup, without its `progenitor-backup.json`) back to the save folder, then start the server; the recovery pass finishes the rest.

This is Bootstrap code: **deploying it needs a restart of each server** ([hot-reload.md](hot-reload.md)).

## Backups: Back up now and the schedule

BACKUPNOW1. Besides the backup every restore makes, an admin can make one at any time, and the server can make them on a schedule. Both are Progenitor backups beside the restore chain, listed in the Backups list under their own heading and restorable through the restore flow above (their files are checked against their own SHA-256 record first).

**What a backup holds.** The whole world folder `Saves\v<N>\<save>` except the game's save staging `.TEMP\`: every save file (autosaves and named saves), `StartDate.json`, `SessionId.json`, the per-save settings files, and the mod data Progenitor keeps for the world (`<save>\progenitor\`, the ModSave stores). **Not included:** anything outside the world folder: other mods' own files, BepInEx config, the server's install and its `Settings\` folder. A restore still keeps the current per-save settings files, as above.

**Back up now** (Server rules → **Back up the world now**). The confirmation says where (`progenitor-backups\v<N>\<save>` beside the `Saves` folder), the estimated size and free space, what is included, and what retention would delete after it. Then, while the server keeps running:

1. **Save.** The game's world save is triggered through the same path as the graceful stop (`TriggerPersistenceSaveSystem.TriggerSave`, then `ModSave.SaveAll`, so the mod data lands with the world). A refused save refuses the backup.
2. **Wait** for the save file: the newest save's write time must move past the one sampled before the trigger, within 15 seconds, polled through the frame ticker (never blocking the frame that performs the save). Then one second to settle.
3. **Copy off the main thread** (a background thread; file IO only, no game object, no logger: its log lines are written by the main thread). The copy is consistent:
   - the folder's shape (path, length and write time of every file) is read before the copy;
   - each file is hashed (SHA-256, the source as read), then copied, flushed, with its write time kept;
   - the shape is read again after the copy: any difference (an autosave landed, the game deleted an old autosave, a ModSave store was written) and the copy is removed and made again after 3 seconds, up to 3 times;
   - the copy is verified against the hashes taken from the source (a file written between its hash and its copy differs here and is retried the same way), and the newest save must decompress (gzip CRC and length; a save caught half written is retried);
   - then the sidecar (`progenitor-backup.json`: kind `manual`, trigger, attempts, every file's SHA-256), the folder flushes and the rename from `<stamp>-manual.partial` to `<stamp>-manual`.

   A backup that mixes two saves is therefore never made without it being detected: the shape and every file's bytes are the same before, during and after the copy. A folder that keeps changing for all 3 copies fails with `the save folder kept changing during the copy`.
4. **Finish** (main thread): retention, the log line, the result in **Last backup** and the list, `BepInEx\progenitor-backups.status.json`, and the audit line in `progenitor-settings.log` (row `progenitor/ServerRules/BackupNow`, `backup_now` when accepted, `backup_now done <folder>` or the failure when finished; both name the admin).

Only one backup runs at a time; a second request is refused while it runs. Core unloading (a hot swap) stops a running copy between files and removes its partial folder. A `*-manual.partial` or `*-scheduled.partial` folder left by a crash is removed by the next backup.

**Disk safety.** Before anything is saved or written: the free space on the save's volume must cover the world's size plus 10 % plus 256 MB, else `Not enough free disk space for a backup: <need> needed (…), <free> free.` The header's **By kind** line shows the disk use of each kind.

**The schedule** (off by default):

| Setting | What it does |
|---|---|
| **Back up at every server start** (`BackupAtStart`) | Bootstrap, in `Plugin.Load`, after the [start-time restore](#what-happens-at-the-next-start) and before the game loads (the game is not writing, so nothing can change under the copy). A Scheduled backup with trigger `start`; a newest save that does not decompress is a warning here (the backup is the exact copy, so a damaged world is still kept). It reads `[WorldRestore]` from `BepInEx\config\progenitor.cfg` itself (Core is not loaded yet). Skipped on the live-server path, for a hosted game, when the save folder does not exist yet (a first start) and when the start stops before the game loads. |
| **Back up every N hours** (`BackupEveryHours`) | Core, while the server runs, through the Back up now path (save, wait, consistent copy). Checked once a minute. Due N hours after the newest scheduled backup on disk (a start backup counts), else N hours after Core started; never in the first 10 minutes after Core started; after a failure, again 30 minutes later (or N hours, when shorter). |

**A start backup with a restore waiting.** The world before a restore is copied **once**, before the swap: by the restore's own before-restore backup, which every restore makes and verifies (it belongs to the restore chain and its keep count). So when a restore runs at that start and succeeds, no second copy is made (`WorldBackup: the start backup is the restore's own backup at this start …`). When the restore is refused, nothing changed (a refused restore removes the backup it made) and the start backup is made as usual, of that same world. A restore result only reported again (it ran at an earlier start) does not count: the start backup runs.

**A failed scheduled backup** is logged (`WorldBackup: START BACKUP FAILED (the server starts anyway): …` or `WorldBackup: the scheduled backup did not start: …`), recorded in `progenitor-backups.status.json`, and shown in amber as **Schedule failed** in the list header until a scheduled backup succeeds. It never stops the server; only the restore's own [fail-closed rules](#what-happens-at-the-next-start) do.

**Retention of manual and scheduled backups** (runs after each manual or scheduled backup):

- **Keep backups per kind** (`BackupKeepCount`, default 10): the newest N **of each kind**. One setting serves both kinds but each kind is counted on its own, so an hourly schedule never pushes out an admin's manual backup, and there is one rule to learn instead of four settings.
- **Delete backups older than** (`BackupKeepDays`, default 0: off): those older than D days.
- **Both set: the stricter rule wins.** A backup must pass both to stay (it goes when it is past the newest N of its kind **or** older than D days). Both 0 keeps every one.
- **Never deleted:** the newest backup of each kind (whatever its age), the source of a pending restore request, every backup made before a restore (the restore chain keeps its own rule, **Keep Progenitor backups**, which in turn never counts or deletes manual and scheduled backups), wipe archives and the game's `.BACKUP` copies.
- A backup is renamed to `.deleting-<name>` before it is deleted, as the restore's retention does. The header's **Next backup deletes** shows what the next manual and the next scheduled backup would delete.

### The game's autosaves and what backups add

The list header also reports the game's own autosaves, read-only: **Game autosaves** (`4 files, 330 MB; the game keeps 10, one every 2 minutes`: the `AutoSave_<N>.save.gz` files at the top of the save folder, and the effective `AutoSaveCount` and `AutoSaveInterval` from the server's host settings, the command line and environment first) and **Autosaved** (the oldest to the newest autosave's time). Two header lines, **Autosaves are** and **Backups add**, say what each one is:

| | Game autosaves | Progenitor manual and scheduled backups | Wipe archives |
|---|---|---|---|
| What is included | One save file each. Not the mod data at that moment (ModSave keeps one current copy in `<save>\progenitor\`), not `StartDate.json` | The whole world folder: every save, the mod data, `StartDate.json`, the per-save files | The whole world folder as the scheduled wipe moved it away |
| When it is made | Every `AutoSaveInterval` seconds while the server runs | **Back up now**, at every server start, and/or every N hours | When a scheduled wipe runs at a server start |
| How long it is kept | The newest `AutoSaveCount` (after `AutoSaveSmartKeep`); older ones are deleted by the game, in the same folder | By the retention rule above; the newest of each kind always | Never deleted |
| Restorable here | Not yet (phase 2) | Yes, checked file by file against their record | Yes |

This is Bootstrap code for the start backup and Core code for the rest: **deploy Bootstrap, Core and Dev together and restart each server** (Bootstrap changed: `WorldRestoreAtStart` and the shared `WorldRestore` files it compiles). Do not hot swap only Core onto a server whose Bootstrap is older: that Bootstrap's restore retention does not know the new kinds and would count manual and scheduled backups as restore-chain backups (and could delete them after a restore).

## Game behavior this relies on

These are the game's own rules (research spec §1-§2, native server build; not yet observed live, see phase 0 below):

- With `SaveFileName` empty, the server loads the save file with the newest **write time** in the save folder (`*.save.gz` or `*.save`, top level, not dot-prefixed), whatever its number. The autosave counter continues from the newest autosave by write time plus one.
- Before each autosave it keeps the newest `AutoSaveCount` autosaves by write time (after the `AutoSaveSmartKeep` buckets) and deletes the rest; named saves are never deleted.
- A scheduled wipe (`CheckIfResetServer`, at start, once local time has reached the start date plus `ResetDaysInterval`, moved to `DayOfReset`) **moves** the whole save folder to `<save>_<DateTime.Now.Ticks>` and copies only the two per-save settings files back into a new empty folder. The game never deletes an archive.
- `StartDate.json` is `MM/dd/yyyy`, invariant culture, no quotes.
- The autosave rotation comes from the host settings: `AutoSaveCount` (overridden by `-saveCount`, `VR_SAVECOUNT`, `VR_SAVE_COUNT`) and `AutoSaveInterval` in seconds (`-saveInterval`, `VR_SAVE_INTERVAL`, `VR_SAVEINTERVAL`); the shipped default `ServerHostSettings.json` has `AutoSaveCount` 10 and `AutoSaveInterval` 120 (server metadata and the shipped file; the effective values are not observed live).

### Pre-update backups

AUTOUPDATE3 ([auto-update.md](auto-update.md#phase-3-as-built), decision 5 of 2026-10-09). With **Back up the world before updating** on (Server tab → Updates, `[AutoUpdate] BackupBeforeUpdate`, off by default), the server makes one backup of kind `pre-update` per update batch, before the batch's first write, through the Back up now path above (save, wait, consistent copy, checked). A batch is one automatic pass that writes (a hot update or a staged one) or one restart Progenitor makes to apply staged updates (Restart when empty, Restart now).

| Rule | What it does |
|---|---|
| Skip | No backup is made when a manual, scheduled or pre-update backup is newer than `PreUpdateBackupSkipHours` (default 6; 0 always makes one). Wipe archives and before-restore copies hold another world and never stand in. The audit line names the backup that stood in. |
| Failure | A batch whose backup fails writes nothing (tried again at the next check); a restart whose backup fails is cancelled and retried at the next empty moment. |
| Retention | The newest `PreUpdateBackupKeep` (default 2, 1 to 20) pre-update backups stay, counted only among them. `BackupKeepCount` / `BackupKeepDays` never count or delete them, and their own rule never deletes a manual, scheduled or before-restore backup. |
| Label | The sidecar's `updates` keeps the mods it came before (`ChatSkin 1.3.0, BroaderMap 2.0.2`); the list's row reads `Before update · ChatSkin 1.3.0, BroaderMap 2.0.2 · <date time>` and the header's **By kind** adds `before update <size>`. |

| Sidecar | A pre-update backup's sidecar is `progenitor-backup.pre-update.json`, not `progenitor-backup.json` (review 2026-10-09). An older Bootstrap or Core lists only folders with `progenitor-backup.json`, so it never sees a pre-update backup: it cannot count one into the restore chain's keep count (which would prune real before-restore backups), restore it or delete it. Code that knows the kind reads either name. |

The kind is shared code Bootstrap compiles, so **deploy Bootstrap with Core and restart each server**; until then an older Bootstrap ignores pre-update backups (the sidecar row above) instead of miscounting them. Game-free tested (`ServerUpdateModelTests`, a real-file backup run); the sidecar name was not run live.

## Not built yet

Deleting a backup by hand, autosave and manual-save restores (phase 2, with per-save mod-data snapshots and a save pause after a failed restored load), hosted private games and Steam Cloud worlds (phase 3), and restoring the game's `.BACKUP` copies (phase 4). Mods that keep data outside `<save>\progenitor\` (their own files, BepInEx config) are not backed up or restored. A restored world whose sidecar names a newer game version is not checked yet.

## Bridge

`world_backups` ([bridge-ops-join.md](bridge-ops-join.md#world_backups)): on the server `list`, `request` (dry run by default), `pending`, `result`, `cancel`, `backup_now` (dry run by default), `schedule`, `retention` (a preview); on the client the same through the page's own request path, plus `open` (the Backups list), `dialog_date`, `backup_now`, `set` and `open_backup_now`. Drive the dialogs with `mods_ui dialog_choose` and `mods_ui dialog_type`.

## Phase 0: the timing line

Every server start logs, from Bootstrap:

```
WorldRestore phase0: Plugin.Load at 2026-10-07 13:30:00.123Z (850 ms after the process started).
WorldRestore phase0: at … the save folder from the command line is …\Saves\v4\<save>; its newest save file is AutoSave_N.save.gz (written …). The game picks its save later, in GameBootstrap.Start.
WorldRestore phase0: BeginCreateServerWorld at …, 9000 ms after Plugin.Load: SaveDirectory …, SaveToLoad AutoSave_N.save.gz (the same file Plugin.Load saw as newest).
```

The first line is logged before a pending restore runs and the second after it; the third comes from the `BeginCreateServerWorld` postfix, after `GameBootstrap.Start` resolved the save and the wipe check ran. With BepInEx's `[Logging.Disk] WriteUnityLog = true` the game's own `Attempting to load most recent save file for SaveDirectory: … SaveToLoad: …` line lands in the same `LogOutput.log`, between them.

## Live acceptance recipe (acceptance procedure — run 2026-10-07)

The isolated test server only (`<testserver>`), on a scratch world, never `C:\VRisingServer` and never the bots' `test1` save. Each start and stop of the test server needs explicit authorization for that session; match the process by its full path. Steps 1-15 were run against the first build on 2026-10-07 (phase 0, archive restore, tampered archive, undo); run them again on this build, then steps 16-21 for the hardening.

**Setup** (server stopped):

1. Build Debug. Deploy Bootstrap, Core and Dev to the test server: `powershell -NoProfile -File tools/deploy.ps1 -Projects Progenitor.Bootstrap,Progenitor.Core,Progenitor.Dev -Side server -NoBuild` (Debug, [deploy note](dev-loop.md#the-three-pieces)). Bootstrap needs the restart that follows.
2. Make a scratch world: copy `forms-save-data-20260906` to `<testserver>\restore-save-data`, rename `Saves\v4\test1` there to `restore1`, and make a launcher copy of `start-test-server.bat` with `-persistentDataPath "<testserver>\restore-save-data" -saveName "restore1"` and its own `-logFile`.
3. Make a wipe archive the way the game does: copy `Saves\v4\restore1` to `Saves\v4\restore1_<ticks>`, where `<ticks>` is `[DateTime]::Now.AddDays(-1).Ticks` in PowerShell. In the copy, change `StartDate.json` to a date more than `ResetDaysInterval` days ago when testing the wipe answers, or leave it.
4. For one run, set `WriteUnityLog = true` under `[Logging.Disk]` in the test server's `BepInEx\config\BepInEx.cfg`; set it back afterwards.

**Phase 0** (start the scratch server):

5. `LogOutput.log` has the three `WorldRestore phase0:` lines in that order, and the game's `Attempting to load most recent save file … SaveToLoad: <file>` between the second and the third. `SaveToLoad` is the file the second line named as newest.

**Server bridge** (scratch server running):

6. `world_backups {"action":"list"}`: one archive (`archive:restore1_<ticks>`), its `startDate`, `ageDays`, `size`, `paths`, `restoreAllowed true`, `saveFolder` ending `restore1`, `backupsRoot` `…\progenitor-backups\v4\restore1`, `pruneNext []`.
7. `world_backups {"action":"request","id":"archive:restore1_<ticks>"}` (dry run): `ok true`, a `Dry run:` note with the next wipe date; `pending` reads `null`. With an old `StartDate.json` and wipes on, the same without `wipe` is refused with `This world's wipe date has passed…`.

**Client, as an admin** (join the scratch server; a client nobody is playing on):

8. Server tab → Server rules: **Backups** reads `1 restore point, <size>`. `world_backups {"action":"open"}` (or **Show**): the list; `mods_ui dialog_choose {"choice":"0"}` (or `mods_ui click_at {"row":"dialog-option-0"}`) → the entry; `dialog_choose yes` → the wipe answers (when due) → `dialog_choose 2` → the date dialog; `world_backups {"action":"dialog_date","date":"<a day next month>"}`; `dialog_choose yes` → the typed confirmation. `mods_ui status`: `dialog.confirmWord RESTORE`, `mainEnabled false`, `extras[0].enabled false`. **Screenshot** the confirmation (focus-free capture).
9. `mods_ui dialog_type {"text":"RESTORE"}`: `mainEnabled true`, `extras[0].enabled true`. `dialog_choose yes`: the notice `Restore requested`; **Restore pending** reads the line; server `world_backups {"action":"pending"}` shows the request (`wipeMode date`, `wipeIntervalDays`, `hostSettingsPath`, the `layers`). The server's `progenitor-settings.log` has the audit line.
10. Cancel restore on the page: `pending` reads `null`. Ask again (steps 8-9). Disconnect normally.

**The restore** (restart the scratch server):

11. `LogOutput.log`: `WorldRestore: validate ok`, `backup ok`, `verify backup ok`, `stage ok`, `wipe check ok`, `swap ok`, then `applied request …` and the phase 0 lines with `SaveToLoad` the archive's newest file. `world_backups {"action":"result"}`: `ok true`, the steps, `backupPath`. On disk: `restore1` holds the archive's files (and the current per-save settings), `restore1_<ticks>` is unchanged, `progenitor-backups\v4\restore1\<stamp>-before-restore\` holds the old world and `progenitor-backup.json`, **`.outgoing-<id>` is still there**, `BepInEx\progenitor-restore.journal.json` is in phase `swapped` with `resultWritten true`, `progenitor-restore.applied-<id>.json` exists and `progenitor-restore.json` does not; the host settings file has the new `ResetDaysInterval`. The world loads (`PersistenceV2 - Finished Loading`).
12. Wait for the first autosave (`WorldRestore: the restored world saved; the replaced copy is removed at the next start`), restart: `.outgoing-<id>` and the journal are gone (`… the replaced copy … is removed`).
13. Join: the world is the archived one. **Last restore** reads `Restored the wipe archive from …`; the list shows `Before restore of the wipe archive from …`. Restore that backup the same way and check the old world is back and its label reads `Before restore of the backup from …` (no nesting).
14. **A refusal**: request again, stop the server, change one file in the archive (or add one), start: `progenitor-restore.refused-<id>.json`, `Not restored: the restore point changed after it was chosen; choose it again. Nothing changed.`, and `restore1` unchanged; no journal.
15. **Restart now**: request with **Restore and restart now**: the chat shows the 60/30/10 s lines and the server stops by itself (graceful stop log lines). Start it by hand and check step 11's lines.

**The hardening** (scratch server; each step starts from a fresh request):

16. **Retention**: set **Keep Progenitor backups** to `1`: refused (`Give 0 … or a whole number from 2 to 100`). Set `2`, make three restores: the confirmation's **Retention** line names the backup that will go, the result's `pruned` lists it, the oldest backup (the world before the first restore) stays, and no `.deleting-*` folder is left.
17. **A backup that does not match its record**: in a Progenitor backup, change one byte of `StartDate.json` keeping its length and write time (`(Get-Item f).LastWriteTime` before, set it back after), request a restore of it, restart: refused with `does not match its own record`.
18. **A wipe that is due at apply time**: request a not-due restore, then before the restart set `ResetDaysInterval` in the host settings so the restored world's start date plus it is in the past: refused with `would be wiped at this start`, and the settings file is byte-identical to before.
19. **Crash before the swap**: request a restore; start the server and kill the process (Task Manager / `Stop-Process -Id <pid>` matched by full path) while `LogOutput.log` shows `backup ok` but not `swap ok` (a large world helps). Start again: `recovery: Not restored: the restore was interrupted before the swap … Nothing changed.`, no `.partial` / `.incoming` folder, no journal, the world unchanged, the request renamed `refused`.
20. **Crash between the renames** (simulated by hand, server stopped; the real window is a few milliseconds): rename `Saves\v4\restore1` to `progenitor-backups\v4\restore1\.outgoing-test` and write a journal `{"schema":1,"requestId":"test","phase":"swapping","saveFolder":"<save>","backupsRoot":"<backups root>","outgoing":"<that .outgoing-test>","incoming":"","sourceName":"test"}` to `BepInEx\progenitor-restore.journal.json` (UTF-8, no BOM; paths as plain Windows paths, as JSON-escaped `\\` or with forward slashes: all three are read). Only `requestId` is required. Also run it once with no journal at all (only `.outgoing-test`): the last check renames it back (`the save folder … was missing after a restore; the world in … was put back`). Start: `recovery: the current world was renamed back from …`, the result `Not restored: the restore was interrupted between the two renames of the swap; the current world was put back.`, and the world loads.
21. **Fail closed** (server stopped): as step 20, but keep `.outgoing-test` open in another process so it cannot be renamed (for example a `cmd` window with `cd` into it). Start: the phase 0 line names the save folder with `(MISSING)`, the log ends with `WorldRestore: FAIL CLOSED: …` and the process exits with code 75 before any world is created (no new `restore1` folder appears). Repeat with the journal replaced by garbage (`{ not json`), and with no journal: the same stop. Close the `cmd` window, start again: renamed back as in step 20.
The live-server fence is not tested live: any launch that names `C:\VRisingServer` could write there. The game-free tests cover it (final paths, `\\?\`, admin shares, `..`, arguments, and no file operation before the check).

**Cleanup**: stop the scratch server, set `WriteUnityLog` back, delete `restore-save-data` and the launcher copy, and leave the test server on its usual `test1` launcher. Disconnect the client normally and leave it at the title screen.

## Live acceptance recipe: backups (acceptance procedure — run 2026-10-07)

BACKUPNOW1 on the headless scratch world: `<testserver>\headless`, launcher `start-restore-test.bat` (`-persistentDataPath "<testserver>\headless\restore-save-data" -saveName "restore1"`), never `C:\VRisingServer` and never a bots' save. Each start and stop of that server needs explicit authorization for the session; match the process by its full path (`<testserver>\headless\server\VRisingServer.exe`) and its `-saveName restore1` argument. Below, `<save>` is `restore-save-data\Saves\v4\restore1` and `<backups>` is `restore-save-data\progenitor-backups\v4\restore1`.

**Setup** (server stopped):

1. Build Debug. Deploy Bootstrap, Core and Dev to the headless server: `powershell -NoProfile -File tools/deploy.ps1 -Projects Progenitor.Bootstrap,Progenitor.Core,Progenitor.Dev -Side server -TestRoot <testserver>\headless -NoBuild`. Bootstrap changed: the start that follows is required.
2. Note what `<backups>` holds now (earlier restore tests may have left before-restore backups; they must survive every step below).

**Start backup** (in `<testserver>\headless\server\BepInEx\config\progenitor.cfg`, `[WorldRestore] BackupAtStart = true`, `BackupKeepCount = 2`):

3. Start the server. `LogOutput.log`, after the `WorldRestore phase0: Plugin.Load` line and before the second phase 0 line: `WorldBackup: start backup of …restore1 (before the game loads)`, `WorldBackup: made <stamp>-scheduled: N files, …`, `WorldBackup: start backup done in … ms`. `<backups>\<stamp>-scheduled\` holds every file of `<save>` but `.TEMP\`, with `progenitor-backup.json` (`kind scheduled`, `trigger start`). `BepInEx\progenitor-backups.status.json` has `start.ok true`. The world loads.

**Server bridge** (server running):

4. `world_backups {"action":"schedule"}`: `atStart true`, `everyHours 0`, `nextDueUtc null`, `lastScheduledUtc` the backup's time, `status.start.ok true`.
5. `world_backups {"action":"backup_now"}` (dry run): `ok true`, `note` starting `Dry run: a manual backup of restore1 (about …` with the free space; nothing new in `<backups>`.
6. `world_backups {"action":"backup_now","dryRun":false}`: `ok true`, `job.step saving`. Within about 20 s: `LogOutput.log` has `WorldBackup: manual backup by dev bridge: the world save was triggered …`, a new `AutoSave_N.save.gz` in `<save>`, then `WorldBackup: made <stamp>-manual …`; `schedule` reads `job null` and `last.ok true`. The copy holds the new autosave; `<save>\progenitor\` matches it file by file.
7. `world_backups {"action":"retention"}`: `afterManual` lists nothing (one manual backup), `afterScheduled` lists nothing (one scheduled backup), `restoreKeep 5`.

**Client, as an admin** (a client nobody is playing on; join the headless server):

8. Server tab → Server rules: the rows **Back up the world now**, **Last backup** (`Made Manual backup from …`), **Back up at every server start** (on), **Back up every** (`Off`), **Keep backups per kind** (`2`), **Delete backups older than** (`Never`). `world_backups {"action":"open"}`: the list has the headings **Manual backups**, **Scheduled backups** (and **Backups made before a restore** / **Wipe archives** when present); the header has **By kind**, **Backups kept** (`the newest 2 of each kind; the newest of each kind always stays`), **Scheduled** (`at every server start`), **Game autosaves** (the count, the size and `the game keeps <AutoSaveCount>, one every …`, matching the files in `<save>` and the server's `ServerHostSettings.json`), **Autosaved**, and **Autosaves are** / **Backups add** wrapped in full (no `…`). **Screenshot** it (focus-free capture).
9. `world_backups {"action":"open_backup_now"}`: the confirmation (Where `progenitor-backups\v4\restore1 …`, Size, Includes). `mods_ui dialog_choose {"choice":"yes"}`: the notice `Backing up`; `world_backups {"action":"read"}` shows `backups.job` while it runs, then `backups.lastBackup.ok true`. The server's `progenitor-settings.log` has two audit lines for the admin, row `progenitor/ServerRules/BackupNow` (`backup_now`, then `backup_now done <stamp>-manual`).
10. A third manual backup (step 9 again): the confirmation's **Retention** line and the header's **Next backup deletes** named the oldest manual backup before it; afterwards that folder is gone (no `.deleting-*` left), the newest two manual backups, the scheduled one and every before-restore backup remain.
11. **Restore** the oldest remaining manual backup through **Show** (steps 8-9 of the restore recipe above, `RESTORE`), restart: the result reads `Restored the manual backup from …`; the before-restore backup reads `Before restore of the manual backup from …`. With `BackupAtStart` on, this start makes **no** scheduled backup (`WorldBackup: the start backup is the restore's own backup at this start …`): `<backups>` gained exactly one folder.

**Schedule, consistency and refusals:**

12. `world_backups {"action":"set","key":"BackupEveryHours","value":"1"}` (client): the cfg has `BackupEveryHours = 1`; `schedule` gives `nextDueUtc` an hour after the newest scheduled backup (at least 10 minutes after Core loaded). Wait for it: `WorldBackup: schedule: started`, a new `<stamp>-scheduled` with `trigger interval`.
13. **An autosave mid-copy:** set the host settings' `AutoSaveInterval` to `10` (restart), then Back up now a few times: when an autosave lands during a copy the log shows `WorldBackup: the save folder changed during the copy (…); copying again (attempt 2 of 3)` and the sidecar has `attempts 2`; the backup equals `<save>` as it was after that autosave. Put `AutoSaveInterval` back.
14. **Free space** (optional; it needs `restore-save-data` on a nearly full volume, such as a small VHD): Back up now refuses with `Not enough free disk space for a backup: …`, and nothing is saved or written. The game-free tests cover it.
15. **Lock:** `[ServerSettings] LockedInGame = progenitor/ServerRules/BackupNow` (applies at once): the confirmation opens as a notice with the lock note, and the server refuses `backup_now`; with `server-rules` all five backup rows are read-only. Clear it.
16. **A failed start backup:** fill the volume or make `<backups>` read-only for the test (an ACL), restart: `WorldBackup: START BACKUP FAILED (the server starts anyway): …`, the world loads, and the list header shows **Schedule failed** in amber until a scheduled backup succeeds. Undo the ACL.

**Cleanup:** stop the headless server, set `BackupAtStart = false` and `BackupEveryHours = 0`, remove the test backups you made (the `-manual` and `-scheduled` folders), and leave the before-restore backups as they were. Disconnect the client normally and leave it at the title screen.
