using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Progenitor.Install;
using Progenitor.Ui;
using Xunit;
using UiApi = Progenitor.Ui.Ui;

namespace Progenitor.Tests;

/// <summary>
/// INSTALLERR1 (user decision 2026-10-14: "retry automatically, don't worry about the buttons"): the Install tab retries a
/// failed mod-list check by itself (2 s, 5 s, 10 s, 30 s, then every 60 s while shown; at once on open), shows a quiet
/// "Connecting to Thunderstore…" and only after several failures "Can't reach Thunderstore right now; still trying", keeps
/// a cached list with one muted age line, and leaves a refusing local test mod list for thunderstore.io for the session
/// without touching the cfg. The cause comes from the exception's type, never its localized text.
/// </summary>
public sealed class InstallIndexStateTests : IDisposable
{
    private const string Refused =
        "the download failed: No connection could be made because the target machine actively refused it. (127.0.0.1:27450)";

    private const string RealUrl = CatalogFetcher.DefaultIndexUrl;
    private const string DevUrl = "http://127.0.0.1:27450/c/v-rising/api/v1/package/";

    private readonly CellRowTests.FakeCellRuntime _runtime = new();
    private readonly List<IUiPanel> _panels = new();
    private readonly object _owner = new();
    private readonly MutableClock _clock = new(new DateTime(2026, 10, 14, 15, 0, 0, DateTimeKind.Utc));

    public InstallIndexStateTests()
    {
        UiLayoutBatch.ResetForTests();
        UiApi.ResetForTests();
        UiApi.SetRuntimeForTests(_runtime);
        InstallModsPane.ProcessFilterProbe = null;
    }

    public void Dispose()
    {
        foreach (var panel in _panels) panel.Dispose();
        UiLayoutBatch.ResetForTests();
        InstallModsPane.ProcessFilterProbe = null;
        if (ThunderstoreMirror.TestOverride != null) ThunderstoreMirror.ResetFallbackForTests();
        ThunderstoreMirror.TestOverride = null;
        UiApi.ResetForTests();
    }

    // ---- Causes: from the exception's type, never the localized text ------------------------------------------------

    [Theory]
    // Without a transport cause the OS text says nothing: an English "actively refused" is not read as refused.
    [InlineData(Refused, (int)IndexProblemCause.Offline)]
    [InlineData("the download failed: Es konnte keine Verbindung hergestellt werden, da der Zielcomputer die Verbindung verweigerte.", (int)IndexProblemCause.Offline)]
    [InlineData(InstallReasons.FetchTimeout, (int)IndexProblemCause.Timeout)]
    [InlineData("the server returned a 5xx error (HTTP 503)", (int)IndexProblemCause.ServerError)]
    [InlineData("the download was refused by the server (HTTP 403)", (int)IndexProblemCause.ServerError)]
    [InlineData(InstallReasons.FetchTooManyRetries, (int)IndexProblemCause.ServerError)]
    [InlineData(InstallReasons.MalformedJson, (int)IndexProblemCause.BadResponse)]
    [InlineData(InstallReasons.IndexTruncated, (int)IndexProblemCause.BadResponse)]
    [InlineData(InstallReasons.FetchSizeCeiling, (int)IndexProblemCause.BadResponse)]
    [InlineData("Thunderstore index checks paused after 3 failures in a row; the next automatic check is after 15:20 UTC", (int)IndexProblemCause.Paused)]
    [InlineData("something odd", (int)IndexProblemCause.Unknown)]
    [InlineData("", (int)IndexProblemCause.Unknown)]
    public void Own_error_sentences_are_classified(string error, int cause) =>
        Assert.Equal((IndexProblemCause)cause, IndexProblem.Classify(error));

    [Theory]
    [InlineData((int)FetchFailureCause.Refused, 0, false, (int)IndexProblemCause.Refused)]
    [InlineData((int)FetchFailureCause.Dns, 0, false, (int)IndexProblemCause.Offline)]
    [InlineData((int)FetchFailureCause.Network, 0, false, (int)IndexProblemCause.Offline)]
    [InlineData((int)FetchFailureCause.Tls, 0, false, (int)IndexProblemCause.Offline)]
    [InlineData((int)FetchFailureCause.Timeout, 0, false, (int)IndexProblemCause.Timeout)]
    [InlineData((int)FetchFailureCause.Stalled, 0, false, (int)IndexProblemCause.Timeout)]
    [InlineData((int)FetchFailureCause.Http, 503, false, (int)IndexProblemCause.ServerError)]
    [InlineData((int)FetchFailureCause.None, 404, false, (int)IndexProblemCause.ServerError)]
    [InlineData((int)FetchFailureCause.Truncated, 0, false, (int)IndexProblemCause.BadResponse)]
    [InlineData((int)FetchFailureCause.Refused, 0, true, (int)IndexProblemCause.Paused)]
    public void The_transport_cause_decides_before_the_text(int transport, int status, bool paused, int cause) =>
        // A localized message must change nothing.
        Assert.Equal((IndexProblemCause)cause,
            IndexProblem.Classify((FetchFailureCause)transport, status, paused, "Une erreur réseau inattendue s'est produite."));

    [Fact]
    public void Tooltip_sentences_are_plain_and_name_no_buttons()
    {
        foreach (IndexProblemCause cause in Enum.GetValues(typeof(IndexProblemCause)))
        {
            foreach (string? dev in new[] { null, "127.0.0.1:27450" })
            {
                string line = IndexProblem.LineFor(cause, dev);
                Assert.EndsWith(".", line);
                foreach (string word in new[] { "Retry", "button", "click", "HTTP", "Exception", "5xx", "socket" })
                    Assert.DoesNotContain(word, line, StringComparison.OrdinalIgnoreCase);
            }
        }

        var problem = IndexProblem.From(FetchFailureCause.Refused, 0, false, Refused, null);
        Assert.Equal(Refused, problem.Detail);
        Assert.StartsWith(problem.Line, problem.Tooltip);
        Assert.EndsWith(Refused, problem.Tooltip);
    }

    /// <summary>
    /// Review (PR #26): on a non-English Windows a refused connection's message is localized. The cause comes from the
    /// socket error code through the fetcher, the refresher and the pane.
    /// </summary>
    [Fact]
    public async Task A_localized_refused_connection_is_still_refused_end_to_end()
    {
        ThunderstoreMirror.TestOverride = ("http://127.0.0.1:27450", true);
        var refused = new System.Net.Http.HttpRequestException(
            "Es konnte keine Verbindung hergestellt werden, da der Zielcomputer die Verbindung verweigerte. (127.0.0.1:27450)",
            new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.ConnectionRefused));
        var fetcher = new CatalogFetcher(new ThrowingTransport(refused), new CatalogFetchOptions(), _clock);
        var fetched = await fetcher.FetchIndexTextAsync(DevUrl, 1 << 20, IndexValidators.None, CancellationToken.None);
        Assert.False(fetched.Ok);
        Assert.Equal(FetchFailureCause.Refused, fetched.FailureCause);

        var timedOut = new System.Net.Http.HttpRequestException("Le délai d'attente de l'opération a expiré.",
            new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.TimedOut));
        var slow = await new CatalogFetcher(new ThrowingTransport(timedOut), new CatalogFetchOptions(), _clock)
            .FetchIndexTextAsync(DevUrl, 1 << 20, IndexValidators.None, CancellationToken.None);
        Assert.Equal(FetchFailureCause.Timeout, slow.FailureCause);

        var pane = NewPane(new FakeCatalog());
        pane.SourceChangedHook = () => { };
        var refresher = new InstallIndexRefresher(null, (json, s) => new CatalogLoadResult(true, null, 0, s), ct => Task.FromResult(fetched));
        var result = await pane.BeginIndexRefresh(refresher, CancellationToken.None);
        Assert.Equal(FetchFailureCause.Refused, result.FailureCause);
        pane.NoteLoadResult(result);
        Assert.Equal(IndexProblemCause.Refused, pane.IndexProblemShown!.Cause);
        // The localized text is kept, as detail only.
        Assert.Contains("Zielcomputer", pane.IndexProblemShown.Detail);
        Assert.DoesNotContain("Zielcomputer", pane.IndexProblemShown.Line);

        // The HTTP status survives the refresher too (it was dropped there before).
        var http = new InstallIndexRefresher(null, (json, s) => new CatalogLoadResult(true, null, 0, s),
            ct => Task.FromResult(new CatalogTextFetchResult(false, InstallReasons.HttpStatus(503), null, 0, 503) { FailureCause = FetchFailureCause.Http }));
        var answered = await http.RefreshAsync(CancellationToken.None);
        Assert.Equal(503, answered.StatusCode);
        Assert.Equal(FetchFailureCause.Http, answered.FailureCause);
    }

    // ---- Automatic retry ----------------------------------------------------------------------------------------

    [Fact]
    public void The_retry_ladder_is_2_5_10_30_then_every_60_seconds()
    {
        var retry = new IndexAutoRetry();
        var seen = new List<double>();
        for (int i = 0; i < 7; i++) seen.Add(retry.NoteFailure().TotalSeconds);
        Assert.Equal(new double[] { 2, 5, 10, 30, 60, 60, 60 }, seen);
        Assert.True(retry.Loud);
        retry.NoteSuccess();
        Assert.Equal(0, retry.Failures);
        Assert.False(retry.Loud);
    }

    [Fact]
    public void A_failed_check_is_retried_by_itself_only_while_the_tab_is_shown()
    {
        var pane = NewPane(new FakeCatalog());
        var delayed = new List<(double Seconds, Action Action)>();
        pane.PostDelayedHook = (seconds, action) => delayed.Add((seconds, action));
        bool shown = true;
        pane.TabShownProbe = () => shown;
        int refreshes = 0;
        pane.Build(NewPanel(), true, false, null, () => refreshes++, () => { }, _clock.UtcNow);

        Fail(pane, FetchFailureCause.Timeout, InstallReasons.FetchTimeout);
        Assert.Single(delayed);
        Assert.Equal(2, delayed[0].Seconds);
        delayed[0].Action();
        Assert.Equal(1, refreshes);

        Fail(pane, FetchFailureCause.Timeout, InstallReasons.FetchTimeout);
        Assert.Equal(5, delayed[1].Seconds);
        // Hidden: the due retry waits for the tab to open, and the open sends it.
        shown = false;
        Assert.Equal("hidden", pane.RunAutoRetry());
        delayed[1].Action();
        Assert.Equal(1, refreshes);
        shown = true;
        Assert.Equal("sent", pane.RetryNowIfFailing());
        Assert.Equal(2, refreshes);

        // A planned retry superseded by the open does nothing when it fires.
        Fail(pane, FetchFailureCause.Timeout, InstallReasons.FetchTimeout);
        Assert.Equal(10, delayed[2].Seconds);
        Assert.Equal("sent", pane.RetryNowIfFailing());
        delayed[2].Action();
        Assert.Equal(3, refreshes);

        // A success ends it: nothing more is planned.
        pane.NoteLoadStarted();
        pane.NoteLoadResult(new CatalogLoadResult(true, null, 0, CatalogFetcher.FormatStamp(_clock.UtcNow)));
        Assert.Null(pane.NextRetryUtc);
        Assert.Equal("ok", pane.RetryNowIfFailing());
        Assert.Equal(0, pane.AutoRetry.Failures);
    }

    /// <summary>Review (PR #26): reports joined to one check count one failure; nothing is sent while a check runs.</summary>
    [Fact]
    public void A_retry_never_goes_out_while_a_check_runs_and_one_check_counts_one_failure()
    {
        var pane = NewPane(new FakeCatalog());
        int refreshes = 0;
        var gate = new TaskCompletionSource<CatalogTextFetchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var refresher = new InstallIndexRefresher(null, (json, s) => new CatalogLoadResult(true, null, 0, s), ct => gate.Task);
        pane.Build(NewPanel(), true, false, null, () => refreshes++, () => { }, _clock.UtcNow);
        Fail(pane, FetchFailureCause.Timeout, InstallReasons.FetchTimeout);
        var first = pane.BeginIndexRefresh(refresher, CancellationToken.None);
        var joined = pane.BeginIndexRefresh(refresher, CancellationToken.None);
        Assert.Same(first, joined);
        Assert.Equal("in-flight", pane.RetryNowIfFailing());
        Assert.Equal(0, refreshes);

        gate.SetResult(new CatalogTextFetchResult(false, InstallReasons.FetchTimeout, null, 0, 0) { FailureCause = FetchFailureCause.Timeout });
        pane.NoteLoadResult(first.Result);
        pane.NoteLoadResult(joined.Result);
        Assert.Equal(2, pane.AutoRetry.Failures);
    }

    // ---- What the tab says ----------------------------------------------------------------------------------------

    [Fact]
    public void With_no_list_it_reads_connecting_then_still_trying_after_several_failures_with_no_buttons()
    {
        var pane = NewPane(new FakeCatalog());
        pane.Build(NewPanel(), true, false, null, () => { }, () => { }, _clock.UtcNow);
        pane.NoteLoadStarted();
        var loading = pane.IndexView(true, false, _clock.UtcNow);
        Assert.Equal(InstallPaneSurface.Loading, loading.Surface);
        Assert.Equal("Connecting to Thunderstore\u2026", loading.Message);
        Assert.Equal("", loading.Tooltip);

        for (int i = 1; i <= IndexAutoRetry.QuietFailures; i++)
        {
            Fail(pane, FetchFailureCause.Dns, "the download failed: Der Name ist unbekannt.");
            var view = pane.IndexView(true, false, _clock.UtcNow);
            Assert.Equal(InstallPaneSurface.Error, view.Surface);
            if (i < IndexAutoRetry.QuietFailures)
            {
                Assert.Equal("Connecting to Thunderstore\u2026", view.Message);
                Assert.Equal("", view.Tooltip);
            }
            else
            {
                Assert.Equal("Can't reach Thunderstore right now; still trying", view.Message);
                Assert.Contains("internet connection", view.Tooltip);
                Assert.Contains("Der Name ist unbekannt", view.Tooltip);
            }
        }

        pane.Build(NewPanel(), true, false, null, () => { }, () => { }, _clock.UtcNow);
        Assert.Equal(new[] { InstallModsPane.IndexLineKey }, pane.IndexBlockRows.Select(r => r.Text).ToArray());
        var cells = Row(InstallModsPane.IndexLineKey).Cells();
        Assert.DoesNotContain(cells, c => c.Kind == CellKind.Button);
        var text = cells.Single(c => c.Kind == CellKind.Text);
        Assert.Equal(CellTone.Muted, text.Tone);
        Assert.Equal(CellAlign.Center, text.Align);
        Assert.Equal(InstallModsPane.StateLineSize, text.FontSize);
        Assert.DoesNotContain(_runtime.Rows, r => r.Cells().Any(c => c.Kind == CellKind.Button && c.Text.Contains("Retry")));
    }

    [Fact]
    public void A_cached_list_stays_with_one_muted_age_line_that_refreshes_silently()
    {
        var catalog = new FakeCatalog();
        catalog.Entries.Add(InstallListViewTests.Listing("o-cached", "Cached", categories: new[] { "Oakveil Update" }));
        var pane = NewPane(catalog);
        pane.SourceUrlProbe = () => RealUrl;
        var stamp = _clock.UtcNow.AddHours(-2);
        pane.NoteLoadResult(new CatalogLoadResult(true, null, 1, CatalogFetcher.FormatStamp(stamp)), RealUrl, fromCache: true);

        var cached = pane.IndexView(true, false, _clock.UtcNow);
        Assert.Equal(InstallPaneSurface.Ready, cached.Surface);
        Assert.Equal("Updated 2 hours ago", cached.StatusLine);

        // A failed refresh keeps the list and the same quiet line (the cause only in its tooltip).
        Fail(pane, FetchFailureCause.Timeout, InstallReasons.FetchTimeout, overList: true);
        var stale = pane.IndexView(true, false, _clock.UtcNow);
        Assert.Equal(InstallPaneSurface.Ready, stale.Surface);
        Assert.Equal("Updated 2 hours ago", stale.StatusLine);
        Assert.Contains("taking too long", stale.Tooltip);

        pane.Build(NewPanel(), true, false, null, () => { }, () => { }, _clock.UtcNow);
        Assert.Single(pane.Rows);
        var status = Row(InstallModsPane.IndexStatusKey).Cells().Single();
        Assert.Equal(CellTone.Muted, status.Tone);
        Assert.Equal(InstallModsPane.StatusLineSize, status.FontSize);
        Assert.DoesNotContain(_runtime.Rows, r => r.Cells().Any(c => c.Kind == CellKind.Button && c.Text.Contains("Retry")));

        // A retry succeeds: the age line reads fresh, nothing else changes.
        pane.NoteLoadStarted();
        pane.NoteLoadResult(new CatalogLoadResult(true, null, 1, CatalogFetcher.FormatStamp(_clock.UtcNow)), RealUrl, fromCache: false);
        var fresh = pane.IndexView(true, false, _clock.UtcNow);
        Assert.Equal("Updated just now", fresh.StatusLine);
        Assert.Equal("", fresh.Tooltip);
    }

    [Theory]
    [InlineData(20, "Updated just now")]
    [InlineData(60, "Updated 1 minute ago")]
    [InlineData(5 * 60, "Updated 5 minutes ago")]
    [InlineData(60 * 60, "Updated 1 hour ago")]
    [InlineData(23 * 3600, "Updated 23 hours ago")]
    [InlineData(30 * 3600, "Updated yesterday")]
    [InlineData(3 * 86400, "Updated 3 days ago")]
    public void The_age_line_reads_in_plain_words(int seconds, string expected)
    {
        var now = new DateTime(2026, 10, 14, 15, 0, 0, DateTimeKind.Utc);
        Assert.Equal(expected, IndexProblem.UpdatedAgo(now.AddSeconds(-seconds), now));
    }

    [Fact]
    public void A_refusing_local_test_list_falls_back_to_thunderstore_for_the_session_without_touching_the_cfg()
    {
        ThunderstoreMirror.TestOverride = ("http://127.0.0.1:27450", true);
        Assert.True(ThunderstoreMirror.Active);
        var pane = NewPane(new FakeCatalog());
        var delayed = new List<(double Seconds, Action Action)>();
        pane.PostDelayedHook = (seconds, action) => delayed.Add((seconds, action));
        int followed = 0;
        pane.SourceChangedHook = () => followed++;
        pane.Build(NewPanel(), true, false, null, () => { }, () => { }, _clock.UtcNow);

        // The stand-in is named while it is the source.
        pane.NoteLoadStarted();
        Assert.Equal("Using a local test mod list (127.0.0.1:27450)", pane.IndexView(true, false, _clock.UtcNow).SubLine);
        Assert.Equal("Connecting to the local test mod list\u2026", pane.IndexView(true, false, _clock.UtcNow).Message);

        // A timeout keeps it (and plans a retry); a refusal leaves it for thunderstore.io at once.
        Fail(pane, FetchFailureCause.Timeout, InstallReasons.FetchTimeout);
        Assert.True(ThunderstoreMirror.Active);
        Assert.Single(delayed);
        Fail(pane, FetchFailureCause.Refused, Refused);
        Assert.Equal(1, followed);
        Assert.Single(delayed); // the source change retries, not the ladder
        Assert.False(ThunderstoreMirror.Active);
        Assert.True(ThunderstoreMirror.SessionFallbackActive);
        Assert.Equal(RealUrl, CatalogFetchOptions.Default.IndexUrl);
        // The setting itself is unchanged: the same URL is still configured.
        Assert.Equal("http://127.0.0.1:27450", ThunderstoreMirror.TestOverride!.Value.Url);

        // The rebuilt pane says so in one muted line.
        var next = NewPane(new FakeCatalog());
        next.NoteLoadStarted();
        var view = next.IndexView(true, false, _clock.UtcNow);
        Assert.Null(view.DevSource);
        Assert.True(view.Fallback);
        Assert.Equal("Connecting to Thunderstore\u2026", view.Message);
        Assert.Equal(IndexProblem.DevFallbackNotice, view.SubLine);
        Assert.Equal("Local test mod list unavailable; using Thunderstore", IndexProblem.DevFallbackNotice);

        // Another configured address is used again.
        ThunderstoreMirror.TestOverride = ("http://127.0.0.1:27451", true);
        Assert.True(ThunderstoreMirror.Active);
        Assert.False(ThunderstoreMirror.SessionFallbackActive);
    }

    [Fact]
    public void A_list_from_another_source_is_never_shown()
    {
        var catalog = new FakeCatalog();
        catalog.Entries.Add(InstallListViewTests.Listing("o-test", "Stand-in package", categories: new[] { "Oakveil Update" }));
        var pane = NewPane(catalog);
        string current = DevUrl;
        pane.SourceUrlProbe = () => current;
        pane.NoteLoadResult(new CatalogLoadResult(true, null, 1, "2026-10-14T13:05:00Z"), DevUrl, fromCache: true);
        Assert.Equal(InstallPaneSurface.Ready, pane.Surface(true, false));
        Assert.Equal(DevUrl, pane.IndexSourceShown);

        // The dev URL is cleared: the stand-in's list is no longer drawn (it reads as connecting until the rebuild lands).
        current = RealUrl;
        Assert.True(pane.ShownFromOtherSource());
        Assert.Equal(InstallPaneSurface.Loading, pane.Surface(true, false));
        pane.Build(NewPanel(), true, false, null, () => { }, () => { }, _clock.UtcNow);
        Assert.Empty(pane.Rows);

        // A refresh over it does not count it as a list to keep.
        var failing = new InstallIndexRefresher(null, (json, s) => new CatalogLoadResult(true, null, 1, s),
            ct => Task.FromResult(new CatalogTextFetchResult(false, InstallReasons.FetchNotHttps, null, 0, 0)));
        pane.NoteLoadResult(pane.BeginIndexRefresh(failing, CancellationToken.None).Result);
        Assert.NotNull(pane.LastError);
    }

    [Fact]
    public void A_source_change_waits_for_a_running_install_or_update()
    {
        Assert.Equal(IndexSourceDecision.Same, IndexSourceFollow.Decide(RealUrl, RealUrl, false, false));
        Assert.Equal(IndexSourceDecision.Rebuild, IndexSourceFollow.Decide(RealUrl, DevUrl, false, false));
        Assert.Equal(IndexSourceDecision.Defer, IndexSourceFollow.Decide(RealUrl, DevUrl, true, false));
        Assert.Equal(IndexSourceDecision.Defer, IndexSourceFollow.Decide(RealUrl, DevUrl, false, true));
        Assert.Equal(IndexSourceDecision.Same, IndexSourceFollow.Decide(RealUrl, RealUrl, true, true));
    }

    [Fact]
    public void The_disk_cache_keeps_one_copy_per_source()
    {
        var real = new IndexDiskCache(@"C:\nowhere\BepInEx", RealUrl);
        var dev = new IndexDiskCache(@"C:\nowhere\BepInEx", DevUrl);
        Assert.NotEqual(real.IndexPath, dev.IndexPath);
        Assert.NotEqual(real.StampPath, dev.StampPath);
        Assert.EndsWith("index.json", real.IndexPath);
    }

    // ---- Helpers ------------------------------------------------------------------------------------------------

    /// <summary>One failed check (a load started here, then its failure, as the transport classified it).</summary>
    private static void Fail(InstallModsPane pane, FetchFailureCause cause, string error, bool overList = false)
    {
        if (overList)
        {
            var refresher = new InstallIndexRefresher(null, (json, s) => new CatalogLoadResult(true, null, 1, s),
                ct => Task.FromResult(new CatalogTextFetchResult(false, error, null, 0, 0) { FailureCause = cause }));
            pane.NoteLoadResult(pane.BeginIndexRefresh(refresher, CancellationToken.None).Result);
            return;
        }

        pane.NoteLoadStarted();
        pane.NoteLoadResult(new CatalogLoadResult(false, error, 0, null) { FailureCause = cause });
    }

    private sealed class ThrowingTransport : ICatalogTransport
    {
        private readonly Exception _e;
        public ThrowingTransport(Exception e) { _e = e; }
        public Task<CatalogTransportResponse> SendAsync(CatalogTransportRequest request, CancellationToken cancellationToken) =>
            Task.FromException<CatalogTransportResponse>(_e);
    }

    private InstallModsPane NewPane(FakeCatalog catalog) => new(catalog, _clock, null) { ColumnRowWidthProbe = () => 1600f };

    private IUiPanel NewPanel()
    {
        var panel = UiApi.Panel("Install mods", _owner, "example.installerr");
        _panels.Add(panel);
        return panel;
    }

    private CellRowTests.FakeCellRow Row(string key) => _runtime.Rows.Last(r => r.Text == key);

    private sealed class MutableClock : ICatalogClock
    {
        public MutableClock(DateTime utcNow) { UtcNow = utcNow; }
        public DateTime UtcNow { get; set; }
        public Task Delay(TimeSpan duration, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeCatalog : IInstallCatalog
    {
        public List<CatalogEntry> Entries { get; } = new();
        public InstallProgress Status => InstallProgress.Idle;

        public Task<CatalogLoadResult> LoadIndexAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new CatalogLoadResult(true, null, Entries.Count, "stamp"));

        public IReadOnlyList<CatalogEntry> Search(string? query) => Entries.ToArray();

        public bool TryGetEntry(string key, out CatalogEntry? entry)
        {
            entry = Entries.Find(e => string.Equals(e.Key, key, StringComparison.OrdinalIgnoreCase));
            return entry != null;
        }

        public Task<InspectCallResult> DownloadAndInspectAsync(string key, CancellationToken cancellationToken) =>
            Task.FromResult(new InspectCallResult(false, InstallReasons.FetchNoUrl, null, null));

        public PlanCallResult Plan(string key, InstallTarget target) => new(false, InstallReasons.NotInspected, null);
        public ApproveCallResult Approve(string planHash) => new(true, null, planHash, false);
        public ExecuteCallResult ExecuteToPending(string planHash) => new(true, null, "pending/" + planHash, false);

        public Task<ListingDocumentSet> LoadListingDocumentsAsync(string key, CancellationToken cancellationToken) =>
            Task.FromResult(ListingDocumentSet.Unavailable(key ?? "", InstallReasons.FetchCancelled));
    }
}
