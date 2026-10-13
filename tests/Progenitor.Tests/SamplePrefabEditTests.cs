using System;
using System.Collections.Generic;
using Progenitor.Content;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Pins the Sample plugin's documented <c>sample.ember</c> conflict line so a Core log-format
/// change cannot drift from <c>samples/Progenitor.Sample/Plugin.cs</c> and the Content section
/// of <c>docs/modules.md</c>. Constants here are copies: this project must not reference Sample
/// (game assemblies).
/// </summary>
public class SamplePrefabEditTests
{
    // Keep in sync with samples/Progenitor.Sample/Plugin.cs and PrefabApplyDecisionTests.EmberHash.
    private const int EmberHash = 802060347;
    private const string SampleId = "progenitor.sample";
    private const string SampleName = "Progenitor Sample";
    private const string LateId = "progenitor.sample.late";
    private const string LateName = "Sample Late";
    private const int StackDefault = 20;
    // Default of Sample.EmberStack (synced); the Late editor reads that entry live.
    private const int StackAfter = 99;

    [Fact]
    public void Sample_ember_default_then_late_emits_the_documented_conflict_line()
    {
        var table = new PrefabEditTable();
        var prefab = new FakePrefab();
        Add(table, SampleId, SampleName, PrefabEditPriority.Default, p => p.Write("ItemData", StackDefault));
        Add(table, LateId, LateName, PrefabEditPriority.Late, p => p.Write("ItemData", StackAfter));

        var warns = new List<string>();
        Assert.True(table.TrySnapshotOrdered(EmberHash, out var ordered, out var cycle));
        Assert.Null(cycle);
        var records = PrefabEditApply.Run(
            ordered,
            reg =>
            {
                prefab.Written.Clear();
                ((Action<FakePrefab>)reg.Editor)(prefab);
                return prefab.Written.ToArray();
            },
            warns.Add,
            (_, _) => { });
        table.RememberRun(EmberHash, ordered, records);

        Assert.Equal(StackAfter, prefab.Values["ItemData"]);
        Assert.Equal(
            new[]
            {
                PrefabEditLog.Conflict(
                    EmberHash,
                    "ItemData",
                    SampleName,
                    SampleId,
                    LateName,
                    LateId,
                    PrefabEditLog.ReasonPriority((int)PrefabEditPriority.Late, (int)PrefabEditPriority.Default)),
            },
            warns);
        Assert.Equal(
            "PrefabRegistry: conflict on prefab guid=802060347 component=ItemData: Progenitor Sample (progenitor.sample) vs Sample Late (progenitor.sample.late); winner=Sample Late (progenitor.sample.late) because later priority (1000 over 0)",
            warns[0]);
        Assert.Equal("edits=2 ItemData", table.FormatDescribe(EmberHash));
    }

    [Fact]
    public void Sample_ember_apply_pass_twice_logs_the_conflict_once()
    {
        var table = new PrefabEditTable();
        var prefab = new FakePrefab();
        Add(table, SampleId, SampleName, PrefabEditPriority.Default, p => p.Write("ItemData", StackDefault));
        Add(table, LateId, LateName, PrefabEditPriority.Late, p => p.Write("ItemData", StackAfter));

        Assert.True(table.TrySnapshotOrdered(EmberHash, out var ordered, out var cycle));
        Assert.Null(cycle);
        var warns = new List<string>();
        PrefabEditApply.Run(ordered, Invoke(prefab), warns.Add, (_, _) => { }, table.Conflicts);
        PrefabEditApply.Run(ordered, Invoke(prefab), warns.Add, (_, _) => { }, table.Conflicts);

        Assert.Equal(StackAfter, prefab.Values["ItemData"]);
        Assert.Single(warns);
        Assert.Equal(
            "PrefabRegistry: conflict on prefab guid=802060347 component=ItemData: Progenitor Sample (progenitor.sample) vs Sample Late (progenitor.sample.late); winner=Sample Late (progenitor.sample.late) because later priority (1000 over 0)",
            warns[0]);
    }

    private static Func<PrefabEditRegistration, IReadOnlyList<string>> Invoke(FakePrefab prefab) =>
        reg =>
        {
            prefab.Written.Clear();
            ((Action<FakePrefab>)reg.Editor)(prefab);
            return prefab.Written.ToArray();
        };

    [Fact]
    public void Sample_ember_converges_through_the_edit_pass_when_the_synced_value_changes()
    {
        // Same two editors as Plugin.OnLoad. Late reads a mutable stack the way Sample
        // reads Sample.EmberStack: first apply writes 99, a later SettingChanged must
        // Invalidate so CollectionReady / InputsChanged re-runs and MaxAmount becomes 80.
        // Without Invalidate the fingerprint (plugin id + priority + index) matches and
        // CollectionReady would skip — that was R20 finding 4 / 7.
        int stack = StackAfter;
        var table = new PrefabEditTable();
        var prefab = new FakePrefab();
        var owner = new object();
        Assert.True(table.TryAdd(
            EmberHash, SampleId, SampleName, (int)PrefabEditPriority.Default,
            before: null, after: null, owner,
            editor: new Action<FakePrefab>(p => p.Write("ItemData", StackDefault)),
            loadOrder: -1, out _, out var refuseDefault));
        Assert.Null(refuseDefault);
        Assert.True(table.TryAdd(
            EmberHash, LateId, LateName, (int)PrefabEditPriority.Late,
            before: null, after: null, owner,
            editor: new Action<FakePrefab>(p => p.Write("ItemData", stack)),
            loadOrder: -1, out _, out var refuseLate));
        Assert.Null(refuseLate);

        Assert.True(table.TrySnapshotOrdered(EmberHash, out var ordered, out var cycle));
        Assert.Null(cycle);
        var records = PrefabEditApply.Run(ordered, Invoke(prefab), _ => { }, (_, _) => { });
        table.RememberRun(EmberHash, ordered, records);
        Assert.Equal(StackAfter, prefab.Values["ItemData"]);
        Assert.False(table.ShouldRun(EmberHash, PrefabEditTrigger.CollectionReady, ordered));

        stack = 80;
        Assert.False(table.ShouldRun(EmberHash, PrefabEditTrigger.CollectionReady, ordered));

        string before = table.CurrentFingerprint(EmberHash, ordered);
        var bumped = table.InvalidateOwner(owner);
        Assert.Equal(new[] { EmberHash }, bumped);
        string after = table.CurrentFingerprint(EmberHash, ordered);
        Assert.NotEqual(before, after);
        Assert.True(table.ShouldRun(EmberHash, PrefabEditTrigger.CollectionReady, ordered));
        Assert.True(table.ShouldRun(EmberHash, PrefabEditTrigger.InputsChanged, ordered));

        var second = PrefabEditApply.Run(ordered, Invoke(prefab), _ => { }, (_, _) => { });
        table.RememberRun(EmberHash, ordered, second);
        Assert.Equal(80, prefab.Values["ItemData"]);
        Assert.False(table.ShouldRun(EmberHash, PrefabEditTrigger.CollectionReady, ordered));
    }

    [Fact]
    public void Sample_two_editors_with_the_same_plugin_id_would_not_warn()
    {
        var table = new PrefabEditTable();
        var prefab = new FakePrefab();
        Add(table, SampleId, SampleName, PrefabEditPriority.Default, p => p.Write("ItemData", StackDefault));
        Add(table, SampleId, SampleName, PrefabEditPriority.Late, p => p.Write("ItemData", StackAfter));

        var warns = new List<string>();
        Assert.True(table.TrySnapshotOrdered(EmberHash, out var ordered, out var cycle));
        Assert.Null(cycle);
        PrefabEditApply.Run(
            ordered,
            reg =>
            {
                prefab.Written.Clear();
                ((Action<FakePrefab>)reg.Editor)(prefab);
                return prefab.Written.ToArray();
            },
            warns.Add,
            (_, _) => { });

        Assert.Equal(StackAfter, prefab.Values["ItemData"]);
        Assert.Empty(warns);
    }

    private static void Add(
        PrefabEditTable table,
        string pluginId,
        string pluginName,
        PrefabEditPriority priority,
        Action<FakePrefab> editor)
    {
        Assert.True(table.TryAdd(
            EmberHash,
            pluginId,
            pluginName,
            (int)priority,
            before: null,
            after: null,
            owner: new object(),
            editor,
            loadOrder: -1,
            out _,
            out var refuse));
        Assert.Null(refuse);
    }

    private sealed class FakePrefab
    {
        public readonly Dictionary<string, int> Values = new(StringComparer.Ordinal);
        public readonly List<string> Written = new();

        public void Write(string component, int value)
        {
            Values[component] = value;
            Written.Add(component);
        }
    }
}
