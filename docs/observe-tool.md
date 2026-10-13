# Watching the client: screenshots and synthetic input (`tools/observe.ps1`)

Part of the [development loop](dev-loop.md). The focus-free capture rules are in [observe-capture.md](observe-capture.md).

`tools/observe.ps1` captures the client window and can send explicitly authorized keyboard/mouse input.

Use [observe-capture.md](observe-capture.md) for the small focus-free screenshot recipe and current capture defaults. A screenshot verifies visible behavior that logs cannot establish.

> ### ⚠ INPUT TAKES OVER THE REAL KEYBOARD AND MOUSE, AND STEALS FOCUS
>
> `key`, `type`, `move`, `click` and `scroll` go through Win32 `SendInput`. There is no per-window delivery — events go to whatever window has focus — so the tool **brings the target window to the foreground first**, which **takes focus away from whatever the user is doing**, and it **moves the mouse pointer**. Anything the user types during a send interleaves with ours.
>
> **Do not run an input subcommand while the user is at the machine doing something else.** Say so before you run one. `-DryRun` (alias `-WhatIf`) prints the exact `SendInput` batch and sends nothing.
>
> `shot` sends nothing and steals nothing. It is safe at any time.

## Why a host-side script and not a Dev bridge op

1. Capture remains available when the mod or bridge cannot load.
2. Capture runs outside the game process and requires no DLL deployment.
3. OS input, when authorized, exercises the player's input path. In-process test calls do not establish that keyboard or mouse routing works.

## Subcommands

Thirteen, exactly (the script's `ValidateSet`, all lower-case): `windows` · `shot` · `key` · `type` · `move` · `click` · `scroll` · `selftest` · `queue` · `drain` · `queue-status` · `queue-cancel` · `queue-selftest`. The default with no argument is `windows`. The chord or text argument is positional (position 1; aliases `-Keys`, `-Text`).

| Command | What it does |
|---|---|
| `windows` | List visible windows; then dump the resolved target's geometry, foreground/minimised state, session lock state and occlusion. Sends nothing, refuses nothing — it prints `session: *** LOCKED ***` rather than failing, and a `-Window` that does not resolve (the game is not running) is a `target … -> NOT RESOLVED:` line, **still exit 0**. A misspelled `-Window` is still exit 1. Start here. |
| `shot` | Capture the target to a PNG. `-Out <path>` (default `<workspace>\_observe\shot-<stamp>.png`, stamp `yyyyMMdd-HHmmss-fff`), `-Region window\|client\|monitor\|virtual` (default `window`), `-Method Auto\|Screen\|PrintWindow` (default `PrintWindow`). `-MaxWidth` bounds output width (0 preserves source size), with blank detection on the source image. Blank-check knobs: `-MinStdDev` (2.0), `-MaxDominantShare` (0.98), `-NoBlankCheck`. Screen-path overrides: `-AllowOccluded`, `-AllowLockedSession`. |
| `key <chord>` | `F10`, `"Ctrl+F9"`, `"F10,Escape"` (comma-separated sequence; `+` joins modifiers, and the last token must be the non-modifier key). Modifiers `ctrl shift alt win`; names `f1`-`f24`, `a`-`z`, `0`-`9`, `numpad0`-`9`, `esc enter space tab backspace delete insert home end pgup pgdn`, the arrows, and `tilde minus equals brackets semicolon quote comma period slash`. **The plus key itself**: a `+` with a name before it is the separator, a `+` with nothing before it is the key — so `"+"`, `"Ctrl++"` and `"Ctrl+Shift++"` all send it (as do the names `plus` and `equals`). A trailing separator (`"Ctrl+"`) is a usage error, not an implied plus. Timing: `-HoldMs` (45), `-RepeatCount` (1), `-GapMs` (120). |
| `type "<text>"` | Types literal text with `KEYEVENTF_UNICODE` (layout-independent). Good for chat commands: `type ".debug on"`. An empty string is a legal no-op. |
| `move` | `-X -Y` (client-relative by default, `-ScreenCoords` for absolute) or `-Dx -Dy` for a **relative** delta. One or the other is required. |
| `click` | `-X -Y -Button left\|right\|middle -Clicks n`. Moves first if a point is given; with no point it clicks wherever the pointer already is. |
| `scroll` | `-Delta <notches>`, required and non-zero (negative = toward the user), `-Horizontal` for a tilt wheel. |
| `selftest` | Creates its own WinForms window, drives it, and reads back what it actually received. Proves the input path against something that is **not** the game. Refuses with exit 3 on a locked machine and tells you to use `-Offline`; refuses `-DryRun` with exit 1, because a self-test that sends nothing asserts nothing. Its PASSED line counts the checks that ran, and its blank check applies **both** halves of the tool's definition of blank. |
| `selftest -Offline` | Tests input layout, chord parsing, capture scaling and fail-closed queue/lock decisions without desktop input. A window fence prevents resolving targets. Read the actual test result/count; these checks do not establish live input delivery. |
| `queue` | Write down an ordered batch of the actions above, to be run when the session can take it. `-Steps 'key F10; shot; key Escape; shot'`. A **capture-only** batch is not queued at all — it runs immediately, lock or no lock. See [the queue](#the-queue-so-nothing-waits-on-a-human). |
| `drain` | Run what is queued: now if the session is usable, otherwise wait, polling, for the lock to clear. Safe to leave running unattended. One drain at a time. |
| `queue-status` | Read-only queue/result and drain-lock inventory. Reports `RUNNING` only for a verified sharing violation; unreadable lock state is explicit. |
| `queue-cancel` | `-BatchId <id>` or `-All`. Marks *pending* batches canceled. Deletes nothing — the files stay as evidence. |
| `queue-selftest` | Proves the queue end to end against the scratch WinForms window. Never touches the game. |

Common to every subcommand: `-Window` selects the target — `client` (default, `VRising.exe` under the game dir), `foreground`, `hwnd:<n>` (decimal or `0x`), `proc:<name>`, `title:<regex>`; `-GameDir` overrides the game path used both to find the client and to decide whether the target *is* the game; `-ProcessId` disambiguates when `tools/extra-client.ps1` has more than one client running (without it, two clients is exit 2 listing the pids); `-Json` adds one compact JSON line on stdout, on success **and** on every refusal.

Common to every input subcommand (`key`, `type`, `move`, `click`, `scroll`): `-AllowGameInput`, `-DryRun` (alias `-WhatIf` — an alias, not `SupportsShouldProcess`), `-NoFocus`, `-SettleMs` (250), `-DelayMs` (0).

## The loop this exists for

```
powershell -NoProfile -ExecutionPolicy Bypass -File tools/observe.ps1 shot -Out <workspace>\_observe\before.png
powershell -NoProfile -ExecutionPolicy Bypass -File tools/observe.ps1 key F10 -AllowGameInput
powershell -NoProfile -ExecutionPolicy Bypass -File tools/observe.ps1 shot -Out <workspace>\_observe\after-f10.png
```

`-AllowGameInput` is required for input that resolves to the client, and only for that. Without it the command refuses with exit 3. The gate is there because a stray keystroke into a live session is a real action in a real world with a real character in it — a `.` opens chat, `Escape` opens a menu, a click swings a weapon.

Check whether one key press opens the panel and the next closes it:

```
tools/observe.ps1 key F10 -AllowGameInput
tools/observe.ps1 shot -Out <workspace>\_observe\press1.png
tools/observe.ps1 key F10 -AllowGameInput
tools/observe.ps1 shot -Out <workspace>\_observe\press2.png
```

If `press1.png` shows the panel, one press is enough. Pair it with `progenitor_logs` for the `Ui: show`/`Ui: hide` lines and the whole diagnosis is one unattended batch.

### UI acceptance checks

For authorized UI tests, press the key, capture the result, then read logs. The screenshot establishes visible behavior; logs explain the path and layout values.

Inspect geometry in the correct coordinate space: UI layout logs can use canvas design units, while screenshots use device pixels. Verify clipping, scrolling and overlap visually instead of comparing incompatible coordinates.

Three panel checks worth running as one batch, because each needs a state the previous one sets up:

```
tools/observe.ps1 key F10 -AllowGameInput          # settings panel: does one press do it?
tools/observe.ps1 shot -Out <workspace>\_observe\p1-settings.png
tools/observe.ps1 key F9  -AllowGameInput          # a second panel: do they tile, or overlap?
tools/observe.ps1 shot -Out <workspace>\_observe\p2-tiled.png
tools/observe.ps1 key Escape -AllowGameInput       # does it close the panel and NOT open the game menu?
tools/observe.ps1 shot -Out <workspace>\_observe\p3-after-escape.png
```

Read `p2-tiled.png` against the layout lines: two panels should sit side by side in two columns with neither title inside the other's 72-unit header band. `p3-after-escape.png` should show one fewer panel and no vanilla escape menu. Those, plus "a panel that fits shows no scrollbar" and "a toggle row with a four-line caption does not draw over the row beneath it", are the live checks the UI toolkit currently owes (`docs/roadmap.md` M3.1).

**The input batch needs an unlocked session.** Capture-only steps can still run while locked; do not infer permission to send input from successful capture.

## Screenshot method, and the black-PNG problem

`PrintWindow` with `PW_RENDERFULLCONTENT` is the default focus-free capture method. It requests the target window's content directly. Rendering support depends on the window; use the pixel-quality result and view the returned image.

`-Method Auto` is an explicit fallback option: it tries PrintWindow, then Screen if that attempt throws or produces a blank frame. The Screen fallback is subject to desktop/lock/occlusion guards and is reported in the result. The default `PrintWindow` has no desktop fallback.

`Auto` only reaches PrintWindow for `-Region window` and `-Region client`. `monitor` and `virtual` are not one window, so `Auto` uses `Screen` for them, and asking for `-Method PrintWindow` with either is a usage error (exit 1) rather than a silent substitution. There is no fallback under an **explicit** `-Method PrintWindow`: a `PrintWindow returned false (win32 error N)` is exit 1, because you asked for that method and got no picture.

`-Method Screen` is `Graphics.CopyFromScreen` over the window's screen rectangle — a BitBlt out of the desktop, so it is the truth about what is on the glass, **including anything drawn over the game**. Use it when that is the question. Its cost is that the pixels must **be** on the glass, so the tool checks first and refuses rather than saving a picture of whatever was on top:

- **minimized** → exit 4;
- **entirely off the virtual desktop** → exit 4;
- **occluded** → exit 4, naming the blocking windows and the covered fraction. `-AllowOccluded` captures anyway with a warning.
- **session locked** → exit 4 (see below).

Those four guards apply to `Screen` only. `Auto` reaching PrintWindow skips them by construction.

Capture quality uses luminance standard deviation, distinct colors and dominant-color share sampled from the source image. A frame fails with exit 5 when `stddev < MinStdDev` (default 2.0) or `dominantShare >= MaxDominantShare` (default 0.98). The PNG is still written for inspection. `-NoBlankCheck` accepts that image but does not establish that it shows the game.

**Blank detection reports the failed check**, such as low standard deviation or dominant-color concentration. It reports measured desktop occlusion, lock or minimization only when relevant to the selected method. A failed `PrintWindow` frame does not prove another app obscured the game; inspect the PNG and method result before diagnosing a cause.

Desktop occlusion uses a Z-order walk and skips DWM-cloaked windows. It is an estimate of visible overlap, not proof that a `PrintWindow` frame is blank.

## The locked-workstation rule

**Locked sessions block input and ordinary desktop capture.** The lock screen can occupy the desktop while the game continues rendering a window surface; `PrintWindow` can still produce a usable frame.

- `shot -Method Screen` refuses a locked desktop with exit 4. `-AllowLockedSession` explicitly captures that desktop, which may be the lock screen.
- `shot -Method PrintWindow` is not gated on lock state. Inspect its quality result and image; a usable window frame does not imply the session can receive input.
- Input subcommands refuse a locked/unknown session with exit 3 and offer no lock override.
- `-DryRun` and `selftest -Offline` can run while locked.

The lock probe checks `LogonUI.exe` in the caller's session. Unknown probe results are treated as locked; do not replace that result with a successful `OpenInputDesktop` call.

**Unknown lock state refuses input.** If the lock probe fails, the script treats the session as locked and prints `lock state UNKNOWN — treating as LOCKED` with the cause. It must not infer an unlocked session from an unreadable process list. Offline tests exercise that decision without using the desktop.

`selftest -Offline` substitutes unreadable and empty lock probes and requires an unknown/locked result. It also prevents target-window resolution so the check can run without interacting with the client.

## The queue, so nothing waits on a human

The queue retains an authorized input batch while the session is locked, subject to its expiry and target-identity checks. A later drain can execute it after the session unlocks.

`queue` writes the batch down. `drain` runs it when the session can take it.

```
powershell -NoProfile -ExecutionPolicy Bypass -File tools\observe.ps1 queue -Steps 'key F10; shot; key Escape; shot' -AllowGameInput -Label f10-toggle
powershell -NoProfile -ExecutionPolicy Bypass -File tools\observe.ps1 drain
```

The first command returns immediately whatever the machine is doing. The second runs the batch now if the session is usable, and otherwise **waits** — polling every `-PollSeconds` (default 15) — and runs it the moment the lock clears. Leave it running; it is built to sit there for hours.

> #### The security boundary
>
> There is **no mechanism here for delivering input to a locked session**, and there must never be one: no desktop switching, no `PostMessage`/`SendMessage` synthesis, no auto-unlock, no credential handling. A queued input batch **waits**. The drain reaches the refusal in `Enter-InputMode` (exit 3, no override) through *exactly the same code path* a hand-typed command does, because it runs each step by spawning `observe.ps1`.
>
> That refusal costs nothing real. Unity's Input System reads **raw input**, which Windows delivers to the foreground window on the **active** desktop. Input synthesized at a locked session would not drive the game even if the tool were willing to try.

### Captures are not gated on the lock — that is the whole point

A batch whose steps are **all** non-input (`shot`, `wait`) is a *capture-only* batch and **runs immediately**, regardless of the lock, both at queue time and on every pass of a drain. Only a batch containing a real input step waits.

```
tools\observe.ps1 queue -Steps 'shot; wait 2000; shot'      # runs now, even on a locked machine
```

Capture-only batches can run without an unlocked session. Input-bearing batches wait for unlock and still require the input authorization flag. Do not use lock overrides to deliver queued input.

The split is per **batch**, not per step, and that is deliberate: "press F10, *then* screenshot" is one question, and running the screenshot half early would answer a different one. A batch is ordered and atomic in intent.

A `-DryRun` batch also runs immediately. It contains input steps but sends none of them — `Enter-InputMode` returns before the lock check, the focus grab and the `SendInput` call — so holding it back would protect nothing. It is the safe way to rehearse an input batch against the live client.

### Steps

Steps are separated by `;`. Each is an action plus `name=value` pairs; `key`, `type` and `wait` also take a single bare first argument. `label=<name>` names a step (it becomes the screenshot filename and the key you match a result back by); unlabeled steps get `NN-<action>`.

| Action | Arguments |
|---|---|
| `key` | `value` (bare), `repeatcount`, `holdms`, `gapms`, `settlems`, `delayms`, `nofocus` |
| `type` | `value` (bare), `settlems`, `delayms`, `nofocus` |
| `move` | `x`, `y`, `dx`, `dy`, `screencoords`, `settlems`, `delayms`, `nofocus` |
| `click` | `x`, `y`, `button`, `clicks`, `screencoords`, `settlems`, `delayms`, `nofocus` |
| `scroll` | `delta`, `horizontal`, `x`, `y`, `screencoords`, `settlems`, `delayms`, `nofocus` |
| `shot` | `out`, `region`, `method`, `minstddev`, `maxdominantshare`, `allowoccluded`, `allowlockedsession`, `noblankcheck` |
| `wait` | `ms` (bare), 0–600000 |

An unknown action or an unknown argument is a **queue-time** error (exit 1), not a surprise at 3 a.m. A double quote in any step argument is refused, because step arguments are spliced onto a child process command line and quoting them almost-correctly is how that goes wrong quietly. A `-Steps` string beginning `[` is read as JSON instead; `-StepsFile <path>` reads either form from a file.

`-Window` is a property of the **batch**, not of a step, which is what makes the staleness check below mean something.

### Results, and where to find them

Everything lands under `-QueueRoot`, default `<workspace>\_observe\queue`:

| Path | What |
|---|---|
| `batches\<batchId>.json` | **The batch and its result in one file.** Steps as queued, target identity, and per-step: what was sent, the exact child command line, start/finish timestamps, duration, exit code, and for a `shot` the file path plus width, height, mean, stddev, unique colors, dominant color, `blank`, and byte size. |
| `shots\<batchId>\<label>.png` | Captures, unless the step named its own `out=`. |
| `logs\<batchId>.clientlog.txt` | The client log lines the batch spanned, already sliced out. |
| `latest.json` | Most recent batch written to a terminal `done`, `failed`, `refused`, `expired` or `aborted` state. Startup repair of an older `running` record to `interrupted` does not replace it. |
| `drain.log` | Append-only narration of every drain, with timestamps and pids. |

A batch result is written whole to a PID-qualified temporary file and then moved over its target. This keeps interrupted or concurrent writes from publishing partial JSON.

Each batch records the client log line range it spanned (`clientLog.lineBefore` / `lineAfter` / `newLines`), so you can read what the game did in response without re-deriving which lines were new. The log is read with a share mode that includes `Delete`, so a roll underneath the batch does not fail the read. The log defaults to `<GameDir>\BepInEx\LogOutput.log`; `-ClientLogPath` overrides it.

`clientLog.rolled` is `true`, `false` or `null` when rollover cannot be established. The start/end stamp includes line count, file identity and prefix evidence so equal or larger post-roll files are not treated as a continuation. An unreadable endpoint stays unknown. Preserve the reported range and rollover status with the result.

`result.sessionLocked` records the state before the first step, next to `startedAt`. It describes the start of execution, not a later lock state.

### Saying no: stale batches

A batch records the **pid and the process start time** of the target, plus a wall-clock expiry. `pid` alone is not an identity — Windows reuses pids, and a client that crashed and relaunched can land on the same number. Two different checks run, on two different schedules, and it is worth knowing which is which. It refuses (exit 8) when:

| # | Refusal | Checked |
|---|---|---|
| 1 | the target process has **exited** | before waiting, on every poll while waiting, and immediately before the first step |
| 2 | the pid is alive but the start time differs — a **different process** | same |
| 3 | the pid is alive but its start time **cannot be read**, so it cannot be confirmed as the same process | same |
| 4 | the batch carries **no** recorded start time at all | same |
| 5 | the `-Window` spec no longer resolves to a window, or resolves to a different pid | **once only**, immediately before the first step |
| 6 | the batch is past `-TtlMinutes` (default 240, four hours), or its expiry field cannot be parsed | before waiting and on every poll while waiting |
| 7 | the drain has waited past `-MaxWaitMinutes` (default 720) with the session still locked | on every poll while waiting |

Rows 1–4 are `Get-BatchStaleReason`, which reads `Get-Process -Id` and compares start time and name; it does not resolve the window spec. Row 5 is `Resolve-BatchTarget`, and it runs once, at the top of `Invoke-QueueBatch` — **not** during a wait that can last twelve hours. So a batch held overnight can have its `-Window` spec drift to a second window of the same process and not be told until the moment before it fires, which is where it is caught. That ordering is deliberate: resolving a window spec means enumerating windows, and the pid pair is the identity that matters. Do not read row 5 as a continuous guard.

A restarted client is a **different world**: the character is somewhere else, the panels are closed, the mod may be a different build. Firing a batch into it produces a confident, wrong answer, which is worse than no answer.

**Target identity checks fail closed.** A batch must match the resolved window, PID and process start time. Missing or unreadable start time refuses the batch at queue time or drain time; it never authorizes input to an unidentified process.

**Target refusal affects one batch.** A missing, ambiguous or changed target marks that batch stale (exit 8) and the drain proceeds to remaining batches. Standalone input/capture commands retain their ordinary target-not-found exit behavior.

Every step of a batch is driven at the **window handle** the drain just verified belongs to the verified pid (`-Window hwnd:0x…`), so a batch cannot drift onto a second window of a different process between steps.

### Running unattended

- **One executor.** Drain and inline queue execution share `drain.lock`. A second executor refuses with exit 7. If a live drain is established, inline queue work stays pending for it; if lock ownership is unknown, the result stays pending and reports `runsUnattended:false`. The lock holder is reported by PID and start time.
- **Bounded polling.** `-PollSeconds` must be 1–3600. Waiting sleeps in one-second slices; it does not busy-loop.
- **Stopping.** A stop file ends the drain with exit 9 and leaves unstarted work pending. Interruption inside a batch can leave it `aborted`; a later drain marks abandoned `running` work `interrupted`. Neither is automatically replayed.
- **Failures.** A failed step stops its batch (exit 10) unless `-ContinueOnError`. `-StepTimeoutSeconds` defaults to 120 and terminates a hung step. An error in one batch does not prevent the drain from checking later batches.
- **Evidence.** An aborted record states whether its step loop began and records a known gate refusal when available. It must not invent whether an unknown unwind was Ctrl-C, exit or a pipeline stop. `queue-status` and `latest.json` report terminal outcomes.

### How a step is executed, and what that costs

By spawning `tools/observe.ps1` as a child process with `-Json`, not by calling its functions in-process. That costs about a second of `Add-Type` per step. It buys the thing worth having: a queued step goes through byte-for-byte the same gates, refusals, blank checks and JSON shape as the same command typed by hand. There is no second implementation of the input path to keep in step with the first, and no way for the queue to become a hole in a gate.

`-AllowGameInput` is recorded **at queue time**, by the human writing the batch, and is only then passed to the child. An unattended drain is never the thing that decides it may touch a live world.

### `-PretendLocked`

A test hook, and read which direction it points. It makes `Test-SessionLocked` answer **true** when the session is in fact unlocked, so the waiting and refusal behavior can be exercised without locking anyone's machine. It is OR-ed with the real signal, so it can only ever produce **more** refusal, never less. There is no switch anywhere in this tool that does the reverse, and there must never be one. The in-process companion is `$script:LockProbe`, which `selftest -Offline` swaps for a probe that cannot answer to prove the guard fails closed — a script variable, not a switch, for the same reason.

## Exit codes

| Code | Meaning |
|---|---|
| 0 | success |
| 1 | usage or unexpected error — including `selftest -DryRun`, which is refused rather than silently ignored, and `-PollSeconds` outside 1–3600, which is a parameter-binding error before anything runs |
| 2 | Target window not found or no longer a window. Drain records a stale batch as exit 8; `windows` reports an unresolved target with exit 0. Invalid selector syntax remains exit 1. |
| 3 | refused by a safety gate: game input without `-AllowGameInput`, input while locked, or `selftest` (without `-Offline`) while locked |
| 4 | the window could not be made ready: locked, minimized, off-screen, occluded, or focus was requested and did not arrive (input is **not** sent in that case) |
| 5 | the capture came back blank / near-uniform |
| 6 | a self-test assertion failed |
| 7 | `drain`: another executor already holds the queue lock — it would double-run the queue. Also when the lock cannot be taken *and* it cannot be established that anything is holding it: the drain refuses rather than running the queue a second time on an assumption. |
| 8 | a batch was refused as **stale** (target gone; pid alive but a different process; the target's start time missing or unreadable, so its identity cannot be established; the `-Window` spec no longer resolving) or **expired** (past `-TtlMinutes`, or past `-MaxWaitMinutes` with the session still locked). That batch is finished with; it is not owed any more. Mostly `drain`, but **`queue` also returns 8** when it cannot read the target's start time, because a batch it cannot pin would be refused at every drain anyway and saying so now is more use than saying it at 3 a.m. |
| 9 | `drain`: **stopped** with work still pending — the stop file. The batch is left `pending`, so a later drain picks it up. |
| 10 | `drain`: a queued step failed; that step's own exit code is in the batch's result file |

If a drain hits several of these, the one it returns is the worst: 9 beats 10 beats 8. They need different things done about them and the caller sees one number.

**Drain counts measure attempted work.** `ran` counts batches that entered the step loop; `failed` counts failed steps or errors; `refused` counts stale/expired batches rejected before steps. They need not sum to batches taken: a pre-step exception is failed but not ran, while a stale batch is refused only.

Every failure prints one `observe:...` line to stderr and exits non-zero, so it is safe to drive unattended. `-Json` adds a single-line JSON object on stdout for a machine caller, on success and on refusals alike.

**One documented exit code the script does not actually produce.** The header lists "a path under the live server" under exit 3, but the guard is `Assert-NotLiveServerPath` in `tools/common/Fence.ps1`, called unguarded from `observe.ps1`; it **throws**, and with `$ErrorActionPreference = "Stop"` and no `try`/`catch` around it that surfaces as an uncaught PowerShell terminating error — exit **1**, with a red exception rather than an `observe: ` line. The refusal still happens, which is what matters; only its shape is wrong. Read from the source, not measured — do not build a caller that branches on exit 3 for this case.

If focus is requested and does not actually arrive within 3 s, the tool **refuses to send** rather than firing the batch at whatever window does have focus — and it re-checks after the settle delay, because a launcher stealing focus back is exactly the case that would otherwise type into the user's window.

## Files

- `tools/observe.ps1` — the tool.
- `tools/observe/Observe.Native.cs` — the Win32 interop, compiled at run time by `Add-Type`. Not part of the solution and never built by `dotnet build`. **C# 5 only** (Windows PowerShell 5.1's `Add-Type` is the CodeDom compiler): no string interpolation, no `out var`, no expression-bodied members. Breaking that is a run-time error, not a build error, so CI will not catch it.
- `tools/observe/selftest-target.ps1` — the WinForms window `selftest` and `queue-selftest` drive. Never run it by hand.
- `tools/observe/Queue.ps1` — the batch queue (`queue`, `drain`, `queue-status`, `queue-cancel`). Dot-sourced into `observe.ps1`'s **script scope**, so it sees the parameter block, `$script:Result`, `Fail`/`Finish`/`Say`, `Test-SessionLocked`/`Get-LockReason` and `Resolve-Target`. Separate file for the same reason the interop is: a 700-line block bolted onto the end of `observe.ps1` would be undiffable. Pure PowerShell 5.1, no C#.
- `tools/observe/QueueSelfTest.ps1` — `queue-selftest`. Scratch queue roots under `%TEMP%`, scratch window only.

## What it does not prove

`selftest` proves `SendInput` reaches a foreground window through the ordinary Windows input queue — the same queue V Rising reads. It does **not** prove how the game reads that queue. That is why every virtual key is sent with its scan code alongside (`MapVirtualKey`, no `KEYEVENTF_SCANCODE` flag), so both a virtual-key reader and a raw-input/DirectInput scan-code reader see the press; sending a vk with `wScan = 0` is the usual mistake and is invisible to raw-input readers. Unity's Input System reads raw input.

Camera look is driven by **relative** raw mouse deltas, so `move -X -Y` (absolute) will not rotate the camera; use `move -Dx -Dy` for that.

`queue-selftest` exercises grammar rejection, capture-only execution, stale identities, expiry, executor lock, stop-file behavior and real input delivery against its own scratch window. It uses OS input and can take focus; run it only with authorization and an unlocked session. Record the actual check count and skips from that run.

A locked-session self-test reports the skipped input leg explicitly in its JSON. Offline/locked checks do not count as verified input delivery.

Qualification gaps remain for `stale-nostart` and live drain interruption/lockout paths. Offline decision tests do not establish real Ctrl-C behavior, input isolation or second-executor behavior. Record fresh live evidence before labeling those cases verified.

The batch's recorded `clientLogFile` determines its log evidence. An explicit `-ClientLogPath` supplied to the drain overrides that recorded path. Verify the chosen path in the result, especially when using the scratch self-test window.

Locked-client captures and dry-run/wait behavior have separate research evidence. Keep each verification claim scoped to its actual capture/input mode; consult the external archive for dated results and preserve fresh output when rerunning.

Queue paths **do** produce exit 3 for a live-server path, unlike the older subcommands described above: `Get-QueuePaths` and the per-step `out=` fence call are wrapped in `try`/`catch` and turned into a proper `observe: ` line.
