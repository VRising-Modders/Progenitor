using System;
using Progenitor.Sample;
using Progenitor.Units;
using Xunit;
using UnitsApi = Progenitor.Units.Units;

namespace Progenitor.Tests;

/// <summary>
/// Game-free coverage of Sample <c>.rat</c>: token parse, spawn in front of a pose,
/// follow / go / off against the Units fake runtime.
/// </summary>
[Collection("PluginLogger")]
public class SampleUnitsTests : IDisposable
{
    private readonly TestLogSource _log = new();
    private readonly UnitsTests.FakeUnitRuntime _server = new() { IsServer = true };
    private readonly object _owner = new();

    public SampleUnitsTests()
    {
        Plugin.Logger = _log;
        UnitsApi.ResetForTests();
        _server.Add(SampleUnits.PrefabName);
        UnitsApi.SetRuntimeForTests(_server);
        SampleUnits.AttachForTests(_owner, "progenitor.sample");
    }

    public void Dispose()
    {
        SampleUnits.Detach();
        UnitsApi.ResetForTests();
        Plugin.Logger = null;
    }

    [Theory]
    [InlineData(null, 1, 3f)]
    [InlineData("", 1, 3f)]
    [InlineData("   ", 1, 3f)]
    [InlineData("5", 1, 5f)]
    [InlineData("3.5", 1, 3.5f)]
    [InlineData("follow", 2, 3f)]
    [InlineData("FOLLOW", 2, 3f)]
    [InlineData("go", 3, 3f)]
    [InlineData("off", 4, 3f)]
    [InlineData("patrol", 5, 3f)]
    [InlineData("PATROL", 5, 3f)]
    [InlineData("stop", 6, 3f)]
    public void TryParse_accepts_distance_and_subcommands(string? token, int expectedKind, float expectedDistance)
    {
        Assert.True(SampleUnits.TryParse(token, out var kind, out var distance));
        Assert.Equal(expectedKind, (int)kind);
        Assert.Equal(expectedDistance, distance);
    }

    [Theory]
    [InlineData("ember")]
    [InlineData("-1")]
    [InlineData("0")]
    [InlineData("follow extra")]
    [InlineData("beam")]
    public void TryParse_rejects_unknown(string token)
    {
        Assert.False(SampleUnits.TryParse(token, out _, out _));
    }

    [Fact]
    public void Spawn_places_the_rat_in_front_of_the_pose_and_replies_entity_and_position()
    {
        var from = new UnitVec3(10f, 5f, 20f);
        var forward = new UnitVec3(0f, 0f, 1f);
        string reply = SampleUnits.Run(SampleUnits.Kind.Spawn, SampleUnits.DefaultDistance, from, forward);
        Assert.Equal(1, SampleUnits.LiveCount);
        Assert.Equal(1, _server.SpawnCalls);
        Assert.StartsWith("Spawned rat ", reply, StringComparison.Ordinal);
        Assert.Contains("at (10, 5, 23)", reply, StringComparison.Ordinal);
        Assert.Contains(_log.Infos, l => l.StartsWith("Sample: rat spawned entity=", StringComparison.Ordinal));
        Assert.Contains(_log.Infos, l => l.Contains("Units: spawned '" + SampleUnits.PrefabName + "'", StringComparison.Ordinal));
    }

    [Fact]
    public void Follow_go_off_steer_the_last_rat()
    {
        var from = new UnitVec3(0f, 0f, 0f);
        var forward = new UnitVec3(1f, 0f, 0f);
        SampleUnits.Run(SampleUnits.Kind.Spawn, 3f, from, forward);
        Assert.Equal(1, SampleUnits.LiveCount);

        string follow = SampleUnits.Run(SampleUnits.Kind.Follow);
        Assert.Contains("following you", follow, StringComparison.Ordinal);
        Assert.Equal(1, _server.FollowCalls);
        Assert.True(_server.HasFollower(_server.LastSpawned));
        Assert.True(_server.AddedFollower(_server.LastSpawned));
        Assert.Contains(_log.Infos, l => l == "Units: added Follower to " + _server.LastSpawned + " (prefab had none)");

        string go = SampleUnits.Run(SampleUnits.Kind.Go, SampleUnits.GoDistance, from, forward);
        Assert.Contains("walking to (6, 0, 0)", go, StringComparison.Ordinal);
        Assert.Equal(1, _server.GoToCalls);
        Assert.Equal(new UnitVec3(6f, 0f, 0f), _server.LastGoTo);
        Assert.False(_server.FollowActive);
        Assert.True(_server.Followed.IsNone);
        Assert.False(_server.HasFollower(_server.LastSpawned));
        Assert.False(_server.AddedFollower(_server.LastSpawned));
        Assert.Equal(UnitSteering.BtIdle, _server.BtState);
        Assert.Equal(UnitSteering.TargettingPosition, _server.TargettingMode);

        string off = SampleUnits.Run(SampleUnits.Kind.Off);
        Assert.Equal("Despawned 1 sample rat(s).", off);
        Assert.Equal(0, SampleUnits.LiveCount);
        Assert.Equal(1, _server.DespawnCalls);
        Assert.Contains(_log.Infos, l => l == "Sample: rat off despawned 1");
    }

    [Fact]
    public void Follow_without_a_rat_tells_the_caller_to_spawn_first()
    {
        string reply = SampleUnits.Run(SampleUnits.Kind.Follow);
        Assert.Equal("No live rat. Spawn one with .rat first.", reply);
        Assert.Equal(0, _server.FollowCalls);
    }

    [Fact]
    public void Prefab_is_the_verified_critter_rat_spawnable()
    {
        Assert.Equal("CHAR_Critter_Rat", SampleUnits.PrefabName);
        Assert.Equal(3f, SampleUnits.DefaultDistance);
        Assert.Equal(6f, SampleUnits.GoDistance);
    }
}
