using System;
using System.Globalization;
using System.Text.Json;
using ProjectM;
using Unity.Entities;

namespace Progenitor.Dev.Ops;

/// <summary>
/// Debug-bridge <c>equip</c> op. Wears the stack already in a bag slot by calling
/// <see cref="Progenitor.Items.Items.Equip"/>. Does not create an item.
/// </summary>
internal static class EquipOps
{
    public static object Run(World world, JsonElement args)
    {
        if (!Progenitor.Plugin.IsServerProcess)
            throw new InvalidOperationException("equip runs on the server side only");
        if (world == null || !world.IsCreated)
            throw new InvalidOperationException("the game world does not exist yet (not in a game)");

        var em = world.EntityManager;
        var target = GiveArgs.ParseTarget(args);
        var character = target.Kind == GiveArgs.TargetKind.Entity
            ? CharacterLookup.FromEntityId(em, target.Value, "entity")
            : CharacterLookup.FromUserName(em, target.Value);
        if (!em.HasComponent<PlayerCharacter>(character))
            throw new InvalidOperationException("entity " + Format(character) + " is not a PlayerCharacter");

        if (args.ValueKind != JsonValueKind.Object
            || !args.TryGetProperty("slot", out var slotEl)
            || slotEl.ValueKind != JsonValueKind.Number)
            throw new ArgumentException("args.slot (inventory slot index) is required");
        int slot = slotEl.GetInt32();

        var equipped = global::Progenitor.Items.Items.Equip(world, character, slot);
        if (!equipped.Ok)
            throw new InvalidOperationException(equipped.Reason ?? "equip refused");
        Plugin.Logger?.LogInfo("Dev: equip " + Format(character) + " slot " + slot.ToString(CultureInfo.InvariantCulture));
        return (object)equipped;
    }

    private static string Format(Entity entity) =>
        entity.Index.ToString(CultureInfo.InvariantCulture) + ":" + entity.Version.ToString(CultureInfo.InvariantCulture);
}
