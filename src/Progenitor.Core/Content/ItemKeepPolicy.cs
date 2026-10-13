using ProjectM;
using ProjectM.Shared;
using Unity.Entities;

namespace Progenitor.Content;

/// <summary>
/// Opt-in keep rules for an owned item clone: <c>DefineItem(...).BloodBound()</c> and
/// <c>.Indestructible()</c>. Decisions are in <see cref="ItemKeepRules"/>; this class only reads
/// and writes the two components through the typed accessors Core already uses for them
/// (<c>GetComponentData&lt;ItemData&gt;</c> on the clone, <c>GetComponentData&lt;Durability&gt;</c>
/// as in <c>Items.Grant</c>'s durability option).
/// </summary>
internal sealed class ItemKeepPolicy
{
    internal ItemKeepPolicy(bool bloodBound, bool indestructible)
    {
        BloodBound = bloodBound;
        Indestructible = indestructible;
    }

    internal bool BloodBound { get; }
    internal bool Indestructible { get; }

    internal ItemKeepPolicy With(bool bloodBound = false, bool indestructible = false)
        => new(BloodBound || bloodBound, Indestructible || indestructible);

    /// <summary>
    /// Clone or map row category. <paramref name="policy"/> null restores the two policy bits
    /// from <paramref name="donor"/>.
    /// </summary>
    internal static void ApplyCategory(ref ItemData data, ItemKeepPolicy? policy, in ItemData donor)
    {
        data.ItemCategory = (ItemCategory)ItemKeepRules.Category(
            (long)data.ItemCategory,
            (long)donor.ItemCategory,
            policy?.BloodBound == true,
            policy?.Indestructible == true);
    }

    /// <summary>Indestructible values on one entity's <c>Durability</c>. False when it has none.</summary>
    internal static bool KeepDurability(EntityManager em, Entity item)
    {
        if (item == Entity.Null || !em.Exists(item) || !em.HasComponent<Durability>(item))
            return false;
        var durability = em.GetComponentData<Durability>(item);
        int lossType = (int)durability.LossType;
        float factor = durability.TakeDamageDurabilityLossFactor;
        bool destroy = durability.DestroyItemWhenBroken;
        float value = durability.Value;
        if (!ItemKeepRules.KeepDurability(ref lossType, ref factor, ref destroy, ref value, durability.MaxDurability))
            return false;
        durability.LossType = (DurabilityLossType)lossType;
        durability.TakeDamageDurabilityLossFactor = factor;
        durability.DestroyItemWhenBroken = destroy;
        durability.Value = value;
        em.SetComponentData(item, durability);
        return true;
    }

    /// <summary>
    /// Copy the donor's three loss rules back onto a clone that no longer asks for
    /// <see cref="Indestructible"/>. Value and maximum stay the clone's own.
    /// </summary>
    internal static bool RestoreDurabilityRules(EntityManager em, Entity clone, Entity donor)
    {
        if (clone == Entity.Null || donor == Entity.Null || clone == donor
            || !em.Exists(clone) || !em.Exists(donor)
            || !em.HasComponent<Durability>(clone) || !em.HasComponent<Durability>(donor))
            return false;
        var mine = em.GetComponentData<Durability>(clone);
        var theirs = em.GetComponentData<Durability>(donor);
        if (mine.LossType == theirs.LossType
            && mine.TakeDamageDurabilityLossFactor == theirs.TakeDamageDurabilityLossFactor
            && mine.DestroyItemWhenBroken == theirs.DestroyItemWhenBroken)
            return false;
        mine.LossType = theirs.LossType;
        mine.TakeDamageDurabilityLossFactor = theirs.TakeDamageDurabilityLossFactor;
        mine.DestroyItemWhenBroken = theirs.DestroyItemWhenBroken;
        em.SetComponentData(clone, mine);
        return true;
    }

    /// <summary>
    /// One live item instance (server): add the policy's category bits to its own
    /// <c>ItemData</c> and make it indestructible. Returns whether anything was written.
    ///
    /// <para>The <c>Durability</c> write is the one with a gameplay effect: combat wear reads the
    /// instance's <c>LossType</c>. The category write is consistency only: the death drop and the
    /// death drain read <c>ItemCategory</c> from the lookup row, not from the instance. It is a
    /// typed write of one field and is kept so a reader of the instance sees the same rules.</para>
    /// </summary>
    internal bool ApplyToInstance(EntityManager em, Entity item)
    {
        if (item == Entity.Null || !em.Exists(item) || !em.HasComponent(item, Ecs.ComponentIndex.Of<ItemData>(ComponentType.AccessMode.ReadOnly)))
            return false;
        bool changed = false;
        var data = em.GetComponentData<ItemData>(item);
        long before = (long)data.ItemCategory;
        long after = ItemKeepRules.InstanceCategory(before, BloodBound, Indestructible);
        if (after != before)
        {
            data.ItemCategory = (ItemCategory)after;
            em.SetComponentData(item, data);
            changed = true;
        }
        if (Indestructible && KeepDurability(em, item))
            changed = true;
        return changed;
    }

    public override string ToString()
        => (BloodBound ? "blood-bound" : "") + (BloodBound && Indestructible ? "+" : "")
            + (Indestructible ? "indestructible" : "");
}
