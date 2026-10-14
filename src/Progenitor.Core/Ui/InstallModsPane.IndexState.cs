using System;
using System.Collections.Generic;
using Progenitor.Install;

namespace Progenitor.Ui;

/// <summary>What the Install tab draws above (or instead of) the list about the mod list itself. Game-free.</summary>
internal sealed class InstallIndexView
{
    /// <summary>The surface the build draws (Ready: the list).</summary>
    public InstallPaneSurface Surface { get; init; }

    /// <summary>The failure on show: the error block's words (Error), or the stale notice's tooltip (Ready).</summary>
    public IndexProblem? Problem { get; init; }

    /// <summary>The source and age line over a list on show, or empty for none.</summary>
    public string Notice { get; init; } = "";

    /// <summary>The notice's tone: Pending for a plain note, Orange for a failed refresh.</summary>
    public CellTone NoticeTone { get; init; } = CellTone.Muted;

    /// <summary>The notice row carries a Retry (a failed refresh over the list).</summary>
    public bool NoticeRetry { get; init; }

    /// <summary>The stand-in's <c>host:port</c> while it is the source, else null.</summary>
    public string? DevSource { get; init; }

    /// <summary>Offer <b>Use thunderstore.io</b>: the stand-in is the source and it refused the connection.</summary>
    public bool OfferThunderstore { get; init; }

    /// <summary>A message line for the non-error surfaces (Loading, nothing loaded, no source).</summary>
    public string Message { get; init; } = "";
}

/// <summary>
/// INSTALLERR1: the Install tab's mod-list states, drawn in the game's look: a failed load is a centered block (title,
/// one plain sentence by cause with the technical detail in its tooltip, the game's popup buttons); a list kept on show
/// after a failed refresh, or served from the disk cache, carries a one-line notice band with its source and age; the
/// local stand-in is always named. Retry is paced (<see cref="IndexRetryGate"/>).
/// </summary>
internal sealed partial class InstallModsPane
{
    internal const string IndexNoticeKey = "install-index-notice";
    internal const string IndexTitleKey = "install-index-title";
    internal const string IndexLineKey = "install-index-line";
    internal const string IndexButtonsKey = "install-index-buttons";
    internal const string IndexWaitKey = "install-index-wait";
    internal const string CellIndexText = "text";
    internal const string CellIndexRetry = "retry";
    internal const string CellIndexUseThunderstore = "use-thunderstore";

    public const string RetryCaption = "Retry";
    public const string UseThunderstoreCaption = "Use thunderstore.io";
    public const string DetailsTooltipTitle = "Details";

    /// <summary>The error block's top margin, the title's size and row, the sentence's column (canvas units).</summary>
    internal const float ErrorBlockTop = 96f;
    internal const float ErrorTitleSize = GameArt.GameSettingsLook.SectionTitleText;
    internal const float ErrorTitleRow = 44f;
    internal const float ErrorLineSize = GameArt.GameSettingsLook.DetailsBodyText;
    internal const float ErrorLineMaxWidth = 640f;
    internal const float ErrorButtonRow = 48f;
    internal const float ErrorButtonGap = 20f;

    /// <summary>The stand-in's <c>host:port</c> while it is the source (tests set it; default <see cref="IndexProblem.DevSourceNow"/>).</summary>
    internal Func<string?>? DevSourceProbe { get; set; }

    /// <summary>The effective index URL now (tests set it; default <see cref="IndexProblem.SourceUrlNow"/>).</summary>
    internal Func<string>? SourceUrlProbe { get; set; }

    /// <summary>The Mods menu's <b>Use thunderstore.io</b>: clears the dev URL and rebuilds the catalog.</summary>
    internal Action? UseThunderstorePress { get; set; }

    private IndexProblem? _problem;
    private DateTime? _indexStampUtc;
    private bool _indexFromCache;
    private string? _indexSource;
    private readonly IndexRetryGate _retryGate = new();
    private string _retryWait = "";
    private Action? _refreshIndex;
    private IUiCellRow? _indexNotice;
    private readonly List<IUiHandle> _indexBlock = new();

    /// <summary>The failure the last load reported (null after a success).</summary>
    internal IndexProblem? IndexProblemShown => _problem;

    /// <summary>The source URL of the list on show (null: not known).</summary>
    internal string? IndexSourceShown => _indexSource;

    internal IndexRetryGate RetryGate => _retryGate;

    /// <summary>The notice band the last build drew (null: none).</summary>
    internal IUiCellRow? IndexNoticeRow => _indexNotice;

    /// <summary>The centered block's rows the last build drew (title, sentence, buttons, wait line; or the one message).</summary>
    internal IReadOnlyList<IUiHandle> IndexBlockRows => _indexBlock;

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

    private string SourceUrl()
    {
        try { return (SourceUrlProbe ?? IndexProblem.SourceUrlNow)() ?? ""; }
        catch { return ""; }
    }

    /// <summary>
    /// The list on show belongs to another source than the current one (the dev URL was set or cleared since): it must
    /// not be shown. The Mods menu rebuilds the catalog for the new source.
    /// </summary>
    internal bool ShownFromOtherSource()
    {
        if (_indexSource == null) return false;
        string now = SourceUrl();
        return now.Length > 0 && !string.Equals(now.Trim(), _indexSource.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Remember what the result says about the list on show (called from <see cref="NoteLoadResult(CatalogLoadResult)"/>).</summary>
    private void NoteIndexState(CatalogLoadResult result, string? sourceUrl, bool fromCache, bool keptCached)
    {
        if (result.Ok)
        {
            _problem = null;
            _retryWait = "";
            _retryGate.NoteSuccess();
            _indexFromCache = fromCache;
            if (IndexProblem.TryParseStamp(result.IndexStamp, out var stamp)) _indexStampUtc = stamp;
            if (!string.IsNullOrWhiteSpace(sourceUrl)) _indexSource = sourceUrl!.Trim();
            return;
        }

        // A cancellation (a tab switch, a rebuilt catalog) is not a failure the player needs to see.
        if (string.Equals(result.Error, InstallReasons.FetchCancelled, StringComparison.Ordinal) && keptCached) return;
        _problem = IndexProblem.From(result.Error ?? InstallModsCopy.IndexNotLoaded, DevSource());
        _retryGate.NoteFailure(NowUtc());
        try
        {
            Plugin.Logger?.LogInfo("Install: the mod list did not load (" + _problem.CauseWord + "): " + _problem.Detail
                + (keptCached ? "; the last list stays on show." : "."));
        }
        catch { /* */ }
    }

    /// <summary>What to draw for the mod list now. Game-free; <paramref name="nowUtc"/> words the list's age.</summary>
    internal InstallIndexView IndexView(bool thunderstoreEnabled, bool nexusEnabled, DateTime nowUtc, TimeZoneInfo? zone = null)
    {
        var surface = Surface(thunderstoreEnabled, nexusEnabled);
        string? dev = DevSource();
        bool refused = _problem?.Cause == IndexProblemCause.Refused;
        bool offer = dev != null && refused;
        if (surface == InstallPaneSurface.Error)
        {
            var problem = _problem ?? IndexProblem.From(_lastError, dev);
            return new InstallIndexView
            {
                Surface = surface,
                Problem = problem,
                DevSource = dev,
                OfferThunderstore = dev != null && problem.Cause == IndexProblemCause.Refused,
                Notice = dev != null ? IndexProblem.DevNotice(dev) : "",
                NoticeTone = CellTone.Pending,
            };
        }

        if (surface != InstallPaneSurface.Ready)
        {
            return new InstallIndexView
            {
                Surface = surface,
                DevSource = dev,
                Message = SurfaceMessage(surface, _lastError),
                Notice = dev != null ? IndexProblem.DevNotice(dev) : "",
                NoticeTone = CellTone.Pending,
            };
        }

        string when = _indexStampUtc is { } at ? IndexProblem.When(at, nowUtc, zone) : "";
        var parts = new List<string>();
        if (dev != null) parts.Add(IndexProblem.DevNotice(dev));
        CellTone tone = CellTone.Pending;
        bool retry = false;
        if (_problem != null)
        {
            parts.Add(when.Length > 0 ? IndexProblem.StaleNotice(when) : IndexProblem.StaleNoticeNoTime);
            tone = CellTone.Orange;
            retry = true;
        }
        else if (_indexFromCache && when.Length > 0)
        {
            parts.Add(IndexProblem.LastListFrom(when));
        }

        return new InstallIndexView
        {
            Surface = surface,
            Problem = _problem,
            DevSource = dev,
            OfferThunderstore = offer,
            Notice = string.Join(SideCells.Dot, parts),
            NoticeTone = tone,
            NoticeRetry = retry,
        };
    }

    /// <summary>The notice band's cells: the sentence, then Retry and Use thunderstore.io as the game's key caps.</summary>
    internal static List<CellSpec> IndexNoticeCells(InstallIndexView view)
    {
        var cells = new List<CellSpec>
        {
            new CellSpec(CellKind.Text, CellIndexText).With(text: view.Notice, width: CellWidth.Fill(160f), tone: view.NoticeTone,
                fontSize: GameArt.GameSettingsLook.ActionRowText),
            new CellSpec(CellKind.Spacer, CellGap),
        };
        if (view.NoticeRetry)
            cells.Add(new CellSpec(CellKind.Button, CellIndexRetry).With(text: RetryCaption, align: CellAlign.Right, skin: CellSkin.KeyCap));
        if (view.OfferThunderstore && view.Surface == InstallPaneSurface.Ready)
            cells.Add(new CellSpec(CellKind.Button, CellIndexUseThunderstore).With(text: UseThunderstoreCaption, align: CellAlign.Right,
                skin: CellSkin.KeyCap));
        return cells;
    }

    /// <summary>The error block's centered title row.</summary>
    internal static CellRowSpec ErrorTitleRowSpec(string title) => new(
        IndexTitleKey,
        new List<CellSpec>
        {
            new CellSpec(CellKind.Text, CellIndexText).With(text: title, width: CellWidth.Fill(200f), align: CellAlign.Center,
                fontSize: ErrorTitleSize, tone: CellTone.Normal),
        },
        height: ErrorTitleRow, rightInset: HeadBlockLayout.RightColumnInset, plain: true, gapAbove: ErrorBlockTop);

    /// <summary>The sentence's column: <see cref="ErrorLineMaxWidth"/>, never wider than the row.</summary>
    internal static float ErrorLineWidth(float rowWidth) =>
        Math.Max(200f, Math.Min(ErrorLineMaxWidth, rowWidth - HeadBlockLayout.RightColumnInset - 2f * ErrorButtonGap));

    /// <summary>The error block's centered sentence (muted, wrapping in its column, as tall as its lines).</summary>
    internal static CellRowSpec ErrorLineRowSpec(string key, string line, float rowWidth, CellTone tone = CellTone.Muted,
        float fontSize = ErrorLineSize)
    {
        float width = ErrorLineWidth(rowWidth);
        int lines = Math.Max(1, MenuPanelLayout.WrappedLines(line, width, fontSize, 4));
        return new CellRowSpec(
            key,
            new List<CellSpec>
            {
                new CellSpec(CellKind.Spacer, "lead"),
                new CellSpec(CellKind.Text, CellIndexText).With(text: line, width: CellWidth.Px(width), align: CellAlign.Center,
                    wrap: CellWrap.Wrap, fontSize: fontSize, tone: tone),
                new CellSpec(CellKind.Spacer, "trail"),
            },
            height: lines * fontSize * 1.3f + 8f, rightInset: HeadBlockLayout.RightColumnInset, plain: true);
    }

    /// <summary>The error block's buttons: the game's popup buttons, one width, centered; Retry (the main action) first.</summary>
    internal static CellRowSpec ErrorButtonsRowSpec(bool offerThunderstore)
    {
        var captions = offerThunderstore ? new[] { RetryCaption, UseThunderstoreCaption } : new[] { RetryCaption };
        float width = DialogLayout.ButtonWidth(captions);
        var cells = new List<CellSpec> { new CellSpec(CellKind.Spacer, "lead") };
        var retry = new CellSpec(CellKind.Button, CellIndexRetry).With(text: RetryCaption, width: CellWidth.Px(width),
            fontSize: DialogLayout.ButtonFontSize, skin: CellSkin.PopupButton, tone: CellTone.Gold);
        retry.HeightOverride = GameArt.GameSettingsLook.PopupButtonHeight;
        cells.Add(retry);
        if (offerThunderstore)
        {
            var use = new CellSpec(CellKind.Button, CellIndexUseThunderstore).With(text: UseThunderstoreCaption, width: CellWidth.Px(width),
                fontSize: DialogLayout.ButtonFontSize, skin: CellSkin.PopupButton, tone: CellTone.Normal);
            use.HeightOverride = GameArt.GameSettingsLook.PopupButtonHeight;
            cells.Add(use);
        }

        cells.Add(new CellSpec(CellKind.Spacer, "trail"));
        return new CellRowSpec(IndexButtonsKey, cells, height: ErrorButtonRow, rightInset: HeadBlockLayout.RightColumnInset, plain: true,
            gapAbove: ErrorButtonGap);
    }

    /// <summary>The notice band (Ready, and over the non-list surfaces while the stand-in is the source).</summary>
    private void BuildIndexNotice(IUiPanel panel, InstallIndexView view)
    {
        _indexNotice = null;
        if (view.Notice.Length == 0) return;
        var band = panel.CellRow(new CellRowSpec(IndexNoticeKey, IndexNoticeCells(view),
            height: ModSettingsRowGrid.ActionRowHeight, rightInset: HeadBlockLayout.RightColumnInset, band: true));
        if (view.NoticeRetry) band.OnClick(CellIndexRetry, () => PressIndexRetry());
        if (view.OfferThunderstore) band.OnClick(CellIndexUseThunderstore, () => PressUseThunderstore());
        if (view.Problem != null && view.Surface == InstallPaneSurface.Ready)
            band.Tooltip(view.Problem.Line + "\n\n" + view.Problem.Detail, view.Problem.Title);
        _indexNotice = band;
    }

    /// <summary>A surface other than the list: the notice, then the centered block (an error's title, sentence and buttons).</summary>
    private void BuildIndexSurface(IUiPanel panel, InstallIndexView view)
    {
        _indexBlock.Clear();
        BuildIndexNotice(panel, view);
        float rowWidth = ColumnRowWidth();
        if (view.Surface != InstallPaneSurface.Error || view.Problem == null)
        {
            var spec = ErrorLineRowSpec(IndexLineKey, view.Message, rowWidth, CellTone.Muted);
            var only = panel.CellRow(new CellRowSpec(spec.Key, spec.Cells, height: spec.Height, rightInset: spec.RightInset, plain: true,
                gapAbove: ErrorBlockTop));
            _indexBlock.Add(only);
            return;
        }

        var problem = view.Problem;
        _indexBlock.Add(panel.CellRow(ErrorTitleRowSpec(problem.Title)));
        var line = panel.CellRow(ErrorLineRowSpec(IndexLineKey, problem.Line, rowWidth));
        // The technical detail is for the log and this tooltip only, never the headline.
        if (problem.Detail.Length > 0) line.Tooltip(problem.Detail, DetailsTooltipTitle);
        _indexBlock.Add(line);
        var buttons = panel.CellRow(ErrorButtonsRowSpec(view.OfferThunderstore));
        buttons.OnClick(CellIndexRetry, () => PressIndexRetry());
        if (view.OfferThunderstore) buttons.OnClick(CellIndexUseThunderstore, () => PressUseThunderstore());
        _indexBlock.Add(buttons);
        if (_retryWait.Length > 0)
            _indexBlock.Add(panel.CellRow(ErrorLineRowSpec(IndexWaitKey, _retryWait, rowWidth, CellTone.Muted, 16f)));
    }

    /// <summary>
    /// Retry (the error block's and the notice's; bridge <c>widget_click label=install-index-buttons/retry</c>). Paced by
    /// <see cref="IndexRetryGate"/>: a press while it waits says how long instead of sending another request. Returns
    /// <c>sent</c>, <c>wait</c> or <c>unavailable</c>.
    /// </summary>
    internal string PressIndexRetry()
    {
        if (_refreshIndex == null) return "unavailable";
        if (!_retryGate.CanRetry(NowUtc(), out var wait))
        {
            _retryWait = IndexRetryGate.WaitLine(wait);
            RequestDetailRebuild();
            return "wait";
        }

        _retryWait = "";
        try { _refreshIndex(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Install: Retry threw: " + e.Message); }
        return "sent";
    }

    /// <summary><b>Use thunderstore.io</b>: hand over to the Mods menu (clears the dev URL, rebuilds the catalog).</summary>
    internal string PressUseThunderstore()
    {
        if (UseThunderstorePress == null) return "unavailable";
        try { UseThunderstorePress(); }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Install: Use thunderstore.io threw: " + e.Message);
            return "threw";
        }

        return "sent";
    }

    /// <summary>Readback on the notice and the block's rows: <c>indexState {surface, cause, title, line, detail, notice, …}</c>.</summary>
    private bool TryDescribeIndexRow(IUiHandle handle, out Dictionary<string, object?>? extra)
    {
        extra = null;
        bool ours = _indexNotice != null && ReferenceEquals(handle, _indexNotice);
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
            ["cause"] = view?.Problem?.CauseWord,
            ["title"] = view?.Problem?.Title,
            ["line"] = view?.Problem?.Line,
            ["detail"] = view?.Problem?.Detail,
            ["notice"] = view?.Notice,
            ["devSource"] = view?.DevSource,
            ["offerThunderstore"] = view?.OfferThunderstore,
            ["retryWait"] = _retryWait.Length > 0 ? _retryWait : null,
            ["failures"] = _retryGate.Failures,
            ["source"] = _indexSource,
            ["fromCache"] = _indexFromCache,
            ["stamp"] = _indexStampUtc?.ToString("u"),
        };
    }

    private InstallIndexView? _lastIndexView;
}
