using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;

namespace Progenitor.Units;

// Game-free half of ctx.Units.SetHitboxScale (docs/modules.md, "Hitbox scale and move speed").
// Every offset is a Layout constant resolved against the server metadata. The live half (UnitBody.cs)
// hands these methods a span over a private copy of the unit's collider blob; the tests hand them
// a synthetic compound blob in a byte array.
//
// Evidence (internal research notes, 2026-09-25 creature customization review, "Hitbox and movement
// speed"): a scaled Hittable capsule plus a BVH refit was hit at 1.6 m and 2.5 m off the bolt's
// line; without the refit it was never hit.

/// <summary>What <see cref="UnitHitboxCodec.TryScale"/> changed, for the one log line and the tests.</summary>
internal sealed class UnitHitboxScaleReport
{
    /// <summary>Indices of the compound children on the Hittable layer that were scaled.</summary>
    public List<int> Children { get; } = new();

    /// <summary>BVH leaf lanes rewritten to a scaled child's new box.</summary>
    public int LeafLanes { get; set; }

    /// <summary>BVH internal lanes grown to contain a rewritten leaf.</summary>
    public int GrownLanes { get; set; }

    /// <summary>Compound bounding radius before and after.</summary>
    public float BoundingRadiusBefore { get; set; }

    public float BoundingRadiusAfter { get; set; }

    public override string ToString() =>
        "children=[" + string.Join(",", Children) + "] leafLanes=" + LeafLanes.ToString(CultureInfo.InvariantCulture)
        + " grownLanes=" + GrownLanes.ToString(CultureInfo.InvariantCulture)
        + " boundingRadius=" + BoundingRadiusBefore.ToString("0.###", CultureInfo.InvariantCulture)
        + "->" + BoundingRadiusAfter.ToString("0.###", CultureInfo.InvariantCulture);
}

/// <summary>
/// Scales the Hittable child of a compound <c>Unity.Physics</c> collider blob in place, keeping its
/// bottom on the ground, and refits the compound's bounding-volume hierarchy so hit queries reach
/// the bigger shape. Works on the blob's data (what <c>PhysicsCollider.Value</c> points at), never
/// on its 32-byte header; <see cref="PerturbHash"/> handles the header.
/// </summary>
internal static class UnitHitboxCodec
{
    /// <summary>Smallest accepted scale. Below this a hitbox is far smaller than any practical body.</summary>
    public const float MinScale = 0.25f;

    /// <summary>Largest accepted scale.</summary>
    public const float MaxScale = 4f;

    /// <summary>
    /// Native stride of one <c>BoundingVolumeHierarchy.Node</c>. The metadata states Bounds @0x0,
    /// Data @0x60 and Flags @0x70, which a field-width derivation rounds to 0x74; the native node is
    /// that extent rounded up to 16 bytes. Two measurements agree: the live leaf walk at this stride
    /// found node 0 empty, node 1 the root and node 2 the leaf; and Frostmaw's 928-byte compound
    /// only adds up with 0x80 nodes (0x60 header + 3 children x 0x28, 16-aligned, + 3 nodes x 0x80 +
    /// two 0x70 capsules + one 0x60 sphere = 928; with 0x74 nodes it is 896).
    /// </summary>
    public const int BvhNodeStride = (Layout.BvhNodeFlagsOffset + sizeof(int) + 15) & ~15;

    /// <summary>Width of a <c>Unity.Physics.BlobArray</c> {Offset, Length}.</summary>
    private const int BlobArrayWidth = Layout.PhysicsBlobArrayLengthOffset + sizeof(int);

    /// <summary>Bytes a compound blob must have for the fields this codec reads.</summary>
    private const int CompoundMinBytes = Layout.CompoundColliderBvhNodesOffset + BlobArrayWidth;

    private const int CapsuleBytes = Layout.CapsuleColliderVertex1Offset + 12;
    private const int SphereBytes = Layout.SphereColliderVertexOffset + 12;

    /// <summary>Leaf flag of a BVH node (Unity.Physics: <c>IsLeaf</c> is Flags != 0, set to 1; internal nodes are 0).</summary>
    private const int BvhLeafFlag = 1;

    /// <summary>Deepest BVH walk accepted. A compound this small never nears it; a cycle would.</summary>
    private const int MaxBvhDepth = 32;

    /// <summary>True for a finite scale within <see cref="MinScale"/>..<see cref="MaxScale"/>.</summary>
    public static bool IsValidScale(float scale) =>
        !float.IsNaN(scale) && !float.IsInfinity(scale) && scale >= MinScale && scale <= MaxScale;

    /// <summary>The message a caller throws for an out-of-range scale.</summary>
    public static string ScaleRangeMessage(float scale) =>
        "hitbox scale " + scale.ToString(CultureInfo.InvariantCulture) + " is outside "
        + MinScale.ToString(CultureInfo.InvariantCulture) + " to " + MaxScale.ToString(CultureInfo.InvariantCulture)
        + " (1 restores the vanilla hitbox).";

    /// <summary>
    /// Header hash salt for a copy made at <paramref name="scale"/>. The copy is a different blob and
    /// must not share the original's hash, so nothing that dedupes blobs by hash folds the two.
    /// </summary>
    public static ulong HashSalt(float scale) =>
        0x5CA1_E000_0000_0000UL | (uint)BitConverter.SingleToInt32Bits(scale);

    /// <summary>XOR the header's <c>Hash</c> with <see cref="HashSalt"/>. <paramref name="header"/> is the 32-byte BlobAssetHeader.</summary>
    public static void PerturbHash(Span<byte> header, float scale)
    {
        if (header.Length < Layout.BlobAssetHeaderBytes) throw new ArgumentException("header is shorter than a BlobAssetHeader", nameof(header));
        var slot = header.Slice(Layout.BlobAssetHeaderHashOffset, 8);
        BinaryPrimitives.WriteUInt64LittleEndian(slot, BinaryPrimitives.ReadUInt64LittleEndian(slot) ^ HashSalt(scale));
    }

    // ---- ground-anchored scaling (pure math, compound space) ------------------------------------

    /// <summary>
    /// Scale a round shape (a capsule's segment ends or a sphere's center, plus its radius) by
    /// <paramref name="scale"/> about the point (0, bottom, 0), where bottom = min(point.y) - radius.
    /// The lowest point of the shape stays where it was, so a bigger hitbox grows up out of the
    /// ground instead of sinking into it or floating; horizontal offsets from the unit's origin scale
    /// with it. <paramref name="points"/> is compound space and is rewritten in place.
    /// </summary>
    public static float ScaleAboutGround(Span<Vec3> points, float radius, float scale)
    {
        if (points.Length == 0) throw new ArgumentException("a round shape has at least one point", nameof(points));
        float bottom = Bottom(points, radius);
        for (int i = 0; i < points.Length; i++)
        {
            var p = points[i];
            points[i] = new Vec3(p.X * scale, bottom + (p.Y - bottom) * scale, p.Z * scale);
        }
        return radius * scale;
    }

    /// <summary>Lowest y a round shape reaches: min(point.y) - radius.</summary>
    public static float Bottom(Span<Vec3> points, float radius)
    {
        float min = float.PositiveInfinity;
        for (int i = 0; i < points.Length; i++) min = Math.Min(min, points[i].Y);
        return min - radius;
    }

    // ---- the blob surgery -----------------------------------------------------------------------

    /// <summary>
    /// Scale every compound child on the Hittable layer of <paramref name="data"/> by
    /// <paramref name="scale"/> (ground-anchored), refit the BVH and raise the bounding radius.
    /// <paramref name="data"/> must be a pristine copy of the original blob: the scale is absolute,
    /// not relative to whatever was there. Nothing is written unless every check passes; on false
    /// <paramref name="why"/> says what was refused.
    /// </summary>
    public static bool TryScale(Span<byte> data, float scale, out UnitHitboxScaleReport report, out string why)
    {
        report = new UnitHitboxScaleReport();
        if (!IsValidScale(scale)) { why = ScaleRangeMessage(scale); return false; }
        if (!TryPlan(data, out var plan, out why)) return false;

        // Everything below writes. Every bound it relies on was checked by TryPlan.
        var boxes = new Dictionary<int, Aabb>();
        foreach (var child in plan.Children)
        {
            var points = new Vec3[child.VertexCount];
            for (int k = 0; k < child.VertexCount; k++)
                points[k] = child.Transform.Apply(ReadVec3(data, child.VertexAt[k]));

            float newRadius = ScaleAboutGround(points, child.Radius, scale);
            var box = Aabb.Empty;
            for (int k = 0; k < child.VertexCount; k++)
            {
                box = box.Include(points[k], newRadius);
                // Back to child space: the child's transform is kept, its geometry moves.
                WriteVec3(data, child.VertexAt[k], child.Transform.InverseApply(points[k]));
            }
            // A hull that mirrors (rather than aliases) the vertices gets the same values. An aliased
            // hull IS m_Vertex0/1: writing it again would scale it twice.
            if (child.HullMirrors)
                for (int k = 0; k < child.VertexCount; k++)
                    WriteVec3(data, child.HullVerticesAt + k * 12, ReadVec3(data, child.VertexAt[k]));
            PutF32(data, child.RadiusAt, newRadius);
            boxes[child.Index] = box;
            report.Children.Add(child.Index);
        }

        // Leaf lanes that hold a scaled child get its new box; internal lanes grow to contain their
        // subtree, from the root down. Lanes only ever grow, so a box the game computed is never cut.
        foreach (int node in plan.LeafNodes)
        {
            int at = plan.NodesAt + node * BvhNodeStride;
            for (int lane = 0; lane < 4; lane++)
            {
                int child = I32(data, at + Layout.BvhNodeDataOffset + lane * 4);
                if (child < 0 || !boxes.TryGetValue(child, out var box)) continue;
                WriteLane(data, at, lane, box);
                report.LeafLanes++;
            }
        }
        var visiting = new HashSet<int>();
        int grown = 0;
        Refit(data, plan, 1, 0, visiting, ref grown);
        report.GrownLanes = grown;

        float before = F32(data, Layout.CompoundColliderBoundingRadiusOffset);
        float needed = before;
        foreach (var box in boxes.Values) needed = Math.Max(needed, box.FarthestCornerDistance());
        report.BoundingRadiusBefore = before;
        report.BoundingRadiusAfter = needed;
        if (needed > before) PutF32(data, Layout.CompoundColliderBoundingRadiusOffset, needed);
        why = "";
        return true;
    }

    /// <summary>
    /// Read-only pass: the Hittable children, their shapes and where their fields sit, the BVH leaves
    /// that reference them. Refuses anything this codec does not know how to scale correctly.
    /// </summary>
    internal static bool TryPlan(ReadOnlySpan<byte> data, out HitboxPlan plan, out string why)
    {
        plan = new HitboxPlan();
        if (data.Length < CompoundMinBytes) { why = "the collider blob is " + data.Length + " bytes; too short for a compound collider."; return false; }
        int type = data[Layout.ColliderHeaderTypeOffset];
        if (type != Layout.ColliderTypeCompound)
        {
            why = "the unit's collider is type " + type.ToString(CultureInfo.InvariantCulture)
                + ", not a compound collider (type " + Layout.ColliderTypeCompound.ToString(CultureInfo.InvariantCulture)
                + "); only compound hitboxes are supported.";
            return false;
        }

        if (!TryArray(data, Layout.CompoundColliderChildrenOffset, Layout.CompoundChildStride, out int childrenAt, out int childCount)
            || childCount == 0)
        { why = "the compound collider's child array is empty or out of bounds."; return false; }
        if (!TryArray(data, Layout.CompoundColliderBvhNodesOffset, BvhNodeStride, out int nodesAt, out int nodeCount)
            || nodeCount < 2)
        { why = "the compound collider's BVH node array is missing or out of bounds."; return false; }
        plan.NodesAt = nodesAt;
        plan.NodeCount = nodeCount;

        for (int i = 0; i < childCount; i++)
        {
            int childAt = childrenAt + i * Layout.CompoundChildStride;
            int offsetField = childAt + Layout.CompoundChildColliderOffset;
            long colliderAt = (long)offsetField + I32(data, offsetField);
            if (colliderAt < 0 || colliderAt + Layout.ConvexColliderHeaderFilterOffset + 4 > data.Length)
            { why = "compound child " + i + " points outside the blob."; return false; }
            int col = (int)colliderAt;
            uint belongsTo = (uint)I32(data, col + Layout.ConvexColliderHeaderFilterOffset + Layout.CollisionFilterBelongsToOffset);
            if ((belongsTo & (uint)Layout.CollisionFilterFlagsHittable) == 0) continue;

            int childType = data[col + Layout.ColliderHeaderTypeOffset];
            var child = new HitboxChild { Index = i, ColliderAt = col, Type = childType, BelongsTo = belongsTo };
            if (childType == Layout.ColliderTypeCapsule)
            {
                if (col + CapsuleBytes > data.Length) { why = "Hittable capsule " + i + " runs past the blob."; return false; }
                child.VertexCount = 2;
                child.VertexAt = new[] { col + Layout.CapsuleColliderVertex0Offset, col + Layout.CapsuleColliderVertex1Offset };
                child.RadiusAt = col + Layout.CapsuleColliderHullOffset + Layout.ConvexHullConvexRadiusOffset;
                child.HullAt = col + Layout.CapsuleColliderHullOffset;
            }
            else if (childType == Layout.ColliderTypeSphere)
            {
                if (col + SphereBytes > data.Length) { why = "Hittable sphere " + i + " runs past the blob."; return false; }
                child.VertexCount = 1;
                child.VertexAt = new[] { col + Layout.SphereColliderVertexOffset };
                child.RadiusAt = col + Layout.SphereColliderHullOffset + Layout.ConvexHullConvexRadiusOffset;
                child.HullAt = col + Layout.SphereColliderHullOffset;
            }
            else
            {
                why = "compound child " + i + " is on the Hittable layer but is collider type "
                    + childType.ToString(CultureInfo.InvariantCulture)
                    + "; only capsule (" + Layout.ColliderTypeCapsule.ToString(CultureInfo.InvariantCulture)
                    + ") and sphere (" + Layout.ColliderTypeSphere.ToString(CultureInfo.InvariantCulture) + ") hitboxes can be scaled.";
                return false;
            }

            child.Radius = F32(data, child.RadiusAt);
            if (!(child.Radius >= 0f) || float.IsInfinity(child.Radius))
            { why = "Hittable child " + i + " has an unreadable radius."; return false; }

            // The hull's vertex array must either BE the shape's vertices (a capsule's aliases
            // m_Vertex0/1, verified live) or hold exactly the same values; anything else is a layout
            // this codec has not seen, and scaling half of it would leave the two disagreeing.
            int hullArray = child.HullAt + Layout.ConvexHullVerticesOffset;
            if (!TryArray(data, hullArray, 12, out int hullAt, out int hullCount) || hullCount != child.VertexCount)
            { why = "Hittable child " + i + " has a hull vertex array that does not match its shape."; return false; }
            child.HullVerticesAt = hullAt;
            if (hullAt == child.VertexAt[0] && (child.VertexCount == 1 || child.VertexAt[1] == hullAt + 12))
                child.HullAliases = true;
            else
            {
                for (int k = 0; k < child.VertexCount; k++)
                    if (!ReadVec3(data, hullAt + k * 12).Equals(ReadVec3(data, child.VertexAt[k])))
                    { why = "Hittable child " + i + " has hull vertices that neither alias nor mirror its shape."; return false; }
                child.HullMirrors = true;
            }

            child.Transform = RigidTransform.Read(data, childAt + Layout.CompoundChildTransformOffset);
            if (!child.Transform.IsUsable)
            { why = "Hittable child " + i + " has an unreadable transform."; return false; }
            plan.Children.Add(child);
        }

        if (plan.Children.Count == 0) { why = "the compound collider has no child on the Hittable layer."; return false; }

        // Every node's flags must be leaf or internal, and every scaled child must sit in a leaf lane:
        // without that lane's box the bigger shape is never reached (verified live).
        var wanted = new HashSet<int>();
        foreach (var c in plan.Children) wanted.Add(c.Index);
        var found = new HashSet<int>();
        for (int n = 0; n < nodeCount; n++)
        {
            int at = nodesAt + n * BvhNodeStride;
            int flags = I32(data, at + Layout.BvhNodeFlagsOffset);
            if (flags != 0 && flags != BvhLeafFlag) { why = "BVH node " + n + " has flags " + flags + "; the node layout is not the one this codec knows."; return false; }
            if (flags != BvhLeafFlag) continue;
            bool holds = false;
            for (int lane = 0; lane < 4; lane++)
            {
                int child = I32(data, at + Layout.BvhNodeDataOffset + lane * 4);
                if (child == -1) continue;
                if (child < 0 || child >= childCount) { why = "BVH leaf " + n + " names child " + child + " of " + childCount + "."; return false; }
                if (wanted.Contains(child)) { found.Add(child); holds = true; }
            }
            if (holds) plan.LeafNodes.Add(n);
        }
        foreach (var c in plan.Children)
            if (!found.Contains(c.Index)) { why = "no BVH leaf holds Hittable child " + c.Index + "."; return false; }

        why = "";
        return true;
    }

    /// <summary>
    /// Grow internal node <paramref name="node"/>'s lanes to contain their subtrees; returns the
    /// node's union box. Internal lanes are valid when their data is not 0 (Unity.Physics
    /// <c>AreInternalsValid</c>), leaf lanes when not -1 (<c>AreLeavesValid</c>).
    /// </summary>
    private static Aabb Refit(Span<byte> data, HitboxPlan plan, int node, int depth, HashSet<int> visiting, ref int grown)
    {
        if (node <= 0 || node >= plan.NodeCount || depth > MaxBvhDepth || !visiting.Add(node)) return Aabb.Empty;
        int at = plan.NodesAt + node * BvhNodeStride;
        bool leaf = I32(data, at + Layout.BvhNodeFlagsOffset) == BvhLeafFlag;
        var union = Aabb.Empty;
        for (int lane = 0; lane < 4; lane++)
        {
            int d = I32(data, at + Layout.BvhNodeDataOffset + lane * 4);
            if (leaf ? d == -1 : d == 0) continue;
            var lanebox = ReadLane(data, at, lane);
            if (!leaf)
            {
                var sub = Refit(data, plan, d, depth + 1, visiting, ref grown);
                if (!sub.IsEmpty && !lanebox.Contains(sub))
                {
                    lanebox = lanebox.Union(sub);
                    WriteLane(data, at, lane, lanebox);
                    grown++;
                }
            }
            union = union.Union(lanebox);
        }
        visiting.Remove(node);
        return union;
    }

    private static Aabb ReadLane(ReadOnlySpan<byte> data, int nodeAt, int lane)
    {
        int b = nodeAt + Layout.BvhNodeBoundsOffset + lane * 4;
        return new Aabb(
            new Vec3(F32(data, b + Layout.FourTransposedAabbsLxOffset), F32(data, b + Layout.FourTransposedAabbsLyOffset), F32(data, b + Layout.FourTransposedAabbsLzOffset)),
            new Vec3(F32(data, b + Layout.FourTransposedAabbsHxOffset), F32(data, b + Layout.FourTransposedAabbsHyOffset), F32(data, b + Layout.FourTransposedAabbsHzOffset)));
    }

    private static void WriteLane(Span<byte> data, int nodeAt, int lane, Aabb box)
    {
        int b = nodeAt + Layout.BvhNodeBoundsOffset + lane * 4;
        PutF32(data, b + Layout.FourTransposedAabbsLxOffset, box.Min.X);
        PutF32(data, b + Layout.FourTransposedAabbsHxOffset, box.Max.X);
        PutF32(data, b + Layout.FourTransposedAabbsLyOffset, box.Min.Y);
        PutF32(data, b + Layout.FourTransposedAabbsHyOffset, box.Max.Y);
        PutF32(data, b + Layout.FourTransposedAabbsLzOffset, box.Min.Z);
        PutF32(data, b + Layout.FourTransposedAabbsHzOffset, box.Max.Z);
    }

    /// <summary>Absolute start and count of the self-relative BlobArray at <paramref name="at"/>, bounds-checked.</summary>
    private static bool TryArray(ReadOnlySpan<byte> data, int at, int stride, out int start, out int count)
    {
        start = 0;
        count = 0;
        if (at < 0 || at + BlobArrayWidth > data.Length) return false;
        int rel = I32(data, at + Layout.PhysicsBlobArrayOffsetOffset);
        int len = I32(data, at + Layout.PhysicsBlobArrayLengthOffset);
        if (len < 0) return false;
        if (len == 0) return true;
        long s = (long)at + rel;
        long end = s + (long)len * stride;
        if (s < 0 || end > data.Length) return false;
        start = (int)s;
        count = len;
        return true;
    }

    internal static int I32(ReadOnlySpan<byte> b, int at) => BinaryPrimitives.ReadInt32LittleEndian(b.Slice(at, 4));
    internal static float F32(ReadOnlySpan<byte> b, int at) => BitConverter.Int32BitsToSingle(I32(b, at));
    internal static void PutF32(Span<byte> b, int at, float v) => BinaryPrimitives.WriteInt32LittleEndian(b.Slice(at, 4), BitConverter.SingleToInt32Bits(v));
    internal static Vec3 ReadVec3(ReadOnlySpan<byte> b, int at) => new(F32(b, at), F32(b, at + 4), F32(b, at + 8));

    internal static void WriteVec3(Span<byte> b, int at, Vec3 v)
    {
        PutF32(b, at, v.X);
        PutF32(b, at + 4, v.Y);
        PutF32(b, at + 8, v.Z);
    }

    // ---- small value types (no Unity.Mathematics in the test build) ----------------------------

    internal readonly struct Vec3 : IEquatable<Vec3>
    {
        public Vec3(float x, float y, float z) { X = x; Y = y; Z = z; }
        public float X { get; }
        public float Y { get; }
        public float Z { get; }
        public static Vec3 operator +(Vec3 a, Vec3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static Vec3 operator -(Vec3 a, Vec3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static Vec3 operator *(Vec3 a, float s) => new(a.X * s, a.Y * s, a.Z * s);
        public static Vec3 Cross(Vec3 a, Vec3 b) => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
        public bool Equals(Vec3 o) => X.Equals(o.X) && Y.Equals(o.Y) && Z.Equals(o.Z);
        public override bool Equals(object? obj) => obj is Vec3 v && Equals(v);
        public override int GetHashCode() => HashCode.Combine(X, Y, Z);
        public override string ToString() => "(" + X.ToString("0.###", CultureInfo.InvariantCulture) + ", "
            + Y.ToString("0.###", CultureInfo.InvariantCulture) + ", " + Z.ToString("0.###", CultureInfo.InvariantCulture) + ")";
    }

    /// <summary><c>Unity.Mathematics.RigidTransform</c>: quaternion (x, y, z, w) then float3 position.</summary>
    internal readonly struct RigidTransform
    {
        public RigidTransform(float qx, float qy, float qz, float qw, Vec3 pos) { Q = new Vec3(qx, qy, qz); W = qw; Pos = pos; }
        public Vec3 Q { get; }
        public float W { get; }
        public Vec3 Pos { get; }

        public static RigidTransform Read(ReadOnlySpan<byte> b, int at)
        {
            int r = at + Layout.RigidTransformRotOffset;
            return new RigidTransform(F32(b, r), F32(b, r + 4), F32(b, r + 8), F32(b, r + 12), ReadVec3(b, at + Layout.RigidTransformPosOffset));
        }

        /// <summary>A unit quaternion (within float noise) and a finite position.</summary>
        public bool IsUsable
        {
            get
            {
                float n = Q.X * Q.X + Q.Y * Q.Y + Q.Z * Q.Z + W * W;
                return Math.Abs(n - 1f) < 1e-3f && float.IsFinite(Pos.X) && float.IsFinite(Pos.Y) && float.IsFinite(Pos.Z);
            }
        }

        /// <summary>Child space to compound space: pos + rotate(q, v).</summary>
        public Vec3 Apply(Vec3 v) => Pos + Rotate(Q, W, v);

        /// <summary>Compound space to child space: rotate(conjugate(q), p - pos).</summary>
        public Vec3 InverseApply(Vec3 p) => Rotate(Q * -1f, W, p - Pos);

        private static Vec3 Rotate(Vec3 q, float w, Vec3 v)
        {
            var t = Vec3.Cross(q, v) * 2f;
            return v + t * w + Vec3.Cross(q, t);
        }
    }

    internal readonly struct Aabb
    {
        public Aabb(Vec3 min, Vec3 max) { Min = min; Max = max; }
        public Vec3 Min { get; }
        public Vec3 Max { get; }
        public static Aabb Empty => new(new Vec3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity),
                                        new Vec3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity));
        public bool IsEmpty => !(Min.X <= Max.X && Min.Y <= Max.Y && Min.Z <= Max.Z);

        public Aabb Include(Vec3 p, float r) => new(
            new Vec3(Math.Min(Min.X, p.X - r), Math.Min(Min.Y, p.Y - r), Math.Min(Min.Z, p.Z - r)),
            new Vec3(Math.Max(Max.X, p.X + r), Math.Max(Max.Y, p.Y + r), Math.Max(Max.Z, p.Z + r)));

        public Aabb Union(Aabb o)
        {
            if (o.IsEmpty) return this;
            if (IsEmpty) return o;
            return new Aabb(
                new Vec3(Math.Min(Min.X, o.Min.X), Math.Min(Min.Y, o.Min.Y), Math.Min(Min.Z, o.Min.Z)),
                new Vec3(Math.Max(Max.X, o.Max.X), Math.Max(Max.Y, o.Max.Y), Math.Max(Max.Z, o.Max.Z)));
        }

        public bool Contains(Aabb o) =>
            !IsEmpty && Min.X <= o.Min.X && Min.Y <= o.Min.Y && Min.Z <= o.Min.Z
            && Max.X >= o.Max.X && Max.Y >= o.Max.Y && Max.Z >= o.Max.Z;

        /// <summary>Distance from the compound origin to the box's farthest corner.</summary>
        public float FarthestCornerDistance()
        {
            float x = Math.Max(Math.Abs(Min.X), Math.Abs(Max.X));
            float y = Math.Max(Math.Abs(Min.Y), Math.Abs(Max.Y));
            float z = Math.Max(Math.Abs(Min.Z), Math.Abs(Max.Z));
            return (float)Math.Sqrt(x * x + y * y + z * z);
        }
    }

    internal sealed class HitboxChild
    {
        public int Index;
        public int ColliderAt;
        public int Type;
        public uint BelongsTo;
        public int VertexCount;
        public int[] VertexAt = Array.Empty<int>();
        public int RadiusAt;
        public int HullAt;
        public int HullVerticesAt;
        public bool HullAliases;
        public bool HullMirrors;
        public float Radius;
        public RigidTransform Transform;
    }

    internal sealed class HitboxPlan
    {
        public List<HitboxChild> Children { get; } = new();
        public List<int> LeafNodes { get; } = new();
        public int NodesAt;
        public int NodeCount;
    }
}
