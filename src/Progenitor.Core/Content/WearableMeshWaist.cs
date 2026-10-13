using System;
using System.Collections.Generic;

namespace Progenitor.Content;

internal static partial class WearableMeshBuild
{
    // Every consumer uses identical sample limits, angular planes and clearance.
    // This is the one rest-space waist seam, even for an asymmetric body.
    private static WearableVec3[]? MeasuredWaist(IReadOnlyList<WearableVec3>? body,
        WearableVec3 pelvis, float span, WearableVec3 forward) => body == null ? null
        : BodiceOutline(body, pelvis, Right(forward), forward, pelvis.Y - .035f, pelvis.Y + .035f,
            span * .65f, span * .65f, 32, requireComplete: true);

    private static bool FittedWaistBelt(Acc acc, IReadOnlyDictionary<string, WearableVec3> pose,
        WearablePart part, float span, WearableVec3 forward, IReadOnlyList<WearableVec3>? body)
    {
        if (part.A >= 0f || part.Bone != "Pelvis_JNT"
            || !acc.TryBone(pose, part.Bone, out int bone, out var pelvis)) return false;
        var outline = MeasuredWaist(body, pelvis, span, forward);
        if (outline == null) return false;
        var top = new WearableVec3[outline.Length];
        var bottom = new WearableVec3[outline.Length];
        for (int i = 0; i < outline.Length; i++)
        {
            // The offset locates the upper edge; the belt straddles the waist instead
            // of adding the previous outward flare above it.
            top[i] = pelvis + outline[i] + new WearableVec3(0f, part.C, 0f);
            bottom[i] = pelvis + outline[i] + new WearableVec3(0f, part.C - .025f, 0f);
        }
        Connect(acc, top, bottom, bone, bone, part.Surface, forward);
        return true;
    }

    private static WearableVec3[] PreserveHipEnvelope(WearableVec3[] measured,
        WearableVec3[] blended, WearableVec3 right, WearableVec3 forward)
    {
        int count = measured.Length;
        float step = 2f * MathF.PI / count;
        var support = new float[count];
        for (int i = 0; i < count; i++)
        {
            float angle = (i + .5f) * step;
            var normal = right * MathF.Cos(angle) + forward * MathF.Sin(angle);
            float extent = float.NegativeInfinity;
            for (int j = 0; j < count; j++)
                extent = MathF.Max(extent, MathF.Max(DotBodice(measured[j], normal), DotBodice(blended[j], normal)));
            support[i] = extent;
        }
        var result = new WearableVec3[count];
        for (int i = 0; i < count; i++)
        {
            int before = (i + count - 1) % count;
            float a = (i - .5f) * step, b = (i + .5f) * step;
            float x = (support[before] * MathF.Sin(b) - support[i] * MathF.Sin(a)) / MathF.Sin(step);
            float z = (MathF.Cos(a) * support[i] - MathF.Cos(b) * support[before]) / MathF.Sin(step);
            result[i] = right * x + forward * z;
        }
        return result;
    }
}
