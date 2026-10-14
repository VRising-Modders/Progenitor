using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

#if !PROGENITOR_TESTS
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
#endif

namespace Progenitor.Dev.Ops;

/// <summary>
/// BRIDGEUI1: <c>display</c> and <c>screenshot</c>. Client only, main thread.
///
/// <para><c>display set</c> changes the resolution and window mode through the game's own graphics path,
/// <c>ProjectM.GraphicsSettingsManager.SetResolution(width, height, mode)</c> (what the Options menu's resolution and
/// window-mode rows end in), and reports the game's client settings (<c>ClientSettings.GraphicSettings.Resolution</c> /
/// <c>WindowMode</c>) before and after. Those are the user's saved settings: if the game wrote them, its next settings
/// save keeps the test resolution. <c>display restore</c> puts back the resolution, the mode and those two fields as
/// they were before the first <c>set</c> of this Dev generation. Always restore.</para>
///
/// <para><c>screenshot</c> is <c>ScreenCapture.CaptureScreenshot</c>: the game writes the frame it renders, with its UI,
/// at the end of the frame. It needs no window focus (the game renders in the background; a minimized window renders
/// nothing). <c>superSize</c> renders that frame at a multiple of the window's size (2 on a 1920x1080 window gives
/// 3840x2160 pixels; the UI's layout is the 1920x1080 one, which is the same in canvas units as 3840x2160 because the
/// kit scales with the screen at a fixed aspect ratio). A region crops the file around a row, a panel or a rect.</para>
/// </summary>
internal static class DisplayOps
{
    internal const string ClientOnlyMessage = "display and screenshot run on the client only.";

#if PROGENITOR_TESTS
    internal static object Run(JsonElement args)
    {
        DisplayPlan.Parse(args);
        throw new InvalidOperationException(ClientOnlyMessage);
    }

    internal static object Screenshot(JsonElement args)
    {
        DisplayPlan.ParseShot(args);
        throw new InvalidOperationException(ClientOnlyMessage);
    }
#else
    private static Dictionary<string, object?>? _baseline;
    private static int _baseW, _baseH, _baseMode;
    private static int _baseSettingsW = -1, _baseSettingsH = -1, _baseSettingsMode = -1;

    internal static object Run(JsonElement args)
    {
        var r = DisplayPlan.Parse(args);
        if (Progenitor.Plugin.IsServerProcess) throw new InvalidOperationException(ClientOnlyMessage);
        var reply = new Dictionary<string, object?>(StringComparer.Ordinal) { ["action"] = r.Action.ToString().ToLowerInvariant() };
        reply["before"] = Read();
        reply["baseline"] = _baseline;
        if (r.Action == DisplayPlan.Action.Status)
        {
            reply["ok"] = true;
            return reply;
        }

        if (r.Action == DisplayPlan.Action.Restore)
        {
            if (_baseline == null)
            {
                reply["ok"] = true;
                reply["note"] = "nothing to restore: no display set ran in this Dev generation";
                return reply;
            }

            ProjectM.GraphicsSettingsManager.SetResolution(_baseW, _baseH, (FullScreenMode)_baseMode);
            RestoreSettingsFields();
            Plugin.Logger?.LogInfo("Dev display restore: " + _baseW + "x" + _baseH + " " + (FullScreenMode)_baseMode);
            return AfterFrames(reply, () => { _baseline = null; });
        }

        if (_baseline == null)
        {
            _baseW = Screen.width;
            _baseH = Screen.height;
            _baseMode = (int)Screen.fullScreenMode;
            ReadSettingsFields(out _baseSettingsW, out _baseSettingsH, out _baseSettingsMode);
            _baseline = DisplayPlan.Baseline(_baseW, _baseH, _baseMode);
            _baseline["settingsResolution"] = _baseSettingsW < 0 ? null : _baseSettingsW + "x" + _baseSettingsH;
            _baseline["settingsWindowMode"] = _baseSettingsMode < 0 ? null : _baseSettingsMode;
            reply["baseline"] = _baseline;
        }

        ProjectM.GraphicsSettingsManager.SetResolution(r.Width, r.Height, (FullScreenMode)(int)r.WindowMode);
        Plugin.Logger?.LogInfo("Dev display set: " + r.Width + "x" + r.Height + " " + r.WindowMode + " (GraphicsSettingsManager.SetResolution)");
        reply["requested"] = DisplayPlan.Baseline(r.Width, r.Height, (int)r.WindowMode);
        reply["warning"] = "this can change the user's saved graphics settings; run display restore when done";
        return AfterFrames(reply, null);
    }

    private static object AfterFrames(Dictionary<string, object?> reply, Action? done)
    {
        var deferred = new DeferredResult(8_000);
        int frames = 0;

        void Step()
        {
            if (deferred.BoundGate != null && deferred.BoundGate.ShouldSkip(deferred.BoundTicket)) return;
            if (++frames < 10)
            {
                MainThread.PostDelayed(0, Step, deferred);
                return;
            }

            reply["after"] = Read();
            reply["ok"] = true;
            try { done?.Invoke(); } catch { /* */ }
            deferred.SetResult(reply);
        }

        MainThread.PostDelayed(0, Step, deferred);
        return deferred;
    }

    private static Dictionary<string, object?> Read()
    {
        var d = new Dictionary<string, object?>(StringComparer.Ordinal);
        try
        {
            d["width"] = Screen.width;
            d["height"] = Screen.height;
            d["mode"] = Screen.fullScreenMode.ToString();
            var cur = Screen.currentResolution;
            d["monitor"] = cur.width + "x" + cur.height;
        }
        catch (Exception e) { d["screenError"] = e.Message; }

        ReadSettingsFields(out int w, out int h, out int mode);
        d["settingsResolution"] = w < 0 ? null : w + "x" + h;
        d["settingsWindowMode"] = mode < 0 ? null : mode;
        return d;
    }

    private static void ReadSettingsFields(out int w, out int h, out int mode)
    {
        w = h = mode = -1;
        try
        {
            var g = ProjectM.SettingsManager.ClientSettings?.GraphicSettings;
            if (g == null) return;
            var res = g.Resolution;
            w = res.x;
            h = res.y;
            mode = g.WindowMode;
        }
        catch { w = h = mode = -1; }
    }

    /// <summary>Put the two client-settings fields back to their values before the first set, when the game changed them.</summary>
    private static void RestoreSettingsFields()
    {
        if (_baseSettingsW < 0) return;
        try
        {
            var g = ProjectM.SettingsManager.ClientSettings?.GraphicSettings;
            if (g == null) return;
            var res = g.Resolution;
            if (res.x != _baseSettingsW || res.y != _baseSettingsH) g.Resolution = new Unity.Mathematics.int2(_baseSettingsW, _baseSettingsH);
            if (g.WindowMode != (byte)_baseSettingsMode) g.WindowMode = (byte)_baseSettingsMode;
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("Dev display restore: settings fields: " + e.Message); }
    }

    // ---- screenshot ---------------------------------------------------------------------------------

    internal static object Screenshot(JsonElement args)
    {
        var shot = DisplayPlan.ParseShot(args);
        if (Progenitor.Plugin.IsServerProcess) throw new InvalidOperationException(ClientOnlyMessage);
        var reply = new Dictionary<string, object?>(StringComparer.Ordinal);
        string path = shot.Path ?? DisplayPlan.DefaultShotPath(BepInEx.Paths.BepInExRootPath, DateTime.UtcNow);
        path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path)) File.Delete(path);

        // The region is read now, in window pixels, while the UI is as the caller left it.
        Dictionary<string, object?>? region = null;
        float rx = shot.RX, ry = shot.RY, rw = shot.RW, rh = shot.RH;
        bool hasRegion = shot.HasRect;
        if (shot.Region != null)
        {
            var t = shot.Region;
            region = UiInputOps.Dict(UiInputOps.Bridge("Resolve", t.Row, t.Cell, t.Panel, 0f, 0f, false, 0.5f, 0.5f, 0f, 0f));
            if (region["ok"] is not true || region["rect"] is not Dictionary<string, object?> rect)
            {
                reply["ok"] = false;
                reply["error"] = region["error"] ?? "the region has no rect";
                return reply;
            }

            rx = Convert.ToSingle(rect["x"]);
            ry = Convert.ToSingle(rect["y"]);
            rw = Convert.ToSingle(rect["w"]);
            rh = Convert.ToSingle(rect["h"]);
            hasRegion = true;
        }

        int windowW = Screen.width, windowH = Screen.height;
        reply["window"] = windowW + "x" + windowH;
        reply["focused"] = Application.isFocused;
        reply["superSize"] = shot.SuperSize;
        ScreenCapture.CaptureScreenshot(path, shot.SuperSize);
        Plugin.Logger?.LogInfo("Dev screenshot: " + path + " (superSize " + shot.SuperSize + ")");

        var deferred = new DeferredResult(15_000);
        int frames = 0;
        long lastLength = -1;

        void Step()
        {
            if (deferred.BoundGate != null && deferred.BoundGate.ShouldSkip(deferred.BoundTicket)) return;
            frames++;
            long length = File.Exists(path) ? new FileInfo(path).Length : -1;
            bool settled = length > 0 && length == lastLength;
            lastLength = length;
            if (!settled && frames < 300)
            {
                MainThread.PostDelayed(0, Step, deferred);
                return;
            }

            reply["framesWaited"] = frames;
            if (!settled)
            {
                reply["ok"] = false;
                reply["error"] = "the game wrote no screenshot (is the window minimized?)";
                deferred.SetResult(reply);
                return;
            }

            try
            {
                var bytes = File.ReadAllBytes(path);
                var size = DisplayPlan.PngSize(bytes);
                reply["path"] = path;
                reply["bytes"] = bytes.Length;
                reply["width"] = size?.W;
                reply["height"] = size?.H;
                if (hasRegion && size != null)
                    reply["region"] = CropFile(path, bytes, size.Value.W, size.Value.H, rx, ry, rw, rh, shot.Pad, shot.SuperSize, region);
                reply["ok"] = true;
            }
            catch (Exception e)
            {
                reply["ok"] = false;
                reply["error"] = e.Message;
            }

            deferred.SetResult(reply);
        }

        MainThread.PostDelayed(0, Step, deferred);
        return deferred;
    }

    private static Dictionary<string, object?> CropFile(string path, byte[] png, int imageW, int imageH,
        float x, float y, float w, float h, float pad, int scale, Dictionary<string, object?>? target)
    {
        var d = new Dictionary<string, object?>(StringComparer.Ordinal) { ["target"] = target?["source"] };
        var crop = DisplayPlan.Crop(x, y, w, h, pad, scale, imageW, imageH);
        if (crop == null)
        {
            d["error"] = "the region is outside the image";
            return d;
        }

        Texture2D? full = null, part = null;
        try
        {
            full = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            var native = new Il2CppStructArray<byte>(png.Length);
            System.Runtime.InteropServices.Marshal.Copy(png, 0, native.Pointer + IntPtr.Size * 4, png.Length);
            if (!ImageConversion.LoadImage(full, native)) throw new InvalidOperationException("the capture did not decode");
            var raw = full.GetRawTextureData();
            var rgba = new byte[raw.Length];
            System.Runtime.InteropServices.Marshal.Copy(raw.Pointer + IntPtr.Size * 4, rgba, 0, raw.Length);
            var c = crop.Value;
            // A PNG without alpha loads as RGB24 whatever the texture was made as.
            int bpp = rgba.Length / Math.Max(1, full.width * full.height);
            var format = full.format;
            if (bpp != 3 && bpp != 4) throw new InvalidOperationException("the capture loaded as " + format + " (" + bpp + " bytes a pixel)");
            var cut = DisplayPlan.CropPixels(rgba, full.width, full.height, bpp, c.X, c.Y, c.W, c.H);
            part = new Texture2D(c.W, c.H, format, false);
            var cutNative = new Il2CppStructArray<byte>(cut.Length);
            System.Runtime.InteropServices.Marshal.Copy(cut, 0, cutNative.Pointer + IntPtr.Size * 4, cut.Length);
            part.LoadRawTextureData(cutNative);
            part.Apply(false);
            string cropPath = Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + "-region.png");
            File.WriteAllBytes(cropPath, ImageConversion.EncodeToPNG(part));
            d["path"] = cropPath;
            d["x"] = c.X;
            d["y"] = c.Y;
            d["width"] = c.W;
            d["height"] = c.H;
        }
        catch (Exception e) { d["error"] = e.Message; }
        finally
        {
            if (full != null) UnityEngine.Object.Destroy(full);
            if (part != null) UnityEngine.Object.Destroy(part);
        }

        return d;
    }
#endif
}
