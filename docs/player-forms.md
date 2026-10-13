# Player forms (experimental, uncommitted)

## Final test result — 2026-09-06 12:28 local

Normal engine mode is restored (`Burst is Enabled`). In `v3-save-122716`, requesting serialization while Styx was controlled restored the canonical vampire and refused that save while deferred body cleanup was outstanding. Before the harness's fallback return, the player already controlled the vampire; fallback found zero live leases. The former body was absent and the direct `EntityOwner` count was zero. This proves the observed barrier/cleanup path, not complete save safety for arbitrary ability descendants.

After cleanup, an explicit second request wrote `FormsActiveBarrier.save.gz`: **7,417,320 bytes**, 12:28:09 local. The first refused request was not automatically retried. Final guard readback: all five armed, `live=0`, `cleanup=0`, and normal Styx requests still refused. This newly written manual save has not been restart-tested.

The client was released after restoration; no further client operations are part of this test. Remaining blockers include the reproducible Burst-enabled melee abort, recovery-failure branches, full ability/descendant behavior, reward/progression suppression, and destruction/reload coverage. Neither the selectable candidate nor the successful diagnostic melee run is a qualified playable form.

Evidence: the normal-recovery game and framework logs (internal research notes) and `<local-evidence>\TransformationProbe\v3-save-122716` (`<local-evidence>` files are kept outside the repository; see [path placeholders](dev-loop.md#path-placeholders)).

The current working tree adds a selectable-form API under `Progenitor.Players.Forms`. It has **not landed**. Updated Bootstrap and Core have been deployed only to an isolated test fixture. Styx has been visibly controlled and returned to the canonical vampire on timed leases, but forced melee still aborts the server with Burst enabled. The native service remains under review. All normal requests currently fail: Bootstrap's qualified-profile set is empty.

`AvailableForms` currently contains one candidate: **Lord Styx**, prefab `1112948824` (`CHAR_BatVampire_VBlood`), with `Verification == PlayerFormVerification.Experimental`. Listing a candidate does not qualify it for control. `PlayerForms.IsReady` reports whether Bootstrap's recovery guards are available; it does not mean Styx or any other form is verified.

## Public surface

| Type/member | Contract |
|---|---|
| `PlayerForms.AvailableForms` | Read-only selectable candidate descriptors. |
| `PlayerForms.IsReady` | Bootstrap guard availability only; missing Bootstrap returns false. |
| `PlayerForms.TryBegin(World world, Entity user, int prefabGuid, TimeSpan duration, string owner, out PlayerFormHandle? handle, out string error)` | Request on the server game thread using the **User entity**. Duration must be positive and at most 300 seconds. Reports refusal/exception as an error. A failed request can still return a recovery handle; retain every non-null handle. |
| `PlayerFormDescriptor` | `Name`, `PrefabName`, `PrefabGuid`, and `Verification`. |
| `PlayerFormVerification` | Currently only `Experimental`; no profile is verified. |
| `PlayerFormHandle.Id` / `.State` | Read-only recovery identifier and backend status text. State text is diagnostic, not a stable enum. |
| `PlayerFormHandle.TryReturn(out string error)` | Inspectable restoration request. A false result leaves the handle retryable. |
| `PlayerFormHandle.Dispose()` | Requests restoration; failed restoration retains the identifier and permits retry. |

## Consumer shape, not a working demo

The following illustrates how a consumer would select a descriptor and track an accepted lease after qualification. **It currently takes the refusal branch.** `world`, `userEntity`, and `ctx` are supplied by the consumer's server-side command or system.

```csharp
using System;
using Progenitor.Players.Forms;

// Populate a selector from these descriptors; there is currently only Styx.
var candidates = PlayerForms.AvailableForms;
var selected = candidates[0];

bool accepted = PlayerForms.TryBegin(world, userEntity, selected.PrefabGuid,
    TimeSpan.FromSeconds(30), "example.mod", out var handle, out var error);
if (handle != null) ctx.Track(handle); // Also retain recovery from a failed begin.
if (accepted)
{
    var status = handle!.State;
    // Keep the handle and observe State on later ticks. Request acceptance can
    // mean pending native initialization; it does not prove actual activation.
}
else
{
    // Present error; retain any non-null handle for recovery. Do not bypass the gate.
}
```

For a non-null handle, retain it if explicit recovery fails. State always comes from Bootstrap, including world-ended, cleanup-pending, and unknown-record statuses; a prior successful return does not replace those with a locally invented restored state:

```csharp
if (!handle.TryReturn(out var reason))
{
    // Report reason and retain handle.Id for inspection/retry.
}
```

Bootstrap owns recovery for the process lifetime. `ctx.Track(handle)` requests normal cleanup but is not the only recovery mechanism, and disposal is not proof that native restoration succeeded. End the previous lease before requesting a different form.

## Installation and evidence limits

The guards require the updated **Bootstrap together with the Core facade**. A Core-only hot reload cannot supply process-owned guards. Updating Bootstrap requires a **server process restart**; this server-side addition does not currently require a client update/restart. Updated Bootstrap and Core are deployed only to the isolated test fixture described below.

The current focused regression run passed **78/78 tests**: 44 control policy, 5 facade, 6 LeaveGamePolicy and 23 manifest cases. These verify their specific bookkeeping, connection policy and catalog contracts; they do not qualify native abilities, save integrity or descendant cleanup. Do not enable a profile by editing `Qualified` or skipping the gate.

Remaining qualification includes ability execution and rewards, death and direct destruction, disconnect variants beyond the successful LeftGame case below, plugin/Core reload while active, pending-transfer and failed-recovery save cases beyond the successful active-save barrier above, failure injection, and linked or later-created children. Partial live successes do not close that matrix. Mechanism details are in the internal research notes.

## Isolated live evidence (2026-09-06)

The test fixture uses port `9976` and `-persistentDataPath <testserver>\forms-save-data-20260906`. Original canonical `save-data` hashes remained unchanged after the reported tests. This is a disposable fixture, not a production installation. Process IDs and entity IDs are historical observations; resolve them fresh before testing.

The initial startup probe reported all five flags (`_saveArmed`, `_disconnectArmed`, `_destroyArmed`, `_tickArmed`, `_loadArmed`) true and `IsReady=True`. A normal Styx request was refused without a lease. An ordinary named save, `FormsGuardBaseline.save.gz`, wrote 7,399,020 bytes at 11:26:39 local. Startup evidence: `<local-evidence>\TransformationProbe\guard-startup.result.json` and the guard startup log (internal research notes).

| Test | Observed result and limit |
|---|---|
| v2 Styx leases 1–3 | The client visibly controlled Styx, and timed expiry returned control to the canonical vampire. Root cleanup was observed. Screenshot: `<local-evidence>\TransformationProbe\styx-visible.png`. This does not prove all descendants were cleaned or every ability worked. |
| v2 lease 4, forced slot-0 melee | The server aborted in Burst with `AppendRemovedComponentRecordError`. Restarting the same fixture's autosave restored canonical control without copying a baseline save back. This is one observed crash/restart recovery, not a general crash-safety guarantee. |
| v3, network components preserved | The missing network-index errors disappeared, but forced melee still caused the same Burst abort. The schema fix did not solve the melee failure. |
| v3 capture/return evidence | `<local-evidence>\TransformationProbe\v3-cycle-121916` contains distinct before/after request and result files. Before-cast counter was 855; the cast request reported acceptance, but after-cast and return requests failed because the bridge was gone. No counter advance or successful post-cast return was observed. |
| Scratch catalog filter | With the updated Core, new Scratch requests no longer generated a missing-Scratch installation prompt. This tooling result does not qualify form behavior. |

Crash evidence: the v2 and v3 melee-crash Unity and BepInEx logs (internal research notes). A separate diagnostic boot confirmed `Burst is Disabled` with the one-off `--burst-disable-compilation` argument. In `<local-evidence>\TransformationProbe\v3-cycle-122153`, melee advanced counter 855 to 856, explicit return restored canonical control, and later cleanup reported root `exists=False` and `totalDirectOwned=0`; no targeted native errors appeared in the newly appended logs. This changed engine mode is diagnostic evidence, not a fix or qualification under normal settings. Direct-owned cleanup is not proof of all recursive or later-created descendants. The saved launcher was not changed. Normal engine mode was subsequently restored and the startup log confirmed `Burst is Enabled`; the disconnect result below was obtained in that normal mode.

The same-world load guard covers `PersistenceV2Test.TriggerNewLoad` and `LoadPersistenceSystemV2.ResetLoadedPersistenceForTest` / `OnUpdate`; all three hooks must be installed for load readiness. Unknown lower-level bypasses remain unverified. Saves are refused while known owned-body cleanup remains; a refused save request is **not automatically requeued**. Public `Qualified` remains empty and normal begin requests remain refused.

### Normal-mode active disconnect

`<local-evidence>\TransformationProbe\v3-disconnect-122608` records a successful active LeftGame disconnect with Burst enabled. Styx `Entity(566744:10)` was controlled before the focus-free disconnect, which verified the client reached `MainMenu`. The server log records `FORMS restored` before `PlayerDisconnected`; the first inspection, before fallback `TryReturn`, already reported canonical vampire control. The later fallback reported `leases=0`, and cleanup showed the former root absent with `totalDirectOwned=0`. This proves this disconnect path and the inspected direct-owner cleanup, not every disconnect variant or all descendants. `original-save-verification-v3.json` records 12 checked original save files, zero changed.

An active serialization test is next; no result is claimed here. Public `Qualified` remains empty.
