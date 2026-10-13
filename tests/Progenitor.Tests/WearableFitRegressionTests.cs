using System;
using System.Collections.Generic;
using System.Linq;
using Progenitor.Content;
using Xunit;

namespace Progenitor.Tests;

// Synthetic bind-pose geometry and skinning checks; these do not qualify in-game appearance.
public class WearableFitRegressionTests
{
    [Fact]
    public void Robe_faces_point_outward_and_shared_edges_have_opposite_directions()
    {
        var pose = Pose();
        var mesh = Body(pose);
        AssertConsistentWinding(mesh);
        for (int i = 0; i < mesh.Cloth.Length; i += 3)
        {
            var a = mesh.Vertices[mesh.Cloth[i]];
            var b = mesh.Vertices[mesh.Cloth[i + 1]];
            var c = mesh.Vertices[mesh.Cloth[i + 2]];
            var center = (a + b + c) * (1f / 3f);
            // The synthetic fixture's lower-leg center shifts slightly forward.
            float legBlend = Math.Clamp((pose["Pelvis_JNT"].Y - center.Y) /
                (pose["Pelvis_JNT"].Y - pose["Left_Knee_JNT"].Y), 0f, 1f);
            var radial = new WearableVec3(center.X, 0f, center.Z - legBlend * pose["Left_Knee_JNT"].Z);
            Assert.True(Dot(Cross(b - a, c - a), radial) > 0f,
                "Every robe side face must point away from the body axis.");
        }
    }

    [Theory]
    [InlineData(0f, 0.20f)]
    [InlineData(0.20f, 0f)]
    [InlineData(-0.12f, -0.16f)]
    public void Shoe_faces_and_caps_point_outward_with_positive_signed_volume(float dx, float dz)
    {
        var pose = Pose();
        var ankle = pose["Left_Ankle_JNT"];
        pose["Left_Ball_JNT"] = new WearableVec3(ankle.X + dx, 0.035f, ankle.Z + dz);
        var mesh = Shoe(pose);
        AssertConsistentWinding(mesh);
        var interior = new WearableVec3(mesh.Vertices.Average(v => v.X),
            mesh.Vertices.Average(v => v.Y), mesh.Vertices.Average(v => v.Z));
        float volume = 0f;
        for (int i = 0; i < mesh.Cloth.Length; i += 3)
        {
            var a = mesh.Vertices[mesh.Cloth[i]];
            var b = mesh.Vertices[mesh.Cloth[i + 1]];
            var c = mesh.Vertices[mesh.Cloth[i + 2]];
            var center = (a + b + c) * (1f / 3f);
            Assert.True(Dot(Cross(b - a, c - a), center - interior) > 0f,
                "Shoe sides and end caps must face away from the interior.");
            volume += Dot(a - interior, Cross(b - interior, c - interior)) / 6f;
        }
        Assert.True(volume > 0.00001f, "A closed outward-facing shoe must enclose positive signed volume.");
    }

    [Fact]
    public void Robe_body_is_one_connected_surface_open_only_at_neck_and_hem()
    {
        var pose = Pose();
        var mesh = Body(pose);
        var edges = Edges(mesh);
        AssertConnected(mesh, edges);
        Assert.All(edges.Values, uses => Assert.InRange(uses, 1, 2));
        var boundary = edges.Where(e => e.Value == 1).Select(e => e.Key).ToArray();
        Assert.NotEmpty(boundary);
        float neck = pose["Neck_JNT"].Y, hem = pose["Left_Ankle_JNT"].Y;
        Assert.All(boundary, edge => Assert.True(
            At(mesh.Vertices[edge.A].Y, neck) && At(mesh.Vertices[edge.B].Y, neck) ||
            At(mesh.Vertices[edge.A].Y, hem) && At(mesh.Vertices[edge.B].Y, hem),
            "Only the neck opening and skirt hem may contain boundary edges."));
        Assert.Equal(2, BoundaryComponents(boundary));

        // A waist vertex must be shared by triangles above and below it. Merely
        // placing duplicate rings at the same coordinates would split when animated.
        var waist = Enumerable.Range(0, mesh.Vertices.Length)
            .Where(i => At(mesh.Vertices[i].Y, pose["Pelvis_JNT"].Y)).ToArray();
        Assert.NotEmpty(waist);
        foreach (int index in waist)
        {
            var neighbours = edges.Keys.Where(e => e.A == index || e.B == index)
                .Select(e => e.A == index ? e.B : e.A).ToArray();
            Assert.Contains(neighbours, i => mesh.Vertices[i].Y > mesh.Vertices[index].Y);
            Assert.Contains(neighbours, i => mesh.Vertices[i].Y < mesh.Vertices[index].Y);
        }
    }

    [Fact]
    public void Lower_robe_blends_both_legs_with_finite_normalized_weights()
    {
        var mesh = Body(Pose());
        Assert.Equal(mesh.Vertices.Length, mesh.BoneIndex.Length);
        Assert.Equal(mesh.Vertices.Length, mesh.BoneIndex2.Length);
        Assert.Equal(mesh.Vertices.Length, mesh.BoneWeight2.Length);
        for (int i = 0; i < mesh.Vertices.Length; i++)
        {
            Assert.InRange(mesh.BoneIndex[i], 0, mesh.Bones.Length - 1);
            Assert.InRange(mesh.BoneIndex2[i], 0, mesh.Bones.Length - 1);
            float secondary = mesh.BoneWeight2[i];
            Assert.True(float.IsFinite(secondary));
            Assert.InRange(secondary, 0f, 1f);
            Assert.Equal(1f, (1f - secondary) + secondary);
        }
        foreach (string joint in new[] { "Knee", "Ankle" })
        {
            var ring = Enumerable.Range(0, mesh.Vertices.Length).Where(i =>
                mesh.Bones[mesh.BoneIndex[i]] == "Left_" + joint + "_JNT" &&
                mesh.Bones[mesh.BoneIndex2[i]] == "Right_" + joint + "_JNT").ToArray();
            Assert.NotEmpty(ring);
            Assert.Contains(ring, i => mesh.BoneWeight2[i] < 0.01f);
            Assert.Contains(ring, i => mesh.BoneWeight2[i] > 0.99f);
            Assert.Contains(ring, i => mesh.BoneWeight2[i] > 0.25f && mesh.BoneWeight2[i] < 0.75f);
        }
    }

    [Fact]
    public void Shared_seams_remain_shared_when_bones_rotate_and_translate()
    {
        var pose = Pose();
        var mesh = Body(pose);
        var deformed = new WearableVec3[mesh.Vertices.Length];
        for (int i = 0; i < deformed.Length; i++)
        {
            var a = Transform(mesh.Vertices[i], mesh.Bones[mesh.BoneIndex[i]], pose);
            var b = Transform(mesh.Vertices[i], mesh.Bones[mesh.BoneIndex2[i]], pose);
            deformed[i] = a * (1f - mesh.BoneWeight2[i]) + b * mesh.BoneWeight2[i];
            Assert.True(float.IsFinite(deformed[i].X) && float.IsFinite(deformed[i].Y) && float.IsFinite(deformed[i].Z));
        }
        Assert.Contains(Enumerable.Range(0, deformed.Length), i => (deformed[i] - mesh.Vertices[i]).Length() > 0.2f);
        // Reject coincident vertices carrying incompatible skinning at the torso seams.
        foreach (float y in new[] { pose["Chest_JNT"].Y, pose["Pelvis_JNT"].Y })
        {
            var seam = Enumerable.Range(0, mesh.Vertices.Length).Where(i => At(mesh.Vertices[i].Y, y)).ToArray();
            Assert.NotEmpty(seam);
            foreach (int i in seam)
                foreach (int j in seam.Where(j => j > i))
                    if ((mesh.Vertices[i] - mesh.Vertices[j]).Length() < 0.00001f)
                        Assert.True((deformed[i] - deformed[j]).Length() < 0.00001f);
            var above = new HashSet<int>();
            var below = new HashSet<int>();
            for (int t = 0; t < mesh.Cloth.Length; t += 3)
            {
                var triangle = mesh.Cloth.Skip(t).Take(3).ToArray();
                foreach (int i in triangle.Where(seam.Contains))
                {
                    if (triangle.Any(j => mesh.Vertices[j].Y > y + 0.00001f)) above.Add(i);
                    if (triangle.Any(j => mesh.Vertices[j].Y < y - 0.00001f)) below.Add(i);
                }
            }
            Assert.True(above.SetEquals(seam) && below.SetEquals(seam), "Both sides must use the same deformed seam vertices.");
        }
    }

    [Theory]
    [InlineData(0f, 0.20f)]
    [InlineData(0.20f, 0f)]
    [InlineData(-0.12f, -0.16f)]
    public void Shoe_is_closed_and_extends_toward_the_ball_even_when_foot_turns(float dx, float dz)
    {
        var pose = Pose();
        var ankle = pose["Left_Ankle_JNT"];
        var ball = new WearableVec3(ankle.X + dx, 0.035f, ankle.Z + dz);
        pose["Left_Ball_JNT"] = ball;
        var mesh = Shoe(pose);
        var edges = Edges(mesh);
        AssertConnected(mesh, edges);
        Assert.All(edges.Values, uses => Assert.Equal(2, uses));
        var direction = new WearableVec3(dx, 0f, dz).Normalized();
        float alongBall = Dot(ball - ankle, direction);
        Assert.True(mesh.Vertices.Max(v => Dot(v - ankle, direction)) > alongBall,
            "The toe must extend beyond the ball in that foot's direction.");
        float sole = mesh.Vertices.Min(v => v.Y);
        Assert.InRange(sole, ball.Y - 0.04f, ball.Y);
        Assert.True(sole > ankle.Y - 0.15f, "A shoe must not continue downward along the shin.");
    }

    [Fact]
    public void Shoe_missing_ball_reports_it_without_drawing_a_guessed_foot()
    {
        var pose = Pose();
        pose.Remove("Left_Ball_JNT");
        var mesh = Shoe(pose);
        Assert.Contains("Left_Ball_JNT", mesh.Missing);
        Assert.Empty(mesh.Vertices);
        Assert.Empty(mesh.Cloth);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Loft_far_ring_honours_follow_end_skinning(bool followEnd)
    {
        var pose = Pose();
        var mesh = WearableGraphics.Define(WearableSlot.Chest, s => s.Loft(
            "Neck_JNT", "Chest_JNT", 0.1f, 0.2f, WearableSurface.Cloth, followEnd)).Build(pose);
        var far = Enumerable.Range(0, mesh.Vertices.Length).Where(i => At(mesh.Vertices[i].Y, pose["Chest_JNT"].Y)).ToArray();
        Assert.NotEmpty(far);
        Assert.All(far, i =>
        {
            Assert.Equal(followEnd ? "Chest_JNT" : "Neck_JNT", mesh.Bones[mesh.BoneIndex[i]]);
            Assert.Equal(0f, mesh.BoneWeight2[i]);
        });
    }

    [Fact]
    public void Adjacent_fitted_torso_lofts_meet_at_the_same_chest_radius()
    {
        var pose = Pose();
        WearableMesh Loft(string start, string end) => WearableGraphics.Define(WearableSlot.Chest,
            s => s.Loft(start, end, WearableSketch.Fit, WearableSketch.Fit, WearableSurface.Cloth)).Build(pose);
        var upper = Loft("Neck_JNT", "Chest_JNT");
        var lower = Loft("Chest_JNT", "Pelvis_JNT");
        float chest = pose["Chest_JNT"].Y;
        var a = upper.Vertices.Where(v => At(v.Y, chest)).ToArray();
        var b = lower.Vertices.Where(v => At(v.Y, chest)).ToArray();
        Assert.NotEmpty(a);
        Assert.Equal(a.Length, b.Length);
        Assert.All(a, v => Assert.Contains(b, other => (v - other).Length() < 0.00001f));
    }

    private static WearableMesh Body(Dictionary<string, WearableVec3> pose) =>
        WearableGraphics.Define(WearableSlot.Chest, s => s.RobeBody(WearableSurface.Cloth)).Build(pose);

    private static WearableMesh Shoe(Dictionary<string, WearableVec3> pose) =>
        WearableGraphics.Define(WearableSlot.Footgear, s => s.Shoe("Left_Ankle_JNT", "Left_Ball_JNT", 0.065f, WearableSurface.Cloth)).Build(pose);

    private static bool At(float a, float b) => MathF.Abs(a - b) < 0.00001f;
    private static float Dot(WearableVec3 a, WearableVec3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

    private static WearableVec3 Cross(WearableVec3 a, WearableVec3 b) =>
        new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

    private static void AssertConsistentWinding(WearableMesh mesh)
    {
        var counts = Edges(mesh);
        var directions = new Dictionary<(int A, int B), int>();
        for (int i = 0; i < mesh.Cloth.Length; i += 3)
            for (int j = 0; j < 3; j++)
            {
                int a = mesh.Cloth[i + j], b = mesh.Cloth[i + (j + 1) % 3];
                var key = (Math.Min(a, b), Math.Max(a, b));
                directions.TryGetValue(key, out int sum);
                directions[key] = sum + (a < b ? 1 : -1);
            }
        foreach (var edge in counts.Where(e => e.Value == 2))
            Assert.True(directions[edge.Key] == 0,
                "Two faces sharing an interior edge must traverse it in opposite directions.");
    }

    private static Dictionary<(int A, int B), int> Edges(WearableMesh mesh)
    {
        Assert.NotEmpty(mesh.Cloth);
        Assert.Equal(0, mesh.Cloth.Length % 3);
        var edges = new Dictionary<(int A, int B), int>();
        for (int i = 0; i < mesh.Cloth.Length; i += 3)
            for (int j = 0; j < 3; j++)
            {
                int a = mesh.Cloth[i + j], b = mesh.Cloth[i + (j + 1) % 3];
                Assert.InRange(a, 0, mesh.Vertices.Length - 1);
                Assert.NotEqual(a, b);
                var key = (Math.Min(a, b), Math.Max(a, b));
                edges.TryGetValue(key, out int n);
                edges[key] = n + 1;
            }
        return edges;
    }

    private static void AssertConnected(WearableMesh mesh, Dictionary<(int A, int B), int> edges)
    {
        Assert.Equal(1, BoundaryComponents(edges.Keys.ToArray()));
        Assert.Equal(mesh.Vertices.Length, edges.Keys.SelectMany(e => new[] { e.A, e.B }).Distinct().Count());
    }

    private static int BoundaryComponents((int A, int B)[] edges)
    {
        var remaining = edges.SelectMany(e => new[] { e.A, e.B }).ToHashSet();
        int components = 0;
        while (remaining.Count > 0)
        {
            components++;
            var queue = new Queue<int>();
            queue.Enqueue(remaining.First());
            remaining.Remove(queue.Peek());
            while (queue.Count > 0)
            {
                int i = queue.Dequeue();
                foreach (var edge in edges.Where(e => e.A == i || e.B == i))
                {
                    int other = edge.A == i ? edge.B : edge.A;
                    if (remaining.Remove(other)) queue.Enqueue(other);
                }
            }
        }
        return components;
    }

    private static WearableVec3 Transform(WearableVec3 vertex, string bone, Dictionary<string, WearableVec3> pose)
    {
        float angle = bone.StartsWith("Left_", StringComparison.Ordinal) ? 0.8f :
            bone.StartsWith("Right_", StringComparison.Ordinal) ? -0.35f : 0.2f;
        var local = vertex - pose[bone];
        var rotated = new WearableVec3(local.X, local.Y * MathF.Cos(angle) - local.Z * MathF.Sin(angle),
            local.Y * MathF.Sin(angle) + local.Z * MathF.Cos(angle));
        return pose[bone] + rotated + new WearableVec3(0f, 0.05f, angle * 0.3f);
    }

    private static Dictionary<string, WearableVec3> Pose() => new(StringComparer.Ordinal)
    {
        ["Neck_JNT"] = new(0f, 1.55f, 0f),
        ["Chest_JNT"] = new(0f, 1.42f, 0f),
        ["Pelvis_JNT"] = new(0f, 1f, 0f),
        ["Left_Shoulder_JNT"] = new(-0.18f, 1.48f, 0f),
        ["Right_Shoulder_JNT"] = new(0.18f, 1.48f, 0f),
        ["Left_Knee_JNT"] = new(-0.10f, 0.52f, 0.02f),
        ["Right_Knee_JNT"] = new(0.10f, 0.52f, 0.02f),
        ["Left_Ankle_JNT"] = new(-0.10f, 0.08f, 0.02f),
        ["Right_Ankle_JNT"] = new(0.10f, 0.08f, 0.02f),
        ["Left_Ball_JNT"] = new(-0.10f, 0.035f, 0.22f),
        ["Right_Ball_JNT"] = new(0.10f, 0.035f, 0.22f),
    };
}
