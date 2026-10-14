using System;

namespace Progenitor.Ui;

/// <summary>
/// Refcounted ownership of one mod input context, so an open panel stops the vampire from
/// walking, aiming and swinging underneath it. Game-free on purpose: the game's ordered
/// context list is behind <see cref="IUiInputBlocker"/>.
///
/// <para>
/// The mechanism (job FUIC): <c>ProjectM.InputActionSystem</c>
/// owns a list of <c>IInputContext</c> ascending by <c>InputContextOrder</c>, and each entry
/// subtracts inputs from the ones after it (<c>Camera = 1000</c>, <c>Gameplay = 1001</c> are
/// last on purpose). A mod cannot author a context type — <c>IInputContext</c> is emitted by
/// Il2CppInterop as a class — but the game ships an instantiable one,
/// <c>ProjectM.RotateCameraBlocker</c>, whose <c>Register</c> is exactly
/// <c>AddInputContext(this, world, 600)</c>. We push that instance ourselves at <see cref="Order"/>
/// — <b>not</b> through its own <c>Register</c>, whose hard-coded 600 ties us with the system that
/// opens the vanilla escape menu (job FUI20) — and postfix its uniquely-addressed
/// <c>GetConsumedInputs</c>, guarded on our own instance pointer, to apply chat's
/// mask recipe: <c>BlockAllKeys(false)</c>, analog Move/Aim, our own <c>ZoomCamera</c>
/// (evidence 167: the wheel scrolls a panel, so it must not also zoom the camera), and
/// <c>RotateCamera</c> kept alive on
/// keyboard-and-mouse (<c>ChatFocusedInputContext.GetConsumedInputs</c>) — plus
/// <c>Back</c> and <c>Menu_Close</c>, which are taken in every <see cref="UiMaskScope"/> so that
/// one Escape closes the panel without also opening the game's menu (see
/// <see cref="InputMaskPlan"/>).
/// </para>
///
/// <para>
/// Two rules this class exists to enforce. (1) <b>Never mutate the list inside a callback</b>:
/// <c>_DenyModificationsOfInputContexts</c> makes both mutators log an error and no-op, so a
/// change requested while it is set is deferred to the next frame tick. (2) <b>Nothing may
/// survive teardown</b>: panel dispose, plugin unload and Core Uninstall all drive the count
/// back to zero and remove the context, because a Core swap that left one registered would
/// leave the player's movement blocked with no owner (collectible-ALC rules, docs/modules.md).
/// </para>
///
/// <para>
/// The blocked state is a per-frame mask, so losing it costs one frame of movement rather than a
/// stuck character — with one exception that teardown has to handle: the context object carries a
/// persistent <c>_BlockRotateCamera</c> field, and vanilla <c>GetConsumedInputs</c> blocks
/// <c>RotateCamera</c> unconditionally while it is set. A removal the game refuses therefore has
/// to leave that field <c>false</c> (<see cref="IUiInputBlocker.BlocksCameraRotation"/>), or a
/// leaked list entry would block the camera for the rest of the process with no owner.
/// </para>
///
/// <para>
/// (3) <b>Main thread only.</b> The deny flag is set only while the main thread is inside a
/// context callback, so it says nothing about a caller on another thread; an off-thread
/// <c>Show</c> would read <c>false</c> and mutate <c>_InputContexts</c> under the game's own
/// <c>OnUpdate</c> walk. Every mutation is therefore posted through <c>MainThread</c> when the
/// caller is not on it. Teardown is the exception and is documented at <see cref="Uninstall"/>.
/// </para>
///
/// <para>
/// (4) <b>No game call runs under <c>Gate</c>.</b> <c>Gate</c> protects module state — the
/// refcount, the enabled flag, the retry budget, the pump subscription — and nothing else, the
/// same rule Effects and Patches follow (<c>PatchRegistry.RemoveWhere</c> removes under its lock
/// and unpatches outside it). Every pass is therefore three steps: <see cref="DecideLocked"/>
/// turns module state into one <see cref="Act"/> under <c>Gate</c>; <see cref="Execute"/> makes
/// the <c>AddInputContext</c> / <c>RemoveInputContext</c> / <c>IsContextRegistered</c> calls with
/// no lock held; <see cref="FoldLocked"/> writes the result back under <c>Gate</c> after
/// re-checking that the decision is still current. That is what makes the two hazards in
/// R32 finding 13 impossible rather than merely unlikely: a blocker that pumps, waits, or
/// re-enters this module during <c>Register</c> cannot deadlock against a concurrent
/// <c>Acquire</c> (the re-entrant call takes a free <c>Gate</c> and returns), and a panel closed
/// while a register is in flight is caught by the re-check and answered with a follow-up remove
/// instead of leaking the context.
/// </para>
/// </summary>
internal static class UiInputCapture
{
    /// <summary>
    /// One below <c>InputContextOrder.MenuInput</c> (600), which
    /// keeps every ordering relationship the module has ever documented — still below chat (99)
    /// and the HUD menus (495-502), so a
    /// vanilla menu opened on top still wins, and still above Camera (1000) / Gameplay (1001)
    /// — while removing the one relationship that was not an
    /// ordering at all.
    ///
    /// <para>
    /// <b>Why not 600 (job FUI20).</b> <c>RotateCameraBlocker.Register</c> hard-codes
    /// <c>AddInputContext(this, world, 600)</c>, and 600 is <c>MenuInput</c> — the order
    /// <c>MenuInputSystem</c> itself sits at. Registering there made our context and the system
    /// that opens the vanilla menu <i>peers</i>, and which of two equal-order entries subtracts
    /// first is decided by a sort we cannot read offline (<c>InputContextState.Order</c>,
    /// sorted through a <c>Comparison&lt;InputContextState&gt;</c>; the
    /// comparison bodies are not in the game's metadata). A close key that is
    /// consumed or not depending on an unspecified tie-break is exactly the half-working fix
    /// defect 1 called for avoiding, so the tie is removed instead of reasoned about: no named
    /// order lies between <c>HUDMenu</c> (502) and <c>MenuInput</c> (600), so 599 is free and is
    /// strictly ahead of the menu system.
    /// </para>
    ///
    /// <para>
    /// The cost is that the context can no longer be pushed by <c>RotateCameraBlocker.Register</c>
    /// — it hard-codes 600 — so <c>GameInputBlocker</c> calls
    /// <c>InputActionSystem.AddInputContext(IInputContext, World, int)</c>
    /// and <c>RemoveInputContext</c> directly, and
    /// must therefore not call the blocker's own <c>Deregister</c>, whose <c>_Registered</c> guard
    /// we never set. Both halves already verify against <c>IsContextRegistered</c>,
    /// which is what makes the swap safe rather than hopeful.
    /// </para>
    /// </summary>
    public const int Order = 599;

    /// <summary>
    /// <c>InputContextOrder.MenuInput</c> in the client metadata. Named so the reason
    /// <see cref="Order"/> is what it is survives someone tidying the number.
    /// </summary>
    public const int MenuInputOrder = 600;

    /// <summary>
    /// <c>InputContextOrder.HUDMenu</c> in the client metadata — and the order
    /// <c>ProjectM.UI.FullscreenMenuMapper</c> registers <i>itself</i> at:
    /// <c>AddInputContext(this, world, 0x1f6)</c> in its native <c>OnStartRunning</c>.
    /// Named because <see cref="HotkeyGuardOrder"/> means nothing without it.
    /// </summary>
    public const int HudMenuOrder = 502;

    /// <summary>
    /// Job KEY3. The order of the <b>second</b> context Progenitor registers: the focus-scoped
    /// menu-hotkey guard, whose whole reason for existing is to be walked <i>before</i>
    /// <see cref="HudMenuOrder"/>.
    ///
    /// <para>
    /// <b>Why <see cref="Order"/> could not do this job.</b> <c>InputActionSystem.OnUpdate</c> walks
    /// <c>_InputContexts</c> ascending from index 0 and each context subtracts its
    /// <c>BlockInputState</c> from the ones after it
    /// (the game's native <c>ProjectM.InputActionSystem.OnUpdate</c>). The in-menu tab hotkeys are consumed by
    /// <c>FullscreenMenuMapper.HandleInput</c>, and the mapper is a context
    /// at 502 — <i>ahead</i> of 599. So KEY2's mask on 599 was walked after the consumer had already
    /// read the key, and live on 2026-09-12 a scan-code <c>P</c> with the Install search focused
    /// still switched the menu to Social with <c>hotkeysBlocked: true</c> in the readback. A mask
    /// cannot subtract from something already read; only an earlier context can.
    /// </para>
    ///
    /// <para>
    /// <b>Why 498 and not 496, 497 or 502.</b> The named orders around here are HUDElement 495,
    /// GenericPopup 499, DebugMenu 500, HUDSubMenu 501, HUDMenu 502,
    /// so 496, 497 and 498 are the free integers strictly below the
    /// mapper with no named order to collide with. Any of the three would do and 498 is the closest
    /// to the consumer, which keeps the guard behind every other HUD context that might legitimately
    /// want the key first. <b>Never tie.</b> <c>AddInputContext</c> inserts at the first index whose
    /// order is ≥ the new one, so two entries at an equal order are processed in registration order —
    /// which for the mapper is "whenever its system started running". That is the unspecified
    /// tie-break job FUI20 moved <see cref="Order"/> off 600 to escape, and it would be a worse bug
    /// here, where the whole fix <i>is</i> the ordering.
    /// </para>
    ///
    /// <para>
    /// <b>This is not a second input mask.</b> The 498 context applies the hotkey set and nothing
    /// else, and only while a Progenitor field has owned focus — see
    /// <see cref="HotkeyMaskTarget.Guard"/>. Putting any part of the 599 recipe on it would move
    /// <c>BlockAllKeys</c>, Move/Aim, zoom and the close actions ahead of every HUD menu in the game,
    /// which is a far larger change than the defect asked for. The <see cref="Order"/> block stays
    /// exactly as KEY2 left it, because that is what covers <c>MenuInputSystem</c> at 600 — the
    /// out-of-menu path, where <c>P</c> opens Social with no menu on screen.
    /// </para>
    /// </summary>
    public const int HotkeyGuardOrder = 498;

    /// <summary>
    /// CHAT1: the order of the chat click guard, a third <c>RotateCameraBlocker</c> registered for the
    /// whole of a client world (not per panel). Ahead of the game's own chat contexts (99,
    /// <c>InputContextOrder.ChatInput</c>) and of everything that reads gameplay buttons (Camera 1000,
    /// Gameplay 1001). Its mask is the mouse-bound gameplay actions and nothing else, and only while the
    /// pointer rests over the visible chat box or a press that started there is still held
    /// (<c>Progenitor.Chat.ChatClickGuard</c>), so
    /// the game's own chat hover context, which takes only the zoom axis, no longer lets a click on the
    /// chat box reach the attack or the camera.
    /// </summary>
    public const int ChatClickGuardOrder = 97;

    /// <summary>
    /// What was registered for the hotkey guard, and what it does and does not cover. Separate from
    /// <see cref="RegisteredLog"/> because the two contexts fail independently: the 498 add can be
    /// refused while the 599 one landed, and a reader then needs to know that the in-menu tab keys
    /// are live again while everything else about the panel is fine.
    /// </summary>
    public static string GuardRegisteredLog =>
        "Ui: menu-hotkey guard context registered order=" + HotkeyGuardOrder
        + " (ahead of FullscreenMenuMapper at " + HudMenuOrder + ", which is what reads the in-menu "
        + "tab letters; it masks the hotkey set only, and only while a Progenitor field has owned "
        + "focus).";

    public static string GuardRemovedLog =>
        "Ui: menu-hotkey guard context removed order=" + HotkeyGuardOrder;

    /// <summary>
    /// The 498 add did not land while the 599 one did. Deliberately not a failure of
    /// <c>Register()</c>: the panel is usable, Escape still closes it, and the out-of-menu hotkey
    /// block on <see cref="Order"/> is still in place. What is lost is exactly the KEY3 defect —
    /// typing in a search field can still move the fullscreen menu's tab.
    /// </summary>
    public static string GuardRefusedLog =>
        "Ui: menu-hotkey guard context did NOT land order=" + HotkeyGuardOrder
        + "; the order-" + Order + " context is registered and the panel works, but the in-menu "
        + "letter hotkeys are not being taken, so typing in a Progenitor search field can still "
        + "switch the fullscreen menu's tab (job KEY3's defect). It is retried on the next "
        + "register.";

    /// <summary>
    /// What was registered, and <b>what was installed to do it with</b> (job FUI23, R44 finding 3).
    ///
    /// <para>
    /// The old line stated the whole recipe unconditionally, on every successful register. The
    /// recipe is <c>GameInputBlocker.Mask</c>'s, and <c>Mask</c> only runs if the
    /// <c>GetConsumedInputs</c> detour resolved and applied — two documented, non-fatal failures
    /// (a game update that moves the method's address, a refused <c>CreateAndApply</c>) after which
    /// <c>Register</c> still returns <c>true</c>. In that state the player gets no
    /// <c>BlockAllKeys</c>, no Move/Aim block, no zoom block and, worst of all, <b>Escape not
    /// consumed</b> — one press closes the panel and opens the vanilla system menu, which is job
    /// FUI20's defect 1 verbatim. The line whose whole job is to report what was installed said
    /// the opposite.
    /// </para>
    ///
    /// <para>
    /// So it takes the fact instead of asserting it. <paramref name="maskLive"/> comes from
    /// <see cref="IUiInputBlocker.MaskLive"/>, observed on the same pass that did the register and
    /// outside <c>Gate</c> like every other blocker read. A log line that names a behavior has to
    /// be reporting something the code checked.
    /// </para>
    /// </summary>
    public static string RegisteredLog(bool maskLive) =>
        "Ui: input context registered order=" + Order
        + (maskLive
            ? " mask=live (BlockAllKeys + Move/Aim + Zoom; RotateCamera kept on keyboard+mouse; "
              + "Back/Menu_Close taken in every scope so Escape closes the panel without also "
              + "opening the game menu)"
            : " mask=absent (the GetConsumedInputs detour is not installed on this build, so the "
              + "context blocks camera rotation only: no BlockAllKeys, no Move/Aim, no Zoom, and "
              + "Escape is NOT consumed — one press closes the panel AND opens the game menu. See "
              + "the 'Ui: input mask not installed' line above for how to fix it.)");

    public static string RemovedLog => "Ui: input context removed order=" + Order;

    /// <summary>
    /// How many frames a refused <c>Register</c> is retried before the module stops trying. A
    /// server or main-menu process must not retry forever; a client that is mid-loading-screen
    /// gets a handful of frames to bring the world up.
    /// </summary>
    public const int MaxRegisterAttempts = 5;

    /// <summary>
    /// How many decide/act/fold passes one call may chain before the module stops and logs. A
    /// pass only chains when the wanted state changed while the game call was in flight, which
    /// takes an external <c>Acquire</c> / <c>Release</c> / <c>SetEnabled</c> each time, so the
    /// bound is generous — it exists so a blocker that flips the refcount from inside its own
    /// <c>Register</c> spins a bounded number of times instead of forever.
    /// </summary>
    private const int MaxChainedPasses = 8;

    /// <summary>
    /// How many times one panel-open may exhaust <see cref="MaxChainedPasses"/> before the module
    /// stops touching the game's list at all. The re-arm that R33 finding 11 added is what makes an
    /// exhaustion self-correcting, and it is also a way to spin forever: a blocker that flips the
    /// refcount from inside its own <c>Register</c> on every pass exhausts the chain, re-arms, and
    /// exhausts it again on the next frame — eight game calls and a warning line per frame, at frame
    /// rate, with no counter of its own (R34 finding 15). This is that counter. It is spent by
    /// consecutive exhaustions only: any pass that settles or lands refills it, and so does the next
    /// panel open (an <see cref="Acquire"/> that is <b>not</b> re-entered from inside a game call —
    /// a refcount change made from inside the blocker is the churn being bounded, not a new panel).
    /// </summary>
    private const int MaxChainExhaustions = 3;

    public static string DeferredLog(bool wanted) =>
        "Ui: input context " + (wanted ? "register" : "remove") + " order=" + Order
        + " deferred: InputActionSystem is inside a context callback (_DenyModificationsOfInputContexts).";

    /// <summary>
    /// Logged once when a change is requested off the game main thread. Mutating
    /// <c>InputActionSystem._InputContexts</c> from another thread races the game's own
    /// <c>OnUpdate</c> walk, and the deny flag is no defense: it is only set while the main
    /// thread is inside a context callback, so an off-thread caller reads <c>false</c> and
    /// proceeds straight into the list (R31 finding 23).
    /// </summary>
    public static string PostedLog(bool wanted) =>
        "Ui: input context " + (wanted ? "register" : "remove") + " order=" + Order
        + " posted to the game main thread: the caller is on another thread, and "
        + "_InputContexts must not be mutated off it (_DenyModificationsOfInputContexts only "
        + "guards main-thread callbacks).";

    /// <summary>
    /// The give-up line for <see cref="MaxRegisterAttempts"/> refused registers. It names the
    /// deny flag on purpose: that path is the unbounded one, so seeing this line means the
    /// failure was something else.
    /// </summary>
    public static string GaveUpLog =>
        "Ui: input context register order=" + Order + " gave up after " + MaxRegisterAttempts
        + " attempts; the panel stays shown but gameplay input is not captured until it is hidden "
        + "and shown again. This is not the _DenyModificationsOfInputContexts deferral (that one "
        + "waits without a limit and logs its own line) — the InputActionSystem or the Client "
        + "world was unreachable (main menu or server), or AddInputContext did not land.";

    /// <summary>
    /// Logged when a decision is superseded while its game call was in flight: a panel closed
    /// during the register, or a register arrived during the remove. The follow-up pass is what
    /// keeps the context from being leaked, so the line names both halves.
    /// </summary>
    public static string SupersededLog(bool wanted) =>
        "Ui: input context " + (wanted ? "register" : "remove") + " order=" + Order
        + " was superseded while the game call was in flight (the panel count changed); folding "
        + "the result and applying the current decision instead.";

    /// <summary>
    /// Guards module state only: the refcount, the enabled flag, the pending/pump bookkeeping,
    /// the retry budget and the mirrored registration. It is never held across a call into
    /// <see cref="IUiInputBlocker"/>.
    /// </summary>
    private static readonly object Gate = new();

    /// <summary>
    /// Serializes the game calls themselves, so a teardown on one thread cannot interleave its
    /// <c>Remove</c> with a <c>Register</c> on another.
    ///
    /// <para>
    /// <b>Lock order — one direction only: <see cref="CallGate"/> is never acquired while
    /// <see cref="Gate"/> is held.</b> The reverse is not forbidden and cannot be: the whole point
    /// of running the game call outside <c>Gate</c> is that a blocker may re-enter
    /// <see cref="Acquire"/> / <see cref="Release"/> from inside <c>Register</c>, and those take
    /// <c>Gate</c> underneath a held <c>CallGate</c> — routinely, on the path this module
    /// documents as expected. That is safe precisely because the order is never reversed, so no
    /// cycle can form; the earlier comment here claimed the stronger, symmetric rule, which was
    /// false in that direction and invited a "fix" that moved a lock (R33 finding 12).
    /// </para>
    ///
    /// <para>
    /// What an edit must preserve, then, is exactly this: every <c>lock (CallGate)</c> —
    /// <see cref="Execute"/> and <see cref="Uninstall"/> — is entered with <c>Gate</c> released,
    /// and nothing calls into <see cref="IUiInputBlocker"/> with <c>Gate</c> held.
    /// <c>A_blocker_that_re_enters_the_capture_from_another_thread_during_Register_does_not_deadlock</c>
    /// is the test that fails if either half is broken.
    /// </para>
    /// </summary>
    private static readonly object CallGate = new();

    private static IUiInputBlocker _blocker = NullUiInputBlocker.Instance;
    private static int _count;
    private static bool _enabled = true;

    /// <summary>
    /// Exclusive holds (<see cref="AcquireExclusive"/>): a framework mode, photo mode today, that owns
    /// the whole of gameplay input. Separate from <see cref="_count"/> (the shown-panel refcount) so
    /// panel bookkeeping is untouched, and not subject to <see cref="_enabled"/>
    /// (<c>Ui.CaptureInput(false)</c> is about panels; photo mode must still keep the character still).
    /// </summary>
    private static int _exclusive;

    /// <summary>
    /// P7b: close-key holds (<see cref="AcquireCloseKey"/>): something on screen that must take the panel close key
    /// (Escape / B, the game's Back and Menu_Close) and nothing else — the pick-in-world band while the pick is topmost.
    /// A hold registers the context like a shown panel, but it is not a panel the pointer routes: it is never a term of
    /// <see cref="UiMaskScope.Full"/>, so it masks <see cref="UiMaskScope.CloseKeyOnly"/> wherever the pointer is, even
    /// with no readable pointer (a controller player). Separate from <see cref="_count"/> so the panel bookkeeping and
    /// the pointer gate are untouched.
    /// </summary>
    private static int _closeKey;

    /// <summary>
    /// The pointer gate (job FUI19), driven by <see cref="UiHoverGate"/> when a
    /// <see cref="UiCaptureMode.WhenHovered"/> panel is shown.
    ///
    /// <para>
    /// <b>Job FUI20 moved what this decides.</b> It used to be the third term of
    /// <see cref="WantedLocked"/> — the pointer leaving a panel <i>removed the context from the
    /// game's list</i>. That is why one Escape press both closed the mod panel and opened the
    /// vanilla system menu: with nothing registered there was nothing to take the key with, and
    /// Escape has to close a panel from anywhere, not only from inside its frame. So registration
    /// now follows the panel and this gate selects the <see cref="UiMaskScope"/> the registered
    /// context applies: <see cref="UiMaskScope.Full"/> (chat's whole recipe) while the pointer is
    /// over a panel, <see cref="UiMaskScope.CloseKeyOnly"/> (two actions and nothing else) while
    /// it is not. See <see cref="InputMaskPlan"/> for the metadata evidence behind that.
    /// </para>
    ///
    /// <para>
    /// <b>The safety argument survives the move, and gets simpler.</b> It used to rest on the gate
    /// being ANDed rather than ORed into <see cref="WantedLocked"/>. It now rests on the gate not
    /// being in <see cref="WantedLocked"/> at all: the block is wanted exactly while
    /// <c>_enabled &amp;&amp; _count &gt; 0</c>, so a gate stuck in either position — a throw in
    /// the pump, a frame handler that stopped being called, a Core swap mid-decision — cannot
    /// affect whether a context is registered, only how much it masks while it is. The outcome
    /// this class exists to prevent (a list entry with no owner) stays unreachable from the new
    /// code by construction rather than by care, and a pointer crossing a panel edge now changes a
    /// flag instead of issuing an <c>AddInputContext</c> / <c>RemoveInputContext</c> pair.
    /// </para>
    ///
    /// <para>
    /// <b>Default <c>true</c>, and reset to <c>true</c> on every 1→0.</b> A process with no
    /// pointer source — a server, a test, the frames before the mouse device exists — never drives
    /// this and therefore masks exactly what it did before FUI19. Resetting it when the last panel
    /// closes means the next panel open starts fail-closed instead of inheriting whatever the
    /// pointer happened to be doing when the previous one was hidden.
    /// </para>
    ///
    /// <para>
    /// <b>Written in exactly one place (job FUI23, R44 finding 1).</b> This field and
    /// <c>GameInputBlocker._scope</c> are two copies of the same answer, and only one of them is
    /// the one the game reads. <see cref="Release"/> used to write this one directly on the last
    /// 1→0 without telling the blocker, so a panel closed with the pointer off it left the module
    /// saying <see cref="UiMaskScope.Full"/> and the blocker still applying
    /// <see cref="UiMaskScope.CloseKeyOnly"/> — and the next panel opened <i>under</i> the pointer
    /// could not repair it, because the router had already reset to "blocked" and reported no
    /// transition. The player got evidence 167's wheel back: every notch scrolled the list and
    /// zoomed the camera. <see cref="WritePointerBlockLocked"/> is now the only writer, and it
    /// returns the push its caller owes the blocker, so the two copies cannot be changed
    /// independently. <c>grep -n "_pointerBlock ="</c> must return exactly one line, and
    /// <c>UiInputMaskTests.The_pointer_flag_has_exactly_one_writer</c> is that grep.
    /// </para>
    ///
    /// <para>
    /// <b>This flag is not the scope</b> (job FUI27, R45 finding 8). It is one of three terms
    /// <see cref="EffectiveScopeLocked"/> reads; the other two are the ones that say there is a
    /// panel to be masked at all. Read that summary before adding a fourth writer or a fourth term.
    /// </para>
    /// </summary>
    private static bool _pointerBlock = true;
    private static bool _pending;
    private static bool _pumpArmed;
    private static bool _posted;
    private static int _registerAttempts;

    /// <summary>
    /// What we last observed of <c>IUiInputBlocker.IsRegistered</c>. The decision is taken from
    /// this mirror rather than from a live read, because a live read is a game call and would
    /// have to happen under <c>Gate</c>. Every <see cref="Execute"/> re-observes the blocker and
    /// <see cref="FoldLocked"/> writes the observation back, so the mirror is corrected on every
    /// pass that touches the game.
    /// </summary>
    private static bool _registered;

    /// <summary>True while a game call is running outside <c>Gate</c>.</summary>
    private static bool _inFlight;

    /// <summary>
    /// Set when <see cref="MaxRegisterAttempts"/> refusals exhausted the budget. It suppresses
    /// further register decisions until something external happens, and it — not a pinned
    /// attempt count — is what makes the give-up sticky. The counter itself is reset with it, so
    /// the next <c>Acquire</c> / <c>Release</c> / <c>SetEnabled</c> gets a fresh budget of five
    /// rather than the single attempt R32 finding 13 described.
    /// </summary>
    private static bool _gaveUp;

    /// <summary>
    /// Consecutive <see cref="MaxChainedPasses"/> exhaustions, and the latch that ends them. See
    /// <see cref="MaxChainExhaustions"/>: unlike <see cref="_gaveUp"/> this one covers the remove
    /// direction as well, because the state an exhausted chain leaves can be either.
    /// </summary>
    private static int _chainExhaustions;

    private static bool _chainGaveUp;

    /// <summary>Live refcount of shown panels that asked for input capture.</summary>
    public static int Count
    {
        get { lock (Gate) return _count; }
    }

    /// <summary>False after <c>Ui.CaptureInput(false)</c>: panels open without taking input.</summary>
    public static bool Enabled
    {
        get { lock (Gate) return _enabled; }
    }

    /// <summary>
    /// True while our context sits in the game's list, as of the last observation. A mirror, not
    /// a live read: <c>Ui.IsCapturingInput</c> and <c>Ui.DescribePins</c> call this, and neither
    /// should reach into <c>InputActionSystem</c> from whatever thread it happens to be on.
    /// </summary>
    public static bool IsRegistered
    {
        get { lock (Gate) return _registered; }
    }

    /// <summary>True while a register/remove is waiting for the deny flag to clear.</summary>
    public static bool HasPendingChange
    {
        get { lock (Gate) return _pending; }
    }

    /// <summary>
    /// Whether a context <b>should</b> be registered right now: enabled, and at least one panel
    /// shown. The wanted state, not the observed one — unlike <see cref="IsRegistered"/> this does
    /// not lag a deferred game call.
    ///
    /// <para>
    /// <b>It does not consult the pointer gate</b> (job FUI20 moved that term out of
    /// <see cref="WantedLocked"/>, and job FUI23 corrected this comment, which went on asserting
    /// it — R44 finding 2). Registration follows the panel so that Escape can be taken from
    /// anywhere; how much the registered context masks follows the pointer. A caller asking "may
    /// this keybind fire" wants the pointer term and therefore wants
    /// <see cref="IsFullyBlocking"/>, not this.
    /// </para>
    /// </summary>
    public static bool IsBlocking
    {
        get { lock (Gate) return WantedLocked(); }
    }

    /// <summary>
    /// The <i>effective</i> block: enabled, a panel shown, <b>and</b> the pointer over one of them
    /// (job FUI23, R44 finding 2). This is the question "is the player interacting with a mod
    /// panel right now", which is what a keybind has to answer before suppressing itself.
    ///
    /// <para>
    /// <b>Why a second property rather than a term added back to <see cref="IsBlocking"/>.</b>
    /// FUI19 made <see cref="_pointerBlock"/> the third term of <see cref="WantedLocked"/> and
    /// pointed <c>Keybinds</c> at it; FUI20 moved the term out — correctly, so that a panel the
    /// pointer is not over still consumes Escape — and the one caller whose entire justification
    /// was that term kept reading the property it had been removed from. The rename that followed
    /// read like a fix and changed nothing. Registration and suppression are two different
    /// questions about one flag, so they are two named properties over the same fields rather than
    /// one property two callers disagree about.
    /// </para>
    ///
    /// <para>
    /// Same terms the mask scope is derived from, so a panel open in one corner while the player
    /// is looking at the world does not suppress their binds, and a hovered panel — or any
    /// <see cref="UiCaptureMode.WhileShown"/> panel, which holds the gate closed wherever the
    /// pointer is — still does.
    /// </para>
    /// </summary>
    public static bool IsFullyBlocking
    {
        get { lock (Gate) return (_enabled && _count > 0 && _pointerBlock) || _exclusive > 0; }
    }

    /// <summary>Live exclusive holds (<see cref="AcquireExclusive"/>).</summary>
    public static int ExclusiveCount
    {
        get { lock (Gate) return _exclusive; }
    }

    /// <summary>
    /// A framework mode takes the whole of gameplay input (photo mode): registers the context like a
    /// shown panel would and masks <see cref="UiMaskScope.Exclusive"/> wherever the pointer is. Pair
    /// with <see cref="ReleaseExclusive"/>; <see cref="Uninstall"/> drops every hold.
    /// </summary>
    public static void AcquireExclusive()
    {
        ScopePush push;
        lock (Gate)
        {
            _exclusive++;
            NudgeLocked(freshOpen: _exclusive == 1 && _count == 0 && !_inFlight);
            push = ScopeLocked();
        }

        push.Send();
        Apply();
    }

    /// <summary>Live close-key holds (<see cref="AcquireCloseKey"/>).</summary>
    public static int CloseKeyCount
    {
        get { lock (Gate) return _closeKey; }
    }

    /// <summary>
    /// P7b: take the panel close key and nothing else: registers the context (as a shown panel would) while at least one
    /// hold is live, masking <see cref="UiMaskScope.CloseKeyOnly"/> unless a shown panel or an exclusive hold asks for
    /// more. Gameplay input (movement, aim, zoom, every other key) is never blocked by a hold. Pair with
    /// <see cref="ReleaseCloseKey"/>; <see cref="Uninstall"/> drops every hold.
    /// </summary>
    public static void AcquireCloseKey()
    {
        ScopePush push;
        lock (Gate)
        {
            _closeKey++;
            NudgeLocked(freshOpen: _closeKey == 1 && _count == 0 && _exclusive == 0 && !_inFlight);
            push = ScopeLocked();
        }

        push.Send();
        Apply();
    }

    /// <summary>End one close-key hold. Never goes below zero. The last one removes the context unless a panel still wants it.</summary>
    public static void ReleaseCloseKey()
    {
        ScopePush push;
        lock (Gate)
        {
            if (_closeKey == 0) return;
            _closeKey--;
            push = ScopeLocked();
            NudgeLocked();
        }

        push.Send();
        Apply();
    }

    /// <summary>End one exclusive hold. Never goes below zero. The last one hands the scope back to the panels (or removes the context).</summary>
    public static void ReleaseExclusive()
    {
        ScopePush push;
        lock (Gate)
        {
            if (_exclusive == 0) return;
            _exclusive--;
            push = ScopeLocked();
            NudgeLocked();
        }

        push.Send();
        Apply();
    }

    /// <summary>True while the pointer gate allows the full mask. See <see cref="_pointerBlock"/>.</summary>
    public static bool PointerBlock
    {
        get { lock (Gate) return _pointerBlock; }
    }

    /// <summary>
    /// How much a registered context masks right now — read from the <b>blocker</b>, which is the
    /// copy the game's input path actually consults (job FUI23, R44 finding 4). Independent of
    /// <see cref="IsRegistered"/>: this says what would be blocked, not whether anything is.
    ///
    /// <para>
    /// It used to report <see cref="_pointerBlock"/>, i.e. the module's own decision, which by
    /// construction always agreed with itself. That made R44 finding 1 invisible to the only
    /// instrument aimed at it: in the desynced state the pin dump printed <c>scope=Full</c> while
    /// the native mask was applying <see cref="UiMaskScope.CloseKeyOnly"/>. The decision is still
    /// readable as <see cref="WantedMaskScope"/>, and <see cref="Describe"/> prints both and says
    /// so when they differ.
    /// </para>
    ///
    /// <para>
    /// The blocker is read with <c>Gate</c> released, like every other call into it. A blocker that
    /// throws from a field read is answered with the module's own decision and a warning, because a
    /// diagnostic that throws is worse than one that degrades.
    /// </para>
    /// </summary>
    public static UiMaskScope MaskScope
    {
        get
        {
            TryGetAppliedScope(out UiMaskScope scope);
            return scope;
        }
    }

    /// <summary>
    /// The blocker's applied scope, and <b>whether it could be read at all</b> (job FUI27,
    /// R45 finding 21).
    ///
    /// <para>
    /// <see cref="MaskScope"/> degrades to <see cref="WantedMaskScope"/> when the blocker throws,
    /// which is right for a caller that needs an answer and wrong for the one diagnostic whose
    /// entire job is to say whether the two copies agree: <see cref="Describe"/> then printed
    /// <c>scope=Full wanted=Full</c> and no <c>DESYNC</c> — the second value wearing the first
    /// value's label — in the one state where nothing whatever is known about the copy the game
    /// reads. "I could not tell" is not "it is false", the same distinction this module already
    /// draws for <see cref="PointerKind"/>. So the fallible read is a <c>Try</c> and the caller
    /// that must not conflate them asks for the bool.
    /// </para>
    ///
    /// <para>
    /// Returns <c>false</c> and hands back the module's own decision when the read threw, so a
    /// caller that ignores the bool behaves exactly as <see cref="MaskScope"/> always has.
    /// </para>
    /// </summary>
    public static bool TryGetAppliedScope(out UiMaskScope scope)
    {
        IUiInputBlocker blocker;
        lock (Gate) blocker = _blocker;

        try
        {
            scope = blocker.AppliedScope;
            return true;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: input mask scope read threw: " + e.Message);
            scope = WantedMaskScope;
            return false;
        }
    }

    /// <summary>
    /// The scope the module has <i>decided</i> the blocker should be applying. The intention;
    /// <see cref="MaskScope"/> is the measurement. They are equal in every healthy state, which is
    /// exactly why both are printed.
    ///
    /// <para>
    /// <b>It is not the pointer gate alone</b> (job FUI27, R45 finding 8) — see
    /// <see cref="EffectiveScopeLocked"/>. A module that decided <see cref="UiMaskScope.Full"/>
    /// while no panel is open would be deciding to mask a player who has nothing on screen.
    /// <see cref="PointerBlock"/> is still printed separately, so the pin line keeps the raw gate
    /// answer next to the scope derived from it.
    /// </para>
    /// </summary>
    public static UiMaskScope WantedMaskScope
    {
        get { lock (Gate) return EffectiveScopeLocked(); }
    }

    /// <summary>
    /// The pointer crossed a panel edge (job FUI19). Called only on a transition — see
    /// <see cref="HoverRouter.Update"/> for why per-frame calls into this module would be
    /// unacceptable — and idempotent anyway, so a caller that flips it back to the value it
    /// already had makes no call at all.
    ///
    /// <para>
    /// Job FUI20: this no longer changes whether the context is registered, so it issues no
    /// <c>AddInputContext</c> / <c>RemoveInputContext</c> and needs neither the retry budget nor a
    /// deferral. It hands the blocker a new <see cref="UiMaskScope"/> — the rule every other
    /// method here follows applies unchanged: the decision is taken under <c>Gate</c> and the call
    /// into the blocker is made with no lock held.
    /// </para>
    /// </summary>
    public static void SetPointerBlock(bool blocked) => WritePointerBlock(blocked);

    /// <summary>
    /// The one writer of <see cref="_pointerBlock"/>, and the one place a scope push is decided.
    /// Takes the field under <c>Gate</c> and hands the push back to be made outside it, which is
    /// the shape every other blocker call in this file has.
    ///
    /// <para>
    /// Job FUI23 (R44 finding 1). Before this there were three writers — <see cref="Release"/>,
    /// <see cref="Uninstall"/> and <c>SetPointerBlock</c> — and only the last one told the blocker.
    /// Making the write and the push one operation is what stops the two copies of the pointer
    /// answer from being changed independently; the alternative (remembering to push at each of
    /// three sites) is what produced the defect.
    /// </para>
    /// </summary>
    /// <param name="force">
    /// Skip <see cref="ScopePush.Send"/>'s redundancy check and tell the blocker whatever it
    /// already thinks (job FUI28). Exactly one caller passes it — see <see cref="Use"/>.
    /// </param>
    private static ScopePush WritePointerBlockLocked(bool blocked, bool force = false)
    {
        _pointerBlock = blocked;
        return ScopeLocked(force);
    }

    /// <summary>
    /// The scope the blocker should be applying right now, from <b>every</b> term that decides it,
    /// and the only place <see cref="InputMaskPlan.ScopeFor"/> is called in this file.
    ///
    /// <para>
    /// <b>Why the wanted state is a term (job FUI27, R45 finding 8).</b> It used to be
    /// <c>ScopeFor(_pointerBlock)</c>, so <see cref="Release"/>'s fail-closed reset of the pointer
    /// gate on the last 1→0 pushed <see cref="UiMaskScope.Full"/> at the exact moment the last
    /// panel was closing — before <see cref="Apply"/> had run the removal.
    /// <c>GameInputBlocker.SetMaskScope</c> writes <c>_BlockRotateCamera</c> on the context object
    /// from that scope, and only the subsequent <c>Remove()</c> clears it. A removal that is
    /// deferred (the deny flag: <see cref="Execute"/> returns <see cref="Result.Deferred"/> without
    /// calling <c>Remove</c> at all, and <see cref="GaveUpLog"/> says that path waits without a
    /// limit) or refused (<see cref="FoldRefusedRemoveLocked"/> retries unbounded) therefore left a
    /// player with <b>no panel open</b> carrying <c>BlockAllKeys</c>, blocked Move/Aim, blocked
    /// zoom and a persistent camera-rotation flag. Before that reset the same stuck removal left
    /// the blocker at <see cref="UiMaskScope.CloseKeyOnly"/> and cost the player Escape and nothing
    /// else.
    /// </para>
    ///
    /// <para>
    /// <b>Moving the push to <see cref="Acquire"/> would not have fixed it.</b> That was the
    /// review's suggested smallest fix, and the hover gate defeats it one call later:
    /// <c>UiHandles</c> calls <see cref="Release"/> and then <c>UiHoverGate.Detach</c>, whose
    /// <c>Refresh</c> sees an empty shown set and calls
    /// <see cref="SetPointerBlock"/><c>(true)</c> — pushing <see cref="UiMaskScope.Full"/> into
    /// exactly the same window from a different file. Any fix that lives in one caller is a fix
    /// one caller can walk around, so this is a property of the derivation instead: every scope
    /// any caller can push is <see cref="InputMaskPlan.ScopeFor"/> of the wanted state, so no
    /// caller can <i>decide</i> <see cref="UiMaskScope.Full"/> while nothing is wanted, whoever
    /// makes the push.
    /// </para>
    ///
    /// <para>
    /// <b>How much "and in whatever order" was worth, stated exactly</b> (job FUI28, R46 finding
    /// 5). That sentence used to end "whoever makes it and in whatever order", which is a claim
    /// about <i>delivery</i> that this file does not make good on and does not try to. A push is
    /// decided under <c>Gate</c> and sent outside it (<see cref="ScopePush"/>) on purpose: a
    /// blocker's <c>Dispose</c> can pump or wait, so a game call made under <c>Gate</c> is the
    /// deadlock R32 finding 13 closed. What holds is therefore a property of every <b>sequential</b>
    /// order: for any single-threaded interleaving of the mutators, the last push sent is the scope
    /// the module decided, and no decided push is ever <see cref="UiMaskScope.Full"/> with nothing
    /// wanted. Two threads can still deliver two pushes in the reverse of the order they were
    /// decided in — A decides <see cref="UiMaskScope.Full"/> under <c>Gate</c> and releases it, B
    /// decides and sends <see cref="UiMaskScope.CloseKeyOnly"/>, A then sends its Full — and the
    /// blocker keeps that stale Full until something pushes again, which nothing is obliged to do.
    /// Not hypothetical by construction: <c>Ui.CaptureInput</c> and <c>IUiHandle.Show</c>/
    /// <c>Hide</c> are public, and <see cref="DecideLocked"/> already <i>posts</i> an off-thread
    /// caller's register rather than refusing it (R31 finding 23), so the module contemplates the
    /// caller it cannot order. Not fixed here because the fix is a delivery order — a stamp taken
    /// under <c>Gate</c> and honored where the call is made, or a threading contract for the whole
    /// module — and a sixth latch in this file for a race no shipped caller makes is worth less
    /// than the sentence above being true. It is recorded as owed in the internal research notes
    /// instead.
    /// </para>
    ///
    /// <para>
    /// The pointer gate's own fail-closed reset survives unchanged — <see cref="Release"/> still
    /// sets <see cref="_pointerBlock"/> back to <c>true</c> on the last 1→0, so the next
    /// <see cref="Acquire"/> starts from a known state rather than inheriting where the mouse was
    /// left. What changed is only that the <i>scope pushed for it</i> is read through the wanted
    /// state, so the reset means <see cref="UiMaskScope.Full"/> from the moment a panel exists to
    /// be masked and not a moment earlier.
    /// </para>
    ///
    /// <para>
    /// <b>What an edit must preserve.</b> Because the scope now depends on <c>_count</c> and
    /// <c>_enabled</c> as well as <c>_pointerBlock</c>, every mutation of any of the three owes the
    /// blocker a push — <see cref="Acquire"/>, <see cref="Release"/>, <see cref="SetEnabled"/>,
    /// <see cref="SetPointerBlock"/>, <see cref="Use"/> and <see cref="Uninstall"/> all end in one.
    /// That is not left to a grep: <c>UiInputMaskTests.Every_reachable_sequence_leaves_the_blocker
    /// _at_the_scope_the_module_decided</c> walks every sequence of those calls to depth five and
    /// fails on the first one that drifts.
    /// </para>
    ///
    /// <para>
    /// <b>And it walks all six now</b> (job FUI28, R46 finding 5). The paragraph above named six
    /// mutators while the walk's op list held four of them: <see cref="Use"/> appeared only as the
    /// harness's setup and <see cref="Uninstall"/> appeared nowhere in the suite except an
    /// off-thread teardown test that asserts nothing about the scope — so this doc claimed a proof
    /// for two pushes that nothing checked, which is the disease the batch it shipped in was about.
    /// Both are ops in the walk now, each installing a <b>fresh</b> blocker afterwards, which is
    /// also the only way either push is observable: a new blocker's own field starts wherever its
    /// constructor left it, and correcting that is what <see cref="Use"/>'s push is for.
    /// </para>
    /// </summary>
    private static UiMaskScope EffectiveScopeLocked() =>
        _exclusive > 0 ? UiMaskScope.Exclusive : PanelScopeLocked();

    /// <summary>
    /// The panels' scope: the pointer gate and the wanted state (the one ScopeFor derivation). Only shown panels can
    /// ask for <see cref="UiMaskScope.Full"/>; a close-key hold (<see cref="AcquireCloseKey"/>) registers the context
    /// but is never a term of it. Without holds this is exactly <c>ScopeFor(_pointerBlock &amp;&amp; WantedLocked())</c>
    /// as before (the exclusive term is decided one level up).
    /// </summary>
    private static UiMaskScope PanelScopeLocked() =>
        InputMaskPlan.ScopeFor(_pointerBlock && PanelsWantedLocked());

    /// <summary>The push owed to the current blocker for the current state. See <see cref="ScopePush"/>.</summary>
    private static ScopePush ScopeLocked(bool force = false) =>
        new(_blocker, EffectiveScopeLocked(), force);

    /// <summary>Take <c>Gate</c>, write the flag, release it, push. The common case.</summary>
    private static void WritePointerBlock(bool blocked)
    {
        ScopePush push;
        lock (Gate) push = WritePointerBlockLocked(blocked);
        push.Send();
    }

    /// <summary>
    /// A scope owed to a blocker: decided under <c>Gate</c>, sent outside it. A struct rather than
    /// two locals so that the pairing cannot be split up by a later edit.
    /// </summary>
    private readonly struct ScopePush
    {
        private readonly IUiInputBlocker _target;
        private readonly UiMaskScope _scope;
        private readonly bool _force;

        internal ScopePush(IUiInputBlocker target, UiMaskScope scope, bool force = false)
        {
            _target = target;
            _scope = scope;
            _force = force;
        }

        /// <summary>
        /// Hand the blocker the scope it should be masking at, unless it is already there. Never
        /// throws: a blocker that fails to take a scope keeps the one it had, which is at worst the
        /// pre-FUI19 full mask and never a panel that cannot be closed.
        ///
        /// <para>
        /// <b>The redundancy check reads the blocker, not the module's copy of it</b> (job FUI23).
        /// <c>SetPointerBlock</c> used to return early when <c>_pointerBlock</c> already held the
        /// requested value — which is correct only while the two copies agree, and is precisely
        /// what stopped the desynced blocker from ever being repaired: the module thought it had
        /// already said this. Asking the blocker what it is applying makes every push
        /// self-correcting, and still costs no game call when nothing changed, because
        /// <see cref="IUiInputBlocker.AppliedScope"/> is a field read by contract.
        /// </para>
        ///
        /// <para>
        /// <b>And it is skipped for a blocker that has never been told anything</b> (job FUI28,
        /// found by extending R46 finding 5's walk to cover <see cref="Use"/>). "Already there" is
        /// a claim about the object being pushed to, and <c>AppliedScope</c> cannot make it for a
        /// fresh one: <c>GameInputBlocker</c>'s backing <c>_scope</c> is <b>static</b>
        /// (<c>GameInputBlocker.cs:147</c>, a volatile int the native callback reads once per frame)
        /// while the persistent camera flag it drives, <c>_cameraBlocked</c>, is per instance. So a
        /// blocker constructed mid-session answers <see cref="UiMaskScope.Full"/> — the value the
        /// previous instance left, or the field's initializer — with its own camera flag false and
        /// its context never written, and a redundancy check that believes it returns without
        /// making the one call <see cref="Use"/> exists to make. The exhaustive walk fails on
        /// exactly that state. It is out of reach on the shipped path, where <c>Ui.Install</c>
        /// installs a blocker with no panel open so the wanted scope differs from the static
        /// anyway, and it is fixed rather than argued about because the alternative is a comment
        /// two methods apart claiming a self-correction that does not happen.
        /// </para>
        /// </summary>
        internal void Send()
        {
            // A forced push asks the blocker nothing, because there is nothing it could usefully
            // answer: see the class doc.
            if (!_force)
            {
                try
                {
                    if (_target.AppliedScope == _scope) return;
                }
                catch (Exception e)
                {
                    // Unreadable is not "already correct": push anyway.
                    Plugin.Logger?.LogWarning("Ui: input mask scope read threw: " + e.Message);
                }
            }

            try { _target.SetMaskScope(_scope); }
            catch (Exception e)
            {
                Plugin.Logger?.LogWarning("Ui: input mask scope " + _scope + " threw: " + e.Message);
            }
        }
    }

    /// <summary>
    /// CHAT1: true while a real blocker is installed (<c>Ui.Install</c> ran on a client and has not been
    /// uninstalled). The chat click guard arms the shared mask detour only then, because only the real
    /// blocker's teardown releases that detour when Core unloads.
    /// </summary>
    internal static bool HasBlocker
    {
        get { lock (Gate) return !ReferenceEquals(_blocker, NullUiInputBlocker.Instance); }
    }

    /// <summary>Point at the process blocker. <c>Ui.Install</c> passes the real one on a client.</summary>
    public static void Use(IUiInputBlocker? blocker)
    {
        ScopePush push;
        lock (Gate)
        {
            _blocker = blocker ?? NullUiInputBlocker.Instance;
            // A freshly installed blocker owns nothing yet (both NullUiInputBlocker and
            // GameInputBlocker start unregistered), and an in-flight fold against the old one is
            // discarded by the blocker-identity re-check in FoldLocked.
            _registered = false;
            _registerAttempts = 0;
            _gaveUp = false;
            _chainExhaustions = 0;
            _chainGaveUp = false;

            // Job FUI20: a blocker installed mid-session must not start at whatever scope its own
            // field happened to default to. Re-stating the flag we already hold is what tells it.
            //
            // Job FUI28: forced, because until now it did not tell it. ScopePush.Send asks the
            // blocker what it is applying and returns if it matches, and GameInputBlocker's
            // AppliedScope reads a STATIC field (_scope, :147) while the persistent camera flag it
            // drives is per instance — so a blocker constructed while a panel is open, enabled and
            // hovered answers Full, matches, and is handed nothing at all, which is the one case
            // this line exists for. This is the only forced push in the file: everywhere else the
            // blocker has already been told by this module and the check is exactly right.
            push = WritePointerBlockLocked(_pointerBlock, force: true);
        }

        // Told outside the lock, like every other blocker call.
        push.Send();
    }

    /// <summary>
    /// Global opt-out. <c>true</c> (the default) means a plugin gets input capture for free the
    /// moment a panel is shown; <c>false</c> removes the context now and keeps it off while
    /// panels open, for a HUD overlay that must not eat gameplay input.
    /// </summary>
    public static void SetEnabled(bool enabled)
    {
        ScopePush push;
        lock (Gate)
        {
            if (_enabled == enabled) return;
            _enabled = enabled;
            NudgeLocked(freshOpen: true);

            // _enabled is a term of the scope (EffectiveScopeLocked), so the opt-out lowers the
            // mask to CloseKeyOnly before Apply tries to take the context out — and raising it
            // again restores the mask before the register.
            push = ScopeLocked();
        }

        push.Send();
        Apply();
    }

    /// <summary>
    /// 0 → 1 registers the context. Later panels only raise the count.
    ///
    /// <para>
    /// Job FUI27: the push happens here, before <see cref="Apply"/>, so the blocker is told
    /// <see cref="UiMaskScope.Full"/> when a panel <i>opens</i> — <c>GameInputBlocker.Register</c>
    /// reads the scope to set the context's persistent camera flag, so the scope has to be current
    /// before the register rather than after it. See <see cref="EffectiveScopeLocked"/> for why the
    /// other end of this (the last close) may not push <see cref="UiMaskScope.Full"/> at all.
    /// </para>
    /// </summary>
    public static void Acquire()
    {
        ScopePush push;
        lock (Gate)
        {
            _count++;
            // A 0 → 1 that is not re-entered from inside a game call is a new panel opening, which
            // is what refills the chain budget (MaxChainExhaustions).
            NudgeLocked(freshOpen: _count == 1 && !_inFlight);
            push = ScopeLocked();
        }

        push.Send();
        Apply();
    }

    /// <summary>
    /// 1 → 0 removes the context. Never goes below zero.
    ///
    /// <para>
    /// The last panel closing also resets the pointer gate, and job FUI23 made that reset go
    /// through <see cref="WritePointerBlockLocked"/> like every other write (R44 finding 1). It
    /// used to set the field alone: closing a panel with the mouse off it therefore left the
    /// blocker masking <see cref="UiMaskScope.CloseKeyOnly"/> while the module believed
    /// <see cref="UiMaskScope.Full"/>, and the next open <i>under</i> the pointer produced no
    /// router transition to repair it — so the panel was hovered, the mask was believed on, and
    /// none of it was applied.
    /// </para>
    ///
    /// <para>
    /// Job FUI27 (R45 finding 8) left that reset exactly where it was and changed what it pushes.
    /// The push is now unconditional — a decrement that does not reach zero is a no-op at the
    /// blocker, because <see cref="ScopePush.Send"/> asks it what it is applying first — and the
    /// scope it carries is <see cref="EffectiveScopeLocked"/>, which reads the wanted state. So the
    /// last close hands the blocker <see cref="UiMaskScope.CloseKeyOnly"/> and the removal window
    /// that follows is masked no harder than it was before any of this existed, whether the removal
    /// lands on this pass, is deferred by the deny flag, or is refused for as long as the client
    /// runs.
    /// </para>
    /// </summary>
    public static void Release()
    {
        ScopePush push;
        lock (Gate)
        {
            _count--;
            if (_count < 0) _count = 0;
            // The last panel closed: the pointer gate goes back to its fail-closed default so the
            // next open starts from a known state rather than from wherever the mouse was left —
            // and the blocker is told, or "the next open starts fail-closed" is true of one copy
            // of the answer and false of the one the game reads.
            //
            // The write is unconditional in intent and conditional in fact: below zero panels there
            // is no gate answer to keep, and above zero the gate still belongs to the panels that
            // are left. What is NOT conditional is the push, so the count change itself reaches the
            // blocker on every call (EffectiveScopeLocked reads _count).
            push = _count == 0 ? WritePointerBlockLocked(true) : ScopeLocked();

            NudgeLocked();
        }

        push.Send();

        Apply();
    }

    /// <summary>
    /// Core Uninstall / plugin unload: drop the count and take the context out, whatever the
    /// refcount said. A deferred removal is retried here rather than left to a pump that is
    /// about to be unsubscribed.
    ///
    /// <para>
    /// The state reset and the blocker hand-off happen under <c>Gate</c>; the three game calls
    /// (<c>IsRegistered</c>, <c>Remove</c>, <c>Dispose</c>) run after it is released, on
    /// <c>CallGate</c>, so a blocker whose <c>Dispose</c> pumps or waits cannot deadlock against
    /// a concurrent <c>Acquire</c> (R32 finding 13). Swapping the field to
    /// <c>NullUiInputBlocker</c> before the calls is what makes that safe: any pass that starts
    /// meanwhile decides against the null blocker and does nothing.
    /// </para>
    /// </summary>
    public static void Uninstall()
    {
        IUiInputBlocker doomed;
        bool offThread;
        ScopePush push;
        lock (Gate)
        {
            _count = 0;
            _exclusive = 0;
            _closeKey = 0;
            _pending = false;
            _registerAttempts = 0;
            _gaveUp = false;
            _chainExhaustions = 0;
            _chainGaveUp = false;
            _registered = false;

            // Job FUI23: the same door as everywhere else, while _blocker is still the doomed one.
            // This used to write the field alone and be right by luck — Dispose resets the mask
            // scope on its way out — which is finding 1's hazard seen and handled one method away
            // from the place it was not.
            push = WritePointerBlockLocked(true);
            DisarmPumpLocked();

            doomed = _blocker;
            _blocker = NullUiInputBlocker.Instance;
            _enabled = true;
            offThread = !OnMainThread;
        }

        push.Send();

        // Teardown is the one mutation that cannot be posted: the Core generation is going
        // away and a queued removal would be dropped with it, leaving a context in the list
        // with no owner. So it runs here and says so.
        //
        // Job FLATCH2 (R45 finding 19). The line used to name RemoveInputContext only, which is
        // the mild half. The Dispose below unwinds GameInputBlocker's native detour on
        // RotateCameraBlocker.GetConsumedInputs — a method the game's main thread is inside once
        // per frame per registered context — and unpatching native code from another thread is a
        // use-after-free, which in this process is a hard crash with no managed stack, not a
        // mutated list. Neither posting the Dispose nor skipping it is available (see
        // GameInputBlocker.UninstallMask for why both are worse), so the hazard is named where the
        // decision is taken rather than left silent. In this build the only production caller that
        // reaches here off the main thread would be BepInEx's own BasePlugin.Unload: the Core swap
        // cascade runs on Bootstrap's FrameTicker tick, which is the main thread by construction.
        if (offThread)
            Plugin.Logger?.LogWarning(
                "Ui: input context teardown is running off the game main thread and cannot be "
                + "deferred (the Core generation is going away), so RemoveInputContext is called "
                + "from here, AND the blocker's Dispose runs from here. If the input mask's native "
                + "detour on GetConsumedInputs is owned by this Core generation, that Dispose "
                + "unpatches it, and if the main thread is inside it the process crashes in "
                + "native code with no managed stack. Job FLATCH3 made the normal case a "
                + "Bootstrap-owned process-lifetime detour that is never disposed at all; the "
                + "blocker's own 'input mask released' / 'input mask installed ... owner=' lines "
                + "say which of the two this build got. Call Ui.Uninstall / plugin unload on the "
                + "main thread either way.");

        lock (CallGate)
        {
            bool registered;
            try { registered = doomed.IsRegistered; }
            catch (Exception e)
            {
                Plugin.Logger?.LogWarning("Ui: input context IsRegistered threw: " + e.Message);
                registered = true;
            }

            if (registered) ForceRemove(doomed);

            try { doomed.Dispose(); }
            catch (Exception e) { Plugin.Logger?.LogWarning("Ui: input blocker dispose threw: " + e.Message); }
        }
    }

    /// <summary>
    /// Next-frame retry for a change the deny flag refused. Subscribed to <c>Plugin.Frame</c>
    /// only while something is pending, so a quiet client pays nothing.
    /// </summary>
    internal static void Pump()
    {
        lock (Gate)
        {
            if (!_pending)
            {
                DisarmPumpLocked();
                return;
            }
        }

        Apply();
    }

    /// <summary>
    /// One grep line for <c>Ui.DescribePins</c> / the pin diagnostic.
    ///
    /// <para>
    /// <c>scope=</c> is the blocker's <see cref="MaskScope"/> — what is actually being masked — and
    /// it is printed next to <c>wanted=</c>, the module's own decision. They agree in every healthy
    /// state; when they do not, the line says <c>DESYNC</c> and names both, because a diagnostic
    /// that reports only the copy which cannot be wrong is the disease R44 findings 3 and 4
    /// describe, not an instrument for it (job FUI23).
    /// </para>
    ///
    /// <para>
    /// <b>And an unreadable scope is reported as unreadable</b> (job FUI27, R45 finding 21). The
    /// blocker read degrades to the module's own decision when it throws, which made this line
    /// print <c>scope=Full wanted=Full</c> with no <c>DESYNC</c> — agreement — in the one state
    /// where the copy the game reads is entirely unknown. That is the disease this instrument was
    /// built to end, in the instrument's own fallback. It now prints <c>scope=UNREADABLE</c> and
    /// treats it as a <c>DESYNC</c>-class outcome: a reader grepping for <c>DESYNC</c> and not
    /// finding it may conclude the two copies agree, so not finding it has to mean they were both
    /// read.
    /// </para>
    /// </summary>
    public static string Describe()
    {
        bool readable = TryGetAppliedScope(out UiMaskScope applied);
        UiMaskScope wanted = WantedMaskScope;

        return "input capture count=" + Count
            + " exclusive=" + ExclusiveCount
            + " enabled=" + Enabled
            + " pointerBlock=" + PointerBlock
            + " scope=" + (readable ? applied.ToString() : "UNREADABLE")
            + " wanted=" + wanted
            + (!readable
                ? " DESYNC (the blocker's applied scope could not be read, so what the game is "
                  + "masking is unknown; this module decided " + wanted + " and this line is not "
                  + "evidence that the blocker agrees)"
                : applied == wanted
                    ? ""
                    : " DESYNC (the blocker is masking " + applied + " while this module decided "
                      + wanted + "; the mask on screen is the first one)")
            + " registered=" + IsRegistered
            + " pending=" + HasPendingChange;
    }

    /// <summary>What one pass decided to do to the game's list.</summary>
    private enum Act
    {
        /// <summary>Nothing to do: the mirror already matches, or the give-up latch is set.</summary>
        None,

        /// <summary>Hand the change to the game main thread; the caller is on another one.</summary>
        Post,

        Register,

        Remove,
    }

    /// <summary>What the game said.</summary>
    private enum Result
    {
        /// <summary>The deny flag was set; the mutator was not called at all.</summary>
        Deferred,

        Ok,

        Refused,
    }

    /// <summary>
    /// One decision, taken under <c>Gate</c> and carried out with no lock held. It carries the
    /// blocker it was decided against and the <c>wanted</c> it was decided from, which are the
    /// two things <see cref="FoldLocked"/> re-checks before it believes the result.
    /// </summary>
    private readonly struct Plan
    {
        public readonly Act Act;
        public readonly IUiInputBlocker Blocker;
        public readonly bool Wanted;

        public Plan(Act act, IUiInputBlocker blocker, bool wanted)
        {
            Act = act;
            Blocker = blocker;
            Wanted = wanted;
        }

        public static readonly Plan Nothing = new(Act.None, NullUiInputBlocker.Instance, false);
    }

    /// <summary>
    /// The result of one game call, plus what the blocker said about itself afterwards.
    ///
    /// <para>
    /// <see cref="MaskLive"/> rides along for one reason: <see cref="FoldLocked"/> writes the
    /// register log under <c>Gate</c>, and no member of <see cref="IUiInputBlocker"/> may be read
    /// there. Observing it in <see cref="Execute"/> — on the same pass, right after the call whose
    /// outcome the line reports — is what lets the line state a measured fact instead of a
    /// constant string (job FUI23, R44 finding 3).
    /// </para>
    /// </summary>
    private readonly struct Outcome
    {
        public readonly Result Result;
        public readonly bool Registered;
        public readonly bool MaskLive;

        public Outcome(Result result, bool registered, bool maskLive)
        {
            Result = result;
            Registered = registered;
            MaskLive = maskLive;
        }
    }

    /// <summary>
    /// Decide, act, fold — and chain if the decision was superseded mid-call. <b>Must not be
    /// called with <c>Gate</c> held</b>: everything it does to the game happens between the two
    /// locked sections.
    /// </summary>
    private static void Apply()
    {
        for (int pass = 0; pass < MaxChainedPasses; pass++)
        {
            Plan plan;
            lock (Gate)
            {
                // Someone above us on this thread (a blocker that re-entered Acquire from inside
                // Register) or on another thread is already in a game call. Let that pass finish;
                // its fold re-checks the decision and chains to the new one.
                if (_inFlight) return;

                plan = DecideLocked();
                if (plan.Act == Act.None) return;
                _inFlight = true;
            }

            Outcome? outcome = null;
            try
            {
                outcome = Execute(plan);
            }
            finally
            {
                lock (Gate) _inFlight = false;
            }

            if (outcome == null) return;

            bool chain;
            lock (Gate) chain = FoldLocked(plan, outcome.Value);
            if (!chain) return;
        }

        // The bound is reached in whatever state the last pass left, and that state can be
        // "registered, wanted by nobody" — the exact leak the fold-and-chain design exists to
        // prevent, reached by the bound that protects it. Stopping is right (the chain is not
        // making progress), giving up is not: arm the pump so the next frame reconciles wanted
        // against registered with one more ordinary pass. That costs one frame subscription and
        // makes the exhaustion self-correcting rather than latched (R33 finding 11).
        bool reconciling;
        bool bounded;
        int exhaustions;
        bool leaked;
        lock (Gate)
        {
            bool wanted = WantedLocked();
            // Nothing to reconcile, or the give-up latch already owns this state and says why —
            // re-arming there would spin the pump on a decision that is deliberately suppressed.
            reconciling = wanted != _registered && !(wanted && _gaveUp);

            // The re-arm is itself bounded now. Without this counter a blocker that churns on
            // every pass exhausts, re-arms, and exhausts again — forever, at frame rate
            // (R34 finding 15).
            _chainExhaustions++;
            exhaustions = _chainExhaustions;
            bounded = _chainExhaustions >= MaxChainExhaustions;
            if (bounded)
            {
                _chainGaveUp = true;
                _pending = false;
                reconciling = false;
                DisarmPumpLocked();
            }
            else if (reconciling)
            {
                _pending = true;
                ArmPumpLocked();
            }

            leaked = bounded && _registered && !WantedLocked();
        }

        Plugin.Logger?.LogWarning(
            "Ui: input context gave up chaining after " + MaxChainedPasses + " passes; the panel "
            + "count is being changed from inside IUiInputBlocker faster than the context can "
            + "follow it. "
            + (reconciling
                ? "The wanted and registered states do not match, so one reconciling pass is "
                  + "armed for the next frame. "
                : bounded
                    ? "That is exhaustion " + exhaustions + " of " + MaxChainExhaustions
                      + " in a row, so no further pass is armed and this module makes no more "
                      + "InputActionSystem calls until a panel is opened again. "
                      + (leaked
                          ? "The context is left REGISTERED with nobody wanting it: gameplay input "
                            + "stays captured until the blocker stops churning and a panel is "
                            + "opened and closed. Fix the IUiInputBlocker that mutates the panel "
                            + "count from inside Register. "
                          : "The wanted and registered states match, so nothing is left outstanding. ")
                    : "The wanted and registered states match, so nothing is left outstanding. ")
            + Describe());
    }

    /// <summary>
    /// The whole decision, from module state only. No <see cref="IUiInputBlocker"/> member is
    /// touched here — that is the point of the split.
    /// </summary>
    private static Plan DecideLocked()
    {
        bool wanted = WantedLocked();
        if (wanted == _registered)
        {
            _pending = false;
            _registerAttempts = 0;
            _gaveUp = false;
            _chainExhaustions = 0;
            _chainGaveUp = false;
            DisarmPumpLocked();
            return Plan.Nothing;
        }

        // The budget is spent and nothing external has happened since; do not spin.
        if (wanted && _gaveUp) return Plan.Nothing;

        // The chain budget is spent: a blocker is churning the refcount faster than the module can
        // follow, and every further pass is eight game calls that change nothing. Stop, in both
        // directions (R34 finding 15). Cleared by the next panel open.
        if (_chainGaveUp) return Plan.Nothing;

        // Thread first, flag second. Every other module routes world work through MainThread;
        // this one mutates a game-owned list and applies a native detour, so an off-thread
        // caller is posted, never executed here (R31 finding 23).
        if (!OnMainThread)
        {
            if (!_pending)
                Plugin.Logger?.LogInfo(PostedLog(wanted));
            _pending = true;
            if (_posted) return Plan.Nothing;
            _posted = true;
            return new Plan(Act.Post, _blocker, wanted);
        }

        return new Plan(wanted ? Act.Register : Act.Remove, _blocker, wanted);
    }

    /// <summary>
    /// Two terms, both required. <c>_enabled</c> is the global opt-out
    /// (<c>Ui.CaptureInput(false)</c>); <c>_count</c> is the shown-panel refcount, which is what
    /// makes a leak impossible.
    ///
    /// <para>
    /// <c>_pointerBlock</c> was the third term until job FUI20 and is deliberately not one now:
    /// the pointer decides <i>how much</i> a registered context masks
    /// (<see cref="MaskScope"/>), never <i>whether</i> one is registered. A panel's close key has
    /// to be taken from anywhere on the screen, and a context that is not in the list cannot take
    /// anything — see <see cref="InputMaskPlan"/>.
    /// </para>
    /// </summary>
    private static bool WantedLocked() => PanelsWantedLocked() || (_enabled && _closeKey > 0) || _exclusive > 0;

    /// <summary>P7b: shown panels want a context (the only term that can ask for <see cref="UiMaskScope.Full"/>; a close-key hold cannot).</summary>
    private static bool PanelsWantedLocked() => _enabled && _count > 0;

    /// <summary>
    /// The game half. Runs with <c>Gate</c> released and <see cref="CallGate"/> held, so the
    /// blocker may take as long as it likes, may pump, and may re-enter
    /// <see cref="Acquire"/> / <see cref="Release"/> without meeting a held lock.
    /// Returns <c>null</c> when there is nothing to fold (the post path).
    /// </summary>
    private static Outcome? Execute(Plan plan)
    {
        if (plan.Act == Act.Post)
        {
            try { Dev.MainThread.Post(ApplyFromMainThread); }
            catch (Exception e)
            {
                lock (Gate)
                {
                    _posted = false;
                    ArmPumpLocked();
                }

                Plugin.Logger?.LogWarning("Ui: input context main-thread post threw: " + e.Message);
            }

            return null;
        }

        lock (CallGate)
        {
            bool deny;
            try { deny = plan.Blocker.DenyModifications; }
            catch (Exception e)
            {
                Plugin.Logger?.LogWarning("Ui: input context deny-flag read threw: " + e.Message);
                deny = true;
            }

            if (deny)
                return new Outcome(
                    Result.Deferred,
                    ObserveRegistered(plan.Blocker, fallback: !plan.Wanted),
                    ObserveMaskLive(plan.Blocker));

            bool ok;
            if (plan.Act == Act.Register)
            {
                try { ok = plan.Blocker.Register(); }
                catch (Exception e)
                {
                    Plugin.Logger?.LogWarning("Ui: input context register threw: " + e.Message);
                    ok = false;
                }
            }
            else
            {
                try { ok = plan.Blocker.Remove(); }
                catch (Exception e)
                {
                    Plugin.Logger?.LogWarning("Ui: input context remove threw: " + e.Message);
                    ok = false;
                }
            }

            // Verify rather than assume: RotateCameraBlocker.Register sets its own _Registered
            // unconditionally, so the blocker's own view is the only honest answer (R31 finding 4).
            bool registered = ObserveRegistered(plan.Blocker, fallback: ok ? plan.Wanted : !plan.Wanted);
            return new Outcome(ok ? Result.Ok : Result.Refused, registered, ObserveMaskLive(plan.Blocker));
        }
    }

    private static bool ObserveRegistered(IUiInputBlocker blocker, bool fallback)
    {
        try { return blocker.IsRegistered; }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: input context IsRegistered threw: " + e.Message);
            return fallback;
        }
    }

    /// <summary>
    /// Whether the mask this module's log line describes is actually installed. Unreadable is
    /// <c>false</c>: the line then promises less than it might be delivering, which is the safe
    /// direction for a message whose failure mode is telling a player Escape is consumed when it
    /// is not (job FUI23, R44 finding 3).
    /// </summary>
    private static bool ObserveMaskLive(IUiInputBlocker blocker)
    {
        try { return blocker.MaskLive; }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: input mask liveness read threw: " + e.Message);
            return false;
        }
    }

    /// <summary>
    /// Write the result back under <c>Gate</c>. Returns true when the caller should run another
    /// pass, which happens exactly when the decision is no longer current: the panel count moved
    /// while the game call was in flight. That is the case that would otherwise leak a context —
    /// a register that landed for a panel already closed is answered here with a remove.
    /// </summary>
    private static bool FoldLocked(Plan plan, Outcome outcome)
    {
        // The blocker was swapped out from under us (Uninstall, or Ui.Install on a new
        // generation). The observation belongs to an object we no longer own; keep none of it.
        if (!ReferenceEquals(plan.Blocker, _blocker)) return false;

        // The observation itself is a fact about the world and is kept either way.
        _registered = outcome.Registered;

        bool wanted = WantedLocked();
        if (wanted != plan.Wanted)
        {
            // Superseded. No retry bookkeeping — the refusal (or success) answered a question
            // nobody is asking any more — and no give-up budget spent on it.
            Plugin.Logger?.LogInfo(SupersededLog(plan.Wanted));
            _pending = false;
            _registerAttempts = 0;
            _gaveUp = false;
            return true;
        }

        switch (outcome.Result)
        {
            case Result.Deferred:
                if (!_pending)
                    Plugin.Logger?.LogInfo(DeferredLog(plan.Wanted));
                _pending = true;
                ArmPumpLocked();
                return false;

            case Result.Ok:
                _pending = false;
                _registerAttempts = 0;
                _gaveUp = false;
                // A pass that landed what it decided is progress: the chain budget is about
                // consecutive exhaustions, so it refills here.
                _chainExhaustions = 0;
                _chainGaveUp = false;
                DisarmPumpLocked();
                Plugin.Logger?.LogInfo(plan.Wanted ? RegisteredLog(outcome.MaskLive) : RemovedLog);
                return false;

            default:
                return plan.Wanted ? FoldRefusedRegisterLocked() : FoldRefusedRemoveLocked();
        }
    }

    private static bool FoldRefusedRegisterLocked()
    {
        // Register() fails for exactly the transient conditions — no InputActionSystem yet
        // (loading screen, main menu) and no Client world — so one refusal is not an answer.
        // Retry on later frames, but bounded, so a server process does not retry forever
        // (R31 finding 22).
        _registerAttempts++;
        if (_registerAttempts < MaxRegisterAttempts)
        {
            if (_registerAttempts == 1)
                Plugin.Logger?.LogInfo(
                    "Ui: input context not registered order=" + Order
                    + "; the panel is shown but gameplay input is not captured yet. Retrying on the "
                    + "next frames (attempt 1 of " + MaxRegisterAttempts + ").");
            _pending = true;
            ArmPumpLocked();
            return false;
        }

        // Give up, but hand the budget back: the latch is what keeps this sticky, and it is
        // cleared by the next Acquire / Release / SetEnabled. A second panel shown while the
        // first is still up therefore gets five fresh attempts rather than one (R32 finding 13).
        _pending = false;
        _registerAttempts = 0;
        _gaveUp = true;
        DisarmPumpLocked();
        Plugin.Logger?.LogWarning(GaveUpLog);
        return false;
    }

    private static bool FoldRefusedRemoveLocked()
    {
        _pending = true;
        ArmPumpLocked();
        return false;
    }

    /// <summary>
    /// The teardown removal, with <see cref="CallGate"/> already held and <c>Gate</c> released.
    /// Unlike the ordinary path this one does not retry and does not defer: the Core generation
    /// is going away, so a refusal is reported here and now.
    /// </summary>
    private static void ForceRemove(IUiInputBlocker blocker)
    {
        bool ok;
        try { ok = blocker.Remove(); }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: input context remove threw: " + e.Message);
            ok = false;
        }

        if (ok)
        {
            Plugin.Logger?.LogInfo(RemovedLog);
            return;
        }

        // "The mask is per-frame, so it lapses" was only half the story: the mask lapses,
        // but _BlockRotateCamera is a persistent field on the leaked context object and
        // vanilla GetConsumedInputs blocks RotateCamera unconditionally while it is set.
        // IUiInputBlocker.Remove must have cleared it; say which of the two happened
        // instead of asserting the happy one (R31 finding 8).
        bool stillBlocking;
        try { stillBlocking = blocker.BlocksCameraRotation; }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: input context camera-flag read threw: " + e.Message);
            stillBlocking = true;
        }

        if (stillBlocking)
            Plugin.Logger?.LogError(
                "Ui: input context order=" + Order + " refused removal during teardown AND its "
                + "_BlockRotateCamera could not be cleared: the leaked list entry blocks camera "
                + "rotation for the rest of the process. Restart the client to clear it.");
        else
            Plugin.Logger?.LogWarning(
                "Ui: input context order=" + Order + " refused removal during teardown; the list "
                + "entry is leaked until the process exits, but it was made inert "
                + "(_BlockRotateCamera=false and the per-frame mask is gone), so it consumes no input.");
    }

    /// <summary>
    /// Something external changed the wanted state: clear the give-up latch so the next pass
    /// gets a full retry budget.
    /// </summary>
    private static void NudgeLocked(bool freshOpen = false)
    {
        _gaveUp = false;
        _registerAttempts = 0;
        if (!freshOpen) return;
        _chainExhaustions = 0;
        _chainGaveUp = false;
    }

    /// <summary>
    /// True when the caller is on the game main thread. Under tests there is no game loop to be
    /// on, so the check is a seam the off-thread fact drives; production reads the same
    /// <c>MainThread</c> facade every other module uses.
    /// </summary>
    private static bool OnMainThread =>
#if PROGENITOR_TESTS
        MainThreadCheckForTests();
#else
        Dev.MainThread.IsMainThread;
#endif

    private static void ApplyFromMainThread()
    {
        lock (Gate) _posted = false;
        Apply();
    }

    private static void ArmPumpLocked()
    {
        if (_pumpArmed) return;
        _pumpArmed = true;
#if !PROGENITOR_TESTS
        try { Plugin.Frame += Pump; }
        catch (Exception e)
        {
            _pumpArmed = false;
            Plugin.Logger?.LogWarning("Ui: input context pump subscribe threw: " + e.Message);
        }
#endif
    }

    private static void DisarmPumpLocked()
    {
        if (!_pumpArmed) return;
        _pumpArmed = false;
#if !PROGENITOR_TESTS
        try { Plugin.Frame -= Pump; }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: input context pump unsubscribe threw: " + e.Message); }
#endif
    }

#if PROGENITOR_TESTS
    /// <summary>
    /// Stands in for <c>Dev.MainThread.IsMainThread</c>, which has no meaning in a test process.
    /// Default true (the ordinary facts drive the module as if on the main thread); the
    /// off-thread fact points it at the real thread id it captured.
    /// </summary>
    internal static Func<bool> MainThreadCheckForTests = () => true;

    /// <summary>
    /// The pump subscription itself is a <c>Plugin.Frame</c> event a test process has no frames
    /// for, so <see cref="ArmPumpLocked"/> only flips the flag here. Exposing the flag is what
    /// lets a test assert that a next-frame pass is actually armed rather than only that
    /// <see cref="HasPendingChange"/> is set (R33's test-quality note on the pump no-ops).
    /// </summary>
    internal static bool PumpArmedForTests
    {
        get { lock (Gate) return _pumpArmed; }
    }

    /// <summary>How many chained passes one <c>Apply</c> may run; the bound R33 finding 11 is about.</summary>
    internal static int MaxChainedPassesForTests => MaxChainedPasses;

    /// <summary>Consecutive chain exhaustions allowed before the module stops (R34 finding 15).</summary>
    internal static int MaxChainExhaustionsForTests => MaxChainExhaustions;

    /// <summary>True once the chain budget is spent and no further game call will be made.</summary>
    internal static bool ChainGaveUpForTests
    {
        get { lock (Gate) return _chainGaveUp; }
    }

    /// <summary>
    /// Back to a fresh-process state. The pointer flag goes through the one writer here too, and
    /// the push is sent outside <c>Gate</c>: a harness that reset the field alone would be
    /// reproducing R44 finding 1 inside the tests written to catch it.
    /// </summary>
    internal static void ResetForTests()
    {
        ScopePush push;
        lock (Gate)
        {
            _blocker = NullUiInputBlocker.Instance;
            _count = 0;
            _exclusive = 0;
            _closeKey = 0;
            _enabled = true;
            push = WritePointerBlockLocked(true);
            _pending = false;
            _pumpArmed = false;
            _posted = false;
            _registerAttempts = 0;
            _registered = false;
            _inFlight = false;
            _gaveUp = false;
            _chainExhaustions = 0;
            _chainGaveUp = false;
            MainThreadCheckForTests = () => true;
        }

        push.Send();
    }
#endif
}
