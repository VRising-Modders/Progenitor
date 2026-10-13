namespace Progenitor.Effects;

/// <summary>Owned handle state. Not the game's <c>LoadingStatus</c>.</summary>
public enum AssetAcquisitionStatus
{
    Queued = 0,
    Loading = 1,
    Ready = 2,
    Error = 3,
    Refused = 4,
}
