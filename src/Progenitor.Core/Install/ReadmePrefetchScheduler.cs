using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Progenitor.Install;

/// <summary>Prefetch state for the Install filter row and <c>widget_read</c>.</summary>
internal enum ReadmePrefetchState
{
    Idle = 0,
    Running = 1,
    Done = 2,
    Cancelled = 3,
}

/// <summary>
/// Bounded README prefetch: one request in flight, at least <see cref="MinSpacing"/> between
/// requests, cancellable, with <c>n/m</c> progress. TSTRAFFIC1: the Install pane starts it from
/// each list build for the rows on screen only (<c>InstallModsPane.ResumePrefetch</c>, at most
/// <c>InstallModsPane.MaxReadmePrefetchPerBuild</c> keys) — never for the whole index (LIST2 did
/// that, and every Install build swept the index). Any other README is fetched when its drawer
/// opens. Results are delivered through the supplied marshaler so the pane touches its state on
/// the main thread only. Nothing here is per-frame; the fetch it calls owns the disk cache.
/// </summary>
internal sealed class ReadmePrefetchScheduler
{
    public static readonly TimeSpan MinSpacing = TimeSpan.FromMilliseconds(250);

    private readonly Func<string, CancellationToken, Task<ListingDocumentSet>> _fetch;
    private readonly ICatalogClock _clock;
    private readonly Action<Action> _post;
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private int _generation;
    private int _done;
    private int _total;
    private ReadmePrefetchState _state = ReadmePrefetchState.Idle;

    public ReadmePrefetchScheduler(
        Func<string, CancellationToken, Task<ListingDocumentSet>> fetch,
        ICatalogClock? clock = null,
        Action<Action>? post = null)
    {
        _fetch = fetch ?? throw new ArgumentNullException(nameof(fetch));
        _clock = clock ?? new SystemCatalogClock();
        _post = post ?? (action => action());
    }

    public ReadmePrefetchState State
    {
        get { lock (_gate) return _state; }
    }

    public bool Running => State == ReadmePrefetchState.Running;

    public int Done
    {
        get { lock (_gate) return _done; }
    }

    public int Total
    {
        get { lock (_gate) return _total; }
    }

    /// <summary><c>n/m</c>: requests completed over requests asked for.</summary>
    public string Progress
    {
        get { lock (_gate) return _done + "/" + _total; }
    }

    public static string StateName(ReadmePrefetchState state) => state switch
    {
        ReadmePrefetchState.Running => "running",
        ReadmePrefetchState.Done => "done",
        ReadmePrefetchState.Cancelled => "cancelled",
        _ => "idle",
    };

    public object ToWidget()
    {
        lock (_gate)
        {
            return new
            {
                state = StateName(_state),
                done = _done,
                total = _total,
                progress = _done + "/" + _total,
            };
        }
    }

    /// <summary>
    /// Start a run over <paramref name="keys"/>. A run already in progress is canceled
    /// first. <paramref name="onResult"/> and <paramref name="onFinished"/> run through the
    /// marshaler, in order, one per key; a result that lands after <see cref="Cancel"/>
    /// is dropped. Returns false (and stays idle) for an empty set.
    /// </summary>
    public bool Start(
        IReadOnlyList<string> keys,
        Action<string, ListingDocumentSet> onResult,
        Action? onFinished = null)
    {
        if (keys == null) throw new ArgumentNullException(nameof(keys));
        if (onResult == null) throw new ArgumentNullException(nameof(onResult));
        Cancel();
        var snapshot = new List<string>(keys.Count);
        for (int i = 0; i < keys.Count; i++)
        {
            if (!string.IsNullOrWhiteSpace(keys[i])) snapshot.Add(keys[i].Trim());
        }

        if (snapshot.Count == 0)
        {
            lock (_gate)
            {
                _done = 0;
                _total = 0;
                _state = ReadmePrefetchState.Done;
            }

            return false;
        }

        CancellationTokenSource cts;
        int generation;
        lock (_gate)
        {
            _cts = cts = new CancellationTokenSource();
            generation = ++_generation;
            _done = 0;
            _total = snapshot.Count;
            _state = ReadmePrefetchState.Running;
        }

        _ = RunAsync(snapshot, generation, cts, onResult, onFinished);
        return true;
    }

    /// <summary>Stop after the request in flight; its result is discarded. Idempotent.</summary>
    public void Cancel()
    {
        CancellationTokenSource? cts;
        lock (_gate)
        {
            cts = _cts;
            _cts = null;
            if (_state == ReadmePrefetchState.Running) _state = ReadmePrefetchState.Cancelled;
            _generation++;
        }

        if (cts == null) return;
        try { cts.Cancel(); }
        catch { /* */ }
        try { cts.Dispose(); }
        catch { /* */ }
    }

    private async Task RunAsync(
        List<string> keys,
        int generation,
        CancellationTokenSource cts,
        Action<string, ListingDocumentSet> onResult,
        Action? onFinished)
    {
        CancellationToken ct = cts.Token;
        try
        {
            for (int i = 0; i < keys.Count; i++)
            {
                if (ct.IsCancellationRequested || !IsCurrent(generation)) return;
                if (i > 0)
                {
                    try { await _clock.Delay(MinSpacing, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }
                }

                if (ct.IsCancellationRequested || !IsCurrent(generation)) return;
                string key = keys[i];
                ListingDocumentSet set;
                try
                {
                    set = await _fetch(key, ct).ConfigureAwait(false)
                        ?? ListingDocumentSet.Unavailable(key, InstallReasons.FetchTransport);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception e)
                {
                    set = ListingDocumentSet.Unavailable(key, e.Message);
                }

                if (!IsCurrent(generation)) return;
                var delivered = set;
                _post(() =>
                {
                    if (!IsCurrent(generation)) return;
                    lock (_gate) _done++;
                    onResult(key, delivered);
                });
            }
        }
        finally
        {
            bool finished;
            lock (_gate)
            {
                finished = _generation == generation;
                if (finished)
                {
                    _state = ReadmePrefetchState.Done;
                    if (ReferenceEquals(_cts, cts)) _cts = null;
                }
            }

            if (finished)
            {
                try { cts.Dispose(); }
                catch { /* */ }
                if (onFinished != null) _post(onFinished);
            }
        }
    }

    private bool IsCurrent(int generation)
    {
        lock (_gate) return _generation == generation;
    }
}
