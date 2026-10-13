using System;
using System.Collections.Generic;
using Progenitor.Install.Admission;
using Progenitor.Networking;

namespace Progenitor.Install;

/// <summary>One DLL of an ordinary BepInEx package as the bytes describe it. No assembly was loaded.</summary>
internal sealed class OrdinaryDll
{
    public OrdinaryDll(
        string destination,
        string fileName,
        string sha256,
        long size,
        bool declaresPlugin,
        string pluginGuid,
        string pluginName,
        string pluginVersion,
        IReadOnlyList<string> processes,
        IReadOnlyList<string> dependencyGuids)
    {
        Destination = destination ?? "";
        FileName = fileName ?? "";
        Sha256 = sha256 ?? "";
        Size = size;
        DeclaresPlugin = declaresPlugin;
        PluginGuid = pluginGuid ?? "";
        PluginName = pluginName ?? "";
        PluginVersion = pluginVersion ?? "";
        Processes = processes ?? Array.Empty<string>();
        DependencyGuids = dependencyGuids ?? Array.Empty<string>();
    }

    /// <summary>The normalized zip destination with forward slashes (<c>BepInEx/plugins/A-B/X.dll</c>).</summary>
    public string Destination { get; }
    public string FileName { get; }
    public string Sha256 { get; }
    public long Size { get; }
    public bool DeclaresPlugin { get; }
    public string PluginGuid { get; }
    public string PluginName { get; }
    public string PluginVersion { get; }

    /// <summary>The executable names <c>[BepInProcess]</c> declared. Empty: the plugin never said.</summary>
    public IReadOnlyList<string> Processes { get; }

    /// <summary>The plugin GUIDs <c>[BepInDependency]</c> declared.</summary>
    public IReadOnlyList<string> DependencyGuids { get; }

    /// <summary>The folder the DLL sits in (<c>""</c> for the zip root).</summary>
    public string Folder
    {
        get
        {
            int slash = Destination.LastIndexOf('/');
            return slash < 0 ? "" : Destination.Substring(0, slash);
        }
    }
}

/// <summary>
/// INSTORD1: the approved-archive-layout verdict for an ordinary BepInEx package — one plugin
/// DLL, the companions beside it, and the processes the plugin says it loads in. Refused carries
/// the exact sentence the button shows; the caller then keeps its own earlier refusal.
/// </summary>
internal sealed class OrdinaryPackageLayout
{
    private OrdinaryPackageLayout(bool ok, string? refusal, OrdinaryDll? plugin, IReadOnlyList<OrdinaryDll> companions)
    {
        Ok = ok;
        Refusal = refusal;
        Plugin = plugin;
        Companions = companions ?? Array.Empty<OrdinaryDll>();
    }

    public bool Ok { get; }
    public string? Refusal { get; }
    public OrdinaryDll? Plugin { get; }
    public IReadOnlyList<OrdinaryDll> Companions { get; }

    public static OrdinaryPackageLayout Approve(OrdinaryDll plugin, IReadOnlyList<OrdinaryDll> companions) =>
        new(true, null, plugin, companions);

    public static OrdinaryPackageLayout Refuse(string reason) =>
        new(false, reason, null, Array.Empty<OrdinaryDll>());

    /// <summary><c>client</c> | <c>server</c> | <c>both</c> | <c>unknown</c> from the plugin's <c>[BepInProcess]</c>.</summary>
    public string LoadsIn => OrdinaryPackage.LoadsInName(Plugin?.Processes);

    /// <summary>The companion file names, in archive order.</summary>
    public IReadOnlyList<string> CompanionNames
    {
        get
        {
            var list = new List<string>(Companions.Count);
            for (int i = 0; i < Companions.Count; i++) list.Add(Companions[i].FileName);
            return list;
        }
    }
}

/// <summary>
/// INSTORD1 approved archive layout and plugin identity for an ordinary BepInEx package.
///
/// <para>A package is accepted when its DLLs sit at the zip root or in one folder under
/// <c>BepInEx/plugins/</c>, exactly one of them declares <c>[BepInPlugin]</c>, every other DLL in
/// that folder declares none, and no entry targets <c>patchers/</c>, <c>core/</c>, <c>config/</c>
/// or any other non-DLL location Progenitor would have to place. A README, manifest, icon,
/// changelog or license at the zip root is ignored, not installed. Anything else keeps the
/// caller's existing refusal: this type never widens a refusal into an install.</para>
///
/// <para>Identity comes from <see cref="ArtifactPreflight"/> over the bytes — <c>MetadataReader</c>
/// only, no <c>Assembly.Load</c>, no constructor, no module initializer — so a package name is
/// never reinterpreted as a plugin GUID.</para>
/// </summary>
internal static class OrdinaryPackage
{
    /// <summary>The client executable a <c>[BepInProcess]</c> names.</summary>
    public const string ClientProcess = "VRising.exe";

    /// <summary>The dedicated-server executable a <c>[BepInProcess]</c> names.</summary>
    public const string ServerProcess = "VRisingServer.exe";

    /// <summary>DLLs one evaluation will open (the preflight's image cap).</summary>
    public const int MaxDlls = 32;

    /// <summary>Per-DLL byte ceiling for the identity scan.</summary>
    public const long MaxDllBytes = 16L * 1024 * 1024;

    /// <summary>Root files this route ignores instead of placing.</summary>
    private static readonly string[] IgnoredRootFiles =
    {
        "readme.md", "readme.txt", "readme",
        "manifest.json", "progenitor.json",
        "icon.png", "icon.jpg",
        "changelog.md", "changelog.txt", "changelog",
        "license", "license.md", "license.txt", "licence", "licence.md", "licence.txt",
        "notice", "notice.md", "notice.txt",
        "thunderstore.toml",
    };

    /// <summary>
    /// Decide the layout. <paramref name="destinations"/> is every non-directory entry's
    /// normalized destination; <paramref name="dllBytes"/> maps a DLL destination to its bytes and
    /// hash, in archive order.
    /// </summary>
    public static OrdinaryPackageLayout Evaluate(
        IReadOnlyList<string> destinations,
        IReadOnlyList<KeyValuePair<string, ArtifactBytes>> dllBytes)
    {
        if (destinations == null) throw new ArgumentNullException(nameof(destinations));
        if (dllBytes == null) throw new ArgumentNullException(nameof(dllBytes));

        if (dllBytes.Count == 0)
            return OrdinaryPackageLayout.Refuse(InstallReasons.OrdinaryNoDll);
        if (dllBytes.Count > MaxDlls)
            return OrdinaryPackageLayout.Refuse(InstallReasons.OrdinaryTooManyDlls);

        for (int i = 0; i < destinations.Count; i++)
        {
            string destination = destinations[i];
            if (IsForbiddenFolder(destination))
                return OrdinaryPackageLayout.Refuse(InstallReasons.OrdinaryForbiddenFolderAt(destination));
            if (IsDll(destination))
            {
                if (!IsApprovedDllLocation(destination))
                    return OrdinaryPackageLayout.Refuse(InstallReasons.OrdinaryDllElsewhere);
                continue;
            }

            if (!IsIgnorableFile(destination))
                return OrdinaryPackageLayout.Refuse(InstallReasons.OrdinaryExtraFile(destination));
        }

        var dlls = new List<OrdinaryDll>(dllBytes.Count);
        for (int i = 0; i < dllBytes.Count; i++)
        {
            string destination = dllBytes[i].Key;
            var artifact = dllBytes[i].Value;
            if (artifact.Bytes.Length == 0 || artifact.Bytes.Length > MaxDllBytes)
                return OrdinaryPackageLayout.Refuse(InstallReasons.OrdinaryUnreadableImage(Leaf(destination)));

            var outcome = ArtifactPreflight.Scan(artifact.Bytes);
            if (!outcome.Valid)
                return OrdinaryPackageLayout.Refuse(InstallReasons.OrdinaryUnreadableImage(Leaf(destination)));

            if (outcome.EntryPoints.Count > 1)
                return OrdinaryPackageLayout.Refuse(InstallReasons.OrdinaryMultipleEntryPoints);

            var entry = outcome.EntryPoints.Count == 1 ? outcome.EntryPoints[0] : null;
            dlls.Add(new OrdinaryDll(
                destination,
                Leaf(destination),
                artifact.Sha256,
                artifact.Bytes.Length,
                entry != null,
                entry?.Guid ?? "",
                entry?.Name ?? "",
                entry?.Version ?? "",
                outcome.Processes,
                outcome.DependencyGuids));
        }

        OrdinaryDll? plugin = null;
        for (int i = 0; i < dlls.Count; i++)
        {
            if (!dlls[i].DeclaresPlugin) continue;
            if (plugin != null)
                return OrdinaryPackageLayout.Refuse(InstallReasons.OrdinaryMultipleEntryPoints);
            plugin = dlls[i];
        }

        if (plugin == null)
            return OrdinaryPackageLayout.Refuse(InstallReasons.OrdinaryNoEntryPoint);
        if (plugin.PluginGuid.Length == 0)
            return OrdinaryPackageLayout.Refuse(InstallReasons.OrdinaryNoEntryPoint);

        var companions = new List<OrdinaryDll>();
        for (int i = 0; i < dlls.Count; i++)
        {
            if (ReferenceEquals(dlls[i], plugin)) continue;
            // "Every other DLL in that folder declares none" — one folder is the whole package,
            // so a DLL somewhere else is a layout this route does not place.
            if (!string.Equals(dlls[i].Folder, plugin.Folder, StringComparison.OrdinalIgnoreCase))
                return OrdinaryPackageLayout.Refuse(InstallReasons.OrdinaryDllElsewhere);
            companions.Add(dlls[i]);
        }

        return OrdinaryPackageLayout.Approve(plugin, companions);
    }

    /// <summary>
    /// Does a plugin that named <paramref name="processes"/> load in every process
    /// <paramref name="target"/> runs? A plugin that named nothing loads anywhere.
    /// </summary>
    public static bool TargetRunsTheProcess(IReadOnlyList<string>? processes, InstallTarget target, out string? refusal)
    {
        refusal = null;
        if (processes == null || processes.Count == 0) return true;

        var needed = target switch
        {
            InstallTarget.LocalServer => new[] { ServerProcess },
            InstallTarget.Both => new[] { ClientProcess, ServerProcess },
            _ => new[] { ClientProcess },
        };

        for (int i = 0; i < needed.Length; i++)
        {
            if (Names(processes, needed[i])) continue;
            refusal = InstallReasons.OrdinaryProcessMismatch(LoadsInName(processes), needed[i]);
            return false;
        }

        return true;
    }

    /// <summary><c>client</c> | <c>server</c> | <c>both</c> | <c>unknown</c>. An unrecognized executable name reads as <c>unknown</c>.</summary>
    public static string LoadsInName(IReadOnlyList<string>? processes)
    {
        if (processes == null || processes.Count == 0) return "unknown";
        bool client = Names(processes, ClientProcess);
        bool server = Names(processes, ServerProcess);
        if (client && server) return "both";
        if (client) return "client";
        if (server) return "server";
        return "unknown";
    }

    private static bool Names(IReadOnlyList<string> processes, string executable)
    {
        for (int i = 0; i < processes.Count; i++)
        {
            if (string.Equals(processes[i].Trim(), executable, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    internal static bool IsDll(string destination) =>
        destination.EndsWith(ModFolders.ExpectedExtension, StringComparison.OrdinalIgnoreCase);

    /// <summary>The zip root, or one folder under <c>BepInEx/plugins/</c> (or a bare <c>plugins/</c>).</summary>
    internal static bool IsApprovedDllLocation(string destination)
    {
        string n = destination.Replace('\\', '/').TrimStart('/');
        if (n.IndexOf('/') < 0) return true;
        if (n.StartsWith("BepInEx/plugins/", StringComparison.OrdinalIgnoreCase)) return true;
        if (n.StartsWith("plugins/", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    internal static bool IsForbiddenFolder(string destination)
    {
        string n = destination.Replace('\\', '/').TrimStart('/');
        return Under(n, "BepInEx/patchers") || Under(n, "patchers")
            || Under(n, "BepInEx/core") || Under(n, "core")
            || Under(n, "BepInEx/config") || Under(n, "config");
    }

    /// <summary>
    /// INSTPLUG1: documentation, images and source code a package carries for people, never for the
    /// plugin at run time (live: QATeam-QStacks ships <c>SOURCE_REVIEW.md</c> and <c>Source/</c>).
    /// </summary>
    private static readonly string[] PaperworkExtensions =
    {
        ".md", ".txt", ".pdf", ".png", ".jpg", ".jpeg", ".gif", ".webp",
        ".cs", ".csproj", ".sln", ".props", ".targets", ".editorconfig", ".gitignore", ".gitattributes",
        ".yml", ".yaml", ".pdb",
    };

    /// <summary>Beside the plugin DLL only symbols and documentation are left out; anything else there may be data the plugin reads.</summary>
    private static readonly string[] PluginFolderExtras = { ".pdb", ".md" };

    /// <summary>
    /// A non-DLL file this route leaves in the archive, never placing it: the listing's own paperwork
    /// at the zip root (the fixed names, or any documentation/source extension), documentation and
    /// source in a folder outside <c>BepInEx/</c> (<c>Source/</c>, <c>docs/</c>), and symbols or a
    /// README beside the plugin under <c>BepInEx/plugins/</c>. A data file next to the plugin, or a
    /// file anywhere else under <c>BepInEx/</c>, is not ignorable: the plugin may need it.
    /// </summary>
    internal static bool IsIgnorableFile(string destination)
    {
        string n = destination.Replace('\\', '/').TrimStart('/');
        if (n.IndexOf('/') < 0)
        {
            for (int i = 0; i < IgnoredRootFiles.Length; i++)
            {
                if (string.Equals(n, IgnoredRootFiles[i], StringComparison.OrdinalIgnoreCase)) return true;
            }

            return HasExtension(n, PaperworkExtensions);
        }

        if (Under(n, "BepInEx/plugins") || Under(n, "plugins")) return HasExtension(n, PluginFolderExtras);
        if (Under(n, "BepInEx")) return false;
        return HasExtension(n, PaperworkExtensions);
    }

    private static bool HasExtension(string path, string[] extensions)
    {
        for (int i = 0; i < extensions.Length; i++)
        {
            if (path.EndsWith(extensions[i], StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    private static bool Under(string path, string folder) =>
        path.Equals(folder, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase);

    private static string Leaf(string destination)
    {
        int slash = destination.LastIndexOf('/');
        return slash >= 0 ? destination.Substring(slash + 1) : destination;
    }
}

/// <summary>Bytes of one archive entry plus their hash, held only for the duration of an inspection.</summary>
internal readonly struct ArtifactBytes
{
    public ArtifactBytes(byte[] bytes, string sha256)
    {
        Bytes = bytes ?? Array.Empty<byte>();
        Sha256 = sha256 ?? "";
    }

    public byte[] Bytes { get; }
    public string Sha256 { get; }
}
