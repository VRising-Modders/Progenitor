using System;
using System.Collections.Generic;

namespace Progenitor.Servants;

/// <summary>
/// The registrations behind <c>ctx.Servants.SuppressGameNotices</c>: every plugin's filter, combined
/// with OR. Game-free (the user type is a parameter) so the decision is unit-tested; the server send
/// hook and Progenitor's own "hunt started" notice ask it once per recipient.
/// </summary>
internal sealed class ServantNoticeFilters<TUser>
{
    private readonly object _gate = new();
    private readonly List<Entry> _entries = new();
    private Entry[] _snapshot = Array.Empty<Entry>();
    private long _token;
    private long _suppressed;
    private long _failures;

    private sealed class Entry
    {
        public long Token;
        public string Owner = "";
        public Func<TUser, ServantNoticeKind, bool> Suppress = null!;
        public bool FailureLogged;
    }

    /// <summary>Lines the registry wants logged (registration, a filter that threw).</summary>
    internal Action<string>? Log { get; set; }

    /// <summary>True when at least one filter is registered (the send hook has work).</summary>
    internal bool Any => System.Threading.Volatile.Read(ref _snapshot).Length > 0;

    /// <summary>How many filters are registered.</summary>
    internal int Count => System.Threading.Volatile.Read(ref _snapshot).Length;

    /// <summary>Notices dropped since Core installed.</summary>
    internal long Suppressed { get { lock (_gate) return _suppressed; } }

    /// <summary>Filter calls that threw (each is treated as "keep the line").</summary>
    internal long Failures { get { lock (_gate) return _failures; } }

    /// <summary>
    /// Add <paramref name="suppress"/> for <paramref name="owner"/>. Several filters, from one plugin or
    /// many, may be registered at once. The handle removes exactly this registration; disposing it
    /// twice is harmless.
    /// </summary>
    internal IDisposable Register(Func<TUser, ServantNoticeKind, bool> suppress, string owner)
    {
        if (suppress == null) throw new ArgumentNullException(nameof(suppress));
        if (string.IsNullOrWhiteSpace(owner)) throw new ArgumentException("owner is required", nameof(owner));
        long token;
        lock (_gate)
        {
            token = ++_token;
            _entries.Add(new Entry { Token = token, Owner = owner, Suppress = suppress });
            Publish_NoLock();
        }
        Log?.Invoke("servants: game notice filter registered owner=" + owner);
        return new Handle(this, token);
    }

    /// <summary>
    /// True when any filter asks to drop this notice for <paramref name="user"/>. Filters run in
    /// registration order and the first true wins. A filter that throws counts as false (the line is
    /// kept), is logged once per registration, and the next filter is still asked.
    /// </summary>
    internal bool ShouldSuppress(TUser user, ServantNoticeKind kind)
    {
        var entries = System.Threading.Volatile.Read(ref _snapshot);
        foreach (var e in entries)
        {
            bool drop;
            try { drop = e.Suppress(user, kind); }
            catch (Exception ex)
            {
                bool first;
                lock (_gate)
                {
                    _failures++;
                    first = !e.FailureLogged;
                    e.FailureLogged = true;
                }
                if (first) Log?.Invoke("servants: game notice filter of '" + e.Owner + "' threw; the line is kept: " + ex.Message);
                continue;
            }
            if (!drop) continue;
            lock (_gate) _suppressed++;
            return true;
        }
        return false;
    }

    /// <summary>The kind of a finished-hunt notice: the game prints the success line or the killed line.</summary>
    internal static ServantNoticeKind FinishedKind(bool success) => success ? ServantNoticeKind.Returned : ServantNoticeKind.Killed;

    /// <summary>Drop every registration (Core uninstall).</summary>
    internal void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            Publish_NoLock();
        }
    }

    private void Unregister(long token)
    {
        string? owner = null;
        lock (_gate)
        {
            for (int i = 0; i < _entries.Count; i++)
            {
                if (_entries[i].Token != token) continue;
                owner = _entries[i].Owner;
                _entries.RemoveAt(i);
                Publish_NoLock();
                break;
            }
        }
        if (owner != null) Log?.Invoke("servants: game notice filter unregistered owner=" + owner);
    }

    private void Publish_NoLock() => System.Threading.Volatile.Write(ref _snapshot, _entries.ToArray());

    private sealed class Handle : IDisposable
    {
        private ServantNoticeFilters<TUser>? _owner;
        private readonly long _token;

        internal Handle(ServantNoticeFilters<TUser> owner, long token)
        {
            _owner = owner;
            _token = token;
        }

        public void Dispose()
        {
            var o = System.Threading.Interlocked.Exchange(ref _owner, null);
            o?.Unregister(_token);
        }
    }
}
