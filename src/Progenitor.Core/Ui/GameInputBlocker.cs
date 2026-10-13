#if !PROGENITOR_TESTS
using System;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using BepInEx.Unity.IL2CPP.Hook;
using Progenitor.Native;
using ProjectM;
using Unity.Entities;

namespace Progenitor.Ui;

/// <summary>
/// Client implementation of <see cref="IUiInputBlocker"/>: one game-owned
/// <c>ProjectM.RotateCameraBlocker</c> pushed into <c>InputActionSystem._InputContexts</c> at
/// <see cref="UiInputCapture.Order"/>, plus a native postfix on that type's
/// <c>GetConsumedInputs</c> which applies chat's mask — but only for our own instance.
///
/// <para>
/// Why this shape: <c>IInputContext</c> is emitted as a
/// class, so <c>ClassInjector</c> cannot implement it and a mod cannot author a context type.
/// <c>RotateCameraBlocker</c> is interop-public in <c>ProjectM.Camera.dll</c>, has a
/// parameterless ctor, and its <c>Register</c> is literally
/// <c>AddInputContext(this, world, 600)</c> — the game's own use of the push API. Its
/// <c>GetConsumedInputs</c> has its own native body, so detouring it does not hit the
/// shared empty stub that <c>MenuInputSystem</c> / <c>GameplayInputSystem</c> /
/// dozens of unrelated methods all share.
/// </para>
///
/// <para>
/// The mask is chat's, from <c>ChatFocusedInputContext.GetConsumedInputs</c>:
/// <c>BlockAllKeys(false)</c> (UI navigation excluded, so uGUI and the panel keep working),
/// analog <c>MoveX|MoveY|AimX|AimY</c>, and on keyboard-and-mouse <c>RotateCamera</c> is allowed
/// back and <c>RotateCameraX|RotateCameraY</c> un-blocked. It is applied per frame into a
/// caller-owned <c>BlockInputState</c> and nothing is written to the world, which is why a
/// missed teardown costs one frame rather than a stuck character.
/// </para>
///
/// <para>
/// Job FUI20 made two changes here. The context is pushed at <see cref="UiInputCapture.Order"/>
/// (599) through <c>InputActionSystem.AddInputContext</c> directly rather than through
/// <c>RotateCameraBlocker.Register</c>'s hard-coded 600, so it is strictly ahead of
/// <c>MenuInputSystem</c> instead of tied with it; and how much of the recipe is applied is now a
/// <see cref="UiMaskScope"/> that follows the pointer, with <c>Back</c> and <c>Menu_Close</c>
/// taken in every scope. Both exist for the same defect: one Escape press closed the mod panel
/// and opened the vanilla system menu. See <see cref="InputMaskPlan"/>.
/// </para>
///
/// <para>
/// If the detour cannot be resolved (the address table for this build has no
/// <c>ProjectM.RotateCameraBlocker$$GetConsumedInputs</c> row yet) the context is still
/// registered with the vanilla <c>_BlockRotateCamera</c> behavior, so the camera stops turning
/// while a panel is open even without the mask. One log line says which of the two happened.
/// </para>
/// </summary>
internal sealed unsafe class GameInputBlocker : IUiInputBlocker
{
    /// <summary>Address-library id. Add this line to <c>tools/addresslib/wanted-client.txt</c>.</summary>
    public const string AddressIdGetConsumedInputs = "ProjectM.RotateCameraBlocker$$GetConsumedInputs";

    /// <summary>Detour site name in the <c>native</c> ping line.</summary>
    public const string Site = "ui.inputmask";

    /// <summary>NativePinRegistry name for the mask hook; released with the Core generation.</summary>
    public const string PinName = "ui.inputmask";

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void GetConsumedInputsFn(IntPtr instance, IntPtr blockInputState, IntPtr methodInfo);

    private static readonly object MaskGate = new();

    /// <summary>
    /// The detour handle and the managed hook delegate. Plain statics on purpose, and job FLATCH2
    /// (R45 finding 20) writes the reason down so the next reader does not "fix" the asymmetry
    /// with the two fields below: neither of these is read by <see cref="Mask"/>. Every access is
    /// under <see cref="MaskGate"/>, on the main thread, from install or teardown.
    /// </summary>
    private static INativeDetour? _detour;
    private static GetConsumedInputsFn? _hook;

    /// <summary>
    /// The trampoline back to the game's <c>GetConsumedInputs</c>, and the instance pointer of the
    /// context that is ours. Job FLATCH2 (R45 finding 20): both are written under
    /// <see cref="MaskGate"/> by three writers (<see cref="EnsureMask"/>,
    /// <see cref="ForgetOurs"/>, <see cref="UninstallMask"/>) and read by <see cref="Mask"/>, the
    /// native callback, with no lock — the same situation <see cref="_maskLive"/> and
    /// <see cref="_scope"/> are in, and they got <c>volatile</c> plus a comment while these two,
    /// four lines away in the same block and read on the same two lines of <c>Mask</c>, did not.
    ///
    /// <para><c>_ours</c> is the <em>guard</em>: <c>Mask</c> runs for every
    /// <c>RotateCameraBlocker</c> in the process, ours and the game's, and
    /// <c>instance != _ours</c> is the only thing keeping our mask off a vanilla context. A stale
    /// non-zero read after <see cref="ForgetOurs"/> applies <c>BlockAllKeys</c>, Move/Aim, zoom
    /// and both close actions to a context we no longer own.</para>
    ///
    /// <para><c>volatile</c> is not legal on <c>IntPtr</c>, so the accesses are
    /// <c>Volatile.Read</c>/<c>Volatile.Write</c> instead; <c>_original</c> takes the same
    /// treatment rather than the keyword so the pair reads as one rule. This is a compiler and
    /// ordering guarantee, not a race fix — <c>Mask</c> is entered fresh per call and copies the
    /// field into a local, so nothing here was measured misbehaving on x64. It is written down
    /// because the fields it sits beside were reasoned about and these were not.</para>
    /// </summary>
    private static GetConsumedInputsFn? _original;
    private static IntPtr _ours;

    /// <summary>
    /// Job KEY3: the instance pointer of the <b>second</b> context, at
    /// <see cref="UiInputCapture.HotkeyGuardOrder"/> (498). Under <see cref="MaskGate"/> and read by
    /// <see cref="Mask"/> through <c>Volatile.Read</c>, for exactly the reasons <see cref="_ours"/>
    /// documents — one shared detour fires for every <c>RotateCameraBlocker</c> in the process, so
    /// the pointer is the whole of the authorization, and now it also selects which of the two
    /// recipes is applied (<see cref="MenuHotkeyGuard.TargetFor"/>).
    ///
    /// <para>
    /// Kept distinct from <see cref="_ours"/> rather than folded into a set: the two contexts apply
    /// <em>different</em> masks, and a design that only asked "is this one of ours" would put the
    /// full 599 recipe — <c>BlockAllKeys</c>, Move/Aim, zoom, the close actions — on a context that
    /// sits ahead of every HUD menu in the game.
    /// </para>
    /// </summary>
    private static IntPtr _guard;

    /// <summary>
    /// CHAT1: the instance pointer of the chat click guard at
    /// <see cref="UiInputCapture.ChatClickGuardOrder"/>. Same rules as <see cref="_guard"/>: written under
    /// <see cref="MaskGate"/>, read by <see cref="Mask"/> through <c>Volatile.Read</c>, zeroed on every drop
    /// and teardown path, and the pointer selects the chat recipe (<see cref="MaskChat"/>) and nothing else.
    /// </summary>
    private static IntPtr _chat;

    /// <summary>
    /// Whether the detour is installed. <c>volatile</c> because <see cref="MaskLive"/> reads it
    /// with no lock held: it is read on the register path to build a log line that must report
    /// what was installed rather than what was intended (job FUI23, R44 finding 3), and taking
    /// <see cref="MaskGate"/> there would put a lock on a path <see cref="EnsureMask"/> may already
    /// hold it from.
    /// </summary>
    private static volatile bool _maskLive;

    /// <summary>
    /// Whether the armed <c>GetConsumedInputs</c> JMP belongs to Bootstrap rather than to this
    /// Core generation (job FLATCH3, R45 finding 19). True is the good case and the normal one on
    /// a client with Bootstrap present: <see cref="UninstallMask"/> then hands the slot back with
    /// one volatile store instead of disposing an <c>INativeDetour</c>, so teardown frees no
    /// native memory and stops being a main-thread-only operation. False is the fallback — an
    /// absent or older Bootstrap, or one whose apply failed — where <see cref="_detour"/> is ours
    /// and the thread rule on <see cref="UninstallMask"/> is live.
    ///
    /// <para>Under <see cref="MaskGate"/> like the two fields above, and for the same reason:
    /// <see cref="Mask"/> does not read it. The two paths are mutually exclusive by construction —
    /// on the Bootstrap path <see cref="_detour"/>, <see cref="_hook"/> and <see cref="_original"/>
    /// all stay null, because Bootstrap owns all three and calls the game's body itself before it
    /// calls <see cref="Mask"/> — which is why <see cref="Mask"/> reads a null original there and
    /// skips its own invoke rather than double-calling the game.</para>
    /// </summary>
    private static bool _bootstrapOwnsMask;

    /// <summary>
    /// Whether <see cref="CheckCloseActionIdsLocked"/> has run in this process. Under
    /// <see cref="MaskGate"/> like everything else in the detour half; it is a once-per-process log
    /// line, not a gate on any behavior.
    /// </summary>
    private static bool _closeActionsChecked;

    /// <summary>
    /// The scope <see cref="Mask"/> applies, as a plain volatile int (job FUI20). Read from a
    /// native callback that runs once per frame per context: taking <see cref="MaskGate"/> there
    /// would put a managed lock on the game's input path, and there is nothing to serialize — one
    /// writer (the pointer gate, on the main thread) and one reader.
    /// </summary>
    private static volatile int _scope = (int)UiMaskScope.Full;

    private readonly object _gate = new();
    private RotateCameraBlocker? _context;
    private bool _registered;
    private bool _cameraBlocked;

    /// <summary>
    /// Job KEY3. The second context object, pushed at
    /// <see cref="UiInputCapture.HotkeyGuardOrder"/>. Its lifetime is the 599 context's: created on
    /// the same <see cref="Register"/>, taken out on the same <see cref="Remove"/>, dropped on the
    /// same <see cref="Dispose"/>. Only its <em>mask</em> is focus-scoped, and that is a flag
    /// (<see cref="MenuHotkeyGuard.HotkeysBlocked"/>) read per frame — no focus change ever touches
    /// the game's ordered list, which is the rule <see cref="MenuHotkeyGuard"/> was written under and
    /// KEY3 keeps.
    ///
    /// <para>
    /// Its <c>_BlockRotateCamera</c> is left <c>false</c> for the whole of its life. That field is
    /// the persistent one R31 finding 8 is about — vanilla <c>GetConsumedInputs</c> blocks
    /// <c>RotateCamera</c> unconditionally while it is set, and only a successful removal clears it —
    /// so a context whose only job is a letter block must never acquire it. It is also why a leaked
    /// 498 entry is inert by construction rather than by teardown: with the flag false and the mask
    /// pointer-gated, an entry we have forgotten consumes nothing at all.
    /// </para>
    /// </summary>
    private RotateCameraBlocker? _guardContext;

    private bool _guardRegistered;

    public bool IsRegistered
    {
        get { lock (_gate) return _registered; }
    }

    public bool BlocksCameraRotation
    {
        get { lock (_gate) return _cameraBlocked; }
    }

    public bool DenyModifications
    {
        get
        {
            try
            {
                var system = InputHelper.GetInputActionSystem();
                return system != null && system._DenyModificationsOfInputContexts;
            }
            catch (Exception e)
            {
                // Unreadable means "do not touch the list this frame".
                Plugin.Logger?.LogWarning("Ui: _DenyModificationsOfInputContexts read threw: " + e.Message);
                return true;
            }
        }
    }

    public bool Register()
    {
        lock (_gate)
        {
            if (_registered) return true;

            InputActionSystem? system;
            try { system = InputHelper.GetInputActionSystem(); }
            catch (Exception e)
            {
                Plugin.Logger?.LogWarning("Ui: InputHelper.GetInputActionSystem threw: " + e.Message);
                return false;
            }

            if (system == null)
            {
                Plugin.Logger?.LogInfo("Ui: no InputActionSystem (main menu or server); input capture stays off.");
                return false;
            }

            World? world = Worlds.Client;
            if (world == null || !world.IsCreated)
            {
                Plugin.Logger?.LogInfo("Ui: no Client world; input capture stays off.");
                return false;
            }

            var context = _context ??= TryCreateContext();
            if (context == null) return false;

            // Vanilla behavior is the fallback when the mask detour is not live: block the
            // camera-rotate button and nothing else. With the mask live our postfix re-allows
            // RotateCamera on keyboard+mouse exactly the way chat does. Job FUI20: it follows the
            // scope rather than the registration, because the context now stays registered while
            // a panel is shown and a panel the pointer is not over must not stop the camera
            // turning on a build where the detour did not resolve.
            SetCameraBlock(context, InputMaskPlan.For(CurrentScope).BlockCameraRotationFallback);

            EnsureMask(context);

            // Job FUI20: pushed with our own order rather than through
            // RotateCameraBlocker.Register, which hard-codes 600 — the order MenuInputSystem
            // itself sits at, making the two peers and leaving which one subtracts Back first to
            // an unspecified tie-break. See UiInputCapture.Order. Because Register is not called,
            // the instance's own _Registered guard is never set and Deregister would be a silent
            // no-op, so Remove() must use RemoveInputContext directly and does.
            try { system.AddInputContext(new IInputContext(context.Pointer), world, UiInputCapture.Order); }
            catch (Exception e)
            {
                Plugin.Logger?.LogError("Ui: AddInputContext(order=" + UiInputCapture.Order + ") threw: " + e);
                DropContext(context);
                return false;
            }

            // Register() is not a verification. RotateCameraBlocker$$Register sets its own
            // _Registered = true unconditionally, and AddInputContext under
            // _DenyModificationsOfInputContexts logs ("You can not add an InputContext during
            // GetConsumedInputs or HandleInput!") and returns without adding. Trusting the
            // absence of an exception would cache a context whose own guard then makes every
            // later Register a silent no-op for the rest of the process (R31 finding 4).
            if (!StillRegistered(system, context))
            {
                Plugin.Logger?.LogWarning(
                    "Ui: the input context did not land — IsContextRegistered is false after "
                    + "AddInputContext(order=" + UiInputCapture.Order + "). The add was refused "
                    + "(_DenyModificationsOfInputContexts, or the list mutated under us); the instance is "
                    + "dropped rather than reused, so a later attempt starts from a clean object.");
                DropContext(context);
                return false;
            }

            _registered = true;

            // Job KEY3. Second, and after the 599 context has been verified: the guard is an
            // addition to a working panel, not a precondition for one. A refusal here is reported
            // and does not fail Register() — see RegisterGuardLocked.
            RegisterGuardLocked(system, world);
            return true;
        }
    }

    /// <summary>
    /// Job KEY3. Push the focus-scoped hotkey guard at
    /// <see cref="UiInputCapture.HotkeyGuardOrder"/>, with the same
    /// <c>AddInputContext</c> + <c>IsContextRegistered</c> shape the 599 context uses and for the
    /// same reason (R31 finding 4: <c>AddInputContext</c> under
    /// <c>_DenyModificationsOfInputContexts</c> logs and returns without adding, so an unverified
    /// add caches a context that is not in the list).
    ///
    /// <para>
    /// <b>It returns void and cannot fail the register.</b> The 599 context is what makes the panel
    /// usable — <c>BlockAllKeys</c>, Move/Aim, zoom, and Escape closing the panel without opening the
    /// vanilla menu — and it has already landed by the time this runs. Failing <see cref="Register"/>
    /// on a refused 498 add would therefore trade the whole panel for the in-menu letter block, and
    /// would do it on exactly the transient conditions the deny flag creates. The caller's own retry
    /// machinery does not help either, because from its point of view the register succeeded; so the
    /// next successful <see cref="Register"/> retries this, and until then
    /// <see cref="UiInputCapture.GuardRefusedLog"/> says precisely which behavior is missing.
    /// </para>
    ///
    /// <para>
    /// The deny flag is <b>not</b> re-read here. The caller checked it (<c>Execute</c> in
    /// <see cref="UiInputCapture"/>) and the 599 add has just been verified to have landed, which is
    /// stronger evidence that the list is mutable right now than a second read of the flag would be.
    /// </para>
    /// </summary>
    private void RegisterGuardLocked(InputActionSystem system, World world)
    {
        if (_guardRegistered) return;

        var guard = _guardContext ??= TryCreateContext();
        if (guard == null)
        {
            MenuHotkeyGuard.GuardRegistered = false;
            Plugin.Logger?.LogWarning(
                "Ui: could not construct the menu-hotkey guard context; "
                + UiInputCapture.GuardRefusedLog);
            return;
        }

        // The guard never blocks camera rotation. Written explicitly rather than trusted to the
        // ctor's default, because the field is the persistent one and a stale true on a reused
        // instance would block the camera for the rest of the process with no owner.
        ClearGuardCameraBlock(guard);

        // The pointer must be live before the add: the detour is already armed, and the first frame
        // after AddInputContext can enter Mask for this instance.
        lock (MaskGate) Volatile.Write(ref _guard, guard.Pointer);

        try
        {
            system.AddInputContext(
                new IInputContext(guard.Pointer), world, UiInputCapture.HotkeyGuardOrder);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning(
                "Ui: AddInputContext(order=" + UiInputCapture.HotkeyGuardOrder + ") threw: "
                + e.Message + " " + UiInputCapture.GuardRefusedLog);
            DropGuard(guard);
            return;
        }

        if (!StillRegistered(system, guard))
        {
            Plugin.Logger?.LogWarning(UiInputCapture.GuardRefusedLog);
            DropGuard(guard);
            return;
        }

        _guardRegistered = true;
        MenuHotkeyGuard.GuardRegistered = true;
        Plugin.Logger?.LogInfo(UiInputCapture.GuardRegisteredLog);
    }

    /// <summary>
    /// Forget a guard context we can no longer use: stop the mask matching its pointer, clear the
    /// readback, and drop the cache so the next <see cref="Register"/> builds a fresh instance. The
    /// persistent camera flag is cleared too — it is never set on this instance, and clearing it
    /// anyway is what makes a leaked 498 entry inert rather than probably inert.
    /// </summary>
    private void DropGuard(RotateCameraBlocker guard)
    {
        ClearGuardCameraBlock(guard);
        ForgetGuard(guard);
        _guardContext = null;
        _guardRegistered = false;
        MenuHotkeyGuard.GuardRegistered = false;
    }

    private static void ClearGuardCameraBlock(RotateCameraBlocker guard)
    {
        try { guard._BlockRotateCamera = false; }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: guard _BlockRotateCamera clear threw: " + e.Message);
        }
    }

    /// <summary>
    /// Take the guard context out of the game's list. Mirrors <see cref="Remove"/>'s rules:
    /// <c>RemoveInputContext</c> rather than <c>Deregister</c> (the instance's own
    /// <c>_Registered</c> guard was never set, because <c>Register</c> was never called), and the
    /// removal is verified rather than assumed.
    ///
    /// <para>
    /// Unlike the 599 removal a refusal here is not retried by the caller and does not need to be:
    /// the pointer is forgotten either way, so the leaked entry's mask stops matching, and its
    /// persistent camera flag was never set. It consumes nothing from the frame it is forgotten.
    /// </para>
    /// </summary>
    private void RemoveGuardLocked(InputActionSystem? system)
    {
        var guard = _guardContext;
        if (guard == null)
        {
            _guardRegistered = false;
            MenuHotkeyGuard.GuardRegistered = false;
            return;
        }

        if (system == null)
        {
            // The system is gone (world torn down): the list went with it.
            DropGuard(guard);
            return;
        }

        bool removed = true;
        try { system.RemoveInputContext(new IInputContext(guard.Pointer)); }
        catch (Exception e)
        {
            removed = false;
            Plugin.Logger?.LogWarning(
                "Ui: RemoveInputContext(order=" + UiInputCapture.HotkeyGuardOrder + ") threw: "
                + e.Message);
        }

        if (removed && StillRegistered(system, guard))
        {
            removed = false;
            Plugin.Logger?.LogWarning(
                "Ui: the menu-hotkey guard context order=" + UiInputCapture.HotkeyGuardOrder
                + " refused removal; the list entry is leaked until the process exits, but it is "
                + "inert (the mask no longer matches its pointer and _BlockRotateCamera was never "
                + "set), so it consumes no input.");
        }

        DropGuard(guard);
        if (removed) Plugin.Logger?.LogInfo(UiInputCapture.GuardRemovedLog);
    }

    /// <summary>Stop the mask postfix matching a guard context we have dropped.</summary>
    private static void ForgetGuard(RotateCameraBlocker guard)
    {
        IntPtr p;
        try { p = guard.Pointer; }
        catch { return; }

        lock (MaskGate)
            if (_guard == p) Volatile.Write(ref _guard, IntPtr.Zero);
    }

    /// <summary>
    /// Forget a context object we can no longer use: clear the persistent camera-block flag so a
    /// stray listed entry consumes nothing, stop the mask from matching its pointer, and drop the
    /// cache so the next <see cref="Register"/> builds a fresh instance.
    /// </summary>
    private void DropContext(RotateCameraBlocker context)
    {
        SetCameraBlock(context, false);
        ForgetOurs(context);
        _context = null;
        _registered = false;

        // Job KEY3. A dropped 599 context means the whole register is being reported as failed, so
        // the 498 one goes with it: the caller retries from a clean object, and a cached guard
        // instance from the failed attempt would be reused without being re-added. Every caller of
        // this method is in Register, above RegisterGuardLocked, so the guard is never in the game's
        // list here and forgetting it is enough — DropGuard makes it inert rather than removing it.
        var guard = _guardContext;
        if (guard != null) DropGuard(guard);
    }

    private void SetCameraBlock(RotateCameraBlocker context, bool blocked)
    {
        try
        {
            context._BlockRotateCamera = blocked;
            _cameraBlocked = blocked;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: _BlockRotateCamera set threw: " + e.Message);
            // Unknown is the pessimistic answer: only a successful write to false clears it.
            if (blocked) _cameraBlocked = true;
        }
    }

    public bool Remove()
    {
        lock (_gate)
        {
            // Job KEY3: the guard's lifetime is this context's, so every exit below takes it out
            // too — including the two early ones. It is read first because a state where the 599
            // context is already gone (DropContext, a torn-down world) but the 498 one is still
            // listed is exactly the state that must not be returned from as "removed".
            if (!_registered && !_guardRegistered && _guardContext == null) return true;

            InputActionSystem? system = null;
            try { system = InputHelper.GetInputActionSystem(); }
            catch (Exception e) { Plugin.Logger?.LogWarning("Ui: GetInputActionSystem threw on remove: " + e.Message); }

            var context = _registered ? _context : null;
            if (context == null)
            {
                RemoveGuardLocked(system);
                _registered = false;
                return true;
            }

            if (system == null)
            {
                // The system is gone (world torn down): the list went with it.
                RemoveGuardLocked(null);
                SetCameraBlock(context, false);
                _registered = false;
                return true;
            }

            RemoveGuardLocked(system);

            // RemoveInputContext, not Deregister: Register was never called, so the instance's own
            // _Registered guard is false and Deregister would return without touching the list
            // (job FUI20 — the order change is what made Register unusable).
            try { system.RemoveInputContext(new IInputContext(context.Pointer)); }
            catch (Exception e)
            {
                Plugin.Logger?.LogWarning("Ui: RemoveInputContext threw: " + e.Message);
                // The entry may still be listed. _BlockRotateCamera is a persistent field, not
                // the per-frame mask, so a leaked entry with it set blocks camera rotation for
                // the rest of the process (R31 finding 8). Make it inert before giving up.
                SetCameraBlock(context, false);
                return false;
            }

            if (StillRegistered(system, context))
            {
                SetCameraBlock(context, false);
                return false;
            }

            SetCameraBlock(context, false);
            _registered = false;
            return true;
        }
    }

    /// <summary>
    /// The scope <see cref="Mask"/> is currently applying. Volatile read, no lock: see
    /// <see cref="_scope"/>. An out-of-range value cannot arise from
    /// <see cref="SetMaskScope"/>, and <see cref="InputMaskPlan.For"/> is total anyway.
    /// </summary>
    private static UiMaskScope CurrentScope => (UiMaskScope)_scope;

    /// <summary>
    /// The scope <see cref="Mask"/> reads, exposed (job FUI23, R44 finding 4). This is the value
    /// that decides what is actually blocked this frame; <c>UiInputCapture._pointerBlock</c> is
    /// only the module's decision on its way here, and the two drifted apart with nothing able to
    /// see it. A field read, no lock, never throws — see <see cref="_scope"/>.
    /// </summary>
    public UiMaskScope AppliedScope => CurrentScope;

    /// <summary>
    /// Whether the <c>GetConsumedInputs</c> detour is installed on this build (job FUI23,
    /// R44 finding 3). <c>false</c> means <see cref="Mask"/> never runs: the context is registered
    /// with the vanilla <c>_BlockRotateCamera</c> behavior alone, so <c>Back</c> and
    /// <c>Menu_Close</c> are not consumed and Escape both closes the panel and opens the game menu.
    /// The register log says which of the two happened, from this.
    /// </summary>
    public bool MaskLive => _maskLive;

    /// <summary>
    /// Job FUI20. Changes what the mask blocks; never touches the game's context list, so the
    /// pointer may cross a panel edge as often as a hand can move it.
    ///
    /// <para>
    /// <b>The scope this is handed is the caller's whole responsibility</b> (job FUI27, R45
    /// finding 8), and the reason is the line below: <see cref="SetCameraBlock"/> writes the
    /// context object's <c>_BlockRotateCamera</c>, which is a <i>persistent</i> field that only
    /// <see cref="Remove"/>, <see cref="DropContext"/> or <see cref="Dispose"/> ever clear. The
    /// per-frame half of a scope lapses on its own; this half does not. So a
    /// <see cref="UiMaskScope.Full"/> pushed at the moment the last panel is closing outlives that
    /// panel for exactly as long as the removal takes — unbounded, on the deny-flag and
    /// refused-removal paths. <c>UiInputCapture.EffectiveScopeLocked</c> is what guarantees no
    /// caller can do that; this method deliberately does not second-guess it, because a blocker
    /// that overrode the scope it was handed would be the second writer the whole FUI23 design
    /// removed.
    /// </para>
    /// </summary>
    public void SetMaskScope(UiMaskScope scope)
    {
        _scope = (int)scope;

        // The persistent field is not part of the per-frame mask, so it has to be written here
        // rather than read by the postfix. Under _gate because it touches the cached context, and
        // harmless when there is none: the next Register applies the scope itself.
        lock (_gate)
        {
            var context = _context;
            if (context != null)
                SetCameraBlock(context, InputMaskPlan.For(scope).BlockCameraRotationFallback);
        }
    }

    public void Dispose()
    {
        // Job KEY2. The menu-hotkey focus flag is process-static, so a generation that went away
        // with a field focused would hand the next one a fullscreen menu whose tab keys are dead
        // and nothing focused to explain it. Cleared here rather than only on the pump's own paths
        // because Core Uninstall reaches this unconditionally, on every unload reason, and it is
        // not a game call: it cannot throw and needs no lock.
        MenuHotkeyGuard.Release();

        lock (_gate)
        {
            // Last chance to touch the object: after this nothing can reach it, so a context
            // still in the game's list must go out inert.
            var context = _context;
            if (context != null) SetCameraBlock(context, false);
            _context = null;
            _registered = false;

            // Job KEY3. Same last-chance rule for the guard. UiInputCapture.Uninstall calls Remove
            // before Dispose, so in the ordinary case this is already null; it is written anyway
            // because Dispose is reached unconditionally on every unload reason and a Remove that was
            // refused is exactly the path that leaves an object behind.
            var guard = _guardContext;
            if (guard != null) ClearGuardCameraBlock(guard);
            _guardContext = null;
            _guardRegistered = false;
            MenuHotkeyGuard.GuardRegistered = false;
        }

        UninstallMask();
    }

    private static bool StillRegistered(InputActionSystem system, RotateCameraBlocker context)
    {
        try
        {
            var asContext = new IInputContext(context.Pointer);
            return system.IsContextRegistered(asContext);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: IsContextRegistered threw: " + e.Message);
            return false;
        }
    }

    /// <summary>Stop the mask postfix matching a context object we have dropped.</summary>
    private static void ForgetOurs(RotateCameraBlocker context)
    {
        IntPtr p;
        try { p = context.Pointer; }
        catch { return; }

        lock (MaskGate)
            if (_ours == p) Volatile.Write(ref _ours, IntPtr.Zero);
    }

    private static RotateCameraBlocker? TryCreateContext()
    {
        try
        {
            var context = new RotateCameraBlocker();
            if (context.Pointer == IntPtr.Zero)
            {
                Plugin.Logger?.LogWarning("Ui: RotateCameraBlocker ctor produced a null pointer.");
                return null;
            }

            return context;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("Ui: could not construct a RotateCameraBlocker: " + e);
            return null;
        }
    }

    // ---- CHAT1: the chat click guard ----------------------------------------------------------

    private static readonly object ChatGate = new();
    private static RotateCameraBlocker? _chatContext;
    private static InputActionSystem? _chatSystem;

    /// <summary>
    /// CHAT1: true while the chat click guard belongs to the given input system and the mask detour is live.
    /// With <paramref name="verify"/>, also asks the game whether the context is still in its list (a game
    /// call; the caller does it every few hundred frames, not every frame). Main thread.
    /// </summary>
    internal static bool ChatGuardLive(InputActionSystem? system, bool verify)
    {
        RotateCameraBlocker? context;
        lock (ChatGate)
        {
            context = _chatContext;
            if (context == null || system == null || !ReferenceEquals(_chatSystem, system) || !_maskLive) return false;
        }

        // A check that cannot be read is not evidence the context is gone: re-adding on an unreadable answer
        // is how a retry loop would grow the game's list.
        return !verify || ChatRegistered(system, context) != false;
    }

    /// <summary>
    /// CHAT1: push the chat click guard at <see cref="UiInputCapture.ChatClickGuardOrder"/> into this client
    /// world's input system, once per world, and arm the shared mask detour if no panel has yet. Same
    /// <c>AddInputContext</c> + <c>IsContextRegistered</c> shape as the panel contexts, and the same safety
    /// rule as the 498 guard: <c>_BlockRotateCamera</c> is never set on it and its mask is pointer-gated
    /// and flag-gated (<see cref="Progenitor.Chat.ChatClickGuard.Active"/>), so a leaked entry takes nothing.
    /// Refused while no real blocker is installed (<see cref="UiInputCapture.HasBlocker"/>): the detour this
    /// arms is released only by that blocker's teardown. Main thread, outside a <c>GetConsumedInputs</c> call
    /// (the caller is a system hook). False: try again on a later frame.
    /// </summary>
    internal static bool RegisterChatGuard(World world)
    {
        if (world == null || !world.IsCreated || !UiInputCapture.HasBlocker) return false;
        InputActionSystem? system;
        try { system = InputHelper.GetInputActionSystem(); }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Chat: GetInputActionSystem threw: " + e.Message);
            return false;
        }

        if (system == null) return false;

        lock (ChatGate)
        {
            if (_chatContext != null && ReferenceEquals(_chatSystem, system))
            {
                if (ChatRegistered(system, _chatContext) != false) return true;
                // The game dropped it: forget the object and add a fresh one below.
                DropChatLocked();
            }

            // A new client world brings a new input system; the old list went with the old world.
            if (_chatContext != null) DropChatLocked();

            try { if (system._DenyModificationsOfInputContexts) return false; }
            catch { return false; }

            var context = TryCreateContext();
            if (context == null) return false;
            ClearGuardCameraBlock(context);

            lock (MaskGate)
            {
                EnsureDetourLocked();
                Volatile.Write(ref _chat, context.Pointer);
            }

            try { system.AddInputContext(new IInputContext(context.Pointer), world, UiInputCapture.ChatClickGuardOrder); }
            catch (Exception e)
            {
                Plugin.Logger?.LogWarning("Chat: AddInputContext(order=" + UiInputCapture.ChatClickGuardOrder + ") threw: " + e.Message);
                ForgetChatPointer(context);
                return false;
            }

            if (ChatRegistered(system, context) == false)
            {
                Plugin.Logger?.LogWarning("Chat: the chat click guard did not land in the input list; retrying on a later frame.");
                ForgetChatPointer(context);
                return false;
            }

            // Kept when the check could not be read: the add did not throw, and a retry would add a second entry.
            _chatContext = context;
            _chatSystem = system;
            Plugin.Logger?.LogInfo(
                "Chat: click guard registered (order=" + UiInputCapture.ChatClickGuardOrder + ", mask="
                + (_maskLive ? "live" : "absent: clicks on the chat box still reach the game") + ").");
            return true;
        }
    }

    /// <summary>
    /// CHAT1: take the chat click guard out of the input list (setting off, world change, Core unload). The
    /// pointer is forgotten whenever the object is dropped, so an entry whose removal was refused takes
    /// nothing. Without <paramref name="force"/> a removal is put off while the game refuses list changes (the
    /// caller asks again next frame); Core unload forces it. Main thread.
    /// </summary>
    internal static void RemoveChatGuard(bool force = false)
    {
        lock (ChatGate)
        {
            var context = _chatContext;
            if (context == null) return;
            InputActionSystem? system = null;
            try { system = InputHelper.GetInputActionSystem(); }
            catch { /* the world is going away: the list goes with it */ }

            if (system != null && ReferenceEquals(system, _chatSystem))
            {
                bool deny;
                try { deny = system._DenyModificationsOfInputContexts; }
                catch { deny = true; }
                if (deny && !force) return;

                try { system.RemoveInputContext(new IInputContext(context.Pointer)); }
                catch (Exception e) { Plugin.Logger?.LogWarning("Chat: RemoveInputContext threw: " + e.Message); }
                if (ChatRegistered(system, context) == true)
                    Plugin.Logger?.LogWarning("Chat: the click guard refused removal; the entry is inert (its pointer is forgotten).");
            }

            DropChatLocked();
        }
    }

    /// <summary>
    /// <c>IsContextRegistered</c> as three answers: true, false, or null when the check itself threw. The
    /// panel paths treat a throw as "not registered"; the chat guard must not, or a retry would add a second
    /// entry for a context that did land.
    /// </summary>
    private static bool? ChatRegistered(InputActionSystem system, RotateCameraBlocker context)
    {
        try { return system.IsContextRegistered(new IInputContext(context.Pointer)); }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Chat: IsContextRegistered threw: " + e.Message);
            return null;
        }
    }

    private static void DropChatLocked()
    {
        var context = _chatContext;
        if (context != null) ForgetChatPointer(context);
        _chatContext = null;
        _chatSystem = null;
    }

    private static void ForgetChatPointer(RotateCameraBlocker context)
    {
        IntPtr p;
        try { p = context.Pointer; }
        catch { return; }

        lock (MaskGate)
            if (_chat == p) Volatile.Write(ref _chat, IntPtr.Zero);
    }

    // ---- the mask detour ---------------------------------------------------------------------

    /// <summary>
    /// One-shot: does the interop agree with the metadata-read constant the mask actually blocks?
    /// (Job FUI27, R45 finding 28.)
    ///
    /// <para>
    /// <see cref="Mask"/> blocks <see cref="InputMaskPlan.BlockedCloseActions"/>, which is
    /// <c>ButtonInputActions.Back</c> and <c>ButtonInputActions.MenuClose</c> — two integers read
    /// from the client metadata. <c>Menu_Close</c> has no interop enum member to compare against, so it
    /// is a constant either way; <c>Back</c> does, and until FUI27 the mask spelled it as the
    /// interop member while the test pinned the constant. If a game update renumbers
    /// <c>ButtonInputAction.Back</c>, that arrangement blocked whatever the interop said while the
    /// test went on asserting 17, and if it renumbers <c>Menu_Close</c> the mask blocked a stale
    /// 1004 and the test still passed — one of the two values verified and the other not, with no
    /// way to tell which. The mask is now uniformly the constants, and the one comparison that can
    /// still be made is made here and logged rather than silently relied on.
    /// </para>
    ///
    /// <para>
    /// It warns and does not correct: <c>Menu_Close</c> could not be corrected the same way, so
    /// following the interop for one and the metadata for the other would be the asymmetry again. A
    /// disagreement means the metadata is stale for this build and both constants need re-reading.
    /// </para>
    /// </summary>
    private static void CheckCloseActionIdsLocked()
    {
        if (_closeActionsChecked) return;
        _closeActionsChecked = true;

        try
        {
            int back = (int)ButtonInputAction.Back;
            if (back == ButtonInputActions.Back) return;

            Plugin.Logger?.LogWarning(
                "Ui: ButtonInputAction.Back is " + back.ToString(CultureInfo.InvariantCulture)
                + " in this build's interop but the input mask blocks the metadata-read constant "
                + ButtonInputActions.Back.ToString(CultureInfo.InvariantCulture)
                + ", so Escape may reach MenuInputSystem and open the vanilla menu on top of a mod "
                + "panel. Re-read ButtonInputAction from the client metadata and update "
                + "ButtonInputActions.Back and .MenuClose together (Menu_Close has no interop "
                + "member to check against, so it cannot be verified from here).");
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: close-action id check threw: " + e.Message);
        }
    }

    private static void EnsureMask(RotateCameraBlocker context)
    {
        lock (MaskGate)
        {
            Volatile.Write(ref _ours, context.Pointer);
            EnsureDetourLocked();
        }
    }

    /// <summary>
    /// Install the <c>GetConsumedInputs</c> detour if it is not live yet. Split out of
    /// <see cref="EnsureMask"/> by CHAT1 so the chat click guard can arm the same detour without a mod
    /// panel ever having been shown; it writes no instance pointer. Under <see cref="MaskGate"/>.
    /// </summary>
    private static void EnsureDetourLocked()
    {
        CheckCloseActionIdsLocked();
        if (_maskLive) return;

        DetourResolveResult resolved;
        try
        {
            // Job FACC: the prefix scan over the interop's NativeMethodInfoPtr_* statics moved
            // into the generated access layer. Same mechanism, same failure mode — but the
            // member is now declared in src/Progenitor.Core/Access/access.txt, so the
            // resilience report names it if a game update removes it, and the exception says
            // which member rather than which generated field name.
            resolved = DetourSite.Resolve(
                Site,
                AddressIdGetConsumedInputs,
                () => Progenitor.Access.Access.RotateCameraBlocker.GetConsumedInputsPointer());
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: input mask resolve threw: " + e.Message);
            return;
        }

        if (!resolved.Ok)
        {
            Plugin.Logger?.LogInfo(
                "Ui: input mask not installed; the context blocks camera rotation only. Add '"
                + AddressIdGetConsumedInputs + "' to tools/addresslib/wanted-client.txt and rebuild "
                + "(node tools/addresslib/build.mjs --side client).");
            return;
        }

        // Job FLATCH3 (R45 finding 19): offer the detour to Bootstrap first. Bootstrap is not
        // collectible, so a handle it owns outlives every Core generation and this generation
        // never has to unpatch native code to go away — see UninstallMask. A false answer is
        // an absent, older, or failed Bootstrap and is not an error: the Core-owned path below
        // is exactly what shipped before, thread rule and all.
        try
        {
            if (BootstrapLink.TryInstallUiMask(
                    resolved.Address, new BootstrapLink.UiMaskApply(Mask)))
            {
                _bootstrapOwnsMask = true;
                _maskLive = true;
                Plugin.Logger?.LogInfo(
                    "Ui: input mask installed at 0x"
                    + resolved.Address.ToInt64().ToString("X", CultureInfo.InvariantCulture)
                    + " path=" + resolved.Path + " owner=bootstrap (process-lifetime detour; "
                    + "Core teardown nulls the slot and disposes nothing).");
                return;
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: Bootstrap input-mask hand-off threw: " + e.Message);
        }

        try
        {
            _hook = Mask;
            // Through a local so the publish is a Volatile.Write like every other write to
            // this field (job FLATCH2). CreateAndApply arms the JMP before it returns, so a
            // Mask entered in that window still sees a null original and skips the game's
            // body for one call - unchanged by this, and not something a store barrier can
            // close from here.
            _detour = INativeDetour.CreateAndApply(resolved.Address, _hook, out var original);
            Volatile.Write(ref _original, original);
            _maskLive = true;
            try { BootstrapLink.TryFunctionPointer(_hook, PinName, out _); }
            catch (Exception e) { Plugin.Logger?.LogWarning("Ui: input mask pin: " + e.Message); }
            Plugin.Logger?.LogInfo(
                "Ui: input mask installed at 0x"
                + resolved.Address.ToInt64().ToString("X", CultureInfo.InvariantCulture)
                + " path=" + resolved.Path + " owner=core (this generation owns the detour and "
                + "must dispose it on the game main thread; see UninstallMask).");
        }
        catch (Exception e)
        {
            _detour = null;
            Volatile.Write(ref _original, null);
            _hook = null;
            _maskLive = false;
            Plugin.Logger?.LogError("Ui: input mask install failed: " + e);
        }
    }

    /// <summary>
    /// Give the mask up. Which half of this runs is <see cref="_bootstrapOwnsMask"/>, and the two
    /// halves are not equally dangerous, so the distinction is the point of job FLATCH3.
    ///
    /// <para><b>Bootstrap owns the detour (the normal path).</b> Nothing native is unwound here at
    /// all: the JMP stays armed at <c>Progenitor.Bootstrap.UiMaskDetour.Hook</c>, which is a
    /// Bootstrap delegate no ALC unload can collect, and this teardown is one volatile store into
    /// Bootstrap's slot. It is safe from any thread and safe while the game is inside
    /// <c>GetConsumedInputs</c>, because the slot is re-read on every call. <see cref="_ours"/> is
    /// zeroed first, above, so a frame already inside <see cref="ApplyMask"/> when the store lands
    /// takes the guard and touches nothing.</para>
    ///
    /// <para><b>Core owns the detour (the fallback path).</b> This is FLATCH2's rule, unchanged
    /// and still live on a client whose Bootstrap predates <c>UiMaskDetour</c> or could not apply
    /// it: <b>it must run on the game main thread.</b> Disposing an <c>INativeDetour</c> restores
    /// the patched bytes and frees the trampoline <see cref="Mask"/> calls through, and the game
    /// calls <c>GetConsumedInputs</c> once per frame per registered context — so off the main
    /// thread the failure mode is a jump into freed native memory: a hard crash with no managed
    /// stack (the same rule <c>KeyTableProbe</c> is under, R22 finding 3). Neither posting the
    /// dispose (the generation carrying the queue is what is going away) nor skipping it
    /// (<see cref="_hook"/> is a Core delegate, so an armed detour outliving the ALC is a
    /// guaranteed crash) is available once this generation owns the handle. That is the whole
    /// argument for the branch above.</para>
    ///
    /// <para>On the fallback path <c>_original</c> is still cleared <em>before</em> the dispose. It
    /// does not close the window — a <c>Mask</c> that already copied the field into its local is
    /// past this point — but it shortens it to exactly that, and the cost is that a <c>Mask</c>
    /// entered in the gap skips the game's own body for one call during teardown.</para>
    /// </summary>
    private static void UninstallMask()
    {
        lock (MaskGate)
        {
            Volatile.Write(ref _ours, IntPtr.Zero);
            // Job KEY3: both guards, together and first, so a frame already inside Mask when the
            // slot store below lands takes the pointer check and touches nothing whichever of the
            // two contexts it was entered for.
            Volatile.Write(ref _guard, IntPtr.Zero);
            Volatile.Write(ref _chat, IntPtr.Zero);
            // Back to the documented default, so the next Core generation starts fail-closed
            // rather than inheriting a CloseKeyOnly scope from a pointer that has since moved.
            _scope = (int)UiMaskScope.Full;
            if (!_maskLive) return;
            if (HandBackBootstrapMaskLocked()) return;
            Volatile.Write(ref _original, null);
            try { _detour?.Dispose(); }
            catch (Exception e) { Plugin.Logger?.LogWarning("Ui: input mask dispose: " + e.Message); }
            _detour = null;
            _hook = null;
            _maskLive = false;
        }
    }

    /// <summary>
    /// The Bootstrap half of <see cref="UninstallMask"/>, and false when this generation owns the
    /// detour itself. Split out so the caller stays short enough to read as one rule, and so the
    /// line that says which of the two happened is a measurement rather than an assumption — the
    /// off-thread warning in <c>UiInputCapture.Uninstall</c> has to name a hazard whose presence
    /// depends on this answer.
    /// </summary>
    private static bool HandBackBootstrapMaskLocked()
    {
        if (!_bootstrapOwnsMask) return false;

        // Nulls Bootstrap's slot. No dispose, no unpatch, no native memory freed — so unlike the
        // branch below this is safe on whatever thread the teardown arrived on.
        try { BootstrapLink.ClearUiMask(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: input mask hand-back: " + e.Message); }

        _bootstrapOwnsMask = false;
        _maskLive = false;
        Plugin.Logger?.LogInfo(
            "Ui: input mask released; the GetConsumedInputs detour is Bootstrap-owned and stays "
            + "armed for the life of the process, so this teardown freed no native code.");
        return true;
    }

    /// <summary>
    /// Runs for every <c>RotateCameraBlocker</c> in the process, ours and the game's. The
    /// original always runs first; the mask is applied only when the instance pointer is ours.
    ///
    /// <para><b>One body, two owners</b> (job FLATCH3). On the Core-owned path this is the armed
    /// trampoline and <see cref="_original"/> is the game's own <c>GetConsumedInputs</c>. On the
    /// Bootstrap-owned path this is Bootstrap's slot, <c>UiMaskDetour.Hook</c> has already called
    /// the game's body, and <see cref="_original"/> is <c>null</c> — it is never written on that
    /// path, because Bootstrap owns the trampoline and this generation must not hold a pointer
    /// into it. So the <c>?.</c> below is not defensive: it is the difference between the two
    /// owners, and it keeps the mask itself a single piece of code that cannot drift.</para>
    /// </summary>
    private static void Mask(IntPtr instance, IntPtr blockInputState, IntPtr methodInfo)
    {
        var original = Volatile.Read(ref _original);
        try { original?.Invoke(instance, blockInputState, methodInfo); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: original GetConsumedInputs threw: " + e.Message); }

        // Volatile.Read on the guard for the same reason _maskLive and _scope are volatile: this
        // is a native callback on the game's input path holding no lock, and _ours is what keeps
        // the mask off a vanilla context (job FLATCH2, R45 finding 20).
        //
        // Job KEY3: two pointers now, and which one matched selects the recipe rather than only
        // authorizing one. The branch is MenuHotkeyGuard.TargetFor so it is a pure function with its
        // own tests — this method cannot be called from the test assembly at all.
        if (blockInputState == IntPtr.Zero) return;
        HotkeyMaskTarget target = MenuHotkeyGuard.TargetFor(
            instance, Volatile.Read(ref _ours), Volatile.Read(ref _guard), Volatile.Read(ref _chat));
        if (target == HotkeyMaskTarget.None) return;

        if (target == HotkeyMaskTarget.Guard)
        {
            MaskGuard(blockInputState);
            return;
        }

        if (target == HotkeyMaskTarget.Chat)
        {
            MaskChat(blockInputState);
            return;
        }

        try
        {
            var plan = InputMaskPlan.For(CurrentScope);
            ref BlockInputState state = ref Unsafe.AsRef<BlockInputState>((void*)blockInputState);

            if (plan.BlockAllKeys)
            {
                // includeUINavigation stays false. True would take
                // ButtonInputActionCollection.UINavigationActions with it and the
                // panel would stop responding to its own uGUI widgets — which is exactly why chat
                // passes false. The close actions are named below instead of hoped for here.
                state.BlockAllKeys(plan.IncludeUiNavigation);
            }

            if (plan.BlockMoveAndAim)
            {
                state.BlockedAnalogInputActions |= AnalogInputAction.MoveX
                    | AnalogInputAction.MoveY
                    | AnalogInputAction.AimX
                    | AnalogInputAction.AimY;
            }

            // ZoomCamera (client value 16) is ours and not chat's. Chat
            // deliberately leaves the wheel alive - a player typing still wants to zoom -
            // but a mod panel scrolls with that same wheel, so every notch scrolled the
            // list and zoomed the camera underneath it at the same time (evidence 167).
            // Blocking the analog stops the camera; it does not zero Mouse.current.scroll
            // and it does not raise uGUI OnScroll. The shown-input pump reads that device
            // and writes the hovered list. Only in the full scope.
            if (plan.BlockZoom)
                state.BlockedAnalogInputActions |= AnalogInputAction.ZoomCamera;

            // Exclusive scope (photo mode): every analog action and camera rotation, so no stick,
            // mouse look, wheel or map axis reaches the game while a framework mode owns input.
            if (plan.BlockAllAnalog)
                state.BlockedAnalogInputActions |= AnalogInputAction.All;
            if (plan.BlockCameraRotation)
            {
                state.BlockKey(ButtonInputAction.RotateCamera);
                state.BlockKeyDown(ButtonInputAction.RotateCamera);
            }

            // Job FUI20, and the whole of defect 1. Taken in EVERY scope, including the one where
            // the pointer is nowhere near the panel: Escape is the panel's close gesture and has
            // to work from anywhere, and MenuInputSystem reaches the vanilla
            // system menu only through the InputState this call is subtracting from — its own
            // OnUpdate is the shared empty stub. Both actions, because the Escape
            // key carries the Unity usages "Back" and "Cancel" and ProjectM has
            // a separate Menu_Close in the UI-navigation block.
            //
            // Job FUI27 (R45 finding 28): the list comes from InputMaskPlan rather than being
            // spelled again here. It was documented as the protection against a future scope
            // dropping the close key and nothing read it, so a third action added there would not
            // have been blocked and the only test that noticed would have failed on the array's
            // length. BlockedCloseActions returns the live array, so this allocates nothing on the
            // game's per-frame input path. Both entries are cast from our own metadata-read constants
            // now — the old code spelled Back as the interop enum member and Menu_Close as a cast,
            // so one of the two values the whole of defect 1 rests on was cross-checked against the
            // interop and the other was not, invisibly. CheckCloseActionIdsLocked does that
            // cross-check once, out loud, at install time.
            int[] closes = InputMaskPlan.BlockedCloseActions();
            for (int i = 0; i < closes.Length; i++)
                state.BlockKey((ButtonInputAction)closes[i]);

            // Job KEY2. While a Progenitor text field has owned focus, the fullscreen menu's own
            // letter shortcuts are taken too: P (ClanMenu = 23) reached
            // MenuInputSystem.HandleInput and switched the menu to Social while
            // the same press was being typed into the search field. Orthogonal to the scope on
            // purpose — a field is focused or it is not, and where the pointer happens to be is
            // not part of that question. Both lists, because a menu shortcut is a press and
            // BlockedNewButtonInputActions is the list a press is read from;
            // see MenuHotkeyGuard for why BlockAllKeys(false) could not be relied on to cover it.
            if (MenuHotkeyGuard.HotkeysBlocked || plan.BlockMenuHotkeys)
            {
                int[] hotkeys = MenuHotkeyGuard.BlockedActions();
                for (int i = 0; i < hotkeys.Length; i++)
                {
                    state.BlockKey((ButtonInputAction)hotkeys[i]);
                    state.BlockKeyDown((ButtonInputAction)hotkeys[i]);
                }
            }

            if (plan.AllowCameraRotationOnKeyboardAndMouse
                && state.ControllerType == ControllerType.KeyboardAndMouse)
            {
                state.AllowKey(ButtonInputAction.RotateCamera);
                state.BlockedAnalogInputActions &= ~(AnalogInputAction.RotateCameraX | AnalogInputAction.RotateCameraY);
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: input mask threw: " + e.Message);
        }
    }

    /// <summary>
    /// CHAT1. The order-97 context's whole mask: the chat click guard's actions, in both button lists, on
    /// keyboard and mouse only, while <see cref="Progenitor.Chat.ChatClickGuard.Active"/>; nothing else. No
    /// allocation: the array is replaced whole by its owner, never mutated.
    /// </summary>
    private static void MaskChat(IntPtr blockInputState)
    {
        if (!Progenitor.Chat.ChatClickGuard.Active) return;
        try
        {
            ref BlockInputState state = ref Unsafe.AsRef<BlockInputState>((void*)blockInputState);
            if (state.ControllerType != ControllerType.KeyboardAndMouse) return;
            int[] actions = Progenitor.Chat.ChatClickGuard.BlockedActions();
            for (int i = 0; i < actions.Length; i++)
            {
                state.BlockKey((ButtonInputAction)actions[i]);
                state.BlockKeyDown((ButtonInputAction)actions[i]);
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Chat: click guard mask threw: " + e.Message);
        }
    }

    /// <summary>
    /// Job KEY3. The order-498 context's whole mask: the menu-hotkey set, in both button lists, and
    /// <b>nothing else</b> — no <c>BlockAllKeys</c>, no Move/Aim, no zoom, no close actions, no
    /// <see cref="UiMaskScope"/>. This context is walked before <c>FullscreenMenuMapper</c> at
    /// <see cref="UiInputCapture.HudMenuOrder"/> and therefore before every HUD menu in the game; the
    /// difference between "the letter hotkeys are taken while a search field is focused" and "every
    /// key in the game is taken ahead of the HUD" is this method staying this short.
    ///
    /// <para>
    /// Both lists, as KEY2 established: <c>BlockKey</c> writes
    /// <c>BlockedButtonInputActions</c> and
    /// <c>BlockKeyDown</c> writes <c>BlockedNewButtonInputActions</c>. The mapper's native
    /// <c>HandleInput</c> reads the outer-tab letters from a <em>third</em> list,
    /// <c>InputsUp</c>, and there is no
    /// <c>BlockKeyUp</c> to write — whether <c>BlockKey</c> also subtracts from a later context's
    /// <c>InputsUp</c> is a native body the metadata does not contain. The four tab actions <i>are</i>
    /// read from <c>InputsDown</c>, so those are settled either way.
    /// </para>
    ///
    /// <para>
    /// Which is what the counter is for. <see cref="MenuHotkeyGuard.NoteMaskApplied"/> makes
    /// "the guard ran on the frame the tab moved" — the answer that condemns <c>BlockKey</c> for
    /// <c>InputsUp</c> and calls for <see cref="MenuHotkeyGuard.UseSetStatePrefix"/> — distinguishable
    /// live from "the guard never ran", which would be an ordering or registration bug instead. It is
    /// counted per application rather than per press because this callback is per frame per context
    /// and a held key is many frames; the readback reports the count in the last second, not a total.
    /// </para>
    /// </summary>
    private static void MaskGuard(IntPtr blockInputState)
    {
        bool hotkeysBlocked = MenuHotkeyGuard.HotkeysBlocked;
        bool closeHeld = MenuHotkeyGuard.CloseHeld;
        if (!hotkeysBlocked && !closeHeld) return;

        try
        {
            ref BlockInputState state = ref Unsafe.AsRef<BlockInputState>((void*)blockInputState);

            // KEYS3 polish (live P2, 2026-09-30): while a key capture or a framework dialog owns Escape,
            // the close actions are taken here too, ahead of FullscreenMenuMapper (502), so the game's
            // menu does not close under the routed workspace. Both lists, as for the hotkeys; the
            // same live array as the 599 recipe (no allocation on the input path).
            if (closeHeld)
            {
                int[] closes = InputMaskPlan.BlockedCloseActions();
                for (int i = 0; i < closes.Length; i++)
                {
                    state.BlockKey((ButtonInputAction)closes[i]);
                    state.BlockKeyDown((ButtonInputAction)closes[i]);
                }

                if (MenuHotkeyGuard.NoteCloseHoldApplied())
                    Plugin.Logger?.LogInfo(
                        "Ui: menu-hotkey guard took Back/Menu_Close ahead of FullscreenMenuMapper (order "
                        + UiInputCapture.HotkeyGuardOrder + " < " + UiInputCapture.HudMenuOrder
                        + ") while a key capture or a dialog owns Escape.");
            }

            if (!hotkeysBlocked) return;

            int[] hotkeys = MenuHotkeyGuard.BlockedActions();
            for (int i = 0; i < hotkeys.Length; i++)
            {
                state.BlockKey((ButtonInputAction)hotkeys[i]);
                state.BlockKeyDown((ButtonInputAction)hotkeys[i]);
            }

            // Counted after the writes, so the count is applications that happened and not attempts.
            if (MenuHotkeyGuard.NoteMaskApplied(Environment.TickCount64))
                Plugin.Logger?.LogInfo(
                    "Ui: menu-hotkey guard mask applied for the first time (context order="
                    + UiInputCapture.HotkeyGuardOrder + ", ahead of FullscreenMenuMapper at "
                    + UiInputCapture.HudMenuOrder + "; " + hotkeys.Length
                    + " actions in both button lists). If the fullscreen menu's tab still moves while "
                    + "a Progenitor search field is focused AND mods_ui status.focus.guard.masked is "
                    + "incrementing, then BlockKey does not clear a later context's InputsUp and the "
                    + "MenuHotkeyGuard.UseSetStatePrefix fallback is the fix.");
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: menu-hotkey guard mask threw: " + e.Message);
        }
    }
}
#endif
