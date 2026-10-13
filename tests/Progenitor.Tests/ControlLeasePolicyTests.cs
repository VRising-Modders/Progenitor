using System;
using Progenitor.Players.Forms;
using Xunit;

namespace Progenitor.Tests;

public sealed class ControlLeasePolicyTests
{
    private static ControlLeaseIdentity Identity(ulong user = 1, ulong world = 1,
        int canonical = 10, int temporary = 20, int version = 1) =>
        new(world, user, new(canonical, version), new(temporary, version), "test.mod");

    private static ControlLease Reserve(ControlLeasePolicy policy, ControlLeaseIdentity? identity = null)
    {
        Assert.True(policy.TryReserve(identity ?? Identity(), 10, ControlSafetyCapabilities.Required,
            out var lease, out var error), error);
        return lease!;
    }

    [Fact]
    public void Partial_switch_and_failed_rollback_retain_claim_until_verified()
    {
        var policy = new ControlLeasePolicy();
        var lease = Reserve(policy);
        Assert.Equal(ControlLeaseState.Reserved, lease.Snapshot.State);
        policy.Fail(lease, "native threw after partial mutation");
        Assert.True(policy.BeginRestore(lease));
        Assert.False(policy.ObserveRestored(lease, new(10, 1), new(20, 1), false, false));
        Assert.Equal(ControlLeaseState.Faulted, lease.Snapshot.State);
        Assert.Single(policy.Snapshot());
        Assert.False(policy.TryReserve(Identity(), 10, ControlSafetyCapabilities.Required, out _, out _));
        Assert.True(policy.BeginRestore(lease));
        Assert.True(policy.ObserveRestored(lease, new(10, 1), new(10, 1), true, true));
        Assert.Equal(0, policy.Count);
        Reserve(policy);
    }

    [Fact]
    public void Restoration_is_idempotent_and_does_not_release_a_later_lease()
    {
        var policy = new ControlLeasePolicy();
        var lease = Reserve(policy);
        Assert.True(policy.ObserveActivated(lease, new(10, 1), new(20, 1)));
        policy.BeginRestore(lease);
        Assert.True(policy.ObserveRestored(lease, new(10, 1), new(10, 1), true, true));
        Reserve(policy);
        Assert.True(policy.BeginRestore(lease));
        Assert.True(policy.ObserveRestored(lease, new(10, 1), new(10, 1), true, true));
        policy.Fail(lease, "late failure");
        Assert.Equal(ControlLeaseState.Restored, lease.Snapshot.State);
        Assert.Equal(1, policy.Count);
    }

    [Theory]
    [InlineData(1, 30, 40)] // same user
    [InlineData(2, 10, 40)] // canonical shared
    [InlineData(2, 30, 20)] // temporary shared
    [InlineData(2, 20, 40)] // canonical is another temporary
    [InlineData(2, 30, 10)] // temporary is another canonical
    public void Competing_claims_are_blocked(int user, int canonical, int temporary)
    {
        var policy = new ControlLeasePolicy();
        Reserve(policy);
        Assert.False(policy.TryReserve(Identity((ulong)user, canonical: canonical, temporary: temporary),
            10, ControlSafetyCapabilities.Required, out _, out _));
    }

    [Fact]
    public void Versioned_entities_and_worlds_do_not_alias_but_user_still_exclusive()
    {
        var policy = new ControlLeasePolicy();
        Reserve(policy);
        Reserve(policy, Identity(user: 2, version: 2));
        Reserve(policy, Identity(world: 2));
        Assert.False(policy.TryReserve(Identity(version: 3), 10,
            ControlSafetyCapabilities.Required, out _, out _));
        Assert.Equal(3, policy.Count);
    }

    [Theory]
    [InlineData(0)] [InlineData(-1)] [InlineData(301)]
    [InlineData(double.NaN)] [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void Invalid_duration_never_reserves(double duration)
    {
        var policy = new ControlLeasePolicy();
        Assert.False(policy.TryReserve(Identity(), duration, ControlSafetyCapabilities.Required, out _, out _));
        Assert.Equal(0, policy.Count);
    }

    [Fact]
    public void Every_required_barrier_is_checked_before_reservation()
    {
        var policy = new ControlLeasePolicy();
        foreach (var flag in new[] { ControlSafetyCapabilities.SaveBarrier,
            ControlSafetyCapabilities.DisconnectBarrier, ControlSafetyCapabilities.UnloadRecovery,
            ControlSafetyCapabilities.QualifiedProfile })
            Assert.False(policy.TryReserve(Identity(), 10, ControlSafetyCapabilities.Required & ~flag, out _, out _));
        Assert.Equal(0, policy.Count);
    }

    [Fact]
    public void Invalid_identity_and_self_control_are_rejected()
    {
        var policy = new ControlLeasePolicy();
        foreach (var identity in new[] { Identity(user: 0), Identity(world: 0),
            Identity() with { Owner = " " }, Identity() with { Canonical = default },
            Identity() with { Temporary = default }, Identity(temporary: 10) })
            Assert.False(policy.TryReserve(identity, 10, ControlSafetyCapabilities.Required, out _, out _));
        Assert.Equal(0, policy.Count);
    }

    [Theory]
    [InlineData(false, true, true, true)]
    [InlineData(true, false, true, true)]
    [InlineData(true, true, false, true)]
    [InlineData(true, true, true, false)]
    public void Every_restoration_postcondition_is_required(bool canonical, bool controlled,
        bool cleared, bool previousCleared)
    {
        var policy = new ControlLeasePolicy();
        var lease = Reserve(policy);
        policy.BeginRestore(lease);
        Assert.False(policy.ObserveRestored(lease, canonical ? new(10, 1) : new(10, 2),
            controlled ? new(10, 1) : new(20, 1), cleared, previousCleared));
        Assert.Equal(ControlLeaseState.Faulted, lease.Snapshot.State);
        Assert.Equal(1, policy.Count);
    }

    [Theory]
    [InlineData(10, 2, 20)] [InlineData(10, 1, 30)]
    public void Activation_requires_unchanged_canonical_and_expected_control(int index, int version, int controlled)
    {
        var policy = new ControlLeasePolicy();
        var lease = Reserve(policy);
        Assert.False(policy.ObserveActivated(lease, new(index, version), new(controlled, 1)));
        Assert.Equal(ControlLeaseState.Faulted, lease.Snapshot.State);
        Assert.False(policy.ObserveActivated(lease, new(10, 1), new(20, 1)));
        Assert.Equal(1, policy.Count);
    }

    [Fact]
    public void Limit_counts_only_unrestored_leases_and_foreign_coordinator_cannot_release()
    {
        var policy = new ControlLeasePolicy();
        var first = Reserve(policy);
        for (ulong world = 2; world <= ControlLeasePolicy.MaximumLeases; world++)
            Reserve(policy, Identity(world: world));
        Assert.False(policy.TryReserve(Identity(world: 129), 10, ControlSafetyCapabilities.Required, out _, out _));
        var foreign = new ControlLeasePolicy();
        Assert.False(foreign.BeginRestore(first));
        Assert.False(foreign.ObserveRestored(first, new(10, 1), new(10, 1), true, true));
        policy.BeginRestore(first);
        Assert.True(policy.ObserveRestored(first, new(10, 1), new(10, 1), true, true));
        Reserve(policy, Identity(world: 129));
        Assert.Equal(ControlLeasePolicy.MaximumLeases, policy.Count);
    }
    [Fact]
    public void Ended_world_releases_capacity_and_claims_without_touching_another_world()
    {
        var policy = new ControlLeasePolicy();
        var ended = Reserve(policy);
        var other = Reserve(policy, Identity(world: 2));
        for (ulong world = 3; world <= ControlLeasePolicy.MaximumLeases; world++)
            Reserve(policy, Identity(world: world));
        Assert.False(policy.TryReserve(Identity(world: 129), 10,
            ControlSafetyCapabilities.Required, out _, out _));

        Assert.Equal(1, policy.ObserveWorldEnded(1));
        Assert.Equal(ControlLeaseState.WorldEnded, ended.Snapshot.State);
        Assert.Equal(ControlLeaseState.Reserved, other.Snapshot.State);
        Assert.Equal(ControlLeasePolicy.MaximumLeases - 1, policy.Count);
        Assert.DoesNotContain(policy.Snapshot(), snapshot => snapshot.Identity.WorldEpoch == 1);
        Assert.False(policy.TryReserve(Identity(world: 2), 10,
            ControlSafetyCapabilities.Required, out _, out _));
        // Pure policy releases the identity claim; the host still rejects a disposed native world.
        Reserve(policy, Identity());
        Assert.Equal(ControlLeasePolicy.MaximumLeases, policy.Count);
    }

    [Theory]
    [InlineData(0)] // Reserved
    [InlineData(1)] // Active
    [InlineData(2)] // Restoring
    [InlineData(3)] // Faulted
    public void Ended_world_retires_every_outstanding_phase_and_cannot_be_restored(int state)
    {
        var policy = new ControlLeasePolicy();
        var lease = Reserve(policy);
        if ((ControlLeaseState)state == ControlLeaseState.Active)
            Assert.True(policy.ObserveActivated(lease, new(10, 1), new(20, 1)));
        if ((ControlLeaseState)state == ControlLeaseState.Restoring) Assert.True(policy.BeginRestore(lease));
        if ((ControlLeaseState)state == ControlLeaseState.Faulted) policy.Fail(lease, "partial native mutation");
        string? priorFailure = lease.Snapshot.Failure;

        Assert.Equal(1, policy.ObserveWorldEnded(1));
        Assert.Equal(0, policy.ObserveWorldEnded(1));
        Assert.Equal(ControlLeaseState.WorldEnded, lease.Snapshot.State);
        Assert.Equal(priorFailure, lease.Snapshot.Failure);
        Assert.Equal(0, policy.Count);
        Assert.False(policy.ObserveActivated(lease, new(10, 1), new(20, 1)));
        Assert.False(policy.BeginRestore(lease));
        Assert.False(policy.ObserveRestored(lease, new(10, 1), new(10, 1), true, true));
        Assert.False(policy.Fail(lease, "late failure"));
        Assert.Equal(ControlLeaseState.WorldEnded, lease.Snapshot.State);
        Assert.Equal(priorFailure, lease.Snapshot.Failure);
        Assert.Equal(0, policy.Count);
    }

    [Fact]
    public void Ended_world_does_not_relabel_restored_leases_or_retire_unknown_epochs()
    {
        var policy = new ControlLeasePolicy();
        var restored = Reserve(policy);
        Assert.True(policy.BeginRestore(restored));
        Assert.True(policy.ObserveRestored(restored, new(10, 1), new(10, 1), true, true));
        var pending = Reserve(policy, Identity(world: 2));

        Assert.Equal(0, policy.ObserveWorldEnded(0));
        Assert.Equal(0, policy.ObserveWorldEnded(99));
        Assert.Equal(0, policy.ObserveWorldEnded(1));
        Assert.Equal(ControlLeaseState.Restored, restored.Snapshot.State);
        Assert.Equal(ControlLeaseState.Reserved, pending.Snapshot.State);
        Assert.Equal(1, policy.Count);
    }
    [Fact]
    public void Styx_zero_health_during_spawn_waits_then_becomes_ready_after_native_initialization()
    {
        // Live regression: prefab Health.Value was 0 while SpawnTag was still present.
        // This must retain the pending body, not return the lease before the spawn systems run.
        Assert.Equal(ControlSpawnReadiness.Wait, ControlLeasePolicy.DecideSpawnReadiness(
            exists: true, destroying: false, dead: false, spawnIncomplete: true,
            networkReady: false, health: 0, deadlineReached: false));
        Assert.Equal(ControlSpawnReadiness.Wait, ControlLeasePolicy.DecideSpawnReadiness(
            exists: true, destroying: false, dead: false, spawnIncomplete: true,
            networkReady: true, health: 0, deadlineReached: false));
        Assert.Equal(ControlSpawnReadiness.Ready, ControlLeasePolicy.DecideSpawnReadiness(
            exists: true, destroying: false, dead: false, spawnIncomplete: false,
            networkReady: true, health: 500, deadlineReached: false));
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.NegativeInfinity)]
    [InlineData(-1f)]
    [InlineData(0f)]
    public void Pending_spawn_does_not_interpret_uninitialized_health_as_death(float health)
    {
        Assert.Equal(ControlSpawnReadiness.Wait, ControlLeasePolicy.DecideSpawnReadiness(
            true, false, false, true, false, health, false));
    }

    [Theory]
    [InlineData(false, false, false, false)] // Missing body
    [InlineData(true, true, false, false)] // Pending destruction
    [InlineData(true, false, true, false)] // Explicit death
    [InlineData(true, false, false, true)] // Spawn or lease timeout
    public void Terminal_spawn_conditions_override_incomplete_initialization(bool exists,
        bool destroying, bool dead, bool deadlineReached)
    {
        Assert.Equal(ControlSpawnReadiness.Return, ControlLeasePolicy.DecideSpawnReadiness(
            exists, destroying, dead, true, false, 0, deadlineReached));
        Assert.Equal(ControlSpawnReadiness.Return, ControlLeasePolicy.DecideSpawnReadiness(
            exists, destroying, dead, false, true, 500, deadlineReached));
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(-1f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void Initialized_body_with_invalid_health_returns_even_before_network_ready(float health)
    {
        Assert.Equal(ControlSpawnReadiness.Return, ControlLeasePolicy.DecideSpawnReadiness(
            true, false, false, false, false, health, false));
        Assert.Equal(ControlSpawnReadiness.Return, ControlLeasePolicy.DecideSpawnReadiness(
            true, false, false, false, true, health, false));
    }

    [Fact]
    public void Healthy_initialized_body_waits_for_network_then_is_ready()
    {
        Assert.Equal(ControlSpawnReadiness.Wait, ControlLeasePolicy.DecideSpawnReadiness(
            true, false, false, false, false, 1, false));
        Assert.Equal(ControlSpawnReadiness.Ready, ControlLeasePolicy.DecideSpawnReadiness(
            true, false, false, false, true, 1, false));
    }
}
