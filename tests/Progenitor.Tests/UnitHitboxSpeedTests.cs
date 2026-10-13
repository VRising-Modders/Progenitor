using System;
using System.Buffers.Binary;
using System.Linq;
using Progenitor;
using Progenitor.Units;
using Xunit;
using Vec3 = Progenitor.Units.UnitHitboxCodec.Vec3;

namespace Progenitor.Tests;

/// <summary>
/// ctx.Units hitbox scale and move speed, game-free half (<see cref="UnitHitboxCodec"/>,
/// <see cref="UnitMoveSpeedMath"/>). The synthetic blob reproduces Frostmaw's 928-byte compound
/// collider as read live on 2026-09-25:
/// a body capsule (filter 16384), a Hittable capsule (filter 2179080, radius 1 over y 1..3) and a
/// 5 m sphere (filter 1024); BVH node 0 empty, node 1 the root, node 2 the leaf.
/// </summary>
public class UnitHitboxSpeedTests
{
    private const uint BodyFilter = 16384;
    private const uint HittableFilter = 2179080;
    private const uint SensorFilter = 1024;

    // ---- a synthetic compound blob ------------------------------------------------------------

    private sealed class ChildSpec
    {
        public int Type = Layout.ColliderTypeCapsule;
        public uint Filter;
        public float Radius;
        public Vec3[] Vertices = Array.Empty<Vec3>();
        public Vec3 Position;
        public float[] Rotation = { 0, 0, 0, 1 };
        public bool HullMirrors;         // hull array elsewhere, same values
        public Vec3[]? HullOverride;     // hull array elsewhere, these values
    }

    private sealed class Blob
    {
        public byte[] Data = Array.Empty<byte>();
        public int ChildrenAt;
        public int NodesAt;
        public int[] ColliderAt = Array.Empty<int>();
        public int[] HullAt = Array.Empty<int>();
    }

    private static ChildSpec Capsule(uint filter, float radius, Vec3 a, Vec3 b) =>
        new() { Type = Layout.ColliderTypeCapsule, Filter = filter, Radius = radius, Vertices = new[] { a, b } };

    private static ChildSpec Sphere(uint filter, float radius, Vec3 c) =>
        new() { Type = Layout.ColliderTypeSphere, Filter = filter, Radius = radius, Vertices = new[] { c } };

    private static ChildSpec[] Frostmaw() => new[]
    {
        Capsule(BodyFilter, 0.7f, new Vec3(0, 0.6f, 0), new Vec3(0, 1.4f, 0)),
        Capsule(HittableFilter, 1f, new Vec3(0, 1, 0), new Vec3(0, 3, 0)),
        Sphere(SensorFilter, 5f, new Vec3(0, 0, 0)),
    };

    /// <summary>
    /// Lay the compound out the way the live one is: 0x60 header, children, 16-aligned 0x80 BVH
    /// nodes, then each child collider 16-aligned. Frostmaw's three children come to 928 bytes.
    /// </summary>
    private static Blob Build(ChildSpec[] children, bool leafHoldsAll = true)
    {
        const int header = 0x60;
        int childrenAt = header;
        int nodesAt = Align(childrenAt + children.Length * Layout.CompoundChildStride);
        int nodeCount = 3;
        int at = nodesAt + nodeCount * UnitHitboxCodec.BvhNodeStride;
        var colliderAt = new int[children.Length];
        var hullAt = new int[children.Length];
        for (int i = 0; i < children.Length; i++)
        {
            colliderAt[i] = at;
            int size = children[i].Type == Layout.ColliderTypeSphere ? Layout.SphereColliderVertexOffset + 12 : Layout.CapsuleColliderVertex1Offset + 12;
            at = Align(at + size);
        }
        for (int i = 0; i < children.Length; i++)
        {
            if (!children[i].HullMirrors && children[i].HullOverride == null) continue;
            hullAt[i] = at;
            at = Align(at + children[i].Vertices.Length * 12);
        }

        var d = new byte[at];
        d[Layout.ColliderHeaderTypeOffset] = (byte)Layout.ColliderTypeCompound;
        PutF32(d, Layout.CompoundColliderBoundingRadiusOffset, 5f);
        PutArray(d, Layout.CompoundColliderChildrenOffset, childrenAt, children.Length);
        PutArray(d, Layout.CompoundColliderBvhNodesOffset, nodesAt, nodeCount);

        var boxes = new UnitHitboxCodec.Aabb[children.Length];
        for (int i = 0; i < children.Length; i++)
        {
            var c = children[i];
            int child = childrenAt + i * Layout.CompoundChildStride;
            int rot = child + Layout.CompoundChildTransformOffset + Layout.RigidTransformRotOffset;
            for (int k = 0; k < 4; k++) PutF32(d, rot + k * 4, c.Rotation[k]);
            PutVec3(d, child + Layout.CompoundChildTransformOffset + Layout.RigidTransformPosOffset, c.Position);
            int field = child + Layout.CompoundChildColliderOffset;
            PutI32(d, field, colliderAt[i] - field);

            int col = colliderAt[i];
            d[col + Layout.ColliderHeaderTypeOffset] = (byte)c.Type;
            PutI32(d, col + Layout.ConvexColliderHeaderFilterOffset + Layout.CollisionFilterBelongsToOffset, (int)c.Filter);
            int hull = col + (c.Type == Layout.ColliderTypeSphere ? Layout.SphereColliderHullOffset : Layout.CapsuleColliderHullOffset);
            PutF32(d, hull + Layout.ConvexHullConvexRadiusOffset, c.Radius);
            int v0 = col + (c.Type == Layout.ColliderTypeSphere ? Layout.SphereColliderVertexOffset : Layout.CapsuleColliderVertex0Offset);
            for (int k = 0; k < c.Vertices.Length; k++) PutVec3(d, v0 + k * 12, c.Vertices[k]);
            int arr = hull + Layout.ConvexHullVerticesOffset;
            int hullVerts = hullAt[i] != 0 ? hullAt[i] : v0;   // aliasing, as live
            PutArray(d, arr, hullVerts, c.Vertices.Length);
            if (hullAt[i] != 0)
            {
                var values = c.HullOverride ?? c.Vertices;
                for (int k = 0; k < values.Length; k++) PutVec3(d, hullAt[i] + k * 12, values[k]);
            }

            var box = UnitHitboxCodec.Aabb.Empty;
            foreach (var v in c.Vertices) box = box.Include(v + c.Position, c.Radius);
            boxes[i] = box;
        }

        // Node 0: empty. Node 1: root, lane 0 -> node 2. Node 2: leaf, lane i -> child i.
        for (int n = 0; n < nodeCount; n++)
            for (int lane = 0; lane < 4; lane++)
                WriteLane(d, nodesAt + n * UnitHitboxCodec.BvhNodeStride, lane, UnitHitboxCodec.Aabb.Empty);
        int root = nodesAt + 1 * UnitHitboxCodec.BvhNodeStride;
        int leaf = nodesAt + 2 * UnitHitboxCodec.BvhNodeStride;
        var all = UnitHitboxCodec.Aabb.Empty;
        foreach (var b in boxes) all = all.Union(b);
        WriteLane(d, root, 0, all);
        PutI32(d, root + Layout.BvhNodeDataOffset, 2);
        PutI32(d, leaf + Layout.BvhNodeFlagsOffset, 1);
        for (int lane = 0; lane < 4; lane++)
        {
            bool used = lane < children.Length && (leafHoldsAll || children[lane].Filter != HittableFilter);
            PutI32(d, leaf + Layout.BvhNodeDataOffset + lane * 4, used ? lane : -1);
            if (used) WriteLane(d, leaf, lane, boxes[lane]);
        }

        return new Blob { Data = d, ChildrenAt = childrenAt, NodesAt = nodesAt, ColliderAt = colliderAt, HullAt = hullAt };
    }

    private static int Align(int x) => (x + 15) & ~15;
    private static void PutI32(byte[] d, int at, int v) => BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(at, 4), v);
    private static void PutF32(byte[] d, int at, float v) => PutI32(d, at, BitConverter.SingleToInt32Bits(v));
    private static void PutVec3(byte[] d, int at, Vec3 v) { PutF32(d, at, v.X); PutF32(d, at + 4, v.Y); PutF32(d, at + 8, v.Z); }
    private static void PutArray(byte[] d, int field, int target, int count) { PutI32(d, field, target - field); PutI32(d, field + 4, count); }
    private static float F32(byte[] d, int at) => UnitHitboxCodec.F32(d, at);
    private static Vec3 V(byte[] d, int at) => UnitHitboxCodec.ReadVec3(d, at);

    private static void WriteLane(byte[] d, int nodeAt, int lane, UnitHitboxCodec.Aabb box)
    {
        int b = nodeAt + Layout.BvhNodeBoundsOffset + lane * 4;
        PutF32(d, b + Layout.FourTransposedAabbsLxOffset, box.Min.X);
        PutF32(d, b + Layout.FourTransposedAabbsHxOffset, box.Max.X);
        PutF32(d, b + Layout.FourTransposedAabbsLyOffset, box.Min.Y);
        PutF32(d, b + Layout.FourTransposedAabbsHyOffset, box.Max.Y);
        PutF32(d, b + Layout.FourTransposedAabbsLzOffset, box.Min.Z);
        PutF32(d, b + Layout.FourTransposedAabbsHzOffset, box.Max.Z);
    }

    private static UnitHitboxCodec.Aabb Lane(byte[] d, int nodeAt, int lane)
    {
        int b = nodeAt + Layout.BvhNodeBoundsOffset + lane * 4;
        return new UnitHitboxCodec.Aabb(
            new Vec3(F32(d, b + Layout.FourTransposedAabbsLxOffset), F32(d, b + Layout.FourTransposedAabbsLyOffset), F32(d, b + Layout.FourTransposedAabbsLzOffset)),
            new Vec3(F32(d, b + Layout.FourTransposedAabbsHxOffset), F32(d, b + Layout.FourTransposedAabbsHyOffset), F32(d, b + Layout.FourTransposedAabbsHzOffset)));
    }

    private static void AssertVec(Vec3 expected, Vec3 actual, float tol = 1e-4f)
    {
        Assert.InRange(actual.X, expected.X - tol, expected.X + tol);
        Assert.InRange(actual.Y, expected.Y - tol, expected.Y + tol);
        Assert.InRange(actual.Z, expected.Z - tol, expected.Z + tol);
    }

    private static void AssertBox(Vec3 min, Vec3 max, UnitHitboxCodec.Aabb box)
    {
        AssertVec(min, box.Min);
        AssertVec(max, box.Max);
    }

    private static int Leaf(Blob b) => b.NodesAt + 2 * UnitHitboxCodec.BvhNodeStride;
    private static int Root(Blob b) => b.NodesAt + 1 * UnitHitboxCodec.BvhNodeStride;

    // ---- layout ------------------------------------------------------------------------------

    [Fact]
    public void Bvh_node_stride_is_the_live_0x80_and_the_Frostmaw_blob_is_928_bytes()
    {
        Assert.Equal(0x80, UnitHitboxCodec.BvhNodeStride);
        Assert.Equal(0x28, Layout.CompoundChildStride);
        Assert.Equal(928, Build(Frostmaw()).Data.Length);
    }

    [Fact]
    public void The_live_hittable_filter_carries_the_Hittable_bit_and_the_others_do_not()
    {
        Assert.NotEqual(0u, HittableFilter & (uint)Layout.CollisionFilterFlagsHittable);
        Assert.Equal(0u, BodyFilter & (uint)Layout.CollisionFilterFlagsHittable);
        Assert.Equal(0u, SensorFilter & (uint)Layout.CollisionFilterFlagsHittable);
    }

    // ---- ground-anchored math -----------------------------------------------------------------

    [Fact]
    public void Ground_anchored_scale_keeps_the_bottom_and_scales_height_and_offsets()
    {
        var points = new[] { new Vec3(0.5f, 1, -0.25f), new Vec3(0.5f, 3, -0.25f) };
        float before = UnitHitboxCodec.Bottom(points, 1f);
        float r = UnitHitboxCodec.ScaleAboutGround(points, 1f, 3f);

        Assert.Equal(3f, r);
        Assert.Equal(0f, before);
        Assert.Equal(before, UnitHitboxCodec.Bottom(points, r), 4);
        AssertVec(new Vec3(1.5f, 3, -0.75f), points[0]);
        AssertVec(new Vec3(1.5f, 9, -0.75f), points[1]);
    }

    [Fact]
    public void Ground_anchored_scale_of_a_shape_below_the_origin_keeps_its_own_bottom()
    {
        // Pillar-like capsule: bottom at 0.4 - 0.5 = -0.1.
        var points = new[] { new Vec3(0, 0.4f, 0), new Vec3(0, 1.6f, 0) };
        float r = UnitHitboxCodec.ScaleAboutGround(points, 0.5f, 0.5f);
        Assert.Equal(-0.1f, UnitHitboxCodec.Bottom(points, r), 4);
        AssertVec(new Vec3(0, 0.15f, 0), points[0]);
        AssertVec(new Vec3(0, 0.75f, 0), points[1]);
        Assert.Equal(0.25f, r);
    }

    // ---- the blob surgery -----------------------------------------------------------------

    [Fact]
    public void Scale_two_grows_only_the_hittable_capsule_from_the_ground_up()
    {
        var b = Build(Frostmaw());
        var body = b.Data.AsSpan(b.ColliderAt[0], 0x70).ToArray();
        var sensor = b.Data.AsSpan(b.ColliderAt[2], 0x60).ToArray();

        Assert.True(UnitHitboxCodec.TryScale(b.Data, 2f, out var report, out string why), why);

        int col = b.ColliderAt[1];
        Assert.Equal(2f, F32(b.Data, col + Layout.CapsuleColliderHullOffset + Layout.ConvexHullConvexRadiusOffset));
        AssertVec(new Vec3(0, 2, 0), V(b.Data, col + Layout.CapsuleColliderVertex0Offset));
        AssertVec(new Vec3(0, 6, 0), V(b.Data, col + Layout.CapsuleColliderVertex1Offset));
        Assert.Equal(new[] { 1 }, report.Children.ToArray());
        Assert.Equal(body, b.Data.AsSpan(b.ColliderAt[0], 0x70).ToArray());
        Assert.Equal(sensor, b.Data.AsSpan(b.ColliderAt[2], 0x60).ToArray());
    }

    [Fact]
    public void Aliased_hull_vertices_are_scaled_once_not_twice()
    {
        var b = Build(Frostmaw());
        Assert.True(UnitHitboxCodec.TryPlan(b.Data, out var plan, out string why), why);
        Assert.True(plan.Children.Single().HullAliases);
        Assert.True(UnitHitboxCodec.TryScale(b.Data, 3f, out _, out why), why);
        // Aliased: the hull IS m_Vertex0/1. Scaled twice would put v1 at 27, not 9.
        AssertVec(new Vec3(0, 9, 0), V(b.Data, b.ColliderAt[1] + Layout.CapsuleColliderVertex1Offset));
        AssertVec(new Vec3(0, 3, 0), V(b.Data, b.ColliderAt[1] + Layout.CapsuleColliderVertex0Offset));
    }

    [Fact]
    public void A_hull_that_mirrors_the_vertices_gets_the_same_new_values()
    {
        var specs = Frostmaw();
        specs[1].HullMirrors = true;
        var b = Build(specs);
        Assert.True(UnitHitboxCodec.TryPlan(b.Data, out var plan, out string why), why);
        Assert.True(plan.Children.Single().HullMirrors);
        Assert.True(UnitHitboxCodec.TryScale(b.Data, 2f, out _, out why), why);
        AssertVec(new Vec3(0, 2, 0), V(b.Data, b.HullAt[1]));
        AssertVec(new Vec3(0, 6, 0), V(b.Data, b.HullAt[1] + 12));
    }

    [Fact]
    public void A_hull_that_neither_aliases_nor_mirrors_is_refused_and_nothing_is_written()
    {
        var specs = Frostmaw();
        specs[1].HullOverride = new[] { new Vec3(0, 1, 0), new Vec3(0, 2.5f, 0) };
        var b = Build(specs);
        var before = b.Data.ToArray();
        Assert.False(UnitHitboxCodec.TryScale(b.Data, 2f, out _, out string why));
        Assert.Contains("neither alias nor mirror", why);
        Assert.Equal(before, b.Data);
    }

    [Fact]
    public void The_leaf_lane_takes_the_new_box_and_the_root_grows_to_contain_it()
    {
        var b = Build(Frostmaw());
        var leaf0 = Lane(b.Data, Leaf(b), 0);
        var leaf2 = Lane(b.Data, Leaf(b), 2);
        AssertBox(new Vec3(-5, -5, -5), new Vec3(5, 5, 5), Lane(b.Data, Root(b), 0));

        Assert.True(UnitHitboxCodec.TryScale(b.Data, 2f, out var report, out string why), why);

        AssertBox(new Vec3(-2, 0, -2), new Vec3(2, 8, 2), Lane(b.Data, Leaf(b), 1));
        AssertBox(leaf0.Min, leaf0.Max, Lane(b.Data, Leaf(b), 0));
        AssertBox(leaf2.Min, leaf2.Max, Lane(b.Data, Leaf(b), 2));
        AssertBox(new Vec3(-5, -5, -5), new Vec3(5, 8, 5), Lane(b.Data, Root(b), 0));
        Assert.Equal(1, report.LeafLanes);
        Assert.Equal(1, report.GrownLanes);
        // Node 0 stays empty.
        Assert.True(Lane(b.Data, b.NodesAt, 0).IsEmpty);
    }

    [Fact]
    public void Bounding_radius_rises_to_the_new_box_corner_and_never_drops()
    {
        var b = Build(Frostmaw());
        Assert.True(UnitHitboxCodec.TryScale(b.Data, 2f, out var report, out string why), why);
        float expected = (float)Math.Sqrt(2 * 2 + 8 * 8 + 2 * 2);
        Assert.Equal(expected, F32(b.Data, Layout.CompoundColliderBoundingRadiusOffset), 4);
        Assert.Equal(5f, report.BoundingRadiusBefore);

        var small = Build(Frostmaw());
        Assert.True(UnitHitboxCodec.TryScale(small.Data, 0.5f, out _, out why), why);
        Assert.Equal(5f, F32(small.Data, Layout.CompoundColliderBoundingRadiusOffset));
    }

    [Fact]
    public void Shrinking_rewrites_the_leaf_lane_and_leaves_the_root_alone()
    {
        var b = Build(Frostmaw());
        Assert.True(UnitHitboxCodec.TryScale(b.Data, 0.5f, out var report, out string why), why);
        // Bottom 0; radius 0.5; v0.y 0.5, v1.y 1.5.
        AssertBox(new Vec3(-0.5f, 0, -0.5f), new Vec3(0.5f, 2f, 0.5f), Lane(b.Data, Leaf(b), 1));
        AssertBox(new Vec3(-5, -5, -5), new Vec3(5, 5, 5), Lane(b.Data, Root(b), 0));
        Assert.Equal(0, report.GrownLanes);
    }

    [Fact]
    public void A_hittable_sphere_with_an_offset_scales_from_the_ground_and_outward()
    {
        var specs = new[]
        {
            Sphere(HittableFilter, 0.5f, new Vec3(0.5f, 1, 0)),
            Sphere(SensorFilter, 5f, new Vec3(0, 0, 0)),
        };
        var b = Build(specs);
        Assert.True(UnitHitboxCodec.TryScale(b.Data, 2f, out _, out string why), why);
        // Bottom 0.5; center y 0.5 + (1 - 0.5) * 2 = 1.5; x 1.0; radius 1.
        AssertVec(new Vec3(1, 1.5f, 0), V(b.Data, b.ColliderAt[0] + Layout.SphereColliderVertexOffset));
        Assert.Equal(1f, F32(b.Data, b.ColliderAt[0] + Layout.SphereColliderHullOffset + Layout.ConvexHullConvexRadiusOffset));
        AssertBox(new Vec3(0, 0.5f, -1), new Vec3(2, 2.5f, 1), Lane(b.Data, Leaf(b), 0));
    }

    [Fact]
    public void A_child_transform_is_honoured_and_left_unchanged()
    {
        // Capsule along child-local x, rotated 90 degrees about z so it stands up, positioned at y 2.
        var specs = Frostmaw();
        float s = (float)Math.Sqrt(0.5);
        specs[1] = Capsule(HittableFilter, 1f, new Vec3(-1, 0, 0), new Vec3(1, 0, 0));
        specs[1].Rotation = new[] { 0f, 0f, s, s };
        specs[1].Position = new Vec3(0, 2, 0);
        var b = Build(specs);
        var transformBefore = b.Data.AsSpan(b.ChildrenAt + Layout.CompoundChildStride, 0x1C).ToArray();

        Assert.True(UnitHitboxCodec.TryScale(b.Data, 2f, out _, out string why), why);

        // Compound space before: ends (0, 1, 0) and (0, 3, 0), r 1, bottom 0. After: (0, 2, 0), (0, 6, 0), r 2.
        var t = UnitHitboxCodec.RigidTransform.Read(b.Data, b.ChildrenAt + Layout.CompoundChildStride);
        var ends = new[]
        {
            t.Apply(V(b.Data, b.ColliderAt[1] + Layout.CapsuleColliderVertex0Offset)),
            t.Apply(V(b.Data, b.ColliderAt[1] + Layout.CapsuleColliderVertex1Offset)),
        }.OrderBy(p => p.Y).ToArray();
        AssertVec(new Vec3(0, 2, 0), ends[0]);
        AssertVec(new Vec3(0, 6, 0), ends[1]);
        Assert.Equal(transformBefore, b.Data.AsSpan(b.ChildrenAt + Layout.CompoundChildStride, 0x1C).ToArray());
        AssertBox(new Vec3(-2, 0, -2), new Vec3(2, 8, 2), Lane(b.Data, Leaf(b), 1));
    }

    [Fact]
    public void No_hittable_child_is_refused()
    {
        var specs = Frostmaw();
        specs[1].Filter = BodyFilter;
        var b = Build(specs);
        Assert.False(UnitHitboxCodec.TryScale(b.Data, 2f, out _, out string why));
        Assert.Contains("no child on the Hittable layer", why);
    }

    [Fact]
    public void A_hittable_box_is_refused_by_type()
    {
        var specs = Frostmaw();
        var b = Build(specs);
        b.Data[b.ColliderAt[1]] = 5; // Box
        var before = b.Data.ToArray();
        Assert.False(UnitHitboxCodec.TryScale(b.Data, 2f, out _, out string why));
        Assert.Contains("collider type 5", why);
        Assert.Equal(before, b.Data);
    }

    [Fact]
    public void A_non_compound_collider_is_refused()
    {
        var b = Build(Frostmaw());
        b.Data[0] = (byte)Layout.ColliderTypeCapsule;
        Assert.False(UnitHitboxCodec.TryScale(b.Data, 2f, out _, out string why));
        Assert.Contains("not a compound collider", why);
    }

    [Fact]
    public void A_hittable_child_missing_from_every_leaf_is_refused()
    {
        var b = Build(Frostmaw(), leafHoldsAll: false);
        var before = b.Data.ToArray();
        Assert.False(UnitHitboxCodec.TryScale(b.Data, 2f, out _, out string why));
        Assert.Contains("no BVH leaf holds Hittable child 1", why);
        Assert.Equal(before, b.Data);
    }

    [Fact]
    public void Truncated_or_out_of_bounds_blobs_are_refused()
    {
        var b = Build(Frostmaw());
        Assert.False(UnitHitboxCodec.TryScale(b.Data.AsSpan(0, 0x20).ToArray(), 2f, out _, out _));
        Assert.False(UnitHitboxCodec.TryScale(b.Data.AsSpan(0, 0x300).ToArray(), 2f, out _, out _));

        var bad = Build(Frostmaw());
        PutI32(bad.Data, bad.ChildrenAt + Layout.CompoundChildStride + Layout.CompoundChildColliderOffset, 100_000);
        Assert.False(UnitHitboxCodec.TryScale(bad.Data, 2f, out _, out string why));
        Assert.Contains("outside the blob", why);
    }

    [Fact]
    public void Unknown_bvh_flags_are_refused()
    {
        var b = Build(Frostmaw());
        PutI32(b.Data, Root(b) + Layout.BvhNodeFlagsOffset, 7);
        Assert.False(UnitHitboxCodec.TryScale(b.Data, 2f, out _, out string why));
        Assert.Contains("flags 7", why);
    }

    [Theory]
    [InlineData(0.25f, true)]
    [InlineData(1f, true)]
    [InlineData(4f, true)]
    [InlineData(0.24f, false)]
    [InlineData(4.01f, false)]
    [InlineData(0f, false)]
    [InlineData(-2f, false)]
    [InlineData(float.NaN, false)]
    [InlineData(float.PositiveInfinity, false)]
    public void Scale_range_is_a_quarter_to_four(float scale, bool valid)
    {
        Assert.Equal(valid, UnitHitboxCodec.IsValidScale(scale));
        if (!valid)
        {
            var b = Build(Frostmaw());
            var before = b.Data.ToArray();
            Assert.False(UnitHitboxCodec.TryScale(b.Data, scale, out _, out string why));
            Assert.Contains("outside", why);
            Assert.Equal(before, b.Data);
        }
    }

    [Fact]
    public void Scaling_the_original_twice_at_the_same_scale_gives_identical_bytes()
    {
        var one = Build(Frostmaw());
        var two = Build(Frostmaw());
        Assert.True(UnitHitboxCodec.TryScale(one.Data, 2.5f, out _, out _));
        Assert.True(UnitHitboxCodec.TryScale(two.Data, 2.5f, out _, out _));
        Assert.Equal(one.Data, two.Data);
    }

    [Fact]
    public void Hash_perturbation_is_per_scale_and_reversible()
    {
        var header = new byte[Layout.BlobAssetHeaderBytes];
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(Layout.BlobAssetHeaderHashOffset, 8), 0x1234_5678_9ABC_DEF0UL);
        var a = header.ToArray();
        var b = header.ToArray();
        UnitHitboxCodec.PerturbHash(a, 2f);
        UnitHitboxCodec.PerturbHash(b, 3f);
        Assert.NotEqual(header, a);
        Assert.NotEqual(a, b);
        UnitHitboxCodec.PerturbHash(a, 2f);
        Assert.Equal(header, a);
        // Only the hash moves.
        UnitHitboxCodec.PerturbHash(b, 3f);
        Assert.Equal(header, b);
    }

    // ---- move speed ---------------------------------------------------------------------------

    private static (float[] Write, bool[] Do, UnitMoveSpeedOutcome Outcome) Plan(float[] current, float[] prefab, float applied, float requested)
    {
        var write = new float[current.Length];
        var doWrite = new bool[current.Length];
        var outcome = UnitMoveSpeedMath.Plan(current, prefab, applied, requested, write, doWrite);
        return (write, doWrite, outcome);
    }

    // Frostmaw: Movement.Speed 4.5; AiMoveSpeeds walk 2.5, run 4.5, circle 4.5, return 6.
    private static readonly float[] FrostmawSpeeds = { 4.5f, 2.5f, 4.5f, 4.5f, 6f };

    [Fact]
    public void Doubling_from_vanilla_writes_prefab_times_two_everywhere()
    {
        var p = Plan(FrostmawSpeeds, FrostmawSpeeds, 1f, 2f);
        Assert.Equal(UnitMoveSpeedOutcome.Write, p.Outcome);
        Assert.Equal(new[] { 9f, 5f, 9f, 9f, 12f }, p.Write);
        Assert.All(p.Do, Assert.True);
    }

    [Fact]
    public void Repeated_calls_do_not_compound()
    {
        var state = new UnitMoveSpeedState();
        float[] values = FrostmawSpeeds.ToArray();
        foreach (float m in new[] { 2f, 3f, 3f, 0.5f })
        {
            state.Requested = m;
            var p = Plan(values, FrostmawSpeeds, state.Applied, state.Requested);
            if (p.Outcome == UnitMoveSpeedOutcome.Write) values = p.Write;
            state.Applied = state.Requested;
        }
        Assert.Equal(FrostmawSpeeds.Select(v => v * 0.5f).ToArray(), values);
        Assert.False(state.Pending);
    }

    [Fact]
    public void Same_multiplier_twice_is_unchanged_and_writes_nothing()
    {
        var p = Plan(new[] { 9f }, new[] { 4.5f }, 2f, 2f);
        Assert.Equal(UnitMoveSpeedOutcome.Unchanged, p.Outcome);
        Assert.False(p.Do[0]);
    }

    [Fact]
    public void One_restores_the_prefab_value_exactly()
    {
        var p = Plan(new[] { 4.5f * 2.7f }, new[] { 4.5f }, 2.7f, 1f);
        Assert.Equal(4.5f, p.Write[0]);
    }

    [Fact]
    public void A_multiplicative_modifier_is_kept_and_its_removal_lands_on_the_scaled_base()
    {
        // Live: 7 read 14 during a movement effect. Here vanilla 4.5 under a x2 effect reads 9.
        var p = Plan(new[] { 9f }, new[] { 4.5f }, 1f, 2f);
        Assert.Equal(18f, p.Write[0]);
        Assert.Equal(4.5f * 2f, p.Write[0] / 2f); // the game undoes x2: scaled base
    }

    [Fact]
    public void An_additive_modifier_leaves_the_documented_error()
    {
        const float a = 1f, r = 2f;
        var p = Plan(new[] { 4.5f + a }, new[] { 4.5f }, 1f, r);
        float afterRemoval = p.Write[0] - a;
        Assert.Equal(a * (r - 1f), afterRemoval - 4.5f * r, 4);
    }

    [Fact]
    public void A_zero_speed_with_a_moving_prefab_defers_the_whole_set()
    {
        var p = Plan(new[] { 0f, 2.5f, 4.5f, 4.5f, 6f }, FrostmawSpeeds, 1f, 2f);
        Assert.Equal(UnitMoveSpeedOutcome.Deferred, p.Outcome);
        Assert.All(p.Do, Assert.False);
    }

    [Fact]
    public void A_value_that_is_zero_on_the_prefab_too_is_skipped()
    {
        var p = Plan(new[] { 4.5f, 0f }, new[] { 4.5f, 0f }, 1f, 2f);
        Assert.Equal(UnitMoveSpeedOutcome.Write, p.Outcome);
        Assert.True(p.Do[0]);
        Assert.False(p.Do[1]);
        Assert.Equal(0f, p.Write[1]);
    }

    [Fact]
    public void An_unknown_prefab_falls_back_to_the_ratio()
    {
        var p = Plan(new[] { 6f }, new[] { -1f }, 2f, 3f);
        Assert.Equal(9f, p.Write[0]);
        Assert.True(p.Do[0]);
    }

    [Fact]
    public void Reapply_restores_a_value_a_spawn_system_reset_and_leaves_the_rest()
    {
        var current = new[] { 4.5f, 5f, 13f };      // reset, already scaled, under a modifier
        var prefab = new[] { 4.5f, 2.5f, 4.5f };
        var write = new float[3];
        var doWrite = new bool[3];
        Assert.True(UnitMoveSpeedMath.PlanReapply(current, prefab, 2f, write, doWrite));
        Assert.Equal(new[] { true, false, false }, doWrite);
        Assert.Equal(9f, write[0]);

        Assert.False(UnitMoveSpeedMath.PlanReapply(current, prefab, 1f, write, doWrite));
        Assert.All(doWrite, Assert.False);
    }

    [Theory]
    [InlineData(0.1f, true)]
    [InlineData(1f, true)]
    [InlineData(4f, true)]
    [InlineData(0.09f, false)]
    [InlineData(4.5f, false)]
    [InlineData(0f, false)]
    [InlineData(float.NaN, false)]
    public void Multiplier_range_is_a_tenth_to_four(float multiplier, bool valid)
    {
        Assert.Equal(valid, UnitMoveSpeedMath.IsValidMultiplier(multiplier));
        if (!valid)
            Assert.Throws<ArgumentOutOfRangeException>(() => Plan(new[] { 4.5f }, new[] { 4.5f }, 1f, multiplier));
    }

    [Fact]
    public void State_is_pending_only_while_requested_and_applied_differ()
    {
        var s = new UnitMoveSpeedState();
        Assert.False(s.Pending);
        s.Requested = 2f;
        Assert.True(s.Pending);
        s.Applied = 2f;
        Assert.False(s.Pending);
    }

    [Fact]
    public void Spawn_options_default_to_null()
    {
        var o = new UnitSpawnOptions();
        Assert.Null(o.HitboxScale);
        Assert.Null(o.MoveSpeedMultiplier);
    }
}
