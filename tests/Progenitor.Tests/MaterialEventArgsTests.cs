using System;
using System.Buffers.Binary;
using System.Linq;
using System.Text.Json;
using Progenitor.Dev.Ops;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// The <c>materials send</c> bridge action's game-free half: argument parsing and the three event
/// payloads at the offsets the game's metadata and interop give.
/// </summary>
public class MaterialEventArgsTests
{
    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement;

    private static readonly byte[] Station = Enumerable.Range(1, 12).Select(i => (byte)i).ToArray();
    private static readonly byte[] Bag = Enumerable.Range(101, 12).Select(i => (byte)i).ToArray();

    [Fact]
    public void Repair_item_needs_a_station_and_a_slot_and_defaults_to_the_bag()
    {
        var r = MaterialEventArgs.Parse(Args("{\"event\":\"repair_item\",\"user\":\"A\",\"station\":\"12:3\",\"slot\":4}"));
        Assert.Equal(MaterialEventArgs.Kind.RepairItem, r.Kind);
        Assert.Equal("12:3", r.Station);
        Assert.Equal("self", r.Inventory);
        Assert.Equal(4, r.Slot);
        Assert.Equal("target", MaterialEventArgs.Parse(Args("{\"event\":\"repair_item\",\"station\":\"target\",\"inventory\":\"7:1\",\"slot\":0}")).Station);
        Assert.Throws<ArgumentException>(() => MaterialEventArgs.Parse(Args("{\"event\":\"repair_item\",\"slot\":4}")));
        Assert.Throws<ArgumentException>(() => MaterialEventArgs.Parse(Args("{\"event\":\"repair_item\",\"station\":\"12:3\"}")));
        Assert.Throws<ArgumentException>(() => MaterialEventArgs.Parse(Args("{\"event\":\"repair_item\",\"station\":\"12:3\",\"slot\":-1}")));
        Assert.Throws<ArgumentException>(() => MaterialEventArgs.Parse(Args("{\"event\":\"repair_item\",\"station\":\"twelve\",\"slot\":1}")));
    }

    [Fact]
    public void Repair_equipped_takes_a_name_or_an_equipment_type()
    {
        Assert.Equal(1, MaterialEventArgs.Parse(Args("{\"event\":\"repair_equipped\",\"equipment\":\"Chest\"}")).EquipmentType);
        Assert.Equal(3, MaterialEventArgs.Parse(Args("{\"event\":\"repair_equipped\",\"equipment\":\"grimoire\"}")).EquipmentType);
        Assert.Equal(5, MaterialEventArgs.Parse(Args("{\"event\":\"repair_equipped\",\"equipment\":5}")).EquipmentType);
        Assert.Throws<ArgumentException>(() => MaterialEventArgs.Parse(Args("{\"event\":\"repair_equipped\",\"equipment\":6}")));
        Assert.Throws<ArgumentException>(() => MaterialEventArgs.Parse(Args("{\"event\":\"repair_equipped\",\"equipment\":\"hat\"}")));
        Assert.Throws<ArgumentException>(() => MaterialEventArgs.Parse(Args("{\"event\":\"repair_equipped\"}")));
    }

    [Fact]
    public void Heart_upgrade_defaults_to_the_open_window()
    {
        var r = MaterialEventArgs.Parse(Args("{\"event\":\"heart_upgrade\",\"user\":\"A\"}"));
        Assert.Equal(MaterialEventArgs.Kind.HeartUpgrade, r.Kind);
        Assert.Equal("target", r.Heart);
        Assert.Equal("40:2", MaterialEventArgs.Parse(Args("{\"event\":\"heart_upgrade\",\"heart\":\" 40:2 \"}")).Heart);
        Assert.Throws<ArgumentException>(() => MaterialEventArgs.Parse(Args("{\"event\":\"claim\"}")));
        Assert.Throws<ArgumentException>(() => MaterialEventArgs.Parse(Args("{}")));
    }

    [Fact]
    public void Repair_item_payload_carries_both_network_ids_and_the_slot()
    {
        var raw = new byte[MaterialEventArgs.RepairItemBytes];
        MaterialEventArgs.WriteRepairItem(raw, Station, Bag, 9);
        Assert.Equal(Station, raw.AsSpan(0x0, 12).ToArray());
        Assert.Equal(Bag, raw.AsSpan(0xC, 12).ToArray());
        Assert.Equal(9, BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(0x18)));
        Assert.Equal(28, MaterialEventArgs.RepairItemBytes);
        Assert.Throws<ArgumentException>(() => MaterialEventArgs.WriteRepairItem(new byte[20], Station, Bag, 1));
        Assert.Throws<ArgumentException>(() => MaterialEventArgs.WriteRepairItem(raw, Station.AsSpan(0, 8), Bag, 1));
    }

    [Fact]
    public void Repair_equipped_payload_is_the_equipment_type()
    {
        var raw = new byte[4];
        MaterialEventArgs.WriteRepairEquipped(raw, 9);
        Assert.Equal(9, BinaryPrimitives.ReadInt32LittleEndian(raw));
    }

    [Fact]
    public void Heart_upgrade_payload_is_the_heart_and_event_type_upgrade()
    {
        var raw = Enumerable.Repeat((byte)0xEE, MaterialEventArgs.HeartEventBytes).ToArray();
        MaterialEventArgs.WriteHeartUpgrade(raw, Station);
        Assert.Equal(Station, raw.AsSpan(0, 12).ToArray());
        Assert.Equal(0, raw[0xC]); // CastleHeartInteractEventType.Upgrade
        Assert.Equal(13, MaterialEventArgs.HeartEventBytes);
    }

    [Fact]
    public void Character_craft_needs_a_nonzero_recipe()
    {
        var r = MaterialEventArgs.Parse(Args("{\"event\":\"character_craft\",\"user\":\"A\",\"recipe\":-1234567}"));
        Assert.Equal(MaterialEventArgs.Kind.CharacterCraft, r.Kind);
        Assert.Equal(-1234567, r.Recipe);
        Assert.Throws<ArgumentException>(() => MaterialEventArgs.Parse(Args("{\"event\":\"character_craft\"}")));
        Assert.Throws<ArgumentException>(() => MaterialEventArgs.Parse(Args("{\"event\":\"character_craft\",\"recipe\":0}")));
        Assert.Throws<ArgumentException>(() => MaterialEventArgs.Parse(Args("{\"event\":\"character_craft\",\"recipe\":\"x\"}")));
    }

    [Fact]
    public void Character_craft_payload_is_the_recipe_guid()
    {
        var raw = Enumerable.Repeat((byte)0xEE, MaterialEventArgs.CharacterCraftBytes).ToArray();
        MaterialEventArgs.WriteCharacterCraft(raw, -1234567);
        Assert.Equal(-1234567, BinaryPrimitives.ReadInt32LittleEndian(raw));
        Assert.Equal(4, MaterialEventArgs.CharacterCraftBytes);
        Assert.Throws<ArgumentException>(() => MaterialEventArgs.WriteCharacterCraft(new byte[3], 1));
    }
}
