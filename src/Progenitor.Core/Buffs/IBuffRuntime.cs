namespace Progenitor.Buffs;

/// <summary>What a target entity can be asked to wear.</summary>
internal enum BuffTargetCheck
{
    /// <summary>Exists and carries <c>BuffBuffer</c>.</summary>
    Ok = 0,

    /// <summary>No such entity in this world (destroyed, never existed, or a stale version).</summary>
    Missing = 1,

    /// <summary>Exists but has no <c>BuffBuffer</c>: the game has nowhere to record a buff on it.</summary>
    NotBuffable = 2,

    /// <summary>There is no world to ask.</summary>
    NoWorld = 3,
}

/// <summary>
/// What a destroy request did. Three outcomes, because two were not enough: the caller has to be
/// able to tell "nothing to do" from "I asked and it did not work", and only the second needs
/// watching.
/// </summary>
internal enum BuffDestroy
{
    /// <summary>The entity was already gone. Nothing was issued and nothing needs watching.</summary>
    NotPresent = 0,

    /// <summary>
    /// <c>DestroyUtility.Destroy</c> was issued. The entity is still there for now — the destroy is
    /// an event the game consumes on a later frame — so this outcome <b>must</b> be handed to the
    /// retirement watch.
    /// </summary>
    Issued = 1,

    /// <summary>
    /// The entity was present and the destroy call failed (it threw). The buff is still on the
    /// wearer and nothing has been scheduled to remove it, so this outcome must be watched and
    /// retried too — it is the case that used to vanish silently.
    /// </summary>
    Failed = 2,
}

/// <summary>
/// What the game does when a buff's timer reaches its duration. Mirrors
/// <c>ProjectM.LifeTimeEndAction</c> (server metadata) as a game-free enum so the
/// refresh decision is testable, with an extra <see cref="Unknown"/> this API can refuse on.
/// </summary>
internal enum BuffLifetimeEnd
{
    /// <summary>Nothing happens at zero; the buff is not ended by its own timer.</summary>
    None = 0,

    /// <summary>The wearer of the buff is killed.</summary>
    Kill = 1,

    /// <summary>The buff entity is destroyed. The ordinary timed-buff case.</summary>
    Destroy = 2,

    /// <summary>
    /// One stack is removed and the elapsed counter restarts, so <c>Duration</c> is the length of a
    /// <b>stack period</b> rather than a lifetime.
    /// </summary>
    RemoveBuffStack = 3,

    /// <summary>A value this build does not recognize. Never guessed at; refused.</summary>
    Unknown = 255,
}

/// <summary>
/// The timing of one live buff instance, read together in a single pass so a caller never has to
/// combine two reads taken at different moments.
/// </summary>
internal readonly struct BuffTiming
{
    internal BuffTiming(float elapsed, float duration, BuffLifetimeEnd end)
    {
        Elapsed = elapsed;
        Duration = duration;
        End = end;
    }

    /// <summary>Seconds already elapsed, from <c>ProjectM.Age.Value</c>.</summary>
    internal float Elapsed { get; }

    /// <summary>The instance's current <c>LifeTime.Duration</c> — the <b>total</b>, not the remainder.</summary>
    internal float Duration { get; }

    /// <summary>The instance's <c>LifeTime.EndAction</c>, which decides what Duration even means.</summary>
    internal BuffLifetimeEnd End { get; }
}

/// <summary>Why <see cref="IBuffRuntime.TryResolveBuff"/> said no.</summary>
internal enum BuffResolve
{
    /// <summary>Resolved to a prefab entity that exists in this world's collection.</summary>
    Ok = 0,

    /// <summary>No definition with that id is registered by the asking owner.</summary>
    NotRegistered = 1,

    /// <summary>Registered, but the registry has not put its clone into the world yet.</summary>
    NotApplied = 2,
}

/// <summary>
/// The game behind <see cref="Buffs"/>. Everything that names a <c>ProjectM</c> or
/// <c>Unity.Entities</c> type lives on the far side of this interface (<c>UnityBuffRuntime</c>);
/// the lifecycle above it compiles and is driven by a fake in the test project.
///
/// <para>Every method is <b>main-thread, server-side, read-or-write-now</b> and swallows game
/// exceptions rather than throwing into a frame handler: a call that could not be completed
/// returns false with a reason, which the state machine treats as "not established" and never as
/// "it worked".</para>
/// </summary>
internal interface IBuffRuntime
{
    /// <summary>True in a V Rising server process. Everything else refuses before any write.</summary>
    bool IsServer { get; }

    /// <summary>True when there is a created server world to operate on.</summary>
    bool HasWorld { get; }

    /// <summary>
    /// The current world-load epoch. An application stamps this at apply time and stops touching
    /// the world when it moves: entity indices are re-issued across a save reload, so a stamped
    /// target from the previous epoch names a different entity.
    /// </summary>
    int Epoch { get; }

    /// <summary>
    /// Resolve a <c>DefineBuff</c> id owned by <paramref name="owner"/> to its clone's PrefabGUID
    /// hash, and confirm the clone is actually in this world's prefab collection.
    /// </summary>
    BuffResolve TryResolveBuff(string buffId, object? owner, string? pluginId, out int prefabGuid);

    /// <summary>Can this entity wear a buff right now?</summary>
    BuffTargetCheck CheckTarget(BuffEntityId target);

    /// <summary>
    /// Queue the game's own debug apply for <paramref name="prefabGuid"/> on
    /// <paramref name="target"/>. Returns false when the apply system is not in this world.
    /// <b>Queuing is not applying</b>: the buff entity appears on a later update, which is why
    /// the caller polls <see cref="TryFindInstance"/>.
    /// </summary>
    bool RequestApply(BuffEntityId target, int prefabGuid, out string reason);

    /// <summary>
    /// The live instance of <paramref name="prefabGuid"/> in <paramref name="target"/>'s
    /// <c>BuffBuffer</c>, if the game has built one.
    /// </summary>
    bool TryFindInstance(BuffEntityId target, int prefabGuid, out BuffEntityId instance);

    /// <summary>
    /// Write <c>LifeTime.Duration</c> on a live buff instance. <c>EndAction</c> is left exactly as
    /// the cloned vanilla buff authored it. False when the instance is gone or carries no
    /// <c>LifeTime</c> (<paramref name="timed"/> false for the latter).
    /// </summary>
    bool SetDuration(BuffEntityId instance, float seconds, out bool timed);

    /// <summary>
    /// Read a live instance's timing in one pass: elapsed (<c>ProjectM.Age.Value</c>), the current
    /// <c>LifeTime.Duration</c>, and the <c>LifeTime.EndAction</c>. False when the instance is gone,
    /// carries no <c>LifeTime</c>, or carries no readable <c>Age</c>.
    ///
    /// <para>This exists because <c>LifeTime.Duration</c> is the <b>total</b> lifetime, not the
    /// remaining one: <c>LifeTimeSystem</c> holds <c>RefRW&lt;Age&gt;</c> with
    /// <c>RefRO&lt;LifeTime&gt;</c> (server metadata), so elapsed time
    /// lives in a component the API must read before it can offer a refresh that means what it
    /// says. <c>EndAction</c> comes along because it decides whether <c>Duration</c> is a lifetime at
    /// all, and reading it is free - the implementation already reads the whole struct.</para>
    ///
    /// <para><c>Age</c> is <b>not</b> networked and has no snapshot type
    /// in the game's metadata, and the client runs its own <c>AgeSystem</c> integrator,
    /// so this is a server-side read and <c>Age</c> is never written.</para>
    /// </summary>
    bool TryReadTiming(BuffEntityId instance, out BuffTiming timing);

    /// <summary>
    /// End one buff instance the way the game does: <c>DestroyUtility.Destroy</c> with
    /// <c>DestroyDebugReason.TryRemoveBuff</c>.
    ///
    /// <para>Three outcomes, not two. A <c>bool</c> here conflated "the entity was already gone"
    /// with "the destroy call failed", and the caller only started a retirement watch on success —
    /// so a throwing destroy left the buff on the wearer with nothing watching and no retry. That is
    /// the one silent-failure path removing the out-of-band <c>DestroyTag</c> opened up, and it is
    /// exactly what the watch exists to cover.</para>
    /// </summary>
    BuffDestroy DestroyInstance(BuffEntityId instance);

    /// <summary>True while the entity is still allocated in this world.</summary>
    bool Exists(BuffEntityId entity);
}

/// <summary>Default when no runtime is injected: nothing is a server, nothing exists, no write happens.</summary>
internal sealed class NullBuffRuntime : IBuffRuntime
{
    internal static readonly NullBuffRuntime Instance = new();

    public bool IsServer => false;

    public bool HasWorld => false;

    public int Epoch => 0;

    public BuffResolve TryResolveBuff(string buffId, object? owner, string? pluginId, out int prefabGuid)
    {
        prefabGuid = 0;
        return BuffResolve.NotRegistered;
    }

    public BuffTargetCheck CheckTarget(BuffEntityId target) => BuffTargetCheck.NoWorld;

    public bool RequestApply(BuffEntityId target, int prefabGuid, out string reason)
    {
        reason = "no runtime";
        return false;
    }

    public bool TryFindInstance(BuffEntityId target, int prefabGuid, out BuffEntityId instance)
    {
        instance = BuffEntityId.None;
        return false;
    }

    public bool SetDuration(BuffEntityId instance, float seconds, out bool timed)
    {
        timed = false;
        return false;
    }

    public bool TryReadTiming(BuffEntityId instance, out BuffTiming timing)
    {
        timing = default;
        return false;
    }

    public BuffDestroy DestroyInstance(BuffEntityId instance) => BuffDestroy.NotPresent;

    public bool Exists(BuffEntityId entity) => false;
}
