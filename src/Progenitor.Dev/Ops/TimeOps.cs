using System;
using System.Collections.Generic;
using System.Text.Json;
using Progenitor.Clock;

namespace Progenitor.Dev.Ops;

/// <summary>
/// Debug-bridge <c>time</c>: the game's replicated world speed through
/// <see cref="TimeScaleBridge"/>, the same leases <c>ctx.Time.Freeze</c> / <c>SlowMotion</c> take.
/// Server only. The bridge holds one lease like a plugin would; <c>resume</c> / <c>release</c> drop
/// it, so the previous lease governs or the speed from before the first lease (normally 1) returns.
/// Every action answers with the status after it.
/// </summary>
internal static class TimeOps
{
    public static object Run(JsonElement args)
    {
        var request = TimeArgs.Parse(args);
        bool ok = true;
        string? refused = null;
        bool released = false;
        switch (request.Action)
        {
            case TimeAction.Speed:
                ok = TimeScaleBridge.SetSpeed(request.Value, out var whySpeed);
                if (!ok) refused = whySpeed;
                break;
            case TimeAction.Freeze:
                ok = TimeScaleBridge.Freeze(out var whyFreeze);
                if (!ok) refused = whyFreeze;
                break;
            case TimeAction.Resume:
            case TimeAction.Release:
                released = TimeScaleBridge.Release();
                break;
        }

        var reading = TimeScaleBridge.Read(out string why);
        var leases = new List<object>();
        foreach (var l in TimeScaleBridge.Leases())
            leases.Add(new { owner = l.Owner, kind = l.Kind.ToString(), scale = l.Scale, governs = l.Governs });

        return new
        {
            action = request.Action.ToString().ToLowerInvariant(),
            ok,
            refused,
            released = request.Action is TimeAction.Resume or TimeAction.Release ? released : (bool?)null,
            value = reading?.Scale,
            paused = reading?.Paused,
            engineScale = reading == null || float.IsNaN(reading.EngineScale) ? (float?)null : reading.EngineScale,
            governing = reading?.Override.ToString(),
            bridgeHolds = TimeScaleBridge.Holds,
            leases,
            readError = reading == null ? why : null,
        };
    }

    /// <summary>Dev unload: drop the bridge's lease so a bridge freeze never outlives the bridge.</summary>
    public static void Shutdown()
    {
        try { TimeScaleBridge.Release(); }
        catch (Exception) { /* Core may already be gone; its uninstall released every lease */ }
    }
}
