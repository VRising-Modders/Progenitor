using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Progenitor.Networking;

/// <summary>
/// JOINADMIN1: one mod row of one refused join: the mod, the version the server required, the version the
/// player had (empty when missing), the kind, when, and a join number that groups the rows of one refusal.
/// <b>No player identity:</b> no name, SteamID, address or user index is ever stored; the join number is a
/// counter that only says which rows belong together.
/// </summary>
internal sealed class RefusedJoinRow
{
    public RefusedJoinRow(string mod, string required, string had, string kind, DateTime atUtc, long join, string? name = null)
    {
        Mod = Cap(mod, JoinSectionLimits.MaxIdChars);
        Name = Cap(name, JoinSectionLimits.MaxNameChars);
        Required = Cap(required, JoinSectionLimits.MaxVersionChars);
        Had = Cap(had, JoinSectionLimits.MaxVersionChars);
        Kind = Cap(kind, 16);
        At = DateTime.SpecifyKind(atUtc, DateTimeKind.Utc);
        Join = join;
    }

    public string Mod { get; }
    public string Required { get; }
    public string Had { get; }

    /// <summary><see cref="RefusedJoinLog.KindMissing"/>, <c>older</c>, <c>newer</c>, <c>mismatch</c> or <c>server-lacks</c>.</summary>
    public string Kind { get; }

    public DateTime At { get; }
    public long Join { get; }

    /// <summary>
    /// CLIENTPOLICY2: the readable name the admins gave a required client mod row (their own text, never the client's),
    /// for the alert when the row's name can no longer be looked up. Empty otherwise.
    /// </summary>
    public string Name { get; }

    private static string Cap(string? s, int max)
    {
        string t = (s ?? "").Trim();
        var sb = new StringBuilder(Math.Min(t.Length, max));
        foreach (char c in t)
        {
            if (sb.Length >= max) break;
            if (!char.IsControl(c)) sb.Append(c);
        }

        return sb.ToString();
    }
}

/// <summary>
/// JOINADMIN1: the server's bounded record of refused joins (<c>BepInEx/config/progenitor-refused-joins.json</c>):
/// the last <see cref="WindowDays"/> days, at most <see cref="MaxRows"/> rows (the oldest go first), aggregated per
/// mod and version for the admin panel's alert. Game-free.
/// </summary>
internal sealed class RefusedJoinLog
{
    internal const int Schema = 1;
    internal const int MaxRows = 500;
    internal const int WindowDays = 7;

    internal const string KindMissing = "missing";
    internal const string KindOlder = "older";
    internal const string KindNewer = "newer";
    internal const string KindMismatch = "mismatch";
    internal const string KindServerLacks = "server-lacks";

    /// <summary>CLIENTPOLICY1: a required client mod (the admins' row) the player did not have.</summary>
    internal const string KindClientMissing = "client-missing";

    /// <summary>CLIENTPOLICY1: a required client mod at a version below the admins' rule.</summary>
    internal const string KindClientOlder = "client-older";

    /// <summary>CLIENTPOLICY1: a required client mod newer than the admins' rule allows.</summary>
    internal const string KindClientNewer = "client-newer";

    /// <summary>CLIENTPOLICY1: a client mod Only approved refused (its validated id only).</summary>
    internal const string KindNotApproved = "not-approved";

    /// <summary>CLIENTPOLICY1: a player whose mods could not be checked, refused by Refuse them.</summary>
    internal const string KindUnchecked = "unchecked";

    /// <summary>The mod column of an <see cref="KindUnchecked"/> row (no mod is named).</summary>
    internal const string UncheckedMod = "(unchecked)";

    private readonly List<RefusedJoinRow> _rows = new();

    public IReadOnlyList<RefusedJoinRow> Rows => _rows;

    /// <summary>The next join number (never reused within a file).</summary>
    public long NextJoin { get; private set; } = 1;

    /// <summary>
    /// Record one refused join's rows at <paramref name="nowUtc"/>. Rows older than the window go first, then the
    /// oldest rows past <see cref="MaxRows"/>. Returns the join number used, or 0 when there was nothing to record.
    /// </summary>
    public long Record(IReadOnlyList<(string Mod, string Required, string Had, string Kind)>? rows, DateTime nowUtc) =>
        Record(rows, nowUtc, null);

    /// <summary>The same, with the admins' readable names of their own rows (<paramref name="names"/>: mod to name).</summary>
    public long Record(IReadOnlyList<(string Mod, string Required, string Had, string Kind)>? rows, DateTime nowUtc,
        IReadOnlyDictionary<string, string>? names)
    {
        if (rows == null || rows.Count == 0) return 0;
        long join = NextJoin++;
        foreach (var r in rows)
        {
            if (string.IsNullOrWhiteSpace(r.Mod)) continue;
            string? name = names != null && names.TryGetValue(r.Mod, out var n) ? n : null;
            _rows.Add(new RefusedJoinRow(r.Mod, r.Required, r.Had, r.Kind, nowUtc, join, name));
        }

        Prune(nowUtc);
        return join;
    }

    /// <summary>Drop rows outside the window (or dated in the future by more than a day: a clock jump) and past the cap.</summary>
    public void Prune(DateTime nowUtc)
    {
        var cutoff = nowUtc.AddDays(-WindowDays);
        var future = nowUtc.AddDays(1);
        _rows.RemoveAll(r => r.At < cutoff || r.At > future);
        if (_rows.Count > MaxRows) _rows.RemoveRange(0, _rows.Count - MaxRows);
    }

    /// <summary>
    /// The alert: how many joins were refused in the window, and the groups (mod, required, had, kind) with the most
    /// joins first, at most <paramref name="max"/>. <paramref name="nameOf"/> gives a mod's readable name.
    /// </summary>
    public (int Joins, List<JoinAdminAlertRow> Rows) Aggregate(DateTime nowUtc, int max, Func<string, string?>? nameOf = null)
    {
        var cutoff = nowUtc.AddDays(-WindowDays);
        var joins = new HashSet<long>();
        var groups = new Dictionary<string, (RefusedJoinRow First, HashSet<long> Joins, DateTime Last)>(StringComparer.Ordinal);
        foreach (var r in _rows)
        {
            if (r.At < cutoff) continue;
            joins.Add(r.Join);
            string key = r.Mod.ToLowerInvariant() + "\n" + r.Required + "\n" + r.Had + "\n" + r.Kind;
            if (!groups.TryGetValue(key, out var g)) g = (r, new HashSet<long>(), r.At);
            g.Joins.Add(r.Join);
            if (r.At > g.Last) g.Last = r.At;
            groups[key] = g;
        }

        var list = new List<(JoinAdminAlertRow Row, DateTime Last)>();
        foreach (var g in groups.Values)
        {
            string name = "";
            try { name = nameOf?.Invoke(g.First.Mod) ?? ""; }
            catch { name = ""; }
            // CLIENTPOLICY2 (live 2026-10-05): an admin's required client row read as its id; the name stored with the row
            // stands in when the lookup has none (the row was renamed or removed since).
            if (string.IsNullOrWhiteSpace(name)) name = g.First.Name;
            if (string.IsNullOrWhiteSpace(name)) name = g.First.Mod;
            var row = new JoinAdminAlertRow
            {
                Mod = g.First.Mod, Name = name, Required = g.First.Required, Had = g.First.Had, Kind = g.First.Kind, Count = g.Joins.Count,
            };
            row.Text = AlertText(row);
            list.Add((row, g.Last));
        }

        list.Sort((a, b) =>
        {
            int c = b.Row.Count.CompareTo(a.Row.Count);
            if (c != 0) return c;
            c = b.Last.CompareTo(a.Last);
            return c != 0 ? c : string.CompareOrdinal(a.Row.Mod, b.Row.Mod);
        });
        var top = new List<JoinAdminAlertRow>();
        for (int i = 0; i < list.Count && i < max; i++) top.Add(list[i].Row);
        return (joins.Count, top);
    }

    /// <summary>One alert line: <c>HuntsAndGates: players have 1.2.0, this server runs 1.0.0. (3 joins)</c></summary>
    internal static string AlertText(JoinAdminAlertRow r)
    {
        string name = r.Name.Length > 0 ? r.Name : r.Mod;
        // A row with no player version (a record from before this rule) reads as missing too.
        // CLIENTPOLICY1: the admins' own rows read as theirs; a not-approved line names the client's mod id only.
        switch (r.Kind)
        {
            case KindClientMissing:
                return name + ": players don't have it (required by you)" + Joins(r.Count);
            case KindClientOlder:
                return name + ": players have " + (r.Had.Length > 0 ? r.Had : "an older version") + ", you require " + r.Required + Joins(r.Count);
            case KindClientNewer:
                return name + ": players have " + r.Had + ", newer than you allow (" + r.Required + ")" + Joins(r.Count);
            case KindNotApproved:
                // CLIENTPOLICY3: after Approve (or any approval since) the line says so instead of offering it again.
                if (r.Approved)
                    return name + ": approved now (" + r.Count.ToString(CultureInfo.InvariantCulture) + (r.Count == 1 ? " join" : " joins") + " refused before)";
                return name + ": not approved" + Joins(r.Count);
            case KindUnchecked:
                return "Players whose mods can't be checked were refused" + Joins(r.Count);
        }

        string what = (r.Had.Length == 0 && r.Kind != KindServerLacks ? KindMissing : r.Kind) switch
        {
            KindMissing => name + ": players don't have it.",
            KindOlder => name + ": players have " + r.Had + ", this server needs " + r.Required + ".",
            KindServerLacks => name + ": players require " + (r.Had.Length > 0 ? r.Had : "it") + "; this server doesn't run it.",
            _ => name + ": players have " + r.Had + ", this server runs " + r.Required + ".",
        };
        return what + Joins(r.Count);
    }

    private static string Joins(int count) =>
        " (" + count.ToString(CultureInfo.InvariantCulture) + (count == 1 ? " join)" : " joins)");

    /// <summary>
    /// The rows of one refused join, from the same three tables the join check ran on: every failing id <b>the server
    /// itself requires</b>, with its required version and the version the player answered. A mod only the client
    /// declares (the reverse check: "this server doesn't run it") is not recorded: its id and version are whatever the
    /// client sent, so recording it would let any player write text into the admins' alert. The player's version is
    /// kept only when it is a valid version token. Game-free; never throws.
    /// </summary>
    internal static List<(string Mod, string Required, string Had, string Kind)> RowsFor(
        IReadOnlyList<string>? failedIds, IReadOnlyList<Handshake.ModRecord>? server, IReadOnlyList<Handshake.ModRecord>? answered)
    {
        var list = new List<(string, string, string, string)>();
        if (failedIds == null) return list;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string id in failedIds)
        {
            if (string.IsNullOrWhiteSpace(id) || !seen.Add(id) || list.Count >= 32) continue;
            Handshake.ModRecord? s = Find(server, id), c = Find(answered, id);
            if (s == null) continue; // not a mod this server requires: client-chosen text
            string mod = s.Value.Guid;
            if (!ModManifestIds.TryValidateId(mod, out _)) continue;
            string required = s.Value.Version;
            string had = c?.Version ?? "";
            if (had.Length > 0 && !ModManifestIds.TryValidateVersion(had, out _)) had = "?";
            string kind;
            // Live 2026-10-04: a player without the mod can still answer a row for it with no version; that is
            // missing, not a mismatch ("players have , this server runs …").
            if (c == null || had.Length == 0) kind = KindMissing;
            else
            {
                int? cmp = CompareVersions(had, required);
                kind = cmp switch { < 0 => KindOlder, > 0 => KindNewer, _ => KindMismatch };
            }

            list.Add((mod, required, had, kind));
        }

        return list;
    }

    private static Handshake.ModRecord? Find(IReadOnlyList<Handshake.ModRecord>? rows, string id)
    {
        if (rows == null) return null;
        foreach (var r in rows)
            if (string.Equals(r.Guid, id, StringComparison.OrdinalIgnoreCase)) return r;
        return null;
    }

    /// <summary>Dotted-number compare (<c>1.10.0</c> after <c>1.9.2</c>); null when either side is not numeric.</summary>
    internal static int? CompareVersions(string? a, string? b)
    {
        if (!TryParts(a, out var x) || !TryParts(b, out var y)) return null;
        for (int i = 0; i < Math.Max(x.Count, y.Count); i++)
        {
            long p = i < x.Count ? x[i] : 0, q = i < y.Count ? y[i] : 0;
            if (p != q) return p < q ? -1 : 1;
        }

        return 0;
    }

    private static bool TryParts(string? v, out List<long> parts)
    {
        parts = new List<long>();
        string s = (v ?? "").Trim();
        int cut = s.IndexOfAny(new[] { '-', '+' });
        if (cut >= 0) s = s.Substring(0, cut);
        if (s.Length == 0) return false;
        foreach (string p in s.Split('.'))
        {
            if (!long.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out long n)) return false;
            parts.Add(n);
        }

        return true;
    }

    // ---- the file --------------------------------------------------------------------------

    public string ToJson()
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteNumber("schema", Schema);
            w.WriteNumber("nextJoin", NextJoin);
            w.WriteStartArray("rows");
            foreach (var r in _rows)
            {
                w.WriteStartObject();
                w.WriteString("mod", r.Mod);
                w.WriteString("required", r.Required);
                w.WriteString("had", r.Had);
                w.WriteString("kind", r.Kind);
                w.WriteString("at", r.At.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
                w.WriteNumber("join", r.Join);
                if (r.Name.Length > 0) w.WriteString("name", r.Name);
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>Read the file form; anything unreadable is an empty log (a record, not a setting).</summary>
    public static RefusedJoinLog Parse(string? json, DateTime nowUtc)
    {
        var log = new RefusedJoinLog();
        if (string.IsNullOrWhiteSpace(json)) return log;
        try
        {
            using var doc = JsonDocument.Parse(json!, new JsonDocumentOptions { MaxDepth = 8, AllowTrailingCommas = true });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return log;
            long next = root.TryGetProperty("nextJoin", out var n) && n.ValueKind == JsonValueKind.Number && n.TryGetInt64(out long l) ? l : 1;
            if (root.TryGetProperty("rows", out var rows) && rows.ValueKind == JsonValueKind.Array)
            {
                foreach (var r in rows.EnumerateArray())
                {
                    if (r.ValueKind != JsonValueKind.Object) continue;
                    if (log._rows.Count >= MaxRows * 2) break;
                    if (!DateTime.TryParse(JoinAdminConfig.Str(r, "at"), CultureInfo.InvariantCulture,
                            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var at)) continue;
                    long join = r.TryGetProperty("join", out var j) && j.ValueKind == JsonValueKind.Number && j.TryGetInt64(out long jl) ? jl : 0;
                    log._rows.Add(new RefusedJoinRow(JoinAdminConfig.Str(r, "mod"), JoinAdminConfig.Str(r, "required"),
                        JoinAdminConfig.Str(r, "had"), JoinAdminConfig.Str(r, "kind"), at, join, JoinAdminConfig.Str(r, "name")));
                    if (join >= next) next = join + 1;
                }
            }

            log.NextJoin = Math.Max(1, next);
            log.Prune(nowUtc);
        }
        catch (JsonException) { /* an unreadable record starts empty */ }

        return log;
    }
}

/// <summary>
/// JOINADMIN1, server: the refused-join record as the handshake feeds it. A failing answer is <see cref="Note"/>d;
/// a refusal <see cref="Commit"/>s it (a kick now, or an install window that expired); an accepted answer
/// <see cref="Drop"/>s it. Held notes are keyed by the connection's user index only while the join is in flight
/// and are never written. Never throws into the caller.
/// </summary>
internal static class RefusedJoins
{
    internal const string FileName = "progenitor-refused-joins.json";
    private const int MaxHeld = 256;
    private const double FlushEverySeconds = 2;

    private static readonly object Gate = new();
    private static readonly Dictionary<int, List<(string Mod, string Required, string Had, string Kind)>> Held = new();
    private static RefusedJoinLog? _log;
    private static bool _dirty;
    private static double _lastFlush = double.NegativeInfinity;

    /// <summary>Wall clock for the rows; tests pin it.</summary>
    internal static Func<DateTime> Clock = () => DateTime.UtcNow;

    internal static string FilePath => Path.Combine(ModManifest.ConfigDirectory, FileName);

    /// <summary>The log, read from the file on first use.</summary>
    internal static RefusedJoinLog Log
    {
        get
        {
            lock (Gate) return EnsureLoaded();
        }
    }

    private static RefusedJoinLog EnsureLoaded()
    {
        if (_log != null) return _log;
        string text = "";
        try
        {
            if (File.Exists(FilePath) && new FileInfo(FilePath).Length <= 2 * 1024 * 1024) text = File.ReadAllText(FilePath);
        }
        catch { text = ""; }

        _log = RefusedJoinLog.Parse(text, Clock());
        return _log;
    }

    /// <summary>Refused joins one SteamID may add to the record per <see cref="ThrottleHours"/> (memory only, never written).</summary>
    internal const int MaxCommitsPerSender = 3;

    internal const double ThrottleHours = 1;

    // The connections whose refusal was already recorded: one record per connection, however many failing answers
    // it sends before the kick lands. Cleared when the connection goes or its join is accepted.
    private static readonly HashSet<int> Committed = new();

    // SteamID -> the times of its recorded refusals, for the throttle only. Kept in memory, bounded, never written:
    // the file stays identity-free.
    private static readonly Dictionary<ulong, List<DateTime>> SenderCommits = new();
    private static readonly Dictionary<int, ulong> HeldSender = new();

    // CLIENTPOLICY2: the admins' readable names of the held client rows (mod to name), recorded with them.
    private static readonly Dictionary<int, Dictionary<string, string>> HeldNames = new();

    /// <summary>
    /// A failing answer: hold its rows until the join is refused or accepted. <paramref name="steamId"/> is used
    /// only by the in-memory throttle (<see cref="MaxCommitsPerSender"/>) and is never stored.
    /// </summary>
    internal static void Note(int userIndex, IReadOnlyList<string>? failedIds, IReadOnlyList<Handshake.ModRecord>? server,
        IReadOnlyList<Handshake.ModRecord>? answered, ulong steamId = 0)
    {
        try
        {
            var rows = RefusedJoinLog.RowsFor(failedIds, server, answered);
            lock (Gate)
            {
                if (rows.Count == 0 || Committed.Contains(userIndex))
                {
                    Held.Remove(userIndex);
                    HeldSender.Remove(userIndex);
                    return;
                }

                if (Held.Count >= MaxHeld && !Held.ContainsKey(userIndex))
                {
                    Held.Clear();
                    HeldSender.Clear();
                }

                Held[userIndex] = rows;
                HeldSender[userIndex] = steamId;
                HeldNames.Remove(userIndex);
            }
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("JoinAdmin: refused-join note threw: " + e.Message); }
    }

    /// <summary>
    /// CLIENTPOLICY1: hold the rows of a client mod policy refusal (<see cref="ClientModJudge.RecordRows"/>), replacing
    /// whatever the handshake held, so the following <see cref="Commit"/> records them under the same once-per-connection
    /// and per-SteamID rules.
    /// </summary>
    internal static void NoteRows(int userIndex, IReadOnlyList<(string Mod, string Required, string Had, string Kind)>? rows, ulong steamId = 0,
        IReadOnlyDictionary<string, string>? names = null)
    {
        try
        {
            lock (Gate)
            {
                if (rows == null || rows.Count == 0 || Committed.Contains(userIndex))
                {
                    Held.Remove(userIndex);
                    HeldSender.Remove(userIndex);
                    return;
                }

                if (Held.Count >= MaxHeld && !Held.ContainsKey(userIndex))
                {
                    Held.Clear();
                    HeldSender.Clear();
                }

                Held[userIndex] = new List<(string, string, string, string)>(rows);
                HeldSender[userIndex] = steamId;
                if (names != null && names.Count > 0)
                    HeldNames[userIndex] = new Dictionary<string, string>(names, StringComparer.OrdinalIgnoreCase);
                else HeldNames.Remove(userIndex);
            }
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("JoinAdmin: refused-join note threw: " + e.Message); }
    }

    /// <summary>
    /// The join was refused: record its held rows — once per connection, and at most
    /// <see cref="MaxCommitsPerSender"/> per SteamID per hour (a player reconnecting in a loop cannot flood the
    /// record or rotate real rows out of it).
    /// </summary>
    internal static void Commit(int userIndex)
    {
        try
        {
            List<(string, string, string, string)>? rows;
            bool throttled = false;
            lock (Gate)
            {
                if (!Held.TryGetValue(userIndex, out rows)) return;
                Held.Remove(userIndex);
                HeldNames.TryGetValue(userIndex, out var names);
                HeldNames.Remove(userIndex);
                HeldSender.TryGetValue(userIndex, out ulong sender);
                HeldSender.Remove(userIndex);
                if (Committed.Count >= MaxHeld) Committed.Clear();
                Committed.Add(userIndex);
                var now = Clock();
                if (sender != 0)
                {
                    if (!SenderCommits.TryGetValue(sender, out var times))
                    {
                        if (SenderCommits.Count >= 1024) SenderCommits.Clear();
                        SenderCommits[sender] = times = new List<DateTime>(MaxCommitsPerSender);
                    }

                    times.RemoveAll(t => t < now.AddHours(-ThrottleHours) || t > now.AddMinutes(5));
                    if (times.Count >= MaxCommitsPerSender) throttled = true;
                    else times.Add(now);
                }

                if (!throttled)
                {
                    EnsureLoaded().Record(rows, now, names);
                    _dirty = true;
                }
            }

            Plugin.Logger?.LogInfo(throttled
                ? "JoinAdmin: refused join not recorded (this player already has " + MaxCommitsPerSender + " this hour)"
                : "JoinAdmin: refused join recorded (" + rows.Count.ToString(CultureInfo.InvariantCulture) + " mod row(s))");
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("JoinAdmin: refused-join record threw: " + e.Message); }
    }

    /// <summary>
    /// The join was accepted, or the connection went away: forget its held rows and its "already recorded" mark, so
    /// the next connection on this user index starts fresh.
    /// </summary>
    internal static void Drop(int userIndex)
    {
        lock (Gate)
        {
            Held.Remove(userIndex);
            HeldSender.Remove(userIndex);
            HeldNames.Remove(userIndex);
            Committed.Remove(userIndex);
        }
    }

    /// <summary>
    /// TEST ONLY (the dev bridge's <c>join_admin inject_refused</c>): record <paramref name="count"/> synthetic refused
    /// joins over one mod, exactly as a real refusal would be recorded.
    /// </summary>
    internal static int InjectForTest(string mod, string required, string had, string kind, int count)
    {
        int n = Math.Max(1, Math.Min(50, count));
        string k = string.IsNullOrWhiteSpace(kind)
            ? (had.Length == 0 ? RefusedJoinLog.KindMissing : RefusedJoinLog.CompareVersions(had, required) switch
            {
                < 0 => RefusedJoinLog.KindOlder,
                > 0 => RefusedJoinLog.KindNewer,
                _ => RefusedJoinLog.KindMismatch,
            })
            : kind.Trim().ToLowerInvariant();
        lock (Gate)
        {
            var log = EnsureLoaded();
            for (int i = 0; i < n; i++) log.Record(new[] { (mod, required, had, k) }, Clock());
            _dirty = true;
        }

        Plugin.Logger?.LogWarning("JoinAdmin: TEST ONLY: injected " + n.ToString(CultureInfo.InvariantCulture) + " synthetic refused join(s) for " + mod + " (bridge)");
        return n;
    }

    /// <summary>TEST ONLY (the bridge's <c>join_admin clear_refused</c>): empty the record, in memory and on disk.</summary>
    internal static int ClearForTest()
    {
        int n;
        lock (Gate)
        {
            var log = EnsureLoaded();
            n = log.Rows.Count;
            _log = new RefusedJoinLog();
            _dirty = true;
        }

        Flush();
        Plugin.Logger?.LogWarning("JoinAdmin: TEST ONLY: cleared " + n.ToString(CultureInfo.InvariantCulture) + " refused-join row(s) (bridge)");
        return n;
    }

    /// <summary>Write the file now when it changed (atomic: a temp file, then a replace).</summary>
    internal static void Flush()
    {
        string json;
        lock (Gate)
        {
            if (!_dirty || _log == null) return;
            _log.Prune(Clock());
            json = _log.ToJson();
            _dirty = false;
        }

        try { JoinAdminFiles.WriteAtomic(FilePath, json); }
        catch (Exception e)
        {
            lock (Gate) _dirty = true;
            Plugin.Logger?.LogWarning("JoinAdmin: " + FileName + " could not be written: " + e.Message);
        }
    }

    /// <summary>Server frame: flush at most every couple of seconds.</summary>
    internal static void Tick(double nowSeconds)
    {
        bool dirty;
        lock (Gate) dirty = _dirty;
        if (!dirty || nowSeconds - _lastFlush < FlushEverySeconds) return;
        _lastFlush = nowSeconds;
        Flush();
    }

    /// <summary>Core going down: write what is pending and forget everything held in memory.</summary>
    internal static void Uninstall()
    {
        try { Flush(); }
        catch { /* teardown */ }
        lock (Gate)
        {
            Held.Clear();
            HeldSender.Clear();
            HeldNames.Clear();
            Committed.Clear();
            SenderCommits.Clear();
            _log = null;
            _dirty = false;
            _lastFlush = double.NegativeInfinity;
        }

        Clock = () => DateTime.UtcNow;
    }

    internal static int HeldCount
    {
        get { lock (Gate) return Held.Count; }
    }
}

/// <summary>Atomic text writes for the join admin's files.</summary>
internal static class JoinAdminFiles
{
    internal static void WriteAtomic(string path, string text)
    {
        string dir = Path.GetDirectoryName(path) ?? ".";
        Directory.CreateDirectory(dir);
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, text, new UTF8Encoding(false));
        if (File.Exists(path)) File.Replace(tmp, path, null);
        else File.Move(tmp, path);
    }
}
