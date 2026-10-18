---
title: Local server install targets must follow current configured folder
game: V Rising
game_build: Steam 23521359
progenitor: 15499039
side: both
area: ui
tags: install, server, folder, config
route: InstallModsPane side buttons and ModsMenu configured-folder synchronization
status: verified-live
date: 2026-10-15
evidence: external research hotdemo-install-folder-20261015.md and hotdemo-installed-widgets.json
---

## Goal

Install a missing server half into the configured local test server.

## Route

Use the Install tab's Server button through the same widget handler as a click, then confirm. Inspect the local write plan and the server's loaded image independently.

## What the engine did

Observed: routing correctly identified the local server, while its Install button opened a client-only confirmation claiming no local folder existed. Source-checked cause: configuration was already bound before the folder value became available; EnsureBindings returned without updating the pane's folder. A separate route probe read the newer value.

## Verification

Working changes on the stated base, client Core generation 2 (SHA prefix d5c1f1e): the dialog named the local test server, the plan targeted LocalServer, the file was written to its reload folder, and server ping confirmed the expected HotDemo image. Both menu columns showed Installed. The test's server copy was uninstalled through the same UI; the server log confirmed clean unload. The pre-existing client copy was preserved. Root build and 1,128 related tests passed, including Server/Both requests after late config loading and rejection of unavailable explicit targets. A fresh Both install was tested offline, not repeated live.

## Gotchas

### A local route does not prove the install pane has that folder

- **Symptom:** Server or Both installs only the client; server remains missing.
- **Cause:** independently initialized route and pane state, followed by client fallback for an explicit server request.
- **Fix:** synchronize the shared folder when its configured path changes, clear the old root before validating a changed path, and refuse unavailable explicit targets instead of falling back. Cache unchanged paths so UI ticks do not repeatedly probe the filesystem.
