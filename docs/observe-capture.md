# Focus-free screenshots

Use the current repository script, not a stale build-tools checkout:

```powershell
pwsh -NoProfile -File <repo>\tools\observe.ps1 shot -Window hwnd:0x530942 -MaxWidth 1920 -Out <workspace>\_observe\effect-review.png -Json
```

The HWND above is only an example. Resolve the current game window with `observe.ps1 windows`; the BepInEx console can share the game's process ID. An old HWND is not proof of the current target.

`shot` now defaults to `PrintWindow`. It sends no input, requests no focus and does not automatically switch to a desktop capture. `-Method Auto` remains an explicit opt-in to the existing guarded Screen fallback; `-Method Screen` explicitly reads desktop pixels. A screenshot failure never authorizes using input, focusing the game, or capturing a different window.

`-MaxWidth 1920` bounds the saved PNG width, retaining aspect ratio and never upscaling. A 3840 x 1080 capture becomes 1920 x 540. Omit it, or use zero, to retain the original resolution; valid values are 0 through 16384. Capture quality/blank checks still use the original pixels. JSON reports `sourceWidth`/`sourceHeight` for the captured frame and `width`/`height` for the saved file. `shot -Json` writes its result to stdout and explanatory lines to stderr.

## Runtime compatibility

Observed during the form test session on 2026-09-06: running the old script in PowerShell 7 failed at its native `Add-Type` call with the System.Drawing `Bitmap` type forwarded to a missing System.Drawing.Common compiler reference. Running the same script through Windows PowerShell 5.1 worked. This was a host tool/compiler problem, not evidence that the game could not supply screenshots.

The script now detects PowerShell 7 and re-enters the installed Windows PowerShell 5.1 before loading interop. Bound parameters travel through a temporary serialized data file; they are not interpolated into executable command text. Explicit true/false switches, quoted paths, Unicode, newlines, the working directory and the child exit code are preserved. The temporary file is removed afterward. The handoff emits one compatibility message to stderr; result JSON records the runtime version. If 5.1 cannot be found, it refuses with exit 1 rather than attempting input or another capture mechanism.

Also observed in that session: a roughly 6 MB, 3840 x 1080 PNG was rejected by the image-view tool with an `invalid base64` error, while a 1920 x 540 resize was accepted. This does not establish a universal file-size limit. Use the bounded PNG when review tools reject a full-resolution image; keep full resolution separately when detail matters.

## Offline verification

```powershell
pwsh -NoProfile -File <repo>\tools\observe.ps1 selftest -Offline -MaxWidth 1920 -Json
```

Verified after the fix: the actual PowerShell 7 invocation handed off to Windows PowerShell 5.1.26100.8875 and passed all 94 offline checks. Seven added checks create synthetic bitmaps only: proportional dimensions, retained colors, original bitmap ownership, PNG readback, no upscaling, zero preserving full resolution, and a one-pixel height floor. No game/window capture or synthetic input was used in this verification.

A separate handoff fixture verified literal quotes, command-like text, Unicode, a newline, true and false switches, a script path containing spaces, working-directory preservation, and an explicit child exit code of 37. The fixture reported success. Evidence from this session is kept outside the repository, under `<local-evidence>\observe-offline-ps7.json`, `observe-offline-ps7.stderr.txt`, and `observe-handoff-check.ps1`. These are host-tool checks; they do not claim new live game-capture verification.