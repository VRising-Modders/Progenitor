using System;
using System.Collections.Generic;
using Progenitor.Dev;
using Progenitor.Effects;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Game-free coverage of server-authored beams: spawn/track/dispose/RemoveAll,
/// server-only gating, and tick writes of both endpoints.
/// </summary>
[Collection("PluginLogger")]
public class ServerBeamTests : IDisposable
{
    private readonly TestLogSource _log = new();
    private readonly FakeServerBeamRuntime _server = new() { IsServer = true };
    private readonly object _owner = new Owner("example.beam.test");

    public ServerBeamTests()
    {
        Plugin.Logger = _log;
        ServerBeam.ResetForTests();
        // R29 finding 15: an off-thread Dispose marshals its ECS work through MainThread, so
        // these facts drive that queue. Reset it either side so nothing leaks between them.
        MainThread.ResetForTests();
        _server.Add("ProfessorCoilBeam");
        ServerBeam.SetRuntimeForTests(_server);
    }

    public void Dispose()
    {
        ServerBeam.ResetForTests();
        MainThread.ResetForTests();
        Plugin.Logger = null;
    }

    [Fact]
    public void Spawn_Track_tick_writes_both_endpoints_and_double_dispose_is_safe()
    {
        var start = new EffectVec3(1f, 2f, 3f);
        var end = new EffectVec3(4f, 5f, 6f);
        var handle = ServerBeam.Spawn(BeamPreset.CastleRed, start, end, 8f, _owner, "example.beam.test");
        Assert.True(handle.Enabled);
        Assert.Equal(1, handle.Count);
        Assert.Equal(BeamPreset.CastleRed, handle.Preset);
        Assert.False(handle.Entity == "0:0");
        Assert.Equal(1, _server.SpawnCalls);
        Assert.Equal(BeamPresets.TintMarkerRed, _server.LastBlastTime);
        Assert.Contains(_log.Infos, l => l.Contains("Effects: server beam spawned prefab=", StringComparison.Ordinal)
            && l.Contains("entity=", StringComparison.Ordinal)
            && l.Contains("len=", StringComparison.Ordinal));

        _server.SetTranslation(10, 1, new EffectVec3(10f, 0f, 0f));
        _server.SetTranslation(20, 1, new EffectVec3(20f, 0f, 0f));
        handle.Track(10, 1, 20, 1, new EffectVec3(0f, 1f, 0f), new EffectVec3(0f, 0.5f, 0f));
        Assert.Contains(_log.Infos, l => l == "Effects: server beam tracking 10:1 -> 20:1");

        ServerBeam.TickForTests();
        Assert.Equal(1, _server.WriteCalls);
        Assert.Equal(new EffectVec3(10f, 1f, 0f), _server.LastStart);
        Assert.Equal(new EffectVec3(20f, 0.5f, 0f), _server.LastEnd);

        handle.Dispose();
        Assert.True(handle.IsDisposed);
        Assert.Equal(1, _server.DestroyCalls);
        Assert.Contains(_log.Infos, l => l == "Effects: server beam disposed");

        handle.Dispose();
        Assert.Equal(1, _server.DestroyCalls);
    }

    [Fact]
    public void Tick_updates_both_ends_when_the_entities_move()
    {
        var handle = ServerBeam.Spawn(BeamPreset.Coil, new EffectVec3(0f, 0f, 0f), new EffectVec3(1f, 0f, 0f), 8f, _owner, "example.beam.test");
        Assert.Equal(BeamPresets.MarkerCoil, _server.LastBlastTime);
        _server.SetTranslation(1, 1, new EffectVec3(0f, 0f, 0f));
        _server.SetTranslation(2, 1, new EffectVec3(6f, 0f, 0f));
        handle.Track(1, 1, 2, 1, default, default);
        ServerBeam.TickForTests();
        Assert.Equal(new EffectVec3(0f, 0f, 0f), _server.LastStart);
        Assert.Equal(new EffectVec3(6f, 0f, 0f), _server.LastEnd);

        _server.SetTranslation(1, 1, new EffectVec3(3f, 1f, 0f));
        _server.SetTranslation(2, 1, new EffectVec3(9f, 1f, 2f));
        ServerBeam.TickForTests();
        Assert.Equal(new EffectVec3(3f, 1f, 0f), _server.LastStart);
        Assert.Equal(new EffectVec3(9f, 1f, 2f), _server.LastEnd);
        handle.Dispose();
    }

    [Fact]
    public void Beams_spawns_count_entities_and_tick_applies_lateral_offsets()
    {
        var start = new EffectVec3(0f, 0f, 0f);
        var end = new EffectVec3(0f, 0f, 10f);
        var handle = ServerBeam.Beams(BeamPreset.Blue, start, end, 8f, 2, 0.4f, _owner, "example.beam.test");
        Assert.True(handle.Enabled);
        Assert.Equal(2, handle.Count);
        Assert.Equal(2, _server.SpawnCalls);
        Assert.Equal(BeamPresets.TintMarkerBlue, _server.LastBlastTime);

        _server.SetTranslation(3, 1, start);
        _server.SetTranslation(4, 1, end);
        handle.Track(3, 1, 4, 1, default, default);
        ServerBeam.TickForTests();
        Assert.Equal(2, _server.WriteCalls);
        var left = BeamPresets.LateralOffset(start, end, 0, 2, 0.4f);
        var right = BeamPresets.LateralOffset(start, end, 1, 2, 0.4f);
        Assert.Equal(2, _server.Writes.Count);
        Assert.Equal(Add(start, left), _server.Writes[0].Start);
        Assert.Equal(Add(end, left), _server.Writes[0].End);
        Assert.Equal(Add(start, right), _server.Writes[1].Start);
        Assert.Equal(Add(end, right), _server.Writes[1].End);
        handle.Dispose();
        Assert.Equal(2, _server.DestroyCalls);
    }

    [Fact]
    public void LateralOffset_spreads_evenly_perpendicular_to_the_span()
    {
        var a = BeamPresets.LateralOffset(new EffectVec3(0f, 0f, 0f), new EffectVec3(0f, 0f, 10f), 0, 2, 0.4f);
        var b = BeamPresets.LateralOffset(new EffectVec3(0f, 0f, 0f), new EffectVec3(0f, 0f, 10f), 1, 2, 0.4f);
        Assert.Equal(0.2f, MathF.Abs(a.X), 3);
        Assert.Equal(-a.X, b.X, 3);
        Assert.Equal(0f, a.Z, 3);
        Assert.Equal(0f, BeamPresets.LateralOffset(new EffectVec3(0f, 0f, 0f), new EffectVec3(1f, 0f, 0f), 0, 1, 0.4f).X);
    }

    [Fact]
    public void RemoveAll_destroys_and_leaves_no_live_handle()
    {
        var handle = ServerBeam.Spawn(BeamPreset.Coil, default, new EffectVec3(1f, 0f, 0f), 8f, _owner, "example.beam.test");
        Assert.Equal(1, ServerBeam.LiveCountForTests);
        ServerBeam.RemoveAll(_owner);
        Assert.True(handle.IsDisposed);
        Assert.Equal(1, _server.DestroyCalls);
        Assert.Equal(0, ServerBeam.LiveCountForTests);
        Assert.Contains(_log.Infos, l => l == "Effects: swept 1 server beam handle(s)");
    }

    [Fact]
    public void Client_Spawn_returns_disabled_and_does_not_instantiate()
    {
        var client = new FakeServerBeamRuntime { IsServer = false };
        client.Add("ProfessorCoilBeam");
        ServerBeam.SetRuntimeForTests(client);

        var handle = ServerBeam.Spawn(BeamPreset.CastleRed, default, new EffectVec3(1f, 0f, 0f), 8f, _owner, "example.beam.test");
        Assert.False(handle.Enabled);
        Assert.Equal("0:0", handle.Entity);
        Assert.Equal(0, client.SpawnCalls);
        handle.Track(1, 1, 2, 1, default, default);
        ServerBeam.TickForTests();
        Assert.Equal(0, client.WriteCalls);
        handle.Dispose();
        Assert.Equal(0, client.DestroyCalls);
    }

    [Fact]
    public void Missing_prefab_returns_disabled_handle()
    {
        var handle = ServerBeam.Spawn("MissingBeam", default, new EffectVec3(1f, 0f, 0f), 8f, _owner, "example.beam.test");
        Assert.False(handle.Enabled);
        Assert.Contains(_log.Infos, l => l.Contains("no coil beam prefab", StringComparison.Ordinal)
            || l.Contains("no prefab", StringComparison.Ordinal));
        handle.Dispose();
    }

    [Fact]
    public void Deadline_disposes_on_tick()
    {
        float t = 0f;
        ServerBeam.TimeForTests = () => t;
        var handle = ServerBeam.Spawn(BeamPreset.Coil, default, new EffectVec3(1f, 0f, 0f), 8f, _owner, "example.beam.test");
        Assert.True(handle.Enabled);
        t = 8f;
        ServerBeam.TickForTests();
        Assert.True(handle.IsDisposed);
        Assert.Equal(1, _server.DestroyCalls);
    }

    [Fact]
    public void Skipped_module_returns_disabled()
    {
        ServerBeam.SetSkippedForTests(true);
        var handle = ServerBeam.Spawn(BeamPreset.Coil, default, new EffectVec3(1f, 0f, 0f), 8f, _owner, "example.beam.test");
        Assert.False(handle.Enabled);
        Assert.Equal(0, _server.SpawnCalls);
        handle.Dispose();
    }

    [Fact]
    public void Beams_clamps_count_to_MaxCount()
    {
        var handle = ServerBeam.Beams(BeamPreset.Coil, default, new EffectVec3(0f, 0f, 1f), 8f, 99, 0.4f, _owner, "example.beam.test");
        Assert.Equal(BeamPresets.MaxCount, handle.Count);
        Assert.Equal(BeamPresets.MaxCount, _server.SpawnCalls);
        handle.Dispose();
    }

    [Fact]
    public void Spawn_pins_PreviewTime_to_the_steady_value_and_marks_the_beam()
    {
        var handle = ServerBeam.Spawn(BeamPreset.CastleRed, default, new EffectVec3(4f, 0f, 0f), 8f, _owner, "example.beam.test");
        Assert.Equal(BeamPhase.Steady, handle.Phase);
        // Not the requested 8s: the client leaves Preview when its own local Age passes the
        // synced PreviewTime, so PreviewTime is pinned near its sync maximum instead.
        Assert.Equal(BeamPresets.SteadyPreviewTime, _server.LastPreviewTime);
        Assert.Equal(BeamPresets.TintMarkerRed, _server.LastBlastTime);
        Assert.True(BeamPresets.IsProgenitorMarker(_server.LastBlastTime));
        Assert.Contains(_log.Infos, l => l.Contains("preview=20", StringComparison.Ordinal) && l.Contains("blast=9100", StringComparison.Ordinal));
        handle.Dispose();
    }

    [Fact]
    public void Every_preset_writes_a_Progenitor_marker_and_only_red_blue_tint()
    {
        Assert.Equal(BeamPresets.MarkerCoil, BeamPresets.BlastTimeFor(BeamPreset.Coil));
        Assert.Equal(BeamPresets.TintMarkerRed, BeamPresets.BlastTimeFor(BeamPreset.CastleRed));
        Assert.Equal(BeamPresets.TintMarkerBlue, BeamPresets.BlastTimeFor(BeamPreset.Blue));
        Assert.True(BeamPresets.IsProgenitorMarker(BeamPresets.MarkerCoil));
        Assert.False(BeamPresets.IsTintMarker(BeamPresets.MarkerCoil));
        // A vanilla coil discharge (a small blast duration in seconds) is never claimed.
        Assert.False(BeamPresets.IsProgenitorMarker(0f));
        Assert.False(BeamPresets.IsProgenitorMarker(2.5f));
    }

    [Fact]
    public void Motionless_beam_still_writes_a_changed_PreviewTime_before_the_client_timeout()
    {
        float t = 0f;
        ServerBeam.TimeForTests = () => t;
        var start = new EffectVec3(0f, 0f, 0f);
        var end = new EffectVec3(5f, 0f, 0f);
        var handle = ServerBeam.Spawn(BeamPreset.CastleRed, start, end, 8f, _owner, "example.beam.test");

        // Nothing tracked, nothing moving: without the keep-alive the coil snapshot never
        // changes, the server stops serializing it and every client disables it after
        // NetworkedTimeout.TimeoutAfter seconds.
        t = 0.5f;
        ServerBeam.TickForTests();
        Assert.Equal(0, _server.WriteCalls);

        t = BeamPresets.KeepAliveSeconds;
        ServerBeam.TickForTests();
        Assert.Equal(1, _server.WriteCalls);
        t = 2f * BeamPresets.KeepAliveSeconds;
        ServerBeam.TickForTests();
        Assert.Equal(2, _server.WriteCalls);

        Assert.Equal(2, _server.PreviewTimes.Count);
        Assert.NotEqual(_server.PreviewTimes[0], _server.PreviewTimes[1]);
        foreach (float p in _server.PreviewTimes)
        {
            Assert.True(p >= BeamPresets.MaxSeconds, "steady PreviewTime " + p + " must outlast the beam");
            Assert.True(p <= BeamPresets.PreviewTimeMax, "PreviewTime " + p + " must stay inside the sync range");
        }

        Assert.True(BeamPresets.KeepAliveSeconds < BeamPresets.NetworkedTimeoutSeconds);
        Assert.Contains(_log.Infos, l => l.Contains("server beam keep-alive", StringComparison.Ordinal));
        handle.Dispose();
    }

    [Fact]
    public void Blast_and_Steady_rewrite_PreviewTime_on_the_spot()
    {
        var handle = ServerBeam.Spawn(BeamPreset.Coil, default, new EffectVec3(3f, 0f, 0f), 8f, _owner, "example.beam.test");
        Assert.Same(handle, handle.Blast());
        Assert.Equal(BeamPhase.Blast, handle.Phase);
        Assert.Equal(BeamPresets.BlastPreviewTime, _server.LastWritePreviewTime);

        handle.Steady();
        Assert.Equal(BeamPhase.Steady, handle.Phase);
        Assert.Equal(BeamPresets.SteadyPreviewTime, _server.LastWritePreviewTime);
        Assert.Contains(_log.Infos, l => l.Contains("server beam phase=Blast", StringComparison.Ordinal));
        handle.Dispose();
    }

    [Fact]
    public void Seconds_are_clamped_to_what_a_steady_coil_beam_can_hold()
    {
        Assert.Equal(BeamPresets.MaxSeconds, BeamPresets.ClampSeconds(120f));
        Assert.Equal(BeamPresets.DefaultSeconds, BeamPresets.ClampSeconds(0f));
        Assert.Equal(5f, BeamPresets.ClampSeconds(5f));
        Assert.True(BeamPresets.MaxSeconds < BeamPresets.SteadyPreviewTimeAlt);

        float t = 0f;
        ServerBeam.TimeForTests = () => t;
        var handle = ServerBeam.Spawn(BeamPreset.Coil, default, new EffectVec3(1f, 0f, 0f), 600f, _owner, "example.beam.test");
        Assert.Contains(_log.Infos, l => l.Contains("server beam seconds clamped", StringComparison.Ordinal));
        t = BeamPresets.MaxSeconds;
        ServerBeam.TickForTests();
        Assert.True(handle.IsDisposed);
    }

    [Fact]
    public void Link_tracks_two_entities_natively_and_writes_nothing_per_tick()
    {
        float t = 0f;
        ServerBeam.TimeForTests = () => t;
        var handle = ServerBeam.Link(11, 1, 22, 3, 8f, sequenceGuid: -287312447, owner: _owner, pluginId: "example.beam.test");
        Assert.True(handle.Enabled);
        Assert.True(handle.TracksEntitiesNatively);
        Assert.Equal(BeamPreset.Link, handle.Preset);
        Assert.Equal(1, _server.LinkCalls);
        Assert.Equal(0, _server.SpawnCalls);
        Assert.Equal(-287312447, _server.LastLinkSequenceGuid);
        Assert.Equal((11, 22), _server.LastLinkEntities);
        Assert.Contains(_log.Infos, l => l.Contains("server beam link spawned prefab=NetworkedSequence", StringComparison.Ordinal));

        // No endpoint writes and no keep-alive: the snapshot syncs the entities themselves and
        // the NetworkedSequence prefab carries no NetworkedTimeout.
        handle.Track(11, 1, 22, 3, default, default);
        handle.Blast();
        t = 5f;
        ServerBeam.TickForTests();
        t = 6f;
        ServerBeam.TickForTests();
        Assert.Equal(0, _server.WriteCalls);
        Assert.Contains(_log.Infos, l => l.Contains("link tracks 11:1 -> 22:3 natively", StringComparison.Ordinal));

        // The deadline still applies.
        t = 8f;
        ServerBeam.TickForTests();
        Assert.True(handle.IsDisposed);
        Assert.Equal(1, _server.DestroyCalls);
    }

    [Fact]
    public void Link_without_a_resolvable_sequence_returns_a_disabled_handle()
    {
        _server.LinkAvailable = false;
        var handle = ServerBeam.Link(11, 1, 22, 3, 8f, owner: _owner, pluginId: "example.beam.test");
        Assert.False(handle.Enabled);
        Assert.Equal("0:0", handle.Entity);
        Assert.Contains(_log.Infos, l => l.Contains("no beam SequenceGUID", StringComparison.Ordinal));
        handle.Dispose();
        Assert.Equal(0, _server.DestroyCalls);
    }

    [Fact]
    public void Client_Link_returns_disabled_and_does_not_spawn()
    {
        var client = new FakeServerBeamRuntime { IsServer = false };
        ServerBeam.SetRuntimeForTests(client);
        var handle = ServerBeam.Link(11, 1, 22, 3, 8f, owner: _owner, pluginId: "example.beam.test");
        Assert.False(handle.Enabled);
        Assert.Equal(0, client.LinkCalls);
        handle.Dispose();
    }

    /// <summary>
    /// R28 finding 29.9: the old version asserted <c>ClientTicks == 1</c> against a fake whose
    /// whole body is <c>ClientTicks++</c>, which pins nothing. What <c>ServerBeam.Poll</c>
    /// actually promises is that the client re-point pass runs on every frame regardless of the
    /// handle list, and that a throwing tick neither stops the handle poll nor escapes it — the
    /// tick is the only thing keeping replicated beams pointed at their endpoints.
    /// </summary>
    [Fact]
    public void Poll_runs_the_client_tick_every_frame_and_survives_a_throwing_tick()
    {
        ServerBeam.TickForTests();
        ServerBeam.TickForTests();
        Assert.Equal(2, _server.ClientTicks);

        float now = 0f;
        ServerBeam.TimeForTests = () => now;
        _server.SetTranslation(10, 1, new EffectVec3(10f, 0f, 0f));
        var handle = ServerBeam.Spawn(BeamPreset.Coil, default, new EffectVec3(1f, 0f, 0f), 8f, _owner);
        handle.Track(10, 1, 0, 0, default, default);
        _server.ThrowOnClientTick = true;

        int writesBefore = _server.WriteCalls;
        ServerBeam.TickForTests();

        // The handle was still polled, the throw was caught and named, and the next frame tries
        // the tick again rather than giving up on it.
        Assert.True(_server.WriteCalls > writesBefore);
        Assert.Contains(_log.Warnings, l => l.Contains("client tick threw", StringComparison.Ordinal));
        Assert.Equal(3, _server.ClientTicks);
        ServerBeam.TickForTests();
        Assert.Equal(4, _server.ClientTicks);
        handle.Dispose();
    }

    /// <summary>
    /// R28 finding 26 (EV16). <c>WorldSeq</c> caught to <c>0UL</c>, so on a client where
    /// <c>World.SequenceNumber</c> is not readable through the interop every world reported 0:
    /// the cached <c>EntityQuery</c> was never rebuilt, and after a world swap
    /// (character -> main menu -> character) <c>ToEntityArray</c> ran against a destroyed
    /// world, which is a native crash rather than a catchable exception. On the old code both
    /// sides of that comparison were 0 and the answer was always "unchanged"; an unreadable
    /// number is now "changed".
    /// </summary>
    [Fact]
    public void Client_query_is_rebuilt_when_the_world_sequence_cannot_be_read()
    {
        // Nothing cached: nothing to rebuild, whatever the numbers say.
        Assert.False(ClientBeamPoll.ShouldRebuild(built: false, cached: null, current: null));
        Assert.False(ClientBeamPoll.ShouldRebuild(built: false, cached: 4UL, current: 9UL));

        // The ordinary cases still behave.
        Assert.False(ClientBeamPoll.ShouldRebuild(built: true, cached: 7UL, current: 7UL));
        Assert.True(ClientBeamPoll.ShouldRebuild(built: true, cached: 7UL, current: 8UL));

        // Unreadable on either side is a change: one wasted query beats one dead world.
        Assert.True(ClientBeamPoll.ShouldRebuild(built: true, cached: null, current: null));
        Assert.True(ClientBeamPoll.ShouldRebuild(built: true, cached: null, current: 7UL));
        Assert.True(ClientBeamPoll.ShouldRebuild(built: true, cached: 7UL, current: null));

        // A world whose sequence number really is 0 is still just a world.
        Assert.False(ClientBeamPoll.ShouldRebuild(built: true, cached: 0UL, current: 0UL));
        Assert.True(ClientBeamPoll.ShouldRebuild(built: true, cached: 0UL, current: 1UL));
    }

    /// <summary>
    /// R28 finding 28.4 (EV16). Both client-query failure paths logged unconditionally and left
    /// <c>_lastBeamCount = -1</c>, which <see cref="ClientBeamPoll.ShouldEnumerate"/> reads as
    /// "always enumerate" — sixty warning lines a second for the rest of the session, from the
    /// same kind of log the networking threat model names as a DoS vector. A failure now
    /// reports <see cref="ClientBeamPoll.FailureBeamCount"/> so the idle poll applies, and the
    /// line is written once per failure episode.
    /// </summary>
    [Fact]
    public void A_failing_client_query_logs_once_and_falls_back_to_the_idle_poll()
    {
        var log = default(ClientBeamPoll.FailureLog);
        Assert.Equal("boom", log.Take("boom"));
        Assert.Null(log.Take("boom"));
        Assert.Null(log.Take("boom"));
        Assert.True(log.Logged);

        // A successful enumeration ends the episode, so a later failure is reported again.
        log.Clear();
        Assert.Equal("boom", log.Take("boom"));

        // The retry rate. -1 is what the failure paths used to leave behind: every frame.
        int idle = 0;
        int retriesWithOldValue = 0;
        for (int frame = 0; frame < ClientBeamPoll.IdleQueryEveryFrames * 2; frame++)
        {
            if (ClientBeamPoll.ShouldEnumerate(-1, ref idle)) retriesWithOldValue++;
        }

        Assert.Equal(ClientBeamPoll.IdleQueryEveryFrames * 2, retriesWithOldValue);

        idle = 0;
        int retries = 0;
        for (int frame = 0; frame < ClientBeamPoll.IdleQueryEveryFrames * 2; frame++)
        {
            if (ClientBeamPoll.ShouldEnumerate(ClientBeamPoll.FailureBeamCount, ref idle)) retries++;
        }

        Assert.Equal(2, retries);
    }

    /// <summary>
    /// R28 finding 28.9 (EV16). The tracker survived a client world loss with every entity key
    /// of the dead world still in it, so the first <c>EndTick</c> in the <em>next</em> world
    /// emitted "tracking ended" lines for beams that never existed there. Dropping the keys
    /// without end lines is the fix; the first tracker below is the old behavior.
    /// </summary>
    [Fact]
    public void A_client_world_loss_drops_tracked_beams_without_emitting_end_lines()
    {
        var ended = new List<string>();

        var stale = new ClientBeamTracker();
        stale.BeginTick();
        stale.Observe(ClientBeamTracker.KeyOf(12, 1), "12:1", "88:3", BeamTrackPath.Blackboard);
        stale.Observe(ClientBeamTracker.KeyOf(13, 1), "13:1", "89:3", BeamTrackPath.Blackboard);
        stale.BeginTick();
        stale.EndTick(ended);
        Assert.Equal(2, ended.Count);

        var tracker = new ClientBeamTracker();
        tracker.BeginTick();
        tracker.Observe(ClientBeamTracker.KeyOf(12, 1), "12:1", "88:3", BeamTrackPath.Blackboard);
        tracker.Observe(ClientBeamTracker.KeyOf(13, 1), "13:1", "89:3", BeamTrackPath.Blackboard);
        Assert.Equal(2, tracker.LiveCount);

        tracker.DropForWorldLoss();
        Assert.Equal(0, tracker.LiveCount);

        tracker.BeginTick();
        tracker.EndTick(ended);
        Assert.Empty(ended);

        // A beam in the new world is a first sighting again, not a resumption.
        tracker.BeginTick();
        Assert.NotNull(tracker.Observe(ClientBeamTracker.KeyOf(12, 1), "12:1", "88:3", BeamTrackPath.Blackboard));
    }

    /// <summary>
    /// R28 finding 28.5 (EV16). <c>IServerBeamHandle.OnDisposed</c> promises the callback runs
    /// "by <c>RemoveAll</c>, or by Core uninstall". R27 finding 4 stopped tracking disabled
    /// handles — the right call for an untracked <c>.beam</c> loop on a client — and the two
    /// fixes then contradicted each other: a client handle was in no list, so neither path
    /// could reach it and whatever the plugin tied to it leaked. On the old code both counters
    /// below stay 0 and <c>RemoveAll</c> returns 0.
    /// </summary>
    [Fact]
    public void A_disabled_handle_with_a_dispose_callback_is_still_swept()
    {
        var client = new FakeServerBeamRuntime { IsServer = false };
        ServerBeam.SetRuntimeForTests(client);

        int loose = 0;
        ServerBeam.Spawn(BeamPreset.Coil, default, new EffectVec3(1f, 0f, 0f), 8f, _owner);
        // R27 finding 4 still holds: a disabled handle nobody tied anything to is not tracked.
        Assert.Equal(0, ServerBeam.LiveCountForTests);

        var tied = ServerBeam.Spawn(BeamPreset.Coil, default, new EffectVec3(1f, 0f, 0f), 8f, _owner);
        Assert.False(tied.Enabled);
        tied.OnDisposed(() => loose++);
        Assert.Equal(1, ServerBeam.LiveCountForTests);

        Assert.Equal(1, ServerBeam.RemoveAll(_owner));
        Assert.Equal(1, loose);
        Assert.True(tied.IsDisposed);
        Assert.Equal(0, ServerBeam.LiveCountForTests);

        // Core uninstall reaches one that is still live, and neither path runs a callback twice.
        int uninstalled = 0;
        var second = ServerBeam.Spawn(BeamPreset.Coil, default, new EffectVec3(1f, 0f, 0f), 8f, _owner);
        second.OnDisposed(() => uninstalled++);
        ServerBeam.Uninstall();
        Assert.Equal(1, uninstalled);
        Assert.Equal(1, loose);
    }

    /// <summary>
    /// <b>R31 finding 21.</b> <c>TrackForCallbacks</c> got R30 finding 19's <c>_installed</c>
    /// check; <c>Track</c> — the <em>enabled</em> path every successful <c>Spawn</c> returns
    /// through — did not. A <c>ServerBeam.Spawn</c> after <c>Uninstall</c> (a plugin spawning a
    /// beam from its own unload path, or a queued callback firing after Core's module uninstall)
    /// created real coil entities, added the handle to a post-uninstall <c>Live</c>, and nothing
    /// swept it: <c>UnhookFrame</c> has already run so <c>Poll</c> never expires it, and
    /// <c>DescribePins</c> reported <c>handles=1</c> forever — which the reload pin diagnostic
    /// reads as a live pin holding the Core generation.
    ///
    /// <para>Driven from <em>inside</em> <c>Uninstall</c>, the window a worker thread would hit,
    /// so the ordering is deterministic rather than described. On the pre-fix code the counts
    /// below are 1 and stay 1 for the life of the process.</para>
    /// </summary>
    [Fact]
    public void An_enabled_spawn_from_inside_or_after_uninstall_cannot_resurrect_Live()
    {
        // The fixture's runtime is a server, so Spawn returns an ENABLED handle — the path
        // Track guards and the one R30 finding 19's fix did not reach.
        IServerBeamHandle? spawnedDuringSweep = null;
        var swept = ServerBeam.Spawn(BeamPreset.Coil, default, new EffectVec3(1f, 0f, 0f), 8f, _owner);
        Assert.True(swept.Enabled);
        Assert.Equal(1, ServerBeam.LiveCountForTests);

        // This callback runs from inside Uninstall's sweep — after Live.Clear() and
        // _installed = false — and spawns a real beam.
        swept.OnDisposed(() =>
        {
            spawnedDuringSweep = ServerBeam.Spawn(
                BeamPreset.Coil, default, new EffectVec3(2f, 0f, 0f), 8f, _owner);
        });

        ServerBeam.Uninstall();

        Assert.NotNull(spawnedDuringSweep);
        // The spawn really happened — the entities exist, so the leak was never hypothetical.
        Assert.True(spawnedDuringSweep!.Enabled);
        Assert.Equal(0, ServerBeam.LiveCountForTests);
        Assert.DoesNotContain("handles=", ServerBeam.DescribePins(), StringComparison.Ordinal);

        // A spawn after the sweep is refused the same way. Uninstall nulls the runtime, so put
        // one back — a still-reachable runtime with `_installed` false is the state a queued
        // callback firing after Core's module uninstall arrives in.
        ServerBeam.SetRuntimeForTests(_server);
        ServerBeam.SetInstalledForTests(false);
        var after = ServerBeam.Spawn(BeamPreset.Coil, default, new EffectVec3(3f, 0f, 0f), 8f, _owner);
        Assert.True(after.Enabled);
        Assert.Equal(0, ServerBeam.LiveCountForTests);
        Assert.DoesNotContain("handles=", ServerBeam.DescribePins(), StringComparison.Ordinal);

        // Refusing to track is not the same as dropping the handle: its owner can still dispose
        // it, and that still destroys the entities it made.
        int destroysBefore = _server.DestroyCalls;
        spawnedDuringSweep.Dispose();
        after.Dispose();
        Assert.True(_server.DestroyCalls > destroysBefore);
        Assert.Equal(0, ServerBeam.LiveCountForTests);
    }

    /// <summary>
    /// <b>R31 finding 17.</b> <c>MarkerSchemesOverlap</c> and <c>MarkerSchemeOverlapLog</c> were
    /// added for R30 finding 20 and were correct, but a repository-wide grep found no caller in
    /// <c>src/</c> or <c>samples/</c>: the only client-tick consumer called <c>MarkerBucket</c>
    /// and logged the scheme-1 line only, so a <c>BlastTime</c> both schemes claim resolved to
    /// <c>MarkerCoil</c> with <b>no line at all</b> — while <c>docs/effects.md</c> documented
    /// that line as something an operator sees. The behavioral half of R30 finding 20 was fixed
    /// and the diagnostic half was dead.
    ///
    /// <para>The classification the tick runs now lives in <see cref="ClientMarkerLog"/>, which
    /// is game-free, so this drives the production step itself rather than the helpers under it —
    /// the tick body is inside <c>#if !PROGENITOR_TESTS</c>.</para>
    /// </summary>
    [Fact]
    public void The_client_tick_announces_a_value_both_marker_schemes_claim()
    {
        var tick = default(ClientMarkerLog);

        // A value only scheme 2 claims: driven, and nothing to say.
        Assert.Equal(
            BeamPresets.MarkerCoil,
            tick.Classify(
                BeamPresets.MarkerCoil, out bool plain, out string? plainLegacy, out string? plainOverlap));
        Assert.False(plain);
        Assert.Null(plainLegacy);
        Assert.Null(plainOverlap);

        // 9001.2 is claimed by both schemes. It is still driven — that is the point of accepting
        // both families for one release — and the ambiguity is now announced.
        const float overlapping = 9001.2f;
        Assert.True(BeamPresets.MarkerSchemesOverlap(overlapping));
        Assert.Equal(
            BeamPresets.MarkerCoil,
            tick.Classify(overlapping, out bool legacy, out string? legacyLine, out string? overlapLine));
        Assert.False(legacy);
        Assert.Null(legacyLine);
        Assert.NotNull(overlapLine);
        Assert.Contains("both marker schemes claim", overlapLine!, StringComparison.Ordinal);
        Assert.Contains("scheme 2 won", overlapLine!, StringComparison.Ordinal);

        // Once per Core generation, not once per beam per frame.
        tick.Classify(overlapping, out _, out _, out string? again);
        Assert.Null(again);

        // An exact scheme-1 value is both legacy AND an overlap, and says both — once each.
        var fresh = default(ClientMarkerLog);
        fresh.Classify(
            BeamPresets.LegacyTintMarkerRed,
            out bool exactLegacy,
            out string? exactLegacyLine,
            out string? exactOverlapLine);
        Assert.True(exactLegacy);
        Assert.NotNull(exactLegacyLine);
        Assert.Contains("older Core", exactLegacyLine!, StringComparison.Ordinal);
        Assert.NotNull(exactOverlapLine);
        Assert.Contains("scheme 1 won", exactOverlapLine!, StringComparison.Ordinal);

        // A BlastTime that is not one of ours is skipped before anything is logged.
        var quiet = default(ClientMarkerLog);
        Assert.Equal(0f, quiet.Classify(1.25f, out _, out string? noLegacy, out string? noOverlap));
        Assert.Null(noLegacy);
        Assert.Null(noOverlap);

        // Reset is what a fresh Core generation gets.
        fresh.Reset();
        fresh.Classify(BeamPresets.LegacyTintMarkerRed, out _, out string? afterReset, out _);
        Assert.NotNull(afterReset);
    }

    /// <summary>
    /// <b>R30 finding 19.</b> <c>OnDisposed</c> has no <c>EnsureMainThread</c> and is documented
    /// as callable off-thread, and on a disabled (client-side) handle it calls
    /// <c>ServerBeam.TrackForCallbacks</c>, which took <c>Gate</c> and did <c>Live.Add</c> with
    /// no <c>_installed</c> check. So a registration landing between <c>Uninstall</c>'s
    /// <c>Live.Clear()</c> and the end of its sweep put the handle into a <b>post-uninstall</b>
    /// <c>Live</c>: it was not in the snapshot, so its callback never ran — the exact leak
    /// R28 28.5 added <c>TrackForCallbacks</c> to close — and <c>DescribePins</c> then reported
    /// <c>handles=1</c> forever, which the reload pin diagnostic reads as a real pin holding the
    /// Core generation.
    ///
    /// <para>The interleaving is driven through production code rather than described: the
    /// registration happens <em>inside</em> a dispose callback the sweep itself is running, which
    /// is the same window a worker thread would hit and is deterministic. Both orderings are
    /// covered — a registration during the sweep, and one after it.</para>
    /// </summary>
    [Fact]
    public void A_dispose_callback_registered_during_or_after_uninstall_cannot_resurrect_Live()
    {
        var client = new FakeServerBeamRuntime { IsServer = false };
        ServerBeam.SetRuntimeForTests(client);

        int duringRan = 0;
        int sweptRan = 0;
        var during = ServerBeam.Spawn(BeamPreset.Coil, default, new EffectVec3(1f, 0f, 0f), 8f, _owner);
        var swept = ServerBeam.Spawn(BeamPreset.Coil, default, new EffectVec3(1f, 0f, 0f), 8f, _owner);
        Assert.False(during.Enabled);
        Assert.False(swept.Enabled);

        // `swept` is in Live; its callback runs from inside Uninstall's sweep and registers on
        // `during`, which is not in the snapshot.
        swept.OnDisposed(() =>
        {
            sweptRan++;
            during.OnDisposed(() => duringRan++);
        });
        Assert.Equal(1, ServerBeam.LiveCountForTests);

        ServerBeam.Uninstall();
        Assert.Equal(1, sweptRan);

        // The registration during the sweep did NOT go into Live. On the pre-R30 code this is 1
        // and stays 1 for the life of the process.
        Assert.Equal(0, ServerBeam.LiveCountForTests);
        Assert.DoesNotContain("handles=", ServerBeam.DescribePins(), StringComparison.Ordinal);

        // A registration after uninstall is refused the same way...
        int afterRan = 0;
        var after = ServerBeam.Spawn(BeamPreset.Coil, default, new EffectVec3(1f, 0f, 0f), 8f, _owner);
        after.OnDisposed(() => afterRan++);
        Assert.Equal(0, ServerBeam.LiveCountForTests);

        // ...and refusing to track is not the same as dropping the callback: the handle keeps
        // its own list and its own Dispose still runs it.
        after.Dispose();
        Assert.Equal(1, afterRan);
        during.Dispose();
        Assert.Equal(1, duringRan);
        Assert.Equal(0, ServerBeam.LiveCountForTests);
    }

    /// <summary>
    /// R28 finding 28.6 (EV16) and R29 finding 15 (EV17), which are the same three lines.
    ///
    /// <para>28.6: <c>Dispose</c> ran <c>EnsureMainThread</c> <b>before</b> the
    /// <c>Interlocked.Exchange</c>, so an off-main-thread dispose threw with the handle still
    /// un-latched. The latch is first, and it stays first.</para>
    ///
    /// <para>15: EV16's fix then ran <c>ServerBeam.Forget</c> and the <c>OnDisposed</c>
    /// callbacks in a <c>finally</c> — reached because the guard threw <em>past</em> the destroy
    /// loop. So the handle was latched, removed from <c>Live</c> and reported disposed while its
    /// replicated coil / <c>NetworkedSequence</c> entities were still in the world, owned by
    /// nothing and unreachable from <c>RemoveAll</c> or <c>Uninstall</c>. The ECS work is now
    /// posted to the main thread and the bookkeeping follows it there. On the EV16 code
    /// <c>DestroyCalls</c> below is 0 after the drain, <c>calls</c> is 1 before it, and
    /// <c>LiveCountForTests</c> is 0 before it.</para>
    /// </summary>
    [Fact]
    public void An_off_main_thread_dispose_destroys_its_entities_on_the_main_thread()
    {
        int calls = 0;
        var handle = ServerBeam.Spawn(BeamPreset.Coil, default, new EffectVec3(1f, 0f, 0f), 8f, _owner);
        Assert.True(handle.Enabled);
        handle.OnDisposed(() => calls++);
        Assert.Equal(1, ServerBeam.LiveCountForTests);

        ServerBeam.FailMainThreadCheckForTests = true;
        try
        {
            // R28 28.6: the caller is still told.
            Assert.Throws<InvalidOperationException>(() => handle.Dispose());

            // Latched immediately, so a racing second Dispose is a no-op and the handle stops
            // being polled.
            Assert.True(handle.IsDisposed);
            handle.Dispose();

            // R29 15: nothing has happened to the world yet, and — the part that matters — the
            // handle is still in Live, so RemoveAll and Uninstall can still reach it. The
            // plugin has NOT been told its beam is gone.
            Assert.Equal(0, _server.DestroyCalls);
            Assert.Equal(0, calls);
            Assert.Equal(1, ServerBeam.LiveCountForTests);
            Assert.Contains(
                _log.Warnings,
                l => l.Contains("Dispose ran off the game main thread", StringComparison.Ordinal));
        }
        finally
        {
            ServerBeam.FailMainThreadCheckForTests = false;
        }

        // The posted work runs on the next frame: entities destroyed, then forget, then the
        // callbacks — on the main thread, which is where OnDisposed's contract now puts them.
        MainThread.Drain();
        Assert.Equal(1, _server.DestroyCalls);
        Assert.Equal(1, calls);
        Assert.Equal(0, ServerBeam.LiveCountForTests);

        // Still exactly once: a later dispose on the main thread is the ordinary no-op.
        handle.Dispose();
        MainThread.Drain();
        Assert.Equal(1, calls);
        Assert.Equal(1, _server.DestroyCalls);
    }

    /// <summary>
    /// R29 finding 15, the recovery path. A handle whose off-thread <c>Dispose</c> has latched
    /// but not completed is still in <c>Live</c>, so <c>RemoveAll</c> — which runs on the main
    /// thread — finishes the destroy itself rather than returning a misleading no-op. That is
    /// the guarantee EV16 removed: on that code the handle is gone from <c>Live</c> and its
    /// entities are never destroyed by anything.
    /// </summary>
    [Fact]
    public void A_latched_but_unfinished_dispose_is_still_finished_by_RemoveAll()
    {
        int calls = 0;
        var handle = ServerBeam.Spawn(BeamPreset.Coil, default, new EffectVec3(1f, 0f, 0f), 8f, _owner);
        handle.OnDisposed(() => calls++);

        ServerBeam.FailMainThreadCheckForTests = true;
        try { Assert.Throws<InvalidOperationException>(() => handle.Dispose()); }
        finally { ServerBeam.FailMainThreadCheckForTests = false; }

        Assert.Equal(0, _server.DestroyCalls);
        Assert.Equal(1, ServerBeam.RemoveAll(_owner));
        Assert.Equal(1, _server.DestroyCalls);
        Assert.Equal(1, calls);
        Assert.Equal(0, ServerBeam.LiveCountForTests);

        // The posted copy is idempotent, so the drain neither destroys twice nor re-fires.
        MainThread.Drain();
        Assert.Equal(1, _server.DestroyCalls);
        Assert.Equal(1, calls);
    }

    /// <summary>
    /// R29 finding 15's related note. <c>OnDisposed</c> registered <em>during</em> the window
    /// between an off-thread latch and the main-thread completion must be drained with the rest
    /// — on the main thread, after the destroy — not run inline on the registering thread ahead
    /// of the entities it is meant to clean up after.
    /// </summary>
    [Fact]
    public void A_callback_registered_during_the_dispose_window_runs_on_the_main_thread()
    {
        var handle = ServerBeam.Spawn(BeamPreset.Coil, default, new EffectVec3(1f, 0f, 0f), 8f, _owner);

        ServerBeam.FailMainThreadCheckForTests = true;
        try { Assert.Throws<InvalidOperationException>(() => handle.Dispose()); }
        finally { ServerBeam.FailMainThreadCheckForTests = false; }

        int destroyedWhenCalled = -1;
        handle.OnDisposed(() => destroyedWhenCalled = _server.DestroyCalls);
        Assert.Equal(-1, destroyedWhenCalled);

        MainThread.Drain();
        Assert.Equal(1, destroyedWhenCalled);

        // After completion, registering runs the callback immediately — the documented
        // exception, and the only one.
        int late = 0;
        handle.OnDisposed(() => late++);
        Assert.Equal(1, late);
    }

    /// <summary>
    /// R28 finding 28.8 (EV16). One <c>_clampLogged</c> flag was shared by the coil clamp and
    /// the link clamp, so whichever fired first silenced the other for the life of the Core
    /// generation — and the link line is the one that names a 600-second cap on a replicated,
    /// proximity-ungated entity. On the old code the second assertion below finds no line.
    /// </summary>
    [Fact]
    public void The_coil_clamp_and_the_link_clamp_log_independently()
    {
        ServerBeam.Spawn(BeamPreset.Coil, default, new EffectVec3(1f, 0f, 0f), 999f, _owner);
        Assert.Contains(_log.Infos, l => l.Contains("server beam seconds clamped to 19", StringComparison.Ordinal));

        ServerBeam.Link(11, 1, 22, 1, 999999999f, owner: _owner);
        Assert.Contains(_log.Infos, l => l.Contains("seconds clamped to 600", StringComparison.Ordinal));

        // Each is still once per generation.
        ServerBeam.Spawn(BeamPreset.Coil, default, new EffectVec3(1f, 0f, 0f), 999f, _owner);
        ServerBeam.Link(11, 1, 22, 1, 999999999f, owner: _owner);
        Assert.Equal(1, CountInfoContaining("server beam seconds clamped to 19"));
        Assert.Equal(1, CountInfoContaining("seconds clamped to 600"));
    }

    [Fact]
    public void Client_tracker_logs_once_per_beam_then_once_on_dispose()
    {
        var tracker = new ClientBeamTracker();
        var ended = new List<string>();
        long key = ClientBeamTracker.KeyOf(12, 1);

        tracker.BeginTick();
        string? first = tracker.Observe(key, "12:1", "88:3", BeamTrackPath.Blackboard);
        tracker.EndTick(ended);
        Assert.Equal(
            "Effects: server beam client tracking entity=12:1 sequence=88:3 path=blackboard",
            first);
        Assert.Empty(ended);
        Assert.Equal(1, tracker.LiveCount);

        // Same beam, same sequence, same path: silent for every later frame.
        for (int i = 0; i < 5; i++)
        {
            tracker.BeginTick();
            Assert.Null(tracker.Observe(key, "12:1", "88:3", BeamTrackPath.Blackboard));
            tracker.EndTick(ended);
            Assert.Empty(ended);
        }

        // Beam gone from the query: one end line carrying the write count, then forgotten.
        tracker.BeginTick();
        tracker.EndTick(ended);
        Assert.Equal(
            new[] { "Effects: server beam client tracking ended entity=12:1 sequence=88:3 writes=6" },
            ended);
        Assert.Equal(0, tracker.LiveCount);
    }

    [Fact]
    public void Client_tracker_relogs_when_the_sequence_entity_or_path_changes()
    {
        var tracker = new ClientBeamTracker();
        var ended = new List<string>();
        long key = ClientBeamTracker.KeyOf(12, 1);

        tracker.BeginTick();
        Assert.NotNull(tracker.Observe(key, "12:1", "88:3", BeamTrackPath.Blackboard));
        tracker.EndTick(ended);

        // Preview -> Blast respawns the sequence entity on the client.
        tracker.BeginTick();
        Assert.Equal(
            "Effects: server beam client tracking entity=12:1 sequence=91:4 path=blackboard",
            tracker.Observe(key, "12:1", "91:4", BeamTrackPath.Blackboard));
        tracker.EndTick(ended);
        Assert.Empty(ended);

        tracker.BeginTick();
        Assert.Equal(
            "Effects: server beam client tracking entity=12:1 sequence=91:4 path=blackboard+buffer",
            tracker.Observe(key, "12:1", "91:4", BeamTrackPath.Blackboard | BeamTrackPath.Buffer));
        tracker.EndTick(ended);
        Assert.Equal(1, tracker.LiveCount);
    }

    [Fact]
    public void Client_tracker_says_so_when_no_channel_can_repoint_the_sequence()
    {
        var tracker = new ClientBeamTracker();
        string? line = tracker.Observe(ClientBeamTracker.KeyOf(3, 2), "3:2", "7:1", BeamTrackPath.None);
        Assert.NotNull(line);
        Assert.Contains("path=none", line);
        Assert.Contains("use ServerBeam.Link", line);
        Assert.Equal("none", ClientBeamTracker.PathName(BeamTrackPath.None));
        Assert.Equal("buffer", ClientBeamTracker.PathName(BeamTrackPath.Buffer));
        Assert.Equal("hybrid", ClientBeamTracker.PathName(BeamTrackPath.Hybrid));
    }

    /// <summary>
    /// EV17. <c>writes</c> counted every tick a beam was <em>seen</em>, not every write, so a
    /// live client that could not re-point at all ended with
    /// <c>path=none … writes=1341</c> — which reads as 1341 successful writes and was the exact
    /// opposite of the truth. On the old code the first `writes=0` below is `writes=200`.
    /// </summary>
    [Fact]
    public void A_beam_that_was_never_repointed_reports_zero_writes()
    {
        var tracker = new ClientBeamTracker();
        var ended = new List<string>();
        long none = ClientBeamTracker.KeyOf(3, 2);
        long driven = ClientBeamTracker.KeyOf(4, 2);

        for (int frame = 0; frame < 200; frame++)
        {
            tracker.BeginTick();
            tracker.Observe(none, "3:2", "7:1", BeamTrackPath.None);
            tracker.Observe(driven, "4:2", "8:1", BeamTrackPath.Blackboard);
            tracker.EndTick(ended);
            Assert.Empty(ended);
        }

        tracker.BeginTick();
        tracker.EndTick(ended);
        Assert.Contains(
            "Effects: server beam client tracking ended entity=3:2 sequence=7:1 writes=0", ended);
        Assert.Contains(
            "Effects: server beam client tracking ended entity=4:2 sequence=8:1 writes=200", ended);

        // A beam that starts as none and later resolves counts from the frame it started
        // writing, not from the frame it was first seen.
        var late = new ClientBeamTracker();
        late.BeginTick();
        late.Observe(none, "3:2", "7:1", BeamTrackPath.None);
        late.EndTick(ended);
        late.BeginTick();
        Assert.NotNull(late.Observe(none, "3:2", "7:1", BeamTrackPath.Blackboard));
        late.EndTick(ended);
        late.BeginTick();
        late.Observe(none, "3:2", "7:1", BeamTrackPath.Blackboard);
        late.EndTick(ended);
        late.BeginTick();
        late.EndTick(ended);
        Assert.Contains(
            "Effects: server beam client tracking ended entity=3:2 sequence=7:1 writes=2", ended);
    }

    [Fact]
    public void Client_tracker_tracks_each_beam_separately_and_is_bounded()
    {
        var tracker = new ClientBeamTracker();
        var ended = new List<string>();
        tracker.BeginTick();
        for (int i = 1; i <= 3; i++)
            Assert.NotNull(tracker.Observe(ClientBeamTracker.KeyOf(i, 1), i + ":1", "90:1", BeamTrackPath.Blackboard));
        tracker.EndTick(ended);
        Assert.Equal(3, tracker.LiveCount);

        // Only the beam that disappeared is reported.
        tracker.BeginTick();
        tracker.Observe(ClientBeamTracker.KeyOf(1, 1), "1:1", "90:1", BeamTrackPath.Blackboard);
        tracker.Observe(ClientBeamTracker.KeyOf(3, 1), "3:1", "90:1", BeamTrackPath.Blackboard);
        tracker.EndTick(ended);
        Assert.Single(ended);
        Assert.Contains("entity=2:1", ended[0]);
        Assert.Equal(2, tracker.LiveCount);

        tracker.Clear();
        Assert.Equal(0, tracker.LiveCount);
    }

    [Fact]
    public void Client_tracker_key_round_trips_negative_entity_versions()
    {
        long key = ClientBeamTracker.KeyOf(99603, 2452);
        Assert.Equal("99603:2452", ClientBeamTracker.FormatKey(key));
        Assert.NotEqual(ClientBeamTracker.KeyOf(99603, 2452), ClientBeamTracker.KeyOf(99603, 2453));
    }

    [Fact]
    public void Link_uses_the_default_sequence_when_the_caller_passes_zero()
    {
        var handle = ServerBeam.Link(10, 1, 20, 1, 8f, 0, _owner, "example.beam.test");
        Assert.True(handle.Enabled);
        Assert.Equal(BeamPreset.Link, handle.Preset);
        Assert.True(handle.TracksEntitiesNatively);
        Assert.Equal(BeamPresets.DefaultLinkSequence, _server.LastLinkSequenceGuid);
        Assert.Contains(
            "sequence=" + BeamPresets.DefaultLinkSequence + " (Sequence)AB_Charm_Channeling",
            string.Join("\n", _log.Infos));
        handle.Dispose();
    }

    [Fact]
    public void Link_keeps_an_explicit_sequence_guid()
    {
        var handle = ServerBeam.Link(10, 1, 20, 1, 8f, BeamPresets.LinkSequenceSubdueChanneling, _owner, "example.beam.test");
        Assert.True(handle.Enabled);
        Assert.Equal(BeamPresets.LinkSequenceSubdueChanneling, _server.LastLinkSequenceGuid);
        Assert.Equal("(Sequence)AB_Subdue_Channeling", BeamPresets.LinkSequenceName(BeamPresets.LinkSequenceSubdueChanneling));
        Assert.Equal("1234", BeamPresets.LinkSequenceName(1234));
        handle.Dispose();
    }

    [Fact]
    public void Mark_targets_one_entity_with_the_placement_sequence()
    {
        var handle = ServerBeam.Mark(10, 1, 8f, 0, _owner, "example.beam.test");
        Assert.True(handle.Enabled);
        Assert.True(handle.TracksEntitiesNatively);
        Assert.Equal(BeamPresets.MarkSequenceRed, _server.LastLinkSequenceGuid);
        Assert.Equal((10, 0), _server.LastLinkEntities);
        Assert.True(_server.LastLinkPlayOnSelf);
        string log = string.Join("\n", _log.Infos);
        Assert.Contains("Effects: server beam mark spawned prefab=NetworkedSequence", log);
        Assert.Contains("target=10:1 playOnSelf=true", log);
        Assert.Contains("(invalid/red)", log);
        handle.Dispose();
    }

    [Fact]
    public void Mark_takes_the_blue_placement_variant()
    {
        var handle = ServerBeam.Mark(10, 1, 8f, BeamPresets.MarkSequenceBlue, _owner, "example.beam.test");
        Assert.Equal(BeamPresets.MarkSequenceBlue, _server.LastLinkSequenceGuid);
        Assert.Equal(BeamPresets.MarkSequenceBlue, BeamPresets.MarkSequenceFor(BeamPreset.Blue));
        Assert.Equal(BeamPresets.MarkSequenceRed, BeamPresets.MarkSequenceFor(BeamPreset.CastleRed));
        Assert.Contains("(valid/blue)", string.Join("\n", _log.Infos));
        handle.Dispose();
    }

    [Theory]
    [InlineData(0f, 1)]
    [InlineData(11.99f, 1)]
    [InlineData(12f, 2)]
    [InlineData(19.99f, 2)]
    [InlineData(20f, 3)]
    [InlineData(23.49f, 3)]
    [InlineData(23.5f, 4)]
    [InlineData(200f, 4)]
    public void Coil_length_buckets_match_the_native_GetSequence_cuts(float metres, int bucket) =>
        Assert.Equal(bucket, BeamPresets.LengthBucket(metres));

    /// <summary>
    /// R27 finding 4. On a client every <c>ServerBeam</c> call takes the disabled path, and a
    /// disabled handle has <c>NeedsTick == false</c>: it was never polled, never timed out and
    /// never left <c>Live</c>, so a client running <c>.beam</c> in a loop grew the static list
    /// without bound and <c>DescribePins</c> reported a climbing <c>handles=N</c> nothing could
    /// sweep. Before EV15 each of the three calls below added one entry.
    /// </summary>
    [Fact]
    public void Client_calls_track_nothing_and_log_the_refusal_once()
    {
        var client = new FakeServerBeamRuntime { IsServer = false };
        client.Add("ProfessorCoilBeam");
        ServerBeam.SetRuntimeForTests(client);

        var spawn = ServerBeam.Spawn(BeamPreset.CastleRed, default, new EffectVec3(1f, 0f, 0f), 8f, _owner);
        var link = ServerBeam.Link(11, 1, 22, 1, 8f, owner: _owner);
        var mark = ServerBeam.Mark(11, 1, 8f, owner: _owner);

        Assert.False(spawn.Enabled);
        Assert.False(link.Enabled);
        Assert.False(mark.Enabled);
        Assert.Equal(0, ServerBeam.LiveCountForTests);
        Assert.Equal(0, client.SpawnCalls);
        Assert.Equal(0, client.LinkCalls);
        Assert.Equal(1, CountInfos(ServerBeam.DisabledNotTrackedLog));

        // Nothing to sweep, and the sweep says so rather than reporting a phantom handle.
        Assert.Equal(0, ServerBeam.RemoveAll(_owner));
        Assert.DoesNotContain(_log.Infos, l => l.StartsWith("Effects: swept ", StringComparison.Ordinal));
    }

    /// <summary>
    /// R27 findings 4 and 25: the sweep must reach a handle created through the <b>static</b>
    /// API, which is what pins the plugin ALC, and report how many it dropped.
    /// </summary>
    [Fact]
    public void Static_api_handle_is_released_by_RemoveAll_and_reports_its_count()
    {
        ServerBeam.Spawn(BeamPreset.Coil, default, new EffectVec3(1f, 0f, 0f), 8f, _owner);
        ServerBeam.Spawn(BeamPreset.Coil, default, new EffectVec3(2f, 0f, 0f), 8f, _owner);
        Assert.Equal(2, ServerBeam.LiveCountForTests);
        Assert.Equal(2, ServerBeam.RemoveAll(_owner));
        Assert.Equal(0, ServerBeam.LiveCountForTests);
        Assert.Equal(0, ServerBeam.RemoveAll(_owner));
    }

    /// <summary>
    /// R27 finding 16 + R28 finding 28.7 (EV16). <c>BlastTime</c> crosses the coil snapshot, so
    /// an exact <c>blastTime == 9001f</c> is a coin toss against whatever quantization the wire
    /// applies — and a miss was silent. EV15 answered that with a 0.5 band around markers
    /// <b>one apart</b>, which is a statement about the markers and not about the wire: a step
    /// coarser than 0.5 still missed everything and a step coarser than 1.0 made the three
    /// markers indistinguishable at any tolerance. The markers are now 100 apart, so every
    /// offset below — far larger than any step the old band could survive — still buckets to
    /// the marker it was written as.
    /// </summary>
    [Theory]
    [InlineData(9000f, 9000f)]
    [InlineData(9000.0012f, 9000f)]
    [InlineData(8970f, 9000f)]
    [InlineData(9030f, 9000f)]
    [InlineData(9100f, 9100f)]
    [InlineData(9065f, 9100f)]
    [InlineData(9138f, 9100f)]
    [InlineData(9200f, 9200f)]
    [InlineData(9232.5f, 9200f)]
    public void Marker_matching_tolerates_wire_quantisation(float wire, float bucket)
    {
        Assert.True(BeamPresets.IsProgenitorMarker(wire));
        Assert.Equal(bucket, BeamPresets.MarkerBucket(wire));
        Assert.Equal(bucket != BeamPresets.MarkerCoil, BeamPresets.IsTintMarker(wire));
    }

    /// <summary>
    /// R28 finding 28.7. The spacing has to leave a gap the tolerance does not cover, otherwise
    /// widening the band just turns "misses every marker" into "cannot tell them apart".
    /// 9050 is exactly between two markers and belongs to neither.
    /// </summary>
    [Theory]
    [InlineData(0f)]
    [InlineData(3f)]
    [InlineData(8950f)]
    [InlineData(9050f)]
    [InlineData(9150f)]
    [InlineData(9250f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void Marker_band_still_refuses_a_vanilla_blast_duration(float wire)
    {
        Assert.False(BeamPresets.IsProgenitorMarker(wire));
        Assert.Equal(0f, BeamPresets.MarkerBucket(wire));
        Assert.False(BeamPresets.IsTintMarker(wire));
        Assert.Null(BeamPresets.TintForMarker(wire));
    }

    /// <summary>
    /// R28 finding 28.7. Two properties the old 1-apart markers could not have: the tolerance
    /// is under half the spacing (no value is ambiguous), and each marker survives a wire step
    /// that would have swallowed the entire old band.
    /// </summary>
    [Fact]
    public void Markers_are_spaced_further_apart_than_the_tolerance_they_are_matched_with()
    {
        Assert.True(BeamPresets.MarkerTolerance * 2f < BeamPresets.MarkerSpacing);
        Assert.Equal(BeamPresets.MarkerSpacing, BeamPresets.TintMarkerRed - BeamPresets.MarkerCoil);
        Assert.Equal(BeamPresets.MarkerSpacing, BeamPresets.TintMarkerBlue - BeamPresets.TintMarkerRed);

        // A wire step of 30 — three thousand times the measured one, and enough to erase the
        // EV15 band entirely — leaves every marker unambiguous. (±1 is not usable here any
        // more: 9001 and 9002 are the scheme-1 markers this build still reads, see
        // A_scheme_1_marker_is_still_read_and_reported_once.)
        foreach (float marker in new[] { BeamPresets.MarkerCoil, BeamPresets.TintMarkerRed, BeamPresets.TintMarkerBlue })
        {
            Assert.Equal(marker, BeamPresets.MarkerBucket(marker + 30f));
            Assert.Equal(marker, BeamPresets.MarkerBucket(marker - 30f));
        }
    }

    /// <summary>
    /// R29 finding 16 (EV17), the half that was answerable from the game's metadata rather than guessed at.
    /// R28 28.7 and R29 16 both turn on "what is <c>BlastTime</c>'s quantization step", and both
    /// reviews recorded it as unknown. It is <b>0.01</b>:
    /// <c>[SyncRangedFloat(0, 10000, 2, 1, …)]</c> binds
    /// <c>SyncRangedFloat.ctor(min, max, <b>decimals</b>, overflowHandling, …)</c>
    /// in the server metadata, and the native
    /// <c>NetworkSync.WriteRangedFloat</c> / <c>ReadRangedFloat</c>
    /// are a fixed-point integer over
    /// <c>[min*10^d … max*10^d]</c>.
    ///
    /// <para><b>R30, "Test quality".</b> The review's judgment was fair: every assertion here
    /// was arithmetic over compile-time constants (<c>0.01f == BlastTimeWireStep</c>,
    /// <c>(int)(9000f*100)/100f == 9000f</c>, <c>40 &gt; 1</c>) and no production path was
    /// entered, so it would have passed against any decoder. It puts each marker <b>through the
    /// wire encoding and then through <c>MarkerBucket</c></b> now, which is the claim the name
    /// makes: a marker survives the wire <em>as that marker</em>. A decoder that mis-buckets, a
    /// marker moved off an exact integer, or a band narrowed below the encoder's error all fail
    /// it.</para>
    /// </summary>
    [Fact]
    public void The_blast_time_wire_step_is_measured_not_assumed()
    {
        Assert.Equal(0.01f, BeamPresets.BlastTimeWireStep);

        foreach (var (marker, bucket, legacyExpected) in new[]
                 {
                     (BeamPresets.MarkerCoil, BeamPresets.MarkerCoil, false),
                     (BeamPresets.TintMarkerRed, BeamPresets.TintMarkerRed, false),
                     (BeamPresets.TintMarkerBlue, BeamPresets.TintMarkerBlue, false),
                     (BeamPresets.LegacyTintMarkerRed, BeamPresets.TintMarkerRed, true),
                     (BeamPresets.LegacyTintMarkerBlue, BeamPresets.TintMarkerBlue, true),
                 })
        {
            // Inside the encoded range, so OverflowHandling never clamps two markers onto one.
            Assert.InRange(marker, 0f, BeamPresets.BlastTimeMax);

            // The encoder, from the native WriteRangedFloat: v = (int)(scale * value),
            // read back as v / scale. Run every marker through it...
            float wire = WireRoundTrip(marker);
            Assert.Equal(marker, wire);

            // ...and then through the decoder the client actually runs. This is the assertion
            // the old version of this test was missing entirely.
            Assert.Equal(bucket, BeamPresets.MarkerBucket(wire, out bool legacy));
            Assert.Equal(legacyExpected, legacy);
            Assert.True(BeamPresets.IsProgenitorMarker(wire));
        }

        // One wire step off a marker still decodes to it: the bands are what absorb the encoder,
        // and the encoder's error here is zero, so this holds by a wide margin.
        Assert.Equal(
            BeamPresets.MarkerCoil,
            BeamPresets.MarkerBucket(WireRoundTrip(BeamPresets.MarkerCoil + BeamPresets.BlastTimeWireStep)));
        Assert.Equal(
            BeamPresets.TintMarkerBlue,
            BeamPresets.MarkerBucket(WireRoundTrip(BeamPresets.TintMarkerBlue - BeamPresets.BlastTimeWireStep)));

        // And a vanilla blast duration is still not ours after the round trip.
        Assert.Equal(0f, BeamPresets.MarkerBucket(WireRoundTrip(2.5f)));
        Assert.Equal(0f, BeamPresets.MarkerBucket(WireRoundTrip(8888f)));
    }

    /// <summary>
    /// <c>BlastTime</c>'s wire encoding, transcribed from the native
    /// <c>NetworkSync.WriteRangedFloat</c> / <c>ReadRangedFloat</c>: a fixed-point integer with
    /// <c>decimals = 2</c> over <c>[0, 10000]</c>, truncating.
    /// </summary>
    private static float WireRoundTrip(float value)
    {
        const int scale = 100;
        int lo = 0;
        int hi = (int)MathF.Round(scale * BeamPresets.BlastTimeMax);
        int v = Math.Clamp((int)(scale * value), lo, hi);
        return v / (float)scale;
    }

    /// <summary>
    /// R29 finding 16 (EV17). EV16 moved <c>TintMarkerRed</c> 9001 → 9100 and
    /// <c>TintMarkerBlue</c> 9002 → 9200 with nothing on the wire or in the handshake naming the
    /// change. Against a server on the previous Core, 9001 and 9002 both fall inside scheme 2's
    /// ±40 band around <see cref="BeamPresets.MarkerCoil"/>, so every tinted beam bucketed to
    /// the <em>untinted</em> coil marker and the only symptom was tints that stopped appearing
    /// — the "0 matched markers" line does not fire while other beams match.
    ///
    /// <para>On the EV16 code the first two assertions return <c>MarkerCoil</c>.</para>
    /// </summary>
    [Fact]
    public void A_scheme_1_marker_is_still_read_and_reported_once()
    {
        Assert.Equal(2, BeamPresets.MarkerSchemeVersion);

        Assert.Equal(BeamPresets.TintMarkerRed, BeamPresets.MarkerBucket(BeamPresets.LegacyTintMarkerRed, out bool redLegacy));
        Assert.True(redLegacy);
        Assert.Equal(BeamPresets.TintMarkerBlue, BeamPresets.MarkerBucket(BeamPresets.LegacyTintMarkerBlue, out bool blueLegacy));
        Assert.True(blueLegacy);

        // Everything downstream of the bucket is scheme-agnostic: an old server's beam is
        // tinted, tracked and reported exactly like a new one's.
        Assert.True(BeamPresets.IsTintMarker(BeamPresets.LegacyTintMarkerRed));
        Assert.True(BeamPresets.IsProgenitorMarker(BeamPresets.LegacyTintMarkerBlue));
        Assert.Equal(
            BeamPresets.TintForMarker(BeamPresets.TintMarkerRed),
            BeamPresets.TintForMarker(BeamPresets.LegacyTintMarkerRed));
        Assert.Equal(
            BeamPresets.TintForMarker(BeamPresets.TintMarkerBlue),
            BeamPresets.TintForMarker(BeamPresets.LegacyTintMarkerBlue));

        // 9000 is the same marker in both schemes, so it is never reported as legacy.
        Assert.Equal(BeamPresets.MarkerCoil, BeamPresets.MarkerBucket(BeamPresets.MarkerCoil, out bool coilLegacy));
        Assert.False(coilLegacy);
        Assert.Equal(BeamPresets.TintMarkerRed, BeamPresets.MarkerBucket(BeamPresets.TintMarkerRed, out bool newLegacy));
        Assert.False(newLegacy);

        // And a mismatched pair is named rather than left to be inferred from missing tints.
        string line = BeamPresets.LegacyMarkerLog(BeamPresets.LegacyTintMarkerRed);
        Assert.Contains("marker-scheme-1", line, StringComparison.Ordinal);
        Assert.Contains("9001", line, StringComparison.Ordinal);
        Assert.Contains("older Core", line, StringComparison.Ordinal);
        Assert.Contains("9000/9100/9200", line, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>R30 finding 20.</b> The two schemes overlap: every value in
    /// <c>[9000.6, 9001.4] ∪ [9001.6, 9002.4]</c> is inside a scheme-1 band <em>and</em> inside
    /// scheme 2's ±40 band around <c>MarkerCoil</c>, because
    /// <see cref="BeamPresets.MarkerTolerance"/> is 4000× the measured wire step. EV17 resolved
    /// the whole overlap to scheme 1 unconditionally, so a value a scheme-2 server wrote as an
    /// untinted coil marker came back as <c>TintMarkerRed</c> / <c>TintMarkerBlue</c>, the beam
    /// was tinted, and <c>LegacyMarkerLog</c> fired a permanent and false "this server runs an
    /// older Core" warning. The class comment's "no value can be ambiguous between two markers"
    /// was true only within scheme 2.
    ///
    /// <para>The order is by <b>exactness</b> now, not by scheme: scheme 2's exact set first,
    /// then scheme 1's two exact values, then scheme 2's band. Since the wire step is 0.01 with
    /// no rounding (see <see cref="The_blast_time_wire_step_is_measured_not_assumed"/>), an old
    /// server's 9001 arrives as exactly 9001 and nothing else in the band is a scheme-1
    /// marker.</para>
    /// </summary>
    [Fact]
    public void An_overlapping_marker_value_resolves_by_exactness_and_says_which_scheme_won()
    {
        // 9001.2 is claimed by both schemes: inside scheme 1's ±0.4 around 9001 and inside
        // scheme 2's ±40 around 9000.
        const float overlapping = 9001.2f;
        Assert.True(BeamPresets.MarkerSchemesOverlap(overlapping));
        Assert.True(BeamPresets.MarkerSchemesOverlap(BeamPresets.LegacyTintMarkerRed));
        Assert.True(BeamPresets.MarkerSchemesOverlap(BeamPresets.LegacyTintMarkerBlue));
        Assert.False(BeamPresets.MarkerSchemesOverlap(BeamPresets.TintMarkerRed));
        Assert.False(BeamPresets.MarkerSchemesOverlap(9001.5f));
        Assert.False(BeamPresets.MarkerSchemesOverlap(float.NaN));

        // Scheme 2 wins it, because it is not an exact scheme-1 value. On the EV17 code this is
        // TintMarkerRed with legacy = true.
        Assert.Equal(BeamPresets.MarkerCoil, BeamPresets.MarkerBucket(overlapping, out bool legacy));
        Assert.False(legacy);
        Assert.False(BeamPresets.IsTintMarker(overlapping));

        // The exact legacy values still resolve to their tints and still report legacy.
        Assert.Equal(
            BeamPresets.TintMarkerRed,
            BeamPresets.MarkerBucket(BeamPresets.LegacyTintMarkerRed, out bool exactLegacy));
        Assert.True(exactLegacy);

        // An exact scheme-2 marker is never claimed by a legacy band, whichever way the bands
        // are drawn — it is decided before either band is consulted.
        foreach (float m in new[]
                 {
                     BeamPresets.MarkerCoil, BeamPresets.TintMarkerRed, BeamPresets.TintMarkerBlue,
                 })
        {
            Assert.Equal(m, BeamPresets.MarkerBucket(m, out bool l));
            Assert.False(l);
        }

        // And the decision is said out loud, naming the winner, for either side of the overlap.
        string scheme2 = BeamPresets.MarkerSchemeOverlapLog(overlapping);
        Assert.Contains("both marker schemes claim", scheme2, StringComparison.Ordinal);
        Assert.Contains("scheme 2 won", scheme2, StringComparison.Ordinal);
        Assert.Contains("reads as 9000", scheme2, StringComparison.Ordinal);

        string scheme1 = BeamPresets.MarkerSchemeOverlapLog(BeamPresets.LegacyTintMarkerRed);
        Assert.Contains("scheme 1 won", scheme1, StringComparison.Ordinal);
        Assert.Contains("reads as 9100", scheme1, StringComparison.Ordinal);
        Assert.NotEqual(scheme1, scheme2);
    }

    /// <summary>
    /// R27 finding 10. The client tick used to build a query, a <c>ComponentType[]</c> and a
    /// <c>List&lt;Entity&gt;</c> sixty times a second forever. With no coil beam in the world
    /// the enumeration now runs once every
    /// <see cref="ClientBeamPoll.IdleQueryEveryFrames"/> frames, and it goes back to every frame
    /// as soon as one is seen.
    /// </summary>
    [Fact]
    public void Client_tick_skips_the_query_while_no_coil_beams_exist()
    {
        int idle = 0;
        // -1 is "never enumerated": always run.
        Assert.True(ClientBeamPoll.ShouldEnumerate(-1, ref idle));

        int runs = 0;
        for (int frame = 0; frame < ClientBeamPoll.IdleQueryEveryFrames * 3; frame++)
        {
            if (ClientBeamPoll.ShouldEnumerate(0, ref idle)) runs++;
        }

        Assert.Equal(3, runs);

        for (int frame = 0; frame < 5; frame++)
            Assert.True(ClientBeamPoll.ShouldEnumerate(2, ref idle));
    }

    [Fact]
    public void No_marker_diagnostic_names_the_coil_beam_count() =>
        Assert.Equal(
            "Effects: server beam client: 4 coil beams, 0 matched markers",
            ClientBeamPoll.NoMarkerLog(4));

    /// <summary>R27 finding 10: the cached query is released when the module uninstalls.</summary>
    [Fact]
    public void Uninstall_shuts_the_runtime_down()
    {
        ServerBeam.Uninstall();
        Assert.Equal(1, _server.ShutdownCalls);
    }

    /// <summary>
    /// R27 finding 23. The coil path clamps to <c>MaxSeconds</c> because <c>PreviewTime</c>
    /// cannot sync above 20; the NetworkedSequence carrier has no such ceiling, so
    /// <c>.beam link 999999999</c> wrote a ~31-year <c>LifeTime</c> onto a replicated,
    /// proximity-ungated entity.
    /// </summary>
    [Fact]
    public void Link_seconds_are_clamped_to_the_link_maximum()
    {
        ServerBeam.Link(11, 1, 22, 1, 999999999f, owner: _owner);
        Assert.Equal(BeamPresets.MaxLinkSeconds, _server.LastLinkSeconds);
        ServerBeam.Mark(11, 1, float.PositiveInfinity, owner: _owner);
        Assert.Equal(BeamPresets.DefaultSeconds, _server.LastLinkSeconds);
        Assert.Contains(_log.Infos, l => l.Contains("seconds clamped to 600", StringComparison.Ordinal));
    }

    /// <summary>
    /// R27 finding 24.7: the fake now models the runtime's endpoint check, so a Link against a
    /// destroyed entity really does come back disabled instead of counting a call.
    /// </summary>
    [Fact]
    public void Link_against_a_destroyed_endpoint_is_disabled()
    {
        _server.AddEntity(11, 1);
        _server.AddEntity(22, 1);
        var ok = ServerBeam.Link(11, 1, 22, 1, 8f, owner: _owner);
        Assert.True(ok.Enabled);

        _server.RemoveEntity(22, 1);
        var dead = ServerBeam.Link(11, 1, 22, 1, 8f, owner: _owner);
        Assert.False(dead.Enabled);
        Assert.Contains(_log.Infos, l => l.Contains("link endpoint is gone", StringComparison.Ordinal));
    }

    /// <summary>
    /// R27 finding 20b. Anything spawned only to serve a beam has to die with it, from every
    /// path that can end the beam: the caller, RemoveAll, and the lifetime deadline.
    /// </summary>
    [Fact]
    public void Dispose_callback_runs_once_from_dispose_deadline_and_RemoveAll()
    {
        int calls = 0;
        var handle = ServerBeam.Spawn(BeamPreset.Coil, default, new EffectVec3(1f, 0f, 0f), 8f, _owner);
        handle.OnDisposed(() => calls++);
        handle.Dispose();
        handle.Dispose();
        Assert.Equal(1, calls);

        // Registering after the fact still runs, exactly once.
        handle.OnDisposed(() => calls++);
        Assert.Equal(2, calls);

        int sweptCalls = 0;
        var swept = ServerBeam.Spawn(BeamPreset.Coil, default, new EffectVec3(1f, 0f, 0f), 8f, _owner);
        swept.OnDisposed(() => sweptCalls++);
        ServerBeam.RemoveAll(_owner);
        Assert.Equal(1, sweptCalls);

        int expiredCalls = 0;
        float now = 0f;
        ServerBeam.TimeForTests = () => now;
        var expiring = ServerBeam.Spawn(BeamPreset.Coil, default, new EffectVec3(1f, 0f, 0f), 2f, _owner);
        expiring.OnDisposed(() => expiredCalls++);
        now = 5f;
        ServerBeam.TickForTests();
        Assert.True(expiring.IsDisposed);
        Assert.Equal(1, expiredCalls);
    }

    private int CountInfoContaining(string fragment)
    {
        int n = 0;
        for (int i = 0; i < _log.Infos.Count; i++)
        {
            if (_log.Infos[i].Contains(fragment, System.StringComparison.Ordinal)) n++;
        }

        return n;
    }

    private int CountInfos(string line)
    {
        int n = 0;
        for (int i = 0; i < _log.Infos.Count; i++)
        {
            if (_log.Infos[i] == line) n++;
        }

        return n;
    }

    private static EffectVec3 Add(EffectVec3 a, EffectVec3 b) =>
        new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

    private sealed class Owner
    {
        public Owner(string guid) => Guid = guid;
        public string Guid { get; }
    }

    internal sealed class FakeServerBeamRuntime : IServerBeamRuntime
    {
        private readonly HashSet<string> _names = new(StringComparer.Ordinal);
        private readonly HashSet<int> _guids = new();
        private readonly Dictionary<ServerBeamId, (EffectVec3 Start, EffectVec3 End)> _beams = new();
        private readonly Dictionary<(int, int), EffectVec3> _pos = new();

        /// <summary>
        /// R27 finding 24.7: the fake modeled none of the real <c>SpawnLink</c> — no
        /// <c>em.Exists</c> endpoint check and no <c>TryWriteLifeTime</c> — so every Link/Mark
        /// assertion measured a counter. Register the entities a link may reach and the fake
        /// refuses a dead endpoint the way the runtime does.
        /// </summary>
        private readonly HashSet<(int, int)> _entities = new();
        private int _next = 10;

        public bool IsServer { get; set; }

        public string? LastMissReason { get; private set; }

        public string LastResolvedName { get; private set; } = "";

        public int SpawnCalls { get; private set; }
        public int WriteCalls { get; private set; }
        public int DestroyCalls { get; private set; }
        public float LastBlastTime { get; private set; }
        public float LastPreviewTime { get; private set; }
        public float LastWritePreviewTime { get; private set; } = -1f;
        public List<float> PreviewTimes { get; } = new();
        public EffectVec3 LastStart { get; private set; }
        public EffectVec3 LastEnd { get; private set; }
        public List<(EffectVec3 Start, EffectVec3 End)> Writes { get; } = new();

        /// <summary>Seconds the last SpawnLink was asked for; the runtime writes it as LifeTime.</summary>
        public float LastLinkSeconds { get; private set; }

        /// <summary>How many times <c>ServerBeam.Uninstall</c> released the runtime's caches.</summary>
        public int ShutdownCalls { get; private set; }

        public void Add(string name) => _names.Add(name);

        /// <summary>Declare a live entity a link may target. With none declared, every id is accepted.</summary>
        public void AddEntity(int index, int version) => _entities.Add((index, version));

        /// <summary>Model the deferred destroy: the entity stops existing for SpawnLink.</summary>
        public void RemoveEntity(int index, int version) => _entities.Remove((index, version));

        public void AddGuid(int guid) => _guids.Add(guid);

        public void SetTranslation(int index, int version, EffectVec3 position) =>
            _pos[(index, version)] = position;

        public ServerBeamId Spawn(
            string prefabName,
            int prefabGuid,
            EffectVec3 start,
            EffectVec3 end,
            float seconds,
            float previewTime,
            float blastTime)
        {
            SpawnCalls++;
            LastBlastTime = blastTime;
            LastPreviewTime = previewTime;
            LastMissReason = null;
            LastResolvedName = "";
            bool known = _names.Contains(prefabName)
                || (prefabGuid != 0 && _guids.Contains(prefabGuid))
                || (string.IsNullOrEmpty(prefabName) && prefabGuid == 0 && _names.Count > 0);
            if (!known)
            {
                LastMissReason = "Effects: no coil beam prefab on this server; returning a disabled handle.";
                return ServerBeamId.None;
            }

            var id = new ServerBeamId(_next++, 1);
            _beams[id] = (start, end);
            LastResolvedName = string.IsNullOrEmpty(prefabName) ? "ProfessorCoilBeam" : prefabName;
            LastStart = start;
            LastEnd = end;
            return id;
        }

        public int LinkCalls { get; private set; }
        public int LastLinkSequenceGuid { get; private set; }
        public (int A, int B) LastLinkEntities { get; private set; }
        public bool LinkAvailable { get; set; } = true;

        /// <summary>True when the last call was the single-target mark shape (endIndex 0).</summary>
        public bool LastLinkPlayOnSelf { get; private set; }

        public ServerBeamId SpawnLink(
            int sequenceGuid,
            int startIndex,
            int startVersion,
            int endIndex,
            int endVersion,
            float seconds)
        {
            LinkCalls++;
            LastMissReason = null;
            LastResolvedName = "";
            LastLinkSeconds = seconds;
            if (!LinkAvailable || startIndex == 0)
            {
                LastMissReason = "Effects: no beam SequenceGUID visible on this server; returning a disabled handle.";
                return ServerBeamId.None;
            }

            // The runtime refuses an endpoint the EntityManager no longer knows (R27 24.7).
            if (_entities.Count > 0
                && (!_entities.Contains((startIndex, startVersion))
                    || (endIndex != 0 && !_entities.Contains((endIndex, endVersion)))))
            {
                LastMissReason = "Effects: server beam link endpoint is gone; returning a disabled handle.";
                return ServerBeamId.None;
            }

            LastLinkSequenceGuid = sequenceGuid;
            LastLinkEntities = (startIndex, endIndex);
            LastLinkPlayOnSelf = endIndex == 0;
            LastResolvedName = "NetworkedSequence";
            var id = new ServerBeamId(_next++, 1);
            _beams[id] = (default, default);
            return id;
        }

        public bool WriteEndpoints(ServerBeamId id, EffectVec3 start, EffectVec3 end, float previewTime)
        {
            if (!_beams.ContainsKey(id)) return false;
            WriteCalls++;
            LastStart = start;
            LastEnd = end;
            LastWritePreviewTime = previewTime;
            if (previewTime >= 0f) PreviewTimes.Add(previewTime);
            Writes.Add((start, end));
            _beams[id] = (start, end);
            return true;
        }

        public bool TryReadTranslation(int index, int version, out EffectVec3 position) =>
            _pos.TryGetValue((index, version), out position);

        public bool Exists(ServerBeamId id) => _beams.ContainsKey(id);

        public bool Destroy(ServerBeamId id)
        {
            DestroyCalls++;
            return _beams.Remove(id);
        }

        /// <summary>The real client tick can throw (a released query, a dead world).</summary>
        public bool ThrowOnClientTick { get; set; }

        public void ClientBeamTick()
        {
            ClientTicks++;
            if (ThrowOnClientTick) throw new InvalidOperationException("client tick threw (test)");
        }

        public void Shutdown() => ShutdownCalls++;

        public int ClientTicks { get; private set; }
    }
}
