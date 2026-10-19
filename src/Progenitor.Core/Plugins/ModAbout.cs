using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace Progenitor.Plugins;

/// <summary>
/// The text a mod's page shows under <b>About</b>, written in the plugin's own source. Markdown, drawn by
/// the same renderer as a Thunderstore README. Read from the plugin type by name when it loads, beside
/// <see cref="CounterpartAttribute"/> and <see cref="NetworkCompatibilityAttribute"/>.
/// </summary>
/// <remarks>
/// <para>The page takes its About text from the first of these that has any:</para>
/// <list type="number">
/// <item>the Thunderstore README of the listing this exact install came from: this side's install ledger row, or a
/// package <c>manifest.json</c> beside the DLL, names the package and the listing's version;</item>
/// <item>an embedded resource in the plugin assembly named exactly <c>README.md</c>, else
/// <c>&lt;AssemblyName&gt;.README.md</c> (<c>&lt;EmbeddedResource Include="README.md" /&gt;</c> when the root
/// namespace is the assembly name);</item>
/// <item>a file beside the DLL: <c>&lt;dll name&gt;.README.md</c> or <c>&lt;dll name&gt;.md</c>; in a folder of the
/// mod's own (not <c>BepInEx/plugins</c> or <c>BepInEx/progenitor-reload</c> itself) <c>README.md</c>; then the
/// <c>README.md</c> and the <c>description</c> of the package folder's <c>manifest.json</c>;</item>
/// <item>this attribute;</item>
/// <item>the description the plugin registered with <c>ctx.Menu.Register(name, description)</c>, then its
/// <c>[assembly: AssemblyDescription]</c>;</item>
/// <item><c>No description available.</c></item>
/// </list>
/// <para>A description (registered, assembly or package manifest) is plain text: runs of spaces, tabs and line breaks
/// become one space and only a blank line starts a new paragraph, so a multi-line <c>&lt;Description&gt;</c> in a
/// project file reads cleanly. README and <c>[ModAbout]</c> text is Markdown and is drawn as written. Progenitor's own
/// page always shows its own curated text.</para>
/// <para>A listing found any other way (the plugin's GUID or name spelling a package name) is never shown for an
/// installed mod: it may be another project with the same name.</para>
/// </remarks>
/// <example>
/// <code>
/// [BepInPlugin("example.minimap", "Minimap", "1.0.0")]
/// [ModAbout("A minimap in the top right corner.\n\nPress **M** to zoom.")]
/// public sealed class Plugin : ProgenitorPlugin&lt;PluginContext&gt; { ... }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class ModAboutAttribute : Attribute
{
    /// <param name="markdown">The About text, in Markdown.</param>
    public ModAboutAttribute(string markdown)
    {
        Markdown = markdown ?? "";
    }

    /// <summary>The About text, in Markdown.</summary>
    public string Markdown { get; }
}

/// <summary>Where a mod page's About text came from (the readback's <c>page-readme.source</c>).</summary>
internal enum ModAboutSource
{
    None = 0,
    Listing = 1,
    Embedded = 2,
    Beside = 3,
    Declared = 4,
    Description = 5,

    /// <summary>Progenitor's own page: the text in <see cref="ProgenitorAbout"/>, written for players.</summary>
    Curated = 6,
}

/// <summary>
/// The About text of Progenitor's own mod page (Mods › Progenitor). It lives in Core, so a Core update changes it
/// without a restart, and it wins over every other source: the Bootstrap assembly description and the package
/// README are written for other readers.
/// </summary>
internal static class ProgenitorAbout
{
    internal const string Markdown =
        "Progenitor is a mod manager and mod framework for V Rising. "
        + "Install, update and configure your mods here, and join modded servers with the right mods.";

    /// <summary>True for Progenitor's own plugin id (<c>progenitor</c>, or its pre-rename spelling).</summary>
    internal static bool IsSelf(string? pluginId)
    {
        if (string.IsNullOrWhiteSpace(pluginId)) return false;
        return string.Equals(LegacyIds.Normalize(pluginId!.Trim()), LegacyIds.Root, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>What one plugin says about itself, as plain strings (no assembly or type is kept).</summary>
internal sealed class PluginAboutFacts
{
    internal static PluginAboutFacts Empty { get; } = new();

    /// <summary>The embedded <c>README.md</c> resource, or empty.</summary>
    internal string Embedded { get; init; } = "";

    /// <summary>A README or manifest description beside the DLL, or empty.</summary>
    internal string Beside { get; init; } = "";

    /// <summary><see cref="ModAboutAttribute.Markdown"/>, or empty.</summary>
    internal string Declared { get; init; } = "";

    /// <summary>The registered or assembly description, or empty.</summary>
    internal string Description { get; init; } = "";

    /// <summary>The DLL the assembly was loaded from, when the loader recorded one.</summary>
    internal string Location { get; init; } = "";

    internal PluginAboutFacts With(string? beside = null, string? description = null) => new()
    {
        Embedded = Embedded,
        Beside = beside ?? Beside,
        Declared = Declared,
        Description = description ?? Description,
        Location = Location,
    };
}

/// <summary>The About text a mod page draws, and where it came from.</summary>
internal readonly struct ModAboutText
{
    private readonly string? _markdown;

    internal ModAboutText(string markdown, ModAboutSource source)
    {
        string text = markdown ?? "";
        Source = text.Trim().Length == 0 ? ModAboutSource.None : source;
        _markdown = Source == ModAboutSource.None ? "" : text;
    }

    /// <summary>The Markdown to draw; empty (never null) for <c>default</c>.</summary>
    internal string Markdown => _markdown ?? "";
    internal ModAboutSource Source { get; }

    internal string SourceName => NameOf(Source);

    internal static string NameOf(ModAboutSource source) => source switch
    {
        ModAboutSource.Listing => "listing",
        ModAboutSource.Embedded => "embedded",
        ModAboutSource.Beside => "beside",
        ModAboutSource.Declared => "declared",
        ModAboutSource.Description => "description",
        ModAboutSource.Curated => "curated",
        _ => "none",
    };

    /// <summary>
    /// The order in <see cref="ModAboutAttribute"/>: the README of a listing verified at this version, then the
    /// plugin's embedded README, the file beside its DLL, its <c>[ModAbout]</c>, its description, and nothing.
    /// <paramref name="listingReadme"/> is read only when <paramref name="listingReadmeApplies"/>.
    /// </summary>
    internal static ModAboutText Choose(bool listingReadmeApplies, string? listingReadme, PluginAboutFacts? plugin)
    {
        if (listingReadmeApplies && HasText(listingReadme)) return new ModAboutText(listingReadme!, ModAboutSource.Listing);
        plugin ??= PluginAboutFacts.Empty;
        if (HasText(plugin.Embedded)) return new ModAboutText(plugin.Embedded, ModAboutSource.Embedded);
        if (HasText(plugin.Beside)) return new ModAboutText(plugin.Beside, ModAboutSource.Beside);
        if (HasText(plugin.Declared)) return new ModAboutText(plugin.Declared, ModAboutSource.Declared);
        if (HasText(plugin.Description))
            return new ModAboutText(PluginAboutReader.NormalizePlainText(plugin.Description), ModAboutSource.Description);
        return default;
    }

    /// <summary>
    /// The About text for the page of <paramref name="pluginId"/>: <see cref="ProgenitorAbout"/> on Progenitor's own
    /// page, else <see cref="Choose"/>.
    /// </summary>
    internal static ModAboutText ForPage(string? pluginId, bool listingReadmeApplies, string? listingReadme, PluginAboutFacts? plugin) =>
        ProgenitorAbout.IsSelf(pluginId)
            ? new ModAboutText(ProgenitorAbout.Markdown, ModAboutSource.Curated)
            : Choose(listingReadmeApplies, listingReadme, plugin);

    private static bool HasText(string? s) => !string.IsNullOrWhiteSpace(s);
}

/// <summary>
/// Reads a plugin's own About text: the embedded README, the file beside the DLL and <c>[ModAbout]</c>.
/// Every read is bounded and never throws; a miss is empty.
/// </summary>
internal static class PluginAboutReader
{
    /// <summary>The longest About text read from any one source. The page caps what it draws again.</summary>
    internal const int MaxChars = 256 * 1024;

    internal const string ReadmeName = "README.md";
    internal const string ManifestName = "manifest.json";

    /// <summary>Folders directly under <c>BepInEx</c> that many plugins share.</summary>
    private static readonly string[] SharedUnderBepInEx = { "plugins", "progenitor-reload", "progenitor-pending" };

    /// <summary>Everything the assembly and the type say: the embedded README, <c>[ModAbout]</c>, the assembly description and its file.</summary>
    internal static PluginAboutFacts FromType(Type? type)
    {
        if (type == null) return PluginAboutFacts.Empty;
        Assembly? assembly = null;
        try { assembly = type.Assembly; }
        catch { assembly = null; }
        string location = "";
        try { location = assembly?.Location ?? ""; }
        catch { location = ""; }
        return new PluginAboutFacts
        {
            Embedded = EmbeddedReadme(assembly),
            Declared = DeclaredAbout(type),
            Description = AssemblyDescription(assembly),
            Location = location,
        };
    }

    /// <summary>The assembly's own <c>README.md</c> resource, or empty.</summary>
    internal static string EmbeddedReadme(Assembly? assembly)
    {
        if (assembly == null) return "";
        try
        {
            return EmbeddedReadme(
                assembly.GetManifestResourceNames(),
                name => assembly.GetManifestResourceStream(name),
                assembly.GetName().Name);
        }
        catch { return ""; }
    }

    /// <summary>
    /// The resource named exactly <c>README.md</c>, else exactly <c>&lt;assemblyName&gt;.README.md</c> (the name the
    /// SDK gives <c>&lt;EmbeddedResource Include="README.md" /&gt;</c> when the root namespace is the assembly
    /// name), case-insensitively. Any other <c>*.README.md</c> is not taken: it may be a merged dependency's.
    /// </summary>
    internal static string EmbeddedReadme(IEnumerable<string>? names, Func<string, Stream?> open, string? assemblyName)
    {
        if (names == null || open == null) return "";
        string own = string.IsNullOrWhiteSpace(assemblyName) ? "" : assemblyName!.Trim() + "." + ReadmeName;
        string? exact = null, named = null;
        foreach (var name in names)
        {
            if (string.IsNullOrEmpty(name)) continue;
            if (exact == null && string.Equals(name, ReadmeName, StringComparison.OrdinalIgnoreCase)) exact = name;
            else if (named == null && own.Length > 0 && string.Equals(name, own, StringComparison.OrdinalIgnoreCase)) named = name;
        }

        string? best = exact ?? named;
        if (best == null) return "";
        try
        {
            using var stream = open(best);
            return stream == null ? "" : ReadBounded(stream);
        }
        catch { return ""; }
    }

    /// <summary><c>[ModAbout]</c> read by name, so a plugin built against another Core generation is still read.</summary>
    internal static string DeclaredAbout(Type? type)
    {
        if (type == null) return "";
        try
        {
            for (var t = type; t != null; t = t.BaseType)
            {
                foreach (var a in CustomAttributeData.GetCustomAttributes(t))
                {
                    if (a.AttributeType.FullName != typeof(ModAboutAttribute).FullName || a.ConstructorArguments.Count == 0) continue;
                    return Cap(a.ConstructorArguments[0].Value as string);
                }
            }
        }
        catch { /* nothing declared */ }
        return "";
    }

    /// <summary><c>[assembly: AssemblyDescription]</c>, or empty.</summary>
    internal static string AssemblyDescription(Assembly? assembly)
    {
        if (assembly == null) return "";
        try
        {
            foreach (var a in CustomAttributeData.GetCustomAttributes(assembly))
            {
                if (a.AttributeType.FullName != typeof(AssemblyDescriptionAttribute).FullName || a.ConstructorArguments.Count == 0) continue;
                return NormalizePlainText(Cap(a.ConstructorArguments[0].Value as string));
            }
        }
        catch { /* none */ }
        return "";
    }

    /// <summary>
    /// A plain-text description (an assembly description, one registered with <c>ctx.Menu</c>, a package manifest's)
    /// as clean paragraphs: inside a paragraph every run of spaces, tabs and line breaks becomes one space; a blank
    /// line (only whitespace on it) between text is a paragraph break, written as one empty line. A multi-line
    /// <c>&lt;Description&gt;</c> in a project file then reads as clean text. README files are Markdown and are never
    /// passed through this.
    /// </summary>
    internal static string NormalizePlainText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var paragraphs = new List<string>();
        var current = new StringBuilder();
        foreach (var line in text!.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            if (line.Trim().Length == 0)
            {
                if (current.Length > 0) paragraphs.Add(current.ToString());
                current.Clear();
                continue;
            }

            foreach (var word in line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                if (current.Length > 0) current.Append(' ');
                current.Append(word);
            }
        }

        if (current.Length > 0) paragraphs.Add(current.ToString());
        return string.Join("\n\n", paragraphs);
    }

    /// <summary>
    /// The text beside a DLL: <c>&lt;stem&gt;.README.md</c>, <c>&lt;stem&gt;.md</c>; in a folder of the mod's own,
    /// <c>README.md</c>; then the package folder's (<see cref="PackageManifestBeside"/>) <c>README.md</c> and its
    /// manifest's description. Empty when there is none.
    /// </summary>
    internal static string Beside(string? dllPath)
    {
        if (string.IsNullOrWhiteSpace(dllPath)) return "";
        try
        {
            string full = Path.GetFullPath(dllPath!.Trim());
            string? dir = Path.GetDirectoryName(full);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return "";
            string stem = Path.GetFileNameWithoutExtension(full);
            if (stem.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) stem = Path.GetFileNameWithoutExtension(stem);
            foreach (var name in new[] { stem + "." + ReadmeName, stem + ".md" })
            {
                string text = ReadFile(Path.Combine(dir, name));
                if (text.Trim().Length > 0) return text;
            }

            if (IsSharedFolder(dir)) return "";
            string readme = ReadFile(Path.Combine(dir, ReadmeName));
            if (readme.Trim().Length > 0) return readme;
            var package = PackageManifestBeside.TryRead(full);
            if (package == null) return "";
            if (!string.Equals(package.Folder, dir, StringComparison.OrdinalIgnoreCase))
            {
                string packageReadme = ReadFile(Path.Combine(package.Folder, ReadmeName));
                if (packageReadme.Trim().Length > 0) return packageReadme;
            }

            return NormalizePlainText(package.Description);
        }
        catch { return ""; }
    }

    /// <summary>
    /// True for a folder many plugins share: <c>BepInEx</c> itself, and <c>plugins</c>, <c>progenitor-reload</c> or
    /// <c>progenitor-pending</c> directly under it. A mod's own subfolder named <c>plugins</c> is not shared.
    /// </summary>
    internal static bool IsSharedFolder(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return true;
        string trimmed = dir!.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string name = Path.GetFileName(trimmed);
        if (string.Equals(name, "BepInEx", StringComparison.OrdinalIgnoreCase)) return true;
        string parent = Path.GetFileName(Path.GetDirectoryName(trimmed) ?? "");
        if (!string.Equals(parent, "BepInEx", StringComparison.OrdinalIgnoreCase)) return false;
        foreach (var shared in SharedUnderBepInEx)
            if (string.Equals(name, shared, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    internal static string ReadFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return "";
            using var stream = File.OpenRead(path);
            return ReadBounded(stream);
        }
        catch { return ""; }
    }

    /// <summary>At most <see cref="MaxChars"/> characters, buffered by the stream's own length when it has one.</summary>
    internal static string ReadBounded(Stream stream)
    {
        long length = -1;
        try { if (stream.CanSeek) length = stream.Length - stream.Position; }
        catch { length = -1; }
        int capacity = length >= 0 ? (int)Math.Min(length, MaxChars) : 4096;
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var sb = new StringBuilder(capacity);
        var chunk = new char[Math.Max(16, Math.Min(4096, capacity + 1))];
        while (sb.Length < MaxChars)
        {
            int n = reader.Read(chunk, 0, Math.Min(chunk.Length, MaxChars - sb.Length));
            if (n <= 0) break;
            sb.Append(chunk, 0, n);
        }

        return sb.ToString();
    }

    private static string Cap(string? s)
    {
        s ??= "";
        return s.Length > MaxChars ? s.Substring(0, MaxChars) : s;
    }
}

/// <summary>
/// A Thunderstore package <c>manifest.json</c> for a DLL, in the layouts mod managers install: beside the DLL or
/// one folder up (<c>plugins/Owner-Name/X.dll</c>, <c>plugins/Owner-Name/Sub/X.dll</c>,
/// <c>plugins/Owner-Name/plugins/X.dll</c>), in a folder named <c>Owner-Name</c> or, extracted by hand,
/// <c>Owner-Name-&lt;version&gt;</c>. The folder supplies the owner, which the manifest does not carry; a manifest in
/// a shared folder, or one whose name disagrees with its folder, is not read. Reads are cached by DLL path and
/// revalidated from file stamps at most every <see cref="RevalidateMs"/>.
/// </summary>
internal sealed class PackageManifestBeside
{
    private const int MaxBytes = 256 * 1024;

    /// <summary>How long a cached answer is used without looking at the disk again. Tests set 0.</summary>
    internal static long RevalidateMs { get; set; } = 2000;

    private static readonly object Gate = new();
    private static readonly Dictionary<string, CacheRow> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Stopwatch Clock = Stopwatch.StartNew();

    private sealed class CacheRow
    {
        internal PackageManifestBeside? Value;
        internal string Stamp = "";
        internal long CheckedMs;
    }

    private PackageManifestBeside(string owner, string name, string version, string description, string folder, string websiteUrl)
    {
        Owner = owner;
        Name = name;
        Version = version;
        Description = description;
        Folder = folder;
        WebsiteUrl = websiteUrl;
    }

    internal string Owner { get; }
    internal string Name { get; }
    internal string Version { get; }
    internal string Description { get; }

    /// <summary>The manifest's <c>website_url</c> (the mod's site or repository), or empty.</summary>
    internal string WebsiteUrl { get; }

    /// <summary>The package folder that holds the manifest.</summary>
    internal string Folder { get; }

    /// <summary><c>Owner-Name</c>, the catalog key.</summary>
    internal string Package => Owner + "-" + Name;

    internal static void ForgetCache()
    {
        lock (Gate) Cache.Clear();
    }

    internal static PackageManifestBeside? TryRead(string? dllPath)
    {
        if (string.IsNullOrWhiteSpace(dllPath)) return null;
        string full;
        try { full = Path.GetFullPath(dllPath!.Trim()); }
        catch { return null; }
        long now = Clock.ElapsedMilliseconds;
        CacheRow? row;
        lock (Gate)
        {
            if (Cache.TryGetValue(full, out row) && now - row.CheckedMs < RevalidateMs && RevalidateMs > 0) return row.Value;
        }

        var candidates = Candidates(full);
        string stamp = StampOf(candidates);
        lock (Gate)
        {
            if (row != null && string.Equals(row.Stamp, stamp, StringComparison.Ordinal))
            {
                row.CheckedMs = now;
                return row.Value;
            }
        }

        PackageManifestBeside? value = null;
        foreach (var dir in candidates)
        {
            value = ReadIn(dir);
            if (value != null) break;
        }

        lock (Gate) Cache[full] = new CacheRow { Value = value, Stamp = stamp, CheckedMs = now };
        return value;
    }

    /// <summary>The DLL's folder, then its parent; never a shared folder or anything above one.</summary>
    private static List<string> Candidates(string fullDll)
    {
        var list = new List<string>(2);
        try
        {
            string? dir = Path.GetDirectoryName(fullDll);
            for (int i = 0; i < 2 && !string.IsNullOrEmpty(dir); i++)
            {
                if (PluginAboutReader.IsSharedFolder(dir)) break;
                list.Add(dir!);
                dir = Path.GetDirectoryName(dir);
            }
        }
        catch { /* none */ }
        return list;
    }

    private static string StampOf(List<string> dirs)
    {
        var sb = new StringBuilder();
        foreach (var dir in dirs)
        {
            try
            {
                var info = new FileInfo(Path.Combine(dir, PluginAboutReader.ManifestName));
                sb.Append(dir).Append('|');
                if (info.Exists) sb.Append(info.Length).Append(':').Append(info.LastWriteTimeUtc.Ticks);
                sb.Append(';');
            }
            catch { sb.Append("?;"); }
        }

        return sb.ToString();
    }

    private static PackageManifestBeside? ReadIn(string dir)
    {
        try
        {
            string path = Path.Combine(dir, PluginAboutReader.ManifestName);
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxBytes) return null;
            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            string name = Text(root, "name");
            string version = Text(root, "version_number");
            if (name.Length == 0 || version.Length == 0) return null;
            string owner = OwnerFromFolder(Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)), name, version);
            if (owner.Length == 0) return null;
            return new PackageManifestBeside(owner, name, version, Text(root, "description"), dir, Text(root, "website_url"));
        }
        catch { return null; }
    }

    /// <summary>
    /// The owner a package folder's name gives: <c>Owner-Name</c>, or <c>Owner-Name-&lt;version&gt;</c> with the
    /// manifest's own version. Empty when the folder does not name this package.
    /// </summary>
    internal static string OwnerFromFolder(string folder, string name, string version)
    {
        string f = folder ?? "";
        string versionSuffix = "-" + version;
        if (version.Length > 0 && f.EndsWith(versionSuffix, StringComparison.OrdinalIgnoreCase))
            f = f.Substring(0, f.Length - versionSuffix.Length);
        string suffix = "-" + name;
        if (!f.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) || f.Length <= suffix.Length) return "";
        return f.Substring(0, f.Length - suffix.Length);
    }

    private static string Text(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? (value.GetString() ?? "").Trim()
            : "";
}

/// <summary>
/// What each plugin's assembly said about itself (<see cref="PluginAboutReader.FromType"/>), keyed by plugin GUID:
/// recorded when Progenitor loads the plugin, or read once from a plugin another loader holds. Strings only, so it
/// pins no collectible generation; a later load replaces the row.
/// </summary>
internal static class PluginAboutStore
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, PluginAboutFacts> ById = new(StringComparer.OrdinalIgnoreCase);

    internal static void Note(string? id, PluginAboutFacts? facts)
    {
        if (string.IsNullOrWhiteSpace(id) || facts == null) return;
        lock (Gate) ById[id!.Trim()] = facts;
    }

    /// <summary>Record what <paramref name="type"/>'s assembly says, once per load. Never throws.</summary>
    internal static void NoteFromType(string? id, Type? type)
    {
        try { Note(id, PluginAboutReader.FromType(type)); }
        catch { /* the About text is evidence, never a gate */ }
    }

    internal static bool TryGet(string? id, out PluginAboutFacts facts)
    {
        facts = PluginAboutFacts.Empty;
        if (string.IsNullOrWhiteSpace(id)) return false;
        lock (Gate)
        {
            if (!ById.TryGetValue(id!.Trim(), out var found)) return false;
            facts = found;
            return true;
        }
    }

    internal static void ResetForTests()
    {
        lock (Gate) ById.Clear();
    }
}
