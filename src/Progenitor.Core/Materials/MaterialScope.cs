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
    internal bool TryCurrent(long frame, out MaterialOperation operation)
    {
        Prune(frame);
        if (_stack.Count == 0) { operation = default; return false; }
        operation = _stack[_stack.Count - 1].Operation;
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
