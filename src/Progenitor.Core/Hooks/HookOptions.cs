namespace Progenitor.Hooks;

/// <summary>
/// Filters for a system hook. Defaults match the usual case: fire only when the system would
/// actually run this frame, and fire every such frame. A plugin that just wants a before/after
/// seam can pass null and get that behavior.
/// </summary>
public sealed class HookOptions
{
    /// <summary>
    /// When true (the default), the hook is not invoked if the system is disabled or its
    /// <c>ShouldRunSystem</c> check fails this frame. Both Harmony prefixes sit in front of that
    /// gate, so without this filter a hook would see idle systems too. Set false to observe or
    /// skip a system that would otherwise no-op.
    /// </summary>
    public bool OnlyWhenSystemRuns { get; set; } = true;

    /// <summary>
    /// Call the hook on the first eligible frame and every N eligible frames after that.
    /// 1 (the default) means every eligible frame. Values below 1 are treated as 1.
    /// </summary>
    public int EveryNFrames { get; set; } = 1;

    /// <summary>
    /// Numeric priority among hooks on the same system phase. Higher runs first (Harmony
    /// <c>First = 800</c>). Ties break by plugin load order then registration order.
    /// <see cref="Before"/> / <see cref="After"/> plugin ids can override this.
    /// </summary>
    public int Priority { get; set; }

    /// <summary>
    /// Plugin ids this hook must run before (this first, them later). Missing ids (plugin not
    /// registered on this site) are ignored, same as Harmony.
    /// </summary>
    public string[]? Before { get; set; }

    /// <summary>
    /// Plugin ids this hook must run after (them first, this later). Missing ids are ignored.
    /// </summary>
    public string[]? After { get; set; }

    /// <summary>
    /// This plugin's BepInEx GUID. Other plugins name it in <see cref="Before"/> / <see cref="After"/>.
    /// Empty means the hook cannot be targeted by those constraints.
    /// </summary>
    public string? PluginId { get; set; }
}
