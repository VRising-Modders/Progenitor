using System.Collections.Generic;
using System.Numerics;
using Progenitor.Ui;
using Xunit;

namespace Progenitor.Tests;

public sealed class BuildRecentCorrelationTests
{
    private static readonly Vector3 Position = new(10, 5, 20);
    private static void Add(BuildRecentCorrelation c, ulong source = 1, bool delayed = false,
        long at = 0, HashSet<ulong>? existing = null) =>
        c.Add(source, delayed, 7, 123, Position, 2, at, existing ?? new());
    private static bool Confirm(BuildRecentCorrelation c, ulong target = 90, ulong user = 7,
        int guid = 123, byte rotation = 2, long at = 1) =>
        c.Confirm(target, user, guid, Position, rotation, at);

    [Fact] public void RequestAloneDoesNotConfirmAnything()
    { var c = new BuildRecentCorrelation(); Assert.False(Confirm(c)); }

    [Fact] public void ExactNewTargetIsConsumedOnce()
    { var c = new BuildRecentCorrelation(); Add(c); Assert.True(Confirm(c)); Assert.False(Confirm(c)); Assert.False(Confirm(c, target: 91)); }

    [Fact] public void RepeatedRequestEntityIsNotASecondRequest()
    { var c = new BuildRecentCorrelation(); Add(c); Add(c); Assert.True(Confirm(c)); }

    [Fact] public void ExistingStructureNeverConfirms()
    { var c = new BuildRecentCorrelation(); Add(c, existing: new() { 90 }); Assert.False(Confirm(c)); }

    [Theory]
    [InlineData(8, 123, 2)] [InlineData(7, 456, 2)] [InlineData(7, 123, 1)]
    public void OtherUserBlueprintOrRotationDoesNotConsume(ulong user, int guid, byte rotation)
    { var c = new BuildRecentCorrelation(); Add(c); Assert.False(Confirm(c, user: user, guid: guid, rotation: rotation)); Assert.True(Confirm(c)); }

    [Fact] public void AdjustedTransformIsOmitted()
    { var c = new BuildRecentCorrelation(); Add(c); Assert.False(c.Confirm(90, 7, 123, Position + Vector3.UnitX, 2, 1)); }

    [Fact] public void ExpiryDoesNotTurnAFailedAttemptIntoLaterSuccess()
    { var c = new BuildRecentCorrelation(); Add(c); Assert.False(Confirm(c, at: BuildRecentCorrelation.LifetimeMs + 1)); }

    [Fact] public void AmbiguousRequestsAreConsumedWithoutNotification()
    { var c = new BuildRecentCorrelation(); Add(c); Add(c, source: 2); Assert.False(Confirm(c)); Assert.False(Confirm(c, target: 91)); }

    [Fact] public void DelayedCompletionReplacesEarlierIntent()
    { var c = new BuildRecentCorrelation(); Add(c); Add(c, source: 2, delayed: true, at: 500); Assert.True(Confirm(c, at: 501)); }

    [Fact] public void DelayedCompletionAfterLongCastStartsFreshWindow()
    { var c = new BuildRecentCorrelation(); Add(c); Add(c, source: 2, delayed: true, at: 10000); Assert.True(Confirm(c, at: 10001)); }

    [Fact] public void AmbiguousDelayedCompletionsAreRejected()
    { var c = new BuildRecentCorrelation(); Add(c, delayed: true); Add(c, source: 2, delayed: true); Assert.False(Confirm(c)); }

    [Fact] public void CapacityOverflowCannotLeaveOneApparentWinner()
    {
        var c = new BuildRecentCorrelation();
        for (ulong i = 1; i <= BuildRecentCorrelation.Capacity + 2; i++) Add(c, source: i);
        Assert.False(Confirm(c));
    }

    [Fact] public void WorldResetDropsPendingAndDedupState()
    { var c = new BuildRecentCorrelation(); Add(c); c.Clear(); Assert.False(Confirm(c)); Add(c); Assert.True(Confirm(c)); }

    [Fact] public void NonFinitePositionsCannotMatch()
    { var c = new BuildRecentCorrelation(); c.Add(1, false, 7, 123, new(float.NaN, 5, 20), 2, 0, new()); Assert.False(Confirm(c)); }

    [Fact] public void AddReportsWhetherTheRequestWasRecorded()
    {
        var c = new BuildRecentCorrelation();
        Assert.True(c.Add(1, false, 7, 123, Position, 2, 0, new()));
        Assert.False(c.Add(1, false, 7, 123, Position, 2, 0, new())); // the same request entity again
        Assert.False(c.Add(2, false, 7, 123, Position, 4, 0, new())); // no such rotation
        Assert.Equal(1, c.PendingCount);
        Assert.True(c.HasPendingFor(7));
        Assert.False(c.HasPendingFor(8));
    }

    [Fact] public void CheckNamesTheFirstGateNoRequestPassed()
    {
        var c = new BuildRecentCorrelation();
        Assert.Equal(BuildRecentConfirm.NoPendingForUser, c.Check(90, 7, 123, Position, 2, 1));
        Add(c, existing: new() { 95 });
        Assert.Equal(BuildRecentConfirm.InvalidTarget, c.Check(0, 7, 123, Position, 2, 1));
        Assert.Equal(BuildRecentConfirm.NoPendingForUser, c.Check(90, 8, 123, Position, 2, 1));
        Assert.Equal(BuildRecentConfirm.GuidMismatch, c.Check(90, 7, 456, Position, 2, 1));
        Assert.Equal(BuildRecentConfirm.RotationMismatch, c.Check(90, 7, 123, Position, 1, 1));
        Assert.Equal(BuildRecentConfirm.PositionMismatch, c.Check(90, 7, 123, Position + new Vector3(0, 0.25f, 0), 2, 1));
        Assert.Equal(new Vector3(0, 0.25f, 0), c.LastPositionMiss);
        Assert.Equal(BuildRecentConfirm.ExistedBefore, c.Check(95, 7, 123, Position, 2, 1));
        Assert.Equal(BuildRecentConfirm.Confirmed, c.Check(90, 7, 123, Position, 2, 1));
        Assert.Equal(BuildRecentConfirm.AlreadyConfirmed, c.Check(90, 7, 123, Position, 2, 1));
    }

    [Fact] public void PositionToleranceIsAThousandthOfAUnit()
    {
        var c = new BuildRecentCorrelation(); Add(c);
        Assert.Equal(BuildRecentConfirm.PositionMismatch, c.Check(90, 7, 123, Position + new Vector3(0.002f, 0, 0), 2, 1));
        Assert.Equal(BuildRecentConfirm.Confirmed, c.Check(90, 7, 123, Position + new Vector3(0.0005f, 0, 0), 2, 1));
    }

    [Fact] public void AmbiguousIsReportedAsSuch()
    { var c = new BuildRecentCorrelation(); Add(c); Add(c, source: 2); Assert.Equal(BuildRecentConfirm.Ambiguous, c.Check(90, 7, 123, Position, 2, 1)); }

    [Fact] public void StatsCountEachStageAndListNamedStagesAtZero()
    {
        var s = new BuildRecentStats();
        s.Add("hellosSent"); s.Add("hellosSent"); s.Add(BuildRecentStats.NameOf(BuildRecentConfirm.PositionMismatch));
        var snap = s.Snapshot(new[] { "hellosSent", "packetsReceived" });
        Assert.Equal(2L, snap["hellosSent"]);
        Assert.Equal(0L, snap["packetsReceived"]);
        Assert.Equal(1L, snap["confirmPositionMismatch"]);
        Assert.Equal(new[] { "hellosSent", "packetsReceived" }, System.Linq.Enumerable.Take(snap.Keys, 2));
        s.Clear();
        Assert.Equal(0, s.Get("hellosSent"));
    }
}
