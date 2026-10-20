using System;
using System.Collections.Generic;
using System.IO;
using Progenitor.Ui;

namespace Progenitor.Install;

/// <summary>
/// INDEX1: installed mods a Progenitor Index takedown names (<c>revoked[]</c>, mode Warning or Disable), as mod problems
/// (<see cref="ModProblems"/>), so the Mods page flags the installed copy with the reason. A copy is matched by its
/// install-ledger row (package and version), whichever source installed it. A Disable takedown also keeps the copy from
/// loading (<see cref="DisableReason"/>, read by <c>ModEnableState.OffReason</c> when a Progenitor plugin loads, at a start
/// or a hot load); nothing is deleted, and the copy loads again once the takedown is lifted. A Thunderstore install of a mod
/// the Index now lists gets an information line saying how to move it.
/// </summary>
internal sealed class IndexRevocationSource : IModProblemSource
{
    public const string Kind = "index-takedown";

    /// <summary>The information line of a Thunderstore install the Index now lists.</summary>
    public const string MovableKind = "index-listed";

    public string Name => "progenitor index takedowns";

    /// <summary>One installed copy: its plugin id, listing key (<c>Owner-Name</c>), version and whether it came from the Index.</summary>
    internal readonly record struct Installed(string PluginId, string Package, string Version, bool FromIndex = false);

    /// <summary>
    /// Why plugin <paramref name="pluginId"/> must not load: an installed copy's version is taken down (Disable) in the
    /// Progenitor Index this process holds (its verified cache at start). Null when it may load. Never throws.
    /// </summary>
    internal static string? DisableReason(string? pluginId)
    {
        if (string.IsNullOrWhiteSpace(pluginId)) return null;
        try
        {
            var doc = IndexProbe != null ? IndexProbe() : LiveIndex();
            if (doc == null || doc.Revoked.Count == 0) return null;
            var installed = InstalledProbe != null ? InstalledProbe() : LiveInstalled();
            return DisableReasonFor(doc, installed, pluginId!.Trim());
        }
        catch { return null; }
    }

    /// <summary>What a Disable takedown can do to an installed plugin now (M5).</summary>
    internal enum TakedownLoadState
    {
        /// <summary>A Progenitor plugin the load gate skipped: it is not running.</summary>
        NotLoaded = 0,

        /// <summary>A Progenitor plugin running now (the takedown arrived after it loaded): it stops at the next start.</summary>
        Running = 1,

        /// <summary>An ordinary BepInEx plugin: the gate cannot stop it.</summary>
        Plain = 2,
    }

    /// <summary>Tests: the plugin's state (null: this Core generation's mod registry).</summary>
    internal static Func<string, TakedownLoadState>? StateProbe { get; set; }

    private static TakedownLoadState StateOf(string pluginId)
    {
        try
        {
            if (StateProbe != null) return StateProbe(pluginId);
            if (!Progenitor.Plugins.ModRegistry.TryGet(pluginId, out var e)) return TakedownLoadState.Plain;
            return e.Running ? TakedownLoadState.Running : TakedownLoadState.NotLoaded;
        }
        catch { return TakedownLoadState.NotLoaded; }
    }

    /// <summary>The sentence a Disable takedown adds, per case.</summary>
    internal static string DisableWords(TakedownLoadState state) => state switch
    {
        TakedownLoadState.Running => "It stops loading at the next start; nothing is deleted, and it loads again if the takedown is lifted.",
        TakedownLoadState.Plain => "It is a plain BepInEx plugin, which Progenitor cannot stop from loading.",
        _ => "Progenitor does not load it. Nothing was deleted: it loads again if the takedown is lifted.",
    };

    /// <summary>The pure rule behind <see cref="DisableReason"/>.</summary>
    internal static string? DisableReasonFor(IndexDocument doc, IEnumerable<Installed> installed, string pluginId)
    {
        foreach (var i in installed)
        {
            if (!string.Equals(i.PluginId, pluginId, StringComparison.OrdinalIgnoreCase)) continue;
            var r = IndexCatalog.RevocationForInstalled(doc, i.Package, i.Version, i.FromIndex);
            if (r is { Mode: IndexRevocationMode.Disable })
                return "taken down from the Progenitor Index" + (r.Reason.Length > 0 ? " (" + r.Reason.TrimEnd('.') + ")" : "");
        }

        return null;
    }

    /// <summary>Tests: the installed copies (null: this client's install ledger).</summary>
    internal static Func<IEnumerable<Installed>>? InstalledProbe { get; set; }

    /// <summary>Tests: the Index (null: the shared source of this client's root).</summary>
    internal static Func<IndexDocument?>? IndexProbe { get; set; }

    public IEnumerable<ModProblem> Current()
    {
        IndexDocument? doc;
        try { doc = IndexProbe != null ? IndexProbe() : LiveIndex(); }
        catch { doc = null; }
        if (doc == null || (doc.Revoked.Count == 0 && doc.Packages.Count == 0)) return Array.Empty<ModProblem>();

        IEnumerable<Installed> installed;
        try { installed = InstalledProbe != null ? InstalledProbe() : LiveInstalled(); }
        catch { installed = Array.Empty<Installed>(); }
        return For(doc, installed);
    }

    /// <summary>The problems of <paramref name="installed"/> under <paramref name="doc"/>'s takedowns (pure).</summary>
    internal static List<ModProblem> For(IndexDocument doc, IEnumerable<Installed> installed)
    {
        var list = new List<ModProblem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in IndexCatalog.ToEntries(doc, ""))
            listed.Add(row.Key);
        foreach (var i in installed)
        {
            if (i.PluginId.Length == 0 || !seen.Add(i.PluginId)) continue;
            var r = IndexCatalog.RevocationForInstalled(doc, i.Package, i.Version, i.FromIndex);
            if (r == null || r.Mode is not (IndexRevocationMode.Warning or IndexRevocationMode.Disable))
            {
                // Updates stay with their source: a Thunderstore install the Index now lists is moved by hand.
                if (!i.FromIndex && listed.Contains(i.Package))
                    list.Add(new ModProblem(
                        ModProblemSeverity.Info,
                        MovableKind,
                        i.PluginId,
                        "Installed from Thunderstore, and the Progenitor Index lists it too. It keeps updating from Thunderstore.",
                        "To move it to the Progenitor Index, uninstall it and install it again from Install mods.",
                        key: i.Package,
                        listLine: "Also in the Progenitor Index"));
                continue;
            }

            bool down = r.Mode == IndexRevocationMode.Disable;
            string why = r.Reason.Length > 0 ? ": " + r.Reason : "";
            list.Add(new ModProblem(
                ModProblemSeverity.Warning,
                Kind,
                i.PluginId,
                (down ? "The Progenitor Index took this version down" : "The Progenitor Index warns about this version") + why
                    + (why.EndsWith(".", StringComparison.Ordinal) ? "" : ".")
                    + (down ? " " + DisableWords(StateOf(i.PluginId)) : ""),
                down ? "Uninstall it." : "Read the warning before you keep using it.",
                details: i.Package + " " + i.Version + " (" + r.Date + ")",
                key: i.Package + "@" + i.Version,
                listLine: down ? "Taken down from the Progenitor Index" : "Warning from the Progenitor Index"));
        }

        return list;
    }

    private static IndexDocument? LiveIndex()
    {
#if PROGENITOR_TESTS
        return null;
#else
        var source = ProgenitorIndexSource.ForRoot(BepInEx.Paths.BepInExRootPath);
        return source?.Current;
#endif
    }

    private static readonly object LedgerGate = new();
    private static string _ledgerStamp = "";
    private static List<Installed> _ledgerRows = new();

    private static IEnumerable<Installed> LiveInstalled()
    {
#if PROGENITOR_TESTS
        return Array.Empty<Installed>();
#else
        string? game = ServerInstall.GameRootOf(BepInEx.Paths.BepInExRootPath);
        if (string.IsNullOrEmpty(game)) return Array.Empty<Installed>();
        string path = ServerInstallLedger.PathFor(game!);
        string stamp;
        try { stamp = File.Exists(path) ? File.GetLastWriteTimeUtc(path).Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture) : ""; }
        catch { stamp = ""; }
        lock (LedgerGate)
        {
            if (stamp == _ledgerStamp) return _ledgerRows;
            var rows = new List<Installed>();
            var ledger = stamp.Length == 0 ? null : ServerInstallLedger.TryRead(game!);
            foreach (var f in ledger?.Files ?? (IReadOnlyList<ServerInstallLedgerEntry>)Array.Empty<ServerInstallLedgerEntry>())
            {
                if (f.Category == ServerInstallLedger.CategoryFramework) continue;
                if (!InstalledState.TrySplitPackageSource(f.Source, out string package, out string version)) continue;
                rows.Add(new Installed(f.PluginId, package, version, f.FromIndex));
            }

            _ledgerStamp = stamp;
            _ledgerRows = rows;
            return rows;
        }
#endif
    }
}
