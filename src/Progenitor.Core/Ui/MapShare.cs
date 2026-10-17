#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;
using Progenitor.Chat;
using ProjectM.UI;

namespace Progenitor.Ui;

/// <summary>
/// Share a map location to chat (client, Core, on by default). With the world map open, a click of the
/// <c>ShareMapLocation</c> key (default Shift + left mouse; a map-scope Core keybind in the Keybinds tab) on the map
/// closes the map and opens the chat input with <c>[loc:x,z]</c> for the clicked spot after any draft, caret at the
/// end. It never sends: the player adds text and presses Enter. Rules: <see cref="MapShareLogic"/>.
///
/// <para><b>The game's own clicks.</b> <c>MapMenuMapper.OnMapClicked</c> acts on a left click only with Ctrl and
/// Shift held (the admin teleport) and places the player's marker on a right click, so Shift + left click reaches
/// nothing of the game's and nothing is suppressed. A keybind matches its modifiers exactly, so the game's
/// Ctrl + Shift + left click never also shares.</para>
///
/// <para><b>The point.</b> The press reads the map under the pointer as the game's click does
/// (<see cref="WorldMap.TryReadPointerAt"/>: the map texture's rect, then the inverse of the mapper's world-to-map
/// matrix, <see cref="MapTransformMath"/>). The release shares that point when the pointer stayed within the drag
/// threshold (a drag pans the map). The minimap is never involved: the key is map scope, live only on the full map.</para>
///
/// <para><b>Closing the map.</b> <c>HUDMenuManager.CloseMenu(World)</c>, the call the game's M key makes with the map
/// shown; the chat opens once the menu has closed (<see cref="ChatWindow.InsertIntoInput(string, bool)"/>).
/// <b>Not live-verified.</b></para>
/// </summary>
internal static class MapShare
{
    private sealed class CoreOwner
    {
        public override string ToString() => "Progenitor map share";
    }

    internal static readonly object Owner = new CoreOwner();

    private static readonly MapShareClick Click = new();
    private static readonly List<IDisposable> Registrations = new();
    private static IKeybindHandle? _bind;
    private static MapShareHint? _hint;
    private static bool _installed;
    private static int _frame;
    private static bool _hintWanted;
    private static string _lastShare = "";
    private static string _lastRefusal = "";

    /// <summary>How often (frames) the tip is refreshed while the map is open.</summary>
    private const int HintPeriodFrames = 10;

    internal static void Install()
    {
        if (Plugin.IsServerProcess || _installed) return;
        var file = UiKeybindHost.CoreConfig;
        if (file == null)
        {
            Plugin.Logger?.LogWarning("Map share: Core's ui cfg is not open; the map share key is not registered.");
            return;
        }

        _installed = true;
        try
        {
            _bind = Keybinds.RegisterScoped(Owner, SettingsPanel.PluginId, MapShareLogic.KeybindId, file,
                MapShareLogic.DefaultKey, MapShareLogic.KeybindDescription, Press, null, Release,
                allowWhileCaptured: false, allowWhenChatUnknown: true, KeybindScope.Map);
            Registrations.Add(_bind);
            _hint = new MapShareHint();
            Plugin.Frame += OnFrame;
            Plugin.Logger?.LogInfo("Map share: key '" + MapShareLogic.KeybindId + "' on " + _bind.Chord.DisplayText + " (map scope).");
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("Map share: install threw; the map share is off for this generation: " + e);
            Uninstall();
        }
    }

    internal static void Uninstall()
    {
        if (!_installed) return;
        _installed = false;
        Plugin.Frame -= OnFrame;
        Click.Cancel();
        for (int i = Registrations.Count - 1; i >= 0; i--)
        {
            try { Registrations[i].Dispose(); }
            catch (Exception e) { Plugin.Logger?.LogWarning("Map share: unregister threw: " + e.Message); }
        }

        Registrations.Clear();
        _bind = null;
        try { _hint?.Dispose(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Map share: key tip dispose threw: " + e.Message); }
        _hint = null;
        _hintWanted = false;
    }

    private static void Press()
    {
        if (!WorldMap.TryReadPointer(out var p))
        {
            Click.Cancel();
            Refused(p.Why);
            return;
        }

        if (!Click.Press(p.OnMap, p.HasWorld, p.ScreenX, p.ScreenY, p.WorldX, p.WorldZ))
            Refused(p.OnMap ? "the map has no data under the pointer" : p.Why);
    }

    private static void Release()
    {
        if (!Click.Armed) return;
        bool has = UnityUiRuntime.TrySamplePointerScreen(out float sx, out float sy);
        if (Click.Release(WorldMap.IsOpen, has, sx, sy, out float wx, out float wz)) Share(wx, wz);
    }

    /// <summary>Close the map and put the point's token in the chat input. False when nothing was queued.</summary>
    internal static bool Share(float worldX, float worldZ)
    {
        string token = MapShareLogic.Token(worldX, worldZ);
        var world = Worlds.Client;
        if (world == null || !world.IsCreated) { Refused("no client world"); return false; }
        if (!ChatWindow.InsertIntoInput(token, afterMenuCloses: true)) { Refused("no chat window"); return false; }
        try { HUDMenuManager.CloseMenu(world); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Map share: closing the map threw: " + e.Message); }
        _lastShare = token;
        _lastRefusal = "";
        Plugin.Logger?.LogInfo("Map share: " + token + " goes to the chat input once the map has closed.");
        return true;
    }

    private static void Refused(string why)
    {
        _lastRefusal = why ?? "";
        Plugin.Logger?.LogDiag("Map share: nothing shared (" + _lastRefusal + ").");
    }

    private static void OnFrame()
    {
        var hint = _hint;
        if (hint == null) return;
        try
        {
            bool open = WorldMap.IsOpen;
            var chord = _bind?.Chord ?? KeyChord.None;
            if (!MapShareLogic.ShouldShowHint(_installed, open, chord))
            {
                if (_hintWanted) hint.Hide();
                _hintWanted = false;
                return;
            }

            // On the frame the map opens, then every few frames (the game refreshes the row per tab).
            bool first = !_hintWanted;
            _hintWanted = true;
            if (!first && ++_frame % HintPeriodFrames != 0) return;
            hint.Show(chord);
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("Map share: key tip refresh threw: " + e.Message); }
    }

    /// <summary>The dev bridge's readback (<see cref="MapShareTestBridge"/>).</summary>
    internal static Dictionary<string, object?> Describe() => new(StringComparer.Ordinal)
    {
        ["installed"] = _installed,
        ["key"] = _bind?.Chord.DisplayText ?? "",
        ["mapOpen"] = WorldMap.IsOpen,
        ["hintShown"] = _hint?.Shown ?? false,
        ["hintText"] = _hint?.Text ?? "",
        ["lastShare"] = _lastShare,
        ["lastRefusal"] = _lastRefusal,
        ["pendingInsert"] = ChatWindow.PendingInsert,
    };

    /// <summary>The bridge's share: the same path as a click, at a screen point or (without one) under the pointer.</summary>
    internal static Dictionary<string, object?> ShareFromBridge(bool hasPoint, float x, float y)
    {
        var r = new Dictionary<string, object?>(StringComparer.Ordinal);
        MapPointer p;
        bool read = hasPoint ? WorldMap.TryReadPointerAt(x, y, out p) : WorldMap.TryReadPointer(out p);
        r["onMap"] = read && p.OnMap;
        r["hasWorld"] = read && p.HasWorld;
        r["screen"] = new[] { p.ScreenX, p.ScreenY };
        if (!read || !p.OnMap || !p.HasWorld)
        {
            string why = read ? (p.OnMap ? "the map has no data under the point" : p.Why) : p.Why;
            Refused(why);
            r["ok"] = false;
            r["why"] = why;
            return r;
        }

        r["world"] = new[] { p.WorldX, p.WorldZ };
        r["token"] = MapShareLogic.Token(p.WorldX, p.WorldZ);
        r["ok"] = Share(p.WorldX, p.WorldZ);
        if (!(bool)r["ok"]!) r["why"] = _lastRefusal;
        return r;
    }
}

/// <summary>
/// The dev bridge's <c>map</c> op reads and drives the map share through this by reflection (Core grants Dev no
/// <c>InternalsVisibleTo</c>); it is not a plugin API and no network path reaches it. Main thread.
/// </summary>
internal static class MapShareTestBridge
{
    /// <summary>Installed, the key, the tip, the last share and the pending chat insert.</summary>
    public static Dictionary<string, object?> Status() => MapShare.Describe();

    /// <summary>Share the map point at screen (<paramref name="x"/>, <paramref name="y"/>), or under the pointer when <paramref name="hasPoint"/> is false.</summary>
    public static Dictionary<string, object?> Share(bool hasPoint, float x, float y)
    {
        var r = MapShare.ShareFromBridge(hasPoint, x, y);
        r["status"] = MapShare.Describe();
        return r;
    }
}
#endif
