using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Progenitor.Film;

/// <summary>
/// A recorded take: per-frame character poses, a camera path and prop paths, exported from an
/// animation tool (the film's Blender scene, via <c>export_take.py</c>) in Unity's axes. Two files:
/// <c>take.json</c> (layout) and <c>take.bin</c> (little-endian float32, frame-major).
///
/// <para>Per frame, in order: for each actor its root position (3), root rotation quaternion xyzw
/// (4), root uniform scale (1), pelvis model-space position (3), then for each bone the model-space
/// rotation delta from the bind pose, xyzw (4); if the take has a camera, its position (3), forward
/// (3), up (3) and vertical field of view in degrees (1); for each prop its position (3) and rotation
/// xyzw (4). A bone delta D is pose × bind⁻¹ in the rig's model space, so it does not depend on how
/// the authoring tool oriented its bones: the joint's model rotation is D × its own bind rotation.</para>
///
/// No Unity types: tested offline. Parsed with <see cref="JsonDocument"/> only.
/// </summary>
public sealed class TakeFile
{
    private readonly float[] _data;
    private readonly int[] _actorOffset;
    private readonly int _cameraOffset;
    private readonly int _propOffset;

    private TakeFile(int fps, int first, int frames, List<TakeActor> actors, bool camera, List<string> props, int stride, float[] data)
    {
        Fps = fps;
        FirstFrame = first;
        Frames = frames;
        Actors = actors;
        HasCamera = camera;
        Props = props;
        FloatsPerFrame = stride;
        _data = data;

        _actorOffset = new int[actors.Count];
        int at = 0;
        for (int i = 0; i < actors.Count; i++)
        {
            _actorOffset[i] = at;
            at += ActorFloats(actors[i].Bones.Count);
        }
        _cameraOffset = at;
        if (camera) at += 10;
        _propOffset = at;
        at += 7 * props.Count;
        if (at != stride) throw new FormatException("take layout needs " + at + " floats per frame but declares " + stride + ".");
        if ((long)stride * frames != data.Length) throw new FormatException("take.bin has " + data.Length + " floats; " + frames + " frames × " + stride + " needs " + (long)stride * frames + ".");
    }

    public int Fps { get; }
    /// <summary>The authoring tool's number for frame 0 of this file.</summary>
    public int FirstFrame { get; }
    public int Frames { get; }
    public IReadOnlyList<TakeActor> Actors { get; }
    public bool HasCamera { get; }
    public IReadOnlyList<string> Props { get; }
    public int FloatsPerFrame { get; }

    public double DurationSeconds => Fps > 0 ? (double)Frames / Fps : 0;

    private static int ActorFloats(int bones) => 3 + 4 + 1 + 3 + 4 * bones;

    public static TakeFile Load(string directory) =>
        Parse(File.ReadAllText(Path.Combine(directory, "take.json")), File.ReadAllBytes(Path.Combine(directory, "take.bin")));

    public static TakeFile Parse(string json, byte[] bin)
    {
        if (bin == null || bin.Length % 4 != 0) throw new FormatException("take.bin length is not a multiple of 4.");
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        int version = r.TryGetProperty("version", out var v) ? v.GetInt32() : 0;
        if (version != 1) throw new FormatException("take version " + version + " is not supported (1 is).");
        int fps = Req(r, "fps"), frames = Req(r, "frames"), stride = Req(r, "floatsPerFrame");
        int first = r.TryGetProperty("first", out var fEl) ? fEl.GetInt32() : 0;
        if (fps <= 0 || frames <= 0 || stride <= 0) throw new FormatException("fps, frames and floatsPerFrame must be positive.");

        var actors = new List<TakeActor>();
        if (r.TryGetProperty("actors", out var actorsEl))
            foreach (var a in actorsEl.EnumerateArray())
            {
                var bones = new List<string>();
                foreach (var b in a.GetProperty("bones").EnumerateArray()) bones.Add(b.GetString() ?? "");
                actors.Add(new TakeActor(a.GetProperty("name").GetString() ?? "", bones));
            }
        bool camera = r.TryGetProperty("camera", out var c) && c.ValueKind == JsonValueKind.True;
        var props = new List<string>();
        if (r.TryGetProperty("props", out var p))
            foreach (var x in p.EnumerateArray()) props.Add(x.GetString() ?? "");

        var data = new float[bin.Length / 4];
        Buffer.BlockCopy(bin, 0, data, 0, bin.Length);
        if (!BitConverter.IsLittleEndian) throw new PlatformNotSupportedException("big-endian hosts are not supported.");
        return new TakeFile(fps, first, frames, actors, camera, props, stride, data);
    }

    private static int Req(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : throw new FormatException("take.json needs '" + name + "'.");

    /// <summary>Frame index (0-based) for a time, clamped to the take.</summary>
    public int FrameAt(double seconds) => Math.Clamp((int)Math.Floor(seconds * Fps + 1e-6), 0, Frames - 1);

    public int ActorIndex(string name)
    {
        for (int i = 0; i < Actors.Count; i++) if (Actors[i].Name == name) return i;
        return -1;
    }

    private int Base(int frame, int offset)
    {
        if ((uint)frame >= (uint)Frames) throw new ArgumentOutOfRangeException(nameof(frame), frame, "frame is outside the take.");
        return frame * FloatsPerFrame + offset;
    }

    /// <summary>Root position (3), rotation xyzw (4) and uniform scale of an actor.</summary>
    public TakeTransform ActorRoot(int frame, int actor)
    {
        int b = Base(frame, _actorOffset[actor]);
        return new TakeTransform(_data[b], _data[b + 1], _data[b + 2], _data[b + 3], _data[b + 4], _data[b + 5], _data[b + 6], _data[b + 7]);
    }

    /// <summary>Pelvis position in the actor's model space, rig units.</summary>
    public (float X, float Y, float Z) Pelvis(int frame, int actor)
    {
        int b = Base(frame, _actorOffset[actor]) + 8;
        return (_data[b], _data[b + 1], _data[b + 2]);
    }

    /// <summary>The model-space rotation delta from the bind pose of one bone, xyzw.</summary>
    public (float X, float Y, float Z, float W) BoneDelta(int frame, int actor, int bone)
    {
        if ((uint)bone >= (uint)Actors[actor].Bones.Count) throw new ArgumentOutOfRangeException(nameof(bone));
        int b = Base(frame, _actorOffset[actor]) + 11 + bone * 4;
        return (_data[b], _data[b + 1], _data[b + 2], _data[b + 3]);
    }

    public TakeCamera Camera(int frame)
    {
        if (!HasCamera) throw new InvalidOperationException("this take has no camera.");
        int b = Base(frame, _cameraOffset);
        return new TakeCamera(
            (_data[b], _data[b + 1], _data[b + 2]),
            (_data[b + 3], _data[b + 4], _data[b + 5]),
            (_data[b + 6], _data[b + 7], _data[b + 8]),
            _data[b + 9]);
    }

    public TakeTransform Prop(int frame, int prop)
    {
        if ((uint)prop >= (uint)Props.Count) throw new ArgumentOutOfRangeException(nameof(prop));
        int b = Base(frame, _propOffset) + prop * 7;
        return new TakeTransform(_data[b], _data[b + 1], _data[b + 2], _data[b + 3], _data[b + 4], _data[b + 5], _data[b + 6], 1f);
    }
}

public sealed class TakeActor
{
    internal TakeActor(string name, List<string> bones)
    {
        Name = name;
        Bones = bones;
    }

    public string Name { get; }
    public IReadOnlyList<string> Bones { get; }
}

public readonly struct TakeTransform
{
    public TakeTransform(float x, float y, float z, float qx, float qy, float qz, float qw, float scale)
    {
        X = x; Y = y; Z = z; Qx = qx; Qy = qy; Qz = qz; Qw = qw; Scale = scale;
    }

    public float X { get; }
    public float Y { get; }
    public float Z { get; }
    public float Qx { get; }
    public float Qy { get; }
    public float Qz { get; }
    public float Qw { get; }
    public float Scale { get; }
}

public readonly struct TakeCamera
{
    public TakeCamera((float, float, float) position, (float, float, float) forward, (float, float, float) up, float verticalFov)
    {
        Position = position;
        Forward = forward;
        Up = up;
        VerticalFov = verticalFov;
    }

    public (float X, float Y, float Z) Position { get; }
    public (float X, float Y, float Z) Forward { get; }
    public (float X, float Y, float Z) Up { get; }
    public float VerticalFov { get; }
}
