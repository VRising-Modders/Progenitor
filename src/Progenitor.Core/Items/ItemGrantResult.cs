namespace Progenitor.Items;

/// <summary>
/// Outcome of <see cref="Items.Grant"/> or <see cref="Items.Equip"/>. <see cref="Ok"/> false
/// means the request was not accepted. A completely full bag adds nothing.
/// <see cref="WhenInventoryFull.RequireComplete"/> can be false after a short stack already
/// landed: <see cref="Added"/> is how many stayed, and they are not removed. The debug bridge
/// turns a false result into an error.
/// </summary>
public sealed class ItemGrantResult
{
    /// <summary>The request was carried out, including "already worn" when that was the configured choice.</summary>
    public bool Ok { get; init; }

    /// <summary>added, equipped, already-equipped, or refused.</summary>
    public string Action { get; init; } = "";

    /// <summary>Set when <see cref="Ok"/> is false, and sometimes as a note when a partial add succeeded.</summary>
    public string? Reason { get; init; }

    /// <summary>How many were asked for.</summary>
    public int Requested { get; init; }

    /// <summary>How many the inventory add actually took. Zero when the call only equipped an existing stack.</summary>
    public int Added { get; init; }

    /// <summary><see cref="Requested"/> minus <see cref="Added"/>.</summary>
    public int DidNotFit { get; init; }

    /// <summary>Bag slot of the new or existing stack, or -1.</summary>
    public int Slot { get; init; } = -1;

    /// <summary><c>index:version</c> of the item entity, when one exists.</summary>
    public string? ItemEntity { get; init; }

    /// <summary>An equip event was sent, or the piece was already worn and that was enough.</summary>
    public bool Equipped { get; init; }

    /// <summary><see cref="ItemDurability.Indestructible"/> was written onto an item entity.</summary>
    public bool DurabilityCleared { get; init; }

    /// <summary>The game's <c>AddItemResult</c> name when an add ran. Empty otherwise.</summary>
    public string Result { get; init; } = "";
}
