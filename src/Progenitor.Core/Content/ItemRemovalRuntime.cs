using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using ProjectM;
using ProjectM.Network;
using Progenitor.Ecs;
using Progenitor.Plugins;
using Progenitor.Reload;
using Progenitor.WorldState;
using Stunlock.Core;
using Unity.Entities;
using Unity.Collections;

namespace Progenitor.Content;

/// <summary>Server-owned removal of tracked custom items; ordinary unregister never deletes inventory.</summary>
internal static class ItemRemovalRuntime
{
    private static readonly HashSet<string> StartupAbsent = new(StringComparer.Ordinal);
    private static readonly HashSet<string> Completed = new(StringComparer.Ordinal);
    private static readonly HashSet<string> Warned = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, DateTime> RetryAfter = new(StringComparer.Ordinal);
    private static DateTime _next;
    private static bool _installed;
    private static int _epoch;
    private static Job? _job;
    private static readonly ItemOwnerRegistrationQueue PendingRegistrations = new();
    private static DateTime _nextRegistrationRetry;
    private static DateTime _nextRegistrationFlush;
    private const int RegistrationBatchSize = 128;
    private static string Root => Paths.BepInExRootPath;
    private static string LedgerPath => Path.Combine(Root, ItemOwnerLedger.RelativeFileName);

    internal static void Install()
    {
        if (!Plugin.IsServerProcess || _installed) return;
        _installed = true;
        // Capture actual first-generation startup absence once. A Core swap is not a startup.
        if (WorldEpoch.CoreGenerationOrZero() == 1)
        {
            var ledger = ItemOwnerLedger.Load(LedgerPath);
            if (ledger.IsValid)
            {
                var disk = new ItemOwnerDiskPlugins(Root);
                foreach (var owner in ledger.Owners)
                    if (disk.Absent(ledger, owner)) StartupAbsent.Add(owner);
            }
        }
        _next = DateTime.UtcNow.AddSeconds(30);
        Plugin.Frame += Tick;
    }

    internal static void Uninstall()
    {
        Plugin.Frame -= Tick;
        if (!CaptureAndFlushRegistrations())
            Plugin.Logger?.LogWarning("Item removal: final ownership flush failed; queued provenance was not durably saved.");
        _installed = false;
        _job = null;
        StartupAbsent.Clear(); Completed.Clear(); Warned.Clear(); RetryAfter.Clear();
    }

    internal static void NoteRegistered(PrefabDefinition definition)
    {
        if (!Plugin.IsServerProcess || definition.Kind != PrefabKind.Item) return;
        try
        {
            var type = definition.Owner.GetType();
            var ownerId = ContentOwnerIdentity.Id(definition.Owner);
            if (ownerId == null || ownerId == MyPluginInfo.PLUGIN_GUID) return;
            var assembly = type.Assembly;
            var path = definition.Owner is DataPackOwner pack ? pack.ManifestPath : assembly.Location;
            if (string.IsNullOrEmpty(path) && !PluginReloader.TryGetSourcePath(assembly, out path))
            {
                Warn(ownerId, "item ownership has no plugin file provenance; automatic removal disabled");
                return;
            }
            var relative = Path.GetRelativePath(Path.GetFullPath(Root), Path.GetFullPath(path));
            if (!PendingRegistrations.TryEnqueue(new ItemOwnerRecord(definition.Id, definition.Guid.GuidHash,
                ownerId, relative, definition.Source.GuidHash)))
            {
                Warn(ownerId, "cannot queue unambiguous item ownership; automatic removal disabled");
                return;
            }
            if (PendingRegistrations.Count == 1 && _nextRegistrationFlush == default)
                _nextRegistrationFlush = DateTime.UtcNow.AddSeconds(1);
            StartupAbsent.Remove(ownerId);
            Completed.Remove(ownerId);
        }
        catch (Exception e) { Warn("registration", e.Message); }
    }

    /// <summary>Capture deferred notifications before owner disposal, then attempt durability.</summary>
    internal static bool CaptureAndFlushRegistrations()
    {
        // A Core unload can precede the deferred provenance callbacks. Capture live
        // definitions before their owners dispose them, without retaining plugin objects.
        foreach (var definition in PrefabRegistry.Definitions) NoteRegistered(definition);
        return FlushPendingRegistrations();
    }

    /// <summary>Flush before explicit removal; false preserves the queued batch for retry.</summary>
    internal static bool FlushPendingRegistrations()
    {
        if (PendingRegistrations.Count == 0) return true;
        var activeOwners = new HashSet<string>(StringComparer.Ordinal);
        foreach (var definition in PrefabRegistry.Definitions)
            if (ContentOwnerIdentity.Id(definition.Owner) is { } owner) activeOwners.Add(owner);
        if (!PendingRegistrations.TryFlush(LedgerPath, out var rejected, out var error, activeOwners.Contains))
        {
            _nextRegistrationRetry = DateTime.UtcNow.AddSeconds(5);
            Warn("ownership-persistence", "cannot persist item ownership; pending registrations retained for retry: " + error);
            return false;
        }
        _nextRegistrationRetry = default;
        _nextRegistrationFlush = default;
        foreach (string owner in rejected)
            Warn(owner, "cannot persist unambiguous item ownership; automatic removal disabled");
        return true;
    }

    private static bool Eligible(ItemOwnerLedger ledger, string owner, ItemOwnerDiskPlugins disk)
    {
        if (ModRegistry.TryGet(owner, out var state) && state.Outcome != ModLoadOutcome.Unloaded) return false;
        var ownedGuids = new HashSet<int>(ledger.RowsOf(owner).Select(r => r.Guid));
        if (PrefabRegistry.Definitions.Any(d => ownedGuids.Contains(d.Guid.GuidHash))) return false;
        bool startupAbsent = StartupAbsent.Contains(owner);
        // A present file only ever refuses, so an owner refused even as absent skips the disk scan.
        if (ledger.Eligible(owner, false, false, false, startupAbsent).Count == 0) return false;
        return ledger.Eligible(owner, false, !disk.Absent(ledger, owner), false, startupAbsent).Count != 0;
    }

    private static void Tick()
    {
        if (!_installed) return;
        // Ownership durability has its own cadence; removal scans remain infrequent.
        if (PendingRegistrations.Count != 0 && DateTime.UtcNow >= _nextRegistrationRetry
            && (PendingRegistrations.Count >= RegistrationBatchSize || DateTime.UtcNow >= _nextRegistrationFlush)
            && !FlushPendingRegistrations()) return;
        if (PendingRegistrations.Count != 0 || DateTime.UtcNow < _next) return;
        _next = DateTime.UtcNow.AddSeconds(_job == null ? 5 : .25);
        try
        {
            var world = Worlds.Server;
            if (world == null || !world.IsCreated) { _job = null; return; }
            if (world.GetExistingSystemManaged<LoadPersistenceSystemV2>()?.Initialized != true) { _job = null; return; }
            if (_epoch != WorldEpoch.Current) { _epoch = WorldEpoch.Current; _job = null; Completed.Clear(); RetryAfter.Clear(); }
            var ledger = ItemOwnerLedger.Load(LedgerPath);
            if (!ledger.IsValid) { Warn("ledger", "invalid ownership ledger; preserving items: " + ledger.Error); _job = null; return; }
            // One read of the plugin folders per tick, shared by every owner checked in it.
            var disk = new ItemOwnerDiskPlugins(Root);
            if (_job == null)
            {
                foreach (var owner in ledger.Owners)
                {
                    if (Completed.Contains(owner) || (RetryAfter.TryGetValue(owner, out var retry) && DateTime.UtcNow < retry) || !Eligible(ledger, owner, disk)) continue;
                    _job = new Job(owner, ledger.RowsOf(owner).Select(r => r.Guid), world.EntityManager);
                    break;
                }
                if (_job == null) return;
            }
            // Recheck at every mutation boundary, including a reinstall while cleanup is pending.
            if (!Eligible(ledger, _job.Owner, disk)) { _job = null; return; }
            if (_job.Step(world.EntityManager))
            {
                Plugin.Logger?.LogInfo($"Item removal: owner={_job.Owner} quarantined={_job.Removed} preserved={_job.Preserved}; unsupported equipment, properties and destinations are preserved in place.");
                Completed.Add(_job.Owner);
                _job = null;
            }
        }
        catch (Exception e)
        {
            if (_job != null) RetryAfter[_job.Owner] = DateTime.UtcNow.AddSeconds(60);
            _job = null;
            Warn("cleanup", "cleanup stopped; items preserved where removal was not verified: " + e.Message);
        }
    }

    private static void Warn(string key, string message)
    {
        if (Warned.Add(key)) Plugin.Logger?.LogWarning("Item removal: " + message);
    }

    private sealed class Job
    {
        internal readonly string Owner;
        private readonly HashSet<int> _guids;
        private List<Entity> _entities;
        private int _index;
        private int _phase;
        private int _slot = -1;
        internal int Removed, Preserved;
        internal Job(string owner, IEnumerable<int> guids, EntityManager em)
        {
            Owner = owner; _guids = new HashSet<int>(guids);
            _entities = Snapshot(em, new[] { ComponentIndex.Of<Equipment>(ComponentType.AccessMode.ReadOnly), ComponentIndex.Of<PlayerCharacter>(ComponentType.AccessMode.ReadOnly) });
        }
        internal bool Step(EntityManager em)
        {
            int budget = 128;
            // At most one ledger read per step, and only when a stack is about to be quarantined.
            ItemOwnerLedger? ledger = null;
            while (_index < _entities.Count && budget-- > 0)
            {
                var entity = _entities[_index];
                if (!em.Exists(entity)) { _index++; _slot = -1; continue; }
                if (_phase == 0 || _phase == 2)
                {
                    _index++;
                    if (!em.HasComponent(entity, ComponentIndex.Of<Equipment>(ComponentType.AccessMode.ReadOnly))) continue;
                    var e = em.GetComponentData<Equipment>(entity);
                    void Unequip(EquipmentSlot slot, EquipmentType type)
                    {
                        if (!_guids.Contains(slot.SlotId.GuidHash)) return;
                        // Unique equipment needs a property-complete codec before it can be removed.
                        // Do not unequip or delete it merely to make the cleanup scan pass.
                        if (_phase == 0) Preserved++;
                    }
                    Unequip(e.ArmorHeadgearSlot, EquipmentType.Headgear);
                    Unequip(e.ArmorChestSlot, EquipmentType.Chest);
                    Unequip(e.WeaponSlot, EquipmentType.Weapon);
                    Unequip(e.GrimoireSlot, EquipmentType.MagicSource);
                    Unequip(e.ArmorFootgearSlot, EquipmentType.Footgear);
                    Unequip(e.ArmorLegsSlot, EquipmentType.Legs);
                    Unequip(e.ArmorGlovesSlot, EquipmentType.Gloves);
                    Unequip(e.CloakSlot, EquipmentType.Cloak);
                    Unequip(e.ChestCosmeticSlot, EquipmentType.CosmeticChest);
                    Unequip(e.FootgearCosmeticSlot, EquipmentType.CosmeticFootgear);
                    Unequip(e.LegsCosmeticSlot, EquipmentType.CosmeticLegs);
                    Unequip(e.GlovesCosmeticSlot, EquipmentType.CosmeticGloves);
                    Unequip(e.CloakCosmeticSlot, EquipmentType.CosmeticCloak);
                }
                else
                {
                    if (!em.HasComponent(entity, ComponentIndex.Of<InventoryBuffer>(ComponentType.AccessMode.ReadWrite))) { _index++; _slot = -1; continue; }
                    int length = em.GetBuffer<InventoryBuffer>(entity).Length;
                    if (length > 1024) throw new InvalidOperationException("Inventory exceeds cleanup bounds.");
                    if (_slot < 0) _slot = length - 1;
                    while (_slot >= 0 && budget-- > 0)
                    {
                        int i = _slot--;
                        var buffer = em.GetBuffer<InventoryBuffer>(entity);
                        if (i >= buffer.Length) continue;
                        var row = buffer[i];
                        if (!_guids.Contains(row.ItemType.GuidHash) || row.Amount <= 0) continue;
                        if (_phase == 3) continue; // Unsupported records intentionally remain in place.
                        ledger ??= ItemOwnerLedger.Load(LedgerPath);
                        var ownership = ledger.RowsOf(Owner).First(r => r.Guid == row.ItemType.GuidHash);
                        if (ItemManagementRuntime.Quarantine(Worlds.Server!, entity, i, row, ownership, out var reason))
                            Removed += row.Amount;
                        else { Preserved++; Warn(Owner + ":" + reason, reason); }
                    }
                    if (_slot < 0) _index++;
                }
            }
            if (_index < _entities.Count) return false;
            if (_phase == 3) return true;
            _phase++; _index = 0; _slot = -1;
            _entities = _phase == 2
                ? Snapshot(em, new[] { ComponentIndex.Of<Equipment>(ComponentType.AccessMode.ReadOnly), ComponentIndex.Of<PlayerCharacter>(ComponentType.AccessMode.ReadOnly) })
                : Snapshot(em, new[] { ComponentIndex.Of<InventoryBuffer>(ComponentType.AccessMode.ReadWrite) });
            return false;
        }

        private static List<Entity> Snapshot(EntityManager em, ComponentType[] types)
        {
            var query = Query.Build(em, includeDisabled: true, all: types);
            try
            {
                var entities = query.ToEntityArray(Allocator.Temp);
                try
                {
                    if (entities.Length > 100000) throw new InvalidOperationException("Cleanup query exceeds bounds.");
                    var result = new List<Entity>(entities.Length);
                    for (int i = 0; i < entities.Length; i++) result.Add(entities[i]);
                    return result;
                }
                finally { entities.Dispose(); }
            }
            finally { query.Dispose(); }
        }
    }
}
