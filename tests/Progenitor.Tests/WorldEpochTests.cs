using System;
using System.Collections.Generic;
using Progenitor.Events;
using Progenitor.WorldState;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Job FEPOCH (R32 findings 1 and 6): the Core-owned world-load counter. The game half — the
/// Harmony postfix on <c>LoadPersistenceSystemV2.CopyDataFromPrefabs</c>
/// — is not compiled here; what is pinned is the counter, the event and
/// the two log strings every consumer formats its reason from.
/// </summary>
[Collection("PluginLogger")]
public class WorldEpochTests : IDisposable
{
    private readonly TestLogSource _log = new();
    private readonly object _owner = new();

    public WorldEpochTests()
    {
        WorldEpoch.ResetForTests();
        Plugin.Logger = _log;
    }

    public void Dispose()
    {
        EventBus.RemoveAll(_owner);
        WorldEpoch.ResetForTests();
        Plugin.Logger = null;
    }

    [Fact]
    public void Starts_at_one_so_a_default_int_field_never_reads_as_the_current_epoch()
    {
        Assert.Equal(1, WorldEpoch.Current);
        Assert.Equal(0, WorldEpoch.Loads);
        Assert.NotEqual(default(int), WorldEpoch.Current);
        Assert.True(WorldEpoch.IsStale(default));
    }

    [Fact]
    public void Advance_moves_the_epoch_counts_the_load_and_logs_one_line_naming_the_epoch()
    {
        int epoch = WorldEpoch.Advance("LoadPersistenceSystemV2.CopyDataFromPrefabs");

        Assert.Equal(2, epoch);
        Assert.Equal(2, WorldEpoch.Current);
        Assert.Equal(1, WorldEpoch.Loads);

        string line = Assert.Single(_log.Infos);
        Assert.Equal(
            "world: load epoch 1 -> 2 (loads=1, source=LoadPersistenceSystemV2.CopyDataFromPrefabs); "
            + "entity ids recorded before this are no longer valid.",
            line);
    }

    [Fact]
    public void Every_load_increments_and_logs_again()
    {
        WorldEpoch.Advance("a");
        WorldEpoch.Advance("b");
        WorldEpoch.Advance("c");

        Assert.Equal(4, WorldEpoch.Current);
        Assert.Equal(3, WorldEpoch.Loads);
        Assert.Equal(3, _log.Infos.Count);
    }

    [Fact]
    public void A_stamped_epoch_is_stale_only_after_a_load()
    {
        int stamped = WorldEpoch.Current;
        Assert.False(WorldEpoch.IsStale(stamped));

        WorldEpoch.Advance("reload");

        Assert.True(WorldEpoch.IsStale(stamped));
        Assert.False(WorldEpoch.IsStale(WorldEpoch.Current));
    }

    [Fact]
    public void WorldLoaded_is_published_on_the_bus_with_both_epochs()
    {
        var seen = new List<WorldLoaded>();
        EventBus.On<WorldLoaded>(seen.Add, owner: _owner);

        WorldEpoch.Advance("LoadPersistenceSystemV2.CopyDataFromPrefabs");

        var ev = Assert.Single(seen);
        Assert.Equal(2, ev.Epoch);
        Assert.Equal(1, ev.PreviousEpoch);
        Assert.Equal("LoadPersistenceSystemV2.CopyDataFromPrefabs", ev.Source);
    }

    [Fact]
    public void A_throwing_subscriber_does_not_break_the_load()
    {
        EventBus.On<WorldLoaded>(_ => throw new InvalidOperationException("boom"), owner: _owner);

        int epoch = WorldEpoch.Advance("reload");

        Assert.Equal(2, epoch);
        Assert.Equal(2, WorldEpoch.Current);
    }

    [Fact]
    public void The_stale_reason_string_names_both_epochs()
    {
        string reason = WorldEpoch.FormatStale(2, 3);

        Assert.Equal("stamped in world load epoch 2, the world is now in epoch 3", reason);
        Assert.Contains("2", reason);
        Assert.Contains("3", reason);
    }

    [Fact]
    public void An_empty_source_still_produces_a_readable_line()
    {
        WorldEpoch.Advance("");

        Assert.Contains("source=unknown", Assert.Single(_log.Infos));
    }

    /// <summary>
    /// R33 finding 5. The client never runs <c>LoadPersistenceSystemV2</c>, but its world is
    /// destroyed and rebuilt on every reconnect, which invalidates retained ids the same way. The
    /// game half reads <c>World.SequenceNumber</c>; this drives the decision on the number alone.
    /// </summary>
    [Fact]
    public void A_client_world_appearing_and_ending_each_move_the_epoch_once()
    {
        // Joining a server: the client world is created.
        Assert.True(ClientWorldEpoch.Note(4001));
        Assert.Equal(2, WorldEpoch.Current);

        // Same world, seen again on the next frame and again from WorldReady: nothing happens.
        Assert.False(ClientWorldEpoch.Note(4001));
        Assert.False(ClientWorldEpoch.Note(4001));
        Assert.Equal(2, WorldEpoch.Current);

        // The connection drops and the world is disposed: ids recorded while connected are
        // refused now, not at the next join.
        Assert.True(ClientWorldEpoch.Note(0));
        Assert.Equal(3, WorldEpoch.Current);

        // Rejoining builds a different world.
        Assert.True(ClientWorldEpoch.Note(4002));
        Assert.Equal(4, WorldEpoch.Current);
        Assert.Equal(3, WorldEpoch.Loads);
        Assert.Equal(4002UL, ClientWorldEpoch.ObservedSequence);

        var sources = new List<string>();
        foreach (string line in _log.Infos)
            sources.Add(line.Substring(line.IndexOf("source=", StringComparison.Ordinal)).Split(')')[0]);
        Assert.Equal(
            new[]
            {
                "source=" + ClientWorldEpoch.CreatedSource,
                "source=" + ClientWorldEpoch.EndedSource,
                "source=" + ClientWorldEpoch.CreatedSource,
            },
            sources);
    }

    [Fact]
    public void A_client_stamp_taken_before_a_reconnect_is_stale_after_it()
    {
        ClientWorldEpoch.Note(4001);
        int stamped = PluginWorldState.Instance.LoadEpoch;
        Assert.False(PluginWorldState.Instance.IsStale(stamped));

        // Server change: a straight swap from one world to another, no gap observed.
        ClientWorldEpoch.Note(4002);

        Assert.True(PluginWorldState.Instance.IsStale(stamped));
        Assert.Contains(
            "world: load epoch 2 -> 3 (loads=2, source=" + ClientWorldEpoch.CreatedSource
            + "); entity ids recorded before this are no longer valid.",
            _log.Infos);
    }

    [Fact]
    public void A_client_world_that_never_appears_never_moves_the_epoch()
    {
        Assert.False(ClientWorldEpoch.Note(0));
        Assert.False(ClientWorldEpoch.Note(0));

        Assert.Equal(1, WorldEpoch.Current);
        Assert.Empty(_log.Infos);
    }

    /// <summary>
    /// R33 finding 13. These statics are Core-generation-lifetime, not process-lifetime: a hot
    /// swap replaces the whole ALC and the counter starts over. The epoch is generation-stamped so
    /// that a stamp from the previous generation — one a plugin kept beside an entity, or
    /// persisted through <c>ModSave</c> — is stale by construction rather than by luck.
    ///
    /// <para>This one hand-picks the stamps, so it pins the arithmetic only; R34 finding 17 is
    /// that the arithmetic was fed a <b>random</b> 11-bit nonce, which is what
    /// <see cref="Every_pair_of_consecutive_Bootstrap_generations_gets_a_different_stamp"/> and
    /// <see cref="A_hot_swap_with_no_world_load_on_either_side_is_stale_for_every_generation_pair"/>
    /// cover.</para>
    /// </summary>
    [Fact]
    public void A_stamp_from_a_previous_Core_generation_is_stale_even_at_the_same_load_count()
    {
        WorldEpoch.ResetForTests(generation: 7);
        WorldEpoch.Advance("LoadPersistenceSystemV2.CopyDataFromPrefabs");
        int stamped = WorldEpoch.Current;
        Assert.False(WorldEpoch.IsStale(stamped));
        Assert.Equal(7, WorldEpoch.Generation);

        // The Core swap: same load count reached in the new generation, different nonce.
        WorldEpoch.ResetForTests(generation: 8);
        WorldEpoch.Advance("LoadPersistenceSystemV2.CopyDataFromPrefabs");

        Assert.Equal(1, WorldEpoch.Loads);
        Assert.Equal(8, WorldEpoch.Generation);
        Assert.NotEqual(stamped, WorldEpoch.Current);
        Assert.True(WorldEpoch.IsStale(stamped));
        Assert.True(PluginWorldState.Instance.IsStale(stamped));
    }

    [Fact]
    public void Every_generation_epoch_is_positive_and_no_generation_can_reach_another_ones_range()
    {
        for (int generation = 1; generation <= 2047; generation++)
        {
            WorldEpoch.ResetForTests(generation);
            Assert.Equal(generation, WorldEpoch.Generation);
            Assert.True(WorldEpoch.Current > 0, "epoch must stay a positive int for generation " + generation);
            Assert.True(WorldEpoch.IsStale(default));

            // The load field is 20 bits wide, so a generation owns [n<<20 + 1, (n+1)<<20).
            Assert.Equal(generation << 20, WorldEpoch.Current - 1);
        }
    }

    /// <summary>
    /// R34 finding 17, the fix. The stamp is no longer drawn at random: it is Bootstrap's own
    /// generation number, folded into the 11-bit field with period 2047. Two <i>consecutive</i>
    /// generations — the only pair a hot swap can produce — therefore never share a stamp, so
    /// "stale by construction" is a property and not a 2047/2048 coin flip.
    /// </summary>
    [Fact]
    public void Every_pair_of_consecutive_Bootstrap_generations_gets_a_different_stamp()
    {
        // Bootstrap's first successful load is generation 1 (CoreReloadPolicy.FirstGeneration).
        var seen = new Dictionary<int, int>();
        for (int generation = 1; generation <= 6000; generation++)
        {
            int stamp = WorldEpoch.StampForGeneration(generation);

            Assert.InRange(stamp, 1, 2047);
            Assert.NotEqual(stamp, WorldEpoch.StampForGeneration(generation + 1));

            // No two of any 2047 consecutive generations collide either.
            if (seen.TryGetValue(stamp, out int previous))
                Assert.True(
                    generation - previous >= 2047,
                    "generations " + previous + " and " + generation + " share stamp " + stamp);
            seen[stamp] = generation;
        }
    }

    /// <summary>
    /// The case R34 finding 17 named as both the most likely and the most damaging: a Core swap
    /// with no world load on either side, so both generations sit at load count 0 and only the
    /// stamp separates them. Run over every adjacent generation pair in a long process.
    /// </summary>
    [Fact]
    public void A_hot_swap_with_no_world_load_on_either_side_is_stale_for_every_generation_pair()
    {
        for (int generation = 1; generation <= 2100; generation++)
        {
            WorldEpoch.ResetForTests(WorldEpoch.StampForGeneration(generation));
            int stamped = WorldEpoch.Current;
            Assert.False(WorldEpoch.IsStale(stamped));

            WorldEpoch.ResetForTests(WorldEpoch.StampForGeneration(generation + 1));

            Assert.Equal(0, WorldEpoch.Loads);
            Assert.True(
                WorldEpoch.IsStale(stamped),
                "a stamp from generation " + generation + " survived the swap to " + (generation + 1));
            Assert.True(PluginWorldState.Instance.IsStale(stamped));
        }
    }

    /// <summary>
    /// Where the generation number comes from: <c>CoreReloadPolicy.AlcName(int)</c> names the
    /// collectible context <c>progenitor-core:{generation}</c>, and Core reads its own back off
    /// <c>AssemblyLoadContext.GetLoadContext</c>. Nothing else is accepted — a plugin ALC, the
    /// dry-run inspect context, a default-context load, or a generation below Bootstrap's 1.
    /// </summary>
    [Theory]
    [InlineData("progenitor-core:1", true, 1)]
    [InlineData("progenitor-core:3", true, 3)]
    [InlineData("progenitor-core:2048", true, 2048)]
    [InlineData("progenitor-core:0", false, 0)]
    [InlineData("progenitor-core:-2", false, 0)]
    [InlineData("progenitor-core: 3", false, 0)]
    [InlineData("progenitor-core:3x", false, 0)]
    [InlineData("progenitor-core:", false, 0)]
    [InlineData("progenitor-core-inspect", false, 0)]
    [InlineData("progenitor-reload:Sample:1234", false, 0)]
    [InlineData("Default", false, 0)]
    [InlineData("", false, 0)]
    [InlineData(null, false, 0)]
    public void The_generation_is_parsed_only_from_Bootstraps_core_ALC_name(
        string? alcName, bool expected, int expectedGeneration)
    {
        Assert.Equal(expected, WorldEpoch.TryParseCoreGeneration(alcName, out int generation));
        Assert.Equal(expectedGeneration, generation);
    }

    /// <summary>
    /// The tests do not run inside a <c>progenitor-core:N</c> context, so the fallback path is the
    /// one under test here: it must still produce a usable stamp and must say, through
    /// <see cref="WorldEpoch.GenerationIsExact"/> and the log line, that it is the approximate one
    /// rather than claiming a guarantee it cannot make.
    /// </summary>
    [Fact]
    public void Outside_Bootstrap_the_stamp_is_a_fallback_and_says_so()
    {
        Assert.Equal(0, WorldEpoch.CoreGenerationOrZero());
        Assert.False(WorldEpoch.GenerationIsExact);

        string line = WorldEpoch.DescribeGeneration();
        Assert.Contains("approximate", line);
        Assert.Contains("1/2048", line);
        Assert.DoesNotContain("exact,", line);
    }

    /// <summary>
    /// R34 finding 16, the World half: the client watch must not walk <c>World.s_AllWorlds</c> on
    /// the frames that make up a session. A connected frame answers off the cached world and
    /// never walks; an empty world list with nothing cached cannot hold a client world and is
    /// skipped too.
    /// </summary>
    [Fact]
    public void The_client_watch_never_walks_the_world_list_while_the_cached_world_answers()
    {
        var policy = new ClientWorldScanPolicy();

        // Frame 1, main menu with worlds in the list but no client world cached: it walks.
        Assert.True(policy.ShouldScan(cachedIsLive: false, cachedLost: false, worldCount: 1));

        // The client world comes up. WorldReady says so, which clears the bounded uncached
        // interval (job FEPOCH5), so the walk that resolves it is the very next frame.
        policy.ScanSoon();
        Assert.True(policy.ShouldScan(cachedIsLive: false, cachedLost: false, worldCount: 2));

        // Then the cached world answers every frame for the rest of the session — 100,000 frames,
        // not one list walk.
        for (int i = 0; i < 100_000; i++)
            Assert.False(policy.ShouldScan(cachedIsLive: true, cachedLost: false, worldCount: -1));
        Assert.Equal(2L, policy.Scans);
        Assert.Equal(100_002L, policy.Ticks);

        // An empty world list with nothing cached: no walk, because there is nothing to walk.
        for (int i = 0; i < 600; i++)
            Assert.False(policy.ShouldScan(cachedIsLive: false, cachedLost: false, worldCount: 0));
        Assert.Equal(2L, policy.Scans);

        // ... but an empty list on the frame a cached world was lost still resolves: that frame
        // has a world *ending* to report and must reach Note(0).
        Assert.True(policy.ShouldScan(cachedIsLive: false, cachedLost: true, worldCount: 0));
        Assert.Equal(3L, policy.Scans);

        policy.Forget();
        Assert.Equal(0L, policy.Ticks);
        Assert.Equal(0L, policy.Scans);
        Assert.True(policy.ShouldScan(cachedIsLive: false, cachedLost: false, worldCount: 2));
    }

    /// <summary>
    /// R35 finding 7 (job FEPOCH4): the paired-churn case the policy's docstring claimed — a world
    /// destroyed and another created between two frames, leaving <c>World.s_AllWorlds.Count</c>
    /// exactly where it was. It must scan in that frame both when a client world was cached (the
    /// cache reports it gone) and when none was, which is every frame before a join and every
    /// frame after a disconnect. The old rule covered only the first: with nothing cached the
    /// unchanged count suppressed the walk and the watch stayed blind.
    /// </summary>
    [Fact]
    public void The_client_watch_rescans_on_paired_world_churn_with_and_without_a_cached_world()
    {
        // With a cached world: it was the one destroyed, so the cache says so and the frame walks
        // even though the count did not move.
        var cached = new ClientWorldScanPolicy();
        Assert.True(cached.ShouldScan(cachedIsLive: false, cachedLost: false, worldCount: 3));
        for (int i = 0; i < 50; i++)
            Assert.False(cached.ShouldScan(cachedIsLive: true, cachedLost: false, worldCount: -1));
        Assert.True(cached.ShouldScan(cachedIsLive: false, cachedLost: true, worldCount: 3));
        Assert.Equal(2L, cached.Scans);

        // With no cached world — main menu, or the frames after a disconnect. The count is 3 on
        // every frame; between frame N and N+1 one world was destroyed and the client world was
        // created, so the count is a lie and only a walk can see it. The walk is bounded (job
        // FEPOCH5, R36 finding 4), so the coverage claim is not "every frame" but "no unchanged
        // count can suppress the walk for longer than the interval": in every window of
        // UncachedScanIntervalFrames frames there is exactly one walk, forever.
        int interval = ClientWorldScanPolicy.UncachedScanIntervalFrames;
        var uncached = new ClientWorldScanPolicy();
        for (int window = 0; window < 20; window++)
        {
            int scansThisWindow = 0;
            for (int i = 0; i < interval; i++)
                if (uncached.ShouldScan(cachedIsLive: false, cachedLost: false, worldCount: 3))
                    scansThisWindow++;
            Assert.Equal(1, scansThisWindow);
        }
        Assert.Equal(20L, uncached.Scans);
        Assert.Equal(20L * interval, uncached.Ticks);
    }

    /// <summary>
    /// R36 finding 4 (job FEPOCH5): with nothing cached, FEPOCH4's fix walked the whole world list
    /// on <i>every</i> frame — and the uncached state is the whole main menu, the whole loading
    /// screen and every frame after a disconnect, not a handful of frames. Five minutes on the
    /// server browser at 144 fps is 43,200 frames and was 43,200 full walks. The walk must still
    /// happen (a count is not an identity, R35 finding 7) but at a bounded rate.
    /// </summary>
    [Fact]
    public void The_uncached_walk_is_bounded_to_one_frame_in_the_interval()
    {
        int interval = ClientWorldScanPolicy.UncachedScanIntervalFrames;
        var policy = new ClientWorldScanPolicy();

        // The first uncached frame walks: entering this state must not wait.
        Assert.True(policy.ShouldScan(cachedIsLive: false, cachedLost: false, worldCount: 3));

        // Five minutes of main menu at 144 fps.
        const int FiveMinutesAt144Fps = 144 * 60 * 5;
        for (int i = 1; i < FiveMinutesAt144Fps; i++)
            policy.ShouldScan(cachedIsLive: false, cachedLost: false, worldCount: 3);

        Assert.Equal((long)FiveMinutesAt144Fps, policy.Ticks);
        Assert.Equal(FiveMinutesAt144Fps / interval, policy.Scans);
        Assert.Equal(1440L, policy.Scans);   // was 43,200 before this job

        // An empty list still costs nothing and does not spend interval budget: the frame after a
        // run of empty frames still walks as soon as there is something to walk.
        var empty = new ClientWorldScanPolicy();
        Assert.True(empty.ShouldScan(cachedIsLive: false, cachedLost: false, worldCount: 2));
        for (int i = 0; i < 10_000; i++)
            Assert.False(empty.ShouldScan(cachedIsLive: false, cachedLost: false, worldCount: 0));
        Assert.Equal(1L, empty.Scans);
    }

    /// <summary>
    /// The other half of R36 finding 4's fix: the bound is only affordable because the event path
    /// carries every creation. A <c>WorldReady</c> — the moment a client world appears — calls
    /// <c>ScanSoon</c>, so the next uncached frame walks immediately instead of waiting out the
    /// interval, and a disconnect (a cached world lost) is never deferred either, because that
    /// frame has an ending to report and nothing publishes an ending.
    /// </summary>
    [Fact]
    public void A_world_ready_and_a_lost_world_both_beat_the_interval()
    {
        int interval = ClientWorldScanPolicy.UncachedScanIntervalFrames;
        var policy = new ClientWorldScanPolicy();

        // Sitting at the menu: one walk, then the interval is in effect.
        Assert.True(policy.ShouldScan(cachedIsLive: false, cachedLost: false, worldCount: 2));
        Assert.False(policy.ShouldScan(cachedIsLive: false, cachedLost: false, worldCount: 2));
        Assert.False(policy.ShouldScan(cachedIsLive: false, cachedLost: false, worldCount: 2));

        // WorldReady fires. The very next frame walks — it does not wait out the remaining ~27.
        policy.ScanSoon();
        Assert.True(policy.ShouldScan(cachedIsLive: false, cachedLost: false, worldCount: 3));
        Assert.Equal(2L, policy.Scans);

        // Connected. No walks, and leaving this state must not inherit a stale countdown, so the
        // frame the world is lost walks — and so would the first uncached frame after it.
        for (int i = 0; i < 5_000; i++)
            Assert.False(policy.ShouldScan(cachedIsLive: true, cachedLost: false, worldCount: -1));
        Assert.Equal(2L, policy.Scans);

        Assert.True(policy.ShouldScan(cachedIsLive: false, cachedLost: true, worldCount: 3));
        Assert.Equal(3L, policy.Scans);

        // A lost world is a walk like any other, so it re-arms the interval rather than letting
        // the post-disconnect frames walk freely.
        for (int i = 0; i < interval - 1; i++)
            Assert.False(policy.ShouldScan(cachedIsLive: false, cachedLost: false, worldCount: 3));
        Assert.True(policy.ShouldScan(cachedIsLive: false, cachedLost: false, worldCount: 3));
        Assert.Equal(4L, policy.Scans);
    }

    /// <summary>
    /// R36 finding 4's other cost: the walk itself marshaled one Il2Cpp <c>Name</c> string per
    /// world per walk. <c>World.SequenceNumber</c> is assigned once and never reused, so a world
    /// already classified as not-<c>Client_0</c> is skipped on the <c>ulong</c> alone and
    /// <c>Name</c> is read only for a candidate. After the first walk of a stable list, no walk
    /// marshals anything.
    /// </summary>
    [Fact]
    public void The_walk_reads_a_world_name_once_and_never_again()
    {
        var filter = new ClientWorldNameFilter();

        // Frame 1, three worlds, none of them the client: three name reads.
        ulong[] worlds = { 4UL, 7UL, 11UL };
        foreach (ulong seq in worlds)
        {
            Assert.True(filter.NeedsNameRead(seq));
            filter.RecordNotClient(seq);
        }
        Assert.Equal(3L, filter.NameReads);

        // 1,000 more walks of the same list: not one string marshaled.
        for (int walk = 0; walk < 1_000; walk++)
            foreach (ulong seq in worlds)
                Assert.False(filter.NeedsNameRead(seq));
        Assert.Equal(3L, filter.NameReads);

        // The client world is created. Its sequence number has never been classified, so it is a
        // candidate and its name is read — once.
        const ulong ClientSeq = 12UL;
        Assert.True(filter.NeedsNameRead(ClientSeq));
        filter.RecordClient(ClientSeq);
        Assert.Equal(4L, filter.NameReads);

        // A client world is never filtered out: a later walk must be able to find it again.
        Assert.True(filter.NeedsNameRead(ClientSeq));
        Assert.Equal(3, filter.Remembered);

        // The set is bounded: a process that somehow saw hundreds of worlds cannot leak.
        for (ulong seq = 1_000; seq < 1_000 + (ulong)(ClientWorldNameFilter.Capacity * 4); seq++)
            filter.RecordNotClient(seq);
        Assert.True(filter.Remembered <= ClientWorldNameFilter.Capacity);

        filter.Forget();
        Assert.Equal(0L, filter.NameReads);
        Assert.Equal(0, filter.Remembered);
        Assert.True(filter.NeedsNameRead(4UL));
    }

    [Fact]
    public void The_plugin_facade_reads_the_same_counter()
    {
        var facade = PluginWorldState.Instance;
        int stamped = facade.LoadEpoch;

        Assert.Equal(WorldEpoch.Current, stamped);
        Assert.False(facade.IsStale(stamped));

        WorldEpoch.Advance("reload");

        Assert.True(facade.IsStale(stamped));
        Assert.Equal(1, facade.Loads);
        Assert.Equal("world: epoch=2 loads=1", facade.Describe());
    }

    // ---- job FEVT1 / R44 finding 3: WorldReady is keyed on identity, not on the name -----------

    /// <summary>
    /// The one that matters. <c>EventHooks.WatchWorlds</c> samples the game's world list once per
    /// <c>Plugin.Frame</c>; the client destroys and rebuilds <c>Client_0</c> on every disconnect,
    /// rejoin and server change, so the destroy and the create can both happen between two of our
    /// samples. The old ledger was a <c>HashSet&lt;string&gt;</c> of names: the name was still in
    /// it, <c>Add</c> returned false, and no <c>WorldReady</c> was published for the world that
    /// actually existed — every mod that initializes on that event silently never initialized for
    /// the session. A <c>HashSet&lt;string&gt;</c> cannot pass this test at all.
    /// </summary>
    [Fact]
    public void A_world_destroyed_and_recreated_under_one_name_between_two_frames_publishes_again()
    {
        var ledger = new WorldReadyLedger();

        // Frame N: Client_0 is sequence 5.
        Assert.True(ledger.NoteFound("Client_0", 5UL));
        Assert.False(ledger.NoteFound("Client_0", 5UL));
        Assert.False(ledger.NoteFound("Client_0", 5UL));

        // Between frame N and N+1 the client tears down sequence 5 and creates sequence 7. We
        // never see a frame with no client world, so nothing is ever removed by name.
        Assert.True(ledger.NoteFound("Client_0", 7UL));
        Assert.False(ledger.NoteFound("Client_0", 7UL));

        Assert.True(ledger.TryGetPublished("Client_0", out ulong published));
        Assert.Equal(7UL, published);
    }

    [Fact]
    public void A_world_that_goes_away_and_comes_back_publishes_again()
    {
        var ledger = new WorldReadyLedger();

        Assert.True(ledger.NoteFound("Client_0", 5UL));
        ledger.NoteGone("Client_0");
        Assert.False(ledger.TryGetPublished("Client_0", out _));

        // A fresh world, and also the degenerate case where the game somehow re-presents the same
        // number: a name we hold nothing for always publishes.
        Assert.True(ledger.NoteFound("Client_0", 9UL));
        ledger.NoteGone("Client_0");
        Assert.True(ledger.NoteFound("Client_0", 9UL));
    }

    [Fact]
    public void The_two_world_names_are_tracked_independently()
    {
        var ledger = new WorldReadyLedger();

        Assert.True(ledger.NoteFound("Server", 1UL));
        Assert.True(ledger.NoteFound("Client_0", 2UL));
        Assert.Equal(2, ledger.Count);

        Assert.False(ledger.NoteFound("Server", 1UL));
        Assert.True(ledger.NoteFound("Client_0", 3UL));

        ledger.NoteGone("Client_0");
        Assert.Equal(1, ledger.Count);
        Assert.True(ledger.TryGetPublished("Server", out ulong server));
        Assert.Equal(1UL, server);

        ledger.Clear();
        Assert.Equal(0, ledger.Count);
        Assert.True(ledger.NoteFound("Server", 1UL));
    }

    /// <summary>
    /// The Il2Cpp <c>SequenceNumber</c> getter can throw, and the caller passes
    /// <see cref="WorldReadyLedger.UnknownSequence"/> then. A missed <c>WorldReady</c> is the
    /// failure that matters, so the first sighting publishes — but a getter that throws on every
    /// frame must cost one event, not one per frame.
    /// </summary>
    [Fact]
    public void An_unreadable_sequence_number_publishes_once_and_never_storms()
    {
        var ledger = new WorldReadyLedger();

        Assert.True(ledger.NoteFound("Client_0", WorldReadyLedger.UnknownSequence));
        for (int i = 0; i < 200; i++)
            Assert.False(ledger.NoteFound("Client_0", WorldReadyLedger.UnknownSequence));
    }

    /// <summary>
    /// And when the number becomes readable again the ledger learns it without publishing, so the
    /// next genuine swap is still caught. The residual gap — a swap entirely inside a run of
    /// unreadable frames — is the old name-only behavior and is documented, not tested away.
    /// </summary>
    [Fact]
    public void A_sequence_number_that_becomes_readable_is_learned_without_a_second_publish()
    {
        var ledger = new WorldReadyLedger();

        Assert.True(ledger.NoteFound("Client_0", WorldReadyLedger.UnknownSequence));
        Assert.False(ledger.NoteFound("Client_0", 5UL));
        Assert.True(ledger.TryGetPublished("Client_0", out ulong published));
        Assert.Equal(5UL, published);

        Assert.True(ledger.NoteFound("Client_0", 7UL));
    }

    /// <summary>
    /// A frame that cannot read the number of a world we already have an identity for keeps that
    /// identity rather than forgetting it — otherwise one throwing frame would publish a duplicate
    /// <c>WorldReady</c> for a world nothing happened to.
    /// </summary>
    [Fact]
    public void A_known_identity_survives_a_frame_that_could_not_read_the_number()
    {
        var ledger = new WorldReadyLedger();

        Assert.True(ledger.NoteFound("Client_0", 5UL));
        Assert.False(ledger.NoteFound("Client_0", WorldReadyLedger.UnknownSequence));
        Assert.False(ledger.NoteFound("Client_0", 5UL));

        Assert.True(ledger.TryGetPublished("Client_0", out ulong published));
        Assert.Equal(5UL, published);
    }

    [Fact]
    public void An_empty_name_is_ignored_rather_than_stored()
    {
        var ledger = new WorldReadyLedger();

        Assert.False(ledger.NoteFound("", 5UL));
        Assert.False(ledger.NoteFound(null!, 5UL));
        ledger.NoteGone("");
        Assert.Equal(0, ledger.Count);
    }

    /// <summary>
    /// The ledger above is only the rule; this pins that <c>EventHooks</c> actually asks it, and
    /// that the name-keyed set is gone. <c>EventHooks.cs</c> takes game types and is not compiled
    /// into this assembly, so this is a source pin and says so — without it the ledger could pass
    /// every test above while <c>WatchWorlds</c> kept its <c>HashSet&lt;string&gt;</c>.
    /// </summary>
    [Fact]
    public void EventHooks_asks_the_ledger_with_a_sequence_number_and_keeps_no_name_set()
    {
        var text = System.IO.File.ReadAllText(RepoFile("src/Progenitor.Core/Events/EventHooks.cs"));

        SourcePin.AssertStatement(text, "private static readonly WorldReadyLedger ReadyWorlds = new();");
        Assert.DoesNotContain("HashSet<string> ReadyWorlds", text, StringComparison.Ordinal);

        int at = text.IndexOf("private static void WatchWorlds()", StringComparison.Ordinal);
        Assert.True(at > 0);
        var body = text.Substring(at);
        int end = body.IndexOf("\n    }", StringComparison.Ordinal);
        Assert.True(end > 0);
        body = body.Substring(0, end);

        SourcePin.AssertUncommented(body, "ReadyWorlds.NoteFound(name, ReadSequence(name, world))");
        SourcePin.AssertStatement(body, "ReadyWorlds.NoteGone(name)");
        Assert.DoesNotContain("ReadyWorlds.Add(", body, StringComparison.Ordinal);
        Assert.DoesNotContain("ReadyWorlds.Remove(", body, StringComparison.Ordinal);

        // The getter is an Il2Cpp property on a wrapper the game owns, and this runs in a frame
        // callback on the game main thread: it must not propagate.
        int read = text.IndexOf("private static ulong ReadSequence(", StringComparison.Ordinal);
        Assert.True(read > 0);
        var reader = text.Substring(read, Math.Min(1200, text.Length - read));
        SourcePin.AssertStatement(reader, "try");
        SourcePin.AssertStatement(reader, "catch (Exception e)");
        SourcePin.AssertUncommented(reader, "WorldReadyLedger.UnknownSequence");
    }

    private static string RepoFile(string relative)
    {
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 12 && dir != null; i++)
        {
            var candidate = System.IO.Path.Combine(dir, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));
            if (System.IO.File.Exists(candidate)) return candidate;
            dir = System.IO.Path.GetDirectoryName(dir);
        }
        throw new System.IO.FileNotFoundException("could not locate " + relative + " above " + AppContext.BaseDirectory);
    }
}
