---
title: A running title video can hang the client's quit after every plugin unloaded
game: V Rising
game_build: Steam 23521359
progenitor: 22bf979f
side: client
area: ui
tags: quit, hang, video, title-screen, restart, rejoin, shutdown, videoplayer
route: StunVideoPlayer.Stop, wrapper disabled, Object.Destroy on the VideoPlayer, two frames, then Application.Quit (TitleVideoQuit / TitleVideoLive)
status: verified-live
date: 2026-10-19
evidence: two process memory dumps of the hung Steam client (2026-10-18 and 2026-10-19) read in the internal research notes (client-quit-hang-2026-10-18); headless clients started with -noVideo never hung; bridge quit with the step on the Steam client, 2026-10-19
---

## Goal

Quit the client reliably, so Restart now and Restart and rejoin can start the game again. The relaunch helper waits for the old process to exit and gives up after about 180 s.

## Route

Before every Progenitor quit, retire the video players while frames still run: the game's own `StunVideoPlayer.Stop()`, `StunVideoPlayer.enabled = false`, `Object.Destroy` on the `UnityEngine.Video.VideoPlayer` component, then wait two normal frames (at most 0.5 s) and call `Application.Quit`.

Rejected: `Stop` or `Pause` alone (the player and its playback stay for engine cleanup, and the hang's own stack is a `Stop` inside that cleanup); disabling the component or its GameObject (the game can start it again, and the playback is kept); a Bootstrap exit watchdog that kills the process (process-lifetime behavior in every exit). A player switch that retired the title video on the title screen was dropped (the title background went black: no enabled `RawImage` draws the video).

## What the engine did

- Observed (both dumps): Progenitor and every plugin had unloaded cleanly (`unload reason=Shutdown`). The main thread was the only running thread, in native engine cleanup, with no managed frame: `PlayerCleanup`, `CleanupEngine`, `VideoPlayer::CleanupClass`, the video playback manager's destructor, `ReleaseVideoPlayback`, `StopPlayback`, `VideoClipPlayback::Stop`, then a clear job scheduled again and again.
- Observed: the manager's destructor loops while it holds a playback. A playback whose release is still pending (a pending time, cleared only by the per-frame video update or a finished job) is marked for release and kept, so the loop takes the same playback forever. The player loop no longer runs at that point, so nothing clears it. It is a livelock: about one core busy plus woken job workers.
- Observed: the only video playing was the title screen's background (`MainMenuNewView.VideoPlayer`, a `StunVideoPlayer` around a `VideoPlayer`).
- Source-checked (client native code): the title view's `Update` reads only the wrapper's plain `isPrepared` field to fade its cover, so it keeps working when the `VideoPlayer` is gone.
- Observed live (2026-10-19): the title video is not drawn by any enabled `RawImage`; with the player destroyed on the title screen the background is black.

## Verification

Live, 2026-10-19, Steam client on main plus PR 161, cold launch to the title: a bridge `quit` logged `TitleVideo: quit (bridge): retired 1 video player(s); quitting after 2 frame(s), 0 s` and the game exited normally. The hang itself has not been reproduced on demand: before the change, 0 of 20 bridge quits hung, so the live run shows the step is harmless and exits, not that it prevents the hang.

Game-free tests (`TitleVideoTests`): the two-frame and 0.5 s bound, a throwing retire and a Core unload never blocking the quit, and source pins that the install restart, the join restart and relaunch, the manual quit and the Dev bridge `quit` op all go through `TitleVideoQuit.Request`. Not live-verified: those paths other than the bridge quit. Not covered: the game's own Exit button.

## Gotchas

### The quit hangs after Progenitor has unloaded

- **Symptom:** The client stays in the task list using several cores after quitting; the BepInEx log ends with every plugin unloaded; Restart and rejoin never starts the game again.
- **Cause:** The title video's playback still has a pending release when the engine tears the video system down, and that teardown retries it forever.
- **Fix:** Retire the video players a couple of frames before `Application.Quit` (route above). A process already in this state loses nothing when it is ended: managed shutdown, EOS and Steam have finished.

### A retired title video reads as an exception

- **Symptom:** Reading `StunVideoPlayer.isPlaying` after the player was destroyed throws.
- **Cause:** The wrapper's property reads its destroyed `VideoPlayer`.
- **Fix:** Check `TitleVideoLive.IsRetired(view)` first (the Mods entry's video read does), and keep the wrapper disabled so its own `Update` never runs again.
