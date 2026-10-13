using System;
using System.Collections.Generic;
using System.Globalization;
using Progenitor.Install;

namespace Progenitor.Ui;

/// <summary>
/// The Install list's column model — one stable column set, its widths, the order columns drop in
/// on a narrow pane, which columns sort and in which direction, and the plain text the date and
/// state cells draw. The header and every listing row take their widths from here, so they cannot
/// drift apart.
///
/// <para><b>INSTALLSIDES1 (Option A, user-approved 2026-10-03).</b> The set is
/// <c>[expand] Mod | Client | Server | Released | Downloads</c> (INSTALLSIDES2 dropped Side: a grayed
/// side cell says the mod does not use that side). The Mod column carries the
/// expand button, the icon, the name with the author under it, and — drawn over its right end when
/// both sides want the same action — the quiet <c>Install on both</c> / <c>Update both</c> shortcut.
/// The Client and Server columns each hold that side's state words and up to two buttons that act on
/// that side alone (<see cref="SideCells"/>). Version and Installed are gone from the row: the
/// versions are inside the side cells, the install date is a drawer fact.</para>
///
/// <para>INSTALLCOLS1's rules stand: every column is always in the model, none appears or
/// disappears with the sort, no header caption is cut, and on a narrow pane the optional columns
/// drop in <see cref="DropPriority"/> order — Released, then Downloads. The side columns never
/// drop or shrink.</para>
/// </summary>
internal static class InstallColumns
{
    // ---- Widths (px at the menu's design scale) ------------------------------------------
    //
    // Live at 3840×1080 and 1600×1024 the Install row is 1236 px (1224 inside the scrollbar band).
    // Expand 28 + icon 36 + Client 300 + Server 300 + Released 98 + Downloads 118 and the gaps leave
    // the Mod column 296 px (INSTALLSIDES2: Side's 72 px and its gap went to Mod), so nothing drops at
    // the user's resolution.

    /// <summary>The Install row's width at 3840×1080 and at 1600×1024, as read back live.</summary>
    public const float LiveRowWidth = 1236f;

    /// <summary>The expand/collapse button at the row's left.</summary>
    public const float ExpandWidth = 28f;

    /// <summary>Floor of the Mod column; it takes every pixel the fixed columns leave (<see cref="CellWidth.Fill"/>).</summary>
    public const float ModMinWidth = 150f;

    /// <summary>INSTALLSIDES1's Side column, dropped by INSTALLSIDES2 (kept for the readers that name it).</summary>
    public const float SideWidth = 72f;

    /// <summary>
    /// One side's column at the narrowest live row (1236 px): its state words and at most two
    /// buttons, with the gaps between them. INSTALLSIDES2 live review: 300 px wrapped
    /// <c>Removal pending server restart</c>, and a wide row left the Mod column mostly empty, so the
    /// side columns are wider and grow with the row (<see cref="SideColumnWidthFor"/>).
    /// </summary>
    public const float SideColumnWidth = 330f;

    /// <summary>The widest a side column grows on a wide row.</summary>
    public const float SideColumnMaxWidth = 440f;

    /// <summary>How much of each extra pixel of row width (past <see cref="LiveRowWidth"/>) each side column takes.</summary>
    public const float SideColumnGrowth = 0.5f;

    /// <summary>
    /// A side column's width on a <paramref name="rowWidth"/> px row: <see cref="SideColumnWidth"/> at
    /// the 1236 px row, half of every extra pixel after it, at most <see cref="SideColumnMaxWidth"/>.
    /// The Mod column keeps the rest.
    /// </summary>
    public static float SideColumnWidthFor(float rowWidth)
    {
        if (float.IsNaN(rowWidth) || float.IsInfinity(rowWidth)) return SideColumnWidth;
        float w = SideColumnWidth + Math.Max(0f, rowWidth - LiveRowWidth) * SideColumnGrowth;
        return (float)Math.Round(Math.Min(SideColumnMaxWidth, w));
    }

    /// <summary>
    /// Released: <c>Sep 23</c> / <c>Sep '25</c>, and the whole <c>RELEASED ▼</c> header — live at 84 px it
    /// drew <c>RELEASED…</c> and lost the sort mark.
    /// </summary>
    public const float DateWidth = 98f;

    /// <summary>Right-aligned count (<c>123,456</c>, <c>1.2M</c> from a million) and the whole <c>DOWNLOADS ▼</c> header.</summary>
    public const float DownloadsWidth = 118f;

    /// <summary>INSTALLSIDES2 item 1: a column band reaches this far past its outer cells.</summary>
    public const float BandPad = 3f;

    /// <summary>INSTALLSIDES2 item 1: a row's column band stops this far inside the row's top and bottom.</summary>
    public const float BandInset = 3f;

    /// <summary>INSTALLSIDES2 item 1 / VANILLA1: the side header's rule (the game's section divider) under its caption, in the column's accent.</summary>
    public const float HeaderRuleHeight = 2f;

    /// <summary>
    /// VANILLA1: the header captions' size. They are drawn in small caps in their own case (the
    /// game's menu-tab style), so a lowercase letter is a capital at <see cref="SmallCapsScale"/>.
    /// </summary>
    public const float HeaderFontSize = 15f;

    /// <summary>TMP's small-caps scale for a lowercase letter.</summary>
    public const float SmallCapsScale = 0.8f;

    /// <summary>
    /// Live review 3: the column hue plate over a side button is inset this far from the button's
    /// ends and from its top and bottom, so the vanilla sprite's decorated ends and rim show around it.
    /// </summary>
    public const float ButtonTintInsetX = 8f;

    /// <inheritdoc cref="ButtonTintInsetX"/>
    public const float ButtonTintInsetY = 3f;

    /// <summary>The side cells' buttons are the game's popup buttons, captioned at this size.</summary>
    public const float SideButtonFontSize = 15f;

    /// <summary>A popup button's caption room on top of its text: the sprite's decorated ends.</summary>
    public const float SideButtonPad = 24f;

    /// <summary>No side button is narrower than this, so <c>Update</c> and <c>Install</c> read as buttons.</summary>
    public const float SideButtonMinWidth = 72f;

    /// <summary>The side cells' state words.</summary>
    public const float SideTextFontSize = 15f;

    /// <summary>Most lines a state text is broken into.</summary>
    public const int StatusMaxLines = 2;

    /// <summary>The <c>Install on both</c> / <c>Update both</c> link under the mod's name.</summary>
    public const float ShortcutFontSize = 14f;

    /// <summary>The shortcut's slot at the right end of the Mod column (the author line is clipped short of it).</summary>
    public const float ShortcutWidth = 128f;

    /// <summary>The shortcut's height: one line, against the bottom of the row.</summary>
    public const float ShortcutHeight = 20f;

    // ---- Superseded by INSTALLSIDES1, kept for the readers that still name them -------------------

    /// <summary>INSTALLCOLS1's Author column; the author is now the second line of the Mod column.</summary>
    public const float AuthorWidth = 140f;

    /// <summary>INSTALLCOLS1's Version column; versions are inside the side cells now.</summary>
    public const float VersionWidth = 70f;

    /// <summary>INSTALLCOLS1's Installed column; the install date is a drawer fact now.</summary>
    public const float InstalledWidth = 104f;

    /// <summary>INSTALLCOLS1's state text slot; a side cell's text takes what its buttons leave.</summary>
    public const float StatusTextWidth = 140f;

    /// <summary>INSTALLCOLS1's one Status button.</summary>
    public const float StatusButtonWidth = 172f;

    public const float StatusButtonFontSize = 16f;

    public const float StatusWidth = StatusTextWidth + CellLayout.Gap + StatusButtonWidth;

    /// <summary>The face the state text is drawn in (the kit's muted size).</summary>
    public const float StatusFontSize = CellLayout.MutedFontSize;

    // ---- The stable set ------------------------------------------------------------------

    /// <summary>Every cell key of a listing row, in order. The same for every sort.</summary>
    public static readonly IReadOnlyList<string> Keys = new[]
    {
        InstallModsPane.CellExpand,
        InstallModsPane.CellIcon,
        InstallModsPane.CellName,
        InstallModsPane.CellBoth,
        InstallModsPane.CellShare,
        InstallModsPane.CellClient,
        InstallModsPane.CellClientA,
        InstallModsPane.CellClientB,
        InstallModsPane.CellServer,
        InstallModsPane.CellServerA,
        InstallModsPane.CellServerB,
        InstallModsPane.CellReleased,
        InstallModsPane.CellDownloads,
    };

    /// <summary>The header's cell keys, in order: one cell per column (a side column's header spans its buttons).</summary>
    public static readonly IReadOnlyList<string> HeaderKeys = new[]
    {
        InstallModsPane.CellExpand,
        InstallModsPane.CellIcon,
        InstallModsPane.CellName,
        InstallModsPane.CellClient,
        InstallModsPane.CellServer,
        InstallModsPane.CellReleased,
        InstallModsPane.CellDownloads,
    };

    /// <summary>The readback names of the columns the player sees, in order.</summary>
    public static readonly IReadOnlyList<string> ColumnNames = new[]
    {
        "mod", "client", "server", "released", "downloads",
    };

    /// <summary>
    /// The order optional columns drop in when the pane is too narrow: Released first, then
    /// Downloads. Mod and the two side columns never drop.
    /// </summary>
    public static readonly IReadOnlyList<string> DropPriority = new[]
    {
        InstallModsPane.CellReleased,
        InstallModsPane.CellDownloads,
    };

    /// <summary>1-based position of <paramref name="key"/> in <see cref="DropPriority"/>; 0 for a column that never drops.</summary>
    public static int DropOrderOf(string? key)
    {
        for (int i = 0; i < DropPriority.Count; i++)
        {
            if (string.Equals(DropPriority[i], key, StringComparison.Ordinal)) return i + 1;
        }
        return 0;
    }

    /// <summary>The width one side button takes for <paramref name="caption"/>.</summary>
    public static float SideButtonWidth(string? caption) =>
        (float)Math.Ceiling(Math.Max(SideButtonMinWidth, CellLayout.EstimateTextWidth(caption, SideButtonFontSize) + SideButtonPad));

    /// <summary>
    /// A side cell's state text width when its buttons (<paramref name="captions"/>; empty ones are
    /// hidden and take no gap) take their share of <see cref="SideColumnWidth"/>.
    /// </summary>
    public static float SideTextWidth(params string?[] captions) => SideTextWidthIn(SideColumnWidth, captions);

    /// <summary><see cref="SideTextWidth"/> in a side column <paramref name="column"/> px wide.</summary>
    public static float SideTextWidthIn(float column, params string?[] captions)
    {
        float width = column;
        for (int i = 0; captions != null && i < captions.Length; i++)
        {
            if (string.IsNullOrEmpty(captions[i])) continue;
            width -= SideButtonWidth(captions[i]) + CellLayout.Gap;
        }
        return Math.Max(40f, width);
    }

    /// <summary>
    /// Give <paramref name="spec"/> its column's width, alignment and drop order, so a header cell
    /// and a row cell with the same key are placed identically. Unknown keys are left as they are;
    /// a side cell's text and buttons are sized by their builder (<see cref="SideTextWidth"/>).
    /// <paramref name="sortedKey"/> is the cell key of the active sort column: it is never dropped.
    /// </summary>
    public static CellSpec Shape(CellSpec spec, string? sortedKey = null)
    {
        if (spec == null) throw new ArgumentNullException(nameof(spec));
        switch (spec.Key)
        {
            case InstallModsPane.CellExpand:
                spec.With(width: CellWidth.Px(ExpandWidth), minWidth: ExpandWidth);
                break;
            case InstallModsPane.CellIcon:
                spec.With(width: CellWidth.Px(CellLayout.ImageSize), minWidth: CellLayout.ImageSize);
                break;
            case InstallModsPane.CellName:
                spec.With(width: CellWidth.Fill(ModMinWidth), minWidth: ModMinWidth);
                break;
            case InstallModsPane.CellSide:
                spec.With(width: CellWidth.Px(SideWidth), minWidth: SideWidth);
                break;
            case InstallModsPane.CellReleased:
                spec.With(width: CellWidth.Px(DateWidth), minWidth: DateWidth);
                break;
            case InstallModsPane.CellDownloads:
                spec.With(width: CellWidth.Px(DownloadsWidth), minWidth: DownloadsWidth, align: CellAlign.Right);
                break;
            default:
                return spec;
        }

        int order = string.Equals(spec.Key, sortedKey, StringComparison.Ordinal) ? 0 : DropOrderOf(spec.Key);
        spec.Optional = order > 0;
        spec.DropOrder = order;
        return spec;
    }

    // ---- Header captions -----------------------------------------------------------------

    /// <summary>
    /// Advance of each capital (and the space) in ems of a typical UI sans face. The header draws
    /// its caption upper-case, where the kit's mean-glyph estimate
    /// (<see cref="MenuPanelLayout.AverageGlyphEm"/>, 0.55 em) is too low: live at 3840×1080 the
    /// 84 px Released and Downloads headers drew <c>RELEASED…</c> and <c>DOWNLO…</c>.
    /// </summary>
    private static readonly Dictionary<char, float> CapitalEm = new()
    {
        ['A'] = .67f, ['B'] = .64f, ['C'] = .65f, ['D'] = .66f, ['E'] = .57f, ['F'] = .56f, ['G'] = .68f,
        ['H'] = .71f, ['I'] = .27f, ['J'] = .55f, ['K'] = .64f, ['L'] = .54f, ['M'] = .87f, ['N'] = .71f,
        ['O'] = .69f, ['P'] = .63f, ['Q'] = .69f, ['R'] = .62f, ['S'] = .61f, ['T'] = .60f, ['U'] = .65f,
        ['V'] = .64f, ['W'] = .89f, ['X'] = .63f, ['Y'] = .60f, ['Z'] = .60f, [' '] = .28f,
    };

    /// <summary>How much wider the game's header face draws than <see cref="CapitalEm"/> (calibrated live; 1.15 keeps a margin).</summary>
    public const float HeaderFaceScale = 1.15f;

    /// <summary>The ▼ / ▲ glyph, allowed a full em.</summary>
    public const float IndicatorEm = 1f;

    /// <summary>TMP's 2 px side margins on a header label, plus 2 px of slack.</summary>
    public const float HeaderPad = 6f;

    /// <summary>
    /// The width a header cell needs to draw <paramref name="caption"/> upper-case at
    /// <see cref="CellLayout.HeaderFontSize"/> without an ellipsis. A <paramref name="sortable"/>
    /// column always counts the indicator slot, so moving the sort never changes whether a caption fits.
    /// </summary>
    public static float HeaderCaptionWidth(string? caption, bool sortable)
        => HeaderCaptionWidth(caption, sortable, smallCaps: false, CellLayout.HeaderFontSize);

    /// <summary>
    /// VANILLA1: as above for a caption drawn in its own case in small caps at <paramref name="fontSize"/>:
    /// a capital (and a digit or mark) at the full em, a lowercase letter as its capital at
    /// <see cref="SmallCapsScale"/>, the same face scale and padding.
    /// </summary>
    public static float HeaderCaptionWidth(string? caption, bool sortable, bool smallCaps, float fontSize)
    {
        if (!smallCaps) return UpperCaptionWidth(caption, sortable);
        float em = 0f;
        foreach (char c in caption ?? "")
        {
            char upper = char.ToUpperInvariant(c);
            float w = CapitalEm.TryGetValue(upper, out float known) ? known : .7f;
            em += char.IsLower(c) ? w * SmallCapsScale : w;
        }
        if (sortable) em += CapitalEm[' '] + IndicatorEm;
        float measured = em * fontSize * HeaderFaceScale;
        float kit = CellLayout.EstimateTextWidth(CellLayout.HeaderCaption(caption, sortable, false, smallCaps: true), fontSize);
        return Math.Max(measured, kit) + HeaderPad;
    }

    private static float UpperCaptionWidth(string? caption, bool sortable)
    {
        string text = (caption ?? "").ToUpperInvariant();
        float em = 0f;
        foreach (char c in text) em += CapitalEm.TryGetValue(c, out float w) ? w : .7f;
        if (sortable) em += CapitalEm[' '] + IndicatorEm;
        float measured = em * CellLayout.HeaderFontSize * HeaderFaceScale;
        string shown = CellLayout.HeaderCaption(caption, sorted: sortable);
        float kit = CellLayout.EstimateTextWidth(shown, CellLayout.HeaderFontSize);
        return Math.Max(measured, kit) + HeaderPad;
    }

    /// <summary>INSTALLCOLS1's helper: a status cell that has the whole old Status column to itself.</summary>
    public static CellSpec WidenStatus(CellSpec status)
    {
        if (status == null) throw new ArgumentNullException(nameof(status));
        return status.With(width: CellWidth.Px(StatusWidth), minWidth: StatusWidth);
    }

    /// <summary>
    /// The resolved placement of <paramref name="cells"/> at <paramref name="rowWidth"/>, the same
    /// model every listing row uses (the right inset is the pane's scrollbar band).
    /// </summary>
    public static CellPlacement[] Resolve(float rowWidth, IReadOnlyList<CellSpec> cells) =>
        CellLayout.Resolve(rowWidth, cells, HeadBlockLayout.RightColumnInset);

    /// <summary>
    /// The cell key of the column <paramref name="sort"/> sorts by. The author is the Mod column's
    /// second line, so an author sort marks Mod; the install-date sort marks Client.
    /// </summary>
    public static string CellOfSort(InstallSort sort) => sort switch
    {
        InstallSort.Name or InstallSort.Author => InstallModsPane.CellName,
        InstallSort.Installed => InstallModsPane.CellClient,
        InstallSort.Downloads => InstallModsPane.CellDownloads,
        _ => InstallModsPane.CellReleased,
    };

    /// <summary>The keys the model drops at <paramref name="rowWidth"/>, in the order they were dropped.</summary>
    public static IReadOnlyList<string> DroppedAt(float rowWidth, IReadOnlyList<CellSpec> cells)
    {
        var placed = Resolve(rowWidth, cells);
        var dropped = new List<string>();
        for (int i = 0; i < DropPriority.Count; i++)
        {
            for (int j = 0; j < placed.Length; j++)
            {
                if (placed[j].Dropped && string.Equals(placed[j].Key, DropPriority[i], StringComparison.Ordinal))
                    dropped.Add(placed[j].Key);
            }
        }
        return dropped;
    }

    // ---- Sorting -------------------------------------------------------------------------

    /// <summary>
    /// The header columns that sort, and the sort each one sets: Mod (A to Z), Client (install
    /// date, newest first), Released and Downloads. Side and Server do not sort.
    /// </summary>
    public static bool TrySortOf(string? cellKey, out InstallSort sort)
    {
        switch (cellKey)
        {
            case InstallModsPane.CellName: sort = InstallSort.Name; return true;
            case InstallModsPane.CellClient: sort = InstallSort.Installed; return true;
            case InstallModsPane.CellReleased: sort = InstallSort.Released; return true;
            case InstallModsPane.CellDownloads: sort = InstallSort.Downloads; return true;
            default: sort = InstallSort.Released; return false;
        }
    }

    /// <summary>A column's first-click direction: newest / most first for Released, Installed and Downloads, A to Z for Mod and Author.</summary>
    public static bool NaturalDescending(InstallSort sort) =>
        sort is InstallSort.Released or InstallSort.Installed or InstallSort.Downloads;

    /// <summary>Whether the sorted column reads ▲ (ascending) for this sort and reversal.</summary>
    public static bool Ascending(InstallSort sort, bool reversed) => NaturalDescending(sort) == reversed;

    /// <summary>The readback direction: <c>asc</c> or <c>desc</c>.</summary>
    public static string DirectionName(InstallSort sort, bool reversed) => Ascending(sort, reversed) ? "asc" : "desc";

    // ---- Text ----------------------------------------------------------------------------

    /// <summary>
    /// The short date the Released column draws: <c>Sep 23</c> in <paramref name="today"/>'s year,
    /// else the month and a two-digit year (<c>Sep '25</c>). US English whatever the machine's culture.
    /// </summary>
    public static string ShortDate(DateTime date, DateTime today) =>
        date.Year == today.Year
            ? date.ToString("MMM d", CultureInfo.InvariantCulture)
            : date.ToString("MMM", CultureInfo.InvariantCulture) + " '" + date.ToString("yy", CultureInfo.InvariantCulture);

    /// <summary>The readback's full date: <c>2025-09-23</c>.</summary>
    public static string FullDate(DateTime date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>The Downloads cell: the full count up to 999,999 (<c>123,456</c>), then <c>1.2M</c>.</summary>
    public static string CompactCount(long? count)
    {
        if (count == null) return InstallModsCopy.Dash;
        long n = count.Value;
        if (n < 1_000_000) return n.ToString("#,0", CultureInfo.InvariantCulture);
        double millions = Math.Floor(n / 100_000d) / 10d;
        return millions.ToString(millions >= 100 ? "0" : "0.#", CultureInfo.InvariantCulture) + "M";
    }

    /// <summary>
    /// A state text broken into at most <see cref="StatusMaxLines"/> lines for a cell
    /// <paramref name="width"/> wide at <paramref name="fontSize"/>: unchanged when it fits; else split
    /// at its first <c> · </c> (the dot goes, the line break says it), else at the last space that
    /// lets the first line fit. A second line that still does not fit is left to the kit's ellipsis.
    /// </summary>
    public static string StatusLines(string? text, float width, float fontSize = StatusFontSize)
    {
        string t = (text ?? "").Trim();
        if (t.Length == 0 || t.IndexOf('\n') >= 0) return t;
        if (Fits(t, width, fontSize)) return t;
        // REMOTEUNREACH1: break at the last separator whose first part fits and whose rest fits too
        // (a three-part state such as "1.0.0 · update 2.0.0 · <route note>" used to break after its
        // first part and leave a second line too long for the cell); else at the first separator.
        int firstDot = t.IndexOf(" · ", StringComparison.Ordinal);
        for (int dot = t.LastIndexOf(" · ", StringComparison.Ordinal); dot > 0; dot = dot > 0 ? t.LastIndexOf(" · ", dot - 1, StringComparison.Ordinal) : -1)
        {
            string first = t.Substring(0, dot).Trim();
            string rest = t.Substring(dot + 3).Trim();
            if (Fits(first, width, fontSize) && Fits(rest, width, fontSize)) return first + "\n" + rest;
        }

        if (firstDot > 0)
        {
            string first = t.Substring(0, firstDot).Trim();
            string rest = t.Substring(firstDot + 3).Trim();
            if (Fits(first, width, fontSize)) return first + "\n" + rest;
        }

        int cut = -1;
        for (int i = 0; i < t.Length; i++)
        {
            if (t[i] == ' ' && Fits(t.Substring(0, i), width, fontSize)) cut = i;
        }
        return cut > 0 ? t.Substring(0, cut) + "\n" + t.Substring(cut + 1).Trim() : t;
    }

    /// <summary>Does one line of state text fit a cell <paramref name="width"/> wide (the kit's own estimate)?</summary>
    public static bool Fits(string? line, float width, float fontSize = StatusFontSize) =>
        CellLayout.EstimateTextWidth(line, fontSize) <= width - 4f + .01f;
}
