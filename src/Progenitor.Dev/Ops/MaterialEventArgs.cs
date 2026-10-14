using System;
using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json;

namespace Progenitor.Dev.Ops;

/// <summary>
/// Game-free half of the <c>materials</c> op's <c>send</c> action (server): the three client-to-server
/// events a player's client sends to repair an item at a station, repair a worn item and upgrade a castle
/// heart, queued on the server as an event entity with the player's <c>FromCharacter</c> (the pattern
/// <c>bot_repair</c> and <c>bot_use</c> use). The live send stays in <c>MaterialEventOps</c> so this
/// file compiles in the test project without interop.
///
/// <para>Payload layouts, from the game's metadata and the interop wrappers (the live op checks the
/// interop offsets with <c>Marshal.OffsetOf</c> before writing and refuses on a mismatch):</para>
/// <list type="bullet">
/// <item><c>RepairItemEvent</c>: <c>NetworkId Workstation</c> @0x0, <c>NetworkId Inventory</c> @0xC,
/// <c>int Slot</c> @0x18 (28 bytes).</item>
/// <item><c>RepairEquippedItemEvent</c>: <c>EquipmentType</c> (int) @0x0.</item>
/// <item><c>CastleHeartInteractEvent</c>: <c>NetworkId CastleHeart</c> @0x0,
/// <c>CastleHeartInteractEventType EventType</c> (byte) @0xC; Upgrade = 0.</item>
/// </list>
/// A <c>NetworkId</c> is 12 bytes, copied raw from the entity's own <c>NetworkId</c> component.
/// </summary>
internal static class MaterialEventArgs
{
    internal const int NetworkIdSize = 12;

    internal const int RepairWorkstationOffset = 0x0;
    internal const int RepairInventoryOffset = 0xC;
    internal const int RepairSlotOffset = 0x18;
    internal const int RepairItemBytes = 0x1C;

    internal const int EquipmentTypeOffset = 0x0;

    internal const int HeartOffset = 0x0;
    internal const int HeartEventTypeOffset = 0xC;
    internal const int HeartEventBytes = 0xD;

    /// <summary><c>CastleHeartInteractEventType.Upgrade</c>.</summary>
    internal const byte HeartUpgrade = 0;

    /// <summary>The three events.</summary>
    internal enum Kind
    {
        RepairItem,
        RepairEquipped,
        HeartUpgrade,
    }

    /// <summary>One parsed request. Entities are "index:version" of server entities, or "self" / "target".</summary>
    internal sealed record Request(Kind Kind, string? Station, string Inventory, int Slot, int EquipmentType, string Heart);

    /// <summary>The <c>event</c> names the op accepts.</summary>
    internal const string Kinds = "repair_item, repair_equipped, heart_upgrade";

    /// <summary>
    /// Parse <c>{"action":"send","event":…}</c>. <c>repair_item</c>: <c>station</c> (required),
    /// <c>inventory</c> (default <c>self</c>: the character's own bag), <c>slot</c> (required, main
    /// inventory index). <c>repair_equipped</c>: <c>equipment</c> (name or <c>EquipmentType</c> int).
    /// <c>heart_upgrade</c>: <c>heart</c> (default <c>target</c>: the heart whose window the character has
    /// open). Every kind also takes <c>user</c> or <c>platformId</c> (resolved by the op).
    /// </summary>
    internal static Request Parse(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object) throw new ArgumentException("args must be an object");
        string kind = (Str(args, "event") ?? throw new ArgumentException("args.event is required (" + Kinds + ")")).Trim().ToLowerInvariant();
        switch (kind)
        {
            case "repair_item":
            {
                string station = Entity(Str(args, "station") ?? throw new ArgumentException("args.station (index:version of the workstation, its window open) is required"), "station", allowTarget: true);
                string inventory = Entity(Str(args, "inventory") ?? "self", "inventory", allowTarget: true);
                int slot = Int(args, "slot") ?? throw new ArgumentException("args.slot (main inventory index of the item) is required");
                if (slot < 0) throw new ArgumentException("args.slot must be >= 0");
                return new Request(Kind.RepairItem, station, inventory, slot, -1, "");
            }
            case "repair_equipped":
            {
                if (!args.TryGetProperty("equipment", out var e)) throw new ArgumentException("args.equipment is required (chest, weapon, magicsource, legs, … or the EquipmentType int)");
                int type = e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out int n) ? n
                    : e.ValueKind == JsonValueKind.String ? EquipmentType(e.GetString() ?? "")
                    : throw new ArgumentException("args.equipment must be a name or an int");
                if (!ValidEquipmentType(type)) throw new ArgumentException("args.equipment " + type + " is not an EquipmentType");
                return new Request(Kind.RepairEquipped, null, "self", -1, type, "");
            }
            case "heart_upgrade":
                return new Request(Kind.HeartUpgrade, null, "self", -1, -1, Entity(Str(args, "heart") ?? "target", "heart", allowTarget: true));
            default:
                throw new ArgumentException("unknown event '" + kind + "' (" + Kinds + ")");
        }
    }

    /// <summary><c>ProjectM.EquipmentType</c> by name (the enum's own names, case-insensitive, plus common aliases).</summary>
    internal static int EquipmentType(string name) => name.Trim().ToLowerInvariant() switch
    {
        "headgear" or "head" => 0,
        "chest" => 1,
        "weapon" => 2,
        "magicsource" or "grimoire" or "magic" => 3,
        "footgear" or "boots" or "feet" => 4,
        "legs" => 5,
        "cloak" => 8,
        "gloves" => 9,
        "bag" => 10,
        _ => throw new ArgumentException("unknown equipment '" + name + "' (headgear, chest, weapon, magicsource, footgear, legs, cloak, gloves, bag)"),
    };

    internal static bool ValidEquipmentType(int type) => type is 0 or 1 or 2 or 3 or 4 or 5 or 8 or 9 or 10;

    /// <summary>The <c>RepairItemEvent</c> payload: both network ids raw, then the slot.</summary>
    internal static void WriteRepairItem(Span<byte> raw, ReadOnlySpan<byte> workstationNet, ReadOnlySpan<byte> inventoryNet, int slot)
    {
        Require(raw, RepairItemBytes, workstationNet, inventoryNet);
        workstationNet.CopyTo(raw.Slice(RepairWorkstationOffset, NetworkIdSize));
        inventoryNet.CopyTo(raw.Slice(RepairInventoryOffset, NetworkIdSize));
        BinaryPrimitives.WriteInt32LittleEndian(raw.Slice(RepairSlotOffset), slot);
    }

    /// <summary>The <c>RepairEquippedItemEvent</c> payload.</summary>
    internal static void WriteRepairEquipped(Span<byte> raw, int equipmentType)
    {
        if (raw.Length < 4) throw new ArgumentException("payload buffer too small");
        BinaryPrimitives.WriteInt32LittleEndian(raw.Slice(EquipmentTypeOffset), equipmentType);
    }

    /// <summary>The <c>CastleHeartInteractEvent</c> payload with <c>EventType</c> Upgrade.</summary>
    internal static void WriteHeartUpgrade(Span<byte> raw, ReadOnlySpan<byte> heartNet)
    {
        Require(raw, HeartEventBytes, heartNet, heartNet);
        heartNet.CopyTo(raw.Slice(HeartOffset, NetworkIdSize));
        raw[HeartEventTypeOffset] = HeartUpgrade;
    }

    private static void Require(Span<byte> raw, int bytes, ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        if (raw.Length < bytes) throw new ArgumentException("payload buffer too small");
        if (a.Length != NetworkIdSize || b.Length != NetworkIdSize) throw new ArgumentException("a NetworkId is " + NetworkIdSize + " bytes");
    }

    private static string Entity(string text, string name, bool allowTarget)
    {
        var t = text.Trim();
        if (t.Equals("self", StringComparison.OrdinalIgnoreCase)) return "self";
        if (allowTarget && t.Equals("target", StringComparison.OrdinalIgnoreCase)) return "target";
        var parts = t.Split(':');
        if (parts.Length != 2 || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int i)
            || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) || i < 0 || v < 0)
            throw new ArgumentException("args." + name + " must be index:version, self or target");
        return i.ToString(CultureInfo.InvariantCulture) + ":" + v.ToString(CultureInfo.InvariantCulture);
    }

    private static string? Str(JsonElement a, string k) =>
        a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Int(JsonElement a, string k)
    {
        if (!a.TryGetProperty(k, out var v)) return null;
        if (v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out int n)) throw new ArgumentException("args." + k + " must be an integer");
        return n;
    }
}
