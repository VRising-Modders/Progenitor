# Castle blueprint investigation

Read-only planning tools, not a player construction API. The exporter reads the running server's heart level and configured floor/height limits. The planner preserves existing structures, screens uneven ground, limits new floor blocks by both remaining allowance and requested coverage, and selects complete rectangular room candidates.

An entrance must be supplied explicitly. The planner checks connections on its sampled ground grid; this does not prove that an approach from outside the plot is accessible. It does not reproduce the game's full placement, clearance, research, material-payment, or roof rules. Every output has `constructionReady: false`.

## Offline

```powershell
node --test tools/castle-blueprints/planner.test.mjs
node tools/castle-blueprints/preview.mjs <snapshot-folder> <preview.html>
node tools/castle-blueprints/compare.mjs <snapshot-folder> <comparison.json>
```

Open the resulting HTML. Choose a plot and coverage, then click free ground to select a candidate entrance. Shift-click additional access blocks to preserve their connection. The hypothetical-empty-plot option changes only the preview input; it never clears the world. Exported candidate plans retain their unverified checks.

Comparison runs preserve-existing and hypothetical-empty scenarios at 25%, 50%, and 75% coverage. Its boundary anchors are comparison inputs, not verified entrances. Hypothetical exports retain the original floor usage and explicitly identify the assumed removal of existing construction.

Coverage is a ceiling on eligible free ground blocks reachable from the supplied entrance, after explicit access reservations. Existing floors on every storey count against the running heart allowance. Ground coverage and total floor allowance are separate quantities. Excess capacity is left unused when another complete room does not fit.

## Read a test server

Follow the repository's test-instance and client-availability rules. Coordinate deployments and back up the copied save before any later construction experiment. This exporter itself does not change the world.

1. Build from the repository root:

   ```powershell
   dotnet build tools/castle-blueprints/Probe/Progenitor.CastleBlueprintProbe.csproj -p:BlueprintCore=<reviewed-target-Core-DLL>
   ```

2. Put an array of one to sixteen heart-bearing territory indices in the test server's `BepInEx/config/castle-blueprint-read.json`.
3. Deploy only `Progenitor.CastleBlueprintProbe.dll` to that server's `BepInEx/progenitor-reload` using the normal settled-file reload procedure.
4. Confirm `blueprint-probe: complete` in the log. Snapshots are written to `BepInEx/castle-blueprint-read/plot-<index>.json`, one plot per half-second.
5. Copy snapshots to the external research directory, remove the probe DLL and request file, and confirm `blueprint-probe: unloaded`. Do not leave the harness on the server.

Snapshots are not atomic across plots. Roof-component presence alone is not roof proof; the snapshot also records the game's room-level `HasRoof` and `IsMissingWalls`. Ground is sampled at five positions per block, not every collision tile. Existing building bounds conservatively reserve ground below upper-storey structures. Unknown capacity refuses a plan instead of substituting a default.

The heart block is reserved separately because it need not appear among heart-connected buildings. Older snapshots without its position are explicitly marked incomplete.

For a bounded obstacle census, use `castle-blueprint-audit.json` instead of the plot request, with block coordinates `x`, `y`, `width`, and `depth` (dimensions 1–4). This writes `area-audit.json` with terrain samples and overlapping model bounds. An audit request takes precedence over a plot request; remove it after the batch. Walkability is not checked. Bounds do not prove collision or an accessible interaction position.
