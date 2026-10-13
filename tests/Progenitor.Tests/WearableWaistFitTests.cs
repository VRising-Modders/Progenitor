using System;
using System.Collections.Generic;
using System.Linq;
using Progenitor.Content;
using Xunit;

namespace Progenitor.Tests;

public class WearableWaistFitTests
{
    private const float PelvisY = 1.062f;

    [Fact]
    public void Measured_asymmetric_waist_matches_bodice_skirt_and_belt_without_circular_depth()
    {
        var body = Body();
        var pose = Pose();
        var bodice = WearableGraphics.Define(WearableSlot.Chest, s => s.Bodice(WearableSurface.Cloth)).Build(pose, body);
        var skirt = WearableGraphics.Define(WearableSlot.Chest, s => s.Skirt(WearableSurface.Cloth)).Build(pose, body);
        var belt = WearableGraphics.Define(WearableSlot.Chest,
            s => s.Ring("Pelvis_JNT", WearableSketch.Fit, .02f, WearableSurface.Gold)).Build(pose, body);
        var top = skirt.Vertices.Where(p => MathF.Abs(p.Y - PelvisY) < .00001f).ToArray();
        var bottom = bodice.Vertices.Where(p => MathF.Abs(p.Y - PelvisY) < .00001f).ToArray();
        Assert.Equal(32, top.Length);
        Assert.Equal(32, bottom.Length);
        for (int i = 0; i < top.Length; i++)
        {
            Assert.Equal(top[i].X, bottom[i].X);
            Assert.Equal(top[i].Z, bottom[i].Z);
            Assert.Contains(belt.Vertices, p => p.X == top[i].X && p.Z == top[i].Z);
        }
        Assert.All(belt.Vertices, p => Assert.Contains(top, waist => waist.X == p.X && waist.Z == p.Z));
        Assert.True(top.Max(p => p.Z) < .14f);
        Assert.True(top.Min(p => p.Z) > -.15f);
        Assert.True(top.Max(p => p.X) + top.Min(p => p.X) > .02f, "Keep the measured lateral asymmetry.");
        Assert.InRange(belt.Vertices.Max(p => p.Y) - belt.Vertices.Min(p => p.Y), .02499f, .02501f);
    }

    [Fact]
    public void Hip_surface_encloses_measured_sides_between_rings_and_keeps_lower_silhouette()
    {
        var body = Body();
        var graphics = WearableGraphics.Define(WearableSlot.Chest, s => s.Skirt(WearableSurface.Cloth));
        var mesh = graphics.Build(Pose(), body);
        // Probe heights deliberately between generated 3cm rings.
        foreach (float drop in new[] { .045f, .075f, .105f, .135f, .165f, .195f, .205f, .22f })
        {
            float y = PelvisY - drop;
            float bodyEdge = .015f + HipWidth(drop);
            Assert.True(RightSurface(mesh, y, -.006f) >= bodyEdge + .018f,
                "Measured hip must retain clearance between support rings at " + y);
        }
        var baseline = graphics.Build(Pose());
        var originalLower = baseline.Vertices.Where(p => p.Y < .7f).ToArray();
        var fittedLower = mesh.Vertices.Where(p => p.Y < .7f).ToArray();
        Assert.Equal(originalLower.Length, fittedLower.Length);
        for (int i = 0; i < originalLower.Length; i++)
        {
            Assert.Equal(originalLower[i].X, fittedLower[i].X);
            Assert.Equal(originalLower[i].Y, fittedLower[i].Y);
            Assert.Equal(originalLower[i].Z, fittedLower[i].Z);
        }
    }

    [Fact]
    public void Missing_measurements_preserve_circular_fallback_and_explicit_radius_belt()
    {
        var pose = Pose();
        var measured = WearableGraphics.Define(WearableSlot.Chest,
            s => s.Ring("Pelvis_JNT", .2f, .02f, WearableSurface.Gold)).Build(pose, Body());
        var fallback = WearableGraphics.Define(WearableSlot.Chest,
            s => s.Ring("Pelvis_JNT", .2f, .02f, WearableSurface.Gold)).Build(pose);
        Assert.Equal(fallback.Vertices.Length, measured.Vertices.Length);
        for (int i = 0; i < fallback.Vertices.Length; i++)
        {
            Assert.Equal(fallback.Vertices[i].X, measured.Vertices[i].X);
            Assert.Equal(fallback.Vertices[i].Z, measured.Vertices[i].Z);
        }
    }

    private static List<WearableVec3> Body()
    {
        var points = new List<WearableVec3>();
        for (int row = 0; row <= 90; row++)
        {
            float y = .8f + row * .01f;
            float width = HipWidth(PelvisY - y);
            for (int i = 0; i < 64; i++)
            {
                float a = 2f * MathF.PI * i / 64;
                points.Add(new(.015f + width * MathF.Cos(a), y, -.006f + .10f * MathF.Sin(a)));
            }
        }
        return points;
    }

    private static float HipWidth(float drop) => .156f + .042f * MathF.Max(0f, MathF.Sin(Math.Clamp(drop / .20f, 0f, 1f) * MathF.PI));

    private static Dictionary<string, WearableVec3> Pose() => new()
    {
        ["Pelvis_JNT"] = new(0f, PelvisY, 0f),
        ["Chest_JNT"] = new(0f, 1.243f, 0f),
        ["Neck_JNT"] = new(0f, 1.593f, 0f),
        ["Left_Shoulder_JNT"] = new(-.18f, 1.458f, 0f),
        ["Right_Shoulder_JNT"] = new(.18f, 1.458f, 0f),
        ["Left_Knee_JNT"] = new(-.1f, .55f, 0f),
        ["Right_Knee_JNT"] = new(.1f, .55f, 0f),
        ["Left_Ankle_JNT"] = new(-.1f, .08f, 0f),
        ["Right_Ankle_JNT"] = new(.1f, .08f, 0f),
    };

    private static float RightSurface(WearableMesh mesh, float y, float z)
    {
        float furthest = float.NegativeInfinity;
        for (int i = 0; i < mesh.Cloth.Length; i += 3)
        {
            var a = mesh.Vertices[mesh.Cloth[i]];
            var b = mesh.Vertices[mesh.Cloth[i + 1]];
            var c = mesh.Vertices[mesh.Cloth[i + 2]];
            float det = (b.Y - c.Y) * (a.Z - c.Z) + (c.Z - b.Z) * (a.Y - c.Y);
            if (MathF.Abs(det) < .0000001f) continue;
            float u = ((b.Y - c.Y) * (z - c.Z) + (c.Z - b.Z) * (y - c.Y)) / det;
            float v = ((c.Y - a.Y) * (z - c.Z) + (a.Z - c.Z) * (y - c.Y)) / det;
            float w = 1f - u - v;
            if (u < -.00001f || v < -.00001f || w < -.00001f) continue;
            furthest = MathF.Max(furthest, u * a.X + v * b.X + w * c.X);
        }
        return furthest;
    }
}
