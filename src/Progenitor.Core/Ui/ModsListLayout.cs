using System;
using System.Collections.Generic;
using System.Globalization;

namespace Progenitor.Ui;

/// <summary>The Mods list's status diamond.</summary>
internal enum ModDiamond
{
    /// <summary>Green, filled: running, no problems.</summary>
    Running = 0,

    /// <summary>Amber, filled: a warning or a conflict.</summary>
    Warning = 1,

    /// <summary>Red, filled: failed to load, or another critical problem.</summary>
    Failed = 2,

    /// <summary>Gray, filled: turned off by the player (or turning off at the next start).</summary>
    Disabled = 3,

    /// <summary>Hollow outline: not running now, and not the player's doing (paused for this server, waiting for a restart, only on the server).</summary>
    Waiting = 4,
}

/// <summary>The chips at the top of the Mods list.</summary>
internal enum ModsListFilter
{
    All = 0,
    NeedsAttention = 1,
    Disabled = 2,
}

/// <summary>Which group a Mods list entry sits in.</summary>
internal enum ModsListGroup
{
    Installed = 0,
    Server = 1,
    Required = 2,
}

/// <summary>Everything one list entry is decided from. Plain data.</summary>
internal sealed class ModsListEntryFacts
{
    internal string Id { get; init; } = "";
    internal string Name { get; init; } = "";
    internal string Version { get; init; } = "";
    internal ModSides Sides { get; init; }
    internal ModsListGroup Group { get; init; }

    /// <summary>The framework line composed from the same facts the page uses, so the words match.</summary>
    internal ModFrameworkLine Line { get; init; } = ModFrameworkLine.Compose(new ModFrameworkFacts());

    internal IReadOnlyList<ModProblem> Problems { get; init; } = Array.Empty<ModProblem>();

    /// <summary>A newer version is known (the Install tab's listing).</summary>
    internal bool UpdateAvailable { get; init; }

    /// <summary>A manifest-verified two-sided package whose server half is nowhere this client can see (the page's red warning).</summary>
    internal bool ServerHalfMissing { get; init; }

    /// <summary>
    /// C5: the <c>Peers</c> range this version declares (the loaded copy's attribute, or the verified inspection of an
    /// installed copy that is not loaded); empty when none is declared or none is known.
    /// </summary>
    internal string DeclaredPeers { get; init; } = "";
}

/// <summary>One decided list entry.</summary>
internal sealed class ModsListEntry
{
    internal string Id { get; init; } = "";
    internal string Name { get; init; } = "";
    internal ModsListGroup Group { get; init; }
    internal ModDiamond Diamond { get; init; }

    /// <summary>The muted second line. Always states the state in words (color is never the only signal).</summary>
    internal string SecondLine { get; init; } = "";

    internal bool NeedsAttention { get; init; }

    /// <summary>The player's switch is off (the Disabled filter).</summary>
    internal bool Disabled { get; init; }

    internal bool Update { get; init; }

    /// <summary>C5: the declared range admits other versions (<see cref="ModAcrossVersions.Applies"/>).</summary>
    internal bool AcrossVersions { get; init; }

    /// <summary>C5: the declared range when <see cref="AcrossVersions"/>, else empty.</summary>
    internal string AcrossRange { get; init; } = "";

    /// <summary>C5: <see cref="SecondLine"/> is the plain <c>version · sides</c> line, so the icon may follow its version.</summary>
    internal bool PlainSecondLine { get; init; }

    /// <summary>C5: the version that starts the plain line (<see cref="PlainSecondLine"/>), else empty.</summary>
    internal string LineVersion { get; init; } = "";

    /// <summary>
    /// C5: the Works across versions icon goes on this entry: its declared range admits other versions and its second line
    /// is the plain <c>version · sides</c> line (never a problem or state line).
    /// </summary>
    internal bool AcrossIcon => AcrossVersions && PlainSecondLine && LineVersion.Length > 0
        && SecondLine.StartsWith(LineVersion, StringComparison.Ordinal);
}

/// <summary>
/// SETTINGS1 stage 3 / MODLIST1: the Mods list redesign (the mockup approved
/// 2026-09-29). Decisions only — the drawing is <c>UnityUiRuntime</c>'s — so every rule has a test.
///
/// <para>An entry is one cell row: the status <b>diamond</b>, then the name (left-aligned) over a
/// muted second line — <c>version · sides</c>, or the problem or state in words when there is one —
/// and an <b>Update</b> tag at the right when a newer version is known. The top of the column is a
/// summary line (<c>9 mods · 2 need attention</c>) and the filter chips; the groups have headings
/// (<c>Installed 9</c>, <c>Required by Progenitor 10 ►</c>, collapsible where they were).</para>
///
/// <para>The status words come from <see cref="ModFrameworkLine"/>, the page's own status bar, so the
/// list, the page and the dialogs cannot say two different things.</para>
/// </summary>
internal static class ModsListLayout
{
    internal const string DiamondFilled = "filled";
    internal const string DiamondHollow = "hollow";

    /// <summary>A list entry's height: two lines (name over the muted line). Guessed; not live-checked.</summary>
    internal const float RowHeight = 48f;

    /// <summary>A group heading's height.</summary>
    internal const float GroupHeight = 30f;

    /// <summary>The summary line and the filter row.</summary>
    internal const float SummaryHeight = 26f;
    internal const float FilterHeight = 34f;

    /// <summary>The filter chips' caption size.</summary>
    internal const float ChipFontSize = 14f;

    /// <summary>Hover: the text color at this alpha (a subtle light tint, never the near-black row hover).</summary>
    internal const float HoverAlpha = 0.06f;

    /// <summary>Selected: the accent (<see cref="MenuPalette.Gold"/>) at this alpha, beside the slim left bar.</summary>
    internal const float SelectedAlpha = 0.14f;

    internal const float SelectedHoverAlpha = 0.2f;

    // Keys.
    internal const string CellDiamond = "diamond";
    internal const string CellName = "name";
    internal const string CellUpdate = "update";

    /// <summary>Chat stage 3 (mod links): the Share button over the name's right end, shown while the pointer is on the row.</summary>
    internal const string CellShare = "share";

    internal const string CellGroupTitle = "title";
    internal const string CellGroupCount = "count";
    internal const string CellGroupChevron = "chevron";
    internal const string CellSummary = "summary";
    internal const string SummaryKey = "mods-summary";
    internal const string FilterKey = "mods-filter";

    internal const string UpdateTag = "Update";

    /// <summary>The second line of an entry whose server half is missing (the page carries the full warning).</summary>
    internal const string ServerHalfMissingLine = "Needs its server part";

    internal const string GroupInstalled = "Installed";
    internal const string GroupServer = "On this server";

    /// <summary>F3: the server group while not connected: the configured local server folder's installs.</summary>
    internal const string GroupLocalServer = "On your local server";

    /// <summary>F3: the server group's caption for the list being drawn (set by each list build).</summary>
    internal static string ServerGroupCaptionNow { get; set; } = GroupServer;
    internal const string GroupRequired = "Required by Progenitor";

    internal const string FilterAllCaption = "All";
    internal const string FilterAttentionCaption = "Needs attention";
    internal const string FilterDisabledCaption = "Disabled";

    internal static string RowKey(string id) => "mod:" + (id ?? "");

    internal static string GroupKey(ModsListGroup group) => "group-" + NameOf(group);

    internal static string ChipKey(ModsListFilter filter) => "filter-" + NameOf(filter);

    internal static string NameOf(ModDiamond d) => d switch
    {
        ModDiamond.Warning => "warning",
        ModDiamond.Failed => "failed",
        ModDiamond.Disabled => "disabled",
        ModDiamond.Waiting => "waiting",
        _ => "running",
    };

    internal static string NameOf(ModsListFilter f) => f switch
    {
        ModsListFilter.NeedsAttention => "attention",
        ModsListFilter.Disabled => "disabled",
        _ => "all",
    };

    internal static string NameOf(ModsListGroup g) => g switch
    {
        ModsListGroup.Server => "server",
        ModsListGroup.Required => "required",
        _ => "installed",
    };

    internal static bool TryParseFilter(string? name, out ModsListFilter filter)
    {
        switch ((name ?? "").Trim().ToLowerInvariant())
        {
            case "all": filter = ModsListFilter.All; return true;
            case "attention": case "needs-attention": filter = ModsListFilter.NeedsAttention; return true;
            case "disabled": filter = ModsListFilter.Disabled; return true;
            default: filter = ModsListFilter.All; return false;
        }
    }

    internal static string CaptionOf(ModsListFilter f) => f switch
    {
        ModsListFilter.NeedsAttention => FilterAttentionCaption,
        ModsListFilter.Disabled => FilterDisabledCaption,
        _ => FilterAllCaption,
    };

    internal static string CaptionOf(ModsListGroup g) => g switch
    {
        ModsListGroup.Server => ServerGroupCaptionNow,
        ModsListGroup.Required => GroupRequired,
        _ => GroupInstalled,
    };

    // ---- colors (derived from existing palette entries; no new entry) ------------------------

    internal static PaletteColor RowFill(bool selected) =>
        selected ? MenuPalette.Gold.WithAlpha(SelectedAlpha) : MenuPalette.RowFill;

    internal static PaletteColor RowHover(bool selected) =>
        selected ? MenuPalette.Gold.WithAlpha(SelectedHoverAlpha) : MenuPalette.Text.WithAlpha(HoverAlpha);

    /// <summary>The diamond's tone.</summary>
    internal static CellTone ToneOf(ModDiamond d) => d switch
    {
        ModDiamond.Running => CellTone.Ok,
        ModDiamond.Warning => CellTone.Warn,
        ModDiamond.Failed => CellTone.Danger,
        _ => CellTone.Muted,
    };

    /// <summary>The existing green, amber, red and muted gray.</summary>
    internal static PaletteColor DiamondColor(CellTone tone) => tone switch
    {
        CellTone.Ok => MenuPalette.Ok,
        CellTone.Warn => MenuPalette.Warn,
        CellTone.Danger => MenuPalette.Red,
        _ => MenuPalette.Muted,
    };

    // ---- the decision --------------------------------------------------------------------------

    /// <summary>
    /// The diamond, worst first: a critical problem is red; a warning or a conflict is amber; the
    /// player's switch off (or turning off at the next start) is gray; a mod that is not running now
    /// for any other reason — paused for this server, waiting for a restart to load, turning on at the
    /// next start, only on the server — is a hollow outline; everything else is green.
    /// </summary>
    internal static ModDiamond DiamondOf(ModFrameworkLine line, IReadOnlyList<ModProblem>? problems)
    {
        var worst = ModProblems.Worst(problems);
        if (worst == ModProblemSeverity.Critical || line.Status == ModRunStatus.FailedToLoad) return ModDiamond.Failed;
        if (worst == ModProblemSeverity.Warning || line.Status == ModRunStatus.LoadedWithProblems) return ModDiamond.Warning;
        if (line.Status == ModRunStatus.Disabled || line.Facts.SwitchPending == ModSwitchPending.TurnsOff) return ModDiamond.Disabled;
        switch (line.Status)
        {
            case ModRunStatus.Paused:
            case ModRunStatus.NotOnClient:
                return ModDiamond.Waiting;
            case ModRunStatus.PendingRestart:
                // Waiting to load (a written install, or turning on at the next start) is not running;
                // a pending removal and a changed needs-restart setting are still running now.
                if (line.Facts.PageState == ModPageState.PendingLoad) return ModDiamond.Waiting;
                if (line.Facts.SwitchPending == ModSwitchPending.TurnsOn) return ModDiamond.Waiting;
                return ModDiamond.Running;
        }

        return ModDiamond.Running;
    }

    /// <summary>The short sides word on the second line: <c>Client</c>, <c>Server</c>, <c>Client and server</c>.</summary>
    internal static string SidesShort(ModSides sides) => sides switch
    {
        ModSides.Client => "Client",
        ModSides.Server => "Server",
        ModSides.Both => "Client and server",
        _ => "",
    };

    /// <summary>
    /// The second line: the worst problem in words, else the first conflict, else the status word
    /// when it is not <c>Enabled</c>, else <c>version · sides</c>.
    /// </summary>
    internal static string SecondLineOf(ModsListEntryFacts f) => SecondLineOf(f, out _);

    /// <summary><see cref="SecondLineOf(ModsListEntryFacts)"/>; <paramref name="plain"/> true for the <c>version · sides</c> line.</summary>
    internal static string SecondLineOf(ModsListEntryFacts f, out bool plain)
    {
        plain = false;
        var line = f.Line;
        var worst = ModProblems.WorstNonConflict(f.Problems);
        if (worst != null)
            return worst.Severity == ModProblemSeverity.Critical ? worst.ShortLine : line.StatusWord;
        if (f.Problems != null)
            foreach (var p in f.Problems)
                if (p.IsConflict) return p.ShortLine;
        if (line.Status != ModRunStatus.Enabled) return line.StatusWord;
        if (f.ServerHalfMissing) return ServerHalfMissingLine;
        string version = (f.Version ?? "").Trim();
        if (version.Length == 0) version = ModMenuInventory.VersionUnknown;
        string sides = SidesShort(f.Sides);
        plain = true;
        return sides.Length == 0 ? version : version + " · " + sides;
    }

    /// <summary>C5: the version the plain second line starts with (<see cref="ModMenuInventory.VersionUnknown"/> when none is known).</summary>
    internal static string LineVersionOf(ModsListEntryFacts f)
    {
        string version = (f?.Version ?? "").Trim();
        return version.Length == 0 ? ModMenuInventory.VersionUnknown : version;
    }

    internal static ModsListEntry Decide(ModsListEntryFacts f)
    {
        f ??= new ModsListEntryFacts();
        var diamond = DiamondOf(f.Line, f.Problems);
        if (f.ServerHalfMissing && diamond == ModDiamond.Running) diamond = ModDiamond.Warning;
        string second = SecondLineOf(f, out bool plain);
        bool across = ModAcrossVersions.Applies(f.DeclaredPeers, f.Version);
        return new ModsListEntry
        {
            Id = f.Id,
            Name = f.Name.Length > 0 ? f.Name : f.Id,
            Group = f.Group,
            Diamond = diamond,
            SecondLine = second,
            PlainSecondLine = plain,
            LineVersion = plain ? LineVersionOf(f) : "",
            AcrossVersions = across,
            AcrossRange = across ? (f.DeclaredPeers ?? "").Trim() : "",
            NeedsAttention = ModProblems.NeedsAttention(f.Problems) || f.ServerHalfMissing,
            Disabled = !f.Line.SwitchOn && f.Line.SwitchVisible,
            Update = f.UpdateAvailable,
        };
    }

    internal static bool Passes(ModsListEntry entry, ModsListFilter filter) => filter switch
    {
        ModsListFilter.NeedsAttention => entry.NeedsAttention,
        ModsListFilter.Disabled => entry.Disabled,
        _ => true,
    };

    /// <summary><c>9 mods · 2 need attention</c>; <c>1 mod</c>; the attention half only when some do.</summary>
    internal static string SummaryText(int mods, int attention)
    {
        string text = mods.ToString(CultureInfo.InvariantCulture) + (mods == 1 ? " mod" : " mods");
        if (attention > 0)
            text += " · " + attention.ToString(CultureInfo.InvariantCulture) + (attention == 1 ? " needs attention" : " need attention");
        return text;
    }

    // ---- rows ---------------------------------------------------------------------------------

    /// <summary>
    /// One entry: the diamond, the name over the second line (left-aligned), the Update tag. A
    /// <paramref name="shareable"/> entry (its Thunderstore package is known) also carries the hidden
    /// <see cref="CellShare"/> button over the right end of the name's first line, and its row reports the
    /// pointer (<see cref="CellRowSpec.Hover"/>) so the button shows only while the pointer is on the row.
    /// </summary>
    internal static CellRowSpec EntryRow(ModsListEntry entry, float rowWidth, bool shareable = false)
    {
        var cells = new List<CellSpec>(4)
        {
            new CellSpec(CellKind.Diamond, CellDiamond).With(
                text: entry.Diamond == ModDiamond.Waiting ? DiamondHollow : DiamondFilled,
                tone: ToneOf(entry.Diamond), width: CellWidth.Px(CellLayout.DiamondWidth)),
        };
        float update = 0f;
        CellSpec? tag = null;
        if (entry.Update)
        {
            tag = new CellSpec(CellKind.Badge, CellUpdate).With(text: UpdateTag, tone: CellTone.Pending, align: CellAlign.Right);
            update = CellLayout.AutoWidth(tag);
            tag.Width = CellWidth.Px(update);
        }

        float nameWidth = NameWidth(entry, rowWidth);
        cells.Add(new CellSpec(CellKind.Text, CellName).With(
            text: entry.Name + "\n" + entry.SecondLine, wrap: CellWrap.TwoLine, align: CellAlign.Left,
            width: CellWidth.Px(nameWidth), minWidth: CellLayout.MinTextWidth));
        // C5: the Works across versions icon, inline right after the version; the runtime hides it when the version
        // itself would be cut (the same rule DrawnSecondLine answers).
        if (entry.AcrossIcon) cells.Add(AcrossIconCell(entry.LineVersion.Length));
        if (shareable) cells.Add(ShareCell());
        cells.Add(new CellSpec(CellKind.Spacer, "gap"));
        if (tag != null) cells.Add(tag);
        return new CellRowSpec(RowKey(entry.Id), cells, twoLine: true, height: RowHeight, soft: true, hover: shareable);
    }

    /// <summary>
    /// The name cell's width: the row less the diamond, the Update tag (when shown) with their gaps, and the selected
    /// stripe. The Update tag is sized first, so the marker on the second line can never push it.
    /// </summary>
    internal static float NameWidth(ModsListEntry entry, float rowWidth)
    {
        float update = entry.Update ? CellLayout.AutoWidth(UpdateTagCell()) + CellLayout.Gap : 0f;
        return Math.Max(CellLayout.MinTextWidth,
            rowWidth - CellLayout.DiamondWidth - CellLayout.Gap - update - MenuPalette.SelectedStripeWidth - 4f);
    }

    private static CellSpec UpdateTagCell() =>
        new CellSpec(CellKind.Badge, CellUpdate).With(text: UpdateTag, tone: CellTone.Pending, align: CellAlign.Right);

    /// <summary>C5: the Works across versions icon's cell key.</summary>
    internal const string CellAcross = "across";

    /// <summary>
    /// C5: the icon's cell width: the space before the glyph (about a space of the second line) and the glyph. The
    /// <c> · </c> after the version brings its own space, so nothing is added after it.
    /// </summary>
    internal const float AcrossIconGap = 4f;

    internal const float AcrossIconWidth = AcrossIconGap + CellLayout.LoopGlyphWidth;

    /// <summary>C5: the icon cell's height, the pointer's target (a little taller than the 11 px glyph).</summary>
    internal const float AcrossIconHeight = 16f;

    /// <summary>
    /// C5: the Works across versions icon (user choice 2026-10-10, option A): the loop-arrows glyph
    /// (<see cref="CellSkin.LoopGlyph"/>, muted) drawn inline on the name cell's second line right after the first
    /// <paramref name="versionLength"/> characters (the version), so the line reads <c>1.3.0 ⟲ · Client and server</c>. It
    /// reports the pointer (<see cref="CellSpec.Hover"/>) for its hover text.
    /// </summary>
    internal static CellSpec AcrossIconCell(int versionLength)
    {
        var icon = new CellSpec(CellKind.Image, CellAcross).With(skin: CellSkin.LoopGlyph, tone: CellTone.Muted);
        icon.Width = CellWidth.Px(AcrossIconWidth);
        icon.MinWidth = AcrossIconWidth;
        icon.OverlayOf = CellName;
        icon.InlineAfter = Math.Max(1, versionLength);
        icon.HeightOverride = AcrossIconHeight;
        icon.Hover = true;
        return icon;
    }

    /// <summary>
    /// C5: the second line as drawn (the text; the icon is not a character of it), clipped to the name cell as the runtime
    /// clips it. On an entry with the icon (<see cref="ModsListEntry.AcrossIcon"/>) the line keeps the icon's room after
    /// the version, so <c>sides</c> ends in an ellipsis that much sooner; when even the version would be cut with the room
    /// taken out, the icon is not drawn. <paramref name="shown"/>: <c>icon</c> or <c>none</c>; <paramref name="iconAfter"/>
    /// is the text before the icon (the version), empty when none.
    /// </summary>
    internal static string DrawnSecondLine(ModsListEntry entry, float rowWidth, out string shown, out string iconAfter)
    {
        float noteWidth = Math.Max(24f, NameWidth(entry, rowWidth) - 4f);
        int after = entry.AcrossIcon ? entry.LineVersion.Length : -1;
        string line = CellLayout.InlineNoteLine("", entry.SecondLine, after, noteWidth, AcrossIconWidth, out int at);
        shown = at >= 0 ? ModAcrossVersions.ShownIcon : ModAcrossVersions.ShownNone;
        iconAfter = at >= 0 ? line.Substring(0, at) : "";
        return line;
    }

    /// <inheritdoc cref="DrawnSecondLine(ModsListEntry, float, out string, out string)"/>
    internal static string DrawnSecondLine(ModsListEntry entry, float rowWidth, out string shown) =>
        DrawnSecondLine(entry, rowWidth, out shown, out _);

    /// <summary>The share button's font size and height (the Install list's quiet shortcut sizes).</summary>
    internal const float ShareFontSize = 14f;

    internal const float ShareHeight = 20f;

    /// <summary>
    /// The hidden Share button laid over the right end of the name cell's first line (an overlay takes no
    /// width from the row), in the quiet skin. The row's hover shows it; the Install list uses the same cell.
    /// </summary>
    internal static CellSpec ShareCell(string key = CellShare, string target = CellName)
    {
        var share = new CellSpec(CellKind.Button, key).With(
            text: ModLinks.ShareRowCaption, fontSize: ShareFontSize, vAlign: CellVAlign.Top, skin: CellSkin.Quiet,
            align: CellAlign.Right);
        float width = CellLayout.EstimateTextWidth(ModLinks.ShareRowCaption, ShareFontSize) + CellLayout.ButtonPad + 8f;
        share.Width = CellWidth.Px(width);
        share.MinWidth = width;
        share.OverlayOf = target;
        share.HeightOverride = ShareHeight;
        share.Visible = false;
        return share;
    }

    /// <summary>
    /// MENUKIT2 round D: a navigation row of a sidebar that is not the Mods list (the Configuration tab's
    /// Sources / Local server / Progenitor): the list's row look — left text, its hover band and selected
    /// highlight — instead of a full-width flat button.
    /// </summary>
    internal static CellRowSpec NavRow(string caption, float rowWidth)
    {
        float width = Math.Max(CellLayout.MinTextWidth, rowWidth - MenuPalette.SelectedStripeWidth - 8f);
        var cells = new List<CellSpec>(2)
        {
            new CellSpec(CellKind.Text, CellName).With(
                text: caption ?? "", align: CellAlign.Left, width: CellWidth.Px(width), minWidth: CellLayout.MinTextWidth),
            new CellSpec(CellKind.Spacer, "gap"),
        };
        return new CellRowSpec(NavRowKeyPrefix + (caption ?? ""), cells, height: NavRowHeight, soft: true);
    }

    internal const string NavRowKeyPrefix = "nav-";

    internal const float NavRowHeight = 44f;

    /// <summary>A group heading: the name and the count, with the expand marker when it collapses.</summary>
    internal static CellRowSpec GroupRow(ModsListGroup group, int count, bool collapsible)
    {
        var cells = new List<CellSpec>(4)
        {
            new CellSpec(CellKind.Muted, CellGroupTitle).With(text: CaptionOf(group), vAlign: CellVAlign.Bottom),
            new CellSpec(CellKind.Muted, CellGroupCount).With(text: count.ToString(CultureInfo.InvariantCulture), vAlign: CellVAlign.Bottom),
            new CellSpec(CellKind.Spacer, "gap"),
        };
        if (collapsible) cells.Add(new CellSpec(CellKind.Chevron, CellGroupChevron).With(align: CellAlign.Right, vAlign: CellVAlign.Bottom));
        // A heading that does not collapse is plain: nothing lights up under the pointer.
        return new CellRowSpec(GroupKey(group), cells, height: GroupHeight, plain: !collapsible, soft: collapsible);
    }

    internal static CellRowSpec SummaryRow(string text) =>
        new(SummaryKey, new[]
        {
            new CellSpec(CellKind.Muted, CellSummary).With(text: text ?? "", width: CellWidth.Fraction(1f)),
        }, height: SummaryHeight, plain: true);

    /// <summary>The three chips; the chosen one in the accent, the others muted.</summary>
    internal static CellRowSpec FilterRow(ModsListFilter chosen)
    {
        var cells = new List<CellSpec>(4);
        foreach (var f in new[] { ModsListFilter.All, ModsListFilter.NeedsAttention, ModsListFilter.Disabled })
        {
            var chip = new CellSpec(CellKind.Button, ChipKey(f)).With(
                text: CaptionOf(f), tone: f == chosen ? CellTone.Gold : CellTone.Muted, fontSize: ChipFontSize);
            chip.Width = CellWidth.Px(CellLayout.EstimateTextWidth(chip.Text, ChipFontSize) + CellLayout.ButtonPad + 8f);
            chip.MinWidth = CellLayout.EstimateTextWidth(chip.Text, ChipFontSize) + CellLayout.ButtonPad;
            cells.Add(chip);
        }

        cells.Add(new CellSpec(CellKind.Spacer, "gap"));
        return new CellRowSpec(FilterKey, cells, height: FilterHeight, plain: true);
    }
}
