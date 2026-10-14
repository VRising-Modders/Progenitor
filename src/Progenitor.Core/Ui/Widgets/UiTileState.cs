namespace Progenitor.Ui;

/// <summary>
/// P7b: how a cell row built with <see cref="CellRowSpec.TileStates"/> draws one of its tiles (the cells sharing a
/// <see cref="CellSpec.Tile"/> number): nothing, a list entry's hover or selection band confined to those cells, or a thin
/// vertical rule. The master-detail widget's master half is one tile (its selection band) and its divider another (the
/// rule), so a full-width row can light only its left part.
/// </summary>
internal enum UiTileState
{
    /// <summary>Nothing drawn.</summary>
    Hidden = 0,

    /// <summary>The Mods list entry's hover wash.</summary>
    Hover = 1,

    /// <summary>The Mods list entry's selected band with the gold stripe at its left.</summary>
    Selected = 2,

    /// <summary>The selected band, brighter under the pointer, with the stripe.</summary>
    SelectedHover = 3,

    /// <summary>A 1-unit vertical rule in the row separator's color down the tile's center, joined to the row above.</summary>
    Rule = 4,
}

/// <summary>P7b: the colors and sizes of <see cref="UiTileState"/> (existing palette entries; no new color).</summary>
internal static class UiTileLook
{
    /// <summary>The rule's width.</summary>
    internal const float RuleWidth = 1f;

    /// <summary>The fill of a band state: the Mods list's own entry colors (<see cref="ModsListLayout.RowFill"/>, <see cref="ModsListLayout.RowHover"/>).</summary>
    internal static PaletteColor Fill(UiTileState state) => state switch
    {
        UiTileState.Hover => ModsListLayout.RowHover(false),
        UiTileState.Selected => ModsListLayout.RowFill(true),
        UiTileState.SelectedHover => ModsListLayout.RowHover(true),
        UiTileState.Rule => MenuPalette.RowSeparator,
        _ => MenuPalette.RowFill,
    };

    /// <summary>Whether the state draws the gold stripe (<see cref="MenuPalette.SelectedStripe"/>, <see cref="MenuPalette.SelectedStripeWidth"/>) at its left.</summary>
    internal static bool Stripe(UiTileState state) => state is UiTileState.Selected or UiTileState.SelectedHover;

    /// <summary>The state's band for an entry: selected or not, hovered or not.</summary>
    internal static UiTileState Entry(bool selected, bool hovered) =>
        selected ? (hovered ? UiTileState.SelectedHover : UiTileState.Selected) : hovered ? UiTileState.Hover : UiTileState.Hidden;
}
