using System;
using System.Collections.Generic;
using Progenitor.Sample;
using Progenitor.Units;
using Xunit;
using UnitsApi = Progenitor.Units.Units;

namespace Progenitor.Tests;

/// <summary>
/// Game-free coverage of Sample <c>.rat patrol</c> / <c>.rat stop</c> (job FUNIT1): the
/// behavior-step sequence end to end through the real <see cref="UnitHandle"/>, the real
/// <c>Units</c> poll and a fake runtime.
/// <para>
/// Its own fake rather than <c>UnitsTests.FakeUnitRuntime</c>: this one also implements
/// <c>IUnitBehaviourRuntime</c> (Idle / Face / Animate / tree selection), which is the whole
/// point of that being a second interface — the older fake still compiles without it, and a
/// runtime that lacks it refuses those steps instead of crashing.
/// </para>
/// </summary>
[Collection("PluginLogger")]
public class SamplePatrolTests : IDisposable
{
    private readonly TestLogSource _log = new();
    private readonly BehaviourFake _server = new();
    private readonly object _owner = new();

    public SamplePatrolTests()
    {
        Plugin.Logger = _log;
        UnitsApi.ResetForTests();
        _server.Add(SampleUnits.PrefabName);
        _server.AddTree(SampleUnits.QuietTree);
        UnitsApi.SetRuntimeForTests(_server);
        UnitsApi.SetClockForTests(() => _server.Clock);
        SampleUnits.AttachForTests(_owner, "progenitor.sample");
    }

    public void Dispose()
    {
        SampleUnits.Detach();
        UnitsApi.ResetForTests();
        Plugin.Logger = null;
    }

    [Fact]
    public void Patrol_spawns_on_the_quiet_tree_and_runs_idle_face_wait_go_then_wait_animate_follow()
    {
        var from = new UnitVec3(0f, 0f, 0f);
        var forward = new UnitVec3(1f, 0f, 0f);

        string reply = SampleUnits.Run(SampleUnits.Kind.Patrol, SampleUnits.DefaultDistance, from, forward);

        Assert.Equal(1, SampleUnits.LiveCount);
        Assert.Contains("patrolling", reply, StringComparison.Ordinal);
        // Tree selection happens on the spawn tick, before the sequence starts.
        Assert.Equal(SampleUnits.QuietTree, _server.SelectedTree);
        Assert.Contains(_log.Infos, l => l.Contains("behavior tree '" + SampleUnits.QuietTree + "' bound", StringComparison.Ordinal));

        // Step 1 is an explicit Idle. The sequencer's own forced post is skipped rather than
        // doubled when step 1 already is one, so the driver sees exactly one Idle for the whole
        // sequence - and never a second one between steps, which would walk the rat home.
        Assert.Equal(1, _server.IdleCalls);

        // Step 2 Face is immediate; step 3 Wait(2) holds the sequence.
        Assert.Equal(1, _server.FaceCalls);
        Assert.Equal(0, _server.GoToCalls);

        _server.Clock += 2.0;
        UnitsApi.TickForTests();

        // Step 4 GoTo is under way; the rat has not arrived, so nothing after it has run.
        Assert.Equal(1, _server.GoToCalls);
        Assert.Equal(new UnitVec3(6f, 0f, 0f), _server.LastGoTo);
        Assert.Equal(0, _server.AnimateCalls);

        // Arrive: step 4 completes on the poll, the Then callback runs on the same tick, and step 6 Wait(1) arms.
        _server.SetPosition(_server.LastSpawned, new UnitVec3(6f, 0f, 0f));
        UnitsApi.TickForTests();
        Assert.Contains(_log.Infos, l => l.Contains(SampleUnits.ArrivedNote, StringComparison.Ordinal));
        Assert.Equal(0, _server.AnimateCalls);

        _server.Clock += 1.0;
        UnitsApi.TickForTests();
        Assert.Equal(1, _server.AnimateCalls);
        Assert.Equal(SampleUnits.PatrolAnimation, _server.LastAnimation);

        // Animate holds 1.5 s, then step 8 Follow ends the sequence.
        _server.Clock += 1.5;
        UnitsApi.TickForTests();
        Assert.Equal(1, _server.FollowCalls);
        Assert.Contains(_log.Infos, l => l.Contains("sequence end Completed 8/8", StringComparison.Ordinal));
    }

    [Fact]
    public void The_refused_animation_is_skipped_and_the_patrol_still_reaches_follow()
    {
        // The live server has no route for Animate at all; the sample must not depend on one.
        _server.RefuseAnimate = true;
        SampleUnits.Run(SampleUnits.Kind.Patrol, SampleUnits.DefaultDistance, default, new UnitVec3(1f, 0f, 0f));

        _server.Clock += 2.0;
        UnitsApi.TickForTests();
        _server.SetPosition(_server.LastSpawned, new UnitVec3(6f, 0f, 0f));
        UnitsApi.TickForTests();
        _server.Clock += 1.0;
        UnitsApi.TickForTests();
        _server.Clock += 1.5;
        UnitsApi.TickForTests();

        Assert.Equal(1, _server.FollowCalls);
        Assert.Contains(_log.Infos, l => l.Contains("sequence end Completed 8/8", StringComparison.Ordinal));
    }

    [Fact]
    public void Stop_ends_the_running_sequence_and_says_so()
    {
        SampleUnits.Run(SampleUnits.Kind.Patrol, SampleUnits.DefaultDistance, default, new UnitVec3(1f, 0f, 0f));

        string reply = SampleUnits.Run(SampleUnits.Kind.Stop);
        Assert.Contains("Stopped rat", reply, StringComparison.Ordinal);
        Assert.Contains(_log.Infos, l => l.Contains("sequence end Stopped", StringComparison.Ordinal));

        // Nothing advances afterwards.
        _server.Clock += 100.0;
        UnitsApi.TickForTests();
        Assert.Equal(0, _server.GoToCalls);

        string again = SampleUnits.Run(SampleUnits.Kind.Stop);
        Assert.Contains("was not running a sequence", again, StringComparison.Ordinal);
    }

    [Fact]
    public void Stop_without_a_rat_tells_the_caller_to_spawn_first()
    {
        Assert.Equal("No live rat. Spawn one with .rat first.", SampleUnits.Run(SampleUnits.Kind.Stop));
    }

    [Fact]
    public void Despawning_the_rat_stops_its_sequence_without_publishing_anything()
    {
        var events = new List<UnitSequenceCompleted>();
        Progenitor.Events.EventBus.On<UnitSequenceCompleted>(e => events.Add(e), owner: _owner, pluginId: "test");
        try
        {
            SampleUnits.Run(SampleUnits.Kind.Patrol, SampleUnits.DefaultDistance, default, new UnitVec3(1f, 0f, 0f));
            Assert.Empty(events);

            SampleUnits.Run(SampleUnits.Kind.Off);

            // Teardown is silent: the handle is disposed, so a subscriber would be handed a
            // dead unit. The log line is still there for an operator.
            Assert.Empty(events);
            Assert.Contains(_log.Infos, l => l.Contains("sequence end Stopped 2/8 (teardown)", StringComparison.Ordinal));
        }
        finally
        {
            Progenitor.Events.EventBus.RemoveAll(_owner);
        }
    }

    [Fact]
    public void An_unknown_tree_is_refused_and_the_patrol_runs_on_the_prefabs_own_tree()
    {
        _server.ForgetTrees();
        SampleUnits.Run(SampleUnits.Kind.Patrol, SampleUnits.DefaultDistance, default, new UnitVec3(1f, 0f, 0f));

        Assert.Null(_server.SelectedTree);
        Assert.Contains(_log.Infos, l => l.Contains("no behavior tree named", StringComparison.Ordinal));

        // The sequence is unaffected: tree selection is a preference, not a precondition.
        _server.Clock += 2.0;
        UnitsApi.TickForTests();
        Assert.Equal(1, _server.GoToCalls);
    }

    [Fact]
    public void The_patrol_reply_and_the_log_say_the_animation_is_a_placeholder()
    {
        // R55 finding 8: the placeholder was advertised as an animation in the reply, the log
        // and both doc tables, and denied only in a paragraph and a const docstring. The step
        // stays — it really does hold the patrol for 1.5 s — but it says what it is.
        string reply = SampleUnits.Run(SampleUnits.Kind.Patrol, SampleUnits.DefaultDistance, default, new UnitVec3(1f, 0f, 0f));

        Assert.Contains("Animate('Idle') is a placeholder; no server-side route", reply, StringComparison.Ordinal);
        Assert.DoesNotContain(", animate,", reply, StringComparison.Ordinal);
        Assert.Contains(_log.Infos, l =>
            l.Contains("rat patrol", StringComparison.Ordinal)
            && l.Contains("is a placeholder; no server-side route", StringComparison.Ordinal));
    }

    /// <summary>
    /// The patrol is the worked example for every <c>UnitStep</c> factory, so all seven have to
    /// be in it: Idle, GoTo, Follow, Face, Wait, Animate, Then. Idle can only be step 1 (an Idle
    /// posted to a unit that has already walked sends its tree to Return and walks it home,
    /// <c>unit-movement.md</c> §20), which the <c>IdleCalls</c> assertion above pins from the
    /// driver's side; this pins the source.
    /// </summary>
    [Fact]
    public void The_patrol_sequence_uses_every_UnitStep_factory_and_posts_Idle_only_as_step_one()
    {
        string src = Source();

        SourcePin.AssertStatementExactly(src, "UnitStep.Idle(),");
        SourcePin.AssertStatementExactly(src, "UnitStep.Face(target),");
        SourcePin.AssertStatementExactly(src, "UnitStep.Wait(2f),");
        SourcePin.AssertStatementExactly(src, "UnitStep.GoTo(goal),");
        SourcePin.AssertStatementExactly(src, "UnitStep.Then(() => Log(ArrivedNote)),");
        SourcePin.AssertStatementExactly(src, "UnitStep.Wait(1f),");
        SourcePin.AssertStatementExactly(src, "UnitStep.Animate(PatrolAnimation, 1.5f),");
        SourcePin.AssertStatementExactly(src, "UnitStep.Follow(target));");

        // Exactly one Idle, and it is the first line of the Play call.
        int idles = 0;
        int at = 0;
        while ((at = src.IndexOf("UnitStep.Idle()", at, StringComparison.Ordinal)) >= 0) { idles++; at += 1; }
        Assert.Equal(1, idles);
        Assert.True(
            src.IndexOf("UnitStep.Idle()", StringComparison.Ordinal)
            < src.IndexOf("UnitStep.Face(target)", StringComparison.Ordinal));
    }

    [Fact]
    public void The_patrol_reply_names_the_idle_and_the_callback_steps()
    {
        string reply = SampleUnits.Run(SampleUnits.Kind.Patrol, SampleUnits.DefaultDistance, default, new UnitVec3(1f, 0f, 0f));

        Assert.Contains("idle, face, wait, walk to", reply, StringComparison.Ordinal);
        Assert.Contains("run a Then callback", reply, StringComparison.Ordinal);
    }

    /// <summary>
    /// The callback is a delegate in a reloadable plugin's assembly, so it must hold no state of
    /// its own: the module's owner sweep takes it out on unload
    /// (<c>UnitStep.WithoutCallback</c>) and a closure over live objects would defeat that.
    /// </summary>
    [Fact]
    public void The_Then_callback_closes_over_nothing_but_a_constant()
    {
        SourcePin.AssertStatementExactly(Source(), "UnitStep.Then(() => Log(ArrivedNote)),");
        Assert.False(string.IsNullOrEmpty(SampleUnits.ArrivedNote));
    }

    private static string Source()
    {
        var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            string candidate = System.IO.Path.Combine(dir.FullName, "samples", "Progenitor.Sample", "SampleUnits.cs");
            if (System.IO.File.Exists(candidate))
                return System.IO.File.ReadAllText(candidate).Replace("\r\n", "\n");
            dir = dir.Parent;
        }

        throw new System.IO.FileNotFoundException("samples/Progenitor.Sample/SampleUnits.cs not found from " + AppContext.BaseDirectory);
    }

    [Fact]
    public void RemoveAll_drops_a_playing_sequence_and_the_Then_callback_it_holds()
    {
        // R55 finding 9: a sequence holds an Action a reloadable plugin wrote — target and
        // method in its collectible ALC — for as long as it plays, which is enough to refuse a
        // Core reload. The owner sweep has to reach it.
        var owner = new SweepOwner();
        var handle = UnitsApi.Spawn(SampleUnits.PrefabName, default, owner, "progenitor.sample");
        int ran = 0;
        handle.Behaviour.Play(UnitStep.Then(() => ran++), UnitStep.Wait(600f));
        Assert.Equal(1, ran);
        Assert.True(handle.Behaviour.IsPlaying);

        UnitsApi.RemoveAll(owner);

        Assert.True(handle.IsDisposed);
        Assert.False(handle.Behaviour.IsPlaying);
        Assert.Equal(0, handle.Behaviour.StepCount);
    }

    [Fact]
    public void An_ownerless_sequence_holding_a_Then_from_the_unloading_assembly_keeps_running_without_it()
    {
        // The owner match cannot see a static-API spawn that passed no owner, and that is the
        // case the module's ALC sentence was written to reassure about. The delegate goes — it
        // is the only thing pinning the unloading ALC — and nothing else does: the unit stays
        // and so does the rest of the sequence (R57 finding 1 reshaped this from a drop).
        var orphan = UnitsApi.Spawn(SampleUnits.PrefabName, default);
        orphan.Behaviour.Play(UnitStep.Then(() => { }), UnitStep.Wait(600f));
        Assert.True(orphan.Behaviour.IsPlaying);

        UnitsApi.SweepForUnload(new SweepOwner(), "progenitor.sample");

        Assert.False(orphan.IsDisposed);
        Assert.True(orphan.Behaviour.IsPlaying);
        Assert.Equal(2, orphan.Behaviour.StepCount);
        Assert.False(((UnitHandle)orphan).HoldsCallbackFrom(typeof(SamplePatrolTests).Assembly));
        // One line per handle, naming the assembly the delegates came from and how many went,
        // plus the "pass owner:" hint for the ownerless ones (R56 finding 10 reshaped it).
        Assert.Contains(_log.Infos, l =>
            l.Contains("dropped 1 Then callback(s) written in", StringComparison.Ordinal)
            && l.Contains("(owner: none)", StringComparison.Ordinal)
            && l.Contains("the rest of the sequence keeps running", StringComparison.Ordinal));
        Assert.Contains(_log.Infos, l =>
            l.Contains("had no owner; pass owner: to Units.Spawn", StringComparison.Ordinal));
    }

    [Fact]
    public void An_ownerless_sequence_with_no_plugin_callback_is_left_alone()
    {
        var orphan = UnitsApi.Spawn(SampleUnits.PrefabName, default);
        orphan.Behaviour.Play(UnitStep.Wait(600f));

        UnitsApi.SweepForUnload(new SweepOwner(), "progenitor.sample");

        Assert.True(orphan.Behaviour.IsPlaying);
    }

    [Fact]
    public void The_sample_names_the_only_tree_this_project_has_read_off_a_live_unit()
    {
        Assert.Equal("BEH_Farmlands_Animal_Standard", SampleUnits.QuietTree);
        Assert.False(string.IsNullOrWhiteSpace(SampleUnits.PatrolAnimation));
    }

    /// <summary>
    /// Stands in for the unloading plugin instance: the sweep keys a handle on the owner
    /// object's assembly (or, since R58 finding 3, on the <c>pluginId</c> the handle was
    /// spawned with), so the owner has to live in the same assembly as the <c>Then</c>
    /// callbacks — which a real BepInEx plugin instance does and <c>new object()</c> does not.
    /// The id is passed to <c>SweepForUnload</c> the way <c>PluginReloader</c> passes it (job
    /// FUNIT6, R59 finding 1) and matches nothing here, because the handles these tests sweep
    /// were spawned with no id at all: they exercise the assembly half.
    /// </summary>
    private sealed class SweepOwner
    {
    }

    /// <summary>
    /// Enough of a unit runtime to run a sequence: spawn / steer / despawn plus the four
    /// behavior writes. The clock is a field so a Wait is a value, not a sleep.
    /// </summary>
    private sealed class BehaviourFake : IUnitRuntime, IUnitBehaviourRuntime
    {
        private readonly HashSet<string> _names = new(StringComparer.Ordinal);
        private readonly HashSet<string> _trees = new(StringComparer.Ordinal);
        private readonly Dictionary<UnitId, UnitVec3> _pos = new();
        private int _next = 10;

        public double Clock;
        public bool IsServer => true;
        public string? LastMissReason => null;
        public bool RefuseAnimate { get; set; }

        public int GoToCalls { get; private set; }
        public int FollowCalls { get; private set; }
        public int FaceCalls { get; private set; }
        public int AnimateCalls { get; private set; }
        public int IdleCalls { get; private set; }
        public UnitVec3 LastGoTo { get; private set; }
        public string? LastAnimation { get; private set; }
        public string? SelectedTree { get; private set; }
        public UnitId LastSpawned { get; private set; }

        public void Add(string name) => _names.Add(name);

        public void AddTree(string name) => _trees.Add(name);

        public void ForgetTrees() => _trees.Clear();

        public void SetPosition(UnitId id, UnitVec3 position) => _pos[id] = position;

        public UnitId Spawn(string prefabName, int prefabGuid, UnitVec3 position)
        {
            if (!_names.Contains(prefabName)) return UnitId.None;
            var id = new UnitId(_next++, 1);
            _pos[id] = position;
            LastSpawned = id;
            return id;
        }

        public bool Follow(UnitId unit, UnitId target)
        {
            FollowCalls++;
            return _pos.ContainsKey(unit) && !target.IsNone;
        }

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

        public bool GoTo(UnitId unit, UnitVec3 position)
        {
            GoToCalls++;
            LastGoTo = position;
            return _pos.ContainsKey(unit);
        }

        public bool Despawn(UnitId unit) => _pos.Remove(unit);

        public bool Exists(UnitId unit) => _pos.ContainsKey(unit);

        public bool TryGetPosition(UnitId unit, out UnitVec3 position) => _pos.TryGetValue(unit, out position);

        public bool Idle(UnitId unit)
        {
            IdleCalls++;
            return _pos.ContainsKey(unit);
        }

        public bool Face(UnitId unit, UnitId target)
        {
            FaceCalls++;
            return _pos.ContainsKey(unit);
        }

        public bool Face(UnitId unit, UnitVec3 position)
        {
            FaceCalls++;
            return _pos.ContainsKey(unit);
        }

        public bool Animate(UnitId unit, string animationName, float seconds, out string? refusal)
        {
            AnimateCalls++;
            LastAnimation = animationName;
            refusal = RefuseAnimate ? "Units: Animate('" + animationName + "') has no server-side route." : null;
            return !RefuseAnimate;
        }

        public bool SelectBehaviourTree(UnitId unit, string treeName, int treeGuid, out string? refusal)
        {
            if (!_trees.Contains(treeName))
            {
                refusal = "Units: no behavior tree named '" + treeName + "' on this server; keeping the prefab's own tree.";
                return false;
            }

            SelectedTree = treeName;
            refusal = null;
            return true;
        }
    }
}
