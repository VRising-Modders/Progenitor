using System;
using System.Globalization;

#if !PROGENITOR_TESTS
using Progenitor.Plugins;
using Progenitor.Ui;
#endif

namespace Progenitor.Sample;

/// <summary>
/// Map-scope keybinds (<c>ctx.Keybinds.RegisterInMap</c> / <c>RegisterHoldInMap</c>) and the
/// <see cref="Progenitor.Ui.WorldMap"/> pointer, as a small client-only map ruler. Both keys work only
/// while the game's world map is open, so they never meet a gameplay key:
/// <list type="bullet">
/// <item><b>Ctrl+C</b> (<see cref="PointKeybindId"/>, a press): log the world X/Z under the cursor.</item>
/// <item><b>Hold Ctrl+R</b> (<see cref="MeasureKeybindId"/>, press / held / release): the press marks the
/// point under the cursor, every held frame follows the cursor while it is on the map, and the release logs
/// the straight-line distance in meters. Closing the map under the held key cancels (the keybind pump
/// runs the release once; <see cref="Progenitor.Ui.WorldMap.IsOpen"/> is false by then).</item>
/// </list>
/// Results are client-side, so they go to the client log as <c>Sample map: …</c> (also on the Ctrl+F9
/// panel). Both keys are registered only through <c>ctx.Keybinds</c>, so they appear in the Keybinds tab
/// labeled "Works only while the map is open" and can be remapped. Nothing is sent to the server.
///
/// <para><see cref="MapRuler"/>, the coordinate checks and every line are game-free and tested
/// (<c>SampleMapTests</c>); the keybind registration and the pointer reads are behind
/// <c>!PROGENITOR_TESTS</c>.</para>
///
/// <para><b>Acceptance procedure — not run.</b> On a test client in a world with the Sample loaded: open
/// the map, point at a spot and press Ctrl+C; the client log shows <c>Sample map: point x=… z=…</c> and
/// the Ctrl+F9 panel lists it. Hold Ctrl+R, move the cursor across the map and release; the log shows
/// <c>Sample map: measured … m from (…) to (…)</c>. Hold Ctrl+R and close the map before releasing; the
/// log shows <c>Sample map: measure canceled (map closed)</c>. With the map closed neither chord does
/// anything. The Keybinds tab lists both under the Sample with the map-only note.</para>
/// </summary>
internal static class SampleMap
{
    internal const string Area = "map";
    internal const string PointKeybindId = "MapPoint";
    internal const string MeasureKeybindId = "MapMeasure";
    internal const string PointDescription = "Log the world coordinates under the cursor on the open map.";
    internal const string MeasureDescription = "Hold over the open map and move the cursor to measure a distance.";

    /// <summary>Largest world coordinate a line accepts; anything past it is a bad read, not a place.</summary>
    internal const float MaxCoordinate = 100000f;

    private static readonly MapRuler Ruler = new();

    /// <summary>A world X/Z the map gave is usable: finite and inside <see cref="MaxCoordinate"/>.</summary>
    internal static bool IsUsable(float x, float z) =>
        !float.IsNaN(x) && !float.IsNaN(z) && !float.IsInfinity(x) && !float.IsInfinity(z)
        && Math.Abs(x) <= MaxCoordinate && Math.Abs(z) <= MaxCoordinate;

    internal static string PointLine(float x, float z) => "point x=" + F(x) + " z=" + F(z);

    internal static string NoPointLine(string why) =>
        "no world point under the cursor (" + (string.IsNullOrEmpty(why) ? "unknown" : why) + ")";

    internal static string MeasureLine(MapMeasurement m) =>
        "measured " + F(m.Distance) + " m from (" + F(m.FromX) + ", " + F(m.FromZ) + ") to ("
        + F(m.ToX) + ", " + F(m.ToZ) + "); dx=" + F(m.Dx) + " dz=" + F(m.Dz);

    internal static string CanceledLine(string why) => "measure canceled (" + why + ")";

    private static string F(float v) => v.ToString("0.0", CultureInfo.InvariantCulture);

    /// <summary>Unload: drop a measure in progress. The keybinds themselves are swept by Core.</summary>
    internal static void Detach() => Ruler.Cancel();

#if !PROGENITOR_TESTS
    internal static void Attach(PluginContext ctx)
    {
        if (ctx == null) throw new ArgumentNullException(nameof(ctx));
        if (!ctx.IsClient) return;
        try
        {
            // Ctrl chords: the game binds no keyboard modifier composite, so neither can equal a game map
            // key exactly; the Keybinds tab still reports any clash with another map or menu key.
            ctx.Keybinds.RegisterInMap(
                PointKeybindId,
                new KeyChord(UiKey.C, KeyModifiers.Ctrl),
                PointDescription,
                LogPoint);
            ctx.Keybinds.RegisterHoldInMap(
                MeasureKeybindId,
                new KeyChord(UiKey.R, KeyModifiers.Ctrl),
                MeasureDescription,
                onPressed: StartMeasure,
                whileHeld: TrackMeasure,
                onReleased: EndMeasure);
            SampleLog.Info(Area, "client bound map keys " + PointKeybindId + " (Ctrl+C) and " + MeasureKeybindId + " (hold Ctrl+R).");
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("Sample map attach threw: " + e);
        }
    }

    /// <summary>The world point under the cursor, or false with the reason.</summary>
    private static bool TryPoint(out float x, out float z, out string why)
    {
        x = z = 0f;
        if (!WorldMap.TryReadPointer(out var p)) { why = p.Why; return false; }
        if (!p.OnMap) { why = p.Why; return false; }
        if (!p.HasWorld || !IsUsable(p.WorldX, p.WorldZ)) { why = "no world position here"; return false; }
        x = p.WorldX;
        z = p.WorldZ;
        why = "";
        return true;
    }

    private static void LogPoint()
    {
        try
        {
            SampleLog.Info(Area, TryPoint(out float x, out float z, out string why) ? PointLine(x, z) : NoPointLine(why));
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("Sample map point threw: " + e);
        }
    }

    private static void StartMeasure()
    {
        try
        {
            if (TryPoint(out float x, out float z, out string why)) Ruler.Begin(x, z);
            else SampleLog.Info(Area, NoPointLine(why));
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("Sample map measure threw: " + e);
        }
    }

    private static void TrackMeasure()
    {
        if (!Ruler.Active) return;
        try
        {
            // Off the map the last on-map point stands, so a drag past the edge still measures.
            if (TryPoint(out float x, out float z, out _)) Ruler.Track(x, z);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("Sample map measure threw: " + e);
            Ruler.Cancel();
        }
    }

    private static void EndMeasure()
    {
        if (!Ruler.Active) return;
        if (!WorldMap.IsOpen)
        {
            Ruler.Cancel();
            SampleLog.Info(Area, CanceledLine("map closed"));
            return;
        }

        var m = Ruler.Finish();
        if (m.HasValue) SampleLog.Info(Area, MeasureLine(m.Value));
    }
#endif
}

/// <summary>One finished measure: two world points on the map's X/Z plane.</summary>
internal readonly struct MapMeasurement
{
    internal MapMeasurement(float fromX, float fromZ, float toX, float toZ)
    {
        FromX = fromX;
        FromZ = fromZ;
        ToX = toX;
        ToZ = toZ;
    }

    internal float FromX { get; }
    internal float FromZ { get; }
    internal float ToX { get; }
    internal float ToZ { get; }
    internal float Dx => ToX - FromX;
    internal float Dz => ToZ - FromZ;
    internal float Distance => (float)Math.Sqrt((double)Dx * Dx + (double)Dz * Dz);
}

/// <summary>
/// The hold-to-measure state, game-free: <see cref="Begin"/> on the press, <see cref="Track"/> on each
/// held frame with a usable point, <see cref="Finish"/> on the release (or <see cref="Cancel"/>).
/// </summary>
internal sealed class MapRuler
{
    private float _fromX, _fromZ, _toX, _toZ;

    internal bool Active { get; private set; }

    internal void Begin(float x, float z)
    {
        if (!SampleMap.IsUsable(x, z)) return;
        _fromX = _toX = x;
        _fromZ = _toZ = z;
        Active = true;
    }

    internal void Track(float x, float z)
    {
        if (!Active || !SampleMap.IsUsable(x, z)) return;
        _toX = x;
        _toZ = z;
    }

    /// <summary>The measure since <see cref="Begin"/>, or null when none was started. Ends the measure.</summary>
    internal MapMeasurement? Finish()
    {
        if (!Active) return null;
        Active = false;
        return new MapMeasurement(_fromX, _fromZ, _toX, _toZ);
    }

    internal void Cancel() => Active = false;
}
