#if !PROGENITOR_TESTS
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using UnityEngine;

namespace Progenitor.Film;

/// <summary>
/// Lock-step recording of a <see cref="FilmCamera"/> into a video or an image sequence. While it
/// runs, <c>Time.captureFramerate</c> is the plan's rate, so every game frame advances game time by
/// exactly one film frame however long it takes to render; each frame it calls the stage callback
/// with the film frame to show, renders, reads back and hands the raw frame to a background thread
/// that writes it to an ffmpeg process (see <see cref="CapturePlan.FfmpegArguments"/>).
///
/// <para>Game time is the client's. Units the server moves (NPCs, other players) move at server
/// speed; slow the server to the capture's real frame rate (<see cref="RealFps"/>) with
/// <c>ctx.Time.SlowMotion</c> to keep them in step.</para>
///
/// <para>Stop, dispose or unload ends the capture, restores <c>Time.captureFramerate</c> and closes
/// ffmpeg, which finalizes what was written.</para>
/// </summary>
public sealed class FilmCapture : IDisposable
{
    private readonly FilmCamera _camera;
    private readonly CapturePlan _plan;
    private readonly Action<int> _stage;
    private readonly CaptureClock _clock;
    private readonly BlockingCollection<byte[]> _queue = new(boundedCapacity: 8);
    private readonly ConcurrentBag<byte[]> _free = new();
    private readonly Thread _writer;
    private readonly Process _ffmpeg;
    private readonly int _savedCaptureFramerate;
    private readonly Stopwatch _wall = Stopwatch.StartNew();
    private volatile string? _writerError;
    private bool _finished;

    internal FilmCapture(FilmCamera camera, CapturePlan plan, Action<int> stage)
    {
        _camera = camera ?? throw new ArgumentNullException(nameof(camera));
        _plan = plan ?? throw new ArgumentNullException(nameof(plan));
        _stage = stage ?? throw new ArgumentNullException(nameof(stage));
        if (camera.Width != plan.Width || camera.Height != plan.Height)
            throw new ArgumentException("the camera renders " + camera.Width + "x" + camera.Height + " but the plan is " + plan.Width + "x" + plan.Height + ".");
        _clock = new CaptureClock(plan);

        var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(plan.Output));
        if (!string.IsNullOrEmpty(dir)) System.IO.Directory.CreateDirectory(dir);

        var psi = new ProcessStartInfo(plan.Ffmpeg)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var a in plan.FfmpegArguments()) psi.ArgumentList.Add(a);
        _ffmpeg = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg did not start: " + plan.CommandLine());
        _ffmpeg.ErrorDataReceived += (_, e) => { if (!string.IsNullOrEmpty(e.Data)) Plugin.Logger?.LogWarning("Film: ffmpeg: " + e.Data); };
        _ffmpeg.BeginErrorReadLine();

        _writer = new Thread(WriteLoop) { IsBackground = true, Name = "progenitor-film-writer" };
        _writer.Start();

        _savedCaptureFramerate = Time.captureFramerate;
        Time.captureFramerate = plan.Fps;
        Plugin.Logger?.LogInfo("Film: capture started: " + plan.Frames + " frames at " + plan.Fps + " fps, " + plan.Width + "x" + plan.Height + " -> " + plan.Output);
    }

    public CapturePlan Plan => _plan;
    public int Written => _clock.Written;
    public bool Done => _finished;

    /// <summary>Film frames written per wall-clock second so far.</summary>
    public double RealFps => _wall.Elapsed.TotalSeconds > 0 ? _clock.Written / _wall.Elapsed.TotalSeconds : 0;

    /// <summary>Why the capture stopped early, or null.</summary>
    public string? Error => _writerError;

    public event Action<FilmCapture>? Finished;

    /// <summary>One game frame: stage, render, read back, queue. Called by the Film frame driver.</summary>
    internal void Step()
    {
        if (_finished) return;
        if (_writerError != null) { Finish(); return; }
        try
        {
            _stage(_clock.FilmFrame);
            _camera.Render();
            if (_clock.Recording)
            {
                if (!_free.TryTake(out var buffer)) buffer = new byte[_plan.FrameBytes];
                _camera.ReadRgb24(buffer);
                _queue.Add(buffer);
            }
        }
        catch (Exception e)
        {
            _writerError = "frame " + _clock.FilmFrame + ": " + e.Message;
            Finish();
            return;
        }
        _clock.Advance();
        if (_clock.Done) Finish();
    }

    private void WriteLoop()
    {
        try
        {
            var stdin = _ffmpeg.StandardInput.BaseStream;
            foreach (var frame in _queue.GetConsumingEnumerable())
            {
                stdin.Write(frame, 0, frame.Length);
                _free.Add(frame);
            }
            stdin.Flush();
            stdin.Close();
        }
        catch (Exception e)
        {
            _writerError = "writing to ffmpeg: " + e.Message;
        }
    }

    private void Finish()
    {
        if (_finished) return;
        _finished = true;
        Time.captureFramerate = _savedCaptureFramerate;
        _queue.CompleteAdding();
        try { _writer.Join(TimeSpan.FromSeconds(30)); } catch { }
        try { if (!_ffmpeg.WaitForExit(60000)) _ffmpeg.Kill(); } catch { }
        Plugin.Logger?.LogInfo("Film: capture " + (_writerError == null ? "finished" : "stopped (" + _writerError + ")") + ": " + _clock.Written + " frames in "
                               + _wall.Elapsed.TotalSeconds.ToString("0.0") + " s (" + RealFps.ToString("0.0") + " fps) -> " + _plan.Output
                               + (_ffmpeg.HasExited ? ", ffmpeg exit " + _ffmpeg.ExitCode : ""));
        try { Finished?.Invoke(this); } catch (Exception e) { Plugin.Logger?.LogWarning("Film: capture Finished handler threw: " + e.Message); }
        FilmFrame.Forget(this);
    }

    /// <summary>Stops now; what was written stays a valid file.</summary>
    public void Stop() => Finish();

    public void Dispose() => Finish();
}

/// <summary>The <see cref="Plugin.Frame"/> subscription that steps live captures.</summary>
internal static class FilmFrame
{
    private static readonly System.Collections.Generic.List<FilmCapture> Live = new();
    private static bool _armed;

    internal static void Add(FilmCapture c)
    {
        Live.Add(c);
        if (_armed) return;
        Plugin.Frame += OnFrame;
        _armed = true;
    }

    internal static void Forget(FilmCapture c)
    {
        Live.Remove(c);
        if (Live.Count > 0 || !_armed) return;
        try { Plugin.Frame -= OnFrame; } catch { }
        _armed = false;
    }

    private static void OnFrame()
    {
        foreach (var c in Live.ToArray()) c.Step();
    }

    internal static void Uninstall()
    {
        foreach (var c in Live.ToArray())
        {
            try { c.Stop(); } catch { }
        }
        Live.Clear();
        if (_armed) { try { Plugin.Frame -= OnFrame; } catch { } _armed = false; }
    }
}
#endif
