using System;
using System.Collections.Generic;

namespace Progenitor.Content;

internal static partial class WearableMeshBuild
{
    private static void TailoredCowl(Acc acc, IReadOnlyDictionary<string, WearableVec3> pose,
        WearablePart part, WearableVec3 forward)
    {
        if (!acc.TryBone(pose, part.Bone, out int headBone, out var head)) return;
        float radius = part.A > 0f ? part.A : 0.15f;
        var right = Right(forward);
        var up = new WearableVec3(0f, 1f, 0f);
        const int samples = 48;

        // Smooth, elongated cloth crown, a low brow and loose unequal side ends.
        // The opening stays narrow; this is draped fabric rather than a spherical shell.
        // The complete hood remains head-bound so the opaque recess follows every nod.
        (float X, float Y)[] shellOutline =
        {
            (0.04f, 1.65f), (0.45f, 1.52f), (0.77f, 1.10f), (0.91f, 0.48f),
            (0.94f, -0.15f), (0.87f, -0.91f), (0.63f, -1.74f), (0.26f, -1.56f),
            (0f, -1.47f), (-0.28f, -1.60f), (-0.66f, -1.85f), (-0.88f, -0.95f),
            (-0.94f, -0.17f), (-0.91f, 0.48f), (-0.77f, 1.10f), (-0.43f, 1.52f),
        };
        (float X, float Y)[] faceOutline =
        {
            (0.03f, 0.88f), (0.27f, 0.91f), (0.46f, 0.73f), (0.56f, 0.34f),
            (0.55f, -0.16f), (0.49f, -0.72f), (0.35f, -1.16f), (0.16f, -1.30f),
            (0f, -1.34f), (-0.18f, -1.33f), (-0.38f, -1.20f), (-0.52f, -0.75f),
            (-0.56f, -0.17f), (-0.57f, 0.34f), (-0.45f, 0.75f), (-0.24f, 0.94f),
        };
        (float X, float Y) Curve((float X, float Y)[] outline, int sample)
        {
            float at = (float)sample * outline.Length / samples;
            int index = (int)at;
            float t = at - index, t2 = t * t, t3 = t2 * t;
            var a = outline[(index + outline.Length - 1) % outline.Length];
            var b = outline[index];
            var c = outline[(index + 1) % outline.Length];
            var d = outline[(index + 2) % outline.Length];
            float Blend(float p0, float p1, float p2, float p3) => .5f *
                (2f * p1 + (-p0 + p2) * t + (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2
                    + (-p0 + 3f * p1 - 3f * p2 + p3) * t3);
            return (Blend(a.X, b.X, c.X, d.X), Blend(a.Y, b.Y, c.Y, d.Y));
        }
        int[] Ring(float scale, float depth, bool opening, float foldStrength)
        {
            var ring = new int[samples];
            for (int i = 0; i < samples; i++)
            {
                float angle = 2f * MathF.PI * i / samples;
                var outline = opening ? faceOutline : shellOutline;
                var (x, y) = Curve(outline, i);
                // Gentle depth folds at the hanging ends, with the center brow lower
                // than its temples; no hard planar outline segments or thick chin torus.
                float crease = 0.025f * MathF.Sin(angle * 3f + 0.4f);
                float drape = MathF.Max(0f, -y - 0.45f) * 0.12f;
                float z = depth + (opening ? 0f : foldStrength * (crease - drape));
                var offset = right * (x * scale * radius)
                    + up * (y * scale * radius) + forward * (z * radius);
                ring[i] = acc.Vert(head + offset, offset.Normalized(), headBone);
            }
            return ring;
        }

        // Circumference runs from crown toward the wearer's right. Back-to-front
        // joins therefore use the ordinary winding; the rear fan faces backward.
        var back = Ring(0.34f, -0.78f, false, 0.3f);
        int rearCenter = acc.Vert(head - forward * (radius * 0.86f), forward * -1f, headBone);
        for (int i = 0; i < samples; i++)
            acc.Tri(part.Surface, rearCenter, back[i], back[(i + 1) % samples]);
        var rearShoulder = Ring(0.76f, -0.61f, false, 0.65f);
        ConnectIndices(acc, back, rearShoulder, part.Surface);
        var crown = Ring(1f, -0.22f, false, 1f);
        ConnectIndices(acc, rearShoulder, crown, part.Surface);
        var brow = Ring(0.96f, 0.72f, false, 1f);
        ConnectIndices(acc, crown, brow, part.Surface);
        var foldedEdge = Ring(0.94f, 1.12f, false, 0.8f);
        ConnectIndices(acc, brow, foldedEdge, part.Surface);

        // The cloth turns inward at its soft edge. The void begins behind
        // that lip, then curves inward; it is not a separate floating face disc.
        var opening = Ring(1f, 0.97f, true, 0f);
        ConnectIndices(acc, foldedEdge, opening, part.Surface);
        var recess = Ring(0.68f, 0.82f, true, 0f);
        ConnectIndices(acc, opening, recess, part.SurfaceB);
        var deepRecess = Ring(0.30f, 0.77f, true, 0f);
        ConnectIndices(acc, recess, deepRecess, part.SurfaceB);
        int faceCenter = acc.Vert(head + forward * (radius * 0.76f), forward, headBone);
        for (int i = 0; i < samples; i++)
            acc.Tri(part.SurfaceB, faceCenter, deepRecess[(i + 1) % samples], deepRecess[i]);
    }
}
