using System;
using System.Collections.Generic;
using System.Linq;
using Progenitor.Content;
using Xunit;

namespace Progenitor.Tests;

public class WearableSleeveTests
{
    [Theory]
    [InlineData(0f)]
    [InlineData(65f)]
    [InlineData(110f)]
    public void Continuous_sleeve_has_a_shared_elbow_and_only_an_open_wrist(float bendDegrees)
    {
        var pose = Pose(bendDegrees);
        var mesh = Build(pose);
        var edges = new Dictionary<(int A, int B), (int Count, int Direction)>();
        for (int t = 0; t < mesh.Cloth.Length; t += 3)
            for (int j = 0; j < 3; j++)
            {
                int a = mesh.Cloth[t + j], b = mesh.Cloth[t + (j + 1) % 3];
                var key = (Math.Min(a, b), Math.Max(a, b));
                edges.TryGetValue(key, out var prior);
                edges[key] = (prior.Count + 1, prior.Direction + (a < b ? 1 : -1));
            }
        Assert.All(edges.Values, e => { Assert.InRange(e.Count, 1, 2); if (e.Count == 2) Assert.Equal(0, e.Direction); });
        var wrist = pose["Left_Wrist_JNT"];
        var elbow = pose["Left_Elbow_JNT"];
        var shoulder = pose["Left_Shoulder_JNT"];
        var upper = (elbow - shoulder).Normalized();
        var lower = (wrist - elbow).Normalized();
        var boundary = edges.Where(e => e.Value.Count == 1).SelectMany(e => new[] { e.Key.A, e.Key.B }).Distinct().ToArray();
        Assert.NotEmpty(boundary);
        Assert.All(boundary, i =>
        {
            Assert.True(MathF.Abs(Dot(mesh.Vertices[i] - wrist, lower)) < 0.00001f);
            Assert.InRange((mesh.Vertices[i] - wrist).Length(), 0.04499f, 0.04501f);
        });
        var elbowPlane = (upper + lower).Normalized();
        var shared = Enumerable.Range(0, mesh.Vertices.Length).Where(i =>
            MathF.Abs(Dot(mesh.Vertices[i] - elbow, elbowPlane)) < 0.00001f &&
            MathF.Abs((mesh.Vertices[i] - elbow).Length() - 0.055f) < 0.00001f).ToArray();
        Assert.True(shared.Length >= 16, "The elbow must have one continuous circumferential contour.");
        Assert.DoesNotContain(shared, boundary.Contains);
        foreach (int i in shared)
        {
            var neighbors = edges.Keys.Where(e => e.A == i || e.B == i).Select(e => e.A == i ? e.B : e.A).ToArray();
            Assert.Contains(neighbors, j => Dot(mesh.Vertices[j] - elbow, elbowPlane) < -0.0001f);
            Assert.Contains(neighbors, j => Dot(mesh.Vertices[j] - elbow, elbowPlane) > 0.0001f);
            Assert.True(Weight(mesh, i, "Left_Elbow_JNT") > 0.999f);
        }
        Assert.Contains(mesh.Vertices, v => Dot(v - shoulder, upper) < -0.05f);
        Assert.Contains(Enumerable.Range(0, mesh.Vertices.Length), i =>
            Weight(mesh, i, "Left_Shoulder_JNT") > 0.1f && Weight(mesh, i, "Left_Elbow_JNT") > 0.1f);
        Assert.Contains(Enumerable.Range(0, mesh.Vertices.Length), i =>
            Weight(mesh, i, "Left_Elbow_JNT") > 0.1f && Weight(mesh, i, "Left_Wrist_JNT") > 0.1f);
    }

    [Fact]
    public void Sleeve_missing_wrist_reports_the_joint_and_builds_no_partial_arm()
    {
        var pose = Pose(65f);
        pose.Remove("Left_Wrist_JNT");
        var mesh = Build(pose);
        Assert.Contains("Left_Wrist_JNT", mesh.Missing);
        Assert.Empty(mesh.Vertices);
        Assert.Empty(mesh.Cloth);
    }

    private static float Weight(WearableMesh mesh, int i, string bone) =>
        (mesh.Bones[mesh.BoneIndex[i]] == bone ? 1f - mesh.BoneWeight2[i] : 0f) +
        (mesh.Bones[mesh.BoneIndex2[i]] == bone ? mesh.BoneWeight2[i] : 0f);

    private static float Dot(WearableVec3 a, WearableVec3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

    private static WearableMesh Build(Dictionary<string, WearableVec3> pose) =>
        WearableGraphics.Define(WearableSlot.Chest, s => s.Sleeve(
            "Left_Shoulder_JNT", "Left_Elbow_JNT", "Left_Wrist_JNT", WearableSurface.Cloth)).Build(pose);

    private static Dictionary<string, WearableVec3> Pose(float bendDegrees)
    {
        float angle = bendDegrees * MathF.PI / 180f;
        return new Dictionary<string, WearableVec3>
        {
            ["Left_Shoulder_JNT"] = new(-0.18f, 1.48f, 0f),
            ["Right_Shoulder_JNT"] = new(0.18f, 1.48f, 0f),
            ["Left_Elbow_JNT"] = new(-0.48f, 1.48f, 0f),
            ["Left_Wrist_JNT"] = new(-0.48f - 0.24f * MathF.Cos(angle), 1.48f - 0.24f * MathF.Sin(angle), 0f),
        };
    }
}
