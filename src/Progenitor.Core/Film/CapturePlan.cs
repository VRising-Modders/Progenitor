using System;
using System.Collections.Generic;
using System.Globalization;

namespace Progenitor.Film;

/// <summary>
/// What a <c>FilmCapture</c> records and how: frame range, rate, size, warm-up, and the ffmpeg
/// command that receives the raw frames. Game-free, so the argument building is unit-tested.
/// </summary>
public sealed class CapturePlan
{
    public CapturePlan(string output, int fps, int width, int height, int frames)
    {
        if (string.IsNullOrWhiteSpace(output)) throw new ArgumentException("an output path is required.", nameof(output));
        if (fps < 1 || fps > 240) throw new ArgumentOutOfRangeException(nameof(fps), fps, "1..240");
        if (width < 16 || height < 16 || width > 8192 || height > 8192 || width % 2 != 0 || height % 2 != 0)
            throw new ArgumentOutOfRangeException(nameof(width), width + "x" + height, "even, 16..8192 each");
        if (frames < 1) throw new ArgumentOutOfRangeException(nameof(frames), frames, "at least 1");
        Output = output;
        Fps = fps;
        Width = width;
        Height = height;
        Frames = frames;
    }

    /// <summary>A video file (.mp4, .mov, .mkv) or an image sequence pattern (<c>frames/f_%05d.png</c>).</summary>
    public string Output { get; }
    public int Fps { get; }
    public int Width { get; }
    public int Height { get; }
    /// <summary>Frames written to the output.</summary>
    public int Frames { get; }
    /// <summary>
    /// Frames rendered and thrown away before recording, so temporal anti-aliasing, auto-exposure
    /// and the game's own streaming settle. Staged with frame 0.
    /// </summary>
    public int WarmupFrames { get; set; } = 12;
    /// <summary>x264 quality for video outputs (lower is better).</summary>
    public int Crf { get; set; } = 16;
    /// <summary>The ffmpeg executable; a bare name is looked up on PATH.</summary>
    public string Ffmpeg { get; set; } = "ffmpeg";

    public bool IsImageSequence => Output.Contains("%", StringComparison.Ordinal);

    /// <summary>Bytes of one raw RGB24 frame.</summary>
    public int FrameBytes => Width * Height * 3;

    /// <summary>
    /// ffmpeg arguments: raw RGB24 frames on stdin, rows bottom-up as Unity reads them (so the
    /// first filter is <c>vflip</c>), at <see cref="Fps"/>; H.264 yuv420p for a video, lossless
    /// PNG for an image sequence.
    /// </summary>
    public IReadOnlyList<string> FfmpegArguments()
    {
        var ic = CultureInfo.InvariantCulture;
        var a = new List<string>
        {
            "-hide_banner", "-loglevel", "error", "-y",
            "-f", "rawvideo", "-pix_fmt", "rgb24",
            "-s", Width.ToString(ic) + "x" + Height.ToString(ic),
            "-r", Fps.ToString(ic),
            "-i", "-",
            "-vf", "vflip",
        };
        if (IsImageSequence)
        {
            a.AddRange(new[] { "-start_number", "0", Output });
        }
        else
        {
            a.AddRange(new[] { "-c:v", "libx264", "-preset", "slow", "-crf", Crf.ToString(ic), "-pix_fmt", "yuv420p", "-movflags", "+faststart", Output });
        }
        return a;
    }

    /// <summary>For logs: the arguments joined with quoting where needed.</summary>
    public string CommandLine()
    {
        var parts = new List<string> { Quote(Ffmpeg) };
        foreach (var x in FfmpegArguments()) parts.Add(Quote(x));
        return string.Join(" ", parts);
    }

    private static string Quote(string s) => s.IndexOfAny(new[] { ' ', '"' }) >= 0 ? "\"" + s.Replace("\"", "\\\"") + "\"" : s;
}

/// <summary>
/// Frame bookkeeping for a capture: which take frame to stage on each game frame, including
/// warm-up (which stages frame 0 and records nothing). Game-free.
/// </summary>
public sealed class CaptureClock
{
    private readonly CapturePlan _plan;
    private int _tick;

    public CaptureClock(CapturePlan plan) => _plan = plan ?? throw new ArgumentNullException(nameof(plan));

    /// <summary>Game frames stepped so far.</summary>
    public int Ticks => _tick;

    /// <summary>The film frame to stage now (0 during warm-up).</summary>
    public int FilmFrame => Math.Max(0, _tick - _plan.WarmupFrames);

    /// <summary>True when the frame rendered now is written to the output.</summary>
    public bool Recording => _tick >= _plan.WarmupFrames && !Done;

    /// <summary>Frames written so far.</summary>
    public int Written => Math.Clamp(_tick - _plan.WarmupFrames, 0, _plan.Frames);

    public bool Done => _tick >= _plan.WarmupFrames + _plan.Frames;

    public void Advance() => _tick++;
}
