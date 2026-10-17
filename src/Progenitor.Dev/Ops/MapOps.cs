using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;
using Progenitor.Ui;

#if !PROGENITOR_TESTS
using ProjectM.UI;
#endif

namespace Progenitor.Dev.Ops;

/// <summary>
/// <c>map</c>: open, close or read the game's world map (the full-screen menu's Map tab) from the
/// bridge, focus-free, through the calls the game's own M key ends in. No OS input, no key state.
///
/// <para><b>Route</b> (investigated 2026-10-06). The M key is
/// <c>ButtonInputAction.MapMenu</c> (22). Outside the menu, the native <c>MenuInputSystem.HandleInput</c>
/// answers it with <c>GoToMenu&lt;FullscreenMenu.InitData&gt;(world, FullscreenMenu,
/// {MenuState = Map})</c>, and the menu's <c>FullscreenMenuMapper.OnStartRunning</c> ends
/// in <c>SetState(gotoState)</c>. Inside the menu, <c>FullscreenMenuMapper.HandleInput</c>
/// answers the key's release with <c>SetState(Map)</c> on another tab, and with
/// <c>HUDMenuManager.CloseMenu(World)</c> when the map is already shown. This op makes the
/// same final calls without the generic: <c>HUDMenuManager.GoToMenu(World, FullscreenMenu)</c>
/// (the non-generic overload the Mods route already calls), then <c>FullscreenMenuMapper.SetState(Map)</c>
/// (called typed by Core's native tab), and <c>CloseMenu(World)</c> to close. The generic
/// overload is not called: its interop wrapper takes an <c>Il2CppSystem.Nullable&lt;FullscreenMenu.InitData&gt;</c>,
/// a new generic instantiation over a game value type.</para>
///
/// <para><b>Readback.</b> <c>open</c> is Core's own map detection (<c>WorldMap.IsOpen</c>, the probe
/// map-scope keybinds use), read on later frames until it has held for <see cref="MapPlan.StableFrames"/>
/// frames. Client only, main thread.</para>
/// </summary>
internal static class MapOps
{
    internal const string ClientOnlyMessage = "map runs on the client only: the server has no world map.";

#if PROGENITOR_TESTS
    internal static object Run(JsonElement args)
    {
        MapPlan.Parse(args);
        throw new InvalidOperationException(ClientOnlyMessage);
    }
#else
    internal static object Run(JsonElement args)
    {
        var action = MapPlan.Parse(args);
        var point = action == MapPlan.Action.Share ? MapPlan.ParsePoint(args) : (false, 0f, 0f);
        var spot = action == MapPlan.Action.Focus ? MapPlan.ParseWorld(args) : (0f, 0f);
        if (Progenitor.Plugin.IsServerProcess) throw new InvalidOperationException(ClientOnlyMessage);

        var clock = Stopwatch.StartNew();
        var start = Read();
        if (action == MapPlan.Action.Status)
        {
            var status = Reply(true, "status", start, start, "", 0, clock);
            status["share"] = ShareBridge("Status", Array.Empty<object?>());
            status["focus"] = CoreBridge(FocusBridgeType, "Status", Array.Empty<object?>());
            return status;
        }

        if (action == MapPlan.Action.Focus)
        {
            // Core's map focus (the chat location link's Set marker after the marker): open the map centered on a spot.
            // It works over the next frames; read the result with map status (focus.last).
            var focused = CoreBridge(FocusBridgeType, "Focus", new object?[] { spot.Item1, spot.Item2 });
            Plugin.Logger?.LogInfo("Dev map focus: [" + Math.Round(spot.Item1) + ", " + Math.Round(spot.Item2) + "] "
                + (focused.TryGetValue("ok", out var fok) && fok is true ? "started" : "refused")
                + (focused.TryGetValue("why", out var fw) ? " (" + fw + ")" : ""));
            focused["action"] = "focus";
            focused["before"] = start.ToReply();
            return focused;
        }

        if (action == MapPlan.Action.Share)
        {
            var shared = ShareBridge("Share", new object?[] { point.Item1, point.Item2, point.Item3 });
            Plugin.Logger?.LogInfo("Dev map share: " + (shared.TryGetValue("token", out var t) ? t : "nothing")
                + (shared.TryGetValue("why", out var w) ? " (" + w + ")" : ""));
            shared["action"] = "share";
            shared["before"] = start.ToReply();
            return shared;
        }

        var (step, why) = action == MapPlan.Action.Open ? MapPlan.DecideOpen(start) : MapPlan.DecideClose(start);
        string verb = action == MapPlan.Action.Open ? "open" : "close";
        if (step == MapPlan.Step.NoOp) return Reply(true, verb, start, start, why, 0, clock, step);
        if (step == MapPlan.Step.Refuse)
        {
            Plugin.Logger?.LogInfo("Dev map " + verb + ": refused (" + why + ")");
            return Reply(false, verb, start, start, why, 0, clock, step);
        }

        string handler;
        switch (step)
        {
            case MapPlan.Step.Raise:
                handler = "HUDMenuManager.GoToMenu(FullscreenMenu)+FullscreenMenuMapper.SetState(Map)";
                if (!ModsMenu.TryRequestFullscreenMenu())
                    return Reply(false, verb, start, Read(), "the game menu could not be raised (HUDMenuManager.GoToMenu returned no request)", 0, clock, step);
                Plugin.Logger?.LogInfo("Dev map open: raised the game menu (HUDMenuManager.GoToMenu FullscreenMenu)");
                break;
            case MapPlan.Step.Switch:
                handler = "FullscreenMenuMapper.SetState(Map)";
                if (!TrySetMapState(out string switchError))
                    return Reply(false, verb, start, Read(), switchError, 0, clock, step);
                break;
            default:
                handler = "HUDMenuManager.CloseMenu(World)";
                if (!TryCloseMenu(out string closeError))
                    return Reply(false, verb, start, Read(), closeError, 0, clock, step);
                break;
        }

        bool wantOpen = action == MapPlan.Action.Open;
        var deferred = new DeferredResult(MapPlan.WaitMs);
        int frames = 0, stable = 0, sets = step == MapPlan.Step.Switch ? 1 : 0, sinceSet = 0;
        void Finish(bool ok, MapPlan.State now, string error)
        {
            var reply = Reply(ok, verb, start, now, error, frames, clock, step);
            reply["handler"] = handler;
            reply["setStateCalls"] = sets;
            Plugin.Logger?.LogInfo("Dev map " + verb + ": " + (ok ? "verified open=" + now.CoreOpen : "failed: " + error)
                + " after " + frames + " frame(s)");
            deferred.SetResult(reply);
        }
        void Poll()
        {
            try
            {
                if (deferred.BoundGate != null && deferred.BoundGate.ShouldSkip(deferred.BoundTicket)) return;
                frames++;
                sinceSet++;
                var now = Read();
                if (!now.InWorld)
                {
                    Finish(false, now, MapPlan.WhyTitle);
                    return;
                }
                if (wantOpen && MapPlan.ShouldSetState(now, sets, sinceSet))
                {
                    sets++;
                    sinceSet = 0;
                    stable = 0;
                    if (!TrySetMapState(out string setError))
                    {
                        Finish(false, now, setError);
                        return;
                    }
                }
                else if (now.CoreOpen == wantOpen)
                {
                    if (++stable >= MapPlan.StableFrames)
                    {
                        Finish(true, now, "");
                        return;
                    }
                }
                else stable = 0;

                if (frames >= MapPlan.VerifyFrames)
                {
                    Finish(false, now, "the map did not " + (wantOpen ? "open" : "close") + " within " + MapPlan.VerifyFrames + " frames");
                    return;
                }
                MainThread.PostDelayed(0, Poll, deferred);
            }
            catch (Exception e)
            {
                Plugin.Logger?.LogWarning("Dev map " + verb + ": " + e.Message);
                if (!deferred.Completion.Task.IsCompleted) Finish(false, Read(), e.Message);
            }
        }
        try { MainThread.PostDelayed(0, Poll, deferred); }
        catch
        {
            deferred.SetException(new InvalidOperationException("map could not schedule its readback"));
            throw;
        }
        return deferred;
    }

    /// <summary>One read: the HUD's menu, the full-screen menu's tab, the Mods workspace, the view, and Core's map detection.</summary>
    private static MapPlan.State Read()
    {
        bool inWorld = false, menuUp = false;
        string hudMenu = "", view = "";
        int? tab = null;
        try
        {
            var world = Progenitor.Worlds.Client;
            var hud = world != null && world.IsCreated ? OpenHUDMenuSystem.Get(world) : null;
            inWorld = hud != null;
            if (hud != null)
            {
                hudMenu = hud.CurrentMenuType.ToString();
                menuUp = hud.CurrentMenuType == HUDMenuType.FullscreenMenu;
                var menu = menuUp ? hud.CurrentMenu?.TryCast<FullscreenMenu>() : null;
                if (menu != null)
                {
                    try { if (menu.CurrentView != null) tab = (int)menu.CurrentView.State; } catch { tab = null; }
                }
            }
            try { view = ViewManager.GetCurrentView().ToString(); } catch { view = "?"; }
        }
        catch { /* reads as no world */ }

        string probe;
        try { probe = ModsMenu.MenuTabState().TryGetValue("probe", out var p) ? p as string ?? "" : ""; } catch { probe = "?"; }
        return new MapPlan.State(inWorld, hudMenu, menuUp, tab, ModsMenu.IsOpen, view, probe, WorldMap.IsOpen);
    }

    /// <summary><c>FullscreenMenuMapper.SetState(Map)</c>, what the menu's M handler calls on another tab.</summary>
    private static bool TrySetMapState(out string error)
    {
        error = "";
        try
        {
            var world = Progenitor.Worlds.Client;
            var mapper = world != null && world.IsCreated ? world.GetExistingSystemManaged<FullscreenMenuMapper>() : null;
            if (mapper == null)
            {
                error = "no FullscreenMenuMapper in the client world";
                return false;
            }
            mapper.SetState(FullscreenMenuState.Map);
            Plugin.Logger?.LogInfo("Dev map open: FullscreenMenuMapper.SetState(Map)");
            return true;
        }
        catch (Exception e)
        {
            error = "FullscreenMenuMapper.SetState(Map) threw: " + e.Message;
            return false;
        }
    }

    private const string ShareBridgeType = "Progenitor.Ui.MapShareTestBridge";
    private const string FocusBridgeType = "Progenitor.Ui.MapFocusTestBridge";

    /// <summary>Core's map share readback or share, by reflection (Core grants Dev no internals).</summary>
    private static Dictionary<string, object?> ShareBridge(string method, object?[] args) => CoreBridge(ShareBridgeType, method, args);

    /// <summary>A Core test bridge's static method, by reflection.</summary>
    private static Dictionary<string, object?> CoreBridge(string typeName, string method, object?[] args)
    {
        var type = typeof(ModsMenu).Assembly.GetType(typeName, throwOnError: false);
        if (type == null) return new Dictionary<string, object?> { ["ok"] = false, ["error"] = "Core has no " + typeName + " (older than this op)" };
        var mi = type.GetMethod(method, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
        if (mi == null) return new Dictionary<string, object?> { ["ok"] = false, ["error"] = typeName + "." + method + " not found" };
        try
        {
            return mi.Invoke(null, args) as Dictionary<string, object?>
                ?? new Dictionary<string, object?> { ["ok"] = false, ["error"] = method + " returned nothing" };
        }
        catch (System.Reflection.TargetInvocationException e)
        {
            return new Dictionary<string, object?> { ["ok"] = false, ["error"] = e.InnerException?.Message ?? e.Message };
        }
    }

    /// <summary><c>HUDMenuManager.CloseMenu(World)</c>, what the menu's M handler calls with the map shown.</summary>
    private static bool TryCloseMenu(out string error)
    {
        error = "";
        try
        {
            var world = Progenitor.Worlds.Client;
            if (world == null || !world.IsCreated)
            {
                error = MapPlan.WhyTitle;
                return false;
            }
            HUDMenuManager.CloseMenu(world);
            Plugin.Logger?.LogInfo("Dev map close: HUDMenuManager.CloseMenu");
            return true;
        }
        catch (Exception e)
        {
            error = "HUDMenuManager.CloseMenu threw: " + e.Message;
            return false;
        }
    }
#endif

    private static Dictionary<string, object?> Reply(
        bool ok, string action, MapPlan.State before, MapPlan.State after, string why, int frames, Stopwatch clock,
        MapPlan.Step? step = null)
    {
        var reply = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["ok"] = ok,
            ["action"] = action,
            ["open"] = after.CoreOpen,
            ["changed"] = before.CoreOpen != after.CoreOpen,
            ["step"] = step?.ToString(),
            ["before"] = before.ToReply(),
            ["after"] = after.ToReply(),
            ["framesWaited"] = frames,
            ["readback"] = frames > 0 ? "verified-after-frames" : "same-drain",
            ["ms"] = Math.Round(clock.Elapsed.TotalMilliseconds, 1),
        };
        if (why.Length > 0) reply[ok ? "why" : "error"] = why;
        return reply;
    }
}

/// <summary>
/// The game-free half of <see cref="MapOps"/>: argument parsing, the decision for each state, and the
/// select retry rule. Tested in <c>BridgeMapOpTests</c>.
/// </summary>
internal static class MapPlan
{
    internal enum Action
    {
        Status = 0,
        Open = 1,
        Close = 2,
        Share = 3,
        Focus = 4,
    }

    internal enum Step
    {
        /// <summary>Nothing to do; the reply says why.</summary>
        NoOp = 0,
        /// <summary>Refused; the reply says why.</summary>
        Refuse = 1,
        /// <summary><c>GoToMenu(FullscreenMenu)</c>, then <c>SetState(Map)</c> once the menu has a view.</summary>
        Raise = 2,
        /// <summary>The menu is up on another tab: <c>SetState(Map)</c>.</summary>
        Switch = 3,
        /// <summary>The map is shown: <c>CloseMenu(World)</c>.</summary>
        Close = 4,
    }

    internal const string UsageMessage = "map needs args.action: open, close, status, share (optional x and y) or focus (world x and z).";

    /// <summary>The <c>FullscreenMenuState</c> of the Map tab.</summary>
    internal const int MapState = 3;

    /// <summary>Most frames an open or close waits for Core's detection before replying with what it saw.</summary>
    internal const int VerifyFrames = 180;

    /// <summary>Frames Core's detection must agree before the reply (a raised menu can still pick its remembered tab on frames 0-2).</summary>
    internal const int StableFrames = 4;

    /// <summary>Frames after a <c>SetState</c> before it is called again when the menu is not on the map yet.</summary>
    internal const int SetStateRetryFrames = 10;

    /// <summary>Most <c>SetState(Map)</c> calls one open makes.</summary>
    internal const int MaxSetStateCalls = 3;

    /// <summary>The deferred reply's wait (the bridge adds its slack).</summary>
    internal const int WaitMs = 10_000;

    internal const string WhyTitle = "no-world: there is no client world (title screen or loading), so there is no map";
    internal const string WhyWorkspace = "workspace-open: the Mods workspace is open; close it first (mods_ui close)";
    internal const string WhyAlreadyOpen = "already: the map is open";
    internal const string WhyAlreadyClosed = "already: the map is not open";
    internal const string WhyNoProbe = "no-probe: Core's game-menu probe is not installed, so the map cannot be read back";

    internal static string WhyView(string view) =>
        "game-view: the game shows the full-screen view '" + view + "' (the M key does nothing there either)";

    internal static string WhyOtherMenu(string menu) =>
        "other-menu: the game's '" + menu + "' menu is open; close it first (the M key does not switch from it)";

    internal static string WhyOtherTab(string? tab) =>
        "not-map: the game menu is open on '" + (tab ?? "?") + "', not the map; left as is";

    /// <summary>Parse <c>args.action</c>. Throws <see cref="ArgumentException"/> with the usage line.</summary>
    internal static Action Parse(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty("action", out var el) || el.ValueKind != JsonValueKind.String)
            throw new ArgumentException(UsageMessage);
        switch ((el.GetString() ?? "").Trim().ToLowerInvariant())
        {
            case "open": return Action.Open;
            case "close": return Action.Close;
            case "status": return Action.Status;
            case "share": return Action.Share;
            case "focus": return Action.Focus;
            default: throw new ArgumentException("map: args.action '" + el.GetString() + "' is not open, close, status, share or focus. " + UsageMessage);
        }
    }

    /// <summary>
    /// <c>share</c>'s optional screen point: <c>x</c> and <c>y</c> (Input System pixels, origin bottom left), both or
    /// neither. Throws <see cref="ArgumentException"/> for one without the other or a value that is not a finite number.
    /// </summary>
    internal static (bool HasPoint, float X, float Y) ParsePoint(JsonElement args)
    {
        bool hasX = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("x", out var xe);
        bool hasY = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("y", out var ye);
        if (!hasX && !hasY) return (false, 0f, 0f);
        if (hasX != hasY) throw new ArgumentException("map share: give both x and y, or neither (the pointer).");
        args.TryGetProperty("x", out xe);
        args.TryGetProperty("y", out ye);
        if (xe.ValueKind != JsonValueKind.Number || ye.ValueKind != JsonValueKind.Number)
            throw new ArgumentException("map share: x and y are screen pixels (numbers).");
        float x = (float)xe.GetDouble(), y = (float)ye.GetDouble();
        if (!float.IsFinite(x) || !float.IsFinite(y)) throw new ArgumentException("map share: x and y must be finite.");
        return (true, x, y);
    }

    /// <summary><c>focus</c>'s world spot: <c>x</c> and <c>z</c> (world meters), both required and finite.</summary>
    internal static (float X, float Z) ParseWorld(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty("x", out var xe) || !args.TryGetProperty("z", out var ze))
            throw new ArgumentException("map focus: give the world spot as x and z.");
        if (xe.ValueKind != JsonValueKind.Number || ze.ValueKind != JsonValueKind.Number)
            throw new ArgumentException("map focus: x and z are world meters (numbers).");
        float x = (float)xe.GetDouble(), z = (float)ze.GetDouble();
        if (!float.IsFinite(x) || !float.IsFinite(z)) throw new ArgumentException("map focus: x and z must be finite.");
        return (x, z);
    }

    /// <summary>One read of the game's menus and of Core's map detection.</summary>
    internal readonly struct State
    {
        internal State(bool inWorld, string hudMenu, bool menuUp, int? tab, bool workspaceOpen, string view, string probe, bool coreOpen)
        {
            InWorld = inWorld;
            HudMenu = hudMenu ?? "";
            MenuUp = menuUp;
            Tab = menuUp ? tab : null;
            WorkspaceOpen = workspaceOpen;
            View = view ?? "";
            Probe = probe ?? "";
            CoreOpen = coreOpen;
        }

        internal bool InWorld { get; }
        internal string HudMenu { get; }
        internal bool MenuUp { get; }
        internal int? Tab { get; }
        internal bool WorkspaceOpen { get; }

        /// <summary><c>ViewManager.GetCurrentView()</c>; <c>None</c> in normal play.</summary>
        internal string View { get; }

        /// <summary>The installed game-menu probe's type name (<c>FullscreenMenuProbe</c> in a client world).</summary>
        internal string Probe { get; }

        /// <summary>Core's detection (<c>WorldMap.IsOpen</c> = <c>GameMenuHistory.SafeMapOpen</c>), what map-scope keybinds see.</summary>
        internal bool CoreOpen { get; }

        internal bool ProbeInstalled => Probe.Length > 0 && Probe != NullProbeName;

        internal bool ViewBlocks => View.Length > 0 && View != "None";

        internal Dictionary<string, object?> ToReply() => new(StringComparer.Ordinal)
        {
            ["inWorld"] = InWorld,
            ["hudMenu"] = HudMenu,
            ["menuOpen"] = MenuUp,
            ["tab"] = TabName(Tab),
            ["view"] = View,
            ["workspaceOpen"] = WorkspaceOpen,
            ["probe"] = Probe,
            ["mapOpen"] = CoreOpen,
        };
    }

    /// <summary>Core's probe type when no game menu can be read (title screen, server, not installed).</summary>
    internal const string NullProbeName = "NullGameMenuProbe";

    /// <summary>The game's top tabs in <c>FullscreenMenuState</c> order (client metadata; Core's <c>GameMenuHistory.TopTabs</c>).</summary>
    internal static readonly string[] TopTabs = { "Emotes", "VampirePowers", "SpellBook", "Map", "VBlood", "Social", "System" };

    internal static string? TabName(int? state) =>
        state is int s && s >= 0 && s < TopTabs.Length ? TopTabs[s] : state?.ToString();

    internal static (Step Step, string Why) DecideOpen(State now)
    {
        if (!now.InWorld) return (Step.Refuse, WhyTitle);
        if (!now.ProbeInstalled) return (Step.Refuse, WhyNoProbe);
        if (now.WorkspaceOpen) return (Step.Refuse, WhyWorkspace);
        if (now.CoreOpen) return (Step.NoOp, WhyAlreadyOpen);
        if (now.ViewBlocks) return (Step.Refuse, WhyView(now.View));
        if (now.MenuUp) return (Step.Switch, "");
        if (now.HudMenu.Length > 0 && now.HudMenu != "None") return (Step.Refuse, WhyOtherMenu(now.HudMenu));
        return (Step.Raise, "");
    }

    internal static (Step Step, string Why) DecideClose(State now)
    {
        if (!now.InWorld) return (Step.Refuse, WhyTitle);
        if (!now.ProbeInstalled) return (Step.Refuse, WhyNoProbe);
        if (now.CoreOpen) return (Step.Close, "");
        if (now.MenuUp && !now.WorkspaceOpen) return (Step.NoOp, WhyOtherTab(TabName(now.Tab)));
        return (Step.NoOp, WhyAlreadyClosed);
    }

    /// <summary>
    /// Whether an open should call <c>SetState(Map)</c> on this frame: the menu is up with a view (the
    /// mapper has started), not on the map yet, the last call has had <see cref="SetStateRetryFrames"/>
    /// frames to land, and fewer than <see cref="MaxSetStateCalls"/> calls were made.
    /// </summary>
    internal static bool ShouldSetState(State now, int callsSoFar, int framesSinceLastCall)
    {
        if (!now.MenuUp || now.Tab == null || now.WorkspaceOpen) return false;
        if (now.Tab == MapState) return false;
        if (callsSoFar >= MaxSetStateCalls) return false;
        return callsSoFar == 0 || framesSinceLastCall >= SetStateRetryFrames;
    }
}
