# Changelog

## 0.1.0

First packaged release. Milestone 1 of the framework, verified in-game with `samples/Progenitor.Sample`.

- Ships `Progenitor.Bootstrap.dll` in `BepInEx/plugins` (chainloader guid `progenitor`) and `Progenitor.dll` in `BepInEx/progenitor-core` (loaded by Bootstrap; not a chainloader plugin).
- Plugin base (`ProgenitorPlugin<TContext>`, `[ProgenitorSide]`) with unload reversing every registration on the context.
- Hot reload from `BepInEx/progenitor-reload` (`Progenitor.Bootstrap.dll` stays in `BepInEx/plugins`; `Progenitor.dll` stays in `BepInEx/progenitor-core`; both are restart-only until Core is collectible).
- Event bus: `WorldReady`, `PlayerConnected`, `PlayerDisconnected`, cancellable `ChatMessage`.
- ECS system hooks (managed and Burst) and named virtual systems (`ctx.Systems.Add`).
- Entity toolkit, chat commands, custom networking, version handshake with kick.
- Prefab registry with client sync: cloned items (icon, localized name and description), `ItemKeyLookup` alignment, soft unapply and tombstones.
- Per-mod JSON persistence next to the world save; config file-watch; `localization/en.json`.
- Hosted-game launcher implemented and fenced to the game root; **not** yet verified in a live private-game launch.
- Debug bridge (`Progenitor.Dev`) is **not** in the public zip; pass `-IncludeDev` to `tools/package.ps1` for a local package that also drops it under `BepInEx/progenitor-reload`.
