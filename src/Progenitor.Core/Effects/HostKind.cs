namespace Progenitor.Effects;

/// <summary>
/// Where a borrowed world effect is shown. Overlay is native Screen Space Overlay (UI images).
/// RenderTexture is the only way a world particle / sequence / trail appears inside a menu
/// canvas — a Screen Space Overlay cannot parent a MeshRenderer (effects-catalogue.md).
/// </summary>
public enum HostKind
{
    /// <summary>
    /// Mod-owned overlay canvas (sorting order 5000, ConstantPixelSize). Native for
    /// <see cref="AssetKind.Image"/>; world effects will not draw here.
    /// </summary>
    Overlay = 0,

    /// <summary>
    /// Parked orthographic camera on one unused unnamed layer (31 down to 8) that has no
    /// renderer on it writes an ARGB32 <c>RenderTexture</c> into a <c>RawImage</c>. The layer is
    /// reserved per plugin root. Never falls back to a named gameplay layer. The RT is recreated
    /// when the screen size changes.
    /// </summary>
    RenderTexture = 1,
}
