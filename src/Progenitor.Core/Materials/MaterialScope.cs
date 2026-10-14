using System;
using System.Collections.Generic;

namespace Progenitor.Materials;

/// <summary>
/// Which governed operation the game is running right now on the server. The availability and
/// payment seams are shared by many game features (repair, research, forge, stables …); only calls
/// made while one of the wired systems updates are routed to the provider. The system hooks open a
/// scope before the system's update and close it after.
///
/// <para>A scope is stamped with the rendered frame it was opened in. When the system throws, the
/// closing hook never runs; a scope from an earlier frame is therefore treated as closed (a single
/// system update cannot span two frames), so a stranded scope can never govern a later call.</para>
/// </summary>
internal sealed class MaterialScope
{
    private readonly List<(MaterialOperation Operation, long Frame, long Token)> _stack = new();
    private long _next;

    /// <summary>Open a scope; returns the token <see cref="Exit"/> needs.</summary>
    internal long Enter(MaterialOperation operation, long frame)
    {
        Prune(frame);
        long token = ++_next;
        _stack.Add((operation, frame, token));
        return token;
    }

    /// <summary>Close the scope <paramref name="token"/> opened (and anything left open inside it).</summary>
    internal void Exit(long token)
    {
        for (int i = _stack.Count - 1; i >= 0; i--)
        {
            if (_stack[i].Token != token) continue;
            _stack.RemoveRange(i, _stack.Count - i);
            return;
        }
    }

    /// <summary>The innermost open scope opened in <paramref name="frame"/>, if any.</summary>
    internal bool TryCurrent(long frame, out MaterialOperation operation) => TryCurrent(frame, out operation, out _);

    /// <summary>The innermost open scope opened in <paramref name="frame"/> and its token, if any.</summary>
    internal bool TryCurrent(long frame, out MaterialOperation operation, out long token)
    {
        Prune(frame);
        if (_stack.Count == 0) { operation = default; token = 0; return false; }
        operation = _stack[_stack.Count - 1].Operation;
        token = _stack[_stack.Count - 1].Token;
        return true;
    }

    internal int Depth => _stack.Count;

    internal void Clear() => _stack.Clear();

    private void Prune(long frame)
    {
        // Anything opened in another frame is stranded (its system threw before the closing hook).
        _stack.RemoveAll(e => e.Frame != frame);
    }
}

/// <summary>
/// Pairs the governed systems' Before and After hooks into scopes. Game-free. The hooks are registered
/// to run even when the system is skipped, so every Before has its After unless the system throws.
/// <list type="bullet">
/// <item><description>The governed systems never nest: a Before while a scope is open closes the open
/// (stranded) one first.</description></item>
/// <item><description>An After closes the scope only when it is the opening system's.</description></item>
/// <item><description>A scope is current only in the frame it was opened in; <see cref="Tick"/> closes one
/// left over from an earlier frame.</description></item>
/// </list>
/// <see cref="Closed"/> runs whenever a scope ends, never from inside a seam call.
/// </summary>
internal sealed class MaterialScopeTracker
{
    private readonly MaterialScope _scope = new();
    private long _token;
    private long _frame;
    private string? _system;

    /// <summary>Called after a scope ended (paired, stranded, or cleared).</summary>
    internal Action? Closed { get; set; }

    /// <summary>The system that opened the current scope, or null.</summary>
    internal string? OpenSystem => _token != 0 ? _system : null;

    internal void Before(MaterialOperation operation, string system, long frame)
    {
        if (_token != 0) Close();
        _token = _scope.Enter(operation, frame);
        _frame = frame;
        _system = system;
    }

    internal void After(string system)
    {
        if (_token == 0 || !string.Equals(system, _system, StringComparison.Ordinal)) return;
        Close();
    }

    /// <summary>The current scope for a seam call in <paramref name="frame"/>. Never closes anything.</summary>
    internal bool TryCurrent(long frame, out MaterialOperation operation, out long token)
    {
        operation = default;
        token = 0;
        if (_token == 0 || frame != _frame) return false;
        return _scope.TryCurrent(frame, out operation, out token);
    }

    /// <summary>Outside any system: close a scope left over from an earlier frame (its system threw).</summary>
    internal void Tick(long frame)
    {
        if (_token != 0 && frame != _frame) Close();
    }

    internal void Clear()
    {
        _scope.Clear();
        _token = 0;
        _system = null;
    }

    private void Close()
    {
        _scope.Exit(_token);
        _token = 0;
        _system = null;
        Closed?.Invoke();
    }
}

/// <summary>
/// One castle heart request the game is handling in the current system update: who sent it, for which
/// heart (packed network id), and whether it is an upgrade. Game-free.
/// </summary>
internal readonly struct MaterialHeartRequest
{
    internal ulong Character { get; }
    internal ulong HeartNet { get; }
    internal bool Upgrade { get; }

    internal MaterialHeartRequest(ulong character, ulong heartNet, bool upgrade)
    {
        Character = character;
        HeartNet = heartNet;
        Upgrade = upgrade;
    }
}

/// <summary>
/// Which seam calls inside the castle heart system's scope are an <see cref="MaterialOperation.Upgrade"/>.
/// The game's castle heart system handles upgrade, claim, raid, destroy, abandon and expose requests in
/// one update, and the upgrade, claim, raid and destroy costs all reach the same count and removal. A
/// call is an upgrade only when the character sent an upgrade request for that very heart in this
/// update and no other kind of request for it; everything else (claim, raid and destroy costs, an
/// unreadable request) keeps the game's own rules. The requests are read once per scope, on the first
/// call that asks. Game-free.
/// </summary>
internal sealed class MaterialUpgradeFilter
{
    private long _token;
    private readonly HashSet<(ulong, ulong)> _upgrade = new();
    private readonly HashSet<(ulong, ulong)> _other = new();
    private readonly List<MaterialHeartRequest> _scratch = new();

    /// <summary>
    /// True when the call for <paramref name="character"/> at <paramref name="heartNet"/> inside scope
    /// <paramref name="token"/> is an upgrade. <paramref name="load"/> fills the requests of this
    /// update; it runs once per scope and a throw means "no upgrade requests".
    /// </summary>
    internal bool IsUpgrade(long token, ulong character, ulong heartNet, Action<List<MaterialHeartRequest>> load)
    {
        if (token == 0 || character == 0 || heartNet == 0) return false;
        if (token != _token)
        {
            _token = token;
            _upgrade.Clear();
            _other.Clear();
            _scratch.Clear();
            try { load(_scratch); }
            catch { _scratch.Clear(); }
            foreach (var r in _scratch)
            {
                if (r.Character == 0 || r.HeartNet == 0) continue;
                (r.Upgrade ? _upgrade : _other).Add((r.Character, r.HeartNet));
            }
            _scratch.Clear();
        }
        var key = (character, heartNet);
        return _upgrade.Contains(key) && !_other.Contains(key);
    }

    internal void Clear()
    {
        _token = 0;
        _upgrade.Clear();
        _other.Clear();
        _scratch.Clear();
    }
}
