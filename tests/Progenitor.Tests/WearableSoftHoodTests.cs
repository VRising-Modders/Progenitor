using System;
using System.Linq;
using Progenitor.Content;
using Xunit;

namespace Progenitor.Tests;

public class WearableSoftHoodTests
{
    [Fact]
    public void Soft_hood_has_a_low_brow_and_long_side_drapes_without_a_round_face_ring()
    {
        var pose = WearableMeshTests.Pose(.18f);
        var head = pose["Head_JNT"];
        var mesh = WearableLooks.RobeHood().Build(pose);
        var rim = mesh.Cloth.Intersect(mesh.Void).Select(i => mesh.Vertices[i]).ToArray();
        Assert.True(rim.Length >= 48, "The soft edge needs enough samples to avoid a polygonal silhouette.");
        float width = rim.Max(v => v.X) - rim.Min(v => v.X);
        float height = rim.Max(v => v.Y) - rim.Min(v => v.Y);
        Assert.True(height > 1.9f * width, "Keep the face opening elongated, not circular.");
        var brow = rim.Where(v => v.Y > head.Y + .1f).ToArray();
        Assert.True(brow.Min(v => v.Y) < brow.Max(v => v.Y) - .006f,
            "A hanging brow should dip rather than form a rigid horizontal visor.");
        var cloth = mesh.Cloth.Distinct().Select(i => mesh.Vertices[i]).ToArray();
        Assert.True(cloth.Any(v => v.X < head.X - .06f && v.Y < head.Y - .25f));
        Assert.True(cloth.Any(v => v.X > head.X + .06f && v.Y < head.Y - .24f));
        Assert.Equal(new[] { "Head_JNT" }, mesh.Bones);
        Assert.All(mesh.BoneIndex, index => Assert.Equal(0, index));
    }

    [Fact]
    public void Curved_hood_cloth_and_black_interior_have_finite_non_degenerate_triangles()
    {
        var mesh = WearableLooks.RobeHood().Build(WearableMeshTests.Pose(.18f));
        Assert.Empty(mesh.Missing);
        Assert.All(mesh.Vertices, v => Assert.True(float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z)));
        foreach (var triangles in new[] { mesh.Cloth, mesh.Void })
        {
            Assert.NotEmpty(triangles);
            Assert.Equal(0, triangles.Length % 3);
            for (int i = 0; i < triangles.Length; i += 3)
            {
                var a = mesh.Vertices[triangles[i]];
                var u = mesh.Vertices[triangles[i + 1]] - a;
                var v = mesh.Vertices[triangles[i + 2]] - a;
                float x = u.Y * v.Z - u.Z * v.Y;
                float y = u.Z * v.X - u.X * v.Z;
                float z = u.X * v.Y - u.Y * v.X;
                Assert.True(x * x + y * y + z * z > 1e-14f, "Collapsed hood triangle at " + i);
            }
        }
    }
}
