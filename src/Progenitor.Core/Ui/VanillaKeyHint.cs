using System;
using System.Collections.Generic;

namespace Progenitor.Ui;

/// <summary>
/// What a key-hint row (<see cref="VanillaRow.KeyHint"/>) shows: the game's hint-row form, an optional
/// <i>Hold</i> word, one or two key glyphs (the second after a <c>+</c>) and the action label. Each glyph is an
/// input control path (<c>&lt;Keyboard&gt;/leftAlt</c>, <c>&lt;Mouse&gt;/leftButton</c>, <c>&lt;Gamepad&gt;/buttonNorth</c>)
/// the runtime turns into the game's own glyph sprite; with no path (an unbound key, a chord of several modifiers) the
/// key is drawn as text. Game-free.
/// </summary>
internal readonly struct VanillaKeyHintParts : IEquatable<VanillaKeyHintParts>
{
    internal VanillaKeyHintParts(string prefix, string firstPath, string secondPath, string keyText, string label, bool gamepad)
    {
        Prefix = prefix ?? "";
        FirstPath = firstPath ?? "";
        SecondPath = secondPath ?? "";
        KeyText = keyText ?? "";
        Label = label ?? "";
        Gamepad = gamepad;
    }

    /// <summary><c>Hold</c> for a hold bind, else empty: the game's own word before a held key.</summary>
    internal string Prefix { get; }

    /// <summary>The first glyph's control path (the modifier or the held button of a two-key chord), or empty.</summary>
    internal string FirstPath { get; }

    /// <summary>The second glyph's control path (the key after the <c>+</c>), or empty for a single key.</summary>
    internal string SecondPath { get; }

    /// <summary>The key as words (<c>Alt</c>, <c>Shift + F</c>): the text the cap shows when a glyph cannot be drawn.</summary>
    internal string KeyText { get; }

    internal string Label { get; }

    /// <summary>The chord is a controller chord (the game is showing controller glyphs).</summary>
    internal bool Gamepad { get; }

    /// <summary>Whether there is a key to show at all.</summary>
    internal bool HasKey => KeyText.Length > 0;

    /// <summary>Whether the key can be drawn as glyphs (it has a control path).</summary>
    internal bool HasGlyphs => FirstPath.Length > 0;

    /// <summary>The row in words, as the readback and the log say it: <c>Hold [Alt] Open Legend</c>.</summary>
    internal string Text =>
        (Prefix.Length > 0 ? Prefix + " " : "") + (HasKey ? "[" + KeyText + "] " : "") + Label;

    public bool Equals(VanillaKeyHintParts o) =>
        Prefix == o.Prefix && FirstPath == o.FirstPath && SecondPath == o.SecondPath && KeyText == o.KeyText
        && Label == o.Label && Gamepad == o.Gamepad;

    public override bool Equals(object? obj) => obj is VanillaKeyHintParts p && Equals(p);

    public override int GetHashCode() => HashCode.Combine(Prefix, FirstPath, SecondPath, KeyText, Label, Gamepad);

    public override string ToString() => Text;
}

/// <summary>Where a key-hint row's key came from (<see cref="VanillaKeyHint.Resolve"/>).</summary>
internal enum VanillaKeyHintSource
{
    /// <summary>No key was given.</summary>
    None = 0,

    /// <summary>A bind of the attaching plugin, bound.</summary>
    Bind = 1,

    /// <summary>A bind of the attaching plugin with no key: the row shows no key.</summary>
    UnboundBind = 2,

    /// <summary>A key chord as written that parses (<c>Alt</c>, <c>Shift + F</c>).</summary>
    Chord = 3,

    /// <summary>Neither a bind of the plugin nor a chord: drawn as written, as text, and logged once.</summary>
    Written = 4,
}

/// <summary>
/// The game-free rules of a key-hint row: which chord it names (a bind of the row's plugin by id, following a
/// remap and the controller, or a chord as written) and how that chord becomes glyphs. The Unity half draws them
/// in a copy of the game's own key-hint entry.
/// </summary>
internal static class VanillaKeyHint
{
    /// <summary>The game's word before a held key (its hint rows read <i>Hold [X] ...</i>).</summary>
    internal const string HoldWord = "Hold";

    /// <summary>
    /// The chord <paramref name="key"/> names for <paramref name="pluginId"/>: the bind with that id when the plugin
    /// registered one (its controller chord while the game shows controller glyphs and the bind has one, else its
    /// keyboard or mouse chord; <see cref="KeyChord.None"/> when unbound), else the key parsed as written
    /// (<c>Alt</c>, <c>Shift + F</c>, <c>Mouse0</c>). <paramref name="fromBind"/> says which.
    /// </summary>
    internal static KeyChord Chord(string? key, string? pluginId, IReadOnlyList<IKeybindInfo>? binds, bool controllerActive, out bool fromBind)
    {
        fromBind = false;
        string k = (key ?? "").Trim();
        if (k.Length == 0) return KeyChord.None;
        if (binds != null && !string.IsNullOrEmpty(pluginId))
        {
            foreach (var bind in binds)
            {
                if (bind == null || !string.Equals(bind.PluginId, pluginId, StringComparison.Ordinal)) continue;
                if (!string.Equals(bind.Id, k, StringComparison.Ordinal)) continue;
                fromBind = true;
                return MapShareLogic.HintChord(bind.Chord, bind.GamepadChord, controllerActive);
            }
        }

        return KeyChord.TryParse(k, out var chord) ? chord : KeyChord.None;
    }

    /// <summary>
    /// The parts a key-hint row shows for <paramref name="key"/>: a bind of <paramref name="pluginId"/> (bound: its
    /// chord; unbound: no key at all, never the bind's id), else a chord as written, else the key as written, as text.
    /// <paramref name="source"/> says which, so the caller can log a key that is neither a bind nor a chord.
    /// </summary>
    internal static VanillaKeyHintParts Resolve(
        string? key, string? label, bool hold, string? pluginId, IReadOnlyList<IKeybindInfo>? binds, bool controllerActive,
        out VanillaKeyHintSource source)
    {
        string k = (key ?? "").Trim();
        var chord = Chord(k, pluginId, binds, controllerActive, out bool fromBind);
        if (fromBind)
        {
            source = chord.IsBound ? VanillaKeyHintSource.Bind : VanillaKeyHintSource.UnboundBind;
            return Parts(chord, "", label, hold);
        }

        source = k.Length == 0 ? VanillaKeyHintSource.None : chord.IsBound ? VanillaKeyHintSource.Chord : VanillaKeyHintSource.Written;
        return Parts(chord, k, label, hold);
    }

    /// <summary>
    /// The fallback key text when a glyph cannot be drawn: the game's own text for each control path when the game
    /// gave one for every path (joined by <c> + </c>), else the chord's own words.
    /// </summary>
    internal static string FallbackText(VanillaKeyHintParts parts, string? firstGameText, string? secondGameText)
    {
        string a = (firstGameText ?? "").Trim();
        string b = (secondGameText ?? "").Trim();
        if (parts.HasGlyphs && a.Length > 0 && (parts.SecondPath.Length == 0 || b.Length > 0))
            return parts.SecondPath.Length == 0 ? a : a + " + " + b;
        return parts.KeyText;
    }

    /// <summary>How many times a draw that fell back to text is tried again before it waits for a keybind change.</summary>
    internal const int MaxGlyphRetries = 10;

    /// <summary>
    /// Whether a row drawn with <paramref name="last"/> needs drawing for <paramref name="parts"/>: always when the parts
    /// changed or the keybinds' version moved; a complete draw never again; a text fallback again while it has retries
    /// left (<see cref="MaxGlyphRetries"/>, counted since the last parts or version change).
    /// </summary>
    internal static bool ShouldDraw(bool drawn, bool complete, VanillaKeyHintParts last, VanillaKeyHintParts parts,
        int lastVersion, int version, int retries)
    {
        if (!drawn || !parts.Equals(last) || version != lastVersion) return true;
        if (complete) return false;
        return retries < MaxGlyphRetries;
    }

    /// <summary>
    /// The parts for <paramref name="chord"/>: a lone modifier is its own key (<c>Alt</c> is the Alt cap); a key with
    /// one modifier is two glyphs, the modifier then the key; a controller chord is its held button then its button;
    /// a chord with several modifiers is text only. <paramref name="written"/> is what the row shows when the key is
    /// neither a bind nor a chord that parses (it is drawn as written, as text).
    /// </summary>
    internal static VanillaKeyHintParts Parts(KeyChord chord, string? written, string? label, bool hold)
    {
        string prefix = hold ? HoldWord : "";
        string l = label ?? "";
        if (!chord.IsBound)
        {
            string w = (written ?? "").Trim();
            return new VanillaKeyHintParts(prefix, "", "", w, l, false);
        }

        string text = chord.DisplayText;
        if (chord.IsGamepad)
        {
            string key = GamepadKeybindCatalog.PathOf(chord.Key);
            if (chord.Held != UiKey.None)
            {
                string held = GamepadKeybindCatalog.PathOf(chord.Held);
                return held.Length > 0 && key.Length > 0
                    ? new VanillaKeyHintParts(prefix, held, key, text, l, true)
                    : new VanillaKeyHintParts(prefix, "", "", text, l, true);
            }

            return new VanillaKeyHintParts(prefix, key, "", text, l, true);
        }

        string main = PathOf(chord.Key);
        if (chord.Modifiers == KeyModifiers.None)
            return new VanillaKeyHintParts(prefix, main, "", text, l, false);
        if (!KeyChord.IsSingleModifier(chord.Modifiers) || main.Length == 0)
            return new VanillaKeyHintParts(prefix, "", "", text, l, false);
        string mod = PathOf(KeyChord.LeftKeyOf(chord.Modifiers));
        return mod.Length > 0
            ? new VanillaKeyHintParts(prefix, mod, main, text, l, false)
            : new VanillaKeyHintParts(prefix, "", "", text, l, false);
    }

    /// <summary>A single keyboard key's or mouse button's control path (<c>&lt;Keyboard&gt;/leftAlt</c>), or empty.</summary>
    internal static string PathOf(UiKey key)
    {
        if (key == UiKey.None || KeyChord.IsGamepadKey(key)) return "";
        return GameKeyRemap.TryPath(new KeyChord(key), out string path) ? path : "";
    }

    /// <summary>
    /// The readback word for one glyph: <c>sprite:&lt;name&gt;</c> when the game's glyph was drawn, else
    /// <c>text:&lt;key&gt;</c>.
    /// </summary>
    internal static string GlyphWord(string? sprite, string keyText) =>
        !string.IsNullOrEmpty(sprite) ? "sprite:" + sprite : "text:" + (keyText ?? "");
}

/// <summary>A key-hint row's state for <c>ui_read panels</c> (the Unity half implements it on its row).</summary>
internal interface IKeyHintRowReadback
{
    /// <summary>The parts last drawn.</summary>
    VanillaKeyHintParts Parts { get; }

    /// <summary>What each drawn glyph is (<see cref="VanillaKeyHint.GlyphWord"/>), in order.</summary>
    string[] Glyphs { get; }
}
