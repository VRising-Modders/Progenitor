using System;
using System.Reflection;
using System.Threading;

namespace Progenitor.Bootstrap;

/// <summary>
/// Job FHARM5. A fixed pool of process-lifetime Harmony patch bodies that a collectible
/// assembly can borrow, so that no <c>MethodInfo</c> from a collectible
/// <see cref="System.Runtime.Loader.AssemblyLoadContext"/> is ever handed to HarmonyX.
///
/// <para><b>Why this exists.</b> <c>Harmony.Patch</c> roots the patch method for the lifetime
/// of the process, and <c>Unpatch</c> does not undo that. Two independent append-only statics
/// hold it (both measured and confirmed in the native code):</para>
/// <list type="number">
/// <item><description><c>HarmonyLib.Internal.RuntimeFixes.StackTraceFixes.RealMethodMap</c>, a
/// static <c>Dictionary&lt;MethodBase, MethodBase&gt;</c> written on every
/// <c>ILHook</c>/<c>Detour</c> apply (<c>RealMethodMap[val.Target] = val.Method</c>) and never
/// read back out or cleared. Its key is the generated replacement <c>DynamicMethod</c>, whose
/// token scope keeps the patch method — and therefore its ALC — alive. This fires for
/// <b>every</b> target, managed or IL2CPP.</description></item>
/// <item><description><c>Il2CppInterop.HarmonySupport.Il2CppDetourMethodPatcher.DelegateCache</c>,
/// a static <c>List&lt;object&gt;</c> with exactly one <c>Add</c> and no removal anywhere in the
/// assembly. This fires additionally for IL2CPP targets.</description></item>
/// </list>
///
/// <para><b>The shape.</b> Each slot owns six process-lifetime static methods here — prefix,
/// postfix and finalizer, in an instance-target and a static-target flavor. Harmony only ever
/// sees one of those; the collectible side's behavior is reached through
/// <see cref="Body"/>, a plain delegate held in <see cref="Bodies"/>, which
/// <see cref="Release"/> nulls. HarmonyX and Il2CppInterop keep their rows forever, but every
/// row now names <c>Progenitor.Bootstrap</c>, which is not collectible, so the plugin ALC is
/// free the moment the slot is released. This is the same trade the typed
/// <c>HarmonySlots</c> makes for Core's own sites, generalized: the body is here, the behavior
/// is generational.</para>
///
/// <para><b>The price.</b> A slot body is signature-agnostic, so it is handed
/// <c>__instance</c> and <c>__args</c> only. It cannot read or write <c>__result</c> and it
/// does not see typed by-ref parameters directly (Harmony writes by-ref values back through
/// <c>__args</c>). A patch that needs <c>__result</c> is not expressible through a slot; say so
/// rather than pretending, and use a Bootstrap-owned typed site
/// (<c>HarmonySlotHandoff</c>) for it.</para>
///
/// <para><b>The pool is fixed at <see cref="Capacity"/> = 32.</b> Growing it means
/// emitting more static methods into a non-collectible assembly at run time, which is the
/// problem this file exists to avoid, so exhaustion is a <em>refusal</em>:
/// <c>patches: refused prefix X owner=y reason=slots-exhausted offset=?</c>. 32 is the number
/// of distinct live plugin patches Progenitor supports at once across every loaded plugin; the
/// live count is in the <c>ping</c> line. Raising it is a Bootstrap change (restart both).</para>
/// </summary>
public static class PatchSlots
{
    /// <summary>Number of slots. See the type remarks for why this is fixed.</summary>
    public const int Capacity = 32;

    /// <summary>
    /// Slots no plugin may take (job FHARM7, R41 finding 2). <see cref="Bind"/> is first-come and
    /// Core arms lazily — god mode's three kill prefixes are registered on the first
    /// <c>.godmode</c>, the world-load epoch postfix on the first world load, the event fallbacks
    /// only against a Bootstrap that does not own their sites — so without a reservation a plugin
    /// that patched 32 things at load could starve Core of a seam it later needs, and god mode's
    /// atomic group of three would then be refused as a group and silently stay off.
    ///
    /// <para>Twelve is eleven plus one spare: the world-load postfix (1), god mode's kill
    /// prefixes (3), the three <c>EventHooks</c> fallbacks (chat, connect, disconnect), and the
    /// four job FHARM8 routed (<c>DeserializeLoadFile</c> prefix and postfix,
    /// <c>SetLanguage</c>, <c>BeginCreateServerWorld</c>). Every Core registration in the process
    /// is under the one owner id ending <see cref="CoreOwnerSuffix"/>, so this budget is exact
    /// rather than a guess — the eleven are enumerated in
    /// <c>Progenitor.Patches.CorePatchBudget</c> and <c>PatchSlotBudgetTests</c> fails the build
    /// if a Core patch is added without raising this number. Raising it is a Bootstrap change
    /// (restart both) and costs plugins one slot each.</para>
    /// </summary>
    public const int CoreReserve = 12;

    /// <summary>Slots plugins may take: <see cref="Capacity"/> minus <see cref="CoreReserve"/>.</summary>
    public const int NonCoreCapacity = Capacity - CoreReserve;

    /// <summary>
    /// <see cref="NonCoreFree"/> at or below this logs one warning, so a pool that is filling up
    /// says so <b>before</b> the first refusal rather than after it.
    ///
    /// <para><b>Why this is measured against the plugin budget and not against
    /// <see cref="Free"/></b> (job FHARM8, R42 finding 2). It used to be <see cref="Free"/>, four
    /// slots, sitting nine lines below a <see cref="CoreReserve"/> of eight — so a plugin was
    /// refused the moment total free reached the reserve, which is <em>above</em> the threshold,
    /// and the warning that exists to precede that refusal could only fire later, if Core's own
    /// lazily-armed seams then took the pool down past four. The number a plugin's <see cref="Bind"/>
    /// is actually refused against is <see cref="NonCoreFree"/>, so that is the number the
    /// warning watches. The total pool is watched too, on its own latch, so Core eating its
    /// reserve is not silent either.</para>
    /// </summary>
    public const int LowFreeWarnThreshold = 4;

    /// <summary>
    /// MIRROR of <c>Progenitor.Patches.PatchSitePolicy.CoreOwnerSuffix</c>. Bootstrap must not
    /// compile-reference Core, and the reservation has to recognize Core's owner id
    /// (<c>&lt;guid&gt;.core</c>) here, on the allocating side, or a plugin could simply spell a
    /// core-looking id — which it cannot, because the id is built from Core's own
    /// <c>MyPluginInfo.PLUGIN_GUID</c> and a plugin borrowing it would also inherit Core's sweep.
    /// </summary>
    public const string CoreOwnerSuffix = ".core";

    /// <summary>Slot kind: a prefix; a body returning boxed <c>false</c> skips the original.</summary>
    public const int KindPrefix = 0;

    /// <summary>Slot kind: a postfix; the body's return value is ignored.</summary>
    public const int KindPostfix = 1;

    /// <summary>
    /// Slot kind: a finalizer; the body is given the in-flight exception (or null) and returns
    /// the exception to propagate — return the one it was given to keep it, null to swallow it.
    /// </summary>
    public const int KindFinalizer = 2;

    /// <summary>Sentinel returned by <see cref="Bind"/> when the pool is full.</summary>
    public const int NoSlot = -1;

    /// <summary>
    /// The collectible side's behavior. <paramref name="instance"/> is Harmony's
    /// <c>__instance</c> (null for a static target), <paramref name="args"/> is <c>__args</c>,
    /// and <paramref name="exception"/> is the finalizer's <c>__exception</c> (null otherwise).
    /// A prefix returns a boxed <see cref="bool"/> (false skips the original; null means true);
    /// a postfix's return is ignored; a finalizer returns the <see cref="Exception"/> to
    /// propagate. Declared here, in the non-collectible assembly, so that binding a delegate to
    /// it does not require the collectible side to reference a Harmony type.
    /// </summary>
    public delegate object? Body(object? instance, object?[] args, Exception? exception);

    /// <summary>
    /// Bits of a slot token that hold the index. <see cref="Capacity"/> is 32, so 8 is generous;
    /// the remaining 56 bits are the generation, which at one rebind per millisecond would take
    /// two million years to wrap.
    /// </summary>
    public const int TokenIndexBits = 8;

    private static readonly object Gate = new();
    private static readonly Body?[] Bodies = new Body?[Capacity];
    private static readonly string[] Owners = new string[Capacity];
    private static readonly int[] Kinds = new int[Capacity];
    private static readonly int[] Faults = new int[Capacity];
    private static readonly long[] Generations = new long[Capacity];
    private static readonly bool[] Quarantined = new bool[Capacity];
    private static int _inUse;
    private static int _quarantined;
    private static int _coreInUse;
    private static bool _lowWarned;
    private static bool _lowWarnedPool;
    /// <summary>One line at the FIRST plugin refusal, re-armed when the budget recovers.</summary>
    private static bool _refusalWarned;
    private static string? _pendingWarning;

    /// <summary>Slots currently bound.</summary>
    public static int InUse => Volatile.Read(ref _inUse);

    /// <summary>Slots currently bound to the Core owner (see <see cref="CoreReserve"/>).</summary>
    public static int CoreInUse => Volatile.Read(ref _coreInUse);

    /// <summary>Slots currently bound to plugins.</summary>
    public static int NonCoreInUse => InUse - CoreInUse;

    /// <summary>
    /// True when <paramref name="owner"/> is Core's own registry owner id. Everything Core
    /// registers uses one id ending in <see cref="CoreOwnerSuffix"/>.
    /// </summary>
    public static bool IsCoreOwner(string? owner) =>
        !string.IsNullOrEmpty(owner)
        && owner!.EndsWith(CoreOwnerSuffix, StringComparison.Ordinal);

    /// <summary>
    /// Slots a plugin may still take. <b>Quarantine attrition is charged here first</b> (R41
    /// finding 2): a one-way loss must shrink the plugin budget before it touches the reservation
    /// Core's lazily-armed seams depend on, or the pool would eventually disarm the world-load
    /// epoch postfix at the next Core install with nothing said.
    /// </summary>
    public static int NonCoreFree
    {
        get
        {
            int left = NonCoreCapacity - NonCoreInUse - QuarantineCount;
            int free = Free;
            return Math.Max(0, Math.Min(left, free));
        }
    }

    /// <summary>
    /// False once quarantine attrition has eaten past the plugin budget and into
    /// <see cref="CoreReserve"/> — the state in which a Core seam can be refused. Reported by
    /// <see cref="Describe"/> and warned about by <see cref="Quarantine"/>.
    /// </summary>
    public static bool CoreReserveIntact => QuarantineCount + NonCoreInUse <= NonCoreCapacity;

    /// <summary>
    /// True when <paramref name="owner"/> would be refused by the <see cref="CoreReserve"/> while
    /// slots are still physically free — so the caller can say <c>reason=slots-reserved</c>
    /// rather than the misleading <c>slots-exhausted</c>.
    /// </summary>
    public static bool ReservedForCore(string? owner) =>
        !IsCoreOwner(owner) && Free > 0 && NonCoreFree == 0 && !LostToQuarantine(owner);

    /// <summary>
    /// True when the plugin budget is exhausted and <b>quarantine attrition</b>, not other
    /// plugins, is what exhausted it (job FHARM8, R42 finding 2).
    ///
    /// <para>The distinction is the whole point. <see cref="NonCoreFree"/> deliberately charges
    /// attrition to the plugin budget first, so a plugin starved purely by <em>another</em>
    /// plugin's failed unpatches was being refused with <c>slots-reserved</c>, whose remedy is
    /// "unload a plugin, or raise <c>PatchSlots.Capacity</c>". Neither is the cause and neither
    /// is the fix: quarantined slots are gone for the life of the process, so the fix is a
    /// restart. The only line that said so was the per-quarantine warning, which had scrolled
    /// past thirty reloads earlier.</para>
    /// </summary>
    public static bool LostToQuarantine(string? owner) =>
        !IsCoreOwner(owner)
        && QuarantineCount > 0
        && NonCoreFree == 0
        && NonCoreInUse < NonCoreCapacity;

    /// <summary>
    /// Take the one pending pool warning, or null. A string rather than a logger callback on
    /// purpose: this assembly is not collectible and a delegate handed to it from Core would pin
    /// the Core generation, which is the entire problem this file exists to solve. The registry
    /// drains it after every <see cref="Bind"/> and <see cref="Quarantine"/> and logs it.
    /// </summary>
    public static string? TakeWarning()
    {
        lock (Gate)
        {
            var w = _pendingWarning;
            _pendingWarning = null;
            return w;
        }
    }

    /// <summary>
    /// The line <see cref="Bind"/> raises when the <b>plugin budget</b> is nearly gone — the
    /// number a plugin's bind is refused against (job FHARM8, R42 finding 2). It names both the
    /// budget and the whole pool so the reader can tell which of the three refusals is coming.
    /// </summary>
    public static string FormatLowFree(int pluginFree, int free, int inUse, int quarantined) =>
        "patch-slots: WARNING plugin budget " + pluginFree + "/" + NonCoreCapacity
        + " free (pool free=" + free + "/" + Capacity + ", in-use=" + inUse
        + " quarantined=" + quarantined + ", core reserve=" + CoreReserve + "). The pool is fixed "
        + "and cannot grow at run time; the next plugin patch may be refused with "
        + "reason=slots-exhausted, slots-reserved or slots-lost-to-quarantine. Unload a plugin, "
        + "or raise PatchSlots.Capacity (a Bootstrap change: restart both); quarantined slots "
        + "come back only on a restart.";

    /// <summary>
    /// The line <see cref="Bind"/> raises when the <b>whole pool</b> is nearly gone, which after
    /// R42 finding 2 is a different crossing from <see cref="FormatLowFree"/>: it means Core's
    /// own reserve is being consumed, and the next thing refused is a Core seam, not a plugin.
    /// </summary>
    public static string FormatLowFreePool(int free, int inUse, int quarantined) =>
        "patch-slots: WARNING pool free=" + free + "/" + Capacity + " (in-use=" + inUse
        + " quarantined=" + quarantined + ", core reserve=" + CoreReserve + "). The plugin budget "
        + "is already gone, so this is Core's reserve being consumed; the next Core seam to arm "
        + "(the world-load epoch postfix, god mode's kill group, a module fallback) may be "
        + "refused. Restart the process to get the pool back.";

    /// <summary>The line <see cref="Quarantine"/> raises: the pool just got permanently smaller.</summary>
    public static string FormatQuarantineAttrition(int index, int quarantined, bool reserveIntact) =>
        "patch-slots: WARNING slot " + index + " is out of service for the life of the process; "
        + "quarantined=" + quarantined + " of " + Capacity + ", plugin budget now " + NonCoreFree
        + " free of " + NonCoreCapacity + ". "
        + (reserveIntact
            ? "The " + CoreReserve + "-slot core reserve is intact."
            : "ATTRITION HAS REACHED THE CORE RESERVE: a Core seam (the world-load epoch postfix, "
              + "god mode's kill group, an event fallback) can now be refused on the next Core "
              + "install. Restart the process to get the pool back.");

    /// <summary>
    /// Slots taken out of service for the life of the process by <see cref="Quarantine"/>.
    /// Counted against <see cref="Capacity"/> exactly like a bound slot, so the loss is visible
    /// in <see cref="Describe"/> and eventually costs a <c>slots-exhausted</c> refusal rather
    /// than being silently absorbed.
    /// </summary>
    public static int QuarantineCount => Volatile.Read(ref _quarantined);

    /// <summary>Slots available to <see cref="Bind"/>.</summary>
    public static int Free => Capacity - InUse - QuarantineCount;

    /// <summary>The index half of a slot token.</summary>
    public static int IndexOf(long token) =>
        token < 0 ? NoSlot : (int)(token & ((1L << TokenIndexBits) - 1));

    /// <summary>The generation half of a slot token.</summary>
    public static long GenerationOf(long token) => token < 0 ? -1 : token >> TokenIndexBits;

    /// <summary>The generation a slot is currently on. Tests and diagnostics.</summary>
    public static long CurrentGeneration(int index) =>
        index < 0 || index >= Capacity ? -1 : Volatile.Read(ref Generations[index]);

    /// <summary>True when <paramref name="index"/> has been taken out of service permanently.</summary>
    public static bool IsQuarantined(int index) =>
        index >= 0 && index < Capacity && Volatile.Read(ref Quarantined[index]);

    /// <summary>
    /// Total dispatch faults: a bound body that threw. The slot stays bound and the original
    /// runs as if unpatched, because a collectible body that throws must not take the game with
    /// it. Read by <c>ctx.Patches.Describe()</c>.
    /// </summary>
    public static int FaultCount
    {
        get
        {
            int n = 0;
            for (int i = 0; i < Capacity; i++) n += Volatile.Read(ref Faults[i]);
            return n;
        }
    }

    /// <summary>
    /// Take a slot. Returns a <b>token</b> - the index in its low <see cref="TokenIndexBits"/>
    /// bits and the slot's generation above them - or <see cref="NoSlot"/> when the pool is full
    /// or the arguments are bad. <paramref name="owner"/> must be non-empty: it is what
    /// <see cref="ReleaseOwner"/> sweeps by, and an empty owner would make a leaked slot
    /// unreachable.
    ///
    /// <para><b>Why a token and not an index</b> (R40 finding 2). A bare index is ambiguous the
    /// moment a slot is handed back: "slot 0" means one owner's patch before the release and
    /// another's after it, and this method deliberately prefers the lowest free index, so a
    /// just-released slot is the <em>first</em> one handed out again. Everything that addresses a
    /// slot after binding it - <see cref="Release"/>, <see cref="Quarantine"/> - therefore
    /// carries the generation it bound and is a no-op once the slot has moved on, so a stale
    /// registry entry can never release, or steal, a slot that now belongs to somebody else.</para>
    ///
    /// <para><b>What a generation cannot do</b>, stated so nobody reads more into it: it cannot
    /// be checked on the dispatch path. Harmony is handed <c>Prefix_I00</c>, which is compiled
    /// once and calls <c>Pre(0, ...)</c> forever; a detour that survives a failed unpatch carries
    /// no token, so no check inside the pool can tell it from a live one. That case is what
    /// <see cref="Quarantine"/> is for.</para>
    /// </summary>
    public static long Bind(int kind, Body body, string? owner)
    {
        if (body == null) return NoSlot;
        if (kind != KindPrefix && kind != KindPostfix && kind != KindFinalizer) return NoSlot;
        if (string.IsNullOrEmpty(owner)) return NoSlot;

        bool core = IsCoreOwner(owner);
        lock (Gate)
        {
            // Job FHARM7, R41 finding 2: the last CoreReserve slots are not first-come. Core
            // arms lazily, so a plugin that fills the pool at load would otherwise decide, hours
            // later, that god mode cannot arm.
            if (!core && NonCoreCapacity - (_inUse - _coreInUse) - _quarantined <= 0)
            {
                // R42 finding 2: the refusal path used to return before the accounting, so a
                // refused bind raised nothing at all — the one moment the state most needs
                // stating. It gets its own latch rather than sharing the crossing one, because
                // the crossing warning may already have been logged and drained a hundred binds
                // ago; one line at the first refusal, not one per refused bind.
                NoteLowFreeUnlocked();
                if (!_refusalWarned)
                {
                    _refusalWarned = true;
                    _pendingWarning ??= FormatLowFree(0, Capacity - _inUse - _quarantined, _inUse, _quarantined);
                }
                return NoSlot;
            }

            for (int i = 0; i < Capacity; i++)
            {
                if (Bodies[i] != null || Quarantined[i]) continue;
                Owners[i] = owner!;
                Kinds[i] = kind;
                Faults[i] = 0;
                Volatile.Write(ref Bodies[i], body);
                _inUse++;
                if (core) _coreInUse++;
                NoteLowFreeUnlocked();
                return (Generations[i] << TokenIndexBits) | (long)(uint)i;
            }
        }
        return NoSlot;
    }

    /// <summary>
    /// Raise <see cref="FormatLowFree"/> once per crossing of
    /// <see cref="LowFreeWarnThreshold"/> — once per crossing, not once per bind, so a pool that
    /// sits near full does not fill the log. Must be called under <see cref="Gate"/>.
    /// </summary>
    private static void NoteLowFreeUnlocked()
    {
        int free = Capacity - _inUse - _quarantined;
        int pluginFree = Math.Max(0, Math.Min(free, NonCoreCapacity - (_inUse - _coreInUse) - _quarantined));

        // Two crossings, two latches (R42 finding 2). The plugin budget is the number a plugin's
        // bind is refused against, so it is the one that has to warn first; the whole pool is
        // watched separately because falling past the threshold there means Core's reserve is
        // being consumed, which refuses a different caller for a different reason.
        if (pluginFree > LowFreeWarnThreshold) { _lowWarned = false; _refusalWarned = false; }
        else if (!_lowWarned)
        {
            _lowWarned = true;
            _pendingWarning = FormatLowFree(pluginFree, free, _inUse, _quarantined);
            return;
        }

        if (free > LowFreeWarnThreshold) { _lowWarnedPool = false; return; }
        if (_lowWarnedPool) return;
        _lowWarnedPool = true;
        // The plugin-budget line, when it is pending, is the more actionable of the two; do not
        // overwrite it.
        _pendingWarning ??= FormatLowFreePool(free, _inUse, _quarantined);
    }

    /// <summary>
    /// Release one slot, addressed by the token <see cref="Bind"/> returned. True when it was
    /// bound, still on that token's generation, and <paramref name="owner"/> matched (an empty or
    /// null owner matches anything, for the Core uninstall sweep). The Harmony patch on the
    /// slot's static method is the caller's to remove; releasing only drops the delegate, which
    /// is what frees the collectible ALC - and bumps the generation, so the token that released
    /// it can never address the slot again.
    /// </summary>
    public static bool Release(long token, string? owner)
    {
        int index = IndexOf(token);
        if (index < 0 || index >= Capacity) return false;
        lock (Gate)
        {
            if (Bodies[index] == null) return false;
            if (Generations[index] != GenerationOf(token)) return false;
            if (!string.IsNullOrEmpty(owner)
                && !string.Equals(Owners[index], owner, StringComparison.Ordinal)) return false;
            if (IsCoreOwner(Owners[index])) _coreInUse--;
            Volatile.Write(ref Bodies[index], null);
            Owners[index] = "";
            Volatile.Write(ref Generations[index], Generations[index] + 1);
            _inUse--;
            return true;
        }
    }

    /// <summary>
    /// Take one slot out of service <b>for the life of the process</b>: drop the body, bump the
    /// generation, and never hand this index to <see cref="Bind"/> again. True when the token
    /// still addressed a live slot this owner held.
    ///
    /// <para><b>Why this exists</b> (R40 finding 2). When the Harmony unpatch on a slot's static
    /// method throws, the detour may still be installed on the original. Releasing the slot makes
    /// that surviving detour a no-op that runs the original - but only for as long as the slot
    /// stays free. Rebinding it would turn the stranded detour into a live call into the next
    /// owner's body, with the old target's <c>__instance</c> and <c>__args</c>: the next plugin's
    /// prefix would run, and could return false, on a method nobody patched. Nothing on the
    /// dispatch path can tell the two apart (see <see cref="Bind"/>), so the only sound answer is
    /// to lose the slot. The loss is counted in <see cref="QuarantineCount"/>, charged against
    /// <see cref="Capacity"/> like a bound slot, and shown in <see cref="Describe"/>; the caller
    /// logs the line naming the patch it happened on.</para>
    /// </summary>
    public static bool Quarantine(long token, string? owner)
    {
        int index = IndexOf(token);
        if (index < 0 || index >= Capacity) return false;
        lock (Gate)
        {
            if (Quarantined[index]) return false;
            if (Bodies[index] == null) return false;
            if (Generations[index] != GenerationOf(token)) return false;
            if (!string.IsNullOrEmpty(owner)
                && !string.Equals(Owners[index], owner, StringComparison.Ordinal)) return false;
            if (IsCoreOwner(Owners[index])) _coreInUse--;
            Volatile.Write(ref Bodies[index], null);
            Owners[index] = "";
            Volatile.Write(ref Generations[index], Generations[index] + 1);
            Volatile.Write(ref Quarantined[index], true);
            _inUse--;
            _quarantined++;
            // R41 finding 2: attrition is one-way, so every single loss is said out loud, and
            // the line says whether it has reached the core reserve.
            _pendingWarning = FormatQuarantineAttrition(index, _quarantined, CoreReserveIntact);
            return true;
        }
    }

    /// <summary>Release every slot held by <paramref name="owner"/>. Returns how many.</summary>
    public static int ReleaseOwner(string? owner)
    {
        if (string.IsNullOrEmpty(owner)) return 0;
        int n = 0;
        lock (Gate)
        {
            for (int i = 0; i < Capacity; i++)
            {
                if (Bodies[i] == null) continue;
                if (!string.Equals(Owners[i], owner, StringComparison.Ordinal)) continue;
                if (IsCoreOwner(Owners[i])) _coreInUse--;
                Volatile.Write(ref Bodies[i], null);
                Owners[i] = "";
                Volatile.Write(ref Generations[i], Generations[i] + 1);
                _inUse--;
                n++;
            }
        }
        return n;
    }

    /// <summary>Release every slot, whoever owns it. Core uninstall's last resort.</summary>
    public static int ReleaseAll()
    {
        int n = 0;
        lock (Gate)
        {
            for (int i = 0; i < Capacity; i++)
            {
                if (Bodies[i] == null) continue;
                if (IsCoreOwner(Owners[i])) _coreInUse--;
                Volatile.Write(ref Bodies[i], null);
                Owners[i] = "";
                Volatile.Write(ref Generations[i], Generations[i] + 1);
                _inUse--;
                n++;
            }
        }
        return n;
    }

    /// <summary>
    /// Tests only: forget every quarantine, so one test's stranded slot does not shrink the pool
    /// for the next. Never called in production - a quarantine lasts for the life of the process,
    /// which is the whole guarantee (see <see cref="Quarantine"/>).
    /// </summary>
    public static void ClearQuarantineForTests()
    {
        lock (Gate)
        {
            for (int i = 0; i < Capacity; i++) Volatile.Write(ref Quarantined[i], false);
            _quarantined = 0;
            _lowWarned = false;
            _lowWarnedPool = false;
            _refusalWarned = false;
            _pendingWarning = null;
        }
    }

    /// <summary>The owner id a slot is bound to, or "" when it is free.</summary>
    public static string OwnerOf(int index) =>
        index < 0 || index >= Capacity ? "" : Owners[index] ?? "";

    /// <summary>
    /// The process-lifetime static method Harmony must be handed for
    /// <paramref name="index"/>. <paramref name="instanceTarget"/> picks the flavor that takes
    /// <c>__instance</c>; pass false for a static original, where HarmonyX rejects
    /// <c>__instance</c>. Null for a bad index or kind.
    /// </summary>
    public static MethodInfo? PatchMethod(long token, int kind, bool instanceTarget)
    {
        int index = IndexOf(token);
        if (index < 0 || index >= Capacity) return null;
        string prefix = kind switch
        {
            KindPrefix => "Prefix",
            KindPostfix => "Postfix",
            KindFinalizer => "Finalizer",
            _ => "",
        };
        if (prefix.Length == 0) return null;
        string name = prefix + (instanceTarget ? "_I" : "_S") + index.ToString("00");
        return typeof(PatchSlots).GetMethod(name, BindingFlags.Public | BindingFlags.Static);
    }

    /// <summary>One grep line: <c>patch-slots: in-use=3/32 quarantined=0 faults=0</c>.</summary>
    public static string Describe() =>
        "patch-slots: in-use=" + InUse + "/" + Capacity
        + " quarantined=" + QuarantineCount + " faults=" + FaultCount
        + " core=" + CoreInUse + " plugin-free=" + NonCoreFree + "/" + NonCoreCapacity
        + " reserve=" + CoreReserve + (CoreReserveIntact ? "" : " RESERVE-BREACHED");

    private static bool Pre(int index, object? instance, object?[] args)
    {
        var body = Volatile.Read(ref Bodies[index]);
        if (body == null) return true;
        try
        {
            var result = body(instance, args ?? Array.Empty<object?>(), null);
            return result is bool b ? b : true;
        }
        catch
        {
            Interlocked.Increment(ref Faults[index]);
            return true;
        }
    }

    private static void Post(int index, object? instance, object?[] args)
    {
        var body = Volatile.Read(ref Bodies[index]);
        if (body == null) return;
        try { body(instance, args ?? Array.Empty<object?>(), null); }
        catch { Interlocked.Increment(ref Faults[index]); }
    }

    private static Exception? Fin(int index, object? instance, object?[] args, Exception? exception)
    {
        var body = Volatile.Read(ref Bodies[index]);
        if (body == null) return exception;
        try { return body(instance, args ?? Array.Empty<object?>(), exception) as Exception; }
        catch
        {
            Interlocked.Increment(ref Faults[index]);
            return exception;
        }
    }

    // ---- The pool. Generated shape, six methods per slot; nothing below this line has any
    // ---- reference to a collectible assembly, which is the whole point of the file.

    /// <summary>Prefix body for slot 00, instance target.</summary>
    public static bool Prefix_I00(object __instance, object[] __args) => Pre(0, __instance, __args);
    /// <summary>Prefix body for slot 00, static target.</summary>
    public static bool Prefix_S00(object[] __args) => Pre(0, null, __args);
    /// <summary>Postfix body for slot 00, instance target.</summary>
    public static void Postfix_I00(object __instance, object[] __args) => Post(0, __instance, __args);
    /// <summary>Postfix body for slot 00, static target.</summary>
    public static void Postfix_S00(object[] __args) => Post(0, null, __args);
    /// <summary>Finalizer body for slot 00, instance target.</summary>
    public static Exception? Finalizer_I00(object __instance, object[] __args, Exception __exception) => Fin(0, __instance, __args, __exception);
    /// <summary>Finalizer body for slot 00, static target.</summary>
    public static Exception? Finalizer_S00(object[] __args, Exception __exception) => Fin(0, null, __args, __exception);

    /// <summary>Prefix body for slot 01, instance target.</summary>
    public static bool Prefix_I01(object __instance, object[] __args) => Pre(1, __instance, __args);
    /// <summary>Prefix body for slot 01, static target.</summary>
    public static bool Prefix_S01(object[] __args) => Pre(1, null, __args);
    /// <summary>Postfix body for slot 01, instance target.</summary>
    public static void Postfix_I01(object __instance, object[] __args) => Post(1, __instance, __args);
    /// <summary>Postfix body for slot 01, static target.</summary>
    public static void Postfix_S01(object[] __args) => Post(1, null, __args);
    /// <summary>Finalizer body for slot 01, instance target.</summary>
    public static Exception? Finalizer_I01(object __instance, object[] __args, Exception __exception) => Fin(1, __instance, __args, __exception);
    /// <summary>Finalizer body for slot 01, static target.</summary>
    public static Exception? Finalizer_S01(object[] __args, Exception __exception) => Fin(1, null, __args, __exception);

    /// <summary>Prefix body for slot 02, instance target.</summary>
    public static bool Prefix_I02(object __instance, object[] __args) => Pre(2, __instance, __args);
    /// <summary>Prefix body for slot 02, static target.</summary>
    public static bool Prefix_S02(object[] __args) => Pre(2, null, __args);
    /// <summary>Postfix body for slot 02, instance target.</summary>
    public static void Postfix_I02(object __instance, object[] __args) => Post(2, __instance, __args);
    /// <summary>Postfix body for slot 02, static target.</summary>
    public static void Postfix_S02(object[] __args) => Post(2, null, __args);
    /// <summary>Finalizer body for slot 02, instance target.</summary>
    public static Exception? Finalizer_I02(object __instance, object[] __args, Exception __exception) => Fin(2, __instance, __args, __exception);
    /// <summary>Finalizer body for slot 02, static target.</summary>
    public static Exception? Finalizer_S02(object[] __args, Exception __exception) => Fin(2, null, __args, __exception);

    /// <summary>Prefix body for slot 03, instance target.</summary>
    public static bool Prefix_I03(object __instance, object[] __args) => Pre(3, __instance, __args);
    /// <summary>Prefix body for slot 03, static target.</summary>
    public static bool Prefix_S03(object[] __args) => Pre(3, null, __args);
    /// <summary>Postfix body for slot 03, instance target.</summary>
    public static void Postfix_I03(object __instance, object[] __args) => Post(3, __instance, __args);
    /// <summary>Postfix body for slot 03, static target.</summary>
    public static void Postfix_S03(object[] __args) => Post(3, null, __args);
    /// <summary>Finalizer body for slot 03, instance target.</summary>
    public static Exception? Finalizer_I03(object __instance, object[] __args, Exception __exception) => Fin(3, __instance, __args, __exception);
    /// <summary>Finalizer body for slot 03, static target.</summary>
    public static Exception? Finalizer_S03(object[] __args, Exception __exception) => Fin(3, null, __args, __exception);

    /// <summary>Prefix body for slot 04, instance target.</summary>
    public static bool Prefix_I04(object __instance, object[] __args) => Pre(4, __instance, __args);
    /// <summary>Prefix body for slot 04, static target.</summary>
    public static bool Prefix_S04(object[] __args) => Pre(4, null, __args);
    /// <summary>Postfix body for slot 04, instance target.</summary>
    public static void Postfix_I04(object __instance, object[] __args) => Post(4, __instance, __args);
    /// <summary>Postfix body for slot 04, static target.</summary>
    public static void Postfix_S04(object[] __args) => Post(4, null, __args);
    /// <summary>Finalizer body for slot 04, instance target.</summary>
    public static Exception? Finalizer_I04(object __instance, object[] __args, Exception __exception) => Fin(4, __instance, __args, __exception);
    /// <summary>Finalizer body for slot 04, static target.</summary>
    public static Exception? Finalizer_S04(object[] __args, Exception __exception) => Fin(4, null, __args, __exception);

    /// <summary>Prefix body for slot 05, instance target.</summary>
    public static bool Prefix_I05(object __instance, object[] __args) => Pre(5, __instance, __args);
    /// <summary>Prefix body for slot 05, static target.</summary>
    public static bool Prefix_S05(object[] __args) => Pre(5, null, __args);
    /// <summary>Postfix body for slot 05, instance target.</summary>
    public static void Postfix_I05(object __instance, object[] __args) => Post(5, __instance, __args);
    /// <summary>Postfix body for slot 05, static target.</summary>
    public static void Postfix_S05(object[] __args) => Post(5, null, __args);
    /// <summary>Finalizer body for slot 05, instance target.</summary>
    public static Exception? Finalizer_I05(object __instance, object[] __args, Exception __exception) => Fin(5, __instance, __args, __exception);
    /// <summary>Finalizer body for slot 05, static target.</summary>
    public static Exception? Finalizer_S05(object[] __args, Exception __exception) => Fin(5, null, __args, __exception);

    /// <summary>Prefix body for slot 06, instance target.</summary>
    public static bool Prefix_I06(object __instance, object[] __args) => Pre(6, __instance, __args);
    /// <summary>Prefix body for slot 06, static target.</summary>
    public static bool Prefix_S06(object[] __args) => Pre(6, null, __args);
    /// <summary>Postfix body for slot 06, instance target.</summary>
    public static void Postfix_I06(object __instance, object[] __args) => Post(6, __instance, __args);
    /// <summary>Postfix body for slot 06, static target.</summary>
    public static void Postfix_S06(object[] __args) => Post(6, null, __args);
    /// <summary>Finalizer body for slot 06, instance target.</summary>
    public static Exception? Finalizer_I06(object __instance, object[] __args, Exception __exception) => Fin(6, __instance, __args, __exception);
    /// <summary>Finalizer body for slot 06, static target.</summary>
    public static Exception? Finalizer_S06(object[] __args, Exception __exception) => Fin(6, null, __args, __exception);

    /// <summary>Prefix body for slot 07, instance target.</summary>
    public static bool Prefix_I07(object __instance, object[] __args) => Pre(7, __instance, __args);
    /// <summary>Prefix body for slot 07, static target.</summary>
    public static bool Prefix_S07(object[] __args) => Pre(7, null, __args);
    /// <summary>Postfix body for slot 07, instance target.</summary>
    public static void Postfix_I07(object __instance, object[] __args) => Post(7, __instance, __args);
    /// <summary>Postfix body for slot 07, static target.</summary>
    public static void Postfix_S07(object[] __args) => Post(7, null, __args);
    /// <summary>Finalizer body for slot 07, instance target.</summary>
    public static Exception? Finalizer_I07(object __instance, object[] __args, Exception __exception) => Fin(7, __instance, __args, __exception);
    /// <summary>Finalizer body for slot 07, static target.</summary>
    public static Exception? Finalizer_S07(object[] __args, Exception __exception) => Fin(7, null, __args, __exception);

    /// <summary>Prefix body for slot 08, instance target.</summary>
    public static bool Prefix_I08(object __instance, object[] __args) => Pre(8, __instance, __args);
    /// <summary>Prefix body for slot 08, static target.</summary>
    public static bool Prefix_S08(object[] __args) => Pre(8, null, __args);
    /// <summary>Postfix body for slot 08, instance target.</summary>
    public static void Postfix_I08(object __instance, object[] __args) => Post(8, __instance, __args);
    /// <summary>Postfix body for slot 08, static target.</summary>
    public static void Postfix_S08(object[] __args) => Post(8, null, __args);
    /// <summary>Finalizer body for slot 08, instance target.</summary>
    public static Exception? Finalizer_I08(object __instance, object[] __args, Exception __exception) => Fin(8, __instance, __args, __exception);
    /// <summary>Finalizer body for slot 08, static target.</summary>
    public static Exception? Finalizer_S08(object[] __args, Exception __exception) => Fin(8, null, __args, __exception);

    /// <summary>Prefix body for slot 09, instance target.</summary>
    public static bool Prefix_I09(object __instance, object[] __args) => Pre(9, __instance, __args);
    /// <summary>Prefix body for slot 09, static target.</summary>
    public static bool Prefix_S09(object[] __args) => Pre(9, null, __args);
    /// <summary>Postfix body for slot 09, instance target.</summary>
    public static void Postfix_I09(object __instance, object[] __args) => Post(9, __instance, __args);
    /// <summary>Postfix body for slot 09, static target.</summary>
    public static void Postfix_S09(object[] __args) => Post(9, null, __args);
    /// <summary>Finalizer body for slot 09, instance target.</summary>
    public static Exception? Finalizer_I09(object __instance, object[] __args, Exception __exception) => Fin(9, __instance, __args, __exception);
    /// <summary>Finalizer body for slot 09, static target.</summary>
    public static Exception? Finalizer_S09(object[] __args, Exception __exception) => Fin(9, null, __args, __exception);

    /// <summary>Prefix body for slot 10, instance target.</summary>
    public static bool Prefix_I10(object __instance, object[] __args) => Pre(10, __instance, __args);
    /// <summary>Prefix body for slot 10, static target.</summary>
    public static bool Prefix_S10(object[] __args) => Pre(10, null, __args);
    /// <summary>Postfix body for slot 10, instance target.</summary>
    public static void Postfix_I10(object __instance, object[] __args) => Post(10, __instance, __args);
    /// <summary>Postfix body for slot 10, static target.</summary>
    public static void Postfix_S10(object[] __args) => Post(10, null, __args);
    /// <summary>Finalizer body for slot 10, instance target.</summary>
    public static Exception? Finalizer_I10(object __instance, object[] __args, Exception __exception) => Fin(10, __instance, __args, __exception);
    /// <summary>Finalizer body for slot 10, static target.</summary>
    public static Exception? Finalizer_S10(object[] __args, Exception __exception) => Fin(10, null, __args, __exception);

    /// <summary>Prefix body for slot 11, instance target.</summary>
    public static bool Prefix_I11(object __instance, object[] __args) => Pre(11, __instance, __args);
    /// <summary>Prefix body for slot 11, static target.</summary>
    public static bool Prefix_S11(object[] __args) => Pre(11, null, __args);
    /// <summary>Postfix body for slot 11, instance target.</summary>
    public static void Postfix_I11(object __instance, object[] __args) => Post(11, __instance, __args);
    /// <summary>Postfix body for slot 11, static target.</summary>
    public static void Postfix_S11(object[] __args) => Post(11, null, __args);
    /// <summary>Finalizer body for slot 11, instance target.</summary>
    public static Exception? Finalizer_I11(object __instance, object[] __args, Exception __exception) => Fin(11, __instance, __args, __exception);
    /// <summary>Finalizer body for slot 11, static target.</summary>
    public static Exception? Finalizer_S11(object[] __args, Exception __exception) => Fin(11, null, __args, __exception);

    /// <summary>Prefix body for slot 12, instance target.</summary>
    public static bool Prefix_I12(object __instance, object[] __args) => Pre(12, __instance, __args);
    /// <summary>Prefix body for slot 12, static target.</summary>
    public static bool Prefix_S12(object[] __args) => Pre(12, null, __args);
    /// <summary>Postfix body for slot 12, instance target.</summary>
    public static void Postfix_I12(object __instance, object[] __args) => Post(12, __instance, __args);
    /// <summary>Postfix body for slot 12, static target.</summary>
    public static void Postfix_S12(object[] __args) => Post(12, null, __args);
    /// <summary>Finalizer body for slot 12, instance target.</summary>
    public static Exception? Finalizer_I12(object __instance, object[] __args, Exception __exception) => Fin(12, __instance, __args, __exception);
    /// <summary>Finalizer body for slot 12, static target.</summary>
    public static Exception? Finalizer_S12(object[] __args, Exception __exception) => Fin(12, null, __args, __exception);

    /// <summary>Prefix body for slot 13, instance target.</summary>
    public static bool Prefix_I13(object __instance, object[] __args) => Pre(13, __instance, __args);
    /// <summary>Prefix body for slot 13, static target.</summary>
    public static bool Prefix_S13(object[] __args) => Pre(13, null, __args);
    /// <summary>Postfix body for slot 13, instance target.</summary>
    public static void Postfix_I13(object __instance, object[] __args) => Post(13, __instance, __args);
    /// <summary>Postfix body for slot 13, static target.</summary>
    public static void Postfix_S13(object[] __args) => Post(13, null, __args);
    /// <summary>Finalizer body for slot 13, instance target.</summary>
    public static Exception? Finalizer_I13(object __instance, object[] __args, Exception __exception) => Fin(13, __instance, __args, __exception);
    /// <summary>Finalizer body for slot 13, static target.</summary>
    public static Exception? Finalizer_S13(object[] __args, Exception __exception) => Fin(13, null, __args, __exception);

    /// <summary>Prefix body for slot 14, instance target.</summary>
    public static bool Prefix_I14(object __instance, object[] __args) => Pre(14, __instance, __args);
    /// <summary>Prefix body for slot 14, static target.</summary>
    public static bool Prefix_S14(object[] __args) => Pre(14, null, __args);
    /// <summary>Postfix body for slot 14, instance target.</summary>
    public static void Postfix_I14(object __instance, object[] __args) => Post(14, __instance, __args);
    /// <summary>Postfix body for slot 14, static target.</summary>
    public static void Postfix_S14(object[] __args) => Post(14, null, __args);
    /// <summary>Finalizer body for slot 14, instance target.</summary>
    public static Exception? Finalizer_I14(object __instance, object[] __args, Exception __exception) => Fin(14, __instance, __args, __exception);
    /// <summary>Finalizer body for slot 14, static target.</summary>
    public static Exception? Finalizer_S14(object[] __args, Exception __exception) => Fin(14, null, __args, __exception);

    /// <summary>Prefix body for slot 15, instance target.</summary>
    public static bool Prefix_I15(object __instance, object[] __args) => Pre(15, __instance, __args);
    /// <summary>Prefix body for slot 15, static target.</summary>
    public static bool Prefix_S15(object[] __args) => Pre(15, null, __args);
    /// <summary>Postfix body for slot 15, instance target.</summary>
    public static void Postfix_I15(object __instance, object[] __args) => Post(15, __instance, __args);
    /// <summary>Postfix body for slot 15, static target.</summary>
    public static void Postfix_S15(object[] __args) => Post(15, null, __args);
    /// <summary>Finalizer body for slot 15, instance target.</summary>
    public static Exception? Finalizer_I15(object __instance, object[] __args, Exception __exception) => Fin(15, __instance, __args, __exception);
    /// <summary>Finalizer body for slot 15, static target.</summary>
    public static Exception? Finalizer_S15(object[] __args, Exception __exception) => Fin(15, null, __args, __exception);

    /// <summary>Prefix body for slot 16, instance target.</summary>
    public static bool Prefix_I16(object __instance, object[] __args) => Pre(16, __instance, __args);
    /// <summary>Prefix body for slot 16, static target.</summary>
    public static bool Prefix_S16(object[] __args) => Pre(16, null, __args);
    /// <summary>Postfix body for slot 16, instance target.</summary>
    public static void Postfix_I16(object __instance, object[] __args) => Post(16, __instance, __args);
    /// <summary>Postfix body for slot 16, static target.</summary>
    public static void Postfix_S16(object[] __args) => Post(16, null, __args);
    /// <summary>Finalizer body for slot 16, instance target.</summary>
    public static Exception? Finalizer_I16(object __instance, object[] __args, Exception __exception) => Fin(16, __instance, __args, __exception);
    /// <summary>Finalizer body for slot 16, static target.</summary>
    public static Exception? Finalizer_S16(object[] __args, Exception __exception) => Fin(16, null, __args, __exception);

    /// <summary>Prefix body for slot 17, instance target.</summary>
    public static bool Prefix_I17(object __instance, object[] __args) => Pre(17, __instance, __args);
    /// <summary>Prefix body for slot 17, static target.</summary>
    public static bool Prefix_S17(object[] __args) => Pre(17, null, __args);
    /// <summary>Postfix body for slot 17, instance target.</summary>
    public static void Postfix_I17(object __instance, object[] __args) => Post(17, __instance, __args);
    /// <summary>Postfix body for slot 17, static target.</summary>
    public static void Postfix_S17(object[] __args) => Post(17, null, __args);
    /// <summary>Finalizer body for slot 17, instance target.</summary>
    public static Exception? Finalizer_I17(object __instance, object[] __args, Exception __exception) => Fin(17, __instance, __args, __exception);
    /// <summary>Finalizer body for slot 17, static target.</summary>
    public static Exception? Finalizer_S17(object[] __args, Exception __exception) => Fin(17, null, __args, __exception);

    /// <summary>Prefix body for slot 18, instance target.</summary>
    public static bool Prefix_I18(object __instance, object[] __args) => Pre(18, __instance, __args);
    /// <summary>Prefix body for slot 18, static target.</summary>
    public static bool Prefix_S18(object[] __args) => Pre(18, null, __args);
    /// <summary>Postfix body for slot 18, instance target.</summary>
    public static void Postfix_I18(object __instance, object[] __args) => Post(18, __instance, __args);
    /// <summary>Postfix body for slot 18, static target.</summary>
    public static void Postfix_S18(object[] __args) => Post(18, null, __args);
    /// <summary>Finalizer body for slot 18, instance target.</summary>
    public static Exception? Finalizer_I18(object __instance, object[] __args, Exception __exception) => Fin(18, __instance, __args, __exception);
    /// <summary>Finalizer body for slot 18, static target.</summary>
    public static Exception? Finalizer_S18(object[] __args, Exception __exception) => Fin(18, null, __args, __exception);

    /// <summary>Prefix body for slot 19, instance target.</summary>
    public static bool Prefix_I19(object __instance, object[] __args) => Pre(19, __instance, __args);
    /// <summary>Prefix body for slot 19, static target.</summary>
    public static bool Prefix_S19(object[] __args) => Pre(19, null, __args);
    /// <summary>Postfix body for slot 19, instance target.</summary>
    public static void Postfix_I19(object __instance, object[] __args) => Post(19, __instance, __args);
    /// <summary>Postfix body for slot 19, static target.</summary>
    public static void Postfix_S19(object[] __args) => Post(19, null, __args);
    /// <summary>Finalizer body for slot 19, instance target.</summary>
    public static Exception? Finalizer_I19(object __instance, object[] __args, Exception __exception) => Fin(19, __instance, __args, __exception);
    /// <summary>Finalizer body for slot 19, static target.</summary>
    public static Exception? Finalizer_S19(object[] __args, Exception __exception) => Fin(19, null, __args, __exception);

    /// <summary>Prefix body for slot 20, instance target.</summary>
    public static bool Prefix_I20(object __instance, object[] __args) => Pre(20, __instance, __args);
    /// <summary>Prefix body for slot 20, static target.</summary>
    public static bool Prefix_S20(object[] __args) => Pre(20, null, __args);
    /// <summary>Postfix body for slot 20, instance target.</summary>
    public static void Postfix_I20(object __instance, object[] __args) => Post(20, __instance, __args);
    /// <summary>Postfix body for slot 20, static target.</summary>
    public static void Postfix_S20(object[] __args) => Post(20, null, __args);
    /// <summary>Finalizer body for slot 20, instance target.</summary>
    public static Exception? Finalizer_I20(object __instance, object[] __args, Exception __exception) => Fin(20, __instance, __args, __exception);
    /// <summary>Finalizer body for slot 20, static target.</summary>
    public static Exception? Finalizer_S20(object[] __args, Exception __exception) => Fin(20, null, __args, __exception);

    /// <summary>Prefix body for slot 21, instance target.</summary>
    public static bool Prefix_I21(object __instance, object[] __args) => Pre(21, __instance, __args);
    /// <summary>Prefix body for slot 21, static target.</summary>
    public static bool Prefix_S21(object[] __args) => Pre(21, null, __args);
    /// <summary>Postfix body for slot 21, instance target.</summary>
    public static void Postfix_I21(object __instance, object[] __args) => Post(21, __instance, __args);
    /// <summary>Postfix body for slot 21, static target.</summary>
    public static void Postfix_S21(object[] __args) => Post(21, null, __args);
    /// <summary>Finalizer body for slot 21, instance target.</summary>
    public static Exception? Finalizer_I21(object __instance, object[] __args, Exception __exception) => Fin(21, __instance, __args, __exception);
    /// <summary>Finalizer body for slot 21, static target.</summary>
    public static Exception? Finalizer_S21(object[] __args, Exception __exception) => Fin(21, null, __args, __exception);

    /// <summary>Prefix body for slot 22, instance target.</summary>
    public static bool Prefix_I22(object __instance, object[] __args) => Pre(22, __instance, __args);
    /// <summary>Prefix body for slot 22, static target.</summary>
    public static bool Prefix_S22(object[] __args) => Pre(22, null, __args);
    /// <summary>Postfix body for slot 22, instance target.</summary>
    public static void Postfix_I22(object __instance, object[] __args) => Post(22, __instance, __args);
    /// <summary>Postfix body for slot 22, static target.</summary>
    public static void Postfix_S22(object[] __args) => Post(22, null, __args);
    /// <summary>Finalizer body for slot 22, instance target.</summary>
    public static Exception? Finalizer_I22(object __instance, object[] __args, Exception __exception) => Fin(22, __instance, __args, __exception);
    /// <summary>Finalizer body for slot 22, static target.</summary>
    public static Exception? Finalizer_S22(object[] __args, Exception __exception) => Fin(22, null, __args, __exception);

    /// <summary>Prefix body for slot 23, instance target.</summary>
    public static bool Prefix_I23(object __instance, object[] __args) => Pre(23, __instance, __args);
    /// <summary>Prefix body for slot 23, static target.</summary>
    public static bool Prefix_S23(object[] __args) => Pre(23, null, __args);
    /// <summary>Postfix body for slot 23, instance target.</summary>
    public static void Postfix_I23(object __instance, object[] __args) => Post(23, __instance, __args);
    /// <summary>Postfix body for slot 23, static target.</summary>
    public static void Postfix_S23(object[] __args) => Post(23, null, __args);
    /// <summary>Finalizer body for slot 23, instance target.</summary>
    public static Exception? Finalizer_I23(object __instance, object[] __args, Exception __exception) => Fin(23, __instance, __args, __exception);
    /// <summary>Finalizer body for slot 23, static target.</summary>
    public static Exception? Finalizer_S23(object[] __args, Exception __exception) => Fin(23, null, __args, __exception);

    /// <summary>Prefix body for slot 24, instance target.</summary>
    public static bool Prefix_I24(object __instance, object[] __args) => Pre(24, __instance, __args);
    /// <summary>Prefix body for slot 24, static target.</summary>
    public static bool Prefix_S24(object[] __args) => Pre(24, null, __args);
    /// <summary>Postfix body for slot 24, instance target.</summary>
    public static void Postfix_I24(object __instance, object[] __args) => Post(24, __instance, __args);
    /// <summary>Postfix body for slot 24, static target.</summary>
    public static void Postfix_S24(object[] __args) => Post(24, null, __args);
    /// <summary>Finalizer body for slot 24, instance target.</summary>
    public static Exception? Finalizer_I24(object __instance, object[] __args, Exception __exception) => Fin(24, __instance, __args, __exception);
    /// <summary>Finalizer body for slot 24, static target.</summary>
    public static Exception? Finalizer_S24(object[] __args, Exception __exception) => Fin(24, null, __args, __exception);

    /// <summary>Prefix body for slot 25, instance target.</summary>
    public static bool Prefix_I25(object __instance, object[] __args) => Pre(25, __instance, __args);
    /// <summary>Prefix body for slot 25, static target.</summary>
    public static bool Prefix_S25(object[] __args) => Pre(25, null, __args);
    /// <summary>Postfix body for slot 25, instance target.</summary>
    public static void Postfix_I25(object __instance, object[] __args) => Post(25, __instance, __args);
    /// <summary>Postfix body for slot 25, static target.</summary>
    public static void Postfix_S25(object[] __args) => Post(25, null, __args);
    /// <summary>Finalizer body for slot 25, instance target.</summary>
    public static Exception? Finalizer_I25(object __instance, object[] __args, Exception __exception) => Fin(25, __instance, __args, __exception);
    /// <summary>Finalizer body for slot 25, static target.</summary>
    public static Exception? Finalizer_S25(object[] __args, Exception __exception) => Fin(25, null, __args, __exception);

    /// <summary>Prefix body for slot 26, instance target.</summary>
    public static bool Prefix_I26(object __instance, object[] __args) => Pre(26, __instance, __args);
    /// <summary>Prefix body for slot 26, static target.</summary>
    public static bool Prefix_S26(object[] __args) => Pre(26, null, __args);
    /// <summary>Postfix body for slot 26, instance target.</summary>
    public static void Postfix_I26(object __instance, object[] __args) => Post(26, __instance, __args);
    /// <summary>Postfix body for slot 26, static target.</summary>
    public static void Postfix_S26(object[] __args) => Post(26, null, __args);
    /// <summary>Finalizer body for slot 26, instance target.</summary>
    public static Exception? Finalizer_I26(object __instance, object[] __args, Exception __exception) => Fin(26, __instance, __args, __exception);
    /// <summary>Finalizer body for slot 26, static target.</summary>
    public static Exception? Finalizer_S26(object[] __args, Exception __exception) => Fin(26, null, __args, __exception);

    /// <summary>Prefix body for slot 27, instance target.</summary>
    public static bool Prefix_I27(object __instance, object[] __args) => Pre(27, __instance, __args);
    /// <summary>Prefix body for slot 27, static target.</summary>
    public static bool Prefix_S27(object[] __args) => Pre(27, null, __args);
    /// <summary>Postfix body for slot 27, instance target.</summary>
    public static void Postfix_I27(object __instance, object[] __args) => Post(27, __instance, __args);
    /// <summary>Postfix body for slot 27, static target.</summary>
    public static void Postfix_S27(object[] __args) => Post(27, null, __args);
    /// <summary>Finalizer body for slot 27, instance target.</summary>
    public static Exception? Finalizer_I27(object __instance, object[] __args, Exception __exception) => Fin(27, __instance, __args, __exception);
    /// <summary>Finalizer body for slot 27, static target.</summary>
    public static Exception? Finalizer_S27(object[] __args, Exception __exception) => Fin(27, null, __args, __exception);

    /// <summary>Prefix body for slot 28, instance target.</summary>
    public static bool Prefix_I28(object __instance, object[] __args) => Pre(28, __instance, __args);
    /// <summary>Prefix body for slot 28, static target.</summary>
    public static bool Prefix_S28(object[] __args) => Pre(28, null, __args);
    /// <summary>Postfix body for slot 28, instance target.</summary>
    public static void Postfix_I28(object __instance, object[] __args) => Post(28, __instance, __args);
    /// <summary>Postfix body for slot 28, static target.</summary>
    public static void Postfix_S28(object[] __args) => Post(28, null, __args);
    /// <summary>Finalizer body for slot 28, instance target.</summary>
    public static Exception? Finalizer_I28(object __instance, object[] __args, Exception __exception) => Fin(28, __instance, __args, __exception);
    /// <summary>Finalizer body for slot 28, static target.</summary>
    public static Exception? Finalizer_S28(object[] __args, Exception __exception) => Fin(28, null, __args, __exception);

    /// <summary>Prefix body for slot 29, instance target.</summary>
    public static bool Prefix_I29(object __instance, object[] __args) => Pre(29, __instance, __args);
    /// <summary>Prefix body for slot 29, static target.</summary>
    public static bool Prefix_S29(object[] __args) => Pre(29, null, __args);
    /// <summary>Postfix body for slot 29, instance target.</summary>
    public static void Postfix_I29(object __instance, object[] __args) => Post(29, __instance, __args);
    /// <summary>Postfix body for slot 29, static target.</summary>
    public static void Postfix_S29(object[] __args) => Post(29, null, __args);
    /// <summary>Finalizer body for slot 29, instance target.</summary>
    public static Exception? Finalizer_I29(object __instance, object[] __args, Exception __exception) => Fin(29, __instance, __args, __exception);
    /// <summary>Finalizer body for slot 29, static target.</summary>
    public static Exception? Finalizer_S29(object[] __args, Exception __exception) => Fin(29, null, __args, __exception);

    /// <summary>Prefix body for slot 30, instance target.</summary>
    public static bool Prefix_I30(object __instance, object[] __args) => Pre(30, __instance, __args);
    /// <summary>Prefix body for slot 30, static target.</summary>
    public static bool Prefix_S30(object[] __args) => Pre(30, null, __args);
    /// <summary>Postfix body for slot 30, instance target.</summary>
    public static void Postfix_I30(object __instance, object[] __args) => Post(30, __instance, __args);
    /// <summary>Postfix body for slot 30, static target.</summary>
    public static void Postfix_S30(object[] __args) => Post(30, null, __args);
    /// <summary>Finalizer body for slot 30, instance target.</summary>
    public static Exception? Finalizer_I30(object __instance, object[] __args, Exception __exception) => Fin(30, __instance, __args, __exception);
    /// <summary>Finalizer body for slot 30, static target.</summary>
    public static Exception? Finalizer_S30(object[] __args, Exception __exception) => Fin(30, null, __args, __exception);

    /// <summary>Prefix body for slot 31, instance target.</summary>
    public static bool Prefix_I31(object __instance, object[] __args) => Pre(31, __instance, __args);
    /// <summary>Prefix body for slot 31, static target.</summary>
    public static bool Prefix_S31(object[] __args) => Pre(31, null, __args);
    /// <summary>Postfix body for slot 31, instance target.</summary>
    public static void Postfix_I31(object __instance, object[] __args) => Post(31, __instance, __args);
    /// <summary>Postfix body for slot 31, static target.</summary>
    public static void Postfix_S31(object[] __args) => Post(31, null, __args);
    /// <summary>Finalizer body for slot 31, instance target.</summary>
    public static Exception? Finalizer_I31(object __instance, object[] __args, Exception __exception) => Fin(31, __instance, __args, __exception);
    /// <summary>Finalizer body for slot 31, static target.</summary>
    public static Exception? Finalizer_S31(object[] __args, Exception __exception) => Fin(31, null, __args, __exception);
}
