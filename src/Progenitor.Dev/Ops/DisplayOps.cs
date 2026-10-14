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
/// window-mode rows end in). It is refused while a client world exists unless <c>allowInWorld</c>, never sets exclusive
/// full screen, and is <c>ok</c> only when the window matches. Each set measures whether the game wrote its client
/// settings (<c>ClientSettings.GraphicSettings.Resolution</c> / <c>WindowMode</c>, and the settings file). The baseline is
/// on disk before anything changes; <c>display restore</c>, Dev unload and so every Core swap put back the window, the two
/// fields, and the settings file when a set had written it.</para>
///
/// <para><c>screenshot</c> is <c>ScreenCapture.CaptureScreenshot</c>: the game writes the frame it renders, with its UI,
/// at the end of the frame. It needs no window focus (the game renders in the background; a minimized window renders
/// nothing). Files go only under BepInEx/progenitor-shots, never over an existing file, at most 7680x4320 pixels. <c>superSize</c> renders that frame at a multiple of the window's size (2 on a 1920x1080 window gives
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

    internal static void Shutdown() { }
#else
    /// <summary>Frames <c>set</c> and <c>restore</c> wait for the window to take the change before they compare.</summary>
    private const int SettleFramesMax = 90;

    private static string BaselinePath => Path.Combine(BepInEx.Paths.ConfigPath, DisplayPlan.BaselineFileName);

    private static DisplayPlan.Baseline? LoadBaseline()
    {
        try { return File.Exists(BaselinePath) ? DisplayPlan.ParseBaseline(File.ReadAllText(BaselinePath)) : null; }
        catch { return null; }
    }

    private static void SaveBaseline(DisplayPlan.Baseline b)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(BaselinePath)!);
        File.WriteAllText(BaselinePath, DisplayPlan.SerializeBaseline(b));
    }

    private static void ForgetBaseline()
    {
        try { if (File.Exists(BaselinePath)) File.Delete(BaselinePath); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Dev display: could not remove " + BaselinePath + ": " + e.Message); }
    }

    private static Dictionary<string, object?> DescribeBaseline(DisplayPlan.Baseline? b)
    {
        if (b == null) return new Dictionary<string, object?> { ["pending"] = false };
        var d = DisplayPlan.Describe(b.Width, b.Height, b.Mode);
        d["pending"] = true;
        d["settingsResolution"] = b.SettingsWidth < 0 ? null : b.SettingsWidth + "x" + b.SettingsHeight;
        d["settingsWindowMode"] = b.SettingsWindowMode < 0 ? null : b.SettingsWindowMode;
        d["settingsWritten"] = b.SettingsWritten;
        d["settingsFileWritten"] = b.SettingsFileWritten;
        d["windowPosition"] = b.HasWindowPosition ? b.WindowX + "," + b.WindowY : null;
        d["registryValues"] = b.Registry?.Count ?? 0;
        d["file"] = BaselinePath;
        return d;
    }

    internal static object Run(JsonElement args)
    {
        var r = DisplayPlan.Parse(args);
        if (Progenitor.Plugin.IsServerProcess) throw new InvalidOperationException(ClientOnlyMessage);
        var reply = new Dictionary<string, object?>(StringComparer.Ordinal) { ["action"] = r.Action.ToString().ToLowerInvariant() };
        reply["before"] = Read();
        var baseline = LoadBaseline();
        reply["baseline"] = DescribeBaseline(baseline);
        if (r.Action == DisplayPlan.Action.Status)
        {
            reply["ok"] = true;
            return reply;
        }

        if (r.Action == DisplayPlan.Action.Restore)
        {
            if (baseline == null)
            {
                reply["ok"] = true;
                reply["note"] = "nothing to restore: no display set is pending";
                return reply;
            }

            var keepFront = KeepForeground();
            reply["restored"] = RestoreNow(baseline, "restore");
            keepFront.Check(reply);
            reply["afterCall"] = Read();
            var b0 = baseline;
            return AfterSettle(reply, baseline.Width, baseline.Height, baseline.Mode, keepFront, matched =>
            {
                // The window's place and the engine's remembered values last, once the size has landed.
                var restored = (Dictionary<string, object?>)reply["restored"]!;
                restored["position"] = RestorePosition(b0);
                restored["registry"] = WriteScreenRegistry(b0.Registry);
                if (matched) ForgetBaseline();
            }, "the window did not return to the baseline; the baseline is kept, so restore can be run again");
        }

        bool inWorld;
        try { inWorld = Progenitor.Worlds.Client is { IsCreated: true }; }
        catch { inWorld = false; }
        string? refusal = DisplayPlan.InWorldRefusal(inWorld, r.AllowInWorld);
        if (refusal != null)
        {
            reply["ok"] = false;
            reply["error"] = refusal;
            return reply;
        }

        if (baseline == null)
        {
            baseline = new DisplayPlan.Baseline { Width = Screen.width, Height = Screen.height, Mode = (int)Screen.fullScreenMode };
            ReadSettingsFields(out int sw, out int sh, out int sm);
            baseline.SettingsWidth = sw;
            baseline.SettingsHeight = sh;
            baseline.SettingsWindowMode = sm;
            if (GameWindow.TryRect(out int wx, out int wy, out _, out _))
            {
                baseline.HasWindowPosition = true;
                baseline.WindowX = wx;
                baseline.WindowY = wy;
            }

            baseline.Registry = ReadScreenRegistry();
            // On disk before anything changes: a Dev reload, a Core swap or a crash after this keeps the way back.
            SaveBaseline(baseline);
        }

        ReadSettingsFields(out int beforeW, out int beforeH, out int beforeMode);
        string beforeStamp = SettingsFileStamp();
        var foreground = KeepForeground();
        ProjectM.GraphicsSettingsManager.SetResolution(r.Width, r.Height, (FullScreenMode)(int)r.WindowMode);
        foreground.Check(reply);
        Plugin.Logger?.LogInfo("Dev display set: " + r.Width + "x" + r.Height + " " + r.WindowMode + " (GraphicsSettingsManager.SetResolution)");
        reply["requested"] = DisplayPlan.Describe(r.Width, r.Height, (int)r.WindowMode);
        reply["afterCall"] = Read();
        if (inWorld) reply["inWorld"] = true;
        var kept = baseline;
        return AfterSettle(reply, r.Width, r.Height, (int)r.WindowMode, foreground, matched =>
        {
            // Whether this set wrote the game's settings: its in-memory graphics fields and its settings file.
            ReadSettingsFields(out int afterW, out int afterH, out int afterMode);
            bool wrote = afterW != beforeW || afterH != beforeH || afterMode != beforeMode;
            bool wroteFile = SettingsFileStamp() != beforeStamp;
            reply["settingsWritten"] = wrote;
            reply["settingsFileWritten"] = wroteFile;
            kept.SettingsWritten |= wrote;
            kept.SettingsFileWritten |= wroteFile;
            try { SaveBaseline(kept); } catch (Exception e) { reply["baselineError"] = e.Message; }
            reply["warning"] = "the game's display is changed until display restore (also run on Dev unload and Core swap)";
        }, "the window is not what was asked (the monitor may not hold that size); run display restore");
    }

    /// <summary>Wait until the window matches, at most <see cref="SettleFramesMax"/> frames; <c>ok</c> is whether it did.</summary>
    private static object AfterSettle(Dictionary<string, object?> reply, int w, int h, int mode, Foreground foreground, Action<bool> done, string mismatch)
    {
        var deferred = new DeferredResult(10_000);
        int frames = 0;

        void Step()
        {
            if (deferred.BoundGate != null && deferred.BoundGate.ShouldSkip(deferred.BoundTicket))
            {
                // Abandoned: the reply is gone, but the window still must not keep a foreground it took.
                foreground.Check(null);
                return;
            }

            frames++;
            foreground.Check(reply);
            bool matched = DisplayPlan.Matches(Screen.width, Screen.height, (int)Screen.fullScreenMode, w, h, mode);
            if (!matched && frames < SettleFramesMax)
            {
                MainThread.PostDelayed(0, Step, deferred);
                return;
            }

            reply["after"] = Read();
            reply["framesWaited"] = frames;
            reply["ok"] = matched;
            if (!matched) reply["error"] = mismatch;
            try { done(matched); } catch (Exception e) { reply["doneError"] = e.Message; }
            reply["baseline"] = DescribeBaseline(LoadBaseline());
            deferred.SetResult(reply);
        }

        MainThread.PostDelayed(0, Step, deferred);
        return deferred;
    }

    /// <summary>
    /// Whether the game had the foreground before a resolution call; when it did not and the call raised it, the
    /// foreground goes back to the window that had it (Unity's windowed resize can activate the game's window).
    /// </summary>
    private sealed class Foreground
    {
        internal IntPtr Previous;
        internal bool WasGame;
        internal bool GaveBack;

        internal void Check(Dictionary<string, object?>? reply)
        {
            if (WasGame) return;
            if (GameWindow.GiveForegroundBack(Previous)) GaveBack = true;
            if (reply == null) return;
            reply["focusedBefore"] = WasGame;
            reply["foregroundGivenBack"] = GaveBack;
            reply["focusedNow"] = GameWindow.IsForeground();
        }
    }

    private static Foreground KeepForeground() => new() { Previous = GameWindow.Foreground(), WasGame = GameWindow.IsForeground() };

    /// <summary>A windowed baseline's top-left back on the desktop (no resize, no activation).</summary>
    private static object RestorePosition(DisplayPlan.Baseline b)
    {
        if (!b.HasWindowPosition) return "not recorded";
        if (b.Mode != (int)DisplayPlan.Mode.Windowed) return "not windowed";
        return GameWindow.MoveTo(b.WindowX, b.WindowY) ? (object)new Dictionary<string, object?> { ["x"] = b.WindowX, ["y"] = b.WindowY } : "move failed";
    }

    /// <summary>The engine's remembered screen values in the user's registry, encoded; empty when the key is missing.</summary>
    private static Dictionary<string, string> ReadScreenRegistry()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(DisplayPlan.ScreenRegistryKey, writable: false);
            if (key == null) return map;
            foreach (var name in key.GetValueNames())
            {
                if (!DisplayPlan.IsScreenValue(name)) continue;
                string kind = key.GetValueKind(name) switch
                {
                    Microsoft.Win32.RegistryValueKind.DWord => "dword",
                    Microsoft.Win32.RegistryValueKind.QWord => "qword",
                    Microsoft.Win32.RegistryValueKind.Binary => "binary",
                    Microsoft.Win32.RegistryValueKind.String => "string",
                    _ => "",
                };
                var encoded = kind.Length == 0 ? null : DisplayPlan.EncodeRegistry(kind, key.GetValue(name, null, Microsoft.Win32.RegistryValueOptions.DoNotExpandEnvironmentNames));
                if (encoded != null) map[name] = encoded;
            }
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("Dev display: could not read the screen registry values: " + e.Message); }
        return map;
    }

    /// <summary>Write the recorded screen values back (only those names; nothing else in the key is touched).</summary>
    private static object WriteScreenRegistry(Dictionary<string, string>? values)
    {
        if (values == null || values.Count == 0) return "none recorded";
        int written = 0;
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(DisplayPlan.ScreenRegistryKey, writable: true);
            if (key == null) return "key missing";
            foreach (var pair in values)
            {
                if (!DisplayPlan.IsScreenValue(pair.Key)) continue;
                var decoded = DisplayPlan.DecodeRegistry(pair.Value);
                if (decoded == null) continue;
                var kind = decoded.Value.Kind switch
                {
                    "dword" => Microsoft.Win32.RegistryValueKind.DWord,
                    "qword" => Microsoft.Win32.RegistryValueKind.QWord,
                    "binary" => Microsoft.Win32.RegistryValueKind.Binary,
                    _ => Microsoft.Win32.RegistryValueKind.String,
                };
                key.SetValue(pair.Key, decoded.Value.Value, kind);
                written++;
            }
        }
        catch (Exception e) { return "error: " + e.Message; }
        return written;
    }

    /// <summary>
    /// Back to the baseline now: the window through the game's own call, the two saved graphics fields if they differ, and
    /// the game's settings file saved again when a set had written it, so the file holds the baseline too.
    /// </summary>
    private static Dictionary<string, object?> RestoreNow(DisplayPlan.Baseline b, string why)
    {
        var d = new Dictionary<string, object?>(StringComparer.Ordinal);
        ProjectM.GraphicsSettingsManager.SetResolution(b.Width, b.Height, (FullScreenMode)b.Mode);
        d["window"] = DisplayPlan.Describe(b.Width, b.Height, b.Mode);
        d["settingsFields"] = RestoreSettingsFields(b);
        if (b.SettingsFileWritten || b.SettingsWritten)
        {
            try
            {
                var settings = ProjectM.SettingsManager.ClientSettings;
                if (settings != null)
                {
                    settings.SaveToPath(ProjectM.ClientSettings.LocalPath, true, false);
                    d["settingsSaved"] = true;
                }
            }
            catch (Exception e) { d["settingsSaveError"] = e.Message; }
        }

        Plugin.Logger?.LogInfo("Dev display " + why + ": " + b.Width + "x" + b.Height + " " + DisplayPlan.ModeName(b.Mode)
            + (d.ContainsKey("settingsSaved") ? "; client settings saved" : ""));
        return d;
    }

    /// <summary>
    /// Dev unload, which also runs on a Core swap: put a pending set back. The window change is issued here and cannot be
    /// read back; the baseline file is removed only when the call was made, so a failure leaves it for <c>restore</c>.
    /// </summary>
    internal static void Shutdown()
    {
        try
        {
            var b = LoadBaseline();
            if (b == null) return;
            var front = KeepForeground();
            RestoreNow(b, "restore on unload");
            front.Check(null);
            RestorePosition(b);
            WriteScreenRegistry(b.Registry);
            ForgetBaseline();
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("Dev display: restore on unload failed (the baseline stays for display restore): " + e.Message); }
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
            d["focused"] = Application.isFocused;
        }
        catch (Exception e) { d["screenError"] = e.Message; }

        // The window on the desktop now: its rect and whether it is the foreground window.
        try { d["window"] = GameWindow.Describe(); } catch (Exception e) { d["windowError"] = e.Message; }
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

    /// <summary>The client settings file's size and write time, or "" when there is none (a write changes it).</summary>
    private static string SettingsFileStamp()
    {
        try
        {
            string path = ProjectM.ClientSettings.LocalPath;
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return "";
            var info = new FileInfo(path);
            return info.Length + "@" + info.LastWriteTimeUtc.Ticks;
        }
        catch { return ""; }
    }

    private static string RestoreSettingsFields(DisplayPlan.Baseline b)
    {
        if (b.SettingsWidth < 0) return "unreadable at the first set";
        try
        {
            var g = ProjectM.SettingsManager.ClientSettings?.GraphicSettings;
            if (g == null) return "no client settings";
            bool changed = false;
            var res = g.Resolution;
            if (res.x != b.SettingsWidth || res.y != b.SettingsHeight)
            {
                g.Resolution = new Unity.Mathematics.int2(b.SettingsWidth, b.SettingsHeight);
                changed = true;
            }

            if (g.WindowMode != (byte)b.SettingsWindowMode)
            {
                g.WindowMode = (byte)b.SettingsWindowMode;
                changed = true;
            }

            return changed ? "put back" : "unchanged";
        }
        catch (Exception e) { return "error: " + e.Message; }
    }

    // ---- screenshot ---------------------------------------------------------------------------------

    internal static object Screenshot(JsonElement args)
    {
        var shot = DisplayPlan.ParseShot(args);
        if (Progenitor.Plugin.IsServerProcess) throw new InvalidOperationException(ClientOnlyMessage);
        var reply = new Dictionary<string, object?>(StringComparer.Ordinal);
        int windowW = Screen.width, windowH = Screen.height;
        string? tooBig = DisplayPlan.PixelRefusal(windowW, windowH, shot.SuperSize);
        if (tooBig != null)
        {
            reply["ok"] = false;
            reply["error"] = tooBig;
            return reply;
        }

        // Only under BepInEx/progenitor-shots, only a plain file name, and never over a file that is there.
        string folder = Path.Combine(BepInEx.Paths.BepInExRootPath, DisplayPlan.ShotFolder);
        Directory.CreateDirectory(folder);
        string path = DisplayPlan.UniqueShotPath(folder, shot.Name, DateTime.UtcNow, File.Exists);

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
            string cropPath = DisplayPlan.UniqueShotPath(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + "-region.png", DateTime.UtcNow, File.Exists);
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
