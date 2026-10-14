using System;
using System.Collections.Generic;

namespace Progenitor.Ui;

/// <summary>
/// P7b: the game-free half of the chip set (<see cref="UiWidgets.Chips"/>): how wide a chip is, how chips wrap onto
/// lines for a row width, and the cells a line row is built from. Tested without a client
/// (<c>UiChipsTests</c> resolves every packed line at the checked widths through the kit's own column model and asserts
/// no chip overlaps another or leaves the row, and every icon and remove glyph sits inside its own chip).
///
/// <para>A chip is <b>one</b> button cell drawn on the Install filter chip's art (<see cref="CellSkin.Chip"/>, the game's
/// popup button with the gold plate when selected) as wide as the whole chip. An item chip's icon (a
/// <see cref="ChipIconSize"/> icon well) and a removable chip's remove glyph are overlay cells
/// (<see cref="CellSpec.OverlayOf"/>) drawn inside that art at its left and right ends, and the caption keeps clear of
/// them (<see cref="CellSpec.CaptionInsetLeft"/> / <see cref="CellSpec.CaptionInsetRight"/>); a chip with neither keeps
/// the symmetric button pad, so its caption is centered on the whole art. The art's top and bottom slices take 9 units
/// each, so a chip with a 24-unit icon is <see cref="ItemChipHeight"/> tall; a set of text chips is
/// <see cref="TextChipHeight"/>. Every width is a fixed ask, so the column model never squeezes a chip: the packer decides
/// the wrap, and a caption too long for the row is shortened with an ellipsis here, not by the runtime.</para>
/// </summary>
internal static class UiChipsLayout
{
    /// <summary>A set with an item chip: 9 + the 24 icon + 9 (the chip art's top and bottom slices) and air.</summary>
    internal const float ItemChipHeight = 36f;

    /// <summary>A set of text chips: the Install filter chip's captured height.</summary>
    internal const float TextChipHeight = 31f;

    /// <summary>The line row: the settings grid's row height, a 36 chip centered in it.</summary>
    internal const float LineHeight = 40f;

    /// <summary>
    /// The space above every line after the first, in place of the panel's row gap: with the row's own air it leaves the
    /// 8 between two lines of chips that the chips leave between each other on a line.
    /// </summary>
    internal const float LineGap = 4f;

    /// <summary>The icon well inside an item chip, its inset from the chip's left edge and the gap to the caption.</summary>
    internal const float ChipIconSize = 24f;
    internal const float IconInset = 6f;
    internal const float IconGap = 6f;

    /// <summary>The remove glyph's click target inside a chip, its inset from the chip's right edge and the gap to the caption.</summary>
    internal const float RemoveWidth = 20f;
    internal const float RemoveInset = 4f;
    internal const float RemoveGap = 2f;

    /// <summary>The chip caption's size (16, under the 18 of a full button).</summary>
    internal const float CaptionFontSize = 16f;

    /// <summary>One glyph of slack on every caption estimate (the Install chips' rule: short capitalized words under-measure).</summary>
    internal const float CaptionSlack = CaptionFontSize * MenuPanelLayout.AverageGlyphEm;

    /// <summary>From one chip's right edge to the next chip's left edge: the cell gap.</summary>
    internal const float Spacing = CellLayout.Gap;

    /// <summary>The row width packed for until the real row is measured: the narrowest checked width.</summary>
    internal const float DefaultRowWidth = 560f;

    /// <summary>A measured row narrower than this is a reading taken before the row was laid out, and is ignored.</summary>
    internal const float MinMeasuredWidth = 240f;

    internal const int DefaultMaxLines = 3;
    internal const int MaxLinesLimit = 8;
    internal const int DefaultMaxPerLine = 8;
    internal const int MinPerLine = 2;
    internal const int MaxPerLineLimit = 16;

    /// <summary>What separates a chip's caption from its note.</summary>
    internal const string NoteSeparator = " · ";

    /// <summary>The <see cref="ChipPiece.Index"/> of the add chip and of the <c>+N more</c> chip.</summary>
    internal const int AddIndex = -1;
    internal const int MoreIndex = -2;

    internal static int ClampLines(int lines) => lines < 1 ? DefaultMaxLines : Math.Min(lines, MaxLinesLimit);

    internal static int ClampPerLine(int perLine) =>
        perLine < 1 ? DefaultMaxPerLine : Math.Max(MinPerLine, Math.Min(perLine, MaxPerLineLimit));

    /// <summary>The caption a chip shows: its label, and its note after a middle dot.</summary>
    internal static string Caption(UiChip chip) =>
        chip.Note.Length > 0 ? chip.Label + NoteSeparator + chip.Note : chip.Label;

    /// <summary>The <c>+N more</c> chip's caption.</summary>
    internal static string MoreCaption(int hidden) => "+" + hidden.ToString(System.Globalization.CultureInfo.InvariantCulture) + " more";

    /// <summary>The chip height for a set: <see cref="ItemChipHeight"/> when any chip has an icon, else <see cref="TextChipHeight"/>.</summary>
    internal static float ChipHeight(bool anyItem) => anyItem ? ItemChipHeight : TextChipHeight;

    /// <summary>The caption's left margin inside a chip: past the icon, else half the button pad.</summary>
    internal static float CaptionLeft(bool icon) => icon ? IconInset + ChipIconSize + IconGap : CellLayout.ButtonPad * .5f;

    /// <summary>
    /// The room a glossary term's circle-i marker takes after the caption (P7b help): its side at the chip caption's size
    /// and its gap. The marker is drawn inside the caption's own rect, so a chip with a term keeps this much free at the
    /// caption's right end or the marker would sit on its last letters.
    /// </summary>
    internal static float TermReserve => UiTermMarkerArt.SizeFor(CaptionFontSize) + UiTermMarkerArt.Gap;

    /// <summary>
    /// The caption's right margin inside a chip: past the remove glyph, else half the button pad; plus the term marker's
    /// room when the chip explains a term.
    /// </summary>
    internal static float CaptionRight(bool remove, bool term = false) =>
        (remove ? RemoveInset + RemoveWidth + RemoveGap : CellLayout.ButtonPad * .5f) + (term ? TermReserve : 0f);

    /// <summary>
    /// A chip's natural width: its caption estimate and slack between the margins, whole units, never narrower than the
    /// chip is tall (a <c>+</c> chip is square).
    /// </summary>
    internal static float ChipWidth(string caption, bool icon, bool remove, float height, bool term = false) =>
        Math.Max(height, (float)Math.Ceiling(
            CellLayout.EstimateTextWidth(caption ?? "", CaptionFontSize) + CaptionSlack + CaptionLeft(icon) + CaptionRight(remove, term)));

    // ---- Cells ---------------------------------------------------------------------------------

    internal static string ChipKey(int slot) => "chip" + slot;

    internal static string IconKey(int slot) => "icon" + slot;

    internal static string RemoveKey(int slot) => "x" + slot;

    internal const string EmptyKey = "empty";

    /// <summary>Cells per chip slot in a line row: the chip, its icon overlay, its remove overlay.</summary>
    internal const int CellsPerSlot = 3;

    /// <summary>
    /// The cells of one line row: <paramref name="slots"/> chip slots, every one hidden until a chip is written into it,
    /// then the muted empty text. Each slot is the chip (a button cell on the chip art), then its icon well and its remove
    /// glyph as overlays inside it, so the controller walks a line in reading order: a chip, its remove glyph, the next
    /// chip. The overlays come after the chip so they draw over its art.
    /// </summary>
    internal static List<CellSpec> LineCells(int slots, string removeGlyph)
    {
        var cells = new List<CellSpec>(slots * CellsPerSlot + 1);
        for (int j = 0; j < slots; j++)
        {
            var chip = InstallModsPane.Chip(ChipKey(j), "", false, width: TextChipHeight);
            chip.Hover = true;
            chip.Visible = false;
            chip.FontSizeOverride = CaptionFontSize;
            chip.HeightOverride = TextChipHeight;
            cells.Add(chip);
            cells.Add(new CellSpec(CellKind.Image, IconKey(j))
            {
                MinWidth = ChipIconSize,
                Skin = CellSkin.IconWell,
                Visible = false,
                OverlayOf = ChipKey(j),
                OverlayLeft = true,
                OverlayInset = IconInset,
                HeightOverride = ChipIconSize,
            }.With(width: CellWidth.Px(ChipIconSize)));
            cells.Add(new CellSpec(CellKind.Button, RemoveKey(j))
            {
                MinWidth = RemoveWidth,
                Skin = CellSkin.Quiet,
                Visible = false,
                OverlayOf = ChipKey(j),
                OverlayInset = RemoveInset,
                HeightOverride = ChipIconSize,
            }.With(text: removeGlyph, width: CellWidth.Px(RemoveWidth), wrap: CellWrap.Glyph));
        }

        cells.Add(new CellSpec(CellKind.Muted, EmptyKey) { MinWidth = 1f, Visible = false }.With(width: CellWidth.Fill(1f)));
        return cells;
    }

    /// <summary>
    /// Write <paramref name="pack"/>'s line <paramref name="line"/> into a line row's cell specs (widths, captions, caption
    /// margins, heights, visibility); the handle then repaints them. Game-free, so the tests resolve exactly what the
    /// runtime places.
    /// </summary>
    internal static void Apply(IReadOnlyList<CellSpec> cells, ChipPack pack, int line)
    {
        var placed = line < pack.Lines.Count ? pack.Lines[line] : (IReadOnlyList<ChipPlacement>)Array.Empty<ChipPlacement>();
        int slots = (cells.Count - 1) / CellsPerSlot;
        for (int j = 0; j < slots; j++)
        {
            var chip = cells[j * CellsPerSlot];
            var icon = cells[j * CellsPerSlot + 1];
            var remove = cells[j * CellsPerSlot + 2];
            if (j >= placed.Count)
            {
                chip.Visible = icon.Visible = remove.Visible = false;
                continue;
            }

            var p = placed[j];
            chip.Visible = true;
            chip.Text = p.Shown;
            chip.Width = CellWidth.Px(p.Width);
            chip.MinWidth = p.Width;
            chip.HeightOverride = pack.ChipHeight;
            chip.CaptionInsetLeft = p.Icon ? CaptionLeft(true) : 0f;
            chip.CaptionInsetRight = p.Remove || p.Term ? CaptionRight(p.Remove, p.Term) : 0f;
            icon.Visible = p.Icon;
            remove.Visible = p.Remove;
        }

        var empty = cells[cells.Count - 1];
        empty.Visible = line == 0 && pack.Empty;
    }

    // ---- Packing -------------------------------------------------------------------------------

    /// <summary>One chip to pack: its index (or <see cref="AddIndex"/> / <see cref="MoreIndex"/>) and what it draws.</summary>
    internal readonly struct ChipPiece
    {
        internal ChipPiece(int index, string caption, bool icon, bool remove, bool term = false)
        {
            Term = term;
            Index = index;
            Caption = caption ?? "";
            Icon = icon;
            Remove = remove;
        }

        internal int Index { get; }
        internal string Caption { get; }
        internal bool Icon { get; }
        internal bool Remove { get; }

        /// <summary>The chip explains a glossary term: room is kept for its marker.</summary>
        internal bool Term { get; }
    }

    /// <summary>
    /// Wrap <paramref name="chips"/> onto lines for a row <paramref name="rowWidth"/> wide: at most
    /// <paramref name="maxLines"/> lines of at most <paramref name="maxPerLine"/> chips. The add chip (when
    /// <paramref name="addCaption"/> is not null) always follows the last chip drawn; when not every chip fits, as many as
    /// fit are drawn, in order, followed by a <c>+N more</c> chip counting the rest. A single chip wider than the row is
    /// shortened (its caption ends in an ellipsis) so it never leaves the row. Every chip of the set is
    /// <see cref="ChipHeight"/> tall (36 when any chip has an icon).
    /// </summary>
    internal static ChipPack Pack(IReadOnlyList<ChipPiece> chips, float rowWidth, int maxLines, int maxPerLine, string? addCaption)
    {
        chips ??= Array.Empty<ChipPiece>();
        if (float.IsNaN(rowWidth) || float.IsInfinity(rowWidth) || rowWidth <= 0f) rowWidth = DefaultRowWidth;
        maxLines = ClampLines(maxLines);
        maxPerLine = ClampPerLine(maxPerLine);
        bool anyItem = false;
        foreach (var c in chips) anyItem |= c.Icon;
        float height = ChipHeight(anyItem);
        int n = chips.Count;
        int capacity = maxLines * maxPerLine;
        var add = addCaption != null ? new ChipPiece(AddIndex, addCaption, false, false) : (ChipPiece?)null;
        var sequence = new List<ChipPiece>(Math.Min(n, capacity) + 2);
        for (int k = Math.Min(n, capacity); k >= 0; k--)
        {
            sequence.Clear();
            for (int i = 0; i < k; i++) sequence.Add(chips[i]);
            if (k < n) sequence.Add(new ChipPiece(MoreIndex, MoreCaption(n - k), false, false));
            if (add != null) sequence.Add(add.Value);
            var lines = TryLines(sequence, rowWidth, maxLines, maxPerLine, height);
            if (lines != null) return new ChipPack(lines, rowWidth, k, n - k, n == 0, height);
        }

        // Not even the trailing chips fit (one line, a row too narrow for both): the more chip alone, else the add chip.
        sequence.Clear();
        if (n > 0) sequence.Add(new ChipPiece(MoreIndex, MoreCaption(n), false, false));
        else if (add != null) sequence.Add(add.Value);
        return new ChipPack(TryLines(sequence, rowWidth, 1, 1, height) ?? new List<List<ChipPlacement>>(), rowWidth, 0, n, n == 0, height);
    }

    /// <summary>The lines <paramref name="sequence"/> takes, or null when it needs more than <paramref name="maxLines"/>.</summary>
    private static List<List<ChipPlacement>>? TryLines(List<ChipPiece> sequence, float rowWidth, int maxLines, int maxPerLine, float height)
    {
        var lines = new List<List<ChipPlacement>>();
        if (sequence.Count == 0) return lines;
        var current = new List<ChipPlacement>();
        lines.Add(current);
        float cursor = 0f;
        foreach (var piece in sequence)
        {
            float width = ChipWidth(piece.Caption, piece.Icon, piece.Remove, height, piece.Term);
            string shown = piece.Caption;
            if (width > rowWidth)
            {
                // Wider than the whole row: shortened to the row, the caption clipped to the room its margins leave.
                width = Math.Max(height, (float)Math.Floor(rowWidth));
                float room = width - CaptionLeft(piece.Icon) - CaptionRight(piece.Remove, piece.Term) - CaptionSlack;
                shown = MenuPanelLayout.ClipLabel(piece.Caption, Math.Max(1f, room), CaptionFontSize);
            }

            float left = current.Count == 0 ? 0f : cursor + Spacing;
            if (current.Count > 0 && (left + width > rowWidth + .01f || current.Count >= maxPerLine))
            {
                if (lines.Count >= maxLines) return null;
                current = new List<ChipPlacement>();
                lines.Add(current);
                left = 0f;
            }

            current.Add(new ChipPlacement(piece.Index, piece.Caption, shown, piece.Icon, piece.Remove, lines.Count - 1, current.Count, left, width)
            {
                Term = piece.Term,
            });
            cursor = left + width;
        }

        return lines;
    }

    /// <summary>
    /// The row's width in panel units from what the runtime reads back in screen pixels: the row's own width, and one
    /// placed cell's screen width beside the width it was asked for (the canvas scale). 0 when unreadable.
    /// </summary>
    internal static float LayoutWidth(float rowScreenWidth, float cellScreenWidth, float cellLayoutWidth)
    {
        if (!(rowScreenWidth > 0f) || !(cellScreenWidth > 0f) || !(cellLayoutWidth > 0f)) return 0f;
        if (float.IsInfinity(rowScreenWidth) || float.IsInfinity(cellScreenWidth) || float.IsInfinity(cellLayoutWidth)) return 0f;
        float scale = cellScreenWidth / cellLayoutWidth;
        float width = rowScreenWidth / scale;
        return float.IsNaN(width) || float.IsInfinity(width) ? 0f : (float)Math.Round(width, 1);
    }
}

/// <summary>P7b: one chip placed by <see cref="UiChipsLayout.Pack"/>.</summary>
internal sealed class ChipPlacement
{
    internal ChipPlacement(int index, string caption, string shown, bool icon, bool remove, int line, int slot, float left, float width)
    {
        Index = index;
        Caption = caption;
        Shown = shown;
        Icon = icon;
        Remove = remove;
        Line = line;
        Slot = slot;
        Left = left;
        Width = width;
    }

    /// <summary>The chip's index, or <see cref="UiChipsLayout.AddIndex"/> / <see cref="UiChipsLayout.MoreIndex"/>.</summary>
    internal int Index { get; }

    /// <summary>The full caption, and the caption drawn (shortened with an ellipsis when the chip is wider than the row).</summary>
    internal string Caption { get; }

    internal string Shown { get; }

    internal bool Icon { get; }

    internal bool Remove { get; }

    /// <summary>The chip explains a glossary term (its caption keeps room for the marker).</summary>
    internal bool Term { get; init; }

    internal int Line { get; }

    internal int Slot { get; }

    /// <summary>From the row's left edge to the chip's left edge, and the chip's width (its one chip cell's).</summary>
    internal float Left { get; }

    internal float Width { get; }

    internal float Right => Left + Width;

    internal bool Truncated => !string.Equals(Caption, Shown, StringComparison.Ordinal);

    public override string ToString() => Index + "@" + Line + ":" + Slot + " " + Left.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)
        + ".." + Right.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>P7b: the result of <see cref="UiChipsLayout.Pack"/>.</summary>
internal sealed class ChipPack
{
    internal ChipPack(List<List<ChipPlacement>> lines, float rowWidth, int shown, int overflow, bool empty, float chipHeight)
    {
        ChipHeight = chipHeight;
        Lines = lines;
        RowWidth = rowWidth;
        Shown = shown;
        Overflow = overflow;
        Empty = empty;
    }

    internal static readonly ChipPack None = new(new List<List<ChipPlacement>>(), UiChipsLayout.DefaultRowWidth, 0, 0, true, UiChipsLayout.TextChipHeight);

    /// <summary>Every chip's height in this pack (<see cref="UiChipsLayout.ChipHeight"/>).</summary>
    internal float ChipHeight { get; }

    internal IReadOnlyList<IReadOnlyList<ChipPlacement>> Lines { get; }

    internal float RowWidth { get; }

    /// <summary>Chips drawn (the add and more chips not counted) and chips counted on the more chip.</summary>
    internal int Shown { get; }

    internal int Overflow { get; }

    /// <summary>The set has no chips: line 0 shows the empty text.</summary>
    internal bool Empty { get; }

    /// <summary>The placement at <paramref name="line"/>, <paramref name="slot"/>, or null.</summary>
    internal ChipPlacement? At(int line, int slot) =>
        line >= 0 && line < Lines.Count && slot >= 0 && slot < Lines[line].Count ? Lines[line][slot] : null;

    /// <summary>Where chip <paramref name="index"/> is drawn, or null.</summary>
    internal ChipPlacement? Of(int index)
    {
        foreach (var line in Lines)
            foreach (var p in line)
                if (p.Index == index) return p;
        return null;
    }
}

/// <summary>P7b: the chip set's state: chips in order, unique ids, the selected ids. Game-free.</summary>
internal sealed class UiChipsModel
{
    private readonly List<UiChip> _chips = new();
    private readonly HashSet<string> _selected = new(StringComparer.Ordinal);

    internal UiChipsModel(IEnumerable<UiChip>? chips) => SetChips(chips);

    internal IReadOnlyList<UiChip> Chips => _chips;

    internal int Count => _chips.Count;

    /// <summary>Replace every chip; selection from each chip's <see cref="UiChip.Selected"/>. Duplicate ids are refused, changing nothing.</summary>
    internal void SetChips(IEnumerable<UiChip>? chips)
    {
        var next = new List<UiChip>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        if (chips != null)
        {
            foreach (var c in chips)
            {
                if (c == null) continue;
                if (!ids.Add(c.Id)) throw new ArgumentException("Two chips share the id '" + c.Id + "'.", nameof(chips));
                next.Add(c);
            }
        }

        _chips.Clear();
        _chips.AddRange(next);
        _selected.Clear();
        foreach (var c in next)
            if (c.Selected) _selected.Add(c.Id);
    }

    internal int IndexOf(string? id)
    {
        if (id == null) return -1;
        for (int i = 0; i < _chips.Count; i++)
            if (string.Equals(_chips[i].Id, id, StringComparison.Ordinal)) return i;
        return -1;
    }

    internal UiChip? Find(string? id)
    {
        int i = IndexOf(id);
        return i < 0 ? null : _chips[i];
    }

    internal bool Remove(string? id, out UiChip? removed)
    {
        int i = IndexOf(id);
        removed = i < 0 ? null : _chips[i];
        if (removed == null) return false;
        _chips.RemoveAt(i);
        _selected.Remove(removed.Id);
        return true;
    }

    internal bool IsSelected(string? id) => id != null && _selected.Contains(id);

    internal bool SetSelected(string? id, bool selected)
    {
        if (IndexOf(id) < 0) return false;
        return selected ? _selected.Add(id!) : _selected.Remove(id!);
    }

    internal List<string> SelectedIds()
    {
        var ids = new List<string>(_selected.Count);
        foreach (var c in _chips)
            if (_selected.Contains(c.Id)) ids.Add(c.Id);
        return ids;
    }
}
