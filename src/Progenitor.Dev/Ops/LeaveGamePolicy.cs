using System;

namespace Progenitor.Dev.Ops;

// Serialized by the bridge's game-thread dispatcher. Does not hold a world or game callback.
internal sealed class LeaveGamePolicy
{
    private bool _requested;
    private ulong _epoch;
    private double _started;
    private string? _failure;
    internal double Elapsed { get; private set; }
    internal bool Requested => _requested;
    internal string? Failure => _failure;

    internal string Observe(bool hasWorld, ulong epoch, bool menuVerified,
        bool statusOnly, bool launchInFlight, double now)
    {
        Elapsed = _requested ? Math.Max(0, now - _started) : 0;
        if (menuVerified)
        { _requested = false; _failure = null; return "verified"; }
        if (_requested)
        {
            if (hasWorld && epoch != _epoch)
            { _requested = false; _failure = null; return "superseded"; }
            return _failure != null ? "requestFailed" : Elapsed >= 120 ? "timedOut" : "pending";
        }
        if (statusOnly || !hasWorld || launchInFlight)
            return launchInFlight ? "launchInFlight" : hasWorld ? "inGame" : "notVerified";
        _requested = true;
        _epoch = epoch;
        _started = now;
        _failure = null;
        return "request"; // Reservation precedes the potentially partial native transition.
    }

    internal void Fail(string failure) => _failure = failure;
}
