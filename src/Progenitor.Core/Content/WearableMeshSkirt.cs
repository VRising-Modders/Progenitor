using System;
using System.Collections.Generic;

namespace Progenitor.Content;

internal static partial class WearableMeshBuild
{
    /// <summary>
    /// An original bell-shaped skirt with restrained folds and a sewn-in gold hem.
    /// Shared rings keep the surface continuous as the two legs move; these are
    /// skin weights for a stylized drape, not a cloth simulation.
    /// </summary>
    private static void TailoredSkirt(Acc acc, IReadOnlyDictionary<string, WearableVec3> pose,
        WearableSurface surface, float span, WearableVec3 forward, List<string> missing,
        IReadOnlyList<WearableVec3>? body)
    {
        string[] names = { "Pelvis_JNT", "Left_Knee_JNT", "Right_Knee_JNT",
            "Left_Ankle_JNT", "Right_Ankle_JNT" };
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

        const int samples = 32;
        var right = Right(forward);
        var pelvis = joints[0];
        var ankles = (joints[3] + joints[4]) * 0.5f;
        float hemY = ankles.Y + span * 0.06f;
        if (pelvis.Y - hemY <= span * 0.2f) return;

        float waistRadius = span * 0.65f;
        float ankleSeparation = (joints[4] - joints[3]).Length();
        float hemRadius = MathF.Max(span * 1.2f, ankleSeparation * 0.5f + span * 0.36f);
        var kneeAxis = joints[2] - joints[1];
        float rightSign = kneeAxis.X * right.X + kneeAxis.Z * right.Z < 0f ? -1f : 1f;
        var waist = MeasuredWaist(body, pelvis, span, forward);

        // Most of the growth is below the hips. The close bottom two rings make a
        // narrow gold edging, sharing the cloth's vertices instead of floating over it.
        var heights = new List<float> { 0f, 0.13f, 0.32f, 0.53f, 0.74f, 0.92f, 0.985f, 1f };
        float totalHeight = pelvis.Y - hemY;
        if (waist != null)
        {
            for (float drop = .03f; drop < totalHeight * .32f; drop += .03f)
                heights.Add(drop / totalHeight);
            heights.Sort();
        }
        WearableVec3[]? lastMeasured = waist;
        int[]? previous = null;
        for (int band = 0; band < heights.Count; band++)
        {
            float t = heights[band];
            float flare = t * t * (2f - t);
            float radius = waistRadius + (hemRadius - waistRadius) * flare;
            float shift = t * t;
            var center = new WearableVec3(
                pelvis.X + (ankles.X - pelvis.X) * shift,
                pelvis.Y + (hemY - pelvis.Y) * t,
                pelvis.Z + (ankles.Z - pelvis.Z) * shift);
            // A slight oval below the belt avoids a lampshade silhouette in profile.
            float depth = 1f - 0.16f * t;
            WearableVec3[]? fitted = null;
            float transition = 0f;
            if (waist != null && t < .32f)
            {
                if (band == 0) fitted = waist;
                else
                {
                    float lowY = pelvis.Y - totalHeight * heights[band + 1] - .005f;
                    float highY = pelvis.Y - totalHeight * heights[band - 1] + .005f;
                    fitted = BodiceOutline(body!, center, right, forward,
                        lowY, highY, radius, radius * depth, samples, requireComplete: true);
                    if (fitted != null) lastMeasured = fitted;
                    else fitted = lastMeasured;
                    float drop = totalHeight * t;
                    float blendLength = MathF.Max(.03f, totalHeight * .32f - .14f);
                    float blend = Math.Clamp((drop - .14f) / blendLength, 0f, 1f);
                    transition = blend * blend * (3f - 2f * blend);
                }
            }
            if (fitted != null && transition > 0f)
            {
                var blended = new WearableVec3[samples];
                for (int i = 0; i < samples; i++)
                {
                    float angle = i * 2f * MathF.PI / samples;
                    float fold = 1f + .042f * t * t * MathF.Cos(8f * angle + t * .28f);
                    var authored = right * (MathF.Cos(angle) * radius * fold)
                        + forward * (MathF.Sin(angle) * radius * depth * fold);
                    blended[i] = fitted[i] * (1f - transition) + authored * transition;
                }
                fitted = PreserveHipEnvelope(fitted, blended, right, forward);
                transition = 0f;
            }
            var indices = new int[samples];
            for (int i = 0; i < samples; i++)
            {
                float angle = i * 2f * MathF.PI / samples;
                float lateral = MathF.Cos(angle);
                float front = MathF.Sin(angle);
                // Eight broad folds are resolved by four angular samples each.
                // The phase follows a shallow diagonal rather than forming rigid flutes.
                float pleat = MathF.Cos(8f * angle + t * 0.28f);
                float fold = 1f + 0.042f * t * t * pleat;
                var offset = right * (lateral * radius * fold)
                    + forward * (front * radius * depth * fold);
                if (fitted != null) offset = fitted[i] * (1f - transition) + offset * transition;

                int primary, secondary;
                float secondaryWeight;
                float side = lateral * rightSign;
                if (band == 0)
                {
                    primary = secondary = bones[0];
                    secondaryWeight = 0f;
                }
                else if (t <= .32f)
                {
                    // Taper the knee influence to zero at the front/back centerline.
                    // Switching legs there therefore cannot introduce a weight jump.
                    primary = bones[0];
                    secondary = side >= 0f ? bones[2] : bones[1];
                    float kneeWeight = t <= .13f ? .32f * t / .13f
                        : .32f + .4f * (t - .13f) / .19f;
                    secondaryWeight = kneeWeight * MathF.Abs(side);
                }
                else
                {
                    bool lowerLeg = t >= .92f;
                    primary = bones[lowerLeg ? 3 : 1];
                    secondary = bones[lowerLeg ? 4 : 2];
                    secondaryWeight = (side + 1f) * 0.5f;
                }
                indices[i] = acc.Vert(center + offset, offset.Normalized(),
                    primary, secondary, secondaryWeight);
            }
            if (previous != null)
                ConnectIndices(acc, previous, indices,
                    band == heights.Count - 1 ? WearableSurface.Gold : surface, reverse: true);
            previous = indices;
        }
    }
}
