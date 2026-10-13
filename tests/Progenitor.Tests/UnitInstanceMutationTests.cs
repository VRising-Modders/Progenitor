using System;
using Progenitor.Units;
using Xunit;
using UnitsApi = Progenitor.Units.Units;

namespace Progenitor.Tests;

/// <summary>
/// Offline coverage of spawn-with-callback and the instance mutation kit
/// (<see cref="UnitSpawnOptions"/>, <see cref="UnitStatOverrides"/>, <see cref="UnitStatMath"/>,
/// handle helpers). No death/damage bus and no player directory.
/// </summary>
[Collection("PluginLogger")]
public class UnitInstanceMutationTests : IDisposable
{
    private readonly TestLogSource _log = new();
    private readonly UnitsTests.FakeUnitRuntime _server = new() { IsServer = true };
    private readonly object _owner = new object();

    public UnitInstanceMutationTests()
    {
        Plugin.Logger = _log;
        UnitsApi.ResetForTests();
        _server.Add("CHAR_Critter_Rat");
        UnitsApi.SetRuntimeForTests(_server);
    }

    public void Dispose()
    {
        UnitsApi.ResetForTests();
        Plugin.Logger = null;
    }

    [Fact]
    public void Empty_stat_overrides_are_valid()
    {
        var o = new UnitStatOverrides();
        Assert.True(o.IsEmpty);
        Assert.Null(o.Refusal());
        o.Validate();
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(120, true)]
    [InlineData(0, false)]
    [InlineData(121, false)]
    public void Level_range_is_1_to_120(int level, bool ok)
    {
        Assert.Equal(ok, new UnitStatOverrides { Level = level }.Refusal() == null);
    }

    [Fact]
    public void Spawn_options_refuse_non_positive_lifetime()
    {
        var o = new UnitSpawnOptions { LifetimeSeconds = 0f };
        Assert.Contains("LifetimeSeconds", o.Refusal());
        Assert.Throws<ArgumentOutOfRangeException>(o.Validate);
    }

    [Fact]
    public void ClampLifetime_rejects_non_positive()
    {
        Assert.Null(UnitInstanceMutation.ClampLifetime(0f));
        Assert.Null(UnitInstanceMutation.ClampLifetime(-1f));
        Assert.Equal(UnitInstanceMutation.MinLifetimeSeconds, UnitInstanceMutation.ClampLifetime(0.01f));
        Assert.Equal(30f, UnitInstanceMutation.ClampLifetime(30f));
    }

    [Fact]
    public void SpawnComputed_and_dead_refusal()
    {
        Assert.False(UnitStatMath.SpawnComputed(0f));
        Assert.True(UnitStatMath.SpawnComputed(99.37f));
        Assert.Contains("dead", UnitStatMath.DeadOrDownedRefusal("1:1", true, false, 50f, 99f));
        Assert.Null(UnitStatMath.DeadOrDownedRefusal("1:1", false, false, 0f, 0f));
    }

    [Fact]
    public void PlanWrite_first_write_is_requested()
    {
        Assert.Equal(5000f, UnitStatMath.PlanWrite(UnitStatKind.MaxHealth, 100f, null, 5000f));
        Assert.Equal(91f, UnitStatMath.PlanWrite(UnitStatKind.Level, 40f, null, 91f));
    }

    [Fact]
    public void Configure_runs_only_for_enabled_handle_after_options()
    {
        bool saw = false;
        float? life = null;
        int? level = null;
        var options = new UnitSpawnOptions
        {
            LifetimeSeconds = 45f,
            TeamValue = 11,
            FactionIndex = 21,
            Stats = new UnitStatOverrides { Level = 40, MaxHealth = 500f, PhysicalPower = 26.5f },
            Configure = h =>
            {
                saw = true;
                life = _server.LifetimeOf(h.Entity);
                level = _server.TryGetLevel(h.Entity, out int lv) ? lv : null;
                Assert.True(h.Enabled);
            },
        };

        var handle = UnitsApi.Spawn("CHAR_Critter_Rat", new UnitVec3(1f, 0f, 2f), options, _owner, "example.units.test");
        Assert.True(handle.Enabled);
        Assert.True(saw);
        Assert.Equal(45f, life);
        Assert.Equal(40, level);
        Assert.Equal((11, 21), _server.TeamOf(handle.Entity));
        Assert.True(_server.TryGetHealth(handle.Entity, out float max, out float value, out _));
        Assert.Equal(500f, max);
        Assert.Equal(500f, value);
        handle.Dispose();
    }

    [Fact]
    public void Configure_is_not_called_for_missing_prefab()
    {
        bool saw = false;
        var options = new UnitSpawnOptions { Configure = _ => saw = true };
        var handle = UnitsApi.Spawn("CHAR_Missing", default, options, _owner, "example.units.test");
        Assert.False(handle.Enabled);
        Assert.False(saw);
        handle.Dispose();
    }

    [Fact]
    public void Configure_throw_is_logged_and_handle_stays_alive()
    {
        var options = new UnitSpawnOptions
        {
            Configure = _ => throw new InvalidOperationException("boom"),
        };
        var handle = UnitsApi.Spawn("CHAR_Critter_Rat", default, options, _owner, "example.units.test");
        Assert.True(handle.Enabled);
        Assert.False(handle.IsDisposed);
        Assert.Contains(_log.Warnings, l => l.Contains("Configure callback threw", StringComparison.Ordinal));
        handle.Dispose();
    }

    [Fact]
    public void TrySetStats_pending_until_spawn_computation()
    {
        var handle = UnitsApi.Spawn("CHAR_Critter_Rat", default, _owner, "example.units.test");
        _server.SetHealthRaw(handle.Entity, 0f, 0f);
        var result = handle.TrySetStats(new UnitStatOverrides { MaxHealth = 800f });
        Assert.True(result.PendingSpawnComputation);
        Assert.False(result.Written);
        _server.SetHealthRaw(handle.Entity, 100f, 100f);
        UnitsApi.TickForTests();
        Assert.True(_server.TryGetHealth(handle.Entity, out float max, out _, out _));
        Assert.Equal(800f, max);
        handle.Dispose();
    }

    [Fact]
    public void TryStripVBlood_clears_feed_and_removes_tags()
    {
        var handle = UnitsApi.Spawn("CHAR_Critter_Rat", default, _owner, "example.units.test");
        _server.MarkVBlood(handle.Entity);
        Assert.True(_server.CanBeConsumed(handle.Entity));
        var strip = handle.TryStripVBlood();
        Assert.True(strip.Ok);
        Assert.True(strip.CanBeConsumedCleared);
        Assert.Equal(2, strip.RemovedComponents);
        Assert.False(_server.CanBeConsumed(handle.Entity));
        handle.Dispose();
    }

    [Fact]
    public void RemoveAll_despawns_owned_unit_even_with_lifetime()
    {
        var options = new UnitSpawnOptions { LifetimeSeconds = 600f };
        var handle = UnitsApi.Spawn("CHAR_Critter_Rat", default, options, _owner, "example.units.test");
        Assert.Equal(600f, _server.LifetimeOf(handle.Entity));
        UnitsApi.RemoveAll(_owner);
        Assert.True(handle.IsDisposed);
        Assert.Equal(1, _server.DespawnCalls);
        Assert.Equal(0, UnitsApi.LiveCountForTests);
    }

    [Fact]
    public void Instance_vs_content_edit_contract_string_is_stable()
    {
        Assert.Contains("Content.Edit", UnitInstanceMutation.InstanceVsContentEdit, StringComparison.Ordinal);
        Assert.Contains("spawned entity only", UnitInstanceMutation.InstanceVsContentEdit, StringComparison.Ordinal);
    }
}
