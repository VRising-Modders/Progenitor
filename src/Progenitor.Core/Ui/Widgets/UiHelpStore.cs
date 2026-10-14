using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Progenitor.Ui;

/// <summary>P7b help: the four values the term markers and the tip cards remember per player.</summary>
internal enum UiHelpSlot
{
    /// <summary><c>[Ui] HiddenTermMarkers</c>: the <c>plugin:term</c> keys whose marker is hidden.</summary>
    HiddenTerms = 0,

    /// <summary><c>[Ui] TermMarkerHovers</c>: <c>plugin:term=n</c>, hovers counted toward the auto-hide.</summary>
    TermHovers = 1,

    /// <summary><c>[Ui] SeenTips</c>: the <c>plugin:tip</c> keys of the tips the player dismissed.</summary>
    SeenTips = 2,
}

/// <summary>
/// P7b help: where the term markers and the tip cards keep their per-player state. The client's store is Core's own
/// <c>progenitor.ui.cfg</c> (<see cref="UiHelpSettingsStore"/>, the <c>[Ui] ClosedDrawers</c> precedent: bound by the client
/// host, read on every use, so a Core reload or a restart finds it again); tests swap in <see cref="UiHelpMemoryStore"/>.
/// Game-free.
/// </summary>
internal interface IUiHelpStore
{
    string Read(UiHelpSlot slot);

    void Write(UiHelpSlot slot, string value);

    /// <summary>Hovers after which a term's marker hides itself; 0 never.</summary>
    int AutoHideAfter { get; set; }
}

/// <summary>The cfg section, keys and words of the help values (<see cref="CoreUiSettings"/> holds the bound entries).</summary>
internal static class UiHelpSettings
{
    internal const string Section = "Ui";
    internal const string HiddenTermsKey = "HiddenTermMarkers";
    internal const string TermHoversKey = "TermMarkerHovers";
    internal const string AutoHideKey = "TermMarkerAutoHide";
    internal const string SeenTipsKey = "SeenTips";

    /// <summary>The spec's "after the player has hovered that term three times".</summary>
    internal const int DefaultAutoHide = 3;

    internal const int MaxAutoHide = 20;

    internal const string HiddenTermsDescription =
        "The explained words whose small (i) marker you no longer see, as <mod>:<word>, separated by commas. The hover "
        + "explanation still works. Changed by the markers themselves and by a mod's \"show markers again\" setting.";

    internal const string TermHoversDescription =
        "How often you hovered each explained word while its marker showed, as <mod>:<word>=<count>. Nothing to set by hand.";

    internal const string AutoHideDescription =
        "Hide an explained word's (i) marker after you hovered it this many times. 0: never hide it.";

    internal const string SeenTipsDescription =
        "The first-time tips you closed with Got it, as <mod>:<tip>, separated by commas. Empty: every tip shows once again.";

    internal static string KeyOf(UiHelpSlot slot) => slot switch
    {
        UiHelpSlot.HiddenTerms => HiddenTermsKey,
        UiHelpSlot.TermHovers => TermHoversKey,
        _ => SeenTipsKey,
    };
}

/// <summary>Session memory: tests, and the client before (or without) its cfg.</summary>
internal sealed class UiHelpMemoryStore : IUiHelpStore
{
    private readonly Dictionary<UiHelpSlot, string> _values = new();
    private int _autoHide = UiHelpSettings.DefaultAutoHide;

    public string Read(UiHelpSlot slot) => _values.TryGetValue(slot, out var v) ? v : "";

    public void Write(UiHelpSlot slot, string value) => _values[slot] = value ?? "";

    public int AutoHideAfter
    {
        get => _autoHide;
        set => _autoHide = UiHelpStore.ClampAutoHide(value);
    }

    /// <summary>Writes so far (tests: a write that changed nothing is not made).</summary>
    internal int Writes { get; private set; }

    internal void CountWrite() => Writes++;
}

/// <summary>Core's <c>progenitor.ui.cfg</c> entries (<see cref="CoreUiSettings"/>); session memory for an unbound entry.</summary>
internal sealed class UiHelpSettingsStore : IUiHelpStore
{
    private readonly UiHelpMemoryStore _memory = new();

    public string Read(UiHelpSlot slot)
    {
        var entry = EntryOf(slot);
        if (entry == null) return _memory.Read(slot);
        try { return entry.Value ?? ""; }
        catch { return _memory.Read(slot); }
    }

    public void Write(UiHelpSlot slot, string value)
    {
        var entry = EntryOf(slot);
        if (entry != null)
        {
            try
            {
                entry.Value = value ?? "";
                return;
            }
            catch (Exception e)
            {
                Plugin.Logger?.LogWarning("Ui: [" + UiHelpSettings.Section + "] " + UiHelpSettings.KeyOf(slot) + " could not be saved: " + e.Message);
            }
        }

        _memory.Write(slot, value);
    }

    public int AutoHideAfter
    {
        get
        {
            var entry = CoreUiSettings.TermMarkerAutoHide;
            if (entry == null) return _memory.AutoHideAfter;
            try { return UiHelpStore.ClampAutoHide(entry.Value); }
            catch { return _memory.AutoHideAfter; }
        }
        set
        {
            int v = UiHelpStore.ClampAutoHide(value);
            var entry = CoreUiSettings.TermMarkerAutoHide;
            if (entry != null)
            {
                try
                {
                    entry.Value = v;
                    return;
                }
                catch { /* session memory below */ }
            }

            _memory.AutoHideAfter = v;
        }
    }

    private static BepInEx.Configuration.ConfigEntry<string>? EntryOf(UiHelpSlot slot) => slot switch
    {
        UiHelpSlot.HiddenTerms => CoreUiSettings.HiddenTermMarkers,
        UiHelpSlot.TermHovers => CoreUiSettings.TermMarkerHovers,
        _ => CoreUiSettings.SeenTips,
    };
}

/// <summary>The store in use, and the codec of its values. Game-free.</summary>
internal static class UiHelpStore
{
    private static IUiHelpStore? _override;
    private static readonly UiHelpSettingsStore Settings = new();

    internal static IUiHelpStore Current => _override ?? Settings;

    /// <summary>Tests: use <paramref name="store"/> (null: the settings store again).</summary>
    internal static void UseForTests(IUiHelpStore? store) => _override = store;

    internal static int ClampAutoHide(int value) => value < 0 ? 0 : value > UiHelpSettings.MaxAutoHide ? UiHelpSettings.MaxAutoHide : value;

    /// <summary>
    /// A term or tip id as it is compared and stored: trimmed, inner whitespace runs as one space, lower case. "Mass
    /// Deposit", " mass  deposit" and "MASS DEPOSIT" are one term.
    /// </summary>
    internal static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var sb = new StringBuilder(text!.Length);
        bool space = false;
        foreach (char c in text.Trim())
        {
            if (char.IsWhiteSpace(c))
            {
                space = true;
                continue;
            }

            if (space && sb.Length > 0) sb.Append(' ');
            space = false;
            sb.Append(char.ToLowerInvariant(c));
        }

        return sb.ToString();
    }

    /// <summary>
    /// The stored key of a plugin's term or tip: <c>plugin:name</c>, escaped. The plugin id keeps its case (it is the
    /// plugin's GUID as written, so the cfg line reads as the mod's id); only its ends are trimmed. The name is normalized
    /// (<see cref="Normalize"/>): a term or tip id compares without case.
    /// </summary>
    internal static string Key(string? pluginId, string? name) => PluginPrefix(pluginId) + Escape(Normalize(name));

    /// <summary>The plugin part of a stored key (escaped form, case kept), for a per-plugin reset.</summary>
    internal static string PluginPrefix(string? pluginId) => Escape((pluginId ?? "").Trim()) + ":";

    /// <summary>Percent-escape the separators (<c>%</c> <c>,</c> <c>:</c> <c>=</c> and control characters) so any id round-trips.</summary>
    internal static string Escape(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var sb = new StringBuilder(text.Length + 8);
        foreach (char c in text)
        {
            if (c == '%' || c == ',' || c == ':' || c == '=' || c == ';' || char.IsControl(c))
                sb.Append('%').Append(((int)c).ToString("X2", CultureInfo.InvariantCulture));
            else sb.Append(c);
        }

        return sb.ToString();
    }

    /// <summary>A stored set: keys separated by commas (empty parts skipped).</summary>
    internal static HashSet<string> ParseSet(string? text)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (string part in (text ?? "").Split(','))
        {
            string key = part.Trim();
            if (key.Length > 0) set.Add(key);
        }

        return set;
    }

    /// <summary>A set as stored: sorted, so the file only changes when the set does.</summary>
    internal static string FormatSet(IEnumerable<string> keys)
    {
        var list = new List<string>(keys ?? Array.Empty<string>());
        list.Sort(StringComparer.Ordinal);
        return string.Join(",", list);
    }

    /// <summary>Stored counts: <c>key=n</c> separated by commas; a bad or non-positive part is skipped.</summary>
    internal static Dictionary<string, int> ParseCounts(string? text)
    {
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (string part in (text ?? "").Split(','))
        {
            int eq = part.LastIndexOf('=');
            if (eq <= 0) continue;
            string key = part.Substring(0, eq).Trim();
            if (key.Length == 0) continue;
            if (int.TryParse(part.Substring(eq + 1).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) && n > 0)
                map[key] = n;
        }

        return map;
    }

    internal static string FormatCounts(IReadOnlyDictionary<string, int> counts)
    {
        var keys = new List<string>();
        foreach (var kv in counts)
            if (kv.Value > 0) keys.Add(kv.Key);
        keys.Sort(StringComparer.Ordinal);
        var parts = new List<string>(keys.Count);
        foreach (var k in keys) parts.Add(k + "=" + counts[k].ToString(CultureInfo.InvariantCulture));
        return string.Join(",", parts);
    }

    /// <summary>Write <paramref name="value"/> only when it differs from what is stored. True when it was written.</summary>
    internal static bool WriteIfChanged(UiHelpSlot slot, string value)
    {
        var store = Current;
        string now;
        try { now = store.Read(slot); }
        catch { now = ""; }
        if (string.Equals(now, value ?? "", StringComparison.Ordinal)) return false;
        try
        {
            store.Write(slot, value ?? "");
            if (store is UiHelpMemoryStore memory) memory.CountWrite();
            return true;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: help value " + UiHelpSettings.KeyOf(slot) + " could not be written: " + e.Message);
            return false;
        }
    }

    internal static HashSet<string> ReadSet(UiHelpSlot slot)
    {
        try { return ParseSet(Current.Read(slot)); }
        catch { return new HashSet<string>(StringComparer.Ordinal); }
    }
}
