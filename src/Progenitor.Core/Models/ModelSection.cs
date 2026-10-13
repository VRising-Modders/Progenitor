using System;
using System.Globalization;

namespace Progenitor.Models;

/// <summary>
/// One section of an entity scene that holds a presentation prefab: the scene GUID as the content
/// catalog spells it (32 lowercase hex, the <c>Object: scene:section</c> form in the shipped
/// <c>archive_dependencies.txt</c>) and the section index. Hybrid character models
/// (<c>HYB_*</c>) live one per section of the hybrid-model scene; for example
/// <c>HYB_NPCDracula_Evolved</c> is <c>ddfc6a4db4009e2448b8f8b6f0965b8f:373</c> on the
/// 2026-09 build. <c>tools/catalog/model-sections.mjs</c> generates the name table.
/// </summary>
public readonly struct ModelSection : IEquatable<ModelSection>
{
    public ModelSection(string sceneGuid, int index)
    {
        if (!IsSceneGuid(sceneGuid))
            throw new ArgumentException("scene guid must be 32 hex characters: '" + sceneGuid + "'.", nameof(sceneGuid));
        if (index < 0)
            throw new ArgumentOutOfRangeException(nameof(index), index, "section index must be 0 or more.");
        SceneGuid = sceneGuid.ToLowerInvariant();
        Index = index;
    }

    /// <summary>
    /// No section: the prefab is resident (always loaded by the game, like the player models
    /// <c>HYB_VampireFemale_Prefab</c> and <c>HYB_VampireMale_Prefab</c>) and is only looked up by name.
    /// </summary>
    public static ModelSection None => default;

    /// <summary>True for <see cref="None"/>.</summary>
    public bool IsNone => SceneGuid == null;

    /// <summary>32 lowercase hex characters, catalog spelling; null for <see cref="None"/>.</summary>
    public string SceneGuid { get; }

    /// <summary><c>SceneSectionData.SubSectionIndex</c>.</summary>
    public int Index { get; }

    /// <summary>Parses <c>scene:index</c>, the catalog's object spelling.</summary>
    public static ModelSection Parse(string text)
    {
        if (!TryParse(text, out var section))
            throw new FormatException("expected '<32 hex>:<section index>', got '" + text + "'.");
        return section;
    }

    public static bool TryParse(string? text, out ModelSection section)
    {
        section = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        int colon = text!.IndexOf(':');
        if (colon != 32) return false;
        string scene = text.Substring(0, 32);
        if (!IsSceneGuid(scene)) return false;
        if (!int.TryParse(text.Substring(33), NumberStyles.None, CultureInfo.InvariantCulture, out int index)) return false;
        section = new ModelSection(scene, index);
        return true;
    }

    /// <summary>
    /// The four <c>uint</c> words of the scene's <c>Unity.Entities.Hash128</c> as they sit in
    /// <c>SceneSectionData.SceneGUID</c>. <c>Hash128.ToString</c> (the catalog spelling) writes each
    /// word's bytes little-endian with the two hex digits of every byte swapped, so
    /// <c>ddfc6a4d…</c> is word 0 <c>0xd4a6cfdd</c>. Verified live 2026-09-28 against the section
    /// entities of scene <c>ddfc6a4db4009e2448b8f8b6f0965b8f</c>.
    /// </summary>
    public void ToHashWords(out uint w0, out uint w1, out uint w2, out uint w3)
    {
        if (IsNone) throw new InvalidOperationException("ModelSection.None has no scene.");
        w0 = Word(SceneGuid, 0);
        w1 = Word(SceneGuid, 8);
        w2 = Word(SceneGuid, 16);
        w3 = Word(SceneGuid, 24);
    }

    /// <summary>Inverse of <see cref="ToHashWords"/>: the catalog spelling of four in-memory words.</summary>
    public static string SceneGuidFromHashWords(uint w0, uint w1, uint w2, uint w3)
    {
        var chars = new char[32];
        Spell(w0, chars, 0);
        Spell(w1, chars, 8);
        Spell(w2, chars, 16);
        Spell(w3, chars, 24);
        return new string(chars);
    }

    private static uint Word(string hex, int at)
    {
        uint word = 0;
        for (int b = 0; b < 4; b++)
        {
            // Each byte is written low nibble first.
            int lo = HexValue(hex[at + b * 2]);
            int hi = HexValue(hex[at + b * 2 + 1]);
            word |= (uint)((hi << 4) | lo) << (8 * b);
        }
        return word;
    }

    private static void Spell(uint word, char[] into, int at)
    {
        const string digits = "0123456789abcdef";
        for (int b = 0; b < 4; b++)
        {
            uint value = (word >> (8 * b)) & 0xff;
            into[at + b * 2] = digits[(int)(value & 0xf)];
            into[at + b * 2 + 1] = digits[(int)(value >> 4)];
        }
    }

    private static int HexValue(char c) =>
        c >= '0' && c <= '9' ? c - '0'
        : c >= 'a' && c <= 'f' ? c - 'a' + 10
        : c >= 'A' && c <= 'F' ? c - 'A' + 10
        : throw new FormatException("not a hex digit: '" + c + "'.");

    private static bool IsSceneGuid(string? text)
    {
        if (text == null || text.Length != 32) return false;
        foreach (char c in text)
            if (!Uri.IsHexDigit(c)) return false;
        return true;
    }

    public bool Equals(ModelSection other) =>
        Index == other.Index && string.Equals(SceneGuid, other.SceneGuid, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is ModelSection other && Equals(other);

    public override int GetHashCode() => (SceneGuid?.GetHashCode() ?? 0) * 397 ^ Index;

    public override string ToString() => IsNone ? "resident" : SceneGuid + ":" + Index.ToString(CultureInfo.InvariantCulture);

    public static bool operator ==(ModelSection a, ModelSection b) => a.Equals(b);
    public static bool operator !=(ModelSection a, ModelSection b) => !a.Equals(b);
}
