using System;
using System.Globalization;

#if !PROGENITOR_TESTS
using Progenitor.Commands;
using Progenitor.Ecs;
using Progenitor.Items;
using Unity.Entities;
#endif

namespace Progenitor.Sample;

/// <summary>
/// <c>.items grant &lt;prefabNameOrGuid&gt; [amount]</c> and <c>.items equip &lt;slot&gt;</c> —
/// <c>ctx.Items</c> (<c>docs/api.md</c> Items). Grant adds through
/// <c>ServerGameManager.TryAddInventoryItem</c>; equip wears the stack already in that bag slot.
/// Client calls throw, so this command is the server half only.
///
/// <para>The Necro army sample also calls <c>Grant</c> once while dressing the arena. This command
/// is the author-facing example: both methods, the result fields, and a name or a GUID hash.</para>
///
/// <para>Parsing and every reply line are game-free so the test project compiles this file.
/// Source-checked and game-free tested; not live-verified.</para>
/// </summary>
internal static class SampleItems
{
    internal const string Area = "items";

    internal const string UsageReply =
        "Usage: .items grant <prefabNameOrGuid> [amount] | .items equip <slot>  (amount 1-10000, bag slot 0-34)";

    internal const string NoCharacterReply = "No character entity on this user; there is no bag to change.";

    internal const string NoWorldReply = "No world yet; ctx.Items needs the server world.";

    internal enum Kind
    {
        Grant = 1,
        Equip = 2,
    }

    /// <summary>
    /// Parse <c>.items</c>. A grant token is either a signed prefab GUID or a spawnable name.
    /// Amount defaults to 1 and must sit in <see cref="Progenitor.Items.ItemGrantPolicy.MaxAmount"/>.
    /// Equip slots are the player's 35-slot bag, 0 through 34.
    /// </summary>
    internal static bool TryParse(string? verb, string? token, string? extra, out Kind kind, out string nameOrGuid, out int amount, out int slot)
    {
        kind = Kind.Grant;
        nameOrGuid = "";
        amount = 1;
        slot = 0;
        switch ((verb ?? "").Trim().ToLowerInvariant())
        {
            case "grant":
                kind = Kind.Grant;
                nameOrGuid = (token ?? "").Trim();
                if (nameOrGuid.Length == 0) return false;
                if (string.IsNullOrWhiteSpace(extra)) return true;
                return int.TryParse(extra.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out amount)
                    && amount >= 1 && amount <= Progenitor.Items.ItemGrantPolicy.MaxAmount;
            case "equip":
                kind = Kind.Equip;
                if (!string.IsNullOrWhiteSpace(extra)) return false;
                return int.TryParse((token ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out slot)
                    && slot >= 0 && slot <= 34;
            default:
                return false;
        }
    }

    internal static string UnknownPrefabReply(string token) =>
        "No prefab named '" + token + "'. Pass a spawnable name or the signed GUID hash.";

    internal static string GrantReply(bool ok, string action, int requested, int added, int slot, bool equipped, string? reason)
    {
        string head = ok
            ? "Granted. action=" + action + " added=" + added + "/" + requested
            : "Grant refused. action=" + action + " added=" + added + "/" + requested;
        if (slot >= 0) head += " slot=" + slot;
        head += " equipped=" + equipped;
        if (!string.IsNullOrEmpty(reason)) head += " (" + reason + ")";
        return head;
    }

    internal static string EquipReply(bool ok, string action, int slot, bool equipped, string? reason)
    {
        string head = ok
            ? "Equipped bag slot " + slot + ". action=" + action + " equipped=" + equipped
            : "Equip refused for bag slot " + slot + ". action=" + action;
        if (!string.IsNullOrEmpty(reason)) head += " (" + reason + ")";
        return head;
    }

#if !PROGENITOR_TESTS
    internal static void HandleCommand(ICommandContext ctx, Kind kind, string nameOrGuid, int amount, int slot)
    {
        var plugin = Plugin.Active;
        if (plugin == null)
        {
            ctx.Reply("Sample plugin context is not available.");
            return;
        }

        var character = ctx.CharacterEntity;
        if (character == Entity.Null)
        {
            ctx.Reply(NoCharacterReply);
            SampleLog.Info(Area, "refused: no character");
            return;
        }

        if (plugin.World == null)
        {
            ctx.Reply(NoWorldReply);
            return;
        }

        if (kind == Kind.Equip)
        {
            ItemGrantResult result = plugin.Items.Equip(character, slot);
            ctx.Reply(EquipReply(result.Ok, result.Action, slot, result.Equipped, result.Reason));
            SampleLog.Info(Area, "equip slot=" + slot + " ok=" + result.Ok + " action=" + result.Action);
            return;
        }

        if (!TryResolve(plugin.World, nameOrGuid, out int guid, out string? why))
        {
            ctx.Reply(why ?? UnknownPrefabReply(nameOrGuid));
            SampleLog.Info(Area, "grant unresolved " + nameOrGuid);
            return;
        }

        ItemGrantResult grant = plugin.Items.Grant(character, guid, amount);
        ctx.Reply(GrantReply(grant.Ok, grant.Action, grant.Requested, grant.Added, grant.Slot, grant.Equipped, grant.Reason));
        SampleLog.Info(Area, "grant guid=" + guid + " amount=" + amount + " ok=" + grant.Ok
            + " added=" + grant.Added + " slot=" + grant.Slot);
    }

    private static bool TryResolve(Unity.Entities.World world, string token, out int guid, out string? why)
    {
        why = null;
        if (int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out guid) && guid != 0)
            return true;
        if (!PrefabLookup.TryGetGuidByName(world, token, out var resolved) || resolved._Value == 0)
        {
            guid = 0;
            why = UnknownPrefabReply(token);
            return false;
        }

        guid = resolved._Value;
        return true;
    }
#endif
}
