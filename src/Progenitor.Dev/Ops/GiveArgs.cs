using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace Progenitor.Dev.Ops;

/// <summary>
/// Game-free argument and result helpers for the debug-bridge <c>give</c> op: add an item
/// stack to a character's inventory. The live add stays in <c>GiveOps</c> so this file can
/// compile in the test project without interop.
///
/// <c>{"user":"Name","prefab":"Item_...","amount":N}</c>. <c>prefab</c> is the same
/// name-or-int-hash argument <c>spawn</c> takes (<see cref="PrefabInspectArgs.Parse"/>), so
/// a clone registered by a consumer mod resolves the same way a vanilla item does.
/// </summary>
internal static class GiveArgs
{
    /// <summary><c>args.amount</c> when omitted.</summary>
    public const int DefaultAmount = 1;

    /// <summary>
    /// Hard cap on one call. The inventory add is a loop over stacks inside the game's own
    /// utility; a five-figure amount on the main thread is a stall, and what does not fit is
    /// reported rather than dropped on the floor anyway.
    /// </summary>
    public const int MaxAmount = 10_000;

    public enum TargetKind
    {
        /// <summary>A <c>User.CharacterName</c>.</summary>
        UserName,

        /// <summary>An <c>index:version</c> character (or a User, whose LocalCharacter is used).</summary>
        Entity,
    }

    /// <summary>Who receives the stack.</summary>
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

        var entity = ReadNonEmptyString(args, "entity");
        if (entity != null)
        {
            if (!InspectArgs.TryParseEntityId(entity, out _, out _))
                throw new ArgumentException($"args.entity must be 'index:version' (got '{entity}')");
            return new Target(TargetKind.Entity, entity);
        }

        var user = ReadNonEmptyString(args, "user");
        if (user != null)
            return new Target(TargetKind.UserName, user);

        throw new ArgumentException("args.user or args.entity is required");
    }

    /// <summary>
    /// <c>args.amount</c>: omitted or unparsable is <see cref="DefaultAmount"/>, then clamped
    /// to 1..<see cref="MaxAmount"/>. A zero or negative amount is a caller mistake, not a
    /// request to remove items, so it clamps up to 1 rather than reaching the inventory.
    /// </summary>
    public static int ParseAmount(JsonElement args)
    {
        int n = DefaultAmount;
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("amount", out var el))
        {
            if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var parsed)) n = parsed;
            else if (el.ValueKind == JsonValueKind.String
                     && int.TryParse(el.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed)) n = parsed;
        }

        return Math.Clamp(n, 1, MaxAmount);
    }

    /// <summary>
    /// How many actually landed, from the requested amount and whatever the game's add
    /// reported as left over. A negative or oversized remainder is clamped so the reported
    /// <c>added</c> can never be negative or exceed the request.
    /// </summary>
    public static int AddedFromRemainder(int requested, int remainder)
    {
        if (remainder < 0) remainder = 0;
        if (remainder > requested) remainder = requested;
        return requested - remainder;
    }

    /// <summary>The one-line BepInEx log the live loop greps for.</summary>
    public static string LogLine(string name, string prefab, int requested, int added, int remainder) =>
        FormattableString.Invariant(
            $"Dev: give {name} {prefab} requested={requested} added={added} didNotFit={remainder}");

    /// <summary>
    /// JSON result. <c>added</c> plus <c>didNotFit</c> always equals <c>requested</c>;
    /// <c>didNotFit</c> is what the inventory had no room for.
    /// </summary>
    public static Dictionary<string, object?> Result(
        string entity,
        string? name,
        int prefabHash,
        string? prefabName,
        int requested,
        int added,
        int remainder,
        string note)
    {
        return new Dictionary<string, object?>
        {
            ["entity"] = entity,
            ["name"] = name,
            ["prefab"] = new Dictionary<string, object?> { ["hash"] = prefabHash, ["name"] = prefabName },
            ["requested"] = requested,
            ["added"] = added,
            ["didNotFit"] = remainder,
            ["note"] = note,
        };
    }

    private static string? ReadNonEmptyString(JsonElement args, string name)
    {
        if (!args.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.String) return null;
        var value = el.GetString();
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
