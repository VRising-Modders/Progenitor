using System;
using System.Collections.Generic;
using System.Linq;
using Progenitor.Content;
using Xunit;

namespace Progenitor.Tests;

public class WearableTasselInlayTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Material_partition_has_no_referenced_vertex_inside_an_unsplit_edge(bool includeBodice)
    {
        var mesh = Skirt(true, includeBodice).Build(Pose());
        var triangles = mesh.Cloth.Concat(mesh.Gold).ToArray();
        var vertices = triangles.Distinct().ToArray();
        const float tolerance = .0000005f;
        for (int triangle = 0; triangle < triangles.Length; triangle += 3)
        {
            for (int edge = 0; edge < 3; edge++)
            {
                int a = triangles[triangle + edge], b = triangles[triangle + (edge + 1) % 3];
                var delta = mesh.Vertices[b] - mesh.Vertices[a];
                float lengthSquared = Dot(delta, delta);
                if (lengthSquared <= tolerance * tolerance) continue;
                float endTolerance = tolerance / MathF.Sqrt(lengthSquared);
                foreach (int vertex in vertices)
                {
                    var d = mesh.Vertices[vertex] - mesh.Vertices[a];
                    float t = Dot(d, delta) / lengthSquared;
                    if (t <= endTolerance || t >= 1f - endTolerance) continue;
                    var error = d - delta * t;
                    Assert.True(Dot(error, error) > tolerance * tolerance,
                        $"T-junction: vertex {vertex} lies inside edge {a}->{b} at {t}.");
                }
            }
        }
    }

    [Fact]
    public void Fitted_ribbons_partition_existing_skirt_without_adding_surface_area()
    {
        var baseline = Skirt(false).Build(Pose());
        var decorated = Skirt(true).Build(Pose());
        double originalArea = Area(baseline, baseline.Cloth) + Area(baseline, baseline.Gold);
        double decoratedArea = Area(decorated, decorated.Cloth) + Area(decorated, decorated.Gold);
        Assert.InRange(Math.Abs(originalArea - decoratedArea), 0d, .000001d);
        Assert.True(Area(decorated, decorated.Gold) > Area(baseline, baseline.Gold));
        Assert.True(Area(decorated, decorated.Cloth) < Area(baseline, baseline.Cloth));
    }

    [Fact]
    public void Every_ribbon_triangle_is_coplanar_with_original_skirt_and_preserves_winding()
    {
        var baseline = Skirt(false).Build(Pose());
        var decorated = Skirt(true).Build(Pose());
        int ribbons = 0;
        for (int i = 0; i < decorated.Gold.Length; i += 3)
        {
            var a = decorated.Vertices[decorated.Gold[i]];
            var b = decorated.Vertices[decorated.Gold[i + 1]];
            var c = decorated.Vertices[decorated.Gold[i + 2]];
            if (MathF.Max(a.Y, MathF.Max(b.Y, c.Y)) < .7f) continue; // Existing hem.
            ribbons++;
            bool matches = false;
            for (int j = 0; j < baseline.Cloth.Length && !matches; j += 3)
            {
                var x = baseline.Vertices[baseline.Cloth[j]];
                var y = baseline.Vertices[baseline.Cloth[j + 1]];
                var z = baseline.Vertices[baseline.Cloth[j + 2]];
                if (!OnTriangle(a, x, y, z) || !OnTriangle(b, x, y, z) || !OnTriangle(c, x, y, z)) continue;
                Assert.True(Dot(Cross(b - a, c - a), Cross(y - x, z - x)) > 0f);
                matches = true;
            }
            Assert.True(matches, "Ribbon must occupy a source skirt triangle plane without any floating offset.");
        }
        Assert.True(ribbons >= 10);
    }

    [Fact]
    public void Ribbon_vertices_stay_in_five_narrow_tapered_front_regions_with_valid_weights()
    {
        var mesh = Skirt(true).Build(Pose());
        var ribbons = mesh.Gold.Where(i => mesh.Vertices[i].Y >= .7f).Distinct().ToArray();
        Assert.NotEmpty(ribbons);
        foreach (int i in ribbons)
        {
            var p = mesh.Vertices[i];
            Assert.InRange(p.Y, .75999f, 1.00001f);
            Assert.True(p.Z >= -.00001f);
            float halfWidth = .004f - .002f * (1f - p.Y) / .24f;
            Assert.True(Enumerable.Range(0, 5).Any(strip =>
                MathF.Abs(p.X - (strip * .5f - 1f) * .36f * .4f) <= halfWidth + .00001f));
            Assert.InRange(mesh.BoneWeight2[i], 0f, 1f);
            Assert.InRange(mesh.BoneIndex[i], 0, mesh.Bones.Length - 1);
            Assert.InRange(mesh.BoneIndex2[i], 0, mesh.Bones.Length - 1);
        }
        // Material boundaries reuse vertex indices: the gold cannot move independently.
        Assert.NotEmpty(ribbons.Intersect(mesh.Cloth));
    }

    [Fact]
    public void Explicit_radius_and_staff_tassels_keep_the_original_primitive_path()
    {
        var standalone = WearableGraphics.Define(WearableSlot.Chest,
            s => s.Tassels("Pelvis_JNT", .2f, 5, .24f, WearableSurface.Gold)).Build(Pose());
        Assert.Equal(20, standalone.Vertices.Length);
        Assert.Equal(30, standalone.Gold.Length);
        var staff = WearableLooks.Staff().Build(Pose());
        Assert.Empty(staff.Missing);
        Assert.True(staff.Gold.Length > 0);
    }

    private static WearableGraphics Skirt(bool decorated, bool includeBodice = false) => WearableGraphics.Define(WearableSlot.Chest, s =>
    {
        if (includeBodice) s.Bodice(WearableSurface.Cloth);
        s.Skirt(WearableSurface.Cloth);
        if (decorated) s.Tassels("Pelvis_JNT", WearableSketch.Fit, 5, .24f, WearableSurface.Gold);
    });

    private static Dictionary<string, WearableVec3> Pose() => new()
    {
        ["Pelvis_JNT"] = new(0f, 1f, 0f),
        ["Chest_JNT"] = new(0f, 1.25f, 0f),
        ["Neck_JNT"] = new(0f, 1.58f, 0f),
        ["Left_Knee_JNT"] = new(-.1f, .55f, .015f),
        ["Right_Knee_JNT"] = new(.1f, .55f, .015f),
        ["Left_Ankle_JNT"] = new(-.1f, .08f, 0f),
        ["Right_Ankle_JNT"] = new(.1f, .08f, 0f),
        ["Left_Shoulder_JNT"] = new(-.18f, 1.45f, 0f),
        ["Right_Shoulder_JNT"] = new(.18f, 1.45f, 0f),
        ["Right_Wrist_JNT"] = new(.45f, 1.1f, 0f),
    };

    private static double Area(WearableMesh mesh, int[] indices)
    {
        double total = 0;
        for (int i = 0; i < indices.Length; i += 3)
            total += Cross(mesh.Vertices[indices[i + 1]] - mesh.Vertices[indices[i]],
                mesh.Vertices[indices[i + 2]] - mesh.Vertices[indices[i]]).Length() * .5d;
        return total;
    }

    private static bool OnTriangle(WearableVec3 p, WearableVec3 a, WearableVec3 b, WearableVec3 c)
    {
        var x = b - a; var y = c - a; var d = p - a;
        var normal = Cross(x, y);
        if (MathF.Abs(Dot(d, normal)) > .000001f * normal.Length()) return false;
        float xx = Dot(x, x), xy = Dot(x, y), yy = Dot(y, y), dx = Dot(d, x), dy = Dot(d, y);
        float determinant = xx * yy - xy * xy;
        float u = (yy * dx - xy * dy) / determinant;
        float v = (xx * dy - xy * dx) / determinant;
        return u >= -.0001f && v >= -.0001f && u + v <= 1.0001f;
    }

    private static WearableVec3 Cross(WearableVec3 a, WearableVec3 b) => new(
        a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
    private static float Dot(WearableVec3 a, WearableVec3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
}
