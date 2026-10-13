#if !PROGENITOR_TESTS
using System;
using System.Runtime.InteropServices;
using Progenitor.Ecs;
using Progenitor.Hooks;
using Progenitor.Ui;
using Progenitor.Units;
using Progenitor.WorldState;
using ProjectM;
using ProjectM.Network;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine.InputSystem;

namespace Progenitor.Inputs;

/// <summary>
/// Live <see cref="IInputRuntime"/> and the raw reads and writes behind <c>ctx.Input</c>.
///
/// <para><b>Entities.</b> The input controller is the one client entity with
/// <c>InputCommandBufferElement</c> and <c>Controller</c>: <c>CreateInputCommandsSystem</c>
/// iterates exactly that pair with its <c>EntityInput</c> and copies the
/// <c>EntityInput</c> into each new command's <c>RawInput</c>. The character is the one entity
/// with <c>LocalCharacter</c>; its <c>EntityInput</c> drives client-side facing and prediction.
/// Both are written (live 2026-09-25: writing only the character's turned her on the client
/// while the server cast at the mouse).</para>
///
/// <para><b>Access.</b> Fields are poked through <c>GetComponentDataRawRW</c> /
/// <c>GetBufferRawRW</c> at the <see cref="Layout"/> offsets, after <see cref="CheckLayout"/>
/// compared them with the interop's <c>Marshal.OffsetOf</c> once. No generic interop
/// instantiation over a game value type is added.</para>
///
/// <para><b>Devices.</b> User activity reads <c>Keyboard.current</c> and <c>Mouse.current</c>
/// controls, as the keybind source does; nothing is consumed and the cursor is never touched.
/// Movement keys are the default bindings (W, A, S, D, arrows); ability keys are Q, E, R, C, T,
/// F and Space, ignored while the player is typing in chat or a text field. A player who rebound
/// them is detected by the mouse only.</para>
/// </summary>
internal sealed unsafe class UnityInputRuntime : IInputRuntime
{
    internal static readonly UnityInputRuntime Instance = new();

    private static readonly Key[] MoveKeys = { Key.W, Key.A, Key.S, Key.D, Key.UpArrow, Key.DownArrow, Key.LeftArrow, Key.RightArrow };
    private static readonly Key[] ButtonKeys = { Key.Q, Key.E, Key.R, Key.C, Key.T, Key.F, Key.Space };

    /// <summary>Pointer travel (pixels) between two samples that counts as the user moving the mouse.</summary>
    private const float PointerSlackPixels = 2f;

    private int _mainThread;
    private int _playerFrame = int.MinValue;
    private long _playerToken;
    private bool _havePlayer;
    private InputEntityRef _controller, _character;
    private bool _havePointer;
    private float _pointerX, _pointerY;

    private UnityInputRuntime() { }

    internal void CaptureMainThread() => _mainThread = Environment.CurrentManagedThreadId;

    public long WorldToken
    {
        get
        {
            var world = Worlds.Client;
            if (world == null || !world.IsCreated) return 0;
            return ((long)WorldEpoch.Current << 1) | 1L;
        }
    }

    public double Now => UnityEngine.Time.realtimeSinceStartupAsDouble;

    public int Frame => UnityEngine.Time.frameCount;

    public bool IsMainThread => _mainThread == 0 || Environment.CurrentManagedThreadId == _mainThread;

    public bool Focused
    {
        get
        {
            try { return UnityEngine.Application.isFocused; }
            catch { return false; }
        }
    }

    public bool TryGetLocalPlayer(out InputEntityRef controller, out InputEntityRef character)
    {
        int frame = Frame;
        long token = WorldToken;
        if (frame != _playerFrame || token != _playerToken)
        {
            _playerFrame = frame;
            _playerToken = token;
            _havePlayer = token != 0 && FindLocalPlayer(out _controller, out _character);
        }

        controller = _controller;
        character = _character;
        return _havePlayer;
    }

    private static bool FindLocalPlayer(out InputEntityRef controller, out InputEntityRef character)
    {
        controller = default;
        character = default;
        var world = Worlds.Client;
        if (world == null || !world.IsCreated) return false;
        var em = world.EntityManager;
        var characters = Query.Entities(em, includeDisabled: false, includeSystems: false,
            ComponentIndex.Of<LocalCharacter>(ComponentType.AccessMode.ReadOnly));
        if (characters.Count != 1) return false;
        var controllers = Query.Entities(em, includeDisabled: false, includeSystems: false,
            ComponentIndex.Of<InputCommandBufferElement>(ComponentType.AccessMode.ReadOnly),
            ComponentIndex.Of<Controller>(ComponentType.AccessMode.ReadOnly));
        if (controllers.Count != 1) return false;
        controller = Ref(controllers[0]);
        character = Ref(characters[0]);
        return true;
    }

    public bool TryGetPosition(InputEntityRef entity, out UnitVec3 position)
    {
        position = default;
        var world = Worlds.Client;
        if (world == null || !world.IsCreated || entity.IsNull) return false;
        var em = world.EntityManager;
        var e = ToEntity(entity);
        if (!em.Exists(e) || !e.TryRead<Translation>(em, out var tr)) return false;
        position = tr.Value;
        return true;
    }

    public bool TryGetNetworkId(InputEntityRef entity, out InputNetworkId id)
    {
        id = default;
        var world = Worlds.Client;
        if (world == null || !world.IsCreated || entity.IsNull) return false;
        var em = world.EntityManager;
        var e = ToEntity(entity);
        var type = ComponentIndex.Of<NetworkId>(ComponentType.AccessMode.ReadOnly);
        if (!em.Exists(e) || !em.HasComponent(e, type)) return false;
        int* net = (int*)em.GetComponentDataRawRO(e, type.TypeIndex);
        if (net == null) return false;
        id = new InputNetworkId(net[0], net[1], net[2]);
        return true;
    }

    /// <summary>
    /// Photo mode's pointer sample (the framework input layer reads devices; features do not): the
    /// right button's held state, this frame's mouse movement in pixels and the wheel's vertical
    /// scroll. False when there is no mouse or a device read threw. Binds no action to a key; photo
    /// mode's keys are Photo-scope keybinds.
    /// </summary>
    /// <summary>
    /// True while any keyboard key or mouse button is down (photo mode keeps the exclusive input
    /// hold after it exits until everything held during it is released, so no release reaches the
    /// game as a stray action). False when no device can be read.
    /// </summary>
    internal static bool AnyKeyOrButtonHeld()
    {
        try
        {
            var keyboard = Keyboard.current;
            if (keyboard != null && keyboard.anyKey != null && keyboard.anyKey.isPressed) return true;
            var mouse = Mouse.current;
            if (mouse == null) return false;
            return Pressed(mouse.leftButton) || Pressed(mouse.rightButton) || Pressed(mouse.middleButton);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// CHAT1: true while any of the five mouse buttons is down (the chat click guard's latch). False when no
    /// mouse can be read.
    /// </summary>
    internal static bool AnyMouseButtonHeld()
    {
        try
        {
            var mouse = Mouse.current;
            if (mouse == null) return false;
            return Pressed(mouse.leftButton) || Pressed(mouse.rightButton) || Pressed(mouse.middleButton)
                || Pressed(mouse.backButton) || Pressed(mouse.forwardButton);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// CHAT5: whether a mouse button went down this frame (0 left, 1 right, 2 middle): the chat view's clicks on links
    /// and lines. False when no mouse can be read.
    /// </summary>
    internal static bool MouseButtonPressedThisFrame(int button)
    {
        try
        {
            var mouse = Mouse.current;
            if (mouse == null) return false;
            var control = button switch { 0 => mouse.leftButton, 1 => mouse.rightButton, 2 => mouse.middleButton, _ => null };
            return control != null && control.wasPressedThisFrame;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>CHAT5: whether a mouse button is down now (0 left, 1 right): the chat view's drags.</summary>
    internal static bool MouseButtonHeld(int button)
    {
        try
        {
            var mouse = Mouse.current;
            if (mouse == null) return false;
            var control = button switch { 0 => mouse.leftButton, 1 => mouse.rightButton, 2 => mouse.middleButton, _ => null };
            return Pressed(control);
        }
        catch
        {
            return false;
        }
    }

    internal static bool TrySamplePointer(out bool rightHeld, out float deltaX, out float deltaY, out float scrollY)
    {
        rightHeld = false;
        deltaX = deltaY = scrollY = 0f;
        try
        {
            var mouse = Mouse.current;
            if (mouse == null) return false;
            rightHeld = mouse.rightButton != null && mouse.rightButton.isPressed;
            var d = mouse.delta.ReadValue();
            deltaX = d.x;
            deltaY = d.y;
            scrollY = mouse.scroll.ReadValue().y;
            return true;
        }
        catch
        {
            // A device replaced mid-read is no pointer input this frame.
            return false;
        }
    }

    public UserActivity SampleActivity()
    {
        var activity = UserActivity.None;
        try
        {
            // Keystrokes typed into chat or a text field are text, not play (typing ".input walk"
            // must not pause the walk it asks for).
            var keyboard = Keybinds.PlayerIsTyping ? null : Keyboard.current;
            if (keyboard != null)
            {
                if (AnyPressed(keyboard, MoveKeys)) activity |= UserActivity.MoveKeys;
                if (AnyPressed(keyboard, ButtonKeys)) activity |= UserActivity.ButtonKeys;
            }

            var mouse = Mouse.current;
            if (mouse != null)
            {
                if (Pressed(mouse.leftButton) || Pressed(mouse.rightButton) || Pressed(mouse.middleButton))
                    activity |= UserActivity.Pointer | UserActivity.ButtonKeys;
                var p = mouse.position.ReadValue();
                if (_havePointer && (MathF.Abs(p.x - _pointerX) > PointerSlackPixels || MathF.Abs(p.y - _pointerY) > PointerSlackPixels))
                    activity |= UserActivity.Pointer;
                _pointerX = p.x;
                _pointerY = p.y;
                _havePointer = true;
            }
        }
        catch
        {
            // A device replaced mid-read (unplugged, overlay re-init) is no activity this frame.
        }

        return activity;
    }

    private static bool AnyPressed(Keyboard keyboard, Key[] keys)
    {
        for (int i = 0; i < keys.Length; i++)
        {
            var control = keyboard[keys[i]];
            if (control != null && control.isPressed) return true;
        }

        return false;
    }

    private static bool Pressed(UnityEngine.InputSystem.Controls.ButtonControl? button) => button != null && button.isPressed;

    public void Log(string message) => Plugin.Logger?.LogInfo(message);

    public void Warn(string message) => Plugin.Logger?.LogWarning(message);

    // ---- writes ----------------------------------------------------------------------------------

    /// <summary>Write <paramref name="w"/> into one entity's <c>EntityInput</c>.</summary>
    internal static void Apply(EntityManager em, InputEntityRef target, in InputWrite w)
    {
        if (!w.Any || target.IsNull) return;
        var e = ToEntity(target);
        var type = ComponentIndex.Of<EntityInput>(ComponentType.AccessMode.ReadWrite);
        if (!em.Exists(e) || !em.HasComponent(e, type)) return;
        byte* raw = (byte*)em.GetComponentDataRawRW(e, type.TypeIndex);
        if (raw == null) return;
        WriteInput(raw, w);
    }

    private static void WriteInput(byte* raw, in InputWrite w)
    {
        if (w.WriteMove) *(float2*)(raw + Layout.EntityInputMovementOffset) = new float2(w.MoveX, w.MoveZ);
        if (w.WriteAim)
        {
            *(float3*)(raw + Layout.EntityInputAimPositionOffset) = w.AimPosition;
            *(float3*)(raw + Layout.EntityInputAimPositionPlaneOffset) = w.AimPlane;
            *(float3*)(raw + Layout.EntityInputAimDirectionOffset) = w.AimDirection;
            *(float*)(raw + Layout.EntityInputAimLengthOffset) = w.AimLength;
        }

        if (w.WriteHover) WriteHover(raw, w.HoverEntity, w.HoverNetworkId);
        if (w.Pressed != 0) *(ushort*)(raw + Layout.EntityInputStateOffset + Layout.SyncedInputStateInputsPressedOffset) |= w.Pressed;
    }

    private static void WriteHover(byte* input, InputEntityRef entity, InputNetworkId net)
    {
        *(Entity*)(input + Layout.EntityInputHoveredEntityOffset) = ToEntity(entity);
        int* id = (int*)(input + Layout.EntityInputHoveredNetworkIdOffset);
        id[0] = net.A;
        id[1] = net.B;
        id[2] = net.C;
    }

    // ---- commands --------------------------------------------------------------------------------

    /// <summary>
    /// Command stride: the end of <c>DeltaTime</c> rounded up to <c>EndTime</c>'s 8-byte
    /// alignment (0x98 on this build). The dump cannot size the element directly.
    /// </summary>
    /// <summary>Bytes of <c>HoveredEntityNetworkId</c>: the gap up to <c>ProjectileAimType</c>.</summary>
    internal const int NetworkIdBytes = Layout.EntityInputProjectileAimTypeOffset - Layout.EntityInputHoveredNetworkIdOffset;

    internal static readonly int CommandStride = (Layout.InputCommandDeltaTimeOffset + sizeof(float) + 7) & ~7;

    /// <summary>Read back and patch the controller's commands newer than the last pass.</summary>
    internal static void PatchCommands(InputDirector director, EntityManager em, InputEntityRef controller)
    {
        if (controller.IsNull) return;
        var owner = ToEntity(controller);
        var bufferType = ComponentIndex.Of<InputCommandBufferElement>(ComponentType.AccessMode.ReadWrite);
        if (!em.Exists(owner) || !em.HasComponent(owner, bufferType)) return;
        int length = em.GetBufferLength(owner, bufferType.TypeIndex);
        if (length <= 0) return;
        byte* raw = (byte*)em.GetBufferRawRW(owner, bufferType.TypeIndex);
        if (raw == null) return;
        int stride = CommandStride;

        int newest = int.MinValue;
        for (int i = 0; i < length; i++)
            newest = Math.Max(newest, *(int*)(raw + (i * stride) + Layout.InputCommandClientFrameOffset));
        int threshold = director.CommandThreshold(controller, newest);

        // Commands tick faster than the (capped) frame rate, so one frame can append several; they
        // are in ClientFrame order, and only the oldest new one carries edges.
        int best = threshold, count = 0;
        for (int i = 0; i < length; i++)
        {
            byte* cmd = raw + (i * stride);
            int frame = *(int*)(cmd + Layout.InputCommandClientFrameOffset);
            if (frame <= threshold) continue;
            var view = ReadCommand(cmd, frame);
            director.PatchCommand(ref view, first: count == 0);
            if (view.Patched) WriteCommand(cmd, view);
            if (frame > best) best = frame;
            count++;
        }

        director.CommandsDone(best, count);
    }

    private static InputCommandView ReadCommand(byte* cmd, int frame)
    {
        byte* input = cmd + Layout.InputCommandRawInputOffset;
        byte* state = input + Layout.EntityInputStateOffset;
        var move = *(float2*)(cmd + Layout.InputCommandMovementInputOffset);
        var rawMove = *(float2*)(input + Layout.EntityInputMovementOffset);
        int* net = (int*)(input + Layout.EntityInputHoveredNetworkIdOffset);
        var hover = *(Entity*)(input + Layout.EntityInputHoveredEntityOffset);
        return new InputCommandView
        {
            ClientFrame = frame,
            MoveX = move.x,
            MoveZ = move.y,
            Rotation = *(float3*)(cmd + Layout.InputCommandRotationInputOffset),
            RawMoveX = rawMove.x,
            RawMoveZ = rawMove.y,
            AimPosition = *(float3*)(input + Layout.EntityInputAimPositionOffset),
            AimPlane = *(float3*)(input + Layout.EntityInputAimPositionPlaneOffset),
            AimDirection = *(float3*)(input + Layout.EntityInputAimDirectionOffset),
            AimLength = *(float*)(input + Layout.EntityInputAimLengthOffset),
            HoverEntity = Ref(hover),
            HoverNetworkId = new InputNetworkId(net[0], net[1], net[2]),
            Up = *(ushort*)(state + Layout.SyncedInputStateInputsUpOffset),
            Down = *(ushort*)(state + Layout.SyncedInputStateInputsDownOffset),
            Pressed = *(ushort*)(state + Layout.SyncedInputStateInputsPressedOffset),
        };
    }

    private static void WriteCommand(byte* cmd, in InputCommandView v)
    {
        byte* input = cmd + Layout.InputCommandRawInputOffset;
        byte* state = input + Layout.EntityInputStateOffset;
        *(float2*)(cmd + Layout.InputCommandMovementInputOffset) = new float2(v.MoveX, v.MoveZ);
        *(float3*)(cmd + Layout.InputCommandRotationInputOffset) = v.Rotation;
        *(float2*)(input + Layout.EntityInputMovementOffset) = new float2(v.RawMoveX, v.RawMoveZ);
        *(float3*)(input + Layout.EntityInputAimPositionOffset) = v.AimPosition;
        *(float3*)(input + Layout.EntityInputAimPositionPlaneOffset) = v.AimPlane;
        *(float3*)(input + Layout.EntityInputAimDirectionOffset) = v.AimDirection;
        *(float*)(input + Layout.EntityInputAimLengthOffset) = v.AimLength;
        WriteHover(input, v.HoverEntity, v.HoverNetworkId);
        *(ushort*)(state + Layout.SyncedInputStateInputsUpOffset) = v.Up;
        *(ushort*)(state + Layout.SyncedInputStateInputsDownOffset) = v.Down;
        *(ushort*)(state + Layout.SyncedInputStateInputsPressedOffset) = v.Pressed;
    }

    // ---- layout ----------------------------------------------------------------------------------

    /// <summary>Compare the declared offsets with the interop's once; null when they agree.</summary>
    internal static string? CheckLayout()
    {
        try
        {
            int O<T>(string field) => (int)Marshal.OffsetOf<T>(field);
            var mismatches = new System.Collections.Generic.List<string>();
            void Expect(string name, int actual, int declared)
            {
                if (actual != declared) mismatches.Add(name + " interop 0x" + actual.ToString("X") + " declared 0x" + declared.ToString("X"));
            }

            Expect("EntityInput.Movement", O<EntityInput>(nameof(EntityInput.Movement)), Layout.EntityInputMovementOffset);
            Expect("EntityInput.AimPosition", O<EntityInput>(nameof(EntityInput.AimPosition)), Layout.EntityInputAimPositionOffset);
            Expect("EntityInput.AimPositionPlane", O<EntityInput>(nameof(EntityInput.AimPositionPlane)), Layout.EntityInputAimPositionPlaneOffset);
            Expect("EntityInput.AimDirection", O<EntityInput>(nameof(EntityInput.AimDirection)), Layout.EntityInputAimDirectionOffset);
            Expect("EntityInput.AimLength", O<EntityInput>(nameof(EntityInput.AimLength)), Layout.EntityInputAimLengthOffset);
            Expect("EntityInput.HoveredEntity", O<EntityInput>(nameof(EntityInput.HoveredEntity)), Layout.EntityInputHoveredEntityOffset);
            Expect("EntityInput.HoveredEntityNetworkId", O<EntityInput>(nameof(EntityInput.HoveredEntityNetworkId)), Layout.EntityInputHoveredNetworkIdOffset);
            Expect("EntityInput.ProjectileAimType", O<EntityInput>(nameof(EntityInput.ProjectileAimType)), Layout.EntityInputProjectileAimTypeOffset);
            Expect("EntityInput.State", O<EntityInput>(nameof(EntityInput.State)), Layout.EntityInputStateOffset);
            Expect("SyncedInputState.InputsUp", O<SyncedInputState>(nameof(SyncedInputState.InputsUp)), Layout.SyncedInputStateInputsUpOffset);
            Expect("SyncedInputState.InputsDown", O<SyncedInputState>(nameof(SyncedInputState.InputsDown)), Layout.SyncedInputStateInputsDownOffset);
            Expect("SyncedInputState.InputsPressed", O<SyncedInputState>(nameof(SyncedInputState.InputsPressed)), Layout.SyncedInputStateInputsPressedOffset);
            Expect("InputCommandBufferElement.MovementInput", O<InputCommandBufferElement>(nameof(InputCommandBufferElement.MovementInput)), Layout.InputCommandMovementInputOffset);
            Expect("InputCommandBufferElement.RotationInput", O<InputCommandBufferElement>(nameof(InputCommandBufferElement.RotationInput)), Layout.InputCommandRotationInputOffset);
            Expect("InputCommandBufferElement.RawInput", O<InputCommandBufferElement>(nameof(InputCommandBufferElement.RawInput)), Layout.InputCommandRawInputOffset);
            Expect("InputCommandBufferElement.ClientFrame", O<InputCommandBufferElement>(nameof(InputCommandBufferElement.ClientFrame)), Layout.InputCommandClientFrameOffset);
            Expect("InputCommandBufferElement.EndTime", O<InputCommandBufferElement>(nameof(InputCommandBufferElement.EndTime)), Layout.InputCommandEndTimeOffset);
            Expect("InputCommandBufferElement.DeltaTime", O<InputCommandBufferElement>(nameof(InputCommandBufferElement.DeltaTime)), Layout.InputCommandDeltaTimeOffset);

            // The hovered NetworkId is copied as three ints: the gap up to ProjectileAimType, and
            // the interop's own size for NetworkId (12 on this build, read by the bot driver too).
            Expect("NetworkId size", Marshal.SizeOf<NetworkId>(), NetworkIdBytes);

            int elementSize = 0;
            try
            {
                var bufferType = ComponentIndex.Of<InputCommandBufferElement>(ComponentType.AccessMode.ReadOnly);
                elementSize = TypeManager.GetTypeInfo(bufferType.TypeIndex).ElementSize;
            }
            catch
            {
                // Reads 0 through this interop on some types; the dump offsets then stand alone.
            }

            if (elementSize != 0 && elementSize != CommandStride)
                mismatches.Add("InputCommandBufferElement ECS ElementSize " + elementSize + " stride " + CommandStride);

            return mismatches.Count == 0 ? null : "input layout differs from the dump (" + string.Join("; ", mismatches) + ")";
        }
        catch (Exception e)
        {
            return "input layout check threw: " + e.Message;
        }
    }

    // ---- helpers ---------------------------------------------------------------------------------

    internal static Entity ToEntity(InputEntityRef r) => new() { Index = r.Index, Version = r.Version };

    internal static InputEntityRef Ref(Entity e) => new(e.Index, e.Version);
}

/// <summary>
/// The hooks and the frame subscription behind every lease, armed while the director
/// <see cref="InputDirector.HasWork"/> and removed when it has none and on Core uninstall.
///
/// <list type="number">
/// <item>After <c>GameplayInputSystem</c> (which fills <c>EntityInput</c> from the devices):
/// prepare the frame (validate, sample the user, run lease callbacks) and write.</item>
/// <item>Before <c>CreateInputCommandsSystem</c>: write again, since systems in between may
/// rewrite <c>EntityInput</c>.</item>
/// <item>After <c>CreateInputCommandsSystem</c>: read back and patch the new commands.</item>
/// </list>
/// Plus a <see cref="Plugin.Frame"/> tick that validates leases while no input system runs (a
/// disconnect tears the world down, so the hooks stop firing) and disarms when idle.
/// </summary>
internal static class InputFrame
{
    /// <summary>cfg key for the release-all keybind in <c>progenitor.ui.cfg</c>.</summary>
    internal const string ReleaseKeybindId = "ReleaseInput";

    internal static readonly KeyChord DefaultReleaseKey = new(UiKey.F12, KeyModifiers.Ctrl | KeyModifiers.Shift);

    private static bool _armed;
    private static bool _layoutChecked;
    private static IDisposable? _afterGameplay, _beforeCommands, _afterCommands;
    private static IKeybindHandle? _releaseBind;

    /// <summary>Client Core install: the director and the release key. Hooks wait for the first lease.</summary>
    internal static void Install()
    {
        if (Plugin.IsServerProcess) return;
        UnityInputRuntime.Instance.CaptureMainThread();
        InputDirector.Install(UnityInputRuntime.Instance);
        var file = UiKeybindHost.CoreConfig;
        if (file == null) return;
        try
        {
            _releaseBind = Keybinds.Register(
                owner: typeof(InputFrame),
                pluginId: SettingsPanel.PluginId,
                id: ReleaseKeybindId,
                file: file,
                defaultKey: DefaultReleaseKey,
                description: "Release every mod's control of your character's input (ctx.Input leases).",
                onPressed: PressedRelease,
                allowWhileCaptured: true);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Input: release keybind registration threw: " + e.Message);
        }
    }

    private static void PressedRelease()
    {
        int ended = InputDirector.Current?.ReleaseAll(InputLeaseEnd.UserPanic) ?? 0;
        Plugin.Logger?.LogInfo("Input: release key pressed; " + ended + " lease(s) ended.");
    }

    /// <summary>Core uninstall: end every lease, then remove the hooks and the key.</summary>
    internal static void Uninstall()
    {
        try { InputDirector.Uninstall(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Input: release on uninstall failed: " + e.Message); }
        Disarm();
        try { _releaseBind?.Dispose(); } catch { /* keybinds may already be torn down */ }
        _releaseBind = null;
        _layoutChecked = false;
    }

    /// <summary>
    /// Check the layout once per generation; false (and every lease ended) when it disagrees.
    /// Called before the first acquire so a refused build never writes.
    /// </summary>
    internal static bool EnsureLayout(InputDirector director)
    {
        if (!_layoutChecked)
        {
            _layoutChecked = true;
            var issue = UnityInputRuntime.CheckLayout();
            if (issue != null) director.RefuseLayout(issue);
        }

        return !director.LayoutRefused;
    }

    /// <summary>Arm the hooks (after an acquire).</summary>
    internal static void Arm()
    {
        if (_armed) return;
        _armed = true;
        Plugin.Frame += OnFrame;
        try
        {
            _afterGameplay = SystemHooks.After<GameplayInputSystem>(OnAfterGameplay);
            _beforeCommands = SystemHooks.Before<CreateInputCommandsSystem>(OnBeforeCommands);
            _afterCommands = SystemHooks.After<CreateInputCommandsSystem>(OnAfterCommands);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Input: hooks could not be registered: " + e.Message);
            InputDirector.Current?.Fault("input hooks could not be registered: " + e.Message);
            Disarm();
        }
    }

    private static void Disarm()
    {
        if (!_armed) return;
        _armed = false;
        try { Plugin.Frame -= OnFrame; } catch { /* Frame may already be null on uninstall */ }
        foreach (var hook in new[] { _afterGameplay, _beforeCommands, _afterCommands })
        {
            try { hook?.Dispose(); } catch { /* hooks may already be torn down on uninstall */ }
        }

        _afterGameplay = _beforeCommands = _afterCommands = null;
    }

    private static void OnFrame()
    {
        var director = InputDirector.Current;
        try
        {
            director?.Validate();
        }
        catch (Exception e)
        {
            try { director?.Fault("frame validation threw: " + e); } catch { /* nothing left to try */ }
        }

        if (director == null || !director.HasWork) Disarm();
    }

    private static void OnAfterGameplay(SystemHookContext hook) => Write(hook, "after GameplayInputSystem");

    private static void OnBeforeCommands(SystemHookContext hook) => Write(hook, "before CreateInputCommandsSystem");

    private static void Write(SystemHookContext hook, string where)
    {
        var director = InputDirector.Current;
        if (director == null || !director.HasWork) return;
        try
        {
            var w = director.Prepare();
            if (!w.Any) return;
            var em = hook.World.EntityManager;
            if (!UnityInputRuntime.Instance.TryGetLocalPlayer(out var controller, out var character)) return;
            UnityInputRuntime.Apply(em, controller, w);
            if (!character.Equals(controller)) UnityInputRuntime.Apply(em, character, w);
        }
        catch (Exception e)
        {
            Fail(director, where, e);
        }
    }

    private static void OnAfterCommands(SystemHookContext hook)
    {
        var director = InputDirector.Current;
        if (director == null || !director.HasWork) return;
        try
        {
            if (!UnityInputRuntime.Instance.TryGetLocalPlayer(out var controller, out _)) return;
            UnityInputRuntime.PatchCommands(director, hook.World.EntityManager, controller);
        }
        catch (Exception e)
        {
            Fail(director, "after CreateInputCommandsSystem", e);
        }
    }

    private static void Fail(InputDirector director, string where, Exception e)
    {
        // Disabled rather than retried: a write that threw once would throw every frame.
        try { director.Fault(where + " threw: " + e); } catch { /* nothing left to try */ }
    }
}
#endif
