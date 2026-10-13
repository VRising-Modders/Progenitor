using System;
using System.Collections.Generic;
using System.IO;
using System.Diagnostics;
using Progenitor.Content.Json;
using Progenitor.WorldState;
using System.Linq;
using System.Reflection;
using BepInEx;
using Stunlock.Core;
using ProjectM;
using Progenitor.Ecs;
using Unity.Entities;

namespace Progenitor.Content;

internal sealed record DataPackOwner(string Id, string ManifestPath);

internal static class ContentOwnerIdentity
{
    internal static string? Id(object owner) => owner is DataPackOwner pack ? "content." + pack.Id
        : owner.GetType().GetCustomAttribute<BepInPlugin>()?.GUID;
}

/// <summary>Server-only, data-only registration. No code loading, downloads, archive extraction or client filesystem access.</summary>
internal static class DataPackRuntime
{
    private sealed record Loaded(DataPackManifest Pack, DataPackOwner Owner, Dictionary<string, IDisposable> Handles);
    private static readonly Dictionary<string, Loaded> Active = new(StringComparer.Ordinal);
    private static readonly HashSet<string> Warned = new(StringComparer.Ordinal);
    private static DateTime _next;
    private static string[]? _folders;
    private static int _folderIndex;
    private static Activation? _pending;
    private static long _nextActivationTick;
    private sealed class Activation
    {
        public readonly DataPackManifest Pack;
        public readonly DataPackOwner Owner;
        public readonly Loaded? Previous;
        public readonly Dictionary<string, IDisposable> Handles;
        public readonly World World;
        public readonly int Epoch;
        public readonly DataPackActivation Steps;
        private readonly HashSet<string> _touched = new(StringComparer.Ordinal);
        public Activation(DataPackManifest pack, DataPackOwner owner, Loaded? previous, World world)
        {
            Pack = pack; Owner = owner; Previous = previous; World = world; Epoch = WorldEpoch.Current;
            Handles = previous == null ? new(StringComparer.Ordinal) : new(previous.Handles, StringComparer.Ordinal);
            var oldItems = previous?.Pack.Document.Items.ToDictionary(i => i.Id, StringComparer.Ordinal);
            Steps = new DataPackActivation(pack.Document.Items.Count,
                index => Replace(pack.Document.Items[index]),
                index =>
                {
                    string id = pack.Document.Items[index].Id;
                    // A collision/admission failure before mutation must not disturb that
                    // identity during rollback, especially if another plugin now owns it.
                    if (!_touched.Remove(id)) return;
                    if (Handles.Remove(id, out var handle)) handle.Dispose();
                    if (oldItems != null && oldItems.TryGetValue(id, out var old))
                        Handles[id] = RegisterItem(old, Owner);
                });
        }
        private void Replace(ContentItemSpec item)
        {
            ValidateCurrentOwner(item, Owner);
            if (!Handles.ContainsKey(item.Id)
                && !PrefabRegistry.TryCheckDataPackAdmission(World,
                    new[] { new DataPackItemIdentity(item.Id, PrefabGUID.CalculateGuidHash(item.Id)) }, out var error))
                throw new InvalidOperationException(error);
            _touched.Add(item.Id);
            if (Handles.Remove(item.Id, out var handle)) handle.Dispose();
            Handles[item.Id] = RegisterItem(item, Owner);
        }
    }
    private static string Root => Path.Combine(Paths.BepInExRootPath, "progenitor-content");
    internal static bool RequiresItemStats => Active.Values.Any(p => HasBonuses(p.Pack))
        || (_pending != null && (HasBonuses(_pending.Pack) || (_pending.Previous != null && HasBonuses(_pending.Previous.Pack))));
    private static bool HasBonuses(DataPackManifest pack) => pack.Document.Items.Any(i => i.StatBonuses is { } b
        && (b.MaxHealth != 0 || b.PhysicalPower != 0 || b.SpellPower != 0));

    internal static void Install() { if (Plugin.IsServerProcess) Plugin.Frame += Tick; }
    internal static void Uninstall()
    {
        Plugin.Frame -= Tick;
        if (Plugin.IsServerProcess) ItemRemovalRuntime.CaptureAndFlushRegistrations();
        _pending = null; _folders = null; _folderIndex = 0; _next = default;
        _nextActivationTick = 0;
        foreach (var loaded in Active.Values) Dispose(loaded.Handles);
        Active.Clear(); Warned.Clear();
    }

    private static void Tick()
    {
        var world = Worlds.Server;
        if (_pending != null && (!ReferenceEquals(world, _pending.World) || !_pending.World.IsCreated
            || _pending.Epoch != WorldEpoch.Current)) CancelPending();
        if (world == null || !world.IsCreated || world.GetExistingSystemManaged<LoadPersistenceSystemV2>()?.Initialized != true) return;
        if (_pending != null) { AdvancePending(); return; }
        if (_folders == null && DateTime.UtcNow < _next) return;
        Scan(world, synchronous: false);
        if (_pending != null) AdvancePending();
    }

    private static void CancelPending()
    {
        if (_pending == null) return;
        Dispose(_pending.Handles);
        Active.Remove(_pending.Pack.Id);
        _pending = null; _folders = null; _folderIndex = 0; _next = default;
    }

    private static void AdvancePending(bool synchronous = false)
    {
        var job = _pending;
        if (job == null) return;
        if (!ReferenceEquals(Worlds.Server, job.World) || !job.World.IsCreated || job.Epoch != WorldEpoch.Current)
        {
            CancelPending();
            return;
        }
        // At most 50 slices/sec, including rollback: eight operations per slice gives
        // headroom under the definition/placeholder channel's 1024-packet window even
        // when a replacement emits two packets. Never catch up missed slices in a burst.
        if (!synchronous)
        {
            long now = Stopwatch.GetTimestamp();
            if (now < _nextActivationTick) return;
            _nextActivationTick = now + Stopwatch.Frequency / 50;
        }
        var clock = Stopwatch.StartNew();
        job.Steps.Advance(() => clock.Elapsed.TotalMilliseconds, synchronous);
        if (!job.Steps.Complete) return;
        _pending = null;
        if (job.Steps.Failure == null)
        {
            Active[job.Pack.Id] = new Loaded(job.Pack, job.Owner, job.Handles);
            Plugin.Logger?.LogInfo("Content pack registered: " + job.Pack.Id + " " + job.Pack.Version + "; items=" + job.Pack.Document.Items.Count);
        }
        else
        {
            if (job.Previous != null && job.Steps.RollbackFailure == null)
                Active[job.Pack.Id] = job.Previous with { Handles = job.Handles };
            else
            {
                Dispose(job.Handles); Active.Remove(job.Pack.Id);
            }
            string error = job.Pack.Id + ": " + job.Steps.Failure.Message;
            if (job.Steps.RollbackFailure != null) error += "; rollback failed: " + job.Steps.RollbackFailure.Message;
            if (Warned.Add(error)) Plugin.Logger?.LogWarning("Content pack activation failed: " + error);
        }
    }

    // The existing deserialize prefix guarantees native prefabs and the item-key table
    // are ready, but Initialized is still false. Register before ApplyPending so saved
    // unique items can resolve their custom GUIDs during deserialization.
    internal static void RegisterBeforeDeserialize()
    {
        if (!Plugin.IsServerProcess) return;
        var world = Worlds.Server;
        if (world == null || !world.IsCreated) return;
        if (_pending != null && (!ReferenceEquals(world, _pending.World) || _pending.Epoch != WorldEpoch.Current)) CancelPending();
        AdvancePending(synchronous: true);
        _folders = null; _folderIndex = 0;
        Scan(world, synchronous: true);
    }

    private static void Scan(World world, bool synchronous)
    {
        try
        {
            if (!Directory.Exists(Root))
            {
                _folders = null; _folderIndex = 0; _next = DateTime.UtcNow.AddSeconds(5);
                return; // Missing root may be a deployment/mount interruption.
            }
            if ((File.GetAttributes(Root) & FileAttributes.ReparsePoint) != 0) throw new IOException("Content root cannot be a link.");
            string[] folders = synchronous ? Directory.GetDirectories(Root) : (_folders ??= Directory.GetDirectories(Root));
            if (folders.Length > 128) throw new IOException("Content pack directory limit exceeded.");
            string[] selected = synchronous ? folders : _folderIndex < folders.Length ? new[] { folders[_folderIndex++] } : Array.Empty<string>();
            if (!synchronous && _folderIndex >= folders.Length)
            { _folders = null; _folderIndex = 0; _next = DateTime.UtcNow.AddSeconds(5); }
            foreach (var folder in selected)
            {
                try
                {
                    string manifest = Path.Combine(folder, "pack.json");
                    string removal = Path.Combine(folder, "pack.removed");
                    if (!File.Exists(manifest) && File.Exists(removal))
                    {
                        if (!ItemRemovalRuntime.FlushPendingRegistrations())
                            throw new IOException("Pending item ownership could not be saved before removal.");
                        if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0
                            || (File.GetAttributes(removal) & FileAttributes.ReparsePoint) != 0)
                            throw new IOException("Pack removal cannot use links.");
                        // This also records an explicit removal made while the server was stopped.
                        if (!ItemOwnerLedger.RequestRemovalForFile(Paths.BepInExRootPath, manifest))
                            throw new IOException("Pack removal intent could not be saved.");
                        continue;
                    }
                    var pack = DataPackManifest.Read(folder);
                    if (!string.Equals(Path.GetFileName(folder), pack.Id, StringComparison.Ordinal))
                        throw new InvalidDataException("Pack directory must equal its manifest ID.");
                    if (Active.TryGetValue(pack.Id, out var previous) && previous.Pack.Sha256 == pack.Sha256
                        && previous.Pack.Version == pack.Version) continue;
                    if (HasBonuses(pack) && !Progenitor.Networking.Handshake.ItemStatClientsCompatible())
                        throw new InvalidOperationException("Connected clients need item-stat synchronization support before this pack can activate.");
                    foreach (var item in pack.Document.Items)
                    {
                        PrefabGUID donor;
                        if (item.CloneOfGuidHash.HasValue) donor = new PrefabGUID(item.CloneOfGuidHash.Value);
                        else if (!PrefabLookup.TryGetGuidByName(world, item.CloneOfName!, out donor))
                            throw new InvalidDataException("Item donor is not available: " + item.CloneOfName);
                        if (!PrefabLookup.TryGetEntity(world, donor, out var entity)
                            || !world.EntityManager.HasComponent(entity, ComponentIndex.Of<ItemData>(ComponentType.AccessMode.ReadOnly)))
                            throw new InvalidDataException("Clone donor must be a registered item.");
                        if (item.StatBonuses is { } b && (b.MaxHealth != 0 || b.PhysicalPower != 0 || b.SpellPower != 0)
                            && !world.EntityManager.HasComponent(entity, ComponentIndex.Of<EquippableData>(ComponentType.AccessMode.ReadOnly)))
                            throw new InvalidDataException("Nonzero stat bonuses require an equippable donor.");
                    }
                    var replacements = pack.Document.Items.ToDictionary(i => i.Id, StringComparer.Ordinal);
                    if (previous != null)
                        foreach (var old in previous.Pack.Document.Items)
                        {
                            if (!replacements.TryGetValue(old.Id, out var replacement) || replacement.CloneOfName != old.CloneOfName || replacement.CloneOfGuidHash != old.CloneOfGuidHash)
                                throw new InvalidDataException("Live pack updates must retain existing item IDs and donors; use explicit removal for retirement.");
                        }
                    var proposed = pack.Document.Items.Select(i =>
                        new DataPackItemIdentity(i.Id, PrefabGUID.CalculateGuidHash(i.Id))).ToArray();
                    var registered = PrefabRegistry.Definitions.Select(d =>
                        new RegisteredItemIdentity(d.Id, d.Guid.GuidHash, d.Owner)).ToArray();
                    if (!DataPackRegistrationRules.TryValidate(proposed, registered, previous?.Owner, out var collision))
                        throw new InvalidDataException(collision);
                    if (!PrefabRegistry.TryCheckDataPackAdmission(world, proposed, out var admissionError))
                        throw new InvalidDataException(admissionError);
                    var owner = previous?.Owner ?? new DataPackOwner(pack.Id, Path.Combine(folder, "pack.json"));
                    _pending = new Activation(pack, owner, previous, world);
                    // Keep active ownership tied to the live handle map while metadata remains the
                    // previously committed pack. An activation may temporarily mix its definitions.
                    Active[pack.Id] = new Loaded(previous?.Pack ?? pack, owner, _pending.Handles);
                    if (synchronous) AdvancePending(synchronous: true);
                    else break;
                }
                catch (Exception e)
                {
                    string error = folder + ": " + e.Message;
                    if (Warned.Add(error)) Plugin.Logger?.LogWarning("Content pack refused; existing definition retained where possible: " + error);
                }
            }
            // Removal is explicit: administrators rename pack.json to pack.removed. A transient
            // missing or invalid file never authorizes deleting inventory.
            if (_pending != null) return;
            foreach (var loaded in Active.Values.ToArray())
            {
                string directory = Path.GetDirectoryName(loaded.Owner.ManifestPath)!;
                if (!synchronous && !selected.Contains(directory, StringComparer.OrdinalIgnoreCase)) continue;
                if (!Directory.Exists(directory) || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) continue;
                string removed = Path.Combine(directory, "pack.removed");
                if (File.Exists(loaded.Owner.ManifestPath) || !File.Exists(removed)) continue;
                if ((File.GetAttributes(removed) & FileAttributes.ReparsePoint) != 0) continue;
                if (!ItemRemovalRuntime.CaptureAndFlushRegistrations()) continue;
                var ledgerPath = Path.Combine(Paths.BepInExRootPath, ItemOwnerLedger.RelativeFileName);
                var ledger = ItemOwnerLedger.Load(ledgerPath);
                if (!ledger.MarkRemoved("content." + loaded.Pack.Id) || !ledger.Save(ledgerPath)) continue;
                Dispose(loaded.Handles); Active.Remove(loaded.Pack.Id);
            }
        }
        catch (Exception e)
        {
            _folders = null; _folderIndex = 0; _next = DateTime.UtcNow.AddSeconds(5);
            if (Warned.Add(e.Message)) Plugin.Logger?.LogWarning("Content pack scan deferred: " + e.Message);
        }
    }

    private static IDisposable RegisterItem(ContentItemSpec item, DataPackOwner owner)
    {
        // Register/undo callbacks may have changed ownership after the per-step check.
        // Never let the registry's general replacement behavior overwrite another owner.
        ValidateCurrentOwner(item, owner);
        var builder = PrefabRegistry.DefineItem(item.Id);
        if (item.CloneOfGuidHash.HasValue) builder.CloneOf(new PrefabGUID(item.CloneOfGuidHash.Value));
        else builder.CloneOf(item.CloneOfName!);
        if (item.Name != null) builder.Named(item.Name);
        if (item.Description != null) builder.Described(item.Description);
        if (item.Stack.HasValue) builder.MaxAmount(item.Stack.Value);
        builder.StatBonuses(item.StatBonuses ?? ItemStatBonuses.Empty);
        var handle = builder.Register(owner);
        if (!PrefabRegistry.Definitions.Any(d => d.Id == item.Id && ReferenceEquals(d.Owner, owner)))
        {
            handle.Dispose();
            throw new InvalidOperationException("Item registration was refused: " + item.Id);
        }
        return handle;
    }

    private static void ValidateCurrentOwner(ContentItemSpec item, DataPackOwner owner)
    {
        var proposed = new[] { new DataPackItemIdentity(item.Id, PrefabGUID.CalculateGuidHash(item.Id)) };
        var registered = PrefabRegistry.Definitions.Select(d =>
            new RegisteredItemIdentity(d.Id, d.Guid.GuidHash, d.Owner)).ToArray();
        if (!DataPackRegistrationRules.TryValidate(proposed, registered, owner, out var error))
            throw new InvalidOperationException(error);
    }

    private static void Dispose(Dictionary<string, IDisposable> handles)
    {
        foreach (var handle in handles.Values.Reverse()) handle.Dispose();
        handles.Clear();
    }
}
