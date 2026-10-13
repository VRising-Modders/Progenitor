using System;
using System.Collections.Generic;
using System.Reflection;

namespace Progenitor.Patches;

/// <summary>
/// Per-plugin Harmony patch registry with an unload-safe unpatch (job FHARM).
///
/// <para><b>Why Core owns this.</b> Core is loaded into a collectible <c>AssemblyLoadContext</c>
/// and swapped without restarting the game. Handing HarmonyX a <see cref="MethodInfo"/> from a
/// collectible ALC pins that ALC <b>for the life of the process</b>, and the unpatch does not
/// undo it: <c>StackTraceFixes.RealMethodMap</c> and (for IL2CPP) <c>DelegateCache</c> are both
/// append-only (measured; details are in the internal research notes). So the
/// unpatch is <em>not</em> the guarantee: it is what stops a dead ALC's code from being
/// <em>called</em>. The guarantee is that HarmonyX is never handed a collectible method at
/// all.</para>
///
/// <para><b>Which is why every registration routes through a Bootstrap slot</b> (job FHARM6,
/// R40 finding 1). Plugin patches go through <see cref="PrefixSlot"/> and friends, and Core's
/// own patches (the world-load epoch postfix, god mode's three kill prefixes) go through the
/// <em>same</em> pool under the owner id <c>&lt;guid&gt;.core</c>. Core is collectible too, so a
/// Core-owned direct patch pins the Core generation exactly as hard as a plugin's pins the
/// plugin's; a slot's body belongs to <c>Progenitor.Bootstrap</c>, which is not collectible.
/// <see cref="Prefix"/> / <see cref="Postfix"/> / <see cref="Finalizer"/> (the direct path)
/// remain for tests and for a caller that has measured that it can accept a permanent pin, and
/// every direct registration now logs a warning naming that pin, so the leak is a grep rather
/// than a silence. <c>RemoveAll</c> and <see cref="Uninstall"/> still run on every unload
/// reason, because a call into a dead ALC is a crash whether or not the ALC would have
/// collected.</para>
///
/// <para><b>Prior art.</b> BepInEx 5/6 has no plugin unload at all — a chainloader plugin lives
/// for the process, so its Harmony patches never have to come off and the pin never appears;
/// the BepInEx plugin-unloading discussions land on "restart the game" for exactly this reason.
/// Progenitor is the odd one out (see <c>Plugins/PluginUnloadReason.cs</c>), so the
/// unpatch has to be a first-class, tagged, swept operation rather than an afterthought.</para>
///
/// <para><b>Unload reason is deliberately ignored.</b> Unlike live game state (a buff instance),
/// a patch has nothing to keep across a swap: the plugin assembly is going away in every
/// reason, including <see cref="Plugins.PluginUnloadReason.Swap"/>, and a patch whose method
/// lives in a dead ALC is a crash, not a convenience. <c>SweepRegistrations</c> is reason-blind
/// and this belongs there.</para>
///
/// <para>Game-free: the Harmony calls live behind <see cref="IPatchApplier"/>.</para>
/// </summary>
public static class PatchRegistry
{
    private static readonly object Gate = new();
    private static readonly List<Entry> Entries = new();
    private static long _nextId;

    /// <summary>
    /// Applies patches. <c>null</c> until <c>Patches.Install()</c> attaches the Harmony applier
    /// (or a test attaches a fake). Every registration while this is null is refused with
    /// <see cref="PatchSitePolicy.ReasonNotInstalled"/> rather than silently dropped.
    /// </summary>
    public static IPatchApplier? Applier { get; set; }

    /// <summary>
    /// Describes a target before it is patched (address-library path, relative address, Burst-ness). Set by
    /// <c>Patches.Install()</c> to <c>PatchSites.Describe</c>; tests set their own. A null
    /// resolver means every site is <see cref="PatchSiteInfo.Unknown"/>, which is allowed.
    /// </summary>
    public static Func<MethodBase, PatchSiteInfo>? SiteResolver { get; set; }

    /// <summary>Live patch count across every owner.</summary>
    public static int Count
    {
        get { lock (Gate) return Entries.Count; }
    }

    /// <summary>Distinct owners with at least one live patch.</summary>
    public static int OwnerCount
    {
        get
        {
            lock (Gate)
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                for (int i = 0; i < Entries.Count; i++) seen.Add(Entries[i].OwnerId);
                return seen.Count;
            }
        }
    }

    /// <summary>One grep line for the boot log / <c>ping</c>: <c>patches: live=2 owners=1</c>.</summary>
    public static string DescribePins() => PatchSitePolicy.FormatPins(Count, OwnerCount);

    /// <summary>
    /// True when <c>Progenitor.Bootstrap</c>'s slot pool resolved, i.e. a registration may route
    /// through it. Job FHARM7: this is also how Core's own fallback sites answer "is Bootstrap
    /// there at all", which is the one condition under which a direct patch is tolerable — see
    /// <see cref="CorePatchFallbackPolicy"/>.
    /// </summary>
    public static bool SlotsAvailable => PatchSlotLink.Available;

    /// <summary>
    /// Position of the parameter named <paramref name="name"/> on <paramref name="target"/>, or
    /// -1. A slot body is handed <c>__args</c> positionally, so the name binding Harmony would
    /// have done has to happen at registration time (job FHARM6); job FHARM7 reuses it for the
    /// event hooks' slot-routed fallbacks. -1 must be a refusal at the call site, never a guessed
    /// index.
    /// </summary>
    public static int ArgIndex(MethodBase? target, string name)
    {
        if (target == null || string.IsNullOrEmpty(name)) return -1;
        try
        {
            var parameters = target.GetParameters();
            for (int i = 0; i < parameters.Length; i++)
            {
                if (string.Equals(parameters[i].Name, name, StringComparison.Ordinal)) return i;
            }
        }
        catch
        {
            return -1;
        }
        return -1;
    }

    /// <summary>Register a prefix. Returns null when the patch was refused or threw during apply.</summary>
    public static PatchHandle? Prefix(object? owner, string ownerId, MethodBase? target, MethodInfo? patch, int priority = 400) =>
        Add(owner, ownerId, target, patch, PatchKind.Prefix, priority);

    /// <summary>
    /// Register a <b>slot-routed</b> prefix (job FHARM5). The handler never reaches HarmonyX:
    /// it is parked in a <c>Progenitor.Bootstrap.PatchSlots</c> slot and Harmony is handed that
    /// slot's process-lifetime static method, so unpatching <em>and</em> releasing the slot
    /// really does free the caller's collectible ALC. This is the path <c>ctx.Patches</c> uses
    /// for every plugin patch; see <see cref="PatchSlotLink"/> for the measurement that makes
    /// it necessary and for what a slot cannot express (<c>__result</c>).
    /// </summary>
    public static PatchHandle? PrefixSlot(object? owner, string ownerId, MethodBase? target, SlotPrefix? prefix, int priority = 400) =>
        Add(owner, ownerId, target, null, PatchKind.Prefix, priority, prefix);

    /// <summary>Slot-routed postfix. See <see cref="PrefixSlot"/>.</summary>
    public static PatchHandle? PostfixSlot(object? owner, string ownerId, MethodBase? target, SlotPostfix? postfix, int priority = 400) =>
        Add(owner, ownerId, target, null, PatchKind.Postfix, priority, postfix);

    /// <summary>Slot-routed finalizer. See <see cref="PrefixSlot"/>.</summary>
    public static PatchHandle? FinalizerSlot(object? owner, string ownerId, MethodBase? target, SlotFinalizer? finalizer, int priority = 400) =>
        Add(owner, ownerId, target, null, PatchKind.Finalizer, priority, finalizer);

    /// <summary>
    /// Slot-routed registration from a <see cref="MethodInfo"/> rather than a delegate: the
    /// method must be static and have the slot shape for <paramref name="kind"/>, or the patch
    /// is refused with <see cref="PatchSitePolicy.ReasonSlotSignature"/>.
    /// </summary>
    public static PatchHandle? Slot(object? owner, string ownerId, MethodBase? target, MethodInfo? patch, PatchKind kind, int priority = 400)
    {
        var handler = PatchSlotLink.FromMethod(kind, patch);
        if (handler == null)
        {
            string name = DescribeTarget(target);
            Log(PatchSitePolicy.FormatRefused(kind, name, ownerId ?? "", PatchSitePolicy.ReasonSlotSignature, PatchSitePolicy.UnknownImageOffset), warning: true);
            return null;
        }
        return Add(owner, ownerId ?? "", target, null, kind, priority, handler);
    }

    /// <summary>Register a postfix. Returns null when the patch was refused or threw during apply.</summary>
    public static PatchHandle? Postfix(object? owner, string ownerId, MethodBase? target, MethodInfo? patch, int priority = 400) =>
        Add(owner, ownerId, target, patch, PatchKind.Postfix, priority);

    /// <summary>Register a finalizer. Returns null when the patch was refused or threw during apply.</summary>
    public static PatchHandle? Finalizer(object? owner, string ownerId, MethodBase? target, MethodInfo? patch, int priority = 400) =>
        Add(owner, ownerId, target, patch, PatchKind.Finalizer, priority);

    /// <summary>
    /// Register several patches as <b>one unit</b>: every member applies, or none of them stays
    /// applied. The first member that is refused or throws rolls back the ones that already
    /// landed (in reverse order), logs
    /// <c>patches: group refused …</c> and returns null, so a caller whose correctness needs the
    /// whole set — <c>GodMode</c>'s two kill-path prefixes, where <c>Kill</c> alone leaves a
    /// character alive but reading as dead — never observes a half-installed group.
    ///
    /// <para>Returns one handle per request, in request order, or null when the group did not
    /// register. An empty request list is a successful empty group.</para>
    /// </summary>
    public static PatchHandle[]? All(object? owner, string ownerId, params PatchRequest[] requests)
    {
        if (requests == null || requests.Length == 0) return Array.Empty<PatchHandle>();

        var landed = new List<PatchHandle>(requests.Length);
        for (int i = 0; i < requests.Length; i++)
        {
            var request = requests[i];
            var handle = Add(
                owner, ownerId ?? "", request.Target, request.Patch, request.Kind, request.Priority,
                request.SlotHandler);
            if (handle != null)
            {
                landed.Add(handle);
                continue;
            }

            // Roll back in reverse. Each Dispose is a registry Remove, which unpatches through
            // the applier and logs its own `patches: unpatch` line, so the rollback is visible
            // rather than implied.
            int rolledBack = 0;
            for (int j = landed.Count - 1; j >= 0; j--)
            {
                try
                {
                    landed[j].Dispose();
                    rolledBack++;
                }
                catch (Exception e)
                {
                    Log("patches: group rollback FAILED id=" + landed[j].Id
                        + " owner=" + (ownerId ?? "") + ": " + e.Message
                        + " (this pins the collectible ALC)", warning: true);
                }
            }

            Log(PatchSitePolicy.FormatGroupRefused(
                    ownerId ?? "",
                    request.LabelOrTarget(DescribeTarget(request.Target)),
                    rolledBack,
                    requests.Length),
                warning: true);
            return null;
        }

        return landed.ToArray();
    }

    /// <summary>
    /// The whole apply path: resolve the site, run the refusal gate, apply, register. A refusal
    /// or a throwing apply logs one line and returns null — the caller gets no handle and
    /// <b>the registry holds no entry</b>, so there is nothing for a later sweep to trip over.
    ///
    /// <para><b>What "fail closed" does and does not promise.</b> It is a statement about
    /// <em>this registry</em>: on any failure there is no <see cref="Entry"/>, no
    /// <see cref="PatchHandle"/> and nothing counted by <see cref="Count"/>. It is <b>not</b> a
    /// statement about HarmonyX's internal state. <c>Harmony.Patch</c> records the patch against
    /// the original's <c>PatchInfo</c> in the process-lifetime <c>0Harmony</c> assembly before it
    /// compiles and installs the replacement, so an apply that throws part-way can leave state
    /// there that the registry never had a token for. A throwing apply is therefore followed by a
    /// best-effort <c>applier.Remove</c> (HarmonyX <c>Unpatch</c> is addressed by
    /// <c>(original, patchMethod)</c>, which we still have), logged either way. If that
    /// best-effort undo is itself refused, the collectible ALC may still be pinned by a patch the
    /// registry believes does not exist and <c>patches: Uninstall removed=0 left=0</c> will look
    /// clean; <c>harmony-patches.md</c> §5.4's live <c>pin-diag Harmony core-id patches=0</c>
    /// check after a deliberately-failing apply is the only thing that can settle it.</para>
    /// </summary>
    private static PatchHandle? Add(
        object? owner, string ownerId, MethodBase? target, MethodInfo? patch, PatchKind kind, int priority,
        Delegate? slotHandler = null)
    {
        ownerId ??= "";
        string name = DescribeTarget(target);

        if (target == null || (patch == null && slotHandler == null))
        {
            Log(PatchSitePolicy.FormatRefused(kind, name, ownerId, PatchSitePolicy.ReasonNoTarget, PatchSitePolicy.UnknownImageOffset), warning: true);
            return null;
        }

        string? byRefStruct = PatchSitePolicy.FindByRefValueTypeParameter(target);
        if (byRefStruct != null)
        {
            Log(PatchSitePolicy.FormatRefused(kind, name, ownerId, PatchSitePolicy.ReasonByRefValueType, PatchSitePolicy.UnknownImageOffset)
                + " Parameter '" + byRefStruct + "': this build's Il2CppInterop copies a by-ref struct "
                + "through 8 bytes and writes them back over the caller's struct on every call.",
                warning: true);
            return null;
        }

        var applier = Applier;
        if (applier == null)
        {
            Log(PatchSitePolicy.FormatRefused(kind, name, ownerId, PatchSitePolicy.ReasonNotInstalled, PatchSitePolicy.UnknownImageOffset), warning: true);
            return null;
        }

        PatchSiteInfo site;
        try
        {
            var resolver = SiteResolver;
            site = resolver != null ? resolver(target) : PatchSiteInfo.Unknown(name);
        }
        catch (Exception e)
        {
            // A resolver that throws must not decide the patch: fall back to "unknown", which
            // the gate allows. It is the stub address we are guarding against, and we did not read one.
            Log("patches: site resolve threw for " + name + ": " + e.Message, warning: true);
            site = PatchSiteInfo.Unknown(name);
        }

        if (!string.IsNullOrEmpty(site.Target)) name = site.Target;

        if (!PatchSitePolicy.MayPatch(in site, out var reason))
        {
            Log(PatchSitePolicy.FormatRefused(kind, name, ownerId, reason, site.ImageOffset), warning: true);
            return null;
        }

        // Job FHARM5. A slot-routed registration never hands HarmonyX a collectible MethodInfo:
        // Bootstrap's pool lends one of its own process-lifetime static methods, and the
        // handler is reached from it through a delegate that Undo drops. Everything below this
        // point patches with `patchMethod`, which is Bootstrap's for a slot and the caller's
        // for the direct (Core-owned) path.
        long slotToken = PatchSlotLink.NoSlot;
        MethodInfo? patchMethod = patch;
        if (slotHandler != null)
        {
            if (!PatchSlotLink.Available)
            {
                Log(PatchSitePolicy.FormatRefused(kind, name, ownerId, PatchSitePolicy.ReasonSlotsUnavailable, site.ImageOffset)
                    + PatchSitePolicy.SlotRefusalRemedy(PatchSitePolicy.ReasonSlotsUnavailable), warning: true);
                return null;
            }

            var adapted = PatchSlotLink.Adapt(kind, slotHandler);
            if (adapted == null)
            {
                Log(PatchSitePolicy.FormatRefused(kind, name, ownerId, PatchSitePolicy.ReasonSlotSignature, site.ImageOffset), warning: true);
                return null;
            }

            // Asked before the bind, because a successful bind changes both answers.
            bool reserved = PatchSlotLink.ReservedForCore(ownerId);
            bool quarantineStarved = PatchSlotLink.LostToQuarantine(ownerId);
            slotToken = PatchSlotLink.Bind(kind, adapted, ownerId, instanceTarget: !target.IsStatic, out patchMethod);
            DrainSlotWarning();
            if (slotToken < 0 || patchMethod == null)
            {
                if (slotToken >= 0) PatchSlotLink.Release(slotToken, ownerId);
                // R41 finding 2 / R42 finding 2: "the pool is full", "the free slots are Core's
                // reserve" and "the slots were stranded by failed unpatches" are three failures
                // with three different remedies, so they are three reasons — and the remedy is
                // spelled out on the line rather than left in a doc comment, because the last of
                // them is the one where the other two's advice frees nothing.
                string slotReason =
                    quarantineStarved ? PatchSitePolicy.ReasonSlotsLostToQuarantine
                    : reserved ? PatchSitePolicy.ReasonSlotsReserved
                    : PatchSitePolicy.ReasonSlotsExhausted;
                Log(PatchSitePolicy.FormatRefused(kind, name, ownerId, slotReason, site.ImageOffset)
                        + PatchSitePolicy.SlotRefusalRemedy(slotReason),
                    warning: true);
                return null;
            }
        }

        else
        {
            // The direct path hands HarmonyX a caller-owned MethodInfo, which pins the caller's
            // ALC for the life of the process even after a clean unpatch (R40 finding 1: this
            // was silent, and Core's own patches were taking it). Nothing in Progenitor takes it
            // in production any more; if something starts to, the leak is one grep.
            Log("patches: WARNING " + PatchSitePolicy.KindToken(kind) + " " + name
                + " owner=" + ownerId + " is a direct patch: HarmonyX keeps the patch method for "
                + "the life of the process, so this pins the caller's collectible ALC even after "
                + "unpatch (harmony-patches.md section 8). Register through a slot instead.",
                warning: true);
        }

        if (patchMethod == null)
        {
            Log(PatchSitePolicy.FormatRefused(kind, name, ownerId, PatchSitePolicy.ReasonNoTarget, site.ImageOffset), warning: true);
            return null;
        }

        object token;
        try
        {
            token = applier.Apply(target, patchMethod, kind, priority, ownerId);
        }
        catch (Exception e)
        {
            Log(PatchSitePolicy.FormatApplyFailed(kind, name, ownerId, e.Message), warning: true);
            SweepFailedApply(applier, target, patchMethod, kind, ownerId, name);
            if (slotToken >= 0) PatchSlotLink.Release(slotToken, ownerId);
            return null;
        }

        Entry entry;
        lock (Gate)
        {
            entry = new Entry(++_nextId, owner, ownerId, target, patchMethod, kind, name, token, slotToken);
            Entries.Add(entry);
        }

        Log(PatchSitePolicy.FormatApplied(kind, name, ownerId, site.Path, site.ImageOffset, priority, entry.Id), warning: false);
        return new PatchHandle(entry.Id);
    }

    /// <summary>Undo one patch by handle id. True when an entry was removed.</summary>
    public static bool Remove(long id)
    {
        Entry? found = null;
        lock (Gate)
        {
            for (int i = 0; i < Entries.Count; i++)
            {
                if (Entries[i].Id != id) continue;
                found = Entries[i];
                Entries.RemoveAt(i);
                break;
            }
        }
        if (found == null) return false;
        Undo(found);
        return true;
    }

    /// <summary>
    /// Unpatch everything <paramref name="owner"/> registered. Called from
    /// <c>PluginReloader.SweepRegistrations</c> on every unload reason, because the plugin
    /// assembly is going away in all of them.
    /// </summary>
    public static int RemoveAll(object owner)
    {
        if (owner == null) throw new ArgumentNullException(nameof(owner));
        return RemoveWhere(e => ReferenceEquals(e.Owner, owner), OwnerIdOf(owner));
    }

    /// <summary>
    /// Unpatch everything registered under <paramref name="ownerId"/>. The id overload exists
    /// for a plugin swept by GUID (a raw <c>BasePlugin</c> whose instance is already gone).
    /// </summary>
    public static int RemoveAllById(string ownerId)
    {
        if (string.IsNullOrEmpty(ownerId)) return 0;
        return RemoveWhere(e => string.Equals(e.OwnerId, ownerId, StringComparison.Ordinal), ownerId);
    }

    private static int RemoveWhere(Func<Entry, bool> match, string ownerLabel)
    {
        List<Entry> doomed = new();
        lock (Gate)
        {
            for (int i = Entries.Count - 1; i >= 0; i--)
            {
                if (!match(Entries[i])) continue;
                doomed.Add(Entries[i]);
                Entries.RemoveAt(i);
            }
        }

        for (int i = 0; i < doomed.Count; i++) Undo(doomed[i]);
        if (doomed.Count > 0)
        {
            // Label with the plugin id the entries were tagged with, not the owner's CLR type:
            // the id is what the patch and unpatch lines carry, so the three lines grep together.
            string label = string.IsNullOrEmpty(doomed[0].OwnerId) ? ownerLabel : doomed[0].OwnerId;
            Log(PatchSitePolicy.FormatRemoveAll(label, doomed.Count, Count), warning: false);
        }
        return doomed.Count;
    }

    /// <summary>
    /// Unpatch <b>everything</b>, whoever owns it, and detach the applier. Core
    /// <c>Uninstall</c> calls this before the collectible ALC unloads: at that point every
    /// remaining patch is Core-generation-owned (plugins were swept first).
    ///
    /// <para>Note what this does and does not buy (job FHARM6). The unpatch stops the call; it
    /// is the <b>slot release</b> that frees the ALC, because HarmonyX's pin survives an unpatch
    /// (<c>harmony-patches.md</c> section 8). For a slot-routed patch, which after FHARM6 is
    /// every patch Core or a plugin registers in production, both happen here. For a direct
    /// patch there is no slot to release and the pin is permanent; that is what the warning at
    /// registration time says.</para>
    /// </summary>
    public static int Uninstall()
    {
        Entry[] doomed;
        lock (Gate)
        {
            doomed = Entries.ToArray();
            Entries.Clear();
        }
        for (int i = 0; i < doomed.Length; i++) Undo(doomed[i]);
        // Backstop: an entry whose Undo threw still holds its slot, and a held slot holds the
        // owner's ALC. Nothing may survive this call (job FHARM5).
        int stranded = PatchSlotLink.ReleaseAll();
        if (stranded > 0)
            Log("patches: released " + stranded + " stranded patch slot(s) at uninstall", warning: true);
        Log(PatchSitePolicy.FormatUninstall(doomed.Length, 0), warning: false);
        Applier = null;
        SiteResolver = null;
        return doomed.Length;
    }

    /// <summary>Tests only: drop state without touching the applier's log expectations.</summary>
    internal static void ResetForTests()
    {
        lock (Gate)
        {
            Entries.Clear();
            _nextId = 0;
        }
        PatchSlotLink.ReleaseAll();
        Applier = null;
        SiteResolver = null;
    }

    /// <summary>
    /// Best-effort undo of an apply that threw (R32 finding 8). The registry has no token — the
    /// apply never returned one — but HarmonyX's <c>Unpatch</c> is addressed by
    /// <c>(original, patchMethod)</c>, both of which we still hold, and
    /// <see cref="IPatchApplier.Remove"/> is required to be safe for a patch that was never
    /// applied. Either outcome is logged, because the failure case is a pin the sweep will not
    /// find.
    /// </summary>
    private static void SweepFailedApply(
        IPatchApplier applier, MethodBase target, MethodInfo patch, PatchKind kind, string ownerId, string name)
    {
        try
        {
            applier.Remove(patch, target, patch, kind, ownerId);
            Log(PatchSitePolicy.FormatApplySwept(kind, name, ownerId), warning: false);
        }
        catch (Exception e)
        {
            Log(PatchSitePolicy.FormatApplySweepFailed(kind, name, ownerId, e.Message), warning: true);
        }
    }

    private static void Undo(Entry entry)
    {
        try
        {
            Applier?.Remove(entry.Token, entry.Target, entry.Patch, entry.Kind, entry.OwnerId);
        }
        catch (Exception e)
        {
            // The slot still has to be emptied (it is the release, not the unpatch, that lets
            // the owner's ALC collect) but it must NOT go back into the pool (job FHARM6, R40
            // finding 2). The unpatch threw, so the detour on the original may still be
            // installed; a released slot is a no-op only for as long as it stays free, and Bind
            // hands out the lowest free index first, so the very next patch anyone registers
            // would inherit the stranded detour and run its own body with the old target's
            // __instance and __args. Quarantine drops the body, bumps the generation and takes
            // the index out of service for the life of the process, at the price of one of the
            // 32 slots, which Describe() counts, so the loss is visible rather than silent.
            bool quarantined = entry.SlotToken >= 0
                && PatchSlotLink.Quarantine(entry.SlotToken, entry.OwnerId);
            if (quarantined)
            {
                DrainSlotWarning();
                Log(PatchSitePolicy.FormatSlotQuarantined(
                    PatchSlotLink.IndexOf(entry.SlotToken), entry.OwnerId, entry.Id), warning: true);
            }
            else if (entry.SlotToken >= 0)
            {
                // Only reachable against an old Bootstrap with no Quarantine: fall back to the
                // pre-FHARM6 release rather than leave the ALC pinned by a held slot.
                PatchSlotLink.Release(entry.SlotToken, entry.OwnerId);
            }
            // A failed unpatch means the detour may survive. Say so loudly; the sweep continues
            // so one bad entry cannot strand the rest.
            Log("patches: unpatch FAILED " + PatchSitePolicy.KindToken(entry.Kind) + " " + entry.TargetName
                + " owner=" + entry.OwnerId + " id=" + entry.Id + ": " + e.Message
                + (quarantined
                    ? " (the slot is quarantined, so the surviving detour runs the original)"
                    : " (this pins the collectible ALC)"), warning: true);
            return;
        }
        if (entry.SlotToken >= 0) PatchSlotLink.Release(entry.SlotToken, entry.OwnerId);
        Log(PatchSitePolicy.FormatUnpatched(entry.Kind, entry.TargetName, entry.OwnerId, entry.Id), warning: false);
    }

    /// <summary><c>ProjectM.DeathUtilities::Kill</c>, or <c>?</c> for a null target.</summary>
    public static string DescribeTarget(MethodBase? target)
    {
        if (target == null) return "?";
        var declaring = target.DeclaringType;
        return (declaring?.FullName ?? "?") + "::" + target.Name;
    }

    private static string OwnerIdOf(object owner) => owner?.GetType().FullName ?? "?";

    /// <summary>
    /// Log the pool's pending warning, if it raised one (job FHARM7, R41 finding 2): free slots
    /// down to <c>PatchSlots.LowFreeWarnThreshold</c>, or a quarantine that shrank the pool for
    /// the life of the process. Pulled as a string rather than pushed through a callback, because
    /// a delegate held by the non-collectible Bootstrap would pin this Core generation.
    /// </summary>
    private static void DrainSlotWarning()
    {
        var warning = PatchSlotLink.TakeWarning();
        if (!string.IsNullOrEmpty(warning)) Log(warning!, warning: true);
    }

    private static void Log(string line, bool warning)
    {
        try
        {
            if (warning) Plugin.Logger?.LogWarning(line);
            else Plugin.Logger?.LogInfo(line);
        }
        catch
        {
            // Logging must never break a patch or a sweep.
        }
    }

    private sealed class Entry
    {
        internal Entry(long id, object? owner, string ownerId, MethodBase target, MethodInfo patch, PatchKind kind, string targetName, object token, long slotToken = -1)
        {
            SlotToken = slotToken;
            Id = id;
            Owner = owner;
            OwnerId = ownerId;
            Target = target;
            Patch = patch;
            Kind = kind;
            TargetName = targetName;
            Token = token;
        }

        internal long Id { get; }
        internal object? Owner { get; }
        internal string OwnerId { get; }
        internal MethodBase Target { get; }
        internal MethodInfo Patch { get; }
        internal PatchKind Kind { get; }
        internal string TargetName { get; }
        internal object Token { get; }

        /// <summary>
        /// Bootstrap patch-slot token: index and generation, see
        /// <c>Progenitor.Bootstrap.PatchSlots.Bind</c>. -1 for a direct patch. Held as the token
        /// rather than the index so this entry can never release or quarantine a slot that has
        /// since been handed to somebody else (R40 finding 2).
        /// </summary>
        internal long SlotToken { get; }
    }
}

/// <summary>
/// Handle for one registered patch. Disposing unpatches it. Unload does not need the handle —
/// <see cref="PatchRegistry.RemoveAll(object)"/> sweeps by owner — so a plugin may ignore it.
/// </summary>
public sealed class PatchHandle : IDisposable
{
    private bool _done;

    internal PatchHandle(long id) => Id = id;

    /// <summary>Registry id; appears in the patch and unpatch log lines.</summary>
    public long Id { get; }

    /// <summary>True once this handle has unpatched (or was already swept).</summary>
    public bool Disposed => _done;

    /// <summary>Unpatch. Idempotent.</summary>
    public void Dispose()
    {
        if (_done) return;
        _done = true;
        PatchRegistry.Remove(Id);
    }
}

/// <summary>
/// One member of an atomic <see cref="PatchRegistry.All"/> registration:
/// which method to patch, with what, as which kind, at what priority. <see cref="Label"/> is the
/// short site key that names this member in the group's refusal line
/// (<c>godmode.killentity</c>), so an operator reading the log knows which half failed without
/// decoding a full type name.
/// </summary>
public readonly struct PatchRequest
{
    /// <summary>Method to patch. Null is a refusal, and refuses the whole group.</summary>
    public MethodBase? Target { get; }

    /// <summary>
    /// The prefix/postfix/finalizer body for the <b>direct</b> path. Null is a refusal, and
    /// refuses the group, unless <see cref="SlotHandler"/> is set instead, which is the path
    /// every production caller takes.
    /// </summary>
    public MethodInfo? Patch { get; }

    /// <summary>
    /// The slot-routed body: a <see cref="SlotPrefix"/>, <see cref="SlotPostfix"/> or
    /// <see cref="SlotFinalizer"/> matching <see cref="Kind"/>. When this is set the member is
    /// parked in a <c>Progenitor.Bootstrap.PatchSlots</c> slot and HarmonyX never sees a
    /// collectible method (job FHARM6). Null means the direct path.
    /// </summary>
    public Delegate? SlotHandler { get; }

    /// <summary>Prefix, postfix or finalizer.</summary>
    public PatchKind Kind { get; }

    /// <summary>Harmony priority.</summary>
    public int Priority { get; }

    /// <summary>Short site key for the group's log line; may be empty.</summary>
    public string Label { get; }

    /// <summary>Describe one member of a group registration.</summary>
    public PatchRequest(MethodBase? target, MethodInfo? patch, PatchKind kind = PatchKind.Prefix, int priority = 400, string? label = null)
    {
        Target = target;
        Patch = patch;
        SlotHandler = null;
        Kind = kind;
        Priority = priority;
        Label = label ?? "";
    }

    /// <summary>
    /// Describe one <b>slot-routed</b> member of a group registration (job FHARM6). A null
    /// handler is a refusal and refuses the group, exactly as a null <see cref="Patch"/> is on
    /// the direct path.
    /// </summary>
    public PatchRequest(MethodBase? target, Delegate? slotHandler, PatchKind kind = PatchKind.Prefix, int priority = 400, string? label = null)
    {
        Target = target;
        Patch = null;
        SlotHandler = slotHandler;
        Kind = kind;
        Priority = priority;
        Label = label ?? "";
    }

    /// <summary><see cref="Label"/>, or <paramref name="fallback"/> when no label was given.</summary>
    public string LabelOrTarget(string fallback) => string.IsNullOrEmpty(Label) ? fallback : Label;
}
