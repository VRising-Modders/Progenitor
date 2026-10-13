#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;
using System.Linq;
using ProjectM;
using Progenitor.Commands;
using Progenitor.Items;
using Stunlock.Core;
using Unity.Entities;

namespace Progenitor.Content;

/// <summary>Core-owned Guide clothing; stable item IDs preserve existing inventories.</summary>
internal static class GuideOutfit
{
    private sealed class CoreOwner { public override string ToString() => "Progenitor Guide outfit"; }
    private static readonly object Owner = new CoreOwner();
    private static readonly List<IDisposable> Registrations = new();

    internal static void Uninstall()
    {
        for (int i = Registrations.Count - 1; i >= 0; i--) Registrations[i].Dispose();
        Registrations.Clear();
    }
    internal const string ChestId = "robes.chest";
    internal const string GlovesId = "robes.gloves";
    internal const string HoodId = "robes.hood";
    internal const string ShoesId = "robes.shoes";

    internal static void Install()
    {
        if (Registrations.Count != 0) return;
        try
        {
            // Scholar set names follow the game's Item_*_T09_Dracula_Scholar pattern where those
            // names exist as spawnables. Headgear has no T09_Dracula_Scholar spawnable on this
            // build, so the hood clones Item_Headgear_RazerHood (a known live headgear).
            Define(ChestId, "Item_Chest_T09_Dracula_Scholar", "Guide Robes",
                "A plain robe with a gold tassel belt.", Authored(WearableLooks.RobeChest(), "chest"), "icons/robe.png");
            Define(GlovesId, "Item_Gloves_T09_Dracula_Scholar", "Guide Hands",
                "Cloth gloves with a gold band.", Authored(WearableLooks.RobeGloves(), "gloves"), "icons/gloves.png");
            Define(HoodId, "Item_Headgear_RazerHood", "Guide Hood",
                "A hood that covers the face.", WearableGraphics.FromNativeRenderer(WearableSlot.Headgear, "RazerHood_GEO", "Hair").WithNativeColorOverride(0, "_BaseColor", "primary", 0f, 84f/255f, 182f/255f, 1f).WithNativeFixedColor(0, "_EmissiveColor", 0f, 0f, 0f, 1f).WithNativeWhiteTexture(0, "_BaseColorMap").WithAuthoredOverlayVariants(new[] { ReadAsset("face"), ReadAsset("face-male") }).WithPaletteSource(WearableSlot.Chest), "icons/hood.png");
            Define(ShoesId, "Item_Boots_T09_Dracula_Scholar", "Guide Feet",
                "Low cloth shoes.", Authored(WearableLooks.RobeShoes(), "boots"), "icons/boots.png");

            if (Plugin.IsServerProcess)
                Registrations.Add(CommandRegistry.RegisterType(typeof(RobeCommands), Owner, "progenitor"));
            Plugin.Logger?.LogInfo("Guide outfit: registered built-in chest, gloves, hood and shoes.");
        }
        catch { Uninstall(); throw; }
    }

    private static string ReadAsset(string id)
    {
        using var stream = typeof(GuideOutfit).Assembly.GetManifestResourceStream("progenitor.guide." + id + ".json") ?? throw new System.InvalidOperationException("Missing " + id);
        using var reader = new System.IO.StreamReader(stream); return reader.ReadToEnd();
    }

    private static WearableGraphics Authored(WearableGraphics fallback, string id)
    {
        return WearableGraphics.FromAuthoredJsonVariants(fallback.Slot,
            new[] { ReadAsset(id), ReadAsset(id + "-male") }, fallback.HideNames.ToArray());
    }


    private static void Define(
        string id,
        string cloneOf,
        string name,
        string description,
        WearableGraphics graphics,
        string icon)
    {
        Registrations.Add(PrefabRegistry.DefineItem(id)
            .CloneOf(cloneOf)
            .Named(name)
            .Described(description)
            .Wearable(graphics)
            .Icon(icon)
            .ArmorBaseline(1, removeBonuses: true, removeSet: true)
            // Every Guide piece stays on death and never wears out, however it was obtained.
            // The hood's donor (a cosmetic headgear) is not blood-bound on its own.
            .BloodBound()
            .Indestructible()
            .Register(Owner));
    }
}


/// <summary>Admin grant. The items equip into the slots their sketches name.</summary>
internal static class RobeCommands
{
    [Command("robes", adminOnly: true, usage: ".robes", description: "Admin only. Grant and equip the Guide robes (chest, gloves, hood, shoes).")]
    public static void Robes(ICommandContext ctx)
    {
        if (ctx.CharacterEntity == Entity.Null)
        {
            ctx.Reply("No character entity on this user.");
            return;
        }

        var options = new ItemGrantOptions
        {
            Equip = true,
            WhenPresent = WhenAlreadyPresent.EquipExisting,
            Durability = ItemDurability.Indestructible,
        };
        Grant(ctx, GuideOutfit.ChestId, options);
        Grant(ctx, GuideOutfit.GlovesId, options);
        Grant(ctx, GuideOutfit.HoodId, options);
        Grant(ctx, GuideOutfit.ShoesId, options);
        ctx.Reply("Robe set: look for Wearables: attached on the client. Unequip vanilla legs if a second skirt is still showing.");
    }

    private static bool Qualified(ICommandContext ctx, PrefabGUID guid, out string why)
    {
        why = "";
        var gameData = ctx.World.GetExistingSystemManaged<GameDataSystem>();
        var prefabs = ctx.World.GetExistingSystemManaged<PrefabCollectionSystem>();
        if (gameData == null || !gameData.ItemHashLookupMap.TryGetValue(guid, out var data))
        {
            why = "not in ItemHashLookupMap (CloneOf did not apply)";
            return false;
        }
        if (prefabs == null || !prefabs._PrefabGuidToEntityMap.TryGetValue(guid, out var clone) || clone == Entity.Null)
        {
            why = "clone entity is not in the prefab map";
            return false;
        }
        if (data.Entity != clone)
        {
            why = "ItemData.Entity is " + data.Entity.Index + ":" + data.Entity.Version
                + ", not the clone " + clone.Index + ":" + clone.Version
                + "; a grant would spawn the donor";
            return false;
        }
        return true;
    }

    private static void Grant(ICommandContext ctx, string id, ItemGrantOptions options)
    {
        var guid = new PrefabGUID(PrefabGUID.CalculateGuidHash(id));
        if (!Qualified(ctx, guid, out var why))
        {
            ctx.Reply(id + ": skipped (" + why + ")");
            return;
        }
        var result = global::Progenitor.Items.Items.Grant(ctx.World, ctx.CharacterEntity, guid, 1, options);
        ctx.Reply(id + ": " + (result.Ok ? "ok" : "failed")
            + " action=" + result.Action
            + " equipped=" + result.Equipped
            + (string.IsNullOrEmpty(result.Reason) ? "" : " reason=" + result.Reason));
    }

}

#endif
