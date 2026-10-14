using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Progenitor.Stations;

#if !PROGENITOR_TESTS
using Progenitor.Commands;
using Progenitor.Ecs;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
#endif

namespace Progenitor.Sample;

/// <summary>
/// <c>.stations list | recipes | toggle | status</c> over <c>ctx.Stations</c> (<c>docs/api.md</c>
/// Stations). Server only. A station is named by the <c>index:version</c> id <c>.stations list</c>
/// prints, or omitted to mean the nearest recipe station within <see cref="NearestRadius"/> meters.
///
/// <para>Parsing and every reply line are game-free so the test project compiles this file.
/// Source-checked and game-free tested; not live-verified.</para>
/// </summary>
internal static class SampleStations
{
    internal const string Area = "stations";

    /// <summary>Search radius for <c>.stations list</c> without a radius, and for the implicit nearest station.</summary>
    internal const float NearestRadius = 20f;

    /// <summary>Largest radius <c>.stations list</c> accepts.</summary>
    internal const float MaxRadius = 200f;

    /// <summary>Rows printed before a <c>+N more</c> tail (chat splits at 500 bytes).</summary>
    internal const int MaxRows = 8;

    internal const string UsageReply =
        "Usage: .stations list [radius] | recipes [id] | toggle <recipeNameOrGuid> on|off [id] | status [id]";

    internal const string NoStationReply = "No recipe station within 20 m. Stand next to one or pass an id from .stations list.";

    internal enum Kind
    {
        List = 1,
        Recipes = 2,
        Toggle = 3,
        Status = 4,
    }

    /// <summary>A parsed command.</summary>
    internal sealed class Request
    {
        public Kind Kind;
        public float Radius = NearestRadius;
        public string Recipe = "";
        public bool On;
        public int StationIndex = -1;
        public int StationVersion;
        public bool HasStation => StationIndex >= 0;
    }

    /// <summary>Parse <c>.stations verb a b c</c>.</summary>
    internal static bool TryParse(string? verb, string? a, string? b, string? c, out Request request)
    {
        request = new Request();
        a = (a ?? "").Trim();
        b = (b ?? "").Trim();
        c = (c ?? "").Trim();
        switch ((verb ?? "").Trim().ToLowerInvariant())
        {
            case "":
            case "list":
                request.Kind = Kind.List;
                if (b.Length > 0 || c.Length > 0) return false;
                if (a.Length == 0) return true;
                return float.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out request.Radius)
                    && request.Radius > 0 && request.Radius <= MaxRadius;
            case "recipes":
                request.Kind = Kind.Recipes;
                return b.Length == 0 && c.Length == 0 && OptionalStation(a, request);
            case "status":
                request.Kind = Kind.Status;
                return b.Length == 0 && c.Length == 0 && OptionalStation(a, request);
            case "toggle":
                request.Kind = Kind.Toggle;
                request.Recipe = a;
                if (a.Length == 0) return false;
                switch (b.ToLowerInvariant())
                {
                    case "on": request.On = true; break;
                    case "off": request.On = false; break;
                    default: return false;
                }
                return OptionalStation(c, request);
            default:
                return false;
        }
    }

    private static bool OptionalStation(string token, Request request)
    {
        if (token.Length == 0) return true;
        if (!TryParseId(token, out int index, out int version)) return false;
        request.StationIndex = index;
        request.StationVersion = version;
        return true;
    }

    /// <summary>Parse an <c>index:version</c> entity id.</summary>
    internal static bool TryParseId(string token, out int index, out int version)
    {
        index = -1;
        version = 0;
        int colon = token.IndexOf(':');
        if (colon <= 0 || colon == token.Length - 1) return false;
        return int.TryParse(token.Substring(0, colon), NumberStyles.Integer, CultureInfo.InvariantCulture, out index)
            && int.TryParse(token.Substring(colon + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out version)
            && index >= 0 && version > 0;
    }

    /// <summary>
    /// The recipe a toggle names: a signed GUID hash, or a recipe name matched case-insensitively,
    /// exactly first, then as the only row containing it. Null with a reason otherwise.
    /// </summary>
    internal static StationRecipe? MatchRecipe(IReadOnlyList<StationRecipe> recipes, string token, out string why)
    {
        why = "";
        if (int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out int guid))
        {
            foreach (var r in recipes)
                if (r.RecipeGuid == guid) return r;
            why = "This station has no recipe " + guid + ".";
            return null;
        }
        foreach (var r in recipes)
            if (string.Equals(r.Name, token, StringComparison.OrdinalIgnoreCase)) return r;
        StationRecipe? only = null;
        int hits = 0;
        foreach (var r in recipes)
        {
            if (r.Name.IndexOf(token, StringComparison.OrdinalIgnoreCase) < 0) continue;
            only = r;
            hits++;
        }
        if (hits == 1) return only;
        why = hits == 0
            ? "This station has no recipe matching '" + token + "'."
            : "'" + token + "' matches " + hits + " recipes; use the full name or the GUID.";
        return null;
    }

    internal static string ListReply(IReadOnlyList<(string Id, string Name, StationKind Kind, float Distance)> rows, float radius)
    {
        if (rows.Count == 0) return "No recipe stations within " + F(radius) + " m.";
        var sb = new StringBuilder();
        sb.Append(rows.Count).Append(" station(s) within ").Append(F(radius)).Append(" m:");
        for (int i = 0; i < rows.Count && i < MaxRows; i++)
        {
            var r = rows[i];
            sb.Append(' ').Append(r.Id).Append(' ').Append(Short(r.Name)).Append(" (")
              .Append(r.Kind == StationKind.Refinement ? "refine" : "craft").Append(", ").Append(F(r.Distance)).Append(" m);");
        }
        if (rows.Count > MaxRows) sb.Append(" +").Append(rows.Count - MaxRows).Append(" more");
        return sb.ToString();
    }

    /// <summary>One line per recipe: switch, name, inputs to outputs, seconds.</summary>
    internal static List<string> RecipeLines(IReadOnlyList<StationRecipe> recipes, Func<int, string> itemName)
    {
        var lines = new List<string>();
        if (recipes.Count == 0)
        {
            lines.Add("This station lists no recipes.");
            return lines;
        }
        int shown = 0;
        foreach (var r in recipes)
        {
            if (shown == MaxRows) break;
            var sb = new StringBuilder();
            sb.Append(r.CanToggle ? (r.Enabled ? "[on] " : "[off] ") : "");
            sb.Append(Short(r.Name.Length > 0 ? r.Name : r.RecipeGuid.ToString(CultureInfo.InvariantCulture)));
            if (!r.Unlocked) sb.Append(" (locked)");
            sb.Append(": ").Append(Items(r.Inputs, itemName)).Append(" -> ").Append(Items(r.Outputs, itemName));
            sb.Append(", ").Append(F(r.DurationSeconds)).Append(" s");
            lines.Add(sb.ToString());
            shown++;
        }
        if (recipes.Count > MaxRows) lines.Add("+" + (recipes.Count - MaxRows) + " more");
        return lines;
    }

    internal static string ToggleReply(StationToggleResult result, string recipeName)
    {
        if (!result.Ok) return "Toggle refused for " + Short(recipeName) + ": " + result.Reason + ".";
        return Short(recipeName) + " is now " + (result.Enabled ? "on" : "off")
            + (result.Changed ? "" : " (no change)")
            + (result.LinkedRowsChanged > 0 ? "; " + result.LinkedRowsChanged + " linked recipe(s) switched with it" : "") + ".";
    }

    internal static string StatusReply(StationProgress? p, string recipeName)
    {
        if (p == null) return "Not a recipe station.";
        var sb = new StringBuilder();
        sb.Append(p.Kind == StationKind.Refinement ? "Refinement" : "Workstation").Append(": ").Append(StationLogic.Describe(p.State));
        if (p.RecipeGuid != 0)
        {
            sb.Append(", ").Append(Short(recipeName.Length > 0 ? recipeName : p.RecipeGuid.ToString(CultureInfo.InvariantCulture)));
            sb.Append(' ').Append(((int)Math.Round(p.Fraction * 100)).ToString(CultureInfo.InvariantCulture)).Append('%');
            sb.Append(", ").Append(F(p.RemainingSeconds)).Append(" s left");
        }
        if (p.Kind == StationKind.Workstation) sb.Append(", queue ").Append(p.QueueLength);
        sb.Append(" (working=").Append(p.IsWorking ? "yes" : "no").Append(", raw status ").Append(p.RawStatus).Append(").");
        return sb.ToString();
    }

    private static string Items(IReadOnlyList<StationItem> items, Func<int, string> itemName)
    {
        if (items.Count == 0) return "nothing";
        var parts = new List<string>(items.Count);
        foreach (var it in items) parts.Add(it.Amount + " " + Short(itemName(it.ItemGuid)));
        return string.Join(" + ", parts);
    }

    /// <summary>Drop the common prefab prefixes so lines fit chat.</summary>
    internal static string Short(string name)
    {
        foreach (var prefix in new[] { "Recipe_Ingredient_", "Recipe_", "Item_Ingredient_", "Item_", "TM_RefinementStation_", "TM_CraftingStation_" })
            if (name.StartsWith(prefix, StringComparison.Ordinal) && name.Length > prefix.Length) return name.Substring(prefix.Length);
        return name;
    }

    private static string F(float value) => value.ToString("0.#", CultureInfo.InvariantCulture);

#if !PROGENITOR_TESTS
    internal static void HandleCommand(ICommandContext ctx, Request request)
    {
        var plugin = Plugin.Active;
        if (plugin == null || plugin.World == null)
        {
            ctx.Reply("Sample plugin context or server world is not available.");
            return;
        }
        var world = plugin.World;
        var stations = plugin.Stations;
        var here = Position(ctx);

        if (request.Kind == Kind.List)
        {
            var found = stations.Near(here, request.Radius);
            var rows = new List<(string, string, StationKind, float)>();
            foreach (var s in found) rows.Add((Id(s.Station), s.Name, s.Kind, s.Distance));
            ctx.Reply(ListReply(rows, request.Radius));
            SampleLog.Info(Area, "list radius=" + F(request.Radius) + " found=" + found.Count);
            return;
        }

        Entity station;
        if (request.HasStation)
        {
            station = new Entity { Index = request.StationIndex, Version = request.StationVersion };
        }
        else
        {
            var near = stations.Near(here, NearestRadius);
            if (near.Count == 0)
            {
                ctx.Reply(NoStationReply);
                return;
            }
            station = near[0].Station;
        }
        if (stations.Kind(station) == StationKind.None)
        {
            ctx.Reply("Entity " + Id(station) + " is not a recipe station.");
            return;
        }

        string Name(int guid) => PrefabLookup.TryGetName(world, new Stunlock.Core.PrefabGUID(guid), out var n) ? n : guid.ToString(CultureInfo.InvariantCulture);

        switch (request.Kind)
        {
            case Kind.Recipes:
            {
                var recipes = stations.Recipes(station);
                ctx.Reply("Station " + Id(station) + ", " + recipes.Count + " recipe(s):");
                foreach (var line in RecipeLines(recipes, Name)) ctx.Reply(line);
                SampleLog.Info(Area, "recipes " + Id(station) + " count=" + recipes.Count);
                return;
            }
            case Kind.Toggle:
            {
                var recipe = MatchRecipe(stations.Recipes(station), request.Recipe, out string why);
                if (recipe == null)
                {
                    ctx.Reply(why);
                    return;
                }
                var result = stations.SetRecipeEnabled(station, recipe.RecipeGuid, request.On);
                ctx.Reply(ToggleReply(result, recipe.Name));
                SampleLog.Info(Area, "toggle " + Id(station) + " recipe=" + recipe.RecipeGuid + " on=" + request.On
                    + " ok=" + result.Ok + " changed=" + result.Changed + " linked=" + result.LinkedRowsChanged);
                return;
            }
            case Kind.Status:
            {
                var p = stations.Progress(station);
                ctx.Reply(StatusReply(p, p == null || p.RecipeGuid == 0 ? "" : Name(p.RecipeGuid)));
                SampleLog.Info(Area, "status " + Id(station) + " state=" + (p == null ? "none" : StationLogic.Describe(p.State)));
                return;
            }
        }
    }

    private static string Id(Entity e) => e.Index.ToString(CultureInfo.InvariantCulture) + ":" + e.Version.ToString(CultureInfo.InvariantCulture);

    private static float3 Position(ICommandContext ctx)
    {
        try
        {
            var ch = ctx.CharacterEntity;
            if (ch == Entity.Null) return default;
            var em = ctx.World.EntityManager;
            if (ch.TryRead(em, out Translation translation)) return translation.Value;
            if (ch.TryRead(em, out LocalToWorld ltw)) return ltw.Position;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Sample .stations position read threw: " + e.Message);
        }
        return default;
    }
#endif
}
