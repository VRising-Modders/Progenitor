using System;
using System.Collections.Generic;
using System.Globalization;

namespace Progenitor.GodMode;

/// <summary>
/// The guarded-entity set behind <c>ctx.GodMode.Add/Remove</c> and the kill-path prefixes
/// (job FHARM, consuming the internal research notes, FDR §4 and FBUFF5 §4).
///
/// <para><b>What this covers that a component cannot.</b> <c>ProjectM.Immortal</c>
/// is read exactly once on the whole kill surface — in
/// <c>StatChangeUtility.KillEntity(EntityManager, Entity, Entity killer, ref Health, in
/// HealthConstants, double, StatChangeReason, Entity spellSource, RagdollSourceSetting, float,
/// bool killImmortals)</c>, and only while
/// <c>killImmortals == false</c>. Three paths never consult it at all (FDR §4 rows 4, 7, 8):
/// the 2-arg <c>StatChangeUtility.KillEntity(em, entity, currentTime, reason)</c>
/// which hard-codes <c>killImmortals: true</c>;
/// <c>StatChangeUtility.KillOrDestroyEntity</c> on an entity with no
/// <c>Health</c>, which jumps straight past the check; and <c>DeathUtilities.Kill</c>
/// the raw writer. All three funnel through
/// <c>DeathUtilities.Kill</c>, so that is the choke point — plus the 11-arg
/// <c>KillEntity</c>, which sets <c>health.IsDead = true</c> before calling it.</para>
///
/// <para><b>Why the key alone is not enough (R32 finding 6, job FEPOCH).</b> <c>index:version</c>
/// was treated as a reload-proof identity — <c>harmony-patches.md</c> §4 states the intent as "so
/// a recycled entity index is not guarded". It is not reload-proof. A V Rising save reload
/// rebuilds every entity in the live world from index 0 upward at version 1
/// (<see cref="Progenitor.WorldState.WorldEpoch"/> has the reload table: every id in it is
/// version 1 on <i>both</i> sides), so index 45 is re-issued at version 1 to whatever the load
/// creates first — plausibly another player's character — and a <c>45:1</c> guard added before
/// the reload silently makes that entity unkillable forever, with no buff and no owner able to
/// clear it. Every entry therefore carries the <see cref="Progenitor.WorldState.WorldEpoch"/> it
/// was added in, and an entry from an older epoch is <b>dropped on the next check</b> (with one
/// log line) rather than answered. <see cref="SweepStale"/> does the whole set at once.</para>
///
/// <para><b>Owners are reference counts, not a single field (R40 finding 5).</b> One entity is
/// routinely guarded for two unrelated reasons at once — the Sample's <c>.nodrain</c> buff and
/// its <c>.godmode</c> debug command are the reference case — and the set used to keep one owner
/// per entity with an unconditional <c>Remove</c>, so whichever command was turned off second
/// silently unguarded a character the other one still believed was protected, both replying
/// success. Every entry now holds the list of owners that asked for it: the entity is guarded
/// while any owner holds it, <see cref="Remove(ulong, object?)"/> drops one owner's hold only,
/// and <see cref="DescribeOwners"/> says who is left. A <see cref="GodModeOwner"/> token
/// (plugin + label) lets one plugin be several owners while
/// <see cref="RemoveAll"/> — the unload sweep, which knows only the plugin — still takes all of
/// its holds and no one else's.</para>
///
/// <para><b>Every owner is sweepable by exactly one unload path (R41 finding 7).</b> The sweep
/// reaches a <see cref="GodModeOwner"/> only through its <see cref="GodModeOwner.Plugin"/>
/// reference, so a token without one — the old <c>new GodModeOwner(null, "mine")</c> — was a
/// guard nothing could ever drop, which is the R27 finding 2 failure mode again. The constructor
/// now refuses a null plugin, and a null <c>owner</c> argument to <see cref="Add"/> /
/// <see cref="Remove(ulong, object?)"/> / <see cref="IsGuardedBy"/> is recorded as
/// <see cref="CoreOwner"/>, a Core-owned sentinel that <c>RemoveAll(CorePlugin)</c> and Core's
/// own <c>Uninstall</c> (<see cref="Clear"/>) both take. So for every entry in the set there is a
/// plugin identity, and for every identity a sweep.</para>
///
/// <para>Game-free on purpose: an entity is reduced to an <c>index:version</c> key so the set,
/// the once-per-entity log throttle, the epoch drop and the owner sweep are all unit-testable
/// without a V Rising install. <see cref="GodMode"/> is the Entity-typed face of this.</para>
/// </summary>
public static class GodModeGuard
{
    /// <summary>
    /// One guarded entity: <b>every</b> owner that asked for it, and the world-load epoch they
    /// asked in. The list is the reference count (R40 finding 5): the entity is guarded while it
    /// is non-empty, so one owner's <c>Remove</c> cannot drop another owner's guard.
    /// </summary>
    private sealed class Entry
    {
        internal readonly List<object?> Owners = new();
        internal int Epoch;

        internal Entry(int epoch) => Epoch = epoch;

        /// <summary>True when <paramref name="owner"/> already holds this entity.</summary>
        internal bool Holds(object? owner)
        {
            for (int i = 0; i < Owners.Count; i++)
                if (SameOwner(Owners[i], owner)) return true;
            return false;
        }

        /// <summary>Take this owner's hold. True when it had one.</summary>
        internal bool Drop(object? owner)
        {
            for (int i = 0; i < Owners.Count; i++)
            {
                if (!SameOwner(Owners[i], owner)) continue;
                Owners.RemoveAt(i);
                return true;
            }
            return false;
        }

        /// <summary>Restamp for a new epoch: the old owners named a different entity.</summary>
        internal void Restamp(int epoch)
        {
            Owners.Clear();
            Epoch = epoch;
        }
    }

    /// <summary>
    /// Two owners cannot be told apart by a plugin reference alone: one plugin holds a guard for
    /// several unrelated reasons (the Sample's <c>.nodrain</c> buff and its <c>.godmode</c> debug
    /// command are the reference case), and before R40 finding 5 either command's removal dropped
    /// the other's guard. A token is a plugin <b>plus</b> a label, so the two are distinct owners
    /// for <see cref="Add"/> / <see cref="Remove(ulong, object?)"/> while
    /// <see cref="RemoveAll"/> — the unload sweep, which knows only the plugin — still matches
    /// every token that plugin made.
    /// </summary>
    public sealed class GodModeOwner
    {
        /// <summary>
        /// The plugin identity this token belongs to — never null (R41 finding 7). The unload
        /// sweep reaches a token only through this reference, so a token without one would be a
        /// guard no <see cref="RemoveAll"/> can ever drop. Core's own guards carry
        /// <see cref="CorePlugin"/>.
        /// </summary>
        public object Plugin { get; }

        /// <summary>What this token guards for, e.g. <c>sample.nodrain</c>. Shown by <see cref="DescribeOwners"/>.</summary>
        public string Label { get; }

        /// <summary>
        /// Make a token. Tokens compare by (plugin, label), so callers may rebuild one.
        /// <paramref name="plugin"/> is required: an owner that carries no plugin identity is an
        /// owner the unload sweep cannot match, and the guard it holds would outlive every path
        /// that could remove it — the R27 finding 2 failure mode this class exists to prevent
        /// (R41 finding 7). Core's own guards use <see cref="CoreOwner"/>.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="plugin"/> is null.</exception>
        public GodModeOwner(object plugin, string label)
        {
            Plugin = plugin ?? throw new ArgumentNullException(
                nameof(plugin),
                "a god mode guard owner must carry a plugin identity so unload can sweep it; "
                + "use GodModeGuard.CoreOwner for Core's own guards");
            Label = string.IsNullOrWhiteSpace(label) ? "?" : label;
        }

        /// <summary>Same plugin and same label.</summary>
        public bool Matches(GodModeOwner other) =>
            other != null
            && ReferenceEquals(Plugin, other.Plugin)
            && string.Equals(Label, other.Label, StringComparison.Ordinal);

        /// <inheritdoc/>
        public override string ToString() => Label;
    }

    /// <summary>
    /// The plugin identity Core's own guards carry. Not a plugin, but it is a stable reference
    /// that <see cref="OwnedBy"/> matches, so a guard nobody else claims is still swept by
    /// exactly one unload path — <c>RemoveAll(CorePlugin)</c> — as well as by <see cref="Clear"/>,
    /// which Core's <c>Uninstall</c> calls. Before R41 finding 7 a null owner was recorded as
    /// null and no sweep of any kind could name it.
    /// </summary>
    public static readonly object CorePlugin = new();

    /// <summary>
    /// The owner a null <paramref name="owner"/> argument becomes: Core's own hold. Declared
    /// after <see cref="CorePlugin"/> because static initializers run in declaration order.
    /// </summary>
    public static readonly GodModeOwner CoreOwner = new(CorePlugin, "core");

    /// <summary>
    /// Every public entry point's owner argument passes through here, so no entry is ever
    /// recorded under an owner with no identity (R41 finding 7): null means Core, and Core is
    /// <see cref="CoreOwner"/>.
    /// </summary>
    private static object Normalize(object? owner) => owner ?? CoreOwner;

    /// <summary>
    /// Owner identity. Reference equality for a plain plugin instance — two live plugin objects
    /// are never the same owner — and (plugin, label) for a <see cref="GodModeOwner"/>, so a
    /// caller may rebuild its token rather than cache it.
    /// </summary>
    private static bool SameOwner(object? a, object? b) =>
        ReferenceEquals(a, b)
        || (a is GodModeOwner ga && b is GodModeOwner gb && ga.Matches(gb));

    /// <summary>
    /// Does <paramref name="entryOwner"/> belong to <paramref name="sweepOwner"/>? The unload
    /// sweep is handed the plugin instance and must reach every token that plugin made.
    /// </summary>
    private static bool OwnedBy(object? entryOwner, object sweepOwner) =>
        SameOwner(entryOwner, sweepOwner)
        || (entryOwner is GodModeOwner g && ReferenceEquals(g.Plugin, sweepOwner));

    /// <summary>
    /// <c>core</c>, a token's label, or the owner type's name. A null owner formats as
    /// <c>core</c> because that is what <see cref="Normalize"/> turns it into — and
    /// <see cref="CoreOwner"/>'s label is <c>core</c>, so both spellings print the same.
    /// </summary>
    public static string FormatOwner(object? owner) =>
        owner == null ? "core"
        : owner is GodModeOwner token ? token.Label
        : owner.GetType().Name;

    private static readonly object Gate = new();
    private static readonly Dictionary<ulong, Entry> Guarded = new();
    private static readonly HashSet<ulong> Logged = new();

    /// <summary>Log site token for the <c>DeathUtilities.Kill</c> prefix.</summary>
    public const string SiteKill = "DeathUtilities.Kill";

    /// <summary>
    /// Log site token the 11-arg <c>StatChangeUtility.KillEntity</c> prefix used. That overload
    /// is no longer patched (it takes <c>ref Health</c>; see
    /// <c>PatchSitePolicy.FindByRefValueTypeParameter</c>); the token stays so old log greps and
    /// callers still compile.
    /// </summary>
    public const string SiteKillEntity = "StatChangeUtility.KillEntity";

    /// <summary>Log site token for the <c>StatChangeUtility.KillOrDestroyEntity</c> prefix.</summary>
    public const string SiteKillOrDestroy = "StatChangeUtility.KillOrDestroyEntity";

    /// <summary>
    /// Log site token for the 6-arg <c>StatChangeUtility.KillEntity</c> prefix — the forwarder
    /// with 12 callers that reads <c>Health</c> into a stack local and writes it back over the
    /// 11-arg prefix's restore (R33 finding 3). Named apart from
    /// <see cref="SiteKillEntity"/> so a blocked-kill line says which overload was entered.
    /// </summary>
    public const string SiteKillEntitySix = "StatChangeUtility.KillEntity(6)";

    /// <summary>
    /// Guarded entity count. Sweeps entries from an older world-load epoch first, so the number
    /// an admin sees is the number of entities that are actually still guarded.
    /// </summary>
    public static int Count
    {
        get
        {
            SweepStale();
            lock (Gate) return Guarded.Count;
        }
    }

    /// <summary>Pack an ECS entity's index and version into the set key.</summary>
    public static ulong Key(int index, int version) =>
        ((ulong)(uint)index << 32) | (uint)version;

    /// <summary><c>45:1</c> — the same shape <c>SampleBuffs.FormatEntity</c> logs.</summary>
    public static string FormatKey(ulong key) =>
        ((uint)(key >> 32)).ToString(CultureInfo.InvariantCulture)
        + ":" + ((uint)key).ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Guard <paramref name="key"/> on behalf of <paramref name="owner"/> — a plugin instance, a
    /// <see cref="GodModeOwner"/> token, or null, which is recorded as <see cref="CoreOwner"/>
    /// rather than as nothing (R41 finding 7), so that every entry in the set carries an identity
    /// some sweep can name: a plugin instance is swept by <c>RemoveAll(instance)</c>, a token by
    /// <c>RemoveAll(token.Plugin)</c>, and Core's own hold by <c>RemoveAll(CorePlugin)</c> or by
    /// <see cref="Clear"/>, which Core's <c>Uninstall</c> calls. The owner is recorded so unload can
    /// sweep it, and so that <see cref="Remove(ulong, object?)"/> is owner-scoped. Returns true
    /// when <b>this owner</b> did not already hold the entity; an entity another owner already
    /// guards is still a true (the hold is new), because the caller now has one and the guard
    /// will outlive the other owner's removal. Re-adding resets the once-per-entity log throttle
    /// so a re-applied buff logs its first blocked kill again.
    /// </summary>
    public static bool Add(ulong key, object? owner)
    {
        owner = Normalize(owner);
        int epoch = Progenitor.WorldState.WorldEpoch.Current;
        lock (Gate)
        {
            Logged.Remove(key);
            if (!Guarded.TryGetValue(key, out var entry))
            {
                entry = new Entry(epoch);
                Guarded[key] = entry;
            }
            // An entry from an older epoch is not "already guarded": it named a different
            // entity, and its owners are owners of that entity, not this one. Re-stamping is
            // the same as adding it fresh, which is what the caller means — it is holding a
            // live Entity right now.
            else if (entry.Epoch != epoch)
            {
                entry.Restamp(epoch);
            }
            else if (entry.Holds(owner))
            {
                return false;
            }

            entry.Owners.Add(owner);
            return true;
        }
    }

    /// <summary>
    /// Drop <paramref name="owner"/>'s hold on <paramref name="key"/>. True when that owner held
    /// it. The entity stays guarded while any other owner still holds it (R40 finding 5): the
    /// Sample's <c>.godmode off</c> must not unguard a character <c>.nodrain</c> owns, and
    /// <c>.nodrain</c>'s clear must not unguard one <c>.godmode</c> owns.
    /// </summary>
    public static bool Remove(ulong key, object? owner)
    {
        owner = Normalize(owner);
        lock (Gate)
        {
            if (!Guarded.TryGetValue(key, out var entry)) return false;
            if (entry.Epoch != Progenitor.WorldState.WorldEpoch.Current)
            {
                // Stale: its owners named a different entity. Drop it whole rather than answer.
                Guarded.Remove(key);
                Logged.Remove(key);
                return false;
            }
            if (!entry.Drop(owner)) return false;
            if (entry.Owners.Count == 0)
            {
                Guarded.Remove(key);
                Logged.Remove(key);
            }
            return true;
        }
    }

    /// <summary>
    /// Stop guarding <paramref name="key"/> <b>whoever</b> holds it. True when it was guarded.
    /// This is Core's own unconditional drop; a plugin clearing a guard it took should call
    /// <see cref="Remove(ulong, object?)"/> with its own owner token instead.
    /// </summary>
    public static bool Remove(ulong key)
    {
        lock (Gate)
        {
            Logged.Remove(key);
            return Guarded.Remove(key);
        }
    }

    /// <summary>True when <paramref name="owner"/> holds a live guard on <paramref name="key"/>.</summary>
    public static bool IsGuardedBy(ulong key, object? owner)
    {
        object normalized = Normalize(owner);
        lock (Gate)
        {
            return Guarded.TryGetValue(key, out var entry)
                && entry.Epoch == Progenitor.WorldState.WorldEpoch.Current
                && entry.Holds(normalized);
        }
    }

    /// <summary>
    /// <c>owners: 45:1=[sample.nodrain,sample.godmode] 46:1=[core]</c>, or <c>owners: none</c>.
    /// Stale entries are swept first, so what it lists is what is actually guarded. This is the
    /// line that makes a shared guard visible to an admin: "Already guarded" says nothing about
    /// who would still be holding it after their own <c>off</c>.
    /// </summary>
    public static string DescribeOwners()
    {
        SweepStale();
        var text = new System.Text.StringBuilder("owners:");
        lock (Gate)
        {
            if (Guarded.Count == 0) return "owners: none";
            foreach (var pair in Guarded)
            {
                text.Append(' ').Append(FormatKey(pair.Key)).Append("=[");
                for (int i = 0; i < pair.Value.Owners.Count; i++)
                {
                    if (i > 0) text.Append(',');
                    text.Append(FormatOwner(pair.Value.Owners[i]));
                }
                text.Append(']');
            }
        }
        return text.ToString();
    }

    /// <summary>
    /// True when the entity must not die. Called from a Harmony prefix on the game's kill paths,
    /// so the hot path is one dictionary probe and an int compare under the lock — no logging, no
    /// allocation. The one exception is a guard from an older world-load epoch: that entry is
    /// removed and one line is written (outside the lock), which happens at most once per stale
    /// key because the entry is gone afterwards.
    /// </summary>
    public static bool IsGuarded(ulong key)
    {
        bool guarded;
        string? dropped;
        lock (Gate) guarded = LookupNoLock(key, out dropped);
        if (dropped != null) LogDrop(dropped);
        return guarded;
    }

    /// <summary>
    /// The shared probe. Caller holds <see cref="Gate"/>. An entry stamped in an older
    /// <see cref="Progenitor.WorldState.WorldEpoch"/> is dropped and reported rather than
    /// answered: after a save reload its <c>index:version</c> names a different entity
    /// (R32 finding 6).
    /// </summary>
    private static bool LookupNoLock(ulong key, out string? dropped)
    {
        dropped = null;
        if (!Guarded.TryGetValue(key, out var entry)) return false;

        int current = Progenitor.WorldState.WorldEpoch.Current;
        if (entry.Epoch == current) return true;

        Guarded.Remove(key);
        Logged.Remove(key);
        dropped = FormatDroppedStale(key, entry.Epoch, current, Guarded.Count);
        return false;
    }

    private static void LogDrop(string line)
    {
        try { Plugin.Logger?.LogInfo(line); } catch { /* never break a kill over a log line */ }
    }

    /// <summary>
    /// Drop every guard stamped in an older world-load epoch, in one pass, and log one line each.
    /// <see cref="Count"/> calls this; so may a caller that wants the set tidy without waiting for
    /// each entity's next kill. Returns how many were dropped.
    /// </summary>
    public static int SweepStale()
    {
        int current = Progenitor.WorldState.WorldEpoch.Current;
        List<string>? lines = null;
        lock (Gate)
        {
            List<ulong>? doomed = null;
            foreach (var pair in Guarded)
            {
                if (pair.Value.Epoch == current) continue;
                (doomed ??= new List<ulong>()).Add(pair.Key);
            }
            if (doomed == null) return 0;
            for (int i = 0; i < doomed.Count; i++)
            {
                int epoch = Guarded[doomed[i]].Epoch;
                Guarded.Remove(doomed[i]);
                Logged.Remove(doomed[i]);
                (lines ??= new List<string>()).Add(
                    FormatDroppedStale(doomed[i], epoch, current, Guarded.Count));
            }
        }

        if (lines == null) return 0;
        for (int i = 0; i < lines.Count; i++) LogDrop(lines[i]);
        return lines.Count;
    }

    /// <summary>
    /// True the first time a given entity's kill is blocked. A guarded character standing in
    /// the sun would otherwise write a line per tick, per path.
    /// </summary>
    public static bool ShouldLogBlock(ulong key)
    {
        lock (Gate) return Logged.Add(key);
    }

    /// <summary>
    /// The whole body of both kill-path prefixes, in one game-free place so the "guarded
    /// entity blocks the kill, unguarded entity passes through" rule is pinned by the tests
    /// rather than only by a live server. Returns true when the prefix must return
    /// <c>false</c> (skip the original). <paramref name="logLine"/> is non-null exactly once
    /// per entity — see <see cref="ShouldLogBlock"/>.
    /// </summary>
    public static bool ShouldBlock(ulong key, string site, out string? logLine)
    {
        logLine = null;
        if (!IsGuarded(key)) return false;
        if (ShouldLogBlock(key)) logLine = FormatBlocked(site, key);
        return true;
    }

    /// <summary>
    /// Drop <paramref name="owner"/>'s holds — the unload sweep. <paramref name="owner"/> is the
    /// plugin instance, so <b>every</b> <see cref="GodModeOwner"/> token that plugin made goes
    /// with it, and nothing else does: an entity another plugin (or Core) also holds stays
    /// guarded. Returns how many entities this owner was dropped from.
    /// </summary>
    public static int RemoveAll(object owner)
    {
        if (owner == null) throw new ArgumentNullException(nameof(owner));
        lock (Gate)
        {
            List<ulong>? doomed = null;
            int dropped = 0;
            foreach (var pair in Guarded)
            {
                var owners = pair.Value.Owners;
                bool hit = false;
                for (int i = owners.Count - 1; i >= 0; i--)
                {
                    if (!OwnedBy(owners[i], owner)) continue;
                    owners.RemoveAt(i);
                    hit = true;
                }
                if (!hit) continue;
                dropped++;
                if (owners.Count == 0) (doomed ??= new List<ulong>()).Add(pair.Key);
            }
            if (doomed != null)
            {
                for (int i = 0; i < doomed.Count; i++)
                {
                    Guarded.Remove(doomed[i]);
                    Logged.Remove(doomed[i]);
                }
            }
            return dropped;
        }
    }

    /// <summary>
    /// Drop every guarded entity. Core <c>Uninstall</c> calls this: the prefixes are about to be
    /// unpatched, so a leftover entry would be a set nothing reads and an owner reference that
    /// pins the plugin ALC.
    /// </summary>
    public static int Clear()
    {
        lock (Gate)
        {
            int n = Guarded.Count;
            Guarded.Clear();
            Logged.Clear();
            return n;
        }
    }

    /// <summary>
    /// <c>godmode: blocked kill site=DeathUtilities.Kill entity=45:1</c>. Written once per
    /// entity (see <see cref="ShouldLogBlock"/>).
    /// </summary>
    public static string FormatBlocked(string site, ulong key) =>
        "godmode: blocked kill site=" + (string.IsNullOrEmpty(site) ? "?" : site)
        + " entity=" + FormatKey(key);

    /// <summary>
    /// <c>godmode: dropped guard on entity=45:1 — stamped in world load epoch 1, the world is now
    /// in epoch 2; the save was reloaded and that id names a different entity now. guarded=0</c>
    /// — written once per stale entry, when it is dropped (R32 finding 6).
    /// </summary>
    public static string FormatDroppedStale(ulong key, int stampedEpoch, int currentEpoch, int count) =>
        "godmode: dropped guard on entity=" + FormatKey(key) + " - "
        + Progenitor.WorldState.WorldEpoch.FormatStale(stampedEpoch, currentEpoch)
        + "; the save was reloaded and that id names a different entity now. guarded="
        + count.ToString(CultureInfo.InvariantCulture);

    /// <summary><c>godmode: guard added entity=45:1 guarded=1</c></summary>
    public static string FormatAdded(ulong key, int count) =>
        "godmode: guard added entity=" + FormatKey(key)
        + " guarded=" + count.ToString(CultureInfo.InvariantCulture);

    /// <summary><c>godmode: guard removed entity=45:1 guarded=0</c></summary>
    public static string FormatRemoved(ulong key, int count) =>
        "godmode: guard removed entity=" + FormatKey(key)
        + " guarded=" + count.ToString(CultureInfo.InvariantCulture);

    /// <summary><c>godmode: guarded=1 patched=2</c> for the pin dump.</summary>
    public static string FormatPins(int guarded, int patched) =>
        "godmode: guarded=" + guarded.ToString(CultureInfo.InvariantCulture)
        + " patched=" + patched.ToString(CultureInfo.InvariantCulture);
}
