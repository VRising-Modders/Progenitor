using System;
using System.Globalization;
using System.Text;

namespace Progenitor.Ui.GameArt;

/// <summary>
/// BUTTONART1: the <b>kept</b> rung of the borrowed-art ladders (<see cref="GameArtSource.Kept"/>), as
/// game-free names. Once a part's owned copy has been made in this process, its pixels are also handed to
/// Bootstrap's process-lifetime sprite table (<c>Progenitor.Bootstrap.IconCache</c>, through
/// <c>BootstrapLink.TryGetOrCreateSprite</c>), which outlives every Core swap. A later Core generation that
/// finds no live donor (a swap in a world, where the title's menus and their textures are gone) adopts that
/// copy instead of guessing at a loaded sprite by name.
///
/// <para>The table keeps a texture and a plain sprite (full rect, no border). What the ladder also needs —
/// the part, the donor's sprite name, the 9-slice border, pixels per unit, the image type and the donor
/// image's color — is written into the row's relative path (<see cref="Path"/>). Bootstrap names the
/// texture and sprite <c>progenitor-icon-</c> plus that file name, so the next generation reads it back
/// from the sprite's name (<see cref="TryParse"/>) during the resident scan it already makes
/// (<c>Resources.FindObjectsOfTypeAll(Sprite)</c>). Nothing Core-side is static across generations: the
/// metadata travels with the Unity object Bootstrap keeps.</para>
/// </summary>
internal static class GameArtKept
{
    /// <summary>The IconCache owner id of every kept part (never a real plugin, so no file removal releases it).</summary>
    internal const string PluginId = "progenitor.core.game-art";

    /// <summary>The folder of every kept part's relative path.</summary>
    internal const string Folder = "game-art/";

    /// <summary>Bootstrap's name for a table texture and sprite: this prefix plus the file name without extension.</summary>
    internal const string IconPrefix = "progenitor-icon-";

    internal const char Separator = '~';

    /// <summary>The donor name recorded for a part drawn from the kept rung.</summary>
    internal const string DonorKept = "kept";

    /// <summary>One kept part, as read back from its name.</summary>
    internal readonly struct Entry
    {
        internal Entry(string tagPart, string sprite, (float L, float B, float R, float T) border, float ppu, bool sliced,
            (float R, float G, float B, float A) color)
        {
            TagPart = tagPart ?? "";
            Sprite = sprite ?? "";
            Border = border;
            Ppu = ppu;
            Sliced = sliced;
            Color = color;
        }

        /// <summary>The ladder's asset tag and the part's name (<c>settings-art-menubutton</c>), lowercase.</summary>
        internal string TagPart { get; }

        /// <summary>The donor sprite's name, lowercase (the table lowercases paths).</summary>
        internal string Sprite { get; }

        internal (float L, float B, float R, float T) Border { get; }

        internal float Ppu { get; }

        internal bool Sliced { get; }

        internal (float R, float G, float B, float A) Color { get; }

        internal bool HasBorder => Border.L > 0f || Border.B > 0f || Border.R > 0f || Border.T > 0f;
    }

    /// <summary>The <c>TagPart</c> for a ladder's asset tag and a part name (lowercase).</summary>
    internal static string TagPart(string? tag, string? part) => ((tag ?? "") + (part ?? "")).ToLowerInvariant();

    /// <summary>
    /// The relative path a kept part is stored under:
    /// <c>game-art/{tag}{part}~{sprite}~b{L}_{B}_{R}_{T}~p{ppu}~{s|n}~c{RRGGBBAA}.png</c>, numbers in
    /// hundredths (no dot, so the file name keeps one extension), lowercase as the table stores it.
    /// </summary>
    internal static string Path(string? tag, string? part, string? sprite, (float L, float B, float R, float T) border, float ppu,
        bool sliced, (float R, float G, float B, float A) color)
    {
        var sb = new StringBuilder(96);
        sb.Append(Folder).Append(Safe(TagPart(tag, part))).Append(Separator).Append(Safe(sprite));
        sb.Append(Separator).Append('b').Append(Hundredths(border.L)).Append('_').Append(Hundredths(border.B))
            .Append('_').Append(Hundredths(border.R)).Append('_').Append(Hundredths(border.T));
        sb.Append(Separator).Append('p').Append(Hundredths(ppu));
        sb.Append(Separator).Append(sliced ? 's' : 'n');
        sb.Append(Separator).Append('c').Append(Byte(color.R)).Append(Byte(color.G)).Append(Byte(color.B)).Append(Byte(color.A));
        sb.Append(".png");
        return sb.ToString().ToLowerInvariant();
    }

    /// <summary>
    /// Read a kept part back from the table's sprite or texture name (<c>progenitor-icon-…</c>) or from its
    /// path. False for anything else, including another plugin's icon.
    /// </summary>
    internal static bool TryParse(string? name, out Entry entry)
    {
        entry = default;
        if (string.IsNullOrEmpty(name)) return false;
        string s = name!.Trim();
        if (s.StartsWith(IconPrefix, StringComparison.OrdinalIgnoreCase)) s = s.Substring(IconPrefix.Length);
        else if (s.StartsWith(Folder, StringComparison.OrdinalIgnoreCase)) s = s.Substring(Folder.Length);
        else return false;
        if (s.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) s = s.Substring(0, s.Length - 4);
        var bits = s.Split(Separator);
        if (bits.Length != 6) return false;
        if (bits[0].Length == 0 || bits[1].Length == 0) return false;
        if (bits[2].Length < 2 || char.ToLowerInvariant(bits[2][0]) != 'b') return false;
        var border = bits[2].Substring(1).Split('_');
        if (border.Length != 4) return false;
        if (!TryHundredths(border[0], out float l) || !TryHundredths(border[1], out float b)
            || !TryHundredths(border[2], out float r) || !TryHundredths(border[3], out float t)) return false;
        if (bits[3].Length < 2 || char.ToLowerInvariant(bits[3][0]) != 'p' || !TryHundredths(bits[3].Substring(1), out float ppu) || ppu <= 0f)
            return false;
        bool sliced;
        if (string.Equals(bits[4], "s", StringComparison.OrdinalIgnoreCase)) sliced = true;
        else if (string.Equals(bits[4], "n", StringComparison.OrdinalIgnoreCase)) sliced = false;
        else return false;
        if (bits[5].Length != 9 || char.ToLowerInvariant(bits[5][0]) != 'c') return false;
        if (!TryByte(bits[5], 1, out float cr) || !TryByte(bits[5], 3, out float cg) || !TryByte(bits[5], 5, out float cb)
            || !TryByte(bits[5], 7, out float ca)) return false;
        entry = new Entry(bits[0].ToLowerInvariant(), bits[1].ToLowerInvariant(), (l, b, r, t), ppu, sliced, (cr, cg, cb, ca));
        return true;
    }

    /// <summary>The entry is the kept copy of <paramref name="part"/> on the ladder tagged <paramref name="tag"/>.</summary>
    internal static bool IsFor(in Entry entry, string? tag, string? part) =>
        string.Equals(entry.TagPart, Safe(TagPart(tag, part)), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// When several kept copies exist for one part (its donor sprite changed during the process), the one of
    /// the part's own sprite name wins over another; otherwise the first found stays.
    /// </summary>
    internal static bool Prefer(bool haveCurrent, string? currentSprite, string? candidateSprite, string? primarySprite)
    {
        if (!haveCurrent) return true;
        bool currentPrimary = SameSprite(currentSprite, primarySprite);
        bool candidatePrimary = SameSprite(candidateSprite, primarySprite);
        return candidatePrimary && !currentPrimary;
    }

    private static bool SameSprite(string? a, string? b) =>
        !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b) && string.Equals(Safe(a), Safe(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>A path segment: letters, digits, <c>_</c> and <c>-</c> only (anything else becomes <c>_</c>), lowercase.</summary>
    internal static string Safe(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var sb = new StringBuilder(text!.Length);
        foreach (char c in text)
            sb.Append(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-' ? char.ToLowerInvariant(c) : '_');
        return sb.ToString();
    }

    private static string Hundredths(float value)
    {
        if (float.IsNaN(value) || float.IsInfinity(value) || value < 0f) value = 0f;
        long n = (long)Math.Round(value * 100.0, MidpointRounding.AwayFromZero);
        return n.ToString(CultureInfo.InvariantCulture);
    }

    private static bool TryHundredths(string text, out float value)
    {
        value = 0f;
        if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out long n)) return false;
        value = (float)(n / 100.0);
        return true;
    }

    private static string Byte(float channel)
    {
        if (float.IsNaN(channel)) channel = 0f;
        int b = (int)Math.Round(Math.Max(0f, Math.Min(1f, channel)) * 255.0, MidpointRounding.AwayFromZero);
        return b.ToString("x2", CultureInfo.InvariantCulture);
    }

    private static bool TryByte(string text, int at, out float channel)
    {
        channel = 0f;
        if (at + 2 > text.Length) return false;
        if (!int.TryParse(text.Substring(at, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int b)) return false;
        channel = b / 255f;
        return true;
    }
}
