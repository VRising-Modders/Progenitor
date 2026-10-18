using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Progenitor.Inventory;

#if !PROGENITOR_TESTS
using Progenitor.Castles;
using Progenitor.Commands;
using Progenitor.Ecs;
using Progenitor.Plugins;
using Unity.Entities;
#endif

namespace Progenitor.Sample;

/// <summary>
/// The <c>.items</c> verbs for <c>ctx.Inventory</c>, <c>ctx.Clans</c> and the castle container
/// reads (milestone P-a). Server half only; admin only (it moves items).
///
/// <list type="bullet">
/// <item><description><c>.items chests</c>: every container and station of the castle you stand
/// on (else your own), numbered <c>#1..</c>, with stable id, nameplate and slots.</description></item>
/// <item><description><c>.items slots &lt;c&gt;</c>, <c>.items count &lt;c&gt; &lt;item&gt;</c>,
/// <c>.items free &lt;c&gt; &lt;item&gt;</c>.</description></item>
/// <item><description><c>.items move &lt;from&gt; &lt;to&gt; &lt;item&gt; &lt;amount&gt;</c>: an
/// transfer that conserves the total of interchangeable items and stops on anything else; replies with the counts before and after in both containers.</description></item>
/// <item><description><c>.items moveslot &lt;from&gt; &lt;slot&gt; &lt;to&gt; &lt;amount&gt;</c>: the same
/// transfer, from one slot (<c>ctx.Inventory.TransferSlot</c>).</description></item>
/// <item><description><c>.items moveall &lt;from&gt; &lt;to&gt; [keepbar]</c> (<c>keepbar</c> leaves
/// bag slots 0–7, the action bar, in place), <c>.items add|remove &lt;c&gt;
/// &lt;item&gt; &lt;amount&gt;</c>, <c>.items name &lt;c&gt; "&lt;text&gt;"</c>, <c>.items id
/// &lt;c&gt;</c>, <c>.items watch &lt;c&gt;</c>, <c>.items stamp &lt;c&gt;</c> (<c>ctx.Inventory.ChangeStamp</c> and <c>AnyChangedSince</c> since the last <c>.items stamp</c> of that container), <c>.items moves</c> (toggle a log of players' container moves, <c>ctx.Inventory.OnPlayerMove</c>), <c>.items heart</c>, <c>.items clan</c>.</description></item>
/// <item><description><c>.items lock &lt;c&gt; &lt;password&gt;</c>, <c>.items unlock &lt;c&gt;</c>, <c>.items locks</c>:
/// a password lock through the container open gate (<see cref="SampleLocks"/>).</description></item>
/// </list>
/// A container token is <c>#n</c> (from <c>.items chests</c>), a stable id <c>c-…</c>, an entity
/// <c>index:version</c>, <c>bag</c> (your character) or <c>heart</c> (the castle's heart); append
/// <c>/out</c> for a refinement station's output. Items are a spawnable name or a GUID hash.
/// Parsing and replies are game-free so the test project compiles this file.
/// </summary>
internal static class SampleInventory
{
    internal const string Area = "items";

    internal const string UsageReply =
        "Usage: .items chests | slots <c> | count <c> <item> | free <c> <item> | move <from> <to> <item> <amount> | moveslot <from> <slot> <to> <amount> | moveall <from> <to> [keepbar] | add <c> <item> <n> | remove <c> <item> <n> | name <c> \"text\" | id <c> | net <c> | resolve <index> <generation> [type] | watch <c> | stamp <c> | moves | lock <c> <password> | unlock <c> | locks | heart | clan   (c = #n, c-<id>, index:version, bag, heart; /out = station output)";

    /// <summary>The verbs this file handles (the grant/equip verbs stay in <see cref="SampleItems"/>).</summary>
    internal static bool Handles(string? verb) => (verb ?? "").Trim().ToLowerInvariant() switch
    {
        "chests" or "slots" or "count" or "free" or "move" or "moveslot" or "moveall" or "add" or "remove"
            or "name" or "id" or "net" or "resolve" or "watch" or "stamp" or "moves" or "heart" or "clan"
            or "lock" or "unlock" or "locks" => true,
        _ => false,
    };

    /// <summary>
    /// Parse the <c>.items resolve</c> words as plain integers (type defaults to 1, normal). The
    /// range checks are <c>ctx.Inventory.TryResolveNetworkId</c>'s: it treats them as untrusted.
    /// </summary>
    internal static bool TryParseNetWords(string? index, string? generation, string? type, out int i, out int g, out int t)
    {
        var ci = CultureInfo.InvariantCulture;
        g = t = 0;
        if (!int.TryParse((index ?? "").Trim(), NumberStyles.Integer, ci, out i)) return false;
        if (!int.TryParse((generation ?? "").Trim(), NumberStyles.Integer, ci, out g)) return false;
        if (string.IsNullOrWhiteSpace(type)) { t = 1; return true; }
        return int.TryParse(type.Trim(), NumberStyles.Integer, ci, out t);
    }

    /// <summary>How a container token names its container.</summary>
    internal enum TokenKind
    {
        /// <summary><c>#n</c>: row n of <c>.items chests</c>.</summary>
        Row,

        /// <summary><c>c-…</c>: a stable container id.</summary>
        Id,

        /// <summary><c>index:version</c>.</summary>
        Entity,

        /// <summary><c>bag</c>: the caller's character.</summary>
        Bag,

        /// <summary><c>heart</c>: the castle's heart.</summary>
        Heart,
    }

    /// <summary>A parsed container token.</summary>
    internal readonly struct ContainerToken
    {
        internal ContainerToken(TokenKind kind, int row, ContainerId id, int index, int version, bool output)
        {
            Kind = kind;
            Row = row;
            Id = id;
            Index = index;
            Version = version;
            Output = output;
        }

        internal TokenKind Kind { get; }
        internal int Row { get; }
        internal ContainerId Id { get; }
        internal int Index { get; }
        internal int Version { get; }
        internal bool Output { get; }
    }

    /// <summary>Parse a container token. <c>/out</c> selects instance 1.</summary>
    internal static bool TryParseContainer(string? text, out ContainerToken token)
    {
        token = default;
        string s = (text ?? "").Trim();
        if (s.Length == 0) return false;
        bool output = false;
        if (s.EndsWith("/out", StringComparison.OrdinalIgnoreCase))
        {
            output = true;
            s = s.Substring(0, s.Length - 4);
        }

        var ci = CultureInfo.InvariantCulture;
        if (s.Equals("bag", StringComparison.OrdinalIgnoreCase))
        {
            token = new ContainerToken(TokenKind.Bag, 0, default, 0, 0, output);
            return !output;
        }

        if (s.Equals("heart", StringComparison.OrdinalIgnoreCase))
        {
            token = new ContainerToken(TokenKind.Heart, 0, default, 0, 0, output);
            return !output;
        }

        if (s.StartsWith("#", StringComparison.Ordinal))
        {
            if (!int.TryParse(s.AsSpan(1), NumberStyles.None, ci, out int row) || row < 1) return false;
            token = new ContainerToken(TokenKind.Row, row, default, 0, 0, output);
            return true;
        }

        if (s.StartsWith("c-", StringComparison.OrdinalIgnoreCase))
        {
            if (!ContainerId.TryParse(s, out var id)) return false;
            token = new ContainerToken(TokenKind.Id, 0, id, 0, 0, output);
            return true;
        }

        int colon = s.IndexOf(':');
        if (colon > 0
            && int.TryParse(s.AsSpan(0, colon), NumberStyles.None, ci, out int index)
            && int.TryParse(s.AsSpan(colon + 1), NumberStyles.None, ci, out int version)
            && index > 0)
        {
            token = new ContainerToken(TokenKind.Entity, 0, default, index, version, output);
            return true;
        }

        return false;
    }

    /// <summary>Parse an amount from 1 to the engine's cap.</summary>
    internal static bool TryParseAmount(string? text, out int amount) =>
        int.TryParse((text ?? "").Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out amount)
        && amount >= 1 && amount <= 1_000_000;

    /// <summary>A slot index for <c>.items moveslot</c>: 0 or more (the engine checks the upper bound).</summary>
    internal static bool TryParseSlot(string? text, out int slot) =>
        int.TryParse((text ?? "").Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out slot) && slot >= 0;

    /// <summary>
    /// <c>.items moveall &lt;from&gt; &lt;to&gt; keepbar</c>: leave a player's action bar (bag slots
    /// 0–7) in place, the quick-stash rule.
    /// </summary>
    internal static InventorySlotFilter MoveAllSlots(string? option) =>
        string.Equals((option ?? "").Trim(), "keepbar", StringComparison.OrdinalIgnoreCase)
            ? InventorySlotFilter.SkipActionBar
            : InventorySlotFilter.All;

    /// <summary>The <c>.items moveslot</c> reply: the move reply with the slot named.</summary>
    internal static string MoveSlotReply(string from, int slot, string to, string item, InventoryResult r) =>
        MoveReply(from + " slot " + slot.ToString(CultureInfo.InvariantCulture), to, item, r);

    /// <summary>The <c>.items move</c> reply: outcome and the counts before and after per container.</summary>
    internal static string MoveReply(string from, string to, string item, InventoryResult r) =>
        (r.Ok ? "Moved " : "Move failed: ") + r.Done + "/" + r.Requested + " " + item
        + ". " + from + ": " + r.SourceBefore + " -> " + r.SourceAfter
        + ", " + to + ": " + r.TargetBefore + " -> " + r.TargetAfter
        + " (" + r.Outcome + (r.Reason.Length > 0 ? "; " + r.Reason : "") + ")";

    /// <summary>The log line the live acceptance greps for.</summary>
    internal static string MoveLog(string from, string to, int item, InventoryResult r) =>
        "move from=" + from + " to=" + to + " item=" + item.ToString(CultureInfo.InvariantCulture)
        + " requested=" + r.Requested + " moved=" + r.Done + " outcome=" + r.Outcome
        + " from_before=" + r.SourceBefore + " from_after=" + r.SourceAfter
        + " to_before=" + r.TargetBefore + " to_after=" + r.TargetAfter
        + " total_before=" + (r.SourceBefore + r.TargetBefore) + " total_after=" + (r.SourceAfter + r.TargetAfter)
        + " steps=" + r.Steps + " compensated=" + r.Compensated;

    /// <summary>
    /// The <c>.items stamp</c> reply: the stamp and clock now, and against the previous
    /// <c>.items stamp</c> of the same container (<paramref name="previousStamp"/> 0 = none) whether
    /// the stamp is equal and what <c>AnyChangedSince(previous clock)</c> said.
    /// </summary>
    internal static string StampReply(string label, long stamp, long clock, long previousStamp, long previousClock, bool changedSince)
    {
        var ci = CultureInfo.InvariantCulture;
        string head = label + " stamp=0x" + stamp.ToString("X16", ci) + " clock=" + clock.ToString(ci);
        if (previousStamp == 0) return head + " | first stamp of this container";
        return head + " | since clock " + previousClock.ToString(ci) + ": stamp "
            + (stamp == previousStamp ? "equal (slots unchanged)" : "differs (slots may have changed)")
            + ", AnyChangedSince=" + (changedSince ? "true" : "false");
    }

    /// <summary>Non-empty slots as <c>#slot name xN</c>, capped, plus the free-slot count.</summary>
    internal static string SlotsReply(string label, IReadOnlyList<InventorySlot> slots, Func<int, string> name, int max = 12)
    {
        var sb = new StringBuilder();
        sb.Append(label).Append(": ").Append(slots.Count).Append(" slots");
        int free = 0, shown = 0, used = 0;
        for (int i = 0; i < slots.Count; i++)
        {
            if (slots[i].IsEmpty) { free++; continue; }
            used++;
            if (shown >= max) continue;
            sb.Append(shown == 0 ? " | " : ", ").Append('#').Append(slots[i].Slot).Append(' ')
              .Append(name(slots[i].Item)).Append(" x").Append(slots[i].Amount);
            shown++;
        }

        if (used > shown) sb.Append(", +").Append(used - shown).Append(" more");
        sb.Append(" | free ").Append(free);
        return sb.ToString();
    }

#if !PROGENITOR_TESTS
    private static readonly Dictionary<string, IInventoryWatch> Watches = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, (long Stamp, long Clock)> LastStamps = new(StringComparer.Ordinal);
    private static IDisposable? _moves;

    /// <summary>Log the P-a events so the live acceptance can see them.</summary>
    internal static void Attach(PluginContext ctx)
    {
        if (!ctx.IsServer) return;
        SampleLocks.Attach(ctx);
        ctx.Events.On<PlayerReady>(e => SampleLog.Info(Area, "player ready user=" + e.User + " character=" + e.Character));
        ctx.Events.On<PlayerTerritoryChanged>(e => SampleLog.Info(Area, "territory user=" + e.User + " " + e.OldTerritory
            + " -> " + e.NewTerritory + (e.Initial ? " (initial)" : "") + (e.Castle.Enabled ? " castle=" + e.Castle.Heart : "")));
    }

    internal static void Detach()
    {
        SampleLocks.Detach();
        foreach (var w in Watches.Values) w.Dispose();
        Watches.Clear();
        LastStamps.Clear();
        _moves?.Dispose();
        _moves = null;
    }

    internal static void Handle(ICommandContext ctx, string verb, string a, string b, string c, string d)
    {
        var plugin = Plugin.Active;
        if (plugin == null) { ctx.Reply("Sample plugin context is not available."); return; }
        var inv = plugin.Inventory;
        var world = ctx.World;
        switch (verb.Trim().ToLowerInvariant())
        {
            case "chests":
                Chests(ctx, plugin);
                return;
            case "heart":
            {
                var castle = CastleHere(ctx, plugin);
                if (!castle.Enabled) { ctx.Reply("No castle here or of yours."); return; }
                var fuel = castle.HeartFuel();
                ctx.Reply("Heart " + castle.Heart + " id=" + castle.HeartId + " name=\"" + castle.Name + "\" " + fuel);
                SampleLog.Info(Area, "heart " + castle.Heart + " id=" + castle.HeartId + " " + fuel);
                return;
            }
            case "clan":
                Clan(ctx, plugin);
                return;
            case "moves":
            {
                // ctx.Inventory.OnPlayerMove: every change a player makes by hand to a container, as a move log.
                if (_moves != null)
                {
                    _moves.Dispose();
                    _moves = null;
                    ctx.Reply("Stopped the move log.");
                    return;
                }

                _moves = inv.OnPlayerMove(m => SampleLog.Info(Area, "player move " + m.Direction + " " + ItemName(world, m.Item) + " x" + m.Amount
                    + " " + (m.IsCastleContainer ? m.Container.ToString() : m.Inventory.ToString()) + " by user " + m.User
                    + " (" + m.Kind + ", " + m.CountBefore + " -> " + m.CountAfter + ", other " + m.Counterpart + (m.Ambiguous ? ", ambiguous" : "") + ")"));
                ctx.Reply("Logging players' container moves to the server log (Sample items: player move …). Run again to stop.");
                return;
            }
            case "locks":
            {
                var line = "locks: " + SampleLocks.Count + (SampleLocks.Count > 0 ? " (" + string.Join(", ", SampleLocks.Locked) + ")" : "");
                ctx.Reply(line);
                SampleLog.Info(Area, line);
                return;
            }
            case "resolve":
            {
                // What a request handler does with the words a client sent (VanillaPanelTarget or UiWorldPickResult).
                if (!TryParseNetWords(a, b, c, out int ni, out int ng, out int nt)) { ctx.Reply(UsageReply); return; }
                bool ok = inv.TryResolveNetworkId(ni, ng, nt, out var rid, out var rowner, out string rwhy);
                string line = "resolve net=" + ni + ":" + ng + " type=" + nt + (ok ? " -> " + rowner + " id=" + rid : " refused: " + rwhy);
                ctx.Reply(line);
                SampleLog.Info(Area, line);
                return;
            }
        }

        if (!TryResolve(ctx, plugin, a, out var first, out string firstLabel, out string why)) { ctx.Reply(why); return; }

        switch (verb.Trim().ToLowerInvariant())
        {
            case "slots":
            {
                var snap = inv.Read(first);
                if (!snap.Ok) { ctx.Reply("Unreadable: " + snap.Reason); return; }
                ctx.Reply(SlotsReply(firstLabel, snap.Slots, item => ItemName(world, item)) + " | accepts " + snap.Restriction);
                return;
            }
            case "count":
            case "free":
            {
                if (!TryItem(world, b, out int item, out why)) { ctx.Reply(why); return; }
                int n = verb == "count" ? inv.Count(first, item) : inv.FreeSpace(first, item);
                ctx.Reply(firstLabel + " " + verb + " " + ItemName(world, item) + " = " + n);
                return;
            }
            case "add":
            case "remove":
            {
                if (!TryItem(world, b, out int item, out why)) { ctx.Reply(why); return; }
                if (!TryParseAmount(c, out int amount)) { ctx.Reply(UsageReply); return; }
                var r = verb == "add" ? inv.TryAdd(first, item, amount) : inv.TryRemove(first, item, amount);
                ctx.Reply(verb + " " + firstLabel + ": " + r);
                SampleLog.Info(Area, verb + " " + firstLabel + " item=" + item + " " + r);
                return;
            }
            case "id":
            {
                var owner = first.Owner;
                var id = inv.IdOf(owner, out string reason);
                ctx.Reply(firstLabel + " id=" + id + " status=" + (id.IsNone ? "none (" + reason + ")" : inv.Status(id).ToString()));
                return;
            }
            case "net":
            {
                // The reverse: the words to send to a client so it can find and Locate its own entity.
                var id = inv.IdOf(first.Owner, out string reason);
                string line = id.IsNone
                    ? "net " + firstLabel + " has no id: " + reason
                    : inv.TryGetNetworkId(id, out int ni, out int ng, out int nt, out reason)
                        ? "net " + firstLabel + " id=" + id + " net=" + ni + ":" + ng + " type=" + nt
                        : "net " + firstLabel + " id=" + id + " has no network id: " + reason;
                ctx.Reply(line);
                SampleLog.Info(Area, line);
                return;
            }
            case "lock":
            case "unlock":
            {
                // The container open gate: a salted hash only; the player's answer goes through the masked prompt.
                var id = inv.IdOf(first.Owner, out string reason);
                if (id.IsNone) { ctx.Reply(firstLabel + " has no id: " + reason); return; }
                if (verb.Trim().ToLowerInvariant() == "unlock")
                {
                    bool had = SampleLocks.Unlock(id);
                    inv.RevokeOpenGrants(id);
                    ctx.Reply(had ? "Unlocked " + firstLabel + " (" + id + ")." : firstLabel + " was not locked.");
                    SampleLog.Info(Area, "unlock " + id + " had=" + had);
                    return;
                }
                if (string.IsNullOrEmpty(b)) { ctx.Reply(UsageReply); return; }
                SampleLocks.Lock(id, b);
                int revoked = inv.RevokeOpenGrants(id);
                ctx.Reply("Locked " + firstLabel + " (" + id + "); " + revoked + " earlier unlock(s) forgotten.");
                // Never the password: only the id.
                SampleLog.Info(Area, "lock " + id + " revoked=" + revoked);
                return;
            }
            case "name":
            {
                bool ok = plugin.Castles.TrySetNameplate(first.Owner, b, out string reason);
                ctx.Reply(ok ? "Renamed " + firstLabel + " to \"" + b + "\"." : "Rename refused: " + reason);
                SampleLog.Info(Area, "name " + firstLabel + " ok=" + ok + (ok ? "" : " reason=" + reason));
                return;
            }
            case "moveslot":
            {
                // .items moveslot <from> <slot> <to> <amount>: whatever that one slot holds.
                if (!TryParseSlot(b, out int slot) || !TryParseAmount(d, out int slotAmount)) { ctx.Reply(UsageReply); return; }
                if (!TryResolve(ctx, plugin, c, out var target, out string targetLabel, out why)) { ctx.Reply(why); return; }
                var moved = inv.TransferSlot(first, slot, target, slotAmount);
                ctx.Reply(MoveSlotReply(firstLabel, slot, targetLabel, moved.Item == 0 ? "(empty)" : ItemName(world, moved.Item), moved));
                SampleLog.Info(Area, "moveslot slot=" + slot + " " + MoveLog(firstLabel, targetLabel, moved.Item, moved));
                return;
            }
            case "stamp":
            {
                // A poller's loop in one command: take the clock, compare with the last look, stamp.
                string key = first.ToString();
                LastStamps.TryGetValue(key, out var last);
                bool changed = last.Stamp != 0 && inv.AnyChangedSince(new[] { first }, last.Clock);
                long clock = inv.Clock;
                long stamp = inv.ChangeStamp(first);
                LastStamps[key] = (stamp, clock);
                string line = StampReply(firstLabel, stamp, clock, last.Stamp, last.Clock, changed);
                ctx.Reply(line);
                SampleLog.Info(Area, "stamp " + line);
                return;
            }
            case "watch":
            {
                string key = first.ToString();
                if (Watches.TryGetValue(key, out var existing) && existing.Active)
                {
                    existing.Dispose();
                    Watches.Remove(key);
                    ctx.Reply("Stopped watching " + firstLabel + ".");
                    return;
                }

                string label = firstLabel;
                Watches[key] = inv.Watch(first, change => SampleLog.Info(Area, "watch " + label + " " + change.Kind));
                ctx.Reply("Watching " + firstLabel + "; changes go to the server log (Sample items: watch …). Run again to stop.");
                return;
            }
        }

        if (!TryResolve(ctx, plugin, b, out var second, out string secondLabel, out why)) { ctx.Reply(why); return; }

        if (verb == "moveall")
        {
            var slots = MoveAllSlots(c);
            var all = inv.TransferAll(first, second, slots);
            ctx.Reply("Move all " + firstLabel + " -> " + secondLabel + " (slots " + slots + "): " + all);
            SampleLog.Info(Area, "moveall from=" + firstLabel + " to=" + secondLabel + " slots=" + slots + " " + all);
            return;
        }

        // move
        if (!TryItem(world, c, out int moveItem, out why)) { ctx.Reply(why); return; }
        if (!TryParseAmount(d, out int moveAmount)) { ctx.Reply(UsageReply); return; }
        var result = inv.Transfer(first, second, moveItem, moveAmount);
        ctx.Reply(MoveReply(firstLabel, secondLabel, ItemName(world, moveItem), result));
        SampleLog.Info(Area, MoveLog(firstLabel, secondLabel, moveItem, result));
    }

    private static void Chests(ICommandContext ctx, PluginContext plugin)
    {
        var castle = CastleHere(ctx, plugin);
        if (!castle.Enabled) { ctx.Reply("No castle here or of yours."); return; }
        var rows = Rows(castle);
        ctx.Reply("Castle " + castle.Territory.Index + " \"" + castle.Name + "\": " + rows.Count + " containers and stations.");
        for (int i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            string line = "#" + (i + 1) + " " + r.Building.Entity + " " + r.Id + (r.Nameplate.Length > 0 ? " \"" + r.Nameplate + "\"" : "")
                + " " + r.Building.Label + " slots=" + r.SlotCount + (r.IsRefinement ? " (in+/out)" : r.IsStation ? " (station)" : "");
            ctx.Reply(line);
            SampleLog.Info(Area, "chest " + line);
        }
    }

    private static IReadOnlyList<CastleContainerInfo> Rows(ICastleHandle castle)
    {
        var all = new List<CastleContainerInfo>(castle.Containers());
        all.AddRange(castle.Stations());
        return all;
    }

    private static ICastleHandle CastleHere(ICommandContext ctx, PluginContext plugin)
    {
        var here = plugin.Castles.At(ctx.CharacterEntity);
        return here.Enabled ? here : plugin.Castles.ForUser(ctx.UserEntity);
    }

    private static void Clan(ICommandContext ctx, PluginContext plugin)
    {
        var clan = plugin.Clans.Of(ctx.UserEntity);
        var castle = plugin.Castles.At(ctx.CharacterEntity);
        string relation = castle.Enabled ? plugin.Castles.RelationTo(ctx.UserEntity, castle).ToString() : "not on a castle plot";
        if (clan.IsNone)
        {
            ctx.Reply("No clan. Relation to the castle here: " + relation + ".");
            return;
        }

        var members = plugin.Clans.Members(clan);
        var castles = plugin.Clans.Castles(clan);
        ctx.Reply("Clan " + clan.Entity + " guid=" + clan.ClanGuid.ToString("N") + ": " + members.Count + " member(s), "
            + castles.Count + " castle(s). Relation to the castle here: " + relation + ".");
        SampleLog.Info(Area, "clan " + clan.Entity + " members=" + members.Count + " castles=" + castles.Count + " relation=" + relation);
    }

    private static bool TryResolve(ICommandContext ctx, PluginContext plugin, string text, out InventoryRef container, out string label, out string why)
    {
        container = InventoryRef.None;
        label = text;
        why = "";
        if (!TryParseContainer(text, out var token))
        {
            why = "Not a container: '" + text + "'. " + UsageReply;
            return false;
        }

        var part = token.Output ? InventoryPart.Output : InventoryPart.Main;
        switch (token.Kind)
        {
            case TokenKind.Bag:
                container = plugin.Inventory.Of(ctx.CharacterEntity);
                return true;
            case TokenKind.Heart:
            {
                var castle = CastleHere(ctx, plugin);
                if (!castle.Enabled) { why = "No castle here or of yours."; return false; }
                container = plugin.Inventory.Of(castle.Heart.ToEntity());
                return true;
            }
            case TokenKind.Entity:
                container = plugin.Inventory.Of(new Entity { Index = token.Index, Version = token.Version }, part);
                return true;
            case TokenKind.Id:
                if (!plugin.Inventory.TryFind(token.Id, out var owner, out why)) return false;
                container = plugin.Inventory.Of(owner, part);
                return true;
            default:
            {
                var castle = CastleHere(ctx, plugin);
                var rows = castle.Enabled ? Rows(castle) : Array.Empty<CastleContainerInfo>();
                if (token.Row > rows.Count) { why = "No row #" + token.Row + "; run .items chests (" + rows.Count + " rows)."; return false; }
                var row = rows[token.Row - 1];
                container = token.Output ? row.Output : row.Main;
                if (container.IsNone) { why = "#" + token.Row + " has no output inventory."; return false; }
                return true;
            }
        }
    }

    private static bool TryItem(World world, string token, out int guid, out string why)
    {
        why = "";
        if (int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out guid) && guid != 0) return true;
        if (PrefabLookup.TryGetGuidByName(world, token, out var resolved) && resolved._Value != 0)
        {
            guid = resolved._Value;
            return true;
        }

        why = "No item named '" + token + "'. Pass a spawnable name (Item_Ingredient_Mineral_IronOre) or the GUID hash.";
        return false;
    }

    private static string ItemName(World world, int guid) =>
        PrefabLookup.TryGetName(world, new Stunlock.Core.PrefabGUID(guid), out string name) && name.Length > 0
            ? name
            : guid.ToString(CultureInfo.InvariantCulture);
#endif
}
