using System;
using ProjectM;
using Progenitor.Ecs;
using Stunlock.Core;
using Unity.Entities;

namespace Progenitor.Content;

/// <summary>Opt-in baseline for an owned item clone and its same-GUID live instances.</summary>
internal sealed unsafe class ItemArmorBaseline
{
    internal ItemArmorBaseline(int displayedLevel, bool removeBonuses, bool removeSet)
    {
        if (displayedLevel < 1 || displayedLevel > 100)
            throw new ArgumentOutOfRangeException(nameof(displayedLevel));
        DisplayedLevel = displayedLevel;
        RemoveBonuses = removeBonuses;
        RemoveSet = removeSet;
    }

    internal int DisplayedLevel { get; }
    internal float ArmorLevelSourceValue => checked(DisplayedLevel * 10);
    internal bool RemoveBonuses { get; }
    internal bool RemoveSet { get; }

    internal void Apply(EntityManager em, Entity item)
    {
        if (!em.Exists(item)) throw new InvalidOperationException("Armor baseline item is missing.");
        var equippableType = ComponentIndex.Of<EquippableData>(ComponentType.AccessMode.ReadWrite);
        if (!em.HasComponent(item, equippableType))
            throw new InvalidOperationException("Armor baseline requires EquippableData.");
        var levelType = ComponentIndex.Of<ArmorLevelSource>(ComponentType.AccessMode.ReadWrite);
        var bonusType = ComponentIndex.Of<ModifyUnitStatBuff_DOTS>(ComponentType.AccessMode.ReadWrite);

        // Validate both typed writes before changing the archetype. Retain BuffGuid: it creates the base
        // equip buff that applies the item level and does not carry donor stat rows.
        var equippable = item.Read<EquippableData>(em);
        if (equippable.BuffGuid.GuidHash == 0)
            throw new InvalidOperationException("Armor baseline requires the donor's base equip buff.");
        ValidateWrite<EquippableData>();
        ValidateWrite<ArmorLevelSource>();
        bool hadLevel = em.HasComponent(item, levelType);
        var oldLevel = hadLevel ? item.Read<ArmorLevelSource>(em) : default;
        bool changedSet = RemoveSet && equippable.EquipmentSet.GuidHash != 0;
        bool addedLevel = false;
        try
        {
            if (!hadLevel)
            {
                em.AddComponent(item, levelType);
                addedLevel = true;
            }
            var level = hadLevel ? oldLevel : default;
            level.Level = ArmorLevelSourceValue;
            item.Write(em, in level);

            if (changedSet)
            {
                var withoutSet = equippable;
                withoutSet.EquipmentSet = default;
                item.Write(em, in withoutSet);
            }
            if (RemoveBonuses && em.HasComponent(item, bonusType))
                em.RemoveComponent(item, bonusType);
        }
        catch
        {
            // The buffer removal is last and has no safe raw-content rollback. If it
            // throws, restore the two scalar components where possible, then surface
            // the failure for this item without aborting registration of other items.
            try
            {
                if (changedSet) item.Write(em, in equippable);
                if (addedLevel && em.HasComponent(item, levelType)) em.RemoveComponent(item, levelType);
                else if (hadLevel) item.Write(em, in oldLevel);
            }
            catch { /* Preserve the original failure. */ }
            throw;
        }
    }

    private static void ValidateWrite<T>() where T : unmanaged
    {
        var index = ComponentIndex.IndexOf<T>();
        int ecsSize = TypeManager.GetTypeInfo(index).TypeSize;
        int sizeInChunk = TypeManager.GetTypeInfo(index).SizeInChunk;
        RawWriteGuard.EnsureTypedWrite(ecsSize, sizeof(T), sizeInChunk,
            typeof(T).FullName ?? typeof(T).Name);
    }
}
