using System;
using System.Globalization;
using System.Text.Json;

namespace Progenitor.Dev.Ops;

/// <summary>
/// Game-free policy for the debug-bridge <c>heal</c> op: who to heal, which Health/Blood
/// offsets to poke, the blood fallback when MaxBlood reads 0, the log line, and the
/// JSON result shape. Live writes stay in <c>HealOps</c> so this file can compile in
/// the test project without interop assemblies.
/// </summary>
internal static class HealLogic
{
    /// <summary>
    /// <c>Health.MaxHealth._Value</c> (a <c>ModifiableFloat</c> at offset 0). Inferred
    /// from stubs: <c>the IL viewer -t ProjectM.Health</c> on ProjectM.Shared.dll.
    /// </summary>
    public const int HealthMaxOffset = Layout.HealthMaxOffset;

    /// <summary><c>Health.Value</c> current pool. Inferred from stubs.</summary>
    public const int HealthValueOffset = Layout.HealthValueOffset;

    /// <summary>
    /// <c>Health.IsDead</c> (byte at 24). This is the near-death / dead flag on the
    /// character; <c>BloodQualityBuff</c> is left alone. Inferred from stubs.
    /// </summary>
    public const int HealthIsDeadOffset = Layout.HealthIsDeadOffset;

    /// <summary>Minimum Health blob that covers <see cref="HealthIsDeadOffset"/>.</summary>
    public const int MinHealthBytes = HealthIsDeadOffset + 1;

    /// <summary>
    /// <c>Blood.MaxBlood._Value</c> (a <c>ModifiableFloat</c> at offset 0). Inferred
    /// from stubs: <c>the IL viewer -t ProjectM.Blood</c> on ProjectM.Shared.dll.
    /// </summary>
    public const int BloodMaxOffset = Layout.BloodMaxOffset;

    /// <summary><c>Blood.Value</c> current pool. Inferred from stubs.</summary>
    public const int BloodValueOffset = Layout.BloodValueOffset;

    /// <summary>Minimum Blood blob that covers <see cref="BloodValueOffset"/> (float).</summary>
    public const int MinBloodBytes = BloodValueOffset + sizeof(float);

    /// <summary>
    /// Used when <c>Blood.MaxBlood._Value</c> reads 0 (the interop dump of an unset
    /// ModifiableFloat). The old manual <c>set</c> recipe wrote 100.
    /// </summary>
    public const float DefaultBloodWhenMaxIsZero = 100f;

    /// <summary>
    /// Thrown by the live heal op when the character still has <c>ProjectM.Dead</c>.
    /// Filling health and clearing <c>IsDead</c> in that state leaves movement locked
    /// and hides the slain screen.
    /// </summary>
    public const string DeadComponentRefusal =
        "heal refused: character has ProjectM.Dead. heal does not remove it; filling health and clearing IsDead leaves movement locked with no slain screen. Respawn in game (Space, then a waygate).";

    public enum TargetKind
    {
        UserName,
        Entity,
    }

    /// <summary>Who to heal: a <c>User.CharacterName</c> or a character <c>index:version</c>.</summary>
    public readonly struct Target
    {
        public readonly TargetKind Kind;
        public readonly string Value;

        public Target(TargetKind kind, string value)
        {
            Kind = kind;
            Value = value;
        }
    }

    /// <summary>
    /// <c>args.entity</c> (<c>index:version</c>) wins when both are set; otherwise
    /// <c>args.user</c> is the character name. Neither is an error.
    /// </summary>
    public static Target ParseTarget(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("args.user or args.entity is required");

        string? entity = ReadNonEmptyString(args, "entity");
        if (entity != null)
        {
            if (!TryParseEntityId(entity, out _, out _))
                throw new ArgumentException($"args.entity must be 'index:version' (got '{entity}')");
            return new Target(TargetKind.Entity, entity);
        }

        string? user = ReadNonEmptyString(args, "user");
        if (user != null)
            return new Target(TargetKind.UserName, user);

        throw new ArgumentException("args.user or args.entity is required");
    }

    /// <summary>
    /// <c>index:version</c> from <c>users</c> / <c>query</c>. Rejects a bare integer so a
    /// User.Index cannot silently become entity 7 version 0.
    /// </summary>
    public static bool TryParseEntityId(string text, out int index, out int version)
    {
        index = 0;
        version = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var colon = text.IndexOf(':');
        if (colon <= 0 || colon == text.Length - 1) return false;
        return int.TryParse(text.AsSpan(0, colon), NumberStyles.Integer, CultureInfo.InvariantCulture, out index)
               && int.TryParse(text.AsSpan(colon + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out version);
    }

    /// <summary>
    /// Target blood pool: <paramref name="maxBlood"/> when it is positive, otherwise
    /// <see cref="DefaultBloodWhenMaxIsZero"/>. Health never gets this fallback — the
    /// caller writes <c>Health.MaxHealth._Value</c> even when that reads 0.
    /// </summary>
    public static float BloodTarget(float maxBlood) =>
        maxBlood > 0f ? maxBlood : DefaultBloodWhenMaxIsZero;

    /// <summary>The one-line BepInEx log the live loop greps for.</summary>
    public static string LogLine(string name, float healthBefore, float healthAfter, float bloodBefore, float bloodAfter) =>
        FormattableString.Invariant($"Dev: heal {name} health {healthBefore}->{healthAfter} blood {bloodBefore}->{bloodAfter}");

    /// <summary>The log line with what happened to the downed buff, when there was one.</summary>
    public static string LogLine(string name, float healthBefore, float healthAfter, float bloodBefore, float bloodAfter, Downed downed) =>
        LogLine(name, healthBefore, healthAfter, bloodBefore, bloodAfter)
        + (downed.Found ? FormattableString.Invariant($" downed buff {downed.Buff} (guid {downed.Guid}) {(downed.Removed ? "removed" : "not removed")}") : "");

    /// <summary>
    /// The character's downed buff (the buff entity carrying <c>VampireDownedBuff</c>): whether
    /// heal found one, its <c>index:version</c> and PrefabGUID hash, whether the remove was issued, and
    /// whether the instance is already gone in the same frame (it can last one more frame). Healing a
    /// downed player without removing it leaves the player downed.
    /// </summary>
    public readonly record struct Downed(bool Found, bool Removed, string? Buff, int Guid, bool GoneNow = false)
    {
        public static Downed None => new(false, false, null, 0);
    }

    /// <summary>
    /// JSON result: before/after health and blood plus the max we wrote to, and the
    /// <c>IsDead</c> flag we cleared. <paramref name="hasBlood"/> is false when the
    /// character had no Blood component (health still heals).
    /// </summary>
    public static object Result(
        string entity,
        string name,
        float healthBefore,
        float healthAfter,
        float maxHealth,
        float bloodBefore,
        float bloodAfter,
        float maxBlood,
        bool isDeadBefore,
        bool isDeadAfter,
        bool hasBlood,
        Downed? downed = null)
    {
        var d = downed ?? Downed.None;
        return new
        {
            entity,
            name,
            health = new { before = healthBefore, after = healthAfter, max = maxHealth },
            blood = new { before = bloodBefore, after = bloodAfter, max = maxBlood, present = hasBlood },
            isDead = new { before = isDeadBefore, after = isDeadAfter },
            downed = new { found = d.Found, removed = d.Removed, goneNow = d.GoneNow, buff = d.Buff, guid = d.Found ? d.Guid : (int?)null },
        };
    }

    private static string? ReadNonEmptyString(JsonElement args, string name)
    {
        if (!args.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.String)
            return null;
        var value = el.GetString();
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
