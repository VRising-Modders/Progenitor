using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Progenitor.Ui;

/// <summary>Where a keybind comes from (KEYS1, docs/mods-menu.md#keybinds-tab).</summary>
internal enum KeybindSource
{
    /// <summary>One of the game's own actions. Read-only; the game always counts as installed first.</summary>
    Game = 0,

    /// <summary>A Progenitor mod's bind in the <see cref="Keybinds"/> registry. The winner rule is enforced.</summary>
    Progenitor = 1,

    /// <summary>An ordinary BepInEx mod's <c>KeyboardShortcut</c> / <c>KeyCode</c> config entry. Reported, never enforced.</summary>
    Cfg = 2,
}

/// <summary>
/// KEYS4: where a key works (the matrix is in docs/mods-menu.md, "Where keys meet"). A <see cref="Menu"/> key fires only
/// while the workspace is open, so it never meets a gameplay key: it clashes only with other menu keys
/// and with the game's menu keys (<see cref="GameKeybindCatalog.InMenus"/>: Back, Enter, Select
/// Progression). A <see cref="Photo"/> key (photo mode's fly-camera controls) fires only while photo
/// mode is active, when photo mode holds the character's input, so it never meets a game key or a key
/// of another scope: it clashes only with other photo keys. A <see cref="Map"/> key (a plugin's
/// <c>ctx.Keybinds.RegisterInMap</c>) fires only while the game's world map is open (the full-screen
/// menu on its Map tab), so it never meets a gameplay key. It meets what is live there too: other map
/// keys, menu keys (the full-screen menu counts as a menu), the game's map keys
/// (<see cref="GameKeybindCatalog.InTheMap"/>: Map Pan, Map Marker, Center Player, Toggle Legend, Map Zoom)
/// and menu keys, which stop it on an exact chord, and the game's keys that work on every screen
/// (<see cref="KeybindArbiter.EveryScreenGameActions"/>: Push To Talk), which are reported but do not stop
/// it. Everything else, the game's keys and every other mod key, is <see cref="Gameplay"/>.
/// </summary>
internal enum KeybindScope
{
    Gameplay = 0,
    Menu = 1,
    Photo = 2,
    Map = 3,

    /// <summary>
    /// CHAT2: works only while the player is typing in the game's chat box (the chat has focus), and is
    /// the only kind of bind that runs then. Core-internal: the name completion key.
    /// </summary>
    Chat = 4,

    /// <summary>
    /// HUB2: a menu key that also steps a window's tab history outside menus, but only while that window holds the input
    /// (the game's input is blocked then, so no gameplay action can fire on the same press). Against the game's keys it
    /// clashes as a <see cref="Menu"/> key does (the game's menu keys stop it; its gameplay actions never meet it). It is
    /// never in a clash with another mod's key: every mod key on its chord keeps it, in every scope, and the window key
    /// gives way at press time whenever that key can act. Core only: Navigate back and forward (the mouse's back and
    /// forward buttons) and the Alt+Left / Alt+Right tab-history keys.
    /// </summary>
    Window = 5,
}

/// <summary>
/// One key a player can press for something: a game action's primary or secondary key, a
/// Progenitor bind, or an ordinary mod's key setting. Plain data, game-free.
/// </summary>
internal sealed class KeybindClaim
{
    internal KeybindClaim(
        KeybindSource source, string ownerId, string actionId, string displayName, KeyChord chord,
        string chordText = "", string help = "", KeyChord? defaultChord = null, string defaultText = "",
        bool comparable = true, int slot = 0, string category = "", bool editable = false,
        string notEditableReason = "", object? entry = null, bool keyCodeOnly = false, string mode = "",
        KeybindScope scope = KeybindScope.Gameplay, bool gameChanged = false, bool gameSlotExists = true)
    {
        Scope = scope;
        GameChanged = gameChanged;
        GameSlotExists = gameSlotExists;
        Mode = (mode ?? "").Trim();
        Source = source;
        OwnerId = (ownerId ?? "").Trim();
        ActionId = (actionId ?? "").Trim();
        DisplayName = string.IsNullOrWhiteSpace(displayName) ? ActionId : displayName.Trim();
        Chord = chord;
        ChordText = string.IsNullOrWhiteSpace(chordText) ? (chord.IsBound ? chord.DisplayText : "None") : chordText.Trim();
        Help = help ?? "";
        DefaultChord = defaultChord;
        DefaultText = defaultText ?? "";
        Comparable = comparable && chord.IsBound;
        Slot = slot;
        Category = category ?? "";
        Editable = editable;
        NotEditableReason = notEditableReason ?? "";
        Entry = entry;
        KeyCodeOnly = keyCodeOnly;
    }

    internal KeybindSource Source { get; }

    /// <summary>KEYS4: where the key works (<see cref="KeybindScope"/>); a game key is always gameplay.</summary>
    internal KeybindScope Scope { get; }

    /// <summary>
    /// A game key that works only in one mode (<c>while building</c>, <c>in the emote menu</c>), or empty
    /// for an always-active gameplay key. Review 2026-09-29: until checked live, a mode-only game key
    /// never suppresses a mod key; the clash is shown as a warning only.
    /// </summary>
    internal string Mode { get; }

    /// <summary>The plugin GUID, or <see cref="KeybindArbiter.GameOwnerId"/>.</summary>
    internal string OwnerId { get; }

    /// <summary>The bind id, the cfg <c>Section/Key</c>, or the game action's enum name.</summary>
    internal string ActionId { get; }

    internal string DisplayName { get; }

    /// <summary>The chord compared for clashes. <see cref="KeyChord.None"/> when unbound or not a keyboard key.</summary>
    internal KeyChord Chord { get; }

    /// <summary>What is drawn in the key cell (the game's own text for a game key, the cfg's for a mouse button).</summary>
    internal string ChordText { get; }

    internal string Help { get; }

    /// <summary>
    /// KEYS9: a game key whose slot differs from the game's own default (the player changed it, here or in the
    /// game's Controls page). Read from the binding's path with and without the player's overrides.
    /// </summary>
    internal bool GameChanged { get; }

    /// <summary>KEYS9 review: a game slot the game has a keyboard binding for (false: an action with no second key slot).</summary>
    internal bool GameSlotExists { get; }

    /// <summary>The default for a mod key; null for a game key (its default is <see cref="DefaultText"/>, the game's words).</summary>
    internal KeyChord? DefaultChord { get; }

    /// <summary>The default as the owner writes it (a cfg's own spelling); a game key's default key text (KEYS9), or empty.</summary>
    internal string DefaultText { get; }

    /// <summary>False when the key could not be read as a keyboard chord (a mouse button, an unknown name): it never clashes.</summary>
    internal bool Comparable { get; }

    /// <summary>A game key: 0 primary, 1 secondary. Mod keys: 0.</summary>
    internal int Slot { get; }

    /// <summary>A game key's group on the game's own Controls page.</summary>
    internal string Category { get; }

    /// <summary>A cfg key: can this tab write it (the catalog's rules: not server-synced, other mods' edits allowed).</summary>
    internal bool Editable { get; }

    internal string NotEditableReason { get; }

    /// <summary>A cfg key's <c>ConfigEntryBase</c>; null otherwise. Held as <c>object</c> so this type stays BepInEx-free.</summary>
    internal object? Entry { get; }

    /// <summary>A cfg <c>KeyCode</c> entry: one key, no modifiers.</summary>
    internal bool KeyCodeOnly { get; }

    /// <summary>Stable identity: <c>source:owner/action</c>, plus <c>#2</c> for a game secondary.</summary>
    internal string Key =>
        NameOf(Source) + ":" + OwnerId + "/" + ActionId + (Slot > 0 ? "#" + (Slot + 1).ToString(CultureInfo.InvariantCulture) : "");

    internal bool Modified
    {
        get
        {
            if (Source == KeybindSource.Game) return GameChanged;
            if (DefaultChord is { } d && (Comparable || !Chord.IsBound)) return d != Chord;
            return DefaultText.Length > 0 && !string.Equals(DefaultText, ChordText, StringComparison.OrdinalIgnoreCase);
        }
    }

    internal static string NameOf(KeybindSource source) => source switch
    {
        KeybindSource.Game => "game",
        KeybindSource.Cfg => "cfg",
        _ => "progenitor",
    };
}

/// <summary>A mod's install time for the winner rule, and where it was read.</summary>
internal readonly struct ModInstallTime
{
    internal ModInstallTime(DateTime? utc, string source)
    {
        Utc = utc;
        Source = utc == null ? KeybindArbiter.InstallUnknown : (source ?? KeybindArbiter.InstallUnknown);
    }

    internal DateTime? Utc { get; }

    /// <summary><see cref="KeybindArbiter.InstallLedger"/>, <see cref="KeybindArbiter.InstallFile"/> or <see cref="KeybindArbiter.InstallUnknown"/>.</summary>
    internal string Source { get; }

    internal static ModInstallTime Unknown => new(null, KeybindArbiter.InstallUnknown);
}

/// <summary>What the winner rule decided for one claim.</summary>
internal sealed class KeybindVerdict
{
    internal KeybindVerdict(KeybindClaim claim, bool active, KeybindClaim? winner, string rule, IReadOnlyList<KeybindClaim> others, string reason)
    {
        Claim = claim;
        Active = active;
        Winner = winner;
        Rule = rule ?? "";
        Others = others ?? Array.Empty<KeybindClaim>();
        Reason = reason ?? "";
    }

    internal KeybindClaim Claim { get; }

    /// <summary>
    /// False when another claim wins this chord. For a Progenitor bind that means its handler does
    /// not run; for a cfg key it only means Progenitor reports it (it cannot stop another mod).
    /// </summary>
    internal bool Active { get; }

    /// <summary>The claim that beats this one; null while this one is active.</summary>
    internal KeybindClaim? Winner { get; }

    /// <summary><see cref="KeybindArbiter.RuleGame"/>, <see cref="KeybindArbiter.RuleInstall"/>, <see cref="KeybindArbiter.RuleLoadOrder"/>, or empty.</summary>
    internal string Rule { get; }

    /// <summary>Every other owner's claim on the same chord (empty when there is no clash).</summary>
    internal IReadOnlyList<KeybindClaim> Others { get; }

    /// <summary>The row's sentence: why it is not active, or who else uses the key. Empty without a clash.</summary>
    internal string Reason { get; }

    internal bool InConflict => Others.Count > 0;

    /// <summary>A Progenitor bind that lost: the one case the framework enforces.</summary>
    internal bool Enforced => !Active && Claim.Source == KeybindSource.Progenitor;
}

/// <summary>The whole picture: every claim and its verdict.</summary>
internal sealed class KeybindPicture
{
    private readonly Dictionary<string, KeybindVerdict> _byKey;

    internal KeybindPicture(IReadOnlyList<KeybindVerdict> verdicts, bool gameAvailable, string gameNote)
    {
        Verdicts = verdicts ?? Array.Empty<KeybindVerdict>();
        GameAvailable = gameAvailable;
        GameNote = gameNote ?? "";
        _byKey = new Dictionary<string, KeybindVerdict>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < Verdicts.Count; i++) _byKey[Verdicts[i].Claim.Key] = Verdicts[i];
    }

    internal IReadOnlyList<KeybindVerdict> Verdicts { get; }

    /// <summary>Whether the game's bindings could be read. False: no Game section, and <see cref="GameNote"/> says why.</summary>
    internal bool GameAvailable { get; }

    internal string GameNote { get; }

    internal KeybindVerdict? Find(string key) => _byKey.TryGetValue(key ?? "", out var v) ? v : null;

    internal KeybindVerdict? FindProgenitor(string? pluginId, string? bindId)
    {
        for (int i = 0; i < Verdicts.Count; i++)
        {
            var c = Verdicts[i].Claim;
            if (c.Source != KeybindSource.Progenitor) continue;
            if (!string.Equals(c.ActionId, bindId ?? "", StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.IsNullOrEmpty(pluginId) && !string.Equals(c.OwnerId, pluginId, StringComparison.OrdinalIgnoreCase)) continue;
            return Verdicts[i];
        }

        return null;
    }

    internal int ConflictCount
    {
        get
        {
            int n = 0;
            for (int i = 0; i < Verdicts.Count; i++)
                if (Verdicts[i].Claim.Source != KeybindSource.Game && Verdicts[i].InConflict) n++;
            return n;
        }
    }
}

/// <summary>What the game's bindings reader answered.</summary>
internal sealed class GameKeybindSnapshot
{
    internal GameKeybindSnapshot(bool available, IReadOnlyList<KeybindClaim>? claims, string note)
    {
        Available = available;
        Claims = claims ?? Array.Empty<KeybindClaim>();
        Note = note ?? "";
    }

    internal bool Available { get; }

    /// <summary>Game claims (primary and secondary as separate claims, unbound ones included for display).</summary>
    internal IReadOnlyList<KeybindClaim> Claims { get; }

    /// <summary>Why the bindings are unavailable (shown instead of the Game section), or empty.</summary>
    internal string Note { get; }

    internal static GameKeybindSnapshot Unavailable(string note) => new(false, null, note);
}

/// <summary>
/// KEYS1: who gets a key when more than one thing wants it. Decided 2026-09-29, "first
/// installed":
/// <list type="bullet">
/// <item><description>The game always counts as first: a mod key on a chord a game action is bound to is not active.</description></item>
/// <item><description>Between mods the earlier-installed one wins. Install time is the install ledger's, else the plugin file's creation time.</description></item>
/// <item><description>When that is the same or unknown, the mod that loads first by plugin id (BepInEx's stable order for unrelated plugins) wins, and the sentence says so.</description></item>
/// </list>
/// Only an exact chord clashes (same main key, same modifiers) — the same rule the pump fires by, so
/// a mod on <c>Ctrl + W</c> never clashes with the game's <c>W</c>. Two binds of one mod on one key
/// are not a clash. A Progenitor bind that loses is not fired (<see cref="Keybinds.Pump"/>); an
/// ordinary mod's key is only reported, because Progenitor cannot stop another mod reading its keys.
/// Game-free: the game's bindings, the cfg keys and the install times are supplied.
/// </summary>
internal static class KeybindArbiter
{
    internal const string GameOwnerId = "game";
    internal const string GameName = "the game";

    internal const string RuleGame = "game";
    internal const string RuleInstall = "install";
    internal const string RuleLoadOrder = "load-order";

    /// <summary>The winner is part of Progenitor itself (Core, the Mods-menu opener, the dev bridge).</summary>
    internal const string RuleFramework = "framework";

    /// <summary>The winner's install time is known and the loser's is not.</summary>
    internal const string RuleUnknownTime = "unknown-time";

    /// <summary>
    /// Plugins whose keys rank right after the game's, above every mod: Core (the Mods-menu opener, the
    /// input release key) and the dev bridge. A mod can never take their chord.
    /// </summary>
    internal static readonly string[] FrameworkOwners = { "progenitor", "progenitor.dev" };

    internal static bool IsFramework(string? ownerId)
    {
        foreach (var f in FrameworkOwners)
            if (string.Equals(f, ownerId, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>
    /// <c>[Keybinds] EnforceGameWins</c> in <c>progenitor.ui.cfg</c> (default true): an
    /// always-active game key stops a mod key on the same chord. Off, every game clash is a warning only.
    /// </summary>
    internal static bool EnforceGameWins
    {
        get => _enforceGameWins;
        set { _enforceGameWins = value; Invalidate(); }
    }

    private static bool _enforceGameWins = true;

    /// <summary>True when <paramref name="c"/> is a game key that stops mod keys on its chord.</summary>
    internal static bool GameWins(KeybindClaim c) =>
        c.Source == KeybindSource.Game && EnforceGameWins && c.Mode.Length == 0;

    /// <summary>
    /// KEYS4: true when game key <paramref name="game"/> stops a mod key of <paramref name="scope"/>: an
    /// always-active gameplay key stops a gameplay key (<see cref="GameWins"/>); a game menu key
    /// (<see cref="GameKeybindCatalog.InMenus"/>) stops a menu key, since both act in a menu.
    /// </summary>
    internal static bool GameWins(KeybindClaim game, KeybindScope scope) =>
        scope switch
        {
            KeybindScope.Menu => game.Source == KeybindSource.Game && EnforceGameWins && IsGameMenuKey(game),
            // MAPTP1: a map key acts only in the map, where the game's own map keys and menu keys are live.
            // A key the game reads on every screen (Push To Talk) is reported, and both fire.
            KeybindScope.Map => game.Source == KeybindSource.Game && EnforceGameWins
                && (IsGameMapKey(game) || IsGameMenuKey(game)),
            // Photo keys act only while photo mode holds the character's input: no game key is live then.
            KeybindScope.Photo => false,
            // CHAT2: a chat key shares the game's in-chat keys (Tab cycles the channel) on purpose: both stay
            // live, and the chat key's owner takes the game's action away only while it acts (the name
            // completion puts back the channel the game's own Tab handling cycled).
            KeybindScope.Chat => false,
            // HUB2: a window key clashes like a menu key; outside menus it acts only while the game's input is blocked.
            KeybindScope.Window => game.Source == KeybindSource.Game && EnforceGameWins && IsGameMenuKey(game),
            _ => GameWins(game),
        };

    private static bool IsGameMenuKey(KeybindClaim c) =>
        c.Source == KeybindSource.Game && string.Equals(c.Mode, GameKeybindCatalog.InMenus, StringComparison.Ordinal);

    private static bool IsGameChatKey(KeybindClaim c) =>
        c.Source == KeybindSource.Game && string.Equals(c.Mode, GameKeybindCatalog.InChat, StringComparison.Ordinal);

    private static bool IsGameMapKey(KeybindClaim c) =>
        c.Source == KeybindSource.Game && string.Equals(c.Mode, GameKeybindCatalog.InTheMap, StringComparison.Ordinal);

    /// <summary>
    /// MAPTP1: game actions read on every screen, the full-screen map included (the game's input asset binds
    /// Push To Talk to the middle mouse button by default). A map key on one of their chords is reported as
    /// sharing it; both fire.
    /// </summary>
    internal static readonly string[] EveryScreenGameActions = { "PushToTalk" };

    private static bool IsEveryScreenGameKey(KeybindClaim c)
    {
        if (c.Source != KeybindSource.Game) return false;
        foreach (var a in EveryScreenGameActions)
            if (string.Equals(a, c.ActionId, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>
    /// KEYS4: whether two claims can clash at all. Mod keys meet only in the same scope; a game key meets
    /// every gameplay key, and a menu key only when it is one of the game's menu keys; game keys always
    /// meet each other.
    /// </summary>
    internal static bool ScopesMeet(KeybindClaim a, KeybindClaim b)
    {
        if (a.Source == KeybindSource.Game && b.Source == KeybindSource.Game) return true;
        if (a.Source == KeybindSource.Game) return ScopesMeet(a, b.Scope);
        if (b.Source == KeybindSource.Game) return ScopesMeet(b, a.Scope);
        return ModScopesMeet(a.Scope, b.Scope);
    }

    /// <summary>
    /// Two mod keys meet in the same scope; a map key also meets a menu key, because the map is a tab of the
    /// game's full-screen menu and menu keys are live there too.
    /// </summary>
    private static bool ModScopesMeet(KeybindScope a, KeybindScope b)
    {
        // HUB2: a window key (Core's back and forward) is never in a clash with another mod key: every mod key on its chord
        // keeps it, everywhere, and the window key gives way at press time whenever that key can act (Keybinds.GateNow).
        // Against the game's keys it clashes as a menu key does (ScopesMeet(game, scope)).
        if (a == KeybindScope.Window || b == KeybindScope.Window) return a == b;
        return a == b
            || (a == KeybindScope.Map && b == KeybindScope.Menu)
            || (a == KeybindScope.Menu && b == KeybindScope.Map);
    }

    /// <summary>KEYS4: whether <paramref name="c"/> can clash with a mod key of <paramref name="scope"/>.</summary>
    internal static bool ScopesMeet(KeybindClaim c, KeybindScope scope)
    {
        if (c.Source != KeybindSource.Game) return ModScopesMeet(c.Scope, scope);
        return scope switch
        {
            KeybindScope.Gameplay => true,
            KeybindScope.Menu or KeybindScope.Window => IsGameMenuKey(c),
            KeybindScope.Map => IsGameMapKey(c) || IsGameMenuKey(c) || IsEveryScreenGameKey(c),
            KeybindScope.Chat => IsGameChatKey(c),
            _ => false,
        };
    }

    internal const string InstallLedger = "ledger";
    internal const string InstallFile = "file";
    internal const string InstallUnknown = "unknown";

    internal const string GameUnavailableNote =
        "The game's own keys couldn't be read yet (the game hasn't loaded its controls). Reopen this tab in a moment to see them and to check mod keys against them.";

    /// <summary>The game's bindings; production installs the client reader. Null reads as unavailable.</summary>
    internal static Func<GameKeybindSnapshot>? GameSource
    {
        get => _gameSource;
        set { _gameSource = value; Invalidate(game: true); }
    }

    private static Func<GameKeybindSnapshot>? _gameSource;

    /// <summary>
    /// PAD1: the game's gamepad bindings (<see cref="GamepadKeybindCatalog"/>); production installs the client reader,
    /// which reads the input action map when a gamepad is connected and the shipped defaults otherwise. Null reads as
    /// unavailable: no gamepad chord is then checked against the game.
    /// </summary>
    internal static Func<GameKeybindSnapshot>? GameGamepadSource
    {
        get => _gameGamepadSource;
        set { _gameGamepadSource = value; Invalidate(game: true); }
    }

    private static Func<GameKeybindSnapshot>? _gameGamepadSource;
    private static Func<IReadOnlyList<KeybindClaim>>? _cfgSource = DefaultCfg;
    private static Func<string, ModInstallTime>? _installTimeOf;
    private static Func<string, string>? _nameOf;

    // The picture is rebuilt only when something it was built from changes (review 2026-09-29): a
    // registration, rebind or removal (Keybinds.Version), a cfg key written or a settings source added
    // or removed (SettingsCatalog.KeyVersion / SourceCount), the foreign-edit or game-wins switch, a
    // world entered or left (the game's keys are re-read then), or an explicit Invalidate (the Keybinds
    // tab opening). The pump refreshes it at the top of a frame (RefreshIfStale, a few int compares) and
    // a press only looks its bind up in the blocked table.
    private static readonly object CacheGate = new();
    private static KeybindPicture? _cached;
    private static Stamp _cachedStamp;
    private static bool _dirty = true;
    private static bool _gameStale = true;
    private static GameKeybindSnapshot? _game;
    private static ulong _gameWorld = ulong.MaxValue;
    private static Dictionary<string, string> _blocked = new(StringComparer.OrdinalIgnoreCase);

    // PAD1: the gamepad picture, built with the keyboard one from the same stamp: the game's gamepad bindings and every
    // Progenitor bind's gamepad chord. Gamepad chords never meet keyboard or mouse chords (no button is on both).
    private static KeybindPicture? _cachedGamepad;
    private static GameKeybindSnapshot? _gameGamepad;
    private static Dictionary<string, string> _gamepadBlocked = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>PAD1 review: whether a gamepad was connected when the game's bindings were last read.</summary>
    private static bool _gamePadRead;

    /// <summary>HUB2: window keys (<see cref="KeybindScope.Window"/>) and the other mods' keys on their chord.</summary>
    private static Dictionary<string, List<KeybindClaim>> _windowSharers = new(StringComparer.OrdinalIgnoreCase);

    // PAD1 review: Pad is whether a gamepad is connected; a change re-reads the game's bindings (a connected pad's
    // live controller bindings replace the shipped ones, and the other way round).
    private readonly record struct Stamp(int Binds, int Keys, int Sources, bool Foreign, bool Enforce, int Names, ulong World, bool Pad);

    private static Stamp Now() => new(
        Keybinds.Version, SettingsCatalog.KeyVersion, SettingsCatalog.SourceCount, SettingsCatalog.AllowForeignEdits,
        _enforceGameWins, System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(KeybindConflictSource.NameOf ?? (object)CacheGate),
        WorldSequence(), Keybinds.GamepadConnected);

    private static ulong WorldSequence()
    {
        try { return Progenitor.WorldState.ClientWorldEpoch.ObservedSequence; }
        catch { return 0; }
    }

    /// <summary>
    /// Drop the picture. <paramref name="game"/> also re-reads the game's keys on the next build (the
    /// Keybinds tab opening; the game's own Controls page may have changed them).
    /// </summary>
    internal static void Invalidate(bool game = false)
    {
        lock (CacheGate)
        {
            _dirty = true;
            if (game) _gameStale = true;
        }
    }

    /// <summary>Rebuild now when anything changed. Cheap when nothing did: the pump calls it once a frame.</summary>
    internal static void RefreshIfStale()
    {
        bool stale;
        var now = Now();
        lock (CacheGate) stale = _dirty || _cached == null || now != _cachedStamp;
        if (stale) Current();
    }

    /// <summary>Ordinary mods' key settings; production walks the settings catalog's sources.</summary>
    internal static Func<IReadOnlyList<KeybindClaim>>? CfgSource
    {
        get => _cfgSource;
        set { _cfgSource = value; Invalidate(); }
    }

    private static IReadOnlyList<KeybindClaim> DefaultCfg() => SettingsCatalog.KeyClaims();

    /// <summary>A mod's install time; production reads the install ledger, then the plugin file. Null: unknown.</summary>
    internal static Func<string, ModInstallTime>? InstallTimeOf
    {
        get => _installTimeOf;
        set { _installTimeOf = value; Invalidate(); }
    }

    /// <summary>Plugin id → display name; <see cref="KeybindConflictSource.DisplayName"/> by default.</summary>
    internal static Func<string, string>? NameOf
    {
        get => _nameOf;
        set { _nameOf = value; Invalidate(); }
    }

    internal static void ResetForTests()
    {
        GameKeyRemap.ResetForTests();
        GameSource = null;
        GameGamepadSource = null;
        CfgSource = DefaultCfg;
        InstallTimeOf = null;
        NameOf = null;
        _enforceGameWins = true;
        KeybindGameCache.ResetForTests();
        KeybindRemapGuard.PictureSource = Current;
        lock (CacheGate)
        {
            _cached = null;
            _dirty = true;
            _gameStale = true;
            _game = null;
            _blocked = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _windowSharers = new Dictionary<string, List<KeybindClaim>>(StringComparer.OrdinalIgnoreCase);
            _cachedGamepad = null;
            _gameGamepad = null;
            _gamepadBlocked = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _gamePadRead = false;
        }
    }

    /// <summary>
    /// KEYS4 (found live 2026-09-30: "used by progenitor (Open Mods menu)"): Progenitor's own
    /// plugins by name, wherever a holder is named. Null for any other id.
    /// </summary>
    internal static string? FrameworkName(string? ownerId)
    {
        if (string.Equals(ownerId, FrameworkOwners[0], StringComparison.OrdinalIgnoreCase)) return "Progenitor";
        if (string.Equals(ownerId, FrameworkOwners[1], StringComparison.OrdinalIgnoreCase)) return "Progenitor Dev";
        return null;
    }

    internal static string DisplayNameOf(string ownerId)
    {
        if (string.Equals(ownerId, GameOwnerId, StringComparison.Ordinal)) return GameName;
        if (FrameworkName(ownerId) is { } framework) return framework;
        try
        {
            var f = NameOf;
            if (f != null)
            {
                string n = f(ownerId);
                if (!string.IsNullOrWhiteSpace(n)) return n.Trim();
            }
        }
        catch { /* the id is a fine fallback */ }
        return KeybindConflictSource.DisplayName(ownerId);
    }

    internal static GameKeybindSnapshot ReadGame()
    {
        var f = GameSource;
        if (f == null) return GameKeybindSnapshot.Unavailable(GameUnavailableNote);
        try { return f() ?? GameKeybindSnapshot.Unavailable(GameUnavailableNote); }
        catch (Exception e)
        {
            return GameKeybindSnapshot.Unavailable("The game's keys could not be read (" + e.Message + ").");
        }
    }

    internal static IReadOnlyList<KeybindClaim> ReadCfg()
    {
        var f = CfgSource;
        if (f == null) return Array.Empty<KeybindClaim>();
        try { return f() ?? Array.Empty<KeybindClaim>(); }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: keybinds cfg walk threw: " + e.Message);
            return Array.Empty<KeybindClaim>();
        }
    }

    /// <summary>
    /// A bind's display name: the id in words, except where the id no longer says what the key does
    /// (the cfg key must stay the same). Core's <c>OpenSettingsPanel</c> opens the Mods menu.
    /// </summary>
    internal static string KeybindDisplayName(string? pluginId, string? id)
    {
        if (string.Equals(pluginId, SettingsPanel.PluginId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(id, SettingsPanel.OpenKeybindId, StringComparison.OrdinalIgnoreCase))
            return OpenModsMenuName;
        return ModSettingsLayout.DisplayName(id);
    }

    internal const string OpenModsMenuName = "Open Mods menu";

    internal static IReadOnlyList<KeybindClaim> ReadProgenitor()
    {
        IReadOnlyList<IKeybindInfo> binds;
        try { binds = Keybinds.Snapshot(); }
        catch { return Array.Empty<KeybindClaim>(); }
        var list = new List<KeybindClaim>(binds.Count);
        for (int i = 0; i < binds.Count; i++)
        {
            var b = binds[i];
            if (b is IKeybindHandle { IsDisposed: true }) continue;
            list.Add(new KeybindClaim(
                KeybindSource.Progenitor, b.PluginId, b.Id, KeybindDisplayName(b.PluginId, b.Id), b.Chord,
                help: b.Description, defaultChord: b.DefaultChord, defaultText: b.DefaultChord.DisplayText, editable: true,
                scope: Keybinds.ScopeOf(b)));
        }

        return list;
    }

    /// <summary>PAD1: every live Progenitor bind's gamepad chord as a claim (an unbound one too, for its row).</summary>
    internal static IReadOnlyList<KeybindClaim> ReadProgenitorGamepad()
    {
        IReadOnlyList<IKeybindInfo> binds;
        try { binds = Keybinds.Snapshot(); }
        catch { return Array.Empty<KeybindClaim>(); }
        var list = new List<KeybindClaim>(binds.Count);
        for (int i = 0; i < binds.Count; i++)
        {
            var b = binds[i];
            if (b is IKeybindHandle { IsDisposed: true }) continue;
            list.Add(new KeybindClaim(
                KeybindSource.Progenitor, b.PluginId, b.Id, KeybindDisplayName(b.PluginId, b.Id), b.GamepadChord,
                help: b.Description, defaultChord: b.DefaultGamepadChord, defaultText: b.DefaultGamepadChord.DisplayText,
                editable: true, scope: Keybinds.ScopeOf(b)));
        }

        return list;
    }

    /// <summary>PAD1: the game's gamepad bindings; unavailable when no reader is installed or it throws.</summary>
    internal static GameKeybindSnapshot ReadGameGamepad()
    {
        var f = GameGamepadSource;
        if (f == null) return GameKeybindSnapshot.Unavailable(GamepadUnavailableNote);
        try { return f() ?? GameKeybindSnapshot.Unavailable(GamepadUnavailableNote); }
        catch (Exception e)
        {
            return GameKeybindSnapshot.Unavailable("The game's controller buttons could not be read (" + e.Message + ").");
        }
    }

    internal const string GamepadUnavailableNote = "The game's controller buttons couldn't be read, so controller buttons are not checked against them.";

    /// <summary>
    /// PAD1: the gamepad picture: the game's gamepad bindings and every Progenitor bind's gamepad chord, decided by the
    /// same winner rule and scopes as the keyboard picture (<see cref="Compute"/>). Built with <see cref="Current"/>.
    /// </summary>
    internal static KeybindPicture CurrentGamepad()
    {
        Current();
        lock (CacheGate) return _cachedGamepad ?? new KeybindPicture(Array.Empty<KeybindVerdict>(), false, GamepadUnavailableNote);
    }

    /// <summary>PAD1: the game's gamepad bindings as last read (with the keyboard ones: at world entry, or after <see cref="Invalidate"/>).</summary>
    internal static GameKeybindSnapshot GamepadSnapshot()
    {
        lock (CacheGate)
            if (_gameGamepad != null && !_gameStale) return _gameGamepad;
        Current();
        lock (CacheGate) return _gameGamepad ?? GameKeybindSnapshot.Unavailable(GamepadUnavailableNote);
    }

    /// <summary>PAD1: why a Progenitor bind's gamepad chord must not fire right now, or null. As <see cref="BlockedReason"/>.</summary>
    internal static string? GamepadBlockedReason(string pluginId, string bindId)
    {
        Dictionary<string, string> blocked;
        lock (CacheGate) blocked = _gamepadBlocked;
        return blocked.TryGetValue((pluginId ?? "") + "/" + (bindId ?? ""), out var reason) ? reason : null;
    }

    /// <summary>
    /// PAD1: what in the gamepad picture already has <paramref name="chord"/> where a bind of <paramref name="scope"/>
    /// would meet it (a game binding that stops it, or another Progenitor bind's gamepad chord), or null. The bind
    /// itself (<paramref name="pluginId"/>/<paramref name="bindId"/>) is skipped.
    /// </summary>
    internal static KeybindClaim? GamepadHolderOf(string pluginId, string bindId, KeyChord chord, KeybindScope scope)
    {
        if (!chord.IsBound) return null;
        var picture = CurrentGamepad();
        foreach (var v in picture.Verdicts)
        {
            var c = v.Claim;
            if (!c.Comparable || c.Chord != chord) continue;
            if (c.Source == KeybindSource.Progenitor && string.Equals(c.OwnerId, pluginId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(c.ActionId, bindId, StringComparison.OrdinalIgnoreCase)) continue;
            if (!ScopesMeet(c, scope)) continue;
            // A game binding that only warns in this scope (a mode-only key) does not hold the button.
            if (c.Source == KeybindSource.Game && !GameWins(c, scope)) continue;
            return c;
        }

        return null;
    }

    /// <summary>
    /// The live picture: the game (when readable), every Progenitor bind and every cfg key. Built again only
    /// when something it was built from changed (see <see cref="RefreshIfStale"/>). The built picture holds
    /// no config entry: a cfg key is found again when it is written.
    /// </summary>
    internal static KeybindPicture Current()
    {
        var stamp = Now();
        bool readGame;
        lock (CacheGate)
        {
            if (!_dirty && _cached != null && stamp == _cachedStamp) return _cached;
            readGame = _gameStale || _game == null || stamp.World != _gameWorld || stamp.Pad != _gamePadRead;
        }

        var game = readGame ? GameSnapshot(stamp.World) : _game!;
        var picture = Compute(game.Available ? game.Claims : Array.Empty<KeybindClaim>(), ReadProgenitor(), ReadCfg(),
            game.Available, game.Available ? "" : game.Note);
        var blocked = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var v in picture.Verdicts)
            if (v.Claim.Source == KeybindSource.Progenitor && !v.Active)
                blocked[v.Claim.OwnerId + "/" + v.Claim.ActionId] = v.Reason;
        var windowSharers = WindowSharers(picture);

        // PAD1: the gamepad picture from the same stamp (the game's gamepad bindings are re-read with the keyboard ones).
        GameKeybindSnapshot? padGame;
        lock (CacheGate) padGame = _gameGamepad;
        if (readGame || padGame == null) padGame = ReadGameGamepad();
        var padPicture = Compute(padGame.Available ? padGame.Claims : Array.Empty<KeybindClaim>(), ReadProgenitorGamepad(),
            Array.Empty<KeybindClaim>(), padGame.Available, padGame.Available ? "" : padGame.Note);
        var padBlocked = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var v in padPicture.Verdicts)
            if (v.Claim.Source == KeybindSource.Progenitor && !v.Active)
                padBlocked[v.Claim.OwnerId + "/" + v.Claim.ActionId] = v.Reason;

        lock (CacheGate)
        {
            _cachedGamepad = padPicture;
            _gameGamepad = padGame;
            _gamepadBlocked = padBlocked;
            _cached = picture;
            _cachedStamp = stamp;
            _dirty = false;
            _game = game;
            if (readGame)
            {
                _gameStale = false;
                _gameWorld = stamp.World;
                _gamePadRead = stamp.Pad;
            }

            _blocked = blocked;
            _windowSharers = windowSharers;
        }

        return picture;
    }

    /// <summary>
    /// The game's keys as last read; read again only at world entry or after <see cref="Invalidate"/> with
    /// <c>game: true</c>. Used by the picture, the registration log and the remap preview alike.
    /// </summary>
    internal static GameKeybindSnapshot GameSnapshot() => GameSnapshot(WorldSequence());

    private static GameKeybindSnapshot GameSnapshot(ulong world)
    {
        lock (CacheGate)
        {
            if (!_gameStale && _game != null && world == _gameWorld) return _game;
        }

        var game = ReadGame();
        // KEYS2 review: remember an in-world read, so a remap at the title screen is still checked against it.
        try { KeybindGameCache.Remember(game); }
        catch { /* the cache is a convenience; the read stands */ }
        lock (CacheGate)
        {
            _game = game;
            _gameStale = false;
            _gameWorld = world;
        }

        return game;
    }

    /// <summary>
    /// HUB2: for each window key (<see cref="KeybindScope.Window"/>), the other mods' keys on its chord that keep it: every
    /// active Progenitor bind of another plugin (any scope) and every ordinary mod's key setting. Not a clash (neither side
    /// is reported as losing); at press time the window key gives way whenever one of them can act
    /// (<see cref="Keybinds"/>' window gate), so one press never fires both and never fires neither. Pure.
    /// </summary>
    internal static Dictionary<string, List<KeybindClaim>> WindowSharers(KeybindPicture picture)
    {
        var result = new Dictionary<string, List<KeybindClaim>>(StringComparer.OrdinalIgnoreCase);
        var byChord = new Dictionary<KeyChord, List<KeybindClaim>>();
        foreach (var v in picture.Verdicts)
        {
            var c = v.Claim;
            if (c.Source == KeybindSource.Game || !c.Comparable || c.Scope == KeybindScope.Window) continue;
            // A Progenitor bind that lost its chord (to the game, say) never fires, so it is no reason to stand aside.
            if (c.Source == KeybindSource.Progenitor && !v.Active) continue;
            if (!byChord.TryGetValue(c.Chord, out var list)) byChord[c.Chord] = list = new List<KeybindClaim>();
            list.Add(c);
        }

        foreach (var v in picture.Verdicts)
        {
            var c = v.Claim;
            if (c.Source != KeybindSource.Progenitor || c.Scope != KeybindScope.Window || !c.Comparable) continue;
            if (!byChord.TryGetValue(c.Chord, out var sharers)) continue;
            var others = sharers.FindAll(s => !string.Equals(s.OwnerId, c.OwnerId, StringComparison.OrdinalIgnoreCase));
            if (others.Count > 0) result[c.OwnerId + "/" + c.ActionId] = others;
        }

        return result;
    }

    /// <summary>HUB2: the other mods' keys window key <paramref name="bindId"/> gives way to; empty when none.</summary>
    internal static IReadOnlyList<KeybindClaim> SharersOf(string pluginId, string bindId)
    {
        Dictionary<string, List<KeybindClaim>> sharers;
        lock (CacheGate) sharers = _windowSharers;
        return sharers.TryGetValue((pluginId ?? "") + "/" + (bindId ?? ""), out var list) ? list : Array.Empty<KeybindClaim>();
    }

    /// <summary>HUB2: the Keybinds tab's quiet note on a window key a mod's key shares, or empty.</summary>
    internal static string WindowShareNote(IReadOnlyList<KeybindClaim> sharers)
    {
        if (sharers == null || sharers.Count == 0) return "";
        var s = sharers[0];
        return DisplayNameOf(s.OwnerId) + "'s " + s.DisplayName + " also uses " + s.ChordText
            + "; whenever it can act, it does and this key doesn't.";
    }

    /// <summary>
    /// The reason a Progenitor bind must not fire right now, or null when it may. Asked by the pump on a
    /// press edge: a dictionary lookup in the table the last build left, never a rebuild.
    /// </summary>
    internal static string? BlockedReason(string pluginId, string bindId)
    {
        Dictionary<string, string> blocked;
        lock (CacheGate) blocked = _blocked;
        return blocked.TryGetValue((pluginId ?? "") + "/" + (bindId ?? ""), out var reason) ? reason : null;
    }

    /// <summary>Decide every claim. Pure: the inputs are all it reads, besides install times and names.</summary>
    internal static KeybindPicture Compute(
        IReadOnlyList<KeybindClaim> game, IReadOnlyList<KeybindClaim> progenitor, IReadOnlyList<KeybindClaim> cfg,
        bool gameAvailable = true, string gameNote = "")
    {
        var all = new List<KeybindClaim>();
        if (game != null) all.AddRange(game);
        if (progenitor != null) all.AddRange(progenitor);
        if (cfg != null) all.AddRange(cfg);

        var installCache = new Dictionary<string, ModInstallTime>(StringComparer.OrdinalIgnoreCase);
        var byChord = new Dictionary<KeyChord, List<KeybindClaim>>();
        foreach (var c in all)
        {
            if (!c.Comparable) continue;
            if (!byChord.TryGetValue(c.Chord, out var list)) byChord[c.Chord] = list = new List<KeybindClaim>();
            list.Add(c);
        }

        var verdicts = new List<KeybindVerdict>(all.Count);
        foreach (var c in all)
        {
            if (!c.Comparable || !byChord.TryGetValue(c.Chord, out var sameChord))
            {
                verdicts.Add(new KeybindVerdict(c, true, null, "", Array.Empty<KeybindClaim>(), ""));
                continue;
            }

            // KEYS4: only the claims this one can meet (a menu key never meets a gameplay key).
            var same = sameChord.FindAll(o => ReferenceEquals(o, c) || ScopesMeet(o, c));

            var others = new List<KeybindClaim>();
            foreach (var o in same)
                if (!ReferenceEquals(o, c) && !SameOwner(o, c)) others.Add(o);

            if (others.Count == 0 || c.Source == KeybindSource.Game)
            {
                // A game key is never beaten; it is listed with the mods that want it, for the tab.
                var mods = others.FindAll(o => o.Source != KeybindSource.Game);
                verdicts.Add(new KeybindVerdict(c, true, null, "", c.Source == KeybindSource.Game ? mods : others,
                    c.Source == KeybindSource.Game && mods.Count > 0 ? GameAlsoUsedBy(c, mods) : ""));
                continue;
            }

            // A mode-only game key (or any game key with EnforceGameWins off) takes part as a warning only.
            var contenders = new List<KeybindClaim>();
            var soft = new List<KeybindClaim>();
            foreach (var o in same)
            {
                if (o.Source == KeybindSource.Game && !GameWins(o, c.Scope)) soft.Add(o);
                else contenders.Add(o);
            }

            KeybindClaim winner = WinnerOf(contenders, installCache, out _);
            bool active = SameOwner(winner, c);
            string rule = "";
            if (!active) rule = RuleFor(winner, c, installCache);
            string reason;
            if (active)
            {
                var modOthers = others.FindAll(o => o.Source != KeybindSource.Game);
                reason = JoinSentences(WinnerSentence(c, modOthers), SoftGameSentence(soft));
            }
            else reason = LoserSentence(c, winner, rule);
            verdicts.Add(new KeybindVerdict(c, active, active ? null : winner, rule, others, reason));
        }

        return new KeybindPicture(verdicts, gameAvailable, gameNote);
    }

    /// <summary>
    /// The claim that wins a chord: the first by <see cref="Precedence"/> — an always-active game key, then
    /// Progenitor's own keys, then mods with a known install time by time, then mods whose time is unknown,
    /// ties by plugin id.
    /// </summary>
    internal static KeybindClaim WinnerOf(IReadOnlyList<KeybindClaim> claims, Dictionary<string, ModInstallTime>? cache, out string rule)
    {
        KeybindClaim? best = null;
        for (int i = 0; i < claims.Count; i++)
        {
            var c = claims[i];
            if (best == null || Precedence(c, best, cache, out _) < 0) best = c;
        }

        rule = best == null ? "" : best.Source == KeybindSource.Game ? RuleGame : RuleInstall;
        return best!;
    }

    /// <summary>
    /// A total, transitive order (negative when <paramref name="a"/> comes first): rank (always-active game
    /// key 0, Progenitor's own keys 1, a mod with a known install time 2, a mod with none 3), then the
    /// install time within rank 2, then the plugin id (case-insensitive, then ordinal). <paramref name="rule"/>
    /// says what decided it (<see cref="RuleFor"/> words it for the loser).
    /// </summary>
    internal static int Precedence(KeybindClaim a, KeybindClaim b, Dictionary<string, ModInstallTime>? cache, out string rule)
    {
        int ra = Rank(a, cache, out var ta);
        int rb = Rank(b, cache, out var tb);
        if (ra != rb)
        {
            rule = Math.Min(ra, rb) switch { 0 => RuleGame, 1 => RuleFramework, _ => RuleUnknownTime };
            return ra < rb ? -1 : 1;
        }

        if (ra == 2 && ta.Utc is { } ua && tb.Utc is { } ub && ua != ub)
        {
            rule = RuleInstall;
            return ua < ub ? -1 : 1;
        }

        rule = ra switch { 0 => RuleGame, 1 => RuleFramework, _ => RuleLoadOrder };
        int c = StringComparer.OrdinalIgnoreCase.Compare(a.OwnerId, b.OwnerId);
        if (c == 0) c = StringComparer.Ordinal.Compare(a.OwnerId, b.OwnerId);
        if (c == 0) c = StringComparer.Ordinal.Compare(a.ActionId, b.ActionId);
        return c;
    }

    /// <summary>0 an always-active game key, 1 Progenitor's own, 2 a mod with a known install time, 3 unknown.</summary>
    internal static int Rank(KeybindClaim c, Dictionary<string, ModInstallTime>? cache, out ModInstallTime time)
    {
        time = ModInstallTime.Unknown;
        if (c.Source == KeybindSource.Game) return 0;
        if (IsFramework(c.OwnerId)) return 1;
        time = InstallOf(c.OwnerId, cache);
        return time.Utc != null ? 2 : 3;
    }

    /// <summary>Why <paramref name="winner"/> beat <paramref name="loser"/>, as a rule name.</summary>
    internal static string RuleFor(KeybindClaim winner, KeybindClaim loser, Dictionary<string, ModInstallTime>? cache)
    {
        Precedence(winner, loser, cache, out string rule);
        return rule;
    }

    internal static ModInstallTime InstallOf(string ownerId, Dictionary<string, ModInstallTime>? cache = null)
    {
        if (cache != null && cache.TryGetValue(ownerId ?? "", out var hit)) return hit;
        ModInstallTime t = ModInstallTime.Unknown;
        try
        {
            var f = InstallTimeOf;
            if (f != null) t = f(ownerId ?? "");
        }
        catch { t = ModInstallTime.Unknown; }

        if (cache != null) cache[ownerId ?? ""] = t;
        return t;
    }

    private static bool SameOwner(KeybindClaim a, KeybindClaim b) =>
        a.Source == KeybindSource.Game
            ? b.Source == KeybindSource.Game
            : b.Source != KeybindSource.Game && string.Equals(a.OwnerId, b.OwnerId, StringComparison.OrdinalIgnoreCase);

    // ---- the words (one source for the tab, the Keys section and ModProblems) ----------------

    internal const string PickAnother = "Pick another key.";
    internal const string BothFire = "Both fire; Progenitor can't stop this mod.";

    /// <summary>Who holds the key, as the sentence names it: the game with its action, or a mod by name.</summary>
    internal static string HolderPhrase(KeybindClaim winner) =>
        winner.Source == KeybindSource.Game
            ? GameName + " (" + winner.DisplayName + ")"
            : DisplayNameOf(winner.OwnerId);

    internal static string WhyPhrase(KeybindClaim winner, string rule) => rule switch
    {
        RuleGame => ", which always comes first",
        RuleFramework => ", which is part of Progenitor and comes right after the game",
        RuleInstall => ", installed first",
        RuleUnknownTime => ", installed first (this mod's install time is unknown, so it comes after every mod whose time is known)",
        _ => ", which comes first by mod id (the install times are the same or unknown)",
    };

    /// <summary>
    /// KEYS5: a chord named in a sentence about a game key the game writes differently, with the game's words
    /// too, so the player can tell they are the same key: <c>Mouse Forward (the game's Mouse Button 4)</c>.
    /// Unchanged when the holder is not the game or the two read the same (<c>W</c> and <c>W</c>).
    /// </summary>
    internal static string ChordPhrase(string chordText, KeybindClaim? holder)
    {
        string own = (chordText ?? "").Trim();
        if (holder == null || holder.Source != KeybindSource.Game) return own;
        string game = (holder.ChordText ?? "").Trim();
        if (game.Length == 0 || string.Equals(game, "None", StringComparison.OrdinalIgnoreCase)) return own;
        return SameKeyWords(own, game) ? own : own + " (the game's " + game + ")";
    }

    private static bool SameKeyWords(string a, string b)
    {
        static string N(string s) => s.Replace(" ", "").Replace("Digit", "").ToLowerInvariant();
        return string.Equals(N(a), N(b), StringComparison.Ordinal);
    }

    /// <summary>A warning-only game clash: <c>Also used by the game while building (Place).</c></summary>
    internal static string SoftGameSentence(IReadOnlyList<KeybindClaim> soft)
    {
        if (soft == null || soft.Count == 0) return "";
        var parts = new List<string>();
        foreach (var g in soft)
        {
            string p = GameName + (g.Mode.Length > 0 ? " " + g.Mode : "") + " (" + g.DisplayName + ")";
            if (!parts.Contains(p)) parts.Add(p);
        }

        bool anyMode = soft.Any(g => g.Mode.Length > 0);
        return "Also used by " + JoinNames(parts) + (anyMode
            ? "; both fire there, and Progenitor does not stop either until that is checked in game."
            : "; both fire, because [Keybinds] EnforceGameWins is off.");
    }

    internal static string JoinSentences(string a, string b) =>
        a.Length == 0 ? b : b.Length == 0 ? a : a + " " + b;

    /// <summary>The loser's row: <c>Not active: F8 is used by X, installed first. Pick another key.</c></summary>
    internal static string LoserSentence(KeybindClaim loser, KeybindClaim winner, string rule)
    {
        string chord = ChordPhrase(loser.ChordText, winner);
        if (loser.Source == KeybindSource.Cfg)
            return chord + " is also used by " + HolderPhrase(winner) + WhyPhrase(winner, rule) + ". " + BothFire;
        return "Not active: " + chord + " is used by " + HolderPhrase(winner) + WhyPhrase(winner, rule) + ". " + PickAnother;
    }

    /// <summary>The winner's row: <c>Also used by X (not active there).</c></summary>
    internal static string WinnerSentence(KeybindClaim winner, IReadOnlyList<KeybindClaim> others)
    {
        var parts = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var o in others)
        {
            if (!seen.Add(o.OwnerId)) continue;
            string name = DisplayNameOf(o.OwnerId);
            parts.Add(o.Source == KeybindSource.Cfg ? name + ", which still fires" : name + " (not active there)");
        }

        return parts.Count == 0 ? "" : "Also bound by " + JoinNames(parts) + ".";
    }

    internal static string GameAlsoUsedBy(KeybindClaim game, IReadOnlyList<KeybindClaim> mods)
    {
        var names = new List<string>();
        foreach (var m in mods)
        {
            string n = DisplayNameOf(m.OwnerId);
            if (!names.Contains(n)) names.Add(n);
        }

        return "Also wanted by " + JoinNames(names) + (GameWins(game) ? "; the game keeps it." : "; both fire.");
    }

    /// <summary>The other owners' names, joined (<c>A</c>, <c>A and B</c>, <c>A, B and C</c>).</summary>
    internal static string OthersNames(IReadOnlyList<KeybindClaim> others)
    {
        var names = new List<string>();
        foreach (var o in others)
        {
            string n = o.Source == KeybindSource.Game ? GameName : DisplayNameOf(o.OwnerId);
            if (!names.Contains(n)) names.Add(n);
        }

        return JoinNames(names);
    }

    internal static string JoinNames(List<string> names) => KeybindConflictSource.JoinNames(names);

    /// <summary>The whole-chord readback for a verdict: <c>{with, winner, reason, rule}</c>, or null without a clash.</summary>
    internal static Dictionary<string, object?>? ConflictReadback(KeybindVerdict v)
    {
        if (!v.InConflict) return null;
        var with = new List<object?>();
        foreach (var o in v.Others)
            with.Add(new Dictionary<string, object?>
            {
                ["owner"] = o.OwnerId,
                ["name"] = o.Source == KeybindSource.Game ? GameName : DisplayNameOf(o.OwnerId),
                ["action"] = o.ActionId,
                ["source"] = KeybindClaim.NameOf(o.Source),
            });
        var winner = v.Active ? v.Claim : v.Winner!;
        return new Dictionary<string, object?>
        {
            ["with"] = with,
            ["winner"] = winner.Source == KeybindSource.Game ? GameOwnerId : winner.OwnerId,
            ["winnerAction"] = winner.ActionId,
            ["rule"] = v.Rule,
            ["reason"] = v.Reason,
            ["enforced"] = v.Enforced,
        };
    }
}

/// <summary>
/// UnityEngine <c>KeyCode</c> names (what BepInEx's <c>KeyboardShortcut</c> and a <c>KeyCode</c> entry
/// store) to and from <see cref="UiKey"/> (the Input System's names). <c>Mouse0</c> and <c>Mouse1</c> are the left and
/// right buttons (KEYS8); <c>Mouse2</c> and up and joystick codes have no
/// <see cref="UiKey"/>: they read as not comparable and are never written.
/// </summary>
internal static class LegacyKeyNames
{
    private static readonly Dictionary<string, UiKey> ToUi = Build();
    private static readonly Dictionary<UiKey, string> FromUi = BuildReverse();

    private static Dictionary<string, UiKey> Build()
    {
        var d = new Dictionary<string, UiKey>(StringComparer.OrdinalIgnoreCase)
        {
            ["Return"] = UiKey.Enter,
            ["BackQuote"] = UiKey.Backquote,
            ["Quote"] = UiKey.Quote,
            ["Semicolon"] = UiKey.Semicolon,
            ["Comma"] = UiKey.Comma,
            ["Period"] = UiKey.Period,
            ["Slash"] = UiKey.Slash,
            ["Backslash"] = UiKey.Backslash,
            ["LeftBracket"] = UiKey.LeftBracket,
            ["RightBracket"] = UiKey.RightBracket,
            ["Minus"] = UiKey.Minus,
            ["Equals"] = UiKey.Equals,
            ["Space"] = UiKey.Space,
            ["Tab"] = UiKey.Tab,
            ["Escape"] = UiKey.Escape,
            ["Backspace"] = UiKey.Backspace,
            ["Delete"] = UiKey.Delete,
            ["Insert"] = UiKey.Insert,
            ["Home"] = UiKey.Home,
            ["End"] = UiKey.End,
            ["PageUp"] = UiKey.PageUp,
            ["PageDown"] = UiKey.PageDown,
            ["UpArrow"] = UiKey.UpArrow,
            ["DownArrow"] = UiKey.DownArrow,
            ["LeftArrow"] = UiKey.LeftArrow,
            ["RightArrow"] = UiKey.RightArrow,
            ["CapsLock"] = UiKey.CapsLock,
            ["Numlock"] = UiKey.NumLock,
            ["ScrollLock"] = UiKey.ScrollLock,
            ["Pause"] = UiKey.Pause,
            ["Print"] = UiKey.PrintScreen,
            ["Menu"] = UiKey.ContextMenu,
            ["KeypadEnter"] = UiKey.NumpadEnter,
            ["KeypadDivide"] = UiKey.NumpadDivide,
            ["KeypadMultiply"] = UiKey.NumpadMultiply,
            ["KeypadPlus"] = UiKey.NumpadPlus,
            ["KeypadMinus"] = UiKey.NumpadMinus,
            ["KeypadPeriod"] = UiKey.NumpadPeriod,
            ["KeypadEquals"] = UiKey.NumpadEquals,
            ["LeftShift"] = UiKey.LeftShift,
            ["RightShift"] = UiKey.RightShift,
            ["LeftControl"] = UiKey.LeftCtrl,
            ["RightControl"] = UiKey.RightCtrl,
            ["LeftAlt"] = UiKey.LeftAlt,
            ["RightAlt"] = UiKey.RightAlt,
            ["LeftWindows"] = UiKey.LeftMeta,
            ["RightWindows"] = UiKey.RightMeta,
            // KEYS8: KeyCode's left and right buttons, so another mod's click shortcut is compared with
            // Progenitor's click chords. Only these two: no Progenitor bind could sit on them before KEYS8, so
            // comparing them changes no existing winner. Mouse2-Mouse4 (middle, back, forward) stay not
            // comparable as before, because a Progenitor bind on MouseBack since KEYS4 must not start losing the
            // install-order rule to another mod's KeyCode setting it never met.
            ["Mouse0"] = UiKey.MouseLeft,
            ["Mouse1"] = UiKey.MouseRight,
        };
        for (int i = 0; i <= 9; i++)
        {
            string n = i.ToString(CultureInfo.InvariantCulture);
            d["Alpha" + n] = (UiKey)Enum.Parse(typeof(UiKey), "Digit" + n);
            d["Keypad" + n] = (UiKey)Enum.Parse(typeof(UiKey), "Numpad" + n);
        }

        for (char c = 'A'; c <= 'Z'; c++) d[c.ToString()] = (UiKey)Enum.Parse(typeof(UiKey), c.ToString());
        for (int i = 1; i <= 12; i++) d["F" + i.ToString(CultureInfo.InvariantCulture)] = (UiKey)Enum.Parse(typeof(UiKey), "F" + i.ToString(CultureInfo.InvariantCulture));
        return d;
    }

    private static Dictionary<UiKey, string> BuildReverse()
    {
        var r = new Dictionary<UiKey, string>();
        foreach (var pair in ToUi)
            if (!r.ContainsKey(pair.Value)) r[pair.Value] = pair.Key;
        return r;
    }

    private static KeyModifiers ModifierOf(string token) => token.ToLowerInvariant() switch
    {
        "leftcontrol" or "rightcontrol" => KeyModifiers.Ctrl,
        "leftshift" or "rightshift" => KeyModifiers.Shift,
        "leftalt" or "rightalt" or "altgr" => KeyModifiers.Alt,
        _ => KeyModifiers.None,
    };

    /// <summary>
    /// Read a <c>KeyboardShortcut</c> (<c>F8 + LeftControl</c>) or <c>KeyCode</c> (<c>F8</c>) value.
    /// False for anything without a <see cref="UiKey"/> (Mouse2 and up, a joystick, a
    /// command key as a modifier); <c>None</c> and empty read as unbound.
    /// </summary>
    internal static bool TryParse(string? text, out KeyChord chord)
    {
        chord = KeyChord.None;
        string t = (text ?? "").Trim();
        if (t.Length == 0 || string.Equals(t, "None", StringComparison.OrdinalIgnoreCase)) return true;
        // BepInEx's KeyboardShortcut.Deserialize splits on these, main key first (review 2026-09-29).
        var parts = t.Split(Separators, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return true;
        string main = parts[0].Trim();
        if (string.Equals(main, "None", StringComparison.OrdinalIgnoreCase)) return true;
        if (!ToUi.TryGetValue(main, out var key)) return false;
        var mods = KeyModifiers.None;
        for (int i = 1; i < parts.Length; i++)
        {
            var m = ModifierOf(parts[i].Trim());
            // A command/Windows key as a modifier has no KeyChord equivalent: not comparable.
            if (m == KeyModifiers.None) return false;
            mods |= m;
        }

        chord = new KeyChord(key, mods);
        return true;
    }

    /// <summary>The separators BepInEx's <c>KeyboardShortcut</c> accepts between key names.</summary>
    internal static readonly char[] Separators = { ' ', '+', ',', ';', '|' };

    /// <summary>
    /// The value a <c>KeyboardShortcut</c> (or, with <paramref name="keyCodeOnly"/>, a <c>KeyCode</c>)
    /// entry stores for <paramref name="chord"/>. False with a reason when it cannot hold it.
    /// </summary>
    internal static bool TryFormat(KeyChord chord, bool keyCodeOnly, out string text, out string reason, string? previous = null)
    {
        // Keep the side the entry already names (RightControl stays RightControl), review 2026-09-29.
        string prev = previous ?? "";
        bool Has(string token)
        {
            foreach (var part in prev.Split(Separators, StringSplitOptions.RemoveEmptyEntries))
                if (string.Equals(part.Trim(), token, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        string Side(string left, string right) => Has(right) && !Has(left) ? right : left;
        text = "";
        reason = "";
        if (!chord.IsBound)
        {
            text = "None";
            return true;
        }

        if (!FromUi.TryGetValue(chord.Key, out var main))
        {
            reason = chord.Key + " cannot be stored in this mod's setting.";
            return false;
        }

        if (keyCodeOnly)
        {
            if (chord.Modifiers != KeyModifiers.None)
            {
                reason = "This mod's setting takes one key without Ctrl, Shift or Alt.";
                return false;
            }

            text = main;
            return true;
        }

        text = main;
        if ((chord.Modifiers & KeyModifiers.Ctrl) != 0) text += KeyChord.Separator + Side("LeftControl", "RightControl");
        if ((chord.Modifiers & KeyModifiers.Shift) != 0) text += KeyChord.Separator + Side("LeftShift", "RightShift");
        if ((chord.Modifiers & KeyModifiers.Alt) != 0) text += KeyChord.Separator + Side("LeftAlt", "RightAlt");
        return true;
    }
}

/// <summary>
/// KEYS1: where a plugin's DLL is, asked of several sources in order (the chainloader, the owner of its
/// live keybinds, the reloader's assemblies); the first answer wins. Game-free so the order is tested.
/// </summary>
internal static class KeybindPluginFiles
{
    internal static string? First(string id, params Func<string, string?>[] sources)
    {
        foreach (var source in sources)
        {
            string? path;
            try { path = source(id); }
            catch { path = null; }
            if (!string.IsNullOrWhiteSpace(path)) return path;
        }

        return null;
    }
}
