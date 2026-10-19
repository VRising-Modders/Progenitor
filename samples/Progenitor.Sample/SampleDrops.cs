using System;
using System.Globalization;
using Progenitor.Commands;
using Progenitor.Drops;
using Progenitor.Ecs;
using Unity.Transforms;

namespace Progenitor.Sample;

/// <summary>
/// The <c>.drops</c> verbs for <c>ctx.Drops</c> and the bag-drop events on <c>ctx.Inventory</c>.
/// Server only; admin only (<c>take</c> moves items).
///
/// <list type="bullet">
/// <item><description><c>.drops near [radius]</c>: the item piles within the radius (default 10 m) of
/// your character, nearest first, with their handles and whether <c>TakeInto</c> would take them.</description></item>
/// <item><description><c>.drops take [amount]</c>: up to <c>amount</c> (default the whole pile) of
/// the nearest takeable pile within 10 m into your bag.</description></item>
/// <item><description><c>.drops log</c>: toggle a log line per own-bag ground drop
/// (<c>OnGroundDrop</c>) and per whole-inventory drop (<c>OnDropAll</c>).</description></item>
/// <item><description><c>.drops prefabs</c>: the drop prefabs from the game's prefab table.</description></item>
/// </list>
/// Every action logs a <c>Sample drops: …</c> line.
/// </summary>
internal static class SampleDrops
{
    internal const string Area = "drops";

    internal const string UsageReply = "Usage: .drops near [radius] | take [amount] | log | prefabs";

    private static IDisposable? _ground, _all;

    internal static void Detach()
    {
        _ground?.Dispose();
        _all?.Dispose();
        _ground = _all = null;
    }

    internal static void Handle(ICommandContext ctx, string verb, string arg)
    {
        var plugin = Plugin.Active;
        if (plugin == null) { ctx.Reply("Sample plugin context is not available."); return; }
        var ci = CultureInfo.InvariantCulture;
        switch ((verb ?? "").Trim().ToLowerInvariant())
        {
            case "near":
            {
                if (!Here(ctx, out float x, out float y, out float z)) return;
                float radius = float.TryParse(arg, NumberStyles.Float, ci, out float r) && r > 0 ? r : 10f;
                var piles = plugin.Drops.Near(x, y, z, radius, 12);
                ctx.Reply(piles.Count + " pile(s) within " + radius.ToString("0.#", ci) + " m.");
                foreach (var p in piles)
                    ctx.Reply(p.Id + " item " + p.Item + " x" + p.Amount + " at " + p.Distance.ToString("0.0", ci) + " m"
                        + (p.IsUnique ? " unique" : "") + " via " + p.Route
                        + (p.CanTake ? "" : p.InFlight ? " (in flight: not taken yet)" : !p.IsItemKnown ? " (unknown item: not taken)" : " (no route: not taken)"));
                SampleLog.Info(Area, "near " + radius.ToString("0.#", ci) + " m: " + piles.Count + " pile(s)");
                return;
            }
            case "take":
            {
                if (!Here(ctx, out float x, out float y, out float z)) return;
                int amount = int.TryParse(arg, NumberStyles.Integer, ci, out int n) && n > 0 ? n : int.MaxValue;
                foreach (var p in plugin.Drops.Near(x, y, z, 10f, 64))
                {
                    if (!p.CanTake) continue;
                    var result = plugin.Drops.TakeInto(p.Id, plugin.Inventory.Of(ctx.CharacterEntity), Math.Min(amount, p.Amount));
                    ctx.Reply(result.ToString());
                    SampleLog.Info(Area, "take " + result);
                    return;
                }

                ctx.Reply("No takeable pile within 10 m.");
                return;
            }
            case "log":
            {
                if (_ground != null || _all != null)
                {
                    Detach();
                    ctx.Reply("Stopped the drop log.");
                    return;
                }

                _ground = plugin.Inventory.OnGroundDrop(d => SampleLog.Info(Area, "ground drop " + d));
                _all = plugin.Inventory.OnDropAll(d => SampleLog.Info(Area, "drop all " + d + " items=" + string.Join(",", d.Items)));
                ctx.Reply("Logging bag drops to the server log (Sample drops: …). Run again to stop.");
                return;
            }
            case "prefabs":
            {
                bool ok = plugin.Drops.TryPrefabs(out var prefabs, out string why);
                string line = ok ? prefabs.ToString() : "not readable: " + why;
                ctx.Reply(line);
                SampleLog.Info(Area, "prefabs " + line);
                return;
            }
            default:
                ctx.Reply(UsageReply);
                return;
        }
    }

    private static bool Here(ICommandContext ctx, out float x, out float y, out float z)
    {
        x = y = z = 0;
        var world = ctx.World;
        if (world == null || !ctx.CharacterEntity.TryRead(world.EntityManager, out Translation t))
        {
            ctx.Reply("You have no character in the world.");
            return false;
        }

        x = t.Value.x;
        y = t.Value.y;
        z = t.Value.z;
        return true;
    }
}
