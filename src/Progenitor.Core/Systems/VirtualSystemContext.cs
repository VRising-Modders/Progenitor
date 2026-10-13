using System;
using Unity.Entities;

namespace Progenitor.Systems;

/// <summary>
/// Per-invoke view handed to a virtual system. Created fresh each dispatch so nested group
/// updates cannot clobber each other's <see cref="Stop"/>. <see cref="World.Time"/>'s
/// <c>ElapsedTime</c> reads 0 through the interop, so <see cref="Frame"/> is the elapsed-time
/// stand-in (invocation count of this callback, not Unity's <c>Time.frameCount</c>).
/// </summary>
public sealed class VirtualSystemContext
{
    private readonly VirtualSystemSlot _slot;
    private readonly float _deltaTime;

    internal VirtualSystemContext(World world, VirtualSystemSlot slot, float deltaTime)
    {
        World = world ?? throw new ArgumentNullException(nameof(world));
        _slot = slot ?? throw new ArgumentNullException(nameof(slot));
        _deltaTime = deltaTime;
    }

    /// <summary>World whose hooked system just (or is about to) update.</summary>
    public World World { get; }

    /// <summary>Entity manager of <see cref="World"/>. Do not cache across invokes; the world can be recreated on rejoin.</summary>
    public EntityManager EntityManager => World.EntityManager;

    /// <summary>
    /// <c>World.Time.DeltaTime</c> for this tick, or 0 if the interop read threw.
    /// Prefer <see cref="Frame"/> for "every N ticks" throttles — DeltaTime can also come back 0.
    /// </summary>
    public float DeltaTime => _deltaTime;

    /// <summary>
    /// How many times this virtual system has run, including this invoke. 1 on the first call.
    /// Use <c>Frame % N == 0</c> for an every-N-ticks throttle; do not read
    /// <c>World.Time.ElapsedTime</c> (interop returns 0).
    /// </summary>
    public int Frame => _slot.Frame;

    /// <summary>Name passed to <see cref="VirtualSystems.Add"/>, for logs.</summary>
    public string Name => _slot.Name;

    /// <summary>
    /// Unregister this virtual system from inside the callback. The current invoke finishes;
    /// it will not run again. Same effect as disposing the <see cref="IDisposable"/> returned
    /// from Add.
    /// </summary>
    public void Stop() => _slot.Stop();
}
