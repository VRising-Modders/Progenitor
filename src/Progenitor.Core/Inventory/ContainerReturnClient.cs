#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;
using System.Diagnostics;
using Progenitor.Ecs;
using Progenitor.Inputs;
using Progenitor.Ui;
using Progenitor.WorldState;
using ProjectM;
using ProjectM.UI;
using Unity.Entities;
using Unity.Transforms;

namespace Progenitor.Inventory;

/// <summary>
/// Client, Core-owned: remembers the chest, station or castle heart window the player last opened at the object, and
/// opens it again the game's own way (<c>ctx.Inventory.ReopenLast</c>, and a container preview that took its place).
///
/// <para><b>Remember.</b> Once a frame, only while the game's open HUD menu is an interact window
/// (<c>OpenHUDMenuSystem.CurrentMenuType</c>, the world pick's cached read), the panel sites' mapper scan names the shown
/// window and its object; a container window with no interaction behind it (a remote preview's) is skipped. The object's
/// network id words and where the character stands are kept with it.</para>
///
/// <para><b>Reopen.</b> The interaction the player made, repeated: hover the object for three frames, press Interact
/// for 0.4 s (the Dev bot driver's order, and the container open gate's repeat), then hand the input back and only watch
/// for the window. Two framework leases, held only for that half second: the buttons with
/// <see cref="UserPolicy.Yield"/>, the hover with <see cref="UserPolicy.Override"/> so a mouse movement cannot swap the
/// hovered object under the press. A window the player closed themselves is never reopened. Nothing is written to
/// the game's menus and no event is built: the server sees an ordinary interaction, with its own range and permission
/// checks and every server-side open handler (the open gate) in front of it.</para>
/// </summary>
internal static class ContainerReturnClient
{
    private const string Holder = "progenitor.container-return";
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static bool _installed;
    private static Remembered? _last;
    private static Reopen? _job;
    private static string _lastOutcome = "";

    internal static double Now => Clock.Elapsed.TotalSeconds;

    internal static void Install()
    {
        if (_installed || Plugin.IsServerProcess) return;
        _installed = true;
        GameWindowUnderPanel.MenuTypeNow = WorldPickBackend.MenuTypeNow;
        GameWindowUnderPanel.CloseGameWindow = CloseInteractWindow;
        Plugin.Frame += Tick;
    }

    /// <summary>
    /// MOUNTESC1: the game's own close for the open chest, station or heart window (<c>HUDMenuManager.CloseMenu</c>, the
    /// call the world pick and the preview already make), only while such a window is the open menu.
    /// </summary>
    private static bool CloseInteractWindow()
    {
        var world = Worlds.Client;
        if (world == null || !world.IsCreated) return false;
        if (!GameWindowUnderPanel.IsInteractWindow(WorldPickBackend.MenuTypeNow())) return false;
        HUDMenuManager.CloseMenu(world);
        return true;
    }

    internal static void Uninstall()
    {
        if (!_installed) return;
        _installed = false;
        GameWindowUnderPanel.MenuTypeNow = GameWindowUnderPanel.NoMenu;
        GameWindowUnderPanel.CloseGameWindow = GameWindowUnderPanel.NoClose;
        Plugin.Frame -= Tick;
        var job = _job;
        _job = null;
        job?.End(ContainerReopenResult.Cancelled);
        _last = null;
    }

    /// <summary>The remembered window, or null (<c>ctx.Inventory.LastWindow</c>).</summary>
    internal static ContainerWindowInfo? LastWindow()
    {
        var last = _last;
        if (last == null || !SameWorld(last)) return null;
        return last.Info(Now);
    }

    /// <summary>
    /// Open the remembered window again (<c>ctx.Inventory.ReopenLast</c>). A running reopen is replaced
    /// (<see cref="ContainerReopenResult.Cancelled"/>). <paramref name="done"/> runs once on the main thread with the
    /// outcome, also when the call's own answer is not <see cref="ContainerReopenResult.Started"/>.
    /// <paramref name="anyAge"/> skips the age limit: a container preview returning to the window it took the place of,
    /// however long it ran.
    /// </summary>
    internal static ContainerReopenResult ReopenLast(Action<ContainerReopenResult>? done, string why, object? owner = null, bool anyAge = false,
        ContainerWindowInfo? openWhenStarted = null)
    {
        var world = Worlds.Client;
        bool inGame = _installed && world != null && world.IsCreated;
        var last = _last;
        bool remembered = inGame && last != null && SameWorld(last);
        bool present = false, moved = false;
        double age = remembered ? (anyAge ? 0 : last!.Age(Now)) : double.PositiveInfinity;
        if (remembered)
        {
            var em = world!.EntityManager;
            present = last!.Rebind(em);
            moved = present && last.Moved(em);
        }
        bool hasSnapshot = openWhenStarted != null;
        bool sameOpening = hasSnapshot && remembered && openWhenStarted!.OpenCount == last!.OpenCount
            && openWhenStarted.NetworkIdIndex == last.N0 && openWhenStarted.NetworkIdGeneration == last.N1;
        var answer = ContainerReturnRules.Check(inGame, remembered, age, present, moved,
            closedByPlayer: remembered && last!.ClosedByPlayer, hasSnapshot: hasSnapshot,
            snapshotWasOpen: openWhenStarted?.IsOpen == true, snapshotSameOpening: sameOpening);
        if (answer != ContainerReopenResult.Started)
        {
            Note(why, answer, last);
            Report(done, answer);
            return answer;
        }
        _job?.End(ContainerReopenResult.Cancelled);
        _job = new Reopen(world!, last!, done, why, owner);
        Plugin.Logger?.LogInfo("ContainerReturn: reopening " + last!.Describe() + " (" + why + ").");
        return ContainerReopenResult.Started;
    }

    private static double _flowCloseAt = double.NegativeInfinity;
    private static int _openCount;

    /// <summary>
    /// A Progenitor flow is about to close the game's chest or station window (a world pick with
    /// <c>CloseGameWindows</c>, a container preview taking its place): that close is not the player's
    /// (<see cref="ContainerReopenResult.ClosedByPlayer"/>).
    /// </summary>
    internal static void NoteFlowClose() => _flowCloseAt = Now;

    /// <summary>A plugin unloads: its running reopen ends without calling back into it.</summary>
    internal static void CancelFor(object owner)
    {
        var job = _job;
        if (job == null || !ReferenceEquals(job.Owner, owner)) return;
        _job = null;
        job.Drop();
    }

    private static void Tick()
    {
        var world = Worlds.Client;
        if (world == null || !world.IsCreated)
        {
            var job = _job;
            _job = null;
            job?.End(ContainerReopenResult.Cancelled);
            return;
        }
        try { Track(world); }
        catch (Exception e) { WarnOnce("track", e); }
        var running = _job;
        if (running == null) return;
        try { running.Tick(); }
        catch (Exception e)
        {
            WarnOnce("reopen", e);
            running.End(ContainerReopenResult.Cancelled);
        }
        if (running.Finished && ReferenceEquals(_job, running)) _job = null;
    }

    /// <summary>
    /// Remember the shown chest, station or heart window, if the player opened it at the object; and when the remembered
    /// window stops showing, note who closed it (<see cref="ContainerReturnRules.ClosedByPlayer"/>). Each new opening
    /// gets a new <see cref="ContainerWindowInfo.OpenCount"/>.
    /// </summary>
    private static void Track(World world)
    {
        int menu = WorldPickBackend.MenuTypeNow();
        bool real = false;
        var kind = VanillaPanelKind.None;
        var target = Entity.Null;
        if (GameWindowUnderPanel.IsInteractWindow(menu) && UnityUiRuntime.TryOpenInteractWindow(world, out kind, out target, out var interact))
        {
            bool previewOwns = false;
            try { previewOwns = ContainerPreviewClient.Current?.Machine.OwnsWindow == true; } catch { /* */ }
            real = ContainerReturnRules.Remembers(kind, kind != VanillaPanelKind.Container || interact != Entity.Null, previewOwns)
                && world.EntityManager.Exists(target);
        }
        var last = _last;
        if (last != null && last.WasOpen && !(real && ReferenceEquals(last.World, world) && last.Target == target))
            last.MarkClosed(ContainerReturnRules.ClosedByPlayer(menu, Now - _flowCloseAt));
        // During a world pick the interactions are picks: the window the pick closed stays the one to return to.
        if (!real || WorldPickBackend.PickArmed) return;
        var em = world.EntityManager;
        if (last == null || !ReferenceEquals(last.World, world) || last.Target != target || last.Epoch != WorldEpoch.Current)
        {
            if (!ContainerPreviewClient.TryWords(em, target, out int n0, out int n1, out int n2)) return;
            last = _last = new Remembered(world, WorldEpoch.Current, kind, menu, target, n0, n1, n2);
        }
        if (!last.WasOpen) last.MarkOpen(++_openCount);
        last.Seen(em, Now);
    }

    /// <summary>True when the game's window shows <paramref name="target"/> now.</summary>
    internal static bool WindowShows(World world, Entity target)
    {
        if (!GameWindowUnderPanel.IsInteractWindow(WorldPickBackend.MenuTypeNow())) return false;
        return UnityUiRuntime.TryOpenInteractWindow(world, out _, out var shown, out _) && shown == target;
    }

    private static bool SameWorld(Remembered r)
    {
        var world = Worlds.Client;
        return world != null && world.IsCreated && ReferenceEquals(world, r.World) && r.Epoch == WorldEpoch.Current;
    }

    private static void Report(Action<ContainerReopenResult>? done, ContainerReopenResult result)
    {
        if (done == null) return;
        try { done(result); }
        catch (Exception e) { Plugin.Logger?.LogWarning("ContainerReturn: a done callback threw: " + e.Message); }
    }

    private static void Note(string why, ContainerReopenResult result, Remembered? last)
    {
        _lastOutcome = result + " (" + why + ")";
        Plugin.Logger?.LogInfo("ContainerReturn: " + (last == null ? "no window" : last.Describe()) + " not reopened: " + result + " (" + why + ").");
    }

    private static readonly HashSet<string> Warned = new(StringComparer.Ordinal);

    private static void WarnOnce(string what, Exception e)
    {
        if (Warned.Add(what)) Plugin.Logger?.LogWarning("ContainerReturn: " + what + " threw: " + e.Message);
    }

    /// <summary>Dev bridge readback (<c>container_preview</c> status on the client).</summary>
    internal static Dictionary<string, object?> Describe()
    {
        var info = LastWindow();
        return new Dictionary<string, object?>
        {
            ["lastWindow"] = info?.ToString(),
            ["reopening"] = _job != null,
            ["lastOutcome"] = _lastOutcome.Length == 0 ? null : _lastOutcome,
        };
    }

    /// <summary>The remembered window: the object, its network id words, and where the character stood.</summary>
    private sealed class Remembered
    {
        internal Remembered(World world, int epoch, VanillaPanelKind kind, int menu, Entity target, int n0, int n1, int n2)
        {
            World = world; Epoch = epoch; Kind = kind; Menu = menu; Target = target; N0 = n0; N1 = n1; N2 = n2;
        }

        internal World World { get; }
        internal int Epoch { get; }
        internal VanillaPanelKind Kind { get; }
        internal int Menu { get; }
        internal Entity Target { get; private set; }
        internal int N0 { get; }
        internal int N1 { get; }
        internal int N2 { get; }
        private double _seenAt = double.NegativeInfinity;

        /// <summary>The window shows this object now (as of the last frame).</summary>
        internal bool WasOpen { get; private set; }

        /// <summary>Which opening this is (<see cref="ContainerWindowInfo.OpenCount"/>).</summary>
        internal int OpenCount { get; private set; }

        /// <summary>The last close was the player's own (<see cref="ContainerReopenResult.ClosedByPlayer"/>).</summary>
        internal bool ClosedByPlayer { get; private set; }

        internal void MarkOpen(int count) { WasOpen = true; ClosedByPlayer = false; OpenCount = count; }

        internal void MarkClosed(bool byPlayer)
        {
            WasOpen = false;
            ClosedByPlayer = byPlayer;
            Plugin.Logger?.LogInfo("ContainerReturn: " + Describe() + " closed" + (byPlayer ? " by the player." : " by another window or a Progenitor flow."));
        }
        private bool _stoodKnown;
        private float _stoodX, _stoodZ;

        internal void Seen(EntityManager em, double now)
        {
            _seenAt = now;
            if (LocalCharacters.TryGet(em, out var character) && character.TryRead<Translation>(em, out var at))
            {
                _stoodX = at.Value.x; _stoodZ = at.Value.z; _stoodKnown = true;
            }
        }

        internal double Age(double now) => now - _seenAt;

        /// <summary>The object is still on this client: the same entity with the same network id, or found again by it once.</summary>
        internal bool Rebind(EntityManager em)
        {
            if (Present(em)) return true;
            var found = ContainerPreviewClient.FindByNetworkId(em, N0, N1);
            if (found == Entity.Null) return false;
            Target = found;
            return Present(em);
        }

        internal bool Present(EntityManager em) =>
            Target != Entity.Null && em.Exists(Target)
            && ContainerPreviewClient.TryWords(em, Target, out int a, out int b, out int c) && a == N0 && b == N1 && c == N2;

        internal bool Moved(EntityManager em)
        {
            if (!_stoodKnown) return true;
            if (!LocalCharacters.TryGet(em, out var character) || !character.TryRead<Translation>(em, out var at)) return true;
            return ContainerReturnRules.Moved(_stoodX, _stoodZ, at.Value.x, at.Value.z);
        }

        internal ContainerWindowInfo Info(double now)
        {
            bool open = false;
            try { open = WindowShows(World, Target); } catch { /* */ }
            return new ContainerWindowInfo(Kind, Menu, Target.Index, Target.Version, N0, N1, open, open ? 0 : Math.Max(0, Age(now)), OpenCount);
        }

        internal string Describe() => Kind + " " + Target.Index + ":" + Target.Version + " net=" + N0 + ":" + N1;
    }

    /// <summary>One reopen: the plan plus the input lease it holds from the hover to the end.</summary>
    private sealed class Reopen
    {
        private readonly World _world;
        private readonly Remembered _target;
        private Action<ContainerReopenResult>? _done;
        private readonly string _why;
        private readonly ContainerReopenPlan _plan;
        private InputLease? _hover;
        private InputLease? _buttons;

        internal Reopen(World world, Remembered target, Action<ContainerReopenResult>? done, string why, object? owner)
        {
            _world = world; _target = target; _done = done; _why = why; Owner = owner;
            _plan = new ContainerReopenPlan(Now);
        }

        /// <summary>The plugin that asked (null for Core's own return after a preview).</summary>
        internal object? Owner { get; }

        /// <summary>End without the callback (its plugin is unloading).</summary>
        internal void Drop()
        {
            _done = null;
            End(ContainerReopenResult.Cancelled);
        }

        internal bool Finished => _plan.Done;

        internal void Tick()
        {
            if (_plan.Done) return;
            var s = new ContainerReopenSample { SameWorld = _world.IsCreated && SameWorld(_target) };
            if (s.SameWorld)
            {
                var em = _world.EntityManager;
                s.TargetPresent = _target.Present(em);
                s.Moved = s.TargetPresent && _target.Moved(em);
                int menu = WorldPickBackend.MenuTypeNow();
                s.AnyMenuOpen = menu != 0;
                s.WindowOnTarget = s.TargetPresent && WindowShows(_world, _target.Target);
                s.OtherWindowOpen = menu != 0 && !s.WindowOnTarget && OtherWindow(menu);
                try { s.CameraPreview = Progenitor.Cameras.PluginCamera.CameraPreview.Current?.Active == true; } catch { /* */ }
            }
            switch (_plan.Step(Now, s))
            {
                case ContainerReopenStep.StartHover:
                    StartHover();
                    break;
                case ContainerReopenStep.Press:
                    if (_hover == null || !_hover.Active || _buttons == null || !_buttons.Active) { Finish(_plan.End(ContainerReopenResult.InputBusy)); break; }
                    _buttons.Press(SyncedButtons.Interact, ContainerReopenPlan.PressSeconds);
                    Plugin.Logger?.LogInfo("ContainerReturn: pressing Interact on " + _target.Describe() + ".");
                    break;
                case ContainerReopenStep.ReleaseInput:
                    ReleaseInput();
                    break;
                case ContainerReopenStep.Finish:
                    Finish(ContainerReopenStep.Finish);
                    break;
            }
        }

        /// <summary>
        /// A different window than the one being reopened: another menu type, or the same window bound to another object.
        /// The reopened window's own menu with no object read yet is not a different one.
        /// </summary>
        private bool OtherWindow(int menu)
        {
            if (menu != _target.Menu) return true;
            return UnityUiRuntime.TryOpenInteractWindow(_world, out _, out var shown, out _) && shown != Entity.Null && shown != _target.Target;
        }

        /// <summary>
        /// Two leases for the hover and the press, each only as long as needed. The buttons yield to the player
        /// (<see cref="UserPolicy.Yield"/>): a press of their own suspends ours. The hover overrides the player's mouse for
        /// the half second of the hover and the press only, so a mouse movement cannot swap the hovered object under the
        /// press (the press would reach whatever is under the cursor).
        /// </summary>
        private void StartHover()
        {
            var hover = Acquire(InputChannels.Hover, UserPolicy.Override, "hover the chest or station being opened again", out var hoverResult);
            if (hover == null)
            {
                Plugin.Logger?.LogInfo("ContainerReturn: hover lease " + hoverResult + ".");
                Finish(_plan.End(ContainerReopenResult.InputBusy));
                return;
            }
            _hover = hover;
            var buttons = Acquire(InputChannels.Buttons, UserPolicy.Yield, "press Interact on the chest or station being opened again", out var buttonsResult);
            if (buttons == null)
            {
                Plugin.Logger?.LogInfo("ContainerReturn: buttons lease " + buttonsResult + ".");
                Finish(_plan.End(ContainerReopenResult.InputBusy));
                return;
            }
            _buttons = buttons;
            try { _hover.Hover(_target.Target); }
            catch (Exception e)
            {
                Plugin.Logger?.LogInfo("ContainerReturn: the object cannot be hovered (" + e.Message + ").");
                Finish(_plan.End(ContainerReopenResult.Gone));
            }
        }

        private static InputLease? Acquire(InputChannels channels, UserPolicy policy, string reason, out InputAcquireResult result)
        {
            InputLease? lease = null;
            try { result = InputLeases.TryAcquire(Holder, new InputLeaseOptions(channels, policy, preempt: false, reason: reason), out lease); }
            catch (Exception e)
            {
                Plugin.Logger?.LogWarning("ContainerReturn: the input lease threw: " + e.Message);
                result = InputAcquireResult.Faulted;
            }
            return result == InputAcquireResult.Acquired ? lease : null;
        }

        /// <summary>The press is over: hand the hover and the buttons back; the window is watched without them.</summary>
        private void ReleaseInput()
        {
            var hover = _hover; _hover = null;
            var buttons = _buttons; _buttons = null;
            try { buttons?.Dispose(); } catch { /* */ }
            try { hover?.Dispose(); } catch { /* */ }
        }

        /// <summary>End now (a newer reopen, unload, no world).</summary>
        internal void End(ContainerReopenResult result) => Finish(_plan.End(result));

        private void Finish(ContainerReopenStep step)
        {
            if (step != ContainerReopenStep.Finish) return;
            ReleaseInput();
            var result = _plan.Result;
            _lastOutcome = result + " (" + _why + ")";
            Plugin.Logger?.LogInfo("ContainerReturn: " + _target.Describe() + " " + result + " (" + _why + (_plan.PressedEver ? ", Interact pressed" : "") + ").");
            Report(_done, result);
        }
    }
}
#endif
