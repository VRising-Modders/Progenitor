using System;
using Progenitor.Items;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Adversarial rules for granting items: bad amounts, a copy that is already there,
/// a bag that cannot take the whole request, and an empty equip slot. No game.
/// </summary>
public class ItemGrantPolicyTests
{
    [Fact]
    public void Non_positive_amount_is_refused_not_clamped()
    {
        Assert.Equal(ItemGrantStep.RefuseAmount, ItemGrantPolicy.Before(0, ItemPresence.None, null));
        Assert.Equal(ItemGrantStep.RefuseAmount, ItemGrantPolicy.Before(-3, ItemPresence.None, null));
        Assert.Equal(ItemGrantStep.RefuseAmount, ItemGrantPolicy.Before(10_001, ItemPresence.None, null));
    }

    [Fact]
    public void Stack_adds_even_when_a_copy_is_already_worn()
    {
        var worn = new ItemPresence(true, 4, true);
        Assert.Equal(ItemGrantStep.Add, ItemGrantPolicy.Before(1, worn, new ItemGrantOptions()));
    }

    [Fact]
    public void Refuse_does_not_add_when_the_bag_or_the_body_already_has_it()
    {
        var options = new ItemGrantOptions { WhenPresent = WhenAlreadyPresent.Refuse };
        Assert.Equal(ItemGrantStep.RefusePresent, ItemGrantPolicy.Before(1, new ItemPresence(true, 2, false), options));
        Assert.Equal(ItemGrantStep.RefusePresent, ItemGrantPolicy.Before(1, new ItemPresence(false, -1, true), options));
    }

    [Fact]
    public void EquipExisting_wears_the_bag_copy_and_does_not_add_when_already_worn()
    {
        var options = new ItemGrantOptions { WhenPresent = WhenAlreadyPresent.EquipExisting };
        Assert.Equal(ItemGrantStep.EquipExisting, ItemGrantPolicy.Before(1, new ItemPresence(true, 6, false), options));
        Assert.Equal(ItemGrantStep.AlreadyEquipped, ItemGrantPolicy.Before(1, new ItemPresence(false, -1, true), options));
    }

    [Fact]
    public void Absent_item_is_an_add_for_every_presence_rule()
    {
        Assert.Equal(ItemGrantStep.Add, ItemGrantPolicy.Before(1, ItemPresence.None, new ItemGrantOptions { WhenPresent = WhenAlreadyPresent.Refuse }));
        Assert.Equal(ItemGrantStep.Add, ItemGrantPolicy.Before(1, ItemPresence.None, new ItemGrantOptions { WhenPresent = WhenAlreadyPresent.EquipExisting }));
    }

    [Theory]
    [InlineData(WhenInventoryFull.AllowPartial, 10, 3, true)]
    [InlineData(WhenInventoryFull.RequireComplete, 10, 3, false)]
    [InlineData(WhenInventoryFull.AllowPartial, 10, 0, false)]
    [InlineData(WhenInventoryFull.RequireComplete, 10, 0, false)]
    [InlineData(WhenInventoryFull.RequireComplete, 10, 10, true)]
    public void Full_inventory_accepts_a_short_stack_only_when_asked(WhenInventoryFull whenFull, int requested, int added, bool ok)
    {
        Assert.Equal(ok, ItemGrantPolicy.AcceptAdd(whenFull, requested, added));
    }

    [Fact]
    public void Empty_slot_cannot_be_equipped()
    {
        Assert.False(ItemGrantPolicy.SlotCanEquip(false, 1));
        Assert.False(ItemGrantPolicy.SlotCanEquip(true, 0));
        Assert.True(ItemGrantPolicy.SlotCanEquip(true, 1));
    }

    [Fact]
    public void Unknown_option_words_are_refused()
    {
        Assert.Throws<ArgumentException>(() => ItemGrantPolicy.ParseWhenFull("drop"));
        Assert.Throws<ArgumentException>(() => ItemGrantPolicy.ParseWhenPresent("replace"));
        Assert.Throws<ArgumentException>(() => ItemGrantPolicy.ParseDurability("infinite"));
        Assert.Equal(WhenInventoryFull.RequireComplete, ItemGrantPolicy.ParseWhenFull("requireComplete"));
        Assert.Equal(WhenAlreadyPresent.EquipExisting, ItemGrantPolicy.ParseWhenPresent("equipExisting"));
        Assert.Equal(ItemDurability.Indestructible, ItemGrantPolicy.ParseDurability("none"));
    }
}
