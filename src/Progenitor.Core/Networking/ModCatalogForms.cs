using System;
using System.Collections.Generic;
using System.Text;

namespace Progenitor.Networking;

/// <summary>
/// Mod compatibility C3: the two signed forms of the catalog (docs/mod-manifest.md, Catalog body).
/// <list type="bullet">
/// <item>The <b>current form</b>: today's canonical body, with no new keys. An older client re-canonicalizes the
/// body without keys it does not know before it checks the signature, so a new key would break its check; this
/// form is what every client that did not declare <see cref="Handshake.Capability.VersionRanges"/> gets.</item>
/// <item>The <b>extended form</b>: the same rows, each also carrying <c>peers</c> (the server build's effective range
/// for that mod) and, when the server does not judge the mod by ranges, <c>match</c> (<c>exact</c>, <c>minor</c>,
/// <c>at-least</c> or <c>any</c>). Only for clients that declared the capability.</item>
/// </list>
/// Both are signed with the same operator key and carry the same <c>issued</c>, so one join section (bound to
/// <c>issued</c> and the fingerprint) serves both. Game-free.
/// </summary>
internal static class ModCatalogForms
{
    internal const string MatchExact = ClientModRules.WordExact;
    internal const string MatchMinor = ClientModRules.WordMinor;
    internal const string MatchAtLeast = ClientModRules.WordAtLeast;
    internal const string MatchAny = ClientModRules.WordAny;

    /// <summary>True when a client that declared <paramref name="clientCaps"/> reads (and is sent) the extended form.</summary>
    internal static bool ReadsExtended(Handshake.Capability clientCaps) =>
        (clientCaps & Handshake.Capability.VersionRanges) != 0;

    /// <summary>
    /// Mod compatibility C4: true when the client also reads the <b>Progenitor form</b>: the extended form plus the body's
    /// <c>progenitor</c> and <c>progenitorSha</c> (the server's own build under Exact version for all). A C3 client
    /// re-canonicalizes without those keys, so it keeps getting the extended form.
    /// </summary>
    internal static bool ReadsProgenitor(Handshake.Capability clientCaps) =>
        ReadsExtended(clientCaps) && (clientCaps & Handshake.Capability.ProgenitorExact) != 0;

    /// <summary>
    /// C4: the Progenitor form of an extended body: the same body with <paramref name="requirement"/>'s version and sha256,
    /// or <paramref name="extended"/> itself when there is no requirement (then the two forms are byte-identical and share
    /// one signature).
    /// </summary>
    internal static ManifestBody WithProgenitor(ManifestBody extended, ProgenitorRequirement? requirement)
    {
        if (extended == null) throw new ArgumentNullException(nameof(extended));
        if (requirement == null || !requirement.Valid) return extended;
        return new ManifestBody(extended.Schema, extended.ServerName, extended.Issued, extended.Operator, extended.ProfileCode, extended.Mods)
        {
            Matching = extended.Matching,
            Progenitor = requirement.Version,
            ProgenitorSha = requirement.Sha,
        };
    }

    /// <summary>
    /// How the server judges mod <paramref name="id"/> when it does not use ranges: the admins' per-mod rule, else
    /// <c>exact</c> or <c>minor</c> under Exact or Same minor for all. Null under declared ranges.
    /// </summary>
    internal static string? MatchOf(string? id, VersionMatchSettings? settings)
    {
        settings ??= VersionMatchSettings.Default;
        if (settings.TryRule(id, out var rule)) return ClientModRules.Word(rule);
        return settings.Mode switch
        {
            VersionMatchingMode.Exact => MatchExact,
            VersionMatchingMode.Minor => MatchMinor,
            _ => null,
        };
    }

    /// <summary>
    /// The extended body: <paramref name="body"/>'s rows with <see cref="ManifestMod.Peers"/> from
    /// <paramref name="peersOf"/> (empty: not written) and <see cref="ManifestMod.Match"/> from
    /// <paramref name="settings"/>. Everything else is unchanged.
    /// </summary>
    internal static ManifestBody Extend(ManifestBody body, Func<ManifestMod, string?>? peersOf, VersionMatchSettings? settings)
    {
        if (body == null) throw new ArgumentNullException(nameof(body));
        var rows = new List<ManifestMod>(body.Mods.Count);
        foreach (var m in body.Mods)
        {
            if (m == null) continue;
            string? peers = null;
            try { peers = peersOf?.Invoke(m); }
            catch { peers = null; }
            rows.Add(With(m, ManifestMod.EmptyToNull(peers), MatchOf(m.Id, settings)));
        }

        return new ManifestBody(body.Schema, body.ServerName, body.Issued, body.Operator, body.ProfileCode, rows)
        {
            Matching = VersionMatchingText.Word((settings ?? VersionMatchSettings.Default).Mode),
        };
    }

    /// <summary>The current form of a body: its rows without <c>peers</c> and <c>match</c>, and no <c>matching</c>.</summary>
    internal static ManifestBody Current(ManifestBody body)
    {
        if (body == null) throw new ArgumentNullException(nameof(body));
        var rows = new List<ManifestMod>(body.Mods.Count);
        foreach (var m in body.Mods)
            if (m != null) rows.Add(m.Peers == null && m.Match == null ? m : With(m, null, null));
        return new ManifestBody(body.Schema, body.ServerName, body.Issued, body.Operator, body.ProfileCode, rows);
    }

    /// <summary>
    /// What the extended form adds, for the catalog's change stamp: the server-wide mode (<c>matching=exact;</c>) and one
    /// entry per row (<c>id=peers|match;</c>). A change of a range, of Version matching or of a per-mod rule for a catalog
    /// row re-signs the catalog even when nothing else changed. A per-mod rule for a mod without a catalog row changes
    /// nothing a client reads from the catalog; it reaches clients through the rewritten handshake request rows.
    /// </summary>
    internal static string ExtensionStamp(ManifestBody extended)
    {
        var sb = new StringBuilder();
        if (extended == null) return "";
        sb.Append("matching=").Append(extended.Matching ?? "").Append(';');
        // C4: the server's Progenitor requirement (written only while Exact is enforced for Progenitor).
        if (extended.Progenitor != null) sb.Append("progenitor=").Append(extended.Progenitor).Append('|').Append(extended.ProgenitorSha ?? "").Append(';');
        foreach (var m in extended.Mods)
        {
            if (m == null || (m.Peers == null && m.Match == null)) continue;
            sb.Append(m.Id).Append('=').Append(m.Peers ?? "").Append('|').Append(m.Match ?? "").Append(';');
        }

        return sb.ToString();
    }

    /// <summary>A copy of <paramref name="m"/> with <paramref name="peers"/> and <paramref name="match"/>.</summary>
    internal static ManifestMod With(ManifestMod m, string? peers, string? match) =>
        new(m.Id, m.Version, m.Level, m.VersionRule, m.HotLoadable, m.Namespace, m.Package, m.Sha256, m.Size, m.Url, m.ServerOnly)
        {
            Peers = peers,
            Match = match,
        };
}
