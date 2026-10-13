using System;
using System.Collections.Generic;
using System.IO;
using Progenitor;
using Progenitor.Dev;
using Progenitor.Effects;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Game-free coverage of the Effects module: exact-id ownership, the Borrow → Host →
/// Dispose state machine (double dispose safe), sequence entity lifetime, late-driver poll,
/// RT layer/size, and the server no-op. Unity stays behind <see cref="IEffectRuntime"/>;
/// this file supplies a fake.
/// </summary>
[Collection("PluginLogger")]
public class EffectsTests : IDisposable
{
    private readonly TestLogSource _log = new();
    private readonly FakeEffectRuntime _client = new() { IsClient = true };
    private readonly object _owner = new Owner("example.effects.test");

    public EffectsTests()
    {
        Plugin.Logger = _log;
        Assets.ResetForTests();
        MainThread.ResetForTests();
        _client.Add(AssetKind.Image, "PassiveFrame_Normal");
        _client.Add(AssetKind.Particle, "Embers");
        _client.Add(AssetKind.Volume, "MenuGold");
        _client.Add(AssetKind.Sequence, "AB_Cardinal_SummonOrb_Beam_Buff");
        _client.Add(AssetKind.VisualEffect, "Beam_Cardinal_Orb_Guardian_01");
        Assets.SetRuntimeForTests(_client);
    }

    public void Dispose()
    {
        Assets.ResetForTests();
        MainThread.ResetForTests();
        Plugin.Logger = null;
    }

    /// <summary>
    /// A repo file's text, found by walking up to <c>Progenitor.sln</c>. Used by two source-pin
    /// tests: <see cref="The_input_element_stride_is_derived_from_the_dump_offsets"/>, which
    /// checks that the dump lines a load-bearing constant cites are the ones the docs publish
    /// (R30 finding 22), and
    /// <see cref="The_bind_site_switches_on_the_same_three_state_ownership_answer"/>, which pins
    /// a call site that needs a live Unity clone (R45 finding 30).
    /// </summary>
    private static string ReadRepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Progenitor.sln")))
            {
                string path = Path.Combine(dir.FullName, Path.Combine(parts));
                if (!File.Exists(path)) throw new FileNotFoundException(path);
                return File.ReadAllText(path);
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Progenitor.sln not found from " + AppContext.BaseDirectory);
    }

    [Fact]
    public void RootName_uses_progenitor_effects_prefix_and_sanitizes_separators()
    {
        Assert.Equal("progenitor-effects-example.sample", EffectOwnership.RootName("example.sample"));
        Assert.Equal("progenitor-effects-unowned", EffectOwnership.RootName(null));
        Assert.Equal("progenitor-effects-unowned", EffectOwnership.RootName("  "));
        Assert.Equal("a-b", EffectOwnership.Sanitize("a/b"));
        Assert.True(EffectOwnership.IsOwned("progenitor-effects-example.sample"));
        Assert.True(EffectOwnership.IsOwnedBy("progenitor-effects-example.sample", "example.sample"));
        Assert.True(EffectOwnership.IsOwnedBy("progenitor-effects-example.sample-image", "example.sample"));
        Assert.False(EffectOwnership.IsOwnedBy("progenitor-effects-example.sample.extra", "example.sample"));
        Assert.False(EffectOwnership.IsOwnedBy("progenitor-effects-example.sample.extra-image", "example.sample"));
        Assert.True(EffectOwnership.IsOwnedBy("progenitor-effects-example.sample.extra", "example.sample.extra"));
        Assert.False(EffectOwnership.IsOwnedBy("progenitor-effects-other", "example.sample"));
        Assert.False(EffectOwnership.IsOwned("progenitor-fx-ui"));
    }

    [Fact]
    public void Borrow_Host_Dispose_state_machine_and_double_dispose_is_safe()
    {
        var handle = Assets.Borrow(AssetKind.Image, "PassiveFrame_Normal", _owner, "example.effects.test");
        Assert.IsType<UiImageHandle>(handle);
        Assert.True(handle.Enabled);
        Assert.Equal(EffectHandleState.Borrowed, handle.State);
        Assert.Equal("progenitor-effects-example.effects.test", handle.RootName);
        Assert.Equal("PassiveFrame_Normal", handle.Name);
        Assert.True(_client.HasObject("PassiveFrame_Normal"));

        var host = Assets.Host(handle, HostKind.Overlay);
        Assert.True(host.Enabled);
        Assert.False(host.IsDisposed);
        Assert.Equal(EffectHandleState.Hosted, handle.State);
        Assert.Equal(1, _client.HostCount);

        handle.Dispose();
        Assert.True(handle.IsDisposed);
        Assert.Equal(EffectHandleState.Disposed, handle.State);
        Assert.True(host.IsDisposed);
        Assert.False(_client.HasObject("PassiveFrame_Normal"));
        Assert.Equal(0, _client.HostCount);

        handle.Dispose();
        host.Dispose();
        Assert.True(handle.IsDisposed);
        Assert.True(host.IsDisposed);
        Assert.Contains(_log.Infos, l => l.Contains("Effects: borrowed Image 'PassiveFrame_Normal'", StringComparison.Ordinal));
        Assert.Contains(_log.Infos, l => l.Contains("Effects: hosted Overlay", StringComparison.Ordinal));
        Assert.Contains(_log.Infos, l => l.Contains("Effects: disposed Image 'PassiveFrame_Normal'", StringComparison.Ordinal));
    }

    [Fact]
    public void Host_dispose_leaves_borrow_alive()
    {
        var handle = Assets.Borrow(AssetKind.Particle, "Embers", _owner, "example.effects.test");
        var host = Assets.Host(handle, HostKind.RenderTexture);
        Assert.Equal(EffectHandleState.Hosted, handle.State);
        host.Dispose();
        host.Dispose();
        Assert.True(host.IsDisposed);
        Assert.False(handle.IsDisposed);
        Assert.Equal(EffectHandleState.Borrowed, handle.State);
        Assert.True(_client.HasObject("Embers"));
        handle.Dispose();
    }

    [Fact]
    public void Server_Borrow_Host_Find_return_disabled_and_log_once_each()
    {
        var server = new FakeEffectRuntime { IsClient = false };
        server.Add(AssetKind.Image, "PassiveFrame_Normal");
        Assets.SetRuntimeForTests(server);

        var handle = Assets.Borrow(AssetKind.Image, "PassiveFrame_Normal", _owner, "example.effects.test");
        Assert.False(handle.Enabled);
        Assert.Equal(EffectHandleState.Disabled, handle.State);
        Assert.False(server.HasObject("PassiveFrame_Normal"));
        Assert.Contains(_log.Infos, l => l == "Effects: Borrow(Image, 'PassiveFrame_Normal') is client-only; returning a disabled handle.");

        var host = Assets.Host(handle, HostKind.Overlay);
        Assert.False(host.Enabled);
        Assert.Contains(_log.Infos, l => l == "Effects: Host(Overlay) is client-only; returning a disabled handle.");

        var names = Assets.Find(AssetKind.Image, "Passive");
        Assert.Empty(names);
        Assert.Contains(_log.Infos, l => l == "Effects: Find(Image) is client-only; returning no names.");

        handle.Dispose();
        handle.Dispose();
        Assert.Equal(EffectHandleState.Disposed, handle.State);
        Assert.Equal(0, server.BorrowCalls);
        Assert.Equal(0, server.HostCalls);
        Assert.Equal(0, server.FindCalls);
    }

    [Fact]
    public void Find_filters_catalogue_names_the_client_currently_has()
    {
        var all = Assets.Find(AssetKind.Image, null);
        Assert.Contains("PassiveFrame_Normal", all);
        var filtered = Assets.Find(AssetKind.Image, "frame");
        Assert.Equal(new[] { "PassiveFrame_Normal" }, filtered);
        Assert.Empty(Assets.Find(AssetKind.Image, "nope"));
        Assert.Contains("Embers", Assets.Find(AssetKind.Particle, "Emb"));
        Assert.Contains("Beam_Cardinal_Orb_Guardian_01", Assets.Find(AssetKind.VisualEffect, "Cardinal"));
        Assert.Contains("MenuGold", Assets.Find(AssetKind.Volume, "Gold"));
        Assert.Contains("AB_Cardinal_SummonOrb_Beam_Buff", Assets.Find(AssetKind.Sequence, "Cardinal"));
    }

    [Fact]
    public void Missing_name_returns_disabled_handle()
    {
        var handle = Assets.Borrow(AssetKind.Image, "MissingSprite", _owner, "example.effects.test");
        Assert.False(handle.Enabled);
        Assert.Equal(EffectHandleState.Disabled, handle.State);
        Assert.Contains(_log.Infos, l => l.Contains("no Image named 'MissingSprite'", StringComparison.Ordinal));
        handle.Dispose();
    }

    [Fact]
    public void RemoveAll_disposes_owner_handles_and_sweeps_the_root_prefix()
    {
        var a = Assets.Borrow(AssetKind.Image, "PassiveFrame_Normal", _owner, "example.effects.test");
        var b = Assets.Borrow(AssetKind.Particle, "Embers", _owner, "example.effects.test");
        Assets.Host(a, HostKind.Overlay);
        Assets.RemoveAll(_owner);
        Assert.True(a.IsDisposed);
        Assert.True(b.IsDisposed);
        Assert.Equal(0, _client.ObjectCount);
        Assert.Contains(_client.DestroyedRoots, r => r == "progenitor-effects-example.effects.test");
    }

    [Fact]
    public void Typed_handles_expose_family_knobs_through_the_fake()
    {
        var image = Assert.IsType<UiImageHandle>(
            Assets.Borrow(AssetKind.Image, "PassiveFrame_Normal", _owner, "example.effects.test"));
        image.Color = new EffectColor(1f, 0.84f, 0f, 1f);
        image.Size = new EffectVec2(192f, 192f);
        image.Position = new EffectVec2(0f, 40f);
        image.SortingOrder = 5000;
        Assert.Equal(new EffectColor(1f, 0.84f, 0f, 1f), image.Color);
        Assert.Equal(new EffectVec2(192f, 192f), image.Size);
        Assert.Equal(5000, image.SortingOrder);

        var particle = Assert.IsType<ParticleHandle>(
            Assets.Borrow(AssetKind.Particle, "Embers", _owner, "example.effects.test"));
        particle.Tint = new EffectColor(1f, 0.84f, 0f);
        particle.Position = new EffectVec3(0f, 8000f, 0f);
        particle.Play();
        particle.Stop();
        Assert.Equal(new EffectVec3(0f, 8000f, 0f), particle.Position);
        Assert.True(_client.LastStopped);

        var volume = Assert.IsType<VolumeHandle>(
            Assets.Borrow(AssetKind.Volume, "MenuGold", _owner, "example.effects.test"));
        volume.Tint = new EffectColor(1f, 0.84f, 0f);
        Assert.Equal(volume.Tint, volume.Color);

        var seq = Assert.IsType<SequenceEffectHandle>(
            Assets.Borrow(AssetKind.Sequence, "AB_Cardinal_SummonOrb_Beam_Buff", _owner, "example.effects.test"));
        seq.Tint = new EffectColor(0.25f, 0.55f, 1f);
        seq.Position = new EffectVec3(5f, 0f, 0f);
        Assert.Equal(new EffectVec3(5f, 0f, 0f), seq.Position);
        Assert.Equal(new EffectVec3(5f, 0f, 0f), seq.Start);
    }

    [Fact]
    public void Particle_Scale_before_and_after_Play_round_trips_on_the_fake()
    {
        var particle = Assert.IsType<ParticleHandle>(
            Assets.Borrow(AssetKind.Particle, "Embers", _owner, "example.effects.test"));
        Assert.Same(particle, particle.Scale(2f));
        Assert.Equal(2f, particle.UniformScale);
        Assert.Equal(2f, _client.ObjectScale("Embers"));
        Assert.Equal(0, _client.ObjectPlayCount("Embers"));
        Assert.Contains(_log.Infos, l => l == "Effects: particle 'Embers' scale 2 via transform (fake)");

        particle.Play();
        Assert.True(_client.LastPlayed);
        Assert.Equal(1, _client.ObjectPlayCount("Embers"));
        Assert.Equal(2f, _client.ObjectScale("Embers"));

        Assert.Same(particle, particle.Scale(4f));
        Assert.Equal(4f, particle.UniformScale);
        Assert.Equal(4f, _client.ObjectScale("Embers"));
        Assert.Equal(1, _client.ObjectPlayCount("Embers"));
        particle.Dispose();
    }

    [Fact]
    public void Particle_Scale_after_dispose_logs_and_does_not_throw()
    {
        var particle = Assert.IsType<ParticleHandle>(
            Assets.Borrow(AssetKind.Particle, "Embers", _owner, "example.effects.test"));
        particle.Scale(2f);
        particle.Dispose();
        Assert.True(particle.IsDisposed);
        Assert.Same(particle, particle.Scale(4f));
        Assert.Equal(0f, particle.UniformScale);
        Assert.Contains(_log.Infos, l => l == "Effects: Scale refused; Particle 'Embers' is disposed");
        Assert.DoesNotContain(_log.Infos, l => l.Contains("scale 4", StringComparison.Ordinal));
    }

    [Fact]
    public void Sequence_two_endpoints_round_trip_and_stay_borrowed()
    {
        var seq = Assert.IsType<SequenceEffectHandle>(
            Assets.Borrow(AssetKind.Sequence, "AB_Cardinal_SummonOrb_Beam_Buff", _owner, "example.effects.test"));
        var start = new EffectVec3(10f, 2f, 3f);
        var end = new EffectVec3(15f, 2f, 3f);
        seq.Start = start;
        seq.End = end;
        seq.Tint = new EffectColor(0.25f, 0.55f, 1f);
        Assert.True(seq.Enabled);
        Assert.Equal(EffectHandleState.Borrowed, seq.State);
        Assert.Equal(start, seq.Start);
        Assert.Equal(end, seq.End);
        Assert.Equal(start, seq.Position);

        var host = Assets.Host(seq, HostKind.RenderTexture);
        Assert.True(host.Enabled);
        Assert.Equal(EffectHandleState.Hosted, seq.State);
        Assert.Equal(start, seq.Start);
        Assert.Equal(end, seq.End);
        host.Dispose();
        Assert.Equal(EffectHandleState.Borrowed, seq.State);
        Assert.Equal(start, seq.Start);
        Assert.Equal(end, seq.End);
        seq.Dispose();
    }

    [Fact]
    public void RenderTexture_host_moves_layer_and_restore_on_dispose()
    {
        const string name = "AB_Cardinal_SummonOrb_Beam_Buff";
        var seq = Assert.IsType<SequenceEffectHandle>(
            Assets.Borrow(AssetKind.Sequence, name, _owner, "example.effects.test"));
        seq.Start = new EffectVec3(0f, 0f, 0f);
        seq.End = new EffectVec3(5f, 0f, 0f);
        Assert.Equal(0, _client.ObjectLayer(name));

        var host = Assets.Host(seq, HostKind.RenderTexture);
        Assert.True(host.Enabled);
        Assert.False(host.IsDisposed);
        Assert.Equal(31, _client.ObjectLayer(name));
        Assert.Equal(EffectHandleState.Hosted, seq.State);

        host.Dispose();
        host.Dispose();
        Assert.True(host.IsDisposed);
        Assert.Equal(0, _client.ObjectLayer(name));
        Assert.Equal(EffectHandleState.Borrowed, seq.State);
        Assert.True(_client.HasObject(name));

        seq.Dispose();
        Assert.Equal(EffectHandleState.Disposed, seq.State);
    }

    [Fact]
    public void Volume_intensity_and_smoothness_round_trip()
    {
        var volume = Assert.IsType<VolumeHandle>(
            Assets.Borrow(AssetKind.Volume, "MenuGold", _owner, "example.effects.test"));
        volume.Tint = new EffectColor(1f, 0.55f, 0f);
        volume.Intensity = 0.75f;
        volume.Smoothness = 0.6f;
        Assert.Equal(0.75f, volume.Intensity);
        Assert.Equal(0.6f, volume.Smoothness);
        volume.Dispose();
    }

    [Fact]
    public void Borrow_requires_a_name()
    {
        Assert.Throws<ArgumentException>(() => Assets.Borrow(AssetKind.Image, " "));
    }

    [Fact]
    public void Sequence_play_entity_and_end_marker_are_destroyed_on_dispose()
    {
        Assets.BindSequencerMarkers = true;
        var seq = Assert.IsType<SequenceEffectHandle>(
            Assets.Borrow(AssetKind.Sequence, "AB_Cardinal_SummonOrb_Beam_Buff", _owner, "example.effects.test"));
        seq.SetEndpoints(new EffectVec3(0f, 0f, 0f), new EffectVec3(5f, 0f, 0f));
        Assert.True(seq.SequenceEntityId != 0);
        Assert.True(seq.EndMarkerId != 0);
        Assert.Equal(SequencePlayKind.PoseIdentity, _client.LastPlayKind);
        int seqId = seq.SequenceEntityId;
        int markerId = seq.EndMarkerId;
        seq.Dispose();
        Assert.Contains(seqId, _client.DestroyedSequenceIds);
        Assert.Contains(markerId, _client.DestroyedMarkerIds);
        Assert.Equal(0, seq.SequenceEntityId);
        Assert.Equal(0, seq.EndMarkerId);
    }

    [Fact]
    public void Sequence_play_is_pose_identity_and_binds_end_marker_as_SequenceInstance_target()
    {
        Assets.BindSequencerMarkers = true;
        var start = new EffectVec3(10f, 2f, 3f);
        var end = new EffectVec3(15f, 2f, 3f);
        var seq = Assert.IsType<SequenceEffectHandle>(
            Assets.Borrow(AssetKind.Sequence, "AB_Cardinal_SummonOrb_Beam_Buff", _owner, "example.effects.test"));
        seq.SetEndpoints(start, end);
        Assert.Equal(1, _client.PlaySequenceCalls);
        Assert.Equal(SequencePlayKind.PoseIdentity, _client.LastPlayKind);
        Assert.Equal(start, _client.LastPlayPosition);
        Assert.True(seq.EndMarkerId != 0);
        Assert.Equal("SequenceInstance.Target/SecondaryTarget", _client.LastBeamPath);
        Assert.Contains(
            _log.Infos,
            l => l.StartsWith("Effects: sequence beam SequenceInstance.Target/SecondaryTarget", StringComparison.Ordinal)
                 && l.Contains("start=" + start, StringComparison.Ordinal)
                 && l.Contains("end=" + end, StringComparison.Ordinal)
                 && l.Contains("marker=" + seq.EndMarkerId, StringComparison.Ordinal));
        Assert.NotEqual(SequencePlayKind.TwoPointMarker, _client.LastPlayKind);
        seq.Dispose();
        Assert.Equal(1, _client.PlaySequenceCalls);
    }

    [Fact]
    public void Sequence_Start_then_End_plays_once_with_both_ends()
    {
        Assets.BindSequencerMarkers = true;
        var start = new EffectVec3(10f, 2f, 3f);
        var end = new EffectVec3(15f, 2f, 3f);
        var seq = Assert.IsType<SequenceEffectHandle>(
            Assets.Borrow(AssetKind.Sequence, "AB_Cardinal_SummonOrb_Beam_Buff", _owner, "example.effects.test"));
        seq.Start = start;
        Assert.Equal(0, _client.PlaySequenceCalls);
        Assert.Equal(0, seq.SequenceEntityId);
        seq.End = end;
        Assert.Equal(1, _client.PlaySequenceCalls);
        Assert.Equal(SequencePlayKind.PoseIdentity, _client.LastPlayKind);
        Assert.Equal(start, _client.LastPlayPosition);
        Assert.Equal(start, _client.LastAppliedStart);
        Assert.Equal(end, _client.LastAppliedEnd);
        Assert.Equal(start, _client.LastPosePosition);
        Assert.True(seq.SequenceEntityId != 0);
        Assert.True(seq.EndMarkerId != 0);
        Assert.Contains(
            _log.Infos,
            l => l.StartsWith("Effects: sequence beam SequenceInstance.Target/SecondaryTarget", StringComparison.Ordinal)
                 && l.Contains("start=" + start, StringComparison.Ordinal)
                 && l.Contains("end=" + end, StringComparison.Ordinal));
        seq.Dispose();
        Assert.Equal(1, _client.PlaySequenceCalls);
    }

    [Fact]
    public void Sequence_End_then_Start_plays_once_with_both_ends()
    {
        Assets.BindSequencerMarkers = true;
        var start = new EffectVec3(-2131.84f, 6f, -3168.08f);
        var end = new EffectVec3(-2129.24f, 6f, -3162.08f);
        var seq = Assert.IsType<SequenceEffectHandle>(
            Assets.Borrow(AssetKind.Sequence, "AB_Cardinal_SummonOrb_Beam_Buff", _owner, "example.effects.test"));
        seq.End = end;
        Assert.Equal(0, _client.PlaySequenceCalls);
        seq.Start = start;
        Assert.Equal(1, _client.PlaySequenceCalls);
        Assert.Equal(start, _client.LastPlayPosition);
        Assert.Equal(start, _client.LastAppliedStart);
        Assert.Equal(end, _client.LastAppliedEnd);
        Assert.DoesNotContain(
            _log.Infos,
            l => l.Contains("end=(0, 0, 0)", StringComparison.Ordinal) || l.Contains("end=(0,0,0)", StringComparison.Ordinal));
        Assert.Contains(
            _log.Infos,
            l => l.StartsWith("Effects: sequence beam SequenceInstance.Target/SecondaryTarget", StringComparison.Ordinal)
                 && l.Contains("start=" + start, StringComparison.Ordinal)
                 && l.Contains("end=" + end, StringComparison.Ordinal));
        int seqId = seq.SequenceEntityId;
        seq.Dispose();
        Assert.Contains(seqId, _client.DestroyedSequenceIds);
    }

    [Fact]
    public void Sequence_single_setter_does_not_play_until_the_other_end_or_explicit_Play()
    {
        var seq = Assert.IsType<SequenceEffectHandle>(
            Assets.Borrow(AssetKind.Sequence, "AB_Cardinal_SummonOrb_Beam_Buff", _owner, "example.effects.test"));
        seq.Start = new EffectVec3(1f, 0f, 0f);
        Assert.Equal(0, _client.PlaySequenceCalls);
        seq.Play();
        Assert.Equal(1, _client.PlaySequenceCalls);
        seq.Play();
        Assert.Equal(1, _client.PlaySequenceCalls);
        seq.Dispose();
    }

    [Fact]
    public void Sequence_SetEndpoints_plays_immediately()
    {
        var start = new EffectVec3(2f, 0f, 0f);
        var end = new EffectVec3(8f, 0f, 0f);
        var seq = Assert.IsType<SequenceEffectHandle>(
            Assets.Borrow(AssetKind.Sequence, "AB_Cardinal_SummonOrb_Beam_Buff", _owner, "example.effects.test"));
        seq.SetEndpoints(start, end);
        Assert.Equal(1, _client.PlaySequenceCalls);
        Assert.Equal(start, _client.LastPlayPosition);
        Assert.Equal(end, _client.LastAppliedEnd);
        seq.Dispose();
    }

    [Fact]
    public void Sequence_later_Start_reposes_and_later_End_moves_the_marker_without_replay()
    {
        Assets.BindSequencerMarkers = true;
        var start = new EffectVec3(0f, 1f, 0f);
        var end = new EffectVec3(4f, 1f, 0f);
        var movedStart = new EffectVec3(1f, 1f, 0f);
        var movedEnd = new EffectVec3(9f, 1f, 0f);
        var seq = Assert.IsType<SequenceEffectHandle>(
            Assets.Borrow(AssetKind.Sequence, "AB_Cardinal_SummonOrb_Beam_Buff", _owner, "example.effects.test"));
        seq.SetEndpoints(start, end);
        int seqId = seq.SequenceEntityId;
        int markerId = seq.EndMarkerId;
        Assert.True(seqId != 0);
        Assert.True(markerId != 0);

        seq.Start = movedStart;
        Assert.Equal(1, _client.PlaySequenceCalls);
        Assert.Equal(movedStart, seq.Start);
        Assert.Equal(movedStart, _client.LastAppliedStart);
        Assert.Equal(movedStart, _client.LastPosePosition);
        Assert.Equal(end, _client.LastAppliedEnd);
        Assert.Equal(seqId, seq.SequenceEntityId);

        seq.End = movedEnd;
        Assert.Equal(1, _client.PlaySequenceCalls);
        Assert.Equal(movedEnd, seq.End);
        Assert.Equal(movedEnd, _client.LastAppliedEnd);
        Assert.Equal(markerId, seq.EndMarkerId);
        seq.Dispose();
        Assert.Contains(seqId, _client.DestroyedSequenceIds);
        Assert.Contains(markerId, _client.DestroyedMarkerIds);
    }

    [Fact]
    public void Sequence_without_SequenceInstance_falls_back_to_hybrid_SetPosition()
    {
        Assets.BindSequencerMarkers = true;
        _client.MissingSequenceInstance = true;
        var start = new EffectVec3(1f, 0f, 0f);
        var end = new EffectVec3(4f, 0f, 0f);
        var seq = Assert.IsType<SequenceEffectHandle>(
            Assets.Borrow(AssetKind.Sequence, "AB_Cardinal_SummonOrb_Beam_Buff", _owner, "example.effects.test"));
        seq.SetEndpoints(start, end);
        Assert.Equal(SequencePlayKind.PoseIdentity, _client.LastPlayKind);
        Assert.Equal(0, seq.EndMarkerId);
        Assert.Equal("hybrid SetPosition fallback", _client.LastBeamPath);
        Assert.Contains(
            _log.Infos,
            l => l.StartsWith("Effects: sequence beam hybrid SetPosition fallback", StringComparison.Ordinal));
        Assert.Equal(start, _client.LastAppliedStart);
        Assert.Equal(end, _client.LastAppliedEnd);
        seq.Dispose();
        Assert.Empty(_client.DestroyedMarkerIds);
    }

    [Fact]
    public void Sequence_applies_hybrid_endpoints_after_the_driver_appears()
    {
        Assets.BindSequencerMarkers = true;
        _client.DriverDelayFrames = 2;
        var start = new EffectVec3(1f, 2f, 3f);
        var end = new EffectVec3(6f, 2f, 3f);
        var seq = Assert.IsType<SequenceEffectHandle>(
            Assets.Borrow(AssetKind.Sequence, "AB_Cardinal_SummonOrb_Beam_Buff", _owner, "example.effects.test"));
        seq.SetEndpoints(start, end);
        seq.Tint = new EffectColor(0.25f, 0.55f, 1f);
        Assert.False(seq.HasDriver);
        Assert.True(seq.EndMarkerId != 0);
        Assert.Equal(start, _client.LastAppliedStart);
        Assert.Equal(end, _client.LastAppliedEnd);

        MainThread.Drain();
        Assert.False(seq.HasDriver);
        MainThread.Drain();
        Assert.True(seq.HasDriver);
        Assert.True(seq.TintAppliedToDriver);
        Assert.Equal(start, _client.LastAppliedStart);
        Assert.Equal(end, _client.LastAppliedEnd);
        Assert.True(_client.EndpointApplyCount >= 1);
        seq.Dispose();
    }

    [Fact]
    public void Sequence_SetEndpoints_moves_the_far_end_after_the_driver_appears()
    {
        Assets.BindSequencerMarkers = true;
        var start = new EffectVec3(0f, 0f, 0f);
        var end = new EffectVec3(5f, 0f, 0f);
        var moved = new EffectVec3(7f, 0f, 0f);
        var seq = Assert.IsType<SequenceEffectHandle>(
            Assets.Borrow(AssetKind.Sequence, "AB_Cardinal_SummonOrb_Beam_Buff", _owner, "example.effects.test"));
        seq.SetEndpoints(start, end);
        Assert.True(seq.HasDriver);
        Assert.Equal(end, _client.LastAppliedEnd);
        int markerId = seq.EndMarkerId;
        seq.SetEndpoints(start, moved);
        Assert.Equal(1, _client.PlaySequenceCalls);
        Assert.Equal(start, seq.Start);
        Assert.Equal(moved, seq.End);
        Assert.Equal(start, _client.LastAppliedStart);
        Assert.Equal(moved, _client.LastAppliedEnd);
        Assert.Equal(markerId, seq.EndMarkerId);
        seq.Dispose();
    }

    [Fact]
    public void Sequence_poll_stops_after_bind_and_late_driver_keeps_writing()
    {
        var start = new EffectVec3(0f, 1f, 0f);
        var end = new EffectVec3(4f, 1f, 0f);
        var seq = Assert.IsType<SequenceEffectHandle>(
            Assets.Borrow(AssetKind.Sequence, "AB_Cardinal_SummonOrb_Beam_Buff", _owner, "example.effects.test"));
        seq.SetEndpoints(start, end);
        Assert.True(seq.HasDriver);
        MainThread.Drain();
        Assert.False(seq.IsPolling);
        int n = _client.EndpointApplyCount;
        MainThread.Drain();
        MainThread.Drain();
        Assert.Equal(n, _client.EndpointApplyCount);
        Assert.False(seq.IsPolling);
        SequenceLateDriver.TickForTests();
        Assert.Equal(1, seq.LateWriteCount);
        seq.Dispose();
    }

    [Fact]
    public void Sequence_late_watch_first_write_verdict_and_summary_log()
    {
        var seq = Assert.IsType<SequenceEffectHandle>(
            Assets.Borrow(AssetKind.Sequence, "AB_Cardinal_SummonOrb_Beam_Buff", _owner, "example.effects.test"));
        seq.SetEndpoints(new EffectVec3(0f, 1f, 0f), new EffectVec3(4f, 1f, 0f));
        Assert.Contains(_log.Infos, l => l.StartsWith("Effects: sequence late-frame watch seq=", StringComparison.Ordinal));
        SequenceLateDriver.TickForTests();
        Assert.Equal(1, seq.LastLateWriteFrame);
        Assert.Contains(_log.Infos, l => l.StartsWith("Effects: sequence late-frame first write game=", StringComparison.Ordinal));
        SequenceLateDriver.TickForTests();
        SequenceLateDriver.TickForTests();
        Assert.Equal(SequenceLateDriver.VerdictOwnedLine, seq.LateWriteVerdict);
        Assert.Contains(
            _log.Infos,
            l => l.StartsWith("Effects: sequence beam drawn by owned LineRenderer", StringComparison.Ordinal));
        seq.Dispose();
        Assert.Contains(
            _log.Infos,
            l => l.Contains("late writes=3", StringComparison.Ordinal)
                 && l.Contains("lastFrame=3", StringComparison.Ordinal));
    }

    [Fact]
    public void Sequence_tint_is_applied_once_on_bind()
    {
        _client.DriverDelayFrames = 1;
        var seq = Assert.IsType<SequenceEffectHandle>(
            Assets.Borrow(AssetKind.Sequence, "AB_Cardinal_SummonOrb_Beam_Buff", _owner, "example.effects.test"));
        seq.SetEndpoints(new EffectVec3(0f, 0f, 0f), new EffectVec3(5f, 0f, 0f));
        seq.Tint = new EffectColor(0.25f, 0.55f, 1f);
        Assert.Equal(0, _client.TintApplyCount);
        Assert.Equal(0, _client.CloneMaterialWriteCount);
        MainThread.Drain();
        Assert.True(seq.HasDriver);
        Assert.True(seq.TintAppliedToDriver);
        Assert.Equal(0, _client.TintApplyCount);
        Assert.Equal(0, _client.CloneMaterialWriteCount);
        MainThread.Drain();
        MainThread.Drain();
        SequenceLateDriver.TickForTests();
        Assert.Equal(0, _client.TintApplyCount);
        Assert.Equal(0, _client.CloneMaterialWriteCount);
        seq.Dispose();
    }

    [Fact]
    public void Sequence_finder_picks_the_unbound_clone()
    {
        _client.DriverPool.Add("Beam_Cardinal_Orb_Guardian_01(Clone)");
        _client.DriverPool.Add("Beam_Cardinal_Orb_Guardian_01(Clone)#2");
        var first = Assert.IsType<SequenceEffectHandle>(
            Assets.Borrow(AssetKind.Sequence, "AB_Cardinal_SummonOrb_Beam_Buff", _owner, "example.effects.test"));
        first.SetEndpoints(new EffectVec3(0f, 0f, 0f), new EffectVec3(1f, 0f, 0f));
        Assert.Equal("Beam_Cardinal_Orb_Guardian_01(Clone)", first.DriverName);
        var second = Assert.IsType<SequenceEffectHandle>(
            Assets.Borrow(AssetKind.Sequence, "AB_Cardinal_SummonOrb_Beam_Buff", _owner, "example.effects.test"));
        second.SetEndpoints(new EffectVec3(0f, 0f, 0f), new EffectVec3(2f, 0f, 0f));
        Assert.Equal("Beam_Cardinal_Orb_Guardian_01(Clone)#2", second.DriverName);
        Assert.NotEqual(first.DriverName, second.DriverName);
        first.Dispose();
        second.Dispose();
    }

    [Fact]
    public void Sequence_second_play_rebinds_a_pooled_driver()
    {
        var first = Assert.IsType<SequenceEffectHandle>(
            Assets.Borrow(AssetKind.Sequence, "AB_Cardinal_SummonOrb_Beam_Buff", _owner, "example.effects.test"));
        first.SetEndpoints(new EffectVec3(0f, 0f, 0f), new EffectVec3(1f, 0f, 0f));
        Assert.True(first.HasDriver);
        string name = first.DriverName;
        var second = Assert.IsType<SequenceEffectHandle>(
            Assets.Borrow(AssetKind.Sequence, "AB_Cardinal_SummonOrb_Beam_Buff", _owner, "example.effects.test"));
        second.SetEndpoints(new EffectVec3(0f, 0f, 0f), new EffectVec3(2f, 0f, 0f));
        Assert.True(second.HasDriver);
        Assert.Equal(name, second.DriverName);
        Assert.False(first.HasDriver);
        Assert.Contains(_log.Infos, l => l == "Effects: sequence driver reused from pool");
        Assert.True(second.HasOwnedLine);
        first.Dispose();
        second.Dispose();
    }

    [Fact]
    public void Sequence_RemoveAll_then_play_binds()
    {
        var first = Assert.IsType<SequenceEffectHandle>(
            Assets.Borrow(AssetKind.Sequence, "AB_Cardinal_SummonOrb_Beam_Buff", _owner, "example.effects.test"));
        first.SetEndpoints(new EffectVec3(0f, 1f, 0f), new EffectVec3(4f, 1f, 0f));
        SequenceLateDriver.TickForTests();
        Assets.RemoveAll(_owner);
        Assert.True(first.IsDisposed);
        Assert.Contains(
            _log.Infos,
            l => l.Contains("late writes=", StringComparison.Ordinal)
                 && l.Contains("lastFrame=", StringComparison.Ordinal));
        int summaries = 0;
        for (int i = 0; i < _log.Infos.Count; i++)
        {
            if (_log.Infos[i].Contains("late writes=", StringComparison.Ordinal))
                summaries++;
        }

        Assert.Equal(1, summaries);

        var second = Assert.IsType<SequenceEffectHandle>(
            Assets.Borrow(AssetKind.Sequence, "AB_Cardinal_SummonOrb_Beam_Buff", _owner, "example.effects.test"));
        second.SetEndpoints(new EffectVec3(1f, 1f, 0f), new EffectVec3(5f, 1f, 0f));
        Assert.True(second.HasDriver);
        Assert.True(second.HasOwnedLine);
        second.Dispose();
    }

    [Fact]
    public void Sequence_recycled_driver_is_dropped()
    {
        var seq = Assert.IsType<SequenceEffectHandle>(
            Assets.Borrow(AssetKind.Sequence, "AB_Cardinal_SummonOrb_Beam_Buff", _owner, "example.effects.test"));
        seq.SetEndpoints(new EffectVec3(0f, 0f, 0f), new EffectVec3(3f, 0f, 0f));
        Assert.True(seq.HasDriver);
        _client.RecycleDrivers();
        SequenceLateDriver.TickForTests();
        Assert.False(seq.HasDriver);
        Assert.Contains(_log.Infos, l => l == DriverRecycleState.RecycledLog);
        seq.Dispose();
    }

    [Fact]
    public void Sequence_summary_logged_once_on_dispose()
    {
        var seq = Assert.IsType<SequenceEffectHandle>(
            Assets.Borrow(AssetKind.Sequence, "AB_Cardinal_SummonOrb_Beam_Buff", _owner, "example.effects.test"));
        seq.SetEndpoints(new EffectVec3(0f, 1f, 0f), new EffectVec3(4f, 1f, 0f));
        SequenceLateDriver.TickForTests();
        SequenceLateDriver.TickForTests();
        seq.Dispose();
        seq.Dispose();
        int n = 0;
        for (int i = 0; i < _log.Infos.Count; i++)
        {
            if (_log.Infos[i].Contains("late writes=", StringComparison.Ordinal))
                n++;
        }

        Assert.Equal(1, n);
    }

    [Fact]
    public void Overlay_host_dispose_reparents_borrow_to_plugin_root()
    {
        var handle = Assets.Borrow(AssetKind.Image, "PassiveFrame_Normal", _owner, "example.effects.test");
        var host = Assets.Host(handle, HostKind.Overlay);
        Assert.False(_client.OverlayReturnedBorrowToRoot);
        host.Dispose();
        Assert.True(_client.OverlayReturnedBorrowToRoot);
        Assert.False(handle.IsDisposed);
        handle.Dispose();
    }

    [Fact]
    public void TransformWritePath_selects_generic_SetComponentData_for_Unity_Transforms()
    {
        Assert.True(TransformWritePath.UseGenericSetComponentData("Unity.Transforms.LocalToWorld"));
        Assert.True(TransformWritePath.UseGenericSetComponentData("Unity.Transforms.Translation"));
        Assert.True(TransformWritePath.UseGenericSetComponentData("Unity.Transforms.Rotation"));
        Assert.True(TransformWritePath.UseGenericSetComponentData("LocalToWorld"));
        Assert.False(TransformWritePath.UseGenericSetComponentData("ProjectM.Follower"));
        Assert.False(TransformWritePath.UseGenericSetComponentData(null));
        Assert.True(TransformWritePath.ShouldAttemptWrite(alreadyFailedThisHandle: false));
        Assert.False(TransformWritePath.ShouldAttemptWrite(alreadyFailedThisHandle: true));

        var seq = Assert.IsType<SequenceEffectHandle>(
            Assets.Borrow(AssetKind.Sequence, "AB_Cardinal_SummonOrb_Beam_Buff", _owner, "example.effects.test"));
        seq.SetEndpoints(new EffectVec3(1f, 0f, 0f), new EffectVec3(4f, 0f, 0f));
        Assert.Equal(TransformWritePath.GenericSetComponentData, seq.LastTransformWritePath);
        Assert.Equal(0, seq.TransformWriteWarnings);
        seq.Dispose();
    }

    [Fact]
    public void Sequence_transform_write_failure_logs_once_per_handle_and_stops_retrying()
    {
        _client.FailTransformWrites = true;
        var seq = Assert.IsType<SequenceEffectHandle>(
            Assets.Borrow(AssetKind.Sequence, "AB_Cardinal_SummonOrb_Beam_Buff", _owner, "example.effects.test"));
        seq.SetEndpoints(new EffectVec3(1f, 0f, 0f), new EffectVec3(4f, 0f, 0f));
        Assert.Equal(1, seq.TransformWriteWarnings);
        Assert.Single(_log.Warnings, w => w.Contains("LocalToWorld write threw", StringComparison.Ordinal));
        MainThread.Drain();
        MainThread.Drain();
        MainThread.Drain();
        Assert.Equal(1, seq.TransformWriteWarnings);
        Assert.Single(_log.Warnings, w => w.Contains("LocalToWorld write threw", StringComparison.Ordinal));
        seq.Dispose();
    }

    [Fact]
    public void Sequence_driver_is_kept_active_after_bind()
    {
        _client.DriverStartsInactive = true;
        var seq = Assert.IsType<SequenceEffectHandle>(
            Assets.Borrow(AssetKind.Sequence, "AB_Cardinal_SummonOrb_Beam_Buff", _owner, "example.effects.test"));
        seq.SetEndpoints(new EffectVec3(0f, 0f, 0f), new EffectVec3(5f, 0f, 0f));
        Assert.True(seq.HasDriver);
        Assert.True(seq.DriverActive);
        Assert.Contains(
            _log.Infos,
            l => l.Contains("SetActive(true)", StringComparison.Ordinal)
                 && l.Contains("activeSelf=False", StringComparison.Ordinal));
        seq.Dispose();
    }

    [Fact]
    public void Sequence_markers_are_off_by_default()
    {
        Assert.False(Assets.BindSequencerMarkers);
        var seq = Assert.IsType<SequenceEffectHandle>(
            Assets.Borrow(AssetKind.Sequence, "AB_Cardinal_SummonOrb_Beam_Buff", _owner, "example.effects.test"));
        seq.SetEndpoints(new EffectVec3(0f, 1f, 0f), new EffectVec3(4f, 1f, 0f));
        Assert.Equal(0, seq.EndMarkerId);
        Assert.Equal(SequenceLateDriver.VerdictOwnedLine, _client.LastBeamPath);
        Assert.True(seq.HasOwnedLine);
        Assert.DoesNotContain(
            _log.Infos,
            l => l.StartsWith("Effects: sequence beam SequenceInstance.Target/SecondaryTarget", StringComparison.Ordinal));
        seq.Dispose();
    }

    [Fact]
    public void Sequence_late_frame_writes_while_bound_and_not_when_unbound_or_disposed()
    {
        var start = new EffectVec3(0f, 1f, 0f);
        var end = new EffectVec3(4f, 1f, 0f);
        var seq = Assert.IsType<SequenceEffectHandle>(
            Assets.Borrow(AssetKind.Sequence, "AB_Cardinal_SummonOrb_Beam_Buff", _owner, "example.effects.test"));
        seq.SetEndpoints(start, end);
        Assert.True(seq.HasDriver);
        Assert.False(seq.HybridLastWrite);
        Assert.Equal(0, seq.LateWriteCount);

        SequenceLateDriver.TickForTests();
        Assert.True(seq.HybridLastWrite);
        Assert.Equal(1, seq.LateWriteCount);
        Assert.Equal(1, seq.SequencerOverwriteCount);
        Assert.Equal(start, _client.LastAppliedStart);
        Assert.Equal(end, _client.LastAppliedEnd);
        Assert.Contains(
            _log.Infos,
            l => l.Contains("sequence hybrid Positions before=", StringComparison.Ordinal)
                 && l.Contains("sequencerMatched=False", StringComparison.Ordinal));

        SequenceLateDriver.TickForTests();
        Assert.Equal(2, seq.LateWriteCount);

        seq.Dispose();
        int afterDispose = seq.LateWriteCount;
        SequenceLateDriver.TickForTests();
        Assert.Equal(afterDispose, seq.LateWriteCount);
        Assert.Contains(
            _log.Infos,
            l => l.Contains("late writes=" + afterDispose, StringComparison.Ordinal)
                 && l.Contains("sequencer overwrote before us " + afterDispose + " frames", StringComparison.Ordinal));
    }

    [Fact]
    public void Sequence_late_frame_does_not_write_before_the_driver_binds()
    {
        _client.DriverDelayFrames = 2;
        var seq = Assert.IsType<SequenceEffectHandle>(
            Assets.Borrow(AssetKind.Sequence, "AB_Cardinal_SummonOrb_Beam_Buff", _owner, "example.effects.test"));
        seq.SetEndpoints(new EffectVec3(1f, 0f, 0f), new EffectVec3(5f, 0f, 0f));
        Assert.False(seq.HasDriver);
        SequenceLateDriver.TickForTests();
        Assert.Equal(0, seq.LateWriteCount);
        Assert.False(seq.HybridLastWrite);
        MainThread.Drain();
        MainThread.Drain();
        Assert.True(seq.HasDriver);
        SequenceLateDriver.TickForTests();
        Assert.Equal(1, seq.LateWriteCount);
        seq.Dispose();
    }

    [Fact]
    public void Sequence_late_write_verdict_is_late_frame_when_the_sequencer_overwrites()
    {
        var seq = Assert.IsType<SequenceEffectHandle>(
            Assets.Borrow(AssetKind.Sequence, "AB_Cardinal_SummonOrb_Beam_Buff", _owner, "example.effects.test"));
        seq.SetEndpoints(new EffectVec3(0f, 1f, 0f), new EffectVec3(4f, 1f, 0f));
        SequenceLateDriver.TickForTests();
        SequenceLateDriver.TickForTests();
        SequenceLateDriver.TickForTests();
        Assert.Equal(3, seq.LateWriteCount);
        Assert.Equal(3, seq.SequencerOverwriteCount);
        Assert.Equal(SequenceLateDriver.VerdictOwnedLine, seq.LateWriteVerdict);
        Assert.Contains(
            _log.Infos,
            l => l.StartsWith("Effects: sequence beam drawn by owned LineRenderer", StringComparison.Ordinal));
        seq.Dispose();
    }

    [Fact]
    public void Sequence_late_write_verdict_is_owned_line_even_when_sequencer_matches()
    {
        _client.SequencerHonoursMarkers = true;
        var seq = Assert.IsType<SequenceEffectHandle>(
            Assets.Borrow(AssetKind.Sequence, "AB_Cardinal_SummonOrb_Beam_Buff", _owner, "example.effects.test"));
        seq.SetEndpoints(new EffectVec3(0f, 1f, 0f), new EffectVec3(4f, 1f, 0f));
        SequenceLateDriver.TickForTests();
        SequenceLateDriver.TickForTests();
        SequenceLateDriver.TickForTests();
        Assert.Equal(3, seq.LateWriteCount);
        Assert.Equal(0, seq.SequencerOverwriteCount);
        Assert.Equal(SequenceLateDriver.VerdictOwnedLine, seq.LateWriteVerdict);
        Assert.Contains(
            _log.Infos,
            l => l.StartsWith("Effects: sequence beam drawn by owned LineRenderer", StringComparison.Ordinal));
        seq.Dispose();
    }

    /// <summary>
    /// R27 finding 24.13, stated honestly: this measures <b>the fake's</b>
    /// <c>DriveLateWrite</c>, not <c>UnityEffectRuntime</c>'s, which the test project cannot
    /// compile. What it does pin is the shape both share — the inspect/readback strings are
    /// formatted once per handle and the steady-state loop adds no boxing, closures or
    /// collections. The real path's allocation profile is a live check, not a unit test.
    /// </summary>
    [Fact]
    public void Sequence_late_write_allocates_nothing_after_the_first_frame()
    {
        var seq = Assert.IsType<SequenceEffectHandle>(
            Assets.Borrow(AssetKind.Sequence, "AB_Cardinal_SummonOrb_Beam_Buff", _owner, "example.effects.test"));
        seq.SetEndpoints(new EffectVec3(0f, 1f, 0f), new EffectVec3(4f, 1f, 0f));
        SequenceLateDriver.TickForTests();
        Assert.True(SequenceLateDriver.InspectFormatCount > 0);
        for (int i = 0; i < 5; i++)
            SequenceLateDriver.TickForTests();

        int formats = SequenceLateDriver.InspectFormatCount;
        Assert.Equal(2, formats);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++)
            SequenceLateDriver.TickForTests();
        long after = GC.GetAllocatedBytesForCurrentThread();
        long allocated = after - before;

        Assert.Equal(2, SequenceLateDriver.InspectFormatCount);
        Assert.Equal(0L, allocated);
        Assert.Equal(106, seq.LateWriteCount);
        seq.Dispose();
    }

    [Fact]
    public void Sequence_owned_line_defaults_to_thin_unlit_and_does_not_copy_the_prefab()
    {
        _client.DriverDelayFrames = 2;
        _client.DriverLineWidth = 0.25f;
        var seq = Assert.IsType<SequenceEffectHandle>(
            Assets.Borrow(AssetKind.Sequence, "AB_Cardinal_SummonOrb_Beam_Buff", _owner, "example.effects.test"));
        seq.SetEndpoints(new EffectVec3(0f, 1f, 0f), new EffectVec3(4f, 1f, 0f));
        Assert.True(seq.HasOwnedLine);
        Assert.False(seq.HasDriver);
        Assert.Equal(SequenceLateDriver.OwnedLineDefaultWidth, seq.OwnedLineWidth);
        Assert.Equal(SequenceLateDriver.OwnedLineDefaultWidth, _client.LastOwnedLineWidth);
        Assert.Equal(SequenceLateDriver.OwnedLineDefaultWidth, _client.LastOwnedLineStartWidth);
        Assert.Equal(SequenceLateDriver.OwnedLineDefaultWidth, _client.LastOwnedLineEndWidth);
        Assert.Equal(1f, _client.LastOwnedLineWidthMultiplier);
        Assert.True(_client.LastOwnedLineWidthCurveConstant);
        Assert.Equal(1f, _client.LastOwnedLineLossyScale);
        Assert.True(_client.LastOwnedLineShaderSupported);
        Assert.Equal(3000, _client.LastOwnedLineRenderQueue);
        Assert.Equal("HDRP/Unlit", seq.OwnedLineMaterialName);
        Assert.Equal(0, _client.OwnedLineStyleCopyCount);
        Assert.Equal(0, _client.CloneMaterialWriteCount);
        Assert.Contains(
            _log.Infos,
            l => l == SequenceLateDriver.FormatOwnedLineStyleLog("HDRP/Unlit", SequenceLateDriver.OwnedLineDefaultWidth));
        Assert.Contains(
            _log.Infos,
            l => l == SequenceLateDriver.FormatOwnedLineReadbackLog(
                SequenceLateDriver.OwnedLineDefaultWidth, 1f, 1f, 1f, 1f, "HDRP/Unlit", true, 3000));

        MainThread.Drain();
        Assert.Equal(0, _client.OwnedLineStyleCopyCount);
        Assert.Equal(SequenceLateDriver.OwnedLineDefaultWidth, _client.LastOwnedLineWidth);

        MainThread.Drain();
        Assert.True(seq.HasDriver);
        Assert.Equal(0, _client.OwnedLineStyleCopyCount);
        Assert.Equal(SequenceLateDriver.OwnedLineDefaultWidth, _client.LastOwnedLineWidth);
        Assert.Equal("HDRP/Unlit", _client.LastOwnedLineMaterial);

        SequenceLateDriver.TickForTests();
        SequenceLateDriver.TickForTests();
        SequenceLateDriver.TickForTests();
        Assert.Equal(0, _client.OwnedLineStyleCopyCount);
        seq.Dispose();
        Assert.False(seq.HasOwnedLine);
        Assert.Equal(1, _client.OwnedLineDestroyedCount);
    }

    [Fact]
    public void Sequence_LineWidth_and_LineColor_chain_before_and_after_Play()
    {
        var seq = Assert.IsType<SequenceEffectHandle>(
            Assets.Borrow(AssetKind.Sequence, "AB_Cardinal_SummonOrb_Beam_Buff", _owner, "example.effects.test"));
        var tint = new EffectColor(1f, 0.2f, 0.1f, 0.8f);
        seq.LineWidth(0.2f).LineColor(tint).SetEndpoints(new EffectVec3(0f, 1f, 0f), new EffectVec3(4f, 1f, 0f));
        Assert.Equal(0.2f, seq.OwnedLineWidth);
        Assert.Equal(tint, seq.Tint);
        Assert.Equal(0.2f, _client.LastOwnedLineWidth);
        Assert.Equal(0.2f, _client.LastOwnedLineStartWidth);
        Assert.Equal(0.2f, _client.LastOwnedLineEndWidth);
        Assert.Equal(1f, _client.LastOwnedLineWidthMultiplier);
        seq.LineWidth(0.05f);
        Assert.Equal(0.05f, seq.OwnedLineWidth);
        Assert.Equal(0.05f, _client.LastOwnedLineWidth);
        Assert.Equal(0.05f, _client.LastOwnedLineStartWidth);
        Assert.Equal(1f, _client.LastOwnedLineWidthMultiplier);
        seq.Dispose();
        seq.LineWidth(0.3f);
        Assert.Contains(_log.Infos, l => l.Contains("LineWidth refused", StringComparison.Ordinal));
    }

    [Fact]
    public void Sequence_UsePrefabLineMaterial_copies_the_clone_style_once()
    {
        _client.DriverDelayFrames = 1;
        _client.DriverLineWidth = 0.25f;
        var seq = Assert.IsType<SequenceEffectHandle>(
            Assets.Borrow(AssetKind.Sequence, "AB_Cardinal_SummonOrb_Beam_Buff", _owner, "example.effects.test"));
        seq.UsePrefabLineMaterial().SetEndpoints(new EffectVec3(0f, 1f, 0f), new EffectVec3(4f, 1f, 0f));
        Assert.True(seq.UsesPrefabLineMaterial);
        Assert.Equal(0, _client.OwnedLineStyleCopyCount);
        MainThread.Drain();
        Assert.True(seq.HasDriver);
        Assert.Equal(1, _client.OwnedLineStyleCopyCount);
        Assert.Equal(0.25f, _client.LastOwnedLineWidth);
        Assert.Equal("prefab", seq.OwnedLineMaterialName);
        Assert.Contains(
            _log.Infos,
            l => l == SequenceLateDriver.FormatOwnedLineStyleLog("prefab", 0.25f));
        SequenceLateDriver.TickForTests();
        Assert.Equal(1, _client.OwnedLineStyleCopyCount);
        Assert.Equal(0, _client.CloneMaterialWriteCount);
        Assert.Equal(0.25f, _client.LastOwnedLineStartWidth);
        Assert.Equal(1f, _client.LastOwnedLineWidthMultiplier);
        seq.Dispose();
    }

    [Fact]
    public void TintPreserveIntensity_keeps_source_hdr_and_takes_tint_hue()
    {
        var src = new EffectColor(2f, 0.1f, 0.1f, 1f);
        var blue = new EffectColor(0.25f, 0.55f, 1f, 1f);
        var tinted = EffectColor.TintPreserveIntensity(src, blue);
        Assert.True(Math.Abs(EffectColor.Max3(tinted.R, tinted.G, tinted.B) - 2f) < 1e-4f);
        Assert.True(tinted.B > tinted.R);
        Assert.True(tinted.B > tinted.G);
        var back = EffectColor.TintPreserveIntensity(tinted, src);
        Assert.True(Math.Abs(EffectColor.Max3(back.R, back.G, back.B) - 2f) < 1e-4f);
        Assert.True(back.R > back.B);
    }

    [Fact]
    public void Beam_Thin_is_owned_unlit_without_material_copy_or_glow()
    {
        var seq = Assets.Beam(BeamStyle.Thin, _owner, "example.effects.test");
        seq.SetEndpoints(new EffectVec3(0f, 1f, 0f), new EffectVec3(4f, 1f, 0f));
        Assert.Equal(BeamStyle.Thin, seq.Style);
        Assert.True(seq.HasOwnedLine);
        Assert.False(seq.HasGlowLine);
        Assert.Equal(0, _client.OwnedLineStyleCopyCount);
        Assert.Equal("HDRP/Unlit", seq.OwnedLineMaterialName);
        Assert.Equal(SequenceLateDriver.VerdictOwnedLine, seq.DrawPath);
        seq.Dispose();
        Assert.Equal(1, _client.OwnedLineDestroyedCount);
        Assert.Equal(0, _client.GlowLineDestroyedCount);
    }

    [Fact]
    public void Beam_GameRed_falls_back_to_Thin_when_no_castle_sequence()
    {
        var seq = Assets.Beam(BeamStyle.GameRed, _owner, "example.effects.test");
        Assert.Equal(BeamStyle.Thin, seq.Style);
        Assert.Contains(_log.Infos, l => l.Contains("Beam(GameRed) not found at runtime; using Thin", StringComparison.Ordinal));
        seq.Dispose();
    }

    [Fact]
    public void Beam_GameRed_copies_the_owned_line_material_once_and_creates_glow()
    {
        _client.Add(AssetKind.Sequence, "seq-red");
        _client.BeamPresets[BeamStyle.GameRed] = "seq-red";
        _client.DriverIsTrailSprite = false;
        _client.DriverLineWidth = 0.08f;
        Assert.Equal(BeamStyle.GameRed, Assets.PreferredBeamStyle());

        var seq = Assets.Beam(BeamStyle.GameRed, _owner, "example.effects.test");
        seq.SetEndpoints(new EffectVec3(0f, 1f, 0f), new EffectVec3(4f, 1f, 0f));
        Assert.Equal("seq-red", seq.Name);
        Assert.Equal(BeamStyle.GameRed, seq.Style);
        Assert.True(seq.HasOwnedLine);
        Assert.True(seq.HasGlowLine);
        Assert.Equal(BeamCatalog.DefaultGlowWidth, seq.OwnedGlowWidth);
        Assert.Equal(1, _client.OwnedLineStyleCopyCount);
        Assert.Equal("prefab", seq.OwnedLineMaterialName);
        Assert.Equal(0, _client.CloneMaterialWriteCount);
        Assert.Equal(SequenceLateDriver.VerdictOwnedGameMaterial, seq.DrawPath);
        Assert.Contains(_log.Infos, l => l == SequenceLateDriver.FormatDrawPathLog(SequenceLateDriver.VerdictOwnedGameMaterial));
        Assert.Contains(_log.Infos, l => l == SequenceLateDriver.FormatGlowLineLog(BeamCatalog.DefaultGlowWidth, BeamCatalog.DefaultGlowAlpha));

        SequenceLateDriver.TickForTests();
        Assert.Equal(1, _client.OwnedLineStyleCopyCount);

        seq.LineColor(new EffectColor(0.25f, 0.55f, 1f, 1f));
        float intensity = EffectColor.Max3(seq.LastTintedColor.R, seq.LastTintedColor.G, seq.LastTintedColor.B);
        float srcI = EffectColor.Max3(_client.DriverMaterialColor.R, _client.DriverMaterialColor.G, _client.DriverMaterialColor.B);
        Assert.True(Math.Abs(intensity - srcI) < 1e-4f);
        Assert.True(seq.LastTintedColor.B > seq.LastTintedColor.R);

        seq.Dispose();
        Assert.False(seq.HasGlowLine);
        Assert.False(seq.HasOwnedLine);
        Assert.Equal(1, _client.OwnedLineDestroyedCount);
        Assert.Equal(1, _client.GlowLineDestroyedCount);
    }

    [Fact]
    public void Beam_GameRed_skips_glow_when_the_copied_material_already_glows()
    {
        _client.Add(AssetKind.Sequence, "seq-red");
        _client.BeamPresets[BeamStyle.GameRed] = "seq-red";
        _client.DriverIsTrailSprite = false;
        _client.DriverMaterialHasGlow = true;
        var seq = Assets.Beam(BeamStyle.GameRed, _owner, "example.effects.test");
        seq.SetEndpoints(new EffectVec3(0f, 1f, 0f), new EffectVec3(4f, 1f, 0f));
        Assert.Equal(1, _client.OwnedLineStyleCopyCount);
        Assert.False(seq.HasGlowLine);
        Assert.Contains(_log.Infos, l => l == SequenceLateDriver.GlowFromMaterialLog);
        seq.Dispose();
        Assert.Equal(0, _client.GlowLineDestroyedCount);
    }

    [Fact]
    public void Beam_GameBlue_does_not_copy_a_trail_sprite_and_adds_glow()
    {
        var seq = Assets.Beam(BeamStyle.GameBlue, _owner, "example.effects.test");
        seq.SetEndpoints(new EffectVec3(0f, 1f, 0f), new EffectVec3(4f, 1f, 0f));
        Assert.Equal(BeamStyle.GameBlue, seq.Style);
        Assert.Equal(0, _client.OwnedLineStyleCopyCount);
        Assert.True(seq.HasGlowLine);
        Assert.Contains(_log.Infos, l => l.Contains("trail sprite", StringComparison.Ordinal));
        seq.Dispose();
        Assert.Equal(1, _client.GlowLineDestroyedCount);
    }

    [Fact]
    public void Beam_GameRed_logs_the_LineRendererPositionElement_path()
    {
        _client.Add(AssetKind.Sequence, "seq-red");
        _client.BeamPresets[BeamStyle.GameRed] = "seq-red";
        _client.DriverIsTrailSprite = false;
        _client.WriteLineRendererBuffer = true;
        var seq = Assets.Beam(BeamStyle.GameRed, _owner, "example.effects.test");
        seq.SetEndpoints(new EffectVec3(0f, 1f, 0f), new EffectVec3(4f, 1f, 0f));
        SequenceLateDriver.TickForTests();
        Assert.True(seq.LineRendererBufferWrites > 0);
        Assert.Equal(SequenceLateDriver.VerdictGameLineBuffer, seq.DrawPath);
        seq.Dispose();
    }

    [Fact]
    public void Sequence_Glow_creates_and_clears_the_halo_on_the_handle()
    {
        var seq = Assets.Beam(BeamStyle.Thin, _owner, "example.effects.test");
        seq.SetEndpoints(new EffectVec3(0f, 1f, 0f), new EffectVec3(4f, 1f, 0f));
        Assert.False(seq.HasGlowLine);
        seq.Glow(0.4f, 0.2f);
        Assert.True(seq.HasGlowLine);
        Assert.Equal(0.4f, seq.OwnedGlowWidth);
        seq.Glow(0f);
        Assert.False(seq.HasGlowLine);
        Assert.Equal(1, _client.GlowLineDestroyedCount);
        seq.Dispose();
    }

    [Fact]
    public void Sequence_never_writes_clone_materials()
    {
        _client.DriverDelayFrames = 1;
        var seq = Assert.IsType<SequenceEffectHandle>(
            Assets.Borrow(AssetKind.Sequence, "AB_Cardinal_SummonOrb_Beam_Buff", _owner, "example.effects.test"));
        seq.LineColor(new EffectColor(0.25f, 0.55f, 1f, 0.8f))
            .SetEndpoints(new EffectVec3(0f, 1f, 0f), new EffectVec3(4f, 1f, 0f));
        Assert.Equal(0, _client.CloneMaterialWriteCount);
        MainThread.Drain();
        Assert.True(seq.HasDriver);
        Assert.Equal(0, _client.CloneMaterialWriteCount);
        SequenceLateDriver.TickForTests();
        seq.Dispose();
        Assert.Equal(0, _client.CloneMaterialWriteCount);
        Assert.Equal(1, _client.OwnedLineDestroyedCount);
    }

    [Fact]
    public void Sequence_owned_line_destroyed_on_RemoveAll()
    {
        var seq = Assert.IsType<SequenceEffectHandle>(
            Assets.Borrow(AssetKind.Sequence, "AB_Cardinal_SummonOrb_Beam_Buff", _owner, "example.effects.test"));
        seq.SetEndpoints(new EffectVec3(0f, 1f, 0f), new EffectVec3(4f, 1f, 0f));
        Assert.True(seq.HasOwnedLine);
        Assert.Equal(0, _client.OwnedLineStyleCopyCount);
        Assets.RemoveAll(_owner);
        Assert.True(seq.IsDisposed);
        Assert.False(seq.HasOwnedLine);
        Assert.Equal(1, _client.OwnedLineDestroyedCount);
    }

    [Fact]
    public void Sequence_Track_updates_endpoints_each_late_frame()
    {
        _client.SetEntityPose(10, new EffectVec3(1f, 2f, 3f));
        _client.SetEntityPose(20, new EffectVec3(4f, 5f, 6f));
        var seq = Assert.IsType<SequenceEffectHandle>(
            Assets.Borrow(AssetKind.Sequence, "AB_Cardinal_SummonOrb_Beam_Buff", _owner, "example.effects.test"));
        seq.Track(10, 20, new EffectVec3(0f, 1f, 0f), new EffectVec3(0f, 0.5f, 0f));
        Assert.Equal(new EffectVec3(1f, 3f, 3f), seq.Start);
        Assert.Equal(new EffectVec3(4f, 5.5f, 6f), seq.End);
        SequenceLateDriver.TickForTests();
        Assert.Equal(new EffectVec3(1f, 3f, 3f), _client.LastAppliedStart);
        Assert.Equal(new EffectVec3(4f, 5.5f, 6f), _client.LastAppliedEnd);

        _client.SetEntityPose(10, new EffectVec3(2f, 2f, 3f));
        _client.SetEntityPose(20, new EffectVec3(8f, 5f, 6f));
        SequenceLateDriver.TickForTests();
        Assert.Equal(new EffectVec3(2f, 3f, 3f), seq.Start);
        Assert.Equal(new EffectVec3(8f, 5.5f, 6f), seq.End);
        Assert.Equal(new EffectVec3(2f, 3f, 3f), _client.LastAppliedStart);
        Assert.Equal(new EffectVec3(8f, 5.5f, 6f), _client.LastAppliedEnd);
        seq.Dispose();
    }

    [Fact]
    public void Sequence_Track_to_a_fixed_end_follows_only_the_start()
    {
        _client.SetEntityPose(10, new EffectVec3(1f, 0f, 0f));
        var far = new EffectVec3(6f, 0f, 0f);
        var seq = Assert.IsType<SequenceEffectHandle>(
            Assets.Borrow(AssetKind.Sequence, "AB_Cardinal_SummonOrb_Beam_Buff", _owner, "example.effects.test"));
        seq.Track(10, far, new EffectVec3(0f, 1f, 0f));
        SequenceLateDriver.TickForTests();
        Assert.Equal(new EffectVec3(1f, 1f, 0f), seq.Start);
        Assert.Equal(far, seq.End);
        _client.SetEntityPose(10, new EffectVec3(2f, 0f, 0f));
        SequenceLateDriver.TickForTests();
        Assert.Equal(new EffectVec3(2f, 1f, 0f), seq.Start);
        Assert.Equal(far, seq.End);
        seq.Dispose();
    }

    [Fact]
    public void Sequence_lost_tracked_entity_freezes_that_end()
    {
        _client.SetEntityPose(10, new EffectVec3(1f, 2f, 3f));
        _client.SetEntityPose(20, new EffectVec3(4f, 5f, 6f));
        var seq = Assert.IsType<SequenceEffectHandle>(
            Assets.Borrow(AssetKind.Sequence, "AB_Cardinal_SummonOrb_Beam_Buff", _owner, "example.effects.test"));
        seq.Track(10, 20);
        SequenceLateDriver.TickForTests();
        var frozenEnd = seq.End;
        _client.KillEntity(20);
        _client.SetEntityPose(10, new EffectVec3(9f, 2f, 3f));
        SequenceLateDriver.TickForTests();
        Assert.Equal(new EffectVec3(9f, 2f, 3f), seq.Start);
        Assert.Equal(frozenEnd, seq.End);
        Assert.Contains(_log.Infos, l => l == "Effects: sequence endpoint end lost; frozen");
        SequenceLateDriver.TickForTests();
        int lost = 0;
        for (int i = 0; i < _log.Infos.Count; i++)
        {
            if (_log.Infos[i] == "Effects: sequence endpoint end lost; frozen")
                lost++;
        }

        Assert.Equal(1, lost);
        seq.Dispose();
    }

    [Fact]
    public void Effects_Install_fills_LateFrame_slot_and_Uninstall_clears_it()
    {
        HarmonySlotHandoff.ClearLateFrame();
        Assets.Install();
        Assert.NotNull(HarmonySlotHandoff.LastLateFrame);
        Assert.Equal(nameof(SequenceLateDriver.OnLateFrame), HarmonySlotHandoff.LastLateFrame!.Method.Name);
        Assets.Uninstall();
        Assert.Null(HarmonySlotHandoff.LastLateFrame);
        Assets.SetRuntimeForTests(_client);
    }

    [Fact]
    public void Sequence_without_buffer_returns_disabled_handle_and_one_log_line()
    {
        _client.SequencesWithoutBuffer.Add("SomeOtherBuff");
        _client.Add(AssetKind.Sequence, "SomeOtherBuff");
        var handle = Assets.Borrow(AssetKind.Sequence, "SomeOtherBuff", _owner, "example.effects.test");
        Assert.False(handle.Enabled);
        Assert.Equal(EffectHandleState.Disabled, handle.State);
        Assert.Contains(
            _log.Infos,
            l => l == "Effects: Sequence 'SomeOtherBuff' has no BuffSequence buffer; returning a disabled handle.");
        Assert.Equal(0, _client.PlaySequenceCalls);
        handle.Dispose();
    }

    [Fact]
    public void RemoveAll_does_not_destroy_a_longer_plugin_guid_root()
    {
        var extraOwner = new Owner("example.sample.extra");
        var a = Assets.Borrow(AssetKind.Image, "PassiveFrame_Normal", _owner, "example.sample");
        var b = Assets.Borrow(AssetKind.Particle, "Embers", extraOwner, "example.sample.extra");
        Assert.True(_client.HasObject("PassiveFrame_Normal"));
        Assert.True(_client.HasObject("Embers"));
        Assets.RemoveAll(_owner);
        Assert.True(a.IsDisposed);
        Assert.False(b.IsDisposed);
        Assert.False(_client.HasObject("PassiveFrame_Normal"));
        Assert.True(_client.HasObject("Embers"));
        Assert.Contains(_client.DestroyedRoots, r => r == "progenitor-effects-example.sample");
        Assert.DoesNotContain(_client.DestroyedRoots, r => r == "progenitor-effects-example.sample.extra");
        b.Dispose();
    }

    [Fact]
    public void Sequence_tint_and_host_before_driver_record_intent_then_poll_applies()
    {
        _client.DriverDelayFrames = 2;
        var seq = Assert.IsType<SequenceEffectHandle>(
            Assets.Borrow(AssetKind.Sequence, "AB_Cardinal_SummonOrb_Beam_Buff", _owner, "example.effects.test"));
        seq.Start = new EffectVec3(0f, 0f, 0f);
        seq.End = new EffectVec3(5f, 0f, 0f);
        seq.Tint = new EffectColor(0.25f, 0.55f, 1f);
        Assert.False(seq.HasDriver);
        Assert.True(seq.HasOwnedLine);
        Assert.True(seq.TintAppliedToDriver);
        Assert.Equal(0, _client.CloneMaterialWriteCount);

        var host = Assets.Host(seq, HostKind.RenderTexture);
        Assert.True(host.Enabled);
        Assert.False(seq.HasDriver);
        Assert.Equal(0, _client.CloneMaterialWriteCount);
        Assert.Equal(0, _client.ObjectLayer("AB_Cardinal_SummonOrb_Beam_Buff"));
        Assert.Contains(_log.Infos, l => l.Contains("no sequence driver GO yet", StringComparison.Ordinal));

        MainThread.Drain();
        Assert.False(seq.HasDriver);
        MainThread.Drain();
        Assert.True(seq.HasDriver);
        Assert.Equal(0, _client.CloneMaterialWriteCount);
        Assert.Equal(31, _client.ObjectLayer("AB_Cardinal_SummonOrb_Beam_Buff"));
        Assert.Contains(_log.Infos, l => l.Contains("landed after", StringComparison.Ordinal));
        Assert.Contains(_log.Infos, l => l.Contains("moved driver", StringComparison.Ordinal));
        host.Dispose();
        seq.Dispose();
    }

    [Fact]
    public void Sequence_driver_missing_after_poll_budget_logs_once()
    {
        _client.DriverDelayFrames = 100;
        _client.MaxDriverPollFrames = 3;
        var seq = Assert.IsType<SequenceEffectHandle>(
            Assets.Borrow(AssetKind.Sequence, "AB_Cardinal_SummonOrb_Beam_Buff", _owner, "example.effects.test"));
        seq.Start = new EffectVec3(0f, 0f, 0f);
        seq.End = new EffectVec3(5f, 0f, 0f);
        Assets.Host(seq, HostKind.RenderTexture);
        MainThread.Drain();
        MainThread.Drain();
        MainThread.Drain();
        MainThread.Drain();
        Assert.False(seq.HasDriver);
        Assert.Contains(_log.Infos, l => l.Contains("not found after 3 frames", StringComparison.Ordinal));
        seq.Dispose();
    }

    [Fact]
    public void Rehost_disposes_the_previous_host_handle()
    {
        var handle = Assets.Borrow(AssetKind.Image, "PassiveFrame_Normal", _owner, "example.effects.test");
        var first = Assets.Host(handle, HostKind.Overlay);
        var second = Assets.Host(handle, HostKind.Overlay);
        Assert.True(first.IsDisposed);
        Assert.True(second.Enabled);
        Assert.False(second.IsDisposed);
        Assert.Equal(EffectHandleState.Hosted, handle.State);
        Assert.Equal(1, _client.HostCount);
        first.Dispose();
        Assert.Equal(EffectHandleState.Hosted, handle.State);
        Assert.False(second.IsDisposed);
        second.Dispose();
        Assert.Equal(EffectHandleState.Borrowed, handle.State);
        handle.Dispose();
    }

    [Fact]
    public void Second_plugin_rt_host_does_not_steal_the_first_layer()
    {
        var extraOwner = new Owner("example.effects.extra");
        var a = Assets.Borrow(AssetKind.Particle, "Embers", _owner, "example.effects.test");
        var hostA = Assets.Host(a, HostKind.RenderTexture);
        Assert.Equal(31, hostA.HostedLayer);
        var b = Assets.Borrow(AssetKind.Particle, "Embers", extraOwner, "example.effects.extra");
        var hostB = Assets.Host(b, HostKind.RenderTexture);
        Assert.Equal(30, hostB.HostedLayer);
        Assert.NotEqual(hostA.HostedLayer, hostB.HostedLayer);
        hostA.Dispose();
        hostB.Dispose();
        a.Dispose();
        b.Dispose();
    }

    [Fact]
    public void RenderTexture_resizes_when_screen_size_changes()
    {
        var particle = Assets.Borrow(AssetKind.Particle, "Embers", _owner, "example.effects.test");
        var host = Assets.Host(particle, HostKind.RenderTexture);
        Assert.Equal(1920, _client.LastRtWidth);
        Assert.Equal(1080, _client.LastRtHeight);
        _client.ScreenSize = new EffectVec2(1280f, 720f);
        _client.SyncHosts();
        Assert.Equal(1280, _client.LastRtWidth);
        Assert.Equal(720, _client.LastRtHeight);
        host.Dispose();
        particle.Dispose();
    }

    [Fact]
    public void Default_runtime_is_server_noop_and_does_not_scan()
    {
        Assets.ResetForTests();
        Assets.Install();
        Assert.Contains(_log.Infos, l => l == "Effects: installed (server no-op)");
        var handle = Assets.Borrow(AssetKind.Image, "PassiveFrame_Normal", _owner, "example.effects.test");
        Assert.False(handle.Enabled);
        Assert.Contains(_log.Infos, l => l.Contains("is client-only", StringComparison.Ordinal));
        Assets.RemoveAll(_owner);
        handle.Dispose();
        Assets.Uninstall();
        Assert.Contains(_log.Infos, l => l.Contains("uninstalled leftover=0", StringComparison.Ordinal));
    }

    // ---- EV12 / R26 findings 3, 7, 8, 9 -----------------------------------------------------

    /// <summary>
    /// R26 finding 3. Play, tint (preset default and an explicit color), late writes and
    /// dispose must not produce a single material or color write on the sequencer's pooled
    /// clone. <see cref="FakeEffectRuntime.CloneMaterialWriteCount"/> is incremented from the
    /// one place the fake models such a write, and that place asks
    /// <see cref="CloneWritePolicy"/> — the production rule — first.
    /// </summary>
    [Fact]
    public void Sequence_never_writes_the_pooled_clone_across_play_tint_and_dispose()
    {
        var seq = Assert.IsType<SequenceEffectHandle>(
            Assets.Borrow(AssetKind.Sequence, "AB_Cardinal_SummonOrb_Beam_Buff", _owner, "example.effects.test"));
        seq.SetEndpoints(new EffectVec3(0f, 1f, 0f), new EffectVec3(4f, 1f, 0f));
        Assert.True(seq.HasDriver);
        seq.ApplyPreset(BeamStyle.GameRed);
        seq.Tint = new EffectColor(1f, 0f, 0f, 1f);
        SequenceLateDriver.TickForTests();
        SequenceLateDriver.TickForTests();
        seq.Dispose();

        Assert.Equal(0, _client.CloneMaterialWriteCount);
    }

    /// <summary>A particle clone is ours (we Instantiate it), so it may be tinted.</summary>
    [Fact]
    public void Particle_clone_is_ours_and_is_tinted()
    {
        var particle = Assets.Borrow(AssetKind.Particle, "Embers", _owner, "example.effects.test");
        var clone = Assert.IsType<ParticleHandle>(particle);
        clone.Tint = new EffectColor(0f, 1f, 0f, 1f);
        Assert.Equal(1, _client.CloneMaterialWriteCount);
        particle.Dispose();
    }

    /// <summary>The rule itself: only a GameObject this process instantiated may be written.</summary>
    [Theory]
    [InlineData(AssetKind.Sequence, true, false)]
    [InlineData(AssetKind.Sequence, false, false)]
    [InlineData(AssetKind.Particle, true, true)]
    [InlineData(AssetKind.Particle, false, false)]
    [InlineData(AssetKind.Image, true, true)]
    public void Clone_write_policy_allows_only_objects_we_instantiated(
        AssetKind kind, bool ours, bool expected) =>
        Assert.Equal(expected, CloneWritePolicy.AllowsMaterialWrites(kind, ours));

    /// <summary>
    /// R26 finding 7. A vanilla beam that was already on screen when we played is in the
    /// "seen" snapshot and belongs to no handle: claiming it reparents, activates and then
    /// deactivates a live game effect. Only a clone that appeared after our play frame (and
    /// is still parked, evidence 99) or one a handle already holds may be claimed.
    /// </summary>
    [Fact]
    public void Fallback_finder_refuses_a_live_foreign_clone()
    {
        // seen before our play + nobody bound it + drawing = a vanilla beam.
        Assert.Equal(
            DriverClaim.None,
            DriverClaimPolicy.Decide(seenBefore: true, boundToAnyHandle: false, activeInHierarchy: true));
        // Even parked, a clone that predates our play is not ours to take.
        Assert.Equal(
            DriverClaim.None,
            DriverClaimPolicy.Decide(seenBefore: true, boundToAnyHandle: false, activeInHierarchy: false));
        // An unseen clone that is already drawing started with something else this frame.
        Assert.Equal(
            DriverClaim.None,
            DriverClaimPolicy.Decide(seenBefore: false, boundToAnyHandle: false, activeInHierarchy: true));
    }

    [Fact]
    public void Fallback_finder_claims_a_fresh_clone_and_a_pool_reuse()
    {
        Assert.Equal(
            DriverClaim.NewClone,
            DriverClaimPolicy.Decide(seenBefore: false, boundToAnyHandle: false, activeInHierarchy: false));
        // EV6: the sequencer reactivates a clone a previous handle already holds.
        Assert.Equal(
            DriverClaim.PoolReuse,
            DriverClaimPolicy.Decide(seenBefore: true, boundToAnyHandle: true, activeInHierarchy: true));
    }

    [Fact]
    public void Fallback_finder_prefers_a_fresh_clone_over_a_reuse_then_the_newest_id()
    {
        Assert.True(DriverClaimPolicy.Prefer(DriverClaim.NewClone, 5, DriverClaim.PoolReuse, 900));
        Assert.False(DriverClaimPolicy.Prefer(DriverClaim.PoolReuse, 900, DriverClaim.NewClone, 5));
        Assert.True(DriverClaimPolicy.Prefer(DriverClaim.NewClone, 7, DriverClaim.NewClone, 6));
        Assert.False(DriverClaimPolicy.Prefer(DriverClaim.NewClone, 5, DriverClaim.NewClone, 6));
        // Nothing beats nothing, and anything beats None.
        Assert.False(DriverClaimPolicy.Prefer(DriverClaim.None, 99, DriverClaim.None, 0));
        Assert.True(DriverClaimPolicy.Prefer(DriverClaim.PoolReuse, 1, DriverClaim.None, 99));
    }

    /// <summary>
    /// R27 finding 11. The owned line is our own GameObject with our own material under our own
    /// root: nothing about it depends on the pooled clone, and the sequencer takes that clone
    /// back at the natural end of the <em>game</em> sequence (a second or two), not at the end
    /// of the plugin's beam. EV12 disabled the owned line and unwatched the handle on that
    /// transition, with no rebind path, so a 30-second tracked beam vanished after the
    /// sequence's own length. The recycle now stops only the clone-related writes; the owned
    /// line keeps drawing from the handle's endpoints and the handle stays watched.
    /// </summary>
    [Fact]
    public void Recycled_driver_keeps_the_owned_line_and_reports_recycled()
    {
        var seq = Assert.IsType<SequenceEffectHandle>(
            Assets.Borrow(AssetKind.Sequence, "AB_Cardinal_SummonOrb_Beam_Buff", _owner, "example.effects.test"));
        seq.SetEndpoints(new EffectVec3(0f, 1f, 0f), new EffectVec3(4f, 1f, 0f));
        SequenceLateDriver.TickForTests();
        Assert.True(seq.LineVisible);
        Assert.False(seq.Recycled);

        _client.RecycleDrivers();
        SequenceLateDriver.TickForTests();

        Assert.True(seq.Recycled);
        Assert.True(seq.LineVisible);
        Assert.Equal(1, CountInfos(DriverRecycleState.RecycledLog));

        // Still watched: the owned line keeps taking new endpoints after the recycle.
        seq.SetEndpoints(new EffectVec3(0f, 1f, 0f), new EffectVec3(9f, 1f, 0f));
        SequenceLateDriver.TickForTests();
        Assert.True(seq.LineVisible);
        Assert.Equal(new EffectVec3(9f, 1f, 0f), seq.End);

        // Idempotent: a second detection does not log again.
        SequenceLateDriver.TickForTests();
        Assert.Equal(1, CountInfos(DriverRecycleState.RecycledLog));

        // R28 finding 13: the production rebind this test's SetEndpoints reaches
        // (UnityEffectRuntime.TryBindSequenceDriver) used to fall through from
        // DropRecycledDriver to FindFallback over the play-frame snapshot — which the recycle
        // had just made stale — and DriverClaimPolicy.Decide then rated the pool-owned clone a
        // fresh NewClone. The claim rule itself is unchanged and still says NewClone for that
        // shape; the fallback is what must not run.
        Assert.Equal(
            DriverClaim.NewClone,
            DriverClaimPolicy.Decide(seenBefore: false, boundToAnyHandle: false, activeInHierarchy: false));
        Assert.False(DriverClaimPolicy.MayUseFallback(recycled: true, haveSnapshot: true));
        Assert.False(DriverClaimPolicy.MayUseFallback(recycled: true, haveSnapshot: false));
        Assert.False(DriverClaimPolicy.MayUseFallback(recycled: false, haveSnapshot: false));
        Assert.True(DriverClaimPolicy.MayUseFallback(recycled: false, haveSnapshot: true));

        // The plugin's dispose still cleans up.
        seq.Dispose();
        Assert.Equal(1, _client.OwnedLineDestroyedCount);
        Assert.False(seq.LineVisible);
    }

    /// <summary>
    /// R27 finding 5.3. The old bound was <c>slots.Position + 12 &lt;= length</c> in <c>int</c>
    /// arithmetic: a blob-supplied <c>Index</c> near <c>int.MaxValue</c> made the sum negative,
    /// the comparison passed, and <c>*(float3*)(buffer + 2147483642) = position</c> wrote about
    /// 2 GB past the <c>BlackboardElement</c> buffer. The first assertion below is the old
    /// expression: it is still true, which is exactly why the class needed a real bound.
    /// </summary>
    [Fact]
    public void Blackboard_slot_bounds_do_not_overflow()
    {
        const int hostileSlot = int.MaxValue - 5;
        Assert.False(SequenceBlackboardLayout.SlotFits(hostileSlot, 12, 64));
        Assert.False(SequenceBlackboardLayout.SlotFits(int.MaxValue, 16, 64));

        // Ordinary cases still behave.
        Assert.True(SequenceBlackboardLayout.SlotFits(0, 12, 12));
        Assert.True(SequenceBlackboardLayout.SlotFits(52, 12, 64));
        Assert.False(SequenceBlackboardLayout.SlotFits(53, 12, 64));
        Assert.False(SequenceBlackboardLayout.SlotFits(-1, 12, 64));
        Assert.False(SequenceBlackboardLayout.SlotFits(0, 12, 0));
        Assert.False(SequenceBlackboardLayout.SlotFits(48, 16, 60));
        Assert.True(SequenceBlackboardLayout.SlotFits(48, 16, 64));
    }

    /// <summary>
    /// R28 finding 1's other half. <c>SlotFits</c> bounds the register write against the length
    /// the interop reported for the <c>BlackboardElement</c> buffer, so a garbage length
    /// authorizes a garbage slot — the same dependency the LineRenderer path had, one level
    /// down. The length is bounded before it is used, and an absurd one latches the gate.
    /// </summary>
    [Theory]
    [InlineData(1, true)]
    [InlineData(64, true)]
    [InlineData(SequenceBlackboardLayout.MaxBufferBytes, true)]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    [InlineData(SequenceBlackboardLayout.MaxBufferBytes + 1, false)]
    [InlineData(int.MaxValue, false)]
    public void Blackboard_buffer_length_is_bounded(int length, bool ok)
    {
        Assert.Equal(ok, SequenceBlackboardLayout.BufferLengthOk(length));
        if (!ok)
        {
            // Without the bound, a slot two gigabytes in "fits" a buffer that claims to be
            // that long — which is the write this refuses.
            Assert.Equal(
                length > 0,
                SequenceBlackboardLayout.SlotFits(length - SequenceBlackboardLayout.Float3Bytes, 12, length));
        }
    }

    /// <summary>
    /// R27 finding 5.2. <c>BlobArray.m_OffsetPtr</c> was validated only as <c>&gt; 0</c>, so a
    /// garbage or differently-versioned blob root produced a wild pointer before any element
    /// was read, and <c>count</c> up to <see cref="SequenceBlackboardLayout.MaxInputs"/> then
    /// dereferenced <c>*(ushort*)element</c> up to ~39 KB past the blob.
    /// </summary>
    [Theory]
    [InlineData(0, 4, false)]
    [InlineData(-8, 4, false)]
    [InlineData(1 << 20, 4, false)]
    [InlineData(int.MaxValue, 4, false)]
    [InlineData(16, 0, false)]
    [InlineData(16, -1, false)]
    [InlineData(16, SequenceBlackboardLayout.MaxInputs + 1, false)]
    [InlineData(16, int.MaxValue, false)]
    [InlineData(16, 4, true)]
    [InlineData(16, SequenceBlackboardLayout.MaxInputs, true)]
    public void Blackboard_walk_bounds_the_blob_offset_and_count(int offset, int count, bool ok) =>
        Assert.Equal(ok, SequenceBlackboardLayout.WalkOk(offset, count));

    [Fact]
    public void Blackboard_element_offsets_are_computed_in_long_arithmetic()
    {
        Assert.Equal(16, SequenceBlackboardLayout.ElementOffset(16, 4, 0));
        Assert.Equal(16 + SequenceBlackboardLayout.InputElementStride,
            SequenceBlackboardLayout.ElementOffset(16, 4, 1));
        // Past the declared count, and a walk that was refused, both stop the loop.
        Assert.Equal(-1, SequenceBlackboardLayout.ElementOffset(16, 4, 4));
        Assert.Equal(-1, SequenceBlackboardLayout.ElementOffset(16, 4, -1));
        Assert.Equal(-1, SequenceBlackboardLayout.ElementOffset(0, 4, 0));
        Assert.Equal(-1, SequenceBlackboardLayout.ElementOffset(16, int.MaxValue, 0));
    }

    /// <summary>
    /// R27 finding 5.1. <c>CheckStrideOnce</c> logged "blackboard re-point disabled for this
    /// build." and then returned without setting any flag, so nothing consumed the result and
    /// the walk ran anyway — the message was a lie about the behavior. The gate latches, and
    /// it says so exactly once.
    ///
    /// <para><b>R30 finding 14</b> changed what it latches <em>on</em>. The predicate is no
    /// longer a runtime reading (see
    /// <see cref="A_wrong_sizeof_reading_never_closes_anything"/>) but the module's own dump
    /// constants checked against each other, so the one thing that can disable the blob walk is
    /// a constant pair this source file cannot justify.</para>
    /// </summary>
    [Fact]
    public void Layout_gate_disables_and_logs_exactly_once()
    {
        var gate = default(LayoutOnceGate);

        // A stride that is not the field extent rounded up to the alignment: 74 is neither
        // 4-aligned nor RoundUp(73, 4). Nothing backs it, so the walk refuses.
        string? first = gate.Check(
            SequenceBlackboardLayout.WalkAllowed(74, 73, 4),
            SequenceBlackboardLayout.ConstantsInvariantLog("SequenceInputElement", 74, 73, 4));
        // The exact text is the contract published in docs/effects.md's expected log lines.
        Assert.Equal(
            "Effects: SequenceInputElement dump constants contradict each other (stride 74,"
            + " field extent 73, alignment 4; the stride must be the extent rounded up to the"
            + " alignment); raw walks over SequenceInputElement are disabled for this build.",
            first);
        Assert.True(gate.Disabled);
        Assert.False(gate.Enabled);

        // A later check neither logs again nor un-disables the path.
        Assert.Null(gate.Check(true, "unused"));
        Assert.True(gate.Disabled);

        // The pair this build actually ships opens it, and says nothing.
        gate.Reset();
        Assert.Null(gate.Check(SequenceBlackboardLayout.InputElementWalkAllowed(), "unused"));
        Assert.True(gate.Enabled);
    }

    /// <summary>
    /// R28 finding 2 (EV16). The EV15 gate was a struct whose default was
    /// <c>_checked = false, Disabled = false</c>, so <c>Enabled</c> was true <b>before any
    /// check ran</b> — and <c>CheckStrideOnce</c>'s <c>catch { return; }</c>, the path taken
    /// when the interop <c>sizeof</c> throws (exactly the failure the probe exists for), never
    /// touched the gate at all. The walk then ran against a differently-shaped blob with the
    /// hardcoded stride 76, reading up to 512 elements at wrong offsets.
    ///
    /// <para>The first assertion is the whole fix: an unprobed gate refuses. On the old code it
    /// failed on the first line.</para>
    /// </summary>
    [Fact]
    public void Layout_gate_defaults_closed_until_a_probe_passes()
    {
        var gate = default(LayoutOnceGate);
        Assert.True(gate.Disabled);
        Assert.False(gate.Enabled);
        Assert.False(gate.Checked);
        // Not yet probed is distinguishable from probed-and-failed, so a caller can re-probe.
        Assert.False(gate.Failed);

        // A passing probe is the only thing that opens it.
        Assert.Null(gate.Check(true, "unused"));
        Assert.True(gate.Enabled);
        Assert.True(gate.Checked);
        Assert.False(gate.Failed);

        // Reset closes it again rather than leaving it open for the next Core generation.
        gate.Reset();
        Assert.True(gate.Disabled);
        Assert.False(gate.Checked);
    }

    /// <summary>
    /// R28 finding 2. A probe that <em>throws</em> must land in the same place as a probe that
    /// fails: shut, logged once, and never reopened. That is still true of the gate's own
    /// contract, which is what this pins — but see
    /// <see cref="A_throwing_sizeof_leaves_the_blob_walk_running"/> for what now reaches it.
    /// </summary>
    [Fact]
    public void A_throwing_probe_leaves_the_gate_closed_and_says_so_once()
    {
        var gate = default(LayoutOnceGate);
        string? log = gate.Check(
            false, SequenceBlackboardLayout.ConstantsInvariantLog("SequenceInputElement", 74, 73, 4));
        Assert.NotNull(log);
        Assert.Contains("are disabled for this build.", log);
        Assert.True(gate.Failed);
        Assert.True(gate.Disabled);

        // Nothing can talk it back open, and nothing logs twice.
        Assert.Null(gate.Check(true, "unused"));
        Assert.Null(gate.Fail("unused"));
        Assert.True(gate.Disabled);
    }

    /// <summary>
    /// <b>R30 finding 9.</b> R28 finding 1 made an absurd buffer length latch
    /// <c>LayoutOnceGate.Fail</c> — the same gate the layout probes use — so one entity's
    /// <c>GetBufferLength</c> turned the raw line write off for every beam for the rest of the
    /// process and blamed "this build". EV17 had already made exactly this split for the
    /// blackboard ("a length is a fact about one entity; an element size is a fact about the
    /// build") and did not apply it one file over. The refusal is per call now, and the line
    /// says which sequence was skipped rather than announcing a dead module.
    /// </summary>
    [Fact]
    public void An_impossible_buffer_length_refuses_the_call_and_not_the_build()
    {
        Assert.Equal(
            "Effects: LineRendererPositionElement buffer length is 2147483647, which no buffer"
            + " can have (a real one is 2..4096); skipping that sequence (the raw path stays on).",
            LineRendererBufferLayout.LengthRefusedLog(int.MaxValue));
        Assert.Equal(
            "Effects: BlackboardElement buffer length is -1, which no buffer can have (a real"
            + " one is 1..65536); skipping that sequence (the blackboard path stays on).",
            SequenceBlackboardLayout.BufferLengthRefusedLog(-1));

        // Neither line may claim the module is off; that claim is what R29 finding 4 and R30
        // finding 9 were both raised about.
        foreach (string line in new[]
                 {
                     LineRendererBufferLayout.LengthRefusedLog(int.MaxValue),
                     SequenceBlackboardLayout.BufferLengthRefusedLog(-1),
                     BuffSequenceLayout.LengthRefusedLog(-1),
                 })
        {
            Assert.DoesNotContain("disabled for this build", line, StringComparison.Ordinal);
            Assert.Contains("stays on", line, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// <b>R30 finding 9, the behavior.</b> One sequence whose line buffer reads as an
    /// impossible length must not touch the next sequence's write. Both buffers share one gate
    /// here, exactly as the two calls share <c>_lineElementGate</c> in production.
    /// </summary>
    [Fact]
    public void A_short_or_impossible_buffer_on_one_sequence_leaves_the_next_one_writing()
    {
        var gates = new FakeModuleGates();

        // Sequence 1: GetBufferLength reads garbage.
        var garbage = new FakeLineRendererBuffer(int.MaxValue, allocatedElements: 4, gates: gates);
        Assert.False(garbage.TryWriteEndpoints());
        Assert.False(garbage.WroteOutOfRange);
        Assert.True(gates.ImpossibleLengthLogged);
        Assert.False(gates.ElementSize.Failed);

        // Sequence 2: an ordinary one-point line. A skip, and still no latch.
        var tooSmall = new FakeLineRendererBuffer(1, allocatedElements: 4, gates: gates);
        Assert.False(tooSmall.TryWriteEndpoints());
        Assert.True(tooSmall.Skipped);
        Assert.False(gates.ElementSize.Failed);

        // Sequence 3: an ordinary two-point line, after both. On the pre-R30 code the first
        // sequence had already latched the gate and this write never happened.
        var healthy = new FakeLineRendererBuffer(2, allocatedElements: 2, gates: gates);
        Assert.True(healthy.TryWriteEndpoints());
        Assert.Equal(2, healthy.Writes);
        Assert.Equal(0, healthy.EndpointWrittenAt(0));
        Assert.Equal(1, healthy.EndpointWrittenAt(1));
        Assert.True(gates.ElementSize.Enabled);

        // And the warning was written once, not once per sequence.
        Assert.Single(gates.Warnings);
    }

    /// <summary>
    /// R28 finding 1 (EV16) — the raw-buffer path R27 finding 17 named and EV15 only half
    /// fixed. <c>TryWriteLineRendererPositions</c> kept <c>if (length &lt; 2) return false;</c>
    /// as its only bound while the element-size guard three lines above it exists precisely
    /// because <c>GetBufferLength</c> can read garbage through the interop. A large length
    /// wrote 20 bytes per element for <c>length</c> elements past the chunk.
    /// </summary>
    [Theory]
    [InlineData(2, true)]
    [InlineData(8, true)]
    [InlineData(LineRendererBufferLayout.MaxElements, true)]
    [InlineData(1, false)]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    [InlineData(int.MinValue, false)]
    [InlineData(LineRendererBufferLayout.MaxElements + 1, false)]
    [InlineData(1 << 24, false)]
    [InlineData(int.MaxValue, false)]
    public void Line_renderer_buffer_length_is_bounded(int length, bool ok) =>
        Assert.Equal(
            ok,
            LineRendererBufferLayout.LengthOk(length, BufferElementGuard.LineRendererPositionElementSize));

    /// <summary>
    /// R28 finding 1. Every offset is computed in <c>long</c>: the old
    /// <c>byte* e = (byte*)raw + (i * stride)</c> overflows <c>int</c> at
    /// <c>i ~ 107,374,182</c> and wraps <b>negative</b>, writing before the buffer. The first
    /// assertion is the old expression — still true, which is why the walk needed a real bound.
    /// </summary>
    [Fact]
    public void Line_renderer_element_offsets_are_computed_in_long_arithmetic()
    {
        const int stride = BufferElementGuard.LineRendererPositionElementSize;
        const int hostileIndex = 107374183;
        Assert.True(unchecked(hostileIndex * stride) < 0);

        // That index cannot be reached at all now: it is past the bounded length.
        Assert.Equal(-1L, LineRendererBufferLayout.ElementOffset(hostileIndex, 8, stride));
        Assert.Equal(-1L, LineRendererBufferLayout.ElementOffset(hostileIndex, int.MaxValue, stride));
        Assert.Equal(-1L, LineRendererBufferLayout.ElementOffset(-1, 8, stride));
        Assert.Equal(-1L, LineRendererBufferLayout.ElementOffset(8, 8, stride));

        // Ordinary walking still works, and the last element ends exactly on the last byte.
        Assert.Equal(0L, LineRendererBufferLayout.ElementOffset(0, 8, stride));
        Assert.Equal(stride, LineRendererBufferLayout.ElementOffset(1, 8, stride));
        Assert.Equal(7L * stride, LineRendererBufferLayout.ElementOffset(7, 8, stride));
        Assert.Equal(8L * stride, LineRendererBufferLayout.TotalBytes(8, stride));
        Assert.Equal(-1L, LineRendererBufferLayout.TotalBytes(int.MaxValue, stride));
    }

    /// <summary>
    /// R28 finding 1. Both fields of an element are bounds-checked before the pointer is
    /// formed, so a last element that only <em>starts</em> inside the buffer cannot write its
    /// three floats past the end.
    /// </summary>
    [Fact]
    public void Line_renderer_field_writes_stay_inside_the_buffer()
    {
        const int stride = BufferElementGuard.LineRendererPositionElementSize;
        long total = LineRendererBufferLayout.TotalBytes(2, stride);
        Assert.Equal(40L, total);

        Assert.True(LineRendererBufferLayout.FieldFits(
            0L,
            LineRendererBufferLayout.PositionValueOffset,
            LineRendererBufferLayout.PositionFieldBytes,
            total));
        Assert.True(LineRendererBufferLayout.FieldFits(
            stride,
            LineRendererBufferLayout.PositionValueOffset,
            LineRendererBufferLayout.PositionFieldBytes,
            total));

        // One element further than the buffer holds: the index field and the float3 both refuse.
        Assert.False(LineRendererBufferLayout.FieldFits(
            2L * stride,
            LineRendererBufferLayout.PositionIndexOffset,
            LineRendererBufferLayout.IndexFieldBytes,
            total));
        Assert.False(LineRendererBufferLayout.FieldFits(
            2L * stride,
            LineRendererBufferLayout.PositionValueOffset,
            LineRendererBufferLayout.PositionFieldBytes,
            total));

        // Negative or absent offsets never form a pointer.
        Assert.False(LineRendererBufferLayout.FieldFits(-1L, 8, 12, total));
        Assert.False(LineRendererBufferLayout.FieldFits(0L, -8, 12, total));
        Assert.False(LineRendererBufferLayout.FieldFits(0L, 8, 12, -1L));
    }

    /// <summary>
    /// R28 finding 1: the whole loop, driven by a fake that feeds the length the interop
    /// reported. The old code accepted every one of these and walked
    /// <c>length</c> elements; the new bound refuses before a single byte is touched.
    /// </summary>
    [Theory]
    [InlineData(int.MaxValue)]
    [InlineData(1 << 20)]
    [InlineData(-4)]
    public void A_garbage_buffer_length_writes_nothing(int reportedLength)
    {
        var gates = new FakeModuleGates();
        var buffer = new FakeLineRendererBuffer(reportedLength, allocatedElements: 2, gates: gates);
        Assert.False(buffer.TryWriteEndpoints());
        Assert.Equal(0, buffer.Writes);
        Assert.False(buffer.WroteOutOfRange);
        // R29 finding 4 said the write must be refused, and it still is. R30 finding 9: the
        // refusal is this entity's, not the build's — the gate is untouched and the next
        // sequence, on the same gate, still writes.
        Assert.False(buffer.GateLatched);
        Assert.True(gates.ImpossibleLengthLogged);
        Assert.Single(gates.Warnings);

        // The same fake with an honest length writes both ends and nothing else.
        var honest = new FakeLineRendererBuffer(2, allocatedElements: 2, gates: gates);
        Assert.True(honest.TryWriteEndpoints());
        Assert.Equal(2, honest.Writes);
        Assert.False(honest.WroteOutOfRange);
        Assert.True(honest.GateEnabled);
    }

    /// <summary>
    /// R29 finding 4 (EV17). EV16 gave <c>LengthOk</c> a single boolean answer and
    /// <c>TryWriteLineRendererPositions</c> fed <b>both</b> refusals to
    /// <c>LayoutOnceGate.Fail</c>. A <c>SequenceInstance</c> whose
    /// <c>LineRendererPositionElement</c> buffer has no positions authored yet — or one line
    /// renderer's single point — is an ordinary entity, so the first such sequence the client
    /// or server touched turned the raw path off for <em>every</em> beam for the rest of the
    /// process, and the log line said the length was "outside 2..4096", which reads as a build
    /// problem. Three-way now: too small is skipped, impossible latches.
    /// </summary>
    // The verdict is an internal enum on an internal type, so it cannot appear in a public
    // xunit signature; the name is compared instead, which fails just as loudly on a typo.
    [Theory]
    [InlineData(0, "TooSmall")]
    [InlineData(1, "TooSmall")]
    [InlineData(2, "Ok")]
    [InlineData(8, "Ok")]
    [InlineData(LineRendererBufferLayout.MaxElements, "Ok")]
    [InlineData(-1, "Impossible")]
    [InlineData(int.MinValue, "Impossible")]
    [InlineData(LineRendererBufferLayout.MaxElements + 1, "Impossible")]
    [InlineData(int.MaxValue, "Impossible")]
    public void A_short_line_buffer_is_not_the_same_as_an_impossible_one(int length, string expected) =>
        Assert.Equal(
            expected,
            LineRendererBufferLayout
                .Classify(length, BufferElementGuard.LineRendererPositionElementSize)
                .ToString());

    /// <summary>
    /// R29 finding 4, the behavior rather than the predicate: a 0- and a 1-element buffer are
    /// skipped with the gate <b>left open</b>, so the next sequence — a real two-point line —
    /// still writes. On the EV16 code the first assertion after the 0-length walk fails: the
    /// gate is latched and every later beam re-point silently does nothing for the life of the
    /// process.
    /// </summary>
    [Fact]
    public void A_short_line_buffer_does_not_latch_the_gate_for_the_process()
    {
        var empty = new FakeLineRendererBuffer(0, allocatedElements: 2);
        Assert.False(empty.TryWriteEndpoints());
        Assert.True(empty.Skipped);
        Assert.Equal(0, empty.Writes);
        Assert.False(empty.GateLatched);
        Assert.True(empty.GateEnabled);

        var single = new FakeLineRendererBuffer(1, allocatedElements: 2);
        Assert.False(single.TryWriteEndpoints());
        Assert.True(single.Skipped);
        Assert.False(single.GateLatched);

        // The next sequence is a real two-point line and is still driven.
        var real = new FakeLineRendererBuffer(2, allocatedElements: 2);
        Assert.True(real.TryWriteEndpoints());
        Assert.Equal(2, real.Writes);

        // And the skip is reported as a skip, not as the "raw buffer writes disabled" refusal.
        Assert.Equal(
            "Effects: LineRendererPositionElement buffer has 0 element(s), fewer than 2;"
            + " skipping that sequence (the raw path stays on).",
            LineRendererBufferLayout.LengthSkippedLog(0));
        Assert.DoesNotContain("disabled", LineRendererBufferLayout.LengthSkippedLog(0), StringComparison.Ordinal);
    }

    /// <summary>
    /// R29 residual 21.9 (EV17). Elements 0 and 1 used to be written as start and end
    /// unconditionally, with <c>PositionIndex</c> stamped over the authored value, while the
    /// loop from <c>i = 2</c> consulted <c>PositionIndex</c>. A buffer whose element 0 carries
    /// <c>PositionIndex 1</c> therefore got the <b>start</b> point written into the <b>end</b>
    /// slot — and lost the authored index on the way. On the old code the first two assertions
    /// below are reversed.
    /// </summary>
    [Fact]
    public void Every_element_is_written_from_its_own_position_index()
    {
        var reversed = new FakeLineRendererBuffer(2, allocatedElements: 2);
        reversed.SetPositionIndex(0, 1);
        reversed.SetPositionIndex(1, 0);
        Assert.True(reversed.TryWriteEndpoints());
        Assert.Equal(1, reversed.EndpointWrittenAt(0));
        Assert.Equal(0, reversed.EndpointWrittenAt(1));
        // The authored indices survive: nothing stamped i over them.
        Assert.Equal(1, reversed.PositionIndexAt(0));
        Assert.Equal(0, reversed.PositionIndexAt(1));

        // An already-correct pair is honored too, and reads the same as the seeded case.
        var forward = new FakeLineRendererBuffer(2, allocatedElements: 2);
        forward.SetPositionIndex(0, 0);
        forward.SetPositionIndex(1, 1);
        Assert.True(forward.TryWriteEndpoints());
        Assert.Equal(0, forward.EndpointWrittenAt(0));
        Assert.Equal(1, forward.EndpointWrittenAt(1));

        // A buffer with no usable mapping (a fresh instance is all zeroes, so both elements
        // claim slot 0) is still seeded positionally — the live path EV16 verified.
        var unauthored = new FakeLineRendererBuffer(2, allocatedElements: 2);
        Assert.True(LineRendererBufferLayout.SeedPositionally(0, 0));
        Assert.True(unauthored.TryWriteEndpoints());
        Assert.Equal(0, unauthored.EndpointWrittenAt(0));
        Assert.Equal(1, unauthored.EndpointWrittenAt(1));
        Assert.Equal(0, unauthored.PositionIndexAt(0));
        Assert.Equal(1, unauthored.PositionIndexAt(1));

        Assert.False(LineRendererBufferLayout.SeedPositionally(0, 1));
        Assert.False(LineRendererBufferLayout.SeedPositionally(1, 0));
        Assert.True(LineRendererBufferLayout.SeedPositionally(1, 1));
        Assert.True(LineRendererBufferLayout.SeedPositionally(-1, 7));
        Assert.Equal(0, LineRendererBufferLayout.EndpointFor(0));
        Assert.Equal(1, LineRendererBufferLayout.EndpointFor(1));
        Assert.Equal(-1, LineRendererBufferLayout.EndpointFor(2));
        Assert.Equal(-1, LineRendererBufferLayout.EndpointFor(-3));
    }

    /// <summary>
    /// R29 residual 21.9, the tail of the buffer: an element past the first two whose
    /// <c>PositionIndex</c> is neither 0 nor 1 is left alone, and one that claims an endpoint
    /// gets it. Same rule for every element, which is the point.
    /// </summary>
    [Fact]
    public void Trailing_elements_follow_the_same_position_index_rule()
    {
        var buffer = new FakeLineRendererBuffer(4, allocatedElements: 4);
        buffer.SetPositionIndex(0, 0);
        buffer.SetPositionIndex(1, 1);
        buffer.SetPositionIndex(2, 5);
        buffer.SetPositionIndex(3, 1);

        Assert.True(buffer.TryWriteEndpoints());
        Assert.Equal(0, buffer.EndpointWrittenAt(0));
        Assert.Equal(1, buffer.EndpointWrittenAt(1));
        Assert.Equal(-1, buffer.EndpointWrittenAt(2));
        Assert.Equal(1, buffer.EndpointWrittenAt(3));
        Assert.Equal(3, buffer.Writes);
        Assert.False(buffer.WroteOutOfRange);
    }

    /// <summary>
    /// <b>R30 finding 5.</b> Every reading a raw buffer path can take — <c>TypeManager</c>'s
    /// <c>ElementSize</c> and the interop struct's own <c>sizeof</c>, including an unavailable
    /// one — and none of them closes the gate, because the walk multiplies by the dump
    /// constant and by nothing it read.
    ///
    /// <para>R29 finding 19 raised the direction that still holds: an interop-reported
    /// <c>0</c> is "unknown", not "fine". Its EV17 fix made "unknown" a <em>refusal</em>, which
    /// is the third repetition of the failure that took the module down twice live: a reading
    /// that says nothing about the dump-derived arithmetic turning that arithmetic off. Now it
    /// says so and the walk proceeds.</para>
    ///
    /// <para><paramref name="expectWarning"/> is the severity of the one line, not a verdict.
    /// On the EV17 code the last five rows disabled the raw path for the whole process.</para>
    /// </summary>
    [Theory]
    // Both agree. The ordinary healthy build: no line at all.
    [InlineData(20, 20, false, false, false)]
    // TypeManager read 0 (trap 7); sizeof answers. This is the live path on this interop.
    [InlineData(0, 20, false, false, false)]
    // TypeManager answered a garbage nonzero — its siblings do exactly this (SizeInChunk
    // reported 515 for Follower). One line, and nothing is disabled.
    [InlineData(515, 20, false, false, true)]
    [InlineData(16, 20, false, false, true)]
    // sizeof contradicts the dump. Evidence the dump may be stale: a WARNING, not a switch.
    [InlineData(0, 16, true, true, false)]
    [InlineData(20, 16, true, true, false)]
    // sizeof unavailable (threw, reported as 0). Also only a warning: an unreadable interop
    // struct says nothing about a stride that came from the dump.
    [InlineData(0, 0, true, true, false)]
    [InlineData(20, 0, true, true, false)]
    [InlineData(-1, -1, true, true, false)]
    public void A_wrong_sizeof_reading_never_closes_anything(
        int reported, int sizeOf, bool expectLine, bool expectWarning, bool expectTypeManagerLine)
    {
        const int dump = BufferElementGuard.LineRendererPositionElementSize;
        const int packed = BufferElementGuard.LineRendererPositionElementPackedBytes;
        const int alignment = BufferElementGuard.LineRendererPositionElementAlignment;

        string? note = BufferElementGuard.SizeReadingLog("LineRendererPositionElement", sizeOf, dump, packed, alignment);
        Assert.Equal(expectLine, note != null);
        if (expectLine)
        {
            Assert.Equal(expectWarning, BufferElementGuard.SizeReadingIsWarning(sizeOf, dump, packed, alignment));
            // Whatever it says, it may not say the path is off.
            Assert.DoesNotContain("disabled", note!, StringComparison.Ordinal);
            Assert.Contains("keeps striding 20", note!, StringComparison.Ordinal);
        }

        Assert.Equal(expectTypeManagerLine, BufferElementGuard.TypeManagerDisagrees(reported, dump));

        // The gate the production probe drives is open on every row, because the constants that
        // back the walk are the same on every row.
        var gate = default(LayoutOnceGate);
        gate.Check(BufferElementGuard.WritesAllowed(dump, packed, alignment), "must not be logged");
        Assert.True(gate.Enabled);
    }

    /// <summary>
    /// <b>R30 finding 5, the behavior the theory above only describes.</b> A sequence read on a
    /// build whose <c>sizeof</c> answers a third value must still be re-pointed, because the
    /// walk never used that number. On EV17 the reading latched a process-wide gate and every
    /// beam went <c>path=none</c> while the arithmetic underneath was byte-for-byte correct.
    /// </summary>
    [Fact]
    public void A_third_sizeof_value_leaves_the_blob_walk_resolving()
    {
        // The reading a regenerated interop might produce: neither 76 nor 73.
        Assert.Equal(
            "Mismatch",
            SequenceBlackboardLayout.AgreeInputElement(80).ToString());
        Assert.NotNull(SequenceBlackboardLayout.InputElementReadingLog(80));
        Assert.True(SequenceBlackboardLayout.InputElementWalkAllowed());

        var gate = default(LayoutOnceGate);
        Assert.Null(gate.Check(SequenceBlackboardLayout.InputElementWalkAllowed(), "must not be logged"));
        Assert.True(gate.Enabled);

        // ...and a real blob still resolves both registers, striding the dump's 76.
        var blob = new FakeSequenceInputBlob(blobOffset: 8);
        blob.AddInput("Position", SequenceBlackboardLayout.TypeFloat3, index: 8);
        blob.AddInput("Rotation", SequenceBlackboardLayout.TypeQuaternion, index: 24);
        Assert.True(blob.TryFindSlots(out int position, out int rotation));
        Assert.Equal(8, position);
        Assert.Equal(24, rotation);
    }

    /// <summary>
    /// <b>R30 finding 5.</b> A <c>sizeof</c> that <em>throws</em> is read as 0 and warns; it does
    /// not close the walk either. R28 finding 2's real complaint — that the catch returned
    /// without touching a gate that defaulted <b>open</b> — is answered by the gate defaulting
    /// closed and being opened by the constants, not by latching on the throw.
    /// </summary>
    [Fact]
    public void A_throwing_sizeof_leaves_the_blob_walk_running()
    {
        string? note = SequenceBlackboardLayout.InputElementReadingLog(0);
        Assert.NotNull(note);
        Assert.Contains("sizeof was unavailable", note!, StringComparison.Ordinal);
        Assert.Contains("keeps striding 76 from the dump", note!, StringComparison.Ordinal);
        Assert.DoesNotContain("disabled", note!, StringComparison.Ordinal);
        Assert.True(SequenceBlackboardLayout.ReadingLogIsWarning(
            0,
            SequenceBlackboardLayout.InputElementStride,
            SequenceBlackboardLayout.InputElementPackedBytes,
            SequenceBlackboardLayout.InputElementAlignment));
        Assert.True(SequenceBlackboardLayout.InputElementWalkAllowed());
    }

    /// <summary>
    /// EV17, the live <c>path=none</c> regression. EV15/EV16 made <c>TypeManager</c>'s answer
    /// authoritative for these two buffers and let a nonzero disagreement latch the gate — and
    /// its <c>TypeSize</c> reads 0 for ordinary components while its <c>SizeInChunk</c> reported
    /// 515 for a 32-byte <c>Follower</c>. Because that gate was in the predicate
    /// <c>TryGetSlots</c> read, a client that had been re-pointing beams fine started logging
    /// <c>path=none … this sequence cannot be re-pointed</c> for every beam.
    /// </summary>
    [Fact]
    public void A_disagreeing_TypeManager_warns_but_never_closes_the_gate()
    {
        const int dump = BufferElementGuard.BlackboardElementSize;
        const int packed = BufferElementGuard.BlackboardElementPackedBytes;
        const int alignment = BufferElementGuard.BlackboardElementAlignment;
        Assert.True(BufferElementGuard.TypeManagerDisagrees(515, dump));
        // A 0 is the documented trap, not an answer, and says nothing at all.
        Assert.False(BufferElementGuard.TypeManagerDisagrees(0, dump));

        var gate = default(LayoutOnceGate);
        gate.Check(BufferElementGuard.WritesAllowed(dump, packed, alignment), "must not be logged");
        Assert.True(gate.Enabled);

        string line = BufferElementGuard.TypeManagerNote("BlackboardElement", 515, 1);
        Assert.Contains("the dump says 1", line, StringComparison.Ordinal);
        Assert.Contains("raw buffer writes stay on", line, StringComparison.Ordinal);
        Assert.DoesNotContain("disabled", line, StringComparison.Ordinal);
    }

    /// <summary>
    /// R29 finding 19. "Nothing answered" and "an answer that contradicts the dump" are
    /// different facts and still say so, because an operator reading
    /// `buffer element size is 0, not 20` could not tell which had happened.
    ///
    /// <para><b>R30 finding 5</b> changed what they are: both are notes now, neither is a
    /// refusal, and both have to say what the walk went on doing. The four log shapes the module
    /// can produce for a size reading are pinned here as the contract
    /// <c>docs/effects.md</c> publishes.</para>
    /// </summary>
    [Fact]
    public void The_size_reading_lines_name_both_sources_and_refuse_nothing()
    {
        string? unreadable = BufferElementGuard.SizeReadingLog("LineRendererPositionElement", 0, 20, 20, 4);
        Assert.Equal(
            "Effects: LineRendererPositionElement sizeof was unavailable; the raw walk keeps"
            + " striding 20 from the dump.",
            unreadable);

        string? stale = BufferElementGuard.SizeReadingLog("BlackboardElement", 4, 1, 1, 1);
        Assert.Equal(
            "Effects: BlackboardElement sizeof is 4, which is neither the dump's 1-byte stride"
            + " nor its 1-byte field extent; the raw walk keeps striding 1 (the dump is the"
            + " authority) — this reading is evidence the dump may be stale.",
            stale);

        // The two are distinguishable, which is what R29 finding 19 asked for...
        Assert.NotEqual(unreadable, stale);
        // ...and neither claims the module is off, which is what R30 finding 5 asked for.
        Assert.DoesNotContain("disabled", unreadable!, StringComparison.Ordinal);
        Assert.DoesNotContain("disabled", stale!, StringComparison.Ordinal);

        // The unpadded note, the one reading this interop actually produces.
        Assert.Equal(
            "Effects: SequenceInputElement sizeof is 73 (the managed struct without the native"
            + " array's tail padding); the raw walk uses the dump stride 76 and stays on.",
            SequenceBlackboardLayout.InputElementReadingLog(73));

        // And a healthy reading says nothing at all.
        Assert.Null(BufferElementGuard.SizeReadingLog("LineRendererPositionElement", 20, 20, 20, 4));
        Assert.Null(SequenceBlackboardLayout.InputElementReadingLog(76));
    }

    /// <summary>
    /// <b>EV17, second pass — the live <c>path=none</c> regression, verbatim.</b> The client on
    /// the first EV17 build logged
    /// <c>Effects: SequenceInputElement stride is 73, not 76; blackboard re-point disabled for
    /// this build.</c> and every beam went <c>path=none</c>.
    ///
    /// <para>The managed <c>sizeof</c> of the Il2CppInterop-generated struct is <b>73</b> —
    /// <c>Name@0</c> (64) + <c>Index@64</c> (4) + <c>Type@68</c> (4) +
    /// <c>IsInputParameter@72</c> (1), with no tail padding, because the generated
    /// <c>[StructLayout(LayoutKind.Explicit)]</c> struct does not carry the native array's
    /// alignment. The <b>native</b> stride the blob is laid out with is <b>76</b> (server
    /// metadata). Neither <c>sizeof</c> (first EV17 pass) nor
    /// <c>TypeManager</c> (EV15/EV16) is authority for a native array stride: the metadata is.
    /// A 73 keeps the gate open and the walk strides 76.</para>
    /// </summary>
    [Fact]
    public void A_managed_sizeof_without_the_native_tail_padding_keeps_the_gate_open()
    {
        // The exact live reading.
        Assert.Equal(73, SequenceBlackboardLayout.InputElementPackedBytes);
        Assert.Equal(76, SequenceBlackboardLayout.InputElementStride);
        Assert.Equal(
            SequenceBlackboardLayout.InputIsInputParameterOffset + 1,
            SequenceBlackboardLayout.InputElementPackedBytes);

        Assert.Equal("Unpadded", SequenceBlackboardLayout.AgreeInputElement(73).ToString());

        // The gate the production probe drives stays OPEN. On the first EV17 pass this latched.
        var gate = default(LayoutOnceGate);
        Assert.Null(gate.Check(SequenceBlackboardLayout.InputElementWalkAllowed(), "must not be logged"));
        Assert.True(gate.Enabled);

        // ...and the walk still strides 76, so a blob laid out natively resolves. This is the
        // whole point: a 73-byte managed sizeof must not change a single offset.
        //
        // R30 finding 22: the fixture is built from FakeSequenceInputBlob.NativeStride, a
        // literal 76 the production code does not read. Before, it was built from
        // SequenceBlackboardLayout.InputElementStride, so changing that constant rebuilt the
        // blob to match and the blob still resolved — the test restated the constant instead
        // of measuring it.
        var blob = new FakeSequenceInputBlob(blobOffset: 8);
        blob.AddInput("Position", SequenceBlackboardLayout.TypeFloat3, index: 8);
        blob.AddInput("Rotation", SequenceBlackboardLayout.TypeQuaternion, index: 24);
        Assert.True(blob.TryFindSlots(out int position, out int rotation));
        Assert.Equal(8, position);
        Assert.Equal(24, rotation);
        // Element 1 of a walk starting at blob offset 8 sits at 8 + 76, not 8 + 73.
        Assert.Equal(8 + 76, SequenceBlackboardLayout.ElementOffset(8, 2, 1));

        // The cross-check line is informational and must not read as a refusal.
        string? note = SequenceBlackboardLayout.InputElementReadingLog(73);
        Assert.NotNull(note);
        Assert.Contains("uses the dump stride 76 and stays on", note!, StringComparison.Ordinal);
        Assert.DoesNotContain("disabled", note!, StringComparison.Ordinal);
        Assert.False(SequenceBlackboardLayout.ReadingLogIsWarning(
            73,
            SequenceBlackboardLayout.InputElementStride,
            SequenceBlackboardLayout.InputElementPackedBytes,
            SequenceBlackboardLayout.InputElementAlignment));
    }

    /// <summary>
    /// <b>R30 finding 14.</b> <c>Agree</c> compared a reading against two compile-time constants
    /// and never checked those constants against each other, so an impossible pair read as
    /// agreement. Every case, enumerated:
    ///
    /// <list type="bullet">
    /// <item>a pair that violates the invariant is <c>Unavailable</c> for <em>any</em> reading,
    /// including one that equals one of the two — this is the row that used to come back
    /// <c>Exact</c>, opening the walk with no log at all;</item>
    /// <item>a reading that is neither number, on a valid pair, is <c>Mismatch</c>;</item>
    /// <item><c>Exact</c> and <c>Unpadded</c> require a valid pair as well.</item>
    /// </list>
    /// </summary>
    [Theory]
    // ---- valid pairs (stride == RoundUp(extent, alignment)) ------------------
    [InlineData(76, 76, 73, 4, "Exact")]
    [InlineData(73, 76, 73, 4, "Unpadded")]
    [InlineData(80, 76, 73, 4, "Mismatch")]
    [InlineData(72, 76, 73, 4, "Mismatch")]
    [InlineData(0, 76, 73, 4, "Unavailable")]
    [InlineData(-1, 76, 73, 4, "Unavailable")]
    // No tail padding: Exact and Unpadded collapse, and Exact wins.
    [InlineData(20, 20, 20, 4, "Exact")]
    [InlineData(16, 20, 20, 4, "Mismatch")]
    [InlineData(1, 1, 1, 1, "Exact")]
    [InlineData(4, 1, 1, 1, "Mismatch")]
    // ---- invalid pairs: Unavailable whatever was observed --------------------
    // The extent is larger than the stride.
    [InlineData(76, 73, 76, 4, "Unavailable")]
    // The stride is not the extent rounded up: 80 is 4-aligned but two steps past 73.
    [InlineData(80, 80, 73, 4, "Unavailable")]
    // The stride is not a multiple of the alignment at all.
    [InlineData(74, 74, 73, 4, "Unavailable")]
    // A reading that equals the stride does NOT rescue an impossible pair. This is R30 finding
    // 14's row: on the old predicate it was "Exact", silently.
    [InlineData(76, 76, 76, 8, "Unavailable")]
    // Alignment nonsense.
    [InlineData(20, 20, 20, 0, "Unavailable")]
    [InlineData(20, 20, 20, 3, "Unavailable")]
    [InlineData(20, 20, 20, -4, "Unavailable")]
    // Non-positive constants.
    [InlineData(20, 0, 0, 4, "Unavailable")]
    [InlineData(20, 20, 0, 4, "Unavailable")]
    public void Agree_checks_its_two_constants_against_each_other(
        int observed, int dumpStride, int dumpPacked, int alignment, string expected)
    {
        Assert.Equal(
            expected,
            SequenceBlackboardLayout.Agree(observed, dumpStride, dumpPacked, alignment).ToString());

        // An Unavailable caused by the pair rather than by the reading names the pair, so the
        // one line an operator sees points at the constants.
        bool pairOk = SequenceBlackboardLayout.PaddingInvariantHolds(dumpStride, dumpPacked, alignment);
        Assert.Equal(pairOk, SequenceBlackboardLayout.WalkAllowed(dumpStride, dumpPacked, alignment));
        string? line = SequenceBlackboardLayout.ReadingLogFor("T", observed, dumpStride, dumpPacked, alignment);
        if (!pairOk)
        {
            Assert.NotNull(line);
            Assert.Contains("dump constants contradict each other", line!, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// <b>R30 finding 22.</b> <c>InputElementStride = 76</c> was a literal no dump line backed:
    /// the dump states four field offsets and no size. It is derived now — the last field's
    /// offset plus its one byte, rounded up to the element alignment — and this pins the
    /// derivation against the offsets, and the citation against <c>docs/effects.md</c>, so the
    /// constant and the evidence for it cannot drift apart.
    /// </summary>
    [Fact]
    public void The_input_element_stride_is_derived_from_the_dump_offsets()
    {
        // The dump states these three and nothing else. Everything below is arithmetic.
        Assert.Equal(0x40, SequenceBlackboardLayout.InputIndexOffset);
        Assert.Equal(0x44, SequenceBlackboardLayout.InputTypeOffset);
        Assert.Equal(0x48, SequenceBlackboardLayout.InputIsInputParameterOffset);
        Assert.Equal(1, SequenceBlackboardLayout.InputIsInputParameterBytes);
        Assert.Equal(4, SequenceBlackboardLayout.InputElementAlignment);

        // Field extent = last offset + its size. Stride = that, rounded up to the alignment.
        Assert.Equal(0x48 + 1, SequenceBlackboardLayout.InputElementPackedBytes);
        Assert.Equal(73, SequenceBlackboardLayout.InputElementPackedBytes);
        Assert.Equal(76, SequenceBlackboardLayout.RoundUpToAlignment(
            SequenceBlackboardLayout.InputElementPackedBytes,
            SequenceBlackboardLayout.InputElementAlignment));
        Assert.Equal(76, SequenceBlackboardLayout.InputElementStride);
        Assert.True(SequenceBlackboardLayout.InputElementWalkAllowed());

        // RoundUpToAlignment is the derivation, so it is worth its own rows.
        Assert.Equal(76, SequenceBlackboardLayout.RoundUpToAlignment(73, 4));
        Assert.Equal(76, SequenceBlackboardLayout.RoundUpToAlignment(76, 4));
        Assert.Equal(80, SequenceBlackboardLayout.RoundUpToAlignment(77, 4));
        Assert.Equal(0, SequenceBlackboardLayout.RoundUpToAlignment(0, 4));
        Assert.Equal(-1, SequenceBlackboardLayout.RoundUpToAlignment(5, 0));
        Assert.Equal(-1, SequenceBlackboardLayout.RoundUpToAlignment(-1, 4));
        // No int overflow on the way up.
        Assert.Equal(-1, SequenceBlackboardLayout.RoundUpToAlignment(int.MaxValue, 8));

        // And the metadata types behind the offsets are on the record in docs/effects.md, so the
        // citation is checkable rather than a comment nobody can verify.
        string doc = ReadRepoFile("docs", "effects.md");
        Assert.Contains(
            SequenceBlackboardLayout.InputElementDumpLines, doc, StringComparison.Ordinal);
        Assert.Contains(
            BuffSequenceLayout.DumpLines, doc, StringComparison.Ordinal);
        Assert.Contains(
            BuffSequenceLayout.ConditionDumpLines, doc, StringComparison.Ordinal);
        Assert.Contains(
            BufferElementGuard.LineRendererPositionElementDumpLines, doc, StringComparison.Ordinal);
        Assert.Contains(
            BufferElementGuard.BlackboardElementDumpLines, doc, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>R30 finding 2.</b> <c>TryReadBuffSequenceGuids</c> strode <b>24</b> over a 16-byte
    /// element, behind a comment that cited no dump line and claimed
    /// <c>BlobAssetReference</c> was 16 bytes. It is 8: one <c>BlobAssetReferenceData</c>, itself
    /// a union of <c>byte* m_Ptr</c> and <c>long m_Align8Union</c> both at <c>0x0</c>.
    ///
    /// <para>Three entries is where it becomes visible, and this drives both strides over one
    /// byte array: at 16 the walk reads the three authored guid hashes; at 24 element 1 reads
    /// the low dword of element 1's <c>Condition</c> <b>pointer</b> — a live heap address fed
    /// straight to <c>PrefabLookup</c> — and element 2 lands past the end of the buffer
    /// entirely.</para>
    /// </summary>
    [Fact]
    public void The_buff_sequence_walk_strides_sixteen_not_twenty_four()
    {
        // Derived from the dump offsets, like every other stride in the module.
        Assert.Equal(0x8, BuffSequenceLayout.ConditionOffset);
        Assert.Equal(8, BuffSequenceLayout.ConditionBytes);
        Assert.Equal(16, BuffSequenceLayout.PackedBytes);
        Assert.Equal(16, BuffSequenceLayout.Stride);
        Assert.True(BuffSequenceLayout.WalkAllowed());

        var buffer = new FakeBuffSequenceBuffer();
        buffer.AddEntry(guidHash: 1001, conditionPointer: 0x7FF6_1234_5678L);
        buffer.AddEntry(guidHash: 1002, conditionPointer: 0x7FF6_ABCD_EF01L);
        buffer.AddEntry(guidHash: 1003, conditionPointer: 0x7FF6_0BAD_F00DL);
        Assert.Equal(3 * 16, buffer.Bytes.Length);

        // The production walk reads exactly the three authored hashes.
        Assert.Equal(new[] { 1001, 1002, 1003 }, buffer.WalkGuidHashes());

        // The old walk: `*(int*)((byte*)raw + i * 24)`. Element 0 is right by luck; element 1
        // reads the low dword of element 1's Condition pointer; element 2 is off the end.
        Assert.Equal(1001, buffer.ReadIntAt(0 * 24));
        Assert.Equal(unchecked((int)0xABCDEF01), buffer.ReadIntAt(1 * 24));
        Assert.NotEqual(1002, buffer.ReadIntAt(1 * 24));
        Assert.False(buffer.Fits(2 * 24, 4));

        // And the bound is the buffer extent, not the reported length: a length the interop
        // invented cannot walk past the end.
        Assert.Equal(-1L, BuffSequenceLayout.ElementOffset(3, 3));
        Assert.Equal(-1L, BuffSequenceLayout.ElementOffset(-1, 3));
        Assert.Equal(32L, BuffSequenceLayout.ElementOffset(2, 3));
        Assert.Equal(
            "Impossible",
            BuffSequenceLayout.Classify(BuffSequenceLayout.MaxElements + 1).ToString());
        Assert.Equal("Impossible", BuffSequenceLayout.Classify(-1).ToString());
        Assert.Equal("Impossible", BuffSequenceLayout.Classify(int.MaxValue).ToString());
        // An empty BuffSequence buffer is an ordinary buff prefab, not a misread.
        Assert.Equal("TooSmall", BuffSequenceLayout.Classify(0).ToString());
        Assert.Equal("Ok", BuffSequenceLayout.Classify(1).ToString());

        // A truncated span stops the walk instead of reading past it.
        // Element 1's GuidHash is bytes 16..19, so a 19-byte span cuts it and the walk stops.
        Assert.False(BuffSequenceLayout.TryReadGuidHash(buffer.Bytes.AsSpan(0, 19), 1, 3, out _));
        Assert.True(BuffSequenceLayout.TryReadGuidHash(buffer.Bytes.AsSpan(0, 20), 1, 3, out int at1));
        Assert.Equal(1002, at1);
    }

    /// <summary>
    /// EV17 second pass. The other two Effects raw-buffer element types were checked against the
    /// dump for the same tail-padding trap and do not have it, so routing them through the same
    /// rule changes nothing — which is what this pins, so a future edit cannot quietly give one
    /// of them a padded stride without the constants moving too.
    ///
    /// <para><c>LineRendererPositionElement</c>: <c>int@0</c>, <c>int@4</c>, <c>float3@8</c> =
    /// 20, already 4-aligned (client metadata; the interop stub carries the same
    /// three offsets). <c>BlackboardElement</c>: one <c>byte@0</c> = 1 (server
    /// metadata).</para>
    /// </summary>
    [Fact]
    public void The_other_buffer_elements_have_no_tail_padding_to_lose()
    {
        // The dump's field offsets, and the size they imply with no padding.
        Assert.Equal(4, LineRendererBufferLayout.PositionIndexOffset);
        Assert.Equal(8, LineRendererBufferLayout.PositionValueOffset);
        Assert.Equal(
            LineRendererBufferLayout.PositionValueOffset + LineRendererBufferLayout.PositionFieldBytes,
            BufferElementGuard.LineRendererPositionElementSize);
        Assert.Equal(1, BufferElementGuard.BlackboardElementSize);

        // Padded == unpadded, so `Agree` collapses to plain equality for both. Each carries its
        // own alignment now (R30 finding 14): 4 for the three-field struct, 1 for the byte.
        Assert.Equal(4, BufferElementGuard.LineRendererPositionElementAlignment);
        Assert.Equal(1, BufferElementGuard.BlackboardElementAlignment);
        Assert.Equal("Exact", SequenceBlackboardLayout.Agree(20, 20, 20, 4).ToString());
        Assert.Equal("Mismatch", SequenceBlackboardLayout.Agree(16, 20, 20, 4).ToString());
        Assert.Equal("Exact", SequenceBlackboardLayout.Agree(1, 1, 1, 1).ToString());
        Assert.Equal("Mismatch", SequenceBlackboardLayout.Agree(4, 1, 1, 1).ToString());

        // And a genuinely wrong sizeof still produces a line for them — just not a refusal
        // (R30 finding 5).
        Assert.NotNull(BufferElementGuard.SizeReadingLog(
            "LineRendererPositionElement",
            16,
            BufferElementGuard.LineRendererPositionElementSize,
            BufferElementGuard.LineRendererPositionElementPackedBytes,
            BufferElementGuard.LineRendererPositionElementAlignment));
        Assert.True(BufferElementGuard.WritesAllowed(
            BufferElementGuard.LineRendererPositionElementSize,
            BufferElementGuard.LineRendererPositionElementPackedBytes,
            BufferElementGuard.LineRendererPositionElementAlignment));
    }

    /// <summary>
    /// EV17, the live regression's third leg. The blackboard <b>buffer length</b> refusal was
    /// wired to <c>_elementSizeGate</c> — the gate <c>LayoutOk</c> reads and <c>TryGetSlots</c>
    /// consults — and latched on every refusal, empty included. So one sequence graph with no
    /// registers turned slot resolution off for every sequence for the rest of the process.
    /// Same three-way split as the line buffer (R29 finding 4).
    /// </summary>
    [Theory]
    [InlineData(0, "TooSmall")]
    [InlineData(1, "Ok")]
    [InlineData(64, "Ok")]
    [InlineData(SequenceBlackboardLayout.MaxBufferBytes, "Ok")]
    [InlineData(-1, "Impossible")]
    [InlineData(SequenceBlackboardLayout.MaxBufferBytes + 1, "Impossible")]
    [InlineData(int.MaxValue, "Impossible")]
    [InlineData(int.MinValue, "Impossible")]
    public void An_empty_blackboard_buffer_is_not_a_layout_failure(int length, string expected)
    {
        Assert.Equal(expected, SequenceBlackboardLayout.ClassifyBufferLength(length).ToString());
        // The old single predicate could not tell 0 from int.MaxValue.
        Assert.Equal(
            expected == "Ok",
            SequenceBlackboardLayout.BufferLengthOk(length));
        Assert.DoesNotContain(
            "disabled", SequenceBlackboardLayout.BufferEmptyLog(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The coordinator's ask (3): a sequence carrying valid <c>Position</c> / <c>Rotation</c>
    /// inputs must resolve. EV17 moved the blob walk out of the pointer code into
    /// <see cref="SequenceBlackboardLayout.TryFindSlots"/>, so this is the same code the client
    /// runs — the live evidence (positionSlot=8, rotationSlot=24) is reproduced here byte for
    /// byte.
    /// </summary>
    [Fact]
    public void A_sequence_with_position_and_rotation_inputs_resolves_both_slots()
    {
        var blob = new FakeSequenceInputBlob(blobOffset: 8);
        blob.AddInput("Position", SequenceBlackboardLayout.TypeFloat3, index: 8);
        blob.AddInput("Rotation", SequenceBlackboardLayout.TypeQuaternion, index: 24);

        Assert.True(blob.TryFindSlots(out int position, out int rotation));
        Assert.Equal(8, position);
        Assert.Equal(24, rotation);

        // The live shape: the two registers are not the first entries in the blob.
        var padded = new FakeSequenceInputBlob(blobOffset: 8);
        padded.AddInput("Scale", SequenceBlackboardLayout.TypeFloat3, index: 0);
        padded.AddInput("Target", 10 /* Entity */, index: 40);
        padded.AddInput("Position", SequenceBlackboardLayout.TypeFloat3, index: 8);
        padded.AddInput("Rotation", SequenceBlackboardLayout.TypeQuaternion, index: 24);
        Assert.True(padded.TryFindSlots(out int p2, out int r2));
        Assert.Equal(8, p2);
        Assert.Equal(24, r2);
    }

    /// <summary>
    /// The negative half: the sequence families that genuinely cannot be re-pointed this way
    /// must still say so, and a wrong <em>type</em> on the right name is not a match.
    /// </summary>
    [Fact]
    public void A_sequence_without_those_inputs_resolves_nothing()
    {
        var none = new FakeSequenceInputBlob(blobOffset: 8);
        none.AddInput("Scale", SequenceBlackboardLayout.TypeFloat3, index: 0);
        Assert.False(none.TryFindSlots(out int p, out int r));
        Assert.Equal(-1, p);
        Assert.Equal(-1, r);

        // Right name, wrong register type.
        var mistyped = new FakeSequenceInputBlob(blobOffset: 8);
        mistyped.AddInput("Position", SequenceBlackboardLayout.TypeQuaternion, index: 8);
        Assert.False(mistyped.TryFindSlots(out _, out _));

        // A negative Index is skipped rather than used as an offset.
        var negative = new FakeSequenceInputBlob(blobOffset: 8);
        negative.AddInput("Position", SequenceBlackboardLayout.TypeFloat3, index: -4);
        Assert.False(negative.TryFindSlots(out _, out _));

        // Only one of the two is enough to be worth re-pointing.
        var half = new FakeSequenceInputBlob(blobOffset: 8);
        half.AddInput("Position", SequenceBlackboardLayout.TypeFloat3, index: 8);
        Assert.True(half.TryFindSlots(out int only, out int missing));
        Assert.Equal(8, only);
        Assert.Equal(-1, missing);
    }

    /// <summary>
    /// The walk's bounds still hold, and a refused walk resolves nothing rather than reading
    /// past the blob (R27 finding 5.2).
    /// </summary>
    [Fact]
    public void The_input_walk_still_refuses_an_out_of_range_blob()
    {
        Assert.False(SequenceBlackboardLayout.TryFindSlots(
            new byte[64], offset: 0, count: 1, out _, out _));
        Assert.False(SequenceBlackboardLayout.TryFindSlots(
            new byte[64], offset: 8, count: 0, out _, out _));
        Assert.False(SequenceBlackboardLayout.TryFindSlots(
            new byte[64], offset: 8, count: SequenceBlackboardLayout.MaxInputs + 1, out _, out _));
        Assert.False(SequenceBlackboardLayout.TryFindSlots(
            new byte[64], offset: SequenceBlackboardLayout.MaxBlobOffset, count: 1, out _, out _));

        // A count the walk accepts but a span too short for it stops at the short element
        // instead of reading past the end.
        var blob = new FakeSequenceInputBlob(blobOffset: 8);
        blob.AddInput("Position", SequenceBlackboardLayout.TypeFloat3, index: 8);
        Assert.False(blob.TryFindSlotsWithTruncatedSpan(out _, out _));
    }

    /// <summary>
    /// EV17. The diagnostic is what a live run needs to answer "why is the path none" without
    /// another round trip: the observed stride, the blob offset and count, the resolved slots
    /// and the inputs the walk actually saw.
    /// </summary>
    [Fact]
    public void The_walk_diagnostic_names_the_numbers_the_walk_read()
    {
        var blob = new FakeSequenceInputBlob(blobOffset: 8);
        blob.AddInput("Position", SequenceBlackboardLayout.TypeFloat3, index: 8);
        blob.AddInput("Rotation", SequenceBlackboardLayout.TypeQuaternion, index: 24);

        Assert.Equal("Position:3@8, Rotation:9@24", blob.DescribeInputs());

        // EV17 second pass: `stride=` is the number the walk actually uses (the dump's), and the
        // managed reading is reported beside it as `sizeof=`. The first pass printed the
        // reading as the stride, which is what made 73 look like the walk's stride.
        Assert.Equal(
            "Effects: sequence blackboard probe stride=76 (dump 76) sizeof=73 blobOffset=8"
            + " count=2 position=8 rotation=24 inputs=[Position:3@8, Rotation:9@24]",
            SequenceBlackboardLayout.WalkDiagnostic(73, 8, 2, 8, 24, blob.DescribeInputs()));

        // A refused walk still reports what it saw, so a stride mismatch reads differently from
        // a sequence that simply has no registers.
        Assert.Contains(
            "stride=76 (dump 76) sizeof=80",
            SequenceBlackboardLayout.WalkDiagnostic(80, 8, 2, -1, -1, "walk refused"),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// R27 finding 17. Neither raw buffer write path checked its element size, and the two read
    /// the same accessor differently: <c>TryWriteLineRendererPositions</c> as an element count
    /// times a hard-coded 20, <c>SequenceBlackboard</c> as a byte count (correct only while
    /// <c>BlackboardElement</c> is one byte).
    ///
    /// <para><b>R30 findings 5 and 14.</b> The number each path multiplies by is a dump
    /// constant, so what has to be checked is the constant, and the check is the one the
    /// arithmetic depends on: the size, the alignment and the extent have to agree. The exact
    /// text below is the contract published in <c>docs/effects.md</c>'s expected log lines.</para>
    /// </summary>
    [Fact]
    public void Buffer_element_sizes_are_checked_against_the_dump()
    {
        // Both buffer elements are unpadded, so their size, extent and alignment agree and both
        // walks are allowed.
        Assert.True(BufferElementGuard.WritesAllowed(
            BufferElementGuard.BlackboardElementSize,
            BufferElementGuard.BlackboardElementPackedBytes,
            BufferElementGuard.BlackboardElementAlignment));
        Assert.True(BufferElementGuard.WritesAllowed(
            BufferElementGuard.LineRendererPositionElementSize,
            BufferElementGuard.LineRendererPositionElementPackedBytes,
            BufferElementGuard.LineRendererPositionElementAlignment));

        // A size that is not a multiple of its own alignment cannot be a native array element,
        // and that — not a reading — is what refuses the walk.
        Assert.False(BufferElementGuard.WritesAllowed(18, 20, 4));
        Assert.False(BufferElementGuard.WritesAllowed(0, 20, 4));
        Assert.False(BufferElementGuard.WritesAllowed(20, 20, 0));
        Assert.False(BufferElementGuard.WritesAllowed(20, 20, 3));

        Assert.Equal(
            "Effects: LineRendererPositionElement sizeof is 16, which is neither the dump's"
            + " 20-byte stride nor its 20-byte field extent; the raw walk keeps striding 20 (the"
            + " dump is the authority) — this reading is evidence the dump may be stale.",
            BufferElementGuard.SizeReadingLog("LineRendererPositionElement", 16, 20, 20, 4));

        var gate = default(LayoutOnceGate);
        gate.Check(BufferElementGuard.WritesAllowed(18, 20, 4), "refused");
        Assert.True(gate.Disabled);
    }

    /// <summary>
    /// <b>R31 finding 14.</b> The blob walk was bounded by <c>MaxBlobBytes</c>, a 4 MiB constant
    /// nothing in the process backs, and <c>TryFindSlots</c>' per-element
    /// <c>at + stride &gt; blob.Length</c> check was circular: the span it strides was itself
    /// sized from the same untrusted <c>m_OffsetPtr</c> / <c>m_Length</c>.
    ///
    /// <para>The scenario verbatim: a stale or differently-versioned 256-byte
    /// <c>SequenceInput</c> blob whose root reads 8 / 512. The old bound accepts it (end =
    /// 38,920 &lt; 4 MiB) and the walk reads ~38 KB past the allocation; across an unmapped page
    /// that is an access violation the <c>catch (Exception)</c> around the walk cannot catch.</para>
    /// </summary>
    [Fact]
    public void A_blob_whose_extent_is_smaller_than_its_claimed_count_refuses_the_walk()
    {
        // The old gate — this is exactly what let the walk out of the allocation.
        Assert.True(SequenceBlackboardLayout.WalkOk(8, 512));
        Assert.Equal(38_920L, SequenceBlackboardLayout.WalkEnd(8, 512));

        // The new one reads the blob's own extent and refuses.
        Assert.False(SequenceBlackboardLayout.WalkFitsBlob(8, 512, 256));

        var runtime = new FakeSequenceRuntime();
        var stale = FakeBlobAllocation.Lying(allocationBytes: 256, claimedOffset: 8, claimedCount: 512);
        Assert.False(runtime.TryResolve(stale, out int position, out int rotation));
        Assert.Equal(-1, position);
        Assert.Equal(-1, rotation);
        // The measurement, not a claim: no read left the allocation.
        Assert.False(runtime.ReadPastAllocation);
        Assert.Contains(
            runtime.Warnings,
            w => w.Contains("BlobAssetHeader.Length read 256", StringComparison.Ordinal)
                 && w.Contains("would end at 38920", StringComparison.Ordinal));

        // A fact about ONE blob (R30 finding 9): a healthy blob in the same generation still
        // walks, and the warning is written once.
        int warningsAfterStale = runtime.Warnings.Count;
        Assert.False(runtime.TryResolve(stale, out _, out _));
        Assert.Equal(warningsAfterStale, runtime.Warnings.Count);

        var healthy = FakeBlobAllocation.WithInputs(("Position", 3, 8), ("Rotation", 9, 24));
        Assert.True(runtime.TryResolve(healthy, out int p, out int r));
        Assert.Equal(8, p);
        Assert.Equal(24, r);
        Assert.False(runtime.ReadPastAllocation);

        // The extent classification itself: 0 is "the header did not read as a length", and a
        // number past MaxBlobBytes is not one either. Both refuse the call.
        Assert.Equal(
            SequenceBlackboardLayout.ExtentVerdict.Unreadable,
            SequenceBlackboardLayout.ClassifyBlobExtent(0));
        Assert.Equal(
            SequenceBlackboardLayout.ExtentVerdict.Unreadable,
            SequenceBlackboardLayout.ClassifyBlobExtent(-4));
        Assert.Equal(
            SequenceBlackboardLayout.ExtentVerdict.Impossible,
            SequenceBlackboardLayout.ClassifyBlobExtent(SequenceBlackboardLayout.MaxBlobBytes + 1));
        Assert.Equal(
            SequenceBlackboardLayout.ExtentVerdict.Ok,
            SequenceBlackboardLayout.ClassifyBlobExtent(256));

        // An exactly-fitting walk is allowed; one byte short is not.
        long end = SequenceBlackboardLayout.WalkEnd(8, 2);
        Assert.True(SequenceBlackboardLayout.WalkFitsBlob(8, 2, end));
        Assert.False(SequenceBlackboardLayout.WalkFitsBlob(8, 2, end - 1));

        // The header offsets are the dump's, and the read is behind the data pointer.
        Assert.Equal(32, SequenceBlackboardLayout.BlobAssetHeaderBytes);
        Assert.Equal(0x8, SequenceBlackboardLayout.BlobAssetHeaderLengthOffset);
        Assert.Equal(-24, SequenceBlackboardLayout.BlobAssetHeaderLengthDelta);
    }

    /// <summary>
    /// <b>R31 finding 20.</b> <c>WritesAllowed(dumpSize, alignment)</c> forwarded
    /// <c>dumpSize</c> as <em>both</em> stride and packed, so <c>PaddingInvariantHolds</c>
    /// reduced to "positive, and <c>dumpSize == RoundUp(dumpSize, alignment)</c>". With
    /// <c>BlackboardElementAlignment = 1</c> that is true for every positive value: mistype
    /// <c>BlackboardElementSize</c> to 4 and the sole gate on the blackboard buffer write — the
    /// one <c>docs/effects.md</c> calls the only thing that can refuse this walk — still said
    /// yes. Same shape one type over, the old wrong <c>BuffSequence</c> stride 24 satisfies
    /// <c>24 == RoundUp(24, 8)</c>, so <b>this gate would not have caught R30 finding 2</b>.
    ///
    /// <para>The fix is a second, independently sourced number: the field extent summed from the
    /// dump's own offsets. The rows below are the ones the degenerate check could not refuse.</para>
    /// </summary>
    [Theory]
    // The real pairs, from the dump. Both elements are unpadded, which is what made the old
    // check vacuous for them — and they must still be allowed.
    [InlineData(1, 1, 1, true)]      // BlackboardElement
    [InlineData(20, 20, 4, true)]    // LineRendererPositionElement
    [InlineData(16, 16, 8, true)]    // BuffSequence
    [InlineData(76, 73, 4, true)]    // SequenceInputElement, the one pair that genuinely differs
    // A mistyped size against a correct extent: the rows the degenerate invariant let through.
    [InlineData(4, 1, 1, false)]     // BlackboardElementSize typed as 4
    [InlineData(2, 1, 1, false)]
    [InlineData(24, 16, 8, false)]   // the wrong BuffSequence stride of R30 finding 2
    [InlineData(24, 20, 4, false)]
    [InlineData(80, 73, 4, false)]
    // A size smaller than the extent it is supposed to be the padded form of.
    [InlineData(16, 20, 4, false)]
    [InlineData(72, 73, 4, false)]
    // Nonsense arguments stay refused.
    [InlineData(0, 1, 1, false)]
    [InlineData(20, 0, 4, false)]
    [InlineData(20, 20, 0, false)]
    [InlineData(20, 20, 3, false)]   // alignment not a power of two
    public void The_buffer_write_gate_refuses_a_wrong_size_even_when_stride_equals_packed(
        int dumpSize, int dumpPacked, int alignment, bool allowed)
    {
        Assert.Equal(allowed, BufferElementGuard.WritesAllowed(dumpSize, dumpPacked, alignment));

        // The gate the production probe drives agrees, and a refusal names the pair rather than
        // blaming a reading.
        var gate = default(LayoutOnceGate);
        string? line = gate.Check(
            BufferElementGuard.WritesAllowed(dumpSize, dumpPacked, alignment),
            SequenceBlackboardLayout.ConstantsInvariantLog("T", dumpSize, dumpPacked, alignment));
        Assert.Equal(allowed, gate.Enabled);
        Assert.Equal(allowed, line == null);
        if (line != null)
        {
            Assert.Contains("stride " + dumpSize, line, StringComparison.Ordinal);
            Assert.Contains("field extent " + dumpPacked, line, StringComparison.Ordinal);
            Assert.DoesNotContain("sizeof", line, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The extents are summed from the dump's own field offsets rather than being a second copy
    /// of the size, which is the whole point of R31 finding 20.
    /// </summary>
    [Fact]
    public void The_buffer_element_extents_come_from_the_dumps_field_offsets()
    {
        Assert.Equal(
            BufferElementGuard.BlackboardElementValueOffset + BufferElementGuard.BlackboardElementValueBytes,
            BufferElementGuard.BlackboardElementPackedBytes);
        Assert.Equal(1, BufferElementGuard.BlackboardElementPackedBytes);

        Assert.Equal(
            BufferElementGuard.LineRendererPositionValueOffset + SequenceBlackboardLayout.Float3Bytes,
            BufferElementGuard.LineRendererPositionElementPackedBytes);
        Assert.Equal(20, BufferElementGuard.LineRendererPositionElementPackedBytes);
    }

    /// <summary>
    /// <b>R31 finding 26 / R30 open question 7 — the regression test EV16 and EV17 never had.</b>
    ///
    /// <para>Research §A35.1 diagnosed both EV17 regressions the same way: <em>the tested code
    /// was right and the untested predicate turned it off</em>. The pure layout helpers were
    /// always covered; the wiring that consults them lived inside <c>#if !PROGENITOR_TESTS</c>
    /// in <c>SequenceDriverLookup.cs</c>, so three batches of fixes to the helpers could not
    /// catch a fourth repetition. The wiring now lives in <see cref="SequenceWalkWiring"/> and a
    /// fake runtime drives it end to end.</para>
    ///
    /// <para>The fact: <b>a wrong <c>sizeof</c> reading with a correct dump stride keeps the
    /// walk on</b>, and a beam on such a build is still re-pointed. On EV15/EV16/EV17 each of
    /// these readings latched a process-wide gate and every beam went <c>path=none</c> while the
    /// dump-derived arithmetic underneath was byte-for-byte correct.</para>
    /// </summary>
    [Theory]
    [InlineData(76)]   // the dump's own stride
    [InlineData(73)]   // the unpadded managed struct, what this interop actually answers
    [InlineData(80)]   // a third value from an interop regeneration — the EV17 pass-2 shape
    [InlineData(0)]    // sizeof threw / unavailable — R28 finding 2's `catch { return; }` hole
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    public void A_wrong_sizeof_reading_keeps_the_walk_on_end_to_end_through_the_wiring(int sizeOf)
    {
        var runtime = new FakeSequenceRuntime { SequenceInputSizeOf = sizeOf };
        var blob = FakeBlobAllocation.WithInputs(("Position", 3, 8), ("Rotation", 9, 24));

        Assert.True(runtime.TryResolve(blob, out int position, out int rotation));
        Assert.Equal(8, position);
        Assert.Equal(24, rotation);
        Assert.False(runtime.ReadPastAllocation);

        // Whatever was said about the reading, nothing may say the module is off.
        foreach (string line in runtime.Warnings)
            Assert.DoesNotContain("disabled", line, StringComparison.Ordinal);
        foreach (string line in runtime.Infos)
            Assert.DoesNotContain("disabled", line, StringComparison.Ordinal);
    }

    /// <summary>
    /// The same fact for the <b>buffer write</b> half, which is where EV15/EV16 latched: a
    /// <c>sizeof</c> and a <c>TypeManager</c> that both contradict the dump still leave the
    /// write on, and neither latches. One entity with an empty blackboard buffer must not
    /// disable the write for every other sequence (EV17 / R30 finding 9).
    /// </summary>
    [Fact]
    public void Wrong_readings_and_one_bad_buffer_never_disable_the_blackboard_write()
    {
        var runtime = new FakeSequenceRuntime
        {
            BlackboardElementSizeOf = 4,        // contradicts the dump's 1
            TypeManagerElementSize = 515,       // the CLAUDE.md trap-7 reading, verbatim
        };

        Assert.True(runtime.TryWriteSlot(bufferLength: 64, slot: 8, bytes: 12));
        Assert.Contains(
            runtime.Warnings, w => w.Contains("raw buffer writes stay on", StringComparison.Ordinal));
        foreach (string line in runtime.Warnings)
            Assert.DoesNotContain("disabled", line, StringComparison.Ordinal);

        // An empty buffer is an ordinary sequence graph with no registers: skipped, logged once,
        // and it does not touch the next sequence.
        Assert.False(runtime.TryWriteSlot(bufferLength: 0, slot: 8, bytes: 12));
        // A length no buffer can have refuses THIS call only.
        Assert.False(runtime.TryWriteSlot(bufferLength: -3, slot: 8, bytes: 12));
        Assert.False(runtime.TryWriteSlot(bufferLength: int.MaxValue, slot: 8, bytes: 12));
        Assert.True(runtime.TryWriteSlot(bufferLength: 64, slot: 8, bytes: 12));

        // ...and the one-shot logs really are once per generation.
        int before = runtime.Warnings.Count + runtime.Infos.Count;
        runtime.TryWriteSlot(bufferLength: 0, slot: 8, bytes: 12);
        runtime.TryWriteSlot(bufferLength: -3, slot: 8, bytes: 12);
        Assert.Equal(before, runtime.Warnings.Count + runtime.Infos.Count);
    }

    /// <summary>
    /// The other side of the same wiring: the one thing that <em>can</em> close a gate is this
    /// module's own constants contradicting each other, and a closed gate really does stop the
    /// walk. Without this the fact above would pass on a gate that can never close.
    /// </summary>
    [Fact]
    public void The_wiring_still_refuses_when_the_dump_constants_contradict_each_other()
    {
        // The production constants are self-consistent, which is why the walk runs at all.
        Assert.True(SequenceBlackboardLayout.InputElementWalkAllowed());
        Assert.True(BufferElementGuard.WritesAllowed(
            BufferElementGuard.BlackboardElementSize,
            BufferElementGuard.BlackboardElementPackedBytes,
            BufferElementGuard.BlackboardElementAlignment));

        // Reset is what a fresh Core generation gets: the probes run again.
        var runtime = new FakeSequenceRuntime();
        var blob = FakeBlobAllocation.WithInputs(("Position", 3, 8));
        Assert.True(runtime.TryResolve(blob, out _, out _));
        Assert.True(runtime.InputGateChecked);
        runtime.Reset();
        Assert.False(runtime.InputGateChecked);
        Assert.True(runtime.TryResolve(blob, out _, out _));
    }

    [Fact]
    public void Recycle_state_transitions_once_and_never_hides_the_owned_line()
    {
        var state = default(DriverRecycleState);
        Assert.False(state.Recycled);

        var first = state.OnRecycled();
        Assert.True(state.Recycled);
        Assert.True(state.Logged);
        Assert.Equal(DriverRecycleState.RecycledLog, first.Log);
        // R27 finding 11: the clone-derived line stops, ours does not, and the watch survives.
        Assert.False(first.HideOwnedLine);
        Assert.True(first.HideGameLine);
        Assert.True(first.KeepWatching);
        Assert.True(state.GameLineHidden);

        var second = state.OnRecycled();
        Assert.Null(second.Log);
        Assert.False(second.HideOwnedLine);
        Assert.False(second.HideGameLine);
        Assert.True(second.KeepWatching);
        Assert.True(state.GameLineHidden);
    }

    /// <summary>
    /// R26 finding 9. Il2CppInterop can hand out two managed wrappers for one native object,
    /// so <c>ReferenceEquals</c> is not identity. Two distinct wrapper stand-ins with the same
    /// instance id must compare equal, and our own root must never be recorded as the pool
    /// parent — <c>RestorePoolParent</c> would park the game's clone under it and
    /// <c>DestroyRoot</c> would destroy it.
    /// </summary>
    [Fact]
    public void Pool_parent_identity_uses_instance_ids_not_wrapper_references()
    {
        var wrapperA = new object();
        var wrapperB = new object();
        Assert.False(ReferenceEquals(wrapperA, wrapperB));
        const int rootId = 4242;

        // Both wrappers front the same native root.
        Assert.True(PoolParentPolicy.SameObject(rootId, rootId));
        Assert.False(PoolParentPolicy.CanRecordPoolParent(rootId, rootId));
        Assert.True(PoolParentPolicy.CanRecordPoolParent(99, rootId));
        Assert.False(PoolParentPolicy.CanRecordPoolParent(PoolParentPolicy.NoInstanceId, rootId));
        // Unknown ids are never "the same object".
        Assert.False(PoolParentPolicy.SameObject(PoolParentPolicy.NoInstanceId, PoolParentPolicy.NoInstanceId));
    }

    [Fact]
    public void Recycled_parent_is_the_pool_parent_while_not_drawing()
    {
        const int rootId = 10;
        const int poolId = 20;
        Assert.True(PoolParentPolicy.IsRecycledParent(poolId, poolId, rootId, activeInHierarchy: false));
        // Still drawing: not recycled.
        Assert.False(PoolParentPolicy.IsRecycledParent(poolId, poolId, rootId, activeInHierarchy: true));
        // Under our root: ours, whatever the pool id says.
        Assert.False(PoolParentPolicy.IsRecycledParent(rootId, rootId, rootId, activeInHierarchy: false));
        // Somewhere else entirely.
        Assert.False(PoolParentPolicy.IsRecycledParent(33, poolId, rootId, activeInHierarchy: false));
    }

    // ---- Job FEFF2: the ownership check stops inferring the recycle from activeInHierarchy ----

    /// <summary>
    /// Job FEFF2. <c>BindDriverGo</c> reparents the pooled clone under our own root and the
    /// sequencer's take/return reparents it under <c>ParticleLifetimeSystem._ParticleParent</c>,
    /// so "the parent is still our root" is a positive ownership proof the
    /// game itself maintains. Once the bind has read that parent back, the verdict must not
    /// consult <c>activeInHierarchy</c> at all: a clone under a foreign parent is not ours
    /// whether or not it happens to be drawing.
    /// </summary>
    [Fact]
    public void Bound_clone_is_ours_only_while_it_is_still_parented_under_our_root()
    {
        const int rootId = 10;
        const int poolId = 20;
        const int foreignId = 30;

        Assert.Equal(
            DriverOwnership.Ours,
            PoolParentPolicy.DecideOwnership(
                parentRead: true, parentId: rootId, poolParentId: poolId, rootId: rootId,
                boundUnderRoot: true, activeSelf: true, activeInHierarchy: true));

        // Parked back in the pool.
        Assert.Equal(
            DriverOwnership.Recycled,
            PoolParentPolicy.DecideOwnership(
                parentRead: true, parentId: poolId, poolParentId: poolId, rootId: rootId,
                boundUnderRoot: true, activeSelf: false, activeInHierarchy: false));

        // The case the old check could not see: re-issued to somebody else's effect and drawing
        // again. IsRecycledParent returns false here on its first line (activeInHierarchy), and
        // the !self && !hierarchy fallback is false too, so this used to read as "still ours".
        Assert.False(PoolParentPolicy.IsRecycledParent(foreignId, poolId, rootId, activeInHierarchy: true));
        Assert.Equal(
            DriverOwnership.Recycled,
            PoolParentPolicy.DecideOwnership(
                parentRead: true, parentId: foreignId, poolParentId: poolId, rootId: rootId,
                boundUnderRoot: true, activeSelf: true, activeInHierarchy: true));

        // Job FBUFF17 (R45 finding 30) corrected this row. It used to assert Recycled with the
        // comment "reparented to the scene root: no parent id at all, still not ours" — but
        // NoInstanceId is not "no parent". ReadDriverOwnership writes it for a null Transform, a
        // null Transform.gameObject AND a GetInstanceID() that threw, so it is an unknown, and
        // an unknown must not take a transition the code treats as terminal. It is also no
        // evidence of a recycle: ParticlePool.ReturnInstance reparents under _ParticleParent,
        // which is the whole basis of the positive proof this branch uses.
        Assert.Equal(
            DriverOwnership.Unprovable,
            PoolParentPolicy.DecideOwnership(
                parentRead: true, parentId: PoolParentPolicy.NoInstanceId, poolParentId: poolId,
                rootId: rootId, boundUnderRoot: true, activeSelf: true, activeInHierarchy: true));
    }

    /// <summary>
    /// Job FBUFF17 (R45 finding 30). An unreadable parent id must be answered the same way as an
    /// unreadable root id — the mirror-image unknown one branch away — in both the proved and
    /// the unproved case, and it must not be folded into the terminal recycle. The write is
    /// refused either way; what changes is that the borrowed line is not given up on a read
    /// failure.
    /// </summary>
    [Fact]
    public void An_unknown_parent_id_is_unprovable_not_recycled()
    {
        const int rootId = 10;
        const int poolId = 20;

        Assert.Equal(
            DriverOwnership.Unprovable,
            PoolParentPolicy.DecideOwnership(
                parentRead: true, parentId: PoolParentPolicy.NoInstanceId, poolParentId: poolId,
                rootId: rootId, boundUnderRoot: true, activeSelf: false, activeInHierarchy: false));

        // Without the bind proof the old inference used to answer from activeSelf/hierarchy
        // alone, which is a guess over an id nobody read.
        Assert.Equal(
            DriverOwnership.Unprovable,
            PoolParentPolicy.DecideOwnership(
                parentRead: true, parentId: PoolParentPolicy.NoInstanceId, poolParentId: poolId,
                rootId: rootId, boundUnderRoot: false, activeSelf: true, activeInHierarchy: true));
        Assert.Equal(
            DriverOwnership.Unprovable,
            PoolParentPolicy.DecideOwnership(
                parentRead: true, parentId: PoolParentPolicy.NoInstanceId, poolParentId: poolId,
                rootId: rootId, boundUnderRoot: false, activeSelf: false, activeInHierarchy: false));

        // A real, readable foreign parent is still the positive evidence it always was.
        Assert.Equal(
            DriverOwnership.Recycled,
            PoolParentPolicy.DecideOwnership(
                parentRead: true, parentId: poolId, poolParentId: poolId, rootId: rootId,
                boundUnderRoot: true, activeSelf: false, activeInHierarchy: false));
    }

    /// <summary>
    /// Job FBUFF17 (R45 finding 30), the other half. FEFF2's three-state answer reached only one
    /// of its two call sites: <c>TryBindSequenceDriver</c> asked a bool shim
    /// (<c>IsDriverRecycled</c>) that folded <c>Unprovable</c> into "not recycled" — the exact
    /// fail-open the commit set out to remove — so on an unprovable frame the bind reported the
    /// driver live and ran <c>ApplyPending</c> while <c>DriveLateWrite</c> refused on the same
    /// frame. Source pin, because the site needs a live Unity clone.
    /// </summary>
    [Fact]
    public void The_bind_site_switches_on_the_same_three_state_ownership_answer()
    {
        var src = ReadRepoFile("src", "Progenitor.Core", "Effects", "UnityEffectRuntime.cs")
            .Replace("\r\n", "\n");

        // The shim is gone, so nothing can reintroduce the fold by calling it. Comment lines are
        // excluded: the note at the bind site names the removed method on purpose, so a reader
        // arriving from R45 finding 30 can grep for it.
        var code = new System.Text.StringBuilder();
        foreach (var line in src.Split('\n'))
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("//", StringComparison.Ordinal)
                || trimmed.StartsWith("*", StringComparison.Ordinal))
                continue;
            code.Append(line).Append('\n');
        }

        Assert.DoesNotContain("IsDriverRecycled", code.ToString(), StringComparison.Ordinal);

        int bind = src.IndexOf("internal bool TryBindSequenceDriver()", StringComparison.Ordinal);
        Assert.True(bind > 0, "TryBindSequenceDriver not found");
        int bindEnd = src.IndexOf("\n    public void DriveEndpoints()", bind, StringComparison.Ordinal);
        Assert.True(bindEnd > bind, "end of TryBindSequenceDriver not found");
        string body = src.Substring(bind, bindEnd - bind);

        SourcePin.AssertStatement(body, "switch (ReadDriverOwnership())");
        SourcePin.AssertStatement(body, "case DriverOwnership.Recycled:");
        // Unprovable: no write, no claimed bind — the caller's poll must keep looking.
        int unprovable = body.IndexOf("case DriverOwnership.Unprovable:", StringComparison.Ordinal);
        Assert.True(unprovable > 0, "the bind must handle Unprovable explicitly");
        int returnFalse = body.IndexOf("return false;", unprovable, StringComparison.Ordinal);
        int applyPending = body.IndexOf("ApplyPending();", unprovable, StringComparison.Ordinal);
        Assert.True(returnFalse > 0 && (applyPending < 0 || returnFalse < applyPending),
            "the Unprovable branch must return before it writes anything");
    }

    /// <summary>
    /// Job FEFF2. Writing endpoints into a <c>LineRenderer</c> we no longer own is a visible
    /// beam across the game's or another mod's effect, so evidence we could not read must mean
    /// "do not write" — never "probably still ours". The parent read is the load-bearing one and
    /// it used to be swallowed, leaving an unreadable parent to fall through to
    /// <c>!activeSelf &amp;&amp; !activeInHierarchy</c>, which is false for a drawing clone.
    /// <c>Unprovable</c> is deliberately distinct from <c>Recycled</c>: the recycle transition is
    /// terminal for the borrowed game line (R27 finding 11).
    /// </summary>
    [Fact]
    public void Unreadable_identity_refuses_the_write_without_taking_the_recycle_transition()
    {
        const int rootId = 10;
        const int poolId = 20;

        // The parent read threw. Nothing else can answer the question.
        Assert.Equal(
            DriverOwnership.Unprovable,
            PoolParentPolicy.DecideOwnership(
                parentRead: false, parentId: rootId, poolParentId: poolId, rootId: rootId,
                boundUnderRoot: true, activeSelf: true, activeInHierarchy: true));
        Assert.Equal(
            DriverOwnership.Unprovable,
            PoolParentPolicy.DecideOwnership(
                parentRead: false, parentId: rootId, poolParentId: poolId, rootId: rootId,
                boundUnderRoot: false, activeSelf: true, activeInHierarchy: true));

        // We hold the proof but our own root's id came back unknown, so there is nothing to
        // compare against: refuse rather than guess in either direction.
        Assert.Equal(
            DriverOwnership.Unprovable,
            PoolParentPolicy.DecideOwnership(
                parentRead: true, parentId: rootId, poolParentId: poolId,
                rootId: PoolParentPolicy.NoInstanceId,
                boundUnderRoot: true, activeSelf: true, activeInHierarchy: true));
    }

    /// <summary>
    /// Job FEFF2. When the bind never established the proof — <c>_root</c> was gone, or the
    /// reparent threw — the older inference is kept exactly as it was, so a write that used to
    /// land still lands. Only the unreadable-parent leg changes, and that is covered above.
    /// </summary>
    [Fact]
    public void Without_the_bind_proof_the_previous_inference_is_preserved()
    {
        const int rootId = 10;
        const int poolId = 20;
        const int foreignId = 30;

        // Parked under the recorded pool parent.
        Assert.Equal(
            DriverOwnership.Recycled,
            PoolParentPolicy.DecideOwnership(
                parentRead: true, parentId: poolId, poolParentId: poolId, rootId: rootId,
                boundUnderRoot: false, activeSelf: false, activeInHierarchy: false));

        // Deactivated anywhere: the old fallback.
        Assert.Equal(
            DriverOwnership.Recycled,
            PoolParentPolicy.DecideOwnership(
                parentRead: true, parentId: foreignId, poolParentId: poolId, rootId: rootId,
                boundUnderRoot: false, activeSelf: false, activeInHierarchy: false));

        // Under our root, drawing: ours.
        Assert.Equal(
            DriverOwnership.Ours,
            PoolParentPolicy.DecideOwnership(
                parentRead: true, parentId: rootId, poolParentId: poolId, rootId: rootId,
                boundUnderRoot: false, activeSelf: true, activeInHierarchy: true));

        // No pool parent was ever recorded: only the active/inactive fallback is left.
        Assert.Equal(
            DriverOwnership.Ours,
            PoolParentPolicy.DecideOwnership(
                parentRead: true, parentId: foreignId, poolParentId: PoolParentPolicy.NoInstanceId,
                rootId: rootId, boundUnderRoot: false, activeSelf: true, activeInHierarchy: true));
        Assert.Equal(
            DriverOwnership.Recycled,
            PoolParentPolicy.DecideOwnership(
                parentRead: true, parentId: foreignId, poolParentId: PoolParentPolicy.NoInstanceId,
                rootId: rootId, boundUnderRoot: false, activeSelf: false, activeInHierarchy: false));
    }

    // ---- EV12 addendum: GameRed / GameFeed borrow a real game line ---------------------------

    [Fact]
    public void GameRed_and_GameFeed_want_a_borrowed_line_other_styles_do_not()
    {
        Assert.True(BeamCatalog.WantsGameLine(BeamStyle.GameRed));
        Assert.True(BeamCatalog.WantsGameLine(BeamStyle.GameFeed));
        Assert.False(BeamCatalog.WantsGameLine(BeamStyle.Thin));
        // Cardinal's body is a trail sprite / particle graph (evidence 119), not a line.
        Assert.False(BeamCatalog.WantsGameLine(BeamStyle.GameBlue));
        Assert.Equal(
            "Beam_Dracula_BloodSoul_Channeling_01",
            BeamCatalog.GameLineBeamsFor(BeamStyle.GameRed)[0].PrefabName);
        Assert.Equal(
            "Beam_Feed_Channeling_01",
            BeamCatalog.GameLineBeamsFor(BeamStyle.GameFeed)[0].PrefabName);
        Assert.True(BeamCatalog.IsGameLinePrefabName(BeamStyle.GameRed, "Beam_Charm_Channeling_01"));
        Assert.False(BeamCatalog.IsGameLinePrefabName(BeamStyle.GameRed, "Beam_Feed_Channeling_01"));
    }

    [Fact]
    public void GameRed_beam_body_is_the_borrowed_prefab_when_it_is_loaded()
    {
        _client.LoadedLinePrefabs.Add("Beam_Dracula_BloodSoul_Channeling_01");
        var beam = Assets.Beam(BeamStyle.GameRed, _owner, "example.effects.test");
        beam.SetEndpoints(new EffectVec3(0f, 1f, 0f), new EffectVec3(4f, 1f, 0f));

        Assert.Equal(BeamStyle.GameRed, beam.Style);
        Assert.Equal("Beam_Dracula_BloodSoul_Channeling_01", beam.GameLinePrefab);
        Assert.Equal(SequenceLateDriver.VerdictBorrowedGameLine, beam.DrawPath);
        // The borrowed line is the body, so no owned halo doubles it.
        Assert.False(beam.HasGlowLine);
        beam.Dispose();
        // Our Instantiate, our Destroy.
        Assert.Equal(1, _client.GameLineDestroyedCount);
    }

    [Fact]
    public void GameRed_falls_back_to_the_owned_line_when_no_prefab_is_loaded()
    {
        var beam = Assets.Beam(BeamStyle.GameRed, _owner, "example.effects.test");
        beam.SetEndpoints(new EffectVec3(0f, 1f, 0f), new EffectVec3(4f, 1f, 0f));
        Assert.Equal("", beam.GameLinePrefab);
        Assert.NotEqual(SequenceLateDriver.VerdictBorrowedGameLine, beam.DrawPath);
        Assert.True(beam.HasOwnedLine);
        beam.Dispose();
        Assert.Equal(0, _client.GameLineDestroyedCount);
    }

    /// <summary>
    /// GameRed keeps its identity on a client with no castle sequence entity: the Cardinal
    /// sequence supplies the audio and the borrowed prefab supplies the pixels. Before EV12
    /// this degraded to <see cref="BeamStyle.Thin"/>.
    /// </summary>
    [Fact]
    public void GameRed_stays_GameRed_without_a_castle_sequence_when_a_line_prefab_exists()
    {
        _client.LoadedLinePrefabs.Add("Beam_Dracula_BloodSoul_Channeling_01");
        Assert.Equal(BeamStyle.GameRed, Assets.PreferredBeamStyle());
        var beam = Assets.Beam(BeamStyle.GameRed, _owner, "example.effects.test");
        beam.SetEndpoints(new EffectVec3(0f, 1f, 0f), new EffectVec3(4f, 1f, 0f));
        Assert.Equal(BeamStyle.GameRed, beam.Style);
        Assert.DoesNotContain(
            _log.Infos,
            l => l.Contains("not found at runtime; using Thin", StringComparison.Ordinal));
        beam.Dispose();
    }

    [Fact]
    public void Preferred_beam_style_is_thin_without_a_sequence_or_a_line_prefab()
    {
        Assert.Equal(BeamStyle.Thin, Assets.PreferredBeamStyle());
    }

    [Fact]
    public void GameFeed_borrows_the_blood_feed_channel_beam()
    {
        _client.LoadedLinePrefabs.Add("Beam_Feed_Channeling_01");
        var beam = Assets.Beam(BeamStyle.GameFeed, _owner, "example.effects.test");
        beam.SetEndpoints(new EffectVec3(0f, 1f, 0f), new EffectVec3(4f, 1f, 0f));
        Assert.Equal(BeamStyle.GameFeed, beam.Style);
        Assert.Equal("Beam_Feed_Channeling_01", beam.GameLinePrefab);
        Assert.Equal(SequenceLateDriver.VerdictBorrowedGameLine, beam.DrawPath);
        beam.Dispose();
    }

    /// <summary>A borrowed body still tracks both endpoints every late frame.</summary>
    [Fact]
    public void Borrowed_game_line_tracks_both_endpoints()
    {
        _client.LoadedLinePrefabs.Add("Beam_Dracula_BloodSoul_Channeling_01");
        _client.SetEntityPose(11, new EffectVec3(1f, 0f, 0f));
        _client.SetEntityPose(12, new EffectVec3(9f, 0f, 0f));
        var beam = Assets.Beam(BeamStyle.GameRed, _owner, "example.effects.test");
        beam.SetEndpoints(new EffectVec3(0f, 1f, 0f), new EffectVec3(4f, 1f, 0f));
        beam.Track(11, 12);
        SequenceLateDriver.TickForTests();
        Assert.Equal(new EffectVec3(1f, 0f, 0f), _client.LastAppliedStart);
        Assert.Equal(new EffectVec3(9f, 0f, 0f), _client.LastAppliedEnd);

        _client.SetEntityPose(11, new EffectVec3(2f, 0f, 0f));
        _client.SetEntityPose(12, new EffectVec3(8f, 0f, 0f));
        SequenceLateDriver.TickForTests();
        Assert.Equal(new EffectVec3(2f, 0f, 0f), _client.LastAppliedStart);
        Assert.Equal(new EffectVec3(8f, 0f, 0f), _client.LastAppliedEnd);
        beam.Dispose();
    }

    private int CountInfos(string exact)
    {
        int n = 0;
        for (int i = 0; i < _log.Infos.Count; i++)
        {
            if (_log.Infos[i] == exact) n++;
        }

        return n;
    }

    internal enum SequencePlayKind
    {
        None = 0,
        PoseIdentity = 1,
        TwoPointMarker = 2,
    }

    private sealed class Owner
    {
        public Owner(string id) => Id = id;
        public string Id { get; }
    }

    internal sealed class FakeEffectRuntime : IEffectRuntime
    {
        private readonly Dictionary<AssetKind, List<string>> _catalogue = new();
        private readonly List<FakeObject> _objects = new();
        private readonly List<FakeHost> _hosts = new();
        private readonly Dictionary<string, int> _layersByRoot = new(StringComparer.OrdinalIgnoreCase);
        private int _nextEntity = 1;

        public bool IsClient { get; set; } = true;
        public int BorrowCalls { get; private set; }
        public int HostCalls { get; private set; }
        public int FindCalls { get; private set; }
        public int PlaySequenceCalls { get; private set; }
        public SequencePlayKind LastPlayKind { get; set; }
        public EffectVec3 LastPlayPosition { get; set; }
        public EffectVec3 LastPosePosition { get; set; }
        public EffectVec3 LastAppliedStart { get; set; }
        public EffectVec3 LastAppliedEnd { get; set; }
        public int EndpointApplyCount { get; set; }
        public bool OverwriteEndpoints { get; set; }
        /// <summary>When true, the fake pretends SequenceInstance was missing so DriveEndpoints uses the hybrid fallback.</summary>
        public bool MissingSequenceInstance { get; set; }

        /// <summary>When true, late-slot Positions already read [start,end] (sequencer honored the marker).</summary>
        public bool SequencerHonoursMarkers { get; set; }
        public string LastBeamPath { get; set; } = "";
        public bool LastStopped { get; set; }
        public bool LastPlayed { get; set; }
        public float LastScale { get; set; } = 1f;
        public List<string> DestroyedPrefixes { get; } = new();
        public List<string> DestroyedRoots { get; } = new();
        public List<int> DestroyedSequenceIds { get; } = new();
        public List<int> DestroyedMarkerIds { get; } = new();
        public HashSet<string> SequencesWithoutBuffer { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<int> OccupiedLayers { get; } = new();
        public int DriverDelayFrames { get; set; }
        /// <summary>When true, DriveEndpoints logs one LocalToWorld failure and then skips that write.</summary>
        public bool FailTransformWrites { get; set; }
        /// <summary>When true, the clone lands inactive (evidence 99) and bind must SetActive once.</summary>
        public bool DriverStartsInactive { get; set; } = true;
        public EffectVec2 ScreenSize { get; set; } = new(1920f, 1080f);
        public int MaxDriverPollFrames { get; set; } = SequenceEffectHandle.DefaultDriverPollFrames;
        public string? LastMissReason { get; private set; }

        public string? ResolveBeamPreset(BeamStyle style, out string source)
        {
            source = "";
            if (style == BeamStyle.Thin || style == BeamStyle.GameBlue)
            {
                source = "Cardinal AB_Cardinal_SummonOrb_Beam_Buff dump/wiki guid -2014639169";
                if (_catalogue.TryGetValue(AssetKind.Sequence, out var list))
                {
                    for (int i = 0; i < list.Count; i++)
                    {
                        if (BeamCatalog.IsCardinalName(list[i]))
                            return list[i];
                    }

                    if (list.Count > 0) return list[0];
                }

                return BeamCatalog.CardinalBuffHashName;
            }

            if (BeamPresets.TryGetValue(style, out var name) && !string.IsNullOrEmpty(name))
            {
                source = "test";
                return name;
            }

            return null;
        }

        /// <summary>
        /// Prefab names this fake client has "loaded". Add a
        /// <see cref="BeamCatalog.GameLineBeamsFor"/> name to make GameRed / GameFeed
        /// resolve a borrowed body.
        /// </summary>
        public HashSet<string> LoadedLinePrefabs { get; } = new(StringComparer.Ordinal);

        public string? ResolveBeamLinePrefab(BeamStyle style, out string source)
        {
            source = "";
            var wanted = BeamCatalog.GameLineBeamsFor(style);
            for (int i = 0; i < wanted.Length; i++)
            {
                if (!LoadedLinePrefabs.Contains(wanted[i].PrefabName)) continue;
                source = "test loaded prefab '" + wanted[i].PrefabName + "' " + wanted[i].Shader;
                return wanted[i].PrefabName;
            }

            return null;
        }
        public int LastRtWidth { get; private set; }
        public int LastRtHeight { get; private set; }
        public int TintApplyCount { get; set; }
        public bool OverlayReturnedBorrowToRoot { get; set; }
        /// <summary>Width copied from the fake clone once it lands (opt-in <c>UsePrefabLineMaterial</c>).</summary>
        public float DriverLineWidth { get; set; } = 0.25f;
        public float LastOwnedLineWidth { get; set; } = SequenceLateDriver.OwnedLineDefaultWidth;
        public float LastOwnedLineStartWidth { get; set; }
        public float LastOwnedLineEndWidth { get; set; }
        public float LastOwnedLineWidthMultiplier { get; set; } = 1f;
        public bool LastOwnedLineWidthCurveConstant { get; set; }
        public float LastOwnedLineLossyScale { get; set; } = 1f;
        public bool LastOwnedLineShaderSupported { get; set; } = true;
        public int LastOwnedLineRenderQueue { get; set; } = 3000;
        public string LastOwnedLineMaterial { get; set; } = "";
        public int OwnedLineStyleCopyCount { get; set; }
        public int OwnedLineDestroyedCount { get; set; }
        public int GlowLineDestroyedCount { get; set; }

        /// <summary>Borrowed prefab copies destroyed on release (our Instantiate, our Destroy).</summary>
        public int GameLineDestroyedCount { get; set; }
        /// <summary>When true, the fake clone LineRenderer is a trail sprite (Cardinal); Game presets skip the copy.</summary>
        public bool DriverIsTrailSprite { get; set; } = true;
        /// <summary>When true, copied material already glows; no halo line.</summary>
        public bool DriverMaterialHasGlow { get; set; }
        public EffectColor DriverMaterialColor { get; set; } = new(1.8f, 0.14f, 0.06f, 1f);
        public bool WriteLineRendererBuffer { get; set; }
        public Dictionary<BeamStyle, string> BeamPresets { get; } = new();
        /// <summary>Must stay 0 for sequences: the pooled clone is never written.</summary>
        public int CloneMaterialWriteCount { get; set; }
        public Dictionary<int, EffectVec3> EntityPoses { get; } = new();
        public HashSet<int> DeadEntities { get; } = new();

        public void SetEntityPose(int id, EffectVec3 pos)
        {
            DeadEntities.Remove(id);
            EntityPoses[id] = pos;
        }

        public void KillEntity(int id) => DeadEntities.Add(id);

        public bool TryGetEntityPose(int id, out EffectVec3 pos)
        {
            pos = default;
            if (DeadEntities.Contains(id)) return false;
            return EntityPoses.TryGetValue(id, out pos);
        }
        public List<string> DriverPool { get; } = new();
        public HashSet<string> BoundDrivers { get; } = new(StringComparer.Ordinal);
        private readonly Dictionary<string, FakeObject> BoundOwners = new(StringComparer.Ordinal);

        private string BindDriverName(FakeObject owner)
        {
            string? unbound = null;
            if (DriverPool.Count > 0)
            {
                for (int i = 0; i < DriverPool.Count; i++)
                {
                    string n = DriverPool[i];
                    if (BoundDrivers.Contains(n)) continue;
                    unbound = n;
                    break;
                }
            }

            if (unbound == null)
            {
                unbound = DriverPool.Count > 0
                    ? DriverPool[0]
                    : "Beam_Cardinal_Orb_Guardian_01(Clone)";
                if (BoundOwners.TryGetValue(unbound, out var prev) && !ReferenceEquals(prev, owner))
                {
                    prev.DropDriverForReuse();
                    Plugin.Logger?.LogInfo("Effects: sequence driver reused from pool");
                }
            }

            BoundDrivers.Add(unbound);
            BoundOwners[unbound] = owner;
            return unbound;
        }

        public void RecycleDrivers()
        {
            for (int i = 0; i < _objects.Count; i++)
                _objects[i].MarkRecycled();
        }

        public int ObjectCount
        {
            get
            {
                int n = 0;
                for (int i = 0; i < _objects.Count; i++)
                    if (_objects[i].Alive) n++;
                return n;
            }
        }

        public int HostCount
        {
            get
            {
                int n = 0;
                for (int i = 0; i < _hosts.Count; i++)
                    if (_hosts[i].Alive) n++;
                return n;
            }
        }

        public void Add(AssetKind kind, string name)
        {
            if (!_catalogue.TryGetValue(kind, out var list))
            {
                list = new List<string>();
                _catalogue[kind] = list;
            }

            list.Add(name);
        }

        public bool HasObject(string name)
        {
            for (int i = 0; i < _objects.Count; i++)
            {
                if (_objects[i].Alive && _objects[i].Name == name) return true;
            }

            return false;
        }

        public IReadOnlyList<string> Find(AssetKind kind, string? nameContains)
        {
            FindCalls++;
            if (!IsClient) return Array.Empty<string>();
            if (!_catalogue.TryGetValue(kind, out var list)) return Array.Empty<string>();
            if (string.IsNullOrEmpty(nameContains)) return list.ToArray();
            var filtered = new List<string>();
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].IndexOf(nameContains, StringComparison.OrdinalIgnoreCase) >= 0)
                    filtered.Add(list[i]);
            }

            return filtered;
        }

        public IRuntimeObject? Borrow(AssetKind kind, string name, string rootName)
        {
            BorrowCalls++;
            LastMissReason = null;
            if (!IsClient) return null;
            if (!_catalogue.TryGetValue(kind, out var list)) return null;
            bool hit = false;
            for (int i = 0; i < list.Count; i++)
            {
                if (string.Equals(list[i], name, StringComparison.OrdinalIgnoreCase))
                {
                    hit = true;
                    break;
                }
            }

            if (!hit) return null;
            if (kind == AssetKind.Sequence && SequencesWithoutBuffer.Contains(name))
            {
                LastMissReason = "Effects: Sequence '" + name + "' has no BuffSequence buffer; returning a disabled handle.";
                return null;
            }

            var obj = new FakeObject(this, kind, name, rootName);
            _objects.Add(obj);
            return obj;
        }

        public IRuntimeHost? Host(IRuntimeObject borrowed, HostKind kind, string rootName)
        {
            HostCalls++;
            if (!IsClient) return null;
            var fake = borrowed as FakeObject;
            if (fake != null && fake.Kind == AssetKind.Sequence && !fake.HasDriver)
                Plugin.Logger?.LogInfo(
                    "Effects: RT host has no sequence driver GO yet; camera looks along the beam (clone is not parented under our root).");
            int layer = kind == HostKind.RenderTexture ? ReserveLayer(rootName) : -1;
            var host = new FakeHost(this, kind, fake, layer);
            _hosts.Add(host);
            LastRtWidth = host.RtWidth;
            LastRtHeight = host.RtHeight;
            return host;
        }

        public int ReserveLayer(string rootName)
        {
            if (!string.IsNullOrEmpty(rootName) && _layersByRoot.TryGetValue(rootName, out int cached))
                return cached;
            for (int i = 31; i >= 8; i--)
            {
                if (OccupiedLayers.Contains(i)) continue;
                OccupiedLayers.Add(i);
                if (!string.IsNullOrEmpty(rootName))
                    _layersByRoot[rootName] = i;
                Plugin.Logger?.LogInfo("Effects: RT host reserved layer " + i + " (unnamed, no renderer) for " + rootName + ".");
                return i;
            }

            return -1;
        }

        public int ObjectLayer(string name)
        {
            for (int i = 0; i < _objects.Count; i++)
            {
                if (_objects[i].Alive && _objects[i].Name == name)
                    return _objects[i].Layer;
            }

            return -1;
        }

        public float ObjectScale(string name)
        {
            for (int i = 0; i < _objects.Count; i++)
            {
                if (_objects[i].Alive && _objects[i].Name == name)
                    return _objects[i].UniformScale;
            }

            return 0f;
        }

        public int ObjectPlayCount(string name)
        {
            for (int i = 0; i < _objects.Count; i++)
            {
                if (_objects[i].Name == name)
                    return _objects[i].PlayCount;
            }

            return 0;
        }

        public int DestroyRoot(string rootName)
        {
            DestroyedRoots.Add(rootName);
            int n = 0;
            for (int i = 0; i < _objects.Count; i++)
            {
                if (!_objects[i].Alive) continue;
                if (!_objects[i].RootName.Equals(rootName, StringComparison.OrdinalIgnoreCase))
                    continue;
                _objects[i].Release();
                n++;
            }

            return n;
        }

        public int DestroyByPrefix(string prefix)
        {
            DestroyedPrefixes.Add(prefix);
            int n = 0;
            for (int i = 0; i < _objects.Count; i++)
            {
                if (!_objects[i].Alive) continue;
                if (!_objects[i].RootName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                    && !_objects[i].Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    continue;
                _objects[i].Release();
                n++;
            }

            for (int i = 0; i < _hosts.Count; i++)
            {
                if (_hosts[i].Alive) _hosts[i].Release();
            }

            return n;
        }

        public void SyncHosts()
        {
            for (int i = 0; i < _hosts.Count; i++)
            {
                if (!_hosts[i].Alive) continue;
                _hosts[i].SyncScreenSize();
                LastRtWidth = _hosts[i].RtWidth;
                LastRtHeight = _hosts[i].RtHeight;
            }
        }

        internal int NextEntity() => _nextEntity++;

        private sealed class FakeObject : IRuntimeObject
        {
            private readonly FakeEffectRuntime _runtime;
            private bool _released;
            private bool _played;
            private int _age;
            private EffectColor _color;
            private bool _hasColor;
            private FakeHost? _pendingHost;

            public FakeObject(FakeEffectRuntime runtime, AssetKind kind, string name, string rootName)
            {
                _runtime = runtime;
                Kind = kind;
                Name = name;
                RootName = rootName;
            }

            public AssetKind Kind { get; }
            public string Name { get; }
            public string RootName { get; }
            public bool Alive => !_released;
            public EffectColor Color
            {
                get => _color;
                set
                {
                    _color = value;
                    _hasColor = true;
                    if (Kind == AssetKind.Sequence)
                    {
                        // This is where a tint would reach the pooled clone. It goes through
                        // the policy like every other write, so a rule that ever allowed a
                        // sequence clone to be written shows up as a clone material write.
                        if (HasDriver)
                            ApplyTintOnce();
                        if (!TintAppliedToDriver)
                            TintAppliedToDriver = HasOwnedLine || HasGameLine;
                        ApplyOwnedTint();
                        return;
                    }

                    // A non-sequence borrow is our own clone and exists immediately.
                    if (HasDriver || Kind != AssetKind.Sequence)
                        ApplyTintOnce();
                }
            }

            public EffectVec2 Size { get; set; }
            public EffectVec2 ScreenPosition { get; set; }
            public EffectVec3 WorldPosition
            {
                get => Kind == AssetKind.Sequence ? BeamStart : _world;
                set
                {
                    if (Kind == AssetKind.Sequence) BeamStart = value;
                    else _world = value;
                }
            }

            public float UniformScale
            {
                get => _scale;
                set
                {
                    if (_released) return;
                    _scale = value;
                    _runtime.LastScale = value;
                    if (_loggedScale) return;
                    _loggedScale = true;
                    Plugin.Logger?.LogInfo(
                        "Effects: particle '" + Name + "' scale " + value.ToString("0.##")
                        + " via transform (fake)");
                }
            }

            public int PlayCount { get; private set; }

            public EffectVec3 BeamStart { get; set; }
            public EffectVec3 BeamEnd { get; set; }
            public float Intensity { get; set; }
            public float Smoothness { get; set; }
            public int Layer { get; set; }
            public int SortingOrder { get; set; }
            public bool HasDriver { get; private set; }
            public bool HasOwnedLine { get; private set; }
            public float OwnedLineWidth
            {
                get => LineWidth;
                private set => LineWidth = value;
            }

            public float LineWidth
            {
                get => _lineWidth;
                set
                {
                    _lineWidth = value;
                    _lineWidthSet = true;
                    WriteOwnedLineWidth();
                }
            }

            public bool UsePrefabLineMaterial
            {
                get => _usePrefabMaterial;
                set
                {
                    _usePrefabMaterial = value;
                    if (value && HasDriver)
                        CopyOwnedLineStyleOnce();
                }
            }

            public string OwnedLineMaterialName { get; private set; } = "";
            public BeamStyle BeamStyle { get; private set; }
            public bool HasGlowLine { get; private set; }
            public float GlowWidth
            {
                get => _glowWidth;
                set
                {
                    _glowWidth = value;
                    if (value <= 0f || _runtime.DriverMaterialHasGlow)
                    {
                        if (HasGlowLine)
                        {
                            HasGlowLine = false;
                            _runtime.GlowLineDestroyedCount++;
                        }

                        return;
                    }

                    if (HasOwnedLine)
                        EnsureGlowLine();
                }
            }

            public float GlowAlpha
            {
                get => _glowAlpha;
                set => _glowAlpha = value;
            }

            public string DrawPath { get; private set; } = "";

            public string GameLinePrefabName { get; private set; } = "";

            internal bool HasGameLine => !string.IsNullOrEmpty(GameLinePrefabName);

            /// <summary><c>LineRenderer.enabled</c> on the owned core line.</summary>
            internal bool OwnedLineEnabled { get; private set; }

            /// <summary><c>LineRenderer.enabled</c> on the halo line.</summary>
            internal bool GlowLineEnabled { get; private set; }

            /// <summary><c>LineRenderer.enabled</c> on the borrowed prefab copy.</summary>
            internal bool GameLineEnabled { get; private set; }

            public bool Recycled => _recycle.Recycled;

            public bool LineVisible
            {
                get
                {
                    // R27 finding 11: a recycle no longer hides the owned line.
                    if (_released) return false;
                    return (HasOwnedLine && OwnedLineEnabled)
                        || (HasGameLine && GameLineEnabled && !_recycle.GameLineHidden);
                }
            }

            public EffectColor CopiedMaterialColor { get; private set; }
            public EffectColor LastTintedColor { get; private set; }
            public int LineRendererBufferWrites { get; private set; }

            public void ApplyBeamPreset(BeamStyle style)
            {
                BeamStyle = style;
                if (style == BeamStyle.Thin)
                {
                    _copyGameLine = false;
                    _usePrefabMaterial = false;
                    if (!_hasColor)
                    {
                        _color = SequenceLateDriver.OwnedLineDefaultTint;
                        _hasColor = true;
                    }

                    LastTintedColor = _color;
                    DrawPath = SequenceLateDriver.VerdictOwnedLine;
                    return;
                }

                _copyGameLine = true;
                if (!_hasColor)
                {
                    _color = BeamCatalog.DefaultTint(style);
                    _hasColor = true;
                }

                // EV12: a copy of a vanilla LineRenderer-bodied beam prefab is the body when
                // this client has one loaded (evidence 130). The copy is ours, so writing it
                // is legal; the pooled sequencer clone still is not.
                EnsureGameLine(style);
                if (HasGameLine)
                {
                    ApplyOwnedTint();
                    return;
                }

                if (!_runtime.DriverMaterialHasGlow && _glowWidth <= 0f)
                {
                    _glowWidth = BeamCatalog.DefaultGlowWidth;
                    _glowAlpha = BeamCatalog.DefaultGlowAlpha;
                }

                if (HasOwnedLine)
                {
                    CopyOwnedLineStyleOnce();
                    EnsureGlowLine();
                    ApplyOwnedTint();
                }
            }

            private void EnsureGameLine(BeamStyle style)
            {
                if (HasGameLine || !BeamCatalog.WantsGameLine(style)) return;
                string? found;
                try { found = _runtime.ResolveBeamLinePrefab(style, out _); }
                catch { found = null; }
                if (string.IsNullOrEmpty(found)) return;
                if (!BeamCatalog.IsGameLinePrefabName(style, found)) return;

                GameLinePrefabName = found!;
                GameLineEnabled = true;
                // The borrowed line is the body; the owned core and its halo would double-draw.
                OwnedLineEnabled = false;
                if (HasGlowLine)
                {
                    HasGlowLine = false;
                    _runtime.GlowLineDestroyedCount++;
                }

                DrawPath = SequenceLateDriver.VerdictBorrowedGameLine;
                LateWriteVerdict = DrawPath;
                if (_loggedGameLine) return;
                _loggedGameLine = true;
                Plugin.Logger?.LogInfo("Effects: sequence beam body = borrowed prefab '" + found + "'");
            }

            public string DriverName { get; private set; } = "";
            public int SequenceEntityId { get; private set; }
            public int EndMarkerId { get; private set; }
            public bool TintAppliedToDriver { get; private set; }
            public bool DriverActive { get; private set; }
            public string LastTransformWritePath { get; private set; } = "";
            public int TransformWriteWarnings => _writes.WarningCount;
            public bool HybridLastWrite { get; private set; }
            public int LateWriteCount { get; private set; }
            public int LastLateWriteFrame { get; private set; }
            public int SequencerOverwriteCount { get; private set; }
            public string LateWriteVerdict { get; private set; } = "";

            private bool _loggedLateInspect;
            private bool _loggedVerdict;
            private bool _loggedSummary;
            private DriverRecycleState _recycle;
            private bool _loggedGameLine;
            private bool _ownedLineLogged;
            private bool _ownedLineStyleCopied;
            private bool _ownedLineStyleLogged;
            private bool _ownedLineReadbackLogged;
            private bool _recycled;
            private bool _loggedScale;
            private readonly TransformWriteState _writes = new();
            private EffectVec3 _world;
            private float _scale = 1f;
            private float _lineWidth = SequenceLateDriver.OwnedLineDefaultWidth;
            private bool _lineWidthSet;
            private bool _usePrefabMaterial;
            private bool _copyGameLine;
            private bool _loggedTrailSkip;
            private float _glowWidth;
            private float _glowAlpha = BeamCatalog.DefaultGlowAlpha;
            private bool _trackStart;
            private bool _trackEnd;
            private bool _trackEndFixed;
            private bool _startFrozen;
            private bool _endFrozen;
            private bool _loggedStartLost;
            private bool _loggedEndLost;
            private int _trackStartId;
            private int _trackEndId;
            private EffectVec3 _trackStartOffset;
            private EffectVec3 _trackEndOffset;
            private EffectVec3 _trackEndWorld;

            public void EnsurePlayed()
            {
                if (Kind != AssetKind.Sequence || _played || _released) return;
                _played = true;
                _runtime.PlaySequenceCalls++;
                _runtime.LastPlayKind = SequencePlayKind.PoseIdentity;
                _runtime.LastPlayPosition = BeamStart;
                SequenceEntityId = _runtime.NextEntity();
                if (Assets.BindSequencerMarkers && !_runtime.MissingSequenceInstance)
                {
                    EndMarkerId = _runtime.NextEntity();
                    _runtime.LastBeamPath = "SequenceInstance.Target/SecondaryTarget";
                    Plugin.Logger?.LogInfo(
                        "Effects: sequence beam SequenceInstance.Target/SecondaryTarget start=" + BeamStart
                        + " end=" + BeamEnd
                        + " seq=" + SequenceEntityId + ":1"
                        + " marker=" + EndMarkerId + ":1");
                }
                else if (Assets.BindSequencerMarkers)
                {
                    EndMarkerId = 0;
                    _runtime.LastBeamPath = "hybrid SetPosition fallback";
                    Plugin.Logger?.LogInfo(
                        "Effects: sequence beam hybrid SetPosition fallback (no SequenceInstance on seq="
                        + SequenceEntityId + ":1)");
                }
                else
                {
                    EndMarkerId = 0;
                    _runtime.LastBeamPath = SequenceLateDriver.VerdictOwnedLine;
                }

                LastTransformWritePath = TransformWritePath.GenericSetComponentData;
                HasOwnedLine = true;
                OwnedLineEnabled = true;
                if (_hasColor)
                    TintAppliedToDriver = true;
                if (!_lineWidthSet)
                    _lineWidth = SequenceLateDriver.OwnedLineDefaultWidth;
                WriteOwnedLineWidth();
                if ((_usePrefabMaterial || _copyGameLine) && HasDriver)
                    CopyOwnedLineStyleOnce();
                else if (!_usePrefabMaterial)
                {
                    OwnedLineMaterialName = "HDRP/Unlit";
                    _runtime.LastOwnedLineMaterial = OwnedLineMaterialName;
                    LogOwnedLineStyleOnce();
                    LogOwnedLineReadbackOnce();
                }

                ApplyOwnedTint();
                EnsureGlowLine();
                LogOwnedLineOnce();
                SequenceLateDriver.Watch(this);
                // Markers exist from this play; apply them even if the driver GO is a frame late.
                DriveEndpoints();
                TryBindDriver();
            }

            public bool TryBindDriver()
            {
                if (_released) return false;
                if (Kind != AssetKind.Sequence) return Alive;
                if (HasDriver)
                {
                    DriveEndpoints();
                    return true;
                }

                if (!_played) return false;
                _age++;
                if (_age <= _runtime.DriverDelayFrames) return false;
                HasDriver = true;
                DriverName = _runtime.BindDriverName(this);
                KeepDriverActive();
                if (_usePrefabMaterial || _copyGameLine)
                    CopyOwnedLineStyleOnce();
                if (_hasColor && Kind != AssetKind.Sequence)
                    ApplyTintOnce();
                DriveEndpoints();
                _pendingHost?.AttachDriver(this);
                return true;
            }

            /// <summary>
            /// The one place the fake models "write the GameObject the runtime handed us".
            /// It asks the production policy first, so a regression that lets a sequence
            /// clone be tinted shows up as a non-zero
            /// <see cref="FakeEffectRuntime.CloneMaterialWriteCount"/>.
            /// </summary>
            private void ApplyTintOnce()
            {
                if (TintAppliedToDriver) return;
                if (!CloneWritePolicy.AllowsMaterialWrites(Kind, weInstantiatedIt: OwnsGameObject))
                {
                    Plugin.Logger?.LogInfo(CloneWritePolicy.FormatSkippedTintLog(DriverName));
                    return;
                }

                TintAppliedToDriver = true;
                _runtime.TintApplyCount++;
                _runtime.CloneMaterialWriteCount++;
            }

            /// <summary>Sequence handles borrow the sequencer's pooled clone; every other kind clones its own.</summary>
            private bool OwnsGameObject => Kind != AssetKind.Sequence;

            public void DriveEndpoints()
            {
                if (_released || Kind != AssetKind.Sequence) return;
                LastTransformWritePath = TransformWritePath.GenericSetComponentData;
                if (_runtime.FailTransformWrites)
                    RecordTransformWriteFailure();
                else
                    _writes.MarkSucceeded();

                _runtime.LastAppliedStart = BeamStart;
                _runtime.LastAppliedEnd = BeamEnd;
                _runtime.LastPosePosition = BeamStart;
                _runtime.EndpointApplyCount++;
            }

            public void DriveLateWrite()
            {
                if (_released || Kind != AssetKind.Sequence) return;
                ResolveTrackedEndpoints();
                LogOwnedLineOnce();
                LogOwnedLineStyleOnce();
                LogOwnedLineReadbackOnce();
                WriteOwnedLine();
                if (_runtime.WriteLineRendererBuffer && HasOwnedLine)
                {
                    LineRendererBufferWrites++;
                    if (_copyGameLine && !_runtime.DriverIsTrailSprite)
                    {
                        DrawPath = SequenceLateDriver.VerdictGameLineBuffer;
                        LateWriteVerdict = DrawPath;
                    }
                }

                if (_recycled)
                {
                    DropRecycledDriver();
                    return;
                }

                if (!HasDriver) return;
                bool matched = _runtime.SequencerHonoursMarkers;
                if (!matched)
                    SequencerOverwriteCount++;
                LateWriteCount++;
                LastLateWriteFrame = LateWriteCount;
                HybridLastWrite = true;
                _runtime.LastAppliedStart = BeamStart;
                _runtime.LastAppliedEnd = BeamEnd;
                if (!_loggedLateInspect)
                {
                    _loggedLateInspect = true;
                    string before = matched
                        ? "[" + BeamStart + "," + BeamEnd + "]"
                        : "[(0,0,0),driver]";
                    string after = "[" + BeamStart + "," + BeamEnd + "]";
                    Plugin.Logger?.LogInfo(
                        SequenceLateDriver.FormatHybridPositionsLog(before, after, stuck: true, matched));
                    Plugin.Logger?.LogInfo(
                        SequenceLateDriver.FormatFirstWriteLog(before, BeamStart, BeamEnd, LastLateWriteFrame));
                }

                if (!_loggedVerdict && !HasOwnedLine && LateWriteCount >= SequenceLateDriver.VerdictAfterFrames)
                    LogLateVerdict();
            }

            private void WriteOwnedLine()
            {
                if (!HasOwnedLine) return;
                _runtime.LastAppliedStart = BeamStart;
                _runtime.LastAppliedEnd = BeamEnd;
                WriteOwnedLineWidth();
            }

            private void WriteOwnedLineWidth()
            {
                _runtime.LastOwnedLineWidth = _lineWidth;
                _runtime.LastOwnedLineStartWidth = _lineWidth;
                _runtime.LastOwnedLineEndWidth = _lineWidth;
                _runtime.LastOwnedLineWidthMultiplier = 1f;
                _runtime.LastOwnedLineWidthCurveConstant = true;
                _runtime.LastOwnedLineLossyScale = 1f;
                _runtime.LastOwnedLineShaderSupported = true;
                _runtime.LastOwnedLineRenderQueue = 3000;
            }

            public void TrackStartEntity(int index, int version, EffectVec3 offset)
            {
                _trackStart = index != 0;
                _trackStartId = index;
                _trackStartOffset = offset;
                _startFrozen = false;
                _loggedStartLost = false;
                if (_trackStart)
                    SeedStart();
            }

            public void TrackEndEntity(int index, int version, EffectVec3 offset)
            {
                _trackEnd = index != 0;
                _trackEndFixed = false;
                _trackEndId = index;
                _trackEndOffset = offset;
                _endFrozen = false;
                _loggedEndLost = false;
                if (_trackEnd)
                    SeedEnd();
            }

            public void TrackEndWorld(EffectVec3 world)
            {
                _trackEnd = false;
                _trackEndFixed = true;
                _trackEndWorld = world;
                BeamEnd = world;
            }

            public void ClearEndpointTracking()
            {
                _trackStart = false;
                _trackEnd = false;
                _trackEndFixed = false;
                _startFrozen = false;
                _endFrozen = false;
            }

            private void SeedStart()
            {
                if (_runtime.TryGetEntityPose(_trackStartId, out var pos))
                    BeamStart = AddOffset(pos, _trackStartOffset);
            }

            private void SeedEnd()
            {
                if (_runtime.TryGetEntityPose(_trackEndId, out var pos))
                    BeamEnd = AddOffset(pos, _trackEndOffset);
            }

            private void ResolveTrackedEndpoints()
            {
                if (_trackStart && !_startFrozen)
                {
                    if (_runtime.TryGetEntityPose(_trackStartId, out var pos))
                        BeamStart = AddOffset(pos, _trackStartOffset);
                    else
                    {
                        _startFrozen = true;
                        if (!_loggedStartLost)
                        {
                            _loggedStartLost = true;
                            Plugin.Logger?.LogInfo("Effects: sequence endpoint start lost; frozen");
                        }
                    }
                }

                if (_trackEndFixed)
                {
                    BeamEnd = _trackEndWorld;
                    return;
                }

                if (_trackEnd && !_endFrozen)
                {
                    if (_runtime.TryGetEntityPose(_trackEndId, out var pos))
                        BeamEnd = AddOffset(pos, _trackEndOffset);
                    else
                    {
                        _endFrozen = true;
                        if (!_loggedEndLost)
                        {
                            _loggedEndLost = true;
                            Plugin.Logger?.LogInfo("Effects: sequence endpoint end lost; frozen");
                        }
                    }
                }
            }

            private static EffectVec3 AddOffset(in EffectVec3 pos, in EffectVec3 offset) =>
                new(pos.X + offset.X, pos.Y + offset.Y, pos.Z + offset.Z);

            private void CopyOwnedLineStyleOnce()
            {
                if (_ownedLineStyleCopied || !HasOwnedLine) return;
                // A borrowed prefab copy is already the body; nothing to copy onto the core.
                if (HasGameLine) return;
                if (!_usePrefabMaterial && !_copyGameLine) return;
                if (_copyGameLine && !_usePrefabMaterial && _runtime.DriverIsTrailSprite)
                {
                    if (!_loggedTrailSkip)
                    {
                        _loggedTrailSkip = true;
                        Plugin.Logger?.LogInfo(SequenceLateDriver.FormatTrailSpriteSkipLog("S_VFX_GenericTrails_SPRITE"));
                    }

                    return;
                }

                _ownedLineStyleCopied = true;
                if (!_lineWidthSet)
                    _lineWidth = _runtime.DriverLineWidth;

                OwnedLineMaterialName = "prefab";
                _runtime.LastOwnedLineMaterial = OwnedLineMaterialName;
                _runtime.OwnedLineStyleCopyCount++;
                CopiedMaterialColor = _runtime.DriverMaterialColor;
                DrawPath = SequenceLateDriver.VerdictOwnedGameMaterial;
                LateWriteVerdict = DrawPath;
                if (_runtime.DriverMaterialHasGlow)
                    Plugin.Logger?.LogInfo(SequenceLateDriver.GlowFromMaterialLog);
                WriteOwnedLineWidth();
                ApplyOwnedTint();
                LogOwnedLineStyleOnce();
                LogOwnedLineReadbackOnce();
                Plugin.Logger?.LogInfo(SequenceLateDriver.FormatDrawPathLog(DrawPath));
            }

            private void ApplyOwnedTint()
            {
                if (!_hasColor)
                {
                    LastTintedColor = CopiedMaterialColor.R > 0f || CopiedMaterialColor.G > 0f || CopiedMaterialColor.B > 0f
                        ? CopiedMaterialColor
                        : SequenceLateDriver.OwnedLineDefaultTint;
                    return;
                }

                if (CopiedMaterialColor.R > 0f || CopiedMaterialColor.G > 0f || CopiedMaterialColor.B > 0f
                    || CopiedMaterialColor.A > 0f)
                    LastTintedColor = EffectColor.TintPreserveIntensity(CopiedMaterialColor, _color);
                else
                    LastTintedColor = _color;
            }

            private void EnsureGlowLine()
            {
                if (!HasOwnedLine) return;
                if (_glowWidth <= 0f || _runtime.DriverMaterialHasGlow)
                {
                    if (HasGlowLine)
                    {
                        HasGlowLine = false;
                        _runtime.GlowLineDestroyedCount++;
                    }

                    return;
                }

                if (HasGameLine) return;
                if (HasGlowLine) return;
                HasGlowLine = true;
                GlowLineEnabled = true;
                Plugin.Logger?.LogInfo(SequenceLateDriver.FormatGlowLineLog(_glowWidth, _glowAlpha));
            }

            private void LogOwnedLineOnce()
            {
                if (_ownedLineLogged || !HasOwnedLine) return;
                _ownedLineLogged = true;
                _loggedVerdict = true;
                if (string.IsNullOrEmpty(DrawPath))
                    DrawPath = SequenceLateDriver.VerdictOwnedLine;
                LateWriteVerdict = DrawPath;
                if (DrawPath == SequenceLateDriver.VerdictOwnedLine)
                {
                    Plugin.Logger?.LogInfo(
                        "Effects: sequence beam drawn by owned LineRenderer ("
                        + SequenceLateDriver.OwnedLineNotDrawable + ")");
                }
            }

            private void LogOwnedLineStyleOnce()
            {
                if (_ownedLineStyleLogged || !HasOwnedLine) return;
                if (string.IsNullOrEmpty(OwnedLineMaterialName)) return;
                _ownedLineStyleLogged = true;
                Plugin.Logger?.LogInfo(
                    SequenceLateDriver.FormatOwnedLineStyleLog(OwnedLineMaterialName, _lineWidth));
            }

            private void LogOwnedLineReadbackOnce()
            {
                if (_ownedLineReadbackLogged || !HasOwnedLine) return;
                if (string.IsNullOrEmpty(OwnedLineMaterialName)) return;
                _ownedLineReadbackLogged = true;
                Plugin.Logger?.LogInfo(
                    SequenceLateDriver.FormatOwnedLineReadbackLog(
                        _runtime.LastOwnedLineStartWidth,
                        _runtime.LastOwnedLineWidthMultiplier,
                        _runtime.LastOwnedLineLossyScale,
                        _runtime.LastOwnedLineLossyScale,
                        _runtime.LastOwnedLineLossyScale,
                        OwnedLineMaterialName,
                        _runtime.LastOwnedLineShaderSupported,
                        _runtime.LastOwnedLineRenderQueue));
            }

            private void LogLateVerdict()
            {
                if (_loggedVerdict) return;
                _loggedVerdict = true;
                if (string.IsNullOrEmpty(LateWriteVerdict))
                {
                    LateWriteVerdict = SequencerOverwriteCount > 0
                        ? SequenceLateDriver.VerdictLateFrame
                        : SequenceLateDriver.VerdictSequencer;
                }

                Plugin.Logger?.LogInfo("Effects: sequence beam " + LateWriteVerdict);
            }

            private void LogLateSummary()
            {
                if (_loggedSummary) return;
                if (!_played && LateWriteCount <= 0 && !HasOwnedLine) return;
                _loggedSummary = true;
                if (!HasOwnedLine)
                    LogLateVerdict();
                Plugin.Logger?.LogInfo(
                    "Effects: sequence late writes=" + LateWriteCount
                    + ", sequencer overwrote before us " + SequencerOverwriteCount + " frames"
                    + " lastFrame=" + LastLateWriteFrame);
            }

            internal void DropDriverForReuse()
            {
                if (!string.IsNullOrEmpty(DriverName))
                {
                    _runtime.BoundDrivers.Remove(DriverName);
                    _runtime.BoundOwners.Remove(DriverName);
                }

                HasDriver = false;
                DriverName = "";
                DriverActive = false;
            }

            internal void MarkRecycled() => _recycled = true;

            /// <summary>
            /// R27 finding 11: the owned line is independent of the pooled clone, so a recycle
            /// stops only the clone-related writes — the borrowed game line — and the handle
            /// stays watched so the owned line keeps tracking until the handle's own lifetime
            /// ends. The transition itself is <see cref="DriverRecycleState"/>, the production
            /// struct.
            /// </summary>
            private void DropRecycledDriver()
            {
                var action = _recycle.OnRecycled();
                DropDriverForReuse();
                if (action.HideOwnedLine)
                {
                    OwnedLineEnabled = false;
                    GlowLineEnabled = false;
                }

                if (action.HideGameLine)
                    GameLineEnabled = false;
                if (action.Log != null)
                    Plugin.Logger?.LogInfo(action.Log);
                if (!action.KeepWatching)
                    SequenceLateDriver.Unwatch(this);
            }

            private void KeepDriverActive()
            {
                if (DriverActive) return;
                bool wasInactive = _runtime.DriverStartsInactive;
                DriverActive = true;
                if (wasInactive)
                    Plugin.Logger?.LogInfo(
                        "Effects: sequence driver '" + DriverName
                        + "' SetActive(true) (was activeSelf=False inHierarchy=False)");
            }

            private void RecordTransformWriteFailure()
            {
                if (!_writes.ShouldAttempt(TransformWriteKind.LocalToWorld))
                    return;
                _writes.MarkFailed(TransformWriteKind.LocalToWorld);
                Plugin.Logger?.LogWarning(
                    "Effects: sequence LocalToWorld write threw: TypeManager.GetTypeInfo.TypeSize 4 does not match sizeof 64");
            }

            public void NotePendingHost(FakeHost host) => _pendingHost = host;

            public void Play()
            {
                if (!_released) PlayCount++;
                _runtime.LastPlayed = true;
                EnsurePlayed();
            }

            public void Stop() => _runtime.LastStopped = true;

            public void Release()
            {
                if (_released) return;
                _released = true;
                if (SequenceEntityId != 0)
                {
                    _runtime.DestroyedSequenceIds.Add(SequenceEntityId);
                    SequenceEntityId = 0;
                }

                if (EndMarkerId != 0)
                {
                    _runtime.DestroyedMarkerIds.Add(EndMarkerId);
                    EndMarkerId = 0;
                }

                LogLateSummary();
                SequenceLateDriver.Unwatch(this);
                if (!string.IsNullOrEmpty(DriverName))
                {
                    _runtime.BoundDrivers.Remove(DriverName);
                    _runtime.BoundOwners.Remove(DriverName);
                }

                HasDriver = false;
                if (HasOwnedLine)
                {
                    HasOwnedLine = false;
                    OwnedLineEnabled = false;
                    _runtime.OwnedLineDestroyedCount++;
                }

                if (HasGlowLine)
                {
                    HasGlowLine = false;
                    GlowLineEnabled = false;
                    _runtime.GlowLineDestroyedCount++;
                }

                if (HasGameLine)
                {
                    // Our own Instantiate: ours to destroy. The source prefab is untouched.
                    GameLinePrefabName = "";
                    GameLineEnabled = false;
                    _runtime.GameLineDestroyedCount++;
                }

                DriverActive = false;
                HybridLastWrite = false;
                SequenceLateDriver.Forget(this);
            }
        }

        private sealed class FakeHost : IRuntimeHost
        {
            private bool _released;
            private readonly FakeEffectRuntime _runtime;
            private readonly FakeObject? _obj;
            private readonly int _savedLayer;
            private bool _moved;

            public FakeHost(FakeEffectRuntime runtime, HostKind kind, FakeObject? obj, int layer)
            {
                _runtime = runtime;
                Kind = kind;
                _obj = obj;
                HostedLayer = -1;
                if (kind == HostKind.Overlay)
                    runtime.OverlayReturnedBorrowToRoot = false;
                if (kind == HostKind.RenderTexture)
                {
                    RtWidth = Math.Max(16, (int)runtime.ScreenSize.X);
                    RtHeight = Math.Max(16, (int)runtime.ScreenSize.Y);
                    HostedLayer = layer;
                    if (obj != null)
                    {
                        _savedLayer = obj.Layer;
                        if (obj.HasDriver && layer >= 8)
                        {
                            obj.Layer = layer;
                            _moved = true;
                            Plugin.Logger?.LogInfo(
                                "Effects: RT host moved driver '" + obj.DriverName + "' to layer " + layer
                                + " (restore on dispose).");
                        }
                        else
                            obj.NotePendingHost(this);
                    }
                }
            }

            public HostKind Kind { get; }
            public int HostedLayer { get; }
            public int RtWidth { get; private set; }
            public int RtHeight { get; private set; }
            public bool Alive => !_released;

            public void SyncScreenSize()
            {
                if (_released || Kind != HostKind.RenderTexture) return;
                int w = Math.Max(16, (int)_runtime.ScreenSize.X);
                int h = Math.Max(16, (int)_runtime.ScreenSize.Y);
                if (w == RtWidth && h == RtHeight) return;
                RtWidth = w;
                RtHeight = h;
                Plugin.Logger?.LogInfo("Effects: RT host resized to " + w + "x" + h);
            }

            public void AttachDriver(IRuntimeObject obj)
            {
                if (_released || HostedLayer < 8 || _moved) return;
                if (obj is not FakeObject fake) return;
                fake.Layer = HostedLayer;
                _moved = true;
                Plugin.Logger?.LogInfo(
                    "Effects: RT host moved driver '" + fake.DriverName + "' to layer " + HostedLayer
                    + " (restore on dispose).");
            }

            public void Release()
            {
                if (_released) return;
                _released = true;
                if (Kind == HostKind.Overlay)
                    _runtime.OverlayReturnedBorrowToRoot = true;
                if (_obj != null && Kind == HostKind.RenderTexture && _moved)
                    _obj.Layer = _savedLayer;
            }
        }
    }
}

/// <summary>
/// R28 finding 1 / R28 finding 29.9's "fakes that diverge" note. The production write loop is
/// <c>unsafe</c> game code the test project cannot compile, so this models it exactly — the
/// same element walk over a byte array — and feeds it the length an interop
/// <c>GetBufferLength</c> reported, which is the input the finding is about. A write outside the
/// allocation is recorded rather than performed, so "the old code would have corrupted the
/// chunk" is an assertion instead of a claim.
/// </summary>
/// <summary>
/// EV17. A real <c>BlobArray&lt;SequenceInputElement&gt;</c> laid out in a byte array, so the blob
/// walk the client runs — <see cref="SequenceBlackboardLayout.TryFindSlots"/> — can be driven
/// without the game. The blob root is <c>{ int m_OffsetPtr; int m_Length; }</c> followed by the
/// elements at <c>m_OffsetPtr</c>, which is what <c>SequenceBlackboard.Resolve</c> reads.
/// </summary>
internal sealed class FakeSequenceInputBlob
{
    /// <summary>
    /// The native element stride, as a <b>literal the production code does not read</b>
    /// (R30 finding 22). The fixture used to be built from
    /// <c>SequenceBlackboardLayout.InputElementStride</c>, so changing that constant rebuilt the
    /// blob to match and every walk over it still resolved: the test restated the constant
    /// instead of measuring it. 76 is what the dump's offsets imply (0x48 + 1, rounded up to 4)
    /// and what EV14 hardcoded and observed working live.
    /// </summary>
    internal const int NativeStride = 76;

    private const int Stride = NativeStride;
    private readonly int _offset;
    private readonly List<byte[]> _elements = new();

    internal FakeSequenceInputBlob(int blobOffset) => _offset = blobOffset;

    /// <summary>Append one <c>SequenceInputElement</c>: name, register type, byte offset.</summary>
    internal void AddInput(string name, int type, int index)
    {
        var e = new byte[Stride];
        BitConverter.TryWriteBytes(e.AsSpan(0, 2), (ushort)name.Length);
        for (int i = 0; i < name.Length; i++)
            e[2 + i] = (byte)name[i];
        BitConverter.TryWriteBytes(e.AsSpan(SequenceBlackboardLayout.InputIndexOffset, 4), index);
        BitConverter.TryWriteBytes(e.AsSpan(SequenceBlackboardLayout.InputTypeOffset, 4), type);
        _elements.Add(e);
    }

    private byte[] Build(int extraTailBytes = 0)
    {
        var bytes = new byte[_offset + (_elements.Count * Stride) + extraTailBytes];
        BitConverter.TryWriteBytes(bytes.AsSpan(0, 4), _offset);
        BitConverter.TryWriteBytes(bytes.AsSpan(4, 4), _elements.Count);
        for (int i = 0; i < _elements.Count; i++)
            _elements[i].CopyTo(bytes, _offset + (i * Stride));
        return bytes;
    }

    internal bool TryFindSlots(out int position, out int rotation) =>
        SequenceBlackboardLayout.TryFindSlots(Build(), _offset, _elements.Count, out position, out rotation);

    /// <summary>
    /// The count says N elements but the span stops short — the production code cannot produce
    /// this (WalkOk bounds the span first), so the walk must stop rather than read past the end.
    /// </summary>
    internal bool TryFindSlotsWithTruncatedSpan(out int position, out int rotation)
    {
        var bytes = Build();
        return SequenceBlackboardLayout.TryFindSlots(
            bytes.AsSpan(0, _offset + 4), _offset, _elements.Count, out position, out rotation);
    }

    internal string DescribeInputs() =>
        SequenceBlackboardLayout.DescribeInputs(Build(), _offset, _elements.Count, 8);
}

/// <summary>
/// The module-lifetime state the production line path keeps in statics: the element-size gate
/// and the one-shot log flags. <b>R30 finding 9</b> is about exactly this scope — a per-entity
/// refusal reaching a per-build gate — so the fakes share one of these across "sequences"
/// the way the real calls share the statics, and a test can assert that sequence 1's bad
/// buffer left sequence 2 writing.
/// </summary>
internal sealed class FakeModuleGates
{
    internal LayoutOnceGate ElementSize;
    internal bool ImpossibleLengthLogged;
    internal bool TooSmallLogged;
    internal readonly List<string> Warnings = new();
    internal readonly List<string> Infos = new();

    internal FakeModuleGates()
    {
        // Production runs CheckLineElementSizeOnce before reading the gate, and on a healthy
        // build the constants back each other, so it opens. Model that, so a test that closes
        // it is doing so for the reason it says.
        ElementSize.Check(
            BufferElementGuard.WritesAllowed(
                BufferElementGuard.LineRendererPositionElementSize,
                BufferElementGuard.LineRendererPositionElementPackedBytes,
                BufferElementGuard.LineRendererPositionElementAlignment),
            "must not be logged");
    }
}

/// <summary>
/// A real <c>DynamicBuffer&lt;BuffSequence&gt;</c> laid out in a byte array, so the walk
/// <c>UnityEffectRuntime.TryReadBuffSequenceGuids</c> runs can be driven without the game
/// (R30 finding 2). Each element is <c>{ int GuidHash @0; (4 bytes padding); ulong Condition @8 }</c>
/// = 16 bytes; the <c>Condition</c> value stands in for the live
/// <c>BlobAssetReferenceData.m_Ptr</c> the old 24-byte stride misread as a guid hash.
/// </summary>
internal sealed class FakeBuffSequenceBuffer
{
    private readonly List<byte[]> _elements = new();

    internal void AddEntry(int guidHash, long conditionPointer)
    {
        var e = new byte[BuffSequenceLayout.Stride];
        BitConverter.TryWriteBytes(e.AsSpan(BuffSequenceLayout.SequenceGuidOffset, 4), guidHash);
        BitConverter.TryWriteBytes(
            e.AsSpan(BuffSequenceLayout.ConditionOffset, BuffSequenceLayout.ConditionBytes),
            conditionPointer);
        _elements.Add(e);
    }

    /// <summary>The buffer as the interop hands it over.</summary>
    internal byte[] Bytes
    {
        get
        {
            var bytes = new byte[_elements.Count * BuffSequenceLayout.Stride];
            for (int i = 0; i < _elements.Count; i++)
                _elements[i].CopyTo(bytes, i * BuffSequenceLayout.Stride);
            return bytes;
        }
    }

    /// <summary>The guid hashes the production walk reads, in order.</summary>
    internal int[] WalkGuidHashes()
    {
        var bytes = Bytes;
        var hashes = new List<int>();
        for (int i = 0; i < _elements.Count; i++)
        {
            if (!BuffSequenceLayout.TryReadGuidHash(bytes, i, _elements.Count, out int hash)) break;
            hashes.Add(hash);
        }

        return hashes.ToArray();
    }

    /// <summary>A raw 4-byte read at an arbitrary byte offset — what the old loop did.</summary>
    internal int ReadIntAt(int byteOffset) => BitConverter.ToInt32(Bytes, byteOffset);

    /// <summary>True when <paramref name="size"/> bytes at that offset are inside the buffer.</summary>
    internal bool Fits(int byteOffset, int size) =>
        byteOffset >= 0 && (long)byteOffset + size <= Bytes.Length;
}

internal sealed class FakeLineRendererBuffer
{
    private const int Stride = BufferElementGuard.LineRendererPositionElementSize;
    private readonly byte[] _bytes;
    private readonly int[] _endpointWritten;
    private readonly int _reportedLength;
    private readonly FakeModuleGates _gates;

    internal FakeLineRendererBuffer(
        int reportedLength, int allocatedElements, FakeModuleGates? gates = null)
    {
        _reportedLength = reportedLength;
        _gates = gates ?? new FakeModuleGates();
        _bytes = new byte[allocatedElements * Stride];
        _endpointWritten = new int[allocatedElements];
        for (int i = 0; i < _endpointWritten.Length; i++)
            _endpointWritten[i] = -1;
    }

    /// <summary>How many elements were written.</summary>
    internal int Writes { get; private set; }

    /// <summary>True if any write was aimed outside the real allocation.</summary>
    internal bool WroteOutOfRange { get; private set; }

    /// <summary>
    /// True when the length was <see cref="LineRendererBufferLayout.LengthVerdict.TooSmall"/>:
    /// this entity is skipped and the gate is untouched (R29 finding 4).
    /// </summary>
    internal bool Skipped { get; private set; }

    /// <summary>True once the gate has latched shut — the raw path is off for the build.</summary>
    internal bool GateLatched => _gates.ElementSize.Failed;

    /// <summary>True while the raw path is still available for other sequences.</summary>
    internal bool GateEnabled => _gates.ElementSize.Enabled;

    /// <summary>Set an element's authored <c>PositionIndex</c> before the walk runs.</summary>
    internal void SetPositionIndex(int element, int value) =>
        BitConverter.TryWriteBytes(
            _bytes.AsSpan((element * Stride) + LineRendererBufferLayout.PositionIndexOffset, 4), value);

    /// <summary>An element's <c>PositionIndex</c> as the buffer holds it now.</summary>
    internal int PositionIndexAt(int element) =>
        BitConverter.ToInt32(_bytes, (element * Stride) + LineRendererBufferLayout.PositionIndexOffset);

    /// <summary>
    /// Which endpoint landed in element <paramref name="element"/>: 0 = start, 1 = end,
    /// -1 = untouched. This is what R29 residual 21.9 is about.
    /// </summary>
    internal int EndpointWrittenAt(int element) => _endpointWritten[element];

    /// <summary>The production shape of <c>TryWriteLineRendererPositions</c>, bounds and all.</summary>
    internal bool TryWriteEndpoints()
    {
        if (_gates.ElementSize.Disabled) return false;

        // R29 finding 4: a legitimate 0- or 1-element buffer skips this entity. R30 finding 9:
        // so does an impossible one — EV16 fed both to Fail(), and EV17 fixed only the first
        // half, so a garbage GetBufferLength still turned the path off for every beam.
        var verdict = LineRendererBufferLayout.Classify(_reportedLength, Stride);
        if (verdict == LineRendererBufferLayout.LengthVerdict.TooSmall)
        {
            Skipped = true;
            if (!_gates.TooSmallLogged)
            {
                _gates.TooSmallLogged = true;
                _gates.Infos.Add(LineRendererBufferLayout.LengthSkippedLog(_reportedLength));
            }

            return false;
        }

        if (verdict != LineRendererBufferLayout.LengthVerdict.Ok)
        {
            if (!_gates.ImpossibleLengthLogged)
            {
                _gates.ImpossibleLengthLogged = true;
                _gates.Warnings.Add(LineRendererBufferLayout.LengthRefusedLog(_reportedLength));
            }

            return false;
        }

        long total = LineRendererBufferLayout.TotalBytes(_reportedLength, Stride);
        int index0 = ReadPositionIndex(0, total);
        int index1 = ReadPositionIndex(1, total);
        bool seed = LineRendererBufferLayout.SeedPositionally(index0, index1);

        // R29 residual 21.9: one loop over every element, each consulting its own PositionIndex
        // unless the buffer carries no usable mapping at all.
        for (int i = 0; i < _reportedLength; i++)
        {
            long at = LineRendererBufferLayout.ElementOffset(i, _reportedLength, Stride);
            if (at < 0L) break;
            if (!LineRendererBufferLayout.FieldFits(
                    at,
                    LineRendererBufferLayout.PositionIndexOffset,
                    LineRendererBufferLayout.IndexFieldBytes,
                    total))
            {
                break;
            }

            int endpoint;
            if (seed && i < LineRendererBufferLayout.MinElements)
            {
                endpoint = i;
                if (!InAllocation(at, LineRendererBufferLayout.PositionIndexOffset, 4)) break;
                SetPositionIndex(i, i);
            }
            else
            {
                int authored = ReadPositionIndex(i, total);
                if (authored == int.MinValue) break;
                endpoint = LineRendererBufferLayout.EndpointFor(authored);
            }

            if (endpoint == 0 || endpoint == 1) Write(i, at, total, endpoint);
        }

        return true;
    }

    /// <summary>
    /// <c>PositionIndex</c> of an element, or <c>int.MinValue</c> when it is outside the walk
    /// or outside the real allocation (which the production code cannot do at all, because the
    /// bounds refuse first — recorded here so a regression shows up as a flag, not a crash).
    /// </summary>
    private int ReadPositionIndex(int element, long total)
    {
        long at = LineRendererBufferLayout.ElementOffset(element, _reportedLength, Stride);
        if (at < 0L) return int.MinValue;
        if (!LineRendererBufferLayout.FieldFits(
                at,
                LineRendererBufferLayout.PositionIndexOffset,
                LineRendererBufferLayout.IndexFieldBytes,
                total))
        {
            return int.MinValue;
        }

        if (!InAllocation(at, LineRendererBufferLayout.PositionIndexOffset, 4))
        {
            WroteOutOfRange = true;
            return int.MinValue;
        }

        return PositionIndexAt(element);
    }

    private bool InAllocation(long at, int fieldOffset, int size) =>
        at >= 0L && at + fieldOffset + size <= _bytes.Length;

    private bool Write(int index, long at, long total, int endpoint)
    {
        if (at < 0L) return false;
        if (!LineRendererBufferLayout.FieldFits(
                at,
                LineRendererBufferLayout.PositionValueOffset,
                LineRendererBufferLayout.PositionFieldBytes,
                total))
        {
            return false;
        }

        if (!InAllocation(
                at,
                LineRendererBufferLayout.PositionValueOffset,
                LineRendererBufferLayout.PositionFieldBytes))
        {
            WroteOutOfRange = true;
            return false;
        }

        _endpointWritten[index] = endpoint;
        Writes++;
        return true;
    }
}

/// <summary>
/// A <c>SequenceInput</c> blob as it really sits in memory: an allocation of a known size, and a
/// blob root whose <c>m_OffsetPtr</c> / <c>m_Length</c> may <b>lie</b> about how much of it is
/// elements. <c>BlobAssetHeader.Length</c> is modeled as the allocation's real size, which is
/// what <see cref="SequenceBlackboardLayout.BlobAssetHeaderLengthDelta"/> reads behind the data
/// pointer in production (R31 finding 14).
/// </summary>
internal sealed class FakeBlobAllocation
{
    private const int Stride = FakeSequenceInputBlob.NativeStride;

    private FakeBlobAllocation(byte[] data, int claimedOffset, int claimedCount)
    {
        Data = data;
        ClaimedOffset = claimedOffset;
        ClaimedCount = claimedCount;
    }

    /// <summary>The allocation. Reading past this is the bug finding 14 is about.</summary>
    internal byte[] Data { get; }

    /// <summary>What <c>BlobAssetHeader.Length</c> reads: the allocation's real size.</summary>
    internal long HeaderLength => Data.Length;

    internal int ClaimedOffset { get; }

    internal int ClaimedCount { get; }

    /// <summary>
    /// A stale or differently-versioned blob: a small allocation whose root claims far more
    /// elements than it holds. R31 finding 14's scenario is (256, 8, 512).
    /// </summary>
    internal static FakeBlobAllocation Lying(int allocationBytes, int claimedOffset, int claimedCount)
    {
        var data = new byte[allocationBytes];
        BitConverter.TryWriteBytes(data.AsSpan(0, 4), claimedOffset);
        BitConverter.TryWriteBytes(data.AsSpan(4, 4), claimedCount);
        return new FakeBlobAllocation(data, claimedOffset, claimedCount);
    }

    /// <summary>An honest blob holding the given named registers.</summary>
    internal static FakeBlobAllocation WithInputs(params (string Name, int Type, int Index)[] inputs)
    {
        const int offset = 8;
        var data = new byte[offset + (inputs.Length * Stride)];
        BitConverter.TryWriteBytes(data.AsSpan(0, 4), offset);
        BitConverter.TryWriteBytes(data.AsSpan(4, 4), inputs.Length);
        for (int i = 0; i < inputs.Length; i++)
        {
            var e = data.AsSpan(offset + (i * Stride), Stride);
            BitConverter.TryWriteBytes(e[..2], (ushort)inputs[i].Name.Length);
            for (int c = 0; c < inputs[i].Name.Length; c++)
                e[2 + c] = (byte)inputs[i].Name[c];
            BitConverter.TryWriteBytes(
                e.Slice(SequenceBlackboardLayout.InputIndexOffset, 4), inputs[i].Index);
            BitConverter.TryWriteBytes(
                e.Slice(SequenceBlackboardLayout.InputTypeOffset, 4), inputs[i].Type);
        }

        return new FakeBlobAllocation(data, offset, inputs.Length);
    }
}

/// <summary>
/// <b>R31 finding 26 / R30 open question 7.</b> The game half of <c>SequenceDriverLookup</c>,
/// modeled step for step over the <em>real</em> <see cref="SequenceWalkWiring"/>: read the
/// numbers, ask the wiring, act on the answer. Only the pointer reads are faked, so a test that
/// passes here is a statement about the wiring the client runs — which is the half every one of
/// EV15, EV16 and EV17 broke while the tested helpers stayed correct.
/// </summary>
internal sealed class FakeSequenceRuntime : IEffectGateLog
{
    private readonly SequenceWalkWiring _wiring = new();

    /// <summary>What <c>sizeof(SequenceInputElement)</c> answers on this "build". 0 = threw.</summary>
    internal int SequenceInputSizeOf { get; set; } = 73;

    /// <summary>What <c>sizeof(BlackboardElement)</c> answers. 0 = threw.</summary>
    internal int BlackboardElementSizeOf { get; set; } = 1;

    /// <summary>What <c>TypeManager.GetTypeInfo(index).ElementSize</c> answers. 0 = unknown.</summary>
    internal int TypeManagerElementSize { get; set; } = 1;

    internal List<string> Warnings { get; } = new();

    internal List<string> Infos { get; } = new();

    /// <summary>
    /// Set when the walk would have built a span past the end of the allocation. In production
    /// that is the access violation the <c>catch (Exception)</c> cannot catch, so it is recorded
    /// rather than performed.
    /// </summary>
    internal bool ReadPastAllocation { get; private set; }

    internal bool InputGateChecked => _wiring.InputGateChecked;

    internal void Reset()
    {
        _wiring.Reset();
        Warnings.Clear();
        Infos.Clear();
        ReadPastAllocation = false;
    }

    public void Info(string message) => Infos.Add(message);

    public void Warning(string message) => Warnings.Add(message);

    /// <summary><c>SequenceDriverLookup.Resolve</c>, with the pointer reads faked.</summary>
    internal bool TryResolve(FakeBlobAllocation blob, out int position, out int rotation)
    {
        position = -1;
        rotation = -1;
        if (!_wiring.InputWalkEnabled(() => SequenceInputSizeOf, this)) return false;

        int offset = blob.ClaimedOffset;
        int count = blob.ClaimedCount;
        if (!_wiring.InputWalkInRange(offset, count, blob.HeaderLength, this)) return false;

        long end = SequenceBlackboardLayout.WalkEnd(offset, count);
        if (end > blob.Data.Length)
        {
            // Production would have constructed `new ReadOnlySpan<byte>(blob, (int)end)` here.
            ReadPastAllocation = true;
            return false;
        }

        return SequenceBlackboardLayout.TryFindSlots(
            blob.Data.AsSpan(0, (int)end), offset, count, out position, out rotation);
    }

    /// <summary><c>SequenceDriverLookup.TryBuffer</c> plus the slot bound, with the ECS faked.</summary>
    internal bool TryWriteSlot(int bufferLength, int slot, int bytes)
    {
        if (!_wiring.BufferWritesEnabled(
                () => BlackboardElementSizeOf, () => TypeManagerElementSize, this))
        {
            return false;
        }

        if (!_wiring.BufferLengthUsable(bufferLength, this)) return false;
        return SequenceBlackboardLayout.SlotFits(slot, bytes, bufferLength);
    }
}

