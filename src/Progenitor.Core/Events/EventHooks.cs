using System;
using System.Collections.Generic;
using HarmonyLib;
using ProjectM;
using ProjectM.Network;
using Progenitor.Patches;
using Progenitor.Reload;
using Progenitor.WorldState;
using Stunlock.Network;
using Unity.Collections;
using Unity.Entities;

namespace Progenitor.Events;

/// <summary>
/// The game hooks behind the event bus. Installed once by Core. Each publishes a typed event and
/// otherwise leaves the game alone, so the same seam never needs a second patch from a mod.
///
/// <para><b>Three routes per site, in this order</b> (job FHARM7). A Bootstrap-owned typed slot
/// (<see cref="HarmonySlotHandoff"/>) is the production path and pins nothing. When Bootstrap
/// does not own the site, the fallback used to hand HarmonyX a method out of this collectible
/// Core ALC, which pins the generation for the life of the process even after unpatch
/// (<c>harmony-patches.md</c> section 8) — on every boot against a Bootstrap one version behind,
/// silently. None of the three bodies here needs <c>__result</c> or a prefix-to-postfix
/// <c>__state</c>, so the fallback now registers through the <b>same</b>
/// <c>Progenitor.Bootstrap.PatchSlots</c> pool the plugin patches use, under the Core owner id;
/// the direct <c>Harmony.Patch</c> survives only for a Core running with no Bootstrap at all,
/// where there is no pool and no hot swap to protect, and it says so in one warning.
/// See <see cref="CorePatchFallbackPolicy"/>.</para>
/// </summary>
internal static class EventHooks
{
    /// <summary>
    /// Owner id the slot-routed fallbacks register under: Core's own generation, shared with
    /// <c>WorldLoadHook</c> and god mode (job FHARM6). One lifetime, one sweep, one id.
    /// </summary>
    internal const string OwnerId = MyPluginInfo.PLUGIN_GUID + PatchSitePolicy.CoreOwnerSuffix;

    private static Harmony? _harmony;

    /// <summary>
    /// Which worlds a <see cref="WorldReady"/> has already been published for, keyed on
    /// <c>World.SequenceNumber</c> rather than on the name (job FEVT1, R44 finding 3). A name is
    /// not an identity: <c>Client_0</c> is destroyed and rebuilt on every disconnect, rejoin and
    /// server change, and a destroy-and-create that straddles one of our frames left the name in
    /// the old set forever. See <see cref="WorldReadyLedger"/>.
    /// </summary>
    private static readonly WorldReadyLedger ReadyWorlds = new();

    /// <summary>
    /// Whether the "could not read a world's sequence number" line has been logged in this Core
    /// generation. Core-static evidence over a Core-static effect (the ledger it guards is one
    /// too), and <see cref="Uninstall"/> re-arms it with the ledger — the shape R44 finding 4 is
    /// about, applied here so a throwing getter costs one line per generation rather than one per
    /// frame or one per process.
    /// </summary>
    private static bool _sequenceReadWarned;

    private static bool _watching;

    public static void Install(Harmony? harmony)
    {
        if (HarmonySlotHandoff.ShouldSkipModule(HarmonySlotHandoff.EventsModule))
        {
            Plugin.Logger?.LogInfo(HarmonySlotHandoff.SkipModuleLog(HarmonySlotHandoff.EventsModule));
            return;
        }

        _harmony = harmony;
        if (HarmonySlotHandoff.SkipCoreHarmony)
        {
            Plugin.Frame += WatchWorlds;
            _watching = true;
            return;
        }

        ApplySite(
            HarmonySlotHandoff.ChatOnUpdate,
            new ChatOnUpdate(ChatHook.Prefix),
            () => FallbackChat(harmony));
        if (Plugin.IsServerProcess)
        {
            ApplySite(
                HarmonySlotHandoff.UserConnected,
                new UserConnected(ConnectionHooks.Connected),
                () => FallbackConnected(harmony));
            ApplySite(
                HarmonySlotHandoff.UserDisconnected,
                new UserDisconnected(SlotDisconnected),
                () => FallbackDisconnected(harmony));
        }

        Plugin.Frame += WatchWorlds;
        _watching = true;
    }

    /// <summary>
    /// What this module registered that can outlive a collectible Core generation
    /// (Plugin.Frame, Harmony slots, the chat EntityQuery).
    /// </summary>
    internal static string DescribePins()
    {
        var holds = new List<string>();
        try
        {
            if (_watching) holds.Add("Plugin.Frame");
            if (_harmony != null) holds.Add("HarmonyFallback");
            if (ReadyWorlds.Count > 0) holds.Add("ReadyWorlds=" + ReadyWorlds.Count);
            if (ChatHook.HasLiveQuery) holds.Add("ChatQuery");
        }
        catch (Exception e)
        {
            holds.Add("threw:" + e.Message);
        }
        return HarmonySlotHandoff.FormatModulePins(HarmonySlotHandoff.EventsModule, holds);
    }

    public static void Uninstall()
    {
        Plugin.Frame -= WatchWorlds;
        _watching = false;
        ReadyWorlds.Clear();
        _sequenceReadWarned = false;
        ChatHook.ResetQuery();
        HarmonySlotHandoff.Clear(HarmonySlotHandoff.ChatOnUpdate);
        HarmonySlotHandoff.Clear(HarmonySlotHandoff.UserConnected);
        HarmonySlotHandoff.Clear(HarmonySlotHandoff.UserDisconnected);
    }

    /// <summary>
    /// WorldReady detection: cheap name lookups once per frame; fires on each creation, including
    /// client rejoins.
    ///
    /// <para>The name finds the world; <c>World.SequenceNumber</c> decides whether it is one we
    /// have already published for (job FEVT1, R44 finding 3). We sample the game's world list on
    /// <c>Plugin.Frame</c>, a cadence the game does not share, so a client that destroys
    /// <c>Client_0</c> and creates a new one between two of our samples presents the same name
    /// twice — and the old name-keyed set published nothing for the world that actually existed,
    /// leaving every mod that initializes on this event uninitialized for the whole session. The
    /// sequence number is assigned once per <c>World</c> and never reused, which is why it is the
    /// right identity for a create/destroy even though <see cref="WorldEpoch"/> rejects it as an
    /// edge for a save reload (that runs inside the existing world). Same rule
    /// <c>ChatHook.GetQuery</c> uses one level down.</para>
    /// </summary>
    private static void WatchWorlds()
    {
        Check(Worlds.ServerName, true);
        Check(Worlds.ClientName, false);

        static void Check(string name, bool isServer)
        {
            var world = Worlds.Find(name);
            if (world == null)
            {
                ReadyWorlds.NoteGone(name);
                return;
            }
            if (!ReadyWorlds.NoteFound(name, ReadSequence(name, world))) return;
            Plugin.Logger?.LogInfo($"World ready: {name}");
            EventBus.Publish(new WorldReady(world, isServer));
        }
    }

    /// <summary>
    /// <c>world.SequenceNumber</c>, or <see cref="WorldReadyLedger.UnknownSequence"/> when the
    /// Il2Cpp getter throws. This runs on the game main thread inside a frame callback, so it must
    /// not propagate; <see cref="WorldReadyLedger"/> documents what an unknown identity costs (the
    /// old name-only behavior for that name, never a per-frame publish storm).
    /// </summary>
    private static ulong ReadSequence(string name, World world)
    {
        try
        {
            return world.SequenceNumber;
        }
        catch (Exception e)
        {
            if (!_sequenceReadWarned)
            {
                _sequenceReadWarned = true;
                try
                {
                    Plugin.Logger?.LogWarning(
                        $"World ready: reading {name}'s SequenceNumber threw, so a destroy-and-create "
                        + $"under that name cannot be told apart this generation: {e.Message}");
                }
                catch { /* a logger that throws must not fail the frame */ }
            }
            return WorldReadyLedger.UnknownSequence;
        }
    }

    /// <summary>Resolves the user entity for a connection id from the server bootstrap system's approved-user table.</summary>
    internal static bool TryFindUser(ServerBootstrapSystem bootstrap, NetConnectionId connectionId, out Entity userEntity, out User user)
    {
        userEntity = Entity.Null;
        user = default;
        var clients = bootstrap._ApprovedUsersLookup;
        if (clients == null) return false;
        var em = bootstrap.EntityManager;
        for (int i = 0; i < clients.Length; i++)
        {
            var client = clients[i];
            if (client == null || !client.Exists || !client.NetConnectionId.Equals(connectionId)) continue;
            userEntity = client.UserEntity;
            if (!em.Exists(userEntity) || !em.HasComponent<User>(userEntity)) return false;
            user = em.GetComponentData<User>(userEntity);
            return true;
        }
        return false;
    }

    internal delegate void ChatOnUpdate(ChatMessageSystem instance);
    internal delegate void UserConnected(ServerBootstrapSystem instance, NetConnectionId netConnectionId);
    internal delegate void UserDisconnected(ServerBootstrapSystem instance, NetConnectionId netConnectionId, string reason, string extra);

    private static void ApplySite(string site, Delegate dispatcher, Action fallback)
    {
        switch (HarmonySlotHandoff.Bind(site, dispatcher))
        {
            case HarmonySlotHandoff.SiteBind.Filled:
                return;
            case HarmonySlotHandoff.SiteBind.Fallback:
                fallback();
                return;
            case HarmonySlotHandoff.SiteBind.Inert:
                Plugin.Logger?.LogError(HarmonySlotHandoff.InertLog("EventHooks", site));
                return;
            case HarmonySlotHandoff.SiteBind.Vanilla:
                Plugin.Logger?.LogError(HarmonySlotHandoff.VanillaLog("EventHooks", site));
                return;
        }
    }

    private static Harmony RequireHarmony(Harmony? harmony)
    {
        if (harmony != null)
        {
            _harmony = harmony;
            return harmony;
        }
        var created = Reload.HarmonyFallback.Ensure();
        _harmony = created;
        return created;
    }

    /// <summary>
    /// Job FHARM7: which route this fallback takes, with the line that names the pin when the
    /// answer is <see cref="CoreFallbackRoute.Direct"/>. Every site here fits the slot shape, so
    /// <see cref="CoreFallbackRoute.Refused"/> is unreachable and is handled only so a future
    /// site that does not fit cannot fall through to a silent direct patch.
    /// </summary>
    private static CoreFallbackRoute Route(string site)
    {
        var route = CorePatchFallbackPolicy.Decide(
            slotShapeFits: true, bootstrapPresent: PatchRegistry.SlotsAvailable);
        if (route == CoreFallbackRoute.Direct)
        {
            Plugin.Logger?.LogWarning(CorePatchFallbackPolicy.FormatDirect(
                CorePatchFallbackPolicy.ModuleEvents, site, WorldEpoch.CoreGenerationOrZero()));
        }
        else if (route == CoreFallbackRoute.Refused)
        {
            Plugin.Logger?.LogError(CorePatchFallbackPolicy.FormatRefused(
                CorePatchFallbackPolicy.ModuleEvents, site));
        }
        return route;
    }

    /// <summary>One line when a slot-routed fallback did not register (the registry logged why).</summary>
    private static void NoteSlotRefused(string site) =>
        Plugin.Logger?.LogError(
            "EventHooks: the slot-routed fallback for " + site + " was refused (see the patches: "
            + "line above); this seam is off for this Core generation.");

    private static void FallbackChat(Harmony? harmony)
    {
        Plugin.Logger?.LogInfo(HarmonySlotHandoff.FallbackSiteLog(HarmonySlotHandoff.ChatOnUpdate));
        try
        {
            var original = AccessTools.Method(typeof(ChatMessageSystem), nameof(ChatMessageSystem.OnUpdate));
            if (original == null)
            {
                Plugin.Logger?.LogError("EventHooks: ChatMessageSystem.OnUpdate was not found on the interop type.");
                return;
            }

            var route = Route(HarmonySlotHandoff.ChatOnUpdate);
            if (route == CoreFallbackRoute.Refused) return;
            if (route == CoreFallbackRoute.Slot)
            {
                // The body reads nothing off the system but its EntityManager, which is the
                // server world's, so the slot's missing __instance costs nothing here.
                Progenitor.Patches.Patches.Install();
                if (PatchRegistry.PrefixSlot(
                        owner: null, ownerId: OwnerId, target: original,
                        prefix: static (_, _) => { ChatHook.Run(); return true; },
                        priority: 300) == null)
                {
                    NoteSlotRefused(HarmonySlotHandoff.ChatOnUpdate);
                }
                return;
            }

            RequireHarmony(harmony).Patch(
                original,
                prefix: new HarmonyMethod(typeof(ChatHook), nameof(ChatHook.Prefix)) { priority = 300 });
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"EventHooks: fallback patch {HarmonySlotHandoff.ChatOnUpdate} threw: {e}");
        }
    }

    private static void FallbackConnected(Harmony? harmony)
    {
        Plugin.Logger?.LogInfo(HarmonySlotHandoff.FallbackSiteLog(HarmonySlotHandoff.UserConnected));
        try
        {
            var original = AccessTools.Method(typeof(ServerBootstrapSystem), nameof(ServerBootstrapSystem.OnUserConnected));
            if (original == null)
            {
                Plugin.Logger?.LogError("EventHooks: ServerBootstrapSystem.OnUserConnected was not found on the interop type.");
                return;
            }

            var route = Route(HarmonySlotHandoff.UserConnected);
            if (route == CoreFallbackRoute.Refused) return;
            if (route == CoreFallbackRoute.Slot)
            {
                // __args is positional; bind netConnectionId by the original's own parameter
                // name, the binding Harmony was doing. A missing name refuses rather than reads
                // position (job FHARM6's rule, kept here).
                int idx = PatchRegistry.ArgIndex(original, "netConnectionId");
                if (idx < 0)
                {
                    Plugin.Logger?.LogError(MissingArg(HarmonySlotHandoff.UserConnected, "netConnectionId"));
                    return;
                }
                Progenitor.Patches.Patches.Install();
                if (PatchRegistry.PostfixSlot(
                        owner: null, ownerId: OwnerId, target: original,
                        postfix: (instance, args) => SlotConnected(instance, args, idx)) == null)
                {
                    NoteSlotRefused(HarmonySlotHandoff.UserConnected);
                }
                return;
            }

            RequireHarmony(harmony).Patch(
                original,
                postfix: new HarmonyMethod(typeof(ConnectionHooks), nameof(ConnectionHooks.Connected)));
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"EventHooks: fallback patch {HarmonySlotHandoff.UserConnected} threw: {e}");
        }
    }

    private static void FallbackDisconnected(Harmony? harmony)
    {
        Plugin.Logger?.LogInfo(HarmonySlotHandoff.FallbackSiteLog(HarmonySlotHandoff.UserDisconnected));
        try
        {
            var original = AccessTools.Method(typeof(ServerBootstrapSystem), nameof(ServerBootstrapSystem.OnUserDisconnected));
            if (original == null)
            {
                Plugin.Logger?.LogError("EventHooks: ServerBootstrapSystem.OnUserDisconnected was not found on the interop type.");
                return;
            }

            var route = Route(HarmonySlotHandoff.UserDisconnected);
            if (route == CoreFallbackRoute.Refused) return;
            if (route == CoreFallbackRoute.Slot)
            {
                int idConnection = PatchRegistry.ArgIndex(original, "netConnectionId");
                int idReason = PatchRegistry.ArgIndex(original, "connectionStatusReason");
                if (idConnection < 0 || idReason < 0)
                {
                    Plugin.Logger?.LogError(MissingArg(
                        HarmonySlotHandoff.UserDisconnected,
                        idConnection < 0 ? "netConnectionId" : "connectionStatusReason"));
                    return;
                }
                Progenitor.Patches.Patches.Install();
                if (PatchRegistry.PrefixSlot(
                        owner: null, ownerId: OwnerId, target: original,
                        prefix: (instance, args) => SlotDisconnectedArgs(instance, args, idConnection, idReason)) == null)
                {
                    NoteSlotRefused(HarmonySlotHandoff.UserDisconnected);
                }
                return;
            }

            RequireHarmony(harmony).Patch(
                original,
                prefix: new HarmonyMethod(typeof(ConnectionHooks), nameof(ConnectionHooks.Disconnected)));
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"EventHooks: fallback patch {HarmonySlotHandoff.UserDisconnected} threw: {e}");
        }
    }

    private static string MissingArg(string site, string name) =>
        "EventHooks: " + site + " has no parameter named '" + name + "' on this build, so the "
        + "slot-routed fallback cannot bind it; the seam is left unpatched rather than reading an "
        + "argument by position. Check the interop signature against the game's metadata.";

    /// <summary>Slot body for <c>OnUserConnected</c>: <c>__instance</c> plus one bound argument.</summary>
    private static void SlotConnected(object? instance, object?[] args, int idConnection)
    {
        try
        {
            if (instance is not ServerBootstrapSystem bootstrap) return;
            if (args == null || idConnection >= args.Length) return;
            if (args[idConnection] is not NetConnectionId id) return;
            ConnectionHooks.Connected(bootstrap, id);
        }
        catch (Exception e) { Plugin.Logger?.LogError($"PlayerConnected hook: {e}"); }
    }

    /// <summary>Slot body for <c>OnUserDisconnected</c>. Returns true: the original always runs.</summary>
    private static bool SlotDisconnectedArgs(object? instance, object?[] args, int idConnection, int idReason)
    {
        try
        {
            if (instance is not ServerBootstrapSystem bootstrap) return true;
            if (args == null || idConnection >= args.Length || idReason >= args.Length) return true;
            if (args[idConnection] is not NetConnectionId id) return true;
            string reason = args[idReason]?.ToString() ?? "";
            SlotDisconnected(bootstrap, id, reason, "");
        }
        catch (Exception e) { Plugin.Logger?.LogError($"PlayerDisconnected hook: {e}"); }
        return true;
    }

    private static void SlotDisconnected(ServerBootstrapSystem instance, NetConnectionId netConnectionId, string reason, string extra)
    {
        try
        {
            var world = Worlds.Server;
            if (world == null) return;
            if (!TryFindUser(instance, netConnectionId, out var entity, out var user)) return;
            EventBus.Publish(new PlayerDisconnected(world, entity, user, reason));
            _ = extra;
        }
        catch (Exception e) { Plugin.Logger?.LogError($"PlayerDisconnected hook: {e}"); }
    }

    private static class ConnectionHooks
    {
        [HarmonyPatch(typeof(ServerBootstrapSystem), nameof(ServerBootstrapSystem.OnUserConnected))]
        [HarmonyPostfix]
        internal static void Connected(ServerBootstrapSystem __instance, NetConnectionId netConnectionId)
        {
            // NODESCCHAT: put back the description held out of the game's join chat line, before anything else.
            Progenitor.Networking.JoinDescriptionChatHook.AfterUserConnected();
            try
            {
                var world = Worlds.Server;
                if (world == null) return;
                if (!TryFindUser(__instance, netConnectionId, out var entity, out var user)) return;
                EventBus.Publish(new PlayerConnected(world, entity, user));
            }
            catch (Exception e) { Plugin.Logger?.LogError($"PlayerConnected hook: {e}"); }
        }

        [HarmonyPatch(typeof(ServerBootstrapSystem), nameof(ServerBootstrapSystem.OnUserDisconnected))]
        [HarmonyPrefix]
        internal static void Disconnected(ServerBootstrapSystem __instance, NetConnectionId netConnectionId, ConnectionStatusChangeReason connectionStatusReason, string extraData)
        {
            try
            {
                var world = Worlds.Server;
                if (world == null) return;
                if (!TryFindUser(__instance, netConnectionId, out var entity, out var user)) return;
                EventBus.Publish(new PlayerDisconnected(world, entity, user, connectionStatusReason.ToString()));
            }
            catch (Exception e) { Plugin.Logger?.LogError($"PlayerDisconnected hook: {e}"); }
        }
    }

    /// <summary>
    /// Runs before another chat-command framework (typically priority 200) so commands handled here never reach its
    /// "command not found" path. Messages a subscriber cancels are destroyed before the game sees them.
    /// </summary>
    [HarmonyPriority(300)]
    private static class ChatHook
    {
        // Cached query is bound to one World. WorldReady fires again after recreate / rejoin;
        // using a query whose EntityManager is gone is a native crash inside Entities.
        // Identity is SequenceNumber, not the C# wrapper: Il2Cpp list accessors can return a
        // new wrapper for the same native World, and `==` would then rebuild every tick.
        // Dispose only while that World is still created — World.Dispose already frees queries.
        private static EntityQuery _query;
        private static World? _queryWorld;
        private static ulong _queryWorldSeq;
        private static bool _hasQuery;

        internal static bool HasLiveQuery => _hasQuery;

        internal static void ResetQuery()
        {
            if (!_hasQuery) return;
            _hasQuery = false;
            var oldWorld = _queryWorld;
            _queryWorld = null;
            _queryWorldSeq = 0;
            bool worldAlive = false;
            try { worldAlive = oldWorld != null && oldWorld.IsCreated; }
            catch { worldAlive = false; }
            if (worldAlive)
            {
                try { _query.Dispose(); }
                catch (Exception e) { Plugin.Logger?.LogError($"ChatMessage query dispose: {e}"); }
            }
            _query = default;
        }

        private static EntityQuery GetQuery(World world, EntityManager em)
        {
            ulong seq = world.SequenceNumber;
            if (_hasQuery && _queryWorldSeq == seq)
                return _query;
            ResetQuery();
            _query = em.CreateEntityQuery(ComponentType.ReadOnly<ChatMessageEvent>(), ComponentType.ReadOnly<FromCharacter>());
            _queryWorld = world;
            _queryWorldSeq = seq;
            _hasQuery = true;
            return _query;
        }

        [HarmonyPatch(typeof(ChatMessageSystem), nameof(ChatMessageSystem.OnUpdate))]
        [HarmonyPrefix]
        internal static void Prefix(ChatMessageSystem __instance)
        {
            // __instance is matched only so the direct fallback keeps Harmony's shape; the body
            // takes its EntityManager off the server world, which is what lets the same body run
            // from a Bootstrap slot that has no __instance (job FHARM7).
            _ = __instance;
            Run();
        }

        /// <summary>The body, independent of <c>__instance</c>. See <see cref="Prefix"/>.</summary>
        internal static void Run()
        {
            if (!Plugin.IsServerProcess) return;
            bool subscribed = EventBus.SubscriberCount<ChatMessage>() > 0;
            // CHAT4: the server's word filter and Discord relay (Server tab → Chat) run after the subscribers.
            bool moderated = Progenitor.Chat.ServerChat.Active;
            if (!subscribed && !moderated) return;
            try
            {
                var world = Worlds.Server;
                if (world == null || !world.IsCreated)
                {
                    ResetQuery();
                    return;
                }
                var em = world.EntityManager;
                var query = GetQuery(world, em);
                var entities = query.ToEntityArray(Allocator.Temp);
                try
                {
                    for (int i = 0; i < entities.Length; i++)
                    {
                        var entity = entities[i];
                        var chat = em.GetComponentData<ChatMessageEvent>(entity);
                        var from = em.GetComponentData<FromCharacter>(entity);
                        if (!em.Exists(from.User) || !em.HasComponent<User>(from.User)) continue;
                        var user = em.GetComponentData<User>(from.User);
                        string text = chat.MessageText.ToString();
                        if (subscribed)
                        {
                            var ev = EventBus.Publish(new ChatMessage(world, from.User, from.Character, user, text, chat.MessageType));
                            if (ev.IsCancelled)
                            {
                                em.DestroyEntity(entity);
                                continue;
                            }
                        }

                        if (!moderated) continue;
                        var (block, replacement) = Progenitor.Chat.ServerChat.Moderate(world, from.User, user.CharacterName.ToString(), text, chat.MessageType);
                        if (block)
                        {
                            em.DestroyEntity(entity);
                            continue;
                        }

                        if (replacement != null)
                        {
                            // Replace in place, before the game's job picks the recipients: Global, Local, Team, Whisper and the
                            // game's mute rules stay the game's own. The same typed write the Dev bridge's command op uses.
                            chat.MessageText = new FixedString512Bytes(Progenitor.Chat.ChatLogic.TruncateUtf8(replacement));
                            em.SetComponentData(entity, chat);
                        }
                    }
                }
                finally
                {
                    entities.Dispose();
                }
            }
            catch (Exception e) { Plugin.Logger?.LogError($"ChatMessage hook: {e}"); }
        }
    }
}
