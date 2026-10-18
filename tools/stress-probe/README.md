# Isolated castle qualification harness

Build from the repository root against the exact deployed Core and Dev:

```powershell
dotnet build tools/stress-probe/Progenitor.StressProbe.csproj -p:StressProbeCore=C:\VRisingMods\_testserver\server\BepInEx\progenitor-core\Progenitor.dll -p:StressProbeDev=C:\VRisingMods\_testserver\server\BepInEx\progenitor-reload\Progenitor.Dev.dll
```

Use only during an authorized, coordinated test window with a backed-up copied save.
Copy only the resulting Progenitor.StressProbe.dll into that server's
`BepInEx/progenitor-reload`. Loading without a marker reads castle state only.

Put one action in `BepInEx/config/stress-probe-once.txt` before loading.
The marker is consumed once; a reload without a marker is read-only.

| Action | Effect |
|---|---|
| `generate:0` | Generate plot 0; refuse an occupied territory. |
| `fuel:0` | Add 100 blood essence to plot 0's heart. |
| `generate-next` | Generate and fuel the first empty eligible territory. |
| `generate-rest` | Generate and fuel every empty eligible territory, checking each after initialization. |
| `claim:0` | Create or reuse Stress01 and claim plot 0; requires the owner build below. |
| `claim-rest` | Create or reuse 40 seed-18 offline owners, then claim generated castles in sorted round-robin order. |
| `rank5:0` | Upgrade plot 0 through the game's heart upgrade routine and fill its fuel inventory. |
| `rank5-all` | Upgrade every existing heart to rank 5 and fill its fuel inventory; needs explicit authorization for the original castles too. |

The harness checks the isolated test executable and copied Moo World save arguments.
Plots 45, 67, 86, 92, 125, 129 and 136 are protected. Every batch checks their
heart and owner identities before continuing. Generation also protects every
castle present when that batch started. Failures stop the batch; completed writes
remain in the test save. Do not use this fixed-save harness on another world.

For owner actions, add `-p:StressOwnerSource=<absolute-reviewed-owner-worktree>`
to the build command. The qualified helper source is PR #101, revision `f13f36c7a`.
It links the offline-user/team helpers into the harness. Claim plot 0 and verify
its owner and reciprocal team links before running the full claim batch. Finish
generation before `claim-rest`, so sorted distribution stays stable. Owners use
unused low User indexes, never connected-user slots, and have no characters or clans.
The native claim wrapper updates ownership and reciprocal team links; the harness
reads both back on later frames. Empty hearts are refueled before verification.

The rank-5 actions use the existing heart event system's upgrade wrapper, which
also expands the inventory slots. They are test setup actions and do not charge
player building costs. Fuel is added through the item API, never by writing fuel
counters. Later readback checks rank, positive fuel and a full-inventory refusal.
Heart and owner identities remain protected during this pass.

Wait for `stress-batch: complete`, `stress-owners: COMPLETE` or
`stress-hearts: COMPLETE` in server logs.
To run another action with the same image, remove it, verify unload, then set the
marker and copy the image back; copying an identical image alone may not reload it.
Do not replace Core or Dev. Remove the harness DLL after the batch and confirm
`stress-probe: unloaded` in server logs.

Generation, heart fueling and an offline owner claim with reciprocal team links
are verified live on the copied test save, including 140 generated castles across
40 owners. The rank-5/full-fuel pass completed on all 147 hearts, including the
seven originals. A world save was written; the temporary harness unloaded cleanly.
Clans, individual clearing checks and
save reload persistence remain unqualified. See the
[knowledge note](../../knowledge/notes/castle-generator-unowned.md).
