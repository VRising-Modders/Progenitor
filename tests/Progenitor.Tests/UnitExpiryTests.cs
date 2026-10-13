using System;
using System.Collections.Generic;
using Progenitor.Units;
using Xunit;
using FakeUnitRuntime = Progenitor.Tests.UnitsTests.FakeUnitRuntime;
using UnitsApi = Progenitor.Units.Units;

namespace Progenitor.Tests;

/// <summary>
/// Unit lifetime across plugin unload, Core generation change, world load and restart
/// (docs/modules.md, "Lifetime across reload and restart"). The stamp lives on the entity
/// (<c>LifeTime</c> + <c>Age</c>, both saved with it), so each scenario rebuilds Core's in-memory
/// state from nothing and checks that the next sweep still ends the unit on time.
/// </summary>
[Collection("PluginLogger")]
public class UnitExpiryTests : IDisposable
{
    private const string Plugin1 = "example.expiry.test";
    private const float Grace = UnitExpiryPolicy.GraceSeconds;

    private readonly TestLogSource _log = new();
    private readonly FakeUnitRuntime _server = new() { IsServer = true };
    private readonly FakeExpiryWorld _world;
    private readonly object _owner = new();

    public UnitExpiryTests()
    {
        Plugin.Logger = _log;
        UnitsApi.ResetForTests();
        _server.Add("CHAR_Farmlands_Sheep");
        UnitsApi.SetRuntimeForTests(_server);
        _world = new FakeExpiryWorld(_server);
    }

    public void Dispose()
    {
        UnitsApi.ResetForTests();
        Plugin.Logger = null;
    }

    // ---- policy ---------------------------------------------------------------------------------

    [Fact]
    public void Stamp_counts_from_the_units_current_age()
    {
        Assert.Equal(90f, UnitExpiryPolicy.StampDuration(0f, 90f));
        Assert.Equal(390f, UnitExpiryPolicy.StampDuration(300f, 90f));
        Assert.Equal(90f, UnitExpiryPolicy.StampDuration(-5f, 90f));
        Assert.Equal(90f, UnitExpiryPolicy.StampDuration(float.NaN, 90f));
    }

    [Fact]
    public void Decide_covers_every_branch()
    {
        Assert.Equal(UnitExpiryDecision.Gone, UnitExpiryPolicy.Decide(Facts(exists: false)));
        Assert.Equal(UnitExpiryDecision.Gone, UnitExpiryPolicy.Decide(Facts(destroying: true)));
        Assert.Equal(UnitExpiryDecision.NotApiSpawned, UnitExpiryPolicy.Decide(Facts(api: false, age: 1000f)));
        Assert.Equal(UnitExpiryDecision.NoLimit, UnitExpiryPolicy.Decide(Facts(hasLifeTime: false)));
        // Permanent writes {-1, None}; a vanilla Kill lifetime is the game's own business.
        Assert.Equal(UnitExpiryDecision.NoLimit, UnitExpiryPolicy.Decide(Facts(duration: -1f, endAction: 0, age: 1000f)));
        Assert.Equal(UnitExpiryDecision.NoLimit, UnitExpiryPolicy.Decide(Facts(endAction: 1, age: 1000f)));
        Assert.Equal(UnitExpiryDecision.NoClock, UnitExpiryPolicy.Decide(Facts(hasAge: false)));
        Assert.Equal(UnitExpiryDecision.Running, UnitExpiryPolicy.Decide(Facts(duration: 90f, age: 90f + Grace - 0.01f)));
        Assert.Equal(UnitExpiryDecision.Expired, UnitExpiryPolicy.Decide(Facts(duration: 90f, age: 90f + Grace)));
        Assert.Equal(UnitExpiryPolicy.EndActionDestroy, Layout.LifeTimeEndActionDestroy);
    }

    [Fact]
    public void Remaining_is_duration_minus_age_and_null_without_a_clock()
    {
        Assert.Equal(60f, UnitExpiryPolicy.Remaining(Facts(duration: 90f, age: 30f)));
        Assert.Equal(0f, UnitExpiryPolicy.Remaining(Facts(duration: 90f, age: 300f)));
        Assert.Null(UnitExpiryPolicy.Remaining(Facts(hasAge: false)));
        Assert.Null(UnitExpiryPolicy.Remaining(Facts(endAction: 0)));
    }

    // ---- (a) plugin unload ----------------------------------------------------------------------

    [Fact]
    public void Plugin_unload_despawns_an_owned_lifetime_unit_at_once_rather_than_at_expiry()
    {
        var handle = SpawnWithLifetime(_owner, 90f);
        _world.Advance(10f);

        UnitsApi.SweepForUnload(_owner, Plugin1);

        Assert.True(handle.IsDisposed);
        Assert.False(_server.Exists(handle.Entity));
        Assert.Equal(1, _server.DespawnCalls);
        // Nothing left for the sweep to do.
        Assert.False(UnitExpirySweep.Run(_world).Acted);
    }

    [Fact]
    public void A_lifetime_unit_the_unloading_plugin_does_not_own_still_ends_on_time()
    {
        var stranger = new object();
        var handle = SpawnWithLifetime(stranger, 30f);

        UnitsApi.SweepForUnload(_owner, Plugin1);
        Assert.True(_server.Exists(handle.Entity));

        _world.Advance(30f + Grace);
        var result = UnitExpirySweep.Run(_world);

        Assert.Equal(1, result.Despawned);
        Assert.True(handle.IsDisposed);
        Assert.False(_server.Exists(handle.Entity));
    }

    // ---- (b) Core generation change -------------------------------------------------------------

    [Fact]
    public void Core_uninstall_despawns_live_lifetime_units()
    {
        var handle = SpawnWithLifetime(_owner, 90f);

        UnitsApi.Uninstall();

        Assert.True(handle.IsDisposed);
        Assert.False(_server.Exists(handle.Entity));
    }

    [Fact]
    public void Expiry_is_enforced_after_a_generation_change_that_lost_the_handle()
    {
        // A despawn the old generation never finished, or a handle it never knew about: the unit
        // stays in the world and every Core-side record of it is gone.
        var handle = SpawnWithLifetime(_owner, 90f);
        var unit = handle.Entity;
        _world.Advance(40f);
        NewCoreGeneration();

        var early = UnitExpirySweep.Run(_world);
        Assert.Equal(0, early.Despawned);
        Assert.Equal(1, early.Running);
        Assert.True(_server.Exists(unit));

        _world.Advance(50f + Grace);
        var due = UnitExpirySweep.Run(_world);

        Assert.Equal(1, due.Despawned);
        Assert.False(_server.Exists(unit));
    }

    [Fact]
    public void Expiring_a_unit_with_a_live_handle_disposes_the_handle()
    {
        var handle = SpawnWithLifetime(_owner, 5f);
        _world.Advance(5f + Grace);

        UnitExpirySweep.Run(_world);

        Assert.True(handle.IsDisposed);
        Assert.Equal(UnitHandleState.Disposed, handle.State);
        Assert.Equal(0, UnitsApi.LiveCountForTests);
    }

    // ---- (c) restart and (d) world load ---------------------------------------------------------

    [Fact]
    public void Expiry_is_enforced_after_a_restart_from_the_persisted_marker_and_stamp()
    {
        var handle = SpawnWithLifetime(_owner, 90f);
        var unit = handle.Entity;
        _world.Advance(30f);

        // Restart: no unload ran, nothing in memory survives; the save keeps the marker, LifeTime
        // and Age. The fake world plays the save, so it keeps its rows.
        NewCoreGeneration();
        Assert.Empty(UnitsApi.LiveUnitIds());

        var atLoad = UnitExpirySweep.Run(_world);
        Assert.Equal(1, atLoad.Running);
        Assert.True(_server.Exists(unit));

        _world.Advance(60f + Grace);
        var due = UnitExpirySweep.Run(_world);

        Assert.Equal(1, due.Despawned);
        Assert.False(_server.Exists(unit));
    }

    [Fact]
    public void A_saved_unit_already_past_its_lifetime_is_removed_by_the_first_sweep_after_load()
    {
        var handle = SpawnWithLifetime(_owner, 90f);
        _world.Advance(500f);
        NewCoreGeneration();

        var atLoad = UnitExpirySweep.Run(_world);

        Assert.Equal(1, atLoad.Despawned);
        Assert.False(_server.Exists(handle.Entity));
    }

    [Fact]
    public void A_unit_saved_without_Age_gets_a_clock_and_then_ends()
    {
        // Before this fix TrySetLifetime added LifeTime only; a prefab without Age never matched
        // LifeTimeSystem's query and the unit lived forever (the 2026-10-01 sheep).
        var unit = _server.Spawn("CHAR_Farmlands_Sheep", 0, new UnitVec3(0f, 0f, 0f));
        _world.Restore(unit, marker: true, hasLifeTime: true, duration: 90f, endAction: UnitExpiryPolicy.EndActionDestroy, hasAge: false, age: 0f);

        var first = UnitExpirySweep.Run(_world);
        Assert.Equal(1, first.ClocksStarted);
        Assert.Equal(0, first.Despawned);

        _world.Advance(90f + Grace);
        var due = UnitExpirySweep.Run(_world);

        Assert.Equal(1, due.Despawned);
        Assert.False(_server.Exists(unit));
    }

    [Fact]
    public void Units_not_spawned_through_the_API_are_never_touched()
    {
        var vanilla = _server.Spawn("CHAR_Farmlands_Sheep", 0, new UnitVec3(0f, 0f, 0f));
        _world.Restore(vanilla, marker: false, hasLifeTime: true, duration: 6f, endAction: UnitExpiryPolicy.EndActionDestroy, hasAge: false, age: 0f);
        _world.Advance(1000f);

        var result = UnitExpirySweep.Run(_world);

        Assert.False(result.Acted);
        Assert.True(_server.Exists(vanilla));
        Assert.False(_world.HasAge(vanilla));
    }

    [Fact]
    public void Permanent_units_are_left_alone()
    {
        var unit = _server.Spawn("CHAR_Farmlands_Sheep", 0, new UnitVec3(0f, 0f, 0f));
        _world.Restore(unit, marker: true, hasLifeTime: true, duration: -1f, endAction: 0, hasAge: true, age: 5000f);

        Assert.False(UnitExpirySweep.Run(_world).Acted);
        Assert.True(_server.Exists(unit));
    }

    [Fact]
    public void Format_names_every_count()
    {
        string line = UnitExpirySweep.Format(new UnitExpirySweepResult(10, 4, 1, 2, 0, 1), "world load");
        Assert.Contains("(world load)", line);
        Assert.Contains("2 expired unit(s) despawned", line);
        Assert.Contains("1 lifetime clock(s) started", line);
        Assert.Contains("1 still running", line);
    }

    // ---- helpers --------------------------------------------------------------------------------

    private IUnitHandle SpawnWithLifetime(object owner, float seconds)
    {
        var handle = UnitsApi.Spawn("CHAR_Farmlands_Sheep", new UnitVec3(0f, 0f, 0f), owner, Plugin1);
        Assert.True(handle.Enabled);
        Assert.True(handle.TrySetLifetime(seconds));
        // What the live TrySetLifetime leaves on the entity, and the marker the spawn path puts on it.
        _world.Stamp(handle.Entity, seconds);
        return handle;
    }

    /// <summary>A Core swap that lost its records, or a fresh process: Units' memory is gone, the world is not.</summary>
    private void NewCoreGeneration()
    {
        UnitsApi.ResetForTests();
        UnitsApi.SetRuntimeForTests(_server);
    }

    private static UnitExpiryFacts Facts(
        bool exists = true,
        bool destroying = false,
        bool api = true,
        bool hasLifeTime = true,
        float duration = 90f,
        int endAction = UnitExpiryPolicy.EndActionDestroy,
        bool hasAge = true,
        float age = 0f) =>
        new(exists, destroying, api, hasLifeTime, duration, endAction, hasAge, age);

    /// <summary>
    /// The entity-side state the live sweep reads: marker, <c>LifeTime</c>, <c>Age</c>. Rows survive
    /// <see cref="NewCoreGeneration"/>, as the components survive a save. The game's own
    /// <c>LifeTimeSystem</c> is deliberately absent, so only Core's sweep can end a unit here.
    /// </summary>
    private sealed class FakeExpiryWorld : IUnitExpiryWorld
    {
        private sealed class Row
        {
            public bool Marker;
            public bool HasLifeTime;
            public float Duration;
            public int EndAction;
            public bool HasAge;
            public float Age;
        }

        private readonly FakeUnitRuntime _runtime;
        private readonly Dictionary<UnitId, Row> _rows = new();

        public FakeExpiryWorld(FakeUnitRuntime runtime) => _runtime = runtime;

        public void Stamp(UnitId unit, float seconds)
        {
            if (!_rows.TryGetValue(unit, out var row)) _rows[unit] = row = new Row { Marker = true };
            if (!row.HasAge) { row.HasAge = true; row.Age = 0f; }
            row.HasLifeTime = true;
            row.Duration = UnitExpiryPolicy.StampDuration(row.Age, seconds);
            row.EndAction = UnitExpiryPolicy.EndActionDestroy;
        }

        public void Restore(UnitId unit, bool marker, bool hasLifeTime, float duration, int endAction, bool hasAge, float age) =>
            _rows[unit] = new Row
            {
                Marker = marker,
                HasLifeTime = hasLifeTime,
                Duration = duration,
                EndAction = endAction,
                HasAge = hasAge,
                Age = age,
            };

        /// <summary>The game's AgeSystem: every entity with Age gets older.</summary>
        public void Advance(float seconds)
        {
            foreach (var row in _rows.Values)
                if (row.HasAge) row.Age += seconds;
        }

        public bool HasAge(UnitId unit) => _rows.TryGetValue(unit, out var row) && row.HasAge;

        public IReadOnlyList<UnitId> Candidates()
        {
            var ids = new List<UnitId>();
            foreach (var pair in _rows)
                if (pair.Value.HasLifeTime && _runtime.Exists(pair.Key)) ids.Add(pair.Key);
            return ids;
        }

        public UnitExpiryFacts Read(UnitId unit)
        {
            if (!_runtime.Exists(unit) || !_rows.TryGetValue(unit, out var row))
                return new UnitExpiryFacts(false, false, false, false, 0f, 0, false, 0f);
            bool api = row.Marker || UnitsApi.LiveUnitIds().Contains(unit);
            return new UnitExpiryFacts(true, false, api, row.HasLifeTime, row.Duration, row.EndAction, row.HasAge, row.Age);
        }

        public bool StartClock(UnitId unit)
        {
            if (!_rows.TryGetValue(unit, out var row) || row.HasAge) return false;
            row.HasAge = true;
            row.Age = 0f;
            return true;
        }

        public bool Despawn(UnitId unit)
        {
            bool ok = UnitsApi.ExpireUnit(unit, _runtime);
            if (ok) _rows.Remove(unit);
            return ok;
        }
    }
}
