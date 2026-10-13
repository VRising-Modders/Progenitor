using System;
using System.Collections.Generic;
using System.Globalization;

namespace Progenitor.Debugging;

/// <summary>
/// Game-free half of <c>.debug build detached [remove [&lt;count&gt;|&lt;id&gt;|all &lt;count&gt;]]</c>:
/// which castle pieces belong to no castle, which of them a remove takes, the count confirmation,
/// and the reply lines. The interop half (<c>DetachedPiecesRuntime</c>) reads the rows and
/// destroys the chosen pieces.
///
/// <para><b>What "detached" means.</b> A piece whose <c>CastleAreaRequirement</c> is
/// <c>AttachToOwnedArea</c> (walls, floors, pillars, most castle furniture) joins the heart of the
/// territory it stands in when placed normally: <c>CastleHeartConnection</c> names that heart and
/// the piece takes the castle's team. Outside an owned territory the game refuses such a piece
/// (<c>CastleAreaNotOwnedByUser</c>, also with <c>GlobalCastleTerritoryEnabled</c> on), so a
/// normally built one always has a heart. With the game's <c>BuildingPlacementRestrictionsDisabled</c>
/// on (build mode turned it on before 2026-10-09), the placement skips the territory check that
/// picks the heart, so the piece keeps an empty <c>CastleHeartConnection</c> and the prefab's own
/// team; its owner could not damage it in testing. Such a piece still carries <c>UserOwner</c>, so
/// a detached piece is: area requirement <c>AttachToOwnedArea</c>, empty heart, an owner.</para>
///
/// <para><b>Why remove and not re-attach.</b> Joining a piece to a heart is the placement's job
/// (heart, territory, team and the castle's own bookkeeping in one step); writing those fields by
/// hand would skip whatever else that step does. Removing goes through the game's own destroy
/// path with no refund, and while build mode is on, building the piece again costs nothing and
/// joins the castle. A piece that may hold items is never removed.</para>
/// </summary>
internal static class DetachedPieces
{
    /// <summary>One piece as read from the world. <see cref="MayHoldItems"/> is only read for detached rows.</summary>
    internal readonly record struct Row(
        string Id,
        string Prefab,
        float X,
        float Y,
        float Z,
        byte AreaRequirement,
        bool HasHeart,
        bool HasOwner,
        string OwnerKey,
        string OwnerName,
        bool MayHoldItems = false);

    /// <summary>How many pieces a reply lists one by one; the rest are counted.</summary>
    internal const int ListLimit = 20;

    /// <summary>True when the row is a piece that should have joined a castle and did not.</summary>
    internal static bool IsDetached(in Row row) =>
        row.AreaRequirement == Layout.CastleAreaRequirementTypeAttachToOwnedArea && !row.HasHeart && row.HasOwner;

    /// <summary>The detached rows, in the order given.</summary>
    internal static List<Row> Detached(IEnumerable<Row> rows)
    {
        var found = new List<Row>();
        foreach (var row in rows)
            if (IsDetached(row)) found.Add(row);
        return found;
    }

    /// <summary>
    /// What a remove matches: the one detached piece whose id is <paramref name="piece"/> (any owner:
    /// the admin named it), else every detached piece with <paramref name="all"/>, else the caller's
    /// own (<paramref name="callerKey"/> matches <see cref="Row.OwnerKey"/>). A piece that is not
    /// detached is never matched. Pieces that may hold items are matched here and skipped by the remove.
    /// </summary>
    internal static List<Row> ToRemove(IEnumerable<Row> rows, string callerKey, bool all, string piece = "")
    {
        var take = new List<Row>();
        foreach (var row in rows)
        {
            if (!IsDetached(row)) continue;
            bool pick = piece.Length > 0
                ? string.Equals(row.Id, piece, StringComparison.Ordinal)
                : all || string.Equals(row.OwnerKey, callerKey, StringComparison.Ordinal);
            if (pick) take.Add(row);
        }
        return take;
    }

    /// <summary>
    /// The count confirmation. <c>remove all</c> always needs the count; the caller's own
    /// <c>remove</c> needs it when it matches more than one piece; <c>remove &lt;id&gt;</c> never does.
    /// Returns the refusal (with the preview count and the exact command to confirm), or empty when
    /// the remove may go ahead. <paramref name="confirm"/> is -1 when no count was given.
    /// </summary>
    internal static string ConfirmRefusal(int matching, int confirm, bool all, string piece)
    {
        if (piece.Length > 0 || matching == 0) return "";
        bool needed = all || matching > 1;
        if (!needed && confirm < 0) return "";
        if (confirm == matching) return "";
        string command = ".debug build detached remove " + (all ? "all " : "") + matching.ToString(CultureInfo.InvariantCulture);
        string head = confirm < 0
            ? "This would remove " + matching + " detached piece(s)" + (all ? " on the whole server" : " of yours") + "."
            : "The count " + confirm + " does not match: this would remove " + matching + " detached piece(s)"
              + (all ? " on the whole server" : " of yours") + " now. Nothing was removed.";
        return head + " There is no refund. To confirm, run " + command + ".";
    }

    /// <summary>The reply to <c>.debug build detached</c>.</summary>
    internal static List<string> ListLines(IReadOnlyList<Row> detached, string callerKey)
    {
        var lines = new List<string>();
        if (detached.Count == 0)
        {
            lines.Add("No detached pieces: every castle piece on this server belongs to a castle.");
            return lines;
        }
        int mine = 0, holding = 0;
        foreach (var row in detached)
        {
            if (string.Equals(row.OwnerKey, callerKey, StringComparison.Ordinal)) mine++;
            if (row.MayHoldItems) holding++;
        }
        lines.Add(detached.Count + " detached piece(s) belong to no castle (" + mine + " yours). They were probably built while "
            + "the game's BuildingPlacementRestrictionsDisabled was on; the owner could not damage such a piece in testing.");
        lines.Add("By owner: " + ByOwner(detached) + ".");
        for (int i = 0; i < detached.Count && i < ListLimit; i++) lines.Add(Describe(detached[i]));
        if (detached.Count > ListLimit) lines.Add("... and " + (detached.Count - ListLimit) + " more.");
        if (holding > 0)
            lines.Add(holding + " of them may hold items and will not be removed; empty them first.");
        lines.Add(".debug build detached remove [<count>] removes yours, remove <id> one piece, remove all <count> every one listed. "
            + "No refund; build them again afterwards (with build mode on that costs nothing).");
        return lines;
    }

    /// <summary>The reply to a remove after <paramref name="removed"/> were handed to the game's destroy path.</summary>
    internal static List<string> RemoveLines(IReadOnlyList<Row> removed, IReadOnlyList<Row> holding, int failed, int detachedTotal, bool all, string piece = "")
    {
        var lines = new List<string>();
        if (removed.Count == 0 && holding.Count == 0 && failed == 0)
        {
            lines.Add(piece.Length > 0
                ? "No detached piece " + piece + ": it is gone, or it belongs to a castle. .debug build detached lists them."
                : detachedTotal == 0
                    ? "No detached pieces to remove."
                    : "None of the " + detachedTotal + " detached piece(s) are yours; .debug build detached remove all <count> removes every one.");
            return lines;
        }
        lines.Add("Removed " + removed.Count + " detached piece(s)" + (all || piece.Length > 0 ? "" : " of yours")
            + (holding.Count > 0 ? "; " + holding.Count + " left because they may hold items" : "")
            + (failed > 0 ? "; " + failed + " could not be removed (see the server log)" : "") + ". No refund.");
        for (int i = 0; i < removed.Count && i < ListLimit; i++) lines.Add(Describe(removed[i]));
        if (removed.Count > ListLimit) lines.Add("... and " + (removed.Count - ListLimit) + " more.");
        for (int i = 0; i < holding.Count && i < ListLimit; i++) lines.Add("Has items; empty it first: " + Describe(holding[i]));
        return lines;
    }

    /// <summary>One piece: prefab, position, owner, entity id, and a note when it may hold items.</summary>
    internal static string Describe(in Row row) =>
        (row.Prefab.Length > 0 ? row.Prefab : "piece") + " at " + F(row.X) + ", " + F(row.Y) + ", " + F(row.Z)
        + ", owner " + (row.OwnerName.Length > 0 ? row.OwnerName : row.OwnerKey) + " (" + row.Id + ")"
        + (row.MayHoldItems ? ", may hold items" : "");

    private static string ByOwner(IReadOnlyList<Row> rows)
    {
        var order = new List<string>();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            string name = row.OwnerName.Length > 0 ? row.OwnerName : row.OwnerKey;
            if (!counts.ContainsKey(name))
            {
                counts[name] = 0;
                order.Add(name);
            }
            counts[name]++;
        }
        var parts = new List<string>();
        foreach (var name in order) parts.Add(name + " " + counts[name]);
        return string.Join(", ", parts);
    }

    private static string F(float v) => v.ToString("0.#", CultureInfo.InvariantCulture);
}
