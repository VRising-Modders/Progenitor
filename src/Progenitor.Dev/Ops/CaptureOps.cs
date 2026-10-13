using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Progenitor.Ecs;
using Progenitor.Reload;
using Stunlock.Core;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;

namespace Progenitor.Dev.Ops;

/// <summary>
/// Client Dev op <c>capture</c>: watch the GAME draw its own beams and effects while the user
/// triggers them, instead of inferring the mechanism from the game's metadata.
///
/// <para><b>Why.</b> Every effects finding so far came from reading the game's metadata and then
/// guessing which of several plausible paths the client actually takes at runtime. This op
/// inverts that: take a baseline of everything
/// sequence-shaped, let the player press the button, and report what actually appeared — which
/// entity, which sequence guid, which prefab, which pooled GameObject, and whether the endpoints
/// moved afterwards.</para>
///
/// <para><b>Appeared is not enough.</b> Four live captures recorded ZERO GameObjects and zero line
/// samples while beams were on screen (evidence 135 to 137): the sequencer <b>pools</b> its
/// particle and line objects, re-activating and re-parenting clones that already exist, so a diff
/// that only looks for objects that appeared since the baseline can never see them. The op
/// therefore also runs a <b>changed</b> detector: it snapshots every object under the pool parent
/// and every <c>LineRenderer</c> / <c>HybridLineRendererComponent</c> in the scene at
/// <c>start</c>, and reports transitions on those — activated, deactivated, re-parented, line
/// positions moved, particle started or stopped — with the full description of anything that was
/// inactive at baseline and woke up.</para>
///
/// <para><b>Cost.</b> Three tiers, each with <b>its own</b> measured cost, its own amortizing
/// interval and its own floor (<see cref="CaptureBudget.PlanFrame"/>). The cached <c>Any</c> entity
/// query is tier 1; the bounded walk of <c>ParticleLifetimeSystem._ParticleParent</c> is tier 2; the
/// scene-wide sweep — two <c>FindObjectsOfType</c> calls, 5 to 30 ms in a loaded client, carrying
/// the position samples, the endpoint re-reads and the whole comparison pass — is tier 3. A tier
/// that goes over budget widens its own interval; it can never switch another tier off, and none of
/// them can be switched off entirely (R28 finding 5): each runs at least once every
/// <see cref="CaptureBudget.MinScanEveryFrames"/> / <see cref="CaptureBudget.MinWalkEveryFrames"/>
/// frames whatever the budget says, because a capture that quietly records nothing is worse than
/// one that stutters. The sweep also carries a deadline of its own
/// (<see cref="CaptureBudget.ScanDeadlineMs"/>). Comparing a tracked object is a dictionary lookup
/// and a few scalar tests; strings are formatted only when a transition is recorded, and only the
/// object's own name is read per pass, because the name is part of its identity. Frame timing uses
/// <see cref="Stopwatch.GetTimestamp"/>, so no stopwatch is allocated per frame. Every skip is
/// counted, and the report carries the skip ratio and flags itself truncated past
/// <see cref="CaptureBudget.MaxSweepSkipRatio"/> (R28 finding 7).</para>
///
/// <para><b>Ownership.</b> No Il2Cpp wrapper is kept between frames: GameObjects are remembered by
/// instance id and name, entities by index:version, and the objects are re-found during the next
/// sweep (docs/modules.md collectible-ALC rules). The capture stops on plugin unload.</para>
///
/// Read paths reused, with their sources:
/// <list type="bullet">
/// <item><c>ParticleLifetimeSystem._ParticleParent</c> is the sequencer's pool parent
/// (<c>ParticlePool.ReturnInstance</c> parks clones there) — the
/// same handle <c>Effects/SequenceDriverLookup.TryGetParticleParent</c> takes.</item>
/// <item><c>SpawnSequenceForEntity</c>: <c>SequenceGuid</c> @0x0,
/// <c>Target</c> @0x4, <c>SecondaryTarget</c> @0x10, <c>SequenceInstance</c> @0x24.</item>
/// <item><c>Stunlock.Sequencer.SequenceInstance</c>: <c>Target</c> @0x8,
/// <c>SecondaryTarget</c> @0x10 — no ParticleGO field, which is why the GameObject side is a
/// separate walk.</item>
/// <item><c>ProfessorCoilBeam_State_Shared</c>: <c>Start</c> @0x0, <c>End</c> @0xC
/// — the only networked two-point beam.</item>
/// <item><c>PreviewPlacementSequence</c> carries the build-mode
/// <c>SequenceState</c> + <c>ValidPlacement</c> — the invalid-placement red beams.</item>
/// <item><c>LineRendererPositionElement</c> (stride 20) is the buffer
/// <c>UpdateLineRendererPositionElement_Job</c> pumps into
/// <c>HybridLineRendererComponent.Positions</c>.</item>
/// </list>
/// </summary>
internal static unsafe class CaptureOps
{
    /// <summary>
    /// Components that make an entity interesting. Resolved once per capture; anything this build
    /// does not have is reported in <c>types.missing</c> rather than failing the op, so a game
    /// update that renames one type degrades instead of breaking the tool.
    /// </summary>
    private static readonly string[] InterestTypeNames =
    {
        "ProjectM.Sequencer.SpawnSequenceForEntity",
        "Stunlock.Sequencer.SequenceInstance",
        "ProjectM.SequenceGUID",
        "ProjectM.BuffSequence",
        "ProjectM.Sequencer.BuffSequenceSpawned",
        "ProjectM.Sequencer.LineRendererPositionElement",
        "ProjectM.PreviewPlacementSequence",
        "ProjectM.PreviewPlacementBuff",
        "ProjectM.ProfessorCoilBeam_Client",
        "ProjectM.ProfessorCoilBeam_State_Shared",
    };

    /// <summary>
    /// The subset whose fields are dumped on appearance and re-read on sample frames. These are the
    /// components that carry endpoints (or the guid that leads to them); dumping every interest
    /// component every sample frame would cost more than it tells.
    /// </summary>
    private static readonly string[] EndpointTypeNames =
    {
        "ProjectM.Sequencer.SpawnSequenceForEntity",
        "Stunlock.Sequencer.SequenceInstance",
        "ProjectM.ProfessorCoilBeam_State_Shared",
        "ProjectM.PreviewPlacementSequence",
        "ProjectM.Sequencer.LineRendererPositionElement",
        "Unity.Transforms.Translation",
    };

    /// <summary>Elements of a buffer component read into the report (both ends plus a few spare).</summary>
    private const int MaxBufferElements = 8;

    /// <summary>
    /// Cap on the scene-wide LineRenderer scan so a pathological scene cannot stall a frame. What
    /// is kept is now the lowest 400 instance ids, not the first 400 in <c>FindObjectsOfType</c>
    /// order (<see cref="CaptureScanSelection"/>, R28 finding 9).
    /// </summary>
    private const int MaxScanObjects = 400;

    /// <summary>Positions read off one LineRenderer / hybrid list.</summary>
    private const int MaxPositionsPerLine = 8;

    private static readonly object Gate = new();

    private static bool _running;
    private static bool _frameHooked;
    private static CaptureReport? _live;
    private static string? _finishedPath;
    private static Dictionary<string, object?>? _finishedSummary;

    private static Stopwatch? _clock;
    private static double _seconds;
    private static string _worldName = "";
    private static string[] _filter = Array.Empty<string>();
    private static int _maxEntities;
    private static int _frameIndex;

    private static readonly HashSet<Entity> SeenEntities = new();
    private static readonly HashSet<Entity> FrameEntities = new();

    /// <summary>
    /// Instance id → the name it had when it was first seen. The name is half the key on purpose:
    /// Unity recycles instance ids, and keying on the bare int made the op skip a genuinely new
    /// object and feed its positions into a dead object's sample series (R27 finding 22).
    /// </summary>
    private static readonly Dictionary<int, string> SeenObjects = new();

    private static readonly List<Entity> Tracked = new();
    private static readonly List<CaptureVec3> ScratchPositions = new();
    private static readonly List<TypeSlot> Slots = new();
    private static readonly List<string> MissingTypes = new();

    /// <summary>
    /// The one snapshot buffer the "changed" detector fills per object per pass. Reused, never
    /// reallocated, and never holds an Il2Cpp wrapper — it is plain fields the live read copies in.
    /// </summary>
    private static readonly CaptureObjectState Scratch = new();

    /// <summary>
    /// Children of the sequencer's pool parent this pass and last pass. The pool parks IDLE clones
    /// and reparents a LIVE one out, so an id that LEFT this set is a clone the sequencer just took
    /// — the strongest pooled-spawn signal there is, and invisible to any "appeared" diff.
    /// </summary>
    private static readonly HashSet<int> PoolChildren = new();
    private static readonly HashSet<int> PoolChildrenPrev = new();
    private static bool _poolPassTaken;

    /// <summary>Node accounting for one pool walk: separate budgets for the root children and the rest.</summary>
    private static readonly CaptureWalkBudget WalkBudget = new();

    /// <summary>Reused sort buffers for the stable entity subset (R27 finding 9).</summary>
    private static long[] _entityKeys = Array.Empty<long>();
    private static Entity[] _entityValues = Array.Empty<Entity>();

    /// <summary>Reused sort buffers for the stable GameObject subset (R28 finding 9).</summary>
    private static int[] _scanIds = Array.Empty<int>();
    private static int[] _scanSlots = Array.Empty<int>();
    private static bool _scanOverflowLogged;
    private static bool _scanDeadlineLogged;

    // The query belongs to the world it was built in. After that world is destroyed (leave to the
    // title mid-window) it must be neither enumerated nor disposed: both fault natively
    // (Core swap crash investigation, 2026-10-02).
    private static EntityQuery _query;
    private static bool _queryCreated;
    private static World? _queryWorld;
    private static ulong? _querySeq;
    private static ComponentType _prefabGuidType;
    private static bool _prefabGuidResolved;

    // ---- op entry ---------------------------------------------------------------------------

    /// <summary>
    /// <c>{"action":"start"|"stop"|"status"}</c>. Main thread, client side only. <c>start</c> takes
    /// the baseline and returns immediately; the window then runs on <c>Plugin.Frame</c> until
    /// <c>seconds</c> elapse or <c>stop</c> arrives, so a window longer than the bridge's 30 s
    /// socket timeout is fine.
    /// </summary>
    internal static object Run(JsonElement args)
    {
        if (Progenitor.Plugin.IsServerProcess)
            throw new InvalidOperationException("capture runs on the client side only; the server has no sequencer or GameObjects to watch");

        return CaptureArgs.ParseAction(args) switch
        {
            "start" => Start(args),
            "stop" => Stop("stop"),
            _ => Status(),
        };
    }

    /// <summary>Dev plugin unload: stop the window and write whatever was collected.</summary>
    internal static void Shutdown()
    {
        try
        {
            lock (Gate)
            {
                if (!_running) { UnhookFrame(); ReleaseQuery(); return; }
            }
            Stop("unload");
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning($"capture Shutdown: {e.Message}");
            try { UnhookFrame(); ReleaseQuery(); } catch { }
            lock (Gate) { _running = false; _live = null; }
        }
    }

    private static object Start(JsonElement args)
    {
        lock (Gate)
        {
            if (_running)
                throw new InvalidOperationException("a capture is already running; call capture {\"action\":\"stop\"} first");
        }

        var world = Progenitor.Worlds.Client;
        if (world == null || !world.IsCreated)
            throw new InvalidOperationException("no client world yet; join a game before starting a capture");

        double seconds = CaptureArgs.ClampSeconds(ReadDouble(args, "seconds", CaptureArgs.DefaultSeconds));
        int maxEntities = CaptureArgs.ClampMaxEntities(ReadInt(args, "maxEntities", CaptureArgs.DefaultMaxEntities));
        string filterText = ReadString(args, "filter") ?? CaptureArgs.DefaultFilter;

        ResetState();
        _filter = CaptureArgs.ParseFilter(filterText);
        _maxEntities = maxEntities;
        _seconds = seconds;
        _worldName = world.Name;

        var report = new CaptureReport
        {
            Side = "client",
            World = world.Name,
            StartedUtc = DateTime.UtcNow,
            RequestedSeconds = seconds,
            Filter = filterText,
            MaxEntities = maxEntities,
            Budget = new CaptureBudget(),
        };

        ResolveSlots();
        CreateQuery(world);

        int baselineEntities = BaselineEntities(world);
        int baselineObjects = BaselineObjects(world, report);
        report.BaselineEntities = baselineEntities;
        report.BaselineObjects = baselineObjects;

        lock (Gate)
        {
            _live = report;
            _running = true;
            _frameIndex = 0;
            _clock = Stopwatch.StartNew();
        }

        HookFrame();
        Plugin.Logger?.LogInfo(
            $"capture: started for {seconds:0.#}s, baseline entities={baselineEntities} gameObjects={baselineObjects}, filter='{filterText}'");

        var resolved = new List<string>(Slots.Count);
        foreach (var slot in Slots) resolved.Add(slot.FullName);

        return new Dictionary<string, object?>
        {
            ["running"] = true,
            ["world"] = world.Name,
            ["startedUtc"] = report.StartedUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
            ["seconds"] = seconds,
            ["filter"] = filterText,
            ["maxEntities"] = maxEntities,
            ["baseline"] = new Dictionary<string, object?>
            {
                ["entities"] = baselineEntities,
                ["gameObjects"] = baselineObjects,
                ["trackedObjects"] = report.Tracker.Tracked,
                ["droppedObjects"] = report.Tracker.Dropped,
            },
            ["types"] = new Dictionary<string, object?>
            {
                ["resolved"] = resolved,
                ["missing"] = new List<string>(MissingTypes),
            },
            ["note"] = "trigger the effect now, then call capture {\"action\":\"stop\"} (or wait for the window to close and call stop to read the report)",
        };
    }

    private static object Status()
    {
        lock (Gate)
        {
            var live = _live;
            var result = new Dictionary<string, object?>
            {
                ["running"] = _running,
                ["elapsedSeconds"] = _clock == null ? 0d : CaptureBudget.Round(_clock.Elapsed.TotalSeconds),
                ["remainingSeconds"] = _running && _clock != null
                    ? CaptureBudget.Round(Math.Max(0, _seconds - _clock.Elapsed.TotalSeconds))
                    : 0d,
                ["frames"] = live?.Frames ?? 0,
            };

            if (live != null)
            {
                result["counts"] = new Dictionary<string, object?>
                {
                    ["entitiesAppeared"] = live.Appeared.Count,
                    ["entitiesDisappeared"] = live.Disappeared.Count,
                    ["gameObjectsAppeared"] = live.Objects.Count,
                    ["gameObjectChanges"] = live.Changes.Count,
                    ["trackedObjects"] = live.Tracker.Tracked,
                    ["lineSeries"] = live.Series.Count,
                    ["endpointTracks"] = live.Endpoints.Count,
                    ["skippedGameObjectWalks"] = live.Budget.SkippedWalks,
                    ["skippedSceneScans"] = live.Budget.SkippedScans,
                    ["skippedComparisons"] = live.Budget.SkippedComparisons,
                    ["skippedEntityDiffs"] = live.Budget.SkippedEntityDiffs,
                    ["sampleInterval"] = live.Budget.SampleInterval,
                    ["entityDiffInterval"] = live.Budget.EntityDiffInterval,
                    ["walkInterval"] = live.Budget.WalkInterval,
                    ["sweepSkipRatio"] = CaptureBudget.Round(live.Budget.SweepSkipRatio),
                    ["samplingSaturated"] = live.Sampled.Saturated || live.SeriesSaturated,
                    ["truncated"] = live.Truncated,
                };
            }

            if (_finishedPath != null)
            {
                result["lastReport"] = new Dictionary<string, object?>
                {
                    ["path"] = _finishedPath,
                    ["summary"] = _finishedSummary,
                };
            }

            return result;
        }
    }

    /// <summary>
    /// Closes the window, writes the JSON report, and returns its path plus the summary. Calling
    /// <c>stop</c> after the window already closed itself returns that finished report instead of
    /// failing, so a caller that missed the deadline still gets the data.
    /// </summary>
    private static object Stop(string reason)
    {
        CaptureReport? report;
        lock (Gate)
        {
            if (!_running)
            {
                if (_finishedPath == null)
                    throw new InvalidOperationException("no capture is running and none has finished in this session");
                return new Dictionary<string, object?>
                {
                    ["running"] = false,
                    ["path"] = _finishedPath,
                    ["summary"] = _finishedSummary,
                    ["note"] = "the window had already closed; this is the report it wrote",
                };
            }

            report = _live;
            _running = false;
            if (report != null)
            {
                report.ElapsedSeconds = _clock?.Elapsed.TotalSeconds ?? 0;
                report.StopReason = reason;
            }
            _clock?.Stop();
        }

        UnhookFrame();
        ReleaseQuery();

        if (report == null)
            throw new InvalidOperationException("capture had no report to write");

        // Summary first, and the write is allowed to fail. R28 finding 28.20: WriteReport rethrew
        // after _running had already been cleared, so a full disk lost the whole capture and the
        // next `stop` said no capture was running.
        var summary = report.Summary();
        string? path = null;
        string? writeError = null;
        try { path = WriteReport(report); }
        catch (Exception e) { writeError = e.Message; }

        lock (Gate)
        {
            _finishedPath = path;
            _finishedSummary = summary;
            _live = null;
        }

        Plugin.Logger?.LogInfo(
            $"capture: stopped ({reason}) after {report.Frames} frames; entities +{report.Appeared.Count}/-{report.Disappeared.Count}, "
            + $"gameObjects {report.Objects.Count}, changes {report.Changes.Count} on {report.Tracker.Tracked} tracked, "
            + $"skipped walks/scans {report.Budget.SkippedWalks}/{report.Budget.SkippedScans} "
            + $"({CaptureBudget.Round(report.Budget.SweepSkipRatio * 100)}% of due sweeps), "
            + $"scans {report.Budget.Scans} avg {CaptureBudget.Round(report.Budget.AverageScanMs)} ms "
            + $"(interval {report.Budget.SampleInterval}); truncated={report.Truncated}; "
            + $"report {path ?? "NOT WRITTEN: " + writeError}");

        return new Dictionary<string, object?>
        {
            ["running"] = false,
            ["path"] = path,
            ["writeError"] = writeError,
            ["summary"] = summary,
        };
    }

    // ---- frame ------------------------------------------------------------------------------

    private static void HookFrame()
    {
        if (_frameHooked) return;
        Progenitor.Plugin.Frame += OnFrame;
        _frameHooked = true;
    }

    private static void UnhookFrame()
    {
        if (!_frameHooked) return;
        Progenitor.Plugin.Frame -= OnFrame;
        _frameHooked = false;
    }

    /// <summary>
    /// One frame of the window. <c>Plugin.Frame</c> runs from <c>FrameTicker</c>, after the
    /// bridge Drain and after the presentation groups of the previous frame, so what this reads is
    /// the state the sequencer left behind — which is exactly what the report is about.
    /// </summary>
    private static void OnFrame()
    {
        CaptureReport? report;
        Stopwatch? clock;
        lock (Gate)
        {
            if (!_running) return;
            report = _live;
            clock = _clock;
        }
        if (report == null || clock == null) return;

        if (clock.Elapsed.TotalSeconds >= _seconds)
        {
            try { Stop("deadline"); }
            catch (Exception e) { Plugin.Logger?.LogWarning($"capture deadline stop: {e.Message}"); }
            return;
        }

        var world = Progenitor.Worlds.Client;
        if (world == null || !world.IsCreated || world.Name != _worldName)
        {
            try { Stop("world-gone"); }
            catch (Exception e) { Plugin.Logger?.LogWarning($"capture world-gone stop: {e.Message}"); }
            return;
        }

        int frameIndex = _frameIndex++;
        double atSeconds = clock.Elapsed.TotalSeconds;
        long frameStart = Stopwatch.GetTimestamp();

        // Every tier is planned before any work happens, from its OWN measured cost and its own
        // floor. R28 finding 5: tier 1 used to run ungated and its cost was then charged against
        // the gate for tiers 2 and 3, so a crowded area — where tier 1 is expensive — switched off
        // exactly the two tiers the op exists for, and nothing in the report said so. Tier 1 now
        // degrades to every Nth frame instead, and the pool walk and the scene sweep have separate
        // budgets with a hard floor under each.
        var plan = report.Budget.PlanFrame(frameIndex);

        try
        {
            double entityMs = 0;
            if (plan.RunEntityDiff)
            {
                long entityStart = Stopwatch.GetTimestamp();
                DiffEntities(world, report, frameIndex, atSeconds);
                entityMs = MsSince(entityStart);
                report.Budget.RecordEntityDiff(entityMs);
            }

            // Deliberately excludes the entity diff: charging tier 1 to the tiers below it is the
            // defect. What is left is the frame's own overhead before the walk.
            double cheapMs = Math.Max(0, MsSince(frameStart) - entityMs);
            plan = report.Budget.Confirm(plan, cheapMs);

            // Tier 2, cheap and bounded: the pool-parent walk. It also compares on a sweep frame,
            // because the pooled clones live exactly there.
            if (plan.RunWalk)
            {
                long walkStart = Stopwatch.GetTimestamp();
                WalkParticleParent(world, report, frameIndex, atSeconds, plan.RunScan);
                report.Budget.RecordWalk(MsSince(walkStart));
            }

            // Tier 3, expensive: the scene sweep and everything that rides on it.
            if (plan.RunScan)
            {
                report.Tracker.BeginPass();
                long scanStart = Stopwatch.GetTimestamp();
                try
                {
                    ScanLines(report, frameIndex, atSeconds, scanStart);
                    ScanHybrids(report, frameIndex, atSeconds, scanStart);
                    TrackEndpoints(world, report);
                }
                finally
                {
                    // R28 finding 9 (related): when a scan threw, the old code skipped both the
                    // pool diff and the set swap, so the NEXT sweep diffed against a two-passes-old
                    // set and reported every intervening pool move as a spawn.
                    DiffPoolChildren(report, frameIndex, atSeconds);
                    report.Tracker.Sweep(frameIndex, report.Budget.SampleInterval, report: report);
                    report.Budget.RecordScan(MsSince(scanStart));
                }
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning($"capture frame {frameIndex}: {e.Message}");
        }
        finally
        {
            report.Budget.Record(MsSince(frameStart), plan);
        }
    }

    /// <summary>Milliseconds since a <see cref="Stopwatch.GetTimestamp"/> mark, without allocating a stopwatch.</summary>
    private static double MsSince(long startTimestamp) =>
        (Stopwatch.GetTimestamp() - startTimestamp) * 1000.0 / Stopwatch.Frequency;

    // ---- entities ---------------------------------------------------------------------------

    private static void DiffEntities(World world, CaptureReport report, int frameIndex, double atSeconds)
    {
        if (!QueryUsableFor(world)) { ReleaseQuery(); return; }
        var em = world.EntityManager;

        FrameEntities.Clear();
        NativeArray<Entity> entities = default;
        bool have = false;
        int available = 0;
        try
        {
            entities = _query.ToEntityArray(Allocator.Temp);
            have = true;
            available = entities.Length;
            int keep = CaptureEntitySelection.Keep(available, _maxEntities);
            int overflow = CaptureEntitySelection.Overflow(available, _maxEntities);

            if (overflow > 0)
            {
                // Chunk order moves every frame, so clipping it made the kept set churn and the
                // diff invented an appear/disappear storm that burned both row caps within a few
                // frames (R27 finding 9). Sorting by entity index gives a subset that is stable as
                // long as the world's low indices are.
                EnsureSortCapacity(available);
                for (int i = 0; i < available; i++)
                {
                    var e = entities[i];
                    _entityKeys[i] = CaptureEntitySelection.Key(e.Index, e.Version);
                    _entityValues[i] = e;
                }

                Array.Sort(_entityKeys, _entityValues, 0, available);
                for (int i = 0; i < keep; i++) FrameEntities.Add(_entityValues[i]);
                report.NoteEntityOverflow(overflow);
            }
            else
            {
                for (int i = 0; i < keep; i++) FrameEntities.Add(entities[i]);
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning($"capture entity query: {e.Message}");
            return;
        }
        finally
        {
            if (have) entities.Dispose();
        }

        foreach (var entity in FrameEntities)
        {
            if (SeenEntities.Contains(entity)) continue;
            SeenEntities.Add(entity);
            var row = DescribeEntity(world, em, entity, frameIndex, atSeconds);
            if (row == null) continue;
            if (report.TryAddAppeared(row) && Tracked.Count < CaptureArgs.MaxTrackedEntities)
                Tracked.Add(entity);
        }

        // A truncated frame saw only part of the match set, so it cannot tell "gone" from "did not
        // fit" and must not emit removal diffs at all (R27 finding 9).
        if (CaptureEntitySelection.ShouldSkipDisappearance(available, _maxEntities)) return;

        // Disappearance: an entity we saw is no longer in the match set (destroyed, or the
        // sequencer removed the component). Both are worth reporting; Exists tells them apart.
        List<Entity>? gone = null;
        foreach (var entity in SeenEntities)
        {
            if (FrameEntities.Contains(entity)) continue;
            (gone ??= new List<Entity>()).Add(entity);
        }

        if (gone == null) return;
        for (int i = 0; i < gone.Count; i++)
        {
            var entity = gone[i];
            SeenEntities.Remove(entity);
            Tracked.Remove(entity);
            bool exists = false;
            try { exists = em.Exists(entity); } catch { }
            var row = new CaptureEntityRow
            {
                Entity = FormatEntity(entity),
                Frame = frameIndex,
                AtSeconds = atSeconds,
            };
            row.Components.Add(exists ? "<still exists, left the match set>" : "<destroyed>");
            report.TryAddDisappeared(row);
        }
    }

    private static CaptureEntityRow? DescribeEntity(World world, EntityManager em, Entity entity, int frameIndex, double atSeconds)
    {
        try
        {
            var row = new CaptureEntityRow
            {
                Entity = FormatEntity(entity),
                Frame = frameIndex,
                AtSeconds = atSeconds,
            };

            var types = em.GetComponentTypes(entity, Allocator.Temp);
            try
            {
                for (int i = 0; i < types.Length; i++)
                {
                    string name;
                    try { name = types[i].GetManagedType()?.Name ?? types[i].ToString(); }
                    catch { name = "<unresolved>"; }
                    row.Components.Add(name);
                }
            }
            finally { types.Dispose(); }

            if (TryPrefabGuid(em, entity, out int hash) && hash != 0)
            {
                row.PrefabHash = hash;
                try { row.PrefabName = PrefabLookup.TryGetName(world, new PrefabGUID(hash), out var n) ? n : null; }
                catch { row.PrefabName = null; }
            }

            foreach (var slot in Slots)
            {
                if (!slot.IsEndpoint) continue;
                var fields = ReadComponent(em, entity, slot, row.SequenceGuids);
                if (fields != null) row.Endpoints[slot.SimpleName] = fields;
            }

            return row;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning($"capture describe {FormatEntity(entity)}: {e.Message}");
            return null;
        }
    }

    /// <summary>
    /// Re-reads the endpoint fields of the tracked entities and folds each value into its
    /// <see cref="CaptureEndpointTrack"/>. This is what answers "do the endpoints move, and which
    /// value do they follow" without keeping a per-frame history of every field.
    /// </summary>
    private static void TrackEndpoints(World world, CaptureReport report)
    {
        if (Tracked.Count == 0) return;
        var em = world.EntityManager;
        for (int i = 0; i < Tracked.Count; i++)
        {
            var entity = Tracked[i];
            bool exists;
            try { exists = em.Exists(entity); } catch { continue; }
            if (!exists) continue;
            string label = FormatEntity(entity);

            foreach (var slot in Slots)
            {
                if (!slot.IsEndpoint) continue;
                var fields = ReadComponent(em, entity, slot, null);
                if (fields == null) continue;
                foreach (var pair in fields)
                {
                    var track = report.TrackFor(label, slot.SimpleName, pair.Key);
                    track?.Observe(FormatValue(pair.Value));
                }
            }
        }
    }

    // ---- component reading ------------------------------------------------------------------

    /// <summary>
    /// Field dump of one component (or the first <see cref="MaxBufferElements"/> buffer elements)
    /// through the raw pointer, the same walk <c>get</c> uses. <paramref name="sequenceGuids"/>,
    /// when given, collects every non-zero <c>SequenceGUID</c> hash found on the way.
    /// </summary>
    private static Dictionary<string, object?>? ReadComponent(
        EntityManager em, Entity entity, TypeSlot slot, List<int>? sequenceGuids)
    {
        try
        {
            if (!em.HasComponent(entity, slot.Type)) return null;
            if (slot.Fields.Length == 0 || slot.Size <= 0) return null;

            var result = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (slot.IsBuffer)
            {
                void* raw = em.GetBufferRawRO(entity, slot.Index);
                if (raw == null) return null;
                int length = em.GetBufferLength(entity, slot.Index);
                result["length"] = length;
                int take = Math.Min(length, MaxBufferElements);
                for (int e = 0; e < take; e++)
                {
                    byte* element = (byte*)raw + (e * slot.Size);
                    foreach (var spec in slot.Fields)
                        result[$"[{e}].{spec.Name}"] = ReadFieldValue(element, spec, sequenceGuids);
                }
                return result;
            }

            byte* blob = (byte*)em.GetComponentDataRawRO(entity, slot.Index);
            if (blob == null) return null;
            foreach (var spec in slot.Fields)
                result[spec.Name] = ReadFieldValue(blob, spec, sequenceGuids);
            return result;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning($"capture read {slot.SimpleName} on {FormatEntity(entity)}: {e.Message}");
            return null;
        }
    }

    /// <summary>
    /// <see cref="ComponentDump.ReadField"/> plus the three interop structs it classifies as
    /// Unknown but that carry everything this op is looking for: <c>SequenceGUID</c> (one int),
    /// <c>SequenceState</c> (one Entity), and
    /// <c>NetworkedEntity</c> (Entity @0x0, <c>_WaitingForSync</c> @0x8).
    /// </summary>
    private static object? ReadFieldValue(byte* blob, ComponentDump.FieldSpec spec, List<int>? sequenceGuids)
    {
        byte* p = blob + spec.Offset;
        string typeName = spec.ClrType.Name;
        try
        {
            if (typeName == "SequenceGUID")
            {
                int hash = *(int*)p;
                if (hash != 0 && sequenceGuids != null && !sequenceGuids.Contains(hash)) sequenceGuids.Add(hash);
                return new Dictionary<string, object?> { ["sequenceGuid"] = hash };
            }

            if (typeName == "SequenceState")
                return new Dictionary<string, object?> { ["index"] = *(int*)p, ["version"] = *(int*)(p + 4) };

            if (typeName == "NetworkedEntity")
                return new Dictionary<string, object?>
                {
                    ["index"] = *(int*)p,
                    ["version"] = *(int*)(p + 4),
                    ["waitingForSync"] = *(p + 8) != 0,
                };
        }
        catch (Exception e)
        {
            return new Dictionary<string, object?> { ["error"] = e.Message };
        }

        return ComponentDump.ReadField(blob, spec);
    }

    /// <summary>Stable text for the endpoint tracks. Dictionaries are flattened key=value, in order.</summary>
    internal static string FormatValue(object? value)
    {
        switch (value)
        {
            case null:
                return "null";
            case float f:
                return f.ToString("0.###", CultureInfo.InvariantCulture);
            case double d:
                return d.ToString("0.###", CultureInfo.InvariantCulture);
            case string s:
                return s;
            case bool b:
                return b ? "true" : "false";
            case Dictionary<string, object?> map:
            {
                var sb = new System.Text.StringBuilder("{");
                bool first = true;
                foreach (var pair in map)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append(pair.Key).Append('=').Append(FormatValue(pair.Value));
                }
                return sb.Append('}').ToString();
            }
            default:
                return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
        }
    }

    // ---- GameObjects ------------------------------------------------------------------------

    /// <summary>
    /// The sequencer's pool parent. Walking it is cheap, and the direction of the
    /// signal is the opposite of what the first version of this op assumed (R27 finding 22):
    /// <c>ParticlePool.ReturnInstance</c> parks every <b>idle</b> clone here and a
    /// <b>live</b> clone is reparented out, so a child that LEAVES this set is a spawn and a child
    /// that ARRIVES is a despawn. A brand-new child only ever means the pool had to grow — which is
    /// why the first trigger of a cold effect recorded something and the second, warm one recorded
    /// nothing at all. <see cref="DiffPoolChildren"/> is the detector that reads it the right way.
    /// </summary>
    private static Transform? ParticleParent(World world)
    {
        try
        {
            var sys = world.GetExistingSystemManaged<ProjectM.Sequencer.ParticleLifetimeSystem>();
            if (sys == null) return null;
            var go = sys._ParticleParent;
            if (go == null) return null;
            return go.transform;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning($"capture particle parent: {e.Message}");
            return null;
        }
    }

    private static void WalkParticleParent(World world, CaptureReport report, int frameIndex, double atSeconds, bool compare)
    {
        var parent = ParticleParent(world);
        if (parent == null) return;
        WalkBudget.Reset();
        if (compare) PoolChildren.Clear();
        Walk(parent, 0, report, frameIndex, atSeconds, compare);
        if (compare && !WalkBudget.Complete) report.NoteIncompleteWalk();
    }

    /// <summary>
    /// Depth-first walk of the pool parent, with a budget for the direct children separate from the
    /// budget for everything below them (R28 finding 9). One shared budget meant which direct
    /// children got visited depended on how many grandchildren the earlier siblings happened to
    /// have, so a parked clone slid past the cap and was reported as taken from the pool.
    /// </summary>
    private static void Walk(
        Transform t, int depth, CaptureReport report, int frameIndex, double atSeconds, bool compare)
    {
        if (!WalkBudget.CanDescend(depth) && depth > 0) return;
        int count;
        try { count = t.childCount; } catch { return; }
        for (int i = 0; i < count; i++)
        {
            if (!WalkBudget.TryVisit(depth)) return;
            Transform? child;
            try { child = t.GetChild(i); } catch { continue; }
            if (child == null) continue;
            GameObject? go;
            try { go = child.gameObject; } catch { continue; }
            if (go == null) continue;
            int id = VisitObject(go, "particleParent", report, frameIndex, atSeconds, compare);
            if (compare && depth == 0 && id != 0) PoolChildren.Add(id);
            if (WalkBudget.CanDescend(depth)) Walk(child, depth + 1, report, frameIndex, atSeconds, compare);
        }
    }

    /// <summary>
    /// Direct children of the pool parent that LEFT since the previous pass: the sequencer took
    /// them out of the pool, which is the pooled-clone spawn the "appeared" diff structurally
    /// cannot see (evidence 137). Objects the scene sweep already compared this pass are skipped,
    /// because <see cref="CaptureObjectTracker.Observe"/> recorded the same move with real parent
    /// names.
    /// </summary>
    private static void DiffPoolChildren(CaptureReport report, int frameIndex, double atSeconds)
    {
        if (!_poolPassTaken)
        {
            SwapPoolSets();
            _poolPassTaken = true;
            return;
        }

        // An incomplete walk did not see the whole child set, so an id that is "missing" may simply
        // not have been visited. Diffing that pass manufactures a spawn on the strongest signal the
        // op reports (R28 finding 9); take the pass as the new baseline instead.
        if (!WalkBudget.Complete)
        {
            SwapPoolSets();
            return;
        }

        foreach (var id in PoolChildrenPrev)
        {
            if (PoolChildren.Contains(id)) continue;
            if (report.Tracker.WasComparedThisPass(id, frameIndex)) continue;
            report.Tracker.NoteParentChange(
                id, frameIndex, atSeconds, "<sequencer pool parent>", "<taken from the pool>", report);
        }

        SwapPoolSets();
    }

    private static void SwapPoolSets()
    {
        PoolChildrenPrev.Clear();
        foreach (var id in PoolChildren) PoolChildrenPrev.Add(id);
    }

    /// <summary>
    /// Scene-wide <c>LineRenderer</c> pass, sweep frames only. It does triple duty: it finds new
    /// lines that are NOT under the particle parent (the build-mode preview beams hang off the
    /// blueprint ghost), it re-finds the tracked ones so the "changed" detector can compare them,
    /// and it takes the throttled position samples — so the capture never has to keep a
    /// <c>LineRenderer</c> wrapper between frames.
    /// </summary>
    private static void ScanLines(CaptureReport report, int frameIndex, double atSeconds, long scanStart)
    {
        try
        {
            var type = Il2CppType.Of<LineRenderer>();
            if (type == null) return;
            var all = UnityEngine.Object.FindObjectsOfType(type, true);
            if (all == null) return;
            NoteScanOverflow(report, all.Length, "LineRenderer");
            int limit = SortByInstanceId(all, MaxScanObjects);
            for (int i = 0; i < limit; i++)
            {
                if (ScanDeadlinePassed(report, scanStart, "LineRenderer")) return;
                LineRenderer? lr;
                try
                {
                    var raw = all[_scanSlots[i]];
                    lr = raw == null ? null : raw.TryCast<LineRenderer>();
                }
                catch { continue; }
                if (lr == null) continue;
                GameObject? go;
                try { go = lr.gameObject; } catch { continue; }
                if (go == null) continue;

                int id = VisitObject(go, "lineRenderer", report, frameIndex, atSeconds, compare: true);
                if (id == 0) continue;

                if (!report.Sampled.TryGet(id, out var sampledName)) continue;
                report.Sampled.Touch(id, frameIndex);
                var series = report.SeriesFor(id, sampledName);
                if (series == null) continue;
                ReadLinePositions(lr, ScratchPositions);
                series.TryAdd(frameIndex, atSeconds, ScratchPositions);
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning($"capture line scan: {e.Message}");
        }
    }

    /// <summary>
    /// Reads every found object's instance id into the reusable buffers, sorts by it, and returns
    /// how many the sweep keeps.
    ///
    /// <para>R28 finding 9: the sweeps used to clip raw <c>FindObjectsOfType</c> order at the cap.
    /// That order is unspecified and shifts as objects are created and destroyed, so an object
    /// drifted out of the compare set, was swept as gone, came back, and its next activation was
    /// reported as a fresh first activation with a full description read — hundreds of phantom rows
    /// that burned the 400-row cap in seconds. The entity side has sorted by a unique key since R27;
    /// this is the same rule on the object side.</para>
    /// </summary>
    private static int SortByInstanceId(Il2CppArrayBase<UnityEngine.Object> all, int max)
    {
        int count = all.Length;
        if (count <= 0) return 0;
        EnsureScanCapacity(count);
        int usable = 0;
        for (int i = 0; i < count; i++)
        {
            int id;
            try
            {
                var raw = all[i];
                if (raw == null) continue;
                id = raw.GetInstanceID();
            }
            catch { continue; }
            if (id == 0) continue;
            _scanIds[usable] = id;
            _scanSlots[usable] = i;
            usable++;
        }

        return CaptureScanSelection.SortAndClip(_scanIds, _scanSlots, usable, max);
    }

    private static void EnsureScanCapacity(int needed)
    {
        if (_scanIds.Length >= needed) return;
        int size = Math.Max(needed, Math.Max(512, _scanIds.Length * 2));
        _scanIds = new int[size];
        _scanSlots = new int[size];
    }

    /// <summary>
    /// True when this sweep has spent its own deadline and must stop early. Without it a sweep ran
    /// to completion however long it took, which is an uninterruptible hitch (R28 finding 28.22).
    /// </summary>
    private static bool ScanDeadlinePassed(CaptureReport report, long scanStart, string what)
    {
        if (MsSince(scanStart) < report.Budget.ScanDeadlineMs) return false;
        report.NoteScanDeadline();
        if (!_scanDeadlineLogged)
        {
            _scanDeadlineLogged = true;
            Plugin.Logger?.LogWarning(
                $"capture: the {what} sweep passed its {CaptureBudget.Round(report.Budget.ScanDeadlineMs)} ms deadline "
                + "and stopped early; the report is marked truncated");
        }

        return true;
    }

    /// <summary>
    /// The same pass for <c>HybridLineRendererComponent</c>. A hybrid line whose GameObject carries
    /// no <c>LineRenderer</c> would otherwise be baselined and then never re-found, so its
    /// activation — the pooled case — would never be reported.
    /// </summary>
    private static void ScanHybrids(CaptureReport report, int frameIndex, double atSeconds, long scanStart)
    {
        try
        {
            var type = Il2CppType.Of<HybridLineRendererComponent>();
            if (type == null) return;
            var all = UnityEngine.Object.FindObjectsOfType(type, true);
            if (all == null) return;
            NoteScanOverflow(report, all.Length, "HybridLineRendererComponent");
            int limit = SortByInstanceId(all, MaxScanObjects);
            for (int i = 0; i < limit; i++)
            {
                if (ScanDeadlinePassed(report, scanStart, "HybridLineRendererComponent")) return;
                Component? component;
                try
                {
                    var raw = all[_scanSlots[i]];
                    component = raw == null ? null : raw.TryCast<Component>();
                }
                catch { continue; }
                if (component == null) continue;
                GameObject? go;
                try { go = component.gameObject; } catch { continue; }
                if (go == null) continue;
                VisitObject(go, "hybrid", report, frameIndex, atSeconds, compare: true);
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning($"capture hybrid scan: {e.Message}");
        }
    }

    /// <summary>
    /// A sweep that stopped at <see cref="MaxScanObjects"/> silently stopped discovering and
    /// sampling everything past that index (R27 finding 22). Say so in the report, and log once.
    /// </summary>
    private static void NoteScanOverflow(CaptureReport report, int found, string what)
    {
        if (found <= MaxScanObjects) return;
        report.NoteScanOverflow(found - MaxScanObjects);
        if (_scanOverflowLogged) return;
        _scanOverflowLogged = true;
        Plugin.Logger?.LogWarning(
            $"capture: {found} {what} objects in the scene, scanning the first {MaxScanObjects}; "
            + "the report is marked truncated and objects past the cap are neither discovered nor sampled");
    }

    /// <summary>
    /// One GameObject on one pass: discover it if it is new, re-key it if Unity recycled its
    /// instance id, and compare it against its snapshot when this is a comparison pass. Returns the
    /// instance id, or 0 if it could not be read.
    /// </summary>
    private static int VisitObject(
        GameObject go, string source, CaptureReport report, int frameIndex, double atSeconds, bool compare)
    {
        int id;
        try { id = go.GetInstanceID(); } catch { return 0; }
        if (id == 0) return 0;

        bool known = SeenObjects.TryGetValue(id, out var seenName);

        // The name is only read when it is needed: on discovery, and on a comparison pass, where it
        // is half the object's identity. Non-comparison frames allocate nothing here.
        string? name = (!known || compare) ? SafeName(go) : null;

        if (!known)
        {
            Consider(go, id, name!, source, report, frameIndex, atSeconds);
        }
        else if (compare && !string.Equals(name, seenName, StringComparison.Ordinal))
        {
            // Recycled instance id: close everything keyed on the old identity and start over.
            // RetireSeries drops the sampling entry too, so the slot goes back into circulation.
            report.RetireSeries(id, seenName ?? "");
            SeenObjects.Remove(id);
            Consider(go, id, name!, source, report, frameIndex, atSeconds);
        }

        if (compare) ObserveObject(go, id, name!, source, report, frameIndex, atSeconds);
        return id;
    }

    /// <summary>Records a GameObject the first time it is seen, if it passes the name filter.</summary>
    private static void Consider(
        GameObject go, int id, string name, string source, CaptureReport report, int frameIndex, double atSeconds)
    {
        if (SeenObjects.ContainsKey(id)) return;
        SeenObjects[id] = name;

        // The name is already in hand and DescribeObject is the expensive read in this op — a
        // GetComponents<Component>() plus a GetIl2CppType().Name per component (R28 finding 28.22).
        // When the name alone settles the filter, take that answer and skip the read entirely;
        // only a name that does NOT match needs the components and the parent to decide.
        bool nameMatches = CaptureArgs.Matches(_filter, name);

        var row = DescribeObject(go, id, source, frameIndex, atSeconds);
        if (row == null) return;
        if (!nameMatches
            && !CaptureArgs.Matches(_filter, row.Name)
            && !CaptureArgs.MatchesAny(_filter, row.Components)
            && !CaptureArgs.Matches(_filter, row.Parent))
        {
            return;
        }

        if (!report.TryAddObject(row)) return;

        // Only register it for sampling. The first positions are already on the object row, and
        // the sample itself is taken by the next scan frame — adding one here would double the
        // sample for any line discovered during a scan frame. A full table rotates its stalest
        // entry out rather than refusing forever (R28 finding 18).
        if (row.Line != null) report.Sampled.TryRegister(id, name, frameIndex);

        // Mid-window discoveries join the tracked set too, so a clone the pool had to grow is
        // watched for the rest of the window. Baseline objects are tracked unconditionally; these
        // are filtered, so an unrelated new object cannot eat the tracking cap.
        TrackObject(go, id, name, report, frameIndex);
    }

    /// <summary>
    /// Compares one tracked object against its snapshot. The read is the light one — no colors, no
    /// shader, no component list — because those are only interesting on a transition, and then the
    /// tracker asks for the full description explicitly.
    /// </summary>
    private static void ObserveObject(
        GameObject go, int id, string name, string source, CaptureReport report, int frameIndex, double atSeconds)
    {
        var tracker = report.Tracker;
        if (!tracker.NeedsObservation(id, frameIndex)) return;
        FillState(go, id, name, Scratch, full: false);
        var row = tracker.Observe(Scratch, frameIndex, atSeconds, report);
        if (row == null) return;
        try { row.Description = DescribeObject(go, id, source, frameIndex, atSeconds); }
        catch (Exception e) { Plugin.Logger?.LogWarning($"capture describe change {id}: {e.Message}"); }
    }

    /// <summary>Starts tracking an object from a full read.</summary>
    private static void TrackObject(GameObject go, int id, string name, CaptureReport report, int frameIndex)
    {
        if (report.Tracker.IsTracked(id)) return;
        FillState(go, id, name, Scratch, full: true);
        report.Tracker.TryTrack(Scratch, frameIndex);
    }

    /// <summary>
    /// Copies the live GameObject into the reusable scratch snapshot. <paramref name="full"/> adds
    /// the fields that allocate (colors, shader name, parent name) and is used at baseline and on
    /// discovery only; a comparison pass takes the cheap half.
    /// </summary>
    private static void FillState(GameObject go, int id, string name, CaptureObjectState state, bool full)
    {
        state.Reset();
        state.InstanceId = id;
        state.Name = name;

        try { state.ActiveSelf = go.activeSelf; } catch { }
        try { state.ActiveInHierarchy = go.activeInHierarchy; } catch { }

        try
        {
            var tf = go.transform;
            var parent = tf == null ? null : tf.parent;
            if (parent != null)
            {
                var parentGo = parent.gameObject;
                if (parentGo != null) state.ParentInstanceId = parentGo.GetInstanceID();
                if (full) state.ParentName = parent.name;
            }
        }
        catch { }

        try
        {
            var lr = go.GetComponent<LineRenderer>();
            if (lr != null)
            {
                state.HasLine = true;
                try { state.LinePositionCount = lr.positionCount; } catch { }
                try { state.StartWidth = lr.startWidth; } catch { }
                try { state.EndWidth = lr.endWidth; } catch { }
                try { state.UseWorldSpace = lr.useWorldSpace; } catch { }
                ReadLinePositions(lr, state.LinePositions);
                if (full)
                {
                    try { state.StartColor = lr.startColor.ToString(); } catch { }
                    try { state.EndColor = lr.endColor.ToString(); } catch { }
                    try
                    {
                        var mat = lr.sharedMaterial;
                        state.Shader = mat != null && mat.shader != null ? mat.shader.name : null;
                    }
                    catch { }
                }
            }
        }
        catch { }

        try
        {
            var ps = go.GetComponent<ParticleSystem>();
            if (ps != null)
            {
                state.HasParticle = true;
                try { state.ParticleIsPlaying = ps.isPlaying; } catch { }
                try { state.ParticleCount = ps.particleCount; } catch { }
            }
        }
        catch { }

        try
        {
            var hybrid = go.GetComponent<HybridLineRendererComponent>();
            if (hybrid != null)
            {
                state.HasHybrid = true;
                var list = hybrid.Positions;
                if (list != null)
                {
                    for (int i = 0; i < list.Count && i < MaxPositionsPerLine; i++)
                    {
                        var v = list[i];
                        state.HybridPositions.Add(new CaptureVec3(v.x, v.y, v.z));
                    }
                }
            }
        }
        catch { }
    }

    private static CaptureObjectRow? DescribeObject(GameObject go, int id, string source, int frameIndex, double atSeconds)
    {
        try
        {
            var row = new CaptureObjectRow
            {
                InstanceId = id,
                Name = SafeName(go),
                Source = source,
                Frame = frameIndex,
                AtSeconds = atSeconds,
            };
            try { row.Active = go.activeInHierarchy; } catch { }
            try
            {
                var parent = go.transform != null ? go.transform.parent : null;
                row.Parent = parent == null ? null : parent.name;
            }
            catch { }

            try
            {
                var comps = go.GetComponents<Component>();
                if (comps != null)
                {
                    for (int i = 0; i < comps.Length; i++)
                    {
                        if (comps[i] == null) continue;
                        try { row.Components.Add(comps[i].GetIl2CppType().Name); }
                        catch { row.Components.Add("<unresolved>"); }
                    }
                }
            }
            catch { }

            try
            {
                var lr = go.GetComponent<LineRenderer>();
                if (lr != null) row.Line = DescribeLine(lr);
            }
            catch { }

            try
            {
                var ps = go.GetComponent<ParticleSystem>();
                if (ps != null)
                {
                    row.Particle = new CaptureParticleInfo();
                    try { row.Particle.IsPlaying = ps.isPlaying; } catch { }
                    try { row.Particle.ParticleCount = ps.particleCount; } catch { }
                }
            }
            catch { }

            try
            {
                var hybrid = go.GetComponent<HybridLineRendererComponent>();
                if (hybrid != null)
                {
                    var list = hybrid.Positions;
                    if (list != null)
                    {
                        var pts = new List<CaptureVec3>(Math.Min(list.Count, MaxPositionsPerLine));
                        for (int i = 0; i < list.Count && i < MaxPositionsPerLine; i++)
                        {
                            var v = list[i];
                            pts.Add(new CaptureVec3(v.x, v.y, v.z));
                        }
                        row.HybridPositions = pts;
                    }
                }
            }
            catch { }

            return row;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning($"capture describe object {id}: {e.Message}");
            return null;
        }
    }

    private static CaptureLineInfo DescribeLine(LineRenderer lr)
    {
        var info = new CaptureLineInfo();
        try { info.PositionCount = lr.positionCount; } catch { }
        try { info.StartWidth = lr.startWidth; } catch { }
        try { info.EndWidth = lr.endWidth; } catch { }
        try { info.StartColor = lr.startColor.ToString(); } catch { }
        try { info.EndColor = lr.endColor.ToString(); } catch { }
        try { info.UseWorldSpace = lr.useWorldSpace; } catch { }
        try
        {
            var mat = lr.sharedMaterial;
            info.Shader = mat != null && mat.shader != null ? mat.shader.name : null;
        }
        catch { }
        ReadLinePositions(lr, info.Positions);
        return info;
    }

    private static void ReadLinePositions(LineRenderer lr, List<CaptureVec3> into)
    {
        into.Clear();
        try
        {
            int n = Math.Min(lr.positionCount, MaxPositionsPerLine);
            for (int i = 0; i < n; i++)
            {
                var v = lr.GetPosition(i);
                into.Add(new CaptureVec3(v.x, v.y, v.z));
            }
        }
        catch { }
    }

    // ---- baseline ---------------------------------------------------------------------------

    private static int BaselineEntities(World world)
    {
        if (!QueryUsableFor(world)) { ReleaseQuery(); return 0; }
        NativeArray<Entity> entities = default;
        bool have = false;
        try
        {
            entities = _query.ToEntityArray(Allocator.Temp);
            have = true;
            int limit = Math.Min(entities.Length, _maxEntities);
            for (int i = 0; i < limit; i++) SeenEntities.Add(entities[i]);
            return SeenEntities.Count;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning($"capture baseline entities: {e.Message}");
            return 0;
        }
        finally
        {
            if (have) entities.Dispose();
        }
    }

    /// <summary>
    /// Baseline GameObjects: everything already under the particle parent plus every existing
    /// <c>LineRenderer</c> and <c>HybridLineRendererComponent</c> in the scene. Anything in this
    /// set is, by construction, not something the user's trigger CREATED — but it may well be
    /// something the trigger woke up, so every one of them is also snapshotted into the tracker,
    /// unfiltered, up to <see cref="CaptureArgs.MaxTrackedObjects"/>.
    /// </summary>
    private static int BaselineObjects(World world, CaptureReport report)
    {
        int count = 0;
        var parent = ParticleParent(world);
        if (parent != null)
        {
            int nodes = 0;
            count += MarkTree(parent, 0, ref nodes, report);
        }

        count += MarkAll(Il2CppType.Of<LineRenderer>(), report);
        count += MarkAll(Il2CppType.Of<HybridLineRendererComponent>(), report);

        if (report.Tracker.Dropped > 0)
        {
            Plugin.Logger?.LogWarning(
                $"capture: baseline had more than {CaptureArgs.MaxTrackedObjects} objects; "
                + $"{report.Tracker.Dropped} are not tracked for changes");
        }

        return count;
    }

    private static int MarkTree(Transform t, int depth, ref int nodes, CaptureReport report)
    {
        if (depth > CaptureWalkBudget.MaxDepth || nodes >= CaptureWalkBudget.MaxNodes) return 0;
        int marked = 0;
        int count;
        try { count = t.childCount; } catch { return 0; }
        for (int i = 0; i < count && nodes < CaptureWalkBudget.MaxNodes; i++)
        {
            Transform? child;
            try { child = t.GetChild(i); } catch { continue; }
            if (child == null) continue;
            nodes++;
            try
            {
                var go = child.gameObject;
                if (go != null && Mark(go, report)) marked++;
            }
            catch { }
            marked += MarkTree(child, depth + 1, ref nodes, report);
        }
        return marked;
    }

    private static int MarkAll(Il2CppSystem.Type? type, CaptureReport report)
    {
        if (type == null) return 0;
        int marked = 0;
        try
        {
            var all = UnityEngine.Object.FindObjectsOfType(type, true);
            if (all == null) return 0;
            for (int i = 0; i < all.Length; i++)
            {
                try
                {
                    var component = all[i] == null ? null : all[i].TryCast<Component>();
                    if (component == null) continue;
                    var go = component.gameObject;
                    if (go != null && Mark(go, report)) marked++;
                }
                catch { }
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning($"capture baseline scan: {e.Message}");
        }
        return marked;
    }

    /// <summary>Marks one baseline object as already-seen and snapshots it for the change detector.</summary>
    private static bool Mark(GameObject go, CaptureReport report)
    {
        int id;
        try { id = go.GetInstanceID(); } catch { return false; }
        if (id == 0 || SeenObjects.ContainsKey(id)) return false;

        string name = SafeName(go);
        SeenObjects[id] = name;
        TrackObject(go, id, name, report, 0);
        return true;
    }

    // ---- type slots -------------------------------------------------------------------------

    /// <summary>One resolved interest component: its ComponentType, TypeIndex, size and field walk.</summary>
    private sealed class TypeSlot
    {
        public string FullName = "";
        public string SimpleName = "";
        public ComponentType Type;
        public TypeIndex Index;
        public bool IsBuffer;
        public bool IsEndpoint;
        public int Size;
        public ComponentDump.FieldSpec[] Fields = Array.Empty<ComponentDump.FieldSpec>();
    }

    private static void ResolveSlots()
    {
        Slots.Clear();
        MissingTypes.Clear();
        var endpoints = new HashSet<string>(EndpointTypeNames, StringComparer.Ordinal);

        foreach (var fullName in InterestTypeNames)
            AddSlot(fullName, endpoints.Contains(fullName));

        // Translation is an endpoint source but never an interest component on its own: matching
        // it would pull in every transform in the world. IsInterest keeps it out of the query;
        // it is read on entities the interest set already matched.
        foreach (var fullName in EndpointTypeNames)
        {
            if (Array.IndexOf(InterestTypeNames, fullName) >= 0) continue;
            AddSlot(fullName, true);
        }
    }

    private static void AddSlot(string fullName, bool isEndpoint)
    {
        var managed = FindInteropType(fullName);
        if (managed == null)
        {
            MissingTypes.Add(fullName);
            return;
        }

        try
        {
            System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(managed.TypeHandle);
            var il2cpp = Il2CppType.From(managed);
            var componentType = new ComponentType(il2cpp, ComponentType.AccessMode.ReadOnly);
            var index = componentType.TypeIndex;
            int ecsSize = 0;
            try { ecsSize = TypeManager.GetTypeInfo(index).TypeSize; } catch { }
            int size = ComponentDump.SizeOf(managed, ecsSize);
            bool isBuffer = false;
            try { isBuffer = componentType.IsBuffer; } catch { }
            // A buffer's TypeSize is the element size in this build, but it reads 0 through the
            // interop as often as an ordinary component does; Marshal.SizeOf of the element struct
            // is the length used for both (interop trap 7, CLAUDE.md).
            if (isBuffer)
            {
                try { size = System.Runtime.InteropServices.Marshal.SizeOf(managed); } catch { }
            }

            Slots.Add(new TypeSlot
            {
                FullName = fullName,
                SimpleName = managed.Name,
                Type = componentType,
                Index = index,
                IsBuffer = isBuffer,
                IsEndpoint = isEndpoint,
                Size = size,
                Fields = size > 0 ? ComponentDump.FieldsOf(managed, size) : Array.Empty<ComponentDump.FieldSpec>(),
            });
        }
        catch (Exception e)
        {
            MissingTypes.Add(fullName + " (" + e.Message + ")");
        }
    }

    /// <summary>
    /// Finds an interop value type by full name across the loaded assemblies. The <c>get</c> op's
    /// index is limited to a fixed assembly list and skips internal helper namespaces, and two of
    /// the types this op needs (<c>Stunlock.Sequencer.SequenceInstance</c>,
    /// <c>ProjectM.Sequencer.BuffSequenceSpawned</c>) fall outside it.
    /// </summary>
    private static Type? FindInteropType(string fullName)
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type? type;
            try { type = assembly.GetType(fullName, false); }
            catch { continue; }
            if (type != null && type.IsValueType) return type;
        }
        return null;
    }

    private static void CreateQuery(World world)
    {
        ReleaseQuery();
        if (Slots.Count == 0) return;
        var builder = new EntityQueryBuilder(Allocator.Temp);
        try
        {
            int added = 0;
            foreach (var slot in Slots)
            {
                if (!IsInterest(slot.FullName)) continue;
                builder = builder.AddAny(slot.Type);
                added++;
            }
            if (added == 0) return;
            builder = builder.WithOptions(EntityQueryOptions.IncludeDisabledEntities);
            _query = world.EntityManager.CreateEntityQuery(ref builder);
            _queryCreated = true;
            _queryWorld = world;
            _querySeq = SeqOf(world);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"capture query build failed: {e.Message}");
            _queryCreated = false;
        }
        finally
        {
            builder.Dispose();
        }
    }

    private static bool IsInterest(string fullName) => Array.IndexOf(InterestTypeNames, fullName) >= 0;

    private static void ReleaseQuery()
    {
        if (!_queryCreated) return;
        var owner = _queryWorld;
        var builtSeq = _querySeq;
        _queryCreated = false;
        _queryWorld = null;
        _querySeq = null;
        // Dispose only while the owning world still lives and is still that world.
        if (IsAlive(owner) && builtSeq != null && SeqOf(owner!) == builtSeq)
        {
            try { _query.Dispose(); }
            catch (Exception e) { Plugin.Logger?.LogWarning($"capture query dispose: {e.Message}"); }
        }
        _query = default;
    }

    private static bool QueryUsableFor(World world)
    {
        if (!_queryCreated || _querySeq == null || !IsAlive(world)) return false;
        return SeqOf(world) == _querySeq;
    }

    private static bool IsAlive(World? world)
    {
        try { return world != null && world.IsCreated; }
        catch { return false; }
    }

    private static ulong? SeqOf(World world)
    {
        try { return world.SequenceNumber; }
        catch { return null; }
    }

    // ---- misc -------------------------------------------------------------------------------

    private static string WriteReport(CaptureReport report)
    {
        string path = CaptureReportPaths.For(PluginReloader.Folder, report.StartedUtc);
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(report.ToJson(), new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
            return path;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"capture report write to '{path}' failed: {e.Message}");
            throw new InvalidOperationException($"capture report write to '{path}' failed: {e.Message}", e);
        }
    }

    private static void ResetState()
    {
        SeenEntities.Clear();
        FrameEntities.Clear();
        SeenObjects.Clear();
        Tracked.Clear();
        ScratchPositions.Clear();
        Scratch.Reset();
        PoolChildren.Clear();
        PoolChildrenPrev.Clear();
        WalkBudget.Reset();
        _poolPassTaken = false;
        _scanOverflowLogged = false;
        _scanDeadlineLogged = false;
        _entityKeys = Array.Empty<long>();
        _entityValues = Array.Empty<Entity>();
        _scanIds = Array.Empty<int>();
        _scanSlots = Array.Empty<int>();
        _frameIndex = 0;
    }

    /// <summary>Grows the reused entity sort buffers. Called only on a frame that had to truncate.</summary>
    private static void EnsureSortCapacity(int needed)
    {
        if (_entityKeys.Length >= needed) return;
        int size = Math.Max(needed, Math.Max(64, _entityKeys.Length * 2));
        _entityKeys = new long[size];
        _entityValues = new Entity[size];
    }

    private static bool TryPrefabGuid(EntityManager em, Entity entity, out int hash)
    {
        hash = 0;
        try
        {
            if (!_prefabGuidResolved)
            {
                _prefabGuidType = new ComponentType(Il2CppType.Of<PrefabGUID>(), ComponentType.AccessMode.ReadOnly);
                _prefabGuidResolved = true;
            }
            if (!em.HasComponent(entity, _prefabGuidType)) return false;
            hash = em.GetComponentData<PrefabGUID>(entity)._Value;
            return true;
        }
        catch { return false; }
    }

    private static string SafeName(GameObject go)
    {
        try { return go.name ?? ""; }
        catch { return "<unnamed>"; }
    }

    private static string FormatEntity(Entity entity) => $"{entity.Index}:{entity.Version}";

    private static string? ReadString(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var el)) return null;
        if (el.ValueKind != JsonValueKind.String) return null;
        var value = el.GetString();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static int ReadInt(JsonElement args, string name, int fallback)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var el)) return fallback;
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var n)) return n;
        if (el.ValueKind == JsonValueKind.String
            && int.TryParse(el.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) return n;
        return fallback;
    }

    private static double ReadDouble(JsonElement args, string name, double fallback)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var el)) return fallback;
        if (el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out var d)) return d;
        if (el.ValueKind == JsonValueKind.String
            && double.TryParse(el.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
        return fallback;
    }
}
