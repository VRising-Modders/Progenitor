using System;
using System.Collections.Generic;
using System.Reflection;
using ProjectM;
using Progenitor.Ecs;
using Progenitor.Patches;
using Unity.Entities;

namespace Progenitor.GodMode;

/// <summary>
/// Debug god mode: a set of entities that the game's unguarded kill paths may not kill
/// (job FHARM, the first consumer of <see cref="PatchRegistry"/>).
///
/// <para><b>Why this is not a component.</b> <c>ProjectM.BuffableFlagState.Invulnerable</c>
/// guards only the stat-change pipeline (FBUFF3 §1) and <c>ProjectM.Immortal</c>
/// is read on exactly one of the five kill entry points, and only when
/// that call passes <c>killImmortals: false</c>. Three paths honor nothing at all
/// (internal research notes, FDR §4 rows 4, 7, 8):</para>
/// <list type="bullet">
/// <item><description><c>StatChangeUtility.KillEntity(EntityManager, Entity, double, StatChangeReason)</c>
/// — forwards <c>killer = Entity.Null, killImmortals = true</c>;</description></item>
/// <item><description><c>StatChangeUtility.KillOrDestroyEntity(...)</c> on an entity with no
/// <c>Health</c> — jumps straight to the writer without reading
/// <c>Immortal</c>;</description></item>
/// <item><description><c>DeathUtilities.Kill(EntityManager, Entity killed, Dead, DeathEvent,
/// DeathReason)</c> — the raw writer, no guard of any kind.</description></item>
/// </list>
///
/// <para><b>The three prefixes.</b> All three funnel through <c>DeathUtilities.Kill</c>
/// on both sides, so that is choke point one. It is not
/// sufficient on its own: the 11-argument
/// <c>StatChangeUtility.KillEntity(EntityManager, Entity, Entity killer, ref Health, in
/// HealthConstants, double, StatChangeReason, Entity spellSource, RagdollSourceSetting, float,
/// bool killImmortals)</c> sets <c>health.IsDead = true</c> in its native body
/// <em>before</em> calling
/// <c>Kill</c>, so a guarded character would survive but read as dead. Choke point two stops
/// that write. <c>KillOrDestroyEntity</c> is a single method on this build (not a separate
/// no-<c>Health</c> overload), so it needs no prefix of its own — both of its branches reach
/// <c>Kill</c>.</para>
///
/// <para><b>Choke point three</b> is the 6-argument
/// <c>StatChangeUtility.KillEntity(EntityManager, Entity, Entity killer, double currentTime,
/// StatChangeReason, bool killImmortals)</c> (id
/// <c>ProjectM.StatChangeUtility$$KillEntity#viiidiii</c>). Patching
/// the 11-arg overload is not enough on the path that matters: this overload has
/// <c>Callers: 12</c>, reads <c>Health</c> into a stack local (the server's native
/// <c>ProjectM.StatChangeUtility.KillEntity</c>), passes it as the
/// 11-arg overload's <c>ref Health</c> and unconditionally writes the local back
/// with <c>SetComponentData&lt;Health&gt;</c> afterwards — straight over the restore the 11-arg
/// prefix just made (R33 finding 3). A prefix here returns <c>false</c> before the read, so
/// there is no write-back at all.</para>
///
/// <para><b>All three go through a Bootstrap patch slot</b> (job FHARM6, R40 finding 1). Handing
/// HarmonyX one of this class's <c>MethodInfo</c>s pins the collectible Core generation for the
/// life of the process, and the unpatch does not undo it (<c>harmony-patches.md</c> section 8).
/// Being opt-in did not save it: the developer who turns god mode on is the same developer who
/// then hot-swaps Core. The typed bodies below are unchanged and still carry the reasoning; what
/// changed is that <see cref="EnsurePatched"/> wraps each one in a <c>SlotPrefix</c> that reads
/// its arguments out of Harmony's <c>__args</c>, by the original's own parameter <em>names</em>
/// (the same binding Harmony would have done), and writes the 11-arg overload's
/// <c>ref Health</c> back into <c>__args</c>. A name that is not on the original refuses the
/// whole group rather than patching with a guessed position.</para>
///
/// <para>All three prefixes return <c>false</c> for a guarded entity and log once per entity. The
/// two <c>KillEntity</c> prefixes additionally restore <c>Health.Value</c> to
/// <see cref="GodModePolicy.RestoredValue"/> when they observe non-positive health, because
/// skipping the body is what stops the <c>IsDead</c> write but nothing in the skipped body would
/// have clamped the value the damage already wrote (R32 finding 9). The patches are registered
/// under Core's own id as <b>one atomic group of three</b> —
/// <see cref="Progenitor.Patches.PatchRegistry.All"/> rolls back whatever landed if any member is
/// refused, so half-installed god mode is not a reachable state (R32 finding 3) — applied lazily
/// on the first <see cref="Add(Entity, object?)"/>, and unpatched by <see cref="Uninstall"/>
/// before the collectible ALC unloads.</para>
///
/// <para><b>2026-10-07: the 11-arg <c>KillEntity</c> is no longer patched.</b> Everything
/// above that describes it as choke point two is history. It takes <c>ref Health</c> and
/// <c>in HealthConstants</c>; the game's Il2CppInterop (1.4.6-dev) marshals a by-ref struct
/// through 8 bytes and writes them back over the caller's struct, which corrupted
/// <c>Health.MaxHealth</c> to 4.03e20 on the facade smoke (see
/// <c>PatchSitePolicy.FindByRefValueTypeParameter</c>). The group is now
/// <c>DeathUtilities.Kill</c>, <c>KillOrDestroyEntity</c> and the 6-arg <c>KillEntity</c>; the
/// two callers that read and write back <c>Health</c> are blocked before the read, and the
/// damage pipeline (which cannot be prefixed) is covered by the <c>Kill</c> block plus a
/// next-frame repair of the <c>IsDead</c> its write-back leaves (<see cref="GodModePolicy.PlanRepair"/>).</para>
/// </summary>
public static class GodMode
{
    /// <summary>
    /// Metadata method name for <c>DeathUtilities.Kill</c>. Unique in both the client and the
    /// server metadata (one method with this name), so <see cref="Progenitor.Native.AddressId"/>'s
    /// disambiguation adds no <c>#TypeSignature</c> suffix.
    /// </summary>
    public const string KillAddressId = "ProjectM.DeathUtilities$$Kill";

    /// <summary>
    /// Metadata method name for the 11-argument <c>StatChangeUtility.KillEntity</c>. The name
    /// has three overloads in the game's metadata, so the id carries the TypeSignature suffix
    /// <c>#viiiiidiiifii</c> — the same string on both sides, but only the <b>server</b> table
    /// carries a row for it: god mode is server-only (<see cref="GodModePolicy.DecideGuard"/>)
    /// and <c>wanted-client.txt</c> deliberately has no god-mode site.
    /// </summary>
    public const string KillEntityAddressId = "ProjectM.StatChangeUtility$$KillEntity#viiiiidiiifii";

    /// <summary>
    /// Metadata method name for <c>StatChangeUtility.KillOrDestroyEntity</c>. One method with this
    /// name, so no TypeSignature suffix. Listed in <c>wanted-server.txt</c>; the server table has
    /// to be rebuilt before it carries the row.
    /// </summary>
    public const string KillOrDestroyAddressId = "ProjectM.StatChangeUtility$$KillOrDestroyEntity";

    /// <summary>
    /// Metadata method name for the <b>6-argument</b> <c>StatChangeUtility.KillEntity</c>, the
    /// 12-caller forwarder. Same three-overload disambiguation as
    /// <see cref="KillEntityAddressId"/>, so the id carries the TypeSignature suffix
    /// <c>#viiidiii</c> — and, like it, only the server table carries a row.
    /// </summary>
    public const string KillEntitySixAddressId = "ProjectM.StatChangeUtility$$KillEntity#viiidiii";

    /// <summary>Detour-site log key for <see cref="KillAddressId"/>.</summary>
    public const string SiteKill = GodModePolicy.SiteKill;

    /// <summary>Detour-site log key for <see cref="KillEntityAddressId"/> (not patched any more).</summary>
    public const string SiteKillEntity = GodModePolicy.SiteKillEntity;

    /// <summary>Detour-site log key for <see cref="KillOrDestroyAddressId"/>.</summary>
    public const string SiteKillOrDestroy = GodModePolicy.SiteKillOrDestroy;

    /// <summary>Detour-site log key for <see cref="KillEntitySixAddressId"/>.</summary>
    public const string SiteKillEntitySix = GodModePolicy.SiteKillEntitySix;

    /// <summary>
    /// Owner id every god-mode patch is tagged with: Core's own generation, shared with the
    /// world-load hook and every other Core-owned registry patch (job FHARM6). One lifetime, one
    /// sweep, one id, so Core's <c>patches:</c> lines grep together. The per-site detour keys
    /// (<see cref="SiteKill"/> and friends) are still what name an individual god-mode patch.
    /// </summary>
    public const string OwnerId = MyPluginInfo.PLUGIN_GUID + PatchSitePolicy.CoreOwnerSuffix;

    /// <summary>
    /// How many times <see cref="EnsurePatched"/> will try to register the pair before it stops
    /// (R32 finding 11). The failures worth retrying are transient: the address table is read
    /// from disk beside Core, and <c>Patches.Install()</c> runs on the first <c>Add</c>, which on
    /// a client can be mid-loading-screen. Bounded the way
    /// <c>UiInputCapture.MaxRegisterAttempts</c> bounds its own retry, so a permanently moved address
    /// costs three attempts, not one per guard.
    /// </summary>
    public const int MaxPatchAttempts = 3;

    private static readonly object Gate = new();
    private static bool _patched;
    private static int _patchAttempts;
    private static PatchHandle? _killHandle;
    private static PatchHandle? _killOrDestroyHandle;
    private static readonly HashSet<ulong> PendingRepairs = new();
    private static PatchHandle? _killEntitySixHandle;

    /// <summary>
    /// Number of live god-mode prefixes: 0 before the first guard and after a failed install,
    /// <see cref="GodModePolicy.RequiredPrefixes"/> once installed. It is never 1 or 2 — the
    /// group is registered atomically.
    /// </summary>
    public static int PatchCount
    {
        get
        {
            lock (Gate)
                return (_killHandle != null ? 1 : 0)
                       + (_killOrDestroyHandle != null ? 1 : 0)
                       + (_killEntitySixHandle != null ? 1 : 0);
        }
    }

    /// <summary>True when every kill-path prefix is live, i.e. a guard actually protects.</summary>
    public static bool Installed => PatchCount == GodModePolicy.RequiredPrefixes;

    /// <summary>Guarded entity count.</summary>
    public static int Count => GodModeGuard.Count;

    /// <summary>
    /// Stop <paramref name="entity"/> dying on the unguarded kill paths. Installs the prefixes
    /// on the first call. <paramref name="owner"/> (the BepInEx plugin instance) is recorded so
    /// <see cref="RemoveAll"/> can sweep it on unload; pass null from Core or from a caller that
    /// clears the guard itself.
    ///
    /// <para><b>Server-only</b> (R34 finding 14). A client process refuses before anything is
    /// patched — the kill paths are server-authoritative, and none of the three sites is in
    /// <c>wanted-client.txt</c>, so patching them there would run with the shared-empty-stub guard
    /// off. See <see cref="GodModePolicy.DecideGuard"/>.</para>
    ///
    /// <para>Returns true when <b>this owner</b> did not already hold the entity <b>and</b> the
    /// guard means something. Guards are reference-counted per (entity, owner) since R40 finding
    /// 5, so adding one to an entity another owner already guards is a true — the hold is new,
    /// and it outlives the other owner's removal.
    /// <c>false</c> with a warning when the prefixes are not both live: a guarded-set
    /// entry with nothing patched is not protection, and reporting success for it is what let an
    /// admin read <c>nodrain on.</c> and die to the first hit (R32 finding 5). Callers should
    /// check the return; <c>PatchCount</c> / <see cref="DescribePins"/> say how many prefixes are
    /// live.</para>
    /// </summary>
    public static bool Add(Entity entity, object? owner = null)
    {
        if (entity == Entity.Null) return false;
        ulong key = KeyOf(entity);

        switch (GodModePolicy.DecideGuard(Plugin.IsServerProcess, EnsurePatched))
        {
            case GodModePolicy.GuardDecision.RefusedClient:
                Plugin.Logger?.LogWarning(GodModePolicy.FormatClientRefused(key));
                return false;
            case GodModePolicy.GuardDecision.RefusedNotInstalled:
                Plugin.Logger?.LogWarning(GodModePolicy.FormatGuardRefused(key, PatchCount));
                return false;
        }

        bool added = GodModeGuard.Add(key, owner);
        if (added) Plugin.Logger?.LogInfo(GodModeGuard.FormatAdded(key, GodModeGuard.Count));
        return added;
    }

    /// <summary>
    /// Stop guarding <paramref name="entity"/> <b>whoever</b> holds it. True when it was guarded.
    /// Core's unconditional drop; a caller that took a guard should pass its own owner to
    /// <see cref="Remove(Entity, object?)"/> instead, so it cannot drop a guard someone else
    /// still needs (R40 finding 5).
    /// </summary>
    public static bool Remove(Entity entity)
    {
        if (entity == Entity.Null) return false;
        ulong key = KeyOf(entity);
        GodModePolicy.ForgetRestoreLog(key);
        bool removed = GodModeGuard.Remove(key);
        if (removed) Plugin.Logger?.LogInfo(GodModeGuard.FormatRemoved(key, GodModeGuard.Count));
        return removed;
    }

    /// <summary>
    /// Drop <paramref name="owner"/>'s hold on <paramref name="entity"/>. True when that owner
    /// held it. The entity stays guarded while another owner still does — and the log line is
    /// only written when the last hold goes, so <c>guard removed</c> never appears for an entity
    /// that is still unkillable.
    /// </summary>
    public static bool Remove(Entity entity, object? owner)
    {
        if (entity == Entity.Null) return false;
        ulong key = KeyOf(entity);
        bool removed = GodModeGuard.Remove(key, owner);
        if (!removed) return false;
        if (GodModeGuard.IsGuarded(key)) return true;
        GodModePolicy.ForgetRestoreLog(key);
        Plugin.Logger?.LogInfo(GodModeGuard.FormatRemoved(key, GodModeGuard.Count));
        return true;
    }

    /// <summary>True when <paramref name="entity"/> is guarded by anyone.</summary>
    public static bool IsGuarded(Entity entity) =>
        entity != Entity.Null && GodModeGuard.IsGuarded(KeyOf(entity));

    /// <summary>True when <paramref name="owner"/> itself holds a guard on <paramref name="entity"/>.</summary>
    public static bool IsGuardedBy(Entity entity, object? owner) =>
        entity != Entity.Null && GodModeGuard.IsGuardedBy(KeyOf(entity), owner);

    /// <summary>
    /// Drop every entity <paramref name="owner"/> guarded. Called from
    /// <c>PluginReloader.SweepRegistrations</c>: the plugin that knew why an entity was
    /// unkillable is going away, so nothing would be left to un-guard it.
    /// </summary>
    public static int RemoveAll(object owner) => GodModeGuard.RemoveAll(owner);

    /// <summary>Pin line: <c>godmode: guarded=1 patched=2</c>.</summary>
    public static string DescribePins() => GodModeGuard.FormatPins(Count, PatchCount);

    /// <summary>
    /// The pin line plus who holds each guard:
    /// <c>godmode: guarded=1 patched=3 owners: 45:1=[sample.nodrain,sample.godmode]</c>. What
    /// <c>ctx.GodMode.Describe()</c> answers, because "already guarded" alone does not tell an
    /// admin that their own <c>off</c> will leave the entity guarded by someone else.
    /// </summary>
    public static string DescribeGuards() => DescribePins() + " " + GodModeGuard.DescribeOwners();

    /// <summary>
    /// Unpatch the prefixes and clear the guarded set. Called from
    /// <see cref="Progenitor.Patches.Patches.Uninstall"/>, which <c>Plugin.Uninstall</c> runs
    /// before the ALC unload. Leaving a prefix applied would pin this Core generation; leaving
    /// the set populated would pin a plugin through its owner reference.
    /// </summary>
    internal static void Uninstall()
    {
        PatchHandle? kill;
        PatchHandle? killOrDestroy;
        PatchHandle? killEntitySix;
        lock (Gate)
        {
            kill = _killHandle;
            killOrDestroy = _killOrDestroyHandle;
            killEntitySix = _killEntitySixHandle;
            _killHandle = null;
            _killOrDestroyHandle = null;
            PendingRepairs.Clear();
            _killEntitySixHandle = null;
            _patched = false;
            // A new Core generation (or a re-Install) gets its full retry budget back.
            _patchAttempts = 0;
        }
        GodModePolicy.ClearRestoreLog();
        try { kill?.Dispose(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("godmode: unpatch Kill: " + e.Message); }
        try { killOrDestroy?.Dispose(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("godmode: unpatch KillOrDestroyEntity: " + e.Message); }
        try { killEntitySix?.Dispose(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("godmode: unpatch KillEntity(6): " + e.Message); }

        int cleared = GodModeGuard.Clear();
        if (cleared > 0) Plugin.Logger?.LogInfo("godmode: cleared " + cleared + " guarded entity(ies) on Uninstall.");
    }

    private static ulong KeyOf(Entity entity) => GodModeGuard.Key(entity.Index, entity.Version);

    /// <summary>
    /// Register all three prefixes as one unit. Returns true only when all three are live.
    ///
    /// <para><b>Atomic, because half-installed is worse than off</b> (R32 finding 3). The members
    /// fail independently and for different reasons — <see cref="ResolveKill"/> is a plain
    /// <c>GetMethod</c> while <see cref="ResolveKillEntity"/> and
    /// <see cref="ResolveKillEntitySix"/> walk for a parameter count, and
    /// <c>PatchSites.Register</c> gives <c>Kill</c> a prefix fallback and neither
    /// <c>KillEntity</c> overload one — so the reachable failure was <c>Kill</c> patched and
    /// <c>KillEntity</c> not, which leaves a character alive, unkillable, and reading as dead to
    /// everything that tests <c>Health.IsDead</c>. <see cref="PatchRegistry.All"/> unpatches
    /// whatever landed if any member is refused, so <see cref="PatchCount"/> is 0 or
    /// <see cref="GodModePolicy.RequiredPrefixes"/> and never in between.</para>
    ///
    /// <para><b>Bounded retry</b> (R32 finding 11): a failed install leaves <c>_patched</c> false
    /// so the next <see cref="Add(Entity, object?)"/> tries again, up to
    /// <see cref="MaxPatchAttempts"/> times per Core generation, logging the attempt number each
    /// time. <see cref="Uninstall"/> resets the counter.</para>
    /// </summary>
    private static bool EnsurePatched()
    {
        int attempt;
        lock (Gate)
        {
            if (_patched) return _killHandle != null && _killOrDestroyHandle != null && _killEntitySixHandle != null;
            if (_patchAttempts >= MaxPatchAttempts) return false;
            attempt = ++_patchAttempts;
            // Latch for the duration of this attempt so two concurrent Adds do not both patch.
            _patched = true;
        }

        Progenitor.Patches.Patches.Install();

        if (GodModePolicy.PrefixPriority != HarmonyLib.Priority.First)
            Plugin.Logger?.LogWarning(
                "godmode: GodModePolicy.PrefixPriority (" + GodModePolicy.PrefixPriority
                + ") no longer matches HarmonyLib.Priority.First (" + HarmonyLib.Priority.First
                + "); the prefixes are registered at the policy value.");

        var kill = ResolveKill();
        var killOrDestroy = ResolveKillOrDestroy();
        var killEntitySix = ResolveKillEntitySix();

        if (kill == null)
            Plugin.Logger?.LogWarning("godmode: ProjectM.DeathUtilities::Kill not found; the raw kill writer is unguarded.");
        if (killOrDestroy == null)
            Plugin.Logger?.LogWarning("godmode: ProjectM.StatChangeUtility::KillOrDestroyEntity (7 args) was not found; its Health write-back would land on a guarded character.");
        if (killEntitySix == null)
            Plugin.Logger?.LogWarning("godmode: the 6-argument ProjectM.StatChangeUtility::KillEntity was not found; its Health write-back would overwrite the restore (R33 finding 3).");

        PatchHandle[]? handles = null;
        if (kill != null && killOrDestroy != null && killEntitySix != null)
        {
            PatchSites.Register(kill, SiteKill, KillAddressId,
                () => Progenitor.Access.Access.DeathUtilities.KillPointer());
            // No prefix fallback for the two StatChangeUtility sites: the table id is their only
            // resolver (KillEntity has three overloads; KillOrDestroyEntity would need an
            // access-layer pointer accessor first). Without a table row the site resolves to
            // path=missing and is still patched, with the shared-empty-stub check unable to run.
            PatchSites.Register(killOrDestroy, SiteKillOrDestroy, KillOrDestroyAddressId, prefixFallback: null);
            PatchSites.Register(killEntitySix, SiteKillEntitySix, KillEntitySixAddressId, prefixFallback: null);

            // Bind each typed body to the slot shape by the original's own parameter names.
            // A missing name is a refusal, not a guessed position: BuildSlotPrefixes returns
            // nulls, PatchRegistry.All refuses the group, and god mode stays off (fail closed).
            var slots = BuildSlotPrefixes(kill, killOrDestroy, killEntitySix);

            handles = PatchRegistry.All(
                owner: null,
                ownerId: OwnerId,
                GodModePolicy.BuildRequests(
                    kill, slots.Kill,
                    killOrDestroy, slots.KillOrDestroy,
                    killEntitySix, slots.KillEntitySix));
        }

        if (handles == null || handles.Length != GodModePolicy.RequiredPrefixes)
        {
            lock (Gate)
            {
                _killHandle = null;
                _killOrDestroyHandle = null;
                _killEntitySixHandle = null;
                if (_patchAttempts < MaxPatchAttempts) _patched = false;
            }
            Plugin.Logger?.LogWarning(GodModePolicy.FormatInstallFailed(attempt, MaxPatchAttempts));
            return false;
        }

        lock (Gate)
        {
            _killHandle = handles[0];
            _killOrDestroyHandle = handles[1];
            _killEntitySixHandle = handles[2];
        }
        return true;
    }

    /// <summary>The three slot-shaped bodies, or nulls where a parameter name did not resolve.</summary>
    private readonly struct SlotPrefixes
    {
        internal SlotPrefixes(SlotPrefix? kill, SlotPrefix? killOrDestroy, SlotPrefix? killEntitySix)
        {
            Kill = kill;
            KillOrDestroy = killOrDestroy;
            KillEntitySix = killEntitySix;
        }

        internal SlotPrefix? Kill { get; }
        internal SlotPrefix? KillOrDestroy { get; }
        internal SlotPrefix? KillEntitySix { get; }
    }

    /// <summary>
    /// Adapt the three typed prefix bodies to the pool's signature-agnostic shape (job FHARM6).
    ///
    /// <para><b>Why by name and not by position.</b> Harmony binds a prefix's parameters by
    /// matching their names against the original's; a slot body is handed <c>__args</c>
    /// positionally, so the name lookup that Harmony was doing has to happen here instead. Doing
    /// it at registration time rather than hard-coding an index means a game update that inserts
    /// an argument moves the index with it, and a game update that <em>renames</em> or removes
    /// one refuses the group with a warning naming the parameter — the same loud failure
    /// <see cref="ResolveKillEntity"/> already gives for a changed argument count, rather than a
    /// prefix reading somebody else's argument as an <c>Entity</c>.</para>
    ///
    /// <para><b>No by-ref argument.</b> None of the three originals takes a struct by
    /// reference, so no body writes anything back into <c>__args</c>. The 11-arg
    /// <c>KillEntity</c>'s <c>ref Health</c> is why that overload left the group (see the class
    /// remarks).</para>
    /// </summary>
    private static SlotPrefixes BuildSlotPrefixes(MethodBase? kill, MethodBase? killOrDestroy, MethodBase? killEntitySix)
    {
        const string Consequence = "the whole kill-path group is refused";
        int killed = ArgIndex(kill, "killed", Consequence);
        int entityKod = ArgIndex(killOrDestroy, "entity", Consequence);
        int entity6 = ArgIndex(killEntitySix, "entity", Consequence);

        SlotPrefix? killSlot = killed < 0 ? null : (_, args) =>
            !TryArg<Entity>(args, killed, SiteKill, "killed", out var e) || KillPrefix(e);

        SlotPrefix? killOrDestroySlot = entityKod < 0 ? null : (_, args) =>
            !TryArg<Entity>(args, entityKod, SiteKillOrDestroy, "entity", out var e) || KillOrDestroyPrefix(e);

        SlotPrefix? killEntitySixSlot = entity6 < 0 ? null : (_, args) =>
            !TryArg<Entity>(args, entity6, SiteKillEntitySix, "entity", out var e) || KillEntitySixPrefix(e);

        return new SlotPrefixes(killSlot, killOrDestroySlot, killEntitySixSlot);
    }

    /// <summary>
    /// Position of <paramref name="name"/> on <paramref name="target"/>, or -1 with a warning
    /// naming the parameter and the method. -1 refuses the whole group.
    ///
    /// <para>Job FHARM8: the lookup itself is <see cref="PatchRegistry.ArgIndex"/> and the
    /// warning is <see cref="SlotArgs.FormatMissingParameter"/>. This used to be a private copy
    /// here, which is exactly why R41 found the fail-closed claim untested: <c>GodMode.cs</c>
    /// references <c>ProjectM</c> and is not linked into the game-free test project, so nothing
    /// could drive it. <c>SlotArgsTests</c> drives the shared one.</para>
    /// </summary>
    private static int ArgIndex(MethodBase? target, string name, string consequence)
    {
        int index = PatchRegistry.ArgIndex(target, name);
        if (index < 0)
        {
            Plugin.Logger?.LogWarning(
                SlotArgs.FormatMissingParameter(target?.Name, name, consequence));
        }
        return index;
    }

    /// <summary>
    /// Read one <c>__args</c> slot as <typeparamref name="T"/>. False (fail open, so the original
    /// runs) with one warning when the argument is missing or is not the expected type — the same
    /// rule the typed bodies' own try/catch follows: a broken guard must not break the game's
    /// kill path.
    ///
    /// <para>Job FHARM8: the read is <see cref="SlotArgs.TryArg{T}"/> and the warning is
    /// <see cref="SlotArgs.FormatWrongArgumentType"/>; only the decision to log stays here,
    /// because a null <c>__args</c> or an out-of-range index is a registration bug that
    /// <see cref="ArgIndex"/> already refused, while a wrong type is a live-build surprise worth
    /// a line.</para>
    /// </summary>
    private static bool TryArg<T>(object?[] args, int index, string site, string name, out T value)
        where T : struct
    {
        if (SlotArgs.TryArg(args, index, out value)) return true;
        if (args != null && index >= 0 && index < args.Length)
        {
            Plugin.Logger?.LogWarning(
                SlotArgs.FormatWrongArgumentType(site, name, index, args[index], typeof(T)));
        }
        return false;
    }

    /// <summary>
    /// Job FACC: the hand-rolled <c>GetMethod(nameof(Kill), Public | Static)</c> moved into the
    /// generated access layer. <c>ProjectM.DeathUtilities Kill</c> is declared in
    /// <c>src/Progenitor.Core/Access/access.txt</c>, so the resilience report names it if a game
    /// update removes or overloads it — and the accessor refuses an ambiguous name rather than
    /// returning whichever overload reflection happened to pick, which is the behavior
    /// <see cref="ResolveKillEntity"/> already had to hand-write for <c>KillEntity</c>.
    /// </summary>
    private static MethodBase? ResolveKill()
    {
        try
        {
            return Progenitor.Access.Access.DeathUtilities.Kill();
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("godmode: resolve DeathUtilities.Kill: " + e.Message);
            return null;
        }
    }

    /// <summary>
    /// <c>KillOrDestroyEntity</c>, selected by name and a parameter count of 7, and only when it
    /// is the one method with that name, so a future overload fails loudly (no patch, one
    /// warning) instead of patching a sibling.
    /// </summary>
    private static MethodBase? ResolveKillOrDestroy()
    {
        try
        {
            MethodBase? found = null;
            foreach (var m in typeof(StatChangeUtility).GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (!string.Equals(m.Name, nameof(StatChangeUtility.KillOrDestroyEntity), StringComparison.Ordinal)) continue;
                if (found != null) return null;
                found = m;
            }
            return found != null && found.GetParameters().Length == 7 ? found : null;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("godmode: resolve StatChangeUtility.KillOrDestroyEntity: " + e.Message);
            return null;
        }
    }

    /// <summary>
    /// The 6-argument overload: <c>KillEntity(EntityManager, Entity, Entity killer, double
    /// currentTime, StatChangeReason reason, bool killImmortals)</c>. Selected by
    /// parameter count, which is unambiguous here — the three overloads take 11, 6 and 4
    /// arguments.
    /// </summary>
    private static MethodBase? ResolveKillEntitySix()
    {
        try
        {
            foreach (var m in typeof(StatChangeUtility).GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (!string.Equals(m.Name, nameof(StatChangeUtility.KillEntity), StringComparison.Ordinal)) continue;
                if (m.GetParameters().Length == 6) return m;
            }
            return null;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("godmode: resolve the 6-arg StatChangeUtility.KillEntity: " + e.Message);
            return null;
        }
    }

    /// <summary>
    /// Prefix on <c>ProjectM.DeathUtilities.Kill</c>. Parameter name <c>killed</c> matches the
    /// original's, which is how Harmony binds it. Returning false skips the whole body —
    /// the <c>Dead</c> add, the <c>DeathReason</c> write and the <c>DeathEvent</c> entity.
    ///
    /// <para>A block here can follow the 11-arg <c>KillEntity</c> having set <c>IsDead</c> on a
    /// caller's local (the damage pipeline, which cannot be prefixed), and that caller writes the
    /// local back after this returns. So a block also posts a repair to the next main-thread
    /// drain, which runs after that write.</para>
    /// </summary>
    private static bool KillPrefix(Entity killed)
    {
        bool blocked = Blocked(killed, GodModeGuard.SiteKill);
        if (blocked) ScheduleRepair(killed);
        return !blocked;
    }

    /// <summary>
    /// Prefix on <c>ProjectM.StatChangeUtility.KillOrDestroyEntity</c>. Same shape and reason
    /// as <see cref="KillEntitySixPrefix"/>: the body reads <c>Health</c> into a local, calls the
    /// 11-arg <c>KillEntity</c> with it and writes it back, so returning false before the read
    /// leaves nothing to write back. The restore still runs for the damage already done.
    /// </summary>
    private static bool KillOrDestroyPrefix(Entity entity)
    {
        bool blocked = Blocked(entity, GodModeGuard.SiteKillOrDestroy);
        if (blocked) RestoreHealth(entity);
        return !blocked;
    }

    /// <summary>
    /// Queue one <see cref="RepairAfterBlockedKill"/> per entity until it runs. Never throws.
    /// </summary>
    private static void ScheduleRepair(Entity entity)
    {
        try
        {
            ulong key = KeyOf(entity);
            lock (Gate)
            {
                if (!PendingRepairs.Add(key)) return;
            }
            Progenitor.Dev.MainThread.Post(() => RepairAfterBlockedKill(entity, key));
        }
        catch
        {
            // Same rule as Blocked: a broken repair must not break the game's kill path.
        }
    }

    /// <summary>
    /// The next-frame half of a blocked <c>Kill</c>: if the entity is still guarded, has no
    /// <c>Dead</c> component and reads as dead, clear <c>IsDead</c> and lift a non-positive
    /// <c>Value</c> to <see cref="GodModePolicy.RestoredValue"/>. Touches nothing else.
    /// </summary>
    private static void RepairAfterBlockedKill(Entity entity, ulong key)
    {
        lock (Gate) PendingRepairs.Remove(key);
        try
        {
            if (!GodModeGuard.IsGuarded(key)) return;
            var world = Worlds.Server ?? Worlds.Client;
            if (world == null || !world.IsCreated) return;
            var em = world.EntityManager;
            if (!em.Exists(entity)) return;
            if (!entity.TryRead<Health>(em, out var health)) return;

            bool wasDead = health.IsDead;
            float was = health.Value;
            if (!GodModePolicy.PlanRepair(wasDead, entity.Has<Dead>(em), was, out bool clearIsDead, out float value))
                return;

            if (clearIsDead) health.IsDead = false;
            health.Value = value;
            entity.Write(em, in health);

            if (GodModePolicy.ShouldLogRestore(key, NowSeconds()))
                Plugin.Logger?.LogInfo(GodModePolicy.FormatRepaired(key, wasDead, was, value));
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("godmode: repair after a blocked kill failed: " + e.Message);
        }
    }

    /// <summary>
    /// Prefix on the <b>6-argument</b> <c>ProjectM.StatChangeUtility.KillEntity</c>, the
    /// 12-caller forwarder. Parameter name <c>entity</c> matches the original's, which is how
    /// Harmony binds it.
    ///
    /// <para><b>Why blocking here is the fix, not a duplicate</b> (R33 finding 3). This body owns
    /// the authoritative copy of <c>Health</c>: in the server's native
    /// <c>ProjectM.StatChangeUtility.KillEntity</c> it reads the component into a stack local, hands
    /// <c>&amp;local</c> to the 11-arg overload as its <c>ref Health</c>, and then
    /// writes that local back unconditionally. The 11-arg prefix's restore writes the
    /// chunk, so on this path the stale local — <c>Value</c> still non-positive — lands on top of
    /// it the instant the call returns, and the character is alive, unkillable and reading as dead
    /// again while the log says it was repaired. Returning <c>false</c> here skips the read, the
    /// inner call and the write-back together, so there is nothing left to overwrite. The restore
    /// still runs, because on this path the 11-arg prefix never will and the damage that got here
    /// has already driven <c>Health.Value</c> down in the chunk.</para>
    /// </summary>
    private static bool KillEntitySixPrefix(Entity entity)
    {
        bool blocked = Blocked(entity, GodModeGuard.SiteKillEntitySix);
        if (blocked) RestoreHealth(entity);
        return !blocked;
    }

    /// <summary>
    /// Put <c>Health.Value</c> back above zero for a guarded entity whose kill was just blocked.
    /// Typed read-modify-write through the toolkit (<c>EntityExtensions.TryRead</c> /
    /// <c>Write&lt;Health&gt;</c>, i.e. <c>GetComponentDataRawRO</c> and
    /// <c>SetComponentDataRaw</c> with the ECS <c>TypeSize</c> check) — never a raw offset poke.
    ///
    /// <para><c>ProjectM.Health</c> (server metadata) is
    /// <c>ModifiableFloat MaxHealth @0x0</c>, <c>double TimeOfDeath @0x8</c>,
    /// <c>float Value @0x10</c>, <c>float MaxRecoveryHealth @0x14</c>, <c>bool IsDead @0x18</c>.
    /// Only <c>Value</c> is changed: <c>IsDead</c> is still false here (the original never ran),
    /// and <c>TimeOfDeath</c> belongs to the death the prefix just prevented.</para>
    ///
    /// <para>Never throws and never blocks the block: a failed restore leaves the kill refused,
    /// which is still the guard doing its job.</para>
    /// </summary>
    private static void RestoreHealth(Entity entity)
    {
        try
        {
            var world = Worlds.Server ?? Worlds.Client;
            if (world == null || !world.IsCreated) return;

            var em = world.EntityManager;
            if (!entity.TryRead<Health>(em, out var health)) return;
            if (!GodModePolicy.NeedsRestore(health.Value)) return;

            float was = health.Value;
            health.Value = GodModePolicy.Restore(health.Value);
            entity.Write(em, in health);

            ulong key = GodModeGuard.Key(entity.Index, entity.Version);
            if (GodModePolicy.ShouldLogRestore(key, NowSeconds()))
                Plugin.Logger?.LogInfo(GodModePolicy.FormatRestored(key, was, health.Value));
        }
        catch
        {
            // Same rule as Blocked: a broken repair must not break the game's kill path.
        }
    }

    /// <summary>
    /// Monotonic seconds for the restore-log throttle. <see cref="Environment.TickCount64"/>
    /// rather than a wall clock or a Unity time: this runs on the sim thread inside a kill path
    /// and must not depend on a world being up.
    /// </summary>
    private static double NowSeconds() => Environment.TickCount64 / 1000.0;

    /// <summary>
    /// Shared body of both prefixes. Runs on the sim thread inside the game's kill path, so it
    /// does one dictionary probe and, at most once per entity, one log line. Never throws: an
    /// exception here would surface as a Harmony error inside a game system.
    /// </summary>
    private static bool Blocked(Entity entity, string site)
    {
        try
        {
            if (entity == Entity.Null) return false;
            ulong key = GodModeGuard.Key(entity.Index, entity.Version);
            if (!GodModeGuard.ShouldBlock(key, site, out var line)) return false;
            if (line != null) Plugin.Logger?.LogInfo(line);
            return true;
        }
        catch
        {
            // Fail open on the guard, never on the game: a broken guard must not break a kill.
            return false;
        }
    }
}

/// <summary>
/// <c>ctx.GodMode</c>: mark entities the game's unguarded kill paths may not kill. Entries are
/// tagged with the plugin and dropped by <c>PluginReloader.SweepRegistrations</c> on unload, so a
/// removed plugin cannot leave a player permanently unkillable with nothing left to undo it
/// (the R27 finding 2 failure mode).
/// </summary>
public sealed class PluginGodMode
{
    private readonly object _plugin;
    private readonly object _owner;

    /// <summary>Bind the guard set to one plugin. <c>PluginContext</c> constructs this.</summary>
    public PluginGodMode(object owner)
    {
        _plugin = owner ?? throw new ArgumentNullException(nameof(owner));
        _owner = _plugin;
    }

    private PluginGodMode(object plugin, object owner)
    {
        _plugin = plugin;
        _owner = owner;
    }

    /// <summary>
    /// A second owner inside this plugin. Two features of one plugin that guard the same
    /// character — a debug buff and a debug command, say — must not be able to drop each other's
    /// guard, and a plugin instance alone cannot tell them apart (R40 finding 5). Give each
    /// feature its own <paramref name="label"/>; <see cref="Add"/> and <see cref="Remove"/> on
    /// the returned handle are scoped to it, while unload (and <see cref="RemoveAll"/>) still
    /// sweeps every label this plugin took.
    /// </summary>
    public PluginGodMode Scope(string label) =>
        new PluginGodMode(_plugin, new GodModeGuard.GodModeOwner(_plugin, label));

    /// <summary>
    /// Guard <paramref name="entity"/>. Installs the kill-path prefixes on first use.
    /// <b>Server-only</b>: a client process refuses with one line and patches nothing, the way
    /// <c>ctx.Units</c> returns a disabled handle, so shared plugin code needs no side check.
    /// <b>Check the return.</b> False means "this owner already holds it", "client process", or "not
    /// installed — this entity is not protected"; <see cref="Describe"/> distinguishes them
    /// (<c>patched=0</c>), and every refusal is logged as a warning.
    /// </summary>
    public bool Add(Entity entity) => GodMode.Add(entity, _owner);

    /// <summary>
    /// Drop <b>this owner's</b> hold on <paramref name="entity"/>. True when it held one. The
    /// entity stays guarded while another owner still holds it, so this can never unguard a
    /// character somebody else is keeping alive — check <see cref="IsGuarded"/> afterwards, or
    /// read <see cref="Describe"/>, if the reply needs to say so.
    /// </summary>
    public bool Remove(Entity entity) => GodMode.Remove(entity, _owner);

    /// <summary>True when <paramref name="entity"/> is guarded (by anyone).</summary>
    public bool IsGuarded(Entity entity) => GodMode.IsGuarded(entity);

    /// <summary>True when <b>this</b> owner holds a guard on <paramref name="entity"/>.</summary>
    public bool HoldsGuard(Entity entity) => GodMode.IsGuardedBy(entity, _owner);

    /// <summary>Drop every entity this plugin guarded, under any label. Unload does this for you.</summary>
    public int RemoveAll() => GodMode.RemoveAll(_plugin);

    /// <summary>
    /// Status line: <c>godmode: guarded=1 patched=3 owners: 45:1=[sample.nodrain]</c> — the pin
    /// counts plus who holds each guard.
    /// </summary>
    public string Describe() => GodMode.DescribeGuards();
}
