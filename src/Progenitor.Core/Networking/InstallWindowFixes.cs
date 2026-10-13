using System;
using System.Collections.Generic;
using System.Globalization;
using Progenitor.Install;
using Progenitor.Plugins;

namespace Progenitor.Networking;

/// <summary>How one failing row can be fixed during the join, as the server judges it (the install window's trigger).</summary>
internal enum MissFix : byte
{
    /// <summary>Nothing this join can do: the kick is not deferred.</summary>
    None = 0,

    /// <summary>The signed catalog (or the admins' required client row) names a Thunderstore package that serves the needed version.</summary>
    Thunderstore = 1,

    /// <summary>The client declared a fix of its own for this row: a kept copy it can swap in, or a copy it can turn on.</summary>
    Local = 2,

    /// <summary>A not-allowed client mod the client can pause for this server (a hot-loaded Progenitor mod).</summary>
    Pause = 3,

    /// <summary>SELFFIX1: the client claimed a copy it can turn on now (untrusted: it only earns the window).</summary>
    TurnOn = 4,

    /// <summary>SELFFIX1: the client claimed its configured local test source offers it (untrusted: it only earns the window).</summary>
    LocalTestSource = 5,
}

/// <summary>
/// The install window's retargeted trigger (2026-10-05). Servers no longer send mod files, so a refused join is held
/// open only when every failing row can be fixed <b>on the client</b> during the join: a Thunderstore package that serves
/// the exact version the server needs (the join screens' own availability rule, <see cref="ThunderstoreVersions"/>), a
/// kept copy or a turned-off copy the client declared it can use (<see cref="FixGuid"/>), or, for the client mod policy, a
/// not-allowed mod the client can pause. Game-free: the handshake and the tests call this same code.
/// </summary>
internal static class InstallWindowFixes
{
    /// <summary>
    /// Reserved answer row: the answer rows (by position) this client can fix by itself during the join — a kept copy
    /// that loads now, or a turned-off copy that hot-loads — as <see cref="FixFormat"/> plus a hex bit mask. Reserved like
    /// <see cref="Handshake.CapabilityGuid"/>: never compared as a plugin, and an older server ignores it (it is a
    /// <c>ClientOnly</c> row, which no check reads). Appended after the capability row, only when a slot is free.
    /// </summary>
    public const string FixGuid = "progenitor.hs.fix";

    /// <summary>First character of the fix blob; the rest is the bit mask in lowercase hex (bit i = answer row i).</summary>
    public const char FixFormat = '1';

    /// <summary>The fix row for <paramref name="mask"/>, or null when nothing is fixable locally.</summary>
    public static Handshake.ModRecord? CreateFixRecord(uint mask) =>
        mask == 0 ? null : Handshake.ModRecord.CreateDeclared(
            FixGuid,
            FixFormat + mask.ToString("x", CultureInfo.InvariantCulture),
            NetworkCompatibilityLevel.ClientOnly,
            NetworkVersionRule.None);

    /// <summary>The mask in one fix blob; anything malformed reads as 0 (nothing declared).</summary>
    public static uint ReadFixBlob(string? version)
    {
        if (string.IsNullOrEmpty(version) || version![0] != FixFormat) return 0;
        string hex = version.Substring(1);
        if (hex.Length == 0 || hex.Length > 8) return 0;
        return uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint bits) ? bits : 0;
    }

    /// <summary>
    /// Append the fix row, last, when <paramref name="mask"/> is not 0 and a slot is free. False otherwise: answering the
    /// server's question is worth more than declaring a fix, so no row is ever dropped to make room.
    /// </summary>
    public static bool AppendFixRow(List<Handshake.ModRecord>? rows, uint mask)
    {
        if (rows == null || mask == 0) return false;
        for (int i = rows.Count - 1; i >= 0; i--)
            if (string.Equals(rows[i].Guid, FixGuid, StringComparison.OrdinalIgnoreCase)) rows.RemoveAt(i);
        if (rows.Count >= Handshake.MaxMods) return false;
        var rec = CreateFixRecord(mask);
        if (rec == null) return false;
        rows.Add(rec.Value);
        return true;
    }

    /// <summary>
    /// The client side: bit i set when answer row i (an asked row) is fixable locally by <paramref name="localFix"/>. Only the
    /// first 32 rows can carry a bit (the answer holds at most <see cref="Handshake.MaxMods"/>). A throwing probe reads as
    /// "no fix".
    /// </summary>
    public static uint LocalFixMask(IReadOnlyList<Handshake.ModRecord>? answerRows, ICollection<string>? askedGuids, Func<string, bool>? localFix)
    {
        if (answerRows == null || localFix == null) return 0;
        uint mask = 0;
        for (int i = 0; i < answerRows.Count && i < 32; i++)
        {
            string guid = answerRows[i].Guid ?? "";
            if (guid.Length == 0 || Handshake.Compare.IsReservedGuid(guid)) continue;
            if (askedGuids != null && !askedGuids.Contains(guid)) continue;
            bool fix;
            try { fix = localFix(guid); }
            catch { fix = false; }
            if (fix) mask |= 1u << i;
        }

        return mask;
    }

    /// <summary>
    /// The server side: the ids whose answer row carries the client's local-fix bit. <paramref name="sent"/> is the answer
    /// <b>as sent</b>, every row in its position (reserved rows included), because those are the positions the client
    /// counted; reserved rows never count as fixes.
    /// </summary>
    public static HashSet<string> DeclaredIds(IReadOnlyList<Handshake.ModRecord>? sent, uint mask)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (sent == null || mask == 0) return ids;
        for (int i = 0; i < sent.Count && i < 32; i++)
        {
            if ((mask & (1u << i)) == 0) continue;
            string guid = sent[i].Guid ?? "";
            if (guid.Length == 0 || Handshake.Compare.IsReservedGuid(guid)) continue;
            ids.Add(guid);
        }

        return ids;
    }

    /// <summary>Whether answer row <paramref name="guid"/> carries the client's local-fix bit (<see cref="DeclaredIds"/>).</summary>
    public static bool DeclaredLocal(IReadOnlyList<Handshake.ModRecord>? sent, uint mask, string guid) =>
        !string.IsNullOrEmpty(guid) && DeclaredIds(sent, mask).Contains(guid);

    /// <summary>
    /// How a failing handshake row is fixed: the client's own declaration first (it costs no download), else a Thunderstore
    /// package of the signed catalog row that serves the version this server's row needs under its rule.
    /// <paramref name="availability"/> answers from the server's read of the Thunderstore index; <see cref="PackageVersionAvailability.Unknown"/>
    /// (not read yet) is not a fix, so a server that cannot reach the index kicks exactly as before.
    /// </summary>
    public static MissFix ForHandshakeRow(
        string id,
        bool declaredLocal,
        string? package,
        string? neededVersion,
        NetworkVersionRule rule,
        Func<string, string, NetworkVersionRule, PackageVersionAvailability>? availability)
    {
        if (string.IsNullOrEmpty(id)) return MissFix.None;
        if (declaredLocal) return MissFix.Local;
        if (string.IsNullOrWhiteSpace(package) || availability == null) return MissFix.None;
        try
        {
            return availability(package!.Trim(), neededVersion ?? "", rule) == PackageVersionAvailability.Available
                ? MissFix.Thunderstore
                : MissFix.None;
        }
        catch
        {
            return MissFix.None;
        }
    }

    /// <summary>
    /// SELFFIX1: the client's claim for one required row, or <see cref="MissFix.None"/>. Only a claim for a key the row
    /// names (its plugin id or package; the problem's own key when there is no row), of a known kind, at a version the
    /// row's rule accepts (a row that names a version needs a claimed version) counts. Untrusted: it only earns the window.
    /// </summary>
    internal static MissFix ClaimedFix(ClientModReport? report, ClientModRow? row, string key)
    {
        if (report == null || report.Fixes.Count == 0) return MissFix.None;
        int seen = 0;
        foreach (var f in report.Fixes)
        {
            if (++seen > ClientModFix.MaxFixes) break;
            if (f == null || f.Key.Length == 0) continue;
            bool names = row != null ? row.Names(f.Key) : string.Equals(f.Key, key, StringComparison.OrdinalIgnoreCase);
            if (!names) continue;
            if (row != null && row.Version.Length > 0
                && (f.Version.Length == 0 || !ClientModRules.Passes(row.Rule, row.Version, f.Version))) continue;
            if (f.Kind == ClientModFix.KindTurnOn) return MissFix.TurnOn;
            if (f.Kind == ClientModFix.KindLocalTest) return MissFix.LocalTestSource;
        }

        return MissFix.None;
    }

    /// <summary>The log word of a fix kind.</summary>
    public static string FixName(MissFix fix) => fix switch
    {
        MissFix.Thunderstore => "thunderstore",
        MissFix.Local => "local",
        MissFix.Pause => "pause",
        MissFix.TurnOn => "turn-on (claimed)",
        MissFix.LocalTestSource => "local-test (claimed)",
        _ => "none",
    };

    /// <summary>The Thunderstore lookup rule for an admins' client rule, as the join screens use it.</summary>
    public static NetworkVersionRule LookupRule(ClientModRule rule) => rule switch
    {
        ClientModRule.Exact => NetworkVersionRule.Exact,
        ClientModRule.Minor => NetworkVersionRule.Minor,
        _ => NetworkVersionRule.None,
    };

    /// <summary>
    /// The client mod policy's half (rule 4 of docs/client-mod-policy.md): true when every problem of a refusing
    /// <paramref name="verdict"/> can be fixed during the join — a required client mod whose row names a Thunderstore
    /// package serving an accepted version, or a not-allowed mod the report shows as a hot-loaded Progenitor mod (the kind
    /// the per-server pause can unload). A verdict that could not be checked never defers. <paramref name="keys"/> lists
    /// the problems the window waits for; <paramref name="why"/> says why it does not defer.
    /// </summary>
    public static bool PolicyFixable(
        ClientModVerdict? verdict,
        ClientPolicyConfig? policy,
        ClientModReport? report,
        Func<string, string, NetworkVersionRule, PackageVersionAvailability>? availability,
        out List<string> keys,
        out string why) => PolicyFixable(verdict, policy, report, availability, out keys, out _, out why);

    /// <summary>The same, with how each key is fixed (<see cref="FixName"/>), for the server's log.</summary>
    public static bool PolicyFixable(
        ClientModVerdict? verdict,
        ClientPolicyConfig? policy,
        ClientModReport? report,
        Func<string, string, NetworkVersionRule, PackageVersionAvailability>? availability,
        out List<string> keys,
        out List<MissFix> kinds,
        out string why)
    {
        keys = new List<string>();
        kinds = new List<MissFix>();
        why = "";
        if (verdict == null || verdict.Outcome != ClientJudgmentOutcome.Refuse) { why = "nothing was refused"; return false; }
        if (verdict.Unchecked) { why = "the client's mods could not be checked"; return false; }
        if (verdict.Required.Count == 0 && verdict.NotAllowed.Count == 0) { why = "the refusal names no mod"; return false; }

        foreach (var p in verdict.Required)
        {
            ClientModRow? row = null;
            if (policy != null)
                foreach (var r in policy.Required)
                    if (r.Names(p.Key)) { row = r; break; }
            var fix = row == null || row.Package.Length == 0 ? MissFix.None
                : ForHandshakeRow(p.Key, false, row.Package, row.Version, LookupRule(row.Rule), availability);
            // SELFFIX1: else the client's own claim (a copy it turns on, its local test source). Untrusted: it only earns
            // the window; when the window ends the real report is judged again.
            if (fix != MissFix.Thunderstore) fix = ClaimedFix(report, row, p.Key);
            if (fix == MissFix.None)
            {
                why = row == null || row.Package.Length == 0
                    ? "'" + p.Key + "' has no Thunderstore package (and the client claims no fix of its own)"
                    : "'" + p.Key + "' is not served on Thunderstore at an accepted version (or the index is not read yet), and the client claims no fix of its own";
                return false;
            }

            keys.Add(p.Key);
            kinds.Add(fix);
        }

        foreach (var p in verdict.NotAllowed)
        {
            bool pausable = false;
            if (report != null)
                foreach (var e in report.Entries)
                    if (string.Equals(e.Guid, p.Key, StringComparison.OrdinalIgnoreCase))
                    {
                        pausable = string.Equals(e.Source, ClientModSources.Progenitor, StringComparison.Ordinal);
                        break;
                    }
            if (!pausable) { why = "'" + p.Key + "' is not a mod the client can pause (it loaded at start)"; return false; }
            keys.Add(p.Key);
            kinds.Add(MissFix.Pause);
        }

        return keys.Count > 0;
    }
}
