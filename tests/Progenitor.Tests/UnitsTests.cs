using System;
using System.Collections.Generic;
using System.IO;
using Progenitor.Events;
using Progenitor.Units;
using Xunit;
using UnitsApi = Progenitor.Units.Units;

namespace Progenitor.Tests;

/// <summary>
/// Game-free coverage of the Units module: handle state machine, server/client gating,
/// RemoveAll, and Arrived event order through the real EventBus (fake runtime).
/// </summary>
[Collection("PluginLogger")]
public class UnitsTests : IDisposable
{
    private readonly TestLogSource _log = new();
    private readonly FakeUnitRuntime _server = new() { IsServer = true };
    private readonly object _owner = new Owner("example.units.test");

    public UnitsTests()
    {
        Plugin.Logger = _log;
        UnitsApi.ResetForTests();
        _server.Add("CHAR_Critter_Rat");
        _server.Add("CHAR_Farmlands_Sheep");
        UnitsApi.SetRuntimeForTests(_server);
    }

    public void Dispose()
    {
        EventBus.RemoveAll(_owner);
        UnitsApi.ResetForTests();
        Plugin.Logger = null;
    }

    [Fact]
    public void Spawn_Follow_GoTo_Despawn_state_machine_and_double_dispose_is_safe()
    {
        var handle = UnitsApi.Spawn("CHAR_Critter_Rat", new UnitVec3(1f, 2f, 3f), _owner, "example.units.test");
        Assert.True(handle.Enabled);
        Assert.Equal(UnitHandleState.Spawned, handle.State);
        Assert.Equal("CHAR_Critter_Rat", handle.Prefab);
        Assert.False(handle.Entity.IsNone);
        Assert.Equal(new UnitVec3(1f, 2f, 3f), handle.Position);
        Assert.Equal(1, _server.SpawnCalls);

        handle.Follow(new UnitId(45, 1));
        Assert.Equal(UnitHandleState.Following, handle.State);
        Assert.Equal(1, _server.FollowCalls);
        Assert.Equal(new UnitId(45, 1), _server.LastFollowTarget);
        Assert.True(_server.FollowActive);
        Assert.Equal(new UnitId(45, 1), _server.Followed);
        Assert.Equal(UnitSteering.BtFollow, _server.BtState);
        Assert.Equal(UnitSteering.TargettingEntity, _server.TargettingMode);

        handle.GoTo(new UnitVec3(7f, 2f, 3f));
        Assert.Equal(UnitHandleState.GoingTo, handle.State);
        Assert.Equal(1, _server.GoToCalls);
        Assert.Equal(new UnitVec3(7f, 2f, 3f), _server.LastGoTo);
        Assert.False(_server.FollowActive);
        Assert.True(_server.Followed.IsNone);
        Assert.Equal(UnitSteering.BtIdle, _server.BtState);
        Assert.Equal(UnitSteering.TargettingPosition, _server.TargettingMode);
        Assert.Equal(0, _server.FollowerStationary);

        handle.Despawn();
        Assert.True(handle.IsDisposed);
        Assert.Equal(UnitHandleState.Disposed, handle.State);
        Assert.Equal(1, _server.DespawnCalls);

        handle.Dispose();
        handle.Despawn();
        Assert.Equal(1, _server.DespawnCalls);
        Assert.Contains(_log.Infos, l => l.Contains("Units: spawned 'CHAR_Critter_Rat'", StringComparison.Ordinal));
        Assert.DoesNotContain(_log.Infos, l => l.Contains("Units: follow entity=", StringComparison.Ordinal));
        Assert.DoesNotContain(_log.Infos, l => l.Contains("Units: go entity=", StringComparison.Ordinal));
        Assert.Contains(_log.Infos, l => l.Contains("Units: despawn entity=", StringComparison.Ordinal));
    }

    [Fact]
    public void GoTo_after_Follow_clears_follower_overlay_and_bt_or_GoTo_returns_false()
    {
        var handle = UnitsApi.Spawn("CHAR_Critter_Rat", new UnitVec3(0f, 0f, 0f), _owner, "example.units.test");
        handle.Follow(new UnitId(45, 1));
        Assert.True(_server.FollowActive);
        handle.GoTo(new UnitVec3(6f, 0f, 0f));
        Assert.Equal(UnitHandleState.GoingTo, handle.State);
        Assert.False(_server.FollowActive);
        Assert.True(_server.Followed.IsNone);
        Assert.Equal(UnitSteering.FollowModeUnit, _server.FollowerMode);
        Assert.Equal(0, _server.FollowerStationary);
        Assert.Equal(UnitSteering.BtIdle, _server.BtState);
        Assert.Equal(UnitSteering.TargettingPosition, _server.TargettingMode);
        Assert.True(_server.TargetEntityMovement.IsNone);
        handle.Dispose();
    }

    [Fact]
    public void Follow_after_GoTo_clears_the_goal()
    {
        var handle = UnitsApi.Spawn("CHAR_Critter_Rat", new UnitVec3(0f, 0f, 0f), _owner, "example.units.test");
        handle.GoTo(new UnitVec3(6f, 0f, 0f));
        Assert.Equal(UnitHandleState.GoingTo, handle.State);
        Assert.Equal(UnitSteering.TargettingPosition, _server.TargettingMode);
        handle.Follow(new UnitId(45, 1));
        Assert.Equal(UnitHandleState.Following, handle.State);
        Assert.True(_server.FollowActive);
        Assert.Equal(UnitSteering.TargettingEntity, _server.TargettingMode);
        Assert.Equal(default(UnitVec3), _server.LastGoTo);
        Assert.Equal(UnitSteering.BtFollow, _server.BtState);
        handle.Dispose();
    }

    [Fact]
    public void Follow_same_target_does_not_repost_the_bt_request()
    {
        var handle = UnitsApi.Spawn("CHAR_Critter_Rat", new UnitVec3(0f, 0f, 0f), _owner, "example.units.test");
        var target = new UnitId(45, 1);
        handle.Follow(target);
        handle.Follow(target);
        Assert.Equal(2, _server.FollowCalls);
        Assert.Equal(1, _server.BtPosts);
        handle.Follow(new UnitId(46, 1));
        Assert.Equal(2, _server.BtPosts);
        handle.Dispose();
    }

    [Fact]
    public void Sweep_of_static_api_spawn_despawns_and_leaves_no_live_handle()
    {
        var handle = UnitsApi.Spawn("CHAR_Critter_Rat", new UnitVec3(0f, 0f, 0f), _owner, "example.units.test");
        Assert.Equal(1, UnitsApi.LiveCountForTests);
        UnitsApi.RemoveAll(_owner);
        Assert.True(handle.IsDisposed);
        Assert.Equal(1, _server.DespawnCalls);
        Assert.Equal(0, UnitsApi.LiveCountForTests);
        Assert.Contains(_log.Infos, l => l == "Units: swept 1 unit(s) of example.units.test");
    }

    [Fact]
    public void Unreachable_rearms_after_a_new_GoTo()
    {
        var reasons = new List<string>();
        using var sub = EventBus.On<UnitUnreachable>(e => reasons.Add(e.Reason), owner: _owner);

        _server.RefuseGoTo = true;
        var handle = UnitsApi.Spawn("CHAR_Critter_Rat", new UnitVec3(0f, 0f, 0f), _owner, "example.units.test");
        handle.GoTo(new UnitVec3(6f, 0f, 0f));
        Assert.Contains("goto write refused", reasons);
        reasons.Clear();
        handle.GoTo(new UnitVec3(8f, 0f, 0f));
        Assert.Contains("goto write refused", reasons);
        reasons.Clear();
        _server.RefuseGoTo = false;
        handle.GoTo(new UnitVec3(6f, 0f, 0f));
        Assert.Equal(UnitHandleState.GoingTo, handle.State);
        Assert.Empty(reasons);
        handle.Dispose();
    }

    [Fact]
    public void Client_Spawn_returns_disabled_and_does_not_info_log_per_call()
    {
        var client = new FakeUnitRuntime { IsServer = false };
        client.Add("CHAR_Critter_Rat");
        UnitsApi.SetRuntimeForTests(client);

        var handle = UnitsApi.Spawn("CHAR_Critter_Rat", new UnitVec3(0f, 0f, 0f), _owner, "example.units.test");
        Assert.False(handle.Enabled);
        Assert.Equal(UnitHandleState.Disabled, handle.State);
        Assert.True(handle.Entity.IsNone);
        Assert.Equal(0, client.SpawnCalls);
        Assert.DoesNotContain(_log.Infos, l => l.Contains("server-only", StringComparison.Ordinal));

        handle.Follow(new UnitId(1, 1));
        handle.GoTo(new UnitVec3(6f, 0f, 0f));
        handle.Follow(new UnitId(1, 1));
        Assert.Equal(0, client.FollowCalls);
        Assert.Equal(0, client.GoToCalls);
        Assert.Equal(UnitHandleState.Disabled, handle.State);
        Assert.DoesNotContain(_log.Infos, l => l.Contains("disabled handle", StringComparison.Ordinal));

        handle.Dispose();
        handle.Dispose();
        Assert.Equal(UnitHandleState.Disposed, handle.State);
        Assert.Equal(0, client.DespawnCalls);
    }

    [Fact]
    public void Missing_prefab_returns_disabled_handle()
    {
        var handle = UnitsApi.Spawn("CHAR_Missing", new UnitVec3(0f, 0f, 0f), _owner, "example.units.test");
        Assert.False(handle.Enabled);
        Assert.Equal(UnitHandleState.Disabled, handle.State);
        Assert.Contains(_log.Infos, l => l.Contains("no prefab named 'CHAR_Missing'", StringComparison.Ordinal));
        handle.Dispose();
    }

    [Fact]
    public void Spawn_by_guid_hash_string_uses_the_int_overload_path()
    {
        _server.AddGuid(1012307512);
        var handle = UnitsApi.Spawn("1012307512", new UnitVec3(2f, 0f, 0f), _owner, "example.units.test");
        Assert.True(handle.Enabled);
        Assert.Equal(1012307512, _server.LastGuid);
        handle.Dispose();
    }

    [Fact]
    public void RemoveAll_despawns_owner_handles_only()
    {
        var other = new Owner("other");
        var a = UnitsApi.Spawn("CHAR_Critter_Rat", new UnitVec3(0f, 0f, 0f), _owner, "example.units.test");
        var b = UnitsApi.Spawn("CHAR_Farmlands_Sheep", new UnitVec3(1f, 0f, 0f), _owner, "example.units.test");
        var c = UnitsApi.Spawn("CHAR_Critter_Rat", new UnitVec3(2f, 0f, 0f), other, "other");
        UnitsApi.RemoveAll(_owner);
        Assert.True(a.IsDisposed);
        Assert.True(b.IsDisposed);
        Assert.False(c.IsDisposed);
        Assert.Equal(2, _server.DespawnCalls);
        c.Dispose();
    }

    [Fact]
    public void GoTo_arrival_publishes_Arrived_in_priority_order()
    {
        var order = new List<string>();
        using var high = EventBus.On<UnitArrived>(_ => order.Add("high"), priority: 100, owner: _owner, pluginId: "example.units.high");
        using var low = EventBus.On<UnitArrived>(_ => order.Add("low"), priority: 0, owner: _owner, pluginId: "example.units.low");

        var handle = UnitsApi.Spawn("CHAR_Critter_Rat", new UnitVec3(0f, 0f, 0f), _owner, "example.units.test");
        var goal = new UnitVec3(6f, 0f, 0f);
        handle.GoTo(goal);
        Assert.Equal(UnitHandleState.GoingTo, handle.State);

        _server.SetPosition(handle.Entity, goal);
        UnitsApi.TickForTests();

        Assert.Equal(new[] { "high", "low" }, order);
        Assert.Equal(UnitHandleState.Spawned, handle.State);
        Assert.Contains(_log.Infos, l => l.Contains("Units: arrived entity=", StringComparison.Ordinal));

        order.Clear();
        UnitsApi.TickForTests();
        Assert.Empty(order);
        handle.Dispose();
    }

    [Fact]
    public void GoTo_write_refused_raises_Unreachable()
    {
        var reasons = new List<string>();
        using var sub = EventBus.On<UnitUnreachable>(e => reasons.Add(e.Reason), owner: _owner);

        _server.RefuseGoTo = true;
        var handle = UnitsApi.Spawn("CHAR_Critter_Rat", new UnitVec3(0f, 0f, 0f), _owner, "example.units.test");
        handle.GoTo(new UnitVec3(6f, 0f, 0f));
        Assert.Contains("goto write refused", reasons);
        Assert.Equal(UnitHandleState.Spawned, handle.State);
        handle.Dispose();
    }

    [Fact]
    public void Follow_target_gone_raises_Unreachable()
    {
        var reasons = new List<string>();
        using var sub = EventBus.On<UnitUnreachable>(e => reasons.Add(e.Reason), owner: _owner);

        var handle = UnitsApi.Spawn("CHAR_Critter_Rat", new UnitVec3(0f, 0f, 0f), _owner, "example.units.test");
        var target = new UnitId(99, 1);
        handle.Follow(target);
        Assert.Equal(UnitHandleState.Following, handle.State);
        UnitsApi.TickForTests();
        Assert.Contains("follow target gone", reasons);
        handle.Dispose();
    }

    [Fact]
    public void Follow_on_unit_without_Follower_adds_it_and_writes_the_target()
    {
        var handle = (UnitHandle)UnitsApi.Spawn("CHAR_Critter_Rat", new UnitVec3(0f, 0f, 0f), _owner, "example.units.test");
        Assert.False(_server.HasFollower(handle.Entity));
        Assert.False(handle.AddedFollower);
        var target = new UnitId(45, 1);
        handle.Follow(target);
        Assert.Equal(UnitHandleState.Following, handle.State);
        Assert.True(_server.HasFollower(handle.Entity));
        Assert.True(_server.AddedFollower(handle.Entity));
        Assert.True(handle.AddedFollower);
        Assert.Equal(target, _server.Followed);
        Assert.Contains(_log.Infos, l => l == "Units: added Follower to " + handle.Entity + " (prefab had none)");
        handle.Dispose();
        Assert.False(_server.HasFollower(handle.Entity));
        Assert.False(_server.AddedFollower(handle.Entity));
    }

    [Fact]
    public void GoTo_after_added_Follower_removes_the_component()
    {
        var handle = (UnitHandle)UnitsApi.Spawn("CHAR_Critter_Rat", new UnitVec3(0f, 0f, 0f), _owner, "example.units.test");
        handle.Follow(new UnitId(45, 1));
        Assert.True(_server.HasFollower(handle.Entity));
        Assert.True(handle.AddedFollower);
        handle.GoTo(new UnitVec3(6f, 0f, 0f));
        Assert.Equal(UnitHandleState.GoingTo, handle.State);
        Assert.False(_server.HasFollower(handle.Entity));
        Assert.False(_server.AddedFollower(handle.Entity));
        Assert.False(handle.AddedFollower);
        Assert.True(_server.Followed.IsNone);
        handle.Dispose();
    }

    [Fact]
    public void Follow_on_unit_that_ships_Follower_does_not_add_or_remove()
    {
        _server.ShipsFollower("CHAR_Farmlands_Sheep");
        var handle = (UnitHandle)UnitsApi.Spawn("CHAR_Farmlands_Sheep", new UnitVec3(0f, 0f, 0f), _owner, "example.units.test");
        Assert.True(_server.HasFollower(handle.Entity));
        Assert.False(_server.AddedFollower(handle.Entity));
        handle.Follow(new UnitId(45, 1));
        Assert.Equal(UnitHandleState.Following, handle.State);
        Assert.True(_server.HasFollower(handle.Entity));
        Assert.False(_server.AddedFollower(handle.Entity));
        Assert.False(handle.AddedFollower);
        Assert.DoesNotContain(_log.Infos, l => l.Contains("added Follower", StringComparison.Ordinal));
        handle.GoTo(new UnitVec3(6f, 0f, 0f));
        Assert.Equal(UnitHandleState.GoingTo, handle.State);
        Assert.True(_server.HasFollower(handle.Entity));
        Assert.False(_server.AddedFollower(handle.Entity));
        Assert.False(handle.AddedFollower);
        Assert.True(_server.Followed.IsNone);
        handle.Dispose();
        Assert.False(_server.HasFollower(handle.Entity));
    }

    [Fact]
    public void SpawnTransform_write_is_skipped_when_the_read_fails()
    {
        Assert.False(UnitSpawnWrite.CanWriteSpawnTransform(hasComponent: true, readSucceeded: false));
        Assert.False(UnitSpawnWrite.CanWriteSpawnTransform(hasComponent: false, readSucceeded: true));
        Assert.True(UnitSpawnWrite.CanWriteSpawnTransform(hasComponent: true, readSucceeded: true));
        Assert.Equal(
            "Units: SpawnTransform read failed; skipped write (no default quaternion)",
            UnitSpawnWrite.SpawnTransformReadFailed);

        var src = ReadUnitsFile("UnitSpawnPlacement.cs");
        SourcePin.AssertStatement(src, "if (!entity.TryRead(em, out SpawnTransform st))");
        SourcePin.AssertUncommented(src, "UnitSpawnWrite.SpawnTransformReadFailed");
        Assert.DoesNotContain("SpawnTransform st = default;", src, StringComparison.Ordinal);
    }

    [Fact]
    public void Spawn_requires_a_name()
    {
        Assert.Throws<ArgumentException>(() => UnitsApi.Spawn(" ", new UnitVec3(0f, 0f, 0f)));
    }

    [Fact]
    public void Skipped_module_returns_disabled_handle()
    {
        UnitsApi.SetSkippedForTests(true);
        var handle = UnitsApi.Spawn("CHAR_Critter_Rat", new UnitVec3(0f, 0f, 0f), _owner, "example.units.test");
        Assert.False(handle.Enabled);
        Assert.Contains(_log.Infos, l => l.Contains("skipped; returning a disabled handle", StringComparison.Ordinal));
        handle.Dispose();
    }

    private static string ReadUnitsFile(string fileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "Progenitor.Core", "Units", fileName);
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);
            dir = dir.Parent;
        }
        throw new FileNotFoundException("src/Progenitor.Core/Units/" + fileName);
    }

    [Fact]
    public void LiveCountFor_counts_only_this_owners_live_enabled_units()
    {
        var other = new Owner("example.units.other");
        var a = UnitsApi.Spawn("CHAR_Critter_Rat", new UnitVec3(0, 0, 0), _owner, "example.units.test");
        UnitsApi.Spawn("CHAR_Critter_Rat", new UnitVec3(1, 0, 0), _owner, "example.units.test");
        UnitsApi.Spawn("CHAR_Farmlands_Sheep", new UnitVec3(2, 0, 0), _owner, "example.units.test");
        UnitsApi.Spawn("CHAR_Critter_Rat", new UnitVec3(3, 0, 0), other, "example.units.other");
        UnitsApi.Spawn("CHAR_Missing_Prefab", new UnitVec3(4, 0, 0), _owner, "example.units.test");

        Assert.Equal(3, UnitsApi.LiveCountFor(_owner));
        Assert.Equal(1, UnitsApi.LiveCountFor(other));
        a.Dispose();
        Assert.Equal(2, UnitsApi.LiveCountFor(_owner));
        UnitsApi.RemoveAll(other);
        Assert.Equal(0, UnitsApi.LiveCountFor(other));
    }

    [Fact]
    public void The_capacity_warning_fires_once_when_the_live_count_first_exceeds_the_limit()
    {
        int limit = Progenitor.Ai.AiLimits.RecommendedActiveUnits;
        Assert.Equal(200, limit);
        Assert.False(Progenitor.Ai.AiLimits.ShouldWarn(limit, alreadyWarned: false));
        Assert.True(Progenitor.Ai.AiLimits.ShouldWarn(limit + 1, alreadyWarned: false));
        Assert.False(Progenitor.Ai.AiLimits.ShouldWarn(limit + 50, alreadyWarned: true));
        string line = Progenitor.Ai.AiLimits.FormatWarning("progenitor.bossvariants", 201);
        Assert.Contains("201 live units", line);
        Assert.Contains("not refused", line);
    }

    private sealed class Owner
    {
        public Owner(string guid) => Guid = guid;
        public string Guid { get; }
    }

    internal sealed class FakeUnitRuntime : IUnitRuntime
    {
        private readonly HashSet<string> _names = new(StringComparer.Ordinal);
        private readonly HashSet<int> _guids = new();
        private readonly HashSet<string> _shipsFollower = new(StringComparer.Ordinal);
        private readonly HashSet<UnitId> _hasFollower = new();
        private readonly HashSet<UnitId> _addedFollower = new();
        private readonly Dictionary<UnitId, UnitVec3> _pos = new();
        private readonly Dictionary<UnitId, (float Max, float Value, bool IsDead)> _health = new();
        private readonly Dictionary<UnitId, (float Physical, float Spell)> _power = new();
        private readonly Dictionary<UnitId, int> _level = new();
        private readonly Dictionary<UnitId, float> _lifetime = new();
        private readonly Dictionary<UnitId, (int Value, int Faction)> _team = new();
        private readonly HashSet<UnitId> _canBeConsumed = new();
        private readonly HashSet<UnitId> _vBloodUnit = new();
        private readonly HashSet<UnitId> _vBloodSource = new();
        private readonly HashSet<UnitId> _deadComponent = new();
        private int _next = 10;

        public bool IsServer { get; set; }

        public string? LastMissReason { get; private set; }

        public int SpawnCalls { get; private set; }
        public int FollowCalls { get; private set; }
        public int GoToCalls { get; private set; }
        public int DespawnCalls { get; private set; }
        public int BtPosts { get; private set; }
        public int LastGuid { get; private set; }
        public UnitId LastFollowTarget { get; private set; }
        public UnitVec3 LastGoTo { get; private set; }
        public bool RefuseGoTo { get; set; }
        public bool RefuseFollow { get; set; }

        public bool FollowActive { get; private set; }
        public UnitId Followed { get; private set; }
        public int FollowerMode { get; private set; }
        public int FollowerStationary { get; private set; }
        public int BtState { get; private set; } = UnitSteering.BtIdle;
        public byte TargettingMode { get; private set; }
        public UnitId TargetEntityMovement { get; private set; }

        public void Add(string name) => _names.Add(name);

        public void AddGuid(int guid) => _guids.Add(guid);

        public void ShipsFollower(string prefab) => _shipsFollower.Add(prefab);

        public bool HasFollower(UnitId unit) => _hasFollower.Contains(unit);

        public string PetTick(UnitId pet, UnitId owner) => "gone";
        public bool AddedFollower(UnitId unit) => _addedFollower.Contains(unit);

        public void SetPosition(UnitId id, UnitVec3 position) => _pos[id] = position;

        public UnitId LastSpawned { get; private set; }

        public UnitId Spawn(string prefabName, int prefabGuid, UnitVec3 position)
        {
            SpawnCalls++;
            LastGuid = prefabGuid;
            LastMissReason = null;
            bool known = _names.Contains(prefabName) || (prefabGuid != 0 && _guids.Contains(prefabGuid));
            if (!known)
            {
                LastMissReason = null;
                return UnitId.None;
            }

            var id = new UnitId(_next++, 1);
            _pos[id] = position;
            LastSpawned = id;
            if (_shipsFollower.Contains(prefabName))
                _hasFollower.Add(id);
            _health[id] = (100f, 100f, false);
            _power[id] = (10f, 10f);
            _level[id] = 1;
            _team[id] = (1, 0);
            _canBeConsumed.Add(id);
            return id;
        }

        public bool Follow(UnitId unit, UnitId target)
        {
            FollowCalls++;
            LastFollowTarget = target;
            if (RefuseFollow) return false;
            if (!_pos.ContainsKey(unit) || target.IsNone) return false;

            bool sameTarget = FollowActive && Followed == target;
            ClearGoTo();
            if (TargettingMode == UnitSteering.TargettingPosition)
                return false;

            if (!_hasFollower.Contains(unit))
            {
                _hasFollower.Add(unit);
                _addedFollower.Add(unit);
                Plugin.Logger?.LogInfo("Units: added Follower to " + unit + " (prefab had none)");
            }

            ApplyFollow(target);
            if (!sameTarget) BtPosts++;
            return true;
        }

        public UnitEngageResult Engage(UnitId unit, UnitId target) => default;

        public bool GoTo(UnitId unit, UnitVec3 position)
        {
            GoToCalls++;
            LastGoTo = position;
            if (RefuseGoTo) return false;
            if (!_pos.ContainsKey(unit)) return false;

            ClearFollow();
            if (_addedFollower.Remove(unit))
                _hasFollower.Remove(unit);
            if (FollowActive) return false;

            ApplyGoTo(position);
            return true;
        }

        public bool Despawn(UnitId unit)
        {
            DespawnCalls++;
            _addedFollower.Remove(unit);
            _hasFollower.Remove(unit);
            _health.Remove(unit);
            _power.Remove(unit);
            _level.Remove(unit);
            _lifetime.Remove(unit);
            _team.Remove(unit);
            _canBeConsumed.Remove(unit);
            _vBloodUnit.Remove(unit);
            _vBloodSource.Remove(unit);
            _deadComponent.Remove(unit);
            return _pos.Remove(unit);
        }

        public bool Exists(UnitId unit) => _pos.ContainsKey(unit);

        public bool TryGetPosition(UnitId unit, out UnitVec3 position) =>
            _pos.TryGetValue(unit, out position);

        private void ClearFollow()
        {
            FollowActive = false;
            Followed = UnitId.None;
            FollowerMode = UnitSteering.FollowModeUnit;
            FollowerStationary = 0;
            BtState = UnitSteering.BtIdle;
            TargettingMode = UnitSteering.TargettingNone;
            TargetEntityMovement = UnitId.None;
        }

        private void ClearGoTo()
        {
            LastGoTo = default;
            TargettingMode = UnitSteering.TargettingNone;
            TargetEntityMovement = UnitId.None;
        }

        private void ApplyFollow(UnitId target)
        {
            FollowActive = true;
            Followed = target;
            FollowerMode = UnitSteering.FollowModeUnit;
            FollowerStationary = 0;
            BtState = UnitSteering.BtFollow;
            TargettingMode = UnitSteering.TargettingEntity;
            TargetEntityMovement = target;
        }

        private void ApplyGoTo(UnitVec3 position)
        {
            LastGoTo = position;
            TargettingMode = UnitSteering.TargettingPosition;
            TargetEntityMovement = UnitId.None;
            BtState = UnitSteering.BtIdle;
        }

        public bool TryGetHealth(UnitId unit, out float maxHealth, out float value, out bool isDead)
        {
            if (_health.TryGetValue(unit, out var h))
            {
                maxHealth = h.Max; value = h.Value; isDead = h.IsDead; return true;
            }
            maxHealth = 0f; value = 0f; isDead = false; return false;
        }

        public bool HasDeadComponent(UnitId unit) => _deadComponent.Contains(unit);

        public bool TrySetHealthPools(UnitId unit, float maxHealth, float value, float maxRecovery)
        {
            if (!_pos.ContainsKey(unit)) return false;
            _health[unit] = (maxHealth, value, false);
            return true;
        }

        public bool TryGetUnitPower(UnitId unit, out float physicalPower, out float spellPower)
        {
            if (_power.TryGetValue(unit, out var p))
            {
                physicalPower = p.Physical; spellPower = p.Spell; return true;
            }
            physicalPower = 0f; spellPower = 0f; return false;
        }

        public bool TrySetUnitPower(UnitId unit, float? physicalPower, float? spellPower)
        {
            if (!_power.TryGetValue(unit, out var p)) return false;
            _power[unit] = (physicalPower ?? p.Physical, spellPower ?? p.Spell);
            return true;
        }

        public bool TryGetLevel(UnitId unit, out int level) => _level.TryGetValue(unit, out level);

        public bool TrySetLevel(UnitId unit, int level)
        {
            if (!_level.ContainsKey(unit)) return false;
            _level[unit] = level;
            return true;
        }

        public bool TrySetLifetime(UnitId unit, float seconds)
        {
            if (!_pos.ContainsKey(unit)) return false;
            _lifetime[unit] = seconds;
            return true;
        }

        public bool TrySetTeam(UnitId unit, int? teamValue, int? factionIndex)
        {
            if (!_team.TryGetValue(unit, out var t)) return false;
            _team[unit] = (teamValue ?? t.Value, factionIndex ?? t.Faction);
            return true;
        }

        public UnitStripVBloodResult StripVBlood(UnitId unit)
        {
            if (!_pos.ContainsKey(unit)) return default;
            if (_canBeConsumed.Contains(unit))
                _canBeConsumed.Remove(unit);
            int removed = 0;
            if (_vBloodUnit.Remove(unit)) removed++;
            if (_vBloodSource.Remove(unit)) removed++;
            return new UnitStripVBloodResult(true, removed, ok: true);
        }

        public float? LifetimeOf(UnitId unit) => _lifetime.TryGetValue(unit, out var s) ? s : null;
        public (int Value, int Faction)? TeamOf(UnitId unit) => _team.TryGetValue(unit, out var t) ? t : null;
        public bool CanBeConsumed(UnitId unit) => _canBeConsumed.Contains(unit);
        public void MarkVBlood(UnitId unit) { _vBloodUnit.Add(unit); _vBloodSource.Add(unit); }
        public void MarkDeadComponent(UnitId unit) => _deadComponent.Add(unit);
        public void SetHealthRaw(UnitId unit, float max, float value, bool isDead = false) => _health[unit] = (max, value, isDead);
    }
}
