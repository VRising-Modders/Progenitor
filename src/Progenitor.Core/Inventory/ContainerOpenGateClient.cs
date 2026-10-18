#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;
using System.Diagnostics;
using Progenitor.Inputs;
using Progenitor.Networking;
using Progenitor.Ui;
using Unity.Entities;

namespace Progenitor.Inventory;

/// <summary>
/// The client half of the container open gate, Core-owned. The server sends a <c>prompt</c> when a plugin asks the
/// player for a password before a container opens: this shows it as a masked dialog (<c>UiDialogs.Prompt</c> with
/// MASKED1) and sends the typed text back, never logging it. A <c>denied</c> or a refused <c>result</c> becomes a toast
/// (<c>ctx.Hud.Notify</c>'s queue). A right answer for an interaction comes back with <c>retry</c>: the server closed
/// that interaction, so the client repeats it once (hover the container, press Interact, the Dev bot driver's order)
/// through a framework input lease; when that cannot run, a toast asks the player to interact again. A preview's prompt
/// goes on by itself on the server.
/// </summary>
internal static unsafe class ContainerOpenGateClient
{
    /// <summary>Owner of the gate's dialogs and toasts.</summary>
    private static readonly object Owner = new();
    private const string Holder = "progenitor.container-gate";
    private const string RememberKey = "remember";

    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static bool _installed;
    private static IDisposable? _dialog;
    private static int _sequence;
    private static ContainerId _container;
    private static ContainerOpenKind _kind;
    private static Retry? _retry;
    private static string _lastNote = "";

    private static double Now => Clock.Elapsed.TotalSeconds;

    internal static void Install()
    {
        if (_installed || Plugin.IsServerProcess) return;
        _installed = true;
        CustomEvents.OnReceive(ContainerGateWire.EventId, OnPacket);
        Handshake.ServerContextCleared += OnContextCleared;
        Plugin.Frame += Tick;
    }

    internal static void Uninstall()
    {
        if (!_installed) return;
        _installed = false;
        CloseDialog();
        EndRetry();
        try { CustomEvents.RemoveHandler(ContainerGateWire.EventId); }
        catch (Exception e) { Plugin.Logger?.LogWarning("ContainerOpenGate: handler removal threw: " + e.Message); }
        Handshake.ServerContextCleared -= OnContextCleared;
        Plugin.Frame -= Tick;
        _sequence = 0;
    }

    private static void OnContextCleared(string _)
    {
        CloseDialog();
        EndRetry();
        _sequence = 0;
    }

    private static void OnPacket(int _, byte* payload, int length)
    {
        if (payload == null || length <= 0 || length > ContainerGateWire.MaxBytes) return;
        var p = ContainerGateWire.Decode(new ReadOnlySpan<byte>(payload, length), fromClient: false);
        if (p == null) return;
        try
        {
            switch (p.Kind)
            {
                case ContainerGateWire.KindPrompt: ShowPrompt(p); break;
                case ContainerGateWire.KindDenied: Toast(p.Mod, p.Message, UiNoticeKind.Warning); break;
                case ContainerGateWire.KindResult: OnResult(p); break;
            }
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("ContainerOpenGate: " + p.Kind + " threw: " + e.GetType().Name); }
    }

    private static void ShowPrompt(ContainerGatePacket p)
    {
        CloseDialog();
        ContainerId.TryParse(p.Container, out var container);
        _sequence = p.Sequence;
        _container = container;
        _kind = p.OpenKind == (int)ContainerOpenKind.Preview ? ContainerOpenKind.Preview : ContainerOpenKind.Interact;
        int sequence = p.Sequence;
        string footer = UiDialogs.Footer(p.Mod, "", showModVersion: p.Mod.Length > 0);
        if (p.Remember)
        {
            // The plugin offers to remember the password: the masked field and one box, in a form.
            _dialog = UiDialogs.Form(Owner, "progenitor", footer, p.Title, new DialogFormOptions
            {
                Caption = "Password",
                Masked = true,
                MaxLength = p.MaxLength,
                Checks = new[] { new DialogCheck { Label = "Remember password", Key = RememberKey } },
                Save = "OK",
                OnCancel = () => Send(new ContainerGatePacket { Kind = ContainerGateWire.KindCancel, Sequence = sequence }),
            }, r => Send(new ContainerGatePacket { Kind = ContainerGateWire.KindAnswer, Sequence = sequence, Text = r.Text ?? "", Remember = r.IsOn(RememberKey) }),
            p.Body);
            Plugin.Logger?.LogInfo("ContainerOpenGate: prompt #" + sequence + " for " + p.Container + (p.Mod.Length > 0 ? " from " + p.Mod : "") + " (with Remember).");
            return;
        }
        _dialog = UiDialogs.Prompt(
            Owner, "progenitor", footer, p.Title, p.Body, "",
            text => Send(new ContainerGatePacket { Kind = ContainerGateWire.KindAnswer, Sequence = sequence, Text = text ?? "" }),
            "", null, "OK", p.MaxLength, "Cancel",
            () => Send(new ContainerGatePacket { Kind = ContainerGateWire.KindCancel, Sequence = sequence }),
            masked: true);
        Plugin.Logger?.LogInfo("ContainerOpenGate: prompt #" + sequence + " for " + p.Container + (p.Mod.Length > 0 ? " from " + p.Mod : "") + ".");
    }

    private static void OnResult(ContainerGatePacket p)
    {
        if (p.Sequence != _sequence) return;
        // The prompt may still be up (the server timed it out, or the lock changed): it goes.
        CloseDialog();
        _sequence = 0;
        if (!p.Ok)
        {
            if (p.Message.Length > 0) Toast(p.Mod, p.Message, UiNoticeKind.Warning);
            return;
        }
        if (p.Retry) StartRetry(p.NetIndex, p.NetGeneration);
    }

    private static void Send(ContainerGatePacket packet)
    {
        try
        {
            if (!CustomEvents.SendToServer(ContainerGateWire.EventId, ContainerGateWire.Encode(packet)))
                Plugin.Logger?.LogWarning("ContainerOpenGate: the " + packet.Kind + " could not be sent.");
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("ContainerOpenGate: " + packet.Kind + " send threw: " + e.GetType().Name); }
        finally { packet.Text = ""; }
    }

    private static void CloseDialog()
    {
        var d = _dialog;
        _dialog = null;
        try { d?.Dispose(); } catch { /* */ }
    }

    private static void Toast(string mod, string message, UiNoticeKind kind)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        string text = string.IsNullOrWhiteSpace(mod) ? message : ModMessagePrefix.Apply(mod, message, true);
        _lastNote = text;
        try { UnityUiRuntime.HudNotify(Owner, text, "", kind, 0); }
        catch (Exception e) { Plugin.Logger?.LogWarning("ContainerOpenGate: toast threw: " + e.GetType().Name); }
    }

    private static void Tick()
    {
        // A preview's prompt is about that preview: when the preview ends first (Esc, its own time-out), the prompt goes.
        if (_dialog != null && _kind == ContainerOpenKind.Preview)
        {
            var current = ContainerPreviewClient.Current;
            if (current == null || current.State == ContainerPreviewState.Closed || current.Container != _container)
            {
                int sequence = _sequence;
                CloseDialog();
                _sequence = 0;
                if (sequence > 0) Send(new ContainerGatePacket { Kind = ContainerGateWire.KindCancel, Sequence = sequence });
            }
        }
        if (_retry != null)
        {
            try { _retry.Tick(); }
            catch (Exception e)
            {
                Plugin.Logger?.LogWarning("ContainerOpenGate: the repeat threw: " + e.GetType().Name);
                EndRetry();
                Toast("", ContainerGateText.Unlocked, UiNoticeKind.Info);
            }
        }
    }

    // ---- repeating the interaction ----------------------------------------------------------------

    private static void StartRetry(int netIndex, int netGeneration)
    {
        EndRetry();
        var world = Worlds.Client;
        Entity target = Entity.Null;
        if (world != null && world.IsCreated && (netIndex != 0 || netGeneration != 0))
        {
            try { target = ContainerPreviewClient.FindByNetworkId(world.EntityManager, netIndex, netGeneration); }
            catch { target = Entity.Null; }
        }
        if (world == null || target == Entity.Null)
        {
            Toast("", ContainerGateText.Unlocked, UiNoticeKind.Info);
            return;
        }
        InputLease? lease = null;
        InputAcquireResult acquired;
        try
        {
            // Yield (the default): no warning per unlock. A player who moves the mouse or presses a key in this second
            // takes the hover or the buttons back, and the toast below is not needed then (they are interacting already).
            acquired = InputLeases.TryAcquire(Holder,
                new InputLeaseOptions(InputChannels.Hover | InputChannels.Buttons, UserPolicy.Yield, preempt: false,
                    reason: "repeat the interaction the container open gate closed"), out lease);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("ContainerOpenGate: the input lease threw: " + e.GetType().Name);
            acquired = InputAcquireResult.Faulted;
        }
        if (acquired != InputAcquireResult.Acquired || lease == null)
        {
            Plugin.Logger?.LogInfo("ContainerOpenGate: the interaction is not repeated (input lease " + acquired + "); asking the player.");
            Toast("", ContainerGateText.Unlocked, UiNoticeKind.Info);
            return;
        }
        try { lease.Hover(target); }
        catch (Exception e)
        {
            Plugin.Logger?.LogInfo("ContainerOpenGate: the container cannot be hovered (" + e.Message + "); asking the player.");
            try { lease.Dispose(); } catch { /* */ }
            Toast("", ContainerGateText.Unlocked, UiNoticeKind.Info);
            return;
        }
        _retry = new Retry(world, target, lease, new ContainerGateRetryPlan(Now));
    }

    private static void EndRetry()
    {
        var r = _retry;
        _retry = null;
        r?.End();
    }

    /// <summary>Dev bridge status (no typed text).</summary>
    internal static Dictionary<string, object?> Describe() => new()
    {
        ["installed"] = _installed,
        ["prompt"] = _dialog != null,
        ["sequence"] = _sequence,
        ["container"] = _sequence > 0 ? _container.ToString() : null,
        ["kind"] = _sequence > 0 ? _kind.ToString() : null,
        ["repeating"] = _retry != null,
        ["lastNote"] = _lastNote,
    };

    private sealed class Retry
    {
        private readonly World _world;
        private readonly Entity _target;
        private readonly InputLease _lease;
        private readonly ContainerGateRetryPlan _plan;

        internal Retry(World world, Entity target, InputLease lease, ContainerGateRetryPlan plan)
        {
            _world = world;
            _target = target;
            _lease = lease;
            _plan = plan;
        }

        internal void Tick()
        {
            if (!_world.IsCreated || !_lease.Active || !_world.EntityManager.Exists(_target))
            {
                bool pressed = _plan.Pressed;
                EndRetry();
                if (!pressed) Toast("", ContainerGateText.Unlocked, UiNoticeKind.Info);
                return;
            }
            var window = ContainerPreviewClient.ReadWindow(_world);
            bool open = window.Mapper != null && window.Target == _target;
            switch (_plan.Step(Now, open))
            {
                case ContainerGateRetryStep.Press:
                    _lease.Press(SyncedButtons.Interact, ContainerGateRetryPlan.PressSeconds);
                    Plugin.Logger?.LogInfo("ContainerOpenGate: repeating the interaction with the unlocked container.");
                    break;
                case ContainerGateRetryStep.End:
                    EndRetry();
                    break;
            }
        }

        internal void End()
        {
            try { _lease.Dispose(); } catch { /* */ }
        }
    }
}
#endif
