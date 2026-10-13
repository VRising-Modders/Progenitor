using System;
using System.Collections.Generic;

namespace Progenitor.Ui;

/// <summary>One tile of a <see cref="CellTiles"/> pair: a link (or a key-cap button) on its first line, a muted note on its second.</summary>
internal sealed record TileSpec(string Cell, string Caption, string NoteCell, string Note, bool Button = false);

/// <summary>
/// TILES1 (kit): bordered tiles in a grid of two per line, built from cell rows. Each line of tiles is two rows: the head
/// row (each tile's link or button) and the note row (each tile's muted note, one line, ellipsized at the tile's width).
/// A tile's cells carry its number (<see cref="CellSpec.Tile"/>); the runtime draws, behind each tile's cells, a fill and
/// a 1 px rim (<see cref="MenuPalette.TileBand"/>) from the left of its first cell to the right of its last
/// (<see cref="Spans"/>), the top rim on the head row (<see cref="CellRowSpec.TileEdge"/> First) and the bottom on the
/// note row (Last), the note row joined to the head with no gap between. The cells are laid out so both tiles of a line
/// are equally wide whatever their captions (<see cref="Pair"/>), and the two rows' spans match exactly. Game-free.
/// </summary>
internal static class CellTiles
{
    /// <summary>Inner padding from a tile's rim to its words, each side.</summary>
    internal const float Pad = 14f;

    /// <summary>The head row: room above the link for the tile's top padding.</summary>
    internal const float HeadHeight = 44f;

    /// <summary>The note row: one muted line and the tile's bottom padding.</summary>
    internal const float NoteHeight = 32f;

    /// <summary>Space between two lines of tiles (the head row's gap above, after the first line).</summary>
    internal const float LineGap = 14f;

    /// <summary>The caption size of a tile's link.</summary>
    internal const float CaptionFontSize = SettingsLinks.TextLinkFontSize;

    /// <summary>
    /// The two rows of one line of two tiles. Tile numbers are <paramref name="firstTile"/> and the next. Head row:
    /// <c>[pad][link W][fill]</c> twice, W the wider caption's ask so both links take the same slot; note row:
    /// <c>[pad][note fill][pad]</c> twice. With the kit's inline gap g between every cell and a row width w, each tile
    /// spans (w − g) / 2 on both rows and the second starts g after the first ends.
    /// </summary>
    internal static (CellRowSpec Head, CellRowSpec Note) Pair(
        string headKey, string noteKey, TileSpec left, TileSpec right, int firstTile = 1, bool firstLine = true, float gapAbove = -1f)
    {
        float w = Math.Max(CaptionWidth(left), CaptionWidth(right));
        var head = new List<CellSpec>();
        var note = new List<CellSpec>();
        int tile = firstTile;
        foreach (var t in new[] { left, right })
        {
            head.Add(Spacer(t.Cell + "-pad", tile));
            head.Add(new CellSpec(CellKind.Button, t.Cell).With(text: t.Caption, width: CellWidth.Px(w), minWidth: w,
                align: CellAlign.Left, fontSize: CaptionFontSize, skin: t.Button ? CellSkin.KeyCap : CellSkin.Link, vAlign: CellVAlign.Bottom).InTile(tile));
            head.Add(new CellSpec(CellKind.Muted, t.Cell + "-fill").With(text: "", width: CellWidth.Fill(0f), minWidth: 0f).InTile(tile));

            note.Add(Spacer(t.NoteCell + "-pad", tile));
            note.Add(new CellSpec(CellKind.Muted, t.NoteCell).With(text: t.Note, width: CellWidth.Fill(0f), minWidth: 0f,
                wrap: CellWrap.Ellipsis, vAlign: CellVAlign.Top).InTile(tile));
            note.Add(Spacer(t.NoteCell + "-trail", tile));
            tile++;
        }

        return (
            new CellRowSpec(headKey, head, height: HeadHeight, plain: true, gapAbove: firstLine ? gapAbove : LineGap) { TileEdge = CellSectionEdge.First },
            new CellRowSpec(noteKey, note, height: NoteHeight, plain: true, compact: true, gapAbove: 0f) { TileEdge = CellSectionEdge.Last });
    }

    /// <summary>A link's (or a key cap's) asked width for its caption.</summary>
    internal static float CaptionWidth(TileSpec t) =>
        CellLayout.EstimateTextWidth(t.Caption, CaptionFontSize) + (t.Button ? 36f : 12f);

    private static CellSpec Spacer(string key, int tile) =>
        new CellSpec(CellKind.Muted, key).With(text: "", width: CellWidth.Px(Pad), minWidth: Pad).InTile(tile); // a kit spacer takes no width of its own

    private static CellSpec InTile(this CellSpec spec, int tile)
    {
        spec.Tile = tile;
        return spec;
    }

    /// <summary>One tile's horizontal extent in a placed row, offsets from the row's center like <see cref="CellPlacement"/>.</summary>
    internal readonly record struct TileSpan(int Tile, float Left, float Right)
    {
        internal float Width => Right - Left;
        internal float Center => (Left + Right) * .5f;
    }

    /// <summary>Each tile's span: the left of its first visible cell to the right of its last, in tile order.</summary>
    internal static List<TileSpan> Spans(IReadOnlyList<CellPlacement> placements, IReadOnlyList<CellSpec> specs)
    {
        var bounds = new SortedDictionary<int, (float Left, float Right)>();
        if (placements == null || specs == null) return new List<TileSpan>();
        for (int i = 0; i < placements.Count; i++)
        {
            var p = placements[i];
            if (!p.Visible || p.Index < 0 || p.Index >= specs.Count) continue;
            int tile = specs[p.Index].Tile;
            if (tile <= 0) continue;
            bounds[tile] = bounds.TryGetValue(tile, out var b) ? (Math.Min(b.Left, p.Left), Math.Max(b.Right, p.Right)) : (p.Left, p.Right);
        }

        var spans = new List<TileSpan>(bounds.Count);
        foreach (var pair in bounds) spans.Add(new TileSpan(pair.Key, pair.Value.Left, pair.Value.Right));
        return spans;
    }
}
