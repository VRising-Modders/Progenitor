using System;
using System.Linq;
using Progenitor.Content;
using Xunit;

namespace Progenitor.Tests;

public class WearableHandSkinTests
{
    [Fact]
    public void Closer_neighbor_finger_and_opposite_hand_cannot_drive_index_vertex()
    {
        var samples = new[]
        {
            Point(0f, new WearableSkinInfluence("Left_Middle02_JNT", 1f)),
            Point(.001f, new WearableSkinInfluence("Right_Index02_JNT", 1f)),
            Point(.01f, new WearableSkinInfluence("Left_Index02_JNT", .7f), new WearableSkinInfluence("Left_Index03_JNT", .3f)),
        };
        var selected = WearableHandSkin.Filter(samples, "Left_Index02_JNT");
        Assert.Single(selected);
        Assert.True(WearableSkinTransfer.TryTransfer(new(0f, 0f, 0f), selected, .06f, out var weights));
        Assert.Equal(.7f, weights.Single(w => w.Bone == "Left_Index02_JNT").Weight);
        Assert.Equal(.3f, weights.Single(w => w.Bone == "Left_Index03_JNT").Weight);
    }

    [Fact]
    public void Thumb_typo_alias_preserves_thumb_joint_and_wrist_weights()
    {
        var sample = Point(0f, new WearableSkinInfluence("Left_Thumb01_JNT", .25f),
            new WearableSkinInfluence("Left_Thum02_JNT", .5f), new WearableSkinInfluence("Left_Wrist_JNT", .25f));
        var selected = WearableHandSkin.Filter(new[] { sample }, "Left_Thumb03_JNT");
        var kept = Assert.Single(selected).Influences;
        Assert.Equal(3, kept.Count);
        Assert.Equal(.5f, kept.Single(w => w.Bone == "Left_Thum02_JNT").Weight);
        Assert.Equal(.25f, kept.Single(w => w.Bone == "Left_Wrist_JNT").Weight);
        Assert.InRange(kept.Sum(w => w.Weight), .99999f, 1.00001f);
        Assert.True(WearableHandSkin.IsDigit("Left_Thum02_JNT"));
    }

    [Fact]
    public void Mixed_adjacent_digit_reference_is_rejected_instead_of_carrying_foreign_influence()
    {
        var sample = Point(0f, new WearableSkinInfluence("Left_Index01_JNT", .7f),
            new WearableSkinInfluence("Left_Middle01_JNT", .3f));
        Assert.Empty(WearableHandSkin.Filter(new[] { sample }, "Left_Index01_JNT"));
    }

    [Fact]
    public void Palm_and_forearm_only_use_same_hand_nonfinger_sources()
    {
        var samples = new[]
        {
            Point(0f, new WearableSkinInfluence("Left_Index01_JNT", 1f)),
            Point(0f, new WearableSkinInfluence("Right_Wrist_JNT", 1f)),
            Point(0f, new WearableSkinInfluence("Left_Hip_JNT", 1f)),
            Point(0f, new WearableSkinInfluence("Left_Wrist_JNT", .6f), new WearableSkinInfluence("Left_Elbow_JNT", .4f)),
        };
        var palm = Assert.Single(WearableHandSkin.Filter(samples, "Left_Wrist_JNT"));
        Assert.Equal(2, palm.Influences.Count);
        Assert.Equal(.6f, palm.Influences.Single(w => w.Bone == "Left_Wrist_JNT").Weight);
        Assert.Single(WearableHandSkin.Filter(samples, "Left_Elbow_JNT"));
    }

    [Fact]
    public void Wrist_keeps_thumb_web_blend_without_borrowing_finger_or_pure_thumb_skin()
    {
        var samples = new[]
        {
            Point(0f, new WearableSkinInfluence("Left_Wrist_JNT", .31f),
                new WearableSkinInfluence("Left_Thumb01_JNT", .69f)),
            Point(.01f, new WearableSkinInfluence("Left_Wrist_JNT", .3f),
                new WearableSkinInfluence("Left_Index01_JNT", .7f)),
            Point(.02f, new WearableSkinInfluence("Left_Thumb01_JNT", 1f)),
        };
        var web = Assert.Single(WearableHandSkin.Filter(samples, "Left_Wrist_JNT"));
        Assert.Equal(.31f, web.Influences.Single(w => w.Bone == "Left_Wrist_JNT").Weight);
        Assert.Equal(.69f, web.Influences.Single(w => w.Bone == "Left_Thumb01_JNT").Weight);
        Assert.Empty(WearableHandSkin.Filter(samples, "Left_Elbow_JNT"));
    }

    [Fact]
    public void Absent_digit_sources_return_no_transfer_instead_of_borrowing_global_sample()
    {
        var selected = WearableHandSkin.Filter(new[] { Point(0f, new WearableSkinInfluence("Left_Wrist_JNT", 1f)) }, "Left_Pinky03_JNT");
        Assert.Empty(selected);
        Assert.False(WearableSkinTransfer.TryTransfer(new(0f, 0f, 0f), selected, .06f, out _));
        Assert.Empty(WearableHandSkin.Filter(Array.Empty<WearableSkinSample>(), "Unknown"));
    }

    private static WearableSkinSample Point(float x, params WearableSkinInfluence[] influences) => new(new(x, 0f, 0f), influences);
}
