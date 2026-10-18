# Isolated castle qualification harness

Build from the repository root against the exact deployed Core and Dev:

```powershell
dotnet build tools/stress-probe/Progenitor.StressProbe.csproj -p:StressProbeCore=C:\VRisingMods\_testserver\server\BepInEx\progenitor-core\Progenitor.dll -p:StressProbeDev=C:\VRisingMods\_testserver\server\BepInEx\progenitor-reload\Progenitor.Dev.dll
```

Use only during an authorized, coordinated test window with a backed-up copied save.
Copy only the resulting Progenitor.StressProbe.dll into that server's
`BepInEx/progenitor-reload`. Loading without a marker reads castle state only.

For a single action, put `generate:0` or `fuel:0` in
`BepInEx/config/stress-probe-once.txt` before loading. The marker is consumed once.
Generation refuses an occupied territory. Fuel adds 100 blood essence to its heart.
The harness is restricted to territory 0 and the isolated test executable path.
Do not replace Core or Dev. Remove the harness DLL after the batch and confirm
`stress-probe: unloaded` in server logs.

Generation and fuel consumption are verified live on one plot. Ownership, clans,
individual clearing checks and reload persistence remain unqualified. See the
[knowledge note](../../knowledge/notes/castle-generator-unowned.md).
