#if !PROGENITOR_TESTS
using System;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;

namespace Progenitor.Ui;

/// <summary>
/// The client-only half of the keybind and settings modules: the real key source, the Core-owned
/// cfg that holds the settings panel's own opener key, and the sweep that finds every chainloader
/// plugin's config file so the panel can list it.
///
/// <para>
/// Split out of <see cref="Keybinds"/> and <see cref="SettingsCatalog"/> on purpose: those two are
/// compiled into the test project and must not name a game, Unity or BepInEx-loader type. This
/// file is the only one in the pair that does.
/// </para>
/// </summary>
internal static class UiKeybindHost
{
    /// <summary>
    /// Core's own cfg. Not <c>ConfigHelper.SharedFileName</c>: that file is Bootstrap's,
    /// process-lifetime, and a Core generation must not add rows to it. This one is created and
    /// dropped with the generation, and holds only <c>string</c> values.
    /// </summary>
    public const string CoreConfigFileName = "progenitor.ui.cfg";

    private static readonly object Gate = new();
    private static ConfigFile? _config;
    private static IKeybindHandle? _openBind;
    private static IKeybindHandle[] _navBinds = Array.Empty<IKeybindHandle>();

    /// <summary>Core's own cfg, for other Core modules' keybinds (the input release key); null before install or if it could not open.</summary>
    internal static ConfigFile? CoreConfig
    {
        get
        {
            lock (Gate)
                return _config;
        }
    }

    internal static void Install()
    {
        Keybinds.Use(new UnityKeyInputSource());
        // KEYS7: the game's full-screen menu joins the Mods workspace's back/forward history.
        GameMenuHistory.Probe = new FullscreenMenuProbe();
        SettingsCatalog.SourceSweep = Sweep;
        // KEYS1: the winner rule's live inputs — the game's own keys and install times.
        KeybindArbiter.GameSource = GameKeybindReader.Read;
        // PAD1: the game's controller bindings, for the controller chords' conflicts.
        KeybindArbiter.GameGamepadSource = GameKeybindReader.ReadGamepad;
        // KEYS9: the Keybinds tab changes the game's own keys through the game's input settings.
        GameKeyRemap.Writer = GameKeybindWriter.Write;
        KeybindArbiter.InstallTimeOf = KeybindInstallTimes.Of;
        // KEYS3: the Keybinds tab borrows the game's Bindings Menu look when it is built.
        KeybindArtLadder.PrepareHook = KeybindArtDonors.Prepare;
        // MENUKIT2: the Mods settings page borrows the game's Options page controls.
        GameArt.SettingsArtLadder.PrepareHook = GameArt.SettingsArtDonors.Prepare;
        // MENUKIT2 round C: the art is prepared on idle frames once a scene settles, never on the open path.
        GameArt.SettingsArtDonors.StartIdle();

        ConfigFile? file = null;
        try
        {
            file = new ConfigFile(Path.Combine(Paths.ConfigPath, CoreConfigFileName), saveOnInit: true);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning(
                "Ui: could not open " + CoreConfigFileName + " (" + e.Message
                + "); the settings panel keeps its default key " + SettingsPanel.DefaultOpenKey
                + " and cannot be rebound this session.");
        }

        lock (Gate)
            _config = file;

        if (file == null) return;

        try
        {
            SettingsCatalog.Register("progenitor", file);
            BindForeignEditPolicy(file);
            BindCaptureMode(file);
            BindEnforceGameWins(file);
            BindMenuHistoryInnerTabs(file);
            BindClosedDrawers(file);
            BindHelp(file);
            BindServerBrowser(file);
            var handle = Keybinds.Register(
                owner: typeof(UiKeybindHost),
                pluginId: SettingsPanel.PluginId,
                id: SettingsPanel.OpenKeybindId,
                file: file,
                defaultKey: SettingsPanel.DefaultOpenKey,
                description: "Open or close the Progenitor Mods menu.",
                onPressed: PressedOpen,
                // The panel it opens captures input; without this the key could open the panel
                // and never close it again.
                allowWhileCaptured: true,
                // R35 finding 5. ChatFocus.Unknown means "no chat window exists in this scene",
                // which is the main menu and the loading screen — exactly where a player kicked by
                // a server whose mods they declined needs Server mods to undo the install. Toggle
                // opens a local panel and types text nowhere, so it is safe under the one state in
                // which a chat box would be invisible to us. A *focused* chat window still
                // suppresses it, and so does any focused text field — R36 finding 5 added
                // IKeyInputSource.TextFieldFocused precisely because this scene's name, address
                // and password boxes are invisible to the chat probe. DefaultOpenKey is a function
                // key, so the printable-chord refusal never fires on the default; it exists for the
                // player who rebinds this to a letter.
                allowWhenChatUnknown: true);

            lock (Gate)
                _openBind = handle;

            // KEYS4: back and forward in the Mods workspace (menu scope; the mouse's back and forward buttons).
            var nav = ModsMenu.RegisterNavigationKeys(typeof(UiKeybindHost), file);
            lock (Gate)
                _navBinds = nav;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: settings panel keybind registration threw: " + e.Message);
        }
    }

    private static void PressedOpen()
    {
        KeyChord chord;
        lock (Gate)
            chord = _openBind?.Chord ?? SettingsPanel.DefaultOpenKey;
        ModsMenu.Toggle(ModsMenu.ReasonShortcutFor(chord));
    }

    /// <summary>
    /// The operator's opt-in for editing other mods' settings from the panel. Default false: see
    /// <see cref="SettingsCatalog.AllowForeignEdits"/> for why a third-party mod's server-synced
    /// value must not be written into its cfg by us.
    ///
    /// <para>Settings organization stage 3: live. It is a row on the Configuration tab, and a change applies at once
    /// (<see cref="CoreUiSettings.ApplyForeignEditPolicy"/> redraws an open mod page). The handler is on Core's own
    /// file, which goes with the generation, like <see cref="BindCaptureMode"/>'s.</para>
    /// </summary>
    private static void BindForeignEditPolicy(ConfigFile file)
    {
        try
        {
            var entry = file.Bind(
                CoreUiSettings.EditOtherModsSection,
                CoreUiSettings.EditOtherModsKey,
                false,
                CoreUiSettings.EditOtherModsDescription);
            SettingsCatalog.AllowForeignEdits = entry.Value;
            CoreUiSettings.EditOtherModsSettings = entry;
            entry.SettingChanged += (_, _) =>
            {
                try { CoreUiSettings.ApplyForeignEditPolicy(entry.Value, "changed"); }
                catch { /* tearing down */ }
            };
        }
        catch (Exception e)
        {
            SettingsCatalog.AllowForeignEdits = false;
            Plugin.Logger?.LogWarning(
                "Ui: could not read EditOtherModsSettings (" + e.Message
                + "); other mods' settings stay read-only in the panel.");
        }
    }

    /// <summary>
    /// The framework-wide capture-mode default (job FUI19), moved by the player rather than by a
    /// plugin.
    ///
    /// <para>
    /// <b>Client-only and never synced, on purpose.</b> Progenitor's config sync makes the server
    /// the owner of a synced entry's value, and this entry decides how a player's own mouse
    /// behaves on their own screen — no server has any business overriding it. It therefore lives
    /// in Core's client cfg alongside <c>EditOtherModsSettings</c>, in a method only ever reached
    /// from <c>Ui.Install</c> on a client, and it is a plain <c>ConfigEntry</c> rather than a
    /// synced one.
    /// </para>
    ///
    /// <para>
    /// <b>It is re-read on every change, and that is safe for a different reason than the one this
    /// paragraph used to give</b> (job FUI23, R44 finding 7). The old text said the entry was read
    /// "once, at install", four lines above the <c>SettingChanged</c> subscription below that
    /// re-reads it and moves the framework default mid-session. The conclusion it drew was right —
    /// a panel the player already has open cannot be reached into — but the reason is
    /// <c>UiPanelHandle._shownMode</c>, which is frozen at <c>Show</c> and used at <c>Hide</c>, not
    /// the number of times this entry is read. Someone trusting "read once" and adding a
    /// mid-session re-read would have added a second one.
    /// </para>
    ///
    /// <para>
    /// The handler is never unsubscribed and does not need to be: the <c>ConfigFile</c> is
    /// Core-owned (created in <c>Install</c>, dropped in <c>Uninstall</c>), the lambda is rooted
    /// only by that file's own entry, and nothing outside the Core generation holds either — so the
    /// whole graph is collectible under the ALC rules. Stated because it is the obvious suspicion
    /// about a never-removed event handler in Core and it is the wrong one here.
    /// </para>
    ///
    /// <para>
    /// A value the enum does not name keeps the default and says which value it was, rather than
    /// falling back silently — the same rule every other unreadable setting in this module follows.
    /// </para>
    /// </summary>
    private static void BindCaptureMode(ConfigFile file)
    {
        try
        {
            // KEYS5: a finite list, so the settings page draws a dropdown, not a text field.
            var entry = file.Bind(
                UiCaptureModes.ConfigSection,
                UiCaptureModes.ConfigKey,
                UiCaptureModes.DefaultName,
                new ConfigDescription(UiCaptureModes.ConfigDescription, new AcceptableValueList<string>(UiCaptureModes.Names)));

            if (UiCaptureModes.TryParse(entry.Value, out var mode))
            {
                Ui.CaptureInput(mode);
                entry.SettingChanged += (_, _) =>
                {
                    try
                    {
                        if (UiCaptureModes.TryParse(entry.Value, out var changed))
                            Ui.CaptureInput(changed);
                        else
                            Plugin.Logger?.LogWarning(UiCaptureModes.BadValueLog(entry.Value));
                    }
                    catch { /* tearing down */ }
                };
            }
            else
            {
                Plugin.Logger?.LogWarning(UiCaptureModes.BadValueLog(entry.Value));
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning(
                "Ui: could not read " + UiCaptureModes.ConfigSection + "."
                + UiCaptureModes.ConfigKey + " (" + e.Message + "); panels keep the framework "
                + "default " + UiCaptureModes.DefaultName + ".");
        }
    }

    /// <summary>
    /// KEYS1 review 2026-09-29: <c>[Keybinds] EnforceGameWins</c> (default true). On, an always-active
    /// game key stops a mod key on the same chord; off, every game clash is a warning only. Mode-only game
    /// keys (building, the emote and shapeshift slots) never stop a mod key either way until they are
    /// checked live. Re-read on every change.
    /// </summary>
    /// <summary>
    /// KEYS7: <c>[Ui] MenuHistoryInnerTabs</c> (default false). The game menu's inner tabs in the back/forward
    /// history, through calls not yet checked live (<see cref="SpellbookSchoolTabs"/>). Re-read on change.
    /// </summary>
    private static void BindClosedDrawers(ConfigFile file)
    {
        // Three homes: which settings drawers are closed. Read on every use, so nothing is cached across a Core reload.
        try { CoreUiSettings.ClosedDrawers = file.Bind(SettingsDrawers.Section, SettingsDrawers.Key, "", SettingsDrawers.Description); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: [Ui] " + SettingsDrawers.Key + " could not be bound (" + e.Message + "); drawers are remembered for this session only."); }
    }

    /// <summary>
    /// P7b help: the term markers' hidden set, hover counts and auto-hide count, and the tip cards' seen set
    /// (<see cref="UiHelpSettings"/>). Read on every use through <see cref="UiHelpStore"/>, so nothing is cached across a
    /// Core reload; unbound, they are remembered for this session only.
    /// </summary>
    private static void BindHelp(ConfigFile file)
    {
        try
        {
            CoreUiSettings.HiddenTermMarkers = file.Bind(UiHelpSettings.Section, UiHelpSettings.HiddenTermsKey, "", UiHelpSettings.HiddenTermsDescription);
            CoreUiSettings.TermMarkerHovers = file.Bind(UiHelpSettings.Section, UiHelpSettings.TermHoversKey, "", UiHelpSettings.TermHoversDescription);
            CoreUiSettings.TermMarkerAutoHide = file.Bind(UiHelpSettings.Section, UiHelpSettings.AutoHideKey, UiHelpSettings.DefaultAutoHide,
                new ConfigDescription(UiHelpSettings.AutoHideDescription, new AcceptableValueRange<int>(0, UiHelpSettings.MaxAutoHide)));
            CoreUiSettings.SeenTips = file.Bind(UiHelpSettings.Section, UiHelpSettings.SeenTipsKey, "", UiHelpSettings.SeenTipsDescription);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: [Ui] term marker and tip values could not be bound (" + e.Message
                + "); hidden markers and seen tips are remembered for this session only.");
        }
    }

    private static void BindServerBrowser(ConfigFile file)
    {
        // BROWSERFILTER1: read on every frame the Join Game browser is open, so nothing is cached across a Core reload.
        try
        {
            CoreUiSettings.ServerBrowserProgenitorOnly = file.Bind(CoreUiSettings.ServerBrowserSection,
                CoreUiSettings.ServerBrowserProgenitorOnlyKey, false, CoreUiSettings.ServerBrowserProgenitorOnlyDescription);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: [" + CoreUiSettings.ServerBrowserSection + "] " + CoreUiSettings.ServerBrowserProgenitorOnlyKey
                + " could not be bound (" + e.Message + "); the Progenitor servers filter is remembered for this session only.");
        }
    }

    private static void BindMenuHistoryInnerTabs(ConfigFile file)
    {
        try
        {
            var entry = file.Bind(
                GameMenuHistory.ConfigSection,
                GameMenuHistory.InnerTabsConfigKey,
                true,
                GameMenuHistory.InnerTabsConfigDescription);
            GameMenuHistory.InnerTabsEnabled = entry.Value;
            CoreUiSettings.MenuHistoryInnerTabs = entry;
            entry.SettingChanged += (_, _) =>
            {
                try { GameMenuHistory.InnerTabsEnabled = entry.Value; }
                catch { /* tearing down */ }
            };
        }
        catch (Exception e)
        {
            GameMenuHistory.InnerTabsEnabled = false;
            Plugin.Logger?.LogWarning("Ui: [Ui] " + GameMenuHistory.InnerTabsConfigKey + " could not be bound (" + e.Message + "); inner tabs stay off.");
        }
    }

    private static void BindEnforceGameWins(ConfigFile file)
    {
        try
        {
            var entry = file.Bind(
                Keybinds.Section,
                "EnforceGameWins",
                true,
                "When a mod key uses the same key as one of the game's always-active gameplay keys, the game "
                + "keeps it and the mod key does not fire. Off: both fire and the Keybinds tab shows a warning. "
                + "Keys the game uses only in one mode (building, the emote and shapeshift menus) never stop a mod key.");
            KeybindArbiter.EnforceGameWins = entry.Value;
            entry.SettingChanged += (_, _) =>
            {
                try { KeybindArbiter.EnforceGameWins = entry.Value; }
                catch { /* tearing down */ }
            };
        }
        catch (Exception e)
        {
            KeybindArbiter.EnforceGameWins = true;
            Plugin.Logger?.LogWarning("Ui: could not read [Keybinds] EnforceGameWins (" + e.Message + "); the game's keys win.");
        }
    }

    internal static void Uninstall()
    {
        GameMenuHistory.Probe = NullGameMenuProbe.Instance;
        CoreUiSettings.Reset();
        KeybindArbiter.EnforceGameWins = true;
        KeybindArbiter.GameSource = null;
        KeybindArbiter.GameGamepadSource = null;
        GameKeyRemap.Writer = null;
        KeybindArbiter.InstallTimeOf = null;
        GameKeybindReader.Forget();
        KeybindInstallTimes.Forget();
        KeybindArtLadder.PrepareHook = null;
        try { KeybindArtDonors.Release(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: keybinds art release threw: " + e.Message); }
        GameArt.SettingsArtLadder.PrepareHook = null;
        GameArt.SettingsArtDonors.StopIdle();
        try { GameArt.SettingsArtDonors.Release(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: settings art release threw: " + e.Message); }
        IKeybindHandle? bind;
        IKeybindHandle[] nav;
        lock (Gate)
        {
            bind = _openBind;
            _openBind = null;
            nav = _navBinds;
            _navBinds = Array.Empty<IKeybindHandle>();
            _config = null;
        }

        try { bind?.Dispose(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: settings keybind dispose threw: " + e.Message); }
        foreach (var n in nav)
        {
            try { n.Dispose(); }
            catch (Exception e) { Plugin.Logger?.LogWarning("Ui: navigation keybind dispose threw: " + e.Message); }
        }
    }

    /// <summary>
    /// Add every chainloader plugin's <c>ConfigFile</c> as a settings source. Progenitor plugins
    /// loaded through the reloader are not in this list — those register themselves the first time
    /// they touch <c>ctx.Keybinds</c> — so the two together are the panel's coverage, and the
    /// panel's log line reports the source count so a missing plugin is visible rather than silent.
    /// </summary>
    private static void Sweep()
    {
        var loader = IL2CPPChainloader.Instance;
        if (loader?.Plugins == null) return;
        foreach (var kv in loader.Plugins)
        {
            var info = kv.Value;
            var meta = info?.Metadata;
            var instance = info?.Instance as BasePlugin;
            if (meta == null || instance == null) continue;
            string id = meta.GUID ?? "";
            if (id.Length == 0) continue;
            // foreign: true - another BepInEx mod's own cfg. Shown, not edited (R34 panel note 2).
            try { SettingsCatalog.Register(id, instance.Config, instance, foreign: true); }
            catch (Exception e)
            {
                Plugin.Logger?.LogWarning("Ui: settings source '" + id + "' threw: " + e.Message);
            }
        }
    }
}
#endif
