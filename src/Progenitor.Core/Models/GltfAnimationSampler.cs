using System;
using System.Collections.Generic;

namespace Progenitor.Models;

/// <summary>Samples an immutable snapshot of a glTF clip into caller-owned packed TRS buffers.</summary>
public sealed class GltfAnimationSampler
{
    private readonly float[] _translation, _rotation, _scale;
    private readonly Channel[] _channels;
    public int NodeCount { get; }
    public float Duration { get; }

    public GltfAnimationSampler(GltfScene scene, GltfAnimation animation)
    {
        if (scene == null) throw new ArgumentNullException(nameof(scene));
        if (animation == null) throw new ArgumentNullException(nameof(animation));
        NodeCount = scene.Nodes.Count;
        _translation = new float[checked(NodeCount * 3)];
        _rotation = new float[checked(NodeCount * 4)];
        _scale = new float[checked(NodeCount * 3)];
        for (int i = 0; i < NodeCount; i++)
        {
            if (scene.Nodes[i] == null) throw new ArgumentException("Scene nodes must not be null.", nameof(scene));
            CopyBase(scene.Nodes[i].Translation, _translation, i * 3, 3);
            CopyBase(scene.Nodes[i].Rotation, _rotation, i * 4, 4);
            CopyBase(scene.Nodes[i].Scale, _scale, i * 3, 3);
            Normalize(_rotation, i * 4);
        }
        var targets = new HashSet<(int, string)>();
        _channels = new Channel[animation.Channels.Count];
        float duration = 0;
        for (int i = 0; i < _channels.Length; i++)
        {
            var source = animation.Channels[i];
            if (source == null) throw new ArgumentException("Animation channels must not be null.", nameof(animation));
            int width = source.Path == "rotation" ? 4 : 3;
            if (source.Node < 0 || source.Node >= NodeCount ||
                (source.Path != "translation" && source.Path != "rotation" && source.Path != "scale") ||
                (source.Interpolation != "LINEAR" && source.Interpolation != "STEP") ||
                !targets.Add((source.Node, source.Path)))
                throw new ArgumentException("Invalid or duplicate animation channel.", nameof(animation));
            if (source.Times == null || source.Values == null || source.Times.Length == 0 ||
                (long)source.Times.Length * width != source.Values.Length)
                throw new ArgumentException("Animation key arrays have invalid lengths.", nameof(animation));
            var times = (float[])source.Times.Clone();
            var values = (float[])source.Values.Clone();
            for (int k = 0; k < times.Length; k++)
                if (!Finite(times[k]) || times[k] < 0 || (k > 0 && times[k] <= times[k - 1]))
                    throw new ArgumentException("Animation times must be finite, nonnegative and strictly increasing.", nameof(animation));
            foreach (float value in values)
                if (!Finite(value)) throw new ArgumentException("Animation values must be finite.", nameof(animation));
            if (width == 4)
                for (int k = 0; k < times.Length; k++) Normalize(values, k * 4);
            _channels[i] = new Channel(source.Node, source.Path, source.Interpolation == "STEP", times, values);
            duration = Math.Max(duration, times[times.Length - 1]);
        }
        Duration = duration;
    }

    /// <summary>Clamps finite seconds to each channel's endpoints; restores untouched properties to the base pose.</summary>
    public void Sample(float seconds, float[] translations, float[] rotations, float[] scales)
    {
        if (!Finite(seconds)) throw new ArgumentOutOfRangeException(nameof(seconds));
        RequireBuffer(translations, _translation.Length, nameof(translations));
        RequireBuffer(rotations, _rotation.Length, nameof(rotations));
        RequireBuffer(scales, _scale.Length, nameof(scales));
        if (ReferenceEquals(translations, rotations) || ReferenceEquals(translations, scales) || ReferenceEquals(rotations, scales))
            throw new ArgumentException("Transform buffers must be distinct.");
        Array.Copy(_translation, translations, translations.Length);
        Array.Copy(_rotation, rotations, rotations.Length);
        Array.Copy(_scale, scales, scales.Length);
        foreach (var channel in _channels)
        {
            int width = channel.Path == "rotation" ? 4 : 3;
            float[] output = channel.Path == "rotation" ? rotations : channel.Path == "scale" ? scales : translations;
            int destination = channel.Node * width;
            int found = Array.BinarySearch(channel.Times, seconds);
            int lower = found >= 0 ? found : Math.Max(0, ~found - 1);
            lower = Math.Min(lower, channel.Times.Length - 1);
            int upper = Math.Min(lower + 1, channel.Times.Length - 1);
            float amount = channel.Step || lower == upper || seconds <= channel.Times[lower] ? 0 :
                (seconds - channel.Times[lower]) / (channel.Times[upper] - channel.Times[lower]);
            if (width == 4) Slerp(channel.Values, lower * 4, upper * 4, amount, output, destination);
            else
                for (int k = 0; k < 3; k++)
                    output[destination + k] = (float)((1.0 - amount) * channel.Values[lower * 3 + k] + amount * (double)channel.Values[upper * 3 + k]);
        }
    }

    private static void Slerp(float[] values, int a, int b, float t, float[] output, int at)
    {
        double dot = 0;
        for (int k = 0; k < 4; k++) dot += (double)values[a + k] * values[b + k];
        double sign = dot < 0 ? -1 : 1;
        dot = Math.Min(1, Math.Abs(dot));
        double left = 1 - t, right = t;
        if (dot < 0.9995)
        {
            double angle = Math.Acos(dot), sine = Math.Sin(angle);
            left = Math.Sin((1 - t) * angle) / sine;
            right = Math.Sin(t * angle) / sine;
        }
        for (int k = 0; k < 4; k++) output[at + k] = (float)(left * values[a + k] + right * sign * values[b + k]);
        Normalize(output, at);
    }

    private static void Normalize(float[] values, int at)
    {
        double length = 0;
        for (int k = 0; k < 4; k++) length += (double)values[at + k] * values[at + k];
        if (length == 0) throw new ArgumentException("Rotation quaternion must have nonzero length.");
        length = Math.Sqrt(length);
        for (int k = 0; k < 4; k++) values[at + k] = (float)(values[at + k] / length);
    }

    private static void CopyBase(float[] source, float[] destination, int at, int count)
    {
        RequireBuffer(source, count, nameof(source));
        foreach (float value in source)
            if (!Finite(value)) throw new ArgumentException("Base transforms must be finite.");
        Array.Copy(source, 0, destination, at, count);
    }
    private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    private static void RequireBuffer(float[] buffer, int length, string name)
    {
        if (buffer == null) throw new ArgumentNullException(name);
        if (buffer.Length != length) throw new ArgumentException("Incorrect transform buffer length.", name);
    }
    private sealed class Channel
    {
        internal readonly int Node;
        internal readonly string Path;
        internal readonly bool Step;
        internal readonly float[] Times, Values;
        internal Channel(int node, string path, bool step, float[] times, float[] values)
        { Node = node; Path = path; Step = step; Times = times; Values = values; }
    }
}
