using System;
using System.Collections.Generic;

namespace Progenitor.Ui;

/// <summary>
/// HUB1: the words a button's key hint (<see cref="UiWidgets.KeyHint"/>) shows, and its cap's size. A hint names one of
/// three things: a keybind the button's plugin registered with <c>ctx.Keybinds</c> (its id: the hint shows the bind's
/// current key and follows a remap), a game action as <c>game:Action</c> (the game's own key text for it, the source the
/// game's key-hint rows read, following the player's game bindings), or the key itself (<c>Esc</c>, <c>Enter</c>), shown
/// as written. Game-free: <c>UiHubControlsTests</c>.
/// </summary>
internal static class UiKeyHint
{
    /// <summary>The prefix that names a game action.</summary>
    internal const string GamePrefix = "game:";

    /// <summary>The cap's text size, height and side padding (the world-pick band's key cap: the kit's one key-cap look).</summary>
    internal const float FontSize = 16f;

    internal const float CapHeight = 26f;

    internal const float CapPadX = 8f;

    /// <summary>Between the cap and the button's caption, and between the cap and the button's right edge.</summary>
    internal const float Gap = 8f;

    internal const float RightInset = 10f;

    /// <summary>The longest hint, so a cap never takes over its button.</summary>
    internal const int MaxLength = 16;

    /// <summary>
    /// The hint's words for <paramref name="hint"/> on a button of <paramref name="pluginId"/>: the bind's key when the hint
    /// is the id of a bind that plugin registered (empty when unbound, which hides the cap), the game's key text for a
    /// <c>game:</c> action (empty when the game has none to give), else the hint as written.
    /// </summary>
    internal static string Resolve(string? hint, string? pluginId, IReadOnlyList<IKeybindInfo>? binds, Func<string, string?>? gameAction)
    {
        string h = (hint ?? "").Trim();
        if (h.Length == 0) return "";
        if (h.StartsWith(GamePrefix, StringComparison.OrdinalIgnoreCase))
        {
            string action = h.Substring(GamePrefix.Length).Trim();
            string? text = null;
            try { text = action.Length == 0 ? null : gameAction?.Invoke(action); }
            catch { text = null; }
            return Clip((text ?? "").Trim());
        }

        if (binds != null && !string.IsNullOrEmpty(pluginId))
            foreach (var bind in binds)
            {
                if (bind == null || !string.Equals(bind.PluginId, pluginId, StringComparison.Ordinal)) continue;
                if (!string.Equals(bind.Id, h, StringComparison.Ordinal)) continue;
                return !bind.Chord.IsBound ? "" : Clip(bind.Chord.DisplayText);
            }

        return Clip(h);
    }

    /// <summary>Whether <paramref name="hint"/> follows something that can change (a bind or a game action), so it is re-read.</summary>
    internal static bool Follows(string? hint, string? pluginId, IReadOnlyList<IKeybindInfo>? binds)
    {
        string h = (hint ?? "").Trim();
        if (h.StartsWith(GamePrefix, StringComparison.OrdinalIgnoreCase)) return true;
        if (binds == null || string.IsNullOrEmpty(pluginId)) return false;
        foreach (var bind in binds)
            if (bind != null && string.Equals(bind.PluginId, pluginId, StringComparison.Ordinal) && string.Equals(bind.Id, h, StringComparison.Ordinal))
                return true;
        return false;
    }

    /// <summary>The cap's width for <paramref name="text"/> (0 for none): its text and pads, never narrower than it is tall.</summary>
    internal static float CapWidth(string? text) =>
        string.IsNullOrEmpty(text) ? 0f : Math.Max(CapHeight, (float)Math.Ceiling(UiChipsText.Width(text, FontSize) + 2f * CapPadX));

    /// <summary>The room a button keeps at its right for a cap of <paramref name="text"/>: the cap, the gap before it and its inset.</summary>
    internal static float Room(string? text)
    {
        float cap = CapWidth(text);
        return cap <= 0f ? 0f : cap + Gap + RightInset;
    }

    /// <summary>
    /// The game's own text for <paramref name="action"/>'s key (its primary slot, as the game's key-hint rows and the
    /// Keybinds tab draw it), or null when the game has not given its bindings yet or the action is unbound.
    /// </summary>
    internal static string? GameActionText(string action)
    {
        try
        {
            var snapshot = KeybindArbiter.GameSnapshot();
            if (!snapshot.Available) return null;
            foreach (var claim in snapshot.Claims)
            {
                if (claim.Source != KeybindSource.Game || claim.Slot != 0) continue;
                if (!string.Equals(claim.ActionId, action, StringComparison.OrdinalIgnoreCase)) continue;
                string text = claim.ChordText ?? "";
                if (!string.IsNullOrWhiteSpace(text) && !string.Equals(text, "None", StringComparison.Ordinal)) return text.Trim();
            }
        }
        catch { /* the game's bindings are not readable now */ }

        return null;
    }

    private static string Clip(string text) => text.Length <= MaxLength ? text : text.Substring(0, MaxLength);
}
