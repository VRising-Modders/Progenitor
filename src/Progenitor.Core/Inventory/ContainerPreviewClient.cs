#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;
using System.Diagnostics;
using Progenitor.Ai;
using Progenitor.Ecs;
using Progenitor.Hooks;
using Progenitor.Networking;
using Progenitor.Ui;
using Progenitor.WorldState;
using ProjectM;
using ProjectM.Network;
using ProjectM.UI;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;

namespace Progenitor.Inventory;

/// <summary>
/// The client half of the container preview, Core-owned. One preview at a time (the game has one container window).
///
/// <para><b>Seams</b> (research note "remote container preview", N1 to N6). Open: the same two entities the game's
/// interaction creates, written raw at layout offsets: an init entity with <c>ContainerMenu.InitData
/// {TargetContainer, InteractBuff = Entity.Null}</c> and an <c>OpenMenuEvent {Menu = ContainerMenu, InitDataEntity}</c>
/// (as <c>BuildMenuOpener</c> does for the build menu). Hold: <c>SystemHooks.Before&lt;InteractSystemHUD&gt;</c> sets
/// Skip while the preview owns the container window, because that system closes an interact menu with no interaction
/// behind it. Close: <c>HUDMenuManager.CloseMenu(world)</c> while the window is still ours. Target gone: a
/// <c>Before&lt;ContainerMenuMapper&gt;</c> skip so the mapper never reads a destroyed container. Courtesy filter: a
/// <c>Before&lt;SerializeAndSendClientEventsSystem&gt;</c> that destroys this client's item events naming the previewed
/// container. UI: the bag panel and the at-the-chest buttons are hidden and put back on close. Every hook exists only
/// while the preview owns the window.</para>
///
/// <para>Read-only is not decided here: the server refuses every item event from a player who holds no interaction
/// with the container. Status: source-checked and game-free tested; not live-verified (experiments E1 to E5).</para>
/// </summary>
internal static unsafe class ContainerPreviewClient
{
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static bool _installed;
    private static int _sequence;
    private static ContainerPreviewHandle? _current;
    private static readonly List<ContainerPreviewHandle> Guarding = new();

    /// <summary>
    /// Bag hide mode for qualification (E5): true (the default) = a CanvasGroup with alpha 0, raycasts and interaction
    /// off, the way container tabs keep native content mounted; false = SetActive(false) on the bag panel. The Dev
    /// bridge switches it; it applies to the next preview that opens.
    /// </summary>
    internal static bool BagHideByAlpha { get; set; } = true;

    internal static double Now => Clock.Elapsed.TotalSeconds;

    internal static ContainerPreviewHandle? Current => _current;

    internal static void Install()
    {
        if (_installed || Plugin.IsServerProcess) return;
        _installed = true;
        CustomEvents.OnReceive(ContainerPreviewWire.EventId, OnAnswer);
        Handshake.ServerContextCleared += OnContextCleared;
        Plugin.Frame += Tick;
    }

    internal static void Uninstall()
    {
        if (!_installed) return;
        _installed = false;
        _current?.Close(ContainerPreviewCloseReason.Unloaded, "", notify: false);
        _current = null;
        ReleaseClosingView();
        foreach (var guarded in Guarding) guarded.FinishMapperGuard();
        Guarding.Clear();
        try { CustomEvents.RemoveHandler(ContainerPreviewWire.EventId); }
        catch (Exception e) { Plugin.Logger?.LogWarning("ContainerPreview: handler removal threw: " + e.Message); }
        Handshake.ServerContextCleared -= OnContextCleared;
        Plugin.Frame -= Tick;
    }

    /// <summary>Start a preview; a running one closes (<see cref="ContainerPreviewCloseReason.Replaced"/>).</summary>
    internal static ContainerPreviewHandle Open(ContainerId container, ContainerPreviewOptions options, object? owner, bool hudGate = true, bool syncInventory = true)
    {
        if (container.IsNone) throw new ArgumentException("No container id.", nameof(container));
        return Open(container, 0, 0, 0, options, owner, hudGate, syncInventory);
    }

    /// <summary>
    /// REOPEN2: a preview of the container this client knows only by its network id words (the window the player had open):
    /// the server resolves them to its stable id and runs every preview check on that; the reply carries the id back.
    /// </summary>
    internal static ContainerPreviewHandle OpenByNetworkId(int netIndex, int netGeneration, int netType, ContainerPreviewOptions options, object? owner)
    {
        if (netIndex == 0 && netGeneration == 0) throw new ArgumentException("No network id.");
        return Open(ContainerId.None, netIndex, netGeneration, netType, options, owner, hudGate: true, syncInventory: true);
    }

    private static ContainerPreviewHandle Open(ContainerId container, int netIndex, int netGeneration, int netType,
        ContainerPreviewOptions options, object? owner, bool hudGate, bool syncInventory)
    {
        if (!_installed) throw new InvalidOperationException("The container preview is not installed (client side only).");
        var world = Worlds.Client;
        if (world == null || !world.IsCreated) throw new InvalidOperationException("No client world (join a game first).");
        // A preview that took the place of the player's window hands the return on to the one replacing it.
        bool inheritReturn = _current?.Machine.TookPlayerWindow == true;
        _current?.Close(ContainerPreviewCloseReason.Replaced, "", notify: true);
        ReleaseClosingView();
        int sequence = _sequence = _sequence == int.MaxValue ? 1 : _sequence + 1;
        var handle = new ContainerPreviewHandle(container, options, world, sequence, owner) { HudGate = hudGate, SyncInventory = syncInventory };
        if (inheritReturn) handle.Machine.InheritPlayerWindow();
        _current = handle;
        if (!SendRequest(ContainerPreviewWire.KindOpen, sequence, container, handle.Machine.RequestSeconds, syncInventory, netIndex, netGeneration, netType))
            handle.Close(ContainerPreviewCloseReason.Failed, "The preview request could not be sent.", notify: true);
        return handle;
    }

    internal static bool SendRequest(string kind, int sequence, ContainerId container, double seconds, bool syncInventory = true,
        int netIndex = 0, int netGeneration = 0, int netType = 0)
    {
        try
        {
            // STATIONPREVIEW1: this client opens station windows too (the server opens nothing else for an older one).
            var bytes = ContainerPreviewWire.Encode(new ContainerPreviewPacket
            {
                Kind = kind, Sequence = sequence, Container = container.IsNone ? "" : container.ToString(), Seconds = seconds,
                SyncInventory = syncInventory, NetIndex = netIndex, NetGeneration = netGeneration, NetType = netType,
                Stations = kind == ContainerPreviewWire.KindOpen,
            });
            return CustomEvents.SendToServer(ContainerPreviewWire.EventId, bytes);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("ContainerPreview: " + kind + " send threw: " + e.Message);
            return false;
        }
    }

    /// <summary>What the running preview says about the container window in <paramref name="world"/>, for the panel sites.</summary>
    internal static Progenitor.Ui.VanillaPreviewWindow PreviewWindow(World world)
    {
        var c = _current;
        if (c != null && ReferenceEquals(c.World, world))
        {
            try { return c.PanelView(); }
            catch { return default; }
        }
        // A preview that just closed: its window goes on a later frame. Until then the panel target keeps naming the
        // previewed chest, so the close is one Closed event, not a TargetChanged to the bare chest first.
        if (_closingWorld == null || !ReferenceEquals(_closingWorld, world)) return default;
        try
        {
            if (world.IsCreated)
            {
                int closingMenu = ContainerPreviewMenus.MenuOf(_closing.Kind);
                var window = ContainerPreviewWindows.ReadWindow(world, closingMenu);
                bool shown = window.Menu == closingMenu;
                bool same = window.Target.Index == _closing.WindowIndex && window.Target.Version == _closing.WindowVersion;
                if (ContainerPreviewMachine.HoldsClosingView(shown, same, window.Interact != Entity.Null)) return _closing;
            }
        }
        catch { /* */ }
        ReleaseClosingView();
        return default;
    }

    private static Progenitor.Ui.VanillaPreviewWindow _closing;
    private static World? _closingWorld;

    /// <summary>The closing preview's panel view, held while its window is still on screen (<see cref="PreviewWindow"/>).</summary>
    internal static void HoldClosingView(World world, in Progenitor.Ui.VanillaPreviewWindow view)
    {
        if (!view.OwnsWindow || view.Container.IsNone) { ReleaseClosingView(); return; }
        _closing = view;
        _closingWorld = world;
    }

    internal static void ReleaseClosingView()
    {
        _closing = default;
        _closingWorld = null;
    }

    internal static void Forget(ContainerPreviewHandle handle)
    {
        if (ReferenceEquals(_current, handle)) _current = null;
    }

    private static void OnAnswer(int _, byte* payload, int length)
    {
        if (payload == null || length <= 0 || length > ContainerPreviewWire.MaxBytes) return;
        var p = ContainerPreviewWire.Decode(new ReadOnlySpan<byte>(payload, length), expectRequest: false);
        var current = _current;
        if (p == null || current == null || p.Sequence != current.Sequence) return;
        if (p.Kind == ContainerPreviewWire.KindReply) current.OnReply(p);
        else if (p.Kind == ContainerPreviewWire.KindHold)
        {
            // The container open gate asks the player first (a prompt is up): keep waiting for the reply.
            if (current.Machine.Hold(Now, p.Seconds))
                Plugin.Logger?.LogInfo("ContainerPreview: " + current.Container + " waits for the open gate's prompt.");
        }
        else current.Close(ContainerPreviewCloseReason.LeaseEnded, p.Message, notify: true, tellServer: false);
    }

    private static void OnContextCleared(string _) =>
        _current?.Close(ContainerPreviewCloseReason.WorldChanged, "", notify: true, tellServer: false);

    /// <summary>L2: a closed preview whose mapper guard waits for the window to be confirmed closed.</summary>
    internal static void Guard(ContainerPreviewHandle handle)
    {
        if (!Guarding.Contains(handle)) Guarding.Add(handle);
    }

    private static void Tick()
    {
        for (int i = Guarding.Count - 1; i >= 0; i--)
        {
            bool done;
            try { done = Guarding[i].PollMapperGuard(); }
            catch { done = true; Guarding[i].FinishMapperGuard(); }
            if (done) Guarding.RemoveAt(i);
        }
        var current = _current;
        if (current == null) return;
        try { current.Tick(); }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("ContainerPreview: tick threw: " + e.Message);
            current.Close(ContainerPreviewCloseReason.Failed, "The preview failed.", notify: true);
        }
    }

    // ---- game reads and writes ------------------------------------------------------------------

    /// <summary>The client entity carrying the normal network id, or Entity.Null.</summary>
    internal static Entity FindByNetworkId(EntityManager em, int index, int generation)
    {
        var all = Query.Entities(em, false, false, ComponentIndex.Of<NetworkId>(ComponentType.AccessMode.ReadOnly));
        for (int i = 0; i < all.Count; i++)
        {
            var e = all[i];
            if (!e.TryRead(em, out NetworkId net) || net.Type != NetworkIdType.Normal) continue;
            if (net.Normal_Index == index && net.Normal_Generation == generation) return e;
        }
        return Entity.Null;
    }

    /// <summary>The entity's network id as three normalized words; false without one.</summary>
    internal static bool TryWords(EntityManager em, Entity entity, out int n0, out int n1, out int n2)
    {
        n0 = n1 = n2 = 0;
        var type = ComponentIndex.Of<NetworkId>(ComponentType.AccessMode.ReadOnly);
        if (entity == Entity.Null || !em.Exists(entity) || !em.HasComponent(entity, type)) return false;
        int* net = (int*)em.GetComponentDataRawRO(entity, type.TypeIndex);
        if (net == null) return false;
        (n0, n1, n2) = InventorySortFilter.Normalize(net[0], net[1], net[2]);
        return !(n0 == 0 && n1 == 0 && n2 == 0);
    }

    /// <summary>N1: create the init entity and the open event, raw at the layout offsets. No generic over a game value type.</summary>
    internal static void WriteOpen(World world, Entity target)
    {
        var em = world.EntityManager;
        var eventType = ComponentIndex.Of<OpenMenuEvent>(ComponentType.AccessMode.ReadWrite);
        var initType = ComponentIndex.Of<ContainerMenu.InitData>(ComponentType.AccessMode.ReadWrite);
        var init = em.CreateEntity(initType);
        byte* initData = (byte*)em.GetComponentDataRawRW(init, initType.TypeIndex);
        if (initData == null)
        {
            em.DestroyEntity(init);
            throw new InvalidOperationException("ContainerMenu.InitData could not be written");
        }
        AiRuntime.WriteEntity(initData + Layout.ContainerMenuInitDataTargetContainerOffset, target);
        AiRuntime.WriteEntity(initData + Layout.ContainerMenuInitDataInteractBuffOffset, Entity.Null);

        var ev = em.CreateEntity(eventType);
        byte* evData = (byte*)em.GetComponentDataRawRW(ev, eventType.TypeIndex);
        if (evData == null)
        {
            em.DestroyEntity(ev);
            em.DestroyEntity(init);
            throw new InvalidOperationException("OpenMenuEvent could not be written");
        }
        // Menu is a byte enum followed by padding up to InitDataEntity (layout.txt, "Build menu open/close").
        *(int*)(evData + Layout.OpenMenuEventMenuOffset) = Layout.HUDMenuTypeContainerMenu;
        AiRuntime.WriteEntity(evData + Layout.OpenMenuEventInitDataEntityOffset, init);
        Plugin.Logger?.LogInfo("ContainerPreview: container window open requested for " + target.Index + ":" + target.Version
            + " (OpenMenuEvent " + ev.Index + ":" + ev.Version + ", init " + init.Index + ":" + init.Version + ").");
    }

    /// <summary>What the game's container window shows now. Null mapper when the system is missing.</summary>
    internal static (int Menu, ContainerMenuMapper? Mapper, Entity Target, Entity Interact) ReadWindow(World world)
    {
        int menu = BuildMenuOpener.CurrentMenuType(world);
        ContainerMenuMapper? mapper = null;
        Entity target = Entity.Null, interact = Entity.Null;
        try
        {
            mapper = world.GetExistingSystemManaged<ContainerMenuMapper>();
            if (mapper != null) { target = mapper._Target; interact = mapper._InteractBuff; }
        }
        catch { mapper = null; }
        return (menu, mapper, target, interact);
    }

    /// <summary>N3: close the HUD menu, the world-only static the build menu's close uses.</summary>
    internal static void CloseMenu(World world)
    {
        HUDMenuManager.CloseMenu(world);
        Plugin.Logger?.LogInfo("ContainerPreview: container window close requested.");
    }
}

/// <summary>One running preview: the state machine plus the hooks, UI changes and input it holds while it owns the window.</summary>
internal sealed unsafe class ContainerPreviewHandle : IContainerPreview
{
    private readonly ContainerPreviewOptions _options;
    private readonly World _world;
    private readonly int _epoch;
    private readonly object? _owner;
    private readonly List<HiddenUi> _hidden = new();
    private Action<ContainerPreviewClosed>? _closed;
    private int _netIndex, _netGeneration, _netType;
    private Entity _target = Entity.Null;
    private Entity _inventory = Entity.Null;
    private (int, int, int) _targetWords, _inventoryWords;
    private double _nextLookup;
    private IDisposable? _hudHook, _mapperHook, _sendHook, _escape;
    private bool _capturing;
    /// <summary>STATIONPREVIEW1: the window this preview opens (the server's answer; the container window until then).</summary>
    private int _menuType = ContainerPreviewMenus.ContainerMenu;
    private long _shownAtArm;
    private readonly WorldBoundQuery _goToMenu = new("ContainerPreview.GoToHUDMenu");
    private Entity _windowTarget = Entity.Null;
    private bool _inventorySeen;
    private double _guardSince;
    private readonly HashSet<string> _warned = new(StringComparer.Ordinal);
    private EventDropQueries? _drop;

    internal ContainerPreviewHandle(ContainerId container, ContainerPreviewOptions options, World world, int sequence, object? owner)
    {
        Container = container; _options = options; _world = world; Sequence = sequence; _owner = owner;
        _epoch = WorldEpoch.Current;
        Machine = new ContainerPreviewMachine(options, ContainerPreviewClient.Now);
    }

    internal ContainerPreviewMachine Machine { get; }
    internal int Sequence { get; }

    /// <summary>
    /// E2 only: false leaves the interact HUD's auto-close alone, to observe the game closing the window the preview
    /// opened. The Dev bridge's <c>hudGate</c>; always true for plugin previews.
    /// </summary>
    internal bool HudGate { get; init; } = true;

    /// <summary>E1b only: false asks the server not to put the user in the chest's inventory sync lists. The Dev bridge's <c>syncInventory</c>; always true for plugin previews.</summary>
    internal bool SyncInventory { get; init; } = true;
    internal Entity Target => _target;
    internal int DroppedEvents { get; private set; }

    /// <summary>The client world this preview was opened in.</summary>
    internal World World => _world;

    /// <summary>
    /// The panel sites' view of this preview (<see cref="Progenitor.Ui.VanillaPanelPreview"/>): whether it owns the
    /// window, the entity the window was opened on, the chest's entity now, the server's network id words and the id.
    /// </summary>
    internal Progenitor.Ui.VanillaPreviewWindow PanelView() => new(
        Machine.OwnsWindow, _windowTarget.Index, _windowTarget.Version, _target.Index, _target.Version,
        _netIndex, _netGeneration, _netType, Container, ContainerPreviewMenus.KindOf(_menuType));

    /// <summary>STATIONPREVIEW1: the window this preview opens (<c>HUDMenuType</c>).</summary>
    internal int MenuType => _menuType;

    /// <summary>
    /// Every part of the open container window a hidden HUD must leave shown: the container half and its parent, and the
    /// character inventory half only while it is shown (the station layout; <see cref="ContainerPreviewOptions.HidePlayerInventory"/>
    /// false). A hidden bag adds nothing, so its canvas is not kept on its account. Empty when the preview does not own the window.
    /// </summary>
    internal List<Transform> WindowParts()
    {
        var parts = new List<Transform>(3);
        if (!Machine.OwnsWindow || !_world.IsCreated) return parts;
        var window = ContainerPreviewWindows.ReadWindow(_world, _menuType);
        if (!window.Present) return parts;
        var sub = window.Sub;
        if (sub != null) { parts.Add(sub.transform); if (sub.transform.parent != null) parts.Add(sub.transform.parent); }
        if (!_options.HidePlayerInventory)
        {
            var bag = window.Bag;
            if (bag != null) parts.Add(bag.transform);
        }
        return parts;
    }

    /// <summary>
    /// Readback for the live checks: each half of the window (shown, its size on screen, its root canvas and whether that
    /// canvas draws), the container grid's slot count, and the chest and inventory entities on this client (present,
    /// disabled, the inventory's row count).
    /// </summary>
    private Dictionary<string, object?> WindowReadback()
    {
        var r = new Dictionary<string, object?>();
        try
        {
            if (!_world.IsCreated) return r;
            var mapper = ContainerPreviewClient.ReadWindow(_world).Mapper;
            if (mapper != null)
            {
                var sub = mapper._ContainerSubMenu;
                r["container"] = sub == null ? null : Half(sub.gameObject);
                if (sub != null && sub.EquipmentItemsParent != null)
                {
                    var grid = sub.EquipmentItemsParent.transform;
                    int active = 0;
                    for (int i = 0; i < grid.childCount; i++) if (grid.GetChild(i).gameObject.activeSelf) active++;
                    r["gridSlots"] = grid.childCount;
                    r["gridSlotsShown"] = active;
                }
                var bag = mapper._InventorySubMenu;
                r["inventory"] = bag == null ? null : Half(bag.gameObject);
            }
            var em = _world.EntityManager;
            r["targetPresent"] = _target != Entity.Null && em.Exists(_target);
            r["targetDisabled"] = _target != Entity.Null && em.Exists(_target) && _target.Has<Disabled>(em);
            r["inventoryPresent"] = _inventory != Entity.Null && em.Exists(_inventory);
            if (_inventory != Entity.Null && em.Exists(_inventory))
            {
                r["inventoryDisabled"] = _inventory.Has<Disabled>(em);
                r["inventoryRows"] = em.GetBuffer<InventoryBuffer>(_inventory, true).Length;
            }
        }
        catch (Exception e) { r["error"] = e.Message; }
        return r;
    }

    private static Dictionary<string, object?> Half(GameObject go)
    {
        var h = new Dictionary<string, object?> { ["active"] = go.activeInHierarchy };
        var rect = go.GetComponent<RectTransform>();
        if (rect != null) { var size = rect.rect.size; h["size"] = new[] { size.x, size.y }; h["position"] = new[] { rect.position.x, rect.position.y }; }
        var canvas = go.GetComponentInParent<Canvas>();
        var root = canvas == null ? null : canvas.rootCanvas;
        h["rootCanvas"] = root == null ? null : root.gameObject.name;
        h["rootCanvasEnabled"] = root != null && root.enabled;
        var group = go.GetComponent<CanvasGroup>();
        if (group != null) { h["alpha"] = group.alpha; h["blocksRaycasts"] = group.blocksRaycasts; h["interactable"] = group.interactable; }
        return h;
    }

    /// <summary>The container panel site's newest target (what attached mod tabs receive) and how many frames ago it was read.</summary>
    private Dictionary<string, object?> PanelTargetReadback()
    {
        var t = Progenitor.Ui.UnityUiRuntime.LastContainerPanelTarget;
        int frame = Progenitor.Ui.UnityUiRuntime.LastContainerPanelFrame;
        return new Dictionary<string, object?>
        {
            ["target"] = frame < 0 ? null : t.ToString(),
            ["framesAgo"] = frame < 0 ? null : Time.frameCount - frame,
            ["previewContainer"] = t.IsPreview ? t.PreviewContainer.ToString() : null,
            ["matchesPreview"] = frame >= 0 && t.IsPreview && t.PreviewContainer == Container
                && t.NetworkIdIndex == _netIndex && t.NetworkIdGeneration == _netGeneration,
        };
    }

    /// <summary>
    /// The container half alone (the mapper's container sub-menu) while this preview owns the window, else null: the
    /// camera preview's readback reports whether it draws. <see cref="WindowParts"/> is what the hidden HUD keeps.
    /// </summary>
    internal Transform? WindowPart()
    {
        if (!Machine.OwnsWindow || !_world.IsCreated) return null;
        var sub = ContainerPreviewWindows.ReadWindow(_world, _menuType).Sub;
        return sub == null ? null : sub.transform;
    }

    public ContainerId Container { get; private set; }
    public ContainerPreviewState State => Machine.State;
    public ContainerPreviewCloseReason CloseReason => Machine.CloseReason;
    public string? Reason => Machine.Phase == ContainerPreviewPhase.Closed ? Machine.Message : null;

    public event Action<ContainerPreviewClosed> Closed
    {
        add { _closed += value; }
        remove { _closed -= value; }
    }

    public void Dispose() => Close(ContainerPreviewCloseReason.Disposed, "", notify: true);

    internal void OnReply(ContainerPreviewPacket p)
    {
        if (!Machine.OnReply(ContainerPreviewClient.Now, p.Ok, p.Message))
        {
            if (Machine.Phase == ContainerPreviewPhase.Closed) Finish(notify: true, tellServer: false);
            return;
        }
        if (Machine.Phase == ContainerPreviewPhase.Closed) { Finish(notify: true, tellServer: false); return; }
        _netIndex = p.NetIndex; _netGeneration = p.NetGeneration; _netType = p.NetType;
        // REOPEN2: a preview asked by network id learns its stable id from the server's answer.
        if (Container.IsNone && ContainerId.TryParse(p.Container, out var resolved)) Container = resolved;
        // STATIONPREVIEW1: the window the server chose (0 from an older server: the container window).
        int menu = p.MenuType == 0 ? ContainerPreviewMenus.ContainerMenu : p.MenuType;
        if (!ContainerPreviewMenus.IsPreviewWindow(menu))
        {
            Close(ContainerPreviewCloseReason.Failed, "The server asked for a window this client cannot open.", notify: true);
            return;
        }
        _menuType = menu;
        if (!NetworkIdWords.Validate(_netIndex, _netGeneration, _netType, out var why))
        {
            Close(ContainerPreviewCloseReason.Failed, "The server sent an unusable network id: " + why, notify: true);
            return;
        }
        Plugin.Logger?.LogInfo("ContainerPreview: " + Container + " authorized (network id " + NetworkIdWords.Format(_netIndex, _netGeneration) + ").");
    }

    internal void Tick()
    {
        if (Machine.Phase == ContainerPreviewPhase.Closed) return;
        double now = ContainerPreviewClient.Now;
        if (Machine.RenewDue(now))
        {
            ContainerPreviewClient.SendRequest(ContainerPreviewWire.KindRenew, Sequence, Container, ContainerPreviewMachine.RenewGrantSeconds);
            Machine.MarkRenewed(now);
        }
        var sample = Sample(now);
        var step = Machine.Step(now, sample);
        if (step == ContainerPreviewStep.Closed) { Finish(notify: true, tellServer: true); return; }
        if (step == ContainerPreviewStep.CloseOtherWindow)
        {
            // The game's own close: the window's mapper ends the player's interaction as Escape would. It is opened
            // again the game's way when this preview ends (Finish).
            Plugin.Logger?.LogInfo("ContainerPreview: taking the place of the player's open window ("
                + (ContainerReturnClient.LastWindow()?.ToString() ?? "?") + "); it opens again when the preview ends.");
            ContainerReturnClient.NoteFlowClose();
            ContainerPreviewClient.CloseMenu(_world);
            return;
        }
        if (step == ContainerPreviewStep.OpenWindow)
        {
            ArmWindow();
            _windowTarget = _target;
            ContainerPreviewWindows.WriteOpen(_world, _target, _menuType);
        }
        if (Machine.Phase == ContainerPreviewPhase.Open) ApplyUi();
    }

    private ContainerPreviewSample Sample(double now)
    {
        var s = new ContainerPreviewSample();
        var world = Worlds.Client;
        s.SameWorld = world != null && ReferenceEquals(world, _world) && world.IsCreated && WorldEpoch.Current == _epoch;
        if (!s.SameWorld || Machine.Phase == ContainerPreviewPhase.Requesting) return s;
        var em = world!.EntityManager;

        if (_target != Entity.Null && (!em.Exists(_target) || !SameWords(em, _target, _targetWords)))
        {
            _target = Entity.Null;
            _inventory = Entity.Null;
            ForgetInventories();
        }
        if (_target == Entity.Null && now >= _nextLookup)
        {
            _nextLookup = now + 0.1;
            _target = ContainerPreviewClient.FindByNetworkId(em, _netIndex, _netGeneration);
            if (_target != Entity.Null) ContainerPreviewClient.TryWords(em, _target, out _targetWords.Item1, out _targetWords.Item2, out _targetWords.Item3);
        }
        s.TargetFound = _target != Entity.Null;
        if (s.TargetFound)
        {
            // STATIONPREVIEW1: every inventory instance (a furnace's input and output), not just the first.
            RefreshInventories(em);
            s.InventoryReady = ContainerPreviewInventories.Ready(_inventories.Count, AllPresent(em));
            if (s.InventoryReady) _inventorySeen = true;
        }

        var window = ContainerPreviewWindows.ReadWindow(world, _menuType);
        s.WindowIsOurs = window.Menu == _menuType && window.Target == _target && _target != Entity.Null && window.Interact == Entity.Null;
        s.ContainerMenuOpen = window.Menu == _menuType;
        s.TargetReplaced = _windowTarget != Entity.Null && _target != Entity.Null && _target != _windowTarget;
        s.OtherMenuOpen = window.Menu > 0 && !s.WindowIsOurs;
        // Only worth reading while it can matter: the chest is ready and the player's own window is in the way.
        if (s.OtherMenuOpen && s.TargetFound && s.InventoryReady && _options.ReturnToOpenWindow && !Machine.TookPlayerWindow)
        {
            try { s.OtherMenuReturnable = ContainerReturnClient.LastWindow()?.IsOpen == true; }
            catch { s.OtherMenuReturnable = false; }
        }
        return s;
    }

    private static bool SameWords(EntityManager em, Entity e, (int, int, int) words) =>
        ContainerPreviewClient.TryWords(em, e, out int a, out int b, out int c) && a == words.Item1 && b == words.Item2 && c == words.Item3;

    // ---- while the preview owns the window -------------------------------------------------------

    /// <summary>Install the hooks and take Escape just before the open is written.</summary>
    private void ArmWindow()
    {
        _shownAtArm = Ui.UiPanelHandle.LastShownSequence;
        _hudHook = SystemHooks.Before<InteractSystemHUD>(BeforeInteractHud);
        _mapperHook = ContainerPreviewWindows.HookMapper(_menuType, BeforeMapper);
        if (_options.DropItemEvents) _sendHook = SystemHooks.Before<SerializeAndSendClientEventsSystem>(BeforeSend);
        try
        {
            Ui.Ui.CurrentRuntime().SetCapture(true); _capturing = true;
            _escape = Ui.Ui.PushEscape(OnEscape);
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("ContainerPreview: Escape hold threw: " + e.Message); }
    }

    /// <summary>N2: keep the game from closing the container window the preview owns.</summary>
    private void BeforeInteractHud(SystemHookContext c)
    {
        if (c.World != _world || !Machine.OwnsWindow || !HudGate) return;
        try
        {
            var window = ContainerPreviewWindows.ReadWindow(c.World, _menuType);
            c.Skip = ContainerPreviewHudGate.ShouldSkip(
                Machine.OwnsWindow, Machine.Phase == ContainerPreviewPhase.Opening,
                window.Menu == _menuType,
                (_windowTarget != Entity.Null ? window.Target == _windowTarget : window.Target == _target && _target != Entity.Null),
                window.Interact == Entity.Null,
                LocalGoToMenuCount(c.World));
        }
        catch (Exception e)
        {
            c.Skip = false;
            WarnOnce("HUD gate", e);
        }
    }

    /// <summary>The game's own pending menu requests for the local character (a real interaction); other players' do not count.</summary>
    private int LocalGoToMenuCount(World world)
    {
        var em = world.EntityManager;
        if (!_goToMenu.TryGet(world, out var query))
        {
            query = Query.Build(em, includeDisabled: false, includeSystems: false, ComponentIndex.Of<GoToHUDMenu>(ComponentType.AccessMode.ReadOnly));
            _goToMenu.Store(world, query);
        }
        var entities = query.ToEntityArray(Allocator.Temp);
        try
        {
            if (entities.Length == 0) return 0;
            if (!LocalCharacters.TryGet(em, out var character)) return entities.Length;
            int n = 0;
            for (int i = 0; i < entities.Length; i++)
                if (!entities[i].TryRead<EntityOwner>(em, out var owner) || owner.Owner == character) n++;
            return n;
        }
        finally { entities.Dispose(); }
    }

    /// <summary>N3: the container left this client; never let the mapper read a destroyed entity.</summary>
    private void BeforeMapper(SystemHookContext c)
    {
        // Also after the close, until the window is confirmed gone (FinishMapperGuard): the mapper must never read a destroyed chest.
        if (c.World != _world || _mapperHook == null || _windowTarget == Entity.Null) return;
        try
        {
            var em = c.World.EntityManager;
            if (em.Exists(_windowTarget))
            {
                // The chest is here but its inventory left (a save suspension, the server letting go): hold the window as
                // it was rather than let the mapper read a missing inventory. The machine closes after a grace period.
                if (Machine.OwnsWindow && _inventorySeen && !ContainerPreviewInventories.Ready(_inventories.Count, AllPresent(em))) c.Skip = true;
                return;
            }
            // The chest left this client (a save suspension takes the streaming origin away for a moment, or it is gone):
            // hold the window; the machine waits a grace period for it to come back, then closes.
            c.Skip = true;
        }
        catch (Exception e) { WarnOnce("mapper gate", e); }
    }

    /// <summary>N5: destroy this client's item events naming the previewed container before they are sent.</summary>
    private void BeforeSend(SystemHookContext c)
    {
        if (c.World != _world || !Machine.OwnsWindow) return;
        try
        {
            _drop ??= new EventDropQueries();
            DroppedEvents += _drop.Drop(c.World, _targetWords, _inventoryWords, _otherInventoryWords.Count == 0 ? null : _otherInventoryWords);
        }
        catch (Exception e) { WarnOnce("event filter", e); }
    }

    /// <summary>STATIONPREVIEW1: the target's inventory per instance (Entity.Null where it has not arrived), and their words.</summary>
    private readonly List<Entity> _inventories = new();
    private readonly List<(int, int, int)> _otherInventoryWords = new();

    /// <summary>The target changed or left: forget its inventories (the filter's words with them).</summary>
    private void ForgetInventories()
    {
        _inventories.Clear();
        _otherInventoryWords.Clear();
    }

    /// <summary>
    /// Every inventory instance of the target, looked up again where missing; no instance is skipped because an earlier
    /// one has not arrived. Instance 0 is <see cref="_inventory"/>; the others' network id words feed the event filter.
    /// </summary>
    private void RefreshInventories(EntityManager em)
    {
        int count = UnityInventoryBackend.InventoryCount(em, _target);
        bool changed = count != _inventories.Count;
        while (_inventories.Count < count) _inventories.Add(Entity.Null);
        if (_inventories.Count > count) _inventories.RemoveRange(count, _inventories.Count - count);
        for (int i = 0; i < count; i++)
        {
            var known = _inventories[i];
            if (known != Entity.Null && em.Exists(known)) continue;
            _inventories[i] = UnityInventoryBackend.TryInventoryEntity(em, _target, i, out var inv, out _) ? inv : Entity.Null;
            changed |= _inventories[i] != known;
        }
        var first = count > 0 ? _inventories[0] : Entity.Null;
        if (first != _inventory)
        {
            _inventory = first;
            _inventoryWords = default;
            if (first != Entity.Null) ContainerPreviewClient.TryWords(em, first, out _inventoryWords.Item1, out _inventoryWords.Item2, out _inventoryWords.Item3);
        }
        if (!changed) return;
        _otherInventoryWords.Clear();
        for (int i = 1; i < _inventories.Count; i++)
        {
            var inv = _inventories[i];
            if (inv != Entity.Null && inv != _target && ContainerPreviewClient.TryWords(em, inv, out int a, out int b, out int c)) _otherInventoryWords.Add((a, b, c));
        }
    }

    /// <summary>Whether each known instance is present now (for <see cref="ContainerPreviewInventories.Ready"/>).</summary>
    private IReadOnlyList<bool> AllPresent(EntityManager em)
    {
        var present = new bool[_inventories.Count];
        for (int i = 0; i < present.Length; i++) present[i] = _inventories[i] != Entity.Null && em.Exists(_inventories[i]);
        return present;
    }

    private bool OnEscape()
    {
        if (!Machine.OwnsWindow) return false;
        bool dialog = false, capture = false, chat = false;
        try { dialog = UiDialog.Current != null || UiChoiceHandle.AnyOpen; } catch { /* */ }
        try { capture = Keybinds.EscapeBelongsToCapture; } catch { /* */ }
        try { chat = Keybinds.ChatFocusNow == ChatFocus.Focused; } catch { /* */ }
        if (ContainerPreviewEscape.Decide(true, dialog, capture, chat) != ContainerPreviewEscapeOwner.Preview) return false;
        // RETURN1: a Progenitor window opened over the preview's window after it (a hub) is above it: that one closes
        // first (the framework's sweep), and the 498 hold keeps this window. A panel docked before the preview (Find) is
        // below it: the preview closes first, as before.
        bool newerOnTop = false;
        try { newerOnTop = Ui.Ui.EscapeHidesAPanelOverGameWindow(_shownAtArm); } catch { /* */ }
        if (newerOnTop) return false;
        Close(ContainerPreviewCloseReason.Escape, "", notify: true);
        return true;
    }

    /// <summary>
    /// N6: hide the bag and the buttons that only work at the chest; reasserted every frame while open. The buttons are
    /// hidden with a CanvasGroup (alpha 0, no raycasts, not interactable) so the window's layout groups keep their
    /// spacing; the bag the same way by default, or with SetActive(false) when the Dev bridge asks (E5 compares the two
    /// for gamepad focus). The container window's mappers stay active either way.
    /// </summary>
    private void ApplyUi()
    {
        if (_menuType != ContainerPreviewMenus.ContainerMenu)
        {
            // STATIONPREVIEW1: a station window keeps its buttons (their events are dropped and refused); the bag as asked.
            if (!_options.HidePlayerInventory) return;
            var stationBag = ContainerPreviewWindows.ReadWindow(_world, _menuType).Bag;
            if (stationBag != null) Hide(stationBag.gameObject, ContainerPreviewClient.BagHideByAlpha);
            return;
        }
        var mapper = ContainerPreviewClient.ReadWindow(_world).Mapper;
        if (mapper == null) return;
        var sub = mapper._ContainerSubMenu;
        if (sub != null)
        {
            Hide(sub.TakeAllButton == null ? null : sub.TakeAllButton.gameObject, byAlpha: true);
            Hide(sub.SortButton == null ? null : sub.SortButton.gameObject, byAlpha: true);
            Hide(sub.OpenTagEditButton == null ? null : sub.OpenTagEditButton.gameObject, byAlpha: true);
            var burn = sub.BurnContainerUI;
            Hide(burn == null ? null : burn.gameObject, byAlpha: true);
        }
        if (!_options.HidePlayerInventory) return;
        var bag = mapper._InventorySubMenu;
        if (bag != null) Hide(bag.gameObject, ContainerPreviewClient.BagHideByAlpha);
    }

    private void Hide(GameObject? go, bool byAlpha)
    {
        if (go == null) return;
        HiddenUi? row = null;
        foreach (var r in _hidden) if (r.Go == go) { row = r; break; }
        if (row == null)
        {
            row = new HiddenUi { Go = go, WasActive = go.activeSelf, ByAlpha = byAlpha };
            if (byAlpha)
            {
                var group = go.GetComponent<CanvasGroup>();
                if (group == null) { group = go.AddComponent<CanvasGroup>(); row.Added = true; }
                row.Group = group;
                row.Alpha = group.alpha; row.Raycasts = group.blocksRaycasts; row.Interactable = group.interactable;
            }
            _hidden.Add(row);
        }
        if (!row.ByAlpha) { if (go.activeSelf) go.SetActive(false); return; }
        var g = row.Group;
        if (g == null) return;
        if (g.alpha != 0) g.alpha = 0;
        if (g.blocksRaycasts) g.blocksRaycasts = false;
        if (g.interactable) g.interactable = false;
    }

    private void RestoreUi()
    {
        foreach (var row in _hidden)
        {
            try
            {
                if (row.Go == null) continue;
                if (!row.ByAlpha) { if (row.WasActive && !row.Go.activeSelf) row.Go.SetActive(true); continue; }
                if (row.Group == null) continue;
                if (row.Added) UnityEngine.Object.Destroy(row.Group);
                else { row.Group.alpha = row.Alpha; row.Group.blocksRaycasts = row.Raycasts; row.Group.interactable = row.Interactable; }
            }
            catch (Exception e) { Plugin.Logger?.LogWarning("ContainerPreview: UI restore threw: " + e.Message); }
        }
        _hidden.Clear();
    }

    private sealed class HiddenUi
    {
        internal GameObject Go = null!;
        internal bool WasActive;
        internal bool ByAlpha;
        internal CanvasGroup? Group;
        internal bool Added;
        internal float Alpha;
        internal bool Raycasts;
        internal bool Interactable;
    }


    // ---- closing ---------------------------------------------------------------------------------

    /// <summary>Close for <paramref name="reason"/>. <paramref name="tellServer"/> false when the server already let go.</summary>
    internal void Close(ContainerPreviewCloseReason reason, string message, bool notify, bool tellServer = true)
    {
        Progenitor.Ui.VanillaPreviewWindow view = default;
        try { view = PanelView(); } catch { /* */ }
        if (!Machine.Close(reason, message)) return;
        // Held only while the window it opened stays on screen; unload or a dead world has no window to hold for.
        if (reason != ContainerPreviewCloseReason.Unloaded && reason != ContainerPreviewCloseReason.WorldChanged && _world.IsCreated)
            ContainerPreviewClient.HoldClosingView(_world, view);
        else ContainerPreviewClient.ReleaseClosingView();
        Finish(notify, tellServer);
    }

    /// <summary>The machine is closed: undo everything the preview did, in order, each step guarded.</summary>
    private void Finish(bool notify, bool tellServer)
    {
        var reason = Machine.CloseReason;
        ContainerPreviewClient.Forget(this);
        bool hadWindow = _hudHook != null;
        try
        {
            bool worldAlive = _world.IsCreated && reason != ContainerPreviewCloseReason.WorldChanged;
            if (hadWindow && worldAlive && reason != ContainerPreviewCloseReason.WindowClosed)
            {
                var window = ContainerPreviewWindows.ReadWindow(_world, _menuType);
                // Close only the window this preview opened: its menu, still on our target (or not bound
                // yet), with no interaction behind it. A real menu the player opened is left alone.
                if (window.Menu == _menuType && window.Interact == Entity.Null
                    && (window.Target == _windowTarget || window.Target == Entity.Null || reason == ContainerPreviewCloseReason.TargetGone))
                    ContainerPreviewClient.CloseMenu(_world);
            }
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("ContainerPreview: window close threw: " + e.Message); }
        try { RestoreUi(); } catch (Exception e) { Plugin.Logger?.LogWarning("ContainerPreview: UI restore threw: " + e.Message); }
        // The machine is closed, so every hook body already returns at once; dispose them now.
        var hud = _hudHook; _hudHook = null;
        var send = _sendHook; _sendHook = null;
        DisposeQuiet(send); DisposeQuiet(hud);
        // The mapper guard stays until the window is confirmed closed (the close above takes effect on a later frame):
        // a chest that went away must not be read by the mapper in between. Unload and a dead world drop it now.
        if (_mapperHook != null)
        {
            if (reason == ContainerPreviewCloseReason.Unloaded || !_world.IsCreated) FinishMapperGuard();
            else { _guardSince = ContainerPreviewClient.Now; ContainerPreviewClient.Guard(this); }
        }
        var escape = _escape; _escape = null;
        DisposeQuiet(escape);
        if (_capturing)
        {
            _capturing = false;
            try { Ui.Ui.CurrentRuntime().SetCapture(false); } catch { /* */ }
        }
        _goToMenu.Release();
        _drop?.Release(); _drop = null;
        if (tellServer && ContainerPreviewMachine.TellsServer(reason))
            ContainerPreviewClient.SendRequest(ContainerPreviewWire.KindClose, Sequence, Container, 0);
        Plugin.Logger?.LogInfo("ContainerPreview: " + Container + " closed (" + reason + (Machine.Message.Length > 0 ? ": " + Machine.Message : "")
            + (DroppedEvents > 0 ? ", " + DroppedEvents + " item events dropped" : "") + ").");
        var closed = _closed; _closed = null;
        if (notify && reason != ContainerPreviewCloseReason.Unloaded && closed != null)
        {
            try { closed(new ContainerPreviewClosed(Container, reason, Machine.Message)); }
            catch (Exception e) { Plugin.Logger?.LogWarning("ContainerPreview: Closed handler threw: " + e.Message); }
        }
        // Back where the player was: the window this preview took the place of opens again, the game's way. After the
        // Closed handlers, and not when one of them started another preview (that one returns when it ends).
        if (Machine.TookPlayerWindow && ContainerReturnRules.ReturnsAfterPreview(reason) && ContainerPreviewClient.Current == null)
        {
            try { ContainerReturnClient.ReopenLast(null, "the preview of " + Container + " ended (" + reason + ")", anyAge: true); }
            catch (Exception e) { Plugin.Logger?.LogWarning("ContainerPreview: the return threw: " + e.Message); }
        }
    }

    /// <summary>One frame of the post-close mapper guard (<see cref="ContainerPreviewMachine.MapperGuardDone"/>); true when it is gone.</summary>
    internal bool PollMapperGuard()
    {
        if (_mapperHook == null) return true;
        bool done;
        try
        {
            bool alive = _world.IsCreated;
            bool onTarget = false;
            if (alive)
            {
                var window = ContainerPreviewWindows.ReadWindow(_world, _menuType);
                onTarget = window.Menu == _menuType && window.Target == _windowTarget;
            }
            done = ContainerPreviewMachine.MapperGuardDone(alive, onTarget, ContainerPreviewClient.Now - _guardSince);
        }
        catch { done = true; }
        if (done) FinishMapperGuard();
        return done;
    }

    internal void FinishMapperGuard()
    {
        var mapper = _mapperHook; _mapperHook = null;
        DisposeQuiet(mapper);
    }

    /// <summary>L3: a hook that throws logs once per preview, not every frame.</summary>
    private void WarnOnce(string what, Exception e)
    {
        if (!_warned.Add(what)) return;
        Plugin.Logger?.LogWarning("ContainerPreview: " + what + " threw (further failures of it are not logged): " + e.Message);
    }

    private static void DisposeQuiet(IDisposable? d)
    {
        try { d?.Dispose(); } catch { /* */ }
    }

    internal Dictionary<string, object?> Describe() => new()
    {
        ["container"] = Container.ToString(),
        ["sequence"] = Sequence,
        ["state"] = State.ToString(),
        ["phase"] = Machine.Phase.ToString(),
        ["held"] = Machine.Held,
        ["closeReason"] = CloseReason.ToString(),
        ["reason"] = Reason,
        ["networkId"] = _netType == 0 ? null : NetworkIdWords.Format(_netIndex, _netGeneration),
        ["target"] = _target == Entity.Null ? null : _target.Index + ":" + _target.Version,
        ["inventory"] = _inventory == Entity.Null ? null : _inventory.Index + ":" + _inventory.Version,
        ["inventoryMissing"] = Machine.InventoryMissing,
        ["window"] = WindowReadback(),
        ["panelTarget"] = PanelTargetReadback(),
        ["hooks"] = _hudHook != null,
        ["hiddenUi"] = _hidden.Count,
        ["droppedEvents"] = DroppedEvents,
        ["keepOpen"] = _options.KeepOpen,
        ["hudGate"] = HudGate,
        ["syncInventory"] = SyncInventory,
        ["menuType"] = _menuType,
        ["returnToOpenWindow"] = _options.ReturnToOpenWindow,
        ["tookPlayerWindow"] = Machine.TookPlayerWindow,
        ["owner"] = _owner?.GetType().Name,
    };


    /// <summary>The item events the window can send, each with the offsets of the network ids it names (N5).</summary>
    private sealed class EventDropQueries
    {
        private const int NetworkIdBytes = 12;

        private readonly List<Row> _rows = new();

        private sealed class Row
        {
            internal WorldBoundQuery Cache = null!;
            internal Func<EntityManager, EntityQuery> Build = null!;
            internal Func<EntityManager, Entity, int, IntPtr> Raw = null!;
            internal int[] Offsets = null!;
            internal int Need;
        }

        internal EventDropQueries()
        {
            Add<MoveItemBetweenInventoriesEvent>(Layout.MoveItemEventFromInventoryOffset, Layout.MoveItemEventToInventoryOffset);
            Add<MoveAllItemsBetweenInventoriesEvent>(Layout.MoveAllItemsEventFromInventoryOffset, Layout.MoveAllItemsEventToInventoryOffset);
            Add<MoveAllItemsBetweenInventoriesEventV2>(Layout.MoveAllItemsEventV2FromInventoryOffset, Layout.MoveAllItemsEventV2ToInventoryOffset);
            Add<SmartMergeItemsBetweenInventoriesEvent>(Layout.SmartMergeEventFromInventoryOffset, Layout.SmartMergeEventToInventoryOffset);
            Add<SplitItemEvent>(Layout.SplitItemEventInventoryOffset);
            Add<SplitItemEventV2>(Layout.SplitItemEventV2FromInventoryOffset, Layout.SplitItemEventV2ToInventoryOffset);
            Add<SortSingleInventoryEvent>(Layout.SortSingleInventoryEventInventoryOffset);
            Add<SortAllInventoriesEvent>(Layout.SortAllInventoriesEventInventoryOffset);
            Add<DropInventoryItemEvent>(Layout.DropInventoryItemEventInventoryOffset);
            Add<EquipItemFromInventoryEvent>(Layout.EquipItemFromInventoryEventFromInventoryOffset);
            Add<ToggleRefiningEvent>(Layout.ToggleRefiningEventStationOffset);
            Add<InteractEvents_Client.RenameInteractable>(Layout.RenameInteractableIdOffset);
            // STATIONPREVIEW1: the station windows' own events, each naming the station at 0x0. The game's server refuses
            // all but the share button's without an interaction; the share event it takes from anyone, so it is dropped
            // here for the previewed station like the rest (research note "remote station window").
            AddStation();
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private void AddStation()
        {
            Add<StartCraftItemEvent>(Layout.StartCraftItemEventWorkstationOffset);
            Add<StopCraftItemEvent>(Layout.StopCraftItemEventWorkstationOffset);
            Add<ToggleRefiningRecipeEvent>(Layout.ToggleRefiningRecipeEventStationOffset);
            Add<ToggleSalvageEvent>(Layout.ToggleSalvageEventStationOffset);
            Add<ShareRefinementEvent>(Layout.ShareRefinementEventStationOffset);
        }

        private void Add<T>(params int[] offsets)
        {
            int need = 0;
            foreach (int o in offsets) need = Math.Max(need, o + NetworkIdBytes);
            _rows.Add(new Row
            {
                Cache = new WorldBoundQuery("ContainerPreview." + typeof(T).Name),
                Build = em => Query.Build(em, includeDisabled: false, includeSystems: false, ComponentIndex.Of<T>(ComponentType.AccessMode.ReadOnly)),
                Raw = (m, e, n) => (IntPtr)AiRuntime.RO<T>(m, e, n),
                Offsets = offsets,
                Need = need,
            });
        }

        /// <summary>Destroy every event naming either id; returns how many.</summary>
        internal int Drop(World world, (int, int, int) target, (int, int, int) inventory, List<(int, int, int)>? others = null)
        {
            if (target == default && inventory == default) return 0;
            var em = world.EntityManager;
            List<Entity>? doomed = null;
            foreach (var row in _rows)
            {
                if (!row.Cache.TryGet(world, out var query))
                {
                    query = row.Build(em);
                    row.Cache.Store(world, query);
                }
                if (query.IsEmptyIgnoreFilter) continue;
                var entities = query.ToEntityArray(Allocator.Temp);
                try
                {
                    for (int i = 0; i < entities.Length; i++)
                    {
                        byte* p = (byte*)row.Raw(em, entities[i], row.Need);
                        if (p == null) continue;
                        foreach (int o in row.Offsets)
                        {
                            int* net = (int*)(p + o);
                            var words = InventorySortFilter.Normalize(net[0], net[1], net[2]);
                            if (words == default || (words != target && words != inventory && (others == null || !others.Contains(words)))) continue;
                            (doomed ??= new List<Entity>()).Add(entities[i]);
                            break;
                        }
                    }
                }
                finally { entities.Dispose(); }
            }
            if (doomed == null) return 0;
            foreach (var e in doomed) if (em.Exists(e)) em.DestroyEntity(e);
            return doomed.Count;
        }

        internal void Release()
        {
            foreach (var row in _rows) row.Cache.Release();
        }
    }
}
#endif
