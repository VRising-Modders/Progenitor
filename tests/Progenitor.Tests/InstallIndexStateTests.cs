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
/// INSTALLERR1: the Install tab's mod-list states. A failed load is a game-styled block (title, plain sentence by cause,
/// the technical detail only in the tooltip, centered popup buttons); a list kept after a failed refresh or served from the
/// cache carries a notice with its source and age; the local stand-in is named and, when it refuses, Use thunderstore.io is
/// offered; Retry is paced; a list from another source than the current one is never shown.
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
        ThunderstoreMirror.TestOverride = null;
        ThunderstoreMirror.ClearProbe = null;
        UiApi.ResetForTests();
    }

    // ---- Text by cause -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData(Refused, (int)IndexProblemCause.Refused)]
    [InlineData("the download failed: No such host is known. (thunderstore.io:443)", (int)IndexProblemCause.Offline)]
    [InlineData("the download failed: The SSL connection could not be established", (int)IndexProblemCause.Offline)]
    [InlineData("the download failed: Network is unreachable", (int)IndexProblemCause.Offline)]
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
    public void Errors_are_classified_by_cause(string error, int cause) =>
        Assert.Equal((IndexProblemCause)cause, IndexProblem.Classify(error));

    [Fact]
    public void Each_cause_has_a_short_title_and_one_plain_sentence_without_internal_codes()
    {
        foreach (IndexProblemCause cause in Enum.GetValues(typeof(IndexProblemCause)))
        {
            foreach (string? dev in new[] { null, "127.0.0.1:27450" })
            {
                string title = IndexProblem.TitleFor(cause);
                string line = IndexProblem.LineFor(cause, dev);
                Assert.InRange(title.Split(' ').Length, 3, 6);
                Assert.EndsWith(".", line);
                foreach (string code in new[] { "HTTP", "http-", "refused it", "Exception", "5xx", "UTC", "socket", "dns" })
                    Assert.DoesNotContain(code, line, StringComparison.OrdinalIgnoreCase);
            }
        }

        Assert.Equal("Can't reach the mod list", IndexProblem.TitleFor(IndexProblemCause.Refused));
        Assert.Equal("Can't reach the mod list", IndexProblem.TitleFor(IndexProblemCause.Timeout));
        Assert.Equal("Can't read the mod list", IndexProblem.TitleFor(IndexProblemCause.BadResponse));
        Assert.Contains("internet connection", IndexProblem.LineFor(IndexProblemCause.Offline, null));
        Assert.Contains("took too long", IndexProblem.LineFor(IndexProblemCause.Timeout, null));
        Assert.Contains("had a problem answering", IndexProblem.LineFor(IndexProblemCause.ServerError, null));
        Assert.Contains("damaged or incomplete", IndexProblem.LineFor(IndexProblemCause.BadResponse, null));
        Assert.Equal("Nothing is answering at 127.0.0.1:27450. Start the local test mod list, or switch back to thunderstore.io.",
            IndexProblem.LineFor(IndexProblemCause.Refused, "127.0.0.1:27450"));

        var problem = IndexProblem.From(Refused, null);
        Assert.Equal(Refused, problem.Detail);
        Assert.DoesNotContain("actively refused", problem.Title + problem.Line);
    }

    [Fact]
    public void The_list_age_reads_in_plain_words()
    {
        var zone = TimeZoneInfo.Utc;
        var now = new DateTime(2026, 10, 14, 15, 0, 0, DateTimeKind.Utc);
        Assert.Equal("today at 3:05 AM", IndexProblem.When(new DateTime(2026, 10, 14, 3, 5, 0, DateTimeKind.Utc), now, zone));
        Assert.Equal("yesterday at 9:12 PM", IndexProblem.When(new DateTime(2026, 10, 13, 21, 12, 0, DateTimeKind.Utc), now, zone));
        Assert.Equal("Oct 2 at 3:05 PM", IndexProblem.When(new DateTime(2026, 10, 2, 15, 5, 0, DateTimeKind.Utc), now, zone));
        Assert.Equal("Dec 30, 2025", IndexProblem.When(new DateTime(2025, 12, 30, 15, 5, 0, DateTimeKind.Utc), now, zone));
        Assert.True(IndexProblem.TryParseStamp(CatalogFetcher.FormatStamp(now), out var parsed));
        Assert.Equal(now, parsed);
        Assert.Equal("Showing the last list from today at 3:05 AM; couldn't refresh", IndexProblem.StaleNotice("today at 3:05 AM"));
    }

    // ---- Retry pacing -------------------------------------------------------------------------------------------

    [Fact]
    public void Retry_backs_off_after_each_failure_and_a_success_clears_it()
    {
        var gate = new IndexRetryGate();
        var t = new DateTime(2026, 10, 14, 15, 0, 0, DateTimeKind.Utc);
        Assert.True(gate.CanRetry(t, out _));
        gate.NoteFailure(t);
        Assert.True(gate.CanRetry(t, out _)); // the first failure leaves Retry free
        var expected = new[] { 2, 4, 8, 16, 30, 30 };
        foreach (int seconds in expected)
        {
            gate.NoteFailure(t);
            Assert.False(gate.CanRetry(t.AddSeconds(seconds - 0.5), out var wait));
            Assert.True(wait > TimeSpan.Zero);
            Assert.True(gate.CanRetry(t.AddSeconds(seconds), out _));
        }

        Assert.Equal("Try again in 4 seconds.", IndexRetryGate.WaitLine(TimeSpan.FromSeconds(3.2)));
        Assert.Equal("Try again in 1 second.", IndexRetryGate.WaitLine(TimeSpan.FromSeconds(0.3)));
        gate.NoteSuccess();
        Assert.True(gate.CanRetry(t, out _));
        Assert.Equal(0, gate.Failures);
    }

    // ---- State selection and the drawn block --------------------------------------------------------------------

    [Fact]
    public void A_failed_load_with_nothing_on_show_draws_the_error_block_with_a_centered_retry()
    {
        var pane = NewPane(new FakeCatalog());
        int refreshes = 0;
        pane.NoteLoadResult(new CatalogLoadResult(false, Refused, 0, null));

        var view = pane.IndexView(true, false, _clock.UtcNow);
        Assert.Equal(InstallPaneSurface.Error, view.Surface);
        Assert.Equal(IndexProblemCause.Refused, view.Problem!.Cause);
        Assert.Equal("", view.Notice);
        Assert.False(view.OfferThunderstore);

        pane.Build(NewPanel(), true, false, null, () => refreshes++, () => { }, _clock.UtcNow);
        var keys = pane.IndexBlockRows.Select(r => r.Text).ToArray();
        Assert.Equal(new[] { InstallModsPane.IndexTitleKey, InstallModsPane.IndexLineKey, InstallModsPane.IndexButtonsKey }, keys);
        Assert.Null(pane.IndexNoticeRow);
        var title = Row(InstallModsPane.IndexTitleKey).Cells().Single();
        Assert.Equal("Can't reach the mod list", title.Text);
        Assert.Equal(CellAlign.Center, title.Align);
        var line = Row(InstallModsPane.IndexLineKey).Cells().Single(c => c.Kind == CellKind.Text);
        Assert.DoesNotContain("actively refused", line.Text);
        Assert.Contains("internet connection", line.Text);

        // The buttons: the game's popup button, a normal width, centered between two spacers (not a full-width bar).
        var cells = Row(InstallModsPane.IndexButtonsKey).Cells();
        Assert.Equal(new[] { CellKind.Spacer, CellKind.Button, CellKind.Spacer }, cells.Select(c => c.Kind).ToArray());
        var retry = cells[1];
        Assert.Equal(CellSkin.PopupButton, retry.Skin);
        Assert.Equal(InstallModsPane.RetryCaption, retry.Text);
        var placed = MenuPanelLayout.CellColumns(1600f, new CellRowSpec(InstallModsPane.IndexButtonsKey, cells,
            rightInset: HeadBlockLayout.RightColumnInset));
        var button = placed.Single(p => p.Key == InstallModsPane.CellIndexRetry);
        Assert.InRange(button.Width, DialogLayout.MinButtonWidth, Progenitor.Ui.GameArt.GameSettingsLook.PopupButtonWidth);
        Assert.InRange(button.X, -HeadBlockLayout.RightColumnInset, 0f);

        // Retry is free after the first failure; after the second it waits (2 s) and says so instead of sending again.
        var buttonRow = (UiCellRowHandle)pane.IndexBlockRows[2];
        UiSlotTable.BootstrapDispatch(buttonRow.CellSlotId(InstallModsPane.CellIndexRetry), (byte)UiSlotKind.Click, 0f, false, null);
        Assert.Equal(1, refreshes);
        pane.NoteLoadResult(new CatalogLoadResult(false, Refused, 0, null));
        Assert.Equal("wait", pane.PressIndexRetry());
        Assert.Equal(1, refreshes);
        pane.Build(NewPanel(), true, false, null, () => refreshes++, () => { }, _clock.UtcNow);
        Assert.Contains(pane.IndexBlockRows, r => r.Text == InstallModsPane.IndexWaitKey);
        _clock.UtcNow = _clock.UtcNow.AddSeconds(5);
        Assert.Equal("sent", pane.PressIndexRetry());
        Assert.Equal(2, refreshes);
    }

    [Fact]
    public void The_local_test_list_is_named_and_offers_thunderstore_only_when_it_refuses()
    {
        ThunderstoreMirror.TestOverride = ("http://127.0.0.1:27450", true);
        bool cleared = false;
        ThunderstoreMirror.ClearProbe = () => { cleared = true; return true; };
        var pane = NewPane(new FakeCatalog());
        int used = 0;
        pane.UseThunderstorePress = () =>
        {
            used++;
            ThunderstoreMirror.ClearConfiguredUrl();
        };

        pane.NoteLoadResult(new CatalogLoadResult(false, Refused, 0, null));
        var view = pane.IndexView(true, false, _clock.UtcNow);
        Assert.Equal("127.0.0.1:27450", view.DevSource);
        Assert.Equal("Using a local test mod list (127.0.0.1:27450)", view.Notice);
        Assert.True(view.OfferThunderstore);
        Assert.StartsWith("Nothing is answering at 127.0.0.1:27450", view.Problem!.Line);

        pane.Build(NewPanel(), true, false, null, () => { }, () => { }, _clock.UtcNow);
        Assert.NotNull(pane.IndexNoticeRow);
        var buttons = Row(InstallModsPane.IndexButtonsKey).Cells().Where(c => c.Kind == CellKind.Button).ToArray();
        Assert.Equal(new[] { InstallModsPane.RetryCaption, InstallModsPane.UseThunderstoreCaption }, buttons.Select(b => b.Text).ToArray());
        Assert.Equal(buttons[0].Width.Value, buttons[1].Width.Value);
        Assert.Equal("sent", pane.PressUseThunderstore());
        Assert.Equal(1, used);
        Assert.True(cleared);

        // A timeout from the stand-in names it but does not offer the switch.
        var other = NewPane(new FakeCatalog());
        other.NoteLoadResult(new CatalogLoadResult(false, InstallReasons.FetchTimeout, 0, null));
        var timeout = other.IndexView(true, false, _clock.UtcNow);
        Assert.False(timeout.OfferThunderstore);
        Assert.Equal("Using a local test mod list (127.0.0.1:27450)", timeout.Notice);

        // Without the stand-in: no notice, no offer, even when refused.
        ThunderstoreMirror.TestOverride = ("", true);
        var real = NewPane(new FakeCatalog());
        real.NoteLoadResult(new CatalogLoadResult(false, Refused, 0, null));
        var plain = real.IndexView(true, false, _clock.UtcNow);
        Assert.False(plain.OfferThunderstore);
        Assert.Equal("", plain.Notice);
    }

    [Fact]
    public void A_failed_refresh_over_a_cached_list_keeps_it_with_a_notice_and_a_retry()
    {
        var catalog = new FakeCatalog();
        catalog.Entries.Add(InstallListViewTests.Listing("o-cached", "Cached", categories: new[] { "Oakveil Update" }));
        var pane = NewPane(catalog);
        var stamp = new DateTime(2026, 10, 14, 13, 5, 0, DateTimeKind.Utc);
        pane.SourceUrlProbe = () => RealUrl;
        pane.NoteLoadResult(new CatalogLoadResult(true, null, 1, CatalogFetcher.FormatStamp(stamp)), RealUrl, fromCache: true);

        // Served from the cache, not refreshed yet: its age, no retry.
        var cached = pane.IndexView(true, false, _clock.UtcNow, TimeZoneInfo.Utc);
        Assert.Equal(InstallPaneSurface.Ready, cached.Surface);
        Assert.Equal("Last list from today at 1:05 PM", cached.Notice);
        Assert.False(cached.NoticeRetry);

        // The refresh fails: the list stays, the notice says so in the Orange tone, with a Retry.
        var failing = new InstallIndexRefresher(null, (json, s) => new CatalogLoadResult(true, null, 1, s),
            ct => Task.FromResult(new CatalogTextFetchResult(false, InstallReasons.FetchTimeout, null, 0, 0)));
        pane.NoteLoadResult(pane.BeginIndexRefresh(failing, CancellationToken.None).Result);
        Assert.Null(pane.LastError);
        var stale = pane.IndexView(true, false, _clock.UtcNow, TimeZoneInfo.Utc);
        Assert.Equal(InstallPaneSurface.Ready, stale.Surface);
        Assert.Equal("Showing the last list from today at 1:05 PM; couldn't refresh", stale.Notice);
        Assert.Equal(CellTone.Orange, stale.NoticeTone);
        Assert.True(stale.NoticeRetry);
        Assert.Equal(IndexProblemCause.Timeout, stale.Problem!.Cause);
        var cells = InstallModsPane.IndexNoticeCells(stale);
        Assert.Contains(cells, c => c.Key == InstallModsPane.CellIndexRetry && c.Skin == CellSkin.KeyCap);

        pane.Build(NewPanel(), true, false, null, () => { }, () => { }, _clock.UtcNow);
        Assert.NotNull(pane.IndexNoticeRow);
        Assert.Single(pane.Rows);

        // A fresh fetch clears the notice.
        pane.NoteLoadResult(new CatalogLoadResult(true, null, 1, CatalogFetcher.FormatStamp(_clock.UtcNow)), RealUrl, fromCache: false);
        Assert.Equal("", pane.IndexView(true, false, _clock.UtcNow).Notice);
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

        // The dev URL is cleared: the stand-in's list is no longer drawn (it reads as loading until the rebuild lands).
        current = RealUrl;
        Assert.True(pane.ShownFromOtherSource());
        Assert.Equal(InstallPaneSurface.Loading, pane.Surface(true, false));
        pane.Build(NewPanel(), true, false, null, () => { }, () => { }, _clock.UtcNow);
        Assert.Empty(pane.Rows);

        // A refresh over it does not count it as a list to keep: its failure is the error surface, not a stale notice.
        var failing = new InstallIndexRefresher(null, (json, s) => new CatalogLoadResult(true, null, 1, s),
            ct => Task.FromResult(new CatalogTextFetchResult(false, InstallReasons.FetchNotHttps, null, 0, 0)));
        pane.NoteLoadResult(pane.BeginIndexRefresh(failing, CancellationToken.None).Result);
        Assert.NotNull(pane.LastError);
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
