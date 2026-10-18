---
title: Remote test mode must not restore local management after disconnect
game: V Rising
game_build: Steam 23521359
progenitor: 15499039
side: client
area: ui
tags: remote, install, disconnect, inventory
route: ServerRouteSelector, ServerPicker and InstallModsPane server cells
status: verified-live
date: 2026-10-15
evidence: external research remote-disconnect-20261015.md; remote-disconnect-stale-widgets.json; remote-fixed-widgets.json
---

## Goal

Keep remote management unavailable after disconnecting, without breaking normal local dedicated-server management.

## Route

Use the current route decision for the picker, catalog suppression and row controls. A disconnected forced-remote target has no usable route and says Connect to manage.

## What the engine did

Observed before the change: leaving a server restored configured-folder management. HotDemo's server row also said Installed while the same readback's file scan and ledger showed no server copy. Source-checked cause of the second issue: server-only hot uninstall cleared session state only for pending installs, leaving SessionInstalled behind. ServerHalfOf used that history even with a newer authoritative scan.

## Verification

Working changes on the stated base, client Core generation 3: successful hot reload, previous generation collected. At the title screen remote-test mode showed Connect to manage with no server buttons; a focus-free screenshot confirmed the disabled column. Temporarily switching remote-test mode off restored local management and HotDemo showed Missing with Install. Remote-test mode was restored. 1,261 related tests and root build passed. The retained-session regression is covered offline; the full install/remove sequence was not repeated after this reload. Connected remote install behavior was not retested in this batch.

## Gotchas

### Disconnect changes the selected route

- **Symptom:** title-screen controls silently operate on local files after remote testing.
- **Cause:** disconnected routing and local picker selection independently enable the folder route.
- **Fix:** preserve forced-remote suppression while disconnected in both decisions; normal local mode remains available with the switch off.

### Session install history is not current inventory

- **Symptom:** Installed/Uninstall conflicts with absent files and ledger.
- **Cause:** the server row trusts a previous session install after removal.
- **Fix:** current scans outrank the session fallback; successful immediate server-only removal clears its session install flags too.
