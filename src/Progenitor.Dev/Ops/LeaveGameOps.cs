using System;
using System.Diagnostics;
using ProjectM.UI;
using UnityEngine;

namespace Progenitor.Dev.Ops;

/// <summary>Focus-free production UI transition. Polling reports observations, not assumed completion.</summary>
internal static class LeaveGameOps
{
    private static readonly LeaveGamePolicy Policy = new();
    private static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    internal static object Request(bool statusOnly, bool launchInFlight)
    {
        if (Progenitor.Plugin.IsServerProcess)
            throw new InvalidOperationException("disconnect runs on the client side only");
        var world = Progenitor.Worlds.Client;
        bool hasWorld = world != null && world.IsCreated;
        var view = ViewManager.GetCurrentView();
        var menu = UnityEngine.Object.FindObjectOfType<MainMenuNewView>();
        bool menuVisible = menu != null && menu.gameObject.activeInHierarchy;
        bool verified = !hasWorld && view == ViewLocation.MainMenu && menuVisible;
        string state = Policy.Observe(hasWorld, hasWorld ? world!.SequenceNumber : 0,
            verified, statusOnly, launchInFlight, Now);
        if (state != "request")
            return Result(state, verified, Policy.Requested, hasWorld, view, menuVisible,
                Policy.Elapsed, Policy.Failure);

        // The game's native Leave confirm calls GoToView<EmptyInitData>(26, null).
        // This non-generic wrapper forwards to exactly that call.
        // ExitGameOverlay then yields a frame and requests MainMenu.
        // No OS input or Harmony callback.
        try
        {
            ViewManager.GoToView(ViewLocation.ExitGame);
            Plugin.Logger?.LogInfo("disconnect: requested production ExitGame view; completion pending");
        }
        catch (Exception ex)
        {
            Policy.Fail(ex.Message);
        }
        return Result(Policy.Failure == null ? "pending" : "requestFailed", false, true,
            hasWorld, view, menuVisible, 0, Policy.Failure);
    }

    private static object Result(string state, bool verified, bool requested, bool clientWorldPresent,
        ViewLocation view, bool mainMenuVisible, double elapsedSeconds, string? error) => new
        {
            ok = error == null,
            state, verified, requested, clientWorldPresent,
            view = view.ToString(), mainMenuVisible, elapsedSeconds, error,
            note = "Poll disconnect with args.statusOnly=true; pending is not proof of leaving. No focus or OS input is used."
        };
}
