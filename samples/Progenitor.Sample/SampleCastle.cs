using System;
using System.Collections.Generic;
using System.Globalization;
using Progenitor.Castles;
using CastlesApi = Progenitor.Castles.Castles;

#if !PROGENITOR_TESTS
using Progenitor.Commands;
using Progenitor.Ecs;
using Unity.Entities;
using Unity.Transforms;
#endif

namespace Progenitor.Sample;

/// <summary>
/// Sample <c>.castle</c> subcommands over <c>ctx.Castles</c>. Parsing and every reply string are
/// game-free so the test project compiles this file and asserts the exact lines; only the ECS
/// position lookup stays behind <c>!PROGENITOR_TESTS</c>.
/// </summary>
internal static class SampleCastle
{
    /// <summary>Prefab rows printed before the <c>+N more</c> tail. Chat replies split at 500 UTF-8 bytes.</summary>
    internal const int MaxPrefabRows = 8;

    /// <summary>Plot rows <c>.castle all</c> prints before its <c>+N more</c> tail.</summary>
    internal const int MaxPlotRows = 6;

    /// <summary>Tile rows <c>.castle tiles</c> prints before its <c>+N more</c> tail.</summary>
    internal const int MaxTileRows = 4;

    /// <summary>
    /// Entries of <see cref="CastlePlacementCheck.NotChecked"/> printed in full by
    /// <c>.castle canplace</c>; the rest are counted. All eleven would blow the 500-byte split.
    /// </summary>
    internal const int MaxNotCheckedRows = 3;

    /// <summary>Plot rows <c>.castle raw</c> reads in detail; the rest are counted.</summary>
    internal const int MaxRawPlots = 3;

    /// <summary>What the caller asked for.</summary>
    internal enum Kind
    {
        /// <summary>Plot summary plus the count of buildings by prefab.</summary>
        Summary = 1,

        /// <summary>Whether the caller stands inside their own territory.</summary>
        Inside = 2,

        /// <summary>Every plot on the server through <c>Castles.All</c>.</summary>
        All = 3,

        /// <summary>One plot through <c>Castles.ForTerritory</c>, then <c>Refresh()</c>.</summary>
        Territory = 4,

        /// <summary>Floor cells of the caller's castle through <c>ICastleHandle.Tiles</c>.</summary>
        Tiles = 5,

        /// <summary>A <c>CastlePlacementCheck</c> for one prefab at the caller's position.</summary>
        CanPlace = 6,

        /// <summary>Raw <c>CastleReader</c> row counts through <c>ctx.Castles</c>.</summary>
        Raw = 7,
    }

    /// <summary>The one usage line; <c>SampleCommands</c> prints it on a parse failure.</summary>
    internal const string UsageReply =
        "Usage: .castle [summary | inside | all | territory <n> | tiles | canplace <prefabGuidOrName> | raw]";

    /// <summary>Parse the single optional argument. Empty means <see cref="Kind.Summary"/>.</summary>
    internal static bool TryParse(string? token, out Kind kind) =>
        TryParse(token, out kind, out _, out _);

    /// <summary>
    /// Parse the whole argument tail. <c>territory</c> takes an index and <c>canplace</c> takes a
    /// prefab token (numeric GUID hash or spawnable name); every other subcommand takes nothing,
    /// and a trailing extra word is a parse failure rather than a silently ignored argument.
    /// </summary>
    internal static bool TryParse(string? args, out Kind kind, out int territoryIndex, out string prefab)
    {
        kind = Kind.Summary;
        territoryIndex = 0;
        prefab = "";
        if (string.IsNullOrWhiteSpace(args))
            return true;

        string text = args.Trim();
        int space = text.IndexOf(' ');
        string head = (space < 0 ? text : text.Substring(0, space)).ToLowerInvariant();
        string rest = space < 0 ? "" : text.Substring(space + 1).Trim();

        switch (head)
        {
            case "summary":
                kind = Kind.Summary;
                return rest.Length == 0;
            case "inside":
            case "in":
                kind = Kind.Inside;
                return rest.Length == 0;
            case "all":
                kind = Kind.All;
                return rest.Length == 0;
            case "tiles":
                kind = Kind.Tiles;
                return rest.Length == 0;
            case "raw":
                kind = Kind.Raw;
                return rest.Length == 0;
            case "territory":
                kind = Kind.Territory;
                return int.TryParse(rest, NumberStyles.Integer, CultureInfo.InvariantCulture, out territoryIndex);
            case "canplace":
                kind = Kind.CanPlace;
                prefab = rest;
                return rest.Length > 0;
            default:
                return false;
        }
    }

    /// <summary>Reply when the caller owns no castle. Same line for both subcommands.</summary>
    internal const string NoCastleReply = "No castle: you do not own a castle heart on this server.";

    /// <summary>
    /// Two reply lines for <c>.castle</c>: the plot summary, then the buildings grouped by
    /// prefab. Returns a single <see cref="NoCastleReply"/> line when the handle is disabled.
    /// </summary>
    internal static IReadOnlyList<string> Summary(ICastleHandle? castle)
    {
        if (castle == null || !castle.Enabled)
            return new[] { NoCastleReply };

        var t = castle.Territory;
        var buildings = castle.Buildings();
        var rows = CastleCounts.ByPrefab(buildings);

        string owner = OwnerLabel(castle);

        var flags = new List<string>(3);
        if (castle.Raided) flags.Add("raided");
        if (castle.Decaying) flags.Add("decaying");
        if (!castle.IsTilePrecise) flags.Add("bounds-only");

        string line1 =
            "Castle #" + N(t.Index) +
            " lv" + N(castle.Level) +
            " — " + owner +
            " — heart " + castle.Heart +
            ", territory " + N(t.MinX) + "," + N(t.MinY) + ".." + N(t.MaxX) + "," + N(t.MaxY) +
            " (" + N(t.Width) + "×" + N(t.Depth) + " tiles, " + N(t.BlockCount) + " blocks)" +
            ", floors " + N(castle.FloorCount) +
            (flags.Count > 0 ? " [" + string.Join(", ", flags) + "]" : "");

        string line2 = "Buildings (" + N(buildings.Count) + "): " + CastleCounts.Format(rows, MaxPrefabRows);

        return new[] { line1, line2 };
    }

    /// <summary>
    /// Reply for <c>.castle inside</c>. <paramref name="position"/> is the caller's world
    /// position; the tile it maps to is printed either way so a "no" is diagnosable.
    /// </summary>
    internal static string Inside(ICastleHandle? castle, CastleVec3 position)
    {
        if (castle == null || !castle.Enabled)
            return NoCastleReply;

        var t = castle.Territory;
        string where = CastleGeometry.TryWorldToTile(position, out int x, out int y)
            ? "tile " + N(x) + "," + N(y)
            : "no tile (position " + position + ")";

        string how = castle.IsTilePrecise ? "buildable-tile mask" : "territory bounds";

        return castle.IsInside(position)
            ? "Inside castle #" + N(t.Index) + " — you are at " + where + ", checked against the " + how + "."
            : "Outside castle #" + N(t.Index) + " — you are at " + where +
              ", territory is " + N(t.MinX) + "," + N(t.MinY) + ".." + N(t.MaxX) + "," + N(t.MaxY) +
              " (checked against the " + how + ").";
    }

    /// <summary>Reply when the server holds no castle at all.</summary>
    internal const string NoCastlesReply =
        "No castles: Castles.All() is empty (client side, module skipped, or nothing claimed here).";

    /// <summary>
    /// Reply for <c>.castle all</c>: one line per plot from <see cref="CastlesApi.All"/>, capped
    /// at <see cref="MaxPlotRows"/> with a <c>+N more</c> tail.
    /// </summary>
    /// <param name="castles"><see cref="CastlesApi.All"/>.</param>
    /// <param name="owned"><see cref="CastlesApi.AllForUser"/> count for the caller.</param>
    /// <param name="standingIn"><see cref="CastlesApi.At(CastleVec3)"/> for the caller's position.</param>
    internal static IReadOnlyList<string> AllReply(
        IReadOnlyList<ICastleHandle>? castles, int owned, ICastleHandle? standingIn)
    {
        if (castles == null || castles.Count == 0)
            return new[] { NoCastlesReply };

        var lines = new List<string>(castles.Count + 3)
        {
            "Castles (" + N(castles.Count) + ") via Castles.All():",
        };

        int shown = Math.Min(castles.Count, MaxPlotRows);
        for (int i = 0; i < shown; i++)
            lines.Add("  " + PlotLine(castles[i]));

        if (castles.Count > shown)
            lines.Add("  +" + N(castles.Count - shown) + " more.");

        lines.Add("You own " + N(owned) + " of these (Castles.AllForUser); " +
                  (standingIn?.Enabled == true
                      ? "you are standing in castle #" + N(standingIn.Territory.Index) + " (Castles.At)."
                      : "you are not standing in any castle (Castles.At)."));

        return lines;
    }

    /// <summary>One <c>.castle all</c> row: identity, level, owner, heart, bounds, floors, flags.</summary>
    internal static string PlotLine(ICastleHandle castle)
    {
        if (castle == null || !castle.Enabled)
            return "disabled handle (" + (castle?.DisabledReason ?? "null") + ")";

        var t = castle.Territory;
        var flags = new List<string>(2);
        if (castle.Raided) flags.Add("raided");
        if (castle.Decaying) flags.Add("decaying");

        return "#" + N(t.Index) +
               " lv" + N(castle.Level) +
               " — " + OwnerLabel(castle) +
               " — heart " + castle.Heart +
               ", territory " + N(t.MinX) + "," + N(t.MinY) + ".." + N(t.MaxX) + "," + N(t.MaxY) +
               ", floors " + N(castle.FloorCount) +
               (flags.Count > 0 ? " [" + string.Join(", ", flags) + "]" : "");
    }

    /// <summary>
    /// Reply for <c>.castle territory &lt;n&gt;</c>. <see cref="CastlesApi.ForTerritory"/> is the
    /// reload-safe lookup — the territory index is the key the game keeps stable — and
    /// <see cref="ICastleHandle.Refresh"/> is the same recovery from a handle you already hold.
    /// </summary>
    internal static IReadOnlyList<string> TerritoryReply(int territoryIndex)
    {
        var castle = CastlesApi.ForTerritory(territoryIndex);
        if (!castle.Enabled)
        {
            return new[]
            {
                "No castle in territory " + N(territoryIndex) +
                ": ForTerritory returned a disabled handle (" + castle.DisabledReason + ").",
            };
        }

        var fresh = castle.Refresh();
        var byHeart = CastlesApi.ForHeart(castle.Heart);
        return new[]
        {
            "ForTerritory(" + N(territoryIndex) + ") → " + castle.Snapshot,
            "Refresh() → " + (fresh.Enabled
                ? fresh.Snapshot.ToString()
                : "disabled (" + fresh.DisabledReason + ")"),
            "ForHeart(" + castle.Heart + ") → " + (byHeart.Enabled
                ? "castle #" + N(byHeart.Territory.Index) + ", the same plot this session only"
                : "disabled (" + byHeart.DisabledReason + ")"),
            "Persist the territory index, not the heart: " + castle.Heart +
                " is re-assigned by a save reload, " + N(territoryIndex) + " is not.",
        };
    }

    /// <summary>
    /// Reply for <c>.castle tiles</c>: the floor-cell count from
    /// <see cref="ICastleHandle.Tiles"/>, the cells grouped by floor-type name, and the first
    /// <see cref="MaxTileRows"/> rows.
    /// </summary>
    internal static IReadOnlyList<string> TilesReply(ICastleHandle? castle)
    {
        if (castle == null || !castle.Enabled)
            return new[] { NoCastleReply };

        int index = castle.Territory.Index;
        var tiles = castle.Tiles();
        if (tiles.Count == 0)
        {
            return new[]
            {
                "Tiles: none on castle #" + N(index) +
                " — the plot has no floors, or the floor rows were not readable.",
            };
        }

        var kinds = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < tiles.Count; i++)
        {
            string key = tiles[i].FloorTypeName.Length > 0
                ? tiles[i].FloorTypeName
                : N(tiles[i].FloorType);
            kinds[key] = kinds.TryGetValue(key, out int n) ? n + 1 : 1;
        }

        var rows = new List<KeyValuePair<string, int>>(kinds.Count);
        foreach (var pair in kinds)
            rows.Add(pair);
        rows.Sort(static (a, b) =>
        {
            int c = b.Value.CompareTo(a.Value);
            return c != 0 ? c : string.CompareOrdinal(a.Key, b.Key);
        });

        int shown = Math.Min(tiles.Count, MaxTileRows);
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < shown; i++)
        {
            if (i > 0) sb.Append("; ");
            sb.Append(tiles[i]);
        }

        if (tiles.Count > shown)
            sb.Append("; +").Append(N(tiles.Count - shown)).Append(" more");

        return new[]
        {
            "Tiles (" + N(tiles.Count) + ") on castle #" + N(index) +
                " — floor types: " + CastleCounts.Format(rows, MaxPrefabRows),
            "First " + N(shown) + ": " + sb,
        };
    }

    /// <summary>What <c>.castle canplace</c> says when the prefab token resolved to nothing.</summary>
    internal static string UnknownPrefabReply(string token) =>
        "Unknown prefab '" + token +
        "': pass a numeric PrefabGUID hash, or a spawnable name this server can resolve.";

    /// <summary>
    /// Reply for <c>.castle canplace</c>. Prints the whole <see cref="CastlePlacementCheck"/>,
    /// including <see cref="CastlePlacementCheck.Complete"/> (always false) and the head of
    /// <see cref="CastlePlacementCheck.NotChecked"/> — the caveat that makes
    /// <see cref="CastlePlacementCheck.Allowed"/> a partial verdict rather than the game's answer.
    /// </summary>
    internal static IReadOnlyList<string> CanPlaceReply(
        ICastleHandle? castle, int prefabGuid, string label, CastleVec3 position)
    {
        if (castle == null || !castle.Enabled)
            return new[] { NoCastleReply };

        var check = castle.CanPlace(prefabGuid, position);
        string where = CastleGeometry.TryWorldToTile(position, out int x, out int y)
            ? "tile " + N(x) + "," + N(y)
            : "no tile (position " + position + ")";

        var lines = new List<string>(MaxNotCheckedRows + 4)
        {
            "CanPlace(" + label + ") at " + where +
                " in castle #" + N(castle.Territory.Index) + ": " + check,
            "Footprint " + N(check.FootprintMinX) + "," + N(check.FootprintMinY) +
                ".." + N(check.FootprintMaxX) + "," + N(check.FootprintMaxY) +
                " h" + N(check.HeightLevel) +
                " — footprint resolved " + B(check.FootprintResolved) +
                ", tile mask read " + B(check.BuildableTilesRead) +
                ", height ambiguous " + B(check.HeightAmbiguous) + ".",
        };

        var notChecked = check.NotChecked;
        lines.Add("Complete=" + B(check.Complete) + " — " + N(notChecked.Count) +
                  " vanilla rules are not checked:");

        int shown = Math.Min(notChecked.Count, MaxNotCheckedRows);
        for (int i = 0; i < shown; i++)
            lines.Add("  - " + notChecked[i]);

        if (notChecked.Count > shown)
            lines.Add("  +" + N(notChecked.Count - shown) + " more in CastlePlacementCheck.NotChecked.");

        return lines;
    }

    /// <summary>One plot's raw <see cref="CastleReader"/> row counts, game-free.</summary>
    internal readonly struct RawRow
    {
        /// <summary>Construct a raw row.</summary>
        internal RawRow(int territoryIndex, int tiles, int buildings, int blockCount, bool blocksSkipped)
        {
            TerritoryIndex = territoryIndex;
            Tiles = tiles;
            Buildings = buildings;
            BlockCount = blockCount;
            BlocksSkipped = blocksSkipped;
        }

        /// <summary><c>CastlePlot.TerritoryIndex</c>.</summary>
        internal int TerritoryIndex { get; }

        /// <summary><c>CastleReader.Tiles</c> row count.</summary>
        internal int Tiles { get; }

        /// <summary><c>CastleReader.Buildings</c> row count.</summary>
        internal int Buildings { get; }

        /// <summary><c>CastlePlot.BlockCount</c>.</summary>
        internal int BlockCount { get; }

        /// <summary><c>CastlePlot.BlocksSkipped</c>.</summary>
        internal bool BlocksSkipped { get; }
    }

    /// <summary>Reply when the raw reader found no plots.</summary>
    internal const string NoRawPlotsReply =
        "CastleReader.Plots() returned no rows (client side, or nothing claimed on this server).";

    /// <summary>
    /// The <c>PlotOf</c> line when the first plot had no building row to ask about. An empty plot
    /// is a real state — a claimed territory with a heart and nothing built yet — so this is
    /// information, not a failure.
    /// </summary>
    internal const string NoPlotOfSubjectReply =
        "  ctx.Castles.PlotOf: the first plot has no building row to ask about, so nothing was "
        + "looked up. PlotOf answers for a heart, a territory entity, or any placed tile-model.";

    /// <summary>
    /// The <c>PlotOf</c> line. <paramref name="fromPlot"/> is the plot the row was read from and
    /// <paramref name="resolved"/> what <c>ctx.Castles.PlotOf(entity)</c> answered
    /// (0 or less for null). Equality is the pass condition: it is the round trip that shows
    /// <c>PlotOf</c> is the inverse of <c>Buildings(plot)</c>, which is what makes it useful — a
    /// mod that has an entity in hand (a chest it was handed, a wall that took damage) can find
    /// the castle it belongs to without walking every plot.
    /// </summary>
    internal static string PlotOfLine(string? subject, int fromPlot, int resolved)
    {
        string what = string.IsNullOrEmpty(subject) ? "a building on it" : subject!;
        if (resolved <= 0)
            return "  ctx.Castles.PlotOf(" + what + ") -> null. The entity is not a heart, a "
                + "territory or a tile-model with a heart connection - or its heart was not readable.";

        if (resolved == fromPlot)
            return "  ctx.Castles.PlotOf(" + what + ") -> plot #" + N(resolved)
                + ", the plot that row was read from. PlotOf is the inverse of Buildings(plot): "
                + "given any entity on a castle it finds the castle, with no walk over the plots.";

        return "  ctx.Castles.PlotOf(" + what + ") -> plot #" + N(resolved)
            + ", which is NOT the #" + N(fromPlot) + " the row was read from.";
    }

    /// <summary>
    /// Reply for <c>.castle raw</c>: the raw-row counts behind the handles, so the
    /// <c>CastleReader</c> / <c>ctx.Castles</c> layer has a worked example too.
    /// </summary>
    internal static IReadOnlyList<string> RawReply(
        IReadOnlyList<RawRow>? rows, int plotCount, string? plotOf = null)
    {
        if (plotCount <= 0)
            return new[] { NoRawPlotsReply };

        int read = rows?.Count ?? 0;
        var lines = new List<string>(read + 2)
        {
            "CastleReader.Plots(): " + N(plotCount) + " plot(s); rows read for " + N(read) + ".",
        };

        for (int i = 0; i < read; i++)
        {
            var row = rows![i];
            lines.Add("  plot #" + N(row.TerritoryIndex) +
                      " — " + N(row.Tiles) + " tile row(s), " +
                      N(row.Buildings) + " building row(s), " +
                      N(row.BlockCount) + " block(s) (blocks skipped: " + B(row.BlocksSkipped) + ").");
        }

        if (plotCount > read)
            lines.Add("  +" + N(plotCount - read) + " more not read.");

        if (!string.IsNullOrEmpty(plotOf))
            lines.Add(plotOf!);

        return lines;
    }

    /// <summary>
    /// Game-free runner used by tests: resolves the caller's castle through
    /// <see cref="CastlesApi.ForUser"/> and returns the reply lines. <c>.castle raw</c> has no
    /// game-free source of rows, so it is exercised through <see cref="RawReply"/> directly.
    /// </summary>
    internal static IReadOnlyList<string> Run(
        CastleId user,
        Kind kind,
        CastleVec3 position = default,
        int territoryIndex = 0,
        int prefabGuid = 0,
        string prefabLabel = "")
    {
        switch (kind)
        {
            case Kind.Inside:
                return new[] { Inside(CastlesApi.ForUser(user), position) };
            case Kind.All:
                return AllReply(CastlesApi.All, CastlesApi.AllForUser(user).Count, CastlesApi.At(position));
            case Kind.Territory:
                return TerritoryReply(territoryIndex);
            case Kind.Tiles:
                return TilesReply(CastlesApi.ForUser(user));
            case Kind.CanPlace:
                return CanPlaceReply(
                    CastlesApi.ForUser(user),
                    prefabGuid,
                    prefabLabel.Length > 0 ? prefabLabel : N(prefabGuid),
                    position);
            case Kind.Raw:
                return RawReply(Array.Empty<RawRow>(), 0);
            default:
                return Summary(CastlesApi.ForUser(user));
        }
    }

    private static string OwnerLabel(ICastleHandle castle)
    {
        string owner = castle.Owner.Name.Length > 0 ? castle.Owner.Name : castle.Owner.User.ToString();
        if (castle.Owner.HasClan && castle.Owner.ClanName.Length > 0)
            owner += " [" + castle.Owner.ClanName + "]";
        return owner;
    }

    private static string B(bool value) => value ? "yes" : "no";

#if !PROGENITOR_TESTS
    internal static void HandleCommand(ICommandContext ctx, Kind kind, int territoryIndex = 0, string prefab = "")
    {
        var castle = ctx.UserEntity == Entity.Null
            ? null
            : CastlesApi.ForUser(CastleId.From(ctx.UserEntity));

        switch (kind)
        {
            case Kind.All:
            {
                // One read of the property: each get re-snapshots the world.
                var all = CastlesApi.All;
                int owned = ctx.UserEntity == Entity.Null
                    ? 0
                    : CastlesApi.AllForUser(CastleId.From(ctx.UserEntity)).Count;
                ReplyAll(ctx, AllReply(all, owned, CastlesApi.At(CharacterPosition(ctx))));
                Log("all count=" + all.Count + " owned=" + owned);
                return;
            }

            case Kind.Territory:
                ReplyAll(ctx, TerritoryReply(territoryIndex));
                Log("territory " + territoryIndex);
                return;
            case Kind.Tiles:
                ReplyAll(ctx, TilesReply(castle));
                Log("tiles castle=" + (castle?.Enabled == true
                    ? castle.Territory.Index.ToString(CultureInfo.InvariantCulture)
                    : "none"));
                return;
            case Kind.CanPlace:
                HandleCanPlace(ctx, castle, prefab);
                return;
            case Kind.Raw:
                HandleRaw(ctx);
                return;
        }

        if (kind == Kind.Inside)
        {
            ctx.Reply(Inside(castle, CharacterPosition(ctx)));
            Log("inside user=" + ctx.UserEntity.Index + ":" + ctx.UserEntity.Version +
                " castle=" + (castle?.Enabled == true ? castle.Territory.Index.ToString(CultureInfo.InvariantCulture) : "none"));
            return;
        }

        // FSAMP feature 1. Stamp the heart entity with the world-load epoch the first time this
        // command resolves a castle, and never touch it again: `.epoch` reports whether that
        // stamp is stale. This is the retained-entity rule from docs/modules.md "World" written
        // out in the one place the Sample has a long-lived entity id worth keeping.
        if (castle?.Enabled == true)
        {
            SampleEpoch.Stamp(
                "castle#" + castle.Territory.Index.ToString(CultureInfo.InvariantCulture)
                    + " heart " + castle.Heart,
                Plugin.Active?.WorldState.LoadEpoch ?? 0);
        }

        var lines = Summary(castle);
        for (int i = 0; i < lines.Count; i++)
            ctx.Reply(lines[i]);

        Log("summary user=" + ctx.UserEntity.Index + ":" + ctx.UserEntity.Version +
            " castle=" + (castle?.Enabled == true ? castle.Territory.Index.ToString(CultureInfo.InvariantCulture) : "none") +
            " buildings=" + (castle?.Enabled == true ? castle.Buildings().Count : 0));
    }

    private static void ReplyAll(ICommandContext ctx, IReadOnlyList<string> lines)
    {
        for (int i = 0; i < lines.Count; i++)
            ctx.Reply(lines[i]);
    }

    /// <summary>
    /// <c>.castle canplace</c>. The token is a numeric <c>PrefabGUID</c> hash, or a spawnable
    /// name resolved through <see cref="Progenitor.Ecs.PrefabLookup.TryGetGuidByName"/>.
    /// </summary>
    private static void HandleCanPlace(ICommandContext ctx, ICastleHandle? castle, string prefab)
    {
        string label = prefab;
        if (!int.TryParse(prefab, NumberStyles.Integer, CultureInfo.InvariantCulture, out int guid))
        {
            if (!PrefabLookup.TryGetGuidByName(ctx.World, prefab, out var resolved))
            {
                ctx.Reply(UnknownPrefabReply(prefab));
                return;
            }

            guid = resolved.GuidHash;
        }
        else
        {
            label = guid.ToString(CultureInfo.InvariantCulture);
        }

        ReplyAll(ctx, CanPlaceReply(castle, guid, label, CharacterPosition(ctx)));
        Log("canplace " + label + " castle=" +
            (castle?.Enabled == true ? castle.Territory.Index.ToString(CultureInfo.InvariantCulture) : "none"));
    }

    /// <summary>
    /// <c>.castle raw</c>. The raw <see cref="CastleReader"/> layer through
    /// <c>ctx.Castles</c>: plots, and the tile / building row counts of the first
    /// <see cref="MaxRawPlots"/> of them (the per-plot reads walk the world, so they are capped).
    /// </summary>
    private static void HandleRaw(ICommandContext ctx)
    {
        var api = Plugin.Active?.Castles;
        if (api == null)
        {
            ctx.Reply(NoRawPlotsReply);
            return;
        }

        var plots = api.Plots();
        var rows = new List<RawRow>(Math.Min(plots.Count, MaxRawPlots));
        string? plotOf = null;
        for (int i = 0; i < plots.Count && i < MaxRawPlots; i++)
        {
            var plot = plots[i];
            var buildings = api.Buildings(plot);
            rows.Add(new RawRow(
                plot.TerritoryIndex,
                api.Tiles(plot).Count,
                buildings.Count,
                plot.BlockCount,
                plot.BlocksSkipped));

            // The round trip, on the first plot only: take one row's entity back to PlotOf and
            // check it lands on the plot the row came from. One lookup, because PlotOf walks the
            // world for the heart and .castle raw is already capped for that reason.
            if (i == 0)
                plotOf = DescribePlotOf(api, plot.TerritoryIndex, buildings);
        }

        ReplyAll(ctx, RawReply(rows, plots.Count, plotOf));
        Log("raw plots=" + plots.Count + " read=" + rows.Count);
    }

    /// <summary>
    /// <c>ctx.Castles.PlotOf(entity)</c> for the first building on <paramref name="plot"/>.
    /// <c>PlotOf</c> accepts a heart, a territory entity, or any placed tile-model with a
    /// <c>CastleHeartConnection</c> (<c>Castles/CastleReader.cs:176</c>) — a building row is the
    /// third kind, and the one a mod is most likely to be holding.
    /// </summary>
    private static string DescribePlotOf(
        Progenitor.Plugins.PluginCastles api, int plotIndex, IReadOnlyList<CastleBuilding> buildings)
    {
        try
        {
            if (buildings == null || buildings.Count == 0)
                return NoPlotOfSubjectReply;

            var building = buildings[0];
            string subject = string.IsNullOrEmpty(building.PrefabName)
                ? "building " + building.Entity.Index.ToString(CultureInfo.InvariantCulture)
                : "building '" + building.PrefabName + "'";

            var found = api.PlotOf(building.Entity);
            return PlotOfLine(subject, plotIndex, found == null ? 0 : found.TerritoryIndex);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Sample .castle PlotOf threw: " + e.Message);
            return NoPlotOfSubjectReply;
        }
    }

    /// <summary>
    /// Caller's world position from <c>Translation</c>, falling back to <c>LocalToWorld</c>.
    /// Default when there is no character (then <see cref="Inside"/> prints the tile for 0,0,0
    /// and the answer is whatever that tile says, which is a visible wrong-looking result rather
    /// than a silent one).
    /// </summary>
    private static CastleVec3 CharacterPosition(ICommandContext ctx)
    {
        try
        {
            var ch = ctx.CharacterEntity;
            if (ch == Entity.Null) return default;
            var em = ctx.World.EntityManager;
            if (ch.TryRead(em, out Translation translation))
                return translation.Value;
            if (ch.TryRead(em, out LocalToWorld ltw))
                return ltw.Position;
            return default;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Sample .castle position read threw: " + e.Message);
            return default;
        }
    }

    private static void Log(string line) => Plugin.Logger?.LogInfo("Sample castle: " + line);
#endif

    private static string N(int value) => value.ToString(CultureInfo.InvariantCulture);
}
