using System;
using Progenitor.Models;
using Xunit;

namespace Progenitor.Tests;

public class GltfAnimationSamplerTests
{
    private static GltfScene Scene()
    {
        var scene = new GltfScene();
        scene.Nodes.Add(new GltfNode("node") { Translation = new float[] { 10, 20, 30 } });
        return scene;
    }

    private static GltfAnimation Clip(string path, string interpolation, float[] times, float[] values)
    {
        var clip = new GltfAnimation { Name = "clip" };
        clip.Channels.Add(new GltfAnimationChannel
        { Node = 0, Path = path, Interpolation = interpolation, Times = times, Values = values });
        return clip;
    }

    [Theory]
    [InlineData(-100, 2)]
    [InlineData(0, 2)]
    [InlineData(2, 2)]
    [InlineData(3, 3)]
    [InlineData(4, 4)]
    [InlineData(100, 4)]
    public void LinearClampsAndInterpolatesWithNonzeroFirstKey(float time, float expected)
    {
        var sampler = new GltfAnimationSampler(Scene(), Clip("translation", "LINEAR", new float[] { 2, 4 }, new float[] { 2, 0, 0, 4, 0, 0 }));
        var t = new float[3];
        sampler.Sample(time, t, new float[4], new float[3]);
        Assert.Equal(expected, t[0]);
        Assert.Equal(4, sampler.Duration);
    }

    [Theory]
    [InlineData(3.9f, 2)]
    [InlineData(4, 4)]
    public void StepUsesLastKeyAtExactBoundary(float time, float expected)
    {
        var sampler = new GltfAnimationSampler(Scene(), Clip("translation", "STEP", new float[] { 2, 4 }, new float[] { 2, 0, 0, 4, 0, 0 }));
        var t = new float[3];
        sampler.Sample(time, t, new float[4], new float[3]);
        Assert.Equal(expected, t[0]);
    }

    [Fact]
    public void OneKeyAndMissingChannelsRestoreBasePose()
    {
        var sampler = new GltfAnimationSampler(Scene(), Clip("scale", "LINEAR", new float[] { 2 }, new float[] { 2, 3, 4 }));
        var t = new float[] { -1, -1, -1 };
        var r = new float[4];
        var s = new float[3];
        sampler.Sample(-10, t, r, s);
        Assert.Equal(new float[] { 10, 20, 30 }, t);
        Assert.Equal(new float[] { 0, 0, 0, 1 }, r);
        Assert.Equal(new float[] { 2, 3, 4 }, s);
        t[0] = 100;
        sampler.Sample(100, t, r, s);
        Assert.Equal(10, t[0]);
    }

    [Fact]
    public void AntipodalRotationsStayNormalizedWithoutMutatingAsset()
    {
        float[] values = { 0, 0, 0, 2, 0, 0, 0, -2 };
        var sampler = new GltfAnimationSampler(Scene(), Clip("rotation", "LINEAR", new float[] { 0, 1 }, values));
        var r = new float[4];
        sampler.Sample(0.5f, new float[3], r, new float[3]);
        Assert.Equal(new float[] { 0, 0, 0, 1 }, r);
        Assert.Equal(2, values[3]);
        Assert.Equal(-2, values[7]);
    }

    [Fact]
    public void RotationUsesSphericalShortestPath()
    {
        var sampler = new GltfAnimationSampler(Scene(), Clip("rotation", "LINEAR", new float[] { 0, 1 }, new float[] { 0, 0, 0, 1, 0, 0, 1, 0 }));
        var r = new float[4];
        sampler.Sample(0.25f, new float[3], r, new float[3]);
        Assert.Equal((float)Math.Sin(Math.PI / 8), r[2], 5);
        Assert.Equal((float)Math.Cos(Math.PI / 8), r[3], 5);
    }

    [Fact]
    public void SamplerSnapshotsAssetsAndEmptyClipRestoresBase()
    {
        var scene = Scene();
        var clip = Clip("translation", "LINEAR", new float[] { 0, 1 }, new float[] { 0, 0, 0, 2, 0, 0 });
        var sampler = new GltfAnimationSampler(scene, clip);
        clip.Channels[0].Values[3] = 500;
        scene.Nodes[0].Scale[0] = 500;
        var t = new float[3];
        var s = new float[3];
        sampler.Sample(0.5f, t, new float[4], s);
        Assert.Equal(1, t[0]);
        Assert.Equal(1, s[0]);
        var empty = new GltfAnimationSampler(Scene(), new GltfAnimation());
        empty.Sample(0, t, new float[4], s);
        Assert.Equal(new float[] { 10, 20, 30 }, t);
        Assert.Equal(0, empty.Duration);
    }

    [Fact]
    public void ConstructorRejectsMalformedChannels()
    {
        Assert.Throws<ArgumentException>(() => new GltfAnimationSampler(Scene(), Clip("translation", "LINEAR", new float[] { 1, 1 }, new float[6])));
        Assert.Throws<ArgumentException>(() => new GltfAnimationSampler(Scene(), Clip("translation", "LINEAR", new float[] { float.NaN }, new float[3])));
        Assert.Throws<ArgumentException>(() => new GltfAnimationSampler(Scene(), Clip("translation", "LINEAR", new float[] { 0 }, new float[] { float.PositiveInfinity, 0, 0 })));
        Assert.Throws<ArgumentException>(() => new GltfAnimationSampler(Scene(), Clip("rotation", "LINEAR", new float[] { 0 }, new float[4])));
        Assert.Throws<ArgumentException>(() => new GltfAnimationSampler(Scene(), Clip("scale", "CUBICSPLINE", new float[] { 0 }, new float[3])));
        Assert.Throws<ArgumentException>(() => new GltfAnimationSampler(Scene(), Clip("scale", "LINEAR", new float[] { 0 }, new float[2])));
        Assert.Throws<ArgumentException>(() => new GltfAnimationSampler(Scene(), Clip("scale", "LINEAR", Array.Empty<float>(), Array.Empty<float>())));
        var duplicate = Clip("scale", "LINEAR", new float[] { 0 }, new float[] { 1, 1, 1 });
        duplicate.Channels.Add(duplicate.Channels[0]);
        Assert.Throws<ArgumentException>(() => new GltfAnimationSampler(Scene(), duplicate));
    }

    [Fact]
    public void SampleRejectsNonfiniteTimeAndIncorrectBuffers()
    {
        var sampler = new GltfAnimationSampler(Scene(), Clip("scale", "STEP", new float[] { 0 }, new float[] { 1, 1, 1 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => sampler.Sample(float.NaN, new float[3], new float[4], new float[3]));
        Assert.Throws<ArgumentException>(() => sampler.Sample(0, new float[2], new float[4], new float[3]));
        var shared = new float[3];
        Assert.Throws<ArgumentException>(() => sampler.Sample(0, shared, new float[4], shared));
    }
}
