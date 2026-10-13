using System;
using System.Globalization;
using Progenitor.Effects;

namespace Progenitor.Units;

/// <summary>
/// How a spawned unit looks on Progenitor clients: visual size, tint, glow and a flat color overlay
/// (<c>ctx.Units.SetLook</c>, <see cref="UnitSpawnOptions.Look"/>; docs/modules.md, "Unit look").
/// Every field defaults to vanilla, so <c>new UnitLook { Tint = new EffectColor(1f, 0.2f, 0.2f) }</c>
/// changes only the tint.
///
/// <para>The look is drawn on each client, on the unit's model. A client without Progenitor sees the
/// vanilla unit. Nothing here changes the server: the hitbox is <c>ctx.Units.SetHitboxScale</c>, a
/// separate call. The bounds below were measured live on 2026-09-25; "recommended" is what reads well
/// in play, "hard" is what <see cref="Validate"/> accepts.</para>
/// </summary>
public sealed class UnitLook
{
    /// <summary>Smallest <see cref="VisualScale"/> accepted. Below about 0.25 the unit is hard to see.</summary>
    public const float MinVisualScale = 0.1f;

    /// <summary>Largest <see cref="VisualScale"/> accepted. From about 4 the model fills the view in an arena.</summary>
    public const float MaxVisualScale = 5f;

    /// <summary>Lower end of the visual scale that reads well in play.</summary>
    public const float RecommendedMinVisualScale = 0.25f;

    /// <summary>Upper end of the visual scale that reads well in play.</summary>
    public const float RecommendedMaxVisualScale = 3f;

    /// <summary>Largest tint or overlay channel accepted. Brighter than vanilla is <see cref="GlowIntensity"/>.</summary>
    public const float MaxColorChannel = 1f;

    /// <summary>A tint whose channels are all at or below this reads as a near-black silhouette.</summary>
    public const float NearBlackTintChannel = 0.3f;

    /// <summary>No glow: the tint is drawn as it is.</summary>
    public const float MinGlowIntensity = 1f;

    /// <summary>Largest <see cref="GlowIntensity"/> accepted. About 10 blows the model out to a featureless silhouette.</summary>
    public const float MaxGlowIntensity = 8f;

    /// <summary>Lower end of the glow intensity that blooms without washing out detail.</summary>
    public const float RecommendedMinGlowIntensity = 4f;

    /// <summary>Upper end of the glow intensity that blooms without washing out detail.</summary>
    public const float RecommendedMaxGlowIntensity = 6f;

    /// <summary>
    /// Size of the model relative to its own natural size: 1 is vanilla, 2 is twice as large.
    /// <see cref="MinVisualScale"/> to <see cref="MaxVisualScale"/>; 0.25 to 3 recommended. Each
    /// creature's model has its own natural scale, so 2 always means "twice this creature".
    /// </summary>
    public float VisualScale { get; set; } = 1f;

    /// <summary>
    /// Multiplies the model's texture color. (1, 1, 1) is vanilla; each channel is 0 to 1. Saturated
    /// colors such as (1, 0.1, 0.1) recolor clearly, (1, 0.6, 0.6) is a soft pastel, and all channels
    /// at 0.3 or below give a near-black silhouette. Alpha is ignored.
    /// </summary>
    public EffectColor Tint { get; set; } = new(1f, 1f, 1f);

    /// <summary>
    /// Brightness multiplier on <see cref="Tint"/>: the model's color is drawn as
    /// <c>Tint x GlowIntensity</c> in HDR, and bloom makes it glow. 1 is no glow; 3 is bright; 4 to 6
    /// glows (recommended); 8 is the hard cap. With a tint whose brightest channel is below 1 the glow
    /// is correspondingly weaker.
    /// </summary>
    public float GlowIntensity { get; set; } = 1f;

    /// <summary>
    /// Flat fill color drawn over the whole model, with <see cref="OverlayStrength"/> as its blend.
    /// Each channel is 0 to 1. Alpha is ignored. The game's hit flash uses the same shader property, so
    /// while an overlay is set a hit flash is replaced by the overlay within a quarter of a second.
    /// </summary>
    public EffectColor Overlay { get; set; } = new(1f, 1f, 1f);

    /// <summary>Blend of <see cref="Overlay"/>: 0 is off (vanilla), 0.5 half, 1 a solid fill.</summary>
    public float OverlayStrength { get; set; }

    /// <summary>A look with every field at its vanilla value.</summary>
    public static UnitLook Vanilla => new();

    /// <summary>True when every field is at its vanilla value, so applying it changes nothing.</summary>
    public bool IsVanilla =>
        VisualScale == 1f && !UnitLookMath.TouchesBaseColor(this) && !UnitLookMath.TouchesOverlay(this);

    /// <summary>
    /// Throw <see cref="ArgumentOutOfRangeException"/> naming the first field outside its hard range
    /// (or not a finite number). <c>ctx.Units.SetLook</c> and <c>Spawn</c> call this for you.
    /// </summary>
    public void Validate()
    {
        string? why = UnitLookMath.Refusal(this);
        if (why != null) throw new ArgumentOutOfRangeException(nameof(UnitLook), why);
    }

    /// <summary>An independent copy.</summary>
    internal UnitLook Copy() => new()
    {
        VisualScale = VisualScale,
        Tint = Tint,
        GlowIntensity = GlowIntensity,
        Overlay = Overlay,
        OverlayStrength = OverlayStrength,
    };

    public override string ToString() =>
        "scale=" + F(VisualScale) + " tint=" + Rgb(Tint) + " glow=" + F(GlowIntensity)
        + " overlay=" + Rgb(Overlay) + "@" + F(OverlayStrength);

    private static string F(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    private static string Rgb(EffectColor c) => "(" + F(c.R) + "," + F(c.G) + "," + F(c.B) + ")";
}

/// <summary>RGBA as four floats: the game-free form of a shader color.</summary>
internal readonly record struct LookRgba(float R, float G, float B, float A)
{
    public static readonly LookRgba White = new(1f, 1f, 1f, 1f);

    /// <summary>Channel-wise equality within <paramref name="epsilon"/>.</summary>
    public bool Near(LookRgba other, float epsilon = 1e-3f) =>
        MathF.Abs(R - other.R) <= epsilon && MathF.Abs(G - other.G) <= epsilon
        && MathF.Abs(B - other.B) <= epsilon && MathF.Abs(A - other.A) <= epsilon;
}

/// <summary>
/// Game-free rules behind <see cref="UnitLook"/>: range validation, the shader values a look composes
/// to, and which properties a look touches at all. The live client applier and the tests share it.
/// </summary>
internal static class UnitLookMath
{
    /// <summary>Scale comparisons: a drift smaller than this is float noise, not a reset.</summary>
    public const float ScaleEpsilon = 1e-4f;

    /// <summary>Why <paramref name="look"/> is out of range, or null when it is valid.</summary>
    public static string? Refusal(UnitLook? look)
    {
        if (look == null) return "look is null.";
        if (!InRange(look.VisualScale, UnitLook.MinVisualScale, UnitLook.MaxVisualScale))
            return "VisualScale " + Show(look.VisualScale) + " is outside " + Show(UnitLook.MinVisualScale)
                + " to " + Show(UnitLook.MaxVisualScale) + " (0.25 to 3 recommended; 1 is the creature's natural size).";
        if (ColorRefusal(look.Tint) is { } tint)
            return "Tint " + tint;
        if (!InRange(look.GlowIntensity, UnitLook.MinGlowIntensity, UnitLook.MaxGlowIntensity))
            return "GlowIntensity " + Show(look.GlowIntensity) + " is outside " + Show(UnitLook.MinGlowIntensity)
                + " to " + Show(UnitLook.MaxGlowIntensity) + " (1 is no glow; 4 to 6 recommended).";
        if (ColorRefusal(look.Overlay) is { } overlay)
            return "Overlay " + overlay;
        if (!InRange(look.OverlayStrength, 0f, 1f))
            return "OverlayStrength " + Show(look.OverlayStrength) + " is outside 0 to 1 (0 is off).";
        return null;
    }

    private static string? ColorRefusal(EffectColor c)
    {
        if (!InRange(c.R, 0f, UnitLook.MaxColorChannel) || !InRange(c.G, 0f, UnitLook.MaxColorChannel)
            || !InRange(c.B, 0f, UnitLook.MaxColorChannel))
            return "(" + Show(c.R) + ", " + Show(c.G) + ", " + Show(c.B) + ") has a channel outside 0 to "
                + Show(UnitLook.MaxColorChannel) + "; use GlowIntensity for brighter than vanilla.";
        return null;
    }

    private static bool InRange(float v, float min, float max) => float.IsFinite(v) && v >= min && v <= max;

    private static string Show(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>True when the look changes <c>_BaseColor</c> (a tint other than white, or a glow).</summary>
    public static bool TouchesBaseColor(UnitLook look) =>
        look.Tint.R != 1f || look.Tint.G != 1f || look.Tint.B != 1f || look.GlowIntensity != 1f;

    /// <summary>True when the look draws an overlay (<c>_BlinkColor</c> alpha above 0).</summary>
    public static bool TouchesOverlay(UnitLook look) => look.OverlayStrength > 0f;

    /// <summary>
    /// The <c>_BaseColor</c> a look draws with on a material whose own <c>_BaseColor</c> is
    /// <paramref name="material"/>: <c>material x Tint x GlowIntensity</c> per RGB channel, the
    /// material's alpha kept. On <c>Shader_MasterLit</c> the material value is white, so this is
    /// <c>Tint x GlowIntensity</c>.
    /// </summary>
    public static LookRgba ComposeBase(LookRgba material, UnitLook look)
    {
        float g = look.GlowIntensity;
        return new LookRgba(material.R * look.Tint.R * g, material.G * look.Tint.G * g, material.B * look.Tint.B * g, material.A);
    }

    /// <summary>The <c>_BlinkColor</c> an overlay draws with: RGB, alpha = strength.</summary>
    public static LookRgba ComposeOverlay(UnitLook look) =>
        new(look.Overlay.R, look.Overlay.G, look.Overlay.B, look.OverlayStrength);

    /// <summary>
    /// Target <c>localScale</c> for a model whose natural scale is <paramref name="natural"/>.
    /// </summary>
    public static UnitVec3 ScaledFrom(UnitVec3 natural, float visualScale) =>
        new(natural.X * visualScale, natural.Y * visualScale, natural.Z * visualScale);

    /// <summary>True when <paramref name="current"/> is not <paramref name="expected"/> (relative tolerance).</summary>
    public static bool ScaleDrifted(UnitVec3 current, UnitVec3 expected) =>
        Differs(current.X, expected.X) || Differs(current.Y, expected.Y) || Differs(current.Z, expected.Z);

    private static bool Differs(float a, float b) =>
        !(MathF.Abs(a - b) <= ScaleEpsilon * MathF.Max(1f, MathF.Abs(b)));

    /// <summary>True when two looks draw the same (field-wise equality).</summary>
    public static bool Same(UnitLook? a, UnitLook? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a == null || b == null) return false;
        return a.VisualScale == b.VisualScale && a.GlowIntensity == b.GlowIntensity
            && a.OverlayStrength == b.OverlayStrength
            && SameRgb(a.Tint, b.Tint) && SameRgb(a.Overlay, b.Overlay);
    }

    private static bool SameRgb(EffectColor a, EffectColor b) => a.R == b.R && a.G == b.G && a.B == b.B;
}

/// <summary>
/// What the client applier writes to one model when the wanted look changes from
/// <see cref="From"/> (what it wrote last, or null) to <see cref="To"/> (null to remove the look).
/// The rule that matters: a property the new look does not use is set back to its neutral value only
/// when the old look had written it, and is otherwise left alone, so a scale-only look never touches
/// colors and a look without an overlay never fights the game's hit flash.
/// </summary>
internal readonly record struct UnitLookTransition(
    bool WriteScale,
    bool WriteBase,
    bool RestoreBase,
    bool WriteOverlay,
    bool RestoreOverlay)
{
    public static UnitLookTransition Plan(UnitLook? from, UnitLook? to)
    {
        bool fromBase = from != null && UnitLookMath.TouchesBaseColor(from);
        bool fromOverlay = from != null && UnitLookMath.TouchesOverlay(from);
        bool toBase = to != null && UnitLookMath.TouchesBaseColor(to);
        bool toOverlay = to != null && UnitLookMath.TouchesOverlay(to);
        bool fromScale = from != null && from.VisualScale != 1f;
        bool toScale = to != null && to.VisualScale != 1f;
        return new UnitLookTransition(
            WriteScale: fromScale || toScale,
            WriteBase: toBase,
            RestoreBase: fromBase && !toBase,
            WriteOverlay: toOverlay,
            RestoreOverlay: fromOverlay && !toOverlay);
    }

    /// <summary>True when nothing at all is written.</summary>
    public bool IsEmpty => !WriteScale && !WriteBase && !RestoreBase && !WriteOverlay && !RestoreOverlay;
}
