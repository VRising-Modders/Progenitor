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
    private static BepInEx.Configuration.ConfigEntry<string>? _mode;
    private static double _nextCheck;

    internal static int ActiveCount => ByUser.Count;

    /// <summary>Dev bridge: ended previews whose releases still retry, records awaiting the startup sweep, persisted records.</summary>
    internal static (int Releasing, int SweepPending, int Records) SyncBookkeeping => (Releasing.Count, SweepPending.Count, _records.Records.Count);

    // Inventory sync: previews whose lease ended but whose releases still retry, the persisted records of previews that
    // may hold entries (for a startup sweep after a run that did not end cleanly), and the records loaded for that sweep.
    private static readonly List<ContainerPreviewSync> Releasing = new();
    private static ContainerPreviewSyncRecords _records = new();
    private static readonly List<ContainerPreviewSyncRecord> SweepPending = new();
    private static Persistence.IModStore? _store;
    private static IDisposable? _storeReady;
    private static double _sweepSince = double.NaN;
    private static bool _storeLoaded;
    private static readonly Dictionary<ContainerPreviewSyncRecord, int> SweepFailures = new();

    /// <summary>A record whose sweep keeps throwing is left for the next start after this many tries.</summary>
    internal const int MaxSweepAttempts = 5;

    internal static void Install()
    {
        if (_installed || !Plugin.IsServerProcess) return;
        _installed = true;
        CustomEvents.OnReceiveFromClient(ContainerPreviewWire.EventId, OnRequest);
        Plugin.Frame += Tick;
        // Only a world save suspends the sync entries (the shared guard; the other ModSave.SaveAll callers write only
        // Progenitor's JSON).
        Persistence.WorldSaveGuard.Suspend += OnSaveSuspend;
        Persistence.WorldSaveGuard.Resume += OnSaveResume;
        try
        {
            _store = Persistence.ModSave.Store(ContainerPreviewSyncRecords.StoreId);
            _storeReady = _store.WhenReady(store =>
            {
                if (!_installed) return;
                var loaded = store.Get<ContainerPreviewSyncRecords>(ContainerPreviewSyncRecords.Key) ?? new ContainerPreviewSyncRecords();
                loaded.Records ??= new List<ContainerPreviewSyncRecord>();
                loaded.Records.RemoveAll(r => r == null);
                SweepPending.Clear();
                SweepPending.AddRange(loaded.Records);
                foreach (var r in _records.Records) loaded.Add(r.Container, r.Platform);   // this run's previews, if any started first
                _records = loaded;
                _storeLoaded = true;
                SaveRecords();
                _sweepSince = Now;
                if (SweepPending.Count > 0)
                    Plugin.Logger?.LogInfo("ContainerPreview: " + SweepPending.Count + " inventory sync record(s) from an earlier run to sweep.");
            });
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("ContainerPreview: the sync record store threw: " + e.Message); }
        try
        {
            Config.SharedConfig.BindWhenAvailable(
                "[" + ContainerPreviewSettings.Section + "] " + ContainerPreviewSettings.ModeKey,
                file =>
                {
                    if (!_installed) return;
                    _mode = Config.ConfigHelper.Bind(file, ContainerPreviewSettings.Section, ContainerPreviewSettings.ModeKey,
                        ContainerPreviewSettings.DefaultWord, ContainerPreviewSettings.ConfigDescription);
                    Plugin.Logger?.LogInfo("ContainerPreview: [" + ContainerPreviewSettings.Section + "] " + ContainerPreviewSettings.ModeKey
                        + " = " + _mode.Value + ".");
                });
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("ContainerPreview: binding the setting threw: " + e.Message + "; the default applies."); }
    }

    internal static void Uninstall()
    {
        if (!_installed) return;
        _installed = false;
        _mode = null;
        try { CustomEvents.RemoveClientHandler(ContainerPreviewWire.EventId); }
        catch (Exception e) { Plugin.Logger?.LogWarning("ContainerPreview: handler removal threw: " + e.Message); }
        Plugin.Frame -= Tick;
        Persistence.WorldSaveGuard.Suspend -= OnSaveSuspend;
        Persistence.WorldSaveGuard.Resume -= OnSaveResume;
        foreach (var holder in new List<Holder>(ByUser.Values)) holder.End(CameraObservationEndReason.Unloaded, notify: false, message: "");
        ByUser.Clear();
        // One last try for releases still pending; whatever is left keeps its record for the next run's sweep.
        foreach (var sync in new List<ContainerPreviewSync>(Releasing))
        {
            try { sync.Tick(Now); } catch { /* the record stays */ }
            if (sync.Done && PendingRecord.TryGetValue(sync, out var rec)) Released(sync, rec.Container, rec.Platform);
        }
        Releasing.Clear();
        PendingRecord.Clear();
        SaveRecords();
        _storeReady?.Dispose();
        _storeReady = null;
        _store = null;
        _storeLoaded = false;
        SweepPending.Clear();
        SweepFailures.Clear();
        _records = new ContainerPreviewSyncRecords();
        _sweepSince = double.NaN;
    }

    /// <summary>
    /// E1b persistence read only (Dev bridge <c>guard</c>): false lets the next world save keep the sync entries and the
    /// streaming origins. The shared <see cref="Persistence.WorldSaveGuard"/> turns it back on after that save, after five
    /// minutes, and at every install.
    /// </summary>
    internal static bool SaveGuardEnabled
    {
        get => Persistence.WorldSaveGuard.Enabled;
        set => Persistence.WorldSaveGuard.Enabled = value;
    }

    /// <summary>True while a world save holds every preview's entries out (the shared <see cref="Persistence.WorldSaveGuard"/>).</summary>
    internal static bool SaveSuspended => Persistence.WorldSaveGuard.Active;

    /// <summary>
    /// The shared world-save guard began (or extended) its suspension: take every preview's sync entries out until the
    /// save file is written.
    /// </summary>
    private static void OnSaveSuspend()
    {
        foreach (var holder in ByUser.Values)
        {
            try { holder.Sync?.Suspend(); }
            catch (Exception e) { Plugin.Logger?.LogWarning("ContainerPreview: taking sync entries out for the save threw: " + e.Message); }
        }
    }

    /// <summary>The save is written: every preview takes its copies again on its next refresh.</summary>
    private static void OnSaveResume()
    {
        foreach (var holder in ByUser.Values) holder.Sync?.Resume();
    }

    // ---- sync records and the startup sweep -----------------------------------------------------------

    internal static void Record(ContainerId container, ulong platform)
    {
        if (_records.Add(container.ToString(), platform)) SaveRecords();
    }

    private static void Unrecord(ContainerId container, ulong platform) => UnrecordRaw(container.ToString(), platform);

    /// <summary>Drop a record by its stored text, so an unparsable record can be removed too.</summary>
    private static void UnrecordRaw(string container, ulong platform)
    {
        if (_records.Remove(container ?? "", platform)) SaveRecords();
    }

    /// <summary>A sync finished releasing: drop its record, unless a release was given up (the sweep needs it then).</summary>
    private static void Released(ContainerPreviewSync sync, ContainerId container, ulong platform)
    {
        if (!sync.GaveUp) { Unrecord(container, platform); return; }
        Plugin.Logger?.LogWarning("ContainerPreview: " + container + " kept its sync record: a release was given up; the next start sweeps it.");
    }

    private static void SaveRecords()
    {
        // Before the store is ready, the previous run's records are not loaded yet: writing now would overwrite them.
        // The ready callback merges this run's records in and writes them.
        if (!_storeLoaded) return;
        try { _store?.Set(ContainerPreviewSyncRecords.Key, _records); (_store as Persistence.ModStore)?.SaveToDisk(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("ContainerPreview: storing the sync records threw: " + e.Message); }
    }

    /// <summary>A sync whose lease ended releases its copies; one that could not keeps retrying from the tick.</summary>
    private static void Finished(ContainerPreviewSync sync, ContainerId container, ulong platform)
    {
        if (sync.Done) { Released(sync, container, platform); return; }
        Releasing.Add(sync);
        PendingRecord[sync] = (container, platform);
    }

    private static readonly Dictionary<ContainerPreviewSync, (ContainerId Container, ulong Platform)> PendingRecord = new();

    private static void RetryReleases(double now)
    {
        for (int i = Releasing.Count - 1; i >= 0; i--)
        {
            var sync = Releasing[i];
            try { sync.Tick(now); } catch (Exception e) { Plugin.Logger?.LogWarning("ContainerPreview: release retry threw: " + e.Message); }
            if (!sync.Done) continue;
            Releasing.RemoveAt(i);
            if (PendingRecord.Remove(sync, out var rec)) Released(sync, rec.Container, rec.Platform);
        }
    }

    /// <summary>
    /// Records from a run that did not end cleanly: remove one copy of that user from the chest's lists where one is left.
    /// Harmless when the game does not save the lists (nothing to remove). Never touches a pair a preview of this run holds.
    /// </summary>
    private static void Sweep(World world, double now)
    {
        if (SweepPending.Count == 0 || double.IsNaN(_sweepSince) || !InventoryRuntime.Installed) return;
        var em = world.EntityManager;
        for (int i = SweepPending.Count - 1; i >= 0; i--)
        {
            var r = SweepPending[i];
            if (IsActive(r)) { SweepPending.RemoveAt(i); continue; }
            SyncSweepStep step;
            Entity container = Entity.Null;
            if (!ContainerId.TryParse(r.Container, out var id)) step = SyncSweepStep.Drop;
            else step = ContainerPreviewSyncSweep.Decide(InventoryRuntime.Status(id), now - _sweepSince);
            if (step == SyncSweepStep.Wait) continue;
            if (step == SyncSweepStep.Sweep && InventoryRuntime.TryFind(id, out container, out _) && container.Exists(em))
            {
                try
                {
                    var user = UserByPlatform(em, r.Platform);
                    // A connected user may be at that chest for real (after a Core hot reload, say): its copy is not ours to take.
                    bool online = user != Entity.Null && em.GetComponentData<User>(user).IsConnected;
                    int removed = user == Entity.Null || online ? 0 : ContainerPreviewSync.SweepOnce(world, user, container);
                    Plugin.Logger?.LogInfo("ContainerPreview: startup sweep of " + r.Container + ": " + removed + " leftover sync entr" + (removed == 1 ? "y" : "ies") + " removed.");
                }
                catch (Exception e)
                {
                    int tries = SweepFailures.TryGetValue(r, out int n) ? n + 1 : 1;
                    SweepFailures[r] = tries;
                    Plugin.Logger?.LogWarning("ContainerPreview: startup sweep of " + r.Container + " threw (try " + tries + " of " + MaxSweepAttempts + "): " + e.Message);
                    if (tries < MaxSweepAttempts) continue;
                    // Given up for this run: the record stays for the next start.
                    SweepPending.RemoveAt(i);
                    SweepFailures.Remove(r);
                    continue;
                }
            }
            SweepPending.RemoveAt(i);
            if (!IsActive(r)) UnrecordRaw(r.Container, r.Platform);
        }
    }

    private static bool IsActive(ContainerPreviewSyncRecord r)
    {
        foreach (var h in ByUser.Values)
            if (h.Platform == r.Platform && string.Equals(h.Container.ToString(), r.Container, StringComparison.Ordinal)) return true;
        foreach (var rec in PendingRecord.Values)
            if (rec.Platform == r.Platform && string.Equals(rec.Container.ToString(), r.Container, StringComparison.Ordinal)) return true;
        return false;
    }

    private static Entity UserByPlatform(EntityManager em, ulong platform)
    {
        foreach (var e in Query.Entities(em, includeDisabled: true, includeSystems: false, ComponentIndex.Of<User>(ComponentType.AccessMode.ReadOnly)))
        {
            try { if (em.GetComponentData<User>(e).PlatformId == platform) return e; }
            catch { /* unreadable: skip */ }
        }
        return Entity.Null;
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
                ["syncInventory"] = h.Sync != null,
                ["syncSuspended"] = h.Sync?.Suspended ?? false,
                ["synced"] = h.Sync?.Describe(),
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
                    // A preview closed while the open gate's prompt was up: the prompt goes, and a later answer opens nothing.
                    else ContainerOpenGateServer.CancelPreview(user.PlatformId, request.Sequence);
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
        var facts = new ContainerPreviewFacts { UserConnected = user.IsConnected, Mode = Mode, IsPvE = IsPvE(), ClientOpensStations = request.Stations };
        int window = 0;
        facts.RateOk = facts.UserConnected && Opens.TryTake(user.PlatformId, Now);
        // Connected, the server's setting, the rate token: before any lookup (the network-id resolve below included).
        if (ContainerPreviewAuthorization.RequestRefusal(facts) is { } early)
        {
            Plugin.Logger?.LogInfo("ContainerPreview: refused user " + user.PlatformId + " " + request.Container + ": " + early);
            Reply(world, user.Index, request.Sequence, false, early, 0, 0, 0);
            return;
        }
        // REOPEN2: a client that knows the container only by its network id (ctx.Inventory.ReopenLast's remote fallback)
        // sends the words instead of the id. Resolved here with the same untrusted-words resolver mods use; every check
        // below then runs on the stable id exactly as for any other preview, the open gate included.
        if (string.IsNullOrEmpty(request.Container) && (request.NetIndex != 0 || request.NetGeneration != 0))
        {
            string why = "inventory not ready";
            if (InventoryRuntime.Installed
                && InventoryRuntime.TryResolveNetworkId(request.NetIndex, request.NetGeneration, request.NetType, out var byNet, out _, out why))
                request.Container = byNet.ToString();
            else
            {
                Plugin.Logger?.LogInfo("ContainerPreview: refused user " + user.PlatformId + " network id "
                    + NetworkIdWords.Format(request.NetIndex, request.NetGeneration) + ": " + why);
                Reply(world, user.Index, request.Sequence, false, ContainerPreviewAuthorization.UnknownByNetworkId, 0, 0, 0);
                return;
            }
        }
        facts.IdParsed = ContainerId.TryParse(request.Container, out var id);
        Entity container = Entity.Null;
        int netIndex = 0, netGeneration = 0, netType = 0;
        if (facts.RateOk && facts.IdParsed && InventoryRuntime.Installed)
        {
            facts.Found = InventoryRuntime.TryFind(id, out container, out _) && container.Exists(em);
            if (facts.Found)
            {
                facts.HasInventory = UnityInventoryBackend.HasInventory(em, container);
                // STATIONPREVIEW1: a lone crafting, refining or salvage station opens its own window for a client that can.
                window = ContainerPreviewMenus.WindowFor(HasOwnMenu(em, container), StationMenusOf(em, container),
                    HasOtherOwnMenu(em, container), request.Stations);
                facts.HasOwnMenu = window == 0;
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

        // The sync must be taken out around world saves; without the save hook Core never hears of them.
        if (request.SyncInventory && !Persistence.ModSave.WorldSaveHookArmed)
        {
            Plugin.Logger?.LogWarning("ContainerPreview: refused user " + user.PlatformId + ": the world-save hook is not in place.");
            Reply(world, user.Index, request.Sequence, false, ContainerPreviewAuthorization.SaveHookRefusal, 0, 0, 0);
            return;
        }

        // The container open gate (ctx.Inventory.OnOpenRequest): a plugin may refuse the preview, or ask the player first.
        // While the player answers, the client is told to keep waiting; a right answer runs this open again (the answer
        // counts then), anything else refuses it with the sentence.
        if (ContainerOpenGateServer.Installed)
        {
            int sequence = request.Sequence;
            ulong platform = user.PlatformId;
            string? gateRefusal = ContainerOpenGateServer.CheckPreview(world, userEntity, user, id, container, sequence,
                (ok, message) => ContinueAfterGate(userEntity, platform, request, ok, message), out bool held);
            if (gateRefusal != null)
            {
                Reply(world, user.Index, request.Sequence, false, gateRefusal, 0, 0, 0);
                return;
            }
            if (held)
            {
                Send(world, user.Index, new ContainerPreviewPacket
                {
                    Kind = ContainerPreviewWire.KindHold, Sequence = request.Sequence, Container = request.Container,
                    Seconds = ContainerGatePending<ContainerOpenRequest>.PromptSeconds + ContainerPreviewMachine.RequestTimeoutSeconds,
                });
                return;
            }
        }

        if (ByUser.TryGetValue(userEntity, out var previous)) previous.End(CameraObservationEndReason.Replaced, notify: false, message: "");
        container.TryRead<Translation>(em, out var at);
        var holder = new Holder(userEntity, user.Index, request.Sequence, id, container, ContainerPreviewAuthorization.GrantSeconds(request.Seconds))
            { Platform = user.PlatformId };
        try { SharedObservation.Acquire(world, userEntity, at.Value, holder); }
        catch (InvalidOperationException)
        {
            Reply(world, user.Index, request.Sequence, false, "Another view is already streaming for you; close it first.", 0, 0, 0);
            return;
        }
        ByUser[userEntity] = holder;
        // E1: the chest's inventory reaches only users in its sync list; the streaming lease cannot reach it.
        if (request.SyncInventory)
        {
            holder.Sync = new ContainerPreviewSync(world, userEntity, container);
            Record(id, user.PlatformId);
            // A world save is being written: start suspended; the entries go in when the save is written (OnSaveResume).
            if (SaveSuspended) holder.Sync.Suspend();
            try { holder.Sync.Refresh(); }
            catch (Exception e)
            {
                Plugin.Logger?.LogWarning("ContainerPreview: inventory sync failed: " + e.Message);
                holder.End(CameraObservationEndReason.Failed, notify: false, message: "");
                Reply(world, user.Index, request.Sequence, false, "The server could not share that container's contents.", 0, 0, 0);
                return;
            }
        }
        Plugin.Logger?.LogInfo("ContainerPreview: user " + user.PlatformId + " previews " + id + " (" + container.Index + ":" + container.Version
            + ", " + holder.Policy.Deadline.ToString("0") + ", inventory sync " + (holder.Sync == null ? "off" : holder.Sync.HeldCount + " entities") + ").");
        Reply(world, user.Index, request.Sequence, true, "", netIndex, netGeneration, netType, id.ToString(), window);
    }

    /// <summary>
    /// The open gate's prompt for a preview was answered: right runs the open again for the same request (the answers count
    /// now), anything else refuses it. A user who left, or a world that went, gets nothing.
    /// </summary>
    private static void ContinueAfterGate(Entity userEntity, ulong platform, ContainerPreviewPacket request, bool ok, string message)
    {
        if (!_installed) return;
        var world = Worlds.Server;
        if (world == null || !world.IsCreated) return;
        var em = world.EntityManager;
        if (!userEntity.Exists(em) || !userEntity.Has<User>(em)) return;
        var user = em.GetComponentData<User>(userEntity);
        if (user.PlatformId != platform || !user.IsConnected) return;
        try
        {
            if (ok) Open(world, userEntity, user, request);
            else Reply(world, user.Index, request.Sequence, false, string.IsNullOrWhiteSpace(message) ? "The preview was not unlocked." : message, 0, 0, 0);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("ContainerPreview: open after the gate threw: " + e.Message);
            Reply(world, user.Index, request.Sequence, false, "The preview failed on the server.", 0, 0, 0);
        }
    }

    /// <summary>
    /// Anything with a menu of its own (or no business in a container window): only plain containers pass. One check per
    /// name of <see cref="ContainerPreviewMenus.OwnMenuComponents"/>, in the same order (a source test keeps them equal).
    /// </summary>
    private static bool HasOwnMenu(EntityManager em, Entity e) =>
        e.Has<CastleHeart>(em) || e.Has<Refinementstation>(em) || e.Has<WorkstationRecipesBuffer>(em)
        || e.Has<ResearchStation>(em) || e.Has<Salvagestation>(em) || e.Has<UnitSpawnerstation>(em)
        || e.Has<PrisonCell>(em) || e.Has<ServantCoffinstation>(em) || e.Has<ServantEquipment>(em)
        || e.Has<Forge_Shared>(em) || e.Has<FusionForge_Shared>(em) || e.Has<BloodMixer_Shared>(em) || e.Has<JewelCraftingStation>(em)
        || e.Has<Stables_Shared>(em) || e.Has<ProjectM.Contest.Arena.CastleArenaStation_Shared>(em) || e.Has<InventoryRouteStation>(em)
        || e.Has<LightningRodStation>(em);

    /// <summary>
    /// STATIONPREVIEW1: the windows of the previewable stations the model is (<see cref="ContainerPreviewMenus.PreviewableStations"/>,
    /// same order).
    /// </summary>
    private static List<int> StationMenusOf(EntityManager em, Entity e)
    {
        var menus = new List<int>(1);
        if (e.Has<Salvagestation>(em)) menus.Add(ContainerPreviewMenus.SalvagestationMenu);
        if (e.Has<Refinementstation>(em)) menus.Add(ContainerPreviewMenus.RefinementstationMenu);
        if (e.Has<WorkstationRecipesBuffer>(em)) menus.Add(ContainerPreviewMenus.WorkstationMenu);
        return menus;
    }

    /// <summary>
    /// STATIONPREVIEW1: an own-menu component other than a previewable station's (<see cref="ContainerPreviewMenus.OtherOwnMenuComponents"/>;
    /// a source test keeps the two lists equal).
    /// </summary>
    private static bool HasOtherOwnMenu(EntityManager em, Entity e) =>
        e.Has<CastleHeart>(em) || e.Has<ResearchStation>(em) || e.Has<UnitSpawnerstation>(em)
        || e.Has<PrisonCell>(em) || e.Has<ServantCoffinstation>(em) || e.Has<ServantEquipment>(em)
        || e.Has<Forge_Shared>(em) || e.Has<FusionForge_Shared>(em) || e.Has<BloodMixer_Shared>(em) || e.Has<JewelCraftingStation>(em)
        || e.Has<Stables_Shared>(em) || e.Has<ProjectM.Contest.Arena.CastleArenaStation_Shared>(em) || e.Has<InventoryRouteStation>(em)
        || e.Has<LightningRodStation>(em);

    /// <summary>The server's setting now (the default until the shared cfg binds).</summary>
    internal static ContainerPreviewMode Mode
    {
        get
        {
            try { return ContainerPreviewSettings.Parse(_mode?.Value); }
            catch { return ContainerPreviewSettings.Parse(null); }
        }
    }

    /// <summary>The server's game mode is PvE; null when unreadable.</summary>
    internal static bool? IsPvE()
    {
        try
        {
            var mode = ServerRulesGame.Loaded(ServerRules.GameModeType);
            return mode == null ? null : string.Equals(mode, "PvE", StringComparison.Ordinal);
        }
        catch { return null; }
    }

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
        if (ByUser.Count == 0 && Releasing.Count == 0 && SweepPending.Count == 0) return;
        double now = Now;
        if (now < _nextCheck) return;
        _nextCheck = now + 1;
        var world = Worlds.Server;
        if (world == null || !world.IsCreated) return;
        var em = world.EntityManager;

        RetryReleases(now);
        Sweep(world, now);
        // An admin turning the setting off (or a PvE-only server becoming PvP) ends every running preview.
        string? settingOff = ContainerPreviewSettings.Refusal(Mode, IsPvE());
        foreach (var holder in new List<Holder>(ByUser.Values))
        {
            string? why = settingOff;
            try
            {
                if (why != null) { }
                else if (!holder.Entity.Exists(em)) why = "The container is gone.";
                else
                {
                    var castle = CastleOf(world, holder.Entity);
                    var relation = castle != null ? PluginCastles.RelationOf(world, holder.User, castle) : CastleRelation.None;
                    if (relation is not (CastleRelation.Owner or CastleRelation.ClanMember)) why = "You no longer have access to that container.";
                }
            }
            catch (Exception e) { why = "The preview check failed: " + e.Message; }
            if (why != null) { holder.End(CameraObservationEndReason.Disposed, notify: true, message: why); continue; }
            // Items that entered or left the chest since the last second; entries put back after a save.
            try { holder.Sync?.Tick(now); }
            catch (Exception e)
            {
                Plugin.Logger?.LogWarning("ContainerPreview: inventory sync refresh threw: " + e.Message);
                holder.End(CameraObservationEndReason.Failed, notify: true, message: "The server could not keep sharing that container's contents.");
            }
        }
    }

    private static void Reply(World world, int userIndex, int sequence, bool ok, string message, int index, int generation, int type,
        string container = "", int menuType = 0)
    {
        Send(world, userIndex, new ContainerPreviewPacket
        {
            Kind = ContainerPreviewWire.KindReply, Sequence = sequence, Ok = ok, Message = ContainerPreviewWire.Clip(message),
            NetIndex = index, NetGeneration = generation, NetType = type, Container = container ?? "", MenuType = menuType,
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
        internal ulong Platform { get; init; }
        internal int UserIndex { get; }
        internal int Sequence { get; }
        internal ContainerId Container { get; }
        internal Entity Entity { get; }
        internal SharedObservation? Lease => _lease;

        /// <summary>This preview's entries in the chest's inventory sync lists; null with <c>syncInventory: false</c>.</summary>
        internal ContainerPreviewSync? Sync { get; set; }

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
            // Every end path (unload, disconnect, expiry, failure, replacement, the 1 s checks) comes through here.
            try { if (Sync != null) { Sync.End(); Finished(Sync, Container, Platform); } }
            catch (Exception e) { Plugin.Logger?.LogWarning("ContainerPreview: sync cleanup threw: " + e.Message); }
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
