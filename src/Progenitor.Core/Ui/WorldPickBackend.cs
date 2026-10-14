#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Progenitor.Ecs;
using Progenitor.Hooks;
using Progenitor.WorldState;
using ProjectM;
using ProjectM.Network;
using ProjectM.UI;
using Unity.Collections;
using Unity.Entities;

namespace Progenitor.Ui;

/// <summary>
/// The prompt band's look (the Unity half, <c>UnityUiRuntime.WorldPick.cs</c>). A runtime that does
/// not implement it leaves the band as a plain kit panel.
/// </summary>
internal interface IWorldPickBandRuntime
{
    /// <summary>Restyle the shown <paramref name="panel"/> as the bottom prompt band.</summary>
    void StyleWorldPickBand(IRuntimePanel panel, string prompt, string interactKey, string cancelKey);
}

/// <summary>
/// P7 item 3, the client half of <c>ctx.Hud.PickInWorld</c> (<see cref="UiWorldPickCoordinator"/> is the
/// game-free half).
///
/// <para><b>How an interact becomes a panel</b> (client metadata; order partly inferred): the server's
/// interact ability gives the character an interact buff carrying <c>GoToHUDMenu</c>; the client's
/// <c>InteractSystemHUD</c> answers it through <c>HUDMenuManager.GoToMenu</c>, which records an event
/// entity carrying <c>OpenMenuEvent {HUDMenuType Menu @0 (a byte); Entity InitDataEntity @4}</c> and an
/// init entity carrying the menu's <c>InitData</c> (<c>ContainerMenu.InitData {Entity TargetContainer @0;
/// Entity InteractBuff @8}</c>, the station menus the same shape). <c>OpenHUDMenuSystem</c> consumes the
/// event and opens the panel.</para>
///
/// <para><b>The seam.</b> A <see cref="SystemHooks"/> Before hook on <c>OpenHUDMenuSystem</c> (the
/// Bootstrap-slot <c>SystemBase.Update</c> patch every Core hook shares: no new Harmony patch, no native
/// call). While a pick is armed it reads each <c>OpenMenuEvent</c> raw; for a wanted menu whose init
/// entity really carries the expected <c>InitData</c> (checked, never assumed) it reads the target and
/// the interact buff, sends the client-to-server <c>StopInteractingWithObjectEvent {NetworkId Target @0}</c>
/// (event id 1426509984 in both the client and server metadata) for the buff — the message the game's
/// own panel mappers send when a station panel closes — and, only when that send succeeded, destroys
/// the two client-only entities so the panel never opens (<see cref="UiWorldPickSuppression"/>). With
/// no buff, or a send that failed, the panel opens as usual and the result says
/// <c>Suppressed = false</c>. The event is built field by field with
/// the recipe <c>DyeSender</c> uses (the entity, <c>NetworkEventType</c>, <c>SendNetworkEventTag</c>;
/// offsets checked against the interop struct first). The game's <c>TrySendStopInteractingWith</c> is
/// not called: it takes an <c>EntityManager</c> by value.</para>
///
/// <para><b>Repeats.</b> Until the server has removed the buff, the HUD may ask for the same panel again;
/// those events are destroyed too (<see cref="UiWorldPickSwallowList"/>) until the client stops seeing the
/// buff entity (the server removed it), at most ten seconds. The hook stays installed that long after
/// the pick ended and is removed as soon as the list is empty. With no pick armed and nothing to
/// swallow, no hook and no frame subscription exist.</para>
///
/// <para><b>Escape</b> follows <see cref="UiWorldPickEscape"/>: dialog &gt; key capture &gt; focused chat
/// &gt; other Progenitor panels &gt; an open game menu &gt; the pick. The handler is a
/// <see cref="Ui.PushEscape"/> lease that acts (and takes the press) only when the pick is topmost; it
/// returns false otherwise, so the press runs as if no pick were armed. The band is a shown kit panel,
/// so Bootstrap forwards Escape; its capture mode is re-decided every frame
/// (<see cref="UiWorldPickEscape.BandMode"/>): <see cref="UiCaptureMode.WhenHovered"/> while the pick
/// is topmost — the registered context takes the game's Back / Menu_Close, so the system menu does not
/// open — and <see cref="UiCaptureMode.Never"/> otherwise, so no context of ours stands between the
/// game and its own menu's close. The band's frame rect is zero-sized below the screen edge (the
/// visible band is a child), so the hover gate never sees the pointer over it: gameplay input is never
/// blocked by the band, only the close key while it is topmost. When the Escape sweep hides the band
/// together with another panel, the next frame shows it again. Plain Escape is not a rebindable mod
/// key, so it is not registered through <c>Keybinds</c>.</para>
/// </summary>
internal static unsafe class WorldPickBackend
{
    private const int StopInteractingEventId = 1426509984;
    private const int NetworkIdSize = 12;
    private const int InitTargetOffset = 0;
    private const int InitBuffOffset = 8;

    private static readonly object Gate = new();
    private static readonly object BandOwner = new();
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static readonly UiWorldPickSwallowList Swallow = new();
    private static readonly WorldBoundQuery EventQuery = new("world-pick OpenMenuEvent");
    private static readonly Dictionary<int, bool> VerifiedInit = new();
    private static readonly HashSet<string> LoggedOnce = new(StringComparer.Ordinal);

    private static UiWorldPickCoordinator? _coordinator;
    private static IDisposable? _hook;
    private static IDisposable? _escape;
    private static IUiPanel? _band;
    private static bool _frameHooked;
    private static bool _lifetimeRegistered;
    private static bool? _stopLayoutOk;
    private static string _prompt = UiWorldPickOptions.DefaultPrompt;
    private static UiCaptureMode _bandMode = UiCaptureMode.Never;
    private static ulong? _armedWorldSeq;
    private static Entity _armedCharacter;
    private static ulong? _hudWorldSeq;
    private static OpenHUDMenuSystem? _hudSystem;

    /// <summary>The process-wide pick state machine (one per Core generation).</summary>
    internal static UiWorldPickCoordinator Coordinator
    {
        get
        {
            lock (Gate)
            {
                if (_coordinator != null) return _coordinator;
                _coordinator = new UiWorldPickCoordinator(new Backend(), Now, () => WorldEpoch.Current, Log);
                if (!_lifetimeRegistered)
                {
                    _lifetimeRegistered = true;
                    UiWidgetLifetime.Register("world-pick", Teardown);
                }

                return _coordinator;
            }
        }
    }

    internal static void DropOwner(object owner)
    {
        UiWorldPickCoordinator? c;
        lock (Gate) c = _coordinator;
        c?.DropOwner(owner);
    }

    /// <summary>Core unload: end any pick silently and release the hook, Escape, band and frame subscription.</summary>
    internal static void Teardown()
    {
        UiWorldPickCoordinator? c;
        lock (Gate) c = _coordinator;
        try { c?.Reset(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: world pick reset threw: " + e.Message); }
        ReleaseBand();
        ReleaseEscape();
        Swallow.Clear();
        ReleaseHook();
        try { EventQuery.Release(); } catch { /* the world may be gone */ }
        _armedWorldSeq = null;
        _hudWorldSeq = null;
        _hudSystem = null;
        lock (Gate)
        {
            _coordinator = null;
            _lifetimeRegistered = false;
        }
    }

    private static double Now() => Clock.Elapsed.TotalSeconds;

    private static ulong? SequenceOf(World world)
    {
        try { return world.SequenceNumber; }
        catch { return null; }
    }

    private static void Log(string line) => Plugin.Logger?.LogInfo(line);

    private static void LogOnce(string key, string line)
    {
        lock (Gate)
            if (!LoggedOnce.Add(key)) return;
        Plugin.Logger?.LogWarning(line);
    }

    private sealed class Backend : IUiWorldPickBackend
    {
        public bool Ready(out string why)
        {
            var world = Worlds.Client;
            if (world == null)
            {
                why = "no client world (join a game first)";
                return false;
            }

            if (!TryLocalCharacter(world.EntityManager, out var character))
            {
                why = "no local character yet";
                return false;
            }

            _armedWorldSeq = SequenceOf(world);
            _armedCharacter = character;
            why = "";
            return true;
        }

        public bool StillReady(out string why)
        {
            var world = Worlds.Client;
            if (world == null)
            {
                why = "the client world is gone (left, kicked or disconnected)";
                return false;
            }

            if (SequenceOf(world) != _armedWorldSeq)
            {
                why = "the client world changed";
                return false;
            }

            if (!world.IsCreated || !world.EntityManager.Exists(_armedCharacter))
            {
                why = "the local character is gone";
                return false;
            }

            why = "";
            return true;
        }

        public void Armed(UiWorldPickHandle pick)
        {
            EnsureHook();
            EnsureFrame();
            if (_escape == null)
            {
                _escape = Ui.PushEscape(OnEscape);
            }

            _prompt = pick.Options.Prompt;
            ShowBand();
        }

        public void Disarmed()
        {
            ReleaseBand();
            ReleaseEscape();
            // The hook and the frame stay while the swallow list has entries; OnFrame removes them.
        }
    }

    private static bool TryLocalCharacter(EntityManager em, out Entity character)
    {
        character = Entity.Null;
        var locals = Query.Entities(em, includeDisabled: false, includeSystems: false,
            ComponentIndex.Of<LocalCharacter>(ComponentType.AccessMode.ReadOnly));
        if (locals.Count == 0 || !em.Exists(locals[0])) return false;
        character = locals[0];
        return true;
    }

    // ---- Escape precedence ----

    /// <summary>The Escape lease: cancel the pick only when it is topmost; otherwise pass the press on.</summary>
    private static bool OnEscape()
    {
        UiWorldPickCoordinator? c;
        lock (Gate) c = _coordinator;
        if (c == null || !c.IsArmed) return false;
        var owner = EscapeOwnerNow(c);
        if (owner != UiWorldPickEscapeOwner.Pick)
        {
            Plugin.Logger?.LogDiag("Ui: world pick left Escape to " + owner + "; the pick stays armed.");
            return false;
        }

        return c.Escape();
    }

    private static UiWorldPickEscapeOwner EscapeOwnerNow(UiWorldPickCoordinator c)
    {
        bool dialog = false, capture = false, chat = false;
        int others = 0;
        try { dialog = UiDialog.Current != null || UiChoiceHandle.AnyOpen; } catch { /* */ }
        try { capture = Keybinds.EscapeBelongsToCapture; } catch { /* */ }
        try { chat = Keybinds.ChatFocusNow == ChatFocus.Focused; } catch { /* */ }
        try
        {
            foreach (var panel in Ui.ShownPanelsSnapshot())
                if (!ReferenceEquals(panel, _band)) others++;
        }
        catch { /* */ }

        return UiWorldPickEscape.Decide(c.IsArmed, dialog, capture, chat, others, UiWorldPickEscape.GameMenuOpen(MenuTypeNow()));
    }

    /// <summary><c>OpenHUDMenuSystem.CurrentMenuType</c> of the client world (the system cached per world), or -1.</summary>
    private static int MenuTypeNow()
    {
        try
        {
            var world = Worlds.Client;
            if (world == null) return -1;
            if (SequenceOf(world) != _hudWorldSeq || _hudSystem == null)
            {
                _hudWorldSeq = SequenceOf(world);
                _hudSystem = world.GetExistingSystemManaged<OpenHUDMenuSystem>();
            }

            return _hudSystem == null ? -1 : (int)_hudSystem.CurrentMenuType;
        }
        catch
        {
            _hudSystem = null;
            return -1;
        }
    }

    /// <summary>Per frame while armed: the band is shown (again, after an Escape sweep) and in the mode the precedence asks for.</summary>
    private static void UpdateBand(UiWorldPickCoordinator c)
    {
        if (!c.IsArmed) return;
        if (_band is not UiPanelHandle { IsDisposed: false } handle)
        {
            ShowBand();
            return;
        }

        if (!handle.IsShownNow)
        {
            try
            {
                handle.Show();
                StyleBand(handle);
            }
            catch (Exception e) { Plugin.Logger?.LogWarning("Ui: world pick prompt re-show threw: " + e.Message); }
        }

        var mode = UiWorldPickEscape.BandMode(EscapeOwnerNow(c));
        if (mode == _bandMode) return;
        _bandMode = mode;
        try { handle.CaptureMode = mode; }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: world pick prompt capture mode threw: " + e.Message); }
    }

    private static void EnsureHook()
    {
        if (_hook != null) return;
        _hook = SystemHooks.Before<OpenHUDMenuSystem>(OnBeforeOpenMenu);
        Plugin.Logger?.LogDiag("Ui: world pick hook on OpenHUDMenuSystem installed.");
    }

    private static void ReleaseHook()
    {
        var h = _hook;
        _hook = null;
        if (h != null)
        {
            try { h.Dispose(); }
            catch (Exception e) { Plugin.Logger?.LogWarning("Ui: world pick hook dispose threw: " + e.Message); }
            Plugin.Logger?.LogDiag("Ui: world pick hook removed.");
        }

        if (_frameHooked)
        {
            try { Plugin.Frame -= OnFrame; } catch { /* */ }
            _frameHooked = false;
        }
    }

    private static void EnsureFrame()
    {
        if (_frameHooked) return;
        Plugin.Frame += OnFrame;
        _frameHooked = true;
    }

    private static void ReleaseEscape()
    {
        var e = _escape;
        _escape = null;
        try { e?.Dispose(); } catch { /* */ }
    }

    private static void OnFrame()
    {
        UiWorldPickCoordinator? c;
        lock (Gate) c = _coordinator;
        try { c?.Tick(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: world pick tick threw: " + e.Message); }
        if (c != null) UpdateBand(c);

        if (Swallow.Count > 0)
        {
            var world = Worlds.Client;
            if (world == null) Swallow.Clear();
            else
            {
                var em = world.EntityManager;
                Swallow.Prune(Now(), key => em.Exists(new Entity { Index = (int)(key >> 32), Version = (int)(uint)key }));
            }
        }

        if ((c == null || !c.IsArmed) && Swallow.Count == 0) ReleaseHook();
    }

    // ---- the hook ----

    private static void OnBeforeOpenMenu(SystemHookContext ctx)
    {
        UiWorldPickCoordinator? coordinator;
        lock (Gate) coordinator = _coordinator;
        if (coordinator == null || (!coordinator.IsArmed && Swallow.Count == 0)) return;
        var world = ctx.World;
        if (world == null || !world.IsCreated) return;
        try { Scan(world, coordinator); }
        catch (Exception e) { LogOnce("scan:" + e.GetType().Name, "Ui: world pick scan threw (logged once): " + e); }
    }

    private static void Scan(World world, UiWorldPickCoordinator coordinator)
    {
        var em = world.EntityManager;
        var eventType = ComponentIndex.Of<OpenMenuEvent>(ComponentType.AccessMode.ReadOnly);
        if (!EventQuery.TryGet(world, out var query))
        {
            query = Query.Build(em, includeDisabled: false, includeSystems: false, eventType);
            EventQuery.Store(world, query);
        }

        if (query.CalculateEntityCount() == 0) return;
        var events = new List<Entity>();
        var array = query.ToEntityArray(Allocator.Temp);
        try
        {
            for (int i = 0; i < array.Length; i++) events.Add(array[i]);
        }
        finally { array.Dispose(); }

        double now = Now();
        foreach (var ev in events)
        {
            if (!em.Exists(ev)) continue;
            byte* raw = (byte*)em.GetComponentDataRawRO(ev, eventType.TypeIndex);
            if (raw == null) continue;
            int menu = raw[Layout.OpenMenuEventMenuOffset];
            var init = new Entity
            {
                Index = *(int*)(raw + Layout.OpenMenuEventInitDataEntityOffset),
                Version = *(int*)(raw + Layout.OpenMenuEventInitDataEntityOffset + sizeof(int)),
            };
            if (!UiWorldPickMenus.TryGet(menu, out var info)) continue;
            if (!TryReadInit(em, menu, info.Name, init, out var target, out var buff)) continue;

            long buffKey = UiWorldPickSwallowList.Key(buff.Index, buff.Version);
            if (buff != Entity.Null && Swallow.Contains(buffKey, now))
            {
                DestroyPair(em, ev, init);
                Plugin.Logger?.LogDiag("Ui: world pick kept a repeated " + info.Name + " closed (buff " + buff.Index + ":" + buff.Version + ").");
                continue;
            }

            var offer = coordinator.Offer(menu);
            if (offer == UiWorldPickOffer.PassThrough) continue;
            var buffForSend = buff;
            var plan = UiWorldPickSuppression.Decide(offer, buff != Entity.Null, () => SendStop(em, buffForSend));
            if (offer == UiWorldPickOffer.TakeAndSuppress && !plan.Suppressed)
                Plugin.Logger?.LogWarning("Ui: world pick could not end the interaction on the server ("
                                          + (buff == Entity.Null ? "no interact buff" : "the stop message was not sent")
                                          + "); the " + info.Name + " opens as usual.");
            if (plan.Suppressed) DestroyPair(em, ev, init);
            if (plan.Swallow) Swallow.Add(buffKey, now);

            UnityUiRuntime.ReadNetworkId(em, target, out int n0, out int n1, out int n2);
            coordinator.Complete(menu, target.Index, target.Version, WorldEpoch.Current, plan.Suppressed, n0, n1, n2);
        }
    }

    /// <summary>The init entity's target and interact buff, when it carries the menu's own InitData.</summary>
    private static bool TryReadInit(EntityManager em, int menu, string menuName, Entity init, out Entity target, out Entity buff)
    {
        target = Entity.Null;
        buff = Entity.Null;
        if (!InitSpecs.TryGetValue(menu, out var spec)) return false;
        if (!VerifyInit(menu, spec)) return false;
        var type = spec.Component();
        if (!em.Exists(init) || !em.HasComponent(init, type))
        {
            LogOnce("noinit:" + menu, "Ui: world pick: the " + menuName + " init entity has no " + spec.Type.FullName
                                      + "; that interact is passed through (logged once).");
            return false;
        }

        byte* data = (byte*)em.GetComponentDataRawRO(init, type.TypeIndex);
        if (data == null) return false;
        target = new Entity { Index = *(int*)(data + InitTargetOffset), Version = *(int*)(data + InitTargetOffset + 4) };
        buff = new Entity { Index = *(int*)(data + InitBuffOffset), Version = *(int*)(data + InitBuffOffset + 4) };
        return target != Entity.Null;
    }

    /// <summary>Refuse a menu whose interop InitData does not put the target at 0 and the buff at 8.</summary>
    private static bool VerifyInit(int menu, InitSpec spec)
    {
        lock (Gate)
            if (VerifiedInit.TryGetValue(menu, out bool ok)) return ok;
        bool good;
        string why = "";
        try
        {
            int t = (int)Marshal.OffsetOf(spec.Type, spec.TargetField);
            int b = (int)Marshal.OffsetOf(spec.Type, "InteractBuff");
            good = t == InitTargetOffset && b == InitBuffOffset;
            if (!good) why = spec.TargetField + " at 0x" + t.ToString("X") + ", InteractBuff at 0x" + b.ToString("X");
        }
        catch (Exception e)
        {
            good = false;
            why = e.Message;
        }

        lock (Gate) VerifiedInit[menu] = good;
        if (!good)
            LogOnce("layout:" + menu, "Ui: world pick refuses " + spec.Type.FullName + " (" + why + "); that menu is passed through.");
        return good;
    }

    private static void DestroyPair(EntityManager em, Entity ev, Entity init)
    {
        try { if (em.Exists(ev)) em.DestroyEntity(ev); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: world pick could not destroy the menu event: " + e.Message); }
        try { if (em.Exists(init)) em.DestroyEntity(init); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: world pick could not destroy the menu init data: " + e.Message); }
    }

    /// <summary>Tell the server the interaction ended (what the game's panel mappers send on close).</summary>
    private static bool SendStop(EntityManager em, Entity buff)
    {
        try
        {
            if (_stopLayoutOk == null)
            {
                _stopLayoutOk = Marshal.SizeOf<NetworkId>() == NetworkIdSize
                                && (int)Marshal.OffsetOf<StopInteractingWithObjectEvent>(nameof(StopInteractingWithObjectEvent.Target)) == 0;
                if (_stopLayoutOk == false)
                    Plugin.Logger?.LogWarning("Ui: world pick: StopInteractingWithObjectEvent does not match the expected layout; "
                                              + "picks let the panel open instead of keeping it closed.");
            }

            if (_stopLayoutOk != true) return false;
            var netType = ComponentIndex.Of<NetworkId>(ComponentType.AccessMode.ReadOnly);
            if (!em.Exists(buff) || !em.HasComponent(buff, netType))
            {
                Plugin.Logger?.LogWarning("Ui: world pick: the interact buff " + buff.Index + ":" + buff.Version
                                          + " has no NetworkId; the stop message cannot be sent.");
                return false;
            }

            int* id = (int*)em.GetComponentDataRawRO(buff, netType.TypeIndex);
            if (id == null) return false;
            int a = id[0], b = id[1], c = id[2];

            var type = ComponentIndex.Of<StopInteractingWithObjectEvent>(ComponentType.AccessMode.ReadWrite);
            var ev = em.CreateEntity(
                type,
                ComponentIndex.Of<NetworkEventType>(ComponentType.AccessMode.ReadWrite),
                ComponentIndex.Of<SendNetworkEventTag>(ComponentType.AccessMode.ReadWrite));
            try
            {
                int* raw = (int*)em.GetComponentDataRawRW(ev, type.TypeIndex);
                if (raw == null) throw new InvalidOperationException("GetComponentDataRawRW returned null for StopInteractingWithObjectEvent");
                raw[0] = a;
                raw[1] = b;
                raw[2] = c;
                // NetworkEventType carries bools: typed SetComponentData, as DyeSender and the character create do.
                em.SetComponentData(ev, new NetworkEventType { EventId = StopInteractingEventId, IsAdminEvent = false, IsDebugEvent = false });
            }
            catch
            {
                em.DestroyEntity(ev); // never leave a send-tagged event with EventId 0 behind
                throw;
            }

            Plugin.Logger?.LogInfo("Ui: world pick sent StopInteractingWithObjectEvent for buff " + buff.Index + ":" + buff.Version + ".");
            return true;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: world pick stop message threw: " + e.Message);
            return false;
        }
    }

    // ---- the band ----

    private static void ShowBand()
    {
        ReleaseBand();
        try
        {
            var panel = Ui.Panel(UiWorldPickCopy.BandTitle, BandOwner, FrameworkVersions.CorePluginId);
            if (panel is not UiPanelHandle { Enabled: true, IsDisposed: false } handle)
            {
                try { panel.Dispose(); } catch { /* */ }
                return;
            }

            try { panel.Footer = ""; } catch { /* */ }
            UiWorldPickCoordinator? c;
            lock (Gate) c = _coordinator;
            _band = panel;
            _bandMode = c == null ? UiCaptureMode.Never : UiWorldPickEscape.BandMode(EscapeOwnerNow(c));
            panel.CaptureMode = _bandMode;
            Ui.HidePaneTitle(panel);
            Ui.SelfPlacePanel(panel);
            panel.Show();
            StyleBand(handle);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: world pick prompt could not be shown: " + e.Message);
        }
    }

    private static void StyleBand(UiPanelHandle handle)
    {
        if (Ui.CurrentRuntime() is IWorldPickBandRuntime band && handle.RuntimePanel is { } runtimePanel)
            band.StyleWorldPickBand(runtimePanel, _prompt, InteractKeyText(), UiWorldPickCopy.EscapeKey);
    }

    private static void ReleaseBand()
    {
        var band = _band;
        _band = null;
        if (band == null) return;
        try { band.Dispose(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: world pick prompt dispose threw: " + e.Message); }
    }

    /// <summary>The game's own text for the Interact key (primary slot), else the word Interact.</summary>
    private static string InteractKeyText()
    {
        try
        {
            var snapshot = KeybindArbiter.GameSnapshot();
            if (snapshot.Available)
            {
                foreach (var claim in snapshot.Claims)
                {
                    if (claim.Source != KeybindSource.Game || claim.Slot != 0) continue;
                    if (!string.Equals(claim.ActionId, "Interact", StringComparison.Ordinal)) continue;
                    var text = claim.ChordText;
                    if (!string.IsNullOrWhiteSpace(text) && !string.Equals(text, "None", StringComparison.Ordinal)) return text.Trim();
                }
            }
        }
        catch (Exception e)
        {
            LogOnce("interactkey", "Ui: world pick could not read the Interact key: " + e.Message);
        }

        return UiWorldPickCopy.InteractFallback;
    }

    // ---- init data per menu ----

    private readonly struct InitSpec
    {
        internal InitSpec(Type type, string targetField, Func<ComponentType> component)
        {
            Type = type;
            TargetField = targetField;
            Component = component;
        }

        internal Type Type { get; }

        internal string TargetField { get; }

        internal Func<ComponentType> Component { get; }
    }

    private static InitSpec Spec<T>(string targetField) =>
        new(typeof(T), targetField, () => ComponentIndex.Of<T>(ComponentType.AccessMode.ReadOnly));

    /// <summary>
    /// Menu → its init data (client metadata: every one has the target Entity at 0x0 and the interact
    /// buff Entity at 0x8). Pairings are by the game's own names; each is checked at runtime by
    /// <c>HasComponent</c> on the init entity before it is read.
    /// </summary>
    private static readonly Dictionary<int, InitSpec> InitSpecs = new()
    {
        [UiWorldPickMenus.ContainerMenu] = Spec<ContainerMenu.InitData>("TargetContainer"),
        [UiWorldPickMenus.WorkstationMenu] = Spec<WorkstationMenu.InitData>("TargetWorkstation"),
        [UiWorldPickMenus.RefinementstationMenu] = Spec<RefinementstationMenu.InitData>("TargetWorkstation"),
        [UiWorldPickMenus.UnitSpawnerstationMenu] = Spec<UnitSpawnerstationMenu.InitData>("TargetWorkstation"),
        [UiWorldPickMenus.ServantCoffinMenu] = Spec<ServantCoffinstationMenu.InitData>("TargetWorkstation"),
        [UiWorldPickMenus.ResearchstationMenu] = Spec<ResearchstationMenu.InitData>("TargetWorkstation"),
        [UiWorldPickMenus.SalvagestationMenu] = Spec<SalvagestationMenu.InitData>("TargetWorkstation"),
        [UiWorldPickMenus.JewelCraftingMenu] = Spec<JewelCraftingMenu.InitData>("TargetStation"),
        [UiWorldPickMenus.ForgeMenu] = Spec<ForgeMenu.InitData>("TargetWorkstation"),
        [UiWorldPickMenus.LightningRodMenu] = Spec<LightningRodMenu.InitData>("TargetWorkstation"),
        [UiWorldPickMenus.RespecStationMenu] = Spec<RespecStationMenu.InitData>("TargetWorkstation"),
        [UiWorldPickMenus.MusicPlayerStationMenu] = Spec<MusicPlayerStationMenu.InitData>("TargetWorkstation"),
        [UiWorldPickMenus.SpellSchoolPassiveMenu] = Spec<SpellSchoolPassiveStationMenu.InitData>("TargetWorkstation"),
        [UiWorldPickMenus.BloodMixerMenu] = Spec<BloodMixerMenu.InitData>("TargetWorkstation"),
        [UiWorldPickMenus.StablesMenu] = Spec<StablesMenu.InitData>("TargetWorkstation"),
        [UiWorldPickMenus.FusionForgeMenu] = Spec<FusionForgeMenu.InitData>("TargetWorkstation"),
        [UiWorldPickMenus.InventoryRouteStationMenu] = Spec<InventoryRouteStationMenu.InitData>("TargetWorkstation"),
        [UiWorldPickMenus.CastleHeartMenu] = Spec<CastleHeartMenu.InitData>("TargetWorkstation"),
        [UiWorldPickMenus.PrisonMenu] = Spec<PrisonMenu.InitData>("TargetWorkstation"),
        [UiWorldPickMenus.ServantMenu] = Spec<UseServantMenu.InitData>("TargetServant"),
        [UiWorldPickMenus.TraderMenu] = Spec<TraderMenu.InitData>("TargetTrader"),
        [UiWorldPickMenus.CastleHeartRebuildingMenu] = Spec<CastleHeartRebuildMenu.InitData>("Target"),
        [UiWorldPickMenus.ArenaStationMenu] = Spec<ArenaStationMenu.InitData>("TargetArenaStation"),
    };
}
#endif
