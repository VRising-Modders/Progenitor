using System;
using System.Collections.Generic;
using System.Globalization;
using Progenitor.Install;

namespace Progenitor.Ui;

/// <summary>
/// Settings organization stage 3 (decided 2026-10-05, question 3): the Install tab's filters and sort as
/// remembered text. With the Configuration tab's <b>Remember Install filters and sort</b> on, the Mods menu saves
/// them in <c>[Mods] InstallFiltersRemembered</c> and a new session starts from them; off (the default), each
/// session starts from the defaults, as before. Game-free: a text codec over the two views' state.
/// </summary>
internal sealed class InstallFilterSnapshot
{
    public InstallSideFilter Side { get; set; } = InstallSideFilter.Any;
    public InstallShowFilter Show { get; set; } = InstallShowFilter.All;
    public bool ProgenitorSupported { get; set; }
    public bool CurrentUpdateOnly { get; set; } = true;
    public bool NoRestartOnly { get; set; }
    public InstallSort Sort { get; set; } = InstallSort.Released;
    public bool SortReversed { get; set; }

    /// <summary>The Simple view's Show chip and sort (Advanced keeps its own above).</summary>
    public InstallSimpleShow SimpleShow { get; set; } = InstallSimpleShow.All;
    public InstallSort SimpleSort { get; set; } = InstallSort.Released;
    public bool SimpleSortReversed { get; set; }

    internal static InstallFilterSnapshot Of(InstallSearchFilters filters, InstallSimpleShow simpleShow, InstallSort simpleSort, bool simpleReversed) => new()
    {
        Side = filters.Side,
        Show = filters.Show,
        ProgenitorSupported = filters.ProgenitorSupported,
        CurrentUpdateOnly = filters.CurrentUpdateOnly,
        NoRestartOnly = filters.NoRestartOnly,
        Sort = filters.Sort,
        SortReversed = filters.SortReversed,
        SimpleShow = simpleShow,
        SimpleSort = simpleSort,
        SimpleSortReversed = simpleReversed,
    };

    /// <summary>Put the Advanced view's part on <paramref name="filters"/>.</summary>
    internal void ApplyTo(InstallSearchFilters filters)
    {
        if (filters == null) return;
        filters.Side = Side;
        filters.Show = Show;
        filters.ProgenitorSupported = ProgenitorSupported;
        filters.CurrentUpdateOnly = CurrentUpdateOnly;
        filters.NoRestartOnly = NoRestartOnly;
        filters.Sort = Sort;
        filters.SortReversed = SortReversed;
    }
}

internal static class InstallFilterMemory
{
    internal const string Version = "v1";

    /// <summary><c>v1;side=Any;show=All;dep=0;cur=1;norestart=0;sort=Released;rev=0;sshow=All;ssort=Released;srev=0</c>.</summary>
    internal static string Encode(InstallFilterSnapshot s)
    {
        if (s == null) return "";
        return string.Join(";", new[]
        {
            Version,
            "side=" + s.Side,
            "show=" + s.Show,
            "dep=" + Bit(s.ProgenitorSupported),
            "cur=" + Bit(s.CurrentUpdateOnly),
            "norestart=" + Bit(s.NoRestartOnly),
            "sort=" + s.Sort,
            "rev=" + Bit(s.SortReversed),
            "sshow=" + s.SimpleShow,
            "ssort=" + s.SimpleSort,
            "srev=" + Bit(s.SimpleSortReversed),
        });
    }

    /// <summary>
    /// Read remembered text. False (and the defaults) for empty text or another version; a part that does not parse
    /// keeps its default, so a hand edit gone wrong costs one filter, never the list.
    /// </summary>
    internal static bool TryDecode(string? text, out InstallFilterSnapshot snapshot)
    {
        snapshot = new InstallFilterSnapshot();
        string t = (text ?? "").Trim();
        if (t.Length == 0) return false;
        var parts = t.Split(';');
        if (!string.Equals(parts[0].Trim(), Version, StringComparison.Ordinal)) return false;
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i < parts.Length; i++)
        {
            int eq = parts[i].IndexOf('=');
            if (eq <= 0) continue;
            values[parts[i].Substring(0, eq).Trim()] = parts[i].Substring(eq + 1).Trim();
        }

        if (values.TryGetValue("side", out var side) && TryEnum(side, out InstallSideFilter sideValue)) snapshot.Side = sideValue;
        if (values.TryGetValue("show", out var show) && TryEnum(show, out InstallShowFilter showValue)) snapshot.Show = showValue;
        if (values.TryGetValue("dep", out var dep) && TryBit(dep, out bool depValue)) snapshot.ProgenitorSupported = depValue;
        if (values.TryGetValue("cur", out var cur) && TryBit(cur, out bool curValue)) snapshot.CurrentUpdateOnly = curValue;
        if (values.TryGetValue("norestart", out var nr) && TryBit(nr, out bool nrValue)) snapshot.NoRestartOnly = nrValue;
        if (values.TryGetValue("sort", out var sort) && TryEnum(sort, out InstallSort sortValue)) snapshot.Sort = sortValue;
        if (values.TryGetValue("rev", out var rev) && TryBit(rev, out bool revValue)) snapshot.SortReversed = revValue;
        if (values.TryGetValue("sshow", out var sshow) && TryEnum(sshow, out InstallSimpleShow sshowValue)) snapshot.SimpleShow = sshowValue;
        if (values.TryGetValue("ssort", out var ssort) && TryEnum(ssort, out InstallSort ssortValue)) snapshot.SimpleSort = ssortValue;
        if (values.TryGetValue("srev", out var srev) && TryBit(srev, out bool srevValue)) snapshot.SimpleSortReversed = srevValue;
        return true;
    }

    private static string Bit(bool on) => on ? "1" : "0";

    private static bool TryBit(string text, out bool value)
    {
        value = false;
        if (text == "1") { value = true; return true; }
        return text == "0";
    }

    private static bool TryEnum<T>(string text, out T value) where T : struct, Enum =>
        Enum.TryParse(text, ignoreCase: true, out value) && Enum.IsDefined(typeof(T), value)
        && !int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out _);
}
