using System;
using System.Collections.Generic;
using System.IO;
using Progenitor.Film;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// <c>TakeFile</c>, the recorded take behind in-game film playback: the float layout (actors, then
/// camera, then props), frame lookup, and loud refusal of a layout that does not match its data.
/// </summary>
public class TakeFileTests
{
    private static (string Json, byte[] Bin) Take(int frames, bool camera = true, int bones = 2, int? declared = null, int version = 1)
    {
        int stride = (3 + 4 + 1 + 3 + 4 * bones) + (camera ? 10 : 0) + 7;
        var floats = new List<float>();
        for (int f = 0; f < frames; f++)
        {
            // actor: root pos, rot, scale, pelvis, bone deltas; values encode the frame for checking
            floats.AddRange(new[] { f + 0.1f, f + 0.2f, f + 0.3f, 0f, 0f, 0f, 1f, 0.55f, 1f, 2f, 3f });
            for (int b = 0; b < bones; b++) floats.AddRange(new[] { b, f, 0f, 1f });
            if (camera) floats.AddRange(new[] { 10f + f, 11f, 12f, 0f, 0f, 1f, 0f, 1f, 0f, 35f });
            floats.AddRange(new[] { 20f + f, 21f, 22f, 0f, 0f, 0f, 1f });
        }
        var bin = new byte[floats.Count * 4];
        Buffer.BlockCopy(floats.ToArray(), 0, bin, 0, bin.Length);
        var boneNames = new List<string>();
        for (int b = 0; b < bones; b++) boneNames.Add("\"B" + b + "\"");
        string json = "{\"version\":" + version + ",\"fps\":24,\"first\":1500,\"frames\":" + frames + ",\"floatsPerFrame\":" + (declared ?? stride) +
                      ",\"actors\":[{\"name\":\"Dracula\",\"bones\":[" + string.Join(",", boneNames) + "]}],\"camera\":" + (camera ? "true" : "false") + ",\"props\":[\"Portrait_frame\"]}";
        return (json, bin);
    }

    [Fact]
    public void Layout_reads_actor_camera_and_prop_values_per_frame()
    {
        var (json, bin) = Take(3);
        var t = TakeFile.Parse(json, bin);
        Assert.Equal(24, t.Fps);
        Assert.Equal(1500, t.FirstFrame);
        Assert.Equal(3, t.Frames);
        Assert.Equal(0, t.ActorIndex("Dracula"));
        Assert.Equal(-1, t.ActorIndex("Nobody"));

        var root = t.ActorRoot(2, 0);
        Assert.Equal(2.1f, root.X, 5);
        Assert.Equal(1f, root.Qw);
        Assert.Equal(0.55f, root.Scale);
        Assert.Equal((1f, 2f, 3f), t.Pelvis(2, 0));
        Assert.Equal((1f, 2f, 0f, 1f), t.BoneDelta(2, 0, 1));

        var cam = t.Camera(1);
        Assert.Equal(11f, cam.Position.X);
        Assert.Equal((0f, 0f, 1f), cam.Forward);
        Assert.Equal(35f, cam.VerticalFov);

        Assert.Equal(22f, t.Prop(2, 0).X);
    }

    [Fact]
    public void A_take_without_a_camera_skips_it()
    {
        var (json, bin) = Take(2, camera: false);
        var t = TakeFile.Parse(json, bin);
        Assert.False(t.HasCamera);
        Assert.Equal(21f, t.Prop(1, 0).X);
        Assert.Throws<InvalidOperationException>(() => t.Camera(0));
    }

    [Fact]
    public void Time_maps_to_frames_and_is_clamped()
    {
        var (json, bin) = Take(48);
        var t = TakeFile.Parse(json, bin);
        Assert.Equal(2.0, t.DurationSeconds);
        Assert.Equal(0, t.FrameAt(0));
        Assert.Equal(24, t.FrameAt(1.0));
        Assert.Equal(47, t.FrameAt(99));
        Assert.Equal(0, t.FrameAt(-1));
    }

    [Fact]
    public void Out_of_range_frames_and_bones_throw()
    {
        var (json, bin) = Take(2);
        var t = TakeFile.Parse(json, bin);
        Assert.Throws<ArgumentOutOfRangeException>(() => t.ActorRoot(2, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => t.BoneDelta(0, 0, 5));
    }

    [Fact]
    public void A_layout_that_does_not_match_the_data_is_refused()
    {
        var (json, bin) = Take(2, declared: 99);
        Assert.Contains("floats per frame", Assert.Throws<FormatException>(() => TakeFile.Parse(json, bin)).Message);
        (json, bin) = Take(2);
        Assert.Contains("take.bin has", Assert.Throws<FormatException>(() => TakeFile.Parse(json, bin[..^4])).Message);
        (json, bin) = Take(2, version: 2);
        Assert.Contains("version", Assert.Throws<FormatException>(() => TakeFile.Parse(json, bin)).Message);
    }

    [Fact]
    public void Load_reads_the_two_files_from_a_directory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "progenitor-take-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var (json, bin) = Take(4);
            File.WriteAllText(Path.Combine(dir, "take.json"), json);
            File.WriteAllBytes(Path.Combine(dir, "take.bin"), bin);
            Assert.Equal(4, TakeFile.Load(dir).Frames);
        }
        finally { Directory.Delete(dir, true); }
    }
}
