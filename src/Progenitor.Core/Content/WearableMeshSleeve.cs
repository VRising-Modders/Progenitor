using System;
using System.Collections.Generic;

namespace Progenitor.Content;

internal static partial class WearableMeshBuild
{
    private static void TailoredSleeve(Acc acc, IReadOnlyDictionary<string, WearableVec3> pose,
        WearablePart part, float span, List<string> missing, IReadOnlyList<WearableSkinSample>? bodySamples)
    {
        if (!acc.TryBone(pose, part.Bone, out int shoulderBone, out var shoulder)) return;
        bool elbowFound = acc.TryBone(pose, part.BoneTo, out int elbowBone, out var elbow);
        string wristName = part.BoneThird ?? "";
        bool wristFound = acc.TryBone(pose, wristName, out int wristBone, out var wrist);
        if (!elbowFound) NoteMissing(missing, part.BoneTo);
        if (!wristFound) NoteMissing(missing, wristName);
        if (!elbowFound || !wristFound) return;
        var upperDelta = elbow - shoulder;
        var lowerDelta = wrist - elbow;
        if (upperDelta.Length() < 0.0001f || lowerDelta.Length() < 0.0001f) return;
        var upper = upperDelta.Normalized();
        var lower = lowerDelta.Normalized();
        var elbowAxis = upper + lower;
        elbowAxis = elbowAxis.Length() > 0.0001f ? elbowAxis.Normalized() : upper;
        var fitPoints = new List<WearableVec3>();
        string side = part.Bone.StartsWith("Left_", StringComparison.Ordinal) ? "Left_" : "Right_";
        if (bodySamples != null)
            foreach (var sample in bodySamples)
            {
                float arm = 0f;
                foreach (var influence in sample.Influences)
                    if (influence.Bone.StartsWith(side + "Shoulder", StringComparison.Ordinal)) arm += influence.Weight;
                // Include torso-dominant underarm skin, not just the outer upper arm.
                if (arm >= .1f) fitPoints.Add(sample.Position);
            }
        // Full reference samples let the proximal sleeve follow the shoulder rather than
        // a shoulder-span cylinder. Sparse diagnostic samples retain the safe fallback.
        bool measuredShoulder = fitPoints.Count >= 12;
        float upperRadius = MathF.Max(0.08f, span * 0.20f);
        var root = measuredShoulder
            ? shoulder - upper * .035f
            : shoulder - upper * (upperRadius * 0.95f) + new WearableVec3(0f, upperRadius * 0.20f, 0f);
        var centers = new[] { root, shoulder, shoulder + upperDelta * 0.33f,
            shoulder + upperDelta * 0.72f, shoulder + upperDelta * 0.90f, elbow,
            elbow + lowerDelta * 0.10f, elbow + lowerDelta * 0.28f,
            elbow + lowerDelta * 0.65f, wrist };
        var axes = new[] { upper, upper, upper, upper,
            (upper + elbowAxis).Normalized(), elbowAxis,
            (elbowAxis + lower).Normalized(), lower, lower, lower };
        float[] radii = { upperRadius * 1.12f, upperRadius * 1.10f, upperRadius,
            0.065f, 0.058f, 0.055f, 0.055f, 0.053f, 0.048f, 0.045f };
        float[] upperWeights = { 0f, 0f, 0.20f, 0.65f, 0.88f, 1f };
        float[] lowerWeights = { 0.10f, 0.28f, 0.65f, 1f };
        var rings = new List<(WearableVec3 Center, WearableVec3 Axis, float Radius, float Weight, bool Upper)>();
        rings.Add((centers[0], axes[0], radii[0], upperWeights[0], true));
        for (int r = 1; r < centers.Length; r++)
        {
            int steps = r <= 5 ? Math.Max(1, (int)MathF.Ceiling((centers[r] - centers[r - 1]).Length() / .025f)) : 1;
            for (int s = 1; s <= steps; s++)
            {
                float t = (float)s / steps;
                float weight = r <= 5 ? upperWeights[r - 1] * (1f - t) + upperWeights[r] * t : lowerWeights[r - 6];
                rings.Add((centers[r - 1] * (1f - t) + centers[r] * t,
                    (axes[r - 1] * (1f - t) + axes[r] * t).Normalized(),
                    radii[r - 1] * (1f - t) + radii[r] * t, weight, r <= 5));
            }
        }
        const int samples = 24;
        var radial = SleeveRadial(new WearableVec3(0f, 0f, 1f), upper);
        int[]? previousIndices = null;
        WearableVec3[]? previousVertices = null;
        for (int ring = 0; ring < rings.Count; ring++)
        {
            var current = rings[ring];
            radial = SleeveRadial(radial, current.Axis);
            var second = Cross(current.Axis, radial).Normalized();
            var vertices = new WearableVec3[samples];
            var indices = new int[samples];
            int primary = current.Upper ? shoulderBone : elbowBone;
            int secondary = current.Upper ? elbowBone : wristBone;
            float weight = current.Weight;
            var supports = new float[samples];
            float step = 2f * MathF.PI / samples;
            int localSamples = 0;
            if (measuredShoulder && current.Upper)
                foreach (var p in fitPoints)
                    if (MathF.Abs(SleeveDot(p - current.Center, current.Axis)) <= .03f) localSamples++;
            float shoulderBlend = measuredShoulder && current.Upper && localSamples >= 3
                ? Math.Clamp((SleeveDot(current.Center - shoulder, upper) / upperDelta.Length() - .15f) / .45f, 0f, 1f)
                : 1f;
            for (int i = 0; i < samples; i++)
            {
                var direction = radial * MathF.Cos((i + .5f) * step) + second * MathF.Sin((i + .5f) * step);
                // Retain every sampled 12 mm body-clearance support; only remove the
                // oversized circular floor where a complete proximal reference exists.
                supports[i] = (.025f * (1f - shoulderBlend) + current.Radius * shoulderBlend) * MathF.Cos(step * .5f);
                if (current.Upper)
                    foreach (var p in fitPoints)
                    {
                        var delta = p - current.Center;
                        if (MathF.Abs(SleeveDot(delta, current.Axis)) <= .03f)
                            supports[i] = MathF.Max(supports[i], SleeveDot(delta, direction) + .012f);
                    }
            }
            for (int i = 0; i < samples; i++)
            {
                float angle = 2f * MathF.PI * i / samples;
                var outward = radial * MathF.Cos(angle) + second * MathF.Sin(angle);
                float a = angle - step * .5f, b = angle + step * .5f;
                float before = supports[(i + samples - 1) % samples], after = supports[i];
                float x = (before * MathF.Sin(b) - after * MathF.Sin(a)) / MathF.Sin(step);
                float y = (MathF.Cos(a) * after - MathF.Cos(b) * before) / MathF.Sin(step);
                vertices[i] = current.Center + radial * x + second * y;
                indices[i] = acc.Vert(vertices[i], outward, primary, secondary, weight);
            }
            if (previousIndices != null && previousVertices != null)
            {
                var normal = Cross(vertices[0] - previousVertices[0], previousVertices[1] - previousVertices[0]);
                bool reverse = SleeveDot(normal, previousVertices[0] - rings[ring - 1].Center) < 0f;
                ConnectIndices(acc, previousIndices, indices, part.Surface, reverse);
            }
            if (ring == 0)
            {
                // Keep the measured root circumference and every sleeve ring intact.
                // Round only the inward cap: the former flat fan made a hard shoulder ridge.
                float depth = measuredShoulder ? .010f : upperRadius * .35f;
                var capIndices = indices;
                var capVertices = vertices;
                foreach (float degrees in new[] { 75f, 50f, 25f })
                {
                    float angle = degrees * MathF.PI / 180f;
                    var capCenter = root - upper * (depth * MathF.Cos(angle));
                    var nextIndices = new int[samples];
                    var nextVertices = new WearableVec3[samples];
                    for (int i = 0; i < samples; i++)
                    {
                        var offset = (vertices[i] - root) * MathF.Sin(angle);
                        nextVertices[i] = capCenter + offset;
                        nextIndices[i] = acc.Vert(nextVertices[i],
                            (offset.Normalized() * MathF.Sin(angle) - upper * MathF.Cos(angle)).Normalized(), shoulderBone);
                    }
                    var capNormal = Cross(nextVertices[0] - capVertices[0], capVertices[1] - capVertices[0]);
                    bool capReverse = SleeveDot(capNormal, capVertices[0] - root) < 0f;
                    ConnectIndices(acc, capIndices, nextIndices, part.Surface, capReverse);
                    capIndices = nextIndices;
                    capVertices = nextVertices;
                }
                var tip = root - upper * depth;
                int center = acc.Vert(tip, upper * -1f, shoulderBone);
                bool reverse = SleeveDot(Cross(capVertices[0] - tip, capVertices[1] - tip), upper * -1f) < 0f;
                for (int i = 0; i < samples; i++)
                {
                    int next = (i + 1) % samples;
                    if (reverse) acc.Tri(part.Surface, center, capIndices[next], capIndices[i]);
                    else acc.Tri(part.Surface, center, capIndices[i], capIndices[next]);
                }
            }
            previousIndices = indices;
            previousVertices = vertices;
        }
    }

    private static float SleeveDot(WearableVec3 a, WearableVec3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

    private static WearableVec3 SleeveRadial(WearableVec3 hint, WearableVec3 axis)
    {
        var projected = hint - axis * SleeveDot(hint, axis);
        if (projected.Length() > 0.00001f) return projected.Normalized();
        var fallback = MathF.Abs(axis.Y) < 0.9f ? new WearableVec3(0f, 1f, 0f) : new WearableVec3(1f, 0f, 0f);
        return (fallback - axis * SleeveDot(fallback, axis)).Normalized();
    }
}
