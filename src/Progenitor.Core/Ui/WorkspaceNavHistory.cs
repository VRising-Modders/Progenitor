using System;
using System.Collections.Generic;

namespace Progenitor.Ui;

/// <summary>
/// KEYS4: one place in the Mods workspace, as the back and forward history records it: the tab, and on
/// the Mods tab the selected mod and its page (<c>overview</c>, <c>mod</c>, <c>custom</c>,
/// <c>settings</c>, <c>settings-all</c>) with the page id; on Configuration the nav section; on
/// Keybinds the section a link jumped to. <see cref="Section"/> on the Mods tab is <c>keys</c> after a
/// jump to the mod page's Keys section.
///
/// <para>KEYS7: or one of the game's own full-screen menu tabs (<see cref="Game"/>: <c>SpellBook</c>,
/// <c>VampirePowers</c>, … the <c>FullscreenMenuState</c> names) with its inner tab when known
/// (<see cref="Inner"/>, -1 unknown). A game step's workspace fields are empty; the in-game MODS tab is a
/// workspace step.</para>
/// </summary>
internal readonly struct NavStep : IEquatable<NavStep>
{
    internal NavStep(ModsTab tab, string plugin = "", string page = "", string pageId = "", string section = "")
    {
        Tab = tab;
        Plugin = plugin ?? "";
        Page = page ?? "";
        PageId = pageId ?? "";
        Section = section ?? "";
        Game = "";
        Inner = -1;
    }

    private NavStep(string game, int inner)
    {
        Tab = ModsTab.Mods;
        Plugin = "";
        Page = "";
        PageId = "";
        Section = "";
        Game = game ?? "";
        Inner = inner < 0 ? -1 : inner;
    }

    /// <summary>KEYS7: a step on one of the game's full-screen menu tabs.</summary>
    internal static NavStep GameTab(string tab, int inner = -1) => new(tab, inner);

    /// <summary>KEYS7: the game's full-screen menu tab (<c>FullscreenMenuState</c> name), or empty for a workspace step.</summary>
    internal string Game { get; }

    /// <summary>KEYS7: the game tab's inner tab index when known, else -1.</summary>
    internal int Inner { get; }

    internal bool IsGame => Game.Length > 0;

    internal ModsTab Tab { get; }

    internal string Plugin { get; }

    internal string Page { get; }

    internal string PageId { get; }

    internal string Section { get; }

    public bool Equals(NavStep other) =>
        string.Equals(Game, other.Game, StringComparison.OrdinalIgnoreCase)
        && Inner == other.Inner
        && Tab == other.Tab
        && string.Equals(Plugin, other.Plugin, StringComparison.OrdinalIgnoreCase)
        && string.Equals(Page, other.Page, StringComparison.Ordinal)
        && string.Equals(PageId, other.PageId, StringComparison.OrdinalIgnoreCase)
        && string.Equals(Section, other.Section, StringComparison.OrdinalIgnoreCase);

    public override bool Equals(object? obj) => obj is NavStep other && Equals(other);

    public override int GetHashCode() =>
        HashCode.Combine(Tab, StringComparer.OrdinalIgnoreCase.GetHashCode(Plugin), Page,
            StringComparer.OrdinalIgnoreCase.GetHashCode(PageId), StringComparer.OrdinalIgnoreCase.GetHashCode(Section),
            StringComparer.OrdinalIgnoreCase.GetHashCode(Game), Inner);

    /// <summary><c>mods/progenitor.sample/mod</c>, <c>configuration//section:General</c> … for logs and the readback.</summary>
    public override string ToString() =>
        IsGame ? "game:" + Game + (Inner >= 0 ? "#" + Inner : "") :
        ModsTabState.StatusName(Tab) + "/" + Plugin + "/" + Page + (PageId.Length > 0 ? ":" + PageId : "")
        + (Section.Length > 0 ? "#" + Section : "");

    internal Dictionary<string, object?> Readback() => IsGame
        ? new(StringComparer.Ordinal)
        {
            ["tab"] = "game",
            ["game"] = Game,
            ["inner"] = Inner,
            ["plugin"] = "",
            ["page"] = "",
            ["pageId"] = "",
            ["section"] = "",
        }
        : new(StringComparer.Ordinal)
    {
        ["tab"] = ModsTabState.StatusName(Tab),
        ["plugin"] = Plugin,
        ["page"] = Page,
        ["pageId"] = PageId,
        ["section"] = Section,
        ["place"] = PlaceWord,
    };

    /// <summary>
    /// Settings organization: the place as one word for the readback — <c>progenitor-page</c> (Progenitor's mod page),
    /// <c>progenitor-page@&lt;group&gt;</c> (reached at a group through its left list, or <c>@keys</c>),
    /// <c>preferences</c> or <c>preferences@&lt;group&gt;</c>, <c>server</c> or <c>server@&lt;group&gt;</c> (three homes); any
    /// other place its <see cref="ToString"/>.
    /// </summary>
    internal string PlaceWord
    {
        get
        {
            if (IsGame) return ToString();
            if (Tab == ModsTab.Preferences) return "preferences" + (Section.Length > 0 ? "@" + Section : "");
            if (Tab == ModsTab.Server) return "server" + (Section.Length > 0 ? "@" + Section : "");
            if (Tab == ModsTab.Mods && Page == "mod"
                && string.Equals(Plugin, SettingsPanel.PluginId, StringComparison.OrdinalIgnoreCase))
                return "progenitor-page" + (Section.Length > 0 ? "@" + Section : "");
            return ToString();
        }
    }
}

/// <summary>
/// KEYS4 (requested 2026-09-30, "like a web browser"): the Mods workspace's back and forward history.
/// A new place after going back drops the forward entries; the same place twice in a row is one entry;
/// back at the start and forward at the end do nothing. The workspace clears it when it closes (the user
/// allowed forgetting it). Game-free.
/// </summary>
internal sealed class WorkspaceNavHistory
{
    /// <summary>The most entries kept; the oldest go first.</summary>
    internal const int Max = 50;

    private readonly List<NavStep> _steps = new();
    private int _index = -1;

    internal int Count => _steps.Count;

    internal int Index => _index;

    internal bool CanBack => _index > 0;

    internal bool CanForward => _index >= 0 && _index < _steps.Count - 1;

    internal NavStep? Current => _index >= 0 && _index < _steps.Count ? _steps[_index] : null;

    /// <summary>Record arriving at <paramref name="step"/>. False when it is where the history already is.</summary>
    internal bool Record(NavStep step)
    {
        if (Current is { } at && at.Equals(step)) return false;
        // KEYS7 (live 2026-09-30: switching inner tabs on mid-session turned "Social -1" into "Social -1, Social 0"):
        // an unknown inner tab (-1) is the same place as any inner tab of that game tab; the entry takes the known one.
        if (Current is { } g && g.IsGame && step.IsGame && string.Equals(g.Game, step.Game, StringComparison.OrdinalIgnoreCase)
            && (g.Inner < 0 || step.Inner < 0))
        {
            if (step.Inner >= 0) _steps[_index] = step;
            return false;
        }
        if (_index < _steps.Count - 1) _steps.RemoveRange(_index + 1, _steps.Count - _index - 1);
        _steps.Add(step);
        if (_steps.Count > Max) _steps.RemoveRange(0, _steps.Count - Max);
        _index = _steps.Count - 1;
        return true;
    }

    /// <summary>KEYS5: the current entry becomes <paramref name="step"/> (where a restore actually landed).</summary>
    internal void ReplaceCurrent(NavStep step)
    {
        if (_index < 0 || _index >= _steps.Count) Record(step);
        else _steps[_index] = step;
    }

    /// <summary>
    /// KEYS6: step back (or forward) to the nearest entry <paramref name="valid"/> accepts. Every entry passed
    /// over is removed (a place that no longer exists), and so is an entry that has become the same as the one
    /// it lands next to. Null, with the position unchanged, when no valid entry remains in that direction.
    /// </summary>
    internal NavStep? Move(bool back, Func<NavStep, bool> valid)
    {
        int i = back ? _index - 1 : _index + 1;
        while (i >= 0 && i < _steps.Count)
        {
            bool ok;
            try { ok = valid(_steps[i]); }
            catch { ok = false; }
            if (ok && !(_index >= 0 && _index < _steps.Count && _steps[i].Equals(_steps[_index])))
            {
                _index = i;
                return _steps[i];
            }

            _steps.RemoveAt(i);
            if (i < _index) _index--;
            if (back) i--;
        }

        return null;
    }

    /// <summary>Step back; null at the start.</summary>
    internal NavStep? Back()
    {
        if (!CanBack) return null;
        _index--;
        return _steps[_index];
    }

    /// <summary>Step forward; null at the end.</summary>
    internal NavStep? Forward()
    {
        if (!CanForward) return null;
        _index++;
        return _steps[_index];
    }

    internal void Clear()
    {
        _steps.Clear();
        _index = -1;
    }

    internal Dictionary<string, object?> Readback()
    {
        var steps = new List<object?>(_steps.Count);
        foreach (var s in _steps) steps.Add(s.Readback());
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["index"] = _index,
            ["count"] = _steps.Count,
            ["canBack"] = CanBack,
            ["canForward"] = CanForward,
            ["steps"] = steps,
        };
    }
}
