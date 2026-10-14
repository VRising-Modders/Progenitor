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

    /// <summary>
    /// Unity's <c>FullScreenMode</c> values the op may set. Exclusive full screen is not offered: it changes the display
    /// mode of the user's monitor, and a failed restore leaves the monitor at the test resolution.
    /// </summary>
    internal enum Mode
    {
        FullScreenWindow = 1,
        MaximizedWindow = 2,
        Windowed = 3,
    }

    internal const string UsageMessage =
        "display needs args.action: status, set (args.width, args.height, args.mode windowed|fullscreenwindow|maximized; "
        + "refused while a client world exists unless args.allowInWorld is true) or restore (back to the resolution, the mode "
        + "and the saved graphics settings before the first set).";

    internal sealed class Request
    {
        internal Action Action { get; init; }
        internal int Width { get; init; }
        internal int Height { get; init; }
        internal Mode WindowMode { get; init; }
        internal bool AllowInWorld { get; init; }
    }

    internal static Request Parse(JsonElement args)
    {
        string action = (UiInputPlan.Text(args, "action") ?? "status").Trim().ToLowerInvariant();
        bool allowInWorld = UiInputPlan.OptBool(args, "allowInWorld");
        switch (action)
        {
            case "status": return new Request { Action = Action.Status };
            case "restore": return new Request { Action = Action.Restore };
            case "set":
                int w = UiInputPlan.OptInt(args, "width", 640, 16_384) ?? throw new ArgumentException("display set needs args.width (640..16384).");
                int h = UiInputPlan.OptInt(args, "height", 360, 16_384) ?? throw new ArgumentException("display set needs args.height (360..16384).");
                return new Request { Action = Action.Set, Width = w, Height = h, WindowMode = ParseMode(UiInputPlan.Text(args, "mode")), AllowInWorld = allowInWorld };
            default:
                throw new ArgumentException("display: args.action '" + action + "' is not known. " + UsageMessage);
        }
    }

    internal static Mode ParseMode(string? text) => (text ?? "windowed").Trim().ToLowerInvariant() switch
    {
        "windowed" or "window" => Mode.Windowed,
        "fullscreenwindow" or "borderless" => Mode.FullScreenWindow,
        "maximized" => Mode.MaximizedWindow,
        "exclusive" or "fullscreen" => throw new ArgumentException("display: exclusive full screen is not offered (it changes the monitor's own display mode); use fullscreenwindow."),
        _ => throw new ArgumentException("display: args.mode '" + text + "' is not windowed, fullscreenwindow or maximized."),
    };

    /// <summary>Why a <c>set</c> is refused now, or null: a client world exists and the caller did not opt in.</summary>
    internal static string? InWorldRefusal(bool clientWorldExists, bool allowInWorld) =>
        clientWorldExists && !allowInWorld
            ? "a client world exists (someone may be playing); pass allowInWorld: true for an authorized in-world test"
            : null;

    /// <summary>
    /// The state <c>restore</c> returns to, kept on disk (<see cref="BaselineFileName"/> under BepInEx/config) so a Dev
    /// reload, a Core swap or a crash between <c>set</c> and <c>restore</c> does not lose it.
    /// </summary>
    internal sealed class Baseline
    {
        public int Width { get; set; }
        public int Height { get; set; }
        public int Mode { get; set; }

        /// <summary>The game's saved graphics fields before the first set; -1 when unreadable.</summary>
        public int SettingsWidth { get; set; } = -1;
        public int SettingsHeight { get; set; } = -1;
        public int SettingsWindowMode { get; set; } = -1;

        /// <summary>A set changed the game's in-memory graphics settings.</summary>
        public bool SettingsWritten { get; set; }

        /// <summary>A set changed the game's settings file on disk.</summary>
        public bool SettingsFileWritten { get; set; }

        /// <summary>The window's top-left on the desktop before the first set (a windowed restore puts it back).</summary>
        public bool HasWindowPosition { get; set; }
        public int WindowX { get; set; }
        public int WindowY { get; set; }

        /// <summary>
        /// The engine's remembered window values in the user's registry (<see cref="ScreenRegistryKey"/>, the values whose
        /// names start with <see cref="ScreenRegistryPrefix"/>) before the first set, encoded by <see cref="EncodeRegistry"/>.
        /// </summary>
        public Dictionary<string, string> Registry { get; set; } = new(StringComparer.Ordinal);

        /// <summary>The last test setting a set asked for (what the window should still be while the baseline is pending).</summary>
        public bool HasRequest { get; set; }
        public int RequestedWidth { get; set; }
        public int RequestedHeight { get; set; }
        public int RequestedMode { get; set; }
    }

    /// <summary>
    /// What a Dev load does with a baseline an earlier run left pending (the process died between set and restore): put
    /// the window back only when it is still the test setting, so a window the user has resized since is never changed
    /// by surprise; otherwise forget the baseline. Returns (restore, why).
    /// </summary>
    internal static (bool Restore, string Why) PendingOnLoad(Baseline b, int width, int height, int mode)
    {
        if (!b.HasRequest) return (false, "it records no test setting");
        if (!Matches(width, height, mode, b.RequestedWidth, b.RequestedHeight, b.RequestedMode))
            return (false, "the window is " + width + "x" + height + " " + ModeName(mode) + ", not the test setting "
                + b.RequestedWidth + "x" + b.RequestedHeight + " " + ModeName(b.RequestedMode));
        return (true, "the window is still the test setting " + b.RequestedWidth + "x" + b.RequestedHeight + " " + ModeName(b.RequestedMode));
    }

    /// <summary>The game's per-user engine preferences key (under HKEY_CURRENT_USER).</summary>
    internal const string ScreenRegistryKey = @"Software\Stunlock Studios\VRising";

    /// <summary>The engine's remembered screen values: size, position and full-screen mode of the window.</summary>
    internal const string ScreenRegistryPrefix = "Screenmanager";

    internal static bool IsScreenValue(string? name) =>
        name != null && name.StartsWith(ScreenRegistryPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>A registry value as text: <c>dword:N</c>, <c>qword:N</c>, <c>binary:base64</c> or <c>string:text</c>; null for other kinds.</summary>
    internal static string? EncodeRegistry(string kind, object? value) => kind.ToLowerInvariant() switch
    {
        "dword" when value is int i => "dword:" + i.ToString(System.Globalization.CultureInfo.InvariantCulture),
        "qword" when value is long l => "qword:" + l.ToString(System.Globalization.CultureInfo.InvariantCulture),
        "binary" when value is byte[] b => "binary:" + Convert.ToBase64String(b),
        "string" when value is string s => "string:" + s,
        _ => null,
    };

    /// <summary>The kind and value of an <see cref="EncodeRegistry"/> text, or null when it does not parse.</summary>
    internal static (string Kind, object Value)? DecodeRegistry(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        int colon = text!.IndexOf(':');
        if (colon <= 0) return null;
        string kind = text.Substring(0, colon);
        string payload = text.Substring(colon + 1);
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        try
        {
            return kind switch
            {
                "dword" => (kind, int.Parse(payload, inv)),
                "qword" => (kind, long.Parse(payload, inv)),
                "binary" => (kind, Convert.FromBase64String(payload)),
                "string" => (kind, payload),
                _ => null,
            };
        }
        catch (FormatException) { return null; }
        catch (OverflowException) { return null; }
    }

    internal const string BaselineFileName = "progenitor-display-baseline.json";

    internal static string SerializeBaseline(Baseline b) => JsonSerializer.Serialize(b);

    internal static Baseline? ParseBaseline(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var b = JsonSerializer.Deserialize<Baseline>(json!);
            // The baseline's own mode may be exclusive (the user's choice); it is restored as it was.
            return b != null && b.Width > 0 && b.Height > 0 && b.Mode >= 0 && b.Mode <= 3 ? b : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// Whether the window now is what was asked for: the size (the window may be a pixel off when the OS rounds) and the
    /// mode. A maximized or full-screen window takes the monitor's size, so only the mode is compared there.
    /// </summary>
    internal static bool Matches(int width, int height, int mode, int wantWidth, int wantHeight, int wantMode)
    {
        if (mode != wantMode) return false;
        if (wantMode != (int)Mode.Windowed) return true;
        return Math.Abs(width - wantWidth) <= 1 && Math.Abs(height - wantHeight) <= 1;
    }

    internal static Dictionary<string, object?> Describe(int w, int h, int mode) => new(StringComparer.Ordinal)
    {
        ["width"] = w,
        ["height"] = h,
        ["mode"] = ModeName(mode),
    };

    internal static string ModeName(int mode) => mode switch
    {
        0 => "ExclusiveFullScreen",
        1 => "FullScreenWindow",
        2 => "MaximizedWindow",
        3 => "Windowed",
        _ => mode.ToString(),
    };

    // ---- screenshot ---------------------------------------------------------------------------------

    internal sealed class Shot
    {
        internal string? Name { get; init; }
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
        "screenshot: args.name (a file name of letters, digits, '-', '_' and '.', ending in .png; written under "
        + "BepInEx/progenitor-shots, never over an existing file; default the UTC time), args.superSize (1..4: render the "
        + "frame at that multiple of the window's size, at most 7680x4320 pixels), and optionally a region: "
        + "args.rect {x, y, w, h} in screen pixels (origin bottom-left), or args.row (and args.cell) or args.panel, with "
        + "args.pad pixels around it.";

    /// <summary>The largest capture: 7680x4320 (33.2 megapixels), four times a 3840x2160 frame.</summary>
    internal const long MaxShotPixels = 7680L * 4320L;

    /// <summary>The folder every capture goes in, under the BepInEx root.</summary>
    internal const string ShotFolder = "progenitor-shots";

    internal static Shot ParseShot(JsonElement args)
    {
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("path", out _))
            throw new ArgumentException("screenshot: args.path is not taken; give args.name (a file name) and the file goes under BepInEx/" + ShotFolder + ".");
        string? name = UiInputPlan.Text(args, "name");
        if (name != null && !IsSafeShotName(name))
            throw new ArgumentException("screenshot: args.name is a plain file name (letters, digits, '-', '_', '.', at most 80 characters) ending in .png.");
        int super = UiInputPlan.OptInt(args, "superSize", 1, 4) ?? 1;
        float pad = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("pad", out _) ? UiInputPlan.Num(args, "pad", 0, 2000) : 0f;
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("rect", out var r))
        {
            if (r.ValueKind != JsonValueKind.Object) throw new ArgumentException("screenshot: args.rect is {x, y, w, h}.");
            return new Shot
            {
                Name = name,
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
        return new Shot { Name = name, SuperSize = super, Region = target.IsEmpty ? null : target, Pad = pad };
    }

    /// <summary>A plain .png file name: no folder, no drive, no "..", only letters, digits, '-', '_' and '.'.</summary>
    internal static bool IsSafeShotName(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 80) return false;
        if (!name.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || name.Length <= 4) return false;
        if (name.Contains("..") || name.StartsWith(".", StringComparison.Ordinal)) return false;
        foreach (char c in name)
            if (!(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_' or '.')) return false;
        return true;
    }

    /// <summary>Why a capture of the window at <paramref name="superSize"/> is refused (too many pixels), or null.</summary>
    internal static string? PixelRefusal(int windowW, int windowH, int superSize)
    {
        long px = (long)windowW * superSize * windowH * superSize;
        return px > MaxShotPixels
            ? "a " + windowW * superSize + "x" + windowH * superSize + " capture is " + px + " pixels; the most is " + MaxShotPixels + " (lower superSize)"
            : null;
    }

    /// <summary>
    /// The file to write in <paramref name="folder"/>: <paramref name="name"/> (default the UTC time), with -2, -3, …
    /// before the extension while a file of that name exists. An existing file is never overwritten or deleted.
    /// </summary>
    internal static string UniqueShotPath(string folder, string? name, DateTime utc, Func<string, bool> exists)
    {
        string file = name ?? utc.ToString("yyyyMMdd-HHmmss-fff") + ".png";
        string stem = System.IO.Path.GetFileNameWithoutExtension(file);
        string path = System.IO.Path.Combine(folder, file);
        for (int i = 2; exists(path) && i < 10_000; i++) path = System.IO.Path.Combine(folder, stem + "-" + i + ".png");
        if (exists(path)) throw new InvalidOperationException("screenshot: no free file name for '" + file + "'");
        return path;
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

    /// <summary>The width and height in a PNG's IHDR (big-endian at bytes 16..23), or null when it is not a PNG.</summary>
    internal static (int W, int H)? PngSize(byte[] head)
    {
        if (head == null || head.Length < 24) return null;
        if (head[0] != 0x89 || head[1] != (byte)'P' || head[2] != (byte)'N' || head[3] != (byte)'G') return null;
        int w = (head[16] << 24) | (head[17] << 16) | (head[18] << 8) | head[19];
        int h = (head[20] << 24) | (head[21] << 16) | (head[22] << 8) | head[23];
        return w > 0 && h > 0 ? (w, h) : null;
    }
}
