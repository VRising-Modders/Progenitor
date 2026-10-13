using System;
using System.Collections.Generic;
using System.Linq;
using Progenitor.Content;
using Xunit;

namespace Progenitor.Tests;

public class WearableBodiceFitTests
{
    [Fact]
    public void Dense_measurements_fit_each_height_without_spreading_bust_volume_downward()
    {
        var samples = new List<WearableVec3>();
        for (int row = 0; row <= 52; row++)
        {
            float y = 1.04f + row * .01f;
            float frontDepth = y >= 1.43f && y <= 1.47f ? .20f : .09f;
            for (int i = 0; i < 64; i++)
            {
                float angle = i * 2f * MathF.PI / 64;
                float z = MathF.Sin(angle);
                samples.Add(new(.14f * MathF.Cos(angle), y, z * (z > 0f ? frontDepth : .09f)));
            }
        }
        var mesh = Bodice().Build(Pose(), samples);
        Assert.InRange(FrontSurface(mesh, 0f, 1.38f), .099f, .105f);
        Assert.InRange(FrontSurface(mesh, 0f, 1.45f), .209f, .215f);
        var widths = mesh.Vertices.Where(p => MathF.Abs(p.Y - 1.24f) < .00001f).Select(p => MathF.Abs(p.X));
        Assert.InRange(widths.Max(), .149f, .155f);
        var heights = mesh.Vertices.Select(p => p.Y).Distinct().OrderByDescending(y => y).ToArray();
        Assert.True(heights.Length >= 18);
        for (int i = 1; i < heights.Length; i++) Assert.True(heights[i - 1] - heights[i] <= .01501f);
    }

    [Fact]
    public void Measured_bulge_between_ring_slices_gets_front_clearance()
    {
        // 1.45 is outside both of the old +/- shoulderSpan*.13 sampling slices
        // at shoulder 1.52 and the shoulder/chest midpoint 1.38.
        var samples = new[]
        {
            new WearableVec3(-.07f, 1.45f, .28f),
            new WearableVec3(.07f, 1.45f, .28f),
            new WearableVec3(0f, 1.45f, -.13f),
        };
        var mesh = Bodice().Build(Pose(), samples);
        Assert.True(FrontSurface(mesh, -.07f, 1.45f) >= .30f, "Left clearance: " + FrontSurface(mesh, -.07f, 1.45f));
        Assert.True(FrontSurface(mesh, .07f, 1.45f) >= .30f, "Right clearance: " + FrontSurface(mesh, .07f, 1.45f));
    }

    [Fact]
    public void Upper_torso_blends_actual_ribcage_to_neck_and_lower_torso_uses_gut()
    {
        var mesh = Bodice().Build(Pose());
        int upper = Array.FindIndex(mesh.Vertices, p => MathF.Abs(p.Y - 1.52f) < .00001f);
        Assert.True(upper >= 0);
        Assert.Equal("Ribcage_JNT", mesh.Bones[mesh.BoneIndex[upper]]);
        Assert.Equal("Neck_JNT", mesh.Bones[mesh.BoneIndex2[upper]]);
        Assert.InRange(mesh.BoneWeight2[upper], .46f, .47f);
        int lower = Array.FindIndex(mesh.Vertices, p => MathF.Abs(p.Y - 1.12f) < .00001f);
        Assert.True(lower >= 0);
        Assert.Equal("Gut_JNT", mesh.Bones[mesh.BoneIndex[lower]]);
        Assert.Equal("Spine_JNT", mesh.Bones[mesh.BoneIndex2[lower]]);
        Assert.InRange(mesh.BoneWeight2[lower], .249f, .251f);
    }

    [Fact]
    public void Measured_bodice_keeps_exact_pelvis_only_skirt_seam()
    {
        var mesh = Bodice().Build(Pose(), new[] { new WearableVec3(.22f, 1.12f, .22f) });
        var waist = Enumerable.Range(0, mesh.Vertices.Length)
            .Where(i => MathF.Abs(mesh.Vertices[i].Y - 1f) < .00001f).ToArray();
        Assert.Equal(32, waist.Length);
        foreach (int i in waist)
        {
            var p = mesh.Vertices[i];
            Assert.InRange(MathF.Sqrt(p.X * p.X + p.Z * p.Z), .23399f, .23401f);
            Assert.Equal("Pelvis_JNT", mesh.Bones[mesh.BoneIndex[i]]);
            Assert.Equal(0f, mesh.BoneWeight2[i]);
        }
    }

    private static float FrontSurface(WearableMesh mesh, float x, float y)
    {
        float furthest = float.NegativeInfinity;
        for (int i = 0; i < mesh.Cloth.Length; i += 3)
        {
            var a = mesh.Vertices[mesh.Cloth[i]];
            var b = mesh.Vertices[mesh.Cloth[i + 1]];
            var c = mesh.Vertices[mesh.Cloth[i + 2]];
            float det = (b.Y - c.Y) * (a.X - c.X) + (c.X - b.X) * (a.Y - c.Y);
            if (MathF.Abs(det) < .0000001f) continue;
            float u = ((b.Y - c.Y) * (x - c.X) + (c.X - b.X) * (y - c.Y)) / det;
            float v = ((c.Y - a.Y) * (x - c.X) + (a.X - c.X) * (y - c.Y)) / det;
            float w = 1f - u - v;
            if (u < -.00001f || v < -.00001f || w < -.00001f) continue;
            furthest = MathF.Max(furthest, u * a.Z + v * b.Z + w * c.Z);
        }
        return furthest;
    }

    private static WearableGraphics Bodice() =>
        WearableGraphics.Define(WearableSlot.Chest, s => s.Bodice(WearableSurface.Cloth));

    private static Dictionary<string, WearableVec3> Pose() => new()
    {
        ["Pelvis_JNT"] = new(0f, 1f, 0f),
        ["Gut_JNT"] = new(0f, 1.10f, 0f),
        ["Spine_JNT"] = new(0f, 1.18f, 0f),
        ["Chest_JNT"] = new(0f, 1.24f, 0f),
        ["Ribcage_JNT"] = new(0f, 1.46f, 0f),
        ["Neck_JNT"] = new(0f, 1.59f, 0f),
        ["Left_Shoulder_JNT"] = new(-.18f, 1.52f, 0f),
        ["Right_Shoulder_JNT"] = new(.18f, 1.52f, 0f),
    };
}
