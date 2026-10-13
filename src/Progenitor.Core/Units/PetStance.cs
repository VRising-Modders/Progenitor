namespace Progenitor.Units;

/// <summary>
/// What a pet does (<c>ctx.Units.SetPet</c>, <c>SetPetStance</c>, <c>PetAttack</c>, <c>HoldAt</c>;
/// docs/modules.md, "Pet stances"). Checked every 0.5 s.
/// </summary>
public enum PetStance
{
    /// <summary>Follow the owner, fight a live aggro target, and return to the owner afterwards.</summary>
    Follow = 0,

    /// <summary>
    /// Stand at the hold position without fighting: aggro is off (<c>AggroConsumer.Active</c> 0), any
    /// aggro target and Follow are cleared, and the unit walks back when pushed away. Leaving Hold
    /// writes back the <c>Active</c> value read when Hold began.
    /// </summary>
    Hold = 1,

    /// <summary>Fight one target (<c>PetAttack</c>) until it is dead or gone, then Follow.</summary>
    Attack = 2,
}
