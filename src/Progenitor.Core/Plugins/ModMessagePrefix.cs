using System;
using System.Collections.Generic;
using System.Text;

namespace Progenitor;

/// <summary>
/// WINDOWS1: the automatic <c>&lt;Mod Name&gt;: </c> prefix on what a mod says to the player — <c>ctx.Hud.Notify</c>
/// (the game's banner and the framework band alike), <c>ctx.Chat.Broadcast</c> / <c>SendTo</c>, and a
/// <c>ctx.Commands</c> handler's <c>Reply</c> — so the player can tell which mod spoke. Game-free and tested.
///
/// <para><b>Rules.</b> The name is the plugin's display name (its BepInEx plugin name) with any rich-text tags removed.
/// No prefix when the caller opted out (<c>prefix: false</c>), when the name or the text is empty, or when the text
/// already opens with the mod's own tag: <c>Name:</c>, <c>[Name]</c> or <c>(Name)</c> (ignoring case, leading white
/// space and leading rich-text tags). A text that merely starts with the same word (<c>"Teleport failed"</c> from a mod
/// named Teleport) still gets the prefix.</para>
///
/// <para><b>It identifies, it does not authenticate.</b> The name is whatever the plugin declares; any plugin can write
/// any text, including another mod's name, so a prefix tells the player which mod most likely spoke, not which one
/// did.</para>
/// </summary>
internal static class ModMessagePrefix
{
    /// <summary>Between the name and the message.</summary>
    internal const string Separator = ": ";

    /// <summary><paramref name="text"/> with <paramref name="modName"/> in front when the rules above call for it.</summary>
    internal static string Apply(string? modName, string? text, bool prefix = true)
    {
        string message = text ?? "";
        string name = CleanName(modName);
        if (!prefix || name.Length == 0 || message.Trim().Length == 0) return message;
        if (AlreadyNamed(name, message)) return message;
        return name + Separator + message;
    }

    /// <summary>The name as it is put in front: rich-text tags removed, white space trimmed.</summary>
    internal static string CleanName(string? modName)
    {
        string raw = modName ?? "";
        if (raw.IndexOf('<') < 0) return raw.Trim();
        var sb = new StringBuilder(raw.Length);
        for (int i = 0; i < raw.Length; i++)
        {
            if (raw[i] == '<')
            {
                int close = raw.IndexOf('>', i + 1);
                if (close > i)
                {
                    i = close;
                    continue;
                }
            }

            sb.Append(raw[i]);
        }

        return sb.ToString().Trim();
    }

    /// <summary>True when <paramref name="text"/> already opens with the mod's own tag: <c>Name:</c>, <c>[Name]</c> or <c>(Name)</c>.</summary>
    internal static bool AlreadyNamed(string name, string text)
    {
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(text)) return false;
        int i = SkipLead(text, 0);
        char close = '\0';
        if (i < text.Length && (text[i] == '[' || text[i] == '('))
        {
            close = text[i] == '[' ? ']' : ')';
            i = SkipLead(text, i + 1);
        }

        if (string.Compare(text, i, name, 0, name.Length, StringComparison.OrdinalIgnoreCase) != 0) return false;
        int end = SkipTags(text, i + name.Length);
        if (end >= text.Length) return false;
        return close != '\0' ? text[end] == close : text[end] == ':';
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

    /// <summary>The index after rich-text tags only (a closing <c>&lt;/color&gt;</c> between the name and its colon).</summary>
    private static int SkipTags(string text, int i)
    {
        while (i < text.Length && text[i] == '<')
        {
            int close = text.IndexOf('>', i + 1);
            if (close <= i + 1) break;
            i = close + 1;
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
