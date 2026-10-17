using System;
using System.IO;
using BepInEx.Configuration;

namespace Progenitor.Dye;

/// <summary>
/// Binds one cfg entry. Core binds through <c>ConfigFile.Bind</c> (<see cref="DyeAllFeature"/>); the tests bind
/// through the config-file shim.
/// </summary>
internal interface IDyeAllBinder
{
    ConfigEntry<bool> Flag(string section, string key, bool value, string description);
}

/// <summary>
/// Dye all's entries in Core's own client cfg (<c>progenitor.ui.cfg</c>, section <c>[DyeAll]</c>). All local to
/// the client, never server-synced. <see cref="Enabled"/> is drawn on Progenitor's page (group <c>Equipment</c>);
/// the rest stay in the file. A hold is always one second and dyes only what shows: hold time and dyeing the piece
/// under a transmog were settings, and the page no longer offers them.
/// </summary>
internal sealed class DyeAllEntries
{
    /// <summary>The cfg section in <c>progenitor.ui.cfg</c>.</summary>
    internal const string Section = "DyeAll";

    internal const string EnabledKey = "Enabled";
    internal const string MatchPalettesKey = "MatchPalettes";
    internal const string LegacyImportedKey = "LegacySettingsImported";

    private DyeAllEntries(ConfigEntry<bool> enabled, ConfigEntry<bool> matchPalettes, ConfigEntry<bool> legacyImported)
    {
        Enabled = enabled;
        MatchPalettes = matchPalettes;
        LegacyImported = legacyImported;
    }

    /// <summary>On (default): holding a swatch dyes every piece that shows. Off: swatches behave exactly as in the game.</summary>
    internal ConfigEntry<bool> Enabled { get; }

    /// <summary>
    /// Experimental, off by default, cfg only: read each piece's palette list (the item's
    /// <c>TransmogAssetKeyBlobData</c> blob) so a color keeps its palette across pieces whose option
    /// lists differ. Off sends the swatch's index to every piece.
    /// </summary>
    internal ConfigEntry<bool> MatchPalettes { get; }

    /// <summary>True once the old plugin's <c>progenitor.dyeall.cfg</c> was looked for (and copied when found).</summary>
    internal ConfigEntry<bool> LegacyImported { get; }

    /// <summary>How long a swatch must be held. Always one second; there is no setting for it.</summary>
    internal double HoldTime => DyeHoldMachine.DefaultHoldSeconds;

    /// <summary>Bind every entry.</summary>
    internal static DyeAllEntries Bind(IDyeAllBinder bind)
    {
        if (bind == null) throw new ArgumentNullException(nameof(bind));
        return new DyeAllEntries(
            bind.Flag(Section, EnabledKey, true,
                "Hold a color swatch in Character > Equipment to dye every piece that shows: the transmog where one shows, never a hidden slot. A click still colors "
                + "only the selected piece. Off: swatches behave as in the game."),
            bind.Flag(Section, MatchPalettesKey, false,
                "Experimental. Read each piece's palette list so a color keeps its palette on pieces whose color lists differ. "
                + "Off sends the same index to every piece."),
            bind.Flag(Section, LegacyImportedKey, false,
                "Set once the settings of the former Dye all mod (progenitor.dyeall.cfg) were looked for and copied here. "
                + "That file is only read, never changed."));
    }
}

/// <summary>
/// Dye all was a separate mod (<c>progenitor.dyeall</c>, the DYEALL sample) before it became part of Core
/// (2026-10-08). Game-free: reading its cfg once into Core's entries, and the rule that makes Core's copy stand
/// down while the old mod still runs, so a swatch is never watched, ringed and dyed twice.
/// </summary>
internal static class DyeAllLegacy
{
    /// <summary>The old mod's plugin GUID.</summary>
    internal const string PluginGuid = "progenitor.dyeall";

    /// <summary>The old mod's cfg file (in <c>BepInEx/config</c>).</summary>
    internal const string CfgFileName = PluginGuid + ".cfg";

    /// <summary>The sentence the old mod's page shows in its provenance block.</summary>
    internal const string ProvenanceNote = "Now part of Progenitor; you can uninstall this mod";

    /// <summary>The old mod's sections.</summary>
    internal const string OldSection = "Dye all";

    /// <summary>What the old cfg held; null where a key was missing or unreadable.</summary>
    internal sealed class Values
    {
        internal bool? Enabled { get; set; }
        internal bool? MatchPalettes { get; set; }

        internal int Count => (Enabled.HasValue ? 1 : 0) + (MatchPalettes.HasValue ? 1 : 0);
    }

    /// <summary>
    /// Read a BepInEx cfg's text: <c>[Section]</c> headers, <c>Key = Value</c> lines, <c>#</c> comments. Only the
    /// old mod's <c>Enabled</c> and <c>MatchPalettes</c> are taken. <c>HoldSeconds</c>, <c>IncludeCovered</c>,
    /// <c>[Test] ColorIndex</c> and <c>[Keybinds] DyeAllTestColor</c> are ignored: a hold is always one second, a hold
    /// dyes only what shows, and Core has no dye key. A value that does not parse is left out.
    /// </summary>
    internal static Values Parse(string? text)
    {
        var values = new Values();
        if (string.IsNullOrEmpty(text)) return values;
        string section = "";
        foreach (string raw in text!.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;
            if (line[0] == '[' && line[line.Length - 1] == ']')
            {
                section = line.Substring(1, line.Length - 2).Trim();
                continue;
            }

            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            string key = line.Substring(0, eq).Trim();
            string value = line.Substring(eq + 1).Trim();
            if (string.Equals(section, OldSection, StringComparison.OrdinalIgnoreCase))
            {
                if (Is(key, "Enabled")) values.Enabled = Bool(value) ?? values.Enabled;
                else if (Is(key, "MatchPalettes")) values.MatchPalettes = Bool(value) ?? values.MatchPalettes;
            }
        }

        return values;
    }

    /// <summary>Copy <paramref name="values"/> into <paramref name="entries"/>, clamped to Core's bounds. Returns how many were copied.</summary>
    internal static int Apply(Values values, DyeAllEntries entries)
    {
        if (values == null) throw new ArgumentNullException(nameof(values));
        if (entries == null) throw new ArgumentNullException(nameof(entries));
        if (values.Enabled is bool enabled) entries.Enabled.Value = enabled;
        if (values.MatchPalettes is bool palettes) entries.MatchPalettes.Value = palettes;
        return values.Count;
    }

    /// <summary>
    /// Import once: when <see cref="DyeAllEntries.LegacyImported"/> is still off, read
    /// <paramref name="configDirectory"/>/<see cref="CfgFileName"/> if it exists (read only; the file is never
    /// written, moved or deleted), copy its values and set the flag. The flag is set whether or not the file was
    /// there, so a later edit of Core's own values is never overwritten. Returns the log line, or null when the
    /// import already ran.
    /// </summary>
    internal static string? ImportOnce(string? configDirectory, DyeAllEntries entries)
    {
        if (entries == null) throw new ArgumentNullException(nameof(entries));
        if (entries.LegacyImported.Value) return null;
        string line;
        string path = string.IsNullOrEmpty(configDirectory) ? CfgFileName : Path.Combine(configDirectory!, CfgFileName);
        try
        {
            if (File.Exists(path))
            {
                int copied = Apply(Parse(File.ReadAllText(path)), entries);
                line = "Dye all: copied " + copied + " setting(s) from the former Dye all mod's " + CfgFileName
                       + " (left as it was).";
            }
            else
            {
                line = "Dye all: no " + CfgFileName + " to copy settings from; using Progenitor's defaults.";
            }
        }
        catch (Exception e)
        {
            line = "Dye all: could not read " + CfgFileName + " (" + e.Message + "); using Progenitor's values.";
        }

        entries.LegacyImported.Value = true;
        return line;
    }

    /// <summary>
    /// Whether the old mod runs in this process, so Core's copy stands down. Progenitor's mod registry is the
    /// first word (it knows a hot-loaded copy and one the player turned off); when the registry has not seen
    /// the mod (a chainloader copy after a Core swap, before the Mods menu notes it), the chainloader's own
    /// plugin list decides.
    /// </summary>
    internal static bool OldModRunning(bool registryKnows, bool registryRunning, bool chainloaderHas) =>
        registryKnows ? registryRunning : chainloaderHas;

    /// <summary>The provenance sentence for a mod page, or null when <paramref name="pluginId"/> is not a mod Core absorbed.</summary>
    internal static string? NoteFor(string? pluginId) =>
        string.Equals((pluginId ?? "").Trim(), PluginGuid, StringComparison.OrdinalIgnoreCase) ? ProvenanceNote : null;

    private static bool Is(string key, string name) => string.Equals(key, name, StringComparison.OrdinalIgnoreCase);

    private static bool? Bool(string value) => bool.TryParse(value, out bool b) ? b : null;
}
