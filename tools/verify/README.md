# Unattended verification cases (server half)

Job **FRIG1**, roadmap M2.13 item 13. Every "live check" in the evidence log from 2026-09-05 is the
same hand-run sequence: create a ghost admin, send a few chat commands through the Dev bridge, wait,
grep the log ring for the lines the feature is supposed to write. `tools/verify.ps1` is that
sequence as a script, and the JSON files beside this one are the sequences.

```
powershell -NoProfile -File tools/verify.ps1 -Case all
powershell -NoProfile -File tools/verify.ps1 -Case config-watch
powershell -NoProfile -File tools/verify.ps1 -Case all -Json -OutDir C:\Temp\progenitor-verify
powershell -NoProfile -File tools/verify.ps1 -ListCases
```

Exit code `0` only when every step of every case passed; `1` when a step failed; `3` when the bridge
did not answer or a precondition (node, a case path, the ghost op) failed.

## What the runner does, in order

1. Fences `-TestRoot`, every path derived from it and `-OutDir` through
   `tools/common/Fence.ps1` (`C:\VRisingServer` under any spelling is refused).
2. `ping`s the side and prints worlds, uptime and the ring head.
3. Resolves **one ghost admin for the whole run**, before the first case, if any case in the run
   declares `requires.ghostAdmin` — reusing one that is already there and leaving it entirely
   alone, otherwise creating one and remembering the index it created.
4. For each case: reads the ring's current sequence, prints the run's ghost index, and runs the
   steps.
5. Prints one `PASS` / `FAIL` / `INFO` / `SKIP` line per step, with the matched log line.
6. Restores every file a case edited from a copy taken before the first edit, then destroys the
   ghost **only if this run created it**, and only after a fresh `ghost list` answers and holds
   exactly the index it created and nothing else.

**The ghost's scope is the run, not the case** (R69 finding 1). It used to be resolved per case,
and every one of the eight cases declares `requires.ghostAdmin` while `-Case` defaults to `all`:
case 1 created ghost 10001 and set the "this run made it" flag, case 2 listed, found 10001 — an
admin ghost, because case 1 had asked for one — took the reuse branch, **cleared the flag** and
printed "this run will not destroy it" about a ghost ninety seconds old, and the run's cleanup then
returned at its first line. The rig left its own ghost on the test save on every default run, said
nothing, and the fail-closed sweep below was reachable only from a single-case run. A case now
neither creates nor destroys a ghost — `Invoke-Case` reads the run's index and throws if there is
none — and the reuse branch additionally refuses to clear the flag over an index this run created.
Resolving once also drops seven `ghost list` round trips off the game's main thread per run.

An unanswered `ghost create` is treated as a create that may well have happened (R69 finding 10):
`call.mjs` destroying its socket does not cancel an op already queued on the main thread. The
runner re-lists before throwing, and because it only ever creates when the list was empty, an admin
ghost found by that second list is **adopted** — index recorded, flag set, destroyed by the run's
own cleanup like any other. If the second list cannot answer either, the throw says a ghost may
exist and that `ghost list` must be run by hand.

The runner never starts, stops, restarts, deploys, builds or promotes anything, and opens no window.
Its case-callable bridge ops are allowlisted in the script (`$AllowedOps`): reads, `command` and
`chat_observe` — `quit`, `host`, `connect`, `core_reload`, `reload`, `save`, `world_save`, `spawn`,
`set` and the rest are refused by the runner itself, not merely unused.

**`ghost` is not case-callable.** It writes and destroys world entities, and the runner owns the
ghost lifecycle, so it lives in `$RunnerOps` and only the runner's own two calls reach it
(`Invoke-Bridge -Runner`); an `op` step naming it is refused with a message pointing at
`requires.ghostAdmin`. That is also what keeps the destroy honest: the bridge's `ghost destroy` is
all-or-nothing over the whole ghost PlatformId range (`docs/bridge-ops.md`), with no per-ghost form,
so the runner **refuses to create a ghost at all while a ghost it did not make is on the server**
(it stops the run and names them) and re-checks the list before the sweep. A maintainer's own ghost
— the non-admin second "player" of a chat-broadcast check — is never taken by a rig run.

**The guard fails closed on "I could not find out."** The bridge serves one main-thread op at a
time, so a `ghost list` that loses a race with a concurrent op is ordinary rather than exceptional,
and R68 finding 3 was the run where that answer read as permission: the rig created its ghost
beside the main session's 10001 and then swept the whole PlatformId range under a line saying
"created by this run". Now an unanswered or malformed list (non-zero exit, no JSON, `ok=false`, no
`ghosts` member, or a row with no integer `index`) is retried three times a second and a half
apart, and then

- **before creating**: the run stops with exit `3` and says the bridge must answer first;
- **before destroying**: the sweep is skipped with a warning naming the index left behind, exactly
  as the "somebody else's ghost is here" branch does.

The destroy runs only when a fresh list answered *and* holds exactly the one index this run
created. An empty list means the ghost is already gone and there is nothing to sweep.

## The match window

Expectations are evaluated against the log ring, not the disk log. The **window** opens at the most
recent `command`, `op`, `editFile` or `resetWindow` step (so that step's own output is inside it) and runs to
now;
`sleepMs` and expectation steps do not move it. Two other scopes exist:

- `"scope": "case"` — everything since this case started. The default for `expectNoLog`.
- `"scope": "ring"` — the whole ring, for lines written at plugin attach rather than by a command.

Each line is matched as `[<Level>] <Source>: <text>`, so `"\\[Error\\]"` is a severity test and the
message text matches as usual. Patterns are .NET regexes, case-insensitive (PowerShell `-match`).

## Step kinds

| Step | Shape | Meaning |
|---|---|---|
| `note` | `{"note": "..."}` | Printed as `INFO`. Where the evidence citation goes. |
| `command` | `{"command": ".cfg status"}` | Injects a chat line as the ghost admin (`userIndex`), or as `{"userIndex": n}` if the step names one. Fails if the bridge does. Opens a new window. |
| `op` | `{"op": "users", "args": {...}, "expectJson": "regex"}` | Any allowlisted read op; `expectJson` is matched against the printed JSON. Opens a new window. |
| `sleepMs` | `{"sleepMs": 1500}` | Wait. |
| `resetWindow` | `{"resetWindow": true}` | Moves the window to the ring head and does nothing else. For a case whose **precondition** steps write log lines of their own: it says "everything above was setup" without injecting a dummy command to buy the same effect. |
| `editFile` | `{"editFile": {"path": "<config>/x.cfg", "replace": "regex", "with": "text", "count": 1}}` | Regex replace in a file under the test server. Backed up before the first edit and restored at the end. `count` is the minimum number of matches required (default 1). Opens a new window **before** writing, so the reload the edit causes is inside it. |
| `expectLog` | `{"expectLog": "regex", "within": 400, "count": 1, "minCount": 1, "scope": "window", "filter": "..."}` | `count` asserts an exact number of matching lines, `minCount` a floor (default 1). `within` looks only at the first N lines of the window. `filter` is passed to the `logs` op (a case-insensitive substring), which is what makes a `ring` scope cheap. |
| `expectNoLog` | `{"expectNoLog": "regex", "scope": "case"}` | Fails if anything matches. |

Path placeholders in `editFile`: `<config>`, `<reload>`, `<server>`, `<testroot>`. A path that
resolves outside `-TestRoot` is refused.

## Cases

| Case | Order | Steps | Evidence | What it proves |
|---|---|---|---|---|
| `config-watch` | 10 | 33 | 207, 209, 214, 222 | `.cfg status` names both files and both bound keys; after `.cfg watch on` one hand edit produces **exactly one** `Watch.Verbosity: Normal -> Loud` reload line, exactly one `ConfigHelper: ignoring the write-back`, and exactly one `ConfigHelper: reloaded <path>` — the last is FCFG8's "one edit, one reload" (evidence 207 saw two reloads per edit), asserted on Core's line rather than the Sample's because `Reloaded` is dispatched per live instance; `.cfg read` reads the new value; `.cfg watch off` unsubscribes. **Known-failing on a Core without `ec16f76`** — see below. |
| `castles` | 20 | 21 | 208 | `.castle all` / `territory 13` / `tiles` / `canplace <guid>` / `raw` / `territory 99` each write their reader line. |
| `systems` | 30 | 20 | 208 | `.sys on` adds three virtual systems at three anchors; twelve seconds later all three counters are non-zero and `Describe()` reports `groupFired=True`; `.sys order` dumps the hook order; `.sys off` disposes exactly three. |
| `ecs` | 40 | 21 | 208, 211, 213 | The user census; `ServerRuntimeSettings` raw read; the **tag-component refusal** (`found=True raw=False tag=True`) with Core's `[Warning] … refused, zero-sized (tag) component` present and the engine's exception absent (FECS1); the `Add/Write/Read/Remove/Destroy` walk. |
| `handshake-net-store` | 50 | 20 | 212 | The attach-time handshake row plan and own-event-id lines (ring scope); `.net reserved` answers RESERVED for `0x50726848` and free for `0x536D4E70`; `.store dir` names the save folder and this plugin's JSON; `.store flush` writes it and reads the bytes back. |
| `content-edit` | 60 | 22 | 213 | `.content define` does the typed write `Default(0) -> Consume(2)` and reads it back; `.content drop` retires the definition; the **second** `.content define` prints `Default(0)` again, which is the FEDIT1 baseline restore. |
| `rifts` | 70 | 12 | 203 | `.rifts send` writes the SmRf line with a freshly read schedule, and a `reason=heartbeat` broadcast lands inside sixty-five seconds. |
| `patch-kinds` | 90 | 24 | 208 | `.patchdemo on` lands prefix, postfix and finalizer on one live site; `.patchdemo ping` fires all three; `.patchdemo off` logs three unpatch lines, the Sample's `RemoveAll removed=3` and Core's `patches: RemoveAll owner=… removed=3 left=N`. |

`-Case all` runs them in the `order` above. `patch-kinds` is last on purpose: `.patchdemo on` pins
the Sample plugin's ALC for the rest of the process.

## Writing a case

Transcribe the expectation from the evidence entry, not from memory, and keep the regex as close to
the logged text as the run allows. Two rules learned writing these:

- **Save-dependent numbers are not expectations.** Evidence 208 read `all count=1 owned=0` and
  `raw plots=1 read=1` on `test1`; those counts belong to that save, so the case asserts the shape
  and prints the run's own numbers in the matched line. What is asserted exactly is what the *code*
  decides: `removed=3`, `Disposed 3`, `Default(0) -> Consume(2)`, `raw=False tag=True`.
- **A case establishes its own precondition, and proves it did.** The first `-Case all` run
  (2026-09-05, generation 44) lost three steps to this: `config-watch` assumed the cfg started at
  `Verbosity = Normal`, a hand test had left it at `Loud`, and the payload edit therefore wrote
  Loud over Loud and logged `Watch.Verbosity=Loud (unchanged)`. A precondition is a step, not an
  assumption: match whatever is on disk (`Verbosity = (Normal|Loud|Quiet)`), put it where the case
  needs it, wait past the debounce **and** the self-write window, assert that the reset landed, and
  `resetWindow` before the assertions that count lines. Order matters too — the reset must run
  after the watch is armed, because the Sample's "before" value is refreshed by its own handler.
- **A negative must not be able to match the positive next to it.** `ecs` asserted
  `expectNoLog /zero sized component/` to mean "the engine was never reached", and it matched
  Core's own `refused, zero-sized (tag) component` — the line the same case asserts is *present*.
  Name the failure, not a substring shared with the success: `Il2CppException`, the engine's
  `This operation can not be called with a zero sized component`, an `[Error]` level.
- **Count the line the framework writes once, not the line a subscriber writes per instance.**
  `config-watch` asserted `Sample config: reload #` exactly once to mean "one edit, one reload".
  Under FCFG12 `ConfigHelper` dispatches `Reloaded` once per **live registered `ConfigFile`
  instance** for the path, and the Sample's handler distinguishes files only by
  `ConfigFilePath` — which two instances of one path share by construction — so on a hot-reloaded
  Sample one edit writes one `reload #N … Normal -> Loud` and then one `… Loud (unchanged)` per
  extra live instance. The Sample's line counts instances; Core's `ConfigHelper: reloaded <path>`
  (singular, or `… into N registered ConfigFile instances`) is written once per reload whatever the
  instance count, and is what the exactly-one assertion now names (R69 finding 4). The transition
  line stays exactly-one for a different reason: only the first dispatch sees a changed value.

- **An expectation comes from a log, never from a doc.** `patch-kinds` took
  `RemoveAll ... removed=3 left=0` out of `docs/bridge-ops.md`. `removed=3` is what the code decides;
  `left` is the whole registry's remaining count with every other owner in it (evidence read
  `left=4`), so it is a `\d+`. The doc was the thing that was wrong, and was fixed.
- **A reply that never reaches a log is not checkable here.** A ghost has no chat slot, so
  `ctx.Reply` alone is invisible; the Sample's `SampleLog.Info` lines are what these cases grep.
  `.hs` and `.rifts` (status) are the two commands whose replies are chat-only — they still owe a
  real user, and the cases say so in a `note`.

## Known failure modes that are not rig defects

- **`config-watch`, three steps, on a Core without `ec16f76` (FCFG11, evidence 222).** The config
  watch reloaded a stale `ConfigFile` left behind by a previous plugin instance, so on any server
  where the Sample has been hot-reloaded since the Core generation started — which, on a server
  whose Core swaps are refused, is every run after the first — the payload edit lands in a dead
  instance and the three counted steps cannot pass. The case declares this itself in a top-level
  `knownFailing` field with `"clearsWhen": {"coreCommitAtLeast": "ec16f76"}` beside it; the exit
  code is unchanged, because a known failure is still a failure. Cleared by a Core generation
  carrying `ec16f76` (swap or restart onto it), not by editing the case — and once that generation
  is the one running, the runner says so itself, as `STALE`. The case also carries
  `knownFailingSteps`, so a failure outside those steps (the Sample not loading at all, say) is
  reported as unexplained rather than folded into this excuse.
- **The counted assertions are written for the FCFG12 contract, not against it.** Under `ec16f76`
  the live instance is reloaded, so the payload edit does land; what changes is that `Reloaded`
  fires once per live instance, which is why "one edit, one reload" is asserted on
  `ConfigHelper: reloaded <path>` and not on `Sample config: reload #` (R69 finding 4, and the
  rule two sections up).
- A `ring`-scoped expectation whose line has aged out of the 4000-entry ring (reload the plugin and
  re-run).
- A case run against a server where the Sample is not loaded at all (every `Sample …` expectation
  fails at once).

## `knownFailing` and `clearsWhen`

A case may declare `"knownFailing": "…"` at the top level: one sentence naming the job and the
evidence entry, and the condition that clears it. Say what makes it pass again, not just that it
fails — the point of the field is that nobody re-diagnoses a known defect as a mis-authored case.

**The clearing condition is carried as data next to it, in `clearsWhen`** (R68 finding 7). A note
printed only on failure survives the fix silently, and the next real regression is then explained
away by a paragraph pointing at a commit that is already in. `clearsWhen` is an object; every key
present must hold, each key evaluates to yes / no / **unknown**, and only an unambiguous yes from
every key clears the note — an excuse is retired on evidence, never on a failed lookup.

| Key | Holds when |
|---|---|
| `describe` | prose for the report; not a condition |
| `coreCommitAtLeast` | the sha is an ancestor of the repo's `HEAD`, **and** the Core image under `-TestRoot` is not older than that commit, **and** the running generation loaded that image (image write time at or before the moment the live generation was loaded, from `lastReload.timestamp` or process start). A staged-but-refused `Progenitor.dll.next` therefore reads as *unknown*, not as "the fix is live". |
| `coreGenerationAtLeast` | `ping`'s `reload.generation` is at least this |
| `coreVersionAtLeast` | the side's reported version is at least this |
| `logMatches` / `logAbsent` | a regex over the whole ring matches / does not match; `logFilter` narrows the `logs` op |
| `manual` | never — a deliberately unevaluable condition, reported as its own text |

**A note may also say which steps it excuses,** as `knownFailingSteps` beside it: a list of step
indices (1-based, as printed on the `FAIL` line) and/or regexes matched against the step's printed
description. R69 finding 9: the verdict used to be decided by `clearsWhen` alone and never by which
steps failed, so a case failing for a completely unrelated reason was reported as the excused one.
That is what happened to the `config-watch` run this rig was measured against — the Sample plugin's
`OnLoad` had thrown, the plugin was not loaded at all, every `expectLog` from step 3 onward failed
for that reason, and the operator was handed the FCFG11 stale-`ConfigFile` story about a mechanism
that was not running. Prefer regexes to indices: an index moves whenever a `note` is inserted above
it.

What the runner prints, with the exit code unchanged in all four cases:

- **`KNOWN FAILING`** — the case failed and the condition does not hold (or could not be
  evaluated). The note, the evaluation of each key and the **failing step numbers** are printed
  under the case and in the summary. When the note declares no `knownFailingSteps`, the runner says
  so: every failing step is being excused by prose.
- **`KNOWN FAILING (plus N unexplained step(s))`** — the case failed, the note names steps, and at
  least one failing step is not among them. Those steps are listed on their own lines: the excuse
  says nothing about them and they need diagnosing.
- **`STALE knownFailing note`** — the case failed and the condition **holds**. This failure is not
  the known one: re-diagnose it and remove the field.
- **`STALE knownFailing note` (passed)** — the case passed while carrying a note. The excuse
  outlived the defect; remove the field.

The exit code is deliberately untouched: a known failure is still a failure (`1`), and a stale note
is a documentation defect, not a run failure. The summary names every stale case, and `-Json`
carries `KnownFailingVerdict`, `KnownFailingStepsDeclared`, `FailedSteps`, `UnexplainedSteps`,
`ClearsWhenState` and `ClearsWhenReason` per case plus a top-level `staleKnownFailing` list.

## Not covered here

The client half of roadmap item 13 — an extra client instance, `tools/observe.ps1` input and
screenshots, and the checks that need a human eye — is still open.
