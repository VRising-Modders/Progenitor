using System;
using System.Collections.Generic;

namespace Progenitor.Content;

/// <summary>Hand-local reference selection; adjacent fingers must not share nearest-point skinning.</summary>
internal static class WearableHandSkin
{
    internal static bool IsDigit(string bone) => Digit(bone).Length > 0;

    internal static List<WearableSkinSample> Filter(IReadOnlyList<WearableSkinSample> samples, string authoredBone)
    {
        var result = new List<WearableSkinSample>();
        string side = Side(authoredBone), digit = Digit(authoredBone);
        if (side.Length == 0 || digit.Length == 0 && !NonDigit(authoredBone, includeUpperArm: true)) return result;
        bool forearm = authoredBone.Contains("Elbow", StringComparison.Ordinal)
            || authoredBone.Contains("Forearm", StringComparison.Ordinal);
        bool wristWeb = digit.Length == 0 && authoredBone.Contains("Wrist", StringComparison.Ordinal);
        foreach (var sample in samples)
        {
            float matched = 0f;
            float thumb = 0f;
            bool foreign = false;
            var retained = new List<WearableSkinInfluence>();
            foreach (var influence in sample.Influences)
            {
                string sourceSide = Side(influence.Bone), sourceDigit = Digit(influence.Bone);
                // Even a close point from the opposite hand or neighboring finger is
                // unsuitable. Tiny rounding residues are the only ignored foreign weights.
                if (sourceSide != side || sourceDigit.Length > 0 && sourceDigit != digit
                    && !(wristWeb && sourceDigit == "Thumb"))
                {
                    if (influence.Weight > .0001f) foreign = true;
                    continue;
                }
                if (digit.Length > 0)
                {
                    if (sourceDigit == digit)
                    {
                        matched += influence.Weight;
                        retained.Add(influence);
                    }
                    else if (WristOrPalm(influence.Bone)) retained.Add(influence);
                }
                else if (sourceDigit.Length == 0 && NonDigit(influence.Bone, forearm))
                {
                    matched += influence.Weight;
                    retained.Add(influence);
                }
                else if (wristWeb && sourceDigit == "Thumb")
                {
                    thumb += influence.Weight;
                    retained.Add(influence);
                }
            }
            // The web at the thumb base is skinned partly to the wrist and partly to
            // Thumb01. Dropping that blend makes a fitted palm lag behind exposed skin
            // when the thumb bends. Require a real wrist share so a pure thumb point
            // cannot drag the rest of the palm along with it.
            float required = digit.Length > 0 || wristWeb && thumb > 0f ? .25f : .5f;
            if (foreign || matched < required || retained.Count == 0) continue;
            // Preserve the finger's own proximal/middle/distal distribution and its
            // wrist blend; the sample constructor re-normalizes after allowed filtering.
            result.Add(new WearableSkinSample(sample.Position, retained.ToArray()));
        }
        return result;
    }

    private static string Side(string bone) => bone.StartsWith("Left_", StringComparison.Ordinal) ? "Left_"
        : bone.StartsWith("Right_", StringComparison.Ordinal) ? "Right_" : "";

    private static string Digit(string bone)
    {
        string side = Side(bone);
        if (side.Length == 0) return "";
        string name = bone.Substring(side.Length);
        foreach (string digit in new[] { "Index", "Middle", "Ring", "Pinky" })
            if (name.StartsWith(digit, StringComparison.Ordinal)) return digit;
        // The actual rig's middle thumb joint is named Thum02_JNT.
        return name.StartsWith("Thum", StringComparison.Ordinal) ? "Thumb" : "";
    }

    private static bool WristOrPalm(string bone)
    {
        string side = Side(bone);
        if (side.Length == 0) return false;
        string name = bone.Substring(side.Length);
        return name.StartsWith("Wrist", StringComparison.Ordinal)
            || name.StartsWith("Hand", StringComparison.Ordinal) || name.StartsWith("Palm", StringComparison.Ordinal);
    }

    private static bool NonDigit(string bone, bool includeUpperArm)
    {
        if (WristOrPalm(bone)) return true;
        string side = Side(bone);
        if (side.Length == 0) return false;
        string name = bone.Substring(side.Length);
        return name.StartsWith("Elbow", StringComparison.Ordinal) || name.StartsWith("Forearm", StringComparison.Ordinal)
            || includeUpperArm && (name.StartsWith("UpperArm", StringComparison.Ordinal)
                || name.StartsWith("Arm", StringComparison.Ordinal) || name.StartsWith("Shoulder", StringComparison.Ordinal));
    }
}
