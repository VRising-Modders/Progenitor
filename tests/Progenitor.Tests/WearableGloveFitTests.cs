using System;
using System.Collections.Generic;
using System.Linq;
using Progenitor.Content;
using Xunit;

namespace Progenitor.Tests;

public class WearableGloveFitTests
{
    [Fact]
    public void Existing_compiled_glove_calls_keep_the_three_argument_method()
    {
        Assert.NotNull(typeof(WearableSketch).GetMethod("Glove", new[] { typeof(string), typeof(string), typeof(WearableSurface) }));
    }

    [Fact]
    public void Measured_fingertip_extent_replaces_the_joint_distance_guess()
    {
        var pose = WearableMeshTests.Pose(.18f);
        string tip = "Left_Index03_JNT";
        var axis = (pose[tip] - pose["Left_Index02_JNT"]).Normalized();
        var radial = Perpendicular(axis);
        var samples = new List<WearableSkinSample>();
        for (int i = 0; i < 8; i++)
        {
            float angle = i * MathF.PI / 4f;
            var point = pose[tip] + axis * .065f + radial * (.008f * MathF.Cos(angle));
            samples.Add(new WearableSkinSample(point, new WearableSkinInfluence(tip, 1f)));
        }
        var baseMesh = Glove(pose, null);
        var fitted = Glove(pose, samples);
        float Extent(WearableMesh mesh) => Enumerable.Range(0, mesh.Vertices.Length)
            .Where(i => mesh.Bones[mesh.BoneIndex[i]] == tip)
            .Max(i => Dot(mesh.Vertices[i] - pose[tip], axis));
        Assert.True(Extent(fitted) >= .0674f);
        Assert.True(Extent(fitted) > Extent(baseMesh));
    }

    [Fact]
    public void Proximal_thumb_web_does_not_inflate_the_next_thumb_joint()
    {
        var pose = WearableMeshTests.Pose(.18f);
        string middle = "Left_Thum02_JNT";
        var center = pose[middle];
        var axis = (pose["Left_Thumb03_JNT"] - pose["Left_Thumb01_JNT"]).Normalized();
        var radial = Perpendicular(axis);
        var other = Cross(axis, radial);
        var web = new List<WearableSkinSample>();
        for (int i = 0; i < 12; i++)
        {
            float angle = i * MathF.PI / 6f;
            var webCenter = pose["Left_Thumb01_JNT"] + (center - pose["Left_Thumb01_JNT"]) * .1f;
            var point = webCenter + radial * (.047f * MathF.Cos(angle)) + other * (.047f * MathF.Sin(angle));
            web.Add(new WearableSkinSample(point, new WearableSkinInfluence(middle, 1f)));
        }
        var fitted = Glove(pose, web);
        float maxRadius = Enumerable.Range(0, fitted.Vertices.Length)
            .Where(i => fitted.Bones[fitted.BoneIndex[i]] == middle)
            .Select(i =>
            {
                var delta = fitted.Vertices[i] - center;
                float along = Dot(delta, axis);
                return MathF.Abs(along) < .008f ? MathF.Sqrt(MathF.Max(0f, Dot(delta, delta) - along * along)) : 0f;
            }).Max();
        Assert.InRange(maxRadius, .015f, .025f);
    }

    [Fact]
    public void Bent_wrist_has_a_fitted_support_between_cuff_and_palm()
    {
        var pose = WearableMeshTests.Pose(.18f);
        var wrist = pose["Left_Wrist_JNT"];
        var elbow = pose["Left_Elbow_JNT"];
        var knuckles = (pose["Left_Index01_JNT"] + pose["Left_Middle01_JNT"]
            + pose["Left_Ring01_JNT"] + pose["Left_Pinky01_JNT"]) * .25f;
        var along = (knuckles - wrist).Normalized();
        var palmEnd = knuckles + along * .012f;
        var center = wrist + (palmEnd - wrist) * .08f;
        var axis = ((wrist - elbow).Normalized() + along).Normalized();
        var radial = Perpendicular(axis);
        var across = Cross(axis, radial).Normalized();
        var skin = new List<WearableSkinSample>();
        for (int i = 0; i < 8; i++)
        {
            float offset = (i - 3.5f) * .001f;
            skin.Add(new WearableSkinSample(center + radial * .048f + across * offset,
                new WearableSkinInfluence("Left_Wrist_JNT", 1f)));
        }
        var mesh = Glove(pose, skin);
        float support = Enumerable.Range(0, mesh.Vertices.Length)
            .Where(i => mesh.Bones[mesh.BoneIndex[i]] == "Left_Wrist_JNT")
            .Select(i => mesh.Vertices[i] - center)
            .Where(delta => MathF.Abs(Dot(delta, axis)) < .002f)
            .Select(delta => Dot(delta, radial))
            .DefaultIfEmpty(0f).Max();
        Assert.True(support >= .055f, $"wrist bend support {support:F4}m");
    }

    [Fact]
    public void Opposite_hand_and_torso_points_do_not_change_the_glove()
    {
        var pose = WearableMeshTests.Pose(.18f);
        var samples = new[]
        {
            new WearableSkinSample(pose["Left_Index03_JNT"], new WearableSkinInfluence("Right_Index03_JNT", 1f)),
            new WearableSkinSample(pose["Left_Wrist_JNT"], new WearableSkinInfluence("Chest_JNT", 1f)),
        };
        Assert.Equal(Glove(pose, null).Vertices, Glove(pose, samples).Vertices);
    }

    [Fact]
    public void Gold_cuff_shares_its_boundary_vertices_with_the_cloth()
    {
        var pose = WearableMeshTests.Pose(.18f);
        var mesh = WearableGraphics.Define(WearableSlot.Gloves, s =>
            s.Glove("Left_Wrist_JNT", "Left_Elbow_JNT", WearableSurface.Cloth, WearableSurface.Gold)).Build(pose);
        Assert.NotEmpty(mesh.Gold);
        var clothVertices = mesh.Cloth.ToHashSet();
        Assert.All(mesh.Gold.Distinct(), index => Assert.Contains(index, clothVertices));
        var plain = Glove(pose, null);
        Assert.Empty(plain.Gold);
        Assert.Equal(plain.Vertices, mesh.Vertices);
    }

    private static WearableMesh Glove(Dictionary<string, WearableVec3> pose, IReadOnlyList<WearableSkinSample>? samples) =>
        WearableGraphics.Define(WearableSlot.Gloves, s =>
            s.Glove("Left_Wrist_JNT", "Left_Elbow_JNT", WearableSurface.Cloth)).Build(pose, bodySamples: samples);
    private static float Dot(WearableVec3 a, WearableVec3 b) => a.X*b.X + a.Y*b.Y + a.Z*b.Z;
    private static WearableVec3 Cross(WearableVec3 a, WearableVec3 b) =>
        new(a.Y*b.Z-a.Z*b.Y, a.Z*b.X-a.X*b.Z, a.X*b.Y-a.Y*b.X);
    private static WearableVec3 Perpendicular(WearableVec3 axis)
    {
        var hint = MathF.Abs(axis.Y) < .9f ? new WearableVec3(0, 1, 0) : new WearableVec3(1, 0, 0);
        return (hint - axis * Dot(axis, hint)).Normalized();
    }
}
