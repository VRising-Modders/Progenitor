using System;

namespace Progenitor.Content;

/// <summary>
/// Game-free apply-gate for cloned-item registration. The registrar and the unit
/// tests share this so WorldReady / first packet / PlayerConnected / give-up cannot drift.
///
/// Live-verified (2026-09-02): the client's <c>ItemKeyLookup</c> is still being built at
/// <c>WorldReady</c> (1077 of a final 1084 rows). A row appended then lands at a different index
/// than the server's once the table finishes (client 659 vs server 1083 → a Hermit's Shawl in the
/// Ember Shard slot). The server sends <c>PrfD</c> / <c>PrfK</c> from its <c>OnUserConnected</c>
/// postfix, after the table is complete, so that packet is the client's "table is done" signal.
/// A new client world (rejoin) must close the gate again and drop last-join synced state.
/// A mid-session Core swap uses <see cref="NotifyLiveWorldAfterCoreSwap"/> because there is
/// no new deserialize and no new join packet. That call also marks the next client
/// <see cref="NotifyWorldReady"/> as a republish of the same world (EventHooks clears
/// ReadyWorlds on Uninstall), so it must not close the gate or drop the restored join.
///
/// Server: <c>DeserializeLoadFile</c> is the authoritative seam (unknown GUIDs are dropped on
/// load). <c>WorldReady</c> does not open the gate — the load takes many frames after it.
/// <c>PlayerConnected</c> opens and applies before sending, so a joiner never receives a
/// definition that is not yet in the server table. The 30 s timer is only a no-player fallback
/// for brand-new worlds that never load a file.
///
/// Client-only items (a local <c>Register</c> with no server packet, including single-player or a
/// server without the mod) never open this gate. The frame retry burns the give-up budget even
/// while closed, logs once, and stops. They require a <c>PrfD</c> or <c>PrfK</c> from the server.
/// </summary>
internal sealed class PrefabApplyGate
{
    /// <summary>Process role. One gate instance lives for the process lifetime.</summary>
    public enum Side
    {
        Client,
        Server,
    }

    /// <summary>Custom prefab packet that may open the client gate. All three kinds are the signal.</summary>
    public enum ClientPacket
    {
        /// <summary><c>PrfD</c> definition.</summary>
        Definition,
        /// <summary><c>PrfK</c> key-table placeholder.</summary>
        KeyPlaceholder,
        /// <summary><c>PrfI</c> server key-table order. Opens the gate but does not dump pending
        /// registrations until the client has rebuilt in that order.</summary>
        IndexLedger,
    }

    /// <summary>
    /// Seconds after server <c>WorldReady</c> to open the gate when no save load and no player
    /// has done it. Brand-new worlds never run <c>DeserializeLoadFile</c>.
    /// </summary>
    public const double NoPlayerFallbackSeconds = 30.0;

    private readonly Side _side;
    /// <summary>
    /// Written from main-thread hooks (WorldReady, packets, connect, load prefix, fallback).
    /// Volatile so a read on the apply path cannot see a stale close after a packet opened it.
    /// No lock: a lock here nested under the registrar's Entries lock would invert with
    /// WorldReady (gate then Entries).
    /// </summary>
    private volatile bool _ready;
    /// <summary>
    /// Client only. Set when <see cref="NotifyLiveWorldAfterCoreSwap"/> opens (or keeps
    /// open) the gate for a world that already exists. The next
    /// <see cref="NotifyWorldReady"/> is EventHooks republishing that same world
    /// (ReadyWorlds is generation-scoped and Uninstall clears it), not a rejoin.
    /// Consumed by that WorldReady so it cannot close the gate or drop the restored
    /// join session. A later genuine new client world still resets.
    /// </summary>
    private bool _openedForLiveWorldSwap;

    public PrefabApplyGate(Side side)
    {
        _side = side;
    }

    /// <summary>Which process this gate belongs to.</summary>
    public Side ProcessSide => _side;

    /// <summary>
    /// True when a definition apply and key-table placeholder appends may run. False:
    /// definitions are only queued.
    /// </summary>
    public bool Ready => _ready;

    /// <summary>
    /// Client burns the give-up budget while waiting for a packet that may never come. Server
    /// does not: burning it between <c>WorldReady</c> and <c>DeserializeLoadFile</c> was the R3
    /// failure. Frame retry therefore runs while closed only on the client.
    /// </summary>
    public bool ShouldRunFrameRetry => _ready || _side == Side.Client;

    /// <summary>
    /// Client <c>WorldReady</c> (including rejoin): close the gate, drop every <c>SyncedOwner</c>
    /// entry, clear tombstones, keep locally-owned <c>Register</c>s, and do not append
    /// placeholders — the table is still being built. Server: leave the gate closed, do not
    /// append, schedule the no-player fallback. Both sides may <c>NotePending</c> local
    /// definitions so the client give-up timer starts.
    /// </summary>
    public WorldReadyPlan NotifyWorldReady()
    {
        if (_side == Side.Client)
        {
            if (_openedForLiveWorldSwap)
            {
                _openedForLiveWorldSwap = false;
                return new WorldReadyPlan(
                    closeGate: false,
                    dropSyncedOwnerEntries: false,
                    clearTombstones: false,
                    applyPending: true,
                    applyPlaceholders: false,
                    scheduleNoPlayerFallback: false);
            }
            _ready = false;
            return new WorldReadyPlan(
                closeGate: true,
                dropSyncedOwnerEntries: true,
                clearTombstones: true,
                applyPending: true,
                applyPlaceholders: false,
                scheduleNoPlayerFallback: false,
                forgetWorldBookkeeping: true);
        }

        return new WorldReadyPlan(
            closeGate: false,
            dropSyncedOwnerEntries: false,
            clearTombstones: false,
            applyPending: true,
            applyPlaceholders: false,
            scheduleNoPlayerFallback: true);
    }

    /// <summary>
    /// First <c>PrfI</c>, <c>PrfD</c> or <c>PrfK</c> of a client join. Opens the gate.
    /// <c>PrfD</c>/<c>PrfK</c> also dump queued local definitions before this packet, but
    /// the registrar refuses custom-item appends when no <c>PrfI</c> was accepted
    /// (<see cref="KeyJoinSession"/>) — dropping the ledger is not a safe local-order apply.
    /// <c>PrfI</c> does not dump: the registrar rebuilds in the server order first. Further
    /// packets of the same join do not re-run the pending dump. No-op on the server.
    /// </summary>
    public PacketPlan NotifyClientPacket(ClientPacket packet)
    {
        if (_side != Side.Client)
            return PacketPlan.None;
        if (packet != ClientPacket.Definition
            && packet != ClientPacket.KeyPlaceholder
            && packet != ClientPacket.IndexLedger)
            return PacketPlan.None;

        bool opened = !_ready;
        _ready = true;
        bool dumpPending = opened && packet != ClientPacket.IndexLedger;
        return new PacketPlan(
            openedGate: opened,
            applyPendingFirst: dumpPending,
            handleThisPacket: true);
    }

    /// <summary>
    /// Client: the connection to the server ended (kick, walk to the menu, timeout, or
    /// <c>Client_0</c> destroyed). Everything that server sent is scoped to that connection, so
    /// the gate closes — nothing may apply until the next join's first packet — and a pending
    /// Core-swap republish mark is dropped, so the next <see cref="NotifyWorldReady"/> is always
    /// treated as a new world. True when the registrar must drop the server-sourced state now.
    /// World-scoped bookkeeping is <b>not</b> dropped here: <c>Client_0</c> can outlive the
    /// connection (FMAN17), and its clones still exist until a new world replaces it.
    /// Server: no-op, false.
    /// </summary>
    public bool NotifyClientConnectionEnded()
    {
        if (_side != Side.Client) return false;
        _ready = false;
        _openedForLiveWorldSwap = false;
        return true;
    }

    /// <summary>
    /// A user is connecting. Open the gate and apply before any send so the packet the client
    /// treats as "table is complete" describes rows this process has actually appended. Skip
    /// <c>PrfD</c> for entries that are still unapplied. No-op on the client.
    /// </summary>
    public ConnectPlan NotifyPlayerConnected()
    {
        if (_side != Side.Server)
            return ConnectPlan.None;

        _ready = true;
        return new ConnectPlan(
            openGate: true,
            applyPendingBeforeSend: true,
            applyPlaceholdersBeforeSend: true,
            sendOnlyApplied: true);
    }

    /// <summary>
    /// <c>DeserializeLoadFile</c> prefix: prefabs are loaded; inventories about to deserialize
    /// need our GUIDs in the maps. Opens and applies. No-op on the client.
    /// </summary>
    public LoadPrefixPlan NotifyLoadPrefix()
    {
        if (_side != Side.Server)
            return LoadPrefixPlan.None;

        bool opened = !_ready;
        _ready = true;
        return new LoadPrefixPlan(
            opened: opened,
            applyPending: true,
            applyPlaceholders: true);
    }

    /// <summary>
    /// Mid-session Core swap: the ECS world already exists, there is no new deserialize and
    /// no new join packet. Open the gate on both sides so local re-Registers apply immediately
    /// instead of the server waiting 30 s and the client waiting forever for a <c>PrfD</c>
    /// that will not arrive. Idempotent when already open.
    /// </summary>
    public LiveWorldPlan NotifyLiveWorldAfterCoreSwap()
    {
        bool opened = !_ready;
        _ready = true;
        if (_side == Side.Client)
            _openedForLiveWorldSwap = true;
        return new LiveWorldPlan(
            opened: opened,
            applyPending: true,
            applyPlaceholders: true);
    }

    /// <summary>
    /// 30 s after server <c>WorldReady</c> with no prefix and no player. Opens only when still
    /// closed. No-op if a load or a connect already opened, and no-op on the client.
    /// </summary>
    public FallbackPlan NotifyNoPlayerFallback()
    {
        if (_side != Side.Server || _ready)
            return FallbackPlan.None;

        _ready = true;
        return new FallbackPlan(
            opened: true,
            applyPending: true,
            applyPlaceholders: true);
    }

    /// <summary>
    /// One pending-apply frame. Client while closed: count toward give-up, never apply (a
    /// client-only item must not land before the server packet, and must not retry forever).
    /// Server while closed: do not count (the load is still coming). Either side while open:
    /// count, retry until the budget, then give up.
    /// </summary>
    public FrameStep StepFrame(int currentFrames, int budget)
    {
        if (budget < 1)
            throw new ArgumentOutOfRangeException(nameof(budget), "Give-up budget must be at least 1.");
        if (currentFrames < 0)
            throw new ArgumentOutOfRangeException(nameof(currentFrames), "Frame count cannot be negative.");

        if (_side == Side.Client && !_ready)
        {
            int next = currentFrames + 1;
            bool giveUp = next >= budget;
            return new FrameStep(
                countThisFrame: true,
                framesAfter: next,
                giveUp: giveUp,
                retryApply: false,
                giveUpBecauseNoServerPacket: giveUp);
        }

        if (!_ready)
        {
            return new FrameStep(
                countThisFrame: false,
                framesAfter: currentFrames,
                giveUp: false,
                retryApply: false,
                giveUpBecauseNoServerPacket: false);
        }

        int after = currentFrames + 1;
        bool reached = after >= budget;
        return new FrameStep(
            countThisFrame: true,
            framesAfter: after,
            giveUp: reached,
            retryApply: !reached,
            giveUpBecauseNoServerPacket: false);
    }

    /// <summary>
    /// Never send <c>PrfD</c> for an entry that has not landed in this process's lookups. The
    /// client treats the first packet as "table is complete" and would append a row the server
    /// does not have.
    /// </summary>
    public static bool ShouldSendDefinition(bool applied) => applied;

    /// <summary>
    /// Key-table placeholder appends are the same class of write as a definition apply: they
    /// must not run while the game is still building <c>ItemKeyLookup</c>.
    /// </summary>
    public static bool ShouldApplyPlaceholders(bool gateReady) => gateReady;

    /// <summary>Reached the give-up budget this frame (after incrementing).</summary>
    public static bool ReachedGiveUp(int framesAfterIncrement, int budget) =>
        framesAfterIncrement >= budget;

    /// <summary>What <see cref="NotifyWorldReady"/> tells the registrar to do.</summary>
    internal readonly struct WorldReadyPlan
    {
        public readonly bool CloseGate;
        public readonly bool DropSyncedOwnerEntries;
        public readonly bool ClearTombstones;
        public readonly bool ApplyPending;
        public readonly bool ApplyPlaceholders;
        public readonly bool ScheduleNoPlayerFallback;
        /// <summary>
        /// The world every clone, key row, edit fingerprint and baseline was recorded in is gone:
        /// forget that world-scoped bookkeeping before anything applies to the new one. Client
        /// only, never for the republish that follows a Core swap.
        /// </summary>
        public readonly bool ForgetWorldBookkeeping;

        public WorldReadyPlan(
            bool closeGate,
            bool dropSyncedOwnerEntries,
            bool clearTombstones,
            bool applyPending,
            bool applyPlaceholders,
            bool scheduleNoPlayerFallback,
            bool forgetWorldBookkeeping = false)
        {
            ForgetWorldBookkeeping = forgetWorldBookkeeping;
            CloseGate = closeGate;
            DropSyncedOwnerEntries = dropSyncedOwnerEntries;
            ClearTombstones = clearTombstones;
            ApplyPending = applyPending;
            ApplyPlaceholders = applyPlaceholders;
            ScheduleNoPlayerFallback = scheduleNoPlayerFallback;
        }
    }

    /// <summary>What <see cref="NotifyClientPacket"/> tells the registrar to do.</summary>
    internal readonly struct PacketPlan
    {
        public static readonly PacketPlan None = new(false, false, false);

        public readonly bool OpenedGate;
        public readonly bool ApplyPendingFirst;
        public readonly bool HandleThisPacket;

        public PacketPlan(bool openedGate, bool applyPendingFirst, bool handleThisPacket)
        {
            OpenedGate = openedGate;
            ApplyPendingFirst = applyPendingFirst;
            HandleThisPacket = handleThisPacket;
        }
    }

    /// <summary>What <see cref="NotifyPlayerConnected"/> tells the registrar to do.</summary>
    internal readonly struct ConnectPlan
    {
        public static readonly ConnectPlan None = new(false, false, false, false);

        public readonly bool OpenGate;
        public readonly bool ApplyPendingBeforeSend;
        public readonly bool ApplyPlaceholdersBeforeSend;
        public readonly bool SendOnlyApplied;

        public ConnectPlan(
            bool openGate,
            bool applyPendingBeforeSend,
            bool applyPlaceholdersBeforeSend,
            bool sendOnlyApplied)
        {
            OpenGate = openGate;
            ApplyPendingBeforeSend = applyPendingBeforeSend;
            ApplyPlaceholdersBeforeSend = applyPlaceholdersBeforeSend;
            SendOnlyApplied = sendOnlyApplied;
        }
    }

    /// <summary>What <see cref="NotifyLoadPrefix"/> tells the registrar to do.</summary>
    internal readonly struct LoadPrefixPlan
    {
        public static readonly LoadPrefixPlan None = new(false, false, false);

        public readonly bool Opened;
        public readonly bool ApplyPending;
        public readonly bool ApplyPlaceholders;

        public LoadPrefixPlan(bool opened, bool applyPending, bool applyPlaceholders)
        {
            Opened = opened;
            ApplyPending = applyPending;
            ApplyPlaceholders = applyPlaceholders;
        }
    }

    /// <summary>What <see cref="NotifyNoPlayerFallback"/> tells the registrar to do.</summary>
    internal readonly struct FallbackPlan
    {
        public static readonly FallbackPlan None = new(false, false, false);

        public readonly bool Opened;
        public readonly bool ApplyPending;
        public readonly bool ApplyPlaceholders;

        public FallbackPlan(bool opened, bool applyPending, bool applyPlaceholders)
        {
            Opened = opened;
            ApplyPending = applyPending;
            ApplyPlaceholders = applyPlaceholders;
        }
    }

    /// <summary>What <see cref="NotifyLiveWorldAfterCoreSwap"/> tells the registrar to do.</summary>
    internal readonly struct LiveWorldPlan
    {
        public readonly bool Opened;
        public readonly bool ApplyPending;
        public readonly bool ApplyPlaceholders;

        public LiveWorldPlan(bool opened, bool applyPending, bool applyPlaceholders)
        {
            Opened = opened;
            ApplyPending = applyPending;
            ApplyPlaceholders = applyPlaceholders;
        }
    }

    /// <summary>One <c>Plugin.Frame</c> against a single pending GUID.</summary>
    internal readonly struct FrameStep
    {
        public readonly bool CountThisFrame;
        public readonly int FramesAfter;
        public readonly bool GiveUp;
        public readonly bool RetryApply;
        public readonly bool GiveUpBecauseNoServerPacket;

        public FrameStep(
            bool countThisFrame,
            int framesAfter,
            bool giveUp,
            bool retryApply,
            bool giveUpBecauseNoServerPacket)
        {
            CountThisFrame = countThisFrame;
            FramesAfter = framesAfter;
            GiveUp = giveUp;
            RetryApply = retryApply;
            GiveUpBecauseNoServerPacket = giveUpBecauseNoServerPacket;
        }
    }
}
