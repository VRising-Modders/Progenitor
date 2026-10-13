using System;
using System.Text.Json;
using ProjectM;
using Progenitor.Items;
using Stunlock.Core;
using Unity.Entities;

namespace Progenitor.Dev.Ops;

/// <summary>
/// Debug-bridge <c>give</c> op: add an item stack to a character's inventory. Server, main
/// thread.
///
/// <b>Game path.</b> <c>ProjectM.Scripting.ServerGameManager.TryAddInventoryItem(Entity,
/// PrefabGUID, int)</c> (in <c>ProjectM.Shared.dll</c>), reached through
/// <c>ServerScriptMapper.GetServerGameManager()</c> (the mapper is
/// <c>[SpawnInWorld(2)]</c>, so it exists only on the server). That wrapper
/// carries its own <c>ItemLookupMap</c>, kept in sync with
/// <c>GameDataSystem.ItemHashLookupMap</c> by
/// <c>SetItemLookupMap(GameDataSystem)</c>, so the op does not have to build
/// an <c>AddItemSettings</c> of its own or guess where the item table lives.
///
/// It bottoms out in the same <c>InventoryUtilitiesServer.TryAddItem(AddItemSettings, Entity,
/// PrefabGUID, int)</c> a mod would call directly, and returns the same
/// <c>AddItemResponse</c>: <c>Result</c>, <c>NewEntity</c>,
/// <c>RemainingAmount</c> and <c>Slot</c>. There is no "amount added" field — the amount that
/// landed is <c>requested - RemainingAmount</c>, which is what this op reports as <c>added</c>
/// and <c>didNotFit</c>.
///
/// A <c>Failed_*</c> result is an error, not a quiet zero: <c>Failed_ItemDoesntExist</c> in
/// particular means the GUID is not in the item table, which is the normal outcome for a
/// cloned prefab that was never registered into <c>ItemHashLookupMap</c>.
/// </summary>
public static class GiveOps
{
    /// <summary>
    /// <paramref name="args"/>: <c>user</c> (character name) or <c>entity</c>
    /// (<c>index:version</c>); <c>prefab</c> required (spawnable name or int PrefabGUID hash,
    /// the same argument <c>spawn</c> takes); <c>amount</c> defaults to 1.
    /// </summary>
    public static object Run(World world, JsonElement args)
    {
        try
        {
            if (!Progenitor.Plugin.IsServerProcess)
                throw new InvalidOperationException("give runs on the server side only; only the server owns inventories");
            if (world == null || !world.IsCreated)
                throw new InvalidOperationException("the game world does not exist yet (not in a game)");

            var em = world.EntityManager;
            var target = GiveArgs.ParseTarget(args);
            int amount = GiveArgs.ParseAmount(args);

            var prefabs = world.GetExistingSystemManaged<PrefabCollectionSystem>()
                ?? throw new InvalidOperationException($"PrefabCollectionSystem is not in world '{world.Name}' yet; wait until the world has finished booting");
            var guid = EntityOps.ResolvePrefabGuid(prefabs, args);
            string? prefabName = EntityOps.TryPrefabName(prefabs, guid);

            var character = target.Kind == GiveArgs.TargetKind.Entity
                ? CharacterLookup.FromEntityId(em, target.Value, "entity")
                : CharacterLookup.FromUserName(em, target.Value);

            var options = new ItemGrantOptions
            {
                WhenFull = ItemGrantPolicy.ParseWhenFull(ReadString(args, "whenFull")),
                WhenPresent = ItemGrantPolicy.ParseWhenPresent(ReadString(args, "whenPresent")),
                Equip = ReadBool(args, "equip"),
                Durability = ItemGrantPolicy.ParseDurability(ReadString(args, "durability")),
            };
            var granted = global::Progenitor.Items.Items.Grant(world, character, guid, amount, options);
            if (!granted.Ok)
                throw new InvalidOperationException(granted.Reason ?? "give refused");
            Plugin.Logger?.LogInfo(GiveArgs.LogLine(CharacterLookup.TryName(em, character) ?? Format(character), prefabName ?? guid._Value.ToString(), granted.Requested, granted.Added, granted.DidNotFit));
            return (object)granted;
        }
        catch (Exception e) when (e is not ArgumentException and not InvalidOperationException)
        {
            Plugin.Logger?.LogError($"give op threw: {e}");
            throw new InvalidOperationException($"give op failed: {e.Message}", e);
        }
    }

    private static string? ReadString(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.String)
            return null;
        var text = el.GetString();
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    private static bool ReadBool(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var el))
            return false;
        if (el.ValueKind == JsonValueKind.True) return true;
        if (el.ValueKind == JsonValueKind.False) return false;
        return el.ValueKind == JsonValueKind.String && bool.TryParse(el.GetString(), out var parsed) && parsed;
    }

    private static string Format(Entity entity) => $"{entity.Index}:{entity.Version}";
}
