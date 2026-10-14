#if !PROGENITOR_TESTS
using System;
using Progenitor.Castles;
using Progenitor.Plugins;
using Unity.Entities;

namespace Progenitor.Materials;

/// <summary>
/// <see cref="PluginContext.MaterialSource"/> (alias <c>ctx.Stations.MaterialSource</c>): register the
/// one provider that decides where build and craft materials come from (milestone P3). The totals shown
/// to the player and the server's payment both follow the provider; the game's own shared storage
/// applies when nothing is registered, for operations the provider does not govern, for contexts it
/// returns no candidates for, and for items it returns null for.
///
/// <para><b>Status: experimental.</b> Registration, invalidation, the status readback, the
/// per-operation transaction and the client view are game-free tested. The native availability and
/// payment seams are opt-in Bootstrap seams (<c>[Materials] NativeSeams = true</c>, then restart the
/// client and the server), off by default; read <see cref="Status"/> (<c>HooksInstalled</c>,
/// <c>Wired</c>) rather than assuming a registration changes anything in the game.</para>
/// </summary>
public sealed class PluginMaterialSource
{
    private readonly PluginContext _ctx;

    internal PluginMaterialSource(PluginContext ctx) => _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));

    /// <summary>
    /// Register <paramref name="provider"/>. Server only. Throws <see cref="InvalidOperationException"/>
    /// naming the current owner when another plugin's provider is registered. The handle is tracked,
    /// so unload releases it; dispose it to unregister earlier.
    /// </summary>
    public IDisposable Register(IMaterialSourceProvider provider)
    {
        RequireServer(nameof(Register));
        var handle = MaterialRuntime.Registry.Register(provider, _ctx.Guid);
        _ctx.Track(handle);
        MaterialRuntime.OnRegistrationChanged();
        return handle;
    }

    /// <summary>Mark one player's displayed totals stale; fresh totals are sent on the next refresh. Server only.</summary>
    public void Invalidate(Entity user)
    {
        RequireServer(nameof(Invalidate));
        MaterialRuntime.Registry.Invalidate(CastleId.From(user));
    }

    /// <summary>Mark every player's displayed totals stale and re-read the provider's operations. Server only.</summary>
    public void InvalidateAll()
    {
        RequireServer(nameof(InvalidateAll));
        MaterialRuntime.Registry.InvalidateAll();
    }

    /// <summary>
    /// What is registered and what is actually armed in this process. Works on both sides; on the
    /// client it reports the client's availability seam and whether totals have arrived.
    /// </summary>
    public MaterialSourceStatus Status
    {
        get
        {
            _ctx.EnsureAttached();
            return MaterialRuntime.Registry.Status();
        }
    }

    private void RequireServer(string op)
    {
        _ctx.EnsureAttached();
        if (!Plugin.IsServerProcess)
            throw new InvalidOperationException("ctx.MaterialSource." + op + " runs on the server side only; the provider decides server-authoritative payments.");
        if (!Dev.MainThread.IsMainThread)
            throw new InvalidOperationException("ctx.MaterialSource." + op + " must run on the game main thread.");
    }
}
#endif
