using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Progenitor.Plugins;

namespace Progenitor.Networking;

/// <summary>Client version that failed the server's <see cref="NetworkVersionRule"/>.</summary>
public readonly struct ManifestMismatch
{
    public ManifestMismatch(ManifestMod wanted, string have, bool hashMismatch = false, string? why = null)
    {
        Wanted = wanted;
        Have = have ?? "";
        HashMismatch = hashMismatch;
        _why = why;
    }

    private readonly string? _why;

    public ManifestMod Wanted { get; }
    public string Have { get; }

    /// <summary>
    /// Mod compatibility C3: the server's own reason when the catalog's Version matching judged the version (<c>this
    /// server requires exactly 1.2.0 (you have 1.3.0)</c>), else empty (the row's rule decided).
    /// </summary>
    public string Why => _why ?? "";

    /// <summary>Catalog sha256 did not match the loaded assembly bytes.</summary>
    public bool HashMismatch { get; }
}

/// <summary>
/// Mod compatibility C3: a catalog row whose version differs from this client's, which the server's own judgment
/// (its Version matching, a per-mod rule or the newer side's range) accepts. Listed with <see cref="ManifestDiff.Present"/>.
/// </summary>
public readonly struct ManifestAccepted
{
    public ManifestAccepted(ManifestMod wanted, string have, string why)
    {
        Wanted = wanted;
        Have = have ?? "";
        Why = why ?? "";
    }

    public ManifestMod Wanted { get; }
    public string Have { get; }

    /// <summary>What accepted it, after the word <c>accepted</c>: <c>by the client's range &gt;=1.4.0 &lt;1.5.0</c>.</summary>
    public string Why { get; }
}

/// <summary>
/// Client-side classification of a received catalog against the plugins this process
/// actually loaded. Phase 1 never installs; this is the log and the later Dev op.
/// </summary>
public sealed class ManifestDiff
{
    public ManifestDiff(
        bool signatureValid,
        bool operatorTrusted,
        bool unverifiedOperator,
        string? signatureError,
        IReadOnlyList<ManifestMod> present,
        IReadOnlyList<ManifestMod> missing,
        IReadOnlyList<ManifestMismatch> mismatches,
        IReadOnlyList<ManifestMod> restartRequired,
        bool fingerprintChanged = false,
        string? previousFingerprint = null,
        bool replayed = false,
        string? replayError = null,
        IReadOnlyList<string>? hashNotCheckedIds = null,
        bool sessionTrust = false,
        IReadOnlyList<ManifestMod>? serverOnly = null,
        IReadOnlyList<ManifestAccepted>? accepted = null)
    {
        SignatureValid = signatureValid;
        OperatorTrusted = operatorTrusted;
        UnverifiedOperator = unverifiedOperator;
        SignatureError = signatureError;
        Present = present ?? Array.Empty<ManifestMod>();
        Missing = missing ?? Array.Empty<ManifestMod>();
        Mismatches = mismatches ?? Array.Empty<ManifestMismatch>();
        RestartRequired = restartRequired ?? Array.Empty<ManifestMod>();
        FingerprintChanged = fingerprintChanged;
        PreviousFingerprint = string.IsNullOrEmpty(previousFingerprint) ? null : previousFingerprint;
        Replayed = replayed;
        ReplayError = string.IsNullOrEmpty(replayError) ? null : replayError;
        HashNotCheckedIds = hashNotCheckedIds ?? Array.Empty<string>();
        SessionTrust = sessionTrust;
        ServerOnly = serverOnly ?? Array.Empty<ManifestMod>();
        Accepted = accepted ?? Array.Empty<ManifestAccepted>();
    }

    public bool SignatureValid { get; }
    public bool OperatorTrusted { get; }

    /// <summary>
    /// Signature matched the embedded SPKI but that fingerprint is not in
    /// <c>progenitor-trusted-operators.json</c>. First contact never auto-trusts;
    /// the join itself is already decided by the handshake.
    /// </summary>
    public bool UnverifiedOperator { get; }

    /// <summary>
    /// A trust row for this host exists with a different fingerprint. Never auto-accepted;
    /// the player must call <see cref="ModManifest.TrustOperator"/> explicitly.
    /// </summary>
    public bool FingerprintChanged { get; }

    public string? PreviousFingerprint { get; }

    public string? SignatureError { get; }
    public IReadOnlyList<ManifestMod> Present { get; }
    public IReadOnlyList<ManifestMod> Missing { get; }
    public IReadOnlyList<ManifestMismatch> Mismatches { get; }
    public IReadOnlyList<ManifestMod> RestartRequired { get; }

    /// <summary>
    /// Signature matched but this envelope's <c>issued</c> is older than the last seen
    /// stamp for the pinned host/key. Never stored as <c>LastEnvelope</c>.
    /// </summary>
    public bool Replayed { get; }

    public string? ReplayError { get; }

    /// <summary>Catalog ids that are version-present but have no 64-hex sha256 to compare.</summary>
    public IReadOnlyList<string> HashNotCheckedIds { get; }

    /// <summary>
    /// The matching pin is <c>always:false</c>: trusted for this process only, not on disk.
    /// </summary>
    public bool SessionTrust { get; }

    /// <summary>
    /// Catalog rows flagged <see cref="ManifestMod.ServerOnly"/>. Never in <see cref="Missing"/>,
    /// <see cref="Mismatches"/>, <see cref="Present"/> or <see cref="RestartRequired"/>: the plugin
    /// runs in the server process only, so the client neither needs nor can use a copy.
    /// </summary>
    public IReadOnlyList<ManifestMod> ServerOnly { get; }

    /// <summary>
    /// Mod compatibility C3: the <see cref="Present"/> rows this client runs at another version that the server's judgment
    /// accepts (an extended catalog's Version matching, per-mod rules and ranges). Never in <see cref="Mismatches"/>: a
    /// mismatch is only a version the server would refuse.
    /// </summary>
    public IReadOnlyList<ManifestAccepted> Accepted { get; }
}

/// <summary>
/// Compare a signed catalog to the client's loaded plugin list. Version matching reuses
/// <see cref="Handshake.Compare.VersionsMatch"/> so Exact / Minor / None cannot drift
/// from the kick path; mod compatibility C3: an extended catalog (one that carries the server's Version matching) is
/// judged by <see cref="VersionMatchJudge.Check"/> under <see cref="VersionMatching.ClientContextFor"/>, the server's own
/// judgment, so a version the server accepts is present (<see cref="ManifestDiff.Accepted"/>), not a mismatch. <see cref="NetworkCompatibilityLevel.ClientOnly"/> rows are listed
/// but never treated as join-blocking (restart-required still reports them when a later
/// installer would need a process restart).
/// </summary>
internal static class ModManifestCompare
{
    /// <summary>Fail-closed log when the body's claimed fp is not SHA-256(SPKI)[:16].</summary>
    public const string FingerprintRejectedLog =
        "Manifest: fingerprint does not match the embedded key; rejected";

    public static string FpOnlyTrustRowLog(string fp) =>
        "ModManifest: trust row for fp=" + (fp ?? "") + " has no spki; refused";

    private static readonly HashSet<string> LoggedFpOnly = new(StringComparer.OrdinalIgnoreCase);

    internal static void ResetFpOnlyLog()
    {
        lock (LoggedFpOnly) LoggedFpOnly.Clear();
    }

    /// <summary>Log the fp-only refusal at most once per fingerprint per Core generation.</summary>
    internal static bool TryTakeFpOnlyLog(string? refuseLog)
    {
        if (string.IsNullOrEmpty(refuseLog)) return false;
        string fp = refuseLog;
        const string prefix = "ModManifest: trust row for fp=";
        if (fp.StartsWith(prefix, StringComparison.Ordinal))
        {
            int start = prefix.Length;
            int end = fp.IndexOf(" has no spki", start, StringComparison.Ordinal);
            if (end > start) fp = fp.Substring(start, end - start);
        }
        lock (LoggedFpOnly)
            return LoggedFpOnly.Add(fp);
    }

    public static string ReplayRejectedLog(string issued, string lastSeen) =>
        "ModManifest: replayed envelope issued " + (issued ?? "")
        + " is older than last seen " + (lastSeen ?? "") + "; rejected";

    public const string HashMismatchToken = "mismatch (hash)";
    public const string HashNotCheckedToken = "hash not checked";

    public static ManifestDiff Classify(
        ManifestEnvelope? envelope,
        IReadOnlyList<LoadedModInfo> clientMods,
        TrustedOperatorsFile? trust,
        string? host = null,
        Func<string, string, string?>? ownRangeOf = null)
    {
        if (envelope?.Body == null)
        {
            return new ManifestDiff(
                signatureValid: false,
                operatorTrusted: false,
                unverifiedOperator: false,
                signatureError: "no manifest",
                Array.Empty<ManifestMod>(),
                Array.Empty<ManifestMod>(),
                Array.Empty<ManifestMismatch>(),
                Array.Empty<ManifestMod>());
        }

        var body = envelope.Body;
        string claimedFp = body.Operator.Fp ?? "";
        string spki = body.Operator.Spki ?? "";
        if (!TryMatchEmbeddedFingerprint(spki, claimedFp, out var computedFp, out var fpError))
        {
            // Do not walk mods: an unsigned / substituted-key body is not a catalog.
            return Reject(fpError ?? FingerprintRejectedLog);
        }

        string canonical = ModManifestJson.CanonicalBody(body);
        byte[] utf8 = ModManifestCrypto.Utf8(canonical);
        bool sigOk = ModManifestCrypto.Verify(spki, utf8, envelope.Signature);
        if (!sigOk)
        {
            string sigError = string.IsNullOrEmpty(envelope.Signature)
                ? "manifest has no signature"
                : "Manifest signature does not match key " + computedFp;
            return Reject(sigError);
        }

        var pin = ModManifestTrust.FindPin(trust, host ?? "", spki, computedFp);
        if (pin != null
            && ModManifestJson.TryParseIssued(pin.Issued, out var lastIssued)
            && ModManifestJson.TryParseIssued(body.Issued, out var issued)
            && issued < lastIssued)
        {
            string replay = ReplayRejectedLog(body.Issued, pin.Issued);
            return new ManifestDiff(
                signatureValid: true,
                operatorTrusted: false,
                unverifiedOperator: false,
                signatureError: null,
                Array.Empty<ManifestMod>(),
                Array.Empty<ManifestMod>(),
                Array.Empty<ManifestMismatch>(),
                Array.Empty<ManifestMod>(),
                replayed: true,
                replayError: replay);
        }

        var hostRow = ModManifestTrust.FindByHost(trust, host ?? "");
        bool fingerprintChanged = hostRow != null
            && !string.IsNullOrEmpty(hostRow.Fp)
            && !string.Equals(hostRow.Fp, computedFp, StringComparison.OrdinalIgnoreCase);
        string? previousFp = fingerprintChanged ? hostRow!.Fp : null;
        // R28 finding 25: host-aware. `pin` above and `trustedRow` here are the same decision
        // now, so a pin at one address no longer trusts the key at another and a session-only
        // pin can no longer be reported as persisted.
        bool trustedRow = ModManifestJson.IsTrusted(trust, computedFp, spki, host ?? "", out var fpOnlyLog);
        if (!string.IsNullOrEmpty(fpOnlyLog) && TryTakeFpOnlyLog(fpOnlyLog))
            Plugin.Logger?.LogWarning(fpOnlyLog);
        bool trusted = !fingerprintChanged && trustedRow;
        bool sessionTrust = trusted && pin != null && !pin.Always;
        bool unverified = !trusted && !fingerprintChanged;

        var have = new Dictionary<string, LoadedModInfo>(StringComparer.OrdinalIgnoreCase);
        if (clientMods != null)
        {
            for (int i = 0; i < clientMods.Count; i++)
            {
                var row = clientMods[i];
                if (string.IsNullOrEmpty(row.Id)) continue;
                have[row.Id] = row;
            }
        }

        var present = new List<ManifestMod>();
        var missing = new List<ManifestMod>();
        var mismatches = new List<ManifestMismatch>();
        var restart = new List<ManifestMod>();
        var hashNotChecked = new List<string>();
        var serverOnly = new List<ManifestMod>();
        var accepted = new List<ManifestAccepted>();
        // C3: the server's judgment, when the catalog carries its Version matching (null: the rows' rules, as before).
        VersionJudgeContext? judge = null;
        try { judge = VersionMatching.ClientContextFor(body, null, ownRangeOf ?? VersionMatching.OwnRangeOf); }
        catch { judge = null; }
        var mods = body.Mods ?? Array.Empty<ManifestMod>();
        for (int i = 0; i < mods.Count; i++)
        {
            var mod = mods[i];
            if (mod == null || string.IsNullOrEmpty(mod.Id)) continue;
            if (mod.ServerOnly)
            {
                // [ProgenitorSide(Side.Server)]: listed so the client knows what the server runs,
                // never offered. A client that must have a mod is kicked by the handshake, which
                // does not read this catalog.
                serverOnly.Add(mod);
                continue;
            }
            if (!have.TryGetValue(mod.Id, out var got) || string.IsNullOrEmpty(got.Version))
            {
                missing.Add(mod);
                if (!mod.HotLoadable) restart.Add(mod);
                continue;
            }
            if (judge != null)
            {
                int before = judge.Accepted.Count;
                var serverRow = new Handshake.ModRecord(mod.Id, mod.Version, mod.Level, mod.VersionRule, declared: true);
                string? refused = VersionMatchJudge.Check(serverRow, got.Version, judge);
                if (refused != null)
                {
                    string why = refused.StartsWith(mod.Id + ": ", StringComparison.OrdinalIgnoreCase) ? refused.Substring(mod.Id.Length + 2) : refused;
                    mismatches.Add(new ManifestMismatch(mod, got.Version, why: why));
                    if (!mod.HotLoadable) restart.Add(mod);
                    continue;
                }

                if (!string.Equals((mod.Version ?? "").Trim(), (got.Version ?? "").Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    // Another version the server accepts: present. The catalog's sha256 is the server's build, so it is
                    // not compared.
                    present.Add(mod);
                    accepted.Add(new ManifestAccepted(mod, got.Version ?? "", VersionMatching.AcceptedPhrase(judge, before)));
                    continue;
                }
            }
            else if (!Handshake.Compare.VersionsMatch(mod.Version, got.Version, mod.VersionRule))
            {
                mismatches.Add(new ManifestMismatch(mod, got.Version));
                if (!mod.HotLoadable) restart.Add(mod);
                continue;
            }

            string? catalogSha = ModManifestJson.NormalizeSha(mod.Sha256);
            string? loadedSha = ModManifestJson.NormalizeSha(got.Sha256);
            if (catalogSha == null)
            {
                present.Add(mod);
                hashNotChecked.Add(mod.Id);
                continue;
            }
            if (loadedSha == null)
            {
                present.Add(mod);
                hashNotChecked.Add(mod.Id);
                continue;
            }
            if (!string.Equals(catalogSha, loadedSha, StringComparison.OrdinalIgnoreCase))
            {
                mismatches.Add(new ManifestMismatch(mod, loadedSha, hashMismatch: true));
                if (!mod.HotLoadable) restart.Add(mod);
                continue;
            }
            present.Add(mod);
        }

        return new ManifestDiff(
            sigOk,
            trusted,
            unverified,
            signatureError: null,
            present,
            missing,
            mismatches,
            restart,
            fingerprintChanged,
            previousFp,
            replayed: false,
            replayError: null,
            hashNotChecked,
            sessionTrust,
            serverOnly,
            accepted);
    }

    internal static bool TryMatchEmbeddedFingerprint(
        string spki,
        string claimedFp,
        out string computedFp,
        out string? error)
    {
        computedFp = "";
        error = null;
        if (!ModManifestCrypto.TryImportSpki(spki, out _, out computedFp, out _))
        {
            error = FingerprintRejectedLog;
            return false;
        }
        if (!string.Equals(computedFp, (claimedFp ?? "").Trim(), StringComparison.OrdinalIgnoreCase))
        {
            error = FingerprintRejectedLog;
            return false;
        }
        return true;
    }

    private static ManifestDiff Reject(string error) =>
        new(
            signatureValid: false,
            operatorTrusted: false,
            unverifiedOperator: false,
            signatureError: error,
            Array.Empty<ManifestMod>(),
            Array.Empty<ManifestMod>(),
            Array.Empty<ManifestMismatch>(),
            Array.Empty<ManifestMod>());

    /// <summary>One greppable block for the client log and for <see cref="ModManifest.Describe"/>.</summary>
    public static string FormatLogBlock(ManifestEnvelope envelope, ManifestDiff diff, string? host = null)
    {
        var sb = new StringBuilder();
        var body = envelope.Body;
        var op = body.Operator;
        if (!diff.SignatureValid)
        {
            string err = diff.SignatureError ?? "signature does not match key " + op.Fp;
            if (err.StartsWith("Manifest:", StringComparison.Ordinal)
                || err.StartsWith("ModManifest:", StringComparison.Ordinal))
                sb.Append(err);
            else
                sb.Append("ModManifest: ").Append(err);
        }
        else if (diff.Replayed)
            sb.Append(diff.ReplayError ?? ReplayRejectedLog(body.Issued, ""));
        else if (diff.FingerprintChanged)
        {
            sb.Append("ModManifest: operator key changed");
            if (!string.IsNullOrEmpty(host))
                sb.Append(" for host ").Append(host);
            sb.Append(": was ").Append(diff.PreviousFingerprint ?? "")
                .Append(" now ").Append(op.Fp)
                .Append(" (not accepted)");
        }
        else if (diff.UnverifiedOperator)
        {
            sb.Append("ModManifest: unverified operator ").Append(op.Name)
                .Append(" fp=").Append(op.Fp)
                .Append(" (first contact; not in progenitor-trusted-operators.json)");
        }
        else
        {
            sb.Append("ModManifest: operator ").Append(op.Name)
                .Append(" fp=").Append(op.Fp)
                .Append(diff.SessionTrust ? " (trusted, session)" : " (trusted)");
        }
        sb.AppendLine();
        sb.Append("ModManifest: server '").Append(body.ServerName)
            .Append("' issued ").Append(body.Issued);
        sb.AppendLine();
        AppendMods(sb, "present", diff.Present, m => FormatPresent(m, diff));
        AppendMods(sb, "missing", diff.Missing, FormatMissing);
        AppendMods(sb, "mismatch", diff.Mismatches, FormatMismatch);
        AppendMods(sb, "restart-required", diff.RestartRequired, m => m.Id);
        if (diff.ServerOnly.Count > 0)
            AppendMods(sb, "server-only", diff.ServerOnly, m => m.Id + " " + m.Version);
        return sb.ToString().TrimEnd();
    }

    public static string FormatDescribe(bool isServer, ManifestEnvelope? envelope, ManifestDiff? diff)
    {
        var sb = new StringBuilder();
        if (envelope?.Body == null)
        {
            sb.Append(isServer ? "ModManifest server: no catalog" : "ModManifest client: no catalog received");
            return sb.ToString();
        }
        var body = envelope.Body;
        sb.Append(isServer ? "ModManifest server" : "ModManifest client");
        sb.Append(" fp=").Append(body.Operator.Fp);
        sb.Append(" mods=").Append(body.Mods.Count.ToString(CultureInfo.InvariantCulture));
        sb.Append(" signed=").Append(string.IsNullOrEmpty(envelope.Signature) ? "false" : "true");
        if (diff != null)
        {
            sb.Append(" trusted=").Append(diff.OperatorTrusted ? "true" : "false");
            if (diff.UnverifiedOperator) sb.Append(" unverified=true");
            if (!diff.SignatureValid) sb.Append(" signature=invalid");
        }
        sb.AppendLine();
        for (int i = 0; i < body.Mods.Count; i++)
        {
            var m = body.Mods[i];
            sb.Append("  ").Append(m.Id).Append(' ').Append(m.Version)
                .Append(' ').Append(m.Level).Append('/').Append(m.VersionRule)
                .Append(m.HotLoadable ? " hotLoadable" : " restart");
            if (m.ServerOnly) sb.Append(" serverOnly");
            if (!string.IsNullOrEmpty(m.Package)) sb.Append(" package=").Append(m.Package);
            sb.AppendLine();
        }
        if (diff != null)
        {
            sb.Append(FormatLogBlock(envelope, diff));
        }
        return sb.ToString().TrimEnd();
    }

    private static string FormatPresent(ManifestMod m, ManifestDiff diff)
    {
        var sb = new StringBuilder();
        foreach (var a in diff.Accepted)
        {
            if (!string.Equals(a.Wanted.Id, m.Id, StringComparison.OrdinalIgnoreCase)) continue;
            // C3: present ... 1.4.1 (server 1.4.0, accepted by the client's range >=1.4.0 <1.5.0)
            sb.Append(m.Id).Append(' ').Append(a.Have).Append(" (server ").Append(m.Version);
            if (a.Why.Length > 0) sb.Append(", accepted ").Append(a.Why);
            sb.Append(')');
            return sb.ToString();
        }

        sb.Append(m.Id).Append(' ').Append(m.Version);
        if (IsHashNotChecked(diff, m.Id))
            sb.Append(" (").Append(HashNotCheckedToken).Append(')');
        return sb.ToString();
    }

    private static string FormatMismatch(ManifestMismatch m)
    {
        if (m.HashMismatch)
            return m.Wanted.Id + " " + HashMismatchToken;
        return m.Wanted.Id + " have " + m.Have + " want " + m.Wanted.Version
            + " (" + (m.Why.Length > 0 ? m.Why : m.Wanted.VersionRule.ToString()) + ")";
    }

    private static bool IsHashNotChecked(ManifestDiff diff, string id)
    {
        var ids = diff.HashNotCheckedIds;
        if (ids == null || ids.Count == 0) return false;
        for (int i = 0; i < ids.Count; i++)
        {
            if (string.Equals(ids[i], id, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static string FormatMissing(ManifestMod m)
    {
        var sb = new StringBuilder();
        sb.Append(m.Id).Append(' ').Append(m.Version);
        if (!string.IsNullOrEmpty(m.Package))
            sb.Append(" (Thunderstore ").Append(m.Package).Append(')');
        else if (!string.IsNullOrEmpty(m.Url))
        {
            string host = HostOf(m.Url);
            if (host.Length > 0) sb.Append(" (url ").Append(host).Append(')');
        }
        if (m.Level == NetworkCompatibilityLevel.ClientOnly)
            sb.Append(" (ClientOnly)");
        return sb.ToString();
    }

    internal static string HostOf(string? url)
    {
        if (string.IsNullOrEmpty(url)) return "";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return "";
        return uri.Host ?? "";
    }

    private static void AppendMods<T>(StringBuilder sb, string label, IReadOnlyList<T> items, Func<T, string> format)
    {
        sb.Append("  ").Append(label).Append(" (").Append(items.Count.ToString(CultureInfo.InvariantCulture)).Append("):");
        if (items.Count == 0)
        {
            sb.AppendLine();
            return;
        }
        sb.Append(' ');
        for (int i = 0; i < items.Count; i++)
        {
            if (i > 0) sb.Append("; ");
            sb.Append(format(items[i]));
        }
        sb.AppendLine();
    }
}
