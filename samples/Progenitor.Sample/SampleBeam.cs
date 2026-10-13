using System;
using System.Globalization;
using Progenitor.Effects;

#if !PROGENITOR_TESTS
using ProjectM.Shared;
using Progenitor.Commands;
using Progenitor.Ecs;
using Progenitor.Units;
using Stunlock.Core;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
#endif

namespace Progenitor.Sample;

/// <summary>
/// Sample <c>.beam</c> server command: a networked coil-beam entity from the caller to
/// the nearest rat (or 6 m ahead), tracking both. <c>.fx beam</c> stays the client-only
/// LineRenderer placeholder.
/// </summary>
internal static class SampleBeam
{
    internal const float DefaultSeconds = BeamPresets.DefaultSeconds;
    internal const float AheadMetres = 6f;
    internal const float DefaultSpacing = BeamPresets.DefaultSpacing;
    internal const string RatPrefab = "CHAR_Critter_Rat";

    internal enum Kind
    {
        Spawn = 1,
        Off = 2,

        /// <summary>
        /// <c>.beam link</c>: the game's own <c>NetworkedSequence</c> between two networked
        /// entities. Both endpoints sync as entities, so the clients track them natively.
        /// </summary>
        Link = 3,

        /// <summary>
        /// <c>.beam mark</c>: the same <c>NetworkedSequence</c> carrier with one endpoint —
        /// the build-mode placement beams playing on a single entity and following it.
        /// </summary>
        Mark = 4,
    }

    internal readonly struct Args
    {
        public Args(Kind kind, float seconds, BeamPreset preset, int count, BeamPhase phase = BeamPhase.Steady)
        {
            Kind = kind;
            Seconds = seconds;
            Preset = preset;
            Count = count;
            Phase = phase;
        }

        public Kind Kind { get; }
        public float Seconds { get; }
        public BeamPreset Preset { get; }
        public int Count { get; }
        public BeamPhase Phase { get; }
    }

    /// <summary>
    /// Game-free view of the world the <c>.beam link</c> marker rat needs, so the ordering
    /// R27 finding 20 got wrong is driven by tests rather than only by the live command.
    /// </summary>
    internal interface ILinkWorld
    {
        /// <summary>
        /// Nearest rat, if any. <paramref name="excludeIndex"/> is the marker rat the sweep
        /// just despawned: a destroy is one frame deferred, so it is still enumerable this
        /// frame and must never be picked as the endpoint we keep (finding 20a).
        /// </summary>
        bool TryFindRat(int excludeIndex, out int index, out int version);

        /// <summary>
        /// Spawn a rat ahead of the caller to act as the far endpoint. Returns the handle that
        /// despawns it, or null when the spawn failed.
        /// </summary>
        IDisposable? SpawnMarkerRat(out int index, out int version, out string where);
    }

    private static readonly object Gate = new();
    private static object? _owner;
    private static string _pluginId = "progenitor.sample";
    private static IServerBeamHandle? _last;
    private static IDisposable? _marker;
    private static int _markerIndex;
    private static int _live;

    internal static int LiveCount
    {
        get { lock (Gate) return _live; }
    }

    /// <summary>
    /// <c>.beam [seconds] [red|blue|coil] [count] [steady|blast]</c>, <c>.beam link [seconds]</c>
    /// or <c>.beam off</c>. Color and phase tokens may appear in any slot; the first positive
    /// number is seconds, the first integer after a color (or the second number) is count.
    /// </summary>
    internal static bool TryParse(string? a, string? b, string? c, out Args args)
    {
        args = new Args(Kind.Spawn, DefaultSeconds, BeamPreset.CastleRed, 1);
        string[] tokens = { a ?? "", b ?? "", c ?? "" };
        bool any = false;
        bool link = false;
        bool mark = false;
        bool haveSeconds = false;
        bool haveColor = false;
        bool haveCount = false;
        bool havePhase = false;
        float seconds = DefaultSeconds;
        var preset = BeamPreset.CastleRed;
        var phase = BeamPhase.Steady;
        int count = 1;

        for (int i = 0; i < tokens.Length; i++)
        {
            string t = tokens[i].Trim();
            if (t.Length == 0) continue;
            any = true;
            string lower = t.ToLowerInvariant();
            if (lower == "off")
            {
                args = new Args(Kind.Off, seconds, preset, count, phase);
                return true;
            }

            if (lower == "link")
            {
                link = true;
                continue;
            }

            if (lower == "mark")
            {
                mark = true;
                continue;
            }

            if (!havePhase && TryPhase(lower, out var ph))
            {
                phase = ph;
                havePhase = true;
                continue;
            }

            if (!haveColor && TryColor(lower, out var p))
            {
                preset = p;
                haveColor = true;
                continue;
            }

            if (int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)
                && n >= 1
                && n <= BeamPresets.MaxCount
                && !haveCount
                && (haveColor || haveSeconds))
            {
                count = n;
                haveCount = true;
                continue;
            }

            if (!haveSeconds
                && float.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out float s)
                && s > 0f
                && !float.IsInfinity(s))
            {
                // s > 0f is already false for NaN, so the old !float.IsNaN(s) was unreachable
                // (R27 finding 20 tail). What was missing is the upper bound: `.beam link
                // 999999999` used to produce a replicated entity with a ~31-year LifeTime
                // (R27 finding 23), so the parse clamps and the reply text stays honest.
                seconds = s > BeamPresets.MaxLinkSeconds ? BeamPresets.MaxLinkSeconds : s;
                haveSeconds = true;
                continue;
            }

            if (!haveCount
                && int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out int c2)
                && c2 >= 1
                && c2 <= BeamPresets.MaxCount)
            {
                count = c2;
                haveCount = true;
                continue;
            }

            return false;
        }

        if (!any)
        {
            args = new Args(Kind.Spawn, DefaultSeconds, BeamPreset.CastleRed, 1);
            return true;
        }

        if (mark)
        {
            // Mark keeps the color token (it picks the placement sequence variant) but has
            // no coil phase and no count.
            args = new Args(Kind.Mark, seconds, preset, 1);
            return true;
        }

        if (link)
        {
            // Link has no coil preset, no phase and no count: the sequence is the whole look.
            args = new Args(Kind.Link, seconds, BeamPreset.Link, 1);
            return true;
        }

        args = new Args(Kind.Spawn, seconds, preset, count, phase);
        return true;
    }

    internal static void AttachForTests(object owner, string pluginId)
    {
        if (owner == null) throw new ArgumentNullException(nameof(owner));
        lock (Gate)
        {
            _owner = owner;
            _pluginId = string.IsNullOrWhiteSpace(pluginId) ? "progenitor.sample" : pluginId;
        }
    }

    internal static void Detach()
    {
        Off();
        lock (Gate)
        {
            _owner = null;
            _last = null;
            _live = 0;
        }
    }

    /// <summary>
    /// <c>.beam link</c> with the marker rat, in the one order that is correct (R27 finding 20):
    ///
    /// <list type="number">
    /// <item>sweep the previous beam <b>and its marker rat</b> first, before anything looks for
    /// a rat — otherwise the second <c>.beam link</c> finds the rat the sweep is about to
    /// despawn, links a destroyed entity, and still replies as if it worked (finding 20a);</item>
    /// <item>find the far endpoint, skipping the rat that sweep just despawned (a destroy is one
    /// frame deferred, so it is still enumerable);</item>
    /// <item>spawn a marker rat only when there is nothing to keep;</item>
    /// <item>link against the rat we are keeping;</item>
    /// <item>tie the marker's lifetime to the handle, so the rat is despawned when the beam
    /// ends — by the deadline, by <c>.beam off</c>, or by unload — instead of standing in the
    /// world until the next <c>.beam off</c> (finding 20b).</item>
    /// </list>
    /// </summary>
    internal static string RunLinkWithMarker(Args args, ILinkWorld world, int characterIndex, int characterVersion)
    {
        if (world == null) throw new ArgumentNullException(nameof(world));
        object? owner;
        lock (Gate) owner = _owner;
        if (owner == null) return "Sample beam has no owner.";
        if (characterIndex == 0)
            return "Beam link needs two networked entities (you and a unit); spawn one with .rat first.";

        Off(out int despawnedMarker);

        IDisposable? marker = null;
        string dest;
        if (!world.TryFindRat(despawnedMarker, out int targetIndex, out int targetVersion) || targetIndex == 0)
        {
            marker = world.SpawnMarkerRat(out targetIndex, out targetVersion, out string at);
            RememberMarker(marker, targetIndex);
            if (marker == null || targetIndex == 0)
            {
                Log("link no endpoint (rat spawn returned a disabled handle)");
                return "Beam link needs a second networked entity and no rat could be spawned.";
            }

            dest = "spawned rat " + targetIndex + ":" + targetVersion;
            Log("link marker rat " + targetIndex + ":" + targetVersion + " at " + at);
        }
        else
        {
            dest = "rat " + targetIndex + ":" + targetVersion;
        }

        var handle = ServerBeam.Link(
            characterIndex, characterVersion, targetIndex, targetVersion, args.Seconds,
            BeamPresets.DefaultLinkSequence, owner, _pluginId);
        Remember(handle);
        if (marker != null)
        {
            // No beam means the rat has no reason to exist; do not wait for the next .beam off.
            if (handle.Enabled) TieMarkerToHandle(handle, marker);
            else ReleaseMarker(marker);
        }

        string seq = BeamPresets.DefaultLinkSequence
            + " " + BeamPresets.LinkSequenceName(BeamPresets.DefaultLinkSequence);
        Log("link entity=" + handle.Entity + " sequence=" + seq
            + " " + characterIndex + ":" + characterVersion
            + " -> " + targetIndex + ":" + targetVersion
            + " seconds=" + args.Seconds.ToString("0.##", CultureInfo.InvariantCulture)
            + " dest=" + dest + " enabled=" + handle.Enabled);
        return handle.Enabled
            ? "Beam link " + handle.Entity + " -> " + dest + " sequence " + seq + " "
                + args.Seconds.ToString("0.##", CultureInfo.InvariantCulture) + "s"
            : "Beam link disabled (server-only; missing NetworkedSequence prefab?).";
    }

    /// <summary>
    /// The marker rat exists only to be the far end of this beam, so it dies with it. The
    /// handle disposes itself at its own deadline (<c>ServerBeam.Poll</c>), on
    /// <c>.beam off</c>, on <c>RemoveAll</c> and on Core uninstall, and every one of those
    /// paths runs this.
    /// </summary>
    private static void TieMarkerToHandle(IServerBeamHandle handle, IDisposable marker)
    {
        handle.OnDisposed(() => ReleaseMarker(marker));
    }

    private static void ReleaseMarker(IDisposable? marker)
    {
        if (marker == null) return;
        lock (Gate)
        {
            if (ReferenceEquals(_marker, marker))
            {
                _marker = null;
                _markerIndex = 0;
            }
        }

        try { marker.Dispose(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Sample: beam link marker dispose threw: " + e.Message); }
        Log("link marker despawned with the beam");
    }

    /// <summary>Game-free runner used by tests (and by the live command after it has a pose).</summary>
    internal static string Run(Args args, EffectVec3 from = default, EffectVec3 forward = default, int startIndex = 0, int startVersion = 0, int endIndex = 0, int endVersion = 0)
    {
        if (args.Kind == Kind.Off)
            return Off();

        var owner = _owner;
        if (owner == null)
            return "Sample beam has no owner.";

        if (args.Kind == Kind.Link)
            return RunLink(args, owner, startIndex, startVersion, endIndex, endVersion);

        if (args.Kind == Kind.Mark)
            return RunMark(args, owner, endIndex != 0 ? endIndex : startIndex, endIndex != 0 ? endVersion : startVersion);

        EffectVec3 fwd = forward;
        if (fwd.X == 0f && fwd.Y == 0f && fwd.Z == 0f)
            fwd = new EffectVec3(1f, 0f, 0f);

        EffectVec3 start = Add(from, BeamPresets.DefaultStartOffset);
        EffectVec3 end = endIndex != 0
            ? Add(from, BeamPresets.DefaultEndOffset)
            : Add(Offset(from, fwd, AheadMetres), BeamPresets.DefaultEndOffset);

        Off();
        var handle = args.Count > 1
            ? ServerBeam.Beams(args.Preset, start, end, args.Seconds, args.Count, DefaultSpacing, owner, _pluginId)
            : ServerBeam.Spawn(args.Preset, start, end, args.Seconds, owner, _pluginId);
        Remember(handle);
        if (args.Phase == BeamPhase.Blast)
            handle.Blast();
        if (startIndex != 0 || endIndex != 0)
            handle.Track(startIndex, startVersion, endIndex, endVersion, BeamPresets.DefaultStartOffset, BeamPresets.DefaultEndOffset);

        string reply = handle.Enabled
            ? "Beam " + handle.Entity + " " + args.Preset + " " + handle.Phase + " x" + args.Count + " " + args.Seconds.ToString("0.##", CultureInfo.InvariantCulture) + "s"
            : "Beam disabled (server-only; missing coil prefab?).";
        Log("spawn entity=" + handle.Entity + " preset=" + args.Preset + " phase=" + handle.Phase + " count=" + args.Count + " seconds=" + args.Seconds.ToString("0.##", CultureInfo.InvariantCulture) + " enabled=" + handle.Enabled);
        return reply;
    }

    /// <summary>
    /// <c>.beam link</c>: <see cref="ServerBeam.Link"/> between two networked entities. No
    /// preset, no phase, no per-tick writes — the clients resolve both endpoints themselves.
    /// </summary>
    private static string RunLink(Args args, object owner, int startIndex, int startVersion, int endIndex, int endVersion)
    {
        if (startIndex == 0 || endIndex == 0)
            return "Beam link needs two networked entities (you and a unit); spawn one with .rat first.";

        Off();
        var handle = ServerBeam.Link(
            startIndex, startVersion, endIndex, endVersion, args.Seconds,
            BeamPresets.DefaultLinkSequence, owner, _pluginId);
        Remember(handle);
        string seq = BeamPresets.DefaultLinkSequence
            + " " + BeamPresets.LinkSequenceName(BeamPresets.DefaultLinkSequence);
        Log("link entity=" + handle.Entity + " sequence=" + seq
            + " " + startIndex + ":" + startVersion + " -> " + endIndex + ":" + endVersion
            + " seconds=" + args.Seconds.ToString("0.##", CultureInfo.InvariantCulture)
            + " enabled=" + handle.Enabled);
        return handle.Enabled
            ? "Beam link " + handle.Entity + " " + startIndex + ":" + startVersion
                + " -> " + endIndex + ":" + endVersion + " sequence " + seq + " "
                + args.Seconds.ToString("0.##", CultureInfo.InvariantCulture) + "s"
            : "Beam link disabled (server-only; missing NetworkedSequence prefab?).";
    }

    /// <summary>
    /// <c>.beam mark [red|blue] [seconds]</c>: one <c>NetworkedSequence</c> whose only endpoint
    /// is the marked entity. The default look is the build-mode invalid-placement effect (two
    /// red flowing beams that rise from the target and follow it).
    /// </summary>
    private static string RunMark(Args args, object owner, int index, int version)
    {
        if (index == 0)
            return "Beam mark needs a networked entity.";

        int guid = BeamPresets.MarkSequenceFor(args.Preset);
        Off();
        var handle = ServerBeam.Mark(index, version, args.Seconds, guid, owner, _pluginId);
        Remember(handle);
        string seq = guid + " " + BeamPresets.LinkSequenceName(guid);
        Log("mark entity=" + handle.Entity + " sequence=" + seq
            + " target=" + index + ":" + version
            + " seconds=" + args.Seconds.ToString("0.##", CultureInfo.InvariantCulture)
            + " enabled=" + handle.Enabled);
        return handle.Enabled
            ? "Beam mark " + handle.Entity + " on " + index + ":" + version + " sequence " + seq + " "
                + args.Seconds.ToString("0.##", CultureInfo.InvariantCulture) + "s"
            : "Beam mark disabled (server-only; missing NetworkedSequence prefab?).";
    }

#if !PROGENITOR_TESTS
    internal static void HandleCommand(ICommandContext ctx, string a, string b, string c)
    {
        var plugin = Plugin.Active;
        if (plugin == null)
        {
            ctx.Reply("Sample plugin context is not available.");
            return;
        }

        AttachForTests(plugin.Owner, plugin.Guid);
        if (!TryParse(a, b, c, out var args))
        {
            ctx.Reply("Usage: .beam [seconds] [red|blue|coil] [count] [steady|blast] | .beam link [seconds] | .beam mark [red|blue] [seconds] | .beam off");
            return;
        }

        if (args.Kind == Kind.Off)
        {
            ctx.Reply(Off());
            return;
        }

        if (!TryCharacterPose(ctx, out var pos, out var fwd))
        {
            ctx.Reply("No character position; cannot place a beam.");
            return;
        }

        var from = new EffectVec3(pos.x, pos.y, pos.z);
        var character = ctx.CharacterEntity;

        if (args.Kind == Kind.Link)
        {
            // The sweep happens inside RunLinkWithMarker, before it looks for a rat.
            ctx.Reply(RunLinkWithMarker(
                args, new LiveLinkWorld(ctx, plugin, pos, fwd), character.Index, character.Version));
            return;
        }

        // R27 finding 20a: sweep before anything looks for a rat, and never target the marker
        // rat this sweep just despawned (the destroy is one frame deferred).
        Off(out int despawnedMarker);
        Entity target = FindNearestRat(ctx, character, from, despawnedMarker, out var end);

        if (args.Kind == Kind.Mark)
        {
            // Nearest rat if there is one, otherwise the caller: both are replicated entities
            // with a NetworkId, which is all SpawnSequenceForEntity.Target can carry.
            var marked = target == Entity.Null ? character : target;
            string what = target == Entity.Null ? "you" : "rat " + target.Index + ":" + target.Version;
            int guid = BeamPresets.MarkSequenceFor(args.Preset);
            var markHandle = plugin.Effects.ServerBeamMark(marked, args.Seconds, guid);
            Remember(markHandle);
            string markSeq = guid + " " + BeamPresets.LinkSequenceName(guid);
            ctx.Reply("Beam mark " + markHandle.Entity + " on " + what + " sequence " + markSeq + " "
                + args.Seconds.ToString("0.##", CultureInfo.InvariantCulture) + "s");
            Log("mark entity=" + markHandle.Entity + " sequence=" + markSeq
                + " target=" + marked.Index + ":" + marked.Version + " dest=" + what
                + " seconds=" + args.Seconds.ToString("0.##", CultureInfo.InvariantCulture)
                + " enabled=" + markHandle.Enabled);
            return;
        }

        if (target == Entity.Null)
            end = Offset3(pos, fwd, AheadMetres);

        var start = Add(from, BeamPresets.DefaultStartOffset);
        var liftedEnd = new EffectVec3(end.x + BeamPresets.DefaultEndOffset.X, end.y + BeamPresets.DefaultEndOffset.Y, end.z + BeamPresets.DefaultEndOffset.Z);

        var handle = args.Count > 1
            ? plugin.Effects.Beams(args.Preset, start, liftedEnd, args.Seconds, args.Count, DefaultSpacing)
            : plugin.Effects.ServerBeam(args.Preset, start, liftedEnd, args.Seconds);
        Remember(handle);
        if (args.Phase == BeamPhase.Blast)
            handle.Blast();
        handle.Track(
            character.Index,
            character.Version,
            target == Entity.Null ? 0 : target.Index,
            target == Entity.Null ? 0 : target.Version,
            BeamPresets.DefaultStartOffset,
            BeamPresets.DefaultEndOffset);

        string dest = target == Entity.Null
            ? "6m ahead"
            : ("rat " + target.Index + ":" + target.Version);
        ctx.Reply("Beam " + handle.Entity + " " + args.Preset + " " + handle.Phase + " x" + args.Count + " -> " + dest + " " + args.Seconds.ToString("0.##", CultureInfo.InvariantCulture) + "s");
        Log("spawn entity=" + handle.Entity + " preset=" + args.Preset + " phase=" + handle.Phase + " count=" + args.Count + " dest=" + dest + " enabled=" + handle.Enabled);
    }

    /// <summary>
    /// Live <see cref="ILinkWorld"/>. Both endpoints of a <c>SpawnSequenceForEntity</c> travel
    /// as <c>NetworkId</c>s, so a bare marker entity is useless here: the far end has to be a
    /// replicated entity. The nearest rat is reused when there is one; otherwise one is spawned
    /// <see cref="AheadMetres"/> ahead as the marker and despawned with the beam. The ordering
    /// lives in <see cref="RunLinkWithMarker"/>, which the tests drive with a fake.
    /// </summary>
    private sealed class LiveLinkWorld : ILinkWorld
    {
        private readonly ICommandContext _ctx;
        private readonly SampleContext _plugin;
        private readonly float3 _pos;
        private readonly float3 _fwd;

        internal LiveLinkWorld(ICommandContext ctx, SampleContext plugin, float3 pos, float3 fwd)
        {
            _ctx = ctx;
            _plugin = plugin;
            _pos = pos;
            _fwd = fwd;
        }

        public bool TryFindRat(int excludeIndex, out int index, out int version)
        {
            index = 0;
            version = 0;
            var from = new EffectVec3(_pos.x, _pos.y, _pos.z);
            var rat = FindNearestRat(_ctx, _ctx.CharacterEntity, from, excludeIndex, out _);
            if (rat == Entity.Null) return false;
            index = rat.Index;
            version = rat.Version;
            return true;
        }

        public IDisposable? SpawnMarkerRat(out int index, out int version, out string where)
        {
            index = 0;
            version = 0;
            var at = Offset3(_pos, _fwd, AheadMetres);
            where = at.x.ToString("0.##", CultureInfo.InvariantCulture)
                + "," + at.y.ToString("0.##", CultureInfo.InvariantCulture)
                + "," + at.z.ToString("0.##", CultureInfo.InvariantCulture);
            var unit = _plugin.Units.Spawn(RatPrefab, new UnitVec3(at.x, at.y, at.z));
            if (!unit.Enabled || unit.Entity.IsNone)
                return unit;
            index = unit.Entity.Index;
            version = unit.Entity.Version;
            return unit;
        }
    }

    private static bool TryCharacterPose(ICommandContext ctx, out float3 position, out float3 forward)
    {
        position = default;
        forward = new float3(1f, 0f, 0f);
        try
        {
            var ch = ctx.CharacterEntity;
            if (ch == Entity.Null) return false;
            var em = ctx.World.EntityManager;
            if (ch.Has<LocalToWorld>(em))
            {
                var ltw = em.GetComponentData<LocalToWorld>(ch);
                position = ltw.Position;
                var f = ltw.Forward;
                if ((f.x * f.x) + (f.z * f.z) > 1e-6f)
                    forward = new float3(f.x, 0f, f.z);
                return true;
            }

            if (ch.Has<Translation>(em))
            {
                position = em.GetComponentData<Translation>(ch).Value;
                return true;
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Sample: beam pose threw: " + e.Message);
        }

        return false;
    }

    /// <summary>
    /// Nearest live rat. <paramref name="excludeIndex"/> is the marker rat the caller's sweep
    /// just despawned: <c>DestroyUtility.Destroy</c> is one frame deferred, so it is still in
    /// the query this frame and picking it would link a destroyed entity (R27 finding 20a).
    /// </summary>
    private static Entity FindNearestRat(ICommandContext ctx, Entity self, EffectVec3 from, int excludeIndex, out float3 end)
    {
        end = default;
        try
        {
            var world = ctx.World;
            var em = world.EntityManager;
            if (!PrefabLookup.TryGetGuidByName(world, RatPrefab, out var ratGuid) || ratGuid._Value == 0)
                return Entity.Null;

            var list = Query.Entities(
                em,
                includeDisabled: false,
                includeSystems: false,
                ComponentIndex.Of<PrefabGUID>(ComponentType.AccessMode.ReadOnly),
                ComponentIndex.Of<Translation>(ComponentType.AccessMode.ReadOnly));
            Entity best = Entity.Null;
            float bestD = float.MaxValue;
            float3 bestPos = default;
            for (int i = 0; i < list.Count; i++)
            {
                var e = list[i];
                if (e == self) continue;
                if (excludeIndex != 0 && e.Index == excludeIndex) continue;
                if (e.Has<DestroyTag>(em)) continue;
                PrefabGUID g;
                try { g = em.GetComponentData<PrefabGUID>(e); }
                catch { continue; }
                if (g._Value != ratGuid._Value) continue;
                Translation t;
                try { t = em.GetComponentData<Translation>(e); }
                catch { continue; }
                float dx = t.Value.x - from.X;
                float dy = t.Value.y - from.Y;
                float dz = t.Value.z - from.Z;
                float d = (dx * dx) + (dy * dy) + (dz * dz);
                if (d < bestD)
                {
                    bestD = d;
                    best = e;
                    bestPos = t.Value;
                }
            }

            if (best == Entity.Null) return Entity.Null;
            end = bestPos;
            return best;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Sample: beam nearest rat threw: " + e.Message);
            return Entity.Null;
        }
    }

    private static float3 Offset3(float3 from, float3 forward, float metres)
    {
        float len = MathF.Sqrt((forward.x * forward.x) + (forward.y * forward.y) + (forward.z * forward.z));
        if (len < 1e-6f) return from + new float3(metres, 0f, 0f);
        float inv = metres / len;
        return from + (forward * inv);
    }
#endif

    private static string Off() => Off(out _);

    /// <summary>
    /// Sweep the current beam and its marker rat. <paramref name="despawnedMarkerIndex"/> is
    /// the entity index of the rat this call despawned (0 when there was none): the destroy is
    /// one frame deferred, so a caller that immediately looks for "the nearest rat" would
    /// otherwise find it again (R27 finding 20a).
    /// </summary>
    private static string Off(out int despawnedMarkerIndex)
    {
        IServerBeamHandle? last;
        IDisposable? marker;
        object? owner;
        lock (Gate)
        {
            last = _last;
            marker = _marker;
            owner = _owner;
            despawnedMarkerIndex = _markerIndex;
            _last = null;
            _marker = null;
            _markerIndex = 0;
            _live = 0;
        }

        try { last?.Dispose(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Sample: beam last dispose threw: " + e.Message); }
        try { marker?.Dispose(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Sample: beam link marker dispose threw: " + e.Message); }
        if (owner != null)
        {
            try { ServerBeam.RemoveAll(owner); }
            catch (Exception e) { Plugin.Logger?.LogWarning("Sample: beam RemoveAll threw: " + e.Message); }
        }

        Log("off");
        return "Beam off.";
    }

    private static void Remember(IServerBeamHandle handle)
    {
        lock (Gate)
        {
            _last = handle;
            _live = handle.Enabled ? handle.Count : 0;
        }
    }

    /// <summary>
    /// Hold the unit spawned as a link endpoint so <c>.beam off</c> (and the next
    /// <c>.beam link</c>) despawns it. Recorded <b>after</b> the sweep, so the marker for the
    /// beam about to be spawned is never the one the sweep just took away; its own release runs
    /// from the handle (<see cref="TieMarkerToHandle"/>).
    /// </summary>
    private static void RememberMarker(IDisposable? marker, int index)
    {
        IDisposable? previous;
        lock (Gate)
        {
            previous = _marker;
            _marker = marker;
            _markerIndex = marker == null ? 0 : index;
        }

        try { previous?.Dispose(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Sample: beam link marker dispose threw: " + e.Message); }
    }

    private static bool TryPhase(string lower, out BeamPhase phase)
    {
        switch (lower)
        {
            case "steady":
            case "preview":
                phase = BeamPhase.Steady;
                return true;
            case "blast":
                phase = BeamPhase.Blast;
                return true;
            default:
                phase = BeamPhase.Steady;
                return false;
        }
    }

    private static bool TryColor(string lower, out BeamPreset preset)
    {
        switch (lower)
        {
            case "red":
            case "castle":
            case "castlered":
                preset = BeamPreset.CastleRed;
                return true;
            case "blue":
                preset = BeamPreset.Blue;
                return true;
            case "coil":
                preset = BeamPreset.Coil;
                return true;
            default:
                preset = BeamPreset.CastleRed;
                return false;
        }
    }

    private static EffectVec3 Offset(EffectVec3 from, EffectVec3 forward, float metres)
    {
        float len = MathF.Sqrt((forward.X * forward.X) + (forward.Y * forward.Y) + (forward.Z * forward.Z));
        if (len < 1e-6f) return new EffectVec3(from.X + metres, from.Y, from.Z);
        float inv = metres / len;
        return new EffectVec3(from.X + (forward.X * inv), from.Y + (forward.Y * inv), from.Z + (forward.Z * inv));
    }

    private static EffectVec3 Add(EffectVec3 a, EffectVec3 b) =>
        new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

    private static void Log(string line) =>
        Plugin.Logger?.LogInfo("Sample: beam " + line);
}
