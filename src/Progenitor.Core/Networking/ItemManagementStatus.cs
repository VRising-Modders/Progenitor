using System.Collections.Generic;

namespace Progenitor.Networking;

/// <summary>Server-owned summary only; no inventory payloads or filesystem paths cross the connection.</summary>
internal sealed class ItemManagementStatus
{
    public List<ItemManagementOwnerStatus> Owners { get; set; } = new();
    public string Note { get; set; } = "";
}

internal sealed class ItemManagementOwnerStatus
{
    public string OwnerId { get; set; } = "";
    public int Pending { get; set; }
    public int Unavailable { get; set; }
    public int Restored { get; set; }
    public bool RestoreOffered { get; set; }
    public bool Available { get; set; }
    private string _reasons = "";
    public string Reasons { get => _reasons; set => _reasons = ItemManagementPolicy.Clip(value, ItemManagementPolicy.MaxReasonChars); }
}
