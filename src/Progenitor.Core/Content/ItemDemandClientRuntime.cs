using System;
using System.Collections.Generic;
using ProjectM;
using ProjectM.Network;
using ProjectM.UI;
using Progenitor.Ecs;
using Progenitor.Ui;
using Stunlock.Core;
using Unity.Entities;

namespace Progenitor.Content;

/// <summary>Read-only, bounded demand sampling; missing occupied rows remain visibly pending.</summary>
internal sealed class ItemDemandClientRuntime : IDisposable
{
    private readonly Action<IReadOnlyList<int>, IReadOnlyList<int>> _demand;
    private double _nextScan;
    private double _nextRequest;
    private IUiPanel? _notice;
    private IUiLabel? _label;
    private World? _world;
    private bool _disposed;
    private int _playerCursor;
    private int _containerCursor;

    internal ItemDemandClientRuntime(Action<IReadOnlyList<int>, IReadOnlyList<int>> demand) =>
        _demand = demand ?? throw new ArgumentNullException(nameof(demand));

    internal void Tick(World world, double now)
    {
        if (_disposed) return;
        if (world == null || !world.IsCreated) { Reset(); return; }
        if (!ReferenceEquals(_world, world)) { Reset(); _world = world; }
        if (now < _nextScan) return;
        _nextScan = now + 0.25;
        try
        {
            var em = world.EntityManager;
            var gameData = world.GetExistingSystemManaged<GameDataSystem>();
            var prefabs = world.GetExistingSystemManaged<PrefabCollectionSystem>();
            if (gameData == null || prefabs == null) { HideNotice(); return; }
            var locals = Query.Entities(em, includeDisabled: false, includeSystems: false,
                ComponentIndex.Of<LocalCharacter>(ComponentType.AccessMode.ReadOnly));
            // LocalCharacter is the empty ProjectM.Network tag on the character,
            // not a singleton containing a character reference (same seam as input).
            if (locals.Count != 1) { HideNotice(); return; }
            var player = new List<int>();
            var container = new List<int>();
            int equippedSlots = ScanEquipment(em, locals[0], gameData, prefabs, player);
            int playerSlots = ScanOwner(em, locals[0], gameData, prefabs, player);
            int containerSlots = 0;
            // Installed client dump: ContainerMenuMapper target and live container submenu.
            // Read fields only: no private native method invocation or hook ABI.
            var mapper = world.GetExistingSystemManaged<ContainerMenuMapper>();
            if (mapper?._ContainerSubMenu != null && mapper._ContainerSubMenu.gameObject.activeInHierarchy)
                containerSlots = ScanOwner(em, mapper._Target, gameData, prefabs, container);
            int pending = playerSlots + equippedSlots + containerSlots;
            if (pending == 0) { HideNotice(); return; }
            if (ItemDefinitionDelivery.DebugLoadingNotice) ShowNotice(pending, playerSlots, equippedSlots, containerSlots);
            else HideNotice();
            if (now >= _nextRequest)
            {
                _nextRequest = now + 1;
                _demand(Batch(player, ref _playerCursor), Batch(container, ref _containerCursor));
            }
        }
        catch (Exception e)
        {
            // A destroyed scene/world must not leave a loading label over the next scene.
            HideNotice();
            Plugin.Logger?.LogDiag("Item demand scan unavailable: " + e.Message);
        }
    }

    private static bool Has<T>(EntityManager em, Entity entity) => entity != Entity.Null && em.Exists(entity)
        && em.HasComponent(entity, ComponentIndex.Of<T>(ComponentType.AccessMode.ReadOnly));

    private static bool Known(EntityManager em, PrefabGUID guid, GameDataSystem data,
        PrefabCollectionSystem prefabs) => guid.GuidHash != 0
        && data.ItemHashLookupMap.TryGetValue(guid, out var item)
        && item.Entity != Entity.Null && em.Exists(item.Entity)
        && prefabs._PrefabGuidToEntityMap.TryGetValue(guid, out var prefab)
        && prefab != Entity.Null && em.Exists(prefab);

    private static int ScanEquipment(EntityManager em, Entity character, GameDataSystem data,
        PrefabCollectionSystem prefabs, List<int> missing)
    {
        if (!Has<Equipment>(em, character)) return 0;
        // Typed accessor is the existing qualified route for this bool-bearing component.
        // SlotId remains authoritative while SlotEntity is still awaiting network sync.
        var e = em.GetComponentData<Equipment>(character);
        var slots = new[] { e.ArmorHeadgearSlot, e.ArmorChestSlot, e.WeaponSlot, e.GrimoireSlot,
            e.ArmorFootgearSlot, e.ArmorLegsSlot, e.CloakSlot, e.ArmorGlovesSlot, e.BagSlot,
            e.ChestCosmeticSlot, e.FootgearCosmeticSlot, e.LegsCosmeticSlot,
            e.CloakCosmeticSlot, e.GlovesCosmeticSlot };
        int pending = 0;
        foreach (var slot in slots)
        {
            int guid = slot.SlotId.GuidHash;
            if (guid == 0 || Known(em, slot.SlotId, data, prefabs)) continue;
            pending++;
            if (!missing.Contains(guid)) missing.Add(guid);
        }
        return pending;
    }

    private static unsafe int ScanOwner(EntityManager em, Entity owner, GameDataSystem data,
        PrefabCollectionSystem prefabs, List<int> missing)
    {
        if (!em.Exists(owner)) return 0;
        if (Has<InventoryBuffer>(em, owner)) return ScanBag(em, owner, data, prefabs, missing);
        if (!Has<InventoryInstanceElement>(em, owner)) return 0;
        int index = ComponentIndex.IndexOf<InventoryInstanceElement>();
        int count = em.GetBufferLength(owner, index);
        if (count < 1 || count > 8) return 0;
        byte* raw = (byte*)em.GetBufferRawRO(owner, index);
        if (raw == null) return 0;
        int pending = 0;
        var seen = new HashSet<Entity>();
        for (int i = 0; i < count; i++)
        {
            // Same live-qualified stride/offset as Items.CopyBags; never instantiate
            // GetBuffer<InventoryInstanceElement> over this game value type.
            byte* row = raw + i * 48;
            var bag = new Entity { Index = *(int*)(row + 16), Version = *(int*)(row + 20) };
            if (seen.Add(bag) && Has<InventoryBuffer>(em, bag))
                pending += ScanBag(em, bag, data, prefabs, missing);
        }
        return pending;
    }

    private static int ScanBag(EntityManager em, Entity bag, GameDataSystem data,
        PrefabCollectionSystem prefabs, List<int> missing)
    {
        var rows = em.GetBuffer<InventoryBuffer>(bag);
        if (rows.Length < 1 || rows.Length > 1024) return 0;
        int pending = 0;
        var seen = new HashSet<int>(missing);
        for (int i = 0; i < rows.Length; i++)
        {
            var row = rows[i];
            if (row.Amount <= 0) continue;
            // A positive amount is occupied even before its item key resolves to a GUID.
            // It cannot yet produce a request, but must never be presented as empty.
            if (row.ItemType.GuidHash == 0) { pending++; continue; }
            if (Known(em, row.ItemType, data, prefabs)) continue;
            pending++;
            int guid = row.ItemType.GuidHash;
            if (seen.Add(guid)) missing.Add(guid);
        }
        return pending;
    }

    private static IReadOnlyList<int> Batch(List<int> guids, ref int cursor)
    {
        if (guids.Count <= 64) { cursor = 0; return guids; }
        var batch = new List<int>(64);
        cursor %= guids.Count;
        for (int i = 0; i < 64; i++) batch.Add(guids[(cursor + i) % guids.Count]);
        cursor = (cursor + 64) % guids.Count;
        return batch;
    }

    private void ShowNotice(int pending, int player, int equipped, int container)
    {
        if (_notice == null || _notice.IsDisposed)
        {
            _notice = Ui.Ui.Panel("Loading items", owner: this, pluginId: "progenitor.item-demand");
            _notice.Footer = "";
            _label = _notice.Label("Loading items…");
        }
        if (_notice is not UiPanelHandle host || host.RuntimePanel == null
            || _label is not UiHandle label || label.Runtime == null
            || Ui.Ui.CurrentRuntime() is not UnityUiRuntime runtime) return;
        _label.Text = "Loading items… " + pending + " occupied " + (pending == 1 ? "slot" : "slots")
            + " pending\nInventory: " + player + " · Equipment: " + equipped
            + (container > 0 ? " · Open container: " + container : "");
        runtime.ShowItemLoadingNotice(host.RuntimePanel, label.Runtime);
    }

    private void HideNotice()
    {
        if (_label != null) _label.Visible = false;
        if (_notice is UiPanelHandle host && host.RuntimePanel != null)
            Ui.Ui.CurrentRuntime().Hide(host.RuntimePanel);
    }

    internal void Reset()
    {
        _notice?.Dispose();
        _notice = null;
        _label = null;
        _world = null;
        _nextScan = _nextRequest = 0;
        _playerCursor = _containerCursor = 0;
    }

    public void Dispose() { if (_disposed) return; Reset(); _disposed = true; }
}
