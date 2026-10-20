using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Progenitor.Install;

/// <summary>JOINFLOW2: whether Thunderstore still serves the exact version a server named.</summary>
internal enum PackageVersionAvailability
{
    /// <summary>The index was not read yet (or could not be).</summary>
    Unknown = 0,

    /// <summary>The exact version is listed and active.</summary>
    Available = 1,

    /// <summary>The package is listed but no longer serves that version.</summary>
    VersionGone = 2,

    /// <summary>The package is not in the index at all.</summary>
    NotListed = 3,
}

/// <summary>JOINFLOW2: one package's version list, and the catalog entry of the version a server named.</summary>
internal sealed class PackageVersions
{
    public PackageVersions(string package, string requested, IReadOnlyList<string> active, string? matched, CatalogEntry? entry, bool local,
        bool fromIndex = false)
    {
        FromIndex = fromIndex;
        Package = package ?? "";
        Requested = requested ?? "";
        Active = active ?? Array.Empty<string>();
        Matched = matched;
        Entry = entry;
        Local = local;
    }

    /// <summary>Thunderstore <c>Owner-Name</c>.</summary>
    public string Package { get; }

    /// <summary>The version the server named.</summary>
    public string Requested { get; }

    /// <summary>Every version Thunderstore still serves, newest first.</summary>
    public IReadOnlyList<string> Active { get; }

    /// <summary>The listed <c>version_number</c> that is the requested version, or null when it is gone.</summary>
    public string? Matched { get; }

    /// <summary>The catalog entry describing <see cref="Matched"/> (its download URL and size), or null.</summary>
    public CatalogEntry? Entry { get; }

    /// <summary>The listing came from the configured local index (<c>[Install] LocalIndexPath</c>), not the fetched one.</summary>
    public bool Local { get; }

    /// <summary>INDEX1: the versions are the Progenitor Index's (it lists the package; Thunderstore's listing is not used).</summary>
    public bool FromIndex { get; }

    /// <summary>
    /// INDEX1: the version lists of the packages in <paramref name="wanted"/> that the Progenitor Index lists
    /// (<paramref name="indexRows"/>: every listed version, <see cref="IndexCatalog.ToEntries"/> with all versions), added to
    /// <paramref name="into"/> unless a package is already there (the local test source is read first). Each version is
    /// chosen under its package's rule exactly as for Thunderstore.
    /// </summary>
    public static void AddFromIndex(
        IReadOnlyList<CatalogEntry>? indexRows,
        IReadOnlyDictionary<string, string> wanted,
        IDictionary<string, PackageVersions> into,
        IReadOnlyDictionary<string, Plugins.NetworkVersionRule>? rules)
    {
        if (indexRows == null || indexRows.Count == 0 || wanted == null) return;
        foreach (var kv in wanted)
        {
            string package = (kv.Key ?? "").Trim();
            if (package.Length == 0 || into.ContainsKey(package)) continue;
            var active = new List<string>();
            var byVersion = new Dictionary<string, CatalogEntry>(StringComparer.Ordinal);
            foreach (var row in indexRows)
            {
                if (!string.Equals(row.Key, package, StringComparison.OrdinalIgnoreCase)) continue;
                string v = row.Manifest.Version ?? "";
                if (v.Length == 0 || byVersion.ContainsKey(v)) continue;
                active.Add(v);
                byVersion[v] = row;
            }

            if (active.Count == 0) continue;
            var rule = Plugins.NetworkVersionRule.Exact;
            if (rules != null)
                foreach (var r in rules)
                    if (string.Equals(r.Key, package, StringComparison.OrdinalIgnoreCase)) rule = r.Value;
            string? matched = ThunderstoreVersions.Resolve(active, kv.Value, rule, out _);
            CatalogEntry? entry = matched != null && byVersion.TryGetValue(matched, out var e) ? e : null;
            string key = entry?.Key ?? byVersion[active[0]].Key;
            into[key] = new PackageVersions(key, kv.Value ?? "", active, entry == null ? null : matched, entry, local: false, fromIndex: true);
        }
    }

    /// <summary>The newest version Thunderstore serves, or empty.</summary>
    public string Latest => ThunderstoreVersions.Highest(Active, _ => true) ?? "";

    public PackageVersionAvailability Availability => Matched != null && Entry != null
        ? PackageVersionAvailability.Available
        : PackageVersionAvailability.VersionGone;
}

/// <summary>
/// JOINFLOW2: the exact-version lookup behind the join screens' Install, Update and "Use version X". A server
/// names a package (<c>Owner-Name</c>) and the version its handshake requires; Thunderstore keeps old versions
/// downloadable, so the version list of the package is read and the exact one is chosen — never the newest
/// one in its place. Game-free: the index JSON is the same Thunderstore v1 array the Install tab reads.
/// </summary>
internal static class ThunderstoreVersions
{
    private static readonly JsonDocumentOptions ParseOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
        MaxDepth = 16,
    };

    /// <summary>
    /// The listed version that is <paramref name="required"/>: the same string first, else the one equal part
    /// by part (<c>1.2</c> is <c>1.2.0</c>; a BepInEx <c>1.2.0.0</c> is Thunderstore's <c>1.2.0</c>). Null when
    /// none is: a different version is never chosen in its place.
    /// </summary>
    public static string? Resolve(IReadOnlyList<string>? available, string? required)
    {
        string want = (required ?? "").Trim();
        if (available == null || available.Count == 0 || want.Length == 0) return null;
        foreach (string v in available)
            if (string.Equals((v ?? "").Trim(), want, StringComparison.Ordinal)) return v!.Trim();
        if (!IsNumericVersion(want)) return null;
        foreach (string v in available)
        {
            string s = (v ?? "").Trim();
            if (IsNumericVersion(s) && Networking.JoinPageModel.CompareVersions(s, want) == 0) return s;
        }

        return null;
    }

    /// <summary>
    /// JOINFLOW2: the version to install under the mod's <see cref="Plugins.NetworkVersionRule"/>, as the
    /// handshake judges it. <c>Exact</c>: <see cref="Resolve(IReadOnlyList{string}, string)"/> (the same
    /// numbers part by part, so <c>1.2.0.0</c> is <c>1.2.0</c>). <c>Minor</c>: that version when it is listed,
    /// else the newest listed with the same <c>major.minor</c>. <c>None</c>: that version when listed, else the
    /// newest. <paramref name="note"/> says which version was picked when it is not the one named.
    /// </summary>
    public static string? Resolve(IReadOnlyList<string>? available, string? required, Plugins.NetworkVersionRule rule, out string note)
    {
        note = "";
        string? exact = Resolve(available, required);
        if (exact != null || available == null || available.Count == 0) return exact;
        string want = (required ?? "").Trim();
        if (rule == Plugins.NetworkVersionRule.Minor)
        {
            // The handshake's own test (pre-release tokens included), so what is installed is what it accepts.
            string? best = Highest(available, s => Networking.Handshake.Compare.SameMinor(want, s));
            if (best == null) return null;
            string[] w = want.TrimStart('v', 'V').Split('.');
            note = best + " (the server accepts any " + (w.Length >= 2 ? w[0] + "." + w[1] : want) + ".x; " + want + " is not listed)";
            return best;
        }

        if (rule == Plugins.NetworkVersionRule.None)
        {
            string? newest = Highest(available, _ => true);
            if (newest == null) return null;
            note = newest + " (the server accepts any version; " + want + " is not listed)";
            return newest;
        }

        // Exact, or a rule this build does not know (the handshake refuses those too).
        return null;
    }

    /// <summary>The highest listed version passing <paramref name="accept"/> (not the list's order: a local index may list oldest first).</summary>
    internal static string? Highest(IReadOnlyList<string> available, Func<string, bool> accept)
    {
        string? best = null;
        foreach (string v in available)
        {
            string s = (v ?? "").Trim();
            if (s.Length == 0 || !accept(s)) continue;
            if (best == null || Networking.JoinPageModel.CompareVersions(s, best) > 0) best = s;
        }

        return best;
    }

    /// <summary>Digits and dots only (<c>1.2.0</c>), at most four parts.</summary>
    internal static bool IsNumericVersion(string s)
    {
        if (string.IsNullOrEmpty(s) || s[0] == '.' || s[s.Length - 1] == '.') return false;
        int dots = 0;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '.')
            {
                if (s[i - 1] == '.') return false;
                dots++;
            }
            else if (c < '0' || c > '9') return false;
        }

        return dots <= 3;
    }

    /// <summary>
    /// Read the version lists of the packages in <paramref name="wanted"/> (package → the version a server
    /// named) from one Thunderstore v1 index body. A package already in <paramref name="into"/> is kept (the
    /// local index is read first and shadows the fetched one, like the Install tab's merge). A malformed body
    /// is a failure with the reason, never an exception.
    /// </summary>
    public static bool TryLoad(
        string? json,
        string? indexStamp,
        IReadOnlyDictionary<string, string> wanted,
        IDictionary<string, PackageVersions> into,
        bool local,
        out string error) => TryLoad(json, indexStamp, wanted, into, local, null, out error);

    /// <summary>The same, choosing each package's version under its rule (<paramref name="rules"/>; Exact when absent).</summary>
    public static bool TryLoad(
        string? json,
        string? indexStamp,
        IReadOnlyDictionary<string, string> wanted,
        IDictionary<string, PackageVersions> into,
        bool local,
        IReadOnlyDictionary<string, Plugins.NetworkVersionRule>? rules,
        out string error)
    {
        error = "";
        if (wanted == null || wanted.Count == 0) return true;
        if (string.IsNullOrWhiteSpace(json))
        {
            error = InstallReasons.IndexEmpty;
            return false;
        }

        if (json!.Length > ThunderstoreAdapter.DefaultMaxJsonBytes)
        {
            error = InstallReasons.IndexTooLarge;
            return false;
        }

        var want = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in wanted)
            if (!string.IsNullOrWhiteSpace(kv.Key)) want[kv.Key.Trim()] = kv.Value ?? "";

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json, ParseOptions); }
        catch (JsonException e)
        {
            error = InstallReasons.MalformedJson + ": " + e.Message;
            return false;
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                error = InstallReasons.IndexNotArray;
                return false;
            }

            foreach (var package in doc.RootElement.EnumerateArray())
            {
                if (package.ValueKind != JsonValueKind.Object) continue;
                string full = (ThunderstoreAdapter.ReadRawString(package, "full_name") ?? "").Trim();
                if (full.Length == 0)
                {
                    string owner = (ThunderstoreAdapter.ReadRawString(package, "owner") ?? "").Trim();
                    string name = (ThunderstoreAdapter.ReadRawString(package, "name") ?? "").Trim();
                    if (owner.Length > 0 && name.Length > 0) full = owner + "-" + name;
                }

                if (full.Length == 0 || !want.TryGetValue(full, out string? requested)) continue;
                if (into.ContainsKey(full)) continue;
                var active = new List<string>();
                if (package.TryGetProperty("versions", out var versions) && versions.ValueKind == JsonValueKind.Array)
                {
                    int seen = 0;
                    foreach (var v in versions.EnumerateArray())
                    {
                        if (v.ValueKind != JsonValueKind.Object) continue;
                        if (++seen > ThunderstoreAdapter.MaxPinnedVersionsPerPackage) break;
                        string number = (ThunderstoreAdapter.ReadRawString(v, "version_number") ?? "").Trim();
                        if (number.Length > 0 && ThunderstoreAdapter.IsActive(v)) active.Add(number);
                    }
                }

                var rule = Plugins.NetworkVersionRule.Exact;
                if (rules != null)
                    foreach (var kv in rules)
                        if (string.Equals(kv.Key, full, StringComparison.OrdinalIgnoreCase)) rule = kv.Value;
                string? matched = Resolve(active, requested, rule, out _);
                CatalogEntry? entry = null;
                if (matched != null
                    && ThunderstoreAdapter.TryAdaptOne(package, indexStamp, ThunderstoreAdapter.DefaultMaxDescriptionChars, matched, out var adapted, out _))
                    entry = adapted;
                into[full] = new PackageVersions(full, requested ?? "", active, entry == null ? null : matched, entry, local);
            }
        }

        return true;
    }

    /// <summary>The availability of <paramref name="package"/> at <paramref name="version"/> in a loaded set.</summary>
    public static PackageVersionAvailability Availability(IReadOnlyDictionary<string, PackageVersions>? loaded, string? package, string? version,
        Plugins.NetworkVersionRule rule = Plugins.NetworkVersionRule.Exact)
    {
        if (loaded == null || string.IsNullOrWhiteSpace(package)) return PackageVersionAvailability.Unknown;
        if (!loaded.TryGetValue(package!.Trim(), out var pv)) return PackageVersionAvailability.NotListed;
        return Resolve(pv.Active, version, rule, out _) != null ? PackageVersionAvailability.Available : PackageVersionAvailability.VersionGone;
    }
}
