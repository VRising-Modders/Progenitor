using System;
using System.Collections.Generic;

namespace Progenitor.Buffs;

/// <summary>
/// Apply, query and remove a timed custom buff through the game paths Progenitor already proves.
///
/// <para><b>The path.</b> Definition is <c>ctx.Content.DefineBuff(id).CloneOf(vanillaBuff)</c> — a
/// verified vanilla base, because the clone inherits the base's buff-bar visibility, its sequence
/// and its <c>LifeTime</c>. Application is the game's own debug apply
/// (<c>DebugEventsSystem.ApplyBuff</c> with an <c>ApplyBuffDebugEvent</c> and a
/// <c>FromCharacter</c>), the same call the Dev <c>buff</c> op and the registry's live refresh
/// already use. Confirmation is a walk of the target's <c>BuffBuffer</c>. Removal is
/// <c>DestroyUtility.Destroy(..., DestroyDebugReason.TryRemoveBuff)</c>. Nothing here writes a
/// component on the <em>character</em>, and nothing here invents a native call.</para>
///
/// <para><b>A registration is not an application</b>, and a request is not an active buff. Apply
/// refuses before it writes anything when the definition is not registered by this plugin or its
/// clone is not in the world yet; and a successful call returns a handle in
/// <see cref="BuffApplicationState.Requested"/>, which becomes
/// <see cref="BuffApplicationState.Active"/> only once the instance has been seen.</para>
///
/// <para><b>Authority and thread.</b> Server only; a client call is refused with an actionable
/// reason rather than writing anything. <see cref="Apply"/>, <see cref="RemoveAll"/> and every
/// handle method must run on the game main thread (<c>Plugin.Frame</c>, a command handler, a
/// virtual system, EventBus) — Apply throws <see cref="InvalidOperationException"/> off it, for the
/// same reason <c>Units</c> does: EntityManager writes crash natively from a worker thread.</para>
///
/// <para><b>Ownership.</b> One buff instance has one owner. A second apply of the same buff on the
/// same target is refused unless the same owner asked for
/// <see cref="BuffRefreshPolicy.Refresh"/>. Two <em>different</em> buffs that modify the same stat
/// are not a conflict at all: each instance registers its own modification through the game's
/// <c>ModificationsRegistry</c> and the matching destroy system reverses it by id, so removing one
/// leaves the other's contribution exactly as it was.</para>
///
/// <para><b>Unload.</b> <c>ctx.Buffs</c> tracks every handle, so plugin unload removes what that
/// plugin applied; a static-API caller passes <c>owner</c> and gets the same sweep. Core uninstall
/// finishes every live application, including an unconfirmed one, before the generation goes.</para>
/// </summary>
public static class Buffs
{
    /// <summary>
    /// Module name for the Core unload pin report.
    ///
    /// <para>This is <b>not</b> a <c>SkipCoreModules</c> token. That list
    /// (<c>HarmonySlotHandoff.ModuleNames</c>) is mirrored by Bootstrap's
    /// <c>HarmonySlotPolicy.ModuleNames</c> and a test pins the two equal, so adding a token means a
    /// Bootstrap source change. The module therefore always installs; it holds nothing but its own
    /// application list and one <c>Plugin.Frame</c> handler, both released by <see cref="Uninstall"/>.</para>
    /// </summary>
    public const string ModuleName = "Buffs";

    private static readonly object Gate = new();
    private static readonly List<BuffApplication> Live = new();
    private static readonly List<BuffApplication> PollBuffer = new();
    private static IBuffRuntime? _runtime;
    private static bool _installed;
    private static bool _frameHooked;

    // ---- BUFFP2: the retirement watch -------------------------------------------------------
    //
    // UnityBuffRuntime.DestroyInstance issues DestroyUtility.Destroy and nothing else (BUFFP1). A
    // destroy raises an event, so the entity goes away on a later frame, not on this one - and it
    // can fail to take. The old code papered over that by adding DestroyTag out of band, which is
    // illegal and crashed a live server. The honest replacement is to keep watching.
    //
    // The watch belongs to this static module, and that is forced rather than chosen:
    // PluginReloader runs MainThread.DropOwnedBy(plugin, ...) AFTER the sweeps
    // (PluginReloader.cs:810-814), so anything posted with the unloading plugin as owner would be
    // thrown away exactly when it is needed. Same shape as UnityUnitRuntime owning its own DIAG5
    // defer.
    //
    // The application handle still goes terminal immediately, so no public contract changes: a
    // retirement is Core's bookkeeping about an entity, not a live application.
    private static readonly List<BuffRetirement> Retiring = new();

    /// <summary>
    /// Frames to wait before re-issuing a destroy that has not been observed to take. Mirrors
    /// <c>PrefabLiveRefresh.DestroyWaitFrames = 2</c> — the in-repo precedent for destroying a buff
    /// on a wearer that stays. Not referenced directly: this file is game-free and compiles into the
    /// test assembly.
    /// </summary>
    internal const int RetireRetryFrames = 2;

    /// <summary>
    /// Total frames a retirement is watched before it is reported and dropped. Mirrors
    /// <c>Ecs/BuffSweep.DestroyTagDeferFrames = 120</c>, the existing budget for "this buff should
    /// be gone by now".
    /// </summary>
    internal const int RetireBudgetFrames = 120;

    /// <summary>One destroy this module issued, watched until the entity is observed gone.</summary>
    private sealed class BuffRetirement
    {
        internal BuffEntityId Instance;
        internal BuffEntityId Target;
        internal string BuffId = "";
        internal int Epoch;
        internal int Frames;
        internal bool Retried;

        /// <summary>
        /// True when this destroy was issued by <c>FinishForShutdown</c>, i.e. microseconds before
        /// Poll is unhooked. It cannot possibly have been observed gone, so reporting it in the same
        /// words as a destroy that was genuinely watched and never took would make every ordinary
        /// Core unload look broken and dilute the signal the warning exists for.
        /// </summary>
        internal bool AtShutdown;
    }

    /// <summary>
    /// Apply <paramref name="request"/> for <paramref name="owner"/>. Never throws for a refusal:
    /// the returned handle carries <see cref="BuffApplicationState.Refused"/> and a
    /// <see cref="IBuffApplication.RefusalReason"/> you can show a player. Throws only for a null
    /// request or an off-main-thread call, both of which are caller bugs.
    ///
    /// <para>Pass <paramref name="pluginId"/> (the BepInEx GUID) so the unload sweep can claim this
    /// application even when <paramref name="owner"/> is a bare token;
    /// <c>PluginContext.Buffs</c> fills both and tracks the handle for you.</para>
    /// <para>Without an owner or plugin id, this application is not swept on plugin unload.
    /// Keep and dispose its handle explicitly, or use <c>ctx.Buffs</c> for owned lifetime.</para>
    /// </summary>
    public static IBuffApplication Apply(BuffRequest request, object? owner = null, string? pluginId = null) =>
        Apply(request, owner, pluginId, out _);

    /// <summary>
    /// <see cref="Apply(BuffRequest, object?, string?)"/>, reporting whether the returned handle is
    /// a <b>new</b> application. It is not on the refresh path (the live application is returned
    /// instead) and not on a refusal (the handle is already terminal), which is exactly when
    /// <c>PluginContext.Track</c> must not record it again - a plugin refreshing on a timer would
    /// otherwise add one entry per call for the life of the plugin.
    /// </summary>
    internal static IBuffApplication Apply(
        BuffRequest request, object? owner, string? pluginId, out bool isNew)
    {
        isNew = false;
        if (request == null) throw new ArgumentNullException(nameof(request));
        EnsureMainThread(nameof(Apply));

        string id = string.IsNullOrWhiteSpace(pluginId) ? InferPluginId(owner) : pluginId!.Trim();
        var runtime = CurrentRuntime();

        bool installed;
        lock (Gate) installed = _installed;

        int prefabGuid = 0;
        var resolve = BuffResolve.NotRegistered;
        var target = BuffTargetCheck.NoWorld;
        bool isServer = false;
        bool hasWorld = false;
        try
        {
            isServer = runtime.IsServer;
            hasWorld = runtime.HasWorld;
            // Only ask the world anything once the ladder's cheap answers have passed. An
            // uninstalled module reading the prefab collection would be work whose answer
            // Validate discards anyway.
            if (installed && isServer && hasWorld)
            {
                resolve = runtime.TryResolveBuff(request.BuffId, owner, id, out prefabGuid);
                target = runtime.CheckTarget(request.Target);
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("Buffs: pre-apply checks threw: " + e);
            return Refuse(request, id, owner, BuffRefusal.ModuleUnavailable, e.Message);
        }

        var refusal = BuffApplicationPolicy.Validate(request, installed, isServer, hasWorld, resolve, target);
        if (refusal != BuffRefusal.None)
            return Refuse(request, id, owner, refusal);

        // Conflict: one instance, one owner.
        BuffApplication? existing = FindLive(request.Target, prefabGuid);
        bool sameOwner = existing != null && OwnedBy(existing, owner, id);
        bool canceling = existing != null && existing.IsCancelling;
        var conflict = BuffApplicationPolicy.DecideConflict(
            existing != null, sameOwner, canceling, request.Refresh);
        switch (conflict)
        {
            case BuffApplicationPolicy.Conflict.Reject:
            case BuffApplicationPolicy.Conflict.Foreign:
            case BuffApplicationPolicy.Conflict.Cancelling:
                return Refuse(
                    request, id, owner, BuffApplicationPolicy.RefusalFor(conflict),
                    conflict == BuffApplicationPolicy.Conflict.Foreign
                        ? "held by " + (existing?.PluginId ?? "another plugin")
                        : null);

            case BuffApplicationPolicy.Conflict.Refresh:
                RefreshExisting(existing!, request);
                return existing!;
        }

        // Never adopt an instance this module did not create. A clone's instance can already be
        // on the target when no application holds it - a save restores live buff instances
        // (evidence 109), and another source can apply the same prefab - and confirming on that
        // one would hand this application a buff it did not create to retime and, on removal,
        // destroy. Refuse, and say which instance is in the way.
        if (runtime.TryFindInstance(request.Target, prefabGuid, out var present) && !present.IsNone)
        {
            // ...unless it is an instance THIS module has already destroyed and is still watching.
            //
            // A destroy is an event: for the frame or two before the game collects it, the entity is
            // still a BuffBuffer row, so TryFindInstance keeps finding it. Removing the out-of-band
            // DestroyTag (BUFFP1) made that window real rather than instantaneous, and without this
            // check a plugin that removes its own buff and re-applies on the next frame is told
            // "the target already wears this buff and no live application created it" - about an
            // instance it just removed itself. Rejecting a re-apply because of our own pending
            // destroy is not the case the AlreadyPresent refusal exists for; it exists for a save
            // restore or a foreign source.
            if (!IsRetiring(present, runtime))
                return Refuse(request, id, owner, BuffRefusal.AlreadyPresent, "instance=" + present);

            Plugin.Logger?.LogInfo(
                "buff apply id='" + request.BuffId + "' target=" + request.Target
                + ": instance=" + present + " is still present but this module already destroyed it "
                + "and is watching it go; applying a fresh one rather than refusing AlreadyPresent");
        }

        if (!runtime.RequestApply(request.Target, prefabGuid, out string reason))
        {
            Plugin.Logger?.LogWarning(
                "buff apply id='" + request.BuffId + "' target=" + request.Target + ": " + reason);
            return Refuse(request, id, owner, BuffRefusal.ApplyPathUnavailable, reason);
        }

        var application = new BuffApplication(
            request.BuffId, id, owner, request.Target, prefabGuid,
            request.KeepsPrefabDuration ? BuffRequest.KeepPrefabDuration : request.DurationSeconds,
            SafeEpoch(runtime), runtime, BuffApplicationState.Requested);

        Track(application);
        isNew = true;
        Plugin.Logger?.LogInfo(BuffApplicationPolicy.FormatTransition(
            request.BuffId, request.Target, BuffEntityId.None, BuffApplicationState.Requested));
        return application;
    }

    /// <summary>
    /// The refresh path, for both windows it has to cover. A confirmed application rewrites the
    /// live instance's <c>LifeTime</c>; one whose apply is queued but not yet confirmed has its
    /// <b>pending</b> duration replaced, so the value that lands when it confirms is the one the
    /// caller just asked for and not the first call's.
    /// </summary>
    private static void RefreshExisting(BuffApplication existing, BuffRequest request)
    {
        if (request.KeepsPrefabDuration)
        {
            Plugin.Logger?.LogInfo(
                "buff refresh id='" + request.BuffId + "' target=" + request.Target
                + " returned the live application unchanged (state=" + existing.State
                + ", no duration requested)");
            return;
        }

        if (existing.State == BuffApplicationState.Active)
        {
            // The bool is deliberately not the only record. This method hands the caller back the
            // application that is already live, so its State stays Active and its Refusal still
            // describes the original apply - a refused refresh would otherwise be indistinguishable
            // from one that took, and the worked example reported success for a duration that was
            // never written. BuffApplication.Refresh records the reason on the handle itself, which
            // is what ApplyOrRefresh callers read (IBuffApplication.LastRefreshRefusal).
            if (!existing.Refresh(request.DurationSeconds))
                Plugin.Logger?.LogWarning(
                    "buff refresh id='" + request.BuffId + "' target=" + request.Target
                    + " could not rewrite the duration (" + existing.LastRefreshRefusal
                    + "); the live instance keeps its own. The returned application is still Active "
                    + "and still carries its previous RequestedDurationSeconds - read "
                    + "LastRefreshRefusal to tell this apart from a refresh that took.");
            return;
        }

        if (existing.UpdatePendingDuration(request.DurationSeconds))
        {
            Plugin.Logger?.LogInfo(
                "buff refresh id='" + request.BuffId + "' target=" + request.Target
                + " is still unconfirmed; it will confirm with duration="
                + request.DurationSeconds + "s");
            return;
        }

        Plugin.Logger?.LogWarning(
            "buff refresh id='" + request.BuffId + "' target=" + request.Target
            + " could not take the new duration (state=" + existing.State + ")");
    }

    /// <summary>
    /// Every live application <paramref name="owner"/> holds, as a snapshot. Terminal ones are not
    /// in it: an application that ended is not something the caller can still act on.
    /// </summary>
    public static IReadOnlyList<IBuffApplication> Active(object owner)
    {
        if (owner == null) throw new ArgumentNullException(nameof(owner));
        var list = new List<IBuffApplication>();
        lock (Gate)
        {
            for (int i = 0; i < Live.Count; i++)
                if (ReferenceEquals(Live[i].Owner, owner) && Live[i].IsLive && !Live[i].IsCancelling)
                    list.Add(Live[i]);
        }

        return list;
    }

    /// <summary>
    /// The live application of <paramref name="buffId"/> on <paramref name="target"/>, whoever owns
    /// it, or null. A read: it changes nothing and it does not touch the world.
    /// </summary>
    public static IBuffApplication? Find(string buffId, BuffEntityId target)
    {
        if (string.IsNullOrWhiteSpace(buffId)) return null;
        lock (Gate)
        {
            for (int i = 0; i < Live.Count; i++)
            {
                var a = Live[i];
                // A canceled-but-unconfirmed application still holds the slot against a second
                // apply, but it is nobody's to act on any more, so a query does not hand it out.
                if (!a.HoldsSlot || a.IsCancelling) continue;
                if (a.Target != target) continue;
                if (!string.Equals(a.BuffId, buffId, StringComparison.Ordinal)) continue;
                return a;
            }
        }

        return null;
    }

    /// <summary>
    /// Remove every application <paramref name="owner"/> holds, and nothing else. Plugin unload
    /// does this through <c>PluginContext.Track</c>; call it yourself to retire one subsystem
    /// while the rest of the plugin runs on. Safe to call twice.
    /// </summary>
    public static void RemoveAll(object owner)
    {
        if (owner == null) throw new ArgumentNullException(nameof(owner));
        EnsureMainThread(nameof(RemoveAll));
        DisposeOwned(owner, null, dropCallbacks: false, wide: false);
    }

    /// <summary>
    /// Plugin unload. Drops handlers written in the unloading assembly first — a delegate whose
    /// method lives in a collectible ALC pins that generation — then removes what the plugin owns,
    /// by owner reference <b>or</b> by the BepInEx id recorded at apply time. The id is a
    /// parameter for the same reason <c>Units.SweepForUnload</c> takes one: no BepInEx plugin type
    /// exposes its GUID as a property, so reading it off the owner never works.
    /// </summary>
    internal static void SweepForUnload(object owner, string? pluginId)
    {
        if (owner == null) throw new ArgumentNullException(nameof(owner));
        EnsureMainThread(nameof(SweepForUnload));
        string? id = string.IsNullOrWhiteSpace(pluginId) ? null : pluginId!.Trim();
        DisposeOwned(owner, id, dropCallbacks: true, wide: true);
        SweepForeignCallbacks(owner, id);
    }

    /// <summary>
    /// The other half of the unload sweep: handlers the unloading plugin wrote on an application it
    /// does <b>not</b> own. <see cref="DisposeOwned"/> above only visits the applications that
    /// matched the ownership rule, so a handler a plugin attached to a stranger's application
    /// survived its unload and pinned its collectible ALC — <see cref="Find"/> is public and
    /// documented as finding an application whoever owns it, so that is an invited use, not an
    /// abuse. Core's other two sweeps cannot reach it: <c>EventBus.RemoveAll</c> and
    /// <c>PluginOwnedAssemblies.RemoveEventHandlers</c> both only know about the event bus, and
    /// <see cref="IBuffApplication.OnStateChanged"/> is a plain C# event.
    ///
    /// <para>The stranger's application itself is left alone and keeps running — it is not this
    /// plugin's to remove, and nothing but the delegate pinned anything. Same shape, and the same
    /// reasoning, as <c>Units.SweepSequences</c>.</para>
    /// </summary>
    private static void SweepForeignCallbacks(object owner, string? pluginId)
    {
        System.Reflection.Assembly assembly;
        try { assembly = owner.GetType().Assembly; }
        catch { return; }

        BuffApplication[] snapshot;
        lock (Gate) snapshot = Live.ToArray();

        int dropped = 0;
        int touched = 0;
        for (int i = 0; i < snapshot.Length; i++)
        {
            bool mine;
            try { mine = OwnedByUnloading(snapshot[i], owner, pluginId); }
            catch { continue; }
            if (mine) continue; // already dropped wholesale and disposed above

            int n;
            try { n = snapshot[i].DropCallbacksFrom(assembly); }
            catch (Exception e)
            {
                Plugin.Logger?.LogWarning("Buffs: callback drop threw: " + e.Message);
                continue;
            }

            if (n == 0) continue;
            dropped += n;
            touched++;
        }

        if (dropped > 0)
        {
            string name = assembly.GetName().Name ?? assembly.FullName ?? "an unloading plugin";
            Plugin.Logger?.LogInfo("Buffs: dropped " + dropped + " OnStateChanged handler(s) written in "
                + name + " from " + touched + " application(s) owned by somebody else; "
                + "those applications keep running.");
        }
    }

    internal static void Install()
    {
        lock (Gate)
        {
            _installed = true;
            _runtime ??= CreateDefaultRuntime();
        }

        HookFrame();
        Plugin.Logger?.LogInfo(_runtime is { IsServer: true }
            ? "Buffs: installed (server)"
            : "Buffs: installed (client no-op)");
    }

    internal static void Uninstall()
    {
        UnhookFrame();
        BuffApplication[] snapshot;
        lock (Gate)
        {
            snapshot = Live.ToArray();
            Live.Clear();
            PollBuffer.Clear();
            _installed = false;
        }

        int finished = 0;
        for (int i = 0; i < snapshot.Length; i++)
        {
            try
            {
                if (!snapshot[i].IsLive) continue;
                snapshot[i].FinishForShutdown();
                finished++;
            }
            catch (Exception e)
            {
                Plugin.Logger?.LogWarning("Buffs: Uninstall finish threw: " + e.Message);
            }
        }

        // BUFFP3. Uninstall unhooks Poll, so the retirement watch ends here. Report whatever is
        // still pending instead of dropping it silently — same honesty as FinishForShutdown's
        // "queued and never confirmed" residual. Read AFTER the finish loop above, because
        // FinishForShutdown issues destroys of its own and those belong in this report.
        BuffRetirement[] pending;
        lock (Gate)
        {
            pending = Retiring.ToArray();
            Retiring.Clear();
        }

        int watched = 0;
        for (int i = 0; i < pending.Length; i++)
        {
            if (pending[i].AtShutdown)
            {
                // Issued by FinishForShutdown a moment ago. Expected on every ordinary unload with a
                // live buff, so it is Info, not a Warning - the destroy is with the game and this
                // generation simply does not outlive it.
                Plugin.Logger?.LogInfo(
                    "Buffs: destroy of buff id='" + pending[i].BuffId + "' instance=" + pending[i].Instance
                    + " on target=" + pending[i].Target + " was issued during Core uninstall; the game "
                    + "completes it after this generation is gone, so nothing here confirms it.");
                continue;
            }

            watched++;
            Plugin.Logger?.LogWarning(
                "Buffs: destroy of buff id='" + pending[i].BuffId + "' instance=" + pending[i].Instance
                + " on target=" + pending[i].Target + " was issued and watched, the entity was still "
                + "there, and Core is now uninstalling; no generation is watching it any more. The "
                + "buff may still be on the wearer.");
        }

        lock (Gate) _runtime = null;
        Plugin.Logger?.LogInfo("Buffs: uninstalled, finished " + finished + " live application(s)"
            + (watched > 0 ? ", " + watched + " destroy(s) still unconfirmed after watching" : ""));
    }

    /// <summary>Line for the Core unload pin report: how many applications this module still holds.</summary>
    internal static string DescribePins()
    {
        var holds = new List<string>();
        lock (Gate)
        {
            if (Live.Count > 0) holds.Add("applications=" + Live.Count);
            if (_installed) holds.Add("installed");
        }

        return HarmonySlotHandoff.FormatModulePins(ModuleName, holds);
    }

    internal static void Forget(BuffApplication application)
    {
        lock (Gate) Live.Remove(application);
    }

    /// <summary>Main-thread poll of every live application. Hooked to <c>Plugin.Frame</c>.</summary>
    internal static void Poll()
    {
        lock (Gate)
        {
            PollBuffer.Clear();
            for (int i = 0; i < Live.Count; i++) PollBuffer.Add(Live[i]);
        }

        for (int i = 0; i < PollBuffer.Count; i++)
        {
            try { PollBuffer[i].Poll(); }
            catch (Exception e) { Plugin.Logger?.LogWarning("Buffs: Poll threw: " + e.Message); }
        }

        // BUFFP2. Deliberately NOT inside the `Live.Count == 0` early return this method used to
        // have: a retirement outlives its application by design — the handle goes terminal and
        // leaves Live the moment the destroy is issued, and the entity goes away some frames later.
        // Gating the drain on Live being non-empty would stop watching in exactly the common case
        // (the last buff being removed).
        DrainRetirements();
    }

    /// <summary>
    /// BUFFP2: record that this module issued a destroy for <paramref name="instance"/>, so
    /// <see cref="Poll"/> can confirm it took. Called by every destroy site <b>after</b> the handle
    /// has gone terminal. Cheap and never throws.
    /// </summary>
    internal static void NoteRetiring(BuffEntityId instance, BuffEntityId target, string buffId, int epoch,
                                      bool atShutdown = false)
    {
        if (instance.IsNone) return;
        lock (Gate)
        {
            for (int i = 0; i < Retiring.Count; i++)
                if (Retiring[i].Instance == instance && Retiring[i].Epoch == epoch) return;

            Retiring.Add(new BuffRetirement
            {
                Instance = instance,
                Target = target,
                BuffId = buffId ?? "",
                Epoch = epoch,
                Frames = 0,
                Retried = false,
                AtShutdown = atShutdown,
            });
        }
    }

    /// <summary>How many destroys this module is still watching. Readback and tests.</summary>
    internal static int RetiringCount
    {
        get { lock (Gate) return Retiring.Count; }
    }

    /// <summary>
    /// Is this exact instance one this module already destroyed and is still watching, in the
    /// current epoch? Used by <see cref="Apply"/> so a re-apply in the frame or two before the game
    /// collects our own destroy is not mistaken for a foreign or save-restored instance.
    /// </summary>
    private static bool IsRetiring(BuffEntityId instance, IBuffRuntime runtime)
    {
        if (instance.IsNone) return false;
        int epochNow;
        try { epochNow = runtime.Epoch; }
        catch { return false; }

        lock (Gate)
        {
            for (int i = 0; i < Retiring.Count; i++)
                if (Retiring[i].Instance == instance && Retiring[i].Epoch == epochNow) return true;
        }

        return false;
    }

    /// <summary>
    /// One frame of the retirement watch: drop what is gone, re-issue once for what is not, report
    /// what outlives the budget. Never adds <c>DestroyTag</c> — that is the defect this replaces.
    /// </summary>
    private static void DrainRetirements()
    {
        IBuffRuntime? runtime;
        // A LOCAL snapshot, not a shared static buffer. This loop calls into the
        // runtime and the logger while iterating, and a re-entrant Poll would clear a shared buffer
        // mid-iteration. Poll is main-thread-only so that is not reachable today; a local list makes
        // it not reachable by construction, and costs one allocation per frame that has retirements.
        List<BuffRetirement> snapshot;
        lock (Gate)
        {
            runtime = _runtime;
            if (Retiring.Count == 0) return;
            snapshot = new List<BuffRetirement>(Retiring);
        }

        for (int i = 0; i < snapshot.Count; i++)
        {
            var entry = snapshot[i];
            bool drop = false;
            string? warn = null;

            if (runtime == null)
            {
                drop = true;
            }
            else
            {
                int epochNow;
                try { epochNow = runtime.Epoch; }
                catch { epochNow = entry.Epoch; }

                if (epochNow != entry.Epoch)
                {
                    // The world was replaced. The recorded index names a different entity now, so
                    // reading or destroying through it would act on a stranger. Drop without
                    // touching the world - the same rule the applications themselves follow.
                    drop = true;
                }
                else
                {
                    bool exists;
                    try { exists = runtime.Exists(entry.Instance); }
                    catch { exists = false; }

                    if (!exists)
                    {
                        drop = true;
                    }
                    else
                    {
                        entry.Frames++;
                        // It has now actually been watched for a frame, so it is no longer merely
                        // "issued at shutdown" - a later Uninstall should report it in the stronger
                        // words.
                        entry.AtShutdown = false;

                        if (!entry.Retried && entry.Frames >= RetireRetryFrames)
                        {
                            entry.Retried = true;
                            try { runtime.DestroyInstance(entry.Instance); }
                            catch (Exception e)
                            {
                                Plugin.Logger?.LogWarning(
                                    "Buffs: retirement re-destroy of " + entry.Instance + " threw: " + e.Message);
                            }
                        }
                        else if (entry.Frames >= RetireBudgetFrames)
                        {
                            drop = true;
                            warn = "Buffs: destroy of buff id='" + entry.BuffId + "' instance="
                                + entry.Instance + " on target=" + entry.Target
                                + " was issued (and re-issued once) but the entity was still there after "
                                + RetireBudgetFrames + " frames; giving up watching it. The buff may still "
                                + "be on the wearer.";
                        }
                    }
                }
            }

            if (warn != null) Plugin.Logger?.LogWarning(warn);
            if (drop) lock (Gate) Retiring.Remove(entry);
        }
    }

    /// <summary>
    /// <paramref name="wide"/> separates the two contracts the way <c>Units</c> separates them
    /// (job FUNIT6, R59 finding 4). <see cref="RemoveAll"/> is narrow — reference equality only —
    /// so retiring one subsystem does not remove the applications of another subsystem of the same
    /// plugin. Only the unload sweep widens to the assembly and the recorded plugin id.
    /// </summary>
    private static void DisposeOwned(object owner, string? pluginId, bool dropCallbacks, bool wide)
    {
        BuffApplication[] snapshot;
        lock (Gate)
        {
            var list = new List<BuffApplication>();
            for (int i = 0; i < Live.Count; i++)
            {
                bool mine = wide
                    ? OwnedByUnloading(Live[i], owner, pluginId)
                    : ReferenceEquals(Live[i].Owner, owner);
                if (mine) list.Add(Live[i]);
            }

            snapshot = list.ToArray();
        }

        for (int i = 0; i < snapshot.Length; i++)
        {
            try
            {
                if (dropCallbacks) snapshot[i].DropCallbacks();
                snapshot[i].Dispose();
            }
            catch (Exception e)
            {
                Plugin.Logger?.LogWarning("Buffs: sweep dispose threw: " + e.Message);
            }
        }

        if (snapshot.Length > 0)
            Plugin.Logger?.LogInfo(
                "Buffs: swept " + snapshot.Length + " application(s) of "
                + (pluginId ?? InferPluginId(owner)));
    }

    /// <summary>
    /// Ownership for the sweep: the same owner object, any owner object whose type the unloading
    /// assembly declares, or the plugin id recorded at apply time. Same three routes, and the same
    /// reasons, as the unit sweep — reference alone misses a second owner object, and the assembly
    /// test alone misses a bare <c>new object()</c> token, whose type the BCL declares.
    /// </summary>
    private static bool OwnedByUnloading(BuffApplication application, object owner, string? pluginId)
    {
        var handleOwner = application.Owner;
        if (handleOwner != null)
        {
            if (ReferenceEquals(handleOwner, owner)) return true;
            try
            {
                if (ReferenceEquals(handleOwner.GetType().Assembly, owner.GetType().Assembly)) return true;
            }
            catch { /* an owner that cannot answer is not a match */ }
        }

        return pluginId != null && string.Equals(application.PluginId, pluginId, StringComparison.Ordinal);
    }

    /// <summary>Is the live application the asking caller's? Reference first, recorded id second.</summary>
    private static bool OwnedBy(BuffApplication application, object? owner, string pluginId)
    {
        if (owner != null && ReferenceEquals(application.Owner, owner)) return true;
        return !string.IsNullOrEmpty(pluginId)
            && string.Equals(application.PluginId, pluginId, StringComparison.Ordinal);
    }

    private static BuffApplication? FindLive(BuffEntityId target, int prefabGuid)
    {
        lock (Gate)
        {
            for (int i = 0; i < Live.Count; i++)
            {
                var a = Live[i];
                if (a.PrefabGuid != prefabGuid) continue;
                if (a.Target != target) continue;
                if (!a.HoldsSlot) continue;
                return a;
            }
        }

        return null;
    }

    private static IBuffApplication Refuse(
        BuffRequest request, string pluginId, object? owner, BuffRefusal refusal, string? detail = null)
    {
        Plugin.Logger?.LogInfo(BuffRefusalText.Line(request.BuffId, request.Target, refusal, detail));
        return new BuffApplication(
            request.BuffId, pluginId, owner, request.Target, 0,
            BuffRequest.KeepPrefabDuration, 0, runtime: null,
            BuffApplicationState.Refused, refusal, detail);
    }

    private static int SafeEpoch(IBuffRuntime runtime)
    {
        try { return runtime.Epoch; }
        catch { return 0; }
    }

    private static BuffApplication Track(BuffApplication application)
    {
        lock (Gate) Live.Add(application);
        return application;
    }

    private static string InferPluginId(object? owner) =>
        owner == null ? "unowned" : owner.GetType().FullName ?? "unowned";

    private static IBuffRuntime CurrentRuntime()
    {
        lock (Gate)
        {
            if (_runtime != null) return _runtime;
            _runtime = CreateDefaultRuntime();
            return _runtime;
        }
    }

    private static IBuffRuntime CreateDefaultRuntime()
    {
#if PROGENITOR_TESTS
        return NullBuffRuntime.Instance;
#else
        if (!Plugin.IsServerProcess) return NullBuffRuntime.Instance;
        return new UnityBuffRuntime();
#endif
    }

    private static void EnsureMainThread(string op)
    {
#if PROGENITOR_TESTS
        _ = op;
#else
        if (Progenitor.Dev.MainThread.IsMainThread) return;
        throw new InvalidOperationException(
            "Buffs." + op + " must run on the game main thread (Plugin.Frame, a command handler, "
            + "a virtual system, or EventBus).");
#endif
    }

    private static void HookFrame()
    {
#if !PROGENITOR_TESTS
        if (_frameHooked) return;
        Plugin.Frame += Poll;
        _frameHooked = true;
#else
        _frameHooked = true;
#endif
    }

    private static void UnhookFrame()
    {
        if (!_frameHooked) return;
#if !PROGENITOR_TESTS
        Plugin.Frame -= Poll;
#endif
        _frameHooked = false;
    }

#if PROGENITOR_TESTS
    internal static void ResetForTests()
    {
        lock (Gate)
        {
            Live.Clear();
            PollBuffer.Clear();
            Retiring.Clear();
            _runtime = null;
            _installed = false;
            _frameHooked = false;
        }
    }

    /// <summary>Drive the "Core is loading or unloading" branch without tearing the module down.</summary>
    internal static void SetInstalledForTests(bool installed)
    {
        lock (Gate) _installed = installed;
    }

    internal static void SetRuntimeForTests(IBuffRuntime runtime)
    {
        lock (Gate)
        {
            _runtime = runtime ?? NullBuffRuntime.Instance;
            _installed = true;
        }
    }

    internal static int LiveCountForTests
    {
        get { lock (Gate) return Live.Count; }
    }

    internal static void TickForTests() => Poll();
#endif
}
