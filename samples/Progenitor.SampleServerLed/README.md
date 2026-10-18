# Server-led sample

`Progenitor.SampleServerLed` (`progenitor.sampleserverled`) announces from the server and optionally shows those announcements as a popup on the client. It demonstrates server-wide cfg settings, persistent personal server settings, and local popup settings.

**Verification:** source-checked, Debug sample build successful, and game-free tested (20 counterpart tests and 5 replay tests). Live acceptance below has not been run. Older [message-protocol live evidence](../../docs/mod-compatibility.md#c5-live-test-2026-10-10) predates this persistence path and does not verify it.

## Behavior and storage

The server announces every **Announce minutes** (1–120, default 10). `.serverled status` reads the caller's preferences by `User.PlatformId`; `.serverled announce` lets an administrator announce immediately. No world objects are changed.

| Settings | Owner and storage | Editing |
|---|---|---|
| `Server/AnnounceMinutes`, `Server/Greeting` | Server-wide, server cfg | Administrators; bound with `ctx.Bind(..., scope: SettingScope.ServerWide, description: ...)` |
| `ForServer/ReceiveAnnouncements` (default `true`), `ForServer/Delivery` (`Chat` or `Popup`, default `Chat`) | Personal, server-owned, separately for each platform account | Core's personal settings controls on the Mods page |
| `Client/PopupSeconds`, `Client/LogAnnouncements`, `Client/JoinCheck` | Personal, local client cfg | Local popup client controls |

The server's personal overrides live in `BepInEx/config/progenitor-players/progenitor.sampleserverled.json`. Core stores only non-default overrides; resetting removes one. A plugin reload, disconnect, or world wipe does not reset them. The sample does not copy old client cfg preferences to the server: existing `[ForServer]` cfg entries are unused, and players should choose their server preferences again through Core's settings page.

The sample registers `PlayerSetting<bool>` and `PlayerSetting<string>` with `ctx.Settings.PerPlayer`. It reads both through `For(new PlayerAccountId(platformId))` when routing each announcement; no connection-slot cache, preference message, heartbeat, or unload goodbye remains. Core owns the tracked handles and persistence. A zero platform ID uses defaults without attempting a persisted lookup.

Core adds personal server rows from the joined server's manifest. The sample's explicit page contains only server-wide cfg rows and local client rows; it does not create duplicate Receive or Delivery cfg controls. Personal edits therefore also work on a scoped-settings-capable Progenitor client without this sample's client half.

## Optional popup client

- **Receive off:** the server skips this account, including after the client half unloads or the player reconnects.
- **Receive on, Delivery Chat:** the server sends a system chat line.
- **Receive on, Delivery Popup:** the server sends the `announce` message only while that client offers `popups`. If the feature is absent, or the send fails, it sends chat instead. The saved Popup preference remains unchanged.
- **New player without the sample client:** defaults to chat. Vanilla clients cannot edit preferences through Core's settings page, but saved account preferences still apply.

The client controls popup duration (0 keeps it open until OK), logging, and one availability log line per join. Popup announcements use the framework notice dialog, which takes the cursor while open.

Both halves remain optional under `[Counterpart(Optional, ClientExtras = ...)]`. The only sample-owned runtime message is `announce` (`Sequence`, `Text`); its shape and `popups` feature are unchanged. The sample no longer offers or sends `prefs`. The 1.x popup compatibility range is unchanged; personal editing requires the new Core scoped-settings protocol. Older clients do not regain preference editing through the retired custom protocol.

## Source and game-free checks

- [Plugin.cs](Plugin.cs): `PerPlayer` registration, scoped binds, platform-account reads, scheduling, and popup delivery.
- [ServerLedSettings.cs](ServerLedSettings.cs): server cfg and local popup page without duplicate personal server entries.
- [ServerLedPolicy.cs](ServerLedPolicy.cs): account-based reads, routing, schedule, and text.
- [ServerLedMessages.cs](ServerLedMessages.cs): the popup announcement only.

From the repository root:

```powershell
dotnet build samples/Progenitor.SampleServerLed -c Debug
dotnet test tests/Progenitor.Tests --filter FullyQualifiedName~CounterpartSamplesTests
dotnet test tests/Progenitor.Testing.Tests --filter FullyQualifiedName~Progenitor.SampleTests.ServerLed
```

The counterpart tests cover persisted per-account routing across store reopening, reset, unidentified accounts, cfg-page shape, and source wiring to the real API. The replay tests keep the frozen 1.0.0 fixtures; retired preference packets use a test-only historical type. Those replays do not test Core's new personal-settings protocol.

## Acceptance procedure — not run

Use an authorized isolated test server and an available client, with the new Core scoped-settings implementation on both. Follow [hot reload](../../docs/hot-reload.md) for deployment and completion checks. This sample is not in the default deployment list; select `Progenitor.SampleServerLed` explicitly.

1. Install the server half only. Join with a scoped-settings-capable Progenitor client. Expect the sample's personal Receive and Delivery settings from the manifest, without installing its popup client; a new account receives announcements as chat.
2. Turn Receive off for account A. Expect `.serverled status` to report `receive=off`, and the next announcement to skip A while account B still receives it. Verify the override under A's decimal platform ID in the server's JSON.
3. Disconnect and reconnect A, then reload the server sample using the completion checks. Expect A to remain off. Reset Receive; expect its persisted key to be removed and announcements to resume.
4. Install the optional client half, set Delivery to Popup, and announce as an administrator. Expect a popup containing the same text as a chat announcement. Turn the client half off: expect chat fallback while Delivery stays saved as Popup. Re-enable it and expect popups to resume.
5. Repeat client-half unload with Receive off. Expect no announcement; unloading must not reset Receive.
6. Confirm each personal server setting appears once and edits through Core's request/result path. Expect administrators to edit the server-wide options and non-administrators to see them read-only. Local popup controls must remain local cfg values.
7. Join a server without this sample's server half. Expect no sample announcements or personal manifest rows, and local server-dependent popup controls to be unavailable. Confirm the sample does not send a custom preference packet.

Cleanup: remove the sample from the authorized test server and client, confirm unload completion, restore the test plugin set, and disconnect normally. Back up or remove test-only personal overrides if the test installation will be reused.
