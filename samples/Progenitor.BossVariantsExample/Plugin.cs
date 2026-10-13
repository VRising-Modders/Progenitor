using System;
using System.Collections.Generic;
using System.Text;
using BepInEx;
using Progenitor.Ai;
using Progenitor.Commands;
using Progenitor.Plugins;
using Progenitor.Units;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace Progenitor.BossVariantsExample;

/// <summary>
/// Six Frostmaws near the caller: two vanilla, two "Frenzied", two "Sentinel", all on one
/// self-friendly faction so they do not down each other. Uses only the public ctx.Ai / ctx.Units API.
/// </summary>
[BepInPlugin("progenitor.bossvariants", "Progenitor Boss Variants Example", "0.1.0")]
[BepInDependency("progenitor")]
[ProgenitorSide(Side.Server)]
public sealed class Plugin : ProgenitorPlugin<BossContext>
{
    internal const string Boss = "CHAR_Wendigo_VBlood";

    /// <summary>
    /// Faction_Wendigo is not self-friendly (Friendly mask 0), so several Frostmaws hit each other.
    /// Faction_Bandits is (its Friendly mask contains its own Membership) and is hostile to players.
    /// </summary>
    internal const string SharedFaction = "Faction_Bandits";

    internal const string Frenzied = "Frenzied";
    internal const string Sentinel = "Sentinel";

    protected override void OnLoad(BossContext ctx)
    {
        // Frenzied: Snow Storm and Ice Beam from full health, frost nova every 8 s (the ability’s own
        // cooldown still applies as a floor), no melee, defeated (downed) at 25 % instead of 0 %.
        //
        // Ice Beam’s hit has no team check in vanilla, so a beam aimed at the player used to damage the
        // other Frostmaws in its line. Core’s ally-safe default (ctx.Ai.AllySafety) guards it, so the
        // variant can use it in a group. See docs/ai.md, "Ally-safe damaging hits".
        ctx.Ai.Variants.Define(Frenzied, new VariantSpec(Boss)
        {
            AllowExperimental = true,
            PhaseThreshold = 0.25f,
            Faction = SharedFaction,
        }
        .HealthGate("SnowStorm", 1f)
        .HealthGate("IceBeam_First", 1f)
        .EditCastOption("FrostNova", new CastOptionEdit { CooldownSeconds = 8f, Weight = 150 })
        .EditCastOption("MeleeAttack", new CastOptionEdit { Weight = 0 }));

        // Sentinel: a private tree copy whose cast node always succeeds without casting. It chases,
        // strafes and relocates like Frostmaw but never attacks.
        ctx.Ai.Variants.Define(Sentinel, new VariantSpec(Boss)
        {
            AllowExperimental = true,
            Tree = tree => tree.DisableCombat(),
            Faction = SharedFaction,
        });

        ctx.Commands.Register(new BossCommands(ctx));
        ctx.Log.LogInfo("Boss variants example: loaded");
    }

    protected override void OnUnload(BossContext ctx, PluginUnloadReason reason)
    {
        // Units, variant edits and handles are tracked by the context and released on every reason.
        ctx.Log.LogInfo("Boss variants example: unloading, reason=" + reason);
    }
}

public sealed class BossContext : PluginContext
{
    internal readonly List<(IUnitHandle Handle, string Variant)> Spawned = new();
}

[CommandGroup("bossvariants")]
internal sealed class BossCommands
{
    private readonly BossContext _ctx;

    internal BossCommands(BossContext ctx) => _ctx = ctx;

    [Command("spawn", adminOnly: true, description: "Spawn 2 vanilla, 2 Frenzied and 2 Sentinel Frostmaws around you.")]
    public void Spawn(ICommandContext cmd)
    {
        if (!TryPosition(cmd, out var centre))
        {
            cmd.Reply("No position for your character.");
            return;
        }

        string[] plan = { "", "", Plugin.Frenzied, Plugin.Frenzied, Plugin.Sentinel, Plugin.Sentinel };
        var options = new UnitSpawnOptions { Faction = Plugin.SharedFaction };
        var fresh = new List<(IUnitHandle, string)>();
        for (int i = 0; i < plan.Length; i++)
        {
            // A ring 5 m out: close enough that the bosses aggro on the caller (about a 10 m vicinity).
            double angle = i * Math.PI * 2 / plan.Length;
            var at = new UnitVec3(centre.X + 5f * (float)Math.Cos(angle), centre.Y, centre.Z + 5f * (float)Math.Sin(angle));
            var handle = _ctx.Units.Spawn(Plugin.Boss, at, options);
            if (!handle.Enabled)
            {
                cmd.Reply("Spawn " + (i + 1) + " failed; see the server log.");
                continue;
            }
            fresh.Add((handle, plan[i]));
            _ctx.Spawned.Add((handle, plan[i]));
        }

        // The tree binding resolves after the spawn tick, so variants go on a moment later.
        Progenitor.Dev.MainThread.PostDelayed(1.5, () => ApplyVariants(fresh), _ctx.Owner);
        cmd.Reply("Spawned " + fresh.Count + " Frostmaws on " + Plugin.SharedFaction + "; variants apply in 1.5 s. .bossvariants status to watch.");
    }

    [Command("status", adminOnly: true, description: "Variant, state, health and recent casts of each example boss.")]
    public void Status(ICommandContext cmd)
    {
        var lines = new StringBuilder();
        int n = 0;
        foreach (var entity in AllUnits(out var adopted))
        {
            try
            {
                var seen = _ctx.Ai.Observe(entity);
                lines.Append(++n).Append(". ").Append(seen.Variant.Length > 0 ? seen.Variant : "vanilla")
                    .Append(adopted.Contains(entity) ? " (restored from save)" : "")
                    .Append(' ').Append(seen).Append('\n');
            }
            catch (Exception e)
            {
                lines.Append(++n).Append(". ").Append(entity.Index).Append(':').Append(entity.Version).Append(" unreadable: ").Append(e.Message).Append('\n');
            }
        }
        cmd.Reply(n == 0 ? "No example bosses." : lines.ToString().TrimEnd());
    }

    [Command("engage", adminOnly: true, description: "Point every example boss at you and request combat.")]
    public void Engage(ICommandContext cmd)
    {
        int ok = 0;
        foreach (var (handle, _) in _ctx.Spawned)
        {
            if (handle.IsDisposed || !handle.Enabled) continue;
            var result = _ctx.Units.Engage(handle, cmd.CharacterEntity);
            if (result.Ok) ok++;
        }
        cmd.Reply("Engaged " + ok + " boss(es).");
    }

    /// <summary>Radius of the <c>.bossvariants clear</c> sweep around the caller, in meters.</summary>
    internal const float ClearRadius = 50f;

    [Command("clear", adminOnly: true, description: "Remove the example bosses, their summons and the loot around you.")]
    public void Clear(ICommandContext cmd)
    {
        // Undo the variant edits on our bosses before they go.
        foreach (var (handle, _) in _ctx.Spawned)
            if (!handle.IsDisposed && handle.Enabled) _ctx.Ai.Variants.Remove(handle.Entity.ToEntity());

        // Owned scope: only bosses this plugin spawned, their summons (the pillars and adds a boss
        // keeps summoning while it is removed) and drops. Boss loot does not record who dropped it,
        // so unattributed drops are opted in: this also removes any other loose item inside the
        // radius, never one owned by a player.
        string cleared = "No position for your character; skipped the area clear.";
        if (TryPosition(cmd, out var centre))
        {
            var result = _ctx.Units.ClearArea(new float3(centre.X, centre.Y, centre.Z), ClearRadius, new ClearAreaOptions
            {
                Scope = ClearAreaScope.Owned,
                IncludeUnattributedDrops = true,
                OnComplete = done => _ctx.Log.LogInfo("Boss variants example: clear finished: " + done),
            });
            cleared = result.Enabled
                ? "Cleared within " + ClearRadius + " m: " + result.UnitsRemoved + " boss(es), " + result.SummonsRemoved
                  + " summon(s), " + result.DropsRemoved + " drop(s) so far; later passes run for a few seconds (server log)."
                : "Area clear refused: " + result.Refusal + ".";
        }

        // Bosses that wandered outside the radius: despawn through their handles (their summons follow).
        int outside = 0;
        foreach (var (handle, _) in _ctx.Spawned)
        {
            if (handle.IsDisposed) continue;
            handle.Dispose();
            outside++;
        }
        _ctx.Spawned.Clear();

        // Bosses restored from a save have no Units handle, so the Owned scope does not reach them:
        // this example reverts them to vanilla and leaves them for an admin to remove.
        int reverted = 0;
        foreach (string name in new[] { Plugin.Frenzied, Plugin.Sentinel })
            foreach (var entity in _ctx.Ai.Variants.UnitsOf(name))
                if (_ctx.Ai.Variants.Remove(entity)) reverted++;

        cmd.Reply(cleared
            + (outside > 0 ? " Despawned " + outside + " boss(es) outside the radius." : "")
            + (reverted > 0 ? " Reverted " + reverted + " restored-from-save boss(es) to vanilla; they were not despawned." : ""));
    }

    private void ApplyVariants(List<(IUnitHandle Handle, string Variant)> fresh)
    {
        foreach (var (handle, variant) in fresh)
        {
            if (variant.Length == 0 || handle.IsDisposed || !handle.Enabled) continue;
            try
            {
                var applied = _ctx.Ai.Variants.Apply(handle.Entity.ToEntity(), variant);
                // The marker is confirmed now or a second later; a problem is also logged by Core.
                if (applied.MarkerProblem != null)
                    _ctx.Log.LogWarning("Boss variants example: " + variant + " on " + handle.Entity + " has no persistence marker: " + applied.MarkerProblem);
            }
            catch (Exception e)
            {
                _ctx.Log.LogWarning("Boss variants example: " + variant + " on " + handle.Entity + " refused: " + e.Message);
            }
        }
    }

    private IEnumerable<Entity> AllUnits(out HashSet<Entity> adopted)
    {
        var list = new List<Entity>();
        var mine = new HashSet<Entity>();
        foreach (var (handle, _) in _ctx.Spawned)
        {
            if (handle.IsDisposed || !handle.Enabled) continue;
            var e = handle.Entity.ToEntity();
            list.Add(e);
            mine.Add(e);
        }
        adopted = new HashSet<Entity>();
        foreach (string name in new[] { Plugin.Frenzied, Plugin.Sentinel })
        {
            foreach (var e in _ctx.Ai.Variants.UnitsOf(name))
            {
                if (mine.Contains(e)) continue;
                list.Add(e);
                adopted.Add(e);
            }
        }
        return list;
    }

    private static bool TryPosition(ICommandContext cmd, out UnitVec3 position)
    {
        position = default;
        var character = cmd.CharacterEntity;
        var em = cmd.World.EntityManager;
        if (character == Entity.Null || !em.Exists(character)) return false;
        float3 p = em.GetComponentData<LocalToWorld>(character).Position;
        position = new UnitVec3(p.x, p.y, p.z);
        return true;
    }
}
