namespace Progenitor.Content;

/// <summary>
/// Game-free half of <c>ItemKeepPolicy</c> (<c>DefineItem(...).BloodBound()</c> /
/// <c>.Indestructible()</c>): which <c>ItemData.ItemCategory</c> bits and which
/// <c>Durability</c> rule values a kept item gets. Linked into the test project.
///
/// <para>The death drop reads <c>ItemCategory</c> from the item lookup map row of the slot's
/// prefab, not from the item entity: a slot whose row has <c>BloodBound</c> or
/// <c>SoulBound</c> is kept, and the death durability drain runs only for rows with
/// <c>LoseDurabilityOnDeath</c>. Damage taken and dealt only wear an item whose
/// <c>Durability.LossType</c> has the matching flag.</para>
/// </summary>
internal static class ItemKeepRules
{
    /// <summary><c>ProjectM.ItemCategory.BloodBound</c> (game metadata, <c>ProjectM.ItemCategory</c>).</summary>
    internal const long BloodBoundBit = 8;

    /// <summary><c>ProjectM.ItemCategory.LoseDurabilityOnDeath</c> (game metadata, <c>ProjectM.ItemCategory</c>).</summary>
    internal const long LoseDurabilityOnDeathBit = 2048;

    /// <summary><c>ProjectM.Shared.DurabilityLossType.None</c> (game metadata, <c>ProjectM.Shared.DurabilityLossType</c>).</summary>
    internal const int LossTypeNone = 0;

    /// <summary>
    /// The category a clone gets. Only the two policy bits are decided here; every other bit is
    /// the clone's own. An option that is off takes the bit from the donor, so dropping an option
    /// and re-registering the same id restores the donor's behavior on the clone.
    /// </summary>
    internal static long Category(long current, long donor, bool bloodBound, bool indestructible)
    {
        long result = current & ~(BloodBoundBit | LoseDurabilityOnDeathBit);
        result |= bloodBound ? BloodBoundBit : donor & BloodBoundBit;
        if (!indestructible) result |= donor & LoseDurabilityOnDeathBit;
        return result;
    }

    /// <summary>
    /// The category a live item instance gets. Only adds what the policy asks for; an instance
    /// is never stripped of a bit, because nothing records whether the policy or the save put it
    /// there.
    /// </summary>
    internal static long InstanceCategory(long current, bool bloodBound, bool indestructible)
    {
        long result = current;
        if (bloodBound) result |= BloodBoundBit;
        if (indestructible) result &= ~LoseDurabilityOnDeathBit;
        return result;
    }

    /// <summary>
    /// Indestructible durability: no loss on damage taken, dealt or spell use, no destroy when
    /// broken, and full durability when the item has a positive finite maximum. The same values
    /// <c>ItemGrantOptions.Durability = Indestructible</c> writes at grant time.
    /// Returns whether any value changed.
    /// </summary>
    internal static bool KeepDurability(
        ref int lossType,
        ref float takeDamageFactor,
        ref bool destroyWhenBroken,
        ref float value,
        float maxDurability)
    {
        bool changed = lossType != LossTypeNone || takeDamageFactor != 0f || destroyWhenBroken;
        lossType = LossTypeNone;
        takeDamageFactor = 0f;
        destroyWhenBroken = false;
        if (maxDurability > 0f && !float.IsInfinity(maxDurability) && value != maxDurability)
        {
            value = maxDurability;
            changed = true;
        }
        return changed;
    }
}
