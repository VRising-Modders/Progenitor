using System;
using System.Collections.Generic;
using Progenitor.Sample;
using Progenitor.Units;
using Xunit;
using UnitsApi = Progenitor.Units.Units;

namespace Progenitor.Tests;

/// <summary>
/// Job FEX3 (RD6 J9). The Sample's unit-event subscribers, driven through the real
/// <see cref="UnitHandle"/>, the real sequencer and a fake runtime — so the events these tests
/// see are published by the same code the server runs, not by the test.
///
/// Two of the assertions are the contract rather than the example: teardown publishes no
/// <see cref="UnitSequenceCompleted"/>, and an entity that vanishes mid-walk raises
/// <see cref="UnitUnreachable"/> rather than nothing. Both are things a subscriber will
/// otherwise discover by waiting for an event that never comes.
///
/// Shares the <c>PluginLogger</c> collection with the other Units suites: the runtime, the clock
/// and <see cref="Plugin.Logger"/> are all process-wide.
/// </summary>
[Collection("PluginLogger")]
public class SampleUnitEventsTests : IDisposable
{
    private readonly TestLogSource _log = new();
    private readonly EventFake _server = new();
    private readonly object _owner = new();

    public SampleUnitEventsTests()
    {
        Plugin.Logger = _log;
        UnitsApi.ResetForTests();
        _server.Add(SampleUnits.PrefabName);
        _server.AddTree(SampleUnits.QuietTree);
        UnitsApi.SetRuntimeForTests(_server);
        UnitsApi.SetClockForTests(() => _server.Clock);

        // The patrol's Face / Follow target. It has to exist and be far away: a follow target the
        // runtime does not know is reported "follow target gone" on the next poll (UnitHandle.cs:271),
        // and one within FollowArriveDistance raises an arrival. Neither belongs in these counts.
        _server.SetPosition(SampleUnits.PatrolTestTarget, new UnitVec3(100f, 0f, 0f));
        SampleUnits.AttachForTests(_owner, "progenitor.sample");
        SampleUnitEvents.Subscribe(_owner, "progenitor.sample");
    }

    public void Dispose()
    {
        SampleUnitEvents.Unsubscribe();
        SampleUnits.Detach();
        UnitsApi.ResetForTests();
        Progenitor.Events.EventBus.RemoveAll(_owner);
        Plugin.Logger = null;
    }

    /// <summary>Run the patrol from spawn to the last step, the way SamplePatrolTests clocks it.</summary>
    private void RunWholePatrol()
    {
        SampleUnits.Run(SampleUnits.Kind.Patrol, SampleUnits.DefaultDistance, default, new UnitVec3(1f, 0f, 0f));
        _server.Clock += 2.0;
        UnitsApi.TickForTests();
        _server.SetPosition(_server.LastSpawned, new UnitVec3(6f, 0f, 0f));
        UnitsApi.TickForTests();
        _server.Clock += 1.0;
        UnitsApi.TickForTests();
        _server.Clock += 1.5;
        UnitsApi.TickForTests();
    }

    [Fact]
    public void A_whole_patrol_reports_every_step_the_arrival_and_the_sequence_end()
    {
        RunWholePatrol();

        Assert.Equal(8, SampleUnitEvents.StepCount);
        Assert.Equal(1, SampleUnitEvents.SequenceCount);
        Assert.Equal(1, SampleUnitEvents.ArrivedCount);

        var lines = SampleUnitEvents.RecentLines;
        Assert.Contains(lines, l => l.StartsWith("step 5/8 Invoke -> Completed", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("step 4/8 GoTo -> Completed", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("arrived entity=", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("sequence Completed 8/8", StringComparison.Ordinal));
    }

    [Fact]
    public void Every_event_writes_one_log_line_so_the_server_log_alone_proves_it_fired()
    {
        // SampleLog's BepInEx call is the one thing behind !PROGENITOR_TESTS, so what the suite
        // can see is its sink — the same string, one step before the log file.
        var written = new List<string>();
        SampleLog.Sink = written.Add;
        try
        {
            RunWholePatrol();
        }
        finally
        {
            SampleLog.Sink = null;
        }

        Assert.Contains(written, l => l.StartsWith("Sample rat-events: step 1/8", StringComparison.Ordinal));
        Assert.Contains(written, l => l.StartsWith("Sample rat-events: arrived entity=", StringComparison.Ordinal));
        Assert.Contains(written, l => l.StartsWith("Sample rat-events: sequence Completed 8/8", StringComparison.Ordinal));
    }

    [Fact]
    public void A_refused_animation_is_reported_Skipped_and_the_sequence_still_completes()
    {
        _server.RefuseAnimate = true;

        RunWholePatrol();

        Assert.Contains(SampleUnitEvents.RecentLines, l => l.StartsWith("step 7/8 Animate -> Skipped", StringComparison.Ordinal));
        Assert.Contains(SampleUnitEvents.RecentLines, l => l.StartsWith("sequence Completed 8/8", StringComparison.Ordinal));
    }

    [Fact]
    public void Teardown_publishes_no_sequence_event_at_all()
    {
        // UnitBehaviourEvents.cs: Despawn and Core uninstall stop the sequencer silently,
        // because the handle is on its way out. `.rat off` is Units.RemoveAll with the very
        // owner the rats were spawned with, so every one of them is despawned by the reference
        // match and takes that silent route - the published Stopped FUNIT4 added is for a
        // sequence the sweep leaves alive (a second owner object from the unloading assembly,
        // or a handle carrying the plugin's id), and there is none here. So a subscriber must
        // still not treat a missing UnitSequenceCompleted after `.rat off` as a dropped event.
        SampleUnits.Run(SampleUnits.Kind.Patrol, SampleUnits.DefaultDistance, default, new UnitVec3(1f, 0f, 0f));
        Assert.Equal(0, SampleUnitEvents.SequenceCount);

        SampleUnits.Run(SampleUnits.Kind.Off);

        Assert.Equal(0, SampleUnitEvents.SequenceCount);
    }

    [Fact]
    public void A_stopped_sequence_does_report_because_nothing_is_being_torn_down()
    {
        SampleUnits.Run(SampleUnits.Kind.Patrol, SampleUnits.DefaultDistance, default, new UnitVec3(1f, 0f, 0f));

        SampleUnits.Run(SampleUnits.Kind.Stop);

        Assert.Equal(1, SampleUnitEvents.SequenceCount);
        Assert.Contains(SampleUnitEvents.RecentLines, l => l.StartsWith("sequence Stopped", StringComparison.Ordinal));
    }

    [Fact]
    public void An_entity_that_vanishes_mid_walk_is_reported_unreachable_with_the_reason()
    {
        var handle = UnitsApi.Spawn(SampleUnits.PrefabName, default, _owner, "progenitor.sample");
        handle.GoTo(new UnitVec3(20f, 0f, 0f));

        _server.Forget(handle.Entity);
        UnitsApi.TickForTests();

        Assert.Equal(1, SampleUnitEvents.UnreachableCount);
        Assert.Equal(0, SampleUnitEvents.ArrivedCount);
        Assert.Contains(SampleUnitEvents.RecentLines, l =>
            l.StartsWith("unreachable entity=", StringComparison.Ordinal)
            && l.Contains("reason=entity gone", StringComparison.Ordinal));
    }

    [Fact]
    public void Unsubscribe_stops_the_counters_and_clears_the_ring()
    {
        RunWholePatrol();
        Assert.NotEmpty(SampleUnitEvents.RecentLines);

        SampleUnitEvents.Unsubscribe();

        Assert.False(SampleUnitEvents.IsSubscribed);
        Assert.Empty(SampleUnitEvents.RecentLines);
        Assert.Equal(0, SampleUnitEvents.StepCount);

        RunWholePatrol();

        Assert.Equal(0, SampleUnitEvents.StepCount);
        Assert.Equal(0, SampleUnitEvents.SequenceCount);
    }

    [Fact]
    public void Subscribing_twice_does_not_double_count()
    {
        SampleUnitEvents.Subscribe(_owner, "progenitor.sample");

        RunWholePatrol();

        Assert.Equal(8, SampleUnitEvents.StepCount);
        Assert.Equal(1, SampleUnitEvents.SequenceCount);
    }

    [Fact]
    public void The_ring_keeps_only_the_last_lines_and_status_prints_them_after_the_counters()
    {
        RunWholePatrol();
        RunWholePatrol();

        Assert.Equal(SampleUnitEvents.Capacity, SampleUnitEvents.RecentLines.Count);

        var status = SampleUnitEvents.StatusLines();
        Assert.Equal(SampleUnitEvents.Capacity + 1, status.Count);
        Assert.StartsWith("unit events: arrived=2 unreachable=0 steps=16 sequences=2 subscribed=True", status[0], StringComparison.Ordinal);
        Assert.StartsWith("  ", status[1], StringComparison.Ordinal);
    }

    [Fact]
    public void Status_with_nothing_heard_yet_says_what_to_type_instead_of_printing_zero_lines()
    {
        var status = SampleUnitEvents.StatusLines();

        Assert.Equal(2, status.Count);
        Assert.Contains("arrived=0", status[0], StringComparison.Ordinal);
        Assert.Contains(".rat patrol", status[1], StringComparison.Ordinal);
    }

    [Fact]
    public void Rat_status_is_a_read_that_needs_no_rat()
    {
        Assert.True(SampleUnits.TryParse("status", out var kind, out _));
        Assert.Equal(SampleUnits.Kind.Status, kind);

        string reply = SampleUnits.Run(SampleUnits.Kind.Status);

        Assert.StartsWith("unit events: ", reply, StringComparison.Ordinal);
        Assert.Equal(0, SampleUnits.LiveCount);
    }

    [Fact]
    public void The_lines_name_the_entity_the_outcome_and_the_reason()
    {
        var id = new UnitId(11, 1);

        Assert.Equal("arrived entity=" + id + " at=" + new UnitVec3(1f, 2f, 3f),
            SampleUnitEvents.ArrivedLine(id, new UnitVec3(1f, 2f, 3f)));
        Assert.Equal("unreachable entity=" + id + " goal=" + new UnitVec3(0f, 0f, 0f) + " reason=entity gone",
            SampleUnitEvents.UnreachableLine(id, default, "entity gone"));
        Assert.Equal("unreachable entity=" + id + " goal=" + new UnitVec3(0f, 0f, 0f) + " reason=<none given>",
            SampleUnitEvents.UnreachableLine(id, default, null));

        // A completed step never trails a reason; a failed one always does.
        Assert.Equal("step 2/6 Wait -> Completed entity=" + id,
            SampleUnitEvents.StepLine(id, UnitStepKind.Wait, 1, 6, UnitStepOutcome.Completed, ""));
        Assert.Equal("step 3/6 GoTo -> TimedOut entity=" + id + " (no players in range)",
            SampleUnitEvents.StepLine(id, UnitStepKind.GoTo, 2, 6, UnitStepOutcome.TimedOut, "no players in range"));
        Assert.Equal("sequence Failed 2/6 entity=" + id + " (step 3 GoTo)",
            SampleUnitEvents.SequenceLine(id, UnitSequenceOutcome.Failed, 2, 6, "step 3 GoTo"));
    }

    /// <summary>
    /// Enough of a unit runtime to spawn, steer and run a sequence. <see cref="Forget"/> is the
    /// only addition over the patrol suite's fake: an entity that stops existing is how a live
    /// server produces <see cref="UnitUnreachable"/>.
    /// </summary>
    private sealed class EventFake : IUnitRuntime, IUnitBehaviourRuntime
    {
        private readonly HashSet<string> _names = new(StringComparer.Ordinal);
        private readonly HashSet<string> _trees = new(StringComparer.Ordinal);
        private readonly Dictionary<UnitId, UnitVec3> _pos = new();
        private int _next = 10;

        public double Clock;
        public bool IsServer => true;
        public string? LastMissReason => null;
        public bool RefuseAnimate { get; set; }
        public UnitId LastSpawned { get; private set; }

        public void Add(string name) => _names.Add(name);

        public void AddTree(string name) => _trees.Add(name);

        public void SetPosition(UnitId id, UnitVec3 position) => _pos[id] = position;

        public void Forget(UnitId id) => _pos.Remove(id);

        public UnitId Spawn(string prefabName, int prefabGuid, UnitVec3 position)
        {
            if (!_names.Contains(prefabName)) return UnitId.None;
            var id = new UnitId(_next++, 1);
            _pos[id] = position;
            LastSpawned = id;
            return id;
        }

        public bool Follow(UnitId unit, UnitId target) => _pos.ContainsKey(unit) && !target.IsNone;

        public UnitEngageResult Engage(UnitId unit, UnitId target) => default;

        public string PetTick(UnitId pet, UnitId owner) => "gone";

        public bool TryGetHealth(UnitId unit, out float maxHealth, out float value, out bool isDead)
        {
            maxHealth = 0f; value = 0f; isDead = false; return false;
        }

        public bool HasDeadComponent(UnitId unit) => false;

        public bool TrySetHealthPools(UnitId unit, float maxHealth, float value, float maxRecovery) => false;

        public bool TryGetUnitPower(UnitId unit, out float physicalPower, out float spellPower)
        {
            physicalPower = 0f; spellPower = 0f; return false;
        }

        public bool TrySetUnitPower(UnitId unit, float? physicalPower, float? spellPower) => false;

        public bool TryGetLevel(UnitId unit, out int level) { level = 0; return false; }

        public bool TrySetLevel(UnitId unit, int level) => false;

        public bool TrySetLifetime(UnitId unit, float seconds) => false;

        public bool TrySetTeam(UnitId unit, int? teamValue, int? factionIndex) => false;

        public UnitStripVBloodResult StripVBlood(UnitId unit) => default;

        public bool AddedFollower(UnitId unit) => false;

        public bool GoTo(UnitId unit, UnitVec3 position) => _pos.ContainsKey(unit);

        public bool Despawn(UnitId unit) => _pos.Remove(unit);

        public bool Exists(UnitId unit) => _pos.ContainsKey(unit);

        public bool TryGetPosition(UnitId unit, out UnitVec3 position) => _pos.TryGetValue(unit, out position);

        public bool Idle(UnitId unit) => _pos.ContainsKey(unit);

        public bool Face(UnitId unit, UnitId target) => _pos.ContainsKey(unit);

        public bool Face(UnitId unit, UnitVec3 position) => _pos.ContainsKey(unit);

        public bool Animate(UnitId unit, string animationName, float seconds, out string? refusal)
        {
            refusal = RefuseAnimate ? "Units: Animate('" + animationName + "') has no server-side route." : null;
            return !RefuseAnimate;
        }

        public bool SelectBehaviourTree(UnitId unit, string treeName, int treeGuid, out string? refusal)
        {
            if (!_trees.Contains(treeName))
            {
                refusal = "Units: no behavior tree named '" + treeName + "'.";
                return false;
            }

            refusal = null;
            return true;
        }
    }
}
