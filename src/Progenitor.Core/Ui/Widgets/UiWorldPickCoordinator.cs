using System;
using System.Collections.Generic;

namespace Progenitor.Ui;

/// <summary>
/// The game side of a world pick. The coordinator tells it when a pick is armed and when none is;
/// everything that touches the game (the system hook, the prompt band, Escape) lives behind it.
/// </summary>
internal interface IUiWorldPickBackend
{
    /// <summary>True when a pick can be armed now (a client world with a local character); else why not.</summary>
    bool Ready(out string why);

    /// <summary>
    /// Per tick while armed, cheap: the world and local character the pick was armed with are still
    /// there (false on leaving the world, a kick, a disconnect or the character going away).
    /// </summary>
    bool StillReady(out string why);

    /// <summary><paramref name="pick"/> is now the armed pick (show its prompt, watch interacts, take Escape).</summary>
    void Armed(UiWorldPickHandle pick);

    /// <summary>No pick is armed any more (hide the prompt, release Escape; the hook may linger, see the swallow list).</summary>
    void Disarmed();
}

/// <summary>What the coordinator answers when the game is about to open an interact panel.</summary>
internal enum UiWorldPickOffer
{
    /// <summary>No armed pick wants this panel: let it open.</summary>
    PassThrough = 0,

    /// <summary>The armed pick takes the target and the panel must not open.</summary>
    TakeAndSuppress = 1,

    /// <summary>The armed pick takes the target and the panel opens as usual.</summary>
    TakeAndOpen = 2,
}

/// <summary>
/// The game-free state machine behind <c>ctx.Hud.PickInWorld</c>.
///
/// <para><b>Rules.</b> One pick is armed at a time, process-wide. Arming a second pick ends the first as
/// <see cref="UiWorldPickState.Cancelled"/> (its <c>onCancelled</c> runs) before the second is armed.
/// A pick ends exactly once: <c>onPicked</c> on a matching interact, <c>onCancelled</c> on Escape,
/// <see cref="UiWorldPickHandle.Cancel"/>, replacement, timeout, a world change or no world at arm time,
/// and no callback at all on Dispose, owner unload or Core unload. State changes and the backend's
/// disarm happen before a callback runs, so a callback may arm the next pick.</para>
/// </summary>
internal sealed class UiWorldPickCoordinator
{
    private readonly IUiWorldPickBackend _backend;
    private readonly Func<double> _clock;
    private readonly Func<int> _epoch;
    private readonly Action<string> _log;
    private UiWorldPickHandle? _current;
    private int _sequence;

    internal UiWorldPickCoordinator(IUiWorldPickBackend backend, Func<double> clock, Func<int> epoch, Action<string>? log = null)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _epoch = epoch ?? throw new ArgumentNullException(nameof(epoch));
        _log = log ?? (_ => { });
    }

    /// <summary>The armed pick, or null.</summary>
    internal UiWorldPickHandle? Current => _current;

    /// <summary>True while a pick is armed.</summary>
    internal bool IsArmed => _current != null;

    /// <summary>Arm a pick for <paramref name="owner"/>; ends the armed one first (see the class rules).</summary>
    internal UiWorldPickHandle Arm(
        object owner, string ownerName, Action<UiWorldPickResult> onPicked, Action? onCancelled, UiWorldPickOptions? options)
    {
        if (owner == null) throw new ArgumentNullException(nameof(owner));
        if (onPicked == null) throw new ArgumentNullException(nameof(onPicked));
        var opts = (options ?? new UiWorldPickOptions()).Copy();
        var pick = new UiWorldPickHandle(this, ++_sequence, owner, ownerName ?? "", onPicked, onCancelled, opts, _epoch(), _clock());

        if (_current != null)
        {
            var previous = _current;
            _log("Ui: world pick #" + previous.Id + " (" + previous.OwnerName + ") replaced by #" + pick.Id + " (" + pick.OwnerName + ").");
            End(previous, UiWorldPickState.Cancelled, runCallback: true);
        }

        string why;
        bool ready;
        try { ready = _backend.Ready(out why); }
        catch (Exception e)
        {
            ready = false;
            why = "the readiness check threw: " + e.Message;
        }

        if (!ready)
        {
            pick.State = UiWorldPickState.Cancelled;
            _log("Ui: world pick #" + pick.Id + " (" + pick.OwnerName + ") cancelled at once: " + why);
            Invoke(pick, pick.OnCancelled, "onCancelled");
            return pick;
        }

        // A previous pick's callback may have armed one of its own while we were ending it; this call is newer.
        if (_current != null) End(_current, UiWorldPickState.Cancelled, runCallback: true);

        pick.State = UiWorldPickState.Armed;
        _current = pick;
        _log("Ui: world pick #" + pick.Id + " armed for " + pick.OwnerName + " (kinds=" + opts.Kinds
             + (opts.TimeoutSeconds > 0 ? ", timeout=" + opts.TimeoutSeconds + "s" : "")
             + (opts.SuppressPanel ? "" : ", panel opens") + ").");
        try { _backend.Armed(pick); }
        catch (Exception e)
        {
            _log("Ui: world pick #" + pick.Id + " backend arm threw: " + e.Message + "; cancelling it.");
            End(pick, UiWorldPickState.Cancelled, runCallback: true);
        }

        return pick;
    }

    /// <summary>The game is about to open the interact panel <paramref name="menuType"/>: does the armed pick want it?</summary>
    internal UiWorldPickOffer Offer(int menuType)
    {
        var pick = _current;
        if (pick == null) return UiWorldPickOffer.PassThrough;
        if (!UiWorldPickMenus.Wants(pick.Options.Kinds, menuType, out _)) return UiWorldPickOffer.PassThrough;
        return pick.Options.SuppressPanel ? UiWorldPickOffer.TakeAndSuppress : UiWorldPickOffer.TakeAndOpen;
    }

    /// <summary>
    /// Finish the armed pick with <paramref name="entityIndex"/>:<paramref name="entityVersion"/> seen in
    /// world epoch <paramref name="epoch"/>. A pick armed in another epoch is cancelled instead (its
    /// world is gone). Returns the result delivered, or null when nothing was picked.
    /// </summary>
    internal UiWorldPickResult? Complete(int menuType, int entityIndex, int entityVersion, int epoch, bool suppressed,
        int networkId0 = 0, int networkId1 = 0, int networkId2 = 0)
    {
        var pick = _current;
        if (pick == null) return null;
        if (!UiWorldPickMenus.Wants(pick.Options.Kinds, menuType, out var info)) return null;
        if (epoch != pick.Epoch)
        {
            _log("Ui: world pick #" + pick.Id + " was armed in another world; cancelled.");
            End(pick, UiWorldPickState.Cancelled, runCallback: true);
            return null;
        }

        var result = new UiWorldPickResult(entityIndex, entityVersion, info.Kind, menuType, info.Name, epoch, suppressed,
            networkId0, networkId1, networkId2);
        pick.Result = result;
        _log("Ui: world pick #" + pick.Id + " picked " + result + ".");
        End(pick, UiWorldPickState.Picked, runCallback: true);
        return result;
    }

    /// <summary>Escape: cancel the armed pick. True when one was armed (the key is taken).</summary>
    internal bool Escape()
    {
        var pick = _current;
        if (pick == null) return false;
        _log("Ui: world pick #" + pick.Id + " cancelled by Escape.");
        End(pick, UiWorldPickState.Cancelled, runCallback: true);
        return true;
    }

    /// <summary>Per frame: the world-change and still-in-the-world checks, then the timeout.</summary>
    internal void Tick()
    {
        var pick = _current;
        if (pick == null) return;
        if (_epoch() != pick.Epoch)
        {
            _log("Ui: world pick #" + pick.Id + " cancelled: the world it was armed in ended.");
            End(pick, UiWorldPickState.Cancelled, runCallback: true);
            return;
        }

        string why;
        bool alive;
        try { alive = _backend.StillReady(out why); }
        catch (Exception e)
        {
            alive = false;
            why = "the readiness check threw: " + e.Message;
        }

        if (!alive)
        {
            _log("Ui: world pick #" + pick.Id + " cancelled: " + why + ".");
            End(pick, UiWorldPickState.Cancelled, runCallback: true);
            return;
        }

        double timeout = pick.Options.TimeoutSeconds;
        if (timeout > 0 && _clock() - pick.ArmedAt >= timeout)
        {
            _log("Ui: world pick #" + pick.Id + " timed out after " + timeout + "s.");
            End(pick, UiWorldPickState.TimedOut, runCallback: true);
        }
    }

    /// <summary>Plugin unload: end <paramref name="owner"/>'s armed pick without callbacks.</summary>
    internal int DropOwner(object owner)
    {
        var pick = _current;
        if (pick == null || owner == null || !ReferenceEquals(pick.Owner, owner)) return 0;
        _log("Ui: world pick #" + pick.Id + " dropped with its plugin (" + pick.OwnerName + ").");
        End(pick, UiWorldPickState.Disposed, runCallback: false);
        return 1;
    }

    /// <summary>Core unload: end any armed pick without callbacks.</summary>
    internal void Reset()
    {
        var pick = _current;
        if (pick != null) End(pick, UiWorldPickState.Disposed, runCallback: false);
    }

    internal void Cancel(UiWorldPickHandle pick)
    {
        if (!ReferenceEquals(_current, pick)) return;
        _log("Ui: world pick #" + pick.Id + " cancelled by its plugin.");
        End(pick, UiWorldPickState.Cancelled, runCallback: true);
    }

    internal void Dispose(UiWorldPickHandle pick)
    {
        if (!ReferenceEquals(_current, pick)) return;
        _log("Ui: world pick #" + pick.Id + " disposed while armed.");
        End(pick, UiWorldPickState.Disposed, runCallback: false);
    }

    private void End(UiWorldPickHandle pick, UiWorldPickState state, bool runCallback)
    {
        if (pick.State != UiWorldPickState.Armed && pick.State != UiWorldPickState.Idle) return;
        pick.State = state;
        if (ReferenceEquals(_current, pick))
        {
            _current = null;
            try { _backend.Disarmed(); }
            catch (Exception e) { _log("Ui: world pick backend disarm threw: " + e.Message); }
        }

        if (!runCallback) return;
        if (state == UiWorldPickState.Picked && pick.Result.HasValue)
        {
            var result = pick.Result.Value;
            try { pick.OnPicked(result); }
            catch (Exception e) { _log("Ui: world pick #" + pick.Id + " onPicked threw: " + e); }
        }
        else
        {
            Invoke(pick, pick.OnCancelled, "onCancelled");
        }
    }

    private void Invoke(UiWorldPickHandle pick, Action? callback, string name)
    {
        if (callback == null) return;
        try { callback(); }
        catch (Exception e) { _log("Ui: world pick #" + pick.Id + " " + name + " threw: " + e); }
    }
}

/// <summary>One world pick (<see cref="IUiWorldPick"/>).</summary>
internal sealed class UiWorldPickHandle : IUiWorldPick
{
    private readonly UiWorldPickCoordinator? _coordinator;

    internal UiWorldPickHandle(
        UiWorldPickCoordinator? coordinator, int id, object? owner, string ownerName,
        Action<UiWorldPickResult> onPicked, Action? onCancelled, UiWorldPickOptions options, int epoch, double armedAt)
    {
        _coordinator = coordinator;
        Id = id;
        Owner = owner;
        OwnerName = ownerName;
        OnPicked = onPicked;
        OnCancelled = onCancelled;
        Options = options;
        Epoch = epoch;
        ArmedAt = armedAt;
    }

    /// <summary>A handle that does nothing (the server, a skipped UI module).</summary>
    internal static UiWorldPickHandle Inert() =>
        new(null, 0, null, "", _ => { }, null, new UiWorldPickOptions(), 0, 0);

    internal int Id { get; }

    internal object? Owner { get; }

    internal string OwnerName { get; }

    internal Action<UiWorldPickResult> OnPicked { get; }

    internal Action? OnCancelled { get; }

    internal UiWorldPickOptions Options { get; }

    internal int Epoch { get; }

    internal double ArmedAt { get; }

    internal UiWorldPickResult? Result { get; set; }

    public UiWorldPickState State { get; internal set; } = UiWorldPickState.Idle;

    public bool Active => State == UiWorldPickState.Armed;

    public void Cancel() => _coordinator?.Cancel(this);

    public void Dispose() => _coordinator?.Dispose(this);
}

/// <summary>
/// The interact buffs a suppressed pick already ended. The game's interact HUD can ask for the same
/// panel again on the next frames, until the server has removed the buff in answer to the stop
/// message; those repeats must stay closed after the pick itself has finished. An entry is only added
/// when the stop message was actually sent, and it ends when the client stops seeing the buff (the
/// server removed it); <see cref="Ttl"/> is only a safety net for a buff that never goes away.
/// </summary>
internal sealed class UiWorldPickSwallowList
{
    /// <summary>Upper bound in seconds on remembering a swallowed buff (normally it ends when the buff is gone).</summary>
    internal const double Ttl = 10.0;

    private readonly List<(long Key, double Until)> _entries = new();

    internal int Count => _entries.Count;

    internal static long Key(int index, int version) => ((long)index << 32) | (uint)version;

    internal void Add(long key, double now)
    {
        for (int i = 0; i < _entries.Count; i++)
        {
            if (_entries[i].Key != key) continue;
            _entries[i] = (key, now + Ttl);
            return;
        }

        _entries.Add((key, now + Ttl));
    }

    internal bool Contains(long key, double now)
    {
        for (int i = 0; i < _entries.Count; i++)
            if (_entries[i].Key == key && _entries[i].Until > now) return true;
        return false;
    }

    /// <summary>Forget expired entries and those <paramref name="stillExists"/> says are gone.</summary>
    internal void Prune(double now, Func<long, bool>? stillExists = null)
    {
        for (int i = _entries.Count - 1; i >= 0; i--)
        {
            var e = _entries[i];
            bool gone = e.Until <= now;
            if (!gone && stillExists != null)
            {
                try { gone = !stillExists(e.Key); }
                catch { gone = true; }
            }

            if (gone) _entries.RemoveAt(i);
        }
    }

    internal void Clear() => _entries.Clear();
}
