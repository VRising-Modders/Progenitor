#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;
using System.Diagnostics;
using Progenitor.Ecs;
using Progenitor.Inputs;
using Progenitor.Ui;
using Progenitor.WorldState;
using ProjectM;
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
/// <para><b>Reopen.</b> The interaction the player made, repeated: a one-second framework input lease (hover and
/// buttons, <see cref="UserPolicy.Override"/> so a mouse movement cannot swap the hovered object under the press), hover
/// the object, press Interact (the Dev bot driver's order, and the container open gate's repeat). Nothing is written to
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
        Plugin.Frame += Tick;
    }

    internal static void Uninstall()
    {
        if (!_installed) return;
        _installed = false;
        GameWindowUnderPanel.MenuTypeNow = GameWindowUnderPanel.NoMenu;
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
    internal static ContainerReopenResult ReopenLast(Action<ContainerReopenResult>? done, string why, object? owner = null, bool anyAge = false)
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
        var answer = ContainerReturnRules.Check(inGame, remembered, age, present, moved);
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

    /// <summary>Remember the shown chest, station or heart window, if the player opened it at the object.</summary>
    private static void Track(World world)
    {
        int menu = WorldPickBackend.MenuTypeNow();
        if (!GameWindowUnderPanel.IsInteractWindow(menu)) return;
        if (!UnityUiRuntime.TryOpenInteractWindow(world, out var kind, out var target, out var interact)) return;
        bool previewOwns = false;
        try { previewOwns = ContainerPreviewClient.Current?.Machine.OwnsWindow == true; } catch { /* */ }
        if (!ContainerReturnRules.Remembers(kind, kind != VanillaPanelKind.Container || interact != Entity.Null, previewOwns)) return;
        var em = world.EntityManager;
        if (!em.Exists(target)) return;
        var last = _last;
        if (last == null || !ReferenceEquals(last.World, world) || last.Target != target || last.Epoch != WorldEpoch.Current)
        {
            if (!ContainerPreviewClient.TryWords(em, target, out int n0, out int n1, out int n2)) return;
            last = _last = new Remembered(world, WorldEpoch.Current, kind, menu, target, n0, n1, n2);
        }
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
            return new ContainerWindowInfo(Kind, Menu, Target.Index, Target.Version, N0, N1, open, open ? 0 : Math.Max(0, Age(now)));
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
        private InputLease? _lease;

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
                try { s.CameraPreview = Progenitor.Cameras.PluginCamera.CameraPreview.Current?.Active == true; } catch { /* */ }
            }
            switch (_plan.Step(Now, s))
            {
                case ContainerReopenStep.StartHover:
                    StartHover();
                    break;
                case ContainerReopenStep.Press:
                    if (_lease == null || !_lease.Active) { Finish(_plan.End(ContainerReopenResult.InputBusy)); break; }
                    _lease.Press(SyncedButtons.Interact, ContainerReopenPlan.PressSeconds);
                    Plugin.Logger?.LogInfo("ContainerReturn: pressing Interact on " + _target.Describe() + ".");
                    break;
                case ContainerReopenStep.Finish:
                    Finish(ContainerReopenStep.Finish);
                    break;
            }
        }

        private void StartHover()
        {
            InputAcquireResult acquired;
            try
            {
                acquired = InputLeases.TryAcquire(Holder,
                    new InputLeaseOptions(InputChannels.Hover | InputChannels.Buttons, UserPolicy.Override, preempt: false,
                        reason: "open the chest or station window the player had open again"), out _lease);
            }
            catch (Exception e)
            {
                Plugin.Logger?.LogWarning("ContainerReturn: the input lease threw: " + e.Message);
                acquired = InputAcquireResult.Faulted;
            }
            if (acquired != InputAcquireResult.Acquired || _lease == null)
            {
                Plugin.Logger?.LogInfo("ContainerReturn: input lease " + acquired + ".");
                Finish(_plan.End(ContainerReopenResult.InputBusy));
                return;
            }
            try { _lease.Hover(_target.Target); }
            catch (Exception e)
            {
                Plugin.Logger?.LogInfo("ContainerReturn: the object cannot be hovered (" + e.Message + ").");
                Finish(_plan.End(ContainerReopenResult.Gone));
            }
        }

        /// <summary>End now (a newer reopen, unload, no world).</summary>
        internal void End(ContainerReopenResult result) => Finish(_plan.End(result));

        private void Finish(ContainerReopenStep step)
        {
            if (step != ContainerReopenStep.Finish) return;
            var lease = _lease;
            _lease = null;
            try { lease?.Dispose(); } catch { /* */ }
            var result = _plan.Result;
            _lastOutcome = result + " (" + _why + ")";
            Plugin.Logger?.LogInfo("ContainerReturn: " + _target.Describe() + " " + result + " (" + _why + (_plan.PressedEver ? ", Interact pressed" : "") + ").");
            Report(_done, result);
        }
    }
}
#endif
