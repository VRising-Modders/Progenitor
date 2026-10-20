using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Progenitor.Install;

/// <summary>The client's own current Progenitor image: the only install source. Never a download.</summary>
internal sealed class ServerInstallSource
{
    public ServerInstallSource(string bootstrapPath, string corePath, string version)
    {
        BootstrapPath = bootstrapPath ?? "";
        CorePath = corePath ?? "";
        Version = version ?? "";
    }

    public string BootstrapPath { get; }
    public string CorePath { get; }
    public string Version { get; }

    /// <summary>Client image under <paramref name="clientBepInExRoot"/> (<c>plugins/</c> + <c>progenitor-core/</c>).</summary>
    public static ServerInstallSource FromClientRoot(string clientBepInExRoot, string version) =>
        new(
            Hosted.Staging.Layout.BootstrapDllPath(clientBepInExRoot),
            Hosted.Staging.Layout.CoreDllPath(clientBepInExRoot),
            version);

    /// <summary>The running client's image, or null when BepInEx paths are unavailable (tests, no Bootstrap).</summary>
    public static ServerInstallSource? Detect()
    {
#if PROGENITOR_TESTS
        return null;
#else
        try
        {
            string root = BepInEx.Paths.BepInExRootPath;
            if (string.IsNullOrWhiteSpace(root)) return null;
            return FromClientRoot(root, Plugin.Version);
        }
        catch
        {
            return null;
        }
#endif
    }
}

internal enum ServerInstallState
{
    NotInstalled = 0,
    Installed = 1,
    UpdateAvailable = 2,
    Refused = 3,
}

/// <summary>What the Configuration pane shows and <c>mods_ui</c> reads back.</summary>
internal sealed class ServerInstallStatus
{
    public ServerInstallStatus(
        ServerInstallState state,
        string version,
        string reason,
        ServerInstallLedger? ledger,
        string installedBootstrapSha,
        string clientBootstrapSha,
        string installedCoreSha,
        string clientCoreSha)
    {
        State = state;
        Version = version ?? "";
        Reason = reason ?? "";
        Ledger = ledger;
        InstalledBootstrapSha = installedBootstrapSha ?? "";
        ClientBootstrapSha = clientBootstrapSha ?? "";
        InstalledCoreSha = installedCoreSha ?? "";
        ClientCoreSha = clientCoreSha ?? "";
    }

    public ServerInstallState State { get; }
    public string Version { get; }
    public string Reason { get; }
    public ServerInstallLedger? Ledger { get; }
    public string InstalledBootstrapSha { get; }
    public string ClientBootstrapSha { get; }
    public string InstalledCoreSha { get; }
    public string ClientCoreSha { get; }

    public IReadOnlyList<string> LedgerFiles
    {
        get
        {
            var list = new List<string>();
            if (Ledger == null) return list;
            for (int i = 0; i < Ledger.Files.Count; i++) list.Add(Ledger.Files[i].Path);
            return list;
        }
    }

    public string StateName => ServerInstall.StateName(State);
}

internal sealed class ServerInstallResult
{
    public ServerInstallResult(
        bool ok,
        string message,
        IReadOnlyList<string> removed,
        IReadOnlyList<string> skipped,
        bool noOp = false,
        bool hotUnloaded = false,
        IReadOnlyList<string>? dependents = null)
    {
        Ok = ok;
        Message = message ?? "";
        Removed = removed ?? Array.Empty<string>();
        Skipped = skipped ?? Array.Empty<string>();
        NoOp = noOp;
        HotUnloaded = hotUnloaded;
        Dependents = dependents ?? Array.Empty<string>();
    }

    public bool Ok { get; }
    public string Message { get; }
    /// <summary>Uninstall: ledger files deleted. Install: files written.</summary>
    public IReadOnlyList<string> Removed { get; }
    /// <summary>Uninstall: ledger files left in place because their bytes changed.</summary>
    public IReadOnlyList<string> Skipped { get; }
    public bool NoOp { get; }

    /// <summary>
    /// HOT1: one of the removed files was a <c>progenitor-reload/</c> copy, so the reloader unloads
    /// that plugin within a second and nothing stays loaded until a restart.
    /// </summary>
    public bool HotUnloaded { get; }

    /// <summary>
    /// DEPS1: the ledgered packages that list the removed package among their own Thunderstore
    /// dependencies. Non-empty on a removal that was refused for that reason, and on a forced removal
    /// it names what was broken on purpose. The readback is <c>removed.dependents[]</c>.
    /// </summary>
    public IReadOnlyList<string> Dependents { get; }

    public static ServerInstallResult Refuse(string reason) =>
        new(false, reason, Array.Empty<string>(), Array.Empty<string>());

    /// <summary>DEPS1: refused because other installs depend on this package; nothing was deleted.</summary>
    public static ServerInstallResult RefuseWithDependents(string reason, IReadOnlyList<string> dependents) =>
        new(false, reason, Array.Empty<string>(), Array.Empty<string>(), noOp: false, hotUnloaded: false, dependents: dependents);
}

/// <summary>
/// INSTORD2: what one <see cref="ServerInstall.ReconcilePromoted"/> pass changed on a root. Empty
/// when the ledger was absent, carried no stale <c>pending</c> row, or could not be rewritten.
/// </summary>
internal sealed class LedgerReconcileResult
{
    public LedgerReconcileResult(IReadOnlyList<string> rewritten, IReadOnlyList<string> dropped)
    {
        Rewritten = rewritten ?? Array.Empty<string>();
        Dropped = dropped ?? Array.Empty<string>();
    }

    /// <summary>Rows now naming their promoted <c>progenitor-reload/</c> path as category <c>mod</c>.</summary>
    public IReadOnlyList<string> Rewritten { get; }

    /// <summary>Rows dropped because the pending file is gone and no reload copy carries its bytes.</summary>
    public IReadOnlyList<string> Dropped { get; }

    public bool Changed => Rewritten.Count > 0 || Dropped.Count > 0;
}

/// <summary>
/// Install Progenitor onto a validated same-machine dedicated-server folder from the client's
/// own image, and remove it again together with every file the ledger says Progenitor wrote
/// there. Copies and deletes files only: this type never starts, stops or signals a process,
/// and it refuses while a <c>VRisingServer.exe</c> from inside the folder is running.
/// Stopping and starting that server is <see cref="ServerProcessControl"/>'s job, offered by the
/// same pane directly under this toggle.
/// </summary>
internal static class ServerInstall
{
    public const string BootstrapRelative = "BepInEx/plugins/" + Hosted.Staging.Layout.BootstrapDllName;
    public const string CoreRelative = "BepInEx/" + Hosted.Staging.Layout.CoreFolderName + "/" + Hosted.Staging.Layout.CoreDllName;
    public const string PendingRelative = "BepInEx/" + Networking.ModManifestPending.PendingFolderName;
    public const string ReloadRelative = "BepInEx/" + Networking.ModFolders.ReloadFolderName;

    public const string RefusalNoBepInEx = "BepInEx is not installed on the server";
    public const string RefusalRunningPrefix = "VRisingServer.exe is running from that folder";
    /// <summary>SERVERINST3: the toggle stays refused while the server runs, but Progenitor can now stop it — the row is right below.</summary>
    public const string RefusalRunningSuffix = "Stop the server first (Stop server below), then install or remove.";
    public const string RefusalUnwritable = "The server folder is not writable.";
    public const string RefusalNoSpace = "Not enough free space on the server's drive.";
    public const string RefusalNoClientImage = "This client's own Progenitor files were not found, so there is nothing to copy.";
    public const string RefusalNoFolder = "No validated local dedicated-server folder.";
    public const string RefusalCopyMismatch = "A copied file did not hash as the client's file; the partial install was removed.";
    public const string RestartNote = "(server needs a restart)";
    public const string NotInstalledStatus = "Progenitor is not installed on this server";
    public const string PromptPrefix = "This removes Progenitor from the server AND every mod Progenitor installed there";
    public const string PromptSuffix = "Mods installed by other tools are not touched. The server needs a restart.";

    private static readonly string[] OwnedFolders =
    {
        PendingRelative,
        ReloadRelative,
        "BepInEx/" + Hosted.Staging.Layout.CoreFolderName,
    };

    /// <summary>Test seam: server root → full path of a running <c>VRisingServer.exe</c> inside it, or null. Production scans processes by full path.</summary>
    internal static Func<string, string?>? RunningServerProbe { get; set; }

    /// <summary>Test seam: server root → free bytes. Production reads the drive.</summary>
    internal static Func<string, long>? FreeSpaceOverride { get; set; }

    /// <summary>Test seam: directory → writable. Production shares <see cref="LocalServerFolder"/>'s probe.</summary>
    internal static Func<string, bool>? WriteAccessOverride { get; set; }

    internal static Func<DateTime>? ClockUtc { get; set; }

    public static string StateName(ServerInstallState state) => state switch
    {
        ServerInstallState.Installed => "installed",
        ServerInstallState.UpdateAvailable => "update-available",
        ServerInstallState.Refused => "refused",
        _ => "not-installed",
    };

    public static string InstalledStatus(string version) =>
        "Progenitor " + version + " installed " + RestartNote;

    public static string UpdateStatus(ServerInstallStatus status) =>
        "Update available: installed " + Short(status.InstalledBootstrapSha) + "/" + Short(status.InstalledCoreSha)
        + " → client " + Short(status.ClientBootstrapSha) + "/" + Short(status.ClientCoreSha);

    public static string RunningRefusal(string exePath) =>
        RefusalRunningPrefix + " (" + exePath + "). " + RefusalRunningSuffix;

    /// <summary>The uninstall confirmation. Lists N and the ledger's paths.</summary>
    public static string PromptText(ServerInstallLedger? ledger)
    {
        var names = new List<string>();
        if (ledger != null)
        {
            for (int i = 0; i < ledger.Files.Count; i++) names.Add(ledger.Files[i].Path);
        }

        var sb = new StringBuilder();
        sb.Append(PromptPrefix);
        sb.Append(" (").Append(names.Count).Append(names.Count == 1 ? " file: " : " files: ");
        sb.Append(string.Join(", ", names));
        sb.Append("). ");
        sb.Append(PromptSuffix);
        return sb.ToString();
    }

    /// <summary>Read-only: ledger, client hashes and refusals. Never writes.</summary>
    public static ServerInstallStatus Inspect(string? serverRoot, ServerInstallSource? source)
    {
        if (string.IsNullOrWhiteSpace(serverRoot) || !Directory.Exists(serverRoot))
            return new ServerInstallStatus(ServerInstallState.Refused, "", RefusalNoFolder, null, "", "", "", "");

        var ledger = ServerInstallLedger.TryRead(serverRoot);
        string? running = FindRunningServer(serverRoot);
        if (running != null)
            return new ServerInstallStatus(ServerInstallState.Refused, ledger?.Version ?? "", RunningRefusal(running), ledger, ledger?.BootstrapSha256 ?? "", "", ledger?.CoreSha256 ?? "", "");

        if (!HasBepInExCore(serverRoot))
            return new ServerInstallStatus(ServerInstallState.Refused, ledger?.Version ?? "", RefusalNoBepInEx, ledger, ledger?.BootstrapSha256 ?? "", "", ledger?.CoreSha256 ?? "", "");

        string clientBootstrap = "", clientCore = "";
        bool haveSource = source != null && File.Exists(source.BootstrapPath) && File.Exists(source.CorePath);
        if (haveSource)
        {
            try
            {
                clientBootstrap = CatalogFetcher.HashFile(source!.BootstrapPath);
                clientCore = CatalogFetcher.HashFile(source.CorePath);
            }
            catch
            {
                haveSource = false;
            }
        }

        if (ledger == null)
        {
            if (!haveSource)
                return new ServerInstallStatus(ServerInstallState.Refused, "", RefusalNoClientImage, null, "", clientBootstrap, "", clientCore);
            return new ServerInstallStatus(ServerInstallState.NotInstalled, source!.Version, "", null, "", clientBootstrap, "", clientCore);
        }

        bool same = haveSource
            && string.Equals(ledger.BootstrapSha256, clientBootstrap, StringComparison.OrdinalIgnoreCase)
            && string.Equals(ledger.CoreSha256, clientCore, StringComparison.OrdinalIgnoreCase);
        var state = !haveSource || same ? ServerInstallState.Installed : ServerInstallState.UpdateAvailable;
        return new ServerInstallStatus(state, ledger.Version, "", ledger, ledger.BootstrapSha256, clientBootstrap, ledger.CoreSha256, clientCore);
    }

    /// <summary>
    /// Copy Bootstrap and Core from the client image, verify each copy's sha, create the
    /// pending and reload folders, write the ledger. Identical hashes already installed is a
    /// no-op; different hashes replace the two framework files and keep the ledger's mod and
    /// pending entries. A failed copy removes what this call wrote.
    /// </summary>
    public static ServerInstallResult Install(string? serverRoot, ServerInstallSource? source)
    {
        var status = Inspect(serverRoot, source);
        if (status.State == ServerInstallState.Refused)
            return ServerInstallResult.Refuse(status.Reason);
        if (source == null || !File.Exists(source.BootstrapPath) || !File.Exists(source.CorePath))
            return ServerInstallResult.Refuse(RefusalNoClientImage);
        if (status.State == ServerInstallState.Installed)
            return new ServerInstallResult(true, InstalledStatus(status.Version), Array.Empty<string>(), Array.Empty<string>(), noOp: true);

        string root = serverRoot!;
        if (!HasWriteAccess(Path.Combine(root, LocalServerFolder.BepInExFolderName)))
            return ServerInstallResult.Refuse(RefusalUnwritable);

        byte[] bootstrapBytes, coreBytes;
        try
        {
            bootstrapBytes = File.ReadAllBytes(source.BootstrapPath);
            coreBytes = File.ReadAllBytes(source.CorePath);
        }
        catch (Exception e)
        {
            return ServerInstallResult.Refuse(RefusalNoClientImage + " " + e.Message);
        }

        long need = bootstrapBytes.LongLength + coreBytes.LongLength;
        if (FreeSpace(root) < need)
            return ServerInstallResult.Refuse(RefusalNoSpace);

        string bootstrapSha = CatalogFetcher.ToHex(System.Security.Cryptography.SHA256.HashData(bootstrapBytes));
        string coreSha = CatalogFetcher.ToHex(System.Security.Cryptography.SHA256.HashData(coreBytes));
        string bootstrapDest = ServerInstallLedger.Absolute(root, BootstrapRelative);
        string coreDest = ServerInstallLedger.Absolute(root, CoreRelative);
        var written = new List<string>();
        DateTime now = Now();
        try
        {
            InstallStaging.AtomicWrite(bootstrapDest, bootstrapBytes);
            written.Add(bootstrapDest);
            InstallStaging.AtomicWrite(coreDest, coreBytes);
            written.Add(coreDest);
            if (!string.Equals(CatalogFetcher.HashFile(bootstrapDest), bootstrapSha, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(CatalogFetcher.HashFile(coreDest), coreSha, StringComparison.OrdinalIgnoreCase))
            {
                RollBack(written);
                return ServerInstallResult.Refuse(RefusalCopyMismatch);
            }

            Directory.CreateDirectory(ServerInstallLedger.Absolute(root, PendingRelative));
            Directory.CreateDirectory(ServerInstallLedger.Absolute(root, ReloadRelative));
        }
        catch (Exception e)
        {
            RollBack(written);
            return ServerInstallResult.Refuse(RefusalUnwritable + " " + e.Message);
        }

        var previous = status.Ledger;
        var entries = new List<ServerInstallLedgerEntry>();
        if (previous != null)
        {
            for (int i = 0; i < previous.Files.Count; i++)
            {
                if (!string.Equals(previous.Files[i].Category, ServerInstallLedger.CategoryFramework, StringComparison.Ordinal))
                    entries.Add(previous.Files[i]);
            }
        }

        entries.Add(new ServerInstallLedgerEntry(BootstrapRelative, bootstrapSha, bootstrapBytes.LongLength, ServerInstallLedger.CategoryFramework, ServerInstallLedger.ClientImageSourcePrefix + bootstrapSha, now));
        entries.Add(new ServerInstallLedgerEntry(CoreRelative, coreSha, coreBytes.LongLength, ServerInstallLedger.CategoryFramework, ServerInstallLedger.ClientImageSourcePrefix + coreSha, now));
        var ledger = new ServerInstallLedger(source.Version, bootstrapSha, coreSha, now, entries);
        try
        {
            ledger.Write(root);
        }
        catch (Exception e)
        {
            RollBack(written);
            return ServerInstallResult.Refuse(RefusalUnwritable + " " + e.Message);
        }

        var rel = new List<string> { BootstrapRelative, CoreRelative };
        return new ServerInstallResult(true, InstalledStatus(source.Version), rel, Array.Empty<string>());
    }

    /// <summary>
    /// Remove ledger <c>mod</c> and <c>pending</c> files (and a promoted copy of the same bytes
    /// under <c>progenitor-reload/</c>), then <c>framework</c> files, then empty Progenitor
    /// folders, then the ledger. A file whose bytes changed is reported and kept; a file that
    /// is not in the ledger is never touched.
    /// </summary>
    public static ServerInstallResult Uninstall(string? serverRoot)
    {
        if (string.IsNullOrWhiteSpace(serverRoot) || !Directory.Exists(serverRoot))
            return ServerInstallResult.Refuse(RefusalNoFolder);
        string root = serverRoot!;
        string? running = FindRunningServer(root);
        if (running != null)
            return ServerInstallResult.Refuse(RunningRefusal(running));

        var ledger = ServerInstallLedger.TryRead(root);
        if (ledger == null)
            return new ServerInstallResult(true, NotInstalledStatus, Array.Empty<string>(), Array.Empty<string>(), noOp: true);

        var removed = new List<string>();
        var skipped = new List<string>();
        RemoveCategory(root, ledger, ServerInstallLedger.CategoryMod, removed, skipped);
        RemoveCategory(root, ledger, ServerInstallLedger.CategoryPending, removed, skipped);
        RemoveCategory(root, ledger, ServerInstallLedger.CategoryFramework, removed, skipped);

        for (int i = 0; i < OwnedFolders.Length; i++)
            RemoveEmptyTree(ServerInstallLedger.Absolute(root, OwnedFolders[i]));

        if (skipped.Count == 0)
        {
            InstallStaging.TryDelete(ServerInstallLedger.PathFor(root));
        }
        else
        {
            // Keep only the entries the user changed so a later removal can still name them.
            var keep = new List<ServerInstallLedgerEntry>();
            for (int i = 0; i < ledger.Files.Count; i++)
            {
                if (skipped.Contains(ledger.Files[i].Path)) keep.Add(ledger.Files[i]);
            }

            try { new ServerInstallLedger(ledger.Version, "", "", ledger.InstalledUtc, keep).Write(root); }
            catch { /* the ledger is best-effort after a partial removal */ }
        }

        string message = skipped.Count == 0
            ? "Progenitor removed from the server (" + removed.Count + " files) " + RestartNote
            : "Progenitor removed (" + removed.Count + " files); left in place because they were changed: " + string.Join(", ", skipped);
        return new ServerInstallResult(true, message, removed, skipped);
    }

    /// <summary>
    /// <see cref="InstallStaging"/> hook: a pending file written under a server root that
    /// carries a ledger is appended as <c>pending</c>. A root without a ledger (the client)
    /// is left alone.
    /// </summary>
    public static bool TryAppendPending(
        string bepInExRoot, string pendingFile, string sha256, long size, string source, string? pluginId = null, string? origin = null)
    {
        try
        {
            string bep = Path.GetFullPath(bepInExRoot);
            string? serverRoot = Path.GetDirectoryName(bep.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrEmpty(serverRoot)) return false;
            var ledger = ServerInstallLedger.TryRead(serverRoot);
            if (ledger == null) return false;
            string rel = ServerInstallLedger.Relative(serverRoot, Path.GetFullPath(pendingFile));
            if (rel.StartsWith("..", StringComparison.Ordinal)) return false;
            ledger.With(new ServerInstallLedgerEntry(
                rel, sha256, size, ServerInstallLedger.CategoryPending, source, Now(),
                uninstallPending: false, hot: false, pluginId: pluginId, origin: origin)).Write(serverRoot);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// INSTORD1: the game root that owns a <c>BepInEx</c> folder. The ledger lives at
    /// <c>&lt;game root&gt;/BepInEx/progenitor-install.json</c> on the client exactly as it does on
    /// the server, so one type records both sides.
    /// </summary>
    public static string? GameRootOf(string bepInExRoot)
    {
        try
        {
            string bep = Path.GetFullPath(bepInExRoot);
            string? root = Path.GetDirectoryName(bep.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            return string.IsNullOrEmpty(root) ? null : root;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// INSTORD1: record one file the ordinary-package route wrote under
    /// <paramref name="bepInExRoot"/>. Unlike <see cref="TryAppendPending"/> this creates the
    /// ledger when the root does not carry one yet, which is the normal case on the client: the
    /// client has no framework install to have written one. Only files under the root are recorded.
    /// </summary>
    public static bool TryAppendInstalledFile(
        string bepInExRoot,
        string absoluteFile,
        string sha256,
        long size,
        string source,
        string category,
        bool hot = false,
        string? pluginId = null,
        bool sweepHotFlags = true,
        string? origin = null)
    {
        try
        {
            string? root = GameRootOf(bepInExRoot);
            if (string.IsNullOrEmpty(root)) return false;
            string rel = ServerInstallLedger.Relative(root, Path.GetFullPath(absoluteFile));
            if (rel.StartsWith("..", StringComparison.Ordinal)) return false;
            // INSTALL2 defect A: any hot flag already on disk belongs to a process that has ended, so
            // it is cleared before this one writes its own.
            if (sweepHotFlags) ClearHotFlags(bepInExRoot);
            var ledger = ServerInstallLedger.TryRead(root)
                ?? new ServerInstallLedger("", "", "", Now());
            ledger
                .With(new ServerInstallLedgerEntry(
                    rel, sha256, size, category, source, Now(),
                    uninstallPending: false, hot: hot, pluginId: pluginId, origin: origin))
                .Write(root);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// INSTALL2 defect A: drop every <c>hot</c> flag this root carries, once per process. The flag
    /// means "the process that wrote it hot loaded this file and can hot unload it"; a flag found by a
    /// <em>new</em> process was written by one that has ended, so the file is an ordinary loaded mod
    /// and its removal owes a restart. Called before the first read of the flags and before the
    /// executor writes one, so the ordering cannot leave a stale flag readable.
    ///
    /// <para>The live defect this closes: CryptSnooze — an ordinary package installed through the
    /// restart route and promoted at a restart — read <c>Installed · new</c> and its uninstall deleted
    /// the loaded file at once, because the hot decision came from the reloader's report and the
    /// reloader loads every DLL in its folder.</para>
    /// </summary>
    public static void ClearHotFlags(string bepInExRoot)
    {
        string? root;
        try { root = GameRootOf(bepInExRoot); }
        catch { return; }
        if (string.IsNullOrEmpty(root)) return;
        lock (HotSwept)
        {
            if (!HotSwept.Add(root!)) return;
        }

        try
        {
            var ledger = ServerInstallLedger.TryRead(root!);
            if (ledger == null) return;
            var keep = new List<ServerInstallLedgerEntry>(ledger.Files.Count);
            bool changed = false;
            for (int i = 0; i < ledger.Files.Count; i++)
            {
                var entry = ledger.Files[i];
                if (!entry.Hot)
                {
                    keep.Add(entry);
                    continue;
                }

                changed = true;
                keep.Add(new ServerInstallLedgerEntry(
                    entry.Path, entry.Sha256, entry.Size, entry.Category, entry.Source, entry.WrittenUtc,
                    entry.UninstallPending, hot: false, pluginId: entry.PluginId, route: entry.Route, origin: entry.Origin));
            }

            if (!changed) return;
            new ServerInstallLedger(ledger.Version, ledger.BootstrapSha256, ledger.CoreSha256, ledger.InstalledUtc, keep)
                .Write(root!);
        }
        catch
        {
            // A ledger we cannot rewrite costs the tag, never a wrong removal: a flag we could not
            // clear is still only read together with this process's own session record.
        }
    }

    /// <summary>Roots whose stale hot flags this process has already cleared.</summary>
    private static readonly HashSet<string> HotSwept = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Tests only: forget which roots were swept, so a fresh temp root starts clean.</summary>
    internal static void ResetHotSweepForTests()
    {
        lock (HotSwept) HotSwept.Clear();
    }

    /// <summary>
    /// INSTALL2 defect A: the ledger sources under this root whose file this process hot loaded.
    /// Stale flags are cleared first, so on a fresh process this is empty until the executor writes.
    /// </summary>
    public static IReadOnlyList<string> HotSources(string bepInExRoot)
    {
        var list = new List<string>();
        ClearHotFlags(bepInExRoot);
        string? root;
        try { root = GameRootOf(bepInExRoot); }
        catch { return list; }
        if (string.IsNullOrEmpty(root)) return list;
        ServerInstallLedger? ledger;
        try { ledger = ServerInstallLedger.TryRead(root!); }
        catch { return list; }
        if (ledger == null) return list;
        for (int i = 0; i < ledger.Files.Count; i++)
        {
            var entry = ledger.Files[i];
            if (entry.Hot && entry.Source.Length > 0 && !list.Contains(entry.Source)) list.Add(entry.Source);
        }

        return list;
    }

    /// <summary>
    /// INSTORD2: bring the install ledger back in step with what Bootstrap did to the files.
    /// Bootstrap promotes a pending DLL with <c>File.Move</c>, so after a restart a <c>pending</c>
    /// row names a file that is gone. A row whose bytes are now a DLL under
    /// <c>progenitor-reload/</c> is rewritten to that path as category <c>mod</c> (same source, sha
    /// and size, new timestamp); a row with no reload copy of those bytes is dropped as
    /// promote-lost. <c>framework</c> and <c>mod</c> rows and every row whose file is still there
    /// are left untouched, and the ledger is only rewritten when something changed.
    /// </summary>
    public static LedgerReconcileResult ReconcilePromoted(string? bepInExRoot)
    {
        var rewritten = new List<string>();
        var dropped = new List<string>();
        string? root = null;
        try { root = GameRootOf(bepInExRoot ?? ""); }
        catch { root = null; }
        if (string.IsNullOrEmpty(root)) return new LedgerReconcileResult(rewritten, dropped);

        ServerInstallLedger? ledger;
        try { ledger = ServerInstallLedger.TryRead(root!); }
        catch { ledger = null; }
        if (ledger == null) return new LedgerReconcileResult(rewritten, dropped);

        var keep = new List<ServerInstallLedgerEntry>();
        bool changed = false;
        for (int i = 0; i < ledger.Files.Count; i++)
        {
            var entry = ledger.Files[i];
            if (!string.Equals(entry.Category, ServerInstallLedger.CategoryPending, StringComparison.Ordinal))
            {
                // INSTALL2 item 4: a row Bootstrap's RemoveMarkedAtStart has honored — flagged
                // uninstallPending and its file gone — is a removal that has been carried out.
                // Bootstrap cannot read this ledger, so dropping the row is this pass's job. A
                // flagged row whose file is still there is still only a request and stays.
                if (entry.UninstallPending && !FileStillThere(root!, entry))
                {
                    dropped.Add(entry.Path);
                    changed = true;
                    continue;
                }

                keep.Add(entry);
                continue;
            }

            string abs;
            try { abs = ServerInstallLedger.Absolute(root!, entry.Path); }
            catch { keep.Add(entry); continue; }
            if (File.Exists(abs))
            {
                keep.Add(entry);
                continue;
            }

            string? promoted = FindPromotedCopy(root!, entry);
            if (promoted == null)
            {
                dropped.Add(entry.Path);
                changed = true;
                continue;
            }

            string rel = ServerInstallLedger.Relative(root!, promoted);
            // POLISH4 item 1: the promoted row keeps the pending row's `pluginId`. Dropping it here
            // would leave a promoted install resolvable only through the identity cache again.
            keep.Add(new ServerInstallLedgerEntry(
                rel, entry.Sha256, entry.Size, ServerInstallLedger.CategoryMod, entry.Source, Now(),
                uninstallPending: false, hot: false, pluginId: entry.PluginId, route: entry.Route, origin: entry.Origin));
            rewritten.Add(rel);
            changed = true;
        }

        // REQRULE4: an install record superseded by a newer one of the same package whose file is there.
        var superseded = SupersededOrphans(root!, keep);
        if (superseded.Count > 0)
        {
            keep.RemoveAll(superseded.Contains);
            foreach (var entry in superseded)
            {
                dropped.Add(entry.Path);
                InstallLog.Info("Install: dropped the stale install record " + entry.Path + " (" + entry.Source
                    + "): its file is gone and a newer record of the same package names a file that is there. Nothing on disk was touched.");
            }

            changed = true;
        }

        if (!changed) return new LedgerReconcileResult(rewritten, dropped);

        try
        {
            new ServerInstallLedger(ledger.Version, ledger.BootstrapSha256, ledger.CoreSha256, ledger.InstalledUtc, keep)
                .Write(root!);
        }
        catch
        {
            // A ledger we cannot rewrite keeps its pending rows; the next open tries again.
            return new LedgerReconcileResult(Array.Empty<string>(), Array.Empty<string>());
        }

        LogReconcile(root!, rewritten, dropped);
        return new LedgerReconcileResult(rewritten, dropped);
    }

    /// <summary>
    /// SRVUNINST1 (live 2026-10-07: a test server's ledger still named
    /// an old HotDemo DLL name in <c>progenitor-reload</c> after the file was gone, and both tabs
    /// read the mod as installed there): an installed (<c>mod</c>) package row whose file is no longer
    /// on disk is only an install record. Pending rows are <see cref="ReconcilePromoted"/>'s, and a row
    /// already marked for removal is dropped there too, so neither is an orphan here. A path that
    /// cannot be resolved counts as present, and so does a copy turned off on disk (<c>.old</c>).
    /// </summary>
    public static bool IsOrphanRow(string gameRoot, ServerInstallLedgerEntry entry)
    {
        if (entry == null || string.IsNullOrEmpty(gameRoot)) return false;
        if (!string.Equals(entry.Category, ServerInstallLedger.CategoryMod, StringComparison.Ordinal)) return false;
        if (entry.UninstallPending) return false;
        if (!InstalledState.TrySplitPackageSource(entry.Source, out _, out _)) return false;
        try
        {
            string abs = ServerInstallLedger.Absolute(gameRoot, entry.Path);
            return !File.Exists(abs) && !File.Exists(abs + ".old");
        }
        catch { return false; }
    }

    /// <summary>
    /// SRVUNINST1: drop <paramref name="package"/>'s orphan rows (<see cref="IsOrphanRow"/>) from the
    /// ledger. Nothing on disk is touched — there is nothing left to delete. The result lists the
    /// dropped rows' paths; NoOp when there were none.
    /// </summary>
    public static ServerInstallResult DropOrphanRows(string? bepInExRoot, string package)
    {
        string? root = null;
        try { root = GameRootOf(bepInExRoot ?? ""); }
        catch { root = null; }
        if (string.IsNullOrEmpty(root)) return ServerInstallResult.Refuse("The server folder is not readable.");
        ServerInstallLedger? ledger;
        try { ledger = ServerInstallLedger.TryRead(root!); }
        catch { ledger = null; }
        if (ledger == null) return new ServerInstallResult(true, "", Array.Empty<string>(), Array.Empty<string>(), noOp: true, hotUnloaded: false);

        var keep = new List<ServerInstallLedgerEntry>();
        var dropped = new List<string>();
        foreach (var entry in ledger.Files)
        {
            bool mine = InstalledState.TrySplitPackageSource(entry.Source, out string p, out _)
                && string.Equals(p, package, StringComparison.OrdinalIgnoreCase);
            if (mine && IsOrphanRow(root!, entry)) dropped.Add(entry.Path);
            else keep.Add(entry);
        }

        if (dropped.Count == 0) return new ServerInstallResult(true, "", Array.Empty<string>(), Array.Empty<string>(), noOp: true, hotUnloaded: false);
        try
        {
            new ServerInstallLedger(ledger.Version, ledger.BootstrapSha256, ledger.CoreSha256, ledger.InstalledUtc, keep).Write(root!);
        }
        catch (Exception e) { return ServerInstallResult.Refuse("The install record could not be rewritten: " + e.Message); }

        InstallLog.Info("Install: dropped the install record of " + package + " (its files were already gone): " + string.Join(", ", dropped) + ".");
        return new ServerInstallResult(true, "", dropped, Array.Empty<string>(), noOp: false, hotUnloaded: false);
    }

    /// <summary>
    /// REQRULE4 (live 2026-10-08: the test server's ledger still listed a HotDemo DLL under the plugin id it had before the
    /// rename, the file long gone, beside the current row): the orphan rows (<see cref="IsOrphanRow"/>) that a newer row of
    /// the same package supersedes, one written at the same time or later that names a file still on disk. Those are dropped
    /// by every reconcile. An orphan with no such sibling (a package whose only record lost its file) stays: dropping it
    /// would silently un-ledger an install some other tool broke, and the uninstall still clears it
    /// (<see cref="DropOrphanRows"/>). Only ledger rows are dropped; no file is touched.
    /// </summary>
    /// <remarks>
    /// F38 (live 2026-10-08: the rule above did not fire on the test server): the pre-rename row was
    /// <c>package:OldOwner-HotDemo@0.1.0</c> and the current one <c>package:Progenitor-HotDemo@0.1.0</c> — the owner was
    /// renamed with the plugin id. A newer row with the same package <em>name</em> (the part after the owner) supersedes
    /// the orphan too: the orphan names a file that is gone, so dropping its record loses nothing on disk.
    /// </remarks>
    internal static List<ServerInstallLedgerEntry> SupersededOrphans(string gameRoot, IReadOnlyList<ServerInstallLedgerEntry> rows)
    {
        var list = new List<ServerInstallLedgerEntry>();
        if (rows == null || string.IsNullOrEmpty(gameRoot)) return list;
        for (int i = 0; i < rows.Count; i++)
        {
            var orphan = rows[i];
            if (!IsOrphanRow(gameRoot, orphan) || !InstalledState.TrySplitPackageSource(orphan.Source, out string package, out _)) continue;
            for (int j = 0; j < rows.Count; j++)
            {
                var live = rows[j];
                if (j == i || live.UninstallPending || live.WrittenUtc < orphan.WrittenUtc) continue;
                if (string.Equals(live.Path, orphan.Path, StringComparison.OrdinalIgnoreCase)) continue;
                if (!InstalledState.TrySplitPackageSource(live.Source, out string p, out _) || !SamePackageName(p, package)) continue;
                bool there;
                try { there = File.Exists(ServerInstallLedger.Absolute(gameRoot, live.Path)); }
                catch { there = false; }
                if (!there) continue;
                list.Add(orphan);
                break;
            }
        }

        return list;
    }

    /// <summary>F38: the same listing key, or the same package name under another owner (<c>OldOwner-HotDemo</c> / <c>Progenitor-HotDemo</c>).</summary>
    internal static bool SamePackageName(string a, string b)
    {
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;
        static string Name(string key)
        {
            int dash = (key ?? "").IndexOf('-');
            return dash > 0 && dash < key!.Length - 1 ? key.Substring(dash + 1) : "";
        }

        string na = Name(a), nb = Name(b);
        return na.Length > 0 && string.Equals(na, nb, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Is the file a ledger row names still on disk? An unreadable path counts as still there.</summary>
    private static bool FileStillThere(string root, ServerInstallLedgerEntry entry)
    {
        try { return File.Exists(ServerInstallLedger.Absolute(root, entry.Path)); }
        catch { return true; }
    }

    /// <summary>The DLL under <c>progenitor-reload/</c> with this row's bytes: the same leaf first, then any.</summary>
    private static string? FindPromotedCopy(string root, ServerInstallLedgerEntry entry)
    {
        string reload;
        try { reload = ServerInstallLedger.Absolute(root, ReloadRelative); }
        catch { return null; }
        if (!Directory.Exists(reload)) return null;
        string name = Path.GetFileName(entry.Path);
        string[] dlls;
        try { dlls = Directory.GetFiles(reload, "*.dll", SearchOption.TopDirectoryOnly); }
        catch { return null; }
        string? any = null;
        for (int i = 0; i < dlls.Length; i++)
        {
            if (!HashMatches(dlls[i], entry.Sha256)) continue;
            if (string.Equals(Path.GetFileName(dlls[i]), name, StringComparison.OrdinalIgnoreCase)) return dlls[i];
            any ??= dlls[i];
        }

        return any;
    }

    private static readonly HashSet<string> ReconcileLogged = new(StringComparer.OrdinalIgnoreCase);

    private static void LogReconcile(string root, List<string> rewritten, List<string> dropped)
    {
        for (int i = 0; i < rewritten.Count; i++)
        {
            string key = root + "|mod|" + rewritten[i];
            lock (ReconcileLogged) { if (!ReconcileLogged.Add(key)) continue; }
            Plugin.Logger?.LogInfo("Install ledger: promoted row now names " + rewritten[i] + " (mod)");
        }

        for (int i = 0; i < dropped.Count; i++)
        {
            string key = root + "|lost|" + dropped[i];
            lock (ReconcileLogged) { if (!ReconcileLogged.Add(key)) continue; }
            Plugin.Logger?.LogWarning("Install ledger: dropped " + dropped[i] + " (promote-lost: no reload copy of those bytes)");
        }
    }

    /// <summary>Every ledger <c>source</c> recorded under this root, for the dependency check.</summary>
    public static IReadOnlyList<string> LedgerSources(string bepInExRoot)
    {
        var list = new List<string>();
        string? root = GameRootOf(bepInExRoot);
        if (string.IsNullOrEmpty(root)) return list;
        var ledger = ServerInstallLedger.TryRead(root);
        if (ledger == null) return list;
        for (int i = 0; i < ledger.Files.Count; i++)
        {
            string s = ledger.Files[i].Source;
            if (s.Length > 0 && !list.Contains(s)) list.Add(s);
        }

        return list;
    }

    /// <summary>
    /// INSTORD1 Remove: delete the ledgered <c>pending</c> and <c>mod</c> files whose
    /// <c>source</c> names <paramref name="source"/> — that package and nothing else — plus the
    /// promoted copy of the same bytes under <c>progenitor-reload/</c>, and drop those rows. A file
    /// whose bytes changed is reported and kept; a file the ledger does not name is never touched.
    /// The framework's own rows are left alone.
    /// </summary>
    public static ServerInstallResult RemovePackage(string bepInExRoot, string source)
    {
        string? root = GameRootOf(bepInExRoot);
        if (string.IsNullOrEmpty(root))
            return ServerInstallResult.Refuse(RefusalNoFolder);
        if (string.IsNullOrWhiteSpace(source))
            return ServerInstallResult.Refuse(NotInstalledStatus);

        var ledger = ServerInstallLedger.TryRead(root);
        if (ledger == null)
            return new ServerInstallResult(true, NotInstalledStatus, Array.Empty<string>(), Array.Empty<string>(), noOp: true);

        var removed = new List<string>();
        var skipped = new List<string>();
        var keep = new List<ServerInstallLedgerEntry>();
        bool matchedAny = false;

        for (int i = 0; i < ledger.Files.Count; i++)
        {
            var entry = ledger.Files[i];
            bool mine = SourceNamesSamePackage(entry.Source, source)
                && (string.Equals(entry.Category, ServerInstallLedger.CategoryPending, StringComparison.Ordinal)
                    || string.Equals(entry.Category, ServerInstallLedger.CategoryMod, StringComparison.Ordinal));
            if (!mine)
            {
                keep.Add(entry);
                continue;
            }

            matchedAny = true;
            string abs = ServerInstallLedger.Absolute(root, entry.Path);
            // L9: only inside the root's BepInEx folder, never elsewhere under the game root.
            if (!IsUnderBepInEx(root, abs))
            {
                skipped.Add(entry.Path);
                keep.Add(entry);
                continue;
            }

            if (File.Exists(abs))
            {
                if (!HashMatches(abs, entry.Sha256))
                {
                    skipped.Add(entry.Path);
                    keep.Add(entry);
                    continue;
                }

                InstallStaging.TryDelete(abs);
                if (File.Exists(abs))
                {
                    skipped.Add(entry.Path);
                    keep.Add(entry);
                    continue;
                }
            }

            int firstRemoved = removed.Count;
            removed.Add(entry.Path);
            RemovePromotedCopies(root, entry, removed);
            // This is an explicit successful uninstall, never a watcher unload or update.
            if (entry.Category == ServerInstallLedger.CategoryMod)
            {
                string contentRoot = Path.Combine(root, "BepInEx");
                for (int r = firstRemoved; r < removed.Count; r++)
                    if (!Progenitor.Content.ItemOwnerLedger.RequestRemovalForFile(contentRoot, ServerInstallLedger.Absolute(root, removed[r])))
                        skipped.Add(removed[r] + " (item cleanup could not be recorded)");
            }
        }

        if (!matchedAny)
            return new ServerInstallResult(true, NotInstalledStatus, Array.Empty<string>(), Array.Empty<string>(), noOp: true);

        try { new ServerInstallLedger(ledger.Version, ledger.BootstrapSha256, ledger.CoreSha256, ledger.InstalledUtc, keep).Write(root); }
        catch { /* the ledger is best-effort after a partial removal */ }

        string message = skipped.Count == 0
            ? "Removed " + removed.Count + " files for " + source + " " + RestartNote
            : "Removed " + removed.Count + " files for " + source + "; left in place because they were changed: " + string.Join(", ", skipped);
        return new ServerInstallResult(true, message, removed, skipped);
    }

    /// <summary>
    /// Review 2026-10-07 (M4): cancel a written-but-not-loaded copy and nothing else: delete the <c>pending</c> rows whose
    /// source is exactly <paramref name="source"/> (this version), and drop those rows. A loaded copy of the same package
    /// (another version, or a promoted copy) is never touched, unlike <see cref="RemovePackage"/>.
    /// </summary>
    public static ServerInstallResult RemovePendingOnly(string bepInExRoot, string source)
    {
        string? root = GameRootOf(bepInExRoot);
        if (string.IsNullOrEmpty(root)) return ServerInstallResult.Refuse(RefusalNoFolder);
        if (string.IsNullOrWhiteSpace(source)) return ServerInstallResult.Refuse(NotInstalledStatus);
        var ledger = ServerInstallLedger.TryRead(root);
        if (ledger == null) return new ServerInstallResult(true, NotInstalledStatus, Array.Empty<string>(), Array.Empty<string>(), noOp: true);
        var removed = new List<string>();
        var skipped = new List<string>();
        var keep = new List<ServerInstallLedgerEntry>();
        bool matched = false;
        foreach (var entry in ledger.Files)
        {
            bool mine = string.Equals(entry.Category, ServerInstallLedger.CategoryPending, StringComparison.Ordinal)
                && string.Equals((entry.Source ?? "").Trim(), source.Trim(), StringComparison.OrdinalIgnoreCase);
            if (!mine)
            {
                keep.Add(entry);
                continue;
            }

            matched = true;
            string abs = ServerInstallLedger.Absolute(root, entry.Path);
            if (!IsUnderBepInEx(root, abs) || (File.Exists(abs) && !HashMatches(abs, entry.Sha256)))
            {
                skipped.Add(entry.Path);
                keep.Add(entry);
                continue;
            }

            if (File.Exists(abs)) InstallStaging.TryDelete(abs);
            if (File.Exists(abs))
            {
                skipped.Add(entry.Path);
                keep.Add(entry);
                continue;
            }

            removed.Add(entry.Path);
        }

        if (!matched) return new ServerInstallResult(true, NotInstalledStatus, Array.Empty<string>(), Array.Empty<string>(), noOp: true);
        try { new ServerInstallLedger(ledger.Version, ledger.BootstrapSha256, ledger.CoreSha256, ledger.InstalledUtc, keep).Write(root); }
        catch { /* the ledger is best-effort after a partial removal */ }
        return new ServerInstallResult(true, skipped.Count == 0 ? "Cancelled the download of " + source + "."
            : "Cancelled " + removed.Count + " files of " + source + "; left in place because they were changed: " + string.Join(", ", skipped), removed, skipped);
    }

    /// <summary>Review 2026-10-07 (L9): a ledgered path a removal may delete lies inside the root's BepInEx folder.</summary>
    internal static bool IsUnderBepInEx(string gameRoot, string abs) =>
        Networking.ModManifestIds.IsUnder(Path.Combine(gameRoot, LocalServerFolder.BepInExFolderName), abs);

    /// <summary>
    /// INSTORD2: the same row source, or the same <c>package:Ns-Name</c> at another version. The row
    /// offers Remove whenever the ledger names the package at any version
    /// (<see cref="OrdinaryDependencies.LedgerHas"/>), so removal has to reach that row too;
    /// matching the version exactly made Remove a no-op after the listing moved on.
    /// </summary>
    internal static bool SourceNamesSamePackage(string? rowSource, string? source)
    {
        if (string.IsNullOrWhiteSpace(rowSource) || string.IsNullOrWhiteSpace(source)) return false;
        if (string.Equals(rowSource.Trim(), source.Trim(), StringComparison.OrdinalIgnoreCase)) return true;
        if (!InstalledState.TrySplitPackageSource(rowSource, out string a, out _)) return false;
        if (!InstalledState.TrySplitPackageSource(source, out string b, out _)) return false;
        return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    internal static bool HasBepInExCore(string serverRoot)
    {
        string core = Path.Combine(serverRoot, LocalServerFolder.BepInExFolderName, "core");
        if (!Directory.Exists(core)) return false;
        try { return Directory.GetFiles(core, "BepInEx.*.dll", SearchOption.TopDirectoryOnly).Length > 0; }
        catch { return false; }
    }

    private static void RemoveCategory(string root, ServerInstallLedger ledger, string category, List<string> removed, List<string> skipped)
    {
        var entries = ledger.OfCategory(category);
        for (int i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            string abs = ServerInstallLedger.Absolute(root, entry.Path);
            if (!Networking.ModManifestIds.IsUnder(root, abs))
            {
                skipped.Add(entry.Path);
                continue;
            }

            if (File.Exists(abs))
            {
                if (!HashMatches(abs, entry.Sha256))
                {
                    skipped.Add(entry.Path);
                    continue;
                }

                InstallStaging.TryDelete(abs);
                if (File.Exists(abs)) { skipped.Add(entry.Path); continue; }
            }

            removed.Add(entry.Path);
            if (string.Equals(category, ServerInstallLedger.CategoryPending, StringComparison.Ordinal))
                RemovePromotedCopies(root, entry, removed);
            // This is an explicit successful uninstall, never a watcher unload or update.
            if (entry.Category == ServerInstallLedger.CategoryMod)
            {
                string contentRoot = Path.Combine(root, "BepInEx");
                foreach (var deleted in removed)
                    if (!Progenitor.Content.ItemOwnerLedger.RequestRemovalForFile(contentRoot, ServerInstallLedger.Absolute(root, deleted)))
                        skipped.Add(deleted + " (item cleanup could not be recorded)");
            }
        }
    }

    /// <summary>Bootstrap promotes pending files into <c>progenitor-reload/</c> unchanged: the same bytes there are Progenitor's too.</summary>
    private static void RemovePromotedCopies(string root, ServerInstallLedgerEntry entry, List<string> removed)
    {
        string reload = ServerInstallLedger.Absolute(root, ReloadRelative);
        if (!Directory.Exists(reload)) return;
        string name = Path.GetFileName(entry.Path);
        string[] candidates;
        try { candidates = Directory.GetFiles(reload, name, SearchOption.AllDirectories); }
        catch { return; }
        for (int i = 0; i < candidates.Length; i++)
        {
            if (!HashMatches(candidates[i], entry.Sha256)) continue;
            InstallStaging.TryDelete(candidates[i]);
            if (!File.Exists(candidates[i]))
                removed.Add(ServerInstallLedger.Relative(root, candidates[i]));
        }
    }

    private static bool HashMatches(string path, string sha)
    {
        try { return string.Equals(CatalogFetcher.HashFile(path), sha, StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    private static void RemoveEmptyTree(string dir)
    {
        if (!Directory.Exists(dir)) return;
        try
        {
            foreach (string sub in Directory.GetDirectories(dir))
                RemoveEmptyTree(sub);
            if (Directory.GetFileSystemEntries(dir).Length == 0)
                Directory.Delete(dir);
        }
        catch { /* not ours to force */ }
    }

    private static void RollBack(List<string> written)
    {
        for (int i = 0; i < written.Count; i++) InstallStaging.TryDelete(written[i]);
    }

    private static DateTime Now() => ClockUtc?.Invoke() ?? DateTime.UtcNow;

    private static long FreeSpace(string root)
    {
        if (FreeSpaceOverride != null) return FreeSpaceOverride(root);
        try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(root)) ?? root).AvailableFreeSpace; }
        catch { return long.MaxValue; }
    }

    private static bool HasWriteAccess(string directory)
    {
        if (WriteAccessOverride != null) return WriteAccessOverride(directory);
        string probe = Path.Combine(directory, LocalServerFolder.WriteProbeName);
        try
        {
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return true;
        }
        catch
        {
            InstallStaging.TryDelete(probe);
            return false;
        }
    }

    /// <summary>Full path of a <c>VRisingServer.exe</c> whose image lives inside <paramref name="serverRoot"/>, else null. Never signals the process.</summary>
    internal static string? FindRunningServer(string serverRoot)
    {
        if (RunningServerProbe != null) return RunningServerProbe(serverRoot);
        // Review 2026-10-07: never a process scan on the main thread; the frame reads the cached answer (refreshed in the
        // background by ServerProcessControl.Running).
        if (ServerProcessControl.OnMainThread()) return ServerProcessControl.Running(serverRoot)?.Path;
        return FindRunningServerNow(serverRoot);
    }

    /// <summary>
    /// Review M-b: the scan, saying whether it could be trusted. False on a scan error or when a VRisingServer's image
    /// path could not be read (that one might be this folder's); then <paramref name="path"/> is null and means nothing.
    /// </summary>
    internal static bool TryFindRunningServerNow(string serverRoot, out string? path)
    {
        path = null;
        if (RunningServerProbe != null)
        {
            path = RunningServerProbe(serverRoot);
            return true;
        }

        try
        {
            string canonRoot = LocalServerFolder.Canonicalize(serverRoot);
            bool unreadable = false;
            foreach (var proc in Process.GetProcessesByName("VRisingServer"))
            {
                try
                {
                    string p = ExecutablePath(proc);
                    if (p.Length == 0)
                    {
                        unreadable = true;
                        continue;
                    }

                    string canonPath = LocalServerFolder.Canonicalize(p);
                    if (canonPath == LocalServerFolder.UnrecognisedSentinel) canonPath = p;
                    if (LocalServerFolder.IsUnderOrEqual(canonPath, canonRoot))
                    {
                        path = p;
                        return true;
                    }
                }
                finally
                {
                    try { proc.Dispose(); } catch { /* */ }
                }
            }

            return !unreadable;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>The scan itself (off the main thread, or for an explicit one-shot action such as Force stop).</summary>
    internal static string? FindRunningServerNow(string serverRoot)
    {
        if (RunningServerProbe != null) return RunningServerProbe(serverRoot);
        try
        {
            string canonRoot = LocalServerFolder.Canonicalize(serverRoot);
            foreach (var proc in Process.GetProcessesByName("VRisingServer"))
            {
                try
                {
                    string path = ExecutablePath(proc);
                    if (path.Length == 0) continue;
                    string canonPath = LocalServerFolder.Canonicalize(path);
                    if (canonPath == LocalServerFolder.UnrecognisedSentinel) canonPath = path;
                    if (LocalServerFolder.IsUnderOrEqual(canonPath, canonRoot))
                        return path;
                }
                finally
                {
                    try { proc.Dispose(); } catch { /* */ }
                }
            }
        }
        catch { /* a failed scan is not proof the server is stopped; callers treat null as "none seen" */ }

        return null;
    }

    internal static string ExecutablePath(Process proc)
    {
        IntPtr handle = IntPtr.Zero;
        try
        {
            handle = OpenProcess(0x1000, false, proc.Id);
            if (handle != IntPtr.Zero)
            {
                var sb = new StringBuilder(1024);
                int size = sb.Capacity;
                if (QueryFullProcessImageName(handle, 0, sb, ref size) && size > 0)
                    return sb.ToString();
            }
        }
        catch { /* fall through */ }
        finally
        {
            if (handle != IntPtr.Zero) { try { CloseHandle(handle); } catch { /* */ } }
        }

        try { return proc.MainModule?.FileName ?? ""; }
        catch { return ""; }
    }

    /// <summary>
    /// Job SERVERINST4. The target's command line and working directory, read out of its own PEB.
    /// This is how <c>Start server</c> can replay a launch it did not perform: without the argument
    /// vector the launch record is an exe and nothing else, and a dedicated server started with
    /// <c>-persistentDataPath</c> / <c>-saveName</c> replayed bare would load a different world.
    ///
    /// <para><b>The walk.</b> <c>NtQueryInformationProcess(ProcessBasicInformation)</c> gives
    /// <c>PebBaseAddress</c>; <c>PEB.ProcessParameters</c> gives
    /// <c>RTL_USER_PROCESS_PARAMETERS</c>; its <c>CommandLine</c> and
    /// <c>CurrentDirectory.DosPath</c> are <c>UNICODE_STRING</c>s whose buffers are one more
    /// <c>ReadProcessMemory</c> each. Every offset is x64 and
    /// <see cref="ServerProcessControl.TryDecodeUnicodeString"/> owns the decoding, under test
    /// against a byte fixture.</para>
    ///
    /// <para><b>Read-only, and refused rather than guessed.</b> The handle is
    /// <c>PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_VM_READ</c> — no write, no terminate, no
    /// suspend — and nothing here signals the process. A 32-bit target, a missing ntdll export, a
    /// refused handle, a short read or a torn structure all return false, and the caller then keeps
    /// an argument-less record rather than inventing one.</para>
    /// </summary>
    internal static bool TryReadProcessCommandLine(int pid, out string commandLine, out string workingDirectory)
    {
        commandLine = "";
        workingDirectory = "";
        if (pid <= 0) return false;
        if (!Environment.Is64BitProcess) return false;

        IntPtr handle = IntPtr.Zero;
        try
        {
            // PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_VM_READ.
            handle = OpenProcess(0x1000 | 0x0010, false, pid);
            if (handle == IntPtr.Zero) return false;
            // A WOW64 target has 32-bit process parameters; these offsets would mis-decode it.
            if (IsWow64Process(handle, out bool wow64) && wow64) return false;

            var basic = new byte[48];
            if (NtQueryInformationProcess(handle, 0, basic, basic.Length, out int written) != 0) return false;
            if (written < ProcessBasicInformationMinBytes) return false;
            if (!ServerProcessControl.TryReadPointer(basic, ServerProcessControl.ProcessBasicInformationPebOffset, out ulong peb))
                return false;

            if (!TryReadRemote(handle, peb + ServerProcessControl.PebProcessParametersOffset, 8, out var pointerBytes)) return false;
            if (!ServerProcessControl.TryReadPointer(pointerBytes, 0, out ulong parameters)) return false;

            int parametersBytes = ServerProcessControl.CommandLineOffset + ServerProcessControl.UnicodeStringBytes;
            if (!TryReadRemote(handle, parameters, parametersBytes, out var block)) return false;

            commandLine = ReadRemoteUnicodeString(handle, block, ServerProcessControl.CommandLineOffset);
            workingDirectory = ReadRemoteUnicodeString(handle, block, ServerProcessControl.CurrentDirectoryDosPathOffset)
                .TrimEnd('\\');
            return commandLine.Length > 0;
        }
        catch
        {
            commandLine = "";
            workingDirectory = "";
            return false;
        }
        finally
        {
            if (handle != IntPtr.Zero) { try { CloseHandle(handle); } catch { /* */ } }
        }
    }

    /// <summary>Bytes of <c>PROCESS_BASIC_INFORMATION</c> up to and including <c>PebBaseAddress</c>.</summary>
    private const int ProcessBasicInformationMinBytes = ServerProcessControl.ProcessBasicInformationPebOffset + 8;

    private static string ReadRemoteUnicodeString(IntPtr handle, byte[] block, int offset)
    {
        if (!ServerProcessControl.TryDecodeUnicodeString(block, offset, out int byteLength, out ulong address)) return "";
        if (byteLength <= 0) return "";
        if (!TryReadRemote(handle, address, byteLength, out var text)) return "";
        return ServerProcessControl.DecodeWideString(text, byteLength);
    }

    private static bool TryReadRemote(IntPtr handle, ulong address, int length, out byte[] buffer)
    {
        buffer = Array.Empty<byte>();
        if (address == 0 || length <= 0 || length > 64 * 1024) return false;
        var bytes = new byte[length];
        if (!ReadProcessMemory(handle, (IntPtr)unchecked((long)address), bytes, (IntPtr)length, out IntPtr read)) return false;
        if ((long)read != length) return false;
        buffer = bytes;
        return true;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        IntPtr processHandle, int informationClass, byte[] processInformation, int processInformationLength, out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(
        IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, IntPtr dwSize, out IntPtr lpNumberOfBytesRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool IsWow64Process(IntPtr hProcess, out bool wow64Process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(IntPtr hProcess, int dwFlags, StringBuilder lpExeName, ref int lpdwSize);

    private static string Short(string sha) => string.IsNullOrEmpty(sha) ? "?" : sha.Length > 8 ? sha.Substring(0, 8) : sha;
}
