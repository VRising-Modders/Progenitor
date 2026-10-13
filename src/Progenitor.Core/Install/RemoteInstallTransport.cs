using System;
using System.IO;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Threading;
using System.Threading.Tasks;

namespace Progenitor.Install;

/// <summary>REMOTEINST1: why a fetch the remote installer made did not complete.</summary>
internal enum FetchFailureCause
{
    None = 0,
    Dns,
    Refused,
    Timeout,
    Tls,
    Http,
    Stalled,
    Truncated,
    Network,
    Cancelled,
}

/// <summary>The last failure one <see cref="RecordingCatalogTransport"/> saw.</summary>
internal readonly struct FetchFailure
{
    public FetchFailure(FetchFailureCause cause, int status, string detail)
    {
        Cause = cause;
        Status = status;
        Detail = detail ?? "";
    }

    public FetchFailureCause Cause { get; }
    public int Status { get; }
    public string Detail { get; }

    /// <summary>The sub-cause word the wire carries: <c>dns</c>, <c>timeout</c>, <c>tls</c>, <c>http-404</c>, <c>stalled</c>, …</summary>
    public string Word => Cause switch
    {
        FetchFailureCause.Dns => "dns",
        FetchFailureCause.Refused => "refused",
        FetchFailureCause.Timeout => "timeout",
        FetchFailureCause.Tls => "tls",
        FetchFailureCause.Http => "http-" + Status.ToString(System.Globalization.CultureInfo.InvariantCulture),
        FetchFailureCause.Stalled => "stalled",
        FetchFailureCause.Truncated => "truncated",
        FetchFailureCause.Network => "network",
        FetchFailureCause.Cancelled => "cancelled",
        _ => "",
    };

    /// <summary>
    /// A failure that means "this server cannot reach Thunderstore", as opposed to a bad transfer. An
    /// HTTP answer counts only when it is the kind a blocking proxy, a rate limit or an outage gives
    /// (403, 407, 429, 5xx); a 404 is Thunderstore answering about one file.
    /// </summary>
    public bool IsReachability => Cause is FetchFailureCause.Dns or FetchFailureCause.Refused or FetchFailureCause.Timeout
        or FetchFailureCause.Tls or FetchFailureCause.Stalled or FetchFailureCause.Network
        || (Cause == FetchFailureCause.Http && (Status is 403 or 407 or 429 || Status >= 500));
}

/// <summary>
/// REMOTEINST1: the server's transport around the shared one. <see cref="CatalogFetcher"/> turns a
/// transport failure into a sentence, which is right for the Install tab and loses what the remote
/// admin needs to be told — DNS, a timeout, TLS, an HTTP status, a stalled or truncated transfer.
/// This wrapper records the cause of the last failure for the remote installer to read back, and
/// bounds every body read with a stall timer (no bytes for <see cref="StallTimeout"/> fails the
/// read). It changes nothing about what is fetched or how the fetcher verifies it.
/// </summary>
internal sealed class RecordingCatalogTransport : ICatalogTransport
{
    private readonly ICatalogTransport _inner;
    private readonly Func<TimeSpan> _stall;
    private readonly object _gate = new();
    private FetchFailure _last;

    public RecordingCatalogTransport(ICatalogTransport inner, TimeSpan stallTimeout)
        : this(inner, () => stallTimeout)
    {
    }

    /// <summary>The stall timer is read per body read, so a setting bound after construction applies.</summary>
    public RecordingCatalogTransport(ICatalogTransport inner, Func<TimeSpan> stallTimeout)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _stall = stallTimeout ?? (() => TimeSpan.FromSeconds(RemoteInstallSettings.DefaultStallTimeoutSeconds));
    }

    public TimeSpan StallTimeout
    {
        get
        {
            TimeSpan value;
            try { value = _stall(); }
            catch { value = TimeSpan.Zero; }
            return value > TimeSpan.Zero ? value : TimeSpan.FromSeconds(RemoteInstallSettings.DefaultStallTimeoutSeconds);
        }
    }

    public FetchFailure LastFailure
    {
        get { lock (_gate) return _last; }
    }

    public void Reset()
    {
        lock (_gate) _last = default;
    }

    internal void Record(FetchFailureCause cause, int status, string detail)
    {
        lock (_gate) _last = new FetchFailure(cause, status, detail);
    }

    public async Task<CatalogTransportResponse> SendAsync(CatalogTransportRequest request, CancellationToken cancellationToken)
    {
        CatalogTransportResponse response;
        try
        {
            response = await _inner.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            var cause = Classify(e, cancellationToken.IsCancellationRequested);
            Record(cause, 0, e.Message);
            throw;
        }

        int status = response.StatusCode;
        if (status == 429 || status < 200 || status > 399)
            Record(FetchFailureCause.Http, status, "HTTP " + status.ToString(System.Globalization.CultureInfo.InvariantCulture));
        else if (status >= 200 && status <= 299)
            return response.WithContent(new GuardedStream(response, this)); // TSTRAFFIC1: keeps validators and encoding
        return response;
    }

    /// <summary>
    /// The cause behind one transport exception. A cancellation the caller did not ask for is the
    /// connect or total timeout firing (the handler's connect timeout surfaces as a cancellation).
    /// </summary>
    internal static FetchFailureCause Classify(Exception e, bool callerCancelled)
    {
        for (Exception? x = e; x != null; x = x.InnerException)
        {
            switch (x)
            {
                case RemoteFetchStalledException:
                    return FetchFailureCause.Stalled;
                case SocketException se:
                    return se.SocketErrorCode switch
                    {
                        SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain => FetchFailureCause.Dns,
                        SocketError.ConnectionRefused => FetchFailureCause.Refused,
                        SocketError.TimedOut => FetchFailureCause.Timeout,
                        _ => FetchFailureCause.Network,
                    };
                case AuthenticationException:
                    return FetchFailureCause.Tls;
                case TimeoutException:
                    return FetchFailureCause.Timeout;
            }
        }

        // The caller's own token covers the total timeout; any other cancellation is the handler's
        // connect timeout. Either way the server did not get an answer in time.
        if (e is OperationCanceledException) return FetchFailureCause.Timeout;
        string message = e.Message ?? "";
        if (message.IndexOf("No such host", StringComparison.OrdinalIgnoreCase) >= 0
            || message.IndexOf("Name or service not known", StringComparison.OrdinalIgnoreCase) >= 0)
            return FetchFailureCause.Dns;
        if (message.IndexOf("SSL", StringComparison.OrdinalIgnoreCase) >= 0
            || message.IndexOf("TLS", StringComparison.Ordinal) >= 0)
            return FetchFailureCause.Tls;
        if (message.IndexOf("prematurely", StringComparison.OrdinalIgnoreCase) >= 0) return FetchFailureCause.Truncated;
        return FetchFailureCause.Network;
    }

    /// <summary>A body read that went <see cref="StallTimeout"/> without a byte.</summary>
    internal sealed class RemoteFetchStalledException : IOException
    {
        public RemoteFetchStalledException(TimeSpan stall)
            : base("no bytes arrived for " + ((int)stall.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture) + " seconds")
        {
        }
    }

    /// <summary>The response body with a per-read stall timer and failure recording.</summary>
    private sealed class GuardedStream : Stream
    {
        private readonly CatalogTransportResponse _response;
        private readonly RecordingCatalogTransport _owner;
        private readonly Stream _inner;
        private long _read;

        public GuardedStream(CatalogTransportResponse response, RecordingCatalogTransport owner)
        {
            _response = response;
            _owner = owner;
            _inner = response.Content;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _read; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            stall.CancelAfter(_owner.StallTimeout);
            int n;
            try
            {
                try
                {
                    var read = _inner.ReadAsync(buffer, offset, count, stall.Token);
                    // A source that ignores the token (some fakes, some handlers) still cannot hold
                    // the read past the stall timer: the wait itself is bounded.
                    var winner = await Task.WhenAny(read, Task.Delay(Timeout.InfiniteTimeSpan, stall.Token)).ConfigureAwait(false);
                    if (winner != read)
                    {
                        if (cancellationToken.IsCancellationRequested) throw new OperationCanceledException(cancellationToken);
                        _owner.Record(FetchFailureCause.Stalled, 0, "stalled");
                        throw new RemoteFetchStalledException(_owner.StallTimeout);
                    }

                    n = await read.ConfigureAwait(false);
                }
                finally
                {
                    // Releases the bounded wait above; the read has already finished or failed.
                    try { stall.Cancel(); } catch (ObjectDisposedException) { }
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                _owner.Record(FetchFailureCause.Stalled, 0, "stalled");
                throw new RemoteFetchStalledException(_owner.StallTimeout);
            }
            catch (RemoteFetchStalledException)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                // Mid-body, a generic I/O fault is the connection dropping: the transfer was cut short.
                var cause = Classify(e, false);
                _owner.Record(cause == FetchFailureCause.Network ? FetchFailureCause.Truncated : cause, 0, e.Message);
                throw;
            }

            if (n > 0) _read += n;
            else if (_response.ContentLength is > 0 && _read != _response.ContentLength.Value)
                _owner.Record(FetchFailureCause.Truncated, 0,
                    "received " + _read.ToString(System.Globalization.CultureInfo.InvariantCulture) + " of "
                    + _response.ContentLength.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) + " bytes");
            return n;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _response.Dispose();
            base.Dispose(disposing);
        }
    }
}
