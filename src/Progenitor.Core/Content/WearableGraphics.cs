using System;
using System.Collections.Generic;

namespace Progenitor.Content;

/// <summary>
/// Which <c>Equipment</c> slot a wearable occupies. The names match the fields on the
/// character's <c>Equipment</c> component (<c>ArmorChestSlot</c>, <c>WeaponSlot</c>, …).
/// </summary>
public enum WearableSlot
{
    /// <summary><c>Equipment.ArmorHeadgearSlot</c>.</summary>
    Headgear = 0,
    /// <summary><c>Equipment.ArmorChestSlot</c>.</summary>
    Chest = 1,
    /// <summary><c>Equipment.ArmorGlovesSlot</c>.</summary>
    Gloves = 2,
    /// <summary><c>Equipment.ArmorLegsSlot</c>.</summary>
    Legs = 3,
    /// <summary><c>Equipment.ArmorFootgearSlot</c>.</summary>
    Footgear = 4,
    /// <summary><c>Equipment.CloakSlot</c>.</summary>
    Cloak = 5,
    /// <summary><c>Equipment.WeaponSlot</c>.</summary>
    Weapon = 6,
    /// <summary><c>Equipment.GrimoireSlot</c>.</summary>
    Grimoire = 7,
}

/// <summary>
/// Which material a mesh part uses. Cloth is the garment, gold is the tassel metal,
/// void is the unlit black the hood puts over the face.
/// </summary>
public enum WearableSurface
{
    /// <summary>Dark cloth. Low metal, the body of a robe, glove, shoe or staff.</summary>
    Cloth = 0,
    /// <summary>Gold. The belt, tassels and trim.</summary>
    Gold = 1,
    /// <summary>Unlit black. The hood's face cover.</summary>
    Void = 2,
}

/// <summary>How an authored garment resolves surfaces mapped to native palette channels.</summary>
public enum WearablePaletteMode
{
    /// <summary>Use the selected native equipment palette when available.</summary>
    Native = 0,
    /// <summary>Keep the garment's authored colors even when a native palette is selected.</summary>
    Authored = 1,
}

/// <summary>
/// Worn graphics for one cloned item. Build one with <see cref="Define"/> or take a
/// ready-made set piece from <see cref="WearableLooks"/>. Pass it to
/// <c>DefineItem(...).Wearable(...)</c>.
///
/// <para>The item is still a <c>DefineItem</c> clone, so it grants, equips and syncs like
/// any other item. On the client, while that item is in <see cref="Slot"/>, Progenitor
/// builds the sketch as a skinned mesh on the wearer's bones (the same joint names the
/// game's <c>HybridEquipmentUser</c> binds) and hides donor renderers named in
/// <see cref="HideNames"/>. <see cref="MaleModel"/> and <see cref="FemaleModel"/> are
/// optional diagnostic model names. Native model references remain unchanged until
/// a matching layout/access contract is verified.</para>
/// </summary>
public sealed class WearableGraphics
{
    internal readonly List<WearablePart> Parts;
    internal WearableAuthoredMesh? Authored;
    internal WearableAuthoredMesh[]? AuthoredVariants;
    internal WearableNativeRenderer? NativeRenderer;
    internal WearableAuthoredMesh? AuthoredOverlay;
    internal WearableAuthoredMesh[]? AuthoredOverlayVariants;

    internal bool HasMappedSurfacePalette
    {
        get
        {
            if (AuthoredVariants != null)
            {
                foreach (var variant in AuthoredVariants)
                    if (variant.HasSurfacePalette) return true;
                return false;
            }
            return Authored?.HasSurfacePalette == true;
        }
    }

    internal WearableAuthoredMesh? SelectCompatibleAuthored(
        IReadOnlyDictionary<string, string> paths, IReadOnlyDictionary<string, float[]> rests)
    {
        if (AuthoredVariants == null) return Authored != null && Matches(Authored) ? Authored : null;
        foreach (var variant in AuthoredVariants)
            if (Matches(variant)) return variant;
        return null;

        bool Matches(WearableAuthoredMesh variant)
        {
            for (int i = 0; i < variant.Mesh.Bones.Length; i++)
            {
                var name = variant.Mesh.Bones[i];
                if (!paths.TryGetValue(name, out var path) || !SameCapturedPath(path, variant.Paths[i]))
                    return false;
            }
            return variant.Matches(rests);
        }

        static bool SameCapturedPath(string actual, string captured)
        {
            int actualRootEnd = actual.IndexOf('/');
            int capturedRootEnd = captured.IndexOf('/');
            if (actualRootEnd < 0 || capturedRootEnd < 0)
                return actualRootEnd == capturedRootEnd && string.Equals(actual, captured, StringComparison.Ordinal);
            // The explicit model root may move or be renamed between captures and
            // equip. Every descendant component, including sibling indices, must match.
            return string.Equals(actual[(actualRootEnd + 1)..], captured[(capturedRootEnd + 1)..],
                StringComparison.Ordinal);
        }
    }

    internal WearableGraphics(
        WearableSlot slot,
        string? maleModel,
        string? femaleModel,
        string[] hideNames,
        List<WearablePart> parts,
        WearableSlot? paletteSource = null,
        WearablePaletteMode paletteMode = WearablePaletteMode.Native)
    {
        Slot = slot;
        MaleModel = string.IsNullOrEmpty(maleModel) ? null : maleModel;
        FemaleModel = string.IsNullOrEmpty(femaleModel) ? null : femaleModel;
        HideNames = hideNames;
        Parts = parts;
        PaletteSource = paletteSource ?? slot;
        PaletteMode = paletteMode;
    }

    /// <summary>Equipment slot this item occupies.</summary>
    public WearableSlot Slot { get; }

    /// <summary>
    /// Equipment slot supplying mapped authored surface colors. Defaults to <see cref="Slot"/>.
    /// When the source has an occupied cosmetic slot, its palette takes precedence over stats gear.
    /// A missing palette leaves each surface at its authored fallback color.
    /// </summary>
    public WearableSlot PaletteSource { get; }

    /// <summary>Whether mapped surfaces follow the selected native palette or keep authored colors.</summary>
    public WearablePaletteMode PaletteMode { get; }

    /// <summary>
    /// Return this recipe with mapped surface colors following another equipment slot.
    /// Fixed surface colors, including the hood's black face, remain fixed.
    /// </summary>
    public WearableGraphics WithPaletteSource(WearableSlot source)
    {
        if (!Enum.IsDefined(typeof(WearableSlot), source)) throw new ArgumentOutOfRangeException(nameof(source));
        if (source == PaletteSource) return this;
        return new WearableGraphics(Slot, MaleModel, FemaleModel, new List<string>(HideNames).ToArray(),
            new List<WearablePart>(Parts), source, PaletteMode) { Authored = Authored, AuthoredVariants = AuthoredVariants, NativeRenderer = NativeRenderer, AuthoredOverlay = AuthoredOverlay, AuthoredOverlayVariants = AuthoredOverlayVariants };
    }

    /// <summary>Return this recipe with authored colors, regardless of the selected native palette.</summary>
    public WearableGraphics WithAuthoredColors()
    {
        if (PaletteMode == WearablePaletteMode.Authored) return this;
        return new WearableGraphics(Slot, MaleModel, FemaleModel, new List<string>(HideNames).ToArray(),
            new List<WearablePart>(Parts), PaletteSource, WearablePaletteMode.Authored) { Authored = Authored, AuthoredVariants = AuthoredVariants, NativeRenderer = NativeRenderer, AuthoredOverlay = AuthoredOverlay, AuthoredOverlayVariants = AuthoredOverlayVariants };
    }

    /// <summary>
    /// Tint one material slot of a borrowed native renderer through an explicitly named
    /// shader color property and a native palette channel. The source material supplies
    /// the fallback color when no palette choice is available. Requires a native recipe.
    /// </summary>
    public WearableGraphics WithNativeColorOverride(int materialIndex, string shaderProperty, string paletteChannel)
        => WithNativeColorOverrideCore(materialIndex, shaderProperty, paletteChannel, null);

    /// <summary>Map a native shader color to a palette channel with an explicit fallback RGBA.</summary>
    public WearableGraphics WithNativeColorOverride(int materialIndex, string shaderProperty, string paletteChannel,
        float fallbackR, float fallbackG, float fallbackB, float fallbackA)
        => WithNativeColorOverrideCore(materialIndex, shaderProperty, paletteChannel,
            ValidColor(fallbackR, fallbackG, fallbackB, fallbackA));

    private WearableGraphics WithNativeColorOverrideCore(int materialIndex, string shaderProperty, string paletteChannel,
        (float R, float G, float B, float A)? fallback)
    {
        if (NativeRenderer == null) throw new InvalidOperationException("This recipe does not borrow a native renderer.");
        if (materialIndex < 0) throw new ArgumentOutOfRangeException(nameof(materialIndex));
        if (string.IsNullOrWhiteSpace(shaderProperty) || shaderProperty[0] != '_')
            throw new ArgumentException("An explicit shader color property is required.", nameof(shaderProperty));
        if (paletteChannel != "primary" && paletteChannel != "accent"
            && paletteChannel != "neutral" && paletteChannel != "metal")
            throw new ArgumentException("Palette channel must be primary, accent, neutral or metal.", nameof(paletteChannel));
        var native = NativeRenderer.WithColorOverride(materialIndex, shaderProperty, paletteChannel, fallback);
        return new WearableGraphics(Slot, MaleModel, FemaleModel, new List<string>(HideNames).ToArray(),
            new List<WearablePart>(Parts), PaletteSource, PaletteMode) { Authored = Authored, AuthoredVariants = AuthoredVariants, NativeRenderer = native, AuthoredOverlay = AuthoredOverlay, AuthoredOverlayVariants = AuthoredOverlayVariants };
    }

    /// <summary>Set one verified native shader color to a fixed value, independent of the palette.</summary>
    public WearableGraphics WithNativeFixedColor(int materialIndex, string shaderProperty,
        float r, float g, float b, float a)
    {
        if (NativeRenderer == null) throw new InvalidOperationException("This recipe does not borrow a native renderer.");
        if (materialIndex < 0) throw new ArgumentOutOfRangeException(nameof(materialIndex));
        if (string.IsNullOrWhiteSpace(shaderProperty) || shaderProperty[0] != '_')
            throw new ArgumentException("An explicit shader color property is required.", nameof(shaderProperty));
        var native = NativeRenderer.WithFixedColor(materialIndex, shaderProperty, ValidColor(r, g, b, a));
        return new WearableGraphics(Slot, MaleModel, FemaleModel, new List<string>(HideNames).ToArray(),
            new List<WearablePart>(Parts), PaletteSource, PaletteMode) { Authored = Authored, AuthoredVariants = AuthoredVariants, NativeRenderer = native, AuthoredOverlay = AuthoredOverlay, AuthoredOverlayVariants = AuthoredOverlayVariants };
    }

    /// <summary>Replace one verified native texture property with Unity's built-in white texture.</summary>
    public WearableGraphics WithNativeWhiteTexture(int materialIndex, string shaderProperty)
    {
        if (NativeRenderer == null) throw new InvalidOperationException("This recipe does not borrow a native renderer.");
        if (materialIndex < 0) throw new ArgumentOutOfRangeException(nameof(materialIndex));
        if (string.IsNullOrWhiteSpace(shaderProperty) || shaderProperty[0] != '_')
            throw new ArgumentException("An explicit shader texture property is required.", nameof(shaderProperty));
        var native = NativeRenderer.WithWhiteTexture(materialIndex, shaderProperty);
        return new WearableGraphics(Slot, MaleModel, FemaleModel, new List<string>(HideNames).ToArray(),
            new List<WearablePart>(Parts), PaletteSource, PaletteMode)
        { Authored = Authored, AuthoredVariants = AuthoredVariants, NativeRenderer = native, AuthoredOverlay = AuthoredOverlay, AuthoredOverlayVariants = AuthoredOverlayVariants };
    }

    /// <summary>
    /// Add an original, void-only authored mesh as a face insert over a borrowed native renderer.
    /// Its rig/rest matrix is validated at attach time; the native renderer remains game-owned.
    /// </summary>
    public WearableGraphics WithAuthoredOverlay(string json)
        => WithAuthoredOverlayVariants(new[] { json });

    /// <summary>Add alternative original black face inserts for distinct captured rest rigs.
    /// The first compatible bone path and rest matrix set is selected at attachment.</summary>
    public WearableGraphics WithAuthoredOverlayVariants(IEnumerable<string> jsonVariants)
    {
        if (NativeRenderer == null) throw new InvalidOperationException("An authored overlay requires a native renderer recipe.");
        if (AuthoredOverlay != null) throw new InvalidOperationException("This recipe already has an authored overlay.");
        var overlays = ParseVariants(jsonVariants);
        foreach (var overlay in overlays)
        {
            if (overlay.Mesh.Void.Length == 0 || overlay.Mesh.Cloth.Length != 0 || overlay.Mesh.Gold.Length != 0
                || overlay.TrySurfacePalette("void", out _))
                throw new ArgumentException("A native renderer overlay must contain only fixed-color void triangles.", nameof(jsonVariants));
            if (overlay.TrySurfaceColor("void", out var color)
                && (color.R != 0f || color.G != 0f || color.B != 0f || color.A != 1f))
                throw new ArgumentException("The void overlay color must be opaque black.", nameof(jsonVariants));
        }
        return new WearableGraphics(Slot, MaleModel, FemaleModel, new List<string>(HideNames).ToArray(),
            new List<WearablePart>(Parts), PaletteSource, PaletteMode)
        { NativeRenderer = NativeRenderer, AuthoredOverlay = overlays[0], AuthoredOverlayVariants = overlays };
    }

    private static (float R, float G, float B, float A) ValidColor(float r, float g, float b, float a)
    {
        if (!float.IsFinite(r) || !float.IsFinite(g) || !float.IsFinite(b) || !float.IsFinite(a)
            || r < 0f || g < 0f || b < 0f || a < 0f)
            throw new ArgumentOutOfRangeException(nameof(r), "Native shader colors must have finite nonnegative channels.");
        return (r, g, b, a);
    }

    /// <summary>
    /// Optional male model name for diagnostics; does not retarget native equipment.
    /// </summary>
    public string? MaleModel { get; }

    /// <summary>
    /// Optional female model name for diagnostics. Null falls back to <see cref="MaleModel"/>.
    /// </summary>
    public string? FemaleModel { get; }

    /// <summary>
    /// Renderer-name prefixes to switch off on the wearer while this piece is equipped
    /// (the donor vanity). Matching is ordinal, case-insensitive substring matching.
    /// Body and face parts remain protected. Hair is protected unless a Headgear
    /// piece explicitly includes the exact <c>Hair</c> token.
    /// </summary>
    public IReadOnlyList<string> HideNames { get; }

    /// <summary>How many shapes the sketch recorded.</summary>
    public int PartCount => Authored != null || NativeRenderer != null ? 1 : Parts.Count;

    /// <summary>Effective female diagnostic model name.</summary>
    public string? FemaleModelOrMale => FemaleModel ?? MaleModel;

    /// <summary>
    /// Starts a sketch for <paramref name="slot"/>. The action adds shapes, model names
    /// and hide prefixes. Returns the immutable graphics the item builder stores.
    /// </summary>
    public static WearableGraphics Define(WearableSlot slot, Action<WearableSketch> sketch)
    {
        if (sketch == null) throw new ArgumentNullException(nameof(sketch));
        var s = new WearableSketch(slot);
        sketch(s);
        return s.Freeze();
    }

    /// <summary>Load an original Blender-authored garment. It is displayed only on a matching bind skeleton.
    /// Export separate assets for body types with different rest matrices. Geometry is model-root space in meters.
    /// An optional authored <c>paletteSource</c> chooses the slot supplying mapped colors;
    /// <see cref="WithPaletteSource"/> can override it for a particular item. Optional
    /// <c>paletteMode</c> selects native palette or authored colors.</summary>
    public static WearableGraphics FromAuthoredJson(WearableSlot slot, string json, params string[] hideNames)
        => FromAuthoredJsonVariants(slot, new[] { json }, hideNames);

    /// <summary>Load alternative original garments captured against distinct rest rigs.
    /// Every variant must declare the same palette source and mode. Attachment selects
    /// only a variant whose complete bone paths and rest matrices match the wearer.</summary>
    public static WearableGraphics FromAuthoredJsonVariants(WearableSlot slot,
        IEnumerable<string> jsonVariants, params string[] hideNames)
    {
        if (!Enum.IsDefined(typeof(WearableSlot), slot)) throw new ArgumentOutOfRangeException(nameof(slot));
        var variants = ParseVariants(jsonVariants);
        var authored = variants[0];
        foreach (var variant in variants)
            if (variant.PaletteSource != authored.PaletteSource || variant.PaletteMode != authored.PaletteMode)
                throw new ArgumentException("Authored variants must use the same palette source and mode.", nameof(jsonVariants));
        return new WearableGraphics(slot, null, null, hideNames == null ? Array.Empty<string>() : (string[])hideNames.Clone(),
            new List<WearablePart>(), authored.PaletteSource, authored.PaletteMode) { Authored = authored, AuthoredVariants = variants };
    }

    private static WearableAuthoredMesh[] ParseVariants(IEnumerable<string> jsonVariants)
    {
        if (jsonVariants == null) throw new ArgumentNullException(nameof(jsonVariants));
        var variants = new List<WearableAuthoredMesh>();
        foreach (var json in jsonVariants) variants.Add(WearableAuthoredMesh.Parse(json));
        if (variants.Count == 0) throw new ArgumentException("At least one authored variant is required.", nameof(jsonVariants));
        return variants.ToArray();
    }

    /// <summary>
    /// Reuse one exact skinned renderer already supplied by the equipped native item.
    /// Its game-owned mesh is borrowed only while this item is worn; no native geometry
    /// is exported or owned by Progenitor. Ambiguous or missing renderer names defer
    /// attachment. Materials are independently cloned before any override.
    /// </summary>
    public static WearableGraphics FromNativeRenderer(WearableSlot slot, string rendererName, params string[] hideNames)
    {
        if (!Enum.IsDefined(typeof(WearableSlot), slot)) throw new ArgumentOutOfRangeException(nameof(slot));
        if (string.IsNullOrWhiteSpace(rendererName)) throw new ArgumentException("An exact renderer name is required.", nameof(rendererName));
        return new WearableGraphics(slot, null, null,
            hideNames == null ? Array.Empty<string>() : (string[])hideNames.Clone(), new List<WearablePart>())
        { NativeRenderer = new WearableNativeRenderer(rendererName) };
    }

    internal WearableMesh Build(IReadOnlyDictionary<string, WearableVec3> pose, IReadOnlyList<WearableVec3>? torso = null,
        IReadOnlyList<WearableSkinSample>? bodySamples = null) =>
        Authored?.Mesh ?? WearableMeshBuild.Build(this, pose, torso, bodySamples);
}

internal sealed class WearableNativeRenderer
{
    internal WearableNativeRenderer(string rendererName, WearableNativeColorOverride[]? colors = null,
        WearableNativeFixedColor[]? fixedColors = null, WearableNativeWhiteTexture[]? whiteTextures = null)
    {
        RendererName = rendererName;
        Colors = colors ?? Array.Empty<WearableNativeColorOverride>();
        FixedColors = fixedColors ?? Array.Empty<WearableNativeFixedColor>();
        WhiteTextures = whiteTextures ?? Array.Empty<WearableNativeWhiteTexture>();
    }
    internal string RendererName { get; }
    internal WearableNativeColorOverride[] Colors { get; }
    internal WearableNativeFixedColor[] FixedColors { get; }
    internal WearableNativeWhiteTexture[] WhiteTextures { get; }

    internal WearableNativeRenderer WithColorOverride(int materialIndex, string property, string channel,
        (float R, float G, float B, float A)? fallback)
    {
        CheckUnique(materialIndex, property);
        var colors = new WearableNativeColorOverride[Colors.Length + 1];
        Array.Copy(Colors, colors, Colors.Length);
        colors[^1] = new WearableNativeColorOverride(materialIndex, property, channel, fallback);
        return new WearableNativeRenderer(RendererName, colors, FixedColors, WhiteTextures);
    }

    internal WearableNativeRenderer WithFixedColor(int materialIndex, string property,
        (float R, float G, float B, float A) color)
    {
        CheckUnique(materialIndex, property);
        var fixedColors = new WearableNativeFixedColor[FixedColors.Length + 1];
        Array.Copy(FixedColors, fixedColors, FixedColors.Length);
        fixedColors[^1] = new WearableNativeFixedColor(materialIndex, property, color);
        return new WearableNativeRenderer(RendererName, Colors, fixedColors, WhiteTextures);
    }

    internal WearableNativeRenderer WithWhiteTexture(int materialIndex, string property)
    {
        CheckUnique(materialIndex, property);
        var textures = new WearableNativeWhiteTexture[WhiteTextures.Length + 1];
        Array.Copy(WhiteTextures, textures, WhiteTextures.Length);
        textures[^1] = new WearableNativeWhiteTexture(materialIndex, property);
        return new WearableNativeRenderer(RendererName, Colors, FixedColors, textures);
    }

    private void CheckUnique(int materialIndex, string property)
    {
        foreach (var color in Colors)
            if (color.MaterialIndex == materialIndex && color.Property == property)
                throw new ArgumentException("The native color property is already mapped.", nameof(property));
        foreach (var color in FixedColors)
            if (color.MaterialIndex == materialIndex && color.Property == property)
                throw new ArgumentException("The native color property is already fixed.", nameof(property));
        foreach (var texture in WhiteTextures)
            if (texture.MaterialIndex == materialIndex && texture.Property == property)
                throw new ArgumentException("The native texture property is already overridden.", nameof(property));
    }
}

internal readonly record struct WearableNativeColorOverride(int MaterialIndex, string Property, string Channel,
    (float R, float G, float B, float A)? Fallback);
internal readonly record struct WearableNativeFixedColor(int MaterialIndex, string Property,
    (float R, float G, float B, float A) Color);
internal readonly record struct WearableNativeWhiteTexture(int MaterialIndex, string Property);

/// <summary>
/// Fluent sketch behind <see cref="WearableGraphics.Define"/>. Distances are in the
/// character model's units (the same space as its bones). A radius of
/// <see cref="Fit"/> is measured from the pose: shoulder span for a torso, a small
/// default when the shoulders are missing.
/// </summary>
public sealed class WearableSketch
{
    /// <summary>Pass as a radius to size the shape from the pose instead of a fixed number.</summary>
    public const float Fit = -1f;

    private readonly WearableSlot _slot;
    private readonly List<WearablePart> _parts = new();
    private string? _male;
    private string? _female;
    private readonly List<string> _hide = new();

    internal WearableSketch(WearableSlot slot) => _slot = slot;

    /// <summary>
    /// A tube from <paramref name="fromBone"/> to the height of <paramref name="toBone"/>,
    /// kept on <paramref name="fromBone"/>'s horizontal position so a hem does not chase
    /// one foot. <paramref name="followEnd"/> weights the far ring to <paramref name="toBone"/>
    /// (a sleeve). Otherwise the whole tube follows <paramref name="fromBone"/> (a robe).
    /// </summary>
    public WearableSketch Loft(string fromBone, string toBone, float radiusFrom, float radiusTo, WearableSurface surface, bool followEnd = false)
    {
        _parts.Add(new WearablePart(WearableShape.Loft, surface, fromBone, toBone, radiusFrom, radiusTo, 0f, 0, followEnd));
        return this;
    }

    /// <summary>A ring around <paramref name="bone"/>, shifted up by <paramref name="yOffset"/>.</summary>
    public WearableSketch Ring(string bone, float radius, float yOffset, WearableSurface surface)
    {
        _parts.Add(new WearablePart(WearableShape.Ring, surface, bone, "", radius, 0f, yOffset, 0, false));
        return this;
    }

    /// <summary>
    /// A filled disc in front of <paramref name="bone"/> (along the pose's forward), used
    /// for the hood's black face. <paramref name="forward"/> is how far in front of the bone.
    /// </summary>
    public WearableSketch Disc(string bone, float radius, float forward, WearableSurface surface)
    {
        _parts.Add(new WearablePart(WearableShape.Disc, surface, bone, "", radius, 0f, forward, 0, false));
        return this;
    }

    /// <summary>
    /// <paramref name="count"/> strips hanging from a ring around <paramref name="bone"/>,
    /// on the forward arc. <paramref name="yOffset"/> lifts the ring (the staff puts it
    /// at the top of the pole). With <see cref="Fit"/> at the pelvis after a skirt,
    /// tapered ribbons share that skirt surface instead of projecting away from it.
    /// </summary>
    public WearableSketch Tassels(string bone, float radius, int count, float length, WearableSurface surface, float yOffset = 0f)
    {
        if (count < 1) throw new ArgumentOutOfRangeException(nameof(count), "tassel count must be at least 1.");
        _parts.Add(new WearablePart(WearableShape.Tassels, surface, bone, "", radius, length, yOffset, count, false));
        return this;
    }

    /// <summary>
    /// A short tube starting at <paramref name="bone"/> and continuing past it, away from
    /// <paramref name="awayFrom"/> (wrist away from elbow, ankle away from knee).
    /// </summary>
    public WearableSketch Cap(string bone, string awayFrom, float length, float radius, WearableSurface surface)
    {
        _parts.Add(new WearablePart(WearableShape.Cap, surface, bone, awayFrom, radius, length, 0f, 0, false));
        return this;
    }

    /// <summary>A connected torso and skirt on the vampire rig, with a shared waist and blended leg weights.</summary>
    public WearableSketch RobeBody(WearableSurface surface)
    {
        _parts.Add(new WearablePart(WearableShape.RobeBody, surface, "Neck_JNT", "", 0f, 0f, 0f, 0, false));
        return this;
    }

    /// <summary>A closed shoe from the ankle toward the ball of the foot. Both joints are required.</summary>
    public WearableSketch Shoe(string ankle, string ball, float radius, WearableSurface surface)
    {
        if (!float.IsFinite(radius) || radius <= 0f) throw new ArgumentOutOfRangeException(nameof(radius));
        _parts.Add(new WearablePart(WearableShape.Shoe, surface, ankle, ball, radius, 0f, 0f, 0, false));
        return this;
    }

    /// <summary>An original pleated skirt fitted to the pelvis, knees and ankles, with a gold hem.</summary>
    public WearableSketch Skirt(WearableSurface surface)
    {
        _parts.Add(new WearablePart(WearableShape.Skirt, surface, "Pelvis_JNT", "", 0f, 0f, 0f, 0, false));
        return this;
    }

    /// <summary>An original fitted bodice; uses measured body clearance when the client can read it.</summary>
    public WearableSketch Bodice(WearableSurface surface)
    {
        _parts.Add(new WearablePart(WearableShape.Bodice, surface, "Neck_JNT", "", 0f, 0f, 0f, 0, false));
        return this;
    }

    /// <summary>A continuous forearm, palm and articulated fingers, with an optional flush wrist band.</summary>
    public WearableSketch Glove(string wrist, string elbow, WearableSurface surface) =>
        Glove(wrist, elbow, surface, null);

    /// <summary>A fitted glove with an optional surface for the flush wrist band.</summary>
    public WearableSketch Glove(string wrist, string elbow, WearableSurface surface, WearableSurface? cuff)
    {
        _parts.Add(new WearablePart(WearableShape.Glove, surface, wrist, elbow, 0f, 0f, 0f, cuff.HasValue ? (int)cuff.Value + 1 : 0, false));
        return this;
    }

    /// <summary>A continuous shoulder-to-wrist sleeve with one shared elbow contour and an overlapping shoulder cap.</summary>
    public WearableSketch Sleeve(string shoulder, string elbow, string wrist, WearableSurface surface)
    {
        _parts.Add(new WearablePart(WearableShape.Sleeve, surface, shoulder, elbow, 0f, 0f, 0f, 0, false) { BoneThird = wrist });
        return this;
    }

    /// <summary>
    /// A pole from <paramref name="bone"/>, <paramref name="up"/> units up and
    /// <paramref name="forward"/> units along the pose forward. A staff.
    /// </summary>
    public WearableSketch Pole(string bone, float up, float forward, float radius, WearableSurface surface)
    {
        _parts.Add(new WearablePart(WearableShape.Pole, surface, bone, "", radius, up, forward, 0, false));
        return this;
    }

    /// <summary>
    /// A folded hood shell around <paramref name="headBone"/> with a recessed face opening.
    /// <paramref name="cloth"/> is the hood, <paramref name="face"/> is the void.
    /// </summary>
    public WearableSketch Cowl(string headBone, float radius, WearableSurface cloth, WearableSurface face)
    {
        _parts.Add(new WearablePart(WearableShape.Cowl, cloth, headBone, "", radius, 0f, 0f, 0, false) { SurfaceB = face });
        return this;
    }

    /// <summary>Optional model names for diagnostics; native retargeting requires a verified layout contract.</summary>
    public WearableSketch Models(string? male, string? female = null)
    {
        _male = string.IsNullOrEmpty(male) ? null : male;
        _female = string.IsNullOrEmpty(female) ? null : female;
        return this;
    }

    /// <summary>Renderer-name prefixes to hide while this piece is worn.</summary>
    public WearableSketch Hide(params string[] prefixes)
    {
        if (prefixes == null) return this;
        foreach (var p in prefixes)
            if (!string.IsNullOrEmpty(p)) _hide.Add(p);
        return this;
    }

    internal WearableGraphics Freeze() =>
        new(_slot, _male, _female, _hide.ToArray(), _parts);
}

/// <summary>
/// The tassel robe set, plus a staff on the same path so a weapon uses the same API.
/// Bone names are the character rig's (<c>Spine_JNT</c>, <c>Head_JNT</c>, …). The mesh
/// is measured from the wearer's pose, so a wider male skeleton and a narrower female
/// skeleton each get a robe that fits that body.
/// </summary>
public static class WearableLooks
{
    /// <summary>Plain robe from the neck to the ankles, with a gold tassel belt. Hides the donor chest and legs meshes.</summary>
    public static WearableGraphics RobeChest() => WearableGraphics.Define(WearableSlot.Chest, s => s
        .Bodice(WearableSurface.Cloth)
        .Skirt(WearableSurface.Cloth)
        .Sleeve("Left_Shoulder_JNT", "Left_Elbow_JNT", "Left_Wrist_JNT", WearableSurface.Cloth)
        .Sleeve("Right_Shoulder_JNT", "Right_Elbow_JNT", "Right_Wrist_JNT", WearableSurface.Cloth)
        .Ring("Pelvis_JNT", WearableSketch.Fit, 0.02f, WearableSurface.Gold)
        .Tassels("Pelvis_JNT", WearableSketch.Fit, 5, 0.24f, WearableSurface.Gold)
        .Models("VampireMale_ArmorSet04_Dracula_Chest", "VampireFemale_ArmorSet04_Dracula_Chest")
        .Hide("CHest_GEO", "Chest_GEO", "Legs_GEO", "Bloomers_GEO", "Cape_GEO", "Torso_GEO", "Skirt_GEO", "Cloth_GEO", "MECloth", "OBCloth"));

    /// <summary>Folded hood with a recessed black face opening.</summary>
    public static WearableGraphics RobeHood() => WearableGraphics.Define(WearableSlot.Headgear, s => s
        .Cowl("Head_JNT", 0.15f, WearableSurface.Cloth, WearableSurface.Void)
        .Models("VampireMale_HeadGear_RazerHood_Prefab", "VampireMale_HeadGear_RazerHood_Prefab")
        .Hide("RazerHood", "Hood_GEO", "MEHood", "OBHood", "Headgear", "HeadGear", "Hat_GEO", "Collar_GEO", "Hair"));

    /// <summary>Matching gloves with articulated fingers and a gold wrist band.</summary>
    public static WearableGraphics RobeGloves() => WearableGraphics.Define(WearableSlot.Gloves, s => s
        .Glove("Left_Wrist_JNT", "Left_Elbow_JNT", WearableSurface.Cloth, WearableSurface.Gold)
        .Glove("Right_Wrist_JNT", "Right_Elbow_JNT", WearableSurface.Cloth, WearableSurface.Gold)
        .Models("VampireMale_ArmorSet04_Dracula_Gloves", "VampireFemale_ArmorSet04_Dracula_Gloves")
        .Hide("Gloves_GEO", "Glove_", "Hands_GEO", "Hand_GEO"));

    /// <summary>Matching boots with closed shoes following each ankle-to-ball direction and a gold band.</summary>
    public static WearableGraphics RobeShoes() => WearableGraphics.Define(WearableSlot.Footgear, s => s
        .Loft("Left_Knee_JNT", "Left_Ankle_JNT", 0.07f, 0.055f, WearableSurface.Cloth, followEnd: true)
        .Loft("Right_Knee_JNT", "Right_Ankle_JNT", 0.07f, 0.055f, WearableSurface.Cloth, followEnd: true)
        .Shoe("Left_Ankle_JNT", "Left_Ball_JNT", 0.065f, WearableSurface.Cloth)
        .Shoe("Right_Ankle_JNT", "Right_Ball_JNT", 0.065f, WearableSurface.Cloth)
        .Ring("Left_Ankle_JNT", 0.065f, 0.02f, WearableSurface.Gold)
        .Ring("Right_Ankle_JNT", 0.065f, 0.02f, WearableSurface.Gold)
        .Models("VampireMale_ArmorSet04_Dracula_Boots", "VampireFemale_ArmorSet04_Dracula_Boots")
        .Hide("Boots_GEO", "Boot_GEO", "Footgear", "Feet_GEO", "Foot_GEO"));

    /// <summary>
    /// A staff in the weapon slot: a dark pole up from the right hand and gold tassels at
    /// the top. Same sketch path as the robe. The robe set is the visual proof; this is
    /// the weapon entry on that path.
    /// </summary>
    public static WearableGraphics Staff() => WearableGraphics.Define(WearableSlot.Weapon, s => s
        .Pole("Right_Wrist_JNT", 1.05f, 0.08f, 0.028f, WearableSurface.Cloth)
        .Tassels("Right_Wrist_JNT", 0.05f, 3, 0.16f, WearableSurface.Gold, yOffset: 1.05f)
        .Hide("Sword", "Blade_GEO", "Weapon_GEO", "Spear", "Weapon"));
}

/// <summary>One recorded shape. Game-free; the client mesh builder is the only reader.</summary>
internal readonly struct WearablePart
{
    internal WearablePart(
        WearableShape shape,
        WearableSurface surface,
        string bone,
        string boneTo,
        float a,
        float b,
        float c,
        int n,
        bool followEnd)
    {
        Shape = shape;
        Surface = surface;
        SurfaceB = surface;
        Bone = bone ?? "";
        BoneTo = boneTo ?? "";
        A = a;
        B = b;
        C = c;
        N = n;
        FollowEnd = followEnd;
    }

    internal WearableShape Shape { get; }
    internal WearableSurface Surface { get; }
    internal WearableSurface SurfaceB { get; init; }
    internal string Bone { get; }
    internal string BoneTo { get; }
    internal string? BoneThird { get; init; }
    internal float A { get; }
    internal float B { get; }
    internal float C { get; }
    internal int N { get; }
    internal bool FollowEnd { get; }
}

internal enum WearableShape
{
    Loft = 0,
    Ring = 1,
    Disc = 2,
    Tassels = 3,
    Cap = 4,
    Pole = 5,
    Cowl = 6,
    RobeBody = 7,
    Shoe = 8,
    Skirt = 9,
    Glove = 10,
    Bodice = 11,
    Sleeve = 12,
}

/// <summary>A point in the character model's space. Y is up, and the pose's forward is derived from the shoulders.</summary>
internal readonly struct WearableVec3
{
    internal WearableVec3(float x, float y, float z)
    {
        X = x;
        Y = y;
        Z = z;
    }

    internal float X { get; }
    internal float Y { get; }
    internal float Z { get; }

    public static WearableVec3 operator +(WearableVec3 a, WearableVec3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static WearableVec3 operator -(WearableVec3 a, WearableVec3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static WearableVec3 operator *(WearableVec3 a, float s) => new(a.X * s, a.Y * s, a.Z * s);

    internal float Length() => MathF.Sqrt(X * X + Y * Y + Z * Z);

    internal WearableVec3 Normalized()
    {
        float n = Length();
        return n < 1e-6f ? new WearableVec3(0f, 0f, 1f) : new WearableVec3(X / n, Y / n, Z / n);
    }
}

/// <summary>The mesh <see cref="WearableMeshBuild"/> produces. Up to two bones per vertex.</summary>
internal sealed class WearableMesh
{
    internal WearableAuthoredMesh? Authored;
    internal WearableMesh(
        WearableVec3[] vertices,
        int[] boneIndex,
        int[] boneIndex2,
        float[] boneWeight2,
        string[] bones,
        int[] cloth,
        int[] gold,
        int[] @void,
        string[] missing)
    {
        Vertices = vertices;
        BoneIndex = boneIndex;
        BoneIndex2 = boneIndex2;
        BoneWeight2 = boneWeight2;
        Bones = bones;
        Cloth = cloth;
        Gold = gold;
        Void = @void;
        Missing = missing;
    }

    internal WearableVec3[] Vertices { get; }
    internal int[] BoneIndex { get; }
    internal int[] BoneIndex2 { get; }
    internal float[] BoneWeight2 { get; }
    internal string[] Bones { get; }
    internal int[] Cloth { get; }
    internal int[] Gold { get; }
    internal int[] Void { get; }
    internal string[] Missing { get; }

    internal bool Has(WearableSurface surface) => surface switch
    {
        WearableSurface.Cloth => Cloth.Length > 0,
        WearableSurface.Gold => Gold.Length > 0,
        _ => Void.Length > 0,
    };
}

/// <summary>
/// Which donor renderers a wearable may hide. Body and face remain protected;
/// headgear may explicitly opt into hiding hair with the exact Hair token.
/// </summary>
internal static class WearableHide
{
    private static readonly string[] Protected =
    {
        "Body", "Eye", "Face", "Brow", "Teeth", "Ear", "Skin", "Eyelash",
    };

    internal static bool ShouldHide(string? rendererName, IReadOnlyList<string> prefixes, WearableSlot slot = WearableSlot.Chest)
    {
        if (string.IsNullOrEmpty(rendererName) || prefixes == null || prefixes.Count == 0) return false;
        if (rendererName.IndexOf("Hair", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            bool explicitHair = false;
            for (int i = 0; i < prefixes.Count; i++)
                if (string.Equals(prefixes[i], "Hair", StringComparison.OrdinalIgnoreCase)) explicitHair = true;
            if (slot != WearableSlot.Headgear || !explicitHair) return false;
        }
        // Live equipment often prefixes the mesh (MERazerHood_GEO, MECHest_GEO). Protect
        // body/face parts by Contains so MEBody_GEO is refused; match hide tokens the same way.
        for (int i = 0; i < Protected.Length; i++)
        {
            if (rendererName.IndexOf(Protected[i], StringComparison.OrdinalIgnoreCase) >= 0)
                return false;
        }
        for (int i = 0; i < prefixes.Count; i++)
        {
            string p = prefixes[i];
            if (string.IsNullOrEmpty(p)) continue;
            if (rendererName.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0) return true;
        }
        return false;
    }
}
