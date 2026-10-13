using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace Progenitor.Bootstrap;

/// <summary>Liveness of an already identified hosted child, read through the handle Bootstrap holds.</summary>
internal enum HostedChildLiveness
{
    Running,
    Exited,
    /// <summary>The handle could not be asked. Fail closed: keep the hosting flag and re-identify off thread.</summary>
    Unknown,
}

/// <summary>
/// What the tracker needs from the OS. <c>HostedChildProbe</c> is the Windows
/// implementation; tests pass a fake. Every member except <see cref="Poll"/> and
/// <see cref="Close"/> runs on a background thread only.
/// </summary>
internal interface IHostedChildOs
{
    /// <summary>
    /// PIDs whose process name is one of <paramref name="names"/>. Name is a cheap pre-filter
    /// only; identity is decided by path + argv. Null when the enumeration itself failed.
    /// </summary>
    int[]? CandidatePids(IReadOnlyList<string> names);

    /// <summary>
    /// Open <paramref name="pid"/> and read its image path and command line through that one
    /// handle, so identity and the handle later polled are the same process object (a reused PID
    /// cannot slip in between). False when it could not be opened or neither value could be read;
    /// <paramref name="handle"/> is then <see cref="IntPtr.Zero"/>.
    /// </summary>
    bool TryOpen(int pid, out IntPtr handle, out string? path, out string? commandLine);

    /// <summary>Non-blocking liveness of an open handle (main thread; microseconds).</summary>
    HostedChildLiveness Poll(IntPtr handle);

    void Close(IntPtr handle);
}

/// <summary>
/// Job HOSTPROBE: keeps <c>ProcessHost.IsHostingPrivateGame</c> honest without enumerating
/// processes on the main thread. The old probe ran <c>Process.GetProcesses()</c> plus
/// <c>MainModule</c>, <c>OpenProcess</c> and a command-line read per process once a second on
/// the frame ticker, and cost the hosting client 80–90 ms frames.
///
/// <para>Steady state: once the child is identified (path + argv, never by name; the live
/// dedicated root is always refused) Bootstrap holds an open handle to it and each
/// <see cref="Tick"/> is one non-blocking <see cref="IHostedChildOs.Poll"/>. Holding the handle
/// is also the PID-reuse guard: the kernel cannot hand that process object's PID to another
/// process while the handle is open, and the poll asks the object, not the PID.</para>
///
/// <para>Identification (only while no child is known) runs on a background thread through
/// <c>runInBackground</c>, at most one in flight, and is published to the main thread through a
/// volatile field. It first tries the PID the spawn site reported, then a name-pre-filtered
/// scan. Fail closed: a failed scan, or one that could not read every candidate, keeps the flag;
/// a completed scan that read every candidate and found no match, a completed scan that no longer
/// lists the PID the spawn site reported, or the known handle signaling exit, clears it.</para>
/// </summary>
internal sealed class HostedChildTracker
{
    /// <summary>Default process-name pre-filter. The match itself is path + argv.</summary>
    public const string DefaultServerProcessName = "VRisingServer";

    private readonly IHostedChildOs _os;
    private readonly Action<Action> _runInBackground;
    private readonly int _parentPid;
    private readonly object _gate = new();

    // Main-thread state, under _gate (NoteSpawn may come from the spawn hook's thread).
    private bool _hosting;
    private string? _spawnedPath;
    private int _hintPid;

    // The PID the spawn site reported for this generation. Unlike _hintPid it is kept after adoption: a later scan that no
    // longer lists it knows the reported child is gone, whatever other candidates it could not read (review item b).
    private int _spawnPid;
    private IntPtr _known;
    private int _knownPid;
    private long _generation;

    // Background hand-off.
    private int _inFlight;
    private ScanResult? _published;

    private int _loggedScanFailed;
    private int _loggedQueryFailed;

    public HostedChildTracker(IHostedChildOs os, Action<Action> runInBackground, int parentPid)
    {
        _os = os ?? throw new ArgumentNullException(nameof(os));
        _runInBackground = runInBackground ?? throw new ArgumentNullException(nameof(runInBackground));
        _parentPid = parentPid;
    }

    /// <summary>Called once per log key when a scan fails closed. Never throws back into the tracker.</summary>
    public Action<string>? Warn { get; set; }

    public bool Hosting
    {
        get { lock (_gate) return _hosting; }
    }

    /// <summary>PID of the identified child, or 0 while it is not known yet.</summary>
    public int KnownPid
    {
        get { lock (_gate) return _knownPid; }
    }

    public bool ScanInFlight => Volatile.Read(ref _inFlight) != 0;

    /// <summary>A hosted spawn was seen. Starts a new generation: any older child or scan is dropped.</summary>
    public void NoteSpawn(string? spawnedPath)
    {
        lock (_gate)
        {
            _generation++;
            DropKnown();
            _hosting = true;
            _hintPid = 0;
            _spawnPid = 0;
            _spawnedPath = !string.IsNullOrEmpty(spawnedPath) && !CoreWatchPolicy.IsForbiddenLiveServerPath(spawnedPath)
                ? spawnedPath
                : null;
        }
    }

    /// <summary>
    /// The spawn site reported the child's PID. Only a hint: the background resolve still checks
    /// path + argv on the handle it opens before adopting it.
    /// </summary>
    public void NoteSpawnPid(int pid)
    {
        if (pid <= 0) return;
        lock (_gate)
        {
            if (!_hosting) return;
            _spawnPid = pid;
            if (_known == IntPtr.Zero) _hintPid = pid;
        }
    }

    /// <summary>Drop the flag (dedicated join). Returns true when it was set.</summary>
    public bool Clear()
    {
        lock (_gate) return ClearLocked();
    }

    /// <summary>
    /// True while a background resolve's result waits to be taken. <c>ProcessHost</c> ticks a tracker that is not hosting
    /// while this is true, so a result published after <see cref="Clear"/> releases its handle on the next tick instead of
    /// holding it until the next hosting session (review item a).
    /// </summary>
    public bool HasPublished => Volatile.Read(ref _published) != null;

    /// <summary>
    /// Main-thread tick. Never enumerates processes and never opens one: at most one non-blocking
    /// poll of the held handle, and at most one background resolve queued. Returns true when this
    /// tick cleared the hosting flag (the child exited). Not hosting, it only releases a result
    /// published after the flag was cleared.
    /// </summary>
    public bool Tick()
    {
        bool startResolve = false;
        long generation;
        int hint;
        int spawnPid;
        string? spawnedPath;
        lock (_gate)
        {
            if (!_hosting)
            {
                DiscardPublished();
                return false;
            }

            ScanResult? result = Interlocked.Exchange(ref _published, null);
            if (result != null)
            {
                if (result.Generation != _generation)
                {
                    if (result.Handle != IntPtr.Zero) SafeClose(result.Handle);
                }
                else if (result.Handle != IntPtr.Zero)
                {
                    DropKnown();
                    _known = result.Handle;
                    _knownPid = result.Pid;
                    _hintPid = 0;
                }
                else
                {
                    if (result.QueryFailed) WarnOnce(ref _loggedQueryFailed, CoreWatchPolicy.FormatHostedQueryFailed());
                    bool alive = CoreWatchPolicy.ChildStillRunningAfterScan(
                        result.ScanFailed, result.Candidates, result.Queried, foundLive: false,
                        result.QueryFailed, result.SpawnPid, result.SpawnPidListed);
                    if (!alive) return ClearLocked();
                    WarnOnce(ref _loggedScanFailed, CoreWatchPolicy.FormatHostedScanFailed());
                }
            }

            if (_known != IntPtr.Zero)
            {
                HostedChildLiveness live;
                try { live = _os.Poll(_known); }
                catch { live = HostedChildLiveness.Unknown; }
                switch (live)
                {
                    case HostedChildLiveness.Running:
                        return false;
                    case HostedChildLiveness.Exited:
                        return ClearLocked();
                    default:
                        // Cannot tell: keep the flag, forget the handle, re-identify off thread.
                        DropKnown();
                        break;
                }
            }

            if (Interlocked.CompareExchange(ref _inFlight, 1, 0) == 0)
            {
                startResolve = true;
            }
            generation = _generation;
            hint = _hintPid;
            spawnPid = _spawnPid;
            spawnedPath = _spawnedPath;
        }

        if (startResolve)
        {
            try
            {
                _runInBackground(() => RunResolve(generation, hint, spawnedPath, spawnPid));
            }
            catch
            {
                Volatile.Write(ref _inFlight, 0);
            }
        }
        return false;
    }

    private void RunResolve(long generation, int hint, string? spawnedPath, int spawnPid)
    {
        try
        {
            ScanResult result = Resolve(generation, hint, spawnedPath, spawnPid);
            ScanResult? stale = Interlocked.Exchange(ref _published, result);
            if (stale?.Handle is { } h && h != IntPtr.Zero) SafeClose(h);
        }
        catch
        {
            Interlocked.Exchange(ref _published, new ScanResult { Generation = generation, ScanFailed = true });
        }
        finally
        {
            Volatile.Write(ref _inFlight, 0);
        }
    }

    /// <summary>Background only. Hint first, then the name-pre-filtered scan.</summary>
    internal ScanResult Resolve(long generation, int hint, string? spawnedPath, int spawnPid = 0)
    {
        var result = new ScanResult { Generation = generation, SpawnPid = spawnPid > 0 && spawnPid != _parentPid ? spawnPid : 0 };
        // A hint that cannot be opened (already gone) or no longer matches (PID reused) is not
        // a query failure of the scan; it just falls through to the scan.
        bool hintUnread = false;
        if (hint > 0 && hint != _parentPid
            && TryMatch(hint, spawnedPath, out IntPtr hinted, ref hintUnread))
        {
            result.Handle = hinted;
            result.Pid = hint;
            return result;
        }

        int[]? pids;
        try { pids = _os.CandidatePids(CandidateNames(spawnedPath)); }
        catch { pids = null; }
        if (pids == null)
        {
            result.ScanFailed = true;
            return result;
        }

        for (int i = 0; i < pids.Length; i++)
        {
            int pid = pids[i];
            if (pid == _parentPid) continue;
            if (pid == result.SpawnPid) result.SpawnPidListed = true;
            result.Candidates++;
            bool queryFailed = false;
            bool matched = TryMatch(pid, spawnedPath, out IntPtr handle, ref queryFailed, ref result.Queried);
            if (queryFailed) result.QueryFailed = true;
            if (!matched) continue;
            result.Handle = handle;
            result.Pid = pid;
            return result;
        }
        return result;
    }

    private bool TryMatch(int pid, string? spawnedPath, out IntPtr handle, ref bool queryFailed)
    {
        int ignored = 0;
        return TryMatch(pid, spawnedPath, out handle, ref queryFailed, ref ignored);
    }

    private bool TryMatch(int pid, string? spawnedPath, out IntPtr handle, ref bool queryFailed, ref int queried)
    {
        handle = IntPtr.Zero;
        IntPtr opened;
        string? path;
        string? cmd;
        bool ok;
        try { ok = _os.TryOpen(pid, out opened, out path, out cmd); }
        catch { ok = false; opened = IntPtr.Zero; path = null; cmd = null; }
        if (!ok || (path == null && cmd == null))
        {
            if (opened != IntPtr.Zero) SafeClose(opened);
            queryFailed = true;
            return false;
        }
        queried++;
        if (CoreWatchPolicy.IsLiveHostedChild(path, cmd, _parentPid, spawnedPath))
        {
            handle = opened;
            return true;
        }
        SafeClose(opened);
        return false;
    }

    /// <summary>Process names the scan pre-filters on: the spawned image's name, and the stock server name.</summary>
    internal static IReadOnlyList<string> CandidateNames(string? spawnedPath)
    {
        var names = new List<string>(2) { DefaultServerProcessName };
        if (!string.IsNullOrEmpty(spawnedPath))
        {
            try
            {
                string name = Path.GetFileNameWithoutExtension(spawnedPath);
                if (!string.IsNullOrEmpty(name)
                    && !string.Equals(name, DefaultServerProcessName, StringComparison.OrdinalIgnoreCase))
                    names.Add(name);
            }
            catch
            {
                /* keep the default name */
            }
        }
        return names;
    }

    private bool ClearLocked()
    {
        bool was = _hosting;
        _generation++;
        _hosting = false;
        _spawnedPath = null;
        _hintPid = 0;
        _spawnPid = 0;
        DropKnown();
        DiscardPublished();
        return was;
    }

    private void DropKnown()
    {
        if (_known != IntPtr.Zero) SafeClose(_known);
        _known = IntPtr.Zero;
        _knownPid = 0;
    }

    private void DiscardPublished()
    {
        ScanResult? stale = Interlocked.Exchange(ref _published, null);
        if (stale != null && stale.Handle != IntPtr.Zero) SafeClose(stale.Handle);
    }

    private void SafeClose(IntPtr handle)
    {
        try { _os.Close(handle); }
        catch { /* best effort */ }
    }

    private void WarnOnce(ref int latch, string line)
    {
        if (Interlocked.Exchange(ref latch, 1) != 0) return;
        try { Warn?.Invoke(line); }
        catch { /* probe must not throw */ }
    }

    internal sealed class ScanResult
    {
        public long Generation;
        public bool ScanFailed;
        public bool QueryFailed;
        public int Candidates;
        public int Queried;

        /// <summary>The PID the spawn site reported (0 when none), and whether this scan's candidates still list it.</summary>
        public int SpawnPid;
        public bool SpawnPidListed;
        public IntPtr Handle;
        public int Pid;
    }
}
