using System;
using System.Collections.Generic;

namespace Progenitor.Ui;

/// <summary>KEYS9: one write to a game key slot (<see cref="GameKeyRemap.Writer"/>).</summary>
internal readonly struct GameKeyWrite
{
    internal GameKeyWrite(GameKeybindCatalog.Entry entry, int slot, string? path)
    {
        Entry = entry;
        Slot = slot;
        Path = path;
    }

    /// <summary>The catalog entry: the button action, or the analog axis and its half (a movement direction).</summary>
    internal GameKeybindCatalog.Entry Entry { get; }

    /// <summary>0 primary, 1 secondary.</summary>
    internal int Slot { get; }

    /// <summary>The new control path (<c>&lt;Keyboard&gt;/f</c>), empty to unbind, or null to go back to the game's default.</summary>
    internal string? Path { get; }

    internal bool Reset => Path == null;

    public override string ToString() =>
        (Entry.IsAnalog ? Entry.Analog + (Entry.Positive ? "+" : "-") : Entry.Action) + "#" + (Slot + 1)
        + " -> " + (Path == null ? "default" : Path.Length == 0 ? "unbound" : Path);
}

/// <summary>What a game key write did.</summary>
internal readonly struct GameKeyWriteResult
{
    internal GameKeyWriteResult(bool ok, string status, string? readBack = null, bool saved = true)
    {
        Ok = ok;
        Status = status ?? "";
        ReadBack = readBack;
        Saved = ok && saved;
    }

    /// <summary>The slot reads the new key (or the default after a reset).</summary>
    internal bool Ok { get; }

    /// <summary>The game also saved its input settings; false when the key is applied for this session only.</summary>
    internal bool Saved { get; }

    /// <summary>The row-sized sentence (<c>Saved.</c>, or why not).</summary>
    internal string Status { get; }

    /// <summary>The slot's effective path read back after the write; null when unbound or not read.</summary>
    internal string? ReadBack { get; }
}

/// <summary>
/// KEYS9 (user report 2026-10-10: "I'm trying to change keybinds for the default game in the Mods Keybinds
/// menu and none seem to be changing"). Until KEYS9 the tab's game rows were read-only by design: a cap
/// click only put <see cref="KeybindsTabLayout.GameCapNote"/> in the description column, so every attempt
/// (a plain key, a Shift chord, a mouse button) silently did nothing.
///
/// <para>A game key is now changed through the game's own input settings, the same route the game's
/// Bindings Menu takes: the slot's binding gets an override path and the game saves its input settings
/// (see <c>GameKeybindWriter</c>). This class is the game-free half: which rows can be written, what a
/// game slot can hold, and the sentences.</para>
///
/// <para><b>Clashes.</b> A key Windows or Steam reserves, a bare click and a key a mod holds are refused. Another game
/// action on the same key only warns (the game ships such overlaps and its own page lets the player accept them).</para>
///
/// <para><b>What a game slot can hold.</b> One control: a keyboard key, or the middle, back or forward mouse
/// button. The game's own rebinding waits for exactly one button (it expects a <c>ButtonControl</c>), and
/// none of its keyboard-and-mouse bindings combine a modifier with a key, so a chord with Ctrl, Shift or
/// Alt is refused with <see cref="ModifierReason"/>. A bare left or right click is refused as for every
/// key (KEYS8). A modifier key on its own (Jump from Cliffs is Left Shift) cannot be captured, because a
/// capture of modifiers alone is refused; Reset puts such a default back.</para>
/// </summary>
internal static class GameKeyRemap
{
    /// <summary>The Unity side's writer; null in a game-free host, where no game row can be changed.</summary>
    internal static Func<GameKeyWrite, GameKeyWriteResult>? Writer { get; set; }

    /// <summary>The Bindings Menu's own categories: the actions a player can rebind in the game.</summary>
    private static readonly HashSet<string> PageCategories = new(StringComparer.Ordinal)
    {
        GameKeybindCatalog.Movement, GameKeybindCatalog.Skills, GameKeybindCatalog.General, GameKeybindCatalog.Camera,
        GameKeybindCatalog.Panels, GameKeybindCatalog.ActionBar, GameKeybindCatalog.Building,
    };

    private static Dictionary<string, GameKeybindCatalog.Entry>? _byAction;

    /// <summary>The catalog entry for a game claim's action id, if the catalog lists it.</summary>
    internal static bool TryEntry(string actionId, out GameKeybindCatalog.Entry entry)
    {
        var map = _byAction;
        if (map == null)
        {
            map = new Dictionary<string, GameKeybindCatalog.Entry>(StringComparer.Ordinal);
            foreach (var e in GameKeybindCatalog.Listed)
                if (!map.ContainsKey(e.Action)) map[e.Action] = e;
            _byAction = map;
        }

        return map.TryGetValue((actionId ?? "").Trim(), out entry);
    }

    /// <summary>
    /// A game row this tab can change: an action on the game's own Bindings Menu (the page's seven
    /// categories). The screen-only <c>Other game keys</c>, the debug keys and the mouse axes are not.
    /// </summary>
    internal static bool IsEditable(KeybindClaim claim)
    {
        if (claim == null || claim.Source != KeybindSource.Game) return false;
        if (!TryEntry(claim.ActionId, out var entry)) return false;
        return !entry.NeverConflicts && !entry.WholeAxis && PageCategories.Contains(entry.Category);
    }

    /// <summary>
    /// True while a game key can be written now: a writer is installed and the game's keys were read in this
    /// world (at the title screen the tab shows the last read, which cannot be written).
    /// </summary>
    internal static bool CanWriteNow()
    {
        if (Writer == null) return false;
        try { return KeybindArbiter.GameSnapshot().Available; }
        catch { return false; }
    }

    /// <summary>Why a game row cannot be changed now, or empty when it can.</summary>
    internal static string NotEditableNow(KeybindClaim claim)
    {
        if (!IsEditable(claim)) return OnlyInGameMenuReason;
        if (Writer == null) return KeybindsTabLayout.GameCapNote;
        return CanWriteNow() ? "" : OutsideWorldReason;
    }

    /// <summary>A chord's control path for a game slot, or false when a game slot cannot hold it (<see cref="Refusal"/> says why).</summary>
    internal static bool TryPath(KeyChord chord, out string path)
    {
        path = "";
        if (!chord.IsBound || chord.Modifiers != KeyModifiers.None) return false;
        switch (chord.Key)
        {
            case UiKey.MouseLeft: path = "<Mouse>/leftButton"; return true;
            case UiKey.MouseRight: path = "<Mouse>/rightButton"; return true;
            case UiKey.MouseMiddle: path = "<Mouse>/middleButton"; return true;
            case UiKey.MouseBack: path = "<Mouse>/backButton"; return true;
            case UiKey.MouseForward: path = "<Mouse>/forwardButton"; return true;
        }

        string name = chord.Key.ToString();
        string control = name.StartsWith("Digit", StringComparison.Ordinal) && name.Length == 6
            ? name.Substring(5)
            : char.ToLowerInvariant(name[0]) + name.Substring(1);
        path = "<Keyboard>/" + control;
        return true;
    }

    /// <summary>
    /// Why a game slot cannot hold <paramref name="chord"/> as a key, before the clash checks: a chord with a
    /// modifier. Null when it is one key or mouse button (the bare left and right clicks are refused by the
    /// remap guard, as for every key).
    /// </summary>
    internal static string? Refusal(KeyChord chord)
    {
        if (!chord.IsBound) return null;
        if (chord.Modifiers != KeyModifiers.None) return ModifierReason(chord);
        return null;
    }

    internal static string ModifierReason(KeyChord chord) =>
        chord.DisplayText + " is a combination. The game's keys take one key or mouse button, without Ctrl, Shift or Alt.";

    internal const string OnlyInGameMenuReason = "The game's Controls page doesn't offer this key for rebinding, so it can't be changed here.";

    internal const string OutsideWorldReason = "The game hasn't loaded its controls yet, so game keys can't be changed. Try again in a moment.";

    internal const string SavedStatus = "Saved in the game's controls.";

    /// <summary>The slot has no keyboard binding in the game (an action with one keyboard key has no second slot).</summary>
    internal const string NoSlotReason = "The game has no second keyboard key for this action.";

    /// <summary>KEYS9 review: the key is applied, but the game's settings file was not written.</summary>
    internal static string NotSavedStatus(string error) =>
        "Applied for this session, but the game could not save its controls (" + error + "). The key may go back when the game restarts.";

    /// <summary>KEYS9 review: the writer failed part way; what the row shows after the re-read is what the game has.</summary>
    internal static string WriteErrorStatus(string error) =>
        "The game reported an error while changing the key (" + error + "). The row shows the key the game has now.";

    /// <summary>KEYS9 review: one key on both directions of a movement axis cancels to no movement.</summary>
    internal static string SameAxisReason(KeyChord chord, KeybindClaim holder) =>
        chord.DisplayText + " is already " + holder.DisplayName + ". One key can't move both ways on the same axis. Pick another key.";

    /// <summary>KEYS9 review: <paramref name="holder"/> is another direction (or slot) of the same analog action as <paramref name="claim"/>.</summary>
    internal static bool SameAxis(KeybindClaim claim, KeybindClaim holder)
    {
        if (claim == null || holder == null || holder.Source != KeybindSource.Game || claim.Source != KeybindSource.Game) return false;
        if (holder.ActionId == claim.ActionId && holder.Slot == claim.Slot) return false;
        return TryEntry(claim.ActionId, out var a) && TryEntry(holder.ActionId, out var b)
            && a.IsAnalog && string.Equals(a.Analog, b.Analog, StringComparison.Ordinal);
    }

    internal const string ResetStatus = "Back to the game's default.";

    /// <summary>The edit bar's preview for a game slot: the chord is free, Save writes it into the game's own controls.</summary>
    internal static string PreviewSentence(KeybindClaim claim, KeyChord chord, KeybindClaim? alsoGame = null)
    {
        if (!chord.IsBound) return "Unbind this key: the game action will have no key here until you pick another.";
        if (claim.Comparable && claim.Chord == chord) return chord.DisplayText + " is already this key.";
        if (alsoGame != null)
            return chord.DisplayText + " is also used by " + KeybindRemapGuard.HolderName(alsoGame)
                + ". The game allows this, as its own Controls page does. Save to use it for " + claim.DisplayName + " anyway.";
        return chord.DisplayText + " is free. Save to use it for " + claim.DisplayName + " (in the game's own controls).";
    }

    /// <summary>
    /// Write one slot: <paramref name="chord"/> (unbound to clear), or the game's default when
    /// <paramref name="reset"/>. Refuses without a writer, outside a world, for a row the game's menu does
    /// not list, and for a chord a game slot cannot hold. The remap guard's clash checks are the caller's.
    /// </summary>
    internal static GameKeyWriteResult Store(KeybindClaim claim, int slot, KeyChord chord, bool reset)
    {
        if (!IsEditable(claim) || !TryEntry(claim.ActionId, out var entry)) return new GameKeyWriteResult(false, OnlyInGameMenuReason);
        var writer = Writer;
        if (writer == null) return new GameKeyWriteResult(false, KeybindsTabLayout.GameCapNote);
        if (!CanWriteNow()) return new GameKeyWriteResult(false, OutsideWorldReason);
        string? path;
        if (reset) path = null;
        else if (!chord.IsBound) path = "";
        else
        {
            if (Refusal(chord) is { } why) return new GameKeyWriteResult(false, why);
            if (!TryPath(chord, out string p)) return new GameKeyWriteResult(false, "That key can't be a game key.");
            path = p;
        }

        GameKeyWriteResult result;
        try { result = writer(new GameKeyWrite(entry, slot, path)); }
        catch (Exception e)
        {
            // The write may have been applied before it threw; the tab reads the game's keys again (below), so the row
            // shows what the game has now. Never claim the key is unchanged.
            Plugin.Logger?.LogWarning("Ui: game key " + new GameKeyWrite(entry, slot, path) + " threw: " + e.Message);
            result = new GameKeyWriteResult(false, WriteErrorStatus(e.Message));
        }
        // The game's keys are read again on the next picture, so every row (and every mod key's verdict) sees the change.
        KeybindArbiter.Invalidate(game: true);
        return result;
    }

    internal static void ResetForTests()
    {
        Writer = null;
        _byAction = null;
    }
}
