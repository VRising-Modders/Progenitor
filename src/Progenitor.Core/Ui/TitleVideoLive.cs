#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;
using ProjectM.UI;
using UnityEngine;
using UnityEngine.Video;

namespace Progenitor.Ui;

/// <summary>
/// TITLEVIDEO1: the live half of retiring the video players before a Progenitor quit (the delay and the readback words in
/// <see cref="TitleVideoPolicy"/> and <see cref="TitleVideoQuit"/>). Client only; owned by this Core generation and
/// released on every unload reason. Bootstrap is not involved.
///
/// <para><b>How a player is retired, and why this way.</b> The quit hang is the video system's engine-teardown loop
/// retrying a playback whose release is still pending; only the per-frame video update clears that, and it no longer
/// runs during engine cleanup. So the playback has to be released while frames still run:</para>
/// <list type="number">
/// <item>The game's own <c>StunVideoPlayer.Stop()</c> (the title view's wrapper: it stops the clip and the audio it drives
/// together, the same call the game uses).</item>
/// <item><c>StunVideoPlayer.enabled = false</c>, so its own <c>Update</c> never touches the player destroyed next. The
/// title view's <c>Update</c> reads only the wrapper's plain <c>isPrepared</c> field, so it keeps working with the
/// player gone.</item>
/// <item><c>Object.Destroy</c> on the <c>UnityEngine.Video.VideoPlayer</c> component. Destroying the component is what
/// hands its playback back to the video system in a normal frame; <c>Stop</c> or <c>Pause</c> alone keep the player and
/// its playback alive until engine cleanup (the hang's own stack is a <c>Stop</c> inside that cleanup), and disabling the
/// component or its GameObject leaves the player for the game to start again. Destroying the whole GameObject is not
/// needed.</item>
/// <item>The quit then waits <see cref="TitleVideoPolicy.QuitFrames"/> normal frames (at most
/// <see cref="TitleVideoPolicy.QuitMaxSeconds"/>) so the video update finishes the release before
/// <c>Application.Quit</c>.</item>
/// </list>
/// </summary>
internal static class TitleVideoLive
{
    private static bool _installed;

    internal static void Install()
    {
        if (_installed || Plugin.IsServerProcess) return;
        _installed = true;
        TitleVideoQuit.Retirer = RetireAllForQuit;
        Plugin.Frame += OnFrame;
    }

    internal static void Uninstall()
    {
        if (!_installed) return;
        _installed = false;
        try { Plugin.Frame -= OnFrame; } catch { /* */ }
        // A quit still waiting for its frames runs now rather than being dropped with this generation.
        try { TitleVideoQuit.FlushForUnload(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("TitleVideo: quit at unload threw: " + e.Message); }
        TitleVideoQuit.Retirer = null;
    }

    private static void OnFrame()
    {
        try { TitleVideoQuit.Tick(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("TitleVideo: quit tick threw: " + e.Message); }
    }

    // ---- retiring ------------------------------------------------------------------------------------

    /// <summary>
    /// Every <c>StunVideoPlayer</c> (stopped, disabled), then every <c>VideoPlayer</c> still alive (stopped, destroyed).
    /// Answers how many video players were destroyed.
    /// </summary>
    private static int RetireAllForQuit()
    {
        // Object.Destroy takes effect at the end of this frame, so a player retired through its wrapper is still found
        // by the second search: the ids keep it from being counted (and destroyed) twice.
        var done = new HashSet<int>();
        int destroyed = 0;
        try
        {
            var stuns = UnityEngine.Object.FindObjectsOfType<StunVideoPlayer>(true);
            if (stuns != null)
                for (int i = 0; i < stuns.Length; i++)
                {
                    if (!UnityAlive.Of(stuns[i])) continue;
                    int id = SafeId(SafePlayer(stuns[i]));
                    if (RetireStun(stuns[i]) == 0) continue;
                    destroyed++;
                    if (id != 0) done.Add(id);
                }
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("TitleVideo: finding the game's video players threw: " + e.Message); }

        try
        {
            var players = UnityEngine.Object.FindObjectsOfType<VideoPlayer>(true);
            if (players != null)
                for (int i = 0; i < players.Length; i++)
                {
                    if (done.Contains(SafeId(players[i]))) continue;
                    if (DestroyPlayer(players[i])) destroyed++;
                }
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("TitleVideo: finding video players threw: " + e.Message); }

        return destroyed;
    }

    /// <summary>The game's own Stop, then the wrapper off, then its <c>VideoPlayer</c> destroyed. Answers 1 or 0.</summary>
    private static int RetireStun(StunVideoPlayer stun)
    {
        VideoPlayer? player = SafePlayer(stun);
        if (UnityAlive.Of(player))
        {
            try { stun.Stop(); }
            catch (Exception e) { Plugin.Logger?.LogWarning("TitleVideo: the game's video Stop threw: " + e.Message); }
        }

        try { stun.enabled = false; } catch { /* */ }
        return DestroyPlayer(player) ? 1 : 0;
    }

    private static bool DestroyPlayer(VideoPlayer? player)
    {
        if (!UnityAlive.Of(player)) return false;
        try { player!.Stop(); } catch { /* destroying releases it anyway */ }
        try
        {
            UnityEngine.Object.Destroy(player);
            return true;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("TitleVideo: destroying a video player threw: " + e.Message);
            return false;
        }
    }

    // ---- reads ---------------------------------------------------------------------------------------

    private static StunVideoPlayer? SafeStun(MainMenuNewView view)
    {
        try { return view.VideoPlayer; } catch { return null; }
    }

    private static VideoPlayer? SafePlayer(StunVideoPlayer stun)
    {
        try { return stun.Player; } catch { return null; }
    }

    private static int SafeId(UnityEngine.Object? o)
    {
        try { return UnityAlive.Of(o) ? o!.GetInstanceID() : 0; } catch { return 0; }
    }

    /// <summary>
    /// True when <paramref name="view"/>'s title video was retired: the game's wrapper is there with its player gone.
    /// Read by <c>NativeModsEntry</c>'s video read, which would otherwise read the destroyed player.
    /// </summary>
    internal static bool IsRetired(MainMenuNewView? view)
    {
        if (!UnityAlive.Of(view)) return false;
        var stun = SafeStun(view!);
        return UnityAlive.Of(stun) && !UnityAlive.Of(SafePlayer(stun!));
    }

    /// <summary>The <c>mods_ui scene</c> <c>titleVideo</c> block (<see cref="TitleVideoPolicy.Readback"/>).</summary>
    internal static Dictionary<string, object?> Describe()
    {
        var view = NativeModsEntry.LiveTitleView();
        int players = 0;
        bool retired = false;
        try
        {
            if (UnityAlive.Of(view))
            {
                var all = view!.gameObject.GetComponentsInChildren<VideoPlayer>(true);
                if (all != null)
                    for (int i = 0; i < all.Length; i++)
                        if (UnityAlive.Of(all[i])) players++;
                retired = IsRetired(view);
            }
        }
        catch { /* */ }

        return TitleVideoPolicy.Readback(players, retired);
    }
}

/// <summary>
/// TITLEVIDEO1: the Dev bridge <c>quit</c> op's entry. Public only because Core grants <c>Progenitor.Dev</c> no
/// <c>InternalsVisibleTo</c>; not a plugin API. Main thread.
/// </summary>
public static class TitleVideoQuitBridge
{
    /// <summary>Retire the video players, then <c>Application.Quit</c> after the bounded wait. Answers the step's state.</summary>
    public static Dictionary<string, object?> Quit(string reason)
    {
        string state = TitleVideoQuit.Request(reason, () => Application.Quit());
        return new Dictionary<string, object?>
        {
            ["state"] = state,
            ["pending"] = TitleVideoQuit.Pending,
            ["maxFrames"] = TitleVideoPolicy.QuitFrames,
            ["maxSeconds"] = TitleVideoPolicy.QuitMaxSeconds,
            ["lastQuit"] = TitleVideoQuit.LastQuitReadback(),
        };
    }
}
#endif
