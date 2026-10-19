#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using BepInEx.Configuration;
using Progenitor.Config;
using Progenitor.Ecs;
using Progenitor.Events;
using Progenitor.Networking;
using Progenitor.Players;
using Progenitor.Terrain;
using Progenitor.Ui;
using ProjectM.Network;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using static Progenitor.Debugging.DebugMapPolicy;

namespace Progenitor.Debugging;

/// <summary>
/// The debug buff's map powers (MAPTP1, moved from the Sample into Core with the buff). While a
/// player wears <c>progenitor.debug</c> (<c>.debug on</c>):
/// <list type="bullet">
/// <item><b>The map is revealed</b> on their client: the game's own debug "Reveal Map" switch
/// (<c>RevealMapPresentationSystem.RevealFullMap</c>) through <see cref="WorldMap.RevealAll"/>. It changes
/// what the map draws, never the exploration the server stores, so taking the buff off (or dying with it
/// removed, a Core swap, a disconnect) restores the player's own exploration by construction:
/// nothing was written. There is therefore no snapshot to persist.</item>
/// <item><b>Teleporting on the map.</b> Progenitor's map-scope hold <c>TeleportToMapPoint</c> defaults to
/// Ctrl+Shift+left click (decided 2026-10-18; it was unbound from 2026-10-06), the same chord as the game's own
/// admin map teleport (<c>MapMenuMapper.OnMapClicked</c> sends <c>PlayerTeleportDebugEvent</c>, which the server
/// accepts only from an <c>AdminUser</c> with <c>Level</c> Admin and <c>AuthMethod</c> Authenticated, or with
/// <c>EveryoneIsAdmin</c>). The keybind rules exempt this one bind from the map's Ctrl+Shift+left click refusal
/// (<see cref="MouseChords.IsGameTeleportTwin"/>). Holding it over the open map fills a ring around the cursor
/// over <c>[DebugMap] HoldSeconds</c> (1 s). When it completes, the point under the cursor <b>at that
/// moment</b> goes to the server. Releasing early, moving off the map or closing the map cancels and sends
/// nothing. The map stays open afterwards, as the game's own admin map teleport leaves it.
/// <b>One teleport per gesture:</b> the server's state says whether the wearer is game-authenticated
/// (<c>User.IsAdmin</c>, set by <c>adminauth</c>); then the game's own handler teleports on the click, and the client
/// does not start its hold while the key is the game's chord (<see cref="SkipHold"/>). Under the server's
/// <c>EveryoneIsAdmin</c> the flag is not set, so such a player gets the game's teleport and, a second later,
/// Progenitor's to the same spot.</item>
/// <item><b>The server</b> re-checks admin (the admin list, <c>adminauth</c> or the private-game host), character and buff, finds the nearest valid spot within
/// <c>[DebugMap] SearchRadius</c> (100 m) — ground under the point, on the world grid, and with
/// <c>[DebugMap] WalkableCheck</c> (default off, not live-verified) a standable pathfinding tile. Inside castle
/// territory the ground read walks down the game's 5 m height levels to the castle's ground floor instead of taking
/// the top one, which can be a roof (<see cref="DebugMapPolicy.PickFloor"/>, <see cref="Progenitor.Terrain.GroundProbeLevels"/>).
/// It teleports with <c>PlayerTeleport.TeleportVerified</c> over the floors under the chosen spot: the game's helper
/// with a read-back a second later, then the helper on each lower floor, and the Dev bridge op's guarded position
/// write only onto a known walkable floor (<see cref="DebugMapPolicy.WriteAllowed"/>), never a roof. Then it answers with
/// a reply event and a chat line: "Teleported" or "Teleported (moved N m to the nearest valid spot)" only when a
/// read-back found the character there, "Couldn't teleport there: the game refused that spot." when none did, or the
/// refusal.</item>
/// </list>
/// <c>.debug tp &lt;x&gt; &lt;z&gt;</c> runs the same server path from chat (or the Dev bridge's <c>command</c>
/// op), and <c>.debug status</c> reports both powers. The four messages use Core's reserved event range.
/// </summary>
internal static unsafe class DebugMap
{
    internal const string Section = "DebugMap";
    internal const string KeybindId = TeleportKeybindId;
    private const string KeybindText = "Teleport to map point";
    private const int PushPeriodFrames = 30;

    /// <summary>
    /// An "on" state is sent again this often (frames): a state pushed while the client was still loading
    /// its world can be lost, and the client ignores a repeat. "Off" is not repeated.
    /// </summary>
    private const int ResendFrames = 600;

    private static ConfigEntry<float>? _holdSeconds;
    private static ConfigEntry<float>? _radius;
    private static ConfigEntry<float>? _step;
    private static ConfigEntry<bool>? _walkCheck;

    internal static float Radius => ClampRadius(_radius?.Value ?? DefaultRadius);

    internal static float Step => ClampStep(_step?.Value ?? DefaultStep);

    internal static bool WalkCheck => _walkCheck?.Value ?? false;

    /// <summary>Install this side's half. Every handle goes into <paramref name="registrations"/>.</summary>
    internal static void Attach(List<IDisposable> registrations)
    {
        if (registrations == null) throw new ArgumentNullException(nameof(registrations));
        try
        {
            if (Plugin.IsServerProcess)
            {
                SharedConfig.BindWhenAvailable("[" + Section + "]", file =>
                {
                    _radius = ConfigHelper.Bind(file, Section, "SearchRadius", DefaultRadius,
                        "Server: how far (meters) a debug-buff map teleport may move you to reach a valid spot. "
                        + "0 = only the exact point. Clamped to 0..250.");
                    _step = ConfigHelper.Bind(file, Section, "SearchStep", DefaultStep,
                        "Server: spacing (meters) of the rings the nearest-valid-spot search walks. Clamped to 0.5..10.");
                    _walkCheck = ConfigHelper.Bind(file, Section, "WalkableCheck", false,
                        "Server: also ask the game's pathfinder whether the spot is standable (rejects walls, rocks and deep "
                        + "water). Off by default: PathfindingUtility.TryFindValidTile has not been verified in game yet.");
                });

                CustomEvents.OnReceiveFromClient(RequestEventId, OnRequest);
                registrations.Add(new Disposer(() => CustomEvents.RemoveClientHandler(RequestEventId, OnRequest)));
                CustomEvents.OnReceiveFromClient(QueryEventId, OnQuery);
                registrations.Add(new Disposer(() => CustomEvents.RemoveClientHandler(QueryEventId, OnQuery)));
                registrations.Add(Progenitor.Systems.VirtualSystems.Add("progenitor.debug.map", PushStates, DebugTools.Owner));
                registrations.Add(EventBus.On<PlayerDisconnected>(ev => Sent.Remove(ev.UserEntity), owner: DebugTools.Owner));
                registrations.Add(new Disposer(() =>
                {
                    Sent.Clear();
                    _radius = null;
                    _step = null;
                    _walkCheck = null;
                }));
                Log("server: map reveal and teleport for " + DebugBuff.Id + " wearers.");
                return;
            }

            Client.Attach(registrations);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("Debug map attach threw: " + e);
        }
    }

    // ==== server ===================================================================================

    /// <summary>What each connected user was last sent, and on which frame. Keyed by user entity, cleared on disconnect.</summary>
    private static readonly Dictionary<Entity, (DebugMapState State, int Frame)> Sent = new();

    private static int _frame;

    /// <summary>
    /// The server's one admin rule (<see cref="Progenitor.Install.ServerAdminList.AuthorizeShared"/>): on the admin list,
    /// authenticated with <c>adminauth</c>, or the host of this private game. Remote installs and server settings use the
    /// same rule. The game's own <c>User.IsAdmin</c> alone would refuse a listed admin until they ran <c>adminauth</c>
    /// again after every server restart (2026-10-08).
    /// </summary>
    private static bool IsAdmin(User user) =>
        Progenitor.Install.ServerAdminList.AuthorizeShared(user.PlatformId, user.IsAdmin) != Progenitor.Install.RemoteAuthorizedBy.None;

    /// <summary>The state a character's user would be sent now (also <c>.debug status</c>).</summary>
    internal static DebugMapState CurrentState(EntityManager em, Entity userEntity, Entity character)
    {
        bool isAdmin = false;
        bool gameAuthenticated = false;
        try
        {
            var u = em.GetComponentData<User>(userEntity);
            isAdmin = IsAdmin(u);
            gameAuthenticated = u.IsAdmin;
        }
        catch { isAdmin = false; }
        bool has = character != Entity.Null && em.Exists(character);
        return StateFor(has, has && DebugBuff.HoldsBuff(em, character), isAdmin, gameAuthenticated);
    }

    /// <summary>Whether <paramref name="userEntity"/>'s client was sent <paramref name="state"/> already.</summary>
    internal static bool WasSent(Entity userEntity, DebugMapState state) =>
        Sent.TryGetValue(userEntity, out var last) && last.State == state;

    private static bool Due(Entity userEntity, DebugMapState state)
    {
        if (!Sent.TryGetValue(userEntity, out var last) || last.State != state) return true;
        return state != DebugMapState.None && _frame - last.Frame >= ResendFrames;
    }

    /// <summary>Push now for one user (<c>.debug on</c>), instead of waiting up to half a second.</summary>
    internal static void PushNow(World world, Entity userEntity, Entity character)
    {
        try
        {
            var em = world.EntityManager;
            var state = CurrentState(em, userEntity, character);
            Send(world, userEntity, state, "command");
        }
        catch (Exception e)
        {
            Log("push threw: " + e.Message);
        }
    }

    /// <summary>
    /// <c>.debug off</c>: tell the client the powers are gone now. The buff entity is destroyed at the end of
    /// the frame, so a fresh read here could still see it; the periodic push confirms from the live state after.
    /// </summary>
    internal static void PushOff(World world, Entity userEntity)
    {
        try { Send(world, userEntity, DebugMapState.None, "command"); }
        catch (Exception e) { Log("push threw: " + e.Message); }
    }

    private static void PushStates(Progenitor.Systems.VirtualSystemContext vs)
    {
        if (vs.Frame % PushPeriodFrames != 0) return;
        _frame = vs.Frame;
        try
        {
            var world = Worlds.Server;
            if (world == null || !world.IsCreated) return;
            var em = vs.EntityManager;
            var users = Query.Entities(em, includeDisabled: false, includeSystems: false,
                ComponentIndex.Of<User>(ComponentType.AccessMode.ReadOnly));
            for (int i = 0; i < users.Count; i++)
            {
                var userEntity = users[i];
                User user;
                try { user = em.GetComponentData<User>(userEntity); }
                catch { continue; }
                if (!user.IsConnected) continue;
                var state = CurrentState(em, userEntity, user.LocalCharacter._Entity);
                if (!Due(userEntity, state)) continue;
                Send(world, userEntity, state, WasSent(userEntity, state) ? "repeat" : "watch");
            }
        }
        catch (Exception e)
        {
            Log("state push threw: " + e.Message);
        }
    }

    private static void Send(World world, Entity userEntity, DebugMapState state, string why)
    {
        if (CustomEvents.SendToUser(world, userEntity, StateEventId, StateMsg.For(state)))
        {
            bool repeat = WasSent(userEntity, state);
            Sent[userEntity] = (state, _frame);
            if (repeat) return;
            Log("state user=" + userEntity.Index + ":" + userEntity.Version + " reveal=" + state.Reveal
                + " teleport=" + state.CanTeleport + " gameTeleports=" + state.GameTeleports + " (" + why + ")");
        }
    }

    /// <summary>A client entered a world or loaded Core: answer with its state now.</summary>
    private static unsafe void OnQuery(int eventId, Entity userEntity, User user, byte* payload, int length)
    {
        try
        {
            if (eventId != QueryEventId) return;
            if (!CustomEvents.TryRead<QueryMsg>(payload, length, out var query) || query.Magic != Magic) return;
            var world = Worlds.Server;
            if (world == null || !world.IsCreated) return;
            var em = world.EntityManager;
            var current = em.GetComponentData<User>(userEntity);
            Send(world, userEntity, CurrentState(em, userEntity, current.LocalCharacter._Entity), "query");
        }
        catch (Exception e)
        {
            Log("query threw: " + e.Message);
        }
    }

    private static unsafe void OnRequest(int eventId, Entity userEntity, User user, byte* payload, int length)
    {
        try
        {
            if (eventId != RequestEventId) return;
            if (!CustomEvents.TryRead<RequestMsg>(payload, length, out var request) || request.Magic != Magic) return;
            var world = Worlds.Server;
            if (world == null || !world.IsCreated) return;
            Teleport(world, userEntity, request.X, request.Z, "map", outcome =>
            {
                try
                {
                    if (!world.IsCreated) return;
                    var reply = new ReplyMsg
                    {
                        Magic = Magic,
                        Nonce = request.Nonce,
                        Code = (int)outcome.Result,
                        RequestedX = request.X,
                        RequestedZ = request.Z,
                        X = outcome.Spot.X,
                        Y = outcome.Spot.Y,
                        Z = outcome.Spot.Z,
                        Moved = outcome.Spot.Distance,
                    };
                    CustomEvents.SendToUser(world, userEntity, ReplyEventId, reply);
                    Progenitor.Chat.Chat.SendTo(world, userEntity, Notice(outcome.Result, outcome.Spot.Distance, Radius));
                }
                catch (Exception e)
                {
                    Plugin.Logger?.LogError("Debug map reply threw: " + e);
                }
            });
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("Debug map request threw: " + e);
        }
    }

    /// <summary>One teleport's outcome.</summary>
    internal readonly record struct Outcome(Result Result, Spot Spot, string Method, string Line);

    /// <summary>
    /// The server path both the map request and <c>.debug tp</c> run: re-check, search, teleport, read back, log.
    /// <paramref name="via"/> is <c>map</c> or <c>command</c>, for the log line. <paramref name="done"/> runs once on
    /// the main thread: at once for a refusal before the move, else after the read-back
    /// (<see cref="PlayerTeleport.TeleportVerified"/>, one or two seconds). A Core unload in between drops it.
    /// </summary>
    internal static void Teleport(World world, Entity userEntity, float x, float z, string via, Action<Outcome> done)
    {
        var em = world.EntityManager;
        var none = new Spot(false, x, float.NaN, z, 0f, 0, "");
        string who = "user " + userEntity.Index + ":" + userEntity.Version;
        User user;
        try
        {
            user = em.GetComponentData<User>(userEntity);
            who = user.CharacterName.ToString();
        }
        catch
        {
            done(Finish(who, Result.NoCharacter, x, z, none, "", via));
            return;
        }

        var character = user.LocalCharacter._Entity;
        bool hasCharacter = character != Entity.Null && em.Exists(character);
        var refusal = Gate(IsAdmin(user), hasCharacter, hasCharacter && DebugBuff.HoldsBuff(em, character));
        if (refusal.HasValue)
        {
            done(Finish(who, refusal.Value, x, z, none, "", via));
            return;
        }

        if (!IsUsablePoint(x, z))
        {
            done(Finish(who, Result.BadPoint, x, z, none, "", via));
            return;
        }

        Spot spot;
        bool walkRan;
        var territory = CastleBand(x, z);
        bool castle = territory.Castle;
        var band = territory.Band;
        int reads = 0;
        Walk at;
        using (var ground = GroundProbe.TryOpen(world, out string why))
        {
            if (ground == null)
            {
                done(Finish(who, Result.NoGround, x, z, none with { Why = why }, "", via));
                return;
            }

            walkRan = WalkCheck;
            spot = FindNearestValid(x, z, Radius, Step, (px, pz) =>
            {
                // Inside castle territory the top layer can be a roof the game will not teleport onto: walk down the
                // height levels to the ground floor (PickFloor). Outside, the single read from ProbeHeight, as before.
                var floor = PickFloor(probeY => ground.TryGetHeight(px, probeY, pz, out float h) ? h : null, castle, band);
                reads += floor.Reads;
                if (!floor.Found) return Probe.No("no ground");
                if (!walkRan) return Probe.Ok(floor.Y, floor.Top);
                if (!ground.TryIsWalkable(world, px, floor.Y, pz, out bool standable))
                {
                    // The check itself cannot run here (no agent prefab, the call threw): say so once and fall
                    // back to the height test rather than refuse every point.
                    walkRan = false;
                    Log("walkable check unavailable; height only for this request");
                    return Probe.Ok(floor.Y, floor.Top);
                }

                return standable ? Probe.Ok(floor.Y, floor.Top) : Probe.No("not walkable");
            });

            // A full level walk at the chosen spot (no ground-band stop): the floors under it, for the fallback order and
            // for whether the position write may be used there.
            at = spot.Found
                ? WalkFloors(probeY => ground.TryGetHeight(spot.X, probeY, spot.Z, out float h) ? h : null)
                : new Walk(Array.Empty<float>(), 0);
            reads += at.Reads;
        }

        if (!spot.Found)
        {
            done(Finish(who, Result.NoValidSpot, x, z, spot, "", via, walkRan, castle, reads: reads));
            return;
        }

        var candidates = Candidates(spot.Y, at, castle, band);
        var targets = new PlayerTeleport.TeleportTarget[candidates.Count];
        for (int i = 0; i < candidates.Count; i++)
            targets[i] = new PlayerTeleport.TeleportTarget(new float3(spot.X, candidates[i].Y, spot.Z), candidates[i].WriteAllowed);

        bool walk = walkRan;
        PlayerTeleport.TeleportVerified(world, character, targets, DebugTools.Owner, verified =>
        {
            string verify = verified.Arrived
                ? "arrived"
                : "not-arrived (" + verified.Why + ")" + (verified.After is { } a
                    ? " at (" + a.x.ToString("0.0", CultureInfo.InvariantCulture) + ", "
                        + a.y.ToString("0.0", CultureInfo.InvariantCulture) + ", "
                        + a.z.ToString("0.0", CultureInfo.InvariantCulture) + ")"
                    : "");
            var result = !verified.Arrived
                ? (verified.Method.Length == 0 ? Result.TeleportFailed : Result.Refused)
                : spot.Distance > 0.5f ? Result.Moved : Result.Teleported;
            float landed = verified.Arrived && verified.Target > 0 && verified.Target < candidates.Count
                ? candidates[verified.Target].Y
                : float.NaN;
            done(Finish(who, result, x, z, spot, verified.Method, via, walk, castle, verify, candidates, reads, landed));
        });
    }

    /// <summary>
    /// Whether (<paramref name="x"/>, <paramref name="z"/>) lies in a castle's territory (X/Z only,
    /// <see cref="Progenitor.Castles.Castles.At(Progenitor.Castles.CastleVec3)"/>), and that territory's ground band
    /// (<c>CastleTerritory.MinHeightLevel</c> to <c>MaxHeightLevel</c>, null when it reads as empty). Not a castle when
    /// the castle module cannot answer, which keeps the single top-layer read.
    /// </summary>
    private static (bool Castle, GroundBand? Band) CastleBand(float x, float z)
    {
        try
        {
            var handle = Progenitor.Castles.Castles.At(new Progenitor.Castles.CastleVec3(x, 0f, z));
            if (!handle.Enabled) return (false, null);
            var t = handle.Territory;
            GroundBand? band = t.MaxHeightLevel > 0 && t.MinHeightLevel <= t.MaxHeightLevel
                ? new GroundBand(t.MinHeightLevel, t.MaxHeightLevel)
                : null;
            return (true, band);
        }
        catch (Exception e)
        {
            Log("castle territory lookup failed (" + e.Message + "); top floor only");
            return (false, null);
        }
    }

    private static Outcome Finish(string who, Result result, float x, float z, Spot spot, string method, string via,
        bool walkCheck = false, bool castle = false, string verify = "", IReadOnlyList<Candidate>? floors = null, int reads = 0,
        float landed = float.NaN)
    {
        string line = Readback(who, result, x, z, spot, method, walkCheck, castle, verify, floors, reads, landed) + " via=" + via;
        Log(line);
        return new Outcome(result, spot, method, line);
    }

    /// <summary>
    /// <c>.debug tp &lt;x&gt; &lt;z&gt;</c>: the same path as the map, with the readback as the reply. The reply comes
    /// after the read-back, a second or two later.
    /// </summary>
    internal static void RunCommand(World world, Entity userEntity, Action<string> reply, float x, float z)
    {
        if (world == null || !world.IsCreated)
        {
            reply("No server world.");
            return;
        }

        Teleport(world, userEntity, x, z, "command", outcome =>
        {
            if (!world.IsCreated) return;
            reply(Notice(outcome.Result, outcome.Spot.Distance, Radius));
            reply(outcome.Line);
        });
    }

    /// <summary>The <c>.debug status</c> lines for this character.</summary>
    internal static IReadOnlyList<string> StatusFor(World world, Entity userEntity, Entity character)
    {
        try
        {
            var em = world.EntityManager;
            var state = CurrentState(em, userEntity, character);
            return StatusLines(state, WasSent(userEntity, state), Radius, WalkCheck);
        }
        catch (Exception e)
        {
            return new[] { "  map powers: UNREADABLE (" + e.Message + ")" };
        }
    }

    private static void Log(string line) => Plugin.Logger?.LogInfo("Debug map: " + line);

    private sealed class Disposer : IDisposable
    {
        private Action? _action;

        internal Disposer(Action action) => _action = action;

        public void Dispose()
        {
            var a = _action;
            _action = null;
            a?.Invoke();
        }
    }

    // ==== client ===================================================================================

    private static class Client
    {
        private const float RingDiameter = 56f;

        private static readonly MapTeleportHold Machine = new();
        private static readonly Stopwatch Clock = Stopwatch.StartNew();
        private static HoldRing? _ring;
        private static IDisposable? _reveal;
        private static DebugMapState _state = DebugMapState.None;
        private static ulong _stateWorld;
        private static double _pulseStart = -1;

        // True from Press until the frame that ends the hold. A release ends the hold at once (Phase goes
        // Idle), so the frame must still run once to read the cancel and hide the ring (live 2026-10-02:
        // an early release left the ring on the map).
        private static bool _holdLive;
        private static int _nonce;
        private static int _revealCheck;
        private static ulong _queriedWorld = ulong.MaxValue;
        private static bool _attached;
        private static IKeybindHandle? _key;

        private static double Now => Clock.Elapsed.TotalSeconds;

        internal static void Attach(List<IDisposable> registrations)
        {
            _ring = new HoldRing("progenitor-debug-maptp-ring");
            CustomEvents.OnReceive(StateEventId, OnState);
            registrations.Add(new Disposer(() => CustomEvents.RemoveHandler(StateEventId, OnState)));
            CustomEvents.OnReceive(ReplyEventId, OnReply);
            registrations.Add(new Disposer(() => CustomEvents.RemoveHandler(ReplyEventId, OnReply)));

            var file = UiKeybindHost.CoreConfig;
            if (file != null)
            {
                _holdSeconds = ConfigHelper.Bind(file, Section, "HoldSeconds", (float)HoldMachine.DefaultHoldSeconds,
                    "Client: how long to hold the debug-buff map teleport key over the map before it fires. Clamped to "
                    + HoldMachine.MinHoldSeconds.ToString(CultureInfo.InvariantCulture) + ".."
                    + HoldMachine.MaxHoldSeconds.ToString(CultureInfo.InvariantCulture) + " seconds.");
                _key = Keybinds.RegisterScoped(DebugTools.Owner, SettingsPanel.PluginId, KeybindId, file,
                    DefaultTeleportKey,
                    KeybindText + " (debug buff): hold over the open map to teleport there.",
                    Press, null, Release,
                    allowWhileCaptured: false, allowWhenChatUnknown: true, KeybindScope.Map);
                registrations.Add(_key);
            }
            else
            {
                Log("client: Core's ui cfg is not open; the map teleport key is not registered.");
            }

            Progenitor.Plugin.Frame += OnFrame;
            _attached = true;
            registrations.Add(new Disposer(Detach));
            Log("client: map teleport key '" + KeybindId + "' is " + (_key?.Chord.ToString() ?? "not registered")
                + "; hold it over the map. An admin authenticated with adminauth is teleported by the game's own Ctrl+Shift+left click instead.");
        }

        private static void Detach()
        {
            if (!_attached) return;
            _attached = false;
            Progenitor.Plugin.Frame -= OnFrame;
            Machine.Cancel(HoldCancel.Unloaded);
            _holdLive = false;
            DropReveal();
            _state = DebugMapState.None;
            _queriedWorld = ulong.MaxValue;
            _holdSeconds = null;
            _key = null;
            _ring?.Dispose();
            _ring = null;
        }

        private static unsafe void OnState(int eventId, byte* payload, int length)
        {
            try
            {
                if (eventId != StateEventId) return;
                if (!CustomEvents.TryRead<StateMsg>(payload, length, out var msg) || msg.Magic != Magic) return;
                var state = msg.State;
                _stateWorld = WorldSequence();
                if (state == _state) return;
                _state = state;
                if (state.Reveal) _reveal ??= WorldMap.RevealAll(DebugTools.Owner);
                else DropReveal();
                Log("state reveal=" + state.Reveal + " teleport=" + state.CanTeleport + " gameTeleports=" + state.GameTeleports
                    + " flag=" + (WorldMap.RevealFlag?.ToString() ?? "?"));
            }
            catch (Exception e)
            {
                Plugin.Logger?.LogError("Debug map state threw: " + e);
            }
        }

        private static unsafe void OnReply(int eventId, byte* payload, int length)
        {
            if (eventId != ReplyEventId) return;
            if (!CustomEvents.TryRead<ReplyMsg>(payload, length, out var reply) || reply.Magic != Magic) return;
            var result = (Result)reply.Code;
            Log(Notice(result, reply.Moved, Radius)
                + " requested=(" + reply.RequestedX.ToString("0.0", CultureInfo.InvariantCulture) + ", "
                + reply.RequestedZ.ToString("0.0", CultureInfo.InvariantCulture) + ") nonce=" + reply.Nonce);
        }

        private static void DropReveal()
        {
            var lease = _reveal;
            _reveal = null;
            lease?.Dispose();
        }

        private static ulong WorldSequence()
        {
            try { return Progenitor.WorldState.ClientWorldEpoch.ObservedSequence; }
            catch { return 0; }
        }

        private static void Press()
        {
            try
            {
                if (!_state.CanTeleport) return;
                // One teleport per gesture: the game's own handler already moves a game-authenticated admin on this click.
                // (A controller press of the same bind is skipped too; the bind cannot tell which input fired it.)
                if (_key != null && SkipHold(_state, _key.Chord)) return;
                bool onMap = WorldMap.TryReadPointer(out var p) && p.OnMap && p.MapRect != null;
                double hold = HoldMachine.ClampHold(_holdSeconds?.Value ?? HoldMachine.DefaultHoldSeconds);
                if (!Machine.Press(_state, onMap, hold, Now)) return;
                _pulseStart = -1;
                _ring?.ShowAt(p.MapRect!, new Vector2(p.LocalX, p.LocalY), RingDiameter);
                _holdLive = true;
            }
            catch (Exception e)
            {
                Plugin.Logger?.LogError("Debug map press threw: " + e);
            }
        }

        /// <summary>The key came up, or the map closed under it (the keybind pump releases a map key then).</summary>
        private static void Release()
        {
            if (WorldMap.IsOpen) Machine.Release();
            else Machine.Cancel(HoldCancel.Closed);
        }

        private static void OnFrame()
        {
            try
            {
                // A new client world (a reconnect, another server) starts with no powers until the server says.
                ulong world = WorldSequence();
                if (_state != DebugMapState.None && world != _stateWorld)
                {
                    _state = DebugMapState.None;
                    DropReveal();
                    Machine.Cancel(HoldCancel.Closed);
                }

                // Ask once per client world (and once after a Core load or hot swap) instead of waiting for
                // the server's periodic repeat.
                if (world != _queriedWorld && Progenitor.Worlds.Client != null)
                {
                    _queriedWorld = world;
                    bool asked = CustomEvents.SendToServer(QueryEventId, new QueryMsg { Magic = Magic });
                    Log("asked the server for the map powers (sent=" + asked + ").");
                }

                // A state that arrived before the client's map system existed got an inert lease; take a real
                // one once the flag is readable (and re-assert it if something switched it off).
                if (_state.Reveal && ++_revealCheck % 30 == 0 && WorldMap.RevealFlag == false)
                {
                    DropReveal();
                    _reveal = WorldMap.RevealAll(DebugTools.Owner);
                }

                if (_pulseStart >= 0)
                {
                    var (scale, alpha, done) = HoldRingMath.Pulse(Now - _pulseStart);
                    _ring?.SetPulse(scale, alpha);
                    if (done)
                    {
                        _pulseStart = -1;
                        _ring?.Hide();
                    }
                }

                if (Machine.Phase == HoldPhase.Idle && !_holdLive) return;

                bool open = WorldMap.IsOpen;
                MapPointer pointer = default;
                bool onMap = open && WorldMap.TryReadPointer(out pointer) && pointer.OnMap;
                if (onMap) _ring?.MoveTo(new Vector2(pointer.LocalX, pointer.LocalY));

                var step = Machine.Frame(Now, open, onMap, _state);
                if (step.Fire)
                {
                    _pulseStart = Now;
                    _ring?.SetProgress(1f);
                    Send(pointer);
                    return;
                }

                if (step.Phase == HoldPhase.Charging)
                {
                    _ring?.SetProgress(step.Progress);
                    return;
                }

                if (step.Phase == HoldPhase.Idle)
                {
                    _holdLive = false;
                    if (step.Cancelled != HoldCancel.None)
                        Log("hold canceled (" + step.Cancelled + "); nothing sent.");
                    if (_pulseStart < 0) _ring?.Hide();
                }
            }
            catch (Exception e)
            {
                Plugin.Logger?.LogError("Debug map frame threw: " + e);
                Machine.Cancel(HoldCancel.Closed);
                _holdLive = false;
                _ring?.Hide();
            }
        }

        private static void Send(MapPointer pointer)
        {
            if (!pointer.HasWorld)
            {
                Log("hold completed but the map has no world position there (" + pointer.Why + "); nothing sent.");
                return;
            }

            int nonce = ++_nonce;
            bool sent = CustomEvents.SendToServer(RequestEventId,
                new RequestMsg { Magic = Magic, Nonce = nonce, X = pointer.WorldX, Z = pointer.WorldZ });
            Log("request nonce=" + nonce + " world=(" + pointer.WorldX.ToString("0.0", CultureInfo.InvariantCulture)
                + ", " + pointer.WorldZ.ToString("0.0", CultureInfo.InvariantCulture) + ") sent=" + sent);
        }
    }
}
#endif
