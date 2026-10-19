---
title: JsonDocument.Parse over file bytes rejects a UTF-8 byte order mark
game: V Rising
game_build: Steam 23521359
progenitor: 16aca23ce plus the JsonFiles fix in the same PR as this note
side: offline
area: install
tags: json, bom, byte order mark, utf-8, settings, manifest, ServerHostSettings, parse, notepad, powershell
route: JsonFiles.ParseFile / JsonFiles.Parse (Core); an inline skip in Bootstrap's UninstallAtStart
status: compile-verified
date: 2026-10-19
evidence: tests/Progenitor.Tests/JsonBomTests.cs, PR 152 (a server's ServerHostSettings.json began with a BOM and no server name was read)
---

## Goal

Read JSON files that people edit by hand (server host settings, package `manifest.json`, request markers) whether or not the editor saved them with a UTF-8 byte order mark.

## Route

In Core, read JSON files with `JsonFiles.ParseFile(path, options)`, or `JsonFiles.Parse(bytes, options)` when the bytes are already loaded (for example because they are also hashed). Bootstrap cannot reference Core, so `UninstallAtStart` skips the mark inline. The file is never rewritten, and hashes stay over the exact file bytes.

## What the engine did

- `JsonDocument.Parse(byte[])`, `JsonNode.Parse(ReadOnlySpan<byte>)` and a `Utf8JsonReader` over raw bytes treat a leading EF BB BF as an invalid token and throw `JsonException`.
- `File.ReadAllText` detects and drops the mark, so text-based reads never showed the problem.
- Notepad, Windows PowerShell's `Out-File` and `Set-Content -Encoding UTF8`, and some Thunderstore `manifest.json` files write the mark.

## Verification

**Compile-verified** with game-free tests: the helper on input with and without a mark, the server name read from a `ServerHostSettings.json` that starts with one, a package `manifest.json` with one, and an uninstall request with one read by Core and by Bootstrap's copy. The original symptom was seen while fixing PR 152; the fixed build was not run in the game for this note.

## Gotchas

### A hand-edited JSON file reads as empty or invalid

- **Symptom:** a setting that is plainly in the file (a server name, a manifest version) is missing, or a request file is refused as unreadable, after someone edits it in Notepad or PowerShell.
- **Cause:** the file now starts with a UTF-8 byte order mark and the byte-level parser throws; the surrounding `catch` turns that into "no value".
- **Fix:** parse file bytes through `JsonFiles`; never call `JsonDocument.Parse(File.ReadAllBytes(...))` directly. Keep writing Progenitor's own JSON without a mark.
