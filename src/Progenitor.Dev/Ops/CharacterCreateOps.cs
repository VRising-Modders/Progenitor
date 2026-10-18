using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;
using Il2CppInterop.Runtime;
using ProjectM;
using ProjectM.Network;
using ProjectM.UI;
using Progenitor.Ecs;
using Unity.Collections;
using Unity.Entities;

namespace Progenitor.Dev.Ops;

/// <summary>
/// Op <c>create_character</c>: create the first character for a user that has none, for headless
/// extra clients, which never show the creation screen.
///
/// <para><b>Client (preferred).</b> On a client with a world, it sends the same
/// <see cref="CreateCharacterEvent"/> network event the creation screen sends
/// (<c>NetworkEventType.EventId = NetworkEvents.EventId_CreateCharacterEvent</c>, 527272789 in both
/// the client and server metadata, plus <c>SendNetworkEventTag</c>), so the client's own join flow sees its character arrive.
/// The server's <c>HandleCreateCharacterEventSystem</c> validates the name and spawns it. The op
/// then waits (deferred, at most <see cref="CharacterCreatePlan.WaitMs"/>) for the character and for
/// the client to leave the creation scene, and takes it off that scene with the game's own calls when
/// the game does not (see <see cref="CharacterCreatePlan"/>). With a character already there it sends
/// nothing (<c>already:true</c>) and only does that cleanup, so running it again repairs a client
/// left on the creation scene.</para>
///
/// <para><b>Server (fallback).</b> Enqueues the inbound event + <see cref="FromCharacter"/> pair for
/// a connected user. The character spawns, but that user's client stays on its creation screen
/// (verified live 2026-09-24: the player saw the creation preview while the compass moved). Run the
/// client form on that client afterwards to clear the scene, or disconnect and reconnect it.</para>
///
/// <para>A new character starts asleep in the crypt, hidden from other players, until its client
/// presses the wake-up key. A headless client cannot; run the server <c>wake</c> op
/// (<see cref="WakeOps"/>).</para>
///
/// <para>Args: <c>name</c> (required, the game's name rules apply); server side also
/// <c>platformId</c> (string or number) or <c>userIndex</c>; optional appearance bytes
/// <c>gender</c>, <c>skin</c>, <c>face</c>, <c>eyes</c>, <c>hairStyle</c>, <c>hairColor</c>,
/// <c>features</c>, <c>accessories</c> (default 0).</para>
/// </summary>
internal static class CharacterCreateOps
{
    /// <summary><c>NetworkEvents.EventId_CreateCharacterEvent</c> (client and server metadata).</summary>
    internal const int CreateCharacterEventId = 527272789;

    public static object Run(World world, JsonElement args)
    {
        var em = world.EntityManager;
        string name = ReadString(args, "name") ?? throw new ArgumentException("args.name is required");
        name = name.Trim();
        if (name.Length == 0 || name.Length > 20)
            throw new ArgumentException("args.name must be 1..20 characters");
        var appearance = ReadAppearance(args, name);
        if (!Progenitor.Plugin.IsServerProcess)
            return RunClient(world, appearance);

        var userEntity = ResolveUser(em, args, out var user);
        if (!user.IsConnected)
            throw new InvalidOperationException($"user {user.Index} is not connected");
        var existing = user.LocalCharacter._Entity;
        if (existing != Entity.Null && em.Exists(existing))
            throw new InvalidOperationException(
                $"user {user.Index} already has character {existing.Index}:{existing.Version} ('{user.CharacterName}')");

        var ev = em.CreateEntity(
            ComponentIndex.Of<CreateCharacterEvent>(ComponentType.AccessMode.ReadWrite),
            ComponentIndex.Of<FromCharacter>(ComponentType.AccessMode.ReadWrite));
        ev.Write(em, appearance);
        em.SetComponentData(ev, new FromCharacter { User = userEntity, Character = Entity.Null });

        Plugin.Logger?.LogInfo(
            $"create_character: user {user.Index} platformId {user.PlatformId} name '{name}' event {ev.Index}:{ev.Version}");
        return new
        {
            action = "create_character",
            side = "server",
            userIndex = user.Index,
            platformId = user.PlatformId.ToString(),
            name,
            @event = $"{ev.Index}:{ev.Version}",
            note = "queued; poll users for the character (the game validates the name and spawns it)",
        };
    }

    private static object RunClient(World world, CreateCharacterEvent appearance)
    {
        var em = world.EntityManager;
        string name = appearance.Name.ToString();
        var clock = Stopwatch.StartNew();
        int poll = 0, scenes = 0;
        bool afterCall = false;
        var before = ReadClient(world, scanScenes: true, ref scenes);
        bool already = before.State.HasCharacter;
        string? sent = null;

        // Idempotent, so a launcher can retry: a server drops a create sent while the join is
        // still loading (live 2026-09-24: the first send vanished, a resend seconds later spawned).
        if (CharacterCreatePlan.Start(before.State) == CharacterCreatePlan.Step.Send)
        {
            var ev = em.CreateEntity(
                ComponentIndex.Of<CreateCharacterEvent>(ComponentType.AccessMode.ReadWrite),
                ComponentIndex.Of<NetworkEventType>(ComponentType.AccessMode.ReadWrite),
                ComponentIndex.Of<SendNetworkEventTag>(ComponentType.AccessMode.ReadWrite));
            ev.Write(em, appearance);
            // NetworkEventType has bools, so it is not C# unmanaged: typed SetComponentData (as Chat does).
            em.SetComponentData(ev, new NetworkEventType { EventId = CreateCharacterEventId, IsAdminEvent = false, IsDebugEvent = false });
            sent = $"{ev.Index}:{ev.Version}";
            Plugin.Logger?.LogInfo($"create_character: client sent CreateCharacterEvent name '{name}' event {sent}"
                + $" (view {before.State.View}, menu {before.State.HudMenu})");
        }

        var tracker = new CharacterCreatePlan.Tracker();
        var deferred = new DeferredResult(CharacterCreatePlan.WaitMs);
        int cleared = 0;
        void Finish(CharacterCreatePlan.Step step, ClientRead now, string? error = null)
        {
            var reply = new Dictionary<string, object?>
            {
                ["action"] = "create_character",
                ["side"] = "client",
                ["ok"] = step == CharacterCreatePlan.Step.Done,
                ["already"] = already,
                ["result"] = CharacterCreatePlan.ResultName(step),
                ["character"] = now.Character,
                ["leftCreation"] = tracker.LeaveIssued || cleared > 0,
                ["handler"] = CharacterCreatePlan.Handler(tracker),
                ["hudMenu"] = now.State.HudMenu,
                ["view"] = now.State.View,
                ["creationScenes"] = now.State.CreationScenes,
                ["frames"] = tracker.Frames,
                ["elapsedMs"] = clock.ElapsedMilliseconds,
            };
            if (sent != null)
            {
                reply["name"] = name;
                reply["event"] = sent;
            }
            if (tracker.ClearIssued) reply["destroyedScenes"] = cleared;
            string? note = error ?? CharacterCreatePlan.Note(step);
            if (note != null) reply["note"] = note;
            Plugin.Logger?.LogInfo($"create_character: {CharacterCreatePlan.ResultName(step)} after {tracker.Frames} frame(s)"
                + $" (menu {now.State.HudMenu}, view {now.State.View}, scenes {now.State.CreationScenes}, handler {CharacterCreatePlan.Handler(tracker)})");
            deferred.SetResult(reply);
        }
        void Poll()
        {
            try
            {
                if (deferred.BoundGate != null && deferred.BoundGate.ShouldSkip(deferred.BoundTicket)) return;
                if (!world.IsCreated)
                {
                    Finish(CharacterCreatePlan.Step.Stuck, default, "the client left the game before the character was ready");
                    return;
                }
                var now = ReadClient(world, CharacterCreatePlan.ShouldScanScenes(++poll, afterCall), ref scenes);
                var step = tracker.Next(now.State, clock.ElapsedMilliseconds);
                afterCall = step is CharacterCreatePlan.Step.Leave or CharacterCreatePlan.Step.Clear;
                switch (step)
                {
                    case CharacterCreatePlan.Step.Wait:
                        break;
                    case CharacterCreatePlan.Step.Leave:
                        // CharacterCreationMenuMapper.OnUpdate's own exit once the local user has a character.
                        HUDMenuManager.GoToMenu(world, HUDMenuType.WakeUpMenu);
                        Plugin.Logger?.LogInfo("create_character: the creation menu stayed after the character arrived; HUDMenuManager.GoToMenu(WakeUpMenu)");
                        break;
                    case CharacterCreatePlan.Step.Clear:
                        cleared = DestroyCreationScenes();
                        Plugin.Logger?.LogInfo($"create_character: destroyed {cleared} creation scene(s) left by the loading screen (the creation menu's DestroyBrowser)");
                        break;
                    default:
                        Finish(step, now);
                        return;
                }
                MainThread.PostDelayed(0, Poll, deferred);
            }
            catch (Exception e)
            {
                Plugin.Logger?.LogWarning("create_character: " + e.Message);
                if (!deferred.Completion.Task.IsCompleted) Finish(CharacterCreatePlan.Step.Stuck, default, e.Message);
            }
        }
        try { MainThread.PostDelayed(0, Poll, deferred); }
        catch
        {
            deferred.SetException(new InvalidOperationException("create_character could not schedule its readback"));
            throw;
        }
        return deferred;
    }

    private readonly record struct ClientRead(CharacterCreatePlan.State State, string? Character);

    /// <summary>
    /// The local character, the HUD menu and view, and the creation scenes, read once on the main
    /// thread. Searches the scene only when <paramref name="scanScenes"/>; otherwise reports the last
    /// count in <paramref name="scenes"/>.
    /// </summary>
    private static ClientRead ReadClient(World world, bool scanScenes, ref int scenes)
    {
        var em = world.EntityManager;
        string? character = null;
        var local = Query.Entities(
            em,
            includeDisabled: true,
            includeSystems: false,
            ComponentIndex.Of<LocalCharacter>(ComponentType.AccessMode.ReadOnly));
        if (local.Count > 0) character = $"{local[0].Index}:{local[0].Version}";

        string hudMenu = "", view = "";
        try
        {
            var hud = OpenHUDMenuSystem.Get(world);
            if (hud != null) hudMenu = hud.CurrentMenuType.ToString();
        }
        catch { hudMenu = ""; }
        try { view = ViewManager.GetCurrentView().ToString(); } catch { view = ""; }
        if (scanScenes)
        {
            int found = 0;
            ForEachCreationScene(_ => found++);
            scenes = found;
        }
        return new ClientRead(new CharacterCreatePlan.State(character != null, hudMenu, view, scenes), character);
    }

    /// <summary>
    /// Each <c>CustomizationBrowser</c> (the creation scene's behaviour) except one owned by the
    /// character-creation icon generator, which is not part of the join.
    /// </summary>
    private static void ForEachCreationScene(Action<CustomizationBrowser> body)
    {
        var found = UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<CustomizationBrowser>());
        if (found == null || found.Length == 0) return;
        var icons = UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<CharacterCreationIconGenerator>());
        for (int i = 0; i < found.Length; i++)
        {
            var browser = found[i]?.TryCast<CustomizationBrowser>();
            if (browser == null) continue;
            bool icon = false;
            for (int j = 0; icons != null && j < icons.Length && !icon; j++)
            {
                var gen = icons[j]?.TryCast<CharacterCreationIconGenerator>();
                icon = gen != null && gen.CustomizationBrowser != null && gen.CustomizationBrowser.Pointer == browser.Pointer;
            }
            if (!icon) body(browser);
        }
    }

    /// <summary>What <c>CharacterCreationMenuMapper.DestroyBrowser</c> does: <c>Object.Destroy(browser.gameObject)</c>.</summary>
    private static int DestroyCreationScenes()
    {
        int n = 0;
        ForEachCreationScene(browser =>
        {
            UnityEngine.Object.Destroy(browser.gameObject);
            n++;
        });
        return n;
    }

    private static CreateCharacterEvent ReadAppearance(JsonElement args, string name) => new()
    {
        GenderIndex = ReadByte(args, "gender"),
        SkintoneIndex = ReadByte(args, "skin"),
        FaceIndex = ReadByte(args, "face"),
        EyeColorIndex = ReadByte(args, "eyes"),
        HairStyleIndex = ReadByte(args, "hairStyle"),
        HairColorIndex = ReadByte(args, "hairColor"),
        FeaturesIndex = ReadByte(args, "features"),
        AccessoriesIndex = ReadByte(args, "accessories"),
        Name = new FixedString64Bytes(name),
    };

    private static Entity ResolveUser(EntityManager em, JsonElement args, out User user)
    {
        ulong? platformId = null;
        int? userIndex = null;
        if (args.ValueKind == JsonValueKind.Object)
        {
            if (args.TryGetProperty("platformId", out var p))
            {
                if (p.ValueKind == JsonValueKind.String && ulong.TryParse(p.GetString(), out var ps)) platformId = ps;
                else if (p.ValueKind == JsonValueKind.Number && p.TryGetUInt64(out var pn)) platformId = pn;
                else throw new ArgumentException("args.platformId must be an unsigned integer (string preferred)");
            }
            if (args.TryGetProperty("userIndex", out var ui) && ui.ValueKind == JsonValueKind.Number)
                userIndex = ui.GetInt32();
        }
        if (platformId == null && userIndex == null)
            throw new ArgumentException("args.platformId or args.userIndex is required");

        var entities = Query.Entities(
            em,
            includeDisabled: true,
            includeSystems: false,
            ComponentIndex.Of<User>(ComponentType.AccessMode.ReadOnly));
        for (int i = 0; i < entities.Count; i++)
        {
            var u = em.GetComponentData<User>(entities[i]);
            if ((platformId != null && u.PlatformId == platformId.Value)
                || (platformId == null && u.Index == userIndex!.Value))
            {
                user = u;
                return entities[i];
            }
        }
        throw new InvalidOperationException(
            platformId != null ? $"no User with platformId {platformId}" : $"no User with index {userIndex}");
    }

    private static string? ReadString(JsonElement args, string key) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static byte ReadByte(JsonElement args, string key)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(key, out var v)) return 0;
        if (v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out int n) || n < 0 || n > 255)
            throw new ArgumentException($"args.{key} must be 0..255");
        return (byte)n;
    }
}
