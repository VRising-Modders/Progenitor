using System;
using System.Collections.Generic;
using System.Text;
using Progenitor.Castles;

namespace Progenitor.Materials;

/// <summary>
/// What the material seams can actually do in this process right now. Read it instead of
/// assuming: a provider can register while no native seam is armed, and then nothing changes in
/// the game (<see cref="HooksInstalled"/> false).
/// </summary>
public sealed class MaterialSourceStatus
{
    /// <summary>True when a provider is registered.</summary>
    public bool Registered { get; }

    /// <summary>The registered provider's owner (plugin GUID), or "" when none.</summary>
    public string Owner { get; }

    /// <summary>
    /// True when the availability seam (and, on the server, the payment seam) is armed in this
    /// process: the opt-in native seams are installed by Bootstrap and Core's handlers are bound.
    /// False means registration is accepted but the game's own rules still apply everywhere.
    /// </summary>
    public bool HooksInstalled { get; }

    /// <summary>Why <see cref="HooksInstalled"/> is false ("" when true).</summary>
    public string HooksReason { get; }

    /// <summary>Operations the provider asked for.</summary>
    public IReadOnlyCollection<MaterialOperation> Requested { get; }

    /// <summary>
    /// Operations that are actually routed through the provider: requested, supported by
    /// Progenitor (<see cref="MaterialOperations.Supported"/>) and with the seams armed. Empty
    /// when <see cref="HooksInstalled"/> is false.
    /// </summary>
    public IReadOnlyCollection<MaterialOperation> Wired { get; }

    /// <summary>Registry revision: bumps on every register, unregister and invalidation.</summary>
    public long Revision { get; }

    /// <summary>Governed payments made since the provider registered (lines that touched the pool).</summary>
    public long Payments { get; }

    /// <summary>Availability views computed since the provider registered.</summary>
    public long Views { get; }

    /// <summary>
    /// Provider calls that threw since the provider registered. Each one was treated as "no remote
    /// sources" for that context or item.
    /// </summary>
    public long Faults { get; }

    /// <summary>
    /// Governed operations with a shortfall at payment since the provider registered: a cost line could
    /// not be paid in full from the pool the game's check was shown (a container emptied, removed or
    /// refusing writes between the check and the payment). The line took what was there and the game
    /// completed the action, as it does when its own removal comes back short. Expected to stay 0;
    /// each one is logged once with the reason.
    /// </summary>
    public long Shortfalls { get; }

    internal MaterialSourceStatus(bool registered, string owner, bool hooksInstalled, string hooksReason,
        IReadOnlyCollection<MaterialOperation> requested, IReadOnlyCollection<MaterialOperation> wired,
        long revision, long payments, long views, long faults = 0, long shortfalls = 0)
    {
        Faults = faults;
        Shortfalls = shortfalls;
        Registered = registered;
        Owner = owner;
        HooksInstalled = hooksInstalled;
        HooksReason = hooksReason;
        Requested = requested;
        Wired = wired;
        Revision = revision;
        Payments = payments;
        Views = views;
    }

    /// <summary>One grep-able line: <c>materials: provider=… hooks=… wired=… revision=… faults=… shortfalls=…</c>.</summary>
    public override string ToString()
    {
        var sb = new StringBuilder("materials: provider=");
        sb.Append(Registered ? Owner : "none");
        sb.Append(" hooks=").Append(HooksInstalled ? "on" : "off");
        if (!HooksInstalled && HooksReason.Length > 0) sb.Append(" (").Append(HooksReason).Append(')');
        sb.Append(" requested=").Append(Join(Requested));
        sb.Append(" wired=").Append(Join(Wired));
        sb.Append(" revision=").Append(Revision);
        sb.Append(" payments=").Append(Payments);
        sb.Append(" views=").Append(Views);
        sb.Append(" faults=").Append(Faults);
        sb.Append(" shortfalls=").Append(Shortfalls);
        return sb.ToString();
    }

    private static string Join(IReadOnlyCollection<MaterialOperation> ops)
    {
        if (ops.Count == 0) return "-";
        var parts = new List<string>(ops.Count);
        foreach (var op in ops) parts.Add(op.ToString());
        return string.Join(",", parts);
    }
}

/// <summary>
/// The one-provider registry behind <c>ctx.MaterialSource</c>. Game-free: it holds the provider,
/// its owner, the invalidation queue and the counters; the seams read it.
/// </summary>
internal sealed class MaterialSourceRegistry
{
    private readonly object _gate = new();
    private IMaterialSourceProvider? _provider;
    private string _owner = "";
    private MaterialOperation[] _requested = Array.Empty<MaterialOperation>();
    private long _token;
    private long _revision;
    private long _payments;
    private long _views;
    private long _faults;
    private long _shortfalls;
    private bool _invalidateAll;
    private readonly HashSet<CastleId> _invalidUsers = new();

    /// <summary>Set by the seam runtime: whether the seams are armed in this process, and why not.</summary>
    internal Func<(bool installed, string reason)> HookState { get; set; } = () => (false, "seams not installed");

    /// <summary>Lines the registry wants logged (the caller logs them; this class stays game-free).</summary>
    internal Action<string>? Log { get; set; }

    internal IMaterialSourceProvider? Provider { get { lock (_gate) return _provider; } }

    internal string Owner { get { lock (_gate) return _owner; } }

    internal long Revision { get { lock (_gate) return _revision; } }

    /// <summary>
    /// Register <paramref name="provider"/> for <paramref name="owner"/>. Throws
    /// <see cref="InvalidOperationException"/> naming the current owner when another provider is
    /// registered. The returned handle unregisters (idempotent; a stale handle never removes a
    /// later registration).
    /// </summary>
    internal IDisposable Register(IMaterialSourceProvider provider, string owner)
    {
        if (provider == null) throw new ArgumentNullException(nameof(provider));
        if (string.IsNullOrWhiteSpace(owner)) throw new ArgumentException("owner is required", nameof(owner));
        IReadOnlyCollection<MaterialOperation>? ops;
        try { ops = provider.Operations; }
        catch (Exception e) { throw new InvalidOperationException("ctx.MaterialSource.Register: provider.Operations threw: " + e.Message, e); }

        long token;
        MaterialOperation[] requested;
        lock (_gate)
        {
            if (_provider != null)
                throw new InvalidOperationException(
                    "ctx.MaterialSource.Register: a material source provider is already registered by '" + _owner
                    + "'. Only one provider may be registered at a time; '" + owner + "' was refused.");
            requested = Normalize(ops);
            _provider = provider;
            _owner = owner;
            _requested = requested;
            token = ++_token;
            _revision++;
            _payments = 0;
            _views = 0;
            _faults = 0;
            _shortfalls = 0;
            _invalidateAll = true;
            _invalidUsers.Clear();
        }

        var unsupported = new List<string>();
        foreach (var op in requested)
            if (!MaterialOperations.IsSupported(op)) unsupported.Add(op.ToString());
        Log?.Invoke("materials: provider registered owner=" + owner + " requested=" + JoinOps(requested)
            + (unsupported.Count > 0 ? " not-wired=" + string.Join(",", unsupported) + " (these stay vanilla)" : ""));
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
            _requested = Array.Empty<MaterialOperation>();
            _revision++;
            _invalidateAll = true;
            _invalidUsers.Clear();
        }
        Log?.Invoke("materials: provider unregistered owner=" + owner + " (vanilla rules apply again)");
    }

    /// <summary>Drop every registration (Core uninstall). Logs when one was held.</summary>
    internal void Clear()
    {
        long token;
        lock (_gate) token = _token;
        Unregister(token);
    }

    /// <summary>Remove the provider when <paramref name="owner"/> owns it (plugin unload sweep).</summary>
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

    /// <summary>True when an operation is routed through the provider (registered, requested, supported).</summary>
    internal bool Governs(MaterialOperation operation)
    {
        lock (_gate)
        {
            if (_provider == null || !MaterialOperations.IsSupported(operation)) return false;
            return Array.IndexOf(_requested, operation) >= 0;
        }
    }

    /// <summary>Mark one user's views stale.</summary>
    internal void Invalidate(CastleId user)
    {
        lock (_gate)
        {
            _invalidUsers.Add(user);
            _revision++;
        }
    }

    /// <summary>Mark every view stale and re-read the provider's operations.</summary>
    internal void InvalidateAll()
    {
        IMaterialSourceProvider? provider;
        lock (_gate)
        {
            _invalidateAll = true;
            _invalidUsers.Clear();
            _revision++;
            provider = _provider;
        }
        if (provider == null) return;
        try
        {
            var ops = Normalize(provider.Operations);
            lock (_gate) if (ReferenceEquals(_provider, provider)) _requested = ops;
        }
        catch (Exception e)
        {
            Log?.Invoke("materials: provider.Operations threw on invalidate: " + e.Message);
        }
    }

    /// <summary>
    /// Take the pending invalidations: <paramref name="all"/> true means every view; otherwise
    /// <paramref name="users"/> receives the users marked since the last drain.
    /// </summary>
    internal void DrainInvalidations(List<CastleId> users, out bool all)
    {
        users.Clear();
        lock (_gate)
        {
            all = _invalidateAll;
            if (!all) users.AddRange(_invalidUsers);
            _invalidateAll = false;
            _invalidUsers.Clear();
        }
    }

    internal void CountPayment() { lock (_gate) _payments++; }

    internal void CountView() { lock (_gate) _views++; }

    internal void CountFault() { lock (_gate) _faults++; }

    internal void CountShortfall() { lock (_gate) _shortfalls++; }

    internal MaterialSourceStatus Status()
    {
        var (installed, reason) = SafeHookState();
        lock (_gate)
        {
            var requested = (MaterialOperation[])_requested.Clone();
            var wired = new List<MaterialOperation>();
            if (_provider != null && installed)
                foreach (var op in _requested)
                    if (MaterialOperations.IsSupported(op)) wired.Add(op);
            return new MaterialSourceStatus(_provider != null, _owner, installed, installed ? "" : reason,
                requested, wired, _revision, _payments, _views, _faults, _shortfalls);
        }
    }

    private (bool, string) SafeHookState()
    {
        try { return HookState(); }
        catch (Exception e) { return (false, "hook state threw: " + e.Message); }
    }

    private static MaterialOperation[] Normalize(IReadOnlyCollection<MaterialOperation>? ops)
    {
        if (ops == null) return Array.Empty<MaterialOperation>();
        var seen = new List<MaterialOperation>();
        foreach (var op in ops)
            if (Enum.IsDefined(typeof(MaterialOperation), op) && !seen.Contains(op)) seen.Add(op);
        seen.Sort();
        return seen.ToArray();
    }

    private static string JoinOps(MaterialOperation[] ops) =>
        ops.Length == 0 ? "-" : string.Join(",", Array.ConvertAll(ops, o => o.ToString()));

    private sealed class Handle : IDisposable
    {
        private MaterialSourceRegistry? _registry;
        private readonly long _token;

        internal Handle(MaterialSourceRegistry registry, long token)
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
