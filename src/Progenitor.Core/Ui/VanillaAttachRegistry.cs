using System;
using System.Collections.Generic;

namespace Progenitor.Ui;

/// <summary>
/// Every live vanilla attachment, and the one per-site frame watch each of them shares.
///
/// <para><b>Why this exists at all.</b> Every other GameObject the Ui module creates lives under
/// a DontDestroyOnLoad root named <c>progenitor-ui-&lt;pluginId&gt;</c>, so plugin unload can
/// destroy it by exact name and Core Uninstall can sweep the prefix. An attachment container is
/// parented into the <i>vanilla</i> tree — that is the point of it — so neither sweep can see it.
/// Without this table an attachment survives a plugin unload and a Core swap as a frozen
/// GameObject with a dead text writer: visible, wrong, and unownable by the next generation.
/// This is the single new hole the capability opens and the table is the whole of the fix
/// (research <c>war-events.md</c> §4.4).</para>
///
/// <para><b>The watch is a pin too.</b> The per-site refresh is a <c>SystemHooks</c> subscription
/// holding a Core-owned delegate, which pins the collectible Core generation exactly the way a
/// Harmony trampoline did before the Step D breakthrough (evidence 52). It is installed lazily on
/// the first attachment for a site and disposed when the last one goes, so a client with no
/// attaching plugin pays nothing per frame — and it is reported by
/// <see cref="Describe"/> so "who holds this generation" has an answer without a process
/// dump.</para>
/// </summary>
internal static class VanillaAttachRegistry
{
    private static readonly object Gate = new();
    private static readonly List<VanillaAttachHandle> Live = new();
    private static readonly Dictionary<VanillaUiSite, IDisposable> Watches = new();

    /// <summary>
    /// How a site's watch is built, remembered from the first <see cref="Add"/> that offered one.
    /// <see cref="RepairLostWatches"/> needs it: the one case it has to repair is a handle that
    /// still owns a doomed object on a site whose watch is gone, and re-installing it needs the
    /// factory the registry was originally handed. Core-owned delegate, dropped by
    /// <see cref="Clear"/> with everything else this table pins.
    /// </summary>
    private static readonly Dictionary<VanillaUiSite, Func<VanillaUiSite, IDisposable?>> Factories = new();

    /// <summary>
    /// How many times a site's watch is rebuilt before the repair gives up on it. Bounded because
    /// the failure mode of an unbounded repair is a per-frame install attempt that logs, allocates
    /// and fails for the life of the generation.
    ///
    /// <para><b>Rebuilds, not consecutive failures (R63 finding 2).</b> The count used to be given
    /// back the moment a watch was filed, so it bounded a run of failures and bounded nothing at
    /// all for the other shape: a site whose watch keeps being rebuilt and keeps disappearing was
    /// repaired and warned about once a frame, forever, and never reached the give-up line —
    /// every loss after a success counted as "attempt 1" again. The counter now runs over the
    /// whole generation and is given back only where the site genuinely stopped needing a watch
    /// (<see cref="Settle"/>'s remove arm), so five rebuilds is five rebuilds either way.</para>
    /// </summary>
    private const int MaxWatchRepairs = 5;

    /// <summary>
    /// Repair attempts already spent per site over this generation. Cleared only when the site
    /// stops needing a watch at all — never on a successful install, which is R63 finding 2.
    /// </summary>
    private static readonly Dictionary<VanillaUiSite, int> RepairTries = new();

    /// <summary>
    /// How the repair gets a frame, and the whole of R62 finding 2.
    ///
    /// <para>Every other driver in this module is the site's own frame watch:
    /// <see cref="Dispatch"/> runs from it, <c>Apply</c> runs from <see cref="Dispatch"/>, and
    /// <see cref="Settle"/> runs from <c>Apply</c>. So the one state the repair exists for — a
    /// kept handle on a site whose watch is gone — is the one state that watch-driven chain can
    /// never observe, because losing the watch also loses the caller. The heartbeat is a
    /// per-frame tick that is <b>not</b> the watch: the Unity half hands it over the first time it
    /// builds a watch, and it is dropped with everything else at Uninstall.</para>
    ///
    /// <para><b>It lasts exactly as long as the table does (R63 finding 1).</b> It is started by
    /// the first <see cref="Add"/> that has something to repair for, stopped by
    /// <see cref="Settle"/> the moment the last attachment and the last watch are gone, and
    /// restarted by the next <see cref="Add"/>. Left running it is the object
    /// <see cref="InstallWatch"/>'s own comment refuses to file: a per-frame Core-owned delegate
    /// walking an empty list until Core Uninstall.</para>
    ///
    /// <para><b>Including the build-time interleave (R64 finding 2).</b> The tick is built outside
    /// <see cref="Gate"/>, so the last handle can go while the factory is running and
    /// <see cref="Settle"/> then finds no tick to stop. That needs no lock held across the Unity
    /// half to prevent: the factory has already returned by the time the filing lock is taken, so
    /// <see cref="EnsureHeartbeat"/> re-asks the live table there and gives the tick straight back
    /// if it emptied — the same second-lock guard <see cref="InstallWatch"/> makes for a watch
    /// (R60 finding 7). <see cref="Describe"/> still reports the tick whenever it exists, the way
    /// <c>UiSettleGate.PumpArmed</c> is reported even with nothing owed, because a tick over an
    /// empty table is now a state no path produces and therefore worth seeing if it ever
    /// appears.</para>
    ///
    /// <para><b>And the question is "can anything need a repair", not "is the table empty"
    /// (R65 finding 2).</b> All three of those decisions used to read
    /// <c>Live.Count == 0 &amp;&amp; Watches.Count == 0</c>, and <see cref="Live"/> holds disabled
    /// handles too — the one <c>Ui.AttachToVanilla</c> hands back for a site Core does not know,
    /// for a skipped module or off the client. Such a handle can never install a watch, can never
    /// lose one and can never be repaired: <see cref="NeedsWatchLocked"/> never returns true for
    /// it and <see cref="RepairLostWatches"/> can never do anything for it. It nevertheless made
    /// the table non-empty forever, so one of them kept the per-frame Core-owned delegate alive
    /// for the whole generation over a list it could not act on — R63 finding 1's object reached
    /// by another path. The predicate is now <see cref="NothingToDriveLocked"/>: no watch that
    /// could drop, and no enabled handle that could need one.</para>
    /// </summary>
    private static Func<Action, IDisposable?>? _heartbeatFactory;

    private static IDisposable? _heartbeat;

    /// <summary>
    /// Teach the registry how to get a frame that does not come from a site watch. Called by the
    /// Unity half (which is the only half that may name <c>Plugin.Frame</c> and still compile in
    /// the test project); first one wins, and passing null is a no-op rather than a reset.
    /// </summary>
    internal static void UseHeartbeat(Func<Action, IDisposable?>? factory)
    {
        if (factory == null) return;
        lock (Gate)
        {
            if (_heartbeatFactory != null) return;
            _heartbeatFactory = factory;
        }
    }

    /// <summary>Attachments currently tracked, disabled ones included.</summary>
    internal static int Count
    {
        get { lock (Gate) return Live.Count; }
    }

    /// <summary>Distinct sites with at least one attachment.</summary>
    internal static int SiteCount
    {
        get { lock (Gate) return SiteCountLocked(); }
    }

    /// <summary>Frame watches currently held. One per site with a live enabled attachment.</summary>
    internal static int WatchCount
    {
        get { lock (Gate) return Watches.Count; }
    }

    /// <summary>
    /// Is the per-frame repair tick installed? Reported by the pin diagnostic even when nothing is
    /// attached, because a tick with nothing to drive is the state that means an earlier teardown
    /// left one standing (R63 finding 1) — a state no path produces since R64 finding 2 closed the
    /// build-time interleave and R65 finding 2 stopped a permanently disabled handle from counting
    /// as something to drive, which is exactly why it is worth seeing if it ever appears.
    /// </summary>
    internal static bool HasHeartbeat
    {
        get { lock (Gate) return _heartbeat != null; }
    }

    /// <summary>One grep line for <c>Ui.DescribePins</c>.</summary>
    internal static string Describe()
    {
        lock (Gate)
            return "attach=" + Live.Count + " attachSites=" + SiteCountLocked() + " attachHooks=" + Watches.Count
                + " attachTick=" + (_heartbeat != null);
    }

    /// <summary>
    /// Track <paramref name="handle"/> and make sure its site is being watched. The watch
    /// factory is passed in rather than called here because installing it is the Unity half's
    /// job and this type must stay game-free.
    /// </summary>
    internal static void Add(VanillaAttachHandle handle, Func<VanillaUiSite, IDisposable?>? installWatch)
    {
        if (handle == null) return;
        bool needWatch;
        lock (Gate)
        {
            if (!Contains(handle)) Live.Add(handle);
            if (installWatch != null) Factories[handle.Site] = installWatch;
            needWatch = handle.Enabled && !Watches.ContainsKey(handle.Site);
        }

        if (needWatch && installWatch != null) InstallWatch(handle.Site, installWatch);

        // After the install, never before it: the Unity half hands the heartbeat over from inside
        // the watch factory, so this is the first moment there is one to start.
        EnsureHeartbeat();
    }

    /// <summary>
    /// Start the per-frame repair tick, once, as soon as there is something to repair for and a
    /// way to get a frame. Built outside <see cref="Gate"/> for the same reason a watch is, and a
    /// second one that loses the race is given straight back.
    ///
    /// <para>It declines when there is nothing a repair could ever act on: the caller is
    /// <see cref="Add"/>, whose handle can have been disposed from inside the watch factory before
    /// this runs — the R60-finding-7 interleave — and whose handle can equally be a permanently
    /// disabled one (R65 finding 2), which is in the table but is not something to drive. Starting
    /// a tick for either is the same object that finding refused for the watch. The question is
    /// asked <b>twice</b>, once before the factory and once after it (R64 finding 2): the handle
    /// can also go from inside the heartbeat factory itself, or on another thread, and only the
    /// second read is the one the filing decision is made on.</para>
    /// </summary>
    private static void EnsureHeartbeat()
    {
        Func<Action, IDisposable?>? factory;
        lock (Gate)
        {
            if (_heartbeat != null || _heartbeatFactory == null) return;
            if (NothingToDriveLocked()) return;
            factory = _heartbeatFactory;
        }

        IDisposable? tick = null;
        try { tick = factory(RepairTick); }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: attach watch-repair tick install threw: " + e.Message);
        }

        if (tick == null) return;

        IDisposable? loser = null;
        lock (Gate)
        {
            // Both ways this tick can be unwanted by the time it exists, decided against the live
            // table under the same lock that owns it — the guard InstallWatch makes twelve lines
            // down for a watch (R64 finding 2). Another Add filed one first; or the table stopped
            // having anything to drive while the factory was running, and Settle's remove arm —
            // the only path that stops a tick — has already run and found none. Filing it then
            // would leave the per-frame Core-owned delegate over a list it cannot act on, which is
            // what this whole mechanism exists to remove.
            if (_heartbeat != null || NothingToDriveLocked()) loser = tick;
            else _heartbeat = tick;
        }

        // Said out loud, like every other tick disposal in this file (R65 finding 2, secondary): a
        // Unity lease that refuses to release is the one thing here worth a warning, and swallowing
        // it silently is how a leaked per-frame delegate stops being visible.
        if (loser != null)
        {
            try { loser.Dispose(); }
            catch (Exception e)
            {
                Plugin.Logger?.LogWarning("Ui: attach watch-repair tick dispose threw: " + e.Message);
            }
        }
    }

    /// <summary>One heartbeat frame. Named so the delegate reads as itself in a pin dump.</summary>
    private static void RepairTick() => RepairLostWatches();

    /// <summary>
    /// Give back the frame watch to every site that still needs one and has lost it, and say so.
    /// Returns how many sites were re-installed this pass, for the tests and for a caller that
    /// wants a number.
    ///
    /// <para>Driven from two places, neither of which is a site watch (R62 finding 2): the
    /// heartbeat above, every frame, and <see cref="Settle"/>'s keep arm, which is the
    /// no-frame-of-latency path for the ordinary case of a handle disposed while its site happens
    /// to have no watch. Both go through here so the bound and the log lines exist once.</para>
    /// </summary>
    internal static int RepairLostWatches()
    {
        List<VanillaUiSite>? sites = null;
        List<Func<VanillaUiSite, IDisposable?>>? factories = null;
        List<int>? attempts = null;
        lock (Gate)
        {
            // The gate on the whole thing: with no handle owing anything on a watch-less site
            // this is a walk of a list that is almost always empty or one long.
            for (int i = 0; i < Live.Count; i++)
            {
                var site = Live[i].Site;
                if (Watches.ContainsKey(site)) continue;
                if (sites != null && sites.Contains(site)) continue;
                if (!NeedsWatchLocked(site)) continue;
                if (!Factories.TryGetValue(site, out var factory)) continue;

                RepairTries.TryGetValue(site, out int spent);
                if (spent >= MaxWatchRepairs) continue;
                RepairTries[site] = spent + 1;

                sites ??= new List<VanillaUiSite>();
                factories ??= new List<Func<VanillaUiSite, IDisposable?>>();
                attempts ??= new List<int>();
                sites.Add(site);
                factories.Add(factory);
                attempts.Add(spent + 1);
            }
        }

        if (sites == null) return 0;

        for (int i = 0; i < sites.Count; i++)
        {
            // Once per loss, not once per frame: the retry is bounded and its first line is the
            // one an operator needs.
            if (attempts![i] == 1)
            {
                Plugin.Logger?.LogWarning(
                    "Ui: attach " + VanillaUiSites.Key(sites[i])
                    + " lost its frame watch while an attachment still needs it; rebuilding.");
            }

            InstallWatch(sites[i], factories![i]);

            bool got;
            lock (Gate) got = Watches.ContainsKey(sites[i]);

            // The bound being reached is what earns the line, not the last attempt having failed
            // (R63 finding 2). A site that is rebuilt every frame and loses it again every frame
            // never fails an install, so gating this on !got meant the flapping shape was the one
            // shape that repaired and warned forever without ever giving up. The counter stops at
            // MaxWatchRepairs, so this fires exactly once per site per generation either way.
            if (attempts[i] < MaxWatchRepairs) continue;

            Plugin.Logger?.LogWarning(
                "Ui: attach " + VanillaUiSites.Key(sites[i]) + " gave up repairing its frame watch after "
                + MaxWatchRepairs + " attempts (watch=" + (got ? "rebuilt and lost again" : "absent")
                + "); anything it still owes a destroy stays in the vanilla hierarchy.");
        }

        return sites.Count;
    }

    /// <summary>
    /// Build a site's watch outside <see cref="Gate"/> and file it — or give it straight back if
    /// the site stopped wanting one while it was being built.
    /// </summary>
    private static void InstallWatch(VanillaUiSite site, Func<VanillaUiSite, IDisposable?> installWatch)
    {
        IDisposable? watch = null;
        try { watch = installWatch(site); }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning(
                "Ui: attach " + VanillaUiSites.Key(site) + " watch install threw: " + e.Message);
        }

        if (watch == null)
        {
            // No refresh means the container can never be built or rebuilt, so say so once
            // rather than leaving a handle that reports Enabled and never attaches. A site the
            // resolver already reported missing has said its one line (P6).
            if (VanillaSiteHealth.IsMissing(site)) return;
            Plugin.Logger?.LogWarning(
                "Ui: attach " + VanillaUiSites.Key(site)
                + " has no frame watch; rows will not appear (site="
                + VanillaUiSites.Info(site).System + ").");
            return;
        }

        IDisposable? loser = null;
        lock (Gate)
        {
            // Two ways this watch can be unwanted by the time it exists, and both are decided
            // here under the same lock as the count so neither can slip between the two reads.
            // Another attachment on the site installed one first; or the handle we were installing
            // *for* was disposed while we were outside the gate — its Remove found no watch to
            // drop, because none was registered yet, and storing this one now would leave a
            // per-frame Core-owned delegate dispatching to an empty list until Core Uninstall,
            // reported by pin-diag as attachHooks=1 attach=0 (R60 finding 7).
            // The repair count is deliberately *not* given back here (R63 finding 2): a site whose
            // watch keeps being rebuilt and keeps vanishing must reach the same bound as one that
            // cannot be rebuilt at all. Only Settle's remove arm, where the site stopped needing a
            // watch, clears it.
            if (Watches.ContainsKey(site) || !NeedsWatchLocked(site)) loser = watch;
            else Watches[site] = watch;
        }

        // Two attachments created on the same frame can both come through the gate above. The
        // second watch is disposed rather than kept, because two subscriptions would drive every
        // handle's refresh twice per frame and the second one would never be given back.
        if (loser != null)
        {
            try { loser.Dispose(); } catch { /* */ }
            return;
        }

        Plugin.Logger?.LogInfo(
            "Ui: attach " + VanillaUiSites.Key(site) + " watch installed on "
            + VanillaUiSites.Info(site).System);
    }

    /// <summary>
    /// Put the table in agreement with what <paramref name="handle"/> currently is: tracked while
    /// it is live or still owes a destroy, gone once it is neither, with the site's frame watch
    /// following it.
    ///
    /// <para><b>The decision is made here, not by the caller.</b> Whether a handle is finished is
    /// two facts — it is disposed, and it owns nothing whose destroy was refused — and a caller
    /// that reads them, then calls in, has already let a frame land in between. Both are read
    /// under <see cref="Gate"/>, from the handle itself, at the moment the table is edited. That
    /// is the whole of R61 finding 3: <see cref="VanillaAttachHandle.Dispose"/> used to publish
    /// its disposed flag first and hand over an emptiness that was not true yet, and the frame
    /// that believed it dropped both the handle and the only thing that could retry its
    /// container.</para>
    ///
    /// <para>A handle that is kept must have a watch, so a site that has lost one is handed to
    /// <see cref="RepairLostWatches"/> — which is also driven every frame by the heartbeat,
    /// because this call site cannot reach the state on its own (R62 finding 2).</para>
    ///
    /// <para>And the mirror of that: this is the only path on which the table can stop having
    /// anything to drive, so it is where the repair heartbeat is given back. A tick left running
    /// over a table no repair can act on is a per-frame Core-owned delegate that nothing but
    /// Uninstall takes back, which is the object <see cref="InstallWatch"/> refuses to file for the
    /// watch (R63 finding 1) — and "cannot act on" is not the same as "empty", which is R65
    /// finding 2 and why the test is <see cref="NothingToDriveLocked"/>.</para>
    /// </summary>
    internal static void Settle(VanillaAttachHandle? handle)
    {
        if (handle == null) return;
        IDisposable? watch = null;
        IDisposable? heartbeat = null;
        bool repair = false;
        lock (Gate)
        {
            bool keep = !handle.IsDisposed || handle.PendingDestroys > 0;
            if (keep)
            {
                if (!Contains(handle)) Live.Add(handle);
                repair = handle.Enabled && !Watches.ContainsKey(handle.Site);
            }
            else
            {
                for (int i = Live.Count - 1; i >= 0; i--)
                {
                    if (ReferenceEquals(Live[i], handle)) Live.RemoveAt(i);
                }

                if (!NeedsWatchLocked(handle.Site))
                {
                    // The one place the repair count is given back (R63 finding 2): the site
                    // stopped needing a watch, so the next one it asks for starts from zero. Done
                    // whether or not there was a watch to drop — a site that used up its five
                    // tries has no watch, and leaving its count behind would deny the repair to
                    // the next attachment on it.
                    RepairTries.Remove(handle.Site);
                    if (Watches.TryGetValue(handle.Site, out var w))
                    {
                        Watches.Remove(handle.Site);
                        watch = w;
                    }
                }

                // The tick outlives nothing (R63 finding 1). The remove arm is the only path on
                // which the table can stop having anything to drive, so it is the only place that
                // can stop it; the next Add starts it again, which EnsureHeartbeat is already
                // written to do. Asked as "nothing to drive" and not "empty" (R65 finding 2): the
                // handles left behind may all be permanently disabled ones, which no repair can
                // ever act on, and waiting for the table to empty then means waiting for Uninstall.
                if (NothingToDriveLocked())
                {
                    heartbeat = _heartbeat;
                    _heartbeat = null;
                }
            }
        }

        if (heartbeat != null)
        {
            try { heartbeat.Dispose(); }
            catch (Exception e)
            {
                Plugin.Logger?.LogWarning("Ui: attach watch-repair tick dispose threw: " + e.Message);
            }
        }

        if (repair)
        {
            RepairLostWatches();
            return;
        }

        if (watch == null) return;
        try { watch.Dispose(); }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning(
                "Ui: attach " + VanillaUiSites.Key(handle.Site) + " watch dispose threw: " + e.Message);
        }

        Plugin.Logger?.LogInfo("Ui: attach " + VanillaUiSites.Key(handle.Site) + " watch removed");
    }

    /// <summary>
    /// Hand this frame's reading of <paramref name="site"/> to every attachment on it. Walked
    /// over a snapshot: an <c>Apply</c> can dispose its own handle (a plugin's row callback is
    /// the plugin's code), and a list mutated under the loop that reads it is the other half of
    /// every defect of this shape this project has already fixed once.
    /// </summary>
    internal static void Dispatch(VanillaUiSite site, in VanillaSiteSample sample)
    {
        VanillaAttachHandle[] pass;
        lock (Gate)
        {
            var list = new List<VanillaAttachHandle>();
            for (int i = 0; i < Live.Count; i++)
            {
                if (Live[i].Site == site) list.Add(Live[i]);
            }

            if (list.Count == 0) return;
            pass = list.ToArray();
        }

        for (int i = 0; i < pass.Length; i++)
        {
            try { pass[i].Apply(sample); }
            catch (Exception e)
            {
                Plugin.Logger?.LogWarning(
                    "Ui: attach " + VanillaUiSites.Key(site) + " refresh threw: " + e.Message);
            }
        }
    }

    /// <summary>
    /// Dispose every attachment recorded for <paramref name="owner"/>. Returns how many, so
    /// <c>Ui.RemoveAll</c> can fold a real number into its count rather than a hardcoded zero —
    /// the defect <c>PluginStaticApiSweep</c> already records once.
    /// </summary>
    internal static int RemoveAll(object owner)
    {
        if (owner == null) return 0;
        VanillaAttachHandle[] mine;
        lock (Gate)
        {
            var list = new List<VanillaAttachHandle>();
            for (int i = 0; i < Live.Count; i++)
            {
                if (ReferenceEquals(Live[i].Owner, owner)) list.Add(Live[i]);
            }

            mine = list.ToArray();
        }

        return DisposeAll(mine);
    }

    /// <summary>Core Uninstall: drop every attachment and every watch. Returns how many attachments went.</summary>
    internal static int Clear()
    {
        VanillaAttachHandle[] all;
        IDisposable[] watches;
        IDisposable? heartbeat;
        lock (Gate)
        {
            all = Live.ToArray();
            watches = new IDisposable[Watches.Count];
            Watches.Values.CopyTo(watches, 0);
            Watches.Clear();
            Factories.Clear();
            RepairTries.Clear();
            heartbeat = _heartbeat;
            _heartbeat = null;
        }

        // Before the disposals, so nothing a handle's Dispose does can restart a repair for a
        // site whose factory has already gone.
        if (heartbeat != null)
        {
            try { heartbeat.Dispose(); }
            catch (Exception e) { Plugin.Logger?.LogWarning("Ui: attach watch-repair tick dispose threw: " + e.Message); }
        }

        int n = DisposeAll(all);

        // Uninstall is the last frame this Core generation gets, so a destroy that was refused
        // here will never be retried. Say so with a count rather than letting the objects go
        // quietly: they are parented into the vanilla tree and nothing can reach them again.
        int stranded = 0;
        for (int i = 0; i < all.Length; i++) stranded += all[i].PendingDestroys;
        if (stranded > 0)
        {
            Plugin.Logger?.LogWarning(
                "Ui: attach Uninstall left " + stranded
                + " object(s) whose destroy was refused; they stay in the vanilla hierarchy.");
        }

        // The watches go last and unconditionally. A handle's own Dispose already drops the
        // watch when it was the last on its site, but a watch whose handles all threw would
        // otherwise be left holding a Core delegate across the swap.
        for (int i = 0; i < watches.Length; i++)
        {
            try { watches[i].Dispose(); }
            catch (Exception e) { Plugin.Logger?.LogWarning("Ui: attach watch dispose threw: " + e.Message); }
        }

        lock (Gate)
        {
            Live.Clear();
            Watches.Clear();
            Factories.Clear();
            RepairTries.Clear();
        }

        return n;
    }

    private static int DisposeAll(VanillaAttachHandle[] handles)
    {
        int n = 0;
        for (int i = 0; i < handles.Length; i++)
        {
            // Counted before the call, not after: a handle that throws on the way out is still a
            // handle that was in the table, and reporting it as swept-zero is the shape of log
            // line this project has a standing rule about.
            if (!handles[i].IsDisposed) n++;
            try { handles[i].Dispose(); }
            catch (Exception e) { Plugin.Logger?.LogWarning("Ui: attach dispose threw: " + e.Message); }
        }

        return n;
    }

    private static bool Contains(VanillaAttachHandle handle)
    {
        for (int i = 0; i < Live.Count; i++)
        {
            if (ReferenceEquals(Live[i], handle)) return true;
        }

        return false;
    }

    /// <summary>
    /// Is there still something on <paramref name="site"/> that the per-frame watch has to drive?
    /// An enabled attachment that is not disposed, or a disposed one whose container refused to
    /// be destroyed — the retry runs from that same watch, so dropping it would strand the object
    /// (R60 finding 1). A disabled handle never installs a watch and never needs one.
    /// </summary>
    private static bool NeedsWatchLocked(VanillaUiSite site)
    {
        for (int i = 0; i < Live.Count; i++)
        {
            var h = Live[i];
            if (h.Site != site || !h.Enabled) continue;
            if (!h.IsDisposed || h.PendingDestroys > 0) return true;
        }

        return false;
    }

    /// <summary>
    /// Can anything in the table ever need a repair? Two ways it can: a watch is held, and a watch
    /// can drop; or an <b>enabled</b> handle is tracked, and an enabled handle is the only kind
    /// that installs a watch, loses one and is repaired for.
    ///
    /// <para>This is the predicate the heartbeat lives and dies by, and R65 finding 2 is the whole
    /// of why it is not <c>Live.Count == 0 &amp;&amp; Watches.Count == 0</c>. A permanently
    /// disabled handle — <c>Ui.AttachToVanilla</c> hands one back for an unknown site, a skipped
    /// module or a non-client runtime, and <c>Enabled</c> is get-only — sits in <see cref="Live"/>
    /// until its owner disposes it, is skipped by <see cref="NeedsWatchLocked"/> and can never be
    /// the reason <see cref="RepairLostWatches"/> does anything. Counting it as a reason to keep a
    /// per-frame Core-owned delegate meant one such handle held the tick for the rest of the
    /// generation, over a list that could not produce a single repair.</para>
    ///
    /// <para>Deliberately coarser than <see cref="NeedsWatchLocked"/>: an enabled handle that is
    /// disposed with nothing pending is still counted here, for the one frame before its
    /// <see cref="Settle"/> takes it out of the list. Erring towards keeping the tick costs a frame
    /// of a list walk; erring the other way would drop the driver of the repair that frame owed.</para>
    /// </summary>
    private static bool NothingToDriveLocked()
    {
        if (Watches.Count != 0) return false;
        for (int i = 0; i < Live.Count; i++)
        {
            if (Live[i].Enabled) return false;
        }

        return true;
    }

    private static int SiteCountLocked()
    {
        int n = 0;
        var seen = new List<VanillaUiSite>();
        for (int i = 0; i < Live.Count; i++)
        {
            if (seen.Contains(Live[i].Site)) continue;
            seen.Add(Live[i].Site);
            n++;
        }

        return n;
    }

#if PROGENITOR_TESTS
    internal static void ResetForTests()
    {
        IDisposable? heartbeat;
        lock (Gate)
        {
            Live.Clear();
            Watches.Clear();
            Factories.Clear();
            RepairTries.Clear();
            heartbeat = _heartbeat;
            _heartbeat = null;
            _heartbeatFactory = null;
        }

        if (heartbeat != null) { try { heartbeat.Dispose(); } catch { /* */ } }
    }

    internal static bool HasWatch(VanillaUiSite site)
    {
        lock (Gate) return Watches.ContainsKey(site);
    }

    /// <summary>
    /// Take a site's watch away without going through <see cref="Settle"/> — the state R62
    /// finding 2 is about, which no production path reaches today and which the repair must
    /// nevertheless recover from. Returns true if there was one.
    /// </summary>
    internal static bool DropWatchForTests(VanillaUiSite site)
    {
        IDisposable? watch;
        lock (Gate)
        {
            if (!Watches.TryGetValue(site, out watch)) return false;
            Watches.Remove(site);
        }

        try { watch.Dispose(); } catch { /* */ }
        return true;
    }

    /// <summary>Is the per-frame repair tick running?</summary>
    internal static bool HasRepairTickForTests
    {
        get { lock (Gate) return _heartbeat != null; }
    }
#endif
}
