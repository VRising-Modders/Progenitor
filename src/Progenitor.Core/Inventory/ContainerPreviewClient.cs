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
        try { CustomEvents.RemoveHandler(ContainerPreviewWire.EventId); }
        catch (Exception e) { Plugin.Logger?.LogWarning("ContainerPreview: handler removal threw: " + e.Message); }
        Handshake.ServerContextCleared -= OnContextCleared;
        Plugin.Frame -= Tick;
    }

    /// <summary>Start a preview; a running one closes (<see cref="ContainerPreviewCloseReason.Replaced"/>).</summary>
    internal static ContainerPreviewHandle Open(ContainerId container, ContainerPreviewOptions options, object? owner, bool hudGate = true)
    {
        if (!_installed) throw new InvalidOperationException("The container preview is not installed (client side only).");
        if (container.IsNone) throw new ArgumentException("No container id.", nameof(container));
        var world = Worlds.Client;
        if (world == null || !world.IsCreated) throw new InvalidOperationException("No client world (join a game first).");
        _current?.Close(ContainerPreviewCloseReason.Replaced, "", notify: true);
        int sequence = _sequence = _sequence == int.MaxValue ? 1 : _sequence + 1;
        var handle = new ContainerPreviewHandle(container, options, world, sequence, owner) { HudGate = hudGate };
        _current = handle;
        if (!SendRequest(ContainerPreviewWire.KindOpen, sequence, container, handle.Machine.RequestSeconds))
            handle.Close(ContainerPreviewCloseReason.Failed, "The preview request could not be sent.", notify: true);
        return handle;
    }

    internal static bool SendRequest(string kind, int sequence, ContainerId container, double seconds)
    {
        try
        {
            var bytes = ContainerPreviewWire.Encode(new ContainerPreviewPacket { Kind = kind, Sequence = sequence, Container = container.ToString(), Seconds = seconds });
            return CustomEvents.SendToServer(ContainerPreviewWire.EventId, bytes);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("ContainerPreview: " + kind + " send threw: " + e.Message);
            return false;
        }
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
        else current.Close(ContainerPreviewCloseReason.LeaseEnded, p.Message, notify: true, tellServer: false);
    }

    private static void OnContextCleared(string _) =>
        _current?.Close(ContainerPreviewCloseReason.WorldChanged, "", notify: true, tellServer: false);

    private static void Tick()
    {
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
    private readonly WorldBoundQuery _goToMenu = new("ContainerPreview.GoToHUDMenu");
    private bool _targetGoneSeen;
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
    internal Entity Target => _target;
    internal int DroppedEvents { get; private set; }

    public ContainerId Container { get; }
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
        if (step == ContainerPreviewStep.OpenWindow)
        {
            ArmWindow();
            ContainerPreviewClient.WriteOpen(_world, _target);
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

        if (_target != Entity.Null && (!em.Exists(_target) || !SameWords(em, _target, _targetWords))) { _target = Entity.Null; _inventory = Entity.Null; }
        if (_target == Entity.Null && Machine.Phase == ContainerPreviewPhase.Waiting && now >= _nextLookup)
        {
            _nextLookup = now + 0.1;
            _target = ContainerPreviewClient.FindByNetworkId(em, _netIndex, _netGeneration);
            if (_target != Entity.Null) ContainerPreviewClient.TryWords(em, _target, out _targetWords.Item1, out _targetWords.Item2, out _targetWords.Item3);
        }
        s.TargetFound = _target != Entity.Null && !_targetGoneSeen;
        if (s.TargetFound)
        {
            if (_inventory == Entity.Null || !em.Exists(_inventory))
            {
                _inventory = Entity.Null;
                if (UnityInventoryBackend.TryInventoryEntity(em, _target, 0, out var inv, out _))
                {
                    _inventory = inv;
                    ContainerPreviewClient.TryWords(em, inv, out _inventoryWords.Item1, out _inventoryWords.Item2, out _inventoryWords.Item3);
                }
            }
            s.InventoryReady = _inventory != Entity.Null;
        }

        var window = ContainerPreviewClient.ReadWindow(world);
        s.WindowIsOurs = window.Menu == Layout.HUDMenuTypeContainerMenu && window.Target == _target && _target != Entity.Null && window.Interact == Entity.Null;
        s.OtherMenuOpen = window.Menu > 0 && !s.WindowIsOurs;
        return s;
    }

    private static bool SameWords(EntityManager em, Entity e, (int, int, int) words) =>
        ContainerPreviewClient.TryWords(em, e, out int a, out int b, out int c) && a == words.Item1 && b == words.Item2 && c == words.Item3;

    // ---- while the preview owns the window -------------------------------------------------------

    /// <summary>Install the hooks and take Escape just before the open is written.</summary>
    private void ArmWindow()
    {
        _hudHook = SystemHooks.Before<InteractSystemHUD>(BeforeInteractHud);
        _mapperHook = SystemHooks.Before<ContainerMenuMapper>(BeforeMapper);
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
            var window = ContainerPreviewClient.ReadWindow(c.World);
            c.Skip = ContainerPreviewHudGate.ShouldSkip(
                Machine.OwnsWindow, Machine.Phase == ContainerPreviewPhase.Opening,
                window.Menu == Layout.HUDMenuTypeContainerMenu,
                window.Target == _target && _target != Entity.Null,
                window.Interact == Entity.Null,
                LocalGoToMenuCount(c.World));
        }
        catch (Exception e)
        {
            c.Skip = false;
            Plugin.Logger?.LogWarning("ContainerPreview: HUD gate threw: " + e.Message);
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
            if (!Progenitor.Dye.DyeSender.TryLocalCharacter(em, out var character)) return entities.Length;
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
        if (c.World != _world || !Machine.OwnsWindow || _target == Entity.Null) return;
        try
        {
            if (c.World.EntityManager.Exists(_target)) return;
            c.Skip = true;
            if (_targetGoneSeen) return;
            _targetGoneSeen = true;
            Dev.MainThread.Post(() => Close(ContainerPreviewCloseReason.TargetGone, "", notify: true));
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("ContainerPreview: mapper gate threw: " + e.Message); }
    }

    /// <summary>N5: destroy this client's item events naming the previewed container before they are sent.</summary>
    private void BeforeSend(SystemHookContext c)
    {
        if (c.World != _world || !Machine.OwnsWindow) return;
        try
        {
            _drop ??= new EventDropQueries();
            DroppedEvents += _drop.Drop(c.World, _targetWords, _inventoryWords);
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("ContainerPreview: event filter threw: " + e.Message); }
    }

    private bool OnEscape()
    {
        if (!Machine.OwnsWindow) return false;
        bool dialog = false, capture = false, chat = false;
        try { dialog = UiDialog.Current != null || UiChoiceHandle.AnyOpen; } catch { /* */ }
        try { capture = Keybinds.EscapeBelongsToCapture; } catch { /* */ }
        try { chat = Keybinds.ChatFocusNow == ChatFocus.Focused; } catch { /* */ }
        if (ContainerPreviewEscape.Decide(true, dialog, capture, chat) != ContainerPreviewEscapeOwner.Preview) return false;
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
        if (!Machine.Close(reason, message)) return;
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
                var window = ContainerPreviewClient.ReadWindow(_world);
                // Close only the window this preview opened: the container menu, still on our target (or not bound
                // yet), with no interaction behind it. A real menu the player opened is left alone.
                if (window.Menu == Layout.HUDMenuTypeContainerMenu && window.Interact == Entity.Null
                    && (window.Target == _target || window.Target == Entity.Null || reason == ContainerPreviewCloseReason.TargetGone))
                    ContainerPreviewClient.CloseMenu(_world);
            }
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("ContainerPreview: window close threw: " + e.Message); }
        try { RestoreUi(); } catch (Exception e) { Plugin.Logger?.LogWarning("ContainerPreview: UI restore threw: " + e.Message); }
        // The machine is closed, so every hook body already returns at once; dispose them now.
        var hud = _hudHook; _hudHook = null;
        var mapper = _mapperHook; _mapperHook = null;
        var send = _sendHook; _sendHook = null;
        DisposeQuiet(send); DisposeQuiet(hud); DisposeQuiet(mapper);
        var escape = _escape; _escape = null;
        DisposeQuiet(escape);
        if (_capturing)
        {
            _capturing = false;
            try { Ui.Ui.CurrentRuntime().SetCapture(false); } catch { /* */ }
        }
        _goToMenu.Release();
        _drop?.Release(); _drop = null;
        if (tellServer && reason is not (ContainerPreviewCloseReason.Refused or ContainerPreviewCloseReason.NoAnswer
            or ContainerPreviewCloseReason.LeaseEnded or ContainerPreviewCloseReason.WorldChanged or ContainerPreviewCloseReason.Unloaded))
            ContainerPreviewClient.SendRequest(ContainerPreviewWire.KindClose, Sequence, Container, 0);
        Plugin.Logger?.LogInfo("ContainerPreview: " + Container + " closed (" + reason + (Machine.Message.Length > 0 ? ": " + Machine.Message : "")
            + (DroppedEvents > 0 ? ", " + DroppedEvents + " item events dropped" : "") + ").");
        var closed = _closed; _closed = null;
        if (!notify || reason == ContainerPreviewCloseReason.Unloaded || closed == null) return;
        try { closed(new ContainerPreviewClosed(Container, reason, Machine.Message)); }
        catch (Exception e) { Plugin.Logger?.LogWarning("ContainerPreview: Closed handler threw: " + e.Message); }
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
        ["closeReason"] = CloseReason.ToString(),
        ["reason"] = Reason,
        ["networkId"] = _netType == 0 ? null : NetworkIdWords.Format(_netIndex, _netGeneration),
        ["target"] = _target == Entity.Null ? null : _target.Index + ":" + _target.Version,
        ["inventory"] = _inventory == Entity.Null ? null : _inventory.Index + ":" + _inventory.Version,
        ["hooks"] = _hudHook != null,
        ["hiddenUi"] = _hidden.Count,
        ["droppedEvents"] = DroppedEvents,
        ["keepOpen"] = _options.KeepOpen,
        ["hudGate"] = HudGate,
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
        internal int Drop(World world, (int, int, int) target, (int, int, int) inventory)
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
                            if (words == default || (words != target && words != inventory)) continue;
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
