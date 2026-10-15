using System;
using System.Collections.Generic;
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
/// <item>the Thunderstore README of the listing this exact install came from: the install ledger names
/// the package and version, or a package <c>manifest.json</c> beside the DLL does;</item>
/// <item>an embedded resource in the plugin assembly named <c>README.md</c> or ending in <c>.README.md</c>
/// (<c>&lt;EmbeddedResource Include="README.md" /&gt;</c> in the project file);</item>
/// <item>a file beside the DLL: <c>&lt;dll name&gt;.README.md</c> or <c>&lt;dll name&gt;.md</c>, and in a
/// folder of the mod's own (not <c>plugins</c> or <c>progenitor-reload</c> itself) <c>README.md</c>, then
/// the <c>description</c> of a <c>manifest.json</c> there;</item>
/// <item>this attribute;</item>
/// <item>the description the plugin registered with <c>ctx.Menu.Register(name, description)</c>, then its
/// <c>[assembly: AssemblyDescription]</c>;</item>
/// <item><c>No description available.</c></item>
/// </list>
/// <para>A listing found any other way (the plugin's GUID or name spelling a package name) is never
/// shown for an installed mod: it may be another version, or another project with the same name.</para>
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
        _ => "none",
    };

    /// <summary>
    /// The order in <see cref="ModAboutAttribute"/>: a verified listing's README, then the plugin's embedded
    /// README, the file beside its DLL, its <c>[ModAbout]</c>, its description, and nothing.
    /// <paramref name="listingReadme"/> is read only when <paramref name="listingVerified"/>.
    /// </summary>
    internal static ModAboutText Choose(bool listingVerified, string? listingReadme, PluginAboutFacts? plugin)
    {
        if (listingVerified && HasText(listingReadme)) return new ModAboutText(listingReadme!, ModAboutSource.Listing);
        plugin ??= PluginAboutFacts.Empty;
        if (HasText(plugin.Embedded)) return new ModAboutText(plugin.Embedded, ModAboutSource.Embedded);
        if (HasText(plugin.Beside)) return new ModAboutText(plugin.Beside, ModAboutSource.Beside);
        if (HasText(plugin.Declared)) return new ModAboutText(plugin.Declared, ModAboutSource.Declared);
        if (HasText(plugin.Description)) return new ModAboutText(plugin.Description, ModAboutSource.Description);
        return default;
    }

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

    /// <summary>Folders many plugins share: a <c>README.md</c> or <c>manifest.json</c> there belongs to none of them.</summary>
    private static readonly string[] SharedFolders = { "plugins", "progenitor-reload", "progenitor-pending", "BepInEx" };

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

    /// <summary>The assembly's <c>README.md</c> resource, or empty.</summary>
    internal static string EmbeddedReadme(Assembly? assembly)
    {
        if (assembly == null) return "";
        try
        {
            return EmbeddedReadme(assembly.GetManifestResourceNames(), name => assembly.GetManifestResourceStream(name));
        }
        catch { return ""; }
    }

    /// <summary>
    /// The resource named exactly <c>README.md</c>, else the shortest name ending in <c>.README.md</c> (the
    /// SDK names an <c>EmbeddedResource</c> <c>&lt;RootNamespace&gt;.README.md</c>), case-insensitively.
    /// </summary>
    internal static string EmbeddedReadme(IEnumerable<string>? names, Func<string, Stream?> open)
    {
        if (names == null || open == null) return "";
        string? best = null;
        foreach (var name in names)
        {
            if (string.IsNullOrEmpty(name)) continue;
            if (string.Equals(name, ReadmeName, StringComparison.OrdinalIgnoreCase)) { best = name; break; }
            if (!name.EndsWith("." + ReadmeName, StringComparison.OrdinalIgnoreCase)) continue;
            if (best == null || name.Length < best.Length
                || (name.Length == best.Length && string.CompareOrdinal(name, best) < 0)) best = name;
        }

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
                return Cap(a.ConstructorArguments[0].Value as string).Trim();
            }
        }
        catch { /* none */ }
        return "";
    }

    /// <summary>
    /// The text beside a DLL: <c>&lt;stem&gt;.README.md</c>, <c>&lt;stem&gt;.md</c>, then — in a folder of the
    /// mod's own — <c>README.md</c> and the <c>manifest.json</c> description. Empty when there is none.
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
            return PackageManifestBeside.TryRead(full)?.Description ?? "";
        }
        catch { return ""; }
    }

    /// <summary>True for a folder many plugins share (<c>plugins</c>, <c>progenitor-reload</c>, ...).</summary>
    internal static bool IsSharedFolder(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return true;
        string name = Path.GetFileName(dir!.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        foreach (var shared in SharedFolders)
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

    private static string ReadBounded(Stream stream)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var buffer = new char[MaxChars + 1];
        int read = 0;
        while (read < buffer.Length)
        {
            int n = reader.Read(buffer, read, buffer.Length - read);
            if (n <= 0) break;
            read += n;
        }

        return new string(buffer, 0, Math.Min(read, MaxChars));
    }

    private static string Cap(string? s)
    {
        s ??= "";
        return s.Length > MaxChars ? s.Substring(0, MaxChars) : s;
    }
}

/// <summary>
/// A Thunderstore package <c>manifest.json</c> beside a DLL, in a folder named <c>&lt;Owner&gt;-&lt;Name&gt;</c>
/// (the layout a mod manager installs). The folder supplies the owner, which the manifest itself does not
/// carry; a manifest in a shared folder, or one whose name disagrees with its folder, is not read.
/// </summary>
internal sealed class PackageManifestBeside
{
    private const int MaxBytes = 256 * 1024;

    private PackageManifestBeside(string owner, string name, string version, string description)
    {
        Owner = owner;
        Name = name;
        Version = version;
        Description = description;
    }

    internal string Owner { get; }
    internal string Name { get; }
    internal string Version { get; }
    internal string Description { get; }

    /// <summary><c>Owner-Name</c>, the catalog key.</summary>
    internal string Package => Owner + "-" + Name;

    internal static PackageManifestBeside? TryRead(string? dllPath)
    {
        if (string.IsNullOrWhiteSpace(dllPath)) return null;
        try
        {
            string? dir = Path.GetDirectoryName(Path.GetFullPath(dllPath!.Trim()));
            if (string.IsNullOrEmpty(dir) || PluginAboutReader.IsSharedFolder(dir)) return null;
            string path = Path.Combine(dir, PluginAboutReader.ManifestName);
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxBytes) return null;
            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            string name = Text(root, "name");
            string version = Text(root, "version_number");
            if (name.Length == 0 || version.Length == 0) return null;
            string folder = Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            string suffix = "-" + name;
            if (!folder.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) || folder.Length <= suffix.Length) return null;
            string owner = folder.Substring(0, folder.Length - suffix.Length);
            return new PackageManifestBeside(owner, name, version, Text(root, "description"));
        }
        catch { return null; }
    }

    private static string Text(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? (value.GetString() ?? "").Trim()
            : "";
}

/// <summary>
/// What each Progenitor plugin's assembly said about itself when it loaded (<see cref="PluginAboutReader.FromType"/>),
/// keyed by plugin GUID. Strings only, so it pins no collectible generation; a later load replaces the row.
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
