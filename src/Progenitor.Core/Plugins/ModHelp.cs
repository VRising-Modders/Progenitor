using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;

namespace Progenitor.Plugins;

/// <summary>
/// Names the mod's own HTML help file when it is not at the conventional place. The mod's page in the Mods menu
/// then shows a <b>Help</b> button that opens it in the player's browser. Read from the plugin type by name when it
/// loads, beside <see cref="ModAboutAttribute"/>.
/// </summary>
/// <remarks>
/// <para>The page takes the help file from the first of these that exists:</para>
/// <list type="number">
/// <item>a file in the mod's own folder: <c>&lt;dll name&gt;.help.html</c> beside the DLL; in a folder that holds only
/// this mod, <c>help/index.html</c> there, then in the package folder; for a DLL directly in a shared folder
/// (<c>BepInEx/plugins</c>, <c>BepInEx/progenitor-reload</c>), <c>&lt;AssemblyName&gt;/help/index.html</c> under it;</item>
/// <item>an embedded resource named <c>help/index.html</c> (or <c>&lt;AssemblyName&gt;.help.index.html</c>), else
/// <c>Help.html</c> (or <c>&lt;AssemblyName&gt;.Help.html</c>), extracted with its sibling <c>help/*</c> resources to
/// <c>BepInEx/cache/progenitor-help/&lt;guid&gt;/&lt;version&gt;/</c> when the button is pressed;</item>
/// <item>the path this attribute (or <c>ctx.Menu.Help(path)</c>) names, relative to the mod's own folder, or the name of
/// an embedded resource.</item>
/// </list>
/// <para>Only a file under the mod's own folder or its cache folder is opened: a path that leaves that folder, an
/// absolute path and a URL are refused, and the button is not drawn.</para>
/// </remarks>
/// <example>
/// <code>
/// [BepInPlugin("example.minimap", "Minimap", "1.0.0")]
/// [ModHelp("docs/manual.html")]
/// public sealed class Plugin : ProgenitorPlugin&lt;PluginContext&gt; { ... }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class ModHelpAttribute : Attribute
{
    /// <param name="path">The help file, relative to the mod's own folder (forward slashes), or an embedded resource name.</param>
    public ModHelpAttribute(string path)
    {
        Path = path ?? "";
    }

    /// <summary>The help file, relative to the mod's own folder, or an embedded resource name.</summary>
    public string Path { get; }
}

/// <summary>Where a mod page's help file came from (the readback's <c>page-help.source</c>).</summary>
internal enum ModHelpSource
{
    None = 0,

    /// <summary>A file in the mod's own folder at the conventional name.</summary>
    Folder = 1,

    /// <summary>An embedded resource at the conventional name, extracted to the cache.</summary>
    Embedded = 2,

    /// <summary>The path <c>[ModHelp]</c> or <c>ctx.Menu.Help</c> names (a file or an embedded resource).</summary>
    Declared = 3,

    /// <summary>A file Progenitor names for one of its own entries (the Progenitor guide).</summary>
    Guide = 4,
}

/// <summary>One embedded help file, as plain bytes (no assembly is kept).</summary>
internal sealed class ModHelpResource
{
    internal ModHelpResource(string path, byte[] bytes)
    {
        Path = path ?? "";
        Bytes = bytes ?? Array.Empty<byte>();
    }

    /// <summary>The path under the cache folder, forward slashes (<c>help/index.html</c>, <c>help/style.css</c>).</summary>
    internal string Path { get; }

    internal byte[] Bytes { get; }
}

/// <summary>What one plugin's assembly says about its help, as plain data (no assembly or type is kept).</summary>
internal sealed class ModHelpFacts
{
    internal static ModHelpFacts Empty { get; } = new();

    /// <summary>The plugin version, which scopes the extraction cache.</summary>
    internal string Version { get; init; } = "";

    /// <summary>The <see cref="ModHelpAttribute.Path"/>, or empty.</summary>
    internal string Declared { get; init; } = "";

    /// <summary>The embedded help files this assembly carries (the conventional ones and the declared one's folder).</summary>
    internal IReadOnlyList<ModHelpResource> Embedded { get; init; } = Array.Empty<ModHelpResource>();

    internal ModHelpResource? FindEmbedded(string relative)
    {
        foreach (var r in Embedded)
            if (string.Equals(r.Path, relative, StringComparison.OrdinalIgnoreCase)) return r;
        return null;
    }
}

/// <summary>The help file a mod page's Help button opens, decided from evidence; <see cref="Found"/> false for none.</summary>
internal sealed class ModHelpTarget
{
    internal static ModHelpTarget None { get; } = new(ModHelpSource.None, "", "", "", null);

    internal ModHelpTarget(ModHelpSource source, string root, string relative, string file, IReadOnlyList<ModHelpResource>? extract)
    {
        Source = source;
        Root = root ?? "";
        Relative = relative ?? "";
        File = file ?? "";
        Extract = extract;
    }

    internal ModHelpSource Source { get; }

    /// <summary>The folder the file must stay under: the mod's own folder, or the cache folder for an extracted file.</summary>
    internal string Root { get; }

    /// <summary>The file's path under <see cref="Root"/>, forward slashes (the readback's <c>file</c>).</summary>
    internal string Relative { get; }

    /// <summary>The full path the browser opens (for an embedded file, where it is extracted to).</summary>
    internal string File { get; }

    /// <summary>The embedded files written under <see cref="Root"/> before opening; null for a file already on disk.</summary>
    internal IReadOnlyList<ModHelpResource>? Extract { get; }

    internal bool Found => Source != ModHelpSource.None && File.Length > 0;

    internal string SourceName => ModHelp.NameOf(Source);
}

/// <summary>
/// The help file conventions, the resolver and the safe <c>file://</c> URI. Game-free: every read is bounded and never
/// throws; a miss is <see cref="ModHelpTarget.None"/>.
/// </summary>
internal static class ModHelp
{
    /// <summary>The conventional help page in the mod's folder, and the embedded resource's logical name.</summary>
    internal const string IndexPath = "help/index.html";

    /// <summary>The single-file embedded help page.</summary>
    internal const string SingleName = "Help.html";

    /// <summary>The help page beside the DLL, after its stem: <c>&lt;dll name&gt;.help.html</c>.</summary>
    internal const string BesideSuffix = ".help.html";

    /// <summary>The folder under <c>BepInEx/cache</c> extracted help goes to.</summary>
    internal const string CacheFolderName = "progenitor-help";

    /// <summary>The Progenitor guide, under <c>BepInEx/progenitor-core</c>, for the Progenitor entry.</summary>
    internal const string ProgenitorGuidePath = "docs/guide/progenitor-guide.html";

    /// <summary>Embedded help read per assembly: at most this many files and bytes.</summary>
    internal const int MaxFiles = 512;

    internal const long MaxBytes = 16L * 1024 * 1024;

    internal static string NameOf(ModHelpSource source) => source switch
    {
        ModHelpSource.Folder => "folder",
        ModHelpSource.Embedded => "embedded",
        ModHelpSource.Declared => "declared",
        ModHelpSource.Guide => "guide",
        _ => "none",
    };

    // ---- reading the assembly ---------------------------------------------------------------------------------

    /// <summary>The help facts of <paramref name="type"/>'s assembly: <c>[ModHelp]</c> and the embedded help files.</summary>
    internal static ModHelpFacts FromType(Type? type, string? version)
    {
        if (type == null) return ModHelpFacts.Empty;
        string declared = DeclaredHelp(type);
        Assembly? assembly = null;
        try { assembly = type.Assembly; }
        catch { assembly = null; }
        var embedded = new List<ModHelpResource>();
        if (assembly != null)
        {
            try
            {
                var asm = assembly;
                embedded = EmbeddedHelp(asm.GetManifestResourceNames(), name => asm.GetManifestResourceStream(name), asm.GetName().Name, declared);
            }
            catch { embedded = new List<ModHelpResource>(); }
        }

        return new ModHelpFacts { Version = (version ?? "").Trim(), Declared = declared, Embedded = embedded };
    }

    /// <summary><c>[ModHelp]</c> read by name, so a plugin built against another Core generation is still read.</summary>
    internal static string DeclaredHelp(Type? type)
    {
        if (type == null) return "";
        try
        {
            for (var t = type; t != null; t = t.BaseType)
            {
                foreach (var a in CustomAttributeData.GetCustomAttributes(t))
                {
                    if (a.AttributeType.FullName != typeof(ModHelpAttribute).FullName || a.ConstructorArguments.Count == 0) continue;
                    return ((a.ConstructorArguments[0].Value as string) ?? "").Trim();
                }
            }
        }
        catch { /* nothing declared */ }
        return "";
    }

    /// <summary>
    /// The embedded help files among <paramref name="names"/>, each under the path it is extracted to:
    /// a resource named with a folder (<c>help/index.html</c>, the <c>LogicalName</c> spelling) keeps it;
    /// <c>&lt;assemblyName&gt;.help.&lt;rest&gt;</c> (the SDK's default name) becomes <c>help/&lt;rest&gt;</c>;
    /// <c>Help.html</c> and <c>&lt;assemblyName&gt;.Help.html</c> become <c>Help.html</c>. Taken: everything under
    /// <c>help/</c>, <c>Help.html</c>, and the declared path and its folder. A name that is not a safe relative path is
    /// skipped. Bounded by <see cref="MaxFiles"/> and <see cref="MaxBytes"/>.
    /// </summary>
    internal static List<ModHelpResource> EmbeddedHelp(IEnumerable<string>? names, Func<string, Stream?> open, string? assemblyName, string? declared = null)
    {
        var list = new List<ModHelpResource>();
        if (names == null || open == null) return list;
        string asm = (assemblyName ?? "").Trim();
        string? declaredRel = SafeRelative(declared);
        string declaredFolder = declaredRel != null && declaredRel.LastIndexOf('/') > 0
            ? declaredRel.Substring(0, declaredRel.LastIndexOf('/') + 1)
            : "";
        long total = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
        {
            if (list.Count >= MaxFiles) break;
            string? rel = HelpPathOfResource(name, asm);
            if (rel == null) continue;
            bool take = rel.StartsWith("help/", StringComparison.OrdinalIgnoreCase)
                || string.Equals(rel, SingleName, StringComparison.OrdinalIgnoreCase)
                || (declaredRel != null && string.Equals(rel, declaredRel, StringComparison.OrdinalIgnoreCase))
                || (declaredFolder.Length > 0 && rel.StartsWith(declaredFolder, StringComparison.OrdinalIgnoreCase));
            if (!take || !seen.Add(rel)) continue;
            try
            {
                using var stream = open(name);
                if (stream == null) continue;
                var bytes = ReadBounded(stream, MaxBytes - total);
                if (bytes == null) break;
                total += bytes.Length;
                list.Add(new ModHelpResource(rel, bytes));
            }
            catch { /* skipped */ }
        }

        return list;
    }

    /// <summary>The path a resource is extracted to (see <see cref="EmbeddedHelp"/>), or null when it is not a safe one.</summary>
    internal static string? HelpPathOfResource(string? name, string? assemblyName)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        string n = name!.Trim();
        string asm = (assemblyName ?? "").Trim();
        if (n.IndexOf('/') >= 0 || n.IndexOf('\\') >= 0) return SafeRelative(n);
        if (asm.Length > 0 && n.StartsWith(asm + ".", StringComparison.OrdinalIgnoreCase))
        {
            string rest = n.Substring(asm.Length + 1);
            if (string.Equals(rest, SingleName, StringComparison.OrdinalIgnoreCase)) return SingleName;
            if (rest.StartsWith("help.", StringComparison.OrdinalIgnoreCase) && rest.Length > 5)
                return SafeRelative("help/" + rest.Substring(5));
            return SafeRelative(rest);
        }

        return SafeRelative(n);
    }

    private static byte[]? ReadBounded(Stream stream, long budget)
    {
        if (budget <= 0) return null;
        using var ms = new MemoryStream();
        var chunk = new byte[81920];
        while (true)
        {
            int n = stream.Read(chunk, 0, chunk.Length);
            if (n <= 0) break;
            if (ms.Length + n > budget) return null;
            ms.Write(chunk, 0, n);
        }

        return ms.ToArray();
    }

    // ---- paths ------------------------------------------------------------------------------------------------

    /// <summary>
    /// <paramref name="path"/> as a relative path with forward slashes, or null when it is empty, absolute, a URL, has a
    /// drive or a colon, or has an empty, <c>.</c> or <c>..</c> segment or a character a file name cannot hold.
    /// </summary>
    internal static string? SafeRelative(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        string p = path!.Trim().Replace('\\', '/');
        if (p.IndexOf(':') >= 0 || p.StartsWith("/", StringComparison.Ordinal)) return null;
        var invalid = Path.GetInvalidFileNameChars();
        var parts = p.Split('/');
        foreach (var part in parts)
        {
            if (part.Length == 0 || part == "." || part == "..") return null;
            if (part.Trim().Length == 0 || part.IndexOfAny(invalid) >= 0) return null;
        }

        return string.Join("/", parts);
    }

    /// <summary>True when <paramref name="path"/> is <paramref name="root"/> or under it, both made full first.</summary>
    internal static bool IsUnder(string? root, string? path)
    {
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            string r = Path.GetFullPath(root!.Trim()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string f = Path.GetFullPath(path!.Trim());
            if (r.Length == 0) return false;
            return f.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary><paramref name="relative"/> under <paramref name="root"/> as a full path, or null when it leaves the root.</summary>
    internal static string? Under(string root, string relative)
    {
        string? safe = SafeRelative(relative);
        if (safe == null || string.IsNullOrWhiteSpace(root)) return null;
        try
        {
            string full = Path.GetFullPath(Path.Combine(root, safe.Replace('/', Path.DirectorySeparatorChar)));
            return IsUnder(root, full) ? full : null;
        }
        catch { return null; }
    }

    /// <summary>A safe folder name for a GUID or version: letters, digits, <c>. _ -</c>; anything else becomes <c>_</c>.</summary>
    internal static string FolderName(string? text, string fallback)
    {
        string s = (text ?? "").Trim();
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
            sb.Append((char.IsLetterOrDigit(c) && c < 128) || c == '.' || c == '_' || c == '-' ? c : '_');
        string name = sb.ToString().Trim('.');
        return name.Length == 0 || name.Length > 120 ? fallback : name;
    }

    /// <summary>The cache folder a plugin version's embedded help is extracted to.</summary>
    internal static string CacheFolder(string cacheRoot, string pluginId, string version) =>
        Path.Combine(cacheRoot, CacheFolderName, FolderName(pluginId, "plugin"), FolderName(version, "0"));

    /// <summary>
    /// The mod's own folders, in order: the DLL's folder and then its package folder, when the DLL's folder holds only
    /// this mod; <c>&lt;shared folder&gt;/&lt;dll stem&gt;</c> when the DLL sits directly in a shared folder.
    /// </summary>
    internal static List<string> OwnRoots(string? dllPath)
    {
        var roots = new List<string>(2);
        if (string.IsNullOrWhiteSpace(dllPath)) return roots;
        try
        {
            string full = Path.GetFullPath(dllPath!.Trim());
            string? dir = Path.GetDirectoryName(full);
            if (string.IsNullOrEmpty(dir)) return roots;
            if (PluginAboutReader.IsSharedFolder(dir))
            {
                string stem = Stem(full);
                if (stem.Length > 0) roots.Add(Path.Combine(dir, stem));
                return roots;
            }

            roots.Add(dir!);
            var package = PackageManifestBeside.TryRead(full);
            if (package != null && !string.Equals(package.Folder, dir, StringComparison.OrdinalIgnoreCase)) roots.Add(package.Folder);
        }
        catch { /* none */ }
        return roots;
    }

    private static string Stem(string fullDll)
    {
        string stem = Path.GetFileNameWithoutExtension(fullDll);
        if (stem.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) stem = Path.GetFileNameWithoutExtension(stem);
        return stem;
    }

    /// <summary>
    /// The help file for one plugin, first match wins (<see cref="ModHelpAttribute"/>): <paramref name="guides"/> (a
    /// root and a relative path Progenitor names for its own entries), a file in the mod's own folder, an embedded
    /// resource, then the declared path (<paramref name="declaredApi"/>, else the attribute). Reads the disk; never throws.
    /// </summary>
    internal static ModHelpTarget Resolve(
        string pluginId,
        string? dllPath,
        ModHelpFacts? facts,
        string? declaredApi,
        string? cacheRoot,
        IReadOnlyList<(string Root, string Relative)>? guides = null)
    {
        facts ??= ModHelpFacts.Empty;
        try
        {
            if (guides != null)
            {
                foreach (var (root, relative) in guides)
                {
                    string? file = Under(root, relative);
                    if (file != null && System.IO.File.Exists(file))
                        return new ModHelpTarget(ModHelpSource.Guide, root, SafeRelative(relative)!, file, null);
                }
            }

            // 1. A file in the mod's own folder.
            if (!string.IsNullOrWhiteSpace(dllPath))
            {
                string full = Path.GetFullPath(dllPath!.Trim());
                string? dir = Path.GetDirectoryName(full);
                if (!string.IsNullOrEmpty(dir))
                {
                    string beside = Path.Combine(dir!, Stem(full) + BesideSuffix);
                    if (System.IO.File.Exists(beside))
                        return new ModHelpTarget(ModHelpSource.Folder, dir!, Path.GetFileName(beside), beside, null);
                }
            }

            var roots = OwnRoots(dllPath);
            foreach (var root in roots)
            {
                string? file = Under(root, IndexPath);
                if (file != null && System.IO.File.Exists(file)) return new ModHelpTarget(ModHelpSource.Folder, root, IndexPath, file, null);
            }

            // 2. An embedded resource at the conventional name.
            bool cacheKnown = !string.IsNullOrWhiteSpace(cacheRoot);
            if (cacheKnown && facts.Embedded.Count > 0)
            {
                foreach (var name in new[] { IndexPath, SingleName })
                {
                    if (facts.FindEmbedded(name) == null) continue;
                    var target = EmbeddedTarget(ModHelpSource.Embedded, pluginId, facts, cacheRoot!, name);
                    if (target != null) return target;
                }
            }

            // 3. The declared path: the API's, else the attribute's; a file in the mod's folder, else an embedded one.
            foreach (var declared in new[] { declaredApi, facts.Declared })
            {
                string? rel = SafeRelative(declared);
                if (rel == null) continue;
                foreach (var root in roots)
                {
                    string? file = Under(root, rel);
                    if (file != null && System.IO.File.Exists(file)) return new ModHelpTarget(ModHelpSource.Declared, root, rel, file, null);
                }

                if (cacheKnown && facts.FindEmbedded(rel) != null)
                {
                    var target = EmbeddedTarget(ModHelpSource.Declared, pluginId, facts, cacheRoot!, rel);
                    if (target != null) return target;
                }
            }
        }
        catch { /* no help */ }

        return ModHelpTarget.None;
    }

    private static ModHelpTarget? EmbeddedTarget(ModHelpSource source, string pluginId, ModHelpFacts facts, string cacheRoot, string relative)
    {
        string folder = CacheFolder(cacheRoot, pluginId, facts.Version);
        string? file = Under(folder, relative);
        return file == null ? null : new ModHelpTarget(source, folder, relative, file, facts.Embedded);
    }

    /// <summary>
    /// Write <paramref name="target"/>'s embedded files under its cache folder (only those whose bytes differ from what is
    /// there) and return the file to open; a file already on disk is returned as is. Null when a write failed or a path
    /// would leave the folder.
    /// </summary>
    internal static string? Materialize(ModHelpTarget target, out string error)
    {
        error = "";
        if (target == null || !target.Found)
        {
            error = "no help file";
            return null;
        }

        try
        {
            if (target.Extract != null)
            {
                foreach (var resource in target.Extract)
                {
                    string? path = Under(target.Root, resource.Path);
                    if (path == null) continue;
                    if (System.IO.File.Exists(path) && SameBytes(path, resource.Bytes)) continue;
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    System.IO.File.WriteAllBytes(path, resource.Bytes);
                }
            }

            if (!IsUnder(target.Root, target.File) || !System.IO.File.Exists(target.File))
            {
                error = "the help file is not in the mod's folder";
                return null;
            }

            return Path.GetFullPath(target.File);
        }
        catch (Exception e)
        {
            error = e.Message;
            return null;
        }
    }

    private static bool SameBytes(string path, byte[] bytes)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Length != bytes.LongLength) return false;
            var existing = System.IO.File.ReadAllBytes(path);
            for (int i = 0; i < existing.Length; i++)
                if (existing[i] != bytes[i]) return false;
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// A <c>file://</c> URI for a full local path, each segment percent-encoded (spaces, <c>#</c>, <c>%</c>, non-ASCII as
    /// UTF-8), checked to round-trip back to the same path. Null for a relative path, or one that does not round-trip.
    /// </summary>
    internal static string? FileUri(string? fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath)) return null;
        try
        {
            string raw = fullPath!.Trim();
            if (!Path.IsPathFullyQualified(raw)) return null;
            string full = Path.GetFullPath(raw);
            string p = full.Replace('\\', '/');
            string uri;
            if (p.StartsWith("//", StringComparison.Ordinal))
            {
                var parts = p.Substring(2).Split('/');
                if (parts.Length < 2 || parts[0].Length == 0 || parts[0] == "?" || parts[0] == ".") return null;
                uri = "file://" + parts[0] + "/" + EscapeSegments(parts, 1);
            }
            else if (p.StartsWith("/", StringComparison.Ordinal))
            {
                uri = "file:///" + EscapeSegments(p.Substring(1).Split('/'), 0);
            }
            else
            {
                var parts = p.Split('/');
                string drive = parts[0];
                if (drive.Length != 2 || drive[1] != ':' || !char.IsLetter(drive[0])) return null;
                uri = "file:///" + drive + "/" + EscapeSegments(parts, 1);
            }

            if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed) || !parsed.IsFile) return null;
            string back = Path.GetFullPath(parsed.LocalPath);
            return string.Equals(back, full, StringComparison.OrdinalIgnoreCase) ? uri : null;
        }
        catch { return null; }
    }

    private static string EscapeSegments(string[] parts, int from)
    {
        var sb = new StringBuilder();
        for (int i = from; i < parts.Length; i++)
        {
            if (i > from) sb.Append('/');
            sb.Append(Uri.EscapeDataString(parts[i]));
        }

        return sb.ToString();
    }
}

/// <summary>
/// What each plugin's assembly said about its help (<see cref="ModHelp.FromType"/>) and the path it named with
/// <c>ctx.Menu.Help</c>, keyed by plugin GUID. Plain data, so it pins no collectible generation; a later load replaces
/// the facts, and the declared path goes with the registration that set it.
/// </summary>
internal static class ModHelpStore
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, ModHelpFacts> Facts = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, (object Token, string Path)> Declared = new(StringComparer.OrdinalIgnoreCase);

    internal static void Note(string? id, ModHelpFacts? facts)
    {
        if (string.IsNullOrWhiteSpace(id) || facts == null) return;
        lock (Gate) Facts[id!.Trim()] = facts;
    }

    /// <summary>Record what <paramref name="type"/>'s assembly says, once per load. Never throws.</summary>
    internal static void NoteFromType(string? id, Type? type, string? version)
    {
        try { Note(id, ModHelp.FromType(type, version)); }
        catch { /* help is optional */ }
    }

    internal static bool TryGet(string? id, out ModHelpFacts facts)
    {
        facts = ModHelpFacts.Empty;
        if (string.IsNullOrWhiteSpace(id)) return false;
        lock (Gate)
        {
            if (!Facts.TryGetValue(id!.Trim(), out var found)) return false;
            facts = found;
            return true;
        }
    }

    /// <summary>Set the declared path for <paramref name="id"/>; disposing the lease clears it (if it is still this one).</summary>
    internal static IDisposable Declare(string id, string path)
    {
        var token = new object();
        lock (Gate) Declared[id.Trim()] = (token, path ?? "");
        return new Lease(id.Trim(), token);
    }

    internal static string DeclaredPath(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return "";
        lock (Gate) return Declared.TryGetValue(id!.Trim(), out var d) ? d.Path : "";
    }

    internal static void ResetForTests()
    {
        lock (Gate)
        {
            Facts.Clear();
            Declared.Clear();
        }
    }

    private sealed class Lease : IDisposable
    {
        private readonly string _id;
        private object? _token;

        internal Lease(string id, object token)
        {
            _id = id;
            _token = token;
        }

        public void Dispose()
        {
            var token = _token;
            if (token == null) return;
            _token = null;
            lock (Gate)
            {
                if (Declared.TryGetValue(_id, out var d) && ReferenceEquals(d.Token, token)) Declared.Remove(_id);
            }
        }
    }
}
