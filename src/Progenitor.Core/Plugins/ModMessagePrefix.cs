using System;
using System.Collections.Generic;

namespace Progenitor;

/// <summary>
/// WINDOWS1: the automatic <c>&lt;Mod Name&gt;: </c> prefix on what a mod says to the player — <c>ctx.Hud.Notify</c>
/// (the game's banner and the framework band alike), <c>ctx.Chat.Broadcast</c> / <c>SendTo</c>, and a
/// <c>ctx.Commands</c> handler's <c>Reply</c> — so the player can tell which mod spoke. Game-free and tested.
///
/// <para><b>Rules.</b> The name is the plugin's display name (its BepInEx plugin name). No prefix when the caller opted
/// out (<c>prefix: false</c>), when the name is empty, when the text is empty, or when the text already starts with the
/// name (ignoring case and any leading rich-text tags) followed by something that is not a letter or digit — so
/// <c>"Sortmancer: sorted"</c>, <c>"Sortmancer sorted 3 chests"</c> and <c>"[Sortmancer] ..."</c>-style lines the
/// mod already names itself in are left alone, but <c>"Sortmancer2 ..."</c> is not mistaken for the name.</para>
/// </summary>
internal static class ModMessagePrefix
{
    /// <summary>Between the name and the message.</summary>
    internal const string Separator = ": ";

    /// <summary><paramref name="text"/> with <paramref name="modName"/> in front when the rules above call for it.</summary>
    internal static string Apply(string? modName, string? text, bool prefix = true)
    {
        string message = text ?? "";
        string name = (modName ?? "").Trim();
        if (!prefix || name.Length == 0 || message.Trim().Length == 0) return message;
        if (AlreadyNamed(name, message)) return message;
        return name + Separator + message;
    }

    /// <summary>True when <paramref name="text"/> already opens with <paramref name="name"/> as a whole word.</summary>
    internal static bool AlreadyNamed(string name, string text)
    {
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(text)) return false;
        int i = SkipLead(text, 0);
        // "[Name]" and "(Name)" style tags name the mod too.
        if (i < text.Length && (text[i] == '[' || text[i] == '(')) i = SkipLead(text, i + 1);
        if (string.Compare(text, i, name, 0, name.Length, StringComparison.OrdinalIgnoreCase) != 0) return false;
        int end = i + name.Length;
        if (end > text.Length) return false;
        if (end == text.Length) return true;
        return !char.IsLetterOrDigit(text[end]);
    }

    /// <summary>The index after leading white space and rich-text tags (<c>&lt;color=#fff&gt;</c>, <c>&lt;b&gt;</c>).</summary>
    private static int SkipLead(string text, int i)
    {
        while (i < text.Length)
        {
            if (char.IsWhiteSpace(text[i])) { i++; continue; }
            if (text[i] == '<')
            {
                int close = text.IndexOf('>', i + 1);
                if (close > i + 1) { i = close + 1; continue; }
            }

            break;
        }

        return i;
    }

    /// <summary>
    /// A command reply as the pieces it is sent in: the prefix (when it applies) on the first piece only, then split so
    /// each piece fits the chat's 500-byte limit (<see cref="Progenitor.Commands.ReplyText"/>).
    /// </summary>
    internal static string[] ReplyChunks(string? modName, string? text, bool prefix = true)
    {
        string message = Apply(modName, text, prefix);
        return message.Length == 0 ? Array.Empty<string>() : Progenitor.Commands.ReplyText.Chunk(message);
    }

    /// <summary>
    /// The display name and version a BepInEx plugin instance declares in its <c>[BepInPlugin]</c> attribute, read by
    /// reflection (so a test stand-in attribute of the same name works); empty strings when it has none. Not cached: a
    /// cache keyed by a plugin type would hold a collectible plugin generation alive.
    /// </summary>
    internal static (string Name, string Version) PluginIdentity(object? owner)
    {
        if (owner == null) return ("", "");
        var found = ("", "");
        try
        {
            foreach (var attr in owner.GetType().GetCustomAttributes(inherit: true))
            {
                var t = attr.GetType();
                if (!string.Equals(t.Name, "BepInPlugin", StringComparison.Ordinal)) continue;
                string name = t.GetProperty("Name")?.GetValue(attr) as string ?? "";
                object? version = t.GetProperty("Version")?.GetValue(attr);
                found = (name.Trim(), version?.ToString() ?? "");
                break;
            }
        }
        catch
        {
            // Best effort: a plugin without readable metadata gets no prefix.
        }

        return found;
    }

}
