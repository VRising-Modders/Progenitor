#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;
using Progenitor.Content;
using Progenitor.Ecs;
using Progenitor.Events;
using Progenitor.Networking;
using ProjectM;
using Stunlock.Core;
using Unity.Entities;

namespace Progenitor.Ai;

/// <summary>
/// Core's inert buff clones on the client (<see cref="InertCloneRules"/>): the API-spawn marker, every
/// <c>ctx.Ai.Variants</c> marker and the hide-health-bar clone.
///
/// <para>A Progenitor client builds each clone from the definition packet by instantiating its
/// <b>own</b> source prefab, and a replicated buff spawns from that copy (the registry rewrote the
/// snapshot GUID to the clone's). The server-side strip in <see cref="AiVariants.MakeInert"/> never
/// reaches it, so without this every marker showed <c>Buff_General_Haste</c>'s client visuals: the
/// cyan <c>BuffSequence</c> shimmer, its movement-speed modifier and its buff-bar entry.</para>
///
/// <para><b>Which clones.</b> Core's two fixed ids are recognized by id. Every other inert clone (the
/// variant markers) is <b>announced by the server</b> on <see cref="InertCloneWire.EventId"/>: to each
/// joiner, to everyone when a variant is defined, and again to a client whose Core swapped (the
/// handshake re-answer, <see cref="ResendTo"/>). The client never guesses from an id.</para>
///
/// <para><b>Only a haste buff clone.</b> An announcement alone strips nothing: the prefab must have
/// <c>Buff</c> and be a registry clone whose source is <see cref="InertCloneRules.MarkerSource"/>
/// (<see cref="InertCloneRules.Decide"/>).</para>
///
/// <para><b>When.</b> From <see cref="PrefabRegistry.AfterApply"/>, the frame the client's clone is
/// built or updated and before anything is instantiated from it; inside the receive of an
/// announcement that names a clone that already exists; and on install, for the two fixed ids by GUID (a
/// client Core swap starts with an empty registry list, so that list is not used). It changes the clone
/// prefab only: a replicated instance that already exists keeps what it was spawned with until the
/// server re-sends it.</para>
/// </summary>
internal static unsafe class InertClones
{
    private static readonly InertCloneBook Book = new();
    private static readonly HashSet<int> Logged = new();

    private static bool _installed;
    private static bool _server;
    private static Action<World, PrefabDefinition>? _hook;
    private static IDisposable? _connectedSub;
    private static bool _clientHandler;
    private static bool _frameHooked;
    private static int _decodeWarnings;

    internal static readonly int ApiSpawnMarkerGuid = PrefabGUID.CalculateGuidHash(InertCloneRules.ApiSpawnMarkerId);
    internal static readonly int HideHudGuid = PrefabGUID.CalculateGuidHash(InertCloneRules.HideHudId);

    internal static void Install()
    {
        if (_installed) return;
        _installed = true;
        _server = Plugin.IsServerProcess;
        Book.Clear();
        Logged.Clear();
        _decodeWarnings = 0;
        _markerSource = 0;
        _markerSourceWarned = false;
        if (_server)
        {
            // Above PrefabRegistry (2000): a joiner gets the announcements before the definitions, so
            // its AfterApply hook already knows every variant marker it builds.
            _connectedSub = EventBus.On<PlayerConnected>(OnPlayerConnected, priority: 2100);
            return;
        }
        _hook = OnApplied;
        PrefabRegistry.AfterApply += _hook;
        CustomEvents.OnReceive(InertCloneWire.EventId, OnMessage);
        _clientHandler = true;
        // The frame check below misses a Client_0 destroyed and recreated between two frames.
        Progenitor.Networking.Handshake.ClientConnectionEnded += OnConnectionEnded;
        Plugin.Frame += Frame;
        _frameHooked = true;
        // A client Core swap into a live session: PrefabRegistry.Definitions is this generation's (empty)
        // static, and the server re-announces variant markers only on the handshake re-answer. The two
        // fixed clones are found by GUID directly.
        try
        {
            var world = Worlds.Client;
            if (world != null && world.IsCreated)
            {
                TryStrip(world, InertCloneRules.ApiSpawnMarkerId, ApiSpawnMarkerGuid);
                TryStrip(world, InertCloneRules.HideHudId, HideHudGuid);
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ai: inert-clone install sweep threw: " + e.Message);
        }
    }

    internal static void Uninstall()
    {
        if (!_installed) return;
        if (_hook != null) PrefabRegistry.AfterApply -= _hook;
        _hook = null;
        try { _connectedSub?.Dispose(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ai: inert-clone PlayerConnected dispose threw: " + e.Message); }
        _connectedSub = null;
        if (_clientHandler)
        {
            CustomEvents.RemoveHandler(InertCloneWire.EventId, OnMessage);
            Progenitor.Networking.Handshake.ClientConnectionEnded -= OnConnectionEnded;
            _clientHandler = false;
        }
        if (_frameHooked)
        {
            Plugin.Frame -= Frame;
            _frameHooked = false;
        }
        Book.Clear();
        Logged.Clear();
        _installed = false;
    }

    internal static string DescribePins()
    {
        var holds = new List<string>();
        if (_installed) holds.Add("installed");
        if (_hook != null) holds.Add("PrefabRegistry.AfterApply");
        if (_connectedSub != null) holds.Add("EventBus.PlayerConnected");
        if (_clientHandler) holds.Add("CustomEvents.PrIc");
        if (_frameHooked) holds.Add("Plugin.Frame");
        if (Book.Count > 0) holds.Add("inert=" + Book.Count);
        return HarmonySlotHandoff.FormatModulePins("Ai.InertClones", holds);
    }

    // ---- server --------------------------------------------------------------------------------

    /// <summary>
    /// Server: record that Core strips the clone <paramref name="guid"/> and tell every connected client
    /// (joiners get the whole set on connect). Core's two fixed ids need no announcement.
    /// </summary>
    internal static void Announce(int guid, InertCloneRole role)
    {
        if (!_installed || !_server) return;
        if (!Book.Announce(guid, role)) return;
        var world = Worlds.Server;
        if (world == null || !world.IsCreated) return;
        try { CustomEvents.SendToAll(world, InertCloneWire.EventId, InertCloneWire.Announce(guid, role)); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ai: inert-clone announce threw: " + e.Message); }
    }

    /// <summary>
    /// Server: send the whole set to one user again. Called from the handshake re-answer of a user that
    /// was already ok (a client Core swap), whose new generation starts with an empty set. Idempotent.
    /// </summary>
    internal static void ResendTo(int userIndex)
    {
        if (!_installed || !_server || Book.Count == 0) return;
        var world = Worlds.Server;
        var bootstrap = world != null && world.IsCreated ? world.GetExistingSystemManaged<ServerBootstrapSystem>() : null;
        if (bootstrap == null) return;
        foreach (var pair in Book.Snapshot())
        {
            try { CustomEvents.SendToUser(bootstrap, userIndex, InertCloneWire.EventId, InertCloneWire.Announce(pair.Key, pair.Value)); }
            catch (Exception e) { Plugin.Logger?.LogWarning("Ai: inert-clone resend of " + pair.Key + " to user " + userIndex + " threw: " + e.Message); }
        }
    }

    /// <summary>
    /// Server: stop announcing <paramref name="guid"/> (its variant was released with its plugin). Clients
    /// that stripped their copy keep it stripped; the next generation's <c>Define</c> announces it again.
    /// </summary>
    internal static void Forget(int guid)
    {
        if (!_installed || !_server) return;
        Book.Forget(guid);
    }

    private static void OnPlayerConnected(PlayerConnected ev)
    {
        foreach (var pair in Book.Snapshot())
        {
            try { CustomEvents.SendToUser(ev.World, ev.UserEntity, InertCloneWire.EventId, InertCloneWire.Announce(pair.Key, pair.Value)); }
            catch (Exception e) { Plugin.Logger?.LogWarning("Ai: inert-clone join sync of " + pair.Key + " threw: " + e.Message); }
        }
    }

    // ---- client --------------------------------------------------------------------------------

    private static void OnMessage(int eventId, byte* payload, int length)
    {
        if (payload == null) return;
        if (!CustomEvents.TryRead<InertCloneMessage>(payload, length, out var message)
            || !InertCloneWire.TryDecode(message, out int guid, out var role, out _))
        {
            if (_decodeWarnings++ < 5)
                Plugin.Logger?.LogWarning("Ai: ignored a " + length + "-byte payload on the inert-clone channel.");
            return;
        }
        Book.Announce(guid, role);
        // A clone that already exists is stripped now, inside the receive, as the AfterApply hook does
        // for a definition: a replicated instance must not spawn from it in between.
        try
        {
            var world = Worlds.Client;
            if (world != null && world.IsCreated) TryStrip(world, IdOf(guid), guid);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ai: inert-clone strip of " + guid + " threw: " + e.Message);
        }
    }

    /// <summary>Left the server: its announcements name nothing on the next one (FSCOPE1).</summary>
    private static void OnConnectionEnded()
    {
        if (Book.Count > 0) Book.Clear();
        _markerSource = 0;
    }

    private static void Frame()
    {
        // Every frame: Worlds.Client returns only a created world, so no second IsCreated (an
        // IL2CPP-boxed bool) is needed.
        if (Worlds.Client != null) return;
        // Left the server: its announcements name nothing on the next one.
        if (Book.Count > 0) Book.Clear();
        _markerSource = 0;
    }

    private static void OnApplied(World world, PrefabDefinition def)
    {
        if (_server || world == null || !world.IsCreated || def == null || def.Kind != PrefabKind.Buff) return;
        TryStrip(world, def.Id, def.Guid.GuidHash);
    }

    private static int _markerSource;
    private static bool _markerSourceWarned;

    /// <summary>
    /// <see cref="InertCloneRules.MarkerSource"/>'s GUID in the client world, through
    /// <c>PrefabLookupMap</c> first (a buff is not a spawnable). 0, with one warning, when it does not resolve.
    /// </summary>
    private static int MarkerSourceGuid(World world)
    {
        if (_markerSource != 0) return _markerSource;
        if (AiRuntime.TryGuidByAnyName(world, InertCloneRules.MarkerSource, out var guid) && guid.GuidHash != 0)
            return _markerSource = guid.GuidHash;
        if (!_markerSourceWarned)
        {
            _markerSourceWarned = true;
            Plugin.Logger?.LogWarning("Ai: " + InertCloneRules.MarkerSource + " does not resolve on this client; announced variant markers are not stripped.");
        }
        return 0;
    }

    /// <summary>The registry id of clone <paramref name="guid"/> when this generation holds its definition.</summary>
    private static string? IdOf(int guid)
    {
        foreach (var def in PrefabRegistry.Definitions)
            if (def.Guid.GuidHash == guid) return def.Id;
        return null;
    }

    /// <summary>
    /// The source of clone <paramref name="guid"/>: the definition's, or after a client Core swap (empty
    /// definition list) the source the registry remembered for the clone across the swap. 0 when unknown.
    /// </summary>
    private static int SourceOf(int guid)
    {
        foreach (var def in PrefabRegistry.Definitions)
            if (def.Guid.GuidHash == guid && def.Source.GuidHash != 0) return def.Source.GuidHash;
        return PrefabApplicator.TryGetAppliedIdentity(new PrefabGUID(guid), out int source, out _) ? source : 0;
    }

    /// <summary>
    /// Strip the client's prefab <paramref name="guid"/> when it exists and
    /// <see cref="InertCloneRules.Decide"/> says it is one of Core's inert clones; nothing otherwise.
    /// </summary>
    private static void TryStrip(World world, string? id, int guid)
    {
        if (!PrefabLookup.TryGetEntity(world, new PrefabGUID(guid), out var clone) || clone == Entity.Null) return;
        var em = world.EntityManager;
        bool announced = Book.Roles.ContainsKey(guid);
        var role = InertCloneRules.Decide(
            id,
            guid,
            clone.Has<Buff>(em),
            ApiSpawnMarkerGuid,
            HideHudGuid,
            Book.Roles,
            announced ? SourceOf(guid) : 0,
            announced ? MarkerSourceGuid(world) : 0);
        if (role == InertCloneRole.None)
        {
            if (announced && Logged.Add(guid))
                Plugin.Logger?.LogWarning("Ai: the server announced " + guid + " as an inert clone, but it is not a " + InertCloneRules.MarkerSource + " buff clone here; left alone.");
            return;
        }
        StripClient(world, id ?? "guid " + guid, guid, clone, role);
    }

    /// <summary>
    /// Make the client's copy of a Core clone inert: <see cref="AiVariants.MakeInert"/> (the
    /// <see cref="InertCloneRules.ServerStrip"/> list), then <see cref="InertCloneRules.ClientExtraStrip"/>,
    /// then for <see cref="InertCloneRole.HideHud"/> the <c>HideTargetHUD</c> component. Logs once per
    /// clone per generation; a failure is an error line, never a throw.
    /// </summary>
    private static void StripClient(World world, string label, int guid, Entity clone, InertCloneRole role)
    {
        var em = world.EntityManager;
        var removed = new List<string>();
        foreach (string name in InertCloneRules.ClientStrip)
            if (BuffBarDataAccess.TryFindComponentByName(em, clone, InertCloneRules.ShortName(name), out _)) removed.Add(name);
        string problem = "";
        try
        {
            AiVariants.MakeInert(world, label, clone);
            foreach (string name in InertCloneRules.ClientExtraStrip)
            {
                // By the archetype's own ComponentType: the cached TypeIndex of a managed component
                // (ShowOnBuffBarData) can disagree with the archetype (BuffBarDataAccess).
                if (BuffBarDataAccess.TryFindComponentByName(em, clone, name, out var type)) em.RemoveComponent(clone, type);
                if (problem.Length == 0 && BuffBarDataAccess.TryFindComponentByName(em, clone, name, out _)) problem = "still has " + name;
            }
            if (problem.Length == 0 && role == InertCloneRole.HideHud && !HasHideTargetHud(em, clone) && !EnsureHideTargetHud(em, clone, out string why))
                problem = why;
        }
        catch (Exception e)
        {
            problem = e.Message;
        }
        if (problem.Length > 0)
        {
            Plugin.Logger?.LogError("Ai: client copy of " + label + " (" + guid + ") is not inert: " + problem + ". Units wearing it may show the source buff's visuals.");
            return;
        }
        if (Logged.Add(guid))
            Plugin.Logger?.LogInfo("Ai: client copy of " + label + " (" + guid + ") made inert (" + role + "); removed "
                + (removed.Count == 0 ? "nothing (already stripped)" : string.Join(", ", removed))
                + (role == InertCloneRole.HideHud ? "; HideTargetHUD Type " + InertCloneRules.HideTargetHudType : ""));
    }

    /// <summary>
    /// Give <paramref name="e"/> <c>HideTargetHUD { Type = -1 }</c> and read it back. Client copy of the
    /// hide-HUD clone only. Typed <c>Write</c> after checking the interop size against the dump
    /// (<c>Layout.HideTargetHUDSize</c>). False, with why, when it did not stick.
    /// </summary>
    internal static bool EnsureHideTargetHud(EntityManager em, Entity e, out string why)
    {
        why = "";
        if (sizeof(HideTargetHUD) != Layout.HideTargetHUDSize)
        {
            why = "HideTargetHUD is " + sizeof(HideTargetHUD) + " bytes in the interop, " + Layout.HideTargetHUDSize + " in the dump; not written";
            return false;
        }
        var wanted = new HideTargetHUD { Type = (HideTargetHUDType)InertCloneRules.HideTargetHudType };
        if (!e.Has<HideTargetHUD>(em)) e.Add<HideTargetHUD>(em);
        e.Write(em, in wanted);
        if (!TryReadHudType(em, e, out int type))
        {
            why = "HideTargetHUD did not stick";
            return false;
        }
        if (!InertCloneRules.HidesHealthBar(type, Layout.HideTargetHUDTypeCharacterHUD, Layout.HideTargetHUDTypeTargetInfo))
        {
            why = "HideTargetHUD.Type reads " + type;
            return false;
        }
        return true;
    }

    /// <summary>True when <paramref name="e"/> carries <c>HideTargetHUD</c> hiding both the character HUD and the target info.</summary>
    internal static bool HasHideTargetHud(EntityManager em, Entity e) =>
        TryReadHudType(em, e, out int type)
        && InertCloneRules.HidesHealthBar(type, Layout.HideTargetHUDTypeCharacterHUD, Layout.HideTargetHUDTypeTargetInfo);

    /// <summary>The native <c>HideTargetHUD.Type</c> read at its dump offset, not through the wrapper layout.</summary>
    private static bool TryReadHudType(EntityManager em, Entity e, out int type)
    {
        type = 0;
        byte* p = AiRuntime.RO<HideTargetHUD>(em, e, Layout.HideTargetHUDTypeOffset + sizeof(int));
        if (p == null) return false;
        type = *(int*)(p + Layout.HideTargetHUDTypeOffset);
        return true;
    }
}
#endif
