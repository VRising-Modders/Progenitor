using System;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using ProjectM;
using Progenitor.Ecs;
using Progenitor.Networking;
using Progenitor.Persistence;
using Unity.Entities;

namespace Progenitor.Content;

/// <summary>Server-owned recovery decisions. Every engine mutation has a durable prior claim.</summary>
internal static class ItemManagementRuntime
{
    private static DateTime _next;
    private static string? _folder, _worldId;
    private static int _cursor, _epoch;
    private static string JournalPath => Path.Combine(_folder!, "progenitor", "item-quarantine.json");

    internal static void Install() { if (Plugin.IsServerProcess) Plugin.Frame += Tick; }
    internal static void Uninstall()
    {
        Plugin.Frame -= Tick;
        ItemQuarantineAdapter.Reset(); _folder = null; _worldId = null; _cursor = 0;
    }

    private static ItemQuarantineJournal Open()
    {
        if (!Plugin.IsServerProcess || Worlds.Server is not { IsCreated: true } world
            || world.GetExistingSystemManaged<LoadPersistenceSystemV2>()?.Initialized != true
            || !ModSave.TryResolveDirectoryNow() || string.IsNullOrEmpty(ModSave.CurrentSaveDirectory))
            throw new InvalidOperationException("The server world is not ready for item management.");
        string folder = Path.GetFullPath(ModSave.CurrentSaveDirectory!);
        if (_epoch != Progenitor.WorldState.WorldEpoch.Current)
        { _epoch = Progenitor.WorldState.WorldEpoch.Current; ItemQuarantineAdapter.Reset(); _cursor = 0; }
        if (_folder != folder || _worldId == null)
        {
            ItemQuarantineAdapter.Reset();
            string dir = Path.Combine(folder, "progenitor");
            Directory.CreateDirectory(dir);
            string identity = Path.Combine(dir, "item-world-id.txt");
            if (!File.Exists(identity))
            {
                using var stream = new FileStream(identity, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                byte[] bytes = System.Text.Encoding.UTF8.GetBytes(Guid.NewGuid().ToString("N"));
                stream.Write(bytes); stream.Flush(true);
            }
            string id = File.ReadAllText(identity).Trim();
            if (!Guid.TryParseExact(id, "N", out _)) throw new InvalidOperationException("Invalid item world identity; preserving items.");
            _folder = folder; _worldId = id;
        }
        var journal = ItemQuarantineJournal.Load(JournalPath, _worldId);
        if (!journal.IsValid) throw new InvalidOperationException("Item recovery records are unreadable; preserving items: " + journal.Error);
        return journal;
    }

    internal static bool Quarantine(World world, Entity inventory, int slot, InventoryBuffer row, ItemOwnerRecord ownership, out string reason)
    {
        reason = "";
        var journal = Open();
        if (!ItemQuarantineAdapter.TryCapture(world, inventory, slot, row, ownership.SourceGuid, out var snapshot, out var destination, out reason)) return false;
        // An unfinished claim cannot be safely inferred from a later inventory scan.
        if (journal.Rows.Any(r => r.Destination == destination && r.Guid == row.ItemType.GuidHash
            && r.State is ItemQuarantineState.Captured or ItemQuarantineState.RemovalPending or ItemQuarantineState.Restoring))
        { reason = "An earlier item operation needs reconciliation; the item is preserved."; return false; }
        string id = Guid.NewGuid().ToString("N");
        var record = new ItemQuarantineRecord(_worldId!, id, ownership.PluginId, ownership.ItemId, row.ItemType.GuidHash, snapshot, destination);
        if (!journal.Capture(JournalPath, record)
            || !journal.Transition(JournalPath, id, ItemQuarantineState.Captured, ItemQuarantineState.RemovalPending))
            throw new IOException("Cannot save item recovery record: " + journal.Error);
        var em = world.EntityManager;
        if (!InventoryUtilitiesServer.TryRemoveItemAtIndex(em, inventory, row.ItemType, row.Amount, slot, true))
            throw new InvalidOperationException("Removal refused; recovery record retained for reconciliation.");
        var buffer = em.GetBuffer<InventoryBuffer>(inventory);
        if (slot < buffer.Length && buffer[slot].ItemType == row.ItemType && buffer[slot].Amount > 0)
            throw new InvalidOperationException("Removal readback was ambiguous; automatic restoration is blocked.");
        // Readback proves this running world's removal, not a completed game save. Restoration
        // requires the adapter's in-memory incarnation capability, invalidated on reload/restart.
        if (!journal.Transition(JournalPath, id, ItemQuarantineState.RemovalPending, ItemQuarantineState.Quarantined))
            throw new IOException("Removal outcome could not be recorded; automatic recovery is blocked.");
        return true;
    }

    private static bool DefinitionAvailable(ItemQuarantineRecord record) => PrefabRegistry.Definitions.Any(d =>
        d.Kind == PrefabKind.Item && d.Id == record.ItemId && d.Guid.GuidHash == record.Guid
        && ContentOwnerIdentity.Id(d.Owner) == record.PluginId);

    internal static ItemManagementStatus GetStatus()
    {
        var journal = Open();
        var status = new ItemManagementStatus
        {
            Note = "Restoration is best effort. Unsupported item properties and destinations are preserved in place. Saved items whose original identity cannot be verified are not restored."
        };
        foreach (var group in journal.Rows.GroupBy(r => r.PluginId).Take(128))
        {
            bool available = group.Any(DefinitionAvailable);
            string reasons = string.Join(" ", group.Select(r => r.Reason).Where(r => !string.IsNullOrEmpty(r)).Distinct().Take(3));
            status.Owners.Add(new ItemManagementOwnerStatus
            {
                OwnerId = group.Key, Available = available,
                Pending = group.Count(r => r.State is ItemQuarantineState.Quarantined or ItemQuarantineState.RestorePending),
                Unavailable = group.Count(r => r.State is ItemQuarantineState.Unavailable or ItemQuarantineState.RemovalPending or ItemQuarantineState.Restoring or ItemQuarantineState.Captured),
                Restored = group.Count(r => r.State == ItemQuarantineState.Restored),
                Reasons = reasons.Substring(0, Math.Min(512, reasons.Length)),
                RestoreOffered = available && group.Any(r => r.State == ItemQuarantineState.Quarantined && r.RestoreDecisionPending)
            });
        }
        return status;
    }

    internal static string Decide(string owner, string decision)
    {
        var journal = Open();
        if (!journal.Rows.Any(r => r.PluginId == owner)) throw new InvalidOperationException("No saved items for that mod.");
        bool ok;
        switch (decision)
        {
            case "restore": case "retry":
                ok = journal.RequestRestore(JournalPath, owner); break;
            case "keep":
                ok = journal.Keep(JournalPath, owner); break;
            case "discard":
                ok = journal.Discard(JournalPath, owner); break;
            default: throw new InvalidOperationException("Unknown item recovery decision.");
        }
        if (!ok) throw new IOException("The decision was not saved: " + journal.Error);
        return decision == "discard" ? "Saved items discarded; uncertain operations remain blocked."
            : decision == "keep" ? "Saved items kept without restoring." : "Best-effort restoration requested; check the results here.";
    }

    private static void Tick()
    {
        if (DateTime.UtcNow < _next) return;
        _next = DateTime.UtcNow.AddSeconds(5);
        try
        {
            var journal = Open();
            var pending = journal.Rows.Where(r => r.State == ItemQuarantineState.RestorePending).ToArray();
            int start = pending.Length == 0 ? 0 : _cursor % pending.Length;
            _cursor = pending.Length == 0 ? 0 : (start + Math.Min(16, pending.Length)) % pending.Length;
            foreach (var record in pending.Skip(start).Concat(pending.Take(start)).Take(16))
            {
                if (!DefinitionAvailable(record))
                { journal.Annotate(JournalPath, record.InstanceId, "Waiting for the original mod and item definition."); continue; }
                var world = Worlds.Server!;
                var probe = ItemQuarantineAdapter.ProbeRestore(world, record);
                if (probe.Status == ItemQuarantineRestoreStatus.Unavailable)
                { journal.Transition(JournalPath, record.InstanceId, record.State, ItemQuarantineState.Unavailable, probe.Reason); continue; }
                if (probe.Status != ItemQuarantineRestoreStatus.Ready)
                { journal.Annotate(JournalPath, record.InstanceId, probe.Reason); continue; }
                if (!journal.Transition(JournalPath, record.InstanceId, record.State, ItemQuarantineState.Restoring)) return;
                var result = ItemQuarantineAdapter.TryRestore(world, record with { State = ItemQuarantineState.Restoring });
                if (result.Status == ItemQuarantineRestoreStatus.Restored)
                    journal.Transition(JournalPath, record.InstanceId, ItemQuarantineState.Restoring, ItemQuarantineState.Restored,
                        "Restored and read back in the running world; restart recovery is not qualified.");
                else if (!result.Attempted)
                    journal.RestoreNotAttempted(JournalPath, record.InstanceId, result.Status == ItemQuarantineRestoreStatus.Unavailable, result.Reason);
                // Any uncertain or partial native operation remains claimed; never repeat a grant.
            }
        }
        catch (Exception e) { Plugin.Logger?.LogDebug("Item management deferred: " + e.Message); }
    }
}
