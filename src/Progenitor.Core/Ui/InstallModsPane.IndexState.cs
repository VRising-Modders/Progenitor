using System;
using System.Collections.Generic;
using Progenitor.Install;

namespace Progenitor.Ui;

/// <summary>What the Install tab says about the mod list itself. Game-free.</summary>
internal sealed class InstallIndexView
{
    /// <summary>The surface the build draws (Ready: the list).</summary>
    public InstallPaneSurface Surface { get; init; }

    /// <summary>The last failed check (null after a success): the tooltip's words and the log's cause.</summary>
    public IndexProblem? Problem { get; init; }

    /// <summary>No list on show: the one centered line (<c>Connecting to Thunderstore…</c>, then <c>…still trying</c>).</summary>
    public string Message { get; init; } = "";

    /// <summary>No list on show: a second, smaller line about the source (the stand-in, or the session fallback), or empty.</summary>
    public string SubLine { get; init; } = "";

    /// <summary>A list on show: the small muted line over it (source and age, <c>Updated 2 hours ago</c>), or empty.</summary>
    public string StatusLine { get; init; } = "";

    /// <summary>The hover text on the line (the cause and the raw error), or empty when there is nothing to explain.</summary>
    public string Tooltip { get; init; } = "";

    /// <summary>The stand-in's <c>host:port</c> while it is the source, else null.</summary>
    public string? DevSource { get; init; }

    /// <summary>The stand-in refused, and this session uses thunderstore.io (the cfg is unchanged).</summary>
    public bool Fallback { get; init; }

    /// <summary>Several checks failed in a row; the tab says so (still quietly).</summary>
    public bool Loud { get; init; }
}

/// <summary>
/// INSTALLERR1 (user decision 2026-10-14: "in most cases the user just wants it to work, retry automatically, don't worry
/// about the buttons"): the Install tab never shows an error wall or a Retry button. While the tab is shown a failed check
/// is retried by itself (<see cref="IndexAutoRetry"/>: 2 s, 5 s, 10 s, 30 s, then every 60 s), and at once when the tab is
/// opened, the network comes back or the source changes. With no list it reads <c>Connecting to Thunderstore…</c>, and
/// only after several failures <c>Can't reach Thunderstore right now; still trying</c>, the cause and the raw error in its
/// tooltip and the log. A list on show stays, under one small muted age line. A local stand-in that refuses is left for
/// thunderstore.io for the session, without touching the cfg.
/// </summary>
internal sealed partial class InstallModsPane
{
    internal const string IndexStatusKey = "install-index-status";
    internal const string IndexLineKey = "install-index-line";
    internal const string IndexSubKey = "install-index-sub";
    internal const string CellIndexText = "text";
    public const string DetailsTooltipTitle = "Details";

    /// <summary>The centered line's top margin, size and column; the sub line's and the status line's size (canvas units).</summary>
    internal const float StateBlockTop = 96f;
    internal const float StateLineSize = GameArt.GameSettingsLook.DetailsBodyText;
    internal const float StateLineMaxWidth = 640f;
    internal const float StateSubSize = 16f;
    internal const float StatusLineSize = 16f;
    internal const float StatusLineRow = 24f;

    /// <summary>The stand-in's <c>host:port</c> while it is the source (tests set it; default <see cref="IndexProblem.DevSourceNow"/>).</summary>
    internal Func<string?>? DevSourceProbe { get; set; }

    /// <summary>The session fell back from the stand-in (tests set it; default <see cref="ThunderstoreMirror.SessionFallbackActive"/>).</summary>
    internal Func<bool>? FallbackProbe { get; set; }

    /// <summary>The effective index URL now (tests set it; default <see cref="IndexProblem.SourceUrlNow"/>).</summary>
    internal Func<string>? SourceUrlProbe { get; set; }

    /// <summary>The Install tab is on screen (the Mods menu's; default: the pane was built). Automatic retries run only then.</summary>
    internal Func<bool>? TabShownProbe { get; set; }

    /// <summary>
    /// The index source changed under this pane (the stand-in refused and the session fell back): the Mods menu follows it,
    /// posted to its next main-thread turn, never inside the result's own call.
    /// </summary>
    internal Action? SourceChangedHook { get; set; }

    private IndexProblem? _problem;
    private DateTime? _indexStampUtc;
    private bool _indexFromCache;
    private string? _indexSource;
    private readonly IndexAutoRetry _autoRetry = new();
    private int _retryGeneration;
    private DateTime? _nextRetryUtc;
    private Action? _refreshIndex;
    private IUiCellRow? _indexStatus;
    private readonly List<IUiHandle> _indexBlock = new();
    private InstallIndexView? _lastIndexView;

    /// <summary>The failure the last check reported (null after a success).</summary>
    internal IndexProblem? IndexProblemShown => _problem;

    /// <summary>The source URL of the list on show (null: not known).</summary>
    internal string? IndexSourceShown => _indexSource;

    internal IndexAutoRetry AutoRetry => _autoRetry;

    /// <summary>When the next automatic retry is due (null: none planned).</summary>
    internal DateTime? NextRetryUtc => _nextRetryUtc;

    /// <summary>The small status line over the list the last build drew (null: none).</summary>
    internal IUiCellRow? IndexStatusRow => _indexStatus;

    /// <summary>The centered rows the last build drew in place of the list (the line, the sub line).</summary>
    internal IReadOnlyList<IUiHandle> IndexBlockRows => _indexBlock;

    /// <summary>
    /// INSTALLERR1: a load the Mods menu runs itself has started (the new source's cache read off the main thread, then
    /// its check): the tab reads as connecting, not as "not loaded", until it reports.
    /// </summary>
    internal void NoteLoadStarted()
    {
        _indexLoadAttempted = true;
        _indexRefreshing = true;
        _lastError = null;
    }

    /// <summary>An install flow is under way (download and check, the review, the write): the tab is not rebuilt for a source change.</summary>
    internal bool FlowActive => _phase is InstallActionPhase.Downloading or InstallActionPhase.Reviewing or InstallActionPhase.Executing;

    private DateTime NowUtc()
    {
        try { return _clock?.UtcNow ?? DateTime.UtcNow; }
        catch { return DateTime.UtcNow; }
    }

    private string? DevSource()
    {
        try { return (DevSourceProbe ?? IndexProblem.DevSourceNow)(); }
        catch { return null; }
    }

    private bool Fallback()
    {
        try { return (FallbackProbe ?? (() => ThunderstoreMirror.SessionFallbackActive))(); }
        catch { return false; }
    }

    private string SourceUrl()
    {
        try { return (SourceUrlProbe ?? IndexProblem.SourceUrlNow)() ?? ""; }
        catch { return ""; }
    }

    private bool TabShown()
    {
        try { return TabShownProbe?.Invoke() ?? _panel != null; }
        catch { return false; }
    }

    /// <summary>
    /// The list on show belongs to another source than the current one (the dev URL was set or cleared, or the session fell
    /// back): it must not be shown. The Mods menu rebuilds the catalog for the new source.
    /// </summary>
    internal bool ShownFromOtherSource()
    {
        if (_indexSource == null) return false;
        string now = SourceUrl();
        return now.Length > 0 && !string.Equals(now.Trim(), _indexSource.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Remember what a check says (called from <see cref="NoteLoadResult(CatalogLoadResult)"/>).</summary>
    private void NoteIndexState(CatalogLoadResult result, string? sourceUrl, bool fromCache, bool keptCached, bool outstanding)
    {
        if (result.Ok)
        {
            _problem = null;
            _autoRetry.NoteSuccess();
            _retryGeneration++;
            _nextRetryUtc = null;
            _indexFromCache = fromCache;
            if (IndexProblem.TryParseStamp(result.IndexStamp, out var stamp)) _indexStampUtc = stamp;
            if (!string.IsNullOrWhiteSpace(sourceUrl)) _indexSource = sourceUrl!.Trim();
            return;
        }

        // A cancellation (a tab switch, a rebuilt catalog) is not a failure.
        if (string.Equals(result.Error, InstallReasons.FetchCancelled, StringComparison.Ordinal)) return;
        // The cause from the transport's classification (exception types, status), never the localized text.
        string? dev = DevSource();
        _problem = IndexProblem.From(new CatalogLoadResult(false, result.Error ?? InstallModsCopy.IndexNotLoaded, 0, null)
        {
            FailureCause = result.FailureCause,
            StatusCode = result.StatusCode,
            Paused = result.Paused,
        }, dev);
        // One failure per check: a joined caller's second report of the same check is not counted again.
        if (!outstanding) return;
        var delay = _autoRetry.NoteFailure();
        try
        {
            Plugin.Logger?.LogInfo("Install: the mod list did not load (" + _problem.CauseWord + ", " + _autoRetry.Failures
                + " in a row): " + _problem.Detail + (keptCached ? "; the last list stays on show" : "")
                + "; next try in " + ((int)delay.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture) + " s.");
        }
        catch { /* */ }

        // The local stand-in refused: this session uses thunderstore.io (the cfg is untouched), and the menu follows.
        if (dev != null && _problem.Cause == IndexProblemCause.Refused && ThunderstoreMirror.FallBackForSession())
        {
            try { Plugin.Logger?.LogWarning("Install: the local test mod list at " + dev + " refused the connection; using thunderstore.io for this session."); }
            catch { /* */ }
            _retryGeneration++;
            _nextRetryUtc = null;
            try { SourceChangedHook?.Invoke(); }
            catch (Exception e) { Plugin.Logger?.LogWarning("Install: following the fallback source threw: " + e.Message); }
            return;
        }

        ScheduleAutoRetry(delay);
    }

    /// <summary>Plan the next automatic retry after <paramref name="delay"/>; it runs only while the tab is shown.</summary>
    private void ScheduleAutoRetry(TimeSpan delay)
    {
        var post = PostDelayedHook;
        int generation = ++_retryGeneration;
        _nextRetryUtc = NowUtc() + delay;
        if (post == null) return;
        try { post(delay.TotalSeconds, () => { if (generation == _retryGeneration) RunAutoRetry(); }); }
        catch { /* the next open retries */ }
    }

    /// <summary>A planned retry is due: send it when the tab is shown and nothing is in flight. Returns what it did.</summary>
    internal string RunAutoRetry()
    {
        if (_problem == null) return "ok";
        if (_refreshIndex == null) return "unwired";
        if (_indexRefreshing) return "in-flight";
        if (!TabShown()) return "hidden"; // the open of the tab retries (RetryNowIfFailing)
        _nextRetryUtc = null;
        try { _refreshIndex(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Install: the automatic retry threw: " + e.Message); }
        return "sent";
    }

    /// <summary>
    /// Retry now when the last check failed: the tab was opened, the network came back, or the source changed. Cancels the
    /// planned retry; does nothing while a check is in flight. Returns what it did.
    /// </summary>
    internal string RetryNowIfFailing()
    {
        if (_problem == null) return "ok";
        _retryGeneration++;
        return RunAutoRetry();
    }

    /// <summary>What to say about the mod list now. Game-free; <paramref name="nowUtc"/> words the list's age.</summary>
    internal InstallIndexView IndexView(bool thunderstoreEnabled, bool nexusEnabled, DateTime nowUtc)
    {
        var surface = Surface(thunderstoreEnabled, nexusEnabled);
        string? dev = DevSource();
        bool fallback = dev == null && Fallback();
        string sourceNote = dev != null ? IndexProblem.DevNotice(dev) : fallback ? IndexProblem.DevFallbackNotice : "";
        bool loud = _problem != null && _autoRetry.Loud;

        if (surface == InstallPaneSurface.Error || surface == InstallPaneSurface.Loading)
        {
            return new InstallIndexView
            {
                Surface = surface,
                Problem = _problem,
                Message = loud ? IndexProblem.StillTrying(dev != null) : IndexProblem.Connecting(dev != null),
                SubLine = sourceNote,
                Tooltip = loud ? _problem!.Tooltip : "",
                DevSource = dev,
                Fallback = fallback,
                Loud = loud,
            };
        }

        if (surface != InstallPaneSurface.Ready)
        {
            return new InstallIndexView
            {
                Surface = surface,
                Message = SurfaceMessage(surface, _lastError),
                SubLine = sourceNote,
                DevSource = dev,
                Fallback = fallback,
            };
        }

        var parts = new List<string>();
        if (sourceNote.Length > 0) parts.Add(sourceNote);
        if (_indexStampUtc is { } at) parts.Add(IndexProblem.UpdatedAgo(at, nowUtc));
        return new InstallIndexView
        {
            Surface = surface,
            Problem = _problem,
            StatusLine = string.Join(SideCells.Dot, parts),
            Tooltip = _problem?.Tooltip ?? "",
            DevSource = dev,
            Fallback = fallback,
            Loud = loud,
        };
    }

    /// <summary>The small muted line over a list on show: one cell, 16 px, left-aligned with the rows' names.</summary>
    internal static CellRowSpec StatusRowSpec(string line) => new(
        IndexStatusKey,
        new List<CellSpec>
        {
            new CellSpec(CellKind.Muted, CellIndexText).With(text: line, width: CellWidth.Fill(160f), tone: CellTone.Muted,
                fontSize: StatusLineSize, wrap: CellWrap.Ellipsis),
        },
        height: StatusLineRow, rightInset: HeadBlockLayout.RightColumnInset, plain: true, compact: true,
        indent: ModSettingsRowGrid.NameInset);

    /// <summary>The centered line's column: <see cref="StateLineMaxWidth"/>, never wider than the row.</summary>
    internal static float StateLineWidth(float rowWidth) =>
        Math.Max(200f, Math.Min(StateLineMaxWidth, rowWidth - HeadBlockLayout.RightColumnInset - 40f));

    /// <summary>A centered muted line in place of the list (wrapping in its column, as tall as its lines).</summary>
    internal static CellRowSpec CenteredLineSpec(string key, string line, float rowWidth, float fontSize, float gapAbove)
    {
        float width = StateLineWidth(rowWidth);
        int lines = Math.Max(1, MenuPanelLayout.WrappedLines(line, width, fontSize, 3));
        return new CellRowSpec(
            key,
            new List<CellSpec>
            {
                new CellSpec(CellKind.Spacer, "lead"),
                new CellSpec(CellKind.Text, CellIndexText).With(text: line, width: CellWidth.Px(width), align: CellAlign.Center,
                    wrap: CellWrap.Wrap, fontSize: fontSize, tone: CellTone.Muted),
                new CellSpec(CellKind.Spacer, "trail"),
            },
            height: lines * fontSize * 1.3f + 8f, rightInset: HeadBlockLayout.RightColumnInset, plain: true, gapAbove: gapAbove);
    }

    /// <summary>The status line over a list on show (pinned above the restart bar and the column header).</summary>
    private void BuildIndexStatus(IUiPanel panel, InstallIndexView view)
    {
        _indexStatus = null;
        if (view.StatusLine.Length == 0) return;
        var row = panel.CellRow(StatusRowSpec(view.StatusLine));
        if (view.Tooltip.Length > 0) row.Tooltip(view.Tooltip, DetailsTooltipTitle);
        _indexStatus = row;
    }

    /// <summary>A surface other than the list: one centered muted line, and the source's line under it when there is one.</summary>
    private void BuildIndexSurface(IUiPanel panel, InstallIndexView view)
    {
        _indexBlock.Clear();
        _indexStatus = null;
        float rowWidth = ColumnRowWidth();
        var line = panel.CellRow(CenteredLineSpec(IndexLineKey, view.Message, rowWidth, StateLineSize, StateBlockTop));
        if (view.Tooltip.Length > 0) line.Tooltip(view.Tooltip, DetailsTooltipTitle);
        _indexBlock.Add(line);
        if (view.SubLine.Length > 0)
            _indexBlock.Add(panel.CellRow(CenteredLineSpec(IndexSubKey, view.SubLine, rowWidth, StateSubSize, -1f)));
    }

    /// <summary>Readback on the status line and the centered rows: <c>indexState {surface, message, statusLine, cause, …}</c>.</summary>
    private bool TryDescribeIndexRow(IUiHandle handle, out Dictionary<string, object?>? extra)
    {
        extra = null;
        bool ours = _indexStatus != null && ReferenceEquals(handle, _indexStatus);
        for (int i = 0; !ours && i < _indexBlock.Count; i++) ours = ReferenceEquals(handle, _indexBlock[i]);
        if (!ours) return false;
        extra = new Dictionary<string, object?> { ["indexState"] = DescribeIndexState() };
        return true;
    }

    internal Dictionary<string, object?> DescribeIndexState()
    {
        var view = _lastIndexView;
        return new Dictionary<string, object?>
        {
            ["surface"] = view?.Surface.ToString(),
            ["message"] = view?.Message,
            ["subLine"] = view?.SubLine,
            ["statusLine"] = view?.StatusLine,
            ["tooltip"] = view?.Tooltip,
            ["cause"] = _problem?.CauseWord,
            ["detail"] = _problem?.Detail,
            ["failures"] = _autoRetry.Failures,
            ["loud"] = view?.Loud,
            ["devSource"] = view?.DevSource,
            ["fallback"] = view?.Fallback,
            ["refreshing"] = _indexRefreshing,
            ["nextRetryUtc"] = _nextRetryUtc?.ToString("u"),
            ["source"] = _indexSource,
            ["fromCache"] = _indexFromCache,
            ["stamp"] = _indexStampUtc?.ToString("u"),
        };
    }
}
