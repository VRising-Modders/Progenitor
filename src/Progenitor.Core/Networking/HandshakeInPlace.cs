using System;
using System.Collections.Generic;
using Progenitor.Plugins;

namespace Progenitor.Networking;

/// <summary>
/// NOKICK1 (live 2026-10-17): a player admitted this session whose handshake answer no longer matches after a server
/// change (a Core swap, a server plugin hot-loaded) stays connected (<see cref="HandshakeReask.MismatchOutcome"/>). This
/// turns the answer's failing rows into the lines of the in-place notice: the same notice, in the game's look, that the
/// client mod policy sends mid-session (<see cref="ClientPolicyNoticeWire"/>, action <c>warn</c>), so a mod the player
/// lacks reads <c>Now required here; install it from Install Mods before your next join</c> with
/// <b>Open Install Mods</b> when this client has no copy. Game-free.
/// </summary>
internal static class HandshakeInPlace
{
    /// <summary>The name a notice line shows for Progenitor's own row (a Core capability or Exact version problem).</summary>
    internal const string ProgenitorName = "Progenitor";

    /// <summary>
    /// One notice item per failing row, in the order the rows failed (each id once). A row this server asked for and the
    /// answer lacks is <c>missing</c> (told as added mid-session: it applies from the next join); one the answer has at
    /// another version is <c>older</c>, <c>newer</c> or <c>mismatch</c> with the rule and the version they have.
    /// Progenitor's own row is a version line. An id this server does not list (a row the client requires and this
    /// server lacks) is the client's to handle and is left out.
    /// </summary>
    internal static List<RecheckItem> Items(IReadOnlyList<string>? failedIds, IReadOnlyList<Handshake.ModRecord>? server,
        IReadOnlyList<Handshake.ModRecord>? answered, Func<string, string?>? nameOf = null)
    {
        var items = new List<RecheckItem>();
        if (failedIds == null) return items;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string id in failedIds)
        {
            if (string.IsNullOrEmpty(id) || !seen.Add(id)) continue;
            if (string.Equals(id, ProgenitorExact.RowId, StringComparison.OrdinalIgnoreCase))
            {
                items.Add(new RecheckItem { Key = ProgenitorExact.RowId, Name = ProgenitorName, Kind = ClientModProblem.KindMismatch, Added = true });
                continue;
            }

            var row = Find(server, id);
            if (row == null) continue;
            string name = NameOf(id, nameOf);
            var had = Find(answered, id);
            if (had == null)
            {
                items.Add(new RecheckItem { Key = row.Value.Guid, Name = name, Kind = ClientModProblem.KindMissing, Added = true, Rule = RuleWords(row.Value) });
                continue;
            }

            string version = had.Value.Version ?? "";
            int? order = Handshake.Compare.CompareOrder(version, row.Value.Version);
            string kind = version.Length == 0 ? ClientModProblem.KindOlder
                : order switch { < 0 => ClientModProblem.KindOlder, > 0 => ClientModProblem.KindNewer, _ => ClientModProblem.KindMismatch };
            items.Add(new RecheckItem { Key = row.Value.Guid, Name = name, Kind = kind, Added = true, Rule = RuleWords(row.Value), Had = version });
        }

        return items;
    }

    /// <summary>The row's version rule as words: the version (Exact), <c>1.2.x</c> (Minor), or empty (any version).</summary>
    internal static string RuleWords(Handshake.ModRecord row)
    {
        string version = row.Version ?? "";
        if (version.Length == 0) return "";
        switch (row.VersionRule)
        {
            case NetworkVersionRule.None:
                return "";
            case NetworkVersionRule.Minor:
                var parts = version.TrimStart('v', 'V').Split('.');
                return parts.Length >= 2 ? parts[0] + "." + parts[1] + ".x" : version;
            default:
                return version;
        }
    }

    private static string NameOf(string id, Func<string, string?>? nameOf)
    {
        try
        {
            string? n = nameOf?.Invoke(id);
            if (!string.IsNullOrWhiteSpace(n)) return n!.Trim();
        }
        catch { /* the id is a fine name */ }
        return id;
    }

    private static Handshake.ModRecord? Find(IReadOnlyList<Handshake.ModRecord>? rows, string id)
    {
        if (rows == null) return null;
        foreach (var r in rows)
            if (string.Equals(r.Guid, id, StringComparison.OrdinalIgnoreCase)) return r;
        return null;
    }
}

/// <summary>
/// NOKICK1: what each connected player was already told in place, so a soft re-ask's resend, a second re-send or a later
/// re-answer that fails the same way says nothing new; a problem that goes and comes back is told again. Forgotten at a
/// join and a disconnect (a join is judged by the join rules). Game-free.
/// </summary>
internal sealed class HandshakeInPlaceTold
{
    private readonly object _gate = new();
    private readonly Dictionary<int, HashSet<string>> _told = new();

    /// <summary>The items of <paramref name="now"/> not told to <paramref name="userIndex"/> yet; the told set becomes <paramref name="now"/>.</summary>
    internal List<RecheckItem> Fresh(int userIndex, IReadOnlyList<RecheckItem> now)
    {
        var fresh = new List<RecheckItem>();
        if (userIndex < 0 || now == null) return fresh;
        lock (_gate)
        {
            _told.TryGetValue(userIndex, out var before);
            var after = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in now)
            {
                if (!after.Add(item.Signature)) continue;
                if (before == null || !before.Contains(item.Signature)) fresh.Add(item);
            }

            if (after.Count == 0) _told.Remove(userIndex);
            else
            {
                if (_told.Count >= 512 && !_told.ContainsKey(userIndex)) _told.Clear();
                _told[userIndex] = after;
            }
        }

        return fresh;
    }

    /// <summary>A clean answer: nothing is outstanding for <paramref name="userIndex"/> any more.</summary>
    internal void Clear(int userIndex)
    {
        lock (_gate) _told.Remove(userIndex);
    }

    /// <summary>The user joined or left.</summary>
    internal void Forget(int userIndex) => Clear(userIndex);

    /// <summary>How many problems <paramref name="userIndex"/> has been told about and not fixed (bridge, tests).</summary>
    internal int Outstanding(int userIndex)
    {
        lock (_gate) return _told.TryGetValue(userIndex, out var set) ? set.Count : 0;
    }

    internal void Reset()
    {
        lock (_gate) _told.Clear();
    }
}
