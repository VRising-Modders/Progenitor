using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using Progenitor.Patches;

namespace Progenitor.GodMode;

/// <summary>
/// The game-free half of the kill-path prefixes' <em>decisions</em> (job FHARM2, R32 findings 5
/// and 9). Deliberately holds no entity state that <see cref="GodModeGuard"/> owns — this is the
/// prefix-side policy, not the guard set — so it can be unit-tested without a V Rising install
/// and edited without touching the guarded-entity structure.
///
/// <para><b>Why a health restore exists at all.</b> Blocking a kill is not the same as leaving a
/// consistent entity. The 11-argument
/// <c>StatChangeUtility.KillEntity(EntityManager, Entity, Entity killer, ref Health, in
/// HealthConstants, double, StatChangeReason, Entity, RagdollSourceSetting, float, bool)</c>
/// is reached with <c>Health.Value</c> already
/// driven to zero or below by the damage that triggered it; the body it then runs sets
/// <c>health.IsDead = true</c> in the server's native body before
/// forwarding to <c>DeathUtilities.Kill</c>. A prefix that returns <c>false</c> skips both, which
/// is correct for <c>IsDead</c> and leaves nothing to clamp the value: the guarded character sits
/// at non-positive health with <c>IsDead == false</c>, every system that tests
/// <c>Health.Value &lt;= 0</c> reads it as dead, the health bar shows zero, and the damage source
/// re-enters the kill path every tick for as long as it persists. Restoring the value to
/// <see cref="RestoredValue"/> in the prefix — before the original would have written
/// <c>IsDead</c> — is what makes the blocked kill a consistent state rather than a latched one.
///
/// <para><c>ProjectM.Health</c> layout (server metadata): <c>ModifiableFloat MaxHealth</c>
/// at <c>0x0</c>, <c>double TimeOfDeath</c> at <c>0x8</c>, <c>float Value</c> at <c>0x10</c>,
/// <c>float MaxRecoveryHealth</c> at <c>0x14</c>, <c>bool IsDead</c> at <c>0x18</c>. Only
/// <c>Value</c> is written, through the typed <c>EntityExtensions.Write&lt;Health&gt;</c> path
/// (<c>SetComponentDataRaw</c> with the ECS <c>TypeSize</c> check), never a raw blob.</para>
/// </para>
/// </summary>
public static class GodModePolicy
{
    /// <summary>
    /// Number of live kill-path prefixes god mode needs before a guard means anything. All
    /// three, or none: <c>DeathUtilities.Kill</c> (the raw writer every path reaches), the 6-arg
    /// <c>StatChangeUtility.KillEntity</c> forwarder (12 callers) and
    /// <c>StatChangeUtility.KillOrDestroyEntity</c>. The last two read <c>Health</c> into a
    /// stack local, call the 11-arg <c>KillEntity</c> with <c>&amp;local</c> and write the local
    /// back, so blocking them before the read leaves nothing to write back.
    ///
    /// <para><b>The 11-arg <c>KillEntity</c> is deliberately not patched</b> (2026-10-07). It
    /// takes <c>ref Health</c> and <c>in HealthConstants</c>, and the game's Il2CppInterop
    /// (1.4.6-dev) marshals a by-ref struct through an 8-byte <c>Ldind_I</c> / <c>Stind_I</c>
    /// pair, so while that patch was installed every call wrote 8 bytes over
    /// <c>Health.MaxHealth</c> in the caller's local, which the caller then stored. That is the
    /// <c>MaxHealth = 4.03e20</c> seen on the facade smoke. <c>PatchRegistry</c> now refuses
    /// any such target (<c>PatchSitePolicy.ReasonByRefValueType</c>). Its third caller, the
    /// damage pipeline <c>StatChangeSystem.ApplyHealthChangeToEntity</c>, takes by-ref structs
    /// itself and cannot be prefixed either; on that path the <c>Kill</c> prefix blocks the death
    /// and <see cref="PlanRepair"/> undoes the <c>IsDead</c> the caller wrote back, one frame
    /// later.</para>
    /// </summary>
    public const int RequiredPrefixes = 3;

    /// <summary>
    /// Harmony priority every god-mode prefix is registered at — the numeric value of
    /// <c>HarmonyLib.Priority.First</c>. Spelled as a number here because this file is on the
    /// test compile list, which forbids a Harmony reference; <c>GodMode.EnsurePatched</c> checks
    /// it against <c>HarmonyLib.Priority.First</c> at install time so the two cannot drift.
    /// </summary>
    public const int PrefixPriority = 800;

    /// <summary>Detour-site log key for <c>ProjectM.DeathUtilities.Kill</c>.</summary>
    public const string SiteKill = "godmode.kill";

    /// <summary>
    /// Detour-site log key the 11-argument <c>StatChangeUtility.KillEntity</c> used. Not patched
    /// any more (see <see cref="RequiredPrefixes"/>); kept so existing callers compile.
    /// </summary>
    public const string SiteKillEntity = "godmode.killentity";

    /// <summary>
    /// Detour-site log key for <c>StatChangeUtility.KillOrDestroyEntity(EntityManager, Entity,
    /// Entity killer, Entity killerSource, double, StatChangeReason, bool killImmortals)</c> —
    /// one overload, no by-ref parameters. In the server's native body it reads <c>Health</c>
    /// into a local, calls the 11-arg <c>KillEntity</c> and writes the local back; its
    /// no-<c>Health</c> branch calls <c>DeathUtilities.Kill</c> directly.
    /// </summary>
    public const string SiteKillOrDestroy = "godmode.killordestroy";

    /// <summary>
    /// Detour-site log key for the <b>6-argument</b>
    /// <c>StatChangeUtility.KillEntity(EntityManager, Entity, Entity killer, double currentTime,
    /// StatChangeReason, bool killImmortals)</c> — id
    /// <c>ProjectM.StatChangeUtility$$KillEntity#viiidiii</c>.
    ///
    /// <para><b>Why it needs its own prefix</b> (R33 finding 3). This overload — <c>Callers: 12</c>
    /// in the game's native code, i.e. the overload essentially every gameplay path reaches,
    /// and the one the 4-arg forwarder calls — reads <c>Health</c> into a <em>stack local</em>,
    /// passes <c>&amp;local</c> as the 11-arg overload's <c>ref Health</c>, and then
    /// <b>unconditionally writes that local back</b> with <c>SetComponentData&lt;Health&gt;</c>.
    /// The caller's copy is authoritative — that write-back is how the 11-arg body's
    /// <c>IsDead = true</c> reaches the entity at all. So the 11-arg prefix's restore, which
    /// writes the chunk, is overwritten by the stale local the moment the call returns, and the
    /// alive-but-reads-as-dead state R32 finding 9 described comes straight back with a log line
    /// claiming it was fixed. Blocking here — a prefix returning <c>false</c> — skips the read,
    /// the call and the write-back together, so there is nothing left to overwrite the restore.
    /// </para>
    /// </summary>
    public const string SiteKillEntitySix = "godmode.killentity6";

    /// <summary>
    /// The god-mode registration group, in order: <c>DeathUtilities.Kill</c>,
    /// <c>StatChangeUtility.KillOrDestroyEntity</c>, the 6-arg <c>StatChangeUtility.KillEntity</c>.
    /// All three are prefixes at <see cref="PrefixPriority"/>, labeled with their detour-site keys
    /// so <c>patches: group refused …</c> names the member that failed. None of the three takes a
    /// struct by reference (checked against the interop assembly: <c>Kill(EntityManager, Entity,
    /// Dead, DeathEvent, DeathReason)</c>, <c>KillOrDestroyEntity(EntityManager, Entity, Entity,
    /// Entity, double, StatChangeReason, bool)</c>, <c>KillEntity(EntityManager, Entity, Entity,
    /// double, StatChangeReason, bool)</c>), and <c>PatchRegistry</c> would refuse the group if
    /// a future build changed that.
    ///
    /// <para><b>Slot-routed</b> (job FHARM6, R40 finding 1). The three bodies arrive as
    /// <see cref="SlotPrefix"/> delegates, not as <see cref="MethodInfo"/>s, so
    /// <c>PatchRegistry</c> parks each one in a <c>Progenitor.Bootstrap.PatchSlots</c> slot and
    /// HarmonyX is handed a Bootstrap static instead of a Core method.</para>
    ///
    /// <para>Game-free on purpose: the targets arrive as <see cref="MethodBase"/> and the bodies
    /// as delegates, so a test can drive the whole group through <c>PatchRegistry.All</c> and a
    /// fake applier without a V Rising install. A null target or handler is passed through
    /// unchanged — <c>PatchRegistry.Add</c> refuses it, which refuses the group, which is the
    /// wanted behavior.</para>
    /// </summary>
    public static PatchRequest[] BuildRequests(
        MethodBase? kill, SlotPrefix? killPrefix,
        MethodBase? killOrDestroy, SlotPrefix? killOrDestroyPrefix,
        MethodBase? killEntitySix, SlotPrefix? killEntitySixPrefix) =>
        new[]
        {
            new PatchRequest(kill, killPrefix, PatchKind.Prefix, PrefixPriority, SiteKill),
            new PatchRequest(killOrDestroy, killOrDestroyPrefix, PatchKind.Prefix, PrefixPriority, SiteKillOrDestroy),
            new PatchRequest(killEntitySix, killEntitySixPrefix, PatchKind.Prefix, PrefixPriority, SiteKillEntitySix),
        };

    /// <summary>
    /// Value written to <c>Health.Value</c> when a blocked kill observes non-positive health. One
    /// hit point, not full health: god mode is a debug guard, not a heal, and the caller that
    /// wants a survivable character is expected to write health (or <c>Immortal</c>) itself. It
    /// only has to be above zero to stop the every-tick retry and the "reads as dead" state.
    /// </summary>
    public const float RestoredValue = 1f;

    /// <summary>
    /// Seconds between restore log lines for the same entity. The guard's blocked-kill line is
    /// once per entity for the life of the guard; a restore can genuinely recur (each new damage
    /// source drives health down again), so this one is a window rather than a latch — often
    /// enough to show a character is being hit continuously, rare enough not to be a line per
    /// tick.
    /// </summary>
    public const double RestoreLogWindowSeconds = 30.0;

    private static readonly object Gate = new();
    private static readonly Dictionary<ulong, double> LastRestoreLog = new();

    /// <summary>
    /// True when the prefix must put <c>Health.Value</c> back. Written as <c>!(value &gt; 0)</c>
    /// so <c>NaN</c> — which no comparison against zero catches — is also treated as broken.
    /// </summary>
    public static bool NeedsRestore(float value) => !(value > 0f);

    /// <summary>
    /// The mechanism-level clamp (R33 finding 3, R34 finding 9): repair the <b>argument</b>, not
    /// only the store. Returns true when it changed <paramref name="argumentValue"/>.
    ///
    /// <para><b>Why this is the fix and a per-overload prefix is not.</b> Every one of the 11-arg
    /// <c>KillEntity</c>'s three callers — the whole caller set, from
    /// the server's native code — has the same
    /// shape: read <c>Health</c> into a stack local, pass <c>&amp;local</c> as the 11-arg
    /// overload's <c>ref Health</c>, then write that local back with
    /// <c>SetComponentData&lt;Health&gt;</c> after the call returns.</para>
    ///
    /// <list type="table">
    /// <item><term>6-arg KillEntity</term><description>the 6-arg forwarder
    /// <c>StatChangeUtility.KillEntity</c> — read, call, write-back (12 callers of its
    /// own).</description></item>
    /// <item><term>KillOrDestroyEntity</term><description><c>StatChangeUtility.KillOrDestroyEntity</c>
    /// — read, call, write-back (its no-<c>Health</c> branch takes <c>DeathUtilities.Kill</c>
    /// directly).</description></item>
    /// <item><term>ApplyHealthChangeToEntity</term><description><c>Gameplay.Systems.StatChangeSystem
    /// .ApplyHealthChangeToEntity</c>, the damage pipeline itself — reads <c>Health</c> into a
    /// stack local, passes its address to the call, and writes back from a copy seeded from that
    /// local after the call.</description></item>
    /// </list>
    ///
    /// <para>So the caller's copy is authoritative on <b>all three</b>, and a prefix that writes
    /// only the chunk is undone on all three the instant the call returns — the alive, unkillable,
    /// reads-as-dead state of R32 finding 9, with a log line claiming it was repaired. Clamping
    /// the by-ref argument in place makes the caller's own write-back carry the restore instead of
    /// erasing it, whichever caller it is, including a caller a future build adds. The chunk write
    /// stays as well: it is harmless where the write-back also lands, and it is the only repair on
    /// a hypothetical caller that passes a pointer into the chunk and writes nothing back.</para>
    /// </summary>
    /// <remarks>
    /// No longer called by <c>GodMode</c>: the only by-ref <c>Health</c> it ever saw was the
    /// 11-arg <c>KillEntity</c>'s, and that overload is not patched any more (see
    /// <see cref="RequiredPrefixes"/>). Kept as public API.
    /// </remarks>
    public static bool ClampArgument(ref float argumentValue)
    {
        if (!NeedsRestore(argumentValue)) return false;
        argumentValue = RestoredValue;
        return true;
    }

    /// <summary>
    /// <see cref="RestoredValue"/> when <paramref name="value"/> needs restoring, otherwise
    /// <paramref name="value"/> unchanged. The prefix must never lower a healthy character's
    /// health.
    /// </summary>
    public static float Restore(float value) => NeedsRestore(value) ? RestoredValue : value;

    /// <summary>
    /// True when a restore for <paramref name="key"/> may log, and records the time. Windowed by
    /// <see cref="RestoreLogWindowSeconds"/>; <paramref name="nowSeconds"/> is a monotonic clock
    /// the caller supplies (the prefixes pass <c>Environment.TickCount64 / 1000.0</c>) so the
    /// throttle is testable without one.
    /// </summary>
    public static bool ShouldLogRestore(ulong key, double nowSeconds)
    {
        lock (Gate)
        {
            if (LastRestoreLog.TryGetValue(key, out double last)
                && nowSeconds - last < RestoreLogWindowSeconds
                && nowSeconds >= last)
                return false;
            LastRestoreLog[key] = nowSeconds;
            return true;
        }
    }

    /// <summary>Forget one entity's restore-log window (the guard for it went away).</summary>
    public static void ForgetRestoreLog(ulong key)
    {
        lock (Gate) LastRestoreLog.Remove(key);
    }

    /// <summary>Forget every restore-log window. <c>GodMode.Uninstall</c> calls this.</summary>
    public static int ClearRestoreLog()
    {
        lock (Gate)
        {
            int n = LastRestoreLog.Count;
            LastRestoreLog.Clear();
            return n;
        }
    }

    /// <summary>
    /// <c>godmode: restored health entity=45:1 from=0 to=1 (kill blocked before Health.IsDead was set)</c>
    /// </summary>
    public static string FormatRestored(ulong key, float from, float to) =>
        "godmode: restored health entity=" + GodModeGuard.FormatKey(key)
        + " from=" + from.ToString("0.###", CultureInfo.InvariantCulture)
        + " to=" + to.ToString("0.###", CultureInfo.InvariantCulture)
        + " (kill blocked before Health.IsDead was set)";

    /// <summary>
    /// The deferred repair after a blocked <c>DeathUtilities.Kill</c>. On the damage-pipeline
    /// path (<c>ApplyHealthChangeToEntity</c> → 11-arg <c>KillEntity</c> → <c>Kill</c>) nothing
    /// before <c>Kill</c> can be prefixed, so the 11-arg body has already set <c>IsDead</c> on
    /// the caller's local and the caller writes it back after <c>Kill</c> returns. A repair has
    /// to run after that write, so <c>GodMode</c> posts it to the next main-thread drain and this
    /// decides what it does there.
    ///
    /// <para>Returns true when something must be written. <paramref name="hasDeadComponent"/>
    /// true means the entity really died through a path that was not blocked (or the guard was
    /// dropped first); that is never undone here. Otherwise <c>IsDead</c> is cleared and a
    /// non-positive (or NaN) <c>Value</c> becomes <see cref="RestoredValue"/>. Never lowers a
    /// healthy value and never touches <c>MaxHealth</c>.</para>
    /// </summary>
    public static bool PlanRepair(bool isDead, bool hasDeadComponent, float value, out bool clearIsDead, out float newValue)
    {
        clearIsDead = false;
        newValue = value;
        if (hasDeadComponent) return false;
        bool restore = NeedsRestore(value);
        if (restore) newValue = RestoredValue;
        clearIsDead = isDead;
        return clearIsDead || restore;
    }

    /// <summary>
    /// <c>godmode: repaired health entity=45:1 isDead=True->False from=-3 to=1 (blocked DeathUtilities.Kill; the caller wrote its Health back)</c>
    /// </summary>
    public static string FormatRepaired(ulong key, bool wasDead, float from, float to) =>
        "godmode: repaired health entity=" + GodModeGuard.FormatKey(key)
        + " isDead=" + (wasDead ? "True->False" : "False")
        + " from=" + from.ToString("0.###", CultureInfo.InvariantCulture)
        + " to=" + to.ToString("0.###", CultureInfo.InvariantCulture)
        + " (blocked DeathUtilities.Kill; the caller wrote its Health back)";

    /// <summary>What <see cref="DecideGuard"/> concluded about one <c>GodMode.Add</c>.</summary>
    public enum GuardDecision
    {
        /// <summary>Server process, group live: guard the entity.</summary>
        Allowed,

        /// <summary>Client process: god mode is server-only, nothing is patched (R34 finding 14).</summary>
        RefusedClient,

        /// <summary>Server process, but the prefix group is not live (R32 finding 5).</summary>
        RefusedNotInstalled,
    }

    /// <summary>
    /// The whole <c>GodMode.Add</c> gate, game-free so the client refusal is testable without a
    /// V Rising install. <paramref name="ensurePatched"/> is <c>GodMode.EnsurePatched</c>.
    ///
    /// <para><b>God mode is server-only</b> (R34 finding 14). The order matters as much as the
    /// answer: the side check runs <b>before</b> <paramref name="ensurePatched"/>, so a client
    /// process never reaches <c>PatchRegistry</c> at all. All three sites live in
    /// <c>tools/addresslib/wanted-server.txt</c> and none in <c>wanted-client.txt</c>, so on a
    /// client every one of them would resolve to path <c>unknown</c> — and per
    /// <c>PatchSites</c>'s own contract an <c>unknown</c> site is still patched but with the
    /// <b>shared-empty-stub guard off</b>, for exactly the three sites this module singled out as
    /// needing it. Neither <c>KillEntity</c> overload has a prefix fallback either
    /// (<c>MethodPointerByPrefix</c> refuses an overloaded name), so on the client there is no
    /// resolver at all. Refusing here is the honest fix: the kill paths that matter — the
    /// <c>StatChangeSystem</c> damage pipeline, <c>KillOrDestroyEntity</c>, the death writer — are
    /// server-authoritative, so a client-side guard would not keep anyone alive even if it applied
    /// cleanly. Same shape as <c>Units</c> / <c>Castles</c> / <c>Chat</c>: a disabled result and
    /// one line, so shared plugin code needs no side check.</para>
    /// </summary>
    public static GuardDecision DecideGuard(bool isServerProcess, Func<bool> ensurePatched)
    {
        if (ensurePatched == null) throw new ArgumentNullException(nameof(ensurePatched));
        if (!isServerProcess) return GuardDecision.RefusedClient;
        return ensurePatched() ? GuardDecision.Allowed : GuardDecision.RefusedNotInstalled;
    }

    /// <summary>
    /// The refusal <c>GodMode.Add</c> logs in a client process (R34 finding 14).
    /// <c>godmode: server-only; guard refused for entity=45:1 — the kill paths it patches (StatChangeUtility.KillEntity, DeathUtilities.Kill) are server-side, and nothing was patched in this process.</c>
    /// </summary>
    public static string FormatClientRefused(ulong key) =>
        "godmode: server-only; guard refused for entity=" + GodModeGuard.FormatKey(key)
        + " — the kill paths it patches (StatChangeUtility.KillEntity, DeathUtilities.Kill) are"
        + " server-side, and nothing was patched in this process.";

    /// <summary>
    /// The refusal <c>GodMode.Add</c> logs when the prefixes are not both live. R32 finding 5:
    /// the old code guarded the entity anyway and answered <c>true</c>, so <c>.nodrain on</c>
    /// replied "on" to an admin who was not protected.
    /// <c>godmode: NOT installed (prefixes live=0 of 2); guard refused for entity=45:1 — it would not be protected.</c>
    /// </summary>
    public static string FormatGuardRefused(ulong key, int live) =>
        "godmode: NOT installed (prefixes live=" + live.ToString(CultureInfo.InvariantCulture)
        + " of " + RequiredPrefixes.ToString(CultureInfo.InvariantCulture)
        + "); guard refused for entity=" + GodModeGuard.FormatKey(key)
        + " — it would not be protected.";

    /// <summary>
    /// The line <c>EnsurePatched</c> writes when the group did not register.
    /// <c>godmode: prefix group not installed (attempt 1 of 3); nothing is patched, the next Add retries.</c>
    /// </summary>
    public static string FormatInstallFailed(int attempt, int maxAttempts) =>
        "godmode: prefix group not installed (attempt "
        + attempt.ToString(CultureInfo.InvariantCulture) + " of "
        + maxAttempts.ToString(CultureInfo.InvariantCulture)
        + "); nothing is patched, "
        + (attempt < maxAttempts
            ? "the next Add retries."
            : "no further attempts this Core generation — god mode stays off until Core is swapped.");
}
