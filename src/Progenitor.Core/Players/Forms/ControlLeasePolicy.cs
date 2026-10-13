using System;
using System.Collections.Generic;

namespace Progenitor.Players.Forms;

// Pure bookkeeping only: callers must serialize on the game thread and supply verified
// observations. This does not install barriers or qualify any native control implementation.
[Flags]
internal enum ControlSafetyCapabilities
{
    None = 0, SaveBarrier = 1, DisconnectBarrier = 2, UnloadRecovery = 4, QualifiedProfile = 8,
    Required = SaveBarrier | DisconnectBarrier | UnloadRecovery | QualifiedProfile
}

internal readonly record struct ControlBody(int Index, int Version)
{
    public bool IsValid => Index > 0 && Version > 0;
}

internal readonly record struct ControlLeaseIdentity(
    ulong WorldEpoch, ulong User, ControlBody Canonical, ControlBody Temporary, string Owner);

internal enum ControlLeaseState { Reserved, Active, Restoring, Faulted, Restored, WorldEnded }

internal enum ControlSpawnReadiness { Wait, Ready, Return }

internal readonly record struct ControlLeaseSnapshot(
    ControlLeaseIdentity Identity, ControlLeaseState State, double DurationSeconds, string? Failure);

internal sealed class ControlLease
{
    internal ControlLease(ControlLeasePolicy policy, ControlLeaseIdentity identity, double duration)
    { Policy = policy; Identity = identity; DurationSeconds = duration; }

    internal ControlLeasePolicy Policy { get; }
    internal ControlLeaseIdentity Identity { get; }
    internal double DurationSeconds { get; }
    internal ControlLeaseState State { get; set; } = ControlLeaseState.Reserved;
    internal string? Failure { get; set; }
    internal ControlLeaseSnapshot Snapshot => new(Identity, State, DurationSeconds, Failure);
}

internal sealed class ControlLeasePolicy
{
    internal const int MaximumLeases = 128;
    internal const double MaximumDurationSeconds = 300;
    private readonly HashSet<ControlLease> _outstanding = new();
    internal int Count => _outstanding.Count;

    /// <summary>
    /// Decide whether a reserved body has finished native spawn initialization. The host supplies
    /// spawnIncomplete for SpawnTag or Disabled, and deadlineReached for either lease or spawn
    /// timeout. Uninitialized health is not evidence of death; explicit death/destruction is.
    /// This decision alone does not validate user identity, ownership or the control graph.
    /// </summary>
    internal static ControlSpawnReadiness DecideSpawnReadiness(bool exists, bool destroying,
        bool dead, bool spawnIncomplete, bool networkReady, float health, bool deadlineReached)
    {
        if (deadlineReached || !exists || destroying || dead) return ControlSpawnReadiness.Return;
        if (spawnIncomplete) return ControlSpawnReadiness.Wait;
        if (!float.IsFinite(health) || health <= 0) return ControlSpawnReadiness.Return;
        return networkReady ? ControlSpawnReadiness.Ready : ControlSpawnReadiness.Wait;
    }
    // Reserve BEFORE any native mutation; failure means the caller must not switch control.
    internal bool TryReserve(ControlLeaseIdentity identity, double durationSeconds,
        ControlSafetyCapabilities capabilities, out ControlLease? lease, out string error)
    {
        lease = null;
        error = "Invalid identity or duration.";
        if (identity.WorldEpoch == 0 || identity.User == 0 || string.IsNullOrWhiteSpace(identity.Owner)
            || !identity.Canonical.IsValid || !identity.Temporary.IsValid
            || identity.Canonical == identity.Temporary || !double.IsFinite(durationSeconds)
            || durationSeconds <= 0 || durationSeconds > MaximumDurationSeconds) return false;
        error = "Required safety capabilities are unavailable.";
        if ((capabilities & ControlSafetyCapabilities.Required) != ControlSafetyCapabilities.Required) return false;
        error = "Outstanding lease limit reached.";
        if (_outstanding.Count >= MaximumLeases) return false;
        error = "User or body already claimed in this world.";
        foreach (var existing in _outstanding)
        {
            var other = existing.Identity;
            if (other.WorldEpoch != identity.WorldEpoch) continue;
            if (other.User == identity.User || other.Canonical == identity.Canonical
                || other.Canonical == identity.Temporary || other.Temporary == identity.Canonical
                || other.Temporary == identity.Temporary) return false;
        }
        lease = new ControlLease(this, identity, durationSeconds);
        _outstanding.Add(lease);
        error = "";
        return true;
    }

    internal ControlLeaseSnapshot[] Snapshot()
    {
        var result = new ControlLeaseSnapshot[_outstanding.Count];
        int i = 0;
        foreach (var lease in _outstanding) result[i++] = lease.Snapshot;
        return result;
    }

    /// <summary>
    /// Retire claims only after the host verifies that the original world's IsCreated is false.
    /// This observation does not mean native control was restored, and must never be inferred
    /// from an entity disappearing or another world becoming active. No native objects are touched.
    /// </summary>
    internal int ObserveWorldEnded(ulong epoch)
    {
        int retired = 0;
        foreach (var lease in new List<ControlLease>(_outstanding))
        {
            if (lease.Identity.WorldEpoch != epoch) continue;
            lease.State = ControlLeaseState.WorldEnded;
            _outstanding.Remove(lease);
            retired++;
        }
        return retired;
    }

    internal bool ObserveActivated(ControlLease lease, ControlBody canonical, ControlBody controlled)
    {
        if (!Owns(lease) || lease.State != ControlLeaseState.Reserved) return false;
        if (canonical != lease.Identity.Canonical || controlled != lease.Identity.Temporary)
            return Fail(lease, "Activation verification failed.");
        lease.State = ControlLeaseState.Active;
        return true;
    }

    // A release/dispose request starts recovery; it never forgets a possibly partial switch.
    internal bool BeginRestore(ControlLease lease)
    {
        if (!Owns(lease) || lease.State == ControlLeaseState.WorldEnded) return false;
        if (lease.State == ControlLeaseState.Restored) return true;
        lease.State = ControlLeaseState.Restoring;
        return true;
    }

    internal bool ObserveRestored(ControlLease lease, ControlBody canonical, ControlBody controlled,
        bool temporaryControlledByCleared, bool previousTemporaryReferenceCleared)
    {
        if (!Owns(lease) || lease.State == ControlLeaseState.WorldEnded) return false;
        if (lease.State == ControlLeaseState.Restored) return true;
        if (lease.State != ControlLeaseState.Restoring && lease.State != ControlLeaseState.Faulted) return false;
        if (canonical != lease.Identity.Canonical || controlled != lease.Identity.Canonical
            || !temporaryControlledByCleared || !previousTemporaryReferenceCleared)
            return Fail(lease, "Restoration verification failed.");
        lease.State = ControlLeaseState.Restored;
        lease.Failure = null;
        _outstanding.Remove(lease);
        return true;
    }

    // Native failure may follow partial mutation. Keep the reservation and block new claims.
    internal bool Fail(ControlLease lease, string error)
    {
        if (!Owns(lease) || lease.State == ControlLeaseState.Restored || lease.State == ControlLeaseState.WorldEnded) return false;
        lease.State = ControlLeaseState.Faulted;
        lease.Failure = error;
        return false;
    }

    private bool Owns(ControlLease? lease) => lease != null && ReferenceEquals(lease.Policy, this)
        && (lease.State == ControlLeaseState.Restored || lease.State == ControlLeaseState.WorldEnded || _outstanding.Contains(lease));
}
