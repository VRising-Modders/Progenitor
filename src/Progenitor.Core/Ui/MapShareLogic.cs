using System;
using System.Collections.Generic;
using System.Text;
using Progenitor.Chat;

namespace Progenitor.Ui;

/// <summary>The key glyph a map share key tip draws: the game's mouse button art, or the key's name as text.</summary>
internal enum MapShareGlyph
{
    /// <summary>No glyph: the key's name is drawn as text (<see cref="MapShareHintParts.KeyText"/>).</summary>
    None,

    /// <summary>The game's left mouse button glyph.</summary>
    MouseLeft,

    /// <summary>The game's right mouse button glyph.</summary>
    MouseRight,
}

/// <summary>What the map share key tip shows, from the bound key (<see cref="MapShareLogic.Hint"/>).</summary>
internal readonly struct MapShareHintParts
{
    internal MapShareHintParts(string prefix, MapShareGlyph glyph, string keyText, string action)
    {
        Prefix = prefix ?? "";
        Glyph = glyph;
        KeyText = keyText ?? "";
        Action = action ?? "";
    }

    /// <summary>The words before a mouse glyph (<c>Shift +</c>); empty for a text key.</summary>
    internal string Prefix { get; }

    /// <summary>The mouse glyph, or <see cref="MapShareGlyph.None"/> for a key drawn as text.</summary>
    internal MapShareGlyph Glyph { get; }

    /// <summary>The key as text (<c>Ctrl + K</c>) when there is no glyph; empty otherwise.</summary>
    internal string KeyText { get; }

    /// <summary>The caption after the key.</summary>
    internal string Action { get; }

    /// <summary>The whole tip as it reads, a glyph written as [LMB] or [RMB] (logs, the readback, tests).</summary>
    internal string Text
    {
        get
        {
            string key = Glyph switch
            {
                MapShareGlyph.MouseLeft => (Prefix.Length > 0 ? Prefix + " " : "") + "[LMB]",
                MapShareGlyph.MouseRight => (Prefix.Length > 0 ? Prefix + " " : "") + "[RMB]",
                _ => "[" + KeyText + "]",
            };
            return key + " " + Action;
        }
    }
}

/// <summary>One entry of the map's key-hint row, as <see cref="MapShareLogic.PickDonor"/> sees it.</summary>
internal readonly struct MapHintEntry
{
    internal MapHintEntry(bool active, string action, bool iconShown, string sprite, bool keyTextShown)
    {
        Active = active;
        Action = action ?? "";
        IconShown = iconShown;
        Sprite = sprite ?? "";
        KeyTextShown = keyTextShown;
    }

    /// <summary>The entry is shown.</summary>
    internal bool Active { get; }

    /// <summary>Its caption, as drawn (localized).</summary>
    internal string Action { get; }

    /// <summary>Its key glyph image is on.</summary>
    internal bool IconShown { get; }

    /// <summary>The glyph's sprite name.</summary>
    internal string Sprite { get; }

    /// <summary>Its key name text is on (a keyboard key).</summary>
    internal bool KeyTextShown { get; }
}

/// <summary>
/// Share a map location to chat, game-free (the Unity half is <see cref="MapShare"/>). A click of the map share key
/// (default Shift + left mouse, a map-scope Core keybind) on the open world map closes the map and puts
/// <c>[loc:x,z]</c> for the clicked spot at the end of the chat input, for the player to finish and send. The game's map
/// does nothing on a left click with Shift alone: its marker is the right click, its admin teleport Ctrl + Shift + left
/// click.
/// </summary>
internal static class MapShareLogic
{
    /// <summary>The keybind id (Core's <c>[Keybinds]</c> section in <c>progenitor.ui.cfg</c>).</summary>
    internal const string KeybindId = "ShareMapLocation";

    /// <summary>The Keybinds tab's description.</summary>
    internal const string KeybindDescription =
        "Share map location: click the open world map to put that spot's location in chat.";

    /// <summary>The default key: Shift + left mouse.</summary>
    internal static readonly KeyChord DefaultKey = new(UiKey.MouseLeft, KeyModifiers.Shift);

    /// <summary>The tip's caption, in the game's title case (<i>Add/Remove Marker</i>, <i>Open Legend</i>).</summary>
    internal const string Action = "Share Location";

    /// <summary>
    /// How far (screen pixels) the pointer may move between press and release for the press to count as a click: the
    /// Unity event system's default drag threshold. A longer move is a pan, which shares nothing.
    /// </summary>
    internal const float DragThresholdPixels = 10f;

    /// <summary>The game's left mouse glyph sprite name prefix (seen live in the inventory row: <c>Icon_LMB1</c>).</summary>
    internal const string LeftMouseSpritePrefix = "Icon_LMB";

    /// <summary>The right mouse glyph sprite name prefix, by the left one's naming (not seen live).</summary>
    internal const string RightMouseSpritePrefix = "Icon_RMB";

    /// <summary>The chat token for a world point: <c>[loc:x,z]</c>, whole meters (<see cref="ChatTokens.Location"/>).</summary>
    internal static string Token(float worldX, float worldZ) => ChatTokens.Location(worldX, worldZ);

    /// <summary>The chat line after a share: the token after the draft, with a space when needed.</summary>
    internal static string Draft(string? current, float worldX, float worldZ) =>
        ChatTabInput.JoinInsert(current, Token(worldX, worldZ));

    /// <summary>Whether the key tip is on screen: installed, the map open, the key bound.</summary>
    internal static bool ShouldShowHint(bool installed, bool mapOpen, KeyChord chord) => installed && mapOpen && chord.IsBound;

    /// <summary>
    /// The tip for <paramref name="chord"/>: a left or right click shows the game's mouse glyph after its modifiers
    /// (<c>Shift + [LMB] Share Location</c>); any other key shows its name as text (<c>[Ctrl + K] Share Location</c>).
    /// </summary>
    internal static MapShareHintParts Hint(KeyChord chord)
    {
        string mods = ModifierWords(chord.Modifiers);
        if (chord.Key == UiKey.MouseLeft || chord.Key == UiKey.MouseRight)
        {
            var glyph = chord.Key == UiKey.MouseLeft ? MapShareGlyph.MouseLeft : MapShareGlyph.MouseRight;
            return new MapShareHintParts(mods.Length > 0 ? mods + " +" : "", glyph, "", Action);
        }

        string key = KeyChord.KeyDisplayName(chord.Key);
        return new MapShareHintParts("", MapShareGlyph.None, mods.Length > 0 ? mods + " + " + key : key, Action);
    }

    /// <summary>The modifiers in the order the game writes them: <c>Ctrl + Shift + Alt</c>.</summary>
    internal static string ModifierWords(KeyModifiers modifiers)
    {
        var sb = new StringBuilder();
        void Add(string word)
        {
            if (sb.Length > 0) sb.Append(" + ");
            sb.Append(word);
        }

        if ((modifiers & KeyModifiers.Ctrl) != 0) Add("Ctrl");
        if ((modifiers & KeyModifiers.Shift) != 0) Add("Shift");
        if ((modifiers & KeyModifiers.Alt) != 0) Add("Alt");
        return sb.ToString();
    }

    /// <summary>The sprite prefix a glyph needs, or null for text.</summary>
    internal static string? SpritePrefix(MapShareGlyph glyph) => glyph switch
    {
        MapShareGlyph.MouseLeft => LeftMouseSpritePrefix,
        MapShareGlyph.MouseRight => RightMouseSpritePrefix,
        _ => null,
    };

    /// <summary>
    /// Which entry of the row to copy for <paramref name="glyph"/>, or -1. A glyph tip needs an entry whose glyph is on and
    /// shows that mouse button (the tip's glyph comes from the donor). A text tip prefers an entry that draws its key as
    /// text, then any entry with a glyph on. Shown entries rank before hidden (pooled) ones.
    /// </summary>
    internal static int PickDonor(IReadOnlyList<MapHintEntry> entries, MapShareGlyph glyph)
    {
        if (entries == null) return -1;
        string? prefix = SpritePrefix(glyph);
        int best = -1, bestRank = int.MaxValue;
        for (int i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            int rank;
            if (prefix != null)
            {
                if (!e.IconShown || !e.Sprite.StartsWith(prefix, StringComparison.Ordinal)) continue;
                rank = e.Active ? 0 : 1;
            }
            else
            {
                if (e.KeyTextShown) rank = e.Active ? 0 : 2;
                else if (e.IconShown) rank = e.Active ? 1 : 3;
                else continue;
            }

            if (rank < bestRank)
            {
                best = i;
                bestRank = rank;
            }
        }

        return best;
    }

    /// <summary>
    /// The entry the tip goes right after, or -1 for the row's start: the shown marker entry (its caption names a
    /// marker, <i>Add/Remove Marker</i>), else the last shown entry, else the last entry.
    /// </summary>
    internal static int PlaceAfter(IReadOnlyList<MapHintEntry> entries)
    {
        if (entries == null || entries.Count == 0) return -1;
        int lastActive = -1;
        for (int i = 0; i < entries.Count; i++)
        {
            if (!entries[i].Active) continue;
            if (entries[i].Action.IndexOf("Marker", StringComparison.OrdinalIgnoreCase) >= 0) return i;
            lastActive = i;
        }

        return lastActive >= 0 ? lastActive : entries.Count - 1;
    }

    /// <summary>
    /// The sibling index that puts the tip right after the entry at <paramref name="afterIndex"/>, given the tip is at
    /// <paramref name="tipIndex"/> now (indexes counted with the tip in the row).
    /// </summary>
    internal static int SiblingAfter(int afterIndex, int tipIndex)
    {
        if (tipIndex == afterIndex + 1) return tipIndex;
        return tipIndex < afterIndex ? afterIndex : afterIndex + 1;
    }
}

/// <summary>
/// One map share click, game-free: the press keeps the map point under the pointer; the release shares it when the map
/// is still open and the pointer has not moved past the drag threshold (a drag pans the map, as the game's own click
/// does nothing after a drag).
/// </summary>
internal sealed class MapShareClick
{
    private bool _armed;
    private float _sx, _sy, _wx, _wz;

    /// <summary>A press is waiting for its release.</summary>
    internal bool Armed => _armed;

    /// <summary>The press: armed only when the pointer is on the map and the point has world coordinates.</summary>
    internal bool Press(bool onMap, bool hasWorld, float screenX, float screenY, float worldX, float worldZ)
    {
        _armed = onMap && hasWorld && Finite(worldX) && Finite(worldZ);
        _sx = screenX;
        _sy = screenY;
        _wx = worldX;
        _wz = worldZ;
        return _armed;
    }

    /// <summary>
    /// The release. True (with the press's world point) when armed, the map is still open and the pointer, when it can
    /// be read, is within <see cref="MapShareLogic.DragThresholdPixels"/> of the press. Disarms either way.
    /// </summary>
    internal bool Release(bool mapOpen, bool hasPointer, float screenX, float screenY, out float worldX, out float worldZ)
    {
        worldX = _wx;
        worldZ = _wz;
        bool was = _armed;
        _armed = false;
        if (!was || !mapOpen) return false;
        if (!hasPointer) return true;
        float dx = screenX - _sx, dy = screenY - _sy;
        return dx * dx + dy * dy <= MapShareLogic.DragThresholdPixels * MapShareLogic.DragThresholdPixels;
    }

    /// <summary>Forget a press.</summary>
    internal void Cancel() => _armed = false;

    private static bool Finite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);
}
