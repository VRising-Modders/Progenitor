using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Progenitor.Plugins;

namespace Progenitor.Networking;

/// <summary>
/// One loaded plugin as the catalog sees it, before operator overrides (Thunderstore
/// package, hash, URL) are merged. Tests and the live enumerator both produce this
/// so <see cref="ModManifestJson.BuildBody"/> does not touch BepInEx.
/// </summary>
public readonly struct LoadedModInfo
{
    public LoadedModInfo(
        string id,
        string version,
        NetworkCompatibilityLevel level,
        NetworkVersionRule versionRule,
        bool hotLoadable,
        string? sha256 = null,
        bool serverOnly = false)
    {
        Id = id ?? "";
        Version = version ?? "";
        Level = level;
        VersionRule = versionRule;
        HotLoadable = hotLoadable;
        Sha256 = ModManifestJson.NormalizeSha(sha256);
        ServerOnly = serverOnly;
    }

    public string Id { get; }
    public string Version { get; }
    public NetworkCompatibilityLevel Level { get; }
    public NetworkVersionRule VersionRule { get; }
    public bool HotLoadable { get; }

    /// <summary>
    /// SHA-256 of the loaded assembly bytes (reload-folder file or the reloader's copy),
    /// 64 lowercase hex, or null when this process has not hashed that file.
    /// </summary>
    public string? Sha256 { get; }

    /// <summary>
    /// The plugin type declares <c>[ProgenitorSide(Side.Server)]</c>: it runs only in the server
    /// process, so a client copy would stay dormant. See <see cref="ManifestMod.ServerOnly"/>.
    /// </summary>
    public bool ServerOnly { get; }
}

/// <summary>
/// Operator-supplied fields for one plugin id, from
/// <c>BepInEx/config/progenitor-manifest.json</c>. Missing properties stay null
/// so an auto-built row is left alone.
/// </summary>
public sealed class ManifestModOverride
{
    public string Id { get; set; } = "";
    public string? Version { get; set; }
    public string? Namespace { get; set; }
    public string? Package { get; set; }
    public string? Sha256 { get; set; }
    public long? Size { get; set; }
    public string? Url { get; set; }
    public bool? HotLoadable { get; set; }
    public NetworkCompatibilityLevel? Level { get; set; }
    public NetworkVersionRule? VersionRule { get; set; }
}

/// <summary>
/// Whole operator overlay: server display name plus per-id Thunderstore / hash / URL
/// rows. Extra ids that are not loaded still enter the catalog so a client-only
/// package can be required without the dedicated process loading it.
/// </summary>
public sealed class ManifestOverrides
{
    public string? ServerName { get; set; }
    public string? OperatorName { get; set; }
    public string? ProfileCode { get; set; }
    public IReadOnlyList<ManifestModOverride> Mods { get; set; } = Array.Empty<ManifestModOverride>();

    /// <summary>
    /// JOINFLOW1 stage 1: the mods this server recommends (<c>recommended</c>: <c>{id, package, version,
    /// reason}</c>). They travel in the signed join section, never as catalog rows.
    /// </summary>
    public IReadOnlyList<RecommendedMod> Recommended { get; set; } = Array.Empty<RecommendedMod>();
}

/// <summary>
/// One row of <c>BepInEx/config/progenitor-trusted-operators.json</c>. BCL strings
/// and bools only so a collectible Core generation cannot pin the file.
/// </summary>
public sealed class TrustedOperator
{
    public string Host { get; set; } = "";
    public string Spki { get; set; } = "";
    public string Fp { get; set; } = "";
    public bool Always { get; set; }
    public string Seen { get; set; } = "";

    /// <summary>
    /// Last accepted catalog <c>issued</c> for this host+SPKI. Empty until a pin exists and
    /// a signed envelope has been stored. An older envelope is a replay.
    /// </summary>
    public string Issued { get; set; } = "";

    /// <summary>
    /// Retired (2026-10-05): when an older Core's player ticked "I trust this server's admins and accept the risk of
    /// running mods they send" for this host and key. Servers no longer send mod files, so nothing reads or sets it any
    /// more. A file that has it still parses, and a rewrite carries it through unchanged, so an older Core sharing the
    /// file keeps its answer.
    /// </summary>
    public string RiskAck { get; set; } = "";
}

/// <summary>Trusted-operator list. An empty file is first contact for every fingerprint.</summary>
public sealed class TrustedOperatorsFile
{
    public IReadOnlyList<TrustedOperator> Operators { get; set; } = Array.Empty<TrustedOperator>();

    /// <summary>
    /// JOINFLOW2: the player's "install these Thunderstore packages this server lists" confirmations, one row
    /// per host and key. It needs no pin. Written only when non-empty, so a file without it is byte-identical
    /// to an older Core's; an older Core rewriting the file drops it and the next batch asks again.
    /// </summary>
    internal IReadOnlyList<ThunderstoreAck> ThunderstoreAcks { get; set; } = Array.Empty<ThunderstoreAck>();
}

/// <summary>
/// JOINFLOW2: one confirmed Thunderstore batch for a server (host and key): when, and which
/// <c>Owner-Name@version</c> packages it covered. BCL strings only.
/// </summary>
internal sealed class ThunderstoreAck
{
    public string Host { get; set; } = "";
    public string Spki { get; set; } = "";
    public string Fp { get; set; } = "";

    /// <summary>When the player confirmed (<c>yyyy-MM-ddTHH:mm:ssZ</c>).</summary>
    public string At { get; set; } = "";

    /// <summary><c>Owner-Name@1.2.0</c>, sorted, at most <c>JoinThunderstoreTrust.MaxPackagesPerAck</c>.</summary>
    public IReadOnlyList<string> Packages { get; set; } = Array.Empty<string>();
}

/// <summary>Operator identity carried in the signed body (SPKI + short fingerprint).</summary>
public sealed class ManifestOperator
{
    public ManifestOperator(string name, string spki, string fp)
    {
        Name = name ?? "";
        Spki = spki ?? "";
        Fp = fp ?? "";
    }

    public string Name { get; }
    public string Spki { get; }
    public string Fp { get; }
}

/// <summary>
/// One catalog row. <see cref="Sha256"/> / <see cref="Size"/> / <see cref="Url"/> are
/// optional in phase 1 (nothing is downloaded); they become required for a direct
/// URL fetch in a later phase.
/// </summary>
public sealed class ManifestMod
{
    public ManifestMod(
        string id,
        string version,
        NetworkCompatibilityLevel level,
        NetworkVersionRule versionRule,
        bool hotLoadable,
        string? ns = null,
        string? package = null,
        string? sha256 = null,
        long? size = null,
        string? url = null,
        bool serverOnly = false)
    {
        Id = id ?? "";
        Version = version ?? "";
        Level = level;
        VersionRule = versionRule;
        HotLoadable = hotLoadable;
        Namespace = EmptyToNull(ns);
        Package = EmptyToNull(package);
        Sha256 = EmptyToNull(sha256);
        Size = size is > 0 ? size : null;
        Url = EmptyToNull(url);
        ServerOnly = serverOnly;
    }

    public string Id { get; }
    public string Version { get; }
    public string? Namespace { get; }
    public string? Package { get; }
    public string? Sha256 { get; }
    public long? Size { get; }
    public NetworkCompatibilityLevel Level { get; }
    public NetworkVersionRule VersionRule { get; }
    public bool HotLoadable { get; }
    public string? Url { get; }

    /// <summary>
    /// The server's plugin declares <c>[ProgenitorSide(Side.Server)]</c>. The row stays in the
    /// catalog so the client can list what the server runs, but it is never a client install:
    /// <see cref="ModManifestCompare.Classify"/> reports it as server-only instead of missing or
    /// mismatched, so it never becomes a consent row. Whether a client must have a mod is the
    /// handshake's decision, not this flag's. Written to the signed body only when true.
    /// </summary>
    public bool ServerOnly { get; }

    /// <summary>
    /// Mod compatibility C3: the server build's effective peer range for this mod (its declared <c>Peers</c>, else its
    /// explicit rule, else the same minor version). Only in the catalog form signed for clients that declared
    /// <c>Handshake.Capability.VersionRanges</c>; null in the form older clients get, and from an older server.
    /// </summary>
    public string? Peers { get; init; }

    /// <summary>
    /// Mod compatibility C3: when the server does not judge this mod by ranges, how it does: <c>exact</c>, <c>minor</c>,
    /// <c>at-least</c> or <c>any</c> (an admin's per-mod rule, else Exact or Same minor for all). Null under declared
    /// ranges. Extended form only, like <see cref="Peers"/>.
    /// </summary>
    public string? Match { get; init; }

    internal static string? EmptyToNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>Signed-body contents. Canonical JSON of this object (not the envelope) is what is signed.</summary>
public sealed class ManifestBody
{
    public ManifestBody(
        int schema,
        string serverName,
        string issued,
        ManifestOperator op,
        string? profileCode,
        IReadOnlyList<ManifestMod> mods)
    {
        Schema = schema;
        ServerName = serverName ?? "";
        Issued = issued ?? "";
        Operator = op ?? new ManifestOperator("", "", "");
        ProfileCode = ManifestMod.EmptyToNull(profileCode);
        Mods = mods ?? Array.Empty<ManifestMod>();
    }

    public int Schema { get; }
    public string ServerName { get; }
    public string Issued { get; }
    public ManifestOperator Operator { get; }
    public string? ProfileCode { get; }
    public IReadOnlyList<ManifestMod> Mods { get; }

    /// <summary>
    /// Mod compatibility C3: the server's <b>Version matching</b> when this form was signed (<c>range</c>, <c>minor</c> or
    /// <c>exact</c>). Extended form only, like <see cref="ManifestMod.Peers"/>; null in the form older clients get and from
    /// an older server. A re-signed catalog carries the mode the server has now, so a connected client learns a change
    /// that its handshake request's bits (sent at the join) cannot tell it.
    /// </summary>
    public string? Matching { get; init; }

    /// <summary>
    /// Mod compatibility C4: the Progenitor release a server under Exact version for all requires of every client (its own
    /// Core's release), with <see cref="ProgenitorSha"/>. Only in the catalog form signed for clients that declared
    /// <c>Handshake.Capability.ProgenitorExact</c>, and only while Exact is enforced for Progenitor; null otherwise.
    /// </summary>
    public string? Progenitor { get; init; }

    /// <summary>C4: the sha256 of the server's loaded <c>Progenitor.dll</c>; written with <see cref="Progenitor"/>.</summary>
    public string? ProgenitorSha { get; init; }
}

/// <summary>On-disk / on-wire envelope: canonical body plus IEEE P1363 signature hex.</summary>
public sealed class ManifestEnvelope
{
    public ManifestEnvelope(ManifestBody body, string signature)
    {
        Body = body ?? throw new ArgumentNullException(nameof(body));
        Signature = signature ?? "";
    }

    /// <summary>
    /// JOINFLOW1: the same envelope carrying the separately signed join section. Older clients read only
    /// <c>body</c> and <c>signature</c>, so the extra keys cost them nothing; see <see cref="JoinSection"/>.
    /// </summary>
    public ManifestEnvelope(ManifestBody body, string signature, JoinSection? join, string? joinSignature)
        : this(body, signature)
    {
        Join = join;
        JoinSignature = join == null ? "" : joinSignature ?? "";
    }

    public ManifestBody Body { get; }
    public string Signature { get; }

    /// <summary>JOINFLOW1: the join section as received or built, not yet verified; null when absent.</summary>
    public JoinSection? Join { get; }

    /// <summary>JOINFLOW1: IEEE P1363 hex over <c>progenitor.join.v1\n</c> + the canonical section.</summary>
    public string JoinSignature { get; } = "";

    /// <summary>JOINFLOW1: why a <c>join</c> key that was present could not be read; null otherwise.</summary>
    public string? JoinError { get; internal set; }

    /// <summary>The same envelope with another body and signature, keeping the join section (the deny narrowing).</summary>
    internal ManifestEnvelope WithBody(ManifestBody body, string signature) => new(body, signature, Join, JoinSignature);
}

/// <summary>
/// Canonical JSON for the signed catalog. Keys are sorted, whitespace is stripped, UTF-8
/// is the encoding on the wire; the string returned here is the UTF-16 form of that
/// UTF-8 JSON so ECDSA signs <c>Encoding.UTF8.GetBytes(canonical)</c>. System.Text.Json
/// is used only to parse operator files (comments, trailing commas); it is never used to
/// emit the signed bytes, because its key order is not a contract.
/// </summary>
internal static class ModManifestJson
{
    public const int SchemaVersion = 1;

    /// <summary>The catalog's server name when the operator set none and the server knows no name of its own.</summary>
    internal const string DefaultServerName = "V Rising";

    internal static readonly JsonDocumentOptions ParseOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    public static string FormatIssued(DateTime utc) =>
        utc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    /// <summary>
    /// Parse a catalog <c>issued</c> stamp. Accepts the canonical <c>yyyy-MM-ddTHH:mm:ssZ</c>
    /// form and a round-trip ISO-8601 fallback. Fail closed: unparseable text is not a stamp.
    /// </summary>
    public static bool TryParseIssued(string? text, out DateTime utc)
    {
        utc = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        text = text.Trim();
        if (DateTime.TryParseExact(
                text,
                "yyyy-MM-ddTHH:mm:ssZ",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out utc))
            return true;
        if (DateTime.TryParse(
                text,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out utc))
        {
            utc = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Merge loaded plugins with operator overrides into a body ready to sign.
    /// Override-only ids (not currently loaded) are kept so a client-only package can
    /// be listed without the server process loading that DLL.
    /// </summary>
    public static ManifestBody BuildBody(
        IReadOnlyList<LoadedModInfo> loaded,
        ManifestOverrides? overrides,
        ManifestOperator op,
        DateTime issuedUtc,
        int schema = SchemaVersion)
    {
        overrides ??= new ManifestOverrides();
        var byId = new Dictionary<string, ManifestMod>(StringComparer.OrdinalIgnoreCase);
        if (loaded != null)
        {
            for (int i = 0; i < loaded.Count; i++)
            {
                var row = loaded[i];
                if (string.IsNullOrWhiteSpace(row.Id)) continue;
                if (Handshake.Compare.IsReservedGuid(row.Id)) continue;
                if (IsFrameworkGuid(row.Id)) continue;
                if (!ModManifestIds.TryValidateId(row.Id, out _)) continue;
                byId[row.Id] = new ManifestMod(
                    row.Id,
                    row.Version,
                    row.Level,
                    row.VersionRule,
                    row.HotLoadable,
                    serverOnly: row.ServerOnly);
            }
        }

        var overlay = overrides.Mods ?? Array.Empty<ManifestModOverride>();
        for (int i = 0; i < overlay.Count; i++)
        {
            var o = overlay[i];
            if (o == null || string.IsNullOrWhiteSpace(o.Id)) continue;
            if (Handshake.Compare.IsReservedGuid(o.Id)) continue;
            if (IsFrameworkGuid(o.Id)) continue;
            if (!ModManifestIds.TryValidateId(o.Id, out _)) continue;
            if (byId.TryGetValue(o.Id, out var existing))
                byId[o.Id] = ApplyOverride(existing, o);
            else
            {
                // Job FMAN13 (R44 finding 9). An override-only row is one nothing on this server
                // loaded — most often the row ModManifest.MergeOffers synthesizes from
                // ModManifest.Offer(id, path), which carries an id, a sha256 and a size and says
                // nothing about compatibility. Defaulting that to ServerMustHave invented a
                // join-blocking requirement out of an absent field: the handshake never enforced
                // it (only Handshake.Require does), but the client's consent panel read the level
                // and told the player they would be disconnected over a join that had already
                // succeeded. An operator who has not stated a requirement has not made one.
                byId[o.Id] = ApplyOverride(
                    new ManifestMod(
                        o.Id,
                        o.Version ?? "",
                        o.Level ?? NetworkCompatibilityLevel.NoNeedForSync,
                        o.VersionRule ?? NetworkVersionRule.Exact,
                        o.HotLoadable ?? false,
                        o.Namespace,
                        o.Package,
                        o.Sha256,
                        o.Size,
                        o.Url),
                    o);
            }
        }

        var mods = new List<ManifestMod>(byId.Values);
        mods.Sort(CompareMods);
        string serverName = string.IsNullOrWhiteSpace(overrides.ServerName)
            ? DefaultServerName
            : overrides.ServerName.Trim();
        string opName = string.IsNullOrWhiteSpace(overrides.OperatorName)
            ? (string.IsNullOrWhiteSpace(op.Name) ? "operator" : op.Name)
            : overrides.OperatorName.Trim();
        var named = new ManifestOperator(opName, op.Spki, op.Fp);
        return new ManifestBody(
            schema,
            serverName,
            FormatIssued(issuedUtc),
            named,
            overrides.ProfileCode,
            mods);
    }

    /// <summary>
    /// Bootstrap/Core and the ephemeral exec host are framework infrastructure, not client installs.
    /// Match exact ids: ordinary consumer plugins may use either id as a prefix.
    /// </summary>
    public static bool IsFrameworkGuid(string? guid)
    {
        // An older peer still names them with the id form from before the 2026-10 rename (LegacyIds).
        string g = Progenitor.Plugins.LegacyIds.Normalize(guid);
        return string.Equals(g, "progenitor", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(g, "progenitor.scratch", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Full name of <c>Progenitor.Plugins.ProgenitorSideAttribute</c>, matched by name.</summary>
    internal const string ProgenitorSideAttributeName = "Progenitor.Plugins.ProgenitorSideAttribute";

    /// <summary>
    /// True when <paramref name="pluginType"/> (or a base type, since the attribute is inherited)
    /// declares <c>[ProgenitorSide(Side.Server)]</c>. Matched by attribute name and raw value
    /// rather than by <c>GetCustomAttribute&lt;ProgenitorSideAttribute&gt;</c>, so a plugin bound
    /// to an earlier Core generation's attribute type is still read. No attribute means
    /// <c>Side.Both</c>, which is not server-only.
    /// </summary>
    internal static bool IsServerOnlyType(Type? pluginType)
    {
        for (var t = pluginType; t != null; t = t.BaseType)
        {
            IList<System.Reflection.CustomAttributeData> attrs;
            try { attrs = t.GetCustomAttributesData(); }
            catch { return false; }
            for (int i = 0; i < attrs.Count; i++)
            {
                var a = attrs[i];
                if (!string.Equals(a.AttributeType.FullName, ProgenitorSideAttributeName, StringComparison.Ordinal))
                    continue;
                if (a.ConstructorArguments.Count != 1) return false;
                var value = a.ConstructorArguments[0].Value;
                // Side.Server == 1. Any other value (Client, Both, unknown) is installable.
                return value != null && Convert.ToInt32(value, CultureInfo.InvariantCulture) == 1;
            }
        }
        return false;
    }

    public static string CanonicalBody(ManifestBody body)
    {
        var sb = new StringBuilder(512);
        WriteBody(sb, body ?? new ManifestBody(SchemaVersion, "", "", new ManifestOperator("", "", ""), null, Array.Empty<ManifestMod>()));
        return sb.ToString();
    }

    public static string CanonicalEnvelope(ManifestEnvelope envelope)
    {
        if (envelope == null) throw new ArgumentNullException(nameof(envelope));
        var sb = new StringBuilder(512);
        sb.Append('{');
        WriteKey(sb, "body", first: true);
        WriteBody(sb, envelope.Body);
        // JOINFLOW1: written only when present, so an envelope without a join section is byte-identical
        // to one an older Core built. Keys stay sorted: body, join, joinSignature, signature.
        if (envelope.Join != null)
        {
            WriteKey(sb, "join", first: false);
            sb.Append(JoinSectionJson.Canonical(envelope.Join));
            WriteKey(sb, "joinSignature", first: false);
            WriteString(sb, envelope.JoinSignature ?? "");
        }
        WriteKey(sb, "signature", first: false);
        WriteString(sb, envelope.Signature ?? "");
        sb.Append('}');
        return sb.ToString();
    }

    public static bool TryParseEnvelope(string json, out ManifestEnvelope envelope, out string? error)
    {
        envelope = null!;
        error = null;
        if (!TryReadDocument(json, out var doc, out error)) return false;
        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = "envelope is not a JSON object";
                return false;
            }
            if (!doc.RootElement.TryGetProperty("body", out var bodyEl)
                || bodyEl.ValueKind != JsonValueKind.Object)
            {
                error = "envelope is missing body";
                return false;
            }
            if (!TryReadBody(bodyEl, out var body, out error)) return false;
            string signature = "";
            if (doc.RootElement.TryGetProperty("signature", out var sigEl)
                && sigEl.ValueKind == JsonValueKind.String)
                signature = sigEl.GetString() ?? "";
            // JOINFLOW1: an optional, separately signed section. A section this client cannot read is
            // dropped with the reason and never fails the catalog itself.
            JoinSection? join = null;
            string joinSignature = "";
            string? joinError = null;
            if (doc.RootElement.TryGetProperty("join", out var joinEl) && joinEl.ValueKind != JsonValueKind.Null)
            {
                if (JoinSectionJson.TryRead(joinEl, out var parsed, out joinError)) join = parsed;
                if (doc.RootElement.TryGetProperty("joinSignature", out var jsEl) && jsEl.ValueKind == JsonValueKind.String)
                    joinSignature = jsEl.GetString() ?? "";
            }
            envelope = new ManifestEnvelope(body, signature, join, joinSignature) { JoinError = joinError };
            return true;
        }
    }

    public static bool TryParseBody(string json, out ManifestBody body, out string? error)
    {
        body = null!;
        if (!TryReadDocument(json, out var doc, out error)) return false;
        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = "body is not a JSON object";
                return false;
            }
            return TryReadBody(doc.RootElement, out body, out error);
        }
    }

    public static bool TryParseOverrides(string json, out ManifestOverrides overrides, out string? error)
    {
        overrides = new ManifestOverrides();
        error = null;
        if (string.IsNullOrWhiteSpace(json)) return true;
        if (!TryReadDocument(json, out var doc, out error)) return false;
        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = "progenitor-manifest.json must be a JSON object";
                return false;
            }
            overrides.ServerName = ReadString(doc.RootElement, "serverName");
            overrides.OperatorName = ReadString(doc.RootElement, "operatorName")
                ?? ReadString(doc.RootElement, "operator");
            overrides.ProfileCode = ReadString(doc.RootElement, "profileCode");
            var mods = new List<ManifestModOverride>();
            if (doc.RootElement.TryGetProperty("mods", out var modsEl))
            {
                if (modsEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var el in modsEl.EnumerateArray())
                    {
                        if (el.ValueKind != JsonValueKind.Object) continue;
                        var row = ReadOverride(el, idHint: null);
                        if (row != null) mods.Add(row);
                    }
                }
                else if (modsEl.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in modsEl.EnumerateObject())
                    {
                        if (prop.Value.ValueKind != JsonValueKind.Object) continue;
                        var row = ReadOverride(prop.Value, prop.Name);
                        if (row != null) mods.Add(row);
                    }
                }
            }
            overrides.Mods = mods;
            // JOINFLOW1: recommended mods, read for the join section only (they never enter Mods).
            var recommended = new List<RecommendedMod>();
            if (doc.RootElement.TryGetProperty("recommended", out var recEl) && recEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in recEl.EnumerateArray())
                {
                    if (el.ValueKind != JsonValueKind.Object) continue;
                    recommended.Add(new RecommendedMod(
                        ReadString(el, "id"), ReadString(el, "package"), ReadString(el, "version"), ReadString(el, "reason")));
                }
            }
            overrides.Recommended = recommended;
            return true;
        }
    }

    public static bool TryParseTrust(string json, out TrustedOperatorsFile file, out string? error)
    {
        file = new TrustedOperatorsFile();
        error = null;
        if (string.IsNullOrWhiteSpace(json)) return true;
        if (!TryReadDocument(json, out var doc, out error)) return false;
        using (doc)
        {
            var list = new List<TrustedOperator>();
            JsonElement root = doc.RootElement;
            JsonElement array = default;
            bool haveArray = false;
            if (root.ValueKind == JsonValueKind.Array)
            {
                array = root;
                haveArray = true;
            }
            else if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("operators", out var ops)
                && ops.ValueKind == JsonValueKind.Array)
            {
                array = ops;
                haveArray = true;
            }
            else if (root.ValueKind != JsonValueKind.Object)
            {
                error = "progenitor-trusted-operators.json must be an object or array";
                return false;
            }

            // JOINFLOW2: the Thunderstore batch confirmations (TrustedOperatorsFile.ThunderstoreAcks).
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("thunderstoreAcks", out var acks)
                && acks.ValueKind == JsonValueKind.Array)
            {
                var ackList = new List<ThunderstoreAck>();
                foreach (var el in acks.EnumerateArray())
                {
                    if (el.ValueKind != JsonValueKind.Object) continue;
                    var packages = new List<string>();
                    if (el.TryGetProperty("packages", out var pk) && pk.ValueKind == JsonValueKind.Array)
                        foreach (var p in pk.EnumerateArray())
                            if (p.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(p.GetString()))
                                packages.Add(p.GetString()!.Trim());
                    var ack = new ThunderstoreAck
                    {
                        Host = ReadString(el, "host") ?? "",
                        Spki = ReadString(el, "spki") ?? "",
                        Fp = (ReadString(el, "fp") ?? "").ToLowerInvariant(),
                        At = ReadString(el, "at") ?? "",
                        Packages = packages,
                    };
                    if (ack.Spki.Length == 0 || ack.At.Length == 0) continue;
                    ackList.Add(ack);
                }

                file.ThunderstoreAcks = ackList;
            }

            if (haveArray)
            {
                foreach (var el in array.EnumerateArray())
                {
                    if (el.ValueKind != JsonValueKind.Object) continue;
                    var row = new TrustedOperator
                    {
                        Host = ReadString(el, "host") ?? "",
                        Spki = ReadString(el, "spki") ?? "",
                        Fp = (ReadString(el, "fp") ?? "").ToLowerInvariant(),
                        Always = ReadBool(el, "always"),
                        Seen = ReadString(el, "seen") ?? "",
                        Issued = ReadString(el, "issued") ?? "",
                        RiskAck = ReadString(el, "riskAck") ?? "",
                    };
                    // Session pins (always:false) never load from disk; a new process is first contact.
                    if (!row.Always) continue;
                    list.Add(row);
                }
            }
            file.Operators = list;
            return true;
        }
    }

    /// <summary>Canonical JSON of the trust file (keys sorted, no insignificant whitespace).</summary>
    public static string CanonicalTrust(TrustedOperatorsFile? file)
    {
        var rows = new List<TrustedOperator>();
        if (file?.Operators != null)
        {
            for (int i = 0; i < file.Operators.Count; i++)
            {
                var row = file.Operators[i];
                if (row == null || !row.Always) continue;
                rows.Add(row);
            }
        }
        rows.Sort((a, b) =>
        {
            int c = string.Compare(a.Fp, b.Fp, StringComparison.OrdinalIgnoreCase);
            return c != 0 ? c : string.Compare(a.Host, b.Host, StringComparison.OrdinalIgnoreCase);
        });
        var sb = new StringBuilder();
        sb.Append('{');
        WriteKey(sb, "operators", first: true);
        sb.Append('[');
        for (int i = 0; i < rows.Count; i++)
        {
            if (i > 0) sb.Append(',');
            var row = rows[i];
            sb.Append('{');
            WriteKey(sb, "always", first: true);
            sb.Append(row.Always ? "true" : "false");
            WriteKey(sb, "fp", first: false);
            WriteString(sb, (row.Fp ?? "").ToLowerInvariant());
            WriteKey(sb, "host", first: false);
            WriteString(sb, row.Host ?? "");
            WriteKey(sb, "issued", first: false);
            WriteString(sb, row.Issued ?? "");
            // JOINFLOW1: only when set (see TrustedOperator.RiskAck).
            if (!string.IsNullOrEmpty(row.RiskAck))
            {
                WriteKey(sb, "riskAck", first: false);
                WriteString(sb, row.RiskAck);
            }
            WriteKey(sb, "seen", first: false);
            WriteString(sb, row.Seen ?? "");
            WriteKey(sb, "spki", first: false);
            WriteString(sb, row.Spki ?? "");
            sb.Append('}');
        }
        sb.Append(']');
        // JOINFLOW2: only when there is one (see TrustedOperatorsFile.ThunderstoreAcks).
        var acks = new List<ThunderstoreAck>();
        if (file?.ThunderstoreAcks != null)
            foreach (var a in file.ThunderstoreAcks)
                if (a != null && !string.IsNullOrEmpty(a.Spki) && !string.IsNullOrEmpty(a.At)) acks.Add(a);
        if (acks.Count > 0)
        {
            acks.Sort((a, b) =>
            {
                int c = string.Compare(a.Fp, b.Fp, StringComparison.OrdinalIgnoreCase);
                return c != 0 ? c : string.Compare(a.Host, b.Host, StringComparison.OrdinalIgnoreCase);
            });
            WriteKey(sb, "thunderstoreAcks", first: false);
            sb.Append('[');
            for (int i = 0; i < acks.Count; i++)
            {
                if (i > 0) sb.Append(',');
                var a = acks[i];
                sb.Append('{');
                WriteKey(sb, "at", first: true);
                WriteString(sb, a.At ?? "");
                WriteKey(sb, "fp", first: false);
                WriteString(sb, (a.Fp ?? "").ToLowerInvariant());
                WriteKey(sb, "host", first: false);
                WriteString(sb, a.Host ?? "");
                WriteKey(sb, "packages", first: false);
                sb.Append('[');
                var packages = new List<string>(a.Packages ?? Array.Empty<string>());
                packages.Sort(StringComparer.OrdinalIgnoreCase);
                for (int j = 0; j < packages.Count; j++)
                {
                    if (j > 0) sb.Append(',');
                    WriteString(sb, packages[j] ?? "");
                }

                sb.Append(']');
                WriteKey(sb, "spki", first: false);
                WriteString(sb, a.Spki ?? "");
                sb.Append('}');
            }

            sb.Append(']');
        }

        sb.Append('}');
        return sb.ToString();
    }

    /// <summary>
    /// True when a trust row has the computed fingerprint, the exact SPKI, <b>and is pinned for
    /// this host</b>. <see cref="TrustedOperator.Always"/> is honored: <c>always:true</c> is a
    /// persisted pin; <c>always:false</c> is session-only (trusted until this process drops the
    /// in-memory file). Disk load never yields session rows (<see cref="TryParseTrust"/> /
    /// <see cref="CanonicalTrust"/>). A row without <c>spki</c> is never matched on fingerprint
    /// alone (fail closed). <paramref name="fp"/> must already be
    /// <see cref="ModManifestCrypto.Fingerprint"/> of <paramref name="spki"/>; a mismatch is not
    /// trusted.
    /// <para>
    /// <b>R28 finding 25.</b> This used to ignore the host entirely, so a pin made at
    /// <c>10.0.0.1</c> trusted the same key at <c>10.0.0.2</c> — for the process when the pin was
    /// session-only, and forever when it was persisted — while <see cref="ModManifestTrust.FindPin"/>
    /// (used in the same method to label the pin session-vs-persisted) did implement host
    /// preference. Two answers for one question. <c>FindPin</c> is the single source now: a row
    /// pinned for a specific host matches only that host, and a <b>host-less</b> row matches
    /// everywhere. A host-less row is only ever written deliberately —
    /// <see cref="ModManifestTrust.Upsert"/> keeps a host-less write host-less, which is how an
    /// operator records "trust this key wherever it answers", and only an <c>always</c> row
    /// survives to disk. Passing no host therefore means "host-less pins only", the fail-safe
    /// reading.
    /// </para>
    /// </summary>
    public static bool IsTrusted(TrustedOperatorsFile? file, string fp, string? spki)
    {
        return IsTrusted(file, fp, spki, "", out _);
    }

    /// <summary>Host-less overload: only a pin recorded without a host matches.</summary>
    public static bool IsTrusted(TrustedOperatorsFile? file, string fp, string? spki, out string? refuseLog)
    {
        return IsTrusted(file, fp, spki, "", out refuseLog);
    }

    public static bool IsTrusted(
        TrustedOperatorsFile? file,
        string fp,
        string? spki,
        string? host,
        out string? refuseLog)
    {
        refuseLog = null;
        if (file?.Operators == null || string.IsNullOrEmpty(fp) || string.IsNullOrEmpty(spki))
            return false;
        string wantFp = fp.Trim().ToLowerInvariant();

        // Always is the persist flag, not a deny: a session row is trusted in this process.
        if (ModManifestTrust.FindPin(file, host ?? "", spki, wantFp) != null)
            return true;

        for (int i = 0; i < file.Operators.Count; i++)
        {
            var row = file.Operators[i];
            if (row == null || !string.IsNullOrEmpty(row.Spki)) continue;
            if (!string.Equals(row.Fp, wantFp, StringComparison.OrdinalIgnoreCase)) continue;
            refuseLog = ModManifestCompare.FpOnlyTrustRowLog(wantFp);
            break;
        }
        return false;
    }

    // R27 finding 23: IsSessionTrust lived here with no production caller while
    // ModManifestCompare.Classify computed the same flag through FindPin's host-preference
    // rules; two answers for one question. FindPin is the single source now.

    /// <summary>
    /// AUTOUPDATE3: a loaded mod Progenitor installed on this server from Thunderstore (an install-ledger row whose source is
    /// <c>package:Owner-Name@version</c>, keyed by its plugin id) is named by that package in the catalog, so a player whose copy
    /// is missing or older after a server install or update can install it from the join screens in place. The operator's own
    /// row in <c>progenitor-manifest.json</c> wins: an id the overlay already names a package for is left as it is. Pure.
    /// </summary>
    public static ManifestOverrides WithLedgerPackages(ManifestOverrides? overrides, IReadOnlyList<LoadedModInfo>? loaded,
        IEnumerable<(string PluginId, string Package)>? ledger)
    {
        overrides ??= new ManifestOverrides();
        if (loaded == null || ledger == null) return overrides;
        var loadedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var l in loaded) if (!string.IsNullOrWhiteSpace(l.Id)) loadedIds.Add(l.Id);
        var named = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var o in overrides.Mods ?? Array.Empty<ManifestModOverride>())
            if (o != null && !string.IsNullOrWhiteSpace(o.Package)) named.Add(o.Id);
        var added = new List<ManifestModOverride>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (pluginId, package) in ledger)
        {
            if (string.IsNullOrWhiteSpace(pluginId) || string.IsNullOrWhiteSpace(package)) continue;
            if (!loadedIds.Contains(pluginId) || named.Contains(pluginId) || !seen.Add(pluginId)) continue;
            int dash = package.IndexOf('-');
            if (dash <= 0 || dash == package.Length - 1) continue;
            added.Add(new ManifestModOverride { Id = pluginId, Namespace = package.Substring(0, dash), Package = package.Trim() });
        }

        if (added.Count == 0) return overrides;
        var mods = new List<ManifestModOverride>(overrides.Mods ?? Array.Empty<ManifestModOverride>());
        foreach (var a in added)
        {
            int at = mods.FindIndex(m => m != null && string.Equals(m.Id, a.Id, StringComparison.OrdinalIgnoreCase));
            if (at < 0) mods.Add(a);
            else
            {
                var m = mods[at];
                mods[at] = new ManifestModOverride
                {
                    Id = m.Id, Version = m.Version, Namespace = m.Namespace ?? a.Namespace, Package = a.Package, Sha256 = m.Sha256, Size = m.Size,
                    Url = m.Url, HotLoadable = m.HotLoadable, Level = m.Level, VersionRule = m.VersionRule,
                };
            }
        }

        return new ManifestOverrides
        {
            ServerName = overrides.ServerName, OperatorName = overrides.OperatorName, ProfileCode = overrides.ProfileCode, Mods = mods,
            Recommended = overrides.Recommended,
        };
    }

    private static ManifestMod ApplyOverride(ManifestMod existing, ManifestModOverride o)
    {
        string version = string.IsNullOrWhiteSpace(o.Version) ? existing.Version : o.Version.Trim();
        return new ManifestMod(
            existing.Id,
            version,
            o.Level ?? existing.Level,
            o.VersionRule ?? existing.VersionRule,
            o.HotLoadable ?? existing.HotLoadable,
            o.Namespace ?? existing.Namespace,
            o.Package ?? existing.Package,
            NormalizeSha(o.Sha256) ?? existing.Sha256,
            o.Size ?? existing.Size,
            o.Url ?? existing.Url,
            existing.ServerOnly);
    }

    /// <summary>
    /// 64 lowercase hex or null. Non-hex, the wrong length, and empty values are rejected
    /// so a truncated overlay cannot be compared as a real SHA-256.
    /// </summary>
    internal static string? NormalizeSha(string? sha)
    {
        if (string.IsNullOrWhiteSpace(sha)) return null;
        sha = sha.Trim().ToLowerInvariant();
        if (sha.StartsWith("0x", StringComparison.Ordinal)) sha = sha.Substring(2);
        if (sha.Length != 64) return null;
        for (int i = 0; i < sha.Length; i++)
        {
            char c = sha[i];
            bool hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f');
            if (!hex) return null;
        }
        return sha;
    }

    private static int CompareMods(ManifestMod a, ManifestMod b)
    {
        int c = string.Compare(a.Id, b.Id, StringComparison.OrdinalIgnoreCase);
        return c != 0 ? c : string.Compare(a.Id, b.Id, StringComparison.Ordinal);
    }

    private static void WriteBody(StringBuilder sb, ManifestBody body)
    {
        sb.Append('{');
        WriteKey(sb, "issued", first: true);
        WriteString(sb, body.Issued);
        // C3, the extended form: written only when set, so the current form stays byte-identical to an older Core's.
        if (body.Matching != null)
        {
            WriteKey(sb, "matching", first: false);
            WriteString(sb, body.Matching);
        }
        WriteKey(sb, "mods", first: false);
        sb.Append('[');
        var mods = SortedMods(body.Mods);
        for (int i = 0; i < mods.Count; i++)
        {
            if (i > 0) sb.Append(',');
            WriteMod(sb, mods[i]);
        }
        sb.Append(']');
        WriteKey(sb, "operator", first: false);
        WriteOperator(sb, body.Operator);
        WriteKey(sb, "profileCode", first: false);
        WriteNullableString(sb, body.ProfileCode);
        // C4, the Progenitor form: written only when set, so the current and extended forms are unchanged.
        if (body.Progenitor != null)
        {
            WriteKey(sb, "progenitor", first: false);
            WriteString(sb, body.Progenitor);
        }
        if (body.ProgenitorSha != null)
        {
            WriteKey(sb, "progenitorSha", first: false);
            WriteString(sb, body.ProgenitorSha);
        }
        WriteKey(sb, "schema", first: false);
        sb.Append(body.Schema.ToString(CultureInfo.InvariantCulture));
        WriteKey(sb, "serverName", first: false);
        WriteString(sb, body.ServerName);
        sb.Append('}');
    }

    private static List<ManifestMod> SortedMods(IReadOnlyList<ManifestMod> mods)
    {
        var list = new List<ManifestMod>(mods ?? Array.Empty<ManifestMod>());
        list.Sort(CompareMods);
        return list;
    }

    private static void WriteOperator(StringBuilder sb, ManifestOperator op)
    {
        op ??= new ManifestOperator("", "", "");
        sb.Append('{');
        WriteKey(sb, "fp", first: true);
        WriteString(sb, op.Fp ?? "");
        WriteKey(sb, "name", first: false);
        WriteString(sb, op.Name ?? "");
        WriteKey(sb, "spki", first: false);
        WriteString(sb, op.Spki ?? "");
        sb.Append('}');
    }

    private static void WriteMod(StringBuilder sb, ManifestMod mod)
    {
        sb.Append('{');
        WriteKey(sb, "hotLoadable", first: true);
        sb.Append(mod.HotLoadable ? "true" : "false");
        WriteKey(sb, "id", first: false);
        WriteString(sb, mod.Id);
        WriteKey(sb, "level", first: false);
        WriteString(sb, mod.Level.ToString());
        // Mod compatibility C3, the extended form: "match" and "peers" are written only when set, so a row without
        // them (every row of the current form) is byte-identical to what an older Core signs and verifies.
        if (mod.Match != null)
        {
            WriteKey(sb, "match", first: false);
            WriteString(sb, mod.Match);
        }
        WriteKey(sb, "namespace", first: false);
        WriteNullableString(sb, mod.Namespace);
        WriteKey(sb, "package", first: false);
        WriteNullableString(sb, mod.Package);
        if (mod.Peers != null)
        {
            WriteKey(sb, "peers", first: false);
            WriteString(sb, mod.Peers);
        }
        // Only when true, so a catalog without a server-only plugin is byte-identical to one an
        // older Core signed and verifies there. An older client re-canonicalizes without this key,
        // so it rejects a catalog that carries one as a bad signature rather than offering the row.
        if (mod.ServerOnly)
        {
            WriteKey(sb, "serverOnly", first: false);
            sb.Append("true");
        }
        WriteKey(sb, "sha256", first: false);
        WriteNullableString(sb, mod.Sha256);
        WriteKey(sb, "size", first: false);
        if (mod.Size.HasValue) sb.Append(mod.Size.Value.ToString(CultureInfo.InvariantCulture));
        else sb.Append("null");
        WriteKey(sb, "url", first: false);
        WriteNullableString(sb, mod.Url);
        WriteKey(sb, "version", first: false);
        WriteString(sb, mod.Version);
        WriteKey(sb, "versionRule", first: false);
        WriteString(sb, mod.VersionRule.ToString());
        sb.Append('}');
    }

    private static void WriteKey(StringBuilder sb, string key, bool first)
    {
        if (!first) sb.Append(',');
        WriteString(sb, key);
        sb.Append(':');
    }

    private static void WriteNullableString(StringBuilder sb, string? value)
    {
        if (value == null) sb.Append("null");
        else WriteString(sb, value);
    }

    internal static void WriteString(StringBuilder sb, string value)
    {
        value ??= "";
        sb.Append('"');
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20)
                    {
                        sb.Append("\\u");
                        sb.Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
    }

    private static bool TryReadDocument(string json, out JsonDocument doc, out string? error)
    {
        doc = null!;
        error = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            error = "JSON is empty";
            return false;
        }
        try
        {
            doc = JsonDocument.Parse(json, ParseOptions);
            return true;
        }
        catch (JsonException e)
        {
            error = e.Message;
            return false;
        }
    }

    /// <summary>
    /// Level strings already reported, so a catalog full of a name this build does not know
    /// costs one log line and not one per row per join (job FMAN14, R45 finding 37). An absent
    /// <c>level</c> is the documented offer-only shape and is not reported at all.
    /// </summary>
    private static readonly HashSet<string> UnknownLevelsWarned = new(StringComparer.Ordinal);

    /// <summary>Cap, so a hostile catalog cannot mint unbounded distinct strings to fill the set.</summary>
    private const int MaxUnknownLevelWarnings = 8;

    internal static void WarnUnknownLevelOnce(string id, string? levelText)
    {
        if (string.IsNullOrEmpty(levelText)) return;
        lock (UnknownLevelsWarned)
        {
            if (UnknownLevelsWarned.Count >= MaxUnknownLevelWarnings) return;
            if (!UnknownLevelsWarned.Add(levelText!)) return;
        }

        Plugin.Logger?.LogWarning(
            "ModManifest: catalog level \"" + levelText + "\" (first seen on " + id
            + ") is not a NetworkCompatibilityLevel this build knows; reading it as NoNeedForSync."
            + " The catalog's level is display only — what a server enforces is the handshake"
            + " request it sends — but a newer server's vocabulary is worth knowing about.");
    }

#if PROGENITOR_TESTS
    internal static void ResetUnknownLevelWarningsForTests()
    {
        lock (UnknownLevelsWarned) UnknownLevelsWarned.Clear();
    }
#endif

    private static bool TryReadBody(JsonElement el, out ManifestBody body, out string? error)
    {
        body = null!;
        error = null;
        int schema = ReadInt(el, "schema") ?? 0;
        string serverName = ReadString(el, "serverName") ?? "";
        string issued = ReadString(el, "issued") ?? "";
        string? profileCode = ReadString(el, "profileCode");
        ManifestOperator op;
        if (el.TryGetProperty("operator", out var opEl) && opEl.ValueKind == JsonValueKind.Object)
        {
            op = new ManifestOperator(
                ReadString(opEl, "name") ?? "",
                ReadString(opEl, "spki") ?? "",
                (ReadString(opEl, "fp") ?? "").ToLowerInvariant());
        }
        else op = new ManifestOperator("", "", "");

        var mods = new List<ManifestMod>();
        if (el.TryGetProperty("mods", out var modsEl) && modsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var m in modsEl.EnumerateArray())
            {
                if (m.ValueKind != JsonValueKind.Object) continue;
                string id = ReadString(m, "id") ?? "";
                if (id.Length == 0) continue;
                // Job FMAN14 (R45 finding 37). The writer half of this pair was fixed by FMAN13
                // (BuildBody defaults an override-only row to NoNeedForSync); the reader still
                // resolved an absent level — and, worse, an unrecognised one such as a level name
                // a later Progenitor adds — to the single value that blocks a join, and that value
                // becomes ModConsentRow.Level, which is public API a third-party panel reads. A
                // default that can disconnect a player because a field was merely absent is the
                // wrong direction for this system to fail in, and the two sides now agree. The
                // catalog's level never enforced anything by itself in any case: only
                // Handshake.Require does, which is why the panel stopped reading this field.
                string? levelText = ReadString(m, "level");
                if (!TryReadLevel(levelText, out var level))
                {
                    level = NetworkCompatibilityLevel.NoNeedForSync;
                    WarnUnknownLevelOnce(id, levelText);
                }
                if (!TryReadRule(ReadString(m, "versionRule"), out var rule))
                    rule = NetworkVersionRule.Exact;
                mods.Add(new ManifestMod(
                    id,
                    ReadString(m, "version") ?? "",
                    level,
                    rule,
                    ReadBool(m, "hotLoadable"),
                    ReadString(m, "namespace"),
                    ReadString(m, "package"),
                    NormalizeSha(ReadString(m, "sha256")),
                    ReadInt64(m, "size"),
                    ReadString(m, "url"),
                    ReadBool(m, "serverOnly"))
                {
                    // C3: kept exactly as received (not trimmed or emptied), so the canonical form this client
                    // rebuilds for the signature check is the one the server signed.
                    Peers = ReadRawString(m, "peers"),
                    Match = ReadRawString(m, "match"),
                });
            }
        }

        // C3: kept exactly as received, like the rows' peers and match, so the rebuilt canonical form is the signed one.
        body = new ManifestBody(schema, serverName, issued, op, profileCode, mods)
        {
            Matching = ReadRawString(el, "matching"),
            // C4: kept exactly as received too.
            Progenitor = ReadRawString(el, "progenitor"),
            ProgenitorSha = ReadRawString(el, "progenitorSha"),
        };
        return true;
    }

    private static ManifestModOverride? ReadOverride(JsonElement el, string? idHint)
    {
        string id = ReadString(el, "id") ?? idHint ?? "";
        if (string.IsNullOrWhiteSpace(id)) return null;
        NetworkCompatibilityLevel? level = null;
        if (TryReadLevel(ReadString(el, "level"), out var parsedLevel)) level = parsedLevel;
        NetworkVersionRule? rule = null;
        if (TryReadRule(ReadString(el, "versionRule"), out var parsedRule)) rule = parsedRule;
        bool? hot = null;
        if (el.TryGetProperty("hotLoadable", out var hotEl)
            && (hotEl.ValueKind == JsonValueKind.True || hotEl.ValueKind == JsonValueKind.False))
            hot = hotEl.GetBoolean();
        return new ManifestModOverride
        {
            Id = id.Trim(),
            Version = ReadString(el, "version"),
            Namespace = ReadString(el, "namespace"),
            Package = ReadString(el, "package"),
            Sha256 = NormalizeSha(ReadString(el, "sha256")),
            Size = ReadInt64(el, "size"),
            Url = ReadString(el, "url"),
            HotLoadable = hot,
            Level = level,
            VersionRule = rule,
        };
    }

    internal static bool TryReadLevel(string? text, out NetworkCompatibilityLevel level) =>
        Enum.TryParse(text, ignoreCase: true, out level);

    internal static bool TryReadRule(string? text, out NetworkVersionRule rule) =>
        Enum.TryParse(text, ignoreCase: true, out rule);

    private static string? ReadString(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var p)) return null;
        if (p.ValueKind == JsonValueKind.Null || p.ValueKind == JsonValueKind.Undefined) return null;
        if (p.ValueKind == JsonValueKind.String) return p.GetString();
        return p.ToString();
    }

    /// <summary>A string property exactly as written (null when absent or not a string).</summary>
    private static string? ReadRawString(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool ReadBool(JsonElement el, string name) =>
        el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.True;

    private static int? ReadInt(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var p) || p.ValueKind != JsonValueKind.Number) return null;
        if (p.TryGetInt32(out int n)) return n;
        return null;
    }

    private static long? ReadInt64(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var p) || p.ValueKind != JsonValueKind.Number) return null;
        if (p.TryGetInt64(out long n)) return n;
        return null;
    }
}
