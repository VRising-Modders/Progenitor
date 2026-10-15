using System;
using System.Collections.Generic;
using System.Globalization;
using Progenitor.Install;
using Progenitor.Networking;
using Progenitor.PluginActions;
using Progenitor.Plugins;
#if !PROGENITOR_TESTS
using BepInEx;
using BepInEx.Unity.IL2CPP;
#endif

namespace Progenitor.Ui;

/// <summary>
/// SETTINGS1 stage 3 and MODLIST1: the on/off switch, mod problems, counterparts and the Mods list
/// redesign, as the Mods workspace wires them. The decisions are in <see cref="ModSwitch"/>,
/// <see cref="ModProblems"/>, <see cref="ModFrameworkLine"/> and <see cref="ModsListLayout"/>; this
/// file only gathers their evidence and binds their buttons.
/// </summary>
public static partial class ModsMenu
{
    /// <summary>The chosen filter chip of the Mods list (remembered while the workspace is open).</summary>
    private static ModsListFilter _listFilter = ModsListFilter.All;

    /// <summary>The <c>widget_read</c> extras of the list's own rows (entries, headings, summary, chips).</summary>
    private static readonly List<(IUiHandle Handle, string Key, Func<object?> Value)> _listExtras = new();

    /// <summary>The tag of the switch's confirmation.</summary>
    internal const string SwitchDialogTag = "modSwitch";

    /// <summary>The tag of the plain-plugin uninstall confirmation.</summary>
    internal const string PlainUninstallDialogTag = "modPlainUninstall";

    /// <summary>The tag of the <c>Show all</c> notice.</summary>
    internal const string ProblemsDialogTag = "modProblems";

    private static string _switchDialogPlugin = "";
    private static bool _stage3Hooks;
    private static bool _startResultsReconciled;

    /// <summary>
    /// SETTINGS1 stage 3 seam: plugin id → the identification of an ordinary BepInEx plugin's files.
    /// Production asks the chainloader (location, hard dependencies) and
    /// <see cref="PluginFileActions.Identify"/>; tests supply their own.
    /// </summary>
    internal static Func<string, PluginIdentification?>? PlainIdentityProbe { get; set; }

    /// <summary>The Mods list's filter (readback and tests).</summary>
    internal static ModsListFilter ListFilter => _listFilter;

    // ---- hooks --------------------------------------------------------------------------------

    /// <summary>Once per Core generation: the switch's BepInEx root, the problem names, the refreshes.</summary>
    private static void EnsureStage3Hooks()
    {
        if (_stage3Hooks) return;
        _stage3Hooks = true;
#if !PROGENITOR_TESTS
        ModSwitch.BepInExRoot ??= () => Paths.BepInExRootPath;
#endif
        KeybindConflictSource.NameOf ??= DisplayNameOf;
        ModSwitch.PlainDependantsProbe ??= PlainDependantsOfChainloader;
        InstallModsPane.PlainOfferProbe ??= PlainOfferOf;
        InstallModsPane.PlainActionHandler ??= OnInstallPlainAction;
        InstallModsPane.PlainUninstallNow ??= PlainUninstallNow;
        InstallModsPane.PlainUninstallSentence ??= PlainUninstallSentence;
        InstallModsPane.ShareInChat ??= OnInstallShare;
        // Review finding 1: a process start empties Bootstrap's pause table; stale holds go with it.
        try { ModSwitch.PruneStaleHolds(); }
        catch { /* evidence, never a gate */ }
        // Reviewer caveat: after a Core swap the chainloader's Progenitor plugins do not run Load
        // again; record what the swap left in memory so they are not taken for ordinary plugins.
        RehydrateChainloaderPlugins();
        ModProblems.Changed += OnStage3Changed;
        ModSwitch.Changed += OnStage3SwitchChanged;
        ModRegistry.Changed += OnStage3RegistryChanged;
    }

    /// <summary>
    /// Reviewer caveat (after a Core swap): the chainloader's Progenitor plugins (restart-only mods in
    /// <c>BepInEx/plugins</c>) are not loaded again, so this Core generation's <see cref="ModRegistry"/>
    /// never saw them and they would read as ordinary plugins. Each one the chainloader holds whose type
    /// derives from <c>ProgenitorPlugin&lt;T&gt;</c> (matched by name: the old Core's type is not this
    /// Core's) is recorded from what is left in memory: running when its context is attached, skipped
    /// when the player has it off, dormant for a side it does not run on, else failed. Strings only.
    /// </summary>
    private static void RehydrateChainloaderPlugins()
    {
#if !PROGENITOR_TESTS
        try
        {
            var plugins = IL2CPPChainloader.Instance?.Plugins;
            if (plugins == null) return;
            foreach (var pair in plugins)
            {
                var info = pair.Value;
                var instance = info?.Instance;
                if (info?.Metadata == null || instance == null) continue;
                string id = info.Metadata.GUID;
                if (ModRegistry.TryGet(id, out _)) continue;
                var type = instance.GetType();
                System.Type? baseType = type;
                while (baseType != null && !(baseType.IsGenericType
                    && baseType.GetGenericTypeDefinition().FullName == "Progenitor.Plugins.ProgenitorPlugin`1"))
                    baseType = baseType.BaseType;
                if (baseType == null) continue;

                object? context = null;
                try
                {
                    context = baseType.GetField("_context",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetValue(instance);
                }
                catch { context = null; }

                var deps = new List<string>();
                try
                {
                    foreach (var d in info.Dependencies)
                        if (d != null && (d.Flags & BepInDependency.DependencyFlags.HardDependency) != 0) deps.Add(d.DependencyGUID);
                }
                catch { /* judged without them */ }

                var outcome = RehydratedOutcome(context != null, ModEnableState.IsDisabled(id), RunsOnThisSide(type),
                    ModEnableState.BlockingDependency(id, deps, ModRegistry.IsSkipped), out string blocking);
                ModRegistry.NoteRehydrated(id, info.Metadata.Name, info.Metadata.Version?.ToString() ?? "", deps,
                    CounterpartByName(type), outcome, blocking,
                    CompatDeclaration.From(type, info.Metadata.Version?.ToString() ?? ""));
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Mods: chainloader plugins could not be read after the Core swap: " + e.Message);
        }
#endif
    }

    /// <summary>
    /// What a Core swap left of a chainloader plugin, as a load outcome: running with a context;
    /// skipped when the player has it off, or (review of 5a6e40f8, finding 10) when a hard dependency
    /// is off — kept as that, not read as a failure; dormant for a side it does not run on; else failed.
    /// </summary>
    internal static ModLoadOutcome RehydratedOutcome(
        bool hasContext, bool disabled, bool runsHere, string? blockingDependency, out string offDependency)
    {
        offDependency = "";
        if (hasContext) return ModLoadOutcome.Loaded;
        if (disabled) return ModLoadOutcome.SkippedOff;
        if (!runsHere) return ModLoadOutcome.Dormant;
        if (!string.IsNullOrWhiteSpace(blockingDependency))
        {
            offDependency = blockingDependency!.Trim();
            return ModLoadOutcome.SkippedOff;
        }

        return ModLoadOutcome.Failed;
    }

#if !PROGENITOR_TESTS
    /// <summary><c>[ProgenitorSide]</c> read by name (the attribute type may be the old Core's).</summary>
    private static bool RunsOnThisSide(System.Type type)
    {
        try
        {
            foreach (var a in System.Reflection.CustomAttributeData.GetCustomAttributes(type))
            {
                if (a.AttributeType.FullName != "Progenitor.Plugins.ProgenitorSideAttribute" || a.ConstructorArguments.Count == 0) continue;
                int side = Convert.ToInt32(a.ConstructorArguments[0].Value, CultureInfo.InvariantCulture);
                int process = Plugin.IsServerProcess ? (int)Side.Server : (int)Side.Client;
                return (side & process) != 0;
            }
        }
        catch { /* unknown: treated as running here */ }
        return true;
    }

    /// <summary><c>[Counterpart]</c> read by name, as plain data.</summary>
    private static CounterpartDeclaration? CounterpartByName(System.Type type)
    {
        try
        {
            foreach (var a in System.Reflection.CustomAttributeData.GetCustomAttributes(type))
            {
                if (a.AttributeType.FullName != "Progenitor.Plugins.CounterpartAttribute" || a.ConstructorArguments.Count == 0) continue;
                var need = (CounterpartNeed)Convert.ToByte(a.ConstructorArguments[0].Value, CultureInfo.InvariantCulture);
                var features = new List<string>();
                string extras = "";
                foreach (var named in a.NamedArguments)
                {
                    if (named.MemberName == "Features" && named.TypedValue.Value is System.Collections.Generic.IReadOnlyCollection<System.Reflection.CustomAttributeTypedArgument> items)
                        foreach (var item in items) if (item.Value is string s) features.Add(s);
                    if (named.MemberName == "ClientExtras" && named.TypedValue.Value is string e) extras = e;
                }

                return new CounterpartDeclaration(need, features, extras);
            }
        }
        catch { /* no declaration read */ }
        return null;
    }
#endif

    /// <summary>The chainloader's ordinary plugins (not Progenitor's) that hard-depend on <paramref name="id"/>.</summary>
    private static IReadOnlyList<(string Id, string Name)> PlainDependantsOfChainloader(string id)
    {
        var list = new List<(string Id, string Name)>();
#if !PROGENITOR_TESTS
        try
        {
            var plugins = IL2CPPChainloader.Instance?.Plugins;
            if (plugins == null) return list;
            foreach (var pair in plugins)
            {
                var info = pair.Value;
                if (info?.Metadata == null || ModRegistry.TryGet(info.Metadata.GUID, out _)) continue;
                foreach (var d in info.Dependencies)
                {
                    if (d == null || (d.Flags & BepInDependency.DependencyFlags.HardDependency) == 0) continue;
                    if (!string.Equals(d.DependencyGUID, id, StringComparison.OrdinalIgnoreCase)) continue;
                    list.Add((info.Metadata.GUID, info.Metadata.Name));
                    break;
                }
            }
        }
        catch { /* none known */ }
#endif
        _ = id;
        return list;
    }

    private static void ReleaseStage3Hooks()
    {
        if (!_stage3Hooks) return;
        _stage3Hooks = false;
        ModProblems.Changed -= OnStage3Changed;
        ModSwitch.Changed -= OnStage3SwitchChanged;
        ModRegistry.Changed -= OnStage3RegistryChanged;
        if (KeybindConflictSource.NameOf == DisplayNameOf) KeybindConflictSource.NameOf = null;
        if (ModSwitch.PlainDependantsProbe == PlainDependantsOfChainloader) ModSwitch.PlainDependantsProbe = null;
        if (InstallModsPane.PlainOfferProbe == PlainOfferOf) InstallModsPane.PlainOfferProbe = null;
        if (InstallModsPane.PlainActionHandler == OnInstallPlainAction) InstallModsPane.PlainActionHandler = null;
        if (InstallModsPane.PlainUninstallNow == PlainUninstallNow) InstallModsPane.PlainUninstallNow = null;
        if (InstallModsPane.PlainUninstallSentence == PlainUninstallSentence) InstallModsPane.PlainUninstallSentence = null;
        if (InstallModsPane.ShareInChat == OnInstallShare) InstallModsPane.ShareInChat = null;
        ModLinks.Reset();
    }

    private static void OnStage3Changed() => QueueDirectoryRefresh();

    private static void OnStage3SwitchChanged(string id) => QueueDirectoryRefresh();

    private static void OnStage3RegistryChanged(string id)
    {
        // HOSTS1 round 6: a load, failure, skip or unload changes what the Mods list draws.
        BumpListGeneration("registry:" + id);
        QueueDirectoryRefresh();
    }

    /// <summary>A plugin id's display name, for the problem sentences (another mod's name, never its GUID when a name is known).</summary>
    private static string? DisplayNameOf(string id)
    {
        if (Mods.TryGetValue(id, out var mod) && !string.IsNullOrWhiteSpace(mod.Name)) return mod.Name;
        if (ModRegistry.TryGet(id, out var entry)) return entry.Name;
        return null;
    }

    /// <summary>The preloader's failed results become problems once per workspace open.</summary>
    private static void ReconcileStartResultsOnce()
    {
        if (_startResultsReconciled) return;
        _startResultsReconciled = true;
        try { ModSwitch.ReconcileStartResults(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Mods: start results could not be read: " + e.Message); }
    }

    // ---- evidence -----------------------------------------------------------------------------

    /// <summary>Who owns <paramref name="id"/>'s files, as the switch needs to know it.</summary>
    internal static ModOwner OwnerOf(string id, ModsSidebarRow? row)
    {
        if (row != null && row.IsLoader) return ModOwner.Loader;
        if (ModEnableState.IsRequiredPart(id)) return ModOwner.Required;
        if (row != null && row.Provenance == ModProvenance.Server) return ModOwner.ServerOnly;
        if (ModRegistry.TryGet(id, out _)) return ModOwner.Progenitor;
        // Only Progenitor plugins are ever recorded in the state file or paused by Bootstrap.
        if (ModEnableState.IsDisabled(id)) return ModOwner.Progenitor;
        try
        {
            var pause = ServerModPause.Current;
            if (Contains(pause.PausedIds, id) || Contains(pause.ResumingIds, id)) return ModOwner.Progenitor;
        }
        catch { /* evidence, not a gate */ }
        if (row == null) return ModOwner.Unknown;
        if (row.Provenance == ModProvenance.Progenitor) return ModOwner.Progenitor;
        // FWALLOW1: a reload-folder plugin is hot-loaded by Bootstrap, so Bootstrap can hold it off.
        if (row.Provenance == ModProvenance.Reload) return ModOwner.Progenitor;
        if (row.Provenance == ModProvenance.Required) return ModOwner.Unknown;
        return ModOwner.Plain;
    }

    private static bool Contains(IReadOnlyList<string> list, string id)
    {
        for (int i = 0; i < list.Count; i++)
            if (string.Equals(list[i], id, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>The identification of an ordinary plugin's files, or null when it cannot be made.</summary>
    internal static PluginIdentification? PlainIdentityOf(string id)
    {
        var probe = PlainIdentityProbe;
        if (probe != null)
        {
            try { return probe(id); }
            catch { return null; }
        }
#if PROGENITOR_TESTS
        return null;
#else
        try
        {
            var plugins = IL2CPPChainloader.Instance?.Plugins;
            if (plugins == null) return null;
            var loaded = new List<LoadedPluginFact>();
            string location = "";
            foreach (var pair in plugins)
            {
                var info = pair.Value;
                if (info?.Metadata == null) continue;
                var deps = new List<string>();
                try
                {
                    foreach (var d in info.Dependencies)
                        if (d != null && (d.Flags & BepInDependency.DependencyFlags.HardDependency) != 0) deps.Add(d.DependencyGUID);
                }
                catch { /* no dependencies read: judged on the rest */ }
                string where = "";
                try { where = info.Location ?? ""; }
                catch { where = ""; }
                loaded.Add(new LoadedPluginFact(info.Metadata.GUID, info.Metadata.Name, where, deps));
                if (string.Equals(info.Metadata.GUID, id, StringComparison.OrdinalIgnoreCase)) location = where;
            }

            // Progenitor plugins (reload-owned or not) are never ordinary plugins' neighbors to move.
            foreach (var e in ModRegistry.All())
                if (!loaded.Exists(f => string.Equals(f.Id, e.Id, StringComparison.OrdinalIgnoreCase)))
                    loaded.Add(new LoadedPluginFact(e.Id, e.Name, "", e.Dependencies));
            return PluginFileActions.Identify(Paths.BepInExRootPath, id, location, loaded);
        }
        catch (Exception e)
        {
            // Live F24: the page then says "Managed elsewhere: Its file is not known."; the log says why.
            Plugin.Logger?.LogWarning("Mods: the files of " + id + " could not be identified: " + e.Message);
            return null;
        }
#endif
    }

    /// <summary>
    /// The joined server's full handshake request did not name <paramref name="row"/>'s mod: its
    /// server part is known to be missing (a request that filled every slot proves nothing).
    /// </summary>
    private static bool ServerPartKnownAbsent(ModsSidebarRow? row)
    {
        if (row?.Inventory?.ServerAsked == true) return false;
        try
        {
            var asked = Handshake.CopyServerAsked();
            return CounterpartPolicy.RequestProvesAbsence(asked.Length, Handshake.ServerRequestTruncated);
        }
        catch { return false; }
    }

    /// <summary>
    /// COUNTERPART-SAMPLES: the server-part cell and the limited word for <paramref name="pluginId"/>
    /// with sides <paramref name="sides"/>, read now from the same evidence as
    /// <see cref="FrameworkFactsFor"/>, so an open page can rewrite them in place.
    /// </summary>
    internal static (ServerPartState Part, bool Limited) ServerPartNow(string pluginId, ModSides sides)
    {
        ModMenuInventoryRow? inventory = null;
        foreach (var r in ModMenuInventory.Snapshot().Rows)
            if (string.Equals(r.Id, pluginId, StringComparison.OrdinalIgnoreCase)) { inventory = r; break; }
        bool paused = string.Equals(inventory?.Status, ModMenuInventory.StatusPaused, StringComparison.Ordinal)
            || string.Equals(inventory?.Status, ModMenuInventory.StatusResuming, StringComparison.Ordinal);
        bool asked = inventory?.ServerAsked ?? false;
        var counterpart = ModRegistry.CounterpartOf(pluginId);
        bool knownAbsent = false;
        if (counterpart != null && !asked)
        {
            try { knownAbsent = CounterpartPolicy.RequestProvesAbsence(Handshake.CopyServerAsked().Length, Handshake.ServerRequestTruncated); }
            catch { knownAbsent = false; }
        }
        var part = ModFrameworkLine.DecideServerPart(sides, paused, asked, knownAbsent);
        return (part, counterpart != null && counterpart.RunsWithout && part == ServerPartState.NotOnServer);
    }

    /// <summary>
    /// The framework line's evidence, in one place, for the page's status bar and every list entry,
    /// so the two cannot say different things. <paramref name="plain"/> is only computed for a page.
    /// </summary>
    internal static ModFrameworkFacts FrameworkFactsFor(
        string pluginId, ModsSidebarRow? row, ModPageState pageState, PackageSide listingSide, bool requiresBoth,
        string version, string? actionCaption, LabelTone actionTone, bool settingsPendingRestart,
        PluginIdentification? plain = null, bool plainKnown = false)
    {
        long t = System.Diagnostics.Stopwatch.GetTimestamp();
        var inventory = row?.Inventory;
        bool paused = string.Equals(inventory?.Status, ModMenuInventory.StatusPaused, StringComparison.Ordinal);
        bool resuming = string.Equals(inventory?.Status, ModMenuInventory.StatusResuming, StringComparison.Ordinal);
        bool onClient = row == null || row.Provenance != ModProvenance.Server;
        if (inventory?.LocalVersion is { Length: > 0 } local) version = local;
        // An ordinary plugin the preloader turned off is not loaded: its renamed DLL still says.
        if (string.IsNullOrWhiteSpace(version) || string.Equals(version, ModMenuInventory.VersionUnknown, StringComparison.OrdinalIgnoreCase))
        {
            string off = ModSwitch.DisabledVersionOf(pluginId);
            if (off.Length > 0) version = off;
        }

        WorkspaceOpenTiming.Add("facts-version", t);
        t = System.Diagnostics.Stopwatch.GetTimestamp();
        NetworkCompatibilityLevel? declared = null;
        try
        {
            foreach (var record in ProvidedRecords())
            {
                if (!string.Equals(record.Guid, pluginId, StringComparison.OrdinalIgnoreCase)) continue;
                declared = record.Level;
                break;
            }
        }
        catch { /* declarations are evidence, never a gate */ }

        var counterpart = ModRegistry.CounterpartOf(pluginId);
        var sides = ModFrameworkLine.DecideSides(listingSide, requiresBoth, declared, counterpart != null);
        bool knownAbsent = counterpart != null && ServerPartKnownAbsent(row);
        var part = ModFrameworkLine.DecideServerPart(sides, paused || resuming, inventory?.ServerAsked ?? false, knownAbsent);

        WorkspaceOpenTiming.Add("facts-sides", t);
        t = System.Diagnostics.Stopwatch.GetTimestamp();
        var owner = OwnerOf(pluginId, row);
        WorkspaceOpenTiming.Add("facts-owner", t);
        t = System.Diagnostics.Stopwatch.GetTimestamp();
        ModSwitchView view;
        try { view = ModSwitch.Describe(pluginId, row?.Name ?? pluginId, owner, plainKnown ? plain : null); }
        catch { view = new ModSwitchView { Mode = ModSwitchMode.Unavailable }; }
        // Without an identification (the list) a plain plugin's switch is still decided from what the
        // preloader recorded, but its position is only known on its page.
        bool canRetry = ModSwitch.CanRetry(pluginId);
        WorkspaceOpenTiming.Add("facts-switch", t);
        t = System.Diagnostics.Stopwatch.GetTimestamp();
        List<ModProblem> problems;
        try { problems = ModProblems.WithRetry(ModProblems.For(pluginId), canRetry); }
        catch { problems = new List<ModProblem>(); }
        WorkspaceOpenTiming.Add("facts-problems", t);

        // An ordinary plugin with certain files gets Uninstall (or Cancel for a waiting request).
        if (owner == ModOwner.Plain && plainKnown)
        {
            if (view.Pending == ModSwitchPending.Removed) { actionCaption = PlainCancelCaption; actionTone = LabelTone.Ok; }
            else if (plain is { Certain: true }) { actionCaption = ModPageCopy.Uninstall; actionTone = LabelTone.Orange; }
        }

        return new ModFrameworkFacts
        {
            PluginId = pluginId,
            OnClient = onClient,
            Paused = paused,
            Resuming = resuming,
            PageState = pageState,
            Version = version,
            Sides = sides,
            ServerPart = part,
            ActionCaption = actionCaption,
            ActionTone = actionTone,
            SettingsPendingRestart = settingsPendingRestart,
            EnabledIntent = view.On,
            SwitchAvailable = view.Interactive,
            SwitchReason = view.Reason,
            SwitchMode = view.Mode,
            SwitchPending = view.Pending,
            SwitchedOff = view.Disabled,
            OffBecause = view.OffBecause,
            Problems = problems,
            Limited = counterpart != null && counterpart.RunsWithout && part == ServerPartState.NotOnServer,
            CanRetry = canRetry,
        };
    }

    /// <summary>
    /// The caption of the action that cancels a waiting next-start request. INSTALLCOLS1: it says what
    /// it cancels, the same words as the Install tab's button, never a bare "Cancel".
    /// </summary>
    internal const string PlainCancelCaption = InstallModsCopy.CancelUninstall;

    /// <summary>One list entry, decided from the same facts as the page.</summary>
    private static ModsListEntry ListEntryOf(ModsSidebarRow row)
    {
        // Round 7: per-row costs summed into the open timing line (entry-listing, entry-facts, entry-links).
        long t = System.Diagnostics.Stopwatch.GetTimestamp();
        CatalogEntry? entry = null;
        try { entry = row.Provenance == ModProvenance.Progenitor ? VerifiedListingEntry(row.Id, row, deep: false) : null; }
        catch { entry = null; }
        var listing = entry == null ? null : ModPageListing.FromEntry(entry, null);
        string version = row.Version;
        if (row.Provenance == ModProvenance.Progenitor)
        {
            string ledger = LedgerVersionOf(row);
            if (ledger.Length > 0) version = ledger;
        }

        WorkspaceOpenTiming.Add("entry-listing", t);
        t = System.Diagnostics.Stopwatch.GetTimestamp();
        var state = SidebarStateOf(row);
        WorkspaceOpenTiming.Add("entry-state", t);
        t = System.Diagnostics.Stopwatch.GetTimestamp();
        var facts = FrameworkFactsFor(
            row.Id, row, state, listing?.Side ?? PackageSide.Unknown, listing?.RequiresBoth ?? false,
            version, null, LabelTone.Normal, settingsPendingRestart: false);
        WorkspaceOpenTiming.Add("entry-facts", t);
        t = System.Diagnostics.Stopwatch.GetTimestamp();
        bool update = false;
        if (row.Provenance == ModProvenance.Progenitor)
        {
            try
            {
                var link = ResolveInstallLink(row.Id);
                update = link is { Resolved: true } offer && InstalledState.IsNewer(offer.Version, LedgerVersionOf(row));
            }
            catch { update = false; }
        }

        WorkspaceOpenTiming.Add("entry-links", t);

        var group = row.Provenance switch
        {
            ModProvenance.Server => ModsListGroup.Server,
            ModProvenance.Required => ModsListGroup.Required,
            _ => ModsListGroup.Installed,
        };
        string shownVersion = facts.Version.Length > 0 ? facts.Version : version;
        return ModsListLayout.Decide(new ModsListEntryFacts
        {
            Id = row.Id,
            Name = SidebarNameOf(row, id => ResolveInstallLink(id)?.Key),
            Version = shownVersion,
            // C5: the Works across versions marker reads the declared range of the version shown.
            DeclaredPeers = DeclaredPeersOf(row.Id, row, shownVersion),
            Sides = facts.Sides,
            Group = group,
            Line = ModFrameworkLine.Compose(facts),
            Problems = facts.Problems,
            UpdateAvailable = update,
            ServerHalfMissing = SidebarWarningOf(row),
        });
    }

    // ---- the list's rows ----------------------------------------------------------------------

    /// <summary>One entry row: diamond, name over the second line, Update tag. Selected: accent bar and soft fill.</summary>
    private static IUiHandle AddListEntryRow(IUiPanel panel, ModsSidebarRow row, ModsListEntry entry, float rowWidth)
    {
        string id = row.Id;
        // Chat stage 3 (mod links): a mod whose Thunderstore package is known gets the Share button, shown
        // while the pointer is on its row.
        string share = SharePackageOf(row).Package;
        var handle = panel.CellRow(ModsListLayout.EntryRow(entry, rowWidth, shareable: share.Length > 0));
        bool selected = string.Equals(_selected, id, StringComparison.OrdinalIgnoreCase);
        handle.SetSelected(selected);
        handle.OnRowClick(() => OpenMod(id, ReasonUserButton));
        if (share.Length > 0)
        {
            var h = handle;
            handle.OnHover(entered =>
            {
                if (!h.IsDisposed) h.SetCell(ModsListLayout.CellShare, visible: entered);
            });
            handle.OnClick(ModsListLayout.CellShare, () => ShareInstalled(id));
        }
        // C5: pointing at the Works across versions icon says what it means, in the open page's description column.
        if (entry.AcrossIcon) handle.OnCellHover(ModsListLayout.CellAcross, HoverAcrossIcon);
        string drawn = ModsListLayout.DrawnSecondLine(entry, rowWidth, out string across, out string iconAfter);
        _listExtras.Add((handle, "modsListEntry", () => new Dictionary<string, object?>
        {
            ["id"] = entry.Id,
            ["name"] = entry.Name,
            ["group"] = ModsListLayout.NameOf(entry.Group),
            ["diamond"] = ModsListLayout.NameOf(entry.Diamond),
            ["diamondShape"] = entry.Diamond == ModDiamond.Waiting ? ModsListLayout.DiamondHollow : ModsListLayout.DiamondFilled,
            ["diamondTone"] = ToneName(ModsListLayout.ToneOf(entry.Diamond)),
            ["secondLine"] = entry.SecondLine,
            // C5: the Works across versions icon. secondLineDrawn is the line's text as drawn (clipped as the runtime
            // clips it, with the icon's room after the version); acrossVersionsShown says whether the icon is drawn
            // (icon or none), and acrossIconAfter is the text it follows (the version).
            ["acrossVersions"] = entry.AcrossVersions,
            ["range"] = entry.AcrossRange,
            ["acrossVersionsShown"] = across,
            ["acrossIconAfter"] = iconAfter,
            ["secondLineDrawn"] = drawn,
            ["needsAttention"] = entry.NeedsAttention,
            ["disabled"] = entry.Disabled,
            ["update"] = entry.Update,
            [ShareReadbackKey] = share,
            ["selected"] = selected,
            ["align"] = "left",
        }));
        return handle;
    }

    /// <summary>
    /// C5: the pointer entered or left a list entry's Works across versions icon. The workspace's hover text goes in the
    /// description column (the page's Details rows, the Keybinds tab), so the open mod page's column reads
    /// <see cref="ModAcrossVersions.Words"/> over <see cref="ModAcrossVersions.Help"/> while the pointer is on the icon,
    /// and returns to its rest text when it leaves. Nothing is shown when no mod page is open.
    /// </summary>
    internal static void HoverAcrossIcon(bool entered)
    {
        var details = _modPage?.Details;
        if (details == null) return;
        if (entered) details.Show(ModAcrossVersions.Words, ModAcrossVersions.Help);
        else details.ShowRest();
    }

    private static string ToneName(CellTone tone) => tone switch
    {
        CellTone.Ok => "ok",
        CellTone.Warn => "warn",
        CellTone.Danger => "danger",
        _ => "muted",
    };

    private static IUiCellRow AddListGroupRow(IUiPanel panel, ModsListGroup group, int count, bool collapsible, bool expanded, Action? toggle)
    {
        var handle = panel.CellRow(ModsListLayout.GroupRow(group, count, collapsible));
        if (collapsible)
        {
            handle.SetExpanded(expanded);
            if (toggle != null) handle.OnRowClick(toggle);
        }

        _listExtras.Add((handle, "modsListGroup", () => new Dictionary<string, object?>
        {
            ["group"] = ModsListLayout.NameOf(group),
            ["caption"] = ModsListLayout.CaptionOf(group),
            ["count"] = count,
            ["collapsible"] = collapsible,
            ["expanded"] = !collapsible || expanded,
        }));
        return handle;
    }

    private static void AddListTop(IUiPanel panel, int mods, int attention)
    {
        string text = ModsListLayout.SummaryText(mods, attention);
        var summary = panel.CellRow(ModsListLayout.SummaryRow(text));
        _listExtras.Add((summary, "modsListSummary", () => new Dictionary<string, object?>
        {
            ["text"] = text,
            ["mods"] = mods,
            ["attention"] = attention,
            ["filter"] = ModsListLayout.NameOf(_listFilter),
        }));
        var chips = panel.CellRow(ModsListLayout.FilterRow(_listFilter));
        foreach (var f in new[] { ModsListFilter.All, ModsListFilter.NeedsAttention, ModsListFilter.Disabled })
        {
            var chosen = f;
            chips.OnClick(ModsListLayout.ChipKey(f), () => OnListFilter(chosen));
        }

        _listExtras.Add((chips, "modsListFilter", () => new Dictionary<string, object?>
        {
            ["filter"] = ModsListLayout.NameOf(_listFilter),
            ["options"] = new[] { "all", "attention", "disabled" },
        }));
    }

    /// <summary>A chip was pressed: the list shows only the entries that pass it.</summary>
    internal static void OnListFilter(ModsListFilter filter)
    {
        if (_listFilter == filter) return;
        _listFilter = filter;
        if (!IsOpen) return;
        if (_tabState == null || _tabState.Selected == ModsTab.Mods) RebuildSidebar(ReasonUserButton);
    }

    /// <summary>The extras of a list row the sidebar drew, or null.</summary>
    private static Dictionary<string, object?>? DescribeListRow(IUiHandle handle)
    {
        Dictionary<string, object?>? extra = null;
        for (int i = 0; i < _listExtras.Count; i++)
        {
            if (!ReferenceEquals(_listExtras[i].Handle, handle)) continue;
            extra ??= new Dictionary<string, object?>(StringComparer.Ordinal);
            try { extra[_listExtras[i].Key] = _listExtras[i].Value(); }
            catch (Exception e) { extra[_listExtras[i].Key] = "error: " + e.Message; }
        }

        return extra;
    }

    // ---- the switch -----------------------------------------------------------------------------

    /// <summary>The switch was pressed. Turning off a mod others need asks first, naming them.</summary>
    private static void OnModsSwitchClick(string pluginId)
    {
        var row = CurrentSidebarModel(applySearch: false).Find(pluginId);
        var owner = OwnerOf(pluginId, row);
        var plain = owner == ModOwner.Plain ? PlainIdentityOf(pluginId) : null;
        string name = row != null ? SidebarNameOf(row, id => ResolveInstallLink(id)?.Key) : pluginId;
        var view = ModSwitch.Describe(pluginId, name, owner, plain);
        if (!view.Interactive)
        {
            NoteAndReopen(pluginId, view.Reason);
            return;
        }

        bool turnOn = !view.On;
        var also = turnOn ? new List<string>() : ModSwitch.AlsoTurnsOff(pluginId, view);
        var factory = ModPagePane.DialogFactory;
        if (also.Count == 0 || factory == null)
        {
            ApplySwitch(pluginId, name, owner, turnOn, plain, view.Mode);
            return;
        }

        CloseSwitchDialog();
        _switchDialogPlugin = pluginId;
        var dialog = UiDialog.Open(
            factory, SwitchDialogTag, "switch-off", pluginId,
            SwitchOffHeading(name), SwitchOffBody(also, view.Mode), SwitchOffYes, LabelTone.Orange, ModPageCopy.PromptNo);
        var mode = view.Mode;
        dialog.OnYes(() =>
        {
            // The dialog belongs to the page: a Yes after the page moved on changes nothing.
            if (!string.Equals(_switchDialogPlugin, pluginId, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(_selected, pluginId, StringComparison.OrdinalIgnoreCase))
            {
                Plugin.Logger?.LogWarning("Mods: switch-off of " + pluginId + " refused: that page is no longer on show.");
                return;
            }

            _switchDialogPlugin = "";
            ApplySwitch(pluginId, name, owner, turnOn: false, plain, mode);
        });
        dialog.OnNo(() => _switchDialogPlugin = "");
    }

    internal static string SwitchOffHeading(string name) => "Turn off " + name + "?";

    internal const string SwitchOffYes = "Turn off";

    internal static string PlainAlsoOffNote(List<string> names) =>
        KeybindConflictSource.JoinNames(names) + (names.Count == 1 ? " turns off" : " turn off") + " at next start too.";

    internal static string SwitchOffBody(IReadOnlyList<string> also, ModSwitchMode mode)
    {
        string names = KeybindConflictSource.JoinNames(new List<string>(also));
        string when = mode == ModSwitchMode.Live ? "now" : "at the next start";
        return (also.Count == 1 ? names + " needs it and turns off " : "These mods need it and turn off " )
            + (also.Count == 1 ? when + " too." : when + " too: " + names + ".");
    }

    private static void ApplySwitch(string pluginId, string name, ModOwner owner, bool turnOn, PluginIdentification? plain, ModSwitchMode mode)
    {
        // A live switch-off unloads the page's own mod: step off its page first, so the unload
        // releases nothing that is on screen (it would otherwise close the workspace, the rule for a
        // mod that unloads under its open page), then come back to it.
        // Review finding 5: turning on a mod skipped at this start also unloads it first, so every
        // live action steps off the page, not only turning off.
        bool live = mode == ModSwitchMode.Live;
        if (live && IsOpen && string.Equals(_selected, pluginId, StringComparison.OrdinalIgnoreCase))
            Open(ReasonUserButton);
        var outcome = ModSwitch.Set(pluginId, name, owner, turnOn, plain);
        string note = outcome.Message;
        // Review of 5a6e40f8, finding 6: an ordinary plugin that needs it is turned off at the next
        // start too, where the preloader can name its files; the rest keep their problem line.
        if (outcome.Ok && !turnOn && owner == ModOwner.Progenitor)
        {
            var also = ModSwitch.TurnOffPlainDependants(pluginId, PlainIdentityOf);
            if (also.Count > 0) note += " " + PlainAlsoOffNote(also);
        }

        // F7: the list banner and the page read fresh state (a hot suspend or resume needs no restart).
        RefreshAfterPageAction();
        NoteAndReopen(pluginId, note);
    }

    private static void NoteAndReopen(string pluginId, string note)
    {
        _modsActionPlugin = pluginId;
        _modsActionNote = note ?? "";
        if (!IsOpen) return;
        // The Install tab's row answered: redraw that tab, never navigate away from it.
        if (_tabState != null && _tabState.Selected == ModsTab.InstallMods)
        {
            RebuildInstallDetailAfterAction();
            return;
        }

        RebuildSidebar(ReasonUserButton);
        OpenMod(pluginId, ReasonUserButton);
    }

    /// <summary>Close the switch or uninstall confirmation if it is the one open.</summary>
    internal static void CloseSwitchDialog()
    {
        _switchDialogPlugin = "";
        var open = UiDialog.Current;
        if (open == null) return;
        if (!string.Equals(open.Tag, SwitchDialogTag, StringComparison.Ordinal)
            && !string.Equals(open.Tag, PlainUninstallDialogTag, StringComparison.Ordinal)
            && !string.Equals(open.Tag, ReloadUninstallDialogTag, StringComparison.Ordinal)) return;
        try { open.Close(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: switch dialog close threw: " + e.Message); }
    }

    /// <summary>KEYS2: <b>Dismiss</b> on a dismissible problem line (a reserved key already replaced); the page redraws.</summary>
    internal static void OnModsDismissProblem(string pluginId, ModProblem problem)
    {
        if (!ModProblems.Dismiss(problem)) return;
        NoteAndReopen(pluginId, "");
    }

    /// <summary>Retry: load a hot-loadable mod whose <c>OnLoad</c> threw again.</summary>
    private static void OnModsRetryClick(string pluginId)
    {
        if (IsOpen && string.Equals(_selected, pluginId, StringComparison.OrdinalIgnoreCase))
            Open(ReasonUserButton);
        var outcome = ModSwitch.Retry(pluginId);
        NoteAndReopen(pluginId, outcome.Message);
    }

    /// <summary>
    /// <c>Show all</c>: every problem in a notice, with the report for the mod author. <b>Copy
    /// report</b> puts it on the clipboard; where this runtime has no clipboard the report is shown in
    /// the notice instead, and the notice says so.
    /// </summary>
    private static void OnModsShowAllProblems(string pluginId)
    {
        var factory = ModPagePane.DialogFactory;
        if (factory == null) return;
        var row = CurrentSidebarModel(applySearch: false).Find(pluginId);
        string name = row != null ? SidebarNameOf(row, id => ResolveInstallLink(id)?.Key) : pluginId;
        string version = row?.Inventory?.LocalVersion ?? row?.Version ?? "";
        var problems = ModProblems.For(pluginId);
        string report = ModProblems.ReportText(pluginId, name, version, problems);
        var lines = new List<string>(problems.Count + 4);
        foreach (var p in problems) lines.Add(p.Line);
        bool canCopy = Ui.CanCopyToClipboard;
        if (!canCopy)
        {
            lines.Add(ModProblems.NotCopiedNote);
            lines.Add(ModProblems.ReportHeading + ":");
            lines.Add(Cap(report, 2000));
        }

        var content = new DialogContent
        {
            Shape = UiDialogShape.Confirm,
            Title = ModProblems.NoticeTitlePrefix + name,
            Body = "",
            Lines = lines,
            MainCaption = canCopy ? CopyReportCaption : "",
            MainTone = CellTone.Gold,
            CancelCaption = ServerModPauseText.CloseButton,
        };
        var dialog = UiDialog.OpenContent(factory, content, ProblemsDialogTag, "problems", pluginId);
        dialog.OnYes(() =>
        {
            bool copied = Ui.TryCopyToClipboard(report);
            _modsActionPlugin = pluginId;
            _modsActionNote = copied ? ModProblems.CopiedNote : ModProblems.NotCopiedNote;
            LastProblemReport = report;
            if (IsOpen && string.Equals(_selected, pluginId, StringComparison.OrdinalIgnoreCase))
                OpenMod(pluginId, ReasonUserButton);
        });
        LastProblemReport = report;
    }

    internal const string CopyReportCaption = "Copy report";

    /// <summary>The last report <c>Show all</c> built (readback and tests).</summary>
    internal static string LastProblemReport { get; private set; } = "";

    private static string Cap(string text, int max) =>
        text.Length <= max ? text : text.Substring(0, max) + "…";

    // ---- an ordinary plugin's uninstall --------------------------------------------------------

    /// <summary>
    /// The Install tab's row for a mod another tool installed: what the preloader can do for it —
    /// Uninstall when its files are known with certainty, Cancel while a removal waits, else why not.
    /// </summary>
    internal static InstallModsPane.PlainOffer? PlainOfferOf(string pluginId)
    {
        if (string.IsNullOrWhiteSpace(pluginId) || ModEnableState.IsRequiredPart(pluginId)) return null;
        // SRVUNINST1: a reload-folder copy no ledger lists (FWALLOW1) is removable here, as on the Mods page:
        // the Install tab must not call it "Managed elsewhere" while the Mods page offers its Uninstall.
        if (!ModMenuInventory.IsFrameworkId(pluginId) && CurrentReloadFiles().ContainsKey(pluginId))
            return new InstallModsPane.PlainOffer { CanUninstall = true, Reload = true };
        if (ModRegistry.TryGet(pluginId, out _)) return null; // a Progenitor plugin has its own switch
        var state = ModSwitch.PlainState();
        bool pending = PluginFileActions.PendingFor(state, pluginId)?.Action == PluginFileAction.Remove;
        var last = PluginFileActions.LastResultFor(state, pluginId);
        string failure = last is { Done: false, Action: PluginFileAction.Remove }
            ? "Could not be removed at the last start: " + last.Reason
            : "";
        var ident = PlainIdentityOf(pluginId);
        return new InstallModsPane.PlainOffer
        {
            RemovalPending = pending,
            CanUninstall = ident is { Certain: true },
            Why = ident is { Certain: true } ? "" : ModSwitch.ShortWhy(ident?.Why ?? PluginFileActions.WhyNoLocation),
            LastFailure = failure,
        };
    }

    /// <summary>The Install tab's Uninstall / Cancel on such a row: the same flow as the mod's page.</summary>
    private static void OnInstallPlainAction(string pluginId, string name, bool cancel)
    {
        // SRVUNINST1: a reload-folder copy is the Mods page's FWALLOW1 removal (delete now, unload), not a next-start request.
        if (!cancel && ReloadRowOf(pluginId) is { } reloadRow)
        {
            OpenReloadUninstallDialog(reloadRow, name);
            return;
        }

        if (cancel)
        {
            NoteAndReopen(pluginId, ModSwitch.CancelRequest(pluginId).Message);
            return;
        }

        OpenPlainUninstallDialog(pluginId, name);
    }

    /// <summary>Uninstall (asks first) or Cancel a waiting request, for an ordinary plugin.</summary>
    private static void OnPlainActionClick(string pluginId)
    {
        var row = CurrentSidebarModel(applySearch: false).Find(pluginId);
        string name = row != null ? SidebarNameOf(row, id => ResolveInstallLink(id)?.Key) : pluginId;
        var plain = PlainIdentityOf(pluginId);
        var view = ModSwitch.Describe(pluginId, name, ModOwner.Plain, plain);
        if (view.Pending == ModSwitchPending.Removed)
        {
            NoteAndReopen(pluginId, ModSwitch.CancelRequest(pluginId).Message);
            return;
        }

        OpenPlainUninstallDialog(pluginId, name);
    }

    /// <summary>The framework confirmation of a next-start uninstall, in the danger tone.</summary>
    private static void OpenPlainUninstallDialog(string pluginId, string name)
    {
        EnsureModPageDialogFactory();
        var factory = ModPagePane.DialogFactory;
        if (factory == null)
        {
            NoteAndReopen(pluginId, ModSwitch.RequestRemove(pluginId, name, PlainIdentityOf(pluginId)).Message);
            return;
        }

        CloseSwitchDialog();
        _switchDialogPlugin = pluginId;
        var dialog = UiDialog.Open(
            factory, PlainUninstallDialogTag, "uninstall", pluginId,
            ModPageCopy.UninstallHeading(name, ""), PlainUninstallBody, PlainUninstallYes, LabelTone.Danger, ModPageCopy.PromptNo,
            danger: true);
        dialog.OnYes(() =>
        {
            if (!string.Equals(_switchDialogPlugin, pluginId, StringComparison.OrdinalIgnoreCase)) return;
            _switchDialogPlugin = "";
            NoteAndReopen(pluginId, ModSwitch.RequestRemove(pluginId, name, PlainIdentityOf(pluginId)).Message);
        });
        dialog.OnNo(() => _switchDialogPlugin = "");
    }

    internal const string PlainUninstallYes = "Yes, uninstall";

    internal const string PlainUninstallBody =
        "Progenitor moves it to BepInEx/progenitor-removed the next time the game starts. You can restore it "
        + "from there by moving it back into BepInEx/plugins. It stays loaded until then.";

    /// <summary>
    /// The provenance note of an ordinary plugin: what Progenitor can do for it, or why not. One the
    /// preloader turned off is not loaded (so its file cannot be judged from the loaded plugin), but
    /// Progenitor manages it: the note says so rather than "Managed elsewhere".
    /// </summary>
    internal static string PlainNoteOf(PluginIdentification? plain, bool offByProgenitor = false) =>
        offByProgenitor
            ? PlainOffByProgenitorNote
            : plain is { Certain: true }
                ? "Installed by another tool. Progenitor can turn it off or uninstall it at the next start."
                : ModSwitch.ManagedElsewhereReason(plain?.Why ?? PluginFileActions.WhyNoLocation);

    /// <summary>
    /// Live F24 (2026-10-07): the readback's <c>removable</c> and <c>provenanceLine</c> for a row, decided the
    /// way its page decides them. The row's own pair (<see cref="ModsSidebarRow.Removable"/>,
    /// <see cref="ModsSidebarRow.PageNote"/>) knows only its provenance, so for a hand-installed loose DLL
    /// it said <c>removable: false</c> and "Installed by another tool — remove it with that tool." while
    /// the page offered Uninstall. An ordinary plugin's row is judged from its identification instead:
    /// removable when its files are certain, and otherwise <c>Managed elsewhere: &lt;why&gt;</c>.
    /// </summary>
    internal static (bool Removable, string Line) ReadbackProvenanceOf(ModsSidebarRow row)
    {
        if (row.Provenance != ModProvenance.Other || OwnerOf(row.Id, row) != ModOwner.Plain)
            return (row.Removable, row.PageNote ?? "");
        var plain = PlainIdentityOf(row.Id);
        return (plain is { Certain: true }, PlainNoteOf(plain, ModSwitch.PlainOffOnDisk(row.Id)));
    }

    internal const string PlainOffByProgenitorNote =
        "Installed by another tool. Turned off by Progenitor; turn it on to load it at the next start.";

    // ---- teardown ---------------------------------------------------------------------------------

    private static void ResetStage3()
    {
        _listFilter = ModsListFilter.All;
        _listExtras.Clear();
        _switchDialogPlugin = "";
        _startResultsReconciled = false;
        LastProblemReport = "";
        ReleaseStage3Hooks();
    }

#if PROGENITOR_TESTS
    internal static void ResetStage3ForTests()
    {
        ResetStage3();
        ResetNavigationForTests();
        GameMenuHistory.ResetForTests();
        PlainIdentityProbe = null;
    }
#endif
}
