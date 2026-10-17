using System;
using System.IO;
using Progenitor.SelfUpdate;

namespace Progenitor.Bootstrap;

/// <summary>
/// On-disk layout for the Core assembly. Step D: Bootstrap <c>LoadFromStream</c>s this file
/// into a collectible ALC. The chainloader never sees it: in the single-folder layout it is
/// <c>BepInEx/plugins/Progenitor/progenitor-core/Progenitor.core</c>, which no BepInEx scan or
/// resolver treats as an assembly; a <c>Progenitor.dll</c> under <c>BepInEx/plugins</c> would be
/// loaded first and steal the <c>Progenitor</c> identity. Where Core lives is decided once, at the
/// start of <c>Plugin.Load</c>, by <see cref="InstallLayout.Resolve"/> (<see cref="ResolveAtStart"/>);
/// every path below reads that one answer.
/// </summary>
public static class CoreLayout
{
    public const string FolderName = InstallLayout.CoreFolderName;

    /// <summary>
    /// Core's <em>logical</em> image name: the allowlist row name, the log label and the name a
    /// staged copy is checked against. On disk the single-folder image is
    /// <see cref="ImageFileName"/>; a row for either name lists Core
    /// (<see cref="DecideCoreRow"/>), so an existing <c>progenitor-allow.txt</c> keeps working.
    /// </summary>
    public const string FileName = InstallLayout.LegacyCoreFileName;

    /// <summary>Core's file name on disk in the single-folder layout.</summary>
    public const string ImageFileName = InstallLayout.CoreFileName;
    public const string AssemblySimpleName = "Progenitor";
    public const string RuntimeTypeName = "Progenitor.Runtime";
    public const string InstallMethod = "Install";
    public const string UninstallMethod = "Uninstall";
    public const string SnapshotMethod = "SnapshotContent";
    public const string NotifyLiveWorldMethod = "NotifyLiveWorldAfterCoreSwap";
    public const string ApplyPlaceholdersMethod = "ApplyKeyPlaceholdersAfterPlugins";
    public const string DiagnosePinsMethod = "DiagnosePins";
    public const string ReaskHandshakeMethod = "ReaskConnectedHandshake";

    /// <summary>
    /// Bootstrap ↔ Core install handshake. Core publishes the same int on
    /// <c>Progenitor.BootstrapLink.ProtocolVersion</c>. A mismatch refuses the load
    /// (both numbers in the log). First load does not restore last-good of the
    /// old protocol; the process stays Core-less until a matching image is
    /// deployed. A running generation still restores last-good of the <em>same</em>
    /// protocol. Bump both constants in the same change whenever
    /// <c>Install</c>/<c>Uninstall</c>/<c>ICoreHost</c> shape, Harmony/receive slot
    /// names, or <c>ContentMemory</c> layout change. Never accept a Core that does
    /// not report the field (read as 0).
    ///
    /// <b>3</b> (R31 finding 13, job FRLD3): the plugin unload hand-off. Bootstrap's
    /// <see cref="PluginUnloadSlot"/> - its <c>Reason</c> and <c>ProcessExiting</c> members and
    /// the codes they carry - is part of this handshake, and adding it was a contract change
    /// that FBUFF4 shipped without a bump. New Core against protocol-2 Bootstrap is the normal
    /// two-part deploy (Core swaps this launch, Bootstrap's own <c>.next</c> is one launch
    /// later), and that pairing carried no reason at all: every unload inferred Unknown and a
    /// deleted plugin never swept. It is refused now, both ways, so the operator restarts.
    ///
    /// <b>4</b> (job FHOOK3): the four system-update slot delegates
    /// (<c>HarmonySlots.ManagedUpdatePrefix</c>/<c>Postfix</c> and the unmanaged twins) widened
    /// from <c>bool hasAfter</c> to <c>long callToken</c> so Core's <c>PostfixManaged</c> can
    /// release the call frame <em>this</em> call pushed. Those declarations exist twice, once per
    /// assembly, and <c>HarmonySlots.Bind</c> joins them with <c>Delegate.CreateDelegate</c>:
    /// without a bump, a new Core against an old Bootstrap (the normal two-part deploy) would bind
    /// a skewed delegate, <c>Bind</c> would throw, and both system-hook sites would go Inert with
    /// system hooks silently off for that generation. Refusing the pairing is the honest answer,
    /// and it is why the widening was affordable at all. Deploy while both processes are down, or
    /// the first launch after the deploy runs Core-less and the second recovers.
    ///
    /// <b>5</b> (2026-10 id rename): Bootstrap's BepInEx GUID became <c>progenitor</c> and its Harmony id
    /// <c>progenitor.bootstrap</c>, which Core reads by name (<c>BootstrapLink</c>); consumer plugins now declare
    /// <c>[BepInDependency("progenitor")]</c>, which an older Bootstrap does not satisfy, and Bootstrap renames the
    /// old cfg files before anything binds them. A new Core under an older Bootstrap would bind fresh, empty cfg
    /// files beside the old ones, so the pairing is refused and the process stays Core-less until Bootstrap is
    /// deployed and the process restarted.
    /// </summary>
    public const int ProtocolVersion = 5;

    /// <summary>Type Core uses to publish <see cref="ProtocolVersion"/> (reflection; no compile reference).</summary>
    public const string ProtocolTypeName = "Progenitor.BootstrapLink";

    /// <summary>Field name on <see cref="ProtocolTypeName"/>.</summary>
    public const string ProtocolFieldName = "ProtocolVersion";

    /// <summary>
    /// Logged when no Core image is found: every path tried, in order
    /// (<see cref="InstallLayout.FormatMissing"/>). Bootstrap then does not call Install or scan
    /// plugins. Tools and tests match this prefix.
    /// </summary>
    public const string MissingLogPrefix = "Core missing:";

    /// <summary>Core's folder for this process (see <see cref="InstallLayout.CoreFolder"/>).</summary>
    public static string Folder(string bepInExRoot) => InstallLayout.CoreFolder(bepInExRoot);

    /// <summary>Core's live image for this process. Staging is this path plus <c>.next</c>.</summary>
    public static string DllPath(string bepInExRoot) => InstallLayout.CoreFile(bepInExRoot);

    /// <summary>
    /// Core reads these by reflection (<c>BootstrapLink</c>) so both assemblies use the one
    /// resolution Bootstrap made at start. Null before <see cref="ResolveAtStart"/>.
    /// </summary>
    public static string? ResolvedFolder => InstallLayout.Current?.Folder;

    /// <summary>See <see cref="ResolvedFolder"/>.</summary>
    public static string? ResolvedCoreFile => InstallLayout.Current?.CoreFile;

    /// <summary>
    /// Allowlist verdict for a Core image: listed when a row names either
    /// <see cref="ImageFileName"/> or <see cref="FileName"/> with this hash. Otherwise the verdict
    /// for <see cref="FileName"/> (no list, unlisted or malformed).
    /// </summary>
    public static AllowDecision DecideCoreRow(Func<string, string, AllowDecision> decide, string? fileName, string sha256Hex)
    {
        _ = fileName;
        var legacy = decide(FileName, sha256Hex);
        if (legacy == AllowDecision.Listed) return legacy;
        var image = decide(ImageFileName, sha256Hex);
        return image == AllowDecision.Listed ? image : legacy;
    }

    /// <summary>
    /// Resolve Core's location for this process and record it (<see cref="InstallLayout.SetCurrent"/>).
    /// Called first thing in <c>Plugin.Load</c>, before the uninstall check, the start-loop guard
    /// and the first Core read, so every one of them reads the same folder.
    /// </summary>
    internal static InstallLayout.CoreHome ResolveAtStart(string bepInExRoot, string? bootstrapPath, bool hostedChild, Action<string>? warn)
    {
        string? dir = null;
        try { dir = string.IsNullOrEmpty(bootstrapPath) ? null : Path.GetDirectoryName(bootstrapPath); }
        catch { dir = null; }
        var home = InstallLayout.Resolve(bepInExRoot, dir, hostedChild, InstallLayout.SafeFileExists, InstallLayout.SafeDirectoryExists);
        InstallLayout.SetCurrent(bepInExRoot, home, bootstrapPath);
        if (home.StaleLegacyFolder != null)
        {
            try { warn?.Invoke(InstallLayout.FormatStaleLegacy(home)); }
            catch { /* a log is not a gate */ }
        }
        return home;
    }
}
