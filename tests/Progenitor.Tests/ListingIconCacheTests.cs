using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Progenitor.Install;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// LIST1: the per-process listing-icon cache. One fetch in flight, 100 ms between fetches,
/// a 512 KB body cap, per-request and whole-cache cancellation, LRU eviction that destroys
/// the evicted handle, a placeholder while loading, and decode through the marshaler.
/// Removing the spacing wait fails <see cref="Fetches_are_spaced_by_at_least_100_ms"/>;
/// removing the LRU trim fails <see cref="Cache_evicts_least_recently_used_past_the_cap_and_destroys_it"/>;
/// removing the queue drain on cancel fails <see cref="Cancelled_request_never_fetches"/>.
/// </summary>
public sealed class ListingIconCacheTests
{
    private static readonly byte[] Png = { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3 };

    [Fact]
    public async Task Ready_icons_answer_true_and_others_get_the_placeholder()
    {
        var fetch = new FakeFetch();
        var decoder = new FakeDecoder();
        var cache = new IconCache(fetch.FetchAsync, decoder, new ImmediateClock());
        Assert.False(cache.TryGetSprite("https://x/a.png", out var sprite));
        Assert.Same(decoder.Placeholder, sprite);
        Assert.Equal(IconState.Missing, cache.StateOf("https://x/a.png"));

        fetch.Respond("https://x/a.png", Png);
        Assert.True(await cache.RequestAsync("https://x/a.png"));
        Assert.Equal(IconState.Ready, cache.StateOf("https://x/a.png"));
        Assert.True(cache.TryGetSprite("https://x/a.png", out sprite));
        Assert.Same(decoder.Handles["https://x/a.png"], sprite);
        Assert.Equal(1, cache.Count);
        Assert.Equal(1, cache.FetchCount);
        // A second request for a ready URL is answered without a fetch.
        Assert.True(await cache.RequestAsync("https://x/a.png"));
        Assert.Equal(1, cache.FetchCount);
    }

    [Fact]
    public async Task Only_one_fetch_is_in_flight()
    {
        var fetch = new FakeFetch { Hold = true };
        var decoder = new FakeDecoder();
        var cache = new IconCache(fetch.FetchAsync, decoder, new ImmediateClock());
        for (int i = 0; i < 5; i++) fetch.Respond("https://x/" + i, Png);
        var tasks = new List<Task<bool>>();
        for (int i = 0; i < 5; i++) tasks.Add(cache.RequestAsync("https://x/" + i));
        await fetch.WaitForStart(1);
        Assert.Equal(1, fetch.Started);
        Assert.Equal(1, fetch.MaxConcurrent);
        Assert.True(cache.InFlight);
        Assert.Equal(4, cache.QueuedCount);
        fetch.ReleaseAll();
        Assert.All(await Task.WhenAll(tasks), ok => Assert.True(ok));
        Assert.Equal(5, fetch.Started);
        Assert.Equal(1, fetch.MaxConcurrent);
        Assert.False(cache.InFlight);
        Assert.Equal(5, cache.Count);
    }

    [Fact]
    public async Task Fetches_are_spaced_by_at_least_100_ms()
    {
        var fetch = new FakeFetch();
        var clock = new SteppingClock();
        var cache = new IconCache(fetch.FetchAsync, new FakeDecoder(), clock);
        fetch.Respond("https://x/a", Png);
        fetch.Respond("https://x/b", Png);
        Assert.True(await cache.RequestAsync("https://x/a"));
        Assert.True(await cache.RequestAsync("https://x/b"));
        // The first fetch waited nothing; the second waited the remainder of MinSpacing.
        Assert.Single(clock.Delays);
        Assert.InRange(clock.Delays[0], IconCache.MinSpacing - SteppingClock.Step, IconCache.MinSpacing);
        Assert.Equal(TimeSpan.FromMilliseconds(100), IconCache.MinSpacing);
    }

    [Fact]
    public async Task Body_over_the_cap_and_a_failed_decode_are_failed_not_cached()
    {
        var fetch = new FakeFetch();
        var decoder = new FakeDecoder();
        var cache = new IconCache(fetch.FetchAsync, decoder, new ImmediateClock());
        fetch.Respond("https://x/big", new byte[IconCache.MaxBytes + 1]);
        Assert.False(await cache.RequestAsync("https://x/big"));
        Assert.Equal(IconState.Failed, cache.StateOf("https://x/big"));
        Assert.Equal(InstallReasons.FetchSizeCeiling, cache.ErrorOf("https://x/big"));
        Assert.Equal(512L * 1024, IconCache.MaxBytes);

        fetch.Respond("https://x/junk", new byte[] { 1, 2 });
        decoder.RefuseNext = true;
        Assert.False(await cache.RequestAsync("https://x/junk"));
        Assert.Equal(IconState.Failed, cache.StateOf("https://x/junk"));
        Assert.Equal("decode failed", cache.ErrorOf("https://x/junk"));

        fetch.Fail("https://x/404", 404);
        Assert.False(await cache.RequestAsync("https://x/404"));
        Assert.Equal(InstallReasons.HttpStatus(404), cache.ErrorOf("https://x/404"));
        Assert.Equal(0, cache.Count);
        Assert.False(cache.TryGetSprite("https://x/404", out var sprite));
        Assert.Same(decoder.Placeholder, sprite);
        // A failed URL can be asked for again.
        fetch.Respond("https://x/404", Png);
        Assert.True(await cache.RequestAsync("https://x/404"));
    }

    [Fact]
    public async Task Cancelled_request_never_fetches()
    {
        var fetch = new FakeFetch { Hold = true };
        var cache = new IconCache(fetch.FetchAsync, new FakeDecoder(), new ImmediateClock());
        fetch.Respond("https://x/a", Png);
        fetch.Respond("https://x/b", Png);
        var a = cache.RequestAsync("https://x/a");
        using var cts = new CancellationTokenSource();
        var b = cache.RequestAsync("https://x/b", cts.Token);
        await fetch.WaitForStart(1);
        cts.Cancel();
        Assert.False(await b);
        Assert.Equal(IconState.Failed, cache.StateOf("https://x/b"));
        Assert.Equal(InstallReasons.FetchCancelled, cache.ErrorOf("https://x/b"));
        fetch.ReleaseAll();
        Assert.True(await a);
        Assert.Equal(1, fetch.Started);

        // Canceling the one in flight cancels its token and answers false.
        fetch.Hold = true;
        fetch.Respond("https://x/c", Png);
        var c = cache.RequestAsync("https://x/c");
        await fetch.WaitForStart(2);
        cache.Cancel("https://x/c");
        fetch.ReleaseAll();
        Assert.False(await c);
        Assert.True(fetch.LastTokenCancelled);
        Assert.Equal(IconState.Failed, cache.StateOf("https://x/c"));

        // CancelAll drops the queue and keeps ready entries.
        fetch.Hold = true;
        fetch.Respond("https://x/d", Png);
        fetch.Respond("https://x/e", Png);
        var d = cache.RequestAsync("https://x/d");
        var e = cache.RequestAsync("https://x/e");
        await fetch.WaitForStart(3);
        cache.CancelAll();
        fetch.ReleaseAll();
        Assert.False(await d);
        Assert.False(await e);
        Assert.Equal(IconState.Ready, cache.StateOf("https://x/a"));
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public async Task Cache_evicts_least_recently_used_past_the_cap_and_destroys_it()
    {
        var fetch = new FakeFetch();
        var decoder = new FakeDecoder();
        var cache = new IconCache(fetch.FetchAsync, decoder, new ImmediateClock(), capacity: 3);
        Assert.Equal(256, IconCache.DefaultCapacity);
        for (int i = 0; i < 3; i++)
        {
            fetch.Respond("https://x/" + i, Png);
            Assert.True(await cache.RequestAsync("https://x/" + i));
        }
        Assert.Equal(new[] { "https://x/2", "https://x/1", "https://x/0" }, cache.RecentKeys);
        // Touch 0 so 1 is the least recently used.
        Assert.True(cache.TryGetSprite("https://x/0", out _));
        Assert.Equal(new[] { "https://x/0", "https://x/2", "https://x/1" }, cache.RecentKeys);
        fetch.Respond("https://x/3", Png);
        Assert.True(await cache.RequestAsync("https://x/3"));
        Assert.Equal(3, cache.Count);
        Assert.Equal(new[] { "https://x/3", "https://x/0", "https://x/2" }, cache.RecentKeys);
        Assert.Equal(IconState.Missing, cache.StateOf("https://x/1"));
        Assert.Contains("https://x/1", decoder.Destroyed);
        Assert.DoesNotContain("https://x/0", decoder.Destroyed);
        // ReleaseAll destroys the rest.
        cache.ReleaseAll();
        Assert.Equal(0, cache.Count);
        Assert.Equal(4, decoder.Destroyed.Count);
        Assert.Equal(IconState.Missing, cache.StateOf("https://x/3"));
    }

    [Fact]
    public async Task Decode_runs_through_the_marshaller_and_Arrived_fires_there()
    {
        var fetch = new FakeFetch();
        var decoder = new FakeDecoder();
        var posted = new List<Action>();
        var cache = new IconCache(fetch.FetchAsync, decoder, new ImmediateClock(), post: posted.Add);
        var arrived = new List<string>();
        cache.Arrived += arrived.Add;
        fetch.Respond("https://x/a", Png);
        var task = cache.RequestAsync("https://x/a");
        await fetch.WaitForStart(1);
        // The bytes are back but nothing is decoded until the main thread drains the post.
        await Task.Delay(20);
        Assert.False(task.IsCompleted);
        Assert.Empty(decoder.Handles);
        Assert.Equal(IconState.Loading, cache.StateOf("https://x/a"));
        Assert.False(cache.TryGetSprite("https://x/a", out var sprite));
        Assert.Same(decoder.Placeholder, sprite);
        Assert.Single(posted);
        posted[0]();
        Assert.True(await task);
        Assert.Equal(new[] { "https://x/a" }, arrived);
        Assert.Equal(IconState.Ready, cache.StateOf("https://x/a"));
    }

    [Fact]
    public async Task Over_a_fetcher_uses_the_bytes_call_with_the_cap()
    {
        var transport = new RecordingTransport();
        var fetcher = new CatalogFetcher(transport, clock: new ImmediateClock());
        var cache = IconCache.Over(fetcher, new FakeDecoder(), new ImmediateClock());
        Assert.True(await cache.RequestAsync("https://thunderstore.io/icon.png"));
        Assert.Equal("https://thunderstore.io/icon.png", transport.LastUrl);
        Assert.Equal(1, cache.Count);
        // A plain http URL is refused by the same transport rules.
        Assert.False(await cache.RequestAsync("http://thunderstore.io/icon.png"));
        Assert.Equal(InstallReasons.FetchNotHttps, cache.ErrorOf("http://thunderstore.io/icon.png"));
    }

    [Fact]
    public async Task FetchBytesAsync_returns_the_body_and_honours_the_cap()
    {
        var transport = new RecordingTransport { Body = new byte[] { 1, 2, 3, 4 } };
        var fetcher = new CatalogFetcher(transport, clock: new ImmediateClock());
        var ok = await fetcher.FetchBytesAsync("https://x/a", 16, CancellationToken.None);
        Assert.True(ok.Ok);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, ok.Bytes);
        Assert.Equal(4, ok.ByteCount);
        Assert.Equal(200, ok.StatusCode);
        var capped = await fetcher.FetchBytesAsync("https://x/a", 3, CancellationToken.None);
        Assert.False(capped.Ok);
        Assert.Equal(InstallReasons.FetchSizeCeiling, capped.Error);
        // The text path is the same body decoded.
        var text = await fetcher.FetchTextAsync("https://x/a", 16, CancellationToken.None);
        Assert.True(text.Ok);
        Assert.Equal(4, text.ByteCount);
    }

    // ---- Fakes ------------------------------------------------------------------------

    private sealed class FakeFetch
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, CatalogBytesFetchResult> _answers = new();
        private readonly List<TaskCompletionSource<bool>> _holds = new();
        private int _concurrent;
        public bool Hold;
        public int Started;
        public int MaxConcurrent;
        public bool LastTokenCancelled;

        public void Respond(string url, byte[] body) =>
            _answers[url] = new CatalogBytesFetchResult(true, null, body, body.Length, 200);

        public void Fail(string url, int status) =>
            _answers[url] = new CatalogBytesFetchResult(false, InstallReasons.HttpStatus(status), null, 0, status);

        public async Task<CatalogBytesFetchResult> FetchAsync(string url, CancellationToken ct)
        {
            TaskCompletionSource<bool>? hold = null;
            lock (_gate)
            {
                Started++;
                _concurrent++;
                if (_concurrent > MaxConcurrent) MaxConcurrent = _concurrent;
                if (Hold)
                {
                    hold = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _holds.Add(hold);
                }
            }
            try
            {
                if (hold != null) await hold.Task;
                LastTokenCancelled = ct.IsCancellationRequested;
                if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
                return _answers.TryGetValue(url, out var answer) ? answer : new CatalogBytesFetchResult(false, "no answer", null, 0, 0);
            }
            finally
            {
                lock (_gate) _concurrent--;
            }
        }

        public async Task WaitForStart(int count)
        {
            for (int i = 0; i < 500; i++)
            {
                lock (_gate) if (Started >= count) return;
                await Task.Delay(2);
            }
            throw new TimeoutException("fetch " + count + " never started");
        }

        public void ReleaseAll()
        {
            List<TaskCompletionSource<bool>> holds;
            lock (_gate)
            {
                holds = new List<TaskCompletionSource<bool>>(_holds);
                _holds.Clear();
                Hold = false;
            }
            foreach (var h in holds) h.TrySetResult(true);
        }
    }

    private sealed class FakeDecoder : IIconDecoder
    {
        public readonly Dictionary<string, object> Handles = new();
        public readonly List<string> Destroyed = new();
        public bool RefuseNext;
        public object? Placeholder { get; } = new object();

        public object? Decode(byte[] bytes, string url)
        {
            if (RefuseNext) { RefuseNext = false; return null; }
            var handle = new Handle(url);
            Handles[url] = handle;
            return handle;
        }

        public void Destroy(object handle)
        {
            if (handle is Handle h) Destroyed.Add(h.Url);
        }

        private sealed class Handle
        {
            public Handle(string url) { Url = url; }
            public string Url { get; }
        }
    }

    private sealed class ImmediateClock : ICatalogClock
    {
        public DateTime UtcNow => DateTime.UtcNow;
        public Task Delay(TimeSpan duration, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>A clock that advances one <see cref="Step"/> per read, so spacing is observable without sleeping.</summary>
    private sealed class SteppingClock : ICatalogClock
    {
        public static readonly TimeSpan Step = TimeSpan.FromMilliseconds(5);
        private DateTime _now = new(2026, 9, 12, 0, 0, 0, DateTimeKind.Utc);
        public readonly List<TimeSpan> Delays = new();

        public DateTime UtcNow
        {
            get
            {
                _now += Step;
                return _now;
            }
        }

        public Task Delay(TimeSpan duration, CancellationToken cancellationToken)
        {
            Delays.Add(duration);
            _now += duration;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingTransport : ICatalogTransport
    {
        public string? LastUrl;
        public byte[] Body = { 0x89, 0x50, 0x4E, 0x47 };

        public Task<CatalogTransportResponse> SendAsync(CatalogTransportRequest request, CancellationToken cancellationToken)
        {
            LastUrl = request.Uri.ToString();
            var stream = new System.IO.MemoryStream(Body);
            return Task.FromResult(new CatalogTransportResponse(200, stream, null, Body.Length, null));
        }
    }
}
