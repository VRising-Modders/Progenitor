namespace Progenitor.Units;

/// <summary>
/// Game-free gates for spawn-placement writes. <c>SpawnTransform</c> is a read-modify-write
/// so Rotation stays the prefab facing; a failed read must not write a default quaternion
/// (R26 finding 14: (0,0,0,0) home pose).
/// </summary>
public static class UnitSpawnWrite
{
    /// <summary>True when LastTranslation can be overwritten with the requested position.</summary>
    public static bool CanWriteLastTranslation(bool hasComponent) => hasComponent;

    /// <summary>
    /// True when SpawnTransform.Position can be written. Requires a successful typed read
    /// so Rotation is not zeroed.
    /// </summary>
    public static bool CanWriteSpawnTransform(bool hasComponent, bool readSucceeded) =>
        hasComponent && readSucceeded;

    /// <summary>
    /// Exact warning, once per process. Grep <c>SpawnTransform read failed</c>.
    /// </summary>
    public const string SpawnTransformReadFailed =
        "Units: SpawnTransform read failed; skipped write (no default quaternion)";
}
