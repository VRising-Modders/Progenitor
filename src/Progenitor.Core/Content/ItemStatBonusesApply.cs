using System;
using System.Runtime.InteropServices;
using ProjectM;
using Progenitor.Ecs;
using Unity.Entities;

namespace Progenitor.Content;

/// <summary>Rebuilds owned item stat rows from the unchanged donor plus authored additive bonuses.</summary>
internal static unsafe class ItemStatBonusesApply
{
    // Matching client/server dump: ModifyUnitStatBuff_DOTS ends with a four-byte
    // ModificationId at 0x20. Native stride 0x24, alignment 4; wrapper sizeof is not used.
    internal const int Stride = ItemStatRows.Stride;

    internal static void Apply(EntityManager em, Entity target, Entity donor, ItemStatBonuses bonuses)
    {
        ArgumentNullException.ThrowIfNull(bonuses);
        if (target == donor || target == Entity.Null || donor == Entity.Null || !em.Exists(target) || !em.Exists(donor))
            throw new InvalidOperationException("Item stat bonuses require distinct existing target and donor entities.");
        var type = ComponentIndex.Of<ModifyUnitStatBuff_DOTS>(ComponentType.AccessMode.ReadWrite);
        CheckLayout();
        bool hasBonuses = bonuses.MaxHealth != 0 || bonuses.PhysicalPower != 0 || bonuses.SpellPower != 0;
        if (hasBonuses && !em.HasComponent(target, ComponentIndex.Of<EquippableData>(ComponentType.AccessMode.ReadOnly)))
            throw new InvalidOperationException("Nonzero item stat bonuses require EquippableData.");
        bool hadBuffer = em.HasComponent(target, type);
        byte[] previous = Snapshot(em, target, type);
        byte[] inherited = Snapshot(em, donor, type);
        byte[] authored = ItemStatRows.Build(inherited, bonuses);
        try
        {
            if (authored.Length == 0 && !em.HasComponent(donor, type))
            {
                if (hadBuffer) em.RemoveComponent(target, type);
            }
            else
            {
                if (!hadBuffer) em.AddComponent(target, type);
                Replace(em, target, type, authored);
            }
        }
        catch (Exception original)
        {
            try
            {
                if (!hadBuffer)
                {
                    if (em.HasComponent(target, type)) em.RemoveComponent(target, type);
                }
                else
                {
                    if (!em.HasComponent(target, type)) em.AddComponent(target, type);
                    Replace(em, target, type, previous);
                }
            }
            catch (Exception rollback)
            {
                throw new AggregateException("Item stat bonus write and rollback failed.", original, rollback);
            }
            throw;
        }
    }

    private static byte[] Snapshot(EntityManager em, Entity entity, ComponentType type)
    {
        if (!em.HasComponent(entity, type)) return Array.Empty<byte>();
        int count = em.GetBufferLength(entity, type.TypeIndex);
        if (count < 0 || count > ItemStatRows.MaxRows)
            throw new InvalidOperationException("Item stat row count exceeds the guarded limit.");
        var data = new byte[checked(count * Stride)];
        if (count == 0) return data;
        byte* raw = (byte*)em.GetBufferRawRO(entity, type.TypeIndex);
        if (raw == null) throw new InvalidOperationException("Item stat buffer pointer is null.");
        new ReadOnlySpan<byte>(raw, data.Length).CopyTo(data);
        return data;
    }

    private static void Replace(EntityManager em, Entity target, ComponentType type, byte[] data)
    {
        // A non-null address also permits a zero-row rollback without manufacturing a row.
        byte empty = 0;
        fixed (byte* raw = data)
            UnsafeEntityManagerUtility.SetBufferDataRaw(em, target, type.TypeIndex.Value,
                Stride, 4, data.Length / Stride, data.Length == 0 ? &empty : raw);
        byte[] after = Snapshot(em, target, type);
        if (!after.AsSpan().SequenceEqual(data))
            throw new InvalidOperationException("Item stat buffer write did not read back exactly.");
    }

    private static void CheckLayout()
    {
        // TypeManager.GetTypeInfo's interop return is not a reliable native layout
        // description (observed ElementSize=0, TypeSize=4 for this 36-byte row).
        // Stride comes from the shared dump-checked Layout constants, like the other
        // native buffer adapters. Wrapper field checks below are an additional guard,
        // not the source of the native stride.
        Expect(nameof(ModifyUnitStatBuff_DOTS.AttributeCapType), Layout.ItemStatCapOffset);
        Expect(nameof(ModifyUnitStatBuff_DOTS.StatType), Layout.ItemStatTypeOffset);
        Expect(nameof(ModifyUnitStatBuff_DOTS.ModificationType), Layout.ItemStatOperationOffset);
        Expect(nameof(ModifyUnitStatBuff_DOTS.Value), Layout.ItemStatValueOffset);
        Expect(nameof(ModifyUnitStatBuff_DOTS.SoftCapValue), Layout.ItemStatSoftCapOffset);
        Expect(nameof(ModifyUnitStatBuff_DOTS.Modifier), Layout.ItemStatModifierOffset);
        Expect(nameof(ModifyUnitStatBuff_DOTS.IncreaseByStacks), Layout.ItemStatStacksOffset);
        Expect(nameof(ModifyUnitStatBuff_DOTS.ValueByStacks), Layout.ItemStatStackValueOffset);
        Expect(nameof(ModifyUnitStatBuff_DOTS.Priority), Layout.ItemStatPriorityOffset);
        Expect(nameof(ModifyUnitStatBuff_DOTS.Id), Layout.ItemStatIdOffset);
    }

    private static void Expect(string field, int expected)
    {
        if ((int)Marshal.OffsetOf<ModifyUnitStatBuff_DOTS>(field) != expected)
            throw new InvalidOperationException("Item stat field " + field + " differs from the verified native layout.");
    }
}
