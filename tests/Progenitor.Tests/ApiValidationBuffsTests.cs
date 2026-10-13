using System;
using System.Collections.Generic;
using Progenitor.Buffs;
using Progenitor.Events;
using Progenitor.WorldState;
using Xunit;
using BuffsApi = Progenitor.Buffs.Buffs;

namespace Progenitor.Tests;

/// <summary>
/// Edge-of-signature coverage for the public <see cref="BuffsApi"/> surface: argument guards,
/// overloads, builder members and the list-versus-identity queries that
/// <c>BuffLifecycleTests</c> does not reach. Drives the production module behind its
/// <see cref="IBuffRuntime"/> seam; nothing asserts about source text.
/// </summary>
[Collection("PluginLogger")]
public sealed class ApiValidationBuffsTests : IDisposable
{
    private const string BuffId = "test.api.buff";
    private const string OtherBuffId = "test.api.other";
    private const int Guid = 4711;
    private const int OtherGuid = 4712;
    private const string PluginId = "test.api.plugin";

    private static readonly BuffEntityId Target = new(310, 1);
    private static readonly BuffEntityId SecondTarget = new(311, 1);
    private static readonly BuffEntityId Instance = new(410, 2);
    private static readonly BuffEntityId SecondInstance = new(411, 2);

    private readonly TestLogSource _log = new();
    private readonly FakeBuffWorld _world = new();
    private readonly object _owner = new PluginOwner();

    public ApiValidationBuffsTests()
    {
        Plugin.Logger = _log;
        WorldEpoch.ResetForTests();
        BuffsApi.ResetForTests();

        _world.Register(BuffId, Guid);
        _world.Register(OtherBuffId, OtherGuid);
        _world.AddTarget(Target);
        _world.AddTarget(SecondTarget);
        BuffsApi.SetRuntimeForTests(_world);
    }

    public void Dispose()
    {
        BuffsApi.ResetForTests();
        WorldEpoch.ResetForTests();
        Plugin.Logger = null;
    }

    private sealed class PluginOwner
    {
    }

    private IBuffApplication Apply(
        string buffId = BuffId,
        BuffEntityId? target = null,
        float seconds = 30f,
        object? owner = null,
        BuffRefreshPolicy refresh = BuffRefreshPolicy.Reject)
    {
        var request = BuffRequest.For(buffId, target ?? Target).WithRefresh(refresh);
        request = seconds > 0f ? request.WithDuration(seconds) : request.WithPrefabDuration();
        return BuffsApi.Apply(request, owner ?? _owner, PluginId);
    }

    /// <summary>Apply → queued → instance appears → confirmed, the sequence the module documents.</summary>
    private IBuffApplication ApplyAndConfirm(
        string buffId, BuffEntityId target, int guid, BuffEntityId instance, float seconds = 30f)
    {
        var application = Apply(buffId, target, seconds);
        Assert.Equal(BuffApplicationState.Requested, application.State);
        _world.CreateInstance(target, guid, instance);
        BuffsApi.TickForTests();
        Assert.Equal(BuffApplicationState.Active, application.State);
        return application;
    }

    // -----------------------------------------------------------------
    // Apply: the argument contract at the edge of the signature
    // -----------------------------------------------------------------

    /// <summary>
    /// <c>Apply</c> promises it "never throws for a refusal" and throws "only for a null request or
    /// an off-main-thread call, both of which are caller bugs". The null half is the one nothing
    /// drove: the check has to come <b>before</b> the world is touched, or a caller bug costs a
    /// prefab resolve and an apply-path probe on the way to the exception.
    /// </summary>
    [Fact]
    public void A_null_request_is_a_caller_bug_and_nothing_is_read_before_it_throws()
    {
        Assert.Throws<ArgumentNullException>(() => BuffsApi.Apply(null!, _owner, PluginId));

        Assert.Equal(0, _world.ResolveCalls);
        Assert.Equal(0, _world.ApplyCalls);
        Assert.Equal(0, BuffsApi.LiveCountForTests);
    }

    /// <summary>
    /// The owner is <c>object?</c>, so a caller may legitimately pass nothing — a one-off apply from
    /// a command handler that has no subsystem token. That must still work end to end and must still
    /// be removable, because the only thing holding it is the returned handle: the owner-keyed sweeps
    /// (<see cref="Buffs.Active"/>, <see cref="Buffs.RemoveAll"/>) cannot be given null.
    /// </summary>
    [Fact]
    public void An_unowned_application_still_applies_and_is_removed_through_its_own_handle()
    {
        var application = BuffsApi.Apply(BuffRequest.For(BuffId, Target).WithDuration(30f));

        Assert.Equal(BuffApplicationState.Requested, application.State);
        Assert.Equal("unowned", application.PluginId);

        _world.CreateInstance(Target, Guid, Instance);
        BuffsApi.TickForTests();
        Assert.Equal(BuffApplicationState.Active, application.State);

        application.Remove();

        Assert.Equal(BuffApplicationState.Removed, application.State);
        Assert.Equal(new[] { Instance }, _world.Destroyed);
        Assert.Equal(0, BuffsApi.LiveCountForTests);
    }

    /// <summary>
    /// The three module-level queries divide into "identify an owner" and "look something up". The
    /// first two take <c>object owner</c> and cannot answer for null, so they throw rather than
    /// silently matching every unowned application; <see cref="Buffs.Find"/> is a total function over
    /// a string and answers null. A caller that reads a buff id out of chat hits the third case.
    /// </summary>
    [Fact]
    public void The_owner_queries_reject_null_while_Find_tolerates_a_missing_id()
    {
        Assert.Throws<ArgumentNullException>(() => BuffsApi.Active(null!));
        Assert.Throws<ArgumentNullException>(() => BuffsApi.RemoveAll(null!));

        ApplyAndConfirm(BuffId, Target, Guid, Instance);

        Assert.Null(BuffsApi.Find(null!, Target));
        Assert.Null(BuffsApi.Find("", Target));
        Assert.Null(BuffsApi.Find("   ", Target));
        Assert.NotNull(BuffsApi.Find(BuffId, Target));
        Assert.Empty(_world.Destroyed);
    }

    // -----------------------------------------------------------------
    // Active: what the snapshot contains, and that it is a snapshot
    // -----------------------------------------------------------------

    /// <summary>
    /// <c>Active</c> is documented as "a snapshot". The obvious thing to do with it is walk it and
    /// remove as you go, and that is exactly what a live view would break — removal takes the
    /// application out of the module's own list mid-enumeration.
    /// </summary>
    [Fact]
    public void Active_hands_out_a_snapshot_that_a_removal_during_the_walk_does_not_disturb()
    {
        ApplyAndConfirm(BuffId, Target, Guid, Instance);
        ApplyAndConfirm(BuffId, SecondTarget, Guid, SecondInstance);

        var snapshot = BuffsApi.Active(_owner);
        Assert.Equal(2, snapshot.Count);

        int walked = 0;
        foreach (var application in snapshot)
        {
            application.Remove();
            walked++;
        }

        Assert.Equal(2, walked);
        Assert.Equal(2, snapshot.Count);
        Assert.Empty(BuffsApi.Active(_owner));
        Assert.Equal(2, _world.Destroyed.Count);
    }

    /// <summary>
    /// A cancel that arrives before the game confirmed the apply leaves the application holding the
    /// (target, buff) slot — it is the one that will destroy a late instance — but it is nobody's to
    /// act on any more. <c>Find</c> is already driven for this; <c>Active</c> is the other query and
    /// was not, and a plugin that walks <c>Active</c> to refresh its buffs would otherwise be handed
    /// a handle whose removal is already in flight.
    /// </summary>
    [Fact]
    public void Active_skips_an_application_whose_cancel_is_still_in_flight()
    {
        var application = Apply();
        Assert.Single(BuffsApi.Active(_owner));

        application.Remove();

        Assert.Equal(BuffApplicationState.Requested, application.State);
        Assert.Empty(BuffsApi.Active(_owner));
        Assert.Null(BuffsApi.Find(BuffId, Target));

        // Still holding the slot, which is why the cancel is not just a removal from the list.
        var second = Apply();
        Assert.Equal(BuffRefusal.CancelInFlight, second.Refusal);
    }

    // -----------------------------------------------------------------
    // Find: what identifies an application
    // -----------------------------------------------------------------

    /// <summary>
    /// A buff id is the input to <c>PrefabGUID.CalculateGuidHash</c>, so it is case-sensitive, and an
    /// application is identified by <i>both</i> halves of (id, target). Neither is pinned anywhere:
    /// one plugin buffing two players is the ordinary case, and handing back the wrong player's
    /// application would retime — and on removal destroy — the wrong buff.
    /// </summary>
    [Fact]
    public void Find_is_scoped_to_one_target_and_matches_the_buff_id_exactly()
    {
        var first = ApplyAndConfirm(BuffId, Target, Guid, Instance);
        var second = ApplyAndConfirm(BuffId, SecondTarget, Guid, SecondInstance);

        Assert.Same(first, BuffsApi.Find(BuffId, Target));
        Assert.Same(second, BuffsApi.Find(BuffId, SecondTarget));

        Assert.Null(BuffsApi.Find(BuffId.ToUpperInvariant(), Target));
        Assert.Null(BuffsApi.Find(OtherBuffId, Target));
        Assert.Null(BuffsApi.Find(BuffId, new BuffEntityId(999, 1)));
    }

    // -----------------------------------------------------------------
    // RemoveAll: "safe to call twice"
    // -----------------------------------------------------------------

    /// <summary>
    /// <c>RemoveAll</c> is documented "safe to call twice" and is what a plugin calls to retire one
    /// subsystem. Twice in a row must not re-issue a destroy: the entity is already on its way out,
    /// and a second <c>DestroyUtility.Destroy</c> against an id the game has reissued is a write
    /// against a stranger.
    /// </summary>
    [Fact]
    public void RemoveAll_destroys_once_and_a_second_call_asks_for_nothing()
    {
        ApplyAndConfirm(BuffId, Target, Guid, Instance);

        BuffsApi.RemoveAll(_owner);
        Assert.Equal(new[] { Instance }, _world.Destroyed);

        BuffsApi.RemoveAll(_owner);
        Assert.Equal(new[] { Instance }, _world.Destroyed);

        // And an owner that never applied anything is not an error either.
        BuffsApi.RemoveAll(new PluginOwner());
        Assert.Equal(new[] { Instance }, _world.Destroyed);
        Assert.Equal(0, BuffsApi.LiveCountForTests);
    }

    // -----------------------------------------------------------------
    // BuffRequest: the builder members
    // -----------------------------------------------------------------

    /// <summary>
    /// The <c>TimeSpan</c> overload is a public member with no coverage at all. It is the one a
    /// caller reaches for when the duration comes from config (<c>TimeSpan.Parse</c>), and it must
    /// land on the instance in seconds — a minutes/seconds mix-up here is silent.
    /// </summary>
    [Fact]
    public void A_duration_given_as_a_TimeSpan_reaches_the_instance_in_seconds()
    {
        var application = BuffsApi.Apply(
            BuffRequest.For(BuffId, Target).WithDuration(TimeSpan.FromSeconds(45)), _owner, PluginId);

        Assert.Equal(45f, application.RequestedDurationSeconds);

        _world.CreateInstance(Target, Guid, Instance);
        BuffsApi.TickForTests();

        Assert.Equal(BuffApplicationState.Active, application.State);
        Assert.Equal(45f, _world.DurationOf(Instance));
    }

    /// <summary>
    /// The same domain rule applies whichever overload produced the number: <c>LifeTime.Duration</c>
    /// is a <c>[SyncRangedFloat(0, 10000, …)]</c> networked field, and four hours is past it. The
    /// float overload is driven for this; the <c>TimeSpan</c> one is the overload where a caller is
    /// most likely to exceed it without noticing, because four hours does not look like a big number.
    /// </summary>
    [Fact]
    public void A_TimeSpan_past_the_networked_range_is_refused_before_anything_is_written()
    {
        var application = BuffsApi.Apply(
            BuffRequest.For(BuffId, Target).WithDuration(TimeSpan.FromHours(4)), _owner, PluginId);

        Assert.Equal(BuffApplicationState.Refused, application.State);
        Assert.Equal(BuffRefusal.BadDuration, application.Refusal);
        Assert.Equal(0, _world.ApplyCalls);
    }

    /// <summary>
    /// Zero and negative are <b>not</b> refusals: <see cref="BuffRequest.KeepsPrefabDuration"/> is
    /// <c>DurationSeconds &lt;= 0f</c>, so they are the keep-the-clone's-own-duration sentinel, and
    /// the confirm must then write no duration at all. This is the branch that decides whether a
    /// <c>buff X 0</c> command leaves a vanilla clone on its own timer or stamps zero seconds onto a
    /// networked field.
    /// </summary>
    [Theory]
    [InlineData(0f)]
    [InlineData(-5f)]
    public void A_non_positive_duration_is_the_prefab_duration_sentinel_not_a_refusal(float seconds)
    {
        var application = BuffsApi.Apply(
            BuffRequest.For(BuffId, Target).WithDuration(seconds), _owner, PluginId);

        Assert.Equal(BuffApplicationState.Requested, application.State);
        Assert.Equal(BuffRequest.KeepPrefabDuration, application.RequestedDurationSeconds);

        _world.CreateInstance(Target, Guid, Instance);
        BuffsApi.TickForTests();

        Assert.Equal(BuffApplicationState.Active, application.State);
        Assert.Equal(0, _world.SetDurationCalls);
    }

    /// <summary>
    /// The <c>With…</c> members exist "so adding a field later cannot break a caller's constructor
    /// call", which only holds while each one carries the other fields through. A request built in
    /// either order must behave identically, and the request it was built from must be unchanged —
    /// a plugin that keeps a prepared <c>BuffRequest</c> as a template and specialises it per call
    /// depends on both.
    /// </summary>
    [Fact]
    public void The_request_builder_carries_every_field_through_whatever_order_it_is_chained_in()
    {
        var template = BuffRequest.For(BuffId, Target);

        var durationFirst = template.WithDuration(30f).WithRefresh(BuffRefreshPolicy.Refresh);
        var refreshFirst = template.WithRefresh(BuffRefreshPolicy.Refresh).WithDuration(30f);

        Assert.Equal(durationFirst.DurationSeconds, refreshFirst.DurationSeconds);
        Assert.Equal(durationFirst.Refresh, refreshFirst.Refresh);
        Assert.Equal(BuffId, refreshFirst.BuffId);
        Assert.Equal(Target, refreshFirst.Target);

        // The template it was specialised from is untouched.
        Assert.Equal(BuffRequest.KeepPrefabDuration, template.DurationSeconds);
        Assert.Equal(BuffRefreshPolicy.Reject, template.Refresh);
        Assert.True(template.KeepsPrefabDuration);

        // And the last call in the chain did not drop the first: the duration lands and the policy
        // is honored, which is only observable through an actual apply.
        var application = BuffsApi.Apply(refreshFirst, _owner, PluginId);
        _world.CreateInstance(Target, Guid, Instance);
        BuffsApi.TickForTests();
        Assert.Equal(30f, _world.DurationOf(Instance));

        var again = BuffsApi.Apply(refreshFirst, _owner, PluginId);
        Assert.Same(application, again);
        Assert.Equal(BuffRefusal.None, again.Refusal);
    }

    /// <summary>
    /// <see cref="BuffRequest.WithPrefabDuration"/> is the undo of <c>WithDuration</c>, and the only
    /// way a caller that has already specialised a template can get back to the clone's own timer.
    /// </summary>
    [Fact]
    public void WithPrefabDuration_takes_a_specialised_request_back_to_the_clone_s_own_timer()
    {
        var request = BuffRequest.For(BuffId, Target).WithDuration(30f).WithPrefabDuration();

        Assert.True(request.KeepsPrefabDuration);

        var application = BuffsApi.Apply(request, _owner, PluginId);
        _world.CreateInstance(Target, Guid, Instance);
        BuffsApi.TickForTests();

        Assert.Equal(BuffApplicationState.Active, application.State);
        Assert.Equal(0, _world.SetDurationCalls);
        Assert.Equal(BuffRequest.KeepPrefabDuration, application.RequestedDurationSeconds);
    }

    /// <summary>
    /// A world of dictionaries, lean enough to keep this file's intent visible: registration,
    /// buffable targets, instances the test creates by hand, and a count of every write asked for so
    /// a "nothing was written" claim is measured rather than assumed. Destroys complete immediately —
    /// the deferred-destroy model and the retirement watch belong to <c>BuffLifecycleTests</c>.
    /// </summary>
    private sealed class FakeBuffWorld : IBuffRuntime
    {
        private readonly Dictionary<string, int> _definitions = new(StringComparer.Ordinal);
        private readonly HashSet<BuffEntityId> _entities = new();
        private readonly HashSet<BuffEntityId> _buffable = new();
        private readonly Dictionary<(BuffEntityId Target, int Guid), BuffEntityId> _instances = new();
        private readonly Dictionary<BuffEntityId, float> _durations = new();

        public bool IsServer { get; set; } = true;

        public bool HasWorld { get; set; } = true;

        public int Epoch => WorldEpoch.Current;

        public int ResolveCalls { get; private set; }

        public int ApplyCalls { get; private set; }

        public int SetDurationCalls { get; private set; }

        public List<BuffEntityId> Destroyed { get; } = new();

        public void Register(string id, int guid) => _definitions[id] = guid;

        public void AddTarget(BuffEntityId entity)
        {
            _entities.Add(entity);
            _buffable.Add(entity);
        }

        public void CreateInstance(BuffEntityId target, int guid, BuffEntityId instance)
        {
            _entities.Add(instance);
            _instances[(target, guid)] = instance;
        }

        public float DurationOf(BuffEntityId instance) =>
            _durations.TryGetValue(instance, out float value) ? value : float.NaN;

        public BuffResolve TryResolveBuff(string buffId, object? owner, string? pluginId, out int prefabGuid)
        {
            ResolveCalls++;
            return _definitions.TryGetValue(buffId ?? "", out prefabGuid)
                ? BuffResolve.Ok
                : BuffResolve.NotRegistered;
        }

        public BuffTargetCheck CheckTarget(BuffEntityId target)
        {
            if (!HasWorld) return BuffTargetCheck.NoWorld;
            if (!_entities.Contains(target)) return BuffTargetCheck.Missing;
            return _buffable.Contains(target) ? BuffTargetCheck.Ok : BuffTargetCheck.NotBuffable;
        }

        public bool RequestApply(BuffEntityId target, int prefabGuid, out string reason)
        {
            reason = "";
            ApplyCalls++;
            return true;
        }

        public bool TryFindInstance(BuffEntityId target, int prefabGuid, out BuffEntityId instance)
        {
            if (_instances.TryGetValue((target, prefabGuid), out instance) && _entities.Contains(instance))
                return true;
            instance = BuffEntityId.None;
            return false;
        }

        public bool SetDuration(BuffEntityId instance, float seconds, out bool timed)
        {
            SetDurationCalls++;
            timed = true;
            if (!_entities.Contains(instance)) return false;
            _durations[instance] = seconds;
            return true;
        }

        public bool TryReadTiming(BuffEntityId instance, out BuffTiming timing)
        {
            timing = default;
            if (!_entities.Contains(instance)) return false;
            timing = new BuffTiming(0f, DurationOf(instance), BuffLifetimeEnd.Destroy);
            return true;
        }

        public BuffDestroy DestroyInstance(BuffEntityId instance)
        {
            Destroyed.Add(instance);
            if (!_entities.Remove(instance)) return BuffDestroy.NotPresent;

            var drop = new List<(BuffEntityId, int)>();
            foreach (var pair in _instances)
                if (pair.Value == instance) drop.Add(pair.Key);
            foreach (var key in drop) _instances.Remove(key);

            _durations.Remove(instance);
            return BuffDestroy.Issued;
        }

        public bool Exists(BuffEntityId entity) => _entities.Contains(entity);
    }
}
