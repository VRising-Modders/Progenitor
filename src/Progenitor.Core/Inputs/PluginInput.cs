#if !PROGENITOR_TESTS
using System;
using Progenitor.Plugins;
using Unity.Entities;
using Unity.Mathematics;

namespace Progenitor.Inputs;

/// <summary>
/// <see cref="PluginContext.Input"/>: steer the <b>local</b> player the way the keyboard and mouse
/// would — aim at a point or unit, hover a unit, walk, press synced buttons — and hand control
/// back. <b>Client only</b>: on a server <see cref="TryAcquire"/> returns
/// <see cref="InputAcquireResult.ServerSide"/>. The server receives ordinary input commands and
/// applies its own movement, cooldown and ability rules.
///
/// <para>Control is a <see cref="InputLease"/> over some <see cref="InputChannels"/>, one owner
/// per channel. A request for a held channel is refused unless it asks to preempt. Every lease
/// this plugin holds ends when the plugin unloads (any reason). By default a lease pauses a
/// channel while the user is using it (<see cref="UserPolicy.Yield"/>). The API never moves or
/// hides the OS cursor. See <c>docs/input.md</c>.</para>
/// </summary>
public sealed class PluginInput
{
    private readonly PluginContext _ctx;

    internal PluginInput(PluginContext ctx) => _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));

    /// <summary>
    /// Take the channels in <paramref name="options"/>. On <see cref="InputAcquireResult.Acquired"/>
    /// <paramref name="lease"/> is live and tracked for this plugin's unload; otherwise it is null
    /// and nothing changed (a refused preempt ends nothing).
    /// </summary>
    public InputAcquireResult TryAcquire(InputLeaseOptions options, out InputLease? lease)
    {
        _ctx.EnsureAttached();
        lease = null;
        if (!_ctx.IsClient) return InputAcquireResult.ServerSide;
        var result = InputLeases.TryAcquire(_ctx.Guid, options, out lease);
        if (result != InputAcquireResult.Acquired || lease == null) return result;

        var acquired = lease;
        var untrack = new ActionDisposable(() => InputDirector.Current?.End(acquired, InputLeaseEnd.PluginUnloaded));
        _ctx.Track(untrack);
        acquired.Ended += _ => _ctx.Untrack(untrack);
        return result;
    }

    /// <summary>Channels any lease (this plugin's or another's) holds now.</summary>
    public InputChannels HeldChannels => InputDirector.Current?.HeldChannels ?? InputChannels.None;

    /// <summary>The holder (plugin GUID) of one channel, or null.</summary>
    public string? HolderOf(InputChannels channel) => InputDirector.Current?.HolderOf(channel);

    /// <summary>Diagnostics: layout, local player, whether commands reach the server, focus, leases.</summary>
    public InputStatus Status => InputDirector.Current?.Status() ?? new InputStatus { LayoutOk = true, Issue = "no input director (server, or Core not installed)" };
}

/// <summary>
/// Framework entry for input leases that belong to no plugin context (the Dev bridge's
/// <c>bot</c> op). Plugins use <see cref="PluginContext.Input"/>, which also ends their leases on
/// unload; a lease taken here lives until disposed or ended by the framework.
/// </summary>
public static class InputLeases
{
    /// <summary>Acquire as <paramref name="holder"/>. Client only; main thread only.</summary>
    public static InputAcquireResult TryAcquire(string holder, InputLeaseOptions options, out InputLease? lease)
    {
        lease = null;
        if (Plugin.IsServerProcess) return InputAcquireResult.ServerSide;
        var director = InputDirector.Current;
        if (director == null) return InputAcquireResult.NoLocalPlayer;
        director.RequireMainThread();
        if (!InputFrame.EnsureLayout(director)) return InputAcquireResult.LayoutRefused;
        var result = director.TryAcquire(string.IsNullOrWhiteSpace(holder) ? "unnamed" : holder.Trim(), options, out lease);
        if (result == InputAcquireResult.Acquired) InputFrame.Arm();
        return result;
    }

    /// <summary>Status of the process's input director.</summary>
    public static InputStatus Status => InputDirector.Current?.Status() ?? new InputStatus { LayoutOk = true, Issue = "no input director (server, or Core not installed)" };
}

public sealed partial class InputLease
{
    /// <summary>
    /// Aim at a <b>client-world</b> unit, re-resolved every frame from its position. When it cannot
    /// be found the last point is kept and <see cref="TargetLost"/> is set.
    /// </summary>
    public void AimAt(Entity clientUnit)
    {
        if (!Active) return;
        RequireClientEntity(clientUnit, nameof(clientUnit), needNetworkId: false);
        AimAtTarget(UnityInputRuntime.Ref(clientUnit));
    }

    /// <summary>
    /// Hover a <b>client-world</b> unit that has a <c>NetworkId</c>: targeted spells, feeding and
    /// interactions pick it. Writes <c>HoveredEntity</c> and the unit's network id. When it
    /// disappears, nothing is hovered and <see cref="TargetLost"/> is set.
    /// </summary>
    public void Hover(Entity clientUnit)
    {
        if (!Active) return;
        RequireClientEntity(clientUnit, nameof(clientUnit), needNetworkId: true);
        HoverTargetSet(UnityInputRuntime.Ref(clientUnit));
    }

    /// <summary>Walk in world direction (x, z); see <see cref="Move(float, float)"/>.</summary>
    public void Move(float2 direction) => Move(direction.x, direction.y);

    private static void RequireClientEntity(Entity e, string name, bool needNetworkId)
    {
        var r = UnityInputRuntime.Ref(e);
        if (!UnityInputRuntime.Instance.TryGetPosition(r, out _))
            throw new ArgumentException("Entity " + e.Index + ":" + e.Version + " does not exist in the client world or has no position (a server entity id is not valid here).", name);
        if (needNetworkId && !UnityInputRuntime.Instance.TryGetNetworkId(r, out _))
            throw new ArgumentException("Entity " + e.Index + ":" + e.Version + " has no NetworkId, so it cannot be hovered.", name);
    }
}
#endif
