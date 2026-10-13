using System;
using System.Collections.Generic;
using Progenitor.Combat;
using Progenitor.Events;
using Progenitor.Players;
using Progenitor.Units;
using Xunit;
using UnitsApi = Progenitor.Units.Units;

namespace Progenitor.Tests;

/// <summary>
/// Edge / bound probes for the three MVP authoring surfaces (combat bus, spawn+instance kit,
/// player directory). Prefer extending the focused suites
/// (<see cref="CombatEventPolicyTests"/>, <see cref="UnitInstanceMutationTests"/>,
/// <see cref="PlayerDirectoryTests"/>); this file covers cross-cutting edges those suites
/// do not duplicate. Live-server bounds that offline cannot reach are named in the
/// Skip reasons below.
/// </summary>
[Collection("PluginLogger")]
public sealed class MvpBoundsTests : IDisposable
{
    private readonly TestLogSource _log = new();
    private readonly UnitsTests.FakeUnitRuntime _server = new() { IsServer = true };
    private readonly object _owner = new();

    public MvpBoundsTests()
    {
        Plugin.Logger = _log;
        UnitsApi.ResetForTests();
        _server.Add("CHAR_Critter_Rat");
        UnitsApi.SetRuntimeForTests(_server);
    }

    public void Dispose()
    {
        EventBus.RemoveAll(_owner);
        UnitsApi.ResetForTests();
        Plugin.Logger = null;
    }

    // ----- 1. Combat bus -------------------------------------------------------

    [Fact]
    public void Combat_double_subscribe_delivers_twice_and_double_dispose_is_safe()
    {
        int hits = 0;
        using var a = EventBus.On<UnitDied>(_ => hits++, owner: _owner);
        using var b = EventBus.On<UnitDied>(_ => hits++, owner: _owner);
        Assert.Equal(2, EventBus.SubscriberCount<UnitDied>());

        EventBus.Publish(new UnitDied(new UnitId(1, 1), UnitId.None, UnitId.None, "Any", epoch: 1));
        Assert.Equal(2, hits);

        a.Dispose();
        a.Dispose(); // second dispose is a no-op
        Assert.Equal(1, EventBus.SubscriberCount<UnitDied>());

        EventBus.Publish(new UnitDied(new UnitId(2, 1), UnitId.None, UnitId.None, "Any", epoch: 1));
        Assert.Equal(3, hits);

        Assert.Equal(1, EventBus.RemoveAll(_owner));
        Assert.Equal(0, EventBus.SubscriberCount<UnitDied>());
        EventBus.Publish(new UnitDied(new UnitId(3, 1), UnitId.None, UnitId.None, "Any", epoch: 1));
        Assert.Equal(3, hits); // no further delivery after RemoveAll
    }


    [Fact]
    public void Combat_None_entity_ids_are_valid_payload_fields_not_refused()
    {
        // Bound: bus accepts UnitId.None for killer/source/user; live Kill postfix stamps None
        // when the game left those args empty. Offline cannot assert real Kill after dead.
        UnitDied? died = null;
        PlayerDied? player = null;
        using var a = EventBus.On<UnitDied>(e => died = e, owner: _owner);
        using var b = EventBus.On<PlayerDied>(e => player = e, owner: _owner);

        EventBus.Publish(new UnitDied(UnitId.None, UnitId.None, UnitId.None, null, epoch: 0));
        EventBus.Publish(new PlayerDied(UnitId.None, UnitId.None, UnitId.None, UnitId.None, null, 0));

        Assert.NotNull(died);
        Assert.True(died!.Died.IsNone);
        Assert.Equal("", died.Reason);
        Assert.NotNull(player);
        Assert.True(player!.User.IsNone);
        Assert.Equal("", player.Reason);
    }

    [Fact]
    public void Combat_damage_publish_without_kill_is_independent_of_death_bus()
    {
        // Bound: DamageDealt is StatChangeSystem health rows; UnitDied is Kill path.
        // Publishing damage alone must not imply a death event (and vice versa).
        int deaths = 0;
        int damages = 0;
        using var d = EventBus.On<UnitDied>(_ => deaths++, owner: _owner);
        using var g = EventBus.On<DamageDealt>(_ => damages++, owner: _owner);

        Assert.True(CombatEventPolicy.ShouldPublishDamage(0, -12f, -12f));
        EventBus.Publish(new DamageDealt(new UnitId(9, 1), new UnitId(1, 1), -12f, -12f, 0, "Default", 3));
        Assert.Equal(0, deaths);
        Assert.Equal(1, damages);

        EventBus.Publish(new UnitDied(new UnitId(9, 1), UnitId.None, UnitId.None, "Any", 3));
        Assert.Equal(1, deaths);
        Assert.Equal(1, damages);
    }

    [Fact(Skip =
        "LIVE_ONLY+DESTRUCTIVE: PlayerDied needs a real PlayerCharacter on the Kill path. " +
        "Dev kill op refuses players (LifeOps.Kill throws). No safe disposable player-kill path; " +
        "MVP demo subscribes PlayerDied but never kills a player.")]
    public void Combat_PlayerDied_live_player_kill_skipped_unless_safe_disposable_path()
    {
        Assert.Fail("unreachable: Skip keeps this from running");
    }

    // ----- 2. Spawn + instance kit --------------------------------------------

    [Theory]
    [InlineData(0f)]
    [InlineData(-1f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void Spawn_options_refuse_non_positive_or_non_finite_lifetime(float life)
    {
        var o = new UnitSpawnOptions { LifetimeSeconds = life };
        Assert.NotNull(o.Refusal());
        Assert.Contains("LifetimeSeconds", o.Refusal(), StringComparison.Ordinal);
        Assert.Throws<ArgumentOutOfRangeException>(o.Validate);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(-5f)]
    [InlineData(10_000_001f)]
    [InlineData(float.NaN)]
    public void Spawn_stats_refuse_invalid_max_health(float maxHealth)
    {
        var o = new UnitStatOverrides { MaxHealth = maxHealth };
        Assert.NotNull(o.Refusal());
        Assert.Throws<ArgumentOutOfRangeException>(o.Validate);
    }

    [Theory]
    [InlineData(-0.1f)]
    [InlineData(10_001f)]
    [InlineData(float.NegativeInfinity)]
    public void Spawn_stats_refuse_out_of_band_power(float power)
    {
        Assert.NotNull(new UnitStatOverrides { PhysicalPower = power }.Refusal());
        Assert.NotNull(new UnitStatOverrides { SpellPower = power }.Refusal());
    }

    [Fact]
    public void Spawn_extreme_but_valid_lifetime_and_stats_apply()
    {
        float? life = null;
        var options = new UnitSpawnOptions
        {
            LifetimeSeconds = UnitInstanceMutation.MinLifetimeSeconds,
            Stats = new UnitStatOverrides
            {
                Level = UnitStatOverrides.MaxLevel,
                MaxHealth = UnitStatOverrides.MaxMaxHealth,
                PhysicalPower = UnitStatOverrides.MaxPower,
                SpellPower = 0f,
            },
            Configure = h => life = _server.LifetimeOf(h.Entity),
        };

        var handle = UnitsApi.Spawn("CHAR_Critter_Rat", default, options, _owner, "example.units.test");
        Assert.True(handle.Enabled);
        Assert.Equal(UnitInstanceMutation.MinLifetimeSeconds, life);
        Assert.True(_server.TryGetLevel(handle.Entity, out int lv));
        Assert.Equal(120, lv);
        Assert.True(_server.TryGetHealth(handle.Entity, out float max, out _, out _));
        Assert.Equal(UnitStatOverrides.MaxMaxHealth, max);
        handle.Dispose();
    }

    [Fact]
    public void StripVBlood_on_non_VBlood_clears_feed_flag_and_removes_zero_tags()
    {
        // Live (2026-09-30 ET): CHAR_Farmlands_Sheep StripVBlood ok=true cleared=true removed=0.
        var handle = UnitsApi.Spawn("CHAR_Critter_Rat", default, _owner, "example.units.test");
        Assert.True(_server.CanBeConsumed(handle.Entity));
        var strip = handle.TryStripVBlood();
        Assert.True(strip.Ok);
        Assert.True(strip.CanBeConsumedCleared);
        Assert.Equal(0, strip.RemovedComponents);
        Assert.False(_server.CanBeConsumed(handle.Entity));
        // Second strip: still ok, still cleared, still zero removals (idempotent no-op on tags).
        var again = handle.TryStripVBlood();
        Assert.True(again.Ok);
        Assert.Equal(0, again.RemovedComponents);
        handle.Dispose();
    }

    [Fact]
    public void TrySetStats_on_dead_unit_refuses_instead_of_reviving()
    {
        var handle = UnitsApi.Spawn("CHAR_Critter_Rat", default, _owner, "example.units.test");
        _server.SetHealthRaw(handle.Entity, 100f, 0f, isDead: true);
        var result = handle.TrySetStats(new UnitStatOverrides { MaxHealth = 500f });
        Assert.False(result.Ok);
        Assert.False(result.Written);
        Assert.Contains("dead", result.Refusal!, StringComparison.OrdinalIgnoreCase);
        handle.Dispose();
    }

    [Fact]
    public void TrySetStats_on_Dead_component_refuses()
    {
        var handle = UnitsApi.Spawn("CHAR_Critter_Rat", default, _owner, "example.units.test");
        _server.MarkDeadComponent(handle.Entity);
        var result = handle.TrySetStats(new UnitStatOverrides { MaxHealth = 500f });
        Assert.False(result.Ok);
        Assert.Contains("dead", result.Refusal!, StringComparison.OrdinalIgnoreCase);
        handle.Dispose();
    }

    [Fact]
    public void Disposed_or_despawned_handle_mutation_silently_refuses()
    {
        var handle = UnitsApi.Spawn("CHAR_Critter_Rat", default, _owner, "example.units.test");
        handle.Dispose();
        Assert.True(handle.IsDisposed);

        var stats = handle.TrySetStats(new UnitStatOverrides { Level = 10 });
        Assert.False(stats.Ok);
        Assert.Equal("handle disabled or disposed", stats.Refusal);
        Assert.False(handle.TrySetLifetime(30f));
        Assert.False(handle.TrySetTeam(2, 3));
        Assert.False(handle.TryStripVBlood().Ok);

        handle.Dispose(); // double-dispose no-op
        Assert.Equal(1, _server.DespawnCalls);
    }

    [Fact]
    public void Concurrent_spawn_then_RemoveAll_despawns_every_owned_handle()
    {
        var handles = new List<IUnitHandle>();
        for (int i = 0; i < 8; i++)
        {
            handles.Add(UnitsApi.Spawn(
                "CHAR_Critter_Rat",
                new UnitVec3(i, 0f, 0f),
                new UnitSpawnOptions { LifetimeSeconds = 600f },
                _owner,
                "example.units.test"));
        }

        Assert.All(handles, h => Assert.True(h.Enabled));
        Assert.Equal(8, UnitsApi.LiveCountForTests);

        UnitsApi.RemoveAll(_owner);
        Assert.Equal(0, UnitsApi.LiveCountForTests);
        Assert.All(handles, h => Assert.True(h.IsDisposed));
        Assert.Equal(8, _server.DespawnCalls);
    }

    [Fact]
    public void TrySetLifetime_zero_or_negative_returns_false_without_throw()
    {
        var handle = UnitsApi.Spawn("CHAR_Critter_Rat", default, _owner, "example.units.test");
        Assert.False(handle.TrySetLifetime(0f));
        Assert.False(handle.TrySetLifetime(-10f));
        Assert.False(handle.TrySetLifetime(float.NaN));
        Assert.Null(_server.LifetimeOf(handle.Entity));
        Assert.True(handle.TrySetLifetime(UnitInstanceMutation.MaxLifetimeSeconds));
        Assert.Equal(UnitInstanceMutation.MaxLifetimeSeconds, _server.LifetimeOf(handle.Entity));
        handle.Dispose();
    }

    // ----- 3. Player directory -------------------------------------------------

    private static PlayerDirectoryRow Row(
        ulong account,
        string name,
        bool online = true,
        int userIndex = 1,
        int userVersion = 1) =>
        new(account, name, online, online ? 0 : -1, userIndex, userVersion, userIndex + 100, userVersion);

    [Fact]
    public void Players_partial_name_is_NotFound_exact_is_whole_token_only()
    {
        // Bound: NamesMatch is whole-name OrdinalIgnoreCase — "Claud" does not hit "TestPlayer".
        var rows = new[] { Row(1, "TestPlayer"), Row(2, "Bob") };
        var hits = new List<int>();
        Assert.Equal(PlayerLookupStatus.NotFound, PlayerDirectoryLogic.MatchByName(rows, "Claud", onlineOnly: true, hits));
        Assert.Empty(hits);
        Assert.Equal(PlayerLookupStatus.NotFound, PlayerDirectoryLogic.MatchByName(rows, "lia", onlineOnly: true, hits));
        Assert.Equal(PlayerLookupStatus.Exact, PlayerDirectoryLogic.MatchByName(rows, "TestPlayer", onlineOnly: true, hits));
        Assert.Equal(PlayerLookupStatus.Exact, PlayerDirectoryLogic.MatchByName(rows, "  testplayer  ", onlineOnly: true, hits));
    }

    [Fact]
    public void Players_missing_name_and_missing_account_are_NotFound()
    {
        var rows = new[] { Row(10, "Alice") };
        var hits = new List<int>();
        Assert.Equal(PlayerLookupStatus.NotFound, PlayerDirectoryLogic.MatchByName(rows, "Nobody", onlineOnly: true, hits));
        Assert.Equal(-1, PlayerDirectoryLogic.FindByAccount(rows, 999UL, onlineOnly: false));
        Assert.Equal(-1, PlayerDirectoryLogic.FindByAccount(rows, 0UL, onlineOnly: false));

        var lookup = PlayerDirectoryLogic.ToNameLookup(
            PlayerLookupStatus.NotFound, rows, hits, worldEpoch: 1, directoryRevision: 1, isFresh: true);
        Assert.Equal(PlayerLookupStatus.NotFound, lookup.Status);
        Assert.Null(lookup.Player);
    }

    [Fact]
    public void Players_null_destination_or_null_hit_list_throws()
    {
        var rows = new[] { Row(1, "A") };
        Assert.Throws<ArgumentNullException>(() => PlayerDirectoryLogic.CollectOnline(rows, null!));
        Assert.Throws<ArgumentNullException>(() =>
            PlayerDirectoryLogic.MatchByName(rows, "A", onlineOnly: true, hitIndexes: null!));
    }

    [Fact]
    public void Players_IsFresh_false_when_either_stamp_or_live_side_is_zero_or_mismatched()
    {
        Assert.False(PlayerDirectoryLogic.IsFresh(0, 1, 0, 1));
        Assert.False(PlayerDirectoryLogic.IsFresh(1, 0, 1, 0));
        Assert.False(PlayerDirectoryLogic.IsFresh(5, 5, 5, 6));
        Assert.True(PlayerDirectoryLogic.IsFresh(5, 5, 5, 5));
    }
}