using System;
using System.Globalization;

namespace Progenitor.Units;

/// <summary>
/// Game-free rules for <see cref="PluginUnits.SetBlood(Unity.Entities.Entity, string, float)"/>:
/// quality range, the blood type name form, the event payload bytes and the refusal texts.
/// Unit-tested without the game.
/// </summary>
public static class UnitBloodPolicy
{
    /// <summary>Blood quality is a percentage, 0-100, as the game shows it.</summary>
    public const float MaxQuality = 100f;

    /// <summary>Blood amount the event gives: the game's full pool (a player's <c>Blood.MaxBlood</c> is 100).</summary>
    public const int FullAmount = 100;

    /// <summary>Bytes of <c>ConsumeBloodAdminEvent</c> this writes: up to the flag byte at 0x15.</summary>
    public const int PayloadSize = 0x16;

    public static bool IsValidQuality(float quality) =>
        !float.IsNaN(quality) && quality >= 0f && quality <= MaxQuality;

    /// <summary>
    /// The prefab name for a blood type: <c>"Draculin"</c> and <c>"BloodType_Draculin"</c> both give
    /// <c>"BloodType_Draculin"</c>. Case is kept; the game's names are capitalized.
    /// </summary>
    public static string PrefabName(string bloodType)
    {
        if (string.IsNullOrWhiteSpace(bloodType)) throw new ArgumentException("A blood type is required.", nameof(bloodType));
        string name = bloodType.Trim();
        return name.StartsWith("BloodType_", StringComparison.Ordinal) ? name : "BloodType_" + name;
    }

    /// <summary>
    /// The <c>ConsumeBloodAdminEvent</c> payload: full amount, the primary type and quality, no
    /// secondary type (0), secondary quality 0, no tier-4 secondary buff. Every other byte is zero.
    /// </summary>
    public static void WritePayload(Span<byte> payload, int bloodTypeGuid, float quality)
    {
        if (payload.Length < PayloadSize)
            throw new ArgumentException("payload needs " + PayloadSize + " bytes", nameof(payload));
        payload.Slice(0, PayloadSize).Clear();
        WriteInt(payload, Layout.ConsumeBloodAmountOffset, FullAmount);
        WriteInt(payload, Layout.ConsumeBloodPrimaryQualityOffset, BitConverter.SingleToInt32Bits(quality));
        WriteInt(payload, Layout.ConsumeBloodPrimaryTypeOffset, bloodTypeGuid);
    }

    public static string QualityRefusal(float quality) =>
        "blood quality " + quality.ToString("0.##", CultureInfo.InvariantCulture) + " is outside 0-100";

    public static string UnknownTypeRefusal(string name) =>
        "no blood type prefab named '" + name + "' (a blood type is a BloodType_ prefab)";

    public static string NoBloodRefusal(int index, int version) =>
        "entity " + index + ":" + version + " is not a player character with blood";

    private static void WriteInt(Span<byte> span, int offset, int value)
    {
        span[offset] = (byte)value;
        span[offset + 1] = (byte)(value >> 8);
        span[offset + 2] = (byte)(value >> 16);
        span[offset + 3] = (byte)(value >> 24);
    }
}
