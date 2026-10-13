# Animated model example

**Status: under development and incomplete.** This experimental asset-import workflow has only scoped example verification. It is not a complete or production-ready third-party armor, character or creature pipeline; the limitations below still apply.

Client-only authored GLB preview. Loading the plugin registers controls; explicitly showing the preview imports the configured GLB and starts its first clip. The preview expires after 60 seconds by default. Remove, expiry and unload release its tracked model and resources. The two-joint banner is original geometry and animation generated offline; its clips are `Breeze` and `Gust`.

**Verification:** Blender 4.5.10 export verified offline. The public Core import API and this example were verified in-game with the original banner: registered controls, both clips, pause/resume, seek, rest pose, explicit removal, expiry and unload/reload.

From the repository root:

```powershell
& 'C:\Program Files\Blender Foundation\Blender 4.5\blender.exe' --background --factory-startup --python-exit-code 1 --python tools/asset-build/blender_animated.py -- --example --output C:\Assets\animated-banner.glb
dotnet build samples/Progenitor.AnimatedModelExample/Progenitor.AnimatedModelExample.csproj
```

The output GLB path must not already exist. To export your saved scene instead, replace `--example` with `--blend C:\Assets\banner.blend --objects 'BannerRig,BannerCloth'`. Include the meshes, their armature and every object ancestor; selected meshes may have at most four joint influences per vertex and only armature modifiers. Shape keys are refused. Export samples animation to LINEAR node translation/rotation/scale channels; unsupported interpolation, required extensions or nonembedded images cause refusal and removal of the new output. Always validate the exported file with the current runtime parser before live use.

Configure `BepInEx/config/progenitor.animatedmodel.example.cfg` on an explicitly authorized, available test client:

```ini
[Model]
Path = C:\Assets\animated-banner.glb
LifetimeSeconds = 60

[Placement]
X = 0
Y = 0
Z = 0
Scale = 1
```

Use a known visible world position. This sample is built explicitly, outside the default solution/deployment set. Deploy only after authorization and follow [hot reload](../../docs/hot-reload.md) completion checks.

| Default chord | Action |
| --- | --- |
| Ctrl+Alt+Shift+F6 | Show/remove preview |
| Ctrl+Alt+Shift+F7 | Play next clip from its start |
| Ctrl+Alt+Shift+F8 | Pause/resume |
| Ctrl+Alt+Shift+F9 | Seek current clip to 0.5 seconds |
| Ctrl+Alt+Shift+F11 | Stop and restore rest pose |

These controls appear in Keys and can be remapped. Resume after Stop does not select a clip; use NextClip to start playback again. Expiry remains active while playback is paused or stopped. The controller uses this GLB's authored joints, without native Animator retargeting. This client visual creates no gameplay entity or collisions.

For authorized live acceptance, inspect both authored clips, paused pose, resumed motion, seek and rest pose; then inspect removal, expiry, plugin reload and unload. Record actual observations separately from import logs. Remove the sample DLL afterward and confirm unload and visual cleanup; follow the repository's test-client cleanup rules.
