using System;
using System.Collections.Generic;
#if !PROGENITOR_TESTS
using ProjectM;
using ProjectM.Shared;
using Progenitor.Ecs;
using Stunlock.Core;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
#endif

namespace Progenitor.Effects;

/// <summary>
/// Game (or a test fake) behind <see cref="ServerBeam"/>. Tests inject a fake so they never
/// need the game; the server uses <c>UnityServerBeamRuntime</c>. Every method swallows game
/// exceptions and returns a disabled/empty result rather than throw into a hook.
/// </summary>
internal interface IServerBeamRuntime
{
    /// <summary>True in a VRisingServer process. Client Spawn must not touch ECS.</summary>
    bool IsServer { get; }

    /// <summary>
    /// Why the last <see cref="Spawn"/> returned none, for the one log line ServerBeam writes.
    /// Null means the generic "no coil beam prefab" line.
    /// </summary>
    string? LastMissReason { get; }

    /// <summary>Spawnable name last resolved on a successful <see cref="Spawn"/>. Empty on miss.</summary>
    string LastResolvedName { get; }

    /// <summary>
    /// Instantiate the coil-beam prefab (name or GUID hash) and write synced Start/End /
    /// PreviewTime / BlastTime. Returns <see cref="ServerBeamId.None"/> when the prefab
    /// is missing or instantiate failed.
    /// </summary>
    ServerBeamId Spawn(
        string prefabName,
        int prefabGuid,
        EffectVec3 start,
        EffectVec3 end,
        float seconds,
        float previewTime,
        float blastTime);

    /// <summary>
    /// Overlay-write <c>ProfessorCoilBeam_State_Shared.Start/End</c>, move
    /// <c>Translation</c> to the beam midpoint and reset <c>Age</c> so the server's own
    /// coil system never flips the beam to Blast. A non-negative
    /// <paramref name="previewTime"/> is written too; that is the keep-alive, because the
    /// coil snapshot carries nothing else that can change. False when the entity is gone.
    /// </summary>
    bool WriteEndpoints(ServerBeamId id, EffectVec3 start, EffectVec3 end, float previewTime);

    /// <summary>
    /// Spawn the game's <c>NetworkedSequence</c> entity (PrefabGUID 651179295) linking two
    /// entities: <c>SpawnSequenceForEntity { SequenceGuid, Target, SecondaryTarget }</c>
    /// is <c>[NetworkedComponent]</c> and syncs both endpoints as
    /// <c>NetworkId</c>s (<c>Snapshot_SpawnSequenceForEntity</c>), so
    /// the client re-resolves both positions itself every frame and the server writes
    /// nothing per tick. <paramref name="sequenceGuid"/> 0 asks the runtime to resolve a
    /// beam sequence live. Returns <see cref="ServerBeamId.None"/> on any miss.
    ///
    /// <para><paramref name="endIndex"/> 0 is the <b>single-target mark</b>: only
    /// <c>Target</c> is filled, <c>SecondaryTarget</c> stays <c>Entity.Null</c> and
    /// <c>PlayOnSelf</c> is set — the shape the live capture found on entity-targeted rows
    /// (evidence 135), and what the build-mode placement sequences want.</para>
    /// </summary>
    ServerBeamId SpawnLink(
        int sequenceGuid,
        int startIndex,
        int startVersion,
        int endIndex,
        int endVersion,
        float seconds);

    /// <summary>Read <c>Translation.Value</c> for a tracked endpoint. False when missing.</summary>
    bool TryReadTranslation(int index, int version, out EffectVec3 position);

    bool Exists(ServerBeamId id);

    /// <summary>DestroyUtility then DestroyTag (and the dummy event entity, if any).</summary>
    bool Destroy(ServerBeamId id);

    /// <summary>
    /// Client-only per-frame pass over replicated coil beams tagged with a Progenitor
    /// BlastTime marker: re-point the sequence the vanilla client system spawned at the
    /// beam's live synced endpoints, and take the one-shot tint decision. No-op on the
    /// server. The re-point is what makes a server beam <em>track</em>: the vanilla client
    /// system bakes Start/End into the sequence once, on the frame it spawns it, and never
    /// looks at them again (client native <c>OriginalLambdaBody</c>).
    /// </summary>
    void ClientBeamTick();

    /// <summary>
    /// Release anything the runtime cached for this Core generation — the client tick's
    /// <c>EntityQuery</c> above all (R27 finding 10: it is built once and must be disposed,
    /// the pattern <c>PrefabApplicator</c> already uses). Called from
    /// <c>ServerBeam.Uninstall</c> before the runtime reference is dropped. Idempotent.
    /// </summary>
    void Shutdown();
}

/// <summary>ECS identity without taking a Unity.Entities dependency in tests.</summary>
internal readonly struct ServerBeamId : IEquatable<ServerBeamId>
{
    public int Index { get; }
    public int Version { get; }

    public ServerBeamId(int index, int version)
    {
        Index = index;
        Version = version;
    }

    public bool IsNone => Index == 0 && Version == 0;

    public static ServerBeamId None => default;

    public bool Equals(ServerBeamId other) => Index == other.Index && Version == other.Version;

    public override bool Equals(object? obj) => obj is ServerBeamId other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Index, Version);

    public override string ToString() => Index + ":" + Version;

#if !PROGENITOR_TESTS
    public static ServerBeamId From(Unity.Entities.Entity entity) =>
        new(entity.Index, entity.Version);

    public Unity.Entities.Entity ToEntity()
    {
        var e = new Unity.Entities.Entity();
        e.Index = Index;
        e.Version = Version;
        return e;
    }
#endif
}

/// <summary>Default when no runtime is injected: every call is a no-op, reported as client-side.</summary>
internal sealed class NullServerBeamRuntime : IServerBeamRuntime
{
    internal static readonly NullServerBeamRuntime Instance = new();

    public bool IsServer => false;

    public string? LastMissReason => null;

    public string LastResolvedName => "";

    public ServerBeamId Spawn(
        string prefabName,
        int prefabGuid,
        EffectVec3 start,
        EffectVec3 end,
        float seconds,
        float previewTime,
        float blastTime) => ServerBeamId.None;

    public ServerBeamId SpawnLink(
        int sequenceGuid,
        int startIndex,
        int startVersion,
        int endIndex,
        int endVersion,
        float seconds) => ServerBeamId.None;

    public bool WriteEndpoints(ServerBeamId id, EffectVec3 start, EffectVec3 end, float previewTime) => false;

    public bool TryReadTranslation(int index, int version, out EffectVec3 position)
    {
        position = default;
        return false;
    }

    public bool Exists(ServerBeamId id) => false;

    public bool Destroy(ServerBeamId id) => false;

    public void ClientBeamTick() { }

    public void Shutdown() { }
}

/// <summary>
/// Which channel actually re-pointed a replicated beam's sequence this frame.
/// Reported in the one-per-beam client log line as <c>path=…</c>.
/// </summary>
[Flags]
internal enum BeamTrackPath
{
    /// <summary>Nothing re-pointed it: the drawn line stays where it was baked.</summary>
    None = 0,

    /// <summary>
    /// The sequence instance's own <c>Position</c> / <c>Rotation</c> blackboard registers —
    /// the channel <c>ProfessorCoilSystem_Client.OnUpdate</c> writes once after
    /// <c>PlaySequence</c>. This is the coil beam's real re-point channel.
    /// </summary>
    Blackboard = 1,

    /// <summary>
    /// The sequence entity's <c>LineRendererPositionElement</c> buffer,
    /// for sequence families whose body <em>is</em> a two-point LineRenderer.
    /// </summary>
    Buffer = 2,

    /// <summary>A bound driver GameObject was written directly (never for a pooled clone).</summary>
    Hybrid = 4,
}

/// <summary>
/// Game-free frame policy for <c>ClientBeamTick</c> (R27 finding 10). The tick used to build a
/// fresh <c>EntityQuery</c>, a <c>ComponentType[]</c>, a <c>List&lt;Entity&gt;</c> and a temp
/// <c>NativeArray</c> sixty times a second on every client for the whole session, whether or
/// not anything ever called <c>ServerBeam</c>. The query is now cached; this decides when it is
/// worth running at all.
/// </summary>
internal static class ClientBeamPoll
{
    /// <summary>
    /// While the last enumeration found no coil beams at all — the normal state of a client
    /// that is not near one — the query runs only every Nth frame (a quarter second at 60 fps).
    /// A coil beam's body is baked by the vanilla client system on the frame it spawns, so
    /// starting the re-point a few frames later is invisible.
    /// </summary>
    internal const int IdleQueryEveryFrames = 15;

    /// <summary>
    /// True when this frame should enumerate. <paramref name="lastCount"/> is the number of
    /// coil beams the previous enumeration saw (-1 before the first one).
    /// </summary>
    internal static bool ShouldEnumerate(int lastCount, ref int idleFrames)
    {
        if (lastCount != 0)
        {
            idleFrames = 0;
            return true;
        }

        idleFrames++;
        if (idleFrames < IdleQueryEveryFrames) return false;
        idleFrames = 0;
        return true;
    }

    /// <summary>
    /// R27 finding 16's negative evidence: coil beams are replicating but none carries a
    /// Progenitor marker, so the whole EV13/EV14 client path is doing nothing. Logged once per
    /// client.
    /// </summary>
    internal static string NoMarkerLog(int coilBeams) =>
        "Effects: server beam client: " + coilBeams + " coil beams, 0 matched markers";

    /// <summary>
    /// R28 finding 26 (EV16). The cached <c>EntityQuery</c> belongs to one world; the world
    /// swap (character → main menu → character) is detected by comparing
    /// <c>World.SequenceNumber</c>. <c>WorldSeq</c> caught to <c>0UL</c>, so if the number is
    /// not readable through the interop <em>every</em> world reports 0, the inequality is never
    /// true, and <c>ToEntityArray</c> runs on a query built against a destroyed world — a
    /// native crash, not a catchable exception.
    ///
    /// <para>An unreadable sequence number is therefore treated as <b>changed</b>: null on
    /// either side rebuilds. The cost of a wrong rebuild is one query; the cost of a wrong
    /// keep is the process.</para>
    /// </summary>
    /// <param name="built">A query is currently cached.</param>
    /// <param name="cached">Sequence number recorded when it was built, or null if unreadable.</param>
    /// <param name="current">Sequence number of the world now, or null if unreadable.</param>
    internal static bool ShouldRebuild(bool built, ulong? cached, ulong? current)
    {
        if (!built) return false;
        if (cached == null || current == null) return true;
        return cached.Value != current.Value;
    }

    /// <summary>Written once when the world's sequence number cannot be read at all.</summary>
    internal const string UnreadableWorldSeqLog =
        "Effects: server beam client cannot read World.SequenceNumber; "
        + "treating every world check as a change and rebuilding the beam query.";

    /// <summary>
    /// R28 finding 28.4. Both client-query failure paths logged on every frame with no
    /// once-guard and left <c>_lastBeamCount = -1</c>, so <see cref="ShouldEnumerate"/> was true
    /// again the next frame: sixty warnings a second for the session, from the same log the
    /// networking threat model already named as a DoS vector. A failure now reports the beam
    /// count as <see cref="FailureBeamCount"/> so the idle poll applies (one retry every
    /// <see cref="IdleQueryEveryFrames"/> frames) and the line is written once until the next
    /// enumeration succeeds.
    /// </summary>
    internal const int FailureBeamCount = 0;

    /// <summary>One-log-per-episode guard for a repeating client-query failure.</summary>
    internal struct FailureLog
    {
        private bool _logged;

        /// <summary>True while the current failure episode has already been logged.</summary>
        internal bool Logged => _logged;

        /// <summary>Returns <paramref name="log"/> the first time in this episode, else null.</summary>
        internal string? Take(string log)
        {
            if (_logged) return null;
            _logged = true;
            return log;
        }

        /// <summary>A successful enumeration ends the episode, so a later failure logs again.</summary>
        internal void Clear() => _logged = false;
    }
}

/// <summary>
/// Game-free bookkeeping for the client re-point pass: which replicated beams are being
/// driven, through which channel, and the one-per-beam start / end log lines. Kept out of
/// the Unity runtime so the enumeration and logging contract can be unit-tested.
/// </summary>
internal sealed class ClientBeamTracker
{
    /// <summary>Bound on the live map; a client session never has this many beams.</summary>
    internal const int Cap = 1024;

    private sealed class Entry
    {
        internal string Sequence = "";
        internal BeamTrackPath Path;
        internal int Writes;
        internal bool Seen;
    }

    private readonly Dictionary<long, Entry> _live = new();
    private readonly List<long> _sweep = new();

    internal int LiveCount => _live.Count;

    internal void Clear()
    {
        _live.Clear();
        _sweep.Clear();
    }

    /// <summary>
    /// R28 finding 28.9 (EV16). The client world went away (a swap, or the query was released):
    /// every tracked key names an entity of a world that no longer exists. Drop them <b>without</b>
    /// end lines — <see cref="EndTick"/> would otherwise emit
    /// "server beam client tracking ended entity=…" for a dead world's entities on the next
    /// tick in the new one, which reads as beams that ended here and never existed here.
    /// </summary>
    internal void DropForWorldLoss()
    {
        _live.Clear();
        _sweep.Clear();
    }

    /// <summary>Mark every tracked beam unseen; anything still unseen at <see cref="EndTick"/> ended.</summary>
    internal void BeginTick()
    {
        foreach (var pair in _live)
            pair.Value.Seen = false;
    }

    /// <summary>
    /// Record that <paramref name="beam"/> is being driven through <paramref name="path"/>.
    /// Returns the log line the first time this beam is seen, and again if its sequence entity
    /// or its path changed (the client respawns the sequence on a Preview→Blast swap).
    /// </summary>
    internal string? Observe(long key, string beam, string sequence, BeamTrackPath path)
    {
        // EV17: `writes` counts writes, so a beam that was never re-pointed reports 0. The old
        // counter incremented on every tick the beam was *seen*, so a live client that could
        // not re-point at all still ended with `path=none … writes=1341`, which reads as 1341
        // successful writes and was the opposite of the truth.
        bool wrote = path != BeamTrackPath.None;
        if (!_live.TryGetValue(key, out var entry))
        {
            if (_live.Count >= Cap) return null;
            entry = new Entry();
            _live[key] = entry;
        }
        else if (entry.Sequence == sequence && entry.Path == path)
        {
            entry.Seen = true;
            if (wrote) entry.Writes++;
            return null;
        }

        entry.Sequence = sequence;
        entry.Path = path;
        entry.Writes = wrote ? 1 : 0;
        entry.Seen = true;
        return TrackingLine(beam, sequence, path);
    }

    /// <summary>
    /// Drop everything not observed this tick and append one end line per dropped beam.
    /// <paramref name="ended"/> is cleared first; nothing is allocated when nothing ended.
    /// </summary>
    internal void EndTick(List<string> ended)
    {
        ended.Clear();
        _sweep.Clear();
        foreach (var pair in _live)
        {
            if (!pair.Value.Seen)
                _sweep.Add(pair.Key);
        }

        for (int i = 0; i < _sweep.Count; i++)
        {
            long key = _sweep[i];
            if (!_live.TryGetValue(key, out var entry)) continue;
            ended.Add(EndedLine(FormatKey(key), entry.Sequence, entry.Writes));
            _live.Remove(key);
        }

        _sweep.Clear();
    }

    internal static long KeyOf(int index, int version) => ((long)index << 32) ^ (uint)version;

    internal static string FormatKey(long key) =>
        ((int)(key >> 32)) + ":" + unchecked((int)(uint)key);

    internal static string PathName(BeamTrackPath path)
    {
        if (path == BeamTrackPath.None) return "none";
        string s = "";
        if ((path & BeamTrackPath.Blackboard) != 0) s = "blackboard";
        if ((path & BeamTrackPath.Buffer) != 0) s = s.Length == 0 ? "buffer" : s + "+buffer";
        if ((path & BeamTrackPath.Hybrid) != 0) s = s.Length == 0 ? "hybrid" : s + "+hybrid";
        return s;
    }

    internal static string TrackingLine(string beam, string sequence, BeamTrackPath path)
    {
        string line = "Effects: server beam client tracking entity=" + beam
            + " sequence=" + sequence + " path=" + PathName(path);
        if (path == BeamTrackPath.None)
        {
            line += " (no Position/Rotation blackboard input and no LineRendererPositionElement;"
                + " this sequence cannot be re-pointed — use ServerBeam.Link)";
        }

        return line;
    }

    internal static string EndedLine(string beam, string sequence, int writes) =>
        "Effects: server beam client tracking ended entity=" + beam
        + " sequence=" + sequence + " writes=" + writes;
}

#if !PROGENITOR_TESTS
/// <summary>
/// Live runtime. Spawn is Instantiate of the professor-coil beam prefab plus typed-pointer
/// overlays on <c>ProfessorCoilBeam_State_Shared</c> (Start @0 / End @0xC / PreviewTime @0x18 /
/// BlastTime @0x1C, snapshot 32). The game's native CreateBeam
/// is the same four writes through an ECB; we skip the ECB and the coil pair
/// because a dummy event with no <c>ProfessorCoilEvent_SpawnTag</c> is enough for client
/// systems (they read Client + State_Shared + Age). Age is reset
/// each tick so Preview never elapses (PreviewTime max 20).
///
/// EV13 keep-alive. The coil beam's networked snapshot (<c>Snapshot_acdkgjbefjd</c>)
/// carries exactly one component and nothing else; the server only
/// serializes an entity to a user when that snapshot changed (<c>PrioritizeSystem</c>
/// <c>UpToDateUserBitMask</c> / <c>SnapshotFrameChangedBuffer</c>),
/// and the client disables an entity <c>NetworkedTimeout.TimeoutAfter = 3</c> seconds after
/// the last snapshot it deserialized (client <c>TimeoutSystem</c>;
/// <c>ReceivePacketSystem</c> refresh). So every live beam writes
/// <c>PreviewTime</c> at least once a second, alternating between two values, plus
/// <c>Translation</c> at the beam midpoint for the position relevancy in
/// <c>PrioritizeSystem.AddCloseToUserJob</c> and
/// <c>GetPriorityWithPosition</c>. <c>DisableWhenNoPlayersInRange</c>
/// is removed from the instance so
/// <c>DisableWhenNoPlayersInRangeSystem_Server</c> can never disable a beam
/// server-side, which would stop replication silently.
/// </summary>
internal sealed unsafe class UnityServerBeamRuntime : IServerBeamRuntime
{
    internal const int SharedStart = 0;
    internal const int SharedEnd = 12;
    internal const int SharedPreview = 24;
    internal const int SharedBlast = 28;
    internal const int SharedMinSlot = 32;

    internal const int ServerEvent = 0;
    internal const int ServerCoil0 = 8;
    internal const int ServerCoil1 = 12;
    internal const int ServerState = 16;
    internal const int ServerMinSlot = 20;

    internal const int ClientSequenceState = 0x20;
    internal const int ClientMinSlot = 0x28;

    /// <summary>Bound on the per-entity "already logged" sets in <see cref="ClientBeamTick"/>.</summary>
    internal const int SeenKeyCap = 1024;

    /// <summary><c>SpawnSequenceForEntity</c> field offsets (game metadata).</summary>
    internal const int LinkSequenceGuid = 0x0;
    internal const int LinkTarget = 0x4;
    internal const int LinkSecondaryTarget = 0x10;
    internal const int LinkPlayOnSelf = 0x1C;
    internal const int LinkScale = 0x20;
    internal const int LinkMinSlot = 0x30;

    /// <summary>Spawnable name and PrefabGUID of the game's networked sequence carrier (live-read).</summary>
    internal const string LinkPrefabName = "NetworkedSequence";
    internal const int LinkPrefabGuid = 651179295;

    /// <summary><c>Script_WarEvent_PortalToGateSequenceData.SequenceGuid</c> offset (game metadata).</summary>
    internal const int WarEventSequenceGuid = 0x8;
    internal const int WarEventMinSlot = 0x18;

    public bool IsServer => Plugin.IsServerProcess;

    public string? LastMissReason { get; private set; }

    public string LastResolvedName { get; private set; } = "";

    private readonly Dictionary<ServerBeamId, Entity> _events = new();
    private readonly HashSet<long> _tinted = new();
    private readonly HashSet<string> _loggedTint = new();
    private readonly HashSet<long> _warnedDisabled = new();
    private readonly HashSet<long> _inspected = new();
    private readonly HashSet<long> _overwriteLogged = new();
    private readonly Dictionary<long, float3> _lastWritten = new();
    private readonly ClientBeamTracker _tracker = new();
    private readonly List<string> _ended = new();
    private int _linkSequenceGuid;
    private bool _linkSequenceSearched;

    /// <summary>R27 finding 10: one query per Core generation, disposed by <see cref="Shutdown"/>.</summary>
    private EntityQuery _clientQuery;
    private bool _clientQueryBuilt;

    /// <summary>
    /// The world the cached query belongs to, identified by <c>World.SequenceNumber</c>
    /// — <b>not</b> by the managed wrapper. <c>Worlds.Client</c> re-reads
    /// <c>World.s_AllWorlds</c> every call, so Il2CppInterop can hand back a different wrapper
    /// for the same native world each frame; a <c>ReferenceEquals</c> here would rebuild the
    /// query every frame and undo the whole fix (R26 finding 9's lesson, one type over).
    /// </summary>
    private ulong? _clientQueryWorldSeq;
    private World? _clientQueryWorld;
    private ClientBeamPoll.FailureLog _queryFailureLog;
    private ClientBeamPoll.FailureLog _buildFailureLog;
    private ClientBeamPoll.FailureLog _worldSeqFailureLog;
    private int _lastBeamCount = -1;
    private int _idleFrames;
    private bool _noMarkerLogged;

    /// <summary>
    /// The tick's marker classification and its one-per-generation diagnostics (R31 finding 17).
    /// Game-free, so <c>ServerBeamTests</c> drives the same classification this tick runs.
    /// </summary>
    private ClientMarkerLog _markerLog;

    public ServerBeamId Spawn(
        string prefabName,
        int prefabGuid,
        EffectVec3 start,
        EffectVec3 end,
        float seconds,
        float previewTime,
        float blastTime)
    {
        LastMissReason = null;
        LastResolvedName = "";
        var world = Worlds.Server;
        if (world == null || !world.IsCreated)
        {
            LastMissReason = "Effects: no Server world; returning a disabled handle.";
            return ServerBeamId.None;
        }

        var em = world.EntityManager;
        if (!TryResolvePrefab(world, em, prefabName, prefabGuid, out var prefab, out var resolvedName, out var resolvedGuid))
            return ServerBeamId.None;

        Entity beam;
        try { beam = em.Instantiate(prefab); }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("Effects: Instantiate coil beam '" + resolvedName + "' threw: " + e);
            LastMissReason = "Effects: Instantiate '" + resolvedName + "' failed; returning a disabled handle.";
            return ServerBeamId.None;
        }

        if (beam == Entity.Null || !em.Exists(beam))
        {
            LastMissReason = "Effects: Instantiate '" + resolvedName + "' returned a dead entity.";
            return ServerBeamId.None;
        }

        var start3 = new float3(start.X, start.Y, start.Z);
        var end3 = new float3(end.X, end.Y, end.Z);
        Entity dummy = CreateDummyEvent(em, resolvedGuid, previewTime, blastTime, start3);
        if (!TryWriteShared(em, beam, start3, end3, previewTime, blastTime)
            || !TryWriteServerState(em, beam, dummy))
        {
            LastMissReason = "Effects: coil beam component write failed; returning a disabled handle.";
            TryDespawn(em, beam);
            TryDespawn(em, dummy);
            return ServerBeamId.None;
        }

        TryWriteTranslation(em, beam, Midpoint(start3, end3));
        TryWriteAge(em, beam, 0f);
        TryWriteLifeTime(em, beam, MathF.Max(seconds, 1f));
        TryUngateNoPlayersInRange(em, beam);

        var id = ServerBeamId.From(beam);
        if (dummy != Entity.Null)
            _events[id] = dummy;
        LastResolvedName = resolvedName;
        return id;
    }

    /// <summary>
    /// The second server-authored family: the game's own <c>NetworkedSequence</c> entity.
    /// <c>SpawnSequenceForEntity</c> syncs <c>Target</c> and
    /// <c>SecondaryTarget</c> as <c>NetworkedEntity</c> (<c>{ Entity _Entity; bool
    /// _WaitingForSync; }</c>), and the client's
    /// <c>SpawnSequenceForEntitySystem</c> turns them into a
    /// <c>SequenceInstance { Target, SecondaryTarget }</c>
    /// whose blackboard resolves each entity's position every frame
    /// (<c>SequenceGraphLibraryTag.GetEntityPosition</c>). So the endpoints
    /// track natively and nothing is written per tick — and the prefab carries no
    /// <c>NetworkedTimeout</c>, so there is no keep-alive to run either.
    /// </summary>
    public ServerBeamId SpawnLink(
        int sequenceGuid,
        int startIndex,
        int startVersion,
        int endIndex,
        int endVersion,
        float seconds)
    {
        LastMissReason = null;
        LastResolvedName = "";
        var world = Worlds.Server;
        if (world == null || !world.IsCreated)
        {
            LastMissReason = "Effects: no Server world; returning a disabled handle.";
            return ServerBeamId.None;
        }

        var em = world.EntityManager;
        var a = new Entity { Index = startIndex, Version = startVersion };
        bool single = endIndex == 0;
        var b = single ? Entity.Null : new Entity { Index = endIndex, Version = endVersion };
        try
        {
            if (startIndex == 0 || !em.Exists(a) || (!single && !em.Exists(b)))
            {
                LastMissReason = single
                    ? "Effects: server beam mark needs a live entity; returning a disabled handle."
                    : "Effects: server beam link needs two live entities; returning a disabled handle.";
                return ServerBeamId.None;
            }
        }
        catch
        {
            LastMissReason = "Effects: server beam link entity check failed; returning a disabled handle.";
            return ServerBeamId.None;
        }

        int guid = ResolveLinkSequenceGuid(em, sequenceGuid);
        if (guid == 0)
        {
            LastMissReason =
                "Effects: no beam SequenceGUID visible on this server (BuffSequence is client-world only); "
                + "pass one to ServerBeam.Link. Returning a disabled handle.";
            return ServerBeamId.None;
        }

        if (!TryResolveNamedPrefab(world, em, LinkPrefabName, LinkPrefabGuid, out var prefab, out string resolvedName))
        {
            LastMissReason = "Effects: no NetworkedSequence prefab on this server; returning a disabled handle.";
            return ServerBeamId.None;
        }

        Entity link;
        try { link = em.Instantiate(prefab); }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("Effects: Instantiate NetworkedSequence threw: " + e);
            LastMissReason = "Effects: Instantiate NetworkedSequence failed; returning a disabled handle.";
            return ServerBeamId.None;
        }

        if (link == Entity.Null || !em.Exists(link))
        {
            LastMissReason = "Effects: Instantiate NetworkedSequence returned a dead entity.";
            return ServerBeamId.None;
        }

        if (!TryWriteSpawnSequenceForEntity(em, link, guid, a, b, playOnSelf: single))
        {
            LastMissReason = "Effects: SpawnSequenceForEntity write failed; returning a disabled handle.";
            TryDespawn(em, link);
            return ServerBeamId.None;
        }

        // Translation is in this prefab's snapshot, so it seeds the client's relevancy and the
        // sequence's own spawn pose. The client resolves both endpoints itself afterwards, so
        // a stale midpoint costs nothing but relevancy for a user far from the spawn point.
        if (single)
        {
            if (TryReadTranslationEntity(em, a, out var pa1))
                TryWriteTranslation(em, link, pa1);
        }
        else if (TryReadTranslationEntity(em, a, out var pa) && TryReadTranslationEntity(em, b, out var pb))
        {
            TryWriteTranslation(em, link, Midpoint(pa, pb));
        }
        TryWriteAge(em, link, 0f);
        TryWriteLifeTime(em, link, MathF.Max(seconds, 1f));
        TryUngateNoPlayersInRange(em, link);

        LastResolvedName = resolvedName;
        return ServerBeamId.From(link);
    }

    public bool WriteEndpoints(ServerBeamId id, EffectVec3 start, EffectVec3 end, float previewTime)
    {
        if (!TryWorld(out var em) || !TryAlive(em, id, out var beam))
            return false;
        var start3 = new float3(start.X, start.Y, start.Z);
        var end3 = new float3(end.X, end.Y, end.Z);
        if (!TryWriteShared(em, beam, start3, end3, previewTime, blastTime: -1f))
            return false;
        TryWriteAge(em, beam, 0f);
        TryWriteTranslation(em, beam, Midpoint(start3, end3));
        WarnIfDisabled(em, beam);
        return true;
    }

    public bool TryReadTranslation(int index, int version, out EffectVec3 position)
    {
        position = default;
        if (index == 0 || !TryWorld(out var em)) return false;
        var entity = new Entity { Index = index, Version = version };
        try
        {
            if (!em.Exists(entity) || !entity.Has<Translation>(em))
                return false;
            var t = em.GetComponentData<Translation>(entity);
            position = new EffectVec3(t.Value.x, t.Value.y, t.Value.z);
            return true;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Effects: server beam Translation read failed: " + e.Message);
            return false;
        }
    }

    public bool Exists(ServerBeamId id)
    {
        if (id.IsNone || !TryWorld(out var em)) return false;
        try { return em.Exists(id.ToEntity()); }
        catch { return false; }
    }

    public bool Destroy(ServerBeamId id)
    {
        if (!TryWorld(out var em)) return false;
        if (_events.Remove(id, out var dummy))
            TryDespawn(em, dummy);
        var beam = id.ToEntity();
        if (beam == Entity.Null) return false;
        bool existed = false;
        try { existed = em.Exists(beam); }
        catch { return false; }
        TryDespawn(em, beam);
        return existed;
    }

    /// <summary>
    /// R27 finding 10: the query is built once per Core generation and disposed from
    /// <see cref="Shutdown"/>, the enumeration writes into a reused <c>NativeArray</c> with no
    /// managed list or <c>ComponentType[]</c> per frame, and a client with no coil beams in the
    /// world at all only re-runs the query every
    /// <see cref="ClientBeamPoll.IdleQueryEveryFrames"/> frames.
    /// </summary>
    public void ClientBeamTick()
    {
        if (Plugin.IsServerProcess) return;
        // Worlds.Client returns only a created world, so no second IsCreated (a boxed bool) here.
        var world = Worlds.Client;
        if (world == null)
        {
            ReleaseClientQuery(disposeQuery: false);
            _lastBeamCount = -1;
            // R28 finding 28.9: the tracked keys belong to the world that just went away.
            _tracker.DropForWorldLoss();
            _ended.Clear();
            return;
        }

        // R28 finding 26: an unreadable sequence number counts as a change, so a query built
        // against a destroyed world is never enumerated (that is a native crash, not an
        // exception this method could catch).
        if (ClientBeamPoll.ShouldRebuild(_clientQueryBuilt, _clientQueryWorldSeq, WorldSeq(world)))
        {
            // The cached query belongs to the *previous* world, which may already be destroyed.
            ReleaseClientQuery(disposeQuery: false);
            _lastBeamCount = -1;
            _tracker.DropForWorldLoss();
            _ended.Clear();
        }

        if (!ClientBeamPoll.ShouldEnumerate(_lastBeamCount, ref _idleFrames)) return;

        // Entity keys accumulate across a session; these sets only suppress repeat log lines,
        // so a bounded reset is cheaper than tracking destroys in another world.
        if (_tinted.Count > SeenKeyCap) _tinted.Clear();
        if (_warnedDisabled.Count > SeenKeyCap) _warnedDisabled.Clear();
        if (_inspected.Count > SeenKeyCap) _inspected.Clear();
        if (_overwriteLogged.Count > SeenKeyCap) _overwriteLogged.Clear();
        if (_lastWritten.Count > SeenKeyCap) _lastWritten.Clear();

        if (!TryEnsureClientQuery(world)) return;

        var em = world.EntityManager;
        NativeArray<Entity> beams;
        try
        {
            beams = _clientQuery.ToEntityArray(Allocator.Temp);
        }
        catch (Exception e)
        {
            // R28 finding 28.4: this used to log every frame and leave _lastBeamCount = -1, so
            // ShouldEnumerate was true again immediately — a rebuild-and-throw loop at frame
            // rate with a warning line each time. Back off to the idle poll and log once.
            string? line = _queryFailureLog.Take("Effects: server beam client query threw: " + e.Message);
            if (line != null) Plugin.Logger?.LogWarning(line);
            ReleaseClientQuery();
            _lastBeamCount = ClientBeamPoll.FailureBeamCount;
            return;
        }

        try
        {
            _queryFailureLog.Clear();
            _lastBeamCount = beams.Length;
            int matched = 0;
            _tracker.BeginTick();
            for (int i = 0; i < beams.Length; i++)
            {
                var beam = beams[i];
                try
                {
                    if (!TryReadBlastTime(em, beam, out float blast)) continue;
                    // R31 finding 17: the production caller MarkerSchemesOverlap never had. A
                    // BlastTime both schemes claim used to resolve to MarkerCoil silently while
                    // docs/effects.md documented the overlap line as something an operator sees.
                    float bucket = _markerLog.Classify(
                        blast, out _, out string? legacyLine, out string? overlapLine);
                    if (bucket == 0f) continue;
                    matched++;
                    if (legacyLine != null) Plugin.Logger?.LogWarning(legacyLine);
                    if (overlapLine != null) Plugin.Logger?.LogWarning(overlapLine);
                    if (!TryReadSequenceEntity(em, beam, out var seq) || seq == Entity.Null)
                        continue;

                    long key = ClientBeamTracker.KeyOf(beam.Index, beam.Version);
                    RepointSequence(world, em, beam, seq, key);
                    TintOnce(world, em, beam, seq, blast, key);
                }
                catch (Exception e)
                {
                    Plugin.Logger?.LogWarning("Effects: server beam client tick threw: " + e.Message);
                }
            }

            // R27 finding 16: without this the whole client re-point can be a silent no-op —
            // Observe only ever logs when a beam matched, so a marker that did not survive the
            // wire produced no evidence at all.
            if (matched == 0 && beams.Length > 0 && !_noMarkerLogged)
            {
                _noMarkerLogged = true;
                Plugin.Logger?.LogInfo(ClientBeamPoll.NoMarkerLog(beams.Length));
            }

            _tracker.EndTick(_ended);
            for (int i = 0; i < _ended.Count; i++)
                Plugin.Logger?.LogInfo(_ended[i]);
        }
        finally
        {
            try { beams.Dispose(); }
            catch { /* */ }
        }
    }

    /// <summary>
    /// The world's sequence number, or <b>null</b> when it cannot be read (R28 finding 26).
    /// The old version caught to <c>0UL</c>, which collides with every other unreadable world
    /// and made the change detection a constant "unchanged". The unreadable case is logged
    /// once so the condition is visible rather than silently degrading into a stale query.
    /// </summary>
    private ulong? WorldSeq(World world)
    {
        try { return world.SequenceNumber; }
        catch
        {
            string? line = _worldSeqFailureLog.Take(ClientBeamPoll.UnreadableWorldSeqLog);
            if (line != null) Plugin.Logger?.LogWarning(line);
            return null;
        }
    }

    private bool TryEnsureClientQuery(World world)
    {
        if (_clientQueryBuilt) return true;
        try
        {
            _clientQuery = Query.Build(
                world.EntityManager,
                includeDisabled: true,
                includeSystems: false,
                ComponentIndex.Of<ProfessorCoilBeam_State_Shared>(ComponentType.AccessMode.ReadOnly));
            _clientQueryBuilt = true;
            _clientQueryWorldSeq = WorldSeq(world);
            _clientQueryWorld = world;
            _buildFailureLog.Clear();
            return true;
        }
        catch (Exception e)
        {
            // R28 finding 28.4: same shape as the enumeration failure — one line per episode,
            // and the idle poll rather than a rebuild attempt every frame.
            string? line = _buildFailureLog.Take("Effects: server beam client query build threw: " + e.Message);
            if (line != null) Plugin.Logger?.LogWarning(line);
            _lastBeamCount = ClientBeamPoll.FailureBeamCount;
            return false;
        }
    }

    /// <summary>
    /// Drop the cached query. R28 finding 26: <c>EntityQuery.Dispose</c> on a query whose world
    /// has already been destroyed reaches into freed native state, so the two paths that run
    /// <em>because</em> the world is gone or swapped pass <paramref name="disposeQuery"/> false
    /// and simply forget the handle — a destroyed world disposes its own queries.
    /// </summary>
    private void ReleaseClientQuery(bool disposeQuery = true)
    {
        if (!_clientQueryBuilt) return;
        var owner = _clientQueryWorld;
        var builtSeq = _clientQueryWorldSeq;
        _clientQueryBuilt = false;
        _clientQueryWorldSeq = null;
        _clientQueryWorld = null;
        if (!disposeQuery)
        {
            _clientQuery = default;
            return;
        }

        // Shutdown can run after the client world was destroyed but before a tick noticed it
        // (core-swap-crash-2026-10-02.md): dispose only while the owning world still lives.
        bool alive;
        try { alive = owner != null && owner.IsCreated; }
        catch { alive = false; }
        ulong? seqNow = null;
        if (alive)
        {
            try { seqNow = owner!.SequenceNumber; }
            catch { seqNow = null; }
        }
        if (WorldQueryPolicy.Release(true, builtSeq, alive, seqNow) != CachedQueryRelease.Dispose)
        {
            _clientQuery = default;
            return;
        }

        try { _clientQuery.Dispose(); }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Effects: server beam client query dispose threw: " + e.Message);
        }

        _clientQuery = default;
    }

    public void Shutdown()
    {
        ReleaseClientQuery();
        _lastBeamCount = -1;
        _idleFrames = 0;
        _queryFailureLog.Clear();
        _buildFailureLog.Clear();
        _worldSeqFailureLog.Clear();
        _tracker.Clear();
        _ended.Clear();
        _lastWritten.Clear();
    }

    /// <summary>
    /// Follow the replicated endpoints. The vanilla client system reads
    /// <c>State_Shared.Start/End</c> once, on the frame it enqueues the sequence spawn, and
    /// then only ever swaps Preview to Blast (client native
    /// <c>OriginalLambdaBody</c>: <c>if (SequenceState == Entity.Null) enqueue(Preview)</c>
    /// else <c>if (BeamState == Blast || Age &lt;= PreviewTime) return; enqueue(Blast)</c>).
    ///
    /// <para>EV14: the re-point channel is the sequence instance's own <b>blackboard</b>.
    /// Native <c>ProfessorCoilSystem_Client.OnUpdate</c> on the client
    /// places the beam with <c>SetBlackboardInput("Position", Start)</c> +
    /// <c>SetBlackboardInput("Rotation", LookRotation(End - Start, up))</c> after
    /// <c>PlaySequence</c> — the body itself is a fixed-length prefab picked by
    /// <c>GetSequence</c>'s length bucket, not a two-point line. EV13's
    /// <c>LineRendererPositionElement</c> write is kept for sequence families that really are
    /// LineRenderer-bodied; on the coil sequence that buffer does not exist, which is why the
    /// tracking line never appeared live (evidence 134). Neither path ever touches the pooled
    /// clone's renderers or materials (R26 finding 3).</para>
    /// </summary>
    private void RepointSequence(World world, EntityManager em, Entity beam, Entity seq, long key)
    {
        if (!TryReadEndpoints(em, beam, out var start, out var end))
            return;

        var start3 = new float3(start.X, start.Y, start.Z);
        var end3 = new float3(end.X, end.Y, end.Z);
        var path = BeamTrackPath.None;

        if (SequenceBlackboard.TryGetSlots(world, seq, out var slots))
        {
            // R27 finding 23: _lastWritten is keyed on the SEQUENCE, not the beam. The coil
            // client system destroys and respawns the sequence on a Preview->Blast swap, so a
            // beam-keyed readback compared our last write against a brand-new sequence's
            // registers and logged a spurious "overwritten between frames".
            long seqKey = ClientBeamTracker.KeyOf(seq.Index, seq.Version);
            LogBlackboardOnce(world, seq, in slots, start3);
            WarnIfSequencerOverwrote(world, seq, in slots, seqKey);
            if (SequenceBlackboard.TryWrite(world, seq, in slots, start3, SequenceBlackboard.LookAlong(start3, end3)))
            {
                path |= BeamTrackPath.Blackboard;
                _lastWritten[seqKey] = start3;
            }
        }

        if (UnityEffectRuntime.TryWriteLineRendererPositions(world, seq, in start, in end))
            path |= BeamTrackPath.Buffer;

        string line = beam.Index + ":" + beam.Version;
        string seqLine = seq.Index + ":" + seq.Version;
        string? log = _tracker.Observe(key, line, seqLine, path);
        if (log != null)
            Plugin.Logger?.LogInfo(log);
    }

    /// <summary>
    /// One line per sequence: which register offsets were resolved and what the game had put
    /// there, read back on the frame we first bind. Together with
    /// <see cref="WarnIfSequencerOverwrote"/> this is the EV5 / EV9 readback: if the value we
    /// wrote last frame is still there this frame, the sequencer is not fighting us.
    /// </summary>
    private void LogBlackboardOnce(World world, Entity seq, in SequenceBlackboard.Slots slots, float3 start)
    {
        long seqKey = ClientBeamTracker.KeyOf(seq.Index, seq.Version);
        if (!_inspected.Add(seqKey)) return;
        SequenceBlackboard.TryReadFloat3(world, seq, slots.Position, out var before);
        Plugin.Logger?.LogInfo(
            "Effects: server beam client blackboard sequence=" + seq.Index + ":" + seq.Version
            + " positionSlot=" + slots.Position + " rotationSlot=" + slots.Rotation
            + " game=" + Fmt(before) + " ours=" + Fmt(start));
    }

    private void WarnIfSequencerOverwrote(World world, Entity seq, in SequenceBlackboard.Slots slots, long seqKey)
    {
        if (!_lastWritten.TryGetValue(seqKey, out var ours)) return;
        if (!SequenceBlackboard.TryReadFloat3(world, seq, slots.Position, out var current)) return;
        if (current.x == ours.x && current.y == ours.y && current.z == ours.z) return;
        if (!_overwriteLogged.Add(seqKey)) return;
        Plugin.Logger?.LogInfo(
            "Effects: server beam client blackboard overwritten between frames sequence="
            + seq.Index + ":" + seq.Version + " ours=" + Fmt(ours) + " game=" + Fmt(current));
    }

    private static string Fmt(float3 v) =>
        "(" + v.x.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)
        + "," + v.y.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)
        + "," + v.z.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + ")";

    private void TintOnce(World world, EntityManager em, Entity beam, Entity seq, float blast, long key)
    {
        if (!BeamPresets.IsTintMarker(blast)) return;
        var color = BeamPresets.TintForMarker(blast);
        if (color == null) return;
        if (_tinted.Contains(key)) return;

        var go = SequenceDriverLookup.FromSequenceEntity(world, seq);
        if (go == null) return;

        // R26 finding 3: this GameObject came from ParticlePool.GetInstance.
        // Writing its renderers/materials leaks the tint —
        // and, on Release, a destroyed material — into every later user of the clone,
        // vanilla beams included. The marker is recorded so the coil keeps its baked
        // look; a recolored server beam needs its own body, not a write to the pool.
        UnityEffectRuntime.TintGameObject(
            go, color.Value, instantiated: null, owned: false, kind: AssetKind.Sequence);
        _tinted.Add(key);
        string tag = BeamPresets.MarkerBucket(blast) == BeamPresets.TintMarkerRed ? "red" : "blue";
        string line = "Effects: server beam client tint " + tag + " skipped (pooled clone is read-only)"
            + " entity=" + beam.Index + ":" + beam.Version;
        if (_loggedTint.Add(line))
            Plugin.Logger?.LogInfo(line);
        _ = em;
    }

    private bool TryResolvePrefab(
        World world,
        EntityManager em,
        string prefabName,
        int prefabGuid,
        out Entity prefab,
        out string resolvedName,
        out PrefabGUID resolvedGuid)
    {
        prefab = Entity.Null;
        resolvedName = prefabName ?? "";
        resolvedGuid = default;

        if (prefabGuid != 0)
        {
            resolvedGuid = new PrefabGUID(prefabGuid);
            if (TryMap(world, resolvedGuid, out prefab, out resolvedName))
                return true;
        }

        if (!string.IsNullOrEmpty(prefabName)
            && !int.TryParse(prefabName, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out _))
        {
            try
            {
                if (PrefabLookup.TryGetGuidByName(world, prefabName, out var byName)
                    && TryMap(world, byName, out prefab, out resolvedName))
                {
                    resolvedGuid = byName;
                    return true;
                }
            }
            catch (Exception e)
            {
                Plugin.Logger?.LogWarning("Effects: coil beam TryGetGuidByName '" + prefabName + "' threw: " + e.Message);
            }
        }

        for (int i = 0; i < BeamPresets.CoilPrefabNames.Length; i++)
        {
            string n = BeamPresets.CoilPrefabNames[i];
            try
            {
                if (PrefabLookup.TryGetGuidByName(world, n, out var g)
                    && TryMap(world, g, out prefab, out resolvedName))
                {
                    resolvedGuid = g;
                    return true;
                }
            }
            catch { /* next name */ }
        }

        if (TryQueryCoilPrefab(em, world, out prefab, out resolvedName, out resolvedGuid))
            return true;

        LastMissReason = "Effects: no coil beam prefab on this server; returning a disabled handle.";
        prefab = Entity.Null;
        return false;
    }

    private static bool TryMap(World world, PrefabGUID guid, out Entity prefab, out string name)
    {
        prefab = Entity.Null;
        name = "";
        if (!PrefabLookup.TryGetEntity(world, guid, out prefab) || prefab == Entity.Null)
            return false;
        if (PrefabLookup.TryGetName(world, guid, out var found) && !string.IsNullOrEmpty(found))
            name = found;
        else
            name = guid._Value.ToString();
        return true;
    }

    private static bool TryQueryCoilPrefab(
        EntityManager em,
        World world,
        out Entity prefab,
        out string name,
        out PrefabGUID guid)
    {
        prefab = Entity.Null;
        name = "";
        guid = default;
        try
        {
            var list = Query.Entities(
                em,
                includeDisabled: true,
                includeSystems: false,
                ComponentIndex.Of<Unity.Entities.Prefab>(ComponentType.AccessMode.ReadOnly),
                ComponentIndex.Of<ProfessorCoilBeam_Data_Server>(ComponentType.AccessMode.ReadOnly));
            for (int i = 0; i < list.Count; i++)
            {
                var e = list[i];
                if (!em.Exists(e)) continue;
                prefab = e;
                try
                {
                    if (e.Has<PrefabGUID>(em))
                    {
                        guid = em.GetComponentData<PrefabGUID>(e);
                        if (PrefabLookup.TryGetName(world, guid, out var n) && !string.IsNullOrEmpty(n))
                            name = n;
                        else
                            name = guid._Value.ToString();
                    }
                }
                catch
                {
                    name = e.Index + ":" + e.Version;
                }

                if (string.IsNullOrEmpty(name))
                    name = "ProfessorCoilBeam";
                return true;
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Effects: coil beam prefab query threw: " + e.Message);
        }

        return false;
    }

    private static Entity CreateDummyEvent(EntityManager em, PrefabGUID beamGuid, float previewTime, float blastTime, float3 pos)
    {
        Entity evt = Entity.Null;
        try { evt = em.CreateEntity(); }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Effects: dummy coil event CreateEntity threw: " + e.Message);
            return Entity.Null;
        }

        try
        {
            evt.Add<ProfessorCoilEvent>(em);
            evt.Add<Age>(em);
            if (!evt.Has<Translation>(em))
                evt.Add<Translation>(em);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Effects: dummy coil event Add threw: " + e.Message);
            TryDespawn(em, evt);
            return Entity.Null;
        }

        TryWriteTranslation(em, evt, pos);
        TryWriteAge(em, evt, 0f);
        try
        {
            var typeIndex = ComponentIndex.IndexOf<ProfessorCoilEvent>();
            if (!SlotOk(typeIndex, 0x1C, "ProfessorCoilEvent"))
                return evt;
            void* raw = em.GetComponentDataRawRW(evt, typeIndex);
            if (raw == null) return evt;
            var p = (byte*)raw;
            *(Entity*)p = Entity.Null;
            *(float*)(p + 8) = previewTime;
            *(float*)(p + 12) = blastTime;
            *(float*)(p + 16) = 1e10f;
            *(int*)(p + 20) = beamGuid._Value;
            *(int*)(p + 24) = 0;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Effects: dummy coil event overlay threw: " + e.Message);
        }

        return evt;
    }

    private static bool TryWriteShared(
        EntityManager em,
        Entity beam,
        float3 start,
        float3 end,
        float previewTime,
        float blastTime)
    {
        try
        {
            if (!beam.Has<ProfessorCoilBeam_State_Shared>(em))
                beam.Add<ProfessorCoilBeam_State_Shared>(em);
            var typeIndex = ComponentIndex.IndexOf<ProfessorCoilBeam_State_Shared>();
            if (!SlotOk(typeIndex, SharedMinSlot, "ProfessorCoilBeam_State_Shared"))
                return false;
            void* raw = em.GetComponentDataRawRW(beam, typeIndex);
            if (raw == null) return false;
            var p = (byte*)raw;
            *(float3*)(p + SharedStart) = start;
            *(float3*)(p + SharedEnd) = end;
            if (previewTime >= 0f)
                *(float*)(p + SharedPreview) = previewTime;
            if (blastTime >= 0f)
                *(float*)(p + SharedBlast) = blastTime;
            return true;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Effects: State_Shared overlay refused: " + e.Message);
            return false;
        }
    }

    private static bool TryWriteServerState(EntityManager em, Entity beam, Entity dummyEvent)
    {
        try
        {
            if (!beam.Has<ProfessorCoilBeam_State_Server>(em))
                beam.Add<ProfessorCoilBeam_State_Server>(em);
            var typeIndex = ComponentIndex.IndexOf<ProfessorCoilBeam_State_Server>();
            if (!SlotOk(typeIndex, ServerMinSlot, "ProfessorCoilBeam_State_Server"))
                return false;
            void* raw = em.GetComponentDataRawRW(beam, typeIndex);
            if (raw == null) return false;
            var p = (byte*)raw;
            *(Entity*)(p + ServerEvent) = dummyEvent;
            *(int*)(p + ServerCoil0) = 0;
            *(int*)(p + ServerCoil1) = 1;
            *(int*)(p + ServerState) = 0;
            return true;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Effects: State_Server overlay refused: " + e.Message);
            return false;
        }
    }

    private static bool TryReadBlastTime(EntityManager em, Entity beam, out float blast)
    {
        blast = 0f;
        try
        {
            if (!beam.Has<ProfessorCoilBeam_State_Shared>(em)) return false;
            var typeIndex = ComponentIndex.IndexOf<ProfessorCoilBeam_State_Shared>();
            void* raw = em.GetComponentDataRawRO(beam, typeIndex);
            if (raw == null) return false;
            blast = *(float*)((byte*)raw + SharedBlast);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Read the synced <c>Start</c>/<c>End</c> back off a replicated beam.</summary>
    private static bool TryReadEndpoints(EntityManager em, Entity beam, out EffectVec3 start, out EffectVec3 end)
    {
        start = default;
        end = default;
        try
        {
            if (!beam.Has<ProfessorCoilBeam_State_Shared>(em)) return false;
            var typeIndex = ComponentIndex.IndexOf<ProfessorCoilBeam_State_Shared>();
            if (!SlotOk(typeIndex, SharedMinSlot, "ProfessorCoilBeam_State_Shared"))
                return false;
            void* raw = em.GetComponentDataRawRO(beam, typeIndex);
            if (raw == null) return false;
            var p = (byte*)raw;
            var s = *(float3*)(p + SharedStart);
            var e = *(float3*)(p + SharedEnd);
            start = new EffectVec3(s.x, s.y, s.z);
            end = new EffectVec3(e.x, e.y, e.z);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// A real beam <c>SequenceGUID</c> the server can see. <c>BuffSequence</c>, which holds the
    /// channeling beams' GUIDs, is <c>[ComponentInWorld(4)]</c> — client world only —
    /// so the charm / subdue / feed beams cannot be named from here.
    /// <c>Script_WarEvent_PortalToGateSequenceData</c> is <c>[ComponentInWorld(2, 4)]</c>
    /// and its <c>SequenceGuid</c> is the war-event portal-to-gate beam
    /// (`Part_WarEvent_ShadowRift_Beam` family, a LineRenderer-bodied `Shader Graphs/Effect`
    /// line — evidence 130), so that is the default. Searched once per runtime instance.
    /// </summary>
    private int ResolveLinkSequenceGuid(EntityManager em, int requested)
    {
        if (requested != 0) return requested;
        if (_linkSequenceSearched) return _linkSequenceGuid;
        _linkSequenceSearched = true;
        try
        {
            var list = Query.Entities(
                em,
                includeDisabled: true,
                includeSystems: false,
                ComponentIndex.Of<ProjectM.Shared.WarEvents.Script_WarEvent_PortalToGateSequenceData>(
                    ComponentType.AccessMode.ReadOnly));
            var typeIndex = ComponentIndex.IndexOf<ProjectM.Shared.WarEvents.Script_WarEvent_PortalToGateSequenceData>();
            if (!SlotOk(typeIndex, WarEventMinSlot, "Script_WarEvent_PortalToGateSequenceData"))
                return 0;
            for (int i = 0; i < list.Count; i++)
            {
                var e = list[i];
                if (!em.Exists(e)) continue;
                void* raw = em.GetComponentDataRawRO(e, typeIndex);
                if (raw == null) continue;
                int guid = *(int*)((byte*)raw + WarEventSequenceGuid);
                if (guid == 0) continue;
                _linkSequenceGuid = guid;
                Plugin.Logger?.LogInfo(
                    "Effects: server beam link sequence " + guid
                    + " from Script_WarEvent_PortalToGateSequenceData on " + e.Index + ":" + e.Version);
                return guid;
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Effects: link sequence lookup threw: " + e.Message);
        }

        return 0;
    }

    private static bool TryResolveNamedPrefab(
        World world,
        EntityManager em,
        string name,
        int guidHash,
        out Entity prefab,
        out string resolvedName)
    {
        prefab = Entity.Null;
        resolvedName = name;
        try
        {
            if (PrefabLookup.TryGetGuidByName(world, name, out var byName)
                && TryMap(world, byName, out prefab, out resolvedName))
                return true;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Effects: link prefab TryGetGuidByName threw: " + e.Message);
        }

        try
        {
            if (guidHash != 0 && TryMap(world, new PrefabGUID(guidHash), out prefab, out resolvedName))
                return true;
        }
        catch { /* miss */ }

        _ = em;
        prefab = Entity.Null;
        return false;
    }

    /// <summary>
    /// Fill <c>SpawnSequenceForEntity</c>: sequence GUID plus both endpoints as
    /// <c>NetworkedEntity</c> (<c>Entity _Entity</c> @0, <c>bool _WaitingForSync</c> @8;
    /// a server-side entity is not waiting for a sync).
    /// </summary>
    private static bool TryWriteSpawnSequenceForEntity(
        EntityManager em,
        Entity link,
        int sequenceGuid,
        Entity a,
        Entity b,
        bool playOnSelf)
    {
        try
        {
            if (!link.Has<ProjectM.Sequencer.SpawnSequenceForEntity>(em))
                link.Add<ProjectM.Sequencer.SpawnSequenceForEntity>(em);
            var typeIndex = ComponentIndex.IndexOf<ProjectM.Sequencer.SpawnSequenceForEntity>();
            if (!SlotOk(typeIndex, LinkMinSlot, "SpawnSequenceForEntity"))
                return false;
            void* raw = em.GetComponentDataRawRW(link, typeIndex);
            if (raw == null) return false;
            var p = (byte*)raw;
            *(int*)(p + LinkSequenceGuid) = sequenceGuid;
            *(Entity*)(p + LinkTarget) = a;
            *(byte*)(p + LinkTarget + 8) = 0;
            *(Entity*)(p + LinkSecondaryTarget) = b;
            *(byte*)(p + LinkSecondaryTarget + 8) = 0;
            *(byte*)(p + LinkPlayOnSelf) = playOnSelf ? (byte)1 : (byte)0;
            *(float*)(p + LinkScale) = 1f;
            return true;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Effects: SpawnSequenceForEntity overlay refused: " + e.Message);
            return false;
        }
    }

    private static bool TryReadTranslationEntity(EntityManager em, Entity entity, out float3 position)
    {
        position = default;
        try
        {
            if (!entity.Has<Translation>(em)) return false;
            position = em.GetComponentData<Translation>(entity).Value;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static float3 Midpoint(float3 a, float3 b) =>
        new((a.x + b.x) * 0.5f, (a.y + b.y) * 0.5f, (a.z + b.z) * 0.5f);

    /// <summary>
    /// Take the spawned beam out of the no-players-in-range gate. The coil beam prefab ships
    /// <c>DisableWhenNoPlayersInRange</c> (live-read on the prefab), and
    /// <c>DisableWhenNoPlayersInRangeSystem_Server</c> (<c>FinalizeGroup_Server</c>)
    /// adds <c>Disabled</c> + <c>DisabledDueToNoPlayersInRange</c> for a whole
    /// terrain chunk. A disabled server entity drops out of <c>PrioritizeSystem</c>'s queries,
    /// so it silently stops replicating and every client times it out three seconds later.
    /// A beam is short-lived and explicitly owned by a mod, so the gate is simply removed.
    /// </summary>
    private static void TryUngateNoPlayersInRange(EntityManager em, Entity beam)
    {
        try
        {
            if (beam.Has<DisableWhenNoPlayersInRange>(em))
                beam.Remove<DisableWhenNoPlayersInRange>(em);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Effects: server beam DisableWhenNoPlayersInRange remove threw: " + e.Message);
        }
    }

    /// <summary>
    /// One warning per beam if something disabled it server-side anyway: a disabled entity is
    /// invisible to <c>PrioritizeSystem</c>, so nothing further reaches any client.
    /// </summary>
    private void WarnIfDisabled(EntityManager em, Entity beam)
    {
        try
        {
            if (!beam.Has<Unity.Entities.Disabled>(em)) return;
            long key = ((long)beam.Index << 32) ^ (uint)beam.Version;
            if (!_warnedDisabled.Add(key)) return;
            Plugin.Logger?.LogWarning(
                "Effects: server beam " + beam.Index + ":" + beam.Version
                + " is Disabled on the server; it will stop replicating and clients time it out after "
                + BeamPresets.NetworkedTimeoutSeconds.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "s.");
        }
        catch { /* diagnostic only */ }
    }

    private static bool TryReadSequenceEntity(EntityManager em, Entity beam, out Entity seq)
    {
        seq = Entity.Null;
        try
        {
            if (!beam.Has<ProfessorCoilBeam_Client>(em)) return false;
            var typeIndex = ComponentIndex.IndexOf<ProfessorCoilBeam_Client>();
            if (!SlotOk(typeIndex, ClientMinSlot, "ProfessorCoilBeam_Client"))
                return false;
            void* raw = em.GetComponentDataRawRO(beam, typeIndex);
            if (raw == null) return false;
            seq = *(Entity*)((byte*)raw + ClientSequenceState);
            return seq != Entity.Null;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryWriteTranslation(EntityManager em, Entity entity, float3 pos)
    {
        try
        {
            var type = ComponentIndex.Of<Translation>(ComponentType.AccessMode.ReadWrite);
            if (!em.HasComponent(entity, type))
                entity.Add<Translation>(em);
            var t = em.GetComponentData<Translation>(entity);
            t.Value = pos;
            em.SetComponentData(entity, t);
            return true;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Effects: server beam Translation write failed: " + e.Message);
            return false;
        }
    }

    private static bool TryWriteAge(EntityManager em, Entity entity, float value)
    {
        try
        {
            if (!entity.Has<Age>(em))
                entity.Add<Age>(em);
            var typeIndex = ComponentIndex.IndexOf<Age>();
            void* raw = em.GetComponentDataRawRW(entity, typeIndex);
            if (raw == null) return false;
            *(float*)raw = value;
            return true;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Effects: Age overlay refused: " + e.Message);
            return false;
        }
    }

    private static bool TryWriteLifeTime(EntityManager em, Entity entity, float seconds)
    {
        try
        {
            if (!entity.Has<LifeTime>(em)) return true;
            var typeIndex = ComponentIndex.IndexOf<LifeTime>();
            void* raw = em.GetComponentDataRawRW(entity, typeIndex);
            if (raw == null) return true;
            float duration = seconds;
            if (duration < 0.05f) duration = 0.05f;
            if (duration > BeamPresets.BlastTimeMax) duration = BeamPresets.BlastTimeMax;
            *(float*)raw = duration;
            *(int*)((byte*)raw + 4) = (int)LifeTimeEndAction.Destroy;
            return true;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Effects: LifeTime overlay refused: " + e.Message);
            return true;
        }
    }

    /// <summary>
    /// The only other place Effects reads a size off <c>TypeManager</c>, checked in EV17's
    /// second pass for the trap that produced the live <c>path=none</c> regression. It is
    /// already the right posture and is left alone: <c>SizeInChunk</c> is <b>never</b> used as
    /// a stride or an offset here — the overlay's offsets come from the dump — it only refuses
    /// when the reported value is nonzero <em>and smaller</em> than the slot about to be
    /// written, it proceeds on 0 (trap 7), a garbage-large reading passes, and the refusal is
    /// per call rather than a latching gate. The residual is a garbage-small nonzero reading
    /// refusing a legal overlay, which costs one write and one log line, not the module.
    /// </summary>
    private static bool SlotOk(TypeIndex typeIndex, int minSlot, string typeName)
    {
        int sizeInChunk = 0;
        try { sizeInChunk = TypeManager.GetTypeInfo(typeIndex).SizeInChunk; }
        catch { /* TypeSize/SizeInChunk can read garbage; overlay still proceeds when 0. */ }
        if (sizeInChunk > 0 && sizeInChunk < minSlot)
        {
            Plugin.Logger?.LogWarning(
                "Effects: " + typeName + " overlay refused: SizeInChunk=" + sizeInChunk + " < " + minSlot + ".");
            return false;
        }

        return true;
    }

    private static bool TryWorld(out EntityManager em)
    {
        em = default;
        var world = Worlds.Server;
        if (world == null || !world.IsCreated) return false;
        em = world.EntityManager;
        return true;
    }

    private static bool TryAlive(EntityManager em, ServerBeamId id, out Entity entity)
    {
        entity = id.ToEntity();
        try { return entity != Entity.Null && em.Exists(entity); }
        catch { return false; }
    }

    private static void TryDespawn(EntityManager em, Entity entity)
    {
        if (entity == Entity.Null) return;
        try
        {
            DestroyUtility.Destroy(em, entity, DestroyDebugReason.None, callerFilePath: null, callerLineNumber: 0);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Effects: DestroyUtility.Destroy threw: " + e.Message);
        }

        try
        {
            if (em.Exists(entity) && !entity.Has<DestroyTag>(em))
                entity.Add<DestroyTag>(em);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Effects: DestroyTag add threw: " + e.Message);
            try { em.DestroyEntity(entity); }
            catch (Exception e2) { Plugin.Logger?.LogWarning("Effects: DestroyEntity threw: " + e2.Message); }
        }
    }
}
#endif
