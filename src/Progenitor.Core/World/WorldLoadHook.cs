using System;
using System.Reflection;
using HarmonyLib;
using ProjectM;
using Unity.Entities;
using Progenitor.Events;
using Progenitor.Patches;

namespace Progenitor.WorldState;

/// <summary>
/// The game-touching half of <see cref="WorldEpoch"/> (job FEPOCH): a Core-owned Harmony
/// postfix that moves the epoch once per completed world load — and, in the client process where
/// that method does not exist, the watch on the client world's own lifetime that moves it once per
/// world creation and once per world ending (job FEPOCH2, R33 finding 5).
///
/// <para><b>The seam.</b>
/// <c>ProjectM.LoadPersistenceSystemV2.CopyDataFromPrefabs(DeserializationJobData jobData)</c> —
/// a server method. It is step 16 of the eighteen call sites
/// inside the game's native <c>LoadPersistenceSystemV2.OnUpdate</c>, and it is the
/// <b>single-shot</b> one: <c>DeserializeLoadFile</c> (step 9) creates the
/// entities but runs once per entry in <c>RawPersistentSaveData.ArchetypeSerializationFiles</c>,
/// several times per load, while <c>CopyDataFromPrefabs</c> runs exactly once,
/// after all of them. Every entity of the new world exists by the time the postfix runs, so an
/// <c>Advance</c> there is "the world just got rebuilt", once, with no de-duplication.</para>
///
/// <para><b>Postfix, not prefix.</b> A prefix would move the epoch before the load could fail;
/// Harmony skips a postfix when the original throws, which is the failed load Bootstrap's
/// <c>SaveSafety</c> guard is built around (it destroys in <i>its</i> postfix on this same
/// method for exactly that reason). A load that threw left the world
/// broken, not reloaded, and the epoch stays where it was.</para>
///
/// <para><b>Why a patch and not a system hook.</b> <see cref="Progenitor.Hooks.SystemHooks"/>
/// can target <c>LoadPersistenceSystemV2</c> — but its unit of work is the system's whole
/// update, and <c>LoadPersistenceSystemV2.OnUpdate</c> runs every frame in
/// <c>PersistenceGroup</c> and does nothing on the frames with no load
/// pending. An <c>After</c> hook would therefore fire ~60 times a second and would still need a
/// load-state comparison to tell a load from a no-op tick. The single-shot inner method is both
/// cheaper and unambiguous.</para>
///
/// <para><b>Why the registry, and why through a slot</b> (job FHARM6, R40 finding 1). The
/// behavior lives in this collectible Core ALC, and handing HarmonyX a Core
/// <c>MethodInfo</c> pins this Core generation <em>for the life of the process</em> — the
/// unpatch does not undo it. This hook is not opt-in and
/// not a debug guard: <c>Install</c> runs on every server that reaches a world load, so the
/// direct path made every <c>.corereload</c> leak a whole generation, silently, with
/// <c>patches: Uninstall removed=1 left=0</c> reading clean. It therefore registers through the
/// same <c>Progenitor.Bootstrap.PatchSlots</c> pool plugin patches use: Harmony is handed a
/// Bootstrap static, the postfix below is reached through a delegate the slot drops on release,
/// and the generation collects. The postfix needs neither <c>__result</c> nor any argument — it
/// bumps a counter — so the slot's signature-agnostic shape costs nothing here.</para>
///
/// <para>Registering under Core's own owner id (<see cref="OwnerId"/>) means
/// <c>Patches.Uninstall()</c> — which <c>Plugin.Uninstall</c> already runs — takes it off with
/// everything else.</para>
///
/// <para><b>Fallback if even the slot cannot bind (described, not implemented).</b> Bootstrap already
/// prefixes and postfixes this exact method for the save-safety guard
/// (<c>HarmonyPatches.SaveSafetyHook</c>, slot <c>HarmonySlotPolicy.CopyDataFromPrefabs</c>).
/// The Bootstrap-slot pattern would add an <c>Action?</c> slot next to it — Bootstrap's
/// non-collectible <c>SaveSafetyHook.Postfix</c> invoking a delegate Core fills on Install and
/// nulls on Uninstall, the way <see cref="Progenitor.HarmonySlotHandoff"/> does for Core's other
/// long-lived sites. That is strictly more robust (it survives a Core generation with no Harmony
/// instance at all, and it cannot double-patch), and it is the fix to reach for if this
/// registration is ever refused; it is not implemented here because it needs a Bootstrap change,
/// Bootstrap is the one assembly that cannot be hot-swapped, and the registry binds today.</para>
/// </summary>
internal static class WorldLoadHook
{
    /// <summary>
    /// Owner id every world-epoch patch is tagged with: Core's own generation. Shared with god
    /// mode and any other Core-owned registry patch (job FHARM6) — they have one lifetime and
    /// one sweep, so they get one id, and <c>patches:</c> lines for Core all grep together.
    /// </summary>
    internal const string OwnerId = MyPluginInfo.PLUGIN_GUID + PatchSitePolicy.CoreOwnerSuffix;

    /// <summary>What <see cref="WorldEpoch.Advance"/> records as the source, and the log's <c>source=</c>.</summary>
    internal const string Source = "LoadPersistenceSystemV2.CopyDataFromPrefabs";

    private static readonly object Gate = new();
    private static bool _installed;
    private static PatchHandle? _handle;
    private static object? _clientOwner;

    /// <summary>
    /// The client <c>World</c> the last scan found, held so the steady-state frame does not scan
    /// (R34 finding 16), and seeded by <see cref="OnWorldReady"/> so a creation the event reports
    /// does not have to be re-found by a walk. Only ever touched on the game main thread: from
    /// <see cref="ClientTick"/> (a <c>Plugin.Frame</c> handler) and from <see cref="OnWorldReady"/>,
    /// which <c>EventHooks.WatchWorlds</c> publishes from its own per-frame tick.
    /// </summary>
    private static World? _cachedClient;

    /// <summary>True while the postfix is applied.</summary>
    internal static bool Patched
    {
        get { lock (Gate) return _handle != null; }
    }

    /// <summary>
    /// Apply the epoch seam for this process. The server gets the postfix below. The client never
    /// runs <c>LoadPersistenceSystemV2</c>, but its <c>World</c> is rebuilt on every reconnect,
    /// which invalidates retained ids exactly the same way, so it gets
    /// <see cref="InstallClientWatch"/> instead (R33 finding 5). Idempotent; never throws.
    /// </summary>
    internal static void Install()
    {
        lock (Gate)
        {
            if (_installed) return;
            _installed = true;
        }

        if (!Plugin.IsServerProcess)
        {
            InstallClientWatch();
            return;
        }

        MethodBase? target = null;
        try
        {
            target = AccessTools.Method(
                typeof(LoadPersistenceSystemV2), nameof(LoadPersistenceSystemV2.CopyDataFromPrefabs));
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("world: resolving LoadPersistenceSystemV2.CopyDataFromPrefabs threw: " + e.Message);
        }

        if (target == null)
        {
            Plugin.Logger?.LogWarning(
                "world: ProjectM.LoadPersistenceSystemV2::CopyDataFromPrefabs was not "
                + "found; the world load epoch will never move, so Castles handles and god-mode guards fall back to "
                + "their liveness checks alone. See WorldLoadHook for the Bootstrap-slot fallback.");
            return;
        }

        Patches.Patches.Install();
        // Slot-routed, not direct (job FHARM6). The lambda's target is a Core object, which the
        // slot drops the moment it is released; the MethodInfo HarmonyX keeps forever belongs to
        // Progenitor.Bootstrap. Nothing of this generation reaches the append-only caches.
        var handle = PatchRegistry.PostfixSlot(
            owner: null,
            ownerId: OwnerId,
            target: target,
            postfix: static (_, _) => LoadPostfix(),
            priority: Priority.Last);

        lock (Gate) _handle = handle;

        if (handle == null)
        {
            Plugin.Logger?.LogWarning(
                "world: the load-epoch postfix was refused or threw during apply (an exhausted or "
                + "unavailable slot pool is one reason: see the patches: refused line above); the "
                + "epoch will never move. See WorldLoadHook for the typed Bootstrap-site fallback.");
            return;
        }

        Plugin.Logger?.LogInfo(
            "world: load epoch armed on " + Source + "; current epoch "
            + WorldEpoch.Current + ". " + WorldEpoch.DescribeGeneration());
    }

    /// <summary>
    /// The client seam: the client world's own lifetime. Two observations feed one decision
    /// (<see cref="ClientWorldEpoch.Note"/>, which de-duplicates on <c>World.SequenceNumber</c>):
    /// the <see cref="WorldReady"/> publication Core already makes for every world creation
    /// including client rejoins (<c>EventHooks.WatchWorlds</c>) — which is the moment a client world
    /// appears, so it carries every creation within one frame — and a poll of the world list, which
    /// is needed because nothing publishes a world <i>ending</i> and a disconnect must invalidate ids
    /// at the disconnect, not at the next join. The poll is per-frame off the cached wrapper while
    /// connected and bounded to one walk in
    /// <see cref="ClientWorldScanPolicy.UncachedScanIntervalFrames"/> frames while there is no client
    /// world (job FEPOCH5, R36 finding 4). Never throws.
    /// </summary>
    private static void InstallClientWatch()
    {
        var owner = new object();
        try { EventBus.On<WorldReady>(OnWorldReady, owner: owner); }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("world: subscribing to WorldReady on the client threw: " + e.Message);
        }

        Plugin.Frame += ClientTick;
        lock (Gate) _clientOwner = owner;

        Plugin.Logger?.LogInfo(
            "world: load epoch armed on the client world lifetime (" + Worlds.ClientName
            + " created/ended; LoadPersistenceSystemV2 is server-side and never runs here); "
            + "current epoch " + WorldEpoch.Current + ". " + WorldEpoch.DescribeGeneration());

        ClientTick();
    }

    /// <summary>
    /// The per-frame client check. It does <b>not</b> call <c>Worlds.Client</c> per frame any more
    /// (R34 finding 16): the world the last scan found is cached, so a connected frame — nearly
    /// every frame of a session — reads <c>IsCreated</c> and <c>SequenceNumber</c> off that one
    /// wrapper and never walks the list. A frame with <i>no</i> cached world still walks, because a
    /// count cannot tell "the same worlds" from "one destroyed and another created" (job FEPOCH4,
    /// R35 finding 7) — but it walks at a bounded rate, one frame in
    /// <see cref="ClientWorldScanPolicy.UncachedScanIntervalFrames"/>, because the uncached state is
    /// the whole main menu and loading screen and the per-frame walk there was R36 finding 4. The
    /// frame a cached world is <i>lost</i> is never deferred (it has an ending to report), the
    /// creation the interval could defer is caught in one frame by <see cref="OnWorldReady"/>, and
    /// an empty <c>World.s_AllWorlds</c> with nothing cached is skipped outright.
    /// <see cref="ClientWorldScanPolicy"/> holds the rule, the cases it covers and the per-frame
    /// cost in each state; <see cref="FindClientWorld"/> is the walk that no longer marshals a
    /// <c>Name</c> string per world.
    ///
    /// <para>A throw here is not treated as "the world ended" — that would move the epoch on a
    /// transient failure — so it returns without noting anything, and drops the cache so the next
    /// frame re-scans rather than trusting a wrapper that just threw.</para>
    /// </summary>
    private static void ClientTick()
    {
        ulong seq;
        try
        {
            var cached = _cachedClient;
            bool cachedIsLive = cached != null && cached.IsCreated;
            if (cachedIsLive)
            {
                seq = cached!.SequenceNumber;
            }
            else
            {
                bool cachedLost = cached != null;
                _cachedClient = null;

                var all = World.s_AllWorlds;
                int count = all == null ? 0 : all.Count;

                if (!ClientWorldEpoch.ScanPolicy.ShouldScan(cachedIsLive: false, cachedLost, count)) return;

                var world = FindClientWorld();
                bool live = world != null;
                _cachedClient = world;
                seq = live ? world!.SequenceNumber : 0UL;
            }
        }
        catch (Exception e)
        {
            _cachedClient = null;
            // The wrapper that threw is no longer trustworthy, so the next frame must look rather
            // than wait out the bounded uncached interval.
            try { ClientWorldEpoch.ScanPolicy.ScanSoon(); } catch { }
            try { Plugin.Logger?.LogWarning("world: reading the client world threw: " + e.Message); } catch { }
            return;
        }

        try { ClientWorldEpoch.Note(seq); }
        catch (Exception e)
        {
            try { Plugin.Logger?.LogWarning("world: client epoch tick threw: " + e.Message); } catch { }
        }
    }

    /// <summary>
    /// <c>Worlds.Client</c>'s walk, minus the Il2Cpp <c>Name</c> marshal per world (job FEPOCH5,
    /// R36 finding 4). <c>Worlds.Find</c> reads <c>Name</c> — an Il2Cpp string property, one managed
    /// allocation per entry — for every world on every walk. A <c>World.SequenceNumber</c> is
    /// assigned once and never reused, so a world already classified as "not <c>Client_0</c>" can be
    /// skipped on the cheap <c>ulong</c> alone; <c>Name</c> is read only for a sequence number the
    /// filter has never seen. After the first walk of a stable list, later walks marshal no strings
    /// at all. <see cref="ClientWorldNameFilter"/> holds the reasoning and counts the reads.
    ///
    /// <para>Returns only a live world, exactly like <c>Worlds.Find</c>: a null result means "no
    /// client world right now", which is the ending the epoch must report.</para>
    /// </summary>
    private static World? FindClientWorld()
    {
        var all = World.s_AllWorlds;
        if (all == null) return null;

        var filter = ClientWorldEpoch.NameFilter;
        for (int i = 0; i < all.Count; i++)
        {
            var world = all[i];
            if (world == null || !world.IsCreated) continue;

            ulong seq = world.SequenceNumber;
            if (!filter.NeedsNameRead(seq)) continue;

            if (world.Name == Worlds.ClientName)
            {
                filter.RecordClient(seq);
                return world;
            }

            filter.RecordNotClient(seq);
        }

        return null;
    }

    /// <summary>
    /// The fast path: the same decision, taken on the frame the world appears, from the
    /// <c>WorldReady</c> publication Core already makes for every world creation including client
    /// rejoins. It also seeds <see cref="_cachedClient"/> and clears the uncached interval (job
    /// FEPOCH5, R36 finding 4) — so a creation is never waiting on the bounded poll: the event
    /// catches it in one frame, and the poll is only the backstop for a creation the event missed
    /// (and for the world <i>ending</i>, which nothing publishes).
    /// </summary>
    private static void OnWorldReady(WorldReady ready)
    {
        try
        {
            if (ready == null || ready.IsServer) return;
            var world = ready.World;
            if (world == null || !world.IsCreated) return;
            _cachedClient = world;
            ClientWorldEpoch.NameFilter.RecordClient(world.SequenceNumber);
            ClientWorldEpoch.ScanPolicy.ScanSoon();
            ClientWorldEpoch.Note(world.SequenceNumber);
        }
        catch (Exception e)
        {
            try { Plugin.Logger?.LogWarning("world: client WorldReady epoch note threw: " + e.Message); } catch { }
        }
    }

    /// <summary>
    /// Unpatch, and drop the client watcher's <c>Plugin.Frame</c> handler and bus subscription —
    /// both of which would otherwise pin this collectible generation. Called from
    /// <c>Plugin.Uninstall</c> before the ALC unloads.
    ///
    /// <para>The counter itself is <b>not</b> reset: a handle retained across a Core swap must
    /// still see the loads that happened while the old generation owned the hook. Note that the
    /// counter is Core-generation-lifetime, not process-lifetime (R33 finding 13) — the new
    /// generation starts over with a new nonce, which is what makes an old stamp stale by
    /// construction. <see cref="WorldEpoch"/> has the reasoning.</para>
    /// </summary>
    internal static void Uninstall()
    {
        PatchHandle? handle;
        object? clientOwner;
        lock (Gate)
        {
            handle = _handle;
            _handle = null;
            clientOwner = _clientOwner;
            _clientOwner = null;
            _installed = false;
        }
        try { handle?.Dispose(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("world: unpatch load epoch: " + e.Message); }

        if (clientOwner == null) return;
        Plugin.Frame -= ClientTick;
        _cachedClient = null;
        try { EventBus.RemoveAll(clientOwner); }
        catch (Exception e) { Plugin.Logger?.LogWarning("world: dropping the client epoch watch: " + e.Message); }
        ClientWorldEpoch.Forget();
    }

    /// <summary>
    /// Postfix on <c>CopyDataFromPrefabs</c>. Runs on the game main thread inside the load, so it
    /// does one interlocked increment, one log line and one event publish. Never throws — an
    /// exception here would surface as a load failure.
    /// </summary>
    private static void LoadPostfix()
    {
        try { WorldEpoch.Advance(Source); }
        catch (Exception e)
        {
            try { Plugin.Logger?.LogWarning("world: load-epoch postfix threw: " + e.Message); } catch { }
        }
    }
}
