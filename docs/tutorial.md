# Writing a Progenitor plugin

A progressive guide. Each step adds one thing to the same plugin and each is independently useful,
so stop wherever you have what you need. The worked reference for every step is
[`samples/Progenitor.Sample`](../samples/Progenitor.Sample); the narrative and the traps are in
[`modules.md`](modules.md), the per-facade status table in [`api.md`](api.md), and the words used
throughout in [`glossary.md`](glossary.md).

Every snippet below names real members of `src/Progenitor.Core`. Where a snippet is a shape rather
than something the Sample compiles today it is marked **not verified** and says why.

**Contents**

1. [What you need](#0-what-you-need)
2. [Your first plugin](#1-your-first-plugin)
    - [A cloned buff](#1a-a-cloned-buff)
    - [Names and descriptions: localization](#1b-names-and-descriptions-localization)
3. [A command](#2-a-command)
    - [Chat: talking to players outside a reply](#2a-chat-talking-to-players-outside-a-reply)
4. [Config: a local entry and a synced one](#3-config-a-local-entry-and-a-synced-one)
    - [Persistence: state that survives a restart](#3a-persistence-state-that-survives-a-restart)
        - [Where should my data go?](#where-should-my-data-go)
    - [Watching your own file](#3b-watching-your-own-file)
5. [A keybind and a panel](#4-a-keybind-and-a-panel)
6. [Reading castle state](#5-reading-castle-state)
    - [Spawning and steering a unit](#5a-spawning-and-steering-a-unit)
    - [Effects: borrowing a vanilla asset](#5b-effects-borrowing-a-vanilla-asset)
7. [A hook, and a patch with the unload rule](#6-a-hook-and-a-patch-with-the-unload-rule)
    - [Custom networking: your own packets](#6a-custom-networking-your-own-packets)
    - [Handshake rows and event ids](#6b-handshake-rows-and-event-ids)
    - [Make your mod work across versions](#6c-make-your-mod-work-across-versions)
8. [Keeping state across a hot swap](#7-keeping-state-across-a-hot-swap)
9. [Testing with the Dev bridge](#8-testing-with-the-dev-bridge)
10. [The hot-reload loop, including Edit-and-Continue](#9-the-hot-reload-loop-including-edit-and-continue)

Each lettered subsection covers one module the numbered spine does not, and says whether the Sample
already compiles an example of it.

When the mod is ready to share, [publishing.md](publishing.md) covers the Thunderstore package, its
`progenitor.json`, dependencies, the settings and keybind declarations players rely on, and
`tools/publish-check.ps1`.

---

## 0. What you need

- **This repo** and **.NET SDK 8**.
- **BepInExPack_V_Rising** installed in the game (r2modman, the Thunderstore App, or by hand) and the
  game **launched once with it enabled**, so Il2CppInterop has generated `<game>\BepInEx\interop`.
  That folder is what the build compiles against and nobody ships it. Full prerequisites and how to
  point at a non-default install: [README, "Building from source"](../README.md#building-from-source).
- **Progenitor installed** in the client `BepInEx` tree and in whichever server you test on. With no
  server, host a private game from the client (the client runs the bundled server as a child with
  the same BepInEx tree, so one install covers both sides) or stage a throwaway one with
  `tools/stage-test-server.ps1`.

Three folders, and they are not interchangeable:

| Folder | What lives there |
|---|---|
| `BepInEx/plugins/` | `Progenitor.Bootstrap.dll` **only** — the chainloader plugin. Not your DLL. |
| `BepInEx/progenitor-core/` | `Progenitor.dll` (Core), loaded by Bootstrap into a collectible context. |
| `BepInEx/progenitor-reload/` | **Your** plugin. Scanned once a second; a settled `*.dll` is loaded, and unloaded again when it changes or disappears. |

## 1. Your first plugin

Add a sibling of the Sample so [`Directory.Build.props`](../Directory.Build.props) and
[`Directory.Build.targets`](../Directory.Build.targets) apply. `ReferenceGame` pulls BepInEx and the
game interop from the install; the project reference is compile-time only (`Private=false`);
`BepInEx.PluginInfoProps` emits `MyPluginInfo`.

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <AssemblyName>MyMod</AssemblyName>
    <RootNamespace>MyMod</RootNamespace>
    <BepInExPluginGuid>com.example.mymod</BepInExPluginGuid>
    <BepInExPluginName>My Mod</BepInExPluginName>
    <ReferenceGame>true</ReferenceGame>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\Progenitor.Core\Progenitor.Core.csproj" Private="false" />
  </ItemGroup>
</Project>
```

**Outside this repo** there is no `Directory.Build.props` to inherit: copy the `ReferenceGame` item
group from `Directory.Build.targets` and the `VRisingGameDir` / `VRisingInteropDir` properties from
`Directory.Build.props` into your own build files, and reference `Progenitor.Core` by path. There is
no published NuGet package. Everything else on this page is the same.

The plugin itself. **Keep the context constructor empty** — `Log` and every facade are
attached after `new`, before `OnLoad`, and touching one earlier throws. Setup goes in `OnLoad`;
unload reverses everything `ctx` recorded.

```csharp
using BepInEx;
using BepInEx.Unity.IL2CPP;
using Progenitor.Content;
using Progenitor.Events;
using Progenitor.Plugins;
using Stunlock.Core;

namespace MyMod;

[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
[BepInDependency("progenitor")]
[ProgenitorSide(Side.Both)]
[NetworkCompatibility(NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact)]
public sealed class Plugin : ProgenitorPlugin<MyContext>
{
    public const string EmberId = "mymod.ember";
    public static readonly PrefabGUID EmberSource = new(-257494203); // Item_Ingredient_Crystal

    protected override void OnLoad(MyContext ctx)
    {
        ctx.Content.DefineItem(EmberId)
            .CloneOf(EmberSource)
            .MaxAmount(50)
            .Named("Ember Shard")
            .Described("A shard that still holds a little heat.")
            .Register();

        ctx.Handshake.DeclareSelf(); // idempotent; the attribute applies this after OnLoad too

        if (ctx.IsServer)
            ctx.Events.On<PlayerConnected>(e => ctx.Log.LogInfo("Welcome, " + e.CharacterName));
    }
}

public sealed class MyContext : PluginContext { }
```

Worn armor and weapons add `.Wearable(...)` on that same `DefineItem` chain. The robe set is the worked example: [wearables.md](wearables.md).

`[ProgenitorSide(Side.Both)]` is a *declaration*; `ctx.Side` is never `Both`.
`[NetworkCompatibility(ServerMustHave, Exact|Minor)]` is **required** if you ever call
`ctx.Content.Edit`: anything weaker and the editor is refused with a log line naming what you
declared. Since job FEDW1 the edited values themselves **are** on the wire — after a server pass the
touched components ship as a `PrfE` packet and a Progenitor client applies them as the final layer
over its own editors ([`edits-on-the-wire.md`](edits-on-the-wire.md)). What the declaration still
buys you is the peer with **no** plugin at all: it would keep unedited stats, so the handshake kicks
it instead. Phase 2 is what relaxes the requirement for wire-safe edits, and the two-process live
check on a real client is still owed.

Build, then drop the DLL in `BepInEx/progenitor-reload/` on **both** sides. Optional
`localization/en.json` goes next to the DLL, or under `<reload>/<AssemblyName>/localization` after a
byte-stream reload; optional PNG icons ship in an `assets/` folder next to the DLL, which is what
`DefineBuff(...).Icon("icons/foo.png")` loads on the client.

### 1a. A cloned buff

Example: [`Progenitor.TimedBuffExample`](../samples/Progenitor.TimedBuffExample) (`DefineBuff`, applied and removed through `ctx.Buffs`). Core's own debug buff ([`DebugBuff.cs`](../src/Progenitor.Core/Debugging/DebugBuff.cs), `.debug on|off|status`) is a larger one: icon, live refresh and a component the game never removes.

A buff is defined on a second builder with the same shape as an item. Clone a **visible** vanilla
buff if you want an action-bar row, because the HUD entry comes from the source prefab:

```csharp
ctx.Content.DefineBuff("mymod.ward")
    .CloneOf("Buff_General_Haste")     // PrefabGUID or spawnable name; resolved at apply time
    .Named("Ward")
    .Described("Hostiles ignore you.")
    .Icon("icons/ward.png")            // PNG under this plugin's assets/ folder
    .RefreshLiveInstances()            // opt-in: destroy-and-reapply live instances after a redefinition
    .Register();                       // no owner argument: the context's plugin is the owner

// The stats come from an ordered edit pass on the clone's own GUID, exactly as for an item.
ctx.Content.Edit(new PrefabGUID(PrefabGUID.CalculateGuidHash("mymod.ward")), EditWard);
```

There is no `MaxAmount` on the buff builder. `Icon` takes a path relative to an `assets/` folder
next to your DLL; absolute paths and `..` are refused, and the path is never a wire field — the
client loads the file after it has cloned the prefab. Unload is a soft unapply: buffs are not
tombstoned the way items are, because a buff never occupies an `ItemKeyLookup` row.

**Defining a buff is not applying one.** Progenitor gives you the prefab; putting it on a character
is the game's own path: `ctx.Buffs.Apply`, or `DebugEventsSystem.ApplyBuff` with an
`ApplyBuffDebugEvent` as Core's debug buff does (`DebugBuff.TryApply`). If your edit adds something nothing in the game
takes off again — the debug buff's `Immortal` is the case in point — then removal is yours to own, on
the command, on the `Removal` unload reason (see [step 7](#7-keeping-state-across-a-hot-swap)) and
on a reconcile pass for every other way the buff can end.

### 1b. Names and descriptions: localization

Example in the Sample: [`localization/en.json`](../samples/Progenitor.Sample/localization/en.json)
(its keys are what make `.ember` read "Ember Shard"). No Sample example yet of the programmatic route (job J10).

`ProgenitorPlugin` loads localization for you before `OnLoad` runs, from
`<dir>/<AssemblyName>/localization` and then `<dir>/localization`. A file's stem is its language:
`en.json` is English, which is also the fallback. The file is a flat object of key → text:

```json
{
  "mymod.ember": "Ember Shard",
  "mymod.ember.desc": "A shard that still holds a little heat.",
  "mymod.ward.name": "Ward",
  "mymod.ward.desc": "Hostiles ignore you."
}
```

The keys are built from your definition ids, and the two kinds differ by one suffix:

| What | Key | Where |
|---|---|---|
| An item's name | `<id>` | the client points the cloned `ManagedItemData` at it |
| A cloned buff's HUD title | `<id>.name` | `BuffBarText.NameKey`, written onto `ShowOnBuffBarData` |
| Either one's description | `<id>.desc` | `PrefabWire.DescriptionKey` |

`Named(...)` / `Described(...)` on a builder win when they are present; leave `Described` off and
the `<id>.desc` key from the file is used instead — which is exactly what the Sample does for
`.ember`, so a translator can edit a file rather than the source.

For a string that is not in a file — one you build at run time — register it directly:

```csharp
using Progenitor.Localization;

IDisposable row = Localization.Register("mymod.dynamic", "Text I computed", owner: ctx.Owner);
ctx.Track(row);

if (Localization.IsRegistered("mymod.dynamic")
    && Localization.TryGetGameString(Localization.KeyFor("mymod.dynamic"), out string text))
    ctx.Log.LogInfo(text);
```

`Register` is side-aware: the client writes the game's own string table, the server only records the
row so the definition packet can carry it. `LoadDirectory` / `LoadForPlugin` / `RemoveAll` are the
file-level forms; `ApplyName` / `ApplyDescription` point a cloned `ManagedItemData` at a key and are
what the registry itself uses.

`Register` is side-aware: the client writes the game's own string table, the server only records the
row so the definition packet can carry it. `ApplyName` / `ApplyDescription` point a cloned
`ManagedItemData` at a key; the registry calls them for you when you use `.Named()` / `.Described()`,
and [`SampleLocalization.cs`](../samples/Progenitor.Sample/SampleLocalization.cs)'s
`.loc apply <id> <text>` does it by hand so you can see what those builder calls actually do. Note where
each half runs: the registration happens on both sides, but the managed-data registry is client-only, so
a server that runs `.loc apply` registers the strings and reports that there was no `ManagedItemData` to
point. `.loc key <id>` prints `KeyFor`'s derived `AssetGuid` next to `IsRegistered` and
`TryGetGameString` for any key; the GUID is MD5 of the key's own bytes, which is how two processes agree
on a key nobody shipped an asset for.

## 2. A command

Prefix is `.` (`CommandRegistry.Prefix`). The first parameter is always `ICommandContext`. A C#
`static` class cannot be a generic type argument, so register the type object:

```csharp
using Progenitor.Commands;

namespace MyMod;

internal static class MyCommands
{
    [Command("hello", adminOnly: false, usage: ".hello [name]", description: "Say hello.")]
    public static void Hello(ICommandContext ctx, [Remainder] string name = "world")
    {
        ctx.Reply("Hello, " + name + " — you are " + (ctx.IsAdmin ? "an admin" : "a player") + ".");
    }
}
```

```csharp
if (ctx.IsServer)
    ctx.Commands.Register(typeof(MyCommands));
```

`ICommandContext` carries `UserEntity`, `CharacterEntity`, `User`, `IsAdmin`, `RawText`, `World` and
`Reply` (which splits replies over 500 UTF-8 bytes). `CommandParser.Tokenize` handles `string`,
`int`, `long`, `float`, `double` and `bool`; `[Remainder]` on the last `string` keeps the rest of the
line. `adminOnly: true` makes the dispatcher answer `This command is admin-only.` and hides the row
from `.help`. Anything that writes to the world from chat input should be admin-only **and clamped**
— the Sample's `.ember` takes a count and clamps it to 50 for exactly that reason.

### 2a. Chat: talking to players outside a reply

Example in the Sample: [`SampleChat.cs`](../samples/Progenitor.Sample/SampleChat.cs) (`.shout
<text>`, `.whisper <name> <text>`, both admin-only). The signatures are `PluginChat`
(`src/Progenitor.Core/Plugins/PluginContext.cs:1160,1171`) and the static `Chat`
(`src/Progenitor.Core/Chat/Chat.cs:36,89`); the narrative and the recipients table are
[`modules.md`](modules.md#chat).

`ctx.Reply` answers the player who typed the command. When you want to say something nobody asked
for — a countdown, an event announcement, a whisper — that is `ctx.Chat`, and it is server-only:

```csharp
using ProjectM.Network;   // ChatMessageType

int recipients = ctx.Chat.Broadcast("Server restarting in 5 minutes.");            // System
int global     = ctx.Chat.Broadcast("An event has started.", ChatMessageType.Global);
bool sent      = ctx.Chat.SendTo(userEntity, "You alone get this.", ChatMessageType.Whisper);
```

Three things are worth knowing before you build on it:

- **`Broadcast` returns the number of recipients** and `SendTo` returns a `bool`, so a server log
  line alone tells you whether it went out. On the client both are a no-op that returns `0` /
  `false`, so shared code needs no side check.
- **Recipients are `ConnectedUser` entities only** — an offline player is not one, and the speaker
  *is* included, which is the opposite of the vanilla chat job's behavior for Global and Local.
- **Text is truncated at 509 UTF-8 bytes**, and this path deliberately skips
  `ChatMessageSystem`, so your own `ChatMessage` subscribers do not see what you send.

`ctx.Chat` is bound to this plugin's world. The static `Chat.Broadcast` / `Chat.SendTo` take an
explicit `World` (and an optional speaker `Entity`) for code that is not holding a context.

## 3. Config: a local entry and a synced one

Two different calls, and mixing them up throws:

```csharp
// Local: this plugin's own GUID.cfg. The player owns it.
var greeting = ctx.Config.Bind("MyMod", "Greeting", "Hello from MyMod",
    "Text the .hello command uses when no name is given.");

// Synced: the server pushes this to clients that also have this plugin, after a good handshake.
var stack = ctx.Bind("MyMod", "EmberStack", 99,
    "Max stack size. The server owns this for the length of a session.",
    synced: true);

// Either kind can carry a range or a list. Pass an AcceptableValue* (or a whole
// ConfigDescription); it works on a synced entry too.
var multiplier = ctx.Bind("MyMod", "SpawnMultiplier", 3,
    "How many extra spawns per wave.",
    synced: true,
    acceptableValues: new AcceptableValueRange<int>(1, 10));
```

- `ctx.Config.Bind(..., synced: true)` **throws** — that path cannot `Track` the sync handle. Synced
  binds go through `ctx.Bind`.
- Bounds are **local metadata**: they go in your cfg comment and on the settings-panel row, but they
  are not on the wire. If a server pushes a value your own bind refuses (it binds `1..50`, you bind
  `1..10`), the client **clamps** it to your bounds and logs the server value next to what it
  stored, rather than sitting on an out-of-range value or letting BepInEx clamp it silently.
- Only BCL primitives, `string` and BCL enums may be bound at all on the shared file or on a synced
  entry (`ConfigSync.ThrowIfNotSyncable`). A `ConfigEntry<YourType>` on the process-lifetime shared
  file `progenitor.cfg` pins a collectible Core [generation](glossary.md#generation) and
  breaks hot reload; bind your settings on **your** `GUID.cfg`.
- The client **locks** a synced entry for the session and restores its own value on disconnect. A
  server value never reaches the player's `GUID.cfg` — see [config-sync.md](config-sync.md).

If a synced value feeds a prefab edit, re-run the edit when it changes and unsubscribe on unload:

```csharp
_stackChanged = (_, _) => ctx.Content.InvalidateEdits(ctx.Owner);
stack.SettingChanged += _stackChanged;
```

(`InvalidateEdits` is an extension method in `PluginContentEdits`, not a member of `PluginContent`.)

### 3a. Persistence: state that survives a restart

Examples in the Sample: the join book in
[`Plugin.cs`](../samples/Progenitor.Sample/Plugin.cs) (`SampleJoins`, read by `.joins` in
[`SampleCommands.cs`](../samples/Progenitor.Sample/SampleCommands.cs)), and
[`SampleStore.cs`](../samples/Progenitor.Sample/SampleStore.cs) for `Remove`, `Keys` and the load
counter that `WhenReady` keeps honest (`.store`).

Config is what the *player* owns. `ctx.Save` is what the *save* owns: an `IModStore`, a per-mod JSON
bag written next to the world save at
`…/Saves/v4/<save>/progenitor/<modGuid>.json`. On the client it is a no-op store, so shared code
again needs no side check, and unload never deletes the file.

```csharp
using Progenitor.Persistence;

// Your own type. System.Text.Json serializes it; keep it a plain DTO with public setters.
internal sealed class JoinBook { public List<JoinRecord> Players { get; set; } = new(); }

IModStore store = ctx.Save;

var book = store.Get("joins", new JoinBook());   // fallback when the key is missing or unreadable
book.Players.Add(new JoinRecord { Name = "…", Count = 1 });
store.Set("joins", book);                        // replaces the key

bool dropped = store.Remove("joins");            // false when it was not there
IReadOnlyList<string> keys = store.Keys;         // snapshot of the keys in memory, not live
```

Four rules that come out of how the file is written:

- **`Set` does not write the disk.** The store is flushed on the game's own `TriggerSave` (autosave
  or the Dev `world_save` op) and on disconnect. Ending a test with a kill rather than a save is why
  a row can look lost.
- **`Get<T>` never throws.** Missing or malformed data is the fallback, so a schema you changed
  between versions degrades instead of failing the load.
- **Key by something stable.** The Sample keys its join records by platform id, not by character
  name, so a rename does not reset the count.
- **A write before the save folder is known is not lost**: the store keeps a pending copy and merges
  it once the directory resolves. `ModSave.Store(guid)` / `SaveAll()` / `CurrentSaveDirectory` are
  the static forms behind `ctx.Save` if you need them.
- **A read-modify-write goes in `WhenReady`, not in `OnLoad`.** This is the one that bites. On a
  server start `OnLoad` runs *before* the save folder is known, so `Get` reads an empty document
  and the merge lays your answer over the file: `store.Set("loads", store.Get("loads", 0) + 1)`
  from `OnLoad` writes `1` over the `17` that was on disk, every start, forever. A blind `Set` is
  safe (that is what the merge is for), but anything that reads first belongs here:

  ```csharp
  // In OnLoad. Runs once the store is backed by the save folder, on the main thread, with the
  // disk value visible; in line if the folder is already known. Dispose to cancel; Core also
  // drops it when your plugin unloads.
  IDisposable armed = ctx.Save.WhenReady(store =>
      store.Set("loads", store.Get("loads", 0) + 1));
  ```

  Working version: `SampleStore.ArmLoadCounter`, whose log line names the branch that ran —
  `WhenReady ran in line: the store was already real` or `WhenReady ran after the pending merge`.
  It knows which because the in-line branch fires *during* the `WhenReady` call while the pending
  one always comes back through the main-thread queue, so a local set after the call tells them
  apart — **as long as you register on the main thread**, which `OnLoad` is, and which is why the
  Sample registers from `OnLoad`. From a background task the in-line branch is not available even
  when the store is already real: the registration is posted, the local reads `false`, and the line
  would say "after the pending merge" over a store that never had one. There are three cases, not
  two (`modules.md`, the `WhenReady` paragraph); nothing on `IModStore` hands the callback which one
  it is in.

Beside `ctx.Save` there are three statics, each for one moment. `ModSave.Store(guid)` is the same
instance from code that has no context: a static helper, a command class registered by `Type`, a
background task (`.store static` proves it with a reference compare). `ModSave.CurrentSaveDirectory` is a
**diagnostic**: print it, never build paths from it, because Core relocates stores when the resolved
folder changes (`.store dir`). `ModSave.SaveAll()` forces a write now; the game's save trigger and a
player disconnect already flush, so use it when an operator has just changed something (`.store flush`,
which then reads the file back rather than trusting that the void call returned). `ModSave.RemoveAll(owner)`
drops your plugin's still-pending `WhenReady` callbacks on unload; Core's reloader sweep calls it for you,
and `SampleStore.Detach` calls it anyway and logs the count.

#### Where should my data go?

A wipe archives the world folder (`Saves/v4/<save>` becomes `<save>_<ticks>`) and a
[world restore](world-restore.md) replaces it with an earlier copy. Nothing else is archived or rolled
back, so choose the place by what must happen to the data then:

| Data | Where | Wipe | World restore |
|---|---|---|---|
| World state (claims, quest progress, what you spawned) | `ctx.Save` | archived with the world | rolled back with the world |
| Server settings (rates, switches) | your config: `ctx.Bind(..., synced: true)`, a Server-wide setting | kept | kept |
| Data meant to survive wipes (season stats, hall of fame, bans, rewards) | `ctx.ServerSave` | kept | kept, **not** rolled back |
| Player preferences | the player's own `ctx.Config` / a Personal setting | kept (their PC) | kept |

`ctx.ServerSave` is the same `IModStore` as `ctx.Save`, written to
`<persistentDataPath>/progenitor-server/<modGuid>.json`, outside every world folder:

```csharp
// Survives wipes and restores. Key by platform id; a character name changes, an entity id is per world.
var wins = ctx.ServerSave.Get("seasonWins", new Dictionary<ulong, int>());
wins[platformId] = wins.GetValueOrDefault(platformId) + 1;
ctx.ServerSave.Set("seasonWins", wins);
```

The Sample keeps its join book in both: `.joins` counts joins in this world, `.store server` reports
the all-time book in the server store. Never keep world state in a file of your own (under
`BepInEx/config` or anywhere else): a wipe would not archive it and a restore would not roll it back,
so your mod and the world would disagree. The full rules are in
[modules.md](modules.md#where-should-my-data-go).

### 3b. Watching your own file

Neither `ctx.Bind` nor `ctx.Config.Bind` reloads anything: BepInEx re-reads a `.cfg` only when
something asks it to, so an operator who edits your file while the server is up is ignored until
restart. `ConfigHelper` is the answer, and the whole feature is proved with a text editor rather
than a command:

```csharp
// Bind through ConfigHelper and the file is watched from the first bind.
_enabled = ConfigHelper.Bind(ctx.Config.File, "Watch", "Enabled", true, "...");
// A plugin enum goes on disk as a string, and comes back typed.
_verbosity = ConfigHelper.BindEnum(ctx.Config.File, "Watch", "Verbosity", SampleVerbosity.Normal, "...");
var now = ConfigHelper.ReadEnum(_verbosity, SampleVerbosity.Normal);

_lease = ConfigHelper.Watch(ctx.Config.File);     // an IDisposable lease, refcounted per path
ConfigHelper.Reloaded += OnReloaded;              // Core static: unsubscribe on unload or you pin the ALC
```

Three things that are easy to conflate. `Watch(file)` is a **lease** on a `FileSystemWatcher`; a
second watch on the same path shares the watcher and the watcher stops when the last lease is
disposed. `Reloaded` is **one static event for every watched file**, raised on the main thread after
a 1 s debounce and after the live `ConfigEntry<T>` values have already been updated, so your
handler must filter on `ConfigFilePath`. And `ConfigFile.Reload()` called by hand re-reads the same
file but does **not** raise `Reloaded`; that event belongs to the watch.

Two rules the Sample's [`SampleConfig.cs`](../samples/Progenitor.Sample/SampleConfig.cs) (`.cfg`)
exists to show. First, **unsubscribe on unload**: `Reloaded` lives in Core and outlives your plugin,
so a delegate left on it holds your ALC alive past a hot reload. The Sample unsubscribes in
`Detach()` *and* tracks a disposable on the context that does the same, because either alone is a
single point of failure. Second, **`ConfigHelper.Bind` takes a watch lease of its own and never
disposes it**, so turning "your" watch off does not stop the watcher; if you need it stoppable, bind
with `ctx.Bind` / `ctx.Config.Bind` and own the only lease. Make the callback line worth reading
while you are at it: nobody has a chat window open at the moment they save a file, so the Sample logs
a timestamp, the file name, the callback number and the before and after value of every entry
(`Sample config: reload #3 at 21:04:33.512Z of progenitor.sample.cfg: Watch.Enabled: True ->
False, ...`), which is enough to prove the edit landed from the server log alone.

## 4. A keybind and a panel

Before building a mod's own UI, read the [UI style guide](ui-style.md): it says where the UI should live (the settings page, a Mods page or a game-look window) and which control fits each value.

Working versions of everything in this section:
[`SampleUi.cs`](../samples/Progenitor.Sample/SampleUi.cs) (the `F9` bind, the demo panel, and the
two settings entries with an `AcceptableValueRange` and an `AcceptableValueList`) and
[`SampleLogPanel.cs`](../samples/Progenitor.Sample/SampleLogPanel.cs) (a second bind on the
modifier chord `Ctrl+F9`, and the panel whose input capture stops movement while the pointer is
over it). The in-game **Mods** workspace is separate from `Ui.Panel`: register a name, grouped
settings and (explicitly opted-in) server edits through `ctx.Menu`; the compiling example is
[`SampleMenu.cs`](../samples/Progenitor.Sample/SampleMenu.cs). Entry sites, close/back, inventory
and what the UI does not offer: [mods-menu.md](mods-menu.md). **F10** toggles that workspace, not
a standalone settings overlay.

Client work. Both are safe to call unconditionally: on the server the keybind registers and never
fires, and `Ui.Panel` returns a disabled handle.

```csharp
using Progenitor.Ui;

ctx.Keybinds.Register(
    "TogglePanel",                       // id; the cfg key under [Keybinds] in your GUID.cfg
    new KeyChord(UiKey.F8),              // or just UiKey.F8 for the no-modifier overload
    "Open or close the MyMod panel.",
    () => Toggle(ctx),
    allowWhileCaptured: true);           // so F8 can also close the panel it opened
```

One registration binds one `string` entry under `[Keybinds]` in your `GUID.cfg`, in BepInEx
`KeyboardShortcut` format — main key first, then modifiers, joined with `" + "`: `F8`, `F10 + Ctrl`,
`K + Ctrl + Shift`, `None` to unbind. Key names are `UnityEngine.InputSystem.Key` names. The handle
is `ctx.Track`ed, so unload unregisters it; the player's cfg value survives.

Three suppression rules apply, in this order: a focused **text field** anywhere suppresses every
bind; a focused or unreadable **chat window** suppresses every bind that did not opt out; and an open
mod panel that is capturing input suppresses every bind without `allowWhileCaptured: true`. Since
FUI19 that last rule tests whether the panel is *actually blocking* — capture on, the refcount above
zero **and** the pointer gate open — so a `WhenHovered` panel open in one corner leaves your binds
live while the player looks at the world. A conflict on one chord is a warning and **both** handlers
still run.

**Do not write your own press detector.** `ButtonControl.wasPressedThisFrame` is not "went down
since you last looked" — it is true only during the input update step that processed the press, and
Progenitor samples once per rendered frame, so reading it raw drops roughly half of every player's
presses (that is exactly what "I have to press F10 twice" was). Core derives the edge from a held
state it owns and hands you the result; register through `ctx.Keybinds` and the problem is not
yours.

The panel is the static `Progenitor.Ui.Ui`, deliberately **not** a `ctx` property: a `UnityAction`
whose method lives in collectible Core would pin the ALC, so Bootstrap owns the listeners and Core
fills a slot.

```csharp
private static IUiPanel? _panel;

private static void Toggle(PluginContext ctx)
{
    if (_panel is { IsDisposed: false })
    {
        _panel.Dispose();
        _panel = null;
        return;
    }

    var panel = Ui.Panel("MyMod", owner: ctx.Owner, pluginId: ctx.Guid);
    if (!panel.Enabled) return;                       // server, or the toolkit is not up

    panel.Label("Hello from MyMod");
    panel.Button("Log a line").OnClick(() => ctx.Log.LogInfo("clicked"));
    panel.Toggle("Verbose", initial: false).OnChanged(v => ctx.Log.LogInfo("verbose=" + v));
    Ui.Show(panel);
    _panel = panel;
}
```

Register a disposer so unload closes it — `ctx.Track(...)` with your own small `IDisposable` whose
`Dispose` closes the panel (Core's own `ActionDisposable` is `internal`, so write your own) — or call
`Ui.RemoveAll(ctx.Owner)`, which destroys **only** your `progenitor-ui-<id>` root and also drops your
keybinds and your settings rows.

**Input capture: where the block applies, and how to opt out.** Showing a panel takes gameplay input
by default: Core registers the game's own `RotateCameraBlocker` context with chat's mask, so movement
and aim are blocked and camera rotation is kept. It is refcounted — a second panel does not add a
second entry.

**The block follows the pointer.** The default is `UiCaptureMode.WhenHovered`: over one of your
shown panels the wheel scrolls the list and a click hits a row; with the pointer anywhere else, the
same wheel and the same click go to the game. That is new in FUI19, and it exists because a panel
that happened to be open used to take the whole screen hostage — the wheel could not zoom and a
click could not swing with the pointer nowhere near the panel.

```csharp
panel.CaptureMode = UiCaptureMode.WhileShown;   // per panel; works on an already-open panel
Ui.CaptureInput(UiCaptureMode.Never, pluginId: ctx.Guid);   // this plugin's panels
Ui.CaptureInput(UiCaptureMode.WhenHovered);                 // framework-wide default
```

Four layers, most specific first: `IUiPanel.CaptureMode` → `Ui.CaptureInput(mode, owner/pluginId)` →
`Ui.CaptureInput(mode)` → the operator's `[Ui] CaptureMode` entry in `progenitor.ui.cfg`.
`WhileShown` is the pre-FUI19 behavior and is the right choice for a genuinely **modal** panel;
`Never` is what `Ui.CaptureInput(false)` has always meant, expressed per panel or per plugin. A mode
applies to the **next** `Show` — a panel already open keeps the mode it was shown with, so another
plugin loading cannot change the block under a player mid-interaction.

`Ui.CaptureInput(false)` is still the master switch and still **process-wide**: it removes the
context now and keeps it off whatever mode any panel asks for. `Ui.CaptureInput(true)` turns it back
on; `Ui.IsCapturingInput` reports the state. The cursor unlock is never routed by the pointer — a
locked cursor could not be moved onto the panel in the first place — so only the input block is.

**Escape closes panels.** While a panel is shown, Escape hides every shown Progenitor panel and does
nothing else; it does not dispose them and never reaches your key handler. That half is the same in
every mode, because it comes from Bootstrap's capture refcount, which every shown panel drives.

**Whether the vanilla system menu also opens depends on the mode, and on one thing about the build.**
Closing the panel and *consuming* the key are separate mechanisms: consumption comes from the input
mask on the registered input context, and only `WhenHovered` and `WhileShown` register one.

- `WhenHovered` / `WhileShown`: one press closes your panel and the game menu stays shut.
- **`Never`: one press closes your panel *and* opens the vanilla system menu.** A `Never` panel
  registers no context by design — a HUD overlay you read while playing should not eat the ESC menu
  — so if you took the HUD-overlay recommendation earlier in this section, this is the behavior you
  get. It is stated in `UiCaptureMode.Never`'s own doc comment for the same reason.
- **`Ui.CaptureInput(false)`** is the same: the master switch removes the context, so Escape neither
  closes your panel nor is masked away from the vanilla menu. Close such a panel from your own
  keybind.
- Either of the first two, on a build where the address library has no
  `ProjectM.RotateCameraBlocker$$GetConsumedInputs` row: the context registers but the mask does
  not, so Escape is not consumed there either.

You do not have to guess which happened. The client log says it once, when the context is registered:

```
Ui: input context registered order=599 mask=live (... Back/Menu_Close taken in every scope so Escape closes the panel without also opening the game menu)
Ui: input context registered order=599 mask=absent (... Escape is NOT consumed -- one press closes the panel AND opens the game menu ...)
```

`mask=absent` is the remediable one: add that id to `tools/addresslib/wanted-client.txt` and rebuild.
For `Never` there is no line to fix, because nothing is wrong — see
[`ui.md`](ui.md#input-what-a-panel-takes-and-for-how-long).

**You do not size or place the panel.** There is no size, position or layout argument on `IUiPanel`,
and that is deliberate: a panel whose row count comes from other people's plugins has no fixed size.
The framework sizes the frame to its rows, clips them into a scrolling viewport when there are more
than fit, measures **every** row's own caption (a four-line config description gets a four-line row,
whatever control is on it), and tiles simultaneously-shown panels into columns. The one placement
promise you can build on is narrow: **a re-layout caused by *using* a panel never moves anything** —
rewriting a row's text under the player's own click lands in the same place. Showing or hiding a
panel re-flows all of them, so opening a second panel does move the first.

**What the settings panel shows.** Touching `ctx.Keybinds` also registers your `ConfigFile` as a
settings source, so your plugin's rows appear in `Progenitor.Ui.SettingsPanel` and in the Mods
workspace. The panel lists every registered keybind first,
with a press-a-key rebind button; then every **non-synced** config entry of every plugin it knows
about, grouped by plugin, with a toggle for `bool` and a text field for `int` / `float` / `string` /
`enum`. Synced entries are shown read-only and say `(set by the server)`. Rows discovered by the
chainloader sweep — another mod's settings — are read-only unless the operator sets
`[Settings] EditOtherModsSettings = true` in `progenitor.ui.cfg`. Last comes the
**Kept versions** group (only when the version store keeps a copy), with **Restore** for the
player's own version. (The **Server mods** group was removed 2026-10-05: servers no longer send mod
files.) Edits commit on Enter or focus loss and are written through the `ConfigEntry`, so BepInEx
saves the cfg; the panel never writes a file itself.

## 5. Reading castle state

Server-only, main-thread-only, and read-only — nothing to reverse on unload. On the client every call
returns a disabled handle with one Debug line, so shared code needs no side check. The Sample's
`.castle` is [`SampleCastle.cs`](../samples/Progenitor.Sample/SampleCastle.cs); it is also where
the Sample stamps the one entity id it keeps past the frame with the world-load epoch, which
[`SampleEpoch.cs`](../samples/Progenitor.Sample/SampleEpoch.cs) (`.epoch`) then reports
`IsStale` for.

```csharp
using Progenitor.Castles;

// userEntity comes from an ICommandContext or a PlayerConnected event, not from PluginContext.
ICastleHandle castle = ctx.Castles.ForUser(userEntity);   // never null; check Enabled
if (castle.Enabled)
{
    ctx.Log.LogInfo($"{castle.Owner.Name} territory {castle.Territory.Index} " +
                    $"lv{castle.Level} floors {castle.FloorCount} raided={castle.Raided}");

    foreach (var b in castle.Buildings())                  // NetworkId order
        ctx.Log.LogInfo($"{b.PrefabName} @{b.TileX},{b.TileY} h{b.HeightLevel}");

    bool inside = castle.IsInside(new CastleVec3(x, y, z));
    var check = castle.CanPlace(prefabGuidHash, new CastleVec3(x, y, z));
}
```

Three things to know before you build on this:

- **`IsInside` is X/Z only.** There is no verified world-Y → `TilePosition.HeightLevel` mapping. When
  the territory's tile mask reads (its length equals the computed tile count) the answer comes from
  the mask and `castle.IsTilePrecise` is `true`; otherwise it falls back to the bounds box and the
  territory logs one info line. For a player, use `ctx.Castles.StandingTerritory(character)` or
  `ctx.Castles.At(character)`: both read the character's `Translation`, so every floor of the castle
  counts and a teleport shows on the next read.
- **`CanPlace` is a documented subset, not the game's answer.** It reproduces territory containment,
  the tile mask, the height band and occupancy against the plot's own placed models.
  `CastlePlacementCheck.Complete` is **always false**, and `.NotChecked` lists the eleven vanilla
  rules it skips.
- **Staleness, and why you should not hold a handle.** Entity ids are session-scoped. A handle
  stamps the [world epoch](glossary.md#world-epoch) at construction and goes stale when that epoch
  moves — a save reload rebuilds every entity from index 0 at version 1, so an id check alone cannot
  see it — *and* when its own heart or territory entity stops being live at that exact
  `index:version` (a demolition inside one epoch). A stale handle has `IsStale == true`,
  `Enabled == false`, empty `Buildings()` / `Tiles()`, `IsInside` false, and a `DisabledReason` that
  names both epochs and the territory index to re-resolve with. `Refresh()` re-resolves through the
  territory index, never through the heart. Persist `Territory.Index`, never an `Entity`; and prefer
  taking a fresh handle each frame over keeping one.

**The recovery, worked through.** `.castle territory <n>` in the Sample is the whole shape in four
lines: `Castles.ForTerritory(13)` resolves the plot from the one key a save reload keeps,
`handle.Refresh()` does the same thing from a handle you are already holding (it calls
`ForTerritory(Territory.Index)` internally, never `ForHeart`), and `Castles.ForHeart(handle.Heart)` is
printed beside them to show what *not* to persist: it answers correctly this session and names somebody
else's entity after a reload. So: store `Territory.Index`, call `ForTerritory` when you come back, and
use `Refresh()` when the handle you hold has gone `IsStale`. A refreshed handle is a new object; the
original keeps its old snapshot, which is what makes `Refresh()` safe to call on a stale one.

If you *do* keep any entity id across frames, stamp the epoch beside it:

```csharp
int stamped = ctx.WorldState.LoadEpoch;
// … later …
if (ctx.WorldState.IsStale(stamped)) { /* drop the id, re-resolve */ }

ctx.Events.On<WorldLoaded>(e =>
    ctx.Log.LogInfo($"world reloaded: {e.PreviousEpoch} -> {e.Epoch} ({e.Source})"));
```

`LoadEpoch` is an opaque token to compare, not a number to do arithmetic on — it is generation-stamped,
so a stamp taken in an older Core generation also reads stale.

### 5a. Spawning and steering a unit

Example in the Sample: [`SampleUnits.cs`](../samples/Progenitor.Sample/SampleUnits.cs)
(`.rat [distance]|follow|go|patrol|stop|off`). No Sample example yet of the `UnitArrived` /
`UnitUnreachable` events (job J9).

Server-only; on the client every call returns a disabled handle. `ctx.Units` `Track`s each handle,
so unload despawns whatever this plugin spawned — which is the point, because a spawned entity with
no handle left to dispose it is an orphan in somebody's world.

```csharp
using Progenitor.Units;

IUnitHandle rat = ctx.Units.Spawn("CHAR_Critter_Rat", new UnitVec3(x, y, z));
if (!rat.Enabled) return;                       // client, missing prefab, or module skipped

rat.Follow(UnitId.From(characterEntity));       // walk toward a real entity
ctx.Units.Follow(rat, characterEntity);         // same thing, without building the UnitId

rat.GoTo(new UnitVec3(x + 6f, y, z));           // walk toward a position
ctx.Log.LogInfo(rat.Position.ToString());       // live Translation, or the last goal if it is gone

rat.Despawn();                                  // == Dispose(); double-dispose is a no-op
ctx.Units.RemoveAll();                          // everything this plugin spawned
```

`Spawn` takes a spawnable name or a `PrefabGUID` hash, and an optional vanilla behavior-tree prefab
name to bind on the spawn tick (the Sample passes `BEH_Farmlands_Animal_Standard` when it wants a
quiet rat). `Follow` and `GoTo` are exclusive: each clears the other's steering state first, so the
handle's `State` is what the entity actually runs. `handle.Behaviour.Play(...)` / `.Stop()` sequences
`UnitStep`s over the unit's own tree.

Two things will waste your afternoon otherwise:

- **A unit with no connected player nearby does not path at all.** The game disables it
  (`DisabledDueToNoPlayersInRange`, from a grid of *connected* users — an offline character does not
  count) and the AI prioritization budget throttles the rest. Test unit work with a player standing
  next to the spawn.
- **Never write `PathRequest` or a bare marker entity yourself.** Both were tried and both were
  wrong; steering goes through `Follow` / `GoTo`, which write what the game's own approach code
  writes and let the game create the request.

Arrival and failure are events on the normal bus, so they order with everything else:

```csharp
ctx.Events.On<UnitArrived>(e => ctx.Log.LogInfo($"arrived at {e.Position}"));
ctx.Events.On<UnitUnreachable>(e => ctx.Log.LogInfo($"stuck: {e.Reason} (goal {e.Goal})"));
```

`handle.Behaviour.Play(...)` / `.Stop()` sequences `UnitStep`s over the unit's own tree: `Idle`, `GoTo`,
`Follow`, `Face`, `Wait`, `Animate` and `Then` (an `Action` run on the main thread between two steps).
`.rat patrol` in [`SampleUnits.cs`](../samples/Progenitor.Sample/SampleUnits.cs) uses all seven. One rule
about `Idle`: put it **first or not at all**. `Play` already forces one Idle before step 1 and skips its
own when you wrote one there; an Idle posted between later steps reaches a unit that has already walked
somewhere, and its tree goes to `Return` and walks it home.

### 5b. Effects: borrowing a vanilla asset

Example in the Sample: [`SampleFx.cs`](../samples/Progenitor.Sample/SampleFx.cs)
(`.fx image|particle|vignette|beam|off [menu]`) for the client half, and
[`SampleBeam.cs`](../samples/Progenitor.Sample/SampleBeam.cs) (`.beam`, `.beam link`, `.beam mark`)
for the server half. No Sample example yet of `Find` or `AssetKind.Sequence` (job J8). Presets, the
catalog and the raw-write guards: [`effects.md`](effects.md).

Progenitor does not ship art. The Effects module is about **repurposing what the game already has**:
find an asset by kind and name, borrow a copy you own, host it somewhere, re-parameterize it, and
dispose it. One facade, two halves — client borrow, and server-authored beams — and each side
returns a disabled handle for the other side's half.

```csharp
using Progenitor.Effects;

// Client. Discover first: Find is a catalogue query, not a load.
IReadOnlyList<string> hits = ctx.Effects.Find(AssetKind.Image, "Frame");

var image = (UiImageHandle)ctx.Effects.Borrow(AssetKind.Image, "PassiveFrame_Normal");
image.Color = new EffectColor(1f, 0.8f, 0.3f, 1f);
image.Size = new EffectVec2(192f, 192f);
image.SortingOrder = 5000;
ctx.Effects.Host(image, HostKind.Overlay);      // Overlay = UI; RenderTexture = world art in a menu
// … later, or on unload
image.Dispose();
```

`AssetKind` is `Image` / `Particle` / `Volume` / `Sequence`, plus `VisualEffect`, which is catalog
only — `Find` lists it and `Borrow` returns a disabled handle. A world particle or sequence will not
draw on an overlay canvas: that is what `HostKind.RenderTexture` is for. Every handle has `Enabled`
(false on the wrong side or on a miss) and a `Dispose` that is safe to call twice, and `ctx.Effects`
`Track`s them all, so unload cleans up on its own.

The server half spawns a real networked game entity every client draws, so it works with no
client-side plugin: `ctx.Effects.ServerBeam(preset, start, end, seconds)`, `Beams(...)` for a
spaced row of them, and `ServerBeamLink` / `ServerBeamMark` for the entity-tracking forms. Further
beam work is parked by decision; what exists is verified live.

## 6. A hook, and a patch with the unload rule

**Rule one: never hand Harmony a method that lives in your plugin.** Measured on this stack
(.NET 6, HarmonyX 2.10.2): a patch applied and then unpatched from a collectible plugin still keeps
the plugin's ALC alive for the life of the process, for managed and IL2CPP targets alike. Two
append-only statics root it: HarmonyX's `StackTraceFixes.RealMethodMap` and, for IL2CPP targets,
`Il2CppDetourMethodPatcher.DelegateCache`. Neither has a removal path. `ctx.Patches` is the answer:
it never gives Harmony your method. Harmony patches one of 32 process-lifetime slot bodies that
live in Bootstrap, and the slot dispatches to your delegate until `RemoveAll` clears it on unload,
so a hot swap unloads cleanly (the tests prove the ALC collects). The costs: 32 slots per process,
no `__result` access, and `__args`-shaped delegates. The Sample's admin-only `.patchdemo`
([`SamplePatchDemo.cs`](../samples/Progenitor.Sample/SamplePatchDemo.cs)) shows the shape and the
unload-reason handling.

**Use `ctx.Patches` for reloadable patches.** The former `ctx.Harmony` property is now `ctx.UnsafeHarmony`. Reading the property (not patching through
it) logs one warning per plugin and marks your plugin **non-hot-swappable**: from then on the
reloader refuses to swap it in place and tells you to restart, because the pin your patch made
cannot be undone and swapping would simply stack a second pinned generation behind the first. If
you want the edit-run-edit loop, stay on `ctx.Patches`.

Hook through Progenitor first. Most of what a mod wants is already an event or a system hook:

```csharp
ctx.Events.On<ChatMessage>(e =>
{
    if (e.Text.StartsWith("!", StringComparison.Ordinal))
    {
        ctx.Log.LogInfo("chat: " + e.Text + " (" + e.Type + ")");
        e.Cancel();                   // later handlers still run and see IsCancelled
    }
}, priority: 100);

ctx.Systems.Add("mymod.tick", c =>
{
    if (c.Frame % 300 == 0) ctx.Log.LogInfo("still here");
});
```

When you need to run *around* one of the game's own ECS systems rather than on your own schedule,
that is `ctx.Hooks`. Working example:
[`SampleHooks.cs`](../samples/Progenitor.Sample/SampleHooks.cs) (`.hooks`, `.hooks skip [1-5]`),
which hooks a managed system and a Burst one and demonstrates `Skip`. The signatures are
`src/Progenitor.Core/Plugins/PluginContext.cs:494-508` and `Hooks/HookOptions.cs:8`:

```csharp
using Progenitor.Hooks;

ctx.Hooks.Before<ProjectM.ServerBootstrapSystem>(
    h => ctx.Log.LogInfo($"{h.SystemName} on {h.World.Name}"),
    new HookOptions { EveryNFrames = 60, Priority = 100 });

ctx.Hooks.After<ProjectM.MoveEntitySystem>(h => { /* managed and Burst systems both work */ });

ctx.Hooks.Before<SomeSystem>(h => h.Skip = true);   // cancel that system's update for this frame
```

`Skip` is settable from a `Before` hook only. `HookOptions` also carries `OnlyWhenSystemRuns`
(default true) and `Before` / `After` plugin ids. Every registration is tracked on `ctx`, and the
non-generic `Before(Type, …)` / `After(Type, …)` forms exist for a system type you only have as a
`Type`.

**Ordering between plugins** — priority, `before` / `after`, and what a cycle does — is the same
model for events, hooks and virtual systems, and is written up once in
[`hooks-ordering.md`](hooks-ordering.md).

When you genuinely need a patch on a game method, use `ctx.Patches` — never `ctx.UnsafeHarmony` — because
every entry is tagged with your plugin GUID and swept on **every** unload reason:

```csharp
using System.Reflection;

var target = typeof(SomeGameType).GetMethod(nameof(SomeGameType.SomeMethod));
var prefix = typeof(MyPatches).GetMethod(nameof(MyPatches.Prefix),
    BindingFlags.NonPublic | BindingFlags.Static);

PatchHandle? handle = ctx.Patches.Prefix(target!, prefix!);   // priority defaults to 400
if (handle == null)
{
    // Refused, or the apply threw. Already logged; nothing was registered (fail closed).
}
```

The exact `SomeGameType` above is a placeholder; the compiling version — target lookup, the void
prefix, the null check and the `RemoveAll` — is
[`SamplePatchDemo.cs`](../samples/Progenitor.Sample/SamplePatchDemo.cs) (the other consumer is
Core's own god-mode guard). `Prefix` / `Postfix` / `Finalizer` and
`PatchHandle.Dispose()` / `.Id` / `ctx.Patches.RemoveAll()` are real members with the signatures
shown.

Two classes of target are refused outright and return `null`: the **shared empty-body thunk** (the
AOT compiler deduplicates every empty method body onto one native address, a different one on the
client and the server, so detouring one detours tens of thousands) and **Burst jobs**, whose
IL2CPP `Execute` is not the body that runs.

**The unload rule.** `PluginReloader.SweepRegistrations` calls `PatchRegistry.RemoveAll(plugin)` on
every unload reason, no exceptions, because the plugin assembly is going away in all of them and a
patch whose method lives in a dead ALC is a crash, not a convenience. So the handle is optional: you
do not have to hold it, and you must not try to keep a patch alive across a swap. That is the
opposite of the state rule in the next step, and deliberately so.

### Postfix and finalizer

`ctx.Patches.Postfix` and `ctx.Patches.Finalizer` carry the same contract, the same owner tagging and
the same slot routing as `Prefix`; what differs is the return type, and the return type is the whole
safety story. A prefix returns `bool`: whether the original still runs. A postfix returns `void`, so
it cannot change the call's outcome at all; reach for it whenever all you want is to observe, because
there is no way to get it wrong. A finalizer returns `Exception?`, it runs whether the original
returned or threw, and **its return value is the exception the game then sees**: returning `null`
makes a real throw vanish and the caller carries on as though the method had succeeded. The Sample's
`.patchdemo on` installs all three on one harmless server method
([`SamplePatchDemo.cs`](../samples/Progenitor.Sample/SamplePatchDemo.cs)) and reports the three
counters separately; its finalizer counts the exception, writes one log line per Core generation
naming the type and message, and returns the exception it was handed, unchanged, on every path,
including the path where its own bookkeeping threw. Copy that shape. Those are three *independent*
registrations: a refusal of one leaves the other two applied, and `RemoveAll` (or any unload) takes
down whatever is there. Where your correctness needs all-or-nothing, use `PatchRegistry.All`, which
rolls back.

**When every patch has to land or none of them may.** `ctx.Patches.Prefix/Postfix/Finalizer` are
three independent registrations: refuse one and the others stay applied. When your correctness needs
the whole set (Core's god mode guards two kill paths, and `Kill` alone leaves a character alive but
reading as dead), use `PatchRegistry.All(ctx.Owner, ctx.Guid, ...)`, which unpatches every member that
already landed as soon as one is refused and returns `null`. Two things to know before you copy it: it
is the *static* form, so you pass the owner pair yourself (the entries are then tagged exactly as a
`ctx.Patches` one and unload sweeps them the same way), and it does **not** attach the Harmony applier
the way the facade's first call does. `.patchdemo group` in the Sample runs a group that is refused on
purpose and prints `PatchRegistry.Count` either side of it; the two numbers being equal is what
"atomic" means here.

### Virtual systems

When work has to run every tick, do not inject a `SystemBase`: an injected type cannot be taken back
out of a running world and it pins the assembly that declared it. `ctx.Systems.Add("my.tick", vs => ...)`
registers a *named callback* on one `SystemHooks` seam instead, so reload is dispose and the game's
schedule is untouched. The default anchor is after `SimulationSystemGroup`, a settled world, and that
is the one to reach for. When work must land *between* two children of a group, name the system:
`ctx.Systems.AddAfter<ServerBootstrapSystem>("my.tick", ...)` or `AddBefore<...>`, which is the same
thing as passing `VirtualSystemAnchor.After<T>()` / `Before<T>()` to the explicit `Add` overload. Lower
`order` runs first among slots on the same anchor, and re-adding the same name from the same plugin
replaces rather than stacks. `World.Time.ElapsedTime` reads 0 through the interop, so throttle on
`vs.Frame % N == 0`, never on elapsed time. Two undo scopes exist and they are not the same: disposing
the handle drops that one system, while `ctx.Systems.RemoveAll()` drops every virtual system *the
plugin* registered, so reach for the handle unless you mean the second. `ctx.Systems.Describe()` prints
the live slots with their anchors, orders, frame counts and last error, which is the first thing to read
when a callback is not firing. The Sample's `.sys` command
([`SampleSystems.cs`](../samples/Progenitor.Sample/SampleSystems.cs)) does all of this: `.sys on`
registers one system on each of the three anchors, `.sys status` prints the counters and the
`Describe()` dump, `.sys off` disposes the handles and `.sys sweep` shows what the owner-scoped
`RemoveAll()` really costs.

### Reaching a private member

Sooner or later a patch target or a field will look unreachable. On IL2CPP it usually is not:
Il2CppInterop emits everything it emits as **public**, so `private` in the game's source is normally
just a normal call. What is genuinely out of reach is narrower — a member the interop generator
skipped, an IL2CPP **static** field, a member that only exists as one of the interop's own
`NativeFieldInfoPtr_*` / `NativeMethodInfoPtr_*` statics, and a method you want as a raw function
pointer. For those, declare it instead of hand-rolling reflection. Write an `access.txt` beside your
plugin:

```
access v1
assembly ProjectM.Shared
method ProjectM.DeathUtilities Kill
field  ProjectM.SomeType someField : System.Single         # ': <Type>' is REQUIRED on a field
field  ProjectM.SomeType someThing : ProjectM.Thing ref    # ...plus 'ref' if it is a reference
```

The field's type is not optional. `il2cpp_field_get_value` takes no size — it copies the *field's*
width into the buffer you hand it — so declaring `System.Single` is what lets the layer check the
accessor's width once and refuse a mismatch with a sentence naming both, instead of writing past
your local. Add `ref` when the declared type is a class: such a field holds an object pointer and
gets `someThingObject(instance)` / `someThingOffset()` rather than a blittable accessor.

then generate plain C# once and check it in (`--check` in CI tells you when it drifts):

```
dotnet run --project <progenitor>/tools/accessgen -- \
  --declarations MyMod/access.txt --out MyMod/Access \
  --namespace MyMod.Access --build <build id>
```

You now have `Access.DeathUtilities.Kill()` (a `MethodBase` for `ctx.Patches`), `.KillPointer()` (a
detour target), and `Access.SomeType.someField(instance)` get/set. Nothing writes an offset down —
the runtime resolves the field through the interop's own metadata every time — and a member that is
not on the build you are running throws one readable sentence at first use *and* is named ahead of
time in the resilience report, because the generator stamps every declaration into your assembly
where the report's metadata scan can read it. Full grammar and limits (generics and Burst job data
are not declarable): [`modules.md`](modules.md#access). A plugin that has done exactly this, with
both files checked in: [`Access/access.txt`](../samples/Progenitor.Sample/Access/access.txt),
[`Access/Access.Generated.cs`](../samples/Progenitor.Sample/Access/Access.Generated.cs) and the
one read that uses them, [`SampleAccessDemo.cs`](../samples/Progenitor.Sample/SampleAccessDemo.cs)
(`.access`).

`.access` in the Sample drives one form per row: bare `.access` (or `.access field`) reads a
reference-typed field and prints the runtime offset next to the metadata offset; `.access type` prints
`Access.<Type>.Type`, which is the *interop stub* type and not the game's native class; `.access method`
resolves `Access.DeathUtilities.Kill()` (a Harmony / `ctx.Patches` target) and `KillPointer()` (a
native-detour target) without invoking anything; and `.access missing` asks for a member that exists on
no build so the `AccessException` sentence is visible somewhere other than an incident. A *plugin* may
use another assembly's generated facade exactly as it uses its own: the accessors are ordinary public
statics, and the Sample reaches Core's for the method forms rather than declaring a second member of its
own.

### 6a. Custom networking: your own packets

Examples in the Sample. **Server → client**:
[`SampleFx.cs`](../samples/Progenitor.Sample/SampleFx.cs) (`.fx` tells the caller's client which
effect to play), [`SampleUi.cs`](../samples/Progenitor.Sample/SampleUi.cs) and
[`SampleMods.cs`](../samples/Progenitor.Sample/SampleMods.cs). **Client → server and back**:
[`SampleNet.cs`](../samples/Progenitor.Sample/SampleNet.cs) — an **F8** keybind sends a ping with
`SendToServer`, the server validates it in `OnReceiveFromClient`, answers the sender with
`SendToUser` and notifies everyone else with `SendToAll`.

Commands and chat both travel on the game's own text path. When you want structured data, `ctx.Net`
carries an **unmanaged struct** under an event id you pick:

```csharp
using Progenitor.Networking;

// Ids are process-wide, one handler owns one id, and 0x5072____ ("Pr**") is Progenitor's own
// reserved range - Core registers twelve ids there and refuses a plugin registration inside it.
// So derive yours from your own four-letter tag: "MyMd" below, the way the Sample uses "Sm**".
internal const int EventId = 0x4D794D64;          // "MyMd" - your tag, not somebody else's

[StructLayout(LayoutKind.Sequential)]
internal struct Packet { public int Kind; public float Scale; }

// Server → one client, or → every connected client.
ctx.Net.SendToUser(userEntity, EventId, new Packet { Kind = 1, Scale = 2f });   // bool
ctx.Net.SendToAll(EventId, new Packet { Kind = 1, Scale = 2f });                // int: recipients

// Client → server.
ctx.Net.SendToServer(EventId, new Packet { Kind = 2, Scale = 0f });             // bool

// Receiving. The handler is unsafe: you get the raw bytes and read them back typed.
ctx.Net.OnReceive(EventId, OnFromServer);              // client side
ctx.Net.OnReceiveFromClient(EventId, OnFromClient);    // server side

private static unsafe void OnFromServer(int eventId, byte* payload, int length)
{
    if (!CustomEvents.TryRead<Packet>(payload, length, out var packet)) return;
    // …
}

// The server-side handler gets the sender as well, which is what makes validation possible.
private static unsafe void OnFromClient(
    int eventId, Entity userEntity, User user, byte* payload, int length)
{
    if (!CustomEvents.TryRead<Packet>(payload, length, out var packet)) return;
    // …
}
```

The rules, all of them from `CustomEvents`:

- **`unmanaged` payloads only**, at most `CustomEvents.MaxPayloadBytes` (15 KiB), **one custom event
  per packet**. `TryRead<T>` is the only way to read one back — it checks the length before it
  copies.
- **Both `ctx.Net` send overloads exist**: the ones shown use `ctx.World`, and there is a
  `(World, …)` form for code that is not on its own side's world. The `ctx.World` forms return
  `false` / `0` when the world is not ready rather than throwing.
- **Two id rules, both refusals rather than warnings** (job FEX2). `0x5072____` (`"Pr**"`) is
  Progenitor's reserved range — every one of Core's twelve protocol ids lives there, including the
  handshake result that explains a mod-mismatch kick — and a plugin registration inside it is
  refused with one error line. So is a registration on an id a **different assembly** already owns:
  the first owner keeps it. Neither refusal throws; you get a handle that removes nothing, and the
  log says who holds the id. Re-registering your *own* id (a hot reload, say) still replaces your
  own handler.
- **Unload drops only *your* handler** for that id. Everything is `Track`ed for you.
- **The server is not obliged to trust the client.** `OnReceiveFromClient` runs on data a player's
  process built; validate it there, exactly as you would a command argument.

If what you are sending is really a *setting*, use a synced bind
([step 3](#3-config-a-local-entry-and-a-synced-one)) instead — it already has the handshake, the
snapshot and the client-side lock.

### 6b. Handshake rows and event ids

Two things a mod declares before it sends anything: which mods the other side must have, and which
event ids it owns.

`ctx.Handshake.DeclareSelf()` plus `[NetworkCompatibility(ServerMustHave, Exact)]` is the whole answer
for most mods: it puts your guid and version on the wire and kicks a peer that lacks them. The other
calls exist for the rest: `RequireSelf()`/`ProvideSelf()` are the undeclared, one-direction form
`DeclareSelf` falls back to without the attribute, and `Require(guid, version, level, rule)` /
`Provide(...)` record a row for something that is not your plugin: an optional feature, a wire-protocol
version, a client-only half. `.hs` in the Sample
([`SampleHandshake.cs`](../samples/Progenitor.Sample/SampleHandshake.cs)) prints every row it declared
and which call made it.

Two rules the reply is built around. The table has 16 slots (`Handshake.MaxMods`), it is keyed by
**guid**, so two calls on one guid cost one slot, and a `ClientOnly` row costs **none**, because the
server neither compares it nor sends it. And rows differ in strength: `ServerMustHave` can refuse a
join, `NoNeedForSync` only compares when both sides have it, `ClientOnly` never compares. Adding a row
that is weaker than one you already have cannot create a new kick; adding a stronger one can, silently,
to players who were joining yesterday.

Event ids are the other half. `0x5072____` ("Pr**") is Progenitor's own range: `OnReceive` refuses a
plugin handler on it and every send entry point refuses to send on it, so a mod that picks one gets
**silence**, not an error. Ask rather than guess: `CustomEvents.IsReservedEventId(id)`, which
`.net reserved <id>` in the Sample runs on whatever you type. Derive yours from a four-letter ASCII tag
of your own; the Sample's are `SmNp` / `SmNr` / `SmFx` / `SmUi`. Ids are process-wide and one handler
owns one id: a second plugin registering an id you already hold is refused and your registration is
kept.

### 6c. Make your mod work across versions

A player's client and the server they join will not always run the same version of your mod. Five
steps let a newer build and an older one talk, and prove it in a test. The worked example is
[`samples/Progenitor.SampleServerLed`](../samples/Progenitor.SampleServerLed/README.md) (its messages
in `ServerLedMessages.cs`, its tests in
`tests/Progenitor.Testing.Tests/Samples/Progenitor.SampleServerLed/`); `Progenitor.SampleClientLed` is the
same pattern the other way round, and `Progenitor.CompatTest` shows two versions of one message. Status:
built and game-free tested; the samples' new wire behavior is **not verified live**. The reference is
[modules.md](modules.md#testing-your-mod-across-versions-mod-compatibility-c5).

1. **Use semantic versions and follow the contract.** A minor release may add a message, a field or a
   feature; removing or retyping anything is a major release. The samples that declare a range carry a
   literal version (`ServerLedDeclaration.Version = "1.0.0"`) rather than the repository's date version,
   so major and minor mean something.

2. **Move your packets to versioned messages**, in a file of their own that uses only
   `Progenitor.Networking` (step 4 links it into a test):

   ```csharp
   [ModMessage("prefs")]
   internal sealed class PrefsMessage
   {
       [Field(1)] public int Kind;
       [Field(2)] public bool Receive = true;
       [Field(3)] public int Delivery;
       // 1.1.0 may add [Field(4, Since = 2)] with Version = 2; a 1.0.0 reader skips it.
   }

   ctx.Messages.On<PrefsMessage>((from, msg, info) => { if (ServerLedMessages.IsValid(msg)) Apply(from, msg); });
   ctx.Messages.SendToServer(new PrefsMessage { Receive = false });
   ```

3. **Offer what each side can do, and ask before sending.** The sample's client part offers `popups`
   and its server part `prefs`; the client sends preferences only when `ctx.Peer.Has("prefs")`, and the
   server sends a popup only to a client whose `ctx.Peers.Of(user).Has("popups")`, else a chat line,
   as before.

   ```csharp
   ctx.Features.Offer("popups");                                       // client part
   if (ctx.Peer.Has("prefs")) ctx.Messages.SendToServer(prefs);         // the server part reads them
   ```

4. **Declare the range you test.** `[NetworkCompatibility(level, rule, Peers = ">=1.0.0 <2.0.0")]`
   ([C3](modules.md#compatibility-ranges-and-version-matching-mod-compatibility-c3)). Then a test project
   references `src/Progenitor.Testing`, links the message file and keeps fixtures:

   ```xml
   <ProjectReference Include="..\..\src\Progenitor.Testing\Progenitor.Testing.csproj" />
   <Compile Include="..\MyMod\MyModMessages.cs" Link="Linked\MyModMessages.cs" />
   <None Include="Fixtures\**\*" CopyToOutputDirectory="PreserveNewest" />
   ```

   ```csharp
   var fixtures = MessageFixtures.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures"));
   var types = MessageTypes.In(typeof(PrefsMessage).Assembly, typeof(PrefsMessage).Namespace);
   MessageCompat.Replay<PrefsMessage>(fixtures, (m, f) => Assert.True(ServerLedMessages.IsValid(m))).ThrowIfFailed();
   MessageCompat.AssertSkipsUnknownFields(new PrefsMessage { Receive = false });
   // 1.0.0 is the first release: nothing older backs the range yet, so allow it (the result notes "not proven").
   MessageCompat.CheckRange(">=1.0.0 <2.0.0", "1.0.0", fixtures, types, allowSelfOnly: true).ThrowIfFailed();
   ```

5. **Record each release's messages and keep them.** Live: `messages_record {"action":"on","mod":"<your guid>"}`
   on the test client and server ([bridge-ops-join.md](bridge-ops-join.md#messages_record)), play the feature, copy
   `BepInEx/progenitor-dev/messages/<your guid>/` into `Fixtures/`. From a test:
   `MessageFixtures.SaveRelease(dir, guid, "1.0.0", messages)`, which refuses a version whose folder already
   exists; the samples do this behind `PROGENITOR_WRITE_FIXTURES=1` and check the files in. Never rewrite a
   released version's fixtures: they are the baseline. When 1.1.0 adds a field, the 1.0.0 fixtures stay, 1.1.0's
   test runs `CheckRange(range, "1.1.0", …)` without `allowSelfOnly`, and a field removed, renumbered or retyped
   since 1.0.0 fails it; keep a copy of the 1.0.0 class in the test (as
   `CompatTestReplayTests` does with `HelloV1`) to prove 1.0.0 reads what 1.1.0 sends.

```
dotnet test tests/Progenitor.Testing.Tests --filter "FullyQualifiedName~Progenitor.SampleTests"
```

The Mods menu then marks the mod **Works across versions**, and its page's **For developers** section
lists the declared range, the features and the messages ([mods-menu.md](mods-menu.md#works-across-versions-and-for-developers-c5)).

## 7. Keeping state across a hot swap

The Sample's own version of this override, including the one line it logs naming the reason and
the branch it took, is in [`Plugin.cs`](../samples/Progenitor.Sample/Plugin.cs) (`OnUnload`), and
the keep-vs-sweep rule for a persisted buff is in Core's
[`DebugBuff.cs`](../src/Progenitor.Core/Debugging/DebugBuff.cs) (`ShouldSweep`, `Reset(reason)`).

Override the reason-taking overload. Its default body calls the parameterless one, so a plugin
written before this keeps working and exactly one of the two runs per unload.

```csharp
protected override void OnUnload(MyContext ctx, PluginUnloadReason reason)
{
    if (_stack != null && _stackChanged != null) _stack.SettingChanged -= _stackChanged;
    _stackChanged = null;
    _stack = null;

    if (!PluginUnloadReasonPolicy.KeepsState(reason))
    {
        // Removal only: this DLL is not coming back. Undo anything only this plugin can undo —
        // a buff on a character, a spawned structure — or it is stuck in the world with no
        // command left to clear it.
        SweepMyBuffFromEveryWearer();
    }
    // Swap / Shutdown / Unknown: keep it. The next generation finds it again by query.
}
```

| reason | what happened | what to do |
|---|---|---|
| `Swap` | Core generation change or an allowed replacement of this plugin DLL. Check that the new instance actually loads. | **Keep** live world state the next generation can re-find by query (a buff instance, a save row). |
| `Removal` | The DLL was deleted from the reload folder, or its replacement was rejected by the allow policy. | **Undo** anything only this plugin can undo. This is the only sweeping reason. |
| `Shutdown` | The process is exiting. | Same as `Swap` — the save carries it, and a sweep only costs the player their buff on the next start. |
| `Unknown` | Nothing established a reason. | Same as `Swap` — **keep**. A wrong keep is recoverable; a wrong sweep is not. |

`PluginUnloadReasonPolicy.KeepsState(reason)` is that table in one call (`reason != Removal`). Every
unload logs one line naming the reason and where it came from:
`Reload: MyMod unload reason=Swap (bootstrap-slot)`.

Handles Core owns — units, beams, UI roots, commands, events, hooks, prefab registrations, patches —
are swept for you on **every** reason, and must be: keeping a spawned unit across a swap would orphan
the entity with no handle left to dispose it. The keep rule is only about plain persisted game state
like a buff instance.

One thing a hot swap invalidates that the reason enum does not cover: **a raw entity id you stored**.
`ctx.WorldState.LoadEpoch` is generation-stamped, so a stamp taken by the previous generation, including
one you persisted through `ModSave`, is stale by construction, and `ctx.WorldState.IsStale(stamped)` says
so. Keep the epoch next to the id and drop the pair together. `.epoch` in
[`SampleEpoch.cs`](../samples/Progenitor.Sample/SampleEpoch.cs) prints both routes (the facade and the
static `WorldEpoch.Current` / `FormatStale`). Never call `WorldEpoch.Advance` yourself: it is Core's
report that the *game* finished a world load, and calling it makes every castle handle and god-mode guard
in the process act on a load that did not happen.

## 8. Testing with the Dev bridge

`Progenitor.Dev` is a loopback JSON bridge (127.0.0.1, token file) plus an MCP server, for driving a
running game from a coding session. **Do not ship `Progenitor.Dev.dll` to a public host**; the public
zip omits it and `tools/package.ps1` asserts that it did. The bridge does not even bind unless
`DevGate` passes: an explicit opt-in (`BepInEx/config/progenitor-dev.enable`, or `PROGENITOR_DEV=1`),
a loopback bind, no master-server listing or public bind, and a positive private-instance signal.

Run from the repository root during an authorized isolated test session. These commands are source-checked examples; they are not a new live test result. Use a JSON file for PowerShell arguments:

```powershell
node tools/mcp/call.mjs server ping
[IO.File]::WriteAllText((Join-Path $PWD 'mymod-logs.json'), '{"since":-1,"max":50,"filter":"MyMod"}', [Text.UTF8Encoding]::new($false))
node tools/mcp/call.mjs server logs @mymod-logs.json
node tools/mcp/call.mjs server users
```

To invoke a command, put `text` and the intended connected test user's `user` name in a JSON file, then call `server command @command.json`. Do not create a ghost just to follow this example. Delete the local request JSON files when finished.

The five ops a plugin author reaches for most:

| Op | What it gives you |
|---|---|
| `teleport` | Move a character to a point or onto another: `{"user":"TestAdmin","x":-2125,"y":5,"z":-3167.6}` or `{"user":"TestAdmin","to":"Ghost1"}`. |
| `give` | Add an item stack: `{"user":"TestAdmin","prefab":"Item_Ingredient_Stone","amount":25}`; reports `added` / `didNotFit`. |
| `inspect` | Every component on one entity with decoded fields in one call: `{"entity":"45:1"}`, narrowed with `only`, deepened with `depth`. |
| `snapshot` | One entity's unmanaged component blobs out, base64 plus decoded, each row carrying a layout stamp. **Not a save** — use `world_save`. |
| `restore` | Those blobs back in. Refuses a size or stamp mismatch, a managed/shared/buffer component, or one that is now missing; `strict` (default) writes nothing unless every row passes. |

`command` injects a chat command without a player typing it, `ghost` makes a fake server-side user to
run it as, and `reload` / `core_reload` / `reload_status` drive the loop in the next step by hand.
The full op list and every argument is in the [bridge operation reference](bridge-ops.md).

## 9. The hot-reload loop, including Edit-and-Continue

Follow [hot-reload.md](hot-reload.md) for the current build, targeted deployment and completion checks. The [minimal reload example](../samples/Progenitor.ReloadExample/README.md) provides a buildable command/event exercise with expected results and cleanup. Its live acceptance steps are explicitly marked as not yet run.

Plugin DLLs and compatible Core generations can reload in process. Bootstrap changes require restarting each affected process. Always check the loaded image and resulting behavior; copying a file or scheduling a swap does not prove the update succeeded.

Edit-and-Continue is a separate, experimental method-body update path. It does not replay registrations and has not yet been verified inside V Rising's embedded runtime. Keep it outside the beginner workflow; prerequisites, limits and diagnostic steps are in the [development-loop reference](dev-loop.md#method-body-hot-reload-edit-and-continue--the-inner-loop).

## Three rules to leave with

1. **Hook through Progenitor, never raw Harmony on an IL2CPP method.** `ctx.Events`, `ctx.Hooks`,
   `ctx.Systems`, `ctx.Patches`, or a Bootstrap-owned slot via `HarmonySlotHandoff.BindPlugin`.
   `ctx.UnsafeHarmony` (was `ctx.Harmony`) is an opt-out of hot reload: taking it marks your plugin
   non-hot-swappable and the reloader will refuse to swap it.
2. **Declare `ServerMustHave` with `Exact` or `Minor` when you `Content.Edit`.** Without it the
   editor is refused outright. Edited values *do* reach a Progenitor client — the server ships the
   touched components as a `PrfE` layer ([`edits-on-the-wire.md`](edits-on-the-wire.md))
   — so what the declaration keeps out is a peer with no plugin at all, which would keep unedited
   stats. Phase 2 relaxes it for wire-safe edits; the two-process live check is owed.
3. **BCL types only on the shared file and on synced binds**, and bind your own settings on your own
   `GUID.cfg`.

And the one that makes the rest work: **every registration must undo.** Every `ctx.*` helper `Track`s
an `IDisposable` for you; anything else — such as a file handle — goes
through `ctx.Track(...)` when it is safely disposable. Tracking does not make native detours or raw Harmony registrations safe to unload; use a supported Bootstrap-owned seam.
