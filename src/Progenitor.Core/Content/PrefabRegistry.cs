using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Threading;
using HarmonyLib;
using ProjectM;
using Progenitor.Ecs;
using Progenitor.Events;
using Progenitor.Networking;
using Progenitor.Reload;
using Stunlock.Core;
using Unity.Entities;

namespace Progenitor.Content;

/// <summary>
/// Clones vanilla item or buff prefabs under a deterministic GUID and keeps every game lookup in
/// sync, including the client that never shipped the GUID.
///
/// Mods describe an item with <see cref="DefineItem"/> and <see cref="ItemDefinitionBuilder.Register"/>,
/// or a buff with <see cref="DefineBuff"/> and <see cref="BuffDefinitionBuilder.Register"/>.
/// The server applies the clone before a save is deserialized (unknown GUIDs are dropped on load)
/// and pushes the authoritative key-table order (<c>PrfI</c>) then each definition so the client
/// appends the same row to its item key table before join-time validation. Dispose the handle (or call
/// <see cref="UnregisterAll"/>) for a soft unapply: the clone entity and every GUID map stay so
/// inventories that still hold the item keep resolving; only the definition's undo list
/// (localization) and this registry's bookkeeping are cleared. The server then sends an
/// unregister packet so connected clients do the same. ItemKeyLookup rows stay — the table never
/// shrinks (inventory slots index into them; join validation is count+hash). A GUID that was
/// appended in this process and then unregistered is kept as a tombstone and sent to new joiners
/// as a key-table placeholder (<c>PrfK</c>) so they append the same row without registering a
/// prefab. A later Register of the same id drops the tombstone and updates the surviving clone
/// in place (ItemData, managed name and description) when <c>Kind</c> and resolved
/// <c>CloneOf</c> are unchanged. A changed source with no live instances destroys and
/// re-clones under the same GUID; live instances or an Item↔Buff kind change refuse that
/// definition (not the batch). Prefab-level edits do not reach buff instances that already
/// exist or that a save restores (evidence 109); <c>DefineBuff(...).RefreshLiveInstances()</c>
/// opts that definition into a destroy-and-<c>ApplyBuff</c> refresh of live instances after
/// a later changed apply (default off; never on first process start; one instance per frame).
/// An appended GUID whose live entry is not an item is still
/// tombstoned so join-time count+hash cannot see a hole.
///
/// A spawnable name that already maps to a GUID this process did not clone is refused
/// even when that GUID equals <c>CalculateGuidHash(id)</c> (vanilla name reuse). Register
/// still returns a handle so OnLoad does not throw; the handle is already invalidated,
/// the definition is not kept, not sent as <c>PrfD</c>, and not tombstoned. A Core ALC swap
/// unloads plugins first (soft unapply, entity kept) then snapshots clone ownership; the
/// next generation rehydrates that set before plugins re-Register so a GUID this process
/// cloned in any earlier generation — item or buff — is re-claimed (in-place when
/// source and kind match; otherwise retarget or refuse). Apply is deferred
/// until the side's apply gate opens (see <see cref="PrefabApplyGate"/>): the client waits for
/// the first <c>PrfD</c> or <c>PrfK</c> of the join; the server waits for
/// <c>DeserializeLoadFile</c>, a connecting player, or a 30 s no-player fallback. A later
/// non-pending failure drops the same way. Client-only items (no server packet) are not
/// applied; the frame retry logs once at the give-up budget and stops. Tombstones are only
/// for GUIDs this process actually appended to ItemKeyLookup.
///
/// Not wired from <c>Plugin.Load</c>; call <see cref="Install"/> once from Core.
/// </summary>
public static unsafe class PrefabRegistry
{
    /// <summary>
    /// Fired after a definition is accepted into the in-memory list, which is also the client-sync
    /// list. Apply into the live world may still be pending if prefabs are not loaded yet.
    /// Not raised when apply refuses (vanilla name / foreign GUID) and the entry is dropped.
    /// </summary>
    public static event Action<PrefabDefinition>? Registered;

    /// <summary>
    /// Core-internal: runs right after a definition's clone was built or updated in
    /// <paramref name="world"/> (either side), before any editor pass and before <c>PrfD</c> goes out.
    /// On a client this is the frame the clone first exists, so nothing has been instantiated from it
    /// yet. Multicast, Core modules only: <c>Ai.InertClones</c> strips Core's inert clones on the client,
    /// and <c>Debugging.DebugBuff</c> runs the debug buff's prefab pass on both sides; each adds and removes
    /// its own delegate. Never a plugin delegate. A throw is logged and does not fail the apply.
    /// </summary>
    internal static Action<World, PrefabDefinition>? AfterApply;

    private static readonly object Gate = new();
    private static readonly List<Entry> Entries = new();
    /// <summary>
    /// GUIDs appended to this process's ItemKeyLookup that are no longer in <see cref="Entries"/>.
    /// Server: sent as <c>PrfK</c> placeholders on connect (and right after unregister) so late
    /// joiners still append the key row. Client: recorded from those packets; a later definition
    /// of the same GUID drops the tombstone and applies (full clone if this client never had the
    /// entity; in-place update if a prior apply left the clone retired-but-resolvable).
    /// </summary>
    private static readonly List<KeyTombstone> Tombstones = new();
    private static readonly object SyncedOwner = new SyncedOwnerTag();

    private static Harmony? _harmony;
    private static IDisposable? _connectedSub;
    private static IDisposable? _worldReadySub;
    /// <summary>
    /// High-priority <see cref="WorldReady"/> handler that decides and performs the new-world
    /// reset before any other subscriber (a plugin's WorldReady, <c>RecipeApply</c>) can apply
    /// against the previous world's bookkeeping. The priority-0 <see cref="OnWorldReady"/> then
    /// consumes <see cref="_worldReadyPlan"/>.
    /// </summary>
    private static IDisposable? _worldReadyResetSub;
    private static PrefabApplyGate.WorldReadyPlan? _worldReadyPlan;
    /// <summary>Above every Core and plugin WorldReady subscriber.</summary>
    private const int WorldReadyResetPriority = 10000;
    /// <summary>Client: <see cref="OnClientConnectionEnded"/> is on <c>Handshake.ClientConnectionEnded</c>.</summary>
    private static bool _connectionEndedHooked;
    private static bool _installed;
    private static bool _frameRetryHooked;
    private static bool _wireFrameHooked;
    private static int _wireFrameTick;
    /// <summary>GuidHash → frames waited while apply was pending (source collection not ready).</summary>
    private static readonly Dictionary<int, int> PendingFrames = new();
    /// <summary>
    /// Client side: GuidHash → attempts spent re-applying a received <c>PrfE</c> whose prefab
    /// entity was not in the collection yet (job FEDW3, R51 finding 7). Before this, a layer that
    /// landed one frame early was dropped in silence, and for the case the feature exists for — a
    /// client with no local editor for that prefab — no later pass ever revisited it.
    /// </summary>
    private static readonly Dictionary<int, int> EditLayerPending = new();
    /// <summary>Guids whose pending-layer give-up line has been logged this generation.</summary>
    private static readonly HashSet<int> EditLayerGaveUp = new();
    /// <summary>
    /// Client join: server-order admission. A dropped <c>PrfI</c> is not a safe
    /// <c>PrfD</c>-first apply; missing/late/corrupt ledgers refuse custom-item appends
    /// so an inventory index cannot resolve as a different item.
    /// </summary>
    private static readonly KeyJoinSession JoinSession = new();
    /// <summary>Guids whose key-agreement refusal line has been logged this join.</summary>
    private static readonly HashSet<int> JoinRefuseLogged = new();
    /// <summary>
    /// Attempts a pending layer gets before it is given up. The wire frame runs every
    /// <see cref="WireFramePeriod"/> frames, so this is about 60 s at 60 Hz — the same order as
    /// <see cref="PrefabWire.ApplyGiveUpFrames"/>, which is the budget the definition retry uses.
    /// </summary>
    private const int EditLayerRetryAttempts = 240;
    /// <summary>Frames between wire-frame passes: deferred sends, handshake edges, layer retries.</summary>
    private const int WireFramePeriod = 15;
    /// <summary>
    /// Applies are held until this gate opens. Verified live (2026-09-02): a per-frame retry that
    /// succeeded while the game was still building ItemKeyLookup appended our row at 1077 into a
    /// table that later held 1084 rows, so the row moved. Before the first join packet (client)
    /// or DeserializeLoadFile / PlayerConnected / 30 s no-player fallback (server) definitions
    /// are only queued. Client-only items require a server packet and give up after
    /// <see cref="PrefabWire.ApplyGiveUpFrames"/>.
    /// </summary>
    private static readonly PrefabApplyGate ApplyGate = new(
        Plugin.IsServerProcess ? PrefabApplyGate.Side.Server : PrefabApplyGate.Side.Client);
    /// <summary>
    /// Ordered editors for this Core generation. Delegates stay here (plugin ALC) and are
    /// dropped on <see cref="Uninstall"/>; plugins re-register after a swap the same way they
    /// re-<see cref="ItemDefinitionBuilder.Register"/>. Bootstrap ContentMemory must not hold
    /// them.
    /// </summary>
    private static readonly PrefabEditTable Edits = NewEditTable();

    /// <summary>
    /// The edit table with its removal hook wired to the coalesced next-frame flush: a
    /// handle <c>Dispose</c> or a plugin unload queues its GUIDs and the flush runs one
    /// <see cref="PrefabEditTrigger.EditorsChanged"/> pass each (restore, then whatever
    /// editors are left).
    /// </summary>
    private static PrefabEditTable NewEditTable()
    {
        var table = new PrefabEditTable();
        table.RemovalScheduled = OnEditRemovalScheduled;
        return table;
    }

    private static void OnEditRemovalScheduled()
    {
        try
        {
            // After Uninstall the frame hook is gone on purpose; re-arming it here would
            // resurrect a handler for a generation that is being torn down.
            if (!_installed) return;
            ScheduleEditFlush();
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"PrefabRegistry: scheduling the edit flush after a removal threw: {e}");
        }
    }
    /// <summary>
    /// Live-refresh phases waiting to be settled. A Core swap or plugin reload applies a
    /// definition twice — once before the plugin's <c>Content.Edit</c> passes are registered
    /// and once after — and both phases note here instead of deciding, so one apply produces
    /// exactly one decision and one log line (R26 finding 4, fixed in FU).
    /// </summary>
    private static readonly LiveRefreshDecisions RefreshDecisions = new();
    /// <summary>
    /// Frames of quiet after the last noted apply phase before the live-refresh decision is
    /// taken: the plugin's <c>Register</c> and its <c>Edit</c> registrations run in the same
    /// frame and a coalesced <c>InputsChanged</c> flush lands on the next one.
    ///
    /// This window is not related to how long the refresh queue takes to drain
    /// (<c>PrefabLiveRefreshApply.OnFrame</c> dequeues one item per frame, so 40 holders is
    /// seconds). The two timescales are independent by design: the fingerprint describes the
    /// definition, not its instances, so a drain still in flight when the settle fires cannot
    /// change what is recorded (R28 finding 28.2).
    /// </summary>
    internal const int RefreshSettleFrames = 2;

    /// <summary>
    /// Arm / tick / stop state for the settle, all three under one lock (R27 finding 21).
    /// <c>ScheduleRefreshSettle</c> runs on the apply path while <c>OnRefreshSettleFrame</c>
    /// and <c>StopRefreshSettle</c> run on the frame thread.
    /// </summary>
    private static readonly RefreshSettleCountdown RefreshSettle = new(RefreshSettleFrames);
    private static bool _editFlushHooked;
    private static bool _handshakeWatchHooked;
    private static string _handshakeFp = "";
    private static int _editRerunDepth;

    /// <summary>
    /// Drained removals whose pass restored nothing, and how many times each has been put back
    /// (R49 finding 6). Frame-thread only, like the flush that reads it; an entry disappears the
    /// moment the removal is serviced or the retry budget is spent.
    /// </summary>
    private static readonly Dictionary<int, int> _removalRetries = new();

    /// <summary>
    /// Guids whose removal has already produced its one terminal line (given up, or handed to
    /// another path) in this Core generation. Without it a GUID that is removed, re-registered
    /// and removed again repeats the same warning, and the cycle case repeated it per re-queue
    /// (R50 finding 6). Cleared with <see cref="_removalRetries"/>.
    /// </summary>
    private static readonly HashSet<int> _removalNoticed = new();
    /// <summary>
    /// Nested depth of <see cref="ApplyOne"/> so an edit pass that already ran inside
    /// apply does not queue a second live-instance refresh.
    /// </summary>
    private static int _applyOneDepth;

    /// <summary>Snapshot of every definition currently in the registry, including ones not yet applied.</summary>
    public static IReadOnlyList<PrefabDefinition> Definitions
    {
        get
        {
            lock (Gate)
            {
                if (Entries.Count == 0) return Array.Empty<PrefabDefinition>();
                var copy = new PrefabDefinition[Entries.Count];
                for (int i = 0; i < Entries.Count; i++)
                    copy[i] = Entries[i].Definition;
                return copy;
            }
        }
    }

    /// <summary>
    /// Starts a cloned-item definition. <paramref name="id"/> is the spawnable name and the input
    /// to <see cref="PrefabGUID.CalculateGuidHash"/>, so it must be identical on every peer.
    /// ASCII, 1–63 bytes.
    /// </summary>
    public static ItemDefinitionBuilder DefineItem(string id) => new(id);

    /// <summary>
    /// Starts a cloned-buff definition. Same id / GUID / client-sync rules as
    /// <see cref="DefineItem"/>; the source must have <c>Buff</c> (not <c>ItemData</c>), and
    /// apply skips <c>ItemKeyLookup</c>. The HUD shows the clone when the source carries
    /// <c>ShowOnBuffBar</c> (and, on the client, <c>ShowOnBuffBarData</c> with an icon).
    /// <c>Named</c> / <c>Described</c> write that component's Header and description keys.
    /// <c>Icon("icons/foo.png")</c> replaces the sprite from a plugin PNG under <c>assets</c>.
    /// <c>RefreshLiveInstances()</c> opts the clone into destroy-and-reapply of live
    /// instances after a later definition change (default off).
    /// </summary>
    public static BuffDefinitionBuilder DefineBuff(string id) => new(id);

    /// <summary>
    /// Live definition count, ItemKeyLookup tombstones, and GUIDs this process appended.
    /// Tombstones are only GUIDs we actually appended and then unregistered — a vanilla
    /// row that already contained a colliding hash is never listed. The table never shrinks;
    /// tombstones are what new joiners must be told so join-time count+hash still matches.
    /// </summary>
    public static string Describe()
    {
        lock (Gate)
        {
            var sb = new StringBuilder();
            sb.Append(PrefabApplyBookkeeping.FormatRegistryDescribe(
                Entries.Count, Tombstones.Count, PrefabApplicator.AppendedCount));
            int editCount = Edits.Count;
            if (editCount > 0)
                sb.Append(" edits=").Append(editCount);
            for (int i = 0; i < Tombstones.Count; i++)
            {
                var t = Tombstones[i];
                sb.Append(PrefabApplyBookkeeping.FormatTombstoneDetail(t.GuidHash, t.Id));
            }
            return sb.ToString();
        }
    }

    /// <summary>Number of ItemKeyLookup tombstones currently remembered in this process.</summary>
    public static int TombstoneCount
    {
        get { lock (Gate) return Tombstones.Count; }
    }

    /// <summary>
    /// State of every lookup the registrar writes, for one GUID in one world. Safe to call from
    /// the debug bridge / <c>progenitor_exec</c>: failures are returned as text, not thrown.
    /// A retired prefab whose clone still resolves is marked <c>retired (entity kept)</c>; a
    /// key-table placeholder with no clone (late joiner, <c>PrfK</c> only) stays a tombstone.
    /// </summary>
    public static string Describe(World world, PrefabGUID guid)
    {
        try
        {
            var text = PrefabApplicator.Describe(world, guid);
            bool isTombstone;
            lock (Gate)
            {
                isTombstone = false;
                for (int i = 0; i < Tombstones.Count; i++)
                {
                    if (Tombstones[i].GuidHash != guid.GuidHash) continue;
                    isTombstone = true;
                    break;
                }
            }
            string edits = Edits.FormatDescribe(guid.GuidHash);
            if (!string.IsNullOrEmpty(edits))
                text = text + "; " + edits;
            if (!isTombstone) return text;
            if (EntityStillResolves(world, guid))
                return text + "; retired (entity kept)";
            return text + "; tombstone (ItemKeyLookup placeholder, not registered)";
        }
        catch (Exception e)
        {
            return $"PrefabRegistry.Describe threw: {e}";
        }
    }

    /// <summary>
    /// Drop every definition registered under <paramref name="owner"/>. Reloadable plugins that
    /// did not keep the <see cref="IDisposable"/> handles can call this from <c>Unload</c>.
    /// </summary>
    public static int UnregisterAll(object owner)
    {
        if (owner == null) throw new ArgumentNullException(nameof(owner));
        List<Entry> removed = new();
        lock (Gate)
        {
            for (int i = Entries.Count - 1; i >= 0; i--)
            {
                if (!ReferenceEquals(Entries[i].Definition.Owner, owner)) continue;
                removed.Add(Entries[i]);
                Entries.RemoveAt(i);
            }
        }
        foreach (var entry in removed)
            UnapplyEntry(entry);
        Edits.UnregisterOwner(owner);
        return removed.Count;
    }

    /// <summary>
    /// Register an ordered editor for <paramref name="prefabGuid"/>. Runs after this process
    /// has cloned/registered that prefab (or, for a vanilla GUID, once the collection has the
    /// entity) and before a definition packet is sent, so both sides apply the same pass
    /// locally. Edited component values are <em>not</em> a wire field: the owning plugin must
    /// declare <c>[NetworkCompatibility(ServerMustHave, Exact or Minor)]</c> so a client without
    /// it is kicked. The gate is the live handshake table after <c>DeclareSelf</c> /
    /// <c>Require</c> / <c>Unrequire</c>; the attribute is the fallback at registration
    /// (OnLoad before DeclareSelf). A later Unrequire or Require at a weaker level drops
    /// this plugin's editors with one Warn. <c>owner</c> must be the registering plugin
    /// (<c>ctx.Owner</c>); a decoy instance whose type has the attribute is refused (log
    /// once). <c>NoNeedForSync</c> / <c>ClientOnly</c> / a missing attribute refuses this
    /// registration (Warn naming the plugin and the fix). A vanilla GUID additionally logs once
    /// that clients without the plugin keep unedited stats.
    /// <paramref name="priority"/> is Early-under / Late-on-top; <paramref name="before"/>
    /// / <paramref name="after"/> are Harmony-style plugin ids (missing ids are ignored).
    /// Dispose (or <see cref="UnregisterAll"/>) drops the editor; a before/after cycle refuses
    /// the new row, logs a Warn, and returns an already-invalid handle.
    /// <paramref name="version"/> is an optional token in the apply fingerprint; when captured
    /// values change without a new registration, call <see cref="Invalidate"/> instead.
    ///
    /// The editor may change component values through <see cref="PrefabEditHandle"/>; it must
    /// not retarget <c>PrefabGUID</c> or the Prefab tag. Editors may be re-run on live
    /// instances (registry-driven refresh); prefab-only strips such as
    /// <c>BuffSequenceSpawned</c> must check the Prefab tag and must not run on instances.
    /// Two plugins writing the same component of the same prefab produce a Warn naming both,
    /// the winner, and why. Delegates are not stored in Bootstrap: a Core swap drops this
    /// table and plugins re-register from <c>OnLoad</c>.
    /// </summary>
    public static IDisposable Edit(
        PrefabGUID prefabGuid,
        Action<PrefabEditHandle> editor,
        object owner,
        PrefabEditPriority priority = PrefabEditPriority.Default,
        string[]? before = null,
        string[]? after = null,
        string? pluginId = null,
        string? pluginName = null,
        string? version = null)
    {
        return Edit(prefabGuid, editor, owner, (int)priority, before, after, pluginId, pluginName, version);
    }

    /// <summary>
    /// Same as <see cref="Edit(PrefabGUID, Action{PrefabEditHandle}, object, PrefabEditPriority, string[], string[], string, string, string)"/>
    /// with a raw priority int so a mod can pass <c>(int)Early + 5</c>.
    /// <paramref name="version"/> is an optional token that participates in the apply
    /// fingerprint. Prefer <see cref="Invalidate"/> when captured values change without
    /// re-registering the editor.
    /// </summary>
    public static IDisposable Edit(
        PrefabGUID prefabGuid,
        Action<PrefabEditHandle> editor,
        object owner,
        int priority,
        string[]? before = null,
        string[]? after = null,
        string? pluginId = null,
        string? pluginName = null,
        string? version = null)
    {
        if (editor == null) throw new ArgumentNullException(nameof(editor));
        if (owner == null) throw new ArgumentNullException(nameof(owner));

        string requestedId = string.IsNullOrWhiteSpace(pluginId) ? "" : pluginId!;
        if (!PrefabEditCompatibility.TryAcceptOwner(owner, requestedId, out var handshakeId, out _))
        {
            string decoyId = requestedId.Length > 0 ? requestedId : DescribeOwner(owner);
            if (Edits.OwnerWarns.ShouldLog(prefabGuid.GuidHash, decoyId))
                Plugin.Logger?.LogWarning(PrefabEditLog.DecoyOwnerRefused(decoyId, prefabGuid.GuidHash));
            return PrefabEditTable.InvalidHandle();
        }

        string id = requestedId.Length > 0 ? requestedId : handshakeId;
        string name = string.IsNullOrEmpty(pluginName) ? id : pluginName;

        if (!PrefabEditCompatibility.MayEdit(handshakeId, owner, out var declaredLevel, out var declaredRule))
        {
            Plugin.Logger?.LogWarning(
                PrefabEditCompatibility.RefusedLine(id, prefabGuid.GuidHash, declaredLevel, declaredRule));
            return PrefabEditTable.InvalidHandle();
        }

        bool clonedHere;
        lock (Gate) clonedHere = HasEntryNoLock(prefabGuid.GuidHash);
        if (!clonedHere && Edits.VanillaWarns.ShouldLog(prefabGuid.GuidHash, id))
            Plugin.Logger?.LogWarning(PrefabEditLog.VanillaEditLocalOnly(id, prefabGuid.GuidHash));

        if (!Edits.TryAdd(
                prefabGuid.GuidHash,
                id,
                name,
                priority,
                before,
                after,
                owner,
                editor,
                loadOrder: -1,
                out var handle,
                out var refuseLog,
                version,
                handshakeId))
        {
            if (!string.IsNullOrEmpty(refuseLog))
                Plugin.Logger?.LogWarning(refuseLog);
            return handle;
        }

        if (PrefabEditCompatibility.LiveAllows(handshakeId))
            Edits.RememberHandshakeAllowed(handshakeId);

        EnsureHandshakeWatch();
        TryRunEdits(prefabGuid.GuidHash, world: null, PrefabEditTrigger.EditorsChanged);
        return handle;
    }

    /// <summary>
    /// Mark editors of <paramref name="prefabGuid"/> as stale because captured inputs changed
    /// (a synced <c>ConfigEntry</c>, a computed stat, anything an editor reads that is not
    /// the ordered set). Bumps that GUID's generation (the InputsChanged version counter)
    /// and queues one re-run on the next frame, coalesced with other Invalidates of the
    /// same prefab. An invalidate during an apply pass does not re-enter: it marks dirty
    /// and re-runs once after the pass. CollectionReady will not notice a closure change
    /// on its own (R20 finding 4, R21 finding 4).
    /// </summary>
    public static void Invalidate(PrefabGUID prefabGuid)
    {
        try
        {
            Edits.Invalidate(prefabGuid.GuidHash);
            ScheduleEditFlush();
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"PrefabRegistry: Invalidate({prefabGuid.GuidHash}) threw: {e}");
        }
    }

    /// <summary>
    /// <see cref="Invalidate(PrefabGUID)"/> for every prefab this <paramref name="owner"/>
    /// has an editor on. Call from <c>SettingChanged</c> when the plugin does not want to
    /// name each GUID (<c>PluginContent.InvalidateEdits</c>).
    /// </summary>
    public static void InvalidateEdits(object owner)
    {
        if (owner == null) throw new ArgumentNullException(nameof(owner));
        try
        {
            Edits.InvalidateOwner(owner);
            ScheduleEditFlush();
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"PrefabRegistry: InvalidateEdits threw: {e}");
        }
    }

    /// <summary>
    /// Harmony prefix on save deserialize, PlayerConnected sender, client definition,
    /// unregister, and key-placeholder receive handlers. Safe to call twice. Pass Core's Harmony
    /// instance so <c>UnpatchSelf</c> covers the prefix.
    /// </summary>
    public static void Install(Harmony? harmony)
    {
        if (_installed) return;
        if (HarmonySlotHandoff.ShouldSkipModule(HarmonySlotHandoff.ContentModule))
        {
            Plugin.Logger?.LogInfo(HarmonySlotHandoff.SkipModuleLog(HarmonySlotHandoff.ContentModule));
            return;
        }

        _harmony = harmony;
        if (Plugin.IsServerProcess)
        {
            if (HarmonySlotHandoff.SkipCoreHarmony)
            {
                // Skip log already emitted from Plugin.Install.
            }
            else
            {
                BindDeserializeSites(harmony);
            }
            _connectedSub = EventBus.On<PlayerConnected>(OnPlayerConnected, priority: 2000);
            UnlockPrune.Install();
            Plugin.Logger?.LogInfo("PrefabRegistry: server prefix on DeserializeLoadFile; syncing definitions and key-table tombstones on PlayerConnected.");
        }
        else
        {
            CustomEvents.OnReceive(KeyLedgerPacket.EventId, OnClientKeyLedger);
            CustomEvents.OnReceive(KeyLedgerChunks.EventId, OnClientKeyLedgerChunk);
            CustomEvents.OnReceive(PrefabDefinitionPacket.EventId, OnClientDefinition);
            CustomEvents.OnReceive(ItemStatPacket.EventId, OnClientItemStats);
            CustomEvents.OnReceive(PrefabUnregisterPacket.EventId, OnClientUnregister);
            CustomEvents.OnReceive(PrefabUnregisterPacket.KeyPlaceholderEventId, OnClientKeyPlaceholder);
            CustomEvents.OnReceive(PrefabEditWire.EventId, OnClientEditLayer);
            Progenitor.Networking.Handshake.ClientConnectionEnded += OnClientConnectionEnded;
            _connectionEndedHooked = true;
            Plugin.Logger?.LogInfo("PrefabRegistry: client handlers for key-table order, definition, unregister, key-placeholder, and edit-layer packets.");
        }
        _worldReadyResetSub = EventBus.On<WorldReady>(OnWorldReadyReset, priority: WorldReadyResetPriority);
        _worldReadySub = EventBus.On<WorldReady>(OnWorldReady);
        PrefabEditWireApply.ResetGenerationState();
        PrefabEditWireApply.OnWithdraw = OnEditLayerWithdrawn;
        PrefabEditWireApply.OnApplyPending = NoteEditLayerPending;
        ItemDefinitionDelivery.Install();
        _installed = true;
        EnsureHandshakeWatch();
        EnsureWireFrame();
    }

    /// <summary>
    /// What this module registered that can outlive a collectible Core generation
    /// (EventBus, CustomEvents handlers, Plugin.Frame retry, Harmony fallback).
    /// </summary>
    internal static string DescribePins()
    {
        var holds = new List<string>();
        try
        {
            if (_installed) holds.Add("installed");
            if (_connectedSub != null) holds.Add("EventBus.PlayerConnected");
            if (_worldReadySub != null) holds.Add("EventBus.WorldReady");
            if (_worldReadyResetSub != null) holds.Add("EventBus.WorldReady(reset)");
            if (_connectionEndedHooked) holds.Add("Handshake.ClientConnectionEnded");
            if (_frameRetryHooked) holds.Add("Plugin.Frame");
            if (_editFlushHooked) holds.Add("editFlush");
            if (RefreshSettle.Hooked) holds.Add("refreshSettle=" + RefreshDecisions.Count);
            if (_handshakeWatchHooked) holds.Add("handshakeWatch");
            if (_wireFrameHooked) holds.Add("wireFrame");
            if (_harmony != null) holds.Add("HarmonyFallback");
            var refreshPin = PrefabLiveRefreshApply.DescribePin();
            if (!string.IsNullOrEmpty(refreshPin)) holds.Add(refreshPin);
            lock (Gate)
            {
                if (PendingFrames.Count > 0) holds.Add("pendingFrames=" + PendingFrames.Count);
            }
        }
        catch (Exception e)
        {
            holds.Add("threw:" + e.Message);
        }
        return HarmonySlotHandoff.FormatModulePins(HarmonySlotHandoff.ContentModule, holds);
    }

    /// <summary>
    /// Removes hooks. Live ECS clones and ItemKeyLookup rows stay; managed lists die with the
    /// collectible ALC. Bootstrap <c>ContentMemory</c> is the handoff a subsequent Install
    /// rehydrates before plugins re-Register.
    /// </summary>
    public static void Uninstall()
    {
        _connectedSub?.Dispose();
        _connectedSub = null;
        _worldReadySub?.Dispose();
        _worldReadySub = null;
        _worldReadyResetSub?.Dispose();
        _worldReadyResetSub = null;
        _worldReadyPlan = null;
        if (_connectionEndedHooked)
        {
            Progenitor.Networking.Handshake.ClientConnectionEnded -= OnClientConnectionEnded;
            _connectionEndedHooked = false;
        }
        ItemDefinitionDelivery.Uninstall();
        StopHandshakeWatch();
        StopWireFrame();
        StopEditFlush();
        StopRefreshSettle();
        RefreshDecisions.Clear();
        StopFrameRetry();
        lock (Gate)
        {
            PendingFrames.Clear();
            EditLayerPending.Clear();
            EditLayerGaveUp.Clear();
            JoinSession.Reset();
            JoinRefuseLogged.Clear();
        }
        CustomEvents.RemoveHandler(KeyLedgerPacket.EventId);
        CustomEvents.RemoveHandler(KeyLedgerChunks.EventId);
        LedgerChunks.Reset();
        CustomEvents.RemoveHandler(PrefabDefinitionPacket.EventId);
        CustomEvents.RemoveHandler(ItemStatPacket.EventId);
        ItemStatsBudget.Reset();
        CustomEvents.RemoveHandler(PrefabUnregisterPacket.EventId);
        CustomEvents.RemoveHandler(PrefabUnregisterPacket.KeyPlaceholderEventId);
        CustomEvents.RemoveHandler(PrefabEditWire.EventId);
        PrefabEditWireApply.ResetGenerationState();
        HarmonySlotHandoff.Clear(HarmonySlotHandoff.DeserializeLoadFilePrefix);
        HarmonySlotHandoff.Clear(HarmonySlotHandoff.DeserializeLoadFilePostfix);
        if (_harmony != null && Plugin.IsServerProcess)
        {
            var method = AccessTools.Method(typeof(LoadPersistenceSystemV2), nameof(LoadPersistenceSystemV2.DeserializeLoadFile));
            if (method != null)
            {
                try { _harmony.Unpatch(method, HarmonyPatchType.Prefix, _harmony.Id); }
                catch (Exception e) { Plugin.Logger?.LogError($"PrefabRegistry: unpatch prefix threw: {e}"); }
                try { _harmony.Unpatch(method, HarmonyPatchType.Postfix, _harmony.Id); }
                catch (Exception e) { Plugin.Logger?.LogError($"PrefabRegistry: unpatch postfix threw: {e}"); }
            }
        }
        _harmony = null;
        _installed = false;
        RestoreAllBaselines();
        Edits.Clear();
        // The flush hook is gone and every baseline has just been written back, so a retry
        // count left here would only shorten the budget of a later Install in this generation.
        _removalRetries.Clear();
        _removalNoticed.Clear();
        try { BuffIconApply.Uninstall(); }
        catch (Exception e) { Plugin.Logger?.LogWarning($"PrefabRegistry: icon uninstall threw: {e.Message}"); }
        try { BuffBarTextApply.Uninstall(); }
        catch (Exception e) { Plugin.Logger?.LogWarning($"PrefabRegistry: buff-bar-text uninstall threw: {e.Message}"); }
        try { RecipeApply.Uninstall(); }
        catch (Exception e) { Plugin.Logger?.LogWarning($"PrefabRegistry: recipe uninstall threw: {e.Message}"); }
        try { UnlockPrune.Uninstall(); }
        catch (Exception e) { Plugin.Logger?.LogWarning($"PrefabRegistry: unlock-prune uninstall threw: {e.Message}"); }
        try { PrefabLiveRefreshApply.Uninstall(); }
        catch (Exception e) { Plugin.Logger?.LogWarning($"PrefabRegistry: live-refresh uninstall threw: {e.Message}"); }
        PrefabLiveRefresh.Reset();
        try { PrefabApplicator.UninstallQueries(); }
        catch (Exception e) { Plugin.Logger?.LogWarning($"PrefabRegistry: live-query uninstall threw: {e.Message}"); }
    }

    /// <summary>
    /// Mid-session Core swap: open the apply gate when this side's world already exists so
    /// local re-Registers apply instead of waiting for a deserialize or join packet that
    /// will not arrive. No-op when there is no world (cold start).
    /// </summary>
    internal static void NotifyLiveWorldAfterCoreSwap()
    {
        try
        {
            var world = TargetWorld();
            if (world == null || !world.IsCreated)
            {
                Plugin.Logger?.LogInfo("PrefabRegistry: Core swap with no live world; apply gate stays closed.");
                return;
            }
            var plan = ApplyGate.NotifyLiveWorldAfterCoreSwap();
            if (plan.ApplyPending)
                ApplyPending(world, sendToConnected: false);
            else
                TryRunAllEdits(world, PrefabEditTrigger.Rehydrate);
            // Placeholders wait until plugins re-Register (R11 finding 10) so a missing
            // GUID is not appended at list->Length before the saved index is verified.
            Plugin.Logger?.LogInfo(
                $"PrefabRegistry: live-world gate after Core swap opened={plan.Opened} world={world.Name}.");
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"PrefabRegistry: NotifyLiveWorldAfterCoreSwap threw: {e}");
        }
    }

    /// <summary>
    /// Copy cloned / appended / tombstone GUID sets and ItemKeyLookup indices out of this
    /// generation so Bootstrap can hand them to the next Core ALC. Tombstone hashes and any
    /// still-Applied entries are unioned into the cloned set so a buff (or item) retired with
    /// the entity kept cannot refuse as a foreign GUID after the swap. Uses PrefabApplicator
    /// internals, not private-field names.
    /// </summary>
    internal static void ExportProcessMemory(
        out int[] cloned,
        out int[] appended,
        out int[] appendedIndices,
        out int[] tombHashes,
        out string[] tombIds,
        out int[] tombIndices)
    {
        PrefabApplicator.ExportClonesAndKeys(out cloned, out appended, out appendedIndices);
        KeyTombstone[] tombs;
        int[] applied;
        lock (Gate)
        {
            tombs = Tombstones.Count == 0 ? Array.Empty<KeyTombstone>() : Tombstones.ToArray();
            applied = AppliedHashesNoLock();
        }
        // One unreadable tombstone row must not cost the whole snapshot: this runs from
        // Runtime.SnapshotContent, which throws the cascade away on failure, and an empty
        // memory refuses every clone as a foreign GUID on the far side. Skip the row, keep the
        // rest, and say how many were dropped (R28 finding 16).
        var keptHashes = new int[tombs.Length];
        var keptIds = new string[tombs.Length];
        var keptIndices = new int[tombs.Length];
        int kept = 0;
        int droppedRows = 0;
        for (int i = 0; i < tombs.Length; i++)
        {
            try
            {
                keptHashes[kept] = tombs[i].GuidHash;
                keptIds[kept] = tombs[i].Id ?? "";
                keptIndices[kept] = tombs[i].Index;
                kept++;
            }
            catch (Exception e)
            {
                droppedRows++;
                Plugin.Logger?.LogWarning(
                    "PrefabRegistry: snapshot skipped one tombstone row: " + e.Message);
            }
        }
        if (droppedRows > 0)
        {
            Plugin.Logger?.LogWarning(
                "PrefabRegistry: snapshot kept " + kept + " of " + tombs.Length
                + " tombstone row(s); " + droppedRows + " were unreadable and were skipped.");
        }
        if (kept == tombs.Length)
        {
            tombHashes = keptHashes;
            tombIds = keptIds;
            tombIndices = keptIndices;
        }
        else
        {
            tombHashes = new int[kept];
            tombIds = new string[kept];
            tombIndices = new int[kept];
            Array.Copy(keptHashes, tombHashes, kept);
            Array.Copy(keptIds, tombIds, kept);
            Array.Copy(keptIndices, tombIndices, kept);
        }
        cloned = PrefabApplyBookkeeping.UnionClonedHashes(cloned, applied, tombHashes);
        appended = UnionHashes(appended, tombHashes);
        appendedIndices = AlignIndices(appended, appendedIndices);
        PrefabApplicator.ExportAppliedSources(out var sourceGuids, out var sourceHashes);
        WriteCloneSourcesToMemory(sourceGuids, sourceHashes);
        // R27 finding 1: the live-refresh fingerprint map is a Core static that Uninstall
        // clears, so without this the next generation always compared against null and the
        // opt-in refresh could never fire on a Core swap. Runtime.SnapshotContent runs this
        // before Plugin.Uninstall (Runtime.cs:61-62), so the map is still populated here.
        // A generation that never settled its first apply pass sweeps here instead, so a fossil
        // row cannot leave through the export either (R29 finding 11). No-op after the settle.
        //
        // R43 job FPRF1: this sweep must NOT be the settle. It used to call the same method
        // SettleLiveRefresh does, which set _applyPassSettled itself, and the very next line reads
        // that flag to choose between AuthoritativelyEmpty and Unsettled — so Unsettled was
        // unreachable here and a generation that never applied anything (swap before the world
        // existed, plugin mid-write, module skipped for one generation) declared its own emptiness
        // authoritative and wiped ContentMemory, the only store that crosses a Core swap. The
        // sweep-only entry point leaves the settle state alone, so the status below is decided by
        // whether SettleLiveRefresh actually ran.
        SweepUnclaimedFingerprints();
        PrefabLiveRefresh.ExportFingerprints(
            out var fpGuids, out var fingerprints, out var fpSkipped, out var fpStatus);
        if (fpSkipped > 0)
        {
            Plugin.Logger?.LogWarning(
                "PrefabRegistry: snapshot skipped " + fpSkipped
                + " empty live-refresh fingerprint row(s); " + fpGuids.Length + " still cross the swap.");
        }
        WriteFingerprintsToMemory(fpGuids, fingerprints, fpStatus);
        // R87 finding 1: JoinSession is a Core static. Uninstall resets it, and the next
        // generation's NotifyLiveWorldAfterCoreSwap then ApplyPending in local order.
        // The refused (and accepted) join must ride Bootstrap ContentMemory like CloneOf
        // hashes and live-refresh fingerprints — ints and strings, no entity handle.
        WriteJoinSessionToMemory();
    }

    /// <summary>
    /// Restore cloned / appended / tombstone sets into a fresh Core generation. Must run
    /// after <see cref="Install"/> and before plugins re-Register so <c>WasClonedHere</c>
    /// is true for the surviving ECS clones.
    /// </summary>
    internal static void ImportProcessMemory(
        int[]? cloned,
        int[]? appended,
        int[]? appendedIndices,
        int[]? tombHashes,
        string[]? tombIds,
        int[]? tombIndices)
    {
        PrefabApplicator.ImportClonesAndKeys(cloned, appended, appendedIndices);
        PrefabApplicator.ImportClonesAndKeys(tombHashes, tombHashes, tombIndices);
        if (TryReadCloneSourcesFromMemory(out var sourceGuids, out var sourceHashes))
            PrefabApplicator.ImportAppliedSources(sourceGuids, sourceHashes);
        // Must run before plugins re-Register, like the clone sets above: the first apply of
        // this generation is the one that has to see the previous generation's fingerprint.
        if (TryReadFingerprintsFromMemory(out var fpGuids, out var fingerprints))
        {
            int offered = PrefabLiveRefresh.ImportFingerprints(
                fpGuids, fingerprints, out int merged, out var importStatus);
            string line = PrefabLiveRefresh.FormatFingerprintImport(
                importStatus, offered, fingerprints.Length, merged);
            if (importStatus == PrefabLiveRefresh.FingerprintImport.Taken
                || importStatus == PrefabLiveRefresh.FingerprintImport.AllAlreadyPresent)
                Plugin.Logger?.LogInfo(line);
            else
                Plugin.Logger?.LogWarning(line);
            if (merged > 0)
                Plugin.Logger?.LogInfo(PrefabLiveRefresh.FormatFingerprintHandoff("rehydrated", merged));
        }
        // Must run before NotifyLiveWorldAfterCoreSwap ApplyPending, otherwise a refused
        // join appends custom items in this generation's registration order.
        TryRestoreJoinSessionFromMemory();
        if (tombHashes == null || tombHashes.Length == 0) return;
        lock (Gate)
        {
            for (int i = 0; i < tombHashes.Length; i++)
            {
                string id = tombIds != null && i < tombIds.Length ? tombIds[i] ?? "" : "";
                int idx = tombIndices != null && i < tombIndices.Length ? tombIndices[i] : -1;
                AddTombstoneNoLock(tombHashes[i], id, idx);
            }
        }
    }

    /// <summary>
    /// Write remembered <c>CloneOf</c> hashes into Bootstrap ContentMemory. Independent of
    /// the 6-arg <c>Replace</c> Runtime already calls, so a Core swap still sees the
    /// previous source after FA's reclaim snapshot rehydrates cloned GUIDs.
    /// </summary>
    private static void WriteCloneSourcesToMemory(int[] guids, int[] sources)
    {
        try
        {
            var memory = TryGetContentMemory();
            if (memory == null) return;
            var mi = memory.GetType().GetMethod("ReplaceCloneSources", BindingFlags.Public | BindingFlags.Instance);
            if (mi == null) return;
            // An empty export carries no information (the read side is a merge), and writing it
            // wipes the store for the rest of the process — R28 finding 16's FA half.
            if (guids.Length == 0)
            {
                Plugin.Logger?.LogInfo(
                    "PrefabRegistry: snapshot has no CloneOf hashes; Bootstrap ContentMemory keeps what it holds.");
                return;
            }
            var outcome = mi.Invoke(memory, new object[] { guids, sources });
            LogContentMemoryStore(outcome, "CloneOf hash", guids.Length, sources.Length);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("PrefabRegistry: snapshot CloneOf hashes threw: " + e.Message);
        }
    }

    /// <summary>
    /// Log the outcome of a Bootstrap <c>ContentMemory.Replace*</c> call. The result is a
    /// Bootstrap enum this assembly cannot reference (and an older Bootstrap returns null from
    /// the <c>void</c> overload), so it is reported by name; anything that is not
    /// <c>Replaced</c> means the previous generation's rows were kept, which is loud on purpose.
    /// </summary>
    private static void LogContentMemoryStore(object? outcome, string what, int offered, int values)
    {
        string name = outcome?.ToString() ?? "";
        if (name.Length == 0 || string.Equals(name, "Replaced", StringComparison.Ordinal))
        {
            Plugin.Logger?.LogInfo(
                "PrefabRegistry: stored " + offered + " " + what + "(s) in Bootstrap ContentMemory.");
            return;
        }
        // Cleared is a success, not a refusal: the caller asked for it on positive evidence that
        // the emptiness is real (R29 finding 11).
        if (string.Equals(name, "Cleared", StringComparison.Ordinal))
        {
            Plugin.Logger?.LogInfo(
                "PrefabRegistry: cleared the " + what + " rows in Bootstrap ContentMemory.");
            return;
        }
        Plugin.Logger?.LogWarning(
            "PrefabRegistry: Bootstrap ContentMemory refused the " + what + " snapshot ("
            + name + "; keys=" + offered + " values=" + values
            + ") and kept the rows it already held.");
    }

    private static bool TryReadCloneSourcesFromMemory(out int[] guids, out int[] sources)
    {
        guids = Array.Empty<int>();
        sources = Array.Empty<int>();
        try
        {
            var memory = TryGetContentMemory();
            if (memory == null) return false;
            var mi = memory.GetType().GetMethod("TakeCloneSources", BindingFlags.Public | BindingFlags.Instance);
            if (mi == null) return false;
            var args = new object?[] { null, null };
            mi.Invoke(memory, args);
            guids = args[0] as int[] ?? Array.Empty<int>();
            sources = args[1] as int[] ?? Array.Empty<int>();
            if (guids.Length != sources.Length)
            {
                Plugin.Logger?.LogWarning(
                    "PrefabRegistry: CloneOf hash rehydrate took nothing: the handoff arrays"
                    + " disagree (guids=" + guids.Length + " sources=" + sources.Length
                    + "). A same-id source change after this swap falls back to the NetSnapshot"
                    + " fingerprint.");
                return false;
            }
            return guids.Length > 0;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("PrefabRegistry: rehydrate CloneOf hashes threw: " + e.Message);
            return false;
        }
    }

    /// <summary>
    /// Write the live-refresh fingerprint map into Bootstrap ContentMemory. Same independent
    /// shape as <see cref="WriteCloneSourcesToMemory"/> so the 6-arg <c>Replace</c> handoff in
    /// <c>Runtime.SnapshotContent</c> does not have to grow, and so a Core built with this
    /// change still loads into a Bootstrap that predates it (the method lookup returns null
    /// and the handoff is simply skipped).
    /// </summary>
    private static void WriteFingerprintsToMemory(
        int[] guids,
        string[] fingerprints,
        PrefabLiveRefresh.FingerprintExport status)
    {
        try
        {
            var memory = TryGetContentMemory();
            if (memory == null) return;
            // The write used to run even for an empty map, and an empty write cleared the store.
            // A rehydrate swallowed once (a throw in ImportClonesAndKeys before the fingerprint
            // read) therefore destroyed the handoff for the whole process, silently — R27
            // finding 1's behavior back permanently (R28 finding 16). Since the import merges,
            // an empty array on its own still carries no information.
            //
            // R29 finding 11: but "there genuinely is nothing left" has to be sayable, or a
            // removed definition's row lives forever and is merged into a generation that has no
            // business seeing it. The exporter says which kind of empty this is, and only
            // AuthoritativelyEmpty — this generation completed an apply pass and settled nothing
            // — reaches ClearFingerprints. Unsettled keeps the store and logs. An export that
            // threw never gets here at all: the catch below skips the write entirely.
            if (guids.Length == 0)
            {
                if (status != PrefabLiveRefresh.FingerprintExport.AuthoritativelyEmpty)
                {
                    Plugin.Logger?.LogWarning(PrefabLiveRefresh.FormatFingerprintExport(status, 0));
                    return;
                }
                var clear = memory.GetType().GetMethod("ClearFingerprints", BindingFlags.Public | BindingFlags.Instance);
                if (clear == null)
                {
                    Plugin.Logger?.LogWarning(
                        "PrefabRegistry: this Bootstrap has no ClearFingerprints; the store keeps"
                        + " rows this generation can no longer account for. Restart both sides to"
                        + " clear them.");
                    return;
                }
                Plugin.Logger?.LogInfo(PrefabLiveRefresh.FormatFingerprintExport(status, 0));
                LogContentMemoryStore(clear.Invoke(memory, null), "live-refresh fingerprint", 0, 0);
                return;
            }
            var mi = memory.GetType().GetMethod("ReplaceFingerprints", BindingFlags.Public | BindingFlags.Instance);
            if (mi == null) return;
            var outcome = mi.Invoke(memory, new object[] { guids, fingerprints });
            LogContentMemoryStore(outcome, "live-refresh fingerprint", guids.Length, fingerprints.Length);
            Plugin.Logger?.LogInfo(PrefabLiveRefresh.FormatFingerprintHandoff("snapshot", guids.Length));
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("PrefabRegistry: snapshot live-refresh fingerprints threw: " + e.Message);
        }
    }

    private static bool TryReadFingerprintsFromMemory(out int[] guids, out string[] fingerprints)
    {
        guids = Array.Empty<int>();
        fingerprints = Array.Empty<string>();
        try
        {
            var memory = TryGetContentMemory();
            if (memory == null) return false;
            var mi = memory.GetType().GetMethod("TakeFingerprints", BindingFlags.Public | BindingFlags.Instance);
            if (mi == null)
            {
                Plugin.Logger?.LogWarning(
                    "PrefabRegistry: this Bootstrap has no TakeFingerprints; the live-refresh"
                    + " fingerprint handoff is unavailable until the process is restarted.");
                return false;
            }
            var args = new object?[] { null, null };
            mi.Invoke(memory, args);
            guids = args[0] as int[] ?? Array.Empty<int>();
            fingerprints = args[1] as string[] ?? Array.Empty<string>();
            // Fail loud, with counts: a silent false here is indistinguishable from "there was
            // nothing to carry", and the consequence (no previous fingerprint, so the next
            // definition comparison can never see a change) is invisible otherwise.
            if (guids.Length == 0)
            {
                Plugin.Logger?.LogWarning(PrefabLiveRefresh.FormatFingerprintImport(
                    PrefabLiveRefresh.FingerprintImport.NothingOffered, 0, fingerprints.Length, 0));
                return false;
            }
            if (guids.Length != fingerprints.Length)
            {
                Plugin.Logger?.LogWarning(PrefabLiveRefresh.FormatFingerprintImport(
                    PrefabLiveRefresh.FingerprintImport.LengthMismatch, guids.Length, fingerprints.Length, 0));
                return false;
            }
            return true;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("PrefabRegistry: rehydrate live-refresh fingerprints threw: " + e.Message);
            return false;
        }
    }

    private static object? TryGetContentMemory()
    {
        try
        {
            var asms = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < asms.Length; i++)
            {
                var name = asms[i].GetName().Name;
                if (!string.Equals(name, "Progenitor.Bootstrap", StringComparison.Ordinal))
                    continue;
                var host = asms[i].GetType("Progenitor.Bootstrap.ProcessHost");
                var prop = host?.GetProperty("Content", BindingFlags.Public | BindingFlags.Static);
                var fromHost = prop?.GetValue(null);
                if (fromHost != null) return fromHost;
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("PrefabRegistry: ContentMemory lookup threw: " + e.Message);
        }
        return Runtime.HostContentMemory();
    }

    /// <summary>
    /// Write the client join session into Bootstrap ContentMemory. Same independent
    /// reflection shape as <see cref="WriteCloneSourcesToMemory"/> / fingerprints so
    /// the 6-arg <c>Replace</c> handoff does not grow, and a Core built with this
    /// change still loads into a Bootstrap that predates it (method lookup returns null).
    /// </summary>
    private static void WriteJoinSessionToMemory()
    {
        try
        {
            KeyJoinSnapshot snap;
            lock (Gate) snap = JoinSession.Capture();
            Plugin.Logger?.LogInfo(
                "PrefabRegistry: snapshot key-table join session refused="
                + (snap.Refused ? "true" : "false")
                + " accepted=" + (snap.Accepted ? "true" : "false")
                + " rows=" + (snap.Rows == null ? 0 : snap.Rows.Count) + ".");
            var memory = TryGetContentMemory();
            if (memory == null)
            {
                Plugin.Logger?.LogWarning(
                    "PrefabRegistry: ContentMemory lookup returned null; the key-table join session was not stored.");
                return;
            }
            var mi = KeyJoinHandoff.FindMethod(memory.GetType(), KeyJoinHandoff.ReplaceMethodName);
            if (mi == null)
            {
                Plugin.Logger?.LogWarning(KeyJoinHandoff.MissingMethodReason(KeyJoinHandoff.ReplaceMethodName));
                return;
            }
            snap.Pack(
                out var header,
                out var rowGuids,
                out var rowIds,
                out var rowIndices,
                out var rowKinds,
                out var rowUsable,
                out var rowReasons,
                out var unknownGuids,
                out var unknownIds,
                out var unknownReasons,
                out var reason);
            var outcome = mi.Invoke(memory, new object[]
            {
                header, rowGuids, rowIds, rowIndices, rowKinds, rowUsable, rowReasons,
                unknownGuids, unknownIds, unknownReasons, reason,
            });
            LogContentMemoryStore(outcome, "key-table join session", header.Length, rowGuids.Length);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("PrefabRegistry: snapshot key-table join session threw: " + e.Message);
        }
    }

    private static void TryRestoreJoinSessionFromMemory()
    {
        try
        {
            var memory = TryGetContentMemory();
            bool failClosed = !Plugin.IsServerProcess;
            bool liveWorld = false;
            try
            {
                var world = TargetWorld();
                liveWorld = world != null && world.IsCreated;
            }
            catch
            {
                liveWorld = false;
            }
            string log;
            bool restored;
            lock (Gate)
            {
                restored = KeyJoinHandoff.TryRestoreInto(
                    memory,
                    KeyJoinHandoff.FindMethod,
                    JoinSession,
                    failClosed,
                    liveWorld,
                    out log);
            }
            if (string.IsNullOrEmpty(log)) return;
            if (restored)
                Plugin.Logger?.LogInfo(log);
            else
                Plugin.Logger?.LogWarning(log);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("PrefabRegistry: rehydrate key-table join session threw: " + e.Message);
            if (!Plugin.IsServerProcess)
            {
                string why = KeyJoinHandoff.RestoreFailedReason(e.Message);
                lock (Gate) JoinSession.RefusePacket(why);
                Plugin.Logger?.LogWarning(why);
            }
        }
    }

    private static void ClearJoinSessionInMemory()
    {
        try
        {
            var memory = TryGetContentMemory();
            if (memory == null) return;
            var mi = KeyJoinHandoff.FindMethod(memory.GetType(), KeyJoinHandoff.ClearMethodName);
            if (mi == null)
            {
                Plugin.Logger?.LogWarning(KeyJoinHandoff.MissingMethodReason(KeyJoinHandoff.ClearMethodName));
                return;
            }
            LogContentMemoryStore(mi.Invoke(memory, null), "key-table join session", 0, 0);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("PrefabRegistry: clear key-table join session threw: " + e.Message);
        }
    }

    /// <summary>
    /// After plugins re-Register, apply key-table placeholders for tombstones still missing
    /// at their saved index. Refuses a GUID whose saved slot no longer holds it.
    /// </summary>
    internal static void ApplyKeyPlaceholdersAfterPlugins()
    {
        try
        {
            ApplyKeyPlaceholders(TargetWorld());
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"PrefabRegistry: ApplyKeyPlaceholdersAfterPlugins threw: {e}");
        }
    }

    private static int[] AlignIndices(int[] guids, int[] indices)
    {
        if (guids == null || guids.Length == 0) return Array.Empty<int>();
        if (indices != null && indices.Length == guids.Length) return indices;
        var aligned = new int[guids.Length];
        for (int i = 0; i < aligned.Length; i++)
            aligned[i] = i < (indices?.Length ?? 0) ? indices![i] : -1;
        return aligned;
    }

    private static int[] UnionHashes(int[] left, int[] right)
    {
        if (right == null || right.Length == 0) return left ?? Array.Empty<int>();
        if (left == null || left.Length == 0) return (int[])right.Clone();
        var set = new HashSet<int>(left);
        for (int i = 0; i < right.Length; i++)
            set.Add(right[i]);
        if (set.Count == left.Length) return left;
        var copy = new int[set.Count];
        set.CopyTo(copy);
        return copy;
    }

    // ---- builder ------------------------------------------------------------------------------

    /// <summary>
    /// Fluent description of one cloned item. <see cref="CloneOf(PrefabGUID)"/> is required;
    /// <see cref="Register"/> is what actually inserts the definition and, when the world is
    /// ready, writes it into the game's lookups.
    /// </summary>
    public sealed class ItemDefinitionBuilder
    {
        private readonly string _id;
        private PrefabGUID _source;
        private string? _sourceName;
        private int? _maxAmount;
        private string? _localizedName;
        private string? _localizedDescription;
        private WearableGraphics? _wearable;
        private ItemArmorBaseline? _armorBaseline;
        private ItemStatBonuses? _statBonuses;
        private ItemKeepPolicy? _keep;
        private string? _iconPath;
        private bool _registered;

        internal ItemDefinitionBuilder(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
                throw new ArgumentException("Item id is required.", nameof(id));
            ValidateId(id);
            _id = id;
        }

        /// <summary>Vanilla (or already-registered) prefab to clone, identified by GUID.</summary>
        public ItemDefinitionBuilder CloneOf(PrefabGUID source)
        {
            _source = source;
            _sourceName = null;
            return this;
        }

        /// <summary>
        /// Vanilla prefab to clone, identified by spawnable name
        /// (for example <c>Item_Ingredient_Crystal</c>). Resolved at apply time, so a plugin can
        /// call this before the collection is populated.
        /// </summary>
        public ItemDefinitionBuilder CloneOf(string spawnableName)
        {
            if (string.IsNullOrEmpty(spawnableName))
                throw new ArgumentException("Spawnable name is required.", nameof(spawnableName));
            _sourceName = spawnableName;
            _source = default;
            return this;
        }

        /// <summary>Override the clone's stack size. Omit to keep the source's <c>ItemData.MaxAmount</c>.</summary>
        public ItemDefinitionBuilder MaxAmount(int maxAmount)
        {
            if (maxAmount <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxAmount), "MaxAmount must be positive.");
            _maxAmount = maxAmount;
            return this;
        }

        /// <summary>
        /// Display name sent to the client (1–63 ASCII bytes, same bound as the id). The client
        /// registers it under <see cref="PrefabDefinition.Id"/> and points the cloned
        /// <c>ManagedItemData</c> at that key so inventory UI shows this text instead of the
        /// source prefab's name. Validated here so <see cref="Register"/> cannot accept a name
        /// that <see cref="PrefabDefinitionPacket"/> cannot encode.
        /// </summary>
        public ItemDefinitionBuilder Named(string localizedName)
        {
            if (localizedName == null)
                throw new ArgumentNullException(nameof(localizedName));
            PrefabWire.ValidateAscii(localizedName, nameof(localizedName), "localized name");
            _localizedName = localizedName;
            return this;
        }

        /// <summary>
        /// Tooltip / description sent to the client (1–63 ASCII bytes, same bound as the id and
        /// as <see cref="Named"/>). The client registers it under <c>id.desc</c> and points
        /// <c>ManagedItemData.Description.Key</c> at that key. Omit to keep the source prefab's
        /// description, unless Localization already has <c>id.desc</c> from a plugin JSON file.
        /// Validated here so <see cref="Register"/> cannot accept a string the packet cannot encode.
        /// </summary>
        public ItemDefinitionBuilder Described(string text)
        {
            if (text == null)
                throw new ArgumentNullException(nameof(text));
            PrefabWire.ValidateAscii(text, nameof(text), "localized description");
            _localizedDescription = text;
            return this;
        }

        /// <summary>
        /// Worn graphics for this item: a sketch built on the wearer's bones, donor
        /// renderers to hide, and optional male/female model prefab names. The item
        /// still has to be an equippable clone (<c>CloneOf</c> an armor or weapon).
        /// See <c>docs/wearables.md</c>.
        /// </summary>
        public ItemDefinitionBuilder Wearable(WearableGraphics graphics)
        {
            if (graphics == null) throw new ArgumentNullException(nameof(graphics));
            _wearable = graphics;
            return this;
        }

        /// <summary>
        /// Set a visible armor tier on this owned item clone (tier 1 is source level 10).
        /// Optionally remove inherited item stat rows and the donor's set association.
        /// Register the same definition on server and client; this setting is local to
        /// each clone and is not an arbitrary-prefab edit or a PrfE wire record.
        /// </summary>
        public ItemDefinitionBuilder ArmorBaseline(int displayedLevel, bool removeBonuses = true, bool removeSet = true)
        {
            _armorBaseline = new ItemArmorBaseline(displayedLevel, removeBonuses, removeSet);
            return this;
        }

        /// <summary>Add explicit health and power bonuses to the donor's equipment stats.
        /// Reapplication replaces authored rows, rather than stacking them. Experimental; requires compatible peers.</summary>
        public ItemDefinitionBuilder StatBonuses(ItemStatBonuses bonuses)
        {
            _statBonuses = bonuses ?? throw new ArgumentNullException(nameof(bonuses));
            return this;
        }

        /// <summary>
        /// Keep this item on death: sets <c>ItemData.ItemCategory.BloodBound</c> on the clone and
        /// its item lookup row, which the death drop reads, and on existing server instances.
        /// Applies in the bag too. With the server's blood-bound equipment setting off, the game
        /// strips the bit only while registering its own prefabs, before clones exist (traced, not
        /// live-verified); the server re-checks the clone and its row on every player connect and
        /// re-sets the bit with a warning if it was cleared.
        /// Register the same definition on server and client; not a wire field.
        /// </summary>
        public ItemDefinitionBuilder BloodBound()
        {
            _keep = (_keep ?? new ItemKeepPolicy(false, false)).With(bloodBound: true);
            return this;
        }

        /// <summary>
        /// Never lose durability: <c>Durability.LossType</c> None, take-damage factor 0,
        /// <c>DestroyItemWhenBroken</c> false and full durability on the clone, and
        /// <c>LoseDurabilityOnDeath</c> cleared from its category, so death drains nothing.
        /// Existing server instances, worn or not, are rearmed at registration and at the first
        /// player connect of each world load. Register the same definition on server and client;
        /// not a wire field.
        /// </summary>
        public ItemDefinitionBuilder Indestructible()
        {
            _keep = (_keep ?? new ItemKeepPolicy(false, false)).With(indestructible: true);
            return this;
        }

        /// <summary>
        /// Inventory PNG relative to this plugin's assets folder. Client-local; register
        /// the same definition on both peers and ship the image with the client plugin.
        /// Missing or invalid images keep the source icon.
        /// </summary>
        public ItemDefinitionBuilder Icon(string relativePath)
        {
            PrefabIconPath.Validate(relativePath, nameof(relativePath));
            _iconPath = PrefabIconPath.Normalize(relativePath);
            return this;
        }

        /// <summary>
        /// Accepts the definition into the registry under <paramref name="owner"/>. Returns a
        /// handle that soft-unapplies it (undo list + bookkeeping; the clone stays resolvable)
        /// and drops it from the client-sync list. Dispose from a reloadable plugin's <c>Unload</c>.
        ///
        /// A spawnable name that already maps to a GUID this process did not clone is refused
        /// even when that GUID equals <c>CalculateGuidHash(id)</c>. The API does not throw for
        /// that (apply often runs after OnLoad, once the collection is populated): the returned
        /// handle is already invalidated (Dispose is a no-op), the definition is not kept in
        /// <see cref="Definitions"/>, not sent as <c>PrfD</c>, and not tombstoned. The refusal
        /// is logged. A later non-pending apply failure of a not-yet-applied entry drops the
        /// same way. Pending failures (source collection not ready) stay queued. On the client,
        /// apply waits for the first <c>PrfD</c> or <c>PrfK</c> of the join; a local-only
        /// registration (single-player, or a server without this mod) is never applied — the
        /// frame retry logs once after <see cref="PrefabWire.ApplyGiveUpFrames"/> and stops.
        /// Client-only items require that server packet.
        /// </summary>
        public IDisposable Register(object owner)
        {
            if (owner == null) throw new ArgumentNullException(nameof(owner));
            if (_registered)
                throw new InvalidOperationException($"DefineItem('{_id}') has already been Register()'d.");
            if (_statBonuses != null && _armorBaseline != null)
                throw new InvalidOperationException("StatBonuses and ArmorBaseline cannot be combined in this API version.");
            if (_source.IsEmpty() && string.IsNullOrEmpty(_sourceName))
                throw new InvalidOperationException($"DefineItem('{_id}') requires CloneOf(...) before Register.");

            _registered = true;
            int hash = PrefabGUID.CalculateGuidHash(_id);
            var guid = new PrefabGUID(hash);
            var def = new PrefabDefinition(
                _id,
                guid,
                _source,
                _sourceName,
                _maxAmount ?? 0,
                _localizedName,
                _localizedDescription,
                owner,
                PrefabKind.Item,
                iconPath: _iconPath);
            if (_wearable != null) def.AssignWearable(_wearable);
            if (_armorBaseline != null) def.AssignArmorBaseline(_armorBaseline);
            if (_statBonuses != null) def.AssignStatBonuses(_statBonuses);
            if (_keep != null) def.AssignKeep(_keep);
            return Add(def);
        }
    }

    /// <summary>
    /// Fluent description of one cloned buff. <see cref="CloneOf(PrefabGUID)"/> is required;
    /// <see cref="Register"/> inserts the definition and, when the world is ready, writes it
    /// into the collection maps and sends <c>PrfD</c>. No <c>MaxAmount</c>: buffs are not items.
    /// </summary>
    public sealed class BuffDefinitionBuilder
    {
        private readonly string _id;
        private PrefabGUID _source;
        private string? _sourceName;
        private string? _localizedName;
        private string? _localizedDescription;
        private string? _iconPath;
        private bool _refreshLiveInstances;
        private bool _registered;

        internal BuffDefinitionBuilder(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
                throw new ArgumentException("Buff id is required.", nameof(id));
            ValidateId(id);
            _id = id;
        }

        /// <summary>Vanilla (or already-registered) buff prefab to clone, identified by GUID.</summary>
        public BuffDefinitionBuilder CloneOf(PrefabGUID source)
        {
            _source = source;
            _sourceName = null;
            return this;
        }

        /// <summary>
        /// Vanilla buff to clone, identified by spawnable name
        /// (for example <c>Buff_General_Haste</c>). Resolved at apply time, so a plugin can
        /// call this before the collection is populated.
        /// </summary>
        public BuffDefinitionBuilder CloneOf(string spawnableName)
        {
            if (string.IsNullOrEmpty(spawnableName))
                throw new ArgumentException("Spawnable name is required.", nameof(spawnableName));
            _sourceName = spawnableName;
            _source = default;
            return this;
        }

        /// <summary>
        /// Display name sent to the client (1–63 ASCII, same bound as the id). The client
        /// registers it under <c>id.name</c> and writes <c>ShowOnBuffBarData.Entries[i].Header</c>
        /// so the hover tooltip shows this text instead of the source prefab's name. Same
        /// display-text semantics as <see cref="ItemDefinitionBuilder.Named"/>. Without
        /// <see cref="Described"/> (and no <c>id.desc</c>), <c>UseParentNameAndDescription</c>
        /// stays so the source tooltip is kept; call both to take over the HUD.
        /// </summary>
        public BuffDefinitionBuilder Named(string localizedName)
        {
            if (localizedName == null)
                throw new ArgumentNullException(nameof(localizedName));
            PrefabWire.ValidateAscii(localizedName, nameof(localizedName), "localized name");
            _localizedName = localizedName;
            return this;
        }

        /// <summary>
        /// Tooltip / description sent to the client (1–63 ASCII). The client registers it
        /// under <c>id.desc</c> and writes <c>ShowOnBuffBarData.Entries[i].Description.Key</c>.
        /// Together with <see cref="Named"/> this clears <c>UseParentNameAndDescription</c>
        /// so the source tooltip does not win. Omit to keep the source, unless Localization
        /// already has <c>id.desc</c> from a plugin JSON file.
        /// </summary>
        public BuffDefinitionBuilder Described(string text)
        {
            if (text == null)
                throw new ArgumentNullException(nameof(text));
            PrefabWire.ValidateAscii(text, nameof(text), "localized description");
            _localizedDescription = text;
            return this;
        }

        /// <summary>
        /// Client HUD icon: a PNG path relative to this plugin's <c>assets</c> folder
        /// (for example <c>icons/nodrain.png</c>). Deploy copies that folder to
        /// <c>&lt;reload&gt;/&lt;AssemblyName&gt;/assets</c>, the same probe Localization
        /// uses for JSON. Absolute paths and <c>..</c> are refused. The path is not sent
        /// on <c>PrfD</c> — the client plugin registers the same <c>DefineBuff</c> and
        /// the applicator loads the file after the clone. Omit to keep the source sprite.
        /// </summary>
        public BuffDefinitionBuilder Icon(string relativePath)
        {
            if (relativePath == null)
                throw new ArgumentNullException(nameof(relativePath));
            PrefabIconPath.Validate(relativePath, nameof(relativePath));
            _iconPath = PrefabIconPath.Normalize(relativePath);
            return this;
        }

        /// <summary>
        /// Opt-in: when this clone is re-applied after a changed definition (plugin
        /// reload, same-id retarget, or an edit pass that wrote the clone), destroy
        /// live instances through <c>DestroyUtility.Destroy(..., TryRemoveBuff)</c> and
        /// re-apply through <c>DebugEventsSystem.ApplyBuff</c>, one per frame. Default
        /// off because a plugin that stacks state on its buff would lose that state.
        /// Never runs on first process start. Client instances are not destroyed
        /// (replication follows the server); HUD icon/name are nudged onto existing
        /// instances without removing buffers.
        /// </summary>
        public BuffDefinitionBuilder RefreshLiveInstances()
        {
            _refreshLiveInstances = true;
            return this;
        }

        private string _passVersion = "";

        /// <summary>Core-internal: see <see cref="PrefabDefinition.PassVersion"/>.</summary>
        internal BuffDefinitionBuilder PassVersion(string version)
        {
            _passVersion = version ?? "";
            return this;
        }

        /// <summary>
        /// Accepts the definition into the registry under <paramref name="owner"/>. Same
        /// handle / soft-unapply / <c>PrfU</c> contract as
        /// <see cref="ItemDefinitionBuilder.Register"/>. Buffs are not tombstoned as
        /// <c>PrfK</c> (they never occupy an ItemKeyLookup row).
        /// </summary>
        public IDisposable Register(object owner)
        {
            if (owner == null) throw new ArgumentNullException(nameof(owner));
            if (_registered)
                throw new InvalidOperationException($"DefineBuff('{_id}') has already been Register()'d.");
            if (_source.IsEmpty() && string.IsNullOrEmpty(_sourceName))
                throw new InvalidOperationException($"DefineBuff('{_id}') requires CloneOf(...) before Register.");

            _registered = true;
            int hash = PrefabGUID.CalculateGuidHash(_id);
            var guid = new PrefabGUID(hash);
            var def = new PrefabDefinition(
                _id,
                guid,
                _source,
                _sourceName,
                maxAmount: 0,
                _localizedName,
                _localizedDescription,
                owner,
                PrefabKind.Buff,
                _iconPath,
                _refreshLiveInstances);
            def.PassVersion = _passVersion;
            return Add(def);
        }
    }

    // ---- registration -------------------------------------------------------------------------

    private static IDisposable Add(PrefabDefinition def)
    {
        var handle = new RegistrationHandle();
        Entry? replaced = null;
        lock (Gate)
        {
            for (int i = Entries.Count - 1; i >= 0; i--)
            {
                if (Entries[i].Definition.Guid.GuidHash != def.Guid.GuidHash) continue;
                replaced = Entries[i];
                Entries.RemoveAt(i);
                Plugin.Logger?.LogWarning(
                    $"PrefabRegistry: '{def.Id}' ({def.Guid.GuidHash}) from {DescribeOwner(def.Owner)} replaces the registration from {DescribeOwner(replaced.Definition.Owner)}.");
                break;
            }
            Entries.Add(new Entry(def, handle));
            RemoveTombstoneNoLock(def.Guid.GuidHash);
        }

        // The reloader publishes file provenance after OnLoad returns. Record on the next frame.
        if (Plugin.IsServerProcess && def.Kind == PrefabKind.Item)
            Dev.MainThread.Post(() => { foreach (var current in Definitions) if (ReferenceEquals(current, def)) { ItemRemovalRuntime.NoteRegistered(def); break; } });

        // Keys live with the owner registration, not the first apply / definition undo.
        def.EnsureLocalizedKeys();

        if (replaced != null)
        {
            PrefabApplicator.RememberAppliedIdentity(
                replaced.Definition.Guid, replaced.Definition.Source, replaced.Definition.Kind);
            UnapplyEntry(replaced);
        }

        TryApplyNow(def, sendToConnected: Plugin.IsServerProcess);
        if (IsLiveEntry(def))
            RaiseRegistered(def);
        return handle;
    }

    private static void Unregister(RegistrationHandle handle)
    {
        Entry? removed = null;
        lock (Gate)
        {
            for (int i = Entries.Count - 1; i >= 0; i--)
            {
                if (!ReferenceEquals(Entries[i].Handle, handle)) continue;
                removed = Entries[i];
                Entries.RemoveAt(i);
                break;
            }
        }
        if (removed != null)
            UnapplyEntry(removed);
    }

    private static void UnapplyEntry(Entry entry)
    {
        ClearPending(entry.Definition.Guid.GuidHash);
        entry.Handle.Invalidate();
        bool entityKept = false;
        try
        {
            // Soft TryUnapply: RunUndo (localization) even when the world is missing; never
            // removes map entries or destroys the clone (inventories still hold the GUID).
            bool ok = PrefabApplicator.TryUnapply(TargetWorld(), entry.Definition, out var report, out entityKept);
            bool keepOwnership = PrefabApplyBookkeeping.ShouldRememberRetiredClone(entityKept, entry.Applied);
            if (keepOwnership)
                PrefabApplicator.RememberClone(entry.Definition.Guid);
            Plugin.Logger?.LogInfo(
                $"PrefabRegistry: unregistered '{entry.Definition.Id}': ok={ok}; {report}{PrefabApplyBookkeeping.FormatRetiredOwnership(keepOwnership)}");
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"PrefabRegistry: unregister '{entry.Definition.Id}' threw: {e}");
        }
        if (Plugin.IsServerProcess)
            RememberTombstoneIfKeyRowExists(entry.Definition);
        // Notify connected clients even if this process had no world to unapply into (the
        // definition may still be live on a client that received it at join). Unregister first
        // (soft: undo + bookkeeping), then a key-table placeholder so nobody's ItemKeyLookup
        // shrinks. The placeholder is append-if-missing for clients that never had the row,
        // and already-present for everyone else. A re-register of this id clears the tombstone
        // and updates the surviving clone.
        SendUnregister(entry.Definition);
    }

    private static string DefinitionFingerprint(PrefabDefinition def)
    {
        string editStamp = "";
        try { editStamp = Edits.Stamp(def.Guid.GuidHash); }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("PrefabRegistry: edit stamp for '" + def.Id + "' threw: " + e.Message);
        }
        return PrefabLiveRefresh.Fingerprint(
            def.Kind,
            def.Source.GuidHash,
            def.SourceName,
            def.LocalizedName,
            def.LocalizedDescription,
            def.IconPath,
            string.IsNullOrEmpty(def.PassVersion) ? editStamp : editStamp + "|pass=" + def.PassVersion);
    }

    private static void TryApplyNow(PrefabDefinition def, bool sendToConnected)
    {
        var world = TargetWorld();
        if (world == null) return;
        ApplyOne(world, def, sendToConnected);
    }

    private static void ApplyPending(World? world, bool sendToConnected)
    {
        if (world == null || !world.IsCreated) return;
        KeyAgreementDecision? plan;
        lock (Gate) plan = JoinSession.Plan;
        if (plan != null && plan.Accepted)
        {
            ApplyPendingInServerOrder(world, sendToConnected, plan);
            return;
        }
        PrefabDefinition[] snapshot;
        lock (Gate)
        {
            if (Entries.Count == 0) return;
            snapshot = new PrefabDefinition[Entries.Count];
            for (int i = 0; i < Entries.Count; i++)
                snapshot[i] = Entries[i].Definition;
        }
        foreach (var def in snapshot)
            ApplyOne(world, def, sendToConnected);
        // Vanilla GUIDs (no clone in this registry) still get their ordered pass once
        // the collection exists. Clones were already edited inside ApplyOne; CollectionReady
        // skips a GUID whose fingerprint matches the last successful pass.
        TryRunAllEdits(world, PrefabEditTrigger.CollectionReady);
    }

    /// <summary>
    /// Client join after <c>PrfI</c>: occupy the server's tail in order (clone when the
    /// local item is usable, reserved key row when it is not), then apply buffs, which
    /// never take a key-table row.
    /// </summary>
    private static void ApplyPendingInServerOrder(World world, bool sendToConnected, KeyAgreementDecision plan)
    {
        PrefabDefinition[] snapshot;
        lock (Gate)
        {
            snapshot = Entries.Count == 0
                ? Array.Empty<PrefabDefinition>()
                : new PrefabDefinition[Entries.Count];
            for (int i = 0; i < Entries.Count; i++)
                snapshot[i] = Entries[i].Definition;
        }

        for (int i = 0; i < plan.Rows.Count; i++)
        {
            var row = plan.Rows[i];
            PrefabDefinition? item = null;
            for (int e = 0; e < snapshot.Length; e++)
            {
                if (snapshot[e].Guid.GuidHash != row.GuidHash) continue;
                if (PrefabApplyBookkeeping.ShouldAppendItemKey(snapshot[e].Kind))
                    item = snapshot[e];
                break;
            }
            if (row.Usable && item != null)
            {
                ApplyOne(world, item, sendToConnected);
                continue;
            }
            if (row.OccupiesRow)
                TryAppendKeyPlaceholder(world, row.GuidHash, row.Id, savedIndex: -1);
            if (!string.IsNullOrEmpty(row.Reason))
                LogJoinRefuseOnce(row.GuidHash, row.Reason);
        }

        for (int e = 0; e < snapshot.Length; e++)
        {
            var def = snapshot[e];
            if (PrefabApplyBookkeeping.ShouldAppendItemKey(def.Kind)) continue;
            ApplyOne(world, def, sendToConnected);
        }

        TryRunAllEdits(world, PrefabEditTrigger.CollectionReady);
    }

    private static void LogJoinRefuseOnce(int guidHash, string reason)
    {
        lock (Gate)
        {
            if (!JoinRefuseLogged.Add(guidHash)) return;
        }
        Plugin.Logger?.LogWarning(reason);
    }

    private static bool ShouldSkipItemForJoinAgreement(PrefabDefinition def, out string reason)
    {
        // Client live-world gate: idle is not permission to append in local order.
        // Server / host process is the authority (no PrfI). WorldReady keeps Ready
        // false so NotePending still runs.
        bool requireAgreedOrder = !Plugin.IsServerProcess && ApplyGate.Ready;
        lock (Gate)
        {
            return JoinSession.ShouldSkipItemAppend(
                def.Kind, def.Guid.GuidHash, def.Id, requireAgreedOrder, out reason);
        }
    }

    private static void ResetJoinSessionNoLock()
    {
        LedgerChunks.Reset();
        JoinSession.Reset();
        JoinRefuseLogged.Clear();
    }

    /// <summary>
    /// Run every editor queued for <paramref name="guidHash"/> in deterministic order.
    /// No-op when the collection does not yet have the entity (a later apply seam retries).
    ///
    /// Triggers that re-run: <see cref="PrefabEditTrigger.NewClone"/> (InsertNew /
    /// UpdateExisting just reset values to the definition), <see cref="PrefabEditTrigger.EditorsChanged"/>,
    /// <see cref="PrefabEditTrigger.Rehydrate"/> (Core swap when ApplyPending did not run),
    /// <see cref="PrefabEditTrigger.InputsChanged"/> (<see cref="Invalidate"/> /
    /// <see cref="InvalidateEdits"/> when captured editor values changed).
    /// <see cref="PrefabEditTrigger.CollectionReady"/> is idempotent: skipped when this GUID
    /// already ran with the current editor fingerprint (plugin id + priority + index +
    /// optional version + invalidation generation). <see cref="PrefabEditTrigger.PlaceholderSend"/>
    /// never runs — sending <c>PrfD</c>/<c>PrfK</c> to a joining client does not create a clone
    /// (evidence 73 logged the same conflict once per apply seam before this skip).
    /// Distinct conflicts log once per Core generation (dedupe key: guid + component + the
    /// two plugin ids) and re-log after a reload or when the editor set for that prefab changes.
    /// </summary>
    private static bool TryRunEdits(int guidHash, World? world, PrefabEditTrigger trigger)
        => TryRunEdits(guidHash, world, trigger, out _);

    /// <summary>
    /// <see cref="TryRunEdits(int, World?, PrefabEditTrigger)"/> reporting what the pass did.
    /// The removal flush needs the outcome, not the touch count: a pass that never restored has
    /// to be re-queued (R49 finding 6) and nothing about <c>anyTouches</c> can say so.
    /// </summary>
    private static bool TryRunEdits(
        int guidHash, World? world, PrefabEditTrigger trigger, out PrefabEditPassOutcome outcome)
    {
        outcome = PrefabEditPassOutcome.Threw;
        bool anyTouches = false;
        RegistryEditPass? pass = null;
        try
        {
            SweepHandshakeIfChanged();
            world ??= TargetWorld();
            pass = new RegistryEditPass(guidHash, world, trigger);
            outcome = PrefabEditPass.Run(trigger, pass);
            anyTouches = pass.AnyTouches;

            if (pass.Again)
            {
                if (_editRerunDepth > 0)
                    ScheduleEditFlush();
                else
                {
                    _editRerunDepth++;
                    try { anyTouches |= TryRunEdits(guidHash, world, PrefabEditTrigger.InputsChanged); }
                    finally { _editRerunDepth--; }
                }
            }

            // The wire half of the pass (job FEDW1). The server ships what its editors just
            // wrote; the client puts the server's layer back on top, because a local editor must
            // not win over the authoritative values. Inside ApplyOne the send waits: PrfD has not
            // gone out yet there, and PrfE must land after the definition it edits.
            if (world != null)
            {
                if (Plugin.IsServerProcess)
                {
                    if (anyTouches && _applyOneDepth == 0)
                        PrefabEditWireApply.SendToConnected(world, Edits, guidHash);
                    else if (_applyOneDepth == 0
                             && outcome is PrefabEditPassOutcome.DroppedThenRan
                                 or PrefabEditPassOutcome.RestoredThenRan)
                        // The pass ran and touched nothing, so whatever layer is out on the wire
                        // is no longer what this server's editors produce. Withdraw it rather
                        // than leaving clients carrying values nothing here still writes
                        // (job FEDW3, R51 finding 7). Idempotent: a prefab with no layer out
                        // takes no packet.
                        PrefabEditWireApply.SendWithdraw(world, Edits, guidHash);
                }
                else if (outcome is PrefabEditPassOutcome.DroppedThenRan
                         or PrefabEditPassOutcome.RestoredThenRan)
                {
                    PrefabEditWireApply.ApplyReceived(world, Edits, guidHash, out bool stillPending);
                    NoteEditLayerPending(guidHash, stillPending);
                }
            }

            if (anyTouches && world != null && _applyOneDepth == 0)
                ConsiderEditRefresh(world, guidHash, trigger);
            return anyTouches;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"PrefabRegistry: TryRunEdits({guidHash}) threw: {e}");
            return anyTouches || (pass?.AnyTouches ?? false);
        }
    }

    /// <summary>
    /// <see cref="IPrefabEditPass"/> against the live world: every game-typed step of the pass,
    /// with the ladder itself in <see cref="PrefabEditPass.Run"/> where the suite can drive it
    /// (job FEDIT2). One instance per pass; the two fields are how the driver's return value is
    /// joined back up with the re-run and live-refresh tail that stays in the registry.
    /// </summary>
    private sealed class RegistryEditPass : IPrefabEditPass
    {
        private readonly int _guidHash;
        private readonly World? _world;
        private readonly PrefabEditTrigger _trigger;
        private PrefabEditRegistration[] _ordered = Array.Empty<PrefabEditRegistration>();
        private PrefabEditHandle? _handle;

        internal RegistryEditPass(int guidHash, World? world, PrefabEditTrigger trigger)
        {
            _guidHash = guidHash;
            _world = world;
            _trigger = trigger;
        }

        /// <summary>True when at least one editor wrote a component in this pass.</summary>
        internal bool AnyTouches { get; private set; }

        /// <summary>What <c>EndRun</c> answered: a nested invalidation asked for another pass.</summary>
        internal bool Again { get; private set; }

        public bool WorldAlive => _world != null && _world.IsCreated;

        public bool HasEditors => Edits.HasGuid(_guidHash);

        public int BaselineCount => Edits.Baselines.Count(_guidHash);

        public bool RestoreOnly() => RestoreEditsOnly(_world!, _guidHash);

        public bool TryBeginRun() => Edits.TryBeginRun(_guidHash);

        public void EndRun() => Again = Edits.EndRun(_guidHash);

        public bool TrySnapshot(out int orderedCount)
        {
            if (!Edits.TrySnapshotOrdered(_guidHash, out _ordered, out var cycleLog))
            {
                _ordered = Array.Empty<PrefabEditRegistration>();
                orderedCount = 0;
                if (!string.IsNullOrEmpty(cycleLog))
                    Plugin.Logger?.LogWarning(cycleLog);
                return false;
            }
            orderedCount = _ordered.Length;
            return true;
        }

        public bool ShouldRun() => Edits.ShouldRun(_guidHash, _trigger, _ordered);

        public bool TryBind()
        {
            _handle = PrefabEditHandle.TryBind(_world!, new PrefabGUID(_guidHash), Edits.Baselines, Edits.Wire);
            return _handle != null;
        }

        /// <summary>
        /// InsertNew is a different entity and UpdateExisting just reset the definition-owned
        /// fields: the old snapshot is not vanilla any more and restoring it here would clobber
        /// what the applicator wrote.
        /// </summary>
        public void DropBaselines()
        {
            Edits.Baselines.Drop(_guidHash);
            // The readers close over the entity that was just replaced or reset, and the sent
            // hash describes bytes that entity no longer has. The editors below re-register.
            Edits.Wire.DropGuid(_guidHash);
        }

        /// <summary>
        /// Every pass except <c>NewClone</c> starts from vanilla, so an editor doing
        /// read-modify-write cannot accumulate across re-runs.
        /// </summary>
        public void RestoreBeforeRerun()
        {
            var restoredBefore = RestoreBaselines(_handle!, _guidHash);
            if (restoredBefore.Length > 0 && _trigger == PrefabEditTrigger.EditorsChanged)
                Plugin.Logger?.LogInfo(
                    PrefabEditLog.RestoredBeforeRerun(_guidHash, restoredBefore.Length, _ordered.Length));
        }

        public void RunEditors()
        {
            var handle = _handle!;
            int guidHash = _guidHash;
            var records = PrefabEditApply.Run(
                _ordered,
                reg =>
                {
                    handle.BeginEditor(reg.Spec.PluginId);
                    var editor = reg.Editor as Action<PrefabEditHandle>;
                    if (editor == null)
                    {
                        Plugin.Logger?.LogError(
                            $"PrefabRegistry: edit from {reg.Spec.PluginId} on guid {guidHash} is not Action<PrefabEditHandle>; skipped.");
                        return Array.Empty<string>();
                    }
                    editor(handle);
                    return handle.TakeTouches();
                },
                warn => Plugin.Logger?.LogWarning(warn),
                (pluginId, e) => Plugin.Logger?.LogError(
                    PrefabEditLog.EditorThrew(pluginId, guidHash, e)),
                Edits.Conflicts,
                Edits.Throws,
                out bool anyThrew);
            Edits.RememberRun(_guidHash, _ordered, records, anyThrew);
            for (int r = 0; r < records.Length; r++)
            {
                if (records[r].Components != null && records[r].Components.Length > 0)
                    AnyTouches = true;
            }
        }
    }

    /// <summary>
    /// Second phase of an apply: the ordered editors wrote components on the clone. On a Core
    /// swap or plugin reload this is the phase where this generation's editors finally exist,
    /// so it only notes; <see cref="SettleLiveRefresh"/> decides once, against the fingerprint
    /// computed with those editors in place.
    /// </summary>
    private static void ConsiderEditRefresh(World world, int guidHash, PrefabEditTrigger trigger)
    {
        try
        {
            PrefabDefinition? def = null;
            lock (Gate)
            {
                for (int i = 0; i < Entries.Count; i++)
                {
                    if (Entries[i].Definition.Guid.GuidHash != guidHash) continue;
                    def = Entries[i].Definition;
                    break;
                }
            }
            if (def == null) return;
            NoteLiveRefresh(
                world,
                def,
                alreadyCloned: PrefabApplicator.WasClonedHere(def.Guid),
                retargeted: false,
                editPassWrote: true,
                // Captured editor values moved (config reload / Invalidate). No build-stable
                // fingerprint can see that, so it is carried as its own refresh trigger.
                inputsChanged: trigger == PrefabEditTrigger.InputsChanged);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"PrefabRegistry: edit live-refresh of guid {guidHash} threw: {e}");
        }
    }

    /// <summary>
    /// Record one apply phase for the live-refresh decision. The client never destroys
    /// instances — its HUD nudge is unconditional — so it runs immediately; the server's
    /// destroy-and-reapply decision waits for <see cref="SettleLiveRefresh"/>.
    /// </summary>
    private static void NoteLiveRefresh(
        World world,
        PrefabDefinition def,
        bool alreadyCloned,
        bool retargeted,
        bool editPassWrote,
        bool inputsChanged)
    {
        if (def == null || def.Kind != PrefabKind.Buff) return;
        // This generation owns this GUID, so a fingerprint rehydrated for it is no longer
        // provisional and survives the end-of-first-apply-pass sweep (R29 finding 11).
        PrefabLiveRefresh.NoteRegistered(def.Guid.GuidHash);
        if (!Plugin.IsServerProcess)
        {
            PrefabLiveRefreshApply.AfterCloneChanged(
                world, def, alreadyCloned,
                definitionChanged: false, retargeted: retargeted,
                editPassChangedClone: false, previousFingerprintMissing: true);
            return;
        }
        RefreshDecisions.Note(
            def.Guid.GuidHash,
            alreadyCloned,
            retargeted,
            editPassWrote,
            inputsChanged,
            PrefabLiveRefresh.LastFingerprint(def.Guid.GuidHash));
        ScheduleRefreshSettle();
    }

    /// <summary>
    /// Arm (or re-arm) the settle for <see cref="RefreshSettleFrames"/> frames. Every note
    /// extends the window: the settle must happen after the LAST phase of the reload, because
    /// it is the settle that records the fingerprint, and a fingerprint recorded before a late
    /// <c>Content.Edit</c> registration makes that registration read as a definition change.
    /// A drip of applies extending the window is the safe direction — nothing is lost, the
    /// decision is merely taken later — while settling early is a spurious destroy+reapply.
    /// </summary>
    private static void ScheduleRefreshSettle()
    {
        if (RefreshSettle.Arm())
            Plugin.Frame += OnRefreshSettleFrame;
    }

    private static void StopRefreshSettle()
    {
        if (RefreshSettle.Stop())
            Plugin.Frame -= OnRefreshSettleFrame;
    }

    private static void OnRefreshSettleFrame()
    {
        try
        {
            if (!RefreshSettle.Tick()) return;
            StopRefreshSettle();
            SettleLiveRefresh();
        }
        catch (Exception e)
        {
            StopRefreshSettle();
            Plugin.Logger?.LogError($"PrefabRegistry: live-refresh settle threw: {e}");
        }
    }

    /// <summary>
    /// One decision per noted prefab, taken once this generation's editors are registered:
    /// compute the fingerprint, compare it with the last recorded one, record it, and let
    /// <c>AfterCloneChanged</c> log exactly one <c>refreshed</c> or
    /// <c>refresh skipped: definition unchanged</c> line.
    /// </summary>
    private static void SettleLiveRefresh()
    {
        var hashes = RefreshDecisions.PendingGuids();
        if (hashes.Length == 0)
        {
            SettleApplyPassAndDropUnclaimed();
            return;
        }
        var world = TargetWorld();
        for (int i = 0; i < hashes.Length; i++)
        {
            int guidHash = hashes[i];
            try
            {
                PrefabDefinition? def = null;
                lock (Gate)
                {
                    for (int e = 0; e < Entries.Count; e++)
                    {
                        if (Entries[e].Definition.Guid.GuidHash != guidHash) continue;
                        def = Entries[e].Definition;
                        break;
                    }
                }
                if (def == null)
                {
                    // Unregistered between the note and the settle: drop it, keep no stale row.
                    RefreshDecisions.TrySettle(guidHash, null, out _);
                    continue;
                }
                string currentFp = DefinitionFingerprint(def);
                if (!RefreshDecisions.TrySettle(guidHash, currentFp, out var decision)) continue;
                PrefabLiveRefresh.RememberFingerprint(guidHash, currentFp);
                if (world == null || !world.IsCreated) continue;
                PrefabLiveRefreshApply.AfterCloneChanged(
                    world, def,
                    decision.AlreadyClonedThisProcess,
                    decision.DefinitionChanged,
                    decision.Retargeted,
                    decision.EditPassChangedClone,
                    decision.PreviousFingerprintMissing);
            }
            catch (Exception e)
            {
                RefreshDecisions.TrySettle(guidHash, null, out _);
                Plugin.Logger?.LogError($"PrefabRegistry: live-refresh settle of guid {guidHash} threw: {e}");
            }
        }

        SettleApplyPassAndDropUnclaimed();
    }

    /// <summary>
    /// End of this generation's first apply pass. The settle is the right seam: it is armed by
    /// every apply and re-armed by every later one, so it runs once the reload burst is quiet and
    /// after every definition this generation is going to register has registered. Whatever
    /// rehydrated fingerprint is still unclaimed at that point belongs to a definition that is
    /// not in this build, and keeping it lets a later generation compare against a build nobody
    /// is running (R29 finding 11). Idempotent — the sweep only acts once per generation.
    ///
    /// <b>This is the only place in Progenitor that may call
    /// <c>PrefabLiveRefresh.SettleApplyPass</c>,</b> and it is reached only from
    /// <see cref="SettleLiveRefresh"/>. Claiming the pass settled is what makes an empty export
    /// authoritative, and an authoritative empty export clears Bootstrap <c>ContentMemory</c>
    /// (R43 job FPRF1). Teardown uses <see cref="SweepUnclaimedFingerprints"/> instead.
    /// </summary>
    private static void SettleApplyPassAndDropUnclaimed()
    {
        try
        {
            if (!PrefabLiveRefresh.SettleApplyPass(out int dropped)) return;
            ReportUnclaimedFingerprintsDropped(dropped);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("PrefabRegistry: fingerprint sweep threw: " + e.Message);
        }
    }

    /// <summary>
    /// The same fossil sweep without the authority, for the teardown export
    /// (<see cref="ExportProcessMemory"/>). A generation can reach teardown without ever having
    /// applied anything, and in that case its empty fingerprint map proves nothing — so the sweep
    /// leaves the settle state exactly as it found it and <c>ExportFingerprints</c> reports
    /// <c>Unsettled</c>, which keeps <c>ContentMemory</c>'s rows (R43 job FPRF1).
    /// </summary>
    private static void SweepUnclaimedFingerprints()
    {
        try
        {
            if (!PrefabLiveRefresh.SweepUnclaimedImports(out int dropped)) return;
            ReportUnclaimedFingerprintsDropped(dropped);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("PrefabRegistry: fingerprint sweep threw: " + e.Message);
        }
    }

    private static void ReportUnclaimedFingerprintsDropped(int dropped)
    {
        if (dropped > 0)
            Plugin.Logger?.LogWarning(PrefabLiveRefresh.FormatFingerprintsDropped(dropped));
    }

    /// <summary>
    /// Write the captured vanilla values of <paramref name="guidHash"/> back onto the bound
    /// entity and return the component names restored. Baselines taken on a different entity
    /// (a new clone, a new client world) are stale and are dropped without being written.
    /// A throwing callback is logged and skipped; the rest still run.
    /// </summary>
    private static string[] RestoreBaselines(PrefabEditHandle handle, int guidHash)
    {
        return Edits.Baselines.Restore(
            guidHash,
            handle.Entity.Index,
            handle.Entity.Version,
            handle.World,
            (typeName, e) => Plugin.Logger?.LogError(PrefabEditLog.RestoreThrew(typeName, guidHash, e)));
    }

    /// <summary>
    /// No editors left for this prefab: restore and forget. Nothing re-runs, and no live
    /// refresh is triggered — the entity simply stops carrying the removed editor's values.
    /// False when the prefab entity could not be bound: the baselines are left in the store and
    /// the caller re-queues the removal, because this is the one path that ever restores them
    /// (R49 finding 6).
    /// </summary>
    private static bool RestoreEditsOnly(World world, int guidHash)
    {
        int owed = Edits.Baselines.Count(guidHash);
        if (owed == 0) return true;
        var handle = PrefabEditHandle.TryBind(world, new PrefabGUID(guidHash), Edits.Baselines);
        if (handle == null) return false;
        var restored = RestoreBaselines(handle, guidHash);
        if (restored.Length < owed)
            Plugin.Logger?.LogWarning(PrefabEditLog.RestoreIncomplete(guidHash, owed, restored.Length));
        Edits.Baselines.Drop(guidHash);
        // Before the reader state goes: tell every client the layer is retired, or they keep the
        // removed editor's values until they change worlds (job FEDW3, R51 finding 7).
        PrefabEditWireApply.SendWithdraw(world, Edits, guidHash);
        // No editors left, so there is nothing to serialize and nothing to dedupe against.
        Edits.Wire.DropGuid(guidHash);
        if (restored.Length > 0)
            Plugin.Logger?.LogInfo(PrefabEditLog.RestoredAfterLastEditor(guidHash, restored));
        return true;
    }

    /// <summary>
    /// Core uninstall: write every captured baseline back before the table goes away.
    /// Baselines live in the collectible Core generation and are not handed through Bootstrap
    /// <c>ContentMemory</c>, so restoring here is what lets the next generation's first write
    /// capture true vanilla; the cost is that a clone carries vanilla values for the few
    /// frames of a swap. A plugin unloaded during the swap has usually restored already —
    /// this is the belt for editors that never went through an owner sweep.
    /// </summary>
    private static void RestoreAllBaselines()
    {
        try
        {
            var guids = Edits.Baselines.Guids();
            if (guids.Length == 0) return;
            var world = TargetWorld();
            if (world == null || !world.IsCreated)
            {
                Edits.Baselines.Clear();
                return;
            }

            int components = 0;
            int prefabs = 0;
            for (int i = 0; i < guids.Length; i++)
            {
                var handle = PrefabEditHandle.TryBind(world, new PrefabGUID(guids[i]), Edits.Baselines);
                if (handle == null) continue;
                var restored = RestoreBaselines(handle, guids[i]);
                if (restored.Length == 0) continue;
                components += restored.Length;
                prefabs++;
            }
            Edits.Baselines.Clear();
            if (components > 0)
                Plugin.Logger?.LogInfo(PrefabEditLog.RestoredAtUninstall(components, prefabs));
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"PrefabRegistry: restoring edit baselines at uninstall threw: {e}");
        }
    }

    /// <summary>
    /// Server-side round trip of one prefab's <c>PrfE</c> layer, with no client and no network
    /// hop (job FEDW1 verification step 1). Serializes the touched components with the reader
    /// closures, frames and re-parses the packet, writes the captured baselines back so the
    /// entity is vanilla again, then applies the parsed bytes through the <em>client</em> apply
    /// path against the same entity and reports what came back.
    ///
    /// <para>Side effects are exactly that restore/apply pair: the entity ends where it started
    /// when the round trip works, and the report says so component by component. Nothing is sent,
    /// no fingerprint is recorded, no editor runs. Intended for the Dev <c>exec</c> op.</para>
    /// </summary>
    internal static string DebugRoundTripEditLayer(int guidHash)
    {
        var sb = new StringBuilder();
        sb.Append("PrfE round trip guid=").Append(guidHash).Append(": ");
        try
        {
            var world = TargetWorld();
            if (world == null || !world.IsCreated) return sb.Append("no world").ToString();

            var guid = new PrefabGUID(guidHash);
            if (!PrefabLookup.TryGetEntity(world, guid, out var entity))
                return sb.Append("prefab entity not in the collection").ToString();

            var readerTypes = Edits.Wire.ReaderTypes(guidHash);
            if (readerTypes.Length == 0)
                return sb.Append("no reader registered (no edit pass wrote this prefab)").ToString();

            if (!PrefabEditWireApply.TryBuildLayer(world, Edits, guidHash, out var packet, out var components, out _))
                return sb.Append("layer did not serialize; see the warnings above").ToString();

            if (!PrefabEditWire.TryRead(
                    new ReadOnlySpan<byte>(packet),
                    out int packetGuid,
                    out int buildHash,
                    out int identityHash,
                    out var records,
                    out string parseError))
                return sb.Append("packet did not parse: ").Append(parseError).ToString();

            sb.Append(packet.Length).Append(" byte(s), guid=").Append(packetGuid)
                .Append(", build=0x").Append(buildHash.ToString("x8"))
                .Append(", identity=0x").Append(identityHash.ToString("x8"))
                .Append(", ").Append(records.Count).Append(" record(s); ");

            var handle = PrefabEditHandle.TryBind(world, guid, Edits.Baselines, Edits.Wire);
            var restored = handle == null ? Array.Empty<string>() : RestoreBaselines(handle, guidHash);
            sb.Append("restored ").Append(restored.Length).Append(" baseline(s); ");

            var applied = PrefabEditWireApply.ApplyRecords(world, entity, guid, records, Edits.Baselines);

            var after = Edits.Wire.Serialize(guidHash, world);
            for (int i = 0; i < records.Count; i++)
            {
                var rec = records[i];
                bool ok = Array.IndexOf(applied, rec.ShortName) >= 0;
                sb.Append("[type=").Append(rec.TypeFullName)
                    .Append(", bytes=").Append(rec.Bytes.Length)
                    .Append(", applied=").Append(ok ? "ok" : "refused")
                    .Append(", readBack=").Append(DescribeReadBack(world, entity, after, rec))
                    .Append("] ");
            }
            _ = components;
            return sb.ToString().TrimEnd();
        }
        catch (Exception e)
        {
            return sb.Append("threw: ").Append(e).ToString();
        }
    }

    /// <summary>
    /// Compare a re-read of the component with the bytes that were applied. <c>match</c> means the
    /// value survived serialize, restore and apply unchanged, which is the whole point of the
    /// round trip. <c>ItemData</c> also reports <c>MaxAmount</c>, the field the Sample edits.
    /// </summary>
    private static string DescribeReadBack(
        World world,
        Entity entity,
        IReadOnlyList<PrefabEditComponentBytes> after,
        in PrefabEditComponentBytes sent)
    {
        for (int i = 0; i < after.Count; i++)
        {
            if (!string.Equals(after[i].TypeFullName, sent.TypeFullName, StringComparison.Ordinal)) continue;
            var now = after[i].Bytes;
            bool same = now.Length == sent.Bytes.Length;
            for (int b = 0; same && b < now.Length; b++)
                same = now[b] == sent.Bytes[b];
            string verdict = same ? "match" : "differs";
            if (!string.Equals(sent.TypeFullName, PrefabEditWrite.ItemDataFullName, StringComparison.Ordinal))
                return verdict;
            return verdict + ", MaxAmount=" + DescribeMaxAmount(world, entity);
        }
        return "component gone";
    }

    private static string DescribeMaxAmount(World world, Entity entity)
    {
        try
        {
            return world.EntityManager.GetComponentData<ItemData>(entity).MaxAmount
                .ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (Exception e)
        {
            return "unreadable (" + e.Message + ")";
        }
    }

    private static void TryRunAllEdits(World? world, PrefabEditTrigger trigger)
    {
        try
        {
            world ??= TargetWorld();
            if (world == null || !world.IsCreated) return;
            var hashes = Edits.UniqueGuidHashes();
            for (int i = 0; i < hashes.Length; i++)
                TryRunEdits(hashes[i], world, trigger);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"PrefabRegistry: TryRunAllEdits threw: {e}");
        }
    }

    private static void ApplyOne(World world, PrefabDefinition def, bool sendToConnected)
    {
        try
        {
            if (ShouldSkipItemForJoinAgreement(def, out var skipReason))
            {
                LogJoinRefuseOnce(def.Guid.GuidHash, skipReason);
                return;
            }
            if (!ApplyGate.Ready)
            {
                NotePending(def);
                return;
            }
            bool alreadyCloned = PrefabApplicator.WasClonedHere(def.Guid);
            bool ok = PrefabApplicator.TryApply(world, def, out var report);
            bool pending = !ok && PrefabApplyBookkeeping.IsApplyStillPending(report);
            if (PrefabApplyBookkeeping.KeepEntryAfterApply(ok, pending))
            {
                if (ok)
                {
                    ClearPending(def.Guid.GuidHash);
                    MarkApplied(def);
                    Plugin.Logger?.LogDiag($"PrefabRegistry: applied '{def.Id}' ({def.Guid.GuidHash}) in {world.Name}: {report}");
                    var afterApply = AfterApply;
                    if (afterApply != null)
                    {
                        // One subscriber's throw must not skip the others.
                        foreach (var hook in afterApply.GetInvocationList())
                        {
                            try { ((Action<World, PrefabDefinition>)hook)(world, def); }
                            catch (Exception e) { Plugin.Logger?.LogWarning($"PrefabRegistry: after-apply hook for '{def.Id}' threw: {e.Message}"); }
                        }
                    }
                    if (def.Wearable != null)
                    {
                        try { WearableModelWrite.TryWrite(world, def); }
                        catch (Exception e) { Plugin.Logger?.LogWarning($"Wearables: model write for '{def.Id}' threw: {e.Message}"); }
                    }
                    // Editors run on the clone before PrfD so a client that applies this
                    // packet and then its own Edit() pass sees the same post-clone entity.
                    // NewClone: InsertNew / UpdateExisting just wrote definition values.
                    bool editChanged = false;
                    _applyOneDepth++;
                    try { editChanged = TryRunEdits(def.Guid.GuidHash, world, PrefabEditTrigger.NewClone); }
                    finally { _applyOneDepth--; }
                    bool retargeted = !string.IsNullOrEmpty(report)
                        && report.IndexOf(PrefabApplyBookkeeping.RetargetedToken, StringComparison.OrdinalIgnoreCase) >= 0;
                    // First phase of the apply. On a Core swap or plugin reload the plugin's
                    // Content.Edit passes are not registered yet, so neither the fingerprint
                    // nor the decision is taken here: both would ignore this generation's
                    // editors and the second phase would then disagree with them
                    // (R26 finding 4). Note and let the settle decide once.
                    NoteLiveRefresh(
                        world, def, alreadyCloned, retargeted,
                        editPassWrote: editChanged, inputsChanged: false);
                    bool sendPrfD = Plugin.IsServerProcess && (sendToConnected || retargeted);
                    if (sendPrfD)
                    {
                        SendDefinition(world, def);
                        if (retargeted)
                            Plugin.Logger?.LogInfo(PrefabApplyBookkeeping.FormatPrfDAfterRetarget(def.Id));
                    }
                    // After PrfD, never before: reliable events keep order, so a client that has
                    // just been told about this clone applies the edited values to an entity it
                    // already has. The pass itself skipped the send (_applyOneDepth).
                    if (editChanged && Plugin.IsServerProcess)
                        PrefabEditWireApply.SendToConnected(world, Edits, def.Guid.GuidHash);
                }
                else
                {
                    // Source collection is not populated yet (WorldReady fires before prefabs finish).
                    // Retry on Plugin.Frame until success or PrefabWire.ApplyGiveUpFrames, not only
                    // on the next network packet — a single definition (or the last in a list) has
                    // no follow-up packet.
                    bool firstPending;
                    lock (Gate) firstPending = !PendingFrames.ContainsKey(def.Guid.GuidHash);
                    if (firstPending)
                        Plugin.Logger?.LogInfo($"PrefabRegistry: apply of '{def.Id}' ({def.Guid.GuidHash}) pending in {world.Name}: {report}");
                    NotePending(def);
                }
            }
            else
            {
                ClearPending(def.Guid.GuidHash);
                DropFailedEntry(def, report);
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"PrefabRegistry: apply '{def.Id}' threw: {e}");
            ClearPending(def.Guid.GuidHash);
            DropFailedEntry(def, e.Message);
        }
    }

    private static void SendDefinition(World server, PrefabDefinition def)
    {
        try
        {
            if (!PrefabDefinitionPacket.TryFrom(def, out var packet, out var error))
            {
                Plugin.Logger?.LogWarning($"PrefabRegistry: skip send of '{def.Id}': {error}");
                return;
            }
            int n = CustomEvents.SendToAll(server, PrefabDefinitionPacket.EventId, packet);
            if (def.Kind == PrefabKind.Item)
                SendItemStatsToSupportedClients(server, def);
            if (n > 0)
                Plugin.Logger?.LogInfo($"PrefabRegistry: sent '{def.Id}' to {n} connected user(s).");
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"PrefabRegistry: send '{def.Id}' threw: {e}");
        }
    }

    private static void SendUnregister(PrefabDefinition def)
    {
        if (!Plugin.IsServerProcess) return;
        var server = Worlds.Server;
        if (server == null || !server.IsCreated) return;
        try
        {
            var packet = PrefabUnregisterPacket.From(def);
            int n = CustomEvents.SendToAll(server, PrefabUnregisterPacket.EventId, packet);
            if (n > 0)
                Plugin.Logger?.LogInfo($"PrefabRegistry: sent unregister '{def.Id}' to {n} connected user(s).");
            // After unregister, not before: reliable events keep order. Only send when this
            // GUID still occupies a server key-table row — otherwise a client would grow a
            // table the server does not have.
            SendKeyPlaceholderToAll(server, def.Guid.GuidHash, def.Id);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"PrefabRegistry: send unregister '{def.Id}' threw: {e}");
        }
    }

    private static void SendKeyPlaceholderToAll(World server, int guidHash, string id)
    {
        var guid = new PrefabGUID(guidHash);
        if (!PrefabApplyBookkeeping.ShouldTombstone(PrefabApplicator.WasAppendedHere(guid))) return;
        if (!KeyTableHas(server, guid)) return;
        try
        {
            var packet = PrefabUnregisterPacket.Placeholder(guidHash, id);
            int n = CustomEvents.SendToAll(server, PrefabUnregisterPacket.KeyPlaceholderEventId, packet);
            if (n > 0)
                Plugin.Logger?.LogInfo($"PrefabRegistry: sent key placeholder '{id}' ({guidHash}) to {n} connected user(s).");
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"PrefabRegistry: send key placeholder '{id}' threw: {e}");
        }
    }

    private static void ScheduleEditFlush()
    {
        if (_editFlushHooked) return;
        Plugin.Frame += OnEditFlushFrame;
        _editFlushHooked = true;
    }

    private static void StopEditFlush()
    {
        if (!_editFlushHooked) return;
        Plugin.Frame -= OnEditFlushFrame;
        _editFlushHooked = false;
    }

    private static void OnEditFlushFrame()
    {
        try
        {
            StopEditFlush();
            SweepHandshakeIfChanged();
            // Removals first: an EditorsChanged pass restores the baselines and re-runs the
            // remaining editors (or restores only, when the last editor is gone). A GUID that
            // also has a pending InputsChanged run has just been re-run by that pass.
            var removals = Edits.DrainPendingRemovals();
            var hashes = PrefabEditPass.PendingRunsToRun(removals, Edits.DrainPendingRuns());
            if (removals.Length == 0 && hashes.Length == 0) return;
            var world = TargetWorld();
            for (int i = 0; i < removals.Length; i++)
            {
                TryRunEdits(removals[i], world, PrefabEditTrigger.EditorsChanged, out var outcome);
                NoteRemovalOutcome(removals[i], outcome);
            }
            for (int i = 0; i < hashes.Length; i++)
                TryRunEdits(hashes[i], world, PrefabEditTrigger.InputsChanged);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"PrefabRegistry: edit flush threw: {e}");
        }
    }

    /// <summary>
    /// A removal was drained off the pending set; if the pass it was handed to did not restore
    /// anything, put it back and arm the flush again (R49 finding 6). The drain is destructive
    /// and, for a prefab whose last editor is gone, no other trigger ever visits the GUID: a
    /// dead world, a run already in flight or a bind that failed would otherwise leave the
    /// removed editor's values on the prefab for the rest of the generation, silently.
    /// Bounded like <c>PrefabLiveRefresh</c>'s owed re-applies, with one line when the budget
    /// is spent — a permanently dead world must not re-arm the frame hook forever.
    ///
    /// <para>Only a prefab that actually owes something is retried (R50 finding 6): the owed
    /// amount is <c>Baselines.Count(guid)</c>, and the outcomes another path already owns
    /// (a bind failure with editors left, a priority cycle) get one Info line instead of thirty
    /// re-queues. Both terminal lines are printed once per guid per Core generation.</para>
    /// </summary>
    private static void NoteRemovalOutcome(int guidHash, PrefabEditPassOutcome outcome)
    {
        _removalRetries.TryGetValue(guidHash, out int soFar);
        int owed = Edits.Baselines.Count(guidHash);
        var action = PrefabEditPass.NextRemovalAction(outcome, soFar, owed, out int retries);
        if (action != PrefabEditPass.RemovalRetryAction.Requeue)
        {
            _removalRetries.Remove(guidHash);
            if (action == PrefabEditPass.RemovalRetryAction.GiveUp)
            {
                if (_removalNoticed.Add(guidHash))
                    Plugin.Logger?.LogWarning(
                        PrefabEditLog.RemovalGivenUp(guidHash, outcome.ToString(), soFar, owed));
            }
            else if (action == PrefabEditPass.RemovalRetryAction.OwnedElsewhere)
            {
                if (_removalNoticed.Add(guidHash))
                    Plugin.Logger?.LogInfo(PrefabEditLog.RemovalNotRetried(
                        guidHash, outcome.ToString(), PrefabEditPass.RemovalNotRetriedReason(outcome)));
            }
            return;
        }
        _removalRetries[guidHash] = retries;
        Edits.RequeueRemoval(guidHash);
        // Same gate as OnEditRemovalScheduled: after Uninstall the frame hook is gone on
        // purpose, and RestoreAllBaselines is what puts this prefab back.
        if (_installed) ScheduleEditFlush();
    }

    // ---- the PrfE wire frame (job FEDW3) -----------------------------------------------------

    /// <summary>
    /// <b>The resend seam</b> (job FEDW4, R52 finding 3): push every stored <c>PrfE</c> layer to
    /// one connected user again. Server-side only, idempotent, and cheap when there is nothing to
    /// do — the send is deduped <em>per user</em>, so a user who already has every layer costs one
    /// hash comparison per prefab and sends nothing.
    ///
    /// <para><b>Who calls it.</b> The <c>Networking</c> side, when an <em>already-ok</em> user
    /// re-answers the handshake. That is the client-side Core swap: the client rebuilds
    /// <c>PrefabEditTable</c> empty and re-declares itself, but the server's answer status for
    /// that user never leaves <c>ok</c> (<c>HandshakeAnswerWatch.Arm</c> returns early at
    /// <c>Ok</c>, <c>OnAnswer</c> writes <c>Ok</c> over <c>Ok</c>, and the Install re-arm is
    /// server-only), so the edge <c>PrefabEditWireApply.PollHandshakeResends</c> watches cannot
    /// fire and only a call from the answer path can start the recovery. The poll stays for the
    /// case it does see — a server Core generation, where every user re-answers — and the two
    /// cannot duplicate each other because they share the per-user record.</para>
    ///
    /// <para>The received layer does <b>not</b> survive a client Core swap: the table is a Core
    /// static and the new generation gets an empty one. This resend is the recovery.</para>
    /// </summary>
    public static void ResendEditLayersTo(int userIndex)
    {
        if (!Plugin.IsServerProcess) return;
        try
        {
            PrefabEditWireApply.ResendToUserIndex(TargetWorld(), Edits, userIndex);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"PrefabRegistry: PrfE resend to user {userIndex} threw: {e}");
        }
    }

    /// <summary>
    /// One low-rate frame hook for the three things the <c>PrfE</c> wire has to do off its own
    /// events: flush a send the rate floor coalesced, notice a user whose handshake has just
    /// answered ok (a <em>server</em> Core generation; a client-side swap is
    /// <see cref="ResendEditLayersTo"/>, R52 finding 3), and
    /// retry a received layer whose prefab entity was not in the collection yet (R51 finding 7).
    /// Every <see cref="WireFramePeriod"/> frames, and every step is a cheap no-op when the wire
    /// has nothing in flight.
    /// </summary>
    private static void EnsureWireFrame()
    {
        if (_wireFrameHooked) return;
        Plugin.Frame += OnWireFrame;
        _wireFrameHooked = true;
    }

    private static void StopWireFrame()
    {
        if (!_wireFrameHooked) return;
        Plugin.Frame -= OnWireFrame;
        _wireFrameHooked = false;
    }

    private static void OnWireFrame()
    {
        try
        {
            if (++_wireFrameTick < WireFramePeriod) return;
            _wireFrameTick = 0;
            var world = TargetWorld();
            if (Plugin.IsServerProcess)
            {
                PrefabEditWireApply.FlushDeferredSends(world, Edits);
                PrefabEditWireApply.PollHandshakeResends(world, Edits);
                return;
            }
            RetryPendingEditLayers(world);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"PrefabRegistry: PrfE wire frame threw: {e}");
        }
    }

    /// <summary>
    /// Queue or clear one prefab's pending-layer retry. Called wherever a layer apply is attempted
    /// and reports whether it is still owed.
    /// </summary>
    private static void NoteEditLayerPending(int guidHash, bool pending)
    {
        lock (Gate)
        {
            if (!pending)
            {
                EditLayerPending.Remove(guidHash);
                return;
            }
            if (EditLayerGaveUp.Contains(guidHash)) return;
            if (!EditLayerPending.ContainsKey(guidHash)) EditLayerPending[guidHash] = 0;
        }
    }

    private static void RetryPendingEditLayers(World? world)
    {
        int[] hashes;
        lock (Gate)
        {
            if (EditLayerPending.Count == 0) return;
            hashes = new int[EditLayerPending.Count];
            EditLayerPending.Keys.CopyTo(hashes, 0);
        }
        for (int i = 0; i < hashes.Length; i++)
        {
            int hash = hashes[i];
            PrefabEditWireApply.ApplyReceived(world, Edits, hash, out bool pending);
            if (!pending)
            {
                lock (Gate) EditLayerPending.Remove(hash);
                continue;
            }
            int attempts;
            lock (Gate)
            {
                if (!EditLayerPending.TryGetValue(hash, out attempts)) continue;
                attempts++;
                EditLayerPending[hash] = attempts;
                if (attempts < EditLayerRetryAttempts) continue;
                EditLayerPending.Remove(hash);
                if (!EditLayerGaveUp.Add(hash)) continue;
            }
            Plugin.Logger?.LogWarning(PrefabEditLog.EditLayerRetryGaveUp(
                hash, attempts, "the prefab entity never appeared in this world's collection"));
        }
    }

    /// <summary>
    /// The server withdrew its layer for one prefab (a <c>count == 0</c> <c>PrfE</c>). Put the
    /// captured values back: when local editors remain, that is an <c>EditorsChanged</c> pass,
    /// which restores and re-runs them; when none do, it is a plain restore. Either way the
    /// prefab stops carrying values no writer still stands behind (R51 finding 7).
    /// </summary>
    private static void OnEditLayerWithdrawn(World? world, int guidHash)
    {
        lock (Gate)
        {
            EditLayerPending.Remove(guidHash);
            EditLayerGaveUp.Remove(guidHash);
        }
        world ??= TargetWorld();
        if (world == null || !world.IsCreated)
        {
            Plugin.Logger?.LogInfo(PrefabEditLog.EditLayerWithdrawn(guidHash, 0));
            return;
        }
        if (Edits.HasGuid(guidHash))
        {
            TryRunEdits(guidHash, world, PrefabEditTrigger.EditorsChanged);
            Plugin.Logger?.LogInfo(PrefabEditLog.EditLayerWithdrawn(guidHash, Edits.Baselines.Count(guidHash)));
            return;
        }
        var handle = PrefabEditHandle.TryBind(world, new PrefabGUID(guidHash), Edits.Baselines);
        if (handle == null)
        {
            Plugin.Logger?.LogInfo(PrefabEditLog.EditLayerWithdrawn(guidHash, 0));
            return;
        }
        var restored = RestoreBaselines(handle, guidHash);
        Edits.Baselines.Drop(guidHash);
        Plugin.Logger?.LogInfo(PrefabEditLog.EditLayerWithdrawn(guidHash, restored.Length));
    }

    private static void EnsureHandshakeWatch()
    {
        if (_handshakeWatchHooked) return;
        Plugin.Frame += OnHandshakeWatch;
        _handshakeWatchHooked = true;
        _handshakeFp = PrefabEditCompatibility.LiveFingerprint();
    }

    private static void StopHandshakeWatch()
    {
        if (!_handshakeWatchHooked) return;
        Plugin.Frame -= OnHandshakeWatch;
        _handshakeWatchHooked = false;
        _handshakeFp = "";
    }

    private static void OnHandshakeWatch()
    {
        try
        {
            if (Edits.Count == 0) return;
            SweepHandshakeIfChanged();
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"PrefabRegistry: handshake watch threw: {e}");
        }
    }

    private static void SweepHandshakeIfChanged()
    {
        string fp = PrefabEditCompatibility.LiveFingerprint();
        if (string.Equals(fp, _handshakeFp, StringComparison.Ordinal)) return;
        _handshakeFp = fp;
        PrefabEditCompatibility.DropDisallowed(Edits, line => Plugin.Logger?.LogWarning(line));
    }

    private static World? TargetWorld() => Plugin.IsServerProcess ? Worlds.Server : Worlds.Client;

    private static void RaiseRegistered(PrefabDefinition def)
    {
        var handlers = Registered;
        if (handlers == null) return;
        try { handlers(def); }
        catch (Exception e) { Plugin.Logger?.LogError($"PrefabRegistry.Registered handler threw: {e}"); }
    }

    private static void ValidateId(string id) =>
        PrefabWire.ValidateAscii(id, nameof(id), "id");

    private static bool HasLocalNonSynced(int guidHash, string id)
    {
        lock (Gate)
        {
            for (int i = 0; i < Entries.Count; i++)
            {
                var entry = Entries[i];
                bool guidMatch = entry.Definition.Guid.GuidHash == guidHash;
                bool idMatch = id.Length > 0 && string.Equals(entry.Definition.Id, id, StringComparison.Ordinal);
                if (!guidMatch && !idMatch) continue;
                if (!ReferenceEquals(entry.Definition.Owner, SyncedOwner))
                    return true;
            }
        }
        return false;
    }

    private static void NotePending(PrefabDefinition def)
    {
        lock (Gate)
        {
            if (!PendingFrames.ContainsKey(def.Guid.GuidHash))
                PendingFrames[def.Guid.GuidHash] = 0;
            EnsureFrameRetryNoLock();
        }
    }

    private static void ClearPending(int guidHash)
    {
        lock (Gate)
        {
            PendingFrames.Remove(guidHash);
            if (PendingFrames.Count == 0)
                StopFrameRetryNoLock();
        }
    }

    private static void EnsureFrameRetryNoLock()
    {
        if (_frameRetryHooked) return;
        Plugin.Frame += OnFrameRetry;
        _frameRetryHooked = true;
    }

    private static void StopFrameRetry()
    {
        lock (Gate) StopFrameRetryNoLock();
    }

    private static void StopFrameRetryNoLock()
    {
        if (!_frameRetryHooked) return;
        Plugin.Frame -= OnFrameRetry;
        _frameRetryHooked = false;
    }

    /// <summary>
    /// Retries applies that failed because prefabs were not loaded yet. Subscribed only while
    /// <see cref="PendingFrames"/> is non-empty. Gives up after
    /// <see cref="PrefabWire.ApplyGiveUpFrames"/> so a missing source is not retried forever.
    /// On the client the budget also runs while the gate is closed: a local-only registration
    /// (single-player, or a server without the mod) never sees a <c>PrfD</c>/<c>PrfK</c>, so
    /// without this it would wait forever. Client-only items require that server packet.
    /// </summary>
    private static void OnFrameRetry()
    {
        try
        {
            var retry = new List<PrefabDefinition>();
            var giveUp = new List<(string id, int hash, int frames, bool noServerPacket)>();
            if (!ApplyGate.ShouldRunFrameRetry) return;
            lock (Gate)
            {
                if (PendingFrames.Count == 0)
                {
                    StopFrameRetryNoLock();
                    return;
                }

                var hashes = new int[PendingFrames.Count];
                PendingFrames.Keys.CopyTo(hashes, 0);
                for (int i = 0; i < hashes.Length; i++)
                {
                    int hash = hashes[i];
                    var step = ApplyGate.StepFrame(PendingFrames[hash], PrefabWire.ApplyGiveUpFrames);
                    if (!step.CountThisFrame) continue;
                    PendingFrames[hash] = step.FramesAfter;

                    PrefabDefinition? def = null;
                    for (int e = 0; e < Entries.Count; e++)
                    {
                        if (Entries[e].Definition.Guid.GuidHash != hash) continue;
                        def = Entries[e].Definition;
                        break;
                    }
                    if (def == null)
                    {
                        PendingFrames.Remove(hash);
                        continue;
                    }
                    if (step.GiveUp)
                    {
                        giveUp.Add((def.Id, hash, step.FramesAfter, step.GiveUpBecauseNoServerPacket));
                        PendingFrames.Remove(hash);
                        continue;
                    }
                    if (step.RetryApply)
                        retry.Add(def);
                }
                if (PendingFrames.Count == 0)
                    StopFrameRetryNoLock();
            }

            for (int i = 0; i < giveUp.Count; i++)
            {
                var g = giveUp[i];
                if (g.noServerPacket)
                {
                    Plugin.Logger?.LogWarning(
                        $"PrefabRegistry: stopped waiting for a server prefab packet to apply '{g.id}' ({g.hash}) after {g.frames} frames. " +
                        "Client-only items require a PrfD or PrfK from the server (the first packet of the join opens the apply gate); " +
                        "without that packet the definition is not applied. The next WorldReady or packet will try again.");
                }
                else
                {
                    Plugin.Logger?.LogWarning($"PrefabRegistry: stopped retrying apply of '{g.id}' ({g.hash}) after {g.frames} frames; the next WorldReady, save load, or definition packet will try again.");
                }
            }

            var world = TargetWorld();
            if (world == null || !world.IsCreated) return;
            for (int i = 0; i < retry.Count; i++)
                ApplyOne(world, retry[i], sendToConnected: Plugin.IsServerProcess);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"PrefabRegistry: frame retry threw: {e}");
        }
    }

    private static string DescribeOwner(object owner)
    {
        try
        {
            if (owner is SyncedOwnerTag) return "server sync";
            var text = owner.ToString();
            if (!string.IsNullOrEmpty(text) && text != owner.GetType().ToString()) return text!;
            return owner.GetType().FullName ?? owner.GetType().Name;
        }
        catch
        {
            return owner.GetType().Name;
        }
    }

    // ---- hooks --------------------------------------------------------------------------------

    private static void OnWorldReady(WorldReady ev)
    {
        try
        {
            // Server: the DeserializeLoadFile prefix is the authoritative seam and runs well
            // after WorldReady (the load takes many frames); retrying every frame in between
            // only burns the give-up budget. A brand-new world never loads a file, so the
            // server also opens the gate 30 s after WorldReady as a no-player fallback —
            // PlayerConnected opens it earlier if someone joins.
            // Client: verified live 2026-09-02 that the game's ItemKeyLookup is still being built
            // at WorldReady (1077 rows of a final 1084); a row appended then ends up at a
            // different index (659) than the server's (1083) once the game finishes. The
            // server's definition packet arrives from its OnUserConnected postfix, after the
            // table is complete, so the client applies only from that packet (PrfD or PrfK).
            // A new client world (rejoin) resets the gate, drops last-join SyncedOwner entries,
            // and clears Tombstones. Locally-owned Register()s stay. Do not append placeholders
            // here — that was the 1077-of-1084 failure on rejoin. Packets of this join arrive
            // after this event (server OnUserConnected postfix).
            // A Core swap of an established session is not a rejoin: EventHooks Uninstall
            // clears ReadyWorlds, so WatchWorlds republishes WorldReady for the same world
            // on this generation's first Frame. NotifyLiveWorldAfterCoreSwap already opened
            // the gate and Import restored the join; treating that republish as a new join
            // Reset the accepted ledger and made every custom item unusable.
            var plan = _worldReadyPlan ?? DecideWorldReady();
            _worldReadyPlan = null;

            if (plan.ScheduleNoPlayerFallback)
            {
                Dev.MainThread.PostDelayed(PrefabApplyGate.NoPlayerFallbackSeconds, () =>
                {
                    try
                    {
                        var fallback = ApplyGate.NotifyNoPlayerFallback();
                        if (!fallback.Opened) return;
                        Plugin.Logger?.LogInfo("PrefabRegistry: no save load and no player in 30 s after WorldReady; applying now (no-player fallback).");
                        if (fallback.ApplyPending)
                            ApplyPending(Worlds.Server, sendToConnected: false);
                        if (fallback.ApplyPlaceholders)
                            ApplyKeyPlaceholders(Worlds.Server);
                        if (!fallback.ApplyPending)
                            TryRunAllEdits(Worlds.Server, PrefabEditTrigger.CollectionReady);
                    }
                    catch (Exception e)
                    {
                        Plugin.Logger?.LogError($"PrefabRegistry: no-player fallback threw: {e}");
                    }
                });
            }

            // NotePending only while the gate is closed (starts the client give-up timer for
            // local registrations). ApplyKeyPlaceholders is skipped until the gate opens.
            // ApplyPending already sweeps vanilla GUIDs (CollectionReady). A WorldReady that
            // does not apply (gate still closed) still runs that sweep so vanilla editors
            // bind once the collection exists; clones wait for NewClone on a later apply.
            if (plan.ApplyPending)
                ApplyPending(ev.World, sendToConnected: false);
            if (plan.ApplyPlaceholders)
                ApplyKeyPlaceholders(ev.World);
            if (!plan.ApplyPending)
                TryRunAllEdits(ev.World, PrefabEditTrigger.CollectionReady);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"PrefabRegistry: WorldReady apply threw: {e}");
        }
    }

    /// <summary>
    /// First WorldReady subscriber (<see cref="WorldReadyResetPriority"/>): close the gate and
    /// run the new-world reset before anyone else sees the new world.
    /// </summary>
    private static void OnWorldReadyReset(WorldReady ev)
    {
        try
        {
            _worldReadyPlan = DecideWorldReady();
        }
        catch (Exception e)
        {
            _worldReadyPlan = null;
            Plugin.Logger?.LogError($"PrefabRegistry: WorldReady reset threw: {e}");
        }
    }

    private static PrefabApplyGate.WorldReadyPlan DecideWorldReady()
    {
        var plan = ApplyGate.NotifyWorldReady();
        if (plan.DropSyncedOwnerEntries || plan.ClearTombstones)
            ResetClientJoinState(
                plan.DropSyncedOwnerEntries,
                plan.ClearTombstones,
                plan.ForgetWorldBookkeeping,
                "for a new client world");
        return plan;
    }

    /// <summary>World epoch whose save-loaded items the keep rearm has already scanned.</summary>
    private static int _keepScannedEpoch = int.MinValue;

    /// <summary>
    /// The server registers clones in the DeserializeLoadFile prefix, before the save's items
    /// exist, so the registration-time rearm cannot reach them. The first connect of each world
    /// epoch scans the whole world once for applied <c>BloodBound</c> / <c>Indestructible</c>
    /// definitions; later instances are made from the clone and need no scan. Every connect
    /// still re-checks the clones and their lookup rows, which costs no scan.
    /// </summary>
    private static void RearmKeptItemsOnConnect(World world, PrefabDefinition[] snapshot, bool[] applied)
    {
        List<PrefabDefinition>? kept = null;
        for (int i = 0; i < snapshot.Length; i++)
        {
            if (!applied[i] || snapshot[i].Keep == null || snapshot[i].Kind != PrefabKind.Item) continue;
            (kept ??= new List<PrefabDefinition>()).Add(snapshot[i]);
        }
        if (kept == null) return;
        int epoch = Progenitor.WorldState.WorldEpoch.Current;
        bool scan = epoch != _keepScannedEpoch;
        try
        {
            PrefabApplicator.RearmKeptItems(world, kept, scanInstances: scan);
            if (scan) _keepScannedEpoch = epoch;
        }
        catch (Exception e)
        { Plugin.Logger?.LogWarning("PrefabRegistry: keep rearm on connect failed: " + e.Message); }
    }

    private static void OnPlayerConnected(PlayerConnected ev)
    {
        try
        {
            // Open and apply before any send: the client treats the first packet as "table is
            // complete". A brand-new world never runs DeserializeLoadFile, so this — not the
            // 30 s timer — is the first apply if someone joins in that window. Skip PrfD for
            // entries that still have Applied == false.
            var plan = ApplyGate.NotifyPlayerConnected();
            if (plan.ApplyPendingBeforeSend)
                ApplyPending(ev.World, sendToConnected: false);
            if (plan.ApplyPlaceholdersBeforeSend)
                ApplyKeyPlaceholders(ev.World);
            // PlaceholderSend: PrfD / PrfK to this joiner does not create or reset a clone.
            // Re-running here re-logged the same conflict on every connect (evidence 73).

            PrefabDefinition[] snapshot;
            bool[] applied;
            KeyTombstone[] tombs;
            lock (Gate)
            {
                snapshot = Entries.Count == 0
                    ? Array.Empty<PrefabDefinition>()
                    : new PrefabDefinition[Entries.Count];
                applied = Entries.Count == 0
                    ? Array.Empty<bool>()
                    : new bool[Entries.Count];
                for (int i = 0; i < Entries.Count; i++)
                {
                    snapshot[i] = Entries[i].Definition;
                    applied[i] = Entries[i].Applied;
                }
                tombs = Tombstones.Count == 0
                    ? Array.Empty<KeyTombstone>()
                    : Tombstones.ToArray();
            }
            RearmKeptItemsOnConnect(ev.World, snapshot, applied);
            // PrfI first: the client rebuilds its tail in this order before any PrfD/PrfK.
            // Reliable events keep order, so the ledger lands after the game's OnUserConnected
            // traffic and before join-time ItemKeyLookup count+hash.
            if (TryBuildKeyLedgerPayload(ev.World, out var ledgerPayload, out var ledgerError))
            {
                var bootstrap = ev.World.GetExistingSystemManaged<ServerBootstrapSystem>();
                bool sentOrder = false;
                if (bootstrap != null)
                {
                    if (ledgerPayload.Length <= KeyLedgerPacket.MaxPacketBytes)
                        sentOrder = CustomEvents.SendToUser(bootstrap, ev.UserIndex, KeyLedgerPacket.EventId, ledgerPayload);
                    else if (KeyLedgerChunks.TrySplit(ledgerPayload, Interlocked.Increment(ref _ledgerGeneration), out var chunks, out ledgerError))
                    {
                        sentOrder = true;
                        foreach (var chunk in chunks)
                            sentOrder &= CustomEvents.SendToUser(bootstrap, ev.UserIndex, KeyLedgerChunks.EventId, chunk);
                    }
                }
                Plugin.Logger?.LogInfo(
                    "PrefabRegistry: on-connect PrfI (" + ledgerPayload.Length
                    + " byte(s)) to " + ev.CharacterName + ": sent=" + sentOrder);
                if (!sentOrder) return;
            }
            else if (!string.IsNullOrEmpty(ledgerError))
            {
                Plugin.Logger?.LogWarning("PrefabRegistry: skipping on-connect PrfI: " + ledgerError);
                return;
            }

            foreach (var tomb in tombs)
            {
                var packet = PrefabUnregisterPacket.Placeholder(tomb.GuidHash, tomb.Id);
                bool sent = CustomEvents.SendToUser(ev.World, ev.UserEntity, PrefabUnregisterPacket.KeyPlaceholderEventId, packet);
                Plugin.Logger?.LogInfo($"PrefabRegistry: on-connect key placeholder '{tomb.Id}' ({tomb.GuidHash}) to {ev.CharacterName}: sent={sent}");
            }
            // Definitions, then key rows, then the edited component values (job FEDW1). Reliable
            // events keep order, so every PrfE lands on an entity this client already has.
            ItemDefinitionDelivery.Begin(ev.World, ev.UserEntity, snapshot, applied,
                () => PrefabEditWireApply.SendAllToUser(ev.World, Edits, ev.UserEntity, ev.CharacterName, freshJoin: true));
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"PrefabRegistry: PlayerConnected send threw: {e}");
        }
    }

    /// <summary>
    /// The client apply gate just opened for a new join (first <c>PrfD</c> or <c>PrfK</c> of the
    /// session, the same moment last join's <c>SyncedOwner</c> entries were dropped). Start a
    /// fresh per-join budget and report what the previous server added.
    /// </summary>
    private static void BeginSyncBudgetJoin()
    {
        PrefabSyncBudget.BeginJoin(out var previous);
        ItemStatsBudget.Reset();
        // The per-prefab PrfE charges are part of the same budget and must start over with it.
        Edits.Wire.ResetBudgetCharges();
        if (previous != null) Plugin.Logger?.LogInfo(previous);
    }

    /// <summary>
    /// <c>PrfE</c> receive (job FEDW1): the server's edited component values for one prefab. The
    /// layer is stored and applied, and re-applied after every local edit pass on that prefab, so
    /// a client-side editor cannot win over the server.
    /// </summary>
    private static void OnClientEditLayer(int eventId, byte* payload, int length)
    {
        _ = eventId;
        try
        {
            PrefabEditWireApply.OnPacket(Edits, TargetWorld(), payload, length);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"PrefabRegistry: PrfE receive threw: {e}");
        }
    }

    private static readonly KeyLedgerChunkAssembler LedgerChunks = new();
    private static int _ledgerGeneration;

    private static void OnClientKeyLedgerChunk(int eventId, byte* payload, int length)
    {
        if (length < KeyLedgerChunks.HeaderBytes || length > KeyLedgerChunks.MaxChunkBytes)
        {
            LedgerChunks.TryAccept(Array.Empty<byte>(), 0, out _, out _);
            lock (Gate) JoinSession.RefusePacket("Invalid chunked item-key ledger size.");
            return;
        }
        var copy = new byte[length];
        for (int i = 0; i < length; i++) copy[i] = payload[i];
        if (!LedgerChunks.TryAccept(copy, length, out var complete, out var error))
        {
            lock (Gate) JoinSession.RefusePacket(error);
            Plugin.Logger?.LogWarning("PrefabRegistry: refused chunked ledger: " + error);
            return;
        }
        if (complete == null) return;
        fixed (byte* bytes = complete) OnClientKeyLedger(KeyLedgerPacket.EventId, bytes, complete.Length);
    }

    private static void OnClientKeyLedger(int eventId, byte* payload, int length)
    {
        _ = eventId;
        try
        {
            LedgerChunks.TryAccept(Array.Empty<byte>(), 0, out _, out _);
            if (length < KeyLedgerPacket.HeaderBytes || length > KeyLedgerPacket.MaxLedgerBytes)
            {
                lock (Gate) JoinSession.RefusePacket("Invalid item-key ledger size.");
                return;
            }
            var copy = new byte[length];
            for (int i = 0; i < length; i++)
                copy[i] = payload[i];

            int clientBase = 0;
            int capacity = KeyAgreement.DefaultCapacity;
            string snapWhy = "no world";
            var world = TargetWorld();
            if (world == null || !world.IsCreated
                || !ItemKeyTable.TrySnapshot(world.EntityManager, out clientBase, out capacity, out _, out snapWhy))
            {
                string unavailable = KeyAgreement.PacketRefusedReason(
                    "client ItemKeyLookup unavailable (" + snapWhy + ")");
                lock (Gate) JoinSession.RefusePacket(unavailable);
                Plugin.Logger?.LogWarning(unavailable);
                return;
            }

            if (!KeyLedgerPacket.TryRead(copy, length, capacity, out int serverBase, out var rows, out var error))
            {
                string refused = KeyAgreement.PacketRefusedReason(error);
                lock (Gate) JoinSession.RefusePacket(refused);
                Plugin.Logger?.LogWarning(refused);
                return;
            }

            var packetPlan = ApplyGate.NotifyClientPacket(PrefabApplyGate.ClientPacket.IndexLedger);
            if (packetPlan.OpenedGate)
            {
                Plugin.Logger?.LogInfo("PrefabRegistry: first key-table order packet of this join.");
                BeginSyncBudgetJoin();
            }

            KeyLocalEntry[] local;
            lock (Gate)
            {
                int n = 0;
                for (int i = 0; i < Entries.Count; i++)
                {
                    if (PrefabApplyBookkeeping.ShouldAppendItemKey(Entries[i].Definition.Kind)) n++;
                }
                local = n == 0 ? Array.Empty<KeyLocalEntry>() : new KeyLocalEntry[n];
                int w = 0;
                for (int i = 0; i < Entries.Count; i++)
                {
                    var def = Entries[i].Definition;
                    if (!PrefabApplyBookkeeping.ShouldAppendItemKey(def.Kind)) continue;
                    local[w++] = new KeyLocalEntry(def.Guid.GuidHash, def.Id);
                }
            }

            var decision = KeyAgreement.Plan(serverBase, clientBase, capacity, rows, local);
            string admitLog;
            bool accepted;
            lock (Gate) accepted = JoinSession.TryAcceptLedger(decision, out admitLog);
            if (!accepted)
            {
                Plugin.Logger?.LogWarning(admitLog);
                return;
            }
            Plugin.Logger?.LogInfo(admitLog);
            for (int i = 0; i < decision.Unknown.Count; i++)
                LogJoinRefuseOnce(decision.Unknown[i].GuidHash, decision.Unknown[i].Reason);
            ApplyPending(world, sendToConnected: false);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"PrefabRegistry: PrfI receive threw: {e}");
        }
    }

    // Read the same historical suffix that joining clients receive; never estimate it from
    // active definitions alone, since retired items retain their native key-table rows.
    internal static bool RequiresExtendedSync
    {
        get
        {
            if (PrefabSyncBudget.Cap > PrefabSyncBudget.DefaultMaxPerJoin) return true;
            var world = Worlds.Server;
            return world != null && world.IsCreated
                && TryBuildKeyLedgerPayload(world, out var payload, out _)
                && payload.Length > KeyLedgerPacket.MaxPacketBytes;
        }
    }

    internal static bool TryCheckDataPackAdmission(World world,
        IReadOnlyList<DataPackItemIdentity> proposed, out string error)
    {
        if (!TryBuildKeyLedgerPayload(world, out var payload, out error)) return false;
        if (!ItemKeyTable.TrySnapshot(world.EntityManager, out _, out int capacity, out _, out error)) return false;
        if (!KeyLedgerPacket.TryRead(payload, payload.Length, capacity, out int baseLength, out var rows, out error)) return false;
        var known = new List<DataPackItemIdentity>();
        var items = new List<DataPackItemIdentity>();
        lock (Gate)
        {
            foreach (var entry in Entries)
            {
                var def = entry.Definition;
                var identity = new DataPackItemIdentity(def.Id, def.Guid.GuidHash);
                known.Add(identity);
                if (def.Kind == PrefabKind.Item) items.Add(identity);
            }
            foreach (var tomb in Tombstones) known.Add(new DataPackItemIdentity(tomb.Id, tomb.GuidHash));
        }
        // Standalone PrfE layers also consume the client's distinct-identity budget,
        // even when their native prefab has no custom item-key row. ReaderGuids snapshots
        // its own lock; do not nest that lock under the registry lock above.
        foreach (int guid in Edits.Wire.ReaderGuids())
            known.Add(new DataPackItemIdentity("", guid));
        if (!DataPackAdmissionRules.TryValidate(proposed, known, items, rows, baseLength, capacity, out error, PrefabSyncBudget.Cap)) return false;
        var projected = new Dictionary<int, KeyLedgerRow>();
        foreach (var row in rows) projected[row.GuidHash] = row;
        foreach (var item in items) projected[item.Guid] = new KeyLedgerRow(item.Guid, item.Id, "", false);
        foreach (var item in proposed) projected[item.Guid] = new KeyLedgerRow(item.Guid, item.Id, "", false);
        if ((PrefabSyncBudget.Cap > PrefabSyncBudget.DefaultMaxPerJoin
            || KeyLedgerPacket.EncodedSize(new List<KeyLedgerRow>(projected.Values)) > KeyLedgerPacket.MaxPacketBytes)
            && !Handshake.ItemLedgerClientsCompatible())
        {
            error = "Connected clients need extended item synchronization before this pack can activate.";
            return false;
        }
        return true;
    }

    private static bool TryBuildKeyLedgerPayload(World world, out byte[] payload, out string error)
    {
        payload = Array.Empty<byte>();
        error = "";
        if (!ItemKeyTable.TrySnapshot(world.EntityManager, out int length, out int capacity, out var hashes, out error))
            return false;

        PrefabApplicator.ExportClonesAndKeys(out _, out var appendedGuids, out var appendedIndices);
        KeyTombstone[] tombs;
        PrefabDefinition[] defs;
        lock (Gate)
        {
            tombs = Tombstones.Count == 0 ? Array.Empty<KeyTombstone>() : Tombstones.ToArray();
            defs = Entries.Count == 0
                ? Array.Empty<PrefabDefinition>()
                : new PrefabDefinition[Entries.Count];
            for (int i = 0; i < Entries.Count; i++)
                defs[i] = Entries[i].Definition;
        }

        var ownedIndex = new Dictionary<int, int>();
        for (int i = 0; i < appendedGuids.Length; i++)
        {
            int idx = appendedIndices[i];
            if (idx >= 0) ownedIndex[appendedGuids[i]] = idx;
        }
        for (int i = 0; i < tombs.Length; i++)
        {
            int idx = tombs[i].Index;
            if (idx < 0)
            {
                for (int h = 0; h < hashes.Length; h++)
                {
                    if (hashes[h] != tombs[i].GuidHash) continue;
                    idx = h;
                    break;
                }
            }
            if (idx >= 0) ownedIndex[tombs[i].GuidHash] = idx;
        }

        int baseLength = length;
        if (ownedIndex.Count > 0)
        {
            int min = length;
            int max = -1;
            foreach (var kv in ownedIndex)
            {
                if (kv.Value < min) min = kv.Value;
                if (kv.Value > max) max = kv.Value;
            }
            if (min < 0 || max != length - 1 || ownedIndex.Count != length - min)
            {
                error = "ItemKeyLookup tail is not a contiguous Progenitor suffix (min="
                    + min + " max=" + max + " owned=" + ownedIndex.Count
                    + " length=" + length + "); not sending PrfI.";
                return false;
            }
            baseLength = min;
        }

        int tail = length - baseLength;
        // Build once: scanning every definition for every row makes each admission
        // quadratic, which compounds when a pack registers many individual items.
        var ids = new Dictionary<int, string>();
        foreach (var def in defs) ids.TryAdd(def.Guid.GuidHash, def.Id);
        foreach (var tomb in tombs) ids.TryAdd(tomb.GuidHash, tomb.Id);
        var rows = tail == 0 ? Array.Empty<KeyLedgerRow>() : new KeyLedgerRow[tail];
        for (int i = 0; i < tail; i++)
        {
            int guid = hashes[baseLength + i];
            string id = ids.TryGetValue(guid, out var knownId) ? knownId : "";
            rows[i] = new KeyLedgerRow(guid, id, owner: "", tombstone: false);
        }

        if (!KeyLedgerPacket.TryValidateBounds(baseLength, capacity, rows, out error))
            return false;
        payload = new byte[KeyLedgerPacket.EncodedSize(rows)];
        return KeyLedgerPacket.TryWrite(payload, baseLength, capacity, rows, out _, out error);
    }

    internal static void SendItemStatsToUser(World world, Entity userEntity)
    {
        // Also restarts an interrupted stream after a compatible Core hot swap.
        if (!ItemDefinitionDelivery.HasPending(userEntity))
            OnPlayerConnected(new PlayerConnected(world, userEntity, world.EntityManager.GetComponentData<ProjectM.Network.User>(userEntity)));
        ItemDefinitionDelivery.Ready(userEntity);
    }
    private static void SendItemStatsToSupportedClients(World world, PrefabDefinition definition)
    {
        var em = world.EntityManager;
        var users = Query.Entities(em, includeDisabled: true, includeSystems: false,
            ComponentIndex.Of<ProjectM.Network.User>(ComponentType.AccessMode.ReadOnly));
        var packet = ItemStatPacket.From(definition);
        for (int i = 0; i < users.Count; i++)
        {
            try
            {
                var user = em.GetComponentData<ProjectM.Network.User>(users[i]);
                if (user.IsConnected && Handshake.SupportsItemStats(user.Index))
                    CustomEvents.SendToUser(world, users[i], ItemStatPacket.EventId, packet);
            }
            catch (Exception e)
            {
                Plugin.Logger?.LogWarning("PrefabRegistry: sending item stats to a client failed: " + e.Message);
            }
        }
    }

    private static readonly ItemStatSyncBudget ItemStatsBudget = new();

    private static void OnClientItemStats(int eventId, byte* payload, int length)
    {
        try
        {
            if (!ItemStatsBudget.TryAdmit(out bool reportCap))
            {
                if (reportCap) Plugin.Logger?.LogWarning("PrefabRegistry: equipment-stat packet rate exceeded; dropping packets until the receive window renews.");
                return;
            }
            if (!CustomEvents.TryRead<ItemStatPacket>(payload, length, out var packet)
                || !packet.TryBonuses(out var bonuses))
            {
                Plugin.Logger?.LogWarning("PrefabRegistry: ignoring invalid item-stat packet.");
                return;
            }
            PrefabDefinition? definition = null;
            lock (Gate)
            {
                foreach (var entry in Entries)
                {
                    var candidate = entry.Definition;
                    if (candidate.Guid.GuidHash != packet.NewGuid) continue;
                    // Never turn an unsolicited packet into a prefab registration or edit.
                    // Local plugins own their definitions; only server-synced items qualify.
                    if (candidate.Kind != PrefabKind.Item
                        || !ReferenceEquals(candidate.Owner, SyncedOwner)
                        || candidate.Source.GuidHash != packet.SourceGuid)
                        break;
                    definition = candidate;
                    break;
                }
            }
            if (definition == null)
            {
                Plugin.Logger?.LogWarning("PrefabRegistry: item-stat packet has no matching server-owned item definition.");
                return;
            }
            definition.AssignStatBonuses(bonuses);
            TryApplyNow(definition, sendToConnected: false);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"PrefabRegistry: client item-stat handler threw: {e}");
        }
    }

    private static void OnClientDefinition(int eventId, byte* payload, int length)
    {
        try
        {
            if (!CustomEvents.TryRead<PrefabDefinitionPacket>(payload, length, out var packet))
            {
                Plugin.Logger?.LogWarning($"PrefabRegistry: definition payload wrong size: {length} (expected {sizeof(PrefabDefinitionPacket)})");
                return;
            }
            // First PrfD or PrfK of this join: the server sent it after its table was complete,
            // so ours is too. Apply anything registered locally (Both-side plugins) first, then
            // this packet, so order matches the wire. A later packet of the same join does not
            // dump pending again.
            var packetPlan = ApplyGate.NotifyClientPacket(PrefabApplyGate.ClientPacket.Definition);
            if (packetPlan.OpenedGate)
            {
                Plugin.Logger?.LogInfo("PrefabRegistry: first definition packet of this join; applying local definitions.");
                BeginSyncBudgetJoin();
            }
            string missingLedger;
            bool newlyRefused;
            lock (Gate) newlyRefused = JoinSession.NoteDefinitionPacket(out missingLedger);
            if (newlyRefused)
                Plugin.Logger?.LogWarning(missingLedger);
            if (packetPlan.ApplyPendingFirst)
                ApplyPending(Worlds.Client, sendToConnected: false);

            string id = packet.Id;
            // Threat model surface 6: a hostile server chooses this id. Hold it to the same
            // 1-63 ASCII rule a local DefineItem must pass before anything is cloned or a key
            // row is appended.
            if (!PrefabWire.TryValidateAscii(id, "id", out var idError, out _))
            {
                Plugin.Logger?.LogWarning($"PrefabRegistry: ignoring PrfD with an invalid id: {idError}");
                return;
            }

            int hashed = PrefabGUID.CalculateGuidHash(id);
            if (hashed != packet.NewGuid)
            {
                Plugin.Logger?.LogWarning($"PrefabRegistry: ignoring PrfD id='{id}' NewGuid={packet.NewGuid} != CalculateGuidHash {hashed}.");
                return;
            }
            if (!PrefabSyncBudget.TryAdmitDefinition(packet.NewGuid, out var capLog))
            {
                if (capLog != null) Plugin.Logger?.LogWarning(capLog);
                return;
            }

            var guid = new PrefabGUID(hashed);
            var source = new PrefabGUID(packet.SourceGuid);
            PrefabDefinition? existing = null;
            bool wasPlaceholder;
            lock (Gate)
            {
                // A PrfK placeholder is not a PrefabDefinition. Dropping the tombstone here lets
                // this packet apply: full clone if this client never had the entity (late joiner),
                // or in-place update if a prior apply left the clone retired-but-resolvable.
                // TryAppend on the reserved ItemKeyLookup row is already-present.
                wasPlaceholder = RemoveTombstoneNoLock(guid.GuidHash);
                foreach (var entry in Entries)
                {
                    if (entry.Definition.Guid.GuidHash != guid.GuidHash) continue;
                    existing = entry.Definition;
                    break;
                }
            }

            string? localizedName = string.IsNullOrEmpty(packet.Name) ? null : packet.Name;
            string? localizedDescription = string.IsNullOrEmpty(packet.Description) ? null : packet.Description;

            if (existing != null)
            {
                if (ReferenceEquals(existing.Owner, SyncedOwner))
                {
                    existing.UpdateSyncedDefinition(new PrefabFillFields(packet.MaxAmount, source.GuidHash,
                        localizedName, localizedDescription, PrefabWire.KindFromWire(packet.Kind)));
                    TryApplyNow(existing, sendToConnected: false);
                    return;
                }
                // Fill only what the local registration left empty; promote to Buff when the
                // packet says so, never demote a local DefineBuff. The fill is connection-scoped:
                // EndClientConnection puts the local values back (and drops any server text, which
                // is registered under SyncedOwner), so the next server fills from scratch.
                existing.FillFromServer(
                    new PrefabFillFields(
                        packet.MaxAmount,
                        source.GuidHash,
                        localizedName,
                        localizedDescription,
                        PrefabWire.KindFromWire(packet.Kind)),
                    SyncedOwner);
                TryApplyNow(existing, sendToConnected: false);
                return;
            }

            if (wasPlaceholder)
                Plugin.Logger?.LogInfo($"PrefabRegistry: definition '{id}' ({packet.NewGuid}) follows a key-table placeholder; applying (clone or in-place update).");

            var def = new PrefabDefinition(
                id,
                guid,
                source,
                sourceName: null,
                packet.MaxAmount,
                localizedName,
                localizedDescription,
                SyncedOwner,
                PrefabWire.KindFromWire(packet.Kind));
            var handle = new RegistrationHandle();
            lock (Gate) Entries.Add(new Entry(def, handle));
            TryApplyNow(def, sendToConnected: false);
            if (IsLiveEntry(def))
                RaiseRegistered(def);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"PrefabRegistry: client definition handler threw: {e}");
        }
    }

    /// <summary>
    /// Soft unapply on the client: drop the matching in-memory definition (bookkeeping) and run
    /// <see cref="PrefabApplicator.TryUnapply"/>, which only <see cref="PrefabDefinition.RunUndo"/>s
    /// localization. The clone entity and GUID maps stay so held stacks still resolve. Does not
    /// compact ItemKeyLookup — the table never shrinks; a <c>PrfK</c> placeholder follows this
    /// packet so a client that never appended the row still matches join-time count+hash. A later
    /// definition of the same id updates the surviving clone (and clears the tombstone).
    /// </summary>
    private static void OnClientUnregister(int eventId, byte* payload, int length)
    {
        try
        {
            if (!CustomEvents.TryRead<PrefabUnregisterPacket>(payload, length, out var packet))
            {
                Plugin.Logger?.LogWarning($"PrefabRegistry: unregister payload wrong size: {length} (expected {sizeof(PrefabUnregisterPacket)})");
                return;
            }

            string id = packet.Id;
            Entry? removed = null;
            lock (Gate)
            {
                for (int i = Entries.Count - 1; i >= 0; i--)
                {
                    var entry = Entries[i];
                    bool guidMatch = entry.Definition.Guid.GuidHash == packet.NewGuid;
                    bool idMatch = id.Length > 0 && string.Equals(entry.Definition.Id, id, StringComparison.Ordinal);
                    if (!guidMatch && !idMatch) continue;
                    if (!ReferenceEquals(entry.Definition.Owner, SyncedOwner))
                    {
                        Plugin.Logger?.LogInfo($"PrefabRegistry: PrfU guid={packet.NewGuid} id='{id}' ignored; local owner {DescribeOwner(entry.Definition.Owner)} keeps its registration.");
                        return;
                    }
                    removed = entry;
                    Entries.RemoveAt(i);
                    break;
                }
            }

            if (removed == null)
            {
                Plugin.Logger?.LogInfo($"PrefabRegistry: unregister packet guid={packet.NewGuid} id='{id}' had no local registration.");
                return;
            }

            // The server retired this prefab, so its edit layer is retired with it. The captured
            // baseline stays: it is what puts vanilla back (job FEDW1 rides FEDIT1's restore).
            Edits.Wire.DropReceived(packet.NewGuid);
            UnapplyEntry(removed);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"PrefabRegistry: client unregister handler threw: {e}");
        }
    }

    /// <summary>
    /// Appends the GUID to ItemKeyLookup without registering a prefab. Already-present is
    /// success. Recorded as a tombstone so a later definition packet of the same GUID drops
    /// the placeholder and applies (full clone here — this client never received <c>PrfD</c>).
    /// The first <c>PrfK</c> of a join is also the apply-gate (same as the first <c>PrfD</c>):
    /// the server sent it after its table was complete. Local definitions are applied before
    /// this placeholder so packet order is preserved.
    /// </summary>
    private static void OnClientKeyPlaceholder(int eventId, byte* payload, int length)
    {
        try
        {
            if (!CustomEvents.TryRead<PrefabUnregisterPacket>(payload, length, out var packet))
            {
                Plugin.Logger?.LogWarning($"PrefabRegistry: key-placeholder payload wrong size: {length} (expected {sizeof(PrefabUnregisterPacket)})");
                return;
            }

            var packetPlan = ApplyGate.NotifyClientPacket(PrefabApplyGate.ClientPacket.KeyPlaceholder);
            if (packetPlan.OpenedGate)
            {
                Plugin.Logger?.LogInfo("PrefabRegistry: first key-placeholder packet of this join; applying local definitions.");
                BeginSyncBudgetJoin();
            }
            string missingLedger;
            bool newlyRefused;
            lock (Gate) newlyRefused = JoinSession.NoteDefinitionPacket(out missingLedger);
            if (newlyRefused)
                Plugin.Logger?.LogWarning(missingLedger);
            if (packetPlan.ApplyPendingFirst)
                ApplyPending(Worlds.Client, sendToConnected: false);

            // Threat model surface 6. Same field rules as PrfD before a key row is appended:
            // 1-63 ASCII id, and a guid that really is CalculateGuidHash(id) so a server cannot
            // append an arbitrary hash under a name that does not derive it.
            string placeholderId = packet.Id;
            if (!PrefabWire.TryValidateAscii(placeholderId, "id", out var placeholderError, out _))
            {
                Plugin.Logger?.LogWarning($"PrefabRegistry: ignoring PrfK with an invalid id: {placeholderError}");
                return;
            }
            int placeholderHash = PrefabGUID.CalculateGuidHash(placeholderId);
            if (placeholderHash != packet.NewGuid)
            {
                Plugin.Logger?.LogWarning($"PrefabRegistry: ignoring PrfK id='{placeholderId}' NewGuid={packet.NewGuid} != CalculateGuidHash {placeholderHash}.");
                return;
            }

            if (!PrefabSyncBudget.TryAdmitPlaceholder(packet.NewGuid, out var capLog))
            {
                if (capLog != null) Plugin.Logger?.LogWarning(capLog);
                return;
            }

            if (HasLocalNonSynced(packet.NewGuid, packet.Id))
            {
                Plugin.Logger?.LogInfo($"PrefabRegistry: PrfK guid={packet.NewGuid} id='{packet.Id}' ignored; a local plugin owns this registration.");
                return;
            }

            bool skipPlaceholder;
            lock (Gate) skipPlaceholder = JoinSession.ShouldSkipItem;
            if (skipPlaceholder)
            {
                Plugin.Logger?.LogWarning(
                    "PrefabRegistry: ignoring PrfK '" + packet.Id + "' (" + packet.NewGuid
                    + "): key-table agreement was refused this join; not appending a local-order row.");
                return;
            }

            RememberTombstone(packet.NewGuid, packet.Id, -1);

            var world = Worlds.Client;
            if (world == null || !world.IsCreated)
            {
                Plugin.Logger?.LogInfo($"PrefabRegistry: key placeholder '{packet.Id}' ({packet.NewGuid}) recorded; world not ready.");
                return;
            }
            if (!PrefabApplyGate.ShouldApplyPlaceholders(ApplyGate.Ready)) return;
            TryAppendKeyPlaceholder(world, packet.NewGuid, packet.Id, -1);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"PrefabRegistry: client key-placeholder handler threw: {e}");
        }
    }

    internal delegate void DeserializeLoadFile();

    private static void BindDeserializeSites(Harmony? harmony)
    {
        var prefix = HarmonySlotHandoff.Bind(
            HarmonySlotHandoff.DeserializeLoadFilePrefix,
            new DeserializeLoadFile(LoadPrefix.BeforeDeserialize));
        var postfix = HarmonySlotHandoff.Bind(
            HarmonySlotHandoff.DeserializeLoadFilePostfix,
            new DeserializeLoadFile(LoadPrefix.AfterDeserialize));
        LogBind("PrefabRegistry", HarmonySlotHandoff.DeserializeLoadFilePrefix, prefix);
        LogBind("PrefabRegistry", HarmonySlotHandoff.DeserializeLoadFilePostfix, postfix);

        if (prefix == HarmonySlotHandoff.SiteBind.Fallback && postfix == HarmonySlotHandoff.SiteBind.Fallback)
        {
            Plugin.Logger?.LogInfo(HarmonySlotHandoff.FallbackSiteLog(HarmonySlotHandoff.DeserializeLoadFile));
            FallbackDeserialize(harmony);
        }
    }

    /// <summary>Core's registry owner id; every Core slot registration shares it (job FHARM8).</summary>
    internal const string OwnerId = MyPluginInfo.PLUGIN_GUID + Progenitor.Patches.PatchSitePolicy.CoreOwnerSuffix;

    /// <summary>
    /// Job FHARM8. The <c>DeserializeLoadFile</c> fallback, taken whenever Bootstrap does not own
    /// that site — which is not only "Bootstrap is missing" but also a Bootstrap one version
    /// behind, or one whose own patch for the site failed. Before FHARM8 it was an unconditional
    /// <c>PatchAll(typeof(LoadPrefix))</c>, which handed HarmonyX two <c>Progenitor.Core</c>
    /// methods and pinned this Core generation for the life of the process even after a clean
    /// unpatch (<c>harmony-patches.md</c> section 8).
    ///
    /// <para><b>The shape fits.</b> Both bodies take no arguments, read no <c>__result</c> and
    /// carry no <c>__state</c> between them — <c>BeforeDeserialize</c> is a plain prefix that
    /// never skips the original and <c>AfterDeserialize</c> is a diagnostic postfix — so the
    /// pool's signature-agnostic <c>(__instance, __args)</c> body costs them nothing at all and
    /// <see cref="CorePatchFallbackPolicy"/> routes them to slots. With no Bootstrap present
    /// there is no pool and no hot swap to protect, so the old direct <c>PatchAll</c> stands with
    /// one line naming the pin.</para>
    ///
    /// <para>The two are registered independently: the postfix is a log probe, so losing it must
    /// not cost the prefix, which is the half that makes modded GUIDs resolve out of the save.</para>
    /// </summary>
    private static void FallbackDeserialize(Harmony? harmony)
    {
        var route = Progenitor.Patches.CorePatchFallbackPolicy.Decide(
            slotShapeFits: true,
            bootstrapPresent: Progenitor.Patches.PatchRegistry.SlotsAvailable);

        if (route == Progenitor.Patches.CoreFallbackRoute.Refused)
        {
            // Unreachable while slotShapeFits is true; handled so a future body that needs
            // __result cannot fall through to a silent permanent pin.
            Plugin.Logger?.LogError(Progenitor.Patches.CorePatchFallbackPolicy.FormatRefused(
                Progenitor.Patches.CorePatchFallbackPolicy.ModuleContent,
                HarmonySlotHandoff.DeserializeLoadFile));
            return;
        }

        if (route == Progenitor.Patches.CoreFallbackRoute.Direct)
        {
            Plugin.Logger?.LogWarning(Progenitor.Patches.CorePatchFallbackPolicy.FormatDirect(
                Progenitor.Patches.CorePatchFallbackPolicy.ModuleContent,
                HarmonySlotHandoff.DeserializeLoadFile,
                Progenitor.WorldState.WorldEpoch.CoreGenerationOrZero()));
            try { RequireHarmony(harmony).PatchAll(typeof(LoadPrefix)); }
            catch (Exception e)
            {
                Plugin.Logger?.LogError($"PrefabRegistry: fallback PatchAll(LoadPrefix) threw: {e}");
            }
            return;
        }

        try
        {
            var original = AccessTools.Method(
                typeof(LoadPersistenceSystemV2), nameof(LoadPersistenceSystemV2.DeserializeLoadFile));
            if (original == null)
            {
                Plugin.Logger?.LogError(
                    "PrefabRegistry: LoadPersistenceSystemV2.DeserializeLoadFile was not found on the "
                    + "interop type; modded prefab GUIDs already in the save will not resolve.");
                return;
            }

            Progenitor.Patches.Patches.Install();
            if (Progenitor.Patches.PatchRegistry.PrefixSlot(
                    owner: null, ownerId: OwnerId, target: original,
                    prefix: static (_, _) => { LoadPrefix.BeforeDeserialize(); return true; }) == null)
            {
                Plugin.Logger?.LogError(
                    "PrefabRegistry: the slot-routed fallback for "
                    + HarmonySlotHandoff.DeserializeLoadFile + " was refused (see the patches: line "
                    + "above); modded prefab GUIDs already in the save will not resolve for this "
                    + "Core generation.");
            }

            if (Progenitor.Patches.PatchRegistry.PostfixSlot(
                    owner: null, ownerId: OwnerId, target: original,
                    postfix: static (_, _) => LoadPrefix.AfterDeserialize()) == null)
            {
                Plugin.Logger?.LogWarning(
                    "PrefabRegistry: the slot-routed post-load key-table probe for "
                    + HarmonySlotHandoff.DeserializeLoadFile + " was refused (see the patches: line "
                    + "above); the load itself is unaffected, only the diagnostic is off.");
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"PrefabRegistry: slot fallback for DeserializeLoadFile threw: {e}");
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

    private static void LogBind(string module, string site, HarmonySlotHandoff.SiteBind kind)
    {
        if (kind == HarmonySlotHandoff.SiteBind.Inert)
            Plugin.Logger?.LogError(HarmonySlotHandoff.InertLog(module, site));
        else if (kind == HarmonySlotHandoff.SiteBind.Vanilla)
            Plugin.Logger?.LogError(HarmonySlotHandoff.VanillaLog(module, site));
    }

    /// <summary>
    /// Runs before the save is deserialized so inventories that already store our GUID still
    /// resolve. Prefabs are loaded by this point; spike 2 used this exact seam.
    /// </summary>
    [HarmonyPatch(typeof(LoadPersistenceSystemV2), nameof(LoadPersistenceSystemV2.DeserializeLoadFile))]
    private static class LoadPrefix
    {
        [HarmonyPrefix]
        internal static void BeforeDeserialize()
        {
            try
            {
                DataPackRuntime.RegisterBeforeDeserialize();
                var plan = ApplyGate.NotifyLoadPrefix();
                LogKeyTable("before load");
                if (plan.ApplyPending)
                    ApplyPending(Worlds.Server, sendToConnected: false);
                if (plan.ApplyPlaceholders)
                    ApplyKeyPlaceholders(Worlds.Server);
                if (!plan.ApplyPending)
                    TryRunAllEdits(Worlds.Server, PrefabEditTrigger.CollectionReady);
            }
            catch (Exception e)
            {
                Plugin.Logger?.LogError($"PrefabRegistry: pre-load apply threw: {e}");
            }
        }

        [HarmonyPostfix]
        internal static void AfterDeserialize()
        {
            try { LogKeyTable("after load"); }
            catch (Exception e) { Plugin.Logger?.LogError($"PrefabRegistry: post-load probe threw: {e}"); }
            UnlockPrune.NoteSaveLoaded();
        }

        /// <summary>Diagnostic: where each registered GUID sits in ItemKeyLookup around the save load.</summary>
        private static int _probeCalls;
        private static void LogKeyTable(string when)
        {
            // DeserializeLoadFile runs several times per load; one before/after pair is enough.
            if (when == "before load") _probeCalls++;
            if (_probeCalls > 1) return;
            var world = Worlds.Server;
            if (world == null || !world.IsCreated) return;
            PrefabDefinition[] snapshot;
            lock (Gate)
            {
                snapshot = new PrefabDefinition[Entries.Count];
                for (int i = 0; i < Entries.Count; i++) snapshot[i] = Entries[i].Definition;
            }
            foreach (var def in snapshot)
                Plugin.Logger?.LogInfo($"PrefabRegistry: key table {when} for '{def.Id}': {ItemKeyTable.Describe(world.EntityManager, def.Guid)}");
        }
    }

    // ---- tombstones ---------------------------------------------------------------------------

    private static void RememberTombstoneIfKeyRowExists(PrefabDefinition def)
    {
        if (!PrefabApplicator.WasAppendedHere(def.Guid)) return;
        var world = TargetWorld();
        if (world == null || !world.IsCreated) return;
        if (!KeyTableHas(world, def.Guid)) return;
        bool hasLive;
        PrefabKind liveKind;
        lock (Gate)
            hasLive = TryGetLiveKindNoLock(def.Guid.GuidHash, out liveKind);
        if (!PrefabApplyBookkeeping.ShouldTombstone(true, hasLive, liveKind)) return;
        int index = PrefabApplicator.TryGetAppendedIndex(def.Guid.GuidHash, out var saved) ? saved : ItemKeyTable.IndexOf(world.EntityManager, def.Guid);
        RememberTombstone(def.Guid.GuidHash, def.Id, index);
    }

    private static void RememberTombstone(int guidHash, string id, int index)
    {
        lock (Gate)
        {
            // A live item of this GUID is a re-register and owns the row. A live buff
            // (or no entry) of an appended GUID is the join-hash hole: tombstone it.
            if (TryGetLiveKindNoLock(guidHash, out var liveKind) && liveKind == PrefabKind.Item)
                return;
            AddTombstoneNoLock(guidHash, id, index);
        }
    }

    private static bool HasEntryNoLock(int guidHash)
    {
        for (int i = 0; i < Entries.Count; i++)
        {
            if (Entries[i].Definition.Guid.GuidHash == guidHash) return true;
        }
        return false;
    }

    private static bool TryGetLiveKindNoLock(int guidHash, out PrefabKind kind)
    {
        for (int i = 0; i < Entries.Count; i++)
        {
            if (Entries[i].Definition.Guid.GuidHash != guidHash) continue;
            kind = Entries[i].Definition.Kind;
            return true;
        }
        kind = PrefabKind.Item;
        return false;
    }

    private static void AddTombstoneNoLock(int guidHash, string id, int index = -1)
    {
        for (int i = 0; i < Tombstones.Count; i++)
        {
            if (Tombstones[i].GuidHash == guidHash) return;
        }
        Tombstones.Add(new KeyTombstone(guidHash, id ?? "", index));
    }

    private static bool RemoveTombstoneNoLock(int guidHash)
    {
        bool removed = false;
        for (int i = Tombstones.Count - 1; i >= 0; i--)
        {
            if (Tombstones[i].GuidHash != guidHash) continue;
            Tombstones.RemoveAt(i);
            removed = true;
        }
        return removed;
    }

    private static bool EntityStillResolves(World world, PrefabGUID guid)
    {
        try
        {
            if (world == null || !world.IsCreated) return false;
            var prefabs = world.GetExistingSystemManaged<PrefabCollectionSystem>();
            if (prefabs == null) return false;
            if (!prefabs._PrefabGuidToEntityMap.TryGetValue(guid, out var entity)) return false;
            return world.EntityManager.Exists(entity);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning($"PrefabRegistry: entity-resolve probe threw: {e.Message}");
            return false;
        }
    }

    private static bool KeyTableHas(World world, PrefabGUID guid)
    {
        try
        {
            if (world == null || !world.IsCreated) return false;
            return ItemKeyTable.IndexOf(world.EntityManager, guid) >= 0;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning($"PrefabRegistry: ItemKeyLookup probe threw: {e.Message}");
            return false;
        }
    }

    /// <summary>
    /// The client's connection to its server ended (kick, walk to the menu, timeout, or
    /// <c>Client_0</c> destroyed): undo everything that server sent, now rather than at the next
    /// join. Its <c>PrfE</c> layers are withdrawn against the world if it still exists — the
    /// captured baselines go back and this client's own editors re-run, exactly as for a server
    /// withdraw packet — then its definitions, text, fills, tombstones and join session go.
    /// Local registrations and local editors stay. World-scoped bookkeeping stays too:
    /// <c>Client_0</c> can outlive the connection (FMAN17), so it is forgotten only when a new
    /// world replaces it (<see cref="OnWorldReady"/>).
    /// </summary>
    private static void OnClientConnectionEnded()
    {
        ItemDefinitionDelivery.EndClientConnection();
        try
        {
            if (!ApplyGate.NotifyClientConnectionEnded()) return;
            int withdrawn = WithdrawReceivedLayers(TargetWorld());
            if (withdrawn > 0)
                Plugin.Logger?.LogInfo($"PrefabRegistry: withdrew {withdrawn} server edit layer(s) at connection end; this client's own values are back.");
            ResetClientJoinState(dropSynced: true, clearTombs: true, forgetWorld: false, when: "at connection end");
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"PrefabRegistry: connection-end reset threw: {e}");
        }
    }

    /// <summary>
    /// Withdraw every received server layer through the same path a withdraw packet takes.
    /// Returns how many layers were dropped.
    /// </summary>
    private static int WithdrawReceivedLayers(World? world)
    {
        var guids = Edits.Wire.ReceivedGuids();
        int n = 0;
        for (int i = 0; i < guids.Length; i++)
        {
            if (!Edits.Wire.DropReceived(guids[i])) continue;
            n++;
            try { OnEditLayerWithdrawn(world, guids[i]); }
            catch (Exception e)
            {
                Plugin.Logger?.LogError($"PrefabRegistry: withdraw of server layer {guids[i]} at connection end threw: {e}");
            }
        }
        return n;
    }

    /// <summary>
    /// Drop the last server's state: its synced definitions (undo run), every string it
    /// supplied (registered under <see cref="SyncedOwner"/>), the values its <c>PrfD</c> filled
    /// into local definitions, its edit layers, key-table tombstones and the join session.
    /// Locally-owned <see cref="ItemDefinitionBuilder.Register"/> entries stay; they apply when
    /// the gate opens. <paramref name="forgetWorld"/> (a new client world only) also forgets the
    /// previous world's clone/key-row ledger, edit fingerprints and baselines, so this client's
    /// own items and edits apply to the new world instead of being refused as a key-index
    /// mismatch or skipped as already applied.
    /// </summary>
    private static void ResetClientJoinState(bool dropSynced, bool clearTombs, bool forgetWorld, string when)
    {
        List<Entry> dropped = new();
        List<PrefabDefinition> locals = new();
        int clearedTombs = 0;
        lock (Gate)
        {
            if (dropSynced)
            {
                for (int i = Entries.Count - 1; i >= 0; i--)
                {
                    if (!ReferenceEquals(Entries[i].Definition.Owner, SyncedOwner)) continue;
                    dropped.Add(Entries[i]);
                    Entries.RemoveAt(i);
                }
                for (int i = 0; i < dropped.Count; i++)
                    PendingFrames.Remove(dropped[i].Definition.Guid.GuidHash);
                if (PendingFrames.Count == 0)
                    StopFrameRetryNoLock();
                for (int i = 0; i < Entries.Count; i++)
                    locals.Add(Entries[i].Definition);
            }
            if (clearTombs)
            {
                clearedTombs = Tombstones.Count;
                Tombstones.Clear();
            }
            // Local registrations survive reconnect, but their applied status and retry
            // budget do not. The accepted ledger will apply them to this world's table.
            for (int i = 0; i < Entries.Count; i++)
                Entries[i].Applied = false;
            PendingFrames.Clear();
            StopFrameRetryNoLock();
            ResetJoinSessionNoLock();
        }
        // World change / rejoin: the stored refuse must not leak onto a later Core swap
        // of a healthy session. ContentMemory is the only store that would otherwise
        // carry it; Uninstall must not clear (SnapshotContent already wrote this join).
        ClearJoinSessionInMemory();

        for (int i = 0; i < dropped.Count; i++)
        {
            var entry = dropped[i];
            entry.Handle.Invalidate();
            try { entry.Definition.RunUndo(); }
            catch (Exception e)
            {
                Plugin.Logger?.LogError($"PrefabRegistry: undo synced '{entry.Definition.Id}' {when} threw: {e}");
            }
        }

        int reverted = 0;
        for (int i = 0; i < locals.Count; i++)
        {
            if (locals[i].RevertServerFill()) reverted++;
        }

        // Synced definitions register their text under SyncedOwner and deliberately record no
        // undo for it (soft unapply keeps keys a live clone points at); server text filled into a
        // local definition uses the same owner. The server is gone, so all of it goes.
        int strings = 0;
        if (dropSynced)
        {
            try { strings = Progenitor.Localization.Localization.RemoveAll(SyncedOwner); }
            catch (Exception e)
            {
                Plugin.Logger?.LogError($"PrefabRegistry: dropping server localization {when} threw: {e}");
            }
        }

        int forgottenEdits = 0;
        int forgottenClones = 0;
        if (forgetWorld)
        {
            // Every entity of the previous world is gone, so no captured baseline can be written
            // back (the stale-identity check would refuse them anyway), no recorded pass wrote the
            // new world, and no clone or key row recorded there exists here.
            forgottenEdits = Edits.ForgetWorld();
            forgottenClones = PrefabApplicator.ForgetClientWorld();
            // Same defect, recipe side: a station claim recorded in the previous world made the
            // next world's re-clone skip the WorkstationRecipesBuffer row.
            forgottenClones += RecipeApply.ForgetClientWorld();
        }
        // The server layer belonged to the previous session's server and its entities. Keep the
        // local readers (this generation's editors are unchanged); drop what was received and the
        // per-join budget charges.
        Edits.Wire.ClearReceived();
        lock (Gate)
        {
            EditLayerPending.Clear();
            EditLayerGaveUp.Clear();
        }
        if (dropped.Count > 0)
            Plugin.Logger?.LogInfo($"PrefabRegistry: dropped {dropped.Count} server-synced definition(s) {when} (local registrations kept).");
        if (clearedTombs > 0)
            Plugin.Logger?.LogInfo($"PrefabRegistry: cleared {clearedTombs} key-table tombstone(s) {when}.");
        if (reverted > 0 || strings > 0)
            Plugin.Logger?.LogInfo($"PrefabRegistry: restored {reverted} local definition(s) the server had filled and dropped {strings} server string(s) {when}.");
        if (forgottenEdits > 0 || forgottenClones > 0)
            Plugin.Logger?.LogInfo($"PrefabRegistry: forgot {forgottenClones} clone/key row(s) and {forgottenEdits} edit pass(es) of the previous client world; local items and edits apply to this one.");
    }

    private static void ApplyKeyPlaceholders(World? world)
    {
        if (!PrefabApplyGate.ShouldApplyPlaceholders(ApplyGate.Ready)) return;
        if (world == null || !world.IsCreated) return;
        KeyTombstone[] tombs;
        lock (Gate)
        {
            if (Tombstones.Count == 0) return;
            tombs = Tombstones.ToArray();
        }
        foreach (var t in tombs)
            TryAppendKeyPlaceholder(world, t.GuidHash, t.Id, t.Index);
    }

    private static void TryAppendKeyPlaceholder(World world, int guidHash, string id, int savedIndex)
    {
        try
        {
            var guid = new PrefabGUID(guidHash);
            int current = ItemKeyTable.IndexOf(world.EntityManager, guid);
            if (savedIndex >= 0)
            {
                if (PrefabApplicator.KeyRowStillHolds(savedIndex, current))
                {
                    Plugin.Logger?.LogInfo($"PrefabRegistry: key placeholder '{id}' ({guidHash}) already at index={savedIndex}");
                    return;
                }
                Plugin.Logger?.LogWarning(
                    $"PrefabRegistry: key placeholder '{id}' ({guidHash}) refused: saved index={savedIndex} now={current}; not appending at list end.");
                return;
            }
            bool ok = ItemKeyTable.TryAppend(world.EntityManager, guid, out var idx, out var why);
            Plugin.Logger?.LogInfo($"PrefabRegistry: key placeholder '{id}' ({guidHash}) append={ok} index={idx} ({why})");
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"PrefabRegistry: key placeholder '{id}' ({guidHash}) threw: {e}");
        }
    }

    private readonly struct KeyTombstone
    {
        public readonly int GuidHash;
        public readonly string Id;
        public readonly int Index;
        public KeyTombstone(int guidHash, string id, int index = -1)
        {
            GuidHash = guidHash;
            Id = id ?? "";
            Index = index;
        }
    }

    internal static bool IsRegisteredForDelivery(PrefabDefinition def) => IsLiveEntry(def);

    private static bool IsLiveEntry(PrefabDefinition def)
    {
        lock (Gate)
        {
            for (int i = 0; i < Entries.Count; i++)
            {
                if (ReferenceEquals(Entries[i].Definition, def)) return true;
            }
            return false;
        }
    }

    private static void MarkApplied(PrefabDefinition def)
    {
        lock (Gate)
        {
            for (int i = 0; i < Entries.Count; i++)
            {
                if (!ReferenceEquals(Entries[i].Definition, def)
                    && Entries[i].Definition.Guid.GuidHash != def.Guid.GuidHash)
                    continue;
                Entries[i].Applied = true;
                break;
            }
        }
        // Successful apply (insert or in-place update) owns the GUID for the snapshot,
        // including buffs that will never occupy a key-table tombstone.
        PrefabApplicator.RememberClone(def.Guid);
    }

    private static int[] AppliedHashesNoLock()
    {
        int n = 0;
        for (int i = 0; i < Entries.Count; i++)
        {
            if (Entries[i].Applied) n++;
        }
        if (n == 0) return Array.Empty<int>();
        var hashes = new int[n];
        int w = 0;
        for (int i = 0; i < Entries.Count; i++)
        {
            if (!Entries[i].Applied) continue;
            hashes[w++] = Entries[i].Definition.Guid.GuidHash;
        }
        return hashes;
    }

    /// <summary>
    /// Non-pending apply failure of an entry that never landed: drop it so it is not sent as
    /// <c>PrfD</c> and is not tombstoned. A clone that already applied stays (a later retry
    /// glitch must not un-register a live item).
    /// </summary>
    private static void DropFailedEntry(PrefabDefinition def, string report)
    {
        Entry? removed = null;
        bool alreadyApplied = false;
        lock (Gate)
        {
            for (int i = Entries.Count - 1; i >= 0; i--)
            {
                var entry = Entries[i];
                if (!ReferenceEquals(entry.Definition, def)
                    && entry.Definition.Guid.GuidHash != def.Guid.GuidHash)
                    continue;
                if (entry.Applied)
                {
                    alreadyApplied = true;
                    break;
                }
                removed = entry;
                Entries.RemoveAt(i);
                break;
            }
            PendingFrames.Remove(def.Guid.GuidHash);
            if (PendingFrames.Count == 0)
                StopFrameRetryNoLock();
        }
        if (alreadyApplied)
        {
            if (PrefabApplyBookkeeping.IsFatalIdentityRefuse(report))
                Plugin.Logger?.LogError(report);
            else
                Plugin.Logger?.LogWarning($"PrefabRegistry: apply '{def.Id}' failed: {report}");
            return;
        }
        if (removed == null) return;
        removed.Handle.Invalidate();
        if (PrefabApplyBookkeeping.IsFatalIdentityRefuse(report))
            Plugin.Logger?.LogError(report);
        else
            Plugin.Logger?.LogWarning(
                $"PrefabRegistry: dropped '{def.Id}' ({def.Guid.GuidHash}) after non-pending apply failure (not sent as PrfD, not tombstoned): {report}");
    }

    private sealed class Entry
    {
        public readonly PrefabDefinition Definition;
        public readonly RegistrationHandle Handle;
        /// <summary>True after a successful <see cref="PrefabApplicator.TryApply"/> this process.</summary>
        public bool Applied;
        public Entry(PrefabDefinition definition, RegistrationHandle handle)
        {
            Definition = definition;
            Handle = handle;
        }
    }

    internal sealed class RegistrationHandle : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            Unregister(this);
        }

        internal void Invalidate() => Interlocked.Exchange(ref _disposed, 1);
    }

    private sealed class SyncedOwnerTag
    {
        public override string ToString() => "server sync";
    }
}
