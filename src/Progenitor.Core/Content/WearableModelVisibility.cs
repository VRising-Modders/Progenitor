namespace Progenitor.Content;

/// <summary>
/// Decides whether a model's owned wearable renderers should draw. The game hides a
/// character by disabling its own renderers (observed: a dead player waiting on the
/// respawn choice has a disabled body). Owned overlays are separate renderers, so they
/// must follow that state or they float without a body. <c>Body_GEO</c> is protected
/// from every wearable hide, so its state is the game's decision, never ours.
/// </summary>
internal static class WearableModelVisibility
{
    internal const string BodyRendererName = "Body_GEO";

    /// <summary>True for a native body renderer that is not one of our generated objects.</summary>
    internal static bool IsNativeBody(string? rendererName, bool ours)
        => !ours && string.Equals(rendererName, BodyRendererName, System.StringComparison.Ordinal);

    /// <summary>
    /// <paramref name="bodies"/> counts native body renderers under the model;
    /// <paramref name="shown"/> counts those that are enabled and active. A model without
    /// a body renderer keeps its overlays visible (no evidence the game hid it).
    /// </summary>
    internal static bool Visible(int bodies, int shown) => bodies <= 0 || shown > 0;
}
