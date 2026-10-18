# Minimal reload example

One server-side command and one event subscription. Read [Plugin.cs](Plugin.cs); the context owns both registrations. The plugin creates no items, buffs, units, or saved state. It is not included in the default deployment set.

**Verification:** compile verification is recorded in the internal research notes. This new sample has not been run in V Rising. The following is its explicit live acceptance procedure, not a report that it passed. Existing plugin/Core reload evidence is linked from the [hot-reload guide](../../docs/hot-reload.md).

## Build offline

From the repository root, with the existing game interop references configured:

```powershell
dotnet build samples/Progenitor.ReloadExample/Progenitor.ReloadExample.csproj -c Debug
```

Expected: build succeeds and produces `samples/Progenitor.ReloadExample/bin/Debug/net6.0/Progenitor.ReloadExample.dll`.

## Verify in an authorized test session

Prerequisites: an isolated test server with compatible Progenitor Bootstrap/Core, the Dev bridge if using its log commands, and a real connected test player. Do not use the live server or a client the user is using to play elsewhere.

1. Complete the offline build above and stop if it fails. Deploy only its output to the server:

   ```powershell
   powershell -NoProfile -File tools/deploy.ps1 -Projects Progenitor.ReloadExample -Side server -NoBuild
   ```

2. Check the server log for `Reload example: loaded` and the reloader's image hash. In test-game chat, enter `.reloadhello`; expect one reply, `Reload example v1`.
3. Change only the reply string in `Plugin.cs` to `Reload example v2`. Build and deploy with the same commands. Expect one unload/load transition and one `v2` reply. A file copy alone is not evidence that the new code loaded.
4. Repeat with `v3`. Reconnect the test player and inspect only new log lines; expect one `Reload example: joined ...` line for that connection. This checks that reloads did not accumulate this plugin's event handlers.
5. Remove only `Progenitor.ReloadExample.dll` from the verified test server's `BepInEx/progenitor-reload` folder. Expect unload reason `Removal`; `.reloadhello` should no longer be registered, and a subsequent connection should produce no new example join line.

Build this project explicitly before every deployment: it is outside the solution, and `-Projects` controls copying, not the solution build. Record the deployed hashes, runtime/build, new log ranges, command replies and cleanup result in the external research repository. A single command reply does not prove event-subscription cleanup. Restore the source message to `v1` after testing if the exercise changes are not intended for review.

## Server-delivered settings

The example also declares two personal server-owned settings (Receive hints and Detail), and the server-wide Hint interval. Install this DLL only on the isolated server; a connected Progenitor client should see **Your settings on this server** and **Server settings** without the sample DLL. Changing a personal value writes only that account's override to `BepInEx/config/progenitor-players/progenitor.reloadexample.json`. A non-admin can edit those preferences but only read Hint interval. An admin can edit the interval within 1–120; its change uses the existing settings audit log.

Reset personal settings restores both personal defaults without changing Hint interval. Unload removes the remote controls; reload restores them with their saved values. These controls demonstrate settings storage; they do not schedule hints. [Settings API](../../docs/settings-scopes.md).
