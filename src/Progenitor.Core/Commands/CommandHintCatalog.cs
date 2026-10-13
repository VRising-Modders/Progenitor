using System;
using System.Collections.Generic;
using System.Text;

namespace Progenitor.Commands;

/// <summary>One chat command a player can run, as the chat box's command hints show it.</summary>
internal sealed class CommandHintEntry
{
    public CommandHintEntry(string command, string usage, string help)
    {
        Command = command ?? "";
        Usage = usage ?? "";
        Help = help ?? "";
    }

    /// <summary>The exact text to type, prefix included: the root shortcut when it is enabled, else the qualified form.</summary>
    public string Command { get; }

    /// <summary>The usage line of every overload this player may run, joined with <c> | </c>.</summary>
    public string Usage { get; }

    /// <summary>One line of help (the command's description), or empty.</summary>
    public string Help { get; }
}

/// <summary>
/// Chat command hints, server side and game-free: the commands one player can run, built from the live
/// <see cref="CommandRegistry"/> table. A command is listed only when at least one of its overloads is usable by this
/// player (an admin-only overload is never shown to a non-admin, and its usage is left out of a mixed command), and
/// its text is the route that player's line will actually reach: the root shortcut when the routing table published
/// one, otherwise the qualified form under the fallback root (a shortcut disabled by the administrator or by a
/// collision). Sorted by command text, capped at <see cref="MaxEntries"/>.
/// </summary>
internal static class CommandHintCatalog
{
    /// <summary>The most commands one catalog lists.</summary>
    internal const int MaxEntries = 200;

    /// <summary>The most characters of one help line.</summary>
    internal const int MaxHelpChars = 120;

    /// <summary>The most characters of one command text.</summary>
    internal const int MaxCommandChars = 80;

    /// <summary>The most characters of one usage line.</summary>
    internal const int MaxUsageChars = 160;

    /// <summary>The catalog for one player, from the live registry.</summary>
    internal static List<CommandHintEntry> Build(bool isAdmin, bool listedAdmin = false)
    {
        CommandRegistry.EnsureBuiltIns();
        return Build(CommandRegistry.ListActive(), isAdmin, CommandRegistry.Prefix, CommandRegistry.FallbackRoot, listedAdmin);
    }

    /// <summary>The catalog for one player from <paramref name="bundles"/> (the routing table's live set).</summary>
    internal static List<CommandHintEntry> Build(IReadOnlyList<CommandBundle> bundles, bool isAdmin, string prefix, string fallbackRoot, bool listedAdmin = false)
    {
        var list = new List<CommandHintEntry>();
        if (bundles == null) return list;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var bundle in bundles)
        {
            if (bundle == null || !bundle.VisibleTo(isAdmin, listedAdmin)) continue;
            string command = Clip(CommandText(bundle, prefix, fallbackRoot), MaxCommandChars);
            if (command.Length == 0 || !seen.Add(command)) continue;

            var usages = new List<string>();
            foreach (var overload in bundle.Overloads)
            {
                if (!overload.UsableBy(isAdmin, listedAdmin)) continue;
                string usage = OneLine(UsageOf(bundle, overload, prefix, fallbackRoot));
                if (usage.Length > 0 && !usages.Contains(usage)) usages.Add(usage);
            }

            list.Add(new CommandHintEntry(command, Clip(string.Join(" | ", usages), MaxUsageChars), Clip(OneLine(HelpOf(bundle, isAdmin, listedAdmin)), MaxHelpChars)));
        }

        list.Sort((a, b) => string.Compare(a.Command, b.Command, StringComparison.OrdinalIgnoreCase));
        if (list.Count > MaxEntries) list.RemoveRange(MaxEntries, list.Count - MaxEntries);
        return list;
    }

    /// <summary>The text to type: <c>.group shortcut</c> / <c>.shortcut</c> when published, else <c>.root owner [group] name</c>.</summary>
    internal static string CommandText(CommandBundle bundle, string prefix, string fallbackRoot)
    {
        if (bundle.ShortcutsEnabled && !string.IsNullOrEmpty(bundle.EffectiveShortcut))
            return string.IsNullOrEmpty(bundle.GroupName)
                ? prefix + bundle.EffectiveShortcut
                : prefix + bundle.GroupName + " " + bundle.EffectiveShortcut;
        return prefix + fallbackRoot + " " + bundle.QualifiedPath;
    }

    /// <summary>
    /// The overload's usage: the author's override when set, else the command text and its parameters
    /// (<c>&lt;required&gt;</c>, <c>[optional]</c>, <c>...</c> for the rest of the line), the way <c>.help</c> prints it.
    /// </summary>
    internal static string UsageOf(CommandBundle bundle, CommandOverload overload, string prefix, string fallbackRoot)
    {
        if (!string.IsNullOrEmpty(overload.Attribute.Usage)) return overload.Attribute.Usage!;
        var sb = new StringBuilder(CommandText(bundle, prefix, fallbackRoot));
        var parameters = overload.Parameters;
        for (int i = 0; i < parameters.Length; i++)
        {
            var p = parameters[i];
            bool optional = p.HasDefaultValue;
            bool remainder = overload.HasRemainder && i == parameters.Length - 1;
            string name = p.Name ?? "arg";
            sb.Append(' ');
            if (remainder) sb.Append(optional ? "[" + name + "...]" : "<" + name + "...>");
            else sb.Append(optional ? "[" + name + "]" : "<" + name + ">");
        }

        return sb.ToString();
    }

    private static string HelpOf(CommandBundle bundle, bool isAdmin, bool listedAdmin)
    {
        // The first description on an overload this player can run, so a non-admin never reads an admin form's help.
        foreach (var overload in bundle.Overloads)
        {
            if (!overload.UsableBy(isAdmin, listedAdmin)) continue;
            if (!string.IsNullOrEmpty(overload.Attribute.Description)) return overload.Attribute.Description!;
        }

        return "";
    }

    /// <summary>Line breaks and tabs become spaces; runs of spaces collapse; trimmed.</summary>
    internal static string OneLine(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var sb = new StringBuilder(text!.Length);
        bool space = false;
        foreach (char c in text)
        {
            bool ws = c == ' ' || c == '\t' || c == '\r' || c == '\n' || char.IsControl(c);
            if (ws)
            {
                space = sb.Length > 0;
                continue;
            }

            if (space) sb.Append(' ');
            space = false;
            sb.Append(c);
        }

        return sb.ToString();
    }

    /// <summary>At most <paramref name="max"/> characters; a clipped line ends in <c>...</c>.</summary>
    internal static string Clip(string? text, int max)
    {
        if (string.IsNullOrEmpty(text) || max <= 0) return "";
        if (text!.Length <= max) return text;
        if (max <= 3) return text.Substring(0, max);
        int cut = max - 3;
        if (char.IsHighSurrogate(text[cut - 1])) cut--;
        return text.Substring(0, cut).TrimEnd() + "...";
    }
}
