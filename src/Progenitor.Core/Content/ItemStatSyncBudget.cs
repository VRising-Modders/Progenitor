using System;

namespace Progenitor.Content;

/// <summary>Bounds receive work in a one-second window without exhausting a long-lived join.</summary>
internal sealed class ItemStatSyncBudget
{
    internal const int DefaultMaxPacketsPerWindow = 1024;
    internal const long WindowMilliseconds = 1000;
    private readonly Func<long> _now;
    private long _windowStart;
    private int _accepted;
    private bool _logged;

    internal ItemStatSyncBudget(Func<long>? now = null)
    {
        _now = now ?? (() => Environment.TickCount64);
        Reset();
    }

    internal bool TryAdmit(out bool reportCap)
    {
        long now = _now();
        if (now - _windowStart >= WindowMilliseconds)
        {
            _windowStart = now;
            _accepted = 0;
            _logged = false;
        }
        reportCap = false;
        if (_accepted >= DefaultMaxPacketsPerWindow)
        {
            reportCap = !_logged;
            _logged = true;
            return false;
        }
        _accepted++;
        return true;
    }

    internal void Reset()
    {
        _windowStart = _now();
        _accepted = 0;
        _logged = false;
    }
}
