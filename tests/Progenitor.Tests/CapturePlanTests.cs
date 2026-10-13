using System;
using System.Linq;
using Progenitor.Film;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// <c>CapturePlan</c> (validation and the ffmpeg command for raw bottom-up RGB24 frames) and
/// <c>CaptureClock</c> (warm-up, recording and the film frame staged on each game frame).
/// </summary>
public class CapturePlanTests
{
    [Fact]
    public void Video_output_encodes_H264_from_raw_rgb24_flipped()
    {
        var p = new CapturePlan(@"C:\film\shot.mp4", 24, 1920, 1080, 120) { Crf = 14 };
        var a = p.FfmpegArguments().ToList();
        Assert.Equal("rawvideo", a[a.IndexOf("-f") + 1]);
        Assert.Equal("rgb24", a[a.IndexOf("-pix_fmt") + 1]);
        Assert.Equal("1920x1080", a[a.IndexOf("-s") + 1]);
        Assert.Equal("24", a[a.IndexOf("-r") + 1]);
        Assert.Equal("-", a[a.IndexOf("-i") + 1]);
        Assert.Equal("vflip", a[a.IndexOf("-vf") + 1]);
        Assert.Equal("libx264", a[a.IndexOf("-c:v") + 1]);
        Assert.Equal("14", a[a.IndexOf("-crf") + 1]);
        Assert.Equal(@"C:\film\shot.mp4", a[^1]);
        Assert.False(p.IsImageSequence);
        Assert.Equal(1920 * 1080 * 3, p.FrameBytes);
    }

    [Fact]
    public void An_image_sequence_pattern_writes_numbered_pngs_from_zero()
    {
        var p = new CapturePlan(@"C:\film\frames\f_%05d.png", 24, 1280, 720, 10);
        var a = p.FfmpegArguments().ToList();
        Assert.True(p.IsImageSequence);
        Assert.DoesNotContain("libx264", a);
        Assert.Equal("0", a[a.IndexOf("-start_number") + 1]);
        Assert.Equal(@"C:\film\frames\f_%05d.png", a[^1]);
    }

    [Fact]
    public void Command_line_quotes_paths_with_spaces()
    {
        var p = new CapturePlan(@"C:\my film\shot.mp4", 24, 640, 360, 1) { Ffmpeg = @"C:\tools\ffmpeg.exe" };
        Assert.EndsWith("\"C:\\my film\\shot.mp4\"", p.CommandLine());
        Assert.StartsWith(@"C:\tools\ffmpeg.exe ", p.CommandLine());
    }

    [Theory]
    [InlineData("", 24, 640, 360, 1)]
    [InlineData("x.mp4", 0, 640, 360, 1)]
    [InlineData("x.mp4", 24, 641, 360, 1)]
    [InlineData("x.mp4", 24, 8, 360, 1)]
    [InlineData("x.mp4", 24, 640, 360, 0)]
    public void Bad_plans_are_refused(string output, int fps, int w, int h, int frames)
    {
        Assert.ThrowsAny<ArgumentException>(() => new CapturePlan(output, fps, w, h, frames));
    }

    [Fact]
    public void Clock_warms_up_on_frame_zero_then_records_each_frame_once()
    {
        var p = new CapturePlan("x.mp4", 24, 640, 360, 3) { WarmupFrames = 2 };
        var c = new CaptureClock(p);
        var staged = new System.Collections.Generic.List<(int Frame, bool Rec)>();
        while (!c.Done)
        {
            staged.Add((c.FilmFrame, c.Recording));
            c.Advance();
        }
        Assert.Equal(new[] { (0, false), (0, false), (0, true), (1, true), (2, true) }, staged);
        Assert.Equal(3, c.Written);
        Assert.False(c.Recording);
    }
}
