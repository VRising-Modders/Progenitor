using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using Progenitor.Inventory;

namespace Progenitor.Dev.Ops;

/// <summary>
/// Debug-bridge <c>inventory_stamp</c> (server, main thread, read only): the
/// <c>ctx.Inventory.ChangeStamp</c> of each named container in one batch, the clock, an optional
/// <c>AnyChangedSince</c>, and the stamp service's counters (audit misses, fresh stamps, raw-read
/// failures by check). Arguments: <see cref="InventoryStampArgs"/>.
/// </summary>
internal static class InventoryStampOps
{
    public static object Run(JsonElement args)
    {
        if (!Progenitor.Plugin.IsServerProcess)
            throw new InvalidOperationException("inventory_stamp runs on the server (inventories are server-authoritative); send it to the server bridge (27342)");
        var req = InventoryStampArgs.Parse(args);
        var r = InventoryStampBridge.Read(req.Containers, req.Since);
        var rows = new List<object>(req.Containers.Count);
        for (int i = 0; i < req.Containers.Count; i++)
        {
            var c = req.Containers[i];
            rows.Add(new
            {
                container = c.Index.ToString(CultureInfo.InvariantCulture) + ":" + c.Version.ToString(CultureInfo.InvariantCulture)
                    + (c.Instance == 0 ? "" : "#" + c.Instance.ToString(CultureInfo.InvariantCulture)),
                inventory = r.Inventories[i],
                // Hex text: a 64-bit stamp does not survive a JSON number in every client.
                stamp = "0x" + r.Stamps[i].ToString("X16", CultureInfo.InvariantCulture),
            });
        }

        return new
        {
            clock = r.Clock,
            since = req.Since,
            changedSince = r.ChangedSince,
            stamps = rows,
            batchMicroseconds = Math.Round(r.BatchMicroseconds, 1),
            misses = r.Misses,
            audits = r.Audits,
            freshStamps = r.FreshStamps,
            resolves = r.Resolves,
            completionFailures = r.CompletionFailures,
            selfTested = r.SelfTested,
            disabled = r.Disabled,
            auditing = r.Auditing,
            entries = r.Entries,
            typeScans = r.TypeScans,
            failures = r.Failures,
            note = "equal stamps = unchanged slots; unequal = may have changed. misses must stay 0. Pass clock back as since to ask AnyChangedSince.",
        };
    }
}
