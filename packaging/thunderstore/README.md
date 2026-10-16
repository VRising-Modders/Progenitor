# Progenitor

V Rising modding framework. **One dependency: BepInEx.** Original LGPL-2.1-only code.

This is the **Client + Server** package. Install it once; the mod manager puts `Progenitor.Bootstrap.dll` in `BepInEx/plugins`, `Progenitor.Preloader.dll` in `BepInEx/patchers` and `Progenitor.dll` in `BepInEx/progenitor-core` on both sides. There is no second download. Bootstrap and Core are required: the chainloader only sees Bootstrap, which loads Core. The preloader patcher carries out the Mods menu's next-start plugin actions and keeps the per-session logs that **Get help → Collect logs** bundles.

## Players

1. Install this package on top of **BepInExPack_V_Rising**.
2. Enable the profile for the game client and for any dedicated server you run.
3. Start the game. Bootstrap loads with BepInEx under guid `progenitor`; mods that declare `[BepInDependency("progenitor")]` follow it.

This zip does **not** include `Progenitor.Dev.dll`. That debug bridge is for development machines only and must not be copied to a public server.

## Mod authors

Subclass `ProgenitorPlugin<TContext>`, register work on `ctx` (`Content`, `Commands`, `Events`, `Net`, `Handshake`, `Save`, …), and ship your plugin next to this one. Start with `PROGENITOR_GUIDE.md` (included in this package) and the repository's `dotnet new progenitor-mod` template (`templates/progenitor-mod`). API: the repository's `docs/modules.md`. Sample: `samples/Progenitor.Sample`.

## Status (0.1.0)

Milestone 1 is complete and was verified in-game (cloned item with client sync, commands, persistence, handshake, hot reload). The hosted-game launcher is in this DLL but has not been verified in a live private-game launch.
