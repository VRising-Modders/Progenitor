using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Progenitor.Dev.Ops;

/// <summary>
/// Game-free half of the server <c>inventory_moves</c> op: argument parsing. The live op
/// (<see cref="InventoryMovesOps"/>) reads through <c>PlayerInventoryMoveBridge</c>.
/// </summary>
internal static class InventoryMovesArgs
{
    /// <summary>Moves returned when <c>last</c> is not given.</summary>
    internal const int DefaultLast = 20;

    /// <summary>The most moves the ring keeps (and one call returns).</summary>
    internal const int MaxLast = 256;

    /// <summary>One parsed request.</summary>
    internal sealed record Request(int Last, bool? Record, bool Clear);

    /// <summary>
    /// <c>last</c>: how many recent moves to return (0 to 256, default 20). <c>record</c>: true turns
    /// the bridge recorder on (the observer runs even with no plugin subscribed, and logs each move),
    /// false turns it off; absent leaves it. <c>clear</c>: forget the recent moves first.
    /// </summary>
    internal static Request Parse(JsonElement args)
    {
        if (args.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return new Request(DefaultLast, null, false);
        if (args.ValueKind != JsonValueKind.Object) throw new ArgumentException("args must be an object: {\"record\":true|false,\"last\":20,\"clear\":false}");
        int last = DefaultLast;
        if (args.TryGetProperty("last", out var l) && l.ValueKind != JsonValueKind.Null)
        {
            if (l.ValueKind != JsonValueKind.Number || !l.TryGetInt32(out last) || last < 0 || last > MaxLast)
                throw new ArgumentException("args.last must be a whole number from 0 to " + MaxLast);
        }

        bool? record = null;
        if (args.TryGetProperty("record", out var r) && r.ValueKind != JsonValueKind.Null)
        {
            if (r.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new ArgumentException("args.record must be true or false");
            record = r.GetBoolean();
        }

        bool clear = false;
        if (args.TryGetProperty("clear", out var c) && c.ValueKind != JsonValueKind.Null)
        {
            if (c.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new ArgumentException("args.clear must be true or false");
            clear = c.GetBoolean();
        }

        return new Request(last, record, clear);
    }
}

#if !PROGENITOR_TESTS
/// <summary>
/// Debug-bridge <c>inventory_moves</c> (server, main thread): the <c>ctx.Inventory.OnPlayerMove</c>
/// observer's state and counters and its last moves; <c>record</c> turns the bridge recorder on or
/// off. Arguments: <see cref="InventoryMovesArgs"/>.
/// </summary>
internal static class InventoryMovesOps
{
    public static object Run(JsonElement args)
    {
        if (!Progenitor.Plugin.IsServerProcess)
            throw new InvalidOperationException("inventory_moves runs on the server (inventories are server-authoritative); send it to the server bridge (27342)");
        var req = InventoryMovesArgs.Parse(args);
        var r = Progenitor.Inventory.PlayerInventoryMoveBridge.Read(req.Last, req.Record, req.Clear);
        var moves = new List<object>(r.Moves.Count);
        foreach (var m in r.Moves)
        {
            moves.Add(new
            {
                seq = m.Sequence,
                utc = m.UtcTime.ToString("O"),
                user = m.UserIndex + ":" + m.UserVersion,
                character = m.CharacterIndex + ":" + m.CharacterVersion,
                kind = m.Kind.ToString(),
                direction = m.Direction.ToString(),
                item = m.Item,
                amount = m.Amount,
                container = m.Container.IsNone ? null : m.Container.ToString(),
                castle = m.IsCastleContainer,
                inventory = m.Inventory.ToString(),
                countBefore = m.CountBefore,
                countAfter = m.CountAfter,
                counterpart = m.Counterpart.ToString(),
                counterpartInventory = m.CounterpartInventory.IsNone ? null : m.CounterpartInventory.ToString(),
                counterpartContainer = m.CounterpartContainer.IsNone ? null : m.CounterpartContainer.ToString(),
                requests = m.Requests,
                ambiguous = m.Ambiguous,
                incomplete = m.Incomplete,
            });
        }

        return new
        {
            installed = r.Installed,
            hooked = r.Hooked,
            subscribers = r.Subscribers,
            recording = r.Recording,
            updates = r.Updates,
            requests = r.Requests,
            truncated = r.Truncated,
            playerOnly = r.PlayerOnly,
            unresolved = r.Unresolved,
            unchanged = r.Unchanged,
            unread = r.Unread,
            lostAfter = r.LostAfter,
            raised = r.Raised,
            dropped = r.Dropped,
            handlerErrors = r.HandlerErrors,
            lastError = r.LastError,
            rebuilds = r.Rebuilds,
            primes = r.Primes,
            hookRebuildMaxMicroseconds = Math.Round(r.HookRebuildMaxMicroseconds, 1),
            primeMicroseconds = Math.Round(r.PrimeMicroseconds, 1),
            moves,
            note = "record:true hooks the item request systems even with no plugin subscribed and logs each move. Amounts are read back: a refused request adds to 'unchanged', not to moves.",
        };
    }
}
#endif
