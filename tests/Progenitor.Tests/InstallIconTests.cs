using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Progenitor.Install;
using Progenitor.Plugins;
using Progenitor.Ui;
using Xunit;
using UiApi = Progenitor.Ui.Ui;

namespace Progenitor.Tests;

/// <summary>
/// ICON2: listing icons must keep arriving after the first build. The defect the user saw —
/// sort by <c>Recently released</c> and nine of the first eleven rows stay blank although their
/// icons are on disk — is one hung network fetch parking <see cref="IconCache"/>'s single
/// worker: the disk read lived inside the fetch delegate
/// (<c>ListingDiskCache.WrapIconFetch</c>), so every disk-cached icon queued behind it, there
/// was no per-fetch timeout, and <c>_running</c> stayed true so later requests only enqueued.
///
/// Removing the disk lane fails <see cref="Disk_cached_icons_paint_while_one_network_fetch_hangs"/>;
/// removing the timeout fails <see cref="A_hung_fetch_fails_after_the_timeout_and_the_queue_continues"/>;
/// removing the worker generation fails <see cref="A_new_request_after_release_all_restarts_the_worker"/>;
/// removing the retry window fails <see cref="A_failed_url_is_asked_again_after_retry_after"/>;
/// removing the completion timeout fails <see cref="A_marshaller_that_never_runs_does_not_park_the_queue"/>.
/// </summary>
public sealed class InstallIconTests : IDisposable
{
    private static readonly byte[] Png = { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3 };

    private readonly CellRowTests.FakeCellRuntime _runtime = new();
    private readonly List<IUiPanel> _panels = new();
    private readonly object _owner = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "progenitor-icon2-" + Guid.NewGuid().ToString("N"));

    public InstallIconTests()
    {
        UiLayoutBatch.ResetForTests();
        UiApi.ResetForTests();
        UiApi.SetRuntimeForTests(_runtime);
        CatalogUpdateTags.ResetProvidersForTests();
        CatalogUpdateTags.ConfiguredTagProvider = () => null;
        CatalogUpdateTags.BuildProvider = () => InstallSearchFilterTests.KnownClientBuild;
        InstallModsPane.ProcessFilterProbe = null;
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        // INSTALLUX2 item 1: the confirmation is a modal dialog on its own panel now. Ui.ResetForTests
        // only FORGETS the open one, so without this its rows outlive the test and the next test's
        // click on prompt:<key> resolves to the dead one.
        Progenitor.Ui.UiDialog.Current?.Close();
        foreach (var panel in _panels) panel.Dispose();
        UiLayoutBatch.ResetForTests();
        CatalogUpdateTags.ResetProvidersForTests();
        InstallModsPane.ProcessFilterProbe = null;
        UiApi.ResetForTests();
        try { Directory.Delete(_root, true); } catch { }
    }

    // ---- The reproduction ---------------------------------------------------------------

    /// <summary>
    /// The user's flow: the list is built under <c>Downloads</c>, the first row's icon is not on
    /// disk and its network fetch never answers, then the sort changes to <c>Recently released</c>
    /// and the pane is rebuilt. Every newly visible row's icon is on disk, so every one of them
    /// must be requested and painted while that one fetch is still hanging.
    ///
    /// On the code before ICON2 this failed at <c>IconCache.RunAsync</c>: the hung
    /// <c>await _fetch</c> (the delegate that also did the disk read) held the only worker, so the
    /// fifteen queued disk hits — and every URL the sort made visible — never painted.
    /// </summary>
    [Fact]
    public void Disk_cached_icons_paint_while_one_network_fetch_hangs()
    {
        var catalog = new IconCatalog();
        for (int i = 0; i < 30; i++)
            catalog.Entries.Add(InstallListViewTests.Listing(
                "o-p" + i, "Pack " + i,
                categories: new[] { "Oakveil Update" },
                downloads: 1000 - i,
                releasedUtc: "2026-08-" + (i + 1).ToString("00") + "T00:00:00Z",
                version: "1.0." + i));

        var disk = new ListingDiskCache(_root);
        // Every icon but row 0's is already on disk (the live cache held 79 of them).
        for (int i = 1; i < 30; i++)
            Assert.True(disk.WriteIcon(InstallModsPane.IconUrlFor(catalog.Entries[i]), Png));

        var decoder = new FakeDecoder();
        var hang = new HangingFetch();
        var icons = new IconCache(disk.WrapIconFetch(hang.FetchAsync), decoder, new ImmediateClock());
        var pane = new InstallModsPane(catalog, new FrozenClock(), null);
        pane.SetDiskCache(disk);
        pane.SetIconCache(icons);
        pane.SetViewportRows(6);

        // POLISH2 item 1: the pane now opens on Recently released, which puts the one entry with no
        // disk-cached icon (o-p0, the oldest) off the first viewport — so this case asks for
        // Downloads explicitly. The icon lanes are what is under test here, not the default.
        Assert.Equal(InstallSort.Released, pane.Sort);
        pane.SetSort(InstallSort.Downloads);
        BuildPane(pane);
        Assert.Equal(InstallSort.Downloads, pane.Sort);
        // Rows 1..15 are disk hits and paint without a single network fetch; row 0 misses disk
        // and takes the network lane, where it hangs.
        for (int i = 1; i < 16; i++) SettleIcon("o-p" + i, decoder, catalog.Entries[i]);
        Settle(() => hang.Started == 1);
        Assert.Equal(1, hang.Started);
        Assert.Equal(IconState.Loading, icons.StateOf(InstallModsPane.IconUrlFor(catalog.Entries[0])));
        Assert.False(Row("o-p0").Images.ContainsKey(InstallModsPane.CellIcon));

        // Recently released reverses the list: rows 29..14 become visible. The pane rebuild goes
        // through the same hook ModsMenu uses (RequestDetailRebuild → a fresh Build).
        pane.SetDetailRebuild(() => BuildPane(pane));
        pane.SetSort(InstallSort.Released);
        Assert.Equal(InstallSort.Released, pane.Sort);
        for (int i = 29; i > 13; i--) SettleIcon("o-p" + i, decoder, catalog.Entries[i]);
        // Still exactly the one network fetch, still hanging: nothing waited on it.
        Assert.Equal(1, hang.Started);
        Assert.True(icons.DiskHitCount >= 16);

        pane.CancelIconRequests();
        icons.ReleaseAll();
        hang.ReleaseAll();
    }

    // ---- (a) the per-fetch timeout -------------------------------------------------------

    /// <summary>
    /// A fetch that never answers fails its own entry after <see cref="IconCache.FetchTimeout"/>
    /// and the queue continues. Before ICON2 the worker awaited it forever.
    /// </summary>
    [Fact]
    public async Task A_hung_fetch_fails_after_the_timeout_and_the_queue_continues()
    {
        Assert.Equal(TimeSpan.FromSeconds(15), IconCache.FetchTimeout);
        var decoder = new FakeDecoder();
        var hang = new HangingFetch { AnswerAfterFirst = true };
        var timeout = new ManualTimer();
        var icons = new IconCache(hang.FetchAsync, decoder, new ImmediateClock()) { TimeoutDelay = timeout.Delay };

        var first = icons.RequestAsync("https://x/hangs.png");
        var second = icons.RequestAsync("https://x/b.png");
        Settle(() => hang.Started == 1);
        Assert.Equal(1, icons.QueuedCount);
        Assert.Equal("https://x/hangs.png", icons.InFlightUrl);

        // The 15 s timer elapses: the entry fails and the second URL is fetched.
        timeout.Elapse();
        Assert.False(await first);
        Assert.Equal(IconState.Failed, icons.StateOf("https://x/hangs.png"));
        Assert.Equal(IconCache.TimedOut, icons.ErrorOf("https://x/hangs.png"));
        Assert.True(await second);
        Assert.Equal(IconState.Ready, icons.StateOf("https://x/b.png"));
        Assert.Null(icons.InFlightUrl);
        Assert.Equal(0, icons.QueuedCount);
        icons.ReleaseAll();
        hang.ReleaseAll();
    }

    // ---- (d) a recoverable worker --------------------------------------------------------

    /// <summary>
    /// Core-reload shape: <see cref="IconCache.ReleaseAll"/> while the worker is parked in a
    /// fetch, then a new pane build asks again. The generation token retires the old worker, so
    /// the new request starts a fetch instead of only enqueuing behind <c>_running</c>.
    /// </summary>
    [Fact]
    public async Task A_new_request_after_release_all_restarts_the_worker()
    {
        var decoder = new FakeDecoder();
        var hang = new HangingFetch();
        var icons = new IconCache(hang.FetchAsync, decoder, new ImmediateClock()) { TimeoutDelay = new ManualTimer().Delay };
        var parked = icons.RequestAsync("https://x/a.png");
        Settle(() => hang.Started == 1);

        icons.ReleaseAll();
        Assert.False(await parked);
        Assert.Equal(0, icons.Count);

        hang.AnswerAfterFirst = true;
        Assert.True(await icons.RequestAsync("https://x/c.png"));
        Assert.Equal(IconState.Ready, icons.StateOf("https://x/c.png"));
        Assert.Equal(2, icons.FetchCount);
        icons.ReleaseAll();
        hang.ReleaseAll();
    }

    /// <summary>A marshaler whose action never runs times out like a fetch: the queue drains on.</summary>
    [Fact]
    public async Task A_marshaller_that_never_runs_does_not_park_the_queue()
    {
        var decoder = new FakeDecoder();
        var timeout = new ManualTimer();
        var posted = new List<Action>();
        var icons = new IconCache(
            (url, ct) => Task.FromResult(new CatalogBytesFetchResult(true, null, Png, Png.Length, 200)),
            decoder,
            new ImmediateClock(),
            action => posted.Add(action))
        { TimeoutDelay = timeout.Delay };

        var first = icons.RequestAsync("https://x/a.png");
        Settle(() => posted.Count == 1);
        timeout.Elapse();
        Assert.False(await first);
        Assert.Equal(IconCache.TimedOut, icons.ErrorOf("https://x/a.png"));

        // The queue moved on: the next URL is fetched and posted too.
        var second = icons.RequestAsync("https://x/b.png");
        Settle(() => posted.Count == 2);
        posted[1]();
        Assert.True(await second);
        icons.ReleaseAll();
    }

    /// <summary>A marshaler that throws fails that entry (it does not kill the worker).</summary>
    [Fact]
    public async Task A_post_that_throws_fails_the_entry_and_the_queue_continues()
    {
        var decoder = new FakeDecoder();
        int posts = 0;
        var icons = new IconCache(
            (url, ct) => Task.FromResult(new CatalogBytesFetchResult(true, null, Png, Png.Length, 200)),
            decoder,
            new ImmediateClock(),
            action =>
            {
                if (++posts == 1) throw new InvalidOperationException("no main thread");
                action();
            });

        Assert.False(await icons.RequestAsync("https://x/a.png"));
        Assert.Equal(IconState.Failed, icons.StateOf("https://x/a.png"));
        Assert.True(await icons.RequestAsync("https://x/b.png"));
        Assert.Equal(IconState.Ready, icons.StateOf("https://x/b.png"));
        icons.ReleaseAll();
    }

    // ---- (b) the disk lane, (c) the retry window -----------------------------------------

    /// <summary>The disk lane serves a cached URL with no network fetch at all, and marks it cached.</summary>
    [Fact]
    public async Task The_disk_lane_serves_a_cached_url_without_a_fetch()
    {
        var disk = new ListingDiskCache(_root);
        Assert.True(disk.WriteIcon("https://x/a.png", Png));
        Assert.Equal(Png, disk.ReadIconBytes("https://x/a.png"));
        Assert.Null(disk.ReadIconBytes("https://x/missing.png"));

        int network = 0;
        var icons = new IconCache(
            (url, ct) => { network++; return Task.FromResult(new CatalogBytesFetchResult(true, null, Png, Png.Length, 200)); },
            new FakeDecoder(),
            new ImmediateClock())
        { DiskProbe = disk.ReadIconBytes };

        Assert.True(await icons.RequestAsync("https://x/a.png"));
        Assert.Equal(0, network);
        Assert.Equal(0, icons.FetchCount);
        Assert.Equal(1, icons.DiskHitCount);
        Assert.True(icons.WasDiskHit("https://x/a.png"));

        // A disk miss falls through to the network lane.
        Assert.True(await icons.RequestAsync("https://x/missing.png"));
        Assert.Equal(1, network);
        Assert.False(icons.WasDiskHit("https://x/missing.png"));
        icons.ReleaseAll();
    }

    /// <summary>
    /// A failed URL is not gated out of the pane for good: <c>RequestVisibleIcons</c> asks again
    /// once <see cref="IconCache.RetryAfter"/> has passed. Before ICON2 <c>_iconRequested</c> was
    /// a set, so a failure was never retried within the build.
    /// </summary>
    [Fact]
    public void A_failed_url_is_asked_again_after_retry_after()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), IconCache.RetryAfter);
        var catalog = new IconCatalog();
        catalog.Entries.Add(InstallListViewTests.Listing("o-p0", "Pack 0", categories: new[] { "Oakveil Update" }));
        var decoder = new FakeDecoder();
        var fetch = new CountingFetch { Fail = true };
        var icons = new IconCache(fetch.FetchAsync, decoder, new ImmediateClock());
        var clock = new FrozenClock();
        var pane = new InstallModsPane(catalog, clock, null);
        pane.SetIconCache(icons);
        pane.SetViewportRows(6);
        string url = InstallModsPane.IconUrlFor(catalog.Entries[0])!;

        BuildPane(pane);
        Settle(() => icons.StateOf(url) == IconState.Failed);
        Assert.Equal(1, fetch.Calls);

        // Same second: no second ask.
        pane.RequestVisibleIcons();
        Assert.Equal(1, fetch.Calls);

        // Past the retry window it is asked again, and now it succeeds and paints.
        clock.Now = clock.Now.Add(IconCache.RetryAfter + TimeSpan.FromSeconds(1));
        fetch.Fail = false;
        pane.RequestVisibleIcons();
        Settle(() => fetch.Calls == 2 && icons.StateOf(url) == IconState.Ready);
        Settle(() => Row("o-p0").Images.ContainsKey(InstallModsPane.CellIcon));
        icons.ReleaseAll();
    }

    // ---- (3) readback --------------------------------------------------------------------

    /// <summary>
    /// <c>listing.icon {state, cached, error}</c> per row and <c>toolbar.icons {ready, queued,
    /// loading, failed, inFlightUrl, lastError, fetches, stalledSeconds}</c>.
    /// </summary>
    [Fact]
    public void Readback_reports_every_row_icon_state_and_the_toolbar_totals()
    {
        var catalog = new IconCatalog();
        for (int i = 0; i < 3; i++)
            catalog.Entries.Add(InstallListViewTests.Listing("o-p" + i, "Pack " + i, categories: new[] { "Oakveil Update" }, downloads: 100 - i));
        var disk = new ListingDiskCache(_root);
        Assert.True(disk.WriteIcon(InstallModsPane.IconUrlFor(catalog.Entries[0]), Png));
        var decoder = new FakeDecoder();
        var hang = new HangingFetch();
        var icons = new IconCache(hang.FetchAsync, decoder, new ImmediateClock()) { TimeoutDelay = new ManualTimer().Delay };
        var pane = new InstallModsPane(catalog, new FrozenClock(), null);
        pane.SetDiskCache(disk);
        pane.SetIconCache(icons);
        pane.SetViewportRows(6);
        BuildPane(pane);

        string hung = InstallModsPane.IconUrlFor(catalog.Entries[1])!;
        Settle(() => icons.StateOf(InstallModsPane.IconUrlFor(catalog.Entries[0])) == IconState.Ready);
        Settle(() => hang.Started == 1);

        var ready = (Dictionary<string, object?>)pane.ListingToWidget(catalog.Entries[0])["icon"]!;
        Assert.Equal("ready", ready["state"]);
        Assert.Equal(true, ready["cached"]);
        Assert.Null(ready["error"]);

        var loading = (Dictionary<string, object?>)pane.ListingToWidget(catalog.Entries[1])["icon"]!;
        Assert.Equal("loading", loading["state"]);
        Assert.Equal(false, loading["cached"]);

        var totals = pane.IconsToWidget();
        Assert.Equal(1, totals["ready"]);
        Assert.Equal(1, totals["loading"]);
        Assert.Equal(hung, totals["inFlightUrl"]);
        Assert.Equal(1, totals["fetches"]);
        Assert.Equal(0d, totals["stalledSeconds"]);

        // A listing with no icon URL reads none; an unwired pane reads missing.
        var bare = new InstallModsPane(new IconCatalog());
        Assert.Equal("missing", ((Dictionary<string, object?>)bare.ListingToWidget(catalog.Entries[0])["icon"]!)["state"]);

        pane.CancelIconRequests();
        icons.ReleaseAll();
        hang.ReleaseAll();
    }

    // ---- Helpers -------------------------------------------------------------------------

    private void BuildPane(InstallModsPane pane) =>
        pane.Build(NewPanel(), true, false, null, () => { }, () => { }, DateTime.UtcNow);

    private IUiPanel NewPanel()
    {
        var panel = UiApi.Panel("Install mods", _owner, "example.icon2");
        _panels.Add(panel);
        return panel;
    }

    /// <summary>The most recently built row for a listing key (a rebuild adds a second row).</summary>
    private CellRowTests.FakeCellRow Row(string key) =>
        _runtime.Rows.Last(r => r.Text == key);

    private void SettleIcon(string key, FakeDecoder decoder, CatalogEntry entry)
    {
        string url = InstallModsPane.IconUrlFor(entry)!;
        Settle(() => Row(key).Images.ContainsKey(InstallModsPane.CellIcon));
        Assert.Same(decoder.Handles[url], Row(key).Images[InstallModsPane.CellIcon]);
    }

    private static void Settle(Func<bool> condition) =>
        Assert.True(SpinWait.SpinUntil(condition, 5000), "the step did not settle");

    private sealed class ImmediateClock : ICatalogClock
    {
        public DateTime UtcNow => new(2026, 9, 12, 9, 0, 0, DateTimeKind.Utc);
        public Task Delay(TimeSpan duration, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>A pane clock the test moves by hand; its delay never elapses, so the README run parks.</summary>
    private sealed class FrozenClock : ICatalogClock
    {
        public DateTime Now = new(2026, 9, 12, 9, 0, 0, DateTimeKind.Utc);
        public DateTime UtcNow => Now;

        public Task Delay(TimeSpan duration, CancellationToken cancellationToken)
        {
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
            return tcs.Task;
        }
    }

    /// <summary>The 15 s timeout under the test's control: <see cref="Elapse"/> fires every armed timer.</summary>
    private sealed class ManualTimer
    {
        private readonly object _lock = new();
        private readonly List<TaskCompletionSource<bool>> _armed = new();

        public Task Delay(TimeSpan duration, CancellationToken token)
        {
            Assert.Equal(IconCache.FetchTimeout, duration);
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_lock) _armed.Add(tcs);
            token.Register(() => tcs.TrySetCanceled(token));
            return tcs.Task;
        }

        public void Elapse()
        {
            List<TaskCompletionSource<bool>> armed;
            lock (_lock)
            {
                armed = new List<TaskCompletionSource<bool>>(_armed);
                _armed.Clear();
            }
            Assert.NotEmpty(armed);
            foreach (var tcs in armed) tcs.TrySetResult(true);
        }
    }

    /// <summary>A network fetch that never answers (the live stall). Optionally answers every fetch after the first.</summary>
    private sealed class HangingFetch
    {
        private readonly object _lock = new();
        private readonly List<TaskCompletionSource<CatalogBytesFetchResult>> _held = new();
        private int _started;

        public bool AnswerAfterFirst;
        public int Started => Volatile.Read(ref _started);

        public Task<CatalogBytesFetchResult> FetchAsync(string url, CancellationToken token)
        {
            int n = Interlocked.Increment(ref _started);
            if (AnswerAfterFirst && n > 1)
                return Task.FromResult(new CatalogBytesFetchResult(true, null, Png, Png.Length, 200));
            var tcs = new TaskCompletionSource<CatalogBytesFetchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_lock) _held.Add(tcs);
            token.Register(() => tcs.TrySetCanceled(token));
            return tcs.Task;
        }

        public void ReleaseAll()
        {
            List<TaskCompletionSource<CatalogBytesFetchResult>> held;
            lock (_lock)
            {
                held = new List<TaskCompletionSource<CatalogBytesFetchResult>>(_held);
                _held.Clear();
            }
            foreach (var tcs in held) tcs.TrySetResult(new CatalogBytesFetchResult(false, "dropped", null, 0, 0));
        }
    }

    private sealed class CountingFetch
    {
        private int _calls;
        public bool Fail;
        public int Calls => Volatile.Read(ref _calls);

        public Task<CatalogBytesFetchResult> FetchAsync(string url, CancellationToken token)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(Fail
                ? new CatalogBytesFetchResult(false, "http 503", null, 0, 503)
                : new CatalogBytesFetchResult(true, null, Png, Png.Length, 200));
        }
    }

    private sealed class FakeDecoder : IIconDecoder
    {
        public readonly Dictionary<string, object> Handles = new(StringComparer.Ordinal);
        public object? Placeholder { get; } = new object();

        public object? Decode(byte[] bytes, string url)
        {
            var handle = new object();
            lock (Handles) Handles[url] = handle;
            return handle;
        }

        public void Destroy(object handle) { }
    }

    /// <summary>The smallest catalog the pane needs: entries, no documents, no flow.</summary>
    private sealed class IconCatalog : IInstallCatalog
    {
        public List<CatalogEntry> Entries { get; } = new();

        public InstallProgress Status => InstallProgress.Idle;

        public Task<CatalogLoadResult> LoadIndexAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new CatalogLoadResult(true, null, Entries.Count, "stamp"));

        public IReadOnlyList<CatalogEntry> Search(string? query) => Entries;

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
