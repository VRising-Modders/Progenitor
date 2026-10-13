using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

#if !PROGENITOR_TESTS
using Progenitor.Commands;
using Progenitor.Players;
#endif

namespace Progenitor.Sample;

/// <summary>
/// <c>.players</c>, <c>.players name &lt;text&gt;</c> and <c>.players account &lt;platformId&gt;</c> —
/// <c>ctx.Players</c>, the server player directory. Online enumeration, name lookup (ambiguous
/// names stay ambiguous) and account lookup. Session entity text is printed only beside
/// <c>IsFresh</c>, because a stale <c>index:version</c> must not be treated as identity.
///
/// <para>Read-only. Admin-gated because the reply includes platform ids. Parsing and every reply
/// line are game-free. Source-checked and game-free tested; not live-verified.</para>
/// </summary>
internal static class SamplePlayerDirectory
{
    internal const string Area = "players";

    internal const string UsageReply = "Usage: .players | .players name <displayName> | .players account <platformId>";

    /// <summary>How many online rows one reply lists before it says how many were left out.</summary>
    internal const int MaxListed = 8;

    internal enum Kind
    {
        Online = 1,
        Name = 2,
        Account = 3,
    }

    /// <summary>One directory row reduced to the fields the reply prints. No entity handles.</summary>
    internal readonly struct Row
    {
        public Row(string name, bool online, ulong platformId, bool fresh, string session)
        {
            Name = name ?? "";
            Online = online;
            PlatformId = platformId;
            Fresh = fresh;
            Session = session ?? "";
        }

        public string Name { get; }
        public bool Online { get; }
        public ulong PlatformId { get; }
        public bool Fresh { get; }
        public string Session { get; }
    }

    internal static bool TryParse(string? verb, string? rest, out Kind kind, out string name, out ulong platformId)
    {
        kind = Kind.Online;
        name = "";
        platformId = 0;
        string token = (verb ?? "").Trim().ToLowerInvariant();
        string tail = (rest ?? "").Trim();
        switch (token)
        {
            case "":
                return tail.Length == 0;
            case "name":
                kind = Kind.Name;
                name = tail;
                return name.Length > 0;
            case "account":
                kind = Kind.Account;
                return ulong.TryParse(tail, NumberStyles.Integer, CultureInfo.InvariantCulture, out platformId)
                    && platformId != 0;
            default:
                return false;
        }
    }

    internal static string OnlineReply(int revision, IReadOnlyList<Row> online)
    {
        var text = new StringBuilder();
        text.Append(online.Count.ToString(CultureInfo.InvariantCulture));
        text.Append(" online (directory revision ");
        text.Append(revision.ToString(CultureInfo.InvariantCulture));
        text.Append(").");
        int shown = Math.Min(online.Count, MaxListed);
        for (int i = 0; i < shown; i++)
        {
            text.Append(' ');
            text.Append(FormatRow(online[i]));
        }

        if (online.Count > shown)
        {
            text.Append(" +");
            text.Append((online.Count - shown).ToString(CultureInfo.InvariantCulture));
            text.Append(" more.");
        }

        return text.ToString();
    }

    internal static string NameReply(string query, string status, Row? match, IReadOnlyList<Row> candidates)
    {
        if (status == "Exact" && match is Row row)
            return "Name '" + query + "' matched one player. " + FormatRow(row);
        if (status == "Ambiguous")
        {
            var text = new StringBuilder();
            text.Append("Name '");
            text.Append(query);
            text.Append("' is ambiguous (");
            text.Append(candidates.Count.ToString(CultureInfo.InvariantCulture));
            text.Append("). Not picking one.");
            for (int i = 0; i < candidates.Count && i < MaxListed; i++)
            {
                text.Append(' ');
                text.Append(FormatRow(candidates[i]));
            }

            return text.ToString();
        }

        return "No player named '" + query + "'.";
    }

    internal static string AccountReply(ulong platformId, bool found, Row row) =>
        found
            ? "Account " + platformId.ToString(CultureInfo.InvariantCulture) + " is " + FormatRow(row)
            : "No player with platform id " + platformId.ToString(CultureInfo.InvariantCulture) + ".";

    internal static string FormatRow(Row row)
    {
        string name = row.Name.Length == 0 ? "(no name)" : row.Name;
        return name
            + (row.Online ? " online" : " offline")
            + " platform=" + row.PlatformId.ToString(CultureInfo.InvariantCulture)
            + " fresh=" + row.Fresh
            + " " + row.Session;
    }

#if !PROGENITOR_TESTS
    internal static void HandleCommand(ICommandContext ctx, Kind kind, string name, ulong platformId)
    {
        var plugin = Plugin.Active;
        if (plugin == null)
        {
            ctx.Reply("Sample plugin context is not available.");
            return;
        }

        var players = plugin.Players;
        switch (kind)
        {
            case Kind.Name:
            {
                PlayerNameLookup lookup = players.FindByName(name);
                Row? match = lookup.Player == null ? null : ToRow(players, lookup.Player);
                var candidates = new List<Row>(lookup.Candidates.Count);
                for (int i = 0; i < lookup.Candidates.Count; i++)
                    candidates.Add(ToRow(players, lookup.Candidates[i]));
                ctx.Reply(NameReply(name, lookup.Status.ToString(), match, candidates));
                SampleLog.Info(Area, "name " + name + " " + lookup.Status);
                return;
            }
            case Kind.Account:
            {
                bool found = players.TryGetByAccount(platformId, out PlayerInfo info);
                ctx.Reply(AccountReply(platformId, found, found ? ToRow(players, info) : default));
                SampleLog.Info(Area, "account " + platformId + " found=" + found);
                return;
            }
            default:
            {
                IReadOnlyList<PlayerInfo> online = players.Online();
                var rows = new List<Row>(online.Count);
                for (int i = 0; i < online.Count; i++)
                    rows.Add(ToRow(players, online[i]));
                ctx.Reply(OnlineReply(players.Revision, rows));
                SampleLog.Info(Area, "online " + online.Count + " revision=" + players.Revision);
                return;
            }
        }
    }

    private static Row ToRow(PluginPlayers players, PlayerInfo info) =>
        new(info.DisplayName, info.IsOnline, info.PlatformId, players.IsFresh(info), info.Session.ToString());
#endif
}
