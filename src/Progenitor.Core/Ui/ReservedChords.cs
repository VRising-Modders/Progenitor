using System;
using System.Collections.Generic;
using System.Text;

namespace Progenitor.Ui;

/// <summary>Who takes a reserved chord before the game sees it (<see cref="ReservedChords"/>).</summary>
internal enum ReservedBy
{
    None = 0,

    /// <summary>Windows itself: the shell, the window manager, the security screen, the screenshot keys.</summary>
    Windows = 1,

    /// <summary>The Steam client's overlay defaults, which a player cannot easily avoid.</summary>
    Steam = 2,
}

/// <summary>One row of <see cref="ReservedChords.Table"/>.</summary>
internal readonly struct ReservedChord
{
    internal ReservedChord(KeyChord chord, ReservedBy by, string does)
    {
        Chord = chord;
        By = by;
        Does = does;
    }

    internal KeyChord Chord { get; }

    internal ReservedBy By { get; }

    /// <summary>What the chord does there, as the reason's verb phrase: <c>closes the window in Windows</c>.</summary>
    internal string Does { get; }
}

/// <summary>
/// KEYS2: a Progenitor bind's reserved chord that was refused at registration, and what replaced it. The
/// replacement is written back to the cfg once, so the refusal (and its problem) happens at most once.
/// </summary>
internal sealed class ReservedNote
{
    internal ReservedNote(KeyChord chord, ReservedBy by, bool fromCfg, KeyChord replacement)
    {
        Chord = chord;
        By = by;
        FromCfg = fromCfg;
        Replacement = replacement;
    }

    /// <summary>The refused chord (the mod's default or the cfg value).</summary>
    internal KeyChord Chord { get; }

    internal ReservedBy By { get; }

    /// <summary>True when the refused chord was the player's cfg value; false for the mod's own default.</summary>
    internal bool FromCfg { get; }

    /// <summary>What the bind got instead: the default, or <see cref="KeyChord.None"/>.</summary>
    internal KeyChord Replacement { get; }

    /// <summary>
    /// The row's and the problem's sentence: <c>F12 is reserved by Steam; your key was reset to F9.</c> /
    /// <c>…; your key was unbound.</c> / <c>Its default key Alt+F4 is reserved by Windows, so it is unbound.</c>
    /// </summary>
    internal string Sentence => ReservedChords.ProblemSentence(this);
}

/// <summary>
/// KEYS2: problems for reserved keys, read live. A Progenitor bind whose default or cfg value was refused
/// at registration (until the player picks a key, when it clears), and an ordinary mod's key setting on a
/// reserved chord (until it is changed). Warnings, not conflicts.
/// </summary>
internal sealed class ReservedKeySource : IModProblemSource
{
    public string Name => "reserved-keys";

    /// <summary>Ordinary mods' reserved keys already logged by this process (logged once, not per read or reload).</summary>
    private static readonly HashSet<string> LoggedCfg = new(StringComparer.OrdinalIgnoreCase);

    internal static void ResetForTests()
    {
        lock (LoggedCfg) LoggedCfg.Clear();
    }

    public IEnumerable<ModProblem> Current()
    {
        IReadOnlyList<(string PluginId, string Id, ReservedNote Note)> refused;
        try { refused = Keybinds.ReservedRefusals(); }
        catch { refused = Array.Empty<(string, string, ReservedNote)>(); }
        foreach (var (plugin, id, note) in refused)
        {
            yield return new ModProblem(
                ModProblemSeverity.Warning, ModProblemKinds.KeybindReserved, plugin, note.Sentence, ReservedChords.ProblemWhatToDo,
                "Ui: keybind '" + plugin + "/" + id + "': " + ReservedChords.RegistrationLog(KeybindArbiter.DisplayNameOf(plugin), note.Chord, note.By),
                ReservedChords.NameOf(note.By), key: id + "|reserved", listLine: ReservedChords.ProblemListLine(note.Chord, note.By),
                dismissible: true);
        }

        KeybindPicture picture;
        try { picture = KeybindArbiter.Current(); }
        catch { yield break; }
        foreach (var v in picture.Verdicts)
        {
            var c = v.Claim;
            if (c.Source != KeybindSource.Cfg || string.IsNullOrWhiteSpace(c.OwnerId)) continue;
            var by = ReservedChords.CfgReservedBy(c);
            if (by == ReservedBy.None) continue;
            string text = c.Comparable ? ReservedChords.Display(c.Chord) : c.ChordText;
            bool first;
            lock (LoggedCfg) first = LoggedCfg.Add(c.OwnerId + "|" + c.ActionId + "|" + c.ChordText);
            if (first)
                Plugin.Logger?.LogWarning("Ui: key setting " + c.OwnerId + " " + c.ActionId + ": "
                    + ReservedChords.CfgProblemSentence(text, by) + " Progenitor cannot change another mod's key by itself; "
                    + "change it in Mods > Keybinds. Logged once per process.");
            yield return new ModProblem(
                ModProblemSeverity.Warning, ModProblemKinds.KeybindReserved, c.OwnerId,
                ReservedChords.CfgProblemSentence(text, by), ModProblems.CfgKeyWhatToDo, "",
                ReservedChords.NameOf(by), key: c.ActionId + "|reserved",
                listLine: "Key " + text + (by == ReservedBy.Steam ? " used by Steam" : " reserved by Windows"));
        }
    }
}

/// <summary>
/// KEYS2 (requested 2026-09-29, "don't allow it to conflict with the OS"): chords Windows or Steam
/// take before the game sees them. A Progenitor bind is never put on one:
/// <list type="bullet">
/// <item><description>a mod's default on one is registered unbound (<see cref="Keybinds.Register"/>);</description></item>
/// <item><description>a cfg value on one falls back to the default, or unbound;</description></item>
/// <item><description>the rebind capture and the Keybinds tab refuse one and keep waiting for another key;</description></item>
/// <item><description>our own writes into an ordinary mod's key setting refuse one (<see cref="SettingsCatalog.WriteKey"/>);</description></item>
/// <item><description>an ordinary mod's key already on one gets a row warning and a problem (we cannot stop that mod).</description></item>
/// </list>
///
/// <para>
/// <b>What <see cref="KeyChord"/> can express.</b> Its modifiers are Ctrl, Shift and Alt only, so no
/// chord can carry the Windows key as a modifier. The Windows key is a <see cref="UiKey"/>
/// (<c>LeftMeta</c> / <c>RightMeta</c>, the Input System's <c>Key.LeftMeta</c> = <c>LeftWindows</c>
/// on the client), so a bare Windows key is representable and is in the table; every
/// <c>Win + X</c> shortcut (the Game Bar's <c>Win+G</c> and <c>Win+Alt+…</c>, <c>Win+Print Screen</c>,
/// <c>Win+Shift+S</c>) is not, and is refused at the capture instead: a key pressed while either
/// Windows key is held (<see cref="ModifierSides.LeftMeta"/>, read off <c>Keyboard.leftMetaKey</c> /
/// <c>rightMetaKey</c>) never becomes a binding and never fires
/// one (<see cref="KeyChord.MatchesHeld"/>). A cfg value naming the Windows key as a modifier
/// (<c>F8 + LeftWindows</c> in a <c>KeyboardShortcut</c>) is not comparable, so it is recognized by its
/// text (<see cref="UsesWindowsKey"/>). <c>Print Screen</c> is <see cref="UiKey.PrintScreen"/>.
/// </para>
///
/// <para>
/// <b>Left out on purpose.</b> <c>Ctrl+Alt+Esc</c> has no Windows 10/11 meaning. <c>Alt+Enter</c> is a
/// game convention (full screen), not the operating system's. The Steam overlay and screenshot keys
/// are the client's defaults; a player who changed them in Steam is still refused the defaults,
/// because Progenitor cannot read Steam's settings.
/// </para>
/// </summary>
internal static class ReservedChords
{
    private const KeyModifiers Ctrl = KeyModifiers.Ctrl;
    private const KeyModifiers Shift = KeyModifiers.Shift;
    private const KeyModifiers Alt = KeyModifiers.Alt;

    internal const string WindowsKeyDoes = "belongs to Windows (the Start menu, the Game Bar and every Windows-key shortcut)";

    /// <summary>The capture's refusal when a key went down with a Windows key held.</summary>
    internal const string WindowsKeyHeldReason =
        "Windows-key shortcuts belong to Windows. " + KeybindArbiter.PickAnother;

    /// <summary>An ordinary mod's row: the key is on a Windows chord, which we cannot stop.</summary>
    internal const string WindowsRowWarning = "Reserved by Windows; this key may not reach the mod.";

    /// <summary>An ordinary mod's row: the key is on a Steam overlay default.</summary>
    internal const string SteamRowWarning = "Used by Steam; this key may not reach the mod.";

    /// <summary>
    /// The table. Exact chords, as the pump fires by, except one whole-key rule (<see cref="Find"/>): the
    /// Windows key with any modifiers.
    /// </summary>
    internal static readonly IReadOnlyList<ReservedChord> Table = new[]
    {
        // Windows: the window manager and the shell.
        new ReservedChord(new KeyChord(UiKey.Tab, Alt), ReservedBy.Windows, "switches windows in Windows"),
        new ReservedChord(new KeyChord(UiKey.Tab, Alt | Shift), ReservedBy.Windows, "switches windows in Windows"),
        new ReservedChord(new KeyChord(UiKey.Tab, Ctrl | Alt), ReservedBy.Windows, "opens the window switcher in Windows"),
        new ReservedChord(new KeyChord(UiKey.Escape, Alt), ReservedBy.Windows, "switches windows in Windows"),
        new ReservedChord(new KeyChord(UiKey.F4, Alt), ReservedBy.Windows, "closes the window in Windows"),
        new ReservedChord(new KeyChord(UiKey.Space, Alt), ReservedBy.Windows, "opens the window menu in Windows"),
        new ReservedChord(new KeyChord(UiKey.Escape, Ctrl), ReservedBy.Windows, "opens the Start menu in Windows"),
        new ReservedChord(new KeyChord(UiKey.Escape, Ctrl | Shift), ReservedBy.Windows, "opens Task Manager in Windows"),
        new ReservedChord(new KeyChord(UiKey.Delete, Ctrl | Alt), ReservedBy.Windows, "opens the Windows security screen"),
        new ReservedChord(new KeyChord(UiKey.Delete, Ctrl | Alt | Shift), ReservedBy.Windows, "opens the Windows security screen"),
        new ReservedChord(new KeyChord(UiKey.NumpadPeriod, Ctrl | Alt), ReservedBy.Windows, "opens the Windows security screen"),

        // Windows: the screenshot keys (Print Screen opens the Snipping Tool on Windows 11). Only the
        // combinations Windows takes: plain, Alt, Ctrl, and Left Alt+Left Shift (High Contrast). Win+Print
        // Screen cannot be a chord; the capture refuses it with the Windows key held.
        new ReservedChord(new KeyChord(UiKey.PrintScreen), ReservedBy.Windows, "takes a screenshot in Windows"),
        new ReservedChord(new KeyChord(UiKey.PrintScreen, Alt), ReservedBy.Windows, "copies the active window in Windows"),
        new ReservedChord(new KeyChord(UiKey.PrintScreen, Ctrl), ReservedBy.Windows, "takes a screenshot in Windows"),
        new ReservedChord(new KeyChord(UiKey.PrintScreen, Alt | Shift), ReservedBy.Windows, "turns High Contrast on or off in Windows"),

        // Windows: accessibility. Left Alt+Left Shift+Num Lock toggles Mouse Keys. Chords are side-agnostic,
        // so either Alt and either Shift are refused, which is the safe side.
        new ReservedChord(new KeyChord(UiKey.NumLock, Alt | Shift), ReservedBy.Windows, "turns Mouse Keys on or off in Windows"),

        // Windows: the Windows key itself.
        new ReservedChord(new KeyChord(UiKey.LeftMeta), ReservedBy.Windows, WindowsKeyDoes),
        new ReservedChord(new KeyChord(UiKey.RightMeta), ReservedBy.Windows, WindowsKeyDoes),

        // Steam: the overlay defaults.
        new ReservedChord(new KeyChord(UiKey.Tab, Shift), ReservedBy.Steam, "opens the Steam overlay"),
        new ReservedChord(new KeyChord(UiKey.F12), ReservedBy.Steam, "takes a Steam screenshot"),
    };

    /// <summary>
    /// The reservation <paramref name="chord"/> falls under, or false. Exact rows first; then any chord
    /// on the Windows key.
    /// </summary>
    internal static bool Find(KeyChord chord, out ReservedChord hit)
    {
        hit = default;
        if (!chord.IsBound) return false;
        for (int i = 0; i < Table.Count; i++)
        {
            if (Table[i].Chord != chord) continue;
            hit = Table[i];
            return true;
        }

        if (chord.Key is UiKey.LeftMeta or UiKey.RightMeta)
        {
            hit = new ReservedChord(chord, ReservedBy.Windows, WindowsKeyDoes);
            return true;
        }

        return false;
    }

    internal static bool IsReserved(KeyChord chord) => Find(chord, out _);

    internal static ReservedBy ReservedByOf(KeyChord chord) => Find(chord, out var hit) ? hit.By : ReservedBy.None;

    /// <summary>
    /// The refusal a player reads: <c>Alt+F4 closes the window in Windows. Pick another key.</c> /
    /// <c>Shift+Tab opens the Steam overlay. Pick another key.</c> Null when not reserved.
    /// </summary>
    internal static string? Reason(KeyChord chord) =>
        Find(chord, out var hit) ? Display(chord) + " " + hit.Does + ". " + KeybindArbiter.PickAnother : null;

    /// <summary>The same refusal for a reset whose default is reserved.</summary>
    internal static string? ResetReason(KeyChord chord) =>
        Find(chord, out var hit)
            ? "Its default, " + Display(chord) + ", " + hit.Does + ". Unbind it or pick another key."
            : null;

    /// <summary><c>Windows</c> or <c>Steam</c>.</summary>
    internal static string NameOf(ReservedBy by) => by == ReservedBy.Steam ? "Steam" : "Windows";

    /// <summary>
    /// The log line and problem sentence for a mod default or a cfg value that was refused:
    /// <c>Hunts's key Alt+F4 is reserved by Windows; pick another key in the Keybinds tab.</c>
    /// </summary>
    internal static string RegistrationLog(string modName, KeyChord chord, ReservedBy by) =>
        modName + "'s key " + Display(chord) + " is reserved by " + NameOf(by) + "; pick another key in the Keybinds tab.";

    /// <summary>The problem's sentence (the mod is the record's owner, so it is not named again).</summary>
    internal static string ProblemSentence(ReservedNote note)
    {
        string head = Display(note.Chord) + " is reserved by " + NameOf(note.By);
        if (!note.FromCfg) return "Its default key " + head + ", so it is unbound.";
        return head + (note.Replacement.IsBound ? "; your key was reset to " + note.Replacement + "." : "; your key was unbound.");
    }

    internal const string ProblemWhatToDo = "Pick another key in the Keybinds tab, or dismiss this.";

    /// <summary>An ordinary mod's key setting on a reserved chord: <c>Its key Alt+F4 is reserved by Windows, so it may not reach the mod.</c></summary>
    internal static string CfgProblemSentence(string chordText, ReservedBy by) =>
        "Its key " + chordText + (by == ReservedBy.Steam ? " is used by Steam" : " is reserved by Windows") + ", so it may not reach the mod.";

    internal static string ProblemListLine(KeyChord chord, ReservedBy by) =>
        "Key " + Display(chord) + " reserved by " + NameOf(by);

    /// <summary>
    /// An ordinary mod's key setting on a reserved chord: its row warning, or empty. A value naming the
    /// Windows key as a modifier is not comparable (<see cref="LegacyKeyNames.TryParse"/>), so its text is
    /// read as well.
    /// </summary>
    internal static string CfgRowWarning(KeybindClaim claim)
    {
        var by = CfgReservedBy(claim);
        return by == ReservedBy.Steam ? SteamRowWarning : by == ReservedBy.Windows ? WindowsRowWarning : "";
    }

    internal static ReservedBy CfgReservedBy(KeybindClaim claim)
    {
        if (claim == null || claim.Source != KeybindSource.Cfg) return ReservedBy.None;
        if (claim.Comparable) return ReservedByOf(claim.Chord);
        return UsesWindowsKey(claim.ChordText) ? ReservedBy.Windows : ReservedBy.None;
    }

    /// <summary>True when a <c>KeyboardShortcut</c> / <c>KeyCode</c> value names a Windows (or Command) key anywhere.</summary>
    internal static bool UsesWindowsKey(string? cfgText)
    {
        foreach (var part in (cfgText ?? "").Split(LegacyKeyNames.Separators, StringSplitOptions.RemoveEmptyEntries))
        {
            switch (part.Trim().ToLowerInvariant())
            {
                case "leftwindows":
                case "rightwindows":
                case "leftcommand":
                case "rightcommand":
                case "leftapple":
                case "rightapple":
                case "leftmeta":
                case "rightmeta":
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The chord as Windows writes it, modifiers first: <c>Alt+F4</c>, <c>Ctrl+Shift+Esc</c>,
    /// <c>Print Screen</c>, <c>Windows key</c>. Only the reserved-chord sentences use this form; the key
    /// cells keep <see cref="KeyChord.ToString"/>.
    /// </summary>
    internal static string Display(KeyChord chord)
    {
        if (!chord.IsBound) return "None";
        var sb = new StringBuilder();
        if ((chord.Modifiers & Ctrl) != 0) sb.Append("Ctrl+");
        if ((chord.Modifiers & Alt) != 0) sb.Append("Alt+");
        if ((chord.Modifiers & Shift) != 0) sb.Append("Shift+");
        sb.Append(chord.Key switch
        {
            UiKey.PrintScreen => "Print Screen",
            UiKey.Escape => "Esc",
            UiKey.LeftMeta => "the left Windows key",
            UiKey.RightMeta => "the right Windows key",
            UiKey.NumpadPeriod => "Num Del",
            _ => chord.Key.ToString(),
        });
        string text = sb.ToString();
        // A sentence starts with it.
        return text.Length > 0 && char.IsLower(text[0]) ? char.ToUpperInvariant(text[0]) + text.Substring(1) : text;
    }
}

/// <summary>
/// KEYS8 (user report 2026-10-08, "I couldn't set the keybind to a mouse click"): the rules for a chord on the
/// left or right mouse button. Those two buttons are how the player attacks, moves and clicks every menu, and
/// Progenitor never consumes input, so the game always sees the click as well. Game-free and applied wherever a
/// Progenitor chord is set: registration (a default or cfg value), <see cref="Keybinds.Rebind"/>, every capture
/// and the Keybinds tab (<see cref="KeybindRemapGuard.Check"/>).
/// <list type="bullet">
/// <item><description>A bare left or right click is refused in every scope: it is the game's attack and move
/// (and the menus' click), so a bind on it would fire on ordinary play. With Ctrl, Shift or Alt held it is a
/// chord like any other.</description></item>
/// <item><description><see cref="KeybindScope.Menu"/> and <see cref="KeybindScope.Chat"/> refuse both buttons
/// with any modifiers: a Mods-menu key is pressed while the pointer is on Progenitor's own buttons, and a chat
/// key while the player clicks in the chat box.</description></item>
/// <item><description><see cref="KeybindScope.Map"/> refuses both buttons with Ctrl and Shift: the game's map
/// sends its admin teleport on the left click and its admin region unlock on the right one
/// (<c>MapMenuMapper.OnMapClicked</c>). One bind is exempt from the left-click rule: Core's debug map teleport
/// (<see cref="IsGameTeleportTwin"/>), which does what the game's gesture does, so the overlap is harmless
/// (decided 2026-10-18).</description></item>
/// </list>
/// The other half of the rule is at the read (<see cref="Keybinds.ClickChordSuppressedReason"/>): a click chord
/// never fires while the pointer is over a Progenitor window, and a gameplay one not while the game's
/// full-screen menu is open.
/// </summary>
internal static class MouseChords
{
    internal const string BareClickReason =
        "A plain left or right click attacks, moves and clicks menus, so it can't be a key. "
        + "Hold Ctrl, Shift or Alt with it (for example Left Mouse + Shift).";

    internal const string MenuClickReason =
        "Left and right clicks press the Mods menu's own buttons, so they can't be a Mods menu key. " + KeybindArbiter.PickAnother;

    internal const string ChatClickReason =
        "Left and right clicks select text in the chat box, so they can't be a chat key. " + KeybindArbiter.PickAnother;

    internal const string MapUnlockReason =
        "Ctrl+Shift+right click on the map unlocks map regions for admins in the game. " + KeybindArbiter.PickAnother;

    /// <summary>#77 review: the game's map left click teleports an admin with LeftCtrl and LeftShift held.</summary>
    internal const string MapTeleportReason =
        "Ctrl+Shift+left click on the map teleports admins in the game. " + KeybindArbiter.PickAnother;

    /// <summary>Why <paramref name="chord"/> may not be a bind in <paramref name="scope"/>, or null (always null off the left and right buttons).</summary>
    internal static string? Refusal(KeyChord chord, KeybindScope scope)
    {
        if (!chord.IsBound || !KeyChord.IsClickButton(chord.Key)) return null;
        // HUB2: a window key acts in menus too, where the clicks press the menus' own buttons.
        if (scope is KeybindScope.Menu or KeybindScope.Window) return MenuClickReason;
        if (scope == KeybindScope.Chat) return ChatClickReason;
        if (chord.Modifiers == KeyModifiers.None) return BareClickReason;
        // The game's map click handler checks only that Ctrl and Shift are held, so Alt with them still reaches it.
        if (scope == KeybindScope.Map
            && (chord.Modifiers & (KeyModifiers.Ctrl | KeyModifiers.Shift)) == (KeyModifiers.Ctrl | KeyModifiers.Shift))
            return chord.Key == UiKey.MouseRight ? MapUnlockReason : MapTeleportReason;
        return null;
    }

    /// <summary>
    /// The same refusal for one bind: <paramref name="pluginId"/>/<paramref name="id"/> is the Progenitor bind being set,
    /// or null for a bind that is not Progenitor's. Core's debug map teleport may take Ctrl+Shift+left click on the map
    /// (<see cref="IsGameTeleportTwin"/>); every other rule applies to it as to any bind.
    /// </summary>
    internal static string? Refusal(KeyChord chord, KeybindScope scope, string? pluginId, string? id)
    {
        string? refusal = Refusal(chord, scope);
        return ReferenceEquals(refusal, MapTeleportReason) && IsGameTeleportTwin(pluginId, id) ? null : refusal;
    }

    /// <summary>
    /// True for Core's debug map teleport (<c>progenitor/TeleportToMapPoint</c>). The game's Ctrl+Shift+left click on
    /// the map teleports an authenticated admin to the point; this bind teleports any admin wearing the debug buff to
    /// the same point, and the client skips its own request when the game's gesture already does it
    /// (<see cref="Progenitor.Debugging.DebugMapPolicy.SkipHold"/>). So the chord may be shared.
    /// </summary>
    internal static bool IsGameTeleportTwin(string? pluginId, string? id) =>
        string.Equals((pluginId ?? "").Trim(), SettingsPanel.PluginId, StringComparison.OrdinalIgnoreCase)
        && string.Equals((id ?? "").Trim(), Progenitor.Debugging.DebugMapPolicy.TeleportKeybindId, StringComparison.OrdinalIgnoreCase);

    /// <summary>True when <paramref name="chord"/> may be a bind in <paramref name="scope"/>.</summary>
    internal static bool Allowed(KeyChord chord, KeybindScope scope) => Refusal(chord, scope) == null;

    /// <summary>True when <paramref name="chord"/> may be <paramref name="pluginId"/>/<paramref name="id"/>'s bind in <paramref name="scope"/>.</summary>
    internal static bool Allowed(KeyChord chord, KeybindScope scope, string? pluginId, string? id) =>
        Refusal(chord, scope, pluginId, id) == null;
}
