using System;
using System.Linq;
using Progenitor.Content;
using Xunit;

namespace Progenitor.Tests;

public class WearableSkinTransferTests
{
    [Fact]
    public void Coincident_vertex_preserves_source_distribution_without_neighbor_bleed()
    {
        var samples = new[]
        {
            Sample(0f, new WearableSkinInfluence("Shoulder", .3f), new WearableSkinInfluence("Chest", .7f)),
            Sample(.001f, new WearableSkinInfluence("Hand", 1f)),
        };
        Assert.True(WearableSkinTransfer.TryTransfer(new(0f, 0f, 0f), samples, 1f, out var result));
        Assert.Equal(2, result.Length);
        Assert.Equal(.7f, result.Single(w => w.Bone == "Chest").Weight);
        Assert.Equal(.3f, result.Single(w => w.Bone == "Shoulder").Weight);
    }

    [Fact]
    public void Nearby_reference_points_blend_by_distance_with_normalized_samples()
    {
        // Source weight magnitudes must not make the farther sample dominate.
        var samples = new[] { Sample(1f, new WearableSkinInfluence("Elbow", 1f)), Sample(2f, new WearableSkinInfluence("Wrist", 100f)) };
        Assert.True(WearableSkinTransfer.TryTransfer(new(0f, 0f, 0f), samples, 3f, out var result));
        Assert.Equal(.8f, result.Single(w => w.Bone == "Elbow").Weight);
        Assert.Equal(.2f, result.Single(w => w.Bone == "Wrist").Weight);
    }

    [Fact]
    public void Only_k_nearest_within_cutoff_contribute()
    {
        var samples = new[] { Sample(1f, new WearableSkinInfluence("Near", 1f)), Sample(2f, new WearableSkinInfluence("Far", 1f)) };
        Assert.True(WearableSkinTransfer.TryTransfer(new(0f, 0f, 0f), samples, 3f, out var nearestOnly, 1));
        Assert.Equal("Near", Assert.Single(nearestOnly).Bone);
        Assert.True(WearableSkinTransfer.TryTransfer(new(0f, 0f, 0f), samples, 1f, out var atCutoff));
        Assert.Equal("Near", Assert.Single(atCutoff).Bone);
        Assert.False(WearableSkinTransfer.TryTransfer(new(0f, 0f, 0f), samples, .9f, out var absent));
        Assert.Empty(absent);
    }

    [Fact]
    public void Merged_distribution_keeps_four_strongest_and_renormalizes()
    {
        var samples = new[]
        {
            new WearableSkinSample(new(1f, 0f, 0f), new WearableSkinInfluence("A", .6f), new WearableSkinInfluence("E", .4f)),
            new WearableSkinSample(new(-1f, 0f, 0f), new WearableSkinInfluence("B", .6f), new WearableSkinInfluence("F", .4f)),
            new WearableSkinSample(new(0f, 1f, 0f), new WearableSkinInfluence("C", .6f), new WearableSkinInfluence("G", .4f)),
            new WearableSkinSample(new(0f, -1f, 0f), new WearableSkinInfluence("D", .6f), new WearableSkinInfluence("H", .4f)),
        };
        Assert.True(WearableSkinTransfer.TryTransfer(new(0f, 0f, 0f), samples, 2f, out var result));
        Assert.Equal(new[] { "A", "B", "C", "D" }, result.Select(w => w.Bone));
        Assert.All(result, w => Assert.Equal(.25f, w.Weight));
    }

    [Fact]
    public void Invalid_measurements_are_ignored_and_duplicate_bones_are_combined()
    {
        var samples = new[]
        {
            Sample(float.NaN, new WearableSkinInfluence("Bad", 1f)),
            Sample(.1f, new WearableSkinInfluence("", 1f), new WearableSkinInfluence("Bad", float.NaN), new WearableSkinInfluence("Bad", -1f)),
            Sample(1f, new WearableSkinInfluence("Chest", .2f), new WearableSkinInfluence("Chest", .3f), new WearableSkinInfluence("Shoulder", .5f)),
        };
        Assert.True(WearableSkinTransfer.TryTransfer(new(0f, 0f, 0f), samples, 2f, out var result));
        Assert.Equal(2, result.Length);
        Assert.All(result, w => Assert.Equal(.5f, w.Weight));
        Assert.False(WearableSkinTransfer.TryTransfer(new(float.PositiveInfinity, 0f, 0f), samples, 2f, out _));
    }

    [Fact]
    public void Empty_or_too_distant_samples_return_no_transfer()
    {
        Assert.False(WearableSkinTransfer.TryTransfer(new(0f, 0f, 0f), Array.Empty<WearableSkinSample>(), 0f, out var result));
        Assert.Empty(result);
        Assert.False(WearableSkinTransfer.TryTransfer(new(0f, 0f, 0f), new[] { Sample(1f, new WearableSkinInfluence("Knee", 1f)) }, 0f, out _));
    }

    private static WearableSkinSample Sample(float x, params WearableSkinInfluence[] weights) => new(new(x, 0f, 0f), weights);
}
