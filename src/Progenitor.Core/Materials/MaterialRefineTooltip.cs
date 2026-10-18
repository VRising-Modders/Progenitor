using System;
using System.Collections.Generic;
using System.Globalization;

namespace Progenitor.Materials;

/// <summary>
/// The color state the game gives a requirement line of a recipe tooltip
/// (<c>TooltipRecipeRequiredItems.SetData</c>): none in stock is red, enough is green, some but not
/// enough is yellow. Both the amount text and the item name take the state's color.
/// </summary>
internal enum RefineLineState
{
    /// <summary>Nothing in stock: the game's red (<c>StunColor.Red2</c>).</summary>
    Missing = 0,

    /// <summary>Some, but less than the line needs: the game's yellow (<c>StunColor.Yellow2</c>).</summary>
    Partial = 1,

    /// <summary>At least what the line needs: the game's green (<c>StunColor.Green1</c>).</summary>
    Met = 2,
}

/// <summary>One requirement line of the refinement recipe tooltip after Progenitor looked at it.</summary>
internal readonly struct RefineLine
{
    /// <summary>The line's index in the tooltip (the recipe's requirement order).</summary>
    internal int Row { get; init; }

    /// <summary>The required item (PrefabGUID hash).</summary>
    internal int Item { get; init; }

    /// <summary>The amount the line needs, as the game wrote it (station bonuses applied).</summary>
    internal int Need { get; init; }

    /// <summary>The game's own "have" count (the station's own slots), as the game wrote it; -1 when unreadable.</summary>
    internal int GameHave { get; init; }

    /// <summary>The pool total the server sent for the item; -1 when the view does not govern it.</summary>
    internal int ProviderHave { get; init; }

    /// <summary>The count the line shows: <see cref="GameHave"/> plus <see cref="ProviderHave"/> when governed.</summary>
    internal int ShownHave { get; init; }

    /// <summary>The amount text the line shows ("have/need").</summary>
    internal string Text { get; init; }

    /// <summary>The color state the line shows.</summary>
    internal RefineLineState State { get; init; }

    /// <summary>Progenitor writes this line (text and color) this frame.</summary>
    internal bool Write { get; init; }

    /// <summary>Why the line keeps the game's own text, or empty.</summary>
    internal string Note { get; init; }
}

/// <summary>
/// The game-free half of the refinement recipe tooltip counts (<see cref="MaterialOperation.RefineView"/>).
/// The game writes each requirement line as <c>"{inStock}/{amount}"</c> with
/// <c>Int32.ToString()</c> and colors it by <see cref="StateOf"/>; at a refinement station
/// <c>inStock</c> is the station's own count of the item. With a RefineView view for that very station
/// a governed item's line shows the station's count plus the pool total, in the game's own format and
/// color rule; every other line keeps the game's text.
/// </summary>
internal static class MaterialRefineTooltip
{
    /// <summary>The separator the game puts between the two counts.</summary>
    internal const char Separator = '/';

    /// <summary>The game's color rule: none in stock red, enough green, otherwise yellow.</summary>
    internal static RefineLineState StateOf(int have, int need)
    {
        if (have <= 0) return RefineLineState.Missing;
        return have >= need ? RefineLineState.Met : RefineLineState.Partial;
    }

    /// <summary>The game's line text: both counts as invariant integers around <see cref="Separator"/>.</summary>
    internal static string Format(int have, int need) =>
        have.ToString(CultureInfo.InvariantCulture) + Separator + need.ToString(CultureInfo.InvariantCulture);

    /// <summary>Read a line the game wrote ("have/need", both non-negative integers); false for anything else.</summary>
    internal static bool TryParse(string? text, out int have, out int need)
    {
        have = need = 0;
        if (string.IsNullOrEmpty(text)) return false;
        int at = text!.IndexOf(Separator);
        if (at <= 0 || at != text.LastIndexOf(Separator) || at == text.Length - 1) return false;
        return int.TryParse(text.AsSpan(0, at), NumberStyles.None, CultureInfo.InvariantCulture, out have)
            && int.TryParse(text.AsSpan(at + 1), NumberStyles.None, CultureInfo.InvariantCulture, out need);
    }

    /// <summary>Game count plus pool total, saturating at <see cref="int.MaxValue"/>.</summary>
    internal static int Shown(int gameHave, int pool)
    {
        long sum = (long)Math.Max(0, gameHave) + Math.Max(0, pool);
        return sum > int.MaxValue ? int.MaxValue : (int)sum;
    }

    /// <summary>
    /// Whether the tooltip's lines can be matched to the recipe's requirements: the game writes one shown
    /// line per requirement it has item data for, in the recipe's order, so the shown line count must equal
    /// the requirement count. Anything else (an item without data, a different recipe) keeps every line vanilla.
    /// </summary>
    internal static bool LinesMatch(int shownLines, int requirements) => shownLines > 0 && shownLines == requirements;

    /// <summary>
    /// The RefineView totals that apply to the refinement window of the station with network id
    /// <paramref name="stationNet"/>: only a view the server computed for that very station. False (show the
    /// game's own counts) without one, or for a window whose station has no network id.
    /// </summary>
    internal static bool TryTotals(MaterialOverlayStore store, ulong stationNet, out MaterialView view, out IReadOnlyDictionary<int, int> totals)
    {
        if (stationNet != 0 && store.TryGet(MaterialOperation.RefineView, out view, out totals) && view.StationNet == stationNet) return true;
        view = null!;
        totals = null!;
        return false;
    }

    /// <summary>
    /// Decide one line from the text the game wrote this frame (<paramref name="current"/>, "have/need")
    /// and <paramref name="totals"/>, the RefineView view's pool totals for this station (null for none).
    /// A governed item's line shows the game's count plus the pool total; any other line is left alone.
    /// </summary>
    internal static RefineLine Decide(int row, int item, string? current, IReadOnlyDictionary<int, int>? totals)
    {
        if (!TryParse(current, out int gameHave, out int need))
            return new RefineLine
            {
                Row = row, Item = item, Need = -1, GameHave = -1, ProviderHave = -1, ShownHave = -1,
                Text = current ?? "", State = RefineLineState.Missing, Write = false, Note = "unreadable line",
            };

        int pool = -1;
        if (totals != null && totals.TryGetValue(item, out int t)) pool = Math.Max(0, t);
        int shown = pool >= 0 ? Shown(gameHave, pool) : gameHave;
        return new RefineLine
        {
            Row = row, Item = item, Need = need, GameHave = gameHave, ProviderHave = pool, ShownHave = shown,
            Text = Format(shown, need), State = StateOf(shown, need), Write = pool >= 0, Note = pool >= 0 ? "" : "not governed",
        };
    }
}

/// <summary>
/// The lines Progenitor wrote over the game's text in the last update, with the game's text and state
/// under each. Before the game's next update of the refinement window those lines are put back to the
/// game's own text, so whatever a line holds after that update is the game's (rewritten, or the game's
/// old text when the game skipped the line) and Progenitor's number is never read back as a game count.
/// Game-free.
/// </summary>
internal sealed class RefineRowMemo
{
    private readonly Dictionary<int, (string Written, string GameText, RefineLineState GameState)> _rows = new();

    /// <summary>Record a line written over the game's <paramref name="gameText"/> (lines not written are ignored).</summary>
    internal void Wrote(in RefineLine line, string gameText)
    {
        if (!line.Write) return;
        _rows[line.Row] = (line.Text, gameText, MaterialRefineTooltip.StateOf(line.GameHave, line.Need));
    }

    /// <summary>
    /// For a line Progenitor wrote: the game's text and state to put back, when the line still holds
    /// Progenitor's text (<paramref name="current"/>); false when it holds something else (the game
    /// replaced it) or was not written.
    /// </summary>
    internal bool TryRestore(int row, string? current, out string gameText, out RefineLineState gameState)
    {
        if (current != null && _rows.TryGetValue(row, out var r) && string.Equals(r.Written, current, StringComparison.Ordinal))
        {
            gameText = r.GameText;
            gameState = r.GameState;
            return true;
        }
        gameText = "";
        gameState = RefineLineState.Missing;
        return false;
    }

    /// <summary>The rows written, for the restore pass.</summary>
    internal IEnumerable<int> Rows => _rows.Keys;

    /// <summary>Lines currently holding Progenitor's text.</summary>
    internal int Count => _rows.Count;

    internal void Clear() => _rows.Clear();
}
