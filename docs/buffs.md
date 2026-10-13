# Timed buffs

Apply, query, refresh and remove a timed buff a plugin defined, through the game paths Progenitor
already proves. Signatures: [`api.md`](api.md) "Buffs". Worked examples:
[`samples/Progenitor.TimedBuffExample`](../samples/Progenitor.TimedBuffExample) and
[`samples/Exercise.TimedBuff`](../samples/Exercise.TimedBuff).

**Status on `main`: landed (port), lifecycle verified live; drain effect not yet measured on `main`.** **Live on `main` (2026-10-02, isolated test server and Steam client, PR #118 port, Core 2026.10.26):** apply (`Requested` then `Active`), a second apply refused `AlreadyActive`, the instance read back with `LifeTime.Duration` 30, `Age` ticking and the declared drain stat, expiry noticed by the API, early removal, a refresh shorter than the elapsed time (`.rested 30` at about 47.6 s elapsed set `Duration` 77.6 = elapsed + 30, and the buff stayed on), and a server Core hot swap with `.rested` and Adrenaline Rush both on: both removed before their plugins unloaded, both entities gone and no `BuffBuffer` row left, server on the same process. **Not measured on `main`:** the drain number itself (`Blood.LossPerSecond` read 0 for the idle test character even without buffs), the HUD icon and timer, chat reply wording, and a plugin-only swap.

 The module was ported onto current `main`
from the 2026-09 buff branch: source-checked against main's helpers, compile-verified (Core and both
samples, Debug and Release) and game-free tested. The same buff code was live-qualified on
2026-09-14 on the pre-port branch, on the isolated pair: apply and expire against a recorded drain
baseline, modifier count for both scopes, movement untouched, overlap, manual removal, refresh short
and long, and plugin unload with a live buff with the server surviving on the same process. That run
predates the port and does not qualify it; "Later live procedure" below is the sequence that would.
`RefreshTotalOutOfRange`, `RefreshWriteFailed`, `RefreshNotActive` and `RefreshEndActionUnsupported`
were never reachable through the samples live and are covered offline only.

## The whole workflow

```csharp
// On the plugin class. REQUIRED before ctx.Buffs.Stats will write anything — see
// "Declaring network compatibility" below. Without it the buff applies but does nothing.
[NetworkCompatibility(NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact)]
public sealed class Plugin : ProgenitorPlugin<MyContext> { … }

// Once, at load.
ctx.Content.DefineBuff("example.rested")
   .CloneOf("Buff_General_Haste")     // a verified vanilla base
   .Named("Rested")
   .Described("Your blood does not drain while this lasts.")
   .Register();

ctx.Buffs.Stats("example.rested", BuffStat.BloodDrain(BuffStatOp.Set, 0f));

// Per use, on the server, on the game main thread.
IBuffApplication application = ctx.Buffs.Apply("example.rested", characterEntity, seconds: 30f);

if (application.State == BuffApplicationState.Refused)
    reply(application.RefusalReason);          // one actionable sentence
else
    application.OnStateChanged += a => { /* Active, then Ended / Removed / TargetLost */ };

// Later, or never — unload removes it either way.
application.Remove();
```

There is no teardown to write. `ctx.Buffs` tracks every application and the context removes them on
every unload reason — silently, without raising `OnStateChanged`, so do not put cleanup your plugin
needs inside that handler. See "Death, disconnect, unload, world reload" below.

## Declaring network compatibility — required for `Stats`

**`ctx.Buffs.Stats` does nothing unless your plugin class carries**

```csharp
[NetworkCompatibility(NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact)]
```

`Stats` writes the stat component onto your buff clone through `ctx.Content.Edit`, and
`PrefabRegistry` refuses a `Content.Edit` from a plugin that has not declared this attribute — the
edited values travel to Progenitor clients as a server layer, so both sides have to be required to
apply the same pass. `Minor` works as well as `Exact`.

**The failure is quiet, which is why it is called out here.** Without the attribute the definition
still registers, `Apply` still succeeds, the icon still appears and the application still reports
`Active` — but the instance carries **no stat modification at all**. You get a buff that does
nothing. The only signal is one line in the server log:

```
PrefabRegistry: refused edit from <your.plugin.guid> on guid <n>: Content.Edit requires
[NetworkCompatibility(ServerMustHave, Exact or Minor)] so both sides apply the same pass;
this plugin declared none.
```

`Stats` returns a plain `IDisposable` and cannot currently report this back to the caller, so
**check the server log the first time you add a stat to a buff.** This was found in live acceptance
on 2026-09-13: both worked examples and an independently written plugin all shipped without the
attribute, applied a buff that looked correct in every observable way, and changed nothing about the
wearer.

## Requested is not active

`ctx.Buffs.Apply` goes through the game's own debug apply
(`DebugEventsSystem.ApplyBuff` with an `ApplyBuffDebugEvent` and a `FromCharacter`), which **queues
an event**. The game builds the buff entity on a later update. So `Apply` returns a handle in
`Requested`, and the module polls the target's `BuffBuffer` once a frame until it sees the instance
and moves to `Active`. The budget is `BuffApplicationPolicy.ConfirmFrames` = 8, the same measured
window as `PrefabLiveRefresh.RestoreDurationFrames`; past it the application is `NotConfirmed` and
nothing was created.

Do not tell a player the buff is on until `Active`. The example's `.rested` reply says so.

| State | Means |
|---|---|
| `Refused` | Nothing was asked of the game. `Refusal` / `RefusalReason` say which check failed. |
| `Requested` | `ApplyBuff` queued, instance not observed. Owns nothing yet. |
| `Active` | The instance was found in the target's `BuffBuffer`. |
| `Ended` | Confirmed, then the instance disappeared and this application did not remove it. |
| `Removed` | This application removed its own instance (`Remove`, `Dispose`, unload). |
| `TargetLost` | The target entity stopped existing, or the world was replaced under it. |
| `NotConfirmed` | Queued, confirm window spent, no instance. The apply did not take. |

`Ended` is **one observation covering three causes** — the duration elapsed, the wearer died and the
game cleared its buffs, or another source destroyed the instance. This path cannot tell them apart
and does not pretend to.

## Duration

`WithDuration(seconds)` is written onto the confirmed instance's `LifeTime.Duration`. It must be
finite, greater than zero and at most `BuffApplicationPolicy.MaxDurationSeconds` (10000, the range
the game's own `[SyncRangedFloat]` declares on that field). Omit it — or call `WithPrefabDuration()`
— to keep the duration the cloned vanilla buff carries.

`LifeTime.EndAction` is **never** written: what happens when the timer reaches zero is the base
buff's contract. This is why the base must be a buff that already expires on its own. Clone one that
does not and the duration write is refused with that reason; the buff still applies, with the
clone's own timer, and the log says so rather than reporting a duration nothing established.

The countdown is the game's, not a timer in Progenitor. `RequestedDurationSeconds` is what was
asked for, not what remains.

## Refresh, and why there is no stacking

`BuffRefreshPolicy.Reject` (the default) refuses a second apply of the same buff on the same target
by the same owner. `BuffRefreshPolicy.Refresh` rewrites the live instance's duration and returns
**the application that is already live** — not a second handle onto one instance.

### `Refresh(n)` leaves `n` seconds remaining

The game keeps elapsed time in a **separate component**. `LifeTimeSystem` iterates
`RefRW<Age>` together with `RefRO<LifeTime>` on the server, and `Age` is its own
component — `public struct Age : IComponentData { public float Value; }`. So
`Age.Value` counts up and `LifeTime.Duration` is the **total** it is measured against.

`Refresh(seconds)` therefore reads `Age` and writes `elapsed + seconds` into `LifeTime.Duration`.
It writes **no component the previous build did not already write**: one extra read, the same single
`LifeTime.Duration` write.

| You call | What happens |
|---|---|
| `Refresh(n)` | the buff has `n` seconds left from now, whatever was already elapsed |
| `Refresh(n)` repeatedly | still `n` seconds left each time — the total does not ratchet upward |
| `Refresh(n)` where `elapsed + n` > 10000 | **refused**; the instance keeps the duration it had |
| `Refresh(n)` on a buff whose `EndAction` is `RemoveBuffStack` | **refused** (see below) |
| `Refresh(n)` where `Age`/`LifeTime` cannot be read | **refused**; nothing is written |

`Age` is **never written.** It is not a `[NetworkedComponent]` and has no snapshot type
in the game's metadata, and the client runs its own `AgeSystem` integrator, so zeroing it on the
server would leave the wearer's HUD timer running against a number the server no longer holds. A
"restart the clock from zero" refresh is therefore still not offered; remove and re-apply if that is
what you mean.

#### What a refusal means here

A refused refresh **writes nothing** — no clamping, no partial write, no guess. The instance keeps
the duration it already had.

On the `ApplyOrRefresh` route the handle you get back is the one that is **already live**, so its
`State` is `Active` and its `Refusal` is `None`: both describe the *original apply*, not the refresh.
Read **`LastRefreshRefusal`** (and `LastRefreshRefusalReason`) to tell a refresh that took from one
that was refused. It is `None` when the last refresh succeeded or none was attempted.

```csharp
var handle = ctx.Buffs.ApplyOrRefresh("my.buff", target, 30f);
if (handle.LastRefreshRefusal != BuffRefusal.None)
    ctx.Log.Warning(handle.LastRefreshRefusalReason);   // the duration was NOT rewritten
```

#### `RemoveBuffStack` is refused, not guessed

When `LifeTime.EndAction` is `RemoveBuffStack`, `Duration` is the length of one **stack period**, not
the instance's lifetime: the game removes a stack and restarts the counter
(`LifeTimeSystem.ReduceStacks` takes `ref Age`). Writing `elapsed + n` into it
would lengthen every later period, so repeated refreshes would ratchet upward. This API refuses that
case, and refuses an `EndAction` value this build does not recognize, rather than write a number
whose meaning it cannot state. `None`, `Kill` and `Destroy` all mean "`Duration` is this instance's
lifetime" and are refreshed normally.

`None` is the honest edge of that sentence: the write is well defined and matches what the old build
did, but a buff whose end action is `None` is not ended by its own timer at all, so a successful
refresh moves a number that decides nothing. It is allowed rather than refused because the write is
harmless and refusing it would break clones that rely on the duration being readable — but do not
read "refreshed" on such a buff as "it will now end in `n` seconds".

Two consequences of `Age` climbing without bound on such a clone, both by design:

- `Age` is never reset, so once it passes 10 000 s (≈ 2 h 47 m) **every** later refresh refuses with
  `RefreshTotalOutOfRange`, permanently. The refusal sentence already says to remove and apply again,
  which is the fix.
- Nothing in the game stops `Age` at `Duration`; `AgeSystem` increments it unconditionally.

> **The bug this replaced, live 2026-09-13.** `.rested 90`, then `.rested 30` about 31 s later. The
> old `Refresh` wrote the argument straight into `Duration`, `Age` was already past it, and the
> instance was gone on the next poll. The application reported `Ended`, correctly — the buff was
> simply over. Nothing offline could see it, because the test fake had no notion of elapsed time at
> all; it does now, and it expires instances at `Age >= Duration`.

Stacking is not exposed. `ApplyBuffDebugEvent` has a `Stacks` field and this API always sends 1,
because what a second stack does is authored per buff prefab and Progenitor has not verified those
semantics for a clone. An API that cannot describe a knob's effect should not offer the knob.

## Ownership: one instance, one owner

A second apply of the same buff on the same target by a **different** owner is refused
(`BuffRefusal.AlreadyOwned`), whatever the refresh policy. A stranger refreshing your buff would
silently retime it.

Two **different** buffs that modify the same stat are not a conflict at all. Each buff instance
registers its own modification through the game's `ModificationsRegistry` and the matching destroy
system reverses exactly that id when the instance dies. That is why removing one application leaves
another mod's contribution to the same stat untouched — the reversal is the game's, per instance,
and this API never writes the wearer's stat directly.

Nothing here writes a component on the **character**. No immortality, no immunity flags, no aggro
suppression: those live in the Sample's debug buff, they are protections rather than a lifecycle,
and they are deliberately not in this API.

## Removal is idempotent, and narrow

`Remove()` / `Dispose()` destroys the instance **this** application confirmed, on the target it was
applied to, in the world epoch it was applied in, with
`DestroyUtility.Destroy(..., DestroyDebugReason.TryRemoveBuff)` — the game's own removal. Everything
else is a no-op:

- calling it twice, or ten times
- calling it after the buff already expired
- calling it after the target died
- calling it after the world was reloaded (entity indices are re-issued, so a stamped target names a
  different entity; the application ends as `Removed` and writes nothing — the `TargetLost` answer
  is what the *poll* gives when it notices the epoch moved on its own)

**Cancelling before confirmation** is the one case that keeps working after you stop caring. The
apply is already queued, so `Remove()` on a `Requested` application marks it canceled and the module
keeps polling for the rest of the confirm window; an instance that arrives late is destroyed. Drop
that and a cancel leaves an orphan buff on a player.

### A destroy is an event — what you will see in the log

`DestroyUtility.Destroy` does not remove the entity on the spot. It raises an event the game consumes
on a later frame, so for a frame or two after `Remove()` the buff entity still exists and is still a
row in the wearer's `BuffBuffer`. Two consequences you can observe:

**1. Progenitor keeps watching, and may issue a second destroy.** The application handle goes
terminal immediately — that is its contract — but the module keeps the instance on an internal
retirement list until it sees the entity actually go. If it is still there after **2 frames** the
destroy is issued **once** more. If it is still there after **120 frames** the module gives up
watching and says so:

```
Buffs: destroy of buff id='…' instance=… on target=… was issued (and re-issued once) but the entity
was still there after 120 frames; giving up watching it. The buff may still be on the wearer.
```

A second `DestroyUtility.Destroy` on one instance is therefore **expected behavior, not a defect** —
it means the first had not taken yet. (This replaced an earlier implementation that forced the issue
by adding `Unity.Entities.DestroyTag` out of band. The game's own diagnostic says that tag may only
be added inside `ProcessDestroyEventsSystem`, and doing it here routed the entity around the detach
pipeline and crashed a live server on 2026-09-13.)

**2. Core uninstall reports what it could not confirm.** Two different lines, deliberately worded
apart:

- *Info*, `"was issued during Core uninstall"` — normal. The destroy went out microseconds before
  this generation stopped running; the game completes it afterwards.
- *Warning*, `"was issued and watched … no generation is watching it any more"` — the module watched
  this one across frames and the entity was still there. Worth looking at.

**Re-applying immediately after removing works.** The instance you just destroyed is still in
`BuffBuffer` during that window, but the module knows it is retiring it and does not mistake it for a
foreign or save-restored instance, so you do not get an `AlreadyPresent` refusal for your own buff.

## Death, disconnect, unload, world reload

| Event | What happens |
|---|---|
| Wearer dies / character entity destroyed | `TargetLost`. Nothing is written: the buff went with the entity. |
| Player disconnects | Nothing, by itself. The character entity survives a disconnect, so the application stays as it is and the buff is the game's to keep or clear. |
| `application.Remove()` | `Removed`, instance destroyed. |
| `ctx.Buffs.RemoveAll()` | Removes **only this owner's** applications, by reference — safe for retiring one subsystem. |
| Plugin unload (any reason) | The buff is removed, **silently**: no `OnStateChanged` is raised for it. Two routes reach that. An application from `ctx.Buffs.Apply` is tracked as a release token, so `ctx.UndoAll()` drops its handlers and then removes it — `UndoAll` runs while the unloading plugin's own handlers are still attached to an assembly that is going away, so publishing there would run code out of a collectible ALC mid-teardown. Anything the context did not track is then caught by the reloader's sweep, which removes what the plugin owns by owner reference, by an owner whose type its assembly declares, or by the BepInEx id recorded at apply time. Your own `Remove()` from a *loaded* plugin still publishes — that is what the event is for. |
| Plugin unload, handler on **somebody else's** application | Dropped, and only that. `Buffs.Find` finds an application whoever owns it, so subscribing to another plugin's application is a supported thing to do; on unload the sweep removes just the handlers written in the unloading assembly and leaves the stranger's application running. Without that, the delegate would pin the subscriber's collectible ALC for as long as that buff stayed live. |
| Core uninstall | Every live application is finished, including an unconfirmed one, which gets one last look for its instance. If a queued apply has still produced nothing, the log says so: after that unload no generation is watching for it. |
| World reload / save restore | The application stamps `WorldEpoch` at apply time and stops reading or writing once it moves. |

## Supported stat modifiers

**Blood drain is the only supported modifier on this build.** That is a short list on purpose: a
modifier belongs here only when this repository can say what the component does from its own code,
its own live evidence and the game's own apply/destroy systems.

| Modifier | Component | Status |
|---|---|---|
| `BuffStatKind.BloodDrain` | `ProjectM.ModifyBloodDrainBuff` | **Supported.** A plain value struct — two floats, three `ModificationId`s, two byte-wide `ModificationType` enums, two priority ints, three bools; nothing reference-shaped. On Core's wire-safe list with a per-field value policy (`PrefabEditWireTypes`). Progenitor's own Sample writes it and has been observed live stopping a vampire's blood drain. |
| `BuffStatKind.MovementSpeed` | `ProjectM.ModifyMovementSpeedBuff` | **Declared and refused.** See below. |

`BuffStat.BloodDrain(op, value, priority, scope)` writes `BloodValue`, `ModificationType` from `op`
(`Set` = 0, `Add` = 3, `Multiply` = 4) and `ModificationPriority`, and sets `AffectBloodValue`.
Whether it *also* writes the matching idle trio is the `scope` argument — see the next section, and
read it before choosing a non-`Set` op. The three `ModificationId` fields are left exactly as read:
on a prefab they are zero, and the buff's spawn system fills them on the **instance** so the destroy
system knows which contribution to reverse.

### One stat, one modification — and the scope that changes it

`BloodDrain` takes an optional `BloodDrainScope`:

| Scope | Writes | Modifications registered |
|---|---|---|
| `Active` (**default**) | active drain only; clears `AffectIdleBloodValue` | **one** — *expected, not yet observed live* |
| `ActiveAndIdle` | active **and** idle drain | **two, against the same value** — *confirmed live* |

**The asymmetry in that last column is deliberate.** *Two* is a measurement: the server logged two
modification ids for one instance and the arithmetic matched. *One* is an inference — that clearing
`AffectIdleBloodValue` stops the game registering the second modification. It follows from the
component's own shape and it is what the field is for, but **nothing offline can observe what
`ModifyBloodDrainSystem_Spawn` actually registers**, and this has not been read back on a live
server yet. Treat it as the designed behavior pending that check; it is the first item in the live
plan below.

This matters more than it looks. `ProjectM.Blood`'s active and idle drain are two views of one
modifiable value, so writing both registers two modifications that *compose*. Under `Set` that is
idempotent and harmless. Under `Add` or `Multiply` it compounds:

> **Live, 2026-09-13.** A single `BloodDrain(Multiply, 0.1f)` wrote both fields. The server logged
> two modification ids against `ProjectM.Blood+4` for one buff instance, and the wearer's drain went
> `0.005 → 5e-5` — that is `0.1 × 0.1 = 0.01×`, not the tenth the caller asked for.

`Active` is the default so that the number you write is the number you get. Ask for `ActiveAndIdle`
when you genuinely want idle drain affected too, and prefer `Set` with it.

**On `Add` and `Multiply` generally:** the `ModificationType` values are read from the game's metadata, and what
this API *writes* for each scope is pinned by test — but a test can only pin the write set, not what
the game does with it. The resulting *rate* has been observed only for `Set 0` and for the
compounding `Multiply` case above. Verify the rate you get rather than assuming it.

## Choosing a base, and what it brings with it

`DefineBuff(...).CloneOf(...)` clones **everything** the base carries — not just the parts you chose
it for.

`Buff_General_Haste` is the base both worked examples use, and it is the right choice for a visible
timed buff: it carries `ShowOnBuffBar` (so the clone appears on the action bar with an icon) and a
`LifeTime` with an `EndAction` (so a duration written onto the instance actually ends it). It also
carries **`ModifyMovementSpeedBuff`**.

> **Live, 2026-09-13.** Both examples granted **+50% run speed** that neither their name, their
> description nor their code mentioned. The server patched `ProjectM.Movement+92` from `6` back to
> `4` when the buff was removed. `Rested` promised only "your blood does not drain".

So: **inventory your base, and suppress what you did not ask for.**

```csharp
ctx.Buffs.Stats("example.rested",
    BuffStat.BloodDrain(BuffStatOp.Set, 0f, scope: BloodDrainScope.ActiveAndIdle),
    BuffStat.Suppress(BuffStatKind.MovementSpeed));   // drop the base's haste
```

`Suppress` removes the components the base carries for that stat from **your own clone**, on the
prefab only. It is subject to the same ownership rule as every other stat, and it is refused on a
live instance because there the `ModificationId`s belong to the game.

Note the deliberate asymmetry: **`BuffStatKind.MovementSpeed` is refused for writes but allowed for
suppression.** Writing a movement value needs three things nobody here has measured — what
`MoveSpeed` means against the wearer's `Movement`, what `MultiplyAdd` switches between, and what
`CurveReference Curve` resolves to. *Removing* the component needs none of them. Those are different
questions and they get different answers.

`Suppress(BuffStatKind.BloodDrain)` is **refused**: it is not a synonym for
`BloodDrain(Set, 0f)`, and silently aliasing it would hide which of the two you meant. To stop drain,
set it to zero; to leave the base's drain alone, omit the stat.

`BloodDrain` never writes **four** fields, and they are enumerated in `BuffDrainWrite.NeverWritten`
with a test asserting no scope/op combination emits any of them:

- the three `ModificationId` fields (`ModificationId`, `IgnoreIdleDrainModId`, `ModificationIdleId`)
  — the game issues these on the **instance** so its own destroy system can reverse exactly this
  contribution; writing one would hand the game an id it never issued;
- `IgnoreIdleDrainWhileActive` — not part of "how much does this wearer drain", and this API has no
  evidence about what changing it does.

Under `BloodDrainScope.Active` the idle *value*, *type* and *priority* are left alone as well; that
is a scope decision rather than a permanent prohibition, so those three are not in `NeverWritten`.

`ctx.Buffs.Stats` only writes a buff clone **this plugin registered** — a vanilla GUID or another
plugin's clone is refused. That is what makes "cleanup removes only what this contributed" true by
construction: the clone is the plugin's, unregistering the definition removes it whole, and the
per-wearer contribution is the game's to reverse. Adding the component to the clone is a structural
edit, so Core logs an "unrestorable structural edit" warning; on a plugin-owned clone that is
accurate and harmless, and the module logs one line beside it saying so.

### Why movement speed is refused

The mechanism is legible from the game's metadata: `Spawn_MoveSpeedBuffSystem.Spawn_Job` reads `Buff` plus
`ModifyMovementSpeedBuff` and registers a `Movement` modification through `ModificationsRegistry`,
and `Destroy_MoveSpeedBuffSystem.Destroy_Job` reverses it through the
`ModifyMovementSpeedBuffModification { Target, Id }` the spawn stamped on the instance. A clone of
`Buff_General_Haste` visibly carries the component.

What nobody here has measured is the **value**:

- what `MoveSpeed` (float) means against the wearer's `Movement`;
- what the `MultiplyAdd` bool switches between;
- what `CurveReference Curve` is — an asset reference this framework has no verified authoring path
  for, and which `MoveSpeedBuffSystem.Update_Job` resolves against a `CurveCollection` every tick.

Writing a number into that struct without those three answers is exactly the guess this project's
own rules forbid, so `BuffStatSupport.Reason(BuffStatKind.MovementSpeed)` names the gap and the API
refuses. Closing it needs a live read of a vanilla haste instance's `ModifyMovementSpeedBuff` values
and its wearer's `Movement` before and after, on a server, through the Dev `prefab` op — not a
guess in a table.

## Known interactions and limits

- **`DefineBuff(...).RefreshLiveInstances()`** is opt-in, and it is not compatible with a live
  application. That refresh destroys every live instance of the clone with `TryRemoveBuff` and
  re-applies it after an edit pass; an application watching one of those instances sees it vanish,
  reports `Ended` and is forgotten, while the re-applied instance belongs to nobody and survives
  plugin unload. Do not combine `RefreshLiveInstances` with `ctx.Buffs` on the same definition.
- **`Ended` can lag by a frame or two.** A confirmed application watches its own instance entity
  rather than re-walking `BuffBuffer`, deliberately — a second source's instance of the same prefab
  must not keep this one alive — and the game's destroy is deferred, so the entity can outlive the
  buff briefly. Self-correcting; do not use `Ended` as a precise clock.
- **An instance this module did not create is never adopted.** If the target already wears the buff
  (a save-restored instance, or another source's), `Apply` refuses with `AlreadyPresent` rather than
  confirming on a buff it would later destroy. A plugin that re-applies on `WorldReady` after a
  restart will see this for a player who logged out wearing it.
- **A cancel is briefly exclusive.** Between `Remove()` on an unconfirmed application and the confirm
  window closing (at most `ConfirmFrames` frames), a new apply of the same buff on the same target is
  refused with `CancelInFlight`, and `Find` / `Active` do not return the canceled application.

## Refusals

Every refusal is a handle in `Refused` carrying one actionable sentence, never an exception.
`Apply` throws only for a null request or an off-main-thread call, both of which are caller bugs.

| Refusal | When |
|---|---|
| `NotServer` | Called on the client. |
| `NoWorld` | No game world yet (apply from `WorldReady`, a command, or a virtual system — not from plugin load). |
| `BuffNotRegistered` | No buff definition with that id is registered by this plugin. |
| `BuffNotApplied` | Registered, but the registry has not put the clone into this world yet. **A registration is not an application.** |
| `TargetMissing` | No such entity in this world. |
| `TargetNotBuffable` | No `BuffBuffer` — the commonest cause is passing a `User` entity instead of a character. |
| `AlreadyOwned` | Another live application owns this buff on this target. |
| `AlreadyActive` | This owner already has it here and asked for `Reject`. |
| `BadDuration` | NaN, infinite, or out of `(0, 10000]` — including a refresh whose `elapsed + n` is not a finite positive number. |
| `ModuleUnavailable` | Core is loading or unloading. |
| `ApplyPathUnavailable` | `DebugEventsSystem` is not in this world yet. |
| `NotTimed` | An instance a runtime reports as carrying no `LifeTime` at write time. A *refresh* on one refuses earlier, with `RefreshTimingUnavailable`. |
| `AlreadyPresent` | The target already wears this buff and no live application created it. |
| `CancelInFlight` | A canceled apply of this buff on this target has not resolved yet. |
| `RefreshTimingUnavailable` | A refresh could not read the instance's `Age`/`LifeTime`, so the remaining time cannot be computed. Nothing was written. |
| `RefreshTotalOutOfRange` | `elapsed + n` would leave `LifeTime.Duration`'s `SyncRangedFloat(0, 10000)` range. Refused, not clamped. |
| `RefreshEndActionUnsupported` | `LifeTime.EndAction` is `RemoveBuffStack` (where `Duration` is one stack period) or a value this build does not recognize. |
| `RefreshWriteFailed` | The new duration could not be written — the instance went away between the read and the write, or the write threw. Nothing was written. |
| `RefreshNotActive` | A refresh on an application that is not `Active`: refused, unconfirmed, ended or removed. There is no live instance to retime. |

## Threading and authority

Server only. `Apply`, `RemoveAll` and every handle method must run on the game main thread —
`Plugin.Frame`, a command handler, a virtual system, or EventBus. `Apply` throws
`InvalidOperationException` off it, for the same reason `Units` does: EntityManager writes crash
natively from a worker thread.

A client copy of a plugin should register the same `DefineBuff` — that is how the client learns the
clone's HUD name, description and icon — and must not apply. `ctx.Buffs.Apply` refuses there anyway.

## Later live procedure

Acceptance procedure — not run on `main`. This is the sequence that would move the `api.md` rows
from *landed (port)* to *verified live*, on the isolated test pair, with one connected player
character. Deploy `Progenitor.Core` and both samples to **both** sides (the client needs the
definition for the HUD name, description and icon, and `ServerMustHave` refuses a client without
the plugin).

**The order matters and is not arbitrary.** Safe observation first, then the two reads that close
open hypotheses, and the unload case **last** — it is the one that crashed a server on 2026-09-13,
and it is only worth attempting once the three before it are clean.

**Baseline before anything.** Read the wearer's `ProjectM.Blood.LossPerSecond` and
`ProjectM.Movement` with no buff on, and keep the numbers. Every "restored" claim below is a
comparison against these, not an impression.

### Phase 1 — apply and expire (safe)

1. Build and deploy `samples/Progenitor.TimedBuffExample` to the isolated server **and the client**
   (both: it declares `ServerMustHave`, and the client half resolves the HUD name/icon). Confirm
   `Timed buff example: loaded` in the server log.
2. `.rested 30`. Expect the reply naming a *request*, then a second message when the buff is
   confirmed. On the client: the buff icon on the action bar named **Rested**. In the server log:
   `buff requested`, then `buff applied … state=Active`.
3. **Gameplay value.** Read the instance and confirm `ModifyBloodDrainBuff` with `BloodValue` 0,
   `BloodIdleValue` 0, `ModificationType` Set **and `AffectIdleBloodValue` true** — this example
   declares `ActiveAndIdle`. Then read the wearer's `Blood.LossPerSecond` and confirm it moved off
   the baseline.
4. **Modification count — closes an open hypothesis.** Read the modification registry for the
   wearer's `Blood` entry and **count the ids** attributable to this instance. `ActiveAndIdle` is
   expected to register **two**. Then repeat steps 2–4 with a build declaring
   `BloodDrainScope.Active` and confirm it registers **one**. *This is the check that turns
   "`Active` → one modification" from designed behavior into an observation.*
5. **Expiry.** Wait out the 30 seconds untouched. Expect `buff ended`, icon gone, and
   `Blood.LossPerSecond` back to the exact baseline from before. `.restedstatus` reports nothing.

### Phase 2 — movement suppression (safe)

6. **`Movement` is never patched while the buff is on.** With `.rested 300` active, read the wearer's
   `ProjectM.Movement`: it must equal the baseline. The pre-fix build patched it 4 → 6.
7. **The instance carries no movement component.** Read the instance: no `ModifyMovementSpeedBuff`
   and no `ModifyMovementSpeedBuffModification`.
8. **Actual motion.** Move the character before, during and after the buff. Speed must not change.
   A component read is not a substitute for this — the point of the fix is what the player feels.
9. **Overlapping owners, expiry and manual removal** all leave `Movement` at baseline.

### Phase 3 — removal, refresh, refusals (safe)

10. **Manual removal.** `.rested 300`, then `.unrested`. Expect `buff removed`, icon gone, drain back
    to baseline. Run `.unrested` twice more: both say there is nothing to remove.
    **A second `DestroyUtility.Destroy` on the same instance is expected, not a defect**, if the
    first had not taken within 2 frames — see "A destroy is an event" above. What must *not* appear
    is the 120-frame `giving up watching it` warning.
11. **Re-apply immediately after removing.** `.unrested` then `.rested 30` on the next command tick.
    It must apply, **not** refuse `AlreadyPresent`: the instance is still in `BuffBuffer` for a frame
    or two and the module knows it is retiring its own.
12. **Refresh leaves the requested time remaining.** This item **inverts** the expectation an earlier
    build documented, and it is the check that qualifies the whole change.
    - `.rested 90`, wait ~20 s, `.rested 120` → one instance throughout (the entity id in
      `.restedstatus` does not change) and the HUD timer shows about **120 s left**, i.e. it ends
      about 140 s after the original apply. Under the old behavior it ended at 120 s from the apply.
    - `.rested 90`, wait ~40 s, `.rested 30` → the buff **survives and runs ~30 s more**. This is the
      2026-09-13 regression; under the old behavior it ended on the spot.
    - Repeat `.rested 30` three times about 10 s apart → each time ~30 s remain. The end time must
      **not** ratchet outward by 30 s per call.
    - **Read `Age` at each step**, not just the HUD: `Age.Value` must keep counting up across a
      refresh and must **never be reset to 0** by this API. If `Age` does not advance on a live
      instance at all, stop — the arithmetic this change rests on is wrong and nothing below it is
      meaningful. (`OverrideInitialAgeBakingSystem` exists, so a clone may legitimately start at a
      non-zero `Age`; a non-zero *starting* value is not a failure, a frozen one is.)
    - `.rested 9999` → refused by the **sample's own** 1–600 s bound, before the API is called at
      all. That is a check of `TimedBuffPolicy`, **not** of `RefreshTotalOutOfRange`, and it must not
      be recorded as one.
      **The range refusal has no runnable check through `.rested`.** With the sample capped at 600 s,
      reaching `elapsed + seconds > 10000` needs an instance older than 9400 s (≈ 2 h 37 m). It is
      covered offline by `Refresh_is_refused_when_the_total_would_exceed_the_networked_range` and
      `A_total_of_exactly_the_range_ceiling_is_allowed`; live, it stays **unqualified** unless someone
      deliberately raises the sample's cap or ages a long-lived clone past that point. Do not claim it
      from a `.rested 9999` reply.
    - The sample replies with the refusal sentence rather than `buff refreshed` whenever
      `LastRefreshRefusal` is set; a reply claiming success while the timer did not move is a defect.
13. **Rejected application.** `.rested 99999` and `.rested abc` refuse with a sentence and change
    nothing. `.rested` from a client-side copy refuses with the server-only sentence.
14. **Two owners, same stat.** A second plugin GUID with a different buff id and the same
    `BloodDrain` stat. Apply both, remove one: the other's contribution still holds and its instance
    is untouched; remove the second and drain returns to baseline.
15. **Death**, and **disconnect / reconnect** within the duration — record what this build does in
    each; the API only claims a disconnect does not by itself end the application.

### Phase 4 — unload. LAST, and only if phases 1–3 are clean

16. **Plugin swap with a live buff.** This is the case that crashed the server. Expect: the buff
    removed on unload, `Reload: unloaded … clean`, **and the server still running**. Then confirm no
    orphan instance and `Blood.LossPerSecond` at baseline.
    If it crashes again, preserve the logs before relaunching and stop — the remedy did not hold.
17. **Full uninstall** of the plugin, same expectations.

### After every phase

18. **Residuals.** Inspect the character: no `ModifyBloodDrainBuff` modification left registered, no
    buff entity in `BuffBuffer`, `Blood.LossPerSecond` and `Movement` both at the recorded baseline,
    and no component added to the character by this API (there should never be one).
    Note that an autosave taken *while* a buff is on can persist its modification — if the server is
    restarted, re-check the baseline before trusting a later reading.

Record what was observed where the documentation rules say to: in the external research repository,
not here.
