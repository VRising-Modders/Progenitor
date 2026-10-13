using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;

namespace Progenitor.Bootstrap;

/// <summary>What <see cref="Il2CppInjectionPolicy.Scan"/> read from one plugin image.</summary>
internal sealed class Il2CppInjectionScan
{
    internal static readonly Il2CppInjectionScan None = new(Array.Empty<string>(), coreBound: false, error: "");

    internal Il2CppInjectionScan(string[]? sites, bool coreBound, string? error, string? assemblyName = null)
    {
        AssemblyName = assemblyName ?? "";
        Sites = sites ?? Array.Empty<string>();
        CoreBound = coreBound;
        Error = error ?? "";
    }

    /// <summary>The injection call sites the image references (<c>ClassInjector::RegisterTypeInIl2Cpp</c>, ...).</summary>
    internal string[] Sites { get; }

    /// <summary>True when the image references at least one injection call site.</summary>
    internal bool Injects => Sites.Length > 0;

    /// <summary>
    /// The image references Core (<see cref="PluginAssemblyResolver.CoreSimpleName"/>), so its context
    /// binds the Core generation it was loaded against and keeping it loaded keeps that Core alive.
    /// </summary>
    internal bool CoreBound { get; }

    /// <summary>The image's own assembly name, or "".</summary>
    internal string AssemblyName { get; }

    /// <summary>Why the image could not be read, or "". An unreadable image is not marked; it will not load either.</summary>
    internal string Error { get; }
}

/// <summary>What an event does to a loaded reload-folder plugin.</summary>
internal enum LifetimeEvent
{
    /// <summary>The DLL on disk changed (a redeploy, the Install tab's in-place swap).</summary>
    FileChanged,
    /// <summary>The DLL left <c>progenitor-reload/</c>.</summary>
    FileRemoved,
    /// <summary>A collectible Core swap unloads and reloads every reload-folder plugin.</summary>
    CoreSwap,
    /// <summary>Per-server mod pause.</summary>
    Suspend,
    /// <summary>The process is exiting.</summary>
    Shutdown,
}

internal enum LifetimeAction
{
    /// <summary>The ordinary path: unload (and reload) the plugin.</summary>
    Proceed,
    /// <summary>Leave the running build loaded; the change takes effect at the next start.</summary>
    KeepRunning,
    /// <summary>Refuse the whole operation (a Core swap that would have to unload a Core-bound plugin, a pause).</summary>
    Refuse,
}

/// <summary>
/// Plugins that register their own IL2CPP types (job FINJ1, observed live 2026-10-06 with a third-party chat plugin).
///
/// <para><c>ClassInjector.RegisterTypeInIl2Cpp</c> has no unregister and keys the IL2CPP class by
/// managed <c>FullName</c> for the life of the process. A reload-folder plugin that injected a type
/// cannot be reloaded in place: the next generation's <c>RegisterTypeInIl2Cpp</c> throws
/// <c>ArgumentException: Type with FullName ... is already injected</c>, and whatever that plugin
/// built on the type (for that chat plugin, its chat canvas driver) never comes back until the game restarts.
/// The injected class also keeps the old collectible context reachable, so the unload does not
/// release anything either.</para>
///
/// <para>Such a plugin is <b>process-lifetime</b>: it stays loaded across file changes, removals and
/// Core swaps, and the new file takes effect at the next start ("restart required"). A Core swap
/// keeps it loaded when it does not reference Core; when it does, it would pin the outgoing Core,
/// so the swap is refused instead. Detection is static (the image references an injection call
/// site, checked before the image loads) and at runtime (<c>ClassInjector</c>'s injected names
/// include a type the plugin's assembly defines), so a helper-mediated injection is caught once it
/// has happened.</para>
///
/// <para>Game-free: the scanner reads metadata only and every decision and sentence is here, so
/// <c>Il2CppInjectionPolicyTests</c> pins them without a game.</para>
/// </summary>
internal static class Il2CppInjectionPolicy
{
    internal const string ClassInjectorType = "Il2CppInterop.Runtime.Injection.ClassInjector";
    internal const string RegisterPrefix = "RegisterTypeInIl2Cpp";
    /// <summary><c>BasePlugin.AddComponent&lt;T&gt;()</c> registers <c>T</c> through ClassInjector.</summary>
    internal const string BasePluginType = "BepInEx.Unity.IL2CPP.BasePlugin";
    internal const string AddComponent = "AddComponent";
    /// <summary><c>IL2CPPChainloader.AddUnityComponent&lt;T&gt;()</c>, the helper <c>AddComponent</c> calls.</summary>
    internal const string ChainloaderType = "BepInEx.Unity.IL2CPP.IL2CPPChainloader";
    internal const string AddUnityComponent = "AddUnityComponent";

    /// <summary>The word the Mods tab, <c>reload_status</c> and the deploy hold use for this state.</summary>
    internal const string StateWord = "process-lifetime";
    internal const string PendingNone = "";
    internal const string PendingChanged = "changed";
    internal const string PendingRemoved = "removed";

    /// <summary>
    /// Marker in <see cref="RefuseCoreSwap"/>. <c>CoreWatchPolicy.ParkOnRefusal</c> parks the watcher on
    /// it: the refusal lasts until the process restarts, exactly like a Core generation that did not
    /// collect, so asking again every few seconds would only repeat the line.
    /// </summary>
    internal const string RefusalMarker = "process-lifetime plugin";

    // ---- the scan ----------------------------------------------------------------------------

    /// <summary>
    /// Read <paramref name="image"/>'s metadata for injection call sites and a Core reference. Never
    /// throws; an unreadable image reports <see cref="Il2CppInjectionScan.Error"/> and no sites.
    /// </summary>
    internal static Il2CppInjectionScan Scan(byte[]? image)
    {
        if (image == null || image.Length == 0) return Il2CppInjectionScan.None;
        try
        {
            using var stream = new MemoryStream(image, writable: false);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata) return new Il2CppInjectionScan(null, false, "no metadata");
            var md = pe.GetMetadataReader();

            var sites = new List<string>();
            foreach (var handle in md.MemberReferences)
            {
                var member = md.GetMemberReference(handle);
                string name = md.GetString(member.Name);
                if (member.Parent.Kind != HandleKind.TypeReference) continue;
                string parent = FullName(md, (TypeReferenceHandle)member.Parent);
                if (!IsInjectionSite(parent, name)) continue;
                string site = parent + "::" + name;
                if (!sites.Contains(site)) sites.Add(site);
            }

            bool coreBound = false;
            foreach (var handle in md.AssemblyReferences)
            {
                string asm = md.GetString(md.GetAssemblyReference(handle).Name);
                if (string.Equals(asm, PluginAssemblyResolver.CoreSimpleName, StringComparison.Ordinal))
                    coreBound = true;
            }
            sites.Sort(StringComparer.Ordinal);
            string self = md.IsAssembly ? md.GetString(md.GetAssemblyDefinition().Name) : "";
            return new Il2CppInjectionScan(sites.ToArray(), coreBound, "", self);
        }
        catch (Exception e)
        {
            return new Il2CppInjectionScan(null, false, e.GetType().Name + ": " + e.Message);
        }
    }

    /// <summary>True for a member reference that registers a managed type with IL2CPP.</summary>
    internal static bool IsInjectionSite(string? parentFullName, string? memberName)
    {
        if (string.IsNullOrEmpty(parentFullName) || string.IsNullOrEmpty(memberName)) return false;
        if (string.Equals(parentFullName, ClassInjectorType, StringComparison.Ordinal))
            return memberName!.StartsWith(RegisterPrefix, StringComparison.Ordinal);
        if (string.Equals(parentFullName, BasePluginType, StringComparison.Ordinal))
            return string.Equals(memberName, AddComponent, StringComparison.Ordinal);
        if (string.Equals(parentFullName, ChainloaderType, StringComparison.Ordinal))
            return string.Equals(memberName, AddUnityComponent, StringComparison.Ordinal);
        return false;
    }

    private static string FullName(MetadataReader md, TypeReferenceHandle handle)
    {
        var tr = md.GetTypeReference(handle);
        string name = md.GetString(tr.Name);
        if (tr.ResolutionScope.Kind == HandleKind.TypeReference)
            return FullName(md, (TypeReferenceHandle)tr.ResolutionScope) + "+" + name;
        string ns = md.GetString(tr.Namespace);
        return ns.Length == 0 ? name : ns + "." + name;
    }

    // ---- runtime detection ---------------------------------------------------------------------

    /// <summary>
    /// The entries of <c>ClassInjector</c>'s injected-name set that <paramref name="definedByPlugin"/>
    /// claims (the plugin's assembly defines a type with that full name; Bootstrap's own injected types
    /// live in Bootstrap's assembly, so they are never claimed). A predicate that throws claims nothing for that name.
    /// </summary>
    internal static List<string> MatchInjected(IEnumerable<string?>? injectedFullNames, Func<string, bool>? definedByPlugin)
    {
        var hits = new List<string>();
        if (injectedFullNames == null || definedByPlugin == null) return hits;
        foreach (var name in injectedFullNames)
        {
            if (string.IsNullOrEmpty(name)) continue;
            bool mine;
            try { mine = definedByPlugin(name); }
            catch { mine = false; }
            if (mine && !hits.Contains(name)) hits.Add(name);
        }
        return hits;
    }

    // ---- decisions -----------------------------------------------------------------------------

    /// <summary>
    /// What <paramref name="ev"/> does to a loaded plugin. An ordinary plugin always proceeds. A
    /// process-lifetime one is never unloaded in place: it keeps running on a file change, a removal
    /// and a Core swap it is not bound to; a Core swap it IS bound to (its context resolves that Core)
    /// and a pause are refused. Process exit unloads everything as before.
    /// </summary>
    internal static LifetimeAction Decide(LifetimeEvent ev, bool processLifetime, bool coreBound)
    {
        if (!processLifetime || ev == LifetimeEvent.Shutdown) return LifetimeAction.Proceed;
        switch (ev)
        {
            case LifetimeEvent.FileChanged:
            case LifetimeEvent.FileRemoved:
                return LifetimeAction.KeepRunning;
            case LifetimeEvent.CoreSwap:
                return coreBound ? LifetimeAction.Refuse : LifetimeAction.KeepRunning;
            default:
                return LifetimeAction.Refuse;
        }
    }

    /// <summary>
    /// The pending state of a kept plugin from what is on disk: removed when the file is gone,
    /// changed when its stamp differs from the one that was loaded, none when it is back to that one.
    /// </summary>
    internal static string PendingFor(bool fileExists, DateTime loadedWrite, DateTime currentWrite)
    {
        if (!fileExists) return PendingRemoved;
        return loadedWrite == currentWrite ? PendingNone : PendingChanged;
    }

    // ---- the words ---------------------------------------------------------------------------

    /// <summary>One line at load: the plugin is process-lifetime and why.</summary>
    internal static string FormatDetected(string? file, IReadOnlyList<string>? sites, bool coreBound, bool runtime = false) =>
        "Reload: " + (file ?? "") + " registers its own IL2CPP types ("
        + (sites is { Count: > 0 } ? string.Join(", ", sites) : "unknown site")
        + (runtime ? ", seen in ClassInjector" : "")
        + "); IL2CPP type injection lasts for the life of the process, so it is " + StateWord
        + ": a file change or a Core swap will not unload or reload it"
        + (coreBound ? " (it references Progenitor, so a Core swap is refused while it is loaded)" : "")
        + "; restart the game to load a new build.";

    internal static string FormatKeptOnChange(string? file) =>
        "Reload: " + (file ?? "") + " changed, but it registers its own IL2CPP types and cannot be reloaded in place; "
        + "the running build stays loaded. Restart required: the new build loads at the next start.";

    internal static string FormatKeptOnRemoval(string? file) =>
        "Reload: " + (file ?? "") + " was removed, but it registers its own IL2CPP types and cannot be unloaded in place; "
        + "it stays loaded until the game restarts.";

    internal static string FormatBackToLoaded(string? file) =>
        "Reload: " + (file ?? "") + " is back to the build that is running; no restart is pending for it.";

    internal static string FormatSecondCopyRefused(string? file, string? holder) =>
        "Reload: skipped " + (file ?? "") + "; it is another copy of " + (holder ?? "")
        + ", which registers its own IL2CPP types and stays loaded until the game restarts. Restart required.";

    internal static string FormatKeptAcrossCoreSwap(string? file) =>
        "Reload: Core swap keeps " + (file ?? "") + " loaded: it registers its own IL2CPP types and does not reference Progenitor.";

    /// <summary>The Core-swap refusal. Ends in the restart marker so the watcher parks on it.</summary>
    internal static string RefuseCoreSwap(IReadOnlyList<string>? files) =>
        "Core reload refused: " + RefusalMarker + " "
        + (files is { Count: > 0 } ? string.Join(", ", files) : "(unknown)")
        + " registers its own IL2CPP types and references this Core; it cannot be unloaded, so this Core generation "
        + "is still alive; restart the process.";

    internal static string FormatSuspendRefused(string? guid) =>
        (guid ?? "") + " registers its own IL2CPP types and cannot be paused without a restart";

    // ---- the row Core reads ------------------------------------------------------------------

    /// <summary>One process-lifetime plugin as Core's Mods tab and <c>reload_status</c> see it. Plain data.</summary>
    internal readonly struct Row
    {
        internal Row(string file, string[]? guids, string[]? names, bool coreBound, string? pending, string[]? sites)
        {
            File = file ?? "";
            Guids = guids ?? Array.Empty<string>();
            Names = names ?? Array.Empty<string>();
            CoreBound = coreBound;
            Pending = pending ?? "";
            Sites = sites ?? Array.Empty<string>();
        }

        internal string File { get; }
        internal string[] Guids { get; }
        internal string[] Names { get; }
        internal bool CoreBound { get; }
        /// <summary><see cref="PendingNone"/>, <see cref="PendingChanged"/> or <see cref="PendingRemoved"/>.</summary>
        internal string Pending { get; }
        internal string[] Sites { get; }
    }

    private const char Field = '\t';
    private const char Item = '|';

    /// <summary>
    /// <c>file \t guid|guid \t name|name \t 0/1 \t pending \t site|site</c>. Core parses this through
    /// reflection-free strings (<c>Progenitor.Reload.ProcessLifetimeRows</c>); the shape is pinned by a test.
    /// </summary>
    internal static string FormatRow(Row row)
    {
        var sb = new StringBuilder();
        sb.Append(Clean(row.File)).Append(Field);
        sb.Append(Join(row.Guids)).Append(Field);
        sb.Append(Join(row.Names)).Append(Field);
        sb.Append(row.CoreBound ? '1' : '0').Append(Field);
        sb.Append(Clean(row.Pending)).Append(Field);
        sb.Append(Join(row.Sites));
        return sb.ToString();
    }

    internal static bool TryParseRow(string? text, out Row row)
    {
        row = default;
        if (string.IsNullOrEmpty(text)) return false;
        var parts = text!.Split(Field);
        if (parts.Length < 6 || parts[0].Length == 0) return false;
        row = new Row(parts[0], Split(parts[1]), Split(parts[2]), parts[3] == "1", parts[4], Split(parts[5]));
        return true;
    }

    private static string Clean(string? s) =>
        (s ?? "").Replace(Field, ' ').Replace(Item, ' ').Replace('\n', ' ').Replace('\r', ' ');

    private static string Join(string[] items)
    {
        var cleaned = new List<string>(items.Length);
        foreach (var item in items)
            if (!string.IsNullOrEmpty(item)) cleaned.Add(Clean(item));
        return string.Join(Item.ToString(), cleaned);
    }

    private static string[] Split(string s) =>
        s.Length == 0 ? Array.Empty<string>() : s.Split(new[] { Item }, StringSplitOptions.RemoveEmptyEntries);
}
