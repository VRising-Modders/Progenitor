using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Progenitor.Dev.Ops;

/// <summary>
/// BRIDGEUI1: the game-free half of <c>display</c> (resolution and window mode through the game's own graphics settings
/// path) and <c>screenshot</c> (a focus-free in-process capture). Tested in <c>BridgeUiInputTests</c>.
/// </summary>
internal static class DisplayPlan
{
    internal enum Action
    {
        Status,
        Set,
        Restore,
    }

    /// <summary>Unity's <c>FullScreenMode</c> values.</summary>
    internal enum Mode
    {
        ExclusiveFullScreen = 0,
        FullScreenWindow = 1,
        MaximizedWindow = 2,
        Windowed = 3,
    }

    internal const string UsageMessage =
        "display needs args.action: status, set (args.width, args.height, args.mode windowed|fullscreenwindow|exclusive|"
        + "maximized) or restore (back to the resolution and mode before the first set).";

    internal sealed class Request
    {
        internal Action Action { get; init; }
        internal int Width { get; init; }
        internal int Height { get; init; }
        internal Mode WindowMode { get; init; }
    }

    internal static Request Parse(JsonElement args)
    {
        string action = (UiInputPlan.Text(args, "action") ?? "status").Trim().ToLowerInvariant();
        switch (action)
        {
            case "status": return new Request { Action = Action.Status };
            case "restore": return new Request { Action = Action.Restore };
            case "set":
                int w = UiInputPlan.OptInt(args, "width", 640, 16_384) ?? throw new ArgumentException("display set needs args.width (640..16384).");
                int h = UiInputPlan.OptInt(args, "height", 360, 16_384) ?? throw new ArgumentException("display set needs args.height (360..16384).");
                return new Request { Action = Action.Set, Width = w, Height = h, WindowMode = ParseMode(UiInputPlan.Text(args, "mode")) };
            default:
                throw new ArgumentException("display: args.action '" + action + "' is not known. " + UsageMessage);
        }
    }

    internal static Mode ParseMode(string? text) => (text ?? "windowed").Trim().ToLowerInvariant() switch
    {
        "windowed" or "window" => Mode.Windowed,
        "fullscreenwindow" or "borderless" => Mode.FullScreenWindow,
        "exclusive" or "fullscreen" => Mode.ExclusiveFullScreen,
        "maximized" => Mode.MaximizedWindow,
        _ => throw new ArgumentException("display: args.mode '" + text + "' is not windowed, fullscreenwindow, exclusive or maximized."),
    };

    // ---- screenshot ---------------------------------------------------------------------------------

    internal sealed class Shot
    {
        internal string? Path { get; init; }
        internal int SuperSize { get; init; } = 1;
        internal UiInputPlan.Target? Region { get; init; }

        /// <summary>A region given as a rect (screen pixels, origin bottom-left) rather than a target.</summary>
        internal float RX { get; init; }
        internal float RY { get; init; }
        internal float RW { get; init; }
        internal float RH { get; init; }
        internal bool HasRect { get; init; }
        internal float Pad { get; init; }
    }

    internal const string ShotUsage =
        "screenshot: args.path (a .png file; default BepInEx/progenitor-shots/<time>.png), args.superSize (1..4: render the "
        + "frame at that multiple of the window's size), and optionally a region: args.rect {x, y, w, h} in screen pixels "
        + "(origin bottom-left), or args.row (and args.cell) or args.panel, with args.pad pixels around it.";

    internal static Shot ParseShot(JsonElement args)
    {
        string? path = UiInputPlan.Text(args, "path");
        if (path != null && !path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("screenshot: args.path ends in .png.");
        int super = UiInputPlan.OptInt(args, "superSize", 1, 4) ?? 1;
        float pad = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("pad", out _) ? UiInputPlan.Num(args, "pad", 0, 2000) : 0f;
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("rect", out var r))
        {
            if (r.ValueKind != JsonValueKind.Object) throw new ArgumentException("screenshot: args.rect is {x, y, w, h}.");
            return new Shot
            {
                Path = path,
                SuperSize = super,
                HasRect = true,
                RX = UiInputPlan.Num(r, "x", 0, 100_000),
                RY = UiInputPlan.Num(r, "y", 0, 100_000),
                RW = UiInputPlan.Num(r, "w", 1, 100_000),
                RH = UiInputPlan.Num(r, "h", 1, 100_000),
                Pad = pad,
            };
        }

        var target = UiInputPlan.ParseTarget(args, "screenshot");
        if (target.HasPoint) throw new ArgumentException("screenshot: a region is args.rect, args.row or args.panel (a point has no size).");
        return new Shot { Path = path, SuperSize = super, Region = target.IsEmpty ? null : target, Pad = pad };
    }

    /// <summary>
    /// A screen region (origin bottom-left, in window pixels) as a pixel crop of an image <paramref name="scale"/> times
    /// the window's size, clamped to the image: (x, y from the image's bottom, width, height). Null when it misses.
    /// </summary>
    internal static (int X, int Y, int W, int H)? Crop(float x, float y, float w, float h, float pad, int scale, int imageW, int imageH)
    {
        if (scale < 1) scale = 1;
        double x0 = Math.Floor((x - pad) * scale), y0 = Math.Floor((y - pad) * scale);
        double x1 = Math.Ceiling((x + w + pad) * scale), y1 = Math.Ceiling((y + h + pad) * scale);
        x0 = Math.Max(0, x0);
        y0 = Math.Max(0, y0);
        x1 = Math.Min(imageW, x1);
        y1 = Math.Min(imageH, y1);
        if (x1 - x0 < 1 || y1 - y0 < 1) return null;
        return ((int)x0, (int)y0, (int)(x1 - x0), (int)(y1 - y0));
    }

    /// <summary>
    /// Copy a <paramref name="w"/>×<paramref name="h"/> block at (<paramref name="x"/>, <paramref name="y"/>) out of a
    /// tightly packed image of <paramref name="bpp"/> bytes a pixel (rows bottom-up, as a texture stores them).
    /// </summary>
    internal static byte[] CropPixels(byte[] src, int srcW, int srcH, int bpp, int x, int y, int w, int h)
    {
        if (bpp < 1 || bpp > 16) throw new ArgumentOutOfRangeException(nameof(bpp));
        if (src.Length < srcW * srcH * bpp) throw new ArgumentException("the image is shorter than " + srcW + "x" + srcH + " at " + bpp + " bytes a pixel");
        if (x < 0 || y < 0 || w < 1 || h < 1 || x + w > srcW || y + h > srcH) throw new ArgumentOutOfRangeException(nameof(w), "the crop leaves the image");
        var dst = new byte[w * h * bpp];
        for (int row = 0; row < h; row++)
            Buffer.BlockCopy(src, ((y + row) * srcW + x) * bpp, dst, row * w * bpp, w * bpp);
        return dst;
    }

    /// <summary>The default capture path under <paramref name="root"/>, named by the UTC time.</summary>
    internal static string DefaultShotPath(string root, DateTime utc) =>
        System.IO.Path.Combine(root, "progenitor-shots", utc.ToString("yyyyMMdd-HHmmss-fff") + ".png");

    /// <summary>The width and height in a PNG's IHDR (big-endian at bytes 16..23), or null when it is not a PNG.</summary>
    internal static (int W, int H)? PngSize(byte[] head)
    {
        if (head == null || head.Length < 24) return null;
        if (head[0] != 0x89 || head[1] != (byte)'P' || head[2] != (byte)'N' || head[3] != (byte)'G') return null;
        int w = (head[16] << 24) | (head[17] << 16) | (head[18] << 8) | head[19];
        int h = (head[20] << 24) | (head[21] << 16) | (head[22] << 8) | head[23];
        return w > 0 && h > 0 ? (w, h) : null;
    }

    internal static Dictionary<string, object?> Baseline(int w, int h, int mode) => new(StringComparer.Ordinal)
    {
        ["width"] = w,
        ["height"] = h,
        ["mode"] = ((Mode)mode).ToString(),
    };
}
