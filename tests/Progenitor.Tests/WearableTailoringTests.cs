using System;
using System.Collections.Generic;
using System.Linq;
using Progenitor.Content;
using Xunit;

namespace Progenitor.Tests;

// Original garment geometry checked against synthetic fitting references, not game screenshots.
public class WearableTailoringTests
{
    [Fact]
    public void Hood_is_watertight_with_a_connected_black_recess_behind_the_cloth_rim()
    {
        var pose = WearableMeshTests.Pose(0.18f);
        var mesh = WearableLooks.RobeHood().Build(pose);
        var edges = Edges(mesh);
        Assert.All(edges.Values, e => { Assert.Equal(2, e.Count); Assert.Equal(0, e.Direction); });
        var cloth = mesh.Cloth.ToHashSet();
        var face = mesh.Void.ToHashSet();
        var rim = cloth.Intersect(face).ToArray();
        Assert.True(rim.Length >= 16, "The interior must share an entire opening with the cloth.");
        Assert.True(face.Except(cloth).All(i => mesh.Vertices[i].Z < rim.Min(j => mesh.Vertices[j].Z)),
            "The black interior must recede behind the joined opening.");
        Assert.True(cloth.Max(i => mesh.Vertices[i].Z) > face.Max(i => mesh.Vertices[i].Z) + 0.015f);
        Assert.True(face.Min(i => mesh.Vertices[i].Z) > pose["Head_JNT"].Z + 0.08f,
            "The opaque interior must remain in front of the synthetic face.");
        Assert.InRange(mesh.Vertices.Max(v => v.Y) - pose["Head_JNT"].Y, 0.24f, 0.26f);
        Assert.InRange(mesh.Vertices.Min(v => v.Y) - pose["Head_JNT"].Y, -0.29f, -0.26f);
        float width = mesh.Vertices.Max(v => v.X) - mesh.Vertices.Min(v => v.X);
        float height = mesh.Vertices.Max(v => v.Y) - mesh.Vertices.Min(v => v.Y);
        Assert.InRange(width, 0.27f, 0.30f);
        Assert.True(height > width * 1.6f, "The hood silhouette must remain elongated rather than circular.");
        float faceWidth = rim.Max(i => mesh.Vertices[i].X) - rim.Min(i => mesh.Vertices[i].X);
        float faceHeight = rim.Max(i => mesh.Vertices[i].Y) - rim.Min(i => mesh.Vertices[i].Y);
        Assert.True(faceHeight > faceWidth * 1.7f, "The opening should be a tall narrow arch.");
        float crownY = mesh.Vertices.Max(v => v.Y);
        var crown = mesh.Vertices.Where(v => v.Y > crownY - .02f).ToArray();
        Assert.True(crown.Max(v => v.X) - crown.Min(v => v.X) > .08f,
            "The high crown should spread smoothly across the head rather than form a point.");
    }

    [Fact]
    public void Skirt_has_only_waist_and_hem_openings_with_a_sewn_gold_edge_and_valid_leg_weights()
    {
        var pose = WearableMeshTests.Pose(0.18f);
        var mesh = Skirt(pose);
        var edges = Edges(mesh);
        float top = mesh.Vertices.Max(v => v.Y), bottom = mesh.Vertices.Min(v => v.Y);
        var boundary = edges.Where(e => e.Value.Count == 1).Select(e => e.Key).ToArray();
        Assert.NotEmpty(boundary);
        Assert.All(edges.Values, e => { Assert.InRange(e.Count, 1, 2); if (e.Count == 2) Assert.Equal(0, e.Direction); });
        Assert.All(boundary, e => Assert.True(
            Near(mesh.Vertices[e.A].Y, top) && Near(mesh.Vertices[e.B].Y, top) ||
            Near(mesh.Vertices[e.A].Y, bottom) && Near(mesh.Vertices[e.B].Y, bottom)));
        Assert.Contains(boundary, e => Near(mesh.Vertices[e.A].Y, top));
        Assert.Contains(boundary, e => Near(mesh.Vertices[e.A].Y, bottom));
        Assert.NotEmpty(mesh.Gold);
        Assert.NotEmpty(mesh.Cloth.Intersect(mesh.Gold));
        Assert.All(mesh.Gold, i => Assert.InRange(mesh.Vertices[i].Y, bottom, bottom + 0.025f));
        Assert.Contains(Enumerable.Range(0, mesh.Vertices.Length), i => mesh.BoneWeight2[i] > 0.1f && mesh.BoneWeight2[i] < 0.9f);
        for (int i = 0; i < mesh.Vertices.Length; i++)
        {
            Assert.True(float.IsFinite(mesh.BoneWeight2[i]));
            Assert.InRange(mesh.BoneWeight2[i], 0f, 1f);
            Assert.InRange(mesh.BoneIndex[i], 0, mesh.Bones.Length - 1);
            Assert.InRange(mesh.BoneIndex2[i], 0, mesh.Bones.Length - 1);
        }
        Assert.Contains("Left_Knee_JNT", mesh.Bones);
        Assert.Contains("Right_Knee_JNT", mesh.Bones);
        Assert.Contains("Left_Ankle_JNT", mesh.Bones);
        Assert.Contains("Right_Ankle_JNT", mesh.Bones);
    }

    [Fact]
    public void Bodice_clears_an_offset_torso_reference_and_meets_skirt_with_matching_skinning()
    {
        var pose = WearableMeshTests.Pose(0.18f);
        float chestY = pose["Chest_JNT"].Y;
        var torso = new List<WearableVec3>();
        foreach (float x in new[] { -0.25f, 0.25f })
            foreach (float z in new[] { -0.19f, 0.19f })
                torso.Add(new WearableVec3(x + 0.025f, chestY, z + 0.04f));
        var bodice = WearableGraphics.Define(WearableSlot.Chest, s => s.Bodice(WearableSurface.Cloth)).Build(pose, torso);
        var chest = bodice.Vertices.Where(v => Near(v.Y, chestY)).ToArray();
        Assert.True(chest.Length >= 16);
        foreach (var point in torso)
        {
            Assert.True(Inside(point, chest), "The fitted chest contour must enclose every torso reference point.");
            Assert.True(Enumerable.Range(0, chest.Length).Min(i => SegmentDistance(point, chest[i], chest[(i + 1) % chest.Length])) > 0.015f,
                "The contour must include clearance, not merely touch the reference skin.");
        }
        var skirt = Skirt(pose);
        var bodiceWaist = Enumerable.Range(0, bodice.Vertices.Length).Where(i => Near(bodice.Vertices[i].Y, pose["Pelvis_JNT"].Y)).ToArray();
        var skirtWaist = Enumerable.Range(0, skirt.Vertices.Length).Where(i => Near(skirt.Vertices[i].Y, pose["Pelvis_JNT"].Y)).ToArray();
        Assert.NotEmpty(bodiceWaist);
        Assert.Equal(bodiceWaist.Length, skirtWaist.Length);
        foreach (int i in bodiceWaist)
        {
            int other = Assert.Single(skirtWaist.Where(j => (bodice.Vertices[i] - skirt.Vertices[j]).Length() < 0.00001f));
            Assert.Equal("Pelvis_JNT", bodice.Bones[bodice.BoneIndex[i]]);
            Assert.Equal("Pelvis_JNT", skirt.Bones[skirt.BoneIndex[other]]);
            Assert.Equal(0f, bodice.BoneWeight2[i]);
            Assert.Equal(0f, skirt.BoneWeight2[other]);
        }
    }

    [Theory]
    [InlineData("Left_")]
    [InlineData("Right_")]
    public void Glove_covers_all_five_fingertips_and_accepts_the_rigs_thumb_spelling(string side)
    {
        var pose = WearableMeshTests.Pose(0.18f);
        var mesh = Glove(pose, side);
        Assert.Empty(mesh.Missing);
        Assert.Contains(side + "Thum02_JNT", mesh.Bones);
        var edges = Edges(mesh);
        Assert.All(edges.Values, e => { Assert.InRange(e.Count, 1, 2); if (e.Count == 2) Assert.Equal(0, e.Direction); });
        var boundary = edges.Where(e => e.Value.Count == 1).SelectMany(e => new[] { e.Key.A, e.Key.B }).ToHashSet();
        foreach (string finger in new[] { "Index", "Middle", "Ring", "Pinky", "Thumb" })
        {
            string tipName = side + finger + "03_JNT";
            string middleName = side + (finger == "Thumb" ? "Thum" : finger) + "02_JNT";
            var direction = (pose[tipName] - pose[middleName]).Normalized();
            var tipVertices = Enumerable.Range(0, mesh.Vertices.Length).Where(i => mesh.Bones[mesh.BoneIndex[i]] == tipName).ToArray();
            Assert.NotEmpty(tipVertices);
            Assert.DoesNotContain(tipVertices, boundary.Contains);
            Assert.True(tipVertices.Max(i => Dot(mesh.Vertices[i] - pose[tipName], direction)) > 0.01f,
                "The closed fingertip must extend beyond its last joint.");
            Assert.Contains(side + finger + "01_JNT", mesh.Bones);
            Assert.Contains(middleName, mesh.Bones);
        }
    }

    [Fact]
    public void Missing_finger_drops_the_glove_and_reports_the_specific_joint()
    {
        var pose = WearableMeshTests.Pose(0.18f);
        pose.Remove("Left_Ring02_JNT");
        var mesh = Glove(pose, "Left_");
        Assert.Contains("Left_Ring02_JNT", mesh.Missing);
        Assert.Empty(mesh.Vertices);
        Assert.Empty(mesh.Cloth);
    }

    private static WearableMesh Skirt(Dictionary<string, WearableVec3> pose) =>
        WearableGraphics.Define(WearableSlot.Chest, s => s.Skirt(WearableSurface.Cloth)).Build(pose);
    private static WearableMesh Glove(Dictionary<string, WearableVec3> pose, string side) =>
        WearableGraphics.Define(WearableSlot.Gloves, s => s.Glove(side + "Wrist_JNT", side + "Elbow_JNT", WearableSurface.Cloth)).Build(pose);
    private static bool Near(float a, float b) => MathF.Abs(a - b) < 0.00001f;
    private static float Dot(WearableVec3 a, WearableVec3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

    private static Dictionary<(int A, int B), (int Count, int Direction)> Edges(WearableMesh mesh)
    {
        var triangles = mesh.Cloth.Concat(mesh.Gold).Concat(mesh.Void).ToArray();
        Assert.NotEmpty(triangles);
        var edges = new Dictionary<(int A, int B), (int Count, int Direction)>();
        for (int t = 0; t < triangles.Length; t += 3)
            for (int j = 0; j < 3; j++)
            {
                int a = triangles[t + j], b = triangles[t + (j + 1) % 3];
                Assert.NotEqual(a, b);
                var key = (Math.Min(a, b), Math.Max(a, b));
                edges.TryGetValue(key, out var prior);
                edges[key] = (prior.Count + 1, prior.Direction + (a < b ? 1 : -1));
            }
        return edges;
    }

    private static bool Inside(WearableVec3 p, WearableVec3[] polygon)
    {
        bool inside = false;
        for (int i = 0, j = polygon.Length - 1; i < polygon.Length; j = i++)
        {
            var a = polygon[i]; var b = polygon[j];
            if ((a.Z > p.Z) != (b.Z > p.Z) && p.X < (b.X - a.X) * (p.Z - a.Z) / (b.Z - a.Z) + a.X)
                inside = !inside;
        }
        return inside;
    }

    private static float SegmentDistance(WearableVec3 p, WearableVec3 a, WearableVec3 b)
    {
        var delta = b - a;
        float t = Math.Clamp(Dot(p - a, delta) / Dot(delta, delta), 0f, 1f);
        return (p - (a + delta * t)).Length();
    }
}
