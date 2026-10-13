using System;
using Progenitor.Content;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// <c>DefineItem(...).BloodBound()</c> / <c>.Indestructible()</c>: the game-free category and
/// durability decisions, plus pins that the Guide outfit and both builders use them.
/// </summary>
public class ItemKeepRulesTests
{
    const long Armor = 2;
    const long BloodBound = ItemKeepRules.BloodBoundBit;
    const long LoseOnDeath = ItemKeepRules.LoseDurabilityOnDeathBit;
    const long SoulBound = 256;
    const long Locked = unchecked((long)0xFFFFFFFF80000000UL); // ItemCategory.Locked, sign-extended

    [Fact]
    public void Bits_match_the_game_enum()
    {
        Assert.Equal(8L, ItemKeepRules.BloodBoundBit);
        Assert.Equal(2048L, ItemKeepRules.LoseDurabilityOnDeathBit);
        Assert.Equal(0, ItemKeepRules.LossTypeNone);
    }

    [Fact]
    public void BloodBound_sets_the_bit_and_keeps_every_other_bit()
    {
        long donor = Armor | LoseOnDeath | Locked;
        long result = ItemKeepRules.Category(donor, donor, bloodBound: true, indestructible: false);
        Assert.Equal(Armor | LoseOnDeath | Locked | BloodBound, result);
    }

    [Fact]
    public void Indestructible_clears_death_durability_loss_only()
    {
        long donor = Armor | LoseOnDeath | SoulBound;
        long result = ItemKeepRules.Category(donor, donor, bloodBound: false, indestructible: true);
        Assert.Equal(Armor | SoulBound, result);
    }

    [Fact]
    public void Both_options_on_a_cosmetic_hood_donor()
    {
        // The hood's donor is not blood-bound; the Guide hood must be.
        long donor = Armor | LoseOnDeath;
        long result = ItemKeepRules.Category(donor, donor, bloodBound: true, indestructible: true);
        Assert.Equal(Armor | BloodBound, result);
    }

    [Fact]
    public void Dropping_the_options_restores_the_donor_bits_on_a_kept_clone()
    {
        long donor = Armor | LoseOnDeath;
        long kept = ItemKeepRules.Category(donor, donor, bloodBound: true, indestructible: true);
        long reverted = ItemKeepRules.Category(kept, donor, bloodBound: false, indestructible: false);
        Assert.Equal(donor, reverted);
    }

    [Fact]
    public void A_donor_that_is_already_blood_bound_stays_so_without_the_option()
    {
        long donor = Armor | BloodBound | LoseOnDeath;
        Assert.Equal(donor, ItemKeepRules.Category(donor, donor, bloodBound: false, indestructible: false));
    }

    [Fact]
    public void Option_off_does_not_invent_a_bit_the_donor_lacks()
    {
        long donor = Armor;
        long clone = Armor | BloodBound | LoseOnDeath;
        Assert.Equal(Armor, ItemKeepRules.Category(clone, donor, bloodBound: false, indestructible: false));
    }

    [Fact]
    public void Instance_category_only_adds_what_the_policy_asks_for()
    {
        long saved = Armor | LoseOnDeath;
        Assert.Equal(Armor | BloodBound, ItemKeepRules.InstanceCategory(saved, true, true));
        Assert.Equal(saved | BloodBound, ItemKeepRules.InstanceCategory(saved, true, false));
        Assert.Equal(Armor, ItemKeepRules.InstanceCategory(saved, false, true));
        Assert.Equal(saved, ItemKeepRules.InstanceCategory(saved, false, false));
        // Never strips a bit the save put there.
        Assert.Equal(saved | BloodBound, ItemKeepRules.InstanceCategory(saved | BloodBound, false, false));
    }

    [Fact]
    public void KeepDurability_stops_every_loss_and_refills()
    {
        int lossType = 1;               // LossOnDamageTaken
        float factor = 1f;
        bool destroy = true;
        float value = 12.5f;
        Assert.True(ItemKeepRules.KeepDurability(ref lossType, ref factor, ref destroy, ref value, 346f));
        Assert.Equal(0, lossType);
        Assert.Equal(0f, factor);
        Assert.False(destroy);
        Assert.Equal(346f, value);
    }

    [Fact]
    public void KeepDurability_is_idempotent()
    {
        int lossType = 0;
        float factor = 0f;
        bool destroy = false;
        float value = 346f;
        Assert.False(ItemKeepRules.KeepDurability(ref lossType, ref factor, ref destroy, ref value, 346f));
        Assert.Equal(346f, value);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(-5f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void KeepDurability_leaves_the_value_when_the_maximum_is_not_usable(float max)
    {
        int lossType = 2;
        float factor = 0.25f;
        bool destroy = false;
        float value = 7f;
        Assert.True(ItemKeepRules.KeepDurability(ref lossType, ref factor, ref destroy, ref value, max));
        Assert.Equal(7f, value);
        Assert.Equal(0, lossType);
    }

    [Fact]
    public void KeepDurability_refills_a_broken_item()
    {
        int lossType = 0;
        float factor = 0f;
        bool destroy = false;
        float value = 0f;
        Assert.True(ItemKeepRules.KeepDurability(ref lossType, ref factor, ref destroy, ref value, 816.95f));
        Assert.Equal(816.95f, value);
    }

    // ---- pins -------------------------------------------------------------------------------

    static string Content(string file) => SourcePin.ReadFile("src", "Progenitor.Core", "Content", file);

    [Fact]
    public void Every_Guide_item_is_blood_bound_and_indestructible()
    {
        string body = SourcePin.MethodBody(Content("GuideOutfit.cs"), "private static void Define(");
        SourcePin.AssertStatementExactly(body, ".BloodBound()");
        SourcePin.AssertStatementExactly(body, ".Indestructible()");
        string install = SourcePin.MethodBody(Content("GuideOutfit.cs"), "internal static void Install()");
        foreach (string id in new[] { "ChestId", "GlovesId", "HoodId", "ShoesId" })
            SourcePin.AssertStatement(install, "Define(" + id + ",");
    }

    [Fact]
    public void Both_builders_expose_the_options()
    {
        string registry = Content("PrefabRegistry.cs");
        SourcePin.AssertStatement(registry, "public ItemDefinitionBuilder BloodBound()");
        SourcePin.AssertStatement(registry, "public ItemDefinitionBuilder Indestructible()");
        SourcePin.AssertStatementExactly(registry, "if (_keep != null) def.AssignKeep(_keep);");

        string context = SourcePin.ReadFile("src", "Progenitor.Core", "Plugins", "PluginContext.cs");
        SourcePin.AssertStatement(context, "public ItemDefinitionBuilder BloodBound()");
        SourcePin.AssertStatementExactly(context, "_inner.BloodBound();");
        SourcePin.AssertStatement(context, "public ItemDefinitionBuilder Indestructible()");
        SourcePin.AssertStatementExactly(context, "_inner.Indestructible();");
    }

    [Fact]
    public void Category_reaches_the_lookup_row_the_death_drop_reads()
    {
        // The row is built from the clone's ItemData, so the category must be written first.
        string insert = SourcePin.MethodBody(Content("PrefabApplicator.cs"), "private static bool InsertNew(");
        int category = insert.IndexOf("ItemKeepPolicy.ApplyCategory(ref itemData, def.Keep", StringComparison.Ordinal);
        int row = insert.IndexOf("gameData.ItemHashLookupMap.TryAdd(target, itemData)", StringComparison.Ordinal);
        Assert.True(category > 0 && row > category, "InsertNew must write the keep category before the map row");

        string update = SourcePin.MethodBody(Content("PrefabApplicator.cs"), "private static bool UpdateExisting(");
        category = update.IndexOf("ItemKeepPolicy.ApplyCategory(ref itemData, def.Keep", StringComparison.Ordinal);
        row = update.IndexOf("gameData.ItemHashLookupMap.TryAdd(target, itemData)", StringComparison.Ordinal);
        Assert.True(category > 0 && row > category, "UpdateExisting must write the keep category before the map row");
        SourcePin.AssertStatement(update, "ApplyKeepToClone(world, em, def, existing, donorEntity, sb);");
    }

    [Fact]
    public void Existing_items_are_rearmed_at_registration_and_on_connect()
    {
        string insert = SourcePin.MethodBody(Content("PrefabApplicator.cs"), "private static bool InsertNew(");
        SourcePin.AssertStatement(insert, "RearmKeptItemsLogged(world, def);");
        string rearm = SourcePin.MethodBody(Content("PrefabApplicator.cs"), "internal static int RearmKeptItems(");
        SourcePin.AssertUncommented(rearm, "!Plugin.IsServerProcess");
        // The clone and its lookup row are re-checked before the optional instance scan.
        SourcePin.AssertStatementExactly(rearm, "int written = RecheckKeptClones(world, em, policies, ids);");
        SourcePin.AssertStatementExactly(rearm, "if (!scanInstances) return written;");

        string recheck = SourcePin.MethodBody(Content("PrefabApplicator.cs"), "private static int RecheckKeptClones(");
        SourcePin.AssertUncommented(recheck, "gameData.ItemHashLookupMap.TryAdd(target, row);");
        SourcePin.AssertUncommented(recheck, "KeepStripLogged.Add(pair.Key)");

        string connect = SourcePin.MethodBody(Content("PrefabRegistry.cs"), "private static void OnPlayerConnected(");
        SourcePin.AssertStatementExactly(connect, "RearmKeptItemsOnConnect(ev.World, snapshot, applied);");
        string onConnect = SourcePin.MethodBody(Content("PrefabRegistry.cs"), "private static void RearmKeptItemsOnConnect(");
        SourcePin.AssertStatementExactly(onConnect, "bool scan = epoch != _keepScannedEpoch;");
        SourcePin.AssertStatementExactly(onConnect, "PrefabApplicator.RearmKeptItems(world, kept, scanInstances: scan);");
    }
}
