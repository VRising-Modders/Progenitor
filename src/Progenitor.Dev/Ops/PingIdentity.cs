using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;

namespace Progenitor.Dev.Ops;

/// <summary>
/// Game-free half of ping's <c>gameIdentity</c> and <c>images</c> fields.
///
/// Identity composition is <c>PrefabEditBuildIdentity.Compose</c> in Core (internal, no
/// InternalsVisibleTo for Dev). This type invokes that method rather than copying the format.
/// Live identity is <c>PrefabEditWireApply.LocalIdentity</c> on the active Core only: the
/// assembly Dev is bound to, verified against Bootstrap <c>CoreLoader.AlcName</c> (the same
/// name ping reports under <c>reload.alcName</c>), or Bootstrap's public
/// <c>CoreLoader.Assembly</c> when that is the published generation. A retained older
/// collectible Core is never queried. Image rows are shaped here; the live list is the
/// process's existing reload bookkeeping (Core generation / last-loaded bytes, ReloaderHost /
/// PluginReloader <c>ImageSha</c>), never a disk rehash of the reload folder.
/// </summary>
internal static class PingIdentity
{
    internal const string UnknownIdentity = "";
    internal const string CoreImageName = "Core";

    private static readonly object CoreShaGate = new();
    private static int _cachedCoreGeneration = int.MinValue;
    private static byte[]? _cachedCoreBytes;
    private static string? _cachedCoreSha;

    /// <summary>
    /// The facts <see cref="PrefabEditBuildIdentity"/> composes. Same shape as Core's
    /// <c>PrefabGameVersionFacts</c> so tests can drive the real Compose without a game.
    /// </summary>
    internal readonly struct GameVersionFacts
    {
        public GameVersionFacts(
            string? name,
            int major,
            int minor,
            int patch,
            int revision,
            int vcsRevision,
            int build,
            long unixTimestamp,
            int networkVersion)
        {
            Name = name ?? "";
            Major = major;
            Minor = minor;
            Patch = patch;
            Revision = revision;
            VcsRevision = vcsRevision;
            Build = build;
            UnixTimestamp = unixTimestamp;
            NetworkVersion = networkVersion;
        }

        public string Name { get; }
        public int Major { get; }
        public int Minor { get; }
        public int Patch { get; }
        public int Revision { get; }
        public int VcsRevision { get; }
        public int Build { get; }
        public long UnixTimestamp { get; }
        public int NetworkVersion { get; }
    }

    /// <summary>One loaded Progenitor image for ping's <c>images</c> array.</summary>
    internal readonly struct Image
    {
        public Image(string name, string? sha256, int? generation = null, string? version = null)
        {
            Name = name ?? "";
            Sha256 = string.IsNullOrEmpty(sha256) ? null : sha256;
            Generation = generation;
            Version = string.IsNullOrEmpty(version) ? null : version;
        }

        public string Name { get; }
        public string? Sha256 { get; }
        public int? Generation { get; }
        /// <summary>The assembly's informational version, <c>year.month.release[+revision]</c> (docs/versioning.md).</summary>
        public string? Version { get; }
    }

    /// <summary>
    /// One loaded Core generation that can answer identity, keyed by the Bootstrap ALC name
    /// (<c>progenitor-core:N</c>). <see cref="SelectActiveCore"/> / <see cref="ResolveIdentity"/>
    /// pick by that name, never by enumeration order.
    /// </summary>
    internal readonly struct CoreCandidate
    {
        public CoreCandidate(string? alcName, string? identity)
        {
            AlcName = alcName ?? "";
            Identity = identity;
        }

        public string AlcName { get; }
        public string? Identity { get; }
    }

    /// <summary>
    /// Core's identity string for these facts, or null when Compose is unreachable, throws, or
    /// answers unknown. Never a guessed format.
    /// </summary>
    internal static string? Compose(GameVersionFacts facts)
    {
        try
        {
            var factsType = FindType("Progenitor.Content.PrefabGameVersionFacts");
            var identityType = FindType("Progenitor.Content.PrefabEditBuildIdentity");
            if (factsType == null || identityType == null) return null;

            var ctor = factsType.GetConstructor(new[]
            {
                typeof(string), typeof(int), typeof(int), typeof(int), typeof(int),
                typeof(int), typeof(int), typeof(long), typeof(int),
            });
            var compose = identityType.GetMethod(
                "Compose", BindingFlags.Public | BindingFlags.Static, null, new[] { factsType }, null);
            if (ctor == null || compose == null) return null;

            object boxed = ctor.Invoke(new object[]
            {
                facts.Name, facts.Major, facts.Minor, facts.Patch, facts.Revision,
                facts.VcsRevision, facts.Build, facts.UnixTimestamp, facts.NetworkVersion,
            });
            var result = compose.Invoke(null, new[] { boxed }) as string;
            return string.IsNullOrEmpty(result) ? null : result;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// JSON objects for ping: <c>{name, sha256, version}</c>, plus <c>generation</c> only when set.
    /// Empty sha256 and version become null. Rows with no name are dropped.
    /// </summary>
    internal static IReadOnlyList<object> ShapeImages(IReadOnlyList<Image> rows)
    {
        var list = new List<object>(rows?.Count ?? 0);
        if (rows == null) return list;
        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (string.IsNullOrEmpty(row.Name)) continue;
            string? sha = row.Sha256;
            if (row.Generation is int gen)
                list.Add(new { name = row.Name, sha256 = sha, version = row.Version, generation = gen });
            else
                list.Add(new { name = row.Name, sha256 = sha, version = row.Version });
        }
        return list;
    }

    /// <summary>
    /// The candidate whose ALC name equals the Bootstrap-owned current generation, or null
    /// when that name is unknown/empty or no candidate matches it. Two hits that disagree on
    /// identity also return null. Never the first enumerable match, never a constructed
    /// <c>progenitor-core:N</c> guess.
    /// </summary>
    internal static CoreCandidate? SelectActiveCore(
        IReadOnlyList<CoreCandidate> candidates,
        string? activeAlcName)
    {
        if (string.IsNullOrEmpty(activeAlcName) || candidates == null)
            return null;

        int found = -1;
        for (int i = 0; i < candidates.Count; i++)
        {
            var alc = candidates[i].AlcName;
            if (alc.Length == 0) continue;
            if (!string.Equals(alc, activeAlcName, StringComparison.Ordinal))
                continue;
            if (found >= 0)
            {
                if (!IdentitiesEqual(candidates[found].Identity, candidates[i].Identity))
                    return null;
                if (string.IsNullOrEmpty(candidates[found].Identity)
                    && !string.IsNullOrEmpty(candidates[i].Identity))
                    found = i;
                continue;
            }
            found = i;
        }
        return found >= 0 ? candidates[found] : null;
    }

    /// <summary>
    /// Identity string of the active Core generation. Null when the active generation is
    /// unknown, no candidate matches it, or the matching candidate answers unknown/empty —
    /// even if an older candidate could answer.
    /// </summary>
    internal static string? ResolveIdentity(
        IReadOnlyList<CoreCandidate> candidates,
        string? activeAlcName)
    {
        var selected = SelectActiveCore(candidates, activeAlcName);
        if (selected == null) return null;
        var id = selected.Value.Identity;
        return string.IsNullOrEmpty(id) ? null : id;
    }

    /// <summary>
    /// Live <c>PrefabEditWireApply.LocalIdentity</c> from the active Core, or null when that
    /// reader is missing, throws, answers unknown, or the bound assembly is not Bootstrap's
    /// current generation. Ping must not fail if this does. Does not retain Type / Assembly /
    /// MethodInfo across calls.
    /// </summary>
    internal static string? TryLiveIdentity() => TryLiveIdentity(boundCore: null);

    /// <summary>
    /// <paramref name="boundCore"/> is the Core assembly this Dev compile-referenced
    /// (<c>typeof</c> of a public Core type). It is used only when its ALC name equals
    /// <c>CoreLoader.AlcName</c>. Bootstrap's public <c>CoreLoader.Assembly</c> is the
    /// other candidate, labeled with that same AlcName.
    /// </summary>
    internal static string? TryLiveIdentity(Assembly? boundCore)
    {
        try
        {
            string? activeAlc = TryReadActiveAlcName();
            Assembly? loaderAsm = TryReadActiveCoreAssembly();
            var candidates = new List<CoreCandidate>(2);
            int beforeBound = candidates.Count;
            AddIdentityCandidate(candidates, boundCore, alcOverride: null, activeAlc);
            bool addedBound = candidates.Count > beforeBound;
            if (loaderAsm != null && (!addedBound || !ReferenceEquals(loaderAsm, boundCore)))
                AddIdentityCandidate(candidates, loaderAsm, alcOverride: activeAlc, activeAlc);
            return ResolveIdentity(candidates, activeAlc);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Core (with generation) then each reloaded plugin image the process already has a record
    /// for. Null when none of that bookkeeping is reachable — not an empty guess.
    /// </summary>
    internal static IReadOnlyList<object>? TryLiveImages()
    {
        try
        {
            var rows = new List<Image>();
            bool any = false;

            if (TryReadCoreImage(out var core))
            {
                rows.Add(core);
                any = true;
            }

            // AUTOUPDATE5: the running Bootstrap image (hashed by Bootstrap at Load, before any promote), so a deploy or a
            // self-update can be checked on both halves. Absent with a Bootstrap older than phase 5.
            if (TryReadBootstrapImage(out var bootstrap))
            {
                rows.Add(bootstrap);
                any = true;
            }

            var plugins = TryReadReloadedImages();
            if (plugins != null)
            {
                any = true;
                plugins.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
                for (int i = 0; i < plugins.Count; i++)
                    rows.Add(plugins[i]);
            }

            EnsureDevRow(rows, ref any);
            if (!any) return null;
            return ShapeImages(rows);
        }
        catch
        {
            return null;
        }
    }

    internal const string BootstrapImageName = "Bootstrap";

    private static bool TryReadBootstrapImage(out Image image)
    {
        image = default;
        try
        {
            var type = FindType("Progenitor.Bootstrap.BootIdentity");
            var sha = type?.GetProperty("Sha256", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) as string;
            if (string.IsNullOrEmpty(sha)) return false;
            image = new Image(BootstrapImageName, sha, version: InformationalVersion(type!.Assembly));
            return true;
        }
        catch
        {
            image = default;
            return false;
        }
    }

    private static bool TryReadCoreImage(out Image image)
    {
        image = default;
        try
        {
            var type = FindType("Progenitor.Bootstrap.CoreLoader");
            if (type == null) return false;
            var genProp = type.GetProperty("Generation", BindingFlags.Public | BindingFlags.Static);
            int? generation = genProp?.GetValue(null) is int g ? g : null;
            string? sha = TryCoreSha256(type, generation);
            var assembly = type.GetProperty("Assembly", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) as Assembly;
            image = new Image(CoreImageName, sha, generation, InformationalVersion(assembly));
            return true;
        }
        catch
        {
            image = default;
            return false;
        }
    }

    private static string? TryCoreSha256(Type coreLoader, int? generation)
    {
        try
        {
            var field = coreLoader.GetField(
                "_lastLoadedBytes", BindingFlags.NonPublic | BindingFlags.Static);
            var bytes = field?.GetValue(null) as byte[];
            if (bytes == null || bytes.Length == 0) return null;

            lock (CoreShaGate)
            {
                if (generation is int gen
                    && gen == _cachedCoreGeneration
                    && ReferenceEquals(bytes, _cachedCoreBytes)
                    && _cachedCoreSha != null)
                    return _cachedCoreSha;
                string sha = Sha256Hex(bytes);
                _cachedCoreGeneration = generation ?? int.MinValue;
                _cachedCoreBytes = bytes;
                _cachedCoreSha = sha;
                return sha;
            }
        }
        catch
        {
            return null;
        }
    }

    private static List<Image>? TryReadReloadedImages()
    {
        var fromHost = ReadLoadedImages("Progenitor.Bootstrap.ReloaderHost");
        if (fromHost != null) return fromHost;
        return ReadLoadedImages("Progenitor.Reload.PluginReloader");
    }

    private static List<Image>? ReadLoadedImages(string ownerTypeName)
    {
        try
        {
            var type = FindType(ownerTypeName);
            var field = type?.GetField("ByPath", BindingFlags.NonPublic | BindingFlags.Static);
            var dict = field?.GetValue(null);
            if (dict == null) return null;
            var values = dict.GetType().GetProperty("Values")?.GetValue(dict) as IEnumerable;
            if (values == null) return null;

            var rows = new List<Image>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var loaded in values)
            {
                if (loaded == null) continue;
                string name = LoadedImageName(loaded);
                if (name.Length == 0 || !seen.Add(name)) continue;
                string? sha = GetInstanceString(loaded, "ImageSha");
                var assembly = loaded.GetType().GetField("Assembly", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(loaded) as Assembly;
                rows.Add(new Image(name, sha, version: InformationalVersion(assembly)));
            }
            return rows;
        }
        catch
        {
            return null;
        }
    }

    private static string LoadedImageName(object loaded)
    {
        string stem = GetInstanceString(loaded, "Stem") ?? "";
        if (stem.Length > 0) return stem;
        string assemblyName = GetInstanceString(loaded, "AssemblyName") ?? "";
        if (assemblyName.Length > 0) return assemblyName;
        return "";
    }

    private static void EnsureDevRow(List<Image> rows, ref bool any)
    {
        string? asmName = null;
        try { asmName = typeof(PingIdentity).Assembly.GetName().Name; }
        catch { return; }
        const string DevName = "Progenitor.Dev";
        if (!string.Equals(asmName, DevName, StringComparison.Ordinal)) return;
        for (int i = 0; i < rows.Count; i++)
        {
            if (string.Equals(rows[i].Name, DevName, StringComparison.OrdinalIgnoreCase))
                return;
        }
        rows.Add(new Image(DevName, sha256: null, version: InformationalVersion(typeof(PingIdentity).Assembly)));
        any = true;
    }

    /// <summary>
    /// The assembly's <c>AssemblyInformationalVersionAttribute</c> (the build stamps
    /// <c>year.month.release+revision</c>, docs/versioning.md), else its assembly version, else null.
    /// Agents compare the revision with their own branch before swapping Core.
    /// </summary>
    internal static string? InformationalVersion(Assembly? assembly)
    {
        if (assembly == null) return null;
        try
        {
            var info = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrEmpty(info)) return info;
            return assembly.GetName().Version?.ToString();
        }
        catch
        {
            return null;
        }
    }

    private static string? GetInstanceString(object obj, string field)
    {
        var f = obj.GetType().GetField(field, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        return f?.GetValue(obj) as string;
    }

    /// <summary>
    /// Lowercase hex SHA-256 of in-memory bytes already held by the loader. Same alphabet as
    /// <c>ReloadAllowPolicy.Sha256Hex</c>; inlined so this file stays game-free in the suite
    /// without taking a compile dependency on the allowlist type for this one helper.
    /// </summary>
    internal static string Sha256Hex(byte[] bytes)
    {
        byte[] hash = SHA256.HashData(bytes ?? Array.Empty<byte>());
        var sb = new StringBuilder(hash.Length * 2);
        for (int i = 0; i < hash.Length; i++)
            sb.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    private static void AddIdentityCandidate(
        List<CoreCandidate> candidates,
        Assembly? asm,
        string? alcOverride,
        string? activeAlc)
    {
        if (asm == null || candidates == null) return;
        string? alc = alcOverride;
        if (string.IsNullOrEmpty(alc))
            alc = TryAlcName(asm);
        if (string.IsNullOrEmpty(alc)) return;

        string? identity = null;
        if (string.Equals(alc, activeAlc, StringComparison.Ordinal))
            identity = ReadLocalIdentityFrom(asm);
        candidates.Add(new CoreCandidate(alc, identity));
    }

    private static string? ReadLocalIdentityFrom(Assembly asm)
    {
        try
        {
            var type = asm.GetType("Progenitor.Content.PrefabEditWireApply", throwOnError: false);
            var method = type?.GetMethod(
                "LocalIdentity", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
            if (method == null) return null;
            var result = method.Invoke(null, Array.Empty<object>()) as string;
            return string.IsNullOrEmpty(result) ? null : result;
        }
        catch
        {
            return null;
        }
    }

    private static string? TryAlcName(Assembly asm)
    {
        try
        {
            return AssemblyLoadContext.GetLoadContext(asm)?.Name;
        }
        catch
        {
            return null;
        }
    }

    private static string? TryReadActiveAlcName()
    {
        try
        {
            var prop = FindBootstrapType("Progenitor.Bootstrap.CoreLoader")
                ?.GetProperty("AlcName", BindingFlags.Public | BindingFlags.Static);
            var name = prop?.GetValue(null) as string;
            return string.IsNullOrEmpty(name) ? null : name;
        }
        catch
        {
            return null;
        }
    }

    private static Assembly? TryReadActiveCoreAssembly()
    {
        try
        {
            var prop = FindBootstrapType("Progenitor.Bootstrap.CoreLoader")
                ?.GetProperty("Assembly", BindingFlags.Public | BindingFlags.Static);
            return prop?.GetValue(null) as Assembly;
        }
        catch
        {
            return null;
        }
    }

    private static bool IdentitiesEqual(string? a, string? b)
    {
        if (string.IsNullOrEmpty(a)) a = null;
        if (string.IsNullOrEmpty(b)) b = null;
        return string.Equals(a, b, StringComparison.Ordinal);
    }

    /// <summary>
    /// Resolve a type without taking the first AppDomain hit. Order: this assembly (the
    /// test host compiles Core identity types here), the verified active Core, then
    /// Bootstrap by assembly name. Collectible Core types are never chosen by walk order.
    /// </summary>
    private static Type? FindType(string fullName)
    {
        try
        {
            var here = typeof(PingIdentity).Assembly.GetType(fullName, throwOnError: false);
            if (here != null) return here;
        }
        catch { /* collectible / reflection-only */ }

        try
        {
            var core = TryReadActiveCoreAssembly();
            string? activeAlc = TryReadActiveAlcName();
            if (core != null && !string.IsNullOrEmpty(activeAlc))
            {
                string? coreAlc = TryAlcName(core);
                if (string.IsNullOrEmpty(coreAlc)
                    || string.Equals(coreAlc, activeAlc, StringComparison.Ordinal))
                {
                    var t = core.GetType(fullName, throwOnError: false);
                    if (t != null) return t;
                }
            }
        }
        catch { /* collectible / reflection-only */ }

        if (fullName.StartsWith("Progenitor.Bootstrap.", StringComparison.Ordinal))
            return FindBootstrapType(fullName);

        return null;
    }

    /// <summary>
    /// Bootstrap is process-lifetime and unique. Filter by assembly name; do not take the
    /// first type match across collectible Core generations.
    /// </summary>
    private static Type? FindBootstrapType(string fullName)
    {
        Assembly[] assemblies;
        try { assemblies = AppDomain.CurrentDomain.GetAssemblies(); }
        catch { return null; }

        for (int i = 0; i < assemblies.Length; i++)
        {
            Assembly asm = assemblies[i];
            if (asm == null) continue;
            string? name;
            try { name = asm.GetName().Name; }
            catch { continue; }
            if (!string.Equals(name, "Progenitor.Bootstrap", StringComparison.Ordinal))
                continue;
            try { return asm.GetType(fullName, throwOnError: false); }
            catch { return null; }
        }
        return null;
    }
}
