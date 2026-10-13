using System;
using System.Collections.Generic;

namespace Progenitor.Content;

internal static partial class WearableMeshBuild
{
    private static void TailoredGlove(Acc acc, IReadOnlyDictionary<string, WearableVec3> pose,
        WearablePart part, List<string> missing, IReadOnlyList<WearableSkinSample>? bodySamples = null)
    {
        if (!acc.TryBone(pose, part.Bone, out int wristBone, out var wrist)) return;
        if (!acc.TryBone(pose, part.BoneTo, out int elbowBone, out var elbow))
        {
            NoteMissing(missing, part.BoneTo);
            return;
        }
        string side = part.Bone.StartsWith("Left_", StringComparison.Ordinal) ? "Left_" : "Right_";
        string[] fingers = { "Index", "Middle", "Ring", "Pinky", "Thumb" };
        var joints = new WearableVec3[5][];
        var bones = new int[5][];
        bool complete = true;
        for (int f = 0; f < fingers.Length; f++)
        {
            joints[f] = new WearableVec3[3];
            bones[f] = new int[3];
            for (int j = 0; j < 3; j++)
            {
                string name = side + fingers[f] + "0" + (j + 1) + "_JNT";
                // The shipped rig spells only the thumb's middle joint "Thum".
                if (f == 4 && j == 1 && pose.ContainsKey(side + "Thum02_JNT"))
                    name = side + "Thum02_JNT";
                if (!acc.TryBone(pose, name, out bones[f][j], out joints[f][j]))
                {
                    NoteMissing(missing, name);
                    complete = false;
                }
            }
        }
        if (!complete) return;

        // Build an original palm envelope from the knuckles, in the hand's own
        // plane. Neither world-up nor the arm direction describes a rotated palm.
        var knuckles = (joints[0][0] + joints[1][0] + joints[2][0] + joints[3][0]) * 0.25f;
        var along = (knuckles - wrist).Normalized();
        var across = GlovePerpendicular(joints[0][0] - joints[3][0], along);
        var normal = Cross(along, across).Normalized();
        float low = float.PositiveInfinity, high = float.NegativeInfinity;
        float thickness = 0.026f;
        for (int f = 0; f < 4; f++)
        {
            float lateral = GloveDot(joints[f][0] - knuckles, across);
            low = MathF.Min(low, lateral);
            high = MathF.Max(high, lateral);
            thickness = MathF.Max(thickness, MathF.Abs(GloveDot(joints[f][0] - knuckles, normal)) + 0.020f);
        }
        var palmEnd = knuckles + across * ((low + high) * 0.5f) + along * 0.012f;
        float width = MathF.Max(0.042f, (high - low) * 0.5f + 0.025f);
        var armSamples = GloveSamples(bodySamples, side, null);
        // Keep the palm contour separate from digit-dominant webbing. Including that
        // skin in every palm support slab turns the thumb base into a bulky mitten.
        var palmSamples = armSamples;
        var armAxis = (wrist - elbow).Normalized();
        var armAcross = GlovePerpendicular(across, armAxis);
        var armNormal = Cross(armAxis, armAcross).Normalized();
        float armLength = (wrist - elbow).Length();
        float[] cuffStations = { 0f, .12f, .24f, .36f, .48f, .60f, .72f, .84f, .94f, .98f, 1f };
        WearableVec3[]? cuffPrevious = null;
        int[]? cuffPreviousIndices = null;
        for (int r = 0; r < cuffStations.Length; r++)
        {
            float t = cuffStations[r];
            var center = elbow + (wrist - elbow) * t;
            var ring = GloveFittedRing(center, armAcross, armNormal, armAxis,
                .05f + (.042f - .05f) * t, .05f + (.042f - .05f) * t,
                armSamples, armLength * .13f, .006f);
            var indices = GloveVertices(acc, ring, elbowBone, wristBone, t);
            if (cuffPrevious != null && cuffPreviousIndices != null)
            {
                var surface = r == 9 && part.N > 0 ? (WearableSurface)(part.N - 1) : part.Surface;
                GloveConnect(acc, cuffPrevious, ring, cuffPreviousIndices, indices, surface);
            }
            cuffPrevious = ring;
            cuffPreviousIndices = indices;
        }
        // The wrist contour is shared with the forearm: no separate cuff tube seam.
        var palmStart = wrist + (palmEnd - wrist) * 0.22f;
        var palmMiddle = wrist + (palmEnd - wrist) * 0.55f;
        float palmLength = (palmEnd - wrist).Length();
        // A hand turned against the forearm has a convex wrist on its inside. A
        // single long arm-to-palm span cuts through that skin even when both end
        // contours fit. Support the bend locally before the palm widens.
        var bridgeCenter = wrist + (palmEnd - wrist) * 0.08f;
        var bridgeAxis = (armAxis + along).Normalized();
        if (bridgeAxis.Length() < .0001f) bridgeAxis = along;
        var bridgeAcross = GlovePerpendicular(across, bridgeAxis);
        var bridgeNormal = Cross(bridgeAxis, bridgeAcross).Normalized();
        var bridgeRing = GloveFittedRing(bridgeCenter, bridgeAcross, bridgeNormal, bridgeAxis,
            .042f, .042f, palmSamples, palmLength * .28f, .008f);
        var startRing = GloveFittedRing(palmStart, across, normal, along, width * 0.88f, thickness * 1.08f,
            palmSamples, palmLength * .32f, .006f);
        var middleRing = GloveFittedRing(palmMiddle, across, normal, along, width * 0.94f, thickness * 1.12f,
            palmSamples, palmLength * .34f, .0045f);
        var endRing = GloveFittedRing(palmEnd, across, normal, along, width, thickness,
            palmSamples, palmLength * .34f, .0045f);
        var bridgeIndices = GloveVertices(acc, bridgeRing, wristBone);
        var startIndices = GloveVertices(acc, startRing, wristBone);
        var middleIndices = GloveVertices(acc, middleRing, wristBone);
        var endIndices = GloveVertices(acc, endRing, wristBone);
        GloveConnect(acc, cuffPrevious!, bridgeRing, cuffPreviousIndices!, bridgeIndices, part.Surface);
        GloveConnect(acc, bridgeRing, startRing, bridgeIndices, startIndices, part.Surface);
        GloveConnect(acc, startRing, middleRing, startIndices, middleIndices, part.Surface);
        GloveConnect(acc, middleRing, endRing, middleIndices, endIndices, part.Surface);
        GloveCap(acc, endRing, endIndices, palmEnd, along, wristBone, part.Surface);

        // Each digit is a separate closed finger envelope overlapping the palm.
        // Joint rings are shared by adjacent sections and follow their own joint;
        // this avoids cracks when the character grips a weapon or curls a finger.
        for (int f = 0; f < fingers.Length; f++)
        {
            var points = joints[f];
            var first = (points[1] - points[0]).Normalized();
            var last = (points[2] - points[1]).Normalized();
            float lastLength = MathF.Max(0.012f, (points[2] - points[1]).Length());
            float radius = f == 4 ? 0.018f : Math.Clamp((points[1] - points[0]).Length() * 0.32f + 0.005f, 0.014f, 0.022f);
            var fingerSamples = GloveSamples(bodySamples, side, fingers[f]);
            float tipLength = lastLength * 0.60f + 0.005f;
            if (fingerSamples.Count >= 4)
            {
                tipLength = .0025f;
                foreach (var sample in fingerSamples)
                    tipLength = MathF.Max(tipLength, GloveDot(sample - points[2], last) + .0025f);
            }
            var tip = points[2] + last * tipLength;
            var centers = new[] { points[0] - first * 0.010f, points[0], points[1], points[2],
                points[2] + (tip - points[2]) * 0.65f, tip };
            var tangents = new[] { first, first, (first + last).Normalized(), last, last, last };
            float[] radii = { radius * 1.04f, radius, radius * 0.90f, radius * 0.78f, radius * 0.60f, radius * 0.22f };
            int[] ringBones = { bones[f][0], bones[f][0], bones[f][1], bones[f][2], bones[f][2], bones[f][2] };
            if (f == 4)
            {
                // Thumb01 starts inside the hand. Its fleshy base needs a local,
                // asymmetric envelope; sampling a whole segment at once makes a
                // block, while clipping those points exposes the thenar skin.
                var proximal = points[1] - points[0];
                var distal = points[2] - points[1];
                centers = new[] { centers[0], points[0], points[0] + proximal * .25f,
                    points[0] + proximal * .5f, points[0] + proximal * .75f, points[1],
                    points[1] + distal * .33f, points[1] + distal * .66f, points[2],
                    points[2] + (tip - points[2]) * .65f, tip };
                tangents = new[] { first, first, first, first, first, (first + last).Normalized(),
                    last, last, last, last, last };
                radii = new[] { radius, radius, radius, radius, radius * .95f, radius * .9f,
                    radius * .86f, radius * .82f, radius * .78f, radius * .6f, radius * .22f };
                ringBones = new[] { bones[f][0], bones[f][0], bones[f][0], bones[f][0], bones[f][0],
                    bones[f][1], bones[f][1], bones[f][1], bones[f][2], bones[f][2], bones[f][2] };
            }
            WearableVec3[]? previous = null;
            int[]? previousIndices = null;
            // Transport the cross section frame along the finger instead of
            // picking a new arbitrary basis at every joint, which twists quads.
            var radial = GlovePerpendicular(normal, tangents[0]);
            for (int ring = 0; ring < centers.Length; ring++)
            {
                radial = GlovePerpendicular(radial, tangents[ring]);
                var second = Cross(tangents[ring], radial).Normalized();
                float slab = .006f;
                if (ring > 0) slab = MathF.Max(slab, (centers[ring] - centers[ring - 1]).Length());
                if (ring + 1 < centers.Length) slab = MathF.Max(slab, (centers[ring + 1] - centers[ring]).Length());
                if (f == 4) slab = MathF.Min(slab, .016f);
                var vertices = GloveFittedRing(centers[ring], radial, second, tangents[ring], radii[ring], radii[ring],
                    fingerSamples, slab);
                var indices = GloveVertices(acc, vertices, ringBones[ring]);
                if (previous != null && previousIndices != null)
                    GloveConnect(acc, previous, vertices, previousIndices, indices, part.Surface);
                if (ring == centers.Length - 1)
                    GloveCap(acc, vertices, indices, centers[ring] + tangents[ring] * radius * 0.08f,
                        tangents[ring], ringBones[ring], part.Surface);
                previous = vertices;
                previousIndices = indices;
            }
        }
    }

    private static List<WearableVec3> GloveSamples(IReadOnlyList<WearableSkinSample>? samples, string side, string? finger)
    {
        var result = new List<WearableVec3>();
        if (samples == null) return result;
        foreach (var sample in samples)
        {
            float weight = 0f;
            foreach (var influence in sample.Influences)
            {
                string bone = influence.Bone;
                if (!bone.StartsWith(side, StringComparison.Ordinal)) continue;
                bool matches = finger == null
                    ? bone.StartsWith(side + "Wrist", StringComparison.Ordinal) || bone.StartsWith(side + "Elbow", StringComparison.Ordinal)
                    : bone.StartsWith(side + finger, StringComparison.Ordinal)
                        || (finger == "Thumb" && bone.StartsWith(side + "Thum", StringComparison.Ordinal));
                if (matches) weight += influence.Weight;
            }
            if (weight >= .5f) result.Add(sample.Position);
        }
        return result;
    }

    // Intersect neighboring support planes around measured body points. Unlike scaling
    // a round tube, this encloses an off-center or flattened cross-section without
    // inflating its opposite side. Adjacent slabs overlap to cover the skin between rings.
    private static WearableVec3[] GloveFittedRing(WearableVec3 center, WearableVec3 a, WearableVec3 b,
        WearableVec3 axis, float width, float thickness, IReadOnlyList<WearableVec3> samples, float slab,
        float clearance = .0025f)
    {
        var near = new List<WearableVec3>();
        foreach (var point in samples)
        {
            var delta = point - center;
            float axial = GloveDot(delta, axis);
            if (MathF.Abs(axial) <= slab)
                near.Add(delta);
        }
        if (near.Count < 4) return GloveEllipse(center, a, b, width, thickness);
        var supports = new float[Segments];
        float step = 2f * MathF.PI / Segments;
        for (int i = 0; i < Segments; i++)
        {
            float angle = (i + .5f) * step;
            var direction = a * MathF.Cos(angle) + b * MathF.Sin(angle);
            float support = clearance;
            foreach (var point in near) support = MathF.Max(support, GloveDot(point, direction) + clearance);
            supports[i] = support;
        }
        var vertices = new WearableVec3[Segments];
        for (int i = 0; i < Segments; i++)
        {
            int previous = (i + Segments - 1) % Segments;
            float p = (i - .5f) * step, q = (i + .5f) * step;
            float determinant = MathF.Sin(q - p);
            float x = (supports[previous] * MathF.Sin(q) - supports[i] * MathF.Sin(p)) / determinant;
            float y = (MathF.Cos(p) * supports[i] - MathF.Cos(q) * supports[previous]) / determinant;
            vertices[i] = center + a * x + b * y;
        }
        return vertices;
    }

    private static float GloveDot(WearableVec3 a, WearableVec3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

    private static WearableVec3 GlovePerpendicular(WearableVec3 hint, WearableVec3 axis)
    {
        var projected = hint - axis * GloveDot(hint, axis);
        if (projected.Length() > 0.00001f) return projected.Normalized();
        var fallback = MathF.Abs(axis.Y) < 0.9f ? new WearableVec3(0f, 1f, 0f) : new WearableVec3(1f, 0f, 0f);
        return (fallback - axis * GloveDot(fallback, axis)).Normalized();
    }

    private static WearableVec3[] GloveEllipse(WearableVec3 center, WearableVec3 a, WearableVec3 b, float width, float thickness)
    {
        var vertices = new WearableVec3[Segments];
        for (int i = 0; i < Segments; i++)
        {
            float angle = i * 2f * MathF.PI / Segments;
            vertices[i] = center + a * (MathF.Cos(angle) * width) + b * (MathF.Sin(angle) * thickness);
        }
        return vertices;
    }

    private static int[] GloveVertices(Acc acc, WearableVec3[] vertices, int bone, int bone2 = 0, float weight2 = 0f)
    {
        var indices = new int[vertices.Length];
        var center = Average(vertices);
        for (int i = 0; i < indices.Length; i++) indices[i] = acc.Vert(vertices[i], (vertices[i] - center).Normalized(), bone, bone2, weight2);
        return indices;
    }

    private static void GloveConnect(Acc acc, WearableVec3[] a, WearableVec3[] b, int[] ia, int[] ib, WearableSurface surface)
    {
        var face = Cross(b[0] - a[0], a[1] - a[0]);
        bool reverse = GloveDot(face, a[0] - Average(a)) < 0f;
        ConnectIndices(acc, ia, ib, surface, reverse);
    }

    private static void GloveCap(Acc acc, WearableVec3[] vertices, int[] indices, WearableVec3 center,
        WearableVec3 outward, int bone, WearableSurface surface)
    {
        int c = acc.Vert(center, outward, bone);
        bool reverse = GloveDot(Cross(vertices[0] - center, vertices[1] - center), outward) < 0f;
        for (int i = 0; i < indices.Length; i++)
        {
            int next = (i + 1) % indices.Length;
            if (reverse) acc.Tri(surface, c, indices[next], indices[i]);
            else acc.Tri(surface, c, indices[i], indices[next]);
        }
    }
}
