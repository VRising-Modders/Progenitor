namespace Progenitor.Content;

/// <summary>
/// When this editor runs relative to other editors of the same prefab. Lower values run first
/// ("under"); <see cref="Late"/> runs last ("on top") and wins a same-component conflict.
/// Early is -1000, Default 0 and Late 1000. Intermediate values are allowed
/// (for example <c>(PrefabEditPriority)((int)Early + 5)</c>).
/// </summary>
public enum PrefabEditPriority
{
    /// <summary>Applied before <see cref="Default"/> edits.</summary>
    Early = -1000,

    /// <summary>The default pass.</summary>
    Default = 0,

    /// <summary>Applied after <see cref="Default"/> edits; last writer of a component wins.</summary>
    Late = 1000,
}
