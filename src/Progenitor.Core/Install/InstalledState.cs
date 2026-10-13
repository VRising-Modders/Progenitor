using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Progenitor.Install;

/// <summary>How the selected listing relates to files and inventory on a target.</summary>
internal enum InstalledKind
{
    NotInstalled = 0,
    Pending = 1,
    Loaded = 2,
    ManagedElsewhere = 3,
    UpdateAvailable = 4,
}

/// <summary>One Progenitor-written file under pending or reload. Never a <c>plugins/</c> path.</summary>
internal readonly struct InstalledFile
{
    public InstalledFile(string path, string kind, string sha256, string? root = null, long size = 0)
    {
        Path = path ?? "";
        Kind = kind ?? "";
        Sha256 = sha256 ?? "";
        Root = string.IsNullOrWhiteSpace(root) ? "" : root.Trim();
        Size = size < 0 ? 0 : size;
    }

    public string Path { get; }
    public string Kind { get; }
    public string Sha256 { get; }

    /// <summary>INSTORD2: <c>client</c> | <c>server</c> — which BepInEx root the scan found it under. Empty when a test supplied the row.</summary>
    public string Root { get; }

    /// <summary>
    /// POLISH1 defect D: the file's length in bytes, as the scan read it. A
    /// <see cref="Admission.QualificationRecord"/> is keyed by the image's sha256 <em>and</em> its
    /// size, so the row that asks "are these bytes reviewed" needs both. Zero means the size is not
    /// known (a synthetic row, or a file whose length could not be read), and every question that
    /// needs it fails closed rather than matching on the hash alone.
    /// </summary>
    public long Size { get; }
}

/// <summary>One Mods-tab inventory row used for "loaded from another tool" detection.</summary>
internal readonly struct InstalledInventoryRow
{
    public InstalledInventoryRow(string id, string version, string location, string? generation = null, string? manager = null)
    {
        Id = id ?? "";
        Version = version ?? "";
        Location = location ?? "";
        Generation = string.IsNullOrWhiteSpace(generation) ? null : generation.Trim();
        Manager = string.IsNullOrWhiteSpace(manager) ? "" : manager.Trim();
    }

    public string Id { get; }
    public string Version { get; }
    public string Location { get; }
    public string? Generation { get; }

    /// <summary>
    /// MANAGED1: the other mod manager that installed this plugin, when the inventory knows it.
    /// Empty when unknown, which is every row production builds today: nothing yet reads another
    /// manager's records.
    /// </summary>
    public string Manager { get; }
}

/// <summary>One ledger row. Absence of the ledger file is not an error.</summary>
internal readonly struct InstalledLedgerRow
{
    public InstalledLedgerRow(
        string id,
        string sha256,
        string version,
        string fileName,
        string? source = null,
        string? category = null,
        string? root = null,
        string? filePath = null,
        string? pluginId = null,
        DateTime? writtenUtc = null)
    {
        WrittenUtc = writtenUtc;
        PluginId = string.IsNullOrWhiteSpace(pluginId) ? "" : pluginId.Trim();
        Id = id ?? "";
        Sha256 = sha256 ?? "";
        Version = version ?? "";
        FileName = fileName ?? "";
        Source = string.IsNullOrWhiteSpace(source) ? "" : source.Trim();
        Category = string.IsNullOrWhiteSpace(category) ? "" : category.Trim();
        Root = string.IsNullOrWhiteSpace(root) ? "" : root.Trim();
        FilePath = string.IsNullOrWhiteSpace(filePath) ? "" : filePath.Trim();
    }

    public string Id { get; }
    public string Sha256 { get; }
    public string Version { get; }
    public string FileName { get; }

    /// <summary>INSTORD2: the <c>progenitor-install.json</c> <c>source</c> (<c>package:Ns-Name@1.2.3</c>). Empty for the old text ledger.</summary>
    public string Source { get; }

    /// <summary>INSTORD2: <c>pending</c> | <c>mod</c> | <c>framework</c>. Empty for the old text ledger.</summary>
    public string Category { get; }

    /// <summary>INSTORD2: <c>client</c> | <c>server</c> — the root whose ledger carried this row.</summary>
    public string Root { get; }

    /// <summary>INSTORD2: the absolute path the row names, resolved against the game root at scan time.</summary>
    public string FilePath { get; }

    /// <summary>
    /// POLISH4 item 1: the row's own <c>pluginId</c> field — the <c>[BepInPlugin]</c> GUID the install
    /// read out of the package. Empty for every row written before this job, which is why
    /// <see cref="LedgerSpelling"/> also has the identity-cache route.
    /// </summary>
    public string PluginId { get; }

    /// <summary>
    /// INSTALLCOLS1: the row's <c>writtenUtc</c> in <c>progenitor-install.json</c> — when Progenitor
    /// wrote the file (see <see cref="InstallTimes"/>). Null for the old text ledger and for rows a
    /// test supplies without one.
    /// </summary>
    public DateTime? WrittenUtc { get; }
}

/// <summary>
/// Injected file, inventory and ledger view. Tests supply synthetic rows; production
/// fills them from the client BepInEx root and the configured local-server folder.
/// </summary>
internal sealed class InstalledStateQuery
{
    public InstalledStateQuery(
        IReadOnlyList<InstalledFile>? files = null,
        IReadOnlyList<InstalledInventoryRow>? inventory = null,
        IReadOnlyList<InstalledLedgerRow>? ledger = null,
        string? loadedGeneration = null,
        LedgerIdentityProbe? identityProbe = null)
    {
        Files = files ?? Array.Empty<InstalledFile>();
        Inventory = inventory ?? Array.Empty<InstalledInventoryRow>();
        Ledger = ledger ?? Array.Empty<InstalledLedgerRow>();
        LoadedGeneration = string.IsNullOrWhiteSpace(loadedGeneration) ? null : loadedGeneration.Trim();
        IdentityProbe = identityProbe;
    }

    public IReadOnlyList<InstalledFile> Files { get; }
    public IReadOnlyList<InstalledInventoryRow> Inventory { get; }
    public IReadOnlyList<InstalledLedgerRow> Ledger { get; }
    public string? LoadedGeneration { get; }

    /// <summary>
    /// POLISH4 item 2: "which plugin GUID did an inspection read out of this listing's package"
    /// (<c>progenitor-cache/identity</c>), so a ledger row an older build wrote under the plugin id
    /// still joins to its listing in a session that has inspected nothing. Null in tests that supply
    /// their own rows, and in any caller with no catalog service: the four name spellings stand.
    /// </summary>
    public LedgerIdentityProbe? IdentityProbe { get; }

    /// <summary>
    /// INSTALLSIDES2 item 6: the same view limited to one root's files and ledger rows
    /// (<see cref="InstalledState.ClientRoot"/> or <see cref="InstalledState.ServerRoot"/>). The
    /// inventory (what this process loaded) is kept: it is this client's evidence. A side's state is
    /// read from this, so the local server's promoted copy can never read as this client's.
    /// </summary>
    public InstalledStateQuery ForRoot(string root)
    {
        var files = new List<InstalledFile>(Files.Count);
        for (int i = 0; i < Files.Count; i++)
            if (OnRoot(Files[i].Root, root)) files.Add(Files[i]);
        var ledger = new List<InstalledLedgerRow>(Ledger.Count);
        for (int i = 0; i < Ledger.Count; i++)
            if (OnRoot(Ledger[i].Root, root)) ledger.Add(Ledger[i]);
        return new InstalledStateQuery(files, Inventory, ledger, LoadedGeneration, IdentityProbe);
    }

    /// <summary>A row with no root is a synthetic row for the root being read (the client's, in practice).</summary>
    private static bool OnRoot(string rowRoot, string root) =>
        string.Equals(rowRoot, root, StringComparison.OrdinalIgnoreCase)
        || (rowRoot.Length == 0 && string.Equals(root, InstalledState.ClientRoot, StringComparison.OrdinalIgnoreCase));

    /// <summary>The same scan with an identity probe attached. Used by the pane after a re-scan.</summary>
    public InstalledStateQuery WithIdentityProbe(LedgerIdentityProbe? probe) =>
        probe == IdentityProbe ? this : new InstalledStateQuery(Files, Inventory, Ledger, LoadedGeneration, probe);
}

/// <summary>Read-only detection result for the listing header.</summary>
internal sealed class InstalledStateResult
{
    public InstalledStateResult(
        InstalledKind state,
        string by,
        IReadOnlyList<string> files,
        string? loadedVersion,
        string reason,
        bool hotLoaded = false,
        string? manager = null,
        string? pluginId = null)
    {
        PluginId = (pluginId ?? "").Trim();
        State = state;
        By = by ?? "";
        Files = files ?? Array.Empty<string>();
        LoadedVersion = loadedVersion;
        Reason = reason ?? "";
        HotLoaded = hotLoaded;
        Manager = string.IsNullOrWhiteSpace(manager) ? "" : manager!.Trim();
    }

    /// <summary>
    /// MANAGED1: the other tool that manages a <see cref="InstalledKind.ManagedElsewhere"/> install,
    /// when the inventory row named one. Empty when unknown and on every other state.
    /// </summary>
    public string Manager { get; }

    /// <summary>
    /// The loaded plugin a <see cref="InstalledKind.ManagedElsewhere"/> listing matched (its GUID), so
    /// the Install tab can offer the preloader's next-start uninstall for it. Empty otherwise.
    /// </summary>
    public string PluginId { get; }

    public InstalledKind State { get; }
    public string By { get; }
    public IReadOnlyList<string> Files { get; }
    public string? LoadedVersion { get; }
    public string Reason { get; }

    /// <summary>
    /// HOT3 defect 1: this listing's <c>progenitor-reload/</c> copy on a root this process runs is
    /// loaded <em>by the reloader</em>, which is what makes its Remove a hot unload with no restart.
    /// False for a copy BepInEx promoted and loaded at start (the inventory knows it, the reloader
    /// does not own it), whose Remove still owes a restart.
    /// </summary>
    public bool HotLoaded { get; }

    public string StateName => InstalledState.StateName(State);

    public object ToWidget() => new
    {
        state = StateName,
        by = By,
        files = Files,
        loadedVersion = LoadedVersion,
        hotLoaded = HotLoaded,
    };
}

/// <summary>
/// Read-only detection of whether a listing is already installed on a target, plus
/// deletion of exactly the Progenitor-written files. Pure over an injected view so
/// tests never touch the game.
/// </summary>
internal static class InstalledState
{
    public const string PendingFolder = "progenitor-pending";
    public const string ReloadFolder = "progenitor-reload";
    public const string PluginsFolder = "plugins";
    public const string LedgerFileName = "progenitor-server-mods.txt";
    public const string ByProgenitor = "progenitor";
    public const string ByOther = "other";
    public const string PendingKind = "pending";
    public const string ReloadKind = "reload";
    /// <summary>INSTORD2 root names in the readback and in <see cref="InstalledFile.Root"/>.</summary>
    public const string ClientRoot = "client";
    public const string ServerRoot = "server";

    public const string PendingReason =
        "installed by Progenitor (pending: takes effect at restart)";
    public const string ElsewhereReason =
        "loaded from BepInEx/plugins by another tool — remove it with that tool";
    public const string UnloadsAtRestart = "unloads at the next restart";

    /// <summary>INSTORD2: Bootstrap promoted the file into <c>progenitor-reload/</c>, but this process has not loaded it.</summary>
    public const string PromotedReason = "installed; loads at the next start";

    /// <summary>INSTORD2: the promoted copy is on the local server root, which this process never loads.</summary>
    public const string PromotedServerReason = "installed on the local server; loads at its next start";

    /// <summary>
    /// POLISH1 defect C: the local server's copy is <em>already promoted</em> — its own Bootstrap
    /// moved the pending file into <c>progenitor-reload/</c> at that process's start and the reconcile
    /// pass rewrote the ledger row as category <c>mod</c>. That pairing only ever happens after the
    /// server has started with the file, so from here it is installed, not waiting: the row is green
    /// and drops out of the pending bar.
    ///
    /// <para>Promotion is the only writer of a <c>progenitor-reload/</c> copy on a root this process
    /// does not run (<see cref="HotLoadExecutor.CanHotLoad"/> refuses a local-server half from the
    /// client), so nothing else can put the file there — but the <c>mod</c> category is still required,
    /// because a stale <c>pending</c> row whose file has moved matches the promoted copy by leaf and
    /// would otherwise read installed before anything reconciled it.</para>
    /// </summary>
    public const string PromotedServerLoadedReason = "installed on the local server";

    public static string StateName(InstalledKind state) => state switch
    {
        InstalledKind.Pending => "pending",
        InstalledKind.Loaded => "loaded",
        InstalledKind.ManagedElsewhere => "managed-elsewhere",
        InstalledKind.UpdateAvailable => "update-available",
        _ => "not-installed",
    };

    public static string LoadedReason(string? generation) =>
        "installed by Progenitor (loaded: "
        + (string.IsNullOrWhiteSpace(generation) ? "unknown" : generation.Trim())
        + ")";

    /// <summary>
    /// Scan pending and reload folders under each BepInEx root, optionally a ledger,
    /// and the supplied inventory. Missing folders and a missing ledger are empty, not errors.
    /// </summary>
    public static InstalledStateQuery Scan(
        string? clientBepInExRoot,
        string? serverBepInExRoot,
        IReadOnlyList<InstalledInventoryRow>? inventory = null,
        string? loadedGeneration = null,
        LedgerIdentityProbe? identityProbe = null)
    {
        var files = new List<InstalledFile>();
        var ledger = new List<InstalledLedgerRow>();
        CollectRoot(clientBepInExRoot, ClientRoot, files, ledger);
        if (!SameRoot(clientBepInExRoot, serverBepInExRoot))
            CollectRoot(serverBepInExRoot, ServerRoot, files, ledger);
        return new InstalledStateQuery(files, inventory, ledger, loadedGeneration, identityProbe);
    }

    /// <summary>
    /// HOT3 defect 1: is one of <paramref name="reloaderPluginNames"/> — what
    /// <c>PluginReloader.LoadedPluginNames()</c> answers for the plugins this process loaded out of
    /// <c>progenitor-reload/</c> — this listing's plugin? The reloader answers BepInEx plugin
    /// <em>names</em> ("Progenitor Hot Demo"), never package keys or GUIDs, so the comparison is over
    /// letters and digits only (case-folded, spaces and punctuation dropped) and a name is a match
    /// when it equals or ends with one of the listing's own identities. Nothing here reads a plugin
    /// instance or an assembly.
    /// </summary>
    internal static bool ReloaderReportsLoaded(
        CatalogEntry entry, IReadOnlyList<string>? reloaderPluginNames, string? pluginGuid = null)
    {
        if (entry == null || reloaderPluginNames == null || reloaderPluginNames.Count == 0) return false;
        var wanted = new List<string>(5);
        AddIdentity(wanted, entry.Manifest.Id);
        AddIdentity(wanted, entry.DisplayName);
        AddIdentity(wanted, entry.PackageName);
        AddIdentity(wanted, pluginGuid);
        if (wanted.Count == 0) return false;

        for (int i = 0; i < reloaderPluginNames.Count; i++)
        {
            string name = Squash(reloaderPluginNames[i]);
            if (name.Length == 0) continue;
            for (int j = 0; j < wanted.Count; j++)
            {
                string want = wanted[j];
                // A prefix ("Progenitor Hot Demo" for HotDemo) is the reloader's own naming; a
                // bare substring is not, so "Demo" never claims "HotDemo"'s row.
                if (name.Equals(want, StringComparison.Ordinal) || name.EndsWith(want, StringComparison.Ordinal))
                    return true;
            }
        }

        return false;
    }

    private static void AddIdentity(List<string> into, string? value)
    {
        string squashed = Squash(value);
        // Two letters is not an identity: it would match almost any reloader name's tail.
        if (squashed.Length < 3) return;
        for (int i = 0; i < into.Count; i++)
        {
            if (string.Equals(into[i], squashed, StringComparison.Ordinal)) return;
        }

        into.Add(squashed);
    }

    /// <summary>Letters and digits only, lower-cased: <c>Progenitor Hot Demo</c> → <c>progenitorhotdemo</c>.</summary>
    private static string Squash(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var sb = new StringBuilder(value.Length);
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        }

        return sb.ToString();
    }

    /// <param name="reloaderPluginNames">
    /// HOT3 defect 1: the plugin names the reloader reports loaded right now
    /// (<c>PluginReloader.LoadedPluginNames()</c>). A ledgered reload copy on a root this process
    /// runs is <see cref="InstalledKind.Loaded"/> as soon as this says so, without waiting for the
    /// Mods-tab inventory snapshot the pane happened to be built with. Null (the default) keeps the
    /// inventory-only behavior.
    /// </param>
    /// <param name="pluginGuid">The plugin GUID a prior inspection cached for this listing, when known.</param>
    public static InstalledStateResult Detect(
        CatalogEntry entry,
        InstalledStateQuery? query,
        IReadOnlyList<string>? reloaderPluginNames = null,
        string? pluginGuid = null)
    {
        if (entry == null) throw new ArgumentNullException(nameof(entry));
        query ??= new InstalledStateQuery();
        bool reloaderLoaded = ReloaderReportsLoaded(entry, reloaderPluginNames, pluginGuid);

        string? listingSha = EmptyToNull(entry.Manifest.Sha256);
        string listingVersion = entry.Manifest.Version ?? "";
        var matched = new List<InstalledFile>();
        var progenitor = new List<InstalledFile>();
        CollectProgenitorFiles(entry, query, progenitor, matched, listingSha);

        var inventory = FindInventory(entry, query.Inventory);
        string? loadedVersion = inventory?.Version;
        string? generation = inventory?.Generation ?? query.LoadedGeneration;

        // INSTORD2: the install ledger outlives the session, so it decides first. Without it a
        // promoted file only matched by this session's inspection sha and read as another tool's.
        var byLedger = DetectFromLedger(entry, query, listingVersion, inventory != null, generation, reloaderLoaded);
        if (byLedger != null) return byLedger;

        if (matched.Count > 0)
        {
            bool loaded = HasKind(matched, ReloadKind);
            var paths = PathsOf(matched);
            return loaded
                ? new InstalledStateResult(
                    InstalledKind.Loaded, ByProgenitor, paths, loadedVersion, LoadedReason(generation), reloaderLoaded)
                : new InstalledStateResult(InstalledKind.Pending, ByProgenitor, paths, loadedVersion, PendingReason);
        }

        if (progenitor.Count > 0)
        {
            return new InstalledStateResult(
                InstalledKind.UpdateAvailable,
                ByProgenitor,
                PathsOf(progenitor),
                loadedVersion,
                HasKind(progenitor, ReloadKind) ? LoadedReason(generation) : PendingReason);
        }

        if (inventory != null && IsLocal(inventory.Value.Location))
        {
            string reason = ElsewhereReason;
            if (IsNewer(listingVersion, inventory.Value.Version))
                reason += "; update available: " + inventory.Value.Version + " → " + listingVersion;
            return new InstalledStateResult(
                InstalledKind.ManagedElsewhere,
                ByOther,
                Array.Empty<string>(),
                inventory.Value.Version,
                reason,
                manager: inventory.Value.Manager,
                pluginId: inventory.Value.Id);
        }

        return new InstalledStateResult(
            InstalledKind.NotInstalled,
            "",
            Array.Empty<string>(),
            loadedVersion,
            "");
    }

    /// <summary>
    /// Delete exactly the listed Progenitor-written files. Refuses anything under
    /// <c>BepInEx/plugins/</c>. Validates every path before deleting any.
    /// </summary>
    public static bool Remove(IReadOnlyList<string> files, out string? error)
    {
        error = null;
        if (files == null || files.Count == 0)
        {
            error = "no Progenitor-written files to remove";
            return false;
        }

        var owned = new List<string>(files.Count);
        for (int i = 0; i < files.Count; i++)
        {
            string path = files[i];
            if (string.IsNullOrWhiteSpace(path)) continue;
            string full;
            try { full = Path.GetFullPath(path.Trim()); }
            catch
            {
                error = "refused a path that is not a Progenitor-written pending or reload file";
                return false;
            }

            if (!IsProgenitorOwnedPath(full))
            {
                error = "refused a path that is not a Progenitor-written pending or reload file";
                return false;
            }

            owned.Add(full);
        }

        if (owned.Count == 0)
        {
            error = "no Progenitor-written files to remove";
            return false;
        }

        var failed = new StringBuilder();
        for (int i = 0; i < owned.Count; i++)
        {
            try
            {
                if (File.Exists(owned[i])) File.Delete(owned[i]);
            }
            catch (Exception e)
            {
                if (failed.Length > 0) failed.Append("; ");
                failed.Append(owned[i]).Append(": ").Append(e.Message);
            }
        }

        if (failed.Length > 0)
        {
            error = failed.ToString();
            return false;
        }

        return true;
    }

    public static bool IsProgenitorOwnedPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        string full;
        try { full = Path.GetFullPath(path.Trim()); }
        catch { return false; }
        if (HasDirectorySegment(full, PluginsFolder)
            && !HasDirectorySegment(full, PendingFolder)
            && !HasDirectorySegment(full, ReloadFolder))
            return false;
        return HasDirectorySegment(full, PendingFolder) || HasDirectorySegment(full, ReloadFolder);
    }

    public static int CompareVersions(string? a, string? b)
    {
        string left = (a ?? "").Trim();
        string right = (b ?? "").Trim();
        if (left.Length == 0 && right.Length == 0) return 0;
        if (left.Length == 0) return -1;
        if (right.Length == 0) return 1;
        if (Version.TryParse(NormalizeVersion(left), out var va)
            && Version.TryParse(NormalizeVersion(right), out var vb))
            return va.CompareTo(vb);
        return string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsNewer(string? listingVersion, string? loadedVersion) =>
        CompareVersions(listingVersion, loadedVersion) > 0;

    public static string HashBytes(byte[] data)
    {
        if (data == null) return "";
        using var sha = SHA256.Create();
        return ToHex(sha.ComputeHash(data));
    }

    public static string HashFile(string path)
    {
        using var sha = SHA256.Create();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return ToHex(sha.ComputeHash(stream));
    }

    internal static IReadOnlyList<InstalledLedgerRow> ParseLedger(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<InstalledLedgerRow>();
        var list = new List<InstalledLedgerRow>();
        string[] lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            string[] parts = line.Split('\t');
            if (parts.Length < 4) continue;
            string sha = parts[0].Trim();
            string fileName = parts[1].Trim();
            string id = parts[2].Trim();
            string version = parts[3].Trim();
            if (id.Length == 0 && sha.Length == 0) continue;
            list.Add(new InstalledLedgerRow(id, sha, version, fileName));
        }

        return list;
    }

    private static void CollectRoot(string? bepInExRoot, string rootKind, List<InstalledFile> files, List<InstalledLedgerRow> ledger)
    {
        if (string.IsNullOrWhiteSpace(bepInExRoot)) return;
        string root;
        try { root = Path.GetFullPath(bepInExRoot.Trim()); }
        catch { return; }
        AddFolder(Path.Combine(root, PendingFolder), PendingKind, rootKind, files);
        AddFolder(Path.Combine(root, ReloadFolder), ReloadKind, rootKind, files);
        CollectInstallLedger(root, rootKind, ledger);
        string config = Path.Combine(root, "config", LedgerFileName);
        try
        {
            if (File.Exists(config))
            {
                var rows = ParseLedger(File.ReadAllText(config));
                for (int i = 0; i < rows.Count; i++) ledger.Add(rows[i]);
            }
        }
        catch { /* ledger is optional */ }
    }

    /// <summary>
    /// INSTORD2: the rows of <c>BepInEx/progenitor-install.json</c> whose <c>source</c> names a
    /// package. This is the only record that survives a restart, so detection reads it instead of
    /// asking this session's inspection for a sha. A missing or torn ledger is empty, not an error.
    /// </summary>
    private static void CollectInstallLedger(string bepInExRoot, string rootKind, List<InstalledLedgerRow> ledger)
    {
        string? gameRoot;
        try { gameRoot = ServerInstall.GameRootOf(bepInExRoot); }
        catch { return; }
        if (string.IsNullOrEmpty(gameRoot)) return;
        ServerInstallLedger? read;
        try { read = ServerInstallLedger.TryRead(gameRoot); }
        catch { return; }
        if (read == null) return;
        for (int i = 0; i < read.Files.Count; i++)
        {
            var row = read.Files[i];
            if (!TrySplitPackageSource(row.Source, out string package, out string version)) continue;
            // SRVUNINST1: a record whose file is gone is not an install; both tabs read it as absent.
            if (ServerInstall.IsOrphanRow(gameRoot, row)) continue;
            string absolute;
            try { absolute = ServerInstallLedger.Absolute(gameRoot, row.Path); }
            catch { absolute = ""; }
            ledger.Add(new InstalledLedgerRow(
                package,
                row.Sha256,
                version,
                Path.GetFileName(row.Path),
                row.Source,
                row.Category,
                rootKind,
                absolute,
                row.PluginId,
                row.WrittenUtc));
        }
    }

    /// <summary><c>package:Ns-Name@1.2.3</c> → <c>Ns-Name</c> and <c>1.2.3</c>. Any other source is not a package row.</summary>
    internal static bool TrySplitPackageSource(string? source, out string package, out string version)
    {
        package = version = "";
        string s = (source ?? "").Trim();
        if (!s.StartsWith(ServerInstallLedger.PackageSourcePrefix, StringComparison.OrdinalIgnoreCase)) return false;
        s = s.Substring(ServerInstallLedger.PackageSourcePrefix.Length);
        int at = s.LastIndexOf('@');
        if (at < 0) { package = s.Trim(); return package.Length > 0; }
        package = s.Substring(0, at).Trim();
        version = s.Substring(at + 1).Trim();
        return package.Length > 0;
    }

    private static void AddFolder(string folder, string kind, string rootKind, List<InstalledFile> files)
    {
        string[] dlls;
        try
        {
            if (!Directory.Exists(folder)) return;
            dlls = Directory.GetFiles(folder, "*.dll", SearchOption.AllDirectories);
        }
        catch { return; }

        for (int i = 0; i < dlls.Length; i++)
        {
            string path = dlls[i];
            if (!IsProgenitorOwnedPath(path)) continue;
            string sha;
            try { sha = HashFile(path); }
            catch { continue; }
            // POLISH1 defect D: one stat beside a whole-file hash. Unreadable length is 0, which
            // every qualification question treats as "not known" and refuses on.
            long size = 0;
            try { size = new FileInfo(path).Length; }
            catch { size = 0; }
            files.Add(new InstalledFile(path, kind, sha, rootKind, size));
        }
    }

    /// <summary>INSTORD2: the install-ledger rows, on either root, whose source names this listing's package.</summary>
    internal static IReadOnlyList<InstalledLedgerRow> LedgerRowsFor(CatalogEntry entry, InstalledStateQuery? query)
    {
        var rows = new List<InstalledLedgerRow>();
        if (entry == null || query == null) return rows;
        for (int i = 0; i < query.Ledger.Count; i++)
        {
            var row = query.Ledger[i];
            if (row.Source.Length == 0) continue;
            // POLISH4 item 2: the row as a whole, so its own `pluginId` field and the identity cache
            // are asked as well as the four name spellings. The live server row for HotDemo carried
            // only the old plugin-id source, and reading `row.Id` alone reported `server: none`.
            if (!LedgerSpelling.Matches(entry, row, query.IdentityProbe)) continue;
            rows.Add(row);
        }

        return rows;
    }

    /// <summary>
    /// INSTORD2 readback: the Progenitor-written files this listing's ledger rows name and that are
    /// on disk now, with the <c>pending</c>/<c>reload</c> kind and the <c>client</c>/<c>server</c> root.
    /// </summary>
    internal static IReadOnlyList<InstalledFile> LedgerFilesFor(CatalogEntry entry, InstalledStateQuery? query)
    {
        var found = new List<InstalledFile>();
        if (query == null) return found;
        var rows = LedgerRowsFor(entry, query);
        for (int i = 0; i < rows.Count; i++)
        {
            var file = FindLedgerFile(query.Files, rows[i]);
            if (file == null) continue;
            bool already = false;
            for (int j = 0; j < found.Count; j++)
            {
                if (string.Equals(found[j].Path, file.Value.Path, StringComparison.OrdinalIgnoreCase)) already = true;
            }

            if (!already) found.Add(file.Value);
        }

        return found;
    }

    /// <summary>
    /// F34 (live 2026-10-08): the server root's reload folder holds a copy of this listing's client reload file (same file
    /// name), with no server ledger row: the private game's launcher stages the client's reload folder into the hosted
    /// child, which loads it. <paramref name="version"/> is the client row's version.
    /// </summary>
    internal static bool StagedOnServer(CatalogEntry entry, InstalledStateQuery? query, out string version)
    {
        version = "";
        if (entry == null || query == null) return false;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rows = LedgerRowsFor(entry, query);
        for (int i = 0; i < rows.Count; i++)
        {
            if (string.Equals(rows[i].Root, ServerRoot, StringComparison.OrdinalIgnoreCase)) continue;
            var file = FindLedgerFile(query.Files, rows[i]);
            if (file == null || !string.Equals(file.Value.Kind, ReloadKind, StringComparison.OrdinalIgnoreCase)) continue;
            string name;
            try { name = Path.GetFileName(file.Value.Path); }
            catch { continue; }
            if (name.Length == 0) continue;
            names.Add(name);
            if (version.Length == 0) version = rows[i].Version;
        }

        if (names.Count == 0) return false;
        foreach (var f in query.Files)
        {
            if (!string.Equals(f.Root, ServerRoot, StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.Equals(f.Kind, ReloadKind, StringComparison.OrdinalIgnoreCase)) continue;
            string name;
            try { name = Path.GetFileName(f.Path); }
            catch { continue; }
            if (names.Contains(name)) return true;
        }

        return false;
    }

    /// <summary>
    /// INSTORD2 readback <c>listing.ledger {client, server, version}</c>: which roots' install
    /// ledgers name this package and at which version, without an inspection this session.
    /// </summary>
    internal static (bool Client, bool Server, string? Version) LedgerViewOf(CatalogEntry entry, InstalledStateQuery? query)
    {
        bool client = false, server = false;
        string? version = null;
        var rows = LedgerRowsFor(entry, query);
        for (int i = 0; i < rows.Count; i++)
        {
            if (string.Equals(rows[i].Root, ServerRoot, StringComparison.OrdinalIgnoreCase)) server = true;
            else client = true;
            if (version == null && rows[i].Version.Length > 0) version = rows[i].Version;
        }

        return (client, server, version);
    }

    /// <summary>
    /// POLISH1 defect C: is the local server's copy of this listing a promoted one — a
    /// <c>progenitor-reload/</c> file on the server root named by a reconciled ledger row of category
    /// <c>mod</c>? See <see cref="PromotedServerLoadedReason"/> for why that pairing is the evidence.
    /// </summary>
    internal static bool ServerCopyPromoted(CatalogEntry entry, InstalledStateQuery? query)
    {
        if (query == null) return false;
        var rows = LedgerRowsFor(entry, query);
        for (int i = 0; i < rows.Count; i++)
        {
            if (!string.Equals(rows[i].Category, ServerInstallLedger.CategoryMod, StringComparison.Ordinal)) continue;
            var file = FindLedgerFile(query.Files, rows[i]);
            if (file == null) continue;
            if (!string.Equals(file.Value.Kind, ReloadKind, StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(file.Value.Root, ServerRoot, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    // ---- POLISH2 item 2: the same facts, read from ledger rows alone -------------------
    /// <summary>
    /// POLISH2 item 2: which Progenitor folder the file a ledger row names sits in —
    /// <see cref="ReloadKind"/>, <see cref="PendingKind"/>, or empty when the row names no path (the
    /// old text ledger) or a path under neither folder. Read from the row itself, so a caller that
    /// holds ledger rows without a disk scan — the Mods sidebar — can tell a promoted copy from a
    /// downloaded one instead of calling everything ledgered "pending".
    /// </summary>
    internal static string LedgerRowFolderKind(InstalledLedgerRow row)
    {
        string path = row.FilePath.Length > 0 ? row.FilePath : row.FileName;
        if (path.Length == 0) return "";
        string folder;
        try { folder = Path.GetFileName(Path.GetDirectoryName(path) ?? ""); }
        catch { return ""; }
        if (string.Equals(folder, ReloadFolder, StringComparison.OrdinalIgnoreCase)) return ReloadKind;
        if (string.Equals(folder, PendingFolder, StringComparison.OrdinalIgnoreCase)) return PendingKind;
        return "";
    }

    /// <summary>Is this row's file in <c>progenitor-reload/</c> — a copy the process on its root loads at start?</summary>
    internal static bool IsReloadLedgerRow(InstalledLedgerRow row) =>
        string.Equals(LedgerRowFolderKind(row), ReloadKind, StringComparison.OrdinalIgnoreCase);

    /// <summary>Is this row's file still in <c>progenitor-pending/</c> — downloaded, not promoted?</summary>
    internal static bool IsPendingLedgerRow(InstalledLedgerRow row) =>
        string.Equals(LedgerRowFolderKind(row), PendingKind, StringComparison.OrdinalIgnoreCase);

    /// <summary>Did this row come from the local server's ledger?</summary>
    internal static bool IsServerLedgerRow(InstalledLedgerRow row) =>
        string.Equals(row.Root, ServerRoot, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// POLISH2 item 2: the row shape <see cref="ServerCopyPromoted"/> looks for, judged from the row
    /// alone — a reconciled <c>mod</c> row on the local-server root naming a
    /// <c>progenitor-reload/</c> file. The local server promoted it at its own start, so it is
    /// installed and loaded there, not waiting for anything.
    /// </summary>
    internal static bool IsPromotedServerLedgerRow(InstalledLedgerRow row) =>
        IsServerLedgerRow(row)
        && IsReloadLedgerRow(row)
        && string.Equals(row.Category, ServerInstallLedger.CategoryMod, StringComparison.Ordinal);

    /// <summary>
    /// POLISH1 defect D: this listing's ledgered <c>progenitor-reload/</c> copy on
    /// <paramref name="rootKind"/>, or null. The file a qualification record has to be keyed to.
    /// </summary>
    internal static InstalledFile? ReloadFileOn(CatalogEntry entry, InstalledStateQuery? query, string rootKind)
    {
        var files = LedgerFilesFor(entry, query);
        for (int i = 0; i < files.Count; i++)
        {
            if (!string.Equals(files[i].Kind, ReloadKind, StringComparison.OrdinalIgnoreCase)) continue;
            bool onServer = string.Equals(files[i].Root, ServerRoot, StringComparison.OrdinalIgnoreCase);
            bool wantServer = string.Equals(rootKind, ServerRoot, StringComparison.OrdinalIgnoreCase);
            if (onServer != wantServer) continue;
            return files[i];
        }

        return null;
    }

    /// <summary>HOT4 readback <c>listing.halves</c> values.</summary>
    public const string HalfLoaded = "loaded";
    public const string HalfPending = "pending";
    public const string HalfNone = "none";

    /// <summary>
    /// HOT4 readback <c>listing.halves {client, server}</c>: what each process holds of this listing
    /// right now, read from the ledgered files on disk. A <c>progenitor-reload/</c> copy on the root
    /// this process runs is <c>loaded</c> when <paramref name="loadedHere"/> says the reloader (or the
    /// inventory) has it; anything else the ledger names is <c>pending</c>, and nothing is <c>none</c>.
    ///
    /// <para>POLISH1 defect C: the local-server half is <c>loaded</c> once its copy has been
    /// <em>promoted</em> — a <c>progenitor-reload/</c> file on the server root named by a reconciled
    /// <c>mod</c> row (<see cref="ServerCopyPromoted"/>). Promotion happens at that process's own
    /// start, so the wait it used to report is over; before the promote the half is still
    /// <c>pending</c>. Live (deploy #70) the server had restarted and promoted both installs and the
    /// client's rows still read <c>pending</c>, still counted in the bar, and still offered
    /// <c>Cancel installing …?</c>.</para>
    /// </summary>
    internal static (string Client, string Server) HalvesOf(
        CatalogEntry entry, InstalledStateQuery? query, bool loadedHere)
    {
        string client = HalfNone, server = HalfNone;
        bool serverPromoted = ServerCopyPromoted(entry, query);
        var files = LedgerFilesFor(entry, query);
        for (int i = 0; i < files.Count; i++)
        {
            bool onServer = string.Equals(files[i].Root, ServerRoot, StringComparison.OrdinalIgnoreCase);
            if (onServer)
            {
                if (serverPromoted
                    && string.Equals(files[i].Kind, ReloadKind, StringComparison.OrdinalIgnoreCase))
                    server = HalfLoaded;
                else if (server != HalfLoaded) server = HalfPending;
                continue;
            }

            bool reload = string.Equals(files[i].Kind, ReloadKind, StringComparison.OrdinalIgnoreCase);
            if (reload && loadedHere) client = HalfLoaded;
            else if (client != HalfLoaded) client = HalfPending;
        }

        return (client, server);
    }

    /// <summary>
    /// INSTORD2: state from the install ledger. A row whose file is on disk makes the listing
    /// Progenitor's: a promoted copy on a root this process runs is <see cref="InstalledKind.Loaded"/>
    /// when the inventory names the plugin and <see cref="InstalledKind.Pending"/> otherwise; a
    /// promoted copy only on the local-server root is always Pending, because this process never
    /// loads it. Null when no ledger row names the package or none of their files is on disk.
    /// </summary>
    private static InstalledStateResult? DetectFromLedger(
        CatalogEntry entry,
        InstalledStateQuery query,
        string listingVersion,
        bool inventoryLoaded,
        string? generation,
        bool reloaderLoaded)
    {
        var rows = LedgerRowsFor(entry, query);
        if (rows.Count == 0) return null;

        string? ledgerVersion = null;
        bool reloadHere = false, reloadServer = false, reloadServerPromoted = false;
        var files = new List<InstalledFile>();
        for (int i = 0; i < rows.Count; i++)
        {
            if (ledgerVersion == null && rows[i].Version.Length > 0) ledgerVersion = rows[i].Version;
            var file = FindLedgerFile(query.Files, rows[i]);
            if (file == null) continue;
            bool duplicate = false;
            for (int j = 0; j < files.Count; j++)
            {
                if (string.Equals(files[j].Path, file.Value.Path, StringComparison.OrdinalIgnoreCase)) duplicate = true;
            }

            if (!duplicate) files.Add(file.Value);
            if (!string.Equals(file.Value.Kind, ReloadKind, StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(file.Value.Root, ServerRoot, StringComparison.OrdinalIgnoreCase))
            {
                reloadServer = true;
                // POLISH1 defect C: promoted on the local server (a reconciled `mod` row naming a
                // reload copy there), not merely moved there by something this process cannot see.
                if (string.Equals(rows[i].Category, ServerInstallLedger.CategoryMod, StringComparison.Ordinal))
                    reloadServerPromoted = true;
            }
            else reloadHere = true;
        }

        if (files.Count == 0) return null;

        InstalledKind state;
        string reason;
        // HOT3 defect 1: the reloader's own report is as good as the inventory snapshot — better,
        // in fact, because the snapshot belongs to the build the pane was made with and a plugin the
        // reloader loaded a second ago is not in it. Either one makes a promoted copy on this root
        // Loaded; only the reloader's makes it hot.
        bool hot = reloadHere && reloaderLoaded;
        if (reloadHere && (inventoryLoaded || reloaderLoaded))
        {
            state = InstalledKind.Loaded;
            reason = LoadedReason(generation);
        }
        else if (reloadHere)
        {
            state = InstalledKind.Pending;
            reason = PromotedReason;
        }
        else if (reloadServerPromoted)
        {
            // POLISH1 defect C: the local server started with it. Installed, not pending.
            state = InstalledKind.Loaded;
            reason = PromotedServerLoadedReason;
        }
        else if (reloadServer)
        {
            state = InstalledKind.Pending;
            reason = PromotedServerReason;
        }
        else
        {
            state = InstalledKind.Pending;
            reason = PendingReason;
        }

        if (IsNewer(listingVersion, ledgerVersion)) state = InstalledKind.UpdateAvailable;
        return new InstalledStateResult(state, ByProgenitor, PathsOf(files), ledgerVersion, reason, hot);
    }

    /// <summary>The scanned file one ledger row names: the same absolute path, else the same leaf under the same root.</summary>
    private static InstalledFile? FindLedgerFile(IReadOnlyList<InstalledFile> files, InstalledLedgerRow row)
    {
        InstalledFile? fallback = null;
        for (int i = 0; i < files.Count; i++)
        {
            var file = files[i];
            if (!IsProgenitorOwnedPath(file.Path)) continue;
            if (row.FilePath.Length > 0
                && string.Equals(file.Path, row.FilePath, StringComparison.OrdinalIgnoreCase))
                return file;
            if (row.FileName.Length == 0) continue;
            if (!string.Equals(Path.GetFileName(file.Path), row.FileName, StringComparison.OrdinalIgnoreCase)) continue;
            if (row.Root.Length > 0 && file.Root.Length > 0
                && !string.Equals(file.Root, row.Root, StringComparison.OrdinalIgnoreCase))
                continue;
            if (fallback == null
                || string.Equals(file.Kind, ReloadKind, StringComparison.OrdinalIgnoreCase))
                fallback = file;
        }

        return fallback;
    }

    /// <summary>
    /// Does <paramref name="package"/> name this listing? Normally a package key (<c>Ns-Name</c>),
    /// which is never read as a plugin GUID.
    ///
    /// <para>HOT4 defect: the first-class <c>progenitor.json</c> restart route writes its ledger row
    /// with the <em>plugin id</em> in the source (<c>InstallStaging.AppendServerLedger</c> →
    /// <c>ServerInstall.TryAppendPending</c>, <c>package:example.x@1.0.0</c>) while the hot route and
    /// the ordinary route both write the package key. Reading only package keys made a real pending
    /// install on both roots read back as <c>ledger {client false, server false}</c> with <c>files
    /// []</c>, and its Remove find nothing. So the listing's own plugin id is accepted here as well —
    /// it is the listing's identity, not a guess: <see cref="IdsMatch"/> is the same comparison the
    /// inventory uses.</para>
    /// </summary>
    /// <summary>
    /// Does <paramref name="package"/> — a ledger row's package name — name this listing? The key,
    /// the manifest id, the manifest package and <c>&lt;owner&gt;-&lt;name&gt;</c> are all spellings the
    /// install routes have written. POLISH3 item A made this internal: the removal used to ask for
    /// two of those four spellings while this read accepted all four, so a half whose row the pane
    /// could <em>see</em> (the local server's, ledgered under the plugin id by the first-class
    /// restart route) was a half the uninstall could not reach.
    /// </summary>
    /// <summary>
    /// POLISH4 item 2: one comparison, <see cref="LedgerSpelling.Matches(CatalogEntry, string, LedgerIdentityProbe)"/>,
    /// and no caller keeps its own — the class of defect this closes is two comparisons disagreeing.
    /// </summary>
    internal static bool PackageMatches(CatalogEntry entry, string package, LedgerIdentityProbe? identity = null) =>
        LedgerSpelling.Matches(entry, package, identity);

    private static void CollectProgenitorFiles(
        CatalogEntry entry,
        InstalledStateQuery query,
        List<InstalledFile> progenitor,
        List<InstalledFile> matched,
        string? listingSha)
    {
        string? fileName = PluginFileName(entry);
        for (int i = 0; i < query.Files.Count; i++)
        {
            var file = query.Files[i];
            if (!IsProgenitorOwnedPath(file.Path)) continue;
            bool nameMatch = fileName != null
                && string.Equals(Path.GetFileName(file.Path), fileName, StringComparison.OrdinalIgnoreCase);
            bool hashMatch = listingSha != null
                && string.Equals(file.Sha256, listingSha, StringComparison.OrdinalIgnoreCase);
            bool ledgerMatch = LedgerOwns(query.Ledger, entry, file, query.IdentityProbe);
            if (!nameMatch && !hashMatch && !ledgerMatch) continue;
            progenitor.Add(file);
            if (hashMatch || (listingSha == null && (nameMatch || ledgerMatch)))
                matched.Add(file);
        }

        if (matched.Count > 0 || progenitor.Count > 0) return;
        for (int i = 0; i < query.Ledger.Count; i++)
        {
            // INSTORD2: an install-ledger row is only evidence while the file it names is there;
            // DetectFromLedger already answered for those. The old text ledger has no source and
            // keeps its file-less "pending" meaning.
            if (query.Ledger[i].Source.Length > 0) continue;
            if (!LedgerMatchesEntry(query.Ledger[i], entry, listingSha)) continue;
            progenitor.Add(new InstalledFile(query.Ledger[i].FileName, PendingKind, query.Ledger[i].Sha256));
            if (listingSha == null
                || string.Equals(query.Ledger[i].Sha256, listingSha, StringComparison.OrdinalIgnoreCase))
                matched.Add(progenitor[progenitor.Count - 1]);
        }
    }

    private static bool LedgerOwns(
        IReadOnlyList<InstalledLedgerRow> ledger,
        CatalogEntry entry,
        InstalledFile file,
        LedgerIdentityProbe? identity = null)
    {
        string name = Path.GetFileName(file.Path);
        for (int i = 0; i < ledger.Count; i++)
        {
            var row = ledger[i];
            if (!LedgerMatchesEntry(row, entry, EmptyToNull(entry.Manifest.Sha256), identity)) continue;
            if (string.Equals(row.FileName, name, StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(row.Sha256, file.Sha256, StringComparison.OrdinalIgnoreCase)
                && row.Sha256.Length > 0)
                return true;
        }

        return false;
    }

    private static bool LedgerMatchesEntry(
        InstalledLedgerRow row, CatalogEntry entry, string? listingSha, LedgerIdentityProbe? identity = null)
    {
        if (row.Id.Length > 0 && IdsMatch(entry, row.Id)) return true;
        // POLISH4 item 2: the row's own plugin id and the identity cache, so a row written under the
        // plugin-id spelling owns its file here too.
        if (row.Source.Length > 0 && LedgerSpelling.Matches(entry, row, identity)) return true;
        if (listingSha != null && string.Equals(row.Sha256, listingSha, StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }

    private static InstalledInventoryRow? FindInventory(
        CatalogEntry entry, IReadOnlyList<InstalledInventoryRow> inventory)
    {
        for (int i = 0; i < inventory.Count; i++)
        {
            if (IdsMatch(entry, inventory[i].Id)) return inventory[i];
        }

        string? name = entry.DisplayName ?? entry.PackageName;
        if (string.IsNullOrWhiteSpace(name)) return null;
        for (int i = 0; i < inventory.Count; i++)
        {
            if (string.Equals(inventory[i].Id, name, StringComparison.OrdinalIgnoreCase))
                return inventory[i];
            if (inventory[i].Id.EndsWith("." + name, StringComparison.OrdinalIgnoreCase)
                || inventory[i].Id.EndsWith(name, StringComparison.OrdinalIgnoreCase))
                return inventory[i];
        }

        return null;
    }

    private static bool IdsMatch(CatalogEntry entry, string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        if (!string.IsNullOrEmpty(entry.Manifest.Id)
            && string.Equals(entry.Manifest.Id, id, StringComparison.OrdinalIgnoreCase))
            return true;
        if (string.Equals(entry.Key, id, StringComparison.OrdinalIgnoreCase)) return true;
        if (!string.IsNullOrEmpty(entry.PackageName)
            && string.Equals(entry.PackageName, id, StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }

    private static string? PluginFileName(CatalogEntry entry)
    {
        string id = entry.Manifest.Id;
        if (string.IsNullOrWhiteSpace(id)) return null;
        return id.Trim() + ".dll";
    }

    private static bool IsLocal(string location) =>
        location.IndexOf("Local", StringComparison.OrdinalIgnoreCase) >= 0;

    private static bool HasKind(List<InstalledFile> files, string kind)
    {
        for (int i = 0; i < files.Count; i++)
        {
            if (string.Equals(files[i].Kind, kind, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static IReadOnlyList<string> PathsOf(List<InstalledFile> files)
    {
        var list = new List<string>(files.Count);
        for (int i = 0; i < files.Count; i++)
        {
            if (string.IsNullOrEmpty(files[i].Path)) continue;
            list.Add(files[i].Path);
        }

        return list;
    }

    private static bool HasDirectorySegment(string path, string segment)
    {
        var parts = path.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < parts.Length; i++)
        {
            if (string.Equals(parts[i], segment, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static bool SameRoot(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        try
        {
            return string.Equals(
                Path.GetFullPath(a.Trim()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                Path.GetFullPath(b.Trim()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static string NormalizeVersion(string value)
    {
        int plus = value.IndexOf('+');
        if (plus >= 0) value = value.Substring(0, plus);
        int dash = value.IndexOf('-');
        if (dash > 0) value = value.Substring(0, dash);
        return value.Trim();
    }

    private static string ToHex(byte[] data)
    {
        var sb = new StringBuilder(data.Length * 2);
        for (int i = 0; i < data.Length; i++)
            sb.Append(data[i].ToString("x2", CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    private static string? EmptyToNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
