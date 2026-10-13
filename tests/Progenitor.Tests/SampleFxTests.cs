using System;
using Progenitor.Dev;
using Progenitor.Effects;
using Progenitor.Sample;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Game-free coverage of the Sample <c>.fx</c> subcommands: token parse, fake-runtime borrow/host,
/// and <c>.fx off</c> disposing every handle the Sample still holds.
/// </summary>
[Collection("PluginLogger")]
public class SampleFxTests : IDisposable
{
    private readonly TestLogSource _log = new();
    private readonly EffectsTests.FakeEffectRuntime _client = new() { IsClient = true };
    private readonly object _owner = new();

    public SampleFxTests()
    {
        Plugin.Logger = _log;
        Assets.ResetForTests();
        MainThread.ResetForTests();
        _client.Add(AssetKind.Image, SampleFx.ImageName);
        _client.Add(AssetKind.Particle, SampleFx.ParticleName);
        _client.Add(AssetKind.Particle, SampleFx.FlameName);
        _client.Add(AssetKind.Sequence, SampleFx.BeamName);
        _client.Add(AssetKind.Volume, SampleFx.VolumeName);
        Assets.SetRuntimeForTests(_client);
        SampleFx.AttachForTests(_owner, "progenitor.sample");
    }

    public void Dispose()
    {
        SampleFx.Detach();
        Assets.ResetForTests();
        MainThread.ResetForTests();
        Plugin.Logger = null;
    }

    [Theory]
    [InlineData("image", 1)]
    [InlineData("IMAGE", 1)]
    [InlineData(" particle ", 2)]
    [InlineData("beam", 3)]
    [InlineData("vignette", 4)]
    [InlineData("off", 5)]
    [InlineData("beam menu", 6)]
    [InlineData("BEAM MENU", 6)]
    [InlineData("  particle   menu  ", 7)]
    [InlineData("particle embers", 2)]
    [InlineData("particle embers 4", 2)]
    [InlineData("particle 4", 2)]
    [InlineData("particle Flame 2", 2)]
    [InlineData("beam thin", 3)]
    [InlineData("beam red", 3)]
    [InlineData("beam blue", 3)]
    [InlineData("beam red menu", 6)]
    public void TryParse_accepts_the_sample_subcommands(string token, int expected)
    {
        Assert.True(SampleFx.TryParse(token, out var kind));
        Assert.Equal(expected, (int)kind);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ember")]
    [InlineData("fx")]
    [InlineData("images")]
    [InlineData("image menu")]
    [InlineData("beam overlay")]
    [InlineData("menu")]
    [InlineData("beam menu extra")]
    [InlineData("particle menu extra")]
    [InlineData("particle embers 4 extra")]
    [InlineData("particle 4 embers")]
    public void TryParse_rejects_unknown_or_empty(string? token)
    {
        Assert.False(SampleFx.TryParse(token, out _));
    }

    [Fact]
    public void Image_borrows_PassiveFrame_Normal_onto_the_overlay_gold()
    {
        SampleFx.Run(SampleFx.Kind.Image);
        Assert.Equal(1, SampleFx.LiveCount);
        Assert.True(_client.HasObject(SampleFx.ImageName));
        Assert.Equal(1, _client.HostCount);
        Assert.Contains(_log.Infos, l => l == "Sample: fx image borrow " + SampleFx.ImageName);
        Assert.Contains(_log.Infos, l => l == "Sample: fx image color gold size=192 center");
        Assert.Contains(_log.Infos, l => l == "Sample: fx image host Overlay");
        Assert.Contains(_log.Infos, l => l.StartsWith("Sample: fx image dispose in ", StringComparison.Ordinal));
    }

    [Fact]
    public void Off_disposes_every_handle_the_sample_borrowed()
    {
        SampleFx.Run(SampleFx.Kind.Image);
        SampleFx.Run(SampleFx.Kind.Particle);
        SampleFx.Run(SampleFx.Kind.Beam, new EffectVec3(1f, 2f, 3f), new EffectVec3(6f, 2f, 3f));
        SampleFx.Run(SampleFx.Kind.Vignette);
        Assert.Equal(5, SampleFx.LiveCount);
        Assert.True(_client.HasObject(SampleFx.ImageName));
        Assert.True(_client.HasObject(SampleFx.ParticleName));
        Assert.True(_client.HasObject(SampleFx.FlameName));
        Assert.True(_client.HasObject(SampleFx.BeamName));
        Assert.True(_client.HasObject(SampleFx.VolumeName));

        int n = SampleFx.Off();
        Assert.Equal(5, n);
        Assert.Equal(0, SampleFx.LiveCount);
        Assert.False(_client.HasObject(SampleFx.ImageName));
        Assert.False(_client.HasObject(SampleFx.ParticleName));
        Assert.False(_client.HasObject(SampleFx.FlameName));
        Assert.False(_client.HasObject(SampleFx.BeamName));
        Assert.False(_client.HasObject(SampleFx.VolumeName));
        Assert.Contains(_log.Infos, l => l.StartsWith("Sample: fx off dispose ", StringComparison.Ordinal));

        SampleFx.Off();
        Assert.Equal(0, SampleFx.LiveCount);
    }

    [Fact]
    public void Particle_plays_a_world_clone_near_the_origin_without_hosting()
    {
        var origin = new EffectVec3(4f, 5f, 6f);
        SampleFx.Run(SampleFx.Kind.Particle, origin);
        Assert.Equal(2, SampleFx.LiveCount);
        Assert.True(_client.HasObject(SampleFx.FlameName));
        Assert.True(_client.HasObject(SampleFx.ParticleName));
        Assert.Equal(0, _client.HostCount);
        Assert.Equal(SampleFx.DefaultFlameScale, _client.ObjectScale(SampleFx.FlameName));
        Assert.Equal(SampleFx.DefaultEmbersScale, _client.ObjectScale(SampleFx.ParticleName));
        Assert.Contains(_log.Infos, l => l == "Sample: fx particle borrow " + SampleFx.FlameName);
        Assert.Contains(_log.Infos, l => l == "Sample: fx particle scale 2");
        Assert.Contains(_log.Infos, l => l == "Sample: fx particle borrow " + SampleFx.ParticleName);
        Assert.Contains(_log.Infos, l => l == "Sample: fx particle scale 3");
        Assert.Contains(_log.Infos, l => l == "Sample: fx particle tint gold");
        Assert.Contains(_log.Infos, l => l == "Sample: fx particle world " + origin);
        Assert.DoesNotContain(_log.Infos, l => l == "Sample: fx particle host RenderTexture");
        Assert.Contains(_log.Infos, l => l.StartsWith("Sample: fx particle dispose in ", StringComparison.Ordinal));
        SampleFx.Off();
        Assert.False(_client.HasObject(SampleFx.FlameName));
        Assert.False(_client.HasObject(SampleFx.ParticleName));
    }

    [Fact]
    public void Particle_token_name_and_scale_pick_one_clone()
    {
        Assert.True(SampleFx.TryParse("particle embers 4", out var kind));
        Assert.Equal(SampleFx.Kind.Particle, kind);
        Assert.Equal("embers", SampleFx.ParsedParticleName);
        Assert.True(SampleFx.ParsedParticleHasScale);
        Assert.Equal(4f, SampleFx.ParsedParticleScale);

        var origin = new EffectVec3(1f, 2f, 3f);
        SampleFx.Run(kind, origin);
        Assert.Equal(1, SampleFx.LiveCount);
        Assert.True(_client.HasObject("embers"));
        Assert.False(_client.HasObject(SampleFx.FlameName));
        Assert.Equal(4f, _client.ObjectScale("embers"));
        Assert.Contains(_log.Infos, l => l == "Sample: fx particle borrow embers");
        Assert.Contains(_log.Infos, l => l == "Sample: fx particle scale 4");
        Assert.Contains(_log.Infos, l => l == "Sample: fx particle world " + origin);
        SampleFx.Off();
        Assert.Equal(0, SampleFx.LiveCount);
    }

    [Fact]
    public void Particle_token_name_without_scale_uses_the_named_default()
    {
        Assert.True(SampleFx.TryParse("particle embers", out var kind));
        Assert.Equal("embers", SampleFx.ParsedParticleName);
        Assert.False(SampleFx.ParsedParticleHasScale);
        SampleFx.Run(kind);
        Assert.Equal(1, SampleFx.LiveCount);
        Assert.Equal(SampleFx.DefaultNamedScale, _client.ObjectScale("embers"));
        SampleFx.Off();
    }

    [Fact]
    public void Particle_menu_skips_host_when_the_module_cannot_size_the_RawImage()
    {
        Assert.False(SampleFx.HostApiCanSizeRawImage);
        SampleFx.Run(SampleFx.Kind.ParticleMenu);
        Assert.Equal(0, SampleFx.LiveCount);
        Assert.Equal(0, _client.HostCount);
        Assert.False(_client.HasObject(SampleFx.ParticleName));
        Assert.Contains(_log.Infos, l => l == "Sample: fx particle menu needs a sized host (module follow-up)");
        Assert.DoesNotContain(_log.Infos, l => l.StartsWith("Sample: fx particle borrow ", StringComparison.Ordinal));
        Assert.DoesNotContain(_log.Infos, l => l == "Sample: fx particle host RenderTexture");
    }

    [Fact]
    public void Beam_sets_two_endpoints_in_the_world_without_hosting()
    {
        var start = new EffectVec3(10f, 1f, 2f);
        var end = new EffectVec3(15f, 1f, 2f);
        SampleFx.Run(SampleFx.Kind.Beam, start, end);
        Assert.Equal(1, SampleFx.LiveCount);
        Assert.True(_client.HasObject(SampleFx.BeamName));
        Assert.Equal(0, _client.HostCount);
        Assert.Equal(0, _client.ObjectLayer(SampleFx.BeamName));
        Assert.Contains(_log.Infos, l => l == "Sample: fx beam style thin");
        Assert.Contains(_log.Infos, l => l == "Sample: fx beam borrow " + SampleFx.BeamName);
        Assert.Contains(_log.Infos, l => l == "Sample: fx beam start=" + start + " end=" + end);
        Assert.Contains(_log.Infos, l => l == "Sample: fx beam tint blue");
        Assert.DoesNotContain(_log.Infos, l => l == "Sample: fx beam host RenderTexture");
        Assert.Contains(_log.Infos, l => l.StartsWith("Sample: fx beam dispose in ", StringComparison.Ordinal));
        SampleFx.Off();
        Assert.False(_client.HasObject(SampleFx.BeamName));
        Assert.Equal(0, _client.HostCount);
    }

    [Fact]
    public void Beam_red_token_borrows_the_game_red_preset()
    {
        _client.Add(AssetKind.Sequence, "seq-red");
        _client.BeamPresets[BeamStyle.GameRed] = "seq-red";
        _client.DriverIsTrailSprite = false;
        Assert.True(SampleFx.TryParse("beam red", out var kind));
        Assert.Equal(SampleFx.Kind.Beam, kind);
        Assert.True(SampleFx.ParsedHasBeamStyle);
        Assert.Equal(BeamStyle.GameRed, SampleFx.ParsedBeamStyle);
        SampleFx.Run(kind, new EffectVec3(1f, 2f, 3f), new EffectVec3(6f, 2f, 3f));
        Assert.Equal(1, SampleFx.LiveCount);
        Assert.True(_client.HasObject("seq-red"));
        Assert.Contains(_log.Infos, l => l == "Sample: fx beam style red");
        Assert.Contains(_log.Infos, l => l == "Sample: fx beam borrow seq-red");
        Assert.Contains(_log.Infos, l => l == "Sample: fx beam tint preset");
        SampleFx.Off();
    }

    [Fact]
    public void Beam_menu_plays_in_the_world_and_skips_an_unsized_host()
    {
        Assert.False(SampleFx.HostApiCanSizeRawImage);
        var start = new EffectVec3(10f, 1f, 2f);
        var end = new EffectVec3(15f, 1f, 2f);
        SampleFx.Run(SampleFx.Kind.BeamMenu, start, end);
        Assert.Equal(1, SampleFx.LiveCount);
        Assert.True(_client.HasObject(SampleFx.BeamName));
        Assert.Equal(0, _client.HostCount);
        Assert.Equal(0, _client.ObjectLayer(SampleFx.BeamName));
        Assert.Contains(_log.Infos, l => l == "Sample: fx beam style thin");
        Assert.Contains(_log.Infos, l => l == "Sample: fx beam borrow " + SampleFx.BeamName);
        Assert.Contains(_log.Infos, l => l == "Sample: fx beam start=" + start + " end=" + end);
        Assert.Contains(_log.Infos, l => l == "Sample: fx beam tint blue");
        Assert.Contains(_log.Infos, l => l == "Sample: fx beam menu needs a sized host (module follow-up)");
        Assert.DoesNotContain(_log.Infos, l => l == "Sample: fx beam host RenderTexture");
        Assert.Contains(_log.Infos, l => l.StartsWith("Sample: fx beam dispose in ", StringComparison.Ordinal));
        SampleFx.Off();
        Assert.False(_client.HasObject(SampleFx.BeamName));
    }

    [Fact]
    public void Vignette_logs_saturated_gold_intensity_and_game_post_effects()
    {
        SampleFx.Run(SampleFx.Kind.Vignette);
        Assert.Equal(1, SampleFx.LiveCount);
        Assert.True(_client.HasObject(SampleFx.VolumeName));
        Assert.Contains(_log.Infos, l => l == "Sample: fx vignette borrow");
        Assert.Contains(
            _log.Infos,
            l => l == "Sample: fx vignette tint gold intensity=0.75 smoothness=0.6 (camera-wide; the game's own post effects can dominate)");
        SampleFx.Off();
    }

    [Fact]
    public void Off_after_a_zero_delay_timer_does_not_throw()
    {
        SampleFx.ImageSeconds = 0;
        SampleFx.Run(SampleFx.Kind.Image);
        Assert.Equal(1, SampleFx.LiveCount);
        MainThread.Drain();
        Assert.Equal(0, SampleFx.LiveCount);
        Assert.Contains(_log.Infos, l => l == "Sample: fx image disposed");
        Assert.Equal(0, SampleFx.Off());
    }

    [Fact]
    public void Beam_and_particle_world_clones_still_dispose_on_PostDelayed()
    {
        SampleFx.BeamSeconds = 0;
        SampleFx.ParticleSeconds = 0;
        SampleFx.Run(SampleFx.Kind.Beam, new EffectVec3(1f, 2f, 3f), new EffectVec3(6f, 2f, 3f));
        SampleFx.Run(SampleFx.Kind.Particle, new EffectVec3(4f, 5f, 6f));
        Assert.Equal(3, SampleFx.LiveCount);
        Assert.Equal(0, _client.HostCount);
        MainThread.Drain();
        Assert.Equal(0, SampleFx.LiveCount);
        Assert.Contains(_log.Infos, l => l == "Sample: fx beam disposed");
        Assert.Contains(_log.Infos, l => l == "Sample: fx particle disposed");
        Assert.Equal(0, SampleFx.Off());
    }
}
