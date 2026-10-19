using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using Progenitor.Persistence;

namespace Progenitor.Install;

/// <summary>One Thunderstore dependency of an ordinary package and how (or whether) it is satisfied.</summary>
internal readonly struct OrdinaryDependency
{
    public OrdinaryDependency(string package, bool satisfied, string satisfiedBy, string? refusal)
    {
        Package = package ?? "";
        Satisfied = satisfied;
        SatisfiedBy = satisfiedBy ?? "";
        Refusal = refusal;
    }

    /// <summary><c>Namespace-Name</c> — the package, without the version.</summary>
    public string Package { get; }
    public bool Satisfied { get; }

    /// <summary><c>bepinex</c> | <c>ledger</c> | <c>loaded</c> | <c>missing</c>.</summary>
    public string SatisfiedBy { get; }
    public string? Refusal { get; }
}

/// <summary>
/// One inspected plugin identity, remembered so a later dependency check can answer "which plugin
/// GUID does this package contain" from a prior inspection rather than from the package name.
/// </summary>
internal sealed class OrdinaryIdentityRecord
{
    public OrdinaryIdentityRecord(
        string ns,
        string name,
        string version,
        string pluginGuid,
        string pluginVersion,
        string sha256,
        string? loadsIn = null)
    {
        Namespace = ns ?? "";
        Name = name ?? "";
        Version = version ?? "";
        PluginGuid = pluginGuid ?? "";
        PluginVersion = pluginVersion ?? "";
        Sha256 = sha256 ?? "";
        LoadsIn = string.IsNullOrWhiteSpace(loadsIn) ? "" : loadsIn.Trim();
    }

    public string Namespace { get; }
    public string Name { get; }
    public string Version { get; }
    public string PluginGuid { get; }
    public string PluginVersion { get; }
    public string Sha256 { get; }

    /// <summary>INSTORD2: the <c>[BepInProcess]</c> answer the inspection read, so the drawer can say it at rest. Empty when unknown.</summary>
    public string LoadsIn { get; }
}

/// <summary>
/// <c>BepInEx/progenitor-cache/identity/&lt;namespace&gt;/&lt;name&gt;/&lt;version&gt;.json</c>: the plugin
/// GUID a package's DLL declared, written after an inspection read it from the bytes. A cache miss
/// means "not known", never "guess from the name".
/// </summary>
internal sealed class OrdinaryIdentityCache
{
    public const string CacheFolderName = "progenitor-cache";
    public const string IdentityFolderName = "identity";

    private readonly string _root;

    public OrdinaryIdentityCache(string bepInExRoot)
    {
        if (string.IsNullOrWhiteSpace(bepInExRoot)) throw new ArgumentException("BepInEx root is required.", nameof(bepInExRoot));
        _root = Path.Combine(Path.GetFullPath(bepInExRoot), CacheFolderName, IdentityFolderName);
    }

    public string Root => _root;

    public string PathFor(string ns, string name, string version) =>
        Path.Combine(_root, Sanitize(ns), Sanitize(name), Sanitize(version) + ".json");

    /// <summary>Write (or replace) the identity of one inspected package version. Best-effort.</summary>
    public bool Write(OrdinaryIdentityRecord record)
    {
        if (record == null) throw new ArgumentNullException(nameof(record));
        if (record.Namespace.Length == 0 || record.Name.Length == 0 || record.Version.Length == 0) return false;
        try
        {
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
            {
                w.WriteStartObject();
                w.WriteNumber("schema", 1);
                w.WriteString("namespace", record.Namespace);
                w.WriteString("name", record.Name);
                w.WriteString("version", record.Version);
                w.WriteString("pluginGuid", record.PluginGuid);
                w.WriteString("pluginVersion", record.PluginVersion);
                w.WriteString("sha256", record.Sha256);
                w.WriteString("loadsIn", record.LoadsIn);
                w.WriteString(
                    "inspectedUtc",
                    DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
                w.WriteEndObject();
            }

            InstallStaging.AtomicWrite(PathFor(record.Namespace, record.Name, record.Version), ms.ToArray());
            return true;
        }
        catch
        {
            return false;
        }
    }

    public OrdinaryIdentityRecord? TryRead(string ns, string name, string version)
    {
        try
        {
            string path = PathFor(ns, name, version);
            if (!File.Exists(path)) return null;
            using var doc = JsonFiles.ParseFile(path);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            return new OrdinaryIdentityRecord(
                Read(root, "namespace"), Read(root, "name"), Read(root, "version"),
                Read(root, "pluginGuid"), Read(root, "pluginVersion"), Read(root, "sha256"),
                Read(root, "loadsIn"));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Every cached version of one package, newest file last. Empty when nothing was inspected.</summary>
    public IReadOnlyList<OrdinaryIdentityRecord> TryReadAll(string ns, string name)
    {
        var list = new List<OrdinaryIdentityRecord>();
        try
        {
            string dir = Path.Combine(_root, Sanitize(ns), Sanitize(name));
            if (!Directory.Exists(dir)) return list;
            string[] files = Directory.GetFiles(dir, "*.json", SearchOption.TopDirectoryOnly);
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < files.Length; i++)
            {
                string version = Path.GetFileNameWithoutExtension(files[i]);
                var record = TryRead(ns, name, version);
                if (record != null) list.Add(record);
            }
        }
        catch { /* a cache we cannot read is a cache miss */ }

        return list;
    }

    private static string Read(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    /// <summary>One path segment, so a listing name can never walk out of the cache folder.</summary>
    internal static string Sanitize(string value)
    {
        string v = (value ?? "").Trim();
        var sb = new StringBuilder(v.Length);
        for (int i = 0; i < v.Length; i++)
        {
            char c = v[i];
            bool ok = c == '_' || c == '-' || c == '.' || char.IsLetterOrDigit(c);
            sb.Append(ok ? c : '_');
        }

        string s = sb.ToString().Trim('.');
        return s.Length == 0 ? "_" : s;
    }
}

/// <summary>What the dependency check may consult on one target. Injected so tests never touch a game folder.</summary>
internal sealed class OrdinaryDependencyView
{
    public OrdinaryDependencyView(
        IReadOnlyList<string>? ledgerSources = null,
        IReadOnlyList<string>? loadedPluginGuids = null,
        OrdinaryIdentityCache? cache = null,
        IReadOnlyList<string>? closurePackages = null)
    {
        LedgerSources = ledgerSources ?? Array.Empty<string>();
        LoadedPluginGuids = loadedPluginGuids ?? Array.Empty<string>();
        Cache = cache;
        ClosurePackages = closurePackages ?? Array.Empty<string>();
    }

    /// <summary>The <c>source</c> field of every ledgered install on the target (<c>package:Ns-Name@1.2.3</c>).</summary>
    public IReadOnlyList<string> LedgerSources { get; }

    /// <summary>The <c>[BepInPlugin]</c> GUIDs the target has loaded.</summary>
    public IReadOnlyList<string> LoadedPluginGuids { get; }

    public OrdinaryIdentityCache? Cache { get; }

    /// <summary>
    /// DEPS1: the <c>Namespace-Name</c> packages this same install is going to place before the one
    /// being planned (the dependency closure). A dependency in this set is not "already on the
    /// target" — it is being installed in the same ordered set — so the planner stops refusing for
    /// it. Empty for every plan that was not built through
    /// <see cref="InstallCatalogService.PlanWithDependenciesAsync"/>, which is the pre-DEPS1 rule.
    /// </summary>
    public IReadOnlyList<string> ClosurePackages { get; }
}

/// <summary>
/// INSTORD1 dependency handling for the ordinary-package route. <c>BepInEx-BepInExPack_V_Rising</c>
/// is satisfied by the BepInEx that is already running this process. Every other Thunderstore
/// dependency must already be on the target: a Progenitor-ledgered install of that package, or a
/// loaded plugin whose GUID a prior inspection of <em>that package</em> recorded. A package name is
/// never read as a plugin GUID, and an uncached package is refused rather than assumed.
/// </summary>
internal static class OrdinaryDependencies
{
    public const string BepInExNamespace = "BepInEx";
    public const string BepInExPackPrefix = "BepInExPack_V_Rising";

    public const string BySatisfiedBepInEx = "bepinex";
    public const string BySatisfiedLedger = "ledger";
    public const string BySatisfiedLoaded = "loaded";
    public const string BySatisfiedMissing = "missing";

    /// <summary>DEPS1: this dependency is a member of the same install's closure and is written before the root.</summary>
    public const string BySatisfiedClosure = "closure";

    /// <summary>
    /// DEPS1: the one rule that answers "is this dependency satisfied for this install", shared by
    /// <see cref="Evaluate"/> and <see cref="PackageDependencyClosure"/> so the planner and the closure can
    /// never disagree about a package. The order is fixed: the BepInEx pack is the loader that is
    /// already running, then a Progenitor-ledgered install on the target, then a loaded plugin whose
    /// GUID a prior inspection of <em>that package</em> recorded, then this install's own closure.
    /// </summary>
    public static bool SatisfiedOn(
        OrdinaryDependencyView? view,
        string ns,
        string name,
        string version,
        out string satisfiedBy)
    {
        view ??= new OrdinaryDependencyView();
        string package = (ns ?? "") + "-" + (name ?? "");
        if (IsBepInExPack(ns ?? "", name ?? ""))
        {
            satisfiedBy = BySatisfiedBepInEx;
            return true;
        }

        if (LedgerHas(view.LedgerSources, package))
        {
            satisfiedBy = BySatisfiedLedger;
            return true;
        }

        // POLISH4 item 2: a row an older build wrote for this package under the *plugin id* is still
        // a Progenitor-ledgered install of it. Only an identity a prior inspection of that package
        // recorded counts, exactly as in LoadedHas — never the package name read as a GUID.
        if (LedgerHasByIdentity(view, ns ?? "", name ?? "", version ?? ""))
        {
            satisfiedBy = BySatisfiedLedger;
            return true;
        }

        if (LoadedHas(view, ns ?? "", name ?? "", version ?? ""))
        {
            satisfiedBy = BySatisfiedLoaded;
            return true;
        }

        if (NamesPackage(view.ClosurePackages, package))
        {
            satisfiedBy = BySatisfiedClosure;
            return true;
        }

        satisfiedBy = BySatisfiedMissing;
        return false;
    }

    /// <summary>Does this list name <paramref name="package"/> (a bare <c>Namespace-Name</c>, not a ledger source)?</summary>
    public static bool NamesPackage(IReadOnlyList<string>? packages, string package)
    {
        if (packages == null) return false;
        for (int i = 0; i < packages.Count; i++)
        {
            if (string.Equals((packages[i] ?? "").Trim(), package, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    public static IReadOnlyList<OrdinaryDependency> Evaluate(
        IReadOnlyList<string>? thunderstoreDependencies,
        OrdinaryDependencyView? view)
    {
        var list = new List<OrdinaryDependency>();
        if (thunderstoreDependencies == null || thunderstoreDependencies.Count == 0) return list;
        view ??= new OrdinaryDependencyView();

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < thunderstoreDependencies.Count; i++)
        {
            if (!TrySplit(thunderstoreDependencies[i], out string ns, out string name, out string version))
                continue;
            string package = ns + "-" + name;
            if (!seen.Add(package)) continue;

            if (SatisfiedOn(view, ns, name, version, out string satisfiedBy))
            {
                list.Add(new OrdinaryDependency(package, true, satisfiedBy, null));
                continue;
            }

            list.Add(new OrdinaryDependency(package, false, BySatisfiedMissing, InstallReasons.OrdinaryRequires(package)));
        }

        return list;
    }

    /// <summary>The first refusal among <paramref name="dependencies"/>, or null when every one is satisfied.</summary>
    public static string? FirstRefusal(IReadOnlyList<OrdinaryDependency> dependencies)
    {
        for (int i = 0; i < dependencies.Count; i++)
        {
            if (!dependencies[i].Satisfied) return dependencies[i].Refusal ?? InstallReasons.OrdinaryRequires(dependencies[i].Package);
        }

        return null;
    }

    /// <summary>The BepInEx pack itself: satisfied by the BepInEx that loaded Progenitor.</summary>
    public static bool IsBepInExPack(string ns, string name) =>
        string.Equals(ns, BepInExNamespace, StringComparison.OrdinalIgnoreCase)
        && name.StartsWith(BepInExPackPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary><c>Namespace-Name-1.2.3</c> → its three parts. A name may itself contain hyphens; the last part is the version.</summary>
    public static bool TrySplit(string? dependency, out string ns, out string name, out string version)
    {
        ns = name = version = "";
        string d = (dependency ?? "").Trim();
        if (d.Length == 0) return false;
        var parts = d.Split('-');
        if (parts.Length < 3) return false;
        ns = parts[0].Trim();
        version = parts[parts.Length - 1].Trim();
        name = string.Join("-", parts, 1, parts.Length - 2).Trim();
        return ns.Length > 0 && name.Length > 0 && version.Length > 0;
    }

    /// <summary>The ledger source this route writes for a listing: <c>package:Namespace-Name@version</c>.</summary>
    public static string LedgerSource(string packageKey, string version) =>
        ServerInstallLedger.PackageSourcePrefix + (packageKey ?? "") + "@" + (string.IsNullOrWhiteSpace(version) ? "0" : version.Trim());

    /// <summary>Does a ledger row name this package, at any version?</summary>
    public static bool LedgerHas(IReadOnlyList<string> sources, string package)
    {
        string prefix = ServerInstallLedger.PackageSourcePrefix + package + "@";
        for (int i = 0; i < sources.Count; i++)
        {
            if ((sources[i] ?? "").StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    /// <summary>
    /// POLISH4 item 2: a ledger row spelled with the plugin GUID a prior inspection of
    /// <em>that package</em> recorded — the spelling the first-class restart route wrote before this
    /// job. A cache miss is "not ledgered", never a guess from the package name.
    /// </summary>
    public static bool LedgerHasByIdentity(OrdinaryDependencyView view, string ns, string name, string version)
    {
        if (view?.Cache == null || view.LedgerSources.Count == 0) return false;
        var candidates = new List<OrdinaryIdentityRecord>();
        var exact = view.Cache.TryRead(ns, name, version);
        if (exact != null) candidates.Add(exact);
        var all = view.Cache.TryReadAll(ns, name);
        for (int i = 0; i < all.Count; i++) candidates.Add(all[i]);

        for (int i = 0; i < candidates.Count; i++)
        {
            string guid = candidates[i].PluginGuid;
            if (guid.Length == 0) continue;
            if (LedgerHas(view.LedgerSources, guid)) return true;
        }

        return false;
    }

    /// <summary>A loaded plugin whose GUID a prior inspection of <em>that package</em> recorded. Never the package name.</summary>
    public static bool LoadedHas(OrdinaryDependencyView view, string ns, string name, string version)
    {
        if (view.Cache == null || view.LoadedPluginGuids.Count == 0) return false;

        // Only an identity a prior inspection of that package recorded counts. The exact version
        // first, then any inspected version of the same package — never the package name itself.
        var candidates = new List<OrdinaryIdentityRecord>();
        var exact = view.Cache.TryRead(ns, name, version);
        if (exact != null) candidates.Add(exact);
        var all = view.Cache.TryReadAll(ns, name);
        for (int i = 0; i < all.Count; i++) candidates.Add(all[i]);

        for (int i = 0; i < candidates.Count; i++)
        {
            string guid = candidates[i].PluginGuid;
            if (guid.Length == 0) continue;
            for (int j = 0; j < view.LoadedPluginGuids.Count; j++)
            {
                if (string.Equals(view.LoadedPluginGuids[j], guid, StringComparison.OrdinalIgnoreCase)) return true;
            }
        }

        return false;
    }
}
