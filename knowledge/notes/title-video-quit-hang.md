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
evidence: two process memory dumps of the hung Steam client (2026-10-18 and 2026-10-19) read in the internal research notes (client-quit-hang-2026-10-18); headless clients started with -noVideo never hung
---

## Goal

Quit the client reliably, so Restart now and Restart and rejoin can start the game again. The relaunch helper waits for the old process to exit and gives up after about 180 s.

## Route

Before every Progenitor quit, retire the video players while frames still run: the game's own `StunVideoPlayer.Stop()`, `StunVideoPlayer.enabled = false`, `Object.Destroy` on the `UnityEngine.Video.VideoPlayer` component, then wait two normal frames (at most 0.5 s) and call `Application.Quit`. The player setting `[TitleScreen] Video` off does the same on the title screen, with a GPU copy of the frame as a still picture.

Rejected: `Stop` or `Pause` alone (the player and its playback stay for engine cleanup, and the hang's own stack is a `Stop` inside that cleanup); disabling the component or its GameObject (the game can start it again, and the playback is kept); destroying the GameObject (also takes the picture); a Bootstrap exit watchdog that kills the process (process-lifetime behavior in every exit).

## What the engine did

- Observed (both dumps): Progenitor and every plugin had unloaded cleanly (`unload reason=Shutdown`). The main thread was the only running thread, in native engine cleanup, with no managed frame: `PlayerCleanup`, `CleanupEngine`, `VideoPlayer::CleanupClass`, the video playback manager's destructor, `ReleaseVideoPlayback`, `StopPlayback`, `VideoClipPlayback::Stop`, then a clear job scheduled again and again.
- Observed: the manager's destructor loops while it holds a playback. A playback whose release is still pending (a pending time, cleared only by the per-frame video update or a finished job) is marked for release and kept, so the loop takes the same playback forever. The player loop no longer runs at that point, so nothing clears it. It is a livelock, about one core busy plus woken job workers.
- Observed: the only video playing was the title screen's background (`MainMenuNewView.VideoPlayer`, a `StunVideoPlayer` around a `VideoPlayer`).
- Source-checked (client native code): the title view's `Update` reads only the wrapper's plain `isPrepared` field to fade its cover, so it keeps working when the `VideoPlayer` is gone. Deactivating the title view's root earlier stopped the video and reactivation did not play it again (live, an earlier generation), so the game plays it once, from the view's start.

## Verification

Game-free tests (`TitleVideoTests`): the retire decision, the switch, the two-frame and 0.5 s bound, a throwing retire and a Core unload never blocking the quit, and source pins that the install restart, the join restart and relaunch, the manual quit and the Dev bridge `quit` op all go through `TitleVideoQuit.Request`. `dotnet build` and `dotnet test` pass.

Not live-verified: that destroying the player releases the playback before the quit, that a quit then exits, and the still picture. The hang itself was seen twice and not reproduced on demand; a normal title quit usually exits.

## Gotchas

### The quit hangs after Progenitor has unloaded

- **Symptom:** The client stays in the task list using several cores after quitting; the BepInEx log ends with every plugin unloaded; Restart and rejoin never starts the game again.
- **Cause:** The title video's playback still has a pending release when the engine tears the video system down, and that teardown retries it forever.
- **Fix:** Retire the video players a couple of frames before `Application.Quit` (route above). A process already in this state loses nothing when it is ended: managed shutdown, EOS and Steam have finished.

### A retired title video reads as an exception

- **Symptom:** Reading `StunVideoPlayer.isPlaying` after the player was destroyed throws.
- **Cause:** The wrapper's property reads its destroyed `VideoPlayer`.
- **Fix:** Check `TitleVideoLive.IsRetired(view)` first (the Mods entry's video read does), and keep the wrapper disabled so its own `Update` never runs again.
