# Progenitor Hot Demo

The smallest plugin that can be hot loaded into a running **client**, so the Install tab's apply-now
lane can be exercised end to end with no restart and nothing published.

- `[BepInPlugin("progenitor.hotdemo", "Progenitor Hot Demo", "0.1.0")]`
- `[ProgenitorSide(Side.Both)]` — it really does load on both sides; that is why it exists beside
  `Progenitor.ReloadExample`, which is server-only and so is refused for a client target with
  `SideMismatch`.
- One log line on load and one on unload, and one owned command, `.hotdemo`, which replies
  `hotdemo alive gen=<n>`. `<n>` counts loads in this **process**, so a fresh start says `gen=1` and a
  real hot swap says `gen=2` with the client still running.

It uses no Harmony, no `ctx.UnsafeHarmony`, no `ClassInjector`, no `DllImport`, no reflection and no
dynamic loading. Each of those is a finding the offline artifact preflight refuses or calls Unknown,
so a demo that used one could never reach apply-now.

## Package it

```
pwsh tools/make-dummy-package.ps1 -Sample HotDemo -Out <folder>
```

writes `<folder>/Progenitor-HotDemo-0.1.0.zip` plus an `index.json` whose `download_url` is a
`file:///` URL. Point the client at it with

```
[Install]
LocalIndexPath = <folder>
```

in `BepInEx/config/progenitor.cfg`.

## Apply now also needs a reviewed record

A listing being present is not an apply-now install. Two more things have to hold, and both are
refusals by design when they do not:

1. A reviewed qualification record for **these exact bytes** in
   `BepInEx/config/progenitor-qualification.json`. Author one with the bridge op
   `install_local {"action":"qualify","zip":"<folder>/Progenitor-HotDemo-0.1.0.zip","target":"Client","evidence":"<where the review lives>"}`.
   A record is a review claim, never self-certification: a record shipped *inside* a package is
   ignored and reported.
2. An available Bootstrap admission snapshot — `ProcessHost.AdmissionSnapshotCapability >= 1`. An
   older Bootstrap makes every admission `Unknown`, and the install is the honest restart route.

With both in place the row reads `Install` with *no restart — loads in seconds*; confirming writes
`BepInEx/progenitor-reload/progenitor.hotdemo.dll` and the reloader loads it within a
second. See [docs/hot-reload.md](../../docs/hot-reload.md).

## Required test build (JOINFLOW4)

`dotnet build samples/Progenitor.HotDemo/Progenitor.HotDemo.csproj -c Debug --no-incremental -p:HotDemoRequired=true` builds a copy that declares `[NetworkCompatibility(ServerMustHave, Exact)]` and calls `ctx.Handshake.DeclareSelf()`, at the build's version (`-p:ProgenitorVersion=2026.10.90.1` makes a newer one). It is the required, hot-swappable mod of the "keep both versions" live recipe ([bridge-ops-join.md](../../docs/bridge-ops-join.md#join-flow-stage-4-acceptance-recipe-joinflow4)). The default build is unchanged: version 0.1.0 and no handshake row. Built; not run live.
