using System;
using System.Collections.Generic;
using Progenitor.Castles;

namespace Progenitor.Servants;

/// <summary>
/// The one-provider registry behind <c>ctx.Servants.ListProvider</c>, plus the per-player record
/// of the list each player was last shown (so a throne send naming those servants can be matched
/// back to them). Game-free: the server hooks read it.
/// </summary>
internal sealed class ServantListRegistry
{
    /// <summary>How long a shown list stays valid for routing a throne send.</summary>
    internal static readonly TimeSpan ShownTtl = TimeSpan.FromMinutes(10);

    private readonly object _gate = new();
    private IServantListProvider? _provider;
    private string _owner = "";
    private long _token;
    private long _swaps;
    private long _routed;
    private long _refused;
    private readonly Dictionary<int, Shown> _shown = new();

    /// <summary>One player's swapped list: the throne's NetworkId, and each shown servant with its NetworkId and heart.</summary>
    internal sealed class Shown
    {
        public byte[] Throne = new byte[12];
        public CastleId User;
        public CastleId Heart;
        public DateTime AtUtc;
        public readonly List<(CastleId Servant, byte[] NetworkId, CastleId Heart)> Servants = new();
    }

    /// <summary>Lines the registry wants logged.</summary>
    internal Action<string>? Log { get; set; }

    internal IServantListProvider? Provider { get { lock (_gate) return _provider; } }

    internal string Owner { get { lock (_gate) return _owner; } }

    internal bool Registered { get { lock (_gate) return _provider != null; } }

    /// <summary>
    /// Register <paramref name="provider"/> for <paramref name="owner"/>. Throws
    /// <see cref="InvalidOperationException"/> naming the current owner when one is registered.
    /// The handle unregisters; a stale handle never removes a later registration.
    /// </summary>
    internal IDisposable Register(IServantListProvider provider, string owner)
    {
        if (provider == null) throw new ArgumentNullException(nameof(provider));
        if (string.IsNullOrWhiteSpace(owner)) throw new ArgumentException("owner is required", nameof(owner));
        long token;
        lock (_gate)
        {
            if (_provider != null)
                throw new InvalidOperationException("ctx.Servants.ListProvider: a servant list provider is already registered by '" + _owner
                    + "'. Only one provider may be registered at a time; '" + owner + "' was refused.");
            _provider = provider;
            _owner = owner;
            token = ++_token;
            _swaps = _routed = _refused = 0;
            _shown.Clear();
        }
        Log?.Invoke("servants: list provider registered owner=" + owner + " (experimental)");
        return new Handle(this, token);
    }

    private void Unregister(long token)
    {
        string owner;
        lock (_gate)
        {
            if (_provider == null || _token != token) return;
            owner = _owner;
            _provider = null;
            _owner = "";
            _shown.Clear();
        }
        Log?.Invoke("servants: list provider unregistered owner=" + owner + " (thrones list their own castle again)");
    }

    /// <summary>Drop the provider when <paramref name="owner"/> owns it.</summary>
    internal bool RemoveOwner(string owner)
    {
        long token;
        lock (_gate)
        {
            if (_provider == null || !string.Equals(_owner, owner, StringComparison.Ordinal)) return false;
            token = _token;
        }
        Unregister(token);
        return true;
    }

    /// <summary>Drop any registration (Core uninstall).</summary>
    internal void Clear()
    {
        long token;
        lock (_gate) token = _token;
        Unregister(token);
    }

    /// <summary>Record the list shown to the player with <paramref name="userIndex"/>.</summary>
    internal void RecordShown(int userIndex, Shown shown)
    {
        lock (_gate)
        {
            if (_provider == null) return;
            _shown[userIndex] = shown;
            _swaps++;
        }
    }

    /// <summary>The list last shown to <paramref name="userIndex"/>, when it is younger than <see cref="ShownTtl"/>.</summary>
    internal Shown? ShownTo(int userIndex, DateTime nowUtc)
    {
        lock (_gate)
        {
            if (!_shown.TryGetValue(userIndex, out var s)) return null;
            if (nowUtc - s.AtUtc > ShownTtl)
            {
                _shown.Remove(userIndex);
                return null;
            }
            return s;
        }
    }

    /// <summary>True when any player has a live swapped list (the send hook has work).</summary>
    internal bool AnyShown { get { lock (_gate) return _provider != null && _shown.Count > 0; } }

    /// <summary>Forget every shown list (world reload).</summary>
    internal void ForgetShown() { lock (_gate) _shown.Clear(); }

    internal void CountRouted(bool ok) { lock (_gate) { if (ok) _routed++; else _refused++; } }

    internal ServantListStatus Status()
    {
        lock (_gate)
            return new ServantListStatus
            {
                Registered = _provider != null,
                Owner = _owner,
                Swaps = _swaps,
                RoutedSends = _routed,
                RoutedRefusals = _refused,
            };
    }

    private sealed class Handle : IDisposable
    {
        private ServantListRegistry? _registry;
        private readonly long _token;

        internal Handle(ServantListRegistry registry, long token)
        {
            _registry = registry;
            _token = token;
        }

        public void Dispose()
        {
            var r = System.Threading.Interlocked.Exchange(ref _registry, null);
            r?.Unregister(_token);
        }
    }
}
