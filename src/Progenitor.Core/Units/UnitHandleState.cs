namespace Progenitor.Units;

/// <summary>
/// Handle lifecycle. Tests assert Spawn → Follow/GoTo → Despawn; double dispose stays Disposed.
/// Disabled handles skip Follow/GoTo the way Effects skips Host on the server.
/// </summary>
public enum UnitHandleState
{
    /// <summary>Client, skip, or missing prefab. No entity was created.</summary>
    Disabled = 0,

    /// <summary>Server spawn succeeded; not seeking or following.</summary>
    Spawned = 1,

    /// <summary>Follower overlay + Follow behavior-tree request is live.</summary>
    Following = 2,

    /// <summary>ApproachPosition <c>AiMove_Server</c> field set is live.</summary>
    GoingTo = 3,

    /// <summary>Released. Further Dispose / Despawn calls are ignored.</summary>
    Disposed = 4,
}
