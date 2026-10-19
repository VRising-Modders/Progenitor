#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;
using ProjectM.UI;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Video;

namespace Progenitor.Ui;

/// <summary>
/// TITLEVIDEO1: the live half of retiring the title screen's background video (decisions and words in
/// <see cref="TitleVideoPolicy"/>, the quit delay in <see cref="TitleVideoQuit"/>). Client only; everything is owned by
/// this Core generation and released on every unload reason. Bootstrap is not involved.
///
/// <para><b>How a player is retired, and why this way.</b> The quit hang is the video system's engine-teardown loop
/// retrying a playback whose release is still pending; only the per-frame video update clears that, and it no longer
/// runs during engine cleanup. So the playback has to be released while frames still run:</para>
/// <list type="number">
/// <item>The game's own <c>StunVideoPlayer.Stop()</c> (the title view's wrapper: it stops the clip and the audio it drives
/// together, the same call the game uses).</item>
/// <item><c>StunVideoPlayer.enabled = false</c>, so its own <c>Update</c> never touches the player destroyed next. The
/// title view's <c>Update</c> reads only the wrapper's plain <c>isPrepared</c> field (to fade the cover in), so it keeps
/// working with the player gone.</item>
/// <item><c>Object.Destroy</c> on the <c>UnityEngine.Video.VideoPlayer</c> component. Destroying the component is what
/// hands its playback back to the video system in a normal frame; <c>Stop</c> or <c>Pause</c> alone keep the player and
/// its playback alive until engine cleanup (the hang's own stack is a <c>Stop</c> inside that cleanup), and disabling the
/// component or its GameObject leaves the player for the game to start again. Destroying the whole GameObject is not
/// needed and would also take the picture the title screen draws.</item>
/// <item>A quit then waits <see cref="TitleVideoPolicy.QuitFrames"/> normal frames (at most
/// <see cref="TitleVideoPolicy.QuitMaxSeconds"/>) so the video update finishes the release before
/// <c>Application.Quit</c>.</item>
/// </list>
///
/// <para><b>The still picture.</b> With <c>[TitleScreen] Video</c> off, the current frame is copied on the GPU into an
/// owned <c>RenderTexture</c> (<c>progenitor-title-still</c>) before the player is stopped, and the title's
/// <c>RawImage</c> shows that copy, so the background never depends on what the video's own target keeps after its player
/// is gone. On unload the copy is written back into the video's own target texture (when it has one) and the
/// <c>RawImage</c> gets its texture back, so the title keeps the picture with nothing of ours left. When the title screen
/// goes away the copy is destroyed with it.</para>
/// </summary>
internal static class TitleVideoLive
{
    internal const string StillName = "progenitor-title-still";
    private const int FindEveryFrames = 30;

    private static bool _installed;
    private static IDisposable? _settingLease;
    private static MainMenuNewView? _view;
    private static int _viewId;
    private static double _viewSeenAt;
    private static bool _retiredThisView;
    private static int _findCountdown;
    private static string _why = "";

    // The still picture and where it is shown.
    private static RenderTexture? _still;
    private static RawImage? _stillImage;
    private static Texture? _stillOriginal;

    internal static void Install()
    {
        if (_installed || Plugin.IsServerProcess) return;
        _installed = true;
        BindSetting();
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
        ReleaseStill(unload: true);
        try { _settingLease?.Dispose(); } catch { /* */ }
        _settingLease = null;
        CoreUiSettings.TitleScreenVideo = null;
        TitleVideoQuit.Retirer = null;
        _view = null;
        _viewId = 0;
        _retiredThisView = false;
        _why = "";
    }

    private static void BindSetting()
    {
        var file = UiKeybindHost.CoreConfig;
        if (file == null) return;
        try
        {
            var entry = file.Bind(TitleVideoPolicy.Section, TitleVideoPolicy.Key, true, TitleVideoPolicy.Description);
            CoreUiSettings.TitleScreenVideo = entry;
            _settingLease = SettingsCatalog.RegisterSetting(SettingsPanel.PluginId, entry, new SettingPresentation
            {
                Label = TitleVideoPolicy.Label,
                Help = TitleVideoPolicy.Help,
                Group = TitleVideoPolicy.Group,
                Order = 10,
                EditorHint = SettingsEditorHint.Toggle,
                CommitPolicy = SettingCommitPolicy.Immediate,
            }, typeof(TitleVideoLive));
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("TitleVideo: [" + TitleVideoPolicy.Section + "] " + TitleVideoPolicy.Key
                + " could not be bound (" + e.Message + "); the title screen video stays on.");
        }
    }

    // ---- per frame ----------------------------------------------------------------------------------

    private static void OnFrame()
    {
        try { TitleVideoQuit.Tick(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("TitleVideo: quit tick threw: " + e.Message); }
        try { TickTitle(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("TitleVideo: title tick threw: " + e.Message); }
    }

    private static double Now() => Environment.TickCount64 / 1000.0;

    private static void TickTitle()
    {
        if (!UnityAlive.Of(_view))
        {
            if (_view != null || _viewId != 0) LeaveView();
            // With the video on (the default) there is nothing to look for: the quit path finds its own players.
            if (TitleVideoPolicy.SettingOn) return;
            if (--_findCountdown > 0) return;
            _findCountdown = FindEveryFrames;
            var found = NativeModsEntry.LiveTitleView();
            if (!UnityAlive.Of(found)) return;
            _view = found;
            _viewId = SafeId(found);
            _viewSeenAt = Now();
            _retiredThisView = false;
        }

        var view = _view!;
        // Turned back on: a retired player cannot be started again, so the still stays until this title screen goes.
        if (TitleVideoPolicy.SettingOn) return;
        bool active = false;
        try { active = view.gameObject.activeInHierarchy; } catch { /* */ }
        if (!active) return;

        var stun = SafeStun(view);
        var player = stun == null ? null : SafePlayer(stun);
        bool playerAlive = UnityAlive.Of(player);
        bool ready = playerAlive && FirstFrameReady(player!);
        var action = TitleVideoPolicy.AtTitle(TitleVideoPolicy.SettingOn, true, playerAlive, ready, Now() - _viewSeenAt);
        if (action != TitleVideoPolicy.TitleAction.Retire) return;

        bool still = MakeStill(view, player!);
        int n = RetireStun(stun!);
        _retiredThisView = true;
        _why = "setting";
        Plugin.Logger?.LogInfo("TitleVideo: the title screen video is off; retired " + n + " video player(s)"
            + (still ? ", showing a still picture" : ready ? ", no still picture (the video's picture was not found)" : ", before its first frame"));
    }

    /// <summary>The title view left (a world opened) or died: the still goes with it.</summary>
    private static void LeaveView()
    {
        ReleaseStill(unload: false);
        _view = null;
        _viewId = 0;
        _retiredThisView = false;
        _why = "";
        _findCountdown = 0;
    }

    private static bool FirstFrameReady(VideoPlayer player)
    {
        try
        {
            if (!player.isPrepared) return false;
            return player.frame >= 1;
        }
        catch { return false; }
    }

    // ---- retiring ------------------------------------------------------------------------------------

    /// <summary>
    /// The quit path: every <c>StunVideoPlayer</c> (stopped, disabled), then every <c>VideoPlayer</c> still alive
    /// (stopped, destroyed). Answers how many video players were destroyed.
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

        if (destroyed > 0 && UnityAlive.Of(_view))
        {
            _retiredThisView = true;
            if (_why.Length == 0) _why = "quit";
        }

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

    // ---- the still picture ---------------------------------------------------------------------------

    private static bool MakeStill(MainMenuNewView view, VideoPlayer player)
    {
        ReleaseStill(unload: false);
        try
        {
            Texture? source = null;
            try
            {
                if (player.renderMode == VideoRenderMode.RenderTexture) source = player.targetTexture;
            }
            catch { /* */ }
            if (!UnityAlive.Of(source))
            {
                try { source = player.texture; } catch { source = null; }
            }

            if (!UnityAlive.Of(source)) return false;
            var image = FindImageShowing(view, source!);
            if (image == null) return false;

            int w = 0, h = 0;
            try { w = source!.width; h = source.height; } catch { /* */ }
            if (w <= 0 || h <= 0) return false;

            var still = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32) { name = StillName };
            try
            {
                Graphics.Blit(source, still);
                _stillOriginal = image.texture;
                image.texture = still;
            }
            catch
            {
                try { still.Release(); UnityEngine.Object.Destroy(still); } catch { /* */ }
                _stillOriginal = null;
                return false;
            }

            _still = still;
            _stillImage = image;
            return true;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("TitleVideo: the still picture threw: " + e.Message);
            return false;
        }
    }

    private static RawImage? FindImageShowing(MainMenuNewView view, Texture source)
    {
        int sourceId = SafeId(source);
        try
        {
            var image = view.VideoImage;
            if (UnityAlive.Of(image) && SafeId(image.texture) == sourceId) return image;
        }
        catch { /* */ }

        try
        {
            var images = view.gameObject.GetComponentsInChildren<RawImage>(true);
            if (images == null) return null;
            for (int i = 0; i < images.Length; i++)
            {
                var raw = images[i];
                if (!UnityAlive.Of(raw)) continue;
                try { if (SafeId(raw.texture) == sourceId) return raw; }
                catch { /* keep looking */ }
            }
        }
        catch { /* */ }

        return null;
    }

    private static void ReleaseStill(bool unload)
    {
        var still = _still;
        var image = _stillImage;
        var original = _stillOriginal;
        _still = null;
        _stillImage = null;
        _stillOriginal = null;
        if (still == null) return;

        try
        {
            if (unload && UnityAlive.Of(image) && UnityAlive.Of(still))
            {
                // Leave the picture in the game's own texture, then give the RawImage that texture back.
                bool keep = false;
                try
                {
                    var target = original == null ? null : original.TryCast<RenderTexture>();
                    if (UnityAlive.Of(target))
                    {
                        Graphics.Blit(still, target);
                        keep = true;
                    }
                }
                catch { /* the picture may go dark; the texture still comes back */ }

                try { if (SafeId(image!.texture) == SafeId(still)) image.texture = original; } catch { /* */ }
                if (!keep) Plugin.Logger?.LogInfo("TitleVideo: unloading with the still picture shown; the video's own texture has no copy of it");
            }
        }
        catch { /* */ }

        try { if (UnityAlive.Of(still)) still.Release(); } catch { /* */ }
        try { if (UnityAlive.Of(still)) UnityEngine.Object.Destroy(still); } catch { /* */ }
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
    /// True when <paramref name="view"/>'s title video is retired: this generation retired it, or the game's wrapper is
    /// there with its player gone (an earlier generation retired it). Read by <c>NativeModsEntry</c>'s video read.
    /// </summary>
    internal static bool IsRetired(MainMenuNewView? view)
    {
        if (!UnityAlive.Of(view)) return false;
        if (_retiredThisView && SafeId(view) == _viewId) return true;
        var stun = SafeStun(view!);
        return UnityAlive.Of(stun) && !UnityAlive.Of(SafePlayer(stun!));
    }

    /// <summary>The <c>mods_ui scene</c> <c>titleVideo</c> block (<see cref="TitleVideoPolicy.Readback"/>).</summary>
    internal static Dictionary<string, object?> Describe()
    {
        var view = UnityAlive.Of(_view) ? _view : NativeModsEntry.LiveTitleView();
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

        return TitleVideoPolicy.Readback(players, retired, UnityAlive.Of(_still), _why);
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
