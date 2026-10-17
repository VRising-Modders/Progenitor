using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using Progenitor.Castles;
using Progenitor.Servants;

namespace Progenitor.Dev.Ops;

/// <summary>
/// Game-free half of the server <c>servants</c> op: argument parsing and the servant name lookup.
/// The live op (<c>ServantOps</c>) reads and writes through <c>ServantBridge</c>, so this file
/// compiles in the test project without interop.
/// </summary>
internal static class ServantArgs
{
    internal enum Action
    {
        List,
        Zones,
        Send,
        Finish,
        Timers,
        Empty,
        Chance,
    }

    /// <summary>The actions the op accepts.</summary>
    internal const string Actions = "list, zones, send, finish, timers, empty, chance";

    /// <summary>One parsed request.</summary>
    internal sealed record Request(
        Action Action,
        CastleId? Heart,
        int? Territory,
        IReadOnlyList<string>? Servants,
        int Mission,
        MissionZone? Zone,
        int Slot,
        bool? TimersOff,
        bool ViaTimers,
        bool All,
        CastleId User,
        bool CheckDiscovered,
        CastleId To = default,
        bool Discard = false);

    /// <summary>
    /// Parse the op's args. <c>action</c> (default <c>list</c>). Every action except
    /// <c>timers</c> names the castle by <c>territory</c> (index) or <c>heart</c> (<c>index:version</c>),
    /// exactly one. <c>zones</c>: <c>all</c> (bool, also undiscovered zones), <c>user</c>.
    /// <c>send</c>: <c>servants</c> (array or comma list of names, <c>#n</c> list rows or
    /// <c>index:version</c>; omitted: the free servants), <c>mission</c> (GUID hash) or <c>zone</c>
    /// (<c>x,y#zone</c>), at most one (neither: a zone is picked), <c>slot</c> (default 0),
    /// <c>user</c> (whose discovery is checked), <c>checkDiscovered</c> (default true).
    /// <c>chance</c>: the same arguments as <c>send</c>; reports each servant's success chance and
    /// whether the send would be accepted, writes nothing.
    /// <c>finish</c>: <c>mission</c> (optional), <c>method</c> <c>timers</c> (default) or <c>row</c>.
    /// <c>timers</c>: <c>off</c> (bool; omitted reads only). <c>empty</c>: <c>servants</c> (as for
    /// <c>send</c>; omitted: every servant at home), <c>to</c> (a container's <c>index:version</c>;
    /// omitted: the castle's ordinary containers), <c>discard</c> (test only: remove what did not fit).
    /// </summary>
    internal static Request Parse(JsonElement args)
    {
        if (args.ValueKind == JsonValueKind.Undefined || args.ValueKind == JsonValueKind.Null)
            throw new ArgumentException("args.territory or args.heart is required");
        if (args.ValueKind != JsonValueKind.Object) throw new ArgumentException("args must be an object");
        string name = (Str(args, "action") ?? "list").Trim().ToLowerInvariant();
        var action = name switch
        {
            "list" => Action.List,
            "zones" => Action.Zones,
            "send" => Action.Send,
            "finish" => Action.Finish,
            "timers" => Action.Timers,
            "empty" => Action.Empty,
            "chance" => Action.Chance,
            _ => throw new ArgumentException("unknown action '" + name + "' (" + Actions + ")"),
        };

        if (action == Action.Timers)
            return new Request(action, null, null, null, 0, null, 0, Bool(args, "off"), true, false, CastleId.None, true);

        int? territory = Int(args, "territory");
        string? heartText = Str(args, "heart");
        if (territory != null && heartText != null) throw new ArgumentException("name the castle by args.territory or args.heart, not both");
        if (territory == null && heartText == null) throw new ArgumentException("args.territory (castle territory index) or args.heart (index:version) is required");
        if (territory != null && territory.Value < 0) throw new ArgumentException("args.territory must be >= 0");
        CastleId? heart = heartText == null ? null : (ParseId(heartText) ?? throw new ArgumentException("args.heart must be index:version"));

        var user = CastleId.None;
        string? userText = Str(args, "user");
        if (userText != null) user = ParseId(userText) ?? throw new ArgumentException("args.user must be the user entity's index:version");

        int mission = MissionArg(args);
        MissionZone? zone = null;
        string? zoneText = Str(args, "zone");
        if (zoneText != null) zone = ParseZone(zoneText) ?? throw new ArgumentException("args.zone must be x,y#zone (as list and zones print it)");

        switch (action)
        {
            case Action.Send:
            case Action.Chance:
            {
                if (mission != 0 && zone != null) throw new ArgumentException("name args.mission or args.zone, not both");
                int slot = Int(args, "slot") ?? 0;
                if (slot < 0) throw new ArgumentException("args.slot must be >= 0");
                var servants = ServantList(args);
                bool check = Bool(args, "checkDiscovered") ?? true;
                return new Request(action, heart, territory, servants, mission, zone, slot, null, true, false, user, check);
            }
            case Action.Finish:
            {
                if (zone != null) throw new ArgumentException("finish takes args.mission (GUID hash), not a zone");
                string method = (Str(args, "method") ?? "timers").Trim().ToLowerInvariant();
                bool viaTimers = method switch
                {
                    "timers" => true,
                    "row" => false,
                    _ => throw new ArgumentException("args.method must be 'timers' or 'row'"),
                };
                return new Request(action, heart, territory, null, mission, null, 0, null, viaTimers, false, user, true);
            }
            case Action.Empty:
            {
                if (mission != 0 || zone != null) throw new ArgumentException("empty takes no mission or zone");
                var servants = ServantList(args, hunt: false);
                var to = CastleId.None;
                string? toText = Str(args, "to");
                if (toText != null) to = ParseId(toText) ?? throw new ArgumentException("args.to must be the container's index:version");
                bool discard = Bool(args, "discard") ?? false;
                return new Request(action, heart, territory, servants, 0, null, 0, null, true, false, user, true, to, discard);
            }
            case Action.Zones:
                return new Request(action, heart, territory, null, 0, null, 0, null, true, Bool(args, "all") ?? false, user, true);
            default:
                return new Request(action, heart, territory, null, 0, null, 0, null, true, false, user, true);
        }
    }

    /// <summary><c>index:version</c>, or null.</summary>
    internal static CastleId? ParseId(string text)
    {
        var parts = text.Trim().Split(':');
        if (parts.Length != 2
            || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int i)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int v)
            || (i == 0 && v == 0))
            return null;
        return new CastleId(i, v);
    }

    /// <summary><c>x,y#zone</c> (the form <see cref="MissionZone.ToString"/> prints), or null.</summary>
    internal static MissionZone? ParseZone(string text)
    {
        var t = text.Trim();
        int hash = t.IndexOf('#');
        if (hash <= 0) return null;
        var xy = t.Substring(0, hash).Split(',');
        if (xy.Length != 2
            || !sbyte.TryParse(xy[0].Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out sbyte x)
            || !sbyte.TryParse(xy[1].Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out sbyte y)
            || !byte.TryParse(t.Substring(hash + 1).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out byte z)
            || z == 0)
            return null;
        return new MissionZone(x, y, z);
    }

    /// <summary>
    /// The servants named, against the castle's list: a name (case-insensitive, must be unique),
    /// <c>#n</c> (row n of <c>list</c>, from 1) or <c>index:version</c> (passed through; the send
    /// checks it). Null with <paramref name="error"/> naming the token that did not resolve.
    /// </summary>
    internal static List<CastleId>? Resolve(IReadOnlyList<ServantInfo> list, IReadOnlyList<string> tokens, out string error)
    {
        error = "";
        var ids = new List<CastleId>();
        foreach (var raw in tokens)
        {
            var t = raw.Trim();
            if (t.Length == 0) continue;
            CastleId id;
            if (t[0] == '#')
            {
                if (!int.TryParse(t.Substring(1), NumberStyles.None, CultureInfo.InvariantCulture, out int row) || row < 1 || row > list.Count)
                {
                    error = "servant '" + t + "': the castle lists " + list.Count + " servant(s) (#1..#" + list.Count + ")";
                    return null;
                }
                id = list[row - 1].Servant;
                if (id.IsNone)
                {
                    error = "servant '" + t + "' (" + list[row - 1].Name + ") is dead (its coffin holds no servant)";
                    return null;
                }
            }
            else if (ParseId(t) is CastleId parsed)
            {
                id = parsed;
            }
            else
            {
                int found = -1;
                for (int i = 0; i < list.Count; i++)
                {
                    if (!string.Equals(list[i].Name, t, StringComparison.OrdinalIgnoreCase)) continue;
                    if (found >= 0)
                    {
                        error = "servant name '" + t + "' is not unique in this castle; use #n or index:version";
                        return null;
                    }
                    found = i;
                }
                if (found < 0)
                {
                    var names = new List<string>();
                    foreach (var s in list) names.Add(s.Name);
                    error = "no servant named '" + t + "' in this castle (" + (names.Count == 0 ? "no servants" : string.Join(", ", names)) + ")";
                    return null;
                }
                id = list[found].Servant;
                if (id.IsNone)
                {
                    error = "servant '" + t + "' is dead (its coffin holds no servant)";
                    return null;
                }
            }
            if (ids.Contains(id))
            {
                error = "servant '" + t + "' is named twice";
                return null;
            }
            ids.Add(id);
        }
        if (ids.Count == 0) error = "args.servants names no servant";
        return ids.Count == 0 ? null : ids;
    }

    /// <summary>The castle's free servants (state <see cref="ServantState.Free"/>), in list order, at most <paramref name="max"/>.</summary>
    internal static List<CastleId> FreeServants(IReadOnlyList<ServantInfo> list, int max)
    {
        var ids = new List<CastleId>();
        foreach (var s in list)
        {
            if (ids.Count >= max) break;
            if (s.State == ServantState.Free && !s.Servant.IsNone) ids.Add(s.Servant);
        }
        return ids;
    }

    /// <summary>Most servants one <c>empty</c> names (the throne lists at most 36).</summary>
    internal const int MaxNamed = 36;

    private static IReadOnlyList<string>? ServantList(JsonElement args, bool hunt = true)
    {
        if (!args.TryGetProperty("servants", out var v) || v.ValueKind == JsonValueKind.Null) return null;
        var list = new List<string>();
        if (v.ValueKind == JsonValueKind.String)
        {
            foreach (var part in (v.GetString() ?? "").Split(','))
                if (part.Trim().Length > 0) list.Add(part.Trim());
        }
        else if (v.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in v.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.String) throw new ArgumentException("args.servants entries must be strings (name, #n or index:version)");
                var s = (e.GetString() ?? "").Trim();
                if (s.Length > 0) list.Add(s);
            }
        }
        else throw new ArgumentException("args.servants must be an array of strings or a comma list");
        if (list.Count == 0) throw new ArgumentException("args.servants is empty; omit it to use the default servants");
        if (list.Count > MaxNamed) throw new ArgumentException("args.servants names " + list.Count + " servants; at most " + MaxNamed);
        if (hunt && list.Count > ServantLogicLimits.MaxServantsPerHunt) throw new ArgumentException("args.servants names " + list.Count + " servants; a hunt takes at most 3");
        return list;
    }

    private static int MissionArg(JsonElement args)
    {
        if (!args.TryGetProperty("mission", out var v) || v.ValueKind == JsonValueKind.Null) return 0;
        int m;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n)) m = n;
        else if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int s)) m = s;
        else throw new ArgumentException("args.mission must be the mission's PrefabGUID hash (an integer)");
        if (m == 0) throw new ArgumentException("args.mission must be nonzero; omit it to pick a zone");
        return m;
    }

    private static string? Str(JsonElement a, string k)
    {
        if (!a.TryGetProperty(k, out var v) || v.ValueKind == JsonValueKind.Null) return null;
        if (v.ValueKind != JsonValueKind.String) throw new ArgumentException("args." + k + " must be a string");
        return v.GetString();
    }

    private static int? Int(JsonElement a, string k)
    {
        if (!a.TryGetProperty(k, out var v) || v.ValueKind == JsonValueKind.Null) return null;
        if (v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out int n)) throw new ArgumentException("args." + k + " must be an integer");
        return n;
    }

    private static bool? Bool(JsonElement a, string k)
    {
        if (!a.TryGetProperty(k, out var v) || v.ValueKind == JsonValueKind.Null) return null;
        if (v.ValueKind == JsonValueKind.True) return true;
        if (v.ValueKind == JsonValueKind.False) return false;
        throw new ArgumentException("args." + k + " must be true or false");
    }

    /// <summary>The game's three servant slots per hunt (kept here so this file needs no Core internals).</summary>
    private static class ServantLogicLimits
    {
        internal const int MaxServantsPerHunt = 3;
    }
}
