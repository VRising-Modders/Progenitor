using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx;
using Progenitor.Commands;
using Progenitor.Castles;
using Progenitor.Inventory;
using Progenitor.Plugins;
using Progenitor.WorldState;
using Unity.Entities;

namespace Progenitor.SharedResourcesTest;

[BepInPlugin("progenitor.sharedresourcestest", "Shared Resources Test", "0.1.0")]
[BepInDependency("progenitor")]
[ProgenitorSide(Side.Server)]
public sealed class Plugin : ProgenitorPlugin<TestContext>
{
    internal static TestContext? Active;
    protected override void OnLoad(TestContext ctx)
    {
        Active = ctx;
        ctx.Commands.Register(typeof(TestCommands));
        ctx.Events.On<WorldLoaded>(_ => ctx.Clear());
        ctx.Log.LogInfo("Shared Resources Test loaded. .srtest status; capture commands never move items.");
    }
    protected override void OnUnload(TestContext ctx, PluginUnloadReason reason)
    {
        ctx.Clear();
        Active = null;
        ctx.Log.LogInfo("Shared Resources Test unloaded; fixture and captures cleared: " + reason);
    }
}

public sealed partial class TestContext : PluginContext
{
    internal readonly Fixture Fixture = new();
    internal readonly Dictionary<string, InventoryRef> Containers = new(StringComparer.Ordinal);
    internal readonly Dictionary<string, ContainerId> SourceIds = new(StringComparer.Ordinal);
    internal Entity Actor;
    private int _epoch;
    internal string Operation = "none";
    internal int Territory = -1;
    internal string ProviderStatus = "unavailable (inventory evidence only; shared-resource framework hooks not integrated)";
    internal bool ProviderEnabled;

    internal void Require(ICommandContext command)
    {
        if (!command.IsAdmin) throw new InvalidOperationException("Administrator required.");
        if (_epoch != WorldState.LoadEpoch) { Clear(); _epoch = WorldState.LoadEpoch; }
        if (Actor != Entity.Null && Actor != command.UserEntity)
            throw new InvalidOperationException("Another administrator owns this fixture. Its owner must clear it first.");
        Actor = command.UserEntity;
    }

    internal void Bind(string alias, Entity owner, bool source)
    {
        if (Fixture.Active) throw new InvalidOperationException("End the capture before binding.");
        if (ProviderEnabled) throw new InvalidOperationException("Disable the provider before changing sources.");
        if (string.IsNullOrWhiteSpace(alias) || alias.Length > 32) throw new ArgumentException("Alias must contain 1–32 characters.");
        if (Containers.ContainsKey(alias)) throw new ArgumentException("Alias already bound; clear the fixture before replacing it.");
        var reference = Inventory.Of(owner);
        var snapshot = Inventory.Read(reference);
        if (!snapshot.Ok) throw new InvalidOperationException(snapshot.Reason);
        foreach (var row in Containers)
            if (row.Key != alias && row.Value == reference) throw new InvalidOperationException("This physical inventory already has alias " + row.Key);
        ContainerId id = default;
        if (source)
        {
            var castle = Progenitor.Castles.Castles.At(CastleId.From(owner));
            if (!castle.Enabled || castle.Raided || castle.Heart == CastleId.From(owner))
                throw new InvalidOperationException("Sources must be ordinary containers on an active, non-raided castle plot.");
            var relation = Castles.RelationTo(Actor, castle);
            if (relation != CastleRelation.Owner && relation != CastleRelation.ClanMember)
                throw new InvalidOperationException("Test sources must be owned by the testing player or their clan.");
            if (!castle.Containers().Any(x => x.Entity == CastleId.From(owner) && !x.IsStation))
                throw new InvalidOperationException("Use an ordinary chest; stations are watch-only.");
            id = Inventory.IdOf(owner, out string reason);
            if (id.IsNone) throw new InvalidOperationException(reason);
        }
        Containers[alias] = reference;
        SourceIds.Remove(alias);
        if (source) SourceIds.Add(alias, id);
        InvalidateProvider();
    }

    internal int Count(string alias, int item)
    {
        if (!Containers.TryGetValue(alias, out var reference)) throw new InvalidOperationException("Unknown alias " + alias);
        var read = Inventory.Read(reference);
        if (!read.Ok) throw new InvalidOperationException(alias + ": " + read.Reason);
        return read.Count(item);
    }

    internal void Report(ICommandContext command, string text)
    {
        Log.LogInfo("Shared Resources Test: " + text);
        command.Reply(text);
    }

    internal void Clear()
    {
        DisableProvider();
        ProviderEnabled = false;
        Fixture.Clear(); Containers.Clear(); SourceIds.Clear(); Actor = Entity.Null; Operation = "none"; Territory = -1;
    }
    partial void DisableProvider();
    partial void EnableProvider(ref bool enabled);
    internal void Enable()
    {
        if (Fixture.Active) throw new InvalidOperationException("End the capture first.");
        if (ProviderEnabled) throw new InvalidOperationException("Provider already enabled.");
        bool enabled = false;
        EnableProvider(ref enabled);
        if (!enabled) throw new InvalidOperationException("The framework material provider is unavailable. This build can capture baseline evidence only.");
        ProviderEnabled = true;
    }
    internal void Disable()
    {
        if (Fixture.Active) throw new InvalidOperationException("End the capture first.");
        DisableProvider(); ProviderEnabled = false;
    }
    internal void Invalidate() => InvalidateProvider();
    partial void InvalidateProvider();
    internal void RefreshStatus() => RefreshProviderStatus();
    partial void RefreshProviderStatus();
}

[CommandGroup("srtest")]
public static class TestCommands
{
    [Command("diagnose", adminOnly: true, description: "Read inventory discovery diagnostics for the test plot.")]
    public static void Diagnose(ICommandContext c, int territory) => Run(c, t =>
    {
        var castle = Progenitor.Castles.Castles.ForTerritory(territory);
        var world = Progenitor.Worlds.Server!;
        var heart = new Entity { Index = castle.Heart.Index, Version = castle.Heart.Version };
        var plot = CastleReader.PlotOf(world, heart);
        t.Report(c, $"diagnose heart={castle.Heart} enabled={castle.Enabled} plotFound={plot != null} containers={castle.Containers().Count} stations={castle.Stations().Count}");
        if (plot == null) return;
        foreach (var b in CastleReader.Buildings(world, plot))
        {
            var inv = t.Inventory.Read(t.Inventory.Of(b.Entity));
            if (inv.Ok) t.Report(c, $"diagnose inventory={b.Entity.Index}:{b.Entity.Version} rows={inv.Slots.Count} items={string.Join(",", inv.Slots.Where(s => !s.IsEmpty).Select(s => s.Item + ":" + s.Amount))}");
        }
    });
    [Command("plots", adminOnly: true, description: "List eligible plots and their ordinary-container stock for one ingredient.")]
    public static void Plots(ICommandContext c, int item) => Run(c, t =>
    {
        foreach (var castle in Progenitor.Castles.Castles.All)
        {
            var relation = t.Castles.RelationTo(c.UserEntity, castle);
            if (!castle.Enabled || castle.Raided || (relation != CastleRelation.Owner && relation != CastleRelation.ClanMember)) continue;
            long count = 0; int unreadable = 0;
            var rows = castle.Containers().Where(x => !x.IsStation && x.Entity != castle.Heart).ToArray();
            foreach (var row in rows)
            {
                var read = t.Inventory.Read(row.Main);
                if (read.Ok) count += read.Count(item); else unreadable++;
            }
            t.Report(c, $"territory={castle.Territory.Index} floors={castle.FloorCount} chests={rows.Length} item={item} stock={count} unreadable={unreadable}");
        }
    });

    [Command("containers", adminOnly: true, description: "List container entity IDs and counts on one eligible plot.")]
    public static void Containers(ICommandContext c, int territory, int item) => Run(c, t =>
    {
        var castle = Progenitor.Castles.Castles.ForTerritory(territory);
        var relation = t.Castles.RelationTo(c.UserEntity, castle);
        if (!castle.Enabled || castle.Raided || (relation != CastleRelation.Owner && relation != CastleRelation.ClanMember))
            throw new InvalidOperationException("Plot is not eligible for this test.");
        foreach (var row in castle.Containers().Take(200))
        {
            var read = t.Inventory.Read(row.Main);
            t.Report(c, $"entity={row.Entity} station={row.IsStation} item={item} count={(read.Ok ? read.Count(item).ToString() : "UNREADABLE")} id={row.Id}");
        }
    });
    private static void Run(ICommandContext command, Action<TestContext> action)
    {
        try
        {
            var test = Plugin.Active ?? throw new InvalidOperationException("Test mod not loaded.");
            test.Require(command); action(test);
        }
        catch (Exception e)
        {
            Plugin.Active?.Log.LogWarning("Shared Resources Test refused: " + e);
            command.Reply("Test refused: " + e.Message);
        }
    }

    [Command("status", adminOnly: true)]
    public static void Status(ICommandContext c) => Run(c, t =>
    {
        t.RefreshStatus();
        t.Report(c, $"provider={t.ProviderStatus}; enabled={t.ProviderEnabled}; territory={t.Territory}; epoch={t.WorldState.LoadEpoch}; sources={t.SourceIds.Count}; capture={t.Fixture.Active}; operation={t.Operation}");
    });

    [Command("scope", adminOnly: true, description: "Restrict the provider to this administrator on one test territory.")]
    public static void Scope(ICommandContext c, int territory) => Run(c, t =>
    {
        if (t.ProviderEnabled || t.Fixture.Active) throw new InvalidOperationException("End capture and disable before changing scope.");
        if (territory < 0) throw new ArgumentException("Use a valid nonnegative territory index.");
        t.Territory = territory; t.Report(c, "Test territory=" + territory);
    });

    [Command("source", adminOnly: true, description: "Bind a real chest by its current entity index and version.")]
    public static void Source(ICommandContext c, string alias, int index, int version) => Run(c, t =>
    { t.Bind(alias, new Entity { Index = index, Version = version }, true); t.Report(c, alias + "=" + t.SourceIds[alias]); });

    [Command("watch", adminOnly: true, description: "Watch a station or other inventory without making it a material source.")]
    public static void Watch(ICommandContext c, string alias, int index, int version) => Run(c, t =>
    { t.Bind(alias, new Entity { Index = index, Version = version }, false); t.Report(c, "Watching " + alias); });

    [Command("bag", adminOnly: true)]
    public static void Bag(ICommandContext c, string alias = "bag") => Run(c, t =>
    { t.Bind(alias, c.CharacterEntity, false); t.Report(c, "Watching player bag as " + alias); });

    [Command("rank", adminOnly: true, description: "Set deposit rank for one ingredient: 0=best home, larger=drained earlier.")]
    public static void Rank(ICommandContext c, int item, string alias, int rank) => Run(c, t =>
    {
        if (t.ProviderEnabled) throw new InvalidOperationException("Disable the provider before editing policy.");
        if (!t.SourceIds.ContainsKey(alias)) throw new ArgumentException("Bind a source alias first.");
        t.Fixture.Rank(item, alias, rank); t.Invalidate();
        t.Report(c, "Spend order " + item + ": " + string.Join(" -> ", t.Fixture.Order(item)));
    });

    [Command("cost", adminOnly: true, description: "Configure one full-operation ingredient cost and pool reserve.")]
    public static void Cost(ICommandContext c, int item, int amount, int reserve = 0) => Run(c, t =>
    { if (t.ProviderEnabled) throw new InvalidOperationException("Disable the provider before editing policy.");
      t.Fixture.Cost(item, amount, reserve); t.Invalidate(); t.Report(c, $"Cost {item}={amount}, pool reserve={reserve}"); });

    [Command("enable", adminOnly: true)]
    public static void Enable(ICommandContext c) => Run(c, t => { t.Enable(); t.Report(c, "Provider enabled: " + t.ProviderStatus); });

    [Command("disable", adminOnly: true)]
    public static void Disable(ICommandContext c) => Run(c, t => { t.Disable(); t.Report(c, "Provider disabled; normal game behavior restored."); });

    [Command("baseline", adminOnly: true, description: "Capture the disabled-provider attempt on a resource-poor plot; expect no payment.")]
    public static void Baseline(ICommandContext c, string operation) => Run(c, t =>
    {
        if (t.ProviderEnabled) throw new InvalidOperationException("Disable the provider first.");
        Capture(c, t, operation, false);
    });

    [Command("begin", adminOnly: true, description: "Capture all configured ingredients; then perform exactly one normal game operation.")]
    public static void Begin(ICommandContext c, string operation) => Run(c, t =>
    {
        if (!t.ProviderEnabled) throw new InvalidOperationException("Enable the provider first, or use baseline for the disabled test.");
        Capture(c, t, operation, true);
    });

    private static void Capture(ICommandContext c, TestContext t, string operation, bool expectPayment)
    {
        if (operation != "build" && operation != "craft") throw new ArgumentException("Operation must be build or craft.");
        var bag = t.Inventory.Of(c.CharacterEntity);
        if (!t.Containers.Any(x => x.Value == bag && !t.SourceIds.ContainsKey(x.Key)))
            throw new InvalidOperationException("Watch the player bag with .srtest bag before capturing.");
        foreach (int item in t.Fixture.Costs.Keys)
            foreach (string alias in t.Containers.Keys)
                if (!t.SourceIds.ContainsKey(alias) && t.Count(alias, item) != 0)
                    throw new InvalidOperationException("Empty local test ingredients from watched inventory " + alias + " first.");
        t.Fixture.Begin(t.Containers.Keys, t.Count, expectPayment); t.Operation = operation;
        foreach (string row in t.Fixture.Baseline()) t.Report(c, row);
        t.Report(c, $"Capture armed: expected {(t.Fixture.ExpectedSuccess ? "complete payment" : "zero debit")}. Perform one {operation}, then verify. Provider: {t.ProviderStatus}");
    }

    [Command("verify", adminOnly: true, description: "Compare inventory deltas; does not assert game action success or displayed counts.")]
    public static void Verify(ICommandContext c) => Run(c, t =>
    {
        var errors = t.Fixture.Verify(t.Count);
        foreach (string error in errors) t.Report(c, "MISMATCH " + error);
        t.Report(c, errors.Length == 0
            ? "Inventory deltas MATCH. Separately verify the placed object/craft queue, displayed costs and no concurrent inventory activity. This is not an end-to-end PASS."
            : "Inventory deltas FAIL (" + errors.Length + "). Capture retained for inspection.");
    });

    [Command("end", adminOnly: true)]
    public static void End(ICommandContext c) => Run(c, t => { t.Fixture.End(); t.Operation = "none"; t.Report(c, "Capture ended; configuration retained."); });

    [Command("clear", adminOnly: true)]
    public static void Clear(ICommandContext c) => Run(c, t => { t.Clear(); t.Report(c, "Test configuration cleared; provider detached."); });
}
