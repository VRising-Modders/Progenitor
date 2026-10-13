using System;
using System.Collections.Generic;
using Progenitor.Content;
using Progenitor.Sample;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// M2.4 two-process edit-layer contract, game-free: competing-editor order does not depend on
/// registration order; a client-originated or off-allowlist layer is refused with a logged
/// reason and no state change; owned removal restores the captured baseline; server and client
/// derive the same effective value from the same layer set.
/// </summary>
public class PrefabEditEffectiveTests
{
    private static readonly int GuidHash = SampleEditLayers.GuidHash;

    [Fact]
    public void Competing_edits_order_by_priority_not_registration()
    {
        var lateFirst = RunBoth(lateFirst: true);
        var defaultFirst = RunBoth(lateFirst: false);

        Assert.Equal(lateFirst.Order, defaultFirst.Order);
        Assert.Equal(new[] { SampleEditLayers.SampleId, SampleEditLayers.LateId }, lateFirst.Order);
        Assert.Equal(SampleEditLayers.ExpectedEffective, lateFirst.Value);
        Assert.Equal(SampleEditLayers.ExpectedEffective, defaultFirst.Value);
        Assert.Equal(lateFirst.Conflict, defaultFirst.Conflict);
        Assert.Equal(SampleEditLayers.ExpectedConflict, lateFirst.Conflict);
    }

    [Fact]
    public void A_server_refuses_a_received_layer_with_no_state_change()
    {
        var world = new FakeWorld();
        world.Values[SampleEditLayers.Component] = 1;
        var table = new PrefabEditTable();
        table.Wire.SetReceived(GuidHash, new[]
        {
            new PrefabEditComponentBytes(
                SampleEditLayers.ComponentFullName,
                BitConverter.GetBytes(SampleEditLayers.LateAmount)),
        });

        Assert.False(PrefabEditWire.TryAdmitReceivedPacket(receiverIsServer: true, out var reason));
        Assert.Equal(PrefabEditWire.ReceivedOnServerReason, reason);
        Assert.Equal(
            "PrefabRegistry: refused PrfE: a server does not apply a received edit layer (client-originated)",
            PrefabEditLog.EditLayerRefusedOnServer());

        // The admit decision is what OnPacket runs first; a refusal must not write the entity.
        Assert.Equal(1, world.Values[SampleEditLayers.Component]);
        Assert.True(PrefabEditWire.TryAdmitReceivedPacket(receiverIsServer: false, out var clientOk));
        Assert.Equal("", clientOk);
    }

    [Fact]
    public void An_off_allowlist_record_is_refused_and_does_not_write()
    {
        const string foreign = "ProjectM.Health";
        Assert.False(PrefabEditWire.TryAdmitReceivedRecord(foreign, out var reason));
        Assert.Contains("not on Core's wire-safe list", reason, StringComparison.Ordinal);
        Assert.True(PrefabEditWire.TryAdmitReceivedRecord(SampleEditLayers.ComponentFullName, out var ok));
        Assert.Equal("", ok);
        Assert.False(PrefabEditWire.TryAdmitReceivedRecord("PrefabGUID", out var identity));
        Assert.Equal("identity component", identity);

        var world = new FakeWorld();
        world.Values[SampleEditLayers.Component] = 1;
        var onlyForeign = new RecordingSite(world);
        var appliedNone = PrefabEditApplyDecision.Apply(
            new[] { new PrefabEditComponentBytes(foreign, new byte[4]) },
            onlyForeign,
            (name, why) => onlyForeign.Refusals.Add(name + ": " + why));
        Assert.Empty(appliedNone);
        Assert.Equal(1, world.Values[SampleEditLayers.Component]);
        Assert.Contains("Health", Assert.Single(onlyForeign.Refusals), StringComparison.Ordinal);

        var site = new RecordingSite(world);
        var applied = PrefabEditApplyDecision.Apply(
            new[]
            {
                new PrefabEditComponentBytes(foreign, new byte[4]),
                new PrefabEditComponentBytes(
                    SampleEditLayers.ComponentFullName,
                    BitConverter.GetBytes(SampleEditLayers.LateAmount)),
            },
            site,
            (name, why) => site.Refusals.Add(name + ": " + why));

        Assert.Equal(new[] { SampleEditLayers.Component }, applied);
        Assert.Equal(SampleEditLayers.LateAmount, world.Values[SampleEditLayers.Component]);
        string refusal = Assert.Single(site.Refusals);
        Assert.Contains("Health", refusal, StringComparison.Ordinal);
        Assert.Contains("not on Core's wire-safe list", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void Owned_removal_and_owner_unload_restore_the_baseline()
    {
        var world = new FakeWorld();
        world.Values[SampleEditLayers.Component] = 1;
        var table = new PrefabEditTable();
        var owner = new object();
        Add(table, SampleEditLayers.SampleId, SampleEditLayers.SampleName, PrefabEditPriority.Default,
            owner, h => h.Write(SampleEditLayers.Component, SampleEditLayers.DefaultAmount), out var keep);
        Add(table, SampleEditLayers.LateId, SampleEditLayers.LateName, PrefabEditPriority.Late,
            owner, h => h.Write(SampleEditLayers.Component, SampleEditLayers.LateAmount), out _);

        Assert.Equal(PrefabEditPassOutcome.RestoredThenRan, RunPass(table, world));
        Assert.Equal(SampleEditLayers.ExpectedEffective, world.Values[SampleEditLayers.Component]);
        Assert.Equal(new[] { SampleEditLayers.Component }, table.Baselines.Components(GuidHash));

        keep.Dispose();
        Assert.Equal(new[] { GuidHash }, table.DrainPendingRemovals());
        Assert.Equal(PrefabEditPassOutcome.RestoredThenRan, RunPass(table, world));
        Assert.Equal(SampleEditLayers.ExpectedEffective, world.Values[SampleEditLayers.Component]);

        Assert.Equal(1, table.UnregisterOwner(owner));
        Assert.Equal(new[] { GuidHash }, table.DrainPendingRemovals());
        Assert.Equal(PrefabEditPassOutcome.Restored, RunPass(table, world));
        Assert.Equal(1, world.Values[SampleEditLayers.Component]);
        Assert.Equal(0, table.Baselines.Count(GuidHash));
    }

    [Fact]
    public void Server_and_client_derive_the_same_effective_bytes_from_the_same_layer_set()
    {
        byte[] baseline = BitConverter.GetBytes(1);
        byte[] loser = BitConverter.GetBytes(SampleEditLayers.DefaultAmount);
        byte[] winner = BitConverter.GetBytes(SampleEditLayers.LateAmount);
        byte[] clientOnly = BitConverter.GetBytes(99);
        var local = new[]
        {
            new PrefabEditComponentBytes(SampleEditLayers.ComponentFullName, loser),
            new PrefabEditComponentBytes(SampleEditLayers.ComponentFullName, winner),
        };
        var serverLayer = new[]
        {
            new PrefabEditComponentBytes(SampleEditLayers.ComponentFullName, winner),
        };

        byte[]? server = PrefabEditEffective.Bytes(
            baseline, local, serverLayer, SampleEditLayers.ComponentFullName);
        var clientLocal = new[]
        {
            local[0],
            local[1],
            new PrefabEditComponentBytes(SampleEditLayers.ComponentFullName, clientOnly),
        };
        byte[]? client = PrefabEditEffective.Bytes(
            baseline, clientLocal, serverLayer, SampleEditLayers.ComponentFullName);

        Assert.Equal(winner, server);
        Assert.Equal(server, client);
        Assert.Equal(
            SampleEditLayers.ExpectedEffective,
            PrefabEditEffective.Int32(
                1,
                new[] { SampleEditLayers.DefaultAmount, SampleEditLayers.LateAmount },
                SampleEditLayers.LateAmount));
        Assert.Equal(
            SampleEditLayers.ExpectedEffective,
            PrefabEditEffective.Int32(
                1,
                new[] { SampleEditLayers.DefaultAmount, SampleEditLayers.LateAmount, 99 },
                SampleEditLayers.LateAmount));
        Assert.Equal(
            SampleEditLayers.LateAmount,
            PrefabEditEffective.Int32(
                1,
                new[] { SampleEditLayers.DefaultAmount, SampleEditLayers.LateAmount },
                serverLayer: null));
    }

    [Fact]
    public void A_layer_for_a_prefab_the_client_does_not_have_does_not_write()
    {
        var world = new FakeWorld();
        world.Values[SampleEditLayers.Component] = 1;
        var site = new RecordingSite(world, hasComponent: false);
        var applied = PrefabEditApplyDecision.Apply(
            new[]
            {
                new PrefabEditComponentBytes(
                    SampleEditLayers.ComponentFullName,
                    BitConverter.GetBytes(SampleEditLayers.LateAmount)),
            },
            site,
            (name, why) => site.Refusals.Add(name + ": " + why));

        Assert.Empty(applied);
        Assert.Equal(1, world.Values[SampleEditLayers.Component]);
        string refusal = Assert.Single(site.Refusals);
        Assert.Contains("not in this world's collection", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void EntityManager_access_logs_unrestorable_structural_edit()
    {
        Assert.Contains(
            "unrestorable structural edit via EntityManager",
            PrefabEditLog.UnrestorableStructuralEdit("mod.a", SampleEditLayers.GuidHash),
            StringComparison.Ordinal);
        Assert.Contains(
            "not restored on unload",
            PrefabEditLog.UnrestorableStructuralEdit("mod.a", SampleEditLayers.GuidHash),
            StringComparison.Ordinal);
        string handle = SourcePin.ReadFile("src", "Progenitor.Core", "Content", "PrefabEditHandle.cs");
        SourcePin.AssertUncommented(handle, "WarnUnrestorableStructuralAccess()");
        SourcePin.AssertUncommented(handle, "PrefabEditLog.UnrestorableStructuralEdit(");
        int getter = handle.IndexOf("public EntityManager EntityManager", StringComparison.Ordinal);
        int warn = handle.IndexOf("WarnUnrestorableStructuralAccess()", getter, StringComparison.Ordinal);
        int em = handle.IndexOf("return Em;", warn, StringComparison.Ordinal);
        Assert.InRange(warn, getter + 1, em - 1);
    }

    [Fact]
    public void OnPacket_asks_the_origin_gate_before_any_store()
    {
        string apply = SourcePin.ReadFile("src", "Progenitor.Core", "Content", "PrefabEditWireApply.cs");
        string body = SourcePin.MethodBody(apply, "internal static unsafe void OnPacket(");
        SourcePin.AssertUncommented(body, "PrefabEditWire.TryAdmitReceivedPacket(Plugin.IsServerProcess");
        SourcePin.AssertUncommented(body, "PrefabEditLog.EditLayerRefusedOnServer()");
        int admit = body.IndexOf("TryAdmitReceivedPacket", StringComparison.Ordinal);
        int copy = body.IndexOf("new byte[length]", StringComparison.Ordinal);
        int store = body.IndexOf("SetReceived", StringComparison.Ordinal);
        Assert.InRange(admit, 0, copy - 1);
        Assert.InRange(copy, admit + 1, store - 1);
    }

    private static (string[] Order, int Value, string Conflict) RunBoth(bool lateFirst)
    {
        var table = new PrefabEditTable();
        var prefab = new FakePrefab();
        if (lateFirst)
        {
            AddEditor(table, SampleEditLayers.LateId, SampleEditLayers.LateName, PrefabEditPriority.Late,
                p => p.Write(SampleEditLayers.Component, SampleEditLayers.LateAmount));
            AddEditor(table, SampleEditLayers.SampleId, SampleEditLayers.SampleName, PrefabEditPriority.Default,
                p => p.Write(SampleEditLayers.Component, SampleEditLayers.DefaultAmount));
        }
        else
        {
            AddEditor(table, SampleEditLayers.SampleId, SampleEditLayers.SampleName, PrefabEditPriority.Default,
                p => p.Write(SampleEditLayers.Component, SampleEditLayers.DefaultAmount));
            AddEditor(table, SampleEditLayers.LateId, SampleEditLayers.LateName, PrefabEditPriority.Late,
                p => p.Write(SampleEditLayers.Component, SampleEditLayers.LateAmount));
        }

        Assert.True(table.TrySnapshotOrdered(GuidHash, out var ordered, out var cycle));
        Assert.Null(cycle);
        var warns = new List<string>();
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

        var ids = new string[ordered.Length];
        for (int i = 0; i < ordered.Length; i++) ids[i] = ordered[i].Spec.PluginId;
        return (ids, prefab.Values[SampleEditLayers.Component], Assert.Single(warns));
    }

    private static void AddEditor(
        PrefabEditTable table,
        string pluginId,
        string pluginName,
        PrefabEditPriority priority,
        Action<FakePrefab> editor)
    {
        Assert.True(table.TryAdd(
            GuidHash, pluginId, pluginName, (int)priority,
            before: null, after: null, owner: new object(), editor, loadOrder: -1,
            out _, out var refuse));
        Assert.Null(refuse);
    }

    private static void Add(
        PrefabEditTable table,
        string pluginId,
        string pluginName,
        PrefabEditPriority priority,
        object owner,
        Action<FakeHandle> editor,
        out IDisposable handle)
    {
        Assert.True(table.TryAdd(
            GuidHash, pluginId, pluginName, (int)priority,
            before: null, after: null, owner, editor, loadOrder: -1,
            out handle, out var refuse));
        Assert.Null(refuse);
    }

    private static PrefabEditPassOutcome RunPass(PrefabEditTable table, FakeWorld world)
    {
        var pass = new FakePass(table, world, PrefabEditTrigger.EditorsChanged);
        return PrefabEditPass.Run(PrefabEditTrigger.EditorsChanged, pass);
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

    private sealed class FakeWorld
    {
        public readonly Dictionary<string, int> Values = new(StringComparer.Ordinal);
        public int EntityIndex = 7;
        public int EntityVersion = 1;
    }

    private sealed class FakeHandle
    {
        private readonly FakeWorld _world;
        private readonly PrefabEditBaselines _baselines;
        private readonly int _guidHash;
        private readonly List<string> _touched = new();

        public FakeHandle(FakeWorld world, PrefabEditBaselines baselines, int guidHash)
        {
            _world = world;
            _baselines = baselines;
            _guidHash = guidHash;
        }

        public IReadOnlyList<string> Touched => _touched;

        public void Begin() => _touched.Clear();

        public void Write(string component, int value)
        {
            if (PrefabEditPass.NeedsCapture(_baselines, _guidHash, component))
            {
                bool present = _world.Values.TryGetValue(component, out int original);
                PrefabEditPass.TryCapture(
                    _baselines, _guidHash, component, present,
                    _world.EntityIndex, _world.EntityVersion,
                    ctx => ((FakeWorld)ctx).Values[component] = original);
            }
            _world.Values[component] = value;
            if (!_touched.Contains(component)) _touched.Add(component);
        }
    }

    private sealed class FakePass : IPrefabEditPass
    {
        private readonly PrefabEditTable _table;
        private readonly FakeWorld _world;
        private readonly PrefabEditTrigger _trigger;
        private PrefabEditRegistration[] _ordered = Array.Empty<PrefabEditRegistration>();
        private FakeHandle? _handle;

        public FakePass(PrefabEditTable table, FakeWorld world, PrefabEditTrigger trigger)
        {
            _table = table;
            _world = world;
            _trigger = trigger;
        }

        public bool WorldAlive => true;
        public bool HasEditors => _table.HasGuid(GuidHash);
        public int BaselineCount => _table.Baselines.Count(GuidHash);

        public bool RestoreOnly()
        {
            Restore();
            _table.Baselines.Drop(GuidHash);
            return true;
        }

        public bool TryBeginRun() => _table.TryBeginRun(GuidHash);
        public void EndRun() => _table.EndRun(GuidHash);

        public bool TrySnapshot(out int orderedCount)
        {
            if (!_table.TrySnapshotOrdered(GuidHash, out _ordered, out _))
            {
                _ordered = Array.Empty<PrefabEditRegistration>();
                orderedCount = 0;
                return false;
            }
            orderedCount = _ordered.Length;
            return true;
        }

        public bool ShouldRun() => _table.ShouldRun(GuidHash, _trigger, _ordered);

        public bool TryBind()
        {
            _handle = new FakeHandle(_world, _table.Baselines, GuidHash);
            return true;
        }

        public void DropBaselines() => _table.Baselines.Drop(GuidHash);
        public void RestoreBeforeRerun() => Restore();

        public void RunEditors()
        {
            var handle = _handle!;
            var records = PrefabEditApply.Run(
                _ordered,
                reg =>
                {
                    handle.Begin();
                    ((Action<FakeHandle>)reg.Editor)(handle);
                    return handle.Touched;
                },
                _ => { },
                (_, _) => { });
            _table.RememberRun(GuidHash, _ordered, records);
        }

        private string[] Restore() =>
            _table.Baselines.Restore(GuidHash, _world.EntityIndex, _world.EntityVersion, _world);
    }

    /// <summary>
    /// Apply-site that consults the live allowlist the way <c>LiveApplySite</c> does, and writes
    /// int payloads into the component bag.
    /// </summary>
    private sealed class RecordingSite : IPrefabEditApplySite
    {
        private readonly FakeWorld _world;
        private readonly bool _hasComponent;

        public RecordingSite(FakeWorld world, bool hasComponent = true)
        {
            _world = world;
            _hasComponent = hasComponent;
        }

        public readonly List<string> Refusals = new();

        public bool TryAcceptType(string? typeFullName, out PrefabEditTypeShape shape, out string reason)
        {
            if (!PrefabEditWire.TryAdmitReceivedRecord(typeFullName, out reason))
            {
                shape = null!;
                return false;
            }

            string shortName = PrefabEditWire.ShortTypeName(typeFullName);
            shape = new PrefabEditTypeShape(
                typeFullName ?? "", sizeof(int), Array.Empty<PrefabEditByteRange>(),
                Array.Empty<string>(), shortName);
            return true;
        }

        public bool HasComponent(PrefabEditTypeShape shape, out string reason)
        {
            if (!_hasComponent)
            {
                reason = "the prefab entity was not in this world's collection";
                return false;
            }
            if (_world.Values.ContainsKey(shape.ShortName))
            {
                reason = "";
                return true;
            }
            reason = "the prefab entity does not carry it";
            return false;
        }

        public byte[]? CurrentBytes(PrefabEditTypeShape shape) =>
            _world.Values.TryGetValue(shape.ShortName, out int v) ? BitConverter.GetBytes(v) : null;

        public void CaptureBaseline(PrefabEditTypeShape shape, string shortName) { }

        public void Write(PrefabEditTypeShape shape, byte[] bytes) =>
            _world.Values[shape.ShortName] = BitConverter.ToInt32(bytes, 0);
    }
}
