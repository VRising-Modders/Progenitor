using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using BepInEx.Logging;
using ProjectM;
using ProjectM.Network;
using ProjectM.Transmog;
using Progenitor.Ecs;
using Unity.Entities;

namespace Progenitor.Dye;

/// <summary>
/// The game-bound reads and the send.
///
/// <para><b>Reads.</b> The local character's <c>Equipment</c> through the typed
/// <c>GetComponentData&lt;Equipment&gt;</c> (the struct carries bools; the wearable code reads it the
/// same way), each worn item's <c>TransmogData</c> through the guarded raw read, and — only with the
/// experimental <c>MatchPalettes</c> setting — the item's <c>TransmogAssetKeyBlobData</c> blob.</para>
///
/// <para><b>Send.</b> The same <c>ChangeTransmogColorEvent</c> the color picker sends
/// (<c>TransmogContainer.HandleInput</c>, via
/// <c>NetworkEvents.SendEvent&lt;ChangeTransmogColorEvent&gt;</c>): item NetworkId @0x0, the
/// character NetworkId @0xC, the equipment type @0x18, the new index @0x1C. It is built with the
/// client-to-server recipe the Dev bot events use (an entity with the event,
/// <c>NetworkEventType</c> and <c>SendNetworkEventTag</c>, fields poked one by one, offsets checked
/// against the interop struct first). No game method is called. The server
/// (<c>TransmogEventsSystem</c> lambda 0) checks that the character is the
/// sender, that the item is equipped in that slot (<c>ValidateItemIsEquipped</c>: PrefabGUID equals
/// the slot's SlotId and the entity matches) and that the index is below
/// <c>TransmogData.NumColorOptions</c>; there is no rate limit and no per-color unlock check in that
/// handler, so the planner's checks are the game's.</para>
/// </summary>
internal static unsafe class DyeSender
{
    private const int NetworkIdSize = 12;

    /// <summary>The local character (this client's own), or false.</summary>
    internal static bool TryLocalCharacter(EntityManager em, out Entity character) => LocalCharacters.TryGet(em, out character);

    /// <summary>The five dyeable slots of <paramref name="character"/>, stat and cosmetic piece each.</summary>
    internal static List<DyeSlot> ReadSlots(EntityManager em, Entity character, bool palettes, ManualLogSource log)
    {
        var slots = new List<DyeSlot>(DyeEquipmentTypes.Dyeable.Length);
        var equipment = em.GetComponentData<Equipment>(character);
        foreach (int type in DyeEquipmentTypes.Dyeable)
        {
            var (stat, cosmetic) = SlotsOf(equipment, type);
            slots.Add(new DyeSlot(type, Item(em, stat, palettes, log), Item(em, cosmetic, palettes, log)));
        }

        return slots;
    }

    private static (EquipmentSlot Stat, EquipmentSlot Cosmetic) SlotsOf(Equipment e, int type) => type switch
    {
        DyeEquipmentTypes.Chest => (e.ArmorChestSlot, e.ChestCosmeticSlot),
        DyeEquipmentTypes.Footgear => (e.ArmorFootgearSlot, e.FootgearCosmeticSlot),
        DyeEquipmentTypes.Legs => (e.ArmorLegsSlot, e.LegsCosmeticSlot),
        DyeEquipmentTypes.Cloak => (e.CloakSlot, e.CloakCosmeticSlot),
        DyeEquipmentTypes.Gloves => (e.ArmorGlovesSlot, e.GlovesCosmeticSlot),
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "not a dyeable slot"),
    };

    private static DyeItem Item(EntityManager em, EquipmentSlot slot, bool palettes, ManualLogSource log)
    {
        // The game's rule (TryGetEquipmentOrCosmeticSlotFromEquipmentType): the entity counts only when
        // it is not waiting for sync and not null.
        var entity = slot.SlotEntity._WaitingForSync ? Entity.Null : slot.SlotEntity._Entity;
        if (entity == Entity.Null || !em.Exists(entity))
        {
            // The server counts a slot as filled by its SlotId; keep that, so a transmog still waiting for
            // sync is not mistaken for an empty cosmetic slot.
            return slot.SlotId.GuidHash != 0 ? DyeItem.Pending(slot.HideEquipment) : DyeItem.None;
        }
        bool has;
        TransmogData data;
        try { has = entity.TryRead(em, out data); }
        catch (Exception e)
        {
            // A size mismatch is a layout bug, not "not dyeable": say so once per apply and skip it.
            log.LogWarning("Dye all: TransmogData read refused on " + entity.Index + ":" + entity.Version + ": " + e.Message);
            has = false;
            data = default;
        }

        var keys = has && palettes ? ReadPalettes(em, entity, log) : null;
        return new DyeItem(entity.Index, entity.Version, present: true, has,
            has ? data.ActiveTransmogIndex : (byte)0, has ? data.NumColorOptions : (byte)0, keys, slot.HideEquipment,
            broken: slot.IsBroken);
    }

    /// <summary>
    /// Experimental (setting <c>MatchPalettes</c>): the item's palette rows from its
    /// <c>TransmogAssetKeyBlobData</c>. Offsets and the stride come from <c>Progenitor.Layout</c>
    /// (client dump 506457-506479); the array must fit inside the blob's own header length or the
    /// read is refused. Null when the item has no blob or anything does not check out.
    /// </summary>
    internal static IReadOnlyList<DyePaletteKey>? ReadPalettes(EntityManager em, Entity item, ManualLogSource log)
    {
        try
        {
            var type = ComponentIndex.Of<TransmogAssetKeyBlobData>(ComponentType.AccessMode.ReadOnly);
            if (!em.Exists(item) || !em.HasComponent(item, type)) return null;
            byte* component = (byte*)em.GetComponentDataRawRO(item, type.TypeIndex);
            if (component == null) return null;
            byte* blob = *(byte**)(component + Layout.TransmogAssetKeyBlobDataBlobOffset);
            if (blob == null) return null;
            int blobLength = *(int*)(blob - Layout.BlobAssetHeaderBytes + Layout.BlobAssetHeaderLengthOffset);
            byte* array = blob + Layout.TransmogAssetKeyBlobGuidsOffset;
            int relative = *(int*)array;
            int count = *(int*)(array + sizeof(int));
            if (!DyeRing.PaletteArrayFits(blobLength, Layout.TransmogAssetKeyBlobGuidsOffset, relative, count, Layout.TransmogAssetKeyStride))
            {
                log.LogWarning("Dye all: palette blob of " + item.Index + ":" + item.Version + " does not check out (length "
                               + blobLength + ", offset " + relative + ", count " + count + "); using the raw index.");
                return null;
            }

            var keys = new List<DyePaletteKey>(count);
            byte* first = array + relative;
            for (int i = 0; i < count; i++)
            {
                byte* row = first + i * Layout.TransmogAssetKeyStride;
                keys.Add(new DyePaletteKey(
                    *(int*)(row + Layout.TransmogAssetKeyAssetGuidOffset),
                    row[Layout.TransmogAssetKeyStartIndexOffset],
                    row[Layout.TransmogAssetKeyNumColorsOffset]));
            }

            return keys;
        }
        catch (Exception e)
        {
            log.LogWarning("Dye all: palette read threw: " + e.Message);
            return null;
        }
    }

    /// <summary>Send one <c>ChangeTransmogColorEvent</c> per target. Returns how many left.</summary>
    internal static int Send(EntityManager em, Entity character, DyePlan plan, ManualLogSource log)
    {
        if (plan.Targets.Count == 0) return 0;
        CheckEventLayout();
        if (!TryNetworkId(em, character, out var characterId))
        {
            log.LogWarning("Dye all: the local character has no NetworkId yet; nothing sent.");
            return 0;
        }

        int sent = 0;
        foreach (var target in plan.Targets)
        {
            var item = new Entity { Index = target.Item.Index, Version = target.Item.Version };
            if (!TryNetworkId(em, item, out var itemId))
            {
                log.LogWarning("Dye all: " + DyeEquipmentTypes.Name(target.EquipmentType) + " item has no NetworkId; skipped.");
                continue;
            }

            try
            {
                SendOne(em, itemId, characterId, target.EquipmentType, target.NewIndex);
                sent++;
            }
            catch (Exception e)
            {
                log.LogWarning("Dye all: sending " + DyeEquipmentTypes.Name(target.EquipmentType) + " threw: " + e.Message);
            }
        }

        return sent;
    }

    private readonly struct RawId
    {
        internal RawId(int a, int b, int c)
        {
            A = a;
            B = b;
            C = c;
        }

        internal int A { get; }

        internal int B { get; }

        internal int C { get; }
    }

    private static bool TryNetworkId(EntityManager em, Entity entity, out RawId id)
    {
        id = default;
        var type = ComponentIndex.Of<NetworkId>(ComponentType.AccessMode.ReadOnly);
        if (!em.Exists(entity) || !em.HasComponent(entity, type)) return false;
        if (Marshal.SizeOf<NetworkId>() != NetworkIdSize)
            throw new InvalidOperationException("NetworkId size differs from the dump (12); refusing the raw copy");
        int* src = (int*)em.GetComponentDataRawRO(entity, type.TypeIndex);
        if (src == null) return false;
        id = new RawId(src[0], src[1], src[2]);
        return true;
    }

    /// <summary>Refuse to poke anything unless the interop struct agrees with the dump offsets.</summary>
    private static void CheckEventLayout()
    {
        Expect(nameof(ChangeTransmogColorEvent.ItemNetworkId), Layout.ChangeTransmogColorEventItemNetworkIdOffset);
        Expect(nameof(ChangeTransmogColorEvent.EquipmentNetworkId), Layout.ChangeTransmogColorEventEquipmentNetworkIdOffset);
        Expect(nameof(ChangeTransmogColorEvent.EquipmentType), Layout.ChangeTransmogColorEventEquipmentTypeOffset);
        Expect(nameof(ChangeTransmogColorEvent.NewIndex), Layout.ChangeTransmogColorEventNewIndexOffset);
    }

    private static void Expect(string field, int offset)
    {
        int actual = (int)Marshal.OffsetOf<ChangeTransmogColorEvent>(field);
        if (actual != offset)
            throw new InvalidOperationException("ChangeTransmogColorEvent." + field + " is at 0x" + actual.ToString("X")
                                                + ", expected 0x" + offset.ToString("X") + "; refusing the raw write");
    }

    private static void SendOne(EntityManager em, RawId item, RawId character, int equipmentType, byte newIndex)
    {
        var type = ComponentIndex.Of<ChangeTransmogColorEvent>(ComponentType.AccessMode.ReadWrite);
        var ev = em.CreateEntity(
            type,
            ComponentIndex.Of<NetworkEventType>(ComponentType.AccessMode.ReadWrite),
            ComponentIndex.Of<SendNetworkEventTag>(ComponentType.AccessMode.ReadWrite));
        try
        {
            byte* raw = (byte*)em.GetComponentDataRawRW(ev, type.TypeIndex);
            if (raw == null) throw new InvalidOperationException("GetComponentDataRawRW returned null for ChangeTransmogColorEvent");
            Put(raw + Layout.ChangeTransmogColorEventItemNetworkIdOffset, item);
            Put(raw + Layout.ChangeTransmogColorEventEquipmentNetworkIdOffset, character);
            *(int*)(raw + Layout.ChangeTransmogColorEventEquipmentTypeOffset) = equipmentType;
            raw[Layout.ChangeTransmogColorEventNewIndexOffset] = newIndex;
            // NetworkEventType has bools, so it is not C# unmanaged: typed SetComponentData, as the
            // character-create and bot events do.
            em.SetComponentData(ev, new NetworkEventType
            {
                EventId = Layout.ChangeTransmogColorEventId, IsAdminEvent = false, IsDebugEvent = false,
            });
        }
        catch
        {
            em.DestroyEntity(ev); // never leave a send-tagged event with EventId 0 behind
            throw;
        }
    }

    private static void Put(byte* at, RawId id)
    {
        *(int*)at = id.A;
        *(int*)(at + 4) = id.B;
        *(int*)(at + 8) = id.C;
    }
}
