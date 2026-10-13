using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.Loader;
using System.Threading;
using Progenitor.Events;

namespace Progenitor.WorldState;

/// <summary>
/// A world load happened: the game rebuilt every entity in the live <c>World</c> from a save
/// file, so every raw <c>Entity.Index</c> / <c>Version</c> pair recorded before it now names
/// something else (or nothing). Published on <see cref="EventBus"/> once per increment of
/// <see cref="WorldEpoch.Current"/>.
/// </summary>
public sealed class WorldLoaded
{
    /// <summary>The epoch the world is in <i>after</i> this load. Matches <see cref="WorldEpoch.Current"/>.</summary>
    public int Epoch { get; }

    /// <summary>The epoch that just ended. <c>Epoch - 1</c>.</summary>
    public int PreviousEpoch { get; }

    /// <summary>What moved the epoch, for the log line. E.g. <c>LoadPersistenceSystemV2.CopyDataFromPrefabs</c>.</summary>
    public string Source { get; }

    /// <summary>Construct. Core publishes this; tests construct it directly.</summary>
    public WorldLoaded(int epoch, int previousEpoch, string source)
    {
        Epoch = epoch;
        PreviousEpoch = previousEpoch;
        Source = source ?? "";
    }
}

/// <summary>
/// Core-owned counter of world loads (job FEPOCH, R32 findings 1 and 6).
///
/// <para><b>The problem it solves.</b> Two modules built staleness on top of an entity's
/// <c>index:version</c> pair: <c>CastleHandle</c> asks the world whether its recorded heart and
/// territory entities are still live, and <c>GodModeGuard</c> keys the unkillable set on
/// <c>Key(index, version)</c>. Both assume a V Rising save reload leaves a retained pair
/// <i>non-existent</i> — that the load recycles an index and ECS bumps its version. The
/// research's own reload table (internal research notes, castle module)
/// is evidence against that assumption: across a
/// reload the heart moved <c>327390:1 → 325974:1</c>, the owner user <c>326690:1 → 326205:1</c>
/// and the character <c>324767:1 → 327864:1</c> — <b>every one of those six ids is version
/// 1</b>. That is the signature of a load that creates entities fresh from index 0 upward at
/// version 1, not of an <c>EntityManager</c> bumping the version of a recycled index. Under
/// that reading <c>EntityManager.Exists(327390:1)</c> answers <b>true</b> after the reload —
/// index <c>327390</c> has simply been re-issued, at version 1, to whatever the load put there.
/// A liveness check cannot see that, and neither can a version-keyed dictionary.</para>
///
/// <para><b>The fix.</b> A load-completion counter owned by Core. Anything that records a raw
/// entity id also records <see cref="Current"/>; when the counter has moved, the recorded id is
/// treated as dead <i>whatever the world says about it</i>. This is a positive signal about the
/// event itself rather than an inference from entity state, so it is correct under either
/// reading of the version behavior. The per-read liveness check stays as an <i>additional</i>
/// guard: it catches a castle demolished or a character despawned <i>within</i> one epoch,
/// which the counter cannot see.</para>
///
/// <para><b>Deliberately not <c>World.SequenceNumber</c></b> (R31 finding 2): that number is
/// get-only, set once in the <c>World</c> ctor (server metadata: <c>get_SequenceNumber</c>,
/// no setter emitted), and the V Rising load runs
/// as a normal <c>PersistenceGroup</c> tick <i>inside the existing world</i>
/// (<c>LoadPersistenceSystemV2.TriggerNewLoad(World world, …)</c>,
/// <c>DeserializeWorldData(RawPersistentSaveData, out NativeArray&lt;int&gt;
/// existingVersionsByEntityIndex, …)</c> — both take the <i>existing</i>
/// world). The sequence number cannot move across the event being detected. See
/// <see cref="WorldLoadHook"/> for the seam that does.</para>
///
/// <para><b>Two kinds of "the world was rebuilt".</b> On the server it is a save reload, and
/// <see cref="WorldLoadHook"/>'s postfix on <c>CopyDataFromPrefabs</c> is the seam. On the client
/// there is no <c>LoadPersistenceSystemV2</c> at all, but the client's <c>World</c> is torn down
/// and rebuilt from scratch on every <em>connect</em> — which has exactly
/// the same consequence for a retained <c>index:version</c> pair, and happens far more often than
/// a server reload (R33 finding 5). Note it is the <em>connect</em>, not the disconnect: job
/// FMAN17 read the client metadata and <c>ProjectM.ClientWorldManager.BeginCreateClientWorlds</c> is
/// the only path to <c>DestroyClientWorlds</c> in a shipping client outside <c>.disconnect</c> and
/// shutdown, and it destroys then creates in one call (FMAN17). <see cref="ClientWorldEpoch"/> is the client half; both call
/// <see cref="Advance"/>, so a consumer never has to know which process it is in.</para>
///
/// <para><b>Core-generation-lifetime, not process-lifetime</b> (R33 finding 13). Everything here
/// is a static in <c>Progenitor.dll</c>, which lives in a collectible ALC and is replaced wholesale
/// on a Core hot-swap: the new generation starts over. Bootstrap's <c>ContentMemory</c> is the only
/// store that crosses a swap, and it carries two fixed row kinds (<c>CloneOf</c> hashes and
/// live-refresh fingerprints) — a durable epoch would need a new row kind there, i.e. a change to
/// the one assembly that cannot be hot-swapped, so it is deliberately not done. Instead the
/// counter is <b>generation-stamped</b>: each generation takes a stamp in <c>[1, 2047]</c> and the
/// epoch is <c>stamp &lt;&lt; 20 | loadsInThisGeneration + 1</c>. Two consequences that matter.
/// <see cref="Loads"/> counts this generation's loads only, and says so. And a stamp taken in an
/// earlier generation — a plugin is invited to keep one next to an entity, and may have persisted
/// it through <c>ModSave</c> — carries the <i>old</i> stamp, so <see cref="IsStale"/> answers true
/// rather than "true with probability 2047/2048". The 20-bit load field caps a single generation at
/// 1,048,575 loads (and the largest possible epoch is exactly <c>int.MaxValue</c>); beyond that the
/// counter would bleed into the next stamp's range, which no real server or client reaches.</para>
///
/// <para><b>The stamp is Bootstrap's generation number, read rather than guessed</b> (job FEPOCH3,
/// R34 finding 17). The first cut drew <c>Guid.NewGuid().GetHashCode() &amp; 2047</c>, which two
/// consecutive generations collide on about 1 time in 2048 — and the colliding case is the common
/// one: a hot swap with no world load on either side leaves both generations at load count 0, so
/// an old stamp compares <i>equal</i> and <see cref="IsStale"/> answers false. R33 finding 13
/// declined to carry a nonce across the swap in <c>ContentMemory</c> because that means changing
/// Bootstrap. Nothing has to be carried: Bootstrap <b>already names the collectible context after
/// the generation</b> — <c>CoreReloadPolicy.AlcName(int)</c> builds
/// <c>progenitor-core:{generation}</c> (<c>CoreReloadPolicy.cs:159</c>, prefix at <c>:19</c>, first
/// load is generation 1 at <c>:157</c>) and <c>CoreLoader</c> constructs the ALC with that name
/// (<c>CoreLoader.cs:382</c>, <c>:412</c>). Core lives in that ALC, so it reads its own generation
/// with one BCL call: <c>AssemblyLoadContext.GetLoadContext(typeof(WorldEpoch).Assembly)?.Name</c>.
/// That is a read-only observation of the running process — no Bootstrap change and no new stored
/// row, which is what R33 finding 13 was actually objecting to. The stamp is
/// <c>((generation - 1) % 2047) + 1</c>, so <b>no two of any 2047 consecutive generations share
/// one</b> and in particular two adjacent generations never do: staleness across a swap is exact,
/// not probabilistic. Generation 2048 reuses generation 1's stamp, and there is no wider field to
/// put it in (the epoch must stay a positive <c>int</c>); a process that has swapped Core 2047
/// times is a development session, not a server.</para>
///
/// <para><b>Fallback when that name is not there.</b> Core loaded outside Bootstrap — the unit
/// tests, and any dev host that <c>Assembly.LoadFrom</c>s it into the default context — has no
/// <c>progenitor-core:N</c> to read. The stamp then falls back to <c>Environment.TickCount</c> at
/// generation start: a monotonic source rather than a GUID hash, but still folded into the same 11
/// bits, so the old caveat applies to that path and is stated honestly — two generations collide
/// when their start times are an exact multiple of 2048 ms apart, i.e. about 1 in 2048 for
/// arbitrary swap timings. <see cref="GenerationIsExact"/> says which path ran and
/// <see cref="DescribeGeneration"/> is the line <see cref="WorldLoadHook"/> logs at install, so an
/// operator can tell. In the shipped configuration — Bootstrap loading Core out of
/// <c>BepInEx/progenitor-core/</c> — it is always the exact path.</para>
///
/// <para>Game-free on purpose: no Unity or ProjectM type appears here, so the whole
/// stamp/compare rule is unit-tested without a V Rising install.
/// <see cref="WorldLoadHook"/> is the game-touching half.</para>
/// </summary>
public static class WorldEpoch
{
    /// <summary>Bits reserved for the load count inside one Core generation.</summary>
    private const int GenerationShift = 20;

    /// <summary>Largest generation stamp that keeps every epoch a positive <c>int</c> (2047).</summary>
    private const int GenerationMax = int.MaxValue >> GenerationShift;

    /// <summary>
    /// What Bootstrap names a Core generation's collectible context:
    /// <c>progenitor-core:{generation}</c> (<c>CoreReloadPolicy.AlcNamePrefix</c>, mirrored here
    /// because Core cannot reference Bootstrap — the same reason <c>ReloadAllowPolicy</c> exists
    /// twice).
    /// </summary>
    internal const string CoreAlcNamePrefix = "progenitor-core:";

    private static bool _generationExact;
    private static int _generationBase = NewGenerationBase();
    private static int _current = _generationBase + 1;
    private static int _loads;

    /// <summary>
    /// The epoch the world is in, for <i>this Core generation</i>. Never 0 and never negative, so
    /// a default-initialized <c>int</c> field cannot read as "the current epoch"; increments once
    /// per completed world load (server) or client world create/end. The value is
    /// generation-stamped — see the class remarks — so it is a token to compare, not a number to
    /// do arithmetic on. Read with <c>Volatile.Read</c>: the increment happens on the game main
    /// thread and readers are everywhere.
    /// </summary>
    public static int Current => Volatile.Read(ref _current);

    /// <summary>
    /// This Core generation's stamp, <c>[1, 2047]</c> in the game and 0 under
    /// <see cref="ResetForTests()"/>. It is what makes a stamp from a previous generation compare
    /// unequal to <see cref="Current"/>. Under Bootstrap it is Bootstrap's own generation number
    /// (folded modulo 2047), so consecutive generations are guaranteed different — see the class
    /// remarks and <see cref="GenerationIsExact"/>.
    /// </summary>
    public static int Generation => Volatile.Read(ref _generationBase) >> GenerationShift;

    /// <summary>
    /// True when <see cref="Generation"/> came from Bootstrap's ALC name
    /// (<c>progenitor-core:{n}</c>), which makes an old stamp unequal to this generation's by
    /// construction. False when Core was loaded outside Bootstrap and the stamp fell back to
    /// <c>Environment.TickCount</c>, where two generations collide with probability about 1/2048.
    /// Always false under <see cref="ResetForTests(int)"/>.
    /// </summary>
    public static bool GenerationIsExact => Volatile.Read(ref _generationExact);

    /// <summary>
    /// <c>world: epoch generation 3 (exact, from ALC progenitor-core:3)</c> or
    /// <c>world: epoch generation 812 (approximate: Core is not in a progenitor-core:N context, so
    /// the generation stamp is a tick-count fallback and two generations collide with probability
    /// about 1/2048)</c> — logged once by <see cref="WorldLoadHook"/> at install.
    /// </summary>
    public static string DescribeGeneration()
    {
        int generation = Generation;
        if (GenerationIsExact)
            return "world: epoch generation " + generation.ToString(CultureInfo.InvariantCulture)
                + " (exact, from ALC " + CoreAlcNamePrefix
                + CoreGenerationOrZero().ToString(CultureInfo.InvariantCulture) + ").";
        return "world: epoch generation " + generation.ToString(CultureInfo.InvariantCulture)
            + " (approximate: Core is not in a " + CoreAlcNamePrefix
            + "N context, so the generation stamp is a tick-count fallback and two generations "
            + "collide with probability about 1/2048).";
    }

    /// <summary>
    /// Bootstrap's generation number for this Core, or 0 when it cannot be read. Never throws:
    /// this runs during static init, before any logger exists.
    /// </summary>
    internal static int CoreGenerationOrZero()
    {
        try
        {
            var alc = AssemblyLoadContext.GetLoadContext(typeof(WorldEpoch).Assembly);
            return TryParseCoreGeneration(alc?.Name, out int generation) ? generation : 0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// <c>progenitor-core:3</c> → 3. Rejects anything else, including
    /// <c>progenitor-core-inspect</c> (<c>CoreLoader.cs:729</c>, the dry-run context, which has no
    /// <c>:</c>), <c>progenitor-reload:*</c> (a plugin ALC) and a generation below 1 — Bootstrap's
    /// first successful load is generation 1 (<c>CoreReloadPolicy.FirstGeneration</c>).
    /// </summary>
    internal static bool TryParseCoreGeneration(string? alcName, out int generation)
    {
        generation = 0;
        if (string.IsNullOrEmpty(alcName)) return false;
        if (!alcName!.StartsWith(CoreAlcNamePrefix, StringComparison.Ordinal)) return false;
        string tail = alcName.Substring(CoreAlcNamePrefix.Length);
        if (!int.TryParse(tail, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed)) return false;
        if (parsed < 1) return false;
        generation = parsed;
        return true;
    }

    /// <summary>
    /// Fold a 1-based generation number into the 11-bit stamp field: <c>[1, 2047]</c>, cycling with
    /// period 2047 so no two of any 2047 consecutive generations — adjacent ones above all — land
    /// on the same stamp.
    /// </summary>
    internal static int StampForGeneration(int generation) =>
        ((generation - 1) % GenerationMax + GenerationMax) % GenerationMax + 1;

    /// <summary>
    /// How many loads <b>this Core generation</b> has completed — not the process: a hot-swap
    /// starts a new generation at 0 (R33 finding 13).
    /// </summary>
    public static int Loads => Volatile.Read(ref _loads);

    /// <summary>
    /// This generation's stamp, shifted into place. Exact under Bootstrap (the ALC name carries the
    /// generation number); a tick-count fold otherwise. Never throws — it runs in the static
    /// constructor, where a throw would take the whole type down.
    /// </summary>
    private static int NewGenerationBase()
    {
        int generation = CoreGenerationOrZero();
        if (generation >= 1)
        {
            _generationExact = true;
            return StampForGeneration(generation) << GenerationShift;
        }

        // Not loaded by Bootstrap (tests, a dev host): a monotonic source, folded into the same 11
        // bits, so the honest claim is "about 1 in 2048", not "by construction".
        int stamp = Environment.TickCount & GenerationMax;
        if (stamp == 0) stamp = 1;
        return stamp << GenerationShift;
    }

    /// <summary>
    /// Raised after <see cref="Current"/> moves and before the <see cref="WorldLoaded"/> event is
    /// published, for Core-internal sweeps that must not depend on subscriber order. The Ai
    /// module's tree repair listens here; the god-mode guard set drops stale entries lazily instead.
    /// </summary>
    public static event Action<int>? Advanced;

    /// <summary>
    /// Record one completed world load. Returns the new epoch. Called from
    /// <see cref="WorldLoadHook"/>'s postfix on the game's load path and from tests; safe to call
    /// from any thread. Logs exactly one line and publishes <see cref="WorldLoaded"/>.
    ///
    /// <para>Never throws: it runs inside a Harmony postfix on a game system, and an exception
    /// there surfaces as a load failure.</para>
    /// </summary>
    public static int Advance(string source)
    {
        int epoch = Interlocked.Increment(ref _current);
        int loads = Interlocked.Increment(ref _loads);
        source = string.IsNullOrEmpty(source) ? "unknown" : source;

        try { Plugin.Logger?.LogInfo(FormatAdvanced(epoch, source, loads)); }
        catch { /* a logger that throws must not fail the load */ }

        try { Advanced?.Invoke(epoch); }
        catch (Exception e)
        {
            try { Plugin.Logger?.LogWarning("world: an epoch listener threw: " + e.Message); } catch { }
        }

        try { EventBus.Publish(new WorldLoaded(epoch, epoch - 1, source)); }
        catch (Exception e)
        {
            try { Plugin.Logger?.LogWarning("world: publishing WorldLoaded threw: " + e.Message); } catch { }
        }

        return epoch;
    }

    /// <summary>
    /// <c>world: load epoch 1 -&gt; 2 (loads=1, source=LoadPersistenceSystemV2.CopyDataFromPrefabs);
    /// entity ids recorded before this are no longer valid.</c> — one line per increment.
    /// </summary>
    public static string FormatAdvanced(int epoch, string source, int loads) =>
        "world: load epoch " + (epoch - 1).ToString(CultureInfo.InvariantCulture)
        + " -> " + epoch.ToString(CultureInfo.InvariantCulture)
        + " (loads=" + loads.ToString(CultureInfo.InvariantCulture)
        + ", source=" + (string.IsNullOrEmpty(source) ? "unknown" : source)
        + "); entity ids recorded before this are no longer valid.";

    /// <summary>
    /// <c>stamped in world load epoch 2, the world is now in epoch 3</c> — the shape both
    /// <c>CastleHandle.DisabledReason</c> and the god-mode drop line use, so an admin reading
    /// either sees both numbers.
    /// </summary>
    public static string FormatStale(int stamped, int current) =>
        "stamped in world load epoch " + stamped.ToString(CultureInfo.InvariantCulture)
        + ", the world is now in epoch " + current.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// True when <paramref name="stamped"/> is not the epoch the world is in now. A stamp taken
    /// in an earlier Core generation carries that generation's stamp and is therefore stale
    /// whatever its load count was — by construction while <see cref="GenerationIsExact"/>
    /// (R33 finding 13, R34 finding 17), with probability about 2047/2048 on the fallback path;
    /// <c>default(int)</c> — a stamp nobody ever took — is stale too.
    /// </summary>
    public static bool IsStale(int stamped) => stamped != Current;

    /// <summary>
    /// Test hook: generation 0 (so epochs read 1, 2, 3 …), zero loads, no listeners. Generation 0
    /// never occurs in the game, so a test stamp can never collide with a real one.
    /// </summary>
    public static void ResetForTests() => ResetForTests(0);

    /// <summary>
    /// Test hook: start over as if this were a fresh Core generation with stamp
    /// <paramref name="generation"/> (masked into <c>[0, 2047]</c>). Calling it twice with
    /// different stamps is exactly what a Core hot-swap does to these statics. Leaves
    /// <see cref="GenerationIsExact"/> false: a test-injected stamp is not one Bootstrap named.
    /// </summary>
    public static void ResetForTests(int generation)
    {
        int @base = (generation & GenerationMax) << GenerationShift;
        Volatile.Write(ref _generationExact, false);
        Volatile.Write(ref _generationBase, @base);
        Volatile.Write(ref _current, @base + 1);
        Volatile.Write(ref _loads, 0);
        Advanced = null;
        ClientWorldEpoch.Forget();
    }
}

/// <summary>
/// The client half of <see cref="WorldEpoch"/> (job FEPOCH2, R33 finding 5): the decision that
/// turns "which client <c>World</c> am I looking at?" into epoch increments.
///
/// <para><b>Why the client needs this at all.</b> The client process never runs
/// <c>LoadPersistenceSystemV2</c>, so <see cref="WorldLoadHook"/>'s server seam can never fire
/// there — but the client's <c>World</c> (<c>Client_0</c>) is destroyed and rebuilt from scratch on
/// every connect: a kick leaves it alive (job FMAN17), and the next connection attempt's
/// <c>BeginCreateClientWorlds</c> is what destroys it. That has the same consequence the epoch exists
/// for: every retained <c>index:version</c> pair now names something else, and a liveness check
/// cannot see it because a fresh world re-issues low indices at version 1. Before this the client
/// sat at epoch 1 forever and a client plugin following the documented rule to the letter got
/// <c>IsStale == false</c> across every reconnect.</para>
///
/// <para><b>The identity is <c>World.SequenceNumber</c>.</b> It is set once in the <c>World</c>
/// constructor (client metadata: <c>get_SequenceNumber</c>, no
/// setter emitted), which is precisely why it is useless for a <i>save reload</i> — that runs
/// inside the existing world (R31 finding 2) — and exactly right here, where the event <i>is</i> a
/// new <c>World</c>. <c>ChatHook</c> already keys its cached <c>EntityQuery</c> on the same number
/// for the same reason: the Il2Cpp list can hand out a new C# wrapper for the same native world, so
/// reference equality would churn.</para>
///
/// <para><b>Both edges move the epoch.</b> A creation (<c>0 → seq</c> or <c>seq → other</c>) and an
/// ending (<c>seq → 0</c>, i.e. the connection dropped and the world was disposed) each call
/// <see cref="WorldEpoch.Advance"/> once, so an id recorded while connected is refused the moment
/// the connection ends rather than at the next join. <see cref="Note"/> is idempotent per sequence
/// number, which is what lets <see cref="WorldLoadHook"/> feed it from two observations — the
/// existing <c>WorldReady</c> publication (a creation, on the frame it happens) and a poll that is
/// per-frame while connected and bounded while not (<see cref="ClientWorldScanPolicy"/>, job
/// FEPOCH5) — without double counting.</para>
///
/// <para>Game-free like <see cref="WorldEpoch"/>: it takes a <c>ulong</c>, not a <c>World</c>.</para>
/// </summary>
public static class ClientWorldEpoch
{
    /// <summary><c>source=</c> for a client world that just came up (first join, rejoin, server change).</summary>
    public const string CreatedSource = "ClientWorld.Created";

    /// <summary>
    /// <c>source=</c> for a client world that went away. Job FMAN17: that is the next
    /// <em>connect</em>, <c>.disconnect</c>, or process shutdown — not the kick and not the walk
    /// back to the menu, both of which leave <c>Client_0</c> alive.
    /// </summary>
    public const string EndedSource = "ClientWorld.Ended";

    private static readonly object Gate = new();
    private static ulong _seq;

    /// <summary>
    /// The frame-cost policy <c>WorldLoadHook.ClientTick</c> asks before it scans (R34 finding 16).
    /// Reset by <see cref="Forget"/> with the rest of the watch state.
    /// </summary>
    internal static readonly ClientWorldScanPolicy ScanPolicy = new();

    /// <summary>
    /// The <c>Name</c>-marshal filter <c>WorldLoadHook</c>'s walk consults before reading a world's
    /// name (job FEPOCH5, R36 finding 4). Reset by <see cref="Forget"/> with the rest of the state.
    /// </summary>
    internal static readonly ClientWorldNameFilter NameFilter = new();

    /// <summary>The client world sequence number last noted; 0 when there is no client world.</summary>
    public static ulong ObservedSequence
    {
        get { lock (Gate) return _seq; }
    }

    /// <summary>
    /// Note the client world's identity — its <c>World.SequenceNumber</c>, or 0 for "there is no
    /// client world right now". Returns true when this moved the epoch. Safe to call every frame
    /// and from more than one observation: the same number twice does nothing.
    /// </summary>
    public static bool Note(ulong sequenceNumber)
    {
        string source;
        lock (Gate)
        {
            if (sequenceNumber == _seq) return false;
            source = sequenceNumber == 0 ? EndedSource : CreatedSource;
            _seq = sequenceNumber;
        }

        WorldEpoch.Advance(source);
        return true;
    }

    /// <summary>
    /// Forget the observed world. Called from <c>WorldLoadHook.Uninstall</c> as well as from
    /// tests: a watcher that stops watching cannot know what happened while it was not looking, so
    /// the next <see cref="Note"/> after a re-install advances — the conservative direction.
    /// </summary>
    public static void Forget()
    {
        lock (Gate) _seq = 0;
        ScanPolicy.Forget();
        NameFilter.Forget();
    }
}

/// <summary>
/// When the client epoch watch may walk the world list, and when it must not (job FEPOCH3, R34
/// finding 16).
///
/// <para><b>What the old watch cost.</b> <c>WorldLoadHook.ClientTick</c> called
/// <c>Worlds.Client</c> — i.e. <c>Worlds.Find("Client_0")</c> — on <b>every</b> frame, for the
/// whole session, whether or not any plugin had ever read the epoch. <c>Find</c> reads the static
/// <c>World.s_AllWorlds</c> (a plain <c>List&lt;World&gt;</c> on the
/// <c>World</c> class) and for each entry touches <c>IsCreated</c>
/// (<c>get_IsCreated</c>) and then <c>Name</c> (<c>get_Name</c>) —
/// and <c>Name</c> is an Il2Cpp string property, so every entry on every frame marshals a fresh
/// managed <c>string</c> just to be compared and dropped. With the handful of worlds a client has,
/// that is a list walk plus one string allocation per world per frame, ~60 times a second, forever.
/// <c>EventHooks.WatchWorlds</c> already pays that twice for its own reasons; the epoch watch was
/// a third copy.</para>
///
/// <para><b>What the new watch costs.</b> The hook caches the <c>World</c> the last scan found.
/// <list type="bullet">
/// <item><description><b>Connected (the overwhelmingly common frame):</b> one field read of the
/// cached wrapper, one <c>IsCreated</c>, one <c>SequenceNumber</c> (<c>get_SequenceNumber</c>)
/// and a <c>ulong</c> compare. No list walk, no <c>Name</c>, no
/// allocation.</description></item>
/// <item><description><b>No cached client world (main menu and loading screen before the first
/// join, and the window inside a reconnect between the old world's destroy and the new one's
/// create):</b> the full <c>Worlds.Find</c> walk, every frame, until a client world is
/// found.</description></item>
/// <item><description><b>Empty world list and nothing cached:</b> one <c>World.s_AllWorlds</c>
/// read and its <c>Count</c>. A list with nothing in it cannot contain <c>Client_0</c>, so the
/// walk is skipped — the only suppression left, and an exact one. It does not apply when a cached
/// world was just lost: that frame has an ending to report and must resolve
/// it.</description></item>
/// </list></para>
///
/// <para><b>Why the uncached frame re-walks</b> (job FEPOCH4, R35 finding 7). The first cut also
/// suppressed the walk while <c>World.s_AllWorlds.Count</c> was unchanged since the last scan,
/// and claimed the separate "the cached world went away" check covered the case a count cannot
/// see — a world destroyed and another created between two frames, leaving the count where it
/// was. It covered that case <i>only when the destroyed world was the cached client world</i>.
/// With nothing cached — every frame before the first join, and the frames inside a reconnect
/// between the destroy and the create — a frame gap in which the client world is created <b>and</b> some other world is
/// destroyed left the count unchanged and the watch blind until the count next moved for an
/// unrelated reason. A count is not an identity, so no bookkeeping on it can decide this; the
/// only sound answer in that state is to look. So: cached and live → no walk (the frame that
/// matters, and the one R34 finding 16 was actually about); cached and destroyed → walk; nothing
/// cached → walk.</para>
///
/// <para><b>Exactly what is and is not covered now.</b> Covered: paired churn with a cached world
/// (the cache reports not-<c>IsCreated</c>, and that frame walks immediately), paired churn with
/// no cached world (the bounded poll below walks it within
/// <see cref="ClientWorldScanPolicy.UncachedScanIntervalFrames"/> frames), and any create the
/// <c>WorldReady</c> publication sees (within one frame). <b>Not</b> covered: a client world
/// created and destroyed <i>entirely between two frames</i> while another is cached and live — no
/// observation available to a per-frame watch can see a world that never existed on a frame
/// boundary.</para>
///
/// <para><b>The uncached walk is bounded, not per frame</b> (job FEPOCH5, R36 finding 4). FEPOCH4
/// closed the paired-churn hole by walking on <i>every</i> uncached frame, and the state it walks
/// in is not a few frames: <c>World.s_AllWorlds</c> is non-empty for the whole main menu and the
/// whole loading screen before the first join. Five minutes on the server browser at
/// 144 fps is ~43,200 frames, and the first cut paid a full <c>Worlds.Find</c> walk on each one —
/// the pre-R34 cost restored, in the one state where nothing can be reading the epoch (there is no
/// client world, so no handle can be stamped against one). Two changes make the state cheap
/// without giving the coverage back:
/// <list type="number">
/// <item><description><b>The poll is bounded.</b> With nothing cached the walk runs on the first
/// such frame and then once every <see cref="ClientWorldScanPolicy.UncachedScanIntervalFrames"/>
/// frames — 1,440 walks per five minutes at 144 fps instead of 43,200, a factor of 30. The
/// interval <i>is</i> the bound on how late a creation the event misses can be seen: at 144 fps,
/// ~0.2 s; at 60 fps, 0.5 s. Leaving the uncached state (a live cached world, or the frame a
/// cached world was lost) resets the interval, so entering it always walks on the first frame
/// rather than waiting out a stale countdown.</description></item>
/// <item><description><b>The event is the fast path.</b> <c>WorldReady</c> — which Core already
/// publishes for every world creation including client rejoins — is the moment a client world
/// appears, and <c>WorldLoadHook.OnWorldReady</c> both notes it and caches the wrapper, so a
/// creation is seen on the frame it happens and the poll never has to be the primary observation
/// of a create. The poll's job is the <i>ending</i> (nothing publishes one) and the create the
/// event could not see.</description></item>
/// </list></para>
///
/// <para><b>And the walk itself no longer marshals a string per world</b>
/// (<see cref="ClientWorldNameFilter"/>). <c>Worlds.Find</c> reads <c>Name</c> for every entry,
/// and <c>Name</c> is an Il2Cpp string property — one managed allocation per world per walk. A
/// <c>World.SequenceNumber</c> is assigned once in the constructor and never reused, so it is a
/// permanent identity for a wrapper: the walk reads the (allocation-free) <c>ulong</c> first and
/// reads <c>Name</c> only for a sequence number it has not already classified. After the first
/// walk of a stable list every later walk marshals <b>zero</b> strings.</para>
///
/// <para><b>What a frame costs now,</b> by state:
/// <list type="bullet">
/// <item><description><b>Connected</b> (nearly every frame of a session): <c>IsCreated</c> +
/// <c>SequenceNumber</c> on the cached wrapper. No list read, no walk, no allocation.</description></item>
/// <item><description><b>Uncached, list non-empty</b> (menu and loading before the first join): 29 frames
/// in 30 read <c>World.s_AllWorlds</c> and its <c>Count</c> and stop; the 30th walks, reading
/// <c>IsCreated</c> + <c>SequenceNumber</c> per world and <c>Name</c> only for a world it has never
/// classified — so zero <c>Name</c> marshals in the steady state.</description></item>
/// <item><description><b>Uncached, list empty:</b> the same two reads, never a walk.</description></item>
/// <item><description><b>Cached world lost</b> (the frame a reconnect destroys the old client
/// world): one walk, immediately — unbounded by the interval, because that frame has an
/// <i>ending</i> to report.</description></item>
/// </list></para>
///
/// <para>Game-free like the rest of this file — it takes two booleans and an <c>int</c>, so the
/// rule is unit-tested with no V Rising install.</para>
/// </summary>
internal sealed class ClientWorldScanPolicy
{
    /// <summary>
    /// Frames between two walks while nothing is cached (job FEPOCH5, R36 finding 4). The first
    /// uncached frame walks; then one frame in this many. It is the bound on how late a client
    /// world the <c>WorldReady</c> event did not deliver can be noticed — ~0.2 s at 144 fps,
    /// 0.5 s at 60 fps — in a state where there is no client world for a handle to be stamped
    /// against.
    /// </summary>
    internal const int UncachedScanIntervalFrames = 30;

    private readonly object _gate = new();
    private long _ticks;
    private long _scans;
    private int _cooldown;

    /// <summary>Frames this policy has been asked about.</summary>
    internal long Ticks { get { lock (_gate) return _ticks; } }

    /// <summary>Frames it allowed a full <c>World.s_AllWorlds</c> walk. Compare with <see cref="Ticks"/>.</summary>
    internal long Scans { get { lock (_gate) return _scans; } }

    /// <summary>
    /// One frame's decision. <paramref name="cachedIsLive"/>: the hook holds a cached client world
    /// and it still reports <c>IsCreated</c> — nothing to look for, and the uncached interval is
    /// reset so the frame this state ends walks at once. <paramref name="cachedLost"/>: it held one
    /// and that world has since been destroyed — an <i>ending</i> to report, so it walks that frame
    /// whatever the count and whatever the interval say. <paramref name="worldCount"/>:
    /// <c>World.s_AllWorlds.Count</c>, read only when the cached world cannot answer; 0 with
    /// nothing cached means the list is empty and therefore holds no client world, and does not
    /// spend interval budget.
    ///
    /// <para>Every other uncached frame is the bounded poll: the first walks, then one frame in
    /// <see cref="UncachedScanIntervalFrames"/>. An unchanged count is still <i>not</i> a reason to
    /// skip — a count is not an identity (R35 finding 7) — but "I looked two frames ago" is, and
    /// the creation this defers is the one <c>WorldReady</c> reports on the frame it happens.</para>
    /// </summary>
    internal bool ShouldScan(bool cachedIsLive, bool cachedLost, int worldCount)
    {
        lock (_gate)
        {
            _ticks++;

            if (cachedIsLive)
            {
                // Connected. The frame this state ends is a state change, so it must not wait.
                _cooldown = 0;
                return false;
            }

            if (cachedLost)
            {
                // An ending to report: never deferred.
                _cooldown = UncachedScanIntervalFrames - 1;
                _scans++;
                return true;
            }

            // An empty list cannot hold Client_0, so there is nothing to look at and nothing to
            // charge against the interval.
            if (worldCount <= 0) return false;

            if (_cooldown > 0)
            {
                _cooldown--;
                return false;
            }

            _cooldown = UncachedScanIntervalFrames - 1;
            _scans++;
            return true;
        }
    }

    /// <summary>
    /// Walk on the next uncached frame regardless of where the interval stands. Called when
    /// something outside the poll says the world list just changed — the <c>WorldReady</c>
    /// observation, and the re-arm after a read of the cached wrapper threw. It does not force a
    /// walk in a state that has nothing to walk for: a live cached world still answers off the
    /// cache, and an empty list is still skipped.
    /// </summary>
    internal void ScanSoon()
    {
        lock (_gate) _cooldown = 0;
    }

    /// <summary>Zero the counters and the interval. The reset seam <c>ClientWorldEpoch.Forget</c> calls.</summary>
    internal void Forget()
    {
        lock (_gate)
        {
            _ticks = 0;
            _scans = 0;
            _cooldown = 0;
        }
    }
}

/// <summary>
/// Which worlds in <c>World.s_AllWorlds</c> the walk still has to read <c>Name</c> for (job
/// FEPOCH5, R36 finding 4).
///
/// <para><b>The cost this removes.</b> <c>Worlds.Find</c> compares <c>world.Name == name</c> for
/// every entry, and <c>Name</c> is an Il2Cpp string property (<c>get_Name</c>):
/// each read marshals a fresh managed <c>string</c> that is compared once
/// and dropped. Every walk therefore allocated one string per world, and R36 finding 4 is about a
/// state that walked at frame rate.</para>
///
/// <para><b>Why a sequence number can stand in for the name.</b> <c>World.SequenceNumber</c>
/// (<c>get_SequenceNumber</c>, <c>:1336829</c>) is assigned once in the <c>World</c> constructor
/// (<c>:1251614</c>, no setter emitted) from a monotonically increasing counter — the same property
/// <see cref="ClientWorldEpoch"/> uses as a world's identity and <c>ChatHook</c> keys its cached
/// query on. It is never reused, so "sequence number 7 is not the client world" stays true for as
/// long as the process runs: a later world with a different name cannot be sequence number 7.
/// Reading it is a <c>ulong</c> getter with no marshaling. So the walk reads the number first and
/// pays for <c>Name</c> only on a number it has never classified — once per world, ever, instead of
/// once per world per walk.</para>
///
/// <para><b>Bounded.</b> The set is capped at <see cref="Capacity"/>; going over clears it, which
/// costs one more walk's worth of <c>Name</c> reads and can never leak. A client holds a handful of
/// worlds, so the cap is headroom, not a policy.</para>
///
/// <para>Game-free like the rest of this file: it takes <c>ulong</c>s, so the rule and the
/// allocation count are unit-tested with no V Rising install.</para>
/// </summary>
internal sealed class ClientWorldNameFilter
{
    /// <summary>Most sequence numbers remembered as not-the-client-world before the set is cleared.</summary>
    internal const int Capacity = 64;

    private readonly object _gate = new();
    private readonly HashSet<ulong> _notClient = new();
    private long _nameReads;

    /// <summary>How many times a caller was told to read <c>Name</c> — i.e. the Il2Cpp string marshals.</summary>
    internal long NameReads { get { lock (_gate) return _nameReads; } }

    /// <summary>Sequence numbers currently remembered as not the client world.</summary>
    internal int Remembered { get { lock (_gate) return _notClient.Count; } }

    /// <summary>
    /// True when the caller must read this world's <c>Name</c> to classify it — i.e. this sequence
    /// number has not already been recorded as not-the-client-world. Counts the read.
    /// </summary>
    internal bool NeedsNameRead(ulong sequenceNumber)
    {
        lock (_gate)
        {
            if (_notClient.Contains(sequenceNumber)) return false;
            _nameReads++;
            return true;
        }
    }

    /// <summary>Record that the name read said this world is not <c>Client_0</c>.</summary>
    internal void RecordNotClient(ulong sequenceNumber)
    {
        lock (_gate)
        {
            if (_notClient.Count >= Capacity) _notClient.Clear();
            _notClient.Add(sequenceNumber);
        }
    }

    /// <summary>
    /// Record that the name read said this world <i>is</i> <c>Client_0</c>. Kept separate from
    /// <see cref="RecordNotClient"/> so a client world can never be filtered out of a later walk.
    /// </summary>
    internal void RecordClient(ulong sequenceNumber)
    {
        lock (_gate) _notClient.Remove(sequenceNumber);
    }

    /// <summary>Drop everything remembered and zero the counter. Part of <c>ClientWorldEpoch.Forget</c>.</summary>
    internal void Forget()
    {
        lock (_gate)
        {
            _notClient.Clear();
            _nameReads = 0;
        }
    }
}
