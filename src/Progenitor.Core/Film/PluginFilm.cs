#if !PROGENITOR_TESTS
using System;
using Progenitor.Plugins;

namespace Progenitor.Film;

/// <summary>
/// <see cref="PluginContext.Film"/>: film cameras and lock-step capture. <b>Client only</b>; every
/// member throws on the server. Cameras and captures are tracked: unload stops captures (the file
/// written so far stays valid), restores the frame rate and releases the cameras.
///
/// <code>
/// var cam = ctx.Film.CreateCamera(1920, 1080);
/// var plan = new CapturePlan(@"C:\film\take.mp4", fps: 24, width: 1920, height: 1080, frames: take.Frames);
/// ctx.Film.Capture(cam, plan, frame => { /* pose puppets, move props, cam.Set(...) for this frame */ });
/// </code>
/// See <c>docs/modules.md</c>, "Film".
/// </summary>
public sealed class PluginFilm
{
    private readonly PluginContext _ctx;

    internal PluginFilm(PluginContext ctx) => _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));

    /// <summary>An offscreen camera rendering <paramref name="width"/> x <paramref name="height"/>.</summary>
    public FilmCamera CreateCamera(int width = 1920, int height = 1080)
    {
        _ctx.EnsureAttached();
        if (!_ctx.IsClient) throw new InvalidOperationException("ctx.Film is client-only.");
        var cam = new FilmCamera(_ctx.Name, width, height);
        _ctx.Track(cam);
        return cam;
    }

    /// <summary>
    /// Starts recording <paramref name="camera"/> with <paramref name="plan"/>. From the next frame,
    /// every game frame calls <paramref name="stage"/> with the film frame to show (0 during
    /// warm-up), renders and writes one frame, until <see cref="CapturePlan.Frames"/> are written.
    /// </summary>
    public FilmCapture Capture(FilmCamera camera, CapturePlan plan, Action<int> stage)
    {
        _ctx.EnsureAttached();
        if (!_ctx.IsClient) throw new InvalidOperationException("ctx.Film is client-only.");
        var capture = new FilmCapture(camera, plan, stage);
        FilmFrame.Add(capture);
        _ctx.Track(capture);
        return capture;
    }
}
#endif
