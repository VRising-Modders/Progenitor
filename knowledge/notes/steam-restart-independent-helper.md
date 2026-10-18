---
title: Steam restart helper must outlive the game independently
game: V Rising
game_build: Steam 23521359
progenitor: 15499039
side: client
area: lifecycle
tags: restart, steam, process
route: Windows PowerShell broker to Win32_Process.Create with hidden startup
status: verified-live
date: 2026-10-15
evidence: Progenitor-research/restart-independent-helper-20261015.md
---

## Goal

Restart through Steam after installing a mod, without Steam rejecting the launch as already running.

## Route

The game starts a short-lived hidden PowerShell broker. It asks Windows WMI to create the waiting helper with ShowWindow=0, reports the helper PID and exits before the game quits. The helper waits for the original process and Steam Running=0, then asks Steam to launch. No additional runtime assembly dependency.

## What the engine did

Observed: the previous direct-child helper coincided with Steam retaining the old session until the helper timeout. The precise Steam tracking mechanism was not determined. With WMI creation, the helper's parent was WmiPrvSE; Steam cleared the old game and accepted a new launch while the helper remained independent.

## Verification

Live client test on 2026-10-15, base commit above plus working changes: invoked the production LiveRelauncher from a temporary scratch plugin, removed the scratch plugin, quit normally from the title screen. Steam cleared the old session at 17:05:01, received the request at 17:05:08 and created the new game at 17:05:09. The new client bridge answered, title screen verified, helper exited. Build and 89 related tests passed. The complete install-button transaction and automatic server rejoin were not repeated in this batch.

## Gotchas

### Waiting inside the game process family can keep Steam busy

- **Symptom:** no relaunch until a helper times out, or Steam says already running.
- **Cause:** the helper was started directly by the game; a hidden window does not establish independent lifetime. A fixed delay also does not prove Steam has cleared the session.
- **Fix:** use an independent Windows-owned helper, require explicit Steam idle, and inspect real Steam process logs. Simulated registry tests alone cannot verify process ownership.

### Standard input garbles non-ASCII paths

- **Symptom:** with an install path such as `D:\Игры\...`, the helper never sees the game (its path check fails) and the log path is wrong.
- **Cause:** Windows PowerShell decodes a `-Command -` script on standard input with the console code page, not UTF-8; setting `[Console]::InputEncoding` inside the script cannot fix a line that was already decoded.
- **Fix:** pass the broker's script on its command line (Windows passes it as Unicode), build the helper's double quotes with `[char]34`, and wait for the broker off the main thread; on timeout kill the broker tree. Game-free tested 2026-10-18 with a stubbed process creation; not live-verified.