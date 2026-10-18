using System;

namespace Progenitor.Dev.Ops;

/// <summary>
/// Game-free decisions for the client <c>create_character</c> op (<see cref="CharacterCreateOps"/>):
/// whether to send, and how to take the client off the character-creation scene once its character
/// exists.
///
/// <para><b>Why a client can stay on the creation scene</b> (client metadata, checked 2026-10-18).
/// While a character-less user's join loads, the loading screen (<c>ConnectingView</c>) asks
/// <c>ClientBootstrapSystem.GetShouldCreateCharacter</c> and, when it is true, instantiates the
/// creation scene (a <c>CustomizationBrowser</c> with a <c>CharacterCreationCamera</c>, which
/// narrows the main camera to the scene's layers until it is destroyed). When the player continues,
/// the loading screen asks again: true opens <c>HUDMenuType.CharacterCreationMenu</c>, false goes
/// straight to the game. Only the creation menu's shutdown (<c>CharacterCreationMenuMapper.DestroyBrowser</c>
/// from <c>OnStopRunning</c>) destroys the scene. So a create that lands during loading (the bridge
/// sends whenever it is asked) skips the menu and leaves the scene and its camera behind: the
/// character plays normally behind the crypt view (seen live 2026-10-18).</para>
///
/// <para><b>The game's own exit from the menu.</b> In create mode the mapper's <c>OnUpdate</c> watches
/// the local user; once it has a character entity, it calls <c>HUDMenuManager.GoToMenu(world, WakeUpMenu)</c>,
/// and the wake-up menu returns to gameplay by itself.</para>
///
/// <para><b>What the op does.</b> After its character exists and the client is past the loading
/// screen, it gives the game <see cref="GameExitGraceFrames"/> frames, then makes the game's own
/// call for what is left: <c>GoToMenu(WakeUpMenu)</c> while the creation menu is still current
/// (<see cref="Step.Leave"/>), or <c>DestroyBrowser</c>'s <c>Object.Destroy</c> of a creation scene
/// that outlived its menu (<see cref="Step.Clear"/>).</para>
/// </summary>
internal static class CharacterCreatePlan
{
    /// <summary><c>HUDMenuType.CharacterCreationMenu</c> (13) by name, as <c>CurrentMenuType.ToString()</c> reads it.</summary>
    internal const string CreationMenu = "CharacterCreationMenu";

    /// <summary><c>HUDMenuType.WakeUpMenu</c> (12): where the game's own exit goes after a successful create.</summary>
    internal const string WakeUpMenu = "WakeUpMenu";

    /// <summary><c>ViewLocation</c> names of the loading screens; the creation scene belongs there.</summary>
    internal static readonly string[] LoadingViews = { "Loading", "GameConnectingAndLoading" };

    /// <summary>How long the op waits for the server to spawn the character after a send.</summary>
    internal const int SpawnWaitMs = 10_000;

    /// <summary>How long the op waits for the client to get past its loading screen.</summary>
    internal const int LoadingWaitMs = 10_000;

    /// <summary>Frames the game's own exit gets once the character exists, before the op makes the call.</summary>
    internal const int GameExitGraceFrames = 30;

    /// <summary>
    /// Frames between scene searches (<c>FindObjectsOfType</c> can take milliseconds in a loaded
    /// client); the last count stands in between, and a search follows each of the op's own calls.
    /// </summary>
    internal const int SceneScanFrames = 10;

    /// <summary>Whether this poll searches the scene: the first one, every <see cref="SceneScanFrames"/>, and right after a call.</summary>
    internal static bool ShouldScanScenes(int poll, bool afterCall) => afterCall || poll % SceneScanFrames == 0;

    /// <summary>Frames with the character, past loading, off the creation menu and without a creation scene, that count as done.</summary>
    internal const int StableFrames = 5;

    /// <summary>Frames after the op's own call before it reports that the creation scene stayed.</summary>
    internal const int VerifyFrames = 120;

    /// <summary>
    /// Hard bound on the whole deferred op. The bridge adds 5 s of slack, and the result must still
    /// beat <c>tools/mcp/call.mjs</c>'s default 30 s timeout.
    /// </summary>
    internal const int WaitMs = SpawnWaitMs + LoadingWaitMs + 2_000;

    internal const string StuckNote =
        "the client stayed on the character-creation scene; disconnect and reconnect it to load into the character";

    internal const string LoadingNote =
        "the character exists but the client is still on its loading screen; once it is in the game, run create_character again to clear a leftover creation scene";

    internal const string NotSpawnedNote =
        "sent, but no character arrived yet (a send during loading can be dropped); run create_character again";

    /// <summary>
    /// One read of the client: does it have a <c>LocalCharacter</c>, the current HUD menu and view
    /// (enum names; empty when unreadable), and how many creation scenes (<c>CustomizationBrowser</c>
    /// objects outside the icon generator) exist.
    /// </summary>
    internal readonly record struct State(bool HasCharacter, string HudMenu, string View, int CreationScenes)
    {
        /// <summary>True while the HUD is still on the character-creation menu.</summary>
        public bool OnCreationMenu => string.Equals(HudMenu, CreationMenu, StringComparison.Ordinal);

        /// <summary>True while the client shows a loading screen.</summary>
        public bool Loading => Array.IndexOf(LoadingViews, View) >= 0;
    }

    internal enum Step
    {
        /// <summary>No character: send the creation screen's <c>CreateCharacterEvent</c>.</summary>
        Send,
        /// <summary>Keep reading on later frames.</summary>
        Wait,
        /// <summary>Make the game's exit call: <c>HUDMenuManager.GoToMenu(world, WakeUpMenu)</c>.</summary>
        Leave,
        /// <summary>Destroy the creation scene that outlived its menu, as <c>DestroyBrowser</c> does.</summary>
        Clear,
        /// <summary>The character exists and the client plays without the creation scene.</summary>
        Done,
        /// <summary>The server did not spawn the character within <see cref="SpawnWaitMs"/>.</summary>
        NotSpawned,
        /// <summary>The character exists, but the client stayed on its loading screen for <see cref="LoadingWaitMs"/>.</summary>
        StillLoading,
        /// <summary>The op made its call and the creation menu or scene stayed.</summary>
        Stuck,
    }

    /// <summary>The first decision, from the read before the op does anything.</summary>
    internal static Step Start(State s) => s.HasCharacter ? Step.Wait : Step.Send;

    /// <summary>The reply's <c>result</c> for a terminal step.</summary>
    internal static string ResultName(Step step) => step switch
    {
        Step.Done => "ready",
        Step.NotSpawned => "not_spawned",
        Step.StillLoading => "still_loading",
        _ => "stuck",
    };

    /// <summary>The reply's <c>note</c> for a terminal step; null when the client is ready.</summary>
    internal static string? Note(Step step) => step switch
    {
        Step.Done => null,
        Step.NotSpawned => NotSpawnedNote,
        Step.StillLoading => LoadingNote,
        _ => StuckNote,
    };

    /// <summary>The reply's <c>handler</c>: which of the game's calls the op made.</summary>
    internal static string Handler(Tracker t) =>
        t.LeaveIssued && t.ClearIssued ? "HUDMenuManager.GoToMenu(WakeUpMenu)+Object.Destroy(creation scene)"
        : t.LeaveIssued ? "HUDMenuManager.GoToMenu(WakeUpMenu)"
        : t.ClearIssued ? "Object.Destroy(creation scene)"
        : "none";

    /// <summary>
    /// Per-frame bookkeeping after the op started. <see cref="Next"/> returns <see cref="Step.Leave"/>
    /// and <see cref="Step.Clear"/> at most once each; the caller makes the call and keeps polling.
    /// A terminal step repeats once reached.
    /// </summary>
    internal sealed class Tracker
    {
        private int _creationMenuFrames, _sceneFrames, _cleanFrames, _sinceCall;
        private long _loadingSinceMs = -1;
        private Step? _final;

        /// <summary>True once the op has called <c>GoToMenu(WakeUpMenu)</c>.</summary>
        internal bool LeaveIssued { get; private set; }

        /// <summary>True once the op has destroyed a leftover creation scene.</summary>
        internal bool ClearIssued { get; private set; }

        /// <summary>True when the client was seen on the creation menu or with a creation scene after its character existed.</summary>
        internal bool SawCreation { get; private set; }

        internal int Frames { get; private set; }

        internal Step Next(State s, long elapsedMs)
        {
            if (_final is Step done) return done;
            Frames++;
            if (elapsedMs >= WaitMs)
                return Finish(!s.HasCharacter ? Step.NotSpawned : s.Loading ? Step.StillLoading : Step.Stuck);
            if (!s.HasCharacter)
            {
                Reset();
                return elapsedMs >= SpawnWaitMs ? Finish(Step.NotSpawned) : Step.Wait;
            }
            if (s.Loading)
            {
                // The creation scene belongs to the loading screen here; nothing to clean yet.
                Reset();
                if (_loadingSinceMs < 0) _loadingSinceMs = elapsedMs;
                return elapsedMs - _loadingSinceMs >= LoadingWaitMs ? Finish(Step.StillLoading) : Step.Wait;
            }
            _loadingSinceMs = -1;
            if (LeaveIssued || ClearIssued) _sinceCall++;

            if (s.OnCreationMenu)
            {
                SawCreation = true;
                _sceneFrames = _cleanFrames = 0;
                if (!LeaveIssued && ++_creationMenuFrames >= GameExitGraceFrames)
                {
                    LeaveIssued = true;
                    _sinceCall = 0;
                    return Step.Leave;
                }
                return CheckStuck();
            }
            _creationMenuFrames = 0;

            if (s.CreationScenes > 0)
            {
                SawCreation = true;
                _cleanFrames = 0;
                if (!ClearIssued && ++_sceneFrames >= GameExitGraceFrames)
                {
                    ClearIssued = true;
                    _sinceCall = 0;
                    return Step.Clear;
                }
                return CheckStuck();
            }
            _sceneFrames = 0;
            return ++_cleanFrames >= StableFrames ? Finish(Step.Done) : Step.Wait;
        }

        private Step CheckStuck() =>
            (LeaveIssued || ClearIssued) && _sinceCall >= VerifyFrames ? Finish(Step.Stuck) : Step.Wait;

        private void Reset() => _creationMenuFrames = _sceneFrames = _cleanFrames = 0;

        private Step Finish(Step step)
        {
            _final = step;
            return step;
        }
    }
}
