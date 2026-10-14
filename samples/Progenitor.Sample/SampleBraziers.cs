using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Progenitor.Stations;

#if !PROGENITOR_TESTS
using Progenitor.Commands;
using Progenitor.Ecs;
using Progenitor.WorldState;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
#endif

namespace Progenitor.Sample;

/// <summary>
/// <c>.brazier list [radius] | status [id] | on [id] | off [id]</c> over <c>ctx.Stations</c>' brazier
/// members (<c>docs/api.md</c> Stations): find burning structures, read their switch, burn state and
/// fuel, and set the switch the way the vanilla window's toggle does. With no id the nearest brazier
/// within <see cref="NearestRadius"/> meters is used; ids are the <c>index:version@epoch</c> form
/// <c>.brazier list</c> prints. Server only.
///
/// <para>Parsing and every reply line are game-free so the test project compiles this file.
/// Source-checked and game-free tested; not live-verified.</para>
/// </summary>
internal static class SampleBraziers
{
    internal const string Area = "brazier";

    /// <summary>Search radius without one, and for the implicit nearest brazier.</summary>
    internal const float NearestRadius = 20f;

    /// <summary>Largest radius <c>.brazier list</c> accepts.</summary>
    internal const float MaxRadius = 200f;

    /// <summary>Rows printed before a <c>+N more</c> tail.</summary>
    internal const int MaxRows = 6;

    internal const string UsageReply = "Usage: .brazier list [radius] | status [id] | on [id] | off [id]";

    internal const string NoBrazierReply = "No brazier within 20 m. Stand next to one or pass an id from .brazier list.";

    internal enum Kind
    {
        List = 1,
        Status = 2,
        On = 3,
        Off = 4,
    }

    /// <summary>A parsed command.</summary>
    internal sealed class Request
    {
        public Kind Kind;
        public float Radius = NearestRadius;
        public int Index = -1;
        public int Version;
        public int Epoch;
        public bool HasId => Index >= 0;
    }

    /// <summary>Parse <c>.brazier verb a</c>.</summary>
    internal static bool TryParse(string? verb, string? a, out Request request)
    {
        request = new Request();
        a = (a ?? "").Trim();
        switch ((verb ?? "").Trim().ToLowerInvariant())
        {
            case "":
            case "list":
                request.Kind = Kind.List;
                if (a.Length == 0) return true;
                return float.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out request.Radius)
                    && request.Radius > 0 && request.Radius <= MaxRadius;
            case "status":
                request.Kind = Kind.Status;
                return OptionalId(a, request);
            case "on":
                request.Kind = Kind.On;
                return OptionalId(a, request);
            case "off":
                request.Kind = Kind.Off;
                return OptionalId(a, request);
            default:
                return false;
        }
    }

    private static bool OptionalId(string token, Request request)
    {
        if (token.Length == 0) return true;
        if (!SampleStations.TryParseId(token, out int index, out int version, out int epoch)) return false;
        request.Index = index;
        request.Version = version;
        request.Epoch = epoch;
        return true;
    }

    internal static string ListReply(IReadOnlyList<(string Id, string Name, float Distance, BrazierState State)> rows, float radius)
    {
        string r = radius.ToString("0.#", CultureInfo.InvariantCulture);
        if (rows.Count == 0) return "No braziers within " + r + " m.";
        var sb = new StringBuilder();
        sb.Append(rows.Count).Append(" brazier(s) within ").Append(r).Append(" m:");
        for (int i = 0; i < rows.Count && i < MaxRows; i++)
        {
            var row = rows[i];
            sb.Append(' ').Append(row.Id).Append(' ').Append(Short(row.Name)).Append(" (")
              .Append(row.Distance.ToString("0.#", CultureInfo.InvariantCulture)).Append(" m, ").Append(row.State).Append(");");
        }
        if (rows.Count > MaxRows) sb.Append(" +").Append(rows.Count - MaxRows).Append(" more");
        return sb.ToString();
    }

    internal static string StatusReply(string id, BrazierState? state) =>
        state == null ? "Entity " + id + " is not a brazier." : "Brazier " + id + ": " + state + ".";

    internal static string SetReply(string id, BrazierToggleResult result) =>
        !result.Ok
            ? "Brazier " + id + ": refused, " + result.Reason + "."
            : "Brazier " + id + " is now " + (result.Enabled ? "on" : "off") + (result.Changed ? "" : " (no change)")
              + (result.Enabled ? ". It burns during the day while fuel is inside." : ".");

    /// <summary>Drop the common prefab prefix so a line fits chat.</summary>
    internal static string Short(string name)
    {
        const string prefix = "TM_";
        return name.StartsWith(prefix, StringComparison.Ordinal) && name.Length > prefix.Length ? name.Substring(prefix.Length) : name;
    }

#if !PROGENITOR_TESTS
    internal static void HandleCommand(ICommandContext ctx, Request request)
    {
        var plugin = Plugin.Active;
        if (plugin == null || plugin.World == null)
        {
            ctx.Reply("Sample plugin context or server world is not available.");
            return;
        }
        var stations = plugin.Stations;
        int epoch = WorldEpoch.Current;
        if (request.HasId && request.Epoch != epoch)
        {
            ctx.Reply(SampleStations.StaleIdReply);
            return;
        }

        float3 here = default;
        if ((request.Kind == Kind.List || !request.HasId) && !TryPosition(ctx, out here))
        {
            ctx.Reply(SampleStations.NoCharacterReply);
            return;
        }

        if (request.Kind == Kind.List)
        {
            var found = stations.BraziersNear(here, request.Radius);
            var rows = new List<(string, string, float, BrazierState)>();
            foreach (var b in found) rows.Add((Id(b.Brazier), b.Name, b.Distance, b.State));
            ctx.Reply(ListReply(rows, request.Radius));
            SampleLog.Info(Area, "list radius=" + request.Radius.ToString(CultureInfo.InvariantCulture) + " found=" + found.Count);
            return;
        }

        Entity brazier;
        if (request.HasId)
        {
            brazier = new Entity { Index = request.Index, Version = request.Version };
        }
        else
        {
            var near = stations.BraziersNear(here, NearestRadius);
            if (near.Count == 0)
            {
                ctx.Reply(NoBrazierReply);
                return;
            }
            brazier = near[0].Brazier;
        }

        string id = Id(brazier);
        if (request.Kind == Kind.Status)
        {
            var state = stations.Brazier(brazier);
            ctx.Reply(StatusReply(id, state));
            SampleLog.Info(Area, "status " + id + " " + (state == null ? "none" : state.ToString()));
            return;
        }

        var result = stations.SetBrazierEnabled(brazier, request.Kind == Kind.On);
        ctx.Reply(SetReply(id, result));
        SampleLog.Info(Area, "set " + id + " on=" + (request.Kind == Kind.On) + " ok=" + result.Ok + " changed=" + result.Changed
            + " enabled=" + result.Enabled + " reason=" + result.Reason);
    }

    private static string Id(Entity e) => SampleStations.FormatId(e.Index, e.Version, WorldEpoch.Current);

    private static bool TryPosition(ICommandContext ctx, out float3 position)
    {
        position = default;
        try
        {
            var ch = ctx.CharacterEntity;
            if (ch == Entity.Null) return false;
            var em = ctx.World.EntityManager;
            if (!em.Exists(ch)) return false;
            if (ch.TryRead(em, out Translation translation)) { position = translation.Value; return true; }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Sample .brazier position read threw: " + e.Message);
        }
        return false;
    }
#endif
}
