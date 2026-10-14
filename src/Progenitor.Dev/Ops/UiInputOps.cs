using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json;
using Progenitor.Ui;

namespace Progenitor.Dev.Ops;

/// <summary>
/// BRIDGEUI1: <c>ui_input</c>: hover, click (with Shift / Ctrl, any button), drag, the wheel, UI-layer keys (Escape,
/// Enter, Tab, editing keys), typing and field focus on the Progenitor UI kit, through the framework's own input layer:
/// Core's <c>UiSyntheticInput</c>, which the kit's samplers read instead of the devices, and a driver that sends pointer
/// events to the objects under the synthetic point through the game's <c>EventSystem</c> raycast. No OS input, no Input
/// System event, no window focus. Client only, main thread. Dev bridge only.
///
/// <para><b>Lease.</b> The first action takes a lease (<c>leaseMs</c>, default 30 s, at most 300 s); each later action
/// renews it, so a hover stays while the caller reads the tooltip. It ends on <c>release</c> (or <c>release: true</c>
/// on an action), when it runs out, when the player uses the real mouse or keyboard (<c>user-activity</c>), on a Core
/// swap and on Dev unload. Ending lets go of every button and key and sends the pointer's exits.</para>
///
/// <para><b>Keys.</b> These are UI-layer events for the kit's fields and the framework's Escape and Enter handling,
/// never keybinds: a registered bind is pressed with <c>keybind_press</c>. The game's own input (its pause menu on
/// Escape, its camera) does not see them.</para>
/// </summary>
internal static class UiInputOps
{
    internal const string ClientOnlyMessage = "ui_input runs on the client only: the server has no UI.";

    private const string BridgeType = "Progenitor.Ui.UiInputTestBridge";
    private const string TextBridgeType = "Progenitor.Ui.UiTextTestBridge";

    internal static object? Call(string type, string method, params object?[] args)
    {
        var t = typeof(ModsMenu).Assembly.GetType(type, throwOnError: false)
            ?? throw new InvalidOperationException("Core has no " + type + " (older than this op)");
        var mi = t.GetMethod(method, BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException(type + "." + method + " not found");
        try { return mi.Invoke(null, args); }
        catch (TargetInvocationException e) { throw new InvalidOperationException(e.InnerException?.Message ?? e.Message); }
    }

    internal static object? Bridge(string method, params object?[] args) => Call(BridgeType, method, args);

    internal static Dictionary<string, object?> Dict(object? o) =>
        o as Dictionary<string, object?> ?? new Dictionary<string, object?> { ["ok"] = false, ["error"] = "Core returned nothing" };

#if PROGENITOR_TESTS
    internal static object Run(JsonElement args)
    {
        UiInputPlan.Parse(args);
        throw new InvalidOperationException(ClientOnlyMessage);
    }

    internal static void Shutdown() { }
#else
    internal static object Run(JsonElement args)
    {
        var request = UiInputPlan.Parse(args);
        if (Progenitor.Plugin.IsServerProcess) throw new InvalidOperationException(ClientOnlyMessage);
        var reply = new Dictionary<string, object?>(StringComparer.Ordinal) { ["request"] = request.Describe() };

        switch (request.Action)
        {
            case UiInputPlan.Action.Status:
                reply["ok"] = true;
                reply["state"] = Bridge("State");
                reply["tooltip"] = Bridge("Tooltip");
                return reply;
            case UiInputPlan.Action.Release:
                bool was = Bridge("Active") is true;
                Bridge("Release", "release");
                reply["ok"] = true;
                reply["released"] = was;
                reply["state"] = Bridge("State");
                return reply;
        }

        string? refused = Bridge("Begin", request.LeaseMs) as string;
        if (refused != null)
        {
            reply["ok"] = false;
            reply["error"] = refused;
            return reply;
        }

        Bridge("ClearLog");
        switch (request.Action)
        {
            case UiInputPlan.Action.Up:
                return RunUp(request, reply);
            case UiInputPlan.Action.Key:
                return RunKey(request, reply);
            case UiInputPlan.Action.Type:
                return RunType(request, reply);
            case UiInputPlan.Action.Focus:
            {
                var at = Resolve(request.At!);
                reply["target"] = at;
                if (at["ok"] is not true) return Fail(reply, at["error"] as string ?? "target not found");
                var focus = Dict(Bridge("FocusAt", ToF(at["x"]), ToF(at["y"])));
                reply["focus"] = focus;
                reply["ok"] = focus["ok"] is true;
                if (focus["ok"] is not true) reply["error"] = focus["error"];
                Finish(request);
                return reply;
            }
        }

        return RunPointer(request, reply);
    }

    private static Dictionary<string, object?> Fail(Dictionary<string, object?> reply, string error)
    {
        reply["ok"] = false;
        reply["error"] = error;
        return reply;
    }

    private static float ToF(object? o) => o switch
    {
        float f => f,
        double d => (float)d,
        int i => i,
        _ => 0f,
    };

    private static Dictionary<string, object?> Resolve(UiInputPlan.Target t) =>
        Dict(Bridge("Resolve", t.Row, t.Cell, t.Panel, t.X, t.Y, t.HasPoint, t.Fx, t.Fy, t.Dx, t.Dy));

    private static void Finish(UiInputPlan.Request request)
    {
        if (request.ReleaseAfter) Bridge("Release", "release");
    }

    private static object RunPointer(UiInputPlan.Request request, Dictionary<string, object?> reply)
    {
        var at = Resolve(request.At!);
        reply["target"] = at;
        if (at["ok"] is not true) return Fail(reply, at["error"] as string ?? "target not found");
        float x = ToF(at["x"]), y = ToF(at["y"]), toX = 0f, toY = 0f;
        if (request.To != null)
        {
            var to = Resolve(request.To);
            reply["to"] = to;
            if (to["ok"] is not true) return Fail(reply, to["error"] as string ?? "drag target not found");
            toX = ToF(to["x"]);
            toY = ToF(to["y"]);
        }

        var timeline = UiInputPlan.Timeline(request, x, y, toX, toY);
        int last = timeline[timeline.Count - 1].Frame;
        var deferred = new DeferredResult(Math.Max(5_000, last * 50 + 5_000));
        int frame = -1;
        int next = 0;
        int notchWait = 0;
        Bridge("Modifiers", request.Modifiers);

        void Step()
        {
            try
            {
                if (deferred.BoundGate != null && deferred.BoundGate.ShouldSkip(deferred.BoundTicket)) return;
                frame++;
                if (Bridge("Active") is not true)
                {
                    reply["ended"] = Bridge("LastEnd");
                    Complete(false, "the synthetic input ended before the action finished (" + reply["ended"] + ")");
                    return;
                }

                while (next < timeline.Count && timeline[next].Frame <= frame)
                {
                    var s = timeline[next];
                    if (s.Kind == UiInputPlan.StepKind.Done)
                    {
                        // Wheel: wait for the pump to take every queued notch (it samples only while a panel is shown).
                        if (request.Action == UiInputPlan.Action.Wheel && Bridge("PendingNotches") is int left && left > 0 && notchWait++ < 120) break;
                        if (request.Action == UiInputPlan.Action.Wheel && Bridge("PendingNotches") is int still && still > 0)
                        {
                            reply["notchesNotTaken"] = still;
                            Complete(false, "the shown-input pump did not take the notches (no Progenitor panel is shown?)");
                            return;
                        }

                        Complete(true, null);
                        return;
                    }

                    switch (s.Kind)
                    {
                        case UiInputPlan.StepKind.Move: Bridge("Move", s.X, s.Y); break;
                        case UiInputPlan.StepKind.Down: Bridge("Buttons", request.Button); break;
                        case UiInputPlan.StepKind.Up: Bridge("Buttons", 0); break;
                        case UiInputPlan.StepKind.Wheel: Bridge("Wheel", request.Notches); break;
                    }

                    next++;
                }

                if (frame > 5_000)
                {
                    Complete(false, "timed out");
                    return;
                }

                MainThread.PostDelayed(0, Step, deferred);
            }
            catch (Exception e)
            {
                try { Bridge("Buttons", 0); } catch { /* */ }
                Complete(false, e.Message);
            }
        }

        void Complete(bool ok, string? error)
        {
            if (deferred.Completion.Task.IsCompleted) return;
            try
            {
                if (!request.KeepDown || !ok) Bridge("Modifiers", 0);
                if (!ok) Bridge("Buttons", 0);
                reply["ok"] = ok;
                if (error != null) reply["error"] = error;
                reply["framesWaited"] = frame;
                reply["state"] = Bridge("State");
                reply["tooltip"] = Bridge("Tooltip");
                Finish(request);
            }
            catch (Exception e) { reply["readbackError"] = e.Message; }
            deferred.SetResult(reply);
        }

        MainThread.PostDelayed(0, Step, deferred);
        return deferred;
    }

    /// <summary>Let go of the buttons a <c>keepDown</c> drag left held, where the pointer is; reply a few frames later.</summary>
    private static object RunUp(UiInputPlan.Request request, Dictionary<string, object?> reply)
    {
        Bridge("Buttons", 0);
        var deferred = new DeferredResult(5_000);
        int frames = 0;

        void Step()
        {
            if (deferred.BoundGate != null && deferred.BoundGate.ShouldSkip(deferred.BoundTicket)) return;
            if (++frames < UiInputPlan.AfterFrames + 2)
            {
                MainThread.PostDelayed(0, Step, deferred);
                return;
            }

            try
            {
                Bridge("Modifiers", 0);
                reply["ok"] = true;
                reply["state"] = Bridge("State");
                Finish(request);
            }
            catch (Exception e) { reply["readbackError"] = e.Message; }
            deferred.SetResult(reply);
        }

        MainThread.PostDelayed(0, Step, deferred);
        return deferred;
    }

    private static object RunKey(UiInputPlan.Request request, Dictionary<string, object?> reply)
    {
        var tap = Dict(Bridge("Tap", request.Key, request.KeyFrames));
        reply["tap"] = tap;
        if (tap["ok"] is not true) return Fail(reply, tap["error"] as string ?? "key refused");
        var deferred = new DeferredResult(5_000);
        int frames = 0;

        void Step()
        {
            if (deferred.BoundGate != null && deferred.BoundGate.ShouldSkip(deferred.BoundTicket)) return;
            frames++;
            if (frames < request.KeyFrames + UiInputPlan.AfterFrames + 1)
            {
                MainThread.PostDelayed(0, Step, deferred);
                return;
            }

            reply["ok"] = true;
            reply["framesWaited"] = frames;
            try
            {
                reply["focus"] = Bridge("Focus");
                reply["tooltip"] = Bridge("Tooltip");
                Finish(request);
            }
            catch (Exception e) { reply["readbackError"] = e.Message; }
            deferred.SetResult(reply);
        }

        MainThread.PostDelayed(0, Step, deferred);
        return deferred;
    }

    private static object RunType(UiInputPlan.Request request, Dictionary<string, object?> reply)
    {
        var queued = Dict(Call(TextBridgeType, "Enqueue", "os-text", request.Text, null, 1));
        reply["queued"] = queued;
        if (queued["ok"] is not true)
        {
            try { Call(TextBridgeType, "Reset"); } catch { /* */ }
            return Fail(reply, queued["error"] as string ?? "no kit field has the keyboard (focus one first: ui_input focus or click)");
        }

        var deferred = new DeferredResult(Math.Max(5_000, request.Text!.Length * 50 + 5_000));
        int frames = 0;

        void Step()
        {
            if (deferred.BoundGate != null && deferred.BoundGate.ShouldSkip(deferred.BoundTicket)) return;
            frames++;
            bool pending = Call(TextBridgeType, "Pending") is int n && n > 0;
            if (pending && frames < request.Text!.Length * 4 + 60)
            {
                MainThread.PostDelayed(0, Step, deferred);
                return;
            }

            try
            {
                reply["typed"] = Call(TextBridgeType, "Finish");
                reply["ok"] = !pending;
                if (pending) reply["error"] = "the field stopped taking text (it lost the keyboard?)";
                reply["focus"] = Bridge("Focus");
                Finish(request);
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

    /// <summary>Dev unload: end the lease (buttons up, exits sent) and drop navigation holds.</summary>
    internal static void Shutdown()
    {
        try
        {
            Bridge("Release", "unload");
            Bridge("ClearNav");
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("Dev ui_input: release on unload threw: " + e.Message); }
    }
#endif
}
