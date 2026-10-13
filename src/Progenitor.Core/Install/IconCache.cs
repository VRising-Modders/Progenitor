using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Progenitor.Install;

/// <summary>
/// Turns fetched icon bytes into a drawable handle (a <c>UnityEngine.Sprite</c> on the client)
/// and destroys it again. Both run on the main thread. Game-free by contract so the cache
/// compiles into the tests with a fake decoder.
/// </summary>
internal interface IIconDecoder
{
    /// <summary>Decode <paramref name="bytes"/>; null when they are not an image.</summary>
    object? Decode(byte[] bytes, string url);

    /// <summary>Release a handle from <see cref="Decode"/>.</summary>
    void Destroy(object handle);

    /// <summary>The sprite an image cell shows while its icon loads (or when it never arrives).</summary>
    object? Placeholder { get; }
}

/// <summary>State of one URL in <see cref="IconCache"/>.</summary>
internal enum IconState
{
    /// <summary>Never asked for.</summary>
    Missing = 0,
    /// <summary>Asked for; waiting its turn.</summary>
    Queued = 1,
    /// <summary>The one request in flight.</summary>
    Loading = 2,
    /// <summary>Decoded and drawable.</summary>
    Ready = 3,
    /// <summary>Fetch or decode failed, or the request was canceled; asked again later.</summary>
    Failed = 4,
}

/// <summary>
/// Per-process, in-memory cache of listing icons keyed by URL (LIST1). One fetch in flight,
/// at least <see cref="MinSpacing"/> between fetches, each body capped at
/// <see cref="MaxBytes"/> through the catalog transport
/// (<see cref="CatalogFetcher.FetchBytesAsync"/>), decoded on the main thread through the
/// supplied marshaler, kept LRU up to <see cref="DefaultCapacity"/> entries (an evicted
/// handle is destroyed), and cancellable per request and as a whole. Nothing here is
/// per-frame and nothing is written to disk. The client's decoder tracks its textures so
/// Core Uninstall destroys them even if this cache is dropped without <see cref="ReleaseAll"/>.
/// </summary>
internal sealed class IconCache
{
    public const long MaxBytes = 512L * 1024;
    public const int DefaultCapacity = 256;
    public static readonly TimeSpan MinSpacing = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// ICON2: how long one fetch (or one marshaled completion) may take before the entry
    /// fails and the queue moves on. Without it a network fetch that never answers — or a
    /// marshaler that never runs its action — parked the single worker forever and every
    /// later request only queued.
    /// </summary>
    public static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(15);

    /// <summary>ICON2: how long a <see cref="IconState.Failed"/> URL is left alone before a caller asks again.</summary>
    public static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds(30);

    /// <summary>The error a timed-out fetch or completion leaves on the entry.</summary>
    public const string TimedOut = "icon fetch timed out";

    private sealed class Entry
    {
        public IconState State;
        public object? Handle;
        public string? Error;
        public bool FromDisk;
        public TaskCompletionSource<bool>? Pending;
        public CancellationTokenSource? Cts;
        public LinkedListNode<string>? Node;

        /// <summary>
        /// INSTALLUX1 item 7: this request may only be served from disk. A miss retires the entry
        /// (back to <see cref="IconState.Missing"/>) instead of falling through to the network
        /// lane, so the build-time sweep over every visible row cannot queue hundreds of fetches.
        /// </summary>
        public bool DiskOnly;
    }

    private readonly Func<string, CancellationToken, Task<CatalogBytesFetchResult>> _fetch;
    private readonly IIconDecoder _decoder;
    private readonly ICatalogClock _clock;
    private readonly Action<Action> _post;
    private readonly int _capacity;
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _recent = new();
    private readonly Queue<string> _queue = new();
    private readonly Queue<string> _diskQueue = new();
    private CancellationTokenSource _all = new();
    private bool _running;
    private bool _diskRunning;
    private bool _inFlight;
    private int _workerGen;
    private string? _inFlightUrl;
    private string? _lastError;
    private DateTime _inFlightSince;
    private DateTime _lastStart = DateTime.MinValue;
    private int _fetches;
    private int _diskHits;

    public IconCache(
        Func<string, CancellationToken, Task<CatalogBytesFetchResult>> fetch,
        IIconDecoder decoder,
        ICatalogClock? clock = null,
        Action<Action>? post = null,
        int capacity = DefaultCapacity)
    {
        _fetch = fetch ?? throw new ArgumentNullException(nameof(fetch));
        _decoder = decoder ?? throw new ArgumentNullException(nameof(decoder));
        _clock = clock ?? new SystemCatalogClock();
        _post = post ?? (action => action());
        _capacity = capacity < 1 ? 1 : capacity;
    }

    /// <summary>Build over a <see cref="CatalogFetcher"/>: the bytes call capped at <see cref="MaxBytes"/>.</summary>
    public static IconCache Over(CatalogFetcher fetcher, IIconDecoder decoder, ICatalogClock? clock = null, Action<Action>? post = null, int capacity = DefaultCapacity)
    {
        if (fetcher == null) throw new ArgumentNullException(nameof(fetcher));
        return new IconCache((url, ct) => fetcher.FetchBytesAsync(url, MaxBytes, ct), decoder, clock, post, capacity);
    }

    /// <summary>Raised through the marshaler when a URL becomes <see cref="IconState.Ready"/> (or fails), so image cells repaint in place.</summary>
    public event Action<string>? Arrived;

    /// <summary>
    /// ICON2 lane 1: a synchronous "are these bytes already on disk" probe (null on a miss).
    /// A hit is decoded straight away on its own lane, so a disk-cached icon never waits
    /// behind a slow or hung network fetch. Set by the pane from its
    /// <see cref="ListingDiskCache"/>; null leaves every request on the network lane.
    /// </summary>
    public Func<string, byte[]?>? DiskProbe { get; set; }

    /// <summary>
    /// The delay backing <see cref="FetchTimeout"/>. A real timer by default; tests replace it
    /// to make a timeout deterministic. Canceled as soon as the awaited step completes, so no
    /// timer outlives its fetch.
    /// </summary>
    public Func<TimeSpan, CancellationToken, Task> TimeoutDelay { get; set; } =
        (duration, token) => Task.Delay(duration, token);

    public object? Placeholder => _decoder.Placeholder;

    public int Capacity => _capacity;

    /// <summary>Decoded entries held.</summary>
    public int Count
    {
        get { lock (_gate) return _recent.Count; }
    }

    public bool InFlight
    {
        get { lock (_gate) return _inFlight; }
    }

    public int QueuedCount
    {
        get { lock (_gate) return _queue.Count + _diskQueue.Count; }
    }

    /// <summary>How many fetches were started over the cache's life.</summary>
    public int FetchCount
    {
        get { lock (_gate) return _fetches; }
    }

    /// <summary>How many entries were served from <see cref="DiskProbe"/> without a network fetch.</summary>
    public int DiskHitCount
    {
        get { lock (_gate) return _diskHits; }
    }

    /// <summary>Readback: the URL of the one network fetch in flight, or null.</summary>
    public string? InFlightUrl
    {
        get { lock (_gate) return _inFlight ? _inFlightUrl : null; }
    }

    /// <summary>Readback: the last error any entry settled with, or null.</summary>
    public string? LastError
    {
        get { lock (_gate) return _lastError; }
    }

    /// <summary>Readback: seconds the current network fetch has been in flight (0 when idle). Over <see cref="FetchTimeout"/> means a stall the timeout is about to break.</summary>
    public double StalledSeconds
    {
        get
        {
            lock (_gate)
            {
                if (!_inFlight) return 0;
                double seconds = (_clock.UtcNow - _inFlightSince).TotalSeconds;
                return seconds > 0 ? seconds : 0;
            }
        }
    }

    /// <summary>True when <paramref name="url"/> was served from the disk lane rather than the network.</summary>
    public bool WasDiskHit(string? url)
    {
        if (string.IsNullOrEmpty(url)) return false;
        lock (_gate) return _entries.TryGetValue(url, out var entry) && entry.FromDisk;
    }

    /// <summary>Cached URLs, most recently used first.</summary>
    public IReadOnlyList<string> RecentKeys
    {
        get { lock (_gate) return new List<string>(_recent); }
    }

    public IconState StateOf(string? url)
    {
        if (string.IsNullOrEmpty(url)) return IconState.Missing;
        lock (_gate) return _entries.TryGetValue(url, out var entry) ? entry.State : IconState.Missing;
    }

    public string? ErrorOf(string? url)
    {
        if (string.IsNullOrEmpty(url)) return null;
        lock (_gate) return _entries.TryGetValue(url, out var entry) ? entry.Error : null;
    }

    /// <summary>
    /// The decoded handle for <paramref name="url"/> (marks it recently used), or false with
    /// <see cref="Placeholder"/> in <paramref name="sprite"/> while it is missing, queued,
    /// loading or failed. Never starts a fetch: pair it with <see cref="RequestAsync"/>.
    /// </summary>
    public bool TryGetSprite(string? url, out object? sprite)
    {
        lock (_gate)
        {
            if (!string.IsNullOrEmpty(url) && _entries.TryGetValue(url, out var entry) && entry.State == IconState.Ready)
            {
                Touch(entry, url);
                sprite = entry.Handle;
                return true;
            }
        }
        sprite = _decoder.Placeholder;
        return false;
    }

    /// <summary>
    /// Ask for <paramref name="url"/>. True once it is <see cref="IconState.Ready"/>; false
    /// when the fetch or decode failed or <paramref name="cancellationToken"/> was canceled
    /// while it was still queued or in flight. A URL already ready answers at once; one
    /// already queued or loading shares its pending task. Never throws.
    /// </summary>
    /// <summary>
    /// INSTALLUX1 item 7: ask the disk lane alone for <paramref name="url"/>. A file under
    /// <c>progenitor-cache/icons/</c> becomes <see cref="IconState.Ready"/> with no network work;
    /// a miss leaves the entry exactly as it was (<see cref="IconState.Missing"/>), so the caller
    /// can sweep every listing on screen cheaply and leave the network lane to the scroll window.
    /// Returns false at once when there is no <see cref="DiskProbe"/>, or when the URL is already
    /// ready, queued, loading or failed — those are someone else's request to finish.
    /// </summary>
    public Task<bool> RequestFromDiskAsync(string? url, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(url) || DiskProbe == null) return Task.FromResult(false);
        url = url.Trim();
        lock (_gate)
        {
            if (_entries.TryGetValue(url, out var existing))
            {
                if (existing.State == IconState.Ready)
                {
                    Touch(existing, url);
                    return Task.FromResult(true);
                }

                // Queued/Loading belongs to a live request; Failed keeps its reason until the
                // caller's own RetryAfter window says to ask again.
                if (existing.State != IconState.Missing) return Task.FromResult(false);
            }
        }

        return RequestAsync(url, cancellationToken, diskOnly: true);
    }

    public Task<bool> RequestAsync(string? url, CancellationToken cancellationToken = default) =>
        RequestAsync(url, cancellationToken, diskOnly: false);

    private Task<bool> RequestAsync(string? url, CancellationToken cancellationToken, bool diskOnly)
    {
        if (string.IsNullOrWhiteSpace(url)) return Task.FromResult(false);
        url = url.Trim();
        Entry entry;
        bool start = false;
        bool startDisk = false;
        int gen;
        lock (_gate)
        {
            if (_entries.TryGetValue(url, out var existing))
            {
                if (existing.State == IconState.Ready)
                {
                    Touch(existing, url);
                    return Task.FromResult(true);
                }
                if (existing.State is IconState.Queued or IconState.Loading && existing.Pending != null)
                {
                    RegisterCancel(existing, url, cancellationToken);
                    return existing.Pending.Task;
                }
                entry = existing;
            }
            else
            {
                entry = new Entry();
                _entries[url] = entry;
            }
            entry.State = IconState.Queued;
            entry.Error = null;
            entry.FromDisk = false;
            entry.DiskOnly = diskOnly;
            entry.Pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            entry.Cts = CancellationTokenSource.CreateLinkedTokenSource(_all.Token);
            RegisterCancel(entry, url, cancellationToken);
            gen = _workerGen;
            if (DiskProbe != null)
            {
                // Lane 1: probe the disk first, so a cached icon is never queued behind a fetch.
                _diskQueue.Enqueue(url);
                startDisk = !_diskRunning;
                if (startDisk) _diskRunning = true;
            }
            else
            {
                _queue.Enqueue(url);
                start = !_running;
                if (start) _running = true;
            }
        }
        // Captured before the worker starts: with an immediate clock and marshaler the whole
        // fetch can settle (and clear Pending) before this method returns.
        var task = entry.Pending!.Task;
        if (startDisk) _ = RunDiskAsync(gen);
        if (start) _ = RunAsync(gen);
        return task;
    }

    private void RegisterCancel(Entry entry, string url, CancellationToken token)
    {
        if (!token.CanBeCanceled) return;
        token.Register(() => Cancel(url), useSynchronizationContext: false);
    }

    /// <summary>Drop <paramref name="url"/> from the queue or cancel its in-flight fetch. A ready entry is kept.</summary>
    public void Cancel(string? url)
    {
        if (string.IsNullOrEmpty(url)) return;
        TaskCompletionSource<bool>? pending = null;
        lock (_gate)
        {
            if (!_entries.TryGetValue(url, out var entry)) return;
            if (entry.State == IconState.Queued)
            {
                entry.State = IconState.Failed;
                entry.Error = InstallReasons.FetchCancelled;
                pending = entry.Pending;
                entry.Pending = null;
                entry.Cts?.Dispose();
                entry.Cts = null;
            }
            else if (entry.State == IconState.Loading)
            {
                try { entry.Cts?.Cancel(); } catch { /* */ }
            }
        }
        pending?.TrySetResult(false);
    }

    /// <summary>Cancel everything queued or in flight; ready entries stay.</summary>
    public void CancelAll()
    {
        List<TaskCompletionSource<bool>> pending = new();
        CancellationTokenSource old;
        lock (_gate)
        {
            old = _all;
            _all = new CancellationTokenSource();
            _queue.Clear();
            _diskQueue.Clear();
            // ICON2 (d): the worker generation is bumped, so a worker still parked in an
            // abandoned fetch retires without touching the flags — the next RequestAsync
            // starts a fresh worker instead of only enqueuing behind a dead one.
            _workerGen++;
            _running = false;
            _diskRunning = false;
            _inFlight = false;
            _inFlightUrl = null;
            foreach (var pair in _entries)
            {
                var entry = pair.Value;
                if (entry.State == IconState.Queued)
                {
                    entry.State = IconState.Failed;
                    entry.Error = InstallReasons.FetchCancelled;
                    if (entry.Pending != null) pending.Add(entry.Pending);
                    entry.Pending = null;
                    entry.Cts?.Dispose();
                    entry.Cts = null;
                }
            }
        }
        try { old.Cancel(); } catch { /* */ }
        old.Dispose();
        for (int i = 0; i < pending.Count; i++) pending[i].TrySetResult(false);
    }

    /// <summary>Cancel everything and destroy every decoded handle. Call on the main thread (pane dispose, Core unload).</summary>
    public void ReleaseAll()
    {
        CancelAll();
        List<object> handles = new();
        lock (_gate)
        {
            foreach (var pair in _entries)
            {
                if (pair.Value.Handle != null) handles.Add(pair.Value.Handle);
                pair.Value.Handle = null;
                pair.Value.State = IconState.Missing;
            }
            _entries.Clear();
            _recent.Clear();
        }
        for (int i = 0; i < handles.Count; i++)
        {
            try { _decoder.Destroy(handles[i]); } catch { /* */ }
        }
    }

    private void Touch(Entry entry, string url)
    {
        if (entry.Node != null) _recent.Remove(entry.Node);
        entry.Node = _recent.AddFirst(url);
    }

    /// <summary>True once <see cref="CancelAll"/> (or <see cref="ReleaseAll"/>) retired this worker's generation.</summary>
    private bool Stale(int gen)
    {
        lock (_gate) return gen != _workerGen;
    }

    /// <summary>
    /// ICON2 lane 1: serve every request that is already on disk, in order, with no spacing and
    /// no network. A miss is handed to the network lane (and starts it if it is idle).
    /// </summary>
    private async Task RunDiskAsync(int gen)
    {
        while (true)
        {
            string url;
            Entry entry;
            lock (_gate)
            {
                if (gen != _workerGen) return;
                while (true)
                {
                    if (_diskQueue.Count == 0)
                    {
                        _diskRunning = false;
                        return;
                    }
                    url = _diskQueue.Dequeue();
                    if (_entries.TryGetValue(url, out var next) && next.State == IconState.Queued && next.Cts != null)
                    {
                        entry = next;
                        break;
                    }
                }
            }

            var probe = DiskProbe;
            byte[]? bytes = null;
            if (probe != null)
            {
                string probed = url;
                try { bytes = await Task.Run(() => { try { return probe(probed); } catch { return null; } }).ConfigureAwait(false); }
                catch { bytes = null; }
            }

            if (bytes == null || bytes.Length == 0)
            {
                bool start;
                TaskCompletionSource<bool>? unwanted = null;
                lock (_gate)
                {
                    if (gen != _workerGen) return;
                    if (entry.DiskOnly)
                    {
                        // INSTALLUX1 item 7: a disk-only miss is not an error and never reaches the
                        // network lane. The entry retires to Missing so the next real request (the
                        // scroll window) starts clean.
                        unwanted = entry.Pending;
                        entry.Pending = null;
                        entry.State = IconState.Missing;
                        entry.DiskOnly = false;
                        try { entry.Cts?.Dispose(); }
                        catch { /* */ }
                        entry.Cts = null;
                        if (entry.Node != null) _recent.Remove(entry.Node);
                        entry.Node = null;
                        _entries.Remove(url);
                        start = false;
                    }
                    else
                    {
                        _queue.Enqueue(url);
                        start = !_running;
                        if (start) _running = true;
                    }
                }

                try { unwanted?.TrySetResult(false); }
                catch { /* */ }
                if (start) _ = RunAsync(gen);
                continue;
            }

            lock (_gate)
            {
                if (gen != _workerGen) return;
                entry.FromDisk = true;
                _diskHits++;
            }
            await CompleteThroughPost(entry, url, new CatalogBytesFetchResult(true, null, bytes, bytes.Length, 0), false, gen).ConfigureAwait(false);
        }
    }

    private async Task RunAsync(int gen)
    {
        while (true)
        {
            string url;
            Entry entry;
            CancellationToken token;
            TimeSpan wait;
            lock (_gate)
            {
                if (gen != _workerGen) return;
                while (true)
                {
                    if (_queue.Count == 0)
                    {
                        _running = false;
                        return;
                    }
                    url = _queue.Dequeue();
                    if (_entries.TryGetValue(url, out var next) && next.State == IconState.Queued && next.Cts != null)
                    {
                        entry = next;
                        break;
                    }
                }
                var elapsed = _clock.UtcNow - _lastStart;
                wait = elapsed >= MinSpacing ? TimeSpan.Zero : MinSpacing - elapsed;
                token = entry.Cts!.Token;
            }

            if (wait > TimeSpan.Zero)
            {
                try { await _clock.Delay(wait, token).ConfigureAwait(false); }
                catch (OperationCanceledException) { /* this entry was canceled while waiting its turn */ }
            }

            lock (_gate)
            {
                if (entry.State != IconState.Queued || token.IsCancellationRequested)
                {
                    Finish(entry, url, null, InstallReasons.FetchCancelled);
                    continue;
                }
                entry.State = IconState.Loading;
                _inFlight = true;
                _inFlightUrl = url;
                _inFlightSince = _clock.UtcNow;
                _lastStart = _clock.UtcNow;
                _fetches++;
            }

            var fetchTask = FetchSafeAsync(url, token);
            bool answered = await WaitOrTimeout(fetchTask).ConfigureAwait(false);
            lock (_gate)
            {
                _inFlight = false;
                _inFlightUrl = null;
            }
            if (!answered)
            {
                // ICON2 (a): a fetch that never answers fails its own entry and the queue moves
                // on. The abandoned fetch is canceled and its result is never completed.
                try { entry.Cts?.Cancel(); } catch { /* */ }
                Observe(fetchTask);
                lock (_gate) Finish(entry, url, null, TimedOut);
                try { Arrived?.Invoke(url); } catch { /* */ }
                continue;
            }

            CatalogBytesFetchResult result = fetchTask.Result;
            await CompleteThroughPost(entry, url, result, token.IsCancellationRequested, gen).ConfigureAwait(false);
        }
    }

    private async Task<CatalogBytesFetchResult> FetchSafeAsync(string url, CancellationToken token)
    {
        try { return await _fetch(url, token).ConfigureAwait(false); }
        catch (OperationCanceledException) { return new CatalogBytesFetchResult(false, InstallReasons.FetchCancelled, null, 0, 0); }
        catch (Exception e) { return new CatalogBytesFetchResult(false, InstallReasons.FetchTransport + ": " + e.Message, null, 0, 0); }
    }

    /// <summary>
    /// Await <paramref name="task"/> for at most <see cref="FetchTimeout"/>. False: it timed out
    /// and the caller must settle the entry itself. The timer is canceled on the normal path,
    /// so nothing keeps ticking after a fetch lands.
    /// </summary>
    private async Task<bool> WaitOrTimeout(Task task)
    {
        if (task.IsCompleted) return true;
        using var cts = new CancellationTokenSource();
        Task timer;
        try { timer = TimeoutDelay(FetchTimeout, cts.Token) ?? Task.CompletedTask; }
        catch { return await AwaitQuietly(task).ConfigureAwait(false); }
        var first = await Task.WhenAny(task, timer).ConfigureAwait(false);
        if (!ReferenceEquals(first, task)) return false;
        try { cts.Cancel(); } catch { /* */ }
        Observe(timer);
        return true;
    }

    private static async Task<bool> AwaitQuietly(Task task)
    {
        try { await task.ConfigureAwait(false); } catch { /* the caller reads the result */ }
        return true;
    }

    private static void Observe(Task task)
    {
        if (task.IsCompleted) { _ = task.Exception; return; }
        _ = task.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>
    /// Decode and publish through the marshaler. A marshaler that never runs the action (or a
    /// completion that hangs) fails the entry after <see cref="FetchTimeout"/> instead of parking
    /// the worker; a stale generation discards the result.
    /// </summary>
    private async Task<bool> CompleteThroughPost(Entry entry, string url, CatalogBytesFetchResult result, bool cancelled, int gen)
    {
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<bool>? pending;
        lock (_gate) pending = entry.Pending;
        try
        {
            _post(() =>
            {
                try
                {
                    if (Stale(gen)) Abandon(entry, pending);
                    else Complete(entry, url, result, cancelled);
                }
                finally { done.TrySetResult(true); }
            });
        }
        catch (Exception e)
        {
            lock (_gate) Finish(entry, url, null, InstallReasons.FetchTransport + ": " + e.Message);
            done.TrySetResult(true);
            try { Arrived?.Invoke(url); } catch { /* */ }
            return true;
        }
        if (await WaitOrTimeout(done.Task).ConfigureAwait(false)) return true;
        lock (_gate) Finish(entry, url, null, TimedOut);
        try { Arrived?.Invoke(url); } catch { /* */ }
        return false;
    }

    /// <summary>
    /// A retired generation's result is discarded, but the caller that asked for it is still
    /// answered (false) — otherwise a <see cref="ReleaseAll"/> during a fetch would leave that
    /// <see cref="RequestAsync"/> task pending forever. Only this request's own task is settled:
    /// a URL asked for again after the release carries a different one.
    /// </summary>
    private void Abandon(Entry entry, TaskCompletionSource<bool>? pending)
    {
        if (pending == null) return;
        lock (_gate)
        {
            if (ReferenceEquals(entry.Pending, pending)) entry.Pending = null;
        }
        pending.TrySetResult(false);
    }

    /// <summary>Main thread (through the marshaler): decode, store, evict, publish.</summary>
    private void Complete(Entry entry, string url, CatalogBytesFetchResult result, bool cancelled)
    {
        object? handle = null;
        string? error = null;
        if (cancelled) error = InstallReasons.FetchCancelled;
        else if (!result.Ok || result.Bytes == null) error = result.Error ?? InstallReasons.FetchTransport;
        else if (result.Bytes.Length > MaxBytes) error = InstallReasons.FetchSizeCeiling;
        else
        {
            try { handle = _decoder.Decode(result.Bytes, url); }
            catch (Exception e) { handle = null; error = "decode: " + e.Message; }
            if (handle == null && error == null) error = "decode failed";
        }

        List<object> evicted = new();
        lock (_gate)
        {
            Finish(entry, url, handle, error);
            if (handle != null)
            {
                Touch(entry, url);
                while (_recent.Count > _capacity && _recent.Last != null)
                {
                    string old = _recent.Last.Value;
                    _recent.RemoveLast();
                    if (_entries.TryGetValue(old, out var victim))
                    {
                        if (victim.Handle != null) evicted.Add(victim.Handle);
                        _entries.Remove(old);
                    }
                }
            }
        }
        for (int i = 0; i < evicted.Count; i++)
        {
            try { _decoder.Destroy(evicted[i]); } catch { /* */ }
        }
        try { Arrived?.Invoke(url); }
        catch { /* a listener's fault is not the cache's */ }
    }

    /// <summary>Under <see cref="_gate"/>: settle the entry and release its pending task.</summary>
    private void Finish(Entry entry, string url, object? handle, string? error)
    {
        entry.Cts?.Dispose();
        entry.Cts = null;
        var pending = entry.Pending;
        entry.Pending = null;
        if (handle != null)
        {
            entry.Handle = handle;
            entry.State = IconState.Ready;
            entry.Error = null;
        }
        else
        {
            entry.State = IconState.Failed;
            entry.Error = error ?? "failed";
            _lastError = entry.Error;
        }
        pending?.TrySetResult(handle != null);
    }
}
