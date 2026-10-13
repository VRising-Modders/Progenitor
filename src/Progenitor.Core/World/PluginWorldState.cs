using System.Globalization;

namespace Progenitor.WorldState;

/// <summary>
/// <c>ctx.WorldState</c>: the world-load epoch, for a plugin that records raw entity ids
/// (job FEPOCH, R32 findings 1 and 6).
///
/// <para><b>The rule.</b> Any <c>Entity</c> you keep past the current frame must be kept with
/// <see cref="LoadEpoch"/> beside it, and dropped when the epoch has moved. A V Rising save
/// reload rebuilds every entity in the live <c>World</c> from index 0 upward at version 1
/// (<see cref="WorldEpoch"/> has the reload table), so <c>EntityManager.Exists</c> on a retained
/// <c>index:version</c> pair can answer <b>true</b> after a reload while naming a completely
/// different entity. The epoch is the only signal that sees that; a liveness check is an
/// additional guard for a death or demolition <i>within</i> one epoch, not a substitute.</para>
///
/// <para><b>It moves on both processes.</b> On the server the epoch moves once per completed save
/// reload. On the client, which never runs the save loader, it moves once when the client world is
/// created and once when it ends: a disconnect, a reconnect or a server change rebuilds
/// <c>Client_0</c> from scratch and invalidates every retained id exactly the same way (R33 finding
/// 5). Either way it is the same <see cref="WorldLoaded"/> event and the same log line.</para>
///
/// <para><b>Treat it as a token, not a number.</b> <see cref="LoadEpoch"/> is stamped with the Core
/// generation that produced it (<see cref="WorldEpoch"/> has the arithmetic), so a stamp kept across
/// a Core hot-swap — including one persisted through <c>ModSave</c> — is stale by construction.
/// Compare it with <see cref="IsStale"/>; do not subtract from it, order by it, or read the number
/// as a count. <see cref="Loads"/> is this Core generation's loads, not the process's.</para>
///
/// <para>Core's own retained ids already do this: <c>ICastleHandle</c> reports
/// <c>Enabled == false</c> after the epoch moves, and <c>ctx.GodMode</c> drops a guard stamped in
/// an older epoch on its next check.</para>
///
/// <para>Subscribe to <see cref="WorldLoaded"/> through <c>ctx.Events</c> to be told when it
/// moves; the subscription is dropped on unload like any other.</para>
///
/// <example><code>
/// int epoch = ctx.WorldState.LoadEpoch;
/// _boss = someEntity;
/// // …later…
/// if (ctx.WorldState.IsStale(epoch)) _boss = Entity.Null;   // the save was reloaded
/// </code></example>
///
/// <para>Named <c>WorldState</c> rather than <c>World</c> because <c>ctx.World</c> is already the
/// plugin's <c>Unity.Entities.World</c>.</para>
/// </summary>
public sealed class PluginWorldState
{
    /// <summary>Shared: the epoch is Core-wide, so this holds nothing per plugin.</summary>
    internal static readonly PluginWorldState Instance = new();

    private PluginWorldState() { }

    /// <summary>
    /// The epoch the world is in: an opaque, generation-stamped token, never 0 or negative, that
    /// changes once per completed world load (server) or client world create/end. Stamp it next to
    /// any retained entity id and compare it - with <see cref="IsStale"/> - before you use that id
    /// again.
    /// </summary>
    public int LoadEpoch => WorldEpoch.Current;

    /// <summary>How many world loads this Core generation has completed (a hot-swap starts over).</summary>
    public int Loads => WorldEpoch.Loads;

    /// <summary>
    /// True when <paramref name="stampedEpoch"/> — a <see cref="LoadEpoch"/> read earlier — is
    /// not the epoch the world is in now: the world was rebuilt in between - a save reload on the
    /// server, a client world created or ended on the client - or the stamp came from an earlier
    /// Core generation. Either way every entity id recorded at that time is meaningless.
    /// </summary>
    public bool IsStale(int stampedEpoch) => WorldEpoch.IsStale(stampedEpoch);

    /// <summary>Status line: <c>world: epoch=2 loads=1</c>.</summary>
    public string Describe() =>
        "world: epoch=" + LoadEpoch.ToString(CultureInfo.InvariantCulture)
        + " loads=" + Loads.ToString(CultureInfo.InvariantCulture);
}
