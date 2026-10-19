using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

// SUPPORT1 (2026-10-10): per-session copies of the BepInEx log. BepInEx rewrites LogOutput.log at
// every start, so after "the game crashed, I restarted it, now I collect logs" the crashed session's
// log is gone. The preloader patcher keeps its own copy of every session under
// BepInEx/progenitor-logs: session.log (this start), session-1.log and session-2.log (the two before).
// This file is the whole file policy. It is compiled into the preloader patcher (which writes),
// Core (whose support bundle reads the same names) and the game-free tests. It references nothing
// but the base class library.
namespace Progenitor.SessionLogs;

/// <summary>Names, rotation and opening of the per-session log files. Game-free.</summary>
internal static class SessionLogFiles
{
    /// <summary>The folder under the BepInEx root.</summary>
    internal const string FolderName = "progenitor-logs";

    /// <summary>This start's log.</summary>
    internal const string CurrentName = "session.log";

    /// <summary>How many earlier sessions are kept (<c>session-1.log</c> is the newest of them).</summary>
    internal const int KeepPrevious = 2;

    /// <summary>Prefix of the file a second process of the same install writes while the first holds <see cref="CurrentName"/>.</summary>
    internal const string PidPrefix = "session-pid";

    /// <summary>How many <c>session-pid*.log</c> files are kept.</summary>
    internal const int KeepPidFiles = 3;

    /// <summary>The largest a session log grows; past it one "truncated" line is written and nothing more.</summary>
    internal const long DefaultMaxBytes = 50L * 1024 * 1024;

    internal static string Folder(string bepInExRoot) => Path.Combine(bepInExRoot, FolderName);

    /// <summary><c>session-<paramref name="n"/>.log</c>; 0 is <see cref="CurrentName"/>.</summary>
    internal static string Previous(int n) =>
        n <= 0 ? CurrentName : "session-" + n.ToString(CultureInfo.InvariantCulture) + ".log";

    internal static string PidName(int pid) => PidPrefix + pid.ToString(CultureInfo.InvariantCulture) + ".log";

    /// <summary>True for a file name this folder's policy writes (what the support bundle collects).</summary>
    internal static bool IsSessionLogName(string? name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        return name!.StartsWith("session", StringComparison.OrdinalIgnoreCase)
            && name.EndsWith(".log", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Rotate and open this session's log. When <see cref="CurrentName"/> is free: <c>session-1.log</c> moves to
    /// <c>session-2.log</c> (the old <c>session-2.log</c> is deleted), <c>session.log</c> to <c>session-1.log</c>, and a new
    /// <c>session.log</c> is opened. When another process of the same install has <c>session.log</c> open, nothing is
    /// rotated and <c>session-pid&lt;pid&gt;.log</c> is opened instead; so is it when the rotation could not move <c>session.log</c> away (the oldest beyond <see cref="KeepPidFiles"/> are
    /// deleted). Returns null, with the reason in <paramref name="error"/>, when nothing could be opened. Never throws.
    /// </summary>
    internal static FileStream? Open(string bepInExRoot, int pid, out string path, out string error)
    {
        path = "";
        error = "";
        try
        {
            string folder = Folder(bepInExRoot);
            Directory.CreateDirectory(folder);
            string current = Path.Combine(folder, CurrentName);

            if (!IsHeldByAnother(current))
            {
                Rotate(folder);
                // A rotation that could not move session.log away (a viewer or another tool holds an older file) must not
                // overwrite the previous session: then this start writes its own pid file instead. CreateNew also loses a race
                // with another process creating session.log at the same moment rather than truncating its file.
                var stream = File.Exists(current) ? null : TryCreate(current, FileMode.CreateNew, out error);
                if (stream != null)
                {
                    path = current;
                    return stream;
                }
            }

            string own = Path.Combine(folder, PidName(pid));
            var fallback = TryCreate(own, FileMode.Create, out error);
            if (fallback != null)
            {
                path = own;
                PrunePidFiles(folder, own);
            }
            return fallback;
        }
        catch (Exception e)
        {
            error = e.GetType().Name + ": " + e.Message;
            return null;
        }
    }

    /// <summary>True when <paramref name="file"/> exists and another process holds it open (it cannot be opened exclusively).</summary>
    internal static bool IsHeldByAnother(string file)
    {
        if (!File.Exists(file)) return false;
        try
        {
            using var probe = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
    }

    /// <summary>session-1 → session-2, session → session-1. A step that fails is skipped; the next start tries again.</summary>
    internal static void Rotate(string folder)
    {
        for (int n = KeepPrevious; n >= 1; n--)
        {
            string to = Path.Combine(folder, Previous(n));
            string from = Path.Combine(folder, Previous(n - 1));
            if (!File.Exists(from)) continue;
            try
            {
                if (File.Exists(to)) File.Delete(to);
                File.Move(from, to);
            }
            catch { /* a held or read-only file stays; the new session still opens */ }
        }
    }

    /// <summary>Delete the oldest <c>session-pid*.log</c> files beyond <see cref="KeepPidFiles"/>, never <paramref name="keep"/>.</summary>
    internal static void PrunePidFiles(string folder, string keep)
    {
        try
        {
            var files = new List<FileInfo>();
            foreach (var f in new DirectoryInfo(folder).GetFiles(PidPrefix + "*.log")) files.Add(f);
            files.Sort((a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));
            string own = Path.GetFullPath(keep);
            int kept = 1; // the file this process writes always stays
            foreach (var f in files)
            {
                if (string.Equals(f.FullName, own, StringComparison.OrdinalIgnoreCase)) continue;
                if (kept < KeepPidFiles) { kept++; continue; }
                try { f.Delete(); }
                catch { /* held by its process */ }
            }
        }
        catch { /* best effort */ }
    }

    private static FileStream? TryCreate(string file, FileMode mode, out string error)
    {
        error = "";
        try
        {
            // FileShare.Read: the support bundle (and any viewer) can read it while this process writes, but a second
            // process cannot rename or truncate it.
            return new FileStream(file, mode, FileAccess.Write, FileShare.Read);
        }
        catch (Exception e)
        {
            error = e.GetType().Name + ": " + e.Message;
            return null;
        }
    }

    /// <summary>The first line of a session log: <c>Progenitor session log · started 2026-10-10T14:22:33Z · VRising (pid 1234)</c>.</summary>
    internal static string Header(DateTime utc, string processName, int pid) =>
        "Progenitor session log · started "
        + utc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)
        + " · " + (string.IsNullOrWhiteSpace(processName) ? "unknown process" : processName.Trim())
        + " (pid " + pid.ToString(CultureInfo.InvariantCulture) + ")";

    /// <summary>The last line written once the size cap is reached.</summary>
    internal static string TruncatedLine(long maxBytes) =>
        "[truncated: this session log reached " + (maxBytes / (1024 * 1024)).ToString(CultureInfo.InvariantCulture)
        + " MB; later lines are only in BepInEx/LogOutput.log]";
}

/// <summary>
/// A size-capped, thread-safe line writer over one session log. The thread that logs only appends the line to a
/// pending list; the timer thread writes and flushes that list every second, outside the lock the logging thread
/// takes, so a slow disk never stalls the game's frame. A line written with <c>flushNow</c> (errors) is written and
/// flushed on the spot when no other write is in progress, so a crash right after an error keeps it; when the timer is
/// mid-write, the line is handed to it to write at once instead of waiting behind it. The timer is one-shot and
/// re-armed after each run, and a reentrancy guard folds overlapping requests into the run in progress. Game-free.
/// </summary>
internal sealed class SessionLogWriter : IDisposable
{
    private static readonly Encoding Utf8 = new UTF8Encoding(false);

    private readonly object _gate = new(); // _pending and the counters: held only for a list append
    private readonly object _io = new();   // the stream: held while writing to disk
    private readonly Stream _stream;
    private readonly StreamWriter _writer;
    private readonly long _maxBytes;
    private readonly Timer? _flush;
    private List<string> _pending = new();
    private long _written;
    private bool _capped;
    private bool _disposed;
    private int _timerRunning; // 1 while OnTimer runs
    private int _timerAgain;   // a request arrived while it ran: run once more

    internal SessionLogWriter(Stream stream, long maxBytes, bool timedFlush = true)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _writer = new StreamWriter(stream, Utf8) { AutoFlush = false };
        _maxBytes = maxBytes <= 0 ? SessionLogFiles.DefaultMaxBytes : maxBytes;
        if (timedFlush) _flush = new Timer(_ => OnTimer(), null, 1000, Timeout.Infinite);
    }

    /// <summary>Bytes accepted so far (UTF-8, newlines included).</summary>
    internal long Written
    {
        get { lock (_gate) return _written; }
    }

    /// <summary>True once the cap was reached; later lines are dropped.</summary>
    internal bool Capped
    {
        get { lock (_gate) return _capped; }
    }

    /// <summary>Lines accepted and not yet written to the stream.</summary>
    internal int PendingLines
    {
        get { lock (_gate) return _pending.Count; }
    }

    internal void WriteLine(string? line, bool flushNow = false)
    {
        bool urgent = flushNow;
        lock (_gate)
        {
            if (_disposed || _capped) return;
            string text = line ?? "";
            long bytes = Utf8.GetByteCount(text) + Environment.NewLine.Length;
            if (_written + bytes > _maxBytes)
            {
                _capped = true;
                _pending.Add(SessionLogFiles.TruncatedLine(_maxBytes));
                urgent = true;
            }
            else
            {
                _pending.Add(text);
                _written += bytes;
            }
        }
        if (urgent) RequestFlush();
    }

    /// <summary>
    /// Have the pending lines written now: here when nothing else is writing (the urgent line is on disk when this
    /// returns), else by the timer as soon as its current write ends. Without a timer, here in any case.
    /// </summary>
    private void RequestFlush()
    {
        if (_flush == null)
        {
            Flush();
            return;
        }
        if (Monitor.TryEnter(_io))
        {
            try { FlushLocked(); }
            finally { Monitor.Exit(_io); }
            return;
        }
        Schedule(0);
    }

    private void Schedule(int dueMs)
    {
        try { _flush?.Change(dueMs, Timeout.Infinite); }
        catch { /* disposed */ }
    }

    /// <summary>The timer's callback: never two at once; a request during a run makes it run again; then re-arm.</summary>
    private void OnTimer()
    {
        if (Interlocked.Exchange(ref _timerRunning, 1) == 1)
        {
            Interlocked.Exchange(ref _timerAgain, 1);
            return;
        }
        try
        {
            do { Flush(); }
            while (Interlocked.Exchange(ref _timerAgain, 0) == 1);
        }
        finally
        {
            Interlocked.Exchange(ref _timerRunning, 0);
            bool disposed;
            lock (_gate) disposed = _disposed;
            if (!disposed) Schedule(1000);
        }
    }

    /// <summary>Write and flush every pending line. Called by the timer, at exit and by tests.</summary>
    internal void Flush()
    {
        lock (_io) FlushLocked();
    }

    /// <summary>Caller holds <see cref="_io"/>.</summary>
    private void FlushLocked()
    {
        List<string> batch;
        lock (_gate)
        {
            if (_disposed || _pending.Count == 0) return;
            batch = _pending;
            _pending = new List<string>();
        }
        WriteBatch(batch);
    }

    /// <summary>Caller holds <see cref="_io"/>.</summary>
    private void WriteBatch(List<string> batch)
    {
        try
        {
            foreach (string text in batch) _writer.WriteLine(text);
            _writer.Flush();
        }
        catch { /* a full disk or a closed stream must not reach the game */ }
    }

    public void Dispose()
    {
        _flush?.Dispose();
        lock (_io)
        {
            List<string> batch;
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                batch = _pending;
                _pending = new List<string>();
            }
            WriteBatch(batch);
            try { _writer.Dispose(); }
            catch { /* best effort */ }
            try { _stream.Dispose(); }
            catch { /* best effort */ }
        }
    }
}
