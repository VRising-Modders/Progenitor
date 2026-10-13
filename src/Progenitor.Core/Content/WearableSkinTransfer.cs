using System;
using System.Collections.Generic;

namespace Progenitor.Content;

internal readonly struct WearableSkinInfluence
{
    internal WearableSkinInfluence(string bone, float weight) { Bone = bone; Weight = weight; }
    internal string Bone { get; }
    internal float Weight { get; }
}

/// <summary>A measured point and its skin weights; no source triangles are retained.</summary>
internal sealed class WearableSkinSample
{
    internal WearableSkinSample(WearableVec3 position, params WearableSkinInfluence[] influences)
    {
        Position = position;
        var weights = new Dictionary<string, double>(StringComparer.Ordinal);
        if (influences != null)
            foreach (var influence in influences)
            {
                if (string.IsNullOrWhiteSpace(influence.Bone) || !float.IsFinite(influence.Weight) || influence.Weight <= 0f) continue;
                weights.TryGetValue(influence.Bone, out double previous);
                weights[influence.Bone] = previous + influence.Weight;
            }
        Influences = Array.AsReadOnly(WearableSkinTransfer.Normalize(weights));
    }

    internal WearableVec3 Position { get; }
    internal IReadOnlyList<WearableSkinInfluence> Influences { get; }
}

/// <summary>
/// Transfers local deformation from reference surface samples onto original vertices.
/// Callers select a compatible body region and coordinate frame before querying: proximity
/// alone cannot distinguish a hand touching the torso or two overlapping clothing layers.
/// </summary>
internal static class WearableSkinTransfer
{
    internal static bool TryTransfer(WearableVec3 position, IReadOnlyList<WearableSkinSample> samples,
        float maxDistance, out WearableSkinInfluence[] influences, int nearestCount = 4)
    {
        if (samples == null) throw new ArgumentNullException(nameof(samples));
        if (!float.IsFinite(maxDistance) || maxDistance < 0f) throw new ArgumentOutOfRangeException(nameof(maxDistance));
        if (nearestCount < 1 || nearestCount > 32) throw new ArgumentOutOfRangeException(nameof(nearestCount));
        influences = Array.Empty<WearableSkinInfluence>();
        if (!Finite(position)) return false;

        var nearest = new (double DistanceSquared, WearableSkinSample Sample)[Math.Min(nearestCount, samples.Count)];
        int count = 0;
        double cutoff = (double)maxDistance * maxDistance;
        foreach (var sample in samples)
        {
            if (sample == null || sample.Influences.Count == 0 || !Finite(sample.Position)) continue;
            double x = (double)sample.Position.X - position.X;
            double y = (double)sample.Position.Y - position.Y;
            double z = (double)sample.Position.Z - position.Z;
            double distanceSquared = x * x + y * y + z * z;
            if (distanceSquared > cutoff) continue;
            if (distanceSquared == 0d)
            {
                influences = new WearableSkinInfluence[sample.Influences.Count];
                for (int i = 0; i < influences.Length; i++) influences[i] = sample.Influences[i];
                return true;
            }
            // Stable insertion keeps equal-distance samples in source order.
            int insert = count;
            while (insert > 0 && distanceSquared < nearest[insert - 1].DistanceSquared) insert--;
            if (insert >= nearest.Length) continue;
            int last = Math.Min(count, nearest.Length - 1);
            for (int i = last; i > insert; i--) nearest[i] = nearest[i - 1];
            nearest[insert] = (distanceSquared, sample);
            if (count < nearest.Length) count++;
        }
        if (count == 0) return false;

        var merged = new Dictionary<string, double>(StringComparer.Ordinal);
        // Scaling by the nearest distance is equivalent to inverse-square weighting,
        // but all factors stay <=1 even when a vertex lies extremely close to a sample.
        double closest = nearest[0].DistanceSquared;
        for (int i = 0; i < count; i++)
        {
            double factor = closest / nearest[i].DistanceSquared;
            foreach (var weight in nearest[i].Sample.Influences)
            {
                merged.TryGetValue(weight.Bone, out double previous);
                merged[weight.Bone] = previous + factor * weight.Weight;
            }
        }
        influences = Normalize(merged);
        return influences.Length > 0;
    }

    internal static WearableSkinInfluence[] Normalize(Dictionary<string, double> weights)
    {
        var sorted = new List<KeyValuePair<string, double>>();
        foreach (var pair in weights)
            if (double.IsFinite(pair.Value) && pair.Value > 0d) sorted.Add(pair);
        sorted.Sort((a, b) =>
        {
            int weight = b.Value.CompareTo(a.Value);
            return weight != 0 ? weight : StringComparer.Ordinal.Compare(a.Key, b.Key);
        });
        int count = Math.Min(4, sorted.Count);
        if (count == 0) return Array.Empty<WearableSkinInfluence>();
        double total = 0d;
        for (int i = 0; i < count; i++) total += sorted[i].Value;
        var result = new WearableSkinInfluence[count];
        for (int i = 0; i < count; i++) result[i] = new WearableSkinInfluence(sorted[i].Key, (float)(sorted[i].Value / total));
        return result;
    }

    private static bool Finite(WearableVec3 p) => float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z);
}
