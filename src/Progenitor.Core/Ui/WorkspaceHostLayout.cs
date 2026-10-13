using System;
using System.Collections.Generic;
using System.Globalization;

namespace Progenitor.Ui;

/// <summary>
/// HOSTS1: which window the Mods workspace is drawn in. The game's FullscreenMenu in a world
/// (<c>top-tab</c>), or a Progenitor-owned window: at the title screen (<c>main-menu</c>), and in a
/// world when the shortcut fell back to the standalone window.
/// </summary>
internal enum WorkspaceHost
{
    /// <summary>A Progenitor-owned window in a world (the shortcut's standalone fallback, <c>mods_ui open site=standalone</c>).</summary>
    Standalone = 0,

    /// <summary>Inside the game's FullscreenMenu: the native top bar, frame and Back handling are the game's.</summary>
    InGame = 1,

    /// <summary>The title screen: a Progenitor-owned window dressed to look like the in-game host.</summary>
    Title = 2,
}

/// <summary>
/// HOSTS1: one in-game frame as it was applied (ViewParent grown by the footer chrome, capped at 2:1),
/// in canvas units, with the canvas it was measured on. Plain values only, so it can be written to a
/// file and read back by a later Core generation or a later process.
/// </summary>
internal readonly struct NativeFrameRecord
{
    /// <summary>
    /// The one tolerance for "the same canvas" and "the same frame", in canvas units: lookups, the
    /// unchanged-frame check and the settle check all use it (review M3).
    /// </summary>
    internal const float Tolerance = 1f;

    internal NativeFrameRecord(float canvasWidth, float canvasHeight, float width, float height, float x, float y)
    {
        CanvasWidth = canvasWidth;
        CanvasHeight = canvasHeight;
        Width = width;
        Height = height;
        X = x;
        Y = y;
    }

    internal float CanvasWidth { get; }
    internal float CanvasHeight { get; }
    internal float Width { get; }
    internal float Height { get; }
    internal float X { get; }
    internal float Y { get; }

    internal bool Valid =>
        CanvasWidth > 1f && CanvasHeight > 1f && Width > 1f && Height > 1f
        && Finite(CanvasWidth) && Finite(CanvasHeight) && Finite(Width) && Finite(Height) && Finite(X) && Finite(Y);

    /// <summary>Distance from the canvas top to the frame top (the game's bar is inside it).</summary>
    internal float TopInset => CanvasHeight * .5f - (Y + Height * .5f);

    internal float Aspect => CanvasHeight > 0.01f ? CanvasWidth / CanvasHeight : 0f;

    internal bool SameCanvas(float canvasWidth, float canvasHeight) =>
        Math.Abs(CanvasWidth - canvasWidth) <= Tolerance && Math.Abs(CanvasHeight - canvasHeight) <= Tolerance;

    internal bool SameFrame(in NativeFrameRecord other) =>
        SameCanvas(other.CanvasWidth, other.CanvasHeight)
        && Math.Abs(Width - other.Width) <= Tolerance
        && Math.Abs(Height - other.Height) <= Tolerance
        && Math.Abs(X - other.X) <= Tolerance
        && Math.Abs(Y - other.Y) <= Tolerance;

    private static bool Finite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);
}

/// <summary>
/// HOSTS1: the canonical window, the one both hosts draw. Round 8 (decided 2026-09-30: the Mods window
/// fits the same rectangle every vanilla menu page uses): the vanilla window of Map, Social, Vampire Powers
/// and Spellbook is 1664×916 with its top 128 units below the canvas top (screen y 127..1043 at 1080p,
/// <c>reviews/2026-09-30-window-fit</c>): the native <c>ViewParent</c> (1664×870) plus the
/// <c>ConsoleInputsGroup</c> strip (48) under it, below the band where Spellbook's own tabs sit (y 82..127,
/// the <c>InnerTabGroup</c> row, 50 tall + 6). Rounds 3–7 took the other native layout as canonical: when the
/// hosted page has never laid the inner-tab row out (the Mods page's own route, entries 0×0), ViewParent sits
/// 56 higher, top 72 (<c>at=(0,10)</c>), and our tab strip went inside the window. Now the canonical window is
/// the vanilla one, top <see cref="Top"/> (128, <c>at=(0,-46)</c> on a 1080-unit canvas at every width), and
/// our tab strip sits in the band above it. <see cref="Canonical"/> moves a collapsed measurement down by
/// <see cref="InnerTabRow"/>. <b>Inferred:</b> 4:3 and 16:10 sizes other than 1600×1024 were not probed.
/// </summary>
internal static class NativeFrameModel
{
    internal const float Width = 1664f;
    internal const float Height = 916f;

    /// <summary>The vanilla window's top below the canvas top: 48-unit bar + the 56-unit tab band + 24.</summary>
    internal const float Top = 128f;

    /// <summary>The native inner-tab band above the window (Spellbook's tabs): 50 + 6.</summary>
    internal const float InnerTabRow = 56f;

    /// <summary>The top of a collapsed native layout (the band not laid out): <see cref="Top"/> - <see cref="InnerTabRow"/>.</summary>
    internal const float CollapsedTop = Top - InnerTabRow;

    /// <summary>Least room kept at each side and below the frame on a canvas smaller than the probes.</summary>
    internal const float MinMargin = 8f;

    /// <summary>
    /// How far a canonical record's top may sit from <see cref="Top"/> before the model wins. Under the
    /// 56-unit band, above the few units a real canvas rounding can shift.
    /// </summary>
    internal const float MaxTopDeviation = 24f;

    /// <summary>Smallest record height accepted (well under the 870 ViewParent).</summary>
    internal const float MinHeight = 400f;

    /// <summary>
    /// How close a measured top must be to <see cref="CollapsedTop"/>, and its width to the native 1664, to read
    /// as the collapsed layout of the game's FullscreenMenu. Tight: every live read was exactly 1664 wide and
    /// 128 or 72 down.
    /// </summary>
    internal const float InnerTabRowTolerance = 8f;

    internal static float TopFor(float canvasWidth, float canvasHeight)
    {
        _ = canvasWidth;
        _ = canvasHeight;
        return Top;
    }

    /// <summary>
    /// The measured frame at the vanilla position: a collapsed measurement (top 72) is moved down by
    /// <see cref="InnerTabRow"/>; the size is unchanged.
    /// </summary>
    internal static NativeFrameRecord Canonical(in NativeFrameRecord measured)
    {
        if (!measured.Valid) return measured;
        if (!IsCollapsed(measured)) return measured;
        return new NativeFrameRecord(measured.CanvasWidth, measured.CanvasHeight, measured.Width, measured.Height,
            measured.X, measured.Y - InnerTabRow);
    }

    /// <summary>Whether a measured frame is the collapsed native layout (<see cref="Canonical"/> moves it down).</summary>
    internal static bool IsCollapsed(in NativeFrameRecord measured) =>
        measured.Valid
        && Math.Abs(measured.Width - Width) <= InnerTabRowTolerance
        && Math.Abs(measured.TopInset - CollapsedTop) <= InnerTabRowTolerance;

    internal static NativeFrameRecord Predict(float canvasWidth, float canvasHeight)
    {
        float cw = MenuPanelLayout.Positive(canvasWidth, 1920f);
        float ch = MenuPanelLayout.Positive(canvasHeight, 1080f);
        return Fit(cw, ch, Width, Height, TopFor(cw, ch));
    }

    /// <summary>
    /// Place a frame of the given size with its top <paramref name="top"/> below the canvas top,
    /// centered, shrunk only when the canvas cannot hold it with <see cref="MinMargin"/> to spare.
    /// </summary>
    internal static NativeFrameRecord Fit(float canvasWidth, float canvasHeight, float width, float height, float top)
    {
        float w = Math.Max(1f, Math.Min(width, canvasWidth - MinMargin * 2f));
        float t = Math.Max(0f, Math.Min(top, canvasHeight - MinMargin - 1f));
        float h = Math.Max(1f, Math.Min(height, canvasHeight - t - MinMargin));
        float y = canvasHeight * .5f - t - h * .5f;
        return new NativeFrameRecord(canvasWidth, canvasHeight, w, h, 0f, y);
    }

    /// <summary>
    /// Whether a (canonical) record may stand in for the model on this canvas: valid, a sane height, and
    /// its top within <see cref="MaxTopDeviation"/> of <see cref="Top"/>.
    /// </summary>
    internal static bool Plausible(in NativeFrameRecord record, float canvasWidth, float canvasHeight)
    {
        if (!record.Valid || record.Height < MinHeight || record.Height > canvasHeight) return false;
        return Math.Abs(record.TopInset - TopFor(canvasWidth, canvasHeight)) <= MaxTopDeviation;
    }

    /// <summary>A record bounded to the canvas it is used on (width, top and height through <see cref="Fit"/>).</summary>
    internal static NativeFrameRecord Bound(in NativeFrameRecord record, float canvasWidth, float canvasHeight) =>
        Fit(canvasWidth, canvasHeight, record.Width, record.Height, record.TopInset);
}

/// <summary>
/// HOSTS1: waits until the in-game frame has settled before it may be recorded (review items 2 and M3).
/// An observation counts only while the workspace is shown in the FullscreenMenu on a measured canvas
/// (<c>eligible</c>); the same frame (within <see cref="NativeFrameRecord.Tolerance"/>) must then be seen
/// for <see cref="Frames"/> consecutive frames, and each settled frame is committed once. A tab
/// transition, a resize or the first unsized read changes the frame and restarts the count.
/// </summary>
internal sealed class NativeFrameSettle
{
    internal const int Frames = 15;

    private NativeFrameRecord _candidate;
    private int _seen;
    private NativeFrameRecord _committed;
    private bool _hasCommitted;

    /// <summary>True, with the record, on the frame a settled frame should be recorded.</summary>
    internal bool Observe(in NativeFrameRecord record, bool eligible, out NativeFrameRecord settled)
    {
        settled = default;
        if (!eligible || !record.Valid)
        {
            _seen = 0;
            return false;
        }

        if (_seen > 0 && _candidate.SameFrame(record)) _seen++;
        else
        {
            _candidate = record;
            _seen = 1;
        }

        if (_seen < Frames) return false;
        if (_hasCommitted && _committed.SameFrame(_candidate)) return false;
        _committed = _candidate;
        _hasCommitted = true;
        settled = _candidate;
        return true;
    }

    internal void Reset()
    {
        _seen = 0;
        _hasCommitted = false;
    }
}

/// <summary>
/// HOSTS1: the settled in-game frames this player has seen, one per canvas size (newest first, at most
/// <see cref="Capacity"/>), kept in <c>BepInEx/config/</c><see cref="FileName"/> so the title screen matches
/// the in-game frame after a Core swap or a restart. The file is stamped with the game build; records
/// from another build are ignored (review M2). Writes are debounced (<see cref="DebounceMs"/>), merge the
/// file's other canvases back in (another process may have written them), and replace the file through
/// a temporary file (review L4). Tests leave <see cref="Directory"/> null.
/// </summary>
internal static class NativeFrameMemory
{
    internal const string FileName = "progenitor-workspace-frame.txt";
    internal const string Header = "# Progenitor Mods workspace: settled in-game frame per canvas (canvasW canvasH width height x y)";
    internal const string BuildKey = "build";
    internal const int Capacity = 8;
    internal const long DebounceMs = 1000;

    private static readonly object Gate = new();
    private static readonly List<NativeFrameRecord> Records = new();
    private static bool _loaded;
    private static bool _dirty;
    private static long _dirtySince;

    /// <summary>Where the file lives; null keeps the records in memory only.</summary>
    internal static Func<string?> Directory { get; set; } = DefaultDirectory;

    /// <summary>The running game's build identity; the file's stamp must equal it.</summary>
    internal static Func<string> GameBuild { get; set; } = DefaultGameBuild;

    /// <summary>The clock for the debounce; a seam for tests.</summary>
    internal static Func<long> NowMs { get; set; } = () => Environment.TickCount64;

    private static string? DefaultDirectory()
    {
#if PROGENITOR_TESTS
        return null;
#else
        try { return Progenitor.Networking.ModManifest.ConfigDirectory; }
        catch { return null; }
#endif
    }

    private static string DefaultGameBuild()
    {
#if PROGENITOR_TESTS
        return "tests";
#else
        try { return Progenitor.Content.PrefabEditWireApply.LocalIdentity() ?? ""; }
        catch { return ""; }
#endif
    }

    internal static void ResetForTests()
    {
        lock (Gate)
        {
            Records.Clear();
            _loaded = true;
            _dirty = false;
        }
    }

    /// <summary>Forget what is in memory so the next read loads the file again, as a new process or Core generation does.</summary>
    internal static void ForgetForTests()
    {
        lock (Gate)
        {
            Records.Clear();
            _loaded = false;
            _dirty = false;
        }
    }

    internal static bool Dirty
    {
        get { lock (Gate) return _dirty; }
    }

    /// <summary>Records in memory, newest first (loads the file on first use).</summary>
    internal static IReadOnlyList<NativeFrameRecord> Snapshot()
    {
        lock (Gate)
        {
            EnsureLoaded();
            return Records.ToArray();
        }
    }

    /// <summary>
    /// Remember a settled in-game frame. A frame equal to the one already held for that canvas changes
    /// nothing. The file is written by <see cref="Flush"/> once the debounce has passed.
    /// </summary>
    internal static bool Remember(in NativeFrameRecord record)
    {
        if (!record.Valid) return false;
        lock (Gate)
        {
            EnsureLoaded();
            int at = IndexOf(Records, record.CanvasWidth, record.CanvasHeight);
            if (at >= 0 && Records[at].SameFrame(record)) return false;
            if (at >= 0) Records.RemoveAt(at);
            Records.Insert(0, record);
            while (Records.Count > Capacity) Records.RemoveAt(Records.Count - 1);
            if (!_dirty) _dirtySince = SafeNow();
            _dirty = true;
            return true;
        }
    }

    /// <summary>Write pending records when the debounce has passed, or at once with <paramref name="force"/>.</summary>
    internal static bool Flush(bool force = false)
    {
        string? dir = SafeDirectory();
        List<NativeFrameRecord> mine;
        lock (Gate)
        {
            if (!_dirty) return false;
            if (!force && SafeNow() - _dirtySince < DebounceMs) return false;
            _dirty = false;
            if (dir == null) return false;
            mine = new List<NativeFrameRecord>(Records);
        }

        try
        {
            System.IO.Directory.CreateDirectory(dir);
            string path = System.IO.Path.Combine(dir, FileName);
            // Keep the canvases another process wrote since we loaded (same build only).
            var merged = Merge(mine, ReadFile(path));
            string temp = path + ".tmp";
            System.IO.File.WriteAllText(temp, Format(merged, CurrentBuild()));
            System.IO.File.Move(temp, path, overwrite: true);
            return true;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: workspace frame record write failed: " + e.Message);
            return false;
        }
    }

    /// <summary>The frame recorded for this canvas size (within <see cref="NativeFrameRecord.Tolerance"/>).</summary>
    internal static bool TryGet(float canvasWidth, float canvasHeight, out NativeFrameRecord record)
    {
        lock (Gate)
        {
            EnsureLoaded();
            int at = IndexOf(Records, canvasWidth, canvasHeight);
            record = at >= 0 ? Records[at] : default;
            return at >= 0;
        }
    }

    /// <summary>
    /// The newest record on a canvas of the same height: its insets (top, height, width) carry to a
    /// canvas of another width, because the canvas is laid out by height.
    /// </summary>
    internal static bool TryGetSimilar(float canvasWidth, float canvasHeight, out NativeFrameRecord record)
    {
        lock (Gate)
        {
            EnsureLoaded();
            for (int i = 0; i < Records.Count; i++)
            {
                var r = Records[i];
                if (Math.Abs(r.CanvasHeight - canvasHeight) > NativeFrameRecord.Tolerance) continue;
                record = r;
                return true;
            }
        }

        record = default;
        return false;
    }

    internal static List<NativeFrameRecord> Merge(IReadOnlyList<NativeFrameRecord> mine, IReadOnlyList<NativeFrameRecord> disk)
    {
        var merged = new List<NativeFrameRecord>(mine);
        foreach (var r in disk)
        {
            if (merged.Count >= Capacity) break;
            if (IndexOf(merged, r.CanvasWidth, r.CanvasHeight) < 0) merged.Add(r);
        }

        return merged;
    }

    internal static string Format(IReadOnlyList<NativeFrameRecord> records, string build)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(Header).Append('\n');
        sb.Append(BuildKey).Append('\t').Append(Clean(build)).Append('\n');
        foreach (var r in records)
        {
            sb.Append(F(r.CanvasWidth)).Append('\t').Append(F(r.CanvasHeight)).Append('\t')
                .Append(F(r.Width)).Append('\t').Append(F(r.Height)).Append('\t')
                .Append(F(r.X)).Append('\t').Append(F(r.Y)).Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>
    /// The records of <paramref name="text"/> when its build stamp equals <paramref name="build"/>; none for a
    /// file from another build or with no stamp (a file written before the stamp existed).
    /// </summary>
    internal static List<NativeFrameRecord> Parse(string? text, string build)
    {
        var list = new List<NativeFrameRecord>();
        if (string.IsNullOrEmpty(text)) return list;
        bool stamped = false;
        foreach (var raw in text!.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            var f = line.Split('\t');
            if (f[0] == BuildKey)
            {
                if (f.Length < 2 || !string.Equals(f[1], Clean(build), StringComparison.Ordinal)) return new List<NativeFrameRecord>();
                stamped = true;
                continue;
            }

            if (!stamped || f.Length < 6) continue;
            if (!P(f[0], out float cw) || !P(f[1], out float ch) || !P(f[2], out float w)
                || !P(f[3], out float h) || !P(f[4], out float x) || !P(f[5], out float y))
                continue;
            var r = new NativeFrameRecord(cw, ch, w, h, x, y);
            if (!r.Valid || IndexOf(list, cw, ch) >= 0) continue;
            list.Add(r);
            if (list.Count >= Capacity) break;
        }

        return list;
    }

    private static int IndexOf(IReadOnlyList<NativeFrameRecord> list, float canvasWidth, float canvasHeight)
    {
        for (int i = 0; i < list.Count; i++)
            if (list[i].SameCanvas(canvasWidth, canvasHeight)) return i;
        return -1;
    }

    private static void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        string? dir = SafeDirectory();
        if (dir == null) return;
        Records.Clear();
        Records.AddRange(ReadFile(System.IO.Path.Combine(dir, FileName)));
    }

    private static List<NativeFrameRecord> ReadFile(string path)
    {
        try
        {
            if (!System.IO.File.Exists(path)) return new List<NativeFrameRecord>();
            return Parse(System.IO.File.ReadAllText(path), CurrentBuild());
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: workspace frame record read failed: " + e.Message);
            return new List<NativeFrameRecord>();
        }
    }

    private static string CurrentBuild()
    {
        try { return GameBuild?.Invoke() ?? ""; }
        catch { return ""; }
    }

    private static long SafeNow()
    {
        try { return NowMs(); }
        catch { return 0; }
    }

    private static string? SafeDirectory()
    {
        try { return Directory?.Invoke(); }
        catch { return null; }
    }

    private static string Clean(string? s) => (s ?? "").Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ').Trim();

    private static string F(float v) => v.ToString("R", CultureInfo.InvariantCulture);

    private static bool P(string s, out float v) =>
        float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) && !float.IsNaN(v) && !float.IsInfinity(v);
}

/// <summary>
/// HOSTS1: the one layer both hosts size themselves from (decided 2026-09-30: keep both hosts,
/// make them look the same, the in-game look is the target). Everything here is canvas units in the
/// game's own scaler space (reference 1920×1080, match height), center-anchored like the panes.
/// <list type="bullet">
/// <item><see cref="TitleFrame"/>: the title (and standalone) window's frame is the in-game frame —
/// recorded for this canvas, else carried from a canvas of the same height, else
/// <see cref="NativeFrameModel"/>; a record the model disagrees with is ignored — never a floating card.</item>
/// <item><see cref="ObserveInGame"/>: records the in-game frame once it has settled.</item>
/// <item><see cref="TabLift"/>: the strip's lift toward the bar, the same arithmetic for both hosts.</item>
/// <item><see cref="TopBand"/>: where the game's outer tab bar sits; the title draws a dark band there.</item>
/// <item><see cref="ListViewportTop"/>: the list viewport's top edge below the pinned rows.</item>
/// </list>
/// </summary>
internal static class WorkspaceHostLayout
{
    /// <summary>The game's outer tab bar (<c>FullscreenMenu/Root/OuterLayout/OuterTabGroup</c>): 48 units at the canvas top.</summary>
    internal const float BarHeight = 48f;

    /// <summary>Room kept between the bar and the strip top (the Spellbook reference: 32 units).</summary>
    internal const float BarClearance = 32f;

    /// <summary>Largest lift of the strip above the frame top.</summary>
    internal const float MaxTabLift = 48f;

    /// <summary>
    /// The bar's own color, read from the probe (<c>OuterTabGroup</c> Image, no sprite, rgba 0,0,0,1).
    /// The title's band copies it; it is the game's value, not a palette choice.
    /// </summary>
    internal const float BarR = 0f, BarG = 0f, BarB = 0f, BarA = 1f;

    /// <summary>The caption the title's band carries where the game's entries would be.</summary>
    internal const string BandCaption = "Mods";

    /// <summary>
    /// The font asset the game's outer tab captions use (in-game probe 2026-09-30, <c>mods_ui tabs</c>
    /// owned MODS <c>Name</c>), and its family for a near match. The caption size is inferred from the
    /// 46-unit caption rect; live 2026-09-30 at 3840×1080 it matched the in-game bar in a capture.
    /// </summary>
    internal const string BandCaptionFont = "BORUTTA GROUP - Nocturne Serif Regular DropShadow";

    internal const string BandCaptionFamily = "Nocturne Serif Regular";

    internal const float BandCaptionSize = 24f;

    internal const string SourceRecorded = "recorded";
    internal const string SourceCarried = "carried";
    internal const string SourceModel = "model";

    private static readonly NativeFrameSettle Settle = new();

    /// <summary>
    /// Round 4 (coordinator decision 2026-09-30): pin the in-game frame to the canonical position too, so
    /// both hosts match every session. Bound to <c>[Workspace] CanonicalInGameFrame</c> (default on) by the
    /// backdrop; off draws the native frame where the game put it.
    /// </summary>
    internal static bool PinInGameFrame { get; set; } = true;

    internal const string PinKey = "CanonicalInGameFrame";

    internal const string PinDescription =
        "Draw the in-game Mods workspace at the same place as the title screen's, even when the game's menu "
        + "still reserves another page's tab row above it. Off draws it where the game's menu puts it.";

    /// <summary>
    /// The in-game frame to draw from the applied native frame: the canonical one when the game's layout
    /// reserved the inner-tab row (moved up by <see cref="NativeFrameModel.InnerTabRow"/>), with
    /// <see cref="ModsWorkspaceFrame.CoverBelow"/> set to that shift, so the backdrop still covers the
    /// native frame's old bottom (the footer strip the extent was grown for); else unchanged.
    /// </summary>
    internal static ModsWorkspaceFrame PinCanonical(float canvasWidth, float canvasHeight, in ModsWorkspaceFrame applied, bool pin)
    {
        var measured = new NativeFrameRecord(canvasWidth, canvasHeight, applied.Width, applied.Height, applied.X, applied.Y);
        if (!pin || !NativeFrameModel.IsCollapsed(measured)) return applied;
        // Round 8: down to the vanilla window (the collapsed layout sits 56 higher).
        float y = applied.Y - NativeFrameModel.InnerTabRow;
        return new ModsWorkspaceFrame(applied.Width, applied.Height, applied.X, y, applied.Path,
            TabLift(canvasHeight, y, applied.Height), NativeFrameModel.InnerTabRow);
    }

    internal static WorkspaceHost HostOf(string? site)
    {
        if (string.Equals(site, "top-tab", StringComparison.OrdinalIgnoreCase)) return WorkspaceHost.InGame;
        if (string.Equals(site, "main-menu", StringComparison.OrdinalIgnoreCase)) return WorkspaceHost.Title;
        return WorkspaceHost.Standalone;
    }

    /// <summary>
    /// How far the strip stands above the frame top: as far toward the bar as leaves
    /// <see cref="BarClearance"/> under it, at most <see cref="MaxTabLift"/>, never negative.
    /// </summary>
    internal static float TabLift(float canvasHeight, float frameY, float frameHeight)
    {
        float spare = canvasHeight * .5f - (frameY + frameHeight * .5f) - (BarHeight + BarClearance);
        if (float.IsNaN(spare)) return 0f;
        return Math.Max(0f, Math.Min(MaxTabLift, spare));
    }

    /// <summary>
    /// The in-game frame for this canvas, and where it came from (<see cref="SourceRecorded"/>,
    /// <see cref="SourceCarried"/> or <see cref="SourceModel"/>). An exact record is tried first; a record
    /// is used only when <see cref="NativeFrameModel.Plausible"/>, and always bounded to this canvas.
    /// </summary>
    internal static NativeFrameRecord InGameFrameFor(float canvasWidth, float canvasHeight, out string source)
    {
        float cw = MenuPanelLayout.Positive(canvasWidth, 1920f);
        float ch = MenuPanelLayout.Positive(canvasHeight, 1080f);
        if (NativeFrameMemory.TryGet(cw, ch, out var exact) && NativeFrameModel.Plausible(exact, cw, ch))
        {
            source = SourceRecorded;
            return NativeFrameModel.Bound(exact, cw, ch);
        }

        if (NativeFrameMemory.TryGetSimilar(cw, ch, out var similar) && NativeFrameModel.Plausible(similar, cw, ch))
        {
            source = SourceCarried;
            return NativeFrameModel.Bound(similar, cw, ch);
        }

        source = SourceModel;
        return NativeFrameModel.Predict(cw, ch);
    }

    /// <summary>The Progenitor-owned window's frame: the in-game frame, with the same strip lift.</summary>
    internal static ModsWorkspaceFrame TitleFrame(float canvasWidth, float canvasHeight, out string source)
    {
        var r = InGameFrameFor(canvasWidth, canvasHeight, out source);
        float ch = MenuPanelLayout.Positive(canvasHeight, 1080f);
        return new ModsWorkspaceFrame(r.Width, r.Height, r.X, r.Y, ModsWorkspaceFrame.PathTitleHost,
            TabLift(ch, r.Y, r.Height));
    }

    /// <summary>
    /// One in-game layout of the workspace (the backdrop's per-frame sync). The frame is recorded only when
    /// it is what the player sees with the workspace settled: the workspace shown in the FullscreenMenu
    /// (<paramref name="shownInGame"/>), on a native frame, on a canvas that has its real size
    /// (<paramref name="canvasMeasured"/>; the first read falls back to a 1920×1080 default and must never
    /// be filed under it), unchanged for <see cref="NativeFrameSettle.Frames"/> frames, and plausible for
    /// the model. True when a record was taken this frame.
    /// </summary>
    internal static bool ObserveInGame(
        float canvasWidth, float canvasHeight, in ModsWorkspaceFrame applied, bool shownInGame, bool canvasMeasured)
    {
        bool eligible = shownInGame && canvasMeasured && ModsWorkspaceFrame.IsNativePath(applied.Path);
        // Round 3: settle on the measured frame, record the canonical one (the stale inner-tab row removed).
        var record = new NativeFrameRecord(canvasWidth, canvasHeight, applied.Width, applied.Height, applied.X, applied.Y);
        bool taken = false;
        if (Settle.Observe(record, eligible, out var settled))
        {
            var canonical = NativeFrameModel.Canonical(settled);
            if (NativeFrameModel.Plausible(canonical, canvasWidth, canvasHeight))
                taken = NativeFrameMemory.Remember(canonical);
        }
        NativeFrameMemory.Flush();
        return taken;
    }

    /// <summary>Close, Core unload: write what is pending and forget the settle state.</summary>
    internal static void EndInGame()
    {
        Settle.Reset();
        NativeFrameMemory.Flush(force: true);
    }

    internal static void ResetForTests()
    {
        Settle.Reset();
        NativeFrameMemory.ResetForTests();
        PinInGameFrame = true;
    }

    /// <summary>
    /// The backdrop's rect (artwork and scrim): the full-width pane, in every host and pinned or not, so the
    /// visible window ends where the panes (and the version footer at their bottom) end. Round 4 had it
    /// reach <see cref="ModsWorkspaceFrame.CoverBelow"/> further down on a pinned frame, which left the
    /// footer floating about 70 px above the art's bottom (user: "version at the bottom is out of place").
    /// The vacated band is handled instead by <see cref="HidesVacatedStrip"/>.
    /// </summary>
    internal static MenuPaneGeometry ArtworkRect(in ModsWorkspaceFrame frame) =>
        MenuPanelLayout.ForFrame(frame, false, MenuPaneMode.FullWidth);

    /// <summary>
    /// The native name of the only graphic in a pinned frame's vacated band: <c>ViewParent/ConsoleInputsGroup</c>,
    /// an opaque black strip (Image, rgba 0,0,0,1, no sprite; probe menus p1 and r2) 48 units tall under
    /// ViewParent. In the canonical layout it sits inside the frame, under the backdrop.
    /// </summary>
    internal const string VacatedStripName = "ConsoleInputsGroup";

    /// <summary>The strip's path under the <c>FullscreenMenu(Clone)</c> root (probe menus p1 and r2).</summary>
    internal const string VacatedStripPath = "Root/OuterLayout/InnerLayout/ViewParent/ConsoleInputsGroup";

    /// <summary>
    /// Whether the native strip below a pinned frame is switched off while the workspace is shown, so the
    /// band under the window shows what is outside the window everywhere else, as in the canonical layout
    /// (restored when the workspace closes).
    /// </summary>
    internal static bool HidesVacatedStrip(in ModsWorkspaceFrame frame)
    {
        // Round 8: the window is the vanilla rectangle, which contains the native strip in both layouts (at
        // its bottom when laid out, 56 above it when collapsed), under the backdrop; nothing is left below.
        _ = frame;
        return false;
    }

    /// <summary>The band where the game's outer tab bar sits: the full canvas width, <see cref="BarHeight"/> at the top.</summary>
    internal static MenuPaneGeometry TopBand(float canvasWidth, float canvasHeight)
    {
        float cw = MenuPanelLayout.Positive(canvasWidth, 1920f);
        float ch = MenuPanelLayout.Positive(canvasHeight, 1080f);
        return new MenuPaneGeometry(cw, BarHeight, 0f, ch * .5f - BarHeight * .5f);
    }

    /// <summary>
    /// Whether a host draws its own band: the title only. In game the game's bar is there; the standalone
    /// window in a world would draw the band over the HUD (review M1).
    /// </summary>
    internal static bool DrawsTopBand(WorkspaceHost host) => host == WorkspaceHost.Title;

    /// <summary>
    /// Round 8: the tab strip's left inset from the window's left edge in the band above it (Spellbook's first
    /// inner tab starts 24 right of the window: the L-trigger glyph, 28 wide, sits in front of it).
    /// </summary>
    internal const float StripLeftInset = 24f;

    /// <summary>
    /// A center-anchored canvas rect (units) as a top-origin screen rect (pixels), for the readback. The canvas
    /// is laid out by height, so the scale is screen height over canvas height.
    /// </summary>
    internal static (float X, float Y, float W, float H) ToScreen(
        in MenuPaneGeometry rect, float canvasWidth, float canvasHeight, float screenWidth, float screenHeight)
    {
        float cw = MenuPanelLayout.Positive(canvasWidth, 1920f);
        float ch = MenuPanelLayout.Positive(canvasHeight, 1080f);
        float scale = screenHeight > 0f ? screenHeight / ch : 1f;
        float left = (rect.X - rect.Width * .5f + cw * .5f) * scale;
        float top = (ch * .5f - (rect.Y + rect.Height * .5f)) * scale;
        _ = screenWidth;
        return (left, top, rect.Width * scale, rect.Height * scale);
    }

    /// <summary>The window the player sees (the backdrop, the panes and the footer): the frame itself.</summary>
    internal static MenuPaneGeometry WindowRect(in ModsWorkspaceFrame frame) => ArtworkRect(frame);

    /// <summary>The tab band above the window: the strip's rect.</summary>
    internal static MenuPaneGeometry TabBandRect(in ModsWorkspaceFrame frame) => MenuPanelLayout.ForStrip(frame);

    /// <summary>
    /// The list viewport's top edge, from the pane top: under the title band, the pinned top chrome
    /// (<paramref name="topChrome"/>: <see cref="MenuPanelLayout.TopChromeHeight"/> of the rows' measured
    /// heights, plus a pinned header band) and every active pinned top row as it was actually placed
    /// (<paramref name="placedPinned"/>, read after this pass's placement), so a row taller than it
    /// measured (a heading's extra top, a font that arrived late) still cannot sit over the list. The
    /// same rule for both hosts.
    /// </summary>
    internal static float ListViewportTop(
        float titleBand, float topChrome, IReadOnlyList<(float Top, float Height)>? placedPinned) =>
        MenuPanelLayout.ViewportTopInset(titleBand, topChrome, placedPinned);

    /// <summary>How far right of the last tab's right edge the trim's ornament center must stay, at least.</summary>
    internal const float TrimNotchMargin = 16f;

    /// <summary>
    /// Half the width kept clear around the sprite's own center on the left piece, so the left copy never
    /// shows its ornament. <b>Inferred</b> from the capture (the ornament is well under 128 units wide).
    /// </summary>
    internal const float TrimNotchClearance = 64f;

    /// <summary>The last trim layout drawn (<c>mods_ui tabs.trimNotch</c>).</summary>
    internal static TrimLayout LastTrim { get; set; }

    /// <summary>
    /// Where the <c>Divider_Big_04</c> trim's ornament goes (user report 2026-09-30: centered on the frame
    /// it sat under the Keybinds tab, because the tabs are left-aligned): centered in the free stretch of
    /// the trim line from the last tab's right edge to the frame's right edge. The sprite keeps its size;
    /// it is drawn as two masked copies of the full-width trim: the right one centered on the ornament's
    /// new place, clipped to [seam, frame right]; the left one where it always was, clipped to
    /// [frame left, seam]. The seam is placed under a tab (a tab's center), which hides it, and left of
    /// the left copy's own ornament. Both hosts (in game the game's divider is disabled and this copy drawn).
    /// </summary>
    internal static TrimLayout Trim(in ModsWorkspaceFrame frame, int tabCount) =>
        Trim(frame, tabCount, OrnamentCentered);

    /// <summary>
    /// False: the ornament sits centered in the dead space of the silver line, from the last tab's right edge to the
    /// line's right end (round 13's split layout). The user, 2026-10-06, after a build had centered it on the whole
    /// line: "silver flair should be centered in the dead space to the right." True would center it on the line itself.
    /// </summary>
    internal const bool OrnamentCentered = false;

    /// <summary>The line's own center (canvas units, frame-center origin): where the ornament goes.</summary>
    internal static float TrimLineCenter(in ModsWorkspaceFrame frame) =>
        MenuPanelLayout.ForFrame(frame, false, MenuPaneMode.FullWidth).X;

    /// <summary><see cref="Trim(in ModsWorkspaceFrame, int)"/> with round 13's split layout selectable (<paramref name="centered"/> false).</summary>
    internal static TrimLayout Trim(in ModsWorkspaceFrame frame, int tabCount, bool centered)
    {
        // Round 13 (user review; round 8 had centered it back under the Keybinds tab, a regression): the
        // ornament sits on the window's top edge centered in the free stretch right of the last tab, as the
        // user asked in round 2, in both hosts. The line is two masked copies of the full-width sprite; the
        // seam is placed under a tab's center, left of the left copy's own ornament.
        var pane = MenuPanelLayout.ForFrame(frame, false, MenuPaneMode.FullWidth);
        float width = pane.Width;
        float left = pane.X - width * .5f, right = pane.X + width * .5f, center = pane.X;
        if (tabCount < 1) return TrimLayout.Whole(left, right, center, float.NaN);

        var strip = MenuPanelLayout.ForStrip(frame);
        var last = MenuPanelLayout.StripButton(strip, tabCount - 1, tabCount);
        float lastRight = last.X + last.Width * .5f;
        if (centered) return TrimLayout.Whole(left, right, center, lastRight);
        float notch = (lastRight + right) * .5f;
        if (notch <= center) return TrimLayout.Whole(left, right, center, lastRight);

        float rightStart = notch - width * .5f;
        float seam = float.NaN;
        for (int i = 0; i < tabCount; i++)
        {
            float tabCenter = MenuPanelLayout.StripButton(strip, i, tabCount).X;
            if (tabCenter > rightStart && tabCenter <= center - TrimNotchClearance)
            {
                seam = tabCenter;
                break;
            }
        }

        if (float.IsNaN(seam)) seam = Math.Max(left, Math.Min(rightStart, center - TrimNotchClearance));
        if (seam < rightStart) seam = rightStart;
        return new TrimLayout(notch, seam, left, seam, center, seam, right, notch, lastRight, true);
    }

    // ---- round 13: tab geometry after Spellbook's inner tabs -----------------------------------------

    /// <summary>A tab's rect height in the band (the donor frame is drawn 8 shorter: y 88..128, as Spellbook's).</summary>
    internal const float TabHeight = 48f;

    /// <summary>Spellbook's tab rect width (176; its frame is drawn 168 wide).</summary>
    internal const float TabMinWidth = 176f;

    /// <summary>Between tab rects (Spellbook's entries sit 178 apart: rect gap 2, drawn gap 10).</summary>
    internal const float TabGap = 2f;

    /// <summary>Room each side of a caption inside the donor frame. <b>Inferred</b> from the capture.</summary>
    internal const float TabCaptionPad = 24f;

    /// <summary>
    /// Average glyph width of a tab caption at the donor's caption size, used to size a tab to its caption.
    /// <b>Inferred</b> (the small-caps serif at the strip's size); check the tab rects in the readback.
    /// </summary>
    internal const float TabGlyphWidth = 11f;

    /// <summary>A tab's width for its caption: the caption plus padding, never under <see cref="TabMinWidth"/>.</summary>
    internal static float TabWidthFor(string? caption, float scale) =>
        Math.Max(TabMinWidth, (caption?.Length ?? 0) * TabGlyphWidth + TabCaptionPad * 2f) * Math.Max(0.01f, scale);

    /// <summary>Every tab's width, shrunk together to fit <paramref name="available"/> when they would not.</summary>
    internal static float[] TabWidths(IReadOnlyList<string> captions, float available, float scale)
    {
        int n = Math.Max(1, captions?.Count ?? 0);
        var widths = new float[n];
        float sum = 0f;
        for (int i = 0; i < n; i++)
        {
            widths[i] = TabWidthFor(captions != null && i < captions.Count ? captions[i] : "", scale);
            sum += widths[i];
        }

        float gaps = TabGap * Math.Max(0.01f, scale) * (n - 1);
        if (available > 0f && sum + gaps > available && sum > 0f)
        {
            float factor = Math.Max(0.01f, (available - gaps) / sum);
            for (int i = 0; i < n; i++) widths[i] *= factor;
        }

        return widths;
    }

    /// <summary>
    /// Whether the viewport must be fitted again after the rows are placed: any active pinned top row
    /// (its placed rect is what the viewport must clear, and it is only known after placement).
    /// </summary>
    internal static bool RefitAfterPlacement(int activeTopPins) => activeTopPins > 0;
}

/// <summary>
/// HOSTS1: the trim line drawn as two masked copies (<see cref="WorkspaceHostLayout.Trim"/>). X values are
/// frame-center canvas units. <see cref="Split"/> false draws one whole copy centered on the frame.
/// </summary>
internal readonly record struct TrimLayout(
    float NotchX,
    float SeamX,
    float LeftFrom,
    float LeftTo,
    float LeftTrimX,
    float RightFrom,
    float RightTo,
    float RightTrimX,
    float LastTabRight,
    bool Split)
{
    internal static TrimLayout Whole(float left, float right, float center, float lastTabRight) =>
        new(center, right, left, right, center, right, right, center, lastTabRight, false);
}

/// <summary>
/// HOSTS1 round 6: the cheap key the kept Mods list is reused on. Change counters, not a recomputed list:
/// round 5 hashed the whole decided list to decide, which cost as much as building it (about 220 ms live).
/// Any counter moving, or the search, filter, group toggles or row width changing, means a rebuild.
/// Game-free.
/// </summary>
internal static class ModsListKey
{
    internal static string Compose(
        int inventoryGeneration, int listGeneration, int problemsVersion, string? ledgerStamp,
        string? search, int filter, bool serverExpanded, bool requiredExpanded, float rowWidth)
    {
        var sb = new System.Text.StringBuilder(128);
        sb.Append("inv=").Append(inventoryGeneration)
            .Append(";list=").Append(listGeneration)
            .Append(";problems=").Append(problemsVersion)
            .Append(";ledger=").Append(ledgerStamp ?? "")
            .Append(";search=").Append(search ?? "")
            .Append(";filter=").Append(filter)
            .Append(";server=").Append(serverExpanded ? '1' : '0')
            .Append(";required=").Append(requiredExpanded ? '1' : '0')
            .Append(";width=").Append(((int)Math.Round(rowWidth)).ToString(CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    /// <summary>
    /// Round 7: which parts of two keys differ, as <c>inv 12→13, width 320→300</c>; <c>same</c> when equal.
    /// The log line for every list build, so a rebuild names what moved.
    /// </summary>
    internal static string Diff(string? before, string? after)
    {
        var a = Parts(before);
        var b = Parts(after);
        var changes = new List<string>();
        foreach (var pair in b)
        {
            a.TryGetValue(pair.Key, out var old);
            if (!string.Equals(old, pair.Value, StringComparison.Ordinal))
                changes.Add(pair.Key + " " + (old ?? "none") + "→" + pair.Value);
        }

        foreach (var pair in a)
            if (!b.ContainsKey(pair.Key)) changes.Add(pair.Key + " " + pair.Value + "→none");
        return changes.Count == 0 ? "same" : string.Join(", ", changes);
    }

    private static Dictionary<string, string> Parts(string? key)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(key)) return d;
        foreach (var part in key!.Split(';'))
        {
            int eq = part.IndexOf('=');
            if (eq <= 0) continue;
            d[part.Substring(0, eq)] = part.Substring(eq + 1);
        }

        return d;
    }
}
