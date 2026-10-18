using System;
using System.Collections.Generic;
using Progenitor.Install;
using Progenitor.Networking;
using Progenitor.PluginActions;

namespace Progenitor.Ui;

/// <summary>
/// INSTALLRESTART1: the Mods window's side of the pending-restart bar — the pending client changes it counts, the same bar
/// at the top of the Mods tab's list, the after-restart check, and the bridge's <c>mods_ui restart</c>.
/// </summary>
public static partial class ModsMenu
{
    internal const string ModsRestartTextKey = "mods-restart";
    internal const string ModsRestartButtonKey = "mods-restart-button";

    /// <summary>Tests only: replaces the pending changes (the Install tab's rows and the ledger).</summary>
    internal static Func<IReadOnlyList<PendingClientChange>>? PendingRestartForTests { get; set; }

    /// <summary>
    /// The client changes waiting for a restart: the Install tab's pending rows once its index is loaded, else the client
    /// ledger's pending installs (the Mods tab before the Install tab was opened).
    /// </summary>
    internal static IReadOnlyList<PendingClientChange> ClientPendingRestart()
    {
        var test = PendingRestartForTests;
        if (test != null) return test();
        var list = new List<PendingClientChange>();
        var pane = _installPane;
        bool fromPane = false;
        if (pane != null)
        {
            try
            {
                foreach (var (entry, remove) in pane.ClientPendingEntries())
                {
                    fromPane = true;
                    string guid = "";
                    try { guid = (_catalog as InstallCatalogService)?.TryRestingIdentity(entry)?.PluginGuid ?? ""; }
                    catch { guid = ""; }
                    bool loaded = guid.Length > 0 && PluginLoadedNow(guid);
                    list.Add(new PendingClientChange
                    {
                        Id = guid,
                        Name = InstallModsPane.HeadingText(entry),
                        Version = entry.Manifest?.Version ?? "",
                        Action = remove ? PendingClientChange.ActionRemove : loaded ? PendingClientChange.ActionUpdate : PendingClientChange.ActionInstall,
                    });
                }
            }
            catch (Exception e) { Plugin.Logger?.LogWarning("Install restart: reading the Install tab's pending rows threw: " + e.Message); }
        }

        AddProgenitorRestart(list);
        if (fromPane || (pane != null && pane.HasEntries))
        {
            MergeRemovals(list, CachedLedgerRemovals());
            MergeRemovals(list, PlainRemovals(ModSwitch.PlainState()));
            return list;
        }

        // The Install tab's index is not loaded: the client ledger's pending downloads are the installs waiting.
        try
        {
            foreach (var row in CurrentLedger())
            {
                if (!string.Equals(row.Root, InstalledState.ClientRoot, StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.Equals(row.Category, ServerInstallLedger.CategoryPending, StringComparison.OrdinalIgnoreCase)) continue;
                list.Add(new PendingClientChange
                {
                    Id = row.PluginId,
                    Name = ModMenuInventory.DisplayNameOfLedger(row, row.Id),
                    Version = row.Version,
                    Action = row.PluginId.Length > 0 && PluginLoadedNow(row.PluginId) ? PendingClientChange.ActionUpdate : PendingClientChange.ActionInstall,
                });
            }
        }
        catch { /* no ledger, nothing pending */ }

        MergeRemovals(list, CachedLedgerRemovals());
        MergeRemovals(list, PlainRemovals(ModSwitch.PlainState()));
        return list;
    }

    /// <summary>
    /// An ordinary plugin marked for removal at the next start (<see cref="ModSwitch.RequestRemove"/>: the preloader's
    /// request file) waits for a restart as a ledger removal does. Neither the Install tab's rows nor the ledger name a
    /// plugin another tool installed (a loose DLL in <c>BepInEx/plugins</c>, a folder), so without this its removal reached
    /// no Restart offer and <b>Yes, uninstall and restart</b> had nothing to restart for.
    /// </summary>
    internal static List<PendingClientChange> PlainRemovals(PluginFileState? state)
    {
        var list = new List<PendingClientChange>();
        if (state == null) return list;
        foreach (var r in state.Requests)
        {
            if (r == null || r.Action != PluginFileAction.Remove || string.IsNullOrWhiteSpace(r.PluginId)) continue;
            list.Add(new PendingClientChange
            {
                Id = r.PluginId,
                Name = string.IsNullOrWhiteSpace(r.Name) ? r.PluginId : r.Name,
                Action = PendingClientChange.ActionRemove,
            });
        }

        return list;
    }

    /// <summary>The restart offer names this mod (by plugin id, else by name).</summary>
    internal static bool OfferCovers(ClientRestartOffer offer, string pluginId, string name)
    {
        if (offer == null || !offer.Shown) return false;
        foreach (var p in offer.Pending)
        {
            if (p == null) continue;
            if (pluginId.Length > 0 && string.Equals(p.Id, pluginId, StringComparison.OrdinalIgnoreCase)) return true;
            if (name.Length > 0 && string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    /// <summary>Added to the page's note when <b>Yes, uninstall and restart</b> cannot ask for the restart during a join.</summary>
    internal const string RestartAfterUninstallJoining = "Restart once the join has finished.";

    /// <summary>Added to the page's note when the restart offer does not cover the removal.</summary>
    internal const string RestartAfterUninstallNotCovered = "Restart the game to finish the uninstall.";

    /// <summary>
    /// <b>Yes, uninstall and restart</b>, after the removal was marked: the shared restart button (its confirmation, the
    /// Manual-mode <c>Quit to desktop</c> wording, its double-press and mid-join guards), and only when the pending offer
    /// covers this mod, so an unrelated pending change never restarts in its place. Returns <c>confirm</c> when the
    /// confirmation opened, else the sentence to add to the page's note.
    /// </summary>
    internal static string RestartAfterUninstall(string pluginId, string name)
    {
        ForgetLedgerRemovals();
        ClientRestartOffer offer;
        try { offer = JoinFlowClient.CurrentRestartOffer(); }
        catch { offer = ClientRestartOffer.None; }
        if (!OfferCovers(offer, pluginId ?? "", name ?? ""))
        {
            Plugin.Logger?.LogInfo("Ui: uninstall and restart of " + pluginId + ": the restart offer does not cover it; nothing restarts");
            return RestartAfterUninstallNotCovered;
        }

        string pressed = JoinFlowClient.PressRestartButton();
        if (pressed == "confirm") return pressed;
        Plugin.Logger?.LogInfo("Ui: uninstall and restart of " + pluginId + ": the restart was not asked for (" + pressed + ")");
        return pressed == JoinFlowClient.RestartRefusedJoining ? RestartAfterUninstallJoining : RestartAfterUninstallNotCovered;
    }

    /// <summary>The note of a combined uninstall and restart: the removal's, then why no confirmation opened (if none did).</summary>
    internal static string NoteAfterUninstallRestart(string removal, string restart) =>
        restart == "confirm" || restart.Length == 0 ? removal : (removal.Length > 0 ? removal + " " + restart : restart);

    private static List<PendingClientChange>? _removals;
    private static long _removalsAt;

    /// <summary>Seconds the client's pending removals are reused (review L-c: a ledger read and a file check per row).</summary>
    internal const double RemovalsCacheSeconds = 5;

    /// <summary>
    /// Review L-c (2026-10-08): the ledger rows marked for removal, read at most every <see cref="RemovalsCacheSeconds"/>
    /// (the list key asks every 2 s on the main thread). A removal this window marks is seen within that time, and
    /// <see cref="ForgetLedgerRemovals"/> drops the answer at once.
    /// </summary>
    private static List<PendingClientChange> CachedLedgerRemovals()
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        var cached = _removals;
        if (cached != null && now - _removalsAt < (long)(RemovalsCacheSeconds * System.Diagnostics.Stopwatch.Frequency)) return cached;
        var fresh = new List<PendingClientChange>();
        AddLedgerRemovals(fresh, SafeLedger(), ClientRemovalMarked);
        _removals = fresh;
        _removalsAt = now;
        return fresh;
    }

    /// <summary>Drop the cached removals (an uninstall or a cancel just changed them).</summary>
    internal static void ForgetLedgerRemovals() => _removals = null;

    /// <summary>Each removal the list does not already name (by plugin id or name).</summary>
    internal static void MergeRemovals(List<PendingClientChange> list, IReadOnlyList<PendingClientChange> removals)
    {
        foreach (var r in removals)
        {
            bool named = false;
            foreach (var c in list)
            {
                if ((r.Id.Length > 0 && string.Equals(c.Id, r.Id, StringComparison.OrdinalIgnoreCase))
                    || (r.Name.Length > 0 && string.Equals(c.Name, r.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    named = true;
                    break;
                }
            }

            if (!named) list.Add(r);
        }
    }

    private static IReadOnlyList<InstalledLedgerRow> SafeLedger()
    {
        try { return CurrentLedger(); }
        catch { return Array.Empty<InstalledLedgerRow>(); }
    }

    /// <summary>F28: the client file a ledger row names carries its removal marker (tests replace it).</summary>
    internal static Func<string, bool> ClientRemovalMarked { get; set; } = DefaultRemovalMarked;

    private static bool DefaultRemovalMarked(string filePath)
    {
        try { return filePath.Length > 0 && System.IO.File.Exists(UninstallMarkers.MarkerFor(filePath)); }
        catch { return false; }
    }

    /// <summary>
    /// F28 (live 2026-10-08): a client mod marked for removal at the next start (the Mods page's Uninstall of a loaded copy,
    /// or the Install tab's) waits for a restart as an install does. The Install tab's rows count only listings in its index,
    /// and the ledger fallback counted only pending downloads, so a removal of a mod the index does not list reached no
    /// Restart offer. Each client ledger row whose file carries the removal marker is one removal, once per package, unless
    /// the list already names that mod.
    /// </summary>
    internal static void AddLedgerRemovals(List<PendingClientChange> list, IReadOnlyList<InstalledLedgerRow> ledger, Func<string, bool> marked)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in ledger)
        {
            if (!string.Equals(row.Root, InstalledState.ClientRoot, StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(row.Category, ServerInstallLedger.CategoryPending, StringComparison.OrdinalIgnoreCase)) continue;
            bool isMarked;
            try { isMarked = marked(row.FilePath); }
            catch { isMarked = false; }
            if (!isMarked) continue;
            string name = ModMenuInventory.DisplayNameOfLedger(row, row.Id);
            string package = row.Source.Length > 0 && InstalledState.TrySplitPackageSource(row.Source, out string p, out _) ? p : row.Id;
            if (!seen.Add(package.Length > 0 ? package : name)) continue;
            bool listed = false;
            foreach (var c in list)
            {
                if ((row.PluginId.Length > 0 && string.Equals(c.Id, row.PluginId, StringComparison.OrdinalIgnoreCase))
                    || (name.Length > 0 && string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)))
                {
                    listed = true;
                    break;
                }
            }

            if (listed) continue;
            list.Add(new PendingClientChange
            {
                Id = row.PluginId,
                Name = name,
                Version = row.Version,
                Action = PendingClientChange.ActionRemove,
            });
        }
    }

    private static bool PluginLoadedNow(string id)
    {
        try
        {
            foreach (var mod in ModManifest.EnumerateLoaded())
                if (string.Equals(mod.Id, id, StringComparison.OrdinalIgnoreCase)) return true;
        }
        catch { /* unknown is not loaded */ }

        return false;
    }

    /// <summary>AUTOUPDATE5: a Progenitor release staged for the next launch is one more change the restart applies.</summary>
    private static void AddProgenitorRestart(List<PendingClientChange> list)
    {
        try
        {
            if (SelfUpdates.PendingRestart() is { } p)
                list.Add(new PendingClientChange { Id = "progenitor", Name = p.Name, Version = p.Version, Action = PendingClientChange.ActionUpdate });
        }
        catch { /* nothing staged */ }
    }

    /// <summary>After the restart: whether this start runs the change's plugin (its id, else a loaded row with its name).</summary>
    internal static bool RunsNowAfterRestart(PendingClientChange change)
    {
        if (change == null) return false;
        if (change.Id.Length > 0 && PluginLoadedNow(change.Id)) return true;
        try
        {
            foreach (var row in InventorySnapshot().Rows)
            {
                if ((row.Location ?? "").IndexOf("server list", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                if ((change.Id.Length > 0 && string.Equals(row.Id, change.Id, StringComparison.OrdinalIgnoreCase))
                    || (change.Name.Length > 0 && string.Equals(row.DisplayName, change.Name, StringComparison.OrdinalIgnoreCase)))
                    return true;
            }
        }
        catch { /* no inventory */ }

        return false;
    }

    /// <summary>The bar's part of the Mods list key: the list is rebuilt when the bar changes.</summary>
    internal static string RestartListKey()
    {
        // The list key must stay cheap (HOSTS1 round 6): the pending rows are read at most every 2 s for it. The band itself
        // is always built from a fresh read, and a pending install moves the ledger stamp, which rebuilds the list anyway.
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_restartKeyAt != 0 && now - _restartKeyAt < 2 * System.Diagnostics.Stopwatch.Frequency) return _restartKey;
        _restartKeyAt = now;
        try
        {
            var offer = JoinFlowClient.CurrentRestartOffer();
            _restartKey = offer.Shown ? offer.Key : "none";
        }
        catch { _restartKey = "none"; }

        return _restartKey;
    }

    private static long _restartKeyAt;
    private static string _restartKey = "none";

    /// <summary>
    /// The Mods tab: the same bar at the top of the list (two band rows in the narrow column: the sentence, then the button).
    /// </summary>
    private static void AddListRestartBand(IUiPanel panel)
    {
        ClientRestartOffer offer;
        try { offer = JoinFlowClient.CurrentRestartOffer(); }
        catch { offer = ClientRestartOffer.None; }
        if (!offer.Shown) return;
        var text = panel.CellRow(new CellRowSpec(ModsRestartTextKey, new List<CellSpec>
        {
            new CellSpec(CellKind.Text, "text").With(text: offer.Sentence, width: CellWidth.Fill(80f), tone: CellTone.Pending),
        }, band: true));
        var button = panel.CellRow(new CellRowSpec(ModsRestartButtonKey, new List<CellSpec>
        {
            new CellSpec(CellKind.Spacer, "gap").With(width: CellWidth.Fill(0f)),
            new CellSpec(CellKind.Button, "restart").With(text: offer.Button, align: CellAlign.Right, skin: CellSkin.KeyCap),
        }, height: ModSettingsRowGrid.ActionRowHeight, band: true));
        button.OnClick("restart", () => JoinFlowClient.PressRestartButton());
        var described = offer.Describe();
        _listExtras.Add((text, ModsRestartTextKey, () => (object?)new Dictionary<string, object?> { ["pendingRestart"] = described }));
        _listExtras.Add((button, ModsRestartButtonKey, () => (object?)new Dictionary<string, object?> { ["pendingRestart"] = described }));
    }

    /// <summary>
    /// Bridge <c>mods_ui restart</c>: <c>op</c> <c>read</c> (default) the bar and the records; <c>press</c> the button (opens
    /// the confirmation); <c>confirm</c> / <c>cancel</c> answer it; <c>pickup</c> takes a dry run's after-restart record now.
    /// <c>dryRun</c> with <c>confirm</c> writes the records and reports the relaunch without leaving or quitting.
    /// </summary>
    public static object RestartOp(string? op, bool dryRun)
    {
        string o = (op ?? "read").Trim().ToLowerInvariant();
        string result = o switch
        {
            "read" => "read",
            "press" => JoinFlowClient.PressRestartButton(),
            "confirm" => JoinFlowClient.AnswerRestart(yes: true, dryRun: dryRun),
            "cancel" => JoinFlowClient.AnswerRestart(yes: false, dryRun: dryRun),
            "pickup" => "pickup: " + JoinFlowClient.PickUpInstallRestart("bridge"),
            _ => throw new ArgumentException("args.op must be read, press, confirm, cancel or pickup (got '" + o + "')"),
        };
        var d = JoinFlowClient.DescribeInstallRestart();
        d["op"] = o;
        d["dryRun"] = dryRun;
        d["result"] = result;
        return d;
    }
}
