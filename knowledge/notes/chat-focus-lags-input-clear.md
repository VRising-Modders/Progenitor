---
title: The chat's focus flag drops one update after Enter empties the input
game: V Rising
game_build: Steam 23521359
progenitor: a328c44c1 (2026.10.115) plus the CHAT7 recall fix in the same PR as this note
side: client
area: chat
tags: chat, input, focus, tmp, enter, send, recall, headless
route: ChatSentWatch keeps a line emptied all at once for two focused frames
status: verified-live
date: 2026-10-18
evidence: internal research notes (chat send path, ClientChatSystem end-edit handler)
---

## Goal

Tell, from Progenitor's per-frame chat hook, which line the player just sent with Enter (sent-line recall, CHAT7).

## Route

Watch `HUDChatWindow.ChatInputField.text` and `HUDChatWindow.IsChatFocused` each frame, and treat a line that goes from typed to empty in one frame as the candidate send for the next two focused frames. It counts when the focus drops in that time (`ChatSentWatch`, game-free tested).

## What the engine did

- The game's end-edit handler on the chat input field (raised by Enter) parses and sends the trimmed line, empties the input at once, and calls its focus-loss routine, which changes the chat state.
- `IsChatFocused` is the window's `_ChatIsFocused`, which `ClientChatSystem.OnUpdate` writes from the chat state on each update. So for one update after Enter the input already reads empty while `IsChatFocused` still reads true.

## Verification

**Verified live** 2026-10-18 on a headless client joined to an isolated test server: right after `chat_tabs submit` raised the field's end-edit, the bridge read `focused: true, text: ""`; the next read was `focused: false`. With the fix the line reached chat, `sent.txt` and Up recall.

## Gotchas

### A send looks like "focused, emptied" before it looks like "unfocused"

- **Symptom:** code that remembers the line while focused and counts it when focus drops with the input empty never sees a send; the remembered line is already "" by then.
- **Cause:** the text is cleared inside the end-edit callback; the focus flag follows only on the next `OnUpdate`.
- **Fix:** keep the line that was emptied all at once for a frame or two, or read the line before the clear (the end-edit's own text).
