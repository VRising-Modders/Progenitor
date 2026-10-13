using System;
using System.Reflection;

namespace Progenitor.Ui;

/// <summary>
/// The running framework components' versions as the UI kit prints them (docs/versioning.md):
/// the assembly's informational version with its <c>+&lt;sha&gt;[.dirty]</c> metadata removed, so a
/// release reads <c>2026.9.58</c> and an unmerged build <c>2026.9.58.3</c>. Game-free; the only
/// runtime input is which assemblies are loaded.
/// </summary>
internal static class FrameworkVersions
{
    /// <summary>The plugin id Core's own panels are created under. Panels with this id get the framework footer.</summary>
    internal const string CorePluginId = "progenitor";

    /// <summary>Product name in front of Core's version.</summary>
    internal const string CoreName = "Progenitor";

    internal const string BootstrapName = "Bootstrap";

    /// <summary>Core's name on the Mods workspace footer, where it sits beside Bootstrap.</summary>
    internal const string WorkspaceCoreName = "Progenitor Core";

    internal const string DevName = "Dev";

    internal const string BootstrapAssemblyName = "Progenitor.Bootstrap";

    internal const string DevAssemblyName = "Progenitor.Dev";

    /// <summary>What a version reads as when the assembly carries none at all.</summary>
    internal const string Unknown = "unknown";

    /// <summary>Between components on the Mods workspace footer. U+00B7 is Latin-1, so the vanilla TMP font has it.</summary>
    internal const string Separator = " · ";

    private static string? _core;

    /// <summary>Test seam: resolves a loaded assembly by simple name. Null means the real AppDomain scan.</summary>
    internal static Func<string, Assembly?>? FindLoadedForTests;

    /// <summary>Test seam: overrides <see cref="Core"/>. Null means this assembly's own version.</summary>
    internal static string? CoreForTests;

    /// <summary>
    /// Strip build metadata and whitespace: <c>2026.9.58+2a14494cde</c> is <c>2026.9.58</c>,
    /// <c>2026.9.58.1+2a14494cde.dirty</c> is <c>2026.9.58.1</c>. Null or empty stays empty.
    /// </summary>
    internal static string Normalize(string? informational)
    {
        if (string.IsNullOrWhiteSpace(informational)) return "";
        string v = informational!.Trim();
        int plus = v.IndexOf('+');
        if (plus >= 0) v = v.Substring(0, plus);
        return v.Trim();
    }

    /// <summary>
    /// The fallback when there is no informational version: <c>AssemblyVersion</c> is always four
    /// numbers and a release's fourth is <c>0</c> (docs/versioning.md), so <c>2026.9.12.0</c> prints
    /// as <c>2026.9.12</c> and <c>2026.9.12.3</c> keeps its sub-release.
    /// </summary>
    internal static string FromAssemblyVersion(Version? version)
    {
        if (version == null) return "";
        int build = version.Build < 0 ? 0 : version.Build;
        string three = version.Major + "." + version.Minor + "." + build;
        return version.Revision > 0 ? three + "." + version.Revision : three;
    }

    /// <summary>The printed version of <paramref name="assembly"/>, or <see cref="Unknown"/>.</summary>
    internal static string Of(Assembly? assembly)
    {
        if (assembly == null) return Unknown;
        string v = "";
        try
        {
            v = Normalize(assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);
        }
        catch { /* fall back to the assembly version */ }
        if (v.Length == 0)
        {
            try { v = FromAssemblyVersion(assembly.GetName().Version); }
            catch { v = ""; }
        }
        return v.Length == 0 ? Unknown : v;
    }

    /// <summary>The running Core's version (this assembly). Cached for the generation.</summary>
    internal static string Core
    {
        get
        {
            if (CoreForTests != null) return CoreForTests;
            return _core ??= Of(typeof(FrameworkVersions).Assembly);
        }
    }

    /// <summary>Bootstrap's version, or null when it is not loaded (a test host, a partial install).</summary>
    internal static string? Bootstrap => Loaded(BootstrapAssemblyName);

    /// <summary>The dev bridge's version, or null when it is not loaded.</summary>
    internal static string? Dev => Loaded(DevAssemblyName);

    /// <summary>
    /// A loaded assembly's version by simple name. The <b>last</b> match wins: a reloaded
    /// generation is loaded after the one it replaced.
    /// </summary>
    internal static string? Loaded(string simpleName)
    {
        try
        {
            var seam = FindLoadedForTests;
            if (seam != null)
            {
                var found = seam(simpleName);
                return found == null ? null : Of(found);
            }

            Assembly? last = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                string? name = null;
                try { name = asm.GetName().Name; }
                catch { /* an unloading assembly */ }
                if (string.Equals(name, simpleName, StringComparison.Ordinal)) last = asm;
            }
            return last == null ? null : Of(last);
        }
        catch
        {
            return null;
        }
    }

    /// <summary><c>Progenitor 2026.9.58</c> — the line every Core-owned prompt carries.</summary>
    internal static string PromptFooter(string? coreVersion) =>
        CoreName + " " + (string.IsNullOrWhiteSpace(coreVersion) ? Unknown : Normalize(coreVersion));

    /// <summary>
    /// <c>Progenitor Core 2026.9.58 · Bootstrap 2026.9.57</c>. The dev bridge is not listed: it is a
    /// developer tool players never have, and its version normally repeats Core's (the user,
    /// 2026-09-29, on "Progenitor 2026.9.64 · Bootstrap 0.1.0 · Dev 2026.9.64": "why does it say the
    /// progenitor version twice?"); the bridge's own status reports it. A Bootstrap that cannot be
    /// found reads <c>Bootstrap unknown</c> rather than vanishing: Core cannot run without one, so a
    /// missing reading is itself worth seeing.
    /// </summary>
    internal static string WorkspaceFooter(string? coreVersion, string? bootstrapVersion) =>
        WorkspaceCoreName + " " + (string.IsNullOrWhiteSpace(coreVersion) ? Unknown : Normalize(coreVersion))
        + Separator + BootstrapName + " "
        + (string.IsNullOrWhiteSpace(bootstrapVersion) ? Unknown : Normalize(bootstrapVersion));

    /// <summary>The live prompt footer.</summary>
    internal static string CurrentPromptFooter() => PromptFooter(Core);

    /// <summary>The live Mods workspace footer.</summary>
    internal static string CurrentWorkspaceFooter() => WorkspaceFooter(Core, Bootstrap);

    internal static void ResetForTests()
    {
        FindLoadedForTests = null;
        CoreForTests = null;
    }
}
