using System;
using System.Collections.Generic;
using Progenitor.Effects;
using Progenitor.Sample;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Game-free coverage of Sample <c>.beam</c>: token parse, spawn/track/off against the
/// ServerBeam fake runtime.
/// </summary>
[Collection("PluginLogger")]
public class SampleBeamTests : IDisposable
{
    private readonly TestLogSource _log = new();
    private readonly ServerBeamTests.FakeServerBeamRuntime _server = new() { IsServer = true };
    private readonly object _owner = new();

    public SampleBeamTests()
    {
        Plugin.Logger = _log;
        ServerBeam.ResetForTests();
        _server.Add("ProfessorCoilBeam");
        ServerBeam.SetRuntimeForTests(_server);
        SampleBeam.AttachForTests(_owner, "progenitor.sample");
    }

    public void Dispose()
    {
        SampleBeam.Detach();
        ServerBeam.ResetForTests();
        Plugin.Logger = null;
    }

    [Theory]
    [InlineData(null, null, null, 1, 8f, 0, 1)]
    [InlineData("", "", "", 1, 8f, 0, 1)]
    [InlineData("off", "", "", 2, 8f, 0, 1)]
    [InlineData("OFF", "", "", 2, 8f, 0, 1)]
    [InlineData("12", "", "", 1, 12f, 0, 1)]
    [InlineData("12", "blue", "", 1, 12f, 2, 1)]
    [InlineData("12", "red", "3", 1, 12f, 0, 3)]
    [InlineData("red", "", "", 1, 8f, 0, 1)]
    [InlineData("blue", "2", "", 1, 8f, 2, 2)]
    [InlineData("coil", "", "", 1, 8f, 1, 1)]
    [InlineData("5", "coil", "2", 1, 5f, 1, 2)]
    public void TryParse_accepts_seconds_colour_count_and_off(
        string? a, string? b, string? c, int kind, float seconds, int preset, int count)
    {
        Assert.True(SampleBeam.TryParse(a, b, c, out var args));
        Assert.Equal(kind, (int)args.Kind);
        Assert.Equal(seconds, args.Seconds);
        Assert.Equal((BeamPreset)preset, args.Preset);
        Assert.Equal(count, args.Count);
    }

    [Theory]
    [InlineData("ember", "", "")]
    [InlineData("-1", "", "")]
    [InlineData("0", "", "")]
    [InlineData("nope", "", "")]
    [InlineData("12", "green", "")]
    public void TryParse_rejects_unknown(string a, string b, string c)
    {
        Assert.False(SampleBeam.TryParse(a, b, c, out _));
    }

    [Fact]
    public void Run_spawns_a_red_beam_and_off_disposes_it()
    {
        var args = new SampleBeam.Args(SampleBeam.Kind.Spawn, 8f, BeamPreset.CastleRed, 1);
        string reply = SampleBeam.Run(args, new EffectVec3(10f, 5f, 20f), new EffectVec3(0f, 0f, 1f));
        Assert.Equal(1, SampleBeam.LiveCount);
        Assert.Equal(1, _server.SpawnCalls);
        Assert.StartsWith("Beam ", reply, StringComparison.Ordinal);
        Assert.Contains("CastleRed", reply, StringComparison.Ordinal);
        Assert.Contains(_log.Infos, l => l.StartsWith("Sample: beam spawn entity=", StringComparison.Ordinal));
        Assert.Contains(_log.Infos, l => l.Contains("Effects: server beam spawned prefab=", StringComparison.Ordinal));

        string off = SampleBeam.Run(new SampleBeam.Args(SampleBeam.Kind.Off, 8f, BeamPreset.CastleRed, 1));
        Assert.Equal("Beam off.", off);
        Assert.Equal(0, SampleBeam.LiveCount);
        Assert.Equal(1, _server.DestroyCalls);
    }

    [Fact]
    public void Run_with_count_two_spawns_parallel_beams()
    {
        var args = new SampleBeam.Args(SampleBeam.Kind.Spawn, 8f, BeamPreset.Blue, 2);
        SampleBeam.Run(args, new EffectVec3(0f, 0f, 0f), new EffectVec3(0f, 0f, 1f));
        Assert.Equal(2, SampleBeam.LiveCount);
        Assert.Equal(2, _server.SpawnCalls);
        Assert.Equal(BeamPresets.TintMarkerBlue, _server.LastBlastTime);
        SampleBeam.Detach();
        Assert.Equal(2, _server.DestroyCalls);
    }

    [Theory]
    [InlineData("blast", "", "", 1)]
    [InlineData("5", "blast", "", 1)]
    [InlineData("5", "red", "blast", 1)]
    [InlineData("steady", "", "", 0)]
    [InlineData("preview", "", "", 0)]
    [InlineData("5", "red", "2", 0)]
    public void TryParse_reads_the_phase_token_in_any_slot(string a, string b, string c, int phase)
    {
        Assert.True(SampleBeam.TryParse(a, b, c, out var args));
        Assert.Equal((BeamPhase)phase, args.Phase);
    }

    [Fact]
    public void Run_blast_asks_the_handle_for_the_discharge()
    {
        var args = new SampleBeam.Args(SampleBeam.Kind.Spawn, 8f, BeamPreset.Coil, 1, BeamPhase.Blast);
        string reply = SampleBeam.Run(args, new EffectVec3(0f, 0f, 0f), new EffectVec3(0f, 0f, 1f));
        Assert.Contains("Blast", reply, StringComparison.Ordinal);
        Assert.Equal(BeamPresets.BlastPreviewTime, _server.LastWritePreviewTime);
        Assert.Contains(_log.Infos, l => l.Contains("Sample: beam spawn", StringComparison.Ordinal)
            && l.Contains("phase=Blast", StringComparison.Ordinal));
    }

    [Fact]
    public void Run_defaults_to_a_steady_beam_pinned_at_the_sync_maximum()
    {
        var args = new SampleBeam.Args(SampleBeam.Kind.Spawn, 8f, BeamPreset.CastleRed, 1);
        SampleBeam.Run(args, new EffectVec3(0f, 0f, 0f), new EffectVec3(0f, 0f, 1f));
        Assert.Equal(BeamPresets.SteadyPreviewTime, _server.LastPreviewTime);
    }

    [Theory]
    [InlineData("link", "", "", 8f)]
    [InlineData("LINK", "", "", 8f)]
    [InlineData("link", "12", "", 12f)]
    [InlineData("12", "link", "", 12f)]
    public void TryParse_reads_the_link_token_in_any_slot(string a, string b, string c, float seconds)
    {
        Assert.True(SampleBeam.TryParse(a, b, c, out var args));
        Assert.Equal(SampleBeam.Kind.Link, args.Kind);
        Assert.Equal(seconds, args.Seconds);
        Assert.Equal(BeamPreset.Link, args.Preset);
        Assert.Equal(1, args.Count);
    }

    [Fact]
    public void TryParse_off_still_wins_over_link()
    {
        Assert.True(SampleBeam.TryParse("link", "off", "", out var args));
        Assert.Equal(SampleBeam.Kind.Off, args.Kind);
    }

    [Fact]
    public void Run_link_spawns_a_networked_sequence_between_two_entities()
    {
        var args = new SampleBeam.Args(SampleBeam.Kind.Link, 8f, BeamPreset.Link, 1);
        string reply = SampleBeam.Run(args, default, default, 11, 1, 22, 1);

        Assert.Equal(1, _server.LinkCalls);
        Assert.Equal(0, _server.SpawnCalls);
        Assert.Equal(BeamPresets.DefaultLinkSequence, _server.LastLinkSequenceGuid);
        Assert.Equal((11, 22), _server.LastLinkEntities);
        Assert.StartsWith("Beam link ", reply, StringComparison.Ordinal);
        Assert.Contains("(Sequence)AB_Charm_Channeling", reply, StringComparison.Ordinal);
        Assert.Contains(_log.Infos, l => l.StartsWith("Sample: beam link entity=", StringComparison.Ordinal)
            && l.Contains("11:1 -> 22:1", StringComparison.Ordinal));
        Assert.Contains(_log.Infos, l => l.Contains("Effects: server beam link spawned prefab=NetworkedSequence", StringComparison.Ordinal));
        Assert.Equal(1, SampleBeam.LiveCount);

        SampleBeam.Run(new SampleBeam.Args(SampleBeam.Kind.Off, 8f, BeamPreset.Link, 1));
        Assert.Equal(1, _server.DestroyCalls);
    }

    [Fact]
    public void Run_link_refuses_without_two_entities()
    {
        var args = new SampleBeam.Args(SampleBeam.Kind.Link, 8f, BeamPreset.Link, 1);
        string reply = SampleBeam.Run(args, default, default, 11, 1, 0, 0);
        Assert.Equal(0, _server.LinkCalls);
        Assert.Contains(".rat", reply, StringComparison.Ordinal);
        Assert.Equal(0, SampleBeam.LiveCount);
    }

    [Fact]
    public void Run_link_reports_a_disabled_handle_when_the_server_cannot_spawn_it()
    {
        _server.LinkAvailable = false;
        var args = new SampleBeam.Args(SampleBeam.Kind.Link, 8f, BeamPreset.Link, 1);
        string reply = SampleBeam.Run(args, default, default, 11, 1, 22, 1);
        Assert.Equal(1, _server.LinkCalls);
        Assert.Contains("disabled", reply, StringComparison.Ordinal);
        Assert.Equal(0, SampleBeam.LiveCount);
    }

    [Theory]
    [InlineData("mark", "", "", 8f, 0)]
    [InlineData("mark", "red", "", 8f, 0)]
    [InlineData("mark", "blue", "", 8f, 2)]
    [InlineData("mark", "blue", "12", 12f, 2)]
    [InlineData("12", "mark", "", 12f, 0)]
    public void TryParse_reads_the_mark_token_with_its_colour(string a, string b, string c, float seconds, int preset)
    {
        Assert.True(SampleBeam.TryParse(a, b, c, out var args));
        Assert.Equal(SampleBeam.Kind.Mark, args.Kind);
        Assert.Equal(seconds, args.Seconds);
        Assert.Equal((BeamPreset)preset, args.Preset);
        Assert.Equal(1, args.Count);
    }

    [Fact]
    public void Run_mark_puts_the_red_placement_sequence_on_one_entity()
    {
        var args = new SampleBeam.Args(SampleBeam.Kind.Mark, 8f, BeamPreset.CastleRed, 1);
        string reply = SampleBeam.Run(args, default, default, 0, 0, 22, 1);

        Assert.Equal(1, _server.LinkCalls);
        Assert.Equal(BeamPresets.MarkSequenceRed, _server.LastLinkSequenceGuid);
        Assert.Equal((22, 0), _server.LastLinkEntities);
        Assert.True(_server.LastLinkPlayOnSelf);
        Assert.StartsWith("Beam mark ", reply, StringComparison.Ordinal);
        Assert.Contains(_log.Infos, l => l.StartsWith("Sample: beam mark entity=", StringComparison.Ordinal)
            && l.Contains("target=22:1", StringComparison.Ordinal));
        Assert.Equal(1, SampleBeam.LiveCount);
    }

    [Fact]
    public void Run_mark_falls_back_to_the_caller_and_takes_the_blue_variant()
    {
        var args = new SampleBeam.Args(SampleBeam.Kind.Mark, 5f, BeamPreset.Blue, 1);
        SampleBeam.Run(args, default, default, 11, 1, 0, 0);
        Assert.Equal((11, 0), _server.LastLinkEntities);
        Assert.Equal(BeamPresets.MarkSequenceBlue, _server.LastLinkSequenceGuid);
    }

    /// <summary>
    /// R27 finding 20a. <c>.beam link</c> found the nearest rat <b>before</b> sweeping the
    /// previous beam, so the second run picked marker rat R1, the sweep then despawned R1, and
    /// the link was spawned against a destroyed entity while the reply still read
    /// "Beam link … -> rat 12:1". The sweep now happens first and the rat it despawned is
    /// excluded from the search, so the beam always targets a rat it keeps.
    /// </summary>
    [Fact]
    public void Link_sweeps_first_and_never_targets_the_rat_it_just_despawned()
    {
        var world = new FakeLinkWorld();
        var args = new SampleBeam.Args(SampleBeam.Kind.Link, 8f, BeamPreset.Link, 1);

        string first = SampleBeam.RunLinkWithMarker(args, world, 11, 1);
        Assert.Equal(1, world.SpawnCalls);
        var r1 = world.Spawned[0];
        Assert.Equal((11, r1.Index), _server.LastLinkEntities);
        Assert.Contains("spawned rat", first, StringComparison.Ordinal);
        Assert.False(r1.Disposed);

        string second = SampleBeam.RunLinkWithMarker(args, world, 11, 1);

        // The sweep ran before the search, and told the search which rat it took away.
        Assert.Equal(r1.Index, world.ExcludeSeen);
        Assert.True(r1.Disposed);
        Assert.Equal(2, world.SpawnCalls);
        var r2 = world.Spawned[1];
        Assert.NotEqual(r1.Index, r2.Index);
        Assert.False(r2.Disposed);
        Assert.Equal((11, r2.Index), _server.LastLinkEntities);
        Assert.Contains(r2.Index.ToString(System.Globalization.CultureInfo.InvariantCulture), second, StringComparison.Ordinal);
    }

    /// <summary>
    /// R27 finding 20b. The doc said the rat was "despawned with the beam"; it was not. The
    /// <c>NetworkedSequence</c> carrier expired on its own <c>LifeTime</c>, nothing observed
    /// that, and the marker was released only by the <em>next</em> <c>.beam off</c> — so
    /// <c>.beam link</c>, wait 8 s, walk away, and a rat stood in the world indefinitely with
    /// no beam attached. The marker's lifetime is now tied to the handle.
    /// </summary>
    [Fact]
    public void Link_marker_rat_is_despawned_when_the_beam_ends()
    {
        float now = 0f;
        ServerBeam.TimeForTests = () => now;
        var world = new FakeLinkWorld();
        var args = new SampleBeam.Args(SampleBeam.Kind.Link, 4f, BeamPreset.Link, 1);

        SampleBeam.RunLinkWithMarker(args, world, 11, 1);
        var rat = world.Spawned[0];
        Assert.False(rat.Disposed);

        // Nothing else happens; the beam simply reaches its own deadline.
        now = 10f;
        ServerBeam.TickForTests();

        Assert.True(rat.Disposed);
        Assert.Contains(_log.Infos, l => l == "Sample: beam link marker despawned with the beam");
    }

    /// <summary>An existing rat is reused, no marker is spawned, and nothing is despawned.</summary>
    [Fact]
    public void Link_reuses_a_live_rat_without_spawning_a_marker()
    {
        var world = new FakeLinkWorld { RatIndex = 55, RatVersion = 2 };
        var args = new SampleBeam.Args(SampleBeam.Kind.Link, 8f, BeamPreset.Link, 1);

        string reply = SampleBeam.RunLinkWithMarker(args, world, 11, 1);

        Assert.Equal(0, world.SpawnCalls);
        Assert.Equal((11, 55), _server.LastLinkEntities);
        Assert.Contains("rat 55:2", reply, StringComparison.Ordinal);
    }

    [Fact]
    public void Link_reports_a_failed_marker_spawn_without_a_beam()
    {
        var world = new FakeLinkWorld { SpawnFails = true };
        var args = new SampleBeam.Args(SampleBeam.Kind.Link, 8f, BeamPreset.Link, 1);

        string reply = SampleBeam.RunLinkWithMarker(args, world, 11, 1);

        Assert.Equal(0, _server.LinkCalls);
        Assert.Contains("no rat could be spawned", reply, StringComparison.Ordinal);
        Assert.Equal(0, SampleBeam.LiveCount);
    }

    /// <summary>R27 finding 23: an absurd `seconds` token is clamped at the parse.</summary>
    [Theory]
    [InlineData("999999999", 600f)]
    [InlineData("link", 8f)]
    [InlineData("30", 30f)]
    public void TryParse_clamps_seconds_to_the_link_maximum(string token, float seconds)
    {
        Assert.True(SampleBeam.TryParse(token, "", "", out var args));
        Assert.Equal(seconds, args.Seconds);
    }

    private sealed class FakeMarkerRat : IDisposable
    {
        internal FakeMarkerRat(int index) => Index = index;

        internal int Index { get; }

        internal bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }

    /// <summary>
    /// Models the one thing that matters here: a destroyed rat is no longer a candidate, and
    /// the rat spawned as a marker <em>is</em> in the world for the next search — which is
    /// exactly how R27 finding 20a's second <c>.beam link</c> found the rat it was about to
    /// destroy.
    /// </summary>
    private sealed class FakeLinkWorld : SampleBeam.ILinkWorld
    {
        internal int RatIndex;
        internal int RatVersion;
        internal int ExcludeSeen = -1;
        internal int SpawnCalls;
        internal bool SpawnFails;
        internal List<FakeMarkerRat> Spawned { get; } = new();

        private int _next = 100;

        public bool TryFindRat(int excludeIndex, out int index, out int version)
        {
            ExcludeSeen = excludeIndex;
            index = 0;
            version = 0;
            if (RatIndex == 0 || RatIndex == excludeIndex) return false;
            index = RatIndex;
            version = RatVersion;
            return true;
        }

        public IDisposable? SpawnMarkerRat(out int index, out int version, out string where)
        {
            SpawnCalls++;
            where = "1,2,3";
            index = 0;
            version = 0;
            if (SpawnFails) return null;
            index = _next++;
            version = 1;
            var rat = new FakeMarkerRat(index);
            Spawned.Add(rat);
            RatIndex = index;
            RatVersion = 1;
            return rat;
        }
    }

    [Fact]
    public void Run_tracks_supplied_entity_ids()
    {
        _server.SetTranslation(11, 1, new EffectVec3(1f, 0f, 0f));
        _server.SetTranslation(22, 1, new EffectVec3(7f, 0f, 0f));
        var args = new SampleBeam.Args(SampleBeam.Kind.Spawn, 8f, BeamPreset.Coil, 1);
        SampleBeam.Run(args, new EffectVec3(1f, 0f, 0f), new EffectVec3(1f, 0f, 0f), 11, 1, 22, 1);
        ServerBeam.TickForTests();
        Assert.Equal(
            new EffectVec3(1f + BeamPresets.DefaultStartOffset.X, BeamPresets.DefaultStartOffset.Y, 0f),
            _server.LastStart);
        Assert.Equal(
            new EffectVec3(7f + BeamPresets.DefaultEndOffset.X, BeamPresets.DefaultEndOffset.Y, 0f),
            _server.LastEnd);
    }
}
