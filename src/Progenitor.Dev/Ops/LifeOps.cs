using System;
using System.Globalization;
using System.Text.Json;
using ProjectM;
using ProjectM.Network;
using Progenitor.Ecs;
using Unity.Entities;
using Unity.Mathematics;

namespace Progenitor.Dev.Ops;

/// <summary>
/// Server-side death and respawn. Damage goes through <see cref="DealDamageEvent"/>,
/// <see cref="Kill"/> goes through <c>StatChangeUtility.KillEntity</c> (the health kill path
/// that writes <c>Health.IsDead</c> and forwards to <c>DeathUtilities.Kill</c>), the downed wait
/// is finished by <see cref="GiveUpReviveEvent"/> or by rewriting the downed buff's
/// <see cref="LifeTime"/>, and the spawn is <see cref="CharacterRespawnEvent"/>.
/// <see cref="ServerBootstrapSystem.RespawnCharacter"/> is never called.
/// </summary>
public static class LifeOps
{
    public static object Damage(World world, JsonElement args)
    {
        RequireServer(world);
        var character = ResolveVictim(world.EntityManager, args);
        float amount = ReadFloat(args, "amount", 100000f);
        if (amount <= 0f)
            throw new ArgumentException("args.amount must be greater than 0");
        var source = ReadOptionalEntity(world.EntityManager, args, "source") ?? character;

        string mode = ReadString(args, "mode") ?? "deal";
        if (string.Equals(mode, "statChange", StringComparison.OrdinalIgnoreCase)
            || string.Equals(mode, "statchange", StringComparison.OrdinalIgnoreCase))
        {
            // Live 2026-09-30: DealDamageEvent can lower Health.Value without publishing
            // DamageDealt (StatChangeSystem never sees a StatChangeEvent). This mode creates
            // the Health StatChangeEvent the combat bus After-hook reads.
            float delta = -amount;
            var createdSc = StatChangeEvent.CreateStatChangeEvent(
                world.EntityManager,
                StatType.Health,
                character,
                delta,
                0,
                StatChangeReason.DealDamageSystem_0,
                source);
            Plugin.Logger?.LogInfo(
                "life: damage mode=statChange " + delta.ToString(CultureInfo.InvariantCulture)
                + " on " + Format(character) + " event " + Format(createdSc));
            return new
            {
                action = "damage",
                mode = "statChange",
                character = Format(character),
                amount,
                change = delta,
                source = Format(source),
                @event = Format(createdSc),
            };
        }


        // A zero modifier makes GetDamage multiply the raw amount by 0. A value of
        // 100 multiplied the raw amount by 100 on TestPlayer, so 1 means one hit point.
        // 1 means the raw amount is hit points for that victim category. A zero
        // category zeroes the hit, which is why a vampire-only modifier never moved
        // a human or beast boss.
        var modifiers = default(EntityTypeModifiers);
        modifiers.Human = 1f;
        modifiers.Undead = 1f;
        modifiers.Demon = 1f;
        modifiers.Mechanical = 1f;
        modifiers.Beast = 1f;
        modifiers.CastleObject = 1f;
        modifiers.PlayerVampire = 1f;
        modifiers.PvEVampire = 1f;
        modifiers.ShadowVBlood = 1f;
        modifiers.Wood = 1f;
        modifiers.Minerals = 1f;
        modifiers.Vegetation = 1f;
        modifiers.LightArmor = 1f;
        modifiers.VBlood = 1f;
        modifiers.Magic = 1f;
        modifiers.Explosives = 1f;
        modifiers.MassiveResource = 1f;
        modifiers.MonsterGate = 1f;
        var created = DealDamageEvent.CreateDealDamageEvent(
            world.EntityManager,
            character,
            MainDamageType.Physical,
            0f,
            0f,
            modifiers,
            source,
            amount,
            0f,
            1f,
            (int)DealDamageFlag.None);

        Plugin.Logger?.LogInfo(
            "life: damage " + amount.ToString(CultureInfo.InvariantCulture)
            + " on " + Format(character) + " event " + Format(created));
        return new
        {
            action = "damage",
            character = Format(character),
            amount,
            source = Format(source),
            @event = Format(created),
        };
    }


    /// <summary>
    /// Kill a disposable unit through the game's health kill path:
    /// <c>StatChangeUtility.KillEntity(EntityManager, Entity, double, StatChangeReason)</c>.
    /// That overload forwards to the 11-arg body which writes
    /// <c>Health.IsDead</c> and then <c>DeathUtilities.Kill</c> — the same choke point
    /// <c>Progenitor.Combat</c> postfixes for <c>UnitDied</c>/<c>PlayerDied</c>. Prefer this
    /// over <see cref="Damage"/> when proving the death bus: DealDamageEvent can create an
    /// event that never applies to some spawned NPCs (material modifier / flag path).
    /// </summary>
    public static object Kill(World world, JsonElement args)
    {
        RequireServer(world);
        var em = world.EntityManager;
        var victim = ResolveVictim(em, args);
        if (!em.Exists(victim))
            throw new InvalidOperationException("entity " + Format(victim) + " does not exist");
        if (em.HasComponent<PlayerCharacter>(victim)
            && !TryAllowDisposablePlayerKill(em, victim, out string refuseWhy))
            throw new InvalidOperationException(refuseWhy);

        // currentTime is recorded on Dead / Health.TimeOfDeath. world.Time.ElapsedTime is not
        // reliably populated on the dedicated-server world from Dev (observed ~0 / denormal);
        // 0 is accepted by KillEntity and is enough for the death-bus smoke.
        const double now = 0d;

        bool disposablePlayer = em.HasComponent<PlayerCharacter>(victim);

        // 4-arg overload: killImmortals forced true, killer = Null.
        StatChangeUtility.KillEntity(em, victim, now, StatChangeReason.DealDamageSystem_0);

        // Live 2026-09-30 (headless): KillEntity alone sets Health.IsDead on players but does
        // not call DeathUtilities.Kill (sheep UnitDied still fires). Force the choke point for
        // gated disposable players only. Dead.DoNotDestroy=true: without it the game spam-logs
        // "Vampire ... tagged as destroy ... declined if in live" every frame (HeadlessOne).
        if (disposablePlayer)
        {
            var dead = new Dead
            {
                ServerTimeOfDeath = now,
                DestroyAfterDuration = 0f,
                Killer = Entity.Null,
                KillerSource = Entity.Null,
                DoNotDestroy = true,
                StatChangeReason = StatChangeReason.DealDamageSystem_0,
            };
            var deathEvent = new DeathEvent
            {
                Died = victim,
                Killer = Entity.Null,
                Source = Entity.Null,
                StatChangeReason = StatChangeReason.DealDamageSystem_0,
            };
            var deathReason = new DeathReason { PrefabGuid = default };
            DeathUtilities.Kill(em, victim, dead, deathEvent, deathReason);
            Plugin.Logger?.LogInfo("life: kill forced DeathUtilities.Kill for disposable player " + Format(victim));
        }


        bool exists = em.Exists(victim);
        bool isDead = false;
        float value = 0f;
        if (exists && em.HasComponent<Health>(victim))
        {
            var health = em.GetComponentData<Health>(victim);
            isDead = health.IsDead;
            value = health.Value;
        }

        Plugin.Logger?.LogInfo(
            "life: kill " + Format(victim)
            + " exists=" + exists
            + " isDead=" + isDead
            + " health=" + value.ToString(CultureInfo.InvariantCulture)
            + " t=" + now.ToString("0.###", CultureInfo.InvariantCulture));
        return new
        {
            action = "kill",
            entity = Format(victim),
            exists,
            isDead,
            health = value,
            elapsedTime = now,
        };
    }

    public static object Downed(World world, JsonElement args)
    {
        RequireServer(world);
        var em = world.EntityManager;
        var (user, character) = ResolveUserAndCharacter(em, args);
        string action = ReadString(args, "action") ?? "finish";
        if (string.Equals(action, "seconds", StringComparison.OrdinalIgnoreCase))
        {
            if (!args.TryGetProperty("seconds", out var secondsEl) || secondsEl.ValueKind != JsonValueKind.Number)
                throw new ArgumentException("args.seconds is required when action is seconds");
            float seconds = secondsEl.GetSingle();
            if (seconds < 0f)
                throw new ArgumentException("args.seconds must be >= 0");
            var buff = FindDownedBuff(em, character);
            if (buff == Entity.Null)
                throw new InvalidOperationException("character " + Format(character) + " has no VampireDownedBuff");
            float age = 0f;
            if (em.HasComponent<Age>(buff))
                age = em.GetComponentData<Age>(buff).Value;
            var life = em.GetComponentData<LifeTime>(buff);
            float before = life.Duration;
            life.Duration = age + seconds;
            em.SetComponentData(buff, life);
            Plugin.Logger?.LogInfo(
                "life: downed timer " + Format(character) + " buff " + Format(buff)
                + " duration " + before.ToString(CultureInfo.InvariantCulture)
                + " -> " + life.Duration.ToString(CultureInfo.InvariantCulture));
            return new
            {
                action = "seconds",
                character = Format(character),
                buff = Format(buff),
                age,
                duration = new { before, after = life.Duration },
            };
        }

        if (!string.Equals(action, "finish", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("args.action must be finish or seconds");

        var ev = CreateFromCharacterEvent<GiveUpReviveEvent>(em, user, character);
        Plugin.Logger?.LogInfo("life: give up " + Format(character) + " event " + Format(ev));
        return new { action = "finish", character = Format(character), @event = Format(ev) };
    }

    public static object Respawn(World world, JsonElement args)
    {
        RequireServer(world);
        var em = world.EntityManager;
        var (user, character) = ResolveUserAndCharacter(em, args);
        var location = ParseLocation(ReadString(args, "location") ?? "waypoint");
        bool hasPoint = TryPoint(args, out float px, out float py, out float pz);
        if (hasPoint && !Progenitor.Players.PlayerRespawn.Bind(world, user, px, py, pz, out string bindWhy))
            throw new InvalidOperationException(bindWhy);
        bool indexGiven = false;
        int index = 0;
        if (args.ValueKind == JsonValueKind.Object
            && args.TryGetProperty("index", out var indexEl)
            && indexEl.ValueKind == JsonValueKind.Number)
        {
            indexGiven = true;
            index = indexEl.GetInt32();
        }
        // A bound point (2026-10-11) no longer rides on a native respawn-list entry, which broke the
        // client's death screen: the revive goes where location/index say, then a bound user is
        // teleported to the point. Only this op does that; a normal respawn is never changed.
        bool bound = Progenitor.Players.PlayerRespawn.TryBoundPoint(world, user, out float bx, out float by, out float bz, out _);
        _ = indexGiven;

        var ev = em.CreateEntity(
            ComponentType.ReadWrite<CharacterRespawnEvent>(),
            ComponentType.ReadWrite<FromCharacter>());
        em.SetComponentData(ev, new CharacterRespawnEvent
        {
            SpawnLocationType = location,
            SpawnOptionIndex = index,
            SpawnLocationIcon = NetworkId.Empty,
        });
        em.SetComponentData(ev, new FromCharacter { User = user, Character = character });
        if (bound)
            TeleportAfterRespawn(world, user, new float3(bx, by, bz), attemptsLeft: 10);
        Plugin.Logger?.LogInfo(
            "life: respawn " + Format(character) + " location " + location + " index " + index
            + " event " + Format(ev)
            + (bound ? " bound-point" : ""));
        return new
        {
            action = "respawn",
            character = Format(character),
            location = location.ToString(),
            index,
            bound,
            @event = Format(ev),
        };
    }

    /// <summary>
    /// Once the revive has replaced the character (no <c>Dead</c> on the user's current
    /// character), teleport it to the bound point. Retries once a second, up to
    /// <paramref name="attemptsLeft"/> times.
    /// </summary>
    private static void TeleportAfterRespawn(World world, Entity user, float3 point, int attemptsLeft)
    {
        MainThread.PostDelayed(1, () =>
        {
            try
            {
                if (!world.IsCreated) return;
                var em = world.EntityManager;
                if (!em.Exists(user)) return;
                var character = em.GetComponentData<User>(user).LocalCharacter._Entity;
                bool ready = character != Entity.Null && em.Exists(character) && !em.HasComponent(character, ComponentIndex.Of<Dead>(ComponentType.AccessMode.ReadOnly));
                if (!ready)
                {
                    if (attemptsLeft > 1) TeleportAfterRespawn(world, user, point, attemptsLeft - 1);
                    else Plugin.Logger?.LogWarning("life: respawn bound-point teleport gave up: the character did not come back");
                    return;
                }
                var result = Progenitor.Players.PlayerTeleport.Teleport(em, character, point, out string why);
                Plugin.Logger?.LogInfo("life: respawn bound-point teleport " + Format(character) + " -> "
                    + point.x + "," + point.y + "," + point.z + " " + (result ?? why));
            }
            catch (Exception e)
            {
                Plugin.Logger?.LogWarning("life: respawn bound-point teleport threw: " + e.Message);
            }
        });
    }

    /// <summary>
    /// Optional point for this user. All three coordinates bind that user (test tooling): this op
    /// teleports them there after the revive.
    /// </summary>
    private static bool TryPoint(JsonElement args, out float x, out float y, out float z)
    {
        x = y = z = 0f;
        if (args.ValueKind != JsonValueKind.Object) return false;
        bool hx = args.TryGetProperty("x", out var xe) && xe.ValueKind == JsonValueKind.Number;
        bool hy = args.TryGetProperty("y", out var ye) && ye.ValueKind == JsonValueKind.Number;
        bool hz = args.TryGetProperty("z", out var ze) && ze.ValueKind == JsonValueKind.Number;
        if (!hx && !hy && !hz) return false;
        if (!hx || !hy || !hz)
            throw new ArgumentException("respawn x, y, and z must be given together");
        x = xe.GetSingle();
        y = ye.GetSingle();
        z = ze.GetSingle();
        return true;
    }

    private static Entity CreateFromCharacterEvent<T>(EntityManager em, Entity user, Entity character)
        where T : struct
    {
        // GiveUpReviveEvent is a zero-sized tag. SetComponentData rejects those.
        var ev = em.CreateEntity(ComponentType.ReadWrite<T>(), ComponentType.ReadWrite<FromCharacter>());
        em.SetComponentData(ev, new FromCharacter { User = user, Character = character });
        return ev;
    }

    private static Entity FindDownedBuff(EntityManager em, Entity character)
    {
        if (!em.HasComponent<BuffBuffer>(character))
            return Entity.Null;
        var buffer = em.GetBuffer<BuffBuffer>(character);
        for (int i = 0; i < buffer.Length; i++)
        {
            var buff = buffer[i].Entity;
            if (buff == Entity.Null || !em.Exists(buff))
                continue;
            if (em.HasComponent<VampireDownedBuff>(buff) && em.HasComponent<LifeTime>(buff))
                return buff;
        }
        return Entity.Null;
    }

    private static SpawnLocationType ParseLocation(string text)
    {
        if (string.Equals(text, "crypt", StringComparison.OrdinalIgnoreCase))
            return SpawnLocationType.Crypt;
        if (string.Equals(text, "wooden", StringComparison.OrdinalIgnoreCase)
            || string.Equals(text, "woodencoffin", StringComparison.OrdinalIgnoreCase))
            return SpawnLocationType.WoodenCoffin;
        if (string.Equals(text, "stone", StringComparison.OrdinalIgnoreCase)
            || string.Equals(text, "stonecoffin", StringComparison.OrdinalIgnoreCase))
            return SpawnLocationType.StoneCoffin;
        if (string.Equals(text, "waypoint", StringComparison.OrdinalIgnoreCase)
            || string.Equals(text, "waygate", StringComparison.OrdinalIgnoreCase))
            return SpawnLocationType.Waypoint;
        throw new ArgumentException(
            "args.location must be crypt, wooden, stone, or waypoint (got '" + text + "')");
    }


    /// <summary>
    /// Dev kill stays refused for real Steam players (TestPlayer). Allowed only for disposable
    /// fixtures: ghost-user platform ids, extra-instance fake ids, or <c>User.IsBot</c>.
    /// Still uses <c>StatChangeUtility.KillEntity</c> so <c>PlayerDied</c> publishes.
    /// </summary>
    private static bool TryAllowDisposablePlayerKill(EntityManager em, Entity victim, out string why)
    {
        why = "kill refuses players (disposable ghost/extra-instance/IsBot only); entity " + Format(victim);
        try
        {
            if (!em.HasComponent<PlayerCharacter>(victim))
            {
                why = "not a player";
                return false;
            }
            var pc = em.GetComponentData<PlayerCharacter>(victim);
            var userEntity = pc.UserEntity;
            if (userEntity == Entity.Null || !em.Exists(userEntity) || !em.HasComponent<User>(userEntity))
            {
                why = "kill refuses player without User; entity " + Format(victim);
                return false;
            }
            var user = em.GetComponentData<User>(userEntity);
            if (GhostOps.IsGhostPlatformId(user.PlatformId)
                || ExtraInstanceAuth.IsOwnPlatformId(user.PlatformId)
                || user.IsBot)
            {
                why = "";
                Plugin.Logger?.LogInfo(
                    "life: kill allowing disposable player " + Format(victim)
                    + " user=" + Format(userEntity)
                    + " platformId=" + user.PlatformId.ToString(CultureInfo.InvariantCulture)
                    + " bot=" + user.IsBot);
                return true;
            }
            why = "kill refuses non-disposable player (not ghost/extra-instance/IsBot); entity "
                + Format(victim) + " platformId=" + user.PlatformId.ToString(CultureInfo.InvariantCulture);
            return false;
        }
        catch (Exception e)
        {
            why = "kill refuses player (identity check failed): " + e.Message;
            return false;
        }
    }
    private static void RequireServer(World world)
    {
        if (!Progenitor.Plugin.IsServerProcess)
            throw new InvalidOperationException("life ops run on the server side only");
        if (world == null || !world.IsCreated)
            throw new InvalidOperationException("the game world does not exist yet (not in a game)");
    }

    /// <summary>
    /// A player or user resolves to the character, same as <see cref="ResolveCharacter"/>.
    /// Any other existing entity is the victim itself, so a boss can be damaged without
    /// being a <c>PlayerCharacter</c>.
    /// </summary>
    private static Entity ResolveVictim(EntityManager em, JsonElement args)
    {
        if (args.ValueKind == JsonValueKind.Object
            && args.TryGetProperty("entity", out var entityEl)
            && entityEl.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(entityEl.GetString()))
        {
            var text = entityEl.GetString()!.Trim();
            var parts = text.Split(':');
            if (parts.Length != 2 || !int.TryParse(parts[0], out var index) || !int.TryParse(parts[1], out var version))
                throw new ArgumentException("args.entity must be 'index:version' (got '" + text + "')");
            var entity = new Entity { Index = index, Version = version };
            if (!em.Exists(entity))
                throw new InvalidOperationException("entity " + text + " does not exist");
            if (em.HasComponent<PlayerCharacter>(entity) || em.HasComponent<User>(entity))
                return ResolveCharacter(em, args);
            return entity;
        }

        return ResolveCharacter(em, args);
    }

    private static (Entity User, Entity Character) ResolveUserAndCharacter(EntityManager em, JsonElement args)
    {
        var character = ResolveCharacter(em, args);
        if (!em.HasComponent<PlayerCharacter>(character))
            throw new InvalidOperationException("entity " + Format(character) + " is not a PlayerCharacter");
        var pc = em.GetComponentData<PlayerCharacter>(character);
        if (pc.UserEntity == Entity.Null || !em.Exists(pc.UserEntity))
            throw new InvalidOperationException("character " + Format(character) + " has no user entity");
        return (pc.UserEntity, character);
    }

    private static Entity ResolveCharacter(EntityManager em, JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("args.user (character name) or args.entity (index:version) is required");

        if (args.TryGetProperty("entity", out var entityEl)
            && entityEl.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(entityEl.GetString()))
        {
            var text = entityEl.GetString()!.Trim();
            var parts = text.Split(':');
            if (parts.Length != 2 || !int.TryParse(parts[0], out var index) || !int.TryParse(parts[1], out var version))
                throw new ArgumentException("args.entity must be 'index:version' (got '" + text + "')");
            var entity = new Entity { Index = index, Version = version };
            if (!em.Exists(entity))
                throw new InvalidOperationException("entity " + text + " does not exist");
            if (em.HasComponent<PlayerCharacter>(entity))
                return entity;
            if (em.HasComponent<User>(entity))
            {
                var character = em.GetComponentData<User>(entity).LocalCharacter._Entity;
                if (character == Entity.Null || !em.Exists(character))
                    throw new InvalidOperationException("user " + text + " has no character entity");
                return character;
            }
            throw new InvalidOperationException("entity " + text + " is neither a User nor a PlayerCharacter");
        }

        if (args.TryGetProperty("user", out var userEl)
            && userEl.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(userEl.GetString()))
        {
            var name = userEl.GetString()!.Trim();
            var users = Query.Entities(em, true, false, ComponentIndex.Of<User>(ComponentType.AccessMode.ReadOnly));
            for (int i = 0; i < users.Count; i++)
            {
                User user;
                try { user = em.GetComponentData<User>(users[i]); }
                catch { continue; }
                if (!string.Equals(user.CharacterName.ToString(), name, StringComparison.OrdinalIgnoreCase))
                    continue;
                var character = user.LocalCharacter._Entity;
                if (character == Entity.Null || !em.Exists(character))
                    continue;
                return character;
            }
            throw new InvalidOperationException("no user named '" + name + "'");
        }

        throw new ArgumentException("args.user (character name) or args.entity (index:version) is required");
    }

    private static string? ReadString(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.String)
            return null;
        var text = el.GetString();
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    private static Entity? ReadOptionalEntity(EntityManager em, JsonElement args, string name)
    {
        var text = ReadString(args, name);
        if (text == null)
            return null;
        var parts = text.Split(':');
        if (parts.Length != 2 || !int.TryParse(parts[0], out var index) || !int.TryParse(parts[1], out var version))
            throw new ArgumentException("args." + name + " must be 'index:version' (got '" + text + "')");
        var entity = new Entity { Index = index, Version = version };
        if (!em.Exists(entity))
            throw new InvalidOperationException("entity " + text + " does not exist");
        return entity;
    }

    private static float ReadFloat(JsonElement args, string name, float fallback)
    {
        if (args.ValueKind == JsonValueKind.Object
            && args.TryGetProperty(name, out var el)
            && el.ValueKind == JsonValueKind.Number)
            return el.GetSingle();
        return fallback;
    }

    private static string Format(Entity entity)
    {
        if (entity == Entity.Null) return "";
        return entity.Index + ":" + entity.Version;
    }
}
