# Model previews

Client presentation previews show a static copy of a loaded model with its original materials.
They create no gameplay entity. Camera framing fits the model's renderer bounds. Missing material
slots, pose drivers and runtime effects are not reconstructed; a preview is not proof of the
asset's appearance during gameplay.

## Plugin API

Load through `ctx.Models.Load(section, exactName)` and wait for `ModelHandle.IsReady`, then call
`ModelHandle.Preview()`. The returned `ModelPreview` implements `IDisposable`; disposing it
closes the surface. Disposing the model handle closes its previews before releasing the model's
streaming hold. Core unload also removes preview surfaces. Call from the client's main thread.

## Developer bridge

`model_preview` accepts `action: open | status | close`. `open` requires the exact `name`, a
catalog `section` (`scene:index`) for unloaded models, and optional `seconds` (default 30,
range 1–300). Omitting `section` looks for a resident prefab only. Opening replaces the previous
Dev preview. Status becomes `visible` after loading and surface creation; inspect pixels to
verify appearance. Failure reports a reason. Timeout, world change, Dev unload and Core unload
close the preview.

Generate the name-to-section table for the installed build using
`node tools/catalog/model-sections.mjs --out <output.json>` after generating the catalog inputs.
Section numbers are build-dependent. Do not substitute a similarly named asset when lookup fails.

From the repository root, with an authorized test client connected:

```powershell
$requestPath = Join-Path $env:TEMP 'progenitor-model-preview.json'
$request = @{ action='open'; name='HYB_CreatureBat_Legion_GiantBat_DraculaShadow_PLACEHOLDER'; section='ddfc6a4db4009e2448b8f8b6f0965b8f:377'; seconds=30 }
[IO.File]::WriteAllText($requestPath, ($request | ConvertTo-Json -Compress), [Text.UTF8Encoding]::new($false))
node tools/mcp/call.mjs client model_preview "@$requestPath"
[IO.File]::WriteAllText($requestPath, '{"action":"status"}', [Text.UTF8Encoding]::new($false))
node tools/mcp/call.mjs client model_preview "@$requestPath"
[IO.File]::WriteAllText($requestPath, '{"action":"close"}', [Text.UTF8Encoding]::new($false))
node tools/mcp/call.mjs client model_preview "@$requestPath"
```

The MCP equivalent is `progenitor_model_preview`; restart the MCP process after updating its tool
definitions. No mouse input is needed. Use the [focus-free capture tools](observe-capture.md) for
visual evidence. **Acceptance procedure — not run:** verify the bat is visible, then close and
verify the model, camera and surface disappear; repeat and test unload. The placeholder's name
alone does not establish a ghost appearance.
