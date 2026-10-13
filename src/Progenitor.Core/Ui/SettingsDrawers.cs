using System;
using System.Collections.Generic;

namespace Progenitor.Ui;

/// <summary>
/// Three homes (docs/settings-organization.md, "Layout rules"; decided 2026-10-06): only a group with more than
/// <see cref="Threshold"/> settings is a collapsible drawer. Its header shows the group name and its row count; its open
/// or closed state is remembered per drawer in <c>[Ui] ClosedDrawers</c> of Core's <c>progenitor.ui.cfg</c>
/// (plain text: the closed drawers' keys, so every drawer is open the first time). Read on every use, so a Core reload
/// or a restart finds it again. Game-free.
/// </summary>
internal static class SettingsDrawers
{
    /// <summary>A group is a drawer when it has more settings than this (the user's "more than four").</summary>
    internal const int Threshold = 4;

    internal const string Section = "Ui";
    internal const string Key = "ClosedDrawers";

    internal const string Description =
        "The settings drawers you closed in the Mods menu (Preferences, Server, Progenitor's page), separated by commas, as "
        + "<tab>/<group>. Empty: every drawer is open. Changed by the drawers themselves; nothing to set by hand.";

    /// <summary>The drawer scope of each home (a drawer key is <c>&lt;scope&gt;/&lt;group slug&gt;</c>).</summary>
    internal const string ScopePreferences = "preferences";
    internal const string ScopeServer = "server";
    internal const string ScopeProgenitor = "progenitor";

    /// <summary>Without a bound entry (a server, tests that do not bind one): this session's memory.</summary>
    private static string _memory = "";

    /// <summary>Whether an item is one of its group's settings: not a divider, a link, or an item marked <c>Uncounted</c>.</summary>
    internal static bool Counts(ModSettingsItem? item) =>
        item != null && item.Kind is not (ModSettingsItemKind.Divider or ModSettingsItemKind.Link) && !item.Options.Uncounted;

    /// <summary>The settings a section declares (what decides whether it is a drawer).</summary>
    internal static int CountOf(ModSettingsSection? section)
    {
        if (section == null) return 0;
        int n = 0;
        foreach (var item in section.Items) if (Counts(item)) n++;
        return n;
    }

    /// <summary>True for a group of more than <see cref="Threshold"/> settings.</summary>
    internal static bool IsDrawer(int settings) => settings > Threshold;

    /// <summary>A drawer's key: <c>&lt;scope&gt;/&lt;group slug&gt;</c>.</summary>
    internal static string KeyOf(string scope, string group) => (scope ?? "") + "/" + SectionNav.Slug(group);

    /// <summary>The stored text (the entry's value, else this session's memory).</summary>
    internal static string Stored
    {
        get
        {
            var entry = CoreUiSettings.ClosedDrawers;
            if (entry == null) return _memory;
            try { return entry.Value ?? ""; }
            catch { return _memory; }
        }
    }

    /// <summary>The closed drawers' keys in a stored text: separated by commas, semicolons or spaces, compared without case.</summary>
    internal static HashSet<string> Parse(string? text)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string part in (text ?? "").Split(new[] { ',', ';', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string key = part.Trim();
            if (key.Length > 0) set.Add(key);
        }

        return set;
    }

    /// <summary>The stored text for a set: sorted, so the file only changes when the set does.</summary>
    internal static string Format(IEnumerable<string> closed)
    {
        var list = new List<string>(closed ?? Array.Empty<string>());
        list.Sort(StringComparer.OrdinalIgnoreCase);
        return string.Join(",", list);
    }

    /// <summary>True unless the player closed this drawer (first time: open).</summary>
    internal static bool IsOpen(string key) => !Parse(Stored).Contains(key ?? "");

    /// <summary>Remember the drawer open or closed. True when the stored text changed.</summary>
    internal static bool SetOpen(string key, bool open)
    {
        if (string.IsNullOrEmpty(key)) return false;
        var set = Parse(Stored);
        bool changed = open ? set.Remove(key) : set.Add(key);
        if (!changed) return false;
        string text = Format(set);
        var entry = CoreUiSettings.ClosedDrawers;
        if (entry != null)
        {
            try
            {
                entry.Value = text;
                return true;
            }
            catch (Exception e) { Plugin.Logger?.LogWarning("Ui: [Ui] " + Key + " could not be saved: " + e.Message); }
        }

        _memory = text;
        return true;
    }

    /// <summary>Tests: forget this session's memory.</summary>
    internal static void ResetMemory() => _memory = "";
}
