using System;
using System.Threading.Tasks;

namespace Progenitor.Install;

/// <summary>
/// When this process reads Progenitor's release list (<see cref="SelfFeed"/>, <see cref="SelfFeedCadence"/>): a client at the
/// first start of a session when its copy is older than 6 hours, then whenever the mod index was read (the Install tab, the
/// join screens), at most every 10 minutes; a server on its <c>[AutoUpdate] CheckHours</c> cadence once its world loaded.
/// Each read is one conditional request (<see cref="CheckIndex"/>); detection then runs on the cached copy.
/// </summary>
internal static partial class SelfUpdates
{
    /// <summary>When the cached release list was last fetched or confirmed current, or null.</summary>
    internal static Func<DateTime?>? FeedStampProbe { get; set; }

    /// <summary>A client's mod index stamp (it moves when the Install tab or the join screens read the index); empty on a server.</summary>
    internal static Func<string>? IndexReadProbe { get; set; }

    /// <summary>A server's <c>[AutoUpdate] CheckHours</c>.</summary>
    internal static Func<int> CheckHoursProbe { get; set; } = () => ServerUpdateSettings.DefaultCheckHours;

    private static DateTime? _lastFeedCheck;
    private static string? _lastIndexRead;
    private static bool _feedChecking;

    /// <summary>Main thread, from <see cref="Tick"/>: start one release-list request when one is due. Returns why, or empty.</summary>
    internal static string FeedTick()
    {
        if (CheckIndex == null) return "";
        bool server = IsServer();
        if (server)
        {
            bool world;
            try { world = WorldReady(); }
            catch { world = false; }
            if (!world) return "";
        }

        DateTime now = SafeUtcNow();
        DateTime? stamp;
        try { stamp = FeedStampProbe?.Invoke(); }
        catch { stamp = null; }
        string index = "";
        if (!server)
        {
            try { index = IndexReadProbe?.Invoke() ?? ""; }
            catch { index = ""; }
        }

        int hours;
        try { hours = CheckHoursProbe(); }
        catch { hours = ServerUpdateSettings.DefaultCheckHours; }
        string due;
        lock (Gate)
        {
            if (_feedChecking) return "";
            if (_lastIndexRead == null) _lastIndexRead = index;
            bool indexRead = !string.Equals(index, _lastIndexRead, StringComparison.Ordinal);
            due = SelfFeedCadence.Due(server, now, _lastFeedCheck, stamp, indexRead, hours);
            if (due.Length == 0) return "";
            _feedChecking = true;
            _lastFeedCheck = now;
            _lastIndexRead = index;
        }

        Write("op=feed due=" + due);
        Task<string> t;
        try { t = CheckNow(); }
        catch (Exception e) { t = Task.FromResult("failed: " + e.Message); }
        t.ContinueWith(_ =>
        {
            lock (Gate) _feedChecking = false;
        }, System.Threading.CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return due;
    }

    private static void ResetFeed()
    {
        _lastFeedCheck = null;
        _lastIndexRead = null;
        _feedChecking = false;
        FeedStampProbe = null;
        IndexReadProbe = null;
        CheckHoursProbe = () => ServerUpdateSettings.DefaultCheckHours;
    }
}
