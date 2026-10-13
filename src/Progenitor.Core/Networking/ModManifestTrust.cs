using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Progenitor.Networking;

/// <summary>
/// Last catalog the client accepted this join, plus the trust decision a UI or
/// <c>.mods</c> command acts on. First contact never auto-trusts; a fingerprint
/// change for a known host is reported and never auto-accepted.
/// </summary>
public sealed class PendingManifest
{
    public PendingManifest(
        ManifestEnvelope envelope,
        ManifestDiff diff,
        string host,
        string? previousFingerprint)
    {
        Envelope = envelope ?? throw new ArgumentNullException(nameof(envelope));
        Diff = diff ?? throw new ArgumentNullException(nameof(diff));
        Host = host ?? "";
        PreviousFingerprint = string.IsNullOrEmpty(previousFingerprint) ? null : previousFingerprint;
    }

    public ManifestEnvelope Envelope { get; }
    public ManifestDiff Diff { get; }
    public string Host { get; }
    public string? PreviousFingerprint { get; }

    public string OperatorName => Envelope.Body.Operator.Name;
    public string Fingerprint => Envelope.Body.Operator.Fp;
    public string Spki => Envelope.Body.Operator.Spki;
    public bool FirstContact => Diff.UnverifiedOperator;
    public bool FingerprintChanged => Diff.FingerprintChanged;
    public bool SignatureValid => Diff.SignatureValid;
    public bool OperatorTrusted => Diff.OperatorTrusted;
    public IReadOnlyList<ManifestMod> Missing => Diff.Missing;
    public IReadOnlyList<ManifestMismatch> Mismatches => Diff.Mismatches;

    /// <summary>Same greppable block the client log prints on receive.</summary>
    public string FormatReport() => ModManifestCompare.FormatLogBlock(Envelope, Diff, Host);
}

/// <summary>
/// Atomic read/write of <c>progenitor-trusted-operators.json</c>. BCL strings and bools
/// only so a collectible Core generation cannot pin the file. Write is temp + move in
/// the same directory; a leftover <c>.tmp</c> is never consulted on read.
/// </summary>
internal static class ModManifestTrust
{
    public static TrustedOperatorsFile ReadFile(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return new TrustedOperatorsFile();
        try
        {
            string json = File.ReadAllText(path);
            if (!ModManifestJson.TryParseTrust(json, out var file, out _))
                return new TrustedOperatorsFile();
            return file;
        }
        catch
        {
            return new TrustedOperatorsFile();
        }
    }

    public static void WriteAtomic(string path, TrustedOperatorsFile file)
    {
        if (string.IsNullOrEmpty(path)) throw new ArgumentException("path is required", nameof(path));
        string dir = Path.GetDirectoryName(path) ?? "";
        if (dir.Length > 0) Directory.CreateDirectory(dir);
        string json = ModManifestJson.CanonicalTrust(file);
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        try
        {
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            try { File.Copy(tmp, path, overwrite: true); }
            finally
            {
                try { File.Delete(tmp); }
                catch { /* leftover tmp is ignored on the next read */ }
            }
        }
    }

    public static TrustedOperator? FindByFingerprint(TrustedOperatorsFile? file, string fp)
    {
        if (file?.Operators == null || string.IsNullOrEmpty(fp)) return null;
        string want = fp.Trim().ToLowerInvariant();
        for (int i = 0; i < file.Operators.Count; i++)
        {
            var row = file.Operators[i];
            if (row == null) continue;
            if (string.Equals(row.Fp, want, StringComparison.OrdinalIgnoreCase))
                return row;
        }
        return null;
    }

    public static TrustedOperator? FindByHost(TrustedOperatorsFile? file, string host)
    {
        if (file?.Operators == null || string.IsNullOrWhiteSpace(host)) return null;
        string want = host.Trim();
        for (int i = 0; i < file.Operators.Count; i++)
        {
            var row = file.Operators[i];
            if (row == null) continue;
            if (string.Equals(row.Host, want, StringComparison.OrdinalIgnoreCase))
                return row;
        }
        return null;
    }

    /// <summary>
    /// Pin for this host+SPKI. Prefers an exact host+SPKI row; a host-less row with the
    /// same SPKI is the fallback so a pin written without a connect address still matches.
    /// A row without SPKI is never returned (fp-only pins are refused).
    /// </summary>
    public static TrustedOperator? FindPin(
        TrustedOperatorsFile? file,
        string host,
        string spki,
        string fp)
    {
        if (file?.Operators == null || string.IsNullOrEmpty(spki)) return null;
        TrustedOperator? hostless = null;
        for (int i = 0; i < file.Operators.Count; i++)
        {
            var row = file.Operators[i];
            if (row == null || string.IsNullOrEmpty(row.Spki)) continue;
            if (!string.Equals(row.Spki, spki, StringComparison.Ordinal)) continue;
            if (!string.IsNullOrEmpty(fp)
                && !string.IsNullOrEmpty(row.Fp)
                && !string.Equals(row.Fp, fp, StringComparison.OrdinalIgnoreCase))
                continue;
            if (host.Length > 0
                && string.Equals(row.Host, host, StringComparison.OrdinalIgnoreCase))
                return row;
            if (string.IsNullOrEmpty(row.Host))
                hostless = row;
        }
        return hostless;
    }

    /// <summary>
    /// Upsert by host+SPKI (when host is set) else by exact SPKI. Returns the new file.
    /// Does not auto-trust: the caller is an explicit <see cref="ModManifest.TrustOperator"/>.
    /// A host-less write is kept host-less; it is never matched on fingerprint alone.
    /// </summary>
    public static TrustedOperatorsFile Upsert(
        TrustedOperatorsFile? file,
        string host,
        string fp,
        string spki,
        bool always,
        DateTime seenUtc,
        string? issued = null)
    {
        host ??= "";
        spki ??= "";
        var list = new List<TrustedOperator>();
        // JOINFLOW1: re-pinning the same key keeps when it was first pinned and the player's risk
        // acknowledgement; a different key for the host starts over (the trust prompt asks again).
        TrustedOperator? sameKey = null;
        if (file?.Operators != null)
        {
            for (int i = 0; i < file.Operators.Count; i++)
            {
                var row = file.Operators[i];
                if (row == null) continue;
                bool sameHost = host.Length > 0
                    && string.Equals(row.Host, host, StringComparison.OrdinalIgnoreCase);
                bool sameSpki = spki.Length > 0
                    && string.Equals(row.Spki, spki, StringComparison.Ordinal);
                if (sameHost || (host.Length == 0 && sameSpki))
                {
                    if (sameSpki) sameKey = row;
                    continue;
                }
                list.Add(row);
            }
        }
        list.Add(new TrustedOperator
        {
            Host = host,
            Spki = spki,
            Fp = (fp ?? "").Trim().ToLowerInvariant(),
            Always = always,
            Seen = sameKey != null && sameKey.Seen.Length > 0 ? sameKey.Seen : ModManifestJson.FormatIssued(seenUtc),
            Issued = issued ?? "",
            RiskAck = sameKey?.RiskAck ?? "",
        });
        return new TrustedOperatorsFile { Operators = list, ThunderstoreAcks = AcksOf(file) };
    }

    /// <summary>
    /// HOSTKEY1: move the rows written under per-connection relay strings (<see cref="ConnectHostKey.IsRelayString"/>) for
    /// this key to the stable <paramref name="host"/>. Pins: when no row names <paramref name="host"/> yet, the relay rows
    /// with this SPKI (and fingerprint) become one row for it, kept always if any was, with the earliest <c>seen</c> and the
    /// newest risk acknowledgement; they are dropped
    /// either way. Thunderstore confirmations: the relay rows with this SPKI are merged into the host's row. The caller
    /// passes a verified catalog's key only. No <c>issued</c> is carried: relay rows name no address, and one key may sign
    /// several servers' catalogs at different times, so the newest stamp of any of them could reject this server's own
    /// catalog as a replay. The accepted catalog's stamp is stored next (as for a first use). Returns the same instance
    /// when nothing moved.
    /// </summary>
    public static TrustedOperatorsFile AdoptRelayRows(TrustedOperatorsFile? file, string host, string spki, string fp)
    {
        file ??= new TrustedOperatorsFile();
        host = (host ?? "").Trim();
        spki ??= "";
        fp = (fp ?? "").Trim().ToLowerInvariant();
        if (!ConnectHostKey.IsStable(host) || spki.Length == 0) return file;

        bool Relay(string rowHost, string rowSpki, string rowFp) =>
            ConnectHostKey.IsRelayString(rowHost)
            && string.Equals(rowSpki, spki, StringComparison.Ordinal)
            && (fp.Length == 0 || rowFp.Length == 0 || string.Equals(rowFp, fp, StringComparison.OrdinalIgnoreCase));

        bool hostPinned = false;
        var adopted = new List<TrustedOperator>();
        var operators = new List<TrustedOperator>();
        foreach (var row in file.Operators ?? Array.Empty<TrustedOperator>())
        {
            if (row == null) continue;
            if (string.Equals(row.Host, host, StringComparison.OrdinalIgnoreCase)) hostPinned = true;
            if (Relay(row.Host, row.Spki, row.Fp)) adopted.Add(row);
            else operators.Add(row);
        }

        if (adopted.Count > 0 && !hostPinned)
        {
            var merged = new TrustedOperator { Host = host, Spki = spki, Fp = fp.Length > 0 ? fp : adopted[0].Fp };
            string newestAck = "";
            foreach (var row in adopted)
            {
                merged.Always |= row.Always;
                if (row.Seen.Length > 0 && (merged.Seen.Length == 0 || string.CompareOrdinal(row.Seen, merged.Seen) < 0)) merged.Seen = row.Seen;
                if (row.RiskAck.Length > 0 && string.CompareOrdinal(row.RiskAck, newestAck) > 0) newestAck = row.RiskAck;
            }
            merged.RiskAck = newestAck;
            operators.Add(merged);
        }

        var hostAcks = new List<ThunderstoreAck>();
        var relayAcks = new List<ThunderstoreAck>();
        var acks = new List<ThunderstoreAck>();
        foreach (var row in file.ThunderstoreAcks ?? Array.Empty<ThunderstoreAck>())
        {
            if (row == null) continue;
            if (Relay(row.Host, row.Spki, row.Fp)) relayAcks.Add(row);
            else if (string.Equals(row.Spki, spki, StringComparison.Ordinal) && string.Equals(row.Host, host, StringComparison.OrdinalIgnoreCase)) hostAcks.Add(row);
            else acks.Add(row);
        }

        if (adopted.Count == 0 && relayAcks.Count == 0) return file;
        if (relayAcks.Count > 0)
        {
            // The newest confirmations are kept when the merged row is full, as AcknowledgeThunderstore keeps them.
            var all = new List<ThunderstoreAck>(relayAcks);
            all.AddRange(hostAcks);
            all.Sort((a, b) => string.CompareOrdinal(a.At, b.At));
            var packages = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in all)
                foreach (string p in row.Packages ?? Array.Empty<string>())
                    if (!string.IsNullOrWhiteSpace(p) && seen.Add(p.Trim())) packages.Add(p.Trim());
            if (packages.Count > JoinThunderstoreTrust.MaxPackagesPerAck)
                packages = packages.GetRange(packages.Count - JoinThunderstoreTrust.MaxPackagesPerAck, JoinThunderstoreTrust.MaxPackagesPerAck);
            var newest = all[all.Count - 1];
            acks.Add(new ThunderstoreAck
            {
                Host = host,
                Spki = spki,
                Fp = fp.Length > 0 ? fp : newest.Fp,
                At = newest.At,
                Packages = packages,
            });
        }
        else
        {
            acks.AddRange(hostAcks);
        }

        return new TrustedOperatorsFile { Operators = operators, ThunderstoreAcks = acks };
    }

    /// <summary>JOINFLOW2: every transform carries the Thunderstore confirmations through unchanged.</summary>
    private static IReadOnlyList<ThunderstoreAck> AcksOf(TrustedOperatorsFile? file) =>
        file?.ThunderstoreAcks ?? Array.Empty<ThunderstoreAck>();

    /// <summary>
    /// Advance last-seen <c>issued</c> on the matching host+SPKI pin when the new stamp
    /// is strictly newer. Returns the same instance when nothing changes.
    /// Session rows (<c>Always == false</c>) are copied through so an in-memory file
    /// keeps them; <see cref="ModManifestJson.CanonicalTrust"/> drops them on write.
    /// </summary>
    public static TrustedOperatorsFile AdvanceIssued(
        TrustedOperatorsFile? file,
        string host,
        string spki,
        string issued)
    {
        if (file?.Operators == null || string.IsNullOrEmpty(spki) || string.IsNullOrEmpty(issued))
            return file ?? new TrustedOperatorsFile();
        if (!ModManifestJson.TryParseIssued(issued, out var incoming))
            return file;

        bool changed = false;
        var list = new List<TrustedOperator>(file.Operators.Count);
        for (int i = 0; i < file.Operators.Count; i++)
        {
            var row = file.Operators[i];
            if (row == null) continue;
            bool match = string.Equals(row.Spki, spki, StringComparison.Ordinal)
                && (host.Length == 0
                    ? string.IsNullOrEmpty(row.Host)
                    : string.Equals(row.Host, host, StringComparison.OrdinalIgnoreCase));
            if (!match)
            {
                list.Add(row);
                continue;
            }
            if (ModManifestJson.TryParseIssued(row.Issued, out var last) && incoming <= last)
            {
                list.Add(row);
                continue;
            }
            changed = true;
            list.Add(new TrustedOperator
            {
                Host = row.Host,
                Spki = row.Spki,
                Fp = row.Fp,
                Always = row.Always,
                Seen = row.Seen,
                Issued = issued,
                RiskAck = row.RiskAck,
            });
        }
        return changed ? new TrustedOperatorsFile { Operators = list, ThunderstoreAcks = AcksOf(file) } : file;
    }

    /// <summary>
    /// JOINFLOW2: record the player's confirmation of a Thunderstore batch this server lists, for this host
    /// and key. Keeps the packages of an earlier confirmation for the same host and key (they were already
    /// confirmed), capped at <see cref="JoinThunderstoreTrust.MaxPackagesPerAck"/>; at most
    /// <see cref="JoinThunderstoreTrust.MaxAcks"/> rows (the oldest dropped). Needs no pin and adds none.
    /// </summary>
    public static TrustedOperatorsFile AcknowledgeThunderstore(
        TrustedOperatorsFile? file, string host, string spki, string fp, IEnumerable<string> packages, DateTime nowUtc)
    {
        file ??= new TrustedOperatorsFile();
        host ??= "";
        spki ??= "";
        if (spki.Length == 0) return file;
        var merged = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rows = new List<ThunderstoreAck>();
        foreach (var row in file.ThunderstoreAcks ?? Array.Empty<ThunderstoreAck>())
        {
            if (row == null) continue;
            if (string.Equals(row.Spki, spki, StringComparison.Ordinal) && string.Equals(row.Host, host, StringComparison.OrdinalIgnoreCase))
            {
                foreach (string p in row.Packages ?? Array.Empty<string>())
                    if (!string.IsNullOrWhiteSpace(p) && seen.Add(p.Trim())) merged.Add(p.Trim());
                continue;
            }

            rows.Add(row);
        }

        foreach (string p in packages ?? Array.Empty<string>())
            if (!string.IsNullOrWhiteSpace(p) && seen.Add(p.Trim())) merged.Add(p.Trim());
        // The newest confirmations are kept when the row is full.
        if (merged.Count > JoinThunderstoreTrust.MaxPackagesPerAck)
            merged = merged.GetRange(merged.Count - JoinThunderstoreTrust.MaxPackagesPerAck, JoinThunderstoreTrust.MaxPackagesPerAck);
        rows.Add(new ThunderstoreAck
        {
            Host = host,
            Spki = spki,
            Fp = (fp ?? "").Trim().ToLowerInvariant(),
            At = ModManifestJson.FormatIssued(nowUtc),
            Packages = merged,
        });
        if (rows.Count > JoinThunderstoreTrust.MaxAcks)
        {
            rows.Sort((a, b) => string.CompareOrdinal(a.At, b.At));
            rows = rows.GetRange(rows.Count - JoinThunderstoreTrust.MaxAcks, JoinThunderstoreTrust.MaxAcks);
        }

        return new TrustedOperatorsFile { Operators = file.Operators, ThunderstoreAcks = rows };
    }

    /// <summary>JOINFLOW2 test support: drop the Thunderstore confirmations of operator <paramref name="fp"/> (every host).</summary>
    public static TrustedOperatorsFile ClearThunderstoreAcks(TrustedOperatorsFile? file, string fp)
    {
        file ??= new TrustedOperatorsFile();
        string want = (fp ?? "").Trim().ToLowerInvariant();
        var rows = new List<ThunderstoreAck>();
        bool changed = false;
        foreach (var row in file.ThunderstoreAcks ?? Array.Empty<ThunderstoreAck>())
        {
            if (row == null) continue;
            if (want.Length > 0 && string.Equals(row.Fp, want, StringComparison.OrdinalIgnoreCase))
            {
                changed = true;
                continue;
            }

            rows.Add(row);
        }

        return changed ? new TrustedOperatorsFile { Operators = file.Operators, ThunderstoreAcks = rows } : file;
    }
}
