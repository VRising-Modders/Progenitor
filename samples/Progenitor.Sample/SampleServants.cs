using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Progenitor.Castles;
using Progenitor.Servants;

#if !PROGENITOR_TESTS
using Progenitor.Commands;
using Progenitor.Plugins;
using Progenitor.WorldState;
using Unity.Entities;
#endif

namespace Progenitor.Sample;

/// <summary>
/// The <c>.servants</c> verbs for <c>ctx.Servants</c> (experimental). Server only, admin only (it
/// sends other players' servants).
///
/// <list type="bullet">
/// <item><description><c>.servants list</c>: the servants the throne of the castle you stand on
/// (else your own) lists, numbered <c>#1..</c>, with state, proficiency and items.</description></item>
/// <item><description><c>.servants hunts</c>: that castle heart's hunt record (mission, zone, length
/// choice, time left, servants).</description></item>
/// <item><description><c>.servants send &lt;#n[,#n,#n]&gt; &lt;missionGuid|x,y#zone&gt; &lt;slot&gt;</c>:
/// a server send, validated with the game's checks.</description></item>
/// <item><description><c>.servants watch on|off|resend</c>: log every <c>ServantsReturned</c>;
/// <c>resend</c> also sends the unhurt servants of each return again (when their inventories are
/// empty).</description></item>
/// <item><description><c>.servants again</c>: resend the last hunt that returned to this castle.</description></item>
/// <item><description><c>.servants swap on|off</c>: register a list provider that lists, on any
/// throne you open, the servants of every castle you or your clan own (this castle first).</description></item>
/// <item><description><c>.servants status</c>: the list hook's counters.</description></item>
/// </list>
/// Parsing and reply lines are game-free so the test project compiles this file.
/// </summary>
internal static class SampleServants
{
    internal const string Area = "servants";

    internal const string UsageReply =
        "Usage: .servants list | hunts | send <#n[,#n,#n]> <missionGuid|x,y#zone> <slot> | watch on|off|resend | again | swap on|off | status";

    internal enum Kind
    {
        List = 1,
        Hunts,
        Send,
        Watch,
        Again,
        Swap,
        Status,
    }

    internal enum WatchMode
    {
        Off = 0,
        Log = 1,
        Resend = 2,
    }

    /// <summary>A parsed command.</summary>
    internal sealed class Request
    {
        public Kind Kind;
        public List<int> Rows = new();
        public int MissionGuid;
        public MissionZone Zone;
        public bool ByZone;
        public int Slot;
        public WatchMode Watch;
        public bool On;
    }

    /// <summary>Parse <c>.servants verb a b c</c>.</summary>
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
                return a.Length == 0;
            case "hunts":
                request.Kind = Kind.Hunts;
                return a.Length == 0;
            case "again":
                request.Kind = Kind.Again;
                return a.Length == 0;
            case "status":
                request.Kind = Kind.Status;
                return a.Length == 0;
            case "watch":
                request.Kind = Kind.Watch;
                switch (a.ToLowerInvariant())
                {
                    case "on": request.Watch = WatchMode.Log; return true;
                    case "off": request.Watch = WatchMode.Off; return true;
                    case "resend": request.Watch = WatchMode.Resend; return true;
                    default: return false;
                }
            case "swap":
                request.Kind = Kind.Swap;
                switch (a.ToLowerInvariant())
                {
                    case "on": request.On = true; return true;
                    case "off": request.On = false; return true;
                    default: return false;
                }
            case "send":
                request.Kind = Kind.Send;
                return TryParseRows(a, request.Rows)
                    && TryParseMission(b, out request.MissionGuid, out request.Zone, out request.ByZone)
                    && int.TryParse(c, NumberStyles.Integer, CultureInfo.InvariantCulture, out request.Slot) && request.Slot >= 0;
            default:
                return false;
        }
    }

    /// <summary><c>#1,#3</c> or <c>1,3</c>: one to three distinct row numbers from <c>.servants list</c>.</summary>
    internal static bool TryParseRows(string token, List<int> rows)
    {
        rows.Clear();
        if (token.Length == 0) return false;
        foreach (var part in token.Split(','))
        {
            var p = part.Trim().TrimStart('#');
            if (!int.TryParse(p, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) || n < 1 || rows.Contains(n)) return false;
            rows.Add(n);
        }
        return rows.Count >= 1 && rows.Count <= 3;
    }

    /// <summary>A mission GUID hash, or a zone written <c>x,y#zone</c> as <c>.servants hunts</c> prints it.</summary>
    internal static bool TryParseMission(string token, out int mission, out MissionZone zone, out bool byZone)
    {
        mission = 0;
        zone = MissionZone.None;
        byZone = false;
        if (token.Length == 0) return false;
        int hash = token.IndexOf('#');
        if (hash < 0)
            return int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out mission) && mission != 0;
        int comma = token.IndexOf(',');
        if (comma <= 0 || comma > hash) return false;
        if (!sbyte.TryParse(token.Substring(0, comma), NumberStyles.Integer, CultureInfo.InvariantCulture, out sbyte x)
            || !sbyte.TryParse(token.Substring(comma + 1, hash - comma - 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out sbyte y)
            || !byte.TryParse(token.Substring(hash + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out byte z)
            || z == 0)
            return false;
        zone = new MissionZone(x, y, z);
        byZone = true;
        return true;
    }

    /// <summary>One <c>.servants list</c> row.</summary>
    internal static string ListLine(int row, ServantInfo s) =>
        "#" + row.ToString(CultureInfo.InvariantCulture) + " " + (s.Name.Length > 0 ? s.Name : "(unnamed)") + " " + s.State
        + " prof " + s.HuntProficiency.ToString("0", CultureInfo.InvariantCulture)
        + (s.ItemStacks > 0 ? " items " + s.ItemStacks.ToString(CultureInfo.InvariantCulture) : "")
        + (s.InjuryEndsUtc is DateTime until ? " injured until " + until.ToString("HH:mm", CultureInfo.InvariantCulture) + " UTC" : "")
        + " servant=" + s.Servant;

    /// <summary>One <c>.servants hunts</c> row; <paramref name="nowUtc"/> gives the time left.</summary>
    internal static string HuntLine(ServantMission m, DateTime nowUtc)
    {
        double left = Math.Max(0, (m.EndsUtc - nowUtc).TotalSeconds);
        return (m.MissionName.Length > 0 ? m.MissionName : "mission") + " " + m.MissionGuid.ToString(CultureInfo.InvariantCulture)
            + " zone " + m.Zone + " slot " + m.DataSlot.ToString(CultureInfo.InvariantCulture)
            + " left " + FormatSeconds(left) + " of " + FormatSeconds(m.LengthSeconds)
            + " servants " + m.Servants.Count.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>The log line of a return.</summary>
    internal static string ReturnLine(ServantsReturned ev)
    {
        var sb = new StringBuilder("returned heart=").Append(ev.Heart).Append(" territory=").Append(ev.Territory)
            .Append(" mission=").Append(ev.MissionGuid).Append(" zone=").Append(ev.Zone).Append(" slot=").Append(ev.DataSlot)
            .Append(" servants=");
        for (int i = 0; i < ev.Servants.Count; i++)
        {
            var s = ev.Servants[i];
            if (i > 0) sb.Append(',');
            sb.Append(s.Name.Length > 0 ? s.Name : s.Servant.ToString()).Append(':').Append(s.Outcome);
        }
        sb.Append(" loot=");
        var loot = ev.TotalLoot;
        for (int i = 0; i < loot.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(loot[i].Amount).Append('x').Append(loot[i].Item);
        }
        if (loot.Count == 0) sb.Append("none");
        return sb.ToString();
    }

    /// <summary><c>1h05m</c>, <c>4m10s</c>, <c>12s</c>.</summary>
    internal static string FormatSeconds(double seconds)
    {
        long s = (long)Math.Round(seconds);
        if (s >= 3600) return (s / 3600).ToString(CultureInfo.InvariantCulture) + "h" + (s % 3600 / 60).ToString("00", CultureInfo.InvariantCulture) + "m";
        if (s >= 60) return (s / 60).ToString(CultureInfo.InvariantCulture) + "m" + (s % 60).ToString("00", CultureInfo.InvariantCulture) + "s";
        return s.ToString(CultureInfo.InvariantCulture) + "s";
    }

    /// <summary>
    /// The hearts the sample's provider lists: the heart of the castle the throne is in first, then
    /// every other castle of the user or the user's clan, each once.
    /// </summary>
    internal static List<CastleId> HeartsInOrder(CastleId here, IEnumerable<CastleId> owned)
    {
        var list = new List<CastleId>();
        if (!here.IsNone) list.Add(here);
        foreach (var h in owned)
            if (!h.IsNone && !list.Contains(h)) list.Add(h);
        return list;
    }

#if !PROGENITOR_TESTS
    private static WatchMode _watch;
    private static IDisposable? _returns;
    private static IDisposable? _swap;
    private static readonly Dictionary<int, ServantsReturned> LastByTerritory = new();

    internal static void Detach()
    {
        _returns?.Dispose();
        _returns = null;
        _swap?.Dispose();
        _swap = null;
        _watch = WatchMode.Off;
        LastByTerritory.Clear();
    }

    internal static void HandleCommand(ICommandContext ctx, Request request)
    {
        var plugin = Plugin.Active;
        if (plugin == null || !plugin.IsServer)
        {
            ctx.Reply("Sample plugin context or server world is not available.");
            return;
        }
        var servants = plugin.Servants;
        var castle = plugin.Castles.At(ctx.CharacterEntity);
        if (!castle.Enabled) castle = plugin.Castles.ForUser(ctx.UserEntity);
        switch (request.Kind)
        {
            case Kind.List:
            {
                if (!castle.Enabled) { ctx.Reply("No castle here or of yours."); return; }
                var list = servants.List(castle.Heart.ToEntity());
                ctx.Reply("Castle " + castle.Territory.Index + ": " + list.Count + " servant(s).");
                for (int i = 0; i < list.Count; i++) ctx.Reply(ListLine(i + 1, list[i]));
                SampleLog.Info(Area, "list territory=" + castle.Territory.Index + " count=" + list.Count);
                return;
            }
            case Kind.Hunts:
            {
                if (!castle.Enabled) { ctx.Reply("No castle here or of yours."); return; }
                var hunts = servants.Missions(castle.Heart.ToEntity());
                ctx.Reply("Castle " + castle.Territory.Index + ": " + hunts.Count + " hunt(s).");
                foreach (var m in hunts) ctx.Reply(HuntLine(m, DateTime.UtcNow));
                SampleLog.Info(Area, "hunts territory=" + castle.Territory.Index + " count=" + hunts.Count);
                return;
            }
            case Kind.Send:
            {
                if (!castle.Enabled) { ctx.Reply("No castle here or of yours."); return; }
                var list = servants.List(castle.Heart.ToEntity());
                var picked = new List<CastleId>();
                foreach (int row in request.Rows)
                {
                    if (row > list.Count) { ctx.Reply("No row #" + row + "; run .servants list."); return; }
                    picked.Add(list[row - 1].Servant);
                }
                int mission = request.ByZone ? servants.MissionOf(request.Zone) : request.MissionGuid;
                var result = servants.Send(new ServantSendRequest
                {
                    Heart = castle.Heart,
                    Servants = picked,
                    MissionGuid = mission,
                    DataSlot = request.Slot,
                    User = CastleId.From(ctx.UserEntity),
                    Epoch = WorldEpoch.Current,
                });
                ctx.Reply(result.ToString());
                SampleLog.Info(Area, "send territory=" + castle.Territory.Index + " " + result);
                return;
            }
            case Kind.Watch:
                SetWatch(plugin, request.Watch);
                ctx.Reply("Servant returns: " + _watch + ".");
                return;
            case Kind.Again:
            {
                if (!castle.Enabled) { ctx.Reply("No castle here or of yours."); return; }
                if (!LastByTerritory.TryGetValue(castle.Territory.Index, out var last))
                {
                    ctx.Reply("No hunt returned to this castle since .servants watch was turned on.");
                    return;
                }
                var result = servants.Resend(last);
                ctx.Reply(result.ToString());
                SampleLog.Info(Area, "again territory=" + castle.Territory.Index + " " + result);
                return;
            }
            case Kind.Swap:
                if (request.On)
                {
                    if (_swap == null)
                    {
                        try { _swap = servants.RegisterListProvider(new OwnedCastlesProvider(plugin)); }
                        catch (InvalidOperationException e) { ctx.Reply(e.Message); return; }
                    }
                }
                else
                {
                    _swap?.Dispose();
                    _swap = null;
                }
                ctx.Reply("Throne list swap " + (_swap != null ? "on" : "off") + ". " + servants.ListStatus);
                SampleLog.Info(Area, "swap " + (_swap != null ? "on" : "off"));
                return;
            case Kind.Status:
                ctx.Reply(servants.ListStatus.ToString() + "; returns " + _watch);
                return;
        }
    }

    private static void SetWatch(PluginContext plugin, WatchMode mode)
    {
        _watch = mode;
        if (mode == WatchMode.Off)
        {
            _returns?.Dispose();
            _returns = null;
            return;
        }
        _returns ??= plugin.Events.On<ServantsReturned>(OnReturned);
    }

    private static void OnReturned(ServantsReturned ev)
    {
        if (ev.Territory >= 0) LastByTerritory[ev.Territory] = ev;
        SampleLog.Info(Area, ReturnLine(ev));
        if (_watch != WatchMode.Resend) return;
        var plugin = Plugin.Active;
        if (plugin == null) return;
        var result = plugin.Servants.Resend(ev);
        SampleLog.Info(Area, "resend territory=" + ev.Territory + " " + result);
    }

    /// <summary>The castle the throne is in first, then every castle of the player or the player's clan.</summary>
    private sealed class OwnedCastlesProvider : IServantListProvider
    {
        private readonly PluginContext _plugin;

        internal OwnedCastlesProvider(PluginContext plugin) => _plugin = plugin;

        public IReadOnlyList<CastleId>? HeartsFor(ServantListRequest request)
        {
            var owned = new List<CastleId>();
            foreach (var c in _plugin.Castles.AllForUser(request.User.ToEntity()))
                if (c.Enabled) owned.Add(c.Heart);
            if (owned.Count <= 1) return null; // nothing to add: the game's own list
            var hearts = HeartsInOrder(request.Heart, owned);
            SampleLog.Info(Area, "provider user=" + request.User + " territory=" + request.Territory + " hearts=" + hearts.Count);
            return hearts;
        }
    }
#endif
}
