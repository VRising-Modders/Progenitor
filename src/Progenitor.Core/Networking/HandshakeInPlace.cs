using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Progenitor.Plugins;

namespace Progenitor.Networking;

/// <summary>
/// NOKICK1 (live 2026-10-17): a connected player whose handshake answer no longer matches after a server-initiated
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

    /// <summary>
    /// The in-place judgment's game-free half: the notice items, the ones not told yet (recorded as told), and the mods
    /// masked for this user (<see cref="HandshakeInPlaceMask"/>: every named mod but Progenitor itself).
    /// </summary>
    internal static HandshakeInPlaceDecision Decide(int userIndex, IReadOnlyList<string>? failedIds, IReadOnlyList<Handshake.ModRecord>? server,
        IReadOnlyList<Handshake.ModRecord>? answered, HandshakeInPlaceTold told, HandshakeInPlaceMask mask, Func<string, string?>? nameOf = null)
    {
        var d = new HandshakeInPlaceDecision();
        d.Items.AddRange(Items(failedIds, server, answered, nameOf));
        foreach (var i in d.Items)
            if (!string.Equals(i.Key, ProgenitorExact.RowId, StringComparison.OrdinalIgnoreCase)) d.Masked.Add(i.Key);
        mask.Set(userIndex, d.Masked);
        d.Fresh.AddRange(told.Fresh(userIndex, d.Items));
        return d;
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

/// <summary>NOKICK1: what one in-place judgment tells and masks (<see cref="HandshakeInPlace.Decide"/>).</summary>
internal sealed class HandshakeInPlaceDecision
{
    /// <summary>Every problem now, as notice items.</summary>
    public List<RecheckItem> Items { get; } = new();

    /// <summary>The items not told before: the notice names these (none: no notice).</summary>
    public List<RecheckItem> Fresh { get; } = new();

    /// <summary>The mods masked for this user.</summary>
    public List<string> Masked { get; } = new();
}

/// <summary>
/// NOKICK1: what each connected player was already told in place, so a soft re-ask's resend, a second re-send, a later
/// re-answer that fails the same way, or the re-ask after another Core swap says nothing new; a problem that goes and
/// comes back is told again. Kept in a plain process string (an AppDomain data slot beside the session memory), so a
/// new Core generation does not repeat the popup. Forgotten at a join and a disconnect. Game-free.
/// </summary>
internal sealed class HandshakeInPlaceTold
{
    internal const string ProcessKey = "progenitor.handshake.told.v1";
    private const int MaxUsers = 512;
    private const int MaxPerUser = 64;
    private static readonly char[] Separators = { ';', '=', ',' };

    private readonly object _gate = new();
    private readonly Func<string?> _read;
    private readonly Action<string> _write;

    internal HandshakeInPlaceTold(Func<string?>? read = null, Action<string>? write = null)
    {
        _read = read ?? ReadProcess;
        _write = write ?? WriteProcess;
    }

    private static string? ReadProcess()
    {
        try { return AppDomain.CurrentDomain.GetData(ProcessKey) as string; }
        catch { return null; }
    }

    private static void WriteProcess(string value)
    {
        try { AppDomain.CurrentDomain.SetData(ProcessKey, value); }
        catch { /* best effort: without the slot a swap may tell a problem again */ }
    }

    /// <summary>The items of <paramref name="now"/> not told to <paramref name="userIndex"/> yet; the told set becomes <paramref name="now"/>.</summary>
    internal List<RecheckItem> Fresh(int userIndex, IReadOnlyList<RecheckItem> now)
    {
        var fresh = new List<RecheckItem>();
        if (userIndex < 0 || now == null) return fresh;
        lock (_gate)
        {
            var all = Decode(_read());
            all.TryGetValue(userIndex, out var before);
            var after = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in now)
            {
                string sig = item.Signature;
                if (!after.Add(sig)) continue;
                if (before == null || !before.Contains(sig)) fresh.Add(item);
            }

            if (after.Count == 0) all.Remove(userIndex);
            else
            {
                if (all.Count >= MaxUsers && !all.ContainsKey(userIndex)) all.Clear();
                all[userIndex] = after;
            }

            _write(Encode(all));
        }

        return fresh;
    }

    /// <summary>A clean answer: nothing is outstanding for <paramref name="userIndex"/> any more.</summary>
    internal void Clear(int userIndex)
    {
        lock (_gate)
        {
            var all = Decode(_read());
            if (all.Remove(userIndex)) _write(Encode(all));
        }
    }

    /// <summary>The user joined or left.</summary>
    internal void Forget(int userIndex) => Clear(userIndex);

    /// <summary>How many problems <paramref name="userIndex"/> has been told about and not fixed (bridge, tests).</summary>
    internal int Outstanding(int userIndex)
    {
        lock (_gate) return Decode(_read()).TryGetValue(userIndex, out var set) ? set.Count : 0;
    }

    internal void Reset()
    {
        lock (_gate) _write("");
    }

    /// <summary><c>index=sig,sig;index=sig</c>. A signature holding a separator is not kept (it is then told again).</summary>
    internal static string Encode(IReadOnlyDictionary<int, HashSet<string>> all)
    {
        var sb = new StringBuilder();
        foreach (var pair in all)
        {
            var sigs = new List<string>();
            foreach (var sig in pair.Value)
                if (sig.Length > 0 && sig.IndexOfAny(Separators) < 0 && sigs.Count < MaxPerUser) sigs.Add(sig);
            if (sigs.Count == 0) continue;
            if (sb.Length > 0) sb.Append(';');
            sb.Append(pair.Key.ToString(CultureInfo.InvariantCulture)).Append('=').Append(string.Join(",", sigs));
        }

        return sb.ToString();
    }

    /// <summary>Malformed parts are skipped.</summary>
    internal static Dictionary<int, HashSet<string>> Decode(string? text)
    {
        var all = new Dictionary<int, HashSet<string>>();
        if (string.IsNullOrEmpty(text)) return all;
        foreach (string part in text!.Split(';'))
        {
            if (all.Count >= MaxUsers) break;
            int eq = part.IndexOf('=');
            if (eq <= 0) continue;
            if (!int.TryParse(part.Substring(0, eq), NumberStyles.Integer, CultureInfo.InvariantCulture, out int index) || index < 0) continue;
            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (string sig in part.Substring(eq + 1).Split(','))
                if (sig.Length > 0 && set.Count < MaxPerUser) set.Add(sig);
            if (set.Count > 0) all[index] = set;
        }

        return all;
    }
}

/// <summary>
/// NOKICK1: the mods a player kept in place lacks or runs at a version this server does not accept, per user. A
/// missing mod is already absent from their feature table; a version-mismatched one is listed there, so this masks it:
/// the server's send gates (<c>ctx.Messages</c>, exchanges) answer <c>PeerLacksMod</c> and <c>ctx.Peers.Of(user)</c>
/// reports the mod as absent, exactly as for a missing one. Set by each in-place judgment, cleared by a clean answer, a
/// join and a disconnect (a new Core generation re-derives it at its re-ask). Game-free.
/// </summary>
internal sealed class HandshakeInPlaceMask
{
    /// <summary>The server's mask (empty on a client).</summary>
    internal static readonly HandshakeInPlaceMask Shared = new();

    private readonly object _gate = new();
    private readonly Dictionary<int, HashSet<string>> _masked = new();

    /// <summary>The user's masked mods become <paramref name="guids"/> (none clears it).</summary>
    internal void Set(int userIndex, IEnumerable<string>? guids)
    {
        if (userIndex < 0) return;
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (guids != null)
            foreach (var g in guids)
                if (!string.IsNullOrEmpty(g)) set.Add(g);
        lock (_gate)
        {
            if (set.Count == 0) _masked.Remove(userIndex);
            else
            {
                if (_masked.Count >= 512 && !_masked.ContainsKey(userIndex)) _masked.Clear();
                _masked[userIndex] = set;
            }
        }
    }

    internal void Clear(int userIndex)
    {
        lock (_gate) _masked.Remove(userIndex);
    }

    internal bool IsMasked(int userIndex, string? guid)
    {
        if (string.IsNullOrEmpty(guid)) return false;
        lock (_gate) return _masked.TryGetValue(userIndex, out var set) && set.Contains(guid!);
    }

    internal void Reset()
    {
        lock (_gate) _masked.Clear();
    }

    /// <summary>A send gate's "lists the mod" for a user: their table lists it and it is not masked for them.</summary>
    internal static bool ListsMod(FeatureTable? table, int userIndex, string modGuid) =>
        table != null && table.Mods.ContainsKey(modGuid) && !Shared.IsMasked(userIndex, modGuid);
}
