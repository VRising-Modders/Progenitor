using System;

namespace Progenitor.Effects;

/// <summary>
/// Owned-line look for <see cref="Assets.Beam"/>. Thin is today's additive unlit
/// core. GameRed / GameBlue play a vanilla sequence for sound and particles and,
/// when that clone's visible body is a <c>LineRenderer</c>, copy its material,
/// width, gradient and texture mode onto the owned line once (never the pooled
/// clone — evidence 125). Cardinal's hybrid line is a trail sprite, not a body
/// (evidence 119), so those presets keep the unlit core and add a glow halo.
/// </summary>
public enum BeamStyle
{
    /// <summary>Owned <c>HDRP/Unlit</c> line, 0.12 m, no game-material copy, no glow unless <c>Glow</c> is set.</summary>
    Thin = 0,

    /// <summary>
    /// Red channeling beam. The body is a copy of
    /// <c>Beam_Dracula_BloodSoul_Channeling_01</c> that Progenitor instantiates itself
    /// (evidence 130: 0.24 m, dark red, <c>Shader Graphs/Effect</c>) — the game's own
    /// material draws the line and every write lands on our copy, never on a pooled
    /// clone. The castle family (servant-coffin / invalid-placement / arena-flag
    /// sequence GUIDs: <c>ServantCoffinEffects</c>,
    /// <c>BlueprintData.InvalidPlaceSequence</c>,
    /// <c>CastleArenaFlagBuff_Client.BeamSequenceGUID</c>) is still preferred
    /// for the played sequence when one of those entities exists; when none do, the
    /// Cardinal sequence supplies the sound and the borrowed line supplies the pixels.
    /// Falls back to a Thin core with the copied material when no prefab resolves.
    /// </summary>
    GameRed = 1,

    /// <summary>
    /// Cardinal find-chest buff <c>AB_Cardinal_SummonOrb_Beam_Buff</c>
    /// (<c>PrefabGUID(-2014639169)</c>, evidence 80). The sequence prefab is guid
    /// 586432528. Visible body is the particle/trail graph, not the hybrid line.
    /// </summary>
    GameBlue = 2,

    /// <summary>
    /// Blood-feed channeling beam: a copy of <c>Beam_Feed_Channeling_01</c>
    /// (evidence 130: 0.247 m, <c>Shader Graphs/Liquid</c>). Same borrowed-prefab body
    /// as <see cref="GameRed"/>, a different vanilla look.
    /// </summary>
    GameFeed = 3,
}

/// <summary>
/// Hashes, default tints, glow knobs and HDR-preserving tint math for
/// <see cref="BeamStyle"/> presets. Live GameRed GUIDs are not in the game's metadata (they
/// are baked onto coffin / blueprint / flag prefabs); the Unity runtime walks
/// those components and the inspect spike lists what it found.
/// </summary>
public static class BeamCatalog
{
    /// <summary>Buff id for the Cardinal find-chest beam.</summary>
    public const int CardinalBuffHash = -2014639169;

    /// <summary>Spawnable name of <see cref="CardinalBuffHash"/>.</summary>
    public const string CardinalBuffName = "AB_Cardinal_SummonOrb_Beam_Buff";

    /// <summary>Decimal form of <see cref="CardinalBuffHash"/>; Sample borrows this string.</summary>
    public const string CardinalBuffHashName = "-2014639169";

    /// <summary>
    /// Sequence prefab the Cardinal buff plays (evidence 80: client entity guid
    /// 586432528 after resolving the buff's <c>BuffSequence</c>).
    /// </summary>
    public const int CardinalSequenceHash = 586432528;

    /// <summary>Default halo width in meters (3× the 0.12 m core) when the game material has no emission glow.</summary>
    public const float DefaultGlowWidth = 0.36f;

    /// <summary>Halo alpha multiplied onto the core color when a second owned line is created.</summary>
    public const float DefaultGlowAlpha = 0.35f;

    /// <summary>HDR red used when GameRed has no copied material (thin core + glow).</summary>
    public static readonly EffectColor GameRedTint = new(1.8f, 0.14f, 0.06f, 1f);

    /// <summary>Soft blue matching today's owned-line default (Cardinal startColor family).</summary>
    public static readonly EffectColor GameBlueTint = new(0.25f, 0.55f, 1f, 0.8f);

    /// <summary>Borrow names tried for Thin and GameBlue, hash first then wiki name.</summary>
    public static readonly string[] CardinalBorrowNames =
    {
        CardinalBuffHashName,
        CardinalBuffName,
    };

    /// <summary>Feed-beam tint: the Liquid shader is near-white, so the copy keeps its own color.</summary>
    public static readonly EffectColor GameFeedTint = new(1f, 0.24f, 0.2f, 0.9f);

    /// <summary>
    /// One vanilla two-point beam prefab whose <b>visible body is the LineRenderer</b>
    /// (not a trail sprite or a particle graph), so a copy of it can be driven by
    /// <c>SetPosition(0/1)</c> and the game's own material draws the beam. Names, widths
    /// and shaders are the live client catalog in evidence 130; they are asset names and
    /// do not appear in the game's metadata. The mechanism they rely on is metadata-backed:
    /// <c>HybridLineRendererComponent</c> owns a <c>LineRenderer</c> plus a
    /// <c>List&lt;Vector3&gt; Positions</c> and
    /// <c>ParticleLifetimeSystem.UpdateLineRendererPositionElement_Job</c>
    /// copies <c>LineRendererPositionElement</c>
    /// into it every frame — which is why the hybrid component is
    /// removed from our copy before we drive it.
    /// </summary>
    public readonly struct GameLineBeam
    {
        public GameLineBeam(string prefabName, string shader, float width)
        {
            PrefabName = prefabName;
            Shader = shader;
            Width = width;
        }

        /// <summary>Exact GameObject name of the loaded prefab.</summary>
        public string PrefabName { get; }

        /// <summary>Shader on its LineRenderer material (evidence 130).</summary>
        public string Shader { get; }

        /// <summary>Its own <c>startWidth</c> in meters (evidence 130).</summary>
        public float Width { get; }
    }

    /// <summary>
    /// Red family, best first. Dracula's blood-soul channel is the only cataloged beam
    /// that is already dark red; charm and subdue are the same <c>Shader Graphs/Effect</c>
    /// family and are tinted to red by the preset.
    /// </summary>
    public static readonly GameLineBeam[] GameRedLineBeams =
    {
        new("Beam_Dracula_BloodSoul_Channeling_01", "Shader Graphs/Effect", 0.24f),
        new("Beam_Charm_Channeling_01", "Shader Graphs/Effect", 0.531f),
        new("Beam_Subdue_Channeling_01", "Shader Graphs/Effect", 0.531f),
        new("Part_WarEvent_ShadowRift_Beam", "Shader Graphs/Effect", 1.941f),
    };

    /// <summary>Feed family: the blood-feed channel beam, then the wider war-event node line.</summary>
    public static readonly GameLineBeam[] GameFeedLineBeams =
    {
        new("Beam_Feed_Channeling_01", "Shader Graphs/Liquid", 0.247f),
        new("Part_WarEvent_SubToMainNode_Beam", "Shader Graphs/Effect", 1.406f),
    };

    /// <summary>
    /// Ordered prefab candidates for <paramref name="style"/>, or an empty array when the
    /// style draws with the owned unlit line (Thin) or a particle graph (GameBlue —
    /// Cardinal's line is a trail sprite, evidence 119).
    /// </summary>
    public static GameLineBeam[] GameLineBeamsFor(BeamStyle style)
    {
        return style switch
        {
            BeamStyle.GameRed => GameRedLineBeams,
            BeamStyle.GameFeed => GameFeedLineBeams,
            _ => System.Array.Empty<GameLineBeam>(),
        };
    }

    /// <summary>True when <paramref name="style"/> wants a borrowed game-line body.</summary>
    public static bool WantsGameLine(BeamStyle style) => GameLineBeamsFor(style).Length > 0;

    /// <summary>Default tint for <paramref name="style"/> when the caller set no color.</summary>
    public static EffectColor DefaultTint(BeamStyle style)
    {
        return style switch
        {
            BeamStyle.GameRed => GameRedTint,
            BeamStyle.GameFeed => GameFeedTint,
            BeamStyle.GameBlue => GameBlueTint,
            _ => GameBlueTint,
        };
    }

    /// <summary>
    /// A beam borrowed from <see cref="GameLineBeamsFor"/> is a copy we own, so its
    /// LineRenderer, material and color may be written; the pooled sequencer clone may
    /// not (<see cref="CloneWritePolicy"/>).
    /// </summary>
    public static bool IsGameLinePrefabName(BeamStyle style, string? name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        var list = GameLineBeamsFor(style);
        for (int i = 0; i < list.Length; i++)
        {
            if (string.Equals(list[i].PrefabName, name, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    /// <summary>True when <paramref name="name"/> is the Cardinal buff hash or spawnable name.</summary>
    public static bool IsCardinalName(string? name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        return name == CardinalBuffHashName
            || string.Equals(name, CardinalBuffName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Default Sample <c>.fx beam</c> style: GameRed when this client can supply the red
    /// body — either a borrowed game-line prefab (evidence 130, the usual case) or a castle
    /// sequence GUID — otherwise Thin.
    /// </summary>
    internal static BeamStyle Preferred(IEffectRuntime runtime)
    {
        if (runtime == null) return BeamStyle.Thin;
        try
        {
            if (!string.IsNullOrEmpty(runtime.ResolveBeamLinePrefab(BeamStyle.GameRed, out _)))
                return BeamStyle.GameRed;
        }
        catch
        {
            // Resolver may throw from a missing world; keep checking the sequence path.
        }

        try
        {
            if (!string.IsNullOrEmpty(runtime.ResolveBeamPreset(BeamStyle.GameRed, out _)))
                return BeamStyle.GameRed;
        }
        catch
        {
            // Resolver may throw from a missing world; Thin is the floor.
        }

        return BeamStyle.Thin;
    }
}
