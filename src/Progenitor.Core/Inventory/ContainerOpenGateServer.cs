#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;
using Progenitor.Ai;
using Progenitor.Castles;
using Progenitor.Ecs;
using Progenitor.Events;
using Progenitor.Hooks;
using Progenitor.Install;
using Progenitor.Networking;
using Progenitor.Plugins;
using ProjectM;
using ProjectM.CastleBuilding;
using ProjectM.Network;
using ProjectM.Shared;
using ProjectM.Tiles;
using Unity.Collections;
using Unity.Entities;

namespace Progenitor.Inventory;

/// <summary>
/// One open a handler is asked about (<c>ctx.Inventory.OnOpenRequest</c>). Server entities: valid for this call only;
/// keep <see cref="Container"/> and <see cref="PlatformId"/>, never the entities.
/// </summary>
public sealed class ContainerOpenRequest
{
    internal ContainerOpenRequest(Entity user, Entity character, ulong platformId, ContainerId container, Entity containerEntity,
        ContainerOpenKind kind, bool hasSessionGrant)
    {
        User = user;
        Character = character;
        PlatformId = platformId;
        Container = container;
        ContainerEntity = containerEntity;
        Kind = kind;
        HasSessionGrant = hasSessionGrant;
    }

    /// <summary>The player's <c>User</c> entity (server).</summary>
    public Entity User { get; }

    /// <summary>The player's character (server); <c>Entity.Null</c> for a preview without a character.</summary>
    public Entity Character { get; }

    /// <summary>The player's platform id (a SteamID64): the key to keep.</summary>
    public ulong PlatformId { get; }

    /// <summary>The container's stable id.</summary>
    public ContainerId Container { get; }

    /// <summary>The container, station or castle heart (server entity).</summary>
    public Entity ContainerEntity { get; }

    /// <summary>A walk-up interaction or the remote preview.</summary>
    public ContainerOpenKind Kind { get; }

    /// <summary>
    /// This plugin accepted an answer for this (player, container) earlier this session with
    /// <see cref="ContainerAnswerVerdict.RememberForSession"/>. A <see cref="ContainerOpenDecision.Challenge"/> is then
    /// counted as answered; a <see cref="ContainerOpenDecision.Deny"/> still refuses.
    /// </summary>
    public bool HasSessionGrant { get; }

    internal ContainerOpenRequest WithGrant(bool granted) =>
        granted == HasSessionGrant ? this : new ContainerOpenRequest(User, Character, PlatformId, Container, ContainerEntity, Kind, granted);

    /// <inheritdoc />
    public override string ToString() => Kind + " " + Container + " by " + PlatformId + (HasSessionGrant ? " (granted)" : "");
}

/// <summary>A player's answer to this plugin's challenge (<c>ctx.Inventory.OnOpenRequest</c>'s answer handler).</summary>
public sealed class ContainerOpenAnswer
{
    internal ContainerOpenAnswer(ContainerOpenRequest request, string token, ChallengeSecret secret, bool remember = false)
    {
        Remember = remember;
        Request = request;
        Token = token ?? "";
        Secret = secret;
    }

    /// <summary>The open that was challenged.</summary>
    public ContainerOpenRequest Request { get; }

    /// <summary>The token the challenge carried.</summary>
    public string Token { get; }

    /// <summary>What the player typed. Never logged; <see cref="ChallengeSecret.ToString"/> hides it.</summary>
    public ChallengeSecret Secret { get; }

    /// <summary>
    /// The player ticked <b>Remember password</b> (only with <see cref="ContainerOpenDecision.OfferRemember"/>; false
    /// otherwise). Keep your own grant for it if the answer is right; the gate stores nothing.
    /// </summary>
    public bool Remember { get; }

    /// <inheritdoc />
    public override string ToString() => "answer for " + Request + " (" + Secret.Length + " characters)";
}

/// <summary>
/// The server half of the container open gate, Core-owned. Plugins register handlers with
/// <c>ctx.Inventory.OnOpenRequest</c>; every way a player gets a container window then passes through them: a walk-up
/// interaction (here) and the remote preview (<see cref="ContainerPreviewServer"/>). Deny wins; a challenge sends the
/// client a masked prompt and waits for the answer.
///
/// <para><b>Seam.</b> A player's interaction with anything is an interact buff on the character (<c>InteractBuff</c>,
/// <c>SpellTarget.Target</c> = what is interacted with, <c>EntityOwner</c> = the character). The client opens the window
/// when that buff reaches it, and the server allows the window's item events only while it exists. The gate looks at
/// every new interact buff right after the game's <c>InteractInventorySync_Spawn</c> (the spawn reaction that shares the
/// inventory with the user) and, for a refused open, destroys it with <c>DestroyUtility.Destroy</c>, the way the game's
/// own interact validation ends an interaction it does not allow. Both happen inside the same spawn and destroy pass of
/// the frame, before the frame's network send, so the buff and the inventory never reach the client, and the game's
/// destroy reaction takes the user back off the inventory's sync list. A <c>Plugin.Frame</c> pass catches a buff that
/// did not come through the spawn reaction (one frame late). Hooks exist only while a handler is registered.</para>
///
/// <para><b>Status:</b> source-checked, built and game-free tested; not verified live (docs/modules.md, "Container open
/// gate").</para>
/// </summary>
internal static unsafe class ContainerOpenGateServer
{
    /// <summary>The handlers, grants and passes.</summary>
    internal static readonly ContainerGateBook<ContainerOpenRequest> Book = new();

    /// <summary>At most this many answers per player per minute; more are refused unread.</summary>
    internal const int AnswersPerMinute = 5;

    private static readonly Dictionary<ulong, PendingOpen> PendingByPlatform = new();
    private static readonly RemoteInstallRateLimiter Answers = new(AnswersPerMinute, 60);
    private static readonly WorldBoundQuery BuffQuery = new("ContainerOpenGate interact buffs");
    private static readonly HashSet<long> Seen = new();
    private static readonly HashSet<long> Present = new();
    private static readonly ContainerGateRecent Recent = new();
    private static readonly ContainerGateExtras Extras = new();
    private static int _unresolvedLogged;
    private static IDisposable? _hook;
    private static IDisposable? _disconnected;
    private static bool _installed;
    private static bool _frameHooked;
    private static bool _primed;
    private static int _sequence;
    private static int _failures;
    private static long _closed;

    internal static bool Installed => _installed;

    private static double Now => Cameras.SharedObservation.Now;

    internal static void Install()
    {
        if (_installed || !Plugin.IsServerProcess) return;
        _installed = true;
        Book.Warn = line => Plugin.Logger?.LogWarning(line);
        CustomEvents.OnReceiveFromClient(ContainerGateWire.EventId, OnPacket);
        _disconnected = EventBus.On<PlayerDisconnected>(e => Forget(e.User.PlatformId), priority: 0);
    }

    internal static void Uninstall()
    {
        if (!_installed) return;
        _installed = false;
        try { CustomEvents.RemoveClientHandler(ContainerGateWire.EventId); }
        catch (Exception e) { Plugin.Logger?.LogWarning("ContainerOpenGate: handler removal threw: " + e.Message); }
        try { _disconnected?.Dispose(); } catch { /* */ }
        _disconnected = null;
        Disarm();
        // Previews held for an answer get their refusal now; nothing else needs telling (the client side unloads too).
        foreach (var p in new List<PendingOpen>(PendingByPlatform.Values)) p.Continue?.Invoke(false, ContainerGateText.Changed);
        PendingByPlatform.Clear();
        Book.Clear();
        Book.Warn = null;
    }

    // ---- registration ---------------------------------------------------------------------------

    /// <summary>Add a plugin's handlers; dispose removes them (and the plugin's grants when it has no other handler).</summary>
    internal static IDisposable Register(object owner, string pluginId, string pluginName,
        Func<ContainerOpenRequest, ContainerOpenDecision> decide, Func<ContainerOpenAnswer, ContainerAnswerVerdict>? answer)
    {
        if (!_installed) throw new InvalidOperationException("The container open gate is not installed (server side only).");
        Func<ContainerOpenRequest, string, ChallengeSecret, bool, ContainerAnswerVerdict>? wrapped = null;
        if (answer != null) wrapped = (request, token, secret, remember) => answer(new ContainerOpenAnswer(request, token, secret, remember));
        var handler = Book.Add(owner, pluginId, pluginName, decide, wrapped);
        Arm();
        Plugin.Logger?.LogInfo("ContainerOpenGate: " + handler.PluginName + " registered an open handler (" + Book.Count + " in all).");
        return new ActionDisposable(() => Unregister(handler));
    }

    private static void Unregister(ContainerGateHandler<ContainerOpenRequest> handler)
    {
        if (!Book.Remove(handler)) return;
        // An open waiting for this handler's answer cannot be checked any more: it ends, closed.
        foreach (var p in new List<PendingOpen>(PendingByPlatform.Values))
            if (p.State.Owns(handler)) End(p, ok: false, ContainerGateText.Changed);
        if (Book.Count == 0) Disarm();
        Plugin.Logger?.LogInfo("ContainerOpenGate: " + handler.PluginName + " removed an open handler (" + Book.Count + " left).");
    }

    /// <summary>Drop one plugin's grants and passes for a container (its password changed).</summary>
    internal static int RevokeContainer(string pluginId, ContainerId container) => Book.RevokeContainer(pluginId, container);

    /// <summary>Drop one plugin's grants and passes for a player.</summary>
    internal static int RevokePlayer(string pluginId, ulong platform) => Book.RevokePlayer(pluginId, platform);

    private static void Forget(ulong platform)
    {
        int n = Book.ForgetPlayer(platform);
        if (PendingByPlatform.TryGetValue(platform, out var p)) End(p, ok: false, "", notify: false);
        if (n > 0) Plugin.Logger?.LogInfo("ContainerOpenGate: user " + platform + " left; " + n + " grant(s) and pass(es) cleared.");
    }

    // ---- the interaction seam -------------------------------------------------------------------

    private static void Arm()
    {
        if (_hook != null) return;
        try
        {
            _hook = SystemHooks.After<InteractInventorySync_Spawn>(OnAfterInventorySync, new HookOptions { PluginId = "progenitor.container-gate" });
        }
        catch (Exception e)
        {
            // Without the hook the frame pass still closes refused interactions, one frame late.
            Plugin.Logger?.LogWarning("ContainerOpenGate: the interaction hook could not be installed (" + e.Message + "); refused interactions close one frame late.");
        }
        if (!_frameHooked) { Plugin.Frame += OnFrame; _frameHooked = true; }
        // An interaction already open when the first handler arrives is left alone: mark every current one as seen.
        _primed = false;
        Seen.Clear();
        var world = Worlds.Server;
        if (world != null && world.IsCreated)
        {
            try { Scan(world); }
            catch (Exception e) { Plugin.Logger?.LogWarning("ContainerOpenGate: priming threw: " + e.Message); }
        }
        _primed = true;
    }

    private static void Disarm()
    {
        try { _hook?.Dispose(); } catch { /* */ }
        _hook = null;
        if (_frameHooked) { Plugin.Frame -= OnFrame; _frameHooked = false; }
        BuffQuery.Release();
        Recent.Clear();
        Extras.Clear();
        Seen.Clear();
        Present.Clear();
        _primed = false;
    }

    private static void OnAfterInventorySync(SystemHookContext c)
    {
        var world = c.World;
        if (world == null || Book.Count == 0) return;
        try { Scan(world); }
        catch (Exception e) { if (_failures++ < 4) Plugin.Logger?.LogWarning("ContainerOpenGate: interaction check threw: " + e.GetType().Name); }
    }

    private static void OnFrame()
    {
        if (Book.Count == 0) return;
        var world = Worlds.Server;
        if (world == null || !world.IsCreated) return;
        try { Scan(world); }
        catch (Exception e) { if (_failures++ < 4) Plugin.Logger?.LogWarning("ContainerOpenGate: interaction check threw: " + e.GetType().Name); }
        try { ExpirePending(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("ContainerOpenGate: prompt expiry threw: " + e.Message); }
    }

    /// <summary>Every interact buff not seen before is checked once.</summary>
    private static void Scan(World world)
    {
        if (!world.IsCreated || world.Name != Worlds.ServerName) return;
        var em = world.EntityManager;
        if (!BuffQuery.TryGet(world, out var query))
        {
            query = Query.Build(em, includeDisabled: false, includeSystems: false,
                ComponentIndex.Of<InteractBuff>(ComponentType.AccessMode.ReadOnly),
                ComponentIndex.Of<Buff>(ComponentType.AccessMode.ReadOnly),
                ComponentIndex.Of<SpellTarget>(ComponentType.AccessMode.ReadOnly),
                ComponentIndex.Of<EntityOwner>(ComponentType.AccessMode.ReadOnly));
            BuffQuery.Store(world, query);
        }
        var buffs = query.ToEntityArray(Allocator.Temp);
        try
        {
            Present.Clear();
            for (int i = 0; i < buffs.Length; i++)
            {
                var buff = buffs[i];
                long key = ((long)buff.Index << 32) | (uint)buff.Version;
                Present.Add(key);
                if (!Seen.Add(key) || !_primed) continue;
                // One buff that cannot be read must not stop the others from being checked.
                try { Check(world, em, buff); }
                catch (Exception e) { if (_failures++ < 4) Plugin.Logger?.LogWarning("ContainerOpenGate: interaction check threw: " + e.GetType().Name); }
            }
        }
        finally { buffs.Dispose(); }
        Seen.IntersectWith(Present);
        try { SweepExtras(em); }
        catch (Exception e) { if (_failures++ < 4) Plugin.Logger?.LogWarning("ContainerOpenGate: extra buff sweep threw: " + e.GetType().Name); }
    }

    private static void Check(World world, EntityManager em, Entity buff)
    {
        if (buff.Has<DestroyTag>(em)) return;
        byte* st = AiRuntime.RO<SpellTarget>(em, buff, Layout.SpellTargetTargetOffset + 8);
        if (st == null) return;
        var target = AiRuntime.ReadEntity(st + Layout.SpellTargetTargetOffset);
        if (target == Entity.Null || !em.Exists(target)) return;
        if (!TryGated(world, em, target, out var id, out bool notReady)) return;

        // The user the way the game resolves it for the inventory sync (InteractInventorySync_Spawn): the buffed character
        // (Buff.Target), then its ControlledBy.Controller. A servant's or an NPC's interaction (no player character) is not
        // gated; a player's whose user cannot be resolved is refused, closed.
        var character = Entity.Null;
        byte* b = AiRuntime.RO<Buff>(em, buff, Layout.BuffTargetOffset + 8);
        if (b != null) character = AiRuntime.ReadEntity(b + Layout.BuffTargetOffset);
        if (character == Entity.Null && buff.TryRead<EntityOwner>(em, out var owner)) character = owner.Owner;
        if (character != Entity.Null && em.Exists(character) && !character.Has<PlayerCharacter>(em)) return;
        Entity userEntity = Entity.Null;
        if (character != Entity.Null && em.Exists(character) && character.TryRead<ControlledBy>(em, out var controlled))
            userEntity = controlled.Controller;
        if (userEntity == Entity.Null || !em.Exists(userEntity) || !userEntity.Has<User>(em))
        {
            Close(em, buff, character);
            if (_unresolvedLogged++ < 4)
                Plugin.Logger?.LogWarning("ContainerOpenGate: closed an interaction with " + id + ": its player could not be resolved (the open is refused).");
            return;
        }
        var user = em.GetComponentData<User>(userEntity);

        // One interaction can bring more than one interact buff for the same container: they share the first one's answer.
        if (Recent.TryGet(user.PlatformId, id, Now, out bool allowedBefore))
        {
            if (!allowedBefore) Close(em, buff, character);
            return;
        }

        var request = new ContainerOpenRequest(userEntity, character, user.PlatformId, id, target, ContainerOpenKind.Interact, false);
        var outcome = notReady
            ? new ContainerGateOutcome<ContainerOpenRequest> { Verdict = ContainerOpenVerdict.Deny, Reason = ContainerGateText.NotReady }
            : Book.Evaluate(request, user.PlatformId, id, Now, (r, granted) => r.WithGrant(granted));
        Recent.Record(user.PlatformId, id, outcome.Verdict == ContainerOpenVerdict.Allow, Now);
        if (outcome.Verdict == ContainerOpenVerdict.Allow)
        {
            Extras.ForgetCharacter(Key(character));
            return;
        }

        Close(em, buff, character);
        if (outcome.Verdict == ContainerOpenVerdict.Deny)
        {
            Plugin.Logger?.LogInfo("ContainerOpenGate: closed user " + user.PlatformId + "'s interaction with " + id + ": refused"
                + (outcome.By != null ? " by " + outcome.By : "") + ".");
            SendDenied(world, user.Index, id, ContainerOpenKind.Interact, outcome.Reason, outcome.By);
            return;
        }
        Plugin.Logger?.LogInfo("ContainerOpenGate: closed user " + user.PlatformId + "'s interaction with " + id + " until " + outcome.Steps.Count + " prompt(s) are answered.");
        Begin(world, userEntity, user, id, target, ContainerOpenKind.Interact, request, outcome.Steps, null);
    }

    private static long Key(Entity e) => ((long)e.Index << 32) | (uint)e.Version;

    /// <summary>
    /// End a refused interaction the game's own way (its interact validation destroys the buff). A station's interaction
    /// can carry <c>AdditionalInteractBuff</c>: the game spawns an extra buff on the character for it in the same spawn
    /// reaction, and its destroy reaction removes that one when the interaction ends. The two spawn reactions have no
    /// order between them in the game's metadata, so the extra buff may not exist yet, or may be spawned on a buff that
    /// is already being destroyed: it is watched for a moment and destroyed too (<see cref="SweepExtras"/>).
    /// </summary>
    private static void Close(EntityManager em, Entity buff, Entity character)
    {
        int prefab = 0;
        Entity instance = Entity.Null;
        byte* extra = AiRuntime.RO<AdditionalInteractBuff>(em, buff, Layout.AdditionalInteractBuffInstanceOffset + 8);
        if (extra != null)
        {
            prefab = *(int*)(extra + Layout.AdditionalInteractBuffPrefabOffset);
            instance = AiRuntime.ReadEntity(extra + Layout.AdditionalInteractBuffInstanceOffset);
        }
        DestroyUtility.Destroy(em, buff, DestroyDebugReason.TryRemoveBuff);
        _closed++;
        if (prefab != 0 && character != Entity.Null)
        {
            Extras.Add(Key(character), prefab, instance == Entity.Null ? 0 : Key(instance), Now);
            SweepExtras(em);
        }
    }

    /// <summary>Destroy the extra buffs of refused station interactions (<see cref="Close"/>) while they are watched.</summary>
    private static void SweepExtras(EntityManager em)
    {
        if (Extras.Count == 0) return;
        Extras.Expire(Now);
        foreach (var w in Extras.Live())
        {
            var character = new Entity { Index = (int)(w.Character >> 32), Version = (int)(uint)w.Character };
            if (w.Instance != 0)
            {
                var known = new Entity { Index = (int)(w.Instance >> 32), Version = (int)(uint)w.Instance };
                if (em.Exists(known) && !known.Has<DestroyTag>(em)) DestroyUtility.Destroy(em, known, DestroyDebugReason.TryRemoveBuff);
            }
            if (!em.Exists(character) || !em.HasComponent(character, ComponentIndex.Of<BuffBuffer>(ComponentType.AccessMode.ReadOnly))) continue;
            var buffs = em.GetBuffer<BuffBuffer>(character, true);
            var found = new List<Entity>();
            for (int i = 0; i < buffs.Length; i++)
                if (buffs[i].PrefabGuid._Value == w.Prefab && buffs[i].Entity != Entity.Null) found.Add(buffs[i].Entity);
            foreach (var e in found)
                if (em.Exists(e) && !e.Has<DestroyTag>(em)) DestroyUtility.Destroy(em, e, DestroyDebugReason.TryRemoveBuff);
        }
    }

    /// <summary>
    /// Whether an interaction with <paramref name="target"/> is gated: a placed container, station or castle heart with a
    /// stable id. One whose id is not ready yet (just after a load) is gated and refused, closed; anything else (a player,
    /// a door, a world chest outside castles) is not gated.
    /// </summary>
    private static bool TryGated(World world, EntityManager em, Entity target, out ContainerId id, out bool notReady)
    {
        id = ContainerId.None;
        notReady = false;
        bool hasStorage = target.Has<CastleHeart>(em) || UnityInventoryBackend.HasInventory(em, target);
        if (!hasStorage) return false;
        if (InventoryRuntime.Installed)
        {
            id = InventoryRuntime.IdOf(target, out _);
            if (!id.IsNone) return true;
        }
        bool castleModel = target.Has<TileModel>(em) && CastleReader.PlotOf(world, target) != null;
        notReady = castleModel;
        return castleModel;
    }

    // ---- the preview seam -----------------------------------------------------------------------

    /// <summary>
    /// The remote preview asks (<see cref="ContainerPreviewServer"/>): null to go on, a refusal sentence to refuse, or
    /// <paramref name="held"/> when a prompt was sent; <paramref name="continueOpen"/> then runs once with the result
    /// (true: run the open again, the answers count; false and a sentence: refuse it).
    /// </summary>
    internal static string? CheckPreview(World world, Entity userEntity, User user, ContainerId id, Entity container,
        int previewSequence, Action<bool, string> continueOpen, out bool held)
    {
        held = false;
        if (!_installed || Book.Count == 0) return null;
        Entity character = Entity.Null;
        try { character = user.LocalCharacter._Entity; } catch { /* no character: Entity.Null */ }
        var request = new ContainerOpenRequest(userEntity, character, user.PlatformId, id, container, ContainerOpenKind.Preview, false);
        var outcome = Book.Evaluate(request, user.PlatformId, id, Now, (r, granted) => r.WithGrant(granted));
        switch (outcome.Verdict)
        {
            case ContainerOpenVerdict.Allow:
                return null;
            case ContainerOpenVerdict.Deny:
                Plugin.Logger?.LogInfo("ContainerOpenGate: refused user " + user.PlatformId + "'s preview of " + id
                    + (outcome.By != null ? " (" + outcome.By + ")" : "") + ".");
                return outcome.By != null ? ModMessagePrefix.Apply(outcome.By, outcome.Reason, true) : outcome.Reason;
            default:
                held = true;
                Plugin.Logger?.LogInfo("ContainerOpenGate: holding user " + user.PlatformId + "'s preview of " + id + " for " + outcome.Steps.Count + " prompt(s).");
                Begin(world, userEntity, user, id, container, ContainerOpenKind.Preview, request, outcome.Steps, continueOpen, previewSequence);
                return null;
        }
    }

    // ---- prompts and answers --------------------------------------------------------------------

    private static void Begin(World world, Entity userEntity, User user, ContainerId id, Entity container, ContainerOpenKind kind,
        ContainerOpenRequest request, List<ContainerGateStep<ContainerOpenRequest>> steps, Action<bool, string>? continueOpen,
        int previewSequence = 0)
    {
        if (PendingByPlatform.TryGetValue(user.PlatformId, out var previous)) End(previous, ok: false, ContainerGateText.Replaced);
        int sequence = _sequence = _sequence == int.MaxValue ? 1 : _sequence + 1;
        var state = new ContainerGatePending<ContainerOpenRequest>(sequence, user.PlatformId, id, kind, request, steps, Now);
        var pending = new PendingOpen(state, userEntity, user.Index, container, continueOpen) { PreviewSequence = previewSequence };
        PendingByPlatform[user.PlatformId] = pending;
        SendPrompt(world, pending);
    }

    private static unsafe void OnPacket(int _, Entity userEntity, User ignored, byte* payload, int length)
    {
        if (payload == null || length <= 0 || length > ContainerGateWire.MaxBytes) return;
        var p = ContainerGateWire.Decode(new ReadOnlySpan<byte>(payload, length), fromClient: true);
        if (p == null) return;
        var world = Worlds.Server;
        if (world == null || !world.IsCreated) return;
        var em = world.EntityManager;
        if (!userEntity.Exists(em) || !userEntity.Has<User>(em)) return;
        var user = em.GetComponentData<User>(userEntity);
        if (!PendingByPlatform.TryGetValue(user.PlatformId, out var pending) || pending.State.Sequence != p.Sequence) return;
        try
        {
            if (p.Kind == ContainerGateWire.KindCancel)
            {
                Plugin.Logger?.LogInfo("ContainerOpenGate: user " + user.PlatformId + " canceled the prompt for " + pending.State.Container + ".");
                End(pending, ok: false, ContainerGateText.Canceled, notify: false);
                return;
            }

            if (!Answers.TryTake(user.PlatformId, Now))
            {
                Plugin.Logger?.LogInfo("ContainerOpenGate: user " + user.PlatformId + " answered too often; refused unread.");
                End(pending, ok: false, ContainerGateText.TooMany);
                return;
            }

            var step = pending.State.Current;
            var result = pending.State.Answer(p.Sequence, new ChallengeSecret(p.Text), Book, Now, out string reason, p.Remember);
            p.Text = "";
            string by = step.Handler.PluginName;
            switch (result)
            {
                case ContainerGateAnswerStep.Next:
                    Plugin.Logger?.LogInfo("ContainerOpenGate: user " + user.PlatformId + " answered " + by + " for " + pending.State.Container + "; next prompt.");
                    SendPrompt(world, pending);
                    return;
                case ContainerGateAnswerStep.Done:
                    Plugin.Logger?.LogInfo("ContainerOpenGate: user " + user.PlatformId + " answered " + by + " for " + pending.State.Container + "; the open goes on.");
                    Recent.Forget(user.PlatformId);
                    End(pending, ok: true, "");
                    return;
                case ContainerGateAnswerStep.Failed:
                    Plugin.Logger?.LogInfo("ContainerOpenGate: user " + user.PlatformId + "'s answer for " + pending.State.Container + " was refused by " + by + ".");
                    End(pending, ok: false, reason, by: by);
                    return;
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("ContainerOpenGate: " + p.Kind + " threw: " + e.GetType().Name);
            End(pending, ok: false, ContainerGateText.CheckFailed);
        }
    }

    /// <summary>
    /// Finish an open that waited for answers. Right: a preview runs its open again; an interaction tells the client to
    /// repeat it (the pass lets it through). Wrong, cancelled, timed out, replaced: a preview is refused; the client reads
    /// the sentence as a toast.
    /// </summary>
    private static void End(PendingOpen pending, bool ok, string message, bool notify = true, string? by = null, bool runContinuation = true)
    {
        if (PendingByPlatform.TryGetValue(pending.State.Platform, out var current) && ReferenceEquals(current, pending))
            PendingByPlatform.Remove(pending.State.Platform);
        if (pending.Ended) return;
        pending.Ended = true;
        var world = Worlds.Server;
        bool live = world != null && world.IsCreated;
        int netIndex = 0, netGeneration = 0;
        bool retry = ok && pending.State.Kind == ContainerOpenKind.Interact;
        // Without the words the client cannot find the container to repeat the interaction; it shows a toast instead.
        if (retry && live && InventoryRuntime.Installed)
            InventoryRuntime.TryGetNetworkId(pending.State.Container, out netIndex, out netGeneration, out _, out _);
        if (notify && live)
        {
            try
            {
                Send(world!, pending.UserIndex, new ContainerGatePacket
                {
                    Kind = ContainerGateWire.KindResult, Sequence = pending.State.Sequence, Container = pending.State.Container.ToString(),
                    OpenKind = (int)pending.State.Kind, Ok = ok, Retry = retry, NetIndex = netIndex, NetGeneration = netGeneration,
                    Message = ContainerGateText.Clip(message), Mod = by ?? "",
                });
            }
            catch (Exception e) { Plugin.Logger?.LogWarning("ContainerOpenGate: result send threw: " + e.Message); }
        }
        if (pending.Continue != null && runContinuation)
        {
            try { pending.Continue(ok, ok ? "" : by != null ? ModMessagePrefix.Apply(by, message, true) : message); }
            catch (Exception e) { Plugin.Logger?.LogWarning("ContainerOpenGate: the preview's continuation threw: " + e.Message); }
        }
    }

    /// <summary>The client closed preview <paramref name="previewSequence"/> while its prompt was up: drop the prompt, open nothing.</summary>
    internal static void CancelPreview(ulong platform, int previewSequence)
    {
        if (!PendingByPlatform.TryGetValue(platform, out var p)) return;
        if (p.State.Kind != ContainerOpenKind.Preview || p.PreviewSequence != previewSequence) return;
        Plugin.Logger?.LogInfo("ContainerOpenGate: user " + platform + " closed the preview of " + p.State.Container + " while its prompt was up.");
        // The client already dropped the preview; its prompt closes on the result.
        End(p, ok: false, ContainerGateText.Canceled, notify: true, by: null, runContinuation: false);
    }

    private static void ExpirePending()
    {
        if (PendingByPlatform.Count == 0) return;
        double now = Now;
        foreach (var p in new List<PendingOpen>(PendingByPlatform.Values))
        {
            if (!p.State.Expired(now)) continue;
            Plugin.Logger?.LogInfo("ContainerOpenGate: user " + p.State.Platform + "'s prompt for " + p.State.Container + " timed out.");
            End(p, ok: false, ContainerGateText.TimedOut);
        }
    }

    private static void SendPrompt(World world, PendingOpen pending)
    {
        var step = pending.State.Current;
        Send(world, pending.UserIndex, new ContainerGatePacket
        {
            Kind = ContainerGateWire.KindPrompt, Sequence = pending.State.Sequence, Container = pending.State.Container.ToString(),
            OpenKind = (int)pending.State.Kind, Title = step.Decision.Title, Body = step.Decision.Prompt,
            MaxLength = step.Decision.MaxLength, Mod = step.Handler.PluginName, Remember = step.Decision.OfferRemember,
        });
    }

    private static void SendDenied(World world, int userIndex, ContainerId id, ContainerOpenKind kind, string reason, string? by)
    {
        try
        {
            Send(world, userIndex, new ContainerGatePacket
            {
                Kind = ContainerGateWire.KindDenied, Container = id.ToString(), OpenKind = (int)kind,
                Message = ContainerGateText.Clip(reason), Mod = by ?? "",
            });
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("ContainerOpenGate: denied send threw: " + e.Message); }
    }

    private static void Send(World world, int userIndex, ContainerGatePacket packet)
    {
        var bootstrap = world.GetExistingSystemManaged<ServerBootstrapSystem>();
        if (bootstrap == null) return;
        CustomEvents.SendToUser(bootstrap, userIndex, ContainerGateWire.EventId, new ReadOnlySpan<byte>(ContainerGateWire.Encode(packet)));
    }

    /// <summary>Dev bridge status (no typed text, no tokens).</summary>
    internal static Dictionary<string, object?> Describe()
    {
        var handlers = new List<object?>();
        foreach (var h in Book.Handlers) handlers.Add(h.PluginName);
        var pending = new List<object?>();
        foreach (var p in PendingByPlatform.Values)
            pending.Add(new Dictionary<string, object?>
            {
                ["user"] = p.State.Platform.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["sequence"] = p.State.Sequence,
                ["container"] = p.State.Container.ToString(),
                ["kind"] = p.State.Kind.ToString(),
                ["prompt"] = (p.State.Index + 1) + " of " + p.State.StepCount,
                ["by"] = p.State.Current.Handler.PluginName,
                ["secondsLeft"] = Math.Round(Math.Max(0, p.State.Deadline - Now), 1),
            });
        return new Dictionary<string, object?>
        {
            ["installed"] = _installed,
            ["handlers"] = handlers,
            ["hooked"] = _hook != null,
            ["grants"] = Book.GrantCount,
            ["passes"] = Book.PassCount,
            ["pending"] = pending,
            ["closedInteractions"] = _closed,
        };
    }

    /// <summary>One player's open that waits for answers, with what the server needs to finish it.</summary>
    private sealed class PendingOpen
    {
        internal PendingOpen(ContainerGatePending<ContainerOpenRequest> state, Entity user, int userIndex, Entity container, Action<bool, string>? continueOpen)
        {
            State = state;
            User = user;
            UserIndex = userIndex;
            Container = container;
            Continue = continueOpen;
        }

        internal ContainerGatePending<ContainerOpenRequest> State { get; }
        internal Entity User { get; }
        internal int UserIndex { get; }
        internal Entity Container { get; }
        internal Action<bool, string>? Continue { get; }
        internal bool Ended { get; set; }

        /// <summary>A held preview: the client's preview number (0 for an interaction).</summary>
        internal int PreviewSequence { get; init; }
    }
}
#endif
