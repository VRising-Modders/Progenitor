using System;
using System.Collections.Generic;
using System.Globalization;
using Progenitor.Networking;
using Progenitor.Plugins;
using Progenitor.WorldState;
#if !PROGENITOR_TESTS
using System.Reflection;
using BepInEx;
using BepInEx.Unity.IL2CPP;
#endif

namespace Progenitor.Ui;

internal sealed class ModMenuInventoryRow
{
    internal string Id { get; init; } = "";
    internal string DisplayName { get; init; } = "";
    internal string Version { get; init; } = "";
    internal string Location { get; init; } = "";
    internal string Status { get; init; } = "";
    internal string Requirement { get; init; } = "";
    internal string Explanation { get; init; } = "";

    /// <summary>SETTINGS1: the version the loaded (or paused) plugin itself reports; empty when none was observed.</summary>
    internal string LocalVersion { get; init; } = "";

    /// <summary>SETTINGS1: the joined server's handshake request named this mod — the server runs it.</summary>
    internal bool ServerAsked { get; init; }
}

/// <summary>
/// Where a Mods-tab row's files came from. Decided from the same evidence the Install tab
/// reads: this framework's own id list, then each root's <c>BepInEx/progenitor-install.json</c>
/// ledger (INSTORD2), then whether the row is only a server-list claim.
/// </summary>
internal enum ModProvenance
{
    /// <summary>A ledgered Progenitor install: pending or promoted. Offers Remove.</summary>
    Progenitor = 0,

    /// <summary>Loaded from <c>BepInEx/plugins/</c> or anywhere Progenitor did not write. Read-only.</summary>
    Other = 1,

    /// <summary>
    /// MODS2: the plugin loader and Progenitor's own framework plugins — Core (and Bootstrap, which
    /// shares its GUID), the dev bridge and the preloader, exactly <see cref="ModMenuInventory.FrameworkIds"/>.
    /// Read-only: they sit in the collapsed <c>Required (N)</c> group and offer no Remove and no
    /// Update. This replaces MODS1's <c>framework</c>, which was hidden behind a developer toggle.
    /// Samples and examples are not required (FWALLOW1, 2026-10-06): they are <see cref="Reload"/> rows.
    /// </summary>
    Required = 2,

    /// <summary>Only the server reported it; no loaded client copy was observed.</summary>
    Server = 3,

    /// <summary>
    /// FWALLOW1: a plugin whose DLL sits in <c>BepInEx/progenitor-reload</c> and that no install
    /// ledger lists — a sample or example shipped with Progenitor's source, or a build copied there
    /// by hand. A normal mod: listed under Installed, with the switch and an Uninstall that deletes
    /// exactly that file (<see cref="ModsSidebarRow.ReloadFile"/>).
    /// </summary>
    Reload = 4,
}

/// <summary>One sidebar row of the Mods tab, with its provenance decided.</summary>
internal sealed class ModsSidebarRow
{
    internal string Id { get; init; } = "";
    internal string Name { get; init; } = "";
    internal string Version { get; init; } = "";
    internal ModProvenance Provenance { get; init; }
    internal ModMenuInventoryRow? Inventory { get; init; }

    /// <summary>The ledger rows, on either root, that name this plugin. Empty unless <see cref="Provenance"/> is Progenitor.</summary>
    internal IReadOnlyList<Install.InstalledLedgerRow> Ledger { get; init; } =
        Array.Empty<Install.InstalledLedgerRow>();

    /// <summary>Did this row pass the sidebar search?</summary>
    internal bool Matches { get; init; }

    /// <summary>
    /// FWALLOW1: the DLL in <c>BepInEx/progenitor-reload</c> that declares this plugin, for a
    /// <see cref="ModProvenance.Reload"/> row; empty for every other row.
    /// </summary>
    internal string ReloadFile { get; init; } = "";

    /// <summary>FWALLOW1: one of Progenitor's own framework plugins (<see cref="ModMenuInventory.IsFrameworkId"/>) or the loader.</summary>
    internal bool IsFramework => IsLoader || ModMenuInventory.IsFrameworkId(Id);

    /// <summary>FWALLOW1: the page offers Uninstall — a ledgered install, or a file in the reload folder.</summary>
    internal bool Removable => !IsFramework
        && (Provenance == ModProvenance.Progenitor || Provenance == ModProvenance.Reload);

    /// <summary>
    /// MODS2: the BepInEx row. It is the plugin loader, not a plugin, so it has no inventory row,
    /// no settings and no custom pages — its page is one sentence.
    /// </summary>
    internal bool IsLoader => string.Equals(Id, ModMenuInventory.LoaderId, StringComparison.OrdinalIgnoreCase);

    /// <summary>F3: only the local server folder's ledger installs it (listed in the server group).</summary>
    internal bool LocalServerOnly { get; init; }

    /// <summary>F3: the list group this row is drawn in (a local-server-only install is a server row).</summary>
    internal ModProvenance ListProvenance => LocalServerOnly ? ModProvenance.Server : Provenance;

    internal string Group => ListProvenance switch
    {
        ModProvenance.Server => ModMenuInventory.GroupServer,
        ModProvenance.Required => ModMenuInventory.GroupRequired,
        _ => ModMenuInventory.GroupMods,
    };

    internal string ProvenanceName => ModMenuInventory.NameOf(Provenance);

    internal string ProvenanceWord => ModMenuInventory.WordOf(Provenance);

    /// <summary>
    /// Muted second line: <c>version · provenance word</c>, or <c>version · Paused for this
    /// server</c> while per-server mod pause holds the plugin.
    /// </summary>
    internal string Subtitle => (Version.Length == 0 ? ModMenuInventory.VersionUnknown : Version)
        + " · " + (IsPaused ? ModMenuInventory.StatusPaused : ProvenanceWord);

    /// <summary>True while per-server mod pause holds this plugin (the inventory row says so).</summary>
    internal bool IsPaused => string.Equals(Inventory?.Status, ModMenuInventory.StatusPaused, StringComparison.Ordinal);

    /// <summary>
    /// The muted read-only note the row carries on its page: another tool's file, the loader, or
    /// one of Progenitor's own plugins. A Progenitor-installed mod has none — it offers Remove.
    /// </summary>
    internal string? PageNote => Provenance switch
    {
        ModProvenance.Other => ModMenuInventory.OtherToolNote,
        ModProvenance.Required => IsLoader ? ModMenuInventory.LoaderNote : ModMenuInventory.RequiredNote,
        ModProvenance.Reload => ModMenuInventory.ReloadNoteOf(Id),
        _ => null,
    };
}

/// <summary>
/// The Mods tab's decided sidebar: the default list, then the server-only group, then the
/// required group, with the counts the overview and <c>mods_ui</c> readback report.
/// Pure: built from strings, never from plugin instances.
/// </summary>
internal sealed class ModsSidebarModel
{
    internal IReadOnlyList<ModsSidebarRow> Rows { get; init; } = Array.Empty<ModsSidebarRow>();

    /// <summary>Rows that pass the search and are the player's own mods: not server-only, not required.</summary>
    internal int Shown { get; init; }

    /// <summary>MODS2: rows that pass the search and sit in the <c>Required (N)</c> group.</summary>
    internal int Required { get; init; }

    /// <summary>Rows that pass the search and are server-only.</summary>
    internal int Server { get; init; }

    /// <summary>Overview text: the default list only. The required rows are never counted here.</summary>
    internal string LocalSummary { get; init; } = "";

    /// <summary>The rows the sidebar draws in the mods group, in order.</summary>
    internal IEnumerable<ModsSidebarRow> VisibleModRows
    {
        get
        {
            foreach (var row in Rows)
                if (row.Matches
                    && row.ListProvenance != ModProvenance.Server
                    && row.ListProvenance != ModProvenance.Required) yield return row;
        }
    }

    /// <summary>The rows the collapsible <c>On this server</c> group draws, in order.</summary>
    internal IEnumerable<ModsSidebarRow> ServerRows
    {
        get
        {
            foreach (var row in Rows)
                if (row.Matches && row.ListProvenance == ModProvenance.Server) yield return row;
        }
    }

    /// <summary>
    /// MODS2: the rows the collapsed <c>Required (N)</c> group draws, in order — the loader first,
    /// then Core, then the rest of this framework's plugins. The group is the last one in the
    /// sidebar (decided 2026-09-13: players are not looking for these).
    /// </summary>
    internal IEnumerable<ModsSidebarRow> RequiredRows
    {
        get
        {
            foreach (var row in Rows)
                if (row.Matches && row.Provenance == ModProvenance.Required) yield return row;
        }
    }

    internal ModsSidebarRow? Find(string? id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        foreach (var row in Rows)
            if (string.Equals(row.Id, id, StringComparison.OrdinalIgnoreCase)) return row;
        return null;
    }
}

internal sealed class ModMenuInventorySnapshot
{
    internal IReadOnlyList<ModMenuInventoryRow> Rows { get; init; } = Array.Empty<ModMenuInventoryRow>();
    internal string ServerSummary { get; init; } = "";
    internal string LocalSummary { get; init; } = "";
}

internal readonly struct ModMenuLocalObservation
{
    internal ModMenuLocalObservation(string id, string name, string version, bool instancePresent)
    { Id = id; Name = name; Version = version; InstancePresent = instancePresent; }
    internal string Id { get; }
    internal string Name { get; }
    internal string Version { get; }
    // False means a currently reload-owned assembly, not merely a declaration or a disk file.
    internal bool InstancePresent { get; }
}

/// <summary>
/// Read-only, current evidence for the client Mods directory. No file scan, transfer or
/// lifecycle action. Snapshots retain strings only, never plugin instances or assemblies.
/// Server-advertised rows (handshake requirements, catalog offers) live in one owned
/// context that is cleared on disconnect / world-epoch change and restored on the next
/// successful handshake. The context never stores a raw entity id.
/// </summary>
internal static class ModMenuInventory
{
    internal const string CoreId = "progenitor";

    /// <summary>FWALLOW1: the dev bridge's plugin GUID (<c>BepInExPluginGuid</c> in <c>src/Progenitor.Dev</c>).</summary>
    internal const string DevId = "progenitor.dev";

    /// <summary>FWALLOW1: the preloader patcher's GUID (<c>Progenitor.Preloader.Patcher.Guid</c>).</summary>
    internal const string PreloaderId = "progenitor.preloader";

    internal const string ScratchId = "progenitor.scratch";
    internal const int MaxRows = 512;
    internal const string ContextLogPrefix = "Mods inventory: server context ";
    internal const string StalePacketLogPrefix = "Mods inventory: ignored stale server packet epoch=";
    internal const string ServerListAvailable = "available";
    internal const string ServerListUnavailable = "unavailable";
    internal const string ServerListUnknown = "unknown";

    /// <summary>Per-server mod pause: the status a paused plugin's row shows.</summary>
    internal const string StatusPaused = "Paused for this server";

    /// <summary>Per-server mod pause: a plugin released on leaving that has not loaded again yet.</summary>
    internal const string StatusResuming = "Resuming…";

    /// <summary>The explanation under <see cref="StatusResuming"/>.</summary>
    internal const string ExplanationResuming =
        "Released from its pause; it loads again as soon as its previous copy has been cleaned up.";

#pragma warning disable CS0649 // assigned by the test project only
    /// <summary>Tests only: the resuming plugins <see cref="Snapshot"/> reports.</summary>
    internal static IReadOnlyList<ModMenuLocalObservation>? TestResuming;
#pragma warning restore CS0649

    /// <summary>Plugins released from the pause that have not loaded yet.</summary>
    internal static IReadOnlyList<ModMenuLocalObservation> ResumingObservations()
    {
#if PROGENITOR_TESTS
        if (TestResuming != null) return TestResuming;
#endif
        var list = new List<ModMenuLocalObservation>();
        try
        {
            foreach (var id in ServerModPause.Current.ResumingIds) list.Add(new(id, id, "", instancePresent: false));
        }
        catch { /* evidence, not a gate */ }
        return list;
    }

    /// <summary>SETTINGS1 stage 3: a mod the player turned off with the switch.</summary>
    internal const string StatusDisabled = "Disabled";

    /// <summary>The explanation under <see cref="StatusDisabled"/>.</summary>
    internal const string ExplanationDisabled = "Turned off in the Mods menu. Turn it on from its page.";

#pragma warning disable CS0649 // assigned by the test project only
    /// <summary>Tests only: the turned-off plugins <see cref="Snapshot"/> reports.</summary>
    internal static IReadOnlyList<ModMenuLocalObservation>? TestDisabled;
#pragma warning restore CS0649

    /// <summary>
    /// SETTINGS1 stage 3: the plugins the player's switch holds off — unloaded by a live switch (in
    /// Bootstrap's pause table), loaded but skipped at this start, or an ordinary plugin the preloader
    /// renamed off. Every one of them reads <see cref="StatusDisabled"/>, and the last kind is listed
    /// at all only because of this (nothing has it loaded).
    /// </summary>
    internal static IReadOnlyList<ModMenuLocalObservation> DisabledObservations()
    {
#if PROGENITOR_TESTS
        if (TestDisabled != null) return TestDisabled;
#endif
        var list = new List<ModMenuLocalObservation>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var backend = ModSwitch.Backend();
            if (backend.Supported)
                foreach (var p in backend.Suspended())
                    if (ModSwitch.IsHeld(p.Guid) && seen.Add(p.Guid)) list.Add(new(p.Guid, p.Name, p.Version, instancePresent: false));
        }
        catch { /* evidence, not a gate */ }
        try
        {
            foreach (var e in ModRegistry.All())
                if (e.Outcome == ModLoadOutcome.SkippedOff && seen.Add(e.Id)) list.Add(new(e.Id, e.Name, e.Version, instancePresent: true));
        }
        catch { /* evidence, not a gate */ }
        try
        {
            var state = ModSwitch.PlainState();
            string root = ModSwitch.BepInExRoot?.Invoke() ?? "";
            if (root.Length > 0)
                foreach (var r in Progenitor.PluginActions.PluginFileActions.DisabledOnDisk(root, state))
                    if (seen.Add(r.PluginId)) list.Add(new(r.PluginId, r.Name, "", instancePresent: false));
        }
        catch { /* evidence, not a gate */ }
        return list;
    }

    /// <summary>Per-server mod pause: the explanation under that status.</summary>
    internal const string ExplanationPaused =
        "Paused while you are on this server, which does not run it. It comes back when you leave.";

    /// <summary>Tests only: the paused plugins <see cref="Snapshot"/> reports.</summary>
#pragma warning disable CS0649 // assigned by the test project only
    internal static IReadOnlyList<ModMenuLocalObservation>? TestPaused;
#pragma warning restore CS0649

    /// <summary>
    /// The plugins per-server mod pause holds. They are unloaded, so neither the chainloader nor the
    /// reloader lists them; Bootstrap's paused set is the only source.
    /// </summary>
    internal static IReadOnlyList<ModMenuLocalObservation> PausedObservations()
    {
#if PROGENITOR_TESTS
        if (TestPaused != null) return TestPaused;
#endif
        var list = new List<ModMenuLocalObservation>();
        try
        {
            var state = ServerModPause.Current;
            for (int i = 0; i < state.PausedIds.Count; i++)
                list.Add(new(state.PausedIds[i], i < state.PausedNames.Count ? state.PausedNames[i] : state.PausedIds[i], "", instancePresent: false));
        }
        catch { /* evidence, not a gate */ }
        return list;
    }

    private static readonly object Gate = new();
    private static ServerContext? _server;
    private static int _serverListEpoch;

    // HOT3 defect 3: a snapshot is a read of what is loaded right now, and every snapshot is taken
    // fresh. What was missing is the record of an owed retake: a plugin the reloader loaded (or
    // unloaded) while the workspace stood open changes the list under a tab that was already drawn.
    // Invalidate marks the retake owed; Snapshot is the retake and clears it.
    private static bool _refreshOwed;
    private static int _snapshots;
    private static int _generation;

    /// <summary>
    /// HOSTS1 round 6: bumped by every change the Mods list is drawn from that this class sees (an owed
    /// retake, the server context set, changed or cleared). The kept Mods list is reused only while this
    /// has not moved (<see cref="ModsListKey"/>), so no snapshot is taken to decide.
    /// </summary>
    internal static int Generation
    {
        get { lock (Gate) return _generation; }
    }

    /// <summary>HOT3: reasons the inventory list is known to be behind what this process has loaded.</summary>
    internal const string RefreshReasonHotLoaded = "hot-loaded";

    /// <inheritdoc cref="RefreshReasonHotLoaded"/>
    internal const string RefreshReasonHotUnloaded = "hot-unloaded";

    /// <inheritdoc cref="RefreshReasonHotLoaded"/>
    internal const string RefreshReasonTabSelect = "tab-select";

    /// <inheritdoc cref="RefreshReasonHotLoaded"/>
    internal const string RefreshReasonServerContext = "server-context";

    /// <summary>F2/F7: a Mods-page action (uninstall, cancel, the switch) changed what is installed or loaded.</summary>
    internal const string RefreshReasonPageAction = "page-action";

    /// <summary>True while a snapshot is owed: something changed after the last one was taken.</summary>
    internal static bool RefreshOwed
    {
        get { lock (Gate) return _refreshOwed; }
    }

    /// <summary>How many snapshots have been taken. The readback proof that a retake happened.</summary>
    internal static int SnapshotsTaken
    {
        get { lock (Gate) return _snapshots; }
    }

    /// <summary>
    /// HOT3: record that the loaded set changed, so the next tab build's <see cref="Snapshot"/> is
    /// known to be the retake. Cheap and never throws: it sets one flag.
    /// </summary>
    /// <summary>
    /// HOSTS1 round 9: raised (outside the lock) after <see cref="Generation"/> moved, so a kept Mods list
    /// can be rebuilt on an idle frame instead of at the next open.
    /// </summary>
    internal static event Action? GenerationChanged;

    private static void RaiseGenerationChanged()
    {
        try { GenerationChanged?.Invoke(); }
        catch { /* a listener must not break the inventory */ }
    }

    internal static void Invalidate(string reason)
    {
        lock (Gate) { _refreshOwed = true; _generation++; }
        RaiseGenerationChanged();
        try { Plugin.Logger?.LogInfo("Mods inventory: refresh owed (" + (string.IsNullOrWhiteSpace(reason) ? "unknown" : reason.Trim()) + ")."); }
        catch { /* a log is not a gate */ }
    }


    /// <summary>A row's requirement when the server's handshake request did not name the mod (or named an unknown level).</summary>
    internal const string RequirementUnknown = "Server requirement unknown";

    /// <summary>True for <see cref="RequirementUnknown"/>: no evidence, which the mod page does not print.</summary>
    internal static bool IsUnknownRequirement(string? requirement) =>
        string.IsNullOrWhiteSpace(requirement) || string.Equals(requirement.Trim(), RequirementUnknown, StringComparison.Ordinal);

    internal static bool ServerContextLive
    {
        get { lock (Gate) return _server != null; }
    }

    /// <summary>
    /// Structured server-list fact for <c>mods_ui</c> status. Human overview text stays on
    /// <see cref="ModMenuInventorySnapshot.ServerSummary"/>.
    /// </summary>
    internal static ServerListStatus DescribeServerList()
    {
        try
        {
            lock (Gate)
            {
                if (_server == null)
                    return new ServerListStatus(ServerListUnavailable, 0, _serverListEpoch);
                var catalog = _server.Catalog;
                if (catalog == null)
                    return new ServerListStatus(ServerListUnavailable, 0, _server.Epoch);
                return new ServerListStatus(ServerListAvailable, catalog.Body.Mods.Count, _server.Epoch);
            }
        }
        catch
        {
            return new ServerListStatus(ServerListUnknown, 0, 0);
        }
    }

    internal static ModMenuInventorySnapshot Snapshot()
    {
        // HOT3 defect 3: this call IS the retake, so nothing is owed after it.
        lock (Gate) { _refreshOwed = false; _snapshots++; }
        MaybeAdoptHandshakeResult();
#if PROGENITOR_TESTS
        return Build(Array.Empty<ModMenuLocalObservation>(), Handshake.CopyProvided(), CurrentAsked(),
            CurrentCatalog(), "", paused: PausedObservations(), resuming: ResumingObservations(), disabled: DisabledObservations());
#else
        var local = new List<ModMenuLocalObservation>();
        bool partial = false;
        // Do not use ModManifest.EnumerateLoaded: it includes declaration-only fallback rows.
        try
        {
            var plugins = IL2CPPChainloader.Instance?.Plugins;
            if (plugins != null)
                foreach (var pair in plugins)
                {
                    var info = pair.Value;
                    if (info?.Metadata == null || info.Instance == null) continue;
                    var meta = info.Metadata;
                    local.Add(new(meta.GUID, meta.Name, meta.Version?.ToString() ?? "", instancePresent: true));
                    if (local.Count >= MaxRows) { partial = true; break; }
                }
        }
        catch { partial = true; }
        try
        {
            // An unloaded collectible assembly can remain in AppDomain until GC. Require
            // current reloader ownership, which is removed at unload, before inspecting it.
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            int visited = 0;
            foreach (var assembly in assemblies)
            {
                if (++visited > 2048 || local.Count >= MaxRows) { partial = true; break; }
                if (!Reload.PluginReloader.TryGetSourcePath(assembly, out _)) continue;
                Type?[] types;
                try { types = assembly.GetTypes(); }
                catch (ReflectionTypeLoadException e) { types = e.Types; partial = true; }
                foreach (var type in types)
                {
                    if (type == null || type.IsAbstract || !typeof(BasePlugin).IsAssignableFrom(type)) continue;
                    var meta = type.GetCustomAttribute<BepInPlugin>(inherit: false);
                    if (meta == null) continue;
                    local.Add(new(meta.GUID, meta.Name, meta.Version?.ToString() ?? "", instancePresent: false));
                    if (local.Count >= MaxRows) { partial = true; break; }
                }
            }
        }
        catch { partial = true; }
        return Build(local, Handshake.CopyProvided(), CurrentAsked(), CurrentCatalog(),
            typeof(ModMenuInventory).Assembly.GetName().Version?.ToString() ?? "", partial, PausedObservations(), ResumingObservations(),
            DisabledObservations());
#endif
    }

    /// <summary>
    /// Drop advertised server evidence. Idempotent: a second clear without a restore does not log.
    /// </summary>
    internal static bool ClearServerContext(string reason, int epoch = 0)
    {
        lock (Gate)
        {
            if (_server == null) return false;
            _serverListEpoch = epoch != 0 ? epoch : _server.Epoch;
            _server = null;
            _refreshOwed = true;
            _generation++;
        }
        RaiseGenerationChanged();
        LogContext("cleared", reason, epoch);
        return true;
    }

    /// <summary>
    /// Repopulate from the current handshake request and catalog. Idempotent while already live:
    /// later packets may refresh the copies, but they do not log a second restore.
    /// </summary>
    /// <summary>The server's asked rows are the same list (guid, version, level, rule, declared, in order).</summary>
    internal static bool SameRows(IReadOnlyList<Handshake.ModRecord>? a, IReadOnlyList<Handshake.ModRecord>? b)
    {
        int na = a?.Count ?? 0, nb = b?.Count ?? 0;
        if (na != nb) return false;
        for (int i = 0; i < na; i++)
        {
            var x = a![i];
            var y = b![i];
            if (!string.Equals(x.Guid, y.Guid, StringComparison.Ordinal)
                || !string.Equals(x.Version, y.Version, StringComparison.Ordinal)
                || x.Level != y.Level || x.VersionRule != y.VersionRule || x.Declared != y.Declared)
                return false;
        }

        return true;
    }

    internal static bool RestoreServerContext(string reason, int epoch = 0) =>
        RestoreServerContext(reason, Handshake.CopyServerAsked(), ModManifest.LastEnvelope, epoch);

    internal static bool RestoreServerContext(
        string reason,
        IReadOnlyList<Handshake.ModRecord>? asked,
        ManifestEnvelope? catalog,
        int epoch = 0)
    {
        if (!Handshake.AcceptsIncomingServerPacket(Handshake.IncomingPacketEpoch))
            return false;
        var rows = asked ?? Array.Empty<Handshake.ModRecord>();
        int stamp = epoch != 0 ? epoch : WorldEpoch.Current;
        bool restored;
        int before;
        lock (Gate)
        {
            before = _generation;
            // HOSTS1 round 7: every Snapshot re-adopts the handshake (MaybeAdoptHandshakeResult), so the
            // generation the kept Mods list is keyed on moves only when this actually changed anything;
            // round 6 bumped it on every snapshot, and every reopen rebuilt the list.
            if (_server != null)
            {
                bool changed = _server.Epoch != stamp
                    || (catalog != null && !ReferenceEquals(_server.Catalog, catalog))
                    || !SameRows(_server.Asked, rows);
                _server.Asked = rows;
                if (catalog != null) _server.Catalog = catalog;
                _server.Epoch = stamp;
                _serverListEpoch = stamp;
                if (changed) _generation++;
                restored = false;
            }
            else
            {
                _server = new ServerContext { Asked = rows, Catalog = catalog, Epoch = stamp };
                _serverListEpoch = stamp;
                _generation++;
                restored = true;
            }
        }
        bool moved;
        lock (Gate) moved = _generation != before;
        if (moved) RaiseGenerationChanged();
        if (restored) LogContext("restored", reason, stamp);
        return restored;
    }

#if PROGENITOR_TESTS
    internal static void ResetForTests()
    {
        lock (Gate)
        {
            _server = null;
            _serverListEpoch = 0;
            _refreshOwed = false;
            _snapshots = 0;
        }
        TestPaused = null;
        TestResuming = null;
    }
#endif

    private static Handshake.ModRecord[] CurrentAsked()
    {
        lock (Gate)
        {
            if (_server == null) return Array.Empty<Handshake.ModRecord>();
            var live = Handshake.CopyServerAsked();
            if (live.Length > 0) _server.Asked = live;
            return ToArray(_server.Asked);
        }
    }

    private static ManifestEnvelope? CurrentCatalog()
    {
        lock (Gate)
        {
            if (_server == null) return null;
            if (_server.Catalog == null)
            {
                var live = ModManifest.LastEnvelope;
                if (live != null) _server.Catalog = live;
            }
            return _server.Catalog;
        }
    }

    private static Handshake.ModRecord[] ToArray(IReadOnlyList<Handshake.ModRecord> rows)
    {
        if (rows is Handshake.ModRecord[] array) return array;
        var copy = new Handshake.ModRecord[rows.Count];
        for (int i = 0; i < rows.Count; i++) copy[i] = rows[i];
        return copy;
    }

    private static void MaybeAdoptHandshakeResult()
    {
#if !PROGENITOR_TESTS
        // LastClientResult is this connection's handshake outcome. Invalidate nulls it on
        // disconnect so a later Snapshot cannot revive the previous server's catalog.
        string? result = Handshake.LastClientResult;
        if (string.Equals(result, "ok", StringComparison.Ordinal))
            RestoreServerContext("handshake-ok");
        else
            ClearServerContext(string.IsNullOrEmpty(result) ? "disconnect" : "handshake-ended");
#endif
    }

    internal static void LogStalePacket(int oldEpoch, int current)
    {
        string line = StalePacketLogPrefix
            + oldEpoch.ToString(CultureInfo.InvariantCulture)
            + " current=" + current.ToString(CultureInfo.InvariantCulture);
        try { Plugin.Logger?.LogInfo(line); }
        catch { /* a logger that throws must not pin server evidence */ }
    }

    private static void LogContext(string action, string reason, int epoch)
    {
        int stamp = epoch != 0 ? epoch : WorldEpoch.Current;
        string line = ContextLogPrefix + action + " reason=" + (reason ?? "")
            + " epoch=" + stamp.ToString(CultureInfo.InvariantCulture);
        try { Plugin.Logger?.LogInfo(line); }
        catch { /* a logger that throws must not pin server evidence */ }
    }

    internal readonly struct ServerListStatus
    {
        internal ServerListStatus(string state, int count, int epoch)
        {
            State = state;
            Count = count;
            Epoch = epoch;
        }
        internal string State { get; }
        internal int Count { get; }
        internal int Epoch { get; }
    }

    private sealed class ServerContext
    {
        internal IReadOnlyList<Handshake.ModRecord> Asked = Array.Empty<Handshake.ModRecord>();
        internal ManifestEnvelope? Catalog;
        internal int Epoch;
    }

    // Game-free composition: server catalog claims never become local observations or
    // authoritative handshake requirements. This is also the test boundary.
    internal static ModMenuInventorySnapshot Build(IReadOnlyList<ModMenuLocalObservation> local,
        IReadOnlyList<Handshake.ModRecord> declared, IReadOnlyList<Handshake.ModRecord> serverAsked,
        ManifestEnvelope? catalog, string coreVersion, bool localPartial = false,
        IReadOnlyList<ModMenuLocalObservation>? paused = null,
        IReadOnlyList<ModMenuLocalObservation>? resuming = null,
        IReadOnlyList<ModMenuLocalObservation>? disabled = null)
    {
        var facts = new Dictionary<string, Fact>(StringComparer.OrdinalIgnoreCase);
        bool capped = false;
        Fact? Get(string? id)
        {
            if (string.IsNullOrWhiteSpace(id) || id.Length > 256 || IsExcluded(id)) return null;
            if (facts.TryGetValue(id, out var existing)) return existing;
            if (facts.Count >= MaxRows) { capped = true; return null; }
            var fact = new Fact { Id = id, Name = id };
            facts.Add(id, fact);
            return fact;
        }
        // Core must remain discoverable even if another source fills the bounded inventory.
        var core = Get(CoreId)!;
        core.Name = "Progenitor";
        core.LocalVersion = Clean(coreVersion);
        core.LocalRank = 3;
        foreach (var observed in local)
        {
            var fact = Get(observed.Id);
            if (fact == null) continue;
            int rank = observed.InstancePresent ? 2 : 1;
            if (rank < fact.LocalRank) continue;
            fact.LocalRank = rank;
            fact.Name = string.IsNullOrWhiteSpace(observed.Name) ? fact.Id : Clean(observed.Name);
            fact.LocalVersion = Clean(observed.Version);
        }
        if (paused != null)
            foreach (var p in paused)
            {
                var fact = Get(p.Id);
                if (fact == null || fact.LocalRank >= 2) continue; // a live instance wins over a stale pause row
                fact.Paused = true;
                fact.LocalRank = Math.Max(fact.LocalRank, 1);
                if (!string.IsNullOrWhiteSpace(p.Name)) fact.Name = Clean(p.Name);
                if (p.Version.Length > 0) fact.LocalVersion = Clean(p.Version);
            }
        if (resuming != null)
            foreach (var r in resuming)
            {
                var fact = Get(r.Id);
                if (fact == null || fact.LocalRank >= 2 || fact.Paused) continue;
                fact.Resuming = true;
                fact.LocalRank = Math.Max(fact.LocalRank, 1);
            }
        if (disabled != null)
            foreach (var d in disabled)
            {
                var fact = Get(d.Id);
                if (fact == null || fact.LocalRank == 3) continue;
                fact.Disabled = true;
                fact.Paused = false;
                fact.Resuming = false;
                fact.LocalRank = Math.Max(fact.LocalRank, 1);
                if (!string.IsNullOrWhiteSpace(d.Name) && string.Equals(fact.Name, fact.Id, StringComparison.OrdinalIgnoreCase))
                    fact.Name = Clean(d.Name);
                if (fact.LocalVersion.Length == 0 && d.Version.Length > 0) fact.LocalVersion = Clean(d.Version);
            }
        foreach (var declaration in declared)
        {
            var fact = Get(declaration.Guid);
            if (fact == null) continue;
            fact.LocalDeclaration = true;
            if (fact.LocalRank == 0) fact.LocalVersion = Clean(declaration.Version);
        }
        if (catalog != null)
            foreach (var item in catalog.Body.Mods)
            {
                var fact = Get(item.Id);
                if (fact == null) continue;
                fact.Catalog = true;
                fact.ServerVersion = Clean(item.Version);
                // Catalog Level can be overridden independently of the actual handshake.
            }
        foreach (var required in serverAsked)
        {
            var fact = Get(required.Guid);
            if (fact == null) continue;
            fact.Handshake = true;
            fact.ServerVersion = Clean(required.Version);
            fact.Requirement = required.Level switch
            {
                NetworkCompatibilityLevel.ServerMustHave => "Required by server",
                NetworkCompatibilityLevel.NoNeedForSync => "Optional on this server",
                NetworkCompatibilityLevel.ClientOnly => "Not required by server",
                _ => RequirementUnknown,
            };
        }
        var rows = new List<ModMenuInventoryRow>();
        int observedCount = 0;
        int reportedCount = 0;
        foreach (var fact in facts.Values)
        {
            rows.Add(Render(fact));
            if (fact.LocalRank > 0) observedCount++;
            else if (fact.LocalDeclaration) reportedCount++;
        }
        rows.Sort((a, b) =>
        {
            int name = StringComparer.OrdinalIgnoreCase.Compare(a.DisplayName, b.DisplayName);
            return name != 0 ? name : StringComparer.OrdinalIgnoreCase.Compare(a.Id, b.Id);
        });
        return new()
        {
            Rows = rows,
            ServerSummary = catalog == null
                ? "Server mod list unavailable. Known requirements are shown where available."
                : "Server mod list received (" + catalog.Body.Mods.Count + " entries). Server activity is not reported.",
            LocalSummary = observedCount + (observedCount == 1 ? " mod found on this client." : " mods found on this client.")
                + (reportedCount > 0 ? " " + reportedCount + " more reported; loading is unconfirmed." : "")
                + (localPartial || capped ? " The list is partial." : ""),
        };
    }

    internal static bool IsExcluded(string id) => string.Equals(id, ScratchId, StringComparison.OrdinalIgnoreCase)
        || Handshake.Compare.IsReservedGuid(id);

    // ---- MODS1: provenance ---------------------------------------------------------------

    internal const string GroupMods = "mods";
    internal const string GroupServer = "server";

    /// <summary>MODS2: the collapsed group that holds the loader and this framework's own plugins.</summary>
    internal const string GroupRequired = "required";
    internal const string VersionUnknown = "Version unknown";

    /// <summary>
    /// SRVUNINST1 (live 2026-10-07: "Uninstall progenitor.reloadexample Server 0.1.0?", "vServer 0.1.0"): the bare
    /// version inside an inventory row's display version — <c>Server 0.1.0</c> → <c>0.1.0</c>, <c>Local 1.0; server 1.1</c>
    /// → <c>1.0</c> (this client's), <see cref="VersionUnknown"/> → empty. Anything else is returned trimmed.
    /// </summary>
    internal static string BareVersion(string? display)
    {
        string v = (display ?? "").Trim();
        if (v.Length == 0 || string.Equals(v, VersionUnknown, StringComparison.Ordinal)) return "";
        if (v.StartsWith("Server ", StringComparison.Ordinal)) return v.Substring(7).Trim();
        if (v.StartsWith("Local ", StringComparison.Ordinal))
        {
            int semi = v.IndexOf(';');
            return (semi > 0 ? v.Substring(6, semi - 6) : v.Substring(6)).Trim();
        }

        return v;
    }
    internal const string OtherToolNote = "Installed by another tool — remove it with that tool.";
    internal const string ServerGroupCaption = "On this server";

    /// <summary>MODS2: caption of the last sidebar group, drawn as <c>Required (N)</c>.</summary>
    internal const string RequiredGroupCaption = "Required";

    /// <summary>
    /// MODS2: the loader's sidebar id. It is not a BepInEx plugin GUID — nothing loads under this
    /// id — so it can never collide with an inventory row or a settings-catalog plugin id.
    /// </summary>
    internal const string LoaderId = "bepinex";
    internal const string LoaderName = "BepInEx";

    /// <summary>The loader's whole page: it is not a plugin, so there is nothing else to say.</summary>
    internal const string LoaderNote = "The plugin loader. Required by every mod; not managed here.";

    /// <summary>Every other required row's page note.</summary>
    internal const string RequiredNote = "Part of Progenitor. Required; not removable from this menu.";

    /// <summary>
    /// FWALLOW1 (user, 2026-10-06): Progenitor's own framework plugins, as an explicit allowlist —
    /// Core (whose GUID Bootstrap also carries: <c>BepInExPluginGuid</c> in both projects), the dev
    /// bridge and the preloader patcher. Exactly these rows are required: read-only, no switch, no
    /// Uninstall. Panes must ask <see cref="IsFrameworkId"/> instead of naming ids of their own.
    ///
    /// <para><b>Not a namespace prefix.</b> Before FWALLOW1 every id under <c>progenitor.</c>
    /// was a framework id, so the samples (<c>progenitor.sampleserverled</c>,
    /// <c>.sample</c>, <c>.samplecompanion</c>, ...), examples, apivalidation and test plugins sat
    /// in <c>Required by Progenitor</c> with "Part of Progenitor. Required; not removable from this
    /// menu." and could not be turned off or removed. They are ordinary mods now
    /// (<see cref="ModProvenance.Reload"/> when their file is in the reload folder). MODSPOLISH1's
    /// ledger-first rule (<see cref="IsLedgeredMod"/>) still applies, and is still asked first.</para>
    ///
    /// <para>Kept in step with <see cref="Progenitor.Plugins.ModEnableState.RequiredIds"/> (the switch's
    /// refusal list, which adds the loader's <c>bepinex</c>) and with the handshake's own list
    /// (<c>ClientModJudge.ProgenitorIds</c>), neither of which reads this one; a test pins that they
    /// agree.</para>
    /// </summary>
    internal static readonly string[] FrameworkIds = { CoreId, DevId, PreloaderId };

    /// <summary>FWALLOW1: the GUID namespace this project's plugins use. Not a framework test: see <see cref="IsFrameworkId"/>.</summary>
    internal const string ProgenitorNamespace = "progenitor";

    /// <summary>
    /// MODSPOLISH1: does the install ledger list this plugin as one of the <b>player's</b> mods —
    /// a promoted <c>mod</c> row, or the <c>pending</c> download that becomes one — on either root?
    /// Such a row is a Progenitor install with Remove/Uninstall and is never a required row,
    /// whatever its id spells. This framework's own images are ledgered <c>framework</c>, so they
    /// keep the <see cref="ModProvenance.Required"/> group they had.
    /// </summary>
    /// <summary>F3: every row is the local server folder's ledger's.</summary>
    private static bool AllOnServerRoot(IReadOnlyList<Install.InstalledLedgerRow> rows)
    {
        for (int i = 0; i < rows.Count; i++)
            if (!string.Equals(rows[i].Root, Install.InstalledState.ServerRoot, StringComparison.OrdinalIgnoreCase)) return false;
        return rows.Count > 0;
    }

    internal static bool IsLedgeredMod(IReadOnlyList<Install.InstalledLedgerRow>? rows)
    {
        if (rows == null) return false;
        for (int i = 0; i < rows.Count; i++)
            if (IsModLedgerRow(rows[i])) return true;
        return false;
    }

    /// <summary>One ledger row that installed a player's mod rather than a framework image.</summary>
    internal static bool IsModLedgerRow(Install.InstalledLedgerRow row) =>
        row.Source.Length > 0
        && (string.Equals(row.Category, Install.ServerInstallLedger.CategoryMod, StringComparison.OrdinalIgnoreCase)
            || string.Equals(row.Category, Install.ServerInstallLedger.CategoryPending, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Is <paramref name="id"/> one of this framework's own plugins rather than a player's mod? An
    /// exact match against <see cref="FrameworkIds"/> (case-insensitive); an id that merely sits
    /// under the <c>progenitor</c> namespace is not one (FWALLOW1).
    /// </summary>
    internal static bool IsFrameworkId(string? id)
    {
        string value = (id ?? "").Trim();
        if (value.Length == 0) return false;
        for (int i = 0; i < FrameworkIds.Length; i++)
            if (string.Equals(value, FrameworkIds[i], StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>
    /// FWALLOW1: one of the sample or example mods shipped with Progenitor's source — an id under
    /// <c>progenitor.sample*</c>, or any id that contains <c>example</c>.
    /// </summary>
    internal static bool IsShippedExampleId(string? id)
    {
        string value = (id ?? "").Trim();
        if (value.Length == 0 || IsFrameworkId(value)) return false;
        if (value.StartsWith(ProgenitorNamespace + ".sample", StringComparison.OrdinalIgnoreCase)) return true;
        return value.IndexOf("example", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>FWALLOW1: the provenance line of a sample or example in the reload folder.</summary>
    internal const string ExampleNote = "Example mod shipped with Progenitor's source.";

    /// <summary>FWALLOW1: the provenance line of any other plugin in the reload folder that no ledger lists.</summary>
    internal const string ReloadFolderNote = "Installed in BepInEx/progenitor-reload.";

    /// <summary>FWALLOW1: a <see cref="ModProvenance.Reload"/> row's provenance line. Never "Part of Progenitor".</summary>
    internal static string ReloadNoteOf(string? id) => IsShippedExampleId(id) ? ExampleNote : ReloadFolderNote;

    internal static string NameOf(ModProvenance provenance) => provenance switch
    {
        ModProvenance.Progenitor => "progenitor",
        ModProvenance.Required => "required",
        ModProvenance.Server => "server",
        ModProvenance.Reload => "reload",
        _ => "other",
    };

    /// <summary>The word the muted sidebar second line uses.</summary>
    internal static string WordOf(ModProvenance provenance) => provenance switch
    {
        ModProvenance.Progenitor => "Progenitor",
        ModProvenance.Required => "required",
        ModProvenance.Server => "on this server",
        ModProvenance.Reload => "progenitor-reload",
        _ => "another tool",
    };

    /// <summary>
    /// INSTORD2: the install-ledger rows, on either root, that name this plugin — by the package
    /// id Progenitor wrote into the row's source, or by the plugin file name it wrote. A ledger row
    /// is what survives a restart, so it decides <see cref="ModProvenance.Progenitor"/>.
    /// </summary>
    internal static IReadOnlyList<Install.InstalledLedgerRow> LedgerRowsForPlugin(
        IReadOnlyList<Install.InstalledLedgerRow>? ledger, string? id)
    {
        var rows = new List<Install.InstalledLedgerRow>();
        string value = (id ?? "").Trim();
        if (ledger == null || value.Length == 0) return rows;
        // MODSDUP1 (live 2026-10-07: "Progenitor Hot Demo 0.1.0" and a second "HotDemo"): the local test server's
        // ledger still held the pre-rename install (`<Vendor>-HotDemo`, pluginId and file `<vendor>.progenitor.hotdemo`),
        // which named no listed row, so it became a ledger-only row of its own. Plugin ids and file names are compared
        // with legacy vendor prefixes removed (LegacyIds), the same rule every other id reader applies.
        string current = Plugins.LegacyIds.Normalize(value);
        for (int i = 0; i < ledger.Count; i++)
        {
            var row = ledger[i];
            if (row.Source.Length == 0) continue;
            bool byId = string.Equals(row.Id, value, StringComparison.OrdinalIgnoreCase);
            // POLISH4 item 1: the row's own `pluginId` field. Progenitor now writes the *listing key*
            // in `source`, so a row for `Progenitor-HotDemo` no longer spells the loaded plugin's GUID
            // anywhere else; without this the sidebar would stop calling its own installs Progenitor's.
            bool byPluginId = row.PluginId.Length > 0
                && string.Equals(Plugins.LegacyIds.Normalize(row.PluginId), current, StringComparison.OrdinalIgnoreCase);
            bool byFile = row.FileName.Length > 0
                && string.Equals(Plugins.LegacyIds.Normalize(StripExtension(row.FileName)), current, StringComparison.OrdinalIgnoreCase);
            if (byId || byPluginId || byFile) rows.Add(row);
        }

        return rows;
    }

    private static string StripExtension(string fileName)
    {
        int dot = fileName.LastIndexOf('.');
        return dot > 0 ? fileName.Substring(0, dot) : fileName;
    }

    /// <summary>The last dotted segment of a plugin GUID: <c>gg.dyno.ExampleCommandFramework</c> → <c>ExampleCommandFramework</c>.</summary>
    private static string LastIdSegment(string id)
    {
        int dot = id.LastIndexOf('.');
        return dot > 0 && dot < id.Length - 1 ? id.Substring(dot + 1) : id;
    }

    /// <summary>The name half of a Thunderstore package key: <c>dyno-ExampleCommandFramework</c> → <c>ExampleCommandFramework</c>.</summary>
    private static string PackageNameHalf(string package)
    {
        int dash = package.LastIndexOf('-');
        return dash > 0 && dash < package.Length - 1 ? package.Substring(dash + 1) : package;
    }

    /// <summary>
    /// POLISH2 item 4: does a joined server's mod-list claim name a plugin these install-ledger rows
    /// already name? The claim carries the plugin GUID the server handed out
    /// (<c>gg.dyno.ExampleCommandFramework</c>); a ledger row carries the package key Progenitor
    /// installed (<c>dyno-ExampleCommandFramework</c>) and the plugin file it wrote
    /// (<c>ExampleCommandFramework.dll</c>), which is why <see cref="LedgerRowsForPlugin"/>'s exact
    /// comparison misses and the live sidebar drew one plugin as two rows — a Progenitor row named
    /// <c>ExampleCommandFramework</c> and a server row named <c>gg.dyno.ExampleCommandFramework</c>.
    ///
    /// <para>The match is <see cref="LedgerRowsForPlugin"/>'s rule first — the same row set every
    /// other decision uses — and then the claim's <em>last dotted segment</em> against the file name
    /// the ledger wrote or the package's name half, both case-insensitively. That segment is the
    /// assembly a BepInEx plugin ships under, so it is the one part of the GUID the installed file
    /// is named after; a bare id with no dot and a segment of one or two characters are never matched
    /// this way, so a short common word cannot fold two unrelated rows together.</para>
    /// </summary>
    internal static bool ClaimNamesLedgeredPlugin(
        IReadOnlyList<Install.InstalledLedgerRow>? rows, string? claimId)
    {
        string id = (claimId ?? "").Trim();
        if (rows == null || rows.Count == 0 || id.Length == 0) return false;
        if (LedgerRowsForPlugin(rows, id).Count > 0) return true;
        string leaf = LastIdSegment(id);
        if (leaf.Length < 3 || string.Equals(leaf, id, StringComparison.Ordinal)) return false;
        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (row.Source.Length == 0) continue;
            if (row.FileName.Length > 0
                && string.Equals(StripExtension(row.FileName), leaf, StringComparison.OrdinalIgnoreCase))
                return true;
            if (row.Id.Length > 0
                && string.Equals(PackageNameHalf(row.Id), leaf, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    internal static bool IsLocalLocation(string? location) =>
        !string.IsNullOrEmpty(location)
        && location!.StartsWith("Local", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// MODS1/MODS2: decide the Mods tab's sidebar. <paramref name="entries"/> is the id → display
    /// name list the tab already builds; <paramref name="inventory"/> the current snapshot rows;
    /// <paramref name="ledger"/> the install-ledger rows of both roots;
    /// <paramref name="loaderVersion"/> the BepInEx version the caller read from the chainloader.
    /// Search decides only <see cref="ModsSidebarRow.Matches"/>; nothing else hides a row —
    /// MODS2 retired the developer toggle, and this framework's own plugins are read-only rows in
    /// the <c>Required</c> group instead. <see cref="ModsSidebarModel.Rows"/> is in sidebar order:
    /// the player's mods, then the server-only rows, then the required rows.
    /// <see cref="ModsSidebarModel.Shown"/> and
    /// <see cref="ModsSidebarModel.LocalSummary"/> cover exactly the same rows — the ones that pass
    /// the search — so the overview sentence and <c>status.mods</c> can never disagree.
    /// </summary>
    internal static ModsSidebarModel BuildSidebar(
        IReadOnlyList<KeyValuePair<string, string>> entries,
        IReadOnlyList<ModMenuInventoryRow>? inventory,
        IReadOnlyList<Install.InstalledLedgerRow>? ledger,
        string? loaderVersion = null,
        Func<string, string, bool>? matches = null,
        IReadOnlyDictionary<string, string>? reloadFiles = null)
    {
        var byId = new Dictionary<string, ModMenuInventoryRow>(StringComparer.OrdinalIgnoreCase);
        if (inventory != null)
            foreach (var row in inventory)
                if (!string.IsNullOrEmpty(row.Id)) byId[row.Id] = row;

        var mods = new List<ModsSidebarRow>(entries?.Count ?? 0);
        var serverRows = new List<ModsSidebarRow>();
        var requiredRows = new List<ModsSidebarRow>();
        int shown = 0, required = 0, server = 0;

        // MODS2: the loader heads the required group. It is not a plugin, so it never appears in
        // `entries`; its row is synthesized here and nowhere else.
        bool loaderMatches = matches == null || matches(LoaderId, LoaderName);
        requiredRows.Add(new ModsSidebarRow
        {
            Id = LoaderId,
            Name = LoaderName,
            Version = Clean(loaderVersion),
            Provenance = ModProvenance.Required,
            Matches = loaderMatches,
        });
        if (loaderMatches) required++;

        if (entries != null)
            foreach (var entry in entries)
            {
                string id = entry.Key ?? "";
                if (id.Length == 0) continue;
                if (string.Equals(id, LoaderId, StringComparison.OrdinalIgnoreCase)) continue;
                string name = string.IsNullOrEmpty(entry.Value) ? id : entry.Value;
                byId.TryGetValue(id, out var inv);
                var ledgered = LedgerRowsForPlugin(ledger, id);
                ModProvenance provenance;
                // MODSPOLISH1: the ledger first. A plugin the install ledger lists as one of the
                // player's mods is a Progenitor install with Remove/Uninstall even when its GUID
                // sits under this framework's namespace (`progenitor.hotdemo`); only the
                // framework's own unledgered images fall through to the required group.
                // FWALLOW1: the framework test is an exact allowlist, not the namespace, so a sample
                // nothing ledgered falls through to the reload-folder rule below.
                string reloadFile = "";
                if (IsLedgeredMod(ledgered)) provenance = ModProvenance.Progenitor;
                else if (IsFrameworkId(id)) provenance = ModProvenance.Required;
                else if (ledgered.Count > 0) provenance = ModProvenance.Progenitor;
                else if (reloadFiles != null && reloadFiles.TryGetValue(id, out var file) && !string.IsNullOrEmpty(file))
                {
                    provenance = ModProvenance.Reload;
                    reloadFile = file;
                }
                else if (inv != null && !IsLocalLocation(inv.Location)) provenance = ModProvenance.Server;
                else provenance = ModProvenance.Other;

                bool match = matches == null || matches(id, name);
                var row = new ModsSidebarRow
                {
                    Id = id,
                    Name = name,
                    Version = inv?.Version ?? "",
                    Provenance = provenance,
                    Inventory = inv,
                    Ledger = provenance == ModProvenance.Progenitor
                        ? ledgered
                        : Array.Empty<Install.InstalledLedgerRow>(),
                    Matches = match,
                    ReloadFile = reloadFile,
                };

                switch (row.ListProvenance)
                {
                    case ModProvenance.Server:
                        serverRows.Add(row);
                        if (match) server++;
                        break;
                    case ModProvenance.Required:
                        // Core first, then the dev bridge and the preloader, in the entry order the
                        // tab already sorted by name.
                        if (string.Equals(id, CoreId, StringComparison.OrdinalIgnoreCase))
                            requiredRows.Insert(1, row);
                        else
                            requiredRows.Add(row);
                        if (match) required++;
                        break;
                    default:
                        mods.Add(row);
                        if (match) shown++;
                        break;
                }
            }

        // MODSPAGE1: a ledgered install that nothing has loaded yet is still one of the player's mods
        // — the page reports it as `Pending restart · to load`, and before this it was in no list at
        // all, because the sidebar was built from loaded plugins only. Appended after the loaded
        // rows, so the order of everything already listed is unchanged.
        foreach (var pending in LedgerOnlyRows(ledger, mods, serverRows, requiredRows))
        {
            // POLISH2 item 4: one plugin, one row. A joined server's claim for a plugin this ledger
            // already installed is folded into the ledger row — the ledger row is the one that can be
            // uninstalled, so it survives and gains the claim's requirement text, and
            // `On this server (N)` counts only the claims no install of ours explains.
            ModMenuInventoryRow? claim = null;
            for (int i = 0; i < serverRows.Count; i++)
            {
                if (!ClaimNamesLedgeredPlugin(pending.Ledger, serverRows[i].Id)) continue;
                claim = serverRows[i].Inventory;
                if (serverRows[i].Matches) server--;
                serverRows.RemoveAt(i);
                break;
            }

            bool match = matches == null
                || matches(pending.Id, pending.Name)
                || (claim != null && matches(claim.Id, claim.DisplayName ?? claim.Id));
            // F3 (live 2026-10-07: at the title, the local server folder's ExampleCommandFramework and AutoLatchGates were
            // listed among this client's installed mods): an install only the local server folder's ledger names, with no
            // joined server claiming it, is that server's; it is listed in the server group, which reads
            // "On your local server" while not connected. A joined server's claim keeps POLISH2's fold into the mods.
            bool localServerOnly = claim == null && AllOnServerRoot(pending.Ledger);
            (localServerOnly ? serverRows : mods).Add(new ModsSidebarRow
            {
                LocalServerOnly = localServerOnly,
                Id = pending.Id,
                Name = pending.Name,
                // The ledger's own version first; the folded claim's is what the server reported.
                Version = pending.Version.Length > 0 ? pending.Version : (claim?.Version ?? ""),
                Provenance = ModProvenance.Progenitor,
                Inventory = claim,
                Ledger = pending.Ledger,
                Matches = match,
            });
            if (match && localServerOnly) server++;
            else if (match) shown++;
        }

        var rows = new List<ModsSidebarRow>(mods.Count + serverRows.Count + requiredRows.Count);
        rows.AddRange(mods);
        rows.AddRange(serverRows);
        rows.AddRange(requiredRows);

        return new ModsSidebarModel
        {
            Rows = rows,
            Shown = shown,
            Required = required,
            Server = server,
            LocalSummary = SummaryText(shown),
        };
    }

    /// <summary>One ledgered install that no listed row names, as a sidebar row's worth of facts.</summary>
    internal readonly struct LedgerOnlyRow
    {
        internal LedgerOnlyRow(string id, string name, string version, IReadOnlyList<Install.InstalledLedgerRow> ledger)
        {
            Id = id;
            Name = name;
            Version = version;
            Ledger = ledger;
        }

        internal string Id { get; }
        internal string Name { get; }
        internal string Version { get; }
        internal IReadOnlyList<Install.InstalledLedgerRow> Ledger { get; }
    }

    /// <summary>
    /// MODSPAGE1: the install-ledger rows that no row in <paramref name="built"/> lists, grouped into
    /// one row each. Identity is the plugin file the ledger wrote (<c>&lt;pluginId&gt;.dll</c>), else
    /// the package in its source; this framework's own ids (<see cref="IsFrameworkId"/>, exact) and
    /// the scratch id are never listed here (they are required rows). A row without a source is not an install record.
    /// </summary>
    internal static IReadOnlyList<LedgerOnlyRow> LedgerOnlyRows(
        IReadOnlyList<Install.InstalledLedgerRow>? ledger,
        params IReadOnlyList<ModsSidebarRow>[] built)
    {
        var result = new List<LedgerOnlyRow>();
        if (ledger == null || ledger.Count == 0) return result;
        var byId = new Dictionary<string, List<Install.InstalledLedgerRow>>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        for (int i = 0; i < ledger.Count; i++)
        {
            var row = ledger[i];
            if (row.Source.Length == 0) continue;
            if (NamedByAny(built, row)) continue;
            // MODSDUP1: one id per plugin, so a pre-rename row and a current one group into one row.
            string id = Plugins.LegacyIds.Normalize(StripExtension(row.FileName).Trim());
            if (id.Length == 0) id = row.Id.Trim();
            if (id.Length == 0) continue;
            // MODSPOLISH1: the same order as BuildSidebar — a ledgered mod is the player's even
            // under this framework's namespace, so a not-yet-loaded HotDemo install is listed
            // rather than swallowed by the framework-id test.
            if (IsExcluded(id)) continue;
            if (IsFrameworkId(id) && !IsModLedgerRow(row)) continue;
            if (!byId.TryGetValue(id, out var list))
            {
                list = new List<Install.InstalledLedgerRow>();
                byId[id] = list;
                order.Add(id);
            }

            list.Add(row);
        }

        for (int i = 0; i < order.Count; i++)
        {
            var rows = byId[order[i]];
            string version = "";
            for (int j = 0; j < rows.Count && version.Length == 0; j++) version = rows[j].Version;
            result.Add(new LedgerOnlyRow(order[i], DisplayNameOfLedger(rows[0], order[i]), version, rows));
        }

        return result;
    }

    /// <summary>
    /// The name a ledger-only row shows: the package's own name half (<c>ExampleTeam-PhialStore</c>
    /// → <c>PhialStore</c>), else the plugin file's base name.
    /// </summary>
    internal static string DisplayNameOfLedger(Install.InstalledLedgerRow row, string fallback)
    {
        string package = row.Id.Trim();
        int dash = package.LastIndexOf('-');
        if (dash > 0 && dash < package.Length - 1) return package.Substring(dash + 1);
        return fallback;
    }

    private static bool NamedByAny(IReadOnlyList<ModsSidebarRow>[] built, Install.InstalledLedgerRow row)
    {
        if (built == null) return false;
        for (int i = 0; i < built.Length; i++)
        {
            var list = built[i];
            if (list == null) continue;
            for (int j = 0; j < list.Count; j++)
            {
                var candidate = LedgerRowsForPlugin(new[] { row }, list[j].Id);
                if (candidate.Count > 0) return true;
            }
        }

        return false;
    }

    private static string SummaryText(int shown) =>
        shown + (shown == 1 ? " mod found on this client." : " mods found on this client.");

    private static ModMenuInventoryRow Render(Fact fact)
    {
        bool local = fact.LocalRank > 0 || fact.LocalDeclaration;
        bool server = fact.Catalog || fact.Handshake;
        string status;
        string explanation;
        if (fact.LocalRank == 3)
        { status = "Running"; explanation = "This is the current Progenitor framework."; }
        else if (fact.Disabled)
        { status = StatusDisabled; explanation = ExplanationDisabled; }
        else if (fact.Paused)
        { status = StatusPaused; explanation = ExplanationPaused; }
        else if (fact.Resuming)
        { status = StatusResuming; explanation = ExplanationResuming; }
        else if (fact.LocalRank == 2)
        { status = "Loaded on this client"; explanation = "A plugin instance is present. Its activity has not been checked."; }
        else if (fact.LocalRank == 1)
        { status = "Loaded on this client"; explanation = "Activation status is unavailable."; }
        else if (fact.LocalDeclaration)
        { status = "Reported on this client"; explanation = "Installation and loading have not been confirmed."; }
        else
        { status = "Listed by server"; explanation = "No loaded client copy was observed."; }
        if (fact.Handshake) explanation += " The server supplied this mod's connection requirements.";
        if (fact.Catalog) explanation += " Server activity and download availability are unconfirmed.";
        string version = fact.LocalVersion;
        if (fact.ServerVersion.Length > 0)
            version = version.Length == 0 ? "Server " + fact.ServerVersion
                : string.Equals(version, fact.ServerVersion, StringComparison.OrdinalIgnoreCase) ? version
                : "Local " + version + "; server " + fact.ServerVersion;
        return new()
        {
            Id = fact.Id, DisplayName = fact.Name, Version = version.Length == 0 ? "Version unknown" : version,
            Location = local ? (server ? "Local + server list" : "Local") : "Server list",
            Status = status, Requirement = fact.Requirement, Explanation = explanation,
            LocalVersion = fact.LocalRank > 0 ? fact.LocalVersion : "",
            ServerAsked = fact.Handshake,
        };
    }

    private static string Clean(string? value)
    {
        string text = (value ?? "").Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ').Trim();
        return text.Length > 160 ? text.Substring(0, 157) + "..." : text;
    }

    private sealed class Fact
    {
        internal string Id = "";
        internal string Name = "";
        internal string LocalVersion = "";
        internal string ServerVersion = "";
        internal int LocalRank;
        internal bool LocalDeclaration;
        internal bool Catalog;
        internal bool Handshake;
        internal bool Paused;
        internal bool Resuming;
        internal bool Disabled;
        internal string Requirement = RequirementUnknown;
    }
}
