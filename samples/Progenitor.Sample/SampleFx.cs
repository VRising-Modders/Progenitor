using System;
using System.Collections.Generic;
using System.Globalization;
using Progenitor.Dev;
using Progenitor.Effects;

#if !PROGENITOR_TESTS
using Progenitor.Ecs;
using Progenitor.Networking;
using Progenitor.Plugins;
using ProjectM;
using ProjectM.Network;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
#endif

namespace Progenitor.Sample;

/// <summary>
/// Sample <c>.fx</c> subcommands. Parsing and handle tracking are game-free so the test
/// project can compile this file; Unity / ECS pose lookup stays behind
/// <c>!PROGENITOR_TESTS</c>.
/// </summary>
internal static class SampleFx
{
    /// <summary>Custom event id ('SmFx'). Distinctive so a collision with a game hash is silent, not mixed.</summary>
    internal const int EventId = 0x536D4678;

    internal const string ImageName = "PassiveFrame_Normal";
    internal const string ParticleName = "Embers";
    /// <summary>Showcase flame the user saw at 2x (evidence 95). Default <c>.fx particle</c> pair.</summary>
    internal const string FlameName = "Flame";
    internal const float DefaultFlameScale = 2f;
    internal const float DefaultEmbersScale = 3f;
    /// <summary>Named <c>.fx particle embers</c> without a scale stays visible (unscaled embers are faint).</summary>
    internal const float DefaultNamedScale = 3f;
    /// <summary>Cardinal find-chest buff. <see cref="Assets.Borrow"/> parses this as a PrefabGUID hash.</summary>
    internal const string BeamName = "-2014639169";
    internal const string VolumeName = "vignette";

    internal static readonly EffectColor Gold = new(1f, 0.84f, 0f, 1f);
    /// <summary>
    /// Saturated gold so a camera-wide vignette reads over the game's own low-health vignette
    /// (evidence 79 was subtle at default intensity).
    /// </summary>
    internal static readonly EffectColor VignetteGold = new(1f, 0.55f, 0f, 1f);
    internal static readonly EffectColor Blue = new(0.25f, 0.55f, 1f, 1f);
    internal static readonly EffectVec2 ImageSize = new(192f, 192f);
    internal const float VignetteIntensity = 0.75f;
    internal const float VignetteSmoothness = 0.6f;

    internal static double ImageSeconds = 5;
    internal static double ParticleSeconds = 5;
    internal static double BeamSeconds = 5;
    internal static double VignetteSeconds = 3;

    private static readonly object Gate = new();
    private static readonly List<IDisposable> Live = new();
    private static object? _owner;
    private static string _pluginId = "progenitor.sample";
    private static Func<AssetKind, string, IEffectHandle>? _borrow;
    private static Func<IEffectHandle, HostKind, IHostHandle>? _host;
    private static Func<BeamStyle, SequenceEffectHandle>? _beam;

    /// <summary>Last <c>.fx particle [name]</c> token, or null for the Flame+Embers default combo.</summary>
    internal static string? ParsedParticleName { get; private set; }

    /// <summary>Last <c>.fx particle … [scale]</c> value when <see cref="ParsedParticleHasScale"/> is true.</summary>
    internal static float ParsedParticleScale { get; private set; }

    /// <summary>True when the last parse supplied an explicit scale token.</summary>
    internal static bool ParsedParticleHasScale { get; private set; }

    /// <summary>Last <c>.fx beam [thin|red|blue]</c> style when <see cref="ParsedHasBeamStyle"/> is true.</summary>
    internal static BeamStyle ParsedBeamStyle { get; private set; }

    /// <summary>True when the last parse named a beam style. False means use <see cref="Assets.PreferredBeamStyle"/>.</summary>
    internal static bool ParsedHasBeamStyle { get; private set; }

    internal enum Kind
    {
        Image = 1,
        Particle = 2,
        Beam = 3,
        Vignette = 4,
        Off = 5,
        BeamMenu = 6,
        ParticleMenu = 7,
    }

    /// <summary>
    /// False until <see cref="Assets.Host"/> can size and place the overlay RawImage.
    /// This cut is <c>Host(handle, kind)</c> only; <see cref="IHostHandle"/> has no Size/Position.
    /// An unsized sequence RT host fills the screen (look-along fallback, evidence 89).
    /// </summary>
    internal static bool HostApiCanSizeRawImage => false;

#if !PROGENITOR_TESTS
    /// <summary>
    /// Wire payload. SampleCommands only assigns <see cref="Value"/>; the setter snapshots
    /// the last <c>.fx particle [name] [scale]</c> parse so extras reach the client without
    /// changing the two-token command shape.
    /// </summary>
    internal struct Packet
    {
        private int _value;
        public int Value
        {
            get => _value;
            set
            {
                _value = value;
                Flags = 0;
                Scale = 0f;
                N0 = N1 = N2 = N3 = N4 = N5 = N6 = N7 = N8 = N9 = N10 = N11 = 0;
                if (ParsedParticleHasScale)
                {
                    Flags |= FlagHasScale;
                    Scale = ParsedParticleScale;
                }

                if (!string.IsNullOrEmpty(ParsedParticleName))
                {
                    Flags |= FlagHasName;
                    PackName(ParsedParticleName);
                }

                if (ParsedHasBeamStyle)
                {
                    Flags |= FlagHasBeamStyle;
                    Style = (int)ParsedBeamStyle;
                }
            }
        }

        public float Scale;
        public int Flags;
        public int Style;
        public int N0, N1, N2, N3, N4, N5, N6, N7, N8, N9, N10, N11;

        private const int FlagHasName = 1;
        private const int FlagHasScale = 2;
        private const int FlagHasBeamStyle = 4;

        private void PackName(string name)
        {
            int n = name.Length;
            if (n > 24) n = 24;
            N0 = Pack4(name, 0, n);
            N1 = Pack4(name, 4, n);
            N2 = Pack4(name, 8, n);
            N3 = Pack4(name, 12, n);
            N4 = Pack4(name, 16, n);
            N5 = Pack4(name, 20, n);
        }

        internal void UnpackIntoParse()
        {
            ClearParticleParse();
            if ((Flags & FlagHasScale) != 0)
            {
                ParsedParticleHasScale = true;
                ParsedParticleScale = Scale;
            }

            if ((Flags & FlagHasName) != 0)
            {
                string name = UnpackName();
                ParsedParticleName = name.Length == 0 ? null : name;
            }

            if ((Flags & FlagHasBeamStyle) != 0)
            {
                ParsedHasBeamStyle = true;
                ParsedBeamStyle = (BeamStyle)Style;
            }
        }

        private string UnpackName()
        {
            Span<char> buf = stackalloc char[24];
            int n = 0;
            n += Unpack4(N0, buf, n);
            n += Unpack4(N1, buf, n);
            n += Unpack4(N2, buf, n);
            n += Unpack4(N3, buf, n);
            n += Unpack4(N4, buf, n);
            n += Unpack4(N5, buf, n);
            return n == 0 ? "" : new string(buf.Slice(0, n));
        }

        private static int Pack4(string s, int offset, int length)
        {
            int packed = 0;
            for (int i = 0; i < 4; i++)
            {
                int idx = offset + i;
                if (idx >= length) break;
                packed |= (s[idx] & 0xFFFF) << (16 * i);
            }

            return packed;
        }

        private static int Unpack4(int packed, Span<char> dest, int offset)
        {
            int written = 0;
            for (int i = 0; i < 4; i++)
            {
                char c = (char)((packed >> (16 * i)) & 0xFFFF);
                if (c == 0) break;
                dest[offset + written] = c;
                written++;
            }

            return written;
        }
    }
#endif

    internal static bool TryParse(string? token, out Kind kind)
    {
        kind = default;
        ClearParticleParse();
        ClearBeamParse();
        if (string.IsNullOrWhiteSpace(token)) return false;
        var parts = token.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 1)
        {
            switch (parts[0].ToLowerInvariant())
            {
                case "image": kind = Kind.Image; return true;
                case "particle": kind = Kind.Particle; return true;
                case "beam": kind = Kind.Beam; return true;
                case "vignette": kind = Kind.Vignette; return true;
                case "off": kind = Kind.Off; return true;
                default: return false;
            }
        }

        if (parts.Length >= 2 && string.Equals(parts[0], "particle", StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(parts[1], "menu", StringComparison.OrdinalIgnoreCase))
            {
                if (parts.Length != 2) return false;
                kind = Kind.ParticleMenu;
                return true;
            }

            if (!TryParseParticleExtras(parts, out var name, out var scale, out var hasScale))
                return false;
            ParsedParticleName = name;
            ParsedParticleScale = scale;
            ParsedParticleHasScale = hasScale;
            kind = Kind.Particle;
            return true;
        }

        if (parts.Length >= 2 && string.Equals(parts[0], "beam", StringComparison.OrdinalIgnoreCase))
        {
            if (parts.Length == 2 && string.Equals(parts[1], "menu", StringComparison.OrdinalIgnoreCase))
            {
                kind = Kind.BeamMenu;
                return true;
            }

            if (!TryParseBeamStyle(parts[1], out var style))
                return false;
            ParsedBeamStyle = style;
            ParsedHasBeamStyle = true;
            if (parts.Length == 2)
            {
                kind = Kind.Beam;
                return true;
            }

            if (parts.Length == 3 && string.Equals(parts[2], "menu", StringComparison.OrdinalIgnoreCase))
            {
                kind = Kind.BeamMenu;
                return true;
            }

            return false;
        }

        if (parts.Length == 2 && string.Equals(parts[1], "menu", StringComparison.OrdinalIgnoreCase))
        {
            switch (parts[0].ToLowerInvariant())
            {
                case "beam": kind = Kind.BeamMenu; return true;
                default: return false;
            }
        }

        return false;
    }

    /// <summary>
    /// Extra tokens after <c>particle</c> come from SampleCommands' <c>where</c> string
    /// (joined onto <c>what</c>). <c>.fx particle embers 4</c> is name + scale; a lone
    /// number is a scale for the default Flame+Embers combo; a lone name picks one.
    /// </summary>
    private static bool TryParseParticleExtras(
        string[] parts,
        out string? name,
        out float scale,
        out bool hasScale)
    {
        name = null;
        scale = 0f;
        hasScale = false;
        if (parts.Length == 2)
        {
            if (TryParseScale(parts[1], out scale))
            {
                hasScale = true;
                return true;
            }

            name = parts[1];
            return true;
        }

        if (parts.Length == 3)
        {
            if (TryParseScale(parts[1], out _)) return false;
            if (!TryParseScale(parts[2], out scale)) return false;
            name = parts[1];
            hasScale = true;
            return true;
        }

        return false;
    }

    private static bool TryParseScale(string token, out float scale) =>
        float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out scale)
        && !float.IsNaN(scale)
        && !float.IsInfinity(scale);

    private static void ClearParticleParse()
    {
        ParsedParticleName = null;
        ParsedParticleScale = 0f;
        ParsedParticleHasScale = false;
    }

    private static void ClearBeamParse()
    {
        ParsedBeamStyle = BeamStyle.Thin;
        ParsedHasBeamStyle = false;
    }

    private static bool TryParseBeamStyle(string token, out BeamStyle style)
    {
        style = BeamStyle.Thin;
        switch (token.ToLowerInvariant())
        {
            case "thin": style = BeamStyle.Thin; return true;
            case "red": style = BeamStyle.GameRed; return true;
            case "blue": style = BeamStyle.GameBlue; return true;
            default: return false;
        }
    }

    private static string StyleToken(BeamStyle style) =>
        style switch
        {
            BeamStyle.GameRed => "red",
            BeamStyle.GameBlue => "blue",
            _ => "thin",
        };

    internal static void AttachForTests(object owner, string pluginId)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _pluginId = string.IsNullOrWhiteSpace(pluginId) ? "progenitor.sample" : pluginId;
        _borrow = (k, n) => Assets.Borrow(k, n, _owner, _pluginId);
        _host = (h, k) => Assets.Host(h, k);
        _beam = s => Assets.Beam(s, _owner, _pluginId);
        ClearParticleParse();
        ClearBeamParse();
    }

#if !PROGENITOR_TESTS
    internal static void Attach(PluginContext ctx)
    {
        if (ctx == null) throw new ArgumentNullException(nameof(ctx));
        _owner = ctx.Owner;
        _pluginId = ctx.Guid;
        _borrow = (k, n) => ctx.Effects.Borrow(k, n);
        _host = (h, k) => ctx.Effects.Host(h, k);
        _beam = s => ctx.Effects.Beam(s);
        if (ctx.IsClient)
            BindReceive(ctx);
    }

    private static unsafe void BindReceive(PluginContext ctx) =>
        ctx.Net.OnReceive(EventId, OnReceive);

    internal static unsafe void OnReceive(int eventId, byte* payload, int length)
    {
        try
        {
            if (!CustomEvents.TryRead<Packet>(payload, length, out var packet))
            {
                Log("receive ignored (bad payload)");
                return;
            }

            if (!TryFromWire(packet.Value, out var kind))
            {
                Log("receive ignored (unknown kind " + packet.Value + ")");
                return;
            }

            packet.UnpackIntoParse();
            EffectVec3 start = default;
            EffectVec3 end = default;
            if (kind is Kind.Beam or Kind.BeamMenu)
                TryBeamEnds(6f, out start, out end);
            else if (kind == Kind.Particle)
                TryParticleOrigin(out start);
            Run(kind, start, end);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("Sample: fx receive threw: " + e);
        }
    }

    private static bool TryLocalCharacterPose(out float3 position, out float3 forward)
    {
        position = default;
        forward = new float3(1f, 0f, 0f);
        try
        {
            var world = Worlds.Client;
            if (world == null || !world.IsCreated)
                return false;

            var em = world.EntityManager;
            var users = Query.Entities(
                em,
                includeDisabled: false,
                includeSystems: false,
                ComponentIndex.Of<User>(ComponentType.AccessMode.ReadOnly));
            for (int i = 0; i < users.Count; i++)
            {
                var user = em.GetComponentData<User>(users[i]);
                var ch = user.LocalCharacter._Entity;
                if (ch == Entity.Null) continue;

                float3 pos;
                // Facing fallback is +X (task EK); LocalToWorld.Forward when the character has it.
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
                    continue;
                }

                position = pos;
                forward = fwd;
                return true;
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Sample: fx pose: " + e.Message);
        }

        return false;
    }

    private static void TryBeamEnds(float metres, out EffectVec3 start, out EffectVec3 end)
    {
        start = default;
        end = new EffectVec3(metres, 0f, 0f);
        if (!TryLocalCharacterPose(out var pos, out var fwd))
        {
            Log("beam pose missing; start=origin end=+X " + metres.ToString("0.##", CultureInfo.InvariantCulture) + "m");
            return;
        }

        var far = pos + math.normalizesafe(fwd) * metres;
        start = new EffectVec3(pos.x, pos.y, pos.z);
        end = new EffectVec3(far.x, far.y, far.z);
    }

    /// <summary>
    /// Follow the local character; the far end is the nearest <c>Health</c> unit within
    /// 30 m, else a point 6 m ahead. Offsets raise the line off the ground. Cheap:
    /// one <see cref="Query.Entities"/> at play time, then Core tracks per late frame.
    /// </summary>
    private static bool TryTrackBeam(SequenceEffectHandle handle)
    {
        if (!TryLocalCharacter(out var character, out var pos, out var fwd))
            return false;

        var startOff = new float3(0f, 1f, 0f);
        if (TryNearestUnit(character, pos, out var unit))
        {
            handle.Track(character, unit, startOff, new float3(0f, 0.5f, 0f));
            Log("beam track character -> unit " + unit.Index + ":" + unit.Version);
            return true;
        }

        var far = pos + math.normalizesafe(fwd) * 6f;
        handle.Track(character, far, startOff);
        Log("beam track character -> 6m ahead");
        return true;
    }

    private static bool TryLocalCharacter(out Entity character, out float3 position, out float3 forward)
    {
        character = Entity.Null;
        position = default;
        forward = new float3(1f, 0f, 0f);
        try
        {
            var world = Worlds.Client;
            if (world == null || !world.IsCreated)
                return false;

            var em = world.EntityManager;
            var users = Query.Entities(
                em,
                includeDisabled: false,
                includeSystems: false,
                ComponentIndex.Of<User>(ComponentType.AccessMode.ReadOnly));
            for (int i = 0; i < users.Count; i++)
            {
                var user = em.GetComponentData<User>(users[i]);
                var ch = user.LocalCharacter._Entity;
                if (ch == Entity.Null) continue;
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
                    continue;
                }

                character = ch;
                position = pos;
                forward = fwd;
                return true;
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Sample: fx pose: " + e.Message);
        }

        return false;
    }

    private static bool TryNearestUnit(Entity character, float3 from, out Entity unit)
    {
        unit = Entity.Null;
        try
        {
            var world = Worlds.Client;
            if (world == null || !world.IsCreated)
                return false;

            var em = world.EntityManager;
            var prefabType = ComponentIndex.Of<Prefab>(ComponentType.AccessMode.ReadOnly);
            var list = Query.Entities(
                em,
                includeDisabled: false,
                includeSystems: false,
                ComponentIndex.Of<Health>(ComponentType.AccessMode.ReadOnly),
                ComponentIndex.Of<Translation>(ComponentType.AccessMode.ReadOnly));
            float best = 30f * 30f;
            bool found = false;
            for (int i = 0; i < list.Count; i++)
            {
                var e = list[i];
                if (e == character) continue;
                if (em.HasComponent(e, prefabType)) continue;
                float3 p;
                if (e.Has<LocalToWorld>(em))
                    p = em.GetComponentData<LocalToWorld>(e).Position;
                else
                    p = em.GetComponentData<Translation>(e).Value;
                float dx = p.x - from.x;
                float dy = p.y - from.y;
                float dz = p.z - from.z;
                float d2 = dx * dx + dy * dy + dz * dz;
                if (d2 >= best) continue;
                best = d2;
                unit = e;
                found = true;
            }

            return found;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Sample: fx nearest unit: " + e.Message);
            unit = Entity.Null;
            return false;
        }
    }

    private static void TryParticleOrigin(out EffectVec3 origin)
    {
        if (TryLocalCharacterPose(out var pos, out _))
        {
            origin = new EffectVec3(pos.x, pos.y, pos.z);
            return;
        }

        origin = default;
        Log("particle pose missing; origin");
    }
#endif

    internal static void Detach()
    {
        Off();
        _owner = null;
        _borrow = null;
        _host = null;
        _beam = null;
        ImageSeconds = 5;
        ParticleSeconds = 5;
        BeamSeconds = 5;
        VignetteSeconds = 3;
        ClearParticleParse();
        ClearBeamParse();
    }

    internal static void Run(Kind kind, EffectVec3 start = default, EffectVec3 end = default)
    {
        if (kind == Kind.Off)
        {
            Off();
            return;
        }

        try
        {
            switch (kind)
            {
                case Kind.Image: PlayImage(); break;
                case Kind.Particle: PlayParticle(menu: false, start); break;
                case Kind.ParticleMenu: PlayParticle(menu: true, start); break;
                case Kind.Beam: PlayBeam(start, end, menu: false); break;
                case Kind.BeamMenu: PlayBeam(start, end, menu: true); break;
                case Kind.Vignette: PlayVignette(); break;
            }
        }
        catch (Exception e)
        {
            Log("run " + kind + " threw: " + e.Message);
        }
    }

    internal static int Off()
    {
        IDisposable[] snapshot;
        lock (Gate)
        {
            snapshot = Live.ToArray();
            Live.Clear();
        }

        int n = 0;
        for (int i = 0; i < snapshot.Length; i++)
        {
            try
            {
                snapshot[i].Dispose();
                n++;
            }
            catch (Exception e)
            {
                Log("off dispose threw: " + e.Message);
            }
        }

        Log("off dispose " + n + " handle(s)");
        return n;
    }

    internal static int LiveCount
    {
        get { lock (Gate) return Live.Count; }
    }

    private static void PlayImage()
    {
        Log("image borrow " + ImageName);
        var handle = BorrowAs<UiImageHandle>(AssetKind.Image, ImageName);
        handle.Color = Gold;
        handle.Size = ImageSize;
        handle.Position = new EffectVec2(0f, 0f);
        handle.SortingOrder = 5000;
        Log("image color gold size=192 center");
        Host(handle, HostKind.Overlay);
        Remember(handle);
        Log("image host Overlay");
        ScheduleDispose(handle, ImageSeconds, "image");
    }

    private static void PlayParticle(bool menu, EffectVec3 origin)
    {
        if (menu)
        {
            if (!TryHostSized("particle"))
                return;
        }

        if (!string.IsNullOrEmpty(ParsedParticleName))
        {
            float scale = ParsedParticleHasScale ? ParsedParticleScale : DefaultNamedScale;
            PlayOneParticle(ParsedParticleName, scale, menu, origin);
            return;
        }

        float flameScale = ParsedParticleHasScale ? ParsedParticleScale : DefaultFlameScale;
        float embersScale = ParsedParticleHasScale ? ParsedParticleScale : DefaultEmbersScale;
        PlayOneParticle(FlameName, flameScale, menu, origin);
        PlayOneParticle(ParticleName, embersScale, menu, origin);
    }

    private static void PlayOneParticle(string name, float scale, bool menu, EffectVec3 origin)
    {
        Log("particle borrow " + name);
        var raw = Borrow(AssetKind.Particle, name);
        if (raw is not ParticleHandle handle || !handle.Enabled)
        {
            Log("particle borrow " + name + " disabled");
            try { raw.Dispose(); }
            catch (Exception e) { Log("particle disabled dispose threw: " + e.Message); }
            return;
        }

        handle.Tint = Gold;
        handle.Scale(scale);
        handle.Play();
        Log("particle scale " + scale.ToString("0.##", CultureInfo.InvariantCulture));
        Log("particle tint gold");
        if (menu)
        {
            Host(handle, HostKind.RenderTexture);
            Remember(handle);
            Log("particle host RenderTexture");
        }
        else
        {
            handle.Position = origin;
            Remember(handle);
            Log("particle world " + origin);
        }

        ScheduleDispose(handle, ParticleSeconds, "particle");
    }

    private static void PlayBeam(EffectVec3 start, EffectVec3 end, bool menu)
    {
        var style = ParsedHasBeamStyle ? ParsedBeamStyle : Assets.PreferredBeamStyle();
        SequenceEffectHandle handle = _beam != null
            ? _beam(style)
            : BorrowAs<SequenceEffectHandle>(AssetKind.Sequence, BeamName);
        Log("beam style " + StyleToken(handle.Enabled ? handle.Style : style));
        Log("beam borrow " + handle.Name);
#if !PROGENITOR_TESTS
        if (!TryTrackBeam(handle))
#endif
        {
            handle.Start = start;
            handle.End = end;
        }

        if (handle.Style == BeamStyle.Thin || handle.Style == BeamStyle.GameBlue)
        {
            handle.Tint = Blue;
            Log("beam tint blue");
        }
        else
            Log("beam tint preset");
        Log("beam start=" + start + " end=" + end);
        if (menu && TryHostSized("beam"))
        {
            Host(handle, HostKind.RenderTexture);
            Log("beam host RenderTexture");
        }

        Remember(handle);
        ScheduleDispose(handle, BeamSeconds, "beam");
    }

    /// <summary>
    /// Overlay RawImage host, but only when the module can size and place it. Otherwise the
    /// look-along fallback fills the screen (evidence 89); log the follow-up and skip Host.
    /// </summary>
    private static bool TryHostSized(string label)
    {
        if (HostApiCanSizeRawImage)
            return true;

        Log(label + " menu needs a sized host (module follow-up)");
        return false;
    }

    private static void PlayVignette()
    {
        Log("vignette borrow");
        var handle = BorrowAs<VolumeHandle>(AssetKind.Volume, VolumeName);
        handle.Tint = VignetteGold;
        handle.Intensity = VignetteIntensity;
        handle.Smoothness = VignetteSmoothness;
        Log("vignette tint gold intensity=" + VignetteIntensity.ToString("0.##", CultureInfo.InvariantCulture)
            + " smoothness=" + VignetteSmoothness.ToString("0.##", CultureInfo.InvariantCulture)
            + " (camera-wide; the game's own post effects can dominate)");
        Remember(handle);
        ScheduleDispose(handle, VignetteSeconds, "vignette");
    }

    private static T BorrowAs<T>(AssetKind kind, string name) where T : class, IEffectHandle
    {
        var raw = Borrow(kind, name);
        if (raw is T typed) return typed;
        throw new InvalidOperationException(
            "Sample fx expected " + typeof(T).Name + " for " + kind + " '" + name + "', got "
            + raw.GetType().Name);
    }

    private static IEffectHandle Borrow(AssetKind kind, string name)
    {
        var fn = _borrow;
        if (fn != null) return fn(kind, name);
        return Assets.Borrow(kind, name, _owner, _pluginId);
    }

    private static IHostHandle Host(IEffectHandle handle, HostKind kind)
    {
        var fn = _host;
        if (fn != null) return fn(handle, kind);
        return Assets.Host(handle, kind);
    }

    private static void Remember(IDisposable handle)
    {
        lock (Gate)
            Live.Add(handle);
    }

    private static void ScheduleDispose(IDisposable handle, double seconds, string label)
    {
        Log(label + " dispose in " + seconds.ToString("0.##", CultureInfo.InvariantCulture) + "s");
        try
        {
            MainThread.PostDelayed(seconds, () =>
            {
                try
                {
                    Forget(handle);
                    handle.Dispose();
                    Log(label + " disposed");
                }
                catch (Exception e)
                {
                    Log(label + " timer dispose threw: " + e.Message);
                }
            }, _owner);
        }
        catch (Exception e)
        {
            Log(label + " PostDelayed threw: " + e.Message);
        }
    }

    private static void Forget(IDisposable handle)
    {
        lock (Gate)
            Live.Remove(handle);
    }

    private static bool TryFromWire(int value, out Kind kind)
    {
        kind = (Kind)value;
        return kind is Kind.Image or Kind.Particle or Kind.Beam or Kind.Vignette or Kind.Off
            or Kind.BeamMenu or Kind.ParticleMenu;
    }

    internal static void Log(string step)
    {
        string line = "Sample: fx " + step;
        try
        {
#if PROGENITOR_TESTS
            global::Progenitor.Plugin.Logger?.LogInfo(line);
#else
            Plugin.Logger?.LogInfo(line);
#endif
        }
        catch
        {
            // Logger can be torn down during unload.
        }
    }
}
