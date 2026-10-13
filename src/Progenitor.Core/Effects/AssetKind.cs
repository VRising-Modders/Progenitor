namespace Progenitor.Effects;

/// <summary>
/// Vanilla asset family a mod can look up by name. Beams are only one example: the catalog
/// covers the families the four passing client spikes proved (UI sprites, Shuriken particles,
/// VFX Graph objects, HDRP volumes) plus the Cardinal sequence used for a detached buff visual.
/// </summary>
public enum AssetKind
{
    /// <summary>Unity <c>Image</c> / <c>Sprite</c> (effects-ui-image spike, evidence 78).</summary>
    Image = 0,

    /// <summary>Unity <c>ParticleSystem</c> (effects-particle-clone spike, evidence 79).</summary>
    Particle = 1,

    /// <summary>HDRP <c>VisualEffect</c>. Discoverable via <see cref="Assets.Find"/>; this cut does not clone one.</summary>
    VisualEffect = 2,

    /// <summary>Mod-owned HDRP <c>Volume</c> (effects-volume-menu spike, evidence 79). Camera-wide, not per-panel.</summary>
    Volume = 3,

    /// <summary>
    /// A buff prefab's sequence, played detached through <c>SequenceUtility.PlaySequence</c>
    /// (effects-summonorb spike, evidence 80).
    /// </summary>
    Sequence = 4,
}
