using System;
using System.Collections.Generic;
using Progenitor.Content;
using Progenitor.Ecs;
using Progenitor.Events;
using Progenitor.Hooks;
using Progenitor.Plugins;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Game-free coverage of ordered prefab edits (roadmap M2.4). Fake prefabs are a
/// component-name bag; live ECS is <c>PrefabEditHandle</c> in Core and is not linked here.
/// </summary>
public class PrefabEditTests
{
    private const int GuidHash = 42;

    [Fact]
    public void Priority_runs_early_then_default_then_late()
    {
        var table = new PrefabEditTable();
        var prefab = new FakePrefab();
        var order = new List<string>();
        Add(table, "late", PrefabEditPriority.Late, p => { order.Add("late"); p.Write("ItemData", 3); });
        Add(table, "early", PrefabEditPriority.Early, p => { order.Add("early"); p.Write("ItemData", 1); });
        Add(table, "default", PrefabEditPriority.Default, p => order.Add("default"));

        var (records, warns) = Run(table, prefab);

        Assert.Equal(new[] { "early", "default", "late" }, order);
        Assert.Equal(3, prefab.Values["ItemData"]);
        Assert.Equal(new[] { "early.id", "default.id", "late.id" }, PluginIds(records));
        Assert.Equal(
            new[]
            {
                PrefabEditLog.Conflict(
                    GuidHash,
                    "ItemData",
                    "early",
                    "early.id",
                    "late",
                    "late.id",
                    PrefabEditLog.ReasonPriority((int)PrefabEditPriority.Late, (int)PrefabEditPriority.Early)),
            },
            warns);
    }

    [Fact]
    public void Same_priority_uses_plugin_load_order_then_registration()
    {
        var table = new PrefabEditTable();
        var order = new List<string>();
        Add(table, "a", PrefabEditPriority.Default, _ => order.Add("a"));
        Add(table, "b", PrefabEditPriority.Default, _ => order.Add("b"));
        Add(table, "a-second", PrefabEditPriority.Default, _ => order.Add("a-second"), pluginId: "a.id");

        Run(table, new FakePrefab());

        // a and a-second share load order (first-seen plugin id); registration index breaks the tie.
        Assert.Equal(new[] { "a", "a-second", "b" }, order);
    }

    [Fact]
    public void Before_after_overrides_numeric_priority()
    {
        var table = new PrefabEditTable();
        var order = new List<string>();
        // Early wants to run after Late, so Late is first despite Early's lower number.
        Add(table, "early", PrefabEditPriority.Early, _ => order.Add("early"), after: new[] { "late.id" });
        Add(table, "late", PrefabEditPriority.Late, _ => order.Add("late"));

        var (records, _) = Run(table, new FakePrefab());

        Assert.Equal(new[] { "late", "early" }, order);
        Assert.Equal(new[] { "late.id", "early.id" }, PluginIds(records));
    }

    [Fact]
    public void Missing_before_after_id_is_ignored()
    {
        var table = new PrefabEditTable();
        Assert.True(TryAddEditor(table, "a", PrefabEditPriority.Default, _ => { }, out var refuse, out _, before: new[] { "not.loaded" }));
        Assert.Null(refuse);
        Assert.Equal(1, table.Count);
    }

    [Fact]
    public void Before_after_cycle_is_refused_with_a_log_line()
    {
        var table = new PrefabEditTable();
        Assert.True(TryAddEditor(table, "a", PrefabEditPriority.Default, _ => { }, out _, out _, after: new[] { "b.id" }));
        Assert.False(TryAddEditor(table, "b", PrefabEditPriority.Default, _ => { }, out var refuse, out _, after: new[] { "a.id" }));
        Assert.Equal(1, table.Count);
        Assert.Equal(
            PrefabEditLog.CycleRefused("b.id", GuidHash, new[] { "a.id", "b.id", "a.id" }),
            refuse);
        Assert.Equal(
            OrderSorter.CycleDroppedLog("EventBus", "CycleEvent", "b.id", new[] { "a.id", "b.id", "a.id" }),
            EventOrder.CycleDropped("CycleEvent", "b.id", new[] { "a.id", "b.id", "a.id" }));
        Assert.Equal(
            OrderSorter.CycleDroppedLog("SystemHooks", "MoveEntitySystem Before", "b.id", new[] { "a.id", "b.id", "a.id" }),
            HookOrder.CycleDropped("MoveEntitySystem", "Before", "b.id", new[] { "a.id", "b.id", "a.id" }));
    }

    [Fact]
    public void Cycle_drops_the_candidate_while_DFS_closer_is_independent_of_subscribe_order()
    {
        var aFirst = new PrefabEditTable();
        Assert.True(TryAddEditor(aFirst, "a", PrefabEditPriority.Default, _ => { }, out _, out _, after: new[] { "b.id" }));
        Assert.False(TryAddEditor(aFirst, "b", PrefabEditPriority.Default, _ => { }, out var refuseB, out _, after: new[] { "a.id" }));
        Assert.Equal(PrefabEditLog.CycleRefused("b.id", GuidHash, new[] { "a.id", "b.id", "a.id" }), refuseB);

        var bFirst = new PrefabEditTable();
        Assert.True(TryAddEditor(bFirst, "b", PrefabEditPriority.Default, _ => { }, out _, out _, after: new[] { "a.id" }));
        Assert.False(TryAddEditor(bFirst, "a", PrefabEditPriority.Default, _ => { }, out var refuseA, out _, after: new[] { "b.id" }));
        Assert.Equal(PrefabEditLog.CycleRefused("a.id", GuidHash, new[] { "a.id", "b.id", "a.id" }), refuseA);

        var listedAb = new[]
        {
            PrefabEditOrder.ToOrderSpec(new PrefabEditSpec(0, GuidHash, "a.id", "a", 0, Array.Empty<string>(), new[] { "b.id" }, 0)),
            PrefabEditOrder.ToOrderSpec(new PrefabEditSpec(1, GuidHash, "b.id", "b", 0, Array.Empty<string>(), new[] { "a.id" }, 1)),
        };
        var listedBa = new[]
        {
            PrefabEditOrder.ToOrderSpec(new PrefabEditSpec(1, GuidHash, "b.id", "b", 0, Array.Empty<string>(), new[] { "a.id" }, 1)),
            PrefabEditOrder.ToOrderSpec(new PrefabEditSpec(0, GuidHash, "a.id", "a", 0, Array.Empty<string>(), new[] { "b.id" }, 0)),
        };
        Assert.False(OrderSorter.TryOrder(listedAb, higherPriorityFirst: false, out _, out _, out var closeAb));
        Assert.False(OrderSorter.TryOrder(listedBa, higherPriorityFirst: false, out _, out _, out var closeBa));
        Assert.Equal("b.id", closeAb);
        Assert.Equal("b.id", closeBa);
    }

    [Fact]
    public void Self_before_is_a_cycle()
    {
        var table = new PrefabEditTable();
        Assert.False(TryAddEditor(table, "a", PrefabEditPriority.Default, _ => { }, out var refuse, out _, before: new[] { "a.id" }));
        Assert.Equal(0, table.Count);
        Assert.Equal(
            PrefabEditLog.CycleRefused("a.id", GuidHash, new[] { "a.id", "a.id" }),
            refuse);
    }

    [Fact]
    public void Three_cycle_refuses_the_candidate_same_plugin_as_hooks()
    {
        var table = new PrefabEditTable();
        Assert.True(TryAddEditor(table, "a", PrefabEditPriority.Default, _ => { }, out _, out _, after: new[] { "b.id" }));
        Assert.True(TryAddEditor(table, "b", PrefabEditPriority.Default, _ => { }, out _, out _, after: new[] { "c.id" }));
        Assert.False(TryAddEditor(table, "c", PrefabEditPriority.Default, _ => { }, out var refuse, out _, after: new[] { "a.id" }));
        Assert.Equal(2, table.Count);
        Assert.Equal(
            PrefabEditLog.CycleRefused("c.id", GuidHash, new[] { "a.id", "c.id", "b.id", "a.id" }),
            refuse);
        Assert.Equal(
            OrderSorter.CycleDroppedLog("EventBus", "CycleEvent", "c.id", new[] { "a.id", "c.id", "b.id", "a.id" }),
            EventOrder.CycleDropped("CycleEvent", "c.id", new[] { "a.id", "c.id", "b.id", "a.id" }));
    }

    [Fact]
    public void Cycle_on_one_prefab_does_not_refuse_another()
    {
        var table = new PrefabEditTable();
        Assert.True(TryAddEditor(table, "a", PrefabEditPriority.Default, _ => { }, out _, out _, after: new[] { "b.id" }, guid: 1));
        Assert.True(TryAddEditor(table, "b", PrefabEditPriority.Default, _ => { }, out var refuse, out _, after: new[] { "a.id" }, guid: 2));
        Assert.Null(refuse);
        Assert.Equal(2, table.Count);
    }

    [Fact]
    public void Conflict_line_names_both_plugins_winner_and_reason()
    {
        var table = new PrefabEditTable();
        Add(table, "Alpha", PrefabEditPriority.Default, p => p.Write("ItemData", 1), pluginId: "mod.alpha");
        Add(table, "Beta", PrefabEditPriority.Late, p => p.Write("ItemData", 2), pluginId: "mod.beta");

        var (_, warns) = Run(table, new FakePrefab());

        Assert.Equal(
            new[]
            {
                PrefabEditLog.Conflict(
                    GuidHash,
                    "ItemData",
                    "Alpha",
                    "mod.alpha",
                    "Beta",
                    "mod.beta",
                    PrefabEditLog.ReasonPriority((int)PrefabEditPriority.Late, (int)PrefabEditPriority.Default)),
            },
            warns);
    }

    [Fact]
    public void Conflict_reason_is_before_after_when_priority_matches()
    {
        var a = new PrefabEditSpec(0, GuidHash, "a.id", "A", 0, Array.Empty<string>(), Array.Empty<string>(), 0);
        var b = new PrefabEditSpec(1, GuidHash, "b.id", "B", 0, Array.Empty<string>(), new[] { "a.id" }, 1);
        Assert.Equal(PrefabEditLog.ReasonBeforeAfter("b.id", "a.id"), PrefabEditOrder.ConflictReason(in a, in b));
    }

    [Fact]
    public void Apply_pass_triggered_twice_without_changes_logs_the_conflict_once()
    {
        var table = new PrefabEditTable();
        Add(table, "Alpha", PrefabEditPriority.Default, p => p.Write("ItemData", 1), pluginId: "mod.a");
        Add(table, "Beta", PrefabEditPriority.Late, p => p.Write("ItemData", 2), pluginId: "mod.b");

        var prefab = new FakePrefab();
        var (_, first) = Run(table, prefab, table.Conflicts);
        var (_, second) = Run(table, prefab, table.Conflicts);

        Assert.Single(first);
        Assert.Empty(second);
        Assert.Equal(
            PrefabEditLog.Conflict(
                GuidHash,
                "ItemData",
                "Alpha",
                "mod.a",
                "Beta",
                "mod.b",
                PrefabEditLog.ReasonPriority((int)PrefabEditPriority.Late, (int)PrefabEditPriority.Default)),
            first[0]);
        Assert.Equal(2, prefab.Values["ItemData"]);
        Assert.Equal("edits=2 ItemData", table.FormatDescribe(GuidHash));
    }

    [Fact]
    public void Conflict_relogs_when_the_editor_set_changes()
    {
        var table = new PrefabEditTable();
        Add(table, "Alpha", PrefabEditPriority.Default, p => p.Write("ItemData", 1), pluginId: "mod.a");
        Assert.True(TryAddEditor(table, "Beta", PrefabEditPriority.Late, p => p.Write("ItemData", 2), out _, out var drop, pluginId: "mod.b"));

        var prefab = new FakePrefab();
        var warns = new List<string>();
        Run(table, prefab, table.Conflicts, warns);
        Assert.Single(warns);

        drop.Dispose();
        Add(table, "Beta", PrefabEditPriority.Late, p => p.Write("ItemData", 2), pluginId: "mod.b");
        Run(table, prefab, table.Conflicts, warns);
        Assert.Equal(2, warns.Count);
        Assert.Equal(warns[0], warns[1]);
    }

    [Fact]
    public void Conflict_relogs_after_table_clear_simulating_core_reload()
    {
        var table = new PrefabEditTable();
        var live = new Dictionary<string, Action<FakePrefab>>(StringComparer.Ordinal)
        {
            ["mod.a"] = p => p.Write("ItemData", 1),
            ["mod.b"] = p => p.Write("ItemData", 2),
        };
        Add(table, "Alpha", PrefabEditPriority.Default, live["mod.a"], pluginId: "mod.a");
        Add(table, "Beta", PrefabEditPriority.Late, live["mod.b"], pluginId: "mod.b");

        var warns = new List<string>();
        Run(table, new FakePrefab(), table.Conflicts, warns);
        Assert.Single(warns);

        var intents = table.ExportIntents();
        table.Clear();
        for (int i = 0; i < intents.Length; i++)
        {
            var intent = intents[i];
            Assert.True(table.TryAdd(
                intent.GuidHash,
                intent.PluginId,
                intent.PluginName,
                intent.Priority,
                intent.Before,
                intent.After,
                owner: new object(),
                editor: live[intent.PluginId],
                loadOrder: intent.LoadOrder,
                out _,
                out var refuse,
                version: intent.Version));
            Assert.Null(refuse);
        }

        Run(table, new FakePrefab(), table.Conflicts, warns);
        Assert.Equal(2, warns.Count);
    }

    [Fact]
    public void Placeholder_send_does_not_rerun_when_fingerprint_matches()
    {
        const string fp = "mod.a\00\00";
        Assert.False(PrefabEditApply.ShouldRun(PrefabEditTrigger.PlaceholderSend, fp, fp));
        Assert.True(PrefabEditApply.ShouldRun(PrefabEditTrigger.NewClone, fp, fp));
        Assert.True(PrefabEditApply.ShouldRun(PrefabEditTrigger.EditorsChanged, fp, fp));
        Assert.True(PrefabEditApply.ShouldRun(PrefabEditTrigger.Rehydrate, fp, fp));
        Assert.False(PrefabEditApply.ShouldRun(PrefabEditTrigger.InputsChanged, fp, fp));
        Assert.True(PrefabEditApply.ShouldRun(PrefabEditTrigger.InputsChanged, "old", fp));
        Assert.False(PrefabEditApply.ShouldRun(PrefabEditTrigger.CollectionReady, fp, fp));
        Assert.True(PrefabEditApply.ShouldRun(PrefabEditTrigger.CollectionReady, "old", fp));
        Assert.True(PrefabEditApply.ShouldRun(PrefabEditTrigger.CollectionReady, null, fp));
        Assert.False(PrefabEditApply.ShouldRun(PrefabEditTrigger.CollectionReady, null, ""));
    }

    [Fact]
    public void Fingerprint_changes_on_Invalidate()
    {
        var table = new PrefabEditTable();
        Add(table, "Alpha", PrefabEditPriority.Default, p => p.Write("ItemData", 1), pluginId: "mod.a");
        Assert.True(table.TrySnapshotOrdered(GuidHash, out var ordered, out _));
        string before = table.CurrentFingerprint(GuidHash, ordered);
        table.RememberRun(GuidHash, ordered, Array.Empty<PrefabEditRecord>());
        Assert.False(table.ShouldRun(GuidHash, PrefabEditTrigger.CollectionReady, ordered));
        Assert.False(table.ShouldRun(GuidHash, PrefabEditTrigger.InputsChanged, ordered));
        Assert.Equal(0, table.Generation(GuidHash));

        int gen = table.Invalidate(GuidHash);
        string after = table.CurrentFingerprint(GuidHash, ordered);

        Assert.Equal(1, gen);
        Assert.Equal(1, table.Generation(GuidHash));
        Assert.NotEqual(before, after);
        Assert.Contains("#1", after, StringComparison.Ordinal);
        Assert.True(table.ShouldRun(GuidHash, PrefabEditTrigger.CollectionReady, ordered));
        Assert.True(table.ShouldRun(GuidHash, PrefabEditTrigger.InputsChanged, ordered));
        table.RememberRun(GuidHash, ordered, Array.Empty<PrefabEditRecord>());
        Assert.False(table.ShouldRun(GuidHash, PrefabEditTrigger.CollectionReady, ordered));
        table.Invalidate(GuidHash);
        Assert.Equal(2, table.Generation(GuidHash));
        Assert.True(table.ShouldRun(GuidHash, PrefabEditTrigger.CollectionReady, ordered));
    }

    [Fact]
    public void A_client_edit_of_a_vanilla_prefab_runs_again_in_a_new_client_world()
    {
        // FSCOPE1: the pass fingerprint describes entities of the world it ran in. Before
        // ForgetWorld existed, the next join's CollectionReady matched it and skipped the pass,
        // so this client's own edit reached the first world only.
        var table = new PrefabEditTable();
        Add(table, "Alpha", PrefabEditPriority.Default, p => p.Write("ItemData", 1), pluginId: "mod.a");
        Assert.True(table.TrySnapshotOrdered(GuidHash, out var ordered, out _));
        table.RememberRun(GuidHash, ordered, Array.Empty<PrefabEditRecord>());
        Assert.True(table.Baselines.Capture(GuidHash, "ItemData", 7, 1, _ => { }));
        Assert.False(table.ShouldRun(GuidHash, PrefabEditTrigger.CollectionReady, ordered));

        Assert.Equal(1, table.ForgetWorld());

        Assert.True(table.ShouldRun(GuidHash, PrefabEditTrigger.CollectionReady, ordered));
        Assert.Equal(0, table.Baselines.Count(GuidHash));
        Assert.True(table.TrySnapshotOrdered(GuidHash, out var still, out _)); // the editor stays registered
        Assert.Single(still);
    }

    [Fact]
    public void Fingerprint_includes_a_version_token()
    {
        var table = new PrefabEditTable();
        Assert.True(table.TryAdd(
            GuidHash, "mod.a", "Alpha", (int)PrefabEditPriority.Default,
            before: null, after: null, owner: new object(),
            editor: new Action<FakePrefab>(_ => { }),
            loadOrder: -1, out _, out var refuse, version: "99"));
        Assert.Null(refuse);
        Assert.True(table.TrySnapshotOrdered(GuidHash, out var ordered, out _));
        string withVersion = table.CurrentFingerprint(GuidHash, ordered);
        Assert.Contains("99", withVersion, StringComparison.Ordinal);

        var other = new PrefabEditTable();
        Assert.True(other.TryAdd(
            GuidHash, "mod.a", "Alpha", (int)PrefabEditPriority.Default,
            before: null, after: null, owner: new object(),
            editor: new Action<FakePrefab>(_ => { }),
            loadOrder: -1, out _, out var refuse2, version: "80"));
        Assert.Null(refuse2);
        Assert.True(other.TrySnapshotOrdered(GuidHash, out var ordered2, out _));
        Assert.NotEqual(withVersion, other.CurrentFingerprint(GuidHash, ordered2));
    }

    [Fact]
    public void InvalidateOwner_bumps_only_that_plugin_guids()
    {
        var table = new PrefabEditTable();
        var ownerA = new object();
        var ownerB = new object();
        table.TryAdd(GuidHash, "a.id", "a", 0, null, null, ownerA, new Action<FakePrefab>(_ => { }), -1, out _, out _);
        table.TryAdd(99, "b.id", "b", 0, null, null, ownerB, new Action<FakePrefab>(_ => { }), -1, out _, out _);
        Assert.True(table.TrySnapshotOrdered(GuidHash, out var orderedA, out _));
        Assert.True(table.TrySnapshotOrdered(99, out var orderedB, out _));
        table.RememberRun(GuidHash, orderedA, Array.Empty<PrefabEditRecord>());
        table.RememberRun(99, orderedB, Array.Empty<PrefabEditRecord>());

        var bumped = table.InvalidateOwner(ownerA);
        Assert.Equal(new[] { GuidHash }, bumped);
        Assert.True(table.ShouldRun(GuidHash, PrefabEditTrigger.CollectionReady, orderedA));
        Assert.False(table.ShouldRun(99, PrefabEditTrigger.CollectionReady, orderedB));
        Assert.Empty(table.InvalidateOwner(new object()));
    }

    [Fact]
    public void CollectionReady_skips_after_a_remembered_pass()
    {
        var table = new PrefabEditTable();
        Add(table, "Alpha", PrefabEditPriority.Default, p => p.Write("ItemData", 1), pluginId: "mod.a");
        Add(table, "Beta", PrefabEditPriority.Late, p => p.Write("ItemData", 2), pluginId: "mod.b");
        Assert.True(table.TrySnapshotOrdered(GuidHash, out var ordered, out _));
        Assert.True(table.ShouldRun(GuidHash, PrefabEditTrigger.CollectionReady, ordered));
        table.RememberRun(GuidHash, ordered, Array.Empty<PrefabEditRecord>());
        Assert.False(table.ShouldRun(GuidHash, PrefabEditTrigger.CollectionReady, ordered));
        Assert.True(table.ShouldRun(GuidHash, PrefabEditTrigger.NewClone, ordered));
        Assert.False(table.ShouldRun(GuidHash, PrefabEditTrigger.PlaceholderSend, ordered));
    }

    [Fact]
    public void Edit_summary_lists_count_and_unique_components()
    {
        Assert.Equal("edits=2 ItemData", PrefabEditLog.EditSummary(2, new[] { "ItemData", "ItemData" }));
        Assert.Equal("edits=2 ItemData Icon", PrefabEditLog.EditSummary(2, new[] { "ItemData", "Icon" }));
        Assert.Equal("edits=1", PrefabEditLog.EditSummary(1, Array.Empty<string>()));
        Assert.Equal("", PrefabEditLog.EditSummary(0, new[] { "ItemData" }));
        Assert.Equal("edits=1 ItemData", PrefabEditLog.EditSummary(1, new[] { "ItemData", "PrefabGUID" }));
    }

    [Fact]
    public void Same_plugin_writing_twice_is_not_a_cross_plugin_conflict()
    {
        var table = new PrefabEditTable();
        Add(table, "a", PrefabEditPriority.Early, p => p.Write("ItemData", 1));
        Add(table, "a", PrefabEditPriority.Late, p => p.Write("ItemData", 2), pluginId: "a.id");

        var (_, warns) = Run(table, new FakePrefab());

        Assert.Empty(warns);
    }

    [Fact]
    public void Identity_types_are_not_conflict_components()
    {
        var specs = new[]
        {
            new PrefabEditSpec(0, GuidHash, "a.id", "A", 0, Array.Empty<string>(), Array.Empty<string>(), 0),
            new PrefabEditSpec(1, GuidHash, "b.id", "B", 0, Array.Empty<string>(), Array.Empty<string>(), 1),
        };
        var touches = new IReadOnlyList<string>[]
        {
            new[] { PrefabEditIdentity.PrefabGuidTypeName },
            new[] { PrefabEditIdentity.PrefabGuidTypeName },
        };
        Assert.Empty(PrefabEditOrder.ConflictLines(specs, touches));
        Assert.True(PrefabEditIdentity.IsIdentityType("PrefabGUID"));
        Assert.True(PrefabEditIdentity.IsIdentityType("Prefab"));
        Assert.False(PrefabEditIdentity.IsIdentityType("ItemData"));
    }

    [Fact]
    public void Records_plugin_priority_and_components_per_prefab()
    {
        var table = new PrefabEditTable();
        Add(table, "a", PrefabEditPriority.Early, p => { p.Write("ItemData", 1); p.Write("Icon", 7); });
        Add(table, "b", PrefabEditPriority.Late, p => p.Write("ItemData", 2));

        var (records, _) = Run(table, new FakePrefab());

        Assert.Equal(2, records.Length);
        Assert.Equal("a.id", records[0].PluginId);
        Assert.Equal((int)PrefabEditPriority.Early, records[0].Priority);
        Assert.Equal(new[] { "ItemData", "Icon" }, records[0].Components);
        Assert.Equal("b.id", records[1].PluginId);
        Assert.Equal(new[] { "ItemData" }, records[1].Components);
    }

    [Fact]
    public void Edits_rerun_after_simulated_reload_from_intents()
    {
        var table = new PrefabEditTable();
        var live = new Dictionary<string, Action<FakePrefab>>(StringComparer.Ordinal);
        live["a.id"] = p => p.Write("ItemData", 1);
        live["b.id"] = p => p.Write("ItemData", 9);
        Add(table, "a", PrefabEditPriority.Early, live["a.id"]);
        Add(table, "b", PrefabEditPriority.Late, live["b.id"]);

        var first = new FakePrefab();
        var (before, _) = Run(table, first);
        Assert.Equal(9, first.Values["ItemData"]);
        Assert.Equal(new[] { "a.id", "b.id" }, PluginIds(before));

        var intents = table.ExportIntents();
        table.Clear();
        Assert.Equal(0, table.Count);

        // Re-register in reverse order; restored LoadOrder keeps the original tiebreak.
        for (int i = intents.Length - 1; i >= 0; i--)
        {
            var intent = intents[i];
            Assert.True(table.TryAdd(
                intent.GuidHash,
                intent.PluginId,
                intent.PluginName,
                intent.Priority,
                intent.Before,
                intent.After,
                owner: new object(),
                editor: live[intent.PluginId],
                loadOrder: intent.LoadOrder,
                out _,
                out var refuse,
                version: intent.Version));
            Assert.Null(refuse);
        }

        var second = new FakePrefab();
        var (after, _) = Run(table, second);
        Assert.Equal(new[] { "a.id", "b.id" }, PluginIds(after));
        Assert.Equal(9, second.Values["ItemData"]);
    }

    [Fact]
    public void Dispose_removes_only_that_editor()
    {
        var table = new PrefabEditTable();
        var order = new List<string>();
        Assert.True(TryAddEditor(table, "a", PrefabEditPriority.Default, _ => order.Add("a"), out _, out var keep));
        Assert.True(TryAddEditor(table, "b", PrefabEditPriority.Default, _ => order.Add("b"), out _, out var drop));
        drop.Dispose();
        Assert.Equal(1, table.Count);

        order.Clear();
        Run(table, new FakePrefab());
        Assert.Equal(new[] { "a" }, order);
        keep.Dispose();
        Assert.Equal(0, table.Count);
    }

    [Fact]
    public void UnregisterOwner_drops_that_plugin_only()
    {
        var table = new PrefabEditTable();
        var ownerA = new object();
        var ownerB = new object();
        table.TryAdd(GuidHash, "a.id", "a", 0, null, null, ownerA, new Action<FakePrefab>(_ => { }), -1, out _, out _);
        table.TryAdd(GuidHash, "b.id", "b", 0, null, null, ownerB, new Action<FakePrefab>(_ => { }), -1, out _, out _);
        Assert.Equal(1, table.UnregisterOwner(ownerA));
        Assert.Equal(1, table.Count);
        Assert.Equal(0, table.UnregisterOwner(ownerA));
    }

    [Fact]
    public void Thrown_editor_is_skipped_and_later_editors_still_run()
    {
        var table = new PrefabEditTable();
        Add(table, "boom", PrefabEditPriority.Early, _ => throw new InvalidOperationException("nope"));
        Add(table, "ok", PrefabEditPriority.Late, p => p.Write("ItemData", 4));
        var errors = new List<string>();
        Assert.True(table.TrySnapshotOrdered(GuidHash, out var ordered, out _));
        var prefab = new FakePrefab();
        PrefabEditApply.Run(
            ordered,
            reg =>
            {
                prefab.Written.Clear();
                ((Action<FakePrefab>)reg.Editor)(prefab);
                return prefab.Written.ToArray();
            },
            warn => { },
            (id, e) => errors.Add(id));
        Assert.Equal(new[] { "boom.id" }, errors);
        Assert.Equal(4, prefab.Values["ItemData"]);
    }

    [Fact]
    public void RememberRun_does_not_fingerprint_a_pass_whose_editors_threw()
    {
        var table = new PrefabEditTable();
        Add(table, "boom", PrefabEditPriority.Early, _ => throw new InvalidOperationException("nope"));
        Add(table, "ok", PrefabEditPriority.Late, p => p.Write("ItemData", 4));
        Assert.True(table.TrySnapshotOrdered(GuidHash, out var ordered, out _));
        Assert.True(table.ShouldRun(GuidHash, PrefabEditTrigger.CollectionReady, ordered));

        var prefab = new FakePrefab();
        var errors = new List<string>();
        var records = PrefabEditApply.Run(
            ordered,
            Invoke(prefab),
            warn => { },
            (id, e) => errors.Add(PrefabEditLog.EditorThrew(id, GuidHash, e)),
            table.Conflicts,
            table.Throws,
            out bool anyThrew);
        Assert.True(anyThrew);
        Assert.Single(errors);
        Assert.StartsWith("PrefabRegistry: edit from boom.id on guid 42 threw:", errors[0], StringComparison.Ordinal);
        Assert.Contains("boom.id", errors[0], StringComparison.Ordinal);
        Assert.Contains("nope", errors[0], StringComparison.Ordinal);
        table.RememberRun(GuidHash, ordered, records, anyThrew);

        // Next CollectionReady retries; the throwing plugin is not re-logged this generation.
        Assert.True(table.ShouldRun(GuidHash, PrefabEditTrigger.CollectionReady, ordered));
        var second = PrefabEditApply.Run(
            ordered,
            Invoke(prefab),
            warn => { },
            (id, e) => errors.Add(PrefabEditLog.EditorThrew(id, GuidHash, e)),
            table.Conflicts,
            table.Throws,
            out bool threwAgain);
        Assert.True(threwAgain);
        Assert.Single(errors);
        table.RememberRun(GuidHash, ordered, second, threwAgain);
        Assert.True(table.ShouldRun(GuidHash, PrefabEditTrigger.CollectionReady, ordered));
    }

    [Fact]
    public void RememberRun_clears_a_stale_success_fingerprint_when_a_later_pass_throws()
    {
        var table = new PrefabEditTable();
        Add(table, "ok", PrefabEditPriority.Default, p => p.Write("ItemData", 1));
        Assert.True(table.TrySnapshotOrdered(GuidHash, out var ordered, out _));
        var prefab = new FakePrefab();
        var records = PrefabEditApply.Run(ordered, Invoke(prefab), _ => { }, (_, _) => { });
        table.RememberRun(GuidHash, ordered, records);
        Assert.False(table.ShouldRun(GuidHash, PrefabEditTrigger.CollectionReady, ordered));

        table.RememberRun(GuidHash, ordered, records, anyThrew: true);
        Assert.True(table.ShouldRun(GuidHash, PrefabEditTrigger.CollectionReady, ordered));
    }

    [Fact]
    public void Edit_requires_ServerMustHave_with_Exact_or_Minor()
    {
        Assert.True(PrefabEditCompatibility.AllowsEdit(
            NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact));
        Assert.True(PrefabEditCompatibility.AllowsEdit(
            NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Minor));
        Assert.False(PrefabEditCompatibility.AllowsEdit(
            NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.None));
        Assert.False(PrefabEditCompatibility.AllowsEdit(
            NetworkCompatibilityLevel.NoNeedForSync, NetworkVersionRule.Exact));
        Assert.False(PrefabEditCompatibility.AllowsEdit(
            NetworkCompatibilityLevel.NoNeedForSync, NetworkVersionRule.Minor));
        Assert.False(PrefabEditCompatibility.AllowsEdit(
            NetworkCompatibilityLevel.ClientOnly, NetworkVersionRule.Exact));
        Assert.False(PrefabEditCompatibility.AllowsEdit(
            NetworkCompatibilityLevel.ClientOnly, NetworkVersionRule.None));
    }

    [Fact]
    public void Edit_is_refused_when_the_plugin_declared_NoNeedForSync_or_ClientOnly()
    {
        Assert.True(PrefabEditCompatibility.OwnerMayEdit(new RequiredExactPlugin(), out var exactLevel, out var exactRule));
        Assert.Equal(NetworkCompatibilityLevel.ServerMustHave, exactLevel);
        Assert.Equal(NetworkVersionRule.Exact, exactRule);

        Assert.True(PrefabEditCompatibility.OwnerMayEdit(new RequiredMinorPlugin(), out _, out var minorRule));
        Assert.Equal(NetworkVersionRule.Minor, minorRule);

        Assert.False(PrefabEditCompatibility.OwnerMayEdit(new OptionalPlugin(), out var optLevel, out var optRule));
        Assert.Equal(NetworkCompatibilityLevel.NoNeedForSync, optLevel);
        Assert.Equal(
            PrefabEditLog.CompatibilityRefused("opt.mod", GuidHash, "NoNeedForSync/Exact"),
            PrefabEditCompatibility.RefusedLine("opt.mod", GuidHash, optLevel, optRule));

        Assert.False(PrefabEditCompatibility.OwnerMayEdit(new UiPlugin(), out var uiLevel, out var uiRule));
        Assert.Equal(NetworkCompatibilityLevel.ClientOnly, uiLevel);
        Assert.Equal(
            PrefabEditLog.CompatibilityRefused("ui.mod", GuidHash, "ClientOnly/Exact"),
            PrefabEditCompatibility.RefusedLine("ui.mod", GuidHash, uiLevel, uiRule));

        Assert.False(PrefabEditCompatibility.OwnerMayEdit(new PresenceOnlyPlugin(), out var noneLevel, out var noneRule));
        Assert.Equal(NetworkCompatibilityLevel.ServerMustHave, noneLevel);
        Assert.Equal(NetworkVersionRule.None, noneRule);

        Assert.False(PrefabEditCompatibility.OwnerMayEdit(new UndeclaredPlugin(), out var missingLevel, out var missingRule));
        Assert.Null(missingLevel);
        Assert.Null(missingRule);
        Assert.Equal(
            PrefabEditLog.CompatibilityRefused("bare.mod", GuidHash, "none"),
            PrefabEditCompatibility.RefusedLine("bare.mod", GuidHash, missingLevel, missingRule));

        Assert.False(PrefabEditCompatibility.OwnerMayEdit(null, out _, out _));
        Assert.False(PrefabEditCompatibility.OwnerMayEdit(new object(), out _, out _));
    }

    [Fact]
    public void Vanilla_edit_logs_once_per_plugin_and_guid_this_generation()
    {
        var table = new PrefabEditTable();
        Assert.True(table.VanillaWarns.ShouldLog(GuidHash, "mod.a"));
        Assert.False(table.VanillaWarns.ShouldLog(GuidHash, "mod.a"));
        Assert.True(table.VanillaWarns.ShouldLog(GuidHash, "mod.b"));
        Assert.True(table.VanillaWarns.ShouldLog(99, "mod.a"));
        table.Clear();
        Assert.True(table.VanillaWarns.ShouldLog(GuidHash, "mod.a"));
        Assert.Equal(
            "PrefabRegistry: edit of vanilla prefab guid 42 by mod.a changes no definition; the edited component values reach Progenitor clients as a server layer (PrfE). A client without Progenitor keeps unedited stats.",
            PrefabEditLog.VanillaEditLocalOnly("mod.a", GuidHash));
    }

    [Fact]
    public void Invalidate_during_a_run_marks_dirty_and_reruns_once_after()
    {
        var table = new PrefabEditTable();
        int runs = 0;
        Add(table, "a", PrefabEditPriority.Default, p =>
        {
            runs++;
            p.Write("ItemData", runs);
            if (runs == 1) table.Invalidate(GuidHash);
        });
        Assert.True(table.TrySnapshotOrdered(GuidHash, out var ordered, out _));
        Assert.True(table.TryBeginRun(GuidHash));
        var prefab = new FakePrefab();
        var records = PrefabEditApply.Run(ordered, Invoke(prefab), _ => { }, (_, _) => { });
        table.RememberRun(GuidHash, ordered, records);
        Assert.True(table.EndRun(GuidHash));
        var pending = table.DrainPendingRuns();
        Assert.Equal(new[] { GuidHash }, pending);
        Assert.True(table.ShouldRun(GuidHash, PrefabEditTrigger.InputsChanged, ordered));

        Assert.True(table.TryBeginRun(GuidHash));
        PrefabEditApply.Run(ordered, Invoke(prefab), _ => { }, (_, _) => { });
        table.RememberRun(GuidHash, ordered, records);
        Assert.False(table.EndRun(GuidHash));
        Assert.Empty(table.DrainPendingRuns());
        Assert.False(table.ShouldRun(GuidHash, PrefabEditTrigger.InputsChanged, ordered));
        Assert.Equal(2, runs);
    }

    [Fact]
    public void Many_Invalidates_coalesce_to_one_pending_run_per_guid()
    {
        var table = new PrefabEditTable();
        var owner = new object();
        table.TryAdd(GuidHash, "a.id", "a", 0, null, null, owner, new Action<FakePrefab>(_ => { }), -1, out _, out _);
        table.TryAdd(99, "a.id", "a", 0, null, null, owner, new Action<FakePrefab>(_ => { }), -1, out _, out _);
        table.InvalidateOwner(owner);
        table.InvalidateOwner(owner);
        table.InvalidateOwner(owner);
        var pending = table.DrainPendingRuns();
        Array.Sort(pending);
        Assert.Equal(new[] { GuidHash, 99 }, pending);
        Assert.Empty(table.DrainPendingRuns());
    }

    [Fact]
    public void SetComponent_goes_through_RawWriteGuard_and_is_ItemData_only()
    {
        Assert.True(PrefabEditWrite.IsKnownGoodSetComponent("ItemData"));
        Assert.True(PrefabEditWrite.IsKnownGoodSetComponent("ProjectM.ItemData"));
        Assert.False(PrefabEditWrite.IsKnownGoodSetComponent("Follower"));
        Assert.False(PrefabEditWrite.IsKnownGoodSetComponent(RawWriteGuard.FollowerTypeName));
        Assert.False(PrefabEditWrite.IsKnownGoodSetComponent(null));

        PrefabEditWrite.EnsureTyped(0, 44, 0, PrefabEditWrite.ItemDataFullName);
        PrefabEditWrite.EnsureTyped(0, 32, 0, RawWriteGuard.FollowerTypeName);
        var mismatch = Assert.Throws<InvalidOperationException>(
            () => PrefabEditWrite.EnsureTyped(4, 82, 0, RawWriteGuard.FollowerTypeName));
        Assert.Contains(RawWriteGuard.FollowerTypeName, mismatch.Message, StringComparison.Ordinal);

        Assert.Equal(4, PrefabEditWrite.ManagedSizeOf(typeof(int)));
        Assert.Throws<InvalidOperationException>(() => PrefabEditWrite.ManagedSizeOf(typeof(string)));
        Assert.Equal(
            "PrefabRegistry: refused SetComponent<Follower> by mod.a on guid 42: SetComponent is for ItemData; unmanaged types use Write<T> (RawWriteGuard).",
            PrefabEditLog.SetComponentUseWrite("mod.a", GuidHash, "Follower"));
    }

    [Fact]
    public void Log_line_helpers_are_stable_for_operators()
    {
        Assert.Equal(
            "PrefabRegistry: refused edit from mod.b on guid 42: before/after cycle with mod.a -> mod.b -> mod.a",
            PrefabEditLog.CycleRefused("mod.b", 42, new[] { "mod.a", "mod.b", "mod.a" }));
        Assert.Equal(
            "PrefabRegistry: conflict on prefab guid=42 component=ItemData: Alpha (mod.a) vs Beta (mod.b); winner=Beta (mod.b) because later priority (1000 over 0)",
            PrefabEditLog.Conflict(42, "ItemData", "Alpha", "mod.a", "Beta", "mod.b", PrefabEditLog.ReasonPriority(1000, 0)));
        Assert.Equal(
            "PrefabRegistry: refused structural write of PrefabGUID by mod.a on guid 42",
            PrefabEditLog.IdentityRefused("mod.a", 42, "PrefabGUID"));
        Assert.Equal("edits=2 ItemData", PrefabEditLog.EditSummary(2, new[] { "ItemData", "ItemData" }));
        Assert.Equal(
            "PrefabRegistry: refused edit from opt.mod on guid 42: Content.Edit requires [NetworkCompatibility(ServerMustHave, Exact or Minor)] so both sides apply the same pass; this plugin declared NoNeedForSync/Exact. Set [NetworkCompatibility(NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact)] (or Minor).",
            PrefabEditLog.CompatibilityRefused("opt.mod", 42, "NoNeedForSync/Exact"));
        Assert.Equal(
            "PrefabRegistry: refused edit from ui.mod on guid 42: Content.Edit requires [NetworkCompatibility(ServerMustHave, Exact or Minor)] so both sides apply the same pass; this plugin declared ClientOnly/Exact. Set [NetworkCompatibility(NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact)] (or Minor).",
            PrefabEditLog.CompatibilityRefused("ui.mod", 42, "ClientOnly/Exact"));
        Assert.Equal(
            "PrefabRegistry: refused edit from bare.mod on guid 42: Content.Edit requires [NetworkCompatibility(ServerMustHave, Exact or Minor)] so both sides apply the same pass; this plugin declared none. Set [NetworkCompatibility(NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact)] (or Minor).",
            PrefabEditLog.CompatibilityRefused("bare.mod", 42, "none"));
        Assert.Equal(
            "PrefabRegistry: dropped edits from opt.mod: handshake is NoNeedForSync/Exact; Content.Edit requires ServerMustHave + Exact or Minor",
            PrefabEditLog.HandshakeDropped("opt.mod", "NoNeedForSync/Exact"));
        Assert.Equal(
            "PrefabRegistry: refused edit from opt.mod on guid 42: owner is not the registering plugin (pass ctx.Owner, not a decoy with a matching attribute)",
            PrefabEditLog.DecoyOwnerRefused("opt.mod", 42));
        Assert.Equal(
            "PrefabRegistry: edit of vanilla prefab guid 42 by mod.a changes no definition; the edited component values reach Progenitor clients as a server layer (PrfE). A client without Progenitor keeps unedited stats.",
            PrefabEditLog.VanillaEditLocalOnly("mod.a", 42));
        Assert.Equal(
            "PrefabRegistry: refused SetComponent<Follower> by mod.a on guid 42: SetComponent is for ItemData; unmanaged types use Write<T> (RawWriteGuard).",
            PrefabEditLog.SetComponentUseWrite("mod.a", 42, "Follower"));
        var boom = new InvalidOperationException("nope");
        Assert.Equal(
            "PrefabRegistry: edit from boom.id on guid 42 threw: " + boom,
            PrefabEditLog.EditorThrew("boom.id", 42, boom));
    }

    private static Func<PrefabEditRegistration, IReadOnlyList<string>> Invoke(FakePrefab prefab) =>
        reg =>
        {
            prefab.Written.Clear();
            ((Action<FakePrefab>)reg.Editor)(prefab);
            return prefab.Written.ToArray();
        };

    [NetworkCompatibility(NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact)]
    private sealed class RequiredExactPlugin { }

    [NetworkCompatibility(NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Minor)]
    private sealed class RequiredMinorPlugin { }

    [NetworkCompatibility(NetworkCompatibilityLevel.NoNeedForSync, NetworkVersionRule.Exact)]
    private sealed class OptionalPlugin { }

    [NetworkCompatibility(NetworkCompatibilityLevel.ClientOnly, NetworkVersionRule.Exact)]
    private sealed class UiPlugin { }

    [NetworkCompatibility(NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.None)]
    private sealed class PresenceOnlyPlugin { }

    private sealed class UndeclaredPlugin { }

    private static bool TryAddEditor(
        PrefabEditTable table,
        string name,
        PrefabEditPriority priority,
        Action<FakePrefab> editor,
        out string? refuse,
        out IDisposable handle,
        string[]? before = null,
        string[]? after = null,
        string? pluginId = null,
        int guid = GuidHash)
    {
        return table.TryAdd(
            guid,
            pluginId ?? name + ".id",
            name,
            (int)priority,
            before,
            after,
            owner: new object(),
            editor,
            loadOrder: -1,
            out handle,
            out refuse);
    }

    private static void Add(
        PrefabEditTable table,
        string name,
        PrefabEditPriority priority,
        Action<FakePrefab> editor,
        string[]? before = null,
        string[]? after = null,
        string? pluginId = null,
        int guid = GuidHash)
    {
        Assert.True(TryAddEditor(table, name, priority, editor, out var refuse, out _, before, after, pluginId, guid));
        Assert.Null(refuse);
    }

    private static (PrefabEditRecord[] records, List<string> warns) Run(
        PrefabEditTable table,
        FakePrefab prefab,
        PrefabEditConflictLog? once = null,
        List<string>? warns = null)
    {
        Assert.True(table.TrySnapshotOrdered(GuidHash, out var ordered, out var cycle));
        Assert.Null(cycle);
        warns ??= new List<string>();
        var records = PrefabEditApply.Run(
            ordered,
            reg =>
            {
                prefab.Written.Clear();
                ((Action<FakePrefab>)reg.Editor)(prefab);
                return prefab.Written.ToArray();
            },
            warns.Add,
            (_, _) => { },
            once);
        if (once != null)
            table.RememberRun(GuidHash, ordered, records);
        return (records, warns);
    }

    private static string[] PluginIds(PrefabEditRecord[] records)
    {
        var ids = new string[records.Length];
        for (int i = 0; i < records.Length; i++)
            ids[i] = records[i].PluginId;
        return ids;
    }

    private sealed class FakePrefab
    {
        public readonly Dictionary<string, int> Values = new(StringComparer.Ordinal);
        public readonly List<string> Written = new();

        public void Write(string component, int value)
        {
            Values[component] = value;
            if (!Written.Contains(component))
                Written.Add(component);
        }
    }
}
