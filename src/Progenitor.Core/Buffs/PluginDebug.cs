using System;
using System.Collections.Generic;
using Progenitor.GodMode;
using Progenitor.Plugins;
using Progenitor.WorldState;
using Unity.Entities;

namespace Progenitor.Buffs;

/// <summary>
/// <c>ctx.Debug</c>. Server debug protection. Client calls throw. Protection is held under this
/// plugin's own guard owner and released on every unload reason: the kill-path guard, the
/// protection buffs and an <c>Immortal</c> grant this framework made. Shared buffs stay while
/// another owner (another plugin or the bridge <c>protect</c> op) still guards the character.
/// </summary>
public sealed class PluginDebug
{
    private readonly PluginContext _ctx;
    private readonly GodModeGuard.GodModeOwner _owner;
    private readonly Dictionary<Entity, int> _held = new();

    internal PluginDebug(PluginContext ctx)
    {
        _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
        _owner = new GodModeGuard.GodModeOwner(ctx.Owner, "ctx.Debug");
        ctx.Track(new ActionDisposable(ReleaseAll));
    }

    public DebugProtectionResult Protect(Entity character)
    {
        var world = RequireWorld();
        var result = DebugProtection.Protect(world, character, _owner);
        _held[character] = WorldEpoch.Current;
        return result;
    }

    /// <summary>Block death for this player and leave her targetable. Does not apply the no-aggro buff.</summary>
    public DebugProtectionResult KeepAlive(Entity character)
    {
        var world = RequireWorld();
        var result = DebugProtection.KeepAlive(world, character, _owner);
        _held[character] = WorldEpoch.Current;
        return result;
    }

    public DebugProtectionResult Release(Entity character)
    {
        var world = RequireWorld();
        _held.Remove(character);
        return DebugProtection.Release(world, character, _owner);
    }

    private World RequireWorld()
    {
        _ctx.EnsureAttached();
        return _ctx.World ?? throw new InvalidOperationException("no world");
    }

    /// <summary>
    /// Unload: release every character this plugin still holds in the current world. Entries
    /// from an earlier world load are dropped without touching whatever now has that id.
    /// </summary>
    private void ReleaseAll()
    {
        var world = _ctx.World;
        int epoch = WorldEpoch.Current;
        foreach (var (character, heldEpoch) in _held)
        {
            if (heldEpoch != epoch || world == null || !world.IsCreated) continue;
            try
            {
                if (world.EntityManager.Exists(character))
                    DebugProtection.Release(world, character, _owner);
            }
            catch (Exception e)
            {
                Plugin.Logger?.LogWarning("ctx.Debug: release on unload failed for "
                    + character.Index + ":" + character.Version + ": " + e.Message);
            }
        }
        _held.Clear();
        DebugProtection.Forget(_ctx.Owner);
    }
}
