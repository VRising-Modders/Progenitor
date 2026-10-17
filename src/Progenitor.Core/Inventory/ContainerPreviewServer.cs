#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;
using Progenitor.Cameras;
using Progenitor.Castles;
using Progenitor.Ecs;
using Progenitor.Install;
using Progenitor.Networking;
using Progenitor.Plugins;
using ProjectM;
using ProjectM.CastleBuilding;
using ProjectM.Network;
using Unity.Entities;
using Unity.Transforms;

namespace Progenitor.Inventory;

/// <summary>
/// The server half of the container preview, Core-owned (no plugin can widen or bypass it). A client asks to preview a
/// <see cref="ContainerId"/>; the server checks the container is a plain container (a chest, a brazier: an inventory
/// and no menu of its own) in a castle whose owner or clan the user is (<see cref="ContainerPreviewAuthorization"/>),
/// then holds the user's shared streaming-origin lease at the container (<see cref="SharedObservation"/>, shared with
/// a camera preview at the same spot) and replies with the container's network id. It re-checks existence and
/// access every second and lets go (an <c>ended</c> packet) when either fails.
///
/// <para><b>What it never does.</b> It grants no interaction: the game's own server check (the sender must hold an
/// interaction with the container) refuses every item event the window sends, so the preview is read-only whatever
/// the client sends.</para>
/// </summary>
internal static unsafe class ContainerPreviewServer
{
    private static readonly Dictionary<Entity, Holder> ByUser = new();
    private static readonly RemoteInstallRateLimiter Opens = new(30, 60);
    private static bool _installed;
    private static double _nextCheck;

    internal static int ActiveCount => ByUser.Count;

    internal static void Install()
    {
        if (_installed || !Plugin.IsServerProcess) return;
        _installed = true;
        CustomEvents.OnReceiveFromClient(ContainerPreviewWire.EventId, OnRequest);
        Plugin.Frame += Tick;
    }

    internal static void Uninstall()
    {
        if (!_installed) return;
        _installed = false;
        try { CustomEvents.RemoveClientHandler(ContainerPreviewWire.EventId); }
        catch (Exception e) { Plugin.Logger?.LogWarning("ContainerPreview: handler removal threw: " + e.Message); }
        Plugin.Frame -= Tick;
        foreach (var holder in new List<Holder>(ByUser.Values)) holder.End(CameraObservationEndReason.Unloaded, notify: false, message: "");
        ByUser.Clear();
    }

    /// <summary>Dev bridge status: every server-side preview.</summary>
    internal static List<Dictionary<string, object?>> Describe()
    {
        var rows = new List<Dictionary<string, object?>>();
        foreach (var pair in ByUser)
        {
            var h = pair.Value;
            var lease = h.Lease;
            rows.Add(new Dictionary<string, object?>
            {
                ["user"] = pair.Key.Index + ":" + pair.Key.Version,
                ["sequence"] = h.Sequence,
                ["container"] = h.Container.ToString(),
                ["entity"] = h.Entity.Index + ":" + h.Entity.Version,
                ["secondsLeft"] = Math.Round(Math.Max(0, h.Policy.Deadline - SharedObservation.Now), 1),
                ["leaseHolders"] = lease?.HolderCount ?? 0,
            });
        }
        return rows;
    }

    private static double Now => SharedObservation.Now;

    private static void OnRequest(int _, Entity userEntity, User ignored, byte* payload, int length)
    {
        if (payload == null || length <= 0 || length > ContainerPreviewWire.MaxBytes) return;
        var request = ContainerPreviewWire.Decode(new ReadOnlySpan<byte>(payload, length), expectRequest: true);
        if (request == null) return;
        var world = Worlds.Server;
        if (world == null || !world.IsCreated) return;
        var em = world.EntityManager;
        if (!userEntity.Exists(em) || !userEntity.Has<User>(em)) return;
        var user = em.GetComponentData<User>(userEntity);
        try
        {
            switch (request.Kind)
            {
                case ContainerPreviewWire.KindOpen: Open(world, userEntity, user, request); break;
                case ContainerPreviewWire.KindRenew:
                    if (ByUser.TryGetValue(userEntity, out var renewing) && renewing.Sequence == request.Sequence)
                        renewing.Renew(ContainerPreviewAuthorization.GrantSeconds(request.Seconds));
                    break;
                case ContainerPreviewWire.KindClose:
                    if (ByUser.TryGetValue(userEntity, out var closing) && closing.Sequence == request.Sequence)
                        closing.End(CameraObservationEndReason.Disposed, notify: false, message: "");
                    break;
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("ContainerPreview: " + request.Kind + " threw: " + e.Message);
            if (request.Kind == ContainerPreviewWire.KindOpen) Reply(world, user.Index, request.Sequence, false, "The preview failed on the server.", 0, 0, 0);
        }
    }

    private static void Open(World world, Entity userEntity, User user, ContainerPreviewPacket request)
    {
        var em = world.EntityManager;
        var facts = new ContainerPreviewFacts { UserConnected = user.IsConnected };
        facts.RateOk = facts.UserConnected && Opens.TryTake(user.PlatformId, Now);
        facts.IdParsed = ContainerId.TryParse(request.Container, out var id);
        Entity container = Entity.Null;
        int netIndex = 0, netGeneration = 0, netType = 0;
        if (facts.RateOk && facts.IdParsed && InventoryRuntime.Installed)
        {
            facts.Found = InventoryRuntime.TryFind(id, out container, out _) && container.Exists(em);
            if (facts.Found)
            {
                facts.HasInventory = UnityInventoryBackend.HasInventory(em, container);
                facts.HasOwnMenu = HasOwnMenu(em, container);
                var castle = CastleOf(world, container);
                facts.InCastle = castle != null;
                facts.Relation = castle != null ? PluginCastles.RelationOf(world, userEntity, castle) : CastleRelation.None;
                facts.HasNetworkId = InventoryRuntime.TryGetNetworkId(id, out netIndex, out netGeneration, out netType, out _);
            }
        }

        var refusal = ContainerPreviewAuthorization.Refusal(facts);
        if (refusal == null && !container.TryRead<Translation>(em, out _)) refusal = "That container has no position.";
        if (refusal != null)
        {
            Plugin.Logger?.LogInfo("ContainerPreview: refused user " + user.PlatformId + " " + request.Container + ": " + refusal);
            Reply(world, user.Index, request.Sequence, false, refusal, 0, 0, 0);
            return;
        }

        if (ByUser.TryGetValue(userEntity, out var previous)) previous.End(CameraObservationEndReason.Replaced, notify: false, message: "");
        container.TryRead<Translation>(em, out var at);
        var holder = new Holder(userEntity, user.Index, request.Sequence, id, container, ContainerPreviewAuthorization.GrantSeconds(request.Seconds));
        try { SharedObservation.Acquire(world, userEntity, at.Value, holder); }
        catch (InvalidOperationException)
        {
            Reply(world, user.Index, request.Sequence, false, "Another view is already streaming for you; close it first.", 0, 0, 0);
            return;
        }
        ByUser[userEntity] = holder;
        Plugin.Logger?.LogInfo("ContainerPreview: user " + user.PlatformId + " previews " + id + " (" + container.Index + ":" + container.Version
            + ", " + holder.Policy.Deadline.ToString("0") + ").");
        Reply(world, user.Index, request.Sequence, true, "", netIndex, netGeneration, netType);
    }

    /// <summary>Anything with a menu of its own (or no business in a container window): only plain containers pass.</summary>
    private static bool HasOwnMenu(EntityManager em, Entity e) =>
        e.Has<CastleHeart>(em) || e.Has<Refinementstation>(em) || e.Has<WorkstationRecipesBuffer>(em)
        || e.Has<ResearchStation>(em) || e.Has<Salvagestation>(em) || e.Has<UnitSpawnerstation>(em)
        || e.Has<PrisonCell>(em) || e.Has<ServantCoffinstation>(em) || e.Has<ServantEquipment>(em);

    private static ICastleHandle? CastleOf(World world, Entity container)
    {
        var plot = CastleReader.PlotOf(world, container);
        if (plot == null || plot.HeartEntity == Entity.Null) return null;
        var handle = Castles.Castles.ForHeart(CastleId.From(plot.HeartEntity));
        return handle.Enabled ? handle : null;
    }

    /// <summary>Once a second: the container still exists and the user still has access; otherwise let go.</summary>
    private static void Tick()
    {
        if (ByUser.Count == 0) return;
        double now = Now;
        if (now < _nextCheck) return;
        _nextCheck = now + 1;
        var world = Worlds.Server;
        if (world == null || !world.IsCreated) return;
        var em = world.EntityManager;
        foreach (var holder in new List<Holder>(ByUser.Values))
        {
            string? why = null;
            try
            {
                if (!holder.Entity.Exists(em)) why = "The container is gone.";
                else
                {
                    var castle = CastleOf(world, holder.Entity);
                    var relation = castle != null ? PluginCastles.RelationOf(world, holder.User, castle) : CastleRelation.None;
                    if (relation is not (CastleRelation.Owner or CastleRelation.ClanMember)) why = "You no longer have access to that container.";
                }
            }
            catch (Exception e) { why = "The preview check failed: " + e.Message; }
            if (why != null) holder.End(CameraObservationEndReason.Disposed, notify: true, message: why);
        }
    }

    private static void Reply(World world, int userIndex, int sequence, bool ok, string message, int index, int generation, int type)
    {
        Send(world, userIndex, new ContainerPreviewPacket
        {
            Kind = ContainerPreviewWire.KindReply, Sequence = sequence, Ok = ok, Message = ContainerPreviewWire.Clip(message),
            NetIndex = index, NetGeneration = generation, NetType = type,
        });
    }

    private static void Send(World world, int userIndex, ContainerPreviewPacket packet)
    {
        var bootstrap = world.GetExistingSystemManaged<ServerBootstrapSystem>();
        if (bootstrap == null) return;
        CustomEvents.SendToUser(bootstrap, userIndex, ContainerPreviewWire.EventId, new ReadOnlySpan<byte>(ContainerPreviewWire.Encode(packet)));
    }

    /// <summary>The player-facing sentence for a lease end the client is told about.</summary>
    private static string EndMessage(CameraObservationEndReason reason) => reason switch
    {
        CameraObservationEndReason.Expired => "The preview's time ran out.",
        CameraObservationEndReason.LeftArea => "You moved away from where you opened the preview.",
        CameraObservationEndReason.Superseded => "Another view took over your streaming origin.",
        CameraObservationEndReason.CharacterGone => "Your character changed.",
        CameraObservationEndReason.Replaced => "A newer preview replaced it.",
        CameraObservationEndReason.Failed => "The preview failed on the server.",
        _ => "The server ended the preview.",
    };

    /// <summary>One user's preview: a holder of that user's shared lease.</summary>
    private sealed class Holder : IObservationHolder
    {
        private SharedObservation? _lease;
        private string _message = "";
        private bool _ended;

        internal Holder(Entity user, int userIndex, int sequence, ContainerId container, Entity entity, double seconds)
        {
            User = user; UserIndex = userIndex; Sequence = sequence; Container = container; Entity = entity;
            double now = Now;
            Policy = new CameraObservationPolicy(CameraObservationPolicy.MaxTimedSeconds, hold: false, now);
            Policy.Renew(now, seconds);
        }

        internal Entity User { get; }
        internal int UserIndex { get; }
        internal int Sequence { get; }
        internal ContainerId Container { get; }
        internal Entity Entity { get; }
        internal SharedObservation? Lease => _lease;

        public CameraObservationPolicy Policy { get; }
        public string Name => "container-preview:" + Container;
        public void Attached(SharedObservation lease) => _lease = lease;

        internal void Renew(double seconds)
        {
            if (_ended) return;
            Policy.Renew(Now, seconds);
            _lease?.Rearm();
        }

        /// <summary>Leave the lease; <paramref name="message"/> overrides the reason's sentence in the ended packet.</summary>
        internal void End(CameraObservationEndReason reason, bool notify, string message)
        {
            if (_ended) return;
            _message = message;
            if (_lease != null) _lease.Leave(this, reason, notify);
            else LeaseEnded(reason, notify);
        }

        public void LeaseEnded(CameraObservationEndReason reason, bool notify)
        {
            if (_ended) return;
            _ended = true;
            _lease = null;
            if (ByUser.TryGetValue(User, out var current) && ReferenceEquals(current, this)) ByUser.Remove(User);
            Plugin.Logger?.LogInfo("ContainerPreview: " + Container + " ended (" + reason + (_message.Length > 0 ? ", " + _message : "") + ").");
            if (!notify || reason is CameraObservationEndReason.Disconnected or CameraObservationEndReason.Unloaded) return;
            var world = Worlds.Server;
            if (world == null || !world.IsCreated) return;
            try
            {
                Send(world, UserIndex, new ContainerPreviewPacket
                {
                    Kind = ContainerPreviewWire.KindEnded, Sequence = Sequence, Container = Container.ToString(),
                    Message = ContainerPreviewWire.Clip(_message.Length > 0 ? _message : EndMessage(reason)),
                });
            }
            catch (Exception e) { Plugin.Logger?.LogWarning("ContainerPreview: ended send threw: " + e.Message); }
        }
    }
}
#endif
