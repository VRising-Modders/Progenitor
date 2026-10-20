using System;
using System.Collections.Generic;
using System.IO;
using Progenitor.Ui;

namespace Progenitor.Install;

/// <summary>
/// INDEX1: installed mods a Progenitor Index takedown names (<c>revoked[]</c>, mode Warning or Disable), as mod problems
/// (<see cref="ModProblems"/>), so the Mods page flags the installed copy with the reason. Report only: nothing is turned
/// off or removed. A copy is matched by its install-ledger row (package and version), whichever source installed it.
/// </summary>
internal sealed class IndexRevocationSource : IModProblemSource
{
    public const string Kind = "index-takedown";

    public string Name => "progenitor index takedowns";

    /// <summary>One installed copy: its plugin id, listing key (<c>Owner-Name</c>) and version.</summary>
    internal readonly record struct Installed(string PluginId, string Package, string Version);

    /// <summary>Tests: the installed copies (null: this client's install ledger).</summary>
    internal static Func<IEnumerable<Installed>>? InstalledProbe { get; set; }

    /// <summary>Tests: the Index (null: the shared source of this client's root).</summary>
    internal static Func<IndexDocument?>? IndexProbe { get; set; }

    public IEnumerable<ModProblem> Current()
    {
        IndexDocument? doc;
        try { doc = IndexProbe != null ? IndexProbe() : LiveIndex(); }
        catch { doc = null; }
        if (doc == null || doc.Revoked.Count == 0) return Array.Empty<ModProblem>();

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
        foreach (var i in installed)
        {
            if (i.PluginId.Length == 0 || !seen.Add(i.PluginId)) continue;
            var r = IndexCatalog.RevocationForInstalled(doc, i.Package, i.Version);
            if (r == null || r.Mode is not (IndexRevocationMode.Warning or IndexRevocationMode.Disable)) continue;
            bool down = r.Mode == IndexRevocationMode.Disable;
            string why = r.Reason.Length > 0 ? ": " + r.Reason : "";
            list.Add(new ModProblem(
                ModProblemSeverity.Warning,
                Kind,
                i.PluginId,
                (down ? "The Progenitor Index took this version down" : "The Progenitor Index warns about this version") + why
                    + (why.EndsWith(".", StringComparison.Ordinal) ? "" : "."),
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
                rows.Add(new Installed(f.PluginId, package, version));
            }

            _ledgerStamp = stamp;
            _ledgerRows = rows;
            return rows;
        }
#endif
    }
}
