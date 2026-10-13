using System;
using Progenitor.Networking;

namespace Progenitor.Install;

/// <summary>
/// LIST2 sort keys of the Install list: the header's sortable columns. INSTALLCOLS1 added
/// <see cref="Author"/> and <see cref="Installed"/>; the numbers of the first three are unchanged.
/// </summary>
internal enum InstallSort
{
    Downloads = 0,
    Released = 1,
    Name = 2,
    Author = 3,
    Installed = 4,
}

/// <summary>Side chip on the Install search row. Single choice; <see cref="Any"/> is the default.</summary>
internal enum InstallSideFilter
{
    Any = 0,
    Server = 1,
    Client = 2,
    Both = 3,
}

/// <summary>
/// INSTALLFILTERS1: the Install list's <c>Show</c> group. Single choice; <see cref="All"/> is the
/// default. The pane decides each from the same per-side cells the rows draw.
/// </summary>
internal enum InstallShowFilter
{
    All = 0,

    /// <summary>A copy on either side (this client or the picked server): installed, pending, behind or leaving.</summary>
    Installed = 1,

    /// <summary>One side has it and the other side the mod needs does not (the row's <c>! Missing</c>).</summary>
    MissingSide = 2,

    /// <summary>Either side is older than the listing or than the other side (the row's <c>↑</c>).</summary>
    NeedsUpdate = 3,
}

/// <summary>
/// Filter state under the Install search box. Combines with the text search. Kept per
/// process in memory by the pane (never on disk); a new pane in the same Core generation
/// reuses it, a Core swap starts from the defaults like the tab memory does.
/// </summary>
internal sealed class InstallSearchFilters
{
    public const string SideCaption = "Side:";
    public const string ServerCaption = "Server";
    public const string ClientCaption = "Client";
    public const string BothCaption = "Both";
    public const string AnyCaption = "Any";
    /// <summary>Readback caption of the dependency filter: a declaration, never hot-swap qualification (TAG1).</summary>
    public const string ProgenitorSupportedCaption = "declares the Progenitor dependency — not hot-swap qualification";
    public const string CurrentUpdateOnlyCaption = "Current update only";

    /// <summary>Thunderstore package name of this framework (<c>packaging/thunderstore/manifest.json</c>).</summary>
    public const string ProgenitorPackageName = "Progenitor";

    /// <summary>Plugin GUID a <c>progenitor.json</c> names when it depends on the framework.</summary>
    public const string ProgenitorPluginId = "progenitor";

    public InstallSideFilter Side { get; set; } = InstallSideFilter.Any;

    /// <summary>INSTALLFILTERS1: the <c>Show</c> group (single choice). The pane applies it; <see cref="Matches"/> does not.</summary>
    public InstallShowFilter Show { get; set; } = InstallShowFilter.All;

    /// <summary>On: only packages that declare a dependency on the Progenitor package. Dependency never means hot load.</summary>
    public bool ProgenitorSupported { get; set; }

    /// <summary>On by default: only packages tagged with the resolved current update; untagged packages hide. Has no effect while the current update is unknown.</summary>
    public bool CurrentUpdateOnly { get; set; } = true;

    /// <summary>
    /// LIST2 <c>No restart needed</c> chip: only packages whose Progenitor manifest declares
    /// hot-loading (<see cref="ManifestMod.HotLoadable"/>). The declaration is the author's
    /// claim; the planner still decides at install time.
    /// </summary>
    public bool NoRestartOnly { get; set; }

    /// <summary>
    /// LIST2 sort order of the list. Not a filter: <see cref="Reset"/> keeps it.
    ///
    /// <para>POLISH2 item 1 (user, 2026-09-13: "change the default sort from Downloads to recent"):
    /// the default is <see cref="InstallSort.Released"/> — the newest listing first — so the tab opens
    /// on what was published most recently rather than on the all-time download leaders. Downloads is
    /// one click of the toolbar selector away and is still the header's own sortable column.</para>
    /// </summary>
    public InstallSort Sort { get; set; } = InstallSort.Released;

    /// <summary>
    /// INSTALLCOLS1: the column is sorted against its natural direction — a second click on the
    /// sorted header. Natural is A to Z for Mod and Author and newest / most first for Released,
    /// Installed and Downloads, so false keeps every sort exactly as it was before headers could
    /// reverse. Not a filter: <see cref="Reset"/> keeps it.
    /// </summary>
    public bool SortReversed { get; set; }

    public bool IsDefault =>
        Side == InstallSideFilter.Any && Show == InstallShowFilter.All && !ProgenitorSupported && CurrentUpdateOnly && !NoRestartOnly;

    /// <summary><c>Clear filters</c>: any side, no restart chip, current update on.</summary>
    public void Reset()
    {
        Side = InstallSideFilter.Any;
        Show = InstallShowFilter.All;
        ProgenitorSupported = false;
        CurrentUpdateOnly = true;
        NoRestartOnly = false;
    }

    public InstallSearchFilters Clone() => new()
    {
        Side = Side,
        Show = Show,
        ProgenitorSupported = ProgenitorSupported,
        CurrentUpdateOnly = CurrentUpdateOnly,
        NoRestartOnly = NoRestartOnly,
        Sort = Sort,
        SortReversed = SortReversed,
    };

    /// <summary>
    /// True when the entry passes every filter. <paramref name="currentUpdateTag"/> is the
    /// resolved current tag (<see cref="CatalogUpdateTags.ResolveCurrent"/>); when it is
    /// null (unknown) the update filter has nothing to judge by and lets everything through.
    /// </summary>
    public bool Matches(CatalogEntry entry, string? currentUpdateTag)
    {
        if (entry == null) return false;
        if (!SideMatches(entry)) return false;
        if (ProgenitorSupported && !DeclaresProgenitorDependency(entry)) return false;
        if (NoRestartOnly && !DeclaresNoRestart(entry)) return false;
        if (CurrentUpdateOnly && !string.IsNullOrEmpty(currentUpdateTag)
            && !CatalogUpdateTags.IsTagged(entry, currentUpdateTag))
            return false;
        return true;
    }

    public bool SideMatches(CatalogEntry entry)
    {
        if (Side == InstallSideFilter.Any) return true;
        PackageSide derived = PackageSideDerivation.Derive(entry).Side;
        return Side switch
        {
            InstallSideFilter.Server => derived == PackageSide.Server,
            InstallSideFilter.Client => derived == PackageSide.Client,
            InstallSideFilter.Both => derived == PackageSide.Both,
            _ => true,
        };
    }

    /// <summary>A package whose manifest declares <c>hotLoadable</c>: the only listing-time evidence that no restart is needed.</summary>
    public static bool DeclaresNoRestart(CatalogEntry entry) =>
        entry != null && entry.Manifest != null && entry.Manifest.HotLoadable;

    /// <summary>
    /// A Thunderstore dependency <c>&lt;owner&gt;-Progenitor-&lt;version&gt;</c>, or a
    /// <c>progenitor.json</c> plugin dependency on <c>progenitor</c>. This is the
    /// author's declaration only (<see cref="InstallReasons.ProgenitorDepDoesNotGrantHotLoad"/>).
    /// </summary>
    public static bool DeclaresProgenitorDependency(CatalogEntry entry)
    {
        if (entry == null) return false;
        for (int i = 0; i < entry.ForeignDependencies.Count; i++)
        {
            if (IsProgenitorListingDependency(entry.ForeignDependencies[i])) return true;
        }

        for (int i = 0; i < entry.PluginDependencies.Count; i++)
        {
            if (string.Equals(entry.PluginDependencies[i].Id, ProgenitorPluginId, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>The middle segment of <c>{owner}-{name}-{version}</c> is exactly <c>Progenitor</c>.</summary>
    public static bool IsProgenitorListingDependency(string? dependency)
    {
        if (string.IsNullOrWhiteSpace(dependency)) return false;
        string d = dependency.Trim();
        int first = d.IndexOf('-');
        if (first <= 0) return false;
        int last = d.LastIndexOf('-');
        if (last <= first + 1) return false;
        string name = d.Substring(first + 1, last - first - 1);
        return string.Equals(name, ProgenitorPackageName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>INSTALLFILTERS1: readback name of a Show choice.</summary>
    public static string ShowName(InstallShowFilter show) => show switch
    {
        InstallShowFilter.Installed => "installed",
        InstallShowFilter.MissingSide => "missing",
        InstallShowFilter.NeedsUpdate => "update",
        _ => "all",
    };

    public static string SideName(InstallSideFilter side) => side switch
    {
        InstallSideFilter.Server => "server",
        InstallSideFilter.Client => "client",
        InstallSideFilter.Both => "both",
        _ => "any",
    };

    public static string SideChipCaption(InstallSideFilter side) => side switch
    {
        InstallSideFilter.Server => ServerCaption,
        InstallSideFilter.Client => ClientCaption,
        InstallSideFilter.Both => BothCaption,
        _ => AnyCaption,
    };
}
