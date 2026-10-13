using System;
using System.Globalization;
using Progenitor.Units;
using UnitsApi = Progenitor.Units.Units;

#if !PROGENITOR_TESTS
using Progenitor.Commands;
using Progenitor.Ecs;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
#endif

namespace Progenitor.Sample;

/// <summary>
/// Sample <c>.rat</c> subcommands. Parsing and handle tracking are game-free so the test
/// project can compile this file; ECS pose lookup and <c>ctx.Units</c> stay behind
/// <c>!PROGENITOR_TESTS</c>.
/// </summary>
internal static class SampleUnits
{
    internal const string PrefabName = "CHAR_Critter_Rat";
    internal const float DefaultDistance = 3f;
    internal const float GoDistance = 6f;

    /// <summary>
    /// The vanilla behavior tree <c>.rat patrol</c> selects. It is the only tree GUID this
    /// project has ever read off a live unit — evidence 2026-09-02 evening §11 step 2 logged
    /// <c>BehaviourTreeBinding.PrefabGUID=-1918585836 name=BEH_Farmlands_Animal_Standard</c>
    /// on the sheep — and it is an animal tree, so it wanders and grazes rather than hunting.
    /// <c>Behaviour.Play</c> forces Idle once before step 1 — the sequencer does that, not this
    /// sample and not the first step — and that is what actually keeps it quiet; the tree
    /// choice only decides what it would do if the module let go. Tree names are not in
    /// the game's metadata (they live in the prefab collection), so an unknown name here is refused
    /// at spawn with one log line and the rat keeps its own tree — the patrol still runs.
    /// </summary>
    internal const string QuietTree = "BEH_Farmlands_Animal_Standard";

    /// <summary>
    /// Clip name for the patrol's Animate step. <b>No vanilla clip name could be cited:</b>
    /// clip names are not in the game's metadata or string literals — they live in the
    /// per-prefab <c>AnimationClipNames</c> blob, which is a Client-world
    /// component. The step is a documented timed no-op on the server today
    /// (<c>UnityUnitBehaviour.Animate</c> says why), so the name is a placeholder that costs
    /// the sequence its 1.5 s and nothing else.
    /// </summary>
    internal const string PatrolAnimation = "Idle";

    /// <summary>
    /// What the player and the log are told about that step. R55 finding 8: the placeholder
    /// was advertised as an animation in the reply, the log and both doc tables, and denied
    /// only in a paragraph nobody typing <c>.rat patrol</c> reads. The step stays in the
    /// sequence — it really does hold the patrol for 1.5 s, and a live observer needs to know
    /// where that pause comes from — but it says what it is everywhere it is mentioned.
    /// </summary>
    internal const string AnimationNote = "Animate('" + PatrolAnimation + "') is a placeholder; no server-side route";

    /// <summary>
    /// What the patrol's <c>UnitStep.Then</c> callback logs when the walk finishes. A log line
    /// and nothing else on purpose: the callback runs on the game main thread between two steps,
    /// and it is a delegate in a reloadable assembly, so it holds no state and closes over
    /// nothing but this constant.
    /// </summary>
    internal const string ArrivedNote = "patrol Then: the rat reached the goal; a mod would do its own work here";

    internal enum Kind
    {
        Spawn = 1,
        Follow = 2,
        Go = 3,
        Off = 4,
        Patrol = 5,
        Stop = 6,

        /// <summary>
        /// Job FEX3. Report what <see cref="SampleUnitEvents"/> has heard: the four unit-event
        /// counters and the last few lines. A read, so it needs no rat and changes nothing.
        /// </summary>
        Status = 7,
    }

    private static readonly object Gate = new();
    private static object? _owner;
    private static string _pluginId = "progenitor.sample";
    private static IUnitHandle? _last;
    private static int _live;

    internal static int LiveCount
    {
        get { lock (Gate) return _live; }
    }

    internal static bool TryParse(string? token, out Kind kind, out float distance)
    {
        kind = Kind.Spawn;
        distance = DefaultDistance;
        if (string.IsNullOrWhiteSpace(token))
            return true;

        string t = token.Trim();
        switch (t.ToLowerInvariant())
        {
            case "follow":
                kind = Kind.Follow;
                return true;
            case "go":
                kind = Kind.Go;
                return true;
            case "off":
                kind = Kind.Off;
                return true;
            case "patrol":
                kind = Kind.Patrol;
                return true;
            case "stop":
                kind = Kind.Stop;
                return true;
            case "status":
                kind = Kind.Status;
                return true;
        }

        if (float.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out float metres)
            && metres > 0f
            && !float.IsNaN(metres)
            && !float.IsInfinity(metres))
        {
            kind = Kind.Spawn;
            distance = metres;
            return true;
        }

        return false;
    }

    internal static void AttachForTests(object owner, string pluginId)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _pluginId = string.IsNullOrWhiteSpace(pluginId) ? "progenitor.sample" : pluginId;
    }

    internal static void Detach()
    {
        Off();
        _owner = null;
        _last = null;
        lock (Gate) _live = 0;
    }

    /// <summary>
    /// Game-free runner used by tests (and by the live command after it has a pose).
    /// Returns the reply text a <c>.rat</c> would send.
    /// </summary>
    internal static string Run(Kind kind, float distance = DefaultDistance, UnitVec3 from = default, UnitVec3 forward = default)
    {
        if (kind == Kind.Off)
            return Off();

        if (kind == Kind.Stop)
            return Stop();

        // The live command replies every line; a single string is what this runner returns, so
        // the counters line stands for the report here.
        if (kind == Kind.Status)
            return SampleUnitEvents.StatusLines()[0];

        if (kind == Kind.Follow)
            return Follow();

        UnitVec3 fwd = forward;
        if (fwd.X == 0f && fwd.Y == 0f && fwd.Z == 0f)
            fwd = new UnitVec3(1f, 0f, 0f);

        if (kind == Kind.Go)
            return Go(from, fwd);

        if (kind == Kind.Patrol)
            return Patrol(from, fwd, TestTarget);

        return SpawnAt(from, fwd, distance);
    }

#if !PROGENITOR_TESTS
    internal static void HandleCommand(ICommandContext ctx, Kind kind, float distance)
    {
        var plugin = Plugin.Active;
        if (plugin == null)
        {
            ctx.Reply("Sample plugin context is not available.");
            return;
        }

        AttachForTests(plugin.Owner, plugin.Guid);

        if (kind == Kind.Off)
        {
            ctx.Reply(Off());
            return;
        }

        if (kind == Kind.Stop)
        {
            ctx.Reply(Stop());
            return;
        }

        if (kind == Kind.Status)
        {
            var lines = SampleUnitEvents.StatusLines();
            for (int i = 0; i < lines.Count; i++)
                ctx.Reply(lines[i]);
            return;
        }

        if (kind == Kind.Follow)
        {
            ctx.Reply(FollowLive(ctx));
            return;
        }

        if (!TryCharacterPose(ctx, out var pos, out var fwd))
        {
            ctx.Reply("No character position; cannot place a rat.");
            return;
        }

        if (kind == Kind.Go)
        {
            ctx.Reply(GoLive(ctx, pos, fwd));
            return;
        }

        if (kind == Kind.Patrol)
        {
            ctx.Reply(PatrolLive(ctx, plugin, pos, fwd));
            return;
        }

        ctx.Reply(SpawnLive(plugin, pos, fwd, distance));
    }

    private static string SpawnLive(SampleContext plugin, UnitVec3 pos, UnitVec3 fwd, float distance)
    {
        var at = Offset(pos, fwd, distance);
        var handle = plugin.Units.Spawn(PrefabName, at);
        Remember(handle);
        string reply = "Spawned rat " + handle.Entity + " at " + handle.Position;
        Log("spawned entity=" + handle.Entity + " pos=" + handle.Position + " distance=" + distance.ToString("0.##", CultureInfo.InvariantCulture));
        if (!handle.Enabled)
            Log("spawn returned a disabled handle (missing prefab or client side)");
        return reply;
    }

    /// <summary>
    /// The live <c>.rat patrol</c>. Same sequence as the game-free path, with the caller's
    /// character as the Face / Follow target.
    /// </summary>
    private static string PatrolLive(ICommandContext ctx, SampleContext plugin, UnitVec3 pos, UnitVec3 fwd)
    {
        var at = Offset(pos, fwd, DefaultDistance);
        var handle = plugin.Units.Spawn(PrefabName, at, QuietTree);
        Remember(handle);
        return StartPatrol(handle, pos, fwd, UnitId.From(ctx.CharacterEntity));
    }

    private static string FollowLive(ICommandContext ctx)
    {
        IUnitHandle? last;
        lock (Gate) last = _last;
        if (last == null || last.IsDisposed || !last.Enabled)
        {
            Log("follow no last rat");
            return "No live rat. Spawn one with .rat first.";
        }

        last.Follow(UnitId.From(ctx.CharacterEntity));
        Log("follow entity=" + last.Entity);
        return "Rat " + last.Entity + " following you.";
    }

    private static string GoLive(ICommandContext ctx, UnitVec3 pos, UnitVec3 fwd)
    {
        _ = ctx;
        IUnitHandle? last;
        lock (Gate) last = _last;
        if (last == null || last.IsDisposed || !last.Enabled)
        {
            Log("go no last rat");
            return "No live rat. Spawn one with .rat first.";
        }

        var goal = Offset(pos, fwd, GoDistance);
        last.GoTo(goal);
        Log("go entity=" + last.Entity + " pos=" + goal);
        return "Rat " + last.Entity + " walking to " + goal + ".";
    }

    /// <summary>
    /// Character pose from the command context. LocalToWorld.Forward with +X fallback,
    /// same as SampleFx on the client.
    /// </summary>
    private static bool TryCharacterPose(ICommandContext ctx, out UnitVec3 position, out UnitVec3 forward)
    {
        position = default;
        forward = new UnitVec3(1f, 0f, 0f);
        try
        {
            var ch = ctx.CharacterEntity;
            if (ch == Entity.Null) return false;
            var em = ctx.World.EntityManager;
            float3 pos;
            float3 fwd = new float3(1f, 0f, 0f);
            if (ch.Has<LocalToWorld>(em))
            {
                var ltw = em.GetComponentData<LocalToWorld>(ch);
                pos = ltw.Position;
                var look = ltw.Forward;
                if (math.lengthsq(look) > 1e-6f)
                    fwd = look;
            }
            else if (ch.Has<Translation>(em))
            {
                pos = em.GetComponentData<Translation>(ch).Value;
            }
            else
            {
                return false;
            }

            fwd.y = 0f;
            if (math.lengthsq(fwd) < 1e-6f)
                fwd = new float3(1f, 0f, 0f);
            else
                fwd = math.normalize(fwd);

            position = new UnitVec3(pos.x, pos.y, pos.z);
            forward = new UnitVec3(fwd.x, fwd.y, fwd.z);
            return true;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Sample: rat pose: " + e.Message);
            return false;
        }
    }
#endif

    private static string SpawnAt(UnitVec3 from, UnitVec3 forward, float distance)
    {
        var at = Offset(from, forward, distance);
        var handle = UnitsApi.Spawn(PrefabName, at, _owner, _pluginId);
        Remember(handle);
        Log("spawned entity=" + handle.Entity + " pos=" + handle.Position + " distance=" + distance.ToString("0.##", CultureInfo.InvariantCulture));
        return "Spawned rat " + handle.Entity + " at " + handle.Position;
    }

    private static string Follow()
    {
        IUnitHandle? last;
        lock (Gate) last = _last;
        if (last == null || last.IsDisposed || !last.Enabled)
        {
            Log("follow no last rat");
            return "No live rat. Spawn one with .rat first.";
        }

        last.Follow(new UnitId(1, 1));
        Log("follow entity=" + last.Entity);
        return "Rat " + last.Entity + " following you.";
    }

    private static string Go(UnitVec3 from, UnitVec3 forward)
    {
        IUnitHandle? last;
        lock (Gate) last = _last;
        if (last == null || last.IsDisposed || !last.Enabled)
        {
            Log("go no last rat");
            return "No live rat. Spawn one with .rat first.";
        }

        var goal = Offset(from, forward, GoDistance);
        last.GoTo(goal);
        Log("go entity=" + last.Entity + " pos=" + goal);
        return "Rat " + last.Entity + " walking to " + goal + ".";
    }

    /// <summary>
    /// The demo the roadmap's M2.7 behavior-step item is verified by: spawn a rat on a quiet
    /// vanilla tree, then drive it entirely from the module. Nothing here compiles a
    /// behavior-tree blob.
    /// </summary>
    private static string Patrol(UnitVec3 from, UnitVec3 forward, UnitId target)
    {
        var at = Offset(from, forward, DefaultDistance);
        var handle = UnitsApi.Spawn(PrefabName, at, _owner, _pluginId, QuietTree);
        Remember(handle);
        return StartPatrol(handle, from, forward, target);
    }

    /// <summary>
    /// Park, face me, hold, walk 6 m ahead, run a callback, hold, hold again for the placeholder
    /// Animate, then follow me — every one of the seven <c>UnitStep</c> factories in one
    /// sequence. Wait is a module timer, not a behavior-tree Delay node; Animate has no
    /// server-side route on this build, so it is a documented timed no-op reported
    /// <c>Skipped</c> and the reply says so.
    ///
    /// <para><b>Why <c>Idle()</c> is step 1 and appears nowhere else.</b> <c>Play</c> forces an
    /// Idle post before the first step anyway; writing it explicitly as step 1 is the one place
    /// the sequencer accepts it without duplicating the post
    /// (<c>Units/UnitSequencer.cs:633</c> skips its own force when step 1 already is one). It is
    /// there so this example shows the factory, and it is <b>not</b> repeated between steps: an
    /// Idle request to a unit that has already walked somewhere sends the tree to <c>Return</c>
    /// and walks it home (<c>unit-movement.md</c> §20), so re-posting it before every Face or
    /// Wait would cause exactly the wandering it looks like it prevents.</para>
    ///
    /// <para><b>The <c>Then</c> step</b> runs an <c>Action</c> on the game main thread between
    /// two steps and completes on the same tick — the seam for "when it gets there, do the mod
    /// thing". The callback is a delegate written in this reloadable plugin's assembly, so it
    /// pins that assembly's collectible ALC for as long as the sequence plays; the module's owner
    /// sweep takes it back out on unload (<c>UnitStep.WithoutCallback</c>), which is why this is
    /// safe to write here and why the callback holds no state of its own.</para>
    /// </summary>
    private static string StartPatrol(IUnitHandle handle, UnitVec3 from, UnitVec3 forward, UnitId target)
    {
        var goal = Offset(from, forward, GoDistance);
        handle.Behaviour.Play(
            UnitStep.Idle(),
            UnitStep.Face(target),
            UnitStep.Wait(2f),
            UnitStep.GoTo(goal),
            UnitStep.Then(() => Log(ArrivedNote)),
            UnitStep.Wait(1f),
            UnitStep.Animate(PatrolAnimation, 1.5f),
            UnitStep.Follow(target));

        Log("patrol entity=" + handle.Entity + " tree=" + QuietTree + " goal=" + goal
            + " steps=" + handle.Behaviour.StepCount + " " + AnimationNote);
        if (!handle.Enabled)
            Log("patrol got a disabled handle (missing prefab or client side)");
        return "Rat " + handle.Entity + " patrolling: idle, face, wait, walk to " + goal
            + ", run a Then callback, hold 1.5 s (" + AnimationNote + "), follow.";
    }

    private static string Stop()
    {
        IUnitHandle? last;
        lock (Gate) last = _last;
        if (last == null || last.IsDisposed)
        {
            Log("stop no last rat");
            return "No live rat. Spawn one with .rat first.";
        }

        bool wasPlaying = last.Behaviour.IsPlaying;
        last.Behaviour.Stop();
        Log("stop entity=" + last.Entity + " wasPlaying=" + wasPlaying);
        return wasPlaying
            ? "Stopped rat " + last.Entity + "'s sequence."
            : "Rat " + last.Entity + " was not running a sequence.";
    }

    /// <summary>
    /// Stand-in follow target for the game-free path. The live command passes the caller's
    /// character entity; tests only need a non-none id.
    /// </summary>
    private static UnitId TestTarget => PatrolTestTarget;

    /// <summary>
    /// The game-free path's stand-in follow target, named so a test can give it a position: a
    /// follow target the runtime does not know is reported gone on the next poll.
    /// </summary>
    internal static UnitId PatrolTestTarget => new(1, 1);

    private static string Off()
    {
        int n;
        lock (Gate) n = _live;
        if (_owner != null)
            UnitsApi.RemoveAll(_owner);
        lock (Gate)
        {
            _last = null;
            _live = 0;
        }

        Log("off despawned " + n);
        return "Despawned " + n + " sample rat(s).";
    }

    private static void Remember(IUnitHandle handle)
    {
        lock (Gate)
        {
            _last = handle;
            if (handle.Enabled) _live++;
        }
    }

    private static UnitVec3 Offset(UnitVec3 from, UnitVec3 forward, float metres)
    {
        float fx = forward.X;
        float fy = forward.Y;
        float fz = forward.Z;
        float lenSq = (fx * fx) + (fy * fy) + (fz * fz);
        if (lenSq < 1e-6f)
        {
            fx = 1f;
            fy = 0f;
            fz = 0f;
            lenSq = 1f;
        }

        float inv = metres / MathF.Sqrt(lenSq);
        return new UnitVec3(from.X + (fx * inv), from.Y + (fy * inv), from.Z + (fz * inv));
    }

    private static void Log(string line) =>
        Plugin.Logger?.LogInfo("Sample: rat " + line);
}
