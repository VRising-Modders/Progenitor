using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using Progenitor.Networking;
using Progenitor.Plugins;

namespace Progenitor.Install;

/// <summary>AUTOUPDATE1 (M6.1): <c>[AutoUpdate] Mods</c>. AUTOUPDATE2: Automatic applies updates in the apply window.</summary>
internal enum UpdateMode
{
    Automatic = 0,
    Notify = 1,
    Off = 2,
}

/// <summary>AUTOUPDATE1: the setting's words and defaults (decisions of 2026-10-09).</summary>
internal static class UpdateSettings
{
    public const string Section = "AutoUpdate";
    public const string ModsKey = "Mods";
    public const string PromptKey = "PromptBeforeJoin";
    public const string MajorKey = "MajorVersions";

    /// <summary>Major versions only notify by default (decision 1).</summary>
    public const string DefaultMajor = "Notify";

    public static readonly string[] MajorWords = { "Automatic", "Notify" };

    /// <summary><c>[AutoUpdate] MajorVersions</c>: true for Automatic; anything else (Notify, empty, unknown) is Notify.</summary>
    public static bool MajorAutomatic(string? text) => (text ?? "").Trim().Equals("Automatic", StringComparison.OrdinalIgnoreCase);

    /// <summary>A per-mod mode word, or null for "use the global choice" (empty or unknown).</summary>
    public static UpdateMode? ParsePerMod(string? text)
    {
        string t = (text ?? "").Trim();
        if (t.Equals("Automatic", StringComparison.OrdinalIgnoreCase)) return UpdateMode.Automatic;
        if (t.Equals("Notify", StringComparison.OrdinalIgnoreCase)) return UpdateMode.Notify;
        if (t.Equals("Off", StringComparison.OrdinalIgnoreCase)) return UpdateMode.Off;
        return null;
    }

    /// <summary>Clients: Automatic for mods (decision 1).</summary>
    public const string DefaultMode = "Automatic";

    /// <summary>The pre-join prompt is on by default (decision 2).</summary>
    public const bool DefaultPrompt = true;

    public static readonly string[] ModeWords = { "Automatic", "Notify", "Off" };

    public static UpdateMode Parse(string? text)
    {
        string t = (text ?? "").Trim();
        if (t.Equals("Notify", StringComparison.OrdinalIgnoreCase)) return UpdateMode.Notify;
        if (t.Equals("Off", StringComparison.OrdinalIgnoreCase)) return UpdateMode.Off;
        return UpdateMode.Automatic;
    }

    public static string Word(UpdateMode mode) => mode switch
    {
        UpdateMode.Notify => "Notify",
        UpdateMode.Off => "Off",
        _ => "Automatic",
    };

    /// <summary>What a mode does (the Preferences row's help and the bridge's <c>modeEffect</c>).</summary>
    public static string Effect(UpdateMode mode) => mode switch
    {
        UpdateMode.Off => "no notice, no update list and no pre-join prompt; each listing's Update stays",
        UpdateMode.Notify => "shows updates; nothing is applied without a click",
        _ => "applies updates at the title screen or right after leaving a server; major versions follow Major versions",
    };
}

/// <summary>One package Progenitor installed on this client (a ledger row set), as update detection reads it.</summary>
internal sealed class ManagedPackage
{
    /// <summary>The listing key (<c>Owner-Name</c>) from the ledger's <c>package:&lt;key&gt;@&lt;version&gt;</c>.</summary>
    public string Key { get; set; } = "";

    public string Version { get; set; } = "";

    /// <summary>The first plugin GUID a ledger row names, or empty.</summary>
    public string PluginId => PluginIds.Count > 0 ? PluginIds[0] : "";

    /// <summary>Every plugin GUID the package's ledger rows name (a package can carry more than one plugin).</summary>
    public List<string> PluginIds { get; } = new();

    /// <summary>A ledgered file is gone or its bytes changed after Progenitor wrote it: not managed (design, Terms).</summary>
    public bool Changed { get; set; }

    /// <summary>Waiting for the next start (every row is <c>pending</c>).</summary>
    public bool Pending { get; set; }

    /// <summary>INDEX1: installed from the Progenitor Index (a ledger row says <c>"origin": "index"</c>).</summary>
    public bool FromIndex { get; set; }
}

/// <summary>One listing of the index Progenitor already read, reduced to what detection needs.</summary>
internal sealed class IndexListing
{
    public string Key { get; set; } = "";

    /// <summary>The name shown to the player (the package name with underscores as spaces).</summary>
    public string Name { get; set; } = "";

    /// <summary>The newest active version the index lists.</summary>
    public string Latest { get; set; } = "";

    public bool Deprecated { get; set; }

    public bool Nsfw { get; set; }

    /// <summary>From <c>[Install] LocalIndexPath</c> (decision 10).</summary>
    public bool Local { get; set; }

    /// <summary>The newest version's dependency strings (<c>Owner-Name-1.2.3</c>).</summary>
    public IReadOnlyList<string> Dependencies { get; set; } = Array.Empty<string>();

    /// <summary>INDEX1: this listing is the Progenitor Index's.</summary>
    public bool FromIndex { get; set; }

    /// <summary>INDEX1: on an Index listing, the Thunderstore listing of the same key it shadows, or null.</summary>
    public IndexListing? Thunderstore { get; set; }

    /// <summary>
    /// INDEX1: the listing an install from <paramref name="fromIndex"/>'s source updates from: this one when it is that
    /// source's, the shadowed Thunderstore one for a Thunderstore install, else null.
    /// </summary>
    public IndexListing? ForSource(bool fromIndex) =>
        fromIndex == FromIndex ? this : !fromIndex ? Thunderstore : null;
}

/// <summary>One mod with a newer version in the index.</summary>
internal sealed class UpdateItem
{
    public string Package { get; set; } = "";
    public string Name { get; set; } = "";
    public string Installed { get; set; } = "";
    public string Latest { get; set; } = "";

    /// <summary>The newest version's major number is higher than the installed one's.</summary>
    public bool Major { get; set; }

    /// <summary>Kept at this version (<b>Keep this version</b>), or empty.</summary>
    public string PinnedAt { get; set; } = "";

    public bool Pinned => PinnedAt.Length > 0;

    /// <summary>Packages the newest version needs that are not installed: the update asks first (decision 3).</summary>
    public IReadOnlyList<string> NewDependencies { get; set; } = Array.Empty<string>();

    public string PluginId => PluginIds.Count > 0 ? PluginIds[0] : "";

    /// <summary>Every plugin GUID of the package: a server requirement on any of them caps the update.</summary>
    public IReadOnlyList<string> PluginIds { get; set; } = Array.Empty<string>();

    public bool Local { get; set; }

    /// <summary>The installed copy waits for the next start.</summary>
    public bool Pending { get; set; }

    /// <summary>AUTOUPDATE2: Automatic mode applies this update in the apply window (<see cref="UpdatePolicy"/>).</summary>
    public bool Auto { get; set; }

    /// <summary>AUTOUPDATE2: why it is not applied automatically, or empty.</summary>
    public string NotAutoReason { get; set; } = "";

    /// <summary>AUTOUPDATE2: the per-mod mode word (<c>Automatic</c>, <c>Notify</c>), or empty for the global choice.</summary>
    public string PerMod { get; set; } = "";

    /// <summary>AUTOUPDATE2: <see cref="Latest"/> is in the failed list (its reason), or empty.</summary>
    public string FailedBefore { get; set; } = "";

    /// <summary>The list row's text: <c>ChatSkin 1.2.0 → 1.3.0 · major</c>.</summary>
    public string Line
    {
        get
        {
            var sb = new StringBuilder();
            sb.Append(Name.Length > 0 ? Name : Package).Append(' ').Append(Installed).Append(" → ").Append(Latest);
            if (Major) sb.Append(" · major");
            if (NewDependencies.Count > 0)
                sb.Append(" · needs ").Append(string.Join(", ", NewDependencies)).Append(" (not installed)");
            if (FailedBefore.Length > 0) sb.Append(" · failed to load before");
            return sb.ToString();
        }
    }

    public Dictionary<string, object?> Describe() => new()
    {
        ["package"] = Package,
        ["name"] = Name,
        ["installed"] = Installed,
        ["latest"] = Latest,
        ["major"] = Major,
        ["pinned"] = Pinned,
        ["pinnedAt"] = PinnedAt,
        ["newDependencies"] = new List<string>(NewDependencies),
        ["pluginId"] = PluginId,
        ["pluginIds"] = new List<string>(PluginIds),
        ["local"] = Local,
        ["pending"] = Pending,
        ["auto"] = Auto,
        ["notAutoReason"] = NotAutoReason,
        ["perMod"] = PerMod,
        ["failedBefore"] = FailedBefore,
        ["line"] = Line,
    };
}

/// <summary>A managed package detection looked at and did not list, with why.</summary>
internal sealed class UpdateSkip
{
    public UpdateSkip(string package, string reason)
    {
        Package = package ?? "";
        Reason = reason ?? "";
    }

    public string Package { get; }
    public string Reason { get; }
}

/// <summary>The result of one detection pass.</summary>
internal sealed class UpdateReport
{
    public static readonly UpdateReport Empty = new(Array.Empty<UpdateItem>(), Array.Empty<UpdateSkip>(), 0, DateTime.MinValue, "");

    public UpdateReport(IReadOnlyList<UpdateItem> items, IReadOnlyList<UpdateSkip> skipped, int managed, DateTime computedUtc, string indexStamp)
    {
        Items = items ?? Array.Empty<UpdateItem>();
        Skipped = skipped ?? Array.Empty<UpdateSkip>();
        Managed = managed;
        ComputedUtc = computedUtc;
        IndexStamp = indexStamp ?? "";
    }

    /// <summary>Every mod with a newer version, pinned ones included (they sort last).</summary>
    public IReadOnlyList<UpdateItem> Items { get; }

    public IReadOnlyList<UpdateSkip> Skipped { get; }

    /// <summary>How many managed packages detection read.</summary>
    public int Managed { get; }

    public DateTime ComputedUtc { get; }

    public string IndexStamp { get; }

    /// <summary>The badge's N: updates that are not pinned and whose version did not fail to load before (AUTOUPDATE2).</summary>
    public int Count
    {
        get
        {
            int n = 0;
            foreach (var i in Items) if (!i.Pinned && i.FailedBefore.Length == 0) n++;
            return n;
        }
    }

    public bool HasRun => ComputedUtc != DateTime.MinValue;

    public UpdateItem? Find(string package)
    {
        foreach (var i in Items)
            if (string.Equals(i.Package, package, StringComparison.OrdinalIgnoreCase)) return i;
        return null;
    }
}

/// <summary>
/// AUTOUPDATE1 review item 7: the version order updates use. Numeric parts compare as numbers with missing parts as
/// zero (<c>1.2</c> equals <c>1.2.0</c>), a leading <c>v</c> and build metadata (<c>+…</c>) are ignored, and on equal
/// numbers a release is newer than a prerelease (<c>1.2.0</c> updates <c>1.2.0-beta</c>, never the reverse); two
/// prereleases compare by their tag. A part that is not a number compares as text.
/// </summary>
internal static class UpdateVersions
{
    public static bool IsNewer(string? candidate, string? installed) => Compare(candidate, installed) > 0;

    public static int Compare(string? a, string? b)
    {
        Split(a, out var na, out string pa);
        Split(b, out var nb, out string pb);
        int n = Math.Max(na.Length, nb.Length);
        for (int i = 0; i < n; i++)
        {
            string x = i < na.Length ? na[i] : "0";
            string y = i < nb.Length ? nb[i] : "0";
            bool xi = long.TryParse(x, NumberStyles.None, CultureInfo.InvariantCulture, out long xv);
            bool yi = long.TryParse(y, NumberStyles.None, CultureInfo.InvariantCulture, out long yv);
            int c = xi && yi ? xv.CompareTo(yv) : string.Compare(x, y, StringComparison.OrdinalIgnoreCase);
            if (c != 0) return c;
        }

        if (pa.Length == 0 && pb.Length == 0) return 0;
        if (pa.Length == 0) return 1;
        if (pb.Length == 0) return -1;
        return string.Compare(pa, pb, StringComparison.OrdinalIgnoreCase);
    }

    private static void Split(string? version, out string[] numbers, out string prerelease)
    {
        string v = (version ?? "").Trim();
        if (v.StartsWith("v", StringComparison.OrdinalIgnoreCase)) v = v.Substring(1);
        int plus = v.IndexOf('+');
        if (plus >= 0) v = v.Substring(0, plus);
        int dash = v.IndexOf('-');
        prerelease = dash >= 0 ? v.Substring(dash + 1) : "";
        if (dash >= 0) v = v.Substring(0, dash);
        numbers = v.Length == 0 ? Array.Empty<string>() : v.Split('.');
    }
}

/// <summary>
/// AUTOUPDATE1: which managed mods have an update, from the index Progenitor already read and the install ledger. Pure:
/// no file, network or game access (the caller reads both off the main thread).
/// </summary>
internal static class UpdateDetection
{
    public const string SkipChanged = "its files changed or are gone since Progenitor wrote them";
    public const string SkipNotListed = "not in the index";

    /// <summary>INDEX1: only the other index lists the package; an update never switches the source it was installed from.</summary>
    public const string SkipOtherSource = "not listed by the source it was installed from";
    public const string SkipDeprecated = "the listing is deprecated";
    public const string SkipNsfw = "the listing is marked NSFW and Show mods with NSFW content is off";
    public const string SkipLoader = "the loader or Progenitor itself (Progenitor's own update is listed on its own)";
    public const string SkipOff = "updates are off for this mod";

    /// <summary>
    /// AUTOUPDATE2: detection with the per-mod modes, the failed list and the automatic policy. A mod whose own mode is
    /// Off is skipped (no notice, no download); every listed item carries <see cref="UpdateItem.Auto"/>.
    /// </summary>
    public static UpdateReport Detect(
        IReadOnlyList<ManagedPackage> managed,
        IReadOnlyDictionary<string, IndexListing> index,
        UpdatePins? file,
        UpdateMode global,
        bool majorsAutomatic,
        Func<string, bool>? dependencyPresent,
        bool showNsfw,
        DateTime nowUtc,
        string indexStamp = "")
    {
        file ??= new UpdatePins();
        var kept = new List<ManagedPackage>();
        var off = new List<UpdateSkip>();
        foreach (var m in managed ?? Array.Empty<ManagedPackage>())
        {
            if (m == null) continue;
            if (file.ModeOf(m.Key) == UpdateMode.Off && !file.Pins.ContainsKey(m.Key))
            {
                off.Add(new UpdateSkip(m.Key, SkipOff));
                continue;
            }

            kept.Add(m);
        }

        var r = Detect(kept, index, file.Pins, dependencyPresent, showNsfw, nowUtc, indexStamp);
        foreach (var item in r.Items) UpdatePolicy.Apply(item, global, file, majorsAutomatic);
        if (off.Count == 0) return r;
        var skipped = new List<UpdateSkip>(r.Skipped);
        skipped.AddRange(off);
        return new UpdateReport(r.Items, skipped, r.Managed + off.Count, r.ComputedUtc, r.IndexStamp);
    }

    public static UpdateReport Detect(
        IReadOnlyList<ManagedPackage> managed,
        IReadOnlyDictionary<string, IndexListing> index,
        IReadOnlyDictionary<string, string>? pins,
        Func<string, bool>? dependencyPresent,
        bool showNsfw,
        DateTime nowUtc,
        string indexStamp = "")
    {
        var items = new List<UpdateItem>();
        var skipped = new List<UpdateSkip>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int count = 0;
        foreach (var m in managed ?? Array.Empty<ManagedPackage>())
        {
            if (m == null || m.Key.Length == 0 || !seen.Add(m.Key)) continue;
            count++;
            if (LoaderPackages.IsHiddenPackage(null, null, m.Key)) { skipped.Add(new UpdateSkip(m.Key, SkipLoader)); continue; }
            if (m.Changed) { skipped.Add(new UpdateSkip(m.Key, SkipChanged)); continue; }
            if (index == null || !index.TryGetValue(m.Key, out var listing) || listing == null)
            {
                skipped.Add(new UpdateSkip(m.Key, SkipNotListed));
                continue;
            }

            // INDEX1: updates stay with their source. An Index install updates only from the Index; a Thunderstore install
            // only from Thunderstore (the row an Index row shadows). The local test source is not a source of either.
            if (!listing.Local)
            {
                var own = listing.ForSource(m.FromIndex);
                if (own == null)
                {
                    skipped.Add(new UpdateSkip(m.Key, SkipOtherSource));
                    continue;
                }

                listing = own;
            }

            if (listing.Deprecated) { skipped.Add(new UpdateSkip(m.Key, SkipDeprecated)); continue; }
            if (listing.Nsfw && !showNsfw) { skipped.Add(new UpdateSkip(m.Key, SkipNsfw)); continue; }
            if (!UpdateVersions.IsNewer(listing.Latest, m.Version)) continue;

            string pin = "";
            if (pins != null && pins.TryGetValue(m.Key, out var pinned) && pinned != null
                && string.Equals(pinned.Trim(), m.Version.Trim(), StringComparison.OrdinalIgnoreCase))
                pin = pinned.Trim();

            items.Add(new UpdateItem
            {
                Package = m.Key,
                Name = listing.Name.Length > 0 ? listing.Name : m.Key,
                Installed = m.Version,
                Latest = listing.Latest,
                Major = IsMajor(m.Version, listing.Latest),
                PinnedAt = pin,
                NewDependencies = MissingDependencies(listing.Dependencies, dependencyPresent, m.Key),
                PluginIds = m.PluginIds.ToArray(),
                Local = listing.Local,
                Pending = m.Pending,
            });
        }

        // Unpinned first, then by name: the list's order and the pre-join prompt's.
        items.Sort((a, b) =>
        {
            int p = a.Pinned.CompareTo(b.Pinned);
            return p != 0 ? p : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });
        return new UpdateReport(items, skipped, count, nowUtc, indexStamp);
    }

    /// <summary>The first number of <paramref name="latest"/> is higher than that of <paramref name="installed"/>.</summary>
    public static bool IsMajor(string? installed, string? latest)
    {
        if (!TryMajor(installed, out int a) || !TryMajor(latest, out int b)) return false;
        return b > a;
    }

    private static bool TryMajor(string? version, out int major)
    {
        major = 0;
        string v = (version ?? "").Trim();
        if (v.StartsWith("v", StringComparison.OrdinalIgnoreCase)) v = v.Substring(1);
        int end = 0;
        while (end < v.Length && char.IsDigit(v[end])) end++;
        return end > 0 && int.TryParse(v.Substring(0, end), NumberStyles.None, CultureInfo.InvariantCulture, out major);
    }

    /// <summary><c>Owner-Name-1.2.3</c> → <c>Owner-Name</c>; empty when it does not have that shape.</summary>
    public static string DependencyKey(string? dependency)
    {
        string d = (dependency ?? "").Trim();
        int dash = d.LastIndexOf('-');
        if (dash <= 0 || dash == d.Length - 1) return "";
        string version = d.Substring(dash + 1);
        if (version.Length == 0 || !char.IsDigit(version[0])) return "";
        return d.Substring(0, dash);
    }

    private static IReadOnlyList<string> MissingDependencies(IReadOnlyList<string>? deps, Func<string, bool>? present, string self)
    {
        if (deps == null || deps.Count == 0) return Array.Empty<string>();
        List<string>? missing = null;
        foreach (var d in deps)
        {
            string key = DependencyKey(d);
            if (key.Length == 0 || string.Equals(key, self, StringComparison.OrdinalIgnoreCase)) continue;
            if (LoaderPackages.IsHiddenPackage(null, null, key)) continue;
            bool there;
            try { there = present?.Invoke(key) ?? false; }
            catch { there = false; }
            if (there) continue;
            (missing ??= new List<string>()).Add(key);
        }

        return missing ?? (IReadOnlyList<string>)Array.Empty<string>();
    }
}

/// <summary>What a server required at its last handshake (one mod), remembered for the pre-join prompt.</summary>
internal sealed class ServerRequirement
{
    public string ModId { get; set; } = "";
    public string Version { get; set; } = "";
    public NetworkVersionRule Rule { get; set; } = NetworkVersionRule.Exact;

    /// <summary>
    /// Mod compatibility C3: the server build's effective peer range for this mod, from its signed (extended) catalog;
    /// empty when not known (an older server, or no catalog seen yet).
    /// </summary>
    public string Peers { get; set; } = "";

    /// <summary>
    /// C3: how the server judges this mod when not by ranges (<c>exact</c>, <c>minor</c>, <c>at-least</c>, <c>any</c>),
    /// from its catalog; empty when not known or under declared ranges.
    /// </summary>
    public string Match { get; set; } = "";

    /// <summary>
    /// C3: the server-wide Version matching its request bits announced (<c>exact</c>, <c>minor</c>), or empty for declared
    /// ranges and older servers.
    /// </summary>
    public string Matching { get; set; } = "";

    public ServerRequirement Copy() => (ServerRequirement)MemberwiseClone();
}

/// <summary>AUTOUPDATE1: the handshake rows that cap an update (a client-only row is never compared).</summary>
internal static class ServerRequirements
{
    public static List<ServerRequirement> Of(IEnumerable<Handshake.ModRecord>? rows) => Of(rows, VersionMatchingMode.Range);

    /// <summary>
    /// C3: the rows with the server's Version matching from its request bits (<see cref="VersionMatchJudge.ModeOf"/>):
    /// under Exact or Same minor for all the rule bytes are the server's rewritten strict rules.
    /// </summary>
    public static List<ServerRequirement> Of(IEnumerable<Handshake.ModRecord>? rows, VersionMatchingMode mode)
    {
        var list = new List<ServerRequirement>();
        if (rows == null) return list;
        string matching = MatchingWord(mode);
        foreach (var r in rows)
        {
            if (string.IsNullOrEmpty(r.Guid) || r.Level == NetworkCompatibilityLevel.ClientOnly) continue;
            list.Add(new ServerRequirement { ModId = r.Guid, Version = r.Version, Rule = r.VersionRule, Matching = matching });
        }

        return list;
    }

    /// <summary><c>exact</c>, <c>minor</c>, or empty for declared ranges.</summary>
    public static string MatchingWord(VersionMatchingMode mode) => mode switch
    {
        VersionMatchingMode.Exact => ServerVersionCheck.MatchExact,
        VersionMatchingMode.Minor => ServerVersionCheck.MatchMinor,
        _ => "",
    };

    /// <summary>
    /// C3: a signed catalog's per-mod facts (<see cref="ManifestMod.Peers"/>, <see cref="ManifestMod.Match"/>) as
    /// requirement rows to merge (<see cref="Merge"/>). Rows without either are left out.
    /// </summary>
    public static List<ServerRequirement> FromCatalog(IEnumerable<ManifestMod>? mods)
    {
        var list = new List<ServerRequirement>();
        if (mods == null) return list;
        foreach (var m in mods)
        {
            if (m == null || string.IsNullOrEmpty(m.Id)) continue;
            string peers = (m.Peers ?? "").Trim();
            string match = ServerVersionCheck.NormalizeMatch(m.Match);
            if (peers.Length == 0 && match.Length == 0) continue;
            list.Add(new ServerRequirement { ModId = m.Id, Version = m.Version ?? "", Peers = peers, Match = match });
        }

        return list;
    }

    /// <summary>
    /// C3: copy each catalog row's <see cref="ServerRequirement.Peers"/> and <see cref="ServerRequirement.Match"/> onto
    /// the requirement of the same mod at the same version. Returns how many rows changed.
    /// </summary>
    public static int Merge(IList<ServerRequirement>? into, IEnumerable<ServerRequirement>? catalog)
    {
        if (into == null || catalog == null) return 0;
        int changed = 0;
        foreach (var c in catalog)
        {
            if (c == null) continue;
            foreach (var r in into)
            {
                if (r == null || !string.Equals(r.ModId, c.ModId, StringComparison.OrdinalIgnoreCase)) continue;
                if (c.Version.Length > 0 && !string.Equals(r.Version, c.Version, StringComparison.OrdinalIgnoreCase)) continue;
                if (c.Peers.Length > 0) r.Peers = c.Peers;
                r.Match = c.Match;
                changed++;
            }
        }

        return changed;
    }

    /// <summary>
    /// C3: a signed catalog's <c>matching</c> (<see cref="ManifestBody.Matching"/>) as a <see cref="ServerRequirement.Matching"/>
    /// word: <c>exact</c>, <c>minor</c>, or empty for declared ranges. Null when the catalog did not say (the current form,
    /// an older server) or said something this build does not know; the request bits then stay in force.
    /// </summary>
    public static string? CatalogMatching(string? word)
    {
        if (word == null || !VersionMatchingText.TryParse(word, out var mode)) return null;
        return MatchingWord(mode);
    }

    /// <summary>
    /// C3: set every row's <see cref="ServerRequirement.Matching"/> to <paramref name="matching"/> (from
    /// <see cref="CatalogMatching"/>); null leaves them. Returns how many rows changed.
    /// </summary>
    public static int ApplyMatching(IList<ServerRequirement>? rows, string? matching)
    {
        if (rows == null || matching == null) return 0;
        int changed = 0;
        foreach (var r in rows)
        {
            if (r == null || string.Equals(r.Matching, matching, StringComparison.Ordinal)) continue;
            r.Matching = matching;
            changed++;
        }

        return changed;
    }
}

/// <summary>One update the pre-join prompt offers, or holds back because of the server.</summary>
internal sealed class PreJoinOffer
{
    public UpdateItem Item { get; set; } = new();

    /// <summary>The version offered (the newest the server accepts), or empty when held.</summary>
    public string Version { get; set; } = "";

    /// <summary>Why it is not offered (the server needs another version), or empty.</summary>
    public string Held { get; set; } = "";

    public Dictionary<string, object?> Describe() => new()
    {
        ["package"] = Item.Package,
        ["name"] = Item.Name,
        ["installed"] = Item.Installed,
        ["version"] = Version,
        ["held"] = Held,
        ["major"] = Item.Major,
        ["newDependencies"] = new List<string>(Item.NewDependencies),
    };
}

/// <summary>The pre-join prompt's decision for one server.</summary>
internal sealed class PreJoinDecision
{
    public bool Show { get; set; }

    /// <summary>Why nothing is shown (<c>off</c>, <c>prompt-off</c>, <c>nothing</c>, <c>dismissed</c>, …), or empty.</summary>
    public string Why { get; set; } = "";

    public List<PreJoinOffer> Offers { get; } = new();

    public List<PreJoinOffer> Held { get; } = new();

    /// <summary>The session key a <b>Join anyway</b> remembers (server plus the offered versions).</summary>
    public string DismissKey { get; set; } = "";

    /// <summary>The server's requirements were known from an earlier join.</summary>
    public bool RequirementsKnown { get; set; }

    public Dictionary<string, object?> Describe()
    {
        var offers = new List<object?>();
        foreach (var o in Offers) offers.Add(o.Describe());
        var held = new List<object?>();
        foreach (var o in Held) held.Add(o.Describe());
        return new Dictionary<string, object?>
        {
            ["show"] = Show,
            ["why"] = Why,
            ["offers"] = offers,
            ["held"] = held,
            ["dismissKey"] = DismissKey,
            ["requirementsKnown"] = RequirementsKnown,
        };
    }
}

/// <summary>
/// AUTOUPDATE1: the pre-join prompt (decision 2). Offers each unpinned update the server would accept, as remembered from
/// its last handshake and catalog (mod compatibility C3, <see cref="ServerVersionCheck"/>): the server's match when it has
/// one (exact, same minor, at least, any), else the newer side's declared range, else today's cap (an Exact rule offers
/// only the required version, a Minor rule only one with the same major.minor). A mod the server does not check is offered
/// at the newest. Pure.
/// </summary>
internal static class PreJoinOffers
{
    public static PreJoinDecision Decide(
        UpdateReport report,
        IReadOnlyList<ServerRequirement>? requirements,
        UpdateMode mode,
        bool promptOn,
        string serverKey,
        ICollection<string>? dismissed,
        Func<UpdateItem, string, string?>? candidateRange = null)
    {
        var d = new PreJoinDecision { RequirementsKnown = requirements != null };
        if (mode == UpdateMode.Off) { d.Why = "off"; return d; }
        if (!promptOn) { d.Why = "prompt-off"; return d; }
        if (report == null || !report.HasRun) { d.Why = "not-checked"; return d; }

        foreach (var item in report.Items)
        {
            if (item.Pinned) continue;
            // AUTOUPDATE2: a version that failed to load is not offered before a join (the mod page still lists it).
            if (item.FailedBefore.Length > 0)
            {
                d.Held.Add(new PreJoinOffer { Item = item, Held = item.Latest + " failed to load before" });
                continue;
            }

            // C3: the server's remembered match, else the ranges (the newer side's decides), else today's rule cap.
            string why = HeldReason(item, requirements, candidateRange, ServerVersionCheck.ThisServer);
            if (why.Length == 0)
            {
                d.Offers.Add(new PreJoinOffer { Item = item, Version = item.Latest });
                continue;
            }

            d.Held.Add(new PreJoinOffer { Item = item, Held = why });
        }

        var parts = new List<string>();
        foreach (var o in d.Offers) parts.Add(o.Item.Package + "@" + o.Version);
        parts.Sort(StringComparer.OrdinalIgnoreCase);
        d.DismissKey = (serverKey ?? "") + "|" + string.Join(",", parts);
        if (d.Offers.Count == 0) { d.Why = "nothing"; return d; }
        if (dismissed != null && dismissed.Contains(d.DismissKey)) { d.Why = "dismissed"; return d; }
        d.Show = true;
        return d;
    }

    /// <summary>The dialog's body: one sentence listing the offered updates.</summary>
    public static string Body(PreJoinDecision d)
    {
        var parts = new List<string>();
        foreach (var o in d.Offers)
        {
            string line = o.Item.Name + " " + o.Item.Installed + " → " + o.Version;
            if (o.Item.Major) line += " (major)";
            if (o.Item.NewDependencies.Count > 0) line += " (needs " + string.Join(", ", o.Item.NewDependencies) + ")";
            parts.Add(line);
        }

        string body = string.Join(", ", parts) + ".";
        if (d.Held.Count > 0)
        {
            var held = new List<string>();
            foreach (var h in d.Held) held.Add(h.Item.Name + " (" + h.Held + ")");
            body += " Not offered: " + string.Join(", ", held) + ".";
        }

        return body;
    }

    internal static ServerRequirement? FindRequirement(IReadOnlyList<ServerRequirement>? reqs, IReadOnlyList<string> pluginIds)
    {
        if (reqs == null || pluginIds == null) return null;
        foreach (var id in pluginIds)
        {
            if (string.IsNullOrEmpty(id)) continue;
            foreach (var r in reqs)
                if (r != null && string.Equals(r.ModId, id, StringComparison.OrdinalIgnoreCase)) return r;
        }

        return null;
    }

    /// <summary>
    /// Review item 5: the update to <c>item.Latest</c> breaks a version the server this client is connected to checks
    /// (its live handshake rows): the reason, or empty when it does not.
    /// </summary>
    public static string BlockedBy(UpdateItem item, IReadOnlyList<ServerRequirement>? connected,
        Func<UpdateItem, string, string?>? candidateRange = null) =>
        HeldReason(item, connected, candidateRange, ServerVersionCheck.ServerYouAreOn);

    /// <summary>
    /// C3: why the server <paramref name="reqs"/> describe would refuse <c>item.Latest</c>, or empty
    /// (<see cref="ServerVersionCheck"/>). <paramref name="candidateRange"/> gives the effective peer range of a version of
    /// the package from its inspection, or null when it was never inspected.
    /// </summary>
    internal static string HeldReason(UpdateItem item, IReadOnlyList<ServerRequirement>? reqs,
        Func<UpdateItem, string, string?>? candidateRange, string subject)
    {
        if (item == null) return "";
        var req = FindRequirement(reqs, item.PluginIds);
        if (req == null) return "";
        string? range = null;
        try { range = candidateRange?.Invoke(item, item.Latest); }
        catch { range = null; }
        return ServerVersionCheck.Reason(req, item.Latest, range, subject);
    }

    internal static string MinorOf(string version)
    {
        var parts = (version ?? "").Split('.');
        return parts.Length >= 2 ? parts[0] + "." + parts[1] : version ?? "";
    }
}

/// <summary>
/// AUTOUPDATE1: <c>BepInEx/config/progenitor-auto-update.json</c> (schema 1): the per-mod <b>Keep this version</b> pins,
/// keyed by package so they survive reinstalls and Core swaps. Plain values; the caller does the file IO off the main thread.
/// </summary>
internal sealed class UpdatePins
{
    public const string FileName = "progenitor-auto-update.json";
    public const int Schema = 1;

    public Dictionary<string, string> Pins { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>AUTOUPDATE2: the per-mod mode (<c>Automatic</c>, <c>Notify</c>, <c>Off</c>); absent: the global choice.</summary>
    public Dictionary<string, string> Modes { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>AUTOUPDATE2: versions that failed to load and were rolled back; never applied automatically again.</summary>
    public Dictionary<string, List<FailedVersion>> Failed { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>AUTOUPDATE2: restart updates the next start checks (loaded, else rolled back).</summary>
    public Dictionary<string, StartCheck> Verify { get; } = new(StringComparer.OrdinalIgnoreCase);

    public const int MaxFailedPerMod = 16;

    public UpdateMode? ModeOf(string package) =>
        Modes.TryGetValue(package ?? "", out var word) ? UpdateSettings.ParsePerMod(word) : null;

    /// <summary>The failed-list row for <paramref name="version"/>, or null.</summary>
    public FailedVersion? FailedOf(string package, string version)
    {
        if (!Failed.TryGetValue(package ?? "", out var list)) return null;
        foreach (var f in list)
            if (UpdateVersions.Compare(f.Version, version) == 0) return f;
        return null;
    }

    /// <summary>Add (or refresh) a failed version. The newest <see cref="MaxFailedPerMod"/> are kept.</summary>
    public void AddFailed(string package, string version, string reason, DateTime utc)
    {
        if (string.IsNullOrEmpty(package) || string.IsNullOrEmpty(version)) return;
        if (!Failed.TryGetValue(package, out var list)) Failed[package] = list = new List<FailedVersion>();
        list.RemoveAll(f => UpdateVersions.Compare(f.Version, version) == 0);
        list.Add(new FailedVersion { Version = version, Reason = Clip(reason, 300), Utc = utc });
        while (list.Count > MaxFailedPerMod) list.RemoveAt(0);
    }

    private static string Clip(string? s, int n)
    {
        string t = s ?? "";
        return t.Length <= n ? t : t.Substring(0, n);
    }

    public static UpdatePins Parse(string? json) => Parse(json, out _);

    /// <summary>Parse; <paramref name="ok"/> is false for text that is not a JSON object (a torn or foreign file).</summary>
    public static UpdatePins Parse(string? json, out bool ok)
    {
        var p = new UpdatePins();
        ok = true;
        if (string.IsNullOrWhiteSpace(json)) return p;
        ok = false;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return p;
            ok = true;
            if (root.TryGetProperty("pins", out var pins) && pins.ValueKind == JsonValueKind.Object)
                foreach (var prop in pins.EnumerateObject())
                    if (prop.Value.ValueKind == JsonValueKind.String && prop.Name.Length > 0 && prop.Name.Length <= 200)
                    {
                        string v = prop.Value.GetString() ?? "";
                        if (v.Length > 0 && v.Length <= 64) p.Pins[prop.Name] = v;
                    }
            if (root.TryGetProperty("modes", out var modes) && modes.ValueKind == JsonValueKind.Object)
                foreach (var prop in modes.EnumerateObject())
                    if (prop.Value.ValueKind == JsonValueKind.String && prop.Name.Length > 0 && prop.Name.Length <= 200
                        && UpdateSettings.ParsePerMod(prop.Value.GetString()) is { } mode)
                        p.Modes[prop.Name] = UpdateSettings.Word(mode);
            if (root.TryGetProperty("failed", out var failed) && failed.ValueKind == JsonValueKind.Object)
                foreach (var prop in failed.EnumerateObject())
                {
                    if (prop.Value.ValueKind != JsonValueKind.Array || prop.Name.Length == 0 || prop.Name.Length > 200) continue;
                    foreach (var row in prop.Value.EnumerateArray())
                    {
                        if (row.ValueKind != JsonValueKind.Object) continue;
                        string v = Str(row, "version");
                        if (v.Length == 0 || v.Length > 64) continue;
                        p.AddFailed(prop.Name, v, Str(row, "reason"), Time(row, "utc"));
                    }
                }
            if (root.TryGetProperty("verify", out var verify) && verify.ValueKind == JsonValueKind.Object)
                foreach (var prop in verify.EnumerateObject())
                {
                    if (prop.Value.ValueKind != JsonValueKind.Object || prop.Name.Length == 0 || prop.Name.Length > 200) continue;
                    var c = new StartCheck
                    {
                        PluginId = Str(prop.Value, "pluginId"),
                        Version = Str(prop.Value, "version"),
                        Sha256 = Str(prop.Value, "sha256"),
                        Previous = Str(prop.Value, "previous"),
                        File = Str(prop.Value, "file"),
                        Utc = Time(prop.Value, "utc"),
                    };
                    if (c.PluginId.Length > 0 && c.Version.Length > 0) p.Verify[prop.Name] = c;
                }
        }
        catch { /* a torn file pins nothing */ }

        return p;
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static DateTime Time(JsonElement e, string name) =>
        DateTime.TryParse(Str(e, name), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t)
            ? t : default;

    private static string Stamp(DateTime utc) =>
        utc == default ? "" : utc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    public string ToJson()
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteNumber("schema", Schema);
            w.WriteStartObject("pins");
            foreach (var k in Sorted(Pins.Keys)) w.WriteString(k, Pins[k]);
            w.WriteEndObject();
            w.WriteStartObject("modes");
            foreach (var k in Sorted(Modes.Keys)) w.WriteString(k, Modes[k]);
            w.WriteEndObject();
            w.WriteStartObject("failed");
            foreach (var k in Sorted(Failed.Keys))
            {
                if (Failed[k].Count == 0) continue;
                w.WriteStartArray(k);
                foreach (var f in Failed[k])
                {
                    w.WriteStartObject();
                    w.WriteString("version", f.Version);
                    w.WriteString("reason", f.Reason);
                    w.WriteString("utc", Stamp(f.Utc));
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }
            w.WriteEndObject();
            w.WriteStartObject("verify");
            foreach (var k in Sorted(Verify.Keys))
            {
                var c = Verify[k];
                w.WriteStartObject(k);
                w.WriteString("pluginId", c.PluginId);
                w.WriteString("version", c.Version);
                w.WriteString("sha256", c.Sha256);
                w.WriteString("previous", c.Previous);
                w.WriteString("file", c.File);
                w.WriteString("utc", Stamp(c.Utc));
                w.WriteEndObject();
            }
            w.WriteEndObject();
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static List<string> Sorted(IEnumerable<string> keys)
    {
        var list = new List<string>(keys);
        list.Sort(StringComparer.OrdinalIgnoreCase);
        return list;
    }
}

/// <summary>AUTOUPDATE2: one version in a mod's failed list.</summary>
internal sealed class FailedVersion
{
    public string Version { get; set; } = "";
    public string Reason { get; set; } = "";
    public DateTime Utc { get; set; }

    public Dictionary<string, object?> Describe() => new()
    {
        ["version"] = Version,
        ["reason"] = Reason,
        ["utc"] = Utc == default ? null : Utc.ToString("o", CultureInfo.InvariantCulture),
    };
}

/// <summary>AUTOUPDATE2: a restart update to check after the next start (the plugin, the image expected, the copy to go back to).</summary>
internal sealed class StartCheck
{
    public string PluginId { get; set; } = "";

    /// <summary>The version written (the store's name for it).</summary>
    public string Version { get; set; } = "";

    public string Sha256 { get; set; } = "";

    /// <summary>The version it replaced (kept in the version store), or empty.</summary>
    public string Previous { get; set; } = "";

    /// <summary>The file name it was queued under (the reloader's record is keyed by its stem).</summary>
    public string File { get; set; } = "";

    public DateTime Utc { get; set; }
}

/// <summary>
/// AUTOUPDATE2: whether an update is applied automatically. Pinned: never. The mod's own mode, else the global one, must
/// be Automatic; a major version needs <b>Major versions</b> Automatic; an update that would install a new mod asks first
/// (decision 3); a version in the failed list is never retried automatically.
/// </summary>
internal static class UpdatePolicy
{
    public const string WhyKept = "kept at this version";
    public const string WhyNotify = "updates for it only notify";
    public const string WhyMajor = "a major version (Major versions is Notify)";

    public static void Apply(UpdateItem item, UpdateMode global, UpdatePins? file, bool majorsAutomatic)
    {
        var perMod = file?.ModeOf(item.Package);
        item.PerMod = perMod is { } m ? UpdateSettings.Word(m) : "";
        var failed = file?.FailedOf(item.Package, item.Latest);
        item.FailedBefore = failed?.Reason is { Length: > 0 } reason ? reason : failed != null ? "failed to load" : "";
        item.NotAutoReason = WhyNot(item, perMod ?? global, majorsAutomatic);
        item.Auto = item.NotAutoReason.Length == 0;
    }

    public static string WhyNot(UpdateItem item, UpdateMode effective, bool majorsAutomatic)
    {
        if (item.Pinned) return WhyKept;
        if (effective != UpdateMode.Automatic) return WhyNotify;
        if (item.Major && !majorsAutomatic) return WhyMajor;
        if (item.NewDependencies.Count > 0) return "it needs " + string.Join(", ", item.NewDependencies) + ", which is not installed (asks first)";
        if (item.FailedBefore.Length > 0) return item.Latest + " failed to load before";
        return "";
    }
}

/// <summary>AUTOUPDATE2: the facts the apply window reads (live: the world, the join flow, the loader, the store).</summary>
internal sealed class ApplyWindowFacts
{
    public bool AtTitle { get; set; }
    public bool InWorld { get; set; }
    public bool Joining { get; set; }
    public bool PreJoinHolding { get; set; }
    public bool Hosting { get; set; }
    public bool LoaderBusy { get; set; }
    public bool StoreBusy { get; set; }
    public bool RestoreDue { get; set; }
    public bool RunnerBusy { get; set; }
    public bool StoreMissing { get; set; }
}

/// <summary>
/// AUTOUPDATE2: when an automatic update may be applied: at the title screen (which is also right after leaving a server)
/// with no client world, no join in progress or held, no hosted private game, no Core swap or plugin reload in flight, the
/// version store idle and done restoring the player's versions after a leave, and no other update running.
/// </summary>
internal static class ApplyWindow
{
    /// <summary>Empty when the window is open, else why it is closed.</summary>
    public static string Closed(ApplyWindowFacts f)
    {
        if (f == null) return "no facts";
        if (f.InWorld) return "in a world";
        if (f.Joining) return "a join is in progress";
        if (f.PreJoinHolding) return "the pre-join prompt holds a join";
        if (f.Hosting) return "a private game is hosted";
        if (!f.AtTitle) return "not at the title screen";
        if (f.LoaderBusy) return "the loader is busy (a Core swap or a plugin reload)";
        if (f.StoreMissing) return "the version store is not available";
        if (f.StoreBusy) return "a version swap is being finished";
        if (f.RestoreDue) return "your versions are still being restored after leaving a server";
        if (f.RunnerBusy) return "another update is running";
        return "";
    }
}

/// <summary>
/// AUTOUPDATE1: <c>BepInEx/config/progenitor-server-requirements.json</c>: what each server asked at its last handshake
/// (mod id, version, rule), keyed by the address this client dialed. The pre-join prompt reads it before a connect, the
/// only moment it can cap an update at the server's version. At most <see cref="MaxServers"/> servers, oldest dropped.
/// Mod compatibility C3 adds, all optional: the server's <c>matching</c> (<c>exact</c>, <c>minor</c>; from its request
/// bits) and per mod its catalog's <c>peers</c> and <c>match</c> (<see cref="NoteCatalog"/>). The schema stays 1.
/// </summary>
internal sealed class ServerRequirementMemory
{
    public const string FileName = "progenitor-server-requirements.json";
    public const int Schema = 1;
    public const int MaxServers = 32;
    public const int MaxMods = 256;

    private sealed class Server
    {
        public DateTime SeenUtc;
        public List<ServerRequirement> Mods = new();
    }

    private readonly Dictionary<string, Server> _servers = new(StringComparer.OrdinalIgnoreCase);

    public int Count => _servers.Count;

    /// <summary>The key for an address: <c>host:port</c>, lower case.</summary>
    public static string KeyOf(string? host, int port) =>
        string.IsNullOrWhiteSpace(host) || port <= 0 ? "" : host!.Trim().ToLowerInvariant() + ":" + port.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Review item 4: the key for a dialed server, by transport. A peer-to-peer join is keyed on the target's id
    /// (<c>steamp2p://76561198000000001</c>, <c>eosp2p://&lt;id&gt;</c>); an address join on protocol plus address and port
    /// (<c>steamipv4://127.0.0.1:9976</c>). Empty when nothing identifies the server.
    /// </summary>
    public static string KeyFor(string? protocol, string? host, int port, ulong steamId, string? eosId)
    {
        string p = (protocol ?? "").Trim().ToLowerInvariant();
        if (p.EndsWith("://", StringComparison.Ordinal)) p = p.Substring(0, p.Length - 3);
        if (p.Contains("steamp2p") && steamId != 0) return "steamp2p://" + steamId.ToString(CultureInfo.InvariantCulture);
        if (p.Contains("eos") && !string.IsNullOrWhiteSpace(eosId)) return "eosp2p://" + eosId!.Trim();
        string address = KeyOf(host, port);
        if (address.Length == 0)
        {
            if (steamId != 0) return "steamp2p://" + steamId.ToString(CultureInfo.InvariantCulture);
            if (!string.IsNullOrWhiteSpace(eosId)) return "eosp2p://" + eosId!.Trim();
            return "";
        }

        return (p.Length > 0 ? p : "steamipv4") + "://" + address;
    }

    public IReadOnlyList<ServerRequirement>? Find(string key)
    {
        if (string.IsNullOrEmpty(key)) return null;
        return _servers.TryGetValue(key, out var s) ? s.Mods : null;
    }

    public void Note(string key, IEnumerable<ServerRequirement> mods, DateTime nowUtc)
    {
        if (string.IsNullOrEmpty(key)) return;
        _servers.TryGetValue(key, out var before);
        var list = new List<ServerRequirement>();
        foreach (var m in mods ?? Array.Empty<ServerRequirement>())
        {
            if (m == null || m.ModId.Length == 0) continue;
            var row = new ServerRequirement
            {
                ModId = m.ModId,
                Version = m.Version ?? "",
                Rule = m.Rule,
                Peers = m.Peers ?? "",
                Match = ServerVersionCheck.NormalizeMatch(m.Match),
                Matching = ServerVersionCheck.NormalizeMatch(m.Matching),
            };
            // C3: what the last catalog said about this mod at this version is kept until this session's catalog
            // refreshes it (a request arrives before the catalog; the kept-copy choice at the handshake reads it).
            if (before != null)
                foreach (var old in before.Mods)
                {
                    if (!string.Equals(old.ModId, row.ModId, StringComparison.OrdinalIgnoreCase)
                        || !string.Equals(old.Version, row.Version, StringComparison.OrdinalIgnoreCase)) continue;
                    // The server build's range belongs to its version and stays.
                    if (row.Peers.Length == 0) row.Peers = old.Peers;
                    // Review fix: the match only while the request says the same thing (rule byte and mode). A changed rule
                    // or mode means an admin changed how the server judges this mod, so the old catalog's match is stale.
                    if (old.Rule == row.Rule && string.Equals(old.Matching, row.Matching, StringComparison.Ordinal) && row.Match.Length == 0)
                        row.Match = old.Match;
                }
            list.Add(row);
            if (list.Count >= MaxMods) break;
        }

        _servers[key] = new Server { SeenUtc = nowUtc, Mods = list };
        while (_servers.Count > MaxServers)
        {
            string? oldest = null;
            DateTime when = DateTime.MaxValue;
            foreach (var kv in _servers)
                if (kv.Value.SeenUtc < when) { when = kv.Value.SeenUtc; oldest = kv.Key; }
            if (oldest == null) break;
            _servers.Remove(oldest);
        }
    }

    /// <summary>
    /// C3: a verified catalog of the server at <paramref name="key"/> arrived: its per-mod <c>peers</c> and <c>match</c>
    /// are merged into what that server's request asked (<see cref="ServerRequirements.Merge"/>). A server without a
    /// remembered request is not created. Returns true when anything changed.
    /// </summary>
    public bool NoteCatalog(string key, IEnumerable<ServerRequirement>? catalog, string? matching = null)
    {
        if (string.IsNullOrEmpty(key) || (catalog == null && matching == null)) return false;
        if (!_servers.TryGetValue(key, out var s)) return false;
        // C3: the catalog's Version matching (re-signed when an admin changes it) replaces the join's request bits.
        int changed = catalog == null ? 0 : ServerRequirements.Merge(s.Mods, catalog);
        changed += ServerRequirements.ApplyMatching(s.Mods, matching);
        return changed > 0;
    }

    public static ServerRequirementMemory Parse(string? json)
    {
        var m = new ServerRequirementMemory();
        if (string.IsNullOrWhiteSpace(json)) return m;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("servers", out var servers) || servers.ValueKind != JsonValueKind.Object) return m;
            foreach (var s in servers.EnumerateObject())
            {
                if (s.Value.ValueKind != JsonValueKind.Object) continue;
                var server = new Server();
                if (s.Value.TryGetProperty("seen", out var seen) && seen.ValueKind == JsonValueKind.String
                    && DateTime.TryParse(seen.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t))
                    server.SeenUtc = t;
                string matching = ServerVersionCheck.NormalizeMatch(Str(s.Value, "matching"));
                if (s.Value.TryGetProperty("mods", out var mods) && mods.ValueKind == JsonValueKind.Array)
                    foreach (var mod in mods.EnumerateArray())
                    {
                        if (mod.ValueKind != JsonValueKind.Object || server.Mods.Count >= MaxMods) continue;
                        string id = Str(mod, "id");
                        if (id.Length == 0) continue;
                        // C3 fields are optional: a file written before them loads as before.
                        server.Mods.Add(new ServerRequirement
                        {
                            ModId = id,
                            Version = Str(mod, "version"),
                            Rule = RuleOf(Str(mod, "rule")),
                            Peers = Str(mod, "peers").Trim(),
                            Match = ServerVersionCheck.NormalizeMatch(Str(mod, "match")),
                            Matching = matching,
                        });
                    }
                m._servers[s.Name] = server;
                if (m._servers.Count >= MaxServers) break;
            }
        }
        catch { /* a torn file remembers nothing */ }

        return m;
    }

    public string ToJson()
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteNumber("schema", Schema);
            w.WriteStartObject("servers");
            foreach (var kv in _servers)
            {
                w.WriteStartObject(kv.Key);
                w.WriteString("seen", kv.Value.SeenUtc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture));
                string matching = "";
                foreach (var mod in kv.Value.Mods) if (mod.Matching.Length > 0) { matching = mod.Matching; break; }
                if (matching.Length > 0) w.WriteString("matching", matching);
                w.WriteStartArray("mods");
                foreach (var mod in kv.Value.Mods)
                {
                    w.WriteStartObject();
                    w.WriteString("id", mod.ModId);
                    w.WriteString("version", mod.Version);
                    w.WriteString("rule", RuleWord(mod.Rule));
                    if (mod.Peers.Length > 0) w.WriteString("peers", mod.Peers);
                    if (mod.Match.Length > 0) w.WriteString("match", mod.Match);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndObject();
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    public static string RuleWord(NetworkVersionRule rule) => rule switch
    {
        NetworkVersionRule.Minor => "minor",
        NetworkVersionRule.None => "none",
        _ => "exact",
    };

    public static NetworkVersionRule RuleOf(string word) => word switch
    {
        "minor" => NetworkVersionRule.Minor,
        "none" => NetworkVersionRule.None,
        _ => NetworkVersionRule.Exact,
    };

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}
