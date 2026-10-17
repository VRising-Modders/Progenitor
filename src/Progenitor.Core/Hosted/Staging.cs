using System;
using System.Collections.Generic;
using System.IO;
#if !PROGENITOR_TESTS
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using BepInEx;
#endif

namespace Progenitor.Hosted;

/// <summary>
/// Places the Doorstop shim and a rewritten config next to the bundled <c>VRisingServer.exe</c>,
/// and links the client's plugin/core folders into the server's BepInEx tree. Config, cache,
/// interop, and the log stay local so the server can generate its own interop cache and keep its
/// own settings. Doorstop <c>target_assembly</c> is the <em>server</em> copy
/// (<c>{serverDir}\BepInEx\core\BepInEx.Unity.IL2CPP.dll</c>, junctioned at the client's core):
/// BepInEx 6 IL2CPP sets <c>Paths.BepInExRootPath</c> to two parents of
/// <c>DOORSTOP_INVOKE_DLL_PATH</c> (the directory of <c>target_assembly</c>'s parent), so a
/// client-core target made the child share the client's config/cache/log (evidence §24).
/// Step C layout: the child gets <c>plugins/Progenitor.Bootstrap.dll</c> plus
/// <c>progenitor-core/Progenitor.dll</c> (never a leftover <c>plugins/Progenitor.dll</c>, which
/// the chainloader would load first and steal the Core identity). <c>progenitor-core</c> is
/// always a real copy, never a junction: the child's Bootstrap promote of <c>.next</c> would
/// otherwise <c>File.Move</c> the running client's Core (R11 finding 2). The stamp includes
/// live / <c>.next</c> / <c>.prev</c> write times so a deploy that only dropped
/// <c>Progenitor.dll.next</c> restages (R11 finding 7). Idempotent: a stamp file compared
/// against those write times skips work when nothing changed, and only when each shared
/// folder is a reparse point at the client path or a copy carrying
/// <see cref="HostedPathRules.StagedCopyMarkerName"/> with that same stamp, and the
/// doorstop target already points at the server core. A leftover
/// <c>plugins/Progenitor.dll</c> that cannot be deleted fails closed (no stamp, spawn
/// refused) so the child never boots with the identity steal (R11 finding 8). A leftover
/// bundled child that still has the dest <c>progenitor-core/Progenitor.dll</c> mapped
/// refuses the copy and the spawn (R12 finding 11) instead of <c>File.Copy</c> over a
/// running Core. Never writes outside <c>serverDir</c>. The server directory itself must
/// sit strictly under the Steam game root and must not be the live dedicated server.
/// </summary>
public static class Staging
{
    /// <summary>
    /// Step C on-disk layout the bundled child must match. Bootstrap is the only chainloader
    /// plugin; Core is loaded from <c>BepInEx/progenitor-core/</c>. Strings match
    /// <c>Progenitor.Bootstrap.CoreLayout</c> without a compile reference to Bootstrap — Core
    /// has to run when Bootstrap is absent. A leftover <c>plugins/Progenitor.dll</c> would be
    /// loaded first and steal the <c>Progenitor</c> identity, so staging deletes it under a
    /// real child plugins folder and refuses when that file is only visible through a junction
    /// at the client (deleting through a junction would unlink the client's file).
    /// </summary>
    public static class Layout
    {
        /// <summary>BepInEx folder that holds Core. Same as Bootstrap's <c>CoreLayout.FolderName</c>.</summary>
        public const string CoreFolderName = "progenitor-core";

        /// <summary>Core assembly file name. Same as Bootstrap's <c>CoreLayout.FileName</c>.</summary>
        public const string CoreDllName = "Progenitor.dll";

        /// <summary>Chainloader plugin that <c>LoadFrom</c>s Core. Lives in <c>BepInEx/plugins/</c>.</summary>
        public const string BootstrapDllName = "Progenitor.Bootstrap.dll";

        /// <summary>BepInEx chainloader plugins folder name.</summary>
        public const string PluginsFolderName = "plugins";

        /// <summary>Staged-update suffix Bootstrap promotes before <c>LoadFrom</c> (Core) / at exit (Bootstrap).</summary>
        public const string NextSuffix = ".next";

        /// <summary>Temporary name used while promoting <see cref="NextSuffix"/> onto the live DLL.</summary>
        public const string PrevSuffix = ".prev";

        /// <summary>
        /// Names under a real child <c>plugins</c> folder that are leftover Core (pre-Step-C) and
        /// must be deleted so the chainloader cannot map them.
        /// </summary>
        public static readonly IReadOnlyList<string> StalePluginsCoreFileNames = new[]
        {
            CoreDllName,
            CoreDllName + NextSuffix,
            CoreDllName + PrevSuffix,
        };

        /// <summary>
        /// Client BepInEx folders Shared mode links (or copies), including
        /// <see cref="CoreFolderName"/>. <see cref="HostedPathRules.SharedFolderNames"/> is the
        /// BepInEx-loader set; Core's folder is extra because it is not a chainloader path.
        /// </summary>
        public static readonly IReadOnlyList<string> AllSharedFolders = BuildAllSharedFolders();

        /// <summary>What to do with a leftover <c>plugins/Progenitor.dll</c> on the child.</summary>
        public enum StaleCoreDllDisposition
        {
            /// <summary>No leftover file; staging can continue.</summary>
            None = 0,

            /// <summary>
            /// The file sits in a real directory under <c>serverDir</c> (ProgenitorOnly, or a
            /// copy-fallback). Delete it; the chainloader must not see Core in plugins.
            /// </summary>
            Delete = 1,

            /// <summary>
            /// The file is visible through a junction at the client's plugins. Refusing is the
            /// only safe option: <c>File.Delete</c> through a reparse point would unlink the
            /// client's copy, which is outside <c>serverDir</c>.
            /// </summary>
            Refuse = 2,
        }

        /// <summary>True when <paramref name="name"/> is <see cref="CoreFolderName"/> (case-insensitive).</summary>
        public static bool IsCoreFolderName(string? name)
            => !string.IsNullOrEmpty(name)
               && name.Equals(CoreFolderName, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// True when <paramref name="fileName"/> is leftover Core in plugins
        /// (<c>Progenitor.dll</c> / <c>.next</c> / <c>.prev</c>), not Bootstrap.
        /// </summary>
        public static bool IsStalePluginsCoreFile(string? fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return false;
            foreach (var n in StalePluginsCoreFileNames)
            {
                if (n.Equals(fileName, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Folders the shared-folder loop must not junction. <see cref="CoreFolderName"/> is
        /// skipped in every mode (always a real copy so the child's <c>.next</c> promote cannot
        /// <c>File.Move</c> the client's Core). Plugins is skipped only under
        /// <see cref="HostedServerPluginsMode.ProgenitorOnly"/> so that mode can write a real
        /// folder containing only Bootstrap.
        /// </summary>
        public static bool SkipSharedFolder(HostedServerPluginsMode mode, string? name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            if (IsCoreFolderName(name)) return true;
            if (mode != HostedServerPluginsMode.ProgenitorOnly) return false;
            return name.Equals(PluginsFolderName, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Shared folders to junction/copy for <paramref name="mode"/>. Always omits
        /// progenitor-core (copied). ProgenitorOnly also omits plugins so that folder can be
        /// filled with only Bootstrap.
        /// </summary>
        public static IReadOnlyList<string> FoldersToLink(HostedServerPluginsMode mode)
        {
            var list = new List<string>(AllSharedFolders.Count);
            foreach (var name in AllSharedFolders)
            {
                if (SkipSharedFolder(mode, name)) continue;
                list.Add(name);
            }

            return list;
        }

        /// <summary>Child (or client) path of <c>progenitor-core/Progenitor.dll</c>.</summary>
        public static string CoreDllPath(string bepInExRoot)
        {
            if (string.IsNullOrEmpty(bepInExRoot))
                throw new ArgumentException("bepInExRoot is required.", nameof(bepInExRoot));
            return Path.Combine(bepInExRoot, CoreFolderName, CoreDllName);
        }

        /// <summary>Child (or client) path of <c>plugins/Progenitor.Bootstrap.dll</c>.</summary>
        public static string BootstrapDllPath(string bepInExRoot)
        {
            if (string.IsNullOrEmpty(bepInExRoot))
                throw new ArgumentException("bepInExRoot is required.", nameof(bepInExRoot));
            return Path.Combine(bepInExRoot, PluginsFolderName, BootstrapDllName);
        }

        /// <summary>
        /// The client's Core image, wherever this tree keeps it (<see cref="Progenitor.SelfUpdate.InstallLayout.CoreFile"/>:
        /// <c>plugins/Progenitor/progenitor-core/Progenitor.core</c> in the single-folder layout). The child always gets
        /// its own real copy at <see cref="CoreDllPath"/>, which a hosted child's Bootstrap reads first.
        /// </summary>
        public static string ClientCoreDllPath(string clientBepInExRoot) =>
            Progenitor.SelfUpdate.InstallLayout.CoreFile(clientBepInExRoot);

        /// <summary>
        /// The client's Bootstrap: <c>plugins/Progenitor/Progenitor.Bootstrap.dll</c> in the single-folder layout, else
        /// the legacy <see cref="BootstrapDllPath"/>.
        /// </summary>
        public static string ClientBootstrapDllPath(string clientBepInExRoot)
        {
            string home = Path.Combine(
                Progenitor.SelfUpdate.InstallLayout.HomeFolder(clientBepInExRoot), BootstrapDllName);
            return File.Exists(home) ? home : BootstrapDllPath(clientBepInExRoot);
        }

        /// <summary>Child (or client) path of a leftover <c>plugins/Progenitor.dll</c>.</summary>
        public static string StalePluginsCoreDllPath(string bepInExRoot)
        {
            if (string.IsNullOrEmpty(bepInExRoot))
                throw new ArgumentException("bepInExRoot is required.", nameof(bepInExRoot));
            return Path.Combine(bepInExRoot, PluginsFolderName, CoreDllName);
        }

        /// <summary>
        /// Junction: refuse (the file is the client's). Real folder under the child: delete.
        /// Missing: nothing to do. Live-server refusal is applied by the caller before delete.
        /// </summary>
        public static StaleCoreDllDisposition DecideStalePluginsCoreDll(
            bool staleDllExists,
            bool pluginsFolderIsReparsePoint)
        {
            if (!staleDllExists) return StaleCoreDllDisposition.None;
            return pluginsFolderIsReparsePoint
                ? StaleCoreDllDisposition.Refuse
                : StaleCoreDllDisposition.Delete;
        }

        /// <summary>
        /// Copied plugins or progenitor-core is current only as a real (non-junction) folder
        /// we marked, carrying the expected DLL, with no leftover Core in plugins, and with
        /// the <c>.next</c> sidecar matching the client (absent on both, or present and same).
        /// </summary>
        /// <param name="folderName"><see cref="PluginsFolderName"/> or <see cref="CoreFolderName"/>.</param>
        /// <param name="expectedDllPresent">Bootstrap.dll for plugins; Progenitor.dll for progenitor-core.</param>
        /// <param name="stalePluginsCoreDllPresent">True when <c>plugins/Progenitor.dll</c> exists. Ignored for progenitor-core.</param>
        /// <param name="clientHasNext">True when the client has a sibling <c>.next</c> for the expected DLL.</param>
        /// <param name="destHasNext">True when the child folder has that sibling <c>.next</c>.</param>
        /// <param name="nextFilesMatch">True when both <c>.next</c> files exist and have the same length and UTC write time.</param>
        public static bool ProgenitorOnlyFolderLooksStaged(
            string? folderName,
            bool destExists,
            bool destIsReparsePoint,
            bool markerPresent,
            string? markerContents,
            string? expectedStamp,
            bool expectedDllPresent,
            bool stalePluginsCoreDllPresent,
            bool clientHasNext = false,
            bool destHasNext = false,
            bool nextFilesMatch = true)
        {
            if (!destExists || destIsReparsePoint) return false;
            if (!markerPresent) return false;
            if (string.IsNullOrEmpty(expectedStamp)) return false;
            if (!string.Equals(markerContents, expectedStamp, StringComparison.Ordinal)) return false;
            if (!expectedDllPresent) return false;
            if (folderName != null
                && folderName.Equals(PluginsFolderName, StringComparison.OrdinalIgnoreCase)
                && stalePluginsCoreDllPresent)
                return false;
            if (!NextSidecarLooksStaged(clientHasNext, destHasNext, nextFilesMatch))
                return false;
            return true;
        }

        /// <summary>
        /// Child <c>.next</c> must match the client: both missing, or both present and the
        /// same file. A client <c>.next</c> with no child copy would boot yesterday's Core;
        /// a child leftover with no client <c>.next</c> would promote stale bytes.
        /// </summary>
        public static bool NextSidecarLooksStaged(bool clientHasNext, bool destHasNext, bool nextFilesMatch)
        {
            if (!clientHasNext) return !destHasNext;
            return destHasNext && nextFilesMatch;
        }

        /// <summary>
        /// Stamp token version. Bumped to v4 when <c>.next</c> / <c>.prev</c> write times
        /// joined the token so a deploy that only dropped <c>Progenitor.dll.next</c> restages.
        /// </summary>
        public const string StampVersion = "v4";

        /// <summary>Layout line written into the stamp so a pre-Step-C child restages.</summary>
        public const string StampLayoutLine = "layout=bootstrap+progenitor-core";

        /// <summary>Stamp line for a live DLL's UTC last-write ticks.</summary>
        public static string StampDllUtcLine(string key, long utcTicks) => key + "UtcTicks=" + utcTicks;

        /// <summary>Stamp line for a sibling <c>.next</c>; <c>0</c> when the file is missing.</summary>
        public static string StampNextUtcLine(string key, bool exists, long utcTicks)
            => key + "NextUtcTicks=" + (exists ? utcTicks : 0);

        /// <summary>Stamp line for a sibling <c>.prev</c>; <c>0</c> when the file is missing.</summary>
        public static string StampPrevUtcLine(string key, bool exists, long utcTicks)
            => key + "PrevUtcTicks=" + (exists ? utcTicks : 0);

        /// <summary>
        /// True when leftover Core visible through a junction (or otherwise undeletable)
        /// must skip <c>StunProcess.Start</c>. Deleting through the reparse would unlink
        /// the client's file; spawning would let the chainloader steal the Core identity.
        /// </summary>
        public static bool MustRefuseSpawn(StaleCoreDllDisposition disposition)
            => disposition == StaleCoreDllDisposition.Refuse;

        /// <summary>
        /// True when a leftover bundled child is still running and staging would write
        /// <c>progenitor-core</c> (overwrite a mapped <c>Progenitor.dll</c> or recursive-delete
        /// the folder). Skip the copy when dest is already current — the leftover can keep
        /// running. Reuses the launcher's path-matched leftover scan
        /// (<see cref="HostedServerLauncher.ChildLogProbe.IsBundledChildProcess"/>).
        /// </summary>
        public static bool MustRefuseCoreCopyWhileLeftoverChildAlive(
            bool leftoverBundledChildAlive,
            bool destNeedsWrite)
            => leftoverBundledChildAlive && destNeedsWrite;

        /// <summary>
        /// Fail closed (R13 finding 9): a throw from <c>GetProcessesByName</c>, or
        /// <c>VRisingServer</c> processes that exist but could not be identified, count as
        /// leftover-alive so a dest write cannot <c>File.Copy</c> over a mapped Core.
        /// </summary>
        public static bool LeftoverChildScanTreatAsAlive(
            bool scanThrew,
            int processesSeen,
            int processesIdentified,
            bool foundMatchingChild)
        {
            if (foundMatchingChild) return true;
            if (scanThrew) return true;
            if (processesSeen > 0 && processesIdentified == 0) return true;
            return false;
        }

        /// <summary>
        /// True when <paramref name="processPath"/> is a leftover bundled child under
        /// <paramref name="serverDir"/> that would have <paramref name="destCoreDll"/> mapped
        /// (dest sits under that server dir). Live dedicated paths are never a leftover child.
        /// </summary>
        public static bool LeftoverChildOwnsDest(
            string? processPath,
            string? serverDir,
            string? destCoreDll)
        {
            if (!HostedServerLauncher.ChildLogProbe.IsBundledChildProcess(processPath, serverDir))
                return false;
            if (string.IsNullOrWhiteSpace(destCoreDll) || string.IsNullOrWhiteSpace(serverDir))
                return false;

            try
            {
                var destFull = HostedPathRules.Normalize(destCoreDll);
                var serverFull = HostedPathRules.Normalize(serverDir);
                if (HostedPathRules.IsForbiddenLiveServerPath(destFull)
                    || HostedPathRules.IsForbiddenLiveServerPath(serverFull)
                    || HostedPathRules.IsForbiddenLiveServerPath(processPath))
                    return false;
                return HostedPathRules.IsUnder(destFull, serverFull);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Same rules as <see cref="HostedPathRules.MayRecursiveDeleteRealDirectory"/>, plus
        /// <see cref="CoreFolderName"/> (not in the BepInEx-loader shared-folder list). Live
        /// dedicated server paths stay refused.
        /// </summary>
        public static bool MayRecursiveDeleteStagedFolder(
            string? dest,
            string? serverDir,
            bool markerPresent,
            out string reason)
        {
            if (HostedPathRules.MayRecursiveDeleteRealDirectory(dest, serverDir, markerPresent, out reason))
                return true;

            if (string.IsNullOrWhiteSpace(dest) || string.IsNullOrWhiteSpace(serverDir))
                return false;

            if (HostedPathRules.IsForbiddenLiveServerPath(dest)
                || HostedPathRules.IsForbiddenLiveServerPath(serverDir))
                return false;

            try
            {
                var destFull = HostedPathRules.Normalize(dest);
                var serverFull = HostedPathRules.Normalize(serverDir);
                if (HostedPathRules.PathsEqual(destFull, serverFull)
                    || !HostedPathRules.IsUnder(destFull, serverFull))
                    return false;

                var parent = Path.GetDirectoryName(destFull);
                var expectedParent = Path.Combine(serverFull, "BepInEx");
                var name = Path.GetFileName(destFull);
                if (parent == null
                    || !HostedPathRules.PathsEqual(parent, expectedParent)
                    || !IsCoreFolderName(name))
                    return false;

                if (!markerPresent)
                {
                    reason =
                        $"refusing to delete real directory {destFull} without {HostedPathRules.StagedCopyMarkerName} (not a previous Progenitor copy)";
                    return false;
                }

                reason = "";
                return true;
            }
            catch (Exception e)
            {
                reason = "delete-safety check threw: " + e.Message;
                return false;
            }
        }

        /// <summary>True when <paramref name="name"/> is a folder staging may replace (shared BepInEx folders or progenitor-core).</summary>
        public static bool IsLinkableFolderName(string? name)
            => HostedPathRules.IsSharedFolderName(name) || IsCoreFolderName(name);

        private static IReadOnlyList<string> BuildAllSharedFolders()
        {
            var list = new List<string>(HostedPathRules.SharedFolderNames.Count + 1);
            bool hasCore = false;
            foreach (var n in HostedPathRules.SharedFolderNames)
            {
                list.Add(n);
                if (IsCoreFolderName(n)) hasCore = true;
            }

            if (!hasCore) list.Add(CoreFolderName);
            return list.ToArray();
        }
    }

#if !PROGENITOR_TESTS
    private const string StampFileName = ".progenitor-hosted-stamp";
    private const string DoorstopShimName = "winhttp.dll";
    private const string DoorstopConfigName = "doorstop_config.ini";
    private const string TargetAssemblyFile = "BepInEx.Unity.IL2CPP.dll";
    private const string CoreDllFile = "BepInEx.Core.dll";

    /// <summary>Folders considered by the already-staged loop (loader folders plus progenitor-core, which is always a real copy).</summary>
    private static readonly IReadOnlyList<string> SharedFolders = Layout.AllSharedFolders;

    /// <summary>Folders that stay local to the server and must not be linked or wiped.</summary>
    private static readonly string[] LocalFolders = { "config", "cache", "interop" };

    /// <summary>Last <see cref="EnsureStaged"/> report, for <see cref="HostedServerLauncher.Describe"/>.</summary>
    public static string LastSummary { get; private set; } = "";

    /// <summary>True when the last <see cref="EnsureStaged"/> finished without a hard failure.</summary>
    public static bool LastOk { get; private set; }

    /// <summary>
    /// True when the last stage found a leftover <c>plugins/Progenitor.dll</c> it could not
    /// delete, or a leftover bundled child with dest Core mapped. The Start prefix must skip
    /// <c>StunProcess.Start</c> (R11 finding 8 / R13 finding 9).
    /// </summary>
    public static bool LastRefuseSpawn { get; private set; }

    /// <summary>
    /// True when <see cref="LastRefuseSpawn"/> is because a leftover hosted child still has
    /// <c>progenitor-core/Progenitor.dll</c> mapped, not because of leftover
    /// <c>plugins/Progenitor.dll</c>. The Start prefix log must name this reason
    /// (R13 finding 9).
    /// </summary>
    public static bool LastLeftoverChildMappedCore { get; private set; }

    /// <summary>
    /// Stage the loader into <paramref name="serverDir"/> (the folder that contains
    /// <c>VRisingServer.exe</c>). Cheap no-op when the stamp matches the client's core DLL.
    /// Honors <see cref="HostedServerLauncher.DryRun"/>.
    /// </summary>
    public static void EnsureStaged(string serverDir)
    {
        var actions = new List<string>();
        try
        {
            if (string.IsNullOrWhiteSpace(serverDir))
            {
                Finish(false, "serverDir is empty", actions);
                return;
            }

            serverDir = Path.GetFullPath(serverDir);
            if (!Directory.Exists(serverDir))
            {
                Finish(false, $"server directory does not exist: {serverDir}", actions);
                return;
            }

            if (!IsSafeServerDir(serverDir, out var unsafeReason))
            {
                Finish(false, unsafeReason, actions);
                return;
            }

            string clientRoot = Path.GetFullPath(Paths.GameRootPath);
            string clientBepInEx = Path.GetFullPath(Paths.BepInExRootPath);
            string coreDll = Path.Combine(clientBepInEx, "core", CoreDllFile);
            string shimSrc = Path.Combine(clientRoot, DoorstopShimName);
            string iniSrc = Path.Combine(clientRoot, DoorstopConfigName);
            // Client file must exist so the junctioned server core can load it. The doorstop
            // target itself is the server-side path: BepInEx root = parent of that core folder.
            string clientTargetAssembly = Path.Combine(clientBepInEx, "core", TargetAssemblyFile);
            string bepInExDir = Path.Combine(serverDir, "BepInEx");
            string targetAssembly = Path.Combine(bepInExDir, "core", TargetAssemblyFile);
            string coreclr = Path.Combine(clientRoot, "dotnet", "coreclr.dll");
            string corlibDir = Path.Combine(clientRoot, "dotnet");
            var pluginsMode = HostedServerLauncher.ServerPluginsMode;

            bool dry = HostedServerLauncher.DryRun;
            actions.Add(dry ? $"dry-run staging into {serverDir}" : $"staging into {serverDir}");
            actions.Add($"client BepInEx: {clientBepInEx}");
            actions.Add($"child BepInEx root: {bepInExDir} (doorstop target_assembly parent-of-core)");
            actions.Add($"ServerPluginsMode: {pluginsMode}");

            if (!File.Exists(coreDll))
            {
                Finish(false, $"client core DLL missing: {coreDll}", actions);
                return;
            }
            if (!File.Exists(shimSrc))
            {
                Finish(false, $"Doorstop shim missing: {shimSrc}", actions);
                return;
            }
            if (!File.Exists(clientTargetAssembly))
            {
                Finish(false, $"Doorstop target assembly missing on the client: {clientTargetAssembly}", actions);
                return;
            }
            if (!File.Exists(coreclr) || !Directory.Exists(corlibDir))
            {
                Finish(false, $"client CoreCLR missing under {corlibDir}", actions);
                return;
            }

            string clientBootstrapDll = Layout.ClientBootstrapDllPath(clientBepInEx);
            string clientCoreDll = Layout.ClientCoreDllPath(clientBepInEx);
            if (!File.Exists(clientBootstrapDll))
            {
                Finish(false, $"client {Layout.BootstrapDllName} missing: {clientBootstrapDll}", actions);
                return;
            }
            if (!File.Exists(clientCoreDll))
            {
                Finish(false, $"client {Layout.CoreFolderName}/{Layout.CoreDllName} missing: {clientCoreDll}", actions);
                return;
            }

            string stampToken = BuildStampToken(
                coreDll, shimSrc, targetAssembly, pluginsMode, clientBootstrapDll, clientCoreDll);
            string stampPath = Path.Combine(bepInExDir, StampFileName);
            string doorstopDest = Path.Combine(serverDir, DoorstopConfigName);

            if (IsAlreadyStaged(serverDir, stampPath, stampToken, targetAssembly, pluginsMode))
            {
                actions.Add($"already staged (stamp matches BepInEx.Core.dll write time {File.GetLastWriteTimeUtc(coreDll):o}; doorstop target is server core; pluginsMode={pluginsMode}; Core/.next sidecars match)");
                if (!dry)
                {
                    Finish(true, "already staged", actions);
                    return;
                }
                actions.Add("dry-run continues so the would-be work is still listed");
            }

            CopyShim(shimSrc, Path.Combine(serverDir, DoorstopShimName), serverDir, dry, actions);

            EnsureDirectory(bepInExDir, serverDir, dry, actions);

            bool linksOk = true;
            foreach (var name in Layout.FoldersToLink(pluginsMode))
            {
                var source = Path.Combine(clientBepInEx, name);
                var dest = Path.Combine(bepInExDir, name);
                if (!LinkOrCopy(source, dest, serverDir, stampToken, dry, actions))
                    linksOk = false;
            }

            // progenitor-core is a real copy in both modes (R11 finding 2).
            bool coreOk = StageProgenitorOnlyCore(
                Path.Combine(bepInExDir, Layout.CoreFolderName),
                clientCoreDll,
                serverDir,
                stampToken,
                dry,
                actions,
                out var leftoverChildMappedCore);

            bool pluginsOk = true;
            if (pluginsMode == HostedServerPluginsMode.ProgenitorOnly)
            {
                pluginsOk = StageProgenitorOnlyPlugins(
                    Path.Combine(bepInExDir, Layout.PluginsFolderName),
                    clientBootstrapDll,
                    serverDir,
                    stampToken,
                    dry,
                    actions);
            }

            if (leftoverChildMappedCore)
            {
                Finish(false,
                    $"leftover hosted child still has {Layout.CoreFolderName}/{Layout.CoreDllName} mapped; copy refused",
                    actions,
                    refuseSpawn: true,
                    leftoverChildMappedCore: true);
                return;
            }

            if (!HandleStalePluginsCoreDll(bepInExDir, serverDir, dry, actions))
            {
                Finish(false,
                    $"leftover {Layout.PluginsFolderName}/{Layout.CoreDllName} would load before Bootstrap",
                    actions,
                    refuseSpawn: true);
                return;
            }

            if (!linksOk || !coreOk || !pluginsOk)
            {
                Finish(false, "one or more staged folders failed to link or copy; stamp not written", actions);
                return;
            }

            foreach (var name in LocalFolders)
            {
                var dest = Path.Combine(bepInExDir, name);
                if (Directory.Exists(dest))
                {
                    actions.Add($"leave local {name} ({dest})");
                    continue;
                }
                EnsureDirectory(dest, serverDir, dry, actions);
            }

            // Write doorstop after core is copied/junctioned so the server-side target path exists.
            WriteDoorstopConfig(iniSrc, doorstopDest, serverDir, targetAssembly, coreclr, corlibDir, dry, actions);

            if (dry)
            {
                actions.Add($"would write stamp {stampPath}");
            }
            else
            {
                AssertUnder(stampPath, serverDir);
                File.WriteAllText(stampPath, stampToken, Encoding.UTF8);
                actions.Add($"wrote stamp {stampPath}");
            }

            Finish(true, dry ? "dry-run complete" : "staged", actions);
        }
        catch (Exception e)
        {
            actions.Add("threw: " + e);
            Finish(false, e.Message, actions);
        }
    }

    // ---- stamp / safety -----------------------------------------------------------------------

    private static string BuildStampToken(
        string coreDll,
        string shimSrc,
        string targetAssembly,
        HostedServerPluginsMode pluginsMode,
        string bootstrapDll,
        string progenitorCoreDll)
    {
        var sb = new StringBuilder();
        sb.AppendLine(Layout.StampVersion);
        sb.AppendLine(Layout.StampLayoutLine);
        sb.AppendLine("coreUtcTicks=" + File.GetLastWriteTimeUtc(coreDll).Ticks);
        sb.AppendLine("coreUtc=" + File.GetLastWriteTimeUtc(coreDll).ToString("o"));
        sb.AppendLine("shimUtcTicks=" + File.GetLastWriteTimeUtc(shimSrc).Ticks);
        AppendDllStamp(sb, "bootstrap", bootstrapDll);
        AppendDllStamp(sb, "progenitorCore", progenitorCoreDll);
        sb.AppendLine("clientBepInEx=" + Path.GetFullPath(Paths.BepInExRootPath));
        sb.AppendLine("targetAssembly=" + Path.GetFullPath(targetAssembly));
        sb.AppendLine("pluginsMode=" + pluginsMode);
        return sb.ToString();
    }

    private static void AppendDllStamp(StringBuilder sb, string key, string dllPath)
    {
        sb.AppendLine(Layout.StampDllUtcLine(key, UtcTicksOrZero(dllPath)));
        var next = dllPath + Layout.NextSuffix;
        var prev = dllPath + Layout.PrevSuffix;
        bool nextExists = File.Exists(next);
        bool prevExists = File.Exists(prev);
        sb.AppendLine(Layout.StampNextUtcLine(key, nextExists, UtcTicksOrZero(next)));
        sb.AppendLine(Layout.StampPrevUtcLine(key, prevExists, UtcTicksOrZero(prev)));
    }

    private static long UtcTicksOrZero(string path)
    {
        try
        {
            return File.Exists(path) ? File.GetLastWriteTimeUtc(path).Ticks : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static bool IsAlreadyStaged(
        string serverDir,
        string stampPath,
        string expectedToken,
        string expectedTargetAssembly,
        HostedServerPluginsMode pluginsMode)
    {
        try
        {
            if (!File.Exists(stampPath)) return false;
            if (!File.Exists(Path.Combine(serverDir, DoorstopShimName))) return false;
            var doorstopPath = Path.Combine(serverDir, DoorstopConfigName);
            if (!File.Exists(doorstopPath)) return false;
            var actual = File.ReadAllText(stampPath);
            if (!string.Equals(actual, expectedToken, StringComparison.Ordinal)) return false;
            // Old staging wrote the client's core as target_assembly; that stamp may still match
            // a v1 token if we did not bump, and must re-stage once. Always verify the path.
            if (!DoorstopTargetIs(doorstopPath, expectedTargetAssembly)) return false;

            var bep = Path.Combine(serverDir, "BepInEx");
            string clientBep = Path.GetFullPath(Paths.BepInExRootPath);
            foreach (var name in SharedFolders)
            {
                var source = Path.Combine(clientBep, name);
                var dest = Path.Combine(bep, name);
                bool exists = Directory.Exists(dest);
                bool reparse = exists && IsReparsePoint(dest);
                // Never read the copy marker through a junction: that would look into the client tree.
                bool pointsAt = reparse && LinkPointsAt(dest, source);
                bool markerPresent = false;
                string? markerContents = null;
                if (exists && !reparse)
                {
                    var marker = HostedPathRules.MarkerFilePath(dest);
                    if (File.Exists(marker))
                    {
                        markerPresent = true;
                        markerContents = File.ReadAllText(marker);
                    }
                }

                if (Layout.SkipSharedFolder(pluginsMode, name))
                {
                    // Real folder we wrote (marker + Bootstrap or Core DLL), never a junction
                    // at the client's progenitor-core (always copied) or ProgenitorOnly plugins.
                    bool isCore = Layout.IsCoreFolderName(name);
                    string expectedDll = isCore
                        ? Layout.CoreDllPath(bep)
                        : Layout.BootstrapDllPath(bep);
                    string clientDll = isCore
                        ? Layout.ClientCoreDllPath(clientBep)
                        : Layout.ClientBootstrapDllPath(clientBep);
                    bool expectedDllPresent = File.Exists(expectedDll);
                    bool staleCore = File.Exists(Layout.StalePluginsCoreDllPath(bep));
                    bool clientNext = File.Exists(clientDll + Layout.NextSuffix);
                    bool destNext = File.Exists(expectedDll + Layout.NextSuffix);
                    bool nextMatch = clientNext && destNext && SameFile(clientDll + Layout.NextSuffix, expectedDll + Layout.NextSuffix);
                    if (!Layout.ProgenitorOnlyFolderLooksStaged(
                            name,
                            exists,
                            reparse,
                            markerPresent,
                            markerContents,
                            expectedToken,
                            expectedDllPresent,
                            staleCore,
                            clientNext,
                            destNext,
                            nextMatch))
                        return false;
                    continue;
                }

                if (!Directory.Exists(source))
                {
                    // Core is required: a missing progenitor-core is not "already staged".
                    if (Layout.IsCoreFolderName(name)) return false;
                    continue;
                }

                if (!HostedPathRules.SharedFolderLooksStaged(
                        exists, reparse, pointsAt, markerPresent, markerContents, expectedToken))
                    return false;

                if (name.Equals(Layout.PluginsFolderName, StringComparison.OrdinalIgnoreCase)
                    && File.Exists(Layout.StalePluginsCoreDllPath(bep)))
                    return false;
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsSafeServerDir(string serverDir, out string reason)
    {
        try
        {
            return HostedPathRules.TryValidateServerDirectory(
                serverDir,
                Paths.GameRootPath,
                Paths.BepInExRootPath,
                out reason);
        }
        catch (Exception e)
        {
            reason = "safety check threw: " + e.Message;
            return false;
        }
    }

    private static void AssertUnder(string path, string serverDir)
    {
        if (!HostedPathRules.IsUnder(path, serverDir))
            throw new InvalidOperationException($"refusing to write '{path}' which is outside serverDir '{serverDir}'");
    }

    // ---- files --------------------------------------------------------------------------------

    private static void CopyShim(string source, string dest, string serverDir, bool dry, List<string> actions)
    {
        AssertUnder(dest, serverDir);
        if (dry)
        {
            actions.Add(File.Exists(dest) ? $"would refresh {DoorstopShimName}" : $"would copy {DoorstopShimName}");
            return;
        }
        if (SameFile(source, dest))
        {
            actions.Add($"{DoorstopShimName} already current");
            return;
        }
        File.Copy(source, dest, overwrite: true);
        actions.Add($"copied {DoorstopShimName} ({new FileInfo(source).Length} bytes)");
    }

    private static void WriteDoorstopConfig(
        string clientIniPath,
        string dest,
        string serverDir,
        string targetAssembly,
        string coreclr,
        string corlibDir,
        bool dry,
        List<string> actions)
    {
        AssertUnder(dest, serverDir);
        string template = File.Exists(clientIniPath)
            ? File.ReadAllText(clientIniPath)
            : DefaultDoorstopTemplate();

        string rewritten = RewriteDoorstop(template, targetAssembly, coreclr, corlibDir);
        if (dry)
        {
            actions.Add($"would write {DoorstopConfigName} with target_assembly={targetAssembly}");
            actions.Add($"would set coreclr_path={coreclr}");
            actions.Add($"would set corlib_dir={corlibDir}");
            return;
        }

        if (File.Exists(dest) && string.Equals(File.ReadAllText(dest), rewritten, StringComparison.Ordinal))
        {
            actions.Add($"{DoorstopConfigName} already has server target_assembly and client CoreCLR paths");
            return;
        }

        File.WriteAllText(dest, rewritten, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        actions.Add(
            $"wrote {DoorstopConfigName} (target_assembly -> {targetAssembly}; " +
            "coreclr_path/corlib_dir -> client absolute paths). " +
            "BepInEx root = directory of target_assembly's parent.");
    }

    /// <summary>
    /// True when <paramref name="iniPath"/> names <paramref name="expectedTarget"/> as
    /// <c>target_assembly</c>. BepInEx 6 IL2CPP (<c>UnityPreloadRunner.PreloaderMain</c>) sets
    /// <c>Paths.BepInExRootPath</c> to
    /// <c>Path.GetDirectoryName(Path.GetDirectoryName(DOORSTOP_INVOKE_DLL_PATH))</c> — the
    /// directory of <c>target_assembly</c>'s parent — so a client-core path made the child share
    /// the client's config/cache/<c>LogOutput.log</c>.
    /// </summary>
    private static bool DoorstopTargetIs(string iniPath, string expectedTarget)
    {
        try
        {
            if (!File.Exists(iniPath) || string.IsNullOrWhiteSpace(expectedTarget)) return false;
            string? actual = null;
            foreach (var raw in File.ReadAllLines(iniPath))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal) ||
                    line.StartsWith(";", StringComparison.Ordinal))
                    continue;
                int eq = line.IndexOf('=');
                if (eq < 0) continue;
                if (!line.Substring(0, eq).Trim().Equals("target_assembly", StringComparison.OrdinalIgnoreCase))
                    continue;
                actual = line.Substring(eq + 1).Trim().Trim('"');
                break;
            }
            if (string.IsNullOrWhiteSpace(actual)) return false;
            return HostedPathRules.PathsEqual(Path.GetFullPath(actual), Path.GetFullPath(expectedTarget));
        }
        catch
        {
            return false;
        }
    }

    private static string DefaultDoorstopTemplate() =>
        "[General]" + Environment.NewLine +
        "enabled = true" + Environment.NewLine +
        "target_assembly = BepInEx\\core\\BepInEx.Unity.IL2CPP.dll" + Environment.NewLine +
        "redirect_output_log = false" + Environment.NewLine +
        "ignore_disable_switch = false" + Environment.NewLine +
        Environment.NewLine +
        "[Il2Cpp]" + Environment.NewLine +
        "coreclr_path = dotnet\\coreclr.dll" + Environment.NewLine +
        "corlib_dir = dotnet" + Environment.NewLine;

    private static string RewriteDoorstop(string text, string targetAssembly, string coreclr, string corlibDir)
    {
        bool sawTarget = false, sawClr = false, sawCorlib = false;
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var output = new List<string>(lines.Length + 3);
        foreach (var raw in lines)
        {
            var line = raw;
            if (TryReplaceKey(ref line, "target_assembly", targetAssembly)) sawTarget = true;
            else if (TryReplaceKey(ref line, "coreclr_path", coreclr)) sawClr = true;
            else if (TryReplaceKey(ref line, "corlib_dir", corlibDir)) sawCorlib = true;
            output.Add(line);
        }
        if (!sawTarget) output.Add("target_assembly = " + targetAssembly);
        if (!sawClr) output.Add("coreclr_path = " + coreclr);
        if (!sawCorlib) output.Add("corlib_dir = " + corlibDir);
        return string.Join(Environment.NewLine, output);
    }

    private static bool TryReplaceKey(ref string line, string key, string value)
    {
        var trimmed = line.TrimStart();
        if (trimmed.StartsWith("#", StringComparison.Ordinal) || trimmed.StartsWith(";", StringComparison.Ordinal))
            return false;
        int eq = line.IndexOf('=');
        if (eq < 0) return false;
        if (!line.Substring(0, eq).Trim().Equals(key, StringComparison.OrdinalIgnoreCase)) return false;
        line = key + " = " + value;
        return true;
    }

    private static void EnsureDirectory(string path, string serverDir, bool dry, List<string> actions)
    {
        AssertUnder(path, serverDir);
        if (Directory.Exists(path)) return;
        if (dry)
        {
            actions.Add("would create " + path);
            return;
        }
        Directory.CreateDirectory(path);
        actions.Add("created " + path);
    }

    // ---- link / copy --------------------------------------------------------------------------

    private static bool LinkOrCopy(string source, string dest, string serverDir, string stampToken, bool dry, List<string> actions)
    {
        AssertUnder(dest, serverDir);
        var name = Path.GetFileName(dest.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        if (HostedPathRules.IsForbiddenLiveServerPath(source))
        {
            actions.Add($"FAILED {name}: refusing to junction a live dedicated server path ({source})");
            return false;
        }

        if (!Directory.Exists(source))
        {
            actions.Add($"skip {name}: client folder missing ({source})");
            return true;
        }

        if (Directory.Exists(dest) && LinkPointsAt(dest, source))
        {
            actions.Add($"{name} already linked -> {source}");
            return true;
        }

        if (dry)
        {
            if (Directory.Exists(dest) && !IsReparsePoint(dest))
            {
                bool marker = File.Exists(HostedPathRules.MarkerFilePath(dest));
                if (!Layout.MayRecursiveDeleteStagedFolder(dest, serverDir, marker, out var why))
                {
                    actions.Add($"would REFUSE {name}: {why}");
                    return false;
                }
            }
            actions.Add($"would link {name} -> {source} (junction, else symlink, else copy)");
            return true;
        }

        if (!TryClearDestForLink(dest, serverDir, actions))
        {
            actions.Add($"FAILED {name}: could not replace existing path {dest}");
            return false;
        }

        if (FolderLink.TryCreateJunction(dest, source, out var junctionError))
        {
            actions.Add($"junction {name} -> {source}");
            return true;
        }
        actions.Add($"junction {name} failed ({junctionError}); trying CreateSymbolicLink");

        try
        {
            Directory.CreateSymbolicLink(dest, source);
            if (Directory.Exists(dest))
            {
                actions.Add($"symlink {name} -> {source}");
                return true;
            }
        }
        catch (Exception e)
        {
            actions.Add($"CreateSymbolicLink {name} failed ({e.Message}); copying files");
            TryDeleteEmptyOrLink(dest);
        }

        CopyDirectory(source, dest, serverDir, stampToken, actions);
        actions.Add($"copied {name} -> {dest} (fallback; will refresh when the stamp changes)");
        return true;
    }

    /// <summary>
    /// <see cref="HostedServerPluginsMode.ProgenitorOnly"/>: a real <c>plugins</c> folder that
    /// holds only <c>Progenitor.Bootstrap.dll</c> copied from the client. Core lives in
    /// <c>progenitor-core/</c> (<see cref="StageProgenitorOnlyCore"/>). The <c>progenitor-reload</c>
    /// junction is created by the shared-folder loop, not here. Client-only mods in the client's
    /// plugins folder are therefore not loaded by the child. A leftover <c>Progenitor.dll</c> in
    /// this folder is deleted so the chainloader cannot map a stale Core.
    /// </summary>
    private static bool StageProgenitorOnlyPlugins(
        string dest,
        string sourceDll,
        string serverDir,
        string stampToken,
        bool dry,
        List<string> actions)
    {
        AssertUnder(dest, serverDir);
        if (!File.Exists(sourceDll))
        {
            actions.Add($"FAILED plugins (ProgenitorOnly): client {Layout.BootstrapDllName} missing ({sourceDll})");
            return false;
        }

        if (dry)
        {
            if (Directory.Exists(dest) && !IsReparsePoint(dest))
            {
                bool marker = File.Exists(HostedPathRules.MarkerFilePath(dest));
                if (!Layout.MayRecursiveDeleteStagedFolder(dest, serverDir, marker, out var why) &&
                    !(marker && File.Exists(Path.Combine(dest, Layout.BootstrapDllName))))
                {
                    actions.Add($"would REFUSE plugins: {why}");
                    return false;
                }
            }
            actions.Add(
                $"would write ProgenitorOnly plugins at {dest} (copy {Layout.BootstrapDllName} only; " +
                "client-only mods stay on the client)");
            return true;
        }

        var destDll = Path.Combine(dest, Layout.BootstrapDllName);
        bool alreadyOurs = Directory.Exists(dest) && !IsReparsePoint(dest)
                           && File.Exists(HostedPathRules.MarkerFilePath(dest))
                           && File.Exists(destDll)
                           && !File.Exists(Path.Combine(dest, Layout.CoreDllName));
        if (alreadyOurs)
        {
            var markerContents = File.ReadAllText(HostedPathRules.MarkerFilePath(dest));
            if (string.Equals(markerContents, stampToken, StringComparison.Ordinal) &&
                SameFile(sourceDll, destDll) &&
                SidecarCurrent(sourceDll, destDll))
            {
                actions.Add($"plugins already ProgenitorOnly ({Layout.BootstrapDllName} current, sidecars match)");
                return true;
            }

            // Stamp or live DLL matches but .next does not: refresh files in place, do not wipe.
            if (string.Equals(markerContents, stampToken, StringComparison.Ordinal))
            {
                CopyDllAndOptionalNext(sourceDll, destDll, serverDir, actions);
                actions.Add($"plugins ProgenitorOnly: refreshed {Layout.BootstrapDllName} and sidecars in place");
                return true;
            }
        }

        if (!TryClearDestForLink(dest, serverDir, actions))
        {
            actions.Add($"FAILED plugins (ProgenitorOnly): could not replace existing path {dest}");
            return false;
        }

        Directory.CreateDirectory(dest);
        CopyDllAndOptionalNext(sourceDll, destDll, serverDir, actions);
        WriteCopyMarker(dest, serverDir, stampToken, actions);
        actions.Add(
            $"ProgenitorOnly plugins: copied {Layout.BootstrapDllName} ({new FileInfo(sourceDll).Length} bytes) -> {dest}. " +
            "Client-only mods in the client's plugins folder will not run in the child.");
        return true;
    }

    /// <summary>
    /// Real <c>progenitor-core</c> folder holding only <c>Progenitor.dll</c> (and <c>.next</c>
    /// when the client has a staged Core update). Used in both plugin modes: not a junction,
    /// so the child's Bootstrap promote of <c>.next</c> cannot <c>File.Move</c> the client's
    /// mapped Core. A leftover bundled child that still has dest mapped refuses the copy
    /// (R12 finding 11) instead of <c>File.Copy</c> / recursive-delete over a running Core.
    /// </summary>
    private static bool StageProgenitorOnlyCore(
        string dest,
        string sourceDll,
        string serverDir,
        string stampToken,
        bool dry,
        List<string> actions,
        out bool leftoverChildMappedCore)
    {
        leftoverChildMappedCore = false;
        AssertUnder(dest, serverDir);
        if (!File.Exists(sourceDll))
        {
            actions.Add($"FAILED {Layout.CoreFolderName}: client {Layout.CoreDllName} missing ({sourceDll})");
            return false;
        }

        var destDll = Path.Combine(dest, Layout.CoreDllName);
        bool alreadyOurs = Directory.Exists(dest) && !IsReparsePoint(dest)
                           && File.Exists(HostedPathRules.MarkerFilePath(dest))
                           && File.Exists(destDll);
        string? markerContents = null;
        bool destNeedsWrite = true;
        if (alreadyOurs)
        {
            try { markerContents = File.ReadAllText(HostedPathRules.MarkerFilePath(dest)); }
            catch { markerContents = null; }
            destNeedsWrite = !(string.Equals(markerContents, stampToken, StringComparison.Ordinal)
                               && SameFile(sourceDll, destDll)
                               && SidecarCurrent(sourceDll, destDll));
        }

        bool leftoverAlive = TryFindLeftoverBundledChild(serverDir, destDll, out var leftoverPath);
        if (Layout.MustRefuseCoreCopyWhileLeftoverChildAlive(leftoverAlive, destNeedsWrite))
        {
            leftoverChildMappedCore = true;
            actions.Add(
                $"FAILED {Layout.CoreFolderName}: leftover hosted child still has {Layout.CoreDllName} mapped " +
                $"({leftoverPath}); refusing to copy or delete progenitor-core. Kill that VRisingServer.exe by path " +
                $"(never {HostedPathRules.ForbiddenLiveServerRoot}), then retry.");
            return false;
        }

        if (dry)
        {
            if (Directory.Exists(dest) && !IsReparsePoint(dest))
            {
                bool marker = File.Exists(HostedPathRules.MarkerFilePath(dest));
                if (!Layout.MayRecursiveDeleteStagedFolder(dest, serverDir, marker, out var why) &&
                    !(marker && File.Exists(Path.Combine(dest, Layout.CoreDllName))))
                {
                    actions.Add($"would REFUSE {Layout.CoreFolderName}: {why}");
                    return false;
                }
            }
            actions.Add(
                destNeedsWrite
                    ? $"would write {Layout.CoreFolderName} at {dest} (copy {Layout.CoreDllName}; " +
                      "Bootstrap loads Core from here; never a junction)"
                    : $"{Layout.CoreFolderName} already copied ({Layout.CoreDllName} current, sidecars match)");
            return true;
        }

        if (!destNeedsWrite)
        {
            actions.Add($"{Layout.CoreFolderName} already copied ({Layout.CoreDllName} current, sidecars match)");
            return true;
        }

        if (alreadyOurs && string.Equals(markerContents, stampToken, StringComparison.Ordinal))
        {
            CopyDllAndOptionalNext(sourceDll, destDll, serverDir, actions);
            actions.Add($"{Layout.CoreFolderName}: refreshed {Layout.CoreDllName} and sidecars in place");
            return true;
        }

        if (!TryClearDestForLink(dest, serverDir, actions))
        {
            actions.Add($"FAILED {Layout.CoreFolderName}: could not replace existing path {dest}");
            return false;
        }

        Directory.CreateDirectory(dest);
        CopyDllAndOptionalNext(sourceDll, destDll, serverDir, actions);
        WriteCopyMarker(dest, serverDir, stampToken, actions);
        actions.Add(
            $"{Layout.CoreFolderName}: copied {Layout.CoreDllName} ({new FileInfo(sourceDll).Length} bytes) -> {dest}.");
        return true;
    }

    /// <summary>
    /// True when a <c>VRisingServer.exe</c> under <paramref name="serverDir"/> is still
    /// running and would have <paramref name="destCoreDll"/> mapped. Reuses
    /// <see cref="HostedServerLauncher.ChildLogProbe.IsBundledChildProcess"/> (path-matched
    /// leftover scan; never the live dedicated root). Prefers
    /// <c>QueryFullProcessImageName</c> so an access-denied <c>MainModule</c> does not miss.
    /// </summary>
    private static bool TryFindLeftoverBundledChild(string serverDir, string destCoreDll, out string foundPath)
    {
        foundPath = "";
        int seen = 0;
        int identified = 0;
        bool found = false;
        try
        {
            foreach (var proc in Process.GetProcessesByName("VRisingServer"))
            {
                seen++;
                try
                {
                    if (!TryGetProcessImagePath(proc, out var path) || path.Length == 0)
                        continue;

                    identified++;
                    if (found) continue;
                    if (!Layout.LeftoverChildOwnsDest(path, serverDir, destCoreDll))
                        continue;

                    foundPath = path;
                    found = true;
                }
                finally
                {
                    try { proc.Dispose(); } catch { /* best-effort */ }
                }
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning($"Hosted staging: leftover-child scan threw: {e.Message}");
            foundPath = "(leftover-child scan threw; treating as alive)";
            return Layout.LeftoverChildScanTreatAsAlive(
                scanThrew: true,
                processesSeen: seen,
                processesIdentified: identified,
                foundMatchingChild: found);
        }

        if (Layout.LeftoverChildScanTreatAsAlive(
                scanThrew: false,
                processesSeen: seen,
                processesIdentified: identified,
                foundMatchingChild: found))
        {
            if (!found)
                foundPath = "(VRisingServer processes exist but none could be identified; treating as leftover alive)";
            return true;
        }

        return false;
    }

    private const uint ProcessQueryLimitedInformation = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(
        IntPtr hProcess, int dwFlags, StringBuilder lpExeName, ref int lpdwSize);

    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "CloseHandle")]
    private static extern bool CloseHandleProcess(IntPtr hObject);

    private static bool TryGetProcessImagePath(Process proc, out string path)
    {
        path = "";
        try
        {
            if (TryQueryFullProcessImageName(proc.Id, out var queried) && queried.Length > 0)
            {
                path = queried;
                return true;
            }
        }
        catch { /* fall through to MainModule */ }

        try
        {
            path = proc.MainModule?.FileName ?? "";
            return path.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryQueryFullProcessImageName(int pid, out string path)
    {
        path = "";
        IntPtr handle = IntPtr.Zero;
        try
        {
            handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
            if (handle == IntPtr.Zero) return false;
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            if (!QueryFullProcessImageName(handle, 0, sb, ref size) || size <= 0)
                return false;
            path = sb.ToString();
            return path.Length > 0;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (handle != IntPtr.Zero)
            {
                try { CloseHandleProcess(handle); } catch { /* best-effort */ }
            }
        }
    }

    /// <summary>
    /// Copy <paramref name="sourceDll"/> and, when present, its <c>.next</c> / <c>.prev</c>
    /// siblings so the child's Bootstrap can promote a staged Core this launch. A leftover
    /// child sidecar is removed when the client no longer has one.
    /// </summary>
    private static void CopyDllAndOptionalNext(
        string sourceDll, string destDll, string serverDir, List<string> actions)
    {
        AssertUnder(destDll, serverDir);
        File.Copy(sourceDll, destDll, overwrite: true);
        CopyOrDeleteSibling(sourceDll, destDll, Layout.NextSuffix, serverDir, actions);
        CopyOrDeleteSibling(sourceDll, destDll, Layout.PrevSuffix, serverDir, actions);
    }

    private static void CopyOrDeleteSibling(
        string sourceDll, string destDll, string suffix, string serverDir, List<string> actions)
    {
        var src = sourceDll + suffix;
        var dest = destDll + suffix;
        if (File.Exists(src))
        {
            AssertUnder(dest, serverDir);
            File.Copy(src, dest, overwrite: true);
            actions.Add($"copied {Path.GetFileName(src)} so the child can promote it");
        }
        else if (File.Exists(dest))
        {
            AssertUnder(dest, serverDir);
            File.Delete(dest);
            actions.Add($"removed stale {Path.GetFileName(dest)}");
        }
    }

    private static bool SidecarCurrent(string sourceDll, string destDll)
    {
        return SidecarPairCurrent(sourceDll, destDll, Layout.NextSuffix)
               && SidecarPairCurrent(sourceDll, destDll, Layout.PrevSuffix);
    }

    private static bool SidecarPairCurrent(string sourceDll, string destDll, string suffix)
    {
        var src = sourceDll + suffix;
        var dest = destDll + suffix;
        bool srcExists = File.Exists(src);
        bool destExists = File.Exists(dest);
        bool match = srcExists && destExists && SameFile(src, dest);
        // .prev uses the same presence rule as .next: both missing, or both present and same.
        return Layout.NextSidecarLooksStaged(srcExists, destExists, match);
    }

    /// <summary>
    /// The child must never expose <c>plugins/Progenitor.dll</c>: the chainloader would load it
    /// before Bootstrap and steal the Core identity. Delete it from a real child plugins folder;
    /// refuse when it is only visible through a junction (that path is the client's file).
    /// </summary>
    private static bool HandleStalePluginsCoreDll(
        string bepInExDir, string serverDir, bool dry, List<string> actions)
    {
        var pluginsDir = Path.Combine(bepInExDir, Layout.PluginsFolderName);
        var stale = Layout.StalePluginsCoreDllPath(bepInExDir);
        bool exists = File.Exists(stale);
        bool reparse = Directory.Exists(pluginsDir) && IsReparsePoint(pluginsDir);
        var disposition = Layout.DecideStalePluginsCoreDll(exists, reparse);
        switch (disposition)
        {
            case Layout.StaleCoreDllDisposition.None:
                return true;
            case Layout.StaleCoreDllDisposition.Refuse:
                actions.Add(
                    $"leftover {Layout.PluginsFolderName}/{Layout.CoreDllName} is visible through a junction; " +
                    "refusing to delete it (that path is the client's file). Remove the client's leftover " +
                    "Core DLL so the child's chainloader does not load it.");
                return false;
            case Layout.StaleCoreDllDisposition.Delete:
                try
                {
                    foreach (var name in Layout.StalePluginsCoreFileNames)
                    {
                        var path = Path.Combine(pluginsDir, name);
                        if (!File.Exists(path)) continue;
                        if (HostedPathRules.IsForbiddenLiveServerPath(path)
                            || HostedPathRules.IsForbiddenLiveServerPath(serverDir))
                        {
                            actions.Add(
                                $"refusing to delete a path under {HostedPathRules.ForbiddenLiveServerRoot}: {path}");
                            return false;
                        }
                        AssertUnder(path, serverDir);
                        if (dry)
                        {
                            actions.Add($"would delete leftover plugins/{name}");
                            continue;
                        }
                        File.Delete(path);
                        if (File.Exists(path))
                        {
                            actions.Add($"could not delete leftover plugins/{name}; refusing spawn");
                            return false;
                        }
                        actions.Add($"deleted leftover plugins/{name}");
                    }
                    return true;
                }
                catch (Exception e)
                {
                    actions.Add($"deleting leftover plugins Core threw ({e.Message}); refusing spawn");
                    return false;
                }
            default:
                return false;
        }
    }

    private static bool TryClearDestForLink(string dest, string serverDir, List<string> actions)
    {
        if (!Directory.Exists(dest) && !File.Exists(dest)) return true;
        if (HostedPathRules.IsForbiddenLiveServerPath(dest) || HostedPathRules.IsForbiddenLiveServerPath(serverDir))
        {
            actions.Add($"refusing to modify a path under {HostedPathRules.ForbiddenLiveServerRoot}: {dest}");
            return false;
        }
        if (!HostedPathRules.IsUnder(dest, serverDir)) return false;

        try
        {
            if (File.Exists(dest) && !Directory.Exists(dest))
            {
                var parent = Path.GetDirectoryName(dest);
                var expectedParent = Path.Combine(serverDir, "BepInEx");
                var name = Path.GetFileName(dest);
                if (parent == null ||
                    !HostedPathRules.PathsEqual(parent, expectedParent) ||
                    !Layout.IsLinkableFolderName(name))
                {
                    actions.Add($"refusing to delete unexpected file {dest}");
                    return false;
                }
                File.Delete(dest);
                actions.Add($"removed stray file {dest}");
                return true;
            }

            var info = new DirectoryInfo(dest);
            bool isLink = (info.Attributes & FileAttributes.ReparsePoint) != 0
                          || !string.IsNullOrEmpty(info.LinkTarget);
            if (isLink)
            {
                // RemoveDirectory on a reparse point drops the link, never the target.
                if (!FolderLink.RemoveReparsePoint(dest))
                    Directory.Delete(dest);
                actions.Add($"removed existing reparse point {dest}");
                return !Directory.Exists(dest) && !File.Exists(dest);
            }

            // A previous copy-fallback left a real directory. Recursive-delete only when
            // Progenitor itself wrote the marker into that folder.
            bool markerPresent = File.Exists(HostedPathRules.MarkerFilePath(dest));
            if (!Layout.MayRecursiveDeleteStagedFolder(dest, serverDir, markerPresent, out var why))
            {
                actions.Add(why);
                return false;
            }

            Directory.Delete(dest, recursive: true);
            actions.Add($"removed previous Progenitor copy of {Path.GetFileName(dest)} so it can be linked");
            return true;
        }
        catch (Exception e)
        {
            actions.Add($"could not clear {dest}: {e.Message}");
            return false;
        }
    }

    private static void TryDeleteEmptyOrLink(string dest)
    {
        try
        {
            if (!Directory.Exists(dest)) return;
            var attrs = File.GetAttributes(dest);
            if ((attrs & FileAttributes.ReparsePoint) != 0)
            {
                FolderLink.RemoveReparsePoint(dest);
                return;
            }
            if (Directory.GetFileSystemEntries(dest).Length == 0)
                Directory.Delete(dest);
        }
        catch
        {
            // best-effort cleanup before the copy fallback
        }
    }

    private static bool LinkPointsAt(string linkPath, string expectedTarget)
    {
        try
        {
            var info = new DirectoryInfo(linkPath);
            if ((info.Attributes & FileAttributes.ReparsePoint) == 0) return false;
            var target = info.LinkTarget;
            if (string.IsNullOrEmpty(target)) return false;
            var resolved = Path.IsPathRooted(target)
                ? Path.GetFullPath(target)
                : Path.GetFullPath(Path.Combine(linkPath, target));
            return HostedPathRules.PathsEqual(resolved, expectedTarget);
        }
        catch
        {
            return false;
        }
    }

    private static void CopyDirectory(string source, string dest, string serverDir, string stampToken, List<string> actions)
    {
        AssertUnder(dest, serverDir);
        Directory.CreateDirectory(dest);
        int copied = 0, skipped = 0, failed = 0;

        foreach (var dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
        {
            try
            {
                var attrs = File.GetAttributes(dir);
                if ((attrs & FileAttributes.ReparsePoint) != 0) continue;
                var rel = Path.GetRelativePath(source, dir);
                var target = Path.Combine(dest, rel);
                AssertUnder(target, serverDir);
                Directory.CreateDirectory(target);
            }
            catch (Exception e)
            {
                failed++;
                actions.Add($"copy mkdir failed: {e.Message}");
            }
        }

        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            try
            {
                if (Path.GetFileName(file).Equals(HostedPathRules.StagedCopyMarkerName, StringComparison.OrdinalIgnoreCase))
                    continue;
                var rel = Path.GetRelativePath(source, file);
                var target = Path.Combine(dest, rel);
                AssertUnder(target, serverDir);
                var targetDir = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(targetDir)) Directory.CreateDirectory(targetDir);
                if (SameFile(file, target)) { skipped++; continue; }
                File.Copy(file, target, overwrite: true);
                copied++;
            }
            catch (Exception e)
            {
                failed++;
                actions.Add($"copy file failed ({Path.GetFileName(file)}): {e.Message}");
            }
        }

        WriteCopyMarker(dest, serverDir, stampToken, actions);
        actions.Add($"copy {Path.GetFileName(source)}: {copied} written, {skipped} current, {failed} failed");
    }

    private static void WriteCopyMarker(string dest, string serverDir, string stampToken, List<string> actions)
    {
        try
        {
            var marker = HostedPathRules.MarkerFilePath(dest);
            AssertUnder(marker, serverDir);
            File.WriteAllText(marker, stampToken, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            actions.Add($"wrote {HostedPathRules.StagedCopyMarkerName} in {Path.GetFileName(dest)}");
        }
        catch (Exception e)
        {
            actions.Add($"failed to write copy marker in {dest}: {e.Message}");
        }
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            if (!Directory.Exists(path)) return false;
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool SameFile(string a, string b)
    {
        try
        {
            if (!File.Exists(b)) return false;
            var ai = new FileInfo(a);
            var bi = new FileInfo(b);
            return ai.Length == bi.Length && ai.LastWriteTimeUtc == bi.LastWriteTimeUtc;
        }
        catch
        {
            return false;
        }
    }

    private static void Finish(bool ok, string headline, List<string> actions, bool refuseSpawn = false, bool leftoverChildMappedCore = false)
    {
        LastOk = ok;
        LastRefuseSpawn = refuseSpawn;
        LastLeftoverChildMappedCore = leftoverChildMappedCore;
        var sb = new StringBuilder();
        sb.Append(ok ? "ok: " : "FAILED: ");
        sb.AppendLine(headline);
        foreach (var a in actions) sb.AppendLine(a);
        LastSummary = sb.ToString().TrimEnd();

        var log = Plugin.Logger;
        if (log == null) return;
        if (ok) log.LogInfo("Hosted staging: " + headline);
        else log.LogError("Hosted staging: " + headline);
        foreach (var a in actions)
        {
            if (ok) log.LogInfo("  " + a);
            else log.LogError("  " + a);
        }
    }

    /// <summary>
    /// NTFS directory junction (no elevation), then <see cref="Directory.CreateSymbolicLink"/>, then
    /// a recursive copy. Junctions are the preferred link: they work without Developer Mode.
    /// </summary>
    private static unsafe class FolderLink
    {
        private const uint GenericWrite = 0x40000000;
        private const uint OpenExisting = 3;
        private const uint FileFlagBackupSemantics = 0x02000000;
        private const uint FileFlagOpenReparsePoint = 0x00200000;
        private const uint FsctlSetReparsePoint = 0x000900A4;
        private const uint IoReparseTagMountPoint = 0xA0000003;
        private static readonly IntPtr InvalidHandle = new(-1);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateFileW(
            string lpFileName,
            uint dwDesiredAccess,
            uint dwShareMode,
            IntPtr lpSecurityAttributes,
            uint dwCreationDisposition,
            uint dwFlagsAndAttributes,
            IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(
            IntPtr hDevice,
            uint dwIoControlCode,
            IntPtr lpInBuffer,
            uint nInBufferSize,
            IntPtr lpOutBuffer,
            uint nOutBufferSize,
            out uint lpBytesReturned,
            IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool RemoveDirectoryW(string lpPathName);

        internal static bool TryCreateJunction(string linkPath, string targetPath, out string error)
        {
            error = "";
            if (!OperatingSystem.IsWindows())
            {
                error = "not Windows";
                return false;
            }

            try
            {
                targetPath = Path.GetFullPath(targetPath).TrimEnd('\\', '/');
                if (Directory.Exists(linkPath) || File.Exists(linkPath))
                {
                    error = "path exists";
                    return false;
                }

                Directory.CreateDirectory(linkPath);

                string subst = @"\??\" + targetPath;
                string print = targetPath;
                byte[] substBytes = Encoding.Unicode.GetBytes(subst + "\0");
                byte[] printBytes = Encoding.Unicode.GetBytes(print + "\0");

                // Mount-point reparse buffer: tag, data length, reserved, then four USHORTs and PathBuffer.
                int pathBytes = substBytes.Length + printBytes.Length;
                int dataLength = 8 + pathBytes;
                byte[] buf = new byte[8 + dataLength];
                using (var ms = new MemoryStream(buf))
                using (var w = new BinaryWriter(ms))
                {
                    w.Write(IoReparseTagMountPoint);
                    w.Write((ushort)dataLength);
                    w.Write((ushort)0);
                    w.Write((ushort)0); // substitute name offset
                    w.Write((ushort)(substBytes.Length - 2));
                    w.Write((ushort)substBytes.Length); // print name offset (after subst including null)
                    w.Write((ushort)(printBytes.Length - 2));
                    w.Write(substBytes);
                    w.Write(printBytes);
                }

                IntPtr handle = CreateFileW(
                    linkPath,
                    GenericWrite,
                    0,
                    IntPtr.Zero,
                    OpenExisting,
                    FileFlagBackupSemantics | FileFlagOpenReparsePoint,
                    IntPtr.Zero);
                if (handle == IntPtr.Zero || handle == InvalidHandle)
                {
                    error = "CreateFileW " + Marshal.GetLastWin32Error();
                    TryRemoveDir(linkPath);
                    return false;
                }

                try
                {
                    fixed (byte* p = buf)
                    {
                        if (!DeviceIoControl(handle, FsctlSetReparsePoint, (IntPtr)p, (uint)buf.Length,
                                IntPtr.Zero, 0, out _, IntPtr.Zero))
                        {
                            error = "DeviceIoControl " + Marshal.GetLastWin32Error();
                            return false;
                        }
                    }
                    return true;
                }
                finally
                {
                    CloseHandle(handle);
                    if (!string.IsNullOrEmpty(error))
                        TryRemoveDir(linkPath);
                }
            }
            catch (Exception e)
            {
                error = e.Message;
                TryRemoveDir(linkPath);
                return false;
            }
        }

        internal static bool RemoveReparsePoint(string path)
        {
            try
            {
                return RemoveDirectoryW(path);
            }
            catch
            {
                try { Directory.Delete(path); return true; }
                catch { return false; }
            }
        }

        private static void TryRemoveDir(string path)
        {
            try
            {
                if (Directory.Exists(path))
                    RemoveDirectoryW(path);
            }
            catch
            {
                // ignored
            }
        }
    }
#endif
}
