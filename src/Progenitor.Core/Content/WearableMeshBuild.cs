using System;
using System.Collections.Generic;

namespace Progenitor.Content;

/// <summary>
/// Turns a <see cref="WearableGraphics"/> sketch and a bone pose into triangles.
/// No Unity types: the client copies the arrays onto a <c>SkinnedMeshRenderer</c>, and
/// the tests check the robe, the gold tassels and the black face without a game.
///
/// <para>Joint names follow the character rig (<c>Spine_JNT</c> and the aliases below).
/// A missing bone drops that shape and is listed on the mesh; the other shapes still build.</para>
/// </summary>
internal static partial class WearableMeshBuild
{
    private const int Segments = 16;

    // Some body skin palettes omit control joints but retain co-located roll01 pivots.
    // Procedural fallback uses the actual roll bone and its bind matrix; it does not
    // reconstruct the omitted control or promise identical twist/full retargeting.
    // Exact requested names always win; roll fallbacks follow existing rig aliases.
    private static readonly (string Name, string[] Aliases)[] Aliases =
    {
        // Spine_JNT is the low back on the live vampire rig; Chest_JNT / Neck_JNT sit
        // higher. Do not alias Chest onto Spine or the bodice collapses to a waist tube.
        ("Spine_JNT", new[] { "Gut_JNT", "Spine1_JNT", "Spine2_JNT", "SpineVPJ_JNT" }),
        ("Chest_JNT", new[] { "Ribcage_JNT", "ChestReference_JNT" }),
        ("Neck_JNT", new[] { "Neck02_JNT", "Neck01_JNT", "NeckBase_JNT" }),
        ("Pelvis_JNT", new[] { "Hips_JNT", "Hip_JNT" }),
        ("Head_JNT", new[] { "HeadIKTarget_JNT" }),
        ("Left_Shoulder_JNT", new[] { "Left_Clavicle_JNT", "Left_Arm_JNT", "Left_UpperArm_JNT", "Left_Shoulder_roll01_JNT" }),
        ("Right_Shoulder_JNT", new[] { "Right_Clavicle_JNT", "Right_Arm_JNT", "Right_UpperArm_JNT", "Right_Shoulder_roll01_JNT" }),
        ("Left_Elbow_JNT", new[] { "Left_Elbow_JNT", "Left_Elbow_roll01_JNT" }),
        ("Right_Elbow_JNT", new[] { "Right_Elbow_JNT", "Right_Elbow_roll01_JNT" }),
        ("Left_Wrist_JNT", new[] { "Left_Hand_JNT" }),
        ("Right_Wrist_JNT", new[] { "Right_Hand_JNT" }),
        ("Left_Ankle_JNT", new[] { "Left_Foot_JNT" }),
        ("Right_Ankle_JNT", new[] { "Right_Foot_JNT" }),
        ("Left_Hip_JNT", new[] { "Left_Hip_JNT", "Left_UpLeg_JNT", "Left_Thigh_JNT", "Left_Hip_roll01_JNT" }),
        ("Right_Hip_JNT", new[] { "Right_Hip_JNT", "Right_UpLeg_JNT", "Right_Thigh_JNT", "Right_Hip_roll01_JNT" }),
        ("Left_Knee_JNT", new[] { "Left_Knee_JNT", "Left_Knee_roll01_JNT" }),
        ("Right_Knee_JNT", new[] { "Right_Knee_JNT", "Right_Knee_roll01_JNT" }),
        ("Left_Ball_JNT", new[] { "Left_Ball_JNT" }),
        ("Right_Ball_JNT", new[] { "Right_Ball_JNT" }),
    };

    internal static WearableMesh Build(WearableGraphics graphics, IReadOnlyDictionary<string, WearableVec3> pose, IReadOnlyList<WearableVec3>? torso = null,
        IReadOnlyList<WearableSkinSample>? bodySamples = null)
    {
        if (graphics == null) throw new ArgumentNullException(nameof(graphics));
        if (pose == null) throw new ArgumentNullException(nameof(pose));
        var acc = new Acc();
        var missing = new List<string>();
        var forward = Forward(pose);
        float span = ShoulderSpan(pose);
        foreach (var part in graphics.Parts)
        {
            if (!acc.TryBone(pose, part.Bone, out _, out _))
            {
                NoteMissing(missing, part.Bone);
                continue;
            }
            switch (part.Shape)
            {
                case WearableShape.Loft:
                    Loft(acc, pose, part, span, missing);
                    break;
                case WearableShape.Ring:
                    if (!FittedWaistBelt(acc, pose, part, span, forward, torso))
                        Ring(acc, pose, part.Bone, Radius(part.A, span, 0.65f), part.C, part.Surface, forward);
                    break;
                case WearableShape.Disc:
                    Disc(acc, pose, part.Bone, part.A, part.C, part.Surface, forward);
                    break;
                case WearableShape.Tassels:
                    Tassels(acc, pose, part, span, forward);
                    break;
                case WearableShape.Cap:
                    Cap(acc, pose, part, missing);
                    break;
                case WearableShape.Pole:
                    Pole(acc, pose, part, forward);
                    break;
                case WearableShape.Cowl:
                    TailoredCowl(acc, pose, part, forward);
                    break;
                case WearableShape.RobeBody:
                    RobeBody(acc, pose, part.Surface, span, forward, missing);
                    break;
                case WearableShape.Shoe:
                    Shoe(acc, pose, part, missing);
                    break;
                case WearableShape.Skirt:
                {
                    int start = acc.ClothIndexCount;
                    TailoredSkirt(acc, pose, part.Surface, span, forward, missing, torso);
                    acc.MarkSkirt(start);
                    break;
                }
                case WearableShape.Glove:
                    TailoredGlove(acc, pose, part, missing, bodySamples);
                    break;
                case WearableShape.Bodice:
                    TailoredBodice(acc, pose, part.Surface, span, forward, missing, torso);
                    break;
                case WearableShape.Sleeve:
                    TailoredSleeve(acc, pose, part, span, missing, bodySamples);
                    break;
            }
        }
        return acc.Finish(missing);
    }

    private static void Loft(Acc acc, IReadOnlyDictionary<string, WearableVec3> pose, WearablePart part, float span, List<string> missing)
    {
        if (!acc.TryBone(pose, part.BoneTo, out _, out var far))
        {
            NoteMissing(missing, part.BoneTo);
            return;
        }
        if (!acc.TryBone(pose, part.Bone, out int bone, out var near)) return;
        // A short drop is the bodice (shoulders to waist). A long drop is the skirt
        // (waist to the ankle height). Fit radii follow that, so the skirt starts
        // where the bodice ends.
        float drop = MathF.Abs(near.Y - far.Y);
        bool skirt = drop > 0.55f;
        float r0 = part.A < 0f ? TorsoRadius(part.Bone, span, skirt ? 0.50f : 0.40f) : part.A;
        float r1 = part.B < 0f ? TorsoRadius(part.BoneTo, span, skirt ? 0.78f : 0.50f) : part.B;
        var forward = Forward(pose);
        // A robe stays on the start bone's horizontal position so the hem does not chase a foot.
        // A sleeve (followEnd) is a tube along the limb, rings perpendicular to that bone.
        var farCenter = part.FollowEnd ? far : new WearableVec3(near.X, far.Y, near.Z);
        WearableVec3[] top;
        WearableVec3[] bottom;
        if (part.FollowEnd)
        {
            var axis = farCenter - near;
            top = RingAround(near, r0, axis);
            bottom = RingAround(farCenter, r1, axis);
        }
        else
        {
            top = RingPoints(near, r0, 0f, forward);
            bottom = RingPoints(farCenter, r1, 0f, forward);
        }
        int farBone = bone;
        if (part.FollowEnd && acc.TryBone(pose, part.BoneTo, out int endBone, out _)) farBone = endBone;
        Connect(acc, top, bottom, bone, farBone, part.Surface, forward);
    }

    // A shared joint has the same fitted radius whichever end of a loft it occupies.
    private static float TorsoRadius(string joint, float span, float fallback) => span * (joint switch
    {
        "Neck_JNT" => 0.24f,
        "Chest_JNT" => 0.58f,
        "Pelvis_JNT" => 0.65f,
        _ => fallback,
    });

    private static void RobeBody(Acc acc, IReadOnlyDictionary<string, WearableVec3> pose,
        WearableSurface surface, float span, WearableVec3 forward, List<string> missing)
    {
        string[] names = { "Neck_JNT", "Chest_JNT", "Pelvis_JNT", "Left_Knee_JNT",
            "Right_Knee_JNT", "Left_Ankle_JNT", "Right_Ankle_JNT", "Left_Shoulder_JNT", "Right_Shoulder_JNT" };
        var joints = new WearableVec3[names.Length];
        var bones = new int[names.Length];
        bool complete = true;
        for (int i = 0; i < names.Length; i++)
            if (!acc.TryBone(pose, names[i], out bones[i], out joints[i]))
            {
                NoteMissing(missing, names[i]);
                complete = false;
            }
        if (!complete) return;

        // One surface and one set of indices at every seam. The lower rings follow
        // both legs with continuous weights instead of splitting into two trouser legs.
        // Chest_JNT is below the bust on the vampire rig. Keep a separate shoulder
        // ring rather than narrowing straight from that low joint to the neck.
        var centers = new[] { joints[0], (joints[7] + joints[8]) * 0.5f, joints[1], joints[2],
            (joints[3] + joints[4]) * 0.5f, (joints[5] + joints[6]) * 0.5f };
        float[] radii = { span * 0.24f, span * 0.75f, span * 0.58f, span * 0.65f,
            MathF.Max(span * 0.68f, (joints[4] - joints[3]).Length() * 0.5f + span * 0.20f),
            MathF.Max(span * 0.80f, (joints[6] - joints[5]).Length() * 0.5f + span * 0.20f) };
        var right = Right(forward);
        int[]? previous = null;
        for (int ring = 0; ring < centers.Length; ring++)
        {
            var indices = new int[Segments];
            int left = ring < 4 ? bones[ring == 0 ? 0 : ring == 3 ? 2 : 1] : bones[ring == 4 ? 3 : 5];
            int rightBone = ring < 4 ? left : bones[ring == 4 ? 4 : 6];
            for (int i = 0; i < Segments; i++)
            {
                float angle = i * 2f * MathF.PI / Segments;
                float lateral = MathF.Cos(angle);
                var offset = right * (lateral * radii[ring]) + forward * (MathF.Sin(angle) * radii[ring]);
                float weightRight = ring < 4 ? 0f : (lateral + 1f) * 0.5f;
                indices[i] = acc.Vert(centers[ring] + offset, offset.Normalized(), left, rightBone, weightRight);
            }
            if (previous != null) ConnectIndices(acc, previous, indices, surface, reverse: true);
            previous = indices;
        }
    }

    private static void Shoe(Acc acc, IReadOnlyDictionary<string, WearableVec3> pose,
        WearablePart part, List<string> missing)
    {
        if (!acc.TryBone(pose, part.Bone, out int bone, out var ankle)) return;
        if (!acc.TryBone(pose, part.BoneTo, out _, out var ball))
        {
            NoteMissing(missing, part.BoneTo);
            return;
        }
        var delta = ball - ankle;
        var horizontal = new WearableVec3(delta.X, 0f, delta.Z);
        if (horizontal.Length() < 1e-5f) return;
        var forward = horizontal.Normalized();
        var right = Right(forward);
        var up = new WearableVec3(0f, 1f, 0f);
        float radius = part.A;
        float sole = MathF.Min(ankle.Y, ball.Y) - radius * 0.25f;
        var heel = ankle - forward * (radius * 0.65f);
        var toe = ball + forward * (radius * 0.85f);
        float[] heights = { ankle.Y + radius * 0.70f, ball.Y + radius * 0.85f, ball.Y + radius * 0.50f };
        var centers = new[] { heel, ball, toe };
        float[] widths = { radius * 0.85f, radius, radius * 0.65f };
        int[]? previous = null;
        for (int ring = 0; ring < centers.Length; ring++)
        {
            float halfHeight = (heights[ring] - sole) * 0.5f;
            var center = new WearableVec3(centers[ring].X, sole + halfHeight, centers[ring].Z);
            var indices = new int[Segments];
            for (int i = 0; i < Segments; i++)
            {
                float angle = i * 2f * MathF.PI / Segments;
                var offset = right * (MathF.Cos(angle) * widths[ring]) + up * (MathF.Sin(angle) * halfHeight);
                indices[i] = acc.Vert(center + offset, offset.Normalized(), bone);
            }
            if (previous != null) ConnectIndices(acc, previous, indices, part.Surface, reverse: true);
            if (ring == 0 || ring == centers.Length - 1)
            {
                int c = acc.Vert(center, forward, bone);
                for (int i = 0; i < Segments; i++)
                {
                    int next = (i + 1) % Segments;
                    if (ring == 0) acc.Tri(part.Surface, c, indices[next], indices[i]);
                    else acc.Tri(part.Surface, c, indices[i], indices[next]);
                }
            }
            previous = indices;
        }
    }

    private static void Ring(Acc acc, IReadOnlyDictionary<string, WearableVec3> pose, string boneName, float radius, float yOffset, WearableSurface surface, WearableVec3 forward)
    {
        if (!acc.TryBone(pose, boneName, out int bone, out var at)) return;
        string? proximal = boneName.EndsWith("Wrist_JNT", StringComparison.Ordinal) ? boneName.Replace("Wrist_JNT", "Elbow_JNT")
            : boneName.EndsWith("Ankle_JNT", StringComparison.Ordinal) ? boneName.Replace("Ankle_JNT", "Knee_JNT") : null;
        if (proximal != null && Try(pose, proximal, out var away))
        {
            var axis = (at - away).Normalized();
            var center = at - axis * yOffset;
            Connect(acc, RingAround(center, radius, axis), RingAround(center - axis * 0.025f, radius + 0.006f, axis),
                bone, bone, surface, forward);
            return;
        }
        var a = RingPoints(at, radius, yOffset, forward);
        var b = RingPoints(at, radius + 0.012f, yOffset + 0.025f, forward);
        Connect(acc, a, b, bone, bone, surface, forward);
    }

    private static void Disc(Acc acc, IReadOnlyDictionary<string, WearableVec3> pose, string boneName, float radius, float forwardDist, WearableSurface surface, WearableVec3 forward)
    {
        if (!acc.TryBone(pose, boneName, out int bone, out var at)) return;
        var center = at + forward * forwardDist;
        var right = Right(forward);
        var up = new WearableVec3(0f, 1f, 0f);
        int c = acc.Vert(center, forward, bone);
        int first = -1, prev = -1;
        for (int i = 0; i <= Segments; i++)
        {
            float ang = i * MathF.PI * 2f / Segments;
            var p = center + right * (MathF.Cos(ang) * radius) + up * (MathF.Sin(ang) * radius);
            int v = acc.Vert(p, forward, bone);
            if (i == 0) first = v;
            else acc.Tri(surface, c, prev, v);
            prev = v;
        }
        _ = first;
    }

    private static void Tassels(Acc acc, IReadOnlyDictionary<string, WearableVec3> pose, WearablePart part, float span, WearableVec3 forward)
    {
        if (!acc.TryBone(pose, part.Bone, out int bone, out var at)) return;
        at += new WearableVec3(0f, part.C, 0f);
        if (part.A < 0f && part.Bone == "Pelvis_JNT"
            && acc.InlayTassels(at, forward, span, part.N, part.B, part.Surface)) return;
        float radius = Radius(part.A, span, 0.67f);
        var right = Right(forward);
        int count = Math.Max(1, part.N);
        float length = part.B;
        for (int i = 0; i < count; i++)
        {
            float u = count == 1 ? 0f : (i / (float)(count - 1)) * 2f - 1f;
            float ang = u * 0.7f;
            var origin = at + forward * (MathF.Cos(ang) * radius) + right * (MathF.Sin(ang) * radius);
            var down = new WearableVec3(0f, -1f, 0f);
            var side = right * 0.012f;
            int a = acc.Vert(origin, forward, bone);
            int b = acc.Vert(origin + side, forward, bone);
            int c = acc.Vert(origin + down * length, forward, bone);
            int d = acc.Vert(origin + side + down * length, forward, bone);
            acc.Tri(part.Surface, a, c, b);
            acc.Tri(part.Surface, b, c, d);
        }
    }

    private static void Cap(Acc acc, IReadOnlyDictionary<string, WearableVec3> pose, WearablePart part, List<string> missing)
    {
        if (!acc.TryBone(pose, part.Bone, out int bone, out var at)) return;
        if (!acc.TryBone(pose, part.BoneTo, out _, out var away))
        {
            NoteMissing(missing, part.BoneTo);
            return;
        }
        var dir = (at - away).Normalized();
        var end = at + dir * part.B;
        var forward = Forward(pose);
        var top = RingAround(at, part.A, dir);
        var bottom = RingAround(end, part.A * 0.85f, dir);
        Connect(acc, top, bottom, bone, bone, part.Surface, forward);
    }

    private static void Pole(Acc acc, IReadOnlyDictionary<string, WearableVec3> pose, WearablePart part, WearableVec3 forward)
    {
        if (!acc.TryBone(pose, part.Bone, out int bone, out var at)) return;
        var end = at + new WearableVec3(0f, part.B, 0f) + forward * part.C;
        var axis = end - at;
        var top = RingAround(at, part.A, axis);
        var bottom = RingAround(end, part.A, axis);
        Connect(acc, top, bottom, bone, bone, part.Surface, forward);
    }

    private static WearableVec3[] RingPoints(WearableVec3 center, float radius, float yOffset, WearableVec3 forward)
    {
        var right = Right(forward);
        var up = new WearableVec3(0f, 1f, 0f);
        var c = center + up * yOffset;
        var pts = new WearableVec3[Segments];
        for (int i = 0; i < Segments; i++)
        {
            float ang = i * MathF.PI * 2f / Segments;
            pts[i] = c + right * (MathF.Cos(ang) * radius) + forward * (MathF.Sin(ang) * radius);
        }
        return pts;
    }

    /// <summary>A ring in the plane perpendicular to <paramref name="axis"/>, so a sleeve or glove follows the bone.</summary>
    private static WearableVec3[] RingAround(WearableVec3 center, float radius, WearableVec3 axis)
    {
        var up = axis.Length() < 1e-4f ? new WearableVec3(0f, 1f, 0f) : axis.Normalized();
        var hint = MathF.Abs(up.Y) > 0.9f ? new WearableVec3(0f, 0f, 1f) : new WearableVec3(0f, 1f, 0f);
        var right = Cross(up, hint).Normalized();
        var forward = Cross(right, up).Normalized();
        var pts = new WearableVec3[Segments];
        for (int i = 0; i < Segments; i++)
        {
            float ang = i * MathF.PI * 2f / Segments;
            pts[i] = center + right * (MathF.Cos(ang) * radius) + forward * (MathF.Sin(ang) * radius);
        }
        return pts;
    }

    private static void Connect(Acc acc, WearableVec3[] a, WearableVec3[] b, int boneA, int boneB, WearableSurface surface, WearableVec3 forward)
    {
        int n = a.Length;
        var ia = new int[n];
        var ib = new int[n];
        for (int i = 0; i < n; i++)
        {
            ia[i] = acc.Vert(a[i], (a[i] - Average(a)).Normalized(), boneA);
            ib[i] = acc.Vert(b[i], (b[i] - Average(b)).Normalized(), boneB);
        }
        var normal = Cross(b[0] - a[0], a[1] - a[0]);
        var outward = a[0] - Average(a);
        bool reverse = normal.X * outward.X + normal.Y * outward.Y + normal.Z * outward.Z < 0f;
        ConnectIndices(acc, ia, ib, surface, reverse);
        _ = forward;
    }

    private static void ConnectIndices(Acc acc, int[] ia, int[] ib, WearableSurface surface, bool reverse = false)
    {
        for (int i = 0; i < ia.Length; i++)
        {
            int j = (i + 1) % ia.Length;
            if (reverse)
            {
                acc.Tri(surface, ia[i], ia[j], ib[i]);
                acc.Tri(surface, ia[j], ib[j], ib[i]);
            }
            else
            {
                acc.Tri(surface, ia[i], ib[i], ia[j]);
                acc.Tri(surface, ia[j], ib[i], ib[j]);
            }
        }
    }

    private static WearableVec3 Average(WearableVec3[] pts)
    {
        float x = 0f, y = 0f, z = 0f;
        for (int i = 0; i < pts.Length; i++) { x += pts[i].X; y += pts[i].Y; z += pts[i].Z; }
        float n = pts.Length;
        return new WearableVec3(x / n, y / n, z / n);
    }

    private static float Radius(float requested, float span, float fraction) =>
        requested < 0f ? MathF.Max(0.08f, span * fraction) : requested;

    private static float ShoulderSpan(IReadOnlyDictionary<string, WearableVec3> pose)
    {
        if (Try(pose, "Left_Shoulder_JNT", out var l) && Try(pose, "Right_Shoulder_JNT", out var r))
            return MathF.Max(0.2f, (r - l).Length());
        return 0.36f;
    }

    private static WearableVec3 Forward(IReadOnlyDictionary<string, WearableVec3> pose)
    {
        if (Try(pose, "Left_Shoulder_JNT", out var l) && Try(pose, "Right_Shoulder_JNT", out var r))
        {
            var right = (r - l).Normalized();
            var forward = Cross(right, new WearableVec3(0f, 1f, 0f)).Normalized();
            if (forward.Length() > 0.5f) return forward;
        }
        return new WearableVec3(0f, 0f, 1f);
    }

    private static WearableVec3 Right(WearableVec3 forward) =>
        Cross(new WearableVec3(0f, 1f, 0f), forward).Normalized();

    private static WearableVec3 Cross(WearableVec3 a, WearableVec3 b) =>
        new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

    private static bool Try(IReadOnlyDictionary<string, WearableVec3> pose, string canonical, out WearableVec3 pos)
    {
        if (pose.TryGetValue(canonical, out pos)) return true;
        for (int i = 0; i < Aliases.Length; i++)
        {
            if (!string.Equals(Aliases[i].Name, canonical, StringComparison.Ordinal)) continue;
            var names = Aliases[i].Aliases;
            for (int a = 0; a < names.Length; a++)
                if (pose.TryGetValue(names[a], out pos)) return true;
        }
        pos = default;
        return false;
    }

    private static void NoteMissing(List<string> missing, string name)
    {
        if (string.IsNullOrEmpty(name)) return;
        if (!missing.Contains(name)) missing.Add(name);
    }

    private sealed partial class Acc
    {
        private readonly List<WearableVec3> _verts = new();
        private readonly List<int> _bones = new();
        private readonly List<int> _bones2 = new();
        private readonly List<float> _weights2 = new();
        private readonly List<string> _boneNames = new();
        private readonly Dictionary<string, int> _boneId = new(StringComparer.Ordinal);
        private readonly List<int> _cloth = new();
        private readonly List<int> _gold = new();
        private readonly List<int> _void = new();

        internal bool TryBone(IReadOnlyDictionary<string, WearableVec3> pose, string name, out int index, out WearableVec3 pos)
        {
            index = 0;
            if (!Try(pose, name, out pos)) return false;
            string resolved = pose.ContainsKey(name) ? name : ResolvedName(pose, name);
            if (!_boneId.TryGetValue(resolved, out index))
            {
                index = _boneNames.Count;
                _boneNames.Add(resolved);
                _boneId[resolved] = index;
            }
            return true;
        }

        private static string ResolvedName(IReadOnlyDictionary<string, WearableVec3> pose, string canonical)
        {
            for (int i = 0; i < Aliases.Length; i++)
            {
                if (!string.Equals(Aliases[i].Name, canonical, StringComparison.Ordinal)) continue;
                var names = Aliases[i].Aliases;
                for (int a = 0; a < names.Length; a++)
                    if (pose.ContainsKey(names[a])) return names[a];
            }
            return canonical;
        }

        internal int Vert(WearableVec3 p, WearableVec3 normal, int bone, int bone2 = 0, float weight2 = 0f)
        {
            _ = normal;
            _verts.Add(p);
            _bones.Add(bone);
            _bones2.Add(bone2);
            _weights2.Add(weight2);
            return _verts.Count - 1;
        }

        internal void Tri(WearableSurface surface, int a, int b, int c)
        {
            var list = surface switch
            {
                WearableSurface.Gold => _gold,
                WearableSurface.Void => _void,
                _ => _cloth,
            };
            list.Add(a);
            list.Add(b);
            list.Add(c);
        }

        internal WearableMesh Finish(List<string> missing) =>
            new(
                _verts.ToArray(),
                _bones.ToArray(),
                _bones2.ToArray(),
                _weights2.ToArray(),
                _boneNames.ToArray(),
                _cloth.ToArray(),
                _gold.ToArray(),
                _void.ToArray(),
                missing.ToArray());
    }
}
