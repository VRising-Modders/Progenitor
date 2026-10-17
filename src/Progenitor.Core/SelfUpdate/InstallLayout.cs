using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace Progenitor.SelfUpdate;

/// <summary>
/// Where Progenitor's own files live: the one resolver Bootstrap, Core, the preloader and the tests share (this source
/// is compiled into each of them; it references BCL types only).
///
/// <para><b>Single-folder layout.</b> A player copies one folder, <c>BepInEx/plugins/Progenitor/</c>:</para>
/// <list type="bullet">
/// <item><c>Progenitor.Bootstrap.dll</c>, the only chainloader plugin;</item>
/// <item><c>progenitor-core/Progenitor.core</c>, Core. Not a <c>.dll</c> on purpose: BepInEx scans
/// <c>plugins/</c> recursively for <c>*.dll</c>, and its fallback resolver loads a <c>Progenitor.dll</c> found there
/// into the default context, which would take Core's identity. Bootstrap reads the bytes and loads them itself;</item>
/// <item><c>progenitor-core/addresslib/*.json</c> and <c>progenitor-core/docs/guide/progenitor-guide.html</c>;</item>
/// <item><c>Progenitor.Preloader.patcher</c>, the preloader under a name no scan loads. Bootstrap copies it to
/// <c>BepInEx/patchers/Progenitor.Preloader.dll</c> at start when that copy is missing or older
/// (<see cref="DecidePreloader"/>).</item>
/// </list>
///
/// <para><b>Resolution</b> (<see cref="Resolve"/>): Core's folder is <c>progenitor-core</c> beside the running
/// Bootstrap, then the canonical <c>BepInEx/plugins/Progenitor/progenitor-core</c>, then the legacy
/// <c>BepInEx/progenitor-core</c> (which also accepts the legacy file name <c>Progenitor.dll</c>). The first folder
/// holding a Core image wins, so a new folder always beats a leftover legacy one; the legacy folder is then reported as
/// stale and ignored. A hosted child (the bundled server a client starts for a private game) reads its own staged copy
/// in the legacy location first: its <c>plugins</c> folder is a junction at the client's, and the client's Core must
/// never be promoted or quarantined from the child.</para>
/// </summary>
internal static class InstallLayout
{
    public const string PluginsFolderName = "plugins";
    public const string PatchersFolderName = "patchers";

    /// <summary>The one folder a player copies into <c>BepInEx/plugins</c>.</summary>
    public const string HomeFolderName = "Progenitor";

    public const string CoreFolderName = "progenitor-core";

    /// <summary>Core's file name in the single-folder layout.</summary>
    public const string CoreFileName = "Progenitor.core";

    /// <summary>Core's file name in the legacy <c>BepInEx/progenitor-core</c> folder (and the allowlist row name).</summary>
    public const string LegacyCoreFileName = "Progenitor.dll";

    public const string BootstrapFileName = "Progenitor.Bootstrap.dll";

    /// <summary>The preloader's name in <c>BepInEx/patchers</c>, where BepInEx loads it.</summary>
    public const string PreloaderFileName = "Progenitor.Preloader.dll";

    /// <summary>The preloader's name in the shipped folder, where nothing loads it.</summary>
    public const string ShippedPreloaderFileName = "Progenitor.Preloader.patcher";

    public const string TablesFolderName = "addresslib";

    /// <summary>The Progenitor guide, relative to Core's folder.</summary>
    public const string GuideRelativePath = "docs/guide/progenitor-guide.html";

    /// <summary><c>BepInEx/plugins/Progenitor</c>.</summary>
    public static string HomeFolder(string bepInExRoot) =>
        Path.Combine(RequireRoot(bepInExRoot), PluginsFolderName, HomeFolderName);

    /// <summary><c>BepInEx/plugins/Progenitor/progenitor-core</c>.</summary>
    public static string CanonicalCoreFolder(string bepInExRoot) =>
        Path.Combine(HomeFolder(bepInExRoot), CoreFolderName);

    /// <summary><c>BepInEx/progenitor-core</c>, the three-location layout's Core folder.</summary>
    public static string LegacyCoreFolder(string bepInExRoot) =>
        Path.Combine(RequireRoot(bepInExRoot), CoreFolderName);

    /// <summary><c>BepInEx/plugins/Progenitor.Bootstrap.dll</c>, the three-location layout's Bootstrap.</summary>
    public static string LegacyBootstrapPath(string bepInExRoot) =>
        Path.Combine(RequireRoot(bepInExRoot), PluginsFolderName, BootstrapFileName);

    /// <summary><c>BepInEx/patchers/Progenitor.Preloader.dll</c>.</summary>
    public static string PatchersPreloaderPath(string bepInExRoot) =>
        Path.Combine(RequireRoot(bepInExRoot), PatchersFolderName, PreloaderFileName);

    /// <summary>The resolved Core location of one BepInEx tree.</summary>
    internal sealed class CoreHome
    {
        public CoreHome(string folder, string coreFile, bool found, bool legacy, IReadOnlyList<string> tried, string? staleLegacyFolder)
        {
            Folder = folder ?? "";
            CoreFile = coreFile ?? "";
            Found = found;
            Legacy = legacy;
            Tried = tried ?? Array.Empty<string>();
            StaleLegacyFolder = staleLegacyFolder;
        }

        /// <summary>Core's folder: address tables, the guide, staging (<c>.next</c>/<c>.prev</c>) live here.</summary>
        public string Folder { get; }

        /// <summary>Core's live image. When nothing was found, the file a deploy or update should write.</summary>
        public string CoreFile { get; }

        /// <summary>True when <see cref="CoreFile"/> exists.</summary>
        public bool Found { get; }

        /// <summary>True when <see cref="Folder"/> is the legacy <c>BepInEx/progenitor-core</c>.</summary>
        public bool Legacy { get; }

        /// <summary>Every Core file looked for, in order (the missing-Core log lists them).</summary>
        public IReadOnlyList<string> Tried { get; }

        /// <summary>The legacy folder when it still holds a Core image that this resolution ignored; else null.</summary>
        public string? StaleLegacyFolder { get; }
    }

    /// <summary>
    /// Resolve Core's folder for <paramref name="bepInExRoot"/>. <paramref name="bootstrapDir"/> is the folder of the
    /// running <c>Progenitor.Bootstrap.dll</c> (null when unknown, for example another tree). Pure over
    /// <paramref name="fileExists"/> and <paramref name="directoryExists"/>; never throws for a non-empty root.
    /// </summary>
    public static CoreHome Resolve(
        string bepInExRoot,
        string? bootstrapDir,
        bool hostedChild,
        Func<string, bool> fileExists,
        Func<string, bool>? directoryExists = null)
    {
        string root = RequireRoot(bepInExRoot);
        var candidates = new List<(string Folder, string File, bool Legacy)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string folder, string file, bool legacy)
        {
            string path = Path.Combine(folder, file);
            if (seen.Add(Normalize(path))) candidates.Add((folder, path, legacy));
        }

        void AddLegacy()
        {
            string legacy = LegacyCoreFolder(root);
            Add(legacy, CoreFileName, true);
            Add(legacy, LegacyCoreFileName, true);
        }

        // A hosted child reads ONLY its own staged copy (PR #48 review M1): its plugins folder is a junction at the
        // client's, so every other candidate would be the client's file seen through that junction.
        if (hostedChild)
        {
            AddLegacy();
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(bootstrapDir))
                Add(Path.Combine(bootstrapDir!, CoreFolderName), CoreFileName, false);
            Add(CanonicalCoreFolder(root), CoreFileName, false);
            AddLegacy();
        }

        var tried = new List<string>(candidates.Count);
        foreach (var c in candidates) tried.Add(c.File);

        int chosen = -1;
        for (int i = 0; i < candidates.Count && chosen < 0; i++)
            if (SafeTest(fileExists, candidates[i].File)) chosen = i;

        if (chosen >= 0)
        {
            var pick = candidates[chosen];
            string? stale = null;
            if (!pick.Legacy)
                for (int i = 0; i < candidates.Count; i++)
                    if (candidates[i].Legacy && SafeTest(fileExists, candidates[i].File)) { stale = candidates[i].Folder; break; }
            return new CoreHome(pick.Folder, pick.File, true, pick.Legacy, tried, stale);
        }

        // No Core image anywhere: the first candidate folder that exists (a tree being filled, or one holding only the
        // tables or the guide), else the preferred folder. Writes then land in the single-folder layout, except on a
        // hosted child, whose staging writes the legacy location.
        if (directoryExists != null)
            for (int i = 0; i < candidates.Count; i++)
                if (SafeTest(directoryExists, candidates[i].Folder))
                {
                    var c = candidates[i];
                    return new CoreHome(c.Folder, Path.Combine(c.Folder, CoreFileName), false, c.Legacy, tried, null);
                }

        var first = candidates[0];
        return new CoreHome(first.Folder, Path.Combine(first.Folder, CoreFileName), false, first.Legacy, tried, null);
    }

    /// <summary>
    /// Core's resolution in Core itself (<c>BootstrapLink.AdoptInstallLayout</c>). A Bootstrap that publishes its
    /// resolution (<paramref name="publishedFolder"/>, <paramref name="publishedFile"/>) is followed exactly. A Bootstrap
    /// that is loaded but publishes nothing predates the single-folder layout, so it loaded Core from the legacy
    /// <c>BepInEx/progenitor-core</c> only, which is also the hosted-child rule (PR #48 review M2). With no Bootstrap at
    /// all (tests, a Core loaded some other way) the tree is resolved normally.
    /// </summary>
    public static CoreHome ForCore(
        string bepInExRoot,
        bool bootstrapLoaded,
        string? publishedFolder,
        string? publishedFile,
        Func<string, bool> fileExists,
        Func<string, bool>? directoryExists = null)
    {
        string root = RequireRoot(bepInExRoot);
        if (!string.IsNullOrEmpty(publishedFolder) && !string.IsNullOrEmpty(publishedFile))
            return new CoreHome(publishedFolder!, publishedFile!, SafeTest(fileExists, publishedFile!),
                SamePath(publishedFolder, LegacyCoreFolder(root)), new[] { publishedFile! }, null);
        return Resolve(root, null, hostedChild: bootstrapLoaded, fileExists, directoryExists);
    }

    /// <summary>One line for a missing Core: every path tried. Starts with <c>Core missing:</c> (tools match it).</summary>
    public static string FormatMissing(CoreHome home) =>
        "Core missing: none of these files exists: " + string.Join("; ", home?.Tried ?? Array.Empty<string>())
        + ". Bootstrap will not load plugins.";

    /// <summary>One line for a legacy Core folder the resolution ignored.</summary>
    public static string FormatStaleLegacy(CoreHome home) =>
        "Layout: Core loads from " + home.Folder + "; the old folder " + (home.StaleLegacyFolder ?? "")
        + " is ignored and can be deleted.";

    // ---- the process's own resolution -------------------------------------------------------------------------

    private static readonly object Gate = new();
    private static CoreHome? _current;
    private static string _currentRoot = "";
    private static string _currentBootstrap = "";

    /// <summary>The running process's resolution, once Bootstrap (or Core, from Bootstrap) has set it.</summary>
    public static CoreHome? Current
    {
        get { lock (Gate) return _current; }
    }

    /// <summary>
    /// Record the running process's resolution for <paramref name="bepInExRoot"/>, and the file the running
    /// Bootstrap was loaded from (<see cref="BootstrapFile"/>) when known.
    /// </summary>
    public static void SetCurrent(string bepInExRoot, CoreHome? home, string? bootstrapPath = null)
    {
        lock (Gate)
        {
            _current = home;
            _currentRoot = home == null ? "" : Normalize(bepInExRoot ?? "");
            _currentBootstrap = home == null ? "" : (bootstrapPath ?? "");
        }
    }

    /// <summary>
    /// The Bootstrap of <paramref name="bepInExRoot"/>: the running one's own file for the process's own tree (PR #48
    /// review L1: a renamed package folder is followed), else <c>plugins/Progenitor/Progenitor.Bootstrap.dll</c> when it
    /// exists, else the legacy <c>plugins/Progenitor.Bootstrap.dll</c> when that exists, else the single-folder path.
    /// </summary>
    public static string BootstrapFile(string bepInExRoot)
    {
        string root = RequireRoot(bepInExRoot);
        lock (Gate)
        {
            if (_current != null && _currentBootstrap.Length > 0
                && string.Equals(_currentRoot, Normalize(root), StringComparison.OrdinalIgnoreCase))
                return _currentBootstrap;
        }
        string home = Path.Combine(HomeFolder(root), BootstrapFileName);
        if (SafeFileExists(home)) return home;
        string legacy = LegacyBootstrapPath(root);
        return SafeFileExists(legacy) ? legacy : home;
    }

    /// <summary>
    /// Core's folder for <paramref name="bepInExRoot"/>: the process's own resolution for its own tree, else a fresh
    /// resolution of that tree on disk (another tree: a server being installed, a hosted child's folder).
    /// </summary>
    public static string CoreFolder(string bepInExRoot) => HomeFor(bepInExRoot).Folder;

    /// <summary>Core's live image for <paramref name="bepInExRoot"/> (see <see cref="CoreFolder"/>).</summary>
    public static string CoreFile(string bepInExRoot) => HomeFor(bepInExRoot).CoreFile;

    /// <summary>The address-table folder beside Core.</summary>
    public static string Tables(string bepInExRoot) => Path.Combine(CoreFolder(bepInExRoot), TablesFolderName);

    /// <summary>The resolution <see cref="CoreFolder"/> and <see cref="CoreFile"/> answer from.</summary>
    public static CoreHome HomeFor(string bepInExRoot)
    {
        string root = RequireRoot(bepInExRoot);
        lock (Gate)
        {
            if (_current != null && string.Equals(_currentRoot, Normalize(root), StringComparison.OrdinalIgnoreCase))
                return _current;
        }
        return Resolve(root, null, false, SafeFileExists, SafeDirectoryExists);
    }

    /// <summary>True when <paramref name="fileName"/> is a Core image name (<see cref="CoreFileName"/> or the legacy one).</summary>
    public static bool IsCoreImageName(string? fileName) =>
        string.Equals(fileName, CoreFileName, StringComparison.OrdinalIgnoreCase)
        || string.Equals(fileName, LegacyCoreFileName, StringComparison.OrdinalIgnoreCase);

    // ---- branching out to BepInEx/patchers ---------------------------------------------------------------------

    /// <summary>What Bootstrap does with <c>BepInEx/patchers/Progenitor.Preloader.dll</c> at start.</summary>
    internal enum PreloaderAction
    {
        /// <summary>The installed copy is current (same version and bytes) or newer; nothing to do.</summary>
        Keep = 0,

        /// <summary>No installed copy: place the shipped one.</summary>
        Place = 1,

        /// <summary>The installed copy is older, unreadable, or the same version with other bytes: replace it.</summary>
        Replace = 2,

        /// <summary>Nothing shipped (or the shipped copy is unreadable): leave whatever is installed alone.</summary>
        NoSource = 3,
    }

    /// <summary>
    /// The placement decision. <paramref name="installedVersion"/> is null when the installed file is missing and
    /// empty when it exists but its version could not be read. Versions are compared numerically.
    /// </summary>
    public static PreloaderAction DecidePreloader(
        bool shippedExists,
        Version? shippedVersion,
        bool installedExists,
        Version? installedVersion,
        bool sameBytes)
    {
        if (!shippedExists || shippedVersion == null) return PreloaderAction.NoSource;
        if (!installedExists) return PreloaderAction.Place;
        if (installedVersion == null) return PreloaderAction.Replace;
        int cmp = installedVersion.CompareTo(shippedVersion);
        if (cmp < 0) return PreloaderAction.Replace;
        if (cmp > 0) return PreloaderAction.Keep;
        return sameBytes ? PreloaderAction.Keep : PreloaderAction.Replace;
    }

    /// <summary>What Bootstrap does with a legacy <c>BepInEx/plugins/Progenitor.Bootstrap.dll</c> at start.</summary>
    internal enum LegacyBootstrapAction
    {
        /// <summary>None there, or it is the image this process runs: leave it.</summary>
        None = 0,

        /// <summary>A second Bootstrap the chainloader skipped: delete it (and its <c>.next</c>/<c>.prev</c>).</summary>
        Remove = 1,

        /// <summary>A hosted child: the file is the client's, seen through a junction. Never touched from here.</summary>
        LeaveHosted = 2,
    }

    /// <summary>
    /// The legacy Bootstrap cleanup decision. <paramref name="runningPath"/> is the file this process's Bootstrap
    /// was loaded from.
    /// </summary>
    public static LegacyBootstrapAction DecideLegacyBootstrap(
        string bepInExRoot,
        string? runningPath,
        bool legacyExists,
        bool hostedChild)
    {
        if (!legacyExists) return LegacyBootstrapAction.None;
        if (hostedChild) return LegacyBootstrapAction.LeaveHosted;
        if (string.IsNullOrWhiteSpace(runningPath)) return LegacyBootstrapAction.None;
        string legacy = LegacyBootstrapPath(bepInExRoot);
        if (string.Equals(Normalize(legacy), Normalize(runningPath!), StringComparison.OrdinalIgnoreCase))
            return LegacyBootstrapAction.None;
        return LegacyBootstrapAction.Remove;
    }

    // ---- releases from before the single-folder layout -----------------------------------------------------------

    /// <summary>
    /// Refusal for a Progenitor release (or kept copy) whose Core or Bootstrap predates the single-folder layout. Such
    /// a Bootstrap looks for Core only in <c>BepInEx/progenitor-core/Progenitor.dll</c>, and such a Core keeps its own
    /// files there, so putting either into this layout would leave the next start without Core (PR #48 review H2).
    /// </summary>
    public const string CrossLayoutRefusal =
        "it predates the single-folder install (BepInEx/plugins/Progenitor) and cannot be installed over it from here; "
        + "install that release by hand from its release zip";

    /// <summary>
    /// True when <paramref name="image"/> (a Core or Bootstrap assembly) carries this type,
    /// <c>Progenitor.SelfUpdate.InstallLayout</c>: every image built with the single-folder layout compiles it in, and
    /// none built before does. Reads metadata only; nothing is loaded. False for anything unreadable.
    /// </summary>
    public static bool KnowsSingleFolder(byte[]? image) => LayoutOf(image) == ImageLayout.SingleFolder;

    /// <summary>
    /// True only when <paramref name="image"/> is a readable assembly WITHOUT the layout type: proven to predate the
    /// single-folder layout. Anything unreadable is not proven old (the caller's own checks report it).
    /// </summary>
    public static bool PredatesSingleFolder(byte[]? image) => LayoutOf(image) == ImageLayout.Legacy;

    private enum ImageLayout { Unreadable, Legacy, SingleFolder }

    private static ImageLayout LayoutOf(byte[]? image)
    {
        if (image == null || image.Length == 0) return ImageLayout.Unreadable;
        try
        {
            using var pe = new PEReader(new MemoryStream(image, writable: false));
            if (!pe.HasMetadata) return ImageLayout.Unreadable;
            var md = pe.GetMetadataReader();
            foreach (var handle in md.TypeDefinitions)
            {
                var type = md.GetTypeDefinition(handle);
                if (md.StringComparer.Equals(type.Name, nameof(InstallLayout))
                    && md.StringComparer.Equals(type.Namespace, "Progenitor.SelfUpdate"))
                    return ImageLayout.SingleFolder;
            }
            return ImageLayout.Legacy;
        }
        catch
        {
            return ImageLayout.Unreadable;
        }
    }

    // ---- helpers ------------------------------------------------------------------------------------------------

    /// <summary>True when both name the same path (full-path, case-insensitive, trailing separators ignored).</summary>
    public static bool SamePath(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        return string.Equals(Normalize(a!), Normalize(b!), StringComparison.OrdinalIgnoreCase);
    }

    private static string RequireRoot(string bepInExRoot)
    {
        if (string.IsNullOrWhiteSpace(bepInExRoot))
            throw new ArgumentException("bepInExRoot is required.", nameof(bepInExRoot));
        return bepInExRoot;
    }

    private static string Normalize(string path)
    {
        try { return Path.GetFullPath(path).TrimEnd('\\', '/'); }
        catch { return (path ?? "").Replace('/', '\\').TrimEnd('\\'); }
    }

    private static bool SafeTest(Func<string, bool> test, string path)
    {
        try { return test(path); }
        catch { return false; }
    }

    internal static bool SafeFileExists(string path)
    {
        try { return !string.IsNullOrEmpty(path) && File.Exists(path); }
        catch { return false; }
    }

    internal static bool SafeDirectoryExists(string path)
    {
        try { return !string.IsNullOrEmpty(path) && Directory.Exists(path); }
        catch { return false; }
    }
}
