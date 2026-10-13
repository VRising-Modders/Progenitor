using System;
using System.Collections.Generic;

namespace Progenitor.Content;

internal static partial class WearableMeshBuild
{
    private static void TailoredBodice(Acc acc, IReadOnlyDictionary<string, WearableVec3> pose,
        WearableSurface surface, float span, WearableVec3 forward, List<string> missing,
        IReadOnlyList<WearableVec3>? torso)
    {
        string[] names = { "Neck_JNT", "Chest_JNT", "Pelvis_JNT", "Left_Shoulder_JNT", "Right_Shoulder_JNT" };
        var joints = new WearableVec3[names.Length];
        var bones = new int[names.Length];
        bool complete = true;
        for (int i = 0; i < names.Length; i++)
            if (!acc.TryBone(pose, names[i], out bones[i], out joints[i]))
            { NoteMissing(missing, names[i]); complete = false; }
        if (!complete) return;
        var shoulder = (joints[3] + joints[4]) * 0.5f;
        var landmarks = new[] { joints[0], joints[0] + new WearableVec3(0f, -0.014f, 0f),
            shoulder, (shoulder + joints[1]) * 0.5f, joints[1],
            (joints[1] + joints[2]) * 0.5f, joints[2] };
        float[] widths = { .25f, .27f, .55f, .53f, .49f, .54f, .65f };
        float[] depths = { .25f, .27f, .32f, .34f, .34f, .43f, .65f };
        var centers = new List<WearableVec3> { landmarks[0] };
        var ringWidths = new List<float> { widths[0] * span };
        var ringDepths = new List<float> { depths[0] * span };
        for (int segment = 1; segment < landmarks.Length; segment++)
        {
            // Shorter bands follow the neck/shoulder taper without projecting a broad
            // lower cross-section several centimeters up onto the shoulder.
            int steps = Math.Max(1, (int)MathF.Ceiling(MathF.Abs(landmarks[segment].Y - landmarks[segment - 1].Y) / .015f));
            for (int step = 1; step <= steps; step++)
            {
                float t = (float)step / steps;
                centers.Add(landmarks[segment - 1] * (1f - t) + landmarks[segment] * t);
                ringWidths.Add((widths[segment - 1] * (1f - t) + widths[segment] * t) * span);
                ringDepths.Add((depths[segment - 1] * (1f - t) + depths[segment] * t) * span);
            }
        }
        var right = Right(forward);
        const int count = 32;
        var torsoBones = new List<(float Y, int Bone)>();
        // Names are intentionally checked before TryBone: aliases must not register the
        // same physical joint again under another torso landmark. In particular the live
        // Ribcage lies above Chest, and Gut carries the lower torso.
        foreach (var name in new[] { "Pelvis_JNT", "Gut_JNT", "Spine_JNT", "Chest_JNT", "Ribcage_JNT", "Neck_JNT" })
            if (pose.ContainsKey(name) && acc.TryBone(pose, name, out int bone, out var at))
                torsoBones.Add((at.Y, bone));
        if (!torsoBones.Exists(p => p.Bone == bones[2])) torsoBones.Add((joints[2].Y, bones[2]));
        if (!torsoBones.Exists(p => p.Bone == bones[1])) torsoBones.Add((joints[1].Y, bones[1]));
        if (!torsoBones.Exists(p => p.Bone == bones[0])) torsoBones.Add((joints[0].Y, bones[0]));
        torsoBones.Sort((a, b) => a.Y.CompareTo(b.Y));
        int[]? previous = null;
        for (int ring = 0; ring < centers.Count; ring++)
        {
            var center = centers[ring];
            float width = ringWidths[ring], depth = ringDepths[ring];
            WearableVec3[]? outline = null;
            // Measurements are only clearance constraints; no reference triangles or
            // UVs are copied. The measured waist is shared with skirt and belt.
            if (torso != null && ring > 1 && ring < centers.Count - 1)
            {
                // Every point between two rings supports BOTH endpoint rings. Fixed
                // thickness slices left gaps precisely where the breast surface projected
                // farthest. These slabs overlap across the entire adjoining bands.
                float lowY = MathF.Min(centers[ring - 1].Y, centers[ring + 1].Y) - .005f;
                float highY = MathF.Max(centers[ring - 1].Y, centers[ring + 1].Y) + .005f;
                outline = BodiceOutline(torso, center, right, forward, lowY, highY, width, depth, count,
                    completeClearance: .010f);
            }
            if (ring == centers.Count - 1) outline = MeasuredWaist(torso, joints[2], span, forward);
            var indices = new int[count];
            BodiceWeights(torsoBones, center.Y, out int primary, out int secondary, out float blend);
            // This exact seam is shared geometrically with the skirt, so it must also
            // retain precisely the same pelvis-only binding.
            if (ring == centers.Count - 1) { primary = secondary = bones[2]; blend = 0f; }
            for (int i = 0; i < count; i++)
            {
                float angle = i * 2f * MathF.PI / count;
                float fold = ring < 2 || ring == centers.Count - 1 ? 1f : 1f + .018f * MathF.Cos(angle * 8f);
                var offset = outline != null ? outline[i]
                    : right * (MathF.Cos(angle) * width * fold) + forward * (MathF.Sin(angle) * depth * fold);
                indices[i] = acc.Vert(center + offset, offset.Normalized(), primary, secondary, blend);
            }
            if (previous != null) ConnectIndices(acc, previous, indices, ring == 1 ? WearableSurface.Gold : surface, reverse: true);
            previous = indices;
        }
    }

    private static WearableVec3[]? BodiceOutline(IReadOnlyList<WearableVec3> torso,
        WearableVec3 center, WearableVec3 right, WearableVec3 forward,
        float lowY, float highY, float width, float depth, int count, bool requireComplete = false,
        float completeClearance = .022f)
    {
        var samples = new List<(float X, float Z)>();
        float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
        foreach (var p in torso)
        {
            if (p.Y < lowY || p.Y > highY) continue;
            var d = p - center;
            float x = DotBodice(d, right), z = DotBodice(d, forward);
            samples.Add((x, z));
            minX = MathF.Min(minX, x); maxX = MathF.Max(maxX, x);
            minZ = MathF.Min(minZ, z); maxZ = MathF.Max(maxZ, z);
        }
        if (samples.Count == 0) return null;
        // A complete local cross-section may tighten the fallback sketch. A handful
        // of front-only measurements may only expand it, never collapse its back.
        bool complete = samples.Count >= 12 && minX < -.025f && maxX > .025f && minZ < -.025f && maxZ > .025f;
        if (requireComplete && !complete) return null;
        // Reliable body sections allow a close cloth fit; sparse references retain
        // conservative clearance. Waist and skirt callers keep their existing margin.
        float clearance = complete ? completeClearance : .022f;
        var support = new float[count];
        float step = 2f * MathF.PI / count;
        for (int i = 0; i < count; i++)
        {
            float angle = (i + .5f) * step;
            float x = MathF.Cos(angle), z = MathF.Sin(angle);
            float extent = complete ? float.NegativeInfinity : MathF.Sqrt(width * width * x * x + depth * depth * z * z);
            foreach (var p in samples)
                extent = MathF.Max(extent, p.X * x + p.Z * z + clearance);
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
        // This is a new polygon around measured support planes, not source topology.
        // Corresponding polygon edges have identical normals across neighboring rings,
        // preserving their clearance throughout each interpolated band.
        return result;
    }

    private static void BodiceWeights(List<(float Y, int Bone)> bones, float y,
        out int primary, out int secondary, out float blend)
    {
        primary = secondary = bones[0].Bone;
        blend = 0f;
        if (y <= bones[0].Y) return;
        for (int i = 1; i < bones.Count; i++)
        {
            if (y > bones[i].Y) continue;
            primary = bones[i - 1].Bone;
            secondary = bones[i].Bone;
            float height = bones[i].Y - bones[i - 1].Y;
            blend = height > .00001f ? Math.Clamp((y - bones[i - 1].Y) / height, 0f, 1f) : 1f;
            return;
        }
        primary = secondary = bones[bones.Count - 1].Bone;
    }

    private static float DotBodice(WearableVec3 a, WearableVec3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
}
