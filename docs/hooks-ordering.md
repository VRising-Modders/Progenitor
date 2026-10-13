# Hook and event ordering

How `SystemHooks` and `EventBus` order plugin callbacks. Prior art is in the internal research notes. The shared sorter is `Progenitor.Plugins.OrderSorter`, lifted from Content.Edit's `PrefabEditOrder` without changing that module's behavior. All three users refuse the registration that would close a before/after cycle; acyclic edges at the site stay.

## Why

Two plugins on the same ECS system or the same event used to share only an integer priority. That is enough for Core (chat Harmony 300 ahead of another chat-command framework, typically at 200; the command subscriber at bus priority 500) and not enough for two consumer mods that need a relative order without agreeing on a number. Harmony already gives method patches `priority` plus `HarmonyBefore` / `HarmonyAfter` owner ids. This module is that model on Progenitor's own seams.

## Order

For one event type, or one system × Before/After phase:

1. Numeric **priority**. Higher runs first (Harmony `First = 800`). Default is 0.
2. Harmony-style **before** / **after** plugin ids as a topological constraint. These can override the number.
3. Plugin **load order** (first-seen BepInEx GUID in this Core generation, shared across hooks and events). Sticky for the generation: a plugin that unloads and reloads keeps the same slot, so equal-priority order does not reshuffle.
4. **Registration index** (last tiebreak, so equal keys stay insertion-ordered).

Missing before/after ids (the other plugin is not registered on this site) are ignored, same as Harmony. Ids compare case-insensitively.

A before/after **cycle** is refused with one Warn line naming the dropped registration and the cycle path. Only that registration is dropped (the one being added, same as Content.Edit `TryAdd`). Every acyclic edge at the site stays: A ↔ B does not throw away C's `after: D` on the same event or system phase. Harmony silently culls one edge; this path names the closer and keeps the rest of the graph.

Per-site and per-event order is re-resolved on every subscribe and on dispose / `RemoveAll` of **that site or event type only**, so a hot-reloaded plugin cannot leave a stale graph and unloading one plugin does not re-toposort every other system.

## API

```csharp
// Event bus. pluginId is this plugin's BepInEx GUID so others can name it.
EventBus.On<ChatMessage>(OnChat, priority: 10, owner: this,
    before: new[] { "other.mod" }, after: null, pluginId: "my.mod");

// System hooks: fields on HookOptions, or the priority overload.
ctx.Hooks.Before<ServerBootstrapSystem>(OnBefore, new HookOptions
{
    Priority = 100,
    Before = new[] { "other.mod" },
    PluginId = ctx.Guid,
});
SystemHooks.After<ProjectM.MoveEntitySystem>(OnAfter, priority: 0,
    after: new[] { "other.mod" }, pluginId: "my.mod");
```

`ctx.Events.On<T>(handler, priority)` passes this plugin's GUID as `pluginId` (wired on the PluginContext side) so equal-priority handlers stay in load order across a reload. It does not take before/after. Pass those on `EventBus.On` and `ctx.Track` the disposable, or set `HookOptions` on `ctx.Hooks`.

## Sample

`samples/Progenitor.Sample` registers two `WorldReady` handlers in reverse priority order (100 first, then 0) with `after` so the second registration runs first. Each logs one ordinal line. Install (and `.order`) print `EventBus.DescribeOrder<WorldReady>()`.

```csharp
// Higher priority registered first — would run first by number alone.
ctx.Track(EventBus.On<WorldReady>(OnSecond, priority: 100, owner: ctx.Owner,
    after: new[] { "progenitor.sample.order.first" },
    pluginId: "progenitor.sample.order.second"));
ctx.Track(EventBus.On<WorldReady>(OnFirst, priority: 0, owner: ctx.Owner,
    pluginId: "progenitor.sample.order.first"));
ctx.Log.LogInfo("Sample: " + EventBus.DescribeOrder<WorldReady>().Replace("\n", " | "));
```

Live grep on both sides (WorldReady fires on Server and Client_0):

```
Sample: WorldReady order=1 id=progenitor.sample.order.first
Sample: WorldReady order=2 id=progenitor.sample.order.second
```

The install dump contains `progenitor.sample.order.first prio=0` then `progenitor.sample.order.second prio=100 after=[progenitor.sample.order.first]`.

## Diagnostic dump

A `hooks_order` bridge op is not in this change. Print the live order with:

```csharp
EventBus.DescribeOrder<ChatMessage>();
SystemHooks.DescribeOrder<ServerBootstrapSystem>();
SystemHooks.DescribeOrder(typeof(ProjectM.MoveEntitySystem));
SystemHooks.DescribeOrder("MoveEntitySystem");
```

Empty sites dump `(none)` rather than throwing.


```
EventBus ChatMessage:
  1. progenitor prio=500
  2. progenitor.sample prio=0 after=[progenitor]
SystemHooks MoveEntitySystem Before:
  1. mod.a prio=100
SystemHooks MoveEntitySystem After: (none)
```

## Expected log lines

Cycle (dropped closer; acyclic edges at the site stay):

```
EventBus: refused before/after cycle on ChatMessage from b.id: a.id -> b.id -> a.id; dropping that registration
SystemHooks: refused before/after cycle on MoveEntitySystem Before from b.id: a.id -> b.id -> a.id; dropping that registration
```

Self-before is the one-node form: `a.id -> a.id`.

Content.Edit still logs its own refuse-and-drop line (`PrefabRegistry: refused edit from {id} on guid {hash}: before/after cycle with {path}`) and is unchanged.
