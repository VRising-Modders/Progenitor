# The development bridge

Progenitor.Dev's loopback bridge: wire protocol, bridge users and queue, logging, ports, token, the opt-in gate, calling it without MCP, and which operations run on the main thread. The operations themselves are in the [operation reference](bridge-ops.md). Part of the [development loop](dev-loop.md).

## Bridge wire protocol

Newline-delimited JSON over TCP. One object per line in each direction. The MCP server opens a fresh connection per request; the socket will also accept several requests on one connection.

**Caps.** The bridge accepts at most 4 live connections and request lines up to 1 MiB. It allows one main-thread operation at a time per Dev bridge instance. A second returns `a main-thread op is already running; retry after it finishes`.

## Bridge users

Several agents (Claude, Codex, Grok) share the client and the test server. Each request may carry an optional `who` object next to `token`, `op` and `args`, naming the caller; the bridge records it so agents can see who else is using a process and coordinate.

```
→ {"id":1,"token":"...","op":"ping","who":{"ai":"claude","name":"Example session","id":"<session id>","client":"claude-code 2.1","tool":"mcp","pid":1234,"model":"<model>"}}
```

| Field | Meaning |
|---|---|
| `ai` | `claude`, `codex`, `grok`, another name, or `unknown` |
| `name` / `id` | Session name and session ID as the AI's own app shows them |
| `client` | MCP `initialize` client name and version, or the agent string from the environment |
| `tool` / `pid` | `mcp`, `call.mjs` or `live-rig`, and that process's ID |
| `model` | Model, when cheaply known |

Every field is optional and each is clipped to 120 characters. A request without `who` (older scripts, bots) is recorded as `unknown`; older bridges ignore the field. Users are keyed by session ID, else by ai + name + tool, plus the pid for a long-lived MCP process (`call.mjs` starts a new process per request, so its pid is left out).

The bridge keeps the current user and the last three distinct previous users (ai, name, id, client, tool, pid, model, `firstSeen`, `lastSeen` in UTC, `requests`, `lastOp`). It persists them to `progenitor-dev.users.json` beside the token file, so a Dev reload or a game restart keeps the history; it saves on a user change and otherwise at most every 5 seconds, on the socket thread. A corrupt file is logged and replaced. On a change it logs, at most once per user per 5 minutes:

```
bridge: now used by claude "Example session" (<session id>); previously codex "Other session" 3 min ago
```

Where it shows up:

- `ping` and `status` results carry `bridgeUsers: {current, recent}`; each entry adds `ageSeconds`. The `bridge_users` op returns the same object on the socket thread.
- Every successful response carries a top-level `bridgeUser` (the most recent other user) when that user was seen within the last 10 minutes. Old callers ignore it.
- The MCP server appends one line to a tool result when that happens, for example `Note: Codex session "Example" (id …) used the client bridge 3 min ago (last op: connect).` It repeats the note only when that user has made another request. `progenitor_ping` and `progenitor_status` print a short summary of current and recent users above the JSON.
- `call.mjs` prints the same note to stderr only on a terminal or with `PROGENITOR_CALL_NOTES=1`, because PowerShell scripts that merge stderr under `ErrorActionPreference = "Stop"` would treat it as a failure.

**How the caller is detected** (`tools/mcp/identity.mjs`, re-read at most every 60 seconds; detection never fails a call):

1. `PROGENITOR_AGENT`, `PROGENITOR_SESSION_NAME`, `PROGENITOR_SESSION_ID` always win. In MCP, `progenitor_whoami` shows the detected identity and can override `ai`, `session_name` and `session_id` for the rest of that MCP process (an empty string clears an override).
2. Codex: `CODEX_THREAD_ID`, set for commands Codex runs. The name comes from `session_index.jsonl` in `CODEX_HOME` (default `%USERPROFILE%\.codex`).
3. Grok: `GROK_SESSION_ID`, or the MCP process's parent ID listed in `active_sessions.json` in `GROK_HOME` (default `%USERPROFILE%\.grok`). The name and model come from that session's `summary.json`.
4. Claude Code: `CLAUDECODE=1` or `CLAUDE_CODE_SESSION_ID`. The name is the desktop app's sidebar title (the session file named by `CLAUDE_CODE_HOST_SESSION_ID` under `%APPDATA%\Claude\claude-code-sessions`), else the last custom title in the session transcript under `%USERPROFILE%\.claude\projects` (only the last 4 MiB is read, so a CLI session titled long ago may show no name). Claude is checked last because its variables are inherited by anything a Claude session starts, including Codex or Grok; Grok-shaped plugin variables (`GROK_CC_*`) are ignored.
5. Otherwise the MCP client name (`claude-code`, `codex-mcp-client`, …) decides `ai`.

An MCP server that Codex starts has no thread ID in its environment, so it reports `codex` from the client name without a session; use `progenitor_whoami` to name it. Unit-tested (`BridgeUsersTests`, `tools/mcp/test/identity.test.mjs`). Live on the test server bridge (2026-10-10): Claude detected with the sidebar title and model; Codex and Grok users simulated with the override variables showed in `bridge_users`, the change log lines and the sharing notes; the history survived a Dev reload. Detection from a real Codex or Grok session is not yet verified.

## Bridge queue

Agents share one client and one test server. The bridge queue stops them from stomping on each other and makes sure an agent that is done does not hold a side up. It lives in `tools/mcp/queue.mjs`: one JSON file for the whole workspace (`PROGENITOR_QUEUE_FILE`, default `<workspace>\_build\bridge-queue.json`), shared by every MCP process, `call.mjs`, the live rig and the queue CLI. The Dev bridge itself does not know about it.

**Rule:** hold a side before any operation that changes that game, and leave the queue as soon as your live work is done. Don't hold the client while you are idle or working offline; join again for the next batch.

| Who | Changing op on a free side | Changing op on a side another session holds |
|---|---|---|
| MCP tools (bridge tools, `progenitor_exec`, `progenitor_deploy`, `progenitor_test_server` start/stop/restart) | Refused until you join | Refused |
| `call.mjs`, live rig, `deploy.ps1` | Allowed (scripts) | Refused (`call.mjs` and `queue.mjs gate` exit 5; `deploy.ps1` stops before writing; a `-WhatIf` plan is not gated) |

Read-only ops always work for everyone: `ping`, `logs`, `ops`, `status`, `users`, `players`, `worlds`, `query`, `get`, `components`, `inspect`, `prefab`, `snapshot`, `castles`, `bridge_status`, `bridge_users`, `reload_status`, `perf_status`, `tree`, `versions`, `versions_journal`, `versions_plan`, `catalog_status`, `datetime_read`, `rejoin_read`, any op with `action` `status` / `list` / `live` or `statusOnly: true`, `time` / `photo` / `unlock` without an `action`, `store` without `value`, and the `mods_ui` reads (`status`, `widget_read`, the light reads such as `rects` and `nav_state`, `keys` / `restart` reads and `menu` with `op: status`). A script the MCP server starts (for example `deploy.ps1` calling `call.mjs`) acts for that MCP session (`PROGENITOR_QUEUE_KEY`). A script of the same AI kind as the holder is let through when either side lacks a session ID: Codex sets its thread ID for shell commands but not for the MCP server it starts, so the two cannot be matched for certain. An MCP process picks its queue key once, at its first tool call, so a later `progenitor_whoami` override does not strand its own slot. `PROGENITOR_QUEUE=off` turns enforcement off when only one agent uses the machine. It is not a way past the line: under it, a game-changing op on a side another session holds still runs, but is recorded on that holder (status shows `OVERRIDE: <session> last ran <op> on the <side> under PROGENITOR_QUEUE=off`), logged as a queue event, and sent to the holder as a notice at most every 5 minutes. A user's "go ahead" does not replace the queue either: join it, and message the holder first when it is urgent. When your live work ends, leave the queue and tell the user you are done and hold nothing, without waiting to be asked.

**Entries.** One entry per session, keyed like [bridge users](#bridge-users). Joining claims `client`, `server` or both, and requires:

- `estimated_minutes` (1–480): how long the live work will take;
- `progenitor_version`: the Progenitor version or commit you will run, for example `0.1.0+abc1234`;
- `mods`: every mod you will run, as `name@version` or `{name, version}`, or an empty list.

Order is first come, first served per side. A waiter becomes active as soon as no holder and no earlier waiter claims any of its sides, so a server-only job can run beside a client-only one. Widening a held claim (client to client+server) puts you back at the end of the queue.

**What status shows.** For each holder: its sides, purpose, versions, minutes elapsed and left (or how far it is over its estimate), and how to reach it: its MCP process ID and whether that process is alive on this machine, its last activity, and its own address (a Claude desktop session ID for `send_message`, a Codex thread, a Grok session, or a `contact` the holder set). For each waiter: its position, estimate, expected start and finish (from the estimates ahead of it), versions, and whether it may contact the holder. The last five queue events follow.

**Contacting the holder.** Only the next two waiters whose sides overlap a holder may contact it. `probe` reports the holder's process, idle time, estimate and address and records the check, so the others can see someone already asked. `evict` (reason required) releases a holder that has been idle at least 10 minutes and is past its estimate. Everyone else waits.

**Mailbox.** The session address in status is only messageable for a Claude desktop session; Grok and Codex sessions have no inbound channel, so the queue carries messages itself.

- `message` with `text` (CLI: `queue.mjs message --text "..."`). A waiter among the next two writes to the holder it waits for. A holder writes to the waiter at position `to`, else to every waiter that messaged it during this hold, else to every waiter for its sides. Nobody else may send. At most two undelivered messages per sender and recipient; messages are kept for 6 hours (100 at most).
- **Delivery** happens wherever the recipient works, once per message: appended to its next MCP tool result, printed above the output of any `queue.mjs` call, and printed on stderr by `call.mjs` (stdout stays the bridge's JSON; `spike.mjs` passes it through). `call.mjs` holds mail back, leaving it unread for the next queue or MCP call, in two cases: with `PROGENITOR_CALL_NOTES=0`, which `deploy.ps1`, `verify.ps1`, `launch-client.ps1` and `extra-client.ps1` set because they parse or discard its output; and when Windows PowerShell 5.1 (`powershell.exe`) captures its output, where a stderr line under `ErrorActionPreference=Stop` with `2>&1` becomes a terminating error. A script session without its own entry (a Codex shell whose MCP server has no thread ID) gets the mail of the single same-AI entry, as the gate does.
- The same channel carries the holder reminder: who waits, for which sides and how long, and whether the estimate has run out. It is repeated at most every 5 minutes, and sooner when a message arrives.
- **Status** lists the last messages to and from each holder with `delivered N min ago` or `not delivered yet`. A message the holder has not answered for 10 minutes although it has been active since shows as `UNANSWERED`; that is the point to ask the user rather than wait.
- **Notices.** `no_reply: true` (CLI: `--no-reply`) sends a notice: an FYI or a request that needs no answer, such as "main moved, please rebase". A notice is never retracted when you leave, never shows as `UNANSWERED`, and you need not watch for a reply; the recipient sees `Queue notice from … (no reply needed)`.
- **Notify a session that is not queued.** `notify` with `session_id` and `text` (CLI: `queue.mjs notify --session <id> --text "..."`) queues a no-reply notice for any session ID from status or history, queued or not; anyone may send one. It is delivered once with that session's next queue, MCP or `call.mjs` call.
- **After sending, keep watching for the reply.** `wait` (CLI: `queue.mjs wait --seconds 300`) returns as soon as mail arrives, as well as when you get your sides; `status`, any MCP tool call and any `call.mjs` call also deliver it. The send result says so. If you cannot keep watching (the session is ending, or you switch to long offline work), `retract` (CLI: `queue.mjs retract --reason "..."`) withdraws your open messages, the ones not answered yet: an undelivered one is never shown, a delivered one leaves the recipient a `Queue notice: … retracted its message … no reply is expected`. Retraction is automatic when you leave the queue (or release every side), your process exits, you are dropped for not checking in, or you are released or evicted as an idle holder; no notice goes to a recipient that has left. A holder can watch for replies too: its `wait` blocks while it has open messages and returns when mail arrives. A message only counts as answered by a reply sent after it was read.
- `release` with `sides` (CLI: `queue.mjs release --sides client`) hands back some sides and keeps the rest, so a waiter that needs only the client can start while the holder keeps the server. Releasing every side is the same as `leave`. History records it.

**Automatic release.**

| Case | Result |
|---|---|
| The entry's MCP process exited (same machine) | Removed at once |
| Holder idle 15 min while someone waits for one of its sides | Released |
| Holder idle 30 min | Released |
| Waiter with no live process (CLI) and no check-in for 20 min | Dropped |

Activity is any MCP tool call from that session, any bridge call through `call.mjs` or the live rig, and any queue call. It is recorded at most every 30 seconds. A holder gets a reminder through the [mailbox](#bridge-queue) channel, repeated at most every 5 minutes, when its estimate has run out or someone is waiting.

```
progenitor_queue {"action":"join","sides":["server"],"estimated_minutes":20,"progenitor_version":"0.1.0+abc1234","mods":["Example Mod@1.2.0"],"purpose":"hot reload check"}
progenitor_queue {"action":"wait","seconds":300}
progenitor_queue {"action":"update","estimated_minutes":10}
progenitor_queue {"action":"leave"}
```

Without MCP:

```bash
node tools/mcp/queue.mjs join --sides server --minutes 20 --progenitor 0.1.0+abc1234 --mods "Example Mod@1.2.0" --purpose "hot reload check"
```

The CLI also takes `status`, `history --count N`, `wait --seconds N`, `update`, `leave`, `release --sides`, `message --text [--to N]`, `retract [--reason]`, `probe --note`, `evict --reason` and `--no-mods`. A CLI process exits right away, so its entry stays alive through activity, not a process ID.

**What is loaded now.** Agents swap Core in and out depending on when they last rebased, so status opens with LOADED NOW for each side. It is read from the last `ping` on that side: Core's version (`year.month.release+revision`, [versioning.md](versioning.md)), generation and sha256, the Bootstrap version, every reloaded plugin with its version, when it was seen, and through whose session. The queue never pings on its own. It records every ping that MCP, `call.mjs` or `deploy.ps1` makes, except pings from an extra or hosted instance (a non-empty `instance`), and writes only on a change or once a minute; after `progenitor_reload` and a successful `progenitor_deploy`, MCP pings each affected side once. `progenitor_queue {"action":"status","refresh":true}` pings both sides first (one main-thread op each, so don't poll it while someone is playing).

Under the Core line, MCP says how that build's revision relates to its own checkout:

| Note | Meaning |
|---|---|
| `revision … is your HEAD` | Same build as your branch |
| `revision … is in your branch` | The loaded Core is the same as or older than your rebase; your Core can replace it |
| `revision … has commits your HEAD … lacks: rebase before swapping Core` | Someone swapped in a newer Core; swapping yours in over it gets your client kicked |
| `revision … is not in this checkout` | Fetch first, or it was built from an unpushed branch |
| `; built with uncommitted changes` | The build's revision ends in `.dirty` |

A holder whose declared `progenitor_version` names neither the loaded Core's revision (any 7+ character sha that is a prefix of it, or it of that sha; the build stamps 10 characters) nor its full version gets a `note: declared …, but the <side> runs …` line.

**Connection history.** The queue keeps the last 50 connections (holds). `progenitor_queue {"action":"history","count":10}` (CLI: `queue.mjs history --count 10`) lists them newest first: who held which sides, when, for how long against the estimate, how it ended (`left`, `process exited`, `idle N min`, `evicted by …`, `re-queued …`), the declared versions, the build loaded at the start, and every Core, Bootstrap or plugin change seen during the hold. A change seen while nobody held the side becomes a record of its own (`outside the queue`).

Mailbox: unit-tested (`queue.test.mjs`), delivered through `call.mjs` against a fake bridge (`call-mail.test.mjs`) and through the MCP server (`bridge-who.test.mjs`); not yet used between two live agents. Unit-tested (`tools/mcp/test/queue.test.mjs`) and tested end to end through the MCP server against a fake bridge (`tools/mcp/test/bridge-who.test.mjs`); the CLI was run once against a temporary queue file. Image versions in `ping` are unit-tested (`PingIdentityTests`); the queue reading them is tested against a fake bridge. Live (2026-10-11): Dev 2026.10.171 reloaded on the Steam client (title screen, Core 2026.10.167.2+f3f821a01c gen 47) and the isolated test server (Core 2026.10.162.4+df873e433c gen 20, built from a commit not on main) with no errors; both pings reported versions for Core, Bootstrap and every plugin, and LOADED NOW and the connection history showed them. Not yet used live by two agents at once.

## Main-thread queue

One in-flight main-thread op. A timeout is not a free slot: the abandoned lambda still holds the gate until it completes, skips, or is dropped. Do not send another main-thread op after a timeout.

Liveness is the socket-thread ops `logs` and `bridge_status` (`bridge_status` never takes the gate). After an MCP `progenitor_exec` preflight `ping` timeout (25 s, shorter than the bridge 30 s wait) or `already running`, MCP reads `bridge_status` and `logs` and does not retry a mutation. Soak `ping` interval is at least 30 s.

Drain heartbeat (file-only Info, every 5 s unless `heartbeatSeconds` is 0): `tick hb frame=<n> drainMs=<ms> queued=<n> delayed=<n> gate=<free|busy:<op>#<id>>`. Gate lines: `bridge gate enter op= id=` and `bridge gate finish op= id= elapsedMs= abandoned=<bool>` (file-only), `bridge gate abandon op= id= waitedMs=` (console too). Log filter: `tick hb|bridge gate|scratch:|MainThread`. See [Logging policy](#logging-policy).

## Logging policy

The BepInEx console window draws every line it receives, and a console that is busy drawing stalls the game (2026-10-06: the console helper of a stuttering client had used over 6,700 CPU-seconds). Progenitor therefore splits its output:

- **Console and file:** errors, warnings that need action, and one-off lifecycle lines (loaded, installed, joined, a reload, a command's result).
- **File only:** per-frame, per-layout and per-op diagnostics, written with `Plugin.Logger.LogDiag(...)` (`DiagnosticLog`, public in Core). The line goes to `BepInEx/LogOutput.log` and to the bridge's `logs` ring exactly as an Info line, but not to the console. Nothing in `BepInEx.cfg` needs editing, and `[Logging.Console] LogLevels` still governs everything else.

File-only today: the `ui hitch` summary, `tick hb`, `bridge gate enter/finish`, `host_capture:` dumps, panel `laid out` / `re-laid out` / `placed` / `under` / `canvas readable` lines, every `scrollbar read back` (including "they disagree", formerly a warning; taken only once the ScrollRect is active and a frame has passed since the layout it checks, and printed only when it differs from the panel's previous read-back line since it was shown), `Ui: show` / `Ui: hide` / `Ui: key … hid`, `TryWire … returned false`, `Ui: shown-input`, `Ui: tabdiag`, interaction-probe row lines (a panel-level "dead panel" verdict stays a warning), the text-field focus transitions, control `source=` lines, the settings, key cap, workspace artwork, tab and frame-path ladders, the Mods workspace list/strip/open timing and transition lines, `scroll-preserve`, `PrefabRegistry: applied …`, `applied PrfE`, `WorldSaveGuard: guarded state goes back …` and the icon cache check.

To read them: open `BepInEx/LogOutput.log` (BepInEx flushes it every 2 s), use `progenitor_logs` / the `logs` op with a filter, or `progenitor_logfile` when the bridge is down.

**Queued console (every mod's lines).** BepInEx's own console listener writes each line on the thread that logged it: it sets the color, writes the line and resets the color, synchronously. When the console host is slow to take output, every `LogInfo` from every mod stalls the frame, and a longer line stalls it longer. Bootstrap therefore replaces that listener at start with `QueuedConsoleLogListener`. The logging thread only formats the line and adds it to a bounded queue (`ConsoleLogQueue`). One background thread writes the queue to the console, with consecutive lines of one color in a single write. Bounds:

- At most 4,000 lines or 1 M characters wait. A line that does not fit is dropped from the console only, and a `[Progenitor: N console line(s) dropped …]` notice says so at most once a second, with the count since the last one.
- A line longer than 2,000 characters is shortened in the console and ends with `… [N more characters; the whole line is in LogOutput.log]`.
- A batch is at most 256 lines or 64 K characters.

`LogOutput.log`, the bridge's `logs` ring and the session log under `BepInEx/progenitor-logs` still receive every line whole, and in order. At process exit the writer gets at most 500 ms to empty the queue. Exit never waits longer, even when a console write is stuck (a QuickEdit selection pauses console output), and lines logged after that are still queued and written while the process lives.

`[Logging] QueuedConsole=false` in `progenitor.cfg` restores BepInEx's direct writes at the next start. Use it when another mod looks for BepInEx's own `ConsoleLogListener` in `Logger.Listeners`: with the queue on, that listener is no longer there.

The preloader's session log is buffered too. The logging thread appends to a list, and a one-shot timer writes and flushes it every second; overlapping requests fold into the run in progress. An error line is written and flushed at once on the logging thread when no other write is in progress, so a crash right after it keeps the line. When the timer is mid-write, the error line goes to the timer to write as soon as that write ends. Source-checked and tested game-free (`ConsoleLogQueueTests`, `SessionLogWriterTests`); not verified live.

**Hitch reporting.** Per frame `UiFrameCost` only adds to counters. At most every 30 s it writes one file-only summary, and only when a hitch happened (a frame interval, or a named Progenitor phase, of 80 ms or more):

```
ui hitch summary windowS=30 frames=2950 hitches=41 intervalMs=median:85,max:1040 outsideOnly=35 slowest=handler:JoinFlowPanel.OnFrame:211 top=[handler:JoinFlowPanel.OnFrame 420ms/3, handler:Keybinds.Pump 40ms/2]
```

With the queued console, the summary ends with that window's console counters, for example `console=lines:412,dropped:0,cut:3,writeMs:930,maxBatchMs:212,maxQueue:57,maxEnqueueUs:18`. `lines` were queued, `dropped` did not fit the queue, and `cut` were shortened. `writeMs` and `maxBatchMs` are the background writer's time in console writes. Before the queue, the logging thread spent that time itself. `maxEnqueueUs` is the most that one line cost the logging thread. A large `writeMs` means the console is slow, which no longer stalls the frame. An older Bootstrap leaves the field out.

`outsideOnly` counts hitches in which no Progenitor handler or phase took 1 ms (the game's own frame); `top` totals the slowest named phase of each hitch frame. `intervalMs` describes hitch frames only: the median of the hitch intervals, not of every frame. For the average frame time, divide the window by `frames` (`windowS=30 frames=780` is 38 ms, about 26 fps). Frames that are slow but stay under 80 ms are not hitches and do not appear in `top`.

Named phases include Progenitor work that runs inside the game's systems, outside the frame dispatch. Each system hook callback is timed as `hook:<plugin id>:<System>.before` or `.after` (`progenitor` for Core's own hooks). Each server material seam gate is timed as `seam:materials.countGate`, `.compose`, `.payGate` or `.pay`. Calls are added up per name between two frame ticks, so a hook that runs 40 times in one frame is reported as one total. That total is then charged to the frame interval it ran in.

Budgeted server work adds `work=[<name> frames:N ms:T max:M carried:C skips:S, …]` to the summary: the frames that ran it, its total and largest single-frame milliseconds, the most items it carried over to a later frame, and the passes it skipped because nothing could have changed. Names: `materials` (the view refresh and its shared-storage index, 3 ms a frame), `servants.hook` (the hunt hook before `ServantMissionUpdateSystem`; skipped until a hunt is within 2 s of finishing), `servants.returns` (publishing returns and their handlers, 3 ms a frame), `itemRemoval` (2 ms a step), `prefabSettle` (the live-refresh settle, 4 ms a frame) and `saveGuard` (the world-save guard's tick; its save-folder stamp is sampled off the main thread).

A single frame whose named Progenitor handler or phase takes 250 ms or more writes the full `ui hitch intervalMs=… worst=… handler=…` line at once, to the console too, at most once per 5 s.

The waiter has a 30-second cap. A timeout abandons queued work but does not free the slot until that work completes, skips or is dropped; do not interpret a timeout as permission to repeat a mutation. Dev unload drops its owned work, completes pending waiters with an unload error, closes clients and stops its listener. A replacement Dev instance should accept work after it binds. The listener uses exclusive binding so two generations cannot listen on the same port.

```
Request:  {"id": 1, "token": "<from token file>", "op": "logs", "args": {...}}
Success:  {"id": 1, "ok": true, "result": {...}}
Failure:  {"id": 1, "ok": false, "error": "bad token"}
```

`id` is echoed. `args` may be omitted. Unknown `op` returns `ok: false`. A token that does not match returns `"bad token"` and nothing else.

## Ports

| Process | Port | Token file |
|---|---|---|
| V Rising client (primary, Steam) | 27341 | `<V Rising>\BepInEx\config\progenitor-dev.token` |
| Extra client index N (1..9) | `27341 + 10*N` (27351, 27361, …) | `%TEMP%\progenitor-client-N\progenitor-dev.token` |
| Test server (or the bundled server a client launches for a private game) | 27342 | `<that process's BepInEx>\config\progenitor-dev.token` — for the staged test server, `<testserver>\server\BepInEx\config\progenitor-dev.token` |

Defaults come from `Plugin.cs`: port `27342` in a `VRisingServer*` process, `27341` otherwise. Override in BepInEx config (`Bridge.Port`, `Bridge.Enabled`, `Bridge.TokenFile`). **Environment wins over cfg** when set, and is not written back: `PROGENITOR_DEV_PORT` (1..65535), `PROGENITOR_DEV_TOKEN_FILE` (path). Extra `VRising.exe` processes share the Steam install's `BepInEx/config`, so without the env override they would all try to bind 27341. `PROGENITOR_INSTANCE` is an optional string tag logged at bridge start and returned by `ping` / `status` as `instance`. `Bridge.Enabled` defaults to true on a machine that has this DLL.

## Token file

**The token is regenerated on every start.** Dev writes a fresh random 24-byte URL-safe value into `BepInEx/config/progenitor-dev.token`. `PROGENITOR_DEV_TOKEN_FILE` overrides the path (extra clients use `%TEMP%\progenitor-client-N\progenitor-dev.token`). If the file cannot be written, Dev falls back to a session-only token and the MCP server cannot authenticate until the file exists.

**Token write order.** The bridge writes a fresh token only after restricting its temporary file:

1. Refuse outright if either the token path or `<token>.new` is a reparse point. Never write through a symlink or junction, and never delete one either — the log says so and the bridge falls back to a session-only token.
2. Create `<token>.new` with `FileMode.CreateNew` + `FileShare.None`. `CreateNew` refuses both a pre-existing file and a planted link rather than following it, and the file it creates is **empty**.
3. Apply and verify the DACL on that empty file (`token acl=` below).
4. Reopen it `FileShare.None` — which fails if anything grabbed a handle in the gap — and write the secret into it.
5. `File.Move(..., overwrite: true)` onto the real path. The moved file keeps the DACL applied to it, and the replacement is a rename rather than a truncate-and-write, so `server.mjs` can no longer read a half-written token and report it as an auth failure.

**Nothing needs restarting when the token rotates.** The MCP server (`tools/mcp/server.mjs`) and `tools/mcp/call.mjs` read the token file on every call, so they pick the new value up on their own. The one thing that does not is a shell that exported the token into an environment variable — re-read the file (`Get-Content <token file>`) after the game restarts.

The file is created when the process starts with Progenitor.Dev loaded, not at deploy time. No token file yet means the game has not reached `Plugin.Load` with the bridge enabled.

## Loopback only — never ship this

The listener binds `IPAddress.Loopback`. Each accepted connection is dropped unless the remote address is loopback. That is the whole network policy: no bind on `0.0.0.0`, no "allow LAN" flag.

`Progenitor.Dev.dll` is a dev-machine plugin. Do not copy it to `C:\VRisingServer` or any public host. A public server should not have the token file, the reload folder contents, or this DLL.

## The opt-in marker — the bridge refuses to start without it

Packaging omitting the DLL is not a safety property of the code: a copied folder, an `-IncludeDev` build, or a mod manager syncing a config directory can all put the bridge on a machine nobody meant to open a control socket on. Anything that can read the token file gets `set`, `spawn`, `command` as any user, `store`, `world_save` and `reload` (which is code execution). So `Plugin.Load` runs a gate first (`src/Progenitor.Dev/DevGate.cs`) and returns without installing *anything* — bridge, host-capture prefixes, extra-instance Steam skip — unless all of these hold.

1. **An explicit opt-in.** Either the marker file

   ```
   BepInEx/config/progenitor-dev.enable
   ```

   contains the exact line

   ```
   I understand this exposes a local control socket
   ```

   (other lines around it are ignored; the sentence itself is matched case-sensitively and must be exact), **or** the environment variable `PROGENITOR_DEV=1` is set for the process — the value must be exactly `1`. The env form wins over the file, which is how an extra client opts in without writing into the Steam install's shared config folder. The marker file lives next to `progenitor-dev.token`, so **one marker per BepInEx tree**:

   ```
   C:\Program Files (x86)\Steam\steamapps\common\VRising\BepInEx\config\progenitor-dev.enable
   <testserver>\server\BepInEx\config\progenitor-dev.enable
   ```

   Extra clients share the Steam install's config folder, so the client marker covers them. A **hosted child server** does not: staging junctions `core`/`patchers`/`unity-libs`/`plugins`/`progenitor-reload` into `VRising_Server\BepInEx` but not `config`, so the child needs its own marker under `VRising_Server\BepInEx\config` — or `PROGENITOR_DEV=1` in the client's environment, which the child inherits. Neither file is tracked, and `tools/package.ps1` fails if one is ever staged into a zip.

2. **A dedicated server has to identify itself as one of ours.** On a server process the gate reads the server's own settings from disk, rather than `SettingsManager.ServerHostSettings`, which is still null this early in `Load`. Since SERVERDESC1 it reads them in the game's own order, through the resolver Core uses ([server-list-description.md](server-list-description.md#as-built)): the install's `StreamingAssets` default (under the BepInEx game root, else the folder holding `VRisingServer.exe`), `<persistentDataPath>\Settings\ServerHostSettings.json` (`-persistentDataPath`, else the user's `LocalLow\Stunlock Studios\VRisingServer`), then the save folder's copy, each overwriting the keys it carries; then the environment variables and arguments that override the listing flags, the address and the port (`VR_LISTONSTEAM`, `-listOnSteam`, `-enableListOnSteam`, `VR_LIST_ON_EOS`, `-address`, `-gamePort`, …). Before, it took the first file found among `<-persistentDataPath>\Settings`, the game root and the executable's folder (including `Settings\` and in-place copies the game never reads) and ignored the save folder and the environment. The decision table, in order:

   | Condition | Result |
   |---|---|
   | any of the process's paths is `C:\VRisingServer` or below it (**canonicalized**, below) | **refuse** — the user's live server, fenced the way `ExtraInstanceServerAuth` and `WorldSaveOps` fence it |
   | `ListOnSteam=true` or `ListOnEOS=true` (or a wrapper's `ListOnMasterServer=true`) | **refuse** — advertised on a master server |
   | an explicit non-loopback bind address (`-address`, or an `Address` key) | **refuse** — a deliberate public bind. Stock V Rising has neither key and listens on every interface, so `0.0.0.0` / `::` / absent is the *default*, not a decision, and does not refuse on its own |
   | the process is not a **private instance** (below) | **refuse** |
   | otherwise | **allow**, with a warning naming what was read |

   A **private instance** means one of: an `_testserver` directory segment in the persistent-data path, the executable path or the settings path; or a `progenitor-hosted` token in the persistent-data path, the executable path or the `-saveName` value. Only those; the raw command line is *not* scanned, so `-logFile "D:\logs\_testserver.log"` or `-serverName "progenitor-hosted"` on a stranger's server does not qualify.

   **The live-server fence resolves canonical paths** through `LivePathFence`. It normalizes prefixes and dot segments, resolves local administrative shares, expands supported short names, and follows existing links to their final target. Unresolved short-name aliases on the protected volume are refused. Do not bypass a refusal by respelling the path.

   This gate deliberately does not:

   - **"Not listed" is not "private".** `ListOnSteam=false, ListOnEOS=false` means *not advertised*, not *not reachable* — a whitelisted or password-protected community server on a public IP reads exactly like that. So the private-instance test is an independent requirement on **every** server process, not a fallback consulted only when the settings file is unreadable. Before, it was conjoined with "no settings found", so any parseable settings file waived it and an internet-facing server never had to look like a Progenitor instance at all.
   - **An uninformative settings file is not consent.** A file counts as *read* only when it actually carries a `Port` **and** at least one of `ListOnSteam` / `ListOnEOS` as a real boolean. `{}`, `{"Name":"x"}`, `{"Port":9876}` alone, and `"ListOnSteam": 1` or `"yes"` (which is not a boolean) all mean *unknown*, and unknown is refused unless the path is one of ours. With the layering, the merged files must carry the two keys (the shipped default carries both), and the refusal says which case it hit. Before, any JSON object satisfied the gate — including a stray `ServerHostSettings.json` in the game root, which is not the file the server actually uses (and is no longer read at all).

3. **Loopback bind and a locked-down token.** The bind address must be loopback (it already is). The token file's ACL is rewritten to one entry — this user, full control, inheritance off — through `System.Security.AccessControl` reached by reflection: `FileSecurity` / `FileSystemAclExtensions` are only in the reference pack for the `net6.0-windows` TFM and the plugin targets plain `net6.0`, while the assemblies themselves do ship in the runtime BepInEx loads.

   **Verify token access from `token acl=`.** The label reflects the access-control read-back:

   | State | Meaning |
   |---|---|
   | `restricted` | the DACL was re-read and grants exactly the current user, with inheritance off |
   | `unverified` | the tightening ran without throwing but the read-back could not confirm it (the ACL assemblies are missing, the file system carries no ACLs, or the DACL still grants somebody else). A warning names which |
   | `default` | the tightening itself threw, or there is no token file (session-only token). A warning names the exception |

   The bridge starts in all three cases and reports the access-control result. Only `restricted` establishes successful read-back; investigate `unverified` or `failed` instead of treating them as equivalent.

Exactly one line says what happened:

```
Dev bridge: enabled (opt-in=marker, bind=127.0.0.1:27342, token acl=restricted)
Dev bridge: refused (no opt-in; create C:\...\BepInEx\config\progenitor-dev.enable with the opt-in line documented in docs/bridge.md ("The opt-in marker"), or set PROGENITOR_DEV=1)
Dev bridge: refused (dedicated server is listed on a master server (ServerHostSettings.json ListOnSteam=true); the debug bridge is full control of this process and never runs on a public server)
Dev bridge: refused (server process that does not identify itself as a Progenitor test (_testserver) or hosted (progenitor-hosted) instance...; ServerHostSettings.json says it is not listed, but not-listed is not private — a whitelisted server on a public IP reads the same)
```

The refusal log omits the opt-in sentence. Use the documented marker procedure above; do not copy secrets into logs.

`enabled` is Info, `refused` is Error, and the server warning is a Warning line. `Bridge.Enabled=false` in `progenitor.dev.cfg` short-circuits with `bridge disabled by config` and returns before operation hooks install.

The gate decision is a pure function over gathered facts (`DevGate.Evaluate`), covered by `tests/Progenitor.Tests/DevGateTests.cs`; nothing in `DevGate.cs` touches BepInEx or the game.

**Build-time gate.** Both extra-instance authentication shims compile only under `PROGENITOR_EXTRA_INSTANCE`. The client shim is `Ops/ExtraInstanceAuth.cs`; the server shim is `Ops/ExtraInstanceServerAuth.cs` and accepts only the reserved test platform-id range `90000000000020000..+99`. The runtime opt-in and private-instance checks still apply.

The constant hangs off a property of its own, `ProgenitorExtraInstance`, so the boundary is a named switch rather than a side effect of `$(Configuration)`. It defaults to `true` in Debug and `false` everywhere else. `tools/deploy.ps1` builds the solution with the default configuration, so the dev loop keeps it; `tools/package.ps1` builds `-c Release`, so a packaged Dev DLL cannot contain it, and `package.ps1` asserts that (plus: no `Progenitor.Dev.*`, no `*.token`, no `progenitor-dev.enable` in the staged tree or the finished zip) instead of merely omitting the file. To prove a build without it: `dotnet build src/Progenitor.Dev -p:ProgenitorExtraInstance=false`.

## Calling without MCP

Use Node from the repository root. The tool reads the current token file for each call; do not copy a token into examples or logs.

```powershell
node tools/mcp/call.mjs server ping
node tools/mcp/call.mjs server reload_status
node tools/mcp/call.mjs server ops
```

For JSON arguments, use a UTF-8 file. This avoids native-argument quoting differences between PowerShell versions:

```powershell
$bridgeArgs = Join-Path $env:TEMP 'progenitor-log-args.json'
[System.IO.File]::WriteAllText($bridgeArgs, '{"max":20,"filter":"Reload"}', [System.Text.UTF8Encoding]::new($false))
node tools/mcp/call.mjs server logs "@$bridgeArgs"
```

Expected result: `ping` describes the running test process, `reload_status` reports the current Core state, `ops` lists available operations, and `logs` returns a bounded log result. These calls do not request a reload. They require a running, opted-in test bridge; a refusal or missing bridge is not a pass.

The [operation reference](bridge-ops.md) provides JSON request/response shapes. Mutating operations require the corresponding test authorization and their own result checks.

Exit codes: `0` success (pretty-printed JSON, or the raw string if `result` is a string); `1` bridge `ok: false`; `2` usage (`side` or `op` missing); `3` timeout; `4` connection failed. A missing token file or invalid `jsonArgs` throws from Node. `exec` is MCP-only (`progenitor_exec`); it is not a bridge op.

Connection and timeout settings:

| Variable | Default |
|---|---|
| `PROGENITOR_GAME_DIR` | `C:\Program Files (x86)\Steam\steamapps\common\VRising` |
| `PROGENITOR_TEST_ROOT` | `<testserver>` |
| `PROGENITOR_CLIENT_PORT` / `PROGENITOR_SERVER_PORT` | 27341 / 27342 |
| `PROGENITOR_CLIENT_TOKEN_FILE` | `<V Rising>\BepInEx\config\progenitor-dev.token` (extra clients: `%TEMP%\progenitor-client-N\progenitor-dev.token`) |
| `PROGENITOR_SERVER_TOKEN_FILE` | `<test root>\server\BepInEx\config\progenitor-dev.token` |
| `PROGENITOR_CALL_TIMEOUT_MS` | Per-call override; takes precedence over `PROGENITOR_TIMEOUT_MS` |
| `PROGENITOR_TIMEOUT_MS` | 30000 milliseconds, including connection and reply |
| `PROGENITOR_AGENT` / `PROGENITOR_SESSION_NAME` / `PROGENITOR_SESSION_ID` | Unset (detected). Override the `who` identity sent to the bridge ([Bridge users](#bridge-users)) |
| `PROGENITOR_CALL_NOTES` | Unset. `1` prints the bridge-sharing note to stderr even when stderr is not a terminal |
| `PROGENITOR_QUEUE_FILE` | `<workspace>\_build\bridge-queue.json`. The shared [bridge queue](#bridge-queue) |
| `PROGENITOR_QUEUE` | Unset (enforced). `off` stops the queue from refusing ops (single-agent machines); ops on a side another session holds are still recorded on that holder and sent to it as a notice |

## Main thread

`ping`, `reload`, `core_reload`, `users`, `worlds`, `command`, `chat_observe`, `ghost`, `query`, `get`, `set`, `components`, `spawn`, `prefab`, `buff`, `heal`, `teleport`, `give`, `inspect`, `snapshot`, `restore`, `players`, `status`, `save`, `world_save`, `store`, `connect`, `host`, `host_capture`, `capture`, `perf`, `mods_ui`, `keybind_press`, `map`, `key`, `disconnect`, and `quit` are marshaled onto the game main thread. Core drains the queue once per frame from `FrameTicker` (and from the bootstrap-system postfix once a world exists). The socket thread waits up to **30 seconds**; a timeout fails that waiter and **abandons** the original work (it will not run a second time) but **does not free the slot** until that lambda completes, skips, or is dropped, so a retry cannot double-execute. `logs`, `ops`, `bridge_status`, and `bridge_users` run on the socket thread (`bridge_status` and `bridge_users` never take the one-op gate). `ping` / `reload` / `connect` / `host` / `host_capture` / `quit` work from the client main menu (`connect` and `host` must **not** have a client world, and they share a 120 s launch-in-flight slot — a second call while `LaunchGame` has started but `Client_0` is not up yet is an error). `host_capture` is fine on the menu (and during the loading screen). Ops that touch entities need a world. `status`, `save`, `world_save`, and `store` do not take an entity id; they still run on the main thread because they read Core module state (`world_save` also calls into the game's save trigger). `quit` does not refuse: it requests `Application.Quit` even in a world.
